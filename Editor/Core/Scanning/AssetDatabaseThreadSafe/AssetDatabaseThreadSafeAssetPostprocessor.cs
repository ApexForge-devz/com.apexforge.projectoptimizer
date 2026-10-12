using UnityEditor;

namespace ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe
{
    internal sealed class AssetDatabaseThreadSafeAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths,
            bool didDomainReload)
        {
            AssetDatabaseThreadSafeUtility.QueueAssetChanges(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths, didDomainReload);
        }
    }
}
