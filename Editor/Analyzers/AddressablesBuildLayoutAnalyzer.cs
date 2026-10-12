using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Reads the official Addressables BuildLayout report when Addressables is installed and a build
    /// layout exists. Reflection keeps the optimizer package compilable in projects without the
    /// Addressables package while still consuming Unity's richer dependency/duplicate evidence when it is present.
    /// </summary>
    public sealed class AddressablesBuildLayoutAnalyzer : IProjectAnalyzer
    {
        const long Mb = 1024L * 1024L;
        const float DefaultLowEfficiency = 0.25f;
        float m_Progress;

        public string DisplayName => "Addressables build layout";
        public float Progress => m_Progress;
        public bool Enabled(OptimizerSettings settings) => AddressablesUtility.IsInstalled;

        static AddressablesBuildLayoutAnalyzer()
        {
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR001", Title = "Addressables build contains duplicated implicit dependencies", Category = AssetCategory.Addressables,
                Why = "The official Addressables BuildLayout identifies objects/assets that were implicitly included more than once. Duplication inflates content delivery and can create unnecessary bundle residency/download cost.",
                Fix = "Review the Addressables duplicate-dependency Analyze rules and bundle/group topology. Do not blindly move assets between groups: shared dependencies and loading boundaries determine whether a deduplication actually improves the build.",
                DefaultSeverity = IssueSeverity.Warning, FixKind = FixKind.None, Confidence = OptimizationConfidence.Proven, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR002", Title = "Addressables bundle dependency is inefficient", Category = AssetCategory.Addressables,
                Why = "BuildLayout measures how much of a dependency bundle is actually used by its parent. A very small efficiency ratio can mean a large shared bundle is being pulled in for a small dependency, increasing download and load overhead.",
                Fix = "Review packing mode and shared dependencies. Split or regroup content only when the resulting loading boundaries better match actual runtime access patterns.",
                DefaultSeverity = IssueSeverity.Info, FixKind = FixKind.None, Confidence = OptimizationConfidence.Heuristic, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR004", Title = "Addressables bundle is oversized", Category = AssetCategory.Addressables,
                Why = "Very large bundles increase the amount of content that may need to be downloaded, loaded or invalidated together. The correct size depends on the game's loading boundaries, so this remains a review finding.",
                Fix = "Review packing mode and group boundaries; split a large bundle only when its content is not normally consumed together.",
                DefaultSeverity = IssueSeverity.Warning, FixKind = FixKind.None, Confidence = OptimizationConfidence.Heuristic, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR005", Title = "Addressables bundle is tiny or fragmented", Category = AssetCategory.Addressables,
                Why = "Very small bundles can increase catalog entries, request overhead and bundle-management complexity. Some bootstrap or deliberately isolated content is legitimately small, so the threshold is a review trigger.",
                Fix = "Review whether tiny content should be packed with the content that always loads with it.",
                DefaultSeverity = IssueSeverity.Info, FixKind = FixKind.None, Confidence = OptimizationConfidence.Heuristic, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR006", Title = "Addressables bundle has high dependency fan-out", Category = AssetCategory.Addressables,
                Why = "A bundle with many dependency links can become a loading hotspot: changing or loading one bundle may pull in a large dependency neighborhood. This is a topology signal, not proof that the layout is wrong.",
                Fix = "Review shared dependency placement and packing boundaries against actual runtime loading paths.",
                DefaultSeverity = IssueSeverity.Info, FixKind = FixKind.None, Confidence = OptimizationConfidence.Heuristic, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR007", Title = "Addressables Analyze reports duplicate/dependency findings", Category = AssetCategory.Addressables,
                Why = "Unity Addressables Analyze can identify duplicate bundle, Resources-to-Addressable, and Scene-to-Addressable dependency problems that a BuildLayout alone does not fully represent.",
                Fix = "Refresh Addressables Analyze evidence, inspect the reported rule results, and change group topology only when the runtime loading boundary supports it.",
                DefaultSeverity = IssueSeverity.Warning, FixKind = FixKind.None, Confidence = OptimizationConfidence.Proven, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR008", Title = "Addressables Analyze evidence unavailable or stale", Category = AssetCategory.Addressables,
                Why = "Analyze output is a snapshot of the Addressables configuration. Missing or stale evidence weakens dependency conclusions.",
                Fix = "Refresh Addressables Analyze evidence after meaningful Addressables configuration changes.",
                DefaultSeverity = IssueSeverity.Info, FixKind = FixKind.None, Confidence = OptimizationConfidence.Heuristic, DefaultFixRisk = FixRisk.Unspecified
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR009", Title = "Addressables BuildLayout provenance does not match the active build context", Category = AssetCategory.Addressables,
                Why = "A BuildLayout report is only directly comparable when its recorded Unity version and build target match the current analysis context. Missing provenance is also an evidence limitation.",
                Fix = "Generate a new Addressables BuildLayout for the active Unity version and active build target, then rescan.",
                DefaultSeverity = IssueSeverity.Warning, FixKind = FixKind.None, Confidence = OptimizationConfidence.Proven
            });
            RuleCatalog.Register(new RuleDef
            {
                Id = "ADR003", Title = "Addressables build layout is unavailable", Category = AssetCategory.Addressables,
                Why = "A normal Addressables build can be analyzed much more deeply when the build layout report is generated. Without it, bundle-level dependency evidence is incomplete.",
                Fix = "Enable the Addressables build layout/debug report for a representative content build, then rescan.",
                DefaultSeverity = IssueSeverity.Info, FixKind = FixKind.None, Confidence = OptimizationConfidence.Proven
            });
        }

        public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
        {
            m_Progress = 0f;
            var analyze = AddressablesAnalyzeEvidenceStore.LoadLatest();
            {
                var entry = NewEntry("Addressables/Analyze", "Addressables Analyze", "Build/Addressables");
                entry.Set("capturedAtUtc", analyze?.TimestampUtc ?? string.Empty);
                entry.Set("unityVersion", analyze?.UnityVersion ?? string.Empty);
                entry.Set("projectFingerprint", analyze?.ProjectFingerprint ?? string.Empty);
                entry.Set("resultCount", analyze?.ResultCount ?? 0);
                entry.Set("warningCount", analyze?.WarningCount ?? 0);
                entry.Set("errorCount", analyze?.ErrorCount ?? 0);
                entry.Set("infoCount", analyze?.InfoCount ?? 0);
                entry.Set("expectedRuleCount", analyze?.ExpectedRuleCount ?? 0);
                entry.Set("attemptedRuleCount", analyze?.AttemptedRuleCount ?? 0);
                entry.Set("successfulRuleCount", analyze?.SuccessfulRuleCount ?? 0);
                entry.Set("failedRuleCount", analyze?.FailedRuleCount ?? 0);
                entry.Set("unavailableRuleCount", analyze?.UnavailableRuleCount ?? 0);
                entry.Set("ruleScope", analyze?.RuleScope ?? string.Empty);
                if (analyze == null)
                {
                    using (RuleTiming.Begin("ADR008", entry.Path))
                        entry.AddIssue("ADR008", "Analyze evidence is unavailable", "Refresh Addressables Analyze evidence", 0, null, "No programmatic Addressables Analyze evidence snapshot exists for the current project.");
                }
                else
                {
                    using (RuleTiming.Begin("ADR007", entry.Path))
                        if (analyze.WarningCount > 0 || analyze.ErrorCount > 0)
                            entry.AddIssue("ADR007", analyze.ErrorCount + " error(s), " + analyze.WarningCount + " warning(s)",
                                "Review Addressables Analyze results", 0, null,
                                analyze.ResultNames.Count == 0 ? "Unity Analyze returned findings without readable names." : string.Join("; ", analyze.ResultNames.ToArray()));
                    using (RuleTiming.Begin("ADR008", entry.Path))
                        if (!analyze.IsFresh)
                            entry.AddIssue("ADR008", "Analyze evidence is stale", "Refresh Addressables Analyze evidence", 0, null, analyze.FreshnessReason);
                }
                entry.Finalise();
                yield return entry;
            }

            string path = FindLatestBuildLayout();
            if (string.IsNullOrEmpty(path))
            {
                var missing = NewEntry("Addressables/BuildLayout", "Addressables Build Layout", "Build/Addressables");
                missing.TypeLabel = "Addressables Build Layout";
                using (RuleTiming.Begin("ADR003", missing.Path))
                    missing.AddIssue("ADR003", "No Addressables BuildLayout report was found",
                        "Enable Addressables Debug Build Layout, generate a representative content build, then rescan. Reports are normally written to " + ProjectOptimizerConstants.AddressablesBuildReportsPath + ".")
                        ?.Note("Addressables is installed, but deep bundle/dependency evidence is unavailable without a BuildLayout report.", "Evidence");
                missing.Finalise();
                yield return missing;
                m_Progress = 1f;
                yield break;
            }

            object layout = OpenLayout(path);
            if (layout == null)
            {
                var failed = NewEntry("Addressables/BuildLayout", "Addressables Build Layout", "Build/Addressables");
                failed.TypeLabel = "Addressables Build Layout";
                using (RuleTiming.Begin("ADR003", failed.Path))
                    failed.AddIssue("ADR003", "The Addressables BuildLayout report could not be opened",
                        "Regenerate the report with a compatible Addressables package version, then rescan.")
                        ?.Note("The optimizer found a BuildLayout file but Unity's current Addressables editor API could not open it.", "Evidence");
                failed.Finalise();
                yield return failed;
                m_Progress = 1f;
                yield break;
            }
            try
            {
                var summary = NewEntry("Addressables/BuildLayout", "Addressables Build Layout", "Build/Addressables");
                summary.Category = AssetCategory.Addressables;
                summary.Extension = ".json";
                summary.TypeLabel = "Addressables Build Layout";
                summary.Set("file", path);
                summary.Set("unityVersion", GetString(layout, "UnityVersion", "Header.UnityVersion"));
                summary.Set("packageVersion", GetString(layout, "PackageVersion", "Header.PackageVersion"));
                summary.Set("buildTarget", GetString(layout, "BuildTarget", "Header.BuildTarget"));
                if (TryGetDouble(layout, "Duration", out var buildDuration)) summary.Set("buildDuration", buildDuration);
                summary.Set("buildResultHash", GetString(layout, "BuildResultHash", "Header.BuildResultHash"));
                string layoutUnityVersion = GetString(layout, "UnityVersion", "Header.UnityVersion");
                string layoutBuildTarget = GetString(layout, "BuildTarget", "Header.BuildTarget");
                string layoutPackageVersion = GetString(layout, "PackageVersion", "Header.PackageVersion");
                string activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
                string installedAddressablesVersion = GetInstalledAddressablesVersion();
                bool unityMatches = string.IsNullOrEmpty(layoutUnityVersion) || string.Equals(layoutUnityVersion, Application.unityVersion, StringComparison.OrdinalIgnoreCase);
                bool targetMatches = string.IsNullOrEmpty(layoutBuildTarget) || string.Equals(layoutBuildTarget, activeBuildTarget, StringComparison.OrdinalIgnoreCase);
                bool layoutHasProvenance = !string.IsNullOrEmpty(layoutUnityVersion) && !string.IsNullOrEmpty(layoutBuildTarget) && !string.IsNullOrEmpty(layoutPackageVersion);
                bool packageMatches = string.IsNullOrEmpty(installedAddressablesVersion) || string.IsNullOrEmpty(layoutPackageVersion) || string.Equals(layoutPackageVersion, installedAddressablesVersion, StringComparison.OrdinalIgnoreCase);
                summary.Set("provenanceComplete", layoutHasProvenance ? 1L : 0L);
                summary.Set("unityMatches", unityMatches ? 1L : 0L);
                summary.Set("targetMatches", targetMatches ? 1L : 0L);
                summary.Set("addressablesPackageMatches", packageMatches ? 1L : 0L);
                summary.Set("fileModifiedUtc", File.GetLastWriteTimeUtc(path).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                bool layoutTrusted = unityMatches && targetMatches && packageMatches && layoutHasProvenance;
                using (RuleTiming.Begin("ADR009", summary.Path))
                {
                    if (!layoutTrusted)
                    {
                        string reason = !unityMatches ? "Unity version mismatch" : !targetMatches ? "build target mismatch" : !packageMatches ? "Addressables package version mismatch" : "BuildLayout provenance fields are incomplete";
                        summary.AddIssue("ADR009", reason, "Generate a new Addressables BuildLayout for the current build context", 0, null,
                            "Layout Unity=" + (layoutUnityVersion.Length == 0 ? "Unknown" : layoutUnityVersion) + ", target=" + (layoutBuildTarget.Length == 0 ? "Unknown" : layoutBuildTarget) + ", Addressables=" + (layoutPackageVersion.Length == 0 ? "Unknown" : layoutPackageVersion) + "; current Unity=" + Application.unityVersion + ", target=" + activeBuildTarget + ", Addressables=" + (installedAddressablesVersion.Length == 0 ? "Unknown" : installedAddressablesVersion) + ". Bundle-level findings are suppressed until provenance matches.");
                    }
                }

                if (!layoutTrusted)
                {
                    summary.Finalise();
                    yield return summary;
                    yield break;
                }

                var duplicated = Enumerate(GetMember(layout, "DuplicatedAssets"));
                int duplicateCount = duplicated.Count();
                summary.Set("duplicates", (long)duplicateCount);
                using (RuleTiming.Begin("ADR001", summary.Path))
                    if (duplicateCount > 0)
                        summary.AddIssue("ADR001", duplicateCount + " duplicated implicit dependency record(s)", "Review duplicate bundle dependencies").Note(
                            "Generated from the official Addressables BuildLayout DuplicatedAssets collection.", "Evidence");

                var bundles = new List<object>();
                foreach (object group in Enumerate(GetMember(layout, "Groups")))
                    bundles.AddRange(Enumerate(GetMember(group, "Bundles")));
                foreach (object builtIn in Enumerate(GetMember(layout, "BuiltInBundles"))) bundles.Add(builtIn);

                summary.Set("bundles", (long)bundles.Count);
                summary.Finalise();
                yield return summary;

                for (int i = 0; i < bundles.Count; i++)
                {
                    object bundle = bundles[i];
                    string name = GetString(bundle, "Name", "InternalName");
                    if (string.IsNullOrEmpty(name)) name = "Bundle " + i;
                    var entry = NewEntry("Addressables/Bundles/" + SanitizePath(name), name, "Build/Addressables/Bundles");
                    entry.Category = AssetCategory.Addressables;
                    entry.Extension = ".bundle";
                    entry.TypeLabel = "Addressables Bundle";
                    long fileSize;
                    long dependencySize;
                    long expandedDependencySize;
                    long uncompressedSize;
                    long assetCountLong;
                    bool hasFileSize = TryGetLong(bundle, "FileSize", out fileSize);
                    bool hasDependencySize = TryGetLong(bundle, "DependencyFileSize", out dependencySize);
                    bool hasExpandedDependencySize = TryGetLong(bundle, "ExpandedDependencyFileSize", out expandedDependencySize);
                    bool hasUncompressedSize = TryGetLong(bundle, "UncompressedFileSize", out uncompressedSize);
                    bool hasAssetCount = TryGetLong(bundle, "AssetCount", out assetCountLong);
                    int assetCount = hasAssetCount ? (int)Math.Max(0L, assetCountLong) : 0;
                    entry.FileSize = hasFileSize ? fileSize : -1L;
                    if (hasFileSize) entry.Set("fileSize", fileSize);
                    if (hasDependencySize) entry.Set("dependencySize", dependencySize);
                    if (hasExpandedDependencySize) entry.Set("expandedDependencySize", expandedDependencySize);
                    if (hasUncompressedSize) entry.Set("uncompressedSize", uncompressedSize);
                    if (hasAssetCount) entry.Set("assetCount", assetCountLong);
                    entry.Set("fileSizeAvailable", hasFileSize ? 1L : 0L);
                    entry.Set("dependencySizeAvailable", hasDependencySize ? 1L : 0L);
                    entry.Set("expandedDependencySizeAvailable", hasExpandedDependencySize ? 1L : 0L);
                    entry.Set("uncompressedSizeAvailable", hasUncompressedSize ? 1L : 0L);
                    entry.Set("assetCountAvailable", hasAssetCount ? 1L : 0L);

                    int inefficient = 0;
                    float worstEfficiency = 1f;
                    bool efficiencyEvidenceAvailable = false;
                    string worstDependency = string.Empty;
                    foreach (object dep in Enumerate(GetMember(bundle, "BundleDependencies")))
                    {
                        double efficiency;
                        double expandedEfficiency;
                        bool hasEfficiency = TryGetDouble(dep, "Efficiency", out efficiency);
                        bool hasExpandedEfficiency = TryGetDouble(dep, "ExpandedEfficiency", out expandedEfficiency);
                        float localWorst = 1f;
                        bool localEvidence = false;
                        if (hasEfficiency && IsValidEfficiency(efficiency)) { localWorst = Mathf.Clamp01((float)efficiency); localEvidence = true; }
                        if (hasExpandedEfficiency && IsValidEfficiency(expandedEfficiency))
                        {
                            float expanded = Mathf.Clamp01((float)expandedEfficiency);
                            localWorst = localEvidence ? Mathf.Min(localWorst, expanded) : expanded;
                            localEvidence = true;
                        }
                        if (!localEvidence) continue;
                        efficiencyEvidenceAvailable = true;
                        if (localWorst < worstEfficiency)
                        {
                            worstEfficiency = localWorst;
                            worstDependency = GetString(GetMember(dep, "DependencyBundle"), "Name", "InternalName");
                        }
                        if (localWorst < GetEfficiencyThreshold(ctx.Settings)) inefficient++;
                    }
                    var dependencyList = Enumerate(GetMember(bundle, "BundleDependencies")).ToList();
                    entry.Set("dependencyCount", (long)dependencyList.Count);
                    entry.Set("efficiencyEvidenceAvailable", efficiencyEvidenceAvailable ? 1L : 0L);
                    if (efficiencyEvidenceAvailable) entry.Set("worstEfficiency", worstEfficiency * 100f);
                    entry.Set("worstDependency", worstDependency);

                    int largeThresholdMb = ctx.Settings != null ? Math.Max(1, ctx.Settings.addressablesLargeBundleMb) : 100;
                    int tinyThresholdKb = ctx.Settings != null ? Math.Max(1, ctx.Settings.addressablesTinyBundleKb) : 32;
                    int fanoutThreshold = ctx.Settings != null ? Math.Max(1, ctx.Settings.addressablesDependencyFanoutWarn) : 8;
                    long largeBytes = (long)largeThresholdMb * Mb;
                    long tinyBytes = (long)tinyThresholdKb * 1024L;

                    using (RuleTiming.Begin("ADR002", entry.Path))
                        if (efficiencyEvidenceAvailable && inefficient > 0)
                            entry.AddIssue("ADR002", inefficient + " low-efficiency bundle dependency link(s)",
                                "Review bundle grouping and shared dependency boundaries", 0, null,
                                "Worst measured dependency efficiency: " + (worstEfficiency * 100f).ToString("0.#") + "%" +
                                (string.IsNullOrEmpty(worstDependency) ? string.Empty : " → " + worstDependency));

                    using (RuleTiming.Begin("ADR004", entry.Path))
                        if (hasFileSize && fileSize >= largeBytes)
                            entry.AddIssue("ADR004", DisplayUtility.FormatBytes(fileSize), "Review bundle grouping below " + DisplayUtility.FormatBytes(largeBytes), 0, null,
                                "Large bundles are not inherently wrong; split them only when their content is not normally consumed together.");

                    using (RuleTiming.Begin("ADR005", entry.Path))
                        if (hasFileSize && hasAssetCount && fileSize > 0 && fileSize <= tinyBytes && assetCount <= 2)
                            entry.AddIssue("ADR005", DisplayUtility.FormatBytes(fileSize), "Review whether this tiny bundle can be packed with related content", 0, null,
                                "Tiny bundles can increase bundle/request fragmentation. Bootstrap, patch or deliberately isolated content can justify this layout.");

                    using (RuleTiming.Begin("ADR006", entry.Path))
                        if (dependencyList.Count >= fanoutThreshold)
                            entry.AddIssue("ADR006", dependencyList.Count + " dependencies", "Review dependency fan-out below " + fanoutThreshold, 0, null,
                                "High fan-out is a topology signal. Check whether shared dependencies match actual runtime loading boundaries before regrouping.");

                    entry.Finalise();
                    yield return entry;
                    m_Progress = 0.10f + 0.90f * ((i + 1f) / Math.Max(1, bundles.Count));
                }
            }
            finally
            {
                CloseLayout(layout);
            }
            m_Progress = 1f;
        }

        static float GetEfficiencyThreshold(OptimizerSettings settings)
        {
            float value = settings != null ? settings.addressablesDependencyEfficiencyReview : DefaultLowEfficiency;
            return value <= 0f ? DefaultLowEfficiency : Mathf.Clamp01(value);
        }

        AssetEntry NewEntry(string path, string name, string directory) => new AssetEntry
        {
            Path = path, Name = name, Directory = directory, Category = AssetCategory.Addressables,
            TypeLabel = "Addressables", FileSize = 0L, RuntimeSize = -1
        };

        static bool HasConfiguredAddressables()
        {
            try
            {
                Type settingsType = FindType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");
                if (settingsType == null) return false;
                var property = settingsType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
                object settings = property != null ? property.GetValue(null, null) : null;
                if (settings == null) return false;

                object groups = GetMember(settings, "groups");
                if (groups is ICollection collection) return collection.Count > 0;
                if (groups is IEnumerable enumerable)
                {
                    foreach (object _ in enumerable) return true;
                }
                return false;
            }
            catch { return false; }
        }

        static string FindLatestBuildLayout()
        {
            try
            {
                // Addressables' documented Debug Build Layout output is stored under
                // Library/com.unity.addressables/buildReports. Keep the historical locations as
                // fallbacks because projects upgraded from older Addressables versions can retain
                // legacy reports there. Never guess a report outside the project Library.
                string[] roots =
                {
                    ProjectOptimizerConstants.AddressablesBuildReportsPath,
                    ProjectOptimizerConstants.AddressablesBuildReportsAlternatePath,
                    ProjectOptimizerConstants.AddressablesBuildLayoutPath
                };
                var candidates = new List<string>();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (!Directory.Exists(roots[i])) continue;
                    var files = Directory.GetFiles(roots[i], "*.json", SearchOption.AllDirectories);
                    for (int j = 0; j < files.Length; j++)
                    {
                        string name = Path.GetFileName(files[j]);
                        if (name.IndexOf("buildlayout", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("layout", StringComparison.OrdinalIgnoreCase) >= 0)
                            candidates.Add(files[j]);
                    }
                }
                if (candidates.Count == 0) return string.Empty;
                candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                candidates.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                return candidates[0];
            }
            catch { return string.Empty; }
        }

        static object OpenLayout(string path)
        {
            try
            {
                Type type = FindType("UnityEditor.AddressableAssets.Build.Layout.BuildLayout");
                if (type == null) return null;
                var method = type.GetMethod("Open", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(bool), typeof(bool) }, null);
                return method?.Invoke(null, new object[] { path, true, true });
            }
            catch (Exception e)
            {
                Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + " Addressables BuildLayout could not be opened: " + e.Message);
                return null;
            }
        }

        static void CloseLayout(object layout)
        {
            try { layout?.GetType().GetMethod("Close", BindingFlags.Public | BindingFlags.Instance)?.Invoke(layout, null); }
            catch { }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var type = assembly.GetType(fullName, false); if (type != null) return type; } catch { }
            }
            return null;
        }

        static object GetMember(object instance, string path)
        {
            if (instance == null || string.IsNullOrEmpty(path)) return null;
            object current = instance;
            foreach (string part in path.Split('.'))
            {
                if (current == null) return null;
                var type = current.GetType();
                var field = type.GetField(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) { current = field.GetValue(current); continue; }
                var property = type.GetProperty(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null) return null;
                current = property.GetValue(current, null);
            }
            return current;
        }

        static IEnumerable<object> Enumerate(object value)
        {
            if (value is IEnumerable enumerable)
                foreach (object item in enumerable) if (item != null) yield return item;
        }

        static string GetString(object instance, params string[] names)
        {
            foreach (string name in names)
            {
                object value = GetMember(instance, name);
                if (value != null) return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            return string.Empty;
        }

        static bool TryGetLong(object instance, string name, out long value)
        {
            value = 0L;
            object raw = GetMember(instance, name);
            if (raw == null) return false;
            try
            {
                value = Math.Max(0L, Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        static bool TryGetDouble(object instance, string name, out double value)
        {
            value = 0.0;
            object raw = GetMember(instance, name);
            if (raw == null) return false;
            try
            {
                value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        static bool IsValidEfficiency(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0.0 && value <= 1.0;

        static string GetInstalledAddressablesVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(ProjectOptimizerConstants.AddressablesPackageId);
                return info?.version ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        static string SanitizePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Bundle";
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value.Replace('/', '_').Replace('\\', '_');
        }
    }
}
