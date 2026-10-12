using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.U2D;
using UnityEditor;
using UnityEditor.U2D;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Sprite Atlas import, packing, resolution and platform-settings analysis for Unity 6.
	/// Target-dependent findings are evaluated against the selected Sprite Atlas platform override,
	/// falling back to DefaultTexturePlatform when the target does not have an explicit override.
	/// Variant atlases are treated separately because their packed texture is derived from the master.
	/// </summary>
	public sealed class SpriteAtlasAnalyzer : IAssetAnalyzer
	{
		const string k_DefaultTexturePlatform = "DefaultTexturePlatform";
		const int k_HighPaddingPixels = 8;
		const int k_MinUsefulSpriteCount = 1;

		public AssetCategory Category => AssetCategory.SpriteAtlas;

		public AssetKindMask SupportedKinds => AssetKindMask.FontAtlas;

		static SpriteAtlasAnalyzer()
		{
			Rules.Register(id: "ATL001", title: "Atlas is readable at runtime", category: AssetCategory.SpriteAtlas,
							why: "Read/Write enables CPU access to the generated packed atlas texture. Unity creates additional CPU-addressable texture data for readable textures, and this is only appropriate when code or tooling actually needs pixel access.",
							fix: "Review Fix: disable Read/Write after confirming that no runtime or editor workflow needs CPU access to the packed atlas.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "ATL002", title: "Atlas texture is uncompressed", category: AssetCategory.SpriteAtlas,
							why: "An uncompressed packed atlas can require substantially more GPU memory and bandwidth than a platform-supported compressed representation. The exact GPU format is target dependent.",
							fix: "AutoFix: use platform Automatic texture compression at Normal quality for the selected target. Explicitly marked lossless/precision atlases are excluded from this automatic path.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true,
							recommendationConfidence: OptimizationConfidence.HighConfidence
							);

			Rules.Register(id: "ATL003", title: "Atlas max size above the budget", category: AssetCategory.SpriteAtlas,
							why: "Max Texture Size is an upper bound for the generated atlas, not its guaranteed runtime size. Lowering the limit is useful only when all contained sprites remain adequately resolved.",
							fix: "Review Fix: set the selected platform's atlas maximum to the configured budget. This creates or changes only that platform override.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "ATL004", title: "Space-saving packing options disabled", category: AssetCategory.SpriteAtlas,
							why: "Tight Packing and rotation can improve atlas density, but both are content-dependent. Rotation is unsuitable for Canvas UI textures whose orientation must remain fixed.",
							fix: "AutoFix: enable Tight Packing when it is disabled on a populated master atlas. Rotation is deliberately left unchanged.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true,
							recommendationConfidence: OptimizationConfidence.HighConfidence
							);

			Rules.Register(id: "ATL005", title: "Atlas is included in the build but has no packables", category: AssetCategory.SpriteAtlas,
							why: "An included master Sprite Atlas with no packable objects and no packed sprites contributes no useful atlas content. Empty or intentionally reserved atlases can still exist during development.",
							fix: "AutoFix: exclude a genuinely empty included master atlas from the build. Explicitly marked reserved/template atlases are left untouched.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Medium,
							backupRequired: true,
							recommendationConfidence: OptimizationConfidence.Proven
							);

			Rules.Register(id: "ATL006", title: "Atlas mipmaps enabled", category: AssetCategory.SpriteAtlas,
							why: "Mipmaps increase packed-atlas memory and storage, but can improve minification quality for sprites rendered at varying projected sizes. They are often unnecessary for fixed-size UI.",
							fix: "Review Fix: disable mipmaps for atlases that are confirmed not to require mipmapping.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PAA003", title: "Sprite Atlas has too much empty space", category: AssetCategory.SpriteAtlas,
							why: "Unity Project Auditor measures the generated atlas preview and flags atlases whose pixels are predominantly empty. Empty atlas area still consumes texture memory and storage.",
							fix: "Reorganize the atlas packables, padding and packing settings to improve density. Project Optimizer does not auto-repack assets.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "ATL007", title: "Atlas packing padding is high", category: AssetCategory.SpriteAtlas,
							why: "Padding reserves boundary pixels around packed sprites to reduce neighboring-pixel bleeding. Excessive padding consumes atlas area and can reduce packing efficiency, especially when many sprites are present.",
							fix: "AutoFix: reduce packing padding to 8 px when it is higher, for populated atlases without mipmaps or explicit bleed-sensitive markers. Never automatically reduce it to 4 px.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true,
							recommendationConfidence: OptimizationConfidence.HighConfidence
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return false;

			return assetPath.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase)
				   || assetPath.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase);
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(assetPath);
			if (atlas == null) return null;

			var entry = new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.SpriteAtlas,
				TypeLabel = "Sprite Atlas",
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath),
				RuntimeSize = -1
			};

			long generatedRuntimeBytes = 0;
			long generatedReadableBytes = 0;
			long generatedMipSavings = 0;
			try
			{
				var previewMethod = typeof(SpriteAtlasExtensions).GetMethod("GetPreviewTextures", BindingFlags.Static | BindingFlags.NonPublic);
				var previews = previewMethod != null ? previewMethod.Invoke(null, new object[] { atlas }) as Texture2D[] : null;
				if (previews != null) for (int i = 0; i < previews.Length; i++)
				{
					var preview = previews[i]; if (preview == null) continue;
					long bytes = UnityEditorUtility.RuntimeSizeOf(preview); if (bytes > 0) generatedRuntimeBytes += bytes;
					if (preview.isReadable) { try { generatedReadableBytes += preview.GetRawTextureData<byte>().Length; } catch { } }
					if (preview.mipmapCount > 1) generatedMipSavings += EstimateMipOverhead(preview.width, preview.height, preview.mipmapCount, bytes);
				}
			}
			catch { }

			entry.RuntimeSize = generatedRuntimeBytes > 0 ? generatedRuntimeBytes : -1;
			entry.Set("generatedRuntimeBytes", generatedRuntimeBytes);
			entry.Set("generatedReadableBytes", generatedReadableBytes);
			entry.Set("mipSavingsBytes", generatedMipSavings);

			var texture = GetAtlasTextureSettings(assetPath, atlas);
			var packing = GetAtlasPackingSettings(assetPath, atlas);
			var packables = atlas.GetPackables();
			int packableCount = packables != null ? packables.Length : 0;
			int spriteCount = Math.Max(0, atlas.spriteCount);
			bool includeInBuild = IsAtlasIncludedInBuild(assetPath, atlas);
			bool isVariant = atlas.isVariant;
			float variantScale = isVariant ? GetVariantScale(assetPath) : 0f;
			SpriteAtlas masterAtlas = isVariant ? SpriteAtlasExtensions.GetMasterAtlas(atlas) : null;

			string targetPlatform = PlatformUtility.GetAnalyzerTargetPlatform(ctx, k_DefaultTexturePlatform);
			TextureImporterPlatformSettings targetSettings;
			TextureImporterPlatformSettings defaultSettings;
			bool targetSettingsAvailable = TryGetPlatformSettings(assetPath, atlas, targetPlatform, out targetSettings);
			bool defaultSettingsAvailable = TryGetPlatformSettings(assetPath, atlas, k_DefaultTexturePlatform, out defaultSettings);

			bool platformOverridden = targetSettingsAvailable && targetSettings.overridden;
			TextureImporterPlatformSettings effectivePlatformSettings = targetSettings;

			if (!platformOverridden
				&& !string.Equals(targetPlatform, k_DefaultTexturePlatform, StringComparison.OrdinalIgnoreCase)
				&& defaultSettingsAvailable)
			{
				effectivePlatformSettings = defaultSettings;
			}

			bool effectiveSettingsAvailable = targetSettingsAvailable || defaultSettingsAvailable;
			int effectiveMax = effectiveSettingsAvailable && effectivePlatformSettings.maxTextureSize > 0
				? effectivePlatformSettings.maxTextureSize
				: texture.maxTextureSize;

			TextureImporterCompression effectiveCompression = effectiveSettingsAvailable
				? effectivePlatformSettings.textureCompression
				: TextureImporterCompression.Uncompressed;

			TextureImporterFormat effectiveFormat = effectiveSettingsAvailable
				? effectivePlatformSettings.format
				: TextureImporterFormat.Automatic;

			bool effectiveCrunch = effectiveSettingsAvailable && effectivePlatformSettings.crunchedCompression;
			bool effectiveExplicitFormat = effectiveSettingsAvailable
				&& (platformOverridden || string.Equals(targetPlatform, k_DefaultTexturePlatform, StringComparison.OrdinalIgnoreCase));

			// A non-overridden target inherits the DefaultTexturePlatform settings. The target-specific
			// override itself is still kept visible so a finding never claims the target has an override
			// when it does not.
			entry.Set("packables", (long)packableCount);
			entry.Set("sprites", (long)spriteCount);
			entry.Set("maxSize", (long)effectiveMax);
			entry.Set("defaultMaxSize", (long)texture.maxTextureSize);
			entry.Set("targetMaxSize", (long)effectiveMax);
			entry.Set("targetPlatform", targetPlatform);
			entry.Set("platformOverride", platformOverridden ? "Yes" : "No");
			entry.Set("targetSettings", effectiveSettingsAvailable ? "Available" : "Unavailable");
			entry.Set("readWrite", texture.readable ? "Yes" : "No");
			entry.Set("mipmaps", texture.generateMipMaps ? "Yes" : "No");
			entry.Set("filterMode", texture.filterMode.ToString());
			entry.Set("anisoLevel", (long)texture.anisoLevel);
			entry.Set("padding", (long)packing.padding);
			entry.Set("tight", packing.enableTightPacking ? "Yes" : "No");
			entry.Set("rotation", packing.enableRotation ? "Yes" : "No");
			entry.Set("compression", effectiveCompression.ToString());
			entry.Set("targetCompression", effectiveCompression.ToString());
			entry.Set("targetFormat", effectiveFormat.ToString());
			entry.Set("targetCrunch", effectiveCrunch ? "Yes" : "No");
			entry.Set("includeInBuild", includeInBuild ? "Yes" : "No");
			entry.Set("variant", isVariant ? "Yes" : "No");

			if (isVariant)
			{
				entry.Set("variantScale", variantScale > 0f ? variantScale.ToString("0.###") : "Unknown");
				entry.Set("master", masterAtlas != null ? masterAtlas.name : "Missing");
			}

			// -----------------------------------------------------------------
			// ATL005 - only a genuinely empty included master atlas.
			// A variant intentionally derives its content from the master.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL005", entry.Path))
				if (!isVariant
					&& includeInBuild
					&& packableCount < k_MinUsefulSpriteCount
					&& spriteCount == 0
					&& !IsIntentionalEmptyAtlas(atlas, assetPath))
				{
					entry.AddIssue("ATL005",
						"0 packables / 0 packed sprites; included in build",
						"AutoFix: Exclude empty atlas from build",
						fix: c =>
						{
							c.EnsureWritable();
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							if (target == null) throw new InvalidOperationException("Sprite Atlas no longer exists.");
							var currentPackables = target.GetPackables();
							if (!IsAtlasIncludedInBuild(c.AssetPath, target))
								throw new InvalidOperationException("Atlas is no longer included in the build; rescan before applying this fix.");
							if (target.isVariant || (currentPackables != null && currentPackables.Length > 0) || target.spriteCount > 0)
								throw new InvalidOperationException("Atlas is no longer empty; build inclusion was not changed.");
							if (IsIntentionalEmptyAtlas(target, c.AssetPath))
								throw new InvalidOperationException("Atlas is marked as a reserved/template placeholder; build inclusion was not changed.");
							SetAtlasIncludedInBuild(c, target, false);
						},
						verify: c =>
						{
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							return target != null && !IsAtlasIncludedInBuild(c.AssetPath, target);
						})
						?.Note(
							"Only an empty master atlas is excluded. Explicit placeholder/reserved/template markers are treated as opt-outs; the asset is never deleted.",
							"Scope");
				}

			// Empty atlases have no generated texture content to optimize. Variant atlases do have a
			// generated texture, but their content originates from the master rather than their pack list.
			bool hasGeneratedContent = spriteCount > 0 || (isVariant && masterAtlas != null);

			// PAA003 - Project Auditor's exact generated-atlas density measurement. This uses the
			// editor's internal preview texture API by reflection, so the optimizer remains compatible
			// across Unity revisions that move the method between public/internal surfaces. It is only
			// attempted for non-empty, non-variant atlases during the user's deep-analysis mode because
			// generating and sampling the packed preview can be expensive.
			using (RuleTiming.Begin("PAA003", entry.Path))
				if (hasGeneratedContent && !isVariant && ctx.Settings.deepTextureScan)
				{
					float emptyPercent = GetGeneratedAtlasEmptyPercent(atlas);
					entry.Set("emptySpace", emptyPercent >= 0f ? emptyPercent.ToString("0.##") + "%" : "Unavailable");
					if (emptyPercent > ctx.Settings.spriteAtlasEmptySpaceWarnPercent)
						entry.AddIssue("PAA003", emptyPercent.ToString("0.##") + "%", "Review atlas packing density");
				}

			// -----------------------------------------------------------------
			// ATL001 - packed texture readability is a real memory concern when enabled.
			// The exact generated atlas dimensions/format are not always exposed, so do not fabricate
			// a byte-saving estimate from Max Texture Size.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL001", entry.Path))
				if (hasGeneratedContent && texture.readable)
				{
					var issue = entry.AddIssue(
						"ATL001",
						"Read/Write enabled",
						"Review Fix: disable Read/Write",
						basis: SavingsBasis.Unknown,
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "ATL001.DisableReadWrite",
							Label = "Disable Read/Write",
							ResultDescription = "Disables CPU-readable data on the generated packed atlas.",
							Recommended = true,
							Basis = SavingsBasis.Memory,
							Warning = "Do not use this when runtime code or atlas-processing tools read pixels from the packed atlas.",
							Apply = c =>
							{
								c.EnsureWritable();
								var target = (SpriteAtlas)c.Asset;
								var settings = GetAtlasTextureSettings(c.AssetPath, target);
								settings.readable = false;
								SetAtlasTextureSettings(c, target, settings);
							},
							Verify = c =>
							{
								var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
								return target != null && !GetAtlasTextureSettings(c.AssetPath, target).readable;
							}
						}
						});

					issue?.Note(
						"Atlas texture settings apply to the generated packed texture and are independent of the source Sprite Texture importer settings.",
						"Scope");
				}

			// -----------------------------------------------------------------
			// ATL002 - effective target compression.
			// Treat either Uncompressed compression mode OR an explicit uncompressed format as uncompressed.
			// -----------------------------------------------------------------
			bool effectiveUncompressed = effectiveSettingsAvailable
				&& (effectiveCompression == TextureImporterCompression.Uncompressed
					|| (effectiveExplicitFormat && TextureFormatUtility.IsUncompressed(effectiveFormat)));

			using (RuleTiming.Begin("ATL002", entry.Path))
				if (hasGeneratedContent && effectiveUncompressed && !IsCompressionSensitiveAtlas(atlas, assetPath))
				{
					var issue = entry.AddIssue(
						"ATL002",
						targetPlatform + ": Uncompressed"
							+ (effectiveExplicitFormat ? " (" + effectiveFormat + ")" : ""),
						"AutoFix: Compressed / Automatic / Normal Quality",
						basis: SavingsBasis.Unknown,
						fix: c =>
						{
							c.EnsureWritable();
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							if (target == null) throw new InvalidOperationException("Sprite Atlas no longer exists.");
							if (IsCompressionSensitiveAtlas(target, c.AssetPath))
								throw new InvalidOperationException("Auto compression was blocked by an explicit lossless/precision marker.");
							if (target.spriteCount <= 0 && !target.isVariant)
								throw new InvalidOperationException("Sprite Atlas no longer has generated content.");
							var settings = GetAtlasPlatformSettings(c.AssetPath, target, targetPlatform);
							settings.name = targetPlatform;
							settings.overridden = true;
							settings.textureCompression = TextureImporterCompression.Compressed;
							settings.format = TextureImporterFormat.Automatic;
							settings.compressionQuality = (int)TextureCompressionQuality.Normal;
							settings.crunchedCompression = false;
SetAtlasPlatformSettings(c, target, settings);
						},
						verify: c =>
						{
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							if (target == null) return false;
							var settings = GetAtlasPlatformSettings(c.AssetPath, target, targetPlatform);
							return settings.overridden
								&& settings.textureCompression == TextureImporterCompression.Compressed
								&& settings.format == TextureImporterFormat.Automatic
								&& settings.compressionQuality == (int)TextureCompressionQuality.Normal
								&& !settings.crunchedCompression;
						});

					issue?.Note(
						platformOverridden
							? "The selected target has an explicit atlas override. AutoFix changes that override only."
							: "The selected target inherits the atlas default. AutoFix creates a target-platform override instead of changing the shared default.",
						"Scope");
				}

			// -----------------------------------------------------------------
			// ATL003 - only masters can repack their own packable content. A variant's size is derived
			// from the master texture and variant scale, so changing the variant max-size setting is not
			// a safe representation of a standalone packing budget.
			// -----------------------------------------------------------------
			int budget = Math.Max(0, ctx.Settings.maxTextureSize);
			using (RuleTiming.Begin("ATL003", entry.Path))
				if (!isVariant && hasGeneratedContent && budget > 0 && effectiveMax > budget)
				{
					int target = ClampAtlasSize(budget);
					if (target < effectiveMax)
					{
						var issue = entry.AddIssue(
							"ATL003",
							effectiveMax + " for " + targetPlatform,
							"Review Fix: Max Texture Size " + target,
							basis: SavingsBasis.Memory,
							options: new List<FixOption>
							{
							BuildMaxSizeOption(targetPlatform, target)
							});

						issue?.Note(
							platformOverridden
								? "Only the selected platform's Sprite Atlas override will be changed."
								: "The selected platform inherits the atlas default; the Review Fix creates a target-platform override instead of changing the shared default.",
							"Scope");
						issue?.Note(
							"Max Texture Size is an upper bound, not the generated page size. Review the resulting packed atlas before accepting the reduction.",
							"Why review");
					}
				}

			// -----------------------------------------------------------------
			// ATL004 - packing settings only matter for masters that actually repack packable sprites.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL004", entry.Path))
				if (!isVariant && packableCount > 0 && hasGeneratedContent && !packing.enableTightPacking)
				{
					var issue = entry.AddIssue(
						"ATL004",
						"Tight Packing off; Rotation " + (packing.enableRotation ? "on" : "off"),
						"AutoFix: Enable Tight Packing (leave Rotation unchanged)",
						basis: SavingsBasis.Unknown,
						fix: c =>
						{
							c.EnsureWritable();
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							if (target == null) throw new InvalidOperationException("Sprite Atlas no longer exists.");
							var currentPackables = target.GetPackables();
							if (target.isVariant || currentPackables == null || currentPackables.Length == 0 || target.spriteCount <= 0)
								throw new InvalidOperationException("Tight Packing AutoFix requires a populated master atlas.");
							var settings = GetAtlasPackingSettings(c.AssetPath, target);
							settings.enableTightPacking = true;
							SetAtlasPackingSettings(c, target, settings);
						},
						verify: c =>
						{
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							return target != null && GetAtlasPackingSettings(c.AssetPath, target).enableTightPacking;
						});

					issue?.Note(
						"AutoFix enables Tight Packing only. It deliberately does not enable rotation, including for atlases used by Canvas UI.",
						"Scope");
				}

			// -----------------------------------------------------------------
			// ATL006 - mipmaps are atlas-global settings, not target-platform texture compression settings.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL006", entry.Path))
				if (hasGeneratedContent && texture.generateMipMaps)
				{
					var issue = entry.AddIssue(
						"ATL006",
						"Mipmaps enabled",
						"Review Fix: disable mipmaps",
						basis: SavingsBasis.Unknown,
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "ATL006.DisableMipmaps",
							Label = "Disable Mipmaps",
							ResultDescription = "Stops generating the mip chain for the packed atlas texture.",
							Recommended = false,
							Basis = SavingsBasis.Memory,
							Warning = "Only appropriate when the atlas does not need mipmapping for world-space scaling or minification quality.",
							Apply = c =>
							{
								c.EnsureWritable();
								var target = (SpriteAtlas)c.Asset;
								var settings = GetAtlasTextureSettings(c.AssetPath, target);
								settings.generateMipMaps = false;
								SetAtlasTextureSettings(c, target, settings);
							},
							Verify = c =>
							{
								var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
								return target != null && !GetAtlasTextureSettings(c.AssetPath, target).generateMipMaps;
							}
						}
						});

					issue?.Note(
						"This is a Review finding: mipmaps can be necessary for world-space sprites or other minification-sensitive uses.",
						"Why review");
				}

			// -----------------------------------------------------------------
			// ATL007 - auto-reduce only to 8 px and only when mipmaps/explicit bleed-sensitive markers do
			// not identify a known exception. Lower values are never selected automatically.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL007", entry.Path))
				if (!isVariant && hasGeneratedContent && packing.padding > k_HighPaddingPixels
					&& !texture.generateMipMaps && !IsBleedSensitiveAtlas(atlas, assetPath))
				{
					var issue = entry.AddIssue(
						"ATL007",
						packing.padding + " px",
						"AutoFix: Set padding to 8 px",
						basis: SavingsBasis.Unknown,
						fix: c =>
						{
							c.EnsureWritable();
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							if (target == null) throw new InvalidOperationException("Sprite Atlas no longer exists.");
							if (target.isVariant || GetAtlasTextureSettings(c.AssetPath, target).generateMipMaps || IsBleedSensitiveAtlas(target, c.AssetPath))
								throw new InvalidOperationException("Padding AutoFix was blocked because the atlas is mipmapped or marked bleed-sensitive.");
							var currentPackables = target.GetPackables();
							if (currentPackables == null || currentPackables.Length == 0 || target.spriteCount <= 0)
								throw new InvalidOperationException("Padding AutoFix requires a populated atlas.");
							var settings = GetAtlasPackingSettings(c.AssetPath, target);
							if (settings.padding > k_HighPaddingPixels) settings.padding = k_HighPaddingPixels;
							SetAtlasPackingSettings(c, target, settings);
						},
						verify: c =>
						{
							var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
							return target != null && GetAtlasPackingSettings(c.AssetPath, target).padding <= k_HighPaddingPixels;
						});
					issue?.Note(
						"AutoFix selects 8 px only for non-mipmapped atlases without explicit bleed-sensitive markers. Lower padding values are never selected automatically.",
						"Safety gate");
				}

			entry.Finalise();
			return entry;
		}

		static FixOption BuildMaxSizeOption(string platform, int target)
		{
			return new FixOption
			{
				ActionId = "ATL003.MaxSize." + target,
				Label = "Set Max Texture Size to " + target,
				ResultDescription = "Limits this target platform's generated atlas to " + target + " pixels.",
				Recommended = true,
				EstimatedSavings = 0,
				Basis = SavingsBasis.Memory,
				Warning = "This can lower the resolution of every sprite packed into the atlas. It creates/updates only the selected platform override.",
				Apply = c =>
				{
					c.EnsureWritable();
					var targetAtlas = (SpriteAtlas)c.Asset;
					var settings = GetAtlasPlatformSettings(c.AssetPath, targetAtlas, platform);
					settings.name = platform;
					settings.overridden = true;
					settings.maxTextureSize = target;
					SetAtlasPlatformSettings(c, targetAtlas, settings);
				},
				Verify = c =>
				{
					var targetAtlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
					if (targetAtlas == null) return false;
					var settings = GetAtlasPlatformSettings(c.AssetPath, targetAtlas, platform);
					return settings.overridden && settings.maxTextureSize <= target;
				}
			};
		}

		// Explicitly marked cases are excluded from the common-case AutoFix path. Names/labels are
		// opt-out signals; absence of a marker is not treated as proof of visual quality.
		static bool HasAtlasMarker(SpriteAtlas atlas, string assetPath, params string[] markers)
		{
			string name = System.IO.Path.GetFileNameWithoutExtension(assetPath) ?? string.Empty;
			for (int i = 0; i < markers.Length; i++)
				if (name.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;

			try
			{
				string[] labels = AssetDatabase.GetLabels(atlas);
				if (labels != null)
					for (int i = 0; i < labels.Length; i++)
						for (int j = 0; j < markers.Length; j++)
							if (labels[i].IndexOf(markers[j], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			catch { }
			return false;
		}

		static bool IsCompressionSensitiveAtlas(SpriteAtlas atlas, string assetPath) =>
			HasAtlasMarker(atlas, assetPath, "lossless", "uncompressed", "precision", "compression-sensitive", "compressionsensitive", "keep-compression", "keepcompression");

		static bool IsBleedSensitiveAtlas(SpriteAtlas atlas, string assetPath) =>
			HasAtlasMarker(atlas, assetPath, "bleed-sensitive", "bleedsensitive", "keep-padding", "keeppadding", "preserve-padding", "preservepadding", "pixel-bleed", "pixelbleed", "no-auto-padding", "noautopadding");

		static bool IsIntentionalEmptyAtlas(SpriteAtlas atlas, string assetPath) =>
			HasAtlasMarker(atlas, assetPath, "placeholder", "reserved", "template", "keep-empty", "keepempty", "future-atlas", "futureatlas");

		static void SetAtlasIncludedInBuild(FixContext context, SpriteAtlas atlas, bool included)
		{
			// SpriteAtlasImporter is the non-legacy path used by Sprite Atlas V2. Its import settings
			// must be saved/reimported through the FixEngine transaction, not only dirtied as an object.
			var importer = context.Importer as SpriteAtlasImporter;
			if (importer != null)
			{
				importer.includeInBuild = included;
				context.NeedsReimport = true;
				return;
			}

			SpriteAtlasExtensions.SetIncludeInBuild(atlas, included);
			context.NeedsSave = true;
		}

		static bool IsAtlasIncludedInBuild(string assetPath, SpriteAtlas atlas)
		{
			var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
			return importer != null ? importer.includeInBuild : SpriteAtlasExtensions.IsIncludeInBuild(atlas);
		}

		static float GetGeneratedAtlasEmptyPercent(SpriteAtlas atlas)
		{
			if (atlas == null) return -1f;

			try
			{
				var method = typeof(SpriteAtlasExtensions).GetMethod(
					"GetPreviewTextures",
					BindingFlags.Static | BindingFlags.NonPublic);

				if (method == null) return -1f;

				var result = method.Invoke(null, new object[] { atlas }) as Texture2D[];
				if (result == null || result.Length == 0 || result[0] == null) return -1f;

				var texture = result[0];
				if (texture.width <= 0 || texture.height <= 0) return 0f;

				Color32[] pixels = null;
				if (texture.isReadable)
				{
					pixels = texture.GetPixels32();
				}
				else
				{
					var previous = RenderTexture.active;
					var temp = RenderTexture.GetTemporary(
						texture.width,
						texture.height,
						0,
						RenderTextureFormat.Default,
						RenderTextureReadWrite.Linear);
					try
					{
						Graphics.Blit(texture, temp);
						RenderTexture.active = temp;
						var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
						copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
						copy.Apply(false, false);
						pixels = copy.GetPixels32();
						UnityEngine.Object.DestroyImmediate(copy);
					}
					finally
					{
						RenderTexture.active = previous;
						RenderTexture.ReleaseTemporary(temp);
					}
				}

				if (pixels == null || pixels.Length == 0) return -1f;

				int empty = 0;
				for (int i = 0; i < pixels.Length; ++i)
				{
					if (pixels[i].a == 0) ++empty;
				}

				return Mathf.Round((float)empty / pixels.Length * 100f);
			}
			catch (TargetInvocationException)
			{
				return -1f;
			}
			catch (Exception)
			{
				return -1f;
			}
		}

		static int ClampAtlasSize(int value)
		{
			if (value <= 0) return value;
			int size = 1;
			while (size < value && size < 16384) size <<= 1;
			if (size > value) size >>= 1;
			return Mathf.Clamp(size, 32, 16384);
		}

		static float GetVariantScale(string assetPath)
		{
			try
			{
				var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
				return importer != null ? importer.variantScale : 0f;
			}
			catch
			{
				return 0f;
			}
		}

		static bool TryGetPlatformSettings(string assetPath, SpriteAtlas atlas, string platform, out TextureImporterPlatformSettings settings)
		{
			settings = default(TextureImporterPlatformSettings);
			if (atlas == null || string.IsNullOrEmpty(platform)) return false;

			try
			{
				var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
				settings = importer != null ? importer.GetPlatformSettings(platform) : atlas.GetPlatformSettings(platform);
				settings.name = string.IsNullOrEmpty(settings.name) ? platform : settings.name;
				return true;
			}
			catch
			{
				return false;
			}
		}

		static SpriteAtlasTextureSettings GetAtlasTextureSettings(string assetPath, SpriteAtlas atlas)
		{
			var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
			return importer != null ? importer.textureSettings : atlas.GetTextureSettings();
		}

		static void SetAtlasTextureSettings(FixContext context, SpriteAtlas atlas, SpriteAtlasTextureSettings settings)
		{
			var importer = context.Importer as SpriteAtlasImporter;
			if (importer != null)
			{
				importer.textureSettings = settings;
				context.NeedsReimport = true;
			}
			else
			{
				atlas.SetTextureSettings(settings);
				context.NeedsSave = true;
			}
		}

		static SpriteAtlasPackingSettings GetAtlasPackingSettings(string assetPath, SpriteAtlas atlas)
		{
			var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
			return importer != null ? importer.packingSettings : atlas.GetPackingSettings();
		}

		static void SetAtlasPackingSettings(FixContext context, SpriteAtlas atlas, SpriteAtlasPackingSettings settings)
		{
			var importer = context.Importer as SpriteAtlasImporter;
			if (importer != null)
			{
				importer.packingSettings = settings;
				context.NeedsReimport = true;
			}
			else
			{
				atlas.SetPackingSettings(settings);
				context.NeedsSave = true;
			}
		}

		static TextureImporterPlatformSettings GetAtlasPlatformSettings(string assetPath, SpriteAtlas atlas, string platform)
		{
			var importer = AssetImporter.GetAtPath(assetPath) as SpriteAtlasImporter;
			return importer != null ? importer.GetPlatformSettings(platform) : atlas.GetPlatformSettings(platform);
		}

		static void SetAtlasPlatformSettings(FixContext context, SpriteAtlas atlas, TextureImporterPlatformSettings settings)
		{
			var importer = context.Importer as SpriteAtlasImporter;
			if (importer != null)
			{
				importer.SetPlatformSettings(settings);
				context.NeedsReimport = true;
			}
			else
			{
				atlas.SetPlatformSettings(settings);
				context.NeedsSave = true;
			}
		}
		static long EstimateMipOverhead(int width, int height, int mipCount, long runtimeBytes)
		{
			if (runtimeBytes <= 0 || mipCount <= 1) return 0;
			long total = 0, basePixels = (long)Math.Max(1, width) * Math.Max(1, height); int w = Math.Max(1, width), h = Math.Max(1, height);
			for (int m = 0; m < mipCount; m++) { total += (long)w * h; w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); }
			return total > basePixels ? (long)(runtimeBytes * ((double)(total - basePixels) / total)) : 0;
		}

	}
}
