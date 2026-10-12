using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Project-level world data that is stored as .asset files rather than prefabs/scenes. The
	/// analyzer deliberately reports terrain density as a review signal: a large world may need high
	/// resolutions, but high resolution is expensive in memory and bake time and should be justified.
	/// </summary>
	public sealed class WorldDataAnalyzer : IAssetAnalyzer
	{
		public AssetCategory Category => AssetCategory.Other;
		public AssetKindMask SupportedKinds => AssetKindMask.DataAsset;

		static WorldDataAnalyzer()
		{
			Rules.Register(id: "TER001", title: "Terrain heightmap resolution is high", category: AssetCategory.Other,
				why: "Terrain heightmaps consume memory and increase terrain processing/bake cost. High resolution can be necessary for large or close-up terrain, so this is a policy review signal rather than proof of waste.",
				fix: "Use the lowest heightmap resolution that preserves the required terrain silhouette and playable detail for the target platform.",
				severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.High, backupRequired: true);

			Rules.Register(id: "TER002", title: "Terrain alphamap resolution is high", category: AssetCategory.Other,
				why: "Terrain alphamaps store layer weights and scale with the terrain texture footprint. Very high resolutions increase memory and terrain rendering/baking work.",
				fix: "Review alphamap resolution against the required texture blending detail and selected terrain layers.",
				severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.High, backupRequired: true);

			Rules.Register(id: "TER003", title: "Terrain detail resolution is high", category: AssetCategory.Other,
				why: "Terrain detail resolution controls the density of detail-data cells. Excessive resolution increases terrain data size and detail processing cost.",
				fix: "Lower detail resolution where the target platform and terrain presentation do not require the current density.",
				severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.High, backupRequired: true);

			Rules.Register(id: "TER004", title: "Terrain uses many terrain layers", category: AssetCategory.Other,
				why: "Every active terrain layer contributes to terrain texturing and layer data. Many layers can increase memory, sampling and authoring complexity.",
				fix: "Consolidate terrain layers only when the visual result and material requirements remain unchanged.",
				severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.High, backupRequired: true);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
				return false;

			var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
			return type == typeof(TerrainData);
		}

		[Serializable]
		public sealed class TerrainFacts
		{
			public int heightmapResolution;
			public int alphamapResolution;
			public int detailResolution;
			public int detailResolutionPerPatch;
			public int terrainLayerCount;
			public int treePrototypeCount;
			public int detailPrototypeCount;
			public float sizeX;
			public float sizeY;
			public float sizeZ;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var terrain = AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath);
			if (terrain == null) return null;

			var facts = ctx.GetCached(assetPath, () => ReadFacts(terrain));
			if (facts == null) return null;

			var entry = new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileNameWithoutExtension(assetPath),
				Directory = System.IO.Path.GetDirectoryName(assetPath) ?? string.Empty,
				Extension = ".asset",
				Category = Category,
				TypeLabel = "TerrainData",
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : 0
			};

			entry.Set("heightmap", facts.heightmapResolution);
			entry.Set("alphamap", facts.alphamapResolution);
			entry.Set("detail", facts.detailResolution);
			entry.Set("layers", facts.terrainLayerCount);
			entry.Set("trees", facts.treePrototypeCount);
			entry.Set("details", facts.detailPrototypeCount);
			entry.Set("size", facts.sizeX.ToString("0") + " x " + facts.sizeZ.ToString("0"));

			var settings = OptimizerSettings.Instance;
			using (RuleTiming.Begin("TER001", assetPath))
			{
				if (settings.terrainHeightmapResolutionWarn > 0 && facts.heightmapResolution >= settings.terrainHeightmapResolutionWarn)
					entry.AddIssue("TER001", facts.heightmapResolution + " x " + facts.heightmapResolution,
						"Select a lower heightmap resolution", options: BuildHeightmapOptions(facts.heightmapResolution, entry.Guid))
						?.Note("Large or close-up terrains may legitimately need high resolution. Apply a lower option only after reviewing the expected silhouette loss.", "Context");
			}

			using (RuleTiming.Begin("TER002", assetPath))
			{
				if (settings.terrainAlphamapResolutionWarn > 0 && facts.alphamapResolution >= settings.terrainAlphamapResolutionWarn)
					entry.AddIssue("TER002", facts.alphamapResolution + " x " + facts.alphamapResolution,
						"Select a lower alphamap resolution", options: BuildAlphamapOptions(facts.alphamapResolution, entry.Guid))
						?.Note("Layer blending detail and terrain size determine whether this resolution is justified. The selected option resamples alphamap data.", "Context");
			}

			using (RuleTiming.Begin("TER003", assetPath))
			{
				if (settings.terrainDetailResolutionWarn > 0 && facts.detailResolution >= settings.terrainDetailResolutionWarn)
					entry.AddIssue("TER003", facts.detailResolution.ToString(),
						"Select a lower detail resolution", options: BuildDetailOptions(facts.detailResolution, facts.detailResolutionPerPatch, entry.Guid))
						?.Note("Detail resolution is independent from tree/detail prototype counts; lowering it may remove detail placement coverage. Review the resulting terrain before accepting.", "Context");
			}

			using (RuleTiming.Begin("TER004", assetPath))
			{
				if (settings.terrainLayerCountWarn > 0 && facts.terrainLayerCount > settings.terrainLayerCountWarn)
					entry.AddIssue("TER004", facts.terrainLayerCount + " terrain layers",
						"Review terrain layers; remove only trailing unpainted layers when intended", options: BuildTrailingLayerOptions(assetPath, terrain, entry.Guid))
						?.Note("Layer count is a rendering/data-size signal, not an unconditional error. Only trailing layers proven to have zero alphamap weight are offered for removal; future authoring intent still requires the user's explicit choice.", "Review required");
			}

			return entry;
		}

		static List<FixOption> BuildHeightmapOptions(int current, string expectedGuid)
		{
			var options = new List<FixOption>();
			if (string.IsNullOrEmpty(expectedGuid)) return options;
			int[] supported = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
			int recommended = -1;
			for (int i = supported.Length - 1; i >= 0; i--)
				if (supported[i] < current) { recommended = supported[i]; break; }
			for (int i = supported.Length - 1; i >= 0; i--)
			{
				int target = supported[i];
				if (target >= current) continue;
				options.Add(new FixOption
				{
					ActionId = "terrain_heightmap_" + target,
					Label = "Set heightmap to " + target + " × " + target,
					Recommended = target == recommended,
					Risk = FixRisk.High,
					BackupRequiredOverride = true,
					Warning = "This resamples terrain height data and can change silhouettes. Review the terrain after applying.",
					Apply = c => ApplyHeightmapResolution(c, target, current, expectedGuid),
					Verify = c => VerifyTerrainResolution(c.AssetPath, expectedGuid, target, TerrainResolution.Heightmap)
				});
			}
			return options;
		}

		static List<FixOption> BuildAlphamapOptions(int current, string expectedGuid)
		{
			var options = new List<FixOption>();
			if (string.IsNullOrEmpty(expectedGuid)) return options;
			int recommended = Mathf.Max(16, (current / 2 / 16) * 16);
			var targets = new HashSet<int>();
			foreach (int divisor in new[] { 2, 4, 8 })
			{
				int target = Mathf.Max(16, (current / divisor / 16) * 16);
				if (target >= current || !targets.Add(target)) continue;
				options.Add(new FixOption
				{
					ActionId = "terrain_alphamap_" + target,
					Label = "Set alphamap to " + target + " × " + target,
					Recommended = target == recommended,
					Risk = FixRisk.High,
					BackupRequiredOverride = true,
					Warning = "This resamples terrain-layer weights and can soften blend boundaries. Review the terrain after applying.",
					Apply = c => ApplyAlphamapResolution(c, target, current, expectedGuid),
					Verify = c => VerifyTerrainResolution(c.AssetPath, expectedGuid, target, TerrainResolution.Alphamap)
				});
			}
			return options;
		}

		static List<FixOption> BuildDetailOptions(int current, int patchResolution, string expectedGuid)
		{
			var options = new List<FixOption>();
			if (string.IsNullOrEmpty(expectedGuid)) return options;
			int recommended = Mathf.Max(16, current / 2);
			var targets = new HashSet<int>();
			foreach (int divisor in new[] { 2, 4 })
			{
				int target = Mathf.Max(16, current / divisor);
				if (target >= current || !targets.Add(target)) continue;
				options.Add(new FixOption
				{
					ActionId = "terrain_detail_" + target,
					Label = "Set detail resolution to " + target,
					Recommended = target == recommended,
					Risk = FixRisk.High,
					BackupRequiredOverride = true,
					Warning = "This reduces the terrain detail grid and can remove fine placement coverage. Review the terrain after applying.",
					Apply = c => ApplyDetailResolution(c, target, current, patchResolution, expectedGuid),
					Verify = c => VerifyTerrainResolution(c.AssetPath, expectedGuid, target, TerrainResolution.Detail)
				});
			}
			return options;
		}

		static TerrainData ReadTerrain(string path) => AssetDatabase.LoadAssetAtPath<TerrainData>(path);

		enum TerrainResolution { Heightmap, Alphamap, Detail }

		static bool HasExpectedAssetGuid(string path, string expectedGuid)
		{
			if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(expectedGuid)) return false;
			try { return string.Equals(AssetDatabase.AssetPathToGUID(path), expectedGuid, StringComparison.OrdinalIgnoreCase); }
			catch { return false; }
		}

		static bool VerifyTerrainResolution(string path, string expectedGuid, int target, TerrainResolution kind)
		{
			try
			{
				if (!HasExpectedAssetGuid(path, expectedGuid)) return false;
				TerrainData terrain = ReadTerrain(path);
				if (terrain == null) return false;
				switch (kind)
				{
					case TerrainResolution.Heightmap: return terrain.heightmapResolution == target;
					case TerrainResolution.Alphamap: return terrain.alphamapResolution == target;
					case TerrainResolution.Detail: return terrain.detailResolution == target;
					default: return false;
				}
			}
			catch { return false; }
		}

		static void ApplyHeightmapResolution(FixContext context, int target, int expectedCurrent, string expectedGuid)
		{
			if (!HasExpectedAssetGuid(context.AssetPath, expectedGuid))
				throw new InvalidOperationException("Terrain asset identity changed after analysis. Rescan before applying.");
			TerrainData terrain = ReadTerrain(context.AssetPath);
			if (terrain == null || terrain.heightmapResolution != expectedCurrent || target >= terrain.heightmapResolution || !IsSupportedHeightmapResolution(target))
				throw new InvalidOperationException("Terrain heightmap changed after analysis or the target is invalid. Rescan and choose a supported lower resolution.");
			context.RequireBackup(context.AssetPath);
			Undo.RecordObject(terrain, "Reduce terrain heightmap resolution");
			terrain.heightmapResolution = target;
			EditorUtility.SetDirty(terrain);
			context.NeedsSave = true;
		}

		static void ApplyAlphamapResolution(FixContext context, int target, int expectedCurrent, string expectedGuid)
		{
			if (!HasExpectedAssetGuid(context.AssetPath, expectedGuid))
				throw new InvalidOperationException("Terrain asset identity changed after analysis. Rescan before applying.");
			TerrainData terrain = ReadTerrain(context.AssetPath);
			if (terrain == null || terrain.alphamapResolution != expectedCurrent || target >= terrain.alphamapResolution || target < 16 || target % 16 != 0)
				throw new InvalidOperationException("Terrain alphamap changed after analysis or the target is invalid. Rescan and choose a valid lower resolution.");
			context.RequireBackup(context.AssetPath);
			Undo.RecordObject(terrain, "Reduce terrain alphamap resolution");
			terrain.alphamapResolution = target;
			EditorUtility.SetDirty(terrain);
			context.NeedsSave = true;
		}

		static void ApplyDetailResolution(FixContext context, int target, int expectedCurrent, int patchResolution, string expectedGuid)
		{
			if (!HasExpectedAssetGuid(context.AssetPath, expectedGuid))
				throw new InvalidOperationException("Terrain asset identity changed after analysis. Rescan before applying.");
			TerrainData terrain = ReadTerrain(context.AssetPath);
			if (terrain == null || terrain.detailResolution != expectedCurrent || terrain.detailResolutionPerPatch != patchResolution || target >= terrain.detailResolution || target < 16 || !IsValidDetailPatchResolution(patchResolution))
				throw new InvalidOperationException("Terrain detail settings changed after analysis or the target is invalid. Rescan before applying.");
			context.RequireBackup(context.AssetPath);
			Undo.RecordObject(terrain, "Reduce terrain detail resolution");
			terrain.SetDetailResolution(target, patchResolution);
			EditorUtility.SetDirty(terrain);
			context.NeedsSave = true;
		}

		static bool IsSupportedHeightmapResolution(int value)
		{
			return value == 33 || value == 65 || value == 129 || value == 257 || value == 513 || value == 1025 || value == 2049 || value == 4097;
		}

		static bool IsValidDetailPatchResolution(int value) => value == 8 || value == 16 || value == 32 || value == 64;

		static List<FixOption> BuildTrailingLayerOptions(string assetPath, TerrainData terrain, string expectedGuid)
		{
			var options = new List<FixOption>();
			if (string.IsNullOrEmpty(expectedGuid) || !HasExpectedAssetGuid(assetPath, expectedGuid)) return options;
			int trailingUnused = CountTrailingUnpaintedLayers(terrain);
			if (trailingUnused <= 0) return options;
			int layerCount = terrain.terrainLayers != null ? terrain.terrainLayers.Length : 0;
			for (int count = 1; count <= trailingUnused; count++)
			{
				int removeCount = count;
				options.Add(new FixOption
				{
					ActionId = "terrain_remove_trailing_layers_" + removeCount,
					Label = "Remove " + removeCount + " trailing unpainted layer(s)",
					Recommended = false,
					Risk = FixRisk.High,
					BackupRequiredOverride = true,
					Warning = "Only trailing layers with zero alphamap weight are eligible. This removes assigned layers and affects future terrain authoring; confirm they are not intentionally reserved.",
					Apply = c => ApplyRemoveTrailingLayers(c, removeCount, layerCount, expectedGuid),
					Verify = c => VerifyLayerCount(c.AssetPath, expectedGuid, layerCount - removeCount)
				});
			}
			return options;
		}

		static int CountTrailingUnpaintedLayers(TerrainData terrain)
		{
			if (terrain == null || terrain.terrainLayers == null || terrain.terrainLayers.Length == 0) return 0;
			int layerCount = terrain.terrainLayers.Length;
			var used = new bool[layerCount];
			const int chunkSize = 128;
			try
			{
				for (int y = 0; y < terrain.alphamapHeight; y += chunkSize)
				{
					int height = Mathf.Min(chunkSize, terrain.alphamapHeight - y);
					for (int x = 0; x < terrain.alphamapWidth; x += chunkSize)
					{
						int width = Mathf.Min(chunkSize, terrain.alphamapWidth - x);
						float[,,] maps = terrain.GetAlphamaps(x, y, width, height);
						for (int py = 0; py < height; py++)
							for (int px = 0; px < width; px++)
								for (int layer = 0; layer < Mathf.Min(layerCount, maps.GetLength(2)); layer++)
									if (maps[py, px, layer] > 0.00001f) used[layer] = true;
					}
					int trailing = 0;
					for (int i = layerCount - 1; i >= 0 && !used[i]; i--) trailing++;
					if (trailing == 0) return 0;
					// Continue checking all chunks because a weight may be painted farther across the map.
				}
				int result = 0;
				for (int i = layerCount - 1; i >= 0 && !used[i]; i--) result++;
				return result;
			}
			catch { return 0; }
		}

		static void ApplyRemoveTrailingLayers(FixContext context, int removeCount, int expectedLayerCount, string expectedGuid)
		{
			if (!HasExpectedAssetGuid(context.AssetPath, expectedGuid))
				throw new InvalidOperationException("Terrain asset identity changed after analysis. Rescan before applying.");
			TerrainData terrain = ReadTerrain(context.AssetPath);
			if (terrain == null || terrain.terrainLayers == null || terrain.terrainLayers.Length != expectedLayerCount
				|| removeCount < 1 || removeCount >= expectedLayerCount || CountTrailingUnpaintedLayers(terrain) < removeCount)
				throw new InvalidOperationException("Terrain layers changed or at least one selected layer has painted alphamap weights. Rescan before applying.");
			context.RequireBackup(context.AssetPath);
			Undo.RecordObject(terrain, "Remove trailing unpainted terrain layers");
			var reduced = new TerrainLayer[expectedLayerCount - removeCount];
			Array.Copy(terrain.terrainLayers, reduced, reduced.Length);
			terrain.terrainLayers = reduced;
			EditorUtility.SetDirty(terrain);
			context.NeedsSave = true;
		}

		static bool VerifyLayerCount(string path, string expectedGuid, int expectedCount)
		{
			try
			{
				if (!HasExpectedAssetGuid(path, expectedGuid)) return false;
				TerrainData terrain = ReadTerrain(path);
				return terrain != null && terrain.terrainLayers != null && terrain.terrainLayers.Length == expectedCount && terrain.alphamapLayers == expectedCount;
			}
			catch { return false; }
		}

		static TerrainFacts ReadFacts(TerrainData terrain)
		{
			return new TerrainFacts
			{
				heightmapResolution = terrain.heightmapResolution,
				alphamapResolution = terrain.alphamapResolution,
				detailResolution = terrain.detailResolution,
				detailResolutionPerPatch = terrain.detailResolutionPerPatch,
				terrainLayerCount = terrain.terrainLayers != null ? terrain.terrainLayers.Length : 0,
				treePrototypeCount = terrain.treePrototypes != null ? terrain.treePrototypes.Length : 0,
				detailPrototypeCount = terrain.detailPrototypes != null ? terrain.detailPrototypes.Length : 0,
				sizeX = terrain.size.x,
				sizeY = terrain.size.y,
				sizeZ = terrain.size.z
			};
		}
	}
}
