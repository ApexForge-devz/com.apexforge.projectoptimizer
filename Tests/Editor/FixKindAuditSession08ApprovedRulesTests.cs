using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>Behavior and catalog regression coverage for approved Session 08 FixKind targets.</summary>
    public sealed class FixKindAuditSession08ApprovedRulesTests
    {
        const string TestFolder = "Assets/__ProjectOptimizerSession08ApprovedRulesTests";
        OptimizerSettings m_Settings;
        float m_OriginalStreamingSeconds;
        float m_OriginalCompressedSeconds;
        int m_OriginalFileSizeWarnKb;
        int m_OriginalSfxSampleRate;
        int m_OriginalMaxQuality;

        [SetUp]
        public void SetUp()
        {
            RuleBootstrap.EnsureBuiltInRules();
            m_Settings = OptimizerSettings.Instance;
            Assert.IsNotNull(m_Settings, "ProjectOptimizer settings must be initialized for analyzer tests.");
            m_OriginalStreamingSeconds = m_Settings.audioStreamingSeconds;
            m_OriginalCompressedSeconds = m_Settings.audioCompressedInMemorySeconds;
            m_OriginalFileSizeWarnKb = m_Settings.audioFileSizeWarnKb;
            m_OriginalSfxSampleRate = m_Settings.audioSfxSampleRate;
            m_OriginalMaxQuality = m_Settings.audioMaxQuality;
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.CreateFolder("Assets", "__ProjectOptimizerSession08ApprovedRulesTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Settings != null)
            {
                m_Settings.audioStreamingSeconds = m_OriginalStreamingSeconds;
                m_Settings.audioCompressedInMemorySeconds = m_OriginalCompressedSeconds;
                m_Settings.audioFileSizeWarnKb = m_OriginalFileSizeWarnKb;
                m_Settings.audioSfxSampleRate = m_OriginalSfxSampleRate;
                m_Settings.audioMaxQuality = m_OriginalMaxQuality;
                EditorUtility.SetDirty(m_Settings);
                AssetDatabase.SaveAssets();
            }
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void ApprovedTargetTypesMatchAllTwentyCanonicalRules()
        {
            var expected = new Dictionary<string, FixKind>
            {
                { "AUD001", FixKind.Verified }, { "AUD002", FixKind.Verified },
                { "AUD003", FixKind.Verified }, { "AUD004", FixKind.Verified },
                { "AUD005", FixKind.Review }, { "AUD006", FixKind.Verified },
                { "AUD007", FixKind.Verified }, { "AUD008", FixKind.Verified },
                { "AUD009", FixKind.None }, { "AUD010", FixKind.Verified },
                { "AUD011", FixKind.Verified }, { "AUD012", FixKind.Review },
                { "AUD013", FixKind.Verified }, { "VID001", FixKind.Verified },
                { "VID002", FixKind.Review }, { "VID003", FixKind.Review },
                { "VID004", FixKind.Review }, { "VID005", FixKind.Verified },
                { "VID006", FixKind.Verified }, { "VID007", FixKind.None }
            };

            Assert.AreEqual(20, expected.Count);
            foreach (var pair in expected)
            {
                var rule = RuleCatalog.Get(pair.Key);
                Assert.IsNotNull(rule, pair.Key + " must remain registered.");
                Assert.AreEqual(pair.Value, rule.FixKind, pair.Key + " has an unexpected approved FixKind.");
                if (pair.Value == FixKind.Verified)
                {
                    Assert.IsTrue(rule.DefaultBackupRequired, pair.Key + " must retain a backup requirement.");
                    Assert.GreaterOrEqual(rule.RecommendationConfidence, RuleCatalog.MinimumForAutomaticFix, pair.Key + " must meet the automatic confidence floor.");
                }
            }
            Assert.AreEqual(13, expected.Values.Count(v => v == FixKind.Verified));
            Assert.AreEqual(5, expected.Values.Count(v => v == FixKind.Review));
            Assert.AreEqual(2, expected.Values.Count(v => v == FixKind.None));
        }

        [Test]
        public void Aud001StreamsEligibleLongClipAndVerifiesReadback()
        {
            ConfigureThresholds(streamSeconds: 0.5f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 100);
            string path = CreateWave("aud001-long.wav", frames: 44100, channels: 2);
            ConfigureSample(path, AudioClipLoadType.CompressedInMemory, AudioCompressionFormat.Vorbis, 0.7f, AudioSampleRateSetting.PreserveSampleRate);

            var issue = FindIssue(path, "AUD001");
            ApplyVerified(path, issue);
            Assert.AreEqual(AudioClipLoadType.Streaming, EffectiveSample(path).loadType);
        }

        [Test]
        public void Aud002DecompressesEligibleShortStreamingClip()
        {
            Assume.That(!string.Equals(m_Settings.PlatformString, "WebGL", StringComparison.OrdinalIgnoreCase), "AUD002 intentionally excludes WebGL.");
            ConfigureThresholds(streamSeconds: 30f, compressedSeconds: 1f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 100);
            string path = CreateWave("aud002-short.wav", frames: 4410, channels: 2);
            ConfigureSample(path, AudioClipLoadType.Streaming, AudioCompressionFormat.Vorbis, 0.7f, AudioSampleRateSetting.PreserveSampleRate);

            var issue = FindIssue(path, "AUD002");
            ApplyVerified(path, issue);
            Assert.AreEqual(AudioClipLoadType.DecompressOnLoad, EffectiveSample(path).loadType);
        }

        [Test]
        public void Aud003CompressesEligibleLongPcmClipWithoutChangingLoadType()
        {
            ConfigureThresholds(streamSeconds: 0.5f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 100);
            string path = CreateWave("aud003-long-pcm.wav", frames: 44100, channels: 2);
            ConfigureSample(path, AudioClipLoadType.CompressedInMemory, AudioCompressionFormat.PCM, 1f, AudioSampleRateSetting.PreserveSampleRate);

            var issue = FindIssue(path, "AUD003");
            ApplyVerified(path, issue);
            var sample = EffectiveSample(path);
            Assert.AreEqual(AudioCompressionFormat.Vorbis, sample.compressionFormat);
            Assert.AreEqual(AudioClipLoadType.CompressedInMemory, sample.loadType);
        }

        [Test]
        public void Aud004ClampsVorbisQualityToConfiguredBudget()
        {
            ConfigureThresholds(streamSeconds: 30f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 40);
            string path = CreateWave("aud004-quality.wav", frames: 4410, channels: 2);
            ConfigureSample(path, AudioClipLoadType.CompressedInMemory, AudioCompressionFormat.Vorbis, 1f, AudioSampleRateSetting.PreserveSampleRate);

            var issue = FindIssue(path, "AUD004");
            ApplyVerified(path, issue);
            Assert.LessOrEqual(EffectiveSample(path).quality, 0.401f);
        }

        [Test]
        public void Aud007EnablesBackgroundLoadingOnLongNonStreamingClip()
        {
            ConfigureThresholds(streamSeconds: 0.5f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 100);
            string path = CreateWave("aud007-background.wav", frames: 44100, channels: 2);
            ConfigureSample(path, AudioClipLoadType.CompressedInMemory, AudioCompressionFormat.Vorbis, 0.7f, AudioSampleRateSetting.PreserveSampleRate);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.loadInBackground = false;
            importer.SaveAndReimport();

            var issue = FindIssue(path, "AUD007");
            ApplyVerified(path, issue);
            Assert.IsTrue(((AudioImporter)AssetImporter.GetAtPath(path)).loadInBackground);
        }

        [Test]
        public void Aud008UsesUnitySampleRateOptimizerForEligiblePcmClip()
        {
            ConfigureThresholds(streamSeconds: 30f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 22050, maxQuality: 100);
            string path = CreateWave("aud008-samplerate.wav", frames: 4410, channels: 2);
            ConfigureSample(path, AudioClipLoadType.CompressedInMemory, AudioCompressionFormat.PCM, 1f, AudioSampleRateSetting.PreserveSampleRate);

            var issue = FindIssue(path, "AUD008");
            ApplyVerified(path, issue);
            Assert.AreEqual(AudioSampleRateSetting.OptimizeSampleRate, EffectiveSample(path).sampleRateSetting);
        }

        [Test]
        public void Aud010DisablesForceMonoOnlyWhenClipRemainsAmbisonic()
        {
            ConfigureThresholds(streamSeconds: 30f, compressedSeconds: 0.01f, fileWarnKb: int.MaxValue, sfxSampleRate: 96000, maxQuality: 100);
            string path = CreateWave("aud010-ambisonic.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.ambisonic = true;
            importer.forceToMono = true;
            importer.SaveAndReimport();

            var issue = FindIssue(path, "AUD010");
            ApplyVerified(path, issue);
            var after = (AudioImporter)AssetImporter.GetAtPath(path);
            Assert.IsTrue(after.ambisonic);
            Assert.IsFalse(after.forceToMono);
        }

        void ConfigureThresholds(float streamSeconds, float compressedSeconds, int fileWarnKb, int sfxSampleRate, int maxQuality)
        {
            m_Settings.audioStreamingSeconds = streamSeconds;
            m_Settings.audioCompressedInMemorySeconds = compressedSeconds;
            m_Settings.audioFileSizeWarnKb = fileWarnKb;
            m_Settings.audioSfxSampleRate = sfxSampleRate;
            m_Settings.audioMaxQuality = maxQuality;
        }

        void ConfigureSample(string path, AudioClipLoadType loadType, AudioCompressionFormat format, float quality, AudioSampleRateSetting sampleRateSetting)
        {
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var sample = importer.defaultSampleSettings;
            sample.loadType = loadType;
            sample.compressionFormat = format;
            sample.quality = quality;
            sample.sampleRateSetting = sampleRateSetting;
            importer.defaultSampleSettings = sample;
            importer.SaveAndReimport();
        }

        OptimizationIssue FindIssue(string path, string ruleId)
        {
            var entry = new AudioAnalyzer().Analyze(path, new ScanContext { Settings = m_Settings });
            Assert.IsNotNull(entry, "AudioAnalyzer must produce an entry for " + path);
            var issue = entry.Issues.SingleOrDefault(i => i.RuleId == ruleId);
            Assert.IsNotNull(issue, "Expected " + ruleId + " finding for " + path + ". Actual findings: " + string.Join(", ", entry.Issues.Select(i => i.RuleId)));
            Assert.AreEqual(FixKind.Verified, issue.FixKind, ruleId + " must be a Verified AutoFix.");
            Assert.IsNotNull(issue.Fix, ruleId + " requires an automatic action.");
            Assert.IsNotNull(issue.Verify, ruleId + " requires a postcondition verifier.");
            Assert.IsTrue(issue.BackupRequired, ruleId + " must require a backup.");
            return issue;
        }

        void ApplyVerified(string path, OptimizationIssue discoveredIssue)
        {
            // Re-analyze immediately before execution so the FixRequest contains an issue bound
            // to the exact AssetEntry/evidence snapshot supplied to FixEngine.
            var entry = Analyze(path);
            var issue = entry.Issues.SingleOrDefault(i => i.RuleId == discoveredIssue.RuleId);
            Assert.IsNotNull(issue, discoveredIssue.RuleId + " must still be applicable in fresh evidence.");
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix);
            Assert.IsNotNull(issue.Verify);
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(0, report.Blocked, issue.RuleId + " should not be blocked for the tested common case: " + string.Join("; ", report.Errors));
            Assert.AreEqual(0, report.Failed, issue.RuleId + " should apply and verify: " + string.Join("; ", report.Errors));
            Assert.IsTrue(issue.Verify(ReviewBatch.ProbeContext(path)), issue.RuleId + " postcondition should pass after reimport.");
        }

        AssetEntry Analyze(string path) => new AudioAnalyzer().Analyze(path, new ScanContext { Settings = m_Settings });

        AudioImporterSampleSettings EffectiveSample(string path)
        {
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            string platform = m_Settings.PlatformString;
            return !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform)
                ? importer.GetOverrideSampleSettings(platform)
                : importer.defaultSampleSettings;
        }

        string CreateWave(string fileName, int frames, int channels)
        {
            string path = TestFolder + "/" + fileName;
            string absolute = Path.Combine(Directory.GetCurrentDirectory(), path);
            File.WriteAllBytes(absolute, MakePcmWave(frames, channels, 44100));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        static byte[] MakePcmWave(int frames, int channels, int sampleRate)
        {
            const short bitsPerSample = 16;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = frames * blockAlign;
            using (var stream = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * blockAlign);
                writer.Write((short)blockAlign);
                writer.Write(bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                for (int i = 0; i < dataSize; i++) writer.Write((byte)0);
                return stream.ToArray();
            }
        }
    }
}
