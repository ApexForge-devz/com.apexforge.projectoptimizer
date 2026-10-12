using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace ApexForge.ProjectOptimizer.Tests
{
    public sealed class EvidenceExecutionRegressionTests
    {
        const string TestFolder = "Assets/__ProjectOptimizerEvidenceTests";
        string m_PreviousScanSnapshot;
        string m_PreviousAddressablesSnapshot;

        [SetUp]
        public void SetUp()
        {
            RuleBootstrap.EnsureBuiltInRules();
            m_PreviousScanSnapshot = ReadOptional(ScanResultStore.SnapshotPath);
            m_PreviousAddressablesSnapshot = ReadOptional(CacheFileUtility.CacheFolder + "/AddressablesAnalyze.json");
        }

        [TearDown]
        public void TearDown()
        {
            RestoreOptional(ScanResultStore.SnapshotPath, m_PreviousScanSnapshot);
            RestoreOptional(CacheFileUtility.CacheFolder + "/AddressablesAnalyze.json", m_PreviousAddressablesSnapshot);
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void IncompleteProofBlocksVerifiedFixThroughFixEngine()
        {
            var proof = new ProjectProofIndex();
            proof.StartBuildAsync(new[] { Path.Combine(Directory.GetCurrentDirectory(), "this-root-does-not-exist") }).GetAwaiter().GetResult();
            Assert.IsTrue(proof.CompleteBuild());
            Assert.IsFalse(proof.CoverageComplete);

            bool applied = false;
            var issue = VerifiedIssue(EvidenceRequirement.ProjectProofComplete, EvidenceState.InsufficientSamples, _ => applied = true);
            var entry = new AssetEntry { Path = "Assets/__ProjectOptimizerMissing.asset" };
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);

            Assert.AreEqual(1, report.Blocked);
            Assert.IsFalse(applied);
        }

        [Test]
        public void StaleEvidenceBlocksVerifiedFixThroughFixEngine()
        {
            bool applied = false;
            var issue = VerifiedIssue(EvidenceRequirement.CrossAssetFresh, EvidenceState.Stale, _ => applied = true);
            var entry = new AssetEntry { Path = "Assets/__ProjectOptimizerMissing.asset" };
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);

            Assert.AreEqual(1, report.Blocked);
            Assert.IsFalse(applied);
        }

        [Test]
        public void PersistedSemanticSignatureMismatchRetiresFinding()
        {
            var result = new ScanResult();
            result.Entries.Add(BuildPersistedEntry(FixKind.Verified, EvidenceRequirement.None, EvidenceState.Unconfirmed));
            result.CrossAssetEvidenceComplete = false;
            ScanResultStore.Save(result);

            string json = File.ReadAllText(ScanResultStore.SnapshotPath);
            json = json.Replace("\"RuleCatalogSignature\":\"" + RuleCatalog.SemanticSignature + "\"", "\"RuleCatalogSignature\":\"old-catalog-signature\"");
            File.WriteAllText(ScanResultStore.SnapshotPath, json);

            var restored = ScanResultStore.TryLoad(new ProjectScanner(), OptimizerSettings.Instance);
            Assert.IsNotNull(restored);
            Assert.IsTrue(restored.Entries[0].Issues[0].Stale);
            Assert.AreEqual(FixKind.None, restored.Entries[0].Issues[0].FixKind);
        }

        [Test]
        public void PersistedCrossAssetEvidenceBecomesStaleAfterProjectChange()
        {
            Directory.CreateDirectory(TestFolder);
            string assetPath = TestFolder + "/identity_test.txt";
            File.WriteAllText(assetPath, "before");
            AssetDatabase.Refresh();

            var result = new ScanResult();
            result.CrossAssetEvidenceJournalGeneration = ApexForge.ProjectOptimizer.AssetDatabaseThreadSafe.AssetDatabaseThreadSafeUtility.JournalGeneration;
            result.CrossAssetEvidenceScopeFingerprint = EvidenceContract.ComputeProjectAssetScopeFingerprint();
            result.CrossAssetEvidenceContentIdentity = EvidenceContract.ComputeProjectAssetIdentity();
            result.CrossAssetEvidenceComplete = !string.IsNullOrEmpty(result.CrossAssetEvidenceScopeFingerprint) && !string.IsNullOrEmpty(result.CrossAssetEvidenceContentIdentity);
            var entry = BuildPersistedEntry(FixKind.Verified, EvidenceRequirement.CrossAssetFresh, EvidenceState.Fresh);
            entry.Category = AssetCategory.Duplicate;
            result.Entries.Add(entry);
            ScanResultStore.Save(result);

            File.WriteAllText(assetPath, "after-with-different-length");
            AssetDatabase.Refresh();

            var restored = ScanResultStore.TryLoad(new ProjectScanner(), OptimizerSettings.Instance);
            Assert.IsNotNull(restored);
            Assert.IsTrue(restored.CrossAssetPassesStale);
            Assert.IsTrue(restored.Entries[0].Issues[0].Stale);
        }

        [Test]
        public void PersistedVerifiedFindingBecomesStaleAfterAssetChange()
        {
            Directory.CreateDirectory(TestFolder);
            string assetPath = TestFolder + "/verified_identity_test.txt";
            File.WriteAllText(assetPath, "before");
            AssetDatabase.Refresh();

            var result = new ScanResult();
            var entry = BuildPersistedEntry(FixKind.Verified, EvidenceRequirement.None, EvidenceState.Unconfirmed);
            entry.Path = assetPath;
            entry.Category = AssetCategory.Texture;
            var issue = entry.Issues[0];
            issue.AssetEvidenceSignature = EvidenceContract.ComputeAssetEvidenceSignature(assetPath);
            result.Entries.Add(entry);
            ScanResultStore.Save(result);

            File.WriteAllText(assetPath, "after-with-more-bytes");
            AssetDatabase.Refresh();

            var restored = ScanResultStore.TryLoad(new ProjectScanner(), OptimizerSettings.Instance);
            Assert.IsNotNull(restored);
            Assert.IsTrue(restored.Entries[0].Issues[0].Stale);
            Assert.AreEqual(FixKind.None, restored.Entries[0].Issues[0].FixKind);
        }

        [Test]
        public void CompleteProofCoverageSignatureMatchesLiveIdentity()
        {
            Directory.CreateDirectory(TestFolder);
            File.WriteAllText(TestFolder + "/proof_identity.cs", "class ProofIdentity { }");
            AssetDatabase.Refresh();

            var proof = new ProjectProofIndex();
            string assetsRoot = Path.Combine(Directory.GetCurrentDirectory(), "Assets");
            proof.StartBuildAsync(new[] { assetsRoot }).GetAwaiter().GetResult();
            Assert.IsTrue(proof.CompleteBuild(), proof.BuildError);
            Assert.IsTrue(proof.CoverageComplete, proof.CoverageSummary);
            Assert.IsFalse(string.IsNullOrEmpty(proof.CoverageSignature));
            Assert.IsTrue(EvidenceContract.IsProofEvidenceCurrent(proof.CoverageSignature));

            File.AppendAllText(TestFolder + "/proof_identity.cs", "\nclass ProofIdentityChanged { }");
            Assert.IsFalse(EvidenceContract.IsProofEvidenceCurrent(proof.CoverageSignature));
        }

        [Test]
        public void ExecutionTimeAssetIdentityMismatchBlocksVerifiedFix()
        {
            Directory.CreateDirectory(TestFolder);
            string assetPath = TestFolder + "/execution_identity.txt";
            File.WriteAllText(assetPath, "before");
            AssetDatabase.Refresh();

            bool applied = false;
            var issue = VerifiedIssue(EvidenceRequirement.None, EvidenceState.Fresh, _ => applied = true);
            issue.AssetEvidencePath = assetPath;
            issue.AssetEvidenceSignature = EvidenceContract.ComputeAssetEvidenceSignature(assetPath);
            File.WriteAllText(assetPath, "after");

            var entry = new AssetEntry { Path = assetPath };
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(1, report.Blocked);
            Assert.IsFalse(applied);
        }

        [Test]
        public void PartialAddressablesAnalyzeEvidenceIsNotFresh()
        {
            var snapshot = new AddressablesAnalyzeEvidenceSnapshot
            {
                IsComplete = false,
                EvidenceState = EvidenceState.Stale,
                UnityVersion = UnityEngine.Application.unityVersion,
                Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ProjectFingerprint = EvidenceIdentity.ComputeProjectFingerprint(),
                ContentIdentity = "partial",
                ExpectedRuleCount = 3,
                AttemptedRuleCount = 2,
                SuccessfulRuleCount = 1,
                FailedRuleCount = 1,
                UnavailableRuleCount = 0,
                FreshnessReason = "test partial evidence"
            };
            Directory.CreateDirectory(CacheFileUtility.CacheFolder);
            File.WriteAllText(CacheFileUtility.CacheFolder + "/AddressablesAnalyze.json", UnityEngine.JsonUtility.ToJson(snapshot, true));

            var loaded = AddressablesAnalyzeEvidenceStore.LoadLatest();
            Assert.IsNotNull(loaded);
            Assert.AreEqual(EvidenceState.Stale, loaded.EvidenceState);
            Assert.IsFalse(loaded.IsFresh);
        }

        [Test]
        public void RuntimeEvidenceWithInsufficientSamplesIsNotFresh()
        {
            var snapshot = new RuntimeEvidenceSnapshot
            {
                Version = 6,
                IsComplete = true,
                IsPlayerBuild = true,
                BuildId = "test-build",
                FrameSamples = 1
            };
            Assert.AreEqual(EvidenceState.InsufficientSamples, RuntimeEvidenceStore.EvaluateState(snapshot, out _));
            string source = ReadPackageSource("Editor/Core/Profiling/RuntimeEvidenceStore.cs");
            StringAssert.Contains("summary.result", source);
            StringAssert.Contains("summary.evidenceQuality", source);
            StringAssert.Contains("CaptureCharacteristics", source);
        }

        [Test]
        public void IntegrityAuditBootstrapsBuiltInRules()
        {
            var errors = ProjectOptimizerIntegrityAudit.Run();
            Assert.IsNotNull(errors);
            string joined = string.Join("\n", errors);
            StringAssert.DoesNotContain("Rule catalogue is empty after built-in bootstrap.", joined);
            Assert.IsFalse(errors.Any(error => error.IndexOf("Rule alias", StringComparison.Ordinal) >= 0
                || error.IndexOf("Rule aliases", StringComparison.Ordinal) >= 0), joined);
        }

        [Test]
        public void RetiredProjectRuleIdsResolveToCanonicalRulesWithoutDuplicateCatalogEntries()
        {
            var expected = new Dictionary<string, string>
            {
                { "PRJ077", "PRJ013" },
                { "PRJ078", "PRJ014" },
                { "PRJ081", "PRJ059" },
                { "PAS0031", "PRJ030" }
            };

            foreach (string preservedRule in new[] { "PAS004", "PAS011", "VID006" })
                Assert.IsNotNull(FindRule(preservedRule), preservedRule + " is unrelated to this cleanup and must remain registered.");

            foreach (var pair in expected)
            {
                Assert.IsTrue(RuleCatalog.Exists(pair.Key), pair.Key + " must remain resolvable for old scans/settings");
                Assert.IsTrue(RuleCatalog.IsAlias(pair.Key), pair.Key + " must be a legacy alias, not a separate active rule");
                Assert.AreEqual(pair.Value, RuleCatalog.CanonicalizeId(pair.Key));
                Assert.AreEqual(pair.Value, RuleCatalog.Get(pair.Key)?.Id);
                Assert.IsFalse(RuleCatalog.All.Any(rule => rule.Id == pair.Key), pair.Key + " must not appear as a duplicate active rule");
            }

            // These two former aliases are reused as active IDs; schema migration disambiguates old data.
            Assert.IsNotNull(FindRule("PAS027"), "Current PAS027 is the mobile URP MSAA rule.");
            Assert.IsFalse(RuleCatalog.IsAlias("PAS027"));
            Assert.IsNotNull(FindRule("VID007"), "Current VID007 is the WebGL VideoClip compatibility rule.");
            Assert.IsFalse(RuleCatalog.IsAlias("VID007"));
            Assert.AreEqual("PRJ008", RuleIdMigration.MapFromPreviousSchema("PAS1007"));
            Assert.AreEqual("VID001", RuleIdMigration.MapFromPreviousSchema("VID007"));
        }

        [Test]
        public void LegacyRuleSettingsResolveToCanonicalRuleWithoutLosingSuppressionOrSeverity()
        {
            var settings = new OptimizerSettings();
            settings.disabledRules.Add("PRJ077");
            settings.severityOverrideIds.Add("PRJ078");
            settings.severityOverrideValues.Add((int)IssueSeverity.Critical);
            settings.suppressions.Add(new RuleSuppression
            {
                RuleId = "PRJ081",
                AssetPath = "Assets/LegacySuppression.asset",
                Reason = "legacy suppression",
                CreatedUtc = DateTime.UtcNow.ToString("O")
            });
            typeof(OptimizerSettings).GetMethod("RebuildRuntimeIndexes", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(settings, null);

            Assert.IsFalse(settings.IsRuleEnabled("PRJ013"), "Old PRJ077 disablement must apply to canonical PRJ013.");
            Assert.IsFalse(settings.IsRuleEnabled("PRJ077"), "The retired ID itself must resolve the same enabled state.");
            settings.SetRuleEnabled("PRJ013", true);
            Assert.IsTrue(settings.IsRuleEnabled("PRJ013"));
            Assert.IsFalse(settings.disabledRules.Contains("PRJ077"), "Re-enabling canonical rule should remove its historical alias setting.");

            Assert.AreEqual(IssueSeverity.Critical, settings.SeverityFor(FindRule("PRJ014")), "Legacy PRJ078 severity override must apply to PRJ014.");
            settings.SetSeverityOverride("PRJ014", IssueSeverity.Warning);
            Assert.AreEqual(IssueSeverity.Warning, settings.SeverityFor(FindRule("PRJ014")));
            Assert.IsFalse(settings.severityOverrideIds.Contains("PRJ078"), "New severity writes should be canonical.");

            Assert.IsTrue(settings.IsSuppressed("PRJ059", "Assets/LegacySuppression.asset"));
            Assert.AreEqual("legacy suppression", settings.SuppressionReason("PRJ059", "Assets/LegacySuppression.asset"));
            Assert.IsTrue(settings.Unsuppress("PRJ059", "Assets/LegacySuppression.asset"));
            Assert.IsFalse(settings.IsSuppressed("PRJ059", "Assets/LegacySuppression.asset"));
            Assert.IsTrue(settings.Suppress("PRJ077", "Assets/NewCanonicalSuppression.asset", "canonical write"));
            Assert.AreEqual("PRJ013", settings.suppressions[settings.suppressions.Count - 1].RuleId,
                "New suppressions requested through a legacy ID must be persisted against the canonical rule.");
        }

        [Test]
        public void RuleCatalogSignatureIncludesLegacyAliasMappings()
        {
            var canonical = new List<RuleDef> { FindRule("PRJ013") };
            string withAlias = EvidenceContract.ComputeRuleCatalogSignature(canonical,
                new Dictionary<string, string> { { "PRJ077", "PRJ013" } });
            string withoutAlias = EvidenceContract.ComputeRuleCatalogSignature(canonical,
                new Dictionary<string, string> { { "PRJ078", "PRJ013" } });
            Assert.AreNotEqual(withAlias, withoutAlias, "Changing legacy-ID resolution must stale historical scan snapshots.");
        }

        [Test]
        public void WorkloadDependentRulesRemainReviewOnlyUnlessUserPreferenceTargetsAnotherPolicy()
        {
            // These remain Review-only because they can change asset/runtime compatibility or fidelity.
            // PRJ064 is Verified because its detector is gated to an effective Dedicated Server context.
            string[] reviewOnly = { "PRJ059", "MDL013", "MDL008", "ANI010", "ANI011", "ANI001", "UNU003" };
            foreach (string id in reviewOnly)
            {
                var rule = FindRule(id);
                Assert.IsNotNull(rule, id + " must remain registered");
                Assert.AreNotEqual(FixKind.Verified, rule.FixKind, id + " must not be Verified");
            }
        }

        [Test]
        public void ApprovedSessionRulesAreRegisteredWithVerifiedFixKind()
        {
            // Sessions 03 and 08 approved these rules for automatic fixes and supplied guarded action paths.
            string[] verifiedIds = { "PRE004", "AUD008", "AUD010" };
            foreach (string id in verifiedIds)
            {
                var rule = FindRule(id);
                Assert.IsNotNull(rule, id + " must remain registered.");
                Assert.AreEqual(FixKind.Verified, rule.FixKind, id + " must use the approved Verified catalog policy.");
            }

            // Heuristic recommendations must not be registered as Verified; the catalog confidence
            // floor is a production safety invariant, not a test suppression to work around.
            foreach (string id in new[] { "MDL008", "ANI010", "ANI011" })
            {
                var rule = FindRule(id);
                Assert.IsNotNull(rule, id);
                Assert.AreEqual(FixKind.Review, rule.FixKind, id + " must keep a review gate for runtime-content or fidelity uncertainty.");
                Assert.GreaterOrEqual(rule.Confidence, RuleCatalog.MinimumForReviewFix, id + " must meet the Review confidence floor.");
                Assert.Less(rule.RecommendationConfidence, RuleCatalog.MinimumForAutomaticFix,
                    id + " must not silently become eligible for automatic execution.");
            }
        }

        [Test]
        public void MipmapStreamingFixTargetsAndVerifiesTheReportedQualityLevels()
        {
            string supplement = ReadPackageSource("Editor/Analyzers/ProjectAuditorSupplementAnalyzer.cs");
            string settings = ReadPackageSource("Editor/Analyzers/ProjectSettingsAnalyzer.cs");
            string bootstrap = ReadPackageSource("Editor/Core/Analysis/RuleBootstrap.cs");
            string helper = ReadPackageSource("Editor/Core/Fixes/MipmapStreamingFixUtility.cs");

            StringAssert.Contains("MipmapStreamingFixUtility.EnableForTargetLevels(targetPlatform, expectedIndices, expectedNames)", settings,
                "The canonical PRJ008 action must target the quality levels captured by the finding.");
            StringAssert.Contains("MipmapStreamingFixUtility.AreTargetLevelsEnabled(targetPlatform, expectedIndices, expectedNames)", settings);
            StringAssert.Contains("AddMipmapStreamingAggregateIssue(entry, ctx, platformName, activeLevels, editorFallback)", settings,
                "PRJ008 must emit one target-aware finding for all disabled levels in the selected target profile.");
            StringAssert.Contains("GetArrayElementAtIndex(expectedIndices[i])", helper);
            StringAssert.Contains("activeIndices.Contains(index)", helper);
            StringAssert.Contains("string.Equals(currentName, expectedNames[i], StringComparison.Ordinal)", helper);
            StringAssert.Contains("ApplyModifiedProperties()", helper);
            StringAssert.Contains("EditorUtility.SetDirty(qualityObject)", helper);
            StringAssert.DoesNotContain("RuleCatalog.RegisterAlias(\"PAS027\"", bootstrap);
            StringAssert.DoesNotContain("Rules.Register(id: \"PAS027\"", supplement);
            StringAssert.DoesNotContain("AddIssue(\"PAS027\"", supplement);
            StringAssert.DoesNotContain("AnalyzeMipmapStreaming", supplement);
            StringAssert.DoesNotContain("QualitySettings.streamingMipmapsActive = true", settings,
                "Deferred fixes must not write whichever Quality Level happens to be active at fix time.");
        }

        [Test]
        public void PhaseTwoOverlappingFindingsAreConsolidatedWithoutRemovingSpecificCoverage()
        {
            string video = ReadPackageSource("Editor/Analyzers/VideoAnalyzer.cs");
            string shader = ReadPackageSource("Editor/Analyzers/MaterialShaderAnalyzer.cs");
            string animation = ReadPackageSource("Editor/Analyzers/AnimationAnalyzer.cs");
            string sharedModel = ReadPackageSource("Editor/Core/Analysis/ModelAnalysisShared.cs");
            string projectSettings = ReadPackageSource("Editor/Analyzers/ProjectSettingsAnalyzer.cs");
            string prefab = ReadPackageSource("Editor/Analyzers/PrefabSceneAnalyzer.cs");
            string audio = ReadPackageSource("Editor/Analyzers/AudioAnalyzer.cs");
            string texture = ReadPackageSource("Editor/Analyzers/TextureAnalyzer.cs");
            string supplement = ReadPackageSource("Editor/Analyzers/ProjectAuditorSupplementAnalyzer.cs");

            StringAssert.Contains("Rules.Register(id: \"VID007\", title: \"VideoClip is unsupported on WebGL\"", video);
            StringAssert.Contains("entry.AddIssue(\"VID007\"", video);
            StringAssert.Contains("ActionId = \"VID001_SpatialQuality_\"", video);
            StringAssert.Contains("resolutionIssue.Options.Add(option)", video);
            StringAssert.DoesNotContain("entry.AddIssue(\"SHD011\"", shader);
            StringAssert.Contains("mdl015OwnsSkinnedModelOverlap", animation);
            StringAssert.Contains("GetComponentsInChildren<SkinnedMeshRenderer>(true)", sharedModel);
            StringAssert.Contains("activeBuildTarget != PlatformUtility.ToBuildTarget(settings.targetPlatform)", projectSettings);
            // PAS023 intentionally inspects the active target only when the selected scan target differs.
            StringAssert.Contains("var activeNamedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(activeBuildTarget))", projectSettings);
            StringAssert.Contains("PlayerSettings.GetIl2CppCompilerConfiguration(activeNamedTarget)", projectSettings);
            StringAssert.Contains("FilterGenericCcdBodies(facts, settings, entry.Path)", prefab);
            StringAssert.Contains("SetCollisionMode(c, isScene, CollisionDetectionMode.Discrete, continuousBodyPaths)", prefab);
            StringAssert.Contains("CountBodiesNotMode(c.AssetPath, isScene, CollisionDetectionMode.Discrete, continuousBodyPaths)", prefab);
            StringAssert.Contains("bool canSafelyTargetCcdBodies = allContinuousBodyPaths.All", prefab);
            StringAssert.Contains("options: canSafelyTargetCcdBodies ? options : null", prefab);
            StringAssert.Contains("No safe batch fix until each Rigidbody path is unambiguous", prefab);
            StringAssert.Contains("If duplicate sibling names collapse two bodies", prefab);
            StringAssert.Contains("aud001Issue.Note", audio);
            StringAssert.Contains("facts.overrideDefeatsStreamingPolicy && isLong && !IsWebGLPlatform(platform)", audio);
            StringAssert.Contains("targetExplicitMobileUncompressedOverride", texture);
            StringAssert.DoesNotContain("Rules.Register(id: \"PAS0031\"", supplement);
            StringAssert.DoesNotContain("AddIssue(\"PAS0031\"", supplement);
        }

        [Test]
        public void PhaseThreeGroupedInsightsPreserveSeparateMetricsAndRulePolicy()
        {
            string analysis = ReadPackageSource("Editor/Core/Analysis/Analysis.cs");
            string build = ReadPackageSource("Editor/Analyzers/ProjectAuditorSupplementAnalyzer.cs");
            string fonts = ReadPackageSource("Editor/Analyzers/FontAnalyzer.cs");
            string tmp = ReadPackageSource("Editor/Analyzers/TextMeshProAnalyzer.cs");
            string shader = ReadPackageSource("Editor/Analyzers/MaterialShaderAnalyzer.cs");
            string duplicate = ReadPackageSource("Editor/Analyzers/DuplicateAnalyzer.cs");
            string animation = ReadPackageSource("Editor/Analyzers/AnimationAnalyzer.cs");

            StringAssert.Contains("GroupedFindingPolicy.AddGroupedIssue(entry, metrics", build);
            StringAssert.Contains("new GroupedFindingMetric(\"BLD001\"", build);
            StringAssert.Contains("new GroupedFindingMetric(\"BLD002\"", build);
            StringAssert.Contains("new GroupedFindingMetric(\"FNT001\"", fonts);
            StringAssert.Contains("new GroupedFindingMetric(\"FNT007\"", fonts);
            StringAssert.Contains("GroupedFindingPolicy.CanReport(\"FNT006\", entry.Path)", fonts);
            StringAssert.Contains("specificMissingSourceWillShow", fonts);
            StringAssert.Contains("FontAnalysisPolicy.AtlasSizeBudget(ctx.Settings)", tmp);
            StringAssert.Contains("FontAnalysisPolicy.FallbackCountBudget(ctx.Settings)", tmp);
            StringAssert.Contains("FontAnalysisPolicy.MultipleAtlasPagesExplanation", fonts);
            StringAssert.Contains("FontAnalysisPolicy.MultipleAtlasPagesExplanation", tmp);
            StringAssert.Contains("GroupedFindingPolicy.AddGroupedIssue(entry, metrics", shader);
            StringAssert.Contains("new GroupedFindingMetric(\"SHD002\"", shader);
            StringAssert.Contains("new GroupedFindingMetric(\"SHD010\"", shader);
            StringAssert.Contains("new GroupedFindingMetric(\"SHD013\"", shader);
            StringAssert.Contains("entry.AddIssue(\"SHD003\"", shader);
            StringAssert.Contains("entry.AddIssue(\"SHD006\"", shader);
            StringAssert.Contains("entry.AddIssue(\"SHD007\"", shader);
            StringAssert.Contains("entry.AddIssue(\"SHD008\"", shader);
            StringAssert.Contains("entry.AddIssue(\"SHD009\"", shader);
            StringAssert.Contains("do not add these numbers together", shader);
            StringAssert.DoesNotContain("entry.AddIssue(\"SHD013\"", shader,
                "SHD013 must contribute a measurement to the grouped shader finding rather than emit a second card.");
            StringAssert.Contains("CorrelateByteIdenticalMaterialFinding(ctx, path, byteIdenticalPaths, duplicateIssue)", duplicate);
            StringAssert.Contains("if (!byteIdenticalPaths.Contains(materialPath)) return;", duplicate);
            StringAssert.Contains("Render-equivalent files that differ byte-for-byte remain independent findings", duplicate);
            StringAssert.Contains("Import-stage path", animation);
            StringAssert.Contains("Clip-data path", animation);
            StringAssert.Contains("!CanReport(metric.RuleId, entry.Path)", analysis,
                "Grouped metrics must honor each member rule's enabled/suppression policy.");
        }

        [Test]
        public void VerifiedRulesDeclareRecommendationAndSemanticContracts()
        {
            foreach (var rule in RuleCatalog.All)
            {
                if (rule == null || rule.FixKind != FixKind.Verified) continue;
                Assert.GreaterOrEqual(rule.RecommendationConfidence, RuleCatalog.MinimumForAutomaticFix, rule.Id);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.SemanticVersion), rule.Id);
                Assert.IsTrue(rule.RequiredEvidence != EvidenceRequirement.None || rule.Confidence >= RuleCatalog.MinimumForAutomaticFix, rule.Id + " must have a deterministic evidence path or explicit automatic confidence");
            }
        }

        [Test]
        public void MDL013UsesExactMappingVerifierContract()
        {
            string source = ReadPackageSource("Editor/Analyzers/ModelAnalyzer.cs");
            StringAssert.Contains("SourceAssetIdentifier", source);
            StringAssert.Contains("GetExternalObjectMap", source);
            StringAssert.Contains("VerifyMaterialMappingExact", source);
            var rule = FindRule("MDL013");
            Assert.IsNotNull(rule);
            Assert.AreEqual(FixKind.Review, rule.FixKind);
        }

        static OptimizationIssue VerifiedIssue(EvidenceRequirement requirement, EvidenceState state, Action<FixContext> apply)
        {
            return new OptimizationIssue
            {
                RuleId = "TEST001",
                FixKind = FixKind.Verified,
                Confidence = OptimizationConfidence.HighConfidence,
                RecommendationConfidence = OptimizationConfidence.HighConfidence,
                RequiredEvidence = requirement,
                EvidenceState = state,
                RuleCatalogSignature = RuleCatalog.SemanticSignature,
                AnalyzerSignature = EvidenceContract.AnalyzerSignature,
                FixContractSignature = EvidenceContract.FixSignature,
                Fix = apply,
                Verify = _ => true,
                Risk = FixRisk.Low
            };
        }

        static AssetEntry BuildPersistedEntry(FixKind kind, EvidenceRequirement requirement, EvidenceState state)
        {
            var entry = new AssetEntry { Path = "Assets/__ProjectOptimizerPersisted.asset", Category = AssetCategory.Other, Name = "Persisted" };
            entry.AddIssue("TEX003", "test", "test", 0, _ => { }, "test", SavingsBasis.Unknown, _ => true);
            var issue = entry.Issues[entry.Issues.Count - 1];
            issue.FixKind = kind;
            issue.Confidence = OptimizationConfidence.HighConfidence;
            issue.RecommendationConfidence = OptimizationConfidence.HighConfidence;
            issue.RequiredEvidence = requirement;
            issue.EvidenceState = state;
            issue.RuleCatalogSignature = RuleCatalog.SemanticSignature;
            issue.AnalyzerSignature = EvidenceContract.AnalyzerSignature;
            issue.FixContractSignature = EvidenceContract.FixSignature;
            return entry;
        }

        static RuleDef FindRule(string id)
        {
            foreach (var rule in RuleCatalog.All) if (rule != null && string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase)) return rule;
            return null;
        }

        static string ReadPackageSource(string relativePath)
        {
            string root = ProjectOptimizerConstants.PackageRoot;
            Assert.IsFalse(string.IsNullOrEmpty(root), "The ProjectOptimizer package root could not be resolved.");
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "Package source file is missing: " + path);
            return File.ReadAllText(path);
        }

        static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        static void RestoreOptional(string path, string content)
        {
            if (content == null)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }
    }
}
