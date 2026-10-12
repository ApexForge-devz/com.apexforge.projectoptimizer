using System;
using System.Reflection;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using ApexForge.ProjectOptimizer;

namespace ApexForge.ProjectOptimizer.Tests
{
    public sealed class HighPotentialAutoFixTests
    {
        [SetUp]
        public void SetUp() => RuleBootstrap.EnsureBuiltInRules();

        [TestCase("PRJ009")]
        [TestCase("PRJ030")]
        [TestCase("PRJ032")]
        [TestCase("PRJ050")]
        [TestCase("PRJ064")]
        [TestCase("SCR006")]
        public void AcceptedHighPotentialRulesDeclareVerifiedWithBackupAndHighConfidence(string id)
        {
            var rule = RuleCatalog.Get(id);
            Assert.That(rule, Is.Not.Null, id + " must be registered.");
            Assert.That(rule.FixKind, Is.EqualTo(FixKind.Verified), id + " must be eligible for automatic fixes in its proven common case.");
            Assert.That(rule.Confidence, Is.GreaterThanOrEqualTo(OptimizationConfidence.HighConfidence));
            Assert.That(rule.RecommendationConfidence, Is.GreaterThanOrEqualTo(OptimizationConfidence.HighConfidence));
            Assert.That(rule.DefaultBackupRequired, Is.True, id + " mutates project settings or source and must have a backup.");
        }

        [Test]
        public void AnisotropicFilteringFixTargetsTheNamedQualityLevelAndRestoresSelection()
        {
            if (QualitySettings.count <= 0 || QualitySettings.names == null || QualitySettings.names.Length == 0)
                Assert.Ignore("This Unity project exposes no Quality Levels to test.");

            int targetIndex = 0;
            string targetName = QualitySettings.names[targetIndex];
            int previousIndex = QualitySettings.GetQualityLevel();
            var qualityObjects = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset") ?? Array.Empty<UnityEngine.Object>();
            var dirtyBefore = new bool[qualityObjects.Length];
            for (int i = 0; i < qualityObjects.Length; i++) dirtyBefore[i] = qualityObjects[i] != null && EditorUtility.IsDirty(qualityObjects[i]);
            var originalValue = ReadQualityLevel(targetIndex, targetName);
            var context = new FixContext();
            try
            {
                QualitySettings.SetQualityLevel(targetIndex, false);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                if (previousIndex >= 0 && previousIndex < QualitySettings.count && previousIndex != targetIndex)
                    QualitySettings.SetQualityLevel(previousIndex, false);

                InvokeAnisotropicFix(context, targetIndex, targetName);

                Assert.That(ReadQualityLevel(targetIndex, targetName), Is.EqualTo(AnisotropicFiltering.Enable));
                Assert.That(QualitySettings.GetQualityLevel(), Is.EqualTo(previousIndex), "The fix helper must restore the Editor's selected Quality Level.");
                Assert.That(context.ChangedProjectSettings, Is.True);
                var stale = Assert.Throws<TargetInvocationException>(() => InvokeAnisotropicFix(context, targetIndex, targetName));
                Assert.That(stale.InnerException, Is.TypeOf<InvalidOperationException>(), "A stale finding must not rewrite a Quality Level that no longer has Force Enable set.");
            }
            finally
            {
                if (targetIndex < QualitySettings.count && string.Equals(QualitySettings.names[targetIndex], targetName, StringComparison.Ordinal))
                {
                    QualitySettings.SetQualityLevel(targetIndex, false);
                    QualitySettings.anisotropicFiltering = originalValue;
                }
                if (previousIndex >= 0 && previousIndex < QualitySettings.count) QualitySettings.SetQualityLevel(previousIndex, false);
                for (int i = 0; i < qualityObjects.Length; i++)
                    if (qualityObjects[i] != null && !dirtyBefore[i]) EditorUtility.ClearDirty(qualityObjects[i]);
            }
        }

