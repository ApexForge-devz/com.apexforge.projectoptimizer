using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.RegularExpressions;
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
		static HashSet<string> s_DynamicLoadLiterals;
		static Dictionary<string, long> s_DynamicLoaderSourceTimestamps;
		static bool s_DynamicLoaderIndexReady;
		static bool s_DynamicLoaderIndexComplete;
		static int s_DynamicLoaderIndexGeneration;
		static readonly string[] k_DynamicLoaderMarkers =
		{
			"Resources.Load", "Addressables.LoadAsset", "Addressables.LoadResourceLocations",
			"AssetBundle.LoadAsset", "AssetBundle.LoadFrom", "LoadAssetAtPath",
			"Resources.LoadAsync", "SceneManager.LoadScene", "File.ReadAllBytes",
			"File.ReadAllText", "File.OpenRead", "LoadFromFile"
		};
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
							fix: "AutoFix only when the project-wide reference graph is complete and candidate-specific checks find no explicit Addressables/AssetBundle registration or recognizable dynamic-loader literal. The asset is backed up before deletion; any incomplete or suspicious case receives no automatic action.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
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
							fixKind: FixKind.None,
							// Empty-folder deletion is project hygiene, not a runtime/build optimization.
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "UNU004", title: "Large asset with no static referrer", category: AssetCategory.Unused,
							why: "No static referrer was found for this large asset, making it a valuable cleanup candidate. Static analysis cannot prove that nothing loads it dynamically.",
							fix: "AutoFix only when the project-wide reference graph is complete and candidate-specific checks find no explicit Addressables/AssetBundle registration or recognizable dynamic-loader literal. The asset is backed up before deletion; any incomplete or suspicious case receives no automatic action.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
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
			// Dynamic-loader literals are safety evidence for destructive orphan cleanup. Invalidate
			// them alongside the project cache so new/changed source files cannot leave stale proof.
			s_DynamicLoaderIndexReady = false;
			s_DynamicLoaderIndexComplete = false;
			s_DynamicLoaderIndexGeneration++;
			s_DynamicLoadLiterals = null;
			s_DynamicLoaderSourceTimestamps = null;
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
			float assetPassStart = 0f;
			if (references.IsCurrentProjectWide)
			{
				var loaderIndexBuilder = EnsureDynamicLoaderIndexIncremental(done => Phase(unusedStart, k_UnusedShare, 0.08f * done));
				while (loaderIndexBuilder.MoveNext()) yield return null;
				if (s_DynamicLoaderIndexReady && s_DynamicLoaderIndexComplete) assetPassStart = 0.08f;
			}
			for (int index = 0; index < ctx.AllAssetPaths.Count; index++)
			{
				var path = ctx.AllAssetPaths[index];
				if ((index & 255) == 0)
					Phase(unusedStart, k_UnusedShare, assetPassStart + (1f - assetPassStart) * index / (float)Mathf.Max(1, ctx.AllAssetPaths.Count));

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
						// Verified AutoFix is available only when evidence is complete and every
						// machine-detectable loading/bundling exception is excluded. Unknown evidence
						// deliberately produces a diagnostic with no executable action.
						bool graphCanSupportDelete = HasCurrentProjectWideReferenceEvidence(references);
						bool dynamicIndexReady = graphCanSupportDelete && s_DynamicLoaderIndexReady && s_DynamicLoaderIndexComplete;
						bool hasDynamicEvidence = dynamicIndexReady && HasCandidateDynamicLoaderLiteral(path, entry.Guid);
						bool addressableCheckComplete;
						bool addressable = IsExplicitlyAddressable(path, out addressableCheckComplete);
						bool assetBundleMember = IsAssetBundleMember(path);
						bool canAutoDelete = graphCanSupportDelete && dynamicIndexReady && addressableCheckComplete
							&& !hasDynamicEvidence && !addressable && !assetBundleMember
							&& !string.IsNullOrEmpty(entry.Guid) && File.Exists(AssetPathUtility.ToAbsolutePath(path));
						string ruleId = entry.FileSize > settings.largeFileWarnKb * 1024L ? "UNU004" : "UNU001";
						string expectedGuid = entry.Guid;
						long expectedSize = FileUtility.FileSizeOf(path);
						long expectedWriteTicks;
						try { expectedWriteTicks = File.GetLastWriteTimeUtc(AssetPathUtility.ToAbsolutePath(path)).Ticks; }
						catch { expectedWriteTicks = -1; canAutoDelete = false; }

						using (RuleTiming.Begin(ruleId, entry.Path))
						{
							var orphan = entry.AddIssue(ruleId,
								DisplayUtility.FormatBytes(entry.FileSize) + ", no referrers",
								canAutoDelete ? "Delete verified orphan" : "No automatic action: orphan proof is incomplete or a loading exception was detected",
								canAutoDelete ? entry.FileSize : 0,
								canAutoDelete ? (Action<FixContext>)(c => DeleteOrphan(c, expectedGuid, expectedSize, expectedWriteTicks, references)) : null,
								canAutoDelete
									? "The project-wide reference graph is complete and current; the AssetDatabase journal generation is checked again at apply time; no explicit Addressables or AssetBundle registration was found; no candidate-matching literal was found in source files containing recognized dynamic-loader calls; and the source file is backed up before deletion. External catalogs, generated keys and non-source loaders remain outside static proof."
									: BuildOrphanBlockReason(graphCanSupportDelete, dynamicIndexReady, hasDynamicEvidence, addressableCheckComplete, addressable, assetBundleMember),
								canAutoDelete ? SavingsBasis.Disk : SavingsBasis.Unknown,
								verify: canAutoDelete
									? (Func<FixContext, bool>)(c => !System.IO.File.Exists(AssetPathUtility.ToAbsolutePath(c.AssetPath))
													&& AssetDatabase.LoadMainAssetAtPath(c.AssetPath) == null)
									: null);

							orphan?.Note("Static analysis cannot rule out every computed or externally supplied loading key. Automatic deletion is suppressed when the graph/index is incomplete, a recognized candidate-specific loader literal exists, the asset is explicitly Addressable, or it is assigned to an AssetBundle.", "AutoFix safety gates")
							  .Note("Build reachable: " + (inBuild ? "Yes" : "No") + "  ·  In Resources: "
									+ (inResources ? "Yes" : "No") + "  ·  Reference graph: "
									+ (references.IsProjectWide ? "project-wide" : "scoped to this scan")
									+ (graphIsComplete ? ", complete" : ", incomplete"), "Scope");

							if (orphan != null && !graphIsComplete)
							{
								orphan.Note("The reference graph could not be built completely, so a referrer may exist among assets that failed to read. " + references.IncompletenessSummary(), "Incomplete analysis");
							}
							else if (orphan != null && !references.IsProjectWide)
							{
								orphan.Note("The scan reference graph is scoped rather than project-wide, so referrers outside its scope cannot be ruled out. Run a full-project scan to enable AutoFix.", "Scan scope");
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
					entry.AddIssue("UNU003", "Empty", "No runtime/build optimization action", 0, null,
						"This is organizational housekeeping only; it is intentionally diagnostic-only under the approved FixKind policy.");
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
		/// UNU001/UNU004's guarded AutoFix. The asset itself is not among the extensions FixEngine
		/// snapshots automatically (that list is for the in-place-edited formats), so it is backed up
		/// explicitly here before the delete, the same way <see cref="DuplicateRemapper"/> backs up
		/// the duplicate it is about to remove.
		/// </summary>
		static void DeleteOrphan(FixContext c, string expectedGuid, long expectedSize, long expectedWriteTicks, ReferenceIndex expectedReferences)
		{
			string path = c.AssetPath;
			string absolutePath = AssetPathUtility.ToAbsolutePath(path);
			if (!HasCurrentProjectWideReferenceEvidence(expectedReferences))
				throw new InvalidOperationException("Project reference evidence is stale because the AssetDatabase changed after analysis. Rescan before deleting the orphan.");
			if (!IsProjectOwnedAsset(path) || !File.Exists(absolutePath))
				throw new InvalidOperationException("The orphan target is no longer a project-owned file.");
			if (!string.Equals(AssetDatabase.AssetPathToGUID(path), expectedGuid, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The orphan asset identity changed after analysis. Rescan before applying the fix.");
			var info = new FileInfo(absolutePath);
			if (expectedWriteTicks < 0 || info.Length != expectedSize || info.LastWriteTimeUtc.Ticks != expectedWriteTicks)
				throw new InvalidOperationException("The orphan asset changed after analysis. Rescan before applying the fix.");
			if (!DynamicLoaderSourceSetUnchanged())
				throw new InvalidOperationException("A source file changed after dynamic-loading analysis. Rescan before applying the fix.");
			if (HasCandidateDynamicLoaderLiteral(path, expectedGuid))
				throw new InvalidOperationException("A dynamic-loader literal now matches this asset. Automatic deletion was blocked.");
			bool addressableCheckComplete;
			if (IsExplicitlyAddressable(path, out addressableCheckComplete) || !addressableCheckComplete || IsAssetBundleMember(path))
				throw new InvalidOperationException("The asset is explicitly Addressable, assigned to an AssetBundle, or its loading registration could not be checked. Automatic deletion was blocked.");
			c.RequireBackup(path);
			if (!AssetDatabase.MoveAssetToTrash(path))
				throw new InvalidOperationException("Unity refused to move the asset to the OS trash.");
			c.DeletedAsset = true;
		}

		static bool HasCurrentProjectWideReferenceEvidence(ReferenceIndex references)
		{
			try { return references != null && references.IsCurrentProjectWide; }
			catch { return false; }
		}

		static string BuildOrphanBlockReason(bool graphCanSupportDelete, bool dynamicIndexReady, bool hasDynamicEvidence, bool addressableCheckComplete, bool addressable, bool assetBundleMember)
		{
			if (!graphCanSupportDelete) return "The reference graph is incomplete, scoped, or stale rather than a current project-wide index; deletion is blocked.";
			if (!dynamicIndexReady) return "The dynamic-loader source index could not be built completely; deletion is blocked.";
			if (hasDynamicEvidence) return "A source literal matches the candidate in code using a recognized dynamic loader; deletion is blocked.";
			if (!addressableCheckComplete) return "Addressables registration could not be checked reliably; deletion is blocked.";
			if (addressable) return "The asset has an explicit Addressables entry; deletion is blocked.";
			if (assetBundleMember) return "The asset is assigned to an AssetBundle; deletion is blocked.";
			return "The candidate failed one or more required preflight checks; no action is offered.";
		}

		static System.Collections.Generic.IEnumerator<bool> EnsureDynamicLoaderIndexIncremental(Action<float> onProgress)
		{
			if (s_DynamicLoaderIndexReady) yield break;
			int generation = s_DynamicLoaderIndexGeneration;
			s_DynamicLoaderIndexComplete = true;
			s_DynamicLoadLiterals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			s_DynamicLoaderSourceTimestamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			string[] sources;
			try { sources = EnumeratePotentialSourceFiles().ToArray(); }
			catch { s_DynamicLoaderIndexComplete = false; sources = Array.Empty<string>(); }
			for (int i = 0; i < sources.Length; i++)
			{
				if (generation != s_DynamicLoaderIndexGeneration) yield break;
				string source = sources[i];
				try
				{
					var info = new FileInfo(source);
					s_DynamicLoaderSourceTimestamps[source] = info.LastWriteTimeUtc.Ticks;
					string content = File.ReadAllText(source);
					if (ContainsDynamicLoaderMarker(content))
					{
						foreach (Match match in Regex.Matches(content, "\"((?:\\\\.|[^\"\\\\])*)\"|'((?:\\\\.|[^'\\\\])*)'"))
						{
							string literal = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
							literal = Regex.Unescape(literal).Trim();
							if (literal.Length > 0) s_DynamicLoadLiterals.Add(NormalizeLoaderToken(literal));
						}
					}
				}
				catch { s_DynamicLoaderIndexComplete = false; }
				if ((i & 31) == 31 || i == sources.Length - 1)
				{
					onProgress?.Invoke(sources.Length == 0 ? 1f : (i + 1) / (float)sources.Length);
					yield return false;
				}
			}
			if (generation != s_DynamicLoaderIndexGeneration) yield break;
			s_DynamicLoaderIndexReady = s_DynamicLoaderIndexComplete;
			if (!s_DynamicLoaderIndexComplete)
			{
				s_DynamicLoadLiterals = null;
				s_DynamicLoaderSourceTimestamps = null;
			}
			onProgress?.Invoke(1f);
		}

		static IEnumerable<string> EnumeratePotentialSourceFiles()
		{
			var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
			string[] roots =
			{
				Application.dataPath,
				Path.Combine(projectRoot ?? string.Empty, "Packages"),
				Path.Combine(projectRoot ?? string.Empty, "Library", "PackageCache")
			};
			for (int i = 0; i < roots.Length; i++)
			{
				if (string.IsNullOrEmpty(roots[i]) || !Directory.Exists(roots[i])) continue;
				string[] found;
				try { found = Directory.GetFiles(roots[i], "*.cs", SearchOption.AllDirectories); }
				catch { s_DynamicLoaderIndexComplete = false; continue; }
				for (int n = 0; n < found.Length; n++) files.Add(found[n]);
			}
			return files;
		}

		static bool ContainsDynamicLoaderMarker(string content)
		{
			for (int i = 0; i < k_DynamicLoaderMarkers.Length; i++)
				if (content.IndexOf(k_DynamicLoaderMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		static string NormalizeLoaderToken(string value)
		{
			if (string.IsNullOrEmpty(value)) return string.Empty;
			return value.Trim().Replace('\\', '/').TrimStart('/').Replace("Assets/", string.Empty).TrimEnd('/');
		}

		static bool HasCandidateDynamicLoaderLiteral(string assetPath, string guid)
		{
			if (!s_DynamicLoaderIndexReady || !s_DynamicLoaderIndexComplete || s_DynamicLoadLiterals == null) return true;
			var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string normalized = NormalizeLoaderToken(assetPath);
			tokens.Add(normalized);
			tokens.Add(NormalizeLoaderToken(Path.ChangeExtension(normalized, null)));
			string fileName = Path.GetFileName(assetPath);
			tokens.Add(NormalizeLoaderToken(fileName));
			tokens.Add(NormalizeLoaderToken(Path.GetFileNameWithoutExtension(assetPath)));
			if (!string.IsNullOrEmpty(guid)) tokens.Add(NormalizeLoaderToken(guid));
			if (AssetPathUtility.IsInResources(assetPath))
			{
				string resourcesRelative = assetPath.Substring(0, assetPath.LastIndexOf("Resources/", StringComparison.OrdinalIgnoreCase) + "Resources/".Length);
				string relative = assetPath.Substring(resourcesRelative.Length);
				tokens.Add(NormalizeLoaderToken(Path.ChangeExtension(relative, null)));
			}
			foreach (string literal in s_DynamicLoadLiterals)
			{
				if (tokens.Contains(literal)) return true;
				foreach (string token in tokens)
					if (token.Length >= 6 && literal.Length >= 6 && (literal.EndsWith("/" + token, StringComparison.OrdinalIgnoreCase) || token.EndsWith("/" + literal, StringComparison.OrdinalIgnoreCase))) return true;
			}
			return false;
		}

		static bool DynamicLoaderSourceSetUnchanged()
		{
			if (!s_DynamicLoaderIndexReady || !s_DynamicLoaderIndexComplete || s_DynamicLoaderSourceTimestamps == null) return false;
			var current = new SortedSet<string>(EnumeratePotentialSourceFiles(), StringComparer.OrdinalIgnoreCase);
			if (current.Count != s_DynamicLoaderSourceTimestamps.Count) return false;
			foreach (string source in current)
			{
				if (!s_DynamicLoaderSourceTimestamps.TryGetValue(source, out long ticks)) return false;
				try { if (File.GetLastWriteTimeUtc(source).Ticks != ticks) return false; }
				catch { return false; }
			}
			return true;
		}

		static bool IsAssetBundleMember(string path)
		{
			try
			{
				var importer = AssetImporter.GetAtPath(path);
				return importer == null || !string.IsNullOrEmpty(importer.assetBundleName);
			}
			catch { return true; }
		}

		static bool IsExplicitlyAddressable(string path, out bool complete)
		{
			complete = true;
			try
			{
				Type type = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
				if (type == null) { complete = !AddressablesUtility.IsInstalled; return false; }
				var settingsProperty = type.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
				object settings = settingsProperty != null ? settingsProperty.GetValue(null, null) : null;
				if (settings == null) { complete = !AddressablesUtility.IsInstalled; return false; }
				object groupsObject = GetMemberValue(settings, "groups");
				var groups = groupsObject as System.Collections.IEnumerable;
				if (groups == null) { complete = !AddressablesUtility.IsInstalled; return false; }
				string targetGuid = AssetDatabase.AssetPathToGUID(path);
				foreach (object group in groups)
				{
					object entriesObject = GetMemberValue(group, "entries");
					var entries = entriesObject as System.Collections.IEnumerable;
					if (entries == null) continue;
					foreach (object entry in entries)
					{
						string guid = GetMemberValue(entry, "guid") as string;
						if (string.Equals(guid, targetGuid, StringComparison.OrdinalIgnoreCase)) return true;
					}
				}
				return false;
			}
			catch { complete = false; return false; }
		}

		static Type FindType(string fullName)
		{
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type found = null;
				try { found = assembly.GetType(fullName, false); } catch { }
				if (found != null) return found;
			}
			return null;
		}

		static object GetMemberValue(object target, string name)
		{
			if (target == null) return null;
			var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			try { var property = target.GetType().GetProperty(name, flags); if (property != null) return property.GetValue(target, null); } catch { }
			try { var field = target.GetType().GetField(name, flags); if (field != null) return field.GetValue(target); } catch { }
			return null;
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
