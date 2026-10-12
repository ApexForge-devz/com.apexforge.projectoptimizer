using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Linq;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Structural and component-level analysis of prefabs and scenes.</summary>
	public sealed class PrefabSceneAnalyzer : IHybridBackgroundAssetAnalyzer
	{
		public AssetCategory Category => AssetCategory.Prefab;

		public AssetKindMask SupportedKinds => AssetKindMask.Prefab | AssetKindMask.Scene;

		sealed class SerializedMeshCacheEntry
		{
			public string Hash;
			public SerializedUnityHierarchy.MeshStats Stats;
		}

		static readonly Dictionary<string, SerializedMeshCacheEntry> s_SerializedMeshStats = new Dictionary<string, SerializedMeshCacheEntry>(StringComparer.Ordinal);

		/// <summary>
		/// Everything worth knowing about one prefab or scene, gathered in a single walk.
		/// Measuring this is by far the most expensive thing the tool does, so it is cached and
		/// keyed on the asset's dependency hash; the rules below are then evaluated over these
		/// numbers, which costs nothing and lets thresholds change without a re-measure.
		/// </summary>
		[Serializable]
		public sealed class HierarchyFacts
		{
			public int objectCount;
			public int maxDepth;
			public int rendererCount;
			public int missingScripts;
			public int shadowLights;
			public int stopNaNCameras;
			public int canvasCount;
			public int graphicRaycasterCount;
			public int layoutComponentCount;
			public int rigidbodyCount;
			public int jointCount;
			public int particleExpensiveModuleCount;
			public int measurementMask;

			/// <summary>Native fact groups that could not be obtained for the current dependency-hash snapshot.
			/// These are cached as known limitations so unchanged broken prefabs/scenes are not reopened on every scan.</summary>
			public int unavailableNativeCapabilities;
			public string nativeLimitation = string.Empty;

			public List<string> missingRefs = new List<string>();
			public List<string> missingPrefabs = new List<string>();
			public List<string> emptyObjects = new List<string>();
			public List<string> meshColliders = new List<string>();
			public List<string> materialMismatches = new List<string>();
			public List<string> raycastTargets = new List<string>();
			public List<string> alwaysAnimate = new List<string>();
			public List<string> unstaticGeometry = new List<string>();
			public List<string> denseNoLod = new List<string>();
			public List<string> continuousBodies = new List<string>();
			public List<string> rigidbodyPaths = new List<string>();
			public List<int> rigidbodySolverIterations = new List<int>();
			public List<string> continuousDynamicKinematicBodies = new List<string>();
			public List<string> jointPaths = new List<string>();
			public List<string> skinnedAlwaysUpdate = new List<string>();
			public List<string> expensiveParticleModules = new List<string>();

			// Kept raw so the particle budget stays a rule setting rather than a measurement.
			public List<string> particlePaths = new List<string>();
			public List<int> particleMax = new List<int>();

			/// <summary>
			/// Which materials this hierarchy draws with, and how many renderer slots each fills.
			/// <para>
			/// Recorded here rather than gathered separately because this walk already has every
			/// renderer in hand, and because it has to survive the measurement cache: a warm scan
			/// never reopens the prefab, so usage counted outside these facts would silently drop to
			/// zero on the second run and take the material analyzer's evidence with it.
			/// </para>
			/// </summary>
			public List<string> materialPaths = new List<string>();
			public List<int> materialRendererCounts = new List<int>();
		}

		static PrefabSceneAnalyzer()
		{
			Rules.Register(id: "PRE001", title: "Missing script components", category: AssetCategory.Prefab,
							why: "A component whose MonoBehaviour script is missing has no executable script type to load. Removing that dead component is safe only because the referenced script is already absent; restoring the script is preferable when the behaviour is still required.",
							fix: "Restore the missing script when it is still needed; otherwise remove the missing component. The removal is reversible through the framework backup.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "PRE002", title: "Missing asset references", category: AssetCategory.Prefab,
							why: "A serialized reference to a deleted asset stays in the file as a dangling id. At runtime it resolves to null and the feature that needed it fails.",
							fix: "Reassign the reference, or clear the field if it is genuinely unused.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE003", title: "Non-convex Mesh Collider", category: AssetCategory.Prefab,
							why: "A non-convex Mesh Collider is the right choice for static level geometry and the wrong one for anything that moves: it cannot be used by a non-kinematic Rigidbody, and it costs considerably more to cook and to query than a primitive. This rule lists them so the choice can be checked, not because a non-convex collider is a fault.",
							fix: "Where the object is static level geometry, this is usually correct and nothing needs doing. Where it moves, or where a primitive would fit the shape, replace it or enable Convex with a simplified collision mesh.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							// Whether a mesh collider is appropriate depends entirely on what the object does,
							// which is not something a hierarchy walk can see.

							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE004", title: "Renderer material count does not match sub-meshes", category: AssetCategory.Prefab,
							why: "A renderer's material array is normally expected to correspond to the mesh's sub-mesh material assignments. Extra materials can cause Unity to render the last sub-mesh repeatedly, while too few material slots can leave sub-mesh assignments incomplete or otherwise produce unintended rendering. This rule flags the mismatch for inspection rather than assuming a single runtime outcome for every renderer.",
							fix: "Match the material array to the mesh's sub-mesh count: trim the surplus entries, or add the missing ones.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE005", title: "Very large hierarchy", category: AssetCategory.Prefab,
							why: "Every GameObject costs memory, a Transform, and time in scene load, hierarchy updates and culling, whether or not it does anything.",
							fix: "Flatten the hierarchy, merge static geometry, or split the prefab and load parts on demand.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE006", title: "Deeply nested hierarchy", category: AssetCategory.Prefab,
							why: "Deep transform chains make every position change propagate through many levels, and the cost is paid again on each dirty transform.",
							fix: "Flatten intermediate parent objects that exist only for organisation.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE007", title: "Empty GameObjects", category: AssetCategory.Prefab,
							why: "Objects with no components and no children still cost a Transform, a slot in the hierarchy and scene-load time.",
							fix: "Delete the ones that are genuinely spare. This is not auto-fixed: an empty object is just as often a deliberate marker - a spawn point, muzzle or attach anchor - that scripts look up by name or hold a reference to.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE008", title: "Raycast Target enabled on apparently non-interactive UI", category: AssetCategory.Prefab,
							why: "A Graphic with Raycast Target enabled participates in UI raycasts. Decorative graphics can add unnecessary raycast work, but custom input modules can also use raycast results without a standard EventSystem handler on the object, so static inspection cannot prove that a target is unused.",
							fix: "Review Fix: disable Raycast Target only on graphics that are confirmed not to participate in pointer, custom raycast, drag, selection, or accessibility interaction.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Medium,
							backupRequired: false
							);

			Rules.Register(id: "PRE009", title: "Particle System with a very high particle budget", category: AssetCategory.Prefab,
							why: "Max Particles reserves buffers up front and caps how much simulation and overdraw the system can generate. Large budgets are usually left over from tuning.",
							fix: "Lower Max Particles to what the effect actually emits.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE010", title: "Animator always animates while off-screen", category: AssetCategory.Prefab,
							why: "Always Animate evaluates the Animator even when its renderers are not visible. It can be intentional for gameplay-driven animation, but cosmetic animators can often use culling to avoid unnecessary work.",
							fix: "Review Fix: choose Cull Update Transforms or Cull Completely only after confirming what the Animator drives while off-screen. In Unity 6, Cull Update Transforms still evaluates the state machine and root-motion callbacks but stops retargeting, IK and Transform writes when the renderers are invisible; Cull Completely disables animation evaluation while invisible.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE011", title: "static geometry not marked static", category: AssetCategory.Scene,
							why: "Renderers that never move can be combined by static batching, contribute to occlusion culling and receive baked lighting. Without the static flags none of that applies.",
							fix: "Set the appropriate static flags on non-moving scene geometry.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE012", title: "Many realtime shadow-casting lights", category: AssetCategory.Scene,
							why: "Each realtime shadow-casting light renders the scene again into a shadow map. A handful of them can cost more than everything else in the frame.",
							fix: "Bake what you can, disable shadows on fill lights, and keep realtime shadows to the key light.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE013", title: "Missing prefab instances", category: AssetCategory.Prefab,
							why: "An instance whose prefab asset has been deleted cannot be restored by Unity. It loads as an empty "
							+ "placeholder, logs an error every time the scene or prefab opens, and silently drops whatever it "
							+ "was meant to spawn.",
							fix: "Restore the missing prefab, or delete the broken instances. This is the usual cause of a wall of "
							+ "\"Missing Prefab Asset\" errors in the console.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE014", title: "Dense mesh with no LOD Group", category: AssetCategory.Prefab,
							why: "Without an LOD Group a mesh renders at full density however small it is on screen, so a distant object costs exactly what a near one does.",
							fix: "Add an LOD Group with two or three reduced meshes, or generate Mesh LODs on the model importer.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PRE015", title: "Continuous collision detection on a Rigidbody", category: AssetCategory.Prefab,
							why: "Continuous collision detection trades physics performance for protection against fast-moving objects tunnelling through colliders. Unity documents Discrete as the default, Continuous for static-mesh CCD, Continuous Dynamic for static and continuously detected dynamic bodies, and Continuous Speculative as the generally cheaper CCD option that also works on kinematic bodies.",
							fix: "Review Fix: choose Discrete, Continuous, Continuous Dynamic or Continuous Speculative for the affected bodies. The correct mode depends on actual motion, collider types and which other bodies they must reliably collide with; changing it blindly can reintroduce tunnelling or alter collision behaviour.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);
			Rules.Register(id: "PRE016", title: "Renderer material mapping requires review", category: AssetCategory.Prefab,
							why: "A renderer's material-slot count does not match the mesh sub-mesh count, but static inspection cannot safely infer the intended mapping for missing or reordered material slots.",
							fix: "Review the renderer's material-slot and sub-mesh mapping and correct only the assignments that are intentionally missing or reordered.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "PAS1012", title: "URP camera has Stop NaNs enabled", category: AssetCategory.Prefab,
							why: "URP Stop NaNs replaces NaN/Inf output with black pixels. It can prevent some visual failures, but Unity documents it as an expensive safety feature intended for cases where NaN problems cannot otherwise be fixed. It is only effective on URP Base cameras.",
							fix: "Review Fix: disable Stop NaNs on production Base cameras after confirming the project does not rely on it for visual safety or debugging.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: false
							);
			Rules.Register(id: "UIT001", title: "Many UI Canvases", category: AssetCategory.Prefab,
				why: "Each Canvas is a batching/rebuild boundary. Multiple canvases are sometimes intentional, but a high count in one hierarchy is worth reviewing because independent rebuild regions and rendering work can accumulate.",
				fix: "Keep canvases that isolate independently changing UI; merge static or rarely changing UI where profiling shows that a smaller canvas structure is beneficial.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "UIT002", title: "Many Graphic Raycasters", category: AssetCategory.Prefab,
				why: "Every active GraphicRaycaster can participate in UI pointer hit testing. A high count can increase query work, particularly in screen-space UI with many canvases.",
				fix: "Remove raycasters from canvases that never receive pointer input, and keep input handling consolidated where the UI architecture permits.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "UIT003", title: "Many UI layout components", category: AssetCategory.Prefab,
				why: "LayoutGroup and ContentSizeFitter components can trigger layout calculation and rebuild work when hierarchy values change. Large nested layout graphs can become disproportionately expensive.",
				fix: "Use layout components where dynamic sizing is required, but prefer simpler anchors or explicit sizing for stable UI and avoid unnecessary nested layout rebuild chains.",
				severity: IssueSeverity.Warning, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "PHY001", title: "Many 3D Rigidbodies", category: AssetCategory.Prefab,
				why: "A Rigidbody participates in physics integration, broad-phase maintenance and contact processing. High counts are not inherently wrong, but they are a strong prompt to profile sleeping, collision layers and object lifetime.",
				fix: "Use Rigidbody only for physics-driven bodies, allow sleeping where appropriate, and review pooling and collision-layer design for large populations.",
				severity: IssueSeverity.Warning, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "PHY002", title: "Rigidbody uses high solver iterations", category: AssetCategory.Prefab,
				why: "Higher per-body solver iteration counts increase physics solver work. Extra iterations may be necessary for stability, so the optimizer treats this as a policy-driven review rather than a universally bad value.",
				fix: "Reduce per-body solver iterations only after checking joint/contact stability and comparing profiler results.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Medium, backupRequired: false);

			Rules.Register(id: "PHY003", title: "Kinematic Rigidbody uses Continuous Dynamic CCD", category: AssetCategory.Prefab,
				why: "Continuous Dynamic is intended for continuously detected dynamic interactions. On a kinematic Rigidbody it can be an unnecessarily strong setting, while other CCD modes are available for different gameplay requirements.",
				fix: "Review the body's collision requirements and prefer the least expensive CCD mode that still prevents the required tunnelling cases.",
				severity: IssueSeverity.Info, fixKind: FixKind.Review, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Medium, backupRequired: false);

			Rules.Register(id: "PHY004", title: "Many joints in hierarchy", category: AssetCategory.Prefab,
				why: "Joint constraints add solver work and can amplify physics cost when many interconnected bodies are active. The correct amount depends on gameplay topology.",
				fix: "Profile the joint graph and simplify, sleep or disable constraints that do not need to remain active.",
				severity: IssueSeverity.Info, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);

			Rules.Register(id: "RND001", title: "SkinnedMeshRenderer updates while off-screen", category: AssetCategory.Prefab,
				why: "updateWhenOffscreen forces a skinned mesh to continue skinning when its renderer is not visible. This may be required for gameplay or off-screen deformation, but it can waste CPU skinning work for purely visual characters.",
				fix: "Disable Update When Offscreen for cosmetic skinning once gameplay has been verified not to depend on off-screen deformation or bounds updates.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence,
				risk: FixRisk.Medium, backupRequired: false);

			Rules.Register(id: "VFX001", title: "Expensive particle modules enabled", category: AssetCategory.Prefab,
				why: "Particle Collision, Lights and Trails can add simulation, physics-query, rendering or geometry work. Their cost depends on particle count and effect design, so the finding identifies enabled modules rather than assigning a fabricated frame-time cost.",
				fix: "Disable modules that are not visually necessary, reduce their scope or particle count, and validate the effect with the Profiler before/after.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Medium, backupRequired: false);

		}

		public void BeginScan(ScanContext ctx)
		{
			s_SerializedMeshStats.Clear();
			if (ctx.MaterialUsage != null) return;

			ctx.MaterialUsage = new MaterialUsageIndex
			{
				// Anything short of every prefab and scene in the project means a material's count
				// is a floor rather than a total, and whatever reads it has to know that.
				Partial = !ctx.Settings.scanPrefabs
						  || !ctx.Settings.scanScenes
						  || ctx.Settings.scopeMode != ScanScopeMode.EntireProject
						  || !ctx.Settings.IsKindEnabled(AssetKind.Prefab)
						  || !ctx.Settings.IsKindEnabled(AssetKind.Scene)
			};
		}

		public bool Handles(string assetPath)
		{
			return assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
				   || assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
		}

		public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
		{
			if (mainContext == null || backgroundContext == null || string.IsNullOrEmpty(assetPath)) return;
			string hash = mainContext.GetDependencyHash(assetPath);
			if (string.IsNullOrEmpty(hash) || mainContext.Cache == null) return;
			bool isScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
			var needs = HierarchyMeasurementNeeds.Create(mainContext.Settings, isScene);
			string key = ScanCache.ScopedKey(assetPath, typeof(HierarchyFacts).FullName);
			if (mainContext.Cache.TryGet(key, hash, out HierarchyFacts cached) && cached != null && cached.measurementMask == needs.Mask)
				backgroundContext.SeedCached(key, cached);
		}

		public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
		{
			if (context == null || !Handles(assetPath)) return null;
			bool isScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
			var needs = HierarchyMeasurementNeeds.Create(context.Settings, isScene);
			string key = ScanCache.ScopedKey(assetPath, typeof(HierarchyFacts).FullName);
			HierarchyFacts cached;
			SerializedUnityHierarchy.ParseResult parsed = null;
			if (context.TryGetFact(key, out cached) && cached != null && cached.measurementMask == needs.Mask)
			{
				parsed = new SerializedUnityHierarchy.ParseResult
				{
					Facts = cached,
					NativeCapabilities = (SerializedUnityHierarchy.NativeCapability)cached.unavailableNativeCapabilities,
					Limitation = cached.nativeLimitation,
					ObjectCount = cached.objectCount
				};
			}
			else
			{
				parsed = SerializedUnityHierarchy.TryExtract(context.ProjectRoot, assetPath, isScene, context.GetPathForGuid, null);
			}

			var entry = NewEntry(assetPath, isScene ? AssetCategory.Scene : AssetCategory.Prefab, isScene ? "Scene" : "Prefab", context);
			entry.BackgroundPayload = parsed;
			entry.Set("objects", (long)(parsed != null && parsed.Facts != null ? parsed.Facts.objectCount : 0));
			return entry;
		}

		public AssetEntry CompleteBackground(AssetEntry entry, ScanContext mainContext)
		{
			if (entry == null || mainContext == null) return entry;
			bool isScene = entry.Path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
			var needs = HierarchyMeasurementNeeds.Create(mainContext.Settings, isScene);
			var parsed = entry.BackgroundPayload as SerializedUnityHierarchy.ParseResult;
			HierarchyFacts facts = parsed != null && parsed.Facts != null
				? CompleteSerializedFacts(entry.Path, needs, parsed, isScene)
				: null;

			if (facts == null) facts = isScene ? MeasureScene(entry.Path, needs) : MeasurePrefab(entry.Path, needs);
			if (facts == null) return entry;

			string hash = mainContext.GetDependencyHash(entry.Path);
			facts.measurementMask = needs.Mask;
			if (!string.IsNullOrEmpty(facts.nativeLimitation)) entry.Set("analysisLimitation", facts.nativeLimitation);
			if (mainContext.Cache != null && !string.IsNullOrEmpty(hash))
				mainContext.Cache.Set(ScanCache.ScopedKey(entry.Path, typeof(HierarchyFacts).FullName), facts, hash);

			mainContext.MaterialUsage?.Record(entry.Path, isScene, facts.materialPaths, facts.materialRendererCounts);
			Evaluate(facts, entry, mainContext.Settings, true, isScene);
			entry.Finalise();
			entry.BackgroundPayload = null;
			return entry;
		}

		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			bool isScene = assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
			if (isScene && !ctx.Settings.scanScenes) return null;
			if (!isScene && !ctx.Settings.scanPrefabs) return null;

			return isScene ? AnalyzeScene(assetPath, ctx) : AnalyzePrefab(assetPath, ctx);
		}

		AssetEntry AnalyzePrefab(string assetPath, ScanContext ctx)
		{
			var facts = GetHierarchyFacts(assetPath, ctx, false);
			if (facts == null) return null;

			ctx.MaterialUsage?.Record(assetPath, false, facts.materialPaths, facts.materialRendererCounts);

			var entry = NewEntry(assetPath, AssetCategory.Prefab, "Prefab", ctx);
			Evaluate(facts, entry, ctx.Settings, true, false);
			entry.Finalise();
			return entry;
		}

		static HierarchyFacts GetHierarchyFacts(string assetPath, ScanContext ctx, bool isScene)
		{
			var needs = HierarchyMeasurementNeeds.Create(ctx.Settings, isScene);
			int mask = needs.Mask;
			if (ctx.Cache == null) return isScene ? MeasureScene(assetPath, needs) : MeasurePrefab(assetPath, needs);

			string hash = ctx.GetDependencyHash(assetPath);
			string key = ScanCache.ScopedKey(assetPath, typeof(HierarchyFacts).FullName);
			if (ctx.Cache.TryGet(key, hash, out HierarchyFacts cached) && cached.measurementMask == mask)
				return cached;

			// Text-serialized prefabs/scenes contain the hierarchy/component representation we need for
			// most structural rules. Parse that source directly rather than constructing thousands of
			// UnityEngine.Object instances. Any fact that cannot be proven is rejected conservatively and
			// falls through to the existing Unity-object implementation.
			double serializedStart = ScanTiming.Now;
			var serialized = SerializedUnityHierarchy.TryExtract(
				System.IO.Path.GetDirectoryName(Application.dataPath) ?? string.Empty,
				assetPath,
				isScene,
				ctx.AssetCatalog != null ? new Func<string, string>(ctx.AssetCatalog.PathForGuid) : null,
				null);
			ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/SerializedYAMLParse", ScanTiming.Now - serializedStart);
			if (serialized != null && serialized.Facts != null)
			{
				double nativeStart = ScanTiming.Now;
				var completed = CompleteSerializedFacts(assetPath, needs, serialized, isScene);
				ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/TargetedNativeExtraction", ScanTiming.Now - nativeStart);
				if (completed != null)
				{
					completed.measurementMask = mask;
					if (!string.IsNullOrEmpty(completed.nativeLimitation))
						completed.nativeLimitation = completed.nativeLimitation.Trim();
					ctx.Cache.Set(key, completed, hash);
					return completed;
				}
			}

			var measured = isScene ? MeasureScene(assetPath, needs) : MeasurePrefab(assetPath, needs);
			if (measured != null)
			{
				measured.measurementMask = mask;
				ctx.Cache.Set(key, measured, hash);
			}
			return measured;
		}

		readonly struct HierarchyMeasurementNeeds
		{
			public readonly bool MissingScripts, MissingRefs, MissingPrefabs, MeshColliders, MaterialMismatch, Depth, Empty, Raycast, Particles, AlwaysAnimate, Static, Lights, Dense, Continuous, StopNaN;
			public readonly bool Canvases, Raycasters, Layout, Rigidbodies, SolverIterations, KinematicCcd, Joints, Skinned, ExpensiveParticleModules;
			public int Mask { get; }

			HierarchyMeasurementNeeds(OptimizerSettings settings, bool isScene)
			{
				MissingScripts = settings == null || settings.IsRuleEnabled("PRE001");
				MissingRefs = settings == null || settings.IsRuleEnabled("PRE002");
				MeshColliders = settings == null || settings.IsRuleEnabled("PRE003");
				MaterialMismatch = settings == null || settings.IsRuleEnabled("PRE004");
				Depth = settings == null || settings.IsRuleEnabled("PRE006");
				Empty = settings == null || settings.IsRuleEnabled("PRE007");
				Raycast = settings == null || settings.IsRuleEnabled("PRE008");
				Particles = settings == null || settings.IsRuleEnabled("PRE009");
				AlwaysAnimate = settings == null || settings.IsRuleEnabled("PRE010");
				Static = isScene && (settings == null || settings.IsRuleEnabled("PRE011"));
				Lights = isScene && (settings == null || settings.IsRuleEnabled("PRE012"));
				MissingPrefabs = settings == null || settings.IsRuleEnabled("PRE013");
				Dense = settings == null || settings.IsRuleEnabled("PRE014");
				Continuous = settings == null || settings.IsRuleEnabled("PRE015");
				StopNaN = settings == null || settings.IsRuleEnabled("PAS1012");
				Canvases = settings == null || settings.IsRuleEnabled("UIT001");
				Raycasters = settings == null || settings.IsRuleEnabled("UIT002");
				Layout = settings == null || settings.IsRuleEnabled("UIT003");
				Rigidbodies = settings == null || settings.IsRuleEnabled("PHY001") || settings.IsRuleEnabled("PHY002") || settings.IsRuleEnabled("PHY003");
				SolverIterations = settings == null || settings.IsRuleEnabled("PHY002");
				KinematicCcd = settings == null || settings.IsRuleEnabled("PHY003");
				Joints = settings == null || settings.IsRuleEnabled("PHY004");
				Skinned = settings == null || settings.IsRuleEnabled("RND001");
				ExpensiveParticleModules = settings == null || settings.IsRuleEnabled("VFX001");
				int mask = 0;
				if (MissingScripts) mask |= 1 << 0; if (MissingRefs) mask |= 1 << 1; if (MeshColliders) mask |= 1 << 2; if (MaterialMismatch) mask |= 1 << 3;
				if (Depth) mask |= 1 << 4; if (Empty) mask |= 1 << 5; if (Raycast) mask |= 1 << 6; if (Particles) mask |= 1 << 7;
				if (AlwaysAnimate) mask |= 1 << 8; if (Static) mask |= 1 << 9; if (Lights) mask |= 1 << 10; if (MissingPrefabs) mask |= 1 << 11;
				if (Dense) mask |= 1 << 12; if (Continuous) mask |= 1 << 13; if (StopNaN) mask |= 1 << 14;
				if (Canvases) mask |= 1 << 15; if (Raycasters) mask |= 1 << 16; if (Layout) mask |= 1 << 17; if (Rigidbodies) mask |= 1 << 18;
				if (SolverIterations) mask |= 1 << 19; if (KinematicCcd) mask |= 1 << 20; if (Joints) mask |= 1 << 21; if (Skinned) mask |= 1 << 22; if (ExpensiveParticleModules) mask |= 1 << 23;
				Mask = mask;
			}

			public static HierarchyMeasurementNeeds Create(OptimizerSettings settings, bool isScene) => new HierarchyMeasurementNeeds(settings, isScene);
		}

		static HierarchyFacts CompleteSerializedFacts(string assetPath, HierarchyMeasurementNeeds needs, SerializedUnityHierarchy.ParseResult parsed, bool isScene)
		{
			if (parsed == null || parsed.Facts == null) return null;
			var native = parsed.NativeCapabilities;
			var knownUnavailable = (SerializedUnityHierarchy.NativeCapability)parsed.Facts.unavailableNativeCapabilities;
			native &= ~knownUnavailable;
			if (native == SerializedUnityHierarchy.NativeCapability.None)
				return parsed.Facts;

			bool needMesh = (native & SerializedUnityHierarchy.NativeCapability.MeshStats) != 0
				&& (needs.MaterialMismatch || needs.Dense);
			bool needLights = (native & SerializedUnityHierarchy.NativeCapability.LightSemantics) != 0 && needs.Lights;
			bool needColliders = (native & SerializedUnityHierarchy.NativeCapability.MeshColliderState) != 0 && needs.MeshColliders;
			bool needParticles = (native & SerializedUnityHierarchy.NativeCapability.ParticleState) != 0 && (needs.Particles || needs.ExpensiveParticleModules);
			bool needBodies = (native & SerializedUnityHierarchy.NativeCapability.RigidbodyState) != 0
				&& (needs.Rigidbodies || needs.SolverIterations || needs.KinematicCcd || needs.Continuous);
			bool needUi = (native & SerializedUnityHierarchy.NativeCapability.UiInputSemantics) != 0 && needs.Raycast;
			bool needStopNaN = (native & SerializedUnityHierarchy.NativeCapability.StopNaNCameraState) != 0 && needs.StopNaN;
			var required = SerializedUnityHierarchy.NativeCapability.None;
			if (needMesh) required |= SerializedUnityHierarchy.NativeCapability.MeshStats;
			if (needLights) required |= SerializedUnityHierarchy.NativeCapability.LightSemantics;
			if (needColliders) required |= SerializedUnityHierarchy.NativeCapability.MeshColliderState;
			if (needParticles) required |= SerializedUnityHierarchy.NativeCapability.ParticleState;
			if (needBodies) required |= SerializedUnityHierarchy.NativeCapability.RigidbodyState;
			if (needUi) required |= SerializedUnityHierarchy.NativeCapability.UiInputSemantics;
			if (needStopNaN) required |= SerializedUnityHierarchy.NativeCapability.StopNaNCameraState;
			if (required == SerializedUnityHierarchy.NativeCapability.None) return parsed.Facts;

			try
			{
				var completed = WithHierarchyRoots(assetPath, isScene, roots =>
				{
				var facts = parsed.Facts;
				var renderers = needMesh ? GetComponentsInChildren<Renderer>(roots) : null;
				if (needMesh && renderers != null)
				{
					for (int i = 0; i < renderers.Length; i++)
					{
						var renderer = renderers[i];
						if (renderer == null) continue;
						Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
						if (mesh == null) continue;
						if (needs.Dense && mesh.vertexCount > 5000 && renderer.GetComponentInParent<LODGroup>() == null)
							facts.denseNoLod.Add(Path(renderer.transform) + " (" + mesh.vertexCount + " verts)");
						if (needs.MaterialMismatch && mesh.subMeshCount > 0)
						{
							var materials = renderer.sharedMaterials;
							int slots = materials != null ? materials.Length : 0;
							if (slots != mesh.subMeshCount)
								facts.materialMismatches.Add(Path(renderer.transform) + " (" + slots + " material" + (slots == 1 ? "" : "s") + ", " + mesh.subMeshCount + " sub-mesh" + (mesh.subMeshCount == 1 ? "" : "es") + " - " +
										(slots > mesh.subMeshCount ? (slots - mesh.subMeshCount) + " surplus, redrawing the last sub-mesh" : (mesh.subMeshCount - slots) + " sub-mesh(es) not drawn") + ")");
						}
					}
				}

				if (needLights)
				{
					var lights = GetComponentsInChildren<Light>(roots);
					int shadows = 0;
					for (int i = 0; i < lights.Length; i++)
					{
						var light = lights[i];
						if (light != null && light.enabled && light.shadows != LightShadows.None && light.lightmapBakeType != LightmapBakeType.Baked) shadows++;
					}
					facts.shadowLights = shadows;
				}

				if (needColliders)
				{
					var colliders = GetComponentsInChildren<MeshCollider>(roots);
					for (int i = 0; i < colliders.Length; i++)
						if (colliders[i] != null && !colliders[i].convex) facts.meshColliders.Add(Path(colliders[i].transform));
				}

				if (needParticles)
				{
					var systems = GetComponentsInChildren<ParticleSystem>(roots);
					facts.particlePaths.Clear(); facts.particleMax.Clear(); facts.expensiveParticleModules.Clear(); facts.particleExpensiveModuleCount = 0;
					for (int i = 0; i < systems.Length; i++)
					{
						var ps = systems[i]; if (ps == null) continue;
						facts.particlePaths.Add(Path(ps.transform)); facts.particleMax.Add(ps.main.maxParticles);
						if (needs.ExpensiveParticleModules)
						{
							var collision = ps.collision; if (collision.enabled) { facts.particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(Path(ps.transform) + " (Collision)"); }
							var lights = ps.lights; if (lights.enabled) { facts.particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(Path(ps.transform) + " (Lights)"); }
							var trails = ps.trails; if (trails.enabled) { facts.particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(Path(ps.transform) + " (Trails)"); }
						}
					}
				}

				if (needBodies)
				{
					var bodies = GetComponentsInChildren<Rigidbody>(roots);
					facts.rigidbodyPaths.Clear(); facts.rigidbodySolverIterations.Clear(); facts.continuousBodies.Clear(); facts.continuousDynamicKinematicBodies.Clear(); facts.rigidbodyCount = 0;
					for (int i = 0; i < bodies.Length; i++)
					{
						var body = bodies[i]; if (body == null) continue;
						string bodyPath = Path(body.transform);
						facts.rigidbodyCount++; facts.rigidbodyPaths.Add(bodyPath); facts.rigidbodySolverIterations.Add(body.solverIterations);
						if (body.collisionDetectionMode != CollisionDetectionMode.Discrete) facts.continuousBodies.Add(bodyPath + " (" + body.collisionDetectionMode + ")");
						if (body.isKinematic && body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic) facts.continuousDynamicKinematicBodies.Add(bodyPath);
					}
				}

				if (needUi)
				{
					facts.raycastTargets.Clear();
					var graphics = GetComponentsInChildren<Graphic>(roots);
					for (int i = 0; i < graphics.Length; i++)
					{
						var graphic = graphics[i]; if (graphic == null || !graphic.raycastTarget) continue;
						if (!HandlesPointerInput(graphic.gameObject)) facts.raycastTargets.Add(Path(graphic.transform));
					}
				}

				if (needStopNaN)
				{
					facts.stopNaNCameras = 0;
					var cameras = GetComponentsInChildren<Camera>(roots);
					for (int i = 0; i < cameras.Length; i++) if (cameras[i] != null && IsUrpStopNaNEnabled(cameras[i].gameObject)) facts.stopNaNCameras++;
				}

				return facts;
				});
				if (completed != null)
				{
					completed.unavailableNativeCapabilities &= ~(int)required;
					if (completed.unavailableNativeCapabilities == 0) completed.nativeLimitation = string.Empty;
					return completed;
				}
			}
			catch (Exception e)
			{
				parsed.Facts.nativeLimitation = string.IsNullOrEmpty(parsed.Limitation) ? e.Message : parsed.Limitation + " " + e.Message;
			}

			parsed.Facts.unavailableNativeCapabilities |= (int)required;
			if (string.IsNullOrEmpty(parsed.Facts.nativeLimitation))
				parsed.Facts.nativeLimitation = parsed.Limitation ?? "Native hierarchy extraction was unavailable for this dependency-hash snapshot.";
			return parsed.Facts;
		}

		static T[] GetComponentsInChildren<T>(GameObject[] roots) where T : Component
		{
			if (roots == null || roots.Length == 0) return Array.Empty<T>();
			var result = new List<T>();
			for (int i = 0; i < roots.Length; i++) if (roots[i] != null) result.AddRange(roots[i].GetComponentsInChildren<T>(true));
			return result.ToArray();
		}

		static HierarchyFacts WithHierarchyRoots(string assetPath, bool isScene, Func<GameObject[], HierarchyFacts> action)
		{
			if (action == null) return null;
			if (!isScene)
			{
				var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
				if (root == null) return null;
				try { return action(new[] { root }); }
				finally { }
			}

			UnityEngine.SceneManagement.Scene scene = default;
			bool opened = false;
			try
			{
				scene = EditorSceneManager.OpenPreviewScene(assetPath);
				opened = scene.IsValid();
				if (!opened) return null;
				return action(scene.GetRootGameObjects());
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not complete targeted extraction for '{assetPath}': {e.Message}");
				return null;
			}
			finally
			{
				if (opened) EditorSceneManager.ClosePreviewScene(scene);
			}
		}

		static SerializedUnityHierarchy.MeshStats ResolveSerializedMeshStats(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return default;
			string hash;
			try { hash = AssetDatabase.GetAssetDependencyHash(assetPath).ToString(); }
			catch { hash = string.Empty; }
			if (s_SerializedMeshStats.TryGetValue(assetPath, out var cached) && string.Equals(cached.Hash, hash, StringComparison.Ordinal))
				return cached.Stats;

			var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
			var stats = mesh == null
				? default(SerializedUnityHierarchy.MeshStats)
				: new SerializedUnityHierarchy.MeshStats { VertexCount = mesh.vertexCount, SubMeshCount = mesh.subMeshCount, Known = true };
			s_SerializedMeshStats[assetPath] = new SerializedMeshCacheEntry { Hash = hash, Stats = stats };
			return stats;
		}

		static HierarchyFacts MeasurePrefab(string assetPath, HierarchyMeasurementNeeds needs)
		{
			double loadStart = ScanTiming.Now;
			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/LoadPrefabRoot", ScanTiming.Now - loadStart);
			if (root == null) return null;

			double measureStart = ScanTiming.Now;
			var facts = Measure(new[] { root }, needs);
			ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/WalkPrefabHierarchy", ScanTiming.Now - measureStart);
			return facts;
		}

		AssetEntry AnalyzeScene(string assetPath, ScanContext ctx)
		{
			var entry = NewEntry(assetPath, AssetCategory.Scene, "Scene", ctx);

			var facts = GetHierarchyFacts(assetPath, ctx, true);
			if (facts == null) return entry;

			ctx.MaterialUsage?.Record(assetPath, true, facts.materialPaths, facts.materialRendererCounts);

			// Scenes get the same fixes a prefab does now - PRE001 and PRE008 both go through the
			// scene-editing path (FixHierarchy) rather than PrefabUtility's.
			Evaluate(facts, entry, ctx.Settings, true, true);
			entry.Finalise();
			return entry;
		}

		/// <summary>
		/// Opens the scene in an isolated preview stage and measures it.
		/// A scene holding missing prefabs or scripts makes Unity log one error per broken instance
		/// as it loads. Those come from native code and cannot be muted from managed script, so
		/// PRE002/PRE013 report the same problem as findings you can act on instead.
		/// </summary>
		static HierarchyFacts MeasureScene(string assetPath, HierarchyMeasurementNeeds needs)
		{
			UnityEngine.SceneManagement.Scene scene = default;
			bool opened = false;
			try
			{
				double openStart = ScanTiming.Now;
				scene = EditorSceneManager.OpenPreviewScene(assetPath);
				ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/OpenPreviewScene", ScanTiming.Now - openStart);
				opened = scene.IsValid();
				if (!opened) return null;
				double measureStart = ScanTiming.Now;
				var facts = Measure(scene.GetRootGameObjects(), needs);
				ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/WalkSceneHierarchy", ScanTiming.Now - measureStart);
				return facts;
			}
			catch (Exception e)
			{
				Debug.LogWarning(ProjectOptimizerConstants.LogPrefix + $" Could not analyze scene '{assetPath}': {e.Message}");
				return null;
			}
			finally
			{
				if (opened)
				{
					double closeStart = ScanTiming.Now;
					EditorSceneManager.ClosePreviewScene(scene);
					ScanTiming.Active?.AddDiagnosticTiming("PrefabScene/ClosePreviewScene", ScanTiming.Now - closeStart);
				}
			}
		}

		// --- measurement (expensive, cached) ---------------------------------------
		static HierarchyFacts Measure(IEnumerable<GameObject> roots, HierarchyMeasurementNeeds needs)
		{
			var facts = new HierarchyFacts();
			// Measurement needs are resolved once so cache-mask calculation and actual traversal share one source of truth.
			bool needMissingScripts = needs.MissingScripts;
			bool needMissingRefs = needs.MissingRefs;
			bool needMissingPrefabs = needs.MissingPrefabs;
			bool needMeshColliders = needs.MeshColliders;
			bool needMaterialMismatch = needs.MaterialMismatch;
			bool needDepth = needs.Depth;
			bool needEmpty = needs.Empty;
			bool needRaycast = needs.Raycast;
			bool needParticles = needs.Particles;
			bool needAlwaysAnimate = needs.AlwaysAnimate;
			bool needStatic = needs.Static;
			bool needLights = needs.Lights;
			bool needDense = needs.Dense;
			bool needContinuous = needs.Continuous;
			bool needStopNaN = needs.StopNaN;
			bool needCanvases = needs.Canvases;
			bool needRaycasters = needs.Raycasters;
			bool needLayout = needs.Layout;
			bool needRigidbodies = needs.Rigidbodies;
			bool needSolverIterations = needs.SolverIterations;
			bool needKinematicCcd = needs.KinematicCcd;
			bool needJoints = needs.Joints;
			bool needSkinned = needs.Skinned;
			bool needExpensiveParticleModules = needs.ExpensiveParticleModules;
			bool needRendererData = needMaterialMismatch || needDense || needStatic || needRaycast || needSkinned;
			bool needAnyComponentList = needMissingRefs || needEmpty || needRendererData || needMeshColliders || needRaycast || needParticles || needAlwaysAnimate || needStopNaN || needContinuous || needLights || needCanvases || needRaycasters || needLayout || needRigidbodies || needSolverIterations || needKinematicCcd || needJoints || needExpensiveParticleModules;
			bool needObjectPath = needMissingRefs || needMissingPrefabs || needEmpty || needMeshColliders || needRendererData || needRaycast || needParticles || needAlwaysAnimate || needContinuous || needCanvases || needRaycasters || needLayout || needRigidbodies || needSolverIterations || needKinematicCcd || needJoints || needSkinned || needExpensiveParticleModules;

			int objectCount = 0;
			int maxDepth = 0;
			int missingScripts = 0;
			var missingRefs = facts.missingRefs;
			var missingPrefabs = facts.missingPrefabs;
			var missingPrefabRoots = new HashSet<string>(StringComparer.Ordinal);
			var emptyObjects = facts.emptyObjects;
			var meshColliders = facts.meshColliders;
			var materialMismatches = facts.materialMismatches;
			var raycastTargets = facts.raycastTargets;
			var alwaysAnimate = facts.alwaysAnimate;
			var unstaticGeometry = facts.unstaticGeometry;
			var denseNoLod = facts.denseNoLod;
			var continuousBodies = facts.continuousBodies;
			int rendererCount = 0;
			int shadowLights = 0;
			int stopNaNCameras = 0;
			int canvasCount = 0;
			int graphicRaycasterCount = 0;
			int layoutComponentCount = 0;
			int rigidbodyCount = 0;
			int jointCount = 0;
			int particleExpensiveModuleCount = 0;

			var materialUses = new Dictionary<string, int>(StringComparer.Ordinal);

			// The transform enumeration is parent-before-child for Unity hierarchies. Keep the small
			// immutable facts we derive for a parent so child checks become O(1) instead of repeatedly
			// walking the same ancestor chain.
			var pathById = needObjectPath ? new Dictionary<int, string>() : null;
			var depthById = needDepth ? new Dictionary<int, int>() : null;
			var lodChainById = needDense ? new Dictionary<int, bool>() : null;
			var rigidbodyChainById = needStatic ? new Dictionary<int, bool>() : null;
			var animatorChainById = (needStatic || needAlwaysAnimate) ? new Dictionary<int, bool>() : null;
			var pointerChainById = needRaycast ? new Dictionary<int, bool>() : null;
			var missingPrefabRootById = needMissingPrefabs ? new Dictionary<int, string>() : null;

			foreach (var root in roots)
			{
				if (root == null) continue;
				foreach (var transform in root.GetComponentsInChildren<Transform>(true))
				{
					if (transform == null) continue;
					var go = transform.gameObject;
					objectCount++;

					int id = transform.GetInstanceID();
					Transform parent = transform.parent;
					int parentId = parent != null ? parent.GetInstanceID() : 0;

					string objectPath = null;
					if (pathById != null)
					{
						if (parent != null && pathById.TryGetValue(parentId, out var parentPath))
							objectPath = parentPath + "/" + transform.name;
						else
							objectPath = Path(transform);
						pathById[id] = objectPath;
					}

					if (depthById != null)
					{
						int depth = parent != null && depthById.TryGetValue(parentId, out var parentDepth)
							? parentDepth + 1
							: Depth(transform);
						depthById[id] = depth;
						if (depth > maxDepth) maxDepth = depth;
					}

					bool missingPrefab = false;
					string missingPrefabRoot = null;
					if (needMissingPrefabs)
					{
						missingPrefab = PrefabUtility.IsPrefabAssetMissing(go);
						if (missingPrefab)
						{
							missingPrefabRoot = parent != null && missingPrefabRootById.TryGetValue(parentId, out var inheritedRoot)
								? inheritedRoot
								: (objectPath ?? Path(transform));
							missingPrefabRoots.Add(missingPrefabRoot);
							missingPrefabRootById[id] = missingPrefabRoot;
						}
					}

					int missing = needMissingScripts ? GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) : 0;
					if (missing > 0) missingScripts += missing;

					Component[] components = needAnyComponentList
						? go.GetComponents<Component>()
						: Array.Empty<Component>();

					if (needEmpty && components.Length == 1 && transform.childCount == 0)
						emptyObjects.Add(objectPath ?? Path(transform));

					Renderer renderer = null;
					MeshCollider meshCollider = null;
					Graphic graphic = null;
					ParticleSystem particles = null;
					Animator animator = null;
					Camera camera = null;
					Rigidbody body = null;
					Light light = null;
					MeshFilter meshFilter = null;
					bool hasLodGroup = false;
					bool hasEventSystemHandler = false;

					for (int ci = 0; ci < components.Length; ci++)
					{
						var component = components[ci];
						if (component == null) continue;

						if (needMissingRefs)
							CollectMissingReferences(component, objectPath ?? Path(transform), missingRefs);

						if (renderer == null && component is Renderer r) renderer = r;
						if (meshCollider == null && component is MeshCollider mc) meshCollider = mc;
						if (graphic == null && component is Graphic g) graphic = g;
						if (particles == null && component is ParticleSystem ps) particles = ps;
						if (animator == null && component is Animator an) animator = an;
						if (camera == null && component is Camera cam) camera = cam;
						if (body == null && component is Rigidbody rb) body = rb;
						if (light == null && component is Light li) light = li;
						if (meshFilter == null && component is MeshFilter mf) meshFilter = mf;
						if (needDense && !hasLodGroup && component is LODGroup) hasLodGroup = true;
						if (needRaycast && !hasEventSystemHandler && component is UnityEngine.EventSystems.IEventSystemHandler)
							hasEventSystemHandler = true;
						if (needCanvases && component is Canvas) canvasCount++;
						if (needRaycasters && component is GraphicRaycaster) graphicRaycasterCount++;
						if (needLayout && (component is LayoutGroup || component is ContentSizeFitter)) layoutComponentCount++;
						if (needJoints && (component is Joint || component is Joint2D))
						{
							jointCount++;
							facts.jointPaths.Add(objectPath ?? Path(transform));
						}
					}

					if (body != null && needRigidbodies)
					{
						rigidbodyCount++;
						string bodyPath = objectPath ?? Path(transform);
						facts.rigidbodyPaths.Add(bodyPath);
						if (needSolverIterations) facts.rigidbodySolverIterations.Add(body.solverIterations);
						if (needKinematicCcd && body.isKinematic && body.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
							facts.continuousDynamicKinematicBodies.Add(bodyPath);
					}

					bool hasLodChain = false;
					if (lodChainById != null)
					{
						bool parentHasLod = parent != null && lodChainById.TryGetValue(parentId, out var parentLod) && parentLod;
						hasLodChain = hasLodGroup || parentHasLod;
						lodChainById[id] = hasLodChain;
					}

					bool hasRigidbodyChain = false;
					if (rigidbodyChainById != null)
					{
						bool parentHasBody = parent != null && rigidbodyChainById.TryGetValue(parentId, out var parentBody) && parentBody;
						hasRigidbodyChain = body != null || parentHasBody;
						rigidbodyChainById[id] = hasRigidbodyChain;
					}

					bool hasAnimatorChain = false;
					if (animatorChainById != null)
					{
						bool parentHasAnimator = parent != null && animatorChainById.TryGetValue(parentId, out var parentAnimator) && parentAnimator;
						hasAnimatorChain = animator != null || parentHasAnimator;
						animatorChainById[id] = hasAnimatorChain;
					}

					if (pointerChainById != null)
					{
						bool parentHandlesInput = parent != null && pointerChainById.TryGetValue(parentId, out var parentPointer) && parentPointer;
						pointerChainById[id] = hasEventSystemHandler || parentHandlesInput;
					}

					if (needMeshColliders && meshCollider != null && !meshCollider.convex)
						meshColliders.Add(objectPath ?? Path(transform));

					if (renderer != null)
					{
						rendererCount++;
						if (needSkinned && renderer is SkinnedMeshRenderer skinned && skinned.updateWhenOffscreen)
							facts.skinnedAlwaysUpdate.Add(objectPath ?? Path(transform));
						RecordMaterials(renderer, materialUses);
						int lodVertexCount = MeshVertexCount(renderer);

						if (needDense && lodVertexCount > 5000 && !hasLodChain)
							denseNoLod.Add((objectPath ?? Path(transform)) + " (" + lodVertexCount + " verts)");

						int submeshes = needMaterialMismatch ? SubMeshCount(renderer, meshFilter) : 0;
						int slots = renderer.sharedMaterials.Length;
						if (needMaterialMismatch && submeshes > 0 && slots != submeshes)
							materialMismatches.Add((objectPath ?? Path(transform)) + " (" + slots + " material"
								+ (slots == 1 ? "" : "s") + ", " + submeshes + " sub-mesh"
								+ (submeshes == 1 ? "" : "es") + " - "
								+ (slots > submeshes
									? (slots - submeshes) + " surplus, redrawing the last sub-mesh"
									: (submeshes - slots) + " sub-mesh(es) not drawn") + ")");

						if (needStatic && renderer is MeshRenderer
							&& GameObjectUtility.GetStaticEditorFlags(go) == 0
							&& !hasRigidbodyChain
							&& !hasAnimatorChain)
							unstaticGeometry.Add(objectPath ?? Path(transform));
					}

					if (needRaycast && graphic != null && graphic.raycastTarget)
					{
						bool pointerHandled = pointerChainById != null
							&& pointerChainById.TryGetValue(id, out var handlesPointer) && handlesPointer;
						if (!pointerHandled)
							raycastTargets.Add(objectPath ?? Path(transform));
					}

					if (needParticles && particles != null)
					{
						facts.particlePaths.Add(objectPath ?? Path(transform));
						facts.particleMax.Add(particles.main.maxParticles);
						if (needExpensiveParticleModules)
						{
							string particlePath = objectPath ?? Path(transform);
							var collisionModule = particles.collision;
							if (collisionModule.enabled) { particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(particlePath + " (Collision)"); }
							var lightsModule = particles.lights;
							if (lightsModule.enabled) { particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(particlePath + " (Lights)"); }
							var trailsModule = particles.trails;
							if (trailsModule.enabled) { particleExpensiveModuleCount++; facts.expensiveParticleModules.Add(particlePath + " (Trails)"); }
						}
					}

					if (needAlwaysAnimate && animator != null && animator.cullingMode == AnimatorCullingMode.AlwaysAnimate)
						alwaysAnimate.Add(objectPath ?? Path(transform));

					if (needStopNaN && camera != null && IsUrpStopNaNEnabled(go)) stopNaNCameras++;

					if (needContinuous && body != null && body.collisionDetectionMode != CollisionDetectionMode.Discrete)
						continuousBodies.Add((objectPath ?? Path(transform)) + " (" + body.collisionDetectionMode + ")");

					if (needLights && light != null && light.enabled && light.shadows != LightShadows.None
						&& light.lightmapBakeType != LightmapBakeType.Baked)
						shadowLights++;
				}
			}

			foreach (var missingRoot in missingPrefabRoots)
				facts.missingPrefabs.Add(missingRoot);

			facts.objectCount = objectCount;
			facts.maxDepth = maxDepth;
			facts.rendererCount = rendererCount;
			facts.missingScripts = missingScripts;
			facts.shadowLights = shadowLights;
			facts.stopNaNCameras = stopNaNCameras;
			facts.canvasCount = canvasCount;
			facts.graphicRaycasterCount = graphicRaycasterCount;
			facts.layoutComponentCount = layoutComponentCount;
			facts.rigidbodyCount = rigidbodyCount;
			facts.jointCount = jointCount;
			facts.particleExpensiveModuleCount = particleExpensiveModuleCount;

			foreach (var pair in materialUses)
			{
				facts.materialPaths.Add(pair.Key);
				facts.materialRendererCounts.Add(pair.Value);
			}
			return facts;
		}

		/// <summary>
		/// Adds one renderer's material slots to the tally. Counted per slot, not per unique
		/// material: two slots on one renderer are two draws, which is the thing being measured.
		/// </summary>
		static void RecordMaterials(Renderer renderer, Dictionary<string, int> into)
		{
			var materials = renderer.sharedMaterials;
			if (materials == null) return;

			for (int i = 0; i < materials.Length; i++)
			{
				var material = materials[i];
				if (material == null) continue;

				var path = AssetDatabase.GetAssetPath(material);

				// An empty path is a material that lives inside the prefab or scene rather than as
				// its own asset. There is no material row for it to inform, so it is left out
				// rather than counted against a path that means nothing.
				if (string.IsNullOrEmpty(path)) continue;

				into.TryGetValue(path, out int count);
				into[path] = count + 1;
			}
		}

		static Type s_UrpAdditionalCameraDataType;
		static System.Reflection.PropertyInfo s_UrpStopNaNProperty;
		static System.Reflection.PropertyInfo s_UrpRenderTypeProperty;
		static bool s_ResolvedUrpCameraData;

		static bool IsUrpStopNaNEnabled(GameObject go)
		{
			if (go == null) return false;
			if (!s_ResolvedUrpCameraData)
			{
				s_ResolvedUrpCameraData = true;
				s_UrpAdditionalCameraDataType = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime");
				if (s_UrpAdditionalCameraDataType != null)
				{
					const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
					s_UrpStopNaNProperty = s_UrpAdditionalCameraDataType.GetProperty("stopNaN", flags);
					s_UrpRenderTypeProperty = s_UrpAdditionalCameraDataType.GetProperty("renderType", flags);
				}
			}
			if (s_UrpAdditionalCameraDataType == null
				|| s_UrpStopNaNProperty == null
				|| s_UrpRenderTypeProperty == null) return false;
			try
			{
				var component = go.GetComponent(s_UrpAdditionalCameraDataType);
				if (component == null) return false;
				var renderType = s_UrpRenderTypeProperty.GetValue(component, null);
				if (renderType == null || !string.Equals(renderType.ToString(), "Base", StringComparison.Ordinal))
					return false;
				return Convert.ToBoolean(s_UrpStopNaNProperty.GetValue(component, null));
			}
			catch
			{
				return false;
			}
		}

		// --- evaluation (cheap, always re-run so settings apply immediately) ---------

		static bool IsNativeUnavailable(HierarchyFacts facts, SerializedUnityHierarchy.NativeCapability capability)
			=> facts != null && ((SerializedUnityHierarchy.NativeCapability)facts.unavailableNativeCapabilities & capability) != 0;

		static List<string> FilterGenericCcdBodies(HierarchyFacts facts, OptimizerSettings settings, string assetPath)
		{
			var source = facts != null ? facts.continuousBodies : null;
			if (source == null || source.Count == 0) return new List<string>();
			if (settings == null || !settings.IsRuleEnabled("PHY003") || settings.IsSuppressed("PHY003", assetPath)
				|| IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.RigidbodyState)
				|| facts.continuousDynamicKinematicBodies == null || facts.continuousDynamicKinematicBodies.Count == 0)
				return new List<string>(source);
			var specificPaths = new HashSet<string>(facts.continuousDynamicKinematicBodies, StringComparer.Ordinal);
			var pathCounts = source.Select(GetCcdBodyPath)
				.GroupBy(path => path, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
			// Serialized hierarchy labels are name-based. If duplicate sibling names collapse two bodies
			// to the same path, identity is ambiguous, so retain the generic diagnostic rather than
			// accidentally suppressing a different body's issue.
			return source.Where(body =>
			{
				string path = GetCcdBodyPath(body);
				return !specificPaths.Contains(path) || !pathCounts.TryGetValue(path, out int count) || count > 1;
			}).ToList();
		}

		static string GetCcdBodyPath(string bodyDescription)
		{
			if (string.IsNullOrEmpty(bodyDescription)) return string.Empty;
			int modeStart = bodyDescription.LastIndexOf(" (", StringComparison.Ordinal);
			return modeStart > 0 ? bodyDescription.Substring(0, modeStart) : bodyDescription;
		}

		static void Evaluate(HierarchyFacts facts, AssetEntry entry, OptimizerSettings settings, bool fixable, bool isScene)
		{
			int objectCount = facts.objectCount;
			int maxDepth = facts.maxDepth;
			int rendererCount = facts.rendererCount;
			int missingScripts = facts.missingScripts;
			int shadowLights = facts.shadowLights;
			int stopNaNCameras = facts.stopNaNCameras;
			var missingRefs = facts.missingRefs;
			var missingPrefabs = facts.missingPrefabs;
			var missingPrefabRoots = new HashSet<string>(StringComparer.Ordinal);
			var emptyObjects = facts.emptyObjects;
			var meshColliders = facts.meshColliders;
			var materialMismatches = facts.materialMismatches;
			var raycastTargets = facts.raycastTargets;
			var alwaysAnimate = facts.alwaysAnimate;
			var unstaticGeometry = facts.unstaticGeometry;
			var denseNoLod = facts.denseNoLod;
			var continuousBodies = FilterGenericCcdBodies(facts, settings, entry.Path);
			string[] allContinuousBodyPaths = continuousBodies.Select(GetCcdBodyPath).ToArray();
			bool canSafelyTargetCcdBodies = allContinuousBodyPaths.All(path => !string.IsNullOrEmpty(path))
				&& allContinuousBodyPaths.Distinct(StringComparer.Ordinal).Count() == allContinuousBodyPaths.Length;
			string[] continuousBodyPaths = allContinuousBodyPaths.Where(path => !string.IsNullOrEmpty(path))
				.Distinct(StringComparer.Ordinal).ToArray();

			var bigParticleSystems = new List<string>();
			for (int i = 0; i < facts.particlePaths.Count && i < facts.particleMax.Count; i++)
			{
				if (facts.particleMax[i] > settings.particleMaxWarn)
					bigParticleSystems.Add(facts.particlePaths[i] + " (" + facts.particleMax[i] + ")");
			}

			var highSolverIterations = new List<string>();
			for (int i = 0; i < facts.rigidbodyPaths.Count && i < facts.rigidbodySolverIterations.Count; i++)
			{
				int iterations = facts.rigidbodySolverIterations[i];
				if (iterations > settings.physicsSolverIterationsWarn)
					highSolverIterations.Add(facts.rigidbodyPaths[i] + " (" + iterations + ")");
			}

			entry.Set("objects", (long)objectCount);
			entry.Set("depth", (long)maxDepth);
			entry.Set("renderers", (long)rendererCount);
			entry.Set("lights", (long)shadowLights);
			entry.Set("missing", (long)(missingScripts + missingRefs.Count + missingPrefabs.Count));
			entry.Set("canvases", (long)facts.canvasCount);
			entry.Set("raycasters", (long)facts.graphicRaycasterCount);
			entry.Set("layoutComponents", (long)facts.layoutComponentCount);
			entry.Set("rigidbodies", (long)facts.rigidbodyCount);
			entry.Set("joints", (long)facts.jointCount);
			entry.Set("skinnedUpdateWhenOffscreen", (long)facts.skinnedAlwaysUpdate.Count);
			entry.Set("particleExpensiveModules", (long)facts.particleExpensiveModuleCount);

			using (RuleTiming.Begin("PRE001", entry.Path))
				if (missingScripts > 0)
				{
					entry.AddIssue("PRE001", missingScripts + " missing script(s)", "0", 0,
						fixable ? (Action<FixContext>)(c => FixHierarchy(c, isScene, root =>
						{
							foreach (var t in root.GetComponentsInChildren<Transform>(true))
								GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
						})) : null,
						verify: fixable ? (Func<FixContext, bool>)(c => CountMissingScripts(c.AssetPath, isScene) == 0) : null);
				}

			using (RuleTiming.Begin("PRE002", entry.Path))
				if (missingRefs.Count > 0)
					entry.AddIssue("PRE002", missingRefs.Count + " broken reference(s)", "0")
						?.Items("Broken on", missingRefs);

			using (RuleTiming.Begin("PRE013", entry.Path))
				if (missingPrefabs.Count > 0)
				{
					entry.AddIssue("PRE013", missingPrefabs.Count + " missing prefab instance(s)", "0")
							?.Items("Missing on", missingPrefabs);
				}

			using (RuleTiming.Begin("PRE003", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.MeshColliderState) && meshColliders.Count > 0)
					entry.AddIssue("PRE003", meshColliders.Count + " non-convex mesh collider(s)", "Primitive or convex colliders")
						?.Items("Colliders on", meshColliders);

			using (RuleTiming.Begin("PRE004", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.MeshStats) && materialMismatches.Count > 0)
				{
					bool surplusOnly = materialMismatches.All(x => x.IndexOf("surplus", StringComparison.OrdinalIgnoreCase) >= 0);
					using (RuleTiming.Begin("PRE004", entry.Path))
						if (surplusOnly)
						{
							entry.AddIssue("PRE004", materialMismatches.Count + " renderer(s) have surplus material slots", "Trim only provably unused surplus slots", 0,
								c => TrimSurplusMaterialSlots(c),
								"All detected mismatches are surplus-slot cases. The fix trims only slots beyond the mesh sub-mesh count.",
								verify: c => VerifyNoSurplusMaterialSlots(c));
						}
						else
							entry.AddIssue("PRE016", materialMismatches.Count + " renderer(s) mismatched", "Review material-slot/sub-mesh mapping", 0, null,
								"Static analysis cannot safely infer the intended material mapping for missing or reordered slots.")?.Items("Renderers", materialMismatches);
				}

			using (RuleTiming.Begin("PRE005", entry.Path))
				if (objectCount > settings.prefabObjectWarn)
					entry.AddIssue("PRE005", objectCount + " GameObjects", "under " + settings.prefabObjectWarn);

			using (RuleTiming.Begin("PRE006", entry.Path))
				if (maxDepth > settings.hierarchyDepthWarn)
					entry.AddIssue("PRE006", maxDepth + " levels deep", "under " + settings.hierarchyDepthWarn);

			using (RuleTiming.Begin("PRE007", entry.Path))
				if (emptyObjects.Count > 0)
				{
					// Reported only. An object with nothing but a Transform is very often a deliberate
					// marker - a spawn point, muzzle, attach anchor - referenced by name or by a direct
					// reference from a script. Deleting those automatically silently breaks behaviour,
					// so this one is left for a human to judge.
					entry.AddIssue("PRE007", emptyObjects.Count + " empty GameObject(s)",
						"Only the ones that are truly unused", 0, null, Preview(emptyObjects));
				}

			using (RuleTiming.Begin("PRE008", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.UiInputSemantics) && raycastTargets.Count > 0)
				{
					entry.AddIssue("PRE008", raycastTargets.Count + " graphic(s) raycastable", "Review Fix: Raycast Target off", 0,
						fixable ? (Action<FixContext>)(c => FixHierarchy(c, isScene, root =>
						{
							foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
							{
								if (HandlesPointerInput(graphic.gameObject)) continue;
								graphic.raycastTarget = false;
							}
						})) : null,
						verify: fixable ? (Func<FixContext, bool>)(c => CountLooseRaycastTargets(c.AssetPath, isScene) == 0) : null)
							?.Items("Raycast targets on", raycastTargets);
				}

			using (RuleTiming.Begin("PRE009", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.ParticleState) && bigParticleSystems.Count > 0)
					entry.AddIssue("PRE009", bigParticleSystems.Count + " system(s)", "under " + settings.particleMaxWarn + " particles",
						options: new List<FixOption> { new FixOption {
							Label = "Lower Particle Budget",
							Recommended = true,
							Risk = FixRisk.High,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => FixHierarchy(c, isScene, root => { int budget = Mathf.Max(64, OptimizerSettings.Instance?.particleMaxWarn ?? 500); foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true)) { var m = ps.main; if (m.maxParticles > budget) m.maxParticles = budget; } }),
							Verify = c => ParticleBudgetsWithin(c.AssetPath, isScene, Mathf.Max(64, OptimizerSettings.Instance?.particleMaxWarn ?? 500))
						} }
					)
						?.Items("Systems", bigParticleSystems);

		static bool ParticleBudgetsWithin(string assetPath, bool isScene, int budget)
		{
			try
			{
				if (isScene)
					return CountInPreviewScene(assetPath, go =>
					{
						int over = 0;
						foreach (var ps in go.GetComponents<ParticleSystem>()) if (ps != null && ps.main.maxParticles > budget) over++;
						return over;
					}) == 0;

				var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
				if (root == null) return false;
				foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true)) if (ps != null && ps.main.maxParticles > budget) return false;
				return true;
			}
			catch { return false; }
		}
			using (RuleTiming.Begin("UIT001", entry.Path))
				if (facts.canvasCount > settings.uiCanvasWarn)
					entry.AddIssue("UIT001", facts.canvasCount + " Canvas component(s)", "at most " + settings.uiCanvasWarn + " unless independently rebuildable")
						?.Note("Canvas count is a review signal, not proof of a performance problem. Separate canvases are useful when update frequency differs.", "Context");

			using (RuleTiming.Begin("UIT002", entry.Path))
				if (facts.graphicRaycasterCount > settings.uiRaycasterWarn)
					entry.AddIssue("UIT002", facts.graphicRaycasterCount + " GraphicRaycaster component(s)", "at most " + settings.uiRaycasterWarn + " unless multiple input surfaces are intentional")
						?.Note("Remove raycasters only from canvases that are confirmed not to receive pointer/input queries.", "Context");

			using (RuleTiming.Begin("UIT003", entry.Path))
				if (facts.layoutComponentCount > settings.uiLayoutWarn)
					entry.AddIssue("UIT003", facts.layoutComponentCount + " layout component(s)", "at most " + settings.uiLayoutWarn + " where static sizing is possible")
						?.Note("Nested layout systems can amplify rebuild work; inspect actual rebuild frequency before changing the hierarchy.", "Context");

			using (RuleTiming.Begin("PHY001", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.RigidbodyState) && facts.rigidbodyCount > settings.rigidbodyCountWarn)
					entry.AddIssue("PHY001", facts.rigidbodyCount + " Rigidbody component(s)", "at most " + settings.rigidbodyCountWarn + " unless the active physics population is profiled")
						?.Note("High Rigidbody counts can be valid. Profile sleeping, collision layers, query frequency and contact counts before reducing them.", "Context");

			using (RuleTiming.Begin("PHY002", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.RigidbodyState) && highSolverIterations.Count > 0)
					entry.AddIssue("PHY002", highSolverIterations.Count + " Rigidbody(s) above " + settings.physicsSolverIterationsWarn + " solver iterations", "review per-body solver iteration overrides")
						?.Items("Rigidbodies", highSolverIterations);

			using (RuleTiming.Begin("PHY003", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.RigidbodyState) && facts.continuousDynamicKinematicBodies.Count > 0)
					entry.AddIssue("PHY003", facts.continuousDynamicKinematicBodies.Count + " kinematic Rigidbody(s) on Continuous Dynamic CCD", "review CCD mode; use the least expensive mode that meets tunnelling requirements")
						?.Items("Rigidbodies", facts.continuousDynamicKinematicBodies);

			using (RuleTiming.Begin("PHY004", entry.Path))
				if (facts.jointCount > settings.jointCountWarn)
					entry.AddIssue("PHY004", facts.jointCount + " joint component(s)", "at most " + settings.jointCountWarn + " unless the constraint graph requires more")
						?.Items("Joints", facts.jointPaths);

			using (RuleTiming.Begin("RND001", entry.Path))
				if (facts.skinnedAlwaysUpdate.Count > 0)
					entry.AddIssue("RND001", facts.skinnedAlwaysUpdate.Count + " SkinnedMeshRenderer(s) update off-screen", "disable Update When Offscreen for cosmetic-only skinning")
						?.Items("Skinned renderers", facts.skinnedAlwaysUpdate);

			using (RuleTiming.Begin("VFX001", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.ParticleState) && facts.particleExpensiveModuleCount > settings.particleModuleCountWarn)
				{
					var particleOptions = new List<FixOption>
					{
						new FixOption { Label = "Disable Collision", ResultDescription = "Disables Particle Collision modules in this hierarchy.", Risk = FixRisk.Medium, Apply = c => SetParticleModule(c, isScene, ExpensiveParticleModule.Collision, false), Verify = c => CountParticleModule(c.AssetPath, isScene, ExpensiveParticleModule.Collision) == 0 },
						new FixOption { Label = "Disable Lights", ResultDescription = "Disables Particle Lights modules in this hierarchy.", Risk = FixRisk.Medium, Apply = c => SetParticleModule(c, isScene, ExpensiveParticleModule.Lights, false), Verify = c => CountParticleModule(c.AssetPath, isScene, ExpensiveParticleModule.Lights) == 0 },
						new FixOption { Label = "Disable Trails", ResultDescription = "Disables Particle Trails modules in this hierarchy.", Risk = FixRisk.Medium, Apply = c => SetParticleModule(c, isScene, ExpensiveParticleModule.Trails, false), Verify = c => CountParticleModule(c.AssetPath, isScene, ExpensiveParticleModule.Trails) == 0 },
						new FixOption { Label = "Disable All Three", ResultDescription = "Disables Collision, Lights and Trails modules in this hierarchy.", Warning = "This changes particle appearance/simulation. Confirm the effect does not rely on these modules before applying.", Risk = FixRisk.High, Apply = c => SetParticleModule(c, isScene, ExpensiveParticleModule.All, false), Verify = c => CountParticleModule(c.AssetPath, isScene, ExpensiveParticleModule.All) == 0 }
					};
					entry.AddIssue("VFX001", facts.particleExpensiveModuleCount + " expensive particle module(s)", "at most " + settings.particleModuleCountWarn + " enabled Collision/Lights/Trails modules", basis: SavingsBasis.Unknown, options: particleOptions)
						?.Items("Modules", facts.expensiveParticleModules);
				}

			using (RuleTiming.Begin("PRE010", entry.Path))
				if (alwaysAnimate.Count > 0)
				{
					// A Review Fix, not a Verified one: culling an animator stops its state machine, root
					// motion and animation events while it is off-screen. For a background prop that is a
					// saving; for anything the game reads back it is a bug that only shows up when the
					// player is not looking - which is exactly why a human has to choose this, per animator
					// group, rather than the tool choosing it for every prefab and scene at once.
					var options = new List<FixOption>
				{
					new FixOption
					{
						Label = "Cull Update Transforms",
						ResultDescription = "Keeps the state machine and root motion evaluated off-screen, but skips retargeting, IK "
											+ "and Transform writes when the renderers are invisible.",
						Recommended = true,
						Apply = c => SetAlwaysAnimateCulling(c, isScene, AnimatorCullingMode.CullUpdateTransforms),
						Verify = c => CountAlwaysAnimate(c.AssetPath, isScene) == 0
					},
					new FixOption
					{
						Label = "Cull Completely",
						ResultDescription = "Stops all evaluation off-screen: state machine, root motion, "
											+ "animation events and gameplay reads all stop while out of view.",
						Warning = "Anything reading animation state, root motion or animation events while "
								  + "off-screen will stop doing so. Choose this only for purely cosmetic animation.",
						Apply = c => SetAlwaysAnimateCulling(c, isScene, AnimatorCullingMode.CullCompletely),
						Verify = c => CountAlwaysAnimate(c.AssetPath, isScene) == 0
					}
				};

					entry.AddIssue("PRE010", alwaysAnimate.Count + " animator(s) on Always Animate",
						"Review Fix: choose a culling mode below",
						basis: SavingsBasis.Unknown, options: options)
							?.Items("Animators", alwaysAnimate);
				}

			using (RuleTiming.Begin("PRE011", entry.Path))
				if (unstaticGeometry.Count > 0)
					entry.AddIssue("PRE011", unstaticGeometry.Count + " non-static renderer(s)", "Static flags set",
						options: new List<FixOption> { new FixOption {
							Label = "Mark Static Geometry",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => GameObjectAsset(c, go => { foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.gameObject.isStatic = true; })
						} }
					)
						?.Items("Renderers", unstaticGeometry);

			using (RuleTiming.Begin("PRE014", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.MeshStats) && denseNoLod.Count > 0)
					entry.AddIssue("PRE014", denseNoLod.Count + " dense renderer(s) without LOD", "An LOD Group")
						?.Items("Renderers", denseNoLod);

			using (RuleTiming.Begin("PRE015", entry.Path))
				if (continuousBodies.Count > 0)
				{
					// Review Fix, not Verified. Continuous detection is almost always switched on to stop
					// something tunnelling through geometry, and turning it back off restores that bug
					// silently - physics changes do not announce themselves until someone plays the level.
					// Applies the chosen mode to every body this finding lists, together; a per-body picker
					// would need one issue per Rigidbody, which the hierarchy walk does not produce today.
					var options = new List<FixOption>
				{
					new FixOption
					{
						ActionId = "discrete",
						Label = "Discrete",
						ResultDescription = "Turns continuous collision detection off for the affected bodies.",
						Warning = "Use only when these bodies do not need CCD; otherwise fast objects may tunnel through colliders.",
						Apply = c => SetCollisionMode(c, isScene, CollisionDetectionMode.Discrete, continuousBodyPaths),
						Verify = c => CountBodiesNotMode(c.AssetPath, isScene, CollisionDetectionMode.Discrete, continuousBodyPaths) == 0
					},
					new FixOption
					{
						ActionId = "continuous",
						Label = "Continuous",
						ResultDescription = "CCD against static MeshCollider geometry and compatible dynamic CCD interactions.",
						Warning = "Unity's Continuous mode is intended for bodies that need protection against tunnelling through static mesh geometry. It is more expensive than Discrete.",
						Apply = c => SetCollisionMode(c, isScene, CollisionDetectionMode.Continuous, continuousBodyPaths),
						Verify = c => CountBodiesNotMode(c.AssetPath, isScene, CollisionDetectionMode.Continuous, continuousBodyPaths) == 0
					},
					new FixOption
					{
						ActionId = "continuous_dynamic",
						Label = "Continuous Dynamic",
						ResultDescription = "CCD against static and dynamic geometry that also uses continuous collision detection.",
						Warning = "Unity documents this as the slowest collision-detection mode; reserve it for fast bodies that require dynamic-vs-dynamic CCD.",
						Apply = c => SetCollisionMode(c, isScene, CollisionDetectionMode.ContinuousDynamic, continuousBodyPaths),
						Verify = c => CountBodiesNotMode(c.AssetPath, isScene, CollisionDetectionMode.ContinuousDynamic, continuousBodyPaths) == 0
					},
					new FixOption
					{
						ActionId = "continuous_speculative",
						Label = "Continuous Speculative",
						ResultDescription = "Uses speculative CCD and is generally cheaper than the other continuous modes; also works on kinematic bodies.",
						Warning = "Speculative CCD can still allow tunnelling for some high-speed cases, so validate the affected gameplay after the change.",
						Recommended = true,
						Apply = c => SetCollisionMode(c, isScene, CollisionDetectionMode.ContinuousSpeculative, continuousBodyPaths),
						Verify = c => CountBodiesNotMode(c.AssetPath, isScene, CollisionDetectionMode.ContinuousSpeculative, continuousBodyPaths) == 0
					}
				};

					string ccdRecommendation = canSafelyTargetCcdBodies
						? "Review Fix: choose a collision detection mode below"
						: "No safe batch fix until each Rigidbody path is unambiguous";
					var issue = entry.AddIssue("PRE015", continuousBodies.Count + " body/bodies on continuous detection",
						ccdRecommendation, basis: SavingsBasis.Unknown,
						options: canSafelyTargetCcdBodies ? options : null);
					issue?.Note(Preview(continuousBodies), "Bodies");
					if (!canSafelyTargetCcdBodies)
						issue?.Note("The displayed hierarchy paths are missing or ambiguous, so no batch action is offered. Rename duplicate hierarchy objects or rescan so each Rigidbody can be targeted safely.", "Fix unavailable");
				}

			using (RuleTiming.Begin("PAS1012", entry.Path))
				if (!IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.StopNaNCameraState) && stopNaNCameras > 0)
				{
					entry.AddIssue("PAS1012",
						stopNaNCameras + " URP Base camera(s) have Stop NaNs enabled",
						"Review Fix: disable Stop NaNs",
						0,
						fixable ? (Action<FixContext>)(c => SetStopNaN(c, isScene, false)) : null,
						verify: fixable ? (Func<FixContext, bool>)(c => CountStopNaNCameras(c.AssetPath, isScene) == 0) : null)
						?.Note("Stop NaNs is a URP camera safety feature. Disable it when it is not needed on production cameras, especially on performance-sensitive targets.", "Review");
				}

			using (RuleTiming.Begin("PRE012", entry.Path))
				if (isScene && !IsNativeUnavailable(facts, SerializedUnityHierarchy.NativeCapability.LightSemantics) && shadowLights > settings.realtimeLightWarn)
					entry.AddIssue("PRE012", shadowLights + " shadow-casting realtime lights", "at most " + settings.realtimeLightWarn,
						options: new List<FixOption> { new FixOption {
							Label = "Reduce Realtime Shadow Lights",
							Recommended = true,
							Risk = FixRisk.Medium,
							Warning = "This is a Review Fix. Inspect the target state before applying it.",
							Apply = c => GameObjectAsset(c, go => { var lights = go.GetComponentsInChildren<Light>(true); int kept = 0; foreach (var l in lights) if (l.bakingOutput.lightmapBakeType == LightmapBakeType.Realtime && l.shadows != LightShadows.None && ++kept > Mathf.Max(1, OptimizerSettings.Instance.Profile.RealtimeLightWarn)) l.shadows = LightShadows.None; })
						} }
					);
		}

		// --- helpers -------------------------------------------------------------

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

		static AssetEntry NewEntry(string assetPath, AssetCategory category, string typeLabel, BackgroundAssetContext ctx)
		{
			return new AssetEntry
			{
				Guid = ctx != null ? ctx.GetGuid(assetPath) : string.Empty,
				Path = assetPath,
				Name = System.IO.Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = System.IO.Path.GetExtension(assetPath),
				Category = category,
				TypeLabel = typeLabel,
				FileSize = ctx != null ? ctx.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath)
			};
		}

		/// <summary>
		/// Re-reads the saved prefab or scene and counts what PRE001 claimed to have removed. Loaded
		/// fresh from the path rather than from the fix's own GameObject, which is the point: the
		/// object the fix mutated will always look fixed.
		/// </summary>
		static int CountMissingScripts(string assetPath, bool isScene)
		{
			if (isScene)
				return CountInPreviewScene(assetPath, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount);

			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return 0;

			int missing = 0;
			foreach (var transform in root.GetComponentsInChildren<Transform>(true))
				missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
			return missing;
		}

		/// <summary>Re-reads the saved prefab or scene and counts what PRE008 claimed to have cleared.</summary>
		static int CountLooseRaycastTargets(string assetPath, bool isScene)
		{
			if (isScene)
			{
				return CountInPreviewScene(assetPath, go =>
				{
					var graphic = go.GetComponent<Graphic>();
					if (graphic == null || !graphic.raycastTarget) return 0;
					return HandlesPointerInput(go) ? 0 : 1;
				});
			}

			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return 0;

			int loose = 0;
			foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
			{
				if (!graphic.raycastTarget) continue;
				if (HandlesPointerInput(graphic.gameObject)) continue;
				loose++;
			}
			return loose;
		}

		/// <summary>Re-reads the saved prefab or scene and counts animators PRE010 left on Always Animate.</summary>
		static int CountAlwaysAnimate(string assetPath, bool isScene)
		{
			if (isScene)
			{
				return CountInPreviewScene(assetPath, go =>
				{
					var animator = go.GetComponent<Animator>();
					return animator != null && animator.cullingMode == AnimatorCullingMode.AlwaysAnimate ? 1 : 0;
				});
			}

			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return 0;

			int count = 0;
			foreach (var animator in root.GetComponentsInChildren<Animator>(true))
				if (animator.cullingMode == AnimatorCullingMode.AlwaysAnimate) count++;
			return count;
		}

		/// <summary>Re-reads the saved prefab or scene and counts Rigidbody components whose collision mode differs from the requested PRE015 result.</summary>
		static int CountBodiesNotMode(string assetPath, bool isScene, CollisionDetectionMode expected, string[] targetBodyPaths)
		{
			var wanted = new HashSet<string>(targetBodyPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
			if (wanted.Count == 0) return 0;
			if (isScene)
			{
				var seenPaths = new HashSet<string>(StringComparer.Ordinal);
				int mismatched = CountInPreviewScene(assetPath, go =>
				{
					var body = go.GetComponent<Rigidbody>();
					if (body == null) return 0;
					string path = Path(body.transform);
					if (!wanted.Contains(path)) return 0;
					seenPaths.Add(path);
					return body.collisionDetectionMode != expected ? 1 : 0;
				});
				return mismatched + Math.Max(0, wanted.Count - seenPaths.Count);
			}

			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return wanted.Count;

			int seen = 0;
			int count = 0;
			foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
			{
				if (!wanted.Contains(Path(body.transform))) continue;
				seen++;
				if (body.collisionDetectionMode != expected) count++;
			}
			return count + Math.Max(0, wanted.Count - seen);
		}

		internal enum ExpensiveParticleModule
		{
			Collision,
			Lights,
			Trails,
			All
		}

		static int CountParticleModule(string assetPath, bool isScene, ExpensiveParticleModule module)
		{
			int Count(GameObject go)
			{
				if (go == null) return 0;
				int count = 0;
				foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
				{
					if (ps == null) continue;
					bool hit = false;
					switch (module)
					{
						case ExpensiveParticleModule.Collision: hit = ps.collision.enabled; break;
						case ExpensiveParticleModule.Lights: hit = ps.lights.enabled; break;
						case ExpensiveParticleModule.Trails: hit = ps.trails.enabled; break;
						default: hit = ps.collision.enabled || ps.lights.enabled || ps.trails.enabled; break;
					}
					if (hit) count++;
				}
				return count;
			}

			if (isScene) return CountInPreviewScene(assetPath, Count);
			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			return Count(root);
		}

		static void SetParticleModule(FixContext c, bool isScene, ExpensiveParticleModule module, bool enabled)
		{
			FixHierarchy(c, isScene, root =>
			{
				foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
				{
					if (ps == null) continue;
					if (module == ExpensiveParticleModule.Collision || module == ExpensiveParticleModule.All) { var m = ps.collision; m.enabled = module == ExpensiveParticleModule.All ? false : enabled; }
					if (module == ExpensiveParticleModule.Lights || module == ExpensiveParticleModule.All) { var m = ps.lights; m.enabled = module == ExpensiveParticleModule.All ? false : enabled; }
					if (module == ExpensiveParticleModule.Trails || module == ExpensiveParticleModule.All) { var m = ps.trails; m.enabled = module == ExpensiveParticleModule.All ? false : enabled; }
				}
			});
		}

		/// <summary>PRE010's Review Fix: moves every Always Animate animator in the hierarchy to <paramref name="mode"/>.</summary>
		static void SetAlwaysAnimateCulling(FixContext c, bool isScene, AnimatorCullingMode mode)
		{
			FixHierarchy(c, isScene, root =>
			{
				foreach (var animator in root.GetComponentsInChildren<Animator>(true))
					if (animator.cullingMode == AnimatorCullingMode.AlwaysAnimate) animator.cullingMode = mode;
			});
		}

		/// <summary>PRE015's Review Fix mutates only the Rigidbody paths listed in the specific finding.</summary>
		static void SetCollisionMode(FixContext c, bool isScene, CollisionDetectionMode mode, string[] targetBodyPaths)
		{
			var wanted = new HashSet<string>(targetBodyPaths ?? Array.Empty<string>(), StringComparer.Ordinal);
			if (wanted.Count == 0) throw new InvalidOperationException("PRE015 has no verified target Rigidbody paths to change.");
			FixHierarchy(c, isScene, root =>
			{
				var bodies = root.GetComponentsInChildren<Rigidbody>(true);
				var matchedPaths = new HashSet<string>(StringComparer.Ordinal);

				// Preflight every target before mutating any body. A stale result must fail atomically,
				// especially for scenes that remain loaded in memory when a save is rejected.
				foreach (var body in bodies)
				{
					string path = Path(body.transform);
					if (!wanted.Contains(path)) continue;
					matchedPaths.Add(path);
					if (mode != CollisionDetectionMode.Discrete && body.collisionDetectionMode == CollisionDetectionMode.Discrete)
						throw new InvalidOperationException("A PRE015 target Rigidbody became Discrete after the finding was created. Rescan before applying this action.");
				}

				if (matchedPaths.Count != wanted.Count)
					throw new InvalidOperationException("One or more PRE015 target Rigidbody paths no longer exist. Rescan before applying this action.");

				foreach (var body in bodies)
					if (wanted.Contains(Path(body.transform)) && body.collisionDetectionMode != mode)
						body.collisionDetectionMode = mode;
			});
		}

		/// <summary>Runs a mutation against a prefab's or a scene's root objects and saves it back.</summary>
		static void FixHierarchy(FixContext c, bool isScene, Action<GameObject> mutate)
		{
			if (isScene) FixScene(c, mutate);
			else FixPrefab(c, mutate);
		}

		static void FixPrefab(FixContext c, Action<GameObject> mutate)
		{
			var root = PrefabUtility.LoadPrefabContents(c.AssetPath);
			if (root == null)
				throw new InvalidOperationException("The prefab could not be opened for editing.");

			try
			{
				mutate(root);

				// The out-parameter overload, so a save that does not happen is a failure rather than
				// a silent no-op. Without it a fix that changed nothing on disk still reported
				// success, and the row disappeared from the results as though it had been fixed.
				PrefabUtility.SaveAsPrefabAsset(root, c.AssetPath, out bool saved);
				if (!saved)
					throw new InvalidOperationException("Unity could not save the prefab.");
			}
			finally
			{
				PrefabUtility.UnloadPrefabContents(root);
			}
		}

		/// <summary>
		/// Opens the scene for real editing - additively, and only if it is not open already - mutates
		/// every root object, and saves it back. Closed again afterwards only if this call is the one
		/// that opened it, so a scene the user already had open in the Hierarchy is left exactly as
		/// they had it, just with the fix applied and saved.
		/// </summary>
		static void FixScene(FixContext c, Action<GameObject> mutate)
		{
			UnityEngine.SceneManagement.Scene scene = default;
			for (int i = 0; i < EditorSceneManager.sceneCount; i++)
			{
				var open = EditorSceneManager.GetSceneAt(i);
				if (open.path == c.AssetPath) { scene = open; break; }
			}

			bool openedByFix = !scene.IsValid();
			if (openedByFix)
				scene = EditorSceneManager.OpenScene(c.AssetPath, OpenSceneMode.Additive);

			if (!scene.IsValid())
				throw new InvalidOperationException("The scene could not be opened for editing.");

			try
			{
				foreach (var root in scene.GetRootGameObjects())
					mutate(root);

				EditorSceneManager.MarkSceneDirty(scene);
				if (!EditorSceneManager.SaveScene(scene))
					throw new InvalidOperationException("Unity could not save the scene.");
			}
			finally
			{
				if (openedByFix) EditorSceneManager.CloseScene(scene, true);
			}
		}

		/// <summary>
		/// Counts something across a scene's hierarchy without disturbing the Hierarchy window - used
		/// only to verify a fix afterwards, which is why a read-only preview scene is enough here even
		/// though the fix itself needs the real one to save through.
		/// </summary>
		static int CountInPreviewScene(string assetPath, Func<GameObject, int> perObject)
		{
			UnityEngine.SceneManagement.Scene scene = default;
			bool opened = false;
			try
			{
				scene = EditorSceneManager.OpenPreviewScene(assetPath);
				opened = scene.IsValid();
				if (!opened)
					throw new InvalidOperationException("The scene could not be opened for verification.");

				int total = 0;
				foreach (var root in scene.GetRootGameObjects())
					foreach (var transform in root.GetComponentsInChildren<Transform>(true))
						total += perObject(transform.gameObject);
				return total;
			}
			finally
			{
				if (opened) EditorSceneManager.ClosePreviewScene(scene);
			}
		}

		/// <summary>
		/// True when this object, or anything above it, consumes pointer input. Checking only for
		/// Selectable would miss custom scripts implementing IPointerClickHandler, IDragHandler and
		/// friends, and clearing Raycast Target on those would silently kill their input.
		/// </summary>
		static bool HandlesPointerInput(GameObject go)
		{
			var current = go.transform;
			while (current != null)
			{
				foreach (var component in current.GetComponents<Component>())
				{
					if (component is UnityEngine.EventSystems.IEventSystemHandler) return true;
				}
				current = current.parent;
			}
			return false;
		}

		static int Depth(Transform transform)
		{
			int depth = 0;
			var current = transform;
			while (current.parent != null)
			{
				depth++;
				current = current.parent;
			}
			return depth;
		}

		static string Path(Transform transform)
		{
			var name = transform.name;
			var current = transform.parent;
			while (current != null)
			{
				name = current.name + "/" + name;
				current = current.parent;
			}
			return name;
		}

		static int SubMeshCount(Renderer renderer, MeshFilter knownFilter = null)
		{
			if (renderer is SkinnedMeshRenderer skinned)
				return skinned.sharedMesh != null ? skinned.sharedMesh.subMeshCount : 0;

			var filter = knownFilter ?? renderer.GetComponent<MeshFilter>();
			return filter != null && filter.sharedMesh != null ? filter.sharedMesh.subMeshCount : 0;
		}

		static readonly Dictionary<Type, bool> s_ComponentObjectReferenceFields = new Dictionary<Type, bool>();

		static bool ComponentTypeHasObjectReferenceFields(Component component)
		{
			if (component == null) return false;
			var type = component.GetType();
			if (s_ComponentObjectReferenceFields.TryGetValue(type, out var cached)) return cached;

			bool found = false;
			try
			{
				var so = new SerializedObject(component);
				var property = so.GetIterator();
				while (property.Next(true))
				{
					if (property.propertyType == SerializedPropertyType.ObjectReference)
					{
						found = true;
						break;
					}
				}
			}
			catch
			{
				// Unknown types must be scanned rather than assumed reference-free.
				found = true;
			}

			s_ComponentObjectReferenceFields[type] = found;
			return found;
		}

		static void CollectMissingReferences(Component component, string objectPath, List<string> into)
		{
			if (component == null || !ComponentTypeHasObjectReferenceFields(component)) return;

			var so = new SerializedObject(component);
			var property = so.GetIterator();

			// Next() visits hidden serialized fields as well as visible Inspector fields, which is
			// required for complete dangling-reference detection.
			while (property.Next(true))
			{
				if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

				// A non-zero instance ID with a null resolved object is Unity's missing-object reference
				// state. A zero ID is an intentional/unassigned null and is not reported.
				if (property.objectReferenceInstanceIDValue == 0) continue;
				if (property.objectReferenceValue == null)
				{
					string member = string.IsNullOrEmpty(property.propertyPath)
						? "<object reference>"
						: property.propertyPath;
					into.Add((objectPath ?? "<object>") + " (" + component.GetType().Name + "." + member + ")");
				}
			}
		}

		static string FindMissingPrefabRoot(Transform transform)
		{
			if (transform == null || !PrefabUtility.IsPrefabAssetMissing(transform.gameObject))
				return null;

			var root = transform;
			while (root.parent != null && PrefabUtility.IsPrefabAssetMissing(root.parent.gameObject))
				root = root.parent;

			return Path(root);
		}

		static int MeshVertexCount(Renderer renderer)
		{
			if (renderer is SkinnedMeshRenderer skinned)
				return skinned.sharedMesh != null ? skinned.sharedMesh.vertexCount : 0;

			var filter = renderer.GetComponent<MeshFilter>();
			return filter != null && filter.sharedMesh != null ? filter.sharedMesh.vertexCount : 0;
		}

		static int CountStopNaNCameras(string assetPath, bool isScene)
		{
			if (isScene)
				return CountInPreviewScene(assetPath, go => IsUrpStopNaNEnabled(go) ? 1 : 0);

			var root = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (root == null) return 0;

			int count = 0;
			foreach (var camera in root.GetComponentsInChildren<Camera>(true))
				if (IsUrpStopNaNEnabled(camera.gameObject)) count++;
			return count;
		}

		static void SetStopNaN(FixContext c, bool isScene, bool enabled)
		{
			FixHierarchy(c, isScene, root =>
			{
				if (!s_ResolvedUrpCameraData)
					IsUrpStopNaNEnabled(root);

				if (s_UrpAdditionalCameraDataType == null || s_UrpStopNaNProperty == null)
					return;

				foreach (var camera in root.GetComponentsInChildren<Camera>(true))
				{
					var component = camera.gameObject.GetComponent(s_UrpAdditionalCameraDataType);
					if (component == null) continue;

					try
					{
						var renderType = s_UrpRenderTypeProperty?.GetValue(component, null);
						if (renderType == null || !string.Equals(renderType.ToString(), "Base", StringComparison.Ordinal))
							continue;
						s_UrpStopNaNProperty.SetValue(component, enabled, null);
					}
					catch
					{
						// Package/version mismatches should not make an unrelated hierarchy fix fail.
					}
				}
			});
		}

		static string Preview(List<string> items)
		{
			int shown = Mathf.Min(items.Count, 8);
			var lines = new List<string>();
			for (int i = 0; i < shown; i++) lines.Add("  - " + items[i]);
			if (items.Count > shown) lines.Add("  - and " + (items.Count - shown) + " more");
			return string.Join("\n", lines);
		}
		static void TrimSurplusMaterialSlots(FixContext c)
		{
			var root = c.Asset as GameObject;
			if (root == null) throw new InvalidOperationException("Prefab/scene root is unavailable for PRE004 fix.");
			var renderers = root.GetComponentsInChildren<Renderer>(true);
			for (int i = 0; i < renderers.Length; i++)
			{
				var r = renderers[i];
				var mf = r.GetComponent<MeshFilter>();
				if (mf == null || mf.sharedMesh == null) continue;
				int sub = mf.sharedMesh.subMeshCount; var mats = r.sharedMaterials;
				if (mats.Length > sub) { var trimmed = new Material[sub]; Array.Copy(mats, trimmed, sub); r.sharedMaterials = trimmed; }
			}
			c.NeedsSave = true;
		}

		static bool VerifyNoSurplusMaterialSlots(FixContext c)
		{
			var root = AssetDatabase.LoadAssetAtPath<GameObject>(c.AssetPath);
			if (root == null) return false;
			var renderers = root.GetComponentsInChildren<Renderer>(true);
			for (int i = 0; i < renderers.Length; i++) { var mf = renderers[i].GetComponent<MeshFilter>(); if (mf != null && mf.sharedMesh != null && renderers[i].sharedMaterials.Length > mf.sharedMesh.subMeshCount) return false; }
			return true;
		}

		static void GameObjectAsset(FixContext c, Action<GameObject> mutate)
		{
			c.EnsureWritable();
			var go = c.Asset as GameObject;
			if (go == null) throw new InvalidOperationException("Not a GameObject asset");
			mutate(go);
			EditorUtility.SetDirty(go);
			c.NeedsSave = true;
		}
	}
}
