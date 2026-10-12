using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Geometry budgets and ModelImporter settings for imported model assets.
	/// Findings are intentionally conservative: importer facts are measured directly, while
	/// runtime-usage-dependent changes remain Review/advisory instead of being auto-applied.
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
							fix: "Review Fix: disable Read/Write only after confirming this model is not read or written from runtime code or by a system that requires a readable Mesh.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL002", title: "Mesh compression disabled", category: AssetCategory.Model,
							why: "Mesh compression can reduce stored/build mesh data, but increasing compression increases vertex-data error. The acceptable level depends on the asset and visual tolerance.",
							fix: "Review Low or Medium Mesh Compression and compare the imported result before accepting the change.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL003", title: "Mesh GPU optimization disabled", category: AssetCategory.Model,
							why: "Unity can reorder mesh vertices and polygons to improve GPU cache utilization. This can change data ordering, so the project must not depend on strict source ordering.",
							fix: "Enable the available mesh optimization options after confirming no downstream workflow depends on vertex or polygon ordering.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL004", title: "Unnecessary rig on static model", category: AssetCategory.Model,
							why: "Animation Type other than None causes Unity to import animation-related data. A model with no local clips and no detected skinned mesh may still intentionally provide a rig or Avatar for animation clips stored elsewhere.",
							fix: "Review Fix: set Animation Type to None only after confirming the model is not used as a rig/Avatar source and no external animation targets its hierarchy.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL005", title: "Animation import enabled with no clips", category: AssetCategory.Model,
							why: "Import Animation is enabled even though this model currently contains no imported animation clips. Disabling it can reduce unnecessary animation import work, but the setting may be intentional for assets that are expected to gain clips or serve as animation sources.",
							fix: "Disable Import Animation after confirming this model is not intended to provide animation clips.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);

			Rules.Register(id: "MDL006", title: "Blend shape import enabled but no blend shapes are present", category: AssetCategory.Model,
							why: "Import BlendShapes is enabled even though this model currently contains no blend shape data. Disabling it can reduce unnecessary import handling, but the setting may be intentional for source assets that are expected to gain blend shapes later.",
							fix: "Disable Import BlendShapes after confirming this model is intentionally maintained without blend shape data.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Low,
							backupRequired: false
							);

			Rules.Register(id: "MDL007", title: "DCC cameras, lights or visibility imported", category: AssetCategory.Model,
							why: "ModelImporter can import camera, light and visibility data from the DCC file. These can be deliberate content, so the analyzer cannot safely decide that they are unused.",
							fix: "Disable only the camera, light or visibility imports that are confirmed unused by the project.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL008", title: "Tangents imported without detected normal-map usage", category: AssetCategory.Model,
							why: "Tangents are commonly required by tangent-space normal mapping, but runtime materials and shaders can differ from the materials imported from the model.",
							fix: "Review Fix: use Tangents None only after confirming all runtime materials and shaders using this mesh do not require tangent data.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
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
							fix: "Review Fix: enable Weld Vertices only after verifying topology, shading, UV seams and downstream geometry processing.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
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
							risk: FixRisk.Low,
							backupRequired: true
							);

			Rules.Register(id: "MDL014", title: "Secondary lightmap UV generation enabled", category: AssetCategory.Model,
							why: "Generate Secondary UV creates a second UV set for lightmapping. Disabling it is correct only when this asset does not participate in a workflow that needs the generated UV set.",
							fix: "Review Fix: disable Generate Lightmap UVs only when the asset's lighting workflow does not require the generated secondary UV set.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL015", title: "Optimize Game Objects disabled on a skinned model", category: AssetCategory.Model,
							why: "Optimize Game Objects removes Transform-only GameObjects from an imported character hierarchy unless they are exposed. This can improve animation performance, but runtime scripts, constraints, sockets and attachments may need specific transforms.",
							fix: "Review Fix: enable Optimize Game Objects after exposing every transform path required by runtime code, constraints and attachments.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL016", title: "High maximum bone influences per vertex", category: AssetCategory.Model,
							why: "Higher maximum bone influence counts increase skinning work. Unity notes that performance cost becomes more significant above four influences, while lowering the importer value discards the lowest-weight influences.",
							fix: "Review a limit of four or fewer where deformation quality permits. Do not blindly force two influences.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL017", title: "Unreferenced rig bones are imported", category: AssetCategory.Model,
							why: "Optimize Bones can import only bones connected to vertices, reducing unused rig data. Unweighted helper, IK, attachment or scripted bones may still be intentionally required.",
							fix: "Review Fix: enable Optimize Bones only after confirming unweighted helper, IK, attachment and scripted bones are not required at runtime.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "MDL018", title: "Bone-weight pruning is disabled", category: AssetCategory.Model,
							why: "ModelImporter.minBoneWeight controls which low-weight influences are discarded. Increasing it can reduce skinning data, but it changes deformation and is therefore content-dependent.",
							fix: "Review the rig and raise the minimum only when the discarded influences are genuinely insignificant.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
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
				if (facts.isReadable)
				{
					var options = new List<FixOption>
				{
					new FixOption
					{
						ActionId = "disable-read-write",
						Label = "Disable Read/Write",
						ResultDescription = "Removes the CPU-accessible mesh copy after reimport.",
						Recommended = true,
						Basis = SavingsBasis.Memory,
                        // Do not claim a fabricated exact saving. Unity guarantees a second CPU copy exists,
                        // but this analyzer does not have a stable per-mesh accounting contract for its exact
                        // retained memory footprint.
                        EstimatedSavings = 0,
						Warning = "Do not use when runtime code reads/writes mesh data, runtime static batching "
								  + "or runtime NavMesh baking needs it, or when the documented MeshCollider/"
								  + "particle-system readability cases apply.",
						Apply = c =>
						{
							var mi = c.Importer as ModelImporter;
							if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
							mi.isReadable = false;
							c.NeedsReimport = true;
						},
						Verify = c =>
						{
							var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
							return mi != null && !mi.isReadable;
						}
					}
				};

					entry.AddIssue("MDL001", "Read/Write enabled",
						"Review Fix: disable Read/Write Enabled", 0, null,
						"Unity keeps CPU-addressable mesh data when Read/Write is enabled. The precise memory saving is asset-dependent, so no numeric saving is claimed.",
						options: options);
				}

			// MDL002 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL002", entry.Path))
				if (facts.meshCompression == ModelImporterMeshCompression.Off && vertices > 0)
				{
					entry.AddIssue("MDL002", "Off", "Consider Low / Medium", 0, null,
						"Compression is a size-versus-precision tradeoff. The analyzer does not invent a saving without reimporting and measuring the resulting asset.",
						options: new List<FixOption> { new FixOption {
							Label = "Enable Mesh Compression",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Model(c, mi => mi.meshCompression = ModelImporterMeshCompression.Medium)
						} }
					);
				}

			// MDL003 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL003", entry.Path))
				if (facts.meshOptimizationFlags != MeshOptimizationFlags.Everything)
				{
					entry.AddIssue("MDL003", facts.meshOptimizationFlags.ToString(), "Everything", 0, null,
						"Unity documents vertex and polygon ordering optimization for GPU cache utilization. The analyzer cannot prove that strict ordering is irrelevant to downstream code or tools.",
						options: new List<FixOption> { new FixOption {
							Label = "Enable Mesh Optimization",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Model(c, mi => mi.optimizeMeshPolygons = true)
						} }
					);
				}

			// MDL004 ---------------------------------------------------------------
			bool rigPotentiallyUseful = hasSkinnedMesh
										|| facts.avatarSetup != ModelImporterAvatarSetup.NoAvatar;
			using (RuleTiming.Begin("MDL004", entry.Path))
				if (!hasClips && facts.animationType != ModelImporterAnimationType.None && !rigPotentiallyUseful)
				{
					entry.AddIssue("MDL004", facts.animationType.ToString(),
						"Review Fix: None", 0, null,
						"No clips or skinned mesh were found, and the importer is not configured to generate/copy an Avatar. "
						+ "This is still a Review action because external animation content can depend on an imported rig.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "rig-none",
							Label = "Set Animation Type to None",
							ResultDescription = "Stops generation of animation/rig data from this model.",
							Recommended = true,
							Warning = "Only use when this asset is not an external animation/Avatar source.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.animationType = ModelImporterAnimationType.None;
								mi.importAnimation = false;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && mi.animationType == ModelImporterAnimationType.None;
							}
						}
						});
				}

			// MDL005 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL005", entry.Path))
				if (facts.importAnimation && !hasClips)
				{
					entry.AddIssue("MDL005", "Import Animation on, no imported clips",
						"Off", 0, null,
						"No imported animation clips were found in this asset. Disabling animation import can reduce unnecessary import work, but confirm that this model is not intentionally retained as an animation source.",
						options: new List<FixOption>
						{
							new FixOption
							{
								ActionId = "disable-animation-import",
								Label = "Disable Import Animation",
								ResultDescription = "Stops animation extraction from this model on the next import.",
								Recommended = true,
								Warning = "Do not choose this when the source is expected to gain clips later or is intentionally retained as an animation source.",
								Apply = c =>
								{
									var mi = c.Importer as ModelImporter;
									if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
									mi.importAnimation = false;
									c.NeedsReimport = true;
								},
								Verify = c =>
								{
									var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
									return mi != null && !mi.importAnimation;
								}
							}
						});
				}

			// MDL006 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL006", entry.Path))
				if (facts.importBlendShapes && !hasBlendShapes)
				{
					entry.AddIssue("MDL006", "Import BlendShapes on, none present",
						"Off", 0, null,
						"No current imported mesh contains blend shapes. Disabling blend-shape import can reduce unnecessary import handling, but confirm that the source is intentionally maintained without blend shapes.",
						options: new List<FixOption>
						{
							new FixOption
							{
								ActionId = "disable-blend-shapes",
								Label = "Disable Import BlendShapes",
								ResultDescription = "Stops importing blend shape data from this source.",
								Recommended = true,
								Warning = "A future source revision containing blend shapes will not import them.",
								Apply = c =>
								{
									var mi = c.Importer as ModelImporter;
									if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
									mi.importBlendShapes = false;
									c.NeedsReimport = true;
								},
								Verify = c =>
								{
									var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
									return mi != null && !mi.importBlendShapes;
								}
							}
						});
				}

			// MDL007 ---------------------------------------------------------------
			// Importer checkboxes alone do not prove that the source contains the corresponding data.
			var importedContent = new List<string>();
			if (facts.importCameras && hasImportedCamera) importedContent.Add("Cameras");
			if (facts.importLights && hasImportedLight) importedContent.Add("Lights");
			if (facts.importVisibility && hasVisibilityCurves) importedContent.Add("Visibility animation");

			using (RuleTiming.Begin("MDL007", entry.Path))
				if (importedContent.Count > 0)
				{
					entry.AddIssue("MDL007", string.Join(", ", importedContent),
						"Review whether this imported DCC content is required", 0, null,
						"Import Cameras, Import Lights and Import Visibility have targetable runtime effects. "
						+ "Only disable them after confirming they are unused; visibility can be represented by Renderer.enabled animation curves.",
						options: new List<FixOption> { new FixOption {
							Label = "Disable DCC Content Import",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Model(c, mi => { mi.importCameras = false; mi.importLights = false; mi.importVisibility = false; })
						} }
					);
				}

			// MDL008 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL008", entry.Path))
				if (facts.importTangents != ModelImporterTangents.None && !anyNormalMap && vertices > 0)
				{
					entry.AddIssue("MDL008", facts.importTangents.ToString(),
						"Review Fix: None when safe", 0, null,
						"No common normal-map property with a texture was detected on imported/assigned materials. Runtime shader usage can still require tangent data.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "tangents-none",
							Label = "Set Tangents to None",
							ResultDescription = "Stops tangent data from being imported/generated for this model.",
							Recommended = true,
							Warning = "Any runtime tangent-space normal mapping or shader path requiring tangents will break or render incorrectly.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.importTangents = ModelImporterTangents.None;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && mi.importTangents == ModelImporterTangents.None;
							}
						}
						});
				}

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
						c =>
						{
							var mi = c.Importer as ModelImporter;
							if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
							mi.indexFormat = ModelImporterIndexFormat.Auto;
							c.NeedsReimport = true;
						},
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
			// Weld Vertices is not universally beneficial. It only has a possible benefit when
			// duplicate-position vertices exist, which this static importer inspection cannot prove.
			// Retain the rule as a review candidate for non-empty mesh assets, not as a defect.
			using (RuleTiming.Begin("MDL012", entry.Path))
				if (!facts.weldVertices && meshCount > 0 && vertices > 0)
				{
					entry.AddIssue("MDL012", "Weld Vertices off",
						"Review Fix: On when safe", 0, null,
						"Unity combines vertices sharing the same position. Intentional vertex splits can be required "
						+ "for normals, UVs, tangents or downstream processing, so this remains a review action.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "weld-vertices",
							Label = "Enable Weld Vertices",
							ResultDescription = "Allows Unity to merge vertices sharing the same position during import.",
							Recommended = true,
							Warning = "Do not use when intentional vertex splits are required for shading, UVs, tangents or downstream processing.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.weldVertices = true;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && mi.weldVertices;
							}
						}
						});
				}

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
				if (facts.generateSecondaryUV)
				{
					entry.AddIssue("MDL014", "Generate Lightmap UVs on",
						"Review Fix: Off when unused", facts.secondaryUvBytes, null,
						"Secondary UV generation is useful for lightmapping workflows. Disable it only when this asset does not need generated secondary UVs.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "disable-secondary-uv",
							Label = "Disable Generate Lightmap UVs",
							ResultDescription = "Stops generating the secondary lightmap UV set.",
							Recommended = true,
							Warning = "Lightmapping that expects this generated UV set may become invalid or lower quality.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.generateSecondaryUV = false;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && !mi.generateSecondaryUV;
							}
						}
						});
				}

			// MDL015 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL015", entry.Path))
				if (hasSkinnedMesh && hierarchyObjects > 1
					&& (facts.animationType == ModelImporterAnimationType.Generic
						|| facts.animationType == ModelImporterAnimationType.Human)
					&& !facts.optimizeGameObjects)
				{
					entry.AddIssue("MDL015", "Optimize Game Objects off",
						"Review Fix: On when safe", 0, null,
						"Unity can remove Transform-only GameObjects from optimized character hierarchies unless explicitly exposed. Review scripts, constraints, sockets and attachments before enabling it.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "optimize-game-objects",
							Label = "Enable Optimize Game Objects",
							ResultDescription = "Optimizes the imported animated hierarchy by removing non-required Transform-only objects.",
							Recommended = true,
							Warning = "Expose every transform required by runtime code, animation constraints, sockets, IK and attachments before enabling.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.optimizeGameObjects = true;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && mi.optimizeGameObjects;
							}
						}
						});
				}

			// MDL016 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL016", entry.Path))
				if (hasSkinnedMesh && facts.skinWeights == ModelImporterSkinWeights.Custom && facts.maxBonesPerVertex > 4)
				{
					string targetText = settings.IsMobile
						? "4 or fewer on mobile when deformation quality permits"
						: "4 or fewer when deformation quality permits";

					entry.AddIssue("MDL016",
						facts.maxBonesPerVertex + " bones per vertex",
						targetText, 0, null,
						"Lowering this importer limit discards the lowest-weight influences from the underlying mesh data. The correct threshold is a deformation-quality decision.",
						options: new List<FixOption> { new FixOption {
							Label = "Reduce Bone Influences",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => ImporterFixUtility.Model(c, mi => mi.maxBonesPerVertex = Mathf.Clamp(2, 1, 255))
						} }
					);
				}

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
							Apply = c => { c.EnsureWritable(); var mi = c.Importer as ModelImporter; if (mi == null) throw new InvalidOperationException("Not a ModelImporter"); mi.minBoneWeight = 0.01f; c.NeedsReimport = true; }
						} }
					);
				}

			// MDL017 ---------------------------------------------------------------
			using (RuleTiming.Begin("MDL017", entry.Path))
				if (hasSkinnedMesh && hierarchyObjects > 1 && !facts.optimizeBones)
				{
					entry.AddIssue("MDL017", "Optimize Bones off",
						"Review Fix: On when safe", 0, null,
						"Optimize Bones imports only bones connected to vertices. Unweighted helper, IK, attachment or scripted bones can still be required.",
						options: new List<FixOption>
						{
						new FixOption
						{
							ActionId = "optimize-bones",
							Label = "Enable Optimize Bones",
							ResultDescription = "Removes unweighted bones from the imported character hierarchy/data where Unity can determine they are not connected to vertices.",
							Recommended = true,
							Warning = "Do not use when unweighted helper, IK, attachment or scripted bones are accessed at runtime.",
							Apply = c =>
							{
								var mi = c.Importer as ModelImporter;
								if (mi == null) throw new InvalidOperationException("ModelImporter unavailable.");
								mi.optimizeBones = true;
								c.NeedsReimport = true;
							},
							Verify = c =>
							{
								var mi = AssetImporter.GetAtPath(c.AssetPath) as ModelImporter;
								return mi != null && mi.optimizeBones;
							}
						}
						});
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

	}
}
