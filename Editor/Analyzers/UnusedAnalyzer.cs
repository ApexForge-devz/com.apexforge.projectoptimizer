using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Project-wide detection of unused/unreachable assets and related project hygiene findings.</summary>
	public sealed class UnusedAnalyzer : IProjectAnalyzer
	{
		static readonly string[] k_IgnoredExtensions =
		{ ".cs", ".js", ".meta", ".asmdef", ".asmref", ".dll", ".rsp", ".uss", ".uxml" };
		public string DisplayName => "Unused assets";
		const float k_ReferencesShare = 0.30f;
		const float k_UnusedShare = 0.55f;
		static List<string> s_EmptyFoldersCache;
		static bool s_EmptyFoldersCacheReady;
		static readonly Dictionary<string, string> s_AssetTypeLabels = new Dictionary<string, string>(StringComparer.Ordinal);
		float m_Progress = -1f;
		public float Progress => m_Progress;

		void Phase(float start, float share, float within)
		{
			m_Progress = Mathf.Clamp01(start + share * Mathf.Clamp01(within));
		}

		static UnusedAnalyzer()
		{
			try { EditorApplication.projectChanged += InvalidateEmptyFoldersCache; } catch { }
			Rules.Register(id: "UNU001", title: "No static referrer anywhere in the project", category: AssetCategory.Unused,
							why: "No other asset points at this file, and nothing the static reachability analysis can follow from enabled build scenes, Resources, StreamingAssets, preloaded assets or explicit Addressables entries reaches it. Runtime-generated Addressables keys, externally built AssetBundles and computed Resources.Load paths remain outside static proof, so this is a cleanup candidate rather than proof the asset is dead.",
							fix: "Review Fix: delete the asset, offered only when the reference graph is complete enough for a project-wide deletion review. Confirm it is not loaded through runtime-generated Addressables keys, external AssetBundles, computed Resources.Load paths, reflection or other dynamic loaders before approving it - static analysis cannot rule those paths out.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Destructive,
							backupRequired: true
							);

			Rules.Register(id: "UNU002", title: "Asset lives in a Resources folder", category: AssetCategory.Unused,
							why: "Assets under a Resources folder are included in the player build regardless of whether a scene directly references them. This is a build-content and asset-management finding, not a statement about whether the asset is used: Resources is a runtime loading system, and code loading this by name is exactly what the analyzer cannot see.",
							fix: "Move the asset out of Resources when practical and reference it directly or through Addressables. Do not remove it merely because this rule fires.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							// Whether the asset is in a Resources folder is a fact; whether that is a problem for
							// this project is not something the analyzer can settle.
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "UNU003", title: "Empty folder", category: AssetCategory.Unused,
							why: "Empty folders create Project-window clutter and a .meta file without containing usable content.",
							fix: "Review/delete the folder. It contains no child content, but the path and folder metadata can still matter to editor or external tooling. This is project hygiene rather than a guaranteed runtime optimization.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							// Declared destructive even though there is nothing inside to lose. It deletes an
							// asset, and that is the property the fix engine gates backups on; a rule that
							// deletes should never be able to run with backups switched off, whatever it deletes.
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Destructive,
							backupRequired: true
							);

			Rules.Register(id: "UNU004", title: "Large asset with no static referrer", category: AssetCategory.Unused,
							why: "No static referrer was found for this large asset, making it a valuable cleanup candidate. Static analysis cannot prove that nothing loads it dynamically.",
							fix: "Review Fix: delete the asset, offered only when this scan built a complete, project-wide reference graph. Verify Resources, Addressables, AssetBundles, StreamingAssets and runtime loading paths before approving it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Destructive,
							backupRequired: true
							);

			Rules.Register(id: "UNU005", title: "Referenced, but nothing in the build reaches it", category: AssetCategory.Unused,
							why: "Other assets point at this file, so it is part of a live asset graph, but no path the AssetDatabase can follow leads to it from the build. This is information, not proof that the asset is unused.",
							fix: "Nothing to do unless you expected this content to ship. Do not delete it solely because this rule fires.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "UNU006", title: "Non-engine source file stored under Assets", category: AssetCategory.Unused,
							why: "Archives, documents and source-side artifacts generally do not contribute runtime content, but they may intentionally be kept under Assets for team workflows or custom importers. Automatic deletion would be unsafe, so this rule reports only.",
							fix: "Move reference material outside Assets when practical, or keep it if your tooling intentionally consumes it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
		}

		static void InvalidateEmptyFoldersCache()
		{
			s_EmptyFoldersCacheReady = false;
			s_EmptyFoldersCache = null;
			s_AssetTypeLabels.Clear();
		}

		public bool Enabled(OptimizerSettings settings) => settings.scanUnused;

		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			if (!Enabled(ctx.Settings)) yield break;
			m_Progress = 0f;
			foreach (var entry in FindUnused(ctx)) yield return entry;
			foreach (var entry in FindEmptyFolders(ctx)) yield return entry;
			foreach (var entry in FindStrayFiles(ctx)) yield return entry;
			m_Progress = 1f;
		}

		IEnumerable<AssetEntry> FindUnused(ScanContext ctx)
		{
			// The scope already computed this if the scan was restricted to what a build ships.
			var reachable = ctx.BuildSet ?? ScanScope.BuildReachableSet();
			ctx.ReachableAssets = reachable;
			var settings = ctx.Settings;

			// "Referenced by something" and "reachable from the build" are different questions, and
			// answering only the second is what makes an unused list full of obviously-used assets.
			foreach (var step in EnsureReferences(ctx)) yield return step;
			var references = ctx.References;
			bool graphIsComplete = references.IsComplete;

			if (!graphIsComplete)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " The unused-asset pass is running against an incomplete "
								 + "reference graph, so its findings are weaker than usual. "
								 + references.IncompletenessSummary());
			}
			yield return null;

			float unusedStart = k_ReferencesShare;
			for (int index = 0; index < ctx.AllAssetPaths.Count; index++)
			{
				var path = ctx.AllAssetPaths[index];
				if ((index & 255) == 0)
					Phase(unusedStart, k_UnusedShare, index / (float)Mathf.Max(1, ctx.AllAssetPaths.Count));

				if (IsIgnored(path) || !IsProjectOwnedAsset(path)) continue;
				if (AssetPathUtility.IsInEditorFolder(path)) continue;

				// StreamingAssets is loaded by path at runtime, so it has no static referrers by
				// construction. Reporting that absence would be reporting how the folder works.
				if (AssetPathUtility.IsInStreamingAssets(path)) continue;

				bool inResources = AssetPathUtility.IsInResources(path);
				bool inBuild = reachable.Contains(path);
				bool referenced = references.IsReferenced(path);
				if (inBuild && !inResources) continue;

				var entry = NewEntry(path, AssetCategory.Unused, AssetTypeLabel(path), ctx);
				entry.Set("reachable", inBuild ? "Yes" : "No");
				entry.Set("referenced", referenced ? "Yes" : "No");
				entry.Set("resources", inResources ? "Yes" : "No");

				using (RuleTiming.Begin("UNU002", entry.Path))
					if (inResources)
					{
						entry.AddIssue("UNU002", "In Resources", "Outside Resources / Addressables", 0, null,
							"Assets under Resources are included in the player build even without a normal static scene/reference path. This is a build-content finding: "
							+ "it says nothing about whether the asset is used, because code can load it by name.");
					}
					else if (!referenced)
					{
						// A full project scan with a complete reference graph can offer a real Review
						// Fix here: static analysis still cannot rule out dynamic loading, so it is never
						// Verified and never enters Fix All, but a human who has read the evidence below
						// can approve the delete through the same backup/rollback machinery as any other
						// destructive fix. An incomplete or scope-limited graph cannot make that claim -
						// an empty referrer list from a folder-scoped scan means "none in the folders you
						// picked", which is a different statement, so the fix stays off in that case.
						bool canDelete = graphIsComplete && references.IsProjectWide;
						string ruleId = entry.FileSize > settings.largeFileWarnKb * 1024L ? "UNU004" : "UNU001";

						using (RuleTiming.Begin(ruleId, entry.Path))
						{
							var orphan = entry.AddIssue(ruleId,
								DisplayUtility.FormatBytes(entry.FileSize) + ", no referrers",
								"Review Fix: delete the asset",
								canDelete ? entry.FileSize : 0,
								canDelete ? (Action<FixContext>)(c => DeleteOrphan(c)) : null,
								canDelete
									? "The project-wide reference graph is complete and the asset has no known referrers, so deletion can recover its current source-file bytes. This is a conservative disk lower bound; it excludes the .meta file, build packing, and dynamic loaders not visible to static analysis."
									: "The asset has no known referrers, but the reference graph is not complete enough to prove a safe delete. No saving estimate is reported.",
								canDelete ? SavingsBasis.Disk : SavingsBasis.Unknown,
								verify: canDelete
									? (Func<FixContext, bool>)(c => !System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(c.AssetPath))
																	&& AssetDatabase.LoadMainAssetAtPath(c.AssetPath) == null)
									: null);

							orphan?.Note("Dynamic loading cannot be fully ruled out by static analysis: computed Resources.Load paths, runtime-generated "
										 + "Addressables keys, remote catalogs, externally built AssetBundles and reflection-based loaders are "
										 + "outside the proof this scan can establish.", "Static analysis limit")
								  .Note("Build reachable: " + (inBuild ? "Yes" : "No") + "  ·  In Resources: "
										+ (inResources ? "Yes" : "No") + "  ·  Reference graph: "
										+ (references.IsProjectWide ? "project-wide" : "scoped to this scan")
										+ (graphIsComplete ? ", complete" : ", incomplete"), "Scope");

							if (orphan != null && !graphIsComplete)
							{
								orphan.Note("The reference graph could not be built completely, so a referrer may exist "
											+ "among the assets that failed to read. " + references.IncompletenessSummary(),
									"Incomplete analysis");
							}
							else if (orphan != null && !canDelete)
							{
								orphan.Note("No Review Fix: this scan's reference graph is scoped rather than "
											+ "project-wide, so referrers outside its scope cannot be ruled out. Run a "
											+ "full-project scan to enable deletion.", "Scan scope");
							}
						}
					}
					else
					{
						// Used by other assets, just not shipped by the current build settings.
						using (RuleTiming.Begin("UNU005", entry.Path))
							entry.AddIssue("UNU005", "Referenced, outside the build", "In a build scene, or removed", 0, null,
								"Deleting this on the strength of this rule alone would break whatever references it.");
					}

				entry.Finalise();
				yield return entry;
			}
		}

		static readonly string[] k_NonEngineExtensions =
		{
			".zip", ".rar", ".7z", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
			".ai", ".sketch", ".blend1", ".exe", ".msi", ".dmg"
		};

		IEnumerable<AssetEntry> FindStrayFiles(ScanContext ctx)
		{
			foreach (var path in ctx.AllAssetPaths)
			{
				if (!IsProjectOwnedAsset(path)) continue;
				var extension = System.IO.Path.GetExtension(path);
				bool stray = false;
				for (int i = 0; i < k_NonEngineExtensions.Length; i++)
				{
					if (string.Equals(extension, k_NonEngineExtensions[i], StringComparison.OrdinalIgnoreCase))
					{
						stray = true;
						break;
					}
				}
				if (!stray) continue;

				var entry = NewEntry(path, AssetCategory.Unused, "Not engine content", ctx);

				// Reported only. The rule's own text says automatic deletion would be unsafe, and it
				// is right: an extension is not evidence about what a file is for. A .zip under
				// Assets is as likely to be input to a custom importer or a build step as it is to be
				// someone's leftover download, and this analyzer cannot tell the two apart.
				using (RuleTiming.Begin("UNU006", entry.Path))
					entry.AddIssue("UNU006", extension + " under Assets", "Move outside Assets when it is genuinely spare",
						entry.FileSize, null,
						"Nothing here establishes that the file is unused - only that Unity has no importer for it.",
						SavingsBasis.Disk);
				entry.Finalise();
				yield return entry;
			}
		}

		// --- empty folders -----------------------------------------------------------

		IEnumerable<AssetEntry> FindEmptyFolders(ScanContext ctx)
		{
			// An empty folder ships nothing, so it is not a finding for a build-scoped scan.
			if (ctx.Settings.scopeMode == ScanScopeMode.ShippedInBuild) yield break;

			if (!s_EmptyFoldersCacheReady)
			{
				var discovered = new List<string>();
				string root = Application.dataPath;
				try
				{
					string[] directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
					for (int i = 0; i < directories.Length; i++)
					{
						bool empty;
						try { empty = Directory.GetFileSystemEntries(directories[i]).Length == 0; }
						catch { continue; }
						if (!empty) continue;

						var assetPath = "Assets" + directories[i].Substring(root.Length).Replace('\\', '/');
						if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
						if (assetPath.IndexOf("/.", StringComparison.Ordinal) >= 0) continue;
						discovered.Add(assetPath);
					}
				}
				catch
				{
					// Keep an empty cache on failure: a failed discovery must not block the rest of the scan.
				}

				s_EmptyFoldersCache = discovered;
				s_EmptyFoldersCacheReady = true;
			}

			for (int i = 0; i < s_EmptyFoldersCache.Count; i++)
			{
				string assetPath = s_EmptyFoldersCache[i];
				if (ctx.Settings.IsExcluded(assetPath)) continue;
				if (!InScopeFolder(ctx.Settings, assetPath)) continue;
				if (AssetPathUtility.IsInStreamingAssets(assetPath)) continue;

				var entry = NewEntry(assetPath, AssetCategory.Unused, "Folder");
				using (RuleTiming.Begin("UNU003", entry.Path))
					entry.AddIssue("UNU003", "Empty", "Deleted", 0,
						c =>
						{
							if (!AssetDatabase.MoveAssetToTrash(c.AssetPath))
								throw new InvalidOperationException("Unity refused to move the folder to the OS trash.");
							c.DeletedAsset = true;
						},
						verify: c => !AssetDatabase.IsValidFolder(c.AssetPath));
				entry.Finalise();
				yield return entry;
			}
		}

		// --- helpers ---------------------------------------------------------------

		/// <summary>
		/// Folders are walked off disk rather than through the scan's path list, so they need the
		/// include-list check applied by hand. Without it, scoping a scan to one folder would still
		/// report empty folders from the whole project.
		/// </summary>
		static bool InScopeFolder(OptimizerSettings settings, string assetPath)
		{
			if (settings.scopeMode != ScanScopeMode.SelectedFolders) return true;
			if (settings.includedFolders == null || settings.includedFolders.Count == 0) return true;

			for (int i = 0; i < settings.includedFolders.Count; i++)
				if (AssetPathUtility.IsUnder(assetPath, settings.includedFolders[i])) return true;
			return false;
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

		/// <summary>
		/// UNU001/UNU004's Review Fix. The asset itself is not among the extensions FixEngine
		/// snapshots automatically (that list is for the in-place-edited formats), so it is backed up
		/// explicitly here before the delete, the same way <see cref="DuplicateRemapper"/> backs up
		/// the duplicate it is about to remove.
		/// </summary>
		static void DeleteOrphan(FixContext c)
		{
			c.RequireBackup(c.AssetPath);
			if (!AssetDatabase.MoveAssetToTrash(c.AssetPath))
				throw new InvalidOperationException("Unity refused to move the asset to the OS trash.");
			c.DeletedAsset = true;
		}

		static string AssetTypeLabel(string path)
		{
			if (string.IsNullOrEmpty(path)) return string.Empty;
			if (s_AssetTypeLabels.TryGetValue(path, out var cached)) return cached;
			var type = AssetDatabase.GetMainAssetTypeAtPath(path);
			string label = type != null ? type.Name : System.IO.Path.GetExtension(path);
			s_AssetTypeLabels[path] = label;
			return label;
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
