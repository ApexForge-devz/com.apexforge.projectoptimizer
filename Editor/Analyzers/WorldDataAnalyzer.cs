using System;
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
				severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "TER002", title: "Terrain alphamap resolution is high", category: AssetCategory.Other,
				why: "Terrain alphamaps store layer weights and scale with the terrain texture footprint. Very high resolutions increase memory and terrain rendering/baking work.",
				fix: "Review alphamap resolution against the required texture blending detail and selected terrain layers.",
				severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "TER003", title: "Terrain detail resolution is high", category: AssetCategory.Other,
				why: "Terrain detail resolution controls the density of detail-data cells. Excessive resolution increases terrain data size and detail processing cost.",
				fix: "Lower detail resolution where the target platform and terrain presentation do not require the current density.",
				severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "TER004", title: "Terrain uses many terrain layers", category: AssetCategory.Other,
				why: "Every active terrain layer contributes to terrain texturing and layer data. Many layers can increase memory, sampling and authoring complexity.",
				fix: "Consolidate terrain layers only when the visual result and material requirements remain unchanged.",
				severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.Unspecified, backupRequired: false);
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
						"review at or above " + settings.terrainHeightmapResolutionWarn + " x " + settings.terrainHeightmapResolutionWarn)
						?.Note("Large or close-up terrains may legitimately need high resolution.", "Context");
			}

			using (RuleTiming.Begin("TER002", assetPath))
			{
				if (settings.terrainAlphamapResolutionWarn > 0 && facts.alphamapResolution >= settings.terrainAlphamapResolutionWarn)
					entry.AddIssue("TER002", facts.alphamapResolution + " x " + facts.alphamapResolution,
						"review at or above " + settings.terrainAlphamapResolutionWarn + " x " + settings.terrainAlphamapResolutionWarn)
						?.Note("Layer blending detail and terrain size determine whether this resolution is justified.", "Context");
			}

			using (RuleTiming.Begin("TER003", assetPath))
			{
				if (settings.terrainDetailResolutionWarn > 0 && facts.detailResolution >= settings.terrainDetailResolutionWarn)
					entry.AddIssue("TER003", facts.detailResolution.ToString(),
						"review at or above " + settings.terrainDetailResolutionWarn)
						?.Note("Detail resolution is independent from tree/detail prototype counts; inspect the actual detail workload before reducing it.", "Context");
			}

			using (RuleTiming.Begin("TER004", assetPath))
			{
				if (settings.terrainLayerCountWarn > 0 && facts.terrainLayerCount > settings.terrainLayerCountWarn)
					entry.AddIssue("TER004", facts.terrainLayerCount + " terrain layers",
						"review above " + settings.terrainLayerCountWarn)
						?.Note("Layer count is a rendering/data-size signal, not an unconditional error.", "Context");
			}

			return entry;
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
