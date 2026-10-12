using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Project-wide detection of byte-identical project-owned files.</summary>
	public sealed class DuplicateAnalyzer : IProjectAnalyzer
	{
		static readonly string[] k_IgnoredExtensions =
		{ ".cs", ".js", ".meta", ".asmdef", ".asmref", ".dll", ".rsp", ".uss", ".uxml" };
		public string DisplayName => "Duplicate assets";
		const float k_ReferencesShare = 0.55f;
		const float k_DuplicatesShare = 0.45f;
		float m_Progress = -1f;
		DuplicateHashCache m_DuplicateHashCache;
		public float Progress => m_Progress;

		void Phase(float start, float share, float within)
		{
			m_Progress = Mathf.Clamp01(start + share * Mathf.Clamp01(within));
		}

		static DuplicateAnalyzer()
		{
			Rules.Register(id: "DUP001", title: "Duplicate file content", category: AssetCategory.Duplicate,
							why: "Byte-identical project-owned source files contain the same bytes in multiple locations. This proves source duplication, but it does not by itself prove identical imported-object graphs or identical build inclusion.",
							fix: "The verified fix is offered only when the reference graph covers the whole project and the remapper can prove every one of the duplicate's serialized object IDs has a counterpart in the keeper and every static referrer is rewritable.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							// The duplicate relationship itself is proven - the two files hash identically, byte
							// for byte. Whether collapsing them is safe is a separate question, answered per
							// duplicate by DuplicateRemapper.Prepare, which refuses far more often than it agrees.
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Destructive,
							backupRequired: true,
							recommendationConfidence: OptimizationConfidence.Proven,
							requiredEvidence: EvidenceRequirement.CrossAssetFresh
							);
			Rules.Register(id: "DUP002", title: "Duplicate file requires manual handling", category: AssetCategory.Duplicate,
							why: "Byte-identical project-owned source files contain the same bytes in multiple locations, but this entry cannot be represented as a verified automatic collapse in the current scan result.",
							fix: "Review the duplicate relationship and collapse the copy manually when it is safe to do so.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
		}

		public bool Enabled(OptimizerSettings settings) => settings.scanDuplicates;

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			if (!Enabled(ctx.Settings)) yield break;
			m_Progress = 0f;
			if (m_DuplicateHashCache == null) m_DuplicateHashCache = DuplicateHashCache.Load();
			foreach (var step in EnsureReferences(ctx)) yield return step;
			foreach (var entry in FindDuplicates(ctx)) yield return entry;
			m_Progress = 1f;
		}

		/// <summary>
		/// Builds the reference index if this scan does not have one yet, yielding a frame back to
		/// the editor every so often. On a project with hundreds of thousands of assets the walk runs
		/// for minutes; doing it in one call is what makes the editor look hung with no progress.
		/// </summary>
		IEnumerable<AssetEntry> EnsureReferences(ScanContext ctx)
		{
			if (ctx.References != null)
			{
				Phase(0f, k_ReferencesShare, 1f);
				yield break;
			}

			// Built from the reference scope, never from the scan scope. A folder-restricted scan
			// still has to know about the prefab two folders over that holds the only reference to
			// the asset it is about to call an orphan.
			var referenceScope = ctx.ReferenceScopePaths;
			bool projectWide = referenceScope != null && referenceScope.Count > 0;
			if (!projectWide)
			{
				// No reference scope was resolved, which means this is not a full scan. Fall back to
				// what is available and mark the index partial, so the destructive fixes stand down.
				referenceScope = ctx.AllAssetPaths;
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " The project-wide reference scope is empty; "
								 + "duplicate collapsing will be reported but not offered as a fix.");
			}

			// The dependency walk is the longest single stretch of the whole scan, so its own
			// progress is what the bar follows here.
			var builder = ReferenceIndex.BuildIncremental(
				referenceScope, projectWide, done => Phase(0f, k_ReferencesShare, done), ctx.GetReferenceDependencyHash);
			while (builder.MoveNext())
			{
				if (builder.Current != null)
				{
					ctx.References = builder.Current;
					yield break;
				}
				yield return null;
			}
			ctx.References = ctx.References ?? new ReferenceIndex();
			if (!projectWide)
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Reference index is not project-wide; destructive duplicate/unused fixes remain disabled.");
		}

		IEnumerable<AssetEntry> FindDuplicates(ScanContext ctx)
		{
			var bySize = new Dictionary<long, List<string>>();
			foreach (var path in ctx.AllAssetPaths)
			{
				if (IsIgnored(path) || !IsProjectOwnedAsset(path)) continue;
				long size = (ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(path) : FileUtility.FileSizeOf(path));
				if (size <= 0) continue;
				if (!bySize.TryGetValue(size, out var list))
				{
					list = new List<string>();
					bySize[size] = list;
				}
				list.Add(path);
			}

			// Only files whose size collides can possibly be duplicates, so hashing is limited to
			// those. Hashing is pure file I/O and CPU with no Unity API involved, which makes it
			// the one part of a scan that is safe to run on worker threads.
			var candidates = new List<string>();
			foreach (var sizeGroup in bySize)
				if (sizeGroup.Value.Count >= 2) candidates.AddRange(sizeGroup.Value);

			var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
			var toHash = new List<string>();
			for (int i = 0; i < candidates.Count; i++)
			{
				string path = candidates[i];
				long size = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(path) : FileUtility.FileSizeOf(path);
				long stamp = ctx.FileMetadata != null ? ctx.FileMetadata.GetLastWriteTicks(path) : 0L;
				if (m_DuplicateHashCache.TryGet(path, size, stamp, out var cachedHash)) hashes[path] = cachedHash;
				else toHash.Add(path);
			}

			if (toHash.Count > 0)
			{
				var absolute = new string[toHash.Count];
				for (int i = 0; i < toHash.Count; i++) absolute[i] = AssetPathUtility.ToAbsolutePath(toHash[i]);

				// Files are already grouped by exact size. A cheap first/middle/last sample is enough
				// to eliminate almost all non-duplicates before the full-file MD5 pass. Exact duplicates
				// always survive this filter because equal files have equal samples.
				var quick = new string[toHash.Count];
				int quickCompleted = 0;
				Exception quickError = null;
				var quickWork = Task.Run(() =>
				{
					try
					{
						var options = new ParallelOptions { MaxDegreeOfParallelism = ScanPerformance.EffectiveWorkerCount(ctx.Settings) };
						Parallel.For(0, absolute.Length, options, i =>
						{
							quick[i] = FileUtility.QuickFileSignatureOfAbsolute(absolute[i]);
							System.Threading.Interlocked.Increment(ref quickCompleted);
						});
					}
					catch (Exception e) { quickError = e; }
				});
				while (!quickWork.IsCompleted)
				{
					Phase(k_ReferencesShare, k_DuplicatesShare * 0.08f,
						quickCompleted / (float)Mathf.Max(1, quick.Length));
					yield return null;
				}
				if (quickError != null) Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Quick duplicate prefilter failed: " + quickError.Message);

				var quickGroups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
				for (int i = 0; i < quick.Length; i++)
				{
					if (string.IsNullOrEmpty(quick[i])) continue;
					if (!quickGroups.TryGetValue(quick[i], out var group))
					{
						group = new List<int>();
						quickGroups[quick[i]] = group;
					}
					group.Add(i);
				}

				var exactIndices = new List<int>();
				foreach (var group in quickGroups.Values)
					if (group.Count > 1) exactIndices.AddRange(group);

				var results = new string[toHash.Count];
				int completed = 0;
				int workerCount = ScanPerformance.EffectiveWorkerCount(ctx.Settings);
				Exception hashError = null;
				var work = Task.Run(() =>
				{
					try
					{
						var options = new ParallelOptions { MaxDegreeOfParallelism = workerCount };
						Parallel.For(0, exactIndices.Count, options, j =>
						{
							int i = exactIndices[j];
							results[i] = FileUtility.Md5OfAbsolute(absolute[i]);
							System.Threading.Interlocked.Increment(ref completed);
						});
					}
					catch (Exception e) { hashError = e; }
				});

				while (!work.IsCompleted)
				{
					Phase(k_ReferencesShare + k_DuplicatesShare * 0.08f, k_DuplicatesShare * 0.72f,
						completed / (float)Mathf.Max(1, exactIndices.Count));
					yield return null;
				}
				if (hashError != null) Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Parallel hashing failed: " + hashError.Message);

				for (int i = 0; i < toHash.Count; i++)
				{
					string path = toHash[i];
					string hash = results[i];
					if (hash == null) continue;
					hashes[path] = hash;
					long size = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(path) : FileUtility.FileSizeOf(path);
					long stamp = ctx.FileMetadata != null ? ctx.FileMetadata.GetLastWriteTicks(path) : 0L;
					m_DuplicateHashCache.Set(path, size, stamp, hash);
				}
			}

			// The cache is append/replace in memory during the pass and is atomically written only once.
			m_DuplicateHashCache.Prune(ctx.AllAssetPaths);
			m_DuplicateHashCache.Flush();

			int groupsDone = 0;
			foreach (var sizeGroup in bySize)
			{
				groupsDone++;
				if (sizeGroup.Value.Count < 2) continue;
				Phase(k_ReferencesShare + k_DuplicatesShare * 0.8f, k_DuplicatesShare * 0.2f,
					groupsDone / (float)bySize.Count);

				var byHash = new Dictionary<string, List<string>>();
				foreach (var path in sizeGroup.Value)
				{
					if (!hashes.TryGetValue(path, out var hash) || hash == null) continue;
					if (!byHash.TryGetValue(hash, out var list))
					{
						list = new List<string>();
						byHash[hash] = list;
					}
					list.Add(path);
				}

				foreach (var hashGroup in byHash)
				{
					var paths = hashGroup.Value;
					if (paths.Count < 2) continue;
					paths.Sort(CompareKeeperPaths);
					var byteIdenticalPaths = new HashSet<string>(paths, StringComparer.Ordinal);

					// Every copy is listed so the whole group is visible. The first is nominated as
					// the keeper. Prefer a shallower Assets path rather than arbitrarily keeping an export copy.
					var keeper = paths[0];
					for (int i = 0; i < paths.Count; i++)
					{
						var path = paths[i];
						bool isKeeper = i == 0;

						var entry = NewEntry(path, AssetCategory.Duplicate, isKeeper ? "Keeper" : "Duplicate", ctx);
						entry.Set("copies", (long)paths.Count);
						entry.Set("original", keeper);
						entry.Set("role", isKeeper ? "Keeper" : "Duplicate");

						// Every copy other than this row's own. The keeper is left out as well on a
						// duplicate row, since it is already named on its own line just above - listing
						// it twice reads as if there were an extra copy.
						var others = new List<string>();
						for (int j = 0; j < paths.Count; j++)
						{
							if (j == i) continue;
							if (!isKeeper && paths[j] == keeper) continue;
							others.Add(paths[j]);
						}

						OptimizationIssue duplicateIssue = null;
						using (RuleTiming.Begin(isKeeper ? "DUP002" : "DUP001", entry.Path))
							if (isKeeper)
							{
								duplicateIssue = entry.AddIssue("DUP002", paths.Count + " identical copies", "1 copy", 0, null,
									"This is the copy being kept. The other copies can be collapsed onto it when the project-wide reference graph makes that operation safe.");
								duplicateIssue?.Items("Collapse these onto it", others, DetailKind.Bad, arePaths: true);
							}
							else
							{
								var references = ctx.References;

								// Every referrer is listed whether or not the fix is on offer. Seeing who
								// holds a copy is most of the value of the finding, and the referrer list
								// is what a person needs in order to do by hand what the tool declines to
								// do automatically.
								var referrers = new List<string>(references?.ReferrersOf(path)
																 ?? (IReadOnlyList<string>)Array.Empty<string>());

								// A partial reference graph cannot show that a copy is safe to delete: an
								// empty referrer list from a folder-scoped scan means "none in the folders
								// you picked", and one from a graph with failed queries means "none among
								// the assets that could be read". Neither is the claim this fix needs.
								DuplicateRemapper.Plan preparedPlan = null;
								if (references != null && references.IsCurrentProjectWide)
									preparedPlan = DuplicateRemapper.Prepare(path, keeper, references);
								bool canRewrite = preparedPlan != null && preparedPlan.Viable;

								var issueId = canRewrite ? "DUP001" : "DUP002";
								var issue = entry.AddIssue(issueId, paths.Count + " identical copies", "1 copy",
									canRewrite ? entry.FileSize : 0,
									canRewrite ? (Action<FixContext>)(c => CollapseOnto(c, keeper, references)) : null,
									canRewrite
										? "The duplicate file is byte-identical to the keeper and the project-wide reference graph is complete, so the source file can be collapsed safely. The saving is a conservative source-file byte lower bound and excludes its .meta file and build-side packing differences."
										: "The duplicate file is byte-identical to the keeper, but the reference graph is not complete enough to prove a safe delete. No saving estimate is reported.",
									canRewrite ? SavingsBasis.Disk : SavingsBasis.Unknown,
									verify: canRewrite ? (Func<FixContext, bool>)(c => !System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(c.AssetPath))
												&& AssetDatabase.LoadMainAssetAtPath(c.AssetPath) == null) : null);
								duplicateIssue = issue;

								if (issue != null && canRewrite)
					issue.WithEvidence(EvidenceRequirement.CrossAssetFresh, EvidenceState.Fresh, EvidenceContract.ComputeCrossAssetEvidenceSignature(references));

					issue?.Good("Keep this one", keeper, keeper)
									.Bad("Delete this one", entry.Path, entry.Path)
									.Items("Also a copy", others, DetailKind.Note, arePaths: true)
									.Items(referrers.Count == 1 ? "Referenced by" : "Referenced by",
										referrers, DetailKind.Note, arePaths: true);

								if (issue != null && referrers.Count == 0)
									issue.Note("Nothing in the project statically references this copy.");

								if (issue != null && !canRewrite)
								{
									string reason = preparedPlan != null && !string.IsNullOrEmpty(preparedPlan.Reason)
										? preparedPlan.Reason
										: references == null || !references.IsProjectWide
											? "The scan did not build a project-wide reference graph, so referrers outside its scope cannot be ruled out. Run the entire-project scan to enable collapsing."
											: !references.IsComplete
											? "The reference graph is incomplete, so some referrers may be unknown. " + references.IncompletenessSummary()
											: "The duplicate remapper could not prove a complete ID mapping and rewritable referrer set.";
									issue.Note("No automatic fix: " + reason);
								}
							}

						CorrelateByteIdenticalMaterialFinding(ctx, path, byteIdenticalPaths, duplicateIssue);
						entry.Finalise();
						yield return entry;
					}
				}
			}
		}

		/// <summary>
		/// Removes a MAT004 card only when every material in its equivalence group is proven to be
		/// in this exact byte-identical file group. The DUP finding then becomes the single card;
		/// its remapper preflight, full-project reference checks and backup rules remain authoritative.
		/// Render-equivalent files that differ byte-for-byte remain independent findings.
		/// </summary>
		static void CorrelateByteIdenticalMaterialFinding(ScanContext ctx, string assetPath,
			HashSet<string> byteIdenticalPaths, OptimizationIssue duplicateIssue)
		{
			if (ctx?.EntriesByPath == null || byteIdenticalPaths == null || duplicateIssue == null
				|| !string.Equals(Path.GetExtension(assetPath), ".mat", StringComparison.OrdinalIgnoreCase)) return;
			if (!ctx.EntriesByPath.TryGetValue(AssetCategory.Material + ":" + assetPath, out var materialEntry)
				|| materialEntry?.Issues == null) return;

			int materialIssueIndex = -1;
			OptimizationIssue materialIssue = null;
			for (int i = 0; i < materialEntry.Issues.Count; i++)
			{
				if (materialEntry.Issues[i]?.RuleId != "MAT004") continue;
				materialIssueIndex = i;
				materialIssue = materialEntry.Issues[i];
				break;
			}
			if (materialIssue == null) return;

			var materialGroupPaths = new HashSet<string>(StringComparer.Ordinal) { assetPath };
			for (int i = 0; i < materialIssue.Details.Count; i++)
			{
				var detail = materialIssue.Details[i];
				if (!string.IsNullOrEmpty(detail?.AssetPath)) materialGroupPaths.Add(detail.AssetPath);
			}
			if (materialGroupPaths.Count < 2) return;
			foreach (var materialPath in materialGroupPaths)
				if (!byteIdenticalPaths.Contains(materialPath)) return;

			duplicateIssue.Note("Material equivalence also matched for this byte-identical group. MAT004 compared rendered material state separately; the existing duplicate-remapper preflight, full-project reference checks and backup transaction remain required before collapse.", "Material equivalence");
			materialEntry.Issues.RemoveAt(materialIssueIndex);
			materialEntry.Finalise();
		}

		static bool IsIgnored(string path)
		{
			if (string.IsNullOrEmpty(path)) return true;
			if (path.EndsWith("/", StringComparison.Ordinal)) return true;

			for (int i = 0; i < k_IgnoredExtensions.Length; i++)
				if (path.EndsWith(k_IgnoredExtensions[i], StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		static bool IsProjectOwnedAsset(string path)
		{
			return !string.IsNullOrEmpty(path)
				&& path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// DUP001 fix entry point owned by DuplicateAnalyzer. The Review Fix UI invokes this same
		/// verified action after the user reviews and approves the transaction; UnusedAnalyzer does not
		/// participate in duplicate repair.
		/// Repoints every referrer at the keeper and deletes this copy. Refuses rather than guesses
		/// when the reference rewrite cannot be done safely.
		/// </summary>

		static void CollapseOnto(FixContext c, string keeperPath, ReferenceIndex references)
		{
			if (!c.BackupEnabled)
				throw new InvalidOperationException(
					"Collapsing a duplicate deletes an asset and rewrites other files, so it cannot run "
					+ "with backups switched off.");

			var plan = DuplicateRemapper.Prepare(c.AssetPath, keeperPath, references);
			if (!plan.Viable)
				throw new InvalidOperationException("Cannot collapse this duplicate safely. " + plan.Reason);

			// The remapper is handed the backup call that can fail, not one that only tries. Its
			// contract is that nothing is written or deleted until every file it is about to touch
			// has been snapshotted.
			var result = DuplicateRemapper.Apply(plan, c.TryBackupExtra);

			// Only a complete migration counts. Rewriting the referrers and then failing to delete
			// the duplicate, or failing to reimport what was rewritten, are both failures - and the
			// old code read a count of rewritten files as success and marked the asset deleted on
			// the strength of it.
			if (!result.Succeeded)
				throw new InvalidOperationException(result.Summary);

			c.DeletedAsset = result.DuplicateDeleted;
			Debug.Log(ProjectOptimizerConstants.LogPrefix + " " + c.AssetPath + ": " + result.Summary);
		}

		/// <summary>Chooses the keeper deterministically so duplicate fixes are reproducible. </summary>
		static int CompareKeeperPaths(string a, string b)
		{
			int packageA = a.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
			int packageB = b.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
			int cmp = packageA.CompareTo(packageB);
			if (cmp != 0) return cmp;
			cmp = a.Length.CompareTo(b.Length);
			if (cmp != 0) return cmp;
			return StringComparer.OrdinalIgnoreCase.Compare(a, b);
		}

		static AssetEntry NewEntry(string assetPath, AssetCategory category, string typeLabel, ScanContext ctx = null)
		{
			return new AssetEntry
			{
				Guid = AssetDatabase.AssetPathToGUID(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = category,
				TypeLabel = typeLabel,
				FileSize = ctx?.FileMetadata != null
					? ctx.FileMetadata.GetFileSize(assetPath)
					: FileUtility.FileSizeOf(assetPath)
			};
		}
	}
}
