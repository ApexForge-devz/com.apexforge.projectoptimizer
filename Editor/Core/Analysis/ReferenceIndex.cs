using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Who points at what, across the project.
	/// <para>
	/// This is deliberately separate from "reachable from the build". An asset can be referenced by
	/// a material or prefab and still be unreachable, simply because nothing in the enabled build
	/// scenes leads to that prefab. Reporting the two as one thing is what makes an "unused" list
	/// full of assets that obviously are used - so the two questions are answered separately.
	/// </para>
	/// <para>
	/// It is also deliberately separate from the <em>scan</em> scope. Restricting a scan to one
	/// folder restricts what is analyzed and reported; it must not restrict what the tool knows
	/// about references, because a prefab three folders away is exactly the referrer that makes
	/// deleting a duplicate unsafe. <see cref="IsProjectWide"/> records which of the two an index
	/// actually is, and destructive fixes refuse to run against a partial one.
	/// </para>
	/// <para>
	/// Each asset's outgoing dependencies are stored with the dependency hash they were read at, so
	/// a rebuild only re-queries the assets whose hash moved. Editing one prefab in a project of
	/// 100,000 assets costs one dependency query, not 100,000.
	/// </para>
	/// </summary>
	public sealed class ReferenceIndex
	{
		// A flat text file rather than JSON: on a project with hundreds of thousands of assets this
		// index holds millions of strings, and JsonUtility has to materialise the whole document as
		// a single string before it can be written. That is where a big-project scan runs out of
		// memory right at the end, after all the work is already done. Streaming lines costs nothing
		// and cannot fail that way.

		/// <summary>
		/// Where a graph of this scope is cached. Two files, not one: a scoped pass knows about a
		/// fraction of the project, and writing that over the project-wide file threw away a warm
		/// graph that had cost minutes to build - so the next full scan started cold, every time
		/// anyone ran a folder scan in between. Keeping them apart means full, then scoped, then
		/// full reuses the full graph it already had.
		/// </summary>
		static string PathFor(bool projectWide)
		{
			return ProjectOptimizerConstants.ReferenceGraphPath(projectWide);
		}

		const int k_Version = 6;
		const char k_Separator = '\u0009';

		// Project-wide reference state is invalidated by the durable asset-change journal, not by the
		// coarse EditorApplication.project-change event. The journal gives us an exact generation that
		// can be replayed instead of treating every editor-side project transition as a graph rebuild.
		static ReferenceIndex s_SessionProjectIndex;
		static string s_SessionProjectUnity;
		static long s_SessionProjectJournalGeneration = -1;
		static string s_SessionProjectScopeFingerprint;

		/// <summary>One asset's outgoing edges, and the hash they were read at.</summary>
		sealed class Node
		{
			public string Hash;
			public string ValidationStamp;
			public string[] Dependencies;
		}

		/// <summary>Asset -> what it points at, with the dependency hash that produced it.</summary>
		Dictionary<string, Node> m_Nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
		long m_IndexedJournalGeneration = -1;
		string m_ScopeFingerprint;

		/// <summary>Referenced asset -> everything that points at it. Derived from <see cref="m_Nodes"/>.</summary>
		Dictionary<string, List<string>> m_Referrers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		public int Count => m_Referrers.Count;

		/// <summary>How many assets' outgoing edges this index holds.</summary>
		public int NodeCount => m_Nodes.Count;

		/// <summary>All graph node paths. The graph is immutable to readers after construction, so this can be enumerated from worker threads.</summary>
		public IReadOnlyCollection<string> NodePaths => m_Nodes.Keys;

		/// <summary>True when nothing had to be re-queried: every node came back from the cache.</summary>
		public bool FromCache { get; private set; }

		/// <summary>How many assets had to be re-queried on the last build.</summary>
		public int Rebuilt { get; private set; }

		/// <summary>
		/// True when this index was built over every statically referenceable asset in the project
		/// rather than over a restricted scan scope.
		/// <para>
		/// A fix that deletes an asset or rewrites a reference may only run against a project-wide
		/// index. With a partial one, "no referrers found" means "none in the folders you picked",
		/// and acting on that deletes assets that other folders were still using.
		/// </para>
		/// </summary>
		public bool IsProjectWide { get; private set; }

		/// <summary>
		/// True when every asset in scope answered its dependency query.
		/// <para>
		/// Separate from <see cref="IsProjectWide"/>, and the distinction matters: a query that
		/// throws used to be recorded as "this asset depends on nothing", which is
		/// indistinguishable from the truth by the time anything reads the graph. An index over the
		/// whole project that failed on one prefab is project-wide and not complete, and a fix that
		/// deletes an asset because nothing appears to reference it must not run against it.
		/// </para>
		/// </summary>
		public bool IsComplete => FailedQueryCount == 0 && FailedHashCount == 0;

		/// <summary>Assets whose dependency query threw, so their outgoing edges are unknown.</summary>
		public int FailedQueryCount { get; private set; }

		/// <summary>
		/// Assets whose dependency hash could not be read. Their edges were still queried, so the
		/// graph is right; only the incremental check is, which means they are re-queried every scan.
		/// </summary>
		public int FailedHashCount { get; private set; }

		/// <summary>Durable project-change generation represented by this graph.</summary>
		public long IndexedJournalGeneration => m_IndexedJournalGeneration;

		/// <summary>True when the project-wide graph is complete and represents the current AssetDatabase journal generation.</summary>
		public bool IsCurrentProjectWide => IsProjectWide
			&& IsComplete
			&& m_IndexedJournalGeneration >= 0
			&& m_IndexedJournalGeneration == AssetDatabaseThreadSafeUtility.JournalGeneration;

		/// <summary>Deterministic identity of the exact asset scope represented by this dependency graph.</summary>
		public string ScopeFingerprint => m_ScopeFingerprint;

		/// <summary>Compact diagnostics used by scan reports to distinguish cache reuse from incremental rebuilds.</summary>
		public string Diagnostics => "projectWide=" + IsProjectWide
			+ " nodes=" + NodeCount
			+ " referenced=" + Count
			+ " rebuilt=" + Rebuilt
			+ " fromCache=" + FromCache
			+ " indexedGeneration=" + m_IndexedJournalGeneration;

		/// <summary>The first few assets that failed, so the problem can be looked at rather than guessed at.</summary>
		public IReadOnlyList<string> FailedPaths => m_FailedPaths;

		readonly List<string> m_FailedPaths = new List<string>();

		/// <summary>How many failures to name before the list is just noise.</summary>
		const int k_MaxRememberedFailures = 20;

		/// <summary>One line describing what this index does and does not know. Empty when complete.</summary>
		public string IncompletenessSummary()
		{
			if (IsComplete) return string.Empty;

			var text = FailedQueryCount + " asset(s) could not be read for dependencies";
			if (m_FailedPaths.Count > 0)
			{
				text += ": " + string.Join(", ", m_FailedPaths.ToArray());
				if (FailedQueryCount > m_FailedPaths.Count)
					text += " and " + (FailedQueryCount - m_FailedPaths.Count) + " more";
			}
			return text + ".";
		}

		public bool IsReferenced(string assetPath) => m_Referrers.ContainsKey(assetPath);

		/// <summary>
		/// Everything that points at this asset. Knowing the referrers is what makes it possible to
		/// repoint them at a shared copy rather than only reporting the duplicate.
		/// </summary>
		public IReadOnlyList<string> ReferrersOf(string assetPath)
		{
			return m_Referrers.TryGetValue(assetPath, out var list) ? list : Array.Empty<string>();
		}

		/// <summary>What this asset points at, as of the last build.</summary>
		public IReadOnlyList<string> DependenciesOf(string assetPath)
		{
			return m_Nodes.TryGetValue(assetPath, out var node) ? node.Dependencies : Array.Empty<string>();
		}

		/// <summary>
		/// Builds the index without giving any time back. Use this only off the scan path - inside a
		/// scan use <see cref="BuildIncremental"/>, since a walk over a large project takes minutes
		/// and blocks every editor frame for its whole duration.
		/// </summary>
		public static ReferenceIndex Build(IReadOnlyList<string> referenceScopePaths, bool projectWide)
		{
			var steps = BuildIncremental(referenceScopePaths, projectWide);
			while (steps.MoveNext())
				if (steps.Current != null) return steps.Current;
			return new ReferenceIndex { IsProjectWide = projectWide };
		}

		/// <summary>
		/// Walks the project's dependencies a chunk at a time. Yields null while there is more to do
		/// and the finished index as its last value, so the caller can hand frames back to the editor
		/// in between.
		/// <para>
		/// <paramref name="referenceScopePaths"/> is the reference scope, not the scan scope: pass
		/// every statically referenceable asset whenever the result will be used for anything
		/// destructive, and set <paramref name="projectWide"/> accordingly.
		/// </para>
		/// </summary>
		public static IEnumerator<ReferenceIndex> BuildIncremental(
			IReadOnlyList<string> referenceScopePaths, bool projectWide, Action<float> onProgress = null, Func<string, string> dependencyHashProvider = null, Func<string, string> validationStampProvider = null)
		{
			var index = new ReferenceIndex { IsProjectWide = projectWide };
			string unityVersion = Application.unityVersion ?? string.Empty;
			long targetJournalGeneration = AssetDatabaseThreadSafeUtility.JournalGeneration;
			string scopeFingerprint = BuildScopeFingerprint(referenceScopePaths);
			string scopeBase = (projectWide ? "project" : "scoped") + "|tool=" + OptimizerVersion.Schema + "|unity=" + unityVersion;
			string exactScope = scopeBase + "|scope=" + scopeFingerprint;

			if (projectWide && s_SessionProjectIndex != null
				&& string.Equals(s_SessionProjectUnity, unityVersion, StringComparison.Ordinal)
				&& s_SessionProjectJournalGeneration == targetJournalGeneration)
			{
				if (string.Equals(s_SessionProjectScopeFingerprint, scopeFingerprint, StringComparison.Ordinal))
				{
					s_SessionProjectIndex.FromCache = true;
					s_SessionProjectIndex.Rebuilt = 0;
					onProgress?.Invoke(1f);
					yield return s_SessionProjectIndex;
					yield break;
				}

				int membershipRebuilt = ReconcileScopeMembership(s_SessionProjectIndex, referenceScopePaths, dependencyHashProvider, validationStampProvider);
				if (s_SessionProjectIndex.IsComplete)
				{
					s_SessionProjectIndex.Rebuilt = membershipRebuilt;
					s_SessionProjectIndex.FromCache = membershipRebuilt == 0;
					s_SessionProjectIndex.m_IndexedJournalGeneration = targetJournalGeneration;
					s_SessionProjectIndex.m_ScopeFingerprint = scopeFingerprint;
					s_SessionProjectScopeFingerprint = scopeFingerprint;
					onProgress?.Invoke(1f);
					yield return s_SessionProjectIndex;
					yield break;
				}
			}

			string expectedScope = projectWide ? null : exactScope;
			var stored = LoadNodes(PathFor(projectWide), expectedScope, out string storedScope, out long storedJournalGeneration);
			if (stored.Count > 0 && projectWide && (!IsCompatibleProjectScope(storedScope)
				|| storedJournalGeneration > targetJournalGeneration))
			{
				stored.Clear();
				storedScope = null;
				storedJournalGeneration = -1;
			}
			if (stored.Count == 0 && !projectWide)
			{
				stored = LoadNodes(PathFor(true), null, out storedScope, out storedJournalGeneration);
				if (!IsCompatibleProjectScope(storedScope))
				{
					stored.Clear(); storedScope = null; storedJournalGeneration = -1;
				}
			}

			var roots = new List<string>(referenceScopePaths != null ? referenceScopePaths.Count : 0);
			if (referenceScopePaths != null)
				for (int i = 0; i < referenceScopePaths.Count; i++)
				{
					string path = referenceScopePaths[i];
					if (!string.IsNullOrEmpty(path) && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) roots.Add(path);
				}
			var currentSet = new HashSet<string>(roots, StringComparer.Ordinal);

			bool journalReplayAvailable = false;
			var changeRecords = new List<AssetDatabaseThreadSafeUtility.AssetChangeRecord>();
			if (projectWide && stored.Count > 0 && storedJournalGeneration >= 0 && storedJournalGeneration <= targetJournalGeneration)
				journalReplayAvailable = AssetDatabaseThreadSafeUtility.TryGetJournalChangesAfter(storedJournalGeneration, changeRecords);

			var pool = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var pair in stored)
			{
				if (!currentSet.Contains(pair.Key)) continue;
				var node = pair.Value;
				if (node.Dependencies == null) node.Dependencies = Array.Empty<string>();
				for (int d = 0; d < node.Dependencies.Length; d++) node.Dependencies[d] = Intern(pool, node.Dependencies[d]);
				index.m_Nodes[pair.Key] = node;
			}
			index.BuildReverseMap();

			int rebuilt = 0;
			var changedPaths = new HashSet<string>(StringComparer.Ordinal);
			var movedOldPaths = new HashSet<string>(StringComparer.Ordinal);
			if (journalReplayAvailable)
			{
				foreach (var change in changeRecords)
				{
					for (int i = 0; i < change.Deleted.Length; i++)
					{
						string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Deleted[i]);
						if (!string.IsNullOrEmpty(path)) RemoveNode(index, path);
					}
					for (int i = 0; i < change.MovedFrom.Length; i++)
					{
						string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.MovedFrom[i]);
						if (!string.IsNullOrEmpty(path)) { movedOldPaths.Add(path); RemoveNode(index, path); }
					}
					for (int i = 0; i < change.Imported.Length; i++)
					{
						string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Imported[i]);
						if (!string.IsNullOrEmpty(path)) changedPaths.Add(path);
					}
					for (int i = 0; i < change.Moved.Length; i++)
					{
						string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Moved[i]);
						if (!string.IsNullOrEmpty(path)) changedPaths.Add(path);
					}
				}
			}

			// Determine moved-path referrers from the stored graph before the old nodes disappear.
			if (movedOldPaths.Count > 0)
			{
				var oldReferrers = new HashSet<string>(StringComparer.Ordinal);
				foreach (var oldPath in movedOldPaths)
					if (index.m_Referrers.TryGetValue(oldPath, out var refs))
						for (int i = 0; i < refs.Count; i++) oldReferrers.Add(refs[i]);
				foreach (var path in oldReferrers) changedPaths.Add(path);
			}

			// Reconcile membership from the canonical current scope. This catches additions/removals even if a
			// Unity lifecycle transition changed the returned path list without leaving a corresponding record.
			foreach (var path in currentSet)
				if (!index.m_Nodes.ContainsKey(path)) changedPaths.Add(path);
			var removals = new List<string>();
			foreach (var pair in index.m_Nodes)
				if (!currentSet.Contains(pair.Key)) removals.Add(pair.Key);
			for (int i = 0; i < removals.Count; i++) RemoveNode(index, removals[i]);

			int processed = 0;
			int totalChanges = Math.Max(1, changedPaths.Count);
			foreach (var path in changedPaths)
			{
				if (!currentSet.Contains(path)) continue;
				UpsertNode(index, path, BuildNode(index, path, dependencyHashProvider, validationStampProvider, pool));
				rebuilt++;
				if ((++processed & 63) == 0)
				{
					onProgress?.Invoke(Mathf.Clamp01(processed / (float)totalChanges) * 0.8f);
					yield return null;
				}
			}
			changedPaths.Clear();
			movedOldPaths.Clear();

			// If the journal gap was compacted away, dependency hashes provide a conservative second validation
			// layer. We do not perform GetDependencies on unchanged nodes; only hash mismatches are deep-read.
			if (projectWide && stored.Count > 0 && storedJournalGeneration >= 0
				&& storedJournalGeneration != targetJournalGeneration && !journalReplayAvailable)
			{
				for (int i = 0; i < roots.Count; i++)
				{
					string path = roots[i];
					if (!index.m_Nodes.TryGetValue(path, out var node)) continue;
					string liveHash = null;
					try { liveHash = dependencyHashProvider != null ? dependencyHashProvider(path) : AssetDatabase.GetAssetDependencyHash(path).ToString(); }
					catch { index.FailedHashCount++; }
					if (liveHash == null || !string.Equals(liveHash, node.Hash, StringComparison.Ordinal))
					{
						UpsertNode(index, path, BuildNode(index, path, dependencyHashProvider, validationStampProvider, pool));
						rebuilt++;
					}
				}
			}

			// Catch changes that arrived while this graph was being built. This prevents the next scan in the same
			// editor session from missing the session graph solely because Unity imported an asset mid-build.
			if (projectWide)
			{
				for (int attempt = 0; attempt < 4; attempt++)
				{
					long latestGeneration = AssetDatabaseThreadSafeUtility.JournalGeneration;
					if (latestGeneration <= targetJournalGeneration) break;
					var lateRecords = new List<AssetDatabaseThreadSafeUtility.AssetChangeRecord>();
					if (!AssetDatabaseThreadSafeUtility.TryGetJournalChangesAfter(targetJournalGeneration, lateRecords)) break;
					foreach (var change in lateRecords)
					{
						for (int i = 0; i < change.Deleted.Length; i++)
						{
							string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Deleted[i]);
							if (!string.IsNullOrEmpty(path)) RemoveNode(index, path);
						}
						var affected = new HashSet<string>(StringComparer.Ordinal);
						for (int i = 0; i < change.MovedFrom.Length; i++)
						{
							string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.MovedFrom[i]);
							if (string.IsNullOrEmpty(path)) continue;
							if (index.m_Referrers.TryGetValue(path, out var refs)) for (int r = 0; r < refs.Count; r++) affected.Add(refs[r]);
							RemoveNode(index, path);
						}
						for (int i = 0; i < change.Imported.Length; i++)
						{
							string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Imported[i]);
							if (!string.IsNullOrEmpty(path) && currentSet.Contains(path)) changedPaths.Add(path);
						}
						for (int i = 0; i < change.Moved.Length; i++)
						{
							string path = AssetDatabaseThreadSafeUtility.NormalizeAssetPath(change.Moved[i]);
							if (!string.IsNullOrEmpty(path) && currentSet.Contains(path)) changedPaths.Add(path);
						}
						foreach (var path in affected) if (currentSet.Contains(path)) changedPaths.Add(path);
					}
					foreach (var path in changedPaths)
					{
						if (!currentSet.Contains(path)) continue;
						UpsertNode(index, path, BuildNode(index, path, dependencyHashProvider, validationStampProvider, pool));
						rebuilt++;
					}
					changedPaths.Clear();
					targetJournalGeneration = latestGeneration;
				}
			}

			index.Rebuilt = rebuilt;
			index.FromCache = rebuilt == 0 && stored.Count > 0;
			index.m_IndexedJournalGeneration = targetJournalGeneration;
			index.m_ScopeFingerprint = scopeFingerprint;

			if (!index.IsComplete)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " The reference graph is incomplete and was not cached. " + index.IncompletenessSummary());
			}
			else if (rebuilt > 0 || stored.Count != index.m_Nodes.Count || storedJournalGeneration != targetJournalGeneration
				|| !string.Equals(storedScope, exactScope, StringComparison.Ordinal))
			{
				index.Save(PathFor(projectWide), exactScope, targetJournalGeneration);
			}

			long finalGeneration = AssetDatabaseThreadSafeUtility.JournalGeneration;
			if (projectWide && index.IsComplete && finalGeneration == targetJournalGeneration)
			{
				s_SessionProjectIndex = index;
				s_SessionProjectUnity = unityVersion;
				s_SessionProjectJournalGeneration = targetJournalGeneration;
				s_SessionProjectScopeFingerprint = scopeFingerprint;
			}

			onProgress?.Invoke(1f);
			yield return index;
		}

		static Node BuildNode(ReferenceIndex index, string path, Func<string, string> dependencyHashProvider, Func<string, string> validationStampProvider, Dictionary<string, string> pool)
		{
			string hash = null;
			string validationStamp = null;
			if (validationStampProvider != null)
			{
				try { validationStamp = validationStampProvider(path); } catch { validationStamp = null; }
			}
			try { hash = dependencyHashProvider != null ? dependencyHashProvider(path) : AssetDatabase.GetAssetDependencyHash(path).ToString(); }
			catch { index.FailedHashCount++; }
			return new Node { Hash = hash, ValidationStamp = validationStamp, Dependencies = index.QueryDependencies(path, pool) };
		}

		static int ReconcileScopeMembership(ReferenceIndex index, IReadOnlyList<string> referenceScopePaths, Func<string, string> dependencyHashProvider, Func<string, string> validationStampProvider)
		{
			var roots = new HashSet<string>(StringComparer.Ordinal);
			if (referenceScopePaths != null)
				for (int i = 0; i < referenceScopePaths.Count; i++)
				{
					string path = referenceScopePaths[i];
					if (!string.IsNullOrEmpty(path) && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) roots.Add(path);
				}
			var pool = new Dictionary<string, string>(StringComparer.Ordinal);
			var removals = new List<string>();
			foreach (var pair in index.m_Nodes) if (!roots.Contains(pair.Key)) removals.Add(pair.Key);
			for (int i = 0; i < removals.Count; i++) RemoveNode(index, removals[i]);
			int rebuilt = 0;
			foreach (var path in roots)
				if (!index.m_Nodes.ContainsKey(path)) { UpsertNode(index, path, BuildNode(index, path, dependencyHashProvider, validationStampProvider, pool)); rebuilt++; }
			return rebuilt;
		}

		static void RemoveNode(ReferenceIndex index, string path)
		{
			if (!index.m_Nodes.TryGetValue(path, out var node)) return;
			var dependencies = node.Dependencies ?? Array.Empty<string>();
			for (int i = 0; i < dependencies.Length; i++)
			{
				if (!index.m_Referrers.TryGetValue(dependencies[i], out var refs)) continue;
				for (int r = refs.Count - 1; r >= 0; r--)
					if (string.Equals(refs[r], path, StringComparison.Ordinal)) refs.RemoveAt(r);
				if (refs.Count == 0) index.m_Referrers.Remove(dependencies[i]);
			}
			index.m_Nodes.Remove(path);
		}

		static void UpsertNode(ReferenceIndex index, string path, Node node)
		{
			RemoveNode(index, path);
			index.m_Nodes[path] = node;
			var dependencies = node.Dependencies ?? Array.Empty<string>();
			for (int i = 0; i < dependencies.Length; i++)
			{
				if (!index.m_Referrers.TryGetValue(dependencies[i], out var refs)) index.m_Referrers[dependencies[i]] = refs = new List<string>(1);
				refs.Add(path);
			}
		}

		/// <summary>
		/// Asked one asset at a time on purpose. The batched overload returns a single union that
		/// still contains every root, so it cannot tell "A is pointed at by B" from "A merely appears
		/// in the query" - and dropping self-references is the whole point.
		/// </summary>
		string[] QueryDependencies(string path, Dictionary<string, string> pool)
		{
			try
			{
				var dependencies = AssetDatabase.GetDependencies(path, false);
				var result = new List<string>(dependencies.Length);
				for (int i = 0; i < dependencies.Length; i++)
				{
					if (dependencies[i] == path) continue;
					result.Add(Intern(pool, dependencies[i]));
				}
				return result.ToArray();
			}
			catch (Exception e)
			{
				// One unreadable asset must not cost the whole index - but it must not be mistaken
				// for an asset with no dependencies either. An empty array here and an empty array
				// from a successful query look identical to every reader, so the failure is counted
				// and the index reports itself as incomplete. Anything destructive then stands down.
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read dependencies of '" + path + "': " + e.Message);

				FailedQueryCount++;
				if (m_FailedPaths.Count < k_MaxRememberedFailures) m_FailedPaths.Add(path);
				return Array.Empty<string>();
			}
		}

		static string Intern(Dictionary<string, string> pool, string value)
		{
			if (value == null) return null;
			if (pool.TryGetValue(value, out var existing)) return existing;
			pool[value] = value;
			return value;
		}

		void BuildReverseMap()
		{
			m_Referrers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			foreach (var pair in m_Nodes)
			{
				var dependencies = pair.Value.Dependencies;
				for (int i = 0; i < dependencies.Length; i++)
				{
					if (!m_Referrers.TryGetValue(dependencies[i], out var list))
					{
						list = new List<string>();
						m_Referrers[dependencies[i]] = list;
					}
					list.Add(pair.Key);
				}
			}
		}

		/// <summary>
		/// Reads the stored nodes back. Line 1 is the version, line 2 the scope tag, and every line
		/// after that is one asset, its dependency hash, then its outgoing dependencies, tab
		/// separated. A file that cannot be read is simply not used.
		/// </summary>
		static Dictionary<string, Node> LoadNodes(string path, string expectedScope, out string scope, out long indexedJournalGeneration)
		{
			scope = null;
			indexedJournalGeneration = -1;
			var loaded = new Dictionary<string, Node>(StringComparer.Ordinal);
			try
			{
				if (!File.Exists(path))
					return loaded;

				using (var reader = new StreamReader(path))
				{
					if (reader.ReadLine() != k_Version.ToString()) return loaded;

					scope = reader.ReadLine();
					string generationLine = reader.ReadLine();
					if (!long.TryParse(generationLine, out indexedJournalGeneration) || indexedJournalGeneration < 0)
					{
						scope = null;
						indexedJournalGeneration = -1;
						return loaded;
					}

					if (expectedScope != null && !string.Equals(scope, expectedScope, StringComparison.Ordinal))
					{
						scope = null;
						indexedJournalGeneration = -1;
						return loaded;
					}

					string line;
					while ((line = reader.ReadLine()) != null)
					{
						if (line.Length == 0) continue;
						var fields = line.Split(k_Separator);
						if (fields.Length < 2) continue;

						string stamp = fields.Length > 2 ? fields[2] : string.Empty;
						int dependencyStart = fields.Length > 2 ? 3 : 2;
						var dependencies = new string[Math.Max(0, fields.Length - dependencyStart)];
						for (int i = dependencyStart; i < fields.Length; i++) dependencies[i - dependencyStart] = fields[i];
						loaded[fields[0]] = new Node
						{
							Hash = fields[1].Length == 0 ? null : fields[1],
							ValidationStamp = stamp.Length == 0 ? null : stamp,
							Dependencies = dependencies
						};
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read the reference index, rebuilding: " + e.Message);
				loaded.Clear();
				scope = null;
				indexedJournalGeneration = -1;
			}
			return loaded;
		}

		void Save(string path, string scope, long indexedJournalGeneration)
		{
			try
			{
				if (!CacheFileUtility.EnsureCacheFolder()) return;

				// Written to a temporary file and swapped in, so an interrupted write leaves the
				// previous index intact instead of a truncated one that has to be thrown away.
				var temporary = path + ".tmp";
				using (var writer = new StreamWriter(temporary, false))
				{
					writer.WriteLine(k_Version);
					writer.WriteLine(scope);
					writer.WriteLine(indexedJournalGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
					foreach (var pair in m_Nodes)
					{
						// A tab or newline in a path would corrupt the line-based format. Unity does
						// not allow either in an asset path, so this only guards against surprises.
						if (ContainsSeparator(pair.Key)) continue;

						writer.Write(pair.Key);
						writer.Write(k_Separator);
						writer.Write(pair.Value.Hash ?? string.Empty);
						writer.Write(k_Separator);
						writer.Write(pair.Value.ValidationStamp ?? string.Empty);

						var dependencies = pair.Value.Dependencies;
						for (int i = 0; i < dependencies.Length; i++)
						{
							if (ContainsSeparator(dependencies[i])) continue;
							writer.Write(k_Separator);
							writer.Write(dependencies[i]);
						}
						writer.WriteLine();
					}
				}

				CacheFileUtility.ReplaceFile(temporary, path);
			}
			catch (Exception e)
			{
				Debug.LogWarning(
					ProjectOptimizerConstants.LogPrefix + " Could not write the reference index: " + e.Message
				);
			}
		}

		static string BuildScopeFingerprint(IReadOnlyList<string> paths)
		{
			if (paths == null || paths.Count == 0) return "0";
			var sorted = new List<string>(paths.Count);
			for (int i = 0; i < paths.Count; i++)
			{
				string path = paths[i];
				if (string.IsNullOrEmpty(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
				sorted.Add(path);
			}
			sorted.Sort(StringComparer.Ordinal);
			using (var md5 = System.Security.Cryptography.MD5.Create())
			using (var stream = new MemoryStream())
			{
				using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), 4096, true))
				{
					for (int i = 0; i < sorted.Count; i++) { writer.Write(sorted[i]); writer.Write('\n'); }
					writer.Flush();
				}
				stream.Position = 0;
				return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
			}
		}

		static bool IsCompatibleProjectScope(string scope)
		{
			string prefix = "project|tool=" + OptimizerVersion.Schema + "|unity=" + Application.unityVersion + "|scope=";
			return !string.IsNullOrEmpty(scope) && scope.StartsWith(prefix, StringComparison.Ordinal);
		}

		static bool ContainsSeparator(string value)
		{
			return value.IndexOf(k_Separator) >= 0
				   || value.IndexOf('\n') >= 0
				   || value.IndexOf('\r') >= 0;
		}

		public static void Delete()
		{
			try
			{
				foreach (var path in new[] { PathFor(true), PathFor(false) })
				{
					if (File.Exists(path)) File.Delete(path);
					if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
				}

				// Left behind by versions that stored the index as JSON.
				var legacy = ProjectOptimizerConstants.LegacyReferenceGraphPath;
				if (File.Exists(legacy))
					File.Delete(legacy);
			}
			catch
			{ /* best effort */
			}
		}
	}
}
