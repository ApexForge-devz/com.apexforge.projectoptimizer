using System;
using System.Collections.Generic;
using System.Reflection;
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
                why: FontAnalysisPolicy.FallbackChainExplanation + " Verified automation removes only null slots, repeated references, or static fallbacks whose complete known glyph set is already covered by earlier entries.",
                fix: "Automatically remove only proven redundant TMP fallback references; retain non-redundant or incompletely understood entries.",
                severity: IssueSeverity.Info, fixKind: FixKind.Verified, confidence: OptimizationConfidence.HighConfidence,
                risk: FixRisk.High, backupRequired: true);
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
                    {
                        var fontAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                        var redundantIndices = FindRedundantFallbackIndices(fontAsset, "m_FallbackFontAssetTable");
                        var issue = entry.AddIssue("TMP003", fallbackCount + " fallback assets", "Remove only proven redundant TMP fallback references", 0,
                            c => RemoveRedundantFallbacks(c, "m_FallbackFontAssetTable"),
                            FontAnalysisPolicy.FallbackChainExplanation + " Current project threshold: " + fallbackBudget + " fallback assets. Only exact duplicates/nulls or glyph-subset entries proven against static TMP character tables can be removed.",
                            SavingsBasis.Unknown,
                            c => VerifyNoRedundantFallbacks(c.AssetPath, "m_FallbackFontAssetTable"));
                        if (issue != null && redundantIndices.Count == 0)
                        {
                            issue.FixKind = FixKind.None;
                            issue.Fix = null;
                            issue.Verify = null;
                            issue.Recommended = "No automatic change: no null, duplicate, or provably glyph-shadowed TMP fallback reference was found.";
                        }
                        else if (issue != null)
                            issue.Items("Safe redundant fallback indices", FormatIndices(redundantIndices), DetailKind.Good);
                    }

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

        static List<int> FindRedundantFallbackIndices(UnityEngine.Object asset, string fallbackPropertyPath)
        {
            var result = new List<int>();
            if (asset == null) return result;
            var so = new SerializedObject(asset);
            var fallback = so.FindProperty(fallbackPropertyPath);
            if (fallback == null || !fallback.isArray) return result;

            var seen = new HashSet<int>();
            var covered = new HashSet<uint>();
            bool canProveGlyphCoverage = IsStaticPopulation(asset) && TryCollectKnownGlyphs(asset, out covered);
            for (int i = 0; i < fallback.arraySize; i++)
            {
                var element = fallback.GetArrayElementAtIndex(i);
                if (element.propertyType != SerializedPropertyType.ObjectReference) { canProveGlyphCoverage = false; continue; }
                var candidate = element.objectReferenceValue;
                if (candidate == null) { result.Add(i); continue; }
                if (!seen.Add(candidate.GetInstanceID())) { result.Add(i); continue; }

                if (canProveGlyphCoverage && IsStaticPopulation(candidate) && TryCollectKnownGlyphs(candidate, out var candidateGlyphs))
                {
                    if (IsSubset(candidateGlyphs, covered)) result.Add(i);
                    else covered.UnionWith(candidateGlyphs);
                }
                else canProveGlyphCoverage = false;
            }
            return result;
        }

        static bool IsStaticPopulation(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            try
            {
                object mode = ReadMember(asset, "atlasPopulationMode", "m_AtlasPopulationMode");
                if (mode != null) return string.Equals(mode.ToString(), "Static", StringComparison.OrdinalIgnoreCase);
                var so = new SerializedObject(asset);
                var property = Find(so, "m_AtlasPopulationMode", "m_AtlasPopulationModeSettings.populationMode");
                return property != null && property.propertyType == SerializedPropertyType.Enum
                    && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                    && string.Equals(property.enumDisplayNames[property.enumValueIndex], "Static", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static bool TryCollectKnownGlyphs(UnityEngine.Object asset, out HashSet<uint> glyphs)
        {
            glyphs = new HashSet<uint>();
            if (asset == null) return false;
            try
            {
                object table = ReadMember(asset, "characterTable", "m_CharacterTable");
                var enumerable = table as System.Collections.IEnumerable;
                if (enumerable == null) return false;
                foreach (object character in enumerable)
                {
                    object unicode = ReadMember(character, "unicode", "m_Unicode");
                    if (unicode == null) return false;
                    glyphs.Add(Convert.ToUInt32(unicode, System.Globalization.CultureInfo.InvariantCulture));
                }
                return true;
            }
            catch { glyphs.Clear(); return false; }
        }

        static object ReadMember(object target, string propertyOrField, string fallbackField = null)
        {
            if (target == null) return null;
            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty(propertyOrField, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                try { return property.GetValue(target, null); } catch { }
            var field = type.GetField(propertyOrField, flags) ?? (!string.IsNullOrEmpty(fallbackField) ? type.GetField(fallbackField, flags) : null);
            if (field != null) try { return field.GetValue(target); } catch { }
            return null;
        }

        static bool IsSubset(HashSet<uint> subset, HashSet<uint> superset)
        {
            if (subset == null || superset == null) return false;
            foreach (uint value in subset) if (!superset.Contains(value)) return false;
            return true;
        }

        static void RemoveRedundantFallbacks(FixContext context, string fallbackPropertyPath)
        {
            context.EnsureWritable();
            var asset = AssetDatabase.LoadMainAssetAtPath(context.AssetPath);
            var indices = FindRedundantFallbackIndices(asset, fallbackPropertyPath);
            if (indices.Count == 0) throw new InvalidOperationException("No safe redundant TMP fallback references remain; the asset changed since analysis.");
            var so = new SerializedObject(asset);
            var fallback = so.FindProperty(fallbackPropertyPath);
            if (fallback == null || !fallback.isArray) throw new InvalidOperationException("TMP fallback array is unavailable or changed since analysis.");
            int expectedCount = fallback.arraySize;
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int index = indices[i];
                if (index < 0 || index >= fallback.arraySize) throw new InvalidOperationException("TMP fallback array indices changed since analysis.");
                fallback.DeleteArrayElementAtIndex(index);
                if (fallback.arraySize == expectedCount) fallback.DeleteArrayElementAtIndex(index);
                expectedCount = fallback.arraySize;
            }
            if (!so.ApplyModifiedProperties()) throw new InvalidOperationException("Unity did not apply the TMP fallback-array changes.");
            EditorUtility.SetDirty(asset);
            context.NeedsSave = true;
        }

        static bool VerifyNoRedundantFallbacks(string assetPath, string fallbackPropertyPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset != null && FindRedundantFallbackIndices(asset, fallbackPropertyPath).Count == 0;
        }

        static List<string> FormatIndices(List<int> indices)
        {
            var result = new List<string>(indices != null ? indices.Count : 0);
            if (indices != null) for (int i = 0; i < indices.Count; i++) result.Add("Fallback index " + indices[i]);
            return result;
        }
    }
}
