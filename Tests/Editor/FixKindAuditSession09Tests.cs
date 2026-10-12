using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    public sealed class FixKindAuditSession09Tests
    {
        [SetUp]
        public void SetUp() => RuleBootstrap.EnsureBuiltInRules();

        [Test]
        public void Session09DeclaredFixKindsMatchImplementedActionsAndGuardrails()
        {
            var expected = new Dictionary<string, FixKind>
            {
                { "CTL001", FixKind.None },
                { "CTL002", FixKind.None },
                { "CTL003", FixKind.Verified },
                { "CTL004", FixKind.None },
                { "CTL005", FixKind.Review },
                { "CTL006", FixKind.None },
                { "CTL007", FixKind.Review },
                { "DEP001", FixKind.None },
                { "DEP002", FixKind.None },
                { "FNT001", FixKind.None },
                { "FNT002", FixKind.None },
                { "FNT003", FixKind.None },
                { "FNT004", FixKind.None },
                { "FNT005", FixKind.None },
                { "FNT006", FixKind.Review },
                { "FNT007", FixKind.None },
                { "FNT008", FixKind.Verified },
                { "TMP001", FixKind.None },
                { "TMP002", FixKind.None },
                { "TMP003", FixKind.Verified },
                { "TMP004", FixKind.None }
            };

            Assert.AreEqual(21, expected.Count);
            foreach (var pair in expected)
            {
                var definition = RuleCatalog.Get(pair.Key);
                Assert.IsNotNull(definition, pair.Key + " must remain registered.");
                Assert.AreEqual(pair.Value, definition.FixKind, pair.Key + " declared FixKind must match its implemented action state.");
            }
        }


        [Test]
        public void CTL003TreatsDynamicAnimatorParameterArgumentsAsAnException()
        {
            var method = typeof(AnimatorControllerAnalyzer).GetMethod("HasDynamicAnimatorParameterAccess", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "CTL003 must retain its dynamic-usage exception guard.");
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "animator.SetFloat(\"Speed\", value);" }),
                "A literal parameter name is separately checked by whole-source identifier scanning and is not a dynamic-argument exception.");
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "animator.SetFloat(parameterName, value);" }),
                "A runtime-computed parameter name cannot be proven unrelated to the analyzed controller.");
        }

        [TestCase(typeof(FontAnalyzer))]
        [TestCase(typeof(TextMeshProAnalyzer))]
        public void FallbackPruningFindsNullDuplicatesAndStaticGlyphSubsetsOnly(Type analyzerType)
        {
            var root = ScriptableObject.CreateInstance<Session09FallbackTableFixture>();
            var subset = ScriptableObject.CreateInstance<Session09FallbackTableFixture>();
            var extra = ScriptableObject.CreateInstance<Session09FallbackTableFixture>();
            try
            {
                root.atlasPopulationMode = "Static";
                root.characterTable = new List<Session09CharacterFixture> { new Session09CharacterFixture(65), new Session09CharacterFixture(66) };
                subset.atlasPopulationMode = "Static";
                subset.characterTable = new List<Session09CharacterFixture> { new Session09CharacterFixture(65) };
                extra.atlasPopulationMode = "Static";
                extra.characterTable = new List<Session09CharacterFixture> { new Session09CharacterFixture(90) };
                root.SetFallbacks(null, subset, extra, subset);

                var method = analyzerType.GetMethod("FindRedundantFallbackIndices", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.IsNotNull(method, analyzerType.Name + " must keep its guarded fallback analysis helper.");
                var indices = (List<int>)method.Invoke(null, new object[] { root, "m_FallbackFontAssetTable" });
                CollectionAssert.AreEqual(new[] { 0, 1, 3 }, indices,
                    "Only null, duplicate, and later static glyph-subset entries should be pruned.");

                root.atlasPopulationMode = "Dynamic";
                root.SetFallbacks(subset);
                indices = (List<int>)method.Invoke(null, new object[] { root, "m_FallbackFontAssetTable" });
                CollectionAssert.IsEmpty(indices, "Dynamic glyph tables cannot justify subset pruning.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(subset);
                UnityEngine.Object.DestroyImmediate(extra);
            }
        }

        [TestCase("CTL005")]
        [TestCase("CTL007")]
        [TestCase("FNT006")]
        public void Session09HighRiskReviewRulesRequireBackup(string ruleId)
        {
            var definition = RuleCatalog.Get(ruleId);
            Assert.IsNotNull(definition, ruleId + " must be registered.");
            Assert.AreEqual(FixKind.Review, definition.FixKind);
            Assert.GreaterOrEqual((int)definition.DefaultFixRisk, (int)FixRisk.High, ruleId + " must remain high risk.");
            Assert.IsTrue(definition.DefaultBackupRequired, ruleId + " must require backup.");
        }

        [TestCase("FNT001")]
        [TestCase("FNT002")]
        [TestCase("FNT003")]
        [TestCase("FNT004")]
        [TestCase("FNT005")]
        [TestCase("FNT007")]
        [TestCase("TMP001")]
        [TestCase("TMP002")]
        [TestCase("TMP004")]
        public void FontGenerationTargetsDoNotClaimVerifiedWithoutGenerationTransaction(string ruleId)
        {
            var definition = RuleCatalog.Get(ruleId);
            Assert.IsNotNull(definition, ruleId + " must remain registered.");
            Assert.AreEqual(FixKind.None, definition.FixKind,
                ruleId + " must not be declared Verified until candidate generation, rollback and glyph/render verification exist.");
        }
    }

    public sealed class Session09FallbackTableFixture : ScriptableObject
    {
        [SerializeField] List<Session09FallbackTableFixture> m_FallbackFontAssetTable = new List<Session09FallbackTableFixture>();

        public string atlasPopulationMode { get; set; } = "Static";
        public List<Session09CharacterFixture> characterTable { get; set; } = new List<Session09CharacterFixture>();

        public void SetFallbacks(params Session09FallbackTableFixture[] fallbacks)
        {
            m_FallbackFontAssetTable = fallbacks == null
                ? new List<Session09FallbackTableFixture>()
                : new List<Session09FallbackTableFixture>(fallbacks);
        }
    }

    public sealed class Session09CharacterFixture
    {
        public Session09CharacterFixture(uint unicode) => this.unicode = unicode;
        public uint unicode { get; private set; }
    }
}
