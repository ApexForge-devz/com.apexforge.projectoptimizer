using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Caches the expensive part of analysis - the measurements taken off a loaded asset - between
	/// scans, in Library so it never reaches version control.
	/// <para>
	/// Only measurements are cached, never findings. Measurements depend solely on the asset, so
	/// they stay valid when you change a threshold or the target platform; rules are always
	/// re-evaluated from scratch over cached numbers, which costs nothing.
	/// </para>
	/// <para>
	/// Validity is keyed on <see cref="AssetDatabase.GetAssetDependencyHash"/>, which changes when
	/// either the asset or its importer settings change - so applying a fix automatically
	/// invalidates exactly that asset and nothing else.
	/// </para>
	/// </summary>
	public sealed class ScanCache
	{
		// One row per line: path, dependency hash, then the measurements as JSON.
		//
		// Stored as flat text rather than one JSON document because JsonUtility has to build the
		// entire file as a single string before writing it. With a row per asset that string runs to
		// hundreds of megabytes on a very large project, and the failure lands at the very end of a
		// scan - after all the work, with nothing saved, so the next scan starts cold again.
		// Streaming a line at a time has neither problem.
		static readonly string k_Path = ProjectOptimizerConstants.ScanCachePath;

		// Bumped whenever a cached measurement type gains or loses a field. JsonUtility fills a
		// missing field with its default rather than failing, so a stale row does not error - it
		// silently reads as "no material usage recorded", which is worse than a cache miss.
		const int k_Version = 7;
		const char k_Separator = '\u0009';
		const char k_ScopeSeparator = '\u001F';

		sealed class Row
		{
			public string path;
			public string hash;
			public string facts;
		}

		readonly Dictionary<string, Row> m_Rows = new Dictionary<string, Row>(
			StringComparer.Ordinal
		);
		// Most assets are analyzed by only one analyzer, but model files are intentionally
		// shared by ModelAnalyzer and AnimationAnalyzer. Keep the deserialized facts object
		// in memory so the second analyzer never reparses the same JSON row.
		readonly Dictionary<string, object> m_DecodedFacts = new Dictionary<string, object>(StringComparer.Ordinal);
		readonly Queue<string> m_DecodedFactOrder = new Queue<string>();
		const int k_MaxDecodedFacts = 8192;
		// One representative dependency hash per base asset path. ReferenceIndex asks for this
		// hash thousands of times; scanning all fact rows for a scoped key would turn that lookup
		// into an accidental O(n^2) operation on large projects.
		readonly Dictionary<string, string> m_StoredHashes = new Dictionary<string, string>(StringComparer.Ordinal);
		readonly Dictionary<string, HashSet<string>> m_KeysByAsset = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		readonly string m_Fingerprint;
		bool m_Dirty;

		static ScanCache s_SessionCache;
		static string s_SessionFingerprint;

		public int Hits { get; private set; }
		public int Misses { get; private set; }
		public int Count => m_Rows.Count;

		public string GetLiveHash(string assetPath) => HashOf(assetPath);

		public bool TryGetStoredHash(string assetPath, out string hash)
		{
			hash = null;
			if (string.IsNullOrEmpty(assetPath)) return false;
			if (m_Rows.TryGetValue(assetPath, out var direct) && !string.IsNullOrEmpty(direct.hash))
			{
				hash = direct.hash;
				return true;
			}
			return m_StoredHashes.TryGetValue(assetPath, out hash) && !string.IsNullOrEmpty(hash);
		}

		ScanCache(string fingerprint)
		{
			m_Fingerprint = fingerprint;
		}

		/// <summary>
		/// Only settings that change *what gets measured* belong here. Thresholds and severities
		/// deliberately do not, so tuning them keeps the cache warm.
		/// <para>
		/// The Unity version is included because a measurement is only as stable as the importer
		/// that produced it: an editor upgrade can change a texture's imported format or a mesh's
		/// vertex count without touching the asset, and the dependency hash those rows are keyed on
		/// would not move. The tool's own schema version is here for the same reason on the other
		/// side - a change to what a facts type records must not silently reuse rows written before
		/// it.
		/// </para>
		/// </summary>
		static string Fingerprint(OptimizerSettings settings)
		{
			return "v" + k_Version
				   + "|tool=" + OptimizerVersion.Schema
				   + "|unity=" + Application.unityVersion
				   + "|deep=" + (settings != null && settings.deepTextureScan ? 1 : 0);
		}

		public static ScanCache Load(OptimizerSettings settings)
		{
			var fingerprint = Fingerprint(settings);
			if (s_SessionCache != null && string.Equals(s_SessionFingerprint, fingerprint, StringComparison.Ordinal))
			{
				s_SessionCache.Hits = 0;
				s_SessionCache.Misses = 0;
				return s_SessionCache;
			}

			var cache = new ScanCache(fingerprint);
			try
			{
				if (File.Exists(k_Path))
				{
					using (var reader = new StreamReader(k_Path, System.Text.Encoding.UTF8, true, 1024 * 1024))
					{
						if (reader.ReadLine() == k_Version.ToString() && reader.ReadLine() == fingerprint)
						{
							string line;
							while ((line = reader.ReadLine()) != null)
							{
								int first = line.IndexOf(k_Separator);
								if (first <= 0) continue;
								int second = line.IndexOf(k_Separator, first + 1);
								if (second <= first) continue;
								string key = line.Substring(0, first);
								string hash = line.Substring(first + 1, second - first - 1);
								string facts = line.Substring(second + 1);
								if (key.Length == 0 || facts.Length == 0) continue;
								cache.m_Rows[key] = new Row { path = key, hash = hash, facts = facts };
								cache.RegisterKey(key, hash);
							}
						}
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read the scan cache, starting fresh: " + e.Message);
				cache.m_Rows.Clear();
				cache.m_StoredHashes.Clear();
				cache.m_KeysByAsset.Clear();
				Delete();
			}
			s_SessionCache = cache;
			s_SessionFingerprint = fingerprint;
			cache.Hits = 0;
			cache.Misses = 0;
			return cache;
		}

		public void Save()
		{
			if (!m_Dirty)
				return;
			try
			{
				if (!CacheFileUtility.EnsureCacheFolder())
					return;

				// Written beside the live file and swapped in. This runs at the very end of a scan,
				// which is exactly when a cancel or a crash is most likely, and a half-written cache
				// parses cleanly up to the cut - so it would be loaded and trusted, with every row
				// after the cut silently missing.
				var temporary = k_Path + ".tmp";
				using (var writer = new StreamWriter(temporary, false))
				{
					writer.WriteLine(k_Version);
					writer.WriteLine(m_Fingerprint);
					foreach (var pair in m_Rows)
					{
						var row = pair.Value;
						if (string.IsNullOrEmpty(row.path) || string.IsNullOrEmpty(row.facts))
							continue;
						writer.Write(row.path);
						writer.Write(k_Separator);
						writer.Write(row.hash);
						writer.Write(k_Separator);
						writer.WriteLine(row.facts);
					}
				}

				CacheFileUtility.ReplaceFile(temporary, k_Path);
				m_Dirty = false;
			}
			catch (Exception e)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " Could not write the scan cache: " + e.Message
				);
			}
		}

		public static void Delete()
		{
			try
			{
				if (File.Exists(k_Path))
					File.Delete(k_Path);
				if (File.Exists(k_Path + ".tmp"))
					File.Delete(k_Path + ".tmp");

				// Left behind by versions that stored the cache as one JSON document.
				var legacy = ProjectOptimizerConstants.LegacyScanCachePath;
				if (File.Exists(legacy))
					File.Delete(legacy);
			}
			catch
			{ /* best effort */
			}
		}

		public void Invalidate(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return;
			if (!m_KeysByAsset.TryGetValue(assetPath, out var keys) || keys == null || keys.Count == 0) return;
			var remove = new List<string>(keys);
			for (int i = 0; i < remove.Count; i++)
			{
				string key = remove[i];
				m_Rows.Remove(key);
				m_DecodedFacts.Remove(key);
			}
			m_KeysByAsset.Remove(assetPath);
			m_StoredHashes.Remove(assetPath);
			m_Dirty = true;
		}

		void RegisterKey(string key, string hash)
		{
			if (string.IsNullOrEmpty(key)) return;
			string basePath = BaseAssetPath(key);
			if (!m_KeysByAsset.TryGetValue(basePath, out var keys)) m_KeysByAsset[basePath] = keys = new HashSet<string>(StringComparer.Ordinal);
			keys.Add(key);
			if (!string.IsNullOrEmpty(hash)) m_StoredHashes[basePath] = hash;
		}

		void UnregisterKey(string key)
		{
			if (string.IsNullOrEmpty(key)) return;
			string basePath = BaseAssetPath(key);
			if (!m_KeysByAsset.TryGetValue(basePath, out var keys)) return;
			keys.Remove(key);
			if (keys.Count == 0)
			{
				m_KeysByAsset.Remove(basePath);
				m_StoredHashes.Remove(basePath);
			}
		}

		void RememberDecoded(string key, object value)
		{
			if (string.IsNullOrEmpty(key) || value == null) return;
			m_DecodedFacts[key] = value;
			m_DecodedFactOrder.Enqueue(key);
			while (m_DecodedFacts.Count > k_MaxDecodedFacts && m_DecodedFactOrder.Count > 0)
			{
				string oldest = m_DecodedFactOrder.Dequeue();
				m_DecodedFacts.Remove(oldest);
			}
		}

		static string BaseAssetPath(string key)
		{
			if (string.IsNullOrEmpty(key)) return key;
			int index = key.IndexOf(k_ScopeSeparator);
			return index < 0 ? key : key.Substring(0, index);
		}

		public static string ScopedKey(string assetPath, string scope)
		{
			return string.IsNullOrEmpty(scope) ? assetPath : assetPath + k_ScopeSeparator + scope;
		}

		internal static string BaseAssetPathForRuntime(string key) => BaseAssetPath(key);

		static string HashOf(string assetPath)
		{
			return AssetDatabase.GetAssetDependencyHash(assetPath).ToString();
		}

		/// <summary>
		/// Returns cached measurements for the asset, or runs <paramref name="measure"/> and stores
		/// the result. <typeparamref name="T"/> must be a JsonUtility-serializable class.
		/// </summary>
		public bool TryGet<T>(string assetPath, out T value) where T : class
		{
			string hash;
			try { hash = HashOf(assetPath); }
			catch { value = null; Misses++; return false; }
			return TryGet(assetPath, hash, out value);
		}

		public bool TryGet<T>(string assetPath, string dependencyHash, out T value) where T : class
		{
			value = null;
			if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(dependencyHash))
			{
				Misses++;
				return false;
			}

			if (!m_Rows.TryGetValue(assetPath, out var row)
				|| !string.Equals(row.hash, dependencyHash, StringComparison.Ordinal)
				|| string.IsNullOrEmpty(row.facts))
			{
				Misses++;
				return false;
			}

			if (m_DecodedFacts.TryGetValue(assetPath, out var decoded) && decoded is T typed)
			{
				Hits++;
				value = typed;
				return true;
			}

			try
			{
				value = JsonUtility.FromJson<T>(row.facts);
				if (value != null)
				{
					RememberDecoded(assetPath, value);
					Hits++;
					return true;
				}
			}
			catch { }

			Misses++;
			return false;
		}

		public void Set<T>(string assetPath, T value, string dependencyHash = null) where T : class
		{
			if (value == null || string.IsNullOrEmpty(assetPath)) return;
			string hash = dependencyHash;
			if (hash == null)
			{
				try { hash = HashOf(assetPath); }
				catch { return; }
			}
			try
			{
				if (m_Rows.ContainsKey(assetPath)) UnregisterKey(assetPath);
				m_Rows[assetPath] = new Row { path = assetPath, hash = hash, facts = JsonUtility.ToJson(value) };
				RegisterKey(assetPath, hash);
				RememberDecoded(assetPath, value);
				m_Dirty = true;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not cache measurements for '{assetPath}': {e.Message}");
			}
		}

		/// <summary>Stores a JSON-serializable fact produced by a background worker. Called only on the Unity main thread.</summary>
		public void SetUntyped(string cacheKey, object value, string dependencyHash)
		{
			if (value == null || string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(dependencyHash)) return;
			try
			{
				if (m_Rows.ContainsKey(cacheKey)) UnregisterKey(cacheKey);
				m_Rows[cacheKey] = new Row { path = cacheKey, hash = dependencyHash, facts = JsonUtility.ToJson(value) };
				RegisterKey(cacheKey, dependencyHash);
				RememberDecoded(cacheKey, value);
				m_Dirty = true;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not cache background measurements for '{cacheKey}': {e.Message}");
			}
		}

		public T Get<T>(string assetPath, Func<T> measure) where T : class => Get(assetPath, measure, null);

		public T Get<T>(string assetPath, Func<T> measure, string dependencyHash) where T : class
		{
			string hash = dependencyHash;
			if (hash == null)
			{
				try { hash = HashOf(assetPath); }
				catch { return measure(); }
			}

			if (TryGet(assetPath, hash, out T cached)) return cached;

			var measured = measure();
			if (measured == null) return null;
			Set(assetPath, measured, hash);
			return measured;
		}

		/// <summary>
		/// Forgets paths that no longer exist, so the file does not grow forever.
		/// <para>
		/// <paramref name="livePaths"/> must be every asset in the project, not the assets this scan
		/// looked at. Pruning against the scan scope deleted the cached measurements for everything
		/// outside it, so a single folder scan left the next full scan with nothing to reuse - the
		/// cache punished exactly the workflow it was built to support.
		/// </para>
		/// </summary>
		public void Prune(IReadOnlyCollection<string> livePaths)
		{
			if (livePaths == null || livePaths.Count == 0)
				return;
			var live = new HashSet<string>(livePaths, StringComparer.Ordinal);
			var dead = new List<string>();
			foreach (var key in m_Rows.Keys)
				if (!live.Contains(BaseAssetPath(key)))
					dead.Add(key);

			foreach (var key in dead)
			{
				m_Rows.Remove(key);
				m_DecodedFacts.Remove(key);
				UnregisterKey(key);
			}
			if (dead.Count > 0)
				m_Dirty = true;
		}
	}
}
