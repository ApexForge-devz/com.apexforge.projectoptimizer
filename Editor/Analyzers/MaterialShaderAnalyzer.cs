using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
		readonly HashSet<string> m_RuntimeSourceTokens = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> m_RuntimeControlledKeywords = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> m_RuntimeControlledMaterialProperties = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<int> m_MaterialVariantAncestorIds = new HashSet<int>();
		bool m_MaterialVariantTreeBuilt;
		bool m_RuntimeSourceIndexBuilt;
		bool m_RuntimeSourceIndexComplete;
		bool m_RuntimeHasDynamicKeywordControl;
		bool m_RuntimeSourceHasDynamicMaterialPropertyUse;
		bool m_RuntimeSourceHasDynamicTransparencyPropertyUse;
		bool m_RuntimeSourceUsesMaterialPropertyBlock;
		bool m_TransparencyAnimationIndexBuilt;
		bool m_TransparencyAnimationIndexComplete;
		bool m_TransparencyAnimationControlsMaterialState;
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

		/// <summary>
		/// The small set of Standard-shader semantics that must survive Unity's native pipeline
		/// converter before MAT002 can be reported as verified. A shader-name check alone is not a
		/// useful postcondition: conversion can select the right destination shader and still lose
		/// the material's visible texture/color state.
		/// </summary>
		sealed class MaterialConversionSnapshot
		{
			public string SourceShaderName;
			public string TargetPipelineName;
			public string SourceSignature;
			public int SourceRawRenderQueue;
			public bool SourceEnableInstancing;
			public MaterialGlobalIlluminationFlags SourceGlobalIlluminationFlags;
			public bool SourceDoubleSidedGI;
			public Texture MainTexture;
			public Vector2 MainTextureScale;
			public Vector2 MainTextureOffset;
			public Color BaseColor;
			public Texture NormalTexture;
			public Vector2 NormalTextureScale;
			public Vector2 NormalTextureOffset;
			public float NormalScale;
			public Texture EmissionTexture;
			public Vector2 EmissionTextureScale;
			public Vector2 EmissionTextureOffset;
			public Color EmissionColor;
			public Texture MetallicOrSpecularTexture;
			public Vector2 MetallicOrSpecularTextureScale;
			public Vector2 MetallicOrSpecularTextureOffset;
			public float Metallic;
			public float Smoothness;
			public Color SpecularColor;
			public float Cutoff;
			public bool SpecularWorkflow;
			public bool HasNormalTexture;
			public bool HasNormalScale;
			public bool HasEmissionTexture;
			public bool HasEmissionColor;
			public bool HasMetallicOrSpecularTexture;
			public bool HasMetallic;
			public bool HasSmoothness;
			public bool HasSpecularColor;
			public bool HasCutoff;
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
							why: "Built-in pipeline shaders are not interchangeable with URP or HDRP shaders. Only supported stock shader families are passed to Unity's selected-material converter; custom or unsupported shaders remain diagnostic-only.",
							fix: "Run Unity's built-in material conversion command for supported Standard materials and the active URP/HDRP pipeline.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "MAT003", title: "GPU instancing disabled on an instancing-capable shader", category: AssetCategory.Material,
							why: "GPU instancing can reduce draw overhead for compatible repeated renderer cohorts, but static material-use counts do not establish a performance benefit.",
							fix: "Enable GPU Instancing only when fresh target-player profiling for the same build target, render pipeline and compatible mesh/material cohort proves a repeatable benefit. This analyzer has no profile-evidence provider, so it reports the opportunity without offering an automatic mutation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MAT004", title: "Duplicate material", category: AssetCategory.Material,
							why: "Separate byte-identical material assets with equivalent rendering state can be consolidated only when all serialized references can be remapped safely.",
							fix: "Collapse a byte-identical duplicate onto a keeper when the project-wide reference graph and local file-ID mapping prove the remap is safe.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
							recommendationConfidence: OptimizationConfidence.Proven,
							risk: FixRisk.Destructive,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.CrossAssetFresh
							);

			Rules.Register(id: "MAT005", title: "Orphaned texture references", category: AssetCategory.Material,
							why: "Materials can retain serialized texture entries for properties that the current shader no longer declares. Only names proven absent from a complete project source scan are removed automatically.",
							fix: "Remove only selected orphan texture entries whose property names have no detected project-source use; preserve uncertain entries.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
							);

			Rules.Register(id: "MAT006", title: "Transparent material appears fully opaque", category: AssetCategory.Material,
							why: "For a short allowlist of stock URP surface shaders, an opaque tint, alpha-free base texture and disabled alpha clipping provide enough evidence to switch the surface mode to Opaque.",
							fix: "Convert a proven opaque stock URP transparent material to Opaque and verify its serialized surface/blend state.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MAT007", title: "Redundant material render-queue override", category: AssetCategory.Material,
							why: "A material render-queue override equal to the shader's current queue is redundant now; resetting it restores inheritance of future shader queue changes.",
							fix: "Reset a render queue override to -1 when the raw override exactly equals the currently resolved shader queue.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
							recommendationConfidence: OptimizationConfidence.Proven,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MAT008", title: "Material has keywords not present in the shader keyword space", category: AssetCategory.Material,
							why: "A material should only need local shader keywords declared by its shader. A complete project proof scan that finds no use outside this material makes removing those invalid local keyword states a deterministic cleanup.",
							fix: "Remove only proven stale enabled local keywords after a complete project-source scan shows no other references.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
							);

			Rules.Register(id: "MAT009", title: "Material is part of a large shared shader-property family", category: AssetCategory.Material,
							why: "Materials that share shader, texture and keyword state may be managed as Material Variants, but the parent-child hierarchy is a human content-organization choice.",
							fix: "Choose a family member as the parent and convert the current material to a Material Variant, preserving its effective serialized property state.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "MAT010", title: "Material emission GI flags appear inconsistent", category: AssetCategory.Material,
							why: "Unity provides MaterialEditor.FixupEmissiveFlag to synchronize emission state and GI flags. A black emission value that is still flagged as emissive-but-not-black can be synchronized deterministically.",
							fix: "Run Unity's MaterialEditor emissive-flag fixup and verify the EmissiveIsBlack flag is synchronized.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
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
							why: "Unity reports shader support for the platform the editor is currently set to build for, and for nothing else. This is only an editor-platform diagnostic and says nothing about the optimizer's separate target profile.",
							fix: "Switch the editor's active build target and rescan to inspect the intended platform; this finding has no generic source fix.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SHD006", title: "Shader has a large local keyword space", category: AssetCategory.Shader,
							why: "A large local keyword space is actionable only when individual local declarations are proven unused by materials, shader code and project source.",
							fix: "Remove only individual local keyword declaration tokens proven unused by the complete project-source scan; preserve every ambiguous declaration.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
							);

			Rules.Register(id: "SHD007", title: "multi_compile feature appears material-driven", category: AssetCategory.Shader,
							why: "Global keyword source that is enabled by materials and has no detected project code control can use shader_feature so unused variants can be stripped.",
							fix: "Convert only complete, source-proven material-driven multi_compile declarations to shader_feature; preserve ambiguous runtime-controlled keywords.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
							);

			Rules.Register(id: "SHD008", title: "Global multi_compile feature may be local", category: AssetCategory.Shader,
							why: "A global keyword declaration used only by materials on this shader and not controlled by project code can be converted to a local declaration, using shader_feature_local when material state proves the variants.",
							fix: "Convert proven shader-private global declarations to shader_feature_local without changing the candidate keyword set.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
							);

			Rules.Register(id: "SHD009", title: "Large variant shader is Always Included", category: AssetCategory.Shader,
							why: "A large-variant shader can be removed from Always Included only when a complete project asset/source scan finds no material, collection, serialized asset or code references outside the setting entry.",
							fix: "Remove the shader GUID from Graphics Settings only when the complete-reference preflight proves it is otherwise unused; retain a diagnostic-only finding when any reference remains.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true,
							requiredEvidence: EvidenceRequirement.ProjectProofComplete
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
							why: "A large Shader Variant Collection can bloat preload data, but deciding whether its warmup coverage remains necessary is a project/runtime policy choice.",
							fix: "ReviewFix option: clear all recorded variants from this collection, after explicitly confirming the project no longer needs these warmup entries.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Destructive,
							backupRequired: true
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
							why: "Exact duplicate keyword sets and repeated keyword names can be removed while preserving distinct program/pass scope, provided the shader currently compiles.",
							fix: "Remove exact duplicate declarations, back up the source, reimport, and verify both declaration uniqueness and successful shader compilation.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
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
			m_RuntimeSourceTokens.Clear();
			m_RuntimeControlledKeywords.Clear();
			m_RuntimeControlledMaterialProperties.Clear();
			m_MaterialVariantAncestorIds.Clear();
			m_MaterialVariantTreeBuilt = false;
			m_RuntimeSourceIndexBuilt = false;
			m_RuntimeSourceIndexComplete = false;
			m_RuntimeHasDynamicKeywordControl = false;
			m_RuntimeSourceHasDynamicMaterialPropertyUse = false;
			m_RuntimeSourceHasDynamicTransparencyPropertyUse = false;
			m_RuntimeSourceUsesMaterialPropertyBlock = false;
			m_TransparencyAnimationIndexBuilt = false;
			m_TransparencyAnimationIndexComplete = false;
			m_TransparencyAnimationControlsMaterialState = false;
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
			var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
			if (material == null) return null;

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
					// MAT002: use Unity's own converter for explicitly supported stock Standard materials.
					// All other built-in shader families remain diagnostic-only because their property mapping
					// cannot be inferred safely from the shader name.
					using (RuleTiming.Begin("MAT002", entry.Path))
						if (m_UsingSrp && IsBuiltInShader(shaderName))
						{
							if (TryGetStandardMaterialConversionMenu(shaderName, out string conversionMenu))
							{
								var conversionSnapshot = CaptureMaterialConversionSnapshot(material, conversionMenu);
								if (conversionSnapshot != null)
									entry.AddIssue("MAT002", shaderName, "Converted shader for the active pipeline", 0,
										c => ConvertSelectedStandardMaterial(c, conversionMenu, conversionSnapshot),
										"Runs Unity's selected-material converter only for supported Standard shader families. The verifier checks the destination pipeline and the main texture/color, normal, emission, cutoff and supported surface-workflow values that have direct mappings.",
										verify: c => VerifyMaterialConvertedToActivePipeline(c, conversionSnapshot));
								else
									MarkIssueNoFix(entry.AddIssue("MAT002", shaderName, "A shader from the active pipeline", 0, null,
										"The source material uses unsupported surface modes, advanced maps/keywords, or a property configuration that the conservative conversion verifier cannot prove safe."),
										"The selected-material converter is not offered without a complete source-state snapshot.");
							}
							else
								MarkIssueNoFix(entry.AddIssue("MAT002", shaderName, "A shader from the active pipeline", 0, null,
									"No matching stock selected-material conversion route exists for this shader/pipeline combination; migrate this shader manually."),
									"This built-in shader family is outside the converter's supported stock cases.");
						}

					// MAT003: static slot counts are diagnostic only; target-player profile evidence is a shared-contract blocker.
					using (RuleTiming.Begin("MAT003", entry.Path))
						if (!facts.instancing && facts.instancingSupported && !IsUiShader(shaderName)
							&& !(m_UsingSrp && facts.srpBatcherCompatible))
						{
							var usage = ctx.MaterialUsage != null ? ctx.MaterialUsage.For(assetPath) : default(MaterialUsageIndex.Counts);
							// The approved policy requires target-player profile evidence before enabling instancing.
							// Scene and prefab slots are independent evidence and must not be added into a misleading total.
							var issue = entry.AddIssue("MAT003", "Instancing off", "Enable GPU instancing only after profile evidence confirms benefit", 0,
								null,
								"Static renderer-slot usage: " + usage.Describe() + ". No target-player profiling evidence is available in the current scan contract, so automatic mutation is blocked.");
							if (issue != null)
								MarkIssueNoFix(issue, "MAT003 requires a fresh profile record tied to target, mesh/material cohort and render pipeline; the shared ScanContext has no such evidence provider.");
						}

					// MAT005: remove only orphan texture entries whose property names have no occurrence
					// outside this material in a complete project-source proof index.
					var orphans = facts.orphanTextures;
					using (RuleTiming.Begin("MAT005", entry.Path))
						if (orphans.Count > 0)
						{
							var proof = ctx.GetProofIndex();
							EnsureRuntimeSourceIndex();
							var safeOrphans = new List<string>();
							if (proof != null && proof.CoverageComplete && m_RuntimeSourceIndexComplete && !m_RuntimeSourceHasDynamicMaterialPropertyUse)
								for (int i = 0; i < orphans.Count; i++)
									if (!proof.ContainsTokenOutsideFile(orphans[i], assetPath)) safeOrphans.Add(orphans[i]);
							bool proven = safeOrphans.Count > 0 && proof != null && proof.CoverageComplete
								&& m_RuntimeSourceIndexComplete && !m_RuntimeSourceHasDynamicMaterialPropertyUse;
							var issue = entry.AddIssue("MAT005", orphans.Count + " orphaned texture slot(s)", "Remove proven-unused orphan texture entries", 0,
								proven ? (Action<FixContext>)(c => StripOrphanTextureProperties((Material)c.Asset, c, safeOrphans)) : null,
								proven ? "Complete source proof and runtime-source preflight found no use or dynamic material-property API that could synthesize these selected names; uncertain slots are retained." : "No orphan slot passed complete source/runtime API proof; this instance remains diagnostic-only.",
								verify: proven ? (Func<FixContext, bool>)(c => VerifyOrphanTexturePropertiesRemoved(c, safeOrphans)) : null)
								?.Items("Orphaned slots", orphans);
							if (issue != null && proven)
								issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
							else if (issue != null)
								MarkIssueNoFix(issue, "Complete source proof did not establish a defensible set of orphan slots, or project code uses dynamic material property APIs.");
						}

					// MAT006 - needless transparency. The detector is constrained to supported stock
					// shader families and proves an opaque tint plus alpha-free base texture.
					using (RuleTiming.Begin("MAT006", entry.Path))
					if (facts.pointlesslyTransparent)
					{
						bool canMakeOpaque = CanSafelyMakeOpaque(ctx);
						var issue = entry.AddIssue("MAT006", "Surface Type: Transparent", "Surface Type: Opaque", 0,
							canMakeOpaque ? (Action<FixContext>)MakeOpaqueMaterial : null,
							canMakeOpaque
								? "Stock URP material is opaque by its current tint/texture state, complete runtime-source and animation checks found no transparency controls, and the edit backs up and verifies render-state properties."
								: "The runtime-source, material-property-block, animation or project-proof checks did not establish that transparency is never used at runtime.",
							verify: canMakeOpaque ? (Func<FixContext, bool>)VerifyOpaqueMaterial : null);
						if (issue != null && canMakeOpaque)
							issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh,
								EvidenceContract.ComputeProofEvidenceSignature(ctx.GetProofIndex()));
						else if (issue != null)
							MarkIssueNoFix(issue, "MAT006 is diagnostic-only unless full runtime source and animation evidence excludes dynamic transparency control.");
					}

					// MAT007: the override is redundant when the raw queue exactly equals the shader queue.
					using (RuleTiming.Begin("MAT007", entry.Path))
						if (facts.redundantRenderQueueOverride)
							entry.AddIssue("MAT007", "Override " + facts.renderQueue + " (same as shader default)",
								"Inherit the shader's render queue", 0, ResetMaterialRenderQueue,
								"Preflight rechecks the exact raw override and resolved shader queue before clearing the override.",
								verify: VerifyMaterialRenderQueueReset);

					// MAT008: stale keyword removal is offered only when the complete source proof is fresh.
					using (RuleTiming.Begin("MAT008", entry.Path))
						if (facts.unsupportedKeywords.Count > 0)
						{
							var proof = ctx.GetProofIndex();
							if (NoProjectShaderKeywordUse(ctx, assetPath, facts.unsupportedKeywords))
							{
								var issue = entry.AddIssue("MAT008", facts.unsupportedKeywords.Count + " unsupported keyword(s)", "Remove stale local keyword state", 0,
									c => RemoveUnsupportedMaterialKeywords(c, facts.unsupportedKeywords),
									"Complete source proof found no static use of these keyword names outside this material.",
									verify: c => VerifyNoUnsupportedMaterialKeywords(c, facts.unsupportedKeywords))?.Items("Keywords", facts.unsupportedKeywords);
								if (issue != null) issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
							}
							else
								MarkIssueNoFix(entry.AddIssue("MAT008", facts.unsupportedKeywords.Count + " unsupported keyword(s)", "Remove stale local keyword state"),
									"The complete source proof is missing/incomplete or found use of these keyword names elsewhere.");
						}

					using (RuleTiming.Begin("MAT010", entry.Path))
						if (facts.emissionGiMismatch)
							entry.AddIssue("MAT010", "Emission appears black while emissive GI is enabled",
								"Synchronize emissive GI flags", 0, FixMaterialEmissionGiFlags,
								"Runs Unity's MaterialEditor.FixupEmissiveFlag and verifies the persisted GI flag state.",
								verify: VerifyMaterialEmissionGiFlags);

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

		ShaderFacts MeasureShader(string assetPath)
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
			return facts;
		}

		AssetEntry AnalyzeShader(string assetPath, ScanContext ctx)
		{
			var facts = ctx.Cache != null
				? ctx.GetCached(assetPath, () => MeasureShader(assetPath))
				: MeasureShader(assetPath);
			if (facts == null) return null;

			// Project-wide usage and active-platform answers change independently of the shader asset hash,
			// so they must be refreshed even when the static shader facts came from the scan cache.
			ApplyProjectState(assetPath, facts, out HashSet<string> liveKeywords);

			// These source/project-dependent values are recalculated on every scan. A cached facts object may
			// contain old values, and keyword candidates depend on the current material and source indexes.
			facts.multiCompileLines = 0;
			facts.estimatedMultiCompileVariants = 0;
			facts.shaderFeatureLines = 0;
			facts.estimatedShaderFeatureVariants = 0;
			facts.duplicateKeywordSets = 0;
			facts.duplicateKeywordsInSets = 0;
			facts.multiCompileMaterialCandidates.Clear();
			facts.localKeywordCandidates.Clear();

			// Source-level estimates are explicitly approximate; they do not force shader compilation.
			string shaderSource = null;
			if (assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
			{
				try
				{
					shaderSource = ctx.GetAssetText(assetPath);
					if (shaderSource == null) throw new IOException("Could not read shader source.");
					AnalyzeShaderPragmas(shaderSource,
						out facts.multiCompileLines, out facts.estimatedMultiCompileVariants,
						out facts.shaderFeatureLines, out facts.estimatedShaderFeatureVariants,
						out facts.duplicateKeywordSets, out facts.duplicateKeywordsInSets);
					CollectKeywordCandidates(shaderSource, liveKeywords, m_ShadersUsingMaterialKeyword,
						facts.shaderName, facts.multiCompileMaterialCandidates, facts.localKeywordCandidates);
				}
				catch
				{
					shaderSource = null;
					facts.multiCompileLines = 0;
					facts.estimatedMultiCompileVariants = 0;
					facts.shaderFeatureLines = 0;
					facts.estimatedShaderFeatureVariants = 0;
					facts.duplicateKeywordSets = 0;
					facts.duplicateKeywordsInSets = 0;
					facts.multiCompileMaterialCandidates.Clear();
					facts.localKeywordCandidates.Clear();
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

			DedupeStrings(facts.multiCompileMaterialCandidates);
			DedupeStrings(facts.localKeywordCandidates);
			var proof = ctx.GetProofIndex();
			bool proofComplete = proof != null && proof.CoverageComplete
				&& !string.IsNullOrEmpty(EvidenceContract.ComputeProofEvidenceSignature(proof));
			if (shaderSource != null && proofComplete) EnsureRuntimeSourceIndex();
			bool canProveSourceUse = shaderSource != null && proofComplete && !facts.hasError
				&& m_RuntimeSourceIndexBuilt && m_RuntimeSourceIndexComplete;

			using (RuleTiming.Begin("SHD006", entry.Path))
				if (facts.keywordCount > k_LargeLocalKeywordWarning)
				{
					var edits = canProveSourceUse ? BuildUnusedLocalKeywordEdits(shaderSource, assetPath, proof) : new List<ShaderSourceEdit>();
					bool actionable = edits.Count > 0;
					var issue = entry.AddIssue("SHD006", facts.keywordCount + " local keywords", "Remove only proven-unused local keyword declaration tokens", 0,
						actionable ? (Action<FixContext>)(c => ApplyShaderSourceEdits(c, edits)) : null,
						actionable ? "The complete source proof found these local keyword tokens unused by materials, other project source and shader code." : "No local keyword token met every unused-proof precondition; the issue remains diagnostic-only.",
						verify: actionable ? (Func<FixContext, bool>)(c => VerifyShaderSourceEditsAndCompilation(c, edits)) : null);
					if (issue != null && actionable)
						issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
					else if (issue != null)
						MarkIssueNoFix(issue, "Keyword-space count alone is not a mutation target; no safely removable local declaration was proven.");
					if (issue != null && edits.Count > 0) issue.Items("Safe local keyword edits", DescribeShaderEdits(edits));
				}

			using (RuleTiming.Begin("SHD007", entry.Path))
				if (facts.multiCompileMaterialCandidates.Count > 0)
				{
					var edits = canProveSourceUse ? BuildShaderFeatureConversionEdits(shaderSource, facts.shaderName, liveKeywords, localize: false) : new List<ShaderSourceEdit>();
					bool actionable = edits.Count > 0;
					var issue = entry.AddIssue("SHD007",
						facts.multiCompileMaterialCandidates.Count + " multi_compile feature(s) have material-used keywords",
						"Convert only source-proven material-driven declarations to shader_feature", 0,
						actionable ? (Action<FixContext>)(c => ApplyShaderSourceEdits(c, edits)) : null,
						actionable ? "Only exact multi_compile declarations with material keyword use and no detected project-code keyword control are converted." : "No candidate passed the complete project-source/runtime keyword-control preflight; this instance is diagnostic-only.",
						verify: actionable ? (Func<FixContext, bool>)(c => VerifyShaderSourceEditsAndCompilation(c, edits)) : null);
					if (issue != null && actionable)
						issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
					else if (issue != null)
						MarkIssueNoFix(issue, "Material use by itself does not prove runtime keyword control is absent; no directive passed all guards.");
					issue?.Items("Candidates", facts.multiCompileMaterialCandidates);
					if (issue != null && edits.Count > 0) issue.Items("Safe directive edits", DescribeShaderEdits(edits));
				}

			using (RuleTiming.Begin("SHD008", entry.Path))
				if (facts.localKeywordCandidates.Count > 0)
				{
					var edits = canProveSourceUse ? BuildShaderFeatureConversionEdits(shaderSource, facts.shaderName, liveKeywords, localize: true) : new List<ShaderSourceEdit>();
					bool actionable = edits.Count > 0;
					var issue = entry.AddIssue("SHD008",
						facts.localKeywordCandidates.Count + " global multi_compile feature(s) appear shader-local",
						"Convert proven shader-private declarations to shader_feature_local", 0,
						actionable ? (Action<FixContext>)(c => ApplyShaderSourceEdits(c, edits)) : null,
						actionable ? "Every option is confined to materials using this shader, and project code contains no detected keyword control." : "Shader-locality is heuristic unless every option and runtime control passes the source preflight; this instance is diagnostic-only.",
						verify: actionable ? (Func<FixContext, bool>)(c => VerifyShaderSourceEditsAndCompilation(c, edits)) : null);
					if (issue != null && actionable)
						issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
					else if (issue != null)
						MarkIssueNoFix(issue, "No global declaration was proven shader-private and free of project-code keyword controls.");
					issue?.Items("Candidates", facts.localKeywordCandidates);
					if (issue != null && edits.Count > 0) issue.Items("Safe directive edits", DescribeShaderEdits(edits));
				}

			using (RuleTiming.Begin("SHD009", entry.Path))
				if (facts.alwaysIncluded && facts.estimatedMultiCompileVariants > k_LargeVariantWarning)
				{
					string guid = AssetDatabase.AssetPathToGUID(assetPath);
					bool canRemove = proofComplete && facts.materialCount == 0 && !string.IsNullOrEmpty(guid)
						&& !HasProjectReferenceOutsideAlwaysIncluded(guid, facts.shaderName, assetPath);
					var issue = entry.AddIssue("SHD009",
						"Always Included + ~" + facts.estimatedMultiCompileVariants + " multi_compile combinations",
						"Remove from Always Included only when otherwise unused", 0,
						canRemove ? (Action<FixContext>)(c => RemoveShaderFromAlwaysIncluded(c, guid, facts.shaderName)) : null,
						canRemove ? "Complete project source/asset scan found no reference outside the Always Included list." : "The shader may be referenced, material use is nonzero, or complete source proof is unavailable; no setting is changed.",
						verify: canRemove ? (Func<FixContext, bool>)(c => VerifyShaderRemovedFromAlwaysIncluded(c, guid)) : null);
					if (issue != null && canRemove)
						issue.WithEvidence(EvidenceRequirement.ProjectProofComplete, EvidenceState.Fresh, EvidenceContract.ComputeProofEvidenceSignature(proof));
					else if (issue != null)
						MarkIssueNoFix(issue, "Always Included cannot be pruned from potential variant count alone.");
				}


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
					var issue = entry.AddIssue("SHD014",
						facts.duplicateKeywordSets + " duplicate set(s), "
						+ facts.duplicateKeywordsInSets + " duplicate keyword name(s)",
						"Remove exact duplicate shader keyword declarations",
						facts.hasError ? 0 : 0,
						facts.hasError ? null : (Action<FixContext>)CleanDuplicateShaderKeywords,
						facts.hasError
							? "Duplicate declarations are reported, but the source currently has compile errors; changing keyword declarations automatically is blocked until the shader compiles."
							: "The fix removes duplicate keyword-set declarations only within the same shader program, backs up source, reimports, and verifies declaration uniqueness plus successful compilation.",
						verify: facts.hasError ? (Func<FixContext, bool>)null : c => VerifyNoDuplicateShaderKeywords(c) && VerifyShaderCompiles(c));
					if (issue != null && facts.hasError)
						MarkIssueNoFix(issue, "Fixing duplicate declarations while the shader has compiler errors is not safely verifiable.");
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

			// Support and SRP Batcher compatibility are read live rather than cached: both can change
			// when the active render pipeline or editor build target changes without editing the shader.
			try
			{
				var shader = LoadShaderAsset(assetPath);
				facts.supportedInEditor = shader == null || ShaderUtil.GetShaderInfo(shader).supported;
				facts.srpBatcherCompatibilityCode = shader != null ? GetSrpBatcherCompatibilityCode(shader) : 0;
			}
			catch
			{
				// Unknown is treated as supported: reporting an unsupported shader on the strength of
				// a failed query would be inventing the finding.
				facts.supportedInEditor = true;
				facts.srpBatcherCompatibilityCode = 0;
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
						"Review collected variants", 0, null,
						"The available action clears the collection; select it only after confirming the recorded variants are not needed for runtime warmup, Addressables, AssetBundles or runtime keyword switching.",
						options: new List<FixOption>
						{
							new FixOption
							{
								ActionId = "shd012-clear-variants",
								Label = "Clear all recorded variants",
								ResultDescription = "Clears the ShaderVariantCollection; the collection contains no recorded variants.",
								Recommended = false,
								Risk = FixRisk.Destructive,
								Warning = "This removes every variant entry and can reduce runtime shader warmup coverage.",
								Apply = ClearShaderVariantCollection,
								Verify = VerifyShaderVariantCollectionCleared
							}
						});
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

				// Deterministic keeper selection makes the proposed remap reproducible.
				var ordered = new List<AssetEntry>(group);
				ordered.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a?.Path, b?.Path));
				var keeper = ordered[0];
				if (keeper == null) continue;
				bool allAssetsIdentical = true;
				for (int i = 1; i < ordered.Count; i++)
					if (ordered[i] == null || !AssetsAreByteIdenticalIncludingMeta(keeper.Path, ordered[i].Path)) { allAssetsIdentical = false; break; }

				var references = ctx != null ? ctx.References : null;
				bool referenceEvidenceFresh = references != null && references.IsCurrentProjectWide && references.IsComplete;
				string referenceEvidence = referenceEvidenceFresh ? EvidenceContract.ComputeCrossAssetEvidenceSignature(references) : string.Empty;
				bool canUseCrossAssetEvidence = !string.IsNullOrEmpty(referenceEvidence);

				for (int i = 1; i < ordered.Count; i++)
				{
					var entry = ordered[i];
					if (entry == null) continue;
					var plan = canUseCrossAssetEvidence && allAssetsIdentical
						? DuplicateRemapper.Prepare(entry.Path, keeper.Path, references)
						: null;
					bool actionable = plan != null && plan.Viable;
					string detail = allAssetsIdentical
						? (actionable
							? "Byte-identical material asset, matching metadata except GUID, current complete project-wide reference graph and viable local-file-ID map. The remapper backs up every changed referrer and the duplicate before rewriting/deleting."
							: "The byte-identical duplicate cannot be remapped safely with the current reference/serialization evidence: " + (plan != null ? plan.Reason : "project-wide evidence is unavailable") + ". This instance remains diagnostic-only.")
						: "Materials share equivalent measured state but are not all byte-identical with equivalent metadata. This finding remains separate and diagnostic-only; it is not correlated away into an exact-duplicate result.";

					using (RuleTiming.Begin("MAT004", entry.Path))
					{
						var issue = entry.AddIssue("MAT004", ordered.Count + " equivalent materials", "Use " + keeper.Path, 0,
							actionable ? (Action<FixContext>)(c => CollapseByteIdenticalMaterial(c, entry.Path, keeper.Path, references)) : null,
							detail, SavingsBasis.Unknown,
							verify: actionable ? (Func<FixContext, bool>)(c => VerifyDuplicateMaterialRemoved(c.AssetPath)) : null);
						if (issue != null && actionable)
							issue.WithEvidence(EvidenceRequirement.CrossAssetFresh, EvidenceState.Fresh, referenceEvidence);
						else if (issue != null)
							MarkIssueNoFix(issue, "This material group does not satisfy the byte-identity, metadata-equivalence and complete-reference preconditions for automated remapping.");
						issue?.Good("Share this one instead", keeper.Path, keeper.Path);
						var others = new List<string>();
						for (int j = 1; j < ordered.Count; j++) if (j != i && ordered[j] != null) others.Add(ordered[j].Path);
						issue?.Items("Also equivalent", others, DetailKind.Bad, arePaths: true);
					}
					entry.Finalise();
					yield return null;
				}
			}

			foreach (var pair in m_ByFamily)
			{
				var family = pair.Value;
				if (family == null || family.Count < 3) continue;

				// MAT004 owns exact material duplicates. MAT009 should measure a family of distinct
				// effective material states, not reach its threshold because the same material was copied.
				var group = new List<AssetEntry>();
				var distinctSignatures = new HashSet<string>(StringComparer.Ordinal);
				for (int i = 0; i < family.Count; i++)
				{
					var candidate = family[i];
					if (candidate == null) continue;
					var material = AssetDatabase.LoadAssetAtPath<Material>(candidate.Path);
					string signature = BuildSignature(material);
					if (!string.IsNullOrEmpty(signature) && distinctSignatures.Add(signature)) group.Add(candidate);
				}
				if (group.Count < 3) continue;
				group.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a?.Path, b?.Path));

				foreach (var entry in group)
				{
					using (RuleTiming.Begin("MAT009", entry.Path))
					{
						var options = BuildMaterialVariantParentOptions(group, entry);
						var issue = entry.AddIssue("MAT009",
							group.Count + " distinct materials share the same shader/texture/keyword family",
							"Choose a sibling material as parent / base",
							0, null,
							"Choose an explicit family member as parent. Each option previews property differences, converts this material to a Material Variant, and verifies its resulting parent relation and effective serialized signature.",
							options: options);
						issue?.Items("Family members", CollectPaths(group), DetailKind.Bad, arePaths: true);
						if (issue != null && (options == null || options.Count == 0))
							MarkIssueNoFix(issue, "No valid sibling parent could be offered; do not present a non-actionable ReviewFix.");
					}
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
			if ((flags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) != 0)
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

		static void StripOrphanTextureProperties(Material material, FixContext c, IList<string> selectedNames)
		{
			if (material == null) throw new InvalidOperationException("Material could not be loaded for orphan-property cleanup.");
			if (selectedNames == null || selectedNames.Count == 0) throw new InvalidOperationException("No proven orphan texture names were selected.");
			var selected = new HashSet<string>(selectedNames, StringComparer.Ordinal);
			var so = new SerializedObject(material);
			var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
			if (texEnvs == null || !texEnvs.isArray) throw new InvalidOperationException("Serialized texture entries are unavailable.");
			bool changed = false;
			for (int i = texEnvs.arraySize - 1; i >= 0; i--)
			{
				var element = texEnvs.GetArrayElementAtIndex(i);
				var name = element.FindPropertyRelative("first")?.stringValue;
				if (string.IsNullOrEmpty(name) || !selected.Contains(name)) continue;
				if (material.HasProperty(name)) throw new InvalidOperationException("Texture property is no longer orphaned: " + name);
				var texture = element.FindPropertyRelative("second.m_Texture");
				if (texture == null || texture.objectReferenceValue == null) throw new InvalidOperationException("Orphan texture entry changed after analysis: " + name);
				texEnvs.DeleteArrayElementAtIndex(i);
				changed = true;
			}
			if (!changed) throw new InvalidOperationException("No selected orphan texture entries remained to remove.");
			so.ApplyModifiedPropertiesWithoutUndo();
			EditorUtility.SetDirty(material);
			c.NeedsSave = true;
		}

		static bool VerifyOrphanTexturePropertiesRemoved(FixContext c, IList<string> selectedNames)
		{
			var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (saved == null || selectedNames == null) return false;
			var selected = new HashSet<string>(selectedNames, StringComparer.Ordinal);
			var so = new SerializedObject(saved);
			var texEnvs = so.FindProperty("m_SavedProperties.m_TexEnvs");
			if (texEnvs == null || !texEnvs.isArray) return false;
			for (int i = 0; i < texEnvs.arraySize; i++)
			{
				var name = texEnvs.GetArrayElementAtIndex(i).FindPropertyRelative("first")?.stringValue;
				if (!string.IsNullOrEmpty(name) && selected.Contains(name)) return false;
			}
			return true;
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
			c.RequireBackup(c.AssetPath);

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
			if (material.HasProperty("_Blend") && Mathf.RoundToInt(material.GetFloat("_Blend")) != 0) return false;
			if (material.HasProperty("_SrcBlend") && Mathf.RoundToInt(material.GetFloat("_SrcBlend")) != 1) return false;
			if (material.HasProperty("_DstBlend") && Mathf.RoundToInt(material.GetFloat("_DstBlend")) != 0) return false;
			if (material.HasProperty("_ZWrite") && Mathf.RoundToInt(material.GetFloat("_ZWrite")) != 1) return false;
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

			bool stockShader = shader.name == "Universal Render Pipeline/Lit"
							   || shader.name == "Universal Render Pipeline/Simple Lit"
							   || shader.name == "Universal Render Pipeline/Unlit";
			if (!stockShader) return false;

			if (!material.HasProperty("_Surface")) return false;
			if (Mathf.RoundToInt(material.GetFloat("_Surface")) != 1) return false;
			if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f) return false;
			if (material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")
				|| material.IsKeywordEnabled("_ALPHAMODULATE_ON")) return false;

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
		bool NoProjectShaderKeywordUse(ScanContext ctx, string assetPath, List<string> keywords)
		{
			if (ctx == null || !ctx.Settings.projectProofPass || keywords == null || keywords.Count == 0) return false;
			var index = ctx.GetProofIndex();
			if (index == null || !index.CoverageComplete) return false;
			EnsureRuntimeSourceIndex();
			// Any runtime keyword-control code can synthesize the stale name dynamically. The current
			// project contract does not track keyword-name dataflow, so do not remove serialized state.
			if (!m_RuntimeSourceIndexComplete || m_RuntimeHasDynamicKeywordControl) return false;
			for (int i = 0; i < keywords.Count; i++)
				if (index.ContainsTokenOutsideFile(keywords[i], assetPath) || m_RuntimeSourceTokens.Contains(keywords[i])
					|| m_RuntimeControlledKeywords.Contains(keywords[i])) return false;
			return true;
		}
		static void RemoveUnsupportedMaterialKeywords(FixContext c, List<string> keywords)
		{
			var mat = c.Asset as Material;
			if (mat == null) throw new InvalidOperationException("Material unavailable.");
			var stale = new HashSet<string>(keywords ?? new List<string>(), StringComparer.Ordinal);
			if (stale.Count == 0) throw new InvalidOperationException("No stale keyword names were selected.");
			c.RequireBackup(c.AssetPath);

			// SetKeyword(LocalKeyword, false) is ineffective for names that are no longer present in the
			// shader's keyword space. Remove such names from serialized legacy keyword state instead.
			bool changed = false;
			var serialized = new SerializedObject(mat);
			string[] arrayNames = { "m_ValidKeywords", "m_InvalidKeywords" };
			for (int a = 0; a < arrayNames.Length; a++)
			{
				var array = serialized.FindProperty(arrayNames[a]);
				if (array == null || !array.isArray) continue;
				for (int i = array.arraySize - 1; i >= 0; i--)
				{
					var element = array.GetArrayElementAtIndex(i);
					string value = element != null && element.propertyType == SerializedPropertyType.String ? element.stringValue : null;
					if (string.IsNullOrEmpty(value) || !stale.Contains(value)) continue;
					array.DeleteArrayElementAtIndex(i);
					changed = true;
				}
			}
			if (serialized.hasModifiedProperties)
			{
				serialized.ApplyModifiedPropertiesWithoutUndo();
				changed = true;
			}

#pragma warning disable 618
			string[] existing = mat.shaderKeywords ?? Array.Empty<string>();
			var kept = new List<string>(existing.Length);
			for (int i = 0; i < existing.Length; i++)
			{
				if (stale.Contains(existing[i])) { changed = true; continue; }
				kept.Add(existing[i]);
			}
			if (kept.Count != existing.Length) mat.shaderKeywords = kept.ToArray();
#pragma warning restore 618
			if (!changed) throw new InvalidOperationException("Unity did not expose the selected stale keyword names in mutable material keyword state; no verified mutation was made.");
			EditorUtility.SetDirty(mat);
			c.NeedsSave = true;
		}

		static bool VerifyNoUnsupportedMaterialKeywords(FixContext c, List<string> keywords)
		{
			var mat = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (mat == null) return false;
			var stale = new HashSet<string>(keywords ?? new List<string>(), StringComparer.Ordinal);
			var enabled = mat.enabledKeywords;
			for (int i = 0; i < enabled.Length; i++)
				if (stale.Contains(enabled[i].name)) return false;
#pragma warning disable 618
			var legacy = mat.shaderKeywords ?? Array.Empty<string>();
#pragma warning restore 618
			for (int i = 0; i < legacy.Length; i++) if (stale.Contains(legacy[i])) return false;
			var serialized = new SerializedObject(mat);
			string[] arrayNames = { "m_ValidKeywords", "m_InvalidKeywords" };
			for (int a = 0; a < arrayNames.Length; a++)
			{
				var array = serialized.FindProperty(arrayNames[a]);
				if (array == null || !array.isArray) continue;
				for (int i = 0; i < array.arraySize; i++)
				{
					var element = array.GetArrayElementAtIndex(i);
					if (element != null && element.propertyType == SerializedPropertyType.String && stale.Contains(element.stringValue)) return false;
				}
			}
			return true;
		}

		static void CleanDuplicateShaderKeywords(FixContext c)
		{
			if (!VerifyShaderCompiles(c)) throw new InvalidOperationException("Shader compilation errors appeared after analysis; refusing keyword rewrite.");
			string shaderFullPath = AssetPathUtility.ToAbsolutePath(c.AssetPath);
			string text = System.IO.File.ReadAllText(shaderFullPath);
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
			System.IO.File.WriteAllText(shaderFullPath, finalText, new UTF8Encoding(false));
			AssetDatabase.ImportAsset(c.AssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
			c.NeedsReimport = true;
			if (!VerifyShaderCompiles(c)) throw new InvalidOperationException("Duplicate-keyword cleanup caused shader compilation errors.");
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
			try { text = System.IO.File.ReadAllText(AssetPathUtility.ToAbsolutePath(c.AssetPath)); } catch { return false; }
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

		// --- approved FixKind audit actions and their preflights/verifiers --------------

		static void MarkIssueNoFix(OptimizationIssue issue, string reason)
		{
			if (issue == null) return;
			issue.FixKind = FixKind.None;
			issue.Fix = null;
			issue.Verify = null;
			if (issue.Options != null) issue.Options.Clear();
			if (!string.IsNullOrEmpty(reason)) issue.Note(reason, "NoFix safety gate");
		}

		static bool IsSupportedStandardMaterial(string shaderName)
		{
			return shaderName == "Standard" || shaderName == "Standard (Specular setup)";
		}

		static bool TryGetStandardMaterialConversionMenu(string shaderName, out string menuPath)
		{
			menuPath = null;
			if (!IsSupportedStandardMaterial(shaderName)) return false;
			var pipeline = GraphicsSettings.currentRenderPipeline;
			if (pipeline == null) return false;
			string typeName = pipeline.GetType().Name;
			if (typeName.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0)
				menuPath = "Edit/Rendering/Materials/Convert Selected Built-in Materials to URP";
			else if (typeName.IndexOf("HDRenderPipelineAsset", StringComparison.Ordinal) >= 0)
				menuPath = "Edit/Rendering/Materials/Convert Selected Built-in Materials to HDRP";
			return !string.IsNullOrEmpty(menuPath);
		}

		static bool IsConservativeStandardMaterialConversionCandidate(Material material, bool hdrp)
		{
			if (material == null || material.shader == null) return false;
			// The default Standard surface mode is opaque. Cutout/fade/transparent conversion requires
			// additional blend/tag semantics that are not fully represented in this snapshot.
			if (material.HasProperty("_Mode") && Mathf.RoundToInt(material.GetFloat("_Mode")) != 0) return false;
			string[] unsupportedKeywords =
			{
				"_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_PARALLAXMAP",
				"_OCCLUSIONMAP", "_DETAIL_MULX2", "_DETAIL_SCALED", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"
			};
			for (int i = 0; i < unsupportedKeywords.Length; i++)
				if (material.IsKeywordEnabled(unsupportedKeywords[i])) return false;
			string[] unsupportedMaps = { "_OcclusionMap", "_ParallaxMap", "_DetailAlbedoMap", "_DetailNormalMap" };
			for (int i = 0; i < unsupportedMaps.Length; i++)
				if (material.HasProperty(unsupportedMaps[i]) && material.GetTexture(unsupportedMaps[i]) != null) return false;
			if (material.HasProperty("_SmoothnessTextureChannel") && material.GetFloat("_SmoothnessTextureChannel") > 0.5f) return false;
			if (material.HasProperty("_SpecularHighlights") && material.GetFloat("_SpecularHighlights") < 0.5f) return false;
			if (material.HasProperty("_GlossyReflections") && material.GetFloat("_GlossyReflections") < 0.5f) return false;
			// HDRP packs metallic/smoothness/specular data into channel maps. Until the content of
			// the converted pack can be verified, those source map cases are not AutoFix candidates.
			if (hdrp)
			{
				string mapProperty = material.shader.name == "Standard (Specular setup)" ? "_SpecGlossMap" : "_MetallicGlossMap";
				if (material.HasProperty(mapProperty) && material.GetTexture(mapProperty) != null) return false;
			}
			return true;
		}

		static MaterialConversionSnapshot CaptureMaterialConversionSnapshot(Material material, string expectedMenu)
		{
			if (material == null || material.shader == null || !IsSupportedStandardMaterial(material.shader.name)) return null;
			bool hdrp = !string.IsNullOrEmpty(expectedMenu)
				&& expectedMenu.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!IsConservativeStandardMaterialConversionCandidate(material, hdrp)
				|| !material.HasProperty("_MainTex") || !material.HasProperty("_Color")) return null;

			var snapshot = new MaterialConversionSnapshot
			{
				SourceShaderName = material.shader.name,
				TargetPipelineName = hdrp ? "HDRP" : "URP",
				SourceSignature = BuildSignature(material),
				SourceRawRenderQueue = material.rawRenderQueue,
				SourceEnableInstancing = material.enableInstancing,
				SourceGlobalIlluminationFlags = material.globalIlluminationFlags,
				SourceDoubleSidedGI = material.doubleSidedGI,
				MainTexture = material.GetTexture("_MainTex"),
				MainTextureScale = material.GetTextureScale("_MainTex"),
				MainTextureOffset = material.GetTextureOffset("_MainTex"),
				BaseColor = material.GetColor("_Color"),
				SpecularWorkflow = material.shader.name == "Standard (Specular setup)"
			};

			if (material.HasProperty("_BumpMap"))
			{
				snapshot.HasNormalTexture = true;
				snapshot.NormalTexture = material.GetTexture("_BumpMap");
				snapshot.NormalTextureScale = material.GetTextureScale("_BumpMap");
				snapshot.NormalTextureOffset = material.GetTextureOffset("_BumpMap");
			}
			if (material.HasProperty("_BumpScale"))
			{
				snapshot.HasNormalScale = true;
				snapshot.NormalScale = material.GetFloat("_BumpScale");
			}
			if (material.HasProperty("_EmissionMap"))
			{
				snapshot.HasEmissionTexture = true;
				snapshot.EmissionTexture = material.GetTexture("_EmissionMap");
				snapshot.EmissionTextureScale = material.GetTextureScale("_EmissionMap");
				snapshot.EmissionTextureOffset = material.GetTextureOffset("_EmissionMap");
			}
			if (material.HasProperty("_EmissionColor"))
			{
				snapshot.HasEmissionColor = true;
				snapshot.EmissionColor = material.GetColor("_EmissionColor");
			}
			if (material.HasProperty(snapshot.SpecularWorkflow ? "_SpecGlossMap" : "_MetallicGlossMap"))
			{
				snapshot.HasMetallicOrSpecularTexture = true;
				string textureProperty = snapshot.SpecularWorkflow ? "_SpecGlossMap" : "_MetallicGlossMap";
				snapshot.MetallicOrSpecularTexture = material.GetTexture(textureProperty);
				snapshot.MetallicOrSpecularTextureScale = material.GetTextureScale(textureProperty);
				snapshot.MetallicOrSpecularTextureOffset = material.GetTextureOffset(textureProperty);
			}
			if (material.HasProperty("_Metallic"))
			{
				snapshot.HasMetallic = true;
				snapshot.Metallic = material.GetFloat("_Metallic");
			}
			if (material.HasProperty("_Glossiness"))
			{
				snapshot.HasSmoothness = true;
				snapshot.Smoothness = material.GetFloat("_Glossiness");
			}
			if (material.HasProperty("_SpecColor"))
			{
				snapshot.HasSpecularColor = true;
				snapshot.SpecularColor = material.GetColor("_SpecColor");
			}
			if (material.HasProperty("_Cutoff"))
			{
				snapshot.HasCutoff = true;
				snapshot.Cutoff = material.GetFloat("_Cutoff");
			}

			// HDRP's Mask Map packs multiple scalar channels. The current verifier cannot prove
			// that a non-empty Standard metallic/specular map retained the same channel semantics,
			// so make that case diagnostic-only rather than treating any non-null Mask Map as proof.
			if (hdrp && snapshot.HasMetallicOrSpecularTexture && snapshot.MetallicOrSpecularTexture != null)
				return null;
			return string.IsNullOrEmpty(snapshot.SourceSignature) ? null : snapshot;
		}

		static void ConvertSelectedStandardMaterial(FixContext c, string expectedMenu, MaterialConversionSnapshot snapshot)
		{
			var material = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || material.shader == null || snapshot == null) throw new InvalidOperationException("Material, shader or conversion snapshot is unavailable.");
			if (!TryGetStandardMaterialConversionMenu(material.shader.name, out string menu) || menu != expectedMenu)
				throw new InvalidOperationException("The shader family or active render pipeline changed after analysis.");
			if (!string.Equals(material.shader.name, snapshot.SourceShaderName, StringComparison.Ordinal)
				|| !string.Equals(BuildSignature(material), snapshot.SourceSignature, StringComparison.Ordinal))
				throw new InvalidOperationException("Material properties changed after conversion analysis; rescan before applying the fix.");
			c.RequireBackup(c.AssetPath);
			var previousSelection = Selection.objects;
			try
			{
				Selection.objects = new UnityEngine.Object[] { material };
				if (!EditorApplication.ExecuteMenuItem(expectedMenu))
					throw new InvalidOperationException("Unity's selected-material converter menu item is not available: " + expectedMenu);
			}
			finally { Selection.objects = previousSelection; }
			AssetDatabase.SaveAssets();
			AssetDatabase.ImportAsset(c.AssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
			c.NeedsReimport = true;
		}

		static bool VerifyMaterialConvertedToActivePipeline(FixContext c, MaterialConversionSnapshot snapshot)
		{
			var material = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || material.shader == null || snapshot == null || IsBuiltInShader(material.shader.name)) return false;
			var pipeline = GraphicsSettings.currentRenderPipeline;
			if (pipeline == null) return false;
			string typeName = pipeline.GetType().Name;
			bool hdrp = typeName.IndexOf("HDRenderPipelineAsset", StringComparison.Ordinal) >= 0;
			bool urp = typeName.IndexOf("UniversalRenderPipelineAsset", StringComparison.Ordinal) >= 0;
			if ((!hdrp && !urp) || !string.Equals(snapshot.TargetPipelineName, hdrp ? "HDRP" : "URP", StringComparison.Ordinal)) return false;
			if (hdrp ? !material.shader.name.StartsWith("HDRP/", StringComparison.Ordinal)
				: !material.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal)) return false;
			if (material.rawRenderQueue != snapshot.SourceRawRenderQueue
				|| material.enableInstancing != snapshot.SourceEnableInstancing
				|| material.globalIlluminationFlags != snapshot.SourceGlobalIlluminationFlags
				|| material.doubleSidedGI != snapshot.SourceDoubleSidedGI) return false;

			// Base texture and tint are required semantics for every supported source shader.
			string baseMapName = hdrp ? "_BaseColorMap" : "_BaseMap";
			if (!MatchesTexture(material, baseMapName, snapshot.MainTexture, snapshot.MainTextureScale, snapshot.MainTextureOffset)) return false;
			if (!MatchesColor(material, "_BaseColor", snapshot.BaseColor)) return false;

			if (snapshot.HasNormalTexture
				&& !MatchesTexture(material, hdrp ? "_NormalMap" : "_BumpMap", snapshot.NormalTexture,
					snapshot.NormalTextureScale, snapshot.NormalTextureOffset)) return false;
			if (snapshot.HasNormalScale && !MatchesFloat(material, hdrp ? "_NormalScale" : "_BumpScale", snapshot.NormalScale)) return false;
			if (snapshot.HasEmissionTexture
				&& !MatchesTexture(material, hdrp ? "_EmissiveColorMap" : "_EmissionMap", snapshot.EmissionTexture,
					snapshot.EmissionTextureScale, snapshot.EmissionTextureOffset)) return false;
			if (snapshot.HasEmissionColor && !MatchesColor(material, hdrp ? "_EmissiveColor" : "_EmissionColor", snapshot.EmissionColor)) return false;

			if (snapshot.HasSmoothness && !MatchesFloat(material, "_Smoothness", snapshot.Smoothness)) return false;
			if (snapshot.HasCutoff)
			{
				string cutoffName = hdrp ? "_AlphaCutoff" : "_Cutoff";
				if (material.HasProperty(cutoffName) && !MatchesFloat(material, cutoffName, snapshot.Cutoff)) return false;
				if (!material.HasProperty(cutoffName) && Mathf.Abs(snapshot.Cutoff - 0.5f) > 0.0001f) return false;
			}

			if (snapshot.SpecularWorkflow)
			{
				if (snapshot.HasSpecularColor && !MatchesColor(material, hdrp ? "_SpecularColor" : "_SpecColor", snapshot.SpecularColor)) return false;
				if (snapshot.HasMetallicOrSpecularTexture && snapshot.MetallicOrSpecularTexture != null)
				{
					string specMap = hdrp ? "_SpecularColorMap" : "_SpecGlossMap";
					if (!MatchesTexture(material, specMap, snapshot.MetallicOrSpecularTexture,
						snapshot.MetallicOrSpecularTextureScale, snapshot.MetallicOrSpecularTextureOffset))
						return false;
				}
			}
			else
			{
				if (snapshot.HasMetallic && !MatchesFloat(material, "_Metallic", snapshot.Metallic)) return false;
				if (snapshot.HasMetallicOrSpecularTexture && snapshot.MetallicOrSpecularTexture != null)
				{
					if (hdrp)
					{
						// Non-empty HDRP packed-map cases are rejected during capture because this direct
						// verifier cannot prove equivalent channel packing.
						return false;
					}
					else if (!MatchesTexture(material, "_MetallicGlossMap", snapshot.MetallicOrSpecularTexture,
						snapshot.MetallicOrSpecularTextureScale, snapshot.MetallicOrSpecularTextureOffset)) return false;
				}
			}
			return true;
		}

		static bool MatchesTexture(Material material, string property, Texture expected, Vector2 expectedScale, Vector2 expectedOffset)
		{
			if (material == null || !material.HasProperty(property)) return false;
			return material.GetTexture(property) == expected
				&& Vector2.Distance(material.GetTextureScale(property), expectedScale) < 0.0001f
				&& Vector2.Distance(material.GetTextureOffset(property), expectedOffset) < 0.0001f;
		}

		static bool MatchesColor(Material material, string property, Color expected)
		{
			if (material == null || !material.HasProperty(property)) return false;
			Color actual = material.GetColor(property);
			return Mathf.Abs(actual.r - expected.r) < 0.0001f
				&& Mathf.Abs(actual.g - expected.g) < 0.0001f
				&& Mathf.Abs(actual.b - expected.b) < 0.0001f
				&& Mathf.Abs(actual.a - expected.a) < 0.0001f;
		}

		static bool MatchesFloat(Material material, string property, float expected)
		{
			return material != null && material.HasProperty(property)
				&& Mathf.Abs(material.GetFloat(property) - expected) < 0.0001f;
		}

		static void EnableGpuInstancing(FixContext c)
		{
			var material = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || material.shader == null) throw new InvalidOperationException("Material or shader is unavailable.");
			if (!SupportsGpuInstancing(material.shader) || IsUiShader(material.shader.name))
				throw new InvalidOperationException("This shader does not satisfy the instancing safety gate.");
			if (GraphicsSettings.currentRenderPipeline != null && GetSrpBatcherCompatibilityCode(material.shader) == 0)
				throw new InvalidOperationException("The shader is SRP Batcher compatible; the instancing recommendation is no longer applicable.");
			if (material.enableInstancing) return;
			c.RequireBackup(c.AssetPath);
			material.enableInstancing = true;
			EditorUtility.SetDirty(material);
			c.NeedsSave = true;
		}

		static bool VerifyGpuInstancing(FixContext c)
		{
			var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			return saved != null && saved.shader != null && saved.enableInstancing
				&& SupportsGpuInstancing(saved.shader) && !IsUiShader(saved.shader.name)
				&& !(GraphicsSettings.currentRenderPipeline != null && GetSrpBatcherCompatibilityCode(saved.shader) == 0);
		}

		static void ResetMaterialRenderQueue(FixContext c)
		{
			var material = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || material.shader == null) throw new InvalidOperationException("Material or shader is unavailable.");
			if (material.rawRenderQueue < 0 || material.rawRenderQueue != material.shader.renderQueue)
				throw new InvalidOperationException("The raw material queue no longer exactly equals the shader default.");
			c.RequireBackup(c.AssetPath);
			material.renderQueue = -1;
			EditorUtility.SetDirty(material);
			c.NeedsSave = true;
		}

		static bool VerifyMaterialRenderQueueReset(FixContext c)
		{
			var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			return saved != null && saved.shader != null && saved.rawRenderQueue < 0;
		}

		static void FixMaterialEmissionGiFlags(FixContext c)
		{
			var material = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			if (material == null || !HasEmissionGIMismatch(material))
				throw new InvalidOperationException("The material no longer has the specific black-emission / emissive-GI mismatch.");
			c.RequireBackup(c.AssetPath);
			MaterialEditor.FixupEmissiveFlag(material);
			EditorUtility.SetDirty(material);
			c.NeedsSave = true;
		}

		static bool VerifyMaterialEmissionGiFlags(FixContext c)
		{
			var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			return saved != null && !HasEmissionGIMismatch(saved);
		}

		static void ClearShaderVariantCollection(FixContext c)
		{
			var collection = c.Asset as ShaderVariantCollection ?? AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(c.AssetPath);
			if (collection == null || collection.variantCount <= 0)
				throw new InvalidOperationException("Shader Variant Collection no longer has variants to clear.");
			c.RequireBackup(c.AssetPath);
			collection.Clear();
			EditorUtility.SetDirty(collection);
			c.NeedsSave = true;
		}

		static bool VerifyShaderVariantCollectionCleared(FixContext c)
		{
			var saved = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(c.AssetPath);
			return saved != null && saved.variantCount == 0;
		}

		static bool FilesAreByteIdentical(string firstPath, string secondPath)
		{
			try
			{
				string first = AssetPathUtility.ToAbsolutePath(firstPath);
				string second = AssetPathUtility.ToAbsolutePath(secondPath);
				var a = new FileInfo(first); var b = new FileInfo(second);
				if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
				using (var streamA = File.OpenRead(first))
			using (var streamB = File.OpenRead(second))
			{
				byte[] bufferA = new byte[81920]; byte[] bufferB = new byte[81920];
				while (true)
				{
					int readA = streamA.Read(bufferA, 0, bufferA.Length);
					int readB = streamB.Read(bufferB, 0, bufferB.Length);
					if (readA != readB) return false;
					if (readA == 0) return true;
					for (int i = 0; i < readA; i++) if (bufferA[i] != bufferB[i]) return false;
				}
			}
			}
			catch { return false; }
		}

		static bool AssetsAreByteIdenticalIncludingMeta(string firstPath, string secondPath)
		{
			if (!FilesAreByteIdentical(firstPath, secondPath)) return false;
			try
			{
				string firstMeta = AssetPathUtility.ToAbsolutePath(firstPath) + ".meta";
				string secondMeta = AssetPathUtility.ToAbsolutePath(secondPath) + ".meta";
				if (!File.Exists(firstMeta) || !File.Exists(secondMeta)) return false;
				return string.Equals(NormalizeMetaGuid(File.ReadAllText(firstMeta)), NormalizeMetaGuid(File.ReadAllText(secondMeta)), StringComparison.Ordinal);
			}
			catch { return false; }
		}

		static string NormalizeMetaGuid(string metaText)
		{
			if (metaText == null) return null;
			return Regex.Replace(metaText.Replace("\r\n", "\n").Replace('\r', '\n'), @"(?m)^(\s*guid:\s*)[0-9a-fA-F]{32}(\s*)$", "$1<GUID>$2");
		}

		static void CollapseByteIdenticalMaterial(FixContext c, string duplicatePath, string keeperPath, ReferenceIndex references)
		{
			if (references == null || !references.IsCurrentProjectWide || !references.IsComplete)
				throw new InvalidOperationException("The project-wide reference graph is stale or incomplete.");
			if (!AssetsAreByteIdenticalIncludingMeta(duplicatePath, keeperPath))
				throw new InvalidOperationException("Duplicate and keeper files are no longer byte-identical.");
			var plan = DuplicateRemapper.Prepare(duplicatePath, keeperPath, references);
			if (plan == null || !plan.Viable) throw new InvalidOperationException(plan != null ? plan.Reason : "No remap plan was produced.");
			var result = DuplicateRemapper.Apply(plan, c.TryBackupExtra);
			if (result == null || !result.Succeeded)
				throw new InvalidOperationException("Duplicate remap did not complete successfully: " + (result != null ? result.Summary : "no result"));
			c.DeletedAsset = true;
			c.NeedsReimport = true;
		}

		static bool VerifyDuplicateMaterialRemoved(string duplicatePath)
		{
			try
			{
				string full = AssetPathUtility.ToAbsolutePath(duplicatePath);
				return !File.Exists(full) && !File.Exists(full + ".meta");
			}
			catch { return false; }
		}

		List<FixOption> BuildMaterialVariantParentOptions(List<AssetEntry> family, AssetEntry childEntry)
		{
			var options = new List<FixOption>();
			if (family == null || childEntry == null) return options;
			var child = AssetDatabase.LoadAssetAtPath<Material>(childEntry.Path);
			if (child == null || child.shader == null) return options;
			string childFamily = BuildFamilySignature(child);
			string childSignature = BuildSignature(child);
			if (string.IsNullOrEmpty(childFamily) || string.IsNullOrEmpty(childSignature)) return options;
			// Do not create a hierarchy edge if other variants inherit from this child already.
			// The action repeats this check against fresh project state before mutation.
			if (HasMaterialVariantDescendants(child)) return options;
			for (int i = 0; i < family.Count; i++)
			{
				var candidate = family[i];
				if (candidate == null || string.Equals(candidate.Path, childEntry.Path, StringComparison.OrdinalIgnoreCase)) continue;
				var parent = AssetDatabase.LoadAssetAtPath<Material>(candidate.Path);
				if (parent == null || parent == child || parent.shader != child.shader || child.parent == parent) continue;
				if (parent.IsChildOf(child)) continue;
				if (!string.Equals(BuildFamilySignature(parent), childFamily, StringComparison.Ordinal)) continue;
				string parentPath = candidate.Path;
				string parentGuid = AssetDatabase.AssetPathToGUID(parentPath);
				options.Add(new FixOption
				{
					ActionId = "mat009-parent-" + (string.IsNullOrEmpty(parentGuid) ? SanitizeActionId(parentPath) : parentGuid),
					Label = "Use " + parent.name + " as parent",
					ResultDescription = DescribeMaterialVariantDiff(child, parent),
					Recommended = false,
					Risk = FixRisk.High,
					Warning = "Changes the material inheritance hierarchy; effective serialized material state is checked after conversion.",
					Apply = c => ReparentMaterialVariant(c, parentPath, childFamily, childSignature),
					Verify = c => VerifyMaterialVariantParent(c, parentPath, childSignature)
				});
			}
			return options;
		}

		static string DescribeMaterialVariantDiff(Material child, Material parent)
		{
			if (child == null || parent == null || child.shader == null || child.shader != parent.shader)
				return "Convert this material to a Material Variant; compatibility preview is unavailable.";
			var changes = new List<string>();
			try
			{
				var properties = GetShaderProperties(child.shader);
				for (int i = 0; i < properties.Length; i++)
				{
					string name = properties[i].name;
					var type = properties[i].type;
					string left = GetMaterialPropertyValueText(child, name, type);
					string right = GetMaterialPropertyValueText(parent, name, type);
					if (!string.Equals(left, right, StringComparison.Ordinal))
						changes.Add(name + ": child=" + left + ", parent=" + right);
				}
			}
			catch { return "Convert " + child.name + " to a Material Variant of " + parent.name + "; property diff preview failed, so postcondition verification remains mandatory."; }
			if (changes.Count == 0) return child.name + " becomes a Material Variant of " + parent.name + "; no exposed shader-property differences were detected.";
			const int maxPreview = 8;
			var shown = changes.GetRange(0, Math.Min(changes.Count, maxPreview));
			string preview = string.Join("; ", shown.ToArray());
			if (changes.Count > maxPreview) preview += "; and " + (changes.Count - maxPreview) + " additional property difference(s)";
			return child.name + " becomes a Material Variant of " + parent.name + ". Expected child overrides: " + preview;
		}

		static string GetMaterialPropertyValueText(Material material, string name, ShaderPropertyType type)
		{
			switch (type)
			{
				case ShaderPropertyType.Color:
					Color color = material.GetColor(name);
					return string.Format(CultureInfo.InvariantCulture, "({0:R},{1:R},{2:R},{3:R})", color.r, color.g, color.b, color.a);
				case ShaderPropertyType.Vector:
					Vector4 vector = material.GetVector(name);
					return string.Format(CultureInfo.InvariantCulture, "({0:R},{1:R},{2:R},{3:R})", vector.x, vector.y, vector.z, vector.w);
				case ShaderPropertyType.Float:
				case ShaderPropertyType.Range:
					return material.GetFloat(name).ToString("R", CultureInfo.InvariantCulture);
				case ShaderPropertyType.Int:
					return material.GetInteger(name).ToString(CultureInfo.InvariantCulture);
				case ShaderPropertyType.Texture:
					var texture = material.GetTexture(name);
					return (texture != null ? AssetDatabase.GetAssetPath(texture) : "-")
						+ " scale=" + material.GetTextureScale(name) + " offset=" + material.GetTextureOffset(name);
				default: return "<unsupported>";
			}
		}

		bool HasMaterialVariantDescendants(Material material, bool refresh = false)
		{
			if (material == null) return true;
			try
			{
				if (refresh || !m_MaterialVariantTreeBuilt)
				{
					m_MaterialVariantAncestorIds.Clear();
					string[] guids = AssetDatabase.FindAssets("t:Material");
					for (int i = 0; i < guids.Length; i++)
					{
						string path = AssetDatabase.GUIDToAssetPath(guids[i]);
						if (string.IsNullOrEmpty(path)) continue;
						var candidate = AssetDatabase.LoadAssetAtPath<Material>(path);
						if (candidate == null || candidate == material) continue;
						var visited = new HashSet<int>();
						var parent = candidate.parent;
						int depth = 0;
						while (parent != null && depth++ < 256 && visited.Add(parent.GetInstanceID()))
						{
							m_MaterialVariantAncestorIds.Add(parent.GetInstanceID());
							parent = parent.parent;
						}
					}
					m_MaterialVariantTreeBuilt = true;
				}
				return m_MaterialVariantAncestorIds.Contains(material.GetInstanceID());
			}
			catch { m_MaterialVariantTreeBuilt = false; return true; }
		}

		void ReparentMaterialVariant(FixContext c, string parentPath, string expectedFamily, string expectedSignature)
		{
			var child = c.Asset as Material ?? AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			var parent = AssetDatabase.LoadAssetAtPath<Material>(parentPath);
			if (child == null || parent == null || child == parent || child.shader == null || parent.shader != child.shader)
				throw new InvalidOperationException("Child or candidate parent is unavailable/incompatible.");
			if (HasMaterialVariantDescendants(child, refresh: true))
				throw new InvalidOperationException("Reparenting a material with existing descendants could alter inherited values; the option is blocked.");
			if (!string.Equals(BuildFamilySignature(child), expectedFamily, StringComparison.Ordinal)
				|| !string.Equals(BuildSignature(child), expectedSignature, StringComparison.Ordinal)
				|| !string.Equals(BuildFamilySignature(parent), expectedFamily, StringComparison.Ordinal))
				throw new InvalidOperationException("Material-family or effective-property evidence changed after analysis.");
			if (parent.IsChildOf(child)) throw new InvalidOperationException("The selected parent would create a Material Variant cycle.");
			if (child.parent == parent) throw new InvalidOperationException("The selected material is already this material's parent.");
			c.RequireBackup(c.AssetPath);
			child.parent = parent;
			EditorUtility.SetDirty(child);
			c.NeedsSave = true;
		}

		static bool VerifyMaterialVariantParent(FixContext c, string parentPath, string expectedSignature)
		{
			var saved = AssetDatabase.LoadAssetAtPath<Material>(c.AssetPath);
			var parent = AssetDatabase.LoadAssetAtPath<Material>(parentPath);
			return saved != null && parent != null && saved.parent == parent
				&& string.Equals(BuildSignature(saved), expectedSignature, StringComparison.Ordinal);
		}

		static string SanitizeActionId(string value)
		{
			if (string.IsNullOrEmpty(value)) return "candidate";
			var sb = new StringBuilder(value.Length);
			for (int i = 0; i < value.Length; i++) sb.Append(char.IsLetterOrDigit(value[i]) ? char.ToLowerInvariant(value[i]) : '-');
			return sb.ToString().Trim('-');
		}

		sealed class ShaderSourceEdit
		{
			public int LineIndex;
			public string ExpectedLine;
			public string ReplacementLine;
			public string Summary;
		}

		static string ExtractFirstArgument(string arguments)
		{
			if (string.IsNullOrWhiteSpace(arguments)) return string.Empty;
			string value = arguments.Trim();
			int comma = value.IndexOf(',');
			if (comma >= 0) value = value.Substring(0, comma).Trim();
			return value;
		}

		static bool TryGetStringLiteral(string expression, out string value)
		{
			value = null;
			if (string.IsNullOrEmpty(expression)) return false;
			string trimmed = expression.Trim();
			if (trimmed.Length < 2 || trimmed[0] != '"' || trimmed[trimmed.Length - 1] != '"') return false;
			value = trimmed.Substring(1, trimmed.Length - 2);
			return value.Length > 0;
		}

		void EnsureRuntimeSourceIndex()
		{
			if (m_RuntimeSourceIndexBuilt) return;
			m_RuntimeSourceIndexBuilt = true;
			m_RuntimeSourceIndexComplete = true;
			m_RuntimeHasDynamicKeywordControl = false;
			m_RuntimeSourceHasDynamicMaterialPropertyUse = false;
			m_RuntimeSourceHasDynamicTransparencyPropertyUse = false;
			m_RuntimeSourceUsesMaterialPropertyBlock = false;
			string[] paths;
			try { paths = AssetDatabase.GetAllAssetPaths(); }
			catch { m_RuntimeSourceIndexComplete = false; return; }
			for (int i = 0; i < paths.Length; i++)
			{
				string path = paths[i];
				if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
					|| path.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
				try
				{
					string fullPath = AssetPathUtility.ToAbsolutePath(path);
					if (!File.Exists(fullPath)) { m_RuntimeSourceIndexComplete = false; continue; }
					string text = File.ReadAllText(fullPath);
					if (Regex.IsMatch(text, @"\b(?:MaterialPropertyBlock|SetPropertyBlock|GetPropertyBlock)\b", RegexOptions.CultureInvariant))
						m_RuntimeSourceUsesMaterialPropertyBlock = true;

					var keywordCalls = Regex.Matches(text, @"\b(?:EnableKeyword|DisableKeyword|EnableShaderKeyword|DisableShaderKeyword|SetKeyword)\s*\(([^)]*)\)", RegexOptions.CultureInvariant);
					for (int k = 0; k < keywordCalls.Count; k++)
					{
						string firstArgument = ExtractFirstArgument(keywordCalls[k].Groups[1].Value);
						if (TryGetStringLiteral(firstArgument, out string literalKeyword)) m_RuntimeControlledKeywords.Add(literalKeyword);
						else if (!string.IsNullOrEmpty(firstArgument)) m_RuntimeHasDynamicKeywordControl = true;
					}
					var globalKeywordCalls = Regex.Matches(text, @"\bGlobalKeyword\s*\(([^)]*)\)|\bGlobalKeyword\s*\.\s*(?:GetOrCreate|Create)\s*\(([^)]*)\)", RegexOptions.CultureInvariant);
					for (int k = 0; k < globalKeywordCalls.Count; k++)
					{
						string args = !string.IsNullOrEmpty(globalKeywordCalls[k].Groups[1].Value) ? globalKeywordCalls[k].Groups[1].Value : globalKeywordCalls[k].Groups[2].Value;
						string firstArgument = ExtractFirstArgument(args);
						if (TryGetStringLiteral(firstArgument, out string literalKeyword)) m_RuntimeControlledKeywords.Add(literalKeyword);
						else if (!string.IsNullOrEmpty(firstArgument)) m_RuntimeHasDynamicKeywordControl = true;
					}
					var propertyCalls = Regex.Matches(text,
						@"\b(?:SetTexture|GetTexture|SetTextureScale|GetTextureScale|SetTextureOffset|GetTextureOffset|SetFloat|GetFloat|SetColor|GetColor|SetVector|GetVector|SetInt|GetInt|SetInteger|GetInteger|SetMatrix|GetMatrix|SetBuffer|PropertyToID)\s*\(([^)]*)\)",
						RegexOptions.CultureInvariant);
					for (int k = 0; k < propertyCalls.Count; k++)
					{
						string firstArgument = ExtractFirstArgument(propertyCalls[k].Groups[1].Value);
						if (!TryGetStringLiteral(firstArgument, out string literalProperty))
						{
							if (!string.IsNullOrEmpty(firstArgument))
							{
								string call = propertyCalls[k].Value;
								if (Regex.IsMatch(call, @"^\s*(?:SetTexture|GetTexture|SetTextureScale|GetTextureScale|SetTextureOffset|GetTextureOffset|PropertyToID)\b", RegexOptions.CultureInvariant))
									m_RuntimeSourceHasDynamicMaterialPropertyUse = true;
								m_RuntimeSourceHasDynamicTransparencyPropertyUse = true;
							}
							continue;
						}
						if (IsTransparencyRelatedMaterialProperty(literalProperty)) m_RuntimeControlledMaterialProperties.Add(literalProperty);
					}
					foreach (Match match in Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant))
						m_RuntimeSourceTokens.Add(match.Value);
				}
				catch { m_RuntimeSourceIndexComplete = false; }
			}
		}

		static bool IsTransparencyRelatedMaterialProperty(string propertyName)
		{
			if (string.IsNullOrEmpty(propertyName)) return false;
			string[] properties =
			{
				"_Surface", "_AlphaClip", "_BaseColor", "_Color", "_BaseMap", "_MainTex",
				"_Blend", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_ZWrite"
			};
			for (int i = 0; i < properties.Length; i++)
				if (string.Equals(propertyName, properties[i], StringComparison.Ordinal)) return true;
			return false;
		}

		static bool IsTransparencyRelatedAnimationBinding(string propertyName)
		{
			if (string.IsNullOrEmpty(propertyName)) return false;
			// Material swaps can change surface mode without animating a shader property.
			if (propertyName.IndexOf("m_Materials", StringComparison.OrdinalIgnoreCase) >= 0
				|| string.Equals(propertyName, "m_Material", StringComparison.OrdinalIgnoreCase)) return true;
			string[] properties =
			{
				"_Surface", "_AlphaClip", "_BaseColor", "_Color", "_BaseMap", "_MainTex",
				"_Blend", "_SrcBlend", "_DstBlend", "_SrcBlendAlpha", "_DstBlendAlpha", "_ZWrite"
			};
			for (int i = 0; i < properties.Length; i++)
				if (propertyName.IndexOf(properties[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
			return false;
		}

		void EnsureTransparencyAnimationIndex()
		{
			if (m_TransparencyAnimationIndexBuilt) return;
			m_TransparencyAnimationIndexBuilt = true;
			m_TransparencyAnimationIndexComplete = true;
			m_TransparencyAnimationControlsMaterialState = false;
			string[] paths;
			try { paths = AssetDatabase.GetAllAssetPaths(); }
			catch { m_TransparencyAnimationIndexComplete = false; return; }
			for (int i = 0; i < paths.Length; i++)
			{
				string path = paths[i];
				string extension = Path.GetExtension(path);
				if (!string.Equals(extension, ".anim", StringComparison.OrdinalIgnoreCase)
					&& !string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase)) continue;
				try
				{
					var assets = AssetDatabase.LoadAllAssetsAtPath(path);
					if (assets == null) { m_TransparencyAnimationIndexComplete = false; continue; }
					for (int a = 0; a < assets.Length; a++)
					{
						var clip = assets[a] as AnimationClip;
						if (clip == null) continue;
						var floatBindings = AnimationUtility.GetCurveBindings(clip);
						for (int b = 0; b < floatBindings.Length; b++)
							if (IsTransparencyRelatedAnimationBinding(floatBindings[b].propertyName))
								 m_TransparencyAnimationControlsMaterialState = true;
						var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
						for (int b = 0; b < objectBindings.Length; b++)
							if (IsTransparencyRelatedAnimationBinding(objectBindings[b].propertyName))
								 m_TransparencyAnimationControlsMaterialState = true;
					}
				}
				catch { m_TransparencyAnimationIndexComplete = false; }
			}
		}

		bool CanSafelyMakeOpaque(ScanContext ctx)
		{
			if (ctx == null || ctx.Settings == null || !ctx.Settings.projectProofPass) return false;
			var proof = ctx.GetProofIndex();
			if (proof == null || !proof.CoverageComplete) return false;
			EnsureRuntimeSourceIndex();
			if (!m_RuntimeSourceIndexComplete || m_RuntimeSourceHasDynamicTransparencyPropertyUse
				|| m_RuntimeSourceHasDynamicMaterialPropertyUse || m_RuntimeSourceUsesMaterialPropertyBlock) return false;
			foreach (string property in m_RuntimeControlledMaterialProperties)
				if (IsTransparencyRelatedMaterialProperty(property)) return false;
			EnsureTransparencyAnimationIndex();
			return m_TransparencyAnimationIndexComplete && !m_TransparencyAnimationControlsMaterialState;
		}

		bool IsKeywordUsedOutsideShaderCode(string keyword, string shaderSource, int skippedLineIndex, string shaderPath, ProjectProofIndex proof)
		{
			if (string.IsNullOrEmpty(keyword)) return true;
			if (m_RuntimeSourceTokens.Contains(keyword)) return true;
			if (proof == null || !proof.CoverageComplete || proof.ContainsTokenOutsideFile(keyword, shaderPath)) return true;
			string normalized = shaderSource.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			string pattern = @"\b" + Regex.Escape(keyword) + @"\b";
			for (int i = 0; i < lines.Length; i++)
				if (i != skippedLineIndex && Regex.IsMatch(lines[i], pattern, RegexOptions.CultureInvariant)) return true;
			return false;
		}

		List<ShaderSourceEdit> BuildUnusedLocalKeywordEdits(string source, string shaderPath, ProjectProofIndex proof)
		{
			var edits = new List<ShaderSourceEdit>();
			if (string.IsNullOrEmpty(source) || proof == null || !proof.CoverageComplete
				|| !m_RuntimeSourceIndexBuilt || !m_RuntimeSourceIndexComplete) return edits;
			string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			var pragmas = ParseShaderKeywordPragmas(source);
			foreach (var pragma in pragmas)
			{
				if (!(pragma.Directive.StartsWith("multi_compile_local", StringComparison.Ordinal)
					|| pragma.Directive.StartsWith("shader_feature_local", StringComparison.Ordinal))) continue;
				if (pragma.LineIndex < 0 || pragma.LineIndex >= lines.Length) continue;
				var removable = new HashSet<string>(StringComparer.Ordinal);
				for (int t = 0; t < pragma.Tokens.Count; t++)
				{
					string token = pragma.Tokens[t];
					if (token == "_" || token == "__") continue;
					if (IsKeywordUsedOutsideShaderCode(token, source, pragma.LineIndex, shaderPath, proof)) continue;
					removable.Add(token);
				}
				if (removable.Count == 0) continue;
				string rewritten = RewriteShaderKeywordPragmaRemovingTokens(lines[pragma.LineIndex], pragma, removable);
				if (!string.Equals(rewritten, lines[pragma.LineIndex], StringComparison.Ordinal))
					edits.Add(new ShaderSourceEdit { LineIndex = pragma.LineIndex, ExpectedLine = lines[pragma.LineIndex], ReplacementLine = rewritten, Summary = "Remove unused token(s): " + string.Join(", ", new List<string>(removable).ToArray()) });
			}
			return edits;
		}

		List<ShaderSourceEdit> BuildShaderFeatureConversionEdits(string source, string shaderName, HashSet<string> materialKeywords, bool localize)
		{
			var edits = new List<ShaderSourceEdit>();
			if (string.IsNullOrEmpty(source) || !m_RuntimeSourceIndexBuilt || !m_RuntimeSourceIndexComplete || m_RuntimeHasDynamicKeywordControl) return edits;
			string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			var pragmas = ParseShaderKeywordPragmas(source);
			foreach (var pragma in pragmas)
			{
				// Restrict to canonical global multi_compile declarations. Stage-specific directives have
				// different semantics and are left alone until their corresponding feature directive is proven.
				if (!string.Equals(pragma.Directive, "multi_compile", StringComparison.Ordinal)) continue;
				if (pragma.LineIndex < 0 || pragma.LineIndex >= lines.Length) continue;
				bool hasMaterialKeyword = false;
				bool allKeywordsShaderLocal = true;
				bool hasRuntimeUse = false;
				bool hasRealKeyword = false;
				for (int t = 0; t < pragma.Tokens.Count; t++)
				{
					string token = pragma.Tokens[t];
					if (token == "_" || token == "__") continue;
					hasRealKeyword = true;
					if (materialKeywords != null && materialKeywords.Contains(token)) hasMaterialKeyword = true;
					if (m_RuntimeControlledKeywords.Contains(token)) { hasRuntimeUse = true; allKeywordsShaderLocal = false; continue; }
					if (!m_ShadersUsingMaterialKeyword.TryGetValue(token, out var users) || users.Count != 1 || !users.Contains(shaderName))
						allKeywordsShaderLocal = false;
				}
				if (!hasRealKeyword || !hasMaterialKeyword || hasRuntimeUse) continue;
				if (localize != allKeywordsShaderLocal) continue;
				string replacementDirective = localize ? "shader_feature_local" : "shader_feature";
				string rewritten = ReplaceShaderPragmaDirective(lines[pragma.LineIndex], replacementDirective);
				if (!string.Equals(rewritten, lines[pragma.LineIndex], StringComparison.Ordinal))
					edits.Add(new ShaderSourceEdit { LineIndex = pragma.LineIndex, ExpectedLine = lines[pragma.LineIndex], ReplacementLine = rewritten, Summary = "Convert to #pragma " + replacementDirective });
			}
			return edits;
		}

		static string RewriteShaderKeywordPragmaRemovingTokens(string line, ShaderKeywordPragma pragma, HashSet<string> removals)
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
			var kept = new List<string>();
			for (int i = 0; i < pragma.Tokens.Count; i++)
				if (!removals.Contains(pragma.Tokens[i])) kept.Add(pragma.Tokens[i]);
			if (kept.Count == 0) kept.Add("_");
			return prefix + string.Join(" ", kept.ToArray()) + (string.IsNullOrEmpty(comment) ? string.Empty : " " + comment);
		}

		static string ReplaceShaderPragmaDirective(string line, string replacementDirective)
		{
			int pragmaIndex = line.IndexOf("#pragma", StringComparison.OrdinalIgnoreCase);
			if (pragmaIndex < 0) return line;
			int directiveIndex = pragmaIndex + "#pragma".Length;
			while (directiveIndex < line.Length && char.IsWhiteSpace(line[directiveIndex])) directiveIndex++;
			int end = directiveIndex;
			while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
			if (end == directiveIndex) return line;
			return line.Substring(0, directiveIndex) + replacementDirective + line.Substring(end);
		}

		static List<string> DescribeShaderEdits(List<ShaderSourceEdit> edits)
		{
			var descriptions = new List<string>();
			if (edits == null) return descriptions;
			for (int i = 0; i < edits.Count; i++) descriptions.Add("Line " + (edits[i].LineIndex + 1) + ": " + edits[i].Summary);
			return descriptions;
		}

		static void ApplyShaderSourceEdits(FixContext c, IList<ShaderSourceEdit> edits)
		{
			if (edits == null || edits.Count == 0) throw new InvalidOperationException("No safe shader source edits were captured.");
			if (!VerifyShaderCompiles(c)) throw new InvalidOperationException("The shader currently has compile errors; source edits are blocked.");
			string fullPath = AssetPathUtility.ToAbsolutePath(c.AssetPath);
			byte[] originalBytes = File.ReadAllBytes(fullPath);
			bool hadUtf8Bom = originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;
			bool hadCrLf = Array.IndexOf(originalBytes, (byte)'\r') >= 0 && Array.IndexOf(originalBytes, (byte)'\n') >= 0;
			bool hadLoneCr = !hadCrLf && Array.IndexOf(originalBytes, (byte)'\r') >= 0;
			string original = File.ReadAllText(fullPath);
			string normalized = original.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			var byIndex = new Dictionary<int, ShaderSourceEdit>();
			for (int i = 0; i < edits.Count; i++)
			{
				var edit = edits[i];
				if (edit == null || edit.LineIndex < 0 || edit.LineIndex >= lines.Length || !string.Equals(lines[edit.LineIndex], edit.ExpectedLine, StringComparison.Ordinal))
					throw new InvalidOperationException("Shader source changed after analysis; no edits were written.");
				if (byIndex.ContainsKey(edit.LineIndex)) throw new InvalidOperationException("Conflicting edits target the same shader source line.");
				byIndex.Add(edit.LineIndex, edit);
			}
			c.RequireBackup(c.AssetPath);
			for (int i = 0; i < lines.Length; i++)
				if (byIndex.TryGetValue(i, out var edit)) lines[i] = edit.ReplacementLine;
			string newline = hadCrLf ? "\r\n" : hadLoneCr ? "\r" : "\n";
			string output = string.Join("\n", lines).Replace("\n", newline);
			byte[] outputBody = new UTF8Encoding(false).GetBytes(output);
			if (hadUtf8Bom)
			{
				var withBom = new byte[outputBody.Length + 3];
				withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
				Buffer.BlockCopy(outputBody, 0, withBom, 3, outputBody.Length);
				outputBody = withBom;
			}
			File.WriteAllBytes(fullPath, outputBody);
			AssetDatabase.ImportAsset(c.AssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
			c.NeedsReimport = true;
		}

		static bool VerifyShaderSourceEditsAndCompilation(FixContext c, IList<ShaderSourceEdit> edits)
		{
			try
			{
				string normalized = File.ReadAllText(AssetPathUtility.ToAbsolutePath(c.AssetPath)).Replace("\r\n", "\n").Replace('\r', '\n');
				var lines = normalized.Split('\n');
				if (edits == null || edits.Count == 0) return false;
				for (int i = 0; i < edits.Count; i++)
				{
					var edit = edits[i];
					if (edit.LineIndex < 0 || edit.LineIndex >= lines.Length || !string.Equals(lines[edit.LineIndex], edit.ReplacementLine, StringComparison.Ordinal)) return false;
				}
				return VerifyShaderCompiles(c);
			}
			catch { return false; }
		}

		static bool VerifyShaderCompiles(FixContext c)
		{
			try
			{
				var shader = AssetDatabase.LoadAssetAtPath<Shader>(c.AssetPath);
				return shader != null && !ShaderUtil.GetShaderInfo(shader).hasErrors;
			}
			catch { return false; }
		}

		bool HasProjectReferenceOutsideAlwaysIncluded(string guid, string shaderName, string shaderPath)
		{
			try
			{
				var paths = new List<string>(AssetDatabase.GetAllAssetPaths());
				const string graphicsPath = "ProjectSettings/GraphicsSettings.asset";
				if (!paths.Contains(graphicsPath)) paths.Add(graphicsPath);
				for (int i = 0; i < paths.Count; i++)
				{
					string path = paths[i];
					if (string.Equals(path, shaderPath, StringComparison.OrdinalIgnoreCase)) continue;
					string ext = Path.GetExtension(path).ToLowerInvariant();
					if (!(ext == ".mat" || ext == ".prefab" || ext == ".unity" || ext == ".asset" || ext == ".shader"
						|| ext == ".shadergraph" || ext == ".shadervariants" || ext == ".cs" || ext == ".hlsl" || ext == ".cginc"
						|| ext == ".compute" || ext == ".json" || ext == ".xml" || ext == ".txt" || string.Equals(path, graphicsPath, StringComparison.Ordinal))) continue;
					string full = AssetPathUtility.ToAbsolutePath(path);
					if (!File.Exists(full)) continue;
					string text;
					try { text = File.ReadAllText(full); }
					catch { return true; }
					if (string.Equals(path, graphicsPath, StringComparison.Ordinal)) text = RemoveAlwaysIncludedGuidLine(text, guid);
					if ((!string.IsNullOrEmpty(guid) && text.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
						|| (!string.IsNullOrEmpty(shaderName) && text.IndexOf(shaderName, StringComparison.Ordinal) >= 0)) return true;
				}
				return false;
			}
			catch { return true; }
		}

		static string RemoveAlwaysIncludedGuidLine(string settingsText, string guid)
		{
			if (string.IsNullOrEmpty(settingsText) || string.IsNullOrEmpty(guid)) return settingsText;
			string normalized = settingsText.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			int section = -1;
			for (int i = 0; i < lines.Length; i++) if (lines[i].Trim() == "m_AlwaysIncludedShaders:") { section = i; break; }
			if (section < 0) return settingsText;
			int end = lines.Length;
			for (int i = section + 1; i < lines.Length; i++)
				if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && Regex.IsMatch(lines[i], @"^[A-Za-z_][A-Za-z0-9_]*:\s*", RegexOptions.CultureInvariant)) { end = i; break; }
			var kept = new List<string>(lines.Length);
			for (int i = 0; i < lines.Length; i++)
			{
				bool remove = i > section && i < end && lines[i].IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0;
				if (!remove) kept.Add(lines[i]);
			}
			string result = string.Join("\n", kept.ToArray());
			return settingsText.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? result.Replace("\n", "\r\n") : result;
		}

		static bool IsShaderInAlwaysIncluded(string settingsText, string guid)
		{
			if (string.IsNullOrEmpty(settingsText) || string.IsNullOrEmpty(guid)) return false;
			string normalized = settingsText.Replace("\r\n", "\n").Replace('\r', '\n');
			var lines = normalized.Split('\n');
			int section = -1;
			for (int i = 0; i < lines.Length; i++) if (lines[i].Trim() == "m_AlwaysIncludedShaders:") { section = i; break; }
			if (section < 0) return false;
			for (int i = section + 1; i < lines.Length; i++)
			{
				if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && Regex.IsMatch(lines[i], @"^[A-Za-z_][A-Za-z0-9_]*:\s*", RegexOptions.CultureInvariant)) break;
				if (lines[i].IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		static void RemoveShaderFromAlwaysIncluded(FixContext c, string guid, string shaderName)
		{
			const string settingsPath = "ProjectSettings/GraphicsSettings.asset";
			string full = AssetPathUtility.ToAbsolutePath(settingsPath);
			if (!File.Exists(full)) throw new InvalidOperationException("GraphicsSettings.asset is unavailable.");
			string text = File.ReadAllText(full);
			if (!IsShaderInAlwaysIncluded(text, guid)) throw new InvalidOperationException("Shader GUID is no longer in Always Included.");
			if (HasAnyProjectReferenceOtherThanAlwaysIncluded(guid, shaderName, c.AssetPath, settingsPath))
				throw new InvalidOperationException("A project asset/source reference was found outside Always Included.");
			string revised = RemoveAlwaysIncludedGuidLine(text, guid);
			if (string.Equals(revised, text, StringComparison.Ordinal)) throw new InvalidOperationException("Could not isolate the Always Included entry.");
			if (c.TryBackupExtra == null || !c.TryBackupExtra(settingsPath)) throw new InvalidOperationException("Could not back up GraphicsSettings.asset.");
			File.WriteAllText(full, revised, new UTF8Encoding(false));
			c.NeedsReimport = true;
		}

		static bool HasAnyProjectReferenceOtherThanAlwaysIncluded(string guid, string shaderName, string shaderPath, string graphicsSettingsPath)
		{
			try
			{
				var paths = new List<string>(AssetDatabase.GetAllAssetPaths());
				if (!paths.Contains(graphicsSettingsPath)) paths.Add(graphicsSettingsPath);
				for (int i = 0; i < paths.Count; i++)
				{
					string path = paths[i];
					if (string.Equals(path, shaderPath, StringComparison.OrdinalIgnoreCase)) continue;
					string ext = Path.GetExtension(path).ToLowerInvariant();
					if (!(ext == ".mat" || ext == ".prefab" || ext == ".unity" || ext == ".asset" || ext == ".shader"
						|| ext == ".shadergraph" || ext == ".shadervariants" || ext == ".cs" || ext == ".hlsl" || ext == ".cginc"
						|| ext == ".compute" || ext == ".json" || ext == ".xml" || ext == ".txt" || string.Equals(path, graphicsSettingsPath, StringComparison.Ordinal))) continue;
					string full = AssetPathUtility.ToAbsolutePath(path);
					if (!File.Exists(full)) continue;
					string contents;
					try { contents = File.ReadAllText(full); } catch { return true; }
					if (string.Equals(path, graphicsSettingsPath, StringComparison.Ordinal)) contents = RemoveAlwaysIncludedGuidLine(contents, guid);
					if ((!string.IsNullOrEmpty(guid) && contents.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0)
						|| (!string.IsNullOrEmpty(shaderName) && contents.IndexOf(shaderName, StringComparison.Ordinal) >= 0)) return true;
				}
				return false;
			}
			catch { return true; }
		}

		static bool VerifyShaderRemovedFromAlwaysIncluded(FixContext c, string guid)
		{
			try
			{
				string full = AssetPathUtility.ToAbsolutePath("ProjectSettings/GraphicsSettings.asset");
				return File.Exists(full) && !IsShaderInAlwaysIncluded(File.ReadAllText(full), guid);
			}
			catch { return false; }
		}

	}
}
