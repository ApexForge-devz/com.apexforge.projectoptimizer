using System;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// TextMeshPro-specific font evidence. TMP_FontAsset is a TextCore font asset, but its serialized
    /// property layout and fallback/atlas settings are distinct enough to warrant an explicit pass.
    /// All findings remain advisory: reducing an atlas or fallback chain can change glyph coverage.
    /// </summary>
    public sealed class TextMeshProAnalyzer : IAssetAnalyzer
    {
        public AssetCategory Category => AssetCategory.Font;
        public AssetKindMask SupportedKinds => AssetKindMask.DataAsset | AssetKindMask.FontAtlas | AssetKindMask.Other;

        static TextMeshProAnalyzer()
        {
            Rules.Register(id: "TMP001", title: "TMP atlas exceeds the review budget", category: AssetCategory.Font,
                why: FontAnalysisPolicy.AtlasBudgetExplanation + " TMP atlas dimensions should be assessed against the actual character set and target texture limits.",
                fix: "Review the TMP atlas dimensions and glyph population. Reduce the atlas only when the required characters and SDF/effect margins remain valid.",
                severity: IssueSeverity.Warning, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence);
            Rules.Register(id: "TMP002", title: "TMP font uses dynamic atlas population", category: AssetCategory.Font,
                why: FontAnalysisPolicy.DynamicPopulationExplanation,
                fix: "Use static/pre-populated assets only when the required glyph set is known and fixed; retain dynamic population for required runtime-generated or localized text.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence);
            Rules.Register(id: "TMP003", title: "TMP fallback chain is long", category: AssetCategory.Font,
                why: FontAnalysisPolicy.FallbackChainExplanation,
                fix: "Review the fallback chain and remove redundant fallbacks only when multilingual/localized glyph coverage remains complete.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence);
            Rules.Register(id: "TMP004", title: "TMP font uses multiple atlas textures", category: AssetCategory.Font,
                why: FontAnalysisPolicy.MultipleAtlasPagesExplanation,
                fix: "Review glyph coverage, atlas size and multi-atlas settings. Keep additional pages when the project actually needs them.",
                severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence);
        }

        public void BeginScan(ScanContext ctx) { }

        public bool Handles(string assetPath)
        {
            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return false;
            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            return type != null && string.Equals(type.Name, "TMP_FontAsset", StringComparison.Ordinal);
        }

        public AssetEntry Analyze(string assetPath, ScanContext ctx)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (type == null || !string.Equals(type.Name, "TMP_FontAsset", StringComparison.Ordinal)) return null;

            try
            {
                var so = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(assetPath));
                int width = ReadInt(so, "m_AtlasWidth", "m_AtlasWidthProperty", "m_AtlasSize");
                int height = ReadInt(so, "m_AtlasHeight", "m_AtlasHeightProperty", "m_AtlasSize");
                int population = ReadEnum(so, "m_AtlasPopulationMode", "m_AtlasPopulationModeSettings.populationMode");
                int fallbackCount = ReadArraySize(so, "m_FallbackFontAssetTable", "m_fallbackFontAssetTable");
                int atlasCount = ReadArraySize(so, "m_AtlasTextures", "m_AtlasTextures.Array");
                bool multiAtlas = ReadBool(so, "m_IsMultiAtlasTextures", "m_IsMultiAtlasTexturesEnabled");

                if (width <= 0 || height <= 0) return null;

                long fileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath);
                var entry = new AssetEntry
                {
                    Guid = ctx.GetGuid(assetPath),
                    Path = assetPath,
                    Name = System.IO.Path.GetFileName(assetPath),
                    Directory = AssetPathUtility.Directoryish(assetPath),
                    Extension = ".asset",
                    Category = AssetCategory.Font,
                    TypeLabel = "TMP Font Asset",
                    FileSize = fileSize,
                    RuntimeSize = (long)width * height * 4L
                };
                entry.Set("atlas", width + " x " + height);
                entry.Set("atlasPixels", (long)width * height);
                entry.Set("population", population >= 0 ? population.ToString() : "Unknown");
                entry.Set("fallbacks", fallbackCount >= 0 ? (long)fallbackCount : 0L);
                entry.Set("pages", atlasCount > 0 ? (long)atlasCount : (multiAtlas ? 2L : 1L));
                entry.Set("multiAtlas", multiAtlas ? "On" : "Off");

                int budget = FontAnalysisPolicy.AtlasSizeBudget(ctx.Settings);
                int largest = Math.Max(width, height);
                using (RuleTiming.Begin("TMP001", entry.Path))
                    if (budget > 0 && largest > budget)
                        entry.AddIssue("TMP001", width + " x " + height, budget + " or smaller", 0, null,
                            FontAnalysisPolicy.AtlasBudgetExplanation);

                bool dynamic = IsDynamicPopulation(so, population);
                using (RuleTiming.Begin("TMP002", entry.Path))
                    if (dynamic)
                        entry.AddIssue("TMP002", "Dynamic", "Static/pre-populated when the glyph set is fixed", 0, null,
                            FontAnalysisPolicy.DynamicPopulationExplanation);

                int fallbackBudget = FontAnalysisPolicy.FallbackCountBudget(ctx.Settings);
                using (RuleTiming.Begin("TMP003", entry.Path))
                    if (fallbackCount > fallbackBudget)
                        entry.AddIssue("TMP003", fallbackCount + " fallback assets", "Review for redundant fallbacks", 0, null,
                            FontAnalysisPolicy.FallbackChainExplanation + " Current project threshold: " + fallbackBudget + " fallback assets.");

                using (RuleTiming.Begin("TMP004", entry.Path))
                    if (multiAtlas || atlasCount > 1)
                        entry.AddIssue("TMP004", (atlasCount > 0 ? atlasCount : 2) + " atlas textures", "Review atlas/glyph coverage", 0, null,
                            FontAnalysisPolicy.MultipleAtlasPagesExplanation);

                entry.Finalise();
                return entry;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " TMP font analysis skipped '" + assetPath + "': " + e.Message);
                return null;
            }
        }

        static bool IsDynamicPopulation(SerializedObject so, int value)
        {
            string[] names = { "Dynamic", "Dynamic OS", "DynamicOS" };
            var p = Find(so, "m_AtlasPopulationMode", "m_AtlasPopulationModeSettings.populationMode");
            if (p != null && p.propertyType == SerializedPropertyType.Enum)
            {
                string[] display = p.enumDisplayNames;
                int i = p.enumValueIndex;
                if (i >= 0 && i < display.Length)
                {
                    string name = display[i];
                    for (int n = 0; n < names.Length; n++) if (string.Equals(name, names[n], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return value == 2 || value == 1;
        }

        static SerializedProperty Find(SerializedObject so, params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                var p = so.FindProperty(paths[i]);
                if (p != null) return p;
            }
            return null;
        }
        static int ReadInt(SerializedObject so, params string[] paths)
        {
            var p = Find(so, paths); return p != null && p.propertyType == SerializedPropertyType.Integer ? p.intValue : 0;
        }
        static int ReadEnum(SerializedObject so, params string[] paths)
        {
            var p = Find(so, paths); return p != null && p.propertyType == SerializedPropertyType.Enum ? p.enumValueIndex : -1;
        }
        static bool ReadBool(SerializedObject so, params string[] paths)
        {
            var p = Find(so, paths); return p != null && p.propertyType == SerializedPropertyType.Boolean && p.boolValue;
        }
        static int ReadArraySize(SerializedObject so, params string[] paths)
        {
            var p = Find(so, paths); return p != null && p.isArray ? p.arraySize : -1;
        }
    }
}