        [Test]
        public void AcceptedRulesWireTheirExpectedMutationsAndPostconditionVerifiers()
        {
            string packageRoot = ProjectOptimizerConstants.PackageRoot;
            Assert.That(string.IsNullOrEmpty(packageRoot), Is.False);
            string settings = File.ReadAllText(Path.Combine(packageRoot, "Editor/Analyzers/ProjectSettingsAnalyzer.cs"));
            StringAssert.Contains("SetAnisotropicFilteringPerTexture(c, levelIndex, levelName)", settings);
            StringAssert.Contains("PlatformUtility.GraphicsApiListsEqual(apis, preferredApis)", settings);
            StringAssert.Contains("PlatformUtility.PreferAndroidVulkan(autoApis, apis, preferredApis)", settings);
            StringAssert.Contains("verify: _ => HasAndroidVulkanPreferred(preferredApis)", settings);
            StringAssert.Contains("SetAndroidBlitTypeAuto(autoApis, apis)", settings);
            StringAssert.Contains("ShouldRecommendAndroidBlitAuto(PlayerSettings.Android.blitType == AndroidBlitType.Always, autoApis, apis)", settings);
            StringAssert.Contains("CanApplyAndroidBlitAuto(PlayerSettings.Android.blitType, useDefaultApis, currentApis", settings);
            StringAssert.Contains("SetFastSrgbLinearConversion(c, pipelinePath, true)", settings);
            StringAssert.Contains("m_UseFastSRGBLinearConversion", settings);
            StringAssert.Contains("SetOwnedBool(owner, \"dedicatedServerOptimizations\", true, c)", settings);
            StringAssert.Contains("ReadOwnedBool(owner, \"dedicatedServerOptimizations\")", settings);
            StringAssert.Contains("OwnedBoolWriteBlockReason(owner)", settings);
            StringAssert.Contains("RouteToDiagnosticOnly(issue, ownerWriteBlockReason)", settings);

            string scripts = File.ReadAllText(Path.Combine(packageRoot, "Editor/Analyzers/ScriptAnalyzer.cs"));
            StringAssert.Contains("TryRewriteEmptyOnGui", scripts);
            StringAssert.Contains("VerifySourceRewrite(c, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && !TryRewriteEmptyOnGui(source, out _))", scripts);
            StringAssert.Contains("RouteOnGuiIssueToDiagnosticOnly(issue)", scripts);
        }

