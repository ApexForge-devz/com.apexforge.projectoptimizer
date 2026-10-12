using System;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>Migrates persisted rule identifiers when the canonical catalog numbering changes.</summary>
    public static class RuleIdMigration
    {
        public const int CurrentSchemaVersion = 2;

        static readonly Dictionary<string, string> k_PreviousSchemaIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ADR001", "ADR001" },
            { "ADR002", "ADR002" },
            { "ADR003", "ADR003" },
            { "ADR004", "ADR004" },
            { "ADR005", "ADR005" },
            { "ADR006", "ADR006" },
            { "ADR007", "ADR007" },
            { "ADR008", "ADR008" },
            { "ADR009", "ADR009" },
            { "AND001", "AND001" },
            { "AND002", "AND002" },
            { "AND003", "AND003" },
            { "ANI001", "ANI001" },
            { "ANI002", "ANI002" },
            { "ANI003", "ANI003" },
            { "ANI004", "ANI004" },
            { "ANI005", "ANI005" },
            { "ANI006", "ANI006" },
            { "ANI007", "ANI007" },
            { "ANI008", "ANI008" },
            { "ANI009", "ANI009" },
            { "ANI010", "ANI010" },
            { "ANI011", "ANI011" },
            { "ASM001", "ASM001" },
            { "ASM002", "ASM002" },
            { "ATL001", "ATL001" },
            { "ATL002", "ATL002" },
            { "ATL003", "ATL003" },
            { "ATL004", "ATL004" },
            { "ATL005", "ATL005" },
            { "ATL006", "ATL006" },
            { "ATL007", "ATL007" },
            { "AUD001", "AUD001" },
            { "AUD002", "AUD002" },
            { "AUD003", "AUD003" },
            { "AUD004", "AUD004" },
            { "AUD005", "AUD005" },
            { "AUD006", "AUD006" },
            { "AUD007", "AUD007" },
            { "AUD008", "AUD008" },
            { "AUD009", "AUD009" },
            { "AUD010", "AUD010" },
            { "AUD011", "AUD011" },
            { "AUD012", "AUD012" },
            { "AUD013", "AUD013" },
            { "BLD001", "BLD001" },
            { "BLD002", "BLD002" },
            { "BLD003", "BLD003" },
            { "BLD004", "BLD004" },
            { "BLD005", "BLD005" },
            { "BLD006", "BLD006" },
            { "CTL001", "CTL001" },
            { "CTL002", "CTL002" },
            { "CTL003", "CTL003" },
            { "CTL004", "CTL004" },
            { "CTL005", "CTL005" },
            { "CTL006", "CTL006" },
            { "CTL008", "CTL007" },
            { "DEP001", "DEP001" },
            { "DEP002", "DEP002" },
            { "DUP001", "DUP001" },
            { "DUP002", "DUP002" },
            { "FNT001", "FNT001" },
            { "FNT002", "FNT002" },
            { "FNT003", "FNT003" },
            { "FNT004", "FNT004" },
            { "FNT005", "FNT005" },
            { "FNT006", "FNT006" },
            { "FNT007", "FNT007" },
            { "FNT008", "FNT008" },
            { "MAT001", "MAT001" },
            { "MAT002", "MAT002" },
            { "MAT003", "MAT003" },
            { "MAT004", "MAT004" },
            { "MAT005", "MAT005" },
            { "MAT006", "MAT006" },
            { "MAT007", "MAT007" },
            { "MAT008", "MAT008" },
            { "MAT009", "MAT009" },
            { "MAT010", "MAT010" },
            { "MDL001", "MDL001" },
            { "MDL002", "MDL002" },
            { "MDL003", "MDL003" },
            { "MDL004", "MDL004" },
            { "MDL005", "MDL005" },
            { "MDL006", "MDL006" },
            { "MDL007", "MDL007" },
            { "MDL008", "MDL008" },
            { "MDL009", "MDL009" },
            { "MDL010", "MDL010" },
            { "MDL011", "MDL011" },
            { "MDL012", "MDL012" },
            { "MDL013", "MDL013" },
            { "MDL014", "MDL014" },
            { "MDL015", "MDL015" },
            { "MDL016", "MDL016" },
            { "MDL017", "MDL017" },
            { "MDL018", "MDL018" },
            { "PAA0005", "PAA0001" },
            { "PAA0007", "PAA0002" },
            { "PAA0008", "PAA0003" },
            { "PAA3001", "PAA3001" },
            { "PAA3002", "PAA3002" },
            { "PAP0001", "PAP0001" },
            { "PAP0002", "PAP0002" },
            { "PAS0003", "PAS0001" },
            { "PAS0004", "PAS0002" },
            { "PAS0005", "PAS0003" },
            { "PAS0006", "PAS0004" },
            { "PAS0015", "PAS0005" },
            { "PAS0016", "PAS0006" },
            { "PAS0017", "PAS0007" },
            { "PAS0018", "PAS0008" },
            { "PAS0019", "PAS0009" },
            { "PAS0020", "PAS0010" },
            { "PAS0021", "PAS0011" },
            { "PAS0022", "PAS0012" },
            { "PAS0023", "PAS0013" },
            { "PAS0024", "PAS0014" },
            { "PAS0025", "PAS0015" },
            { "PAS0026", "PAS0016" },
            { "PAS0029", "PAS0017" },
            { "PAS0031", "PRJ030" },
            { "PAS0033", "PAS0018" },
            { "PAS0035", "PAS0019" },
            { "PAS1000", "PAS1000" },
            { "PAS1003", "PAS1001" },
            { "PAS1004", "PAS1002" },
            { "PAS1005", "PAS1003" },
            { "PAS1006", "PAS1004" },
            { "PAS1007", "PRJ008" },
            { "PAS1009", "PAS1005" },
            { "PAS1010", "PAS1006" },
            { "PAS1011", "PAS1007" },
            { "PAS1012", "PAS1008" },
            { "PHY001", "PHY001" },
            { "PHY002", "PHY002" },
            { "PHY003", "PHY003" },
            { "PHY004", "PHY004" },
            { "PRE001", "PRE001" },
            { "PRE002", "PRE002" },
            { "PRE003", "PRE003" },
            { "PRE004", "PRE004" },
            { "PRE005", "PRE005" },
            { "PRE006", "PRE006" },
            { "PRE007", "PRE007" },
            { "PRE008", "PRE008" },
            { "PRE009", "PRE009" },
            { "PRE010", "PRE010" },
            { "PRE011", "PRE011" },
            { "PRE012", "PRE012" },
            { "PRE013", "PRE013" },
            { "PRE014", "PRE014" },
            { "PRE015", "PRE015" },
            { "PRE016", "PRE016" },
            { "PRJ001", "PRJ001" },
            { "PRJ002", "PRJ002" },
            { "PRJ003", "PRJ003" },
            { "PRJ004", "PRJ004" },
            { "PRJ005", "PRJ005" },
            { "PRJ006", "PRJ006" },
            { "PRJ007", "PRJ007" },
            { "PRJ008", "PRJ008" },
            { "PRJ010", "PRJ009" },
            { "PRJ011", "PRJ010" },
            { "PRJ012", "PRJ011" },
            { "PRJ013", "PRJ012" },
            { "PRJ014", "PRJ013" },
            { "PRJ015", "PRJ014" },
            { "PRJ016", "PRJ015" },
            { "PRJ017", "PRJ016" },
            { "PRJ018", "PRJ017" },
            { "PRJ019", "PRJ018" },
            { "PRJ020", "PRJ019" },
            { "PRJ021", "PRJ020" },
            { "PRJ022", "PRJ021" },
            { "PRJ023", "PRJ022" },
            { "PRJ024", "PRJ023" },
            { "PRJ025", "PRJ024" },
            { "PRJ026", "PRJ025" },
            { "PRJ027", "PRJ026" },
            { "PRJ028", "PRJ027" },
            { "PRJ029", "PRJ028" },
            { "PRJ030", "PRJ029" },
            { "PRJ031", "PRJ030" },
            { "PRJ032", "PRJ031" },
            { "PRJ034", "PRJ032" },
            { "PRJ035", "PRJ033" },
            { "PRJ036", "PRJ034" },
            { "PRJ037", "PRJ035" },
            { "PRJ038", "PRJ036" },
            { "PRJ039", "PRJ037" },
            { "PRJ040", "PRJ038" },
            { "PRJ041", "PRJ039" },
            { "PRJ042", "PRJ040" },
            { "PRJ043", "PRJ041" },
            { "PRJ044", "PRJ042" },
            { "PRJ045", "PRJ043" },
            { "PRJ046", "PRJ044" },
            { "PRJ047", "PRJ045" },
            { "PRJ048", "PRJ046" },
            { "PRJ049", "PRJ047" },
            { "PRJ050", "PRJ048" },
            { "PRJ051", "PRJ049" },
            { "PRJ052", "PRJ050" },
            { "PRJ054", "PRJ051" },
            { "PRJ055", "PRJ052" },
            { "PRJ056", "PRJ053" },
            { "PRJ057", "PRJ054" },
            { "PRJ058", "PRJ055" },
            { "PRJ059", "PRJ056" },
            { "PRJ060", "PRJ057" },
            { "PRJ062", "PRJ058" },
            { "PRJ063", "PRJ059" },
            { "PRJ064", "PRJ060" },
            { "PRJ065", "PRJ061" },
            { "PRJ066", "PRJ062" },
            { "PRJ067", "PRJ063" },
            { "PRJ068", "PRJ064" },
            { "PRJ069", "PRJ065" },
            { "PRJ070", "PRJ066" },
            { "PRJ071", "PRJ067" },
            { "PRJ072", "PRJ068" },
            { "PRJ073", "PRJ069" },
            { "PRJ074", "PRJ070" },
            { "PRJ075", "PRJ071" },
            { "PRJ076", "PRJ072" },
            { "PRJ077", "PRJ013" },
            { "PRJ078", "PRJ014" },
            { "PRJ079", "PRJ073" },
            { "PRJ080", "PRJ074" },
            { "PRJ081", "PRJ059" },
            { "PRJ082", "PRJ075" },
            { "PRJ083", "PRJ076" },
            { "REN001", "REN001" },
            { "REN002", "REN002" },
            { "REN003", "REN003" },
            { "REN004", "REN004" },
            { "REN005", "REN005" },
            { "REN006", "REN006" },
            { "RND001", "RND001" },
            { "SCR001", "SCR001" },
            { "SCR002", "SCR002" },
            { "SCR003", "SCR003" },
            { "SCR004", "SCR004" },
            { "SCR005", "SCR005" },
            { "SCR006", "SCR006" },
            { "SCR007", "SCR007" },
            { "SCR008", "SCR008" },
            { "SCR009", "SCR009" },
            { "SCR010", "SCR010" },
            { "SCR011", "SCR011" },
            { "SCR012", "SCR012" },
            { "SCR013", "SCR013" },
            { "SCR014", "SCR014" },
            { "SCR015", "SCR015" },
            { "SCR016", "SCR016" },
            { "SCR017", "SCR017" },
            { "SCR018", "SCR018" },
            { "SCR019", "SCR019" },
            { "SCR020", "SCR020" },
            { "SCR021", "SCR021" },
            { "SCR022", "SCR022" },
            { "SCR023", "SCR023" },
            { "SCR024", "SCR024" },
            { "SCR025", "SCR025" },
            { "SHD001", "SHD001" },
            { "SHD002", "SHD002" },
            { "SHD003", "SHD003" },
            { "SHD004", "SHD004" },
            { "SHD005", "SHD005" },
            { "SHD006", "SHD006" },
            { "SHD007", "SHD007" },
            { "SHD008", "SHD008" },
            { "SHD009", "SHD009" },
            { "SHD010", "SHD010" },
            { "SHD011", "SHD011" },
            { "SHD012", "SHD012" },
            { "SHD013", "SHD013" },
            { "SHD014", "SHD014" },
            { "TER001", "TER001" },
            { "TER002", "TER002" },
            { "TER003", "TER003" },
            { "TER004", "TER004" },
            { "TEX001", "TEX001" },
            { "TEX002", "TEX002" },
            { "TEX003", "TEX003" },
            { "TEX004", "TEX004" },
            { "TEX005", "TEX005" },
            { "TEX006", "TEX006" },
            { "TEX007", "TEX007" },
            { "TEX008", "TEX008" },
            { "TEX009", "TEX009" },
            { "TEX010", "TEX010" },
            { "TEX011", "TEX011" },
            { "TEX012", "TEX012" },
            { "TEX013", "TEX013" },
            { "TEX014", "TEX014" },
            { "TEX015", "TEX015" },
            { "TEX016", "TEX016" },
            { "TEX017", "TEX017" },
            { "TEX018", "TEX018" },
            { "TMP001", "TMP001" },
            { "TMP002", "TMP002" },
            { "TMP003", "TMP003" },
            { "TMP004", "TMP004" },
            { "UIT001", "UIT001" },
            { "UIT002", "UIT002" },
            { "UIT003", "UIT003" },
            { "UITK001", "UITK001" },
            { "UITK002", "UITK002" },
            { "UITK003", "UITK003" },
            { "UITK004", "UITK004" },
            { "UNU001", "UNU001" },
            { "UNU002", "UNU002" },
            { "UNU003", "UNU003" },
            { "UNU004", "UNU004" },
            { "UNU005", "UNU005" },
            { "UNU006", "UNU006" },
            { "VFX001", "VFX001" },
            { "VID001", "VID001" },
            { "VID002", "VID002" },
            { "VID003", "VID003" },
            { "VID004", "VID004" },
            { "VID005", "VID005" },
            { "VID006", "VID006" },
            { "VID007", "VID001" },
            { "VID008", "VID007" },
        };

        static readonly HashSet<string> k_PreviousSchemaAliases = new HashSet<string>(StringComparer.Ordinal)
        {
            "PAS0031", "PAS1007", "PRJ077", "PRJ078", "PRJ081", "VID007"
        };

        // Schema-0 IDs that were unassigned before compaction must not inherit settings for new rules.
        static readonly HashSet<string> k_PreviouslyUnassignedReusedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "CTL007", "PAA0001", "PAA0002", "PAA0003", "PAS0001", "PAS0002", "PAS0007", "PAS0008", "PAS0009", "PAS0010", "PAS0011", "PAS0012", "PAS0013", "PAS0014", "PAS1001", "PAS1002", "PAS1008", "PRJ009", "PRJ033", "PRJ053", "PRJ061",
            "PAA001", "PAA002", "PAA003", "PAA004", "PAA005", "PAP001", "PAP002", "PAS001", "PAS002", "PAS003", "PAS004", "PAS005", "PAS006", "PAS007", "PAS008", "PAS009", "PAS010", "PAS011", "PAS012", "PAS013", "PAS014", "PAS015", "PAS016", "PAS017", "PAS018", "PAS019", "PAS020", "PAS021", "PAS022", "PAS023", "PAS024", "PAS025", "PAS026", "PAS027", "PAS028"
        };

        // Schema 1 -> 2 flattens PAS/PAA/PAP numbering and canonicalizes schema-1 aliases.
        static readonly Dictionary<string, string> k_Schema1To2 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "PAA0001", "PAA001" },
            { "PAA0002", "PAA002" },
            { "PAA0003", "PAA003" },
            { "PAA3001", "PAA004" },
            { "PAA3002", "PAA005" },
            { "PAP0001", "PAP001" },
            { "PAP0002", "PAP002" },
            { "PAS0001", "PAS001" },
            { "PAS0002", "PAS002" },
            { "PAS0003", "PAS003" },
            { "PAS0004", "PAS004" },
            { "PAS0005", "PAS005" },
            { "PAS0006", "PAS006" },
            { "PAS0007", "PAS007" },
            { "PAS0008", "PAS008" },
            { "PAS0009", "PAS009" },
            { "PAS0010", "PAS010" },
            { "PAS0011", "PAS011" },
            { "PAS0012", "PAS012" },
            { "PAS0013", "PAS013" },
            { "PAS0014", "PAS014" },
            { "PAS0015", "PAS015" },
            { "PAS0016", "PAS016" },
            { "PAS0017", "PAS017" },
            { "PAS0018", "PAS018" },
            { "PAS0019", "PAS019" },
            { "PAS1000", "PAS020" },
            { "PAS1001", "PAS021" },
            { "PAS1002", "PAS022" },
            { "PAS1003", "PAS023" },
            { "PAS1004", "PAS024" },
            { "PAS1005", "PAS025" },
            { "PAS1006", "PAS026" },
            { "PAS1007", "PAS027" },
            { "PAS1008", "PAS028" },
            { "PAS0031", "PRJ030" },
            { "PRJ077", "PRJ013" },
            { "PRJ078", "PRJ014" },
            { "PRJ081", "PRJ059" },
        };

        // These short forms were not assigned in schema 1 and must be discarded if found in its data.
        static readonly HashSet<string> k_Schema1UnassignedReusedIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "PAA001", "PAA002", "PAA003", "PAA004", "PAA005", "PAP001", "PAP002", "PAS001", "PAS002", "PAS003", "PAS004", "PAS005", "PAS006", "PAS007", "PAS008", "PAS009", "PAS010", "PAS011", "PAS012", "PAS013", "PAS014", "PAS015", "PAS016", "PAS017", "PAS018", "PAS019", "PAS020", "PAS021", "PAS022", "PAS023", "PAS024", "PAS025", "PAS026", "PAS027", "PAS028"
        };

        static readonly HashSet<string> k_Schema1Aliases = new HashSet<string>(StringComparer.Ordinal)
        {
            "PRJ077", "PRJ078", "PRJ081", "PAS0031"
        };

        public static string MapFromPreviousSchema(string ruleId) => MapFromSchema(0, ruleId);

        public static string MapFromSchema(int sourceSchemaVersion, string ruleId)
        {
            if (string.IsNullOrEmpty(ruleId) || sourceSchemaVersion >= CurrentSchemaVersion) return ruleId;

            if (sourceSchemaVersion <= 0)
            {
                if (k_PreviousSchemaIds.TryGetValue(ruleId, out string schema1Id))
                    ruleId = schema1Id;
                else if (k_PreviouslyUnassignedReusedIds.Contains(ruleId))
                    return null;
                sourceSchemaVersion = 1;
            }

            if (sourceSchemaVersion <= 1)
            {
                if (k_Schema1To2.TryGetValue(ruleId, out string currentId)) return currentId;
                if (k_Schema1UnassignedReusedIds.Contains(ruleId)) return null;
            }
            return ruleId;
        }

        static bool IsAliasAtSchema(int schemaVersion, string ruleId)
        {
            if (schemaVersion <= 0) return k_PreviousSchemaAliases.Contains(ruleId);
            if (schemaVersion == 1) return k_Schema1Aliases.Contains(ruleId);
            return false;
        }

        public static bool MigrateSettings(OptimizerSettings settings)
        {
            if (settings == null || settings.ruleIdSchemaVersion >= CurrentSchemaVersion) return false;
            int sourceSchemaVersion = settings.ruleIdSchemaVersion;
            settings.disabledRules = MapDistinct(settings.disabledRules, sourceSchemaVersion);
            MigrateSeverityOverrides(settings, sourceSchemaVersion);
            MigrateSuppressions(settings, sourceSchemaVersion);
            settings.ruleIdSchemaVersion = CurrentSchemaVersion;
            return true;
        }

        public static bool MigrateBaseline(OptimizationBaseline baseline)
        {
            if (baseline == null || baseline.Version > 3 || baseline.RuleIdSchemaVersion > CurrentSchemaVersion) return false;
            if (baseline.Version == 3 && baseline.RuleIdSchemaVersion >= CurrentSchemaVersion) return false;
            int sourceSchemaVersion = baseline.RuleIdSchemaVersion;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (baseline.Rules != null)
            {
                foreach (var rule in baseline.Rules)
                {
                    if (rule == null || string.IsNullOrEmpty(rule.RuleId)) continue;
                    string currentId = MapFromSchema(sourceSchemaVersion, rule.RuleId);
                    if (string.IsNullOrEmpty(currentId)) continue;
                    if (!counts.TryGetValue(currentId, out int oldCount) || rule.Count > oldCount)
                        counts[currentId] = rule.Count;
                }
            }
            baseline.Rules = new List<OptimizationBaselineRule>(counts.Count);
            foreach (var pair in counts)
                baseline.Rules.Add(new OptimizationBaselineRule { RuleId = pair.Key, Count = pair.Value });
            baseline.Rules.Sort((a, b) => string.CompareOrdinal(a.RuleId, b.RuleId));
            baseline.RuleIdSchemaVersion = CurrentSchemaVersion;
            baseline.Version = 3;
            return true;
        }

        static List<string> MapDistinct(List<string> source, int sourceSchemaVersion)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (source == null) return result;
            foreach (string oldId in source)
            {
                string currentId = MapFromSchema(sourceSchemaVersion, oldId);
                if (!string.IsNullOrEmpty(currentId) && seen.Add(currentId)) result.Add(currentId);
            }
            return result;
        }

        static void MigrateSeverityOverrides(OptimizerSettings settings, int sourceSchemaVersion)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            var canonicalSource = new Dictionary<string, bool>(StringComparer.Ordinal);
            int count = Math.Min(settings.severityOverrideIds?.Count ?? 0, settings.severityOverrideValues?.Count ?? 0);
            for (int i = 0; i < count; i++)
            {
                string oldId = settings.severityOverrideIds[i];
                if (string.IsNullOrEmpty(oldId)) continue;
                string currentId = MapFromSchema(sourceSchemaVersion, oldId);
                if (string.IsNullOrEmpty(currentId)) continue;
                bool fromCanonical = !IsAliasAtSchema(sourceSchemaVersion, oldId);
                if (!values.ContainsKey(currentId) || (fromCanonical && !canonicalSource[currentId]))
                {
                    values[currentId] = settings.severityOverrideValues[i];
                    canonicalSource[currentId] = fromCanonical;
                }
            }
            settings.severityOverrideIds = new List<string>(values.Keys);
            settings.severityOverrideIds.Sort(StringComparer.Ordinal);
            settings.severityOverrideValues = new List<int>(settings.severityOverrideIds.Count);
            foreach (string id in settings.severityOverrideIds) settings.severityOverrideValues.Add(values[id]);
        }

        static void MigrateSuppressions(OptimizerSettings settings, int sourceSchemaVersion)
        {
            var selected = new Dictionary<string, RuleSuppression>(StringComparer.Ordinal);
            var canonicalSource = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (settings.suppressions != null)
            {
                foreach (var suppression in settings.suppressions)
                {
                    if (suppression == null || string.IsNullOrEmpty(suppression.RuleId) || string.IsNullOrEmpty(suppression.AssetPath)) continue;
                    string oldId = suppression.RuleId;
                    string currentId = MapFromSchema(sourceSchemaVersion, oldId);
                    if (string.IsNullOrEmpty(currentId)) continue;
                    string key = RuleSuppression.KeyFor(currentId, suppression.AssetPath);
                    bool fromCanonical = !IsAliasAtSchema(sourceSchemaVersion, oldId);
                    if (!selected.TryGetValue(key, out var existing) || (fromCanonical && !canonicalSource[key]))
                    {
                        suppression.RuleId = currentId;
                        selected[key] = suppression;
                        canonicalSource[key] = fromCanonical;
                    }
                }
            }
            settings.suppressions = new List<RuleSuppression>(selected.Values);
            settings.suppressions.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        }
    }
}
