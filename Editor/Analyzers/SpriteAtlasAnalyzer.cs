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
		const int k_DefaultPaddingPixels = 4;
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
							fix: "Review a compressed Automatic setting for the selected target. The fix creates or updates only that target's atlas override and does not force a platform-specific format.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
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
							fix: "Review Fix: enable the missing packing option(s) only when the atlas contents and runtime workflow support them.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "ATL005", title: "Atlas is included in the build but has no packables", category: AssetCategory.SpriteAtlas,
							why: "An included master Sprite Atlas with no packable objects and no packed sprites contributes no useful atlas content. Empty or intentionally reserved atlases can still exist during development.",
							fix: "Add the intended sprites/folders, or manually exclude/remove the atlas when it is genuinely obsolete.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
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

			Rules.Register(id: "PAA0008", title: "Sprite Atlas has too much empty space", category: AssetCategory.SpriteAtlas,
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
							fix: "Review Fix: choose the smallest padding that remains safe for the atlas's filtering, mipmaps and sprite-edge requirements.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
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

			var texture = atlas.GetTextureSettings();
			var packing = atlas.GetPackingSettings();
			var packables = atlas.GetPackables();
			int packableCount = packables != null ? packables.Length : 0;
			int spriteCount = Math.Max(0, atlas.spriteCount);
			bool includeInBuild = SpriteAtlasExtensions.IsIncludeInBuild(atlas);
			bool isVariant = atlas.isVariant;
			float variantScale = isVariant ? GetVariantScale(assetPath) : 0f;
			SpriteAtlas masterAtlas = isVariant ? SpriteAtlasExtensions.GetMasterAtlas(atlas) : null;

			string targetPlatform = PlatformUtility.GetAnalyzerTargetPlatform(ctx, k_DefaultTexturePlatform);
			TextureImporterPlatformSettings targetSettings;
			TextureImporterPlatformSettings defaultSettings;
			bool targetSettingsAvailable = TryGetPlatformSettings(atlas, targetPlatform, out targetSettings);
			bool defaultSettingsAvailable = TryGetPlatformSettings(atlas, k_DefaultTexturePlatform, out defaultSettings);

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
					&& spriteCount == 0)
				{
					entry.AddIssue("ATL005",
						"0 packables / 0 packed sprites",
						"Add content or exclude the atlas")
						?.Note(
							"This master atlas has no packable objects and no generated sprites. The analyzer does not delete it because asset/addressing intent cannot be proven automatically.",
							"Why no fix");
				}

			// Empty atlases have no generated texture content to optimize. Variant atlases do have a
			// generated texture, but their content originates from the master rather than their pack list.
			bool hasGeneratedContent = spriteCount > 0 || (isVariant && masterAtlas != null);

			// PAA0008 - Project Auditor's exact generated-atlas density measurement. This uses the
			// editor's internal preview texture API by reflection, so the optimizer remains compatible
			// across Unity revisions that move the method between public/internal surfaces. It is only
			// attempted for non-empty, non-variant atlases during the user's deep-analysis mode because
			// generating and sampling the packed preview can be expensive.
			using (RuleTiming.Begin("PAA0008", entry.Path))
				if (hasGeneratedContent && !isVariant && ctx.Settings.deepTextureScan)
				{
					float emptyPercent = GetGeneratedAtlasEmptyPercent(atlas);
					entry.Set("emptySpace", emptyPercent >= 0f ? emptyPercent.ToString("0.##") + "%" : "Unavailable");
					if (emptyPercent > ctx.Settings.spriteAtlasEmptySpaceWarnPercent)
						entry.AddIssue("PAA0008", emptyPercent.ToString("0.##") + "%", "Review atlas packing density");
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
								var settings = target.GetTextureSettings();
								settings.readable = false;
								target.SetTextureSettings(settings);
								c.NeedsSave = true;
							},
							Verify = c =>
							{
								var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
								return target != null && !target.GetTextureSettings().readable;
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
				if (hasGeneratedContent && effectiveUncompressed)
				{
					var issue = entry.AddIssue(
						"ATL002",
						targetPlatform + ": Uncompressed"
							+ (effectiveExplicitFormat ? " (" + effectiveFormat + ")" : ""),
						"Review compressed Automatic format",
						basis: SavingsBasis.Memory,
						options: BuildCompressionOptions(targetPlatform));

					issue?.Note(
						platformOverridden
							? "The selected target has an explicit atlas override. Review actions modify that override only."
							: "The selected target inherits the atlas default. Review actions create a target-platform override instead of changing the shared default.",
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
				if (!isVariant && hasGeneratedContent && (!packing.enableTightPacking || !packing.enableRotation))
				{
					var options = BuildPackingOptions(packing.enableTightPacking, packing.enableRotation);
					var issue = entry.AddIssue(
						"ATL004",
						BuildPackingCurrentText(packing.enableTightPacking, packing.enableRotation),
						"Review missing packing option(s)",
						basis: SavingsBasis.Unknown,
						options: options);

					issue?.Note(
						"Tight Packing can improve density. Rotation can improve density too, but should remain disabled for Canvas UI workflows that cannot tolerate rotated packed textures.",
						"Trade-off");
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
								var settings = target.GetTextureSettings();
								settings.generateMipMaps = false;
								target.SetTextureSettings(settings);
								c.NeedsSave = true;
							},
							Verify = c =>
							{
								var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
								return target != null && !target.GetTextureSettings().generateMipMaps;
							}
						}
						});

					issue?.Note(
						"This is a Review finding: mipmaps can be necessary for world-space sprites or other minification-sensitive uses.",
						"Why review");
				}

			// -----------------------------------------------------------------
			// ATL007 - large padding is heuristic. Only report when the atlas contains content and give
			// explicit smaller choices without assuming that 4 is universally safe.
			// -----------------------------------------------------------------
			using (RuleTiming.Begin("ATL007", entry.Path))
				if (!isVariant && hasGeneratedContent && packing.padding > k_HighPaddingPixels)
				{
					var options = BuildPaddingOptions(packing.padding);
					var issue = entry.AddIssue(
						"ATL007",
						packing.padding + " px",
						"Review smaller padding",
						basis: SavingsBasis.Unknown,
						options: options);
					issue?.Note(
						"Padding is a bleed-protection parameter. The minimum safe value depends on sprite edges, filtering, mipmaps and the actual packed content; the tool therefore does not mark a smaller value as universally correct.",
						"Trade-off");
				}

			entry.Finalise();
			return entry;
		}

		static List<FixOption> BuildCompressionOptions(string platform)
		{
			return new List<FixOption>
			{
				AtlasCompressionOption(
					platform,
					TextureImporterCompression.Compressed,
					"Normal Quality / Automatic",
					true,
					"Use Automatic platform-specific compression at Normal quality."),

				AtlasCompressionOption(
					platform,
					TextureImporterCompression.CompressedLQ,
					"Low Quality / Automatic",
					false,
					"Use Automatic platform-specific compression at Low quality."),

				AtlasCompressionOption(
					platform,
					TextureImporterCompression.CompressedHQ,
					"High Quality / Automatic",
					false,
					"Use Automatic platform-specific compression at High quality when visual quality requires it.")
			};
		}

		static FixOption AtlasCompressionOption(
			string platform,
			TextureImporterCompression compression,
			string label,
			bool recommended,
			string description)
		{
			return new FixOption
			{
				ActionId = "ATL002." + compression,
				Label = label,
				ResultDescription = description,
				Recommended = recommended,
				Basis = SavingsBasis.Unknown,
				Warning = "Automatic selects a platform-specific GPU format. Verify the resulting atlas on the actual target device class and check alpha/quality requirements.",
				Apply = c =>
				{
					c.EnsureWritable();
					var target = (SpriteAtlas)c.Asset;
					var settings = target.GetPlatformSettings(platform);
					settings.name = platform;
					settings.overridden = true;
					settings.textureCompression = compression;
					settings.format = TextureImporterFormat.Automatic;
					settings.crunchedCompression = false;
					target.SetPlatformSettings(settings);
					c.NeedsSave = true;
				},
				Verify = c =>
				{
					var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
					if (target == null) return false;
					var settings = target.GetPlatformSettings(platform);
					return settings.overridden
						&& settings.textureCompression == compression
						&& settings.format == TextureImporterFormat.Automatic
						&& !settings.crunchedCompression;
				}
			};
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
					var settings = targetAtlas.GetPlatformSettings(platform);
					settings.name = platform;
					settings.overridden = true;
					settings.maxTextureSize = target;
					targetAtlas.SetPlatformSettings(settings);
					c.NeedsSave = true;
				},
				Verify = c =>
				{
					var targetAtlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
					if (targetAtlas == null) return false;
					var settings = targetAtlas.GetPlatformSettings(platform);
					return settings.overridden && settings.maxTextureSize <= target;
				}
			};
		}

		static List<FixOption> BuildPackingOptions(bool tight, bool rotation)
		{
			var options = new List<FixOption>();
			if (!tight)
			{
				options.Add(new FixOption
				{
					ActionId = "ATL004.EnableTightPacking",
					Label = "Enable Tight Packing",
					ResultDescription = "Allow the atlas packer to use sprite outlines to improve density.",
					Recommended = true,
					Warning = "Review sprites with unusual geometry or workflows that depend on rectangular packing assumptions.",
					Apply = c => SetPacking(c, true, null),
					Verify = c => GetPacking(c).enableTightPacking
				});
			}

			if (!rotation)
			{
				options.Add(new FixOption
				{
					ActionId = "ATL004.EnableRotation",
					Label = "Enable Rotation",
					ResultDescription = "Allow Unity to rotate sprites during atlas packing to improve density.",
					Recommended = false,
					Warning = "Do not use this for Canvas UI element textures where rotation can change their scene orientation.",
					Apply = c => SetPacking(c, null, true),
					Verify = c => GetPacking(c).enableRotation
				});
			}

			if (!tight && !rotation)
			{
				options.Add(new FixOption
				{
					ActionId = "ATL004.EnableTightAndRotation",
					Label = "Enable Tight Packing + Rotation",
					ResultDescription = "Enable both packing-density features.",
					Recommended = false,
					Warning = "Rotation is unsuitable for Canvas UI element textures. Review atlas contents before accepting this option.",
					Apply = c => SetPacking(c, true, true),
					Verify = c =>
					{
						var p = GetPacking(c);
						return p.enableTightPacking && p.enableRotation;
					}
				});
			}
			return options;
		}

		static void SetPacking(FixContext c, bool? tight, bool? rotation)
		{
			c.EnsureWritable();
			var target = (SpriteAtlas)c.Asset;
			var settings = target.GetPackingSettings();
			if (tight.HasValue) settings.enableTightPacking = tight.Value;
			if (rotation.HasValue) settings.enableRotation = rotation.Value;
			target.SetPackingSettings(settings);
			c.NeedsSave = true;
		}

		static SpriteAtlasPackingSettings GetPacking(FixContext c)
		{
			var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
			if (target == null) throw new InvalidOperationException("Sprite Atlas no longer exists.");
			return target.GetPackingSettings();
		}

		static List<FixOption> BuildPaddingOptions(int current)
		{
			var options = new List<FixOption>();
			if (current > k_DefaultPaddingPixels)
				options.Add(PaddingOption(k_DefaultPaddingPixels, false));
			if (current > k_HighPaddingPixels)
				options.Add(PaddingOption(k_HighPaddingPixels, true));
			return options;
		}

		static FixOption PaddingOption(int padding, bool recommended)
		{
			return new FixOption
			{
				ActionId = "ATL007.Padding." + padding,
				Label = padding + " px padding",
				ResultDescription = "Set atlas packing padding to " + padding + " pixels.",
				Recommended = recommended,
				Warning = "Confirm the selected padding remains sufficient for sprite-edge bleeding with this atlas's filtering and mipmap settings.",
				Apply = c =>
				{
					c.EnsureWritable();
					var target = (SpriteAtlas)c.Asset;
					var settings = target.GetPackingSettings();
					settings.padding = padding;
					target.SetPackingSettings(settings);
					c.NeedsSave = true;
				},
				Verify = c =>
				{
					var target = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(c.AssetPath);
					return target != null && target.GetPackingSettings().padding == padding;
				}
			};
		}

		static string BuildPackingCurrentText(bool tight, bool rotation)
		{
			if (!tight && !rotation) return "Tight Packing off; Rotation off";
			if (!tight) return "Tight Packing off; Rotation on";
			return "Tight Packing on; Rotation off";
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

		static bool TryGetPlatformSettings(SpriteAtlas atlas, string platform, out TextureImporterPlatformSettings settings)
		{
			settings = default(TextureImporterPlatformSettings);
			if (atlas == null || string.IsNullOrEmpty(platform)) return false;

			try
			{
				settings = atlas.GetPlatformSettings(platform);
				settings.name = string.IsNullOrEmpty(settings.name) ? platform : settings.name;
				return true;
			}
			catch
			{
				return false;
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
