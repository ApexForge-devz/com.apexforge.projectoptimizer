using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Video clip import analysis for Unity 6.
	///
	/// The analyzer deliberately distinguishes source facts, the currently imported artifact,
	/// and platform-targeted importer settings. Video transcoding/resolution decisions are
	/// target-dependent, so importer actions always address the selected platform rather than
	/// silently changing the shared Default settings.
	/// </summary>
	public sealed class VideoAnalyzer : IBackgroundAssetAnalyzer
	{
		// These are Unity-supported editor-source/container extensions rather than an arbitrary
		// list of every extension a media tool might understand. The importer itself remains the
		// authority: Handles() only determines which assets this analyzer attempts to inspect.
		static readonly string[] k_Extensions =
		{".asf", ".avi", ".dv", ".m4v", ".mov", ".mp4", ".mpg", ".mpeg", ".ogv", ".vp8", ".webm", ".wmv" };

		public AssetCategory Category => AssetCategory.Video;

		public AssetKindMask SupportedKinds => AssetKindMask.Video;

		static VideoAnalyzer()
		{
			Rules.Register(id: "VID001", title: "Video resolution above the budget", category: AssetCategory.Video,
							why: "The imported VideoClip is larger than the configured height budget for this scan profile. "
							+ "Resolution affects encoded size, decode bandwidth and the amount of image data presented to the renderer. "
							+ "The appropriate size depends on the largest presentation size and target device.",
							fix: "Automatically meet the configured selected-target height budget by lowering spatial quality to the least-reduced supported level that fits, then resizing proportionally if necessary. Change only target settings, require transcoding, and verify encoded dimensions after reimport.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "VID002", title: "Very large imported video", category: AssetCategory.Video,
							why: "The currently imported video artifact is larger than the configured file-size budget. "
							+ "This is a storage/build/download finding, not a runtime-memory measurement. Video file size and runtime decoder memory are separate quantities.",
							fix: "Reduce bitrate, resolution or duration, or use an external/remote delivery strategy when the product architecture permits it.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "VID003", title: "Video transcode was skipped", category: AssetCategory.Video,
							why: "The selected target has transcoding enabled, but Unity reports that the most recent import skipped the transcode operation. The resulting imported artifact can therefore differ from the target transcoding settings until the clip is reimported successfully.",
							fix: "Reimport the clip with transcoding enabled and allow the transcode to complete. Do not disable transcoding merely to clear this finding.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "VID007", title: "VideoClip is unsupported on WebGL", category: AssetCategory.Video,
							why: "Unity 6 does not support embedded VideoClip assets on WebGL. Web builds should use VideoPlayer.url with a browser-playable video hosted or delivered separately.",
							fix: "For WebGL, move the video to an appropriate URL/StreamingAssets delivery workflow and use VideoPlayer.url instead of embedding the VideoClip asset.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "VID004", title: "Video contains imported audio tracks", category: AssetCategory.Video,
							why: "Importing audio retains the clip's audio tracks as part of the imported video. Whether those tracks are actually needed depends on how the VideoPlayer is used at runtime.",
							fix: "Review disabling Import Audio only after confirming that the clip's audio is intentionally unused or provided separately.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "VID005", title: "Alpha retained even though the source has no alpha", category: AssetCategory.Video,
							why: "Keep Alpha only preserves transparency data that exists in the source. When the source exposes no alpha channel, retaining alpha cannot preserve useful transparency and can add unnecessary encoded data when transcoding.",
							fix: "Disable Keep Alpha when the source has no alpha and the target is transcoded. This is a direct importer-state correction.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
								recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "VID006", title: "Video is being upscaled during import", category: AssetCategory.Video,
							why: "A custom transcode size larger than the source can increase encoded data without adding source detail. "
							+ "However, the importer API does not expose a portable source-resolution field suitable for proving this across all containers, so the analyzer must not infer source size from the already-imported VideoClip.",
							fix: "When original source dimensions are positively obtained from the importer OriginalSize query, proportionally clamp selected-target Custom Size dimensions to those source bounds. Do not auto-fix when source dimensions are only inferred from the imported artifact.",
							severity: IssueSeverity.Info,
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
			if (string.IsNullOrEmpty(assetPath)) return false;

			for (int i = 0; i < k_Extensions.Length; i++)
				if (assetPath.EndsWith(k_Extensions[i], StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		[Serializable]
		public sealed class VideoFacts
		{
			public int width;
			public int height;
			public double length;
			public double frameRate;
			public int audioTracks;
			public int sourceAudioTracks;
			public long sourceFileSize;
			public long outputFileSize;
			public bool transcode;
			public bool transcodeSkipped;
			public bool sourceHasAlpha;
			public bool keepAlpha;
			public bool importAudio;
			public string codec = "-";
			public string resizeMode = "-";
			public string spatialQuality = "-";
			public int customWidth;
			public int customHeight;
			public string targetPlatform = "Default";
			public bool hasTargetSettings;
			public bool targetDimensionsKnown;
			public int targetWidth;
			public int targetHeight;
			public VideoSpatialQuality targetSpatialQuality;
			public bool targetCustomResize;
			public int sourceWidth;
			public int sourceHeight;
			public bool originalSourceDimensionsVerified;
			public bool buildReachabilityKnown;
			public bool inBuildReachability;
		}

		public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
		{
			if (mainContext == null || backgroundContext == null || !Handles(assetPath)) return;
			var settings = mainContext.Settings;
			string targetPlatform = PlatformUtility.GetAnalyzerTargetPlatform(mainContext, "Default");
			string dependencyHash = backgroundContext.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			VideoFacts facts = null;
			bool measuredForBackground = false;
			if (mainContext.Cache != null) mainContext.Cache.TryGet(cacheKey, dependencyHash, out facts);
			if (facts == null)
			{
				measuredForBackground = true;
				var importer = AssetImporter.GetAtPath(assetPath) as VideoClipImporter;
				VideoImporterTargetSettings targetSettings = default(VideoImporterTargetSettings);
				bool hasTargetSettings = TryGetTargetSettings(importer, targetPlatform, out targetSettings);
				if (importer == null) return;
				facts = Measure(assetPath, importer, targetPlatform, targetSettings, hasTargetSettings);
				if (facts == null) return;
				ApplyCurrentTargetFacts(facts, importer, targetPlatform, targetSettings, hasTargetSettings, mainContext);
			}
			else
			{
				facts.buildReachabilityKnown = mainContext != null && mainContext.BuildSet != null;
				facts.inBuildReachability = facts.buildReachabilityKnown && mainContext.BuildSet.Contains(assetPath);
			}
			backgroundContext.SeedCached(cacheKey, facts, measuredForBackground);
		}

		public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
		{
			if (context == null || !Handles(assetPath)) return null;
			string targetPlatform = context.Settings.PlatformString;
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			var facts = context.GetOrMeasure<VideoFacts>(assetPath, cacheKey, null);
			if (facts == null) return null;
			return BuildEntry(assetPath, context.Settings, facts, context.GetFileSize(assetPath), context.GetGuid(assetPath));
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var settings = ctx.Settings;
			string targetPlatform = PlatformUtility.GetAnalyzerTargetPlatform(ctx, "Default");
			string dependencyHash = ctx.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, targetPlatform ?? string.Empty);
			VideoFacts facts;
			var importer = AssetImporter.GetAtPath(assetPath) as VideoClipImporter;
			VideoImporterTargetSettings targetSettings;
			bool hasTargetSettings = TryGetTargetSettings(importer, targetPlatform, out targetSettings);
			if (ctx.Cache != null && ctx.Cache.TryGet(cacheKey, dependencyHash, out VideoFacts cached)) facts = cached;
			else
			{
				if (importer == null) return null;
				facts = Measure(assetPath, importer, targetPlatform, targetSettings, hasTargetSettings);
				if (facts == null) return null;
				ctx.Cache?.Set(cacheKey, facts, dependencyHash);
			}
			ApplyCurrentTargetFacts(facts, importer, targetPlatform, targetSettings, hasTargetSettings, ctx);
			return BuildEntry(assetPath, settings, facts, ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath), ctx.GetGuid(assetPath));
		}

		static void ApplyCurrentTargetFacts(VideoFacts facts, VideoClipImporter importer, string targetPlatform, VideoImporterTargetSettings targetSettings, bool hasTargetSettings, ScanContext context)
		{
			if (facts == null) return;
			facts.targetPlatform = targetPlatform ?? string.Empty;
			facts.hasTargetSettings = hasTargetSettings;
			facts.targetSpatialQuality = hasTargetSettings ? targetSettings.spatialQuality : default(VideoSpatialQuality);
			facts.targetCustomResize = HasCustomResize(targetSettings, hasTargetSettings);
			facts.targetWidth = 0; facts.targetHeight = 0;
			facts.targetDimensionsKnown = TryGetTargetEncodedDimensions(importer, facts.width, facts.height, targetSettings, hasTargetSettings, out facts.targetWidth, out facts.targetHeight);
			facts.originalSourceDimensionsVerified = TryGetOriginalSourceDimensions(importer, out facts.sourceWidth, out facts.sourceHeight);
			if (!facts.originalSourceDimensionsVerified)
				TryGetSourceDimensions(importer, facts.width, facts.height, out facts.sourceWidth, out facts.sourceHeight);
			facts.buildReachabilityKnown = context != null && context.BuildSet != null;
			facts.inBuildReachability = facts.buildReachabilityKnown && context.BuildSet.Contains(context.CurrentAssetPath ?? string.Empty);
		}

		AssetEntry BuildEntry(string assetPath, OptimizerSettings settings, VideoFacts facts, long fileSize, string guid)
		{
			string targetPlatform = facts.targetPlatform;

			var entry = new AssetEntry
			{
				Guid = guid ?? string.Empty,
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Video,
				TypeLabel = "Video",
				FileSize = fileSize,
				// Video file size is not runtime memory. Leave RuntimeSize unknown rather than
				// polluting memory totals with a storage figure.
				RuntimeSize = -1
			};

			long pixels = 0;
			if (facts.width > 0 && facts.height > 0)
				pixels = (long)facts.width * facts.height;

			entry.Set("dimensions", facts.width > 0 && facts.height > 0
				? facts.width + " x " + facts.height
				: "unknown");
			entry.Set("pixels", pixels);
			entry.Set("lengthText", facts.length > 0 ? facts.length.ToString("0.0") + " s" : "-");
			entry.Set("frameRate", facts.frameRate > 0 ? (long)Math.Round(facts.frameRate) : 0L);
			entry.Set("audioTracks", (long)Math.Max(0, facts.audioTracks));
			entry.Set("sourceAudioTracks", (long)Math.Max(0, facts.sourceAudioTracks));

			entry.Set("sourceSize", facts.sourceFileSize > 0
				? DisplayUtility.FormatBytes(facts.sourceFileSize)
				: "-");
			entry.Set("importedSize", facts.outputFileSize > 0
				? DisplayUtility.FormatBytes(facts.outputFileSize)
				: "-");
			entry.Set("importedSizeBytes", facts.outputFileSize > 0 ? facts.outputFileSize : 0L);
			entry.Set("runtimeMemory", "not estimated");

			if (facts.width > 0 && facts.height > 0)
			{
				long frameBytes = (long)facts.width * facts.height * 4L;
				entry.Set("frameBufferFloor", DisplayUtility.FormatBytes(frameBytes) + " per uncompressed frame");
			}

			entry.Set("targetPlatform", targetPlatform);
			entry.Set("targetSettings", facts.hasTargetSettings ? "Available" : "Unavailable");
			entry.Set("transcode", facts.transcode ? "Yes" : "No");
			entry.Set("transcodeSkipped", facts.transcodeSkipped ? "Yes" : "No");
			entry.Set("alpha", facts.sourceHasAlpha ? "Source has alpha" : "No source alpha");
			entry.Set("keepAlpha", facts.keepAlpha ? "Yes" : "No");
			entry.Set("importAudio", facts.importAudio ? "Yes" : "No");
			entry.Set("codec", facts.codec);
			entry.Set("resizeMode", facts.resizeMode);
			entry.Set("spatialQuality", facts.spatialQuality);
			entry.Set("customSize", facts.customWidth > 0 && facts.customHeight > 0
				? facts.customWidth + " x " + facts.customHeight
				: "- (not active / not custom)");

			// ---------------------------------------------------------------------
			// VID001 - evaluate the selected target's actual encoded dimensions.
			// VideoSpatialQuality changes the encoded image dimensions even though the
			// VideoClip can continue to report the original source dimensions.
			// ---------------------------------------------------------------------
			int budget = settings.videoHeightWarn;
			int targetWidth = facts.targetDimensionsKnown ? facts.targetWidth : facts.width;
			int targetHeight = facts.targetDimensionsKnown ? facts.targetHeight : facts.height;


			entry.Set("targetEncodedDimensions", facts.targetDimensionsKnown && facts.targetWidth > 0 && facts.targetHeight > 0
				? facts.targetWidth + " x " + facts.targetHeight
				: "unknown");
			entry.Set("targetEncodedHeight", targetHeight > 0 ? (long)targetHeight : 0L);

			using (RuleTiming.Begin("VID001", entry.Path))
				if (facts.hasTargetSettings && facts.transcode && facts.targetDimensionsKnown && facts.targetHeight > 0 && budget > 0 && facts.targetHeight > budget)
				{
					long targetPixels = targetWidth > 0 ? (long)targetWidth * targetHeight : 0L;
					long budgetPixels = targetWidth > 0 ? (long)targetWidth * budget : 0L;
					long excess = targetPixels > budgetPixels ? targetPixels - budgetPixels : 0L;

					var issue = entry.AddIssue("VID001",
						targetWidth + " x " + targetHeight + " target encoded",
						"target height at most " + budget,
						excess,
						c => ApplyVideoHeightBudgetFix(c, targetPlatform, targetWidth, targetHeight, budget),
						"The selected transcoding target resolves above the configured encoded-height budget. AutoFix uses target-specific spatial quality and, if needed, a proportional Custom Size. The excess pixel count is a resolution metric, not a guaranteed file-size saving.",
						SavingsBasis.Unknown,
						verify: c => VerifyVideoHeightBudget(c, targetPlatform, budget),
						options: new List<FixOption> { new FixOption {
							Label = "Reduce Video Resolution Further",
							Recommended = false,
							Risk = FixRisk.Medium,
							BackupRequiredOverride = true,
							Warning = "Optional manual alternative: select a stronger resolution reduction if the verified automatic target is not suitable.",
							Apply = c => ApplyVideoHeightBudgetFix(c, targetPlatform, targetWidth, targetHeight, budget, forceLowQuality: true),
							Verify = c => VerifyVideoHeightBudget(c, targetPlatform, budget)
						} }
					);

					if (issue != null)
					{
						issue.Note("Target " + targetPlatform + ": " + targetWidth + " x " + targetHeight
							+ "; configured height budget: " + budget + ".", "Target scope");
						if (facts.hasTargetSettings && facts.transcode)
							issue.Note("Resize mode: " + facts.resizeMode + "; spatial quality: " + facts.spatialQuality + ".", "Transcode settings");
					}
				}

			// ---------------------------------------------------------------------
			// VID002 - current imported artifact size. This is storage/build information, not memory.
			// ---------------------------------------------------------------------
			bool haveOutputSize = facts.outputFileSize > 0;
			long importedSize = haveOutputSize ? facts.outputFileSize : entry.FileSize;
			long fileBudget = Math.Max(0L, settings.videoFileWarnKb) * 1024L;
			using (RuleTiming.Begin("VID002", entry.Path))
				if (fileBudget > 0 && importedSize > fileBudget)
				{
					long excess = importedSize > fileBudget ? importedSize - fileBudget : 0;
					var issue = entry.AddIssue("VID002",
						DisplayUtility.FormatBytes(importedSize)
						+ (haveOutputSize ? " imported output" : " source/project file"),
						"under " + DisplayUtility.FormatBytes(fileBudget),
						excess,
						null,
						null,
						SavingsBasis.Disk,
						options: BuildVideoSizeReviewOptions(entry.Path, targetPlatform, facts));

					if (issue != null)
					{
						issue.Note("This measures storage/build-oriented file size. It is not a runtime-memory estimate; video decoding uses runtime buffers and decoder state instead of keeping the compressed file wholly in image memory.",
							"What this measures");
						issue.Note("The reported imported size is the currently imported artifact; it should not be interpreted as an exhaustive per-platform build-size prediction when multiple target settings exist.",
							"Target scope");
					}
				}

			// ---------------------------------------------------------------------
			// VID003 - actual import/transcode-state inconsistency.
			// ---------------------------------------------------------------------
			using (RuleTiming.Begin("VID003", entry.Path))
				if (facts.hasTargetSettings && facts.transcode && facts.transcodeSkipped)
				{
					var issue = entry.AddIssue("VID003",
						"Transcoding enabled but last import skipped it",
						"Reimport and allow transcoding to complete",
						0,
						null,
						"Unity reports that transcoding is configured for the selected target but the most recent import did not perform it. "
						+ "The importer settings therefore do not necessarily match the generated artifact.",
						options: new List<FixOption> { new FixOption {
							Label = "Reimport and Complete Video Transcode",
							Recommended = true,
							Risk = FixRisk.Medium,
							BackupRequiredOverride = true,
							Warning = "This can take a long time. Confirm before applying; the verifier requires the skipped-transcode flag to clear.",
							Apply = c => RequestVideoTranscodeRetry(c, targetPlatform),
							Verify = c => VerifyVideoTranscodeCompleted(c, targetPlatform)
						} }
					);

					if (issue != null)
						issue.Note("Target codec setting: " + facts.codec + "; resize mode: " + facts.resizeMode + ".", "Target state");
				}

			// ---------------------------------------------------------------------
			// VID007 - WebGL cannot embed VideoClip assets.
			// ---------------------------------------------------------------------
			using (RuleTiming.Begin("VID007", entry.Path))
				if (string.Equals(targetPlatform, "WebGL", StringComparison.OrdinalIgnoreCase))
				{
					var issue = entry.AddIssue("VID007",
						"VideoClip asset is incompatible with WebGL when embedded",
						"Use VideoPlayer.url for WebGL",
						0,
						null,
						"Unity 6 WebGL does not support embedded VideoClip assets in the player. This is a compatibility risk only when this asset is actually referenced by the WebGL player; an unused project asset can remain in the project without itself breaking WebGL. "
						+ "Prefer a browser-playable URL-based delivery workflow for WebGL.");
					if (issue != null)
					{
						issue.Note(facts.buildReachabilityKnown
							? (facts.inBuildReachability
								? "This asset is present in the current computed build-reachable set."
								: "This asset is not present in the current computed build-reachable set; the issue is retained as a project-level WebGL compatibility advisory.")
							: "Build reachability was not computed for this scan, so actual WebGL usage cannot be proven from this analyzer alone.",
							"Build reachability");
					}
				}

			// ---------------------------------------------------------------------
			// VID004 - imported audio can be disabled, but only as a Review Fix because the analyzer
			// cannot know whether gameplay expects the embedded tracks.
			// ---------------------------------------------------------------------
			using (RuleTiming.Begin("VID004", entry.Path))
				if (facts.sourceAudioTracks > 0 && facts.importAudio)
				{
					var issue = entry.AddIssue("VID004",
						facts.sourceAudioTracks + " source audio track(s) selected for import",
						"Review Fix: disable Import Audio",
						0,
						null,
						"Confirm that no VideoPlayer usage, cutscene system or accessibility/localization workflow depends on the embedded audio before removing it. "
						+ "The action is target-independent because Import Audio is an importer-wide setting. Exact encoded-size savings depend on the source audio and output encoding, so they are not inferred here.",
						basis: SavingsBasis.Unknown,
						options: BuildDisableAudioOptions());

					if (issue != null)
						issue.Note("The source currently exposes " + facts.sourceAudioTracks + " audio track(s). The imported VideoClip may currently expose fewer tracks if its latest import did not reflect the importer setting. Disabling Import Audio does not delete the source media; it changes what Unity imports from it.", "Audio evidence");
				}

			// ---------------------------------------------------------------------
			// VID005 - safe direct importer correction. Only meaningful for transcoded clips.
			// ---------------------------------------------------------------------
			using (RuleTiming.Begin("VID005", entry.Path))
				if (facts.keepAlpha && !facts.sourceHasAlpha && facts.transcode)
				{
					var issue = entry.AddIssue("VID005",
						"Keep Alpha on / source has no alpha",
						"Keep Alpha off",
						0,
						c => DisableKeepAlphaWithPreflight(c, targetPlatform),
						"The source positively reports no alpha and the selected target is transcoded. Disable Keep Alpha only if these conditions still hold at apply time; the fix engine backs up the importer metadata and restores it if verification fails.",
						SavingsBasis.Unknown,
						verify: c => VerifyKeepAlphaDisabled(c, targetPlatform));
				}

			// ---------------------------------------------------------------------
			// VID006 - custom transcode size larger than the source.
			// Source dimensions were extracted on the Unity thread and are now immutable facts.
			// ---------------------------------------------------------------------
			using (RuleTiming.Begin("VID006", entry.Path))
			{
				if (facts.targetCustomResize && facts.originalSourceDimensionsVerified && facts.sourceWidth > 0 && facts.sourceHeight > 0)
				{
					int requestedWidth = facts.customWidth;
					int requestedHeight = facts.customHeight;
					if (requestedWidth > facts.sourceWidth || requestedHeight > facts.sourceHeight)
					{
						var issue = entry.AddIssue("VID006",
							requestedWidth + " x " + requestedHeight + " custom target",
							"Do not upscale above source " + facts.sourceWidth + " x " + facts.sourceHeight,
							0,
							c => ClampCustomVideoDimensionsToSource(c, facts.targetPlatform, facts.sourceWidth, facts.sourceHeight, requestedWidth, requestedHeight),
							"The selected target requests a Custom Size larger than dimensions positively read from the source's OriginalSize setting. The fix proportionally clamps dimensions, changes only this target, and verifies the read-back.",
							SavingsBasis.Unknown,
							verify: c => VerifyCustomVideoDimensions(c, facts.targetPlatform, facts.sourceWidth, facts.sourceHeight));
						if (issue != null)
						{
							issue.Note("Source dimensions: " + facts.sourceWidth + " x " + facts.sourceHeight + "; requested: " + requestedWidth + " x " + requestedHeight + ".", "Source vs target");
							issue.Note("Use a target size no larger than the source unless upscaling is an intentional part of the presentation pipeline.", "Review");
						}
					}
				}
			}

			// Spatial-quality alternatives are attached to VID001 when the resolution budget is exceeded.
			// The canonical finding keeps the spatial-quality evidence and options instead of counting a second issue.
			if (facts.hasTargetSettings
				&& facts.transcode
				&& facts.targetSpatialQuality == VideoSpatialQuality.HighSpatialQuality
				&& facts.targetDimensionsKnown
				&& facts.targetHeight > 0
				&& budget > 0
				&& facts.targetHeight > budget)
			{
				var resolutionIssue = entry.Issues.FirstOrDefault(issue => issue != null && issue.RuleId == "VID001");
				if (resolutionIssue != null)
				{
					resolutionIssue.Note(
						"Spatial quality is High, so Unity does not internally downscale the encoded image. Medium scales the dimensions to 3/4 and Low to 1/2, in addition to the selected resize mode.",
						"Spatial quality");
					resolutionIssue.Note("Target encoded height: " + facts.targetHeight + "; height budget: " + budget + ".", "Target scope");
					var spatialOptions = BuildSpatialQualityOptions(targetPlatform, facts.targetWidth, facts.targetHeight, budget);
					for (int i = 0; i < spatialOptions.Count; i++)
					{
						var option = spatialOptions[i];
						if (option == null || resolutionIssue.Options.Any(existing => existing != null
							&& !string.IsNullOrEmpty(option.ActionId)
							&& string.Equals(existing.ActionId, option.ActionId, StringComparison.Ordinal))) continue;
						resolutionIssue.Options.Add(option);
					}
				}
			}

			entry.Finalise();
			return entry;
		}

		static List<FixOption> BuildDisableAudioOptions()
		{
			return new List<FixOption>
			{
				new FixOption
				{
					ActionId = "VID004_DISABLE_IMPORT_AUDIO",
					Label = "Disable Import Audio",
					ResultDescription = "Stops Unity from importing the video's embedded audio tracks.",
					Recommended = false,
					Basis = SavingsBasis.Disk,
					Risk = FixRisk.Medium,
					BackupRequiredOverride = true,
					Warning = "Do this only after confirming the embedded audio is not used by the runtime video workflow.",
					Apply = c => DisableImportedAudioAfterPreflight(c),
					Verify = c =>
					{
						var vi = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
						return vi != null && !vi.importAudio;
					}
				}
			};
		}

		static List<FixOption> BuildSpatialQualityOptions(
			string platform, int targetWidth, int targetHeight, int budget)
		{
			var options = new List<FixOption>();

			int mediumHeight = Math.Max(1, (int)Math.Round(targetHeight * 0.75));
			int lowHeight = Math.Max(1, (int)Math.Round(targetHeight * 0.5));

			if (mediumHeight < targetHeight)
			{
				options.Add(SpatialQualityOption(
					platform,
					VideoSpatialQuality.HighSpatialQuality,
					VideoSpatialQuality.MediumSpatialQuality,
					"MediumSpatialQuality",
					"Scales the encoded image dimensions to 3/4 (approximately "
					+ Math.Max(1, (int)Math.Round(targetWidth * 0.75)) + " x " + mediumHeight
					+ ")."
					+ (mediumHeight <= budget ? " Meets the configured height budget." : " Still exceeds the configured height budget; review the result."),
					mediumHeight <= budget));
			}

			if (lowHeight < targetHeight)
			{
				options.Add(SpatialQualityOption(
					platform,
					VideoSpatialQuality.HighSpatialQuality,
					VideoSpatialQuality.LowSpatialQuality,
					"LowSpatialQuality",
					"Scales the encoded image dimensions to 1/2 (approximately "
					+ Math.Max(1, (int)Math.Round(targetWidth * 0.5)) + " x " + lowHeight
					+ "). Strongest reduction, with greater visual-softness risk."
					,
					mediumHeight > budget && lowHeight <= budget));
			}

			return options;
		}

		static FixOption SpatialQualityOption(string platform, VideoSpatialQuality expectedQuality,
			VideoSpatialQuality quality, string actionSuffix, string description, bool recommended)
		{
			return new FixOption
			{
				ActionId = "VID001_SpatialQuality_" + platform + "_" + actionSuffix,
				Label = quality == VideoSpatialQuality.MediumSpatialQuality ? "Medium" : "Low",
				ResultDescription = description,
				Recommended = recommended,
				Basis = SavingsBasis.Unknown,
				Risk = FixRisk.Medium,
				BackupRequiredOverride = true,
				Warning = "This changes the encoded image dimensions for the selected target platform and may reduce visual sharpness.",
				Apply = c => SetVideoSpatialQualityChecked(c, platform, expectedQuality, quality),
				Verify = c => ReadVideoSpatialQuality(c, platform) == quality
			};
		}

		static void SetVideoSpatialQualityChecked(FixContext c, string platform, VideoSpatialQuality expectedQuality, VideoSpatialQuality quality)
		{
			if (c == null || string.IsNullOrEmpty(platform)) throw new InvalidOperationException("A selected video target is required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target)
				|| !target.enableTranscoding || target.spatialQuality != expectedQuality || target.spatialQuality == quality)
				throw new InvalidOperationException("Selected target or spatial-quality state changed since analysis.");
			c.Importer = importer;
			target.spatialQuality = quality;
			importer.SetTargetSettings(platform, target);
			c.NeedsReimport = true;
		}

		static bool HasCustomResize(VideoImporterTargetSettings settings, bool available)
		{
			if (!available) return false;

			return settings.resizeMode == VideoResizeMode.CustomSize
				   && settings.customWidth > 0
				   && settings.customHeight > 0;
		}

		static bool TryGetTargetSettings(VideoClipImporter importer, string platform,
			out VideoImporterTargetSettings settings)
		{
			settings = default(VideoImporterTargetSettings);
			if (importer == null || string.IsNullOrEmpty(platform)) return false;

			try
			{
				settings = importer.GetTargetSettings(platform);
				return true;
			}
			catch
			{
				return false;
			}
		}

		static bool TryGetSourceDimensions(
			VideoClipImporter importer, int importedWidth, int importedHeight,
			out int width, out int height)
		{
			width = 0;
			height = 0;
			if (importer == null) return false;

			try
			{
				width = importer.GetResizeWidth(VideoResizeMode.OriginalSize);
				height = importer.GetResizeHeight(VideoResizeMode.OriginalSize);
				if (width > 0 && height > 0) return true;
			}
			catch
			{
				// Fall through to the imported artifact dimensions.
			}

			width = importedWidth;
			height = importedHeight;
			return width > 0 && height > 0;
		}

		static bool TryGetTargetEncodedDimensions(
			VideoClipImporter importer, int importedWidth, int importedHeight,
			VideoImporterTargetSettings targetSettings, bool hasTargetSettings,
			out int width, out int height)
		{
			width = 0;
			height = 0;

			if (!hasTargetSettings || importer == null)
			{
				width = importedWidth;
				height = importedHeight;
				return width > 0 && height > 0;
			}

			if (!targetSettings.enableTranscoding)
			{
				return TryGetSourceDimensions(importer, importedWidth, importedHeight, out width, out height);
			}

			try
			{
				switch (targetSettings.resizeMode)
				{
					case VideoResizeMode.CustomSize:
						width = targetSettings.customWidth;
						height = targetSettings.customHeight;
						break;

					default:
						width = importer.GetResizeWidth(targetSettings.resizeMode);
						height = importer.GetResizeHeight(targetSettings.resizeMode);
						break;
				}
			}
			catch
			{
				return false;
			}

			if (width <= 0 || height <= 0) return false;

			double spatialScale = 1.0;
			switch (targetSettings.spatialQuality)
			{
				case VideoSpatialQuality.MediumSpatialQuality:
					spatialScale = 0.75;
					break;
				case VideoSpatialQuality.LowSpatialQuality:
					spatialScale = 0.5;
					break;
			}

			width = Math.Max(1, (int)Math.Round(width * spatialScale));
			height = Math.Max(1, (int)Math.Round(height * spatialScale));
			return true;
		}

		static long ToLongSize(ulong size)
		{
			return size > long.MaxValue ? long.MaxValue : (long)size;
		}

		static VideoFacts Measure(string assetPath, VideoClipImporter importer,
			string targetPlatform, VideoImporterTargetSettings targetSettings, bool hasTargetSettings)
		{
			var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(assetPath);
			if (clip == null && importer == null) return null;

			var facts = new VideoFacts
			{
				sourceAudioTracks = Math.Max(0, (int)importer.sourceAudioTrackCount),
				sourceFileSize = ToLongSize(importer.sourceFileSize),
				outputFileSize = ToLongSize(importer.outputFileSize),
				sourceHasAlpha = importer.sourceHasAlpha,
				keepAlpha = importer.keepAlpha,
				importAudio = importer.importAudio,
				transcodeSkipped = importer.transcodeSkipped,
				targetPlatform = targetPlatform
			};

			if (hasTargetSettings)
			{
				facts.transcode = targetSettings.enableTranscoding;
				facts.codec = targetSettings.codec.ToString();
				facts.resizeMode = targetSettings.resizeMode.ToString();
				facts.spatialQuality = targetSettings.spatialQuality.ToString();
				facts.customWidth = targetSettings.customWidth;
				facts.customHeight = targetSettings.customHeight;
			}
			else
			{
				// Preserve visibility of the importer default even if the requested platform name
				// is unavailable, but do not pretend it represents the requested target.
				var fallback = importer.defaultTargetSettings;
				if (fallback != null)
				{
					facts.transcode = fallback.enableTranscoding;
					facts.codec = fallback.codec.ToString();
					facts.resizeMode = fallback.resizeMode.ToString();
					facts.spatialQuality = fallback.spatialQuality.ToString();
					facts.customWidth = fallback.customWidth;
					facts.customHeight = fallback.customHeight;
				}
			}

			if (clip != null)
			{
				facts.width = clip.width > int.MaxValue ? int.MaxValue : (int)clip.width;
				facts.height = clip.height > int.MaxValue ? int.MaxValue : (int)clip.height;
				facts.length = clip.length;
				facts.frameRate = clip.frameRate;
				facts.audioTracks = Math.Max(0, (int)clip.audioTrackCount);
			}

			return facts;
		}

		static bool? ReadMemberBool(string assetPath, string name)
		{
			try
			{
				var importer = AssetImporter.GetAtPath(assetPath) as VideoClipImporter;
				if (importer == null) return null;
				var p = importer.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (p == null) return null;
				return Convert.ToBoolean(p.GetValue(importer, null));
			}
			catch { return null; }
		}
		static int GetInt(object obj, string name, int fallback)
		{
			if (obj == null) return fallback;
			try { var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); return p != null ? Convert.ToInt32(p.GetValue(obj, null)) : fallback; } catch { return fallback; }
		}
		static object FindEnum(string typeName, string name)
		{
			var t = Type.GetType(typeName);
			return t != null && t.IsEnum ? Enum.Parse(t, name) : null;
		}
		static bool TryGetOriginalSourceDimensions(VideoClipImporter importer, out int width, out int height)
		{
			width = 0;
			height = 0;
			if (importer == null) return false;
			try
			{
				width = importer.GetResizeWidth(VideoResizeMode.OriginalSize);
				height = importer.GetResizeHeight(VideoResizeMode.OriginalSize);
				return width > 0 && height > 0;
			}
			catch
			{
				width = 0;
				height = 0;
				return false;
			}
		}

		static void ApplyVideoHeightBudgetFix(FixContext c, string platform, int expectedWidth, int expectedHeight,
			int budget, bool forceLowQuality = false)
		{
			if (c == null || string.IsNullOrEmpty(platform) || budget <= 0)
				throw new InvalidOperationException("A valid target platform and positive video height budget are required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null) throw new InvalidOperationException("VideoClipImporter not found for " + c.AssetPath);
			c.Importer = importer;
			if (!TryGetTargetSettings(importer, platform, out var target) || !target.enableTranscoding)
				throw new InvalidOperationException("The selected target is unavailable or video transcoding is disabled.");
			var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(c.AssetPath);
			int sourceOrImportedWidth = clip != null && clip.width > 0 ? (int)Math.Min(int.MaxValue, clip.width) : 0;
			int sourceOrImportedHeight = clip != null && clip.height > 0 ? (int)Math.Min(int.MaxValue, clip.height) : 0;
			if (!TryGetTargetEncodedDimensions(importer, sourceOrImportedWidth, sourceOrImportedHeight, target, true, out int currentWidth, out int currentHeight)
				|| currentWidth != expectedWidth || currentHeight != expectedHeight || currentHeight <= budget)
				throw new InvalidOperationException("The video target dimensions changed since the finding was analyzed.");

			c.Importer = importer;

			VideoSpatialQuality nextQuality = target.spatialQuality;
			if (forceLowQuality) nextQuality = VideoSpatialQuality.LowSpatialQuality;
			else if (target.spatialQuality == VideoSpatialQuality.HighSpatialQuality)
			{
				if (ScaleDimension(currentHeight, 0.75) <= budget) nextQuality = VideoSpatialQuality.MediumSpatialQuality;
				else nextQuality = VideoSpatialQuality.LowSpatialQuality;
			}
			else if (target.spatialQuality == VideoSpatialQuality.MediumSpatialQuality
				&& ScaleDimension(currentHeight, 2d / 3d) <= budget)
				nextQuality = VideoSpatialQuality.LowSpatialQuality;
			if (forceLowQuality) nextQuality = VideoSpatialQuality.LowSpatialQuality;
			target.spatialQuality = nextQuality;

			int baseWidth, baseHeight;
			if (!TryGetResizeBaseDimensions(importer, target, out baseWidth, out baseHeight))
				throw new InvalidOperationException("Unable to determine the selected target's base resize dimensions.");
			int effectiveHeight = ScaleDimension(baseHeight, SpatialScale(nextQuality));
			if (effectiveHeight > budget)
			{
				// Preserve the existing aspect ratio, applying a small rounding margin so the output
				// verifier can prove the final encoded height is at or below the budget.
				int maxBaseHeight = Math.Max(1, (int)Math.Floor((budget + 0.49d) / SpatialScale(nextQuality)));
				int newHeight = Math.Min(baseHeight, maxBaseHeight);
				int newWidth = Math.Max(1, (int)Math.Round((double)baseWidth * newHeight / baseHeight));
				if (newHeight >= baseHeight && newWidth >= baseWidth)
					throw new InvalidOperationException("The importer could not produce a smaller target within the configured budget.");
				target.resizeMode = VideoResizeMode.CustomSize;
				target.customWidth = newWidth;
				target.customHeight = newHeight;
			}
			importer.SetTargetSettings(platform, target);
			c.NeedsReimport = true;
		}

		static bool VerifyVideoHeightBudget(FixContext c, string platform, int budget)
		{
			if (c == null || string.IsNullOrEmpty(platform) || budget <= 0) return false;
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target) || !target.enableTranscoding) return false;
			var clip = AssetDatabase.LoadAssetAtPath<VideoClip>(c.AssetPath);
			int width = clip != null && clip.width > 0 ? (int)Math.Min(int.MaxValue, clip.width) : 0;
			int height = clip != null && clip.height > 0 ? (int)Math.Min(int.MaxValue, clip.height) : 0;
			return TryGetTargetEncodedDimensions(importer, width, height, target, true, out _, out int encodedHeight)
				&& encodedHeight > 0 && encodedHeight <= budget;
		}

		static bool TryGetResizeBaseDimensions(VideoClipImporter importer, VideoImporterTargetSettings target, out int width, out int height)
		{
			width = 0;
			height = 0;
			try
			{
				if (target.resizeMode == VideoResizeMode.CustomSize)
				{
					width = target.customWidth;
					height = target.customHeight;
				}
				else
				{
					width = importer.GetResizeWidth(target.resizeMode);
					height = importer.GetResizeHeight(target.resizeMode);
				}
				return width > 0 && height > 0;
			}
			catch { return false; }
		}

		static double SpatialScale(VideoSpatialQuality quality)
		{
			switch (quality)
			{
				case VideoSpatialQuality.MediumSpatialQuality: return 0.75d;
				case VideoSpatialQuality.LowSpatialQuality: return 0.5d;
				default: return 1d;
			}
		}

		static int ScaleDimension(int value, double scale) => Math.Max(1, (int)Math.Round(value * scale));

		static List<FixOption> BuildVideoSizeReviewOptions(string assetPath, string platform, VideoFacts facts)
		{
			var options = new List<FixOption>();
			if (facts == null || string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(platform) || !facts.hasTargetSettings || !facts.transcode) return options;
			if (facts.targetSpatialQuality != VideoSpatialQuality.MediumSpatialQuality)
				options.Add(VideoSpatialQualityReviewOption(platform, facts.targetSpatialQuality, VideoSpatialQuality.MediumSpatialQuality, "Use Medium Spatial Quality"));
			if (facts.targetSpatialQuality != VideoSpatialQuality.LowSpatialQuality)
				options.Add(VideoSpatialQualityReviewOption(platform, facts.targetSpatialQuality, VideoSpatialQuality.LowSpatialQuality, "Use Low Spatial Quality"));

			// Capture raw target resize dimensions before spatial-quality scaling. Scaling already-scaled
			// dimensions would apply spatial quality twice and reduce the output more than 25%.
			var importer = AssetImporter.GetAtPath(assetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var currentTarget) || !currentTarget.enableTranscoding
				|| !TryGetResizeBaseDimensions(importer, currentTarget, out int baseWidth, out int baseHeight)
				|| baseWidth <= 1 || baseHeight <= 1) return options;
			int reducedHeight = ScaleDimension(baseHeight, 0.75d);
			int reducedWidth = Math.Max(1, (int)Math.Round((double)baseWidth * reducedHeight / baseHeight));
			if (reducedWidth >= baseWidth && reducedHeight >= baseHeight) return options;
			VideoResizeMode expectedResizeMode = currentTarget.resizeMode;
			int expectedCustomWidth = currentTarget.customWidth;
			int expectedCustomHeight = currentTarget.customHeight;
			options.Add(new FixOption
			{
				ActionId = "VID002_REDUCE_CUSTOM_SIZE_75_" + platform,
				Label = "Reduce Target Dimensions by 25%",
				ResultDescription = "Changes only the selected target to a proportional Custom Size approximately 75% of the current encoded dimensions.",
				Recommended = false,
				Risk = FixRisk.Medium,
				BackupRequiredOverride = true,
				Warning = "Review Fix: reduces video resolution and may soften the image.",
				Apply = c => ApplyVideoSizeScaleReview(c, platform, expectedResizeMode, expectedCustomWidth, expectedCustomHeight, baseWidth, baseHeight, reducedWidth, reducedHeight),
				Verify = c => VerifyVideoSizeScaleReview(c, platform, reducedWidth, reducedHeight)
			});
			return options;
		}

		static FixOption VideoSpatialQualityReviewOption(string platform, VideoSpatialQuality expectedQuality, VideoSpatialQuality quality, string label)
		{
			return new FixOption
			{
				ActionId = "VID002_SPATIAL_" + platform + "_" + quality,
				Label = label,
				ResultDescription = quality == VideoSpatialQuality.MediumSpatialQuality
					? "Reduces target spatial quality to approximately 75% of the encoded dimensions."
					: "Reduces target spatial quality to approximately 50% of the encoded dimensions.",
				Recommended = false,
				Risk = FixRisk.Medium,
				BackupRequiredOverride = true,
				Warning = "Review Fix: confirm that the visual-quality trade-off is acceptable.",
				Apply = c => SetVideoSpatialQualityForReview(c, platform, expectedQuality, quality),
				Verify = c => ReadVideoSpatialQuality(c, platform) == quality
			};
		}

		static void SetVideoSpatialQualityForReview(FixContext c, string platform, VideoSpatialQuality expectedQuality, VideoSpatialQuality quality)
		{
			if (c == null || string.IsNullOrEmpty(platform)) throw new InvalidOperationException("A selected video target is required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target) || !target.enableTranscoding
				|| target.spatialQuality != expectedQuality || target.spatialQuality == quality)
				throw new InvalidOperationException("Selected video target or spatial-quality state changed since analysis.");
			c.Importer = importer;
			target.spatialQuality = quality;
			importer.SetTargetSettings(platform, target);
			c.NeedsReimport = true;
		}

		static VideoSpatialQuality? ReadVideoSpatialQuality(FixContext c, string platform)
		{
			var importer = c != null ? AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter : null;
			return importer != null && TryGetTargetSettings(importer, platform, out var target) ? target.spatialQuality : (VideoSpatialQuality?)null;
		}

		static void ApplyVideoSizeScaleReview(FixContext c, string platform, VideoResizeMode expectedResizeMode,
			int expectedCustomWidth, int expectedCustomHeight, int expectedBaseWidth, int expectedBaseHeight,
			int expectedNewWidth, int expectedNewHeight)
		{
			if (c == null || string.IsNullOrEmpty(platform) || expectedBaseWidth <= 0 || expectedBaseHeight <= 0
				|| expectedNewWidth <= 0 || expectedNewHeight <= 0)
				throw new InvalidOperationException("A selected video target and positive dimensions are required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target) || !target.enableTranscoding
				|| target.resizeMode != expectedResizeMode)
				throw new InvalidOperationException("Selected video target or resize mode changed since analysis.");
			c.Importer = importer;
			if ((expectedResizeMode == VideoResizeMode.CustomSize
					&& (target.customWidth != expectedCustomWidth || target.customHeight != expectedCustomHeight))
				|| !TryGetResizeBaseDimensions(importer, target, out int width, out int height)
				|| width != expectedBaseWidth || height != expectedBaseHeight)
				throw new InvalidOperationException("Selected target dimensions changed since analysis.");
			target.resizeMode = VideoResizeMode.CustomSize;
			target.customWidth = expectedNewWidth;
			target.customHeight = expectedNewHeight;
			importer.SetTargetSettings(platform, target);
			c.NeedsReimport = true;
		}

		static bool VerifyVideoSizeScaleReview(FixContext c, string platform, int expectedWidth, int expectedHeight)
		{
			var importer = c != null ? AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter : null;
			return importer != null && TryGetTargetSettings(importer, platform, out var target)
				&& target.enableTranscoding && target.resizeMode == VideoResizeMode.CustomSize
				&& target.customWidth == expectedWidth && target.customHeight == expectedHeight;
		}

		static void RequestVideoTranscodeRetry(FixContext c, string platform)
		{
			if (c == null || string.IsNullOrEmpty(platform)) throw new InvalidOperationException("A selected video target is required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target)
				|| !target.enableTranscoding || !importer.transcodeSkipped)
				throw new InvalidOperationException("The transcode is no longer in the skipped-but-enabled state; rescan before retrying.");
			c.Importer = importer;
			// Reimport is the intended operation. Do not toggle any other target settings.
			c.NeedsReimport = true;
		}

		static bool VerifyVideoTranscodeCompleted(FixContext c, string platform)
		{
			var importer = c != null ? AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter : null;
			return importer != null && TryGetTargetSettings(importer, platform, out var target)
				&& target.enableTranscoding && !importer.transcodeSkipped;
		}

		static void DisableImportedAudioAfterPreflight(FixContext c)
		{
			if (c == null) throw new InvalidOperationException("Missing video fix context.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !importer.importAudio || importer.sourceAudioTrackCount <= 0)
				throw new InvalidOperationException("The video audio-import state changed or no source audio tracks remain.");
			c.Importer = importer;
			importer.importAudio = false;
			c.NeedsReimport = true;
		}

		static void DisableKeepAlphaWithPreflight(FixContext c, string platform)
		{
			if (c == null || string.IsNullOrEmpty(platform)) throw new InvalidOperationException("A selected video target is required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !importer.keepAlpha || importer.sourceHasAlpha)
				throw new InvalidOperationException("Keep Alpha or source-alpha evidence changed since analysis.");
			if (!TryGetTargetSettings(importer, platform, out var target) || !target.enableTranscoding)
				throw new InvalidOperationException("The selected target is unavailable or transcoding is disabled.");
			c.Importer = importer;
			importer.keepAlpha = false;
			c.NeedsReimport = true;
		}

		static bool VerifyKeepAlphaDisabled(FixContext c, string platform)
		{
			var importer = c != null ? AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter : null;
			return importer != null && !importer.keepAlpha && !importer.sourceHasAlpha
				&& TryGetTargetSettings(importer, platform, out var target) && target.enableTranscoding;
		}

		static void ClampCustomVideoDimensionsToSource(FixContext c, string platform, int sourceWidth, int sourceHeight,
			int expectedWidth, int expectedHeight)
		{
			if (c == null || string.IsNullOrEmpty(platform) || sourceWidth <= 0 || sourceHeight <= 0)
				throw new InvalidOperationException("Verified source dimensions and target platform are required.");
			c.EnsureWritable();
			var importer = AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter;
			if (importer == null || !TryGetTargetSettings(importer, platform, out var target)
				|| !target.enableTranscoding || target.resizeMode != VideoResizeMode.CustomSize)
				throw new InvalidOperationException("Selected target is no longer using Custom Size transcoding.");
			c.Importer = importer;
			if (target.customWidth != expectedWidth || target.customHeight != expectedHeight
				|| (target.customWidth <= sourceWidth && target.customHeight <= sourceHeight))
				throw new InvalidOperationException("The custom target dimensions changed or no longer exceed the source.");
			double scale = Math.Min((double)sourceWidth / target.customWidth, (double)sourceHeight / target.customHeight);
			target.customWidth = Math.Max(1, Math.Min(sourceWidth, (int)Math.Floor(target.customWidth * scale)));
			target.customHeight = Math.Max(1, Math.Min(sourceHeight, (int)Math.Floor(target.customHeight * scale)));
			importer.SetTargetSettings(platform, target);
			c.NeedsReimport = true;
		}

		static bool VerifyCustomVideoDimensions(FixContext c, string platform, int sourceWidth, int sourceHeight)
		{
			var importer = c != null ? AssetImporter.GetAtPath(c.AssetPath) as VideoClipImporter : null;
			return importer != null && TryGetTargetSettings(importer, platform, out var target)
				&& target.resizeMode == VideoResizeMode.CustomSize && target.customWidth > 0 && target.customHeight > 0
				&& target.customWidth <= sourceWidth && target.customHeight <= sourceHeight;
		}

	}
}
