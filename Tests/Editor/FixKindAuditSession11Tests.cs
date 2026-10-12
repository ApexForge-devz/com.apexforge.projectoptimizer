using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>
    /// Session 11 regression coverage for the approved FixKind policy and the concrete ATL005
    /// mutation path. These tests are intentionally session-local so parallel audit merges do not
    /// conflict with the shared test fixtures.
    /// </summary>
    public sealed class FixKindAuditSession11Tests
    {
        static readonly string[] k_AutomaticAtlasRules = { "ATL002", "ATL004", "ATL005", "ATL007" };
        readonly List<string> m_TemporaryAssets = new List<string>();
        OptimizerSettings m_Settings;
        PropertyInfo m_BackgroundSettingsProperty;
        object m_PreviousBackgroundSettings;
        bool m_OverrodeBackgroundSettings;

        [SetUp]
        public void SetUp()
        {
            RuleBootstrap.EnsureBuiltInRules();
            m_Settings = new OptimizerSettings();
            typeof(OptimizerSettings).GetMethod("RebuildRuntimeIndexes", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(m_Settings, null);
            foreach (var ruleId in new[] { "ATL002", "ATL004", "ATL005", "ATL007" })
                m_Settings.SetRuleEnabled(ruleId, true);
            SetBackgroundRuleSettings(m_Settings);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_TemporaryAssets.Count - 1; i >= 0; i--)
                AssetDatabase.DeleteAsset(m_TemporaryAssets[i]);
            m_TemporaryAssets.Clear();
            RestoreBackgroundRuleSettings();
            AssetDatabase.Refresh();
        }

        [TestCase("AND001", FixKind.None)]
        [TestCase("AND002", FixKind.None)]
        [TestCase("AND003", FixKind.Review)]
        [TestCase("ATL001", FixKind.Review)]
        [TestCase("ATL002", FixKind.Verified)]
        [TestCase("ATL003", FixKind.Review)]
        [TestCase("ATL004", FixKind.Verified)]
        [TestCase("ATL005", FixKind.Verified)]
        [TestCase("ATL006", FixKind.Review)]
        [TestCase("ATL007", FixKind.Verified)]
        [TestCase("DUP001", FixKind.Verified)]
        [TestCase("DUP002", FixKind.None)]
        [TestCase("PAA003", FixKind.None)]
        [TestCase("REN001", FixKind.None)]
        [TestCase("REN002", FixKind.None)]
        [TestCase("REN003", FixKind.None)]
        [TestCase("REN004", FixKind.None)]
        [TestCase("REN005", FixKind.None)]
        [TestCase("REN006", FixKind.None)]
        [TestCase("UITK001", FixKind.None)]
        [TestCase("UITK002", FixKind.None)]
        [TestCase("UITK003", FixKind.None)]
        [TestCase("UITK004", FixKind.None)]
        public void CatalogMatchesApprovedSession11Targets(string ruleId, FixKind expected)
        {
            var rule = RuleCatalog.Get(ruleId);
            Assert.IsNotNull(rule, "Canonical rule registration missing: " + ruleId);
            Assert.AreEqual(expected, rule.FixKind, ruleId);
        }

        [Test]
        public void AgpReviewRuleDeclaresHighRiskAndRequiresBackup()
        {
            var rule = RuleCatalog.Get("AND003");
            Assert.IsNotNull(rule);
            Assert.AreEqual(FixKind.Review, rule.FixKind);
            Assert.AreEqual(FixRisk.High, rule.DefaultFixRisk);
            Assert.IsTrue(rule.DefaultBackupRequired, "The AGP template mutation must be backed up.");
        }

        [Test]
        public void AutomaticAtlasRulesMeetFixEngineContractAndRequireBackup()
        {
            foreach (var ruleId in k_AutomaticAtlasRules)
            {
                var rule = RuleCatalog.Get(ruleId);
                Assert.IsNotNull(rule, ruleId);
                Assert.AreEqual(FixKind.Verified, rule.FixKind, ruleId);
                Assert.GreaterOrEqual(rule.Confidence, RuleCatalog.MinimumForAutomaticFix, ruleId + " detection confidence");
                Assert.GreaterOrEqual(rule.RecommendationConfidence, RuleCatalog.MinimumForAutomaticFix, ruleId + " recommendation confidence");
                Assert.IsTrue(rule.DefaultBackupRequired, ruleId + " mutates a serialized Sprite Atlas and must be backed up");
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.SemanticVersion), ruleId + " needs a semantic version");
            }
        }

        [Test]
        public void EmptyIncludedAtlasHasExecutableVerifiedFixAndPassesPostcondition()
        {
            string path = CreateEmptyAtlas("__po_session11_empty_atlas.spriteatlas");
            SetAtlasIncludedInBuild(path, true);
            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL005");

            Assert.IsNotNull(issue, "An empty, included, unmarked master atlas should be reported.");
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix, "Verified ATL005 must carry a default action.");
            Assert.IsNotNull(issue.Verify, "Verified ATL005 must carry a postcondition verifier.");
            Assert.AreEqual(0, issue.Options.Count, "Verified fixes must not depend on Review option selection.");

            var context = CreateContext(path);
            issue.Fix(context);
            Commit(context);
            Assert.IsTrue(issue.Verify(CreateContext(path)), "The reloaded atlas should confirm build inclusion is disabled.");
            Assert.IsFalse(IsAtlasIncludedInBuild(path), "The actual include-in-build state should be false.");
        }

        [Test]
        public void ExplicitPlaceholderMarkerExcludesEmptyAtlasFromAutomaticPath()
        {
            string path = CreateEmptyAtlas("__po_session11_placeholder_atlas.spriteatlas");
            SetAtlasIncludedInBuild(path, true);

            var entry = Analyze(path);
            Assert.IsNull(FindIssue(entry, "ATL005"), "Marked placeholder atlases must not be auto-excluded from builds.");
            Assert.IsTrue(IsAtlasIncludedInBuild(path), "The exception path must not mutate build inclusion.");
        }

        [Test]
        public void StaleAtlasEvidenceBlocksTheVerifiedFix()
        {
            string path = CreateEmptyAtlas("__po_session11_stale_atlas.spriteatlas");
            SetAtlasIncludedInBuild(path, true);
            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL005");
            Assert.IsNotNull(issue);

            // Change and persist the target after analysis. FixEngine must refuse the stale evidence.
            SetAtlasIncludedInBuild(path, false);
            var report = FixEngine.Apply(
                new[] { new FixRequest { Entry = entry, Issue = issue } },
                createBackup: true,
                mode: FixExecutionMode.VerifiedSingle);

            Assert.Greater(report.Blocked, 0, "A changed atlas must not be mutated from stale evidence.");
            Assert.IsFalse(IsAtlasIncludedInBuild(path), "The attempted fix must not have changed the state after the external edit.");
        }

        [Test]
        public void WrongTargetPathIsRejectedWithoutChangingOriginalAtlas()
        {
            string path = CreateEmptyAtlas("__po_session11_wrong_target_atlas.spriteatlas");
            SetAtlasIncludedInBuild(path, true);
            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL005");
            Assert.IsNotNull(issue);
            entry.Path = "Assets/__po_session11_unrelated_target.spriteatlas";

            var report = FixEngine.Apply(
                new[] { new FixRequest { Entry = entry, Issue = issue } },
                createBackup: true,
                mode: FixExecutionMode.VerifiedSingle);

            Assert.Greater(report.Blocked + report.Failed, 0,
                "The evidence path and request target must match. Error report: " + string.Join("\n", report.Errors));
            Assert.IsTrue(IsAtlasIncludedInBuild(path), "The original atlas must remain untouched after wrong-target rejection.");
        }

        [Test]
        public void FailedPostconditionRollsBackEmptyAtlasMutation()
        {
            string path = CreateEmptyAtlas("__po_session11_rollback_atlas.spriteatlas");
            SetAtlasIncludedInBuild(path, true);
            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL005");
            Assert.IsNotNull(issue);
            issue.Verify = _ => false; // Deliberately simulate a failed postcondition.

            var report = FixEngine.Apply(
                new[] { new FixRequest { Entry = entry, Issue = issue } },
                createBackup: true,
                mode: FixExecutionMode.VerifiedSingle);

            Assert.Greater(report.RolledBack, 0, "A failed postcondition must not be reported as an applied fix.");
            Assert.AreEqual(0, report.RollbackFailed, string.Join("\n", report.Errors));
            Assert.IsTrue(IsAtlasIncludedInBuild(path), "Rollback should restore the pre-fix include-in-build state.");

            // Delete only this test's backup folder, and only after the transaction confirms rollback.
            if (report.RolledBack > 0 && report.RollbackFailed == 0 && !string.IsNullOrEmpty(report.BackupFolder)
                && Directory.Exists(report.BackupFolder))
                Directory.Delete(report.BackupFolder, true);
        }


        [Test]
        public void CompressionAutoFixSetsPlatformAutomaticNormalAndVerifiesResult()
        {
            string path = CreatePopulatedAtlas("__po_session11_compression_atlas.spriteatlas", "__po_session11_compression_sprite.png");
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            Assert.IsNotNull(atlas);
            string platform = PlatformUtility.GetAnalyzerTargetPlatform(new ScanContext { Settings = m_Settings }, "DefaultTexturePlatform");
            SetPlatformCompression(atlas, platform, TextureImporterCompression.Uncompressed);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL002");
            Assert.IsNotNull(issue, "An uncompressed populated atlas should be reported for the selected target platform.");
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix);
            Assert.IsNotNull(issue.Verify);
            Assert.AreEqual(0, issue.Options.Count);

            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, createBackup: true, mode: FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(1, report.AppliedVerified, string.Join("\n", report.Errors));
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            var applied = atlas.GetPlatformSettings(platform);
            Assert.IsTrue(applied.overridden);
            Assert.AreEqual(TextureImporterCompression.Compressed, applied.textureCompression);
            Assert.AreEqual(TextureImporterFormat.Automatic, applied.format);
            Assert.AreEqual((int)TextureCompressionQuality.Normal, applied.compressionQuality);
            Assert.IsFalse(applied.crunchedCompression);
            CleanupBackup(report);
        }

        [Test]
        public void CompressionSensitiveMarkerExcludesAtlasFromAutomaticPath()
        {
            string path = CreatePopulatedAtlas("__po_session11_lossless_atlas.spriteatlas", "__po_session11_lossless_sprite.png");
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            string platform = PlatformUtility.GetAnalyzerTargetPlatform(new ScanContext { Settings = m_Settings }, "DefaultTexturePlatform");
            SetPlatformCompression(atlas, platform, TextureImporterCompression.Uncompressed);
            AssetDatabase.SaveAssets();
            Assert.IsNull(FindIssue(Analyze(path), "ATL002"), "Explicit lossless marker must keep this atlas out of the common-case compressor.");
        }

        [Test]
        public void TightPackingAutoFixEnablesOnlyTightPacking()
        {
            string path = CreatePopulatedAtlas("__po_session11_tight_atlas.spriteatlas", "__po_session11_tight_sprite.png");
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            var packing = atlas.GetPackingSettings();
            packing.enableTightPacking = false;
            packing.enableRotation = false;
            atlas.SetPackingSettings(packing);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL004");
            Assert.IsNotNull(issue, "A populated atlas with Tight Packing disabled should be reported.");
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix);
            Assert.IsNotNull(issue.Verify);
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, createBackup: true, mode: FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(1, report.AppliedVerified, string.Join("\n", report.Errors));
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            Assert.IsTrue(atlas.GetPackingSettings().enableTightPacking);
            Assert.IsFalse(atlas.GetPackingSettings().enableRotation, "The fix must not change rotation.");
            CleanupBackup(report);
        }

        [Test]
        public void BleedSensitiveMarkerExcludesAtlasFromPaddingAutoFix()
        {
            string path = CreatePopulatedAtlas("__po_session11_bleed-sensitive_atlas.spriteatlas", "__po_session11_bleed_sprite.png");
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            var packing = atlas.GetPackingSettings();
            packing.padding = 16;
            atlas.SetPackingSettings(packing);
            var textureSettings = atlas.GetTextureSettings();
            textureSettings.generateMipMaps = false;
            atlas.SetTextureSettings(textureSettings);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Assert.IsNull(FindIssue(Analyze(path), "ATL007"), "Bleed-sensitive marker must opt out of automatic padding reduction.");
        }

        [Test]
        public void PaddingAutoFixReducesToEightAndNeverToFour()
        {
            string path = CreatePopulatedAtlas("__po_session11_padding_atlas.spriteatlas", "__po_session11_padding_sprite.png");
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            var packing = atlas.GetPackingSettings();
            packing.padding = 16;
            atlas.SetPackingSettings(packing);
            var textureSettings = atlas.GetTextureSettings();
            textureSettings.generateMipMaps = false;
            atlas.SetTextureSettings(textureSettings);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var entry = Analyze(path);
            var issue = FindIssue(entry, "ATL007");
            Assert.IsNotNull(issue, "A populated non-mipmapped atlas with padding over 8 px should be reported.");
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix);
            Assert.IsNotNull(issue.Verify);
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, createBackup: true, mode: FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(1, report.AppliedVerified, string.Join("\n", report.Errors));
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            Assert.AreEqual(8, atlas.GetPackingSettings().padding);
            CleanupBackup(report);
        }

        [TestCase("6000.0.44f1", "")]
        [TestCase("6000.0.45f1", "8.7.2")]
        [TestCase("6000.0.60f1", "8.7.2")]
        [TestCase("6000.0.61f1", "8.10.0")]
        [TestCase("6000.0.79f1", "9.0.0")]
        [TestCase("6000.0.85f1", "9.1.1")]
        public void AgpUpgradeOnlyUsesDocumentedUnity6000CompatibilityBands(string unityVersion, string expected)
        {
            var method = typeof(AndroidCompatibilityAnalyzer).GetMethod("GetUnityCompatibleAgpVersion", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            Assert.AreEqual(expected, method.Invoke(null, new object[] { unityVersion }));
        }

        AssetEntry Analyze(string path)
        {
            return new SpriteAtlasAnalyzer().Analyze(path, new ScanContext { Settings = m_Settings });
        }

        static OptimizationIssue FindIssue(AssetEntry entry, string ruleId)
        {
            if (entry?.Issues == null) return null;
            for (int i = 0; i < entry.Issues.Count; i++)
                if (string.Equals(entry.Issues[i].RuleId, ruleId, StringComparison.Ordinal)) return entry.Issues[i];
            return null;
        }


        string CreatePopulatedAtlas(string atlasFileName, string spriteFileName)
        {
            string spritePath = "Assets/" + spriteFileName;
            AssetDatabase.DeleteAsset(spritePath);
            byte[] png;
            var texture = new Texture2D(16, 16, TextureFormat.ARGB32, false, true);
            try
            {
                var pixels = new Color32[16 * 16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 64, 32, 255);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                png = ImageConversion.EncodeToPNG(texture);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            string absoluteSpritePath = Path.Combine(Directory.GetCurrentDirectory(), spritePath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(absoluteSpritePath, png);
            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var textureImporter = AssetImporter.GetAtPath(spritePath) as TextureImporter;
            Assert.IsNotNull(textureImporter);
            textureImporter.textureType = TextureImporterType.Sprite;
            textureImporter.spriteImportMode = SpriteImportMode.Single;
            textureImporter.mipmapEnabled = false;
            textureImporter.textureCompression = TextureImporterCompression.Uncompressed;
            textureImporter.crunchedCompression = false;
            textureImporter.alphaIsTransparency = false;
            textureImporter.SaveAndReimport();
            var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
            Assert.IsNotNull(importedTexture, "Fixture texture failed to import.");
            Assert.Greater(importedTexture.width, 0, "Fixture texture has no valid imported dimensions.");
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
            Assert.IsNotNull(sprite, "Fixture sprite failed to import.");
            m_TemporaryAssets.Add(spritePath);

            string atlasPath = CreateEmptyAtlas(atlasFileName);
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            Assert.IsNotNull(atlas);
            SpriteAtlasExtensions.Add(atlas, new UnityEngine.Object[] { sprite });
            var textureSettings = atlas.GetTextureSettings();
            textureSettings.generateMipMaps = false;
            atlas.SetTextureSettings(textureSettings);

            // Avoid platform settings whose serialized format is unset (0), which makes Unity's
            // packer emit "Image invalid format!" before atlas analyzer assertions can execute.
            SetExplicitPackFormat(atlas, "DefaultTexturePlatform");
            string activePlatform = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)).TargetName;
            SetExplicitPackFormat(atlas, activePlatform);
            string selectedPlatform = PlatformUtility.GetAnalyzerTargetPlatform(
                new ScanContext { Settings = m_Settings }, "DefaultTexturePlatform");
            if (!string.IsNullOrEmpty(selectedPlatform)) SetExplicitPackFormat(atlas, selectedPlatform);

            AssetDatabase.SaveAssets();
            SpriteAtlasUtility.PackAtlases(new[] { atlas }, EditorUserBuildSettings.activeBuildTarget, false);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            Assert.IsNotNull(atlas, "Packed Sprite Atlas fixture could not be reloaded.");
            Assert.Greater(atlas.spriteCount, 0, "Sprite Atlas fixture must contain packed sprite content.");
            return atlasPath;
        }

        static void SetExplicitPackFormat(SpriteAtlas atlas, string platform)
        {
            if (atlas == null || string.IsNullOrWhiteSpace(platform)) return;
            var settings = atlas.GetPlatformSettings(platform);
            settings.name = platform;
            settings.overridden = true;
            settings.textureCompression = TextureImporterCompression.Uncompressed;
            settings.format = TextureImporterFormat.RGBA32;
            settings.crunchedCompression = false;
            atlas.SetPlatformSettings(settings);
        }

        static void SetPlatformCompression(SpriteAtlas atlas, string platform, TextureImporterCompression compression)
        {
            var settings = atlas.GetPlatformSettings(platform);
            settings.name = platform;
            settings.overridden = true;
            settings.textureCompression = compression;
            settings.format = TextureImporterFormat.Automatic;
            settings.compressionQuality = (int)TextureCompressionQuality.Best;
            settings.crunchedCompression = false;
            atlas.SetPlatformSettings(settings);
            EditorUtility.SetDirty(atlas);
        }

        static void CleanupBackup(FixReport report)
        {
            if (report != null && !string.IsNullOrEmpty(report.BackupFolder) && Directory.Exists(report.BackupFolder)
                && report.RollbackFailed == 0)
                Directory.Delete(report.BackupFolder, true);
        }

        void SetBackgroundRuleSettings(OptimizerSettings settings)
        {
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("ApexForge.ProjectOptimizer.BackgroundRuleExecution", false);
                if (type != null) break;
            }
            if (type == null) return;
            m_BackgroundSettingsProperty = type.GetProperty("CurrentSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (m_BackgroundSettingsProperty == null || !m_BackgroundSettingsProperty.CanWrite) return;
            m_PreviousBackgroundSettings = m_BackgroundSettingsProperty.GetValue(null, null);
            m_BackgroundSettingsProperty.SetValue(null, settings, null);
            m_OverrodeBackgroundSettings = true;
        }

        void RestoreBackgroundRuleSettings()
        {
            if (m_OverrodeBackgroundSettings && m_BackgroundSettingsProperty != null)
                m_BackgroundSettingsProperty.SetValue(null, m_PreviousBackgroundSettings, null);
            m_OverrodeBackgroundSettings = false;
        }

        string CreateEmptyAtlas(string fileName)
        {
            string path = "Assets/" + fileName;
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(new SpriteAtlas(), path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            m_TemporaryAssets.Add(path);
            return path;
        }

        static void SetAtlasIncludedInBuild(string path, bool value)
        {
            var importer = AssetImporter.GetAtPath(path) as SpriteAtlasImporter;
            if (importer != null)
            {
                importer.includeInBuild = value;
                importer.SaveAndReimport();
                return;
            }

            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            Assert.IsNotNull(atlas, "Test Sprite Atlas failed to load: " + path);
            SpriteAtlasExtensions.SetIncludeInBuild(atlas, value);
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        static bool IsAtlasIncludedInBuild(string path)
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
            if (atlas == null) return false;
            var importer = AssetImporter.GetAtPath(path) as SpriteAtlasImporter;
            return importer != null ? importer.includeInBuild : SpriteAtlasExtensions.IsIncludeInBuild(atlas);
        }

        static FixContext CreateContext(string path)
        {
            return new FixContext
            {
                AssetPath = path,
                Importer = AssetImporter.GetAtPath(path),
                Asset = AssetDatabase.LoadMainAssetAtPath(path),
                BackupEnabled = true
            };
        }

        static void Commit(FixContext context)
        {
            if (context.NeedsReimport && context.Importer != null)
                context.Importer.SaveAndReimport();
            if (context.NeedsSave && context.Asset != null)
            {
                EditorUtility.SetDirty(context.Asset);
                AssetDatabase.SaveAssetIfDirty(context.Asset);
            }
            AssetDatabase.ImportAsset(context.AssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