        [Test]
        public void ProfileOwnedFixBacksUpAndSavesTheExactOwnerAndRollbackIncludesExtraFiles()
        {
            string packageRoot = ProjectOptimizerConstants.PackageRoot;
            Assert.That(string.IsNullOrEmpty(packageRoot), Is.False);
            string projectSettings = File.ReadAllText(Path.Combine(packageRoot, "Editor/Analyzers/ProjectSettingsAnalyzer.cs"));
            string setOwnedStart = projectSettings.IndexOf("static void SetOwnedBool(", StringComparison.Ordinal);
            string setOwnedEnd = projectSettings.IndexOf("// --- physics", setOwnedStart, StringComparison.Ordinal);
            Assert.That(setOwnedStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(setOwnedEnd, Is.GreaterThan(setOwnedStart));
            string setOwned = projectSettings.Substring(setOwnedStart, setOwnedEnd - setOwnedStart);
            int ownerGuardStart = projectSettings.IndexOf("static string OwnedBoolWriteBlockReason(", StringComparison.Ordinal);
            int ownerGuardEnd = projectSettings.IndexOf("static void SetOwnedBool(", ownerGuardStart, StringComparison.Ordinal);
            Assert.That(ownerGuardStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(ownerGuardEnd, Is.GreaterThan(ownerGuardStart));
            string ownerGuard = projectSettings.Substring(ownerGuardStart, ownerGuardEnd - ownerGuardStart);
            StringAssert.Contains("UnityEditorUtility.CanEditPath(ownerPath", ownerGuard);
            StringAssert.Contains("EditorUtility.IsDirty(owner)", ownerGuard);
            StringAssert.Contains("OwnedBoolWriteBlockReason(owner)", setOwned);
            StringAssert.Contains("context.RequireBackup(ownerPath)", setOwned);
            StringAssert.Contains("if (prop.boolValue == value)", setOwned);
            StringAssert.Contains("AssetDatabase.SaveAssetIfDirty(owner)", setOwned);

            int fastSrgbStart = projectSettings.IndexOf("static void SetFastSrgbLinearConversion(", StringComparison.Ordinal);
            int fastSrgbEnd = projectSettings.IndexOf("static void SetPipelineIntFirstAvailable(", fastSrgbStart, StringComparison.Ordinal);
            Assert.That(fastSrgbStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(fastSrgbEnd, Is.GreaterThan(fastSrgbStart));
            string fastSrgb = projectSettings.Substring(fastSrgbStart, fastSrgbEnd - fastSrgbStart);
            StringAssert.Contains("UnityEditorUtility.CanEditPath(pipelinePath", fastSrgb);
            StringAssert.Contains("EditorUtility.IsDirty(asset)", fastSrgb);
            StringAssert.Contains("context.RequireBackup(pipelinePath)", fastSrgb);
            StringAssert.Contains("property.propertyType != SerializedPropertyType.Boolean", fastSrgb);
            StringAssert.Contains("EditorUtility.SetDirty(asset)", fastSrgb);
            StringAssert.Contains("AssetDatabase.SaveAssetIfDirty(asset)", fastSrgb);
            StringAssert.Contains("context.ChangedProjectSettings = true", fastSrgb);

            string fixEngine = File.ReadAllText(Path.Combine(packageRoot, "Editor/Core/Fixes/FixEngine.cs"));
            string rollbackStart = fixEngine.IndexOf("static bool RollbackProjectSettingsWorks(", StringComparison.Ordinal);
            string rollbackEnd = fixEngine.IndexOf("static void ReloadProjectSettingsFromDisk(", rollbackStart, StringComparison.Ordinal);
            Assert.That(rollbackStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(rollbackEnd, Is.GreaterThan(rollbackStart));
            string rollback = fixEngine.Substring(rollbackStart, rollbackEnd - rollbackStart);
            StringAssert.Contains("works[i].Manifest", rollback);
            StringAssert.Contains("batch.Restore(restoreEntries)", rollback);
        }

        static AnisotropicFiltering ReadQualityLevel(int index, string expectedName)
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod("ReadQualityLevelStrict", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var generic = method.MakeGenericMethod(typeof(AnisotropicFiltering));
            object value = generic.Invoke(null, new object[] { index, expectedName, (Func<AnisotropicFiltering>)(() => QualitySettings.anisotropicFiltering) });
            return (AnisotropicFiltering)value;
        }

        static void InvokeAnisotropicFix(FixContext context, int index, string name)
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod("SetAnisotropicFilteringPerTexture", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { context, index, name });
        }

        [Test]
        public void AndroidVulkanOrderKeepsVulkanAndOpenGlesFallbackFirstAndDeduplicatesOthers()
        {
            var method = typeof(ProjectSettingsAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.PlatformUtility")
                ?.GetMethod("BuildAndroidVulkanPreferredOrder", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing pure Android graphics API ordering helper.");
            var current = new[]
            {
                GraphicsDeviceType.OpenGLES3,
                GraphicsDeviceType.Direct3D11,
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.Direct3D11
            };
            var actual = (GraphicsDeviceType[])method.Invoke(null, new object[] { current });
            CollectionAssert.AreEqual(new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3,
                GraphicsDeviceType.Direct3D11
            }, actual);
        }

        [Test]
        public void AndroidVulkanPostconditionRequiresExplicitExactScannedOrder()
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod("HasAndroidVulkanPreferred", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(bool), typeof(GraphicsDeviceType[]), typeof(GraphicsDeviceType[]) }, null);
            Assert.That(method, Is.Not.Null, "Missing pure Android Vulkan postcondition helper.");
            var expected = new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.Direct3D11 };
            Assert.That((bool)method.Invoke(null, new object[] { false, expected, expected }), Is.True);
            Assert.That((bool)method.Invoke(null, new object[] { true, expected, expected }), Is.False, "Automatic API selection is not an explicit verified order.");
            Assert.That((bool)method.Invoke(null, new object[] { false, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, expected }), Is.False, "A different fallback list must not satisfy the scanned contract.");
            Assert.That((bool)method.Invoke(null, new object[] { false, new[] { GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.Vulkan, GraphicsDeviceType.Direct3D11 }, expected }), Is.False);
        }

        [TestCase(true, true, null, true)]
        [TestCase(false, true, null, false)]
        [TestCase(true, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false)]
        [TestCase(true, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, true)]
        [TestCase(false, false, new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES3, GraphicsDeviceType.Vulkan }, false)]
        public void AndroidBlitRuleOnlyAppliesWhenAlwaysAndANonVulkanApiCanBeUsed(bool blitAlways, bool useDefaultApis, GraphicsDeviceType[] apis, bool expected)
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod("ShouldRecommendAndroidBlitAuto", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing Android Blit Type applicability predicate.");
            Assert.That((bool)method.Invoke(null, new object[] { blitAlways, useDefaultApis, apis }), Is.EqualTo(expected));
        }

