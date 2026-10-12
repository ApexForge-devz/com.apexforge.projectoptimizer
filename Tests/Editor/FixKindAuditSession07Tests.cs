using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Session-specific regression coverage for the approved Model/Animation FixKind policy.
    /// Asset importer integration and visual fidelity still require Unity fixtures in a real project.
    /// </summary>
    public sealed class FixKindAuditSession07Tests
    {
        [SetUp]
        public void RegisterSessionAnalyzers()
        {
            // Forces each analyzer's static rule registration before catalog assertions.
            _ = new ModelAnalyzer();
            _ = new AnimationAnalyzer();
        }

        [TestCase("ANI001", FixKind.Verified)]
        [TestCase("ANI002", FixKind.Verified)]
        [TestCase("ANI003", FixKind.Verified)]
        [TestCase("ANI004", FixKind.None)]
        [TestCase("ANI005", FixKind.None)]
        [TestCase("ANI006", FixKind.Verified)]
        [TestCase("ANI007", FixKind.None)]
        [TestCase("ANI008", FixKind.None)]
        [TestCase("ANI009", FixKind.Verified)]
        [TestCase("ANI010", FixKind.Verified)]
        [TestCase("ANI011", FixKind.Verified)]
        [TestCase("MDL001", FixKind.Verified)]
        [TestCase("MDL002", FixKind.Verified)]
        [TestCase("MDL003", FixKind.Verified)]
        [TestCase("MDL004", FixKind.Verified)]
        [TestCase("MDL005", FixKind.Verified)]
        [TestCase("MDL006", FixKind.Verified)]
        [TestCase("MDL007", FixKind.Verified)]
        [TestCase("MDL008", FixKind.Verified)]
        [TestCase("MDL009", FixKind.None)]
        [TestCase("MDL010", FixKind.Verified)]
        [TestCase("MDL011", FixKind.None)]
        [TestCase("MDL012", FixKind.Verified)]
        [TestCase("MDL013", FixKind.Verified)]
        [TestCase("MDL014", FixKind.Verified)]
        [TestCase("MDL015", FixKind.Verified)]
        [TestCase("MDL016", FixKind.Verified)]
        [TestCase("MDL017", FixKind.Verified)]
        [TestCase("MDL018", FixKind.Review)]
        public void CatalogFixKindMatchesApprovedTarget(string ruleId, FixKind expected)
        {
            var definition = RuleCatalog.Get(ruleId);
            Assert.That(definition, Is.Not.Null, "Expected registered rule " + ruleId);
            Assert.That(definition.FixKind, Is.EqualTo(expected), ruleId);
        }

        [TestCase(true, 0, false, true)]
        [TestCase(false, 0, false, false)]
        [TestCase(true, 1, false, false)]
        [TestCase(true, -1, false, false)]
        [TestCase(true, 0, true, false)]
        public void UnreferencedModelAutoFixRequiresCompleteEmptyGraph(
            bool currentProjectGraph, int referrerCount, bool dynamicLoadingPath, bool expected)
        {
            var method = typeof(ModelAnalyzer).GetMethod("CanAutoFixUnreferencedModel",
                BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(bool), typeof(int), typeof(bool) }, null);
            Assert.That(method, Is.Not.Null, "Expected analyzer-local reference safety gate");
            var actual = (bool)method.Invoke(null, new object[] { currentProjectGraph, referrerCount, dynamicLoadingPath });
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase("Assets/Resources/Characters/Hero.fbx", true)]
        [TestCase("Assets/StreamingAssets/Models/Hero.fbx", true)]
        [TestCase("Packages/com.example/Resources/Hero.fbx", true)]
        [TestCase("Assets/Models/Hero.fbx", false)]
        [TestCase("", true)]
        public void DynamicLoadingPathsAreExcludedFromUnreferencedModelAutoFix(string assetPath, bool expected)
        {
            var method = typeof(ModelAnalyzer).GetMethod("IsDynamicLoadingPath",
                BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            Assert.That(method, Is.Not.Null, "Expected analyzer-local dynamic-loading path gate");
            var actual = (bool)method.Invoke(null, new object[] { assetPath });
            Assert.That(actual, Is.EqualTo(expected));
        }


        [Test]
        public void ConstantCurveEligibilityAcceptsFlatAndRejectsNonFlatInterpolation()
        {
            var method = typeof(AnimationAnalyzer).GetMethod("IsSafeConstantCurve",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Expected ANI006 safe-curve eligibility check");

            var flat = new AnimationCurve(
                new Keyframe(0f, 2f, 0f, 0f),
                new Keyframe(1f, 2f, 0f, 0f),
                new Keyframe(2f, 2f, 0f, 0f));
            Assert.That(method.Invoke(null, new object[] { flat }), Is.EqualTo(true));

            var nonFlatInterpolation = new AnimationCurve(
                new Keyframe(0f, 2f, 1f, 1f),
                new Keyframe(1f, 2f, 1f, 1f),
                new Keyframe(2f, 2f, 1f, 1f));
            Assert.That(method.Invoke(null, new object[] { nonFlatInterpolation }), Is.EqualTo(false));
        }

        [Test]
        public void KeyReductionPostconditionAcceptsExpectedKeyCountAndRejectsWrongTarget()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/__FixKindAuditSession07_Reduced.anim");
            try
            {
                var clip = new AnimationClip();
                var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x");
                var curve = new AnimationCurve(
                    new Keyframe(0f, 0f),
                    new Keyframe(0.5f, 1f),
                    new Keyframe(1f, 2f));
                AnimationUtility.SetEditorCurve(clip, binding, curve);
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();

                var verifier = typeof(AnimationAnalyzer).GetMethod("VerifyReducedClip",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(verifier, Is.Not.Null, "Expected ANI009 key-count postcondition verifier");
                Assert.That(verifier.Invoke(null, new object[] { path, 4L, 1 }), Is.EqualTo(true));
                Assert.That(verifier.Invoke(null, new object[] { path, 4L, 2 }), Is.EqualTo(false));
                Assert.That(verifier.Invoke(null, new object[] { path, 4L, 0 }), Is.EqualTo(false));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void ImporterPostconditionVerifiersRejectMissingContextOrPredicate()
        {
            var modelVerifier = typeof(ModelAnalyzer).GetMethod("VerifyModelImporterSetting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(modelVerifier, Is.Not.Null, "Expected model importer verifier");
            Assert.That(modelVerifier.Invoke(null, new object[] { null, null, false }), Is.EqualTo(false));
            Assert.That(modelVerifier.Invoke(null, new object[] { null, new System.Func<ModelImporter, bool>(_ => true), false }), Is.EqualTo(false));

            var animationVerifier = typeof(AnimationAnalyzer).GetMethod("VerifyModelImporterSetting",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(animationVerifier, Is.Not.Null, "Expected animation importer verifier");
            Assert.That(animationVerifier.Invoke(null, new object[] { null, null, false, false }), Is.EqualTo(false));
        }

        [TestCase(2, 2)]
        [TestCase(3, 3)]
        [TestCase(4, 4)]
        [TestCase(5, 4)]
        [TestCase(8, 4)]
        public void BoneInfluenceTargetUsesFourAsMaximum(int current, int expected)
        {
            var method = typeof(ModelAnalyzer).GetMethod("MaxBoneInfluenceTarget",
                BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(int) }, null);
            Assert.That(method, Is.Not.Null, "Expected explicit four-influence target helper");
            var actual = (int)method.Invoke(null, new object[] { current });
            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
