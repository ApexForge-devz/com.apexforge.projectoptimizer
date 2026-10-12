using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ApexForge.ProjectOptimizer.Tests
{
    /// <summary>Regression coverage for the approved AUD005/AUD006 user-preference overrides.</summary>
    public sealed class FixKindAuditSession08AudioPreferenceTests
    {
        const string TestFolder = "Assets/__ProjectOptimizerSession08AudioPreferenceTests";
        OptimizerSettings m_Settings;
        float m_OriginalStreamingSeconds;
        int m_OriginalFileSizeWarnKb;

        [SetUp]
        public void SetUp()
        {
            RuleBootstrap.EnsureBuiltInRules();
            m_Settings = OptimizerSettings.Instance;
            Assert.IsNotNull(m_Settings, "ProjectOptimizer settings must be initialized for analyzer tests.");
            m_OriginalStreamingSeconds = m_Settings.audioStreamingSeconds;
            m_OriginalFileSizeWarnKb = m_Settings.audioFileSizeWarnKb;
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.CreateFolder("Assets", "__ProjectOptimizerSession08AudioPreferenceTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Settings != null)
            {
                m_Settings.audioStreamingSeconds = m_OriginalStreamingSeconds;
                m_Settings.audioFileSizeWarnKb = m_OriginalFileSizeWarnKb;
                EditorUtility.SetDirty(m_Settings);
                AssetDatabase.SaveAssets();
            }
            if (AssetDatabase.IsValidFolder(TestFolder)) AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void Aud005OffersMonoAsRecommendedReviewFixForMultichannelClips()
        {
            string path = CreateWave("aud005-stereo.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.forceToMono = false;
            importer.SaveAndReimport();

            var entry = Analyze(path);
            var issue = entry.Issues.SingleOrDefault(i => i.RuleId == "AUD005");
            Assert.IsNotNull(issue, "Any non-ambisonic multichannel clip should be covered by the user's mono preference.");
            Assert.AreEqual(FixKind.Review, issue.FixKind, "AUD005 must remain ReviewFix, not automatic bulk mutation.");
            var option = issue.Options.SingleOrDefault(o => o.Label == "Use Mono");
            Assert.IsNotNull(option);
            Assert.IsTrue(option.Recommended);
            Assert.IsNotNull(option.Verify, "The review option must verify the importer state after reimport.");
            Assert.IsTrue(FixContract.EffectiveBackupRequired(issue, option), "Force To Mono is an importer change and must have a backup.");

            var request = new FixRequest
            {
                Entry = entry,
                Issue = issue,
                OptionOverride = option
            };
            var report = FixEngine.Apply(new[] { request }, true, FixExecutionMode.ReviewSingle);
            Assert.AreEqual(0, report.Blocked);
            Assert.AreEqual(0, report.Failed);
            var after = (AudioImporter)AssetImporter.GetAtPath(path);
            Assert.IsTrue(after.forceToMono, "An explicitly approved ReviewFix should set Force To Mono.");
            Assert.IsTrue(option.Verify(ReviewBatch.ProbeContext(path)), "The option verifier should observe Force To Mono after reimport.");
        }

        [Test]
        public void Aud005RejectsStaleEvidenceWhenClipBecomesAmbisonic()
        {
            string path = CreateWave("aud005-stale-ambisonic.wav", frames: 4410, channels: 2);
            var entry = Analyze(path);
            var issue = entry.Issues.Single(i => i.RuleId == "AUD005");
            var option = issue.Options.Single(o => o.Label == "Use Mono");

            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            importer.ambisonic = true;
            importer.SaveAndReimport();

            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue, OptionOverride = option } }, true, FixExecutionMode.ReviewSingle);
            Assert.Greater(report.Blocked + report.Failed + report.RolledBack, 0,
                "Stale channel/ambisonic evidence must not silently authorize Force To Mono.");
            Assert.IsFalse(((AudioImporter)AssetImporter.GetAtPath(path)).forceToMono,
                "The importer must remain unchanged when AUD005 evidence becomes invalid.");
        }

        [Test]
        public void Aud005ExcludesAlreadyMonoAndAmbisonicClips()
        {
            string monoPath = CreateWave("aud005-already-mono.wav", frames: 4410, channels: 1);
            Assert.IsFalse(Analyze(monoPath).Issues.Any(i => i.RuleId == "AUD005"));

            string stereoPath = CreateWave("aud005-ambisonic.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(stereoPath);
            importer.ambisonic = true;
            importer.forceToMono = false;
            importer.SaveAndReimport();
            Assert.IsFalse(Analyze(stereoPath).Issues.Any(i => i.RuleId == "AUD005"));
        }

        [Test]
        public void Aud006VerifiedAutofixDisablesPreloadForMatchingAssetAndVerifiesReadback()
        {
            string path = CreateWave("aud006-preload.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var sample = importer.defaultSampleSettings;
            sample.preloadAudioData = true;
            sample.loadType = AudioClipLoadType.Streaming;
            importer.defaultSampleSettings = sample;
            importer.SaveAndReimport();

            // Even a short, streaming clip is covered: the user preference applies to every item.
            m_Settings.audioStreamingSeconds = float.MaxValue;
            m_Settings.audioFileSizeWarnKb = int.MaxValue;
            entryRefresh(path);
            var entry = Analyze(path);
            var issue = entry.Issues.SingleOrDefault(i => i.RuleId == "AUD006");
            Assert.IsNotNull(issue);
            Assert.AreEqual(FixKind.Verified, issue.FixKind);
            Assert.IsNotNull(issue.Fix, "Verified requires a concrete default action.");
            Assert.IsNotNull(issue.Verify, "Verified requires a deterministic postcondition verifier.");
            Assert.IsTrue(issue.BackupRequired, "The importer mutation must require a backup.");

            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(0, report.Blocked);
            Assert.AreEqual(0, report.Failed);
            var after = (AudioImporter)AssetImporter.GetAtPath(path);
            Assert.IsFalse(ReadEffectiveSampleSettings(after).preloadAudioData);
            Assert.IsTrue(issue.Verify(ReviewBatch.ProbeContext(path)));
        }

        [Test]
        public void Aud006StaleEvidenceBlocksMutation()
        {
            string path = CreateWave("aud006-stale.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var sample = importer.defaultSampleSettings;
            sample.preloadAudioData = true;
            sample.loadType = AudioClipLoadType.CompressedInMemory;
            importer.defaultSampleSettings = sample;
            importer.SaveAndReimport();
            m_Settings.audioStreamingSeconds = 0.01f;
            m_Settings.audioFileSizeWarnKb = int.MaxValue;
            entryRefresh(path);
            var entry = Analyze(path);
            var issue = entry.Issues.Single(i => i.RuleId == "AUD006");
            issue.EvidenceState = EvidenceState.Stale;

            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.Greater(report.Blocked, 0);
            Assert.IsTrue(ReadEffectiveSampleSettings((AudioImporter)AssetImporter.GetAtPath(path)).preloadAudioData,
                "Stale evidence must not mutate the importer.");
        }

        [Test]
        public void Aud006VerifierFailureRollsBackImporterSettings()
        {
            string path = CreateWave("aud006-rollback.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var sample = importer.defaultSampleSettings;
            sample.preloadAudioData = true;
            sample.loadType = AudioClipLoadType.CompressedInMemory;
            importer.defaultSampleSettings = sample;
            importer.SaveAndReimport();
            m_Settings.audioStreamingSeconds = 0.01f;
            m_Settings.audioFileSizeWarnKb = int.MaxValue;
            entryRefresh(path);
            var entry = Analyze(path);
            var issue = entry.Issues.Single(i => i.RuleId == "AUD006");
            issue.Verify = _ => false; // Deliberately fail the postcondition to exercise FixEngine rollback.

            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.Greater(report.VerificationFailed, 0);
            Assert.Greater(report.RolledBack, 0);
            Assert.IsTrue(ReadEffectiveSampleSettings((AudioImporter)AssetImporter.GetAtPath(path)).preloadAudioData,
                "The original importer setting must be restored when the verifier fails.");
        }


        [Test]
        public void Aud006WrongAssetEvidenceRejectsMutation()
        {
            string sourcePath = CreateWave("aud006-source.wav", frames: 4410, channels: 2);
            string wrongPath = CreateWave("aud006-wrong-target.wav", frames: 4410, channels: 2);
            foreach (string path in new[] { sourcePath, wrongPath })
            {
                var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                var sample = importer.defaultSampleSettings;
                sample.preloadAudioData = true;
                sample.loadType = AudioClipLoadType.CompressedInMemory;
                importer.defaultSampleSettings = sample;
                importer.SaveAndReimport();
            }
            m_Settings.audioStreamingSeconds = 0.01f;
            m_Settings.audioFileSizeWarnKb = int.MaxValue;
            entryRefresh(sourcePath);
            var sourceEntry = Analyze(sourcePath);
            var issue = sourceEntry.Issues.Single(i => i.RuleId == "AUD006");
            issue.AssetEvidencePath = sourcePath;
            issue.AssetEvidenceSignature = EvidenceContract.ComputeAssetEvidenceSignature(sourcePath);
            var wrongEntry = Analyze(wrongPath);
            wrongEntry.Path = wrongPath;

            // Pair evidence captured for one asset with a different target path; the fix must not run.
            var request = new FixRequest { Entry = wrongEntry, Issue = issue };
            var report = FixEngine.Apply(new[] { request }, true, FixExecutionMode.VerifiedSingle);
            Assert.Greater(report.Blocked, 0);
            Assert.IsTrue(ReadEffectiveSampleSettings((AudioImporter)AssetImporter.GetAtPath(wrongPath)).preloadAudioData,
                "Evidence for one clip must never authorize mutation of another clip.");
        }


        [Test]
        public void Aud006UpdatesActiveTargetOverrideAndLeavesDefaultAlone()
        {
            string platform = m_Settings.PlatformString;
            Assume.That(!string.IsNullOrEmpty(platform) && platform != "Default", "A concrete target platform is required for this override test.");
            string path = CreateWave("aud006-platform-override.wav", frames: 4410, channels: 2);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var defaults = importer.defaultSampleSettings;
            defaults.preloadAudioData = false;
            importer.defaultSampleSettings = defaults;
            var overrideSettings = defaults;
            overrideSettings.preloadAudioData = true;
            Assert.IsTrue(importer.SetOverrideSampleSettings(platform, overrideSettings), "Unity must accept the selected-platform audio override.");
            importer.SaveAndReimport();

            entryRefresh(path);
            var entry = Analyze(path);
            var issue = entry.Issues.SingleOrDefault(i => i.RuleId == "AUD006");
            Assert.IsNotNull(issue, "AUD006 must inspect the effective selected-target override.");
            var report = FixEngine.Apply(new[] { new FixRequest { Entry = entry, Issue = issue } }, true, FixExecutionMode.VerifiedSingle);
            Assert.AreEqual(0, report.Blocked);
            Assert.AreEqual(0, report.Failed);

            var after = (AudioImporter)AssetImporter.GetAtPath(path);
            Assert.IsFalse(after.GetOverrideSampleSettings(platform).preloadAudioData, "The selected-target override must be disabled.");
            Assert.IsFalse(after.defaultSampleSettings.preloadAudioData, "The pre-existing default must remain unchanged.");
            Assert.IsTrue(issue.Verify(ReviewBatch.ProbeContext(path)));
        }

        AudioImporterSampleSettings ReadEffectiveSampleSettings(AudioImporter importer)
        {
            string platform = m_Settings != null ? m_Settings.PlatformString : null;
            return !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform)
                ? importer.GetOverrideSampleSettings(platform)
                : importer.defaultSampleSettings;
        }

        AssetEntry Analyze(string path)
        {
            return new AudioAnalyzer().Analyze(path, new ScanContext { Settings = m_Settings });
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

        static void entryRefresh(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
