using System;
using System.IO;
using UnityEditor.PackageManager;

namespace ApexForge.ProjectOptimizer
{
    /// <summary>
    /// Single source of truth for Project Optimizer's product identity and package-owned paths.
    /// User/project data and rule-specific semantics remain in their owning systems; this class only
    /// centralizes values whose drift would create broken references or inconsistent package identity.
    /// </summary>
    public static class ProjectOptimizerConstants
    {
        public const string PackageId = "com.apexforge.projectoptimizer";
        public const string PackageDisplayName = "ProjectOptimizer";
        public const string ProductName = "Project Optimizer";
        public const string WindowTitle = "Optimizer";
        public const string LogPrefix = "[Project Optimizer]";
        public const string MenuPath = "Tools/Project Optimizer";
        public const string MinimumUnityVersion = "6000.0";
        public const string AddressablesPackageId = "com.unity.addressables";
        public const string AddressablesBuildReportsPath = "Library/com.unity.addressables/buildReports";
        public const string AddressablesBuildReportsAlternatePath = "Library/com.unity.addressables/BuildReports";
        public const string AddressablesBuildLayoutPath = "Library/com.unity.addressables/buildlayout";

        public const string PackageRootAssetPath = "Packages/" + PackageId;
        public const string CacheFolder = "Library/ProjectOptimizer";
        public const string SettingsPath = "ProjectSettings/ProjectOptimizerSettings.json";
        public const string LegacySettingsPath = "Assets/Settings/ProjectOptimizerSettings.json";
        public const string WindowStatePath = CacheFolder + "/WindowState.asset";
        public const string ScanSnapshotFileName = "ScanSnapshot.json";
        public const string RuntimeEvidenceFileName = "RuntimeEvidence.json";
        public const string AddressablesAnalyzeFileName = "AddressablesAnalyze.json";
        public const string OptimizationBaselineFileName = "OptimizationBaseline.json";
        public const string DuplicateHashCacheFileName = "DuplicateHashes.txt";
        public const string ScanCacheFileName = "Cache.txt";
        public const string LegacyScanCacheFileName = "Cache.json";
        public const string ScanHistoryFileName = "ScanHistory.json";
        public const string ScanProfileFileName = "ScanShape.txt";
        public const string FixBackupFolderName = "Backups";
        public const string AssetDatabaseSnapshotFileName = "AssetDatabaseSnapshot.bin";
        public const string ReferenceGraphFileName = "Refs.txt";
        public const string ScopedReferenceGraphFileName = "Refs.scoped.txt";
        public const string LegacyReferenceGraphFileName = "Refs.json";
        public const string AssetDatabaseChangesFileName = "AssetDatabaseChanges.bin";
        public const string BuildReportsFolderName = "BuildReports";
        public const string BuildReportLatestFileName = "Latest.json";
        public const string BuildReportHistoryFileName = "History.json";
        public const string MemorySnapshotsFolderName = "MemorySnapshots";
        public const string ScanTimingsFolderName = "ScanTimings";
        public const string CiFolderName = "CI";

        public const string RefreshAddressablesAnalyzeCommand = "Refresh Addressables Analyze Evidence";
        public const string RebuildProjectStateCommand = "Rebuild Project State";
        public const string ReconcileProjectStateCommand = "Request Authoritative Reconcile";
        public const string FlushProjectStateCommand = "Flush Persistent Snapshot";
        public const string LogProjectStateDiagnosticsCommand = "Log Project State Diagnostics";
        public const string RunIntegrityAuditCommand = "Run Integrity Audit";

        public static string CacheFile(string fileName) => CacheFolder + "/" + fileName;
        public static string BuildReportsFolder => CacheFolder + "/" + BuildReportsFolderName;
        public static string BuildReportLatestPath => BuildReportsFolder + "/" + BuildReportLatestFileName;
        public static string BuildReportHistoryPath => BuildReportsFolder + "/" + BuildReportHistoryFileName;
        public static string MemorySnapshotsFolder => CacheFolder + "/" + MemorySnapshotsFolderName;
        public static string ScanTimingsFolder => CacheFolder + "/" + ScanTimingsFolderName;
        public static string CiFolder => CacheFolder + "/" + CiFolderName;
        public static string ScanSnapshotPath => CacheFile(ScanSnapshotFileName);
        public static string RuntimeEvidencePath => CacheFile(RuntimeEvidenceFileName);
        public static string AddressablesAnalyzePath => CacheFile(AddressablesAnalyzeFileName);
        public static string OptimizationBaselinePath => CacheFile(OptimizationBaselineFileName);
        public static string ReferenceGraphPath(bool projectWide) => CacheFolder + "/" + (projectWide ? ReferenceGraphFileName : ScopedReferenceGraphFileName);
        public static string LegacyReferenceGraphPath => CacheFile(LegacyReferenceGraphFileName);
        public static string AssetDatabaseChangesPath => CacheFile(AssetDatabaseChangesFileName);
        public static string DuplicateHashCachePath => CacheFile(DuplicateHashCacheFileName);
        public static string ScanCachePath => CacheFile(ScanCacheFileName);
        public static string LegacyScanCachePath => CacheFile(LegacyScanCacheFileName);
        public static string ScanHistoryPath => CacheFile(ScanHistoryFileName);
        public static string ScanProfilePath => CacheFile(ScanProfileFileName);
        public static string FixBackupFolder => CacheFolder + "/" + FixBackupFolderName;
        public static string AssetDatabaseSnapshotPath => CacheFile(AssetDatabaseSnapshotFileName);

        public static string PackageVersion
        {
            get
            {
                try
                {
                    var package = PackageInfo.FindForPackageName(PackageId);
                    if (package != null && !string.IsNullOrEmpty(package.version)) return package.version;
                }
                catch { }
                return "unknown";
            }
        }

        public static string DisplayVersion(int schema) => ProductName + " " + PackageVersion + " schema " + schema;

        /// <summary>Filesystem location of the currently resolved package.</summary>
        public static string PackageRoot
        {
            get
            {
                try
                {
                    var package = PackageInfo.FindForPackageName(PackageId);
                    string resolved = package?.resolvedPath;
                    if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved)) return resolved;
                }
                catch { }

                string embedded = Path.Combine(Directory.GetCurrentDirectory(), PackageRootAssetPath.Replace('/', Path.DirectorySeparatorChar));
                return Directory.Exists(embedded) ? embedded : string.Empty;
            }
        }

        /// <summary>AssetDatabase-relative path inside this package; works for registry/Git/local packages.</summary>
        public static string PackageAssetPath(string relativePath)
        {
            string relative = (relativePath ?? string.Empty).TrimStart('/', '\\').Replace('\\', '/');
            try
            {
                var package = PackageInfo.FindForPackageName(PackageId);
                string assetPath = package?.assetPath;
                if (!string.IsNullOrEmpty(assetPath))
                    return string.IsNullOrEmpty(relative) ? assetPath.TrimEnd('/') : assetPath.TrimEnd('/') + "/" + relative;
            }
            catch { }

            return string.IsNullOrEmpty(relative) ? PackageRootAssetPath : PackageRootAssetPath + "/" + relative;
        }
    }
}
