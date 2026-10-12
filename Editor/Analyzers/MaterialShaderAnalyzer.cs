using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Rendering;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Per-asset analysis of materials and shaders, plus a project-level pass that finds
	/// materials which are semantically equivalent in the serialized shader state and shader variant collections that may need review.
	/// </summary>
	public sealed class MaterialShaderAnalyzer : IAssetAnalyzer, IProjectAnalyzer
	{
		static readonly string[] k_BuiltInPrefixes = { "Legacy Shaders/", "Mobile/", "VertexLit", "Particles/" };
		static readonly string[] k_BuiltInNames = { "Standard", "Standard (Specular setup)", "Standard (Roughness setup)" };

		const long k_LargeVariantWarning = 512;
		const int k_LargePassWarning = 6;
		const int k_LargeLocalKeywordWarning = 32;
		const int k_LargeVariantCollectionWarning = 10000;

		readonly Dictionary<string, List<AssetEntry>> m_BySignature = new Dictionary<string, List<AssetEntry>>();
		readonly Dictionary<string, List<AssetEntry>> m_ByFamily = new Dictionary<string, List<AssetEntry>>();
		Dictionary<string, HashSet<string>> m_MaterialKeywordsByShader = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		Dictionary<string, int> m_MaterialCountsByShader = new Dictionary<string, int>(StringComparer.Ordinal);
		Dictionary<string, HashSet<string>> m_ShadersUsingMaterialKeyword = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

		static Dictionary<string, HashSet<string>> s_MaterialKeywordsByShader;
		static Dictionary<string, int> s_MaterialCountsByShader;
		static Dictionary<string, HashSet<string>> s_ShadersUsingMaterialKeyword;
		static bool s_MaterialUsageDirty = true;
		readonly HashSet<string> m_AlwaysIncludedShaders = new HashSet<string>(StringComparer.Ordinal);
		bool m_UsingSrp;

		sealed class ShaderCapabilityFacts
		{
			public int keywordCount;
			public bool instancingSupported;
			public int srpBatcherCompatibilityCode;
		}
		sealed class ShaderPropertyDescriptor
		{
			public string name;
			public ShaderPropertyType type;
		}
		readonly Dictionary<int, ShaderCapabilityFacts> m_ShaderCapabilities = new Dictionary<int, ShaderCapabilityFacts>();
		static readonly Dictionary<int, ShaderPropertyDescriptor[]> s_ShaderProperties = new Dictionary<int, ShaderPropertyDescriptor[]>();
		static MethodInfo s_SrpBatcherMethod;
		static bool s_SrpBatcherMethodResolved;

		public AssetCategory Category => AssetCategory.Material;

		public AssetKindMask SupportedKinds => AssetKindMask.MaterialShader | AssetKindMask.Other;
		public string DisplayName => "Duplicate materials";

		/// <summary>
		/// Material properties plus the two SerializedObject walks (orphan textures and the
		/// equality signature), which dominate this analyzer's cost. Cached per dependency hash.
		/// </summary>
		[Serializable]
		public sealed class MaterialFacts
		{
			public string shaderName = "<missing>";
			public bool shaderMissing;
			public bool instancing;
			public int renderQueue;
			public int keywordCount;
			public int passCount;
			public bool pointlesslyTransparent;
			public string signature;
			public List<string> orphanTextures = new List<string>();
			public bool instancingSupported;
			public int shaderKeywordCount;
			public bool redundantRenderQueueOverride;
			public bool emissionGiMismatch;
			public bool srpBatcherCompatible;
			public List<string> unsupportedKeywords = new List<string>();
			public string familySignature;
		}

		/// <summary>
		/// Shader facts: compiling and inspecting a shader is not free either, so these are cached
		/// against the shader's dependency hash.
		/// <para>
		/// Only what the shader file itself determines belongs here. Anything decided by the rest of
		/// the project - how many materials use it, whether Graphics Settings lists it as Always
		/// Included, whether the current editor platform supports it - is recomputed on every scan
		/// by <see cref="ApplyProjectState"/>. Those values do not change when the shader changes,
		/// which is exactly the problem: cached against the shader's hash, they would survive edits
		/// to Graphics Settings indefinitely, and SHD009 would keep answering a question about a
		/// setting somebody changed weeks ago.
		/// </para>
		/// </summary>
		[Serializable]
		public sealed class ShaderFacts
		{
			public string shaderName = string.Empty;
			public int passCount;
			public int renderQueue;
			public int keywordCount;
			public int multiCompileLines;
			public int shaderFeatureLines;
			public long estimatedMultiCompileVariants;
			public long estimatedShaderFeatureVariants;
			public int duplicateKeywordSets;
			public int duplicateKeywordsInSets;
			public bool hasError;
			public bool hasWarnings;
			public int messageCount;
			public int warningCount;
			public bool hasLargeMultiCompile;
			public List<string> multiCompileMaterialCandidates = new List<string>();
			public List<string> localKeywordCandidates = new List<string>();

			// --- recomputed every scan, never read from the cache -------------------------
			[NonSerialized] public bool supportedInEditor;
			[NonSerialized] public int materialCount;
			[NonSerialized] public int enabledMaterialKeywordCount;
			[NonSerialized] public bool alwaysIncluded;
			[NonSerialized] public int srpBatcherCompatibilityCode;
		}

		static MaterialShaderAnalyzer()
		{
			try { EditorApplication.projectChanged += InvalidateMaterialUsage; } catch { }
			// Confidence is tracked separately from severity throughout this analyzer. A broken
			// shader reference is Critical and Proven - the reference resolved to null, there is
			// nothing to infer. A shader's pass count is also a fact, but what it implies about
			// performance is a guess, so the finding is only Diagnostic. Conflating the two is how a
			// count of passes ends up presented with the same weight as a broken dependency.
			Rules.Register(id: "MAT001", title: "Material has a missing or broken shader", category: AssetCategory.Material,
							why: "A material whose shader failed to load can render with Unity's error shader and indicates a broken rendering dependency.",
							fix: "Reassign a valid shader and fix any shader compilation problems.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT002", title: "Built-in pipeline shader in a Scriptable Render Pipeline project", category: AssetCategory.Material,
							why: "Built-in pipeline shaders are not interchangeable with URP or HDRP shaders. A blind shader swap can change lighting, surface behavior, keywords and material properties.",
							fix: "Replace the shader deliberately and migrate the material properties and rendering settings required by the target pipeline.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT003", title: "GPU instancing disabled on an instancing-capable shader", category: AssetCategory.Material,
							why: "GPU instancing can reduce draw overhead when many suitable renderers share the same mesh and material. It is workload-dependent, and the best path depends on the active render pipeline and batching setup.",
							fix: "Enable GPU Instancing only when the material is reused by suitable renderers and profiling shows a benefit.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT004", title: "Duplicate material", category: AssetCategory.Material,
							why: "Separate material assets with equivalent rendering state add redundant project data and can make material management harder.",
							fix: "Consolidate genuinely equivalent material assets where all references can safely share one material.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT005", title: "Orphaned texture references", category: AssetCategory.Material,
							why: "Materials can retain serialized texture entries for properties that the current shader no longer declares. Those references can unnecessarily keep texture assets reachable.",
							fix: "Review Fix: remove serialized texture entries that are no longer used by the current shader. Kept as Review rather than Verified because a shader can still read a property through custom code outside the serialized property model that this analyzer cannot see.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT006", title: "Transparent material appears fully opaque", category: AssetCategory.Material,
							why: "For supported stock shaders, the material appears to have no detected source of transparency while using Transparent surface mode. The serialized material facts are not a complete proof of shader semantics, so changing surface mode must remain an explicit review decision.",
							fix: "Review whether Transparent mode is actually required before switching this material to Opaque.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "MAT007", title: "Redundant material render-queue override", category: AssetCategory.Material,
							why: "A material render-queue override equal to the shader's current queue stores unnecessary per-material state, but removing the override changes future behaviour if the shader queue later changes.",
							fix: "Review resetting the material render queue to the shader default when the explicit override is no longer intentional.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "MAT008", title: "Material has keywords not present in the shader keyword space", category: AssetCategory.Material,
							why: "A material should only need local shader keywords declared by the shader it uses. When project-wide evidence shows these states are no longer declared or consumed, removing only those stale local states is a deterministic cleanup.",
							fix: "Remove only the stale local keyword states that are no longer present in the material shader's keyword space and are not referenced by the project source scan.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);

			Rules.Register(id: "MAT009", title: "Material is part of a large shared shader-property family", category: AssetCategory.Material,
							why: "Many materials that share a shader, texture state and keyword state are candidates for Material Variants or a shared base material when their remaining property differences are intentional and compatible with that workflow.",
							fix: "Consider Material Variants or a shared base material when the content workflow benefits from it. This is a structural optimization, not an automatic conversion.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT010", title: "Material emission GI flags appear inconsistent", category: AssetCategory.Material,
							why: "Unity uses MaterialGlobalIlluminationFlags to describe how emissive lighting interacts with lightmapping and light probes. A black emission value with emissive GI enabled can leave unnecessary GI bookkeeping.",
							fix: "Review the material's emission settings and use Unity's MaterialEditor emissive-flag fixup when appropriate.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD001", title: "Shader has compile errors", category: AssetCategory.Shader,
							why: "A shader with compilation errors cannot reliably provide its intended rendering programs.",
							fix: "Open the shader and fix the compiler errors.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD002", title: "Shader has a large multi_compile variant space", category: AssetCategory.Shader,
							why: "multi_compile declarations produce variants for all option combinations, so variant counts can grow multiplicatively. This can increase compilation time, build size and shader loading cost.",
							fix: "Use shader_feature for material-controlled features where runtime global keyword changes are not required, or strip variants deliberately.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD003", title: "Shader has many passes", category: AssetCategory.Shader,
							why: "Multiple passes are often legitimate for shadows, depth, meta passes and other pipeline features, but unnecessary passes increase shader complexity and may add rendering work.",
							fix: "Review each pass and remove only passes that are provably unused by the shader and active render pipeline.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD004", title: "Shader has compiler warnings", category: AssetCategory.Shader,
							why: "Shader compiler warnings can indicate deprecated constructs, precision issues, unsupported paths or other conditions worth cleaning up before optimizing further.",
							fix: "Inspect the shader compiler messages and fix warnings that are relevant to the target platforms.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD005", title: "Shader is unsupported on the editor's current platform", category: AssetCategory.Shader,
							why: "Unity reports shader support for the platform the editor is currently set to build for, and for nothing else. That is often not the platform this scan is profiling: the optimizer's target profile is chosen separately, so a Windows editor session says nothing about whether the shader works on Android. An unsupported shader falls back or fails to render on the platform it was reported for.",
							fix: "Inspect platform restrictions, required graphics APIs and fallback behaviour. To check a different platform, switch the editor's build target and scan again - this rule cannot answer for a platform the editor is not set to.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD006", title: "Shader has a large local keyword space", category: AssetCategory.Shader,
							why: "Large local keyword spaces increase shader state complexity and can contribute to a large variant space. Unity documents local keywords as the preferred scope when a keyword does not need global control.",
							fix: "Reduce unnecessary keywords and prefer local declarations when global keyword control is not required.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD007", title: "multi_compile feature appears material-driven", category: AssetCategory.Shader,
							why: "Unity recommends shader_feature when keyword state is determined by materials and does not change from runtime code, because unused shader_feature variants can be stripped.",
							fix: "Review the highlighted multi_compile declarations; convert them to shader_feature only when runtime/global keyword changes are not required.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD008", title: "Global multi_compile feature may be local", category: AssetCategory.Shader,
							why: "Global-scope keyword declarations consume project-wide keyword namespace and can be unnecessary when a feature is only used by this shader.",
							fix: "Consider multi_compile_local when the keyword never needs to be controlled globally.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD009", title: "Large variant shader is Always Included", category: AssetCategory.Shader,
							why: "Always Included Shaders causes Unity to include all possible variants for a listed shader, overriding the normal shader_feature stripping advantage. Unity specifically warns against using this for shaders with many variants.",
							fix: "Remove the shader from Always Included when safe, or replace the requirement with an appropriately collected Shader Variant Collection.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD010", title: "Shader has a large keyword/directive footprint (heuristic)", category: AssetCategory.Shader,
							why: "This simple footprint adds local keyword count to multi_compile directive count. Those unlike units form a complexity heuristic, not an estimate of effective shader variants.",
							fix: "Review keyword and directive counts as separate complexity clues. Use the separate SHD002 and SHD013 variant-space estimates for variant-related signals; do not interpret this sum as a variant count.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD011", title: "Material-driven keyword usage should be reviewed against variant declarations", category: AssetCategory.Shader,
							why: "The analyzer found shader keywords that are used by project materials and also appear in global multi_compile declarations. These are candidates for shader_feature or local scope, but runtime scripts may still require multi_compile.",
							fix: "Review material usage and runtime keyword changes before changing the declaration.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD012", title: "Shader Variant Collection contains many variants", category: AssetCategory.Shader,
							why: "A Shader Variant Collection is intended to record variants actually needed by a project or play session. Very large collections increase shader preloading data and can slow warmup and loading.",
							fix: "Review whether the collection still contains variants required by current content and target platforms. Do not remove variants blindly when AssetBundles, Addressables or runtime keyword changes depend on them.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD013", title: "Shader has a large shader_feature variant space", category: AssetCategory.Shader,
							why: "shader_feature declarations also create potential keyword combinations, although unused variants can usually be stripped from the build. A large potential space can still increase compilation and analysis cost.",
							fix: "Review unnecessary feature sets and use local declarations where global control is not required. Treat the count as potential space, not final build size.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD014", title: "Shader contains duplicate keyword-set declarations", category: AssetCategory.Shader,
							why: "Unity's shader keyword declaration rules do not allow duplicate keyword sets. Repeating a set or keyword can lead to compiler errors or unnecessary source complexity.",
							fix: "Remove duplicate keyword declarations and recompile the shader.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);
		}

		static void InvalidateMaterialUsage()
		{
			s_MaterialUsageDirty = true;
		}

		public void BeginScan(ScanContext ctx)
		{
			m_BySignature.Clear();
			m_ByFamily.Clear();
			m_AlwaysIncludedShaders.Clear();
			m_ShaderCapabilities.Clear();
			m_UsingSrp = GraphicsSettings.currentRenderPipeline != null;

			CollectAlwaysIncludedShaders();
			if (!s_MaterialUsageDirty && s_MaterialCountsByShader != null)
			{
				m_MaterialCountsByShader = s_MaterialCountsByShader;
				m_MaterialKeywordsByShader = s_MaterialKeywordsByShader;
				m_ShadersUsingMaterialKeyword = s_ShadersUsingMaterialKeyword;
				return;
			}

			m_MaterialKeywordsByShader = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			m_MaterialCountsByShader = new Dictionary<string, int>(StringComparer.Ordinal);
			m_ShadersUsingMaterialKeyword = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			CollectMaterialUsageIndex();
			s_MaterialCountsByShader = m_MaterialCountsByShader;
			s_MaterialKeywordsByShader = m_MaterialKeywordsByShader;
			s_ShadersUsingMaterialKeyword = m_ShadersUsingMaterialKeyword;
			s_MaterialUsageDirty = false;
		}

		public bool Handles(string assetPath)
		{
			return assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
				   || assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
				   || assetPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
				   || assetPath.EndsWith(".shadervariants", StringComparison.OrdinalIgnoreCase);
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			if (assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
				return AnalyzeMaterial(assetPath, ctx);

			if (assetPath.EndsWith(".shadervariants", StringComparison.OrdinalIgnoreCase))
				return AnalyzeShaderVariantCollection(assetPath, ctx);

			return AnalyzeShader(assetPath, ctx);
		}

		MaterialFacts MeasureMaterial(string assetPath)
		{
			var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
			if (material == null) return null;

			var shader = material.shader;
			ShaderCapabilityFacts capability = shader != null ? GetShaderCapabilityFacts(shader) : null;
			var facts = new MaterialFacts
			{
				shaderName = shader != null ? shader.name : "<missing>",
				shaderMissing = shader == null,
				instancing = material.enableInstancing,
				renderQueue = material.renderQueue,
				keywordCount = material.enabledKeywords.Length,
				passCount = material.passCount,
				shaderKeywordCount = capability != null ? capability.keywordCount : 0,
				redundantRenderQueueOverride = material.rawRenderQueue >= 0
					&& shader != null
					&& material.rawRenderQueue == shader.renderQueue
			};

			if (shader != null && facts.shaderName != "Hidden/InternalErrorShader")
			{
				facts.orphanTextures = FindOrphanTextureProperties(material);
				facts.pointlesslyTransparent = IsPointlesslyTransparent(material);
				if (capability == null) capability = GetShaderCapabilityFacts(shader);
				facts.instancingSupported = capability.instancingSupported;
				facts.srpBatcherCompatible = capability.srpBatcherCompatibilityCode == 0;
				facts.unsupportedKeywords = FindUnsupportedKeywords(material);
				facts.emissionGiMismatch = HasEmissionGIMismatch(material);
				facts.signature = BuildSignature(material);
				facts.familySignature = BuildFamilySignature(material);
			}
			return facts;
		}

		AssetEntry AnalyzeMaterial(string assetPath, ScanContext ctx)
		{
			var settings = OptimizerSettings.Instance;
			var facts = ctx.Cache != null
				? ctx.GetCached(assetPath, () => MeasureMaterial(assetPath))
				: MeasureMaterial(assetPath);
			if (facts == null) return null;

			var entry = NewEntry(assetPath, AssetCategory.Material, "Material", ctx);
			string shaderName = facts.shaderName;

			entry.Set("shader", shaderName);
			entry.Set("instancing", facts.instancing ? "Yes" : "No");
			entry.Set("queue", (long)facts.renderQueue);
			entry.Set("keywords", (long)facts.keywordCount);
			entry.Set("passes", (long)facts.passCount);
			entry.Set("rawQueueOverride", facts.redundantRenderQueueOverride ? (long)facts.renderQueue : -1L);
			entry.Set("shaderKeywordCount", (long)(facts.shaderMissing ? 0 : facts.shaderKeywordCount));

			// MAT001 - broken shader
			using (RuleTiming.Begin("MAT001", entry.Path))
				if (facts.shaderMissing || shaderName == "Hidden/InternalErrorShader")
				{
					entry.AddIssue("MAT001", shaderName, "A valid shader");
				}
				else
				{
					// MAT002 - wrong pipeline.
					//
					// Reported only, and the rule's own text says why: built-in and URP shaders are not
					// interchangeable. Reassigning the shader and copying _MainTex and _Color across is a
					// guess at a migration - it drops every other property the material had, and there is
					// no way to tell from here whether the result is the intended look or a flat grey
					// approximation of it. The finding is worth making; the edit is not one to make
					// unattended.
					using (RuleTiming.Begin("MAT002", entry.Path))
						if (m_UsingSrp && IsBuiltInShader(shaderName))
						{
							entry.AddIssue("MAT002", shaderName, "A shader from the active pipeline", 0, null,
								"Migrate this deliberately - Edit > Rendering > Materials > Convert handles the stock "
								+ "shaders, and hand-authored ones need their properties mapped across by someone who "
								+ "knows what the material is meant to look like.");
						}

					// MAT003 - instancing support is a workload decision. Do not auto-enable it solely
					// from the material because SRP Batcher and GPU instancing are different rendering
					// paths and the better one depends on the renderer workload.
					using (RuleTiming.Begin("MAT003", entry.Path))
						if (!facts.instancing && facts.instancingSupported && !IsUiShader(shaderName)
							&& !(m_UsingSrp && facts.srpBatcherCompatible))
						{
							entry.AddIssue("MAT003", "Instancing off", "Review for repeated meshes/materials");
						}

					// MAT005 - orphaned texture slots. Review Fix: the shader's serialized property model
					// says these slots are unused, but a shader can still reach a texture through custom
					// C# (Material.SetTexture at runtime, a custom editor, a URP/HDRP feature) that never
					// shows up as a declared property - which this analyzer has no way to rule out.
					var orphans = facts.orphanTextures;
					using (RuleTiming.Begin("MAT005", entry.Path))
						if (orphans.Count > 0)
						{
							entry.AddIssue("MAT005", orphans.Count + " orphaned texture slot(s)", "Review Fix: remove them", 0,
									c => StripOrphanTextureProperties((Material)c.Asset, c),
									verify: c =>
									{
										// Re-read from the path. The Material the fix held is the one it
										// edited, so asking it would only confirm the edit happened in
										// memory - the question is whether it reached the file.
										var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
										return saved != null && FindOrphanTextureProperties(saved).Count == 0;
									})
								?.Items("Orphaned slots", orphans);
						}

					// MAT006 - needless transparency. The detector is constrained to supported stock
					// shader families and proves an opaque tint plus alpha-free base texture.
					using (RuleTiming.Begin("MAT006", entry.Path))
						if (facts.pointlesslyTransparent)
						{
							entry.AddIssue("MAT006", "Surface Type: Transparent", "Surface Type: Opaque", 0,
								c => MakeOpaqueMaterial(c),
								verify: c => VerifyOpaqueMaterial(c));
						}

					// MAT007 - redundant queue override
					using (RuleTiming.Begin("MAT007", entry.Path))
						if (facts.redundantRenderQueueOverride)
						{
							entry.AddIssue("MAT007",
								"Override " + facts.renderQueue + " (same as shader default)",
								"Reset to shader queue",
								0, null,
								"The explicit override currently matches the shader queue. Removing it also means future shader queue changes will affect this material, so this is a Review Fix.",
								options: new List<FixOption>
								{
									new FixOption
									{
										ActionId = "reset-render-queue",
										Label = "Reset to Shader Queue",
										ResultDescription = "Removes the explicit material queue override.",
										Recommended = true,
										Risk = FixRisk.Medium,
										Warning = "Future changes to the shader's render queue will then affect this material.",
										Apply = c =>
										{
											var material = c.Asset as Material;
											if (material == null) throw new InvalidOperationException("Material unavailable.");
											material.renderQueue = -1;
											c.NeedsSave = true;
										},
										Verify = c =>
										{
											var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
											return saved != null && saved.rawRenderQueue < 0;
										}
									}
								});
						}

					// MAT008 - local keyword state that no longer exists on the current shader.
					using (RuleTiming.Begin("MAT008", entry.Path))
						if (facts.unsupportedKeywords.Count > 0)
						{
							if (NoProjectShaderKeywordUse(ctx, assetPath, facts.unsupportedKeywords))
							{
								entry.AddIssue("MAT008", facts.unsupportedKeywords.Count + " unsupported keyword(s)", "Remove stale local keyword state", 0,
									c => RemoveUnsupportedMaterialKeywords(c, facts.unsupportedKeywords),
									"Project-wide shader/material source scan found no static use of these stale keywords. The fix removes only the invalid local state.",
									verify: c => VerifyNoUnsupportedMaterialKeywords(c, facts.unsupportedKeywords))?.Items("Keywords", facts.unsupportedKeywords);
							}
						}

					using (RuleTiming.Begin("MAT010", entry.Path))
						if (facts.emissionGiMismatch)
							entry.AddIssue("MAT010",
								"Emission appears black while emissive GI is enabled",
								"Review / synchronize emissive GI flags");

					RegisterSignature(facts.signature, entry);
					RegisterFamily(facts.familySignature, entry);
				}

			entry.Finalise();
			return entry;
		}

		static Shader LoadShaderAsset(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			var shader = AssetLoadUtility.LoadAssetAtPath<Shader>(assetPath);
			if (shader != null) return shader;
			shader = AssetLoadUtility.LoadMainAssetAtPath(assetPath) as Shader;
			if (shader != null) return shader;
			try
			{
				var assets = AssetLoadUtility.LoadAllAssetRepresentationsAtPath(assetPath);
				for (int i = 0; i < assets.Length; i++)
					if (assets[i] is Shader candidate) return candidate;
			}
			catch { }
			return null;
		}

		ShaderFacts MeasureShader(string assetPath, ScanContext ctx)
		{
			var shader = LoadShaderAsset(assetPath);
			if (shader == null) return null;

			var info = ShaderUtil.GetShaderInfo(shader);
			var facts = new ShaderFacts
			{
				shaderName = shader.name,
				passCount = shader.passCount,
				renderQueue = shader.renderQueue,
				hasError = info.hasErrors,
				hasWarnings = info.hasWarnings
			};

			try
			{
				uint count = shader.keywordSpace.keywordCount;
				facts.keywordCount = count > int.MaxValue ? int.MaxValue : (int)count;
			}
			catch
			{
				facts.keywordCount = 0;
			}

			facts.messageCount = ShaderUtil.GetShaderMessageCount(shader);
			if (facts.hasWarnings)
			{
				try
				{
					var messages = ShaderUtil.GetShaderMessages(shader);
					int warnings = 0;
					for (int i = 0; i < messages.Length; i++)
						if (messages[i].severity == ShaderCompilerMessageSeverity.Warning)
							warnings++;
					facts.warningCount = warnings;
				}
				catch
				{
					facts.warningCount = 0;
				}
			}

			if (facts.messageCount == 0) facts.messageCount = info.hasErrors ? 1 : 0;

			// For hand-written .shader assets, estimate source-level variant pressure from multi_compile directives
			// without forcing shader compilation. This is deliberately only an estimate: includes, shader stages,
			// platform expansion, pipeline features and stripping can all change the final compiled variant count.
			if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					var source = ctx != null ? ctx.GetAssetText(assetPath) : File.ReadAllText(assetPath);
					if (source == null) throw new IOException("Could not read shader source.");
					AnalyzeShaderPragmas(source,
						out facts.multiCompileLines,
						out facts.estimatedMultiCompileVariants,
						out facts.shaderFeatureLines,
						out facts.estimatedShaderFeatureVariants,
						out facts.duplicateKeywordSets,
						out facts.duplicateKeywordsInSets);
				}
				catch
				{
					facts.multiCompileLines = 0;
					facts.estimatedMultiCompileVariants = 0;
					facts.shaderFeatureLines = 0;
					facts.estimatedShaderFeatureVariants = 0;
					facts.duplicateKeywordSets = 0;
					facts.duplicateKeywordsInSets = 0;
				}
			}

			return facts;
		}

		AssetEntry AnalyzeShader(string assetPath, ScanContext ctx)
		{
			var facts = ctx.Cache != null
				? ctx.GetCached(assetPath, () => MeasureShader(assetPath, ctx))
				: MeasureShader(assetPath, ctx);
			if (facts == null) return null;

			// Everything the rest of the project decides is filled in here, every scan, whether the
			// facts came from the cache or were just measured.
			ApplyProjectState(assetPath, facts, out var liveKeywords);

			if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					var source = ctx.GetAssetText(assetPath);
					if (source == null) throw new IOException("Could not read shader source.");
					CollectKeywordCandidates(source, liveKeywords, m_ShadersUsingMaterialKeyword,
						facts.shaderName, facts.multiCompileMaterialCandidates, facts.localKeywordCandidates);
				}
				catch
				{
				}
			}

			var entry = NewEntry(assetPath, AssetCategory.Shader, "Shader", ctx);
			entry.Set("shader", facts.shaderName);
			entry.Set("passes", (long)facts.passCount);
			entry.Set("renderQueue", (long)facts.renderQueue);
			entry.Set("keywords", (long)facts.keywordCount);
			entry.Set("multiCompileLines", (long)facts.multiCompileLines);
			entry.Set("shaderFeatureLines", (long)facts.shaderFeatureLines);
			entry.Set("estimatedMultiCompileVariants", facts.estimatedMultiCompileVariants);
			entry.Set("estimatedShaderFeatureVariants", facts.estimatedShaderFeatureVariants);
			entry.Set("duplicateKeywordSets", (long)facts.duplicateKeywordSets);
			entry.Set("duplicateKeywordsInSets", (long)facts.duplicateKeywordsInSets);
			entry.Set("warnings", (long)facts.warningCount);
			entry.Set("supportedInEditor", facts.supportedInEditor ? "Yes" : "No");
			entry.Set("alwaysIncluded", facts.alwaysIncluded ? "Yes" : "No");
			entry.Set("materialCount", (long)facts.materialCount);
			entry.Set("materialKeywordsUsed", (long)facts.enabledMaterialKeywordCount);
			entry.Set("srpBatcher", facts.srpBatcherCompatibilityCode == 0 ? "Compatible" : "Incompatible");

			using (RuleTiming.Begin("SHD001", entry.Path))
				if (facts.hasError)
					entry.AddIssue("SHD001", facts.messageCount + " error(s)", "No errors");

			using (RuleTiming.Begin("SHD004", entry.Path))
				if (facts.hasWarnings)
					entry.AddIssue("SHD004", facts.warningCount + " warning(s)", "Review compiler warnings");

			using (RuleTiming.Begin("SHD005", entry.Path))
				if (!facts.supportedInEditor)
				{
					// Deliberately phrased as the editor's platform, because that is the only platform
					// ShaderUtil answers for. The optimizer's own target profile is a separate choice,
					// and reading an editor answer as a statement about that target is how a Windows
					// editor session comes to claim something about an Android build.
					entry.AddIssue("SHD005",
							"Unsupported on the editor's current platform (" + EditorPlatformName() + ")",
							"Check platform support for your build targets", 0, null,
							"Unity only reports shader support for the platform the editor is currently set to. "
							+ "This says nothing either way about "
							+ OptimizerSettings.Instance.Profile.DisplayName
							+ " - switch the editor's build target to check that.")
						?.Note("Optimizer target profile: " + OptimizerSettings.Instance.Profile.DisplayName
							   + ". Editor build target: " + EditorPlatformName() + ".", "Platforms");
				}

			bool multiCompileExceeded;
			using (RuleTiming.Begin("SHD002", entry.Path))
				multiCompileExceeded = facts.estimatedMultiCompileVariants > k_LargeVariantWarning;

			using (RuleTiming.Begin("SHD003", entry.Path))
				if (facts.passCount > k_LargePassWarning)
					entry.AddIssue("SHD003", facts.passCount + " passes", "Review whether every pass is required", 0, null,
						"A pass count alone does not prove a rendering cost: some passes are required by lighting, shadow, depth, meta or render-pipeline features.");

			using (RuleTiming.Begin("SHD006", entry.Path))
				if (facts.keywordCount > k_LargeLocalKeywordWarning)
					entry.AddIssue("SHD006",
						facts.keywordCount + " local keywords",
						"Reduce unnecessary keyword features");

			DedupeStrings(facts.multiCompileMaterialCandidates);
			DedupeStrings(facts.localKeywordCandidates);

			using (RuleTiming.Begin("SHD007", entry.Path))
				if (facts.multiCompileMaterialCandidates.Count > 0)
				{
					entry.AddIssue("SHD007",
						facts.multiCompileMaterialCandidates.Count + " multi_compile feature(s) have material-used keywords",
						"Review shader_feature conversion",
						0,
						null,
						"A runtime script may still need multi_compile, so this is intentionally advisory.")
						?.Items("Candidates", facts.multiCompileMaterialCandidates);
				}

			using (RuleTiming.Begin("SHD008", entry.Path))
				if (facts.localKeywordCandidates.Count > 0)
				{
					entry.AddIssue("SHD008",
						facts.localKeywordCandidates.Count + " global multi_compile feature(s) appear shader-local",
						"Review multi_compile_local conversion",
						0,
						null,
						"This is a static usage heuristic; global Shader.EnableKeyword or CommandBuffer keyword changes may still require global scope.")
						?.Items("Candidates", facts.localKeywordCandidates);
				}

			using (RuleTiming.Begin("SHD009", entry.Path))
				if (facts.alwaysIncluded && facts.estimatedMultiCompileVariants > k_LargeVariantWarning)
					entry.AddIssue("SHD009",
						"Always Included + ~" + facts.estimatedMultiCompileVariants + " multi_compile combinations",
						"Use targeted inclusion / variant collection");

			int keywordDirectiveFootprint = facts.keywordCount + facts.multiCompileLines;
			bool footprintExceeded;
			using (RuleTiming.Begin("SHD010", entry.Path))
				footprintExceeded = keywordDirectiveFootprint > 48;
			bool shaderFeatureExceeded;
			using (RuleTiming.Begin("SHD013", entry.Path))
				shaderFeatureExceeded = facts.estimatedShaderFeatureVariants > k_LargeVariantWarning;
			if (multiCompileExceeded || footprintExceeded || shaderFeatureExceeded)
			{
				var metrics = new List<GroupedFindingMetric>
				{
					new GroupedFindingMetric("SHD002", "Estimated multi_compile variant space",
						facts.multiCompileLines + " directive(s), ~" + facts.estimatedMultiCompileVariants + " combinations",
						multiCompileExceeded,
						facts.multiCompileLines + " multi_compile directive(s), ~" + facts.estimatedMultiCompileVariants + " combinations",
						"Review unnecessary global/runtime variant combinations"),
					new GroupedFindingMetric("SHD010", "Keyword/directive footprint (heuristic)",
						facts.keywordCount + " local keyword(s) + " + facts.multiCompileLines + " multi_compile directive(s) = " + keywordDirectiveFootprint + " footprint units; threshold > 48",
						footprintExceeded,
						keywordDirectiveFootprint + " keyword/directive footprint units",
						"Review keywords and directives separately; this is not a variant count"),
					new GroupedFindingMetric("SHD013", "Estimated shader_feature variant space",
						facts.shaderFeatureLines + " directive(s), ~" + facts.estimatedShaderFeatureVariants + " potential combinations",
						shaderFeatureExceeded,
						facts.shaderFeatureLines + " shader_feature directive(s), ~" + facts.estimatedShaderFeatureVariants + " potential combinations",
						"Review unnecessary feature sets and use local scope where appropriate")
				};
				GroupedFindingPolicy.AddGroupedIssue(entry, metrics,
					"Shader complexity is shown as one grouped insight with independent measurements. The multi_compile and shader_feature estimates are separate potential spaces, and the keyword/directive footprint is a heuristic; do not add these numbers together or treat them as an exact final-build variant count.");
			}

			// SHD011 remains registered for historical reports/settings but is not emitted: SHD007/SHD008 carry the actionable specific findings.

			using (RuleTiming.Begin("SHD014", entry.Path))
				if (facts.duplicateKeywordSets > 0 || facts.duplicateKeywordsInSets > 0)
				{
					entry.AddIssue("SHD014",
						facts.duplicateKeywordSets + " duplicate set(s), "
						+ facts.duplicateKeywordsInSets + " duplicate keyword name(s)",
						"Remove exact duplicate shader keyword declarations",
						0, c => CleanDuplicateShaderKeywords(c),
						"The fix removes duplicate keyword-set declarations only within the same shader program and removes repeated keyword names from individual sets. It preserves declarations in other passes, captures a backup, and re-imports the edited shader.",
						verify: c => VerifyNoDuplicateShaderKeywords(c));
				}

			entry.Finalise();
			return entry;
		}

		static int GetSrpBatcherCompatibilityCode(Shader shader)
		{
			if (shader == null) return 0;
			try
			{
				if (!s_SrpBatcherMethodResolved)
				{
					s_SrpBatcherMethodResolved = true;
					s_SrpBatcherMethod = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				}
				if (s_SrpBatcherMethod == null) return 0;
				object value = s_SrpBatcherMethod.Invoke(null, new object[] { shader });
				return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
			}
			catch
			{
				return 0;
			}
		}

		/// <summary>
		/// Fills in the parts of a shader's picture that the rest of the project decides: whether
		/// Graphics Settings force it into every build, how many materials use it, which keywords
		/// those materials turn on, and whether the editor's current platform supports it.
		/// <para>
		/// None of this is cached. All of it can change without the shader file being touched, and a
		/// value keyed on the shader's dependency hash would then be wrong until somebody happened to
		/// edit the shader.
		/// </para>
		/// </summary>
		void ApplyProjectState(string assetPath, ShaderFacts facts, out HashSet<string> materialKeywords)
		{
			facts.alwaysIncluded = m_AlwaysIncludedShaders.Contains(facts.shaderName);

			facts.materialCount = m_MaterialCountsByShader.TryGetValue(facts.shaderName, out int count)
				? count
				: 0;

			m_MaterialKeywordsByShader.TryGetValue(facts.shaderName, out materialKeywords);
			facts.enabledMaterialKeywordCount = materialKeywords?.Count ?? 0;

			// Support is read live rather than cached: it is a property of the editor's current
			// build target, not of the shader asset.
			try
			{
				var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
				facts.supportedInEditor = shader == null || ShaderUtil.GetShaderInfo(shader).supported;
			}
			catch
			{
				// Unknown is treated as supported: reporting an unsupported shader on the strength of
				// a failed query would be inventing the finding.
				facts.supportedInEditor = true;
			}
		}

		/// <summary>The editor's current build target, named the way the Build Settings window does.</summary>
		static string EditorPlatformName()
		{
			try { return EditorUserBuildSettings.activeBuildTarget.ToString(); }
			catch { return "unknown"; }
		}

		AssetEntry AnalyzeShaderVariantCollection(string assetPath, ScanContext ctx)
		{
			var collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(assetPath);
			if (collection == null) return null;

			var entry = NewEntry(assetPath, AssetCategory.Shader, "Shader Variant Collection", ctx);
			entry.Set("shaders", (long)collection.shaderCount);
			entry.Set("variants", (long)collection.variantCount);
			entry.Set("warmedUpVariants", (long)collection.warmedUpVariantCount);
			entry.Set("warmedUp", collection.isWarmedUp ? "Yes" : "No");
			entry.Set("warmupState", collection.isWarmedUp
				? "Collection has been warmed up in this editor session."
				: "Collection has not been warmed up in this editor session.");

			using (RuleTiming.Begin("SHD012", entry.Path))
				if (collection.variantCount > k_LargeVariantCollectionWarning)
				{
					entry.AddIssue("SHD012",
						collection.variantCount.ToString("N0") + " variants across " + collection.shaderCount + " shader(s)",
						"Review collected variants",
						0,
						null,
						"Large collections are not automatically wrong. Review whether recorded variants still correspond to current scenes, AssetBundles, Addressables and runtime keyword usage.");
				}

			entry.Finalise();
			return entry;
		}

		// --- project-level duplicate pass ---------------------------------------

		/// <summary>Short enough that a progress figure would only ever read as 0 or 1.</summary>
		public float Progress => -1f;

		public bool Enabled(OptimizerSettings settings) => settings.scanDuplicates;

		/// <summary>
		/// The cross-material passes.
		/// <para>
		/// Every entry these touch was already produced and returned by the per-asset pass, so they
		/// are mutated in place and <c>null</c> is yielded. Yielding the entry a second time would
		/// hand the scanner the same object twice and put two rows in the table for one material -
		/// each showing the same issues, and each counted separately in the totals.
		/// </para>
		/// </summary>
		public IEnumerable<AssetEntry> Analyze(ScanContext ctx)
		{
			foreach (var entry in ReportUsage(ctx)) yield return entry;

			foreach (var pair in m_BySignature)
			{
				var group = pair.Value;
				if (group == null || group.Count < 2) continue;

				var keeper = group[0];
				if (keeper == null) continue;
				for (int i = 1; i < group.Count; i++)
				{
					var entry = group[i];

					var others = new List<string>();
					for (int j = 0; j < group.Count; j++)
						if (j != i && j != 0) others.Add(group[j].Path);

					using (RuleTiming.Begin("MAT004", entry.Path))
						entry.AddIssue("MAT004", group.Count + " identical materials", "1 shared material",
								0, null,
								"The duplicate asset may be removable after all references are safely remapped. The analyzer does not claim the full file size as recoverable savings because serialized overhead and reference-remap effects must be verified.",
								SavingsBasis.Unknown)
							?.Good("Share this one instead", keeper.Path, keeper.Path)
							.Items("Also identical", others, DetailKind.Bad, arePaths: true);
					entry.Finalise();
					yield return null;
				}
			}

			foreach (var pair in m_ByFamily)
			{
				var group = pair.Value;
				if (group == null || group.Count < 3) continue;

				// Exact duplicates already have MAT004. Only report the family structure where
				// there are multiple distinct materials sharing the same shader/texture/keyword base.
				foreach (var entry in group)
				{
					using (RuleTiming.Begin("MAT009", entry.Path))
						entry.AddIssue("MAT009",
							group.Count + " materials share the same shader/texture/keyword family",
							"Consider Material Variants / shared base material",
							0,
							null,
							"This is a structural suggestion. Different material values are intentionally allowed and no automatic merge is attempted.")
							?.Items("Family members", CollectPaths(group), DetailKind.Bad, arePaths: true);
					entry.Finalise();
					yield return null;
				}
			}
		}

		/// <summary>
		/// Writes how heavily each material and shader is actually used onto its row, once the
		/// prefab and scene walk has finished counting.
		/// <para>
		/// This is what turns "GPU instancing is off" into a finding someone can rank. A material on
		/// four hundred renderers and a material on one are the same rule and completely different
		/// decisions, and until the counts existed the tool could not tell them apart - so it
		/// reported both with equal weight and neither with any evidence.
		/// </para>
		/// </summary>
		IEnumerable<AssetEntry> ReportUsage(ScanContext ctx)
		{
			var usage = ctx.MaterialUsage;
			if (usage == null || usage.IsEmpty) yield break;

			// Renderer counts per shader are summed from its materials, so a shader's weight is the
			// weight of everything drawing with it.
			var shaderRenderers = new Dictionary<string, int>(StringComparer.Ordinal);

			foreach (var pair in ctx.EntriesByPath)
			{
				var entry = pair.Value;
				if (entry == null || entry.Category != AssetCategory.Material) continue;

				var counts = usage.For(entry.Path);
				entry.Set("prefabSlots", (long)counts.PrefabSlots);
				entry.Set("sceneSlots", (long)counts.SceneSlots);
				entry.Set("prefabs", (long)counts.Prefabs);
				entry.Set("scenes", (long)counts.Scenes);

				// Ranking uses the scene figure, because that is the one that reflects how much of
				// the material is actually placed in the world. It is still a static count and is
				// labelled as one wherever it is shown.
				var shaderName = entry.Field("shader") as string;
				if (!string.IsNullOrEmpty(shaderName) && counts.Any)
				{
					shaderRenderers.TryGetValue(shaderName, out int total);
					shaderRenderers[shaderName] = total + counts.SceneSlots;
				}

				// The evidence goes on the existing findings rather than into a new rule: how much a
				// material is used is not itself a problem, it is what makes the problems already
				// reported on it worth someone's afternoon.
				if (counts.Any)
				{
					for (int i = 0; i < entry.Issues.Count; i++)
					{
						var issue = entry.Issues[i];
						if (issue.RuleId != "MAT003" && issue.RuleId != "MAT004" && issue.RuleId != "MAT009")
							continue;

						issue.Note(counts.Describe()
								   + (usage.Partial ? " (within this scan's scope only)" : ""), "Used by");
					}
				}
			}

			yield return null;

			foreach (var pair in ctx.EntriesByPath)
			{
				var entry = pair.Value;
				if (entry == null || entry.Category != AssetCategory.Shader) continue;

				var shaderName = entry.Field("shader") as string;
				if (string.IsNullOrEmpty(shaderName)) continue;

				shaderRenderers.TryGetValue(shaderName, out int renderers);
				entry.Set("renderers", (long)renderers);

				if (renderers <= 0) continue;

				m_MaterialKeywordsByShader.TryGetValue(shaderName, out var keywords);
				int keywordStates = keywords?.Count ?? 0;

				for (int i = 0; i < entry.Issues.Count; i++)
				{
					var issue = entry.Issues[i];
					issue.Note(renderers + " renderer slot(s) in scenes, across "
							   + (entry.Field("materialCount") is long materials ? materials : 0)
							   + " material(s), with " + keywordStates + " distinct keyword state(s)"
							   + (usage.Partial ? " (within this scan's scope only)" : ""), "Used by");
				}
			}

			yield return null;
		}

		void RegisterFamily(string familySignature, AssetEntry entry)
		{
			if (string.IsNullOrEmpty(familySignature)) return;
			if (!m_ByFamily.TryGetValue(familySignature, out var list))
			{
				list = new List<AssetEntry>();
				m_ByFamily[familySignature] = list;
			}
			list.Add(entry);

		}

		static List<string> FindUnsupportedKeywords(Material material)
		{
			var result = new List<string>();
			if (material == null || material.shader == null) return result;

			var keywordSpace = material.shader.keywordSpace;
			var enabled = material.enabledKeywords;
			for (int i = 0; i < enabled.Length; i++)
			{
				var keyword = enabled[i];
				if (!keyword.isValid || !keywordSpace.FindKeyword(keyword.name).isValid)
					result.Add(keyword.name);
			}
			return result;
		}

		static bool HasEmissionGIMismatch(Material material)
		{
			if (material == null || material.shader == null)
				return false;

			MaterialGlobalIlluminationFlags flags = material.globalIlluminationFlags;
			if ((flags & MaterialGlobalIlluminationFlags.AnyEmissive) == 0)
				return false;

			if (!TryGetEmissionColor(material, out Color emission))
				return false;

			return emission.maxColorComponent <= 1e-5f;
		}

		static bool TryGetEmissionColor(Material material, out Color color)
		{
			color = Color.black;
			string[] names = { "_EmissionColor", "_EmissiveColor", "_Emission" };
			for (int i = 0; i < names.Length; i++)
			{
				if (material.HasProperty(names[i]))
				{
					color = material.GetColor(names[i]);
					return true;
				}
			}
			return false;
		}

		static string BuildFamilySignature(Material material)
		{
			try
			{
				var shader = material.shader;
				if (shader == null) return null;

				var sb = new StringBuilder(512);
				sb.Append("shader=").Append(shader.name);
				sb.Append("|rawQ=").Append(material.rawRenderQueue);
				sb.Append("|inst=").Append(material.enableInstancing ? 1 : 0);

				var keywords = material.enabledKeywords;
				var keywordNames = new List<string>(keywords.Length);
				for (int i = 0; i < keywords.Length; i++)
					keywordNames.Add(keywords[i].name);
				keywordNames.Sort(StringComparer.Ordinal);
				for (int i = 0; i < keywordNames.Count; i++)
					sb.Append("|kw=").Append(keywordNames[i]);

				var properties = GetShaderProperties(shader);
				for (int i = 0; i < properties.Length; i++)
				{
					string name = properties[i].name;
					var type = properties[i].type;
					if (type != ShaderPropertyType.Texture) continue;

					sb.Append("|tex=").Append(name).Append('=');
					var tex = material.GetTexture(name);
					sb.Append(tex != null ? AssetDatabase.GetAssetPath(tex) : "-");
					sb.Append("|scale=");
					AppendVector2(sb, material.GetTextureScale(name));
					sb.Append("|offset=");
					AppendVector2(sb, material.GetTextureOffset(name));
				}

				return sb.ToString();
			}
			catch
			{
				return null;
			}
		}

		static List<string> CollectPaths(List<AssetEntry> group)
		{
			var paths = new List<string>(group.Count);
			for (int i = 0; i < group.Count; i++)
				paths.Add(group[i].Path);
			return paths;
		}

		void CollectAlwaysIncludedShaders()
		{
			try
			{
				string path = "ProjectSettings/GraphicsSettings.asset";
				var objects = AssetDatabase.LoadAllAssetsAtPath(path);
				if (objects == null || objects.Length == 0) return;

				var so = new SerializedObject(objects[0]);
				var shaders = so.FindProperty("m_AlwaysIncludedShaders");
				if (shaders == null || !shaders.isArray) return;

				for (int i = 0; i < shaders.arraySize; i++)
				{
					var element = shaders.GetArrayElementAtIndex(i);
					var shader = element.objectReferenceValue as Shader;
					if (shader != null)
						m_AlwaysIncludedShaders.Add(shader.name);
				}
			}
			catch
			{
			}
		}

		// --- helpers --------------------------------------------------------------

		static AssetEntry NewEntry(string assetPath, AssetCategory category, string typeLabel, ScanContext ctx)
		{
			return new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = category,
				TypeLabel = typeLabel,
				FileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath)
			};
		}

		static bool IsBuiltInShader(string shaderName)
		{
			for (int i = 0; i < k_BuiltInNames.Length; i++)
				if (shaderName == k_BuiltInNames[i]) return true;
			for (int i = 0; i < k_BuiltInPrefixes.Length; i++)
				if (shaderName.StartsWith(k_BuiltInPrefixes[i], StringComparison.Ordinal)) return true;
			return false;
		}

		ShaderCapabilityFacts GetShaderCapabilityFacts(Shader shader)
		{
			if (shader == null) return new ShaderCapabilityFacts();
			int id = shader.GetInstanceID();
			if (m_ShaderCapabilities.TryGetValue(id, out var cached)) return cached;
			var facts = new ShaderCapabilityFacts
			{
				keywordCount = SafeShaderKeywordCount(shader),
				instancingSupported = SupportsGpuInstancing(shader),
				srpBatcherCompatibilityCode = GetSrpBatcherCompatibilityCode(shader)
			};
			m_ShaderCapabilities[id] = facts;
			return facts;
		}

		static ShaderPropertyDescriptor[] GetShaderProperties(Shader shader)
		{
			if (shader == null) return Array.Empty<ShaderPropertyDescriptor>();
			int id = shader.GetInstanceID();
			if (s_ShaderProperties.TryGetValue(id, out var cached)) return cached;
			try
			{
				int count = shader.GetPropertyCount();
				var properties = new ShaderPropertyDescriptor[count];
				for (int i = 0; i < count; i++)
					properties[i] = new ShaderPropertyDescriptor { name = shader.GetPropertyName(i), type = shader.GetPropertyType(i) };
				s_ShaderProperties[id] = properties;
				return properties;
			}
			catch
			{
				return Array.Empty<ShaderPropertyDescriptor>();
			}
		}

		static int SafeShaderKeywordCount(Shader shader)
		{
			if (shader == null) return 0;
			try
			{
				uint count = shader.keywordSpace.keywordCount;
				return count > int.MaxValue ? int.MaxValue : (int)count;
			}
			catch { return 0; }
		}

		static bool SupportsGpuInstancing(Shader shader)
		{
			if (shader == null) return false;
			try
			{
				var keywords = shader.keywordSpace.keywords;
				for (int i = 0; i < keywords.Length; i++)
				{
					if (string.Equals(keywords[i].name, "INSTANCING_ON", StringComparison.Ordinal))
						return true;
				}
			}
			catch { }
			return false;
		}

		static void EstimateMultiCompileVariants(string source, out int directiveCount, out long estimatedVariants)
		{
			AnalyzeShaderPragmas(source, out directiveCount, out estimatedVariants,
				out _, out _, out _, out _);
		}

		sealed class ShaderKeywordPragma
		{
			public int LineIndex;
			public int Scope;
			public string Directive;
			public List<string> Tokens;
			public string Key;
			public List<int> DuplicateTokenIndices;
		}

		static readonly HashSet<string> k_ShaderKeywordPragmaDirectives = new HashSet<string>(StringComparer.Ordinal)
		{
			"multi_compile", "multi_compile_local", "multi_compile_fragment", "multi_compile_vertex",
			"multi_compile_local_fragment", "multi_compile_local_vertex",
			"shader_feature", "shader_feature_local", "shader_feature_local_fragment", "shader_feature_local_vertex"
		};

		static void AnalyzeShaderPragmas(string source,
			out int multiCompileLines, out long estimatedMultiCompileVariants,
			out int shaderFeatureLines, out long estimatedShaderFeatureVariants,
			out int duplicateKeywordSets, out int duplicateKeywordsInSets)
		{
			multiCompileLines = 0;
			estimatedMultiCompileVariants = 0;
			shaderFeatureLines = 0;
			estimatedShaderFeatureVariants = 0;
			duplicateKeywordSets = 0;
			duplicateKeywordsInSets = 0;
			if (string.IsNullOrEmpty(source)) return;

			var pragmas = ParseShaderKeywordPragmas(source);
			var seenSetsByScope = new Dictionary<int, HashSet<string>>();
			for (int i = 0; i < pragmas.Count; i++)
			{
				var pragma = pragmas[i];
				if (pragma.Scope >= 0)
				{
					if (!seenSetsByScope.TryGetValue(pragma.Scope, out var seenSets))
					{
						seenSets = new HashSet<string>(StringComparer.Ordinal);
						seenSetsByScope.Add(pragma.Scope, seenSets);
					}

					if (!seenSets.Add(pragma.Key)) duplicateKeywordSets++;
					duplicateKeywordsInSets += pragma.DuplicateTokenIndices.Count;
				}

				long variants = Math.Max(2, CountKeywordOptions(pragma.Tokens));
				if (pragma.Directive.StartsWith("multi_compile", StringComparison.Ordinal))
				{
					multiCompileLines++;
					estimatedMultiCompileVariants = SaturatingMultiply(
						estimatedMultiCompileVariants == 0 ? 1 : estimatedMultiCompileVariants, variants);
				}
				else
				{
					shaderFeatureLines++;
					estimatedShaderFeatureVariants = SaturatingMultiply(
						estimatedShaderFeatureVariants == 0 ? 1 : estimatedShaderFeatureVariants, variants);
				}
			}
		}

		static List<ShaderKeywordPragma> ParseShaderKeywordPragmas(string source)
		{
			var result = new List<ShaderKeywordPragma>();
			if (string.IsNullOrEmpty(source)) return result;

			string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			int scope = -1;
			int nextScope = 0;
			var conditionStack = new List<string>();

			for (int i = 0; i < lines.Length; i++)
			{
				string trimmed = lines[i].Trim();
				if (trimmed.Equals("HLSLPROGRAM", StringComparison.OrdinalIgnoreCase) ||
					trimmed.Equals("CGPROGRAM", StringComparison.OrdinalIgnoreCase))
				{
					scope = nextScope++;
					conditionStack.Clear();
					continue;
				}
				if (trimmed.Equals("ENDHLSL", StringComparison.OrdinalIgnoreCase) ||
					trimmed.Equals("ENDCG", StringComparison.OrdinalIgnoreCase))
				{
					scope = -1;
					conditionStack.Clear();
					continue;
				}

				string directiveLine = StripPragmaComment(trimmed);
				if (scope >= 0) UpdateConditionContext(directiveLine, conditionStack);
				if (scope < 0) continue;

				if (!directiveLine.StartsWith("#pragma", StringComparison.OrdinalIgnoreCase)) continue;
				string pragma = directiveLine.Substring("#pragma".Length).Trim();
				int space = pragma.IndexOfAny(new[] { ' ', '\t' });
				string directive = space >= 0 ? pragma.Substring(0, space).Trim().ToLowerInvariant() : pragma.ToLowerInvariant();
				if (!k_ShaderKeywordPragmaDirectives.Contains(directive)) continue;
				string args = space >= 0 ? pragma.Substring(space + 1).Trim() : string.Empty;
				var tokens = TokenizePragmaArguments(args);
				if (tokens.Count == 0) continue;

				var duplicateTokenIndices = new List<int>();
				var names = new HashSet<string>(StringComparer.Ordinal);
				for (int t = 0; t < tokens.Count; t++)
				{
					string token = tokens[t];
					if (token == "_" || token == "__") continue;
					if (!names.Add(token)) duplicateTokenIndices.Add(t);
				}

				var normalizedTokens = new List<string>(tokens);
				normalizedTokens.Sort(StringComparer.Ordinal);
				string conditionContext = BuildConditionContextKey(conditionStack);
				string setKey = directive + "|" + conditionContext + "|" + string.Join("\u001f", normalizedTokens);
				result.Add(new ShaderKeywordPragma
				{
					LineIndex = i,
					Scope = scope,
					Directive = directive,
					Tokens = tokens,
					Key = setKey,
					DuplicateTokenIndices = duplicateTokenIndices
				});
			}

			return result;
		}

		static void UpdateConditionContext(string line, List<string> conditionStack)
		{
			if (string.IsNullOrEmpty(line) || !line.StartsWith("#", StringComparison.Ordinal)) return;
			string directive = line.Substring(1).Trim();
			if (directive.StartsWith("ifdef ", StringComparison.OrdinalIgnoreCase))
			{
				conditionStack.Add("ifdef " + directive.Substring(6).Trim());
				return;
			}
			if (directive.StartsWith("ifndef ", StringComparison.OrdinalIgnoreCase))
			{
				conditionStack.Add("ifndef " + directive.Substring(7).Trim());
				return;
			}
			if (directive.StartsWith("if ", StringComparison.OrdinalIgnoreCase))
			{
				conditionStack.Add("if " + directive.Substring(3).Trim());
				return;
			}
			if (directive.StartsWith("elif ", StringComparison.OrdinalIgnoreCase))
			{
				if (conditionStack.Count > 0) conditionStack[conditionStack.Count - 1] = "elif " + directive.Substring(5).Trim();
				return;
			}
			if (directive.Equals("else", StringComparison.OrdinalIgnoreCase))
			{
				if (conditionStack.Count > 0) conditionStack[conditionStack.Count - 1] = "else";
				return;
			}
			if (directive.StartsWith("endif", StringComparison.OrdinalIgnoreCase))
			{
				if (conditionStack.Count > 0) conditionStack.RemoveAt(conditionStack.Count - 1);
			}
		}

		static string BuildConditionContextKey(List<string> conditionStack)
		{
			return conditionStack == null || conditionStack.Count == 0
				? string.Empty
				: string.Join("\u001e", conditionStack.ToArray());
		}

		static string StripPragmaComment(string text)
		{
			if (string.IsNullOrEmpty(text)) return string.Empty;
			int comment = text.IndexOf("//", StringComparison.Ordinal);
			return comment >= 0 ? text.Substring(0, comment) : text;
		}

		static List<string> TokenizePragmaArguments(string args)
		{
			var result = new List<string>();
			if (string.IsNullOrEmpty(args)) return result;
			var parts = args.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < parts.Length; i++)
			{
				string token = parts[i].Trim();
				if (token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"')
					token = token.Substring(1, token.Length - 2);
				if (token.Length > 0) result.Add(token);
			}
			return result;
		}

		static int CountKeywordOptions(List<string> tokens)
		{
			if (tokens == null || tokens.Count == 0) return 0;
			int count = 0;
			for (int i = 0; i < tokens.Count; i++)
				if (tokens[i] != "_" && tokens[i] != "__") count++;
			return Math.Max(2, count + (tokens.Contains("_") || tokens.Contains("__") ? 1 : 0));
		}

		static long SaturatingMultiply(long a, long b)
		{
			const long max = 1000000000L;
			if (a <= 0 || b <= 0) return 0;
			return a > max / b ? max : Math.Min(max, a * b);
		}

		static void DedupeStrings(List<string> values)
		{
			if (values == null || values.Count < 2) return;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			int write = 0;
			for (int i = 0; i < values.Count; i++)
				if (seen.Add(values[i])) values[write++] = values[i];
			if (write < values.Count) values.RemoveRange(write, values.Count - write);
		}

		static void CollectKeywordCandidates(
			string source,
			HashSet<string> materialKeywords,
			Dictionary<string, HashSet<string>> shaderUsersByKeyword,
			string shaderName,
			List<string> materialCandidates,
			List<string> localCandidates)
		{
			if (string.IsNullOrEmpty(source)) return;

			var lines = source.Split(new[] { '\r', '\n' });
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim();
				if (!line.StartsWith("#pragma", StringComparison.Ordinal))
					continue;

				bool globalMulti =
					line.StartsWith("#pragma multi_compile ", StringComparison.Ordinal) ||
					line.StartsWith("#pragma multi_compile_fragment ", StringComparison.Ordinal) ||
					line.StartsWith("#pragma multi_compile_vertex ", StringComparison.Ordinal);

				bool localMulti =
					line.StartsWith("#pragma multi_compile_local ", StringComparison.Ordinal) ||
					line.StartsWith("#pragma multi_compile_local_fragment ", StringComparison.Ordinal) ||
					line.StartsWith("#pragma multi_compile_local_vertex ", StringComparison.Ordinal);

				if (!globalMulti && !localMulti)
					continue;

				int firstSpace = line.IndexOf(' ');
				if (firstSpace < 0) continue;
				int secondSpace = line.IndexOf(' ', firstSpace + 1);
				if (secondSpace < 0) continue;

				string args = line.Substring(secondSpace + 1).Trim();
				if (args.Length == 0) continue;

				var tokens = args.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
				bool hasMaterialKeyword = false;
				bool allKeywordsAreShaderLocal = true;

				for (int t = 0; t < tokens.Length; t++)
				{
					string token = tokens[t];
					if (token == "_" || token == "__" || token.Length == 0)
						continue;

					if (materialKeywords != null && materialKeywords.Contains(token))
						hasMaterialKeyword = true;

					if (shaderUsersByKeyword.TryGetValue(token, out var users))
					{
						if (users.Count != 1 || !users.Contains(shaderName))
							allKeywordsAreShaderLocal = false;
					}
					else
					{
						// A keyword with no material usage cannot establish shader-locality.
						allKeywordsAreShaderLocal = false;
					}
				}

				string declaration = line.Substring(line.IndexOf("multi_compile", StringComparison.Ordinal));
				if (hasMaterialKeyword)
					materialCandidates.Add(declaration);

				if (globalMulti && allKeywordsAreShaderLocal)
					localCandidates.Add(declaration);
			}
		}

		void CollectMaterialUsageIndex()
		{
			try
			{
				foreach (var guid in AssetDatabase.FindAssets("t:Material"))
				{
					var path = AssetDatabase.GUIDToAssetPath(guid);
					var material = AssetDatabase.LoadAssetAtPath<Material>(path);
					if (material == null || material.shader == null)
						continue;

					string shaderName = material.shader.name;
					if (!m_MaterialCountsByShader.TryGetValue(shaderName, out int count))
						count = 0;
					m_MaterialCountsByShader[shaderName] = count + 1;

					if (!m_MaterialKeywordsByShader.TryGetValue(shaderName, out var keywords))
					{
						keywords = new HashSet<string>(StringComparer.Ordinal);
						m_MaterialKeywordsByShader[shaderName] = keywords;
					}

					var enabled = material.enabledKeywords;
					for (int i = 0; i < enabled.Length; i++)
					{
						string keyword = enabled[i].name;
						if (string.IsNullOrEmpty(keyword))
							continue;

						keywords.Add(keyword);

						if (!m_ShadersUsingMaterialKeyword.TryGetValue(keyword, out var users))
						{
							users = new HashSet<string>(StringComparer.Ordinal);
							m_ShadersUsingMaterialKeyword[keyword] = users;
						}
						users.Add(shaderName);
					}
				}
			}
			catch
			{
				// Material usage is an optimization hint; a project indexing failure must not
				// prevent the underlying material/shader analyzers from running.
			}
		}

		static bool IsUiShader(string shaderName)
		{
			return shaderName.StartsWith("UI/", StringComparison.Ordinal)
				   || shaderName.StartsWith("Sprites/", StringComparison.Ordinal)
				   || shaderName.StartsWith("TextMeshPro/", StringComparison.Ordinal)
				   || shaderName.StartsWith("Hidden/", StringComparison.Ordinal);
		}

		static List<string> FindOrphanTextureProperties(Material material)
		{
			var orphans = new List<string>();
			var so = new SerializedObject(material);
			var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
			if (texEnvs == null || !texEnvs.isArray) return orphans;

			for (int i = 0; i < texEnvs.arraySize; i++)
			{
				var element = texEnvs.GetArrayElementAtIndex(i);
				var name = element.FindPropertyRelative("first")?.stringValue;
				if (string.IsNullOrEmpty(name)) continue;
				if (material.HasProperty(name)) continue;

				var texture = element.FindPropertyRelative("second.m_Texture");
				if (texture != null && texture.objectReferenceValue != null)
					orphans.Add(name);
			}
			return orphans;
		}

		static void StripOrphanTextureProperties(Material material, FixContext c)
		{
			if (material == null)
				throw new InvalidOperationException("Material could not be loaded for orphan-property cleanup.");
			var so = new SerializedObject(material);
			var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
			if (texEnvs == null || !texEnvs.isArray) return;

			bool changed = false;
			for (int i = texEnvs.arraySize - 1; i >= 0; i--)
			{
				var element = texEnvs.GetArrayElementAtIndex(i);
				var name = element.FindPropertyRelative("first")?.stringValue;
				if (string.IsNullOrEmpty(name) || material.HasProperty(name)) continue;
				texEnvs.DeleteArrayElementAtIndex(i);
				changed = true;
			}

			if (changed)
			{
				so.ApplyModifiedPropertiesWithoutUndo();
				c.NeedsSave = true;
			}
		}

		/// <summary>
		/// Only claims a material is needlessly transparent when opacity can be positively proven.
		/// <para>
		/// Restricted to the stock URP/Standard shader families on purpose. A custom or VFX shader
		/// may well expose _Surface while taking its alpha from vertex colour, a custom texture slot
		/// or an additive blend - none of which are visible here, so "no _BaseColor and no _BaseMap"
		/// means "cannot tell", not "opaque".
		/// </para>
		/// </summary>
		static void MakeOpaqueMaterial(FixContext c)
		{
			var material = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null) throw new InvalidOperationException("Material unavailable.");
			if (!IsPointlesslyTransparent(material))
				throw new InvalidOperationException("Material is no longer in the proven transparent-but-opaque state.");

			material.SetFloat("_Surface", 0f);
			SetMaterialFloatIfPresent(material, "_Blend", 0f);
			SetMaterialFloatIfPresent(material, "_SrcBlend", 1f);
			SetMaterialFloatIfPresent(material, "_DstBlend", 0f);
			SetMaterialFloatIfPresent(material, "_SrcBlendAlpha", 1f);
			SetMaterialFloatIfPresent(material, "_DstBlendAlpha", 0f);
			SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
			material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
			material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			material.DisableKeyword("_ALPHAMODULATE_ON");
			EditorUtility.SetDirty(material);
			c.NeedsSave = true;
		}

		static bool VerifyOpaqueMaterial(FixContext c)
		{
			var material = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || !material.HasProperty("_Surface")) return false;
			if (Mathf.RoundToInt(material.GetFloat("_Surface")) != 0) return false;
			if (material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")) return false;
			if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) return false;
			if (material.IsKeywordEnabled("_ALPHAMODULATE_ON")) return false;
			return true;
		}

		static void SetMaterialFloatIfPresent(Material material, string property, float value)
		{
			if (material != null && material.HasProperty(property)) material.SetFloat(property, value);
		}

		static bool IsPointlesslyTransparent(Material material)
		{
			var shader = material.shader;
			if (shader == null) return false;

			bool stockShader = shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal)
							   || shader.name.StartsWith("HDRP/", StringComparison.Ordinal)
							   || shader.name.StartsWith("Standard", StringComparison.Ordinal);
			if (!stockShader) return false;

			if (!material.HasProperty("_Surface")) return false;
			if (Mathf.RoundToInt(material.GetFloat("_Surface")) != 1) return false;

			// The tint has to exist and be fully opaque.
			bool haveTint = false;
			if (material.HasProperty("_BaseColor"))
			{
				if (material.GetColor("_BaseColor").a < 0.999f) return false;
				haveTint = true;
			}
			else if (material.HasProperty("_Color"))
			{
				if (material.GetColor("_Color").a < 0.999f) return false;
				haveTint = true;
			}
			if (!haveTint) return false;

			// The albedo texture has to exist and be provably alpha-free.
			var baseMap = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap")
				: material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
			if (baseMap == null) return false;

			var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(baseMap)) as TextureImporter;
			if (importer == null) return false;
			try { if (importer.DoesSourceTextureHaveAlpha()) return false; }
			catch { return false; }

			return true;
		}

		void RegisterSignature(string signature, AssetEntry entry)
		{
			if (signature == null) return;
			if (!m_BySignature.TryGetValue(signature, out var list))
			{
				list = new List<AssetEntry>();
				m_BySignature[signature] = list;
			}
			list.Add(entry);
		}

		/// <summary>Shader name plus every serialized property value, so equivalent materials hash identically.</summary>
		static string BuildSignature(Material material)
		{
			try
			{
				var shader = material.shader;
				if (shader == null) return null;

				var sb = new StringBuilder(1024);
				sb.Append("shader=").Append(shader.name);
				sb.Append("|rawQ=").Append(material.rawRenderQueue);
				sb.Append("|inst=").Append(material.enableInstancing ? 1 : 0);
				sb.Append("|gi=").Append((int)material.globalIlluminationFlags);
				sb.Append("|dsgi=").Append(material.doubleSidedGI ? 1 : 0);

				var keywords = material.enabledKeywords;
				var keywordNames = new List<string>(keywords.Length);
				for (int i = 0; i < keywords.Length; i++)
					keywordNames.Add(keywords[i].name);
				keywordNames.Sort(StringComparer.Ordinal);
				for (int i = 0; i < keywordNames.Count; i++)
					sb.Append("|kw=").Append(keywordNames[i]);

				var properties = GetShaderProperties(shader);
				for (int i = 0; i < properties.Length; i++)
				{
					string name = properties[i].name;
					var type = properties[i].type;
					sb.Append("|p=").Append(name).Append(':').Append(type).Append('=');

					switch (type)
					{
						case ShaderPropertyType.Color:
							AppendVector(sb, material.GetColor(name));
							break;

						case ShaderPropertyType.Vector:
							AppendVector(sb, material.GetVector(name));
							break;

						case ShaderPropertyType.Float:
						case ShaderPropertyType.Range:
							sb.Append(material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture));
							break;

						case ShaderPropertyType.Int:
							sb.Append(material.GetInteger(name));
							break;

						case ShaderPropertyType.Texture:
							var texture = material.GetTexture(name);
							sb.Append(texture != null ? AssetDatabase.GetAssetPath(texture) : "-");
							sb.Append("|scale=");
							AppendVector2(sb, material.GetTextureScale(name));
							sb.Append("|offset=");
							AppendVector2(sb, material.GetTextureOffset(name));
							break;
					}
				}

				return sb.ToString();
			}
			catch
			{
				return null;
			}
		}

		static void AppendVector(StringBuilder sb, Vector4 value)
		{
			sb.Append(value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(value.y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(value.z.ToString("R", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(value.w.ToString("R", CultureInfo.InvariantCulture));
		}

		static void AppendVector2(StringBuilder sb, Vector2 value)
		{
			sb.Append(value.x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(value.y.ToString("R", CultureInfo.InvariantCulture));
		}
		static bool NoProjectShaderKeywordUse(ScanContext ctx, string assetPath, List<string> keywords)
		{
			if (ctx == null || !ctx.Settings.projectProofPass || keywords == null || keywords.Count == 0) return false;
			var index = ctx.GetProofIndex();
			if (!index.IsBuilt || index.BuildFailed) return false;
			for (int i = 0; i < keywords.Count; i++)
				if (index.ContainsTokenOutsideFile(keywords[i], assetPath)) return false;
			return true;
		}
		static void RemoveUnsupportedMaterialKeywords(FixContext c, List<string> keywords)
		{
			var mat = c.Asset as Material; if (mat == null) throw new InvalidOperationException("Material unavailable.");
			var stale = new HashSet<string>(keywords ?? new List<string>(), StringComparer.Ordinal);
			var enabled = mat.enabledKeywords;
			for (int i = 0; i < enabled.Length; i++)
				if (stale.Contains(enabled[i].name))
					mat.SetKeyword(enabled[i], false);
			c.NeedsSave = true;
		}
		static bool VerifyNoUnsupportedMaterialKeywords(FixContext c, List<string> keywords)
		{
			var mat = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (mat == null) return false;
			var enabled = mat.enabledKeywords;
			var stale = new HashSet<string>(keywords ?? new List<string>(), StringComparer.Ordinal);
			for (int i = 0; i < enabled.Length; i++)
				if (stale.Contains(enabled[i].name)) return false;
			return true;
		}

		static void CleanDuplicateShaderKeywords(FixContext c)
		{
			string text = System.IO.File.ReadAllText(c.AssetPath);
			string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			var pragmas = ParseShaderKeywordPragmas(text);
			var seenSetsByScope = new Dictionary<int, HashSet<string>>();
			var removeLines = new HashSet<int>();
			var rewrittenLines = new Dictionary<int, string>();

			for (int i = 0; i < pragmas.Count; i++)
			{
				var pragma = pragmas[i];
				if (pragma.Scope < 0) continue;
				if (!seenSetsByScope.TryGetValue(pragma.Scope, out var seenSets))
				{
					seenSets = new HashSet<string>(StringComparer.Ordinal);
					seenSetsByScope.Add(pragma.Scope, seenSets);
				}

				if (!seenSets.Add(pragma.Key))
				{
					removeLines.Add(pragma.LineIndex);
					continue;
				}

				if (pragma.DuplicateTokenIndices.Count > 0)
					rewrittenLines[pragma.LineIndex] = RewriteShaderKeywordPragmaLine(lines[pragma.LineIndex], pragma);
			}

			if (removeLines.Count == 0 && rewrittenLines.Count == 0) return;

			var output = new StringBuilder(normalized.Length);
			for (int i = 0; i < lines.Length; i++)
			{
				if (removeLines.Contains(i)) continue;
				if (rewrittenLines.TryGetValue(i, out var rewritten)) output.Append(rewritten);
				else output.Append(lines[i]);
				if (i < lines.Length - 1) output.Append('\n');
			}

			c.RequireBackup(c.AssetPath);
			string finalText = output.ToString();
			if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) finalText = finalText.Replace("\n", "\r\n");
			System.IO.File.WriteAllText(c.AssetPath, finalText, new UTF8Encoding(false));
			AssetDatabase.ImportAsset(c.AssetPath, ImportAssetOptions.ForceUpdate);
			c.NeedsReimport = true;
		}

		static string RewriteShaderKeywordPragmaLine(string line, ShaderKeywordPragma pragma)
		{
			int pragmaIndex = line.IndexOf("#pragma", StringComparison.OrdinalIgnoreCase);
			if (pragmaIndex < 0) return line;
			int directiveIndex = pragmaIndex + "#pragma".Length;
			while (directiveIndex < line.Length && char.IsWhiteSpace(line[directiveIndex])) directiveIndex++;
			int argsIndex = directiveIndex;
			while (argsIndex < line.Length && !char.IsWhiteSpace(line[argsIndex])) argsIndex++;
			int argsStart = argsIndex;
			while (argsStart < line.Length && char.IsWhiteSpace(line[argsStart])) argsStart++;
			int commentIndex = line.IndexOf("//", argsStart, StringComparison.Ordinal);
			string comment = commentIndex >= 0 ? line.Substring(commentIndex) : string.Empty;
			string prefix = line.Substring(0, argsStart);
			var duplicateIndices = new HashSet<int>(pragma.DuplicateTokenIndices);
			var kept = new List<string>(pragma.Tokens.Count - pragma.DuplicateTokenIndices.Count);
			for (int i = 0; i < pragma.Tokens.Count; i++)
				if (!duplicateIndices.Contains(i)) kept.Add(pragma.Tokens[i]);
			string rebuiltArgs = string.Join(" ", kept.ToArray());
			return prefix + rebuiltArgs + (string.IsNullOrEmpty(comment) ? string.Empty : " " + comment);
		}

		static bool VerifyNoDuplicateShaderKeywords(FixContext c)
		{
			string text;
			try { text = System.IO.File.ReadAllText(c.AssetPath); } catch { return false; }
			var pragmas = ParseShaderKeywordPragmas(text);
			var seenSetsByScope = new Dictionary<int, HashSet<string>>();
			for (int i = 0; i < pragmas.Count; i++)
			{
				var pragma = pragmas[i];
				if (pragma.Scope < 0) continue;
				if (pragma.DuplicateTokenIndices.Count > 0) return false;
				if (!seenSetsByScope.TryGetValue(pragma.Scope, out var seenSets))
				{
					seenSets = new HashSet<string>(StringComparer.Ordinal);
					seenSetsByScope.Add(pragma.Scope, seenSets);
				}
				if (!seenSets.Add(pragma.Key)) return false;
			}
			return true;
		}
	}
}
