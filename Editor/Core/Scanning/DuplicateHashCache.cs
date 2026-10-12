using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Persistent cache for byte-content hashes. A hash is reusable when the Unity asset path's
	/// on-disk size and UTC last-write timestamp are unchanged. This avoids re-reading large
	/// duplicate candidates after an editor restart or when scanning another platform profile.
	/// </summary>
	public sealed class DuplicateHashCache
	{
		sealed class Entry
		{
			public long Size;
			public long LastWriteTicks;
			public string Hash;
		}

		const int k_Version = 1;
		static readonly string k_Path = ProjectOptimizerConstants.DuplicateHashCachePath;
		readonly Dictionary<string, Entry> m_Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
		bool m_Dirty;

		public static DuplicateHashCache Load()
		{
			var cache = new DuplicateHashCache();
			try
			{
				if (!File.Exists(k_Path)) return cache;
				using (var reader = new StreamReader(k_Path))
				{
					if (reader.ReadLine() != k_Version.ToString(CultureInfo.InvariantCulture)) return cache;
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						var fields = line.Split('\t');
						if (fields.Length != 4) continue;
						if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) continue;
						if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)) continue;
						if (string.IsNullOrEmpty(fields[3])) continue;
						cache.m_Entries[fields[0]] = new Entry { Size = size, LastWriteTicks = ticks, Hash = fields[3] };
					}
				}
			}
			catch { cache.m_Entries.Clear(); }
			return cache;
		}

		public bool TryGet(string path, long size, long lastWriteTicks, out string hash)
		{
			hash = null;
			if (!m_Entries.TryGetValue(path, out var entry)) return false;
			if (entry.Size != size || entry.LastWriteTicks != lastWriteTicks || string.IsNullOrEmpty(entry.Hash)) return false;
			hash = entry.Hash;
			return true;
		}

		public void Set(string path, long size, long lastWriteTicks, string hash)
		{
			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash)) return;
			m_Entries[path] = new Entry { Size = size, LastWriteTicks = lastWriteTicks, Hash = hash };
			m_Dirty = true;
		}

		public void Prune(IReadOnlyCollection<string> livePaths)
		{
			if (livePaths == null || livePaths.Count == 0) return;
			var live = new HashSet<string>(livePaths, StringComparer.Ordinal);
			var dead = new List<string>();
			foreach (var path in m_Entries.Keys)
				if (!live.Contains(path)) dead.Add(path);
			for (int i = 0; i < dead.Count; i++) m_Entries.Remove(dead[i]);
			if (dead.Count > 0) m_Dirty = true;
		}

		public void Flush()
		{
			if (!m_Dirty) return;
			try
			{
				CacheFileUtility.EnsureCacheFolder();
				var temp = k_Path + ".tmp";
				using (var writer = new StreamWriter(temp, false))
				{
					writer.WriteLine(k_Version.ToString(CultureInfo.InvariantCulture));
					foreach (var pair in m_Entries)
					{
						var e = pair.Value;
						writer.Write(pair.Key); writer.Write('\t');
						writer.Write(e.Size.ToString(CultureInfo.InvariantCulture)); writer.Write('\t');
						writer.Write(e.LastWriteTicks.ToString(CultureInfo.InvariantCulture)); writer.Write('\t');
						writer.WriteLine(e.Hash);
					}
				}
				CacheFileUtility.ReplaceFile(temp, k_Path);
				m_Dirty = false;
			}
			catch { }
		}
	}
}
