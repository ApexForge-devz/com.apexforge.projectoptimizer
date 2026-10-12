using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// The families of asset a scan can be pointed at. These mirror the tabs in the window, so
	/// turning one off here is the same as saying "I do not care about that tab this time".
	/// Classification is purely by file extension, which costs nothing - deciding what to scan must
	/// not itself require touching every asset.
	/// </summary>
	public enum AssetKind
	{
		Texture = 0,
		Audio = 1,
		Video = 2,
		Model = 3,
		MaterialShader = 4,
		Animation = 5,
		Prefab = 6,
		Scene = 7,
		Script = 8,
		FontAtlas = 9,
		DataAsset = 10,
		Other = 11
	}

	/// <summary>How the set of assets to scan is chosen.</summary>
	public enum ScanScopeMode
	{
		/// <summary>Everything under Assets, minus the excluded folders.</summary>
		EntireProject = 0,
		/// <summary>Only the folders on the include list.</summary>
		SelectedFolders = 1,
		/// <summary>
		/// Only assets the AssetDatabase can show a build reaching. A lower bound, not a guarantee:
		/// see <see cref="ScanScope.BuildReachableSet"/>.
		/// </summary>
		ShippedInBuild = 2
	}

	/// <summary>Extension-based classification, plus the labels and help text the options UI shows.</summary>
	public static class AssetKinds
	{
		public static readonly AssetKind[] All =
		{
			AssetKind.Texture, AssetKind.Audio, AssetKind.Video, AssetKind.Model,
			AssetKind.MaterialShader, AssetKind.Animation, AssetKind.Prefab, AssetKind.Scene,
			AssetKind.Script, AssetKind.FontAtlas, AssetKind.DataAsset, AssetKind.Other
		};

		static readonly Dictionary<string, AssetKind> k_ByExtension =
			new Dictionary<string, AssetKind>(StringComparer.OrdinalIgnoreCase)
			{
                // Textures
                { ".png", AssetKind.Texture }, { ".jpg", AssetKind.Texture }, { ".jpeg", AssetKind.Texture },
				{ ".tga", AssetKind.Texture }, { ".psd", AssetKind.Texture }, { ".tif", AssetKind.Texture },
				{ ".tiff", AssetKind.Texture }, { ".bmp", AssetKind.Texture }, { ".gif", AssetKind.Texture },
				{ ".exr", AssetKind.Texture }, { ".hdr", AssetKind.Texture }, { ".iff", AssetKind.Texture },
				{ ".pict", AssetKind.Texture }, { ".webp", AssetKind.Texture },

                // Audio
                { ".wav", AssetKind.Audio }, { ".mp3", AssetKind.Audio }, { ".ogg", AssetKind.Audio },
				{ ".aif", AssetKind.Audio }, { ".aiff", AssetKind.Audio }, { ".flac", AssetKind.Audio },
				{ ".mod", AssetKind.Audio }, { ".it", AssetKind.Audio }, { ".s3m", AssetKind.Audio },
				{ ".xm", AssetKind.Audio },

                // Video
                { ".mp4", AssetKind.Video }, { ".mov", AssetKind.Video }, { ".m4v", AssetKind.Video },
				{ ".webm", AssetKind.Video }, { ".avi", AssetKind.Video }, { ".mpg", AssetKind.Video },
				{ ".mpeg", AssetKind.Video }, { ".ogv", AssetKind.Video }, { ".wmv", AssetKind.Video },

                // Models (these also carry the imported animation clips)
                { ".fbx", AssetKind.Model }, { ".obj", AssetKind.Model }, { ".dae", AssetKind.Model },
				{ ".blend", AssetKind.Model }, { ".3ds", AssetKind.Model }, { ".max", AssetKind.Model },
				{ ".ma", AssetKind.Model }, { ".mb", AssetKind.Model }, { ".c4d", AssetKind.Model },
				{ ".dxf", AssetKind.Model }, { ".gltf", AssetKind.Model }, { ".glb", AssetKind.Model },

                // Materials and shaders
                { ".mat", AssetKind.MaterialShader }, { ".shader", AssetKind.MaterialShader },
				{ ".shadergraph", AssetKind.MaterialShader }, { ".shadersubgraph", AssetKind.MaterialShader },
				{ ".cginc", AssetKind.MaterialShader }, { ".hlsl", AssetKind.MaterialShader },

                // Animation
                { ".anim", AssetKind.Animation }, { ".controller", AssetKind.Animation },
				{ ".overridecontroller", AssetKind.Animation }, { ".mask", AssetKind.Animation },

				{ ".prefab", AssetKind.Prefab },
				{ ".unity", AssetKind.Scene },
				{ ".cs", AssetKind.Script },

                // Fonts and atlases
                { ".ttf", AssetKind.FontAtlas }, { ".otf", AssetKind.FontAtlas },
				{ ".spriteatlas", AssetKind.FontAtlas }, { ".spriteatlasv2", AssetKind.FontAtlas },

				{ ".asset", AssetKind.DataAsset }
			};

		public static AssetKind Of(string assetPath)
		{
			var extension = Path.GetExtension(assetPath);
			if (string.IsNullOrEmpty(extension)) return AssetKind.Other;
			return k_ByExtension.TryGetValue(extension, out var kind) ? kind : AssetKind.Other;
		}

		public static string Label(AssetKind kind)
		{
			switch (kind)
			{
				case AssetKind.Texture: return "Textures and sprites";
				case AssetKind.Audio: return "Audio clips";
				case AssetKind.Video: return "Video clips";
				case AssetKind.Model: return "Models and meshes";
				case AssetKind.MaterialShader: return "Materials and shaders";
				case AssetKind.Animation: return "Animation clips and controllers";
				case AssetKind.Prefab: return "Prefabs";
				case AssetKind.Scene: return "Scenes";
				case AssetKind.Script: return "C# scripts";
				case AssetKind.FontAtlas: return "Fonts and sprite atlases";
				case AssetKind.DataAsset: return "Data assets (.asset)";
				default: return "Everything else";
			}
		}

		/// <summary>Plain-English cost note, so turning something off is an informed choice.</summary>
		public static string Description(AssetKind kind)
		{
			switch (kind)
			{
				case AssetKind.Texture:
					return ".png .jpg .tga .psd .exr and friends. Reads the importer for every one, and loads the "
						   + "texture itself only when a rule needs its real dimensions. Usually the largest group "
						   + "in a project and the one with the biggest memory wins.";
				case AssetKind.Audio:
					return ".wav .mp3 .ogg .aiff and friends. Each clip is loaded to read its length and channel "
						   + "count. Cheap per clip.";
				case AssetKind.Video:
					return ".mp4 .mov .webm and friends. Loads each clip for its resolution and frame rate.";
				case AssetKind.Model:
					return ".fbx .obj .blend and friends. Every mesh in the file is loaded to count vertices, so a "
						   + "single large character or environment file can take seconds on its own. This is the "
						   + "group to turn off first when a scan is too slow.";
				case AssetKind.MaterialShader:
					return ".mat .shader .shadergraph. Loads each material to read its shader, keywords and "
						   + "property values. Also feeds the duplicate-material comparison.";
				case AssetKind.Animation:
					return ".anim .controller .mask. Reads curves and keyframe counts off each clip, and walks "
						   + "every state machine in a controller.";
				case AssetKind.Prefab:
					return "Loads each .prefab and walks its whole hierarchy. Loading a prefab resolves all of its "
						   + "references, so Unity logs an error here for every missing script or broken reference "
						   + "the prefab already had - the scan surfaces existing damage, it does not cause it.";
				case AssetKind.Scene:
					return "Opens each .unity file in an isolated preview stage, measures it, and closes it again. "
						   + "Normally the slowest part of any scan, and the loudest: a scene full of broken "
						   + "references produces one Unity error per broken instance while it loads.";
				case AssetKind.Script:
					return "Reads each .cs file as text looking for expensive per-frame patterns. No Unity loading "
						   + "involved, so this is fast even on very large codebases.";
				case AssetKind.FontAtlas:
					return ".ttf .otf .spriteatlas. Reads atlas dimensions, padding and packing settings.";
				case AssetKind.DataAsset:
					return "ScriptableObjects and other .asset files, including TextMesh Pro font assets. Only the "
						   + "ones the tool recognises are analysed; the rest are counted and skipped.";
				default:
					return "Everything with no analyzer of its own: .playable, .terrainlayer, .inputactions, "
						   + ".txt, .dll and the rest. No per-asset rules run on them, but they are still part of "
						   + "the project, so switching this off also removes them from the duplicate and unused "
						   + "passes - a duplicated .txt would stop being reported.";
			}
		}
	}

	/// <summary>
	/// The resolved answer to "which assets is this scan going to look at". Paths are fed in one at
	/// a time through <see cref="Consider"/> so the caller keeps control of the time slicing, and
	/// every rejection is counted so the window can explain what it left out rather than silently
	/// reporting a smaller number.
	/// </summary>
	public sealed class ScanScope
	{
		public readonly List<string> Paths = new List<string>();

		/// <summary>
		/// Every statically referenceable asset in the project, whether or not it is being scanned.
		/// <para>
		/// Collected alongside the scan scope because it costs nothing extra here - the path list is
		/// already being walked - and because getting it wrong is expensive: this is the set the
		/// reference graph is built from, and a reference graph missing a folder is a reference
		/// graph that will tell you an asset has no referrers when it has several.
		/// </para>
		/// <para>
		/// Packages are included. Nothing in a package can be rewritten, which is exactly why a
		/// referrer inside one has to be visible: it is the difference between "this duplicate can
		/// be collapsed" and "this duplicate is held by something read-only".
		/// </para>
		/// </summary>
		public readonly List<string> ReferenceScopePaths = new List<string>();

		public int Candidates;
		public int SkippedByFolder;
		public int SkippedByKind;
		public int SkippedTooLarge;
		public int SkippedOutsideBuild;
		public long SkippedTooLargeBytes;

		/// <summary>Assets a player build reaches. Only built for <see cref="ScanScopeMode.ShippedInBuild"/>.</summary>
		public HashSet<string> BuildSet { get; private set; }

		/// <summary>
		/// True when the dependency walk that produced <see cref="BuildSet"/> ran to completion.
		/// <para>
		/// A failed traversal produces a <em>smaller</em> reachable set, and a smaller set means more
		/// assets look unreachable. That is the direction that gets things deleted, so an incomplete
		/// walk must never be read as an ordinary result: the set is still usable for filtering what
		/// to look at, and unusable as evidence that anything is absent from the build.
		/// </para>
		/// </summary>
		public bool BuildReachabilityComplete { get; private set; } = true;

		/// <summary>How many traversal steps failed while building the reachable set.</summary>
		public int BuildReachabilityFailureCount { get; private set; }

		/// <summary>What went wrong, for the caveat and the console.</summary>
		public readonly List<string> BuildReachabilityFailures = new List<string>();

		/// <summary>One line describing what the reachability walk could not see. Empty when it saw everything.</summary>
		public string ReachabilityIncompletenessSummary()
		{
			if (BuildReachabilityComplete) return string.Empty;
			return BuildReachabilityFailureCount + " step(s) of the build dependency walk failed, so the "
				   + "reachable set is smaller than the truth: "
				   + string.Join("; ", BuildReachabilityFailures.ToArray());
		}

		readonly OptimizerSettings m_Settings;
		readonly long m_MaxBytes;
		readonly bool[] m_KindEnabled;

		public ScanScope(OptimizerSettings settings)
		{
			m_Settings = settings ?? new OptimizerSettings();
			m_MaxBytes = m_Settings.maxAssetSizeMb > 0 ? m_Settings.maxAssetSizeMb * 1024L * 1024L : 0L;

			m_KindEnabled = new bool[AssetKinds.All.Length + 1];
			foreach (var kind in AssetKinds.All) m_KindEnabled[(int)kind] = m_Settings.IsKindEnabled(kind);
		}

		/// <summary>True when this scope needs a build-reachability set before it can filter.</summary>
		public bool NeedsBuildSet => m_Settings.scopeMode == ScanScopeMode.ShippedInBuild && BuildSet == null;

		/// <summary>
		/// Computes the build-reachable set, a step at a time.
		/// <para>
		/// Deliberately not done in the constructor. On a large project the transitive dependency
		/// walk runs for a long time, and a constructor cannot hand a frame back - so the scanner
		/// would report itself as time-sliced and then freeze the editor solid before the progress
		/// bar had drawn anything. Now it is a stage like any other, with progress of its own.
		/// </para>
		/// </summary>
		public IEnumerator<bool> BuildReachableSetIncremental(Action<string> onStatus = null)
		{
			if (!NeedsBuildSet)
			{
				yield return true;
				yield break;
			}

			onStatus?.Invoke("Finding the scenes and folders a build starts from...");
			var roots = new List<string>();

			foreach (var scene in EditorBuildSettings.scenes)
				if (scene.enabled && !string.IsNullOrEmpty(scene.path)) roots.Add(scene.path);

			yield return false;

			// StreamingAssets is collected separately and never dependency-walked. Unity copies those
			// files into the player verbatim, so they ship whatever else is true - and asking the
			// AssetDatabase to walk each one costs a query per file to learn nothing, on exactly the
			// folder most likely to hold thousands of them.
			var streamingAssets = new List<string>();

			IEnumerable<string> allPaths = AssetDatabaseThreadSafeUtility.IsComplete
				? AssetDatabaseThreadSafeUtility.EnumerateIndexedAssetPaths()
				: AssetDatabase.GetAllAssetPaths();
			int allPathIndex = 0;
			foreach (var path in allPaths)
			{
				if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

				if (AssetPathUtility.IsInStreamingAssets(path)) streamingAssets.Add(path);
				else if (AssetPathUtility.IsInResources(path)) roots.Add(path);

				if ((allPathIndex++ & 2047) == 0) yield return false;
			}
			// Addressables are explicit content roots even when no scene/static reference points at them.
			// This is dependency-free: the optional package is accessed only through reflection.
			var addressableRoots = new HashSet<string>(StringComparer.Ordinal);
			AddressablesUtility.AddExplicitRoots(addressableRoots);
			foreach (var addressableRoot in addressableRoots) roots.Add(addressableRoot);

			try
			{
				foreach (var preloaded in PlayerSettings.GetPreloadedAssets())
				{
					if (preloaded == null) continue;
					var path = AssetDatabase.GetAssetPath(preloaded);
					if (!string.IsNullOrEmpty(path)) roots.Add(path);
				}
			}
			catch (Exception e)
			{
				BuildReachabilityComplete = false;
				BuildReachabilityFailureCount++;
				if (BuildReachabilityFailures.Count < 10)
					BuildReachabilityFailures.Add("preloaded assets (" + e.Message + ")");
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not read the preloaded asset list: " + e.Message);
			}

			yield return false;

			var reachable = new HashSet<string>(roots, StringComparer.Ordinal);
			foreach (var path in streamingAssets) reachable.Add(path);

			if (roots.Count > 0)
			{
				onStatus?.Invoke("Following what " + roots.Count + " build root(s) depend on...");
				yield return false;

				// Walked in batches rather than as one call. GetDependencies does not yield, so a
				// single call over every root is an uninterruptible stall on a large project; and a
				// batch that throws now costs its own roots rather than the entire walk.
				const int batchSize = 256;
				for (int start = 0; start < roots.Count; start += batchSize)
				{
					int count = Math.Min(batchSize, roots.Count - start);
					var batch = roots.GetRange(start, count).ToArray();

					try
					{
						foreach (var dependency in AssetDatabase.GetDependencies(batch, true))
							reachable.Add(dependency);
					}
					catch (Exception e)
					{
						// Recorded, not swallowed. Everything these roots led to is now missing from
						// the reachable set, which makes those assets look unreachable - the exact
						// mistake that gets content deleted.
						BuildReachabilityComplete = false;
						BuildReachabilityFailureCount++;
						if (BuildReachabilityFailures.Count < 10)
						{
							BuildReachabilityFailures.Add("roots " + start + "-" + (start + count - 1)
														  + " (" + e.Message + ")");
						}
						Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Could not walk part of the build's dependencies: "
										 + e.Message);
					}

					onStatus?.Invoke("Following build dependencies... "
									 + Math.Min(start + count, roots.Count) + " of " + roots.Count + " roots");
					yield return false;
				}
			}

			BuildSet = reachable;
			yield return true;
		}

		public int Skipped => SkippedByFolder + SkippedByKind + SkippedTooLarge + SkippedOutsideBuild;

		/// <summary>
		/// Offers one asset path to the scope. Returns true when it was accepted for scanning, and
		/// records the reason when it was not. Folders and .meta files are rejected without being
		/// counted, since they were never candidates in the first place.
		/// <para>
		/// A path can be rejected here and still be recorded in <see cref="ReferenceScopePaths"/>.
		/// That is the point of the split: not scanning an asset is a statement about what to
		/// report, not a claim that it holds no references.
		/// </para>
		/// </summary>
		public bool Consider(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return false;

			bool underAssets = assetPath.StartsWith("Assets/", StringComparison.Ordinal);
			bool underPackages = assetPath.StartsWith("Packages/", StringComparison.Ordinal);
			if (!underAssets && !underPackages) return false;

			var extension = Path.GetExtension(assetPath);
			if (string.Equals(extension, ".meta", StringComparison.OrdinalIgnoreCase)) return false;

			// IsValidFolder is a database lookup, and this runs once per path on a list that can hold
			// hundreds of thousands. Anything with an extension is a file, so only the handful
			// without one need asking.
			if (extension.Length == 0)
			{
				bool isFolder = AssetDatabaseThreadSafeUtility.IsComplete
					? AssetDatabaseThreadSafeUtility.IsValidFolder(assetPath)
					: AssetDatabase.IsValidFolder(assetPath);
				if (isFolder) return false;
			}

			// Recorded before any filter is applied. Which assets are analyzed is the user's choice;
			// which assets can hold a reference is not.
			ReferenceScopePaths.Add(assetPath);

			// Package Manager content is not analyzed as editable project assets, but package paths still
			// count as referrers, which is why they were added above first.
			if (!underAssets) return false;

			Candidates++;

			if (!InSelectedFolders(assetPath) || m_Settings.IsExcluded(assetPath))
			{
				SkippedByFolder++;
				return false;
			}

			if (BuildSet != null && !BuildSet.Contains(assetPath))
			{
				SkippedOutsideBuild++;
				return false;
			}

			if (!m_KindEnabled[(int)AssetKinds.Of(assetPath)])
			{
				SkippedByKind++;
				return false;
			}

			if (m_MaxBytes > 0)
			{
				long size = FileUtility.FileSizeOf(assetPath);
				if (size > m_MaxBytes)
				{
					SkippedTooLarge++;
					SkippedTooLargeBytes += size;
					return false;
				}
			}

			Paths.Add(assetPath);
			return true;
		}

		bool InSelectedFolders(string assetPath)
		{
			if (m_Settings.scopeMode != ScanScopeMode.SelectedFolders) return true;
			var folders = m_Settings.includedFolders;
			if (folders == null || folders.Count == 0) return true;

			for (int i = 0; i < folders.Count; i++)
				if (AssetPathUtility.IsUnder(assetPath, folders[i])) return true;
			return false;
		}

		/// <summary>One line describing what this scope covers and what it left behind.</summary>
		public string Summary()
		{
			var text = Paths.Count + " of " + Candidates + " assets";
			if (Skipped == 0) return text + " (everything under Assets)";

			var reasons = new List<string>();
			if (SkippedByFolder > 0) reasons.Add(SkippedByFolder + " outside the chosen folders");
			if (SkippedOutsideBuild > 0) reasons.Add(SkippedOutsideBuild + " not reachable from the build");
			if (SkippedByKind > 0) reasons.Add(SkippedByKind + " of a type that is switched off");
			if (SkippedTooLarge > 0)
				reasons.Add(SkippedTooLarge + " over the size limit (" + DisplayUtility.FormatBytes(SkippedTooLargeBytes) + ")");
			return text + " - skipping " + string.Join(", ", reasons.ToArray());
		}

		// --- build reachability ---------------------------------------------------------

		/// <summary>
		/// One line naming what this analysis can and cannot see, for anywhere the result is shown.
		/// Kept next to the code that produces the set so the two cannot drift apart.
		/// </summary>
		static HashSet<string> s_BuildReachableCache;
		static bool s_BuildReachabilityDirty = true;

		static ScanScope()
		{
			try { EditorApplication.projectChanged += InvalidateBuildReachability; } catch { }
			try { EditorBuildSettings.sceneListChanged += InvalidateBuildReachability; } catch { }
		}

		static void InvalidateBuildReachability()
		{
			s_BuildReachabilityDirty = true;
			s_BuildReachableCache = null;
		}

		public const string ReachabilityCaveat =
			"This follows the enabled build scenes, Resources, StreamingAssets, preloaded assets and explicit Addressables entries when the optional package is present. "
			+ "It cannot follow AssetBundles built elsewhere or anything loaded by a name your code computes at runtime, so \"not reached\" means \"not reached by static analysis\" and never \"not in the build\".";

		/// <summary>
		/// Everything a player build can reach <em>as far as the AssetDatabase can tell</em>: the
		/// enabled build scenes, every Resources folder, StreamingAssets and the preloaded asset
		/// list, followed transitively. This is one batched dependency query rather than one per
		/// asset, so it stays affordable even on a very large project.
		/// <para>
		/// This is a lower bound on what ships and must be read as one. Explicit Addressables entries
		/// are included when the optional package can be inspected; AssetBundles built elsewhere
		/// and any Resources.Load with a computed name remain invisible to it, so an asset outside
		/// this set has not been shown to be absent from the build - only to be unreachable by the
		/// one mechanism that can be checked statically. See <see cref="ReachabilityCaveat"/>.
		/// </para>
		/// </summary>
		public static HashSet<string> BuildReachableSet()
		{
			if (!s_BuildReachabilityDirty && s_BuildReachableCache != null)
				return s_BuildReachableCache;

			var roots = new List<string>();

			foreach (var scene in EditorBuildSettings.scenes)
				if (scene.enabled && !string.IsNullOrEmpty(scene.path)) roots.Add(scene.path);

			// Resources is included wholesale by the build, and is dependency-walked because a
			// Resources asset can reference anything.
			//
			// StreamingAssets ships too, but is added to the result directly rather than used as a
			// walk root: Unity copies those files in verbatim, so walking each one asks the
			// AssetDatabase a question per file whose answer changes nothing.
			var streamingAssets = new List<string>();
			IEnumerable<string> assetPaths = AssetDatabaseThreadSafeUtility.IsComplete
				? AssetDatabaseThreadSafeUtility.EnumerateIndexedAssetPaths()
				: AssetDatabase.GetAllAssetPaths();
			foreach (var path in assetPaths)
			{
				if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

				if (AssetPathUtility.IsInStreamingAssets(path)) streamingAssets.Add(path);
				else if (AssetPathUtility.IsInResources(path)) roots.Add(path);
			}

			var addressableRoots = new HashSet<string>(StringComparer.Ordinal);
			AddressablesUtility.AddExplicitRoots(addressableRoots);
			foreach (var addressableRoot in addressableRoots) roots.Add(addressableRoot);

			try
			{
				foreach (var preloaded in PlayerSettings.GetPreloadedAssets())
				{
					if (preloaded == null) continue;
					var path = AssetDatabase.GetAssetPath(preloaded);
					if (!string.IsNullOrEmpty(path)) roots.Add(path);
				}
			}
			catch (Exception e)
			{
				throw new InvalidOperationException("The preloaded asset list could not be read.", e);
			}

			var reachable = new HashSet<string>(roots, StringComparer.Ordinal);
			foreach (var path in streamingAssets) reachable.Add(path);

			if (roots.Count > 0)
			{
				try
				{
					foreach (var dependency in AssetDatabase.GetDependencies(roots.ToArray(), true))
						reachable.Add(dependency);
				}
				catch (Exception e)
				{
					throw new InvalidOperationException("The build dependency walk could not be completed.", e);
				}
			}
			s_BuildReachableCache = reachable;
			s_BuildReachabilityDirty = false;
			return reachable;
		}
	}
}
