using System;
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Load type, compression, sample rate and channel analysis for AudioClip assets.</summary>
	public sealed class AudioAnalyzer : IBackgroundAssetAnalyzer
	{
		static readonly string[] k_Extensions = { ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac", ".mod", ".it", ".s3m", ".xm" };

		public AssetCategory Category => AssetCategory.Audio;

		public AssetKindMask SupportedKinds => AssetKindMask.Audio;

		[Serializable]
		sealed class AudioFacts
		{
			public float length;
			public int channels;
			public int frequency;
			public long runtimeSize;
			public AudioClipLoadType loadType;
			public AudioCompressionFormat compressionFormat;
			public float quality;
			public bool preloadAudioData;
			public AudioSampleRateSetting sampleRateSetting;
			public uint sampleRateOverride;
			public bool forceToMono;
			public bool ambisonic;
			public bool loadInBackground;
			public string targetPlatform = string.Empty;
			public bool hasTargetOverride;
			public AudioClipLoadType defaultLoadType;
			public AudioCompressionFormat defaultCompressionFormat;
			public float defaultQuality;
			public bool defaultPreloadAudioData;
			public AudioSampleRateSetting defaultSampleRateSetting;
			public AudioClipLoadType targetOverrideLoadType;
			public bool overrideDefeatsStreamingPolicy;
		}

		static AudioAnalyzer()
		{
			Rules.Register(id: "AUD001", title: "Long clip is not streamed", category: AssetCategory.Audio,
							why: "Unity's Streaming load type generally minimizes resident audio memory and moves decoding to a dedicated streaming thread, but it also introduces streaming/disk work. Long music, ambience and other long-form audio are the strongest candidates; this is not a universal rule for every long clip.",
							fix: "Automatically select Streaming for long-form non-WebGL clips when the selected target does not have an explicit sample-settings override. Explicit overrides, tracker modules and settings that differ from the default remain outside this automatic path.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD002", title: "Short clip is streamed", category: AssetCategory.Audio,
							why: "The configured short-clip policy classifies this clip as a short effect, and the finding is emitted specifically because its effective target load type is Streaming. For that policy, keeping the clip decoded avoids streaming latency and is the deterministic importer correction.",
							fix: "Automatically change eligible short clips from Streaming to Decompress On Load on the effective selected target. Preserve WebGL exclusions and require current settings plus a post-reimport read-back.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD003", title: "Uncompressed PCM on a long clip", category: AssetCategory.Audio,
							why: "PCM is uncompressed and therefore consumes substantially more storage than compressed audio. Unity documents compressed formats such as Vorbis/MP3 for long files like music and dialogue, while PCM remains useful where low decode cost or lossless storage is important.",
							fix: "Automatically switch eligible long-form PCM clips to Vorbis while preserving the existing load type and unrelated sample settings. Back up importer metadata and verify the effective selected setting after reimport.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD004", title: "Compression quality above budget", category: AssetCategory.Audio,
							why: "Unity's audio compression quality is a project-specific quality/size trade-off. Higher quality can increase the resulting size, but there is no universal quality value that is safe to lower without listening to the result.",
							fix: "Clamp Vorbis compression quality to the configured audioMaxQuality limit for the effective selected target. The project setting supplies the deterministic size/quality budget; capture and verify the target state.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD005", title: "Multichannel clip is not forced to mono", category: AssetCategory.Audio,
							why: "The user's selected policy is to recommend Force To Mono for all matching non-ambisonic multichannel clips. Downmixing changes stereo presentation, so this remains a Review Fix: the user can keep or restore stereo for assets where it matters.",
							fix: "Review Fix: Use Mono is the preferred action for each matching multichannel clip. Keep or restore stereo on clips whose stereo image, channel separation or phase characteristics matter.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD006", title: "Preload Audio Data is enabled", category: AssetCategory.Audio,
							why: "The selected user policy is to disable Preload Audio Data for every asset matching AUD006. Unity requires runtime code to call AudioClip.LoadAudioData before playback when preload is disabled; the user accepts that trade-off and can manually restore preload for exceptional clips after validation.",
							fix: "Automatically disable Preload Audio Data for every matching clip and the effective target settings. Verify the imported setting and keep a backup so the change can be rolled back if Unity rejects it or verification fails.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD007", title: "Load In Background disabled on a long clip", category: AssetCategory.Audio,
							why: "Load In Background allows audio loading to happen asynchronously instead of on the main thread. This is useful for large files, but it can also delay first playback until the clip is ready. It is mainly relevant to non-streaming clips.",
							fix: "Enable Load In Background for eligible large/long non-streaming clips. Verify the importer state and preserve a backup so projects can restore preload/background behavior for special immediate-playback paths.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD008", title: "High sample-rate clip is not using Unity's sample-rate optimization", category: AssetCategory.Audio,
							why: "Unity's Optimize Sample Rate analyzes the audio and chooses a minimal sample rate while preserving audio quality. This is safer than forcing every short effect to a fixed rate such as 22050 Hz.",
							fix: "Enable Unity Optimize Sample Rate for eligible PCM/ADPCM clips above the configured sample-rate threshold. Exclude tracker formats and verify the effective sample-rate setting after reimport.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD009", title: "Very large audio source file", category: AssetCategory.Audio,
							why: "The source file is not the same thing as the encoded audio shipped by Unity, because Unity transcodes imported audio for the build target. A large source can still increase import time, repository size and source-control storage even when its shipped representation is smaller.",
							fix: "Trim unnecessary silence, remove unused source material, or store the master/source in an appropriate production format. Do not assume source size equals shipped build size.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "AUD010", title: "Ambisonic clip is forced to mono", category: AssetCategory.Audio,
							why: "Ambisonic audio represents a soundfield that depends on its channels. Forcing an ambisonic clip to mono discards that multichannel soundfield representation and is therefore incompatible with the intended ambisonic use.",
							fix: "Disable Force To Mono when an ambisonic clip has it enabled. Ambisonic soundfields depend on their multichannel representation; verify the importer state after reimport.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD011", title: "Tracker module is set to Decompress On Load", category: AssetCategory.Audio,
							why: "Unity recommends Compressed In Memory for tracker modules because Decompress On Load expands the whole song into memory. The recommendation is strong, but the analyzer cannot know whether a project intentionally trades memory for lower playback decode overhead.",
							fix: "Use Compressed In Memory for recognized tracker modules currently set to Decompress On Load. Preserve unrelated settings and verify the selected target load type after reimport.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "AUD012", title: "Target-platform override defeats streaming policy", category: AssetCategory.Audio,
							why: "Audio importer overrides are target-specific. An explicit override can replace otherwise suitable default settings, so a long clip should be checked against the effective target-platform settings rather than assuming the default settings are what the device will use.",
							fix: "Review the effective target-platform settings. Use Streaming only on supported non-WebGL platforms when appropriate; WebGL should use Compressed In Memory or Decompress On Load.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "AUD013", title: "WebGL clip uses Streaming load type", category: AssetCategory.Audio,
							why: "Unity WebGL does not support the imported-audio Streaming load type used by native desktop/mobile audio. WebGL clips using Streaming should use Compressed In Memory instead.",
							fix: "For WebGL only, create or update a WebGL-specific sample-settings override to replace unsupported Streaming with Compressed In Memory. Never modify the global default merely because the WebGL override is absent.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			for (int i = 0; i < k_Extensions.Length; i++)
				if (assetPath.EndsWith(k_Extensions[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
		{
			if (mainContext == null || backgroundContext == null || !Handles(assetPath)) return;
			var settings = mainContext.Settings;
			string targetPlatform = settings.PlatformString;
			string dependencyHash = backgroundContext.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			AudioFacts facts = null;
			bool measuredForBackground = false;
			if (mainContext.Cache != null) mainContext.Cache.TryGet(cacheKey, dependencyHash, out facts);
			if (facts == null)
			{
				measuredForBackground = true;
				var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
				if (importer == null) return;
				var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
				if (clip == null) return;
				bool hasTargetOverride = false;
				var sample = GetEffectiveSampleSettings(importer, targetPlatform, out hasTargetOverride);
				facts = new AudioFacts
				{
					length = clip.length, channels = clip.channels, frequency = clip.frequency,
					loadType = sample.loadType, compressionFormat = sample.compressionFormat, quality = sample.quality,
					preloadAudioData = sample.preloadAudioData, sampleRateSetting = sample.sampleRateSetting, sampleRateOverride = sample.sampleRateOverride,
					forceToMono = importer.forceToMono, ambisonic = importer.ambisonic, loadInBackground = importer.loadInBackground,
					targetPlatform = targetPlatform ?? string.Empty, hasTargetOverride = hasTargetOverride,
					defaultLoadType = importer.defaultSampleSettings.loadType,
						defaultCompressionFormat = importer.defaultSampleSettings.compressionFormat,
						defaultQuality = importer.defaultSampleSettings.quality,
						defaultPreloadAudioData = importer.defaultSampleSettings.preloadAudioData,
						defaultSampleRateSetting = importer.defaultSampleSettings.sampleRateSetting
				};
				facts.runtimeSize = EstimateRuntimeSize(clip, sample, importer);
			}
			// The cache key includes the effective target platform and the dependency hash covers the
			// importer. Therefore a valid cached fact already contains the target override state; loading
			// the AudioImporter again on a warm scan would only add main-thread Unity work.
			backgroundContext.SeedCached(cacheKey, facts, measuredForBackground);
		}

		public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
		{
			if (context == null || !Handles(assetPath)) return null;
			string targetPlatform = context.Settings.PlatformString;
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			var facts = context.GetOrMeasure<AudioFacts>(assetPath, cacheKey, null);
			if (facts == null) return null;
			return BuildEntry(assetPath, context.Settings, facts, context.GetFileSize(assetPath), context.GetGuid(assetPath));
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var settings = ctx.Settings;
			string targetPlatform = settings.PlatformString;
			string dependencyHash = ctx.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			AudioFacts facts;
			if (ctx.Cache != null && ctx.Cache.TryGet(cacheKey, dependencyHash, out AudioFacts cached)) facts = cached;
			else
			{
				var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
				if (importer == null) return null;
				var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
				if (clip == null) return null;
				bool hasTargetOverride = false;
				var sample = GetEffectiveSampleSettings(importer, targetPlatform, out hasTargetOverride);
				facts = new AudioFacts
				{ length = clip.length, channels = clip.channels, frequency = clip.frequency, loadType = sample.loadType, compressionFormat = sample.compressionFormat, quality = sample.quality, preloadAudioData = sample.preloadAudioData, sampleRateSetting = sample.sampleRateSetting, sampleRateOverride = sample.sampleRateOverride, forceToMono = importer.forceToMono, ambisonic = importer.ambisonic, loadInBackground = importer.loadInBackground, targetPlatform = targetPlatform ?? string.Empty, hasTargetOverride = hasTargetOverride,
					defaultLoadType = importer.defaultSampleSettings.loadType,
					defaultCompressionFormat = importer.defaultSampleSettings.compressionFormat,
					defaultQuality = importer.defaultSampleSettings.quality,
					defaultPreloadAudioData = importer.defaultSampleSettings.preloadAudioData,
					defaultSampleRateSetting = importer.defaultSampleSettings.sampleRateSetting };
				facts.runtimeSize = EstimateRuntimeSize(clip, sample, importer);
				ctx.Cache?.Set(cacheKey, facts, dependencyHash);
			}
			var liveImporter = AssetImporter.GetAtPath(assetPath) as AudioImporter;
			if (liveImporter != null && !string.IsNullOrEmpty(targetPlatform))
			{
				facts.hasTargetOverride = liveImporter.ContainsSampleSettingsOverride(targetPlatform);
				var liveDefaults = liveImporter.defaultSampleSettings;
				facts.defaultLoadType = liveDefaults.loadType;
				facts.defaultCompressionFormat = liveDefaults.compressionFormat;
				facts.defaultQuality = liveDefaults.quality;
				facts.defaultPreloadAudioData = liveDefaults.preloadAudioData;
				facts.defaultSampleRateSetting = liveDefaults.sampleRateSetting;
				facts.targetOverrideLoadType = facts.loadType;
				if (facts.hasTargetOverride) facts.targetOverrideLoadType = liveImporter.GetOverrideSampleSettings(targetPlatform).loadType;
				facts.overrideDefeatsStreamingPolicy = facts.hasTargetOverride && facts.defaultLoadType == AudioClipLoadType.Streaming && facts.targetOverrideLoadType != AudioClipLoadType.Streaming;
			}
			return BuildEntry(assetPath, settings, facts, ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath), ctx.GetGuid(assetPath));
		}

		AssetEntry BuildEntry(string assetPath, OptimizerSettings settings, AudioFacts facts, long fileSize, string guid)
		{
			string targetPlatform = facts.targetPlatform;
			long runtimeSize = facts.runtimeSize;
			var entry = new AssetEntry
			{
				Guid = guid ?? string.Empty,
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Audio,
				TypeLabel = "AudioClip",
				FileSize = fileSize,
				RuntimeSize = runtimeSize
			};

			entry.Set("length", (double)facts.length);
			entry.Set("lengthText", facts.length.ToString("0.00") + " s");
			entry.Set("channels", (long)facts.channels);
			entry.Set("frequency", (long)facts.frequency);
			entry.Set("loadType", facts.loadType.ToString());
			entry.Set("format", facts.compressionFormat.ToString());
			entry.Set("quality", (long)Mathf.RoundToInt(facts.quality * 100f));
			entry.Set("preload", facts.preloadAudioData ? "Yes" : "No");
			entry.Set("mono", facts.forceToMono ? "Yes" : "No");
			entry.Set("ambisonic", facts.ambisonic ? "Yes" : "No");
			entry.Set("settingsPlatform", string.IsNullOrEmpty(targetPlatform) ? "Default" : targetPlatform);
			entry.Set("settingsOverride", facts.hasTargetOverride ? "Explicit" : "Default/Translated");

			bool isLong = facts.length >= settings.audioStreamingSeconds;
			bool isShort = facts.length <= settings.audioCompressedInMemorySeconds;
			bool isLarge = fileSize > settings.audioFileSizeWarnKb * 1024L;
			bool trackerModule = IsTrackerModule(assetPath);
			// AUD001 - auto-stream the default-only long-form common case. Explicit overrides and
			// tracker modules are handled as exceptions so a deliberate platform/load policy is not overwritten.
			OptimizationIssue aud001Issue = null;
			using (RuleTiming.Begin("AUD001", entry.Path))
				if (isLong && facts.loadType != AudioClipLoadType.Streaming
					&& !IsWebGLPlatform(targetPlatform)
					&& !trackerModule && !facts.hasTargetOverride
					&& facts.defaultLoadType == facts.loadType)
				{
					aud001Issue = entry.AddIssue(
						"AUD001", facts.loadType.ToString(), "Streaming", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, false,
							s => s.loadType == facts.defaultLoadType && s.loadType != AudioClipLoadType.Streaming,
							s => { s.loadType = AudioClipLoadType.Streaming; return s; }),
						"Long-form audio is switched to Streaming only for the default-only common case; explicit overrides, tracker modules and WebGL are excluded.",
						basis: SavingsBasis.Unknown,
						verify: c => ReadSampleForTarget(c, targetPlatform, false)?.loadType == AudioClipLoadType.Streaming);
				}

			// AUD002 - Very short streamed clips.
			using (RuleTiming.Begin("AUD002", entry.Path))
				if (isShort && facts.loadType == AudioClipLoadType.Streaming && !IsWebGLPlatform(targetPlatform)
					&& (facts.hasTargetOverride || facts.defaultLoadType == AudioClipLoadType.Streaming))
				{
					entry.AddIssue(
						"AUD002", "Streaming", "Decompress On Load", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.loadType == AudioClipLoadType.Streaming,
							s => { s.loadType = AudioClipLoadType.DecompressOnLoad; return s; }),
						"Short clips are covered by the configured SFX policy. Decompress On Load is the deterministic importer correction for this finding.",
						basis: SavingsBasis.Unknown,
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.loadType == AudioClipLoadType.DecompressOnLoad);
				}

			// AUD003 - compress long-form PCM to Vorbis while preserving the existing load type.
			using (RuleTiming.Begin("AUD003", entry.Path))
				if (isLong && facts.compressionFormat == AudioCompressionFormat.PCM
					&& (facts.hasTargetOverride || facts.defaultCompressionFormat == AudioCompressionFormat.PCM))
				{
					entry.AddIssue(
						"AUD003", "PCM", "Vorbis", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.compressionFormat == AudioCompressionFormat.PCM,
							s => { s.compressionFormat = AudioCompressionFormat.Vorbis; return s; }),
						"Compresses the selected target's long-form PCM settings to Vorbis without changing the load type. Reimport and rollback are guarded by the fix engine.",
						basis: SavingsBasis.Unknown,
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.compressionFormat == AudioCompressionFormat.Vorbis);
				}

			// AUD004 - clamp Vorbis quality to the configured project quality budget.
			int qualityPercent = Mathf.RoundToInt(Mathf.Clamp01(facts.quality) * 100f);
			float qualityCap = Mathf.Clamp(settings.audioMaxQuality, 0, 100) / 100f;
			using (RuleTiming.Begin("AUD004", entry.Path))
				if (facts.compressionFormat == AudioCompressionFormat.Vorbis
					&& qualityPercent > settings.audioMaxQuality
					&& (facts.hasTargetOverride || (facts.defaultCompressionFormat == AudioCompressionFormat.Vorbis
						&& Mathf.Abs(facts.defaultQuality - facts.quality) < 0.001f)))
				{
					entry.AddIssue(
						"AUD004", "Quality " + qualityPercent, "Quality " + settings.audioMaxQuality, 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.compressionFormat == AudioCompressionFormat.Vorbis && s.quality > qualityCap + 0.001f,
							s => { s.quality = qualityCap; return s; }),
						"Clamps Vorbis quality to the configured audioMaxQuality budget; the importer state is checked before mutation and after reimport.",
						basis: SavingsBasis.Unknown,
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.quality <= qualityCap + 0.001f);
				}

			// AUD005 - all matching non-ambisonic multichannel clips. Mono is the preferred Review Fix;
			// the user approves the batch and can preserve or restore stereo exceptions.
			using (RuleTiming.Begin("AUD005", entry.Path))
				if (facts.channels > 1 && !facts.forceToMono && !facts.ambisonic)
				{
					entry.AddIssue(
						"AUD005", facts.channels + " channels", "Review Fix: Use Mono (preferred)", 0, null,
						"The user preference is to recommend Mono for every matching multichannel clip. Apply this as a Review Fix; leave or restore stereo where the user considers it necessary.",
						options: new List<FixOption> { new FixOption {
							Label = "Use Mono",
							Recommended = true,
							Risk = FixRisk.Medium,
							BackupRequiredOverride = true,
							Warning = "Review Fix: Force To Mono will downmix this clip. This option is the preferred batch action; keep or restore stereo on exceptions.",
							Apply = c => ApplyForceMonoForReview(c, facts.channels),
							Verify = c =>
							{
								var ai = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
								return ai != null && ai.forceToMono;
							}
						} }
					);
				}

			// AUD006 - explicit project preference: disable preload for every audio clip whose
			// effective selected-target sample settings currently enable it, regardless of size/load type.
			using (RuleTiming.Begin("AUD006", entry.Path))
				if (facts.preloadAudioData && (facts.hasTargetOverride || facts.defaultPreloadAudioData))
				{
					entry.AddIssue(
						"AUD006", "Preload on", "Preload off", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							sample => sample.preloadAudioData,
							sample => { sample.preloadAudioData = false; return sample; }),
						"The selected policy disables Preload Audio Data for every AUD006 finding. This changes only the reported effective target setting (or the default when no override exists); user can restore preload for any runtime exception after testing.",
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.preloadAudioData == false);
				}

			// AUD007 - background-load eligible large/long non-streaming clips.
			using (RuleTiming.Begin("AUD007", entry.Path))
				if ((isLong || isLarge)
					&& facts.loadType != AudioClipLoadType.Streaming
					&& !facts.loadInBackground)
				{
					entry.AddIssue(
						"AUD007", "Load In Background off", "On", 0,
						c => SetLoadInBackgroundChecked(c, false, true),
						"Enables background loading for eligible large/long non-streaming clips. Projects with immediate-playback requirements can restore the setting after validation.",
						basis: SavingsBasis.Unknown,
						verify: c => (AssetImporter.GetAtPath(c.AssetPath) as AudioImporter)?.loadInBackground == true);
				}

			// AUD008 - use Unity's content-aware sample-rate optimizer instead of a fixed 22050 Hz rule.
			using (RuleTiming.Begin("AUD008", entry.Path))
				if (facts.frequency > settings.audioSfxSampleRate
					&& facts.sampleRateSetting == AudioSampleRateSetting.PreserveSampleRate
					&& IsSampleRateOptimizableFormat(facts.compressionFormat)
					&& !trackerModule
					&& (facts.hasTargetOverride || (facts.defaultSampleRateSetting == AudioSampleRateSetting.PreserveSampleRate
						&& facts.defaultCompressionFormat == facts.compressionFormat)))
				{
					entry.AddIssue(
						"AUD008", facts.frequency + " Hz / Preserve", "Optimize Sample Rate", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.sampleRateSetting == AudioSampleRateSetting.PreserveSampleRate && IsSampleRateOptimizableFormat(s.compressionFormat),
							s => { s.sampleRateSetting = AudioSampleRateSetting.OptimizeSampleRate; return s; }),
						"Unity's sample-rate optimizer is intended for PCM/ADPCM-style imported data. It analyzes the audio and chooses a minimal rate while preserving quality; it should not be presented as a universal optimization for every codec.",
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.sampleRateSetting == AudioSampleRateSetting.OptimizeSampleRate);
				}

			// AUD009 - source/project hygiene, not a direct shipped-build size claim.
			using (RuleTiming.Begin("AUD009", entry.Path))
				if (isLarge)
				{
					entry.AddIssue(
						"AUD009", DisplayUtility.FormatBytes(fileSize), "Review source size", 0, null,
						"Unity transcodes imported audio for the target build; source-file size primarily affects project/source-control/import costs rather than directly equaling shipped audio size.");
				}

			// AUD010 - Force To Mono is not appropriate for an ambisonic soundfield.
			using (RuleTiming.Begin("AUD010", entry.Path))
				if (facts.ambisonic && facts.forceToMono)
				{
					entry.AddIssue(
						"AUD010", "Ambisonic + Force To Mono", "Force To Mono off", 0, c => SetForceToMonoChecked(c, true, false),
						"Ambisonic audio relies on a multichannel soundfield; forcing it to mono discards that representation.",
						verify: c => (AssetImporter.GetAtPath(c.AssetPath) as AudioImporter)?.forceToMono == false);
				}

			// AUD011 - Unity's recommended tracker-module memory policy.
			using (RuleTiming.Begin("AUD011", entry.Path))
				if (trackerModule && facts.loadType == AudioClipLoadType.DecompressOnLoad
					&& (facts.hasTargetOverride || facts.defaultLoadType == AudioClipLoadType.DecompressOnLoad))
				{
					entry.AddIssue(
						"AUD011", "Decompress On Load", "Compressed In Memory", 0,
						c => SetSampleForTargetChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.loadType == AudioClipLoadType.DecompressOnLoad,
							s => { s.loadType = AudioClipLoadType.CompressedInMemory; return s; }),
						"Moves the recognized tracker module to Compressed In Memory, avoiding expansion of the full song at load time.",
						basis: SavingsBasis.Unknown,
						verify: c => ReadSampleForTarget(c, targetPlatform, facts.hasTargetOverride)?.loadType == AudioClipLoadType.CompressedInMemory);
				}

			// AUD013 - WebGL imported audio does not support Streaming load type.
			using (RuleTiming.Begin("AUD013", entry.Path))
				if (IsWebGLPlatform(targetPlatform) && facts.loadType == AudioClipLoadType.Streaming)
				{
					entry.AddIssue(
						"AUD013", "WebGL: Streaming", "Compressed In Memory", 0,
						c => SetExplicitSampleOverrideChecked(c, targetPlatform, facts.hasTargetOverride,
							s => s.loadType == AudioClipLoadType.Streaming,
							s => { s.loadType = AudioClipLoadType.CompressedInMemory; return s; }),
						"WebGL does not use Unity's native streaming audio path. Create/update only the WebGL sample-settings override, leaving global default settings unchanged.",
						verify: c => VerifySampleOverrideLoadType(c, targetPlatform, AudioClipLoadType.CompressedInMemory));
				}

			// AUD012 - an explicit target override defeats a Streaming default for a long clip.
			using (RuleTiming.Begin("AUD012", entry.Path))
			{
				string platform = facts.targetPlatform;
				if (!string.IsNullOrEmpty(platform))
				{
					entry.Set("override", facts.hasTargetOverride ? platform : "-");
					if (facts.overrideDefeatsStreamingPolicy && isLong && !IsWebGLPlatform(platform))
					{
						if (aud001Issue != null)
						{
							aud001Issue.Note("Default load type is Streaming, but the explicit " + platform
								+ " override is " + facts.targetOverrideLoadType + ". Review both the default and target override; the canonical finding's options address the effective target setting.", "Target override");
						}
						else entry.AddIssue(
							"AUD012",
							platform + ": " + facts.targetOverrideLoadType,
							platform + ": consider Streaming", 0, null,
							"This rule is specifically about an explicit platform override replacing a Streaming default for a long clip. Review the override rather than changing it automatically.",
							options: new List<FixOption> { new FixOption {
								Label = "Restore Target Streaming Policy",
								Recommended = true,
								Risk = FixRisk.Medium,
								BackupRequiredOverride = true,
								Warning = "This is a Review Fix. Inspect the target state before applying it.",
								Apply = c => SetSampleForTargetChecked(c, platform, true,
										s => s.loadType == facts.targetOverrideLoadType && s.loadType != AudioClipLoadType.Streaming,
										s => { s.loadType = AudioClipLoadType.Streaming; return s; }),
								Verify = c => ReadSampleForTarget(c, platform, true)?.loadType == AudioClipLoadType.Streaming
							} }
						);
					}
				}
				else entry.Set("override", "-");
			}

			entry.Finalise();
			return entry;
		}

		/// <summary>
		/// Estimates resident audio memory for the effective target settings. Exact compressed-memory size is
		/// not publicly exposed for every codec, so unknown compressed formats remain conservative.
		/// Unity documents an approximately 200 KB buffering overhead for Streaming clips.
		/// </summary>
		static long EstimateRuntimeSize(AudioClip clip, AudioImporterSampleSettings sample, AudioImporter importer)
		{
			if (clip == null) return 0;

			long pcmBytes = EstimateDecompressedPcmBytes(clip, sample, importer);

			switch (sample.loadType)
			{
				case AudioClipLoadType.DecompressOnLoad:
					return pcmBytes;

				case AudioClipLoadType.Streaming:
					return 200L * 1024L;

				case AudioClipLoadType.CompressedInMemory:
					switch (sample.compressionFormat)
					{
						case AudioCompressionFormat.ADPCM:
							return Math.Max(1L, (long)Math.Ceiling(pcmBytes / 3.5d));
						case AudioCompressionFormat.PCM:
							return pcmBytes;
						default:
							return 0;
					}

				default:
					return 0;
			}
		}

		static long EstimateDecompressedPcmBytes(float length, int frequency, int channels, bool forceToMono,
			AudioSampleRateSetting sampleRateSetting, uint sampleRateOverride)
		{
			int effectiveChannels = forceToMono ? 1 : Mathf.Max(1, channels);
			uint targetRate = sampleRateSetting == AudioSampleRateSetting.OverrideSampleRate && sampleRateOverride > 0
				? sampleRateOverride
				: (uint)Mathf.Max(1, frequency);
			double bytes = Math.Max(0d, length) * targetRate * effectiveChannels * 2d;
			if (bytes >= long.MaxValue) return long.MaxValue;
			return Math.Max(1L, (long)Math.Ceiling(bytes));
		}

		static long EstimateDecompressedPcmBytes(AudioClip clip, AudioImporterSampleSettings sample,
			AudioImporter importer)
		{
			if (clip == null) return 0;

			int channels = importer != null && importer.forceToMono ? 1 : Mathf.Max(1, clip.channels);
			double duration = Math.Max(0d, clip.length);
			uint targetRate = GetTargetSampleRate(clip, sample);

			double bytes = duration * targetRate * channels * 2d;
			if (bytes >= long.MaxValue) return long.MaxValue;
			return Math.Max(1L, (long)Math.Ceiling(bytes));
		}

		static uint GetTargetSampleRate(AudioClip clip, AudioImporterSampleSettings sample)
		{
			if (sample.sampleRateSetting == AudioSampleRateSetting.OverrideSampleRate
				&& sample.sampleRateOverride > 0)
				return sample.sampleRateOverride;

			return (uint)Mathf.Max(1, clip != null ? clip.frequency : 1);
		}

		static bool IsSampleRateOptimizableFormat(AudioCompressionFormat format)
		{
			return format == AudioCompressionFormat.PCM
				|| format == AudioCompressionFormat.ADPCM;
		}

		static bool IsWebGLPlatform(string platform)
		{
			return !string.IsNullOrEmpty(platform)
				&& string.Equals(platform, "WebGL", StringComparison.OrdinalIgnoreCase);
		}

		static AudioImporterSampleSettings GetEffectiveSampleSettings(
			AudioImporter importer, string platform, out bool hasOverride)
		{
			hasOverride = false;
			if (importer == null) return default(AudioImporterSampleSettings);

			if (!string.IsNullOrEmpty(platform))
			{
				hasOverride = importer.ContainsSampleSettingsOverride(platform);
				return importer.GetOverrideSampleSettings(platform);
			}

			return importer.defaultSampleSettings;
		}

		/// <summary>
		/// Builds review-fix options against the effective target setting. When a platform override exists,
		/// the option edits that override; otherwise it edits the default settings.
		/// </summary>
		static System.Collections.Generic.List<FixOption> BuildLoadTypeOptions(
			AudioClipLoadType recommended, string platform, bool hasOverride)
		{
			return new System.Collections.Generic.List<FixOption>
			{
				LoadTypeOption(AudioClipLoadType.Streaming,
					"Lowest resident memory on supported native platforms: streams from disk using Unity's dedicated "
					+ "streaming path, at the cost of storage I/O and buffering.", recommended, platform, hasOverride),
				LoadTypeOption(AudioClipLoadType.CompressedInMemory,
					"Keeps encoded audio resident and decodes during playback; useful when you want lower memory than "
					+ "Decompress On Load without streaming from disk.", recommended, platform, hasOverride),
				LoadTypeOption(AudioClipLoadType.DecompressOnLoad,
					"Fully decodes and keeps the audio resident, maximizing readiness and minimizing playback decode "
					+ "work at the cost of substantially higher memory usage.", recommended, platform, hasOverride),
			};
		}

		static FixOption LoadTypeOption(AudioClipLoadType loadType, string description,
			AudioClipLoadType recommended, string platform, bool hasOverride)
		{
			return new FixOption
			{
				Label = loadType.ToString(),
				ResultDescription = description,
				Recommended = loadType == recommended,
				Basis = SavingsBasis.Unknown,
				Apply = c => SetSampleForTarget(c, platform, hasOverride, s =>
				{
					s.loadType = loadType;
					return s;
				}),
				Verify = c => ReadSampleForTarget(c, platform, hasOverride)?.loadType == loadType
			};
		}

		static bool IsTrackerModule(string assetPath)
		{
			return assetPath.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)
				|| assetPath.EndsWith(".it", StringComparison.OrdinalIgnoreCase)
				|| assetPath.EndsWith(".s3m", StringComparison.OrdinalIgnoreCase)
				|| assetPath.EndsWith(".xm", StringComparison.OrdinalIgnoreCase);
		}

		static void ApplyForceMonoForReview(FixContext c, int expectedChannels)
		{
			if (c == null || expectedChannels <= 1) throw new InvalidOperationException("AUD005 requires a multichannel clip.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(c.AssetPath);
			if (importer == null || clip == null || clip.channels != expectedChannels || clip.channels <= 1
				|| importer.ambisonic || importer.forceToMono)
				throw new InvalidOperationException("AUD005 channel, ambisonic, or Force To Mono evidence changed since analysis.");
			c.Importer = importer;
			importer.forceToMono = true;
			c.NeedsReimport = true;
		}

		static void SetSampleForTargetChecked(FixContext c, string platform, bool expectedHasOverride,
			Func<AudioImporterSampleSettings, bool> precondition,
			Func<AudioImporterSampleSettings, AudioImporterSampleSettings> mutate)
		{
			if (c == null || mutate == null) throw new InvalidOperationException("Missing audio fix context or mutation.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null) throw new InvalidOperationException("AudioImporter not found for " + c.AssetPath);
			c.Importer = importer;
			bool actualHasOverride = !string.IsNullOrEmpty(platform) && importer.ContainsSampleSettingsOverride(platform);
			if (actualHasOverride != expectedHasOverride)
				throw new InvalidOperationException("Audio target override state changed since the finding was analyzed.");
			AudioImporterSampleSettings current = actualHasOverride && !string.IsNullOrEmpty(platform)
				? importer.GetOverrideSampleSettings(platform)
				: importer.defaultSampleSettings;
			if (precondition != null && !precondition(current))
				throw new InvalidOperationException("Audio importer settings changed since the finding was analyzed.");
			var updated = mutate(current);
			if (actualHasOverride && !string.IsNullOrEmpty(platform))
			{
				if (!importer.SetOverrideSampleSettings(platform, updated))
					throw new InvalidOperationException("Unity rejected the audio override for platform " + platform + ".");
			}
			else importer.defaultSampleSettings = updated;
			c.NeedsReimport = true;
		}

		static void SetExplicitSampleOverrideChecked(FixContext c, string platform, bool expectedHasOverride,
			Func<AudioImporterSampleSettings, bool> precondition,
			Func<AudioImporterSampleSettings, AudioImporterSampleSettings> mutate)
		{
			if (c == null || string.IsNullOrEmpty(platform) || mutate == null)
				throw new InvalidOperationException("A concrete platform is required for an explicit audio override.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null) throw new InvalidOperationException("AudioImporter not found for " + c.AssetPath);
			c.Importer = importer;
			bool actualHasOverride = importer.ContainsSampleSettingsOverride(platform);
			if (actualHasOverride != expectedHasOverride)
				throw new InvalidOperationException("Audio target override state changed since the finding was analyzed.");
			AudioImporterSampleSettings current = importer.GetOverrideSampleSettings(platform);
			if (precondition != null && !precondition(current))
				throw new InvalidOperationException("The platform-specific audio settings changed since the finding was analyzed.");
			var updated = mutate(current);
			if (!importer.SetOverrideSampleSettings(platform, updated))
				throw new InvalidOperationException("Unity rejected the explicit audio override for platform " + platform + ".");
			c.NeedsReimport = true;
		}

		static void SetLoadInBackgroundChecked(FixContext c, bool expectedValue, bool newValue)
		{
			if (c == null) throw new InvalidOperationException("Missing audio fix context.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null) throw new InvalidOperationException("AudioImporter not found for " + c.AssetPath);
			c.Importer = importer;
			if (importer.loadInBackground != expectedValue)
				throw new InvalidOperationException("Load In Background changed since the finding was analyzed.");
			importer.loadInBackground = newValue;
			c.NeedsReimport = true;
		}

		static void SetForceToMonoChecked(FixContext c, bool expectedValue, bool newValue)
		{
			if (c == null) throw new InvalidOperationException("Missing audio fix context.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null) throw new InvalidOperationException("AudioImporter not found for " + c.AssetPath);
			c.Importer = importer;
			// This helper currently serves AUD010 only: the clip must still be ambisonic.
			if (!importer.ambisonic || importer.forceToMono != expectedValue || (newValue && importer.ambisonic))
				throw new InvalidOperationException("Ambisonic or Force To Mono state changed since the finding was analyzed.");
			importer.forceToMono = newValue;
			c.NeedsReimport = true;
		}

		static bool VerifySampleOverrideLoadType(FixContext c, string platform, AudioClipLoadType expected)
		{
			if (c == null || string.IsNullOrEmpty(platform)) return false;
			try
			{
				var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
				return importer != null && importer.ContainsSampleSettingsOverride(platform)
					&& importer.GetOverrideSampleSettings(platform).loadType == expected;
			}
			catch { return false; }
		}

		static void SetSampleForTarget(FixContext c, string platform, bool hasOverride,
			Func<AudioImporterSampleSettings, AudioImporterSampleSettings> mutate)
		{
			if (c == null || mutate == null) return;

			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null)
				throw new InvalidOperationException("AudioImporter not found for " + c.AssetPath);

			if (hasOverride && !string.IsNullOrEmpty(platform))
			{
				var current = importer.GetOverrideSampleSettings(platform);
				var updated = mutate(current);
				if (!importer.SetOverrideSampleSettings(platform, updated))
					throw new InvalidOperationException("Unity rejected the audio override for platform " + platform + ".");
			}
			else
			{
				importer.defaultSampleSettings = mutate(importer.defaultSampleSettings);
			}

			c.NeedsReimport = true;
		}

		static void SetSample(FixContext c,
			Func<AudioImporterSampleSettings, AudioImporterSampleSettings> mutate)
		{
			SetSampleForTarget(c, null, false, mutate);
		}

		static AudioImporterSampleSettings? ReadSampleForTarget(
			FixContext c, string platform, bool hasOverride)
		{
			var importer = AssetImporter.GetAtPath(c.AssetPath) as AudioImporter;
			if (importer == null) return null;

			if (hasOverride && !string.IsNullOrEmpty(platform))
				return importer.GetOverrideSampleSettings(platform);

			return importer.defaultSampleSettings;
		}

		static AudioImporterSampleSettings? ReadSample(FixContext c)
		{
			return ReadSampleForTarget(c, null, false);
		}

		static void SetAudioChannels(AudioImporter ai, int channels) { ai.forceToMono = channels == 1; }

		static void SetLoadInBackground(AudioImporter ai, bool v) { ai.loadInBackground = v; }
		static void SetSample(AudioImporter ai, bool streaming, AudioCompressionFormat? format)
		{
			var s = ai.defaultSampleSettings;
			s.loadType = streaming ? AudioClipLoadType.Streaming : AudioClipLoadType.CompressedInMemory;
			if (format.HasValue) s.compressionFormat = format.Value;
			ai.defaultSampleSettings = s;
		}
	}
}
