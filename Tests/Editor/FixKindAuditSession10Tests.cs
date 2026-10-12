using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Session 10 regression coverage. These tests validate catalog registrations and the safety
    /// contract exposed by the terrain ReviewFix builders without mutating a user's project assets.
    /// ADR008 is deliberately kept at None until the missing shared evidence store/refresh runner
    /// is included and the approved Verified action can be implemented and tested end-to-end.
    /// </summary>
    public sealed class FixKindAuditSession10Tests
    {
        [SetUp]
        public void RegisterSessionAnalyzers()
        {
            _ = new AddressablesBuildLayoutAnalyzer();
            _ = new UnusedAnalyzer();
            _ = new WorldDataAnalyzer();
            _ = new BuildProfileAnalyzer();
            _ = new AssemblyDefinitionAnalyzer();
        }

        [TestCase("ADR001", FixKind.Review)]
        [TestCase("ADR002", FixKind.Review)]
        [TestCase("ADR003", FixKind.Review)]
        [TestCase("ADR004", FixKind.Review)]
        [TestCase("ADR005", FixKind.Review)]
        [TestCase("ADR006", FixKind.Review)]
        [TestCase("ADR007", FixKind.Review)]
        [TestCase("ADR008", FixKind.None)]
        [TestCase("ADR009", FixKind.Review)]
        [TestCase("ASM001", FixKind.None)]
        [TestCase("ASM002", FixKind.None)]
        [TestCase("PRJ071", FixKind.Review)]
        [TestCase("TER001", FixKind.Review)]
        [TestCase("TER002", FixKind.Review)]
        [TestCase("TER003", FixKind.Review)]
        [TestCase("TER004", FixKind.Review)]
        [TestCase("UNU001", FixKind.Verified)]
        [TestCase("UNU002", FixKind.None)]
        [TestCase("UNU003", FixKind.None)]
        [TestCase("UNU004", FixKind.Verified)]
        [TestCase("UNU005", FixKind.None)]
        [TestCase("UNU006", FixKind.None)]
        public void CatalogFixKindMatchesImplementedSession10State(string ruleId, FixKind expected)
        {
            var rule = RuleCatalog.Get(ruleId);
            Assert.IsNotNull(rule, "Expected rule registration: " + ruleId);
            Assert.AreEqual(expected, rule.FixKind, "Unexpected current implementation type for " + ruleId);
        }

        [Test]
        public void HeightmapReviewOptionsRequireBackupAndTheirOwnVerifier() => AssertTerrainOptions("BuildHeightmapOptions", new object[] { 2049, "test-guid" }, 2049);

        [Test]
        public void AlphamapReviewOptionsRequireBackupAndTheirOwnVerifier() => AssertTerrainOptions("BuildAlphamapOptions", new object[] { 512, "test-guid" }, 512);

        [Test]
        public void DetailReviewOptionsRequireBackupAndTheirOwnVerifier() => AssertTerrainOptions("BuildDetailOptions", new object[] { 2048, 16, "test-guid" }, 2048);

        static void AssertTerrainOptions(string methodName, object[] arguments, int currentResolution)
        {
            var method = typeof(WorldDataAnalyzer).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Missing options builder: " + methodName);
            var options = method.Invoke(null, arguments) as System.Collections.IEnumerable;
            Assert.IsNotNull(options, "Options builder must return a collection.");

            int count = 0;
            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in options)
            {
                var option = item as FixOption;
                Assert.IsNotNull(option, "Each ReviewFix item must be a FixOption.");
                Assert.IsNotEmpty(option.ActionId);
                Assert.IsTrue(actionIds.Add(option.ActionId), "Duplicate action ID: " + option.ActionId);
                int suffix = option.ActionId.LastIndexOf('_');
                Assert.GreaterOrEqual(suffix, 0, "Target resolution must be encoded in the action ID.");
                int targetResolution;
                Assert.IsTrue(int.TryParse(option.ActionId.Substring(suffix + 1), out targetResolution), "Target resolution must be parseable.");
                Assert.Less(targetResolution, currentResolution, "Review option must only reduce the analyzed resolution.");
                Assert.IsTrue(option.BackupRequired, "Terrain mutation must require backup: " + option.ActionId);
                Assert.IsNotNull(option.Apply, "Terrain mutation needs an apply callback: " + option.ActionId);
                Assert.IsNotNull(option.Verify, "High-risk terrain mutation needs its own verifier: " + option.ActionId);
                count++;
            }
            Assert.Greater(count, 0, "Expected at least one lower-resolution option for " + methodName);
        }


        [TestCase(false, true, false, true, false, false, "reference graph")]
        [TestCase(true, false, false, true, false, false, "dynamic-loader source index")]
        [TestCase(true, true, true, true, false, false, "literal matches")]
        [TestCase(true, true, false, false, false, false, "Addressables registration")]
        [TestCase(true, true, false, true, true, false, "explicit Addressables")]
        [TestCase(true, true, false, true, false, true, "AssetBundle")]
        public void OrphanAutoFixGateExplainsDetectedExceptions(bool graphReady, bool loaderIndexReady, bool dynamicEvidence,
            bool addressablesCheckComplete, bool addressable, bool assetBundle, string expectedReason)
        {
            var method = typeof(UnusedAnalyzer).GetMethod("BuildOrphanBlockReason", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            string reason = (string)method.Invoke(null, new object[] { graphReady, loaderIndexReady, dynamicEvidence, addressablesCheckComplete, addressable, assetBundle });
            StringAssert.Contains(expectedReason, reason);
        }

        [Test]
        public void OrphanAutoFixRejectsMissingOrNonCurrentReferenceEvidence()
        {
            var method = typeof(UnusedAnalyzer).GetMethod("HasCurrentProjectWideReferenceEvidence", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { null }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { new ReferenceIndex() }));
        }

        [Test]
        public void TerrainPostconditionVerifiersRejectWrongAssetIdentity()
        {
            var resolutionVerifier = typeof(WorldDataAnalyzer).GetMethod("VerifyTerrainResolution", BindingFlags.NonPublic | BindingFlags.Static);
            var layerVerifier = typeof(WorldDataAnalyzer).GetMethod("VerifyLayerCount", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(resolutionVerifier);
            Assert.IsNotNull(layerVerifier);
            Type resolutionKind = resolutionVerifier.GetParameters()[3].ParameterType;
            object heightmapKind = Enum.Parse(resolutionKind, "Heightmap");
            Assert.IsFalse((bool)resolutionVerifier.Invoke(null, new object[] { "Assets/__missing_session10_terrain.asset", "wrong-guid", 257, heightmapKind }));
            Assert.IsFalse((bool)layerVerifier.Invoke(null, new object[] { "Assets/__missing_session10_terrain.asset", "wrong-guid", 2 }));
        }

        [Test]
        public void DynamicLoaderLiteralHelpersNormalizePathsAndDetectRecognizedLoaders()
        {
            var normalize = typeof(UnusedAnalyzer).GetMethod("NormalizeLoaderToken", BindingFlags.NonPublic | BindingFlags.Static);
            var marker = typeof(UnusedAnalyzer).GetMethod("ContainsDynamicLoaderMarker", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(normalize);
            Assert.IsNotNull(marker);
            Assert.AreEqual("Icons/Main.png", normalize.Invoke(null, new object[] { @"Assets\Icons\Main.png" }));
            Assert.IsTrue((bool)marker.Invoke(null, new object[] { "some text RESOURCES.LOAD" }));
            Assert.IsFalse((bool)marker.Invoke(null, new object[] { "no recognized dynamic loader here" }));
        }

        [TestCase(33, true)]
        [TestCase(2049, true)]
        [TestCase(1000, false)]
        public void HeightmapTargetsUseSupportedUnityResolutions(int value, bool expected)
        {
            var method = typeof(WorldDataAnalyzer).GetMethod("IsSupportedHeightmapResolution", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { value }));
        }

        [TestCase(8, true)]
        [TestCase(16, true)]
        [TestCase(24, false)]
        public void DetailPatchResolutionRejectsUnsupportedValues(int value, bool expected)
        {
            var method = typeof(WorldDataAnalyzer).GetMethod("IsValidDetailPatchResolution", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { value }));
        }

        [TestCase("Assets/Settings/Graphics.asset", true)]
        [TestCase("Packages/com.example/Graphics.asset", false)]
        [TestCase("", false)]
        public void BuildProfileReviewOnlyOffersMutationsForBackupSupportedAssetPaths(string path, bool expected)
        {
            var method = typeof(BuildProfileAnalyzer).GetMethod("IsBackupSupportedAssetPath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { path }));
        }

        [Test]
        public void BuildProfileRuleDeclaresHighRiskAndBackupRequirement()
        {
            var rule = RuleCatalog.Get("PRJ071");
            Assert.IsNotNull(rule);
            Assert.AreEqual(FixRisk.High, rule.DefaultFixRisk);
            Assert.IsTrue(rule.DefaultBackupRequired);
            Assert.AreEqual(FixKind.Review, rule.FixKind);
        }
    }
}