        [TestCase(AndroidBlitType.Always, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, true)]
        [TestCase(AndroidBlitType.Auto, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 }, false)]
        [TestCase(AndroidBlitType.Always, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false)]
        [TestCase(AndroidBlitType.Always, true, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false)]
        [TestCase(AndroidBlitType.Always, false, new GraphicsDeviceType[] { GraphicsDeviceType.OpenGLES3 }, false, new GraphicsDeviceType[] { GraphicsDeviceType.Vulkan }, false)]
        public void AndroidBlitAutoPreflightRejectsStaleOrInapplicableSettings(AndroidBlitType currentBlitType,
            bool useDefaultApis, GraphicsDeviceType[] currentApis, bool expectedUseDefaultApis,
            GraphicsDeviceType[] expectedApis, bool expected)
        {
            var method = typeof(ProjectSettingsAnalyzer).GetMethod("CanApplyAndroidBlitAuto", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing Android Blit Type preflight predicate.");
            Assert.That((bool)method.Invoke(null, new object[] { currentBlitType, useDefaultApis, currentApis, expectedUseDefaultApis, expectedApis }), Is.EqualTo(expected));
        }

        [Test]
        public void UnsupportedOnGuiInstanceIsDowngradedToDiagnosticOnly()
        {
            var method = typeof(ProjectSettingsAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.ScriptAnalyzer")
                ?.GetMethod("RouteOnGuiIssueToDiagnosticOnly", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing SCR006 safety-gate helper.");
            var issue = new OptimizationIssue
            {
                FixKind = FixKind.Verified,
                Confidence = OptimizationConfidence.HighConfidence,
                RecommendationConfidence = OptimizationConfidence.HighConfidence,
                Fix = _ => { },
                Verify = _ => true,
                Risk = FixRisk.Medium,
                BackupRequired = true,
                EvidenceState = EvidenceState.Fresh,
                EvidenceSignature = "stale-before-downgrade",
                AssetEvidencePath = "Assets/Example.cs",
                AssetEvidenceSignature = "source-signature"
            };
            method.Invoke(null, new object[] { issue });
            Assert.That(issue.FixKind, Is.EqualTo(FixKind.None));
            Assert.That(issue.Fix, Is.Null);
            Assert.That(issue.Verify, Is.Null);
            Assert.That(issue.Options, Is.Empty);
            Assert.That(issue.BackupRequired, Is.False);
            Assert.That(issue.Confidence, Is.EqualTo(OptimizationConfidence.Diagnostic));
            Assert.That(issue.RecommendationConfidence, Is.EqualTo(OptimizationConfidence.Diagnostic));
            Assert.That(issue.EvidenceState, Is.EqualTo(EvidenceState.Unconfirmed));
            Assert.That(issue.EvidenceSignature, Is.Null);
            Assert.That(issue.AssetEvidencePath, Is.Null);
            Assert.That(issue.AssetEvidenceSignature, Is.Null);
        }

        [Test]
        public void EmptyPrivateOnGuiCanBeRemovedButOtherCallbacksArePreserved()
        {
            const string empty = "using UnityEngine;\npublic class EmptyGui : MonoBehaviour\n{\n    private void OnGUI() { }\n}\n";
            AssertRewrite(empty, true, "empty private callback should be removable");

            const string nonEmpty = "using UnityEngine;\npublic class ActiveGui : MonoBehaviour\n{\n    private void OnGUI() { GUI.Label(new Rect(), \"Hello\"); }\n}\n";
            AssertRewrite(nonEmpty, false, "non-empty callbacks must not be removed");

            const string protectedCallback = "using UnityEngine;\npublic class ProtectedGui : MonoBehaviour\n{\n    protected void OnGUI() { }\n}\n";
            AssertRewrite(protectedCallback, false, "protected callbacks are not safe for automatic removal");

            const string attributed = "using UnityEngine;\npublic class AttributedGui : MonoBehaviour\n{\n    [Obsolete]\n    private void OnGUI() { }\n}\n";
            AssertRewrite(attributed, false, "annotated callbacks must be preserved");

            const string documentedBody = "using UnityEngine;\npublic class DocumentedGui : MonoBehaviour\n{\n    private void OnGUI() { /* intentionally reserved for a reflection hook */ }\n}\n";
            AssertRewrite(documentedBody, false, "comments inside a callback must prevent deleting it as empty");

            const string trailingComment = "using UnityEngine;\npublic class TrailingCommentGui : MonoBehaviour\n{\n    private void OnGUI() { } // intentionally declared for a tooling hook\n}\n";
            AssertRewrite(trailingComment, false, "same-line comments must not be orphaned by the rewrite");

            const string conditional = "using UnityEngine;\npublic class ConditionalGui : MonoBehaviour\n{\n#if DEVELOPMENT_BUILD\n    private void OnGUI() { }\n#endif\n}\n";
            AssertRewrite(conditional, false, "preprocessor-dependent callback presence must not be rewritten");
        }

        static void AssertRewrite(string source, bool expected, string message)
        {
            var method = typeof(ProjectSettingsAnalyzer).Assembly.GetType("ApexForge.ProjectOptimizer.ScriptAnalyzer")
                ?.GetMethod("TryRewriteEmptyOnGui", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing SCR006 rewrite helper.");
            object[] args = { source, null };
            bool changed = (bool)method.Invoke(null, args);
            Assert.That(changed, Is.EqualTo(expected), message);
            if (expected)
            {
                string rewritten = args[1] as string;
                Assert.That(rewritten, Is.Not.Null);
                Assert.That(rewritten, Does.Not.Contain("OnGUI"), "The empty callback should be removed.");
            }
            else
            {
                Assert.That(args[1] as string, Is.EqualTo(source), "A rejected rewrite must leave source unchanged.");
            }
        }
    }
}
