using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Deterministic self-audit for invariants which are otherwise easy to break while adding rules,
    /// fixes, table definitions, or persisted schemas. This intentionally runs inside Unity so it can
    /// inspect the actual imported package rather than a second copy of the source.
    /// </summary>
    public static class ProjectOptimizerIntegrityAudit
    {
        public static void RunAndLog() => Log(Run());

        public static List<string> Run()
        {
            var errors = new List<string>();
            RuleBootstrap.EnsureBuiltInRules();
            CheckRules(errors);
            CheckRuleAliases(errors);
            CheckRuleContracts(errors);
            CheckFixEngineBackupCoverage(errors);
            CheckOptionContracts(errors);
            CheckManifest(errors);
            CheckRuntimeSchema(errors);
            CheckEvidencePipeline(errors);
            CheckUiTableSchema(errors);
            CheckSourceMutationBackupContracts(errors);
            CheckSingleMenuEntry(errors);
            return errors;
        }

        static void Log(List<string> errors)
        {
            if (errors.Count == 0) { Debug.Log(ProjectOptimizerConstants.LogPrefix + " Integrity audit passed."); return; }
            Debug.LogError(ProjectOptimizerConstants.LogPrefix + " Integrity audit found " + errors.Count + " error(s):\n- " + string.Join("\n- ", errors));
        }


        static string PackageRoot() => ProjectOptimizerConstants.PackageRoot;

        static void CheckSingleMenuEntry(List<string> errors)
        {
            string windowPath = PackageRoot() + "/Editor/UI/ProjectOptimizerWindow.cs";
            if (!File.Exists(windowPath)) { errors.Add("ProjectOptimizerWindow.cs is missing."); return; }
            string source = File.ReadAllText(windowPath);
            int count = Regex.Matches(source, @"(?m)^\s*\[MenuItem\s*\(").Count;
            if (count != 1) errors.Add("ProjectOptimizerWindow.cs must contain exactly one MenuItem registration; found " + count + ".");
            if (source.IndexOf("[MenuItem(ProjectOptimizerConstants.MenuPath)]", StringComparison.Ordinal) < 0)
                errors.Add("The sole Project Optimizer menu item must use ProjectOptimizerConstants.MenuPath.");
            string[] files = Directory.Exists(PackageRoot()) ? Directory.GetFiles(PackageRoot(), "*.cs", SearchOption.AllDirectories) : Array.Empty<string>();
            int total = 0;
            for (int i = 0; i < files.Length; i++) total += Regex.Matches(File.ReadAllText(files[i]), @"(?m)^\s*\[MenuItem\s*\(").Count;
            if (total != 1) errors.Add("Project Optimizer package must contain exactly one MenuItem registration; found " + total + ".");
        }

        static void CheckRules(List<string> errors)
        {
            var duplicateIds = RuleCatalog.All
                .Where(r => r != null)
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            if (duplicateIds.Length > 0) errors.Add("Duplicate rule id(s): " + string.Join(", ", duplicateIds));
            if (RuleCatalog.All.Any(r => r == null)) errors.Add("Rule catalogue contains a null rule.");
            if (RuleCatalog.All.Count == 0) errors.Add("Rule catalogue is empty after built-in bootstrap.");
        }


        static void CheckRuleAliases(List<string> errors)
        {
            var aliases = RuleCatalog.GetAliasMappings();
            foreach (var pair in aliases)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    errors.Add("Rule catalogue contains an alias with an empty ID or target.");
                    continue;
                }
                if (!RuleCatalog.Exists(pair.Value))
                {
                    errors.Add("Rule alias " + pair.Key + " targets an unknown rule " + pair.Value + ".");
                    continue;
                }
                if (RuleCatalog.IsAlias(pair.Value))
                {
                    errors.Add("Rule aliases must target an active canonical rule, not form chains: " + pair.Key + " -> " + pair.Value + ".");
                    continue;
                }
                if (!string.Equals(RuleCatalog.Get(pair.Key)?.Id, pair.Value, StringComparison.Ordinal))
                    errors.Add("Rule alias " + pair.Key + " does not resolve to its declared canonical rule " + pair.Value + ".");
                if (RuleCatalog.All.Any(rule => string.Equals(rule.Id, pair.Key, StringComparison.Ordinal)))
                    errors.Add("Rule alias " + pair.Key + " is also present as an active catalogue entry.");
            }
        }

        static void CheckRuleContracts(List<string> errors)
        {
            foreach (var rule in RuleCatalog.All)
            {
                if (rule == null) continue;
                if (rule.FixKind == FixKind.Verified)
                {
                    if (rule.Confidence < RuleCatalog.MinimumForAutomaticFix)
                        errors.Add(rule.Id + ": Verified confidence is below the automatic-fix floor.");
                    if (rule.RecommendationConfidence < RuleCatalog.MinimumForAutomaticFix)
                        errors.Add(rule.Id + ": Verified recommendation confidence is below the automatic-fix floor.");
                    if (rule.Category == AssetCategory.ProjectSetting && !rule.DefaultBackupRequired)
                        errors.Add(rule.Id + ": project-setting Verified action does not require backup.");
                    if (rule.DefaultFixRisk >= FixRisk.High && !rule.DefaultBackupRequired)
                        errors.Add(rule.Id + ": High/Destructive Verified action does not require backup.");
                }
                if (rule.DefaultFixRisk == FixRisk.Destructive && !rule.DefaultBackupRequired)
                    errors.Add(rule.Id + ": destructive action does not require backup.");
            }
        }

        static void CheckFixEngineBackupCoverage(List<string> errors)
        {
            string path = AssetDatabase.FindAssets("FixEngine t:Script")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("FixEngine.cs", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(path)) { errors.Add("FixEngine.cs could not be located."); return; }
            string source = File.ReadAllText(path);
            string[] required = { ".mat", ".anim", ".prefab", ".asset", ".controller", ".unity", ".shadergraph", ".shader", ".shadervariants", ".spriteatlas", ".spriteatlasv2" };
            for (int i = 0; i < required.Length; i++)
                if (source.IndexOf("\"" + required[i] + "\"", StringComparison.OrdinalIgnoreCase) < 0)
                    errors.Add("Missing backup extension in FixEngine: " + required[i]);
        }

        static void CheckOptionContracts(List<string> errors)
        {
            // RuleDef stores declarations rather than concrete FixOption instances. Concrete option
            // contracts are validated when issues are created; this source audit protects the centralized
            // execution gate from accidental bypasses.
            string modelPath = PackageRoot() + "/Editor/Core/Domain/Model.cs";
            if (!File.Exists(modelPath)) { errors.Add("Model.cs could not be located for option-contract audit."); return; }
            string source = File.ReadAllText(modelPath);
            if (source.IndexOf("RequiresPostconditionVerifier(risk) ? option.Verify", StringComparison.Ordinal) < 0)
                errors.Add("High-risk option verification is not enforced through the centralized FixContract.");
            if (source.IndexOf("RequiresPostconditionVerifier(risk) && option.Verify == null", StringComparison.Ordinal) < 0)
                errors.Add("ReviewFixable does not require an option-specific verifier for high-risk options.");
            string fixEnginePath = PackageRoot() + "/Editor/Core/Fixes/FixEngine.cs";
            if (!File.Exists(fixEnginePath) || File.ReadAllText(fixEnginePath).IndexOf("FixContract.EffectiveVerify(Issue, OptionOverride)", StringComparison.Ordinal) < 0)
                errors.Add("FixEngine request path does not resolve verification through FixContract.");
            string reviewBatchPath = PackageRoot() + "/Editor/Core/Fixes/ReviewBatch.cs";
            if (!File.Exists(reviewBatchPath) || File.ReadAllText(reviewBatchPath).IndexOf("FixContract.EffectiveVerify(issue, option)", StringComparison.Ordinal) < 0)
                errors.Add("Review batch eligibility does not use the centralized verification contract.");
            string persistencePath = PackageRoot() + "/Editor/Core/Persistence/ScanResultStore.cs";
            if (!File.Exists(persistencePath)) errors.Add("ScanResultStore.cs could not be located for verifier persistence audit.");
            else
            {
                string persistence = File.ReadAllText(persistencePath);
                if (persistence.IndexOf("const int k_Version = 12", StringComparison.Ordinal) < 0) errors.Add("Persisted scan snapshot schema does not reflect executable/verifier presence fields.");
                if (persistence.IndexOf("public bool HasVerifier", StringComparison.Ordinal) < 0) errors.Add("Persisted fix metadata does not retain verifier presence.");
                if (persistence.IndexOf("public bool HasApply", StringComparison.Ordinal) < 0) errors.Add("Persisted fix metadata does not retain executable-action presence.");
                if (persistence.IndexOf("public string RuleCatalogSignature", StringComparison.Ordinal) < 0 || persistence.IndexOf("public string AnalyzerSignature", StringComparison.Ordinal) < 0 || persistence.IndexOf("public string FixContractSignature", StringComparison.Ordinal) < 0) errors.Add("Persisted findings do not retain semantic rule/analyzer/fix signatures.");
                if (persistence.IndexOf("CrossAssetEvidenceContentIdentity", StringComparison.Ordinal) < 0) errors.Add("Persisted scan does not retain cross-asset content identity.");
                if (persistence.IndexOf("AssetEvidencePath", StringComparison.Ordinal) < 0) errors.Add("Persisted findings do not retain the asset path needed for execution-time evidence validation.");
                if (persistence.IndexOf("if (savedOption.HasApply)", StringComparison.Ordinal) < 0) errors.Add("Restored fix options can become executable when the original option had no Apply delegate.");
                if (persistence.IndexOf("if (savedOption.HasVerifier)", StringComparison.Ordinal) < 0) errors.Add("Restored fix options do not preserve verifier presence.");
                if (persistence.IndexOf("return candidate.Verify != null && candidate.Verify(context)", StringComparison.Ordinal) < 0) errors.Add("Restored verifier path can still treat a missing verifier as a successful check.");
            }
        }

        static void CheckManifest(List<string> errors)
        {
            string path = PackageRoot() + "/package.json";
            if (!File.Exists(path)) { errors.Add("Package manifest is missing: " + path); return; }
            string json = File.ReadAllText(path);
            try
            {
                var manifest = UnityEditor.PackageManager.PackageInfo.FindForPackageName(ProjectOptimizerConstants.PackageId);
                string expected = manifest?.version;
                if (string.IsNullOrEmpty(expected))
                {
                    string marker = "\"version\":";
                    int index = json.IndexOf(marker, StringComparison.Ordinal);
                    if (index < 0) { errors.Add("Package manifest has no version field."); return; }
                    int start = json.IndexOf('\"', index + marker.Length);
                    int end = start >= 0 ? json.IndexOf('\"', start + 1) : -1;
                    expected = start >= 0 && end > start ? json.Substring(start + 1, end - start - 1) : string.Empty;
                }
                if (string.IsNullOrEmpty(expected)) errors.Add("Package manifest version could not be resolved from package metadata.");
                else
                {
                    int versionIndex = json.IndexOf("\"version\":", StringComparison.Ordinal);
                    int versionStart = versionIndex >= 0 ? json.IndexOf('\"', versionIndex + 10) : -1;
                    int versionEnd = versionStart >= 0 ? json.IndexOf('\"', versionStart + 1) : -1;
                    string declared = versionStart >= 0 && versionEnd > versionStart ? json.Substring(versionStart + 1, versionEnd - versionStart - 1) : string.Empty;
                    if (string.IsNullOrEmpty(declared) || !string.Equals(declared, expected, StringComparison.OrdinalIgnoreCase))
                        errors.Add("Package metadata version does not match package.json (declared=" + declared + ", resolved=" + expected + ").");
                }
                string schemaPath = PackageRoot() + "/Editor/Core/Settings/OptimizerVersion.cs";
                if (File.Exists(schemaPath) && File.ReadAllText(schemaPath).IndexOf("Schema =", StringComparison.Ordinal) < 0)
                    errors.Add("OptimizerVersion schema declaration is missing.");
            }
            catch (Exception e)
            {
                errors.Add("Package metadata query failed: " + e.Message);
            }
        }

        static void RequireManifestValue(string json, string field, string expected, List<string> errors)
        {
            string marker = "\"" + field + "\":";
            int index = json.IndexOf(marker, StringComparison.Ordinal);
            int start = index >= 0 ? json.IndexOf('\"', index + marker.Length) : -1;
            int end = start >= 0 ? json.IndexOf('\"', start + 1) : -1;
            string actual = start >= 0 && end > start ? json.Substring(start + 1, end - start - 1) : string.Empty;
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                errors.Add("package.json " + field + " must be '" + expected + "' (found '" + actual + "').");
        }

        static void CheckRuntimeSchema(List<string> errors)
        {
            string runtimePath = PackageRoot() + "/Runtime/RuntimeMetricTypes.cs";
            if (!File.Exists(runtimePath)) { errors.Add("RuntimeMetricTypes.cs is missing."); return; }
            if (File.ReadAllText(runtimePath).IndexOf("public int version = 6", StringComparison.Ordinal) < 0)
                errors.Add("Runtime report schema is not version 6.");
            string editorPath = PackageRoot() + "/Editor/Core/Profiling/RuntimeEvidenceStore.cs";
            if (!File.Exists(editorPath)) { errors.Add("RuntimeEvidenceStore.cs is missing."); return; }
            string editor = File.ReadAllText(editorPath);
            if (editor.IndexOf("const int k_Version = 6", StringComparison.Ordinal) < 0)
                errors.Add("Editor runtime evidence store schema is not version 6.");
            string[] required = { "BuildProfile", "ContentIdentity", "CaptureCharacteristics", "EvidenceState", "BuildId", "CommitId" };
            for (int i = 0; i < required.Length; i++)
                if (editor.IndexOf(required[i], StringComparison.Ordinal) < 0) errors.Add("Runtime evidence is missing identity/state field: " + required[i]);
            if (editor.IndexOf("IsComplete", StringComparison.Ordinal) < 0) errors.Add("Runtime evidence store does not preserve capture completeness.");
            string importerPath = PackageRoot() + "/Editor/Core/Profiling/RuntimeReportImporter.cs";
            if (!File.Exists(importerPath) || File.ReadAllText(importerPath).IndexOf("captureCompleted", StringComparison.Ordinal) < 0)
                errors.Add("Runtime report importer does not preserve capture completion state.");
        }

        static void CheckUiTableSchema(List<string> errors)
        {
            string path = PackageRoot() + "/Editor/UI/ProjectOptimizerWindow.cs";
            if (!File.Exists(path)) { errors.Add("ProjectOptimizerWindow.cs is missing."); return; }
            string text = File.ReadAllText(path);
            AssertSchema(text, "packages", errors);
            AssertSchema(text, "project", errors);
            AssertSchema(text, "buildsteps", errors);
            AssertSchema(text, "buildfiles", errors);
            AssertSchema(text, "build", errors);
            AssertSchema(text, "materials", errors);
            AssertSchema(text, "scripts", errors);
        }

        static void AssertSchema(string source, string id, List<string> errors)
        {
            int index = source.IndexOf("Id = \"" + id + "\"", StringComparison.Ordinal);
            if (index < 0) { errors.Add("Missing UI table schema: " + id); return; }
            int end = source.IndexOf("},", index, StringComparison.Ordinal);
            if (end < 0) end = Math.Min(source.Length, index + 3000);
            string block = source.Substring(index, end - index);
            if (block.IndexOf("AssetMetrics = AssetMetricColumns.None", StringComparison.Ordinal) < 0)
                errors.Add(id + " table unexpectedly uses generic asset metrics.");
        }

        static void CheckSourceMutationBackupContracts(List<string> errors)
        {
            string[] analyzerFiles = Directory.Exists(PackageRoot() + "/Editor/Analyzers")
                ? Directory.GetFiles(PackageRoot() + "/Editor/Analyzers", "*.cs", SearchOption.AllDirectories)
                : Array.Empty<string>();
            for (int i = 0; i < analyzerFiles.Length; i++)
            {
                string source = File.ReadAllText(analyzerFiles[i]);
                bool directFileMutation = source.IndexOf("File.WriteAllText", StringComparison.Ordinal) >= 0
                    || source.IndexOf("File.WriteAllBytes", StringComparison.Ordinal) >= 0
                    || source.IndexOf("AssetDatabase.DeleteAsset", StringComparison.Ordinal) >= 0
                    || source.IndexOf("AssetDatabase.MoveAssetToTrash", StringComparison.Ordinal) >= 0;
                bool extractionMutation = source.IndexOf("AssetDatabase.ExtractAsset", StringComparison.Ordinal) >= 0
                    || source.IndexOf("AssetDatabase.CreateAsset", StringComparison.Ordinal) >= 0;
                if (!directFileMutation && !extractionMutation) continue;

                // Normal importer/project fixes are centrally snapshotted by FixEngine before the
                // analyzer callback executes. Direct filesystem/reference migrations are different: they
                // may touch paths outside the primary work item, so the analyzer must explicitly request
                // each extra backup. Created assets must likewise be registered for rollback.
                bool hasExplicitBoundary = source.IndexOf("RequireBackup(", StringComparison.Ordinal) >= 0
                    || source.IndexOf("tryBackup", StringComparison.OrdinalIgnoreCase) >= 0
                    || source.IndexOf("RegisterCreatedAsset(", StringComparison.Ordinal) >= 0;
                if (!hasExplicitBoundary)
                    errors.Add("Potentially direct file-mutating analyzer has no explicit backup/created-asset boundary: " + analyzerFiles[i].Replace('\\', '/'));
            }
        }
        static void CheckEvidencePipeline(List<string> errors)
        {
            string buildPath = PackageRoot() + "/Editor/Core/Build/BuildReportStore.cs";
            if (!File.Exists(buildPath)) errors.Add("BuildReportStore.cs is missing.");
            else
            {
                string build = File.ReadAllText(buildPath);
                if (build.IndexOf("const int k_Version = 6", StringComparison.Ordinal) < 0) errors.Add("BuildReport persistence schema was not bumped for the evidence-quality fields.");
                if (build.IndexOf("commitId", StringComparison.Ordinal) < 0) errors.Add("BuildReport persistence does not retain optional source commit identity.");
                if (build.IndexOf("public string evidenceQualityReason", StringComparison.Ordinal) < 0) errors.Add("BuildReport evidence quality reason is not persisted separately from the quality value.");
                if (build.IndexOf("BuildReport.GetLatestReport()", StringComparison.Ordinal) < 0) errors.Add("BuildReport evidence does not compare against Unity's latest report.");
                if (build.IndexOf("latestGuid", StringComparison.Ordinal) < 0) errors.Add("BuildReport evidence does not compare build identity.");
                if (build.IndexOf("ProfilesCompatibleWithoutProjectFallback", StringComparison.Ordinal) < 0) errors.Add("BuildReport compatibility does not require sufficient project/profile identity.");
                if (build.IndexOf("buildContextConfirmed", StringComparison.Ordinal) < 0) errors.Add("BuildReport analyzer does not gate findings on confirmed current context.");
            }

            string addressablesPath = PackageRoot() + "/Editor/Core/Scanning/AddressablesAnalyzeEvidenceStore.cs";
            if (!File.Exists(addressablesPath)) errors.Add("AddressablesAnalyzeEvidenceStore.cs is missing.");
            else
            {
                string addressables = File.ReadAllText(addressablesPath);
                if (addressables.IndexOf("SuccessfulRuleCount", StringComparison.Ordinal) < 0 || addressables.IndexOf("UnavailableRuleCount", StringComparison.Ordinal) < 0)
                    errors.Add("Addressables Analyze evidence does not distinguish successful, failed and unavailable rules.");
                if (addressables.IndexOf("EvidenceState", StringComparison.Ordinal) < 0) errors.Add("Addressables Analyze evidence does not expose an explicit evidence state.");
                if (addressables.IndexOf("ContentIdentity", StringComparison.Ordinal) < 0) errors.Add("Addressables Analyze evidence does not persist content/dependency identity.");
                if (addressables.IndexOf("RuleScope", StringComparison.Ordinal) < 0) errors.Add("Addressables Analyze evidence does not preserve the analyzed rule scope.");
                if (addressables.IndexOf("Stopwatch", StringComparison.Ordinal) < 0) errors.Add("Addressables Analyze evidence does not record per-rule timing.");
            }

            string layoutPath = PackageRoot() + "/Editor/Analyzers/AddressablesBuildLayoutAnalyzer.cs";
            if (!File.Exists(layoutPath)) errors.Add("AddressablesBuildLayoutAnalyzer.cs is missing.");
            else
            {
                string layout = File.ReadAllText(layoutPath);
                if (layout.IndexOf("efficiencyEvidenceAvailable", StringComparison.Ordinal) < 0 || layout.IndexOf("layoutTrusted", StringComparison.Ordinal) < 0)
                    errors.Add("Addressables BuildLayout analyzer does not guard unavailable/stale metrics before emitting optimization findings.");
            }
        }

    }
}
