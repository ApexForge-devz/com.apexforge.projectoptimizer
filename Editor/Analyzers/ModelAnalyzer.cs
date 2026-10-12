using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Geometry budgets and ModelImporter settings for imported model assets. Verified settings use
	/// deterministic importer actions and reimport verifiers; dependency-sensitive rig/readability
	/// changes are automatically gated and retain a Review path for assets whose consumers are unknown.
	/// </summary>
	public sealed class ModelAnalyzer : IAssetAnalyzer
	{
		sealed class MaterialMappingExpectation
		{
			public readonly Dictionary<string, string> SlotToSource = new Dictionary<string, string>(StringComparer.Ordinal);
			public readonly Dictionary<string, string> SourceObjectToDestination = new Dictionary<string, string>(StringComparer.Ordinal);
			public readonly Dictionary<AssetImporter.SourceAssetIdentifier, string> IdentifierToDestination = new Dictionary<AssetImporter.SourceAssetIdentifier, string>();
		}
		static readonly string[] k_Extensions =
		{
			".fbx", ".obj", ".dae", ".blend", ".3ds", ".max", ".ma", ".mb", ".c4d", ".dxf", ".gltf", ".glb"
		};

		public AssetCategory Category => AssetCategory.Model;

		public AssetKindMask SupportedKinds => AssetKindMask.Model;

		static ModelAnalyzer()
		{
			Rules.Register(id: "MDL001", title: "Read/Write enabled", category: AssetCategory.Model,
							why: "Read/Write keeps mesh data in both GPU-addressable and CPU-addressable memory. Unity recommends disabling it when runtime code and runtime systems do not need CPU mesh access.",
							fix: "Disable Read/Write automatically for an unreferenced asset when a complete current project-wide reference graph is available; otherwise review runtime Mesh consumers before disabling.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL002", title: "Mesh compression disabled", category: AssetCategory.Model,
							why: "Mesh compression can reduce stored/build mesh data, but increasing compression increases vertex-data error. The acceptable level depends on the asset and visual tolerance.",
							fix: "Set Mesh Compression to Medium by default and verify the importer state after reimport; correct exceptional assets manually if visual quality is affected.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL003", title: "Mesh GPU optimization disabled", category: AssetCategory.Model,
							why: "Unity can reorder mesh vertices and polygons to improve GPU cache utilization. This can change data ordering, so the project must not depend on strict source ordering.",
							fix: "Enable vertex and polygon optimization and verify both importer flags after reimport; adjust exceptional assets if a downstream workflow depends on ordering.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: true
							);

			Rules.Register(id: "MDL004", title: "Unnecessary rig on static model", category: AssetCategory.Model,
							why: "Animation Type other than None causes Unity to import animation-related data. A model with no local clips and no detected skinned mesh may still intentionally provide a rig or Avatar for animation clips stored elsewhere.",
							fix: "Set Animation Type to None automatically only when the model is unreferenced in a complete project-wide graph; otherwise review external rig/Avatar consumers.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL005", title: "Animation import enabled with no clips", category: AssetCategory.Model,
							why: "Import Animation is enabled even though this model currently contains no imported animation clips. Disabling it can reduce unnecessary animation import work, but the setting may be intentional for assets that are expected to gain clips or serve as animation sources.",
							fix: "Disable Import Animation automatically only when no static dependents exist in a complete project-wide reference graph; otherwise review consumers.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL006", title: "Blend shape import enabled but no blend shapes are present", category: AssetCategory.Model,
							why: "Import BlendShapes is enabled even though this model currently contains no blend shape data. Disabling it can reduce unnecessary import handling, but the setting may be intentional for source assets that are expected to gain blend shapes later.",
							fix: "Disable Import BlendShapes when no blend shape data is present in the imported model; re-enable it for future source revisions that add blend shapes.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);

			Rules.Register(id: "MDL007", title: "DCC cameras, lights or visibility imported", category: AssetCategory.Model,
							why: "ModelImporter can import camera, light and visibility data from the DCC file. These can be deliberate content, so the analyzer cannot safely decide that they are unused.",
							fix: "Disable only camera, light or visibility import categories for which corresponding content is detected in the imported model.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: true
							);

			Rules.Register(id: "MDL008", title: "Tangents imported without detected normal-map usage", category: AssetCategory.Model,
							why: "Tangents are commonly required by tangent-space normal mapping, but runtime materials and shaders can differ from the materials imported from the model.",
							fix: "Set imported tangents to None when analyzer-collected material evidence finds no normal-map use; restore tangents on exceptional runtime shader paths.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL009", title: "Very high geometry count", category: AssetCategory.Model,
							why: "High vertex and triangle counts increase vertex processing, memory bandwidth and imported asset size. There is no universal safe geometry count because acceptable budgets depend on platform and viewing conditions.",
							fix: "Reduce unnecessary geometry, split oversized assets, or add appropriate LODs based on the target platform and camera distances.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL010", title: "32-bit index buffer where 16-bit is possible", category: AssetCategory.Model,
							why: "A 16-bit index buffer supports meshes up to 65535 vertices and uses less index memory than 32-bit indices. Unity's Auto mode selects the appropriate format from mesh vertex count.",
							fix: "Set Index Format to Auto so Unity selects 16-bit when every imported mesh fits.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.Proven,
								recommendationConfidence: OptimizationConfidence.Proven,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL011", title: "Many sub-meshes", category: AssetCategory.Model,
							why: "Each sub-mesh can require separate material state and rendering work. A high count can indicate material fragmentation, but merging is content-dependent.",
							fix: "Reduce unnecessary material slots or consolidate materials where the intended visual result is preserved.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL012", title: "Vertex welding disabled", category: AssetCategory.Model,
							why: "Weld Vertices can merge vertices that share positions, reducing duplicated vertex data, but vertices may intentionally be split for normals, UVs, tangents or other attributes.",
							fix: "Enable Weld Vertices and verify a valid reimport; correct assets manually when intentional vertex splits are required for shading, UVs or downstream processing.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL013", title: "Materials are imported from the model", category: AssetCategory.Model,
							why: "Imported materials can create project-owned materials derived from the source asset. Extracting embedded materials into standalone project assets provides explicit, reusable material references and makes them independently configurable for optimization and maintenance.",
							fix: "Extract embedded materials from the model into project-managed .mat assets and remap the model to use the extracted materials.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL014", title: "Secondary lightmap UV generation enabled", category: AssetCategory.Model,
							why: "Generate Secondary UV creates a second UV set for lightmapping. Disabling it is correct only when this asset does not participate in a workflow that needs the generated UV set.",
							fix: "Disable generated secondary UVs automatically only when a complete/current project reference graph proves the model has no static consumers and it is outside known dynamic-loading paths; otherwise review lightmapping use.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL015", title: "Optimize Game Objects disabled on a skinned model", category: AssetCategory.Model,
							why: "Optimize Game Objects removes Transform-only GameObjects from an imported character hierarchy unless they are exposed. This can improve animation performance, but runtime scripts, constraints, sockets and attachments may need specific transforms.",
							fix: "Enable Optimize Game Objects on eligible skinned models; expose required runtime transform paths or revert the setting on exceptional assets.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL016", title: "High maximum bone influences per vertex", category: AssetCategory.Model,
							why: "Higher maximum bone influence counts increase skinning work. Unity notes that performance cost becomes more significant above four influences, while lowering the importer value discards the lowest-weight influences.",
							fix: "Use a maximum of four bone influences per vertex; restore a higher limit for rigs whose deformation quality requires it.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL017", title: "Unreferenced rig bones are imported", category: AssetCategory.Model,
							why: "Optimize Bones can import only bones connected to vertices, reducing unused rig data. Unweighted helper, IK, attachment or scripted bones may still be intentionally required.",
							fix: "Enable Optimize Bones on skinned models; disable it again when unweighted helper, IK, attachment or script-driven bones are required at runtime.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "MDL018", title: "Bone-weight pruning is disabled", category: AssetCategory.Model,
							why: "ModelImporter.minBoneWeight controls which low-weight influences are discarded. Increasing it can reduce skinning data, but it changes deformation and is therefore content-dependent.",
							fix: "Review the rig and raise the minimum only when the discarded influences are genuinely insignificant.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: true
							);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			for (int i = 0; i < k_Extensions.Length; i++)
				if (assetPath.EndsWith(k_Extensions[i], StringComparison.OrdinalIgnoreCase))
					return true;
			return false;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			var settings = OptimizerSettings.Instance;
			var facts = ModelAnalysisShared.GetGeometry(assetPath, ctx);
			if (facts == null) return null;

			long fileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath);
			var entry = new AssetEntry
			{
				Guid = ctx.GetGuid(assetPath),
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = AssetCategory.Model,
				TypeLabel = "Model",
				FileSize = fileSize
			};

			long vertices = facts.vertices;
			long largestMesh = facts.largestMesh;
			long triangles = facts.triangles;
			int submeshes = facts.submeshes;
			int meshCount = facts.meshCount;
			bool hasBlendShapes = facts.hasBlendShapes;
			bool hasSkinnedMesh = facts.hasSkinnedMesh;
			bool hasClips = facts.hasClips;
			bool anyNormalMap = facts.anyNormalMap;
			bool hasImportedCamera = facts.hasImportedCamera;
			bool hasImportedLight = facts.hasImportedLight;
			bool hasVisibilityCurves = facts.hasVisibilityCurves;
			int hierarchyObjects = facts.hierarchyObjects;
			long runtimeSize = facts.runtimeSize;
			entry.RuntimeSize = runtimeSize > 0 ? runtimeSize : -1;
			entry.Set("vertices", vertices);
			entry.Set("triangles", triangles);
			entry.Set("meshCpuBytes", facts.meshCpuBytes);
			entry.Set("tangentBytes", facts.tangentBytes);
			entry.Set("secondaryUvBytes", facts.secondaryUvBytes);
			entry.Set("indexBytes32", facts.indexBytes32);
			entry.Set("indexBytes16", facts.indexBytes16);
			entry.Set("meshes", (long)meshCount);
			entry.Set("submeshes", (long)submeshes);
			entry.Set("readWrite", facts.isReadable ? "Yes" : "No");
			entry.Set("compression", facts.meshCompression.ToString());
			entry.Set("rig", facts.animationType.ToString());
			entry.Set("animationImport", facts.importAnimation ? "On" : "Off");
			entry.Set("blendShapesImport", facts.importBlendShapes ? "On" : "Off");
			entry.Set("optimize", facts.meshOptimizationFlags.ToString());
			entry.Set("indexFormat", facts.indexFormat.ToString());
			entry.Set("optimizeBones", facts.optimizeBones ? "On" : "Off");
			entry.Set("optimizeGameObjects", facts.optimizeGameObjects ? "On" : "Off");
			entry.Set("materials", facts.materialImportMode.ToString());
			entry.Set("skinWeights", facts.skinWeights.ToString());
			entry.Set("secondaryUV", facts.generateSecondaryUV ? "On" : "Off");
			entry.Set("maxBonesPerVertex", (long)facts.maxBonesPerVertex);
			entry.Set("minBoneWeight", (double)facts.minBoneWeight);
			entry.Set("cameras", hasImportedCamera ? "Present" : "None");
			entry.Set("lights", hasImportedLight ? "Present" : "None");
			entry.Set("visibilityCurves", hasVisibilityCurves ? "Present" : "None");
			entry.Set("hierarchyObjects", (long)hierarchyObjects);

			// MDL001 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL001", entry.Path))
				if (facts.isReadable && meshCount > 0)
				{
					bool canAutoFix = CanAutoFixUnreferencedModel(entry.Path, ctx);
					Action<FixContext> disableReadWrite = c => ApplyModelImporterChange(c, mi => mi.isReadable = false);
					Func<FixContext, bool> verifyReadWrite = c => VerifyModelImporterSetting(c, mi => !mi.isReadable, requireMeshes: true);
					var issue = entry.AddIssue("MDL001", "Read/Write enabled",
						canAutoFix ? "Disable Read/Write" : "Review Read/Write requirements before disabling",
						0, canAutoFix ? disableReadWrite : null,
						"Read/Write retains CPU-accessible mesh data. Automatic changes require a complete, current project-wide reference graph with no static referrers; assets with possible dynamic or serialized consumers remain a Review Fix.",
						verify: canAutoFix ? verifyReadWrite : null,
						options: canAutoFix ? null : new List<FixOption> { new FixOption {
							ActionId = "disable-read-write-review",
							Label = "Disable Read/Write after checking runtime consumers",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Check Mesh API reads, MeshCollider/particle usage, runtime batching, NavMesh baking and dynamic asset loading before applying.",
							Apply = disableReadWrite,
							Verify = verifyReadWrite
						} });
					if (issue != null && !canAutoFix)
					{
						issue.FixKind = FixKind.Review;
						issue.Risk = FixRisk.Medium;
						issue.Note("AutoFix withheld because the reference graph is unavailable/incomplete, the asset has static referrers, or its location permits dynamic loading.", "Safety gate");
					}
				}

			// MDL002 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL002", entry.Path))
				if (facts.meshCompression == ModelImporterMeshCompression.Off && vertices > 0)
					entry.AddIssue("MDL002", "Off", "Medium", 0,
						c => ApplyModelImporterChange(c, mi => mi.meshCompression = ModelImporterMeshCompression.Medium),
						"Medium Mesh Compression is the product default. Reimport verification confirms the importer state and a valid imported model; isolated visual-quality exceptions can be corrected manually.",
						verify: c => VerifyModelImporterSetting(c, mi => mi.meshCompression == ModelImporterMeshCompression.Medium, requireMeshes: true));

			// MDL003 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL003", entry.Path))
				if (meshCount > 0 && facts.meshOptimizationFlags != MeshOptimizationFlags.Everything)
					entry.AddIssue("MDL003", facts.meshOptimizationFlags.ToString(), "Everything", 0,
						c => ApplyModelImporterChange(c, mi => { mi.optimizeMeshVertices = true; mi.optimizeMeshPolygons = true; }),
						"Enable both vertex and polygon optimization. The postcondition checks both flags and confirms that the imported model remains loadable.",
						verify: c => VerifyModelImporterSetting(c, mi => mi.optimizeMeshVertices && mi.optimizeMeshPolygons, requireMeshes: true));

			// MDL004 ---------------------------------------------------------------
			bool rigPotentiallyUseful = hasSkinnedMesh || facts.avatarSetup != ModelImporterAvatarSetup.NoAvatar;
			using (RuleTiming.Begin("MDL004", entry.Path))
				if (!hasClips && facts.animationType != ModelImporterAnimationType.None && !rigPotentiallyUseful)
				{
					bool canAutoFix = CanAutoFixUnreferencedModel(entry.Path, ctx);
					Action<FixContext> disableRig = c => ApplyModelImporterChange(c, mi => { mi.animationType = ModelImporterAnimationType.None; mi.importAnimation = false; });
					Func<FixContext, bool> verifyRig = VerifyRigRemoved;
					var issue = entry.AddIssue("MDL004", facts.animationType.ToString(),
						canAutoFix ? "None" : "Review external rig/Avatar dependencies",
						0, canAutoFix ? disableRig : null,
						"This model has no local clips, skinned mesh, or configured Avatar. Automatic rig removal requires a complete current project-wide reference graph with no static referrers.",
						verify: canAutoFix ? verifyRig : null,
						options: canAutoFix ? null : new List<FixOption> { new FixOption {
							ActionId = "rig-none-review", Label = "Set Animation Type to None", Recommended = false,
							Risk = FixRisk.Medium, Warning = "Check external animation clips, Avatar consumers and serialized prefab/scene references before applying.",
							Apply = disableRig, Verify = verifyRig
						} });
					if (issue != null && !canAutoFix)
					{
						issue.FixKind = FixKind.Review;
						issue.Risk = FixRisk.Medium;
						issue.Note("Automatic rig removal was withheld because reference evidence cannot prove this model is not used as an external animation/Avatar source.", "Safety gate");
					}
				}

			// MDL005 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL005", entry.Path))
				if (facts.importAnimation && !hasClips)
				{
					bool canAutoFix = CanAutoFixUnreferencedModel(entry.Path, ctx);
					Action<FixContext> disableAnimationImport = c => ApplyModelImporterChange(c, mi => mi.importAnimation = false);
					Func<FixContext, bool> verifyAnimationImport = VerifyAnimationImportDisabled;
					var issue = entry.AddIssue("MDL005", "Import Animation on, no imported clips",
						canAutoFix ? "Off" : "Review model dependencies and Avatar usage",
						0, canAutoFix ? disableAnimationImport : null,
						"Import Animation is disabled automatically only when the current, complete project-wide reference graph shows no dependents and the asset is not in a dynamic-loading folder. Referenced or incompletely indexed assets retain a Review option.",
						verify: canAutoFix ? verifyAnimationImport : null,
						options: canAutoFix ? null : new List<FixOption> { new FixOption {
							ActionId = "disable-animation-import-review", Label = "Disable Import Animation", Recommended = false,
							Risk = FixRisk.Low, Warning = "Confirm the model is not an external animation source and that any generated Avatar is not required before applying.",
							Apply = disableAnimationImport, Verify = verifyAnimationImport
						} });
					if (issue != null && !canAutoFix)
					{
						issue.FixKind = FixKind.Review;
						issue.Risk = FixRisk.Low;
						issue.Note("Dependency/Avatar consumers or incomplete reference evidence prevent unconditional automation for this asset.", "Safety gate");
					}
				}

			// MDL006 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL006", entry.Path))
				if (meshCount > 0 && facts.importBlendShapes && !hasBlendShapes)
					entry.AddIssue("MDL006", "Import BlendShapes on, none present", "Off", 0,
						c => ApplyModelImporterChange(c, mi => mi.importBlendShapes = false),
						"Disable blend-shape import when the currently imported model contains no blend shape data. A future source revision can re-enable the option.",
						verify: VerifyBlendShapeImportDisabled);

			// MDL007 ---------------------------------------------------------------
			// The action only disables categories for which this source actually contains imported content.
			bool disableCameras = facts.importCameras && hasImportedCamera;
			bool disableLights = facts.importLights && hasImportedLight;
			bool disableVisibility = facts.importVisibility && hasVisibilityCurves;
			var importedContent = new List<string>();
			if (disableCameras) importedContent.Add("Cameras");
			if (disableLights) importedContent.Add("Lights");
			if (disableVisibility) importedContent.Add("Visibility animation");
			using (RuleTiming.Begin("MDL007", entry.Path))
				if (importedContent.Count > 0)
					entry.AddIssue("MDL007", string.Join(", ", importedContent), "Disable detected DCC content import", 0,
						c => ApplyModelImporterChange(c, mi => {
							if (disableCameras) mi.importCameras = false;
							if (disableLights) mi.importLights = false;
							if (disableVisibility) mi.importVisibility = false;
						}),
						"Only the camera, light, and visibility settings with detected imported content are disabled. Postcondition verification checks each targeted option and confirms the model remains loadable.",
						verify: c => VerifyModelImporterSetting(c, mi => (!disableCameras || !mi.importCameras) && (!disableLights || !mi.importLights) && (!disableVisibility || !mi.importVisibility)));

			// MDL008 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL008", entry.Path))
				if (facts.importTangents != ModelImporterTangents.None && !anyNormalMap && vertices > 0)
					entry.AddIssue("MDL008", facts.importTangents.ToString(), "None", 0,
						c => ApplyModelImporterChange(c, mi => mi.importTangents = ModelImporterTangents.None),
						"No normal-map usage was detected in the model evidence collected by this analyzer. Tangents are disabled for the common case; assets using runtime-assigned tangent-space shaders can be corrected manually.",
						verify: VerifyTangentsDisabled);

			// MDL009 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL009", entry.Path))
				if (vertices > settings.meshVertexWarn)
				{
					string severityText = vertices > settings.meshVertexCritical ? "critical budget" : "warning budget";
					entry.AddIssue("MDL009",
						DisplayUtility.FormatCount(vertices) + " verts",
						"under " + DisplayUtility.FormatCount(settings.meshVertexWarn) + " verts",
						0, null,
						"Triangle count: " + DisplayUtility.FormatCount(triangles) + "; this exceeds the configured "
						+ severityText + " geometry budget. The correct fix is content/platform-specific.");
				}

			// MDL010 ---------------------------------------------------------------
			// Auto is documented to choose the index width from per-mesh vertex count.
			using (RuleTiming.Begin("MDL010", entry.Path))
				if (facts.indexFormat == ModelImporterIndexFormat.UInt32
					&& largestMesh > 0
					&& largestMesh <= 65535)
				{
					entry.AddIssue("MDL010", "32 bit", "Auto", 0,
						c => ApplyModelImporterChange(c, mi => mi.indexFormat = ModelImporterIndexFormat.Auto),
						verify: c =>
						{
							var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
							if (mi == null || mi.indexFormat != ModelImporterIndexFormat.Auto) return false;
							var imported = AssetDatabase.LoadAllAssetsAtPath(c.AssetPath);
							int meshCount = 0;
							for (int i = 0; i < imported.Length; i++)
							{
								var mesh = imported[i] as Mesh;
								if (mesh == null) continue;
								meshCount++;
								if (mesh.vertexCount > 65535 || mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32) return false;
							}
							return meshCount > 0;
						});
				}

			// MDL011 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL011", entry.Path))
				if (submeshes > settings.meshSubmeshWarn)
				{
					entry.AddIssue("MDL011", submeshes + " sub-meshes",
						"at most " + settings.meshSubmeshWarn, 0, null,
						"High sub-mesh counts often correspond to material fragmentation, but merging changes materials, UVs or draw behavior and is therefore not automatic.");
				}

			// MDL012 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL012", entry.Path))
				if (!facts.weldVertices && meshCount > 0 && vertices > 0)
					entry.AddIssue("MDL012", "Weld Vertices off", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.weldVertices = true),
						"Enable Weld Vertices for non-empty imported meshes. The verifier checks the importer postcondition and the reimported model; unusual seam/topology cases may be adjusted manually.",
						verify: c => VerifyModelImporterSetting(c, mi => mi.weldVertices, requireMeshes: true));

			// MDL013 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL013", entry.Path))
				if (facts.materialImportMode != ModelImporterMaterialImportMode.None
					&& facts.materialLocation == ModelImporterMaterialLocation.InPrefab)
				{
					var embedded = AssetDatabase.LoadAllAssetsAtPath(entry.Path);
					var materials = new List<Material>();
					var names = new HashSet<string>(StringComparer.Ordinal);
					bool nameCollision = false;
					for (int i = 0; i < embedded.Length; i++)
					{
						var material = embedded[i] as Material;
						if (material == null) continue;
						materials.Add(material);
						if (!names.Add(material.name)) nameCollision = true;
					}

					Action<FixContext> extract = c => ImporterFixUtility.Project(c, () => ExtractAndRemapMaterials(c, materials));
					var issue = entry.AddIssue("MDL013", facts.materialImportMode.ToString(),
						"Project-managed materials", 0, nameCollision ? null : extract,
						nameCollision
							? "The model contains multiple embedded materials with the same name. Unity's SourceAssetIdentifier/material-name mapping is not sufficient to prove an unambiguous remap, so automatic extraction is disabled for this asset."
							: "Embedded materials can be extracted using explicit source-object remapping; the post-fix verifier checks every renderer/submesh slot against the exact extracted asset path.",
						verify: nameCollision ? null : VerifyExtractedAndRemappedMaterials);
					if (issue != null && nameCollision)
					{
						issue.FixKind = FixKind.Review;
						issue.Risk = FixRisk.Medium;
						issue.BackupRequired = true;
						issue.Options.Add(new FixOption
						{
							ActionId = "extract-remap-materials-review",
							Label = "Extract and inspect material mapping",
							Recommended = false,
							Risk = FixRisk.Medium,
							Warning = "Duplicate material names prevent deterministic automatic remapping. Inspect the imported model and verify every renderer/submesh mapping after extraction.",
							Apply = extract,
							Verify = VerifyExtractedAndRemappedMaterials
						});
				}

				}
			// MDL014 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL014", entry.Path))
				if (meshCount > 0 && facts.generateSecondaryUV)
				{
					bool canAutoFix = CanAutoFixUnreferencedModel(entry.Path, ctx);
					Action<FixContext> disableSecondaryUV = c => ApplyModelImporterChange(c, mi => mi.generateSecondaryUV = false);
					Func<FixContext, bool> verifySecondaryUV = c => VerifyModelImporterSetting(c, mi => !mi.generateSecondaryUV, requireMeshes: true);
					var issue = entry.AddIssue("MDL014", "Generate Lightmap UVs on",
						canAutoFix ? "Off" : "Review whether generated UV2 is required",
						facts.secondaryUvBytes,
						canAutoFix ? disableSecondaryUV : null,
						"Generated secondary UVs are disabled automatically only when a complete/current project-wide reference graph proves the model has no static consumers and it is outside known dynamic-loading paths. Referenced or incompletely indexed assets retain a Review option because their baked-lighting use cannot be proven from importer facts alone.",
						verify: canAutoFix ? verifySecondaryUV : null,
						options: canAutoFix ? null : new List<FixOption> { new FixOption {
							ActionId = "disable-secondary-uv-review", Label = "Disable generated secondary UVs after checking lightmapping use", Recommended = false,
							Risk = FixRisk.Medium, Warning = "Confirm no scene, prefab, build, Addressables or dynamic load requires this generated UV set before applying.",
							Apply = disableSecondaryUV, Verify = verifySecondaryUV
						} });
					if (issue != null && !canAutoFix)
					{
						issue.FixKind = FixKind.Review;
						issue.Risk = FixRisk.Medium;
						issue.BackupRequired = true;
						issue.Note("Static usage evidence cannot prove the model is outside every baked-lighting workflow; automatic disable was withheld.", "Safety gate");
					}
				}

			// MDL015 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL015", entry.Path))
				if (hasSkinnedMesh && hierarchyObjects > 1
					&& (facts.animationType == ModelImporterAnimationType.Generic || facts.animationType == ModelImporterAnimationType.Human)
					&& !facts.optimizeGameObjects)
					entry.AddIssue("MDL015", "Optimize Game Objects off", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.optimizeGameObjects = true),
						"Enable Optimize Game Objects for skinned models. The resulting importer flag is verified after reimport; if runtime code requires stripped transform paths, expose them or disable this optimization for that asset.",
						verify: c => VerifySkinnedModelImporterSetting(c, mi => mi.optimizeGameObjects));

			// MDL016 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL016", entry.Path))
				if (hasSkinnedMesh && facts.skinWeights == ModelImporterSkinWeights.Custom && facts.maxBonesPerVertex > 4)
					entry.AddIssue("MDL016", facts.maxBonesPerVertex + " bones per vertex", "4 bones per vertex", 0,
						c => ApplyModelImporterChange(c, mi => mi.maxBonesPerVertex = MaxBoneInfluenceTarget(mi.maxBonesPerVertex)),
						"Use four influences per vertex as the global default. Lower-weight influences may be discarded; the reimport verifier checks the importer target and a valid imported model.",
						verify: c => VerifySkinnedModelImporterSetting(c, mi => mi.maxBonesPerVertex <= 4));

			// MDL017 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL017", entry.Path))
				if (hasSkinnedMesh && hierarchyObjects > 1 && !facts.optimizeBones)
					entry.AddIssue("MDL017", "Optimize Bones off", "On", 0,
						c => ApplyModelImporterChange(c, mi => mi.optimizeBones = true),
						"Enable Optimize Bones on skinned models and verify the option after reimport. If a game depends on unweighted helper, IK, attachment, or script-driven bones, restore the setting for that model.",
						verify: c => VerifySkinnedModelImporterSetting(c, mi => mi.optimizeBones));

			// MDL018 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL018", entry.Path))
				if (hasSkinnedMesh && facts.skinWeights == ModelImporterSkinWeights.Custom && facts.minBoneWeight <= 0f)
				{
					entry.AddIssue("MDL018",
						"minBoneWeight " + facts.minBoneWeight.ToString("0.###"),
						"Review a positive threshold for suitable rigs",
						0, null,
						"Unity discards bone weights below minBoneWeight except when the weight is the largest for a vertex. "
						+ "Raising the threshold can reduce skinning data but can also change deformation quality.",
						options: new List<FixOption> { new FixOption {
							Label = "Enable Bone-Weight Pruning",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ApplyModelImporterChange(c, mi => mi.minBoneWeight = 0.01f),
							Verify = c => VerifySkinnedModelImporterSetting(c, mi => mi.minBoneWeight > 0f)
						} }
					);
				}


			entry.Finalise();
			return entry;
		}
		static void ExtractAndRemapMaterials(FixContext c, List<Material> embeddedMaterials)
		{
			c.EnsureWritable();
			c.RequireBackup(c.AssetPath);
			if (embeddedMaterials == null || embeddedMaterials.Count == 0) throw new InvalidOperationException("No embedded Material sub-assets were found to extract.");

			var model = AssetDatabase.LoadAssetAtPath<GameObject>(c.AssetPath);
			if (model == null) throw new InvalidOperationException("Model GameObject is unavailable for mapping capture.");
			var embeddedSet = new HashSet<Material>(embeddedMaterials);
			var expectation = new MaterialMappingExpectation();
			for (int i = 0; i < embeddedMaterials.Count; i++)
			{
				var material = embeddedMaterials[i];
				if (material == null) continue;
				string sourceId = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString();
				if (string.IsNullOrEmpty(sourceId)) throw new InvalidOperationException("Could not obtain a stable source identity for embedded material '" + material.name + "'.");
				expectation.SourceObjectToDestination[sourceId] = null;
			}
			var renderers = model.GetComponentsInChildren<Renderer>(true);
			for (int r = 0; r < renderers.Length; r++)
			{
				var shared = renderers[r].sharedMaterials;
				string rendererPath = GetRelativeHierarchyPath(model.transform, renderers[r].transform);
				for (int slot = 0; slot < shared.Length; slot++)
				{
					var material = shared[slot];
					if (material == null || !embeddedSet.Contains(material)) continue;
					string sourceId = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString();
					if (string.IsNullOrEmpty(sourceId)) throw new InvalidOperationException("Could not obtain a stable source identity for embedded material '" + material.name + "'.");
					expectation.SourceObjectToDestination[sourceId] = null;
					expectation.SlotToSource[rendererPath + "|" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)] = sourceId;
				}
			}
			if (expectation.SourceObjectToDestination.Count == 0) throw new InvalidOperationException("No renderer/material mappings could be captured before extraction.");

			string parent = AssetPathUtility.Directoryish(c.AssetPath);
			string folder = parent + "/Materials";
			if (!AssetDatabase.IsValidFolder(folder))
			{
				if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent)) throw new InvalidOperationException("The model's containing folder is not a valid Unity folder.");
				AssetDatabase.CreateFolder(parent, "Materials");
			}

			var identifiers = new List<AssetImporter.SourceAssetIdentifier>(embeddedMaterials.Count);
			var destinations = new List<string>(embeddedMaterials.Count);
			for (int i = 0; i < embeddedMaterials.Count; i++)
			{
				var material = embeddedMaterials[i];
				if (material == null) continue;
				string sourceId = GlobalObjectId.GetGlobalObjectIdSlow(material).ToString();
				if (string.IsNullOrEmpty(sourceId)) throw new InvalidOperationException("Could not obtain a stable source identity for embedded material '" + material.name + "'.");
				var identifier = new AssetImporter.SourceAssetIdentifier(material);
				identifiers.Add(identifier);
				string destination = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SanitizeMaterialAssetName(material.name) + ".mat");
				string error = AssetDatabase.ExtractAsset(material, destination);
				if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("Could not extract material '" + material.name + "': " + error);
				destinations.Add(destination);
				c.RegisterCreatedAsset(destination);
				if (AssetDatabase.LoadAssetAtPath<Material>(destination) == null) throw new InvalidOperationException("Extracted material asset did not load: " + destination);
				expectation.SourceObjectToDestination[sourceId] = destination;
				expectation.IdentifierToDestination[identifier] = destination;
			}

			var importer = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
			if (importer == null) throw new InvalidOperationException("ModelImporter unavailable after material extraction.");
			for (int i = 0; i < identifiers.Count; i++)
			{
				Material external = AssetDatabase.LoadAssetAtPath<Material>(destinations[i]);
				if (external == null) throw new InvalidOperationException("Extracted material is unavailable for remapping: " + destinations[i]);
				importer.AddRemap(identifiers[i], external);
			}
			importer.materialLocation = ModelImporterMaterialLocation.External;
			importer.materialSearch = ModelImporterMaterialSearch.Local;
			AssetDatabase.WriteImportSettingsIfDirty(c.AssetPath);
			AssetDatabase.ImportAsset(c.AssetPath, ImportAssetOptions.ForceUpdate);
			c.NeedsReimport = false;
			c.VerificationData = expectation;
			if (!VerifyMaterialMappingExact(c.AssetPath, expectation)) throw new InvalidOperationException("Post-extraction renderer/submesh mapping verification failed.");
		}

		static bool VerifyExtractedAndRemappedMaterials(FixContext c)
		{
			return c != null && VerifyMaterialMappingExact(c.AssetPath, c.VerificationData as MaterialMappingExpectation);
		}

		static bool VerifyMaterialMappingExact(string modelPath, MaterialMappingExpectation expectation)
		{
			if (string.IsNullOrEmpty(modelPath) || expectation == null || expectation.SourceObjectToDestination.Count == 0) return false;
			foreach (var pair in expectation.SourceObjectToDestination)
				if (string.IsNullOrEmpty(pair.Value) || AssetDatabase.LoadAssetAtPath<Material>(pair.Value) == null) return false;
			var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
			if (model == null) return false;
			var renderers = model.GetComponentsInChildren<Renderer>(true);
			var observed = new HashSet<string>(StringComparer.Ordinal);
			for (int r = 0; r < renderers.Length; r++)
			{
				var shared = renderers[r].sharedMaterials;
				string rendererPath = GetRelativeHierarchyPath(model.transform, renderers[r].transform);
				for (int slot = 0; slot < shared.Length; slot++)
				{
					string key = rendererPath + "|" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
					if (!expectation.SlotToSource.TryGetValue(key, out var sourceId)) continue;
					var material = shared[slot];
					if (material == null) return false;
					string actual = AssetDatabase.GetAssetPath(material);
					if (!string.Equals(actual, expectation.SourceObjectToDestination[sourceId], StringComparison.OrdinalIgnoreCase)) return false;
					observed.Add(key);
				}
			}
			foreach (var pair in expectation.SlotToSource) if (!observed.Contains(pair.Key)) return false;
			var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
			if (importer == null) return false;
			var map = importer.GetExternalObjectMap();
			if (map == null) return false;
			foreach (var pair in expectation.IdentifierToDestination)
			{
				if (!map.TryGetValue(pair.Key, out var mapped) || mapped == null) return false;
				string mappedPath = AssetDatabase.GetAssetPath(mapped);
				if (!string.Equals(mappedPath, pair.Value, StringComparison.OrdinalIgnoreCase)) return false;
			}
			return expectation.IdentifierToDestination.Count == expectation.SourceObjectToDestination.Count;
		}

		static string GetRelativeHierarchyPath(Transform root, Transform target)
		{
			if (root == null || target == null) return string.Empty;
			var stack = new Stack<string>();
			var current = target;
			while (current != null) { stack.Push(current.name); if (current == root) break; current = current.parent; }
			return string.Join("/", stack.ToArray());
		}

		static string SanitizeMaterialAssetName(string value)
		{
			if (string.IsNullOrEmpty(value)) return "Material";
			char[] invalid = System.IO.Path.GetInvalidFileNameChars();
			for (int i = 0; i < invalid.Length; i++) value = value.Replace(invalid[i].ToString(), "_");
			return value.Trim();
		}

		/// <summary>Applies one importer mutation only to the expected writable ModelImporter and schedules a reimport.</summary>
		static void ApplyModelImporterChange(FixContext context, Action<ModelImporter> mutation)
		{
			if (context == null) throw new ArgumentNullException(nameof(context));
			if (mutation == null) throw new ArgumentNullException(nameof(mutation));
			context.EnsureWritable();
			var importer = context.Importer as ModelImporter;
			if (importer == null) throw new InvalidOperationException("Expected a ModelImporter for " + context.AssetPath + ".");
			mutation(importer);
			context.NeedsReimport = true;
		}

		/// <summary>Verifies importer state after reimport and confirms the model's main object is still loadable.</summary>
		static bool VerifyModelImporterSetting(FixContext context, Func<ModelImporter, bool> predicate, bool requireMeshes = false)
		{
			if (context == null || predicate == null || string.IsNullOrEmpty(context.AssetPath)) return false;
			var importer = AssetImporter.GetAtPath(context.AssetPath) as ModelImporter;
			if (importer == null || !predicate(importer) || AssetDatabase.LoadAssetAtPath<GameObject>(context.AssetPath) == null) return false;
			if (!requireMeshes) return true;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++) if (imported[i] is Mesh) return true;
			return false;
		}

		static bool VerifyRigRemoved(FixContext context)
		{
			if (!VerifyModelImporterSetting(context, mi => mi.animationType == ModelImporterAnimationType.None && !mi.importAnimation)) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++)
			{
				var clip = imported[i] as AnimationClip;
				if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) return false;
				if (imported[i] is Avatar) return false;
			}
			return true;
		}

		static bool VerifyAnimationImportDisabled(FixContext context)
		{
			if (!VerifyModelImporterSetting(context, mi => !mi.importAnimation)) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++)
			{
				var clip = imported[i] as AnimationClip;
				if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) return false;
			}
			return true;
		}

		static bool VerifyBlendShapeImportDisabled(FixContext context)
		{
			if (!VerifyModelImporterSetting(context, mi => !mi.importBlendShapes, requireMeshes: true)) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++)
				if (imported[i] is Mesh mesh && mesh.blendShapeCount > 0) return false;
			return true;
		}

		static bool VerifyTangentsDisabled(FixContext context)
		{
			if (!VerifyModelImporterSetting(context, mi => mi.importTangents == ModelImporterTangents.None, requireMeshes: true)) return false;
			var imported = AssetDatabase.LoadAllAssetsAtPath(context.AssetPath);
			for (int i = 0; i < imported.Length; i++)
				if (imported[i] is Mesh mesh && mesh.tangents != null && mesh.tangents.Length > 0) return false;
			return true;
		}

		static bool VerifySkinnedModelImporterSetting(FixContext context, Func<ModelImporter, bool> predicate)
		{
			if (!VerifyModelImporterSetting(context, predicate, requireMeshes: true)) return false;
			var root = AssetDatabase.LoadAssetAtPath<GameObject>(context.AssetPath);
			var renderers = root != null ? root.GetComponentsInChildren<SkinnedMeshRenderer>(true) : Array.Empty<SkinnedMeshRenderer>();
			for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null && renderers[i].sharedMesh != null) return true;
			return false;
		}

		/// <summary>
		/// A missing/partial reference graph is not proof of zero consumers. Dynamic-loading folders are also
		/// excluded because their consumers need not appear in serialized dependency edges.
		/// </summary>
		static bool CanAutoFixUnreferencedModel(string assetPath, ScanContext context)
		{
			bool currentProjectGraph = context != null && context.References != null && context.References.IsCurrentProjectWide;
			bool dynamicLoadingPath = IsDynamicLoadingPath(assetPath);
			int referrerCount = currentProjectGraph && context.References.ReferrersOf(assetPath) != null
				? context.References.ReferrersOf(assetPath).Count : -1;
			return CanAutoFixUnreferencedModel(currentProjectGraph, referrerCount, dynamicLoadingPath);
		}

		internal static bool CanAutoFixUnreferencedModel(bool currentProjectGraph, int referrerCount, bool dynamicLoadingPath)
			=> currentProjectGraph && referrerCount == 0 && !dynamicLoadingPath;

		static bool IsDynamicLoadingPath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return true;
			return assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0
				|| assetPath.IndexOf("/StreamingAssets/", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		internal static int MaxBoneInfluenceTarget(int current) => Math.Min(current, 4);

	}
}
