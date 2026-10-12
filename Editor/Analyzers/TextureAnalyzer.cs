using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
		List<AtlasCandidate> m_AtlasCandidates;
		bool m_AnyAtlases;
		static HashSet<string> s_AtlasCovered;
		static List<string> s_AtlasFolders;
		static List<AtlasCandidate> s_AtlasCandidates;
		static bool s_AnyAtlases;
		static bool s_AtlasCoverageDirty = true;
		static bool s_TextureUsageDirty = true;
		static Dictionary<string, TextureUsageEvidence> s_TextureUsageEvidence = new Dictionary<string, TextureUsageEvidence>(StringComparer.OrdinalIgnoreCase);
		static Dictionary<string, string> s_MaterialFileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		[Flags]
		enum TextureUsageFlags
		{
			None = 0,
			NormalSlot = 1,
			LinearDataSlot = 2,
			OtherSlot = 4,
			UiShader = 8,
			NonUiShader = 16
		}

		sealed class TextureUsageEvidence
		{
			public TextureUsageFlags Flags;
			public readonly HashSet<string> MaterialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		}

		sealed class AtlasCandidate
		{
			public string Path;
			public string Name;
			public string Hash;
			public readonly HashSet<string> PackablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			public readonly List<string> PackableFolders = new List<string>();
		}

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
			public bool automaticCompressionCrunchCompatible;
			public bool streamingMipmapsActive;
			public int globalTextureMipmapLimit;
			public bool constantPngEvidenceEvaluated;
			public bool constantPngSourceSupported;
			public string constantPngHash = string.Empty;
			public Color32 constantPngColor;
			public bool constantPngHasAlpha;
			public int constantPngSourceWidth;
			public int constantPngSourceHeight;
		}

		static TextureAnalyzer()
		{
			try { EditorApplication.projectChanged += InvalidateAtlasCoverage; } catch { }
			Rules.Register(id: "TEX001", title: "Texture larger than the size budget for the target platform", category: AssetCategory.Texture,
							why: "Imported texture resolution directly affects GPU memory and bandwidth. The budget is a policy threshold for the selected optimizer platform profile, not proof that this particular asset is visually safe to reduce.",
							fix: "Automatically set only the selected target platform Max Size to the profile budget; preserve Default settings and verify the exact result.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX002", title: "Texture imports uncompressed for the target platform", category: AssetCategory.Texture,
							why: "An uncompressed target texture can consume substantially more GPU memory and bandwidth than a compressed format. The correct compression mode depends on platform support, alpha, HDR/data usage and visual quality requirements.",
							fix: "Use platform Automatic format with standard/Normal compression for eligible color textures; preserve data, atlas, HDR and ambiguous-role exceptions.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX003", title: "Crunch compression / quality differs from the project policy", category: AssetCategory.Texture,
							why: "Crunch is an additional disk/download compression stage for supported compressed texture formats. It does not reduce the GPU memory footprint and increases import/compression time, so this is a build-size policy with a configurable compression-quality target.",
							fix: "Apply the configured Crunch quality only when the selected target and project policy still prove a supported Crunch-compatible compressed format.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
								recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
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
							fix: "Disable mipmaps only for GUI/cursor assets in the deterministic fixed-size common case; sprite textures with unresolved world-space use are skipped.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX006", title: "Mip maps missing on a world-space texture", category: AssetCategory.Texture,
							why: "Mipmaps improve minification quality and can reduce aliasing and texture-cache pressure on surfaces viewed at varying distances. They are not automatically required for every texture.",
							fix: "Enable mipmaps only for large 2D textures with known non-UI material use and no conflicting UI material evidence; unknown-use assets are skipped.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX007", title: "Alpha source configured when the source has no alpha", category: AssetCategory.Texture,
							why: "The importer is configured to obtain alpha even though the source image reports no alpha channel. Alpha can still be intentionally generated from another source, so the source format alone does not prove that disabling the setting is safe.",
							fix: "Set Alpha Source to None only when Unity confirms no source alpha and the current setting is FromInput; preserve grayscale-generated alpha.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX008", title: "Texture uses a filename normal-map hint but is imported as Default", category: AssetCategory.Texture,
							why: "Filename conventions can identify likely normal maps, but naming alone cannot prove that the pixels are normal-map data. Changing Texture Type changes import processing and can alter the texture result.",
							fix: "Import as Normal Map only when a normal-map filename hint is corroborated by exclusive recognized normal-slot usage in known materials.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX009", title: "Mipmap streaming disabled on a large texture", category: AssetCategory.Texture,
							why: "Per-texture mipmap streaming allows Unity to load only the mip levels needed for the current cameras. The project quality level must also have mipmap streaming enabled, so this finding is advisory.",
							fix: "Enable per-texture mipmap streaming only when project streaming is active, mipmaps are enabled, the texture is large and known non-UI use is indicated.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: true
							);

			Rules.Register(id: "TEX010", title: "Anisotropic filtering level is high", category: AssetCategory.Texture,
							why: "Higher anisotropic levels can improve texture clarity at grazing angles but increase sampling work. A high value is not inherently wrong and is often appropriate for roads, floors and terrain.",
							fix: "Clamp anisotropic filtering down to the configured project maximum, preserving all other importer settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX011", title: "Mobile target override uses an uncompressed format", category: AssetCategory.Texture,
							why: "A mobile target override that explicitly selects an uncompressed format can consume substantially more memory and bandwidth than the platform's supported compressed formats. The correct format depends on device compatibility and the texture's content.",
							fix: "Use platform Automatic format with standard/Normal compression for eligible mobile color textures whose explicit override is uncompressed.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
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
							fix: "Disable sRGB only when data-name evidence is corroborated by exclusive recognized linear-data material-slot usage.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX014", title: "Colour texture contains only greyscale data", category: AssetCategory.Texture,
							why: "If RGB channels contain identical information, storing three channels can be wasteful. Repacking into a single-channel texture or combining masks can reduce storage, but that requires matching shader/material changes.",
							fix: "Review repacking or channel-packing with the material/shader that consumes the texture.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX015", title: "Filtering settings are inefficient for a texture without mip maps", category: AssetCategory.Texture,
							why: "Trilinear filtering depends on mip levels, while anisotropic filtering is primarily useful with mipmapped textures. The importer settings can therefore be inconsistent for textures intentionally imported without mipmaps.",
							fix: "Change only Trilinear to Bilinear on non-mipmapped textures with anisotropy already at 0 or 1; preserve anisotropy and all other settings.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "TEX016", title: "Sprite is not packed into a Sprite Atlas", category: AssetCategory.Texture,
							why: "Sprite Atlas packing can reduce texture switches and improve batching, but runtime-generated sprites, special addressing, atlas filtering or UI workflows may intentionally keep sprites separate.",
							fix: "Review Fix can add the sprite to an explicitly selected existing included atlas; creating a new atlas or guessing a destination is not automated.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
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

			Rules.Register(id: "PAA001", title: "Solid-color texture is larger than 1x1", category: AssetCategory.Texture,
							why: "A texture that contains one solid color can be represented by a 1x1 texture, eliminating needless pixels and mip levels. This is a deep-analysis heuristic because decoding every texture is expensive.",
							fix: "Rewrite only metadata-free 8-bit RGB/RGBA PNGs after full-resolution exact pixel equality is proven, with an explicit source backup and post-import verification.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "PAA002", title: "Texture Atlas has too much empty space", category: AssetCategory.Texture,
							why: "Project Auditor measures transparent pixels in Texture2D content and reports assets with excessive empty area because empty pixels still consume texture memory and storage.",
							fix: "Re-author or crop the texture, or reorganize the atlas/source layout to reduce unused transparent area. Project Optimizer does not rewrite texture pixels automatically.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "TEX018", title: "High-quality texture compression on a large texture", category: AssetCategory.Texture,
							why: "High Quality compression can spend extra memory or storage on large textures for a visual-quality benefit. Standard/Normal compression is a practical default for common color textures; an occasional visual regression can be corrected manually on the affected asset.",
							fix: "Automatically switch eligible large high-quality target settings to Automatic standard/Normal compression; preserve data, atlas, HDR and unsupported-Crunch exceptions.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
		}

		static void InvalidateAtlasCoverage()
		{
			s_AtlasCoverageDirty = true;
			s_TextureUsageDirty = true;
		}

		public void BeginScan(ScanContext ctx)
		{
			if (s_TextureUsageDirty || s_TextureUsageEvidence == null)
				BuildTextureUsageEvidence();

			if (!s_AtlasCoverageDirty && s_AtlasCovered != null)
			{
				m_AtlasCovered = s_AtlasCovered;
				m_AtlasFolders = s_AtlasFolders;
				m_AtlasCandidates = s_AtlasCandidates;
				m_AnyAtlases = s_AnyAtlases;
				return;
			}

			m_AtlasCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			m_AtlasFolders = new List<string>();
			m_AtlasCandidates = new List<AtlasCandidate>();
			m_AnyAtlases = false;

			var visitedAtlases = new HashSet<int>();
			foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
			{
				var path = AssetDatabase.GUIDToAssetPath(guid);
				var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
				if (atlas == null || !SpriteAtlasExtensions.IsIncludeInBuild(atlas)) continue;

				m_AnyAtlases = true;
				var candidate = CaptureAtlasCandidate(path, atlas);
				if (candidate != null) m_AtlasCandidates.Add(candidate);
				RegisterAtlasPackables(atlas, visitedAtlases, requireIncludedInBuild: true);
			}
			s_AtlasCovered = m_AtlasCovered;
			s_AtlasFolders = m_AtlasFolders;
			s_AtlasCandidates = m_AtlasCandidates;
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

			// CPU texture decoding and source-file inspection happen on the main thread here. The
			// background analyzer consumes only captured primitive/source evidence fields.
			CaptureConstantPngEvidence(assetPath, facts, settings);

			// These values describe current project/platform policy rather than the imported texture,
			// so refresh them even when the measurement itself came from the persistent cache.
			facts.targetFormatDescription = facts.targetOverridden
				? facts.targetFormat.ToString()
				: GetInheritedTargetFormatDescription(platform, facts.targetCompression, settings.Profile.IsMobileClass);
			facts.crunchCompatible = IsCrunchCompatibleTarget(platform, facts.targetOverridden, facts.targetFormat, facts.targetCompression);
			facts.automaticCompressionCrunchCompatible = IsCrunchCompatibleTarget(platform, true, TextureImporterFormat.Automatic, TextureImporterCompression.Compressed);
			facts.streamingMipmapsActive = QualitySettings.streamingMipmapsActive;
			facts.globalTextureMipmapLimit = QualitySettings.globalTextureMipmapLimit;
			if (mainContext.Cache != null) mainContext.Cache.Set(cacheKey, facts, dependencyHash);
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
			}
			CaptureConstantPngEvidence(assetPath, facts, settings);
			facts.targetFormatDescription = facts.targetOverridden
				? facts.targetFormat.ToString()
				: GetInheritedTargetFormatDescription(platform, facts.targetCompression, settings.Profile.IsMobileClass);
			facts.crunchCompatible = IsCrunchCompatibleTarget(platform, facts.targetOverridden, facts.targetFormat, facts.targetCompression);
			facts.automaticCompressionCrunchCompatible = IsCrunchCompatibleTarget(platform, true, TextureImporterFormat.Automatic, TextureImporterCompression.Compressed);
			facts.streamingMipmapsActive = QualitySettings.streamingMipmapsActive;
			facts.globalTextureMipmapLimit = QualitySettings.globalTextureMipmapLimit;
			ctx.Cache?.Set(cacheKey, facts, dependencyHash);
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

			// TEX001 - safe target-only Max Size reduction. Do not alter Default settings.
			using (RuleTiming.Begin("TEX001", entry.Path))
				if (!isAtlasPackedSprite)
				{
					int budget = isUi ? settings.maxUiTextureSize : settings.maxTextureSize;
					int effectiveMax = targetOverridden ? facts.targetMaxTextureSize : facts.maxTextureSize;
					entry.Set("defaultMaxSize", (long)facts.maxTextureSize);
					entry.Set("targetMaxSize", (long)effectiveMax);
					if (largest > budget)
					{
						int target = Mathf.Max(32, Mathf.Min(effectiveMax, budget));
						if (target < effectiveMax && target < largest)
						{
							long savings = EstimateResolutionSavings(entry.RuntimeSize, width, height, target);
						var issue = entry.AddIssue("TEX001", width + " x " + height + "; " + platform + " Max Size " + effectiveMax,
							"Set " + platform + " Max Size to " + target, savings,
							c => ApplyTargetMaxSize(c, platform, settings, isUi, budget, target, targetOverridden, effectiveMax, facts.maxTextureSize),
							detail: "Applies only the selected platform's Max Size. Default Max Size is preserved; estimated savings are approximate and depend on the target format.",
							basis: savings > 0 ? SavingsBasis.Memory : SavingsBasis.Unknown,
							verify: c => VerifyTargetMaxSize(c, platform, target, facts.maxTextureSize));
							if (issue != null) issue.Note("Profile budget: " + budget + " for " + settings.Profile.DisplayName + ".", "Target");
						}
					}
				}

			// TEX002 - make the standard Automatic-compression action the deterministic default.
			bool targetExplicitUncompressedFormat = targetOverridden && TextureFormatUtility.IsUncompressed(targetFormat);
			bool targetExplicitMobileUncompressedOverride = settings.Profile.IsMobileClass
				&& targetOverridden && targetCompression != TextureImporterCompression.Uncompressed
				&& TextureFormatUtility.IsUncompressed(targetFormat) && !isAtlasPackedSprite && !isDataTexture;
			bool targetActuallyUncompressed = targetCompression == TextureImporterCompression.Uncompressed
				|| (targetExplicitUncompressedFormat && !settings.Profile.IsMobileClass);
			using (RuleTiming.Begin("TEX002", entry.Path))
				if (!isDataTexture && !isAtlasPackedSprite && IsCompressionCommonCase(assetPath, facts)
					&& targetActuallyUncompressed && !targetExplicitMobileUncompressedOverride
					&& (!targetCrunch || facts.automaticCompressionCrunchCompatible))
				{
					var issue = entry.AddIssue("TEX002", "Target " + platform + ": Uncompressed (" + targetFormatDescription + ")",
						"Use Automatic / standard compression", 0,
						c => ApplyAutomaticCompression(c, platform, settings, targetOverridden, targetCompression, targetFormat, targetCrunch, facts.textureCompression, facts.crunchedCompression),
						detail: "Sets only the selected target to Automatic format and standard/Normal compression. HDR, data and other ambiguous sources are excluded.",
						verify: c => VerifyAutomaticCompression(c, platform, targetCrunch, facts.textureCompression, facts.crunchedCompression));
					if (issue != null) issue.Note("Target format: " + targetFormatDescription + "; override: " + (targetOverridden ? "Yes" : "No"), "Target");
				}

			// TEX003 - Crunch is enabled only while the target remains compatible and policy still requests it.
			using (RuleTiming.Begin("TEX003", entry.Path))
			{
				int crunchQuality = Mathf.Clamp(settings.crunchQuality, 0, 100);
				if (settings.preferCrunch && !isAtlasPackedSprite && !isDataTexture
					&& targetCompression != TextureImporterCompression.Uncompressed && facts.crunchCompatible
					&& (!targetCrunch || targetCompressionQuality != crunchQuality))
				{
					string current = targetCrunch ? "Crunch on (quality " + targetCompressionQuality + ")" : "Crunch off";
					var issue = entry.AddIssue("TEX003", "Target " + platform + ": " + current + " (" + targetFormatDescription + ")",
						"Enable Crunch (quality " + crunchQuality + ")", 0,
						c => ApplyCrunch(c, platform, settings, targetOverridden, targetCompression, targetFormat, targetCrunch, targetCompressionQuality, facts.textureCompression, facts.crunchedCompression, crunchQuality),
						detail: "The captured target was Crunch-compatible and the project preference requests Crunch. The action rechecks the effective target and preference before mutation.",
						verify: c => VerifyCrunch(c, platform, settings, crunchQuality));
					if (issue != null) issue.Note("Configured Crunch quality: " + crunchQuality + ".", "Compression Quality");
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

			// TEX005 - fixed-size GUI/cursor assets only. Sprite mipmap usage is not inferred from importer type.
			using (RuleTiming.Begin("TEX005", entry.Path))
				if (facts.mipmapEnabled && !isAtlasPackedSprite
					&& (facts.textureType == TextureImporterType.GUI || facts.textureType == TextureImporterType.Cursor))
				{
					var issue = entry.AddIssue("TEX005", "Mip maps generated for " + facts.textureType,
						"Disable Generate Mip Maps", 0,
						c => ApplyMipmapState(c, expectedType: facts.textureType, expectedMipmap: true, targetMipmap: false),
						detail: "Only fixed-size GUI/cursor types enter the automatic case. Sprite textures are skipped because importer type alone cannot distinguish UI from world-space use.",
						verify: c => VerifyMipmapState(c, facts.textureType, false));
				}

			// TEX006 - only enable mipmaps when the current project has a known non-UI material consumer.
			using (RuleTiming.Begin("TEX006", entry.Path))
				if (!facts.mipmapEnabled && !isAtlasPackedSprite && !isUi
					&& (facts.textureType == TextureImporterType.Default || facts.textureType == TextureImporterType.NormalMap)
					&& facts.textureShape == TextureImporterShape.Texture2D && !isDataTexture
					&& !MatchesAny(lowerPath, k_DataHints) && largest >= 128
					&& HasVerifiedNonUiMaterialUsage(assetPath))
				{
					var issue = entry.AddIssue("TEX006", "No mip maps", "Enable Generate Mip Maps", 0,
						c => ApplyMipmapState(c, expectedType: facts.textureType, expectedMipmap: false, targetMipmap: true, requiredNonUiMaterialPath: assetPath),
						detail: "The texture has a known non-UI material-slot consumer. Assets without stable material-role evidence and mixed UI/non-UI consumers are skipped.",
						verify: c => VerifyMipmapState(c, facts.textureType, true));
				}

			// TEX007 - FromInput is the only alpha source that can be safely cleared from a no-alpha source.
			using (RuleTiming.Begin("TEX007", entry.Path))
				if (facts.sourceHasAlphaKnown && !facts.sourceHasAlpha
					&& facts.alphaSource == TextureImporterAlphaSource.FromInput
					&& facts.textureType != TextureImporterType.NormalMap)
				{
					var issue = entry.AddIssue("TEX007", "Alpha Source: FromInput with no source alpha", "Set Alpha Source to None", 0,
						c => ApplyAlphaSourceNone(c, TextureImporterAlphaSource.FromInput),
						detail: "The source is known to contain no alpha and the importer was using FromInput. Grayscale-generated alpha and unknown source-alpha cases are excluded.",
						verify: c => VerifyAlphaSourceNone(c));
				}

			// TEX008 - filename hints are insufficient without exclusive recognized normal-slot usage.
			using (RuleTiming.Begin("TEX008", entry.Path))
				if (facts.textureType == TextureImporterType.Default && facts.textureShape == TextureImporterShape.Texture2D
					&& MatchesAny(lowerPath, k_NormalHints) && HasExclusiveNormalMaterialUsage(assetPath))
				{
					var issue = entry.AddIssue("TEX008", "Texture Type: Default; normal hint and exclusive normal-slot use",
						"Import as Normal Map", 0,
						c => ApplyNormalMapType(c, assetPath),
						detail: "The filename hint is corroborated by known normal/bump material slots, with no conflicting known material slot use. Unknown/dynamic consumers are not inferred.",
						verify: c => { var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter; return ti != null && ti.textureType == TextureImporterType.NormalMap; });
				}

			// TEX009 - only auto-enable per-texture streaming while the global streaming system is active.
			using (RuleTiming.Begin("TEX009", entry.Path))
				if (facts.streamingMipmapsActive && facts.mipmapEnabled && !facts.streamingMipmaps
					&& !isAtlasPackedSprite && !isDataTexture && facts.textureShape == TextureImporterShape.Texture2D
					&& !MatchesAny(lowerPath, k_DataHints) && largest >= settings.streamingMipmapMinSize && !isUi
					&& HasVerifiedNonUiMaterialUsage(assetPath))
				{
					var issue = entry.AddIssue("TEX009", "Streaming Mipmaps off", "Enable Streaming Mipmaps", 0,
						c => ApplyStreamingMipmaps(c, false, assetPath),
						detail: "Global mipmap streaming is active and known non-UI material use is present. The fix rechecks global streaming and the original importer state.",
						verify: c => VerifyStreamingMipmaps(c, true));
					if (issue != null) issue.Note("Suggested threshold: " + settings.streamingMipmapMinSize + " px; texture maximum dimension: " + largest + " px.", "Threshold");
				}

			// TEX010 - clamp down to the configured project maximum, without changing unrelated settings.
			using (RuleTiming.Begin("TEX010", entry.Path))
			{
				int targetAniso = ClampAnisotropyTarget(settings.maxAnisoLevel);
				if (ShouldClampAnisotropy(facts.mipmapEnabled, !isAtlasPackedSprite, largest, facts.anisoLevel, targetAniso))
				{
					var issue = entry.AddIssue("TEX010", "Aniso " + facts.anisoLevel,
						"Clamp Anisotropic Filtering to " + targetAniso, 0,
						c => ApplyAnisotropyLimit(c, facts.anisoLevel, targetAniso, settings),
						detail: "The action clamps the per-texture anisotropic filtering value to the configured project maximum and leaves other importer fields unchanged.",
						verify: c => VerifyAnisotropyLimit(c, targetAniso, settings));
				}
			}

			// TEX011 - mobile-specific uncompressed override, distinct from TEX002.
			using (RuleTiming.Begin("TEX011", entry.Path))
				if (targetExplicitMobileUncompressedOverride && IsCompressionCommonCase(assetPath, facts)
					&& (!targetCrunch || facts.automaticCompressionCrunchCompatible))
				{
					var issue = entry.AddIssue("TEX011", platform + " override: " + targetFormat,
						"Use Automatic / standard compression", 0,
						c => ApplyAutomaticCompression(c, platform, settings, targetOverridden, targetCompression, targetFormat, targetCrunch, facts.textureCompression, facts.crunchedCompression),
						detail: "Sets only this mobile target to Automatic format with standard/Normal compression. It does not force a single ASTC/ETC format, and skips ambiguous/data/HDR cases.",
						verify: c => VerifyAutomaticCompression(c, platform, targetCrunch, facts.textureCompression, facts.crunchedCompression));
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

			// TEX013 - require both data filename evidence and exclusive known linear-data slot use.
			using (RuleTiming.Begin("TEX013", entry.Path))
				if (facts.sRGBTexture && facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D && MatchesAny(lowerPath, k_DataHints)
					&& HasExclusiveLinearDataMaterialUsage(assetPath))
				{
					var issue = entry.AddIssue("TEX013", "sRGB enabled on a confirmed material data-slot texture", "Disable sRGB (Color Texture)", 0,
						c => ApplySrgbLinear(c, true),
						detail: "The source has a data-map filename hint and all known material-slot uses are recognized linear-data slots. Conflicting, UI, and unknown-role cases are skipped.",
						verify: c => VerifySrgbLinear(c));
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

			using (RuleTiming.Begin("PAA002", entry.Path))
				if (settings.deepTextureScan && facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D && facts.emptyPixelsPercent >= 0f
					&& facts.emptyPixelsPercent > settings.textureAtlasEmptySpaceWarnPercent && !isAtlasPackedSprite)
				{
					entry.Set("emptySpace", facts.emptyPixelsPercent.ToString("0.##") + "%");
					entry.AddIssue("PAA002", facts.emptyPixelsPercent.ToString("0.##") + "% empty pixels", "Review texture/atlas layout",
						detail: "The measured transparent-pixel percentage is diagnostic only. Cropping/repacking arbitrary textures can change UVs, borders and shader/material assumptions.");
				}

			using (RuleTiming.Begin("PAA001", entry.Path))
				if (settings.deepTextureScan && facts.textureType == TextureImporterType.Default
					&& facts.textureShape == TextureImporterShape.Texture2D && facts.solidColor && width * (long)height > 1
					&& facts.constantPngSourceSupported)
				{
					int sourceWidth = facts.constantPngSourceWidth;
					int sourceHeight = facts.constantPngSourceHeight;
					if (sourceWidth > 1 || sourceHeight > 1)
					{
						string pngHash = facts.constantPngHash;
						Color32 constantColor = facts.constantPngColor;
						bool pngHasAlpha = facts.constantPngHasAlpha;
						var issue = entry.AddIssue("PAA001", sourceWidth + " x " + sourceHeight + "; every decoded source pixel is identical",
							"Replace source PNG with an equivalent 1 x 1 PNG", 0,
							c => RewriteConstantPng(c, pngHash, constantColor, pngHasAlpha),
							detail: "Automatic source rewrite is restricted to metadata-free, non-interlaced, 8-bit RGB/RGBA PNGs that passed exhaustive full-resolution pixel comparison on the main thread. The original source is backed up before mutation; unsupported formats receive no action.",
							basis: SavingsBasis.Unknown,
							verify: c => VerifyConstantPngRewrite(c, constantColor, pngHasAlpha));
					}
				}

			// TEX015 - Trilinear without mipmaps has no mip levels to interpolate; change only filter mode.
			using (RuleTiming.Begin("TEX015", entry.Path))
				if (!facts.mipmapEnabled && !isAtlasPackedSprite && facts.filterMode == FilterMode.Trilinear && facts.anisoLevel <= 1)
				{
					var issue = entry.AddIssue("TEX015", "Trilinear filtering without mipmaps", "Set Bilinear filtering", 0,
						c => ApplyBilinearFilter(c, FilterMode.Trilinear, facts.anisoLevel),
						detail: "Changes Trilinear to Bilinear only. Anisotropy and all other importer settings are preserved. High-anisotropy or non-Trilinear cases are skipped.",
						verify: c => VerifyBilinearFilter(c, facts.anisoLevel));
				}

			// TEX016 - atlas destination remains a human choice; expose existing included atlases only.
			using (RuleTiming.Begin("TEX016", entry.Path))
				if (m_AnyAtlases && facts.textureType == TextureImporterType.Sprite && !IsAtlased(assetPath))
				{
					var options = new List<FixOption>();
					if (m_AtlasCandidates != null)
						for (int i = 0; i < m_AtlasCandidates.Count; i++)
						{
							AtlasCandidate candidate = m_AtlasCandidates[i];
							if (candidate == null || string.IsNullOrEmpty(candidate.Hash) || IsPackableAlreadyInAtlas(candidate, assetPath)) continue;
							string capturedAtlasPath = candidate.Path;
							string capturedAtlasHash = candidate.Hash;
							string capturedAtlasName = candidate.Name;
							options.Add(new FixOption
							{
								ActionId = "tex016_add_to_atlas_" + StablePathToken(capturedAtlasPath),
								Label = "Add to Sprite Atlas: " + capturedAtlasName,
								ResultDescription = "Adds this sprite texture to the selected existing included Sprite Atlas.",
								Recommended = false,
								Warning = "Choose an atlas whose runtime usage, filtering, platform settings, and addressing strategy are appropriate. No new atlas is created.",
								Risk = FixRisk.Medium,
								Apply = c => AddTextureToSelectedAtlas(c, capturedAtlasPath, capturedAtlasHash),
								Verify = c => VerifyTextureInAtlas(c.AssetPath, capturedAtlasPath)
							});
						}

					if (options.Count > 0)
					{
						var issue = entry.AddIssue("TEX016", "Loose sprite", "Review destination atlas",
							detail: "Atlas selection is a human decision. Only existing included atlases are offered, and each action backs up the selected atlas before modifying membership.",
							options: options);
						if (issue != null) issue.Note("Known included atlas roots/folders are considered during destination selection.", "Atlas");
					}
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

			// TEX018 - standard/Normal compression is the default for eligible large color textures.
			using (RuleTiming.Begin("TEX018", entry.Path))
				if (ShouldAutoDowngradeHighQualityCompression(assetPath, facts, targetCompression, largest,
					isDataTexture, isAtlasPackedSprite, targetCrunch, facts.automaticCompressionCrunchCompatible))
				{
					var issue = entry.AddIssue("TEX018", "Target " + platform + ": High Quality compression",
						"Use Automatic / standard (Normal) compression", 0,
						c => ApplyAutomaticCompression(c, platform, settings, targetOverridden, targetCompression, targetFormat, targetCrunch, facts.textureCompression, facts.crunchedCompression),
						detail: "Switches only the selected target to Automatic format with Unity's standard Compressed tier (normal/medium-equivalent), preserving other importer settings. For eligible large color textures, follows the project default of standard/Normal quality; users can manually restore High Quality on the rare asset that needs it.",
						verify: c => VerifyAutomaticCompression(c, platform, targetCrunch, facts.textureCompression, facts.crunchedCompression));
					if (issue != null) issue.Note("Target format policy: " + targetFormatDescription + "; override: " + (targetOverridden ? "Yes" : "No") + ".", "Target");
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

		static AtlasCandidate CaptureAtlasCandidate(string atlasPath, SpriteAtlas atlas)
		{
			if (string.IsNullOrEmpty(atlasPath) || atlas == null) return null;
			string hash = SafeMd5File(AssetPathUtility.ToAbsolutePath(atlasPath));
			if (string.IsNullOrEmpty(hash)) return null;
			var candidate = new AtlasCandidate { Path = atlasPath, Name = atlas.name, Hash = hash };
			foreach (var packable in SpriteAtlasExtensions.GetPackables(atlas))
			{
				if (packable == null) continue;
				string path = AssetDatabase.GetAssetPath(packable);
				if (string.IsNullOrEmpty(path)) continue;
				if (AssetDatabase.IsValidFolder(path)) candidate.PackableFolders.Add(path.TrimEnd('/') + "/");
				else candidate.PackablePaths.Add(path);
			}
			return candidate;
		}

		static bool IsPackableAlreadyInAtlas(AtlasCandidate candidate, string assetPath)
		{
			if (candidate == null || string.IsNullOrEmpty(assetPath)) return false;
			if (candidate.PackablePaths.Contains(assetPath)) return true;
			for (int i = 0; i < candidate.PackableFolders.Count; i++)
				if (assetPath.StartsWith(candidate.PackableFolders[i], StringComparison.OrdinalIgnoreCase)) return true;
			return false;
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

        static bool IsPackableAlreadyInAtlas(SpriteAtlas atlas, string assetPath)
        {
            if (atlas == null || string.IsNullOrEmpty(assetPath)) return false;
            foreach (var packable in SpriteAtlasExtensions.GetPackables(atlas))
            {
                if (packable == null) continue;
                string path = AssetDatabase.GetAssetPath(packable);
                if (string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path)
                    && assetPath.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string StablePathToken(string path)
        {
            if (string.IsNullOrEmpty(path)) return "atlas";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path));
                return BitConverter.ToString(bytes, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static void AddTextureToSelectedAtlas(FixContext c, string atlasPath, string expectedAtlasHash)
        {
            if (c == null || !c.BackupEnabled || c.TryBackupExtra == null)
                throw new InvalidOperationException("Adding a sprite to an atlas requires backups to be enabled.");
            var ti = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            if (ti == null || ti.textureType != TextureImporterType.Sprite || atlas == null || !SpriteAtlasExtensions.IsIncludeInBuild(atlas))
                throw new InvalidOperationException("The sprite or selected included atlas is no longer valid.");
            if (IsPackableAlreadyInAtlas(atlas, c.AssetPath)) throw new InvalidOperationException("The selected atlas already contains this sprite texture.");
            string currentHash = SafeMd5File(AssetPathUtility.ToAbsolutePath(atlasPath));
            if (string.IsNullOrEmpty(currentHash) || !string.Equals(currentHash, expectedAtlasHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected atlas changed since analysis; rescan before applying this Review Fix.");
            if (!UnityEditorUtility.CanEditPath(atlasPath, out string reason))
                throw new InvalidOperationException("The selected atlas is not editable: " + reason);
            if (!c.TryBackupExtra(atlasPath)) throw new InvalidOperationException("Could not back up the selected atlas; nothing was changed.");
            SpriteAtlasExtensions.Add(atlas, new UnityEngine.Object[] { AssetDatabase.LoadMainAssetAtPath(c.AssetPath) });
            EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssetIfDirty(atlas);
            c.NeedsReimport = true;
        }

        static bool VerifyTextureInAtlas(string assetPath, string atlasPath)
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
            return atlas != null && SpriteAtlasExtensions.IsIncludeInBuild(atlas) && IsPackableAlreadyInAtlas(atlas, assetPath);
        }

        // TextureAnalyzer-owned common-case evidence and action helpers. These remain in this
        // analyzer so the isolated session does not modify shared fix infrastructure.
        static void BuildTextureUsageEvidence()
        {
            var evidence = new Dictionary<string, TextureUsageEvidence>(StringComparer.OrdinalIgnoreCase);
            var materialHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] guids;
            try { guids = AssetDatabase.FindAssets("t:Material"); }
            catch { s_TextureUsageDirty = true; return; }

            for (int i = 0; i < guids.Length; i++)
            {
                string materialPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(materialPath)) continue;
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null || material.shader == null || EditorUtility.IsDirty(material))
                {
                    AddUnknownMaterialDependencies(evidence, materialPath);
                    continue; // uncertain shader/unsaved state must block auto-classification.
                }

                string absolute = AssetPathUtility.ToAbsolutePath(materialPath);
                string materialHash = SafeMd5File(absolute);
                if (string.IsNullOrEmpty(materialHash))
                {
                    AddUnknownMaterialDependencies(evidence, materialPath);
                    continue;
                }
                materialHashes[materialPath] = materialHash;

                string shaderName = material.shader.name ?? string.Empty;
                bool uiShader = shaderName.StartsWith("UI/", StringComparison.OrdinalIgnoreCase)
                    || shaderName.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                    || shaderName.IndexOf("UI Default", StringComparison.OrdinalIgnoreCase) >= 0;
                try
                {
                    int count = ShaderUtil.GetPropertyCount(material.shader);
                    for (int p = 0; p < count; p++)
                    {
                        if (ShaderUtil.GetPropertyType(material.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                        string propertyName = ShaderUtil.GetPropertyName(material.shader, p);
                        Texture texture = material.GetTexture(propertyName);
                        if (texture == null) continue;
                        string texturePath = AssetDatabase.GetAssetPath(texture);
                        if (string.IsNullOrEmpty(texturePath)) continue;

                        if (!evidence.TryGetValue(texturePath, out var usage))
                        {
                            usage = new TextureUsageEvidence();
                            evidence.Add(texturePath, usage);
                        }
                        usage.MaterialPaths.Add(materialPath);
                        usage.Flags |= uiShader ? TextureUsageFlags.UiShader : TextureUsageFlags.NonUiShader;
                        if (IsNormalMapSlot(propertyName)) usage.Flags |= TextureUsageFlags.NormalSlot;
                        else if (IsLinearDataSlot(propertyName)) usage.Flags |= TextureUsageFlags.LinearDataSlot;
                        else usage.Flags |= TextureUsageFlags.OtherSlot;
                    }
                }
                catch
                {
                    // Unknown material schema must conflict with any positive slot evidence for its dependencies.
                    AddUnknownMaterialDependencies(evidence, materialPath);
                }
            }

            s_TextureUsageEvidence = evidence;
            s_MaterialFileHashes = materialHashes;
            s_TextureUsageDirty = false;
        }

        static void AddUnknownMaterialDependencies(Dictionary<string, TextureUsageEvidence> evidence, string materialPath)
        {
            try
            {
                string[] dependencies = AssetDatabase.GetDependencies(materialPath, false);
                for (int i = 0; i < dependencies.Length; i++)
                {
                    string path = dependencies[i];
                    string extension = Path.GetExtension(path ?? string.Empty);
                    if (Array.IndexOf(k_Extensions, extension.ToLowerInvariant()) < 0) continue;
                    if (!evidence.TryGetValue(path, out var usage))
                    {
                        usage = new TextureUsageEvidence();
                        evidence.Add(path, usage);
                    }
                    usage.Flags |= TextureUsageFlags.OtherSlot;
                    usage.MaterialPaths.Add(materialPath);
                }
            }
            catch { /* unresolved dependent material state remains absent and therefore fails closed */ }
        }

        static bool IsNormalMapSlot(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return false;
            return propertyName.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsLinearDataSlot(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return false;
            string value = propertyName.ToLowerInvariant();
            return value.Contains("occlusion") || value.Contains("metallic") || value.Contains("roughness")
                || value.Contains("smoothness") || value.Contains("height") || value.Contains("parallax")
                || value.Contains("displacement") || value.Contains("mask") || value.Contains("flow")
                || value.Contains("_data") || value.Contains("specular") || value.Contains("gloss");
        }

        static bool HasExclusiveNormalMaterialUsage(string assetPath, bool validateFiles = false) => HasExclusiveUsage(assetPath, TextureUsageFlags.NormalSlot, validateFiles);
        static bool HasExclusiveLinearDataMaterialUsage(string assetPath, bool validateFiles = false) => HasExclusiveUsage(assetPath, TextureUsageFlags.LinearDataSlot, validateFiles);
        static bool HasVerifiedNonUiMaterialUsage(string assetPath, bool validateFiles = false)
        {
            return TryGetStableMaterialUsage(assetPath, out var usage)
                && (usage.Flags & TextureUsageFlags.NonUiShader) != 0
                && (usage.Flags & TextureUsageFlags.UiShader) == 0
                && (!validateFiles || MaterialUsageFilesUnchanged(usage));
        }

        static bool HasExclusiveUsage(string assetPath, TextureUsageFlags wanted, bool validateFiles)
        {
            if (!TryGetStableMaterialUsage(assetPath, out var usage)) return false;
            const TextureUsageFlags conflicting = TextureUsageFlags.OtherSlot | TextureUsageFlags.UiShader;
            if ((usage.Flags & wanted) == 0 || (usage.Flags & conflicting) != 0) return false;
            if (wanted == TextureUsageFlags.NormalSlot && (usage.Flags & TextureUsageFlags.LinearDataSlot) != 0) return false;
            if (wanted == TextureUsageFlags.LinearDataSlot && (usage.Flags & TextureUsageFlags.NormalSlot) != 0) return false;
            return (usage.Flags & TextureUsageFlags.NonUiShader) != 0
                && (!validateFiles || MaterialUsageFilesUnchanged(usage));
        }

        static bool TryGetStableMaterialUsage(string assetPath, out TextureUsageEvidence usage)
        {
            usage = null;
            if (s_TextureUsageDirty || s_TextureUsageEvidence == null || string.IsNullOrEmpty(assetPath)) return false;
            return s_TextureUsageEvidence.TryGetValue(assetPath, out usage) && usage != null;
        }

        static bool MaterialUsageFilesUnchanged(TextureUsageEvidence usage)
        {
            if (usage == null || usage.MaterialPaths.Count == 0) return false;
            foreach (string materialPath in usage.MaterialPaths)
            {
                if (s_TextureUsageDirty) return false;
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null || EditorUtility.IsDirty(material)) return false;
                if (!s_MaterialFileHashes.TryGetValue(materialPath, out string expectedHash)) return false;
                string currentHash = SafeMd5File(AssetPathUtility.ToAbsolutePath(materialPath));
                if (string.IsNullOrEmpty(currentHash) || !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        static bool ShouldClampAnisotropy(bool mipmaps, bool notAtlas, int largest, int current, int target)
        {
            return mipmaps && notAtlas && largest >= 256 && target >= 0 && target <= 16 && current > target;
        }

        static bool ShouldAutoDowngradeHighQualityCompression(string assetPath, TextureFacts facts,
            TextureImporterCompression targetCompression, int largest, bool isDataTexture, bool isAtlasPackedSprite,
            bool targetCrunch, bool automaticCompressionCrunchCompatible)
        {
            return facts != null && targetCompression == TextureImporterCompression.CompressedHQ && largest >= 1024
                && !isDataTexture && !isAtlasPackedSprite && IsCompressionCommonCase(assetPath, facts)
                && (!targetCrunch || automaticCompressionCrunchCompatible);
        }

        static bool IsCompressionCommonCase(string assetPath, TextureFacts facts)
        {
            if (facts == null || facts.textureShape != TextureImporterShape.Texture2D) return false;
            string extension = Path.GetExtension(assetPath ?? string.Empty);
            if (extension.Equals(".exr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase)) return false;
            string format = facts.format ?? string.Empty;
            if (format.IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0
                || format.IndexOf("Half", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (facts.textureType == TextureImporterType.NormalMap || facts.textureType == TextureImporterType.SingleChannel
                || facts.textureType == TextureImporterType.Cookie || facts.textureType == TextureImporterType.Shadowmask
                || facts.textureType == TextureImporterType.Lightmap) return false;
            if (MatchesAny(assetPath.ToLowerInvariant(), k_DataHints) || MatchesAny(assetPath.ToLowerInvariant(), k_NormalHints)) return false;
            return true;
        }

        static void ApplyTargetMaxSize(FixContext c, string platform, OptimizerSettings settings, bool isUi, int expectedBudget,
            int target, bool expectedOverride, int expectedEffectiveMax, int expectedDefaultMax)
        {
            if (settings == null || !string.Equals(settings.PlatformString, platform, StringComparison.Ordinal)
                || (isUi ? settings.maxUiTextureSize : settings.maxTextureSize) != expectedBudget
                || target != Mathf.Max(32, Mathf.Min(expectedEffectiveMax, expectedBudget)))
                throw new InvalidOperationException("The selected platform or texture-size budget changed since analysis.");
            var ti = c?.Importer as TextureImporter;
            if (ti == null) throw new InvalidOperationException("Texture importer is unavailable.");
            var ps = GetTargetPlatformSettings(ti, platform);
            if (ps == null) throw new InvalidOperationException("Target platform settings are unavailable for " + platform + ".");
            int currentEffective = ps.overridden ? ps.maxTextureSize : ti.maxTextureSize;
            if (ps.overridden != expectedOverride || currentEffective != expectedEffectiveMax || ti.maxTextureSize != expectedDefaultMax)
                throw new InvalidOperationException("Texture settings changed since analysis; rescan before applying TEX001.");
            if (target <= 0 || target >= currentEffective) throw new InvalidOperationException("TEX001 target is not a strict reduction.");
            ps.name = platform; ps.overridden = true; ps.maxTextureSize = target;
            ti.SetPlatformTextureSettings(ps);
            c.NeedsReimport = true;
        }

        static bool VerifyTargetMaxSize(FixContext c, string platform, int target, int expectedDefaultMax)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            var ps = ti != null ? GetTargetPlatformSettings(ti, platform) : null;
            return ti != null && ps != null && ps.overridden && ps.maxTextureSize == target && ti.maxTextureSize == expectedDefaultMax;
        }

        static bool TargetStateMatches(TextureImporter ti, string platform, bool expectedOverride,
            TextureImporterCompression expectedCompression, TextureImporterFormat expectedFormat, bool expectedCrunch,
            TextureImporterCompression expectedDefaultCompression, bool expectedDefaultCrunch)
        {
            if (ti == null) return false;
            var ps = GetTargetPlatformSettings(ti, platform);
            if (ps == null || ps.overridden != expectedOverride) return false;
            if (!expectedOverride) return ti.textureCompression == expectedDefaultCompression && ti.crunchedCompression == expectedDefaultCrunch;
            return ps.textureCompression == expectedCompression && ps.format == expectedFormat && ps.crunchedCompression == expectedCrunch;
        }

        static void ApplyAutomaticCompression(FixContext c, string platform, OptimizerSettings settings, bool expectedOverride,
            TextureImporterCompression expectedCompression, TextureImporterFormat expectedFormat, bool expectedCrunch,
            TextureImporterCompression expectedDefaultCompression, bool expectedDefaultCrunch)
        {
            if (settings == null || !string.Equals(settings.PlatformString, platform, StringComparison.Ordinal))
                throw new InvalidOperationException("The selected target platform changed since analysis.");
            if (expectedCrunch && !IsCrunchCompatibleTarget(platform, true, TextureImporterFormat.Automatic, TextureImporterCompression.Compressed))
                throw new InvalidOperationException("Automatic compression cannot preserve Crunch under the selected platform's known format policy.");
            var ti = c?.Importer as TextureImporter;
            if (!TargetStateMatches(ti, platform, expectedOverride, expectedCompression, expectedFormat, expectedCrunch,
                expectedDefaultCompression, expectedDefaultCrunch))
                throw new InvalidOperationException("Texture target changed since analysis; rescan before applying compression.");
            if (c.Importer is TextureImporter current)
            {
                var ps = GetTargetPlatformSettings(current, platform);
                if (ps == null) throw new InvalidOperationException("Target platform settings unavailable for " + platform + ".");
                ps.name = platform; ps.overridden = true;
                ps.format = TextureImporterFormat.Automatic;
                ps.textureCompression = TextureImporterCompression.Compressed;
                ps.crunchedCompression = expectedCrunch;
                current.SetPlatformTextureSettings(ps);
                c.NeedsReimport = true;
            }
            else throw new InvalidOperationException("Texture importer is unavailable.");
        }

        static bool VerifyAutomaticCompression(FixContext c, string platform, bool expectedTargetCrunch,
            TextureImporterCompression expectedDefaultCompression, bool expectedDefaultCrunch)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            var ps = ti != null ? GetTargetPlatformSettings(ti, platform) : null;
            return ti != null && ps != null && ps.overridden && ps.format == TextureImporterFormat.Automatic
                && ps.textureCompression == TextureImporterCompression.Compressed && ps.crunchedCompression == expectedTargetCrunch
                && (!expectedTargetCrunch || IsCrunchCompatibleTarget(platform, true, ps.format, ps.textureCompression))
                && ti.textureCompression == expectedDefaultCompression && ti.crunchedCompression == expectedDefaultCrunch;
        }

        static void ApplyCrunch(FixContext c, string platform, OptimizerSettings settings, bool expectedOverride,
            TextureImporterCompression expectedCompression, TextureImporterFormat expectedFormat, bool expectedCrunch,
            int expectedQuality, TextureImporterCompression expectedDefaultCompression, bool expectedDefaultCrunch, int targetQuality)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || settings == null || !settings.preferCrunch || Mathf.Clamp(settings.crunchQuality, 0, 100) != targetQuality)
                throw new InvalidOperationException("Crunch preference changed since analysis.");
            if (!TargetStateMatches(ti, platform, expectedOverride, expectedCompression, expectedFormat, expectedCrunch,
                expectedDefaultCompression, expectedDefaultCrunch))
                throw new InvalidOperationException("Texture target changed since analysis; rescan before applying TEX003.");
            var ps = GetTargetPlatformSettings(ti, platform);
            int currentQuality = ps.overridden ? ps.compressionQuality : ti.compressionQuality;
            if (currentQuality != expectedQuality) throw new InvalidOperationException("Texture Crunch quality changed since analysis.");
            var effectiveCompression = ps.overridden ? ps.textureCompression : ti.textureCompression;
            if (!IsCrunchCompatibleTarget(platform, ps.overridden, ps.format, effectiveCompression))
                throw new InvalidOperationException("The current target is no longer Crunch-compatible.");
            ps.name = platform; ps.overridden = true; ps.crunchedCompression = true; ps.compressionQuality = targetQuality;
            ti.SetPlatformTextureSettings(ps);
            c.NeedsReimport = true;
        }

        static bool VerifyCrunch(FixContext c, string platform, OptimizerSettings settings, int targetQuality)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            var ps = ti != null ? GetTargetPlatformSettings(ti, platform) : null;
            return settings != null && settings.preferCrunch && Mathf.Clamp(settings.crunchQuality, 0, 100) == targetQuality
                && ps != null && ps.overridden && ps.crunchedCompression && ps.compressionQuality == targetQuality
                && IsCrunchCompatibleTarget(platform, ps.overridden, ps.format, ps.textureCompression);
        }

        static void ApplyMipmapState(FixContext c, TextureImporterType expectedType, bool expectedMipmap, bool targetMipmap, string requiredNonUiMaterialPath = null)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.textureType != expectedType || ti.mipmapEnabled != expectedMipmap
                || (!string.IsNullOrEmpty(requiredNonUiMaterialPath) && !HasVerifiedNonUiMaterialUsage(requiredNonUiMaterialPath, validateFiles: true)))
                throw new InvalidOperationException("Texture type or mipmap setting changed since analysis.");
            ti.mipmapEnabled = targetMipmap;
            c.NeedsReimport = true;
        }

        static bool VerifyMipmapState(FixContext c, TextureImporterType expectedType, bool targetMipmap)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            return ti != null && ti.textureType == expectedType && ti.mipmapEnabled == targetMipmap;
        }

        static void ApplyAlphaSourceNone(FixContext c, TextureImporterAlphaSource expectedSource)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.alphaSource != expectedSource || ti.textureType == TextureImporterType.NormalMap)
                throw new InvalidOperationException("Alpha-source settings changed since analysis.");
            try { if (ti.DoesSourceTextureHaveAlpha()) throw new InvalidOperationException("Source now has an alpha channel."); }
            catch (InvalidOperationException) { throw; }
            catch { throw new InvalidOperationException("Unity cannot verify source alpha; TEX007 is blocked."); }
            ti.alphaSource = TextureImporterAlphaSource.None;
            c.NeedsReimport = true;
        }

        static bool VerifyAlphaSourceNone(FixContext c)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            if (ti == null || ti.alphaSource != TextureImporterAlphaSource.None) return false;
            try { return !ti.DoesSourceTextureHaveAlpha(); } catch { return false; }
        }

        static void ApplyNormalMapType(FixContext c, string assetPath)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.textureType != TextureImporterType.Default || ti.textureShape != TextureImporterShape.Texture2D
                || s_TextureUsageDirty || !HasExclusiveNormalMaterialUsage(assetPath, validateFiles: true))
                throw new InvalidOperationException("Normal-map usage evidence is stale or no longer exclusive.");
            ti.textureType = TextureImporterType.NormalMap;
            c.NeedsReimport = true;
        }

        static void ApplyStreamingMipmaps(FixContext c, bool expectedCurrent, string requiredNonUiMaterialPath)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.streamingMipmaps != expectedCurrent || !QualitySettings.streamingMipmapsActive || !ti.mipmapEnabled
                || !HasVerifiedNonUiMaterialUsage(requiredNonUiMaterialPath, validateFiles: true))
                throw new InvalidOperationException("Streaming preconditions no longer hold.");
            ti.streamingMipmaps = true;
            c.NeedsReimport = true;
        }

        static bool VerifyStreamingMipmaps(FixContext c, bool target)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            return ti != null && ti.streamingMipmaps == target && QualitySettings.streamingMipmapsActive;
        }

        internal static int ClampAnisotropyTarget(int maximum) => Mathf.Clamp(maximum, 0, 16);

        static void ApplyAnisotropyLimit(FixContext c, int expectedCurrent, int target, OptimizerSettings settings)
        {
            var ti = c?.Importer as TextureImporter;
            if (settings == null || target != ClampAnisotropyTarget(settings.maxAnisoLevel)
                || ti == null || ti.anisoLevel != expectedCurrent || target < 0 || target > 16 || target >= expectedCurrent)
                throw new InvalidOperationException("Anisotropic filtering settings changed or no strict reduction is needed.");
            ti.anisoLevel = target;
            c.NeedsReimport = true;
        }

        static bool VerifyAnisotropyLimit(FixContext c, int target, OptimizerSettings settings)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            return settings != null && target == ClampAnisotropyTarget(settings.maxAnisoLevel) && ti != null && ti.anisoLevel == target;
        }

        static void ApplySrgbLinear(FixContext c, bool expectedSrgb)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.sRGBTexture != expectedSrgb || s_TextureUsageDirty || !HasExclusiveLinearDataMaterialUsage(c.AssetPath, validateFiles: true))
                throw new InvalidOperationException("sRGB or material-role evidence changed since analysis.");
            ti.sRGBTexture = false;
            c.NeedsReimport = true;
        }

        static bool VerifySrgbLinear(FixContext c)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            return ti != null && !ti.sRGBTexture;
        }

        internal static bool ShouldAutoFixTrilinearWithoutMipmaps(bool mipmaps, FilterMode filterMode, int anisoLevel)
        {
            return !mipmaps && filterMode == FilterMode.Trilinear && anisoLevel <= 1;
        }

        static void ApplyBilinearFilter(FixContext c, FilterMode expectedFilter, int expectedAniso)
        {
            var ti = c?.Importer as TextureImporter;
            if (ti == null || ti.mipmapEnabled || ti.filterMode != expectedFilter || ti.anisoLevel != expectedAniso
                || !ShouldAutoFixTrilinearWithoutMipmaps(false, expectedFilter, expectedAniso))
                throw new InvalidOperationException("TEX015 preconditions changed since analysis.");
            ti.filterMode = FilterMode.Bilinear;
            c.NeedsReimport = true;
        }

        static bool VerifyBilinearFilter(FixContext c, int expectedAniso)
        {
            var ti = AssetImporter.GetAtPath(c?.AssetPath) as TextureImporter;
            return ti != null && !ti.mipmapEnabled && ti.filterMode == FilterMode.Bilinear && ti.anisoLevel == expectedAniso;
        }

        static void CaptureConstantPngEvidence(string assetPath, TextureFacts facts, OptimizerSettings settings)
        {
            if (facts == null || settings == null || !settings.deepTextureScan || facts.constantPngEvidenceEvaluated) return;
            facts.constantPngEvidenceEvaluated = true;
            if (facts.textureType != TextureImporterType.Default || facts.textureShape != TextureImporterShape.Texture2D
                || !facts.solidColor || facts.width * (long)facts.height <= 1) return;
            if (TryGetConstantPngSource(assetPath, out string hash, out Color32 color, out bool hasAlpha, out int width, out int height))
            {
                facts.constantPngSourceSupported = true;
                facts.constantPngHash = hash;
                facts.constantPngColor = color;
                facts.constantPngHasAlpha = hasAlpha;
                facts.constantPngSourceWidth = width;
                facts.constantPngSourceHeight = height;
            }
        }

        static bool TryGetConstantPngSource(string assetPath, out string md5, out Color32 color, out bool hasAlpha, out int width, out int height)
        {
            md5 = null; color = default; hasAlpha = false; width = height = 0;
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(assetPath), ".png", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string absolute = AssetPathUtility.ToAbsolutePath(assetPath);
                byte[] bytes = File.ReadAllBytes(absolute);
                md5 = ComputeMd5(bytes);
                if (!TryDecodeStrictPng(bytes, out Texture2D decoded, out hasAlpha)) return false;
                try
                {
                    width = decoded.width; height = decoded.height;
                    if (width <= 1 && height <= 1) return false;
                    var pixels = decoded.GetPixels32();
                    if (pixels == null || pixels.Length != (long)width * height || pixels.Length == 0) return false;
                    color = pixels[0];
                    for (int i = 1; i < pixels.Length; i++)
                        if (pixels[i].r != color.r || pixels[i].g != color.g || pixels[i].b != color.b || (hasAlpha && pixels[i].a != color.a)) return false;
                    if (!hasAlpha) color.a = 255;
                    return true;
                }
                finally { if (decoded != null) UnityEngine.Object.DestroyImmediate(decoded); }
            }
            catch { md5 = null; width = height = 0; return false; }
        }

        static bool TryDecodeStrictPng(byte[] bytes, out Texture2D decoded, out bool hasAlpha)
        {
            decoded = null; hasAlpha = false;
            if (bytes == null || bytes.Length < 45) return false;
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) return false;
            int offset = 8; bool seenHeader = false, seenData = false, seenEnd = false; int colorType = -1;
            while (offset + 12 <= bytes.Length)
            {
                uint length = ReadBigEndianUInt32(bytes, offset); offset += 4;
                if (length > int.MaxValue || (long)offset + 4 + length + 4 > bytes.Length) return false;
                string type = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); offset += 4;
                if (!seenHeader && type != "IHDR") return false;
                if (type != "IHDR" && type != "IDAT" && type != "IEND") return false; // rejects metadata/palette chunks
                if (type == "IHDR")
                {
                    if (seenHeader || length != 13) return false;
                    uint width = ReadBigEndianUInt32(bytes, offset);
                    uint height = ReadBigEndianUInt32(bytes, offset + 4);
                    int bitDepth = bytes[offset + 8];
                    colorType = bytes[offset + 9];
                    // IHDR begins with 4-byte width and height; bit depth, color type, compression,
                    // filter and interlace follow those eight bytes. Validate the actual IHDR fields.
                    if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue
                        || bitDepth != 8 || (colorType != 2 && colorType != 6)
                        || bytes[offset + 10] != 0 || bytes[offset + 11] != 0 || bytes[offset + 12] != 0) return false;
                    seenHeader = true;
                }
                else if (type == "IDAT") { if (!seenHeader || seenEnd) return false; seenData = true; }
                else if (type == "IEND") { if (!seenData || length != 0) return false; seenEnd = true; offset += 4; break; }
                offset += (int)length + 4; // chunk data and CRC
            }
            if (!seenHeader || !seenData || !seenEnd || offset != bytes.Length) return false;
            hasAlpha = colorType == 6;
            decoded = new Texture2D(2, 2, TextureFormat.ARGB32, false, false);
            if (!ImageConversion.LoadImage(decoded, bytes, false) || decoded.width <= 0 || decoded.height <= 0)
            {
                if (decoded != null) UnityEngine.Object.DestroyImmediate(decoded);
                decoded = null;
                return false;
            }
            return true;
        }

        static uint ReadBigEndianUInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        static string ComputeMd5(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", string.Empty);
            }
        }

        static string SafeMd5File(string absolutePath)
        {
            try { return File.Exists(absolutePath) ? ComputeMd5(File.ReadAllBytes(absolutePath)) : null; }
            catch { return null; }
        }

        static void RewriteConstantPng(FixContext c, string expectedHash, Color32 expectedColor, bool hasAlpha)
        {
            if (c == null || !c.BackupEnabled || c.TryBackupExtra == null)
                throw new InvalidOperationException("PAA001 requires an enabled, working source-file backup.");
            string absolute = AssetPathUtility.ToAbsolutePath(c.AssetPath);
            if (!UnityEditorUtility.CanEditPath(c.AssetPath, out string reason)) throw new InvalidOperationException("Source is not editable: " + reason);
            byte[] original = File.ReadAllBytes(absolute);
            if (!string.Equals(ComputeMd5(original), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Source PNG changed since analysis; rescan before applying PAA001.");
            if (!TryGetConstantPngSource(c.AssetPath, out string currentHash, out Color32 currentColor, out bool currentAlpha, out int width, out int height)
                || !string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                || currentAlpha != hasAlpha || currentColor.r != expectedColor.r || currentColor.g != expectedColor.g || currentColor.b != expectedColor.b || currentColor.a != expectedColor.a)
                throw new InvalidOperationException("Full-resolution constant-pixel proof no longer holds.");
            if (width <= 1 && height <= 1) throw new InvalidOperationException("Source is already 1x1.");
            if (!c.TryBackupExtra(c.AssetPath)) throw new InvalidOperationException("Could not create an exact backup of the source PNG; nothing was changed.");

            Texture2D onePixel = null;
            string tempPath = null;
            try
            {
                onePixel = new Texture2D(1, 1, hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, false);
                onePixel.SetPixel(0, 0, new Color32(expectedColor.r, expectedColor.g, expectedColor.b, hasAlpha ? expectedColor.a : (byte)255));
                onePixel.Apply(false, false);
                byte[] replacement = ImageConversion.EncodeToPNG(onePixel);
                if (replacement == null || !TryDecodeStrictPng(replacement, out Texture2D verifyPixel, out bool replacementHasAlpha))
                    throw new InvalidOperationException("Unity could not encode a supported 1x1 PNG.");
                try
                {
                    var pixels = verifyPixel.GetPixels32();
                    if (verifyPixel.width != 1 || verifyPixel.height != 1 || replacementHasAlpha != hasAlpha || pixels == null || pixels.Length != 1
                        || pixels[0].r != expectedColor.r || pixels[0].g != expectedColor.g || pixels[0].b != expectedColor.b
                        || (hasAlpha && pixels[0].a != expectedColor.a))
                        throw new InvalidOperationException("Encoded 1x1 PNG did not preserve the proven source pixel.");
                }
                finally { UnityEngine.Object.DestroyImmediate(verifyPixel); }

                tempPath = absolute + ".projectoptimizer-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, replacement);
                File.Replace(tempPath, absolute, null); // atomic same-volume replacement; fail closed if unsupported.
                tempPath = null;
                c.NeedsReimport = true;
            }
            finally
            {
                if (onePixel != null) UnityEngine.Object.DestroyImmediate(onePixel);
                if (!string.IsNullOrEmpty(tempPath)) { try { File.Delete(tempPath); } catch { } }
            }
        }

        static bool VerifyConstantPngRewrite(FixContext c, Color32 expectedColor, bool expectedAlpha)
        {
            if (c == null || string.IsNullOrEmpty(c.AssetPath)) return false;
            try
            {
                string absolute = AssetPathUtility.ToAbsolutePath(c.AssetPath);
                byte[] bytes = File.ReadAllBytes(absolute);
                if (!TryDecodeStrictPng(bytes, out Texture2D decoded, out bool alpha)) return false;
                try
                {
                    if (alpha != expectedAlpha || decoded.width != 1 || decoded.height != 1) return false;
                    var pixels = decoded.GetPixels32();
                    if (pixels == null || pixels.Length != 1 || pixels[0].r != expectedColor.r || pixels[0].g != expectedColor.g
                        || pixels[0].b != expectedColor.b || (alpha && pixels[0].a != expectedColor.a)) return false;
                }
                finally { UnityEngine.Object.DestroyImmediate(decoded); }
                var importer = AssetImporter.GetAtPath(c.AssetPath) as TextureImporter;
                var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(c.AssetPath);
                return importer != null && imported != null && imported.width == 1 && imported.height == 1;
            }
            catch { return false; }
        }


		static TextureImporterFormat ChooseTextureFormat(string platform)
		{
			if (platform.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0 || platform.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0)
				return TextureImporterFormat.ASTC_6x6;
			return TextureImporterFormat.DXT5;
		}
	}
}
