using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Shared advisory thresholds and explanations for TextCore and TextMeshPro font assets.</summary>
	public static class FontAnalysisPolicy
	{
		public const string AtlasBudgetExplanation = "The configured atlas-size threshold is a review budget, not proof that a smaller atlas can contain the required glyph set and SDF/effect margins.";
		public const string DynamicPopulationExplanation = "Dynamic population is valid when glyphs are unknown at authoring time, including localization or runtime-generated text. Prefer static/pre-populated assets only when the required glyph set is known and fixed.";
		public const string FallbackChainExplanation = "Fallback removal is a deliberate review: preserve required glyph coverage across languages, localization and runtime text before simplifying the chain.";
		public const string MultipleAtlasPagesExplanation = "Multiple atlas pages are supported and can be required by large glyph sets or target texture-size limits. Treat page count as a review signal; reduce pages only when required glyph coverage and visual quality remain valid.";

		public static int AtlasSizeBudget(OptimizerSettings settings) => settings != null ? settings.fontAtlasWarnSize : 1024;
		public static int FallbackCountBudget(OptimizerSettings settings) => Math.Max(0, settings != null ? settings.fontFallbackWarnCount : 3);
	}

	/// <summary>
	/// TextCore font assets and source font files. Font assets are inspected through SerializedObject
	/// and reflection so this analyzer does not require a direct TextCore/TMP assembly reference.
	/// </summary>
	public sealed class FontAnalyzer : IAssetAnalyzer
	{
		static readonly string[] k_SourceExtensions = { ".ttf", ".otf", ".ttc" };

		public AssetCategory Category => AssetCategory.Font;

		public AssetKindMask SupportedKinds => AssetKindMask.FontAtlas | AssetKindMask.DataAsset | AssetKindMask.Other;

		static FontAnalyzer()
		{
			Rules.Register(id: "FNT001", title: "Font atlas exceeds dimension or memory budget", category: AssetCategory.Font,
							why: FontAnalysisPolicy.AtlasBudgetExplanation + " This grouped TextCore insight reports estimated resident texture memory as a separate measurement from atlas dimensions.",
							fix: "Review atlas dimensions, generated texture memory, format and page count together. Regenerate at a smaller resolution only when the required glyph set and rendering margins remain valid.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "FNT002", title: "Font asset uses multiple atlas textures", category: AssetCategory.Font,
							why: FontAnalysisPolicy.MultipleAtlasPagesExplanation,
							fix: "Review the character set and atlas packing. Reduce pages only when the required glyph set still fits at the required visual quality.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "FNT003", title: "Dynamic font asset", category: AssetCategory.Font,
							why: FontAnalysisPolicy.DynamicPopulationExplanation,
							fix: "Use static/pre-populated assets only when the required glyph set is known and fixed; retain Dynamic or Dynamic OS for required runtime-generated or platform-provided glyphs.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "FNT004", title: "Glyph padding is large for the sampling size", category: AssetCategory.Font,
							why: "Padding consumes atlas space and also provides separation/SDF effect margin. Unity's current UI Toolkit guidance gives roughly a 1:10 padding-to-sampling-size ratio as a general rule of thumb, not a hard requirement.",
							fix: "Review padding against the actual sampling size and required SDF/outline effects; reduce it only after visual validation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "FNT005", title: "Monochrome font atlas uses a color texture format", category: AssetCategory.Font,
							why: "Monochrome bitmap/SDF font atlases normally do not need four color channels. A color texture can therefore use substantially more texture memory than an alpha-only representation, while color glyph rendering is a legitimate exception.",
							fix: "Review the atlas render mode and texture format. Prefer a single-channel/alpha representation for monochrome glyphs when the rendering pipeline supports it; keep color formats for true color glyphs.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "FNT006", title: "Dynamic font asset has no source font dependency", category: AssetCategory.Font,
							why: "Dynamic font assets require a source font dependency, while static assets are standalone. The intended source font cannot safely be inferred from a filename, so this rule offers explicit user-selected candidates.",
							fix: "Review Fix: choose the intended source Font candidate; never assign a font automatically from filename similarity.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "FNT008", title: "Font fallback chain is long", category: AssetCategory.Font,
						why: FontAnalysisPolicy.FallbackChainExplanation + " Verified automation removes only null slots, repeated references, or static fallbacks whose complete known glyph set is already covered by earlier entries.",
						fix: "Automatically remove only proven redundant fallback references; retain all non-redundant or incompletely understood entries.",
						severity: IssueSeverity.Info,
						fixKind: FixKind.Verified,
						confidence: OptimizationConfidence.HighConfidence,
						risk: FixRisk.High,
						backupRequired: true
						);

			Rules.Register(id: "FNT007", title: "Font atlas texture memory is high", category: AssetCategory.Font,
							why: "This rule contributes the generated-atlas memory measurement to the grouped font-atlas insight. Atlas dimensions and texture format jointly determine the estimate, which is distinct from the dimension budget.",
							fix: "Reduce atlas resolution/pages or use an appropriate single-channel format when the glyph set and rendering mode allow it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			for (int i = 0; i < k_SourceExtensions.Length; i++)
				if (assetPath.EndsWith(k_SourceExtensions[i], StringComparison.OrdinalIgnoreCase))
					return true;

			if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
				return false;

			var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
			if (type == null) return false;
			// TextMeshPro uses TMP_FontAsset, which deserves its own analyzer so TMP-specific atlas and
			// fallback semantics are not accidentally reported through the generic TextCore path.
			if (string.Equals(type.Name, "TMP_FontAsset", StringComparison.Ordinal)) return false;

			return string.Equals(type.Name, "FontAsset", StringComparison.Ordinal)
				   || type.Name.EndsWith("FontAsset", StringComparison.Ordinal);
		}

		[Serializable]
		public sealed class FontFacts
		{
			public int atlasWidth;
			public int atlasHeight;
			public int padding;
			public int pointSize;
			public int atlasCount;
			public string format = "-";
			public string populationMode = "-";
			public string renderMode = "-";
			public long runtimeSize = -1;
			public bool isSourceFont;
			public bool populationKnown;
			public bool renderModeKnown;
			public bool multiAtlas;
			public bool sourceFontReferencePresent;
			public bool sourceFontReferenceKnown;
			public int fallbackCount;
			public bool fallbackKnown;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var facts = ctx.Cache != null
				? ctx.GetCached(assetPath, () => Measure(assetPath))
				: Measure(assetPath);

			if (facts == null) return null;

			var entry = new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Font,
				TypeLabel = facts.isSourceFont ? "Source font" : "Font asset",
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath),
				RuntimeSize = facts.runtimeSize
			};

			entry.Set("atlas",
				facts.atlasWidth > 0 && facts.atlasHeight > 0
					? facts.atlasWidth + " x " + facts.atlasHeight
					: "-");
			entry.Set("atlasPixels",
				facts.atlasWidth > 0 && facts.atlasHeight > 0
					? (long)facts.atlasWidth * facts.atlasHeight
					: 0L);
			entry.Set("pages", (long)facts.atlasCount);
			entry.Set("multiAtlas", facts.multiAtlas ? "On" : "Off");
			entry.Set("padding", (long)facts.padding);
			entry.Set("format", facts.format);
			entry.Set("renderMode", facts.renderMode);
			entry.Set("population", facts.populationMode);
			entry.Set("populationKnown", facts.populationKnown ? "Yes" : "No");
			entry.Set("sourceFont", facts.sourceFontReferenceKnown
				? (facts.sourceFontReferencePresent ? "Assigned" : "Missing")
				: "-");
			entry.Set("fallbacks", facts.fallbackKnown ? (long)facts.fallbackCount : 0L);

			// TTF/OTF/TTC files are source inputs. Atlas-generation settings belong to the generated
			// FontAsset, so there is nothing useful to diagnose on the source file itself.
			if (facts.isSourceFont)
			{
				entry.Finalise();
				return entry;
			}

			int budget = FontAnalysisPolicy.AtlasSizeBudget(ctx.Settings);
			int largest = Mathf.Max(facts.atlasWidth, facts.atlasHeight);
			long runtimeSize = facts.runtimeSize;
			long runtimeWarnBytes = (long)Math.Max(1, budget) * Math.Max(1, budget) * 4L;
			bool atlasDimensionsExceeded;
			using (RuleTiming.Begin("FNT001", entry.Path))
				atlasDimensionsExceeded = largest > 0 && budget > 0 && largest > budget;
			bool atlasMemoryExceeded;
			using (RuleTiming.Begin("FNT007", entry.Path))
				atlasMemoryExceeded = runtimeSize > runtimeWarnBytes;
			if (atlasDimensionsExceeded || atlasMemoryExceeded)
			{
				var metrics = new List<GroupedFindingMetric>
				{
					new GroupedFindingMetric("FNT001", "Atlas dimensions",
						facts.atlasWidth + " x " + facts.atlasHeight + " (largest dimension budget " + budget + ")",
						atlasDimensionsExceeded, facts.atlasWidth + " x " + facts.atlasHeight, budget + " or smaller"),
					new GroupedFindingMetric("FNT007", "Estimated atlas memory",
						runtimeSize >= 0 ? DisplayUtility.FormatBytes(runtimeSize) + " (warning threshold " + DisplayUtility.FormatBytes(runtimeWarnBytes) + ")" : "not available",
						atlasMemoryExceeded, DisplayUtility.FormatBytes(runtimeSize), "Review atlas memory footprint")
				};
				GroupedFindingPolicy.AddGroupedIssue(entry, metrics,
					"Atlas dimensions and estimated runtime texture memory are combined into one insight but remain separate measurements. RuntimeSize estimates the generated atlas texture representation, not the source font file size.");
			}

			using (RuleTiming.Begin("FNT002", entry.Path))
				if (facts.atlasCount > 1)
				{
					entry.AddIssue("FNT002",
						facts.atlasCount + " atlas pages",
						"Review whether fewer pages are possible",
						0,
						null,
						FontAnalysisPolicy.MultipleAtlasPagesExplanation);
				}

			bool dynamicPopulation = facts.populationKnown
				&& (string.Equals(facts.populationMode, "Dynamic", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(facts.populationMode, "DynamicOS", StringComparison.OrdinalIgnoreCase));
			bool dynamicSourceMissing = facts.populationKnown
				&& string.Equals(facts.populationMode, "Dynamic", StringComparison.OrdinalIgnoreCase)
				&& facts.sourceFontReferenceKnown && !facts.sourceFontReferencePresent;
			bool specificMissingSourceWillShow = dynamicSourceMissing
				&& GroupedFindingPolicy.CanReport("FNT006", entry.Path);
			using (RuleTiming.Begin("FNT003", entry.Path))
				if (dynamicPopulation && !specificMissingSourceWillShow)
				{
					entry.AddIssue("FNT003",
						facts.populationMode,
						"Static when the glyph set is fixed",
						0,
						null,
						FontAnalysisPolicy.DynamicPopulationExplanation);
				}

			using (RuleTiming.Begin("FNT004", entry.Path))
				if (facts.pointSize > 0 && facts.padding >= 0)
				{
					float ratio = (float)facts.padding / facts.pointSize;
					entry.Set("paddingRatio", (double)ratio);

					if (ctx.Settings.fontPaddingRatioWarn > 0 && ratio > ctx.Settings.fontPaddingRatioWarn)
					{
						entry.AddIssue("FNT004",
							facts.padding + " px at point size " + facts.pointSize
							+ " (" + ratio.ToString("0.0%") + ")",
							"Review padding for the required SDF/effect margin",
							0,
							null,
							"The threshold is a project-specific review trigger. Unity's guidance is approximately 1:10, but the correct value depends on atlas render mode and required effects.");
					}
				}

			using (RuleTiming.Begin("FNT005", entry.Path))
				if (facts.renderModeKnown && IsMonochromeRenderMode(facts.renderMode)
					&& IsColorTextureFormat(facts.format))
				{
					entry.AddIssue("FNT005",
						facts.format + " / " + facts.renderMode,
						"Review alpha-only/single-channel atlas format",
						0,
						null,
						"This is advisory: color glyphs and certain rendering configurations legitimately require color formats. Do not change the format without validating the font rendering path.");
				}

			using (RuleTiming.Begin("FNT006", entry.Path))
				if (facts.populationKnown
					&& string.Equals(facts.populationMode, "Dynamic", StringComparison.OrdinalIgnoreCase)
					&& facts.sourceFontReferenceKnown
					&& !facts.sourceFontReferencePresent)
				{
					entry.AddIssue("FNT006",
						"Dynamic / source font missing",
						"Assign the source Font",
						0,
						null,
						"Unity's Dynamic font assets depend on their source font. This analyzer reports only an explicitly missing source-font reference; it does not infer whether a runtime OS font is intended.");
				}

			int fallbackBudget = FontAnalysisPolicy.FallbackCountBudget(ctx.Settings);
			using (RuleTiming.Begin("FNT008", entry.Path))
				if (facts.fallbackKnown && facts.fallbackCount > fallbackBudget)
				{
					var fontAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
					var redundantIndices = FindRedundantFallbackIndices(fontAsset, "m_FallbackFontAssetTable");
					var issue = entry.AddIssue("FNT008", facts.fallbackCount + " fallback font(s)",
						"Remove only proven redundant fallback references", 0,
						c => RemoveRedundantFallbacks(c, "m_FallbackFontAssetTable"),
						FontAnalysisPolicy.FallbackChainExplanation + " Current project threshold: " + fallbackBudget + " fallback assets. Exact duplicates/nulls are safe to remove; glyph-subset pruning is allowed only for static assets with fully readable character tables.",
						SavingsBasis.Unknown,
						c => VerifyNoRedundantFallbacks(c.AssetPath, "m_FallbackFontAssetTable"));
					if (issue != null && redundantIndices.Count == 0)
					{
						issue.FixKind = FixKind.None;
						issue.Fix = null;
						issue.Verify = null;
						issue.Recommended = "No automatic change: no null, duplicate, or provably glyph-shadowed fallback reference was found.";
					}
					else if (issue != null)
						issue.Items("Safe redundant fallback indices", FormatIndices(redundantIndices), DetailKind.Good);
				}


			entry.Finalise();
			return entry;
		}

		static FontFacts Measure(string assetPath)
		{
			var facts = new FontFacts();

			if (assetPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
				|| assetPath.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
				|| assetPath.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
			{
				facts.isSourceFont = true;
				return facts;
			}

			var main = AssetLoadUtility.LoadMainAssetAtPath(assetPath);
			if (main == null) return null;

			try
			{
				var so = new SerializedObject(main);

				var width = so.FindProperty("m_CreationSettings.atlasWidth");
				var height = so.FindProperty("m_CreationSettings.atlasHeight");
				var padding = so.FindProperty("m_CreationSettings.padding");
				var pointSize = so.FindProperty("m_CreationSettings.pointSize");
				var renderMode = so.FindProperty("m_CreationSettings.renderMode");
				var sourceFont = so.FindProperty("m_SourceFontFile");
				var fallbackTable = so.FindProperty("m_FallbackFontAssetTable");

				if (width != null) facts.atlasWidth = width.intValue;
				if (height != null) facts.atlasHeight = height.intValue;
				if (padding != null) facts.padding = padding.intValue;
				if (pointSize != null) facts.pointSize = pointSize.intValue;

				if (renderMode != null && renderMode.propertyType == SerializedPropertyType.Enum)
				{
					string[] names = renderMode.enumDisplayNames;

					if (names != null
						&& renderMode.enumValueIndex >= 0
						&& renderMode.enumValueIndex < names.Length)
					{
						facts.renderMode = names[renderMode.enumValueIndex];
						facts.renderModeKnown = true;
					}
				}

				if (sourceFont != null)
				{
					facts.sourceFontReferenceKnown = true;
					facts.sourceFontReferencePresent = sourceFont.objectReferenceValue != null;
				}

				if (fallbackTable != null && fallbackTable.propertyType == SerializedPropertyType.Generic)
				{
					facts.fallbackKnown = fallbackTable.isArray;
					if (facts.fallbackKnown) facts.fallbackCount = Mathf.Max(0, fallbackTable.arraySize);
				}

				var population = so.FindProperty("m_AtlasPopulationMode");
				if (population != null)
				{
					if (population.propertyType == SerializedPropertyType.Enum)
					{
						string[] names = population.enumDisplayNames;

						if (names != null
							&& population.enumValueIndex >= 0
							&& population.enumValueIndex < names.Length)
						{
							facts.populationMode = names[population.enumValueIndex];
							facts.populationKnown = true;
						}
						else
						{
							facts.populationMode = population.intValue.ToString();
						}
					}
					else if (population.propertyType == SerializedPropertyType.Integer)
					{
						// Some Unity/TextCore versions serialize this as an integer
						// rather than a SerializedProperty enum.
						facts.populationMode = population.intValue.ToString();
					}
				}

				var multiAtlas = so.FindProperty("m_IsMultiAtlasTextures");
				if (multiAtlas != null)
					facts.multiAtlas = multiAtlas.boolValue;
			}
			catch
			{
				// Some font-asset implementations do not expose the same serialized layout.
			}

			// Public/non-public reflected property is used as a fallback for TextCore font asset
			// implementations where the serialized field layout differs.
			try
			{
				var property = main.GetType().GetProperty(
					"atlasPopulationMode",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

				if (property != null)
				{
					var value = property.GetValue(main, null);
					if (value != null)
					{
						facts.populationMode = value.ToString();
						facts.populationKnown = true;
					}
				}

				var renderProperty = main.GetType().GetProperty(
					"atlasRenderMode",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

				if (renderProperty != null)
				{
					var value = renderProperty.GetValue(main, null);
					if (value != null)
					{
						facts.renderMode = value.ToString();
						facts.renderModeKnown = true;
					}
				}
			}
			catch
			{
				// Optional API; leave the value unknown.
			}

			long total = 0;
			int pages = 0;

			var assets = AssetLoadUtility.LoadAllAssetRepresentationsAtPath(assetPath);
			for (int i = 0; i < assets.Length; i++)
			{
				var texture = assets[i] as Texture2D;
				if (texture == null) continue;

				string name = texture.name ?? string.Empty;
				if (name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase))
					continue;

				pages++;

				if (pages == 1)
				{
					facts.format = texture.format.ToString();

					// Serialized creation settings are preferable, but runtime subasset dimensions
					// are a safe fallback when those settings were not exposed.
					if (facts.atlasWidth <= 0) facts.atlasWidth = texture.width;
					if (facts.atlasHeight <= 0) facts.atlasHeight = texture.height;
				}

				long size = UnityEditorUtility.RuntimeSizeOf(texture);
				if (size > 0) total += size;
			}

			facts.atlasCount = pages;
			facts.multiAtlas = facts.multiAtlas || pages > 1;
			facts.runtimeSize = total > 0 ? total : -1;

			return facts;
		}

		static List<FixOption> BuildSourceFontReviewOptions(string targetAssetPath)
		{
			var options = new List<FixOption>();
			var paths = SourceFontCandidatePaths();
			for (int i = 0; i < paths.Count; i++)
			{
				string candidatePath = paths[i];
				var candidate = AssetDatabase.LoadAssetAtPath<Font>(candidatePath);
				if (candidate == null) continue;
				options.Add(new FixOption
				{
					Label = "Assign source Font: " + candidatePath,
					ResultDescription = candidatePath,
					Recommended = false,
					Risk = FixRisk.High,
					Warning = "This is an explicit Review action. Confirm this is the intended face and language coverage before applying; the target asset is backed up.",
					Apply = c => AssignSourceFont(c, candidatePath),
					Verify = c => IsAssignedSourceFont(c.AssetPath, candidatePath)
				});
			}
			return options;
		}

		static List<string> SourceFontCandidatePaths()
		{
			var result = new List<string>();
			string[] guids = AssetDatabase.FindAssets("t:Font");
			for (int i = 0; i < guids.Length; i++)
			{
				string path = AssetDatabase.GUIDToAssetPath(guids[i]);
				if (string.IsNullOrEmpty(path)) continue;
				string extension = System.IO.Path.GetExtension(path);
				if (!Array.Exists(k_SourceExtensions, x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase))) continue;
				if (AssetDatabase.LoadAssetAtPath<Font>(path) != null) result.Add(path);
			}
			result.Sort(StringComparer.OrdinalIgnoreCase);
			return result;
		}

		static void AssignSourceFont(FixContext context, string sourcePath)
		{
			context.EnsureWritable();
			var target = AssetDatabase.LoadMainAssetAtPath(context.AssetPath);
			var source = AssetDatabase.LoadAssetAtPath<Font>(sourcePath);
			if (target == null || source == null) throw new InvalidOperationException("FNT006 preflight blocked: target or selected source Font no longer exists.");
			var so = new SerializedObject(target);
			var property = so.FindProperty("m_SourceFontFile");
			if (property == null || property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue != null)
				throw new InvalidOperationException("FNT006 preflight blocked: source-font property changed or is not writable.");
			property.objectReferenceValue = source;
			if (!so.ApplyModifiedProperties()) throw new InvalidOperationException("FNT006 could not commit the selected source-font reference.");
			EditorUtility.SetDirty(target);
			context.NeedsSave = true;
		}

		static bool IsAssignedSourceFont(string targetPath, string sourcePath)
		{
			var target = AssetDatabase.LoadMainAssetAtPath(targetPath);
			if (target == null) return false;
			var so = new SerializedObject(target);
			var property = so.FindProperty("m_SourceFontFile");
			return property != null && property.propertyType == SerializedPropertyType.ObjectReference
				&& property.objectReferenceValue != null
				&& string.Equals(AssetDatabase.GetAssetPath(property.objectReferenceValue), sourcePath, StringComparison.OrdinalIgnoreCase);
		}

		static List<int> FindRedundantFallbackIndices(UnityEngine.Object asset, string fallbackPropertyPath)
		{
			var result = new List<int>();
			if (asset == null) return result;
			var so = new SerializedObject(asset);
			var fallback = so.FindProperty(fallbackPropertyPath);
			if (fallback == null || !fallback.isArray) return result;

			var seen = new HashSet<int>();
			var covered = new HashSet<uint>();
			bool canProveGlyphCoverage = IsStaticPopulation(asset) && TryCollectKnownGlyphs(asset, out covered);
			for (int i = 0; i < fallback.arraySize; i++)
			{
				var element = fallback.GetArrayElementAtIndex(i);
				if (element.propertyType != SerializedPropertyType.ObjectReference) { canProveGlyphCoverage = false; continue; }
				var candidate = element.objectReferenceValue;
				if (candidate == null) { result.Add(i); continue; }
				if (!seen.Add(candidate.GetInstanceID())) { result.Add(i); continue; }

				if (canProveGlyphCoverage && IsStaticPopulation(candidate) && TryCollectKnownGlyphs(candidate, out var candidateGlyphs))
				{
					if (IsSubset(candidateGlyphs, covered)) result.Add(i);
					else covered.UnionWith(candidateGlyphs);
				}
				else canProveGlyphCoverage = false;
			}
			return result;
		}

		static bool IsStaticPopulation(UnityEngine.Object asset)
		{
			if (asset == null) return false;
			try
			{
				object mode = ReadMember(asset, "atlasPopulationMode", "m_AtlasPopulationMode");
				if (mode != null) return string.Equals(mode.ToString(), "Static", StringComparison.OrdinalIgnoreCase);
				var so = new SerializedObject(asset);
				var property = so.FindProperty("m_AtlasPopulationMode");
				if (property == null) property = so.FindProperty("m_AtlasPopulationModeSettings.populationMode");
				return property != null && property.propertyType == SerializedPropertyType.Enum
					&& property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
					&& string.Equals(property.enumDisplayNames[property.enumValueIndex], "Static", StringComparison.OrdinalIgnoreCase);
			}
			catch { return false; }
		}

		static bool TryCollectKnownGlyphs(UnityEngine.Object asset, out HashSet<uint> glyphs)
		{
			glyphs = new HashSet<uint>();
			if (asset == null) return false;
			try
			{
				object table = ReadMember(asset, "characterTable", "m_CharacterTable");
				var enumerable = table as System.Collections.IEnumerable;
				if (enumerable == null) return false;
				foreach (object character in enumerable)
				{
					object unicode = ReadMember(character, "unicode", "m_Unicode");
					if (unicode == null) return false;
					glyphs.Add(Convert.ToUInt32(unicode, System.Globalization.CultureInfo.InvariantCulture));
				}
				return true;
			}
			catch { glyphs.Clear(); return false; }
		}

		static object ReadMember(object target, string propertyOrField, string fallbackField = null)
		{
			if (target == null) return null;
			var type = target.GetType();
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			var property = type.GetProperty(propertyOrField, flags);
			if (property != null && property.GetIndexParameters().Length == 0)
				try { return property.GetValue(target, null); } catch { }
			var field = type.GetField(propertyOrField, flags) ?? (!string.IsNullOrEmpty(fallbackField) ? type.GetField(fallbackField, flags) : null);
			if (field != null) try { return field.GetValue(target); } catch { }
			return null;
		}

		static bool IsSubset(HashSet<uint> subset, HashSet<uint> superset)
		{
			if (subset == null || superset == null) return false;
			foreach (uint value in subset) if (!superset.Contains(value)) return false;
			return true;
		}

		static void RemoveRedundantFallbacks(FixContext context, string fallbackPropertyPath)
		{
			context.EnsureWritable();
			var asset = AssetDatabase.LoadMainAssetAtPath(context.AssetPath);
			var indices = FindRedundantFallbackIndices(asset, fallbackPropertyPath);
			if (indices.Count == 0) throw new InvalidOperationException("No safe redundant fallback references remain; the asset changed since analysis.");
			var so = new SerializedObject(asset);
			var fallback = so.FindProperty(fallbackPropertyPath);
			if (fallback == null || !fallback.isArray) throw new InvalidOperationException("Fallback array is unavailable or changed since analysis.");
			int expectedCount = fallback.arraySize;
			for (int i = indices.Count - 1; i >= 0; i--)
			{
				int index = indices[i];
				if (index < 0 || index >= fallback.arraySize) throw new InvalidOperationException("Fallback array indices changed since analysis.");
				fallback.DeleteArrayElementAtIndex(index);
				if (fallback.arraySize == expectedCount) fallback.DeleteArrayElementAtIndex(index);
				expectedCount = fallback.arraySize;
			}
			if (!so.ApplyModifiedProperties()) throw new InvalidOperationException("Unity did not apply the fallback-array changes.");
			EditorUtility.SetDirty(asset);
			context.NeedsSave = true;
		}

		static bool VerifyNoRedundantFallbacks(string assetPath, string fallbackPropertyPath)
		{
			var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
			return asset != null && FindRedundantFallbackIndices(asset, fallbackPropertyPath).Count == 0;
		}

		static List<string> FormatIndices(List<int> indices)
		{
			var result = new List<string>(indices != null ? indices.Count : 0);
			if (indices != null) for (int i = 0; i < indices.Count; i++) result.Add("Fallback index " + indices[i]);
			return result;
		}

		static bool IsColorTextureFormat(string format)
		{
			if (string.IsNullOrEmpty(format)) return false;
			return format.IndexOf("RGBA", StringComparison.OrdinalIgnoreCase) >= 0
				|| format.IndexOf("ARGB", StringComparison.OrdinalIgnoreCase) >= 0
				|| format.IndexOf("RGB", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		static bool IsMonochromeRenderMode(string renderMode)
		{
			if (string.IsNullOrEmpty(renderMode)) return false;
			// Unity exposes color and bitmap/SDF-family render modes by enum display name.
			// We deliberately avoid assuming an exact complete enum list across Unity versions.
			string mode = renderMode.ToLowerInvariant();
			return mode.IndexOf("color", StringComparison.Ordinal) < 0;
		}
	}
}
