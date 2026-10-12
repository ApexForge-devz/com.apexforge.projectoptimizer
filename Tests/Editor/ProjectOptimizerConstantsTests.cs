using NUnit.Framework;
using UnityEditor.PackageManager;

namespace ApexForge.ProjectOptimizer.Tests
{
    public class ProjectOptimizerConstantsTests
    {
        [Test] public void ProductIdentityMatchesRegisteredPackage()
        {
            var package = PackageInfo.FindForPackageName(ProjectOptimizerConstants.PackageId);
            Assert.IsNotNull(package);
            Assert.AreEqual(package.name, ProjectOptimizerConstants.PackageId);
            Assert.AreEqual(package.displayName, ProjectOptimizerConstants.PackageDisplayName);
            Assert.AreEqual(package.version, ProjectOptimizerConstants.PackageVersion);
            Assert.IsFalse(string.IsNullOrEmpty(ProjectOptimizerConstants.ProductName));
            Assert.IsFalse(string.IsNullOrEmpty(ProjectOptimizerConstants.MenuPath));
        }

        [Test] public void PackageOwnedPathsAreDerivedFromCentralConstants()
        {
            Assert.AreEqual(ProjectOptimizerConstants.CacheFolder + "/" + ProjectOptimizerConstants.ScanSnapshotFileName, ProjectOptimizerConstants.ScanSnapshotPath);
            Assert.AreEqual(ProjectOptimizerConstants.CacheFolder + "/" + ProjectOptimizerConstants.RuntimeEvidenceFileName, ProjectOptimizerConstants.RuntimeEvidencePath);
            Assert.AreEqual(ProjectOptimizerConstants.CacheFolder + "/" + ProjectOptimizerConstants.AddressablesAnalyzeFileName, ProjectOptimizerConstants.AddressablesAnalyzePath);
        }

        [Test] public void PackageAssetPathResolvesThroughPackageMetadata()
        {
            string path = ProjectOptimizerConstants.PackageAssetPath("Editor/UI/ProjectOptimizerStyles.uss");
            Assert.IsTrue(path.EndsWith("Editor/UI/ProjectOptimizerStyles.uss"));
            Assert.IsFalse(string.IsNullOrEmpty(PackageInfo.FindForPackageName(ProjectOptimizerConstants.PackageId)?.assetPath));
        }
    }
}
