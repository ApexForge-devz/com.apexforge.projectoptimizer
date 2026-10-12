using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Session 03 regression coverage. Kept in its own file so parallel FixKind audits do not
    /// conflict with the shared regression suite.
    /// </summary>
    public sealed class FixKindAuditSession03Tests
    {
        [SetUp]
        public void SetUp() => RuleBootstrap.EnsureBuiltInRules();

        [TestCase("PRE001", FixKind.Verified)]
        [TestCase("PRE002", FixKind.Verified)]
        [TestCase("PRE004", FixKind.Verified)]
        [TestCase("PRE005", FixKind.Verified)]
        [TestCase("PRE006", FixKind.Verified)]
        [TestCase("PRE007", FixKind.Verified)]
        [TestCase("PRE008", FixKind.Verified)]
        [TestCase("PRE009", FixKind.Verified)]
        [TestCase("PRE010", FixKind.Verified)]
        [TestCase("PRE011", FixKind.Verified)]
        [TestCase("PRE013", FixKind.Verified)]
        [TestCase("PRE016", FixKind.Verified)]
        [TestCase("RND001", FixKind.Verified)]
        [TestCase("UIT002", FixKind.Verified)]
        [TestCase("PHY002", FixKind.Review)]
        [TestCase("PHY003", FixKind.Review)]
        [TestCase("PRE012", FixKind.Review)]
        [TestCase("PRE015", FixKind.Review)]
        [TestCase("VFX001", FixKind.Review)]
        [TestCase("PAS028", FixKind.None)]
        [TestCase("PHY001", FixKind.None)]
        [TestCase("PHY004", FixKind.None)]
        [TestCase("PRE003", FixKind.None)]
        [TestCase("PRE014", FixKind.None)]
        [TestCase("UIT001", FixKind.None)]
        [TestCase("UIT003", FixKind.None)]
        public void RegisteredFixKindMatchesApprovedSession03Target(string ruleId, FixKind expected)
        {
            var rule = RuleCatalog.Get(ruleId);
            Assert.IsNotNull(rule, "The approved Session 03 rule must be registered: " + ruleId);
            Assert.AreEqual(expected, rule.FixKind, "FixKind mismatch for " + ruleId);
        }

        [Test]
        public void ApprovedSession03ClassificationCountsAreStable()
        {
            var ids = new[]
            {
                "PRE001", "PRE002", "PRE004", "PRE005", "PRE006", "PRE007", "PRE008", "PRE009",
                "PRE010", "PRE011", "PRE013", "PRE016", "RND001", "UIT002",
                "PHY002", "PHY003", "PRE012", "PRE015", "VFX001",
                "PAS028", "PHY001", "PHY004", "PRE003", "PRE014", "UIT001", "UIT003"
            };
            var rules = ids.Select(RuleCatalog.Get).ToArray();
            Assert.AreEqual(26, rules.Length);
            Assert.IsTrue(rules.All(rule => rule != null), "Every canonical Session 03 rule must remain registered.");
            Assert.AreEqual(14, rules.Count(rule => rule.FixKind == FixKind.Verified));
            Assert.AreEqual(5, rules.Count(rule => rule.FixKind == FixKind.Review));
            Assert.AreEqual(7, rules.Count(rule => rule.FixKind == FixKind.None));
        }

        [Test]
        public void HighRiskAutomaticRulesRequireBackups()
        {
            var ids = new[] { "PRE001", "PRE002", "PRE004", "PRE005", "PRE006", "PRE007", "PRE009", "PRE010", "PRE013", "PRE016" };
            foreach (string id in ids)
            {
                var rule = RuleCatalog.Get(id);
                Assert.IsNotNull(rule, id);
                Assert.AreEqual(FixKind.Verified, rule.FixKind, id + " must remain automatic.");
                Assert.IsTrue(rule.DefaultBackupRequired, id + " performs a destructive/reference-sensitive mutation and requires a backup.");
            }
        }

        [Test]
        public void Pas028IsDiagnosticOnlyAndPreservesStopNansPolicy()
        {
            var rule = RuleCatalog.Get("PAS028");
            Assert.IsNotNull(rule);
            Assert.AreEqual(FixKind.None, rule.FixKind);
            StringAssert.Contains("remain enabled", rule.Fix);
        }


        [Test]
        public void EmptyLeafDetectionOnlyOffersUniqueDefaultNamedUnreferencedLeaves()
        {
            var root = new GameObject("Root");
            try
            {
                var candidate = new GameObject("GameObject");
                candidate.transform.SetParent(root.transform, false);
                var marker = new GameObject("SpawnPoint");
                marker.transform.SetParent(root.transform, false);
                var actual = InvokeAnalyzerHelper<List<string>>("FindSafeEmptyLeafPaths", (object)new[] { root });
                CollectionAssert.AreEqual(new[] { "Root/GameObject" }, actual);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void AnimationPathDependenciesDisableHierarchyRemovalAndFlatteningCandidates()
        {
            var root = new GameObject("Root");
            try
            {
                var candidate = new GameObject("GameObject");
                candidate.transform.SetParent(root.transform, false);
                root.AddComponent<Animator>();
                Assert.AreEqual(0, InvokeAnalyzerHelper<List<string>>("FindSafeEmptyLeafPaths", (object)new[] { root }).Count);
                Assert.AreEqual(0, InvokeAnalyzerHelper<List<string>>("FindSafeFlattenableContainerPaths", (object)new[] { root }).Count);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void SurplusMaterialDetectionRejectsAmbiguousHierarchyPaths()
        {
            var root = new GameObject("Root");
            var meshes = new List<Mesh>();
            Material material = null;
            try
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                Assert.IsNotNull(shader, "A supported opaque fixture shader must be available in the editor test project.");
                material = new Material(shader);
                for (int i = 0; i < 2; i++)
                {
                    var child = new GameObject("Renderer");
                    child.transform.SetParent(root.transform, false);
                    var mesh = new Mesh { name = "Session03TestMesh" };
                    mesh.subMeshCount = 1;
                    meshes.Add(mesh);
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>().sharedMaterials = new[] { material, material };
                }
                var paths = InvokeAnalyzerHelper<List<string>>("FindSurplusMaterialPaths", (object)new[] { root });
                Assert.AreEqual(0, paths.Count, "Duplicate transform paths must be rejected rather than repairing the wrong renderer.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                foreach (var mesh in meshes) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void SurplusMaterialDetectionOnlyAcceptsDuplicateOpaqueTrailingSlots()
        {
            var root = new GameObject("Root");
            var mesh = new Mesh { name = "Session03OpaqueMaterialTestMesh" };
            Material first = null;
            Material different = null;
            Material transparent = null;
            try
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                Assert.IsNotNull(shader, "A supported opaque fixture shader must be available in the editor test project.");
                first = new Material(shader);
                different = new Material(shader);
                transparent = new Material(shader);
                if (transparent.HasProperty("_Surface")) transparent.SetFloat("_Surface", 1f);
                if (transparent.HasProperty("_AlphaClip")) transparent.SetFloat("_AlphaClip", 0f);
                transparent.SetOverrideTag("RenderType", "Transparent");
                transparent.renderQueue = 3000;
                transparent.EnableKeyword("_ALPHABLEND_ON");
                mesh.subMeshCount = 1;
                var child = new GameObject("Renderer");
                child.transform.SetParent(root.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = child.AddComponent<MeshRenderer>();

                renderer.sharedMaterials = new[] { first, first };
                CollectionAssert.AreEqual(new[] { "Root/Renderer" }, InvokeAnalyzerHelper<List<string>>("FindSurplusMaterialPaths", (object)new[] { root }));

                renderer.sharedMaterials = new[] { first, different };
                Assert.IsEmpty(InvokeAnalyzerHelper<List<string>>("FindSurplusMaterialPaths", (object)new[] { root }),
                    "A different extra material may intentionally change the final submesh and must not be removed automatically.");

                renderer.sharedMaterials = new[] { first, transparent };
                Assert.IsEmpty(InvokeAnalyzerHelper<List<string>>("FindSurplusMaterialPaths", (object)new[] { root }),
                    "Transparent material slots must remain diagnostic-only.");

                renderer.sharedMaterials = new[] { first, first };
                var propertyBlock = new MaterialPropertyBlock();
                propertyBlock.SetFloat("_Session03SafetyProbe", 1f);
                renderer.SetPropertyBlock(propertyBlock, 1);
                Assert.IsEmpty(InvokeAnalyzerHelper<List<string>>("FindSurplusMaterialPaths", (object)new[] { root }),
                    "Per-material property blocks can make repeated slots render differently.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (different != null) UnityEngine.Object.DestroyImmediate(different);
                if (transparent != null) UnityEngine.Object.DestroyImmediate(transparent);
            }
        }

        [Test]
        public void ParticleBudgetDetectionRequiresConservativeConstantEmissionWithoutBursts()
        {
            var root = new GameObject("Root");
            try
            {
                var particles = root.AddComponent<ParticleSystem>();
                var main = particles.main;
                main.duration = 5f;
                main.startLifetime = 2f;
                main.maxParticles = 5000;
                var emission = particles.emission;
                emission.enabled = true;
                emission.rateOverTime = 25f;
                emission.rateOverDistance = 0f;
                var safe = (IEnumerable)InvokeAnalyzerHelper("FindSafeParticleBudgetTargets", new object[] { new[] { root }, 500 });
                var targets = new List<object>();
                foreach (var target in safe) targets.Add(target);
                Assert.AreEqual(1, targets.Count);
                Assert.AreEqual("Root", targets[0].GetType().GetField("Path").GetValue(targets[0]));
                Assert.AreEqual(51, targets[0].GetType().GetField("MaxParticles").GetValue(targets[0]));

                // Simulate stale scan evidence: once a curve-driven lifetime replaces the constant
                // lifetime captured above, the fresh preflight helper must no longer return the path.
                main.startLifetime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 2f));
                var staleEvidence = (IEnumerable)InvokeAnalyzerHelper("FindSafeParticleBudgetTargets", new object[] { new[] { root }, 500 });
                Assert.AreEqual(0, staleEvidence.Cast<object>().Count(), "Changed curve evidence must invalidate the old automatic candidate.");

                main.startLifetime = 2f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)5) });
                var burstDriven = (IEnumerable)InvokeAnalyzerHelper("FindSafeParticleBudgetTargets", new object[] { new[] { root }, 500 });
                Assert.AreEqual(0, burstDriven.Cast<object>().Count(), "Burst-driven particle systems must not receive a guessed budget.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void PathsAreAbsentVerifierDetectsBothPreconditionAndCompletedPostcondition()
        {
            string assetPath = "Assets/__Session03_PathsAreAbsentTest_" + Guid.NewGuid().ToString("N") + ".prefab";
            var root = new GameObject("Root");
            try
            {
                var leaf = new GameObject("GameObject");
                leaf.transform.SetParent(root.transform, false);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath, out bool initialSaveSucceeded);
                Assert.IsTrue(initialSaveSucceeded, "Temporary fixture prefab must be saved before verifier checks.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }

            try
            {
                // Capture the same root-qualified hierarchy path that production's Path(Transform) records.
                // Deriving it from the saved prefab avoids assuming Unity preserves a particular root name.
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                Assert.IsNotNull(prefabAsset, "Saved prefab must be reloadable before verifier checks.");
                var prefabTarget = prefabAsset.transform.Find("GameObject");
                Assert.IsNotNull(prefabTarget, "The child target must exist in the saved prefab.");
                string targetPath = InvokeAnalyzerHelper<string>("Path", new object[] { prefabTarget });
                Assert.IsFalse(string.IsNullOrEmpty(targetPath), "Path helper must capture the target.");
                var paths = new List<string> { targetPath };
                Assert.IsFalse(InvokeAnalyzerHelper<bool>("PathsAreAbsent", new object[] { assetPath, false, paths }),
                    "The postcondition must fail while a captured target path still exists.");
                var editable = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    var target = editable.transform.Find("GameObject");
                    Assert.IsNotNull(target);
                    UnityEngine.Object.DestroyImmediate(target.gameObject);
                    PrefabUtility.SaveAsPrefabAsset(editable, assetPath, out bool saved);
                    Assert.IsTrue(saved);
                }
                finally { PrefabUtility.UnloadPrefabContents(editable); }
                Assert.IsTrue(InvokeAnalyzerHelper<bool>("PathsAreAbsent", new object[] { assetPath, false, paths }),
                    "The postcondition should pass only once the captured path is absent.");
            }
            finally { AssetDatabase.DeleteAsset(assetPath); }
        }

        static T InvokeAnalyzerHelper<T>(string name, params object[] args)
        {
            var method = typeof(PrefabSceneAnalyzer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Expected private analyzer helper was not found: " + name);
            try { return (T)method.Invoke(null, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        static object InvokeAnalyzerHelper(string name, object[] args)
        {
            var method = typeof(PrefabSceneAnalyzer).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "Expected private analyzer helper was not found: " + name);
            try { return method.Invoke(null, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
    }
}
