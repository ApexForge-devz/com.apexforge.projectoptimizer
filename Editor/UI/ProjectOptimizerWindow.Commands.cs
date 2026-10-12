using System.IO;
using ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe;
using UnityEngine;

namespace ApexForge.ProjectOptimizer
{
    public sealed partial class ProjectOptimizerWindow
    {
        internal static void RebuildProjectState()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            AssetDatabaseThreadSafeUtility.Initialize(root, true);
            AssetDatabaseThreadSafeUtility.RequestFullRebuild();
            Debug.Log(ProjectOptimizerConstants.LogPrefix + " Project state rebuild requested. Run a scan to execute it.");
        }

        internal static void RequestProjectStateReconcile()
        {
            AssetDatabaseThreadSafeUtility.RequestAuthoritativeReconcile();
            Debug.Log(ProjectOptimizerConstants.LogPrefix + " Authoritative project-state reconciliation requested. Run a scan to execute it.");
        }

        internal static void FlushProjectStateSnapshot()
        {
            AssetDatabaseThreadSafeUtility.FlushPersistentSnapshot();
            Debug.Log(ProjectOptimizerConstants.LogPrefix + " Persistent project-state snapshot flushed.");
        }

        internal static void LogProjectStateDiagnostics()
        {
            Debug.Log(ProjectOptimizerConstants.LogPrefix + " " + AssetDatabaseThreadSafeUtility.ProjectStateDiagnostics());
        }

        internal static void RefreshAddressablesAnalyzeEvidence() => AddressablesAnalyzeEvidenceStore.CaptureLatest();

        internal static void RunIntegrityAudit() => ProjectOptimizerIntegrityAudit.RunAndLog();
    }
}
