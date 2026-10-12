using System.Reflection;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.U2D;
using UnityEditor.U2D;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Import-settings and memory analysis for every Texture asset in the project.</summary>
	public sealed class TextureAnalyzer : IBackgroundAssetAnalyzer
	{
		static readonly string[] k_Extensions =
		{
			".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif",
			".exr", ".hdr", ".iff", ".pict", ".pic", ".pct", ".webp"
		};

		static readonly string[] k_NormalHints =
		{
			"_normal", "_norm", "_nrm", "_nor", "normalmap", "_bump", "_ddn"
		};
		static readonly string[] k_DataHints =
		{
			"_mask", "_masks", "_metal", "_metallic", "_rough", "_roughness", "_smooth", "_smoothness",
			"_ao", "_occl", "_occlusion", "_height", "_disp", "_displacement", "_gloss",
			"_spec", "_specular", "_orm", "_data", "_lut", "_flow", "_noise"
		};

		HashSet<string> m_AtlasCovered;
		List<string> m_AtlasFolders;
		bool m_AnyAtlases;
		static HashSet<string> s_AtlasCovered;
		static List<string> s_AtlasFolders;
		static bool s_AnyAtlases;
		static bool s_AtlasCoverageDirty = true;

		public AssetCategory Category => AssetCategory.Texture;

		public AssetKindMask SupportedKinds => AssetKindMask.Texture;

		/// <summary>
		/// The part of texture analysis that needs the imported texture in memory. Cached against
		/// the asset's dependency hash, so a rescan reads these numbers instead of loading
		/// thousands of textures again.
		/// </summary>
		[Serializable]
		public sealed class TextureFacts
		{
			public int width;
			public int height;
			public string format = "-";
			public long runtimeSize = -1;
			public long readableDataSize;
			public long mipSavingsBytes;
			public long globalMipLimitSavingsBytes;
			public bool sourceHasAlpha;
			public bool sourceHasAlphaKnown = true;
			public bool greyscale;
			public bool solidColor;
			public float emptyPixelsPercent = -1f;

			public TextureImporterType textureType;
			public int maxTextureSize;
			public bool mipmapEnabled;
			public bool isReadable;
			public TextureImporterCompression textureCompression;
			public bool crunchedCompression;
			public bool streamingMipmaps;
			public TextureImporterShape textureShape;
			public TextureImporterAlphaSource alphaSource;
			public int anisoLevel;
			public FilterMode filterMode;
			public bool sRGBTexture;
			public bool ignoreMipmapLimit;
			public string targetPlatform = string.Empty;
			public bool targetOverridden;
			public int targetMaxTextureSize;
			public TextureImporterCompression targetCompression;
			public TextureImporterFormat targetFormat;
			public bool targetCrunch;
			public int targetCompressionQuality;
			public string targetFormatDescription = "-";
			public bool crunchCompatible;
			public bool highQualityMemoryConcern;
			public bool streamingMipmapsActive;
			public int globalTextureMipmapLimit;
		}

		static TextureAnalyzer()
		{
			try { EditorApplication.projectChanged += InvalidateAtlasCoverage; } catch { }
			Rules.Register(id: "TEX001", title: "Texture larger than the size budget for the target platform", category: AssetCategory.Texture,
							why: "Imported texture resolution directly affects GPU memory and bandwidth. The budget is a policy threshold for the selected optimizer platform profile, not proof that this particular asset is visually safe to reduce.",
							fix: "Review the target-platform Max Size before changing it. The action is platform-scoped and never changes the shared Default Max Size.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX002", title: "Texture imports uncompressed for the target platform", category: AssetCategory.Texture,
							why: "An uncompressed target texture can consume substantially more GPU memory and bandwidth than a compressed format. The correct compression mode depends on platform support, alpha, HDR/data usage and visual quality requirements.",
							fix: "Review Normal or Low Quality automatic compression for the selected target platform.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX003", title: "Crunch compression / quality differs from the project policy", category: AssetCategory.Texture,
							why: "Crunch is an additional disk/download compression stage for supported compressed texture formats. It does not reduce the GPU memory footprint and increases import/compression time, so this is a build-size policy with a configurable compression-quality target.",
							fix: "When the target is proven Crunch-compatible and the project preference requests Crunch, apply the configured Crunch / compression quality.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
								recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX004", title: "Read/Write enabled", category: AssetCategory.Texture,
							why: "Readable textures keep an additional CPU-addressable copy. Unity requires readability for CPU pixel access such as GetPixels/SetPixels and similar APIs.",
							fix: "Review Fix: disable Read/Write only when no runtime or editor-generated content needs CPU pixel access.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX005", title: "Mip maps generated for a 2D/UI texture", category: AssetCategory.Texture,
							why: "Mipmaps add imported texture data and are often unnecessary for fixed-size UI imagery. Sprite textures can also be rendered in world space or at varying scales, so the importer state alone cannot prove that mipmaps are wasteful.",
							fix: "Review disabling Generate Mip Maps for assets that are truly fixed-size/UI-only.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX006", title: "Mip maps missing on a world-space texture", category: AssetCategory.Texture,
							why: "Mipmaps improve minification quality and can reduce aliasing and texture-cache pressure on surfaces viewed at varying distances. They are not automatically required for every texture.",
							fix: "Review enabling Generate Mip Maps for textures used at varying projected sizes.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX007", title: "Alpha source configured when the source has no alpha", category: AssetCategory.Texture,
							why: "The importer is configured to obtain alpha even though the source image reports no alpha channel. Alpha can still be intentionally generated from another source, so the source format alone does not prove that disabling the setting is safe.",
							fix: "Review setting Alpha Source to None when no generated alpha is required.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX008", title: "Texture uses a filename normal-map hint but is imported as Default", category: AssetCategory.Texture,
							why: "Filename conventions can identify likely normal maps, but naming alone cannot prove that the pixels are normal-map data. Changing Texture Type changes import processing and can alter the texture result.",
							fix: "Review importing as Normal Map only after confirming the content is actually a normal map.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX009", title: "Mipmap streaming disabled on a large texture", category: AssetCategory.Texture,
							why: "Per-texture mipmap streaming allows Unity to load only the mip levels needed for the current cameras. The project quality level must also have mipmap streaming enabled, so this finding is advisory.",
							fix: "Review enabling Streaming Mipmaps for large non-UI textures in a project that actually uses mip streaming.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);

			Rules.Register(id: "TEX010", title: "Anisotropic filtering level is high", category: AssetCategory.Texture,
							why: "Higher anisotropic levels can improve texture clarity at grazing angles but increase sampling work. A high value is not inherently wrong and is often appropriate for roads, floors and terrain.",
							fix: "Review against the actual viewing angle and quality target rather than enforcing a universal maximum.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX011", title: "Mobile target override uses an uncompressed format", category: AssetCategory.Texture,
							why: "A mobile target override that explicitly selects an uncompressed format can consume substantially more memory and bandwidth than the platform's supported compressed formats. The correct format depends on device compatibility and the texture's content.",
							fix: "Review switching the mobile override to Automatic compression with Normal or Low Quality.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX012", title: "Very large source file", category: AssetCategory.Texture,
							why: "Large source images increase repository/source-art storage and can increase import work. Source size is separate from the platform-transcoded texture shipped to players.",
							fix: "Downscale or re-author the source only when the original source resolution is genuinely unnecessary.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX013", title: "sRGB enabled on a likely data texture", category: AssetCategory.Texture,
							why: "Masks, roughness, metallic, height and other numeric data textures generally need linear sampling. A filename heuristic cannot prove intent, so this remains review-only.",
							fix: "Review disabling sRGB (Color Texture) when the texture is confirmed to contain linear data.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX014", title: "Colour texture contains only greyscale data", category: AssetCategory.Texture,
							why: "If RGB channels contain identical information, storing three channels can be wasteful. Repacking into a single-channel texture or combining masks can reduce storage, but that requires matching shader/material changes.",
							fix: "Review repacking or channel-packing with the material/shader that consumes the texture.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX015", title: "Filtering settings are inefficient for a texture without mip maps", category: AssetCategory.Texture,
							why: "Trilinear filtering depends on mip levels, while anisotropic filtering is primarily useful with mipmapped textures. The importer settings can therefore be inconsistent for textures intentionally imported without mipmaps.",
							fix: "Review switching to Bilinear and Aniso 1 when no mip chain is intentionally required.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX016", title: "Sprite is not packed into a Sprite Atlas", category: AssetCategory.Texture,
							why: "Sprite Atlas packing can reduce texture switches and improve batching, but runtime-generated sprites, special addressing, atlas filtering or UI workflows may intentionally keep sprites separate.",
							fix: "Review Sprite Atlas membership based on actual runtime usage and atlas strategy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "TEX017", title: "Texture ignores the global mipmap limit", category: AssetCategory.Texture,
							why: "A texture with Ignore Mipmap Limit enabled opts out of the active global mipmap-limit policy when mipmaps are supported. This can increase resident texture memory when the project intentionally lowers global mip levels.",
							fix: "Review disabling Ignore Mipmap Limit unless this texture genuinely requires full-resolution mip access.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PAA0005", title: "Solid-color texture is larger than 1x1", category: AssetCategory.Texture,
							why: "A texture that contains one solid color can be represented by a 1x1 texture, eliminating needless pixels and mip levels. This is a deep-analysis heuristic because decoding every texture is expensive.",
							fix: "Replace a truly solid-color texture with a 1x1 texture or a material/property value. Project Optimizer only reports this when it can prove the sampled pixels are identical.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAA0007", title: "Texture Atlas has too much empty space", category: AssetCategory.Texture,
							why: "Project Auditor measures transparent pixels in Texture2D content and reports assets with excessive empty area because empty pixels still consume texture memory and storage.",
							fix: "Re-author or crop the texture, or reorganize the atlas/source layout to reduce unused transparent area. Project Optimizer does not rewrite texture pixels automatically.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "TEX018", title: "High-quality texture compression on a large texture", category: AssetCategory.Texture,
							why: "High Quality compression can use more memory than Normal Quality for the same texture dimensions. Higher quality is legitimate when the visual benefit matters, so the correct action is a visual-quality decision.",
							fix: "Review Normal or Low Quality for large textures when the visual difference is acceptable.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
		}

		static void InvalidateAtlasCoverage()
		{
			s_AtlasCoverageDirty = true;
		}

		public void BeginScan(ScanContext ctx)
		{
			if (!s_AtlasCoverageDirty && s_AtlasCovered != null)
			{
				m_AtlasCovered = s_AtlasCovered;
				m_AtlasFolders = s_AtlasFolders;
				m_AnyAtlases = s_AnyAtlases;
				return;
			}

			m_AtlasCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			m_AtlasFolders = new List<string>();
			m_AnyAtlases = false;

			var visitedAtlases = new HashSet<int>();
			foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
			{
				var path = AssetDatabase.GUIDToAssetPath(guid);
				var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
				if (atlas == null || !SpriteAtlasExtensions.IsIncludeInBuild(atlas)) continue;

				m_AnyAtlases = true;
				RegisterAtlasPackables(atlas, visitedAtlases, requireIncludedInBuild: true);
			}
			s_AtlasCovered = m_AtlasCovered;
			s_AtlasFolders = m_AtlasFolders;
			s_AnyAtlases = m_AnyAtlases;
			s_AtlasCoverageDirty = false;
		}

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
			string platform = settings.PlatformString;
			string dependencyHash = backgroundContext.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, platform ?? string.Empty);
			TextureFacts facts = null;
			bool measuredForBackground = false;
			if (mainContext.Cache != null) mainContext.Cache.TryGet(cacheKey, dependencyHash, out facts);
			if (facts == null)
			{
				measuredForBackground = true;
				var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
				if (importer == null) return;
				facts = MeasureTexture(assetPath, importer, settings);
				if (facts == null) return;
			}

			// These values describe current project/platform policy rather than the imported texture,
			// so refresh them even when the measurement itself came from the persistent cache.
			facts.targetFormatDescription = facts.targetOverridden
				? facts.targetFormat.ToString()
				: GetInheritedTargetFormatDescription(platform, facts.targetCompression, settings.Profile.IsMobileClass);
			facts.crunchCompatible = IsCrunchCompatibleTarget(platform, facts.targetOverridden, facts.targetFormat, facts.targetCompression);
			facts.highQualityMemoryConcern = IsHighQualityMemoryConcern(platform, facts.targetOverridden, facts.targetFormat);
			facts.streamingMipmapsActive = QualitySettings.streamingMipmapsActive;
			facts.globalTextureMipmapLimit = QualitySettings.globalTextureMipmapLimit;
			backgroundContext.SeedCached(cacheKey, facts, measuredForBackground);
		}

		public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
		{
			if (context == null || !Handles(assetPath)) return null;
			string platform = context.Settings.PlatformString;
			string cacheKey = ScanCache.ScopedKey(assetPath, platform ?? string.Empty);
			var facts = context.GetOrMeasure<TextureFacts>(assetPath, cacheKey, null);
			if (facts == null) return null;
			return BuildEntry(assetPath, context.Settings, facts, context.GetFileSize(assetPath), context.GetGuid(assetPath));
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var settings = ctx.Settings;
			string platform = settings.PlatformString;
			string dependencyHash = ctx.GetDependencyHash(assetPath);
			string cacheKey = ScanCache.ScopedKey(assetPath, platform ?? string.Empty);
			TextureFacts facts;
			if (ctx.Cache != null && ctx.Cache.TryGet(cacheKey, dependencyHash, out TextureFacts cached))
			{
				facts = cached;
			}
			else
			{
				var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
				if (importer == null) return null;
				facts = MeasureTexture(assetPath, importer, settings);
				if (facts == null) return null;
				ctx.Cache?.Set(cacheKey, facts, dependencyHash);
			}
			facts.targetFormatDescription = facts.targetOverridden
				? facts.targetFormat.ToString()
				: GetInheritedTargetFormatDescription(platform, facts.targetCompression, settings.Profile.IsMobileClass);
			facts.crunchCompatible = IsCrunchCompatibleTarget(platform, facts.targetOverridden, facts.targetFormat, facts.targetCompression);
			facts.highQualityMemoryConcern = IsHighQualityMemoryConcern(platform, facts.targetOverridden, facts.targetFormat);
			facts.streamingMipmapsActive = QualitySettings.streamingMipmapsActive;
			facts.globalTextureMipmapLimit = QualitySettings.globalTextureMipmapLimit;
			return BuildEntry(assetPath, settings, facts, ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath), ctx.GetGuid(assetPath));
		}

		AssetEntry BuildEntry(string assetPath, OptimizerSettings settings, TextureFacts facts, long fileSize, string guid)
		{
			string platform = settings.PlatformString;
			int width = facts.width;
			int height = facts.height;
			long runtimeSize = facts.runtimeSize;
			int largest = Mathf.Max(width, height);

			var entry = new AssetEntry
			{
				Guid = guid ?? string.Empty,
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Texture,
				TypeLabel = facts.textureType.ToString(),
				FileSize = fileSize,
				RuntimeSize = runtimeSize
			};

			entry.Set("pixels", (long)width * height);
			entry.Set("dimensions", width + " x " + height);
			entry.Set("format", facts.format);
			entry.Set("maxSize", (long)facts.maxTextureSize);
			entry.Set("mipmaps", facts.mipmapEnabled ? "Yes" : "No");
			entry.Set("readWrite", facts.isReadable ? "Yes" : "No");
			entry.Set("compression", facts.textureCompression.ToString());
			entry.Set("crunch", facts.crunchedCompression ? "Yes" : "No");
			entry.Set("streaming", facts.streamingMipmaps ? "Yes" : "No");
			entry.Set("readWriteCpuBytes", facts.readableDataSize);
			entry.Set("mipSavingsBytes", facts.mipSavingsBytes);
			entry.Set("globalMipLimitSavingsBytes", facts.globalMipLimitSavingsBytes);
			entry.Set("shape", facts.textureShape.ToString());

			bool isUi = facts.textureType == TextureImporterType.Sprite
						|| facts.textureType == TextureImporterType.GUI
						|| facts.textureType == TextureImporterType.Cursor;
			bool isDataTexture = facts.textureType == TextureImporterType.SingleChannel
						 || facts.textureType == TextureImporterType.Cookie
						 || facts.textureType == TextureImporterType.Shadowmask
						 || facts.textureType == TextureImporterType.Lightmap;
			bool isMipLimitCompatibleShape = facts.textureShape == TextureImporterShape.Texture2D
						 || facts.textureShape == TextureImporterShape.Texture2DArray;
			string lowerPath = assetPath.ToLowerInvariant();
			bool isAtlasPackedSprite = facts.textureType == TextureImporterType.Sprite && IsAtlased(assetPath);
			bool targetOverridden = facts.targetOverridden;
			TextureImporterCompression targetCompression = facts.targetCompression;
			TextureImporterFormat targetFormat = facts.targetFormat;
			bool targetCrunch = facts.targetCrunch;
			int targetCompressionQuality = facts.targetCompressionQuality;
			entry.Set("targetPlatform", platform);
			entry.Set("targetOverride", targetOverridden ? "Yes" : "No");
			entry.Set("targetCompression", targetCompression.ToString());
			string targetFormatDescription = targetOverridden
				? targetFormat.ToString()
				: facts.targetFormatDescription;
			entry.Set("targetFormat", targetFormatDescription);
			entry.Set("targetCrunch", targetCrunch ? "Yes" : "No");
			entry.Set("targetCompressionQuality", (long)facts.targetCompressionQuality);

			// TEX001 - target-specific resolution budget.
			// A Sprite packed into an included Sprite Atlas does not use the source
			// texture importer settings for the packed runtime texture.
			using (RuleTiming.Begin("TEX001", entry.Path))
				if (!isAtlasPackedSprite)
				{
					int budget = isUi ? settings.maxUiTextureSize : settings.maxTextureSize;
					int effectiveMax = targetOverridden
						? facts.targetMaxTextureSize
						: facts.maxTextureSize;

					entry.Set("defaultMaxSize", (long)facts.maxTextureSize);
					entry.Set("targetMaxSize", (long)effectiveMax);

					if (largest > budget)
					{
						int target = Mathf.Max(32, Mathf.Min(effectiveMax, budget));
						if (target < largest)
						{
							long resolutionSavings = EstimateResolutionSavings(entry.RuntimeSize, width, height, target);
							string label = "Set " + platform + " Max Size to " + target;
							var issue = entry.AddIssue(
								"TEX001",
								width + " x " + height + "; " + platform + " effective Max Size " + effectiveMax,
								label,
								estimatedSavings: resolutionSavings,
								basis: resolutionSavings > 0 ? SavingsBasis.Memory : SavingsBasis.Unknown,
								detail: "The selected profile budget is " + budget + " for " + settings.Profile.DisplayName
										+ ". The current target setting is " + effectiveMax
										+ ". Estimated memory savings are approximate and assume the same target format/compression family; actual imported size can vary with format overhead and platform-specific encoding."
										+ " This action creates or edits only the " + platform + " override.",
								options: new List<FixOption>
								{
							new FixOption
							{
								ActionId = "tex001_set_target_max_size",
								Label = label,
								ResultDescription = "Sets only the selected platform's Max Size to " + target + ".",
								EstimatedSavings = resolutionSavings,
								Basis = resolutionSavings > 0 ? SavingsBasis.Memory : SavingsBasis.Unknown,
								Recommended = true,
								Risk = FixRisk.Medium,
								Warning = "This reduces the imported resolution for " + platform
										  + ". Inspect the asset at the new size before accepting the change.",
								Apply = c =>
								{
									var ti = (TextureImporter)c.Importer;
									var ps = GetTargetPlatformSettings(ti, platform);
									if (ps == null) throw new InvalidOperationException(
										"Unity did not return platform settings for " + platform + ".");
									ps.name = platform;
									ps.overridden = true;
									ps.maxTextureSize = target;
									ti.SetPlatformTextureSettings(ps);
									c.NeedsReimport = true;
								},
								Verify = c =>
								{
									var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
									if (ti == null) return false;
									var ps = GetTargetPlatformSettings(ti, platform);
									return ps != null && ps.overridden && ps.maxTextureSize <= target;
								}
							}
							});

							if (issue != null)
							{
								issue.Note("Default Max Size: " + facts.maxTextureSize
										   + " | " + platform + " effective Max Size: " + effectiveMax
										   + " | profile budget: " + budget, "Target");
							}
						}
					}
				}

			// TEX002 - actual target compression mode/format, not merely the Default tab.
			bool targetExplicitUncompressedFormat =
				targetOverridden && TextureFormatUtility.IsUncompressed(targetFormat);
			bool targetExplicitMobileUncompressedOverride = settings.Profile.IsMobileClass
				&& targetOverridden
				&& targetCompression != TextureImporterCompression.Uncompressed
				&& TextureFormatUtility.IsUncompressed(targetFormat)
				&& !isAtlasPackedSprite && !isDataTexture;
			bool targetActuallyUncompressed =
				targetCompression == TextureImporterCompression.Uncompressed
				|| (targetExplicitUncompressedFormat && !settings.Profile.IsMobileClass);

			using (RuleTiming.Begin("TEX002", entry.Path))
				if (!isDataTexture && !isAtlasPackedSprite && targetActuallyUncompressed && !targetExplicitMobileUncompressedOverride)
				{
					var issue = entry.AddIssue(
						"TEX002",
						"Target " + platform + ": Uncompressed (" + targetFormatDescription + ")",
						"Review compressed Automatic format",
						detail: "The selected target currently resolves to an uncompressed texture representation. "
								+ "The recommended action enables Unity Automatic compression for the selected target platform.",
						options: BuildCompressionOptions(
							"tex002",
							platform,
							normalWarning: "Normal Quality is the balanced default; still inspect detail-sensitive textures.",
							lowWarning: "Low Quality can reduce memory further but may introduce visible artifacts.",
							includeCrunch: false));
					if (issue != null)
						issue.Note("Target format: " + targetFormatDescription + "; target override: " + (targetOverridden ? "Yes" : "No"),
							"Target");
				}

			// TEX003 - Crunch only when the selected target's effective compression policy
			// can actually produce a Crunch-compatible format. Do not infer that every
			// Automatic mobile target supports Crunch: Android can target ETC/ETC2, ASTC,
			// DXT or PVRTC depending on build configuration.
			using (RuleTiming.Begin("TEX003", entry.Path))
				{
					int crunchQuality = Mathf.Clamp(settings.crunchQuality, 0, 100);
					bool crunchCompatible = facts.crunchCompatible;
					bool qualityMismatch = targetCrunch && targetCompressionQuality != crunchQuality;
					if (settings.preferCrunch
						&& !isAtlasPackedSprite
						&& targetCompression != TextureImporterCompression.Uncompressed
						&& !isDataTexture
						&& crunchCompatible
						&& (!targetCrunch || qualityMismatch))
					{
						string current = targetCrunch ? "Crunch on (quality " + targetCompressionQuality + ")" : "Crunch off";
						var issue = entry.AddIssue(
							"TEX003",
							"Target " + platform + ": " + current + " (" + targetFormatDescription + ")",
							"Enable Crunch on (quality " + crunchQuality + ")", 0,
							c =>
							{
								var ti = (TextureImporter)c.Importer;
								var ps = GetTargetPlatformSettings(ti, platform);
								if (ps == null) throw new InvalidOperationException("Target platform settings unavailable for " + platform + ".");
								ps.name = platform;
								ps.overridden = true;
								ps.crunchedCompression = true;
								ps.compressionQuality = crunchQuality;
								ti.SetPlatformTextureSettings(ps);
								c.NeedsReimport = true;
							},
							detail: "The selected target format is proven Crunch-compatible and the project preference requests Crunch at the configured quality " + crunchQuality + ".",
							verify: c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								var ps = ti != null ? GetTargetPlatformSettings(ti, platform) : null;
								return ps != null && ps.crunchedCompression && ps.compressionQuality == crunchQuality;
							});
						if (issue != null)
							issue.Note("Configured project Crunch quality: " + crunchQuality + ".", "Compression Quality");
					}
				}

			// TEX004 - CPU-readable copy. Review only.
			using (RuleTiming.Begin("TEX004", entry.Path))
				if (facts.isReadable)
				{
					long estimate = facts.readableDataSize > 0 ? facts.readableDataSize : 0;
					var option = new FixOption
					{
						ActionId = "tex004_disable_read_write",
						Label = "Disable Read/Write",
						ResultDescription = "Removes the CPU-addressable texture copy after reimport.",
						Recommended = true,
						EstimatedSavings = estimate,
						Basis = estimate > 0 ? SavingsBasis.Memory : SavingsBasis.Unknown,
						Risk = FixRisk.Medium,
						Warning = "Breaks CPU-side pixel access that depends on readability, including APIs such as "
								  + "Texture2D.GetPixels/SetPixels and ImageConversion.EncodeToPNG.",
						Apply = c =>
						{
							((TextureImporter)c.Importer).isReadable = false;
							c.NeedsReimport = true;
						},
						Verify = c =>
						{
							var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
							return ti != null && !ti.isReadable;
						}
					};

					var issue = entry.AddIssue("TEX004", "Read/Write enabled",
						"Review Fix: disable Read/Write",
						estimatedSavings: estimate,
						basis: estimate > 0 ? SavingsBasis.Memory : SavingsBasis.Unknown,
						detail: "Unity documents that a readable texture requires an additional CPU-addressable copy. "
								+ (estimate > 0
									? "The current imported raw CPU data measures approximately " + DisplayUtility.FormatBytes(estimate) + "."
									: "The current importer did not yield a reliable raw-data size estimate."),
						options: new List<FixOption> { option });
					if (issue != null)
						issue.Note("CPU-readable data estimate is taken from the imported Texture2D raw data when available; GPU memory is not counted as savings.",
							"Savings");
				}

			// TEX005 / TEX006 - mipmap recommendations remain Review because the importer
			// cannot prove runtime usage. For sprites already packed into an included Sprite
			// Atlas, the atlas owns the packed texture's mipmap settings.
			using (RuleTiming.Begin("TEX005", entry.Path))
				if (facts.mipmapEnabled
					&& !isAtlasPackedSprite
					&& isUi)
				{
					var issue = entry.AddIssue(
						"TEX005",
						"Mip maps generated for " + facts.textureType,
						"Review / disable Generate Mip Maps",
						detail: "Mipmaps are often unnecessary for fixed-size UI imagery, but sprites, GUI textures and cursors can also be rendered at varying scales.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex005_disable_mipmaps",
							Label = "Disable Generate Mip Maps",
							ResultDescription = "Disables mipmap generation for this texture.",
							Recommended = true,
							Warning = "Only use this when the texture is truly fixed-size/UI-only and does not need mipmapping.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								((TextureImporter)c.Importer).mipmapEnabled = false;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && !ti.mipmapEnabled;
							}
						}
						});
					if (issue != null)
						issue.Note("Source texture mipmaps are independent of the packed atlas mipmap setting.", "Why review");
				}
				else using (RuleTiming.Begin("TEX006", entry.Path))
					if (!facts.mipmapEnabled
								&& !isAtlasPackedSprite
								&& (facts.textureType == TextureImporterType.Default
									|| facts.textureType == TextureImporterType.NormalMap)
								&& facts.textureShape == TextureImporterShape.Texture2D
								&& !isDataTexture
								&& largest >= 128)
					{
						var issue = entry.AddIssue(
							"TEX006",
							"No mip maps",
							"Review / enable Generate Mip Maps",
							detail: "This is a 2D Default or Normal Map texture large enough that distance-varying world-space usage is plausible. The analyzer cannot prove runtime usage.",
							options: new List<FixOption>
							{
						new FixOption
						{
							ActionId = "tex006_enable_mipmaps",
							Label = "Enable Generate Mip Maps",
							ResultDescription = "Enables mipmap generation for this texture.",
							Recommended = true,
							Warning = "Keep mipmaps disabled when the texture is intentionally sampled without a mip chain, such as some UI/data workflows.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								((TextureImporter)c.Importer).mipmapEnabled = true;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && ti.mipmapEnabled;
							}
						}
							});
						if (issue != null)
							issue.Note("No mipmap fix is marked Verified because runtime usage can intentionally require a non-mipmapped texture.",
								"Why review");
					}

			// TEX007 - generated alpha configuration without source alpha.
			using (RuleTiming.Begin("TEX007", entry.Path))
				if (facts.sourceHasAlphaKnown
					&& !facts.sourceHasAlpha
					&& facts.alphaSource != TextureImporterAlphaSource.None
					&& facts.textureType != TextureImporterType.NormalMap)
				{
					var issue = entry.AddIssue(
						"TEX007",
						"Alpha Source: " + facts.alphaSource,
						"Review / set Alpha Source to None",
						detail: "The source image reports no alpha channel. A non-None alpha source may still be intentional if alpha is generated from another source, so this is not an automatic fix.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex007_disable_generated_alpha",
							Label = "Set Alpha Source to None",
							ResultDescription = "Stops importing/generated alpha when the source provides no alpha.",
							Recommended = true,
							Warning = "Only use this when the material, shader and downstream texture processing do not require the generated alpha.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								var ti = (TextureImporter)c.Importer;
								ti.alphaSource = TextureImporterAlphaSource.None;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && ti.alphaSource == TextureImporterAlphaSource.None;
							}
						}
						});
					if (issue != null)
						issue.Note("Source alpha: none; configured alpha source: " + facts.alphaSource + ".", "Alpha");
				}

			// TEX008 - filename heuristic. Offer a Review action because the importer change is reversible,
			// but never mark it Verified.
			using (RuleTiming.Begin("TEX008", entry.Path))
				if (facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D
					&& MatchesAny(lowerPath, k_NormalHints))
				{
					var issue = entry.AddIssue(
						"TEX008",
						"Texture Type: Default, normal-map-like name",
						"Review as Normal Map",
						detail: "The filename is only a hint. Confirm the pixels actually encode normal data before changing Texture Type.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex008_set_normal_map",
							Label = "Import as Normal Map",
							ResultDescription = "Changes Texture Type to Normal Map and lets Unity apply its normal-map import processing.",
							Recommended = true,
							Warning = "Do not use this action merely because of the filename. It changes import semantics and the resulting texture.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								var ti = (TextureImporter)c.Importer;
								ti.textureType = TextureImporterType.NormalMap;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && ti.textureType == TextureImporterType.NormalMap;
							}
						}
						});
				}

			// TEX009 - only report when project-wide texture streaming is actually active.
			// A per-texture flag alone does not activate the mipmap streaming system.
			using (RuleTiming.Begin("TEX009", entry.Path))
				if (facts.streamingMipmapsActive
					&& facts.mipmapEnabled
					&& !facts.streamingMipmaps
					&& !isAtlasPackedSprite
					&& largest >= settings.streamingMipmapMinSize
					&& !isUi)
				{
					var issue = entry.AddIssue(
						"TEX009",
						"Streaming Mipmaps off",
						"Review / enable Streaming Mipmaps",
						detail: "Mipmap streaming is active project-wide, but this large texture is not opted into the per-texture streaming system.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex009_enable_streaming_mipmaps",
							Label = "Enable Streaming Mipmaps",
							ResultDescription = "Enables per-texture mipmap streaming for this texture.",
							Recommended = true,
							Warning = "Streaming changes which mip levels Unity keeps resident based on camera demand. Validate memory and visual behavior.",
							Risk = FixRisk.Low,
							Apply = c =>
							{
								((TextureImporter)c.Importer).streamingMipmaps = true;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && ti.streamingMipmaps;
							}
						}
						});
					if (issue != null)
						issue.Note("Project-wide mipmap streaming is active; suggested threshold: " + settings.streamingMipmapMinSize + " px; target " + largest + " px.",
							"Threshold");
				}

			// TEX010 - high anisotropy is informational, never a universal auto-fix.
			using (RuleTiming.Begin("TEX010", entry.Path))
				if (facts.mipmapEnabled
					&& !isAtlasPackedSprite
					&& facts.anisoLevel > settings.maxAnisoLevel
					&& largest >= 256)
				{
					var issue = entry.AddIssue(
						"TEX010",
						"Aniso " + facts.anisoLevel,
						"Review against project maximum " + settings.maxAnisoLevel,
						options: new List<FixOption> { new FixOption {
							Label = "Reduce Anisotropic Filtering",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Texture(c, (ti, _) => ti.anisoLevel = 1)
						} }
					);
					if (issue != null)
						issue.Note("High anisotropy is useful for surfaces viewed at grazing angles; the project maximum is a review reference, not a correctness constraint.",
							"Context");
				}

			// TEX011 - explicit mobile target override selecting an uncompressed format.
			using (RuleTiming.Begin("TEX011", entry.Path))
				if (targetExplicitMobileUncompressedOverride)
				{
					var issue = entry.AddIssue(
						"TEX011",
						platform + " override: " + targetFormat,
						"Review Automatic compressed format",
						detail: "The mobile override explicitly selects an uncompressed texture format. Unity can choose a platform-appropriate compressed format when Format is Automatic.",
						options: BuildCompressionOptions(
							"tex011",
							platform,
							normalWarning: "Normal Quality uses the platform's standard automatic compression for the target.",
							lowWarning: "Low Quality increases compression and may reduce visual quality.",
							includeCrunch: false));
					if (issue != null)
						issue.Note("Explicit target format: " + targetFormatDescription + ". Do not force a single ASTC/ETC2 format here because device capability policy belongs to the project/build configuration.",
							"Target");
				}

			// TEX012 - source file size. No source rewriting action.
			using (RuleTiming.Begin("TEX012", entry.Path))
				if (fileSize > settings.textureFileSizeWarnKb * 1024L)
				{
					var issue = entry.AddIssue(
						"TEX012",
						DisplayUtility.FormatBytes(fileSize),
						"Review source resolution / source-art size");
					if (issue != null)
						issue.Note("Source size is not the same as the build's transcoded GPU texture size.", "Measurement");
				}

			// TEX013 - data texture color-space heuristic.
			using (RuleTiming.Begin("TEX013", entry.Path))
				if (facts.sRGBTexture
					&& facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D
					&& MatchesAny(lowerPath, k_DataHints))
				{
					var issue = entry.AddIssue(
						"TEX013",
						"sRGB on",
						"Review sRGB off for confirmed linear data",
						detail: "The path/name suggests numeric data, but the analyzer cannot prove the shader interpretation.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex013_disable_srgb",
							Label = "Disable sRGB (Color Texture)",
							ResultDescription = "Imports this texture as linear data rather than color data.",
							Recommended = true,
							Warning = "Only correct when the texture is consumed as linear numeric data. Changing this affects sampled values.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								var ti = (TextureImporter)c.Importer;
								ti.sRGBTexture = false;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null && !ti.sRGBTexture;
							}
						}
						});
				}

			// TEX014 - actual grayscale measurement. No auto repack because that would require shader/material changes.
			using (RuleTiming.Begin("TEX014", entry.Path))
				if (settings.deepTextureScan
					&& facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D
					&& facts.greyscale)
				{
					var issue = entry.AddIssue(
						"TEX014",
						"RGB channels are effectively greyscale",
						"Review Single Channel / channel packing",
						detail: "The sampled imported pixels had matching RGB channels. Repacking is deliberately not automatic because material/shader channel semantics must remain valid.",
						options: new List<FixOption> { new FixOption {
							Label = "Use R8 Single-Channel Format",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "Confirm the consuming shader/material reads the texture as a single-channel value before applying this Review Fix.",
							Apply = c => ImporterFixUtility.Texture(c, (ti, platform) =>
							{
								var ps = GetTargetPlatformSettings(ti, platform);
								if (ps == null) throw new InvalidOperationException("Target platform settings unavailable for " + platform + ".");
								ps.name = platform;
								ps.overridden = true;
								ps.format = TextureImporterFormat.R8;
								ps.textureCompression = TextureImporterCompression.Uncompressed;
								ti.SetPlatformTextureSettings(ps);
							}),
							Verify = c => { var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter; var ps = ti != null ? GetTargetPlatformSettings(ti, facts.targetPlatform) : null; return ps != null && ps.overridden && ps.format == TextureImporterFormat.R8 && ps.textureCompression == TextureImporterCompression.Uncompressed; }
						} }
					);
					if (issue != null)
						issue.Note("This is a content measurement, not proof that the shader can consume a single-channel representation.",
							"Safety");
				}

			using (RuleTiming.Begin("PAA0007", entry.Path))
				if (settings.deepTextureScan
					&& facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D
					&& facts.emptyPixelsPercent >= 0f
					&& facts.emptyPixelsPercent > settings.textureAtlasEmptySpaceWarnPercent
					&& !isAtlasPackedSprite)
				{
					entry.Set("emptySpace", facts.emptyPixelsPercent.ToString("0.##") + "%");
					entry.AddIssue(
						"PAA0007",
						facts.emptyPixelsPercent.ToString("0.##") + "% empty pixels",
						"Review texture/atlas layout");
				}

			using (RuleTiming.Begin("PAA0005", entry.Path))
				if (settings.deepTextureScan
					&& facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D
					&& facts.solidColor
					&& width * (long)height > 1)
				{
					entry.AddIssue(
						"PAA0005",
						"All sampled pixels are the same color",
						"Review 1x1 replacement",
						0, null,
						"Unity Project Auditor flags solid-color textures because a constant image can be represented by one texel. Project Optimizer only reports this after its deep texture sampling pass, so a failed/unsupported decode fails closed.");
				}

			// TEX015 - no mip chain plus filtering settings whose intent is questionable without mips.
			using (RuleTiming.Begin("TEX015", entry.Path))
				if (!facts.mipmapEnabled
					&& !isAtlasPackedSprite
					&& (facts.filterMode == FilterMode.Trilinear || facts.anisoLevel > 1))
				{
					var issue = entry.AddIssue(
						"TEX015",
						facts.filterMode + ", aniso " + facts.anisoLevel,
						"Review Bilinear + Aniso 1",
						detail: "Changing filtering can alter visual quality, so this is a Review Fix rather than a Verified optimization.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tex015_use_bilinear_no_aniso",
							Label = "Set Bilinear + Aniso 1",
							ResultDescription = "Uses Bilinear filtering and sets anisotropic filtering to level 1 for this non-mipmapped texture.",
							Recommended = true,
							Warning = "This can change appearance at oblique viewing angles. Keep the current settings when they are intentional.",
							Risk = FixRisk.Medium,
							Apply = c =>
							{
								var ti = (TextureImporter)c.Importer;
								ti.filterMode = FilterMode.Bilinear;
								ti.anisoLevel = 1;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
								return ti != null
									   && ti.filterMode == FilterMode.Bilinear
									   && ti.anisoLevel == 1;
							}
						}
						});
				}

			// TEX016 - atlas membership. Never guess which atlas should own the sprite.
			using (RuleTiming.Begin("TEX016", entry.Path))
				if (m_AnyAtlases
					&& facts.textureType == TextureImporterType.Sprite
					&& !IsAtlased(assetPath))
				{
					var issue = entry.AddIssue(
						"TEX016",
						"Loose sprite",
						"Review Sprite Atlas packing",
						detail: "One or more Sprite Atlases exist, but this texture is not among the packables known to this analyzer. "
								+ "No automatic add-to-atlas action is offered because atlas selection, filters and addressing are project design decisions.");
					if (issue != null)
						issue.Note("Known atlas packable roots/folders were examined during BeginScan.", "Atlas");
				}

			// TEX017 - Unity 6 explicitly documents that Ignore Mipmap Limit has no effect outside
			// Texture2D/Texture2DArray or without mipmaps.
			using (RuleTiming.Begin("TEX017", entry.Path))
				if (facts.mipmapEnabled
					&& !isAtlasPackedSprite
					&& facts.ignoreMipmapLimit
					&& isMipLimitCompatibleShape
					&& facts.globalTextureMipmapLimit > 0)
				{
					var issue = entry.AddIssue(
						"TEX017",
						"Ignore Mipmap Limit enabled",
						"Respect Global Mipmap Limit", 0,
						c =>
						{
							var ti = (TextureImporter)c.Importer;
							ti.ignoreMipmapLimit = false;
							c.NeedsReimport = true;
						},
						detail: "The texture is eligible for Unity's mipmap-limit system and is explicitly opting out of the global policy.",
						verify: c =>
						{
							var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
							return ti != null && !ti.ignoreMipmapLimit;
						});
				}

			// TEX018 - high-quality compression only where we can reasonably establish that
			// the selected platform uses a higher-memory format/quality tier. HQ itself is
			// not universally more memory-expensive (for example WebGL can use the same
			// DXT family), so do not report it merely because the enum says CompressedHQ.
			using (RuleTiming.Begin("TEX018", entry.Path))
				if (!isDataTexture
					&& !isAtlasPackedSprite
					&& targetCompression == TextureImporterCompression.CompressedHQ
					&& largest >= 1024
					&& facts.highQualityMemoryConcern)
				{
					var issue = entry.AddIssue(
						"TEX018",
						"Target " + platform + ": High Quality compression",
						"Review Normal / Low Quality",
						detail: "The selected target's High Quality compression tier can use a higher-memory texture representation than the normal tier. The visual benefit should be justified for large textures.",
						options: BuildCompressionOptions(
							"tex018",
							platform,
							normalWarning: "Normal Quality is the balanced choice for most textures; inspect the visual difference before changing a detail-sensitive asset.",
							lowWarning: "Low Quality can reduce memory further but may introduce visible artifacts.",
							includeCrunch: false));
					if (issue != null)
						issue.Note("Target format policy: " + targetFormatDescription + "; target override: " + (targetOverridden ? "Yes" : "No") + ".", "Target");
				}

			entry.Finalise();
			return entry;
		}

		/// <summary>Loads the texture once and records everything the rules need from it.</summary>
		static TextureFacts MeasureTexture(string assetPath, TextureImporter importer, OptimizerSettings settings)
		{
			var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
			if (texture == null) return null;

			string platform = settings.PlatformString;
			TextureImporterPlatformSettings target = GetTargetPlatformSettings(importer, platform);
			bool targetOverridden = target != null && target.overridden;
			var facts = new TextureFacts
			{
				width = texture.width,
				height = texture.height,
				runtimeSize = UnityEditorUtility.RuntimeSizeOf(texture),
				textureType = importer.textureType,
				maxTextureSize = importer.maxTextureSize,
				mipmapEnabled = importer.mipmapEnabled,
				isReadable = importer.isReadable,
				textureCompression = importer.textureCompression,
				crunchedCompression = importer.crunchedCompression,
				streamingMipmaps = importer.streamingMipmaps,
				textureShape = importer.textureShape,
				alphaSource = importer.alphaSource,
				anisoLevel = importer.anisoLevel,
				filterMode = importer.filterMode,
				sRGBTexture = importer.sRGBTexture,
				ignoreMipmapLimit = importer.ignoreMipmapLimit,
				targetPlatform = platform ?? string.Empty,
				targetOverridden = targetOverridden,
				targetMaxTextureSize = targetOverridden ? target.maxTextureSize : importer.maxTextureSize,
				targetCompression = targetOverridden ? target.textureCompression : importer.textureCompression,
				targetFormat = targetOverridden ? target.format : TextureImporterFormat.Automatic,
				targetCrunch = targetOverridden ? target.crunchedCompression : importer.crunchedCompression,
				targetCompressionQuality = targetOverridden ? target.compressionQuality : importer.compressionQuality
			};

			var texture2D = texture as Texture2D;
			facts.format = texture2D != null ? texture2D.format.ToString() : "-";

			try
			{
				facts.sourceHasAlpha = importer.DoesSourceTextureHaveAlpha();
				facts.sourceHasAlphaKnown = true;
			}
			catch
			{
				// Fail closed for TEX007. If Unity cannot determine source alpha, do not
				// claim that a non-None alpha source is wasteful.
				facts.sourceHasAlpha = true;
				facts.sourceHasAlphaKnown = false;
			}

			if (facts.isReadable && texture2D != null)
			{
				try
				{
					facts.readableDataSize = texture2D.GetRawTextureData<byte>().Length;
				}
				catch
				{
					facts.readableDataSize = 0;
				}
			}

			long mipBytes = EstimateMipBytes(texture.width, texture.height, texture.mipmapCount, facts.runtimeSize);
			facts.mipSavingsBytes = facts.mipmapEnabled ? mipBytes : 0;
			int globalLimit = QualitySettings.globalTextureMipmapLimit;
			if (facts.ignoreMipmapLimit && globalLimit > 0 && facts.mipmapEnabled)
				facts.globalMipLimitSavingsBytes = EstimateMipLimitSavings(texture.width, texture.height, texture.mipmapCount, globalLimit, facts.runtimeSize);

			if (settings.deepTextureScan
				&& facts.textureType == TextureImporterType.Default
				&& facts.textureShape == TextureImporterShape.Texture2D)
			{
				facts.greyscale = IsGreyscale(texture);
				facts.solidColor = IsSolidColor(texture);
				facts.emptyPixelsPercent = GetEmptyPixelsPercent(texture2D);
			}
			return facts;
		}

		static long EstimateMipBytes(int width, int height, int mipCount, long runtimeBytes)
		{
			if (runtimeBytes <= 0 || mipCount <= 1 || width <= 0 || height <= 0) return 0;
			long total = 0, baseLevel = (long)width * height; int w = width, h = height;
			for (int m = 0; m < mipCount; m++) { total += (long)w * h; w = Mathf.Max(1, w >> 1); h = Mathf.Max(1, h >> 1); }
			return total > baseLevel ? Math.Min(runtimeBytes, (long)(runtimeBytes * ((double)(total - baseLevel) / total))) : 0;
		}
		static long EstimateMipLimitSavings(int width, int height, int mipCount, int limit, long runtimeBytes)
		{
			if (runtimeBytes <= 0 || mipCount <= 1 || limit <= 0) return 0;
			long total = 0, removed = 0; int w = width, h = height;
			for (int m = 0; m < mipCount; m++) { long px = (long)w * h; total += px; if (m < limit) removed += px; w = Mathf.Max(1, w >> 1); h = Mathf.Max(1, h >> 1); }
			return removed > 0 ? Math.Min(runtimeBytes, (long)(runtimeBytes * ((double)removed / total))) : 0;
		}

		static float GetEmptyPixelsPercent(Texture2D texture)
		{
			if (texture == null) return -1f;
			if (texture.width <= 0 || texture.height <= 0) return 0f;

			Texture2D copy = null;
			try
			{
				Color32[] pixels;
				if (texture.isReadable)
				{
					pixels = texture.GetPixels32();
				}
				else
				{
					var previous = RenderTexture.active;
					var temp = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
					try
					{
						Graphics.Blit(texture, temp);
						RenderTexture.active = temp;
						copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
						copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
						copy.Apply(false, false);
						pixels = copy.GetPixels32();
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
					if (pixels[i].a == 0) ++empty;
				return (float)empty / pixels.Length * 100f;
			}
			catch
			{
				return -1f;
			}
			finally
			{
				if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
			}
		}

		bool IsAtlased(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return false;
			if (m_AtlasCovered != null && m_AtlasCovered.Contains(assetPath)) return true;
			if (m_AtlasFolders == null) return false;
			for (int i = 0; i < m_AtlasFolders.Count; i++)
				if (assetPath.StartsWith(m_AtlasFolders[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		void RegisterAtlasPackables(SpriteAtlas atlas, HashSet<int> visitedAtlases, bool requireIncludedInBuild)
		{
			if (atlas == null || visitedAtlases == null) return;
			if (requireIncludedInBuild && !SpriteAtlasExtensions.IsIncludeInBuild(atlas)) return;
			if (!visitedAtlases.Add(atlas.GetInstanceID())) return;

			foreach (var packable in atlas.GetPackables())
			{
				if (packable == null) continue;
				var p = AssetDatabase.GetAssetPath(packable);
				if (string.IsNullOrEmpty(p)) continue;
				if (AssetDatabase.IsValidFolder(p)) m_AtlasFolders.Add(p.TrimEnd('/') + "/");
				else m_AtlasCovered.Add(p);
			}

			// A variant atlas can inherit its sprites from a master atlas. When the variant
			// is included in the build, its master's packables are also runtime-relevant.
			var master = SpriteAtlasExtensions.GetMasterAtlas(atlas);
			if (master != null && master != atlas)
				RegisterAtlasPackables(master, visitedAtlases, requireIncludedInBuild: false);
		}

		static string GetInheritedTargetFormatDescription(
			string platform,
			TextureImporterCompression compression,
			bool mobileProfile)
		{
			if (compression == TextureImporterCompression.Uncompressed)
				return "Inherited Default / Uncompressed";

			if (IsAndroidPlatform(platform))
			{
				var subtarget = EditorUserBuildSettings.androidBuildSubtarget;
				if (subtarget != MobileTextureSubtarget.Generic) return "Android " + subtarget + " (Automatic)";

				var formats = PlayerSettings.Android.textureCompressionFormats;
				if (formats != null && formats.Length > 0)
				{
					if (formats.Length == 1) return "Android " + formats[0] + " (Automatic)";
					return "Android " + string.Join(" / ", Array.ConvertAll(formats, f => f.ToString())) + " (Automatic)";
				}

				return "Android platform policy (Automatic)";
			}

			if (IsIOSPlatform(platform))
				return "iOS platform policy (Automatic)";

			if (IsWebGLPlatform(platform))
				return "WebGL DXT family (Automatic)";

			if (IsDesktopPlatform(platform))
				return compression == TextureImporterCompression.CompressedHQ
					? "Desktop BC7-family (Automatic)"
					: "Desktop DXT family (Automatic)";

			return mobileProfile
				? "Mobile platform policy (Automatic)"
				: "Platform policy (Automatic)";
		}

		static bool IsCrunchCompatibleTarget(
			string platform,
			bool targetOverridden,
			TextureImporterFormat format,
			TextureImporterCompression compression)
		{
			if (compression == TextureImporterCompression.Uncompressed) return false;

			// An explicit format always wins over the platform-policy inference.
			if (targetOverridden && format != TextureImporterFormat.Automatic)
				return IsCrunchBaseFormat(format);

			// For Automatic, resolve only where the current Unity build settings expose
			// enough information to know the compression family.
			if (IsAndroidPlatform(platform))
			{
				var subtarget = EditorUserBuildSettings.androidBuildSubtarget;
				if (subtarget != MobileTextureSubtarget.Generic)
				{
					switch (subtarget)
					{
						case MobileTextureSubtarget.DXT:
						case MobileTextureSubtarget.ETC:
						case MobileTextureSubtarget.ETC2:
							return true;
						default:
							return false;
					}
				}

				var formats = PlayerSettings.Android.textureCompressionFormats;
				if (formats == null || formats.Length == 0) return false;
				for (int i = 0; i < formats.Length; i++)
				{
					if (formats[i] == TextureCompressionFormat.DXTC
						|| formats[i] == TextureCompressionFormat.ETC
						|| formats[i] == TextureCompressionFormat.ETC2)
						return true;
				}
				return false;
			}

			if (IsIOSPlatform(platform)) return false;

			if (IsWebGLPlatform(platform)) return true;

			if (IsDesktopPlatform(platform))
				return compression != TextureImporterCompression.CompressedHQ;

			return false;
		}

		static bool IsCrunchBaseFormat(TextureImporterFormat format)
		{
			switch (format)
			{
				case TextureImporterFormat.DXT1:
				case TextureImporterFormat.DXT5:
				case TextureImporterFormat.ETC_RGB4:
				case TextureImporterFormat.ETC2_RGBA8:
					return true;
				case TextureImporterFormat.DXT1Crunched:
				case TextureImporterFormat.DXT5Crunched:
				case TextureImporterFormat.ETC_RGB4Crunched:
				case TextureImporterFormat.ETC2_RGBA8Crunched:
					return false;
				default:
					return false;
			}
		}

		static bool IsAndroidPlatform(string platform)
		{
			return !string.IsNullOrEmpty(platform)
				&& platform.IndexOf("android", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsIOSPlatform(string platform)
		{
			return !string.IsNullOrEmpty(platform)
				&& (platform.IndexOf("iphone", StringComparison.OrdinalIgnoreCase) >= 0
					|| platform.IndexOf("ios", StringComparison.OrdinalIgnoreCase) >= 0
					|| platform.IndexOf("tvos", StringComparison.OrdinalIgnoreCase) >= 0);
		}

		static bool IsWebGLPlatform(string platform)
		{
			return !string.IsNullOrEmpty(platform)
				&& (platform.IndexOf("webgl", StringComparison.OrdinalIgnoreCase) >= 0
					|| string.Equals(platform, "Web", StringComparison.OrdinalIgnoreCase));
		}

		static bool IsDesktopPlatform(string platform)
		{
			if (string.IsNullOrEmpty(platform)) return false;
			return platform.IndexOf("standalone", StringComparison.OrdinalIgnoreCase) >= 0
				|| platform.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0
				|| platform.IndexOf("linux", StringComparison.OrdinalIgnoreCase) >= 0
				|| platform.IndexOf("mac", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		/// <summary>
		/// Returns the platform settings object for the build-target key used by the current
		/// optimizer profile. Unity 6 returns a settings object even when the override is disabled,
		/// so callers must inspect .overridden rather than nullness.
		/// </summary>
		static long EstimateResolutionSavings(long runtimeSize, int width, int height, int target)
		{
			if (runtimeSize <= 0 || width <= 0 || height <= 0 || target <= 0) return 0;
			int largest = Mathf.Max(width, height);
			if (target >= largest) return 0;
			double scale = target / (double)largest;
			long targetWidth = Math.Max(1L, (long)Math.Ceiling(width * scale));
			long targetHeight = Math.Max(1L, (long)Math.Ceiling(height * scale));
			long currentPixels = (long)width * height;
			long targetPixels = targetWidth * targetHeight;
			if (targetPixels >= currentPixels) return 0;
			long estimate = (long)Math.Round(runtimeSize * (1.0 - targetPixels / (double)currentPixels));
			return Math.Max(0, Math.Min(runtimeSize, estimate));
		}

		static TextureImporterPlatformSettings GetTargetPlatformSettings(TextureImporter importer, string platform)
		{
			if (importer == null || string.IsNullOrEmpty(platform)) return null;
			try { return importer.GetPlatformTextureSettings(platform); }
			catch { return null; }
		}

		static List<FixOption> BuildCompressionOptions(
			string actionPrefix,
			string platform,
			string normalWarning,
			string lowWarning,
			bool includeCrunch)
		{
			var options = new List<FixOption>(includeCrunch ? 3 : 2);

			options.Add(BuildCompressionOption(
				actionPrefix + "_normal",
				"Use Normal Quality",
				platform,
				TextureImporterCompression.Compressed,
				normalWarning,
				true));

			options.Add(BuildCompressionOption(
				actionPrefix + "_low",
				"Use Low Quality",
				platform,
				TextureImporterCompression.CompressedLQ,
				lowWarning,
				false));

			if (includeCrunch)
			{
				options.Add(new FixOption
				{
					ActionId = actionPrefix + "_crunch",
					Label = "Use Normal Quality + Crunch",
					ResultDescription = "Uses Automatic Normal compression and enables Crunch on the selected platform.",
					Warning = "Crunch reduces stored/downloaded size but increases import/compression time and does not reduce GPU memory.",
					Risk = FixRisk.Medium,
					Apply = c =>
					{
						var ti = (TextureImporter)c.Importer;
						var ps = GetTargetPlatformSettings(ti, platform);
						if (ps == null) throw new InvalidOperationException(
							"Unity did not return platform settings for " + platform + ".");
						ps.name = platform;
						ps.overridden = true;
						ps.format = TextureImporterFormat.Automatic;
						ps.textureCompression = TextureImporterCompression.Compressed;
						ps.crunchedCompression = true;
						ti.SetPlatformTextureSettings(ps);
						c.NeedsReimport = true;
					},
					Verify = c =>
					{
						var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
						if (ti == null) return false;
						var ps = GetTargetPlatformSettings(ti, platform);
						return ps != null
							   && ps.overridden
							   && ps.format == TextureImporterFormat.Automatic
							   && ps.textureCompression == TextureImporterCompression.Compressed
							   && ps.crunchedCompression;
					}
				});
			}

			return options;
		}

		static FixOption BuildCompressionOption(
			string actionId,
			string label,
			string platform,
			TextureImporterCompression compression,
			string warning,
			bool recommended)
		{
			return new FixOption
			{
				ActionId = actionId,
				Label = label,
				ResultDescription = "Uses Unity Automatic texture-format selection with "
								   + compression + " for the selected platform.",
				Recommended = recommended,
				Warning = warning,
				Risk = FixRisk.Medium,
				Apply = c =>
				{
					var ti = (TextureImporter)c.Importer;
					var ps = GetTargetPlatformSettings(ti, platform);
					if (ps == null) throw new InvalidOperationException(
						"Unity did not return platform settings for " + platform + ".");
					ps.name = platform;
					ps.overridden = true;
					ps.format = TextureImporterFormat.Automatic;
					ps.textureCompression = compression;
					ps.crunchedCompression = false;
					ti.SetPlatformTextureSettings(ps);
					c.NeedsReimport = true;
				},
				Verify = c =>
				{
					var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
					if (ti == null) return false;
					var ps = GetTargetPlatformSettings(ti, platform);
					return ps != null
						   && ps.overridden
						   && ps.format == TextureImporterFormat.Automatic
						   && ps.textureCompression == compression
						   && !ps.crunchedCompression;
				}
			};
		}

		static bool IsHighQualityMemoryConcern(
			string platform,
			bool targetOverridden,
			TextureImporterFormat targetFormat)
		{
			if (targetOverridden && targetFormat != TextureImporterFormat.Automatic)
			{
				switch (targetFormat)
				{
					case TextureImporterFormat.BC7:
					case TextureImporterFormat.ASTC_4x4:
					case TextureImporterFormat.ASTC_HDR_4x4:
						return true;
					default:
						return false;
				}
			}

			if (IsDesktopPlatform(platform))
				return true;

			if (IsAndroidPlatform(platform))
			{
				var subtarget = EditorUserBuildSettings.androidBuildSubtarget;
				if (subtarget == MobileTextureSubtarget.ASTC) return true;

				if (subtarget != MobileTextureSubtarget.Generic) return false;

				var formats = PlayerSettings.Android.textureCompressionFormats;
				if (formats == null) return false;
				for (int i = 0; i < formats.Length; i++)
					if (formats[i] == TextureCompressionFormat.ASTC) return true;
			}

			return false;
		}

		static bool MatchesAny(string pathOrFilename, string[] hints)
		{
			if (string.IsNullOrEmpty(pathOrFilename) || hints == null || hints.Length == 0) return false;

			string filename = System.IO.Path.GetFileNameWithoutExtension(pathOrFilename) ?? pathOrFilename;
			filename = filename.ToLowerInvariant();
			var tokens = filename.Split(new[] { '_', '-', ' ', '.', '[', ']', '(', ')', '{', '}', '+' },
				StringSplitOptions.RemoveEmptyEntries);

			for (int i = 0; i < hints.Length; i++)
			{
				string needle = hints[i].Trim('_').ToLowerInvariant();
				for (int j = 0; j < tokens.Length; j++)
					if (string.Equals(tokens[j], needle, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}

		/// <summary>Samples a small imported copy and proves whether every sampled RGBA value is identical.</summary>
		static bool IsSolidColor(Texture texture)
		{
			if (texture == null) return false;
			RenderTexture rt = null;
			Texture2D copy = null;
			var previous = RenderTexture.active;
			try
			{
				int w = Mathf.Clamp(texture.width, 1, 128);
				int h = Mathf.Clamp(texture.height, 1, 128);
				rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
				Graphics.Blit(texture, rt);
				RenderTexture.active = rt;
				copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
				copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				copy.Apply();
				var pixels = copy.GetPixels32();
				if (pixels.Length == 0) return false;
				var first = pixels[0];
				for (int i = 1; i < pixels.Length; i++)
				{
					var p = pixels[i];
					if (p.r != first.r || p.g != first.g || p.b != first.b || p.a != first.a) return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				RenderTexture.active = previous;
				if (rt != null) RenderTexture.ReleaseTemporary(rt);
				if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
			}
		}

		/// <summary>Blits a small copy of the texture to a readable RT and checks whether R, G and B always match.</summary>
		static bool IsGreyscale(Texture texture)
		{
			if (texture == null) return false;
			RenderTexture rt = null;
			Texture2D copy = null;
			var previous = RenderTexture.active;
			try
			{
				int w = Mathf.Clamp(texture.width, 1, 128);
				int h = Mathf.Clamp(texture.height, 1, 128);
				rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
				Graphics.Blit(texture, rt);
				RenderTexture.active = rt;
				copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
				copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				copy.Apply();

				var pixels = copy.GetPixels32();
				for (int i = 0; i < pixels.Length; i++)
				{
					var p = pixels[i];
					if (Mathf.Abs(p.r - p.g) > 2 || Mathf.Abs(p.g - p.b) > 2) return false;
				}
				return true;
			}
			catch
			{
				return false;
			}
			finally
			{
				RenderTexture.active = previous;
				if (rt != null) RenderTexture.ReleaseTemporary(rt);
				if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
			}
		}

		static TextureImporterFormat ChooseTextureFormat(string platform)
		{
			if (platform.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0 || platform.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0)
				return TextureImporterFormat.ASTC_6x6;
			return TextureImporterFormat.DXT5;
		}
	}
}
