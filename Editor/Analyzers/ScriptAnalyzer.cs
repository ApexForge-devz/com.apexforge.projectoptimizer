using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Conservative static checks over runtime C# source.
	///
	/// This analyzer intentionally does not rewrite source. Text inspection can establish that a
	/// particular API appears in a Unity callback, but it cannot prove execution frequency, object
	/// count, lifetime, allocation behaviour of user code, or whether a seemingly obvious replacement
	/// preserves semantics. Findings therefore remain advisory; the analyzer focuses on high-signal
	/// patterns and provides exact source locations for human review.
	/// </summary>
	public sealed class ScriptAnalyzer : IBackgroundAssetAnalyzer
	{
		// Bumped when ScriptFacts gains/changes measurements. This local version lets the analyzer
		// refresh stale rows even when the host project's global cache schema has not yet been bumped.
		const int k_FactsSchema = 2;

		// Unity message callbacks that Unity invokes on enabled MonoBehaviours.
		static readonly string[] k_PerFrameMethods = { "Update", "LateUpdate", "FixedUpdate" };

		// Scene/global search APIs. Unity 6 includes the Find*ByType family; the legacy APIs remain
		// relevant in existing projects. FindObjectsByType can additionally sort its result, which can
		// make it more expensive than an unsorted search when used frequently.
		static readonly Regex k_FindCalls = new Regex(
			@"\b(?:GameObject\.(?:Find|FindWithTag|FindGameObjectsWithTag)|" +
			@"Object\.(?:FindObjectOfType|FindObjectsOfType|FindFirstObjectByType|FindAnyObjectByType|FindObjectsByType)|" +
			@"(?:FindObjectOfType|FindObjectsOfType))\s*[(<]",
			RegexOptions.Compiled);

		// Keep GetComponent-family detection broad enough to cover generic syntax while not matching
		// similarly named user methods such as "GetComponentsCached".
		static readonly Regex k_GetComponent = new Regex(
			@"\bGetComponent(?:s(?:InChildren|InParent)?|InChildren|InParent)?\s*[(<]",
			RegexOptions.Compiled);

		// These are the LINQ operators most likely to enumerate or create iterators/result sequences.
		// The rule is intentionally heuristic: whether a specific expression allocates depends on the
		// source type, iterator implementation, provider, captures, and surrounding code.
		static readonly Regex k_Linq = new Regex(
			@"\.(?:Where|Select|SelectMany|OrderBy|OrderByDescending|ThenBy|ThenByDescending|" +
			@"Skip|SkipWhile|Take|TakeWhile|Concat|Distinct|Except|Intersect|Union|" +
			@"GroupBy|Join|GroupJoin|Reverse|ToList|ToArray|ToDictionary|ToHashSet|" +
			@"First|FirstOrDefault|Single|SingleOrDefault|Last|LastOrDefault|Any|All|Contains|" +
			@"Sum|Average|Min|Max|Count|LongCount)\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_DebugLog = new Regex(
			@"\bDebug\.(?:Log|LogWarning|LogError|LogException|LogFormat)\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_SendMessage = new Regex(
			@"\b(?:SendMessage|BroadcastMessage|SendMessageUpwards)\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_ResourcesLoad = new Regex(
			@"\bResources\.(?:Load|LoadAsync|LoadAll)\s*[(<]",
			RegexOptions.Compiled);

		static readonly Regex k_InstantiateDestroy = new Regex(
			@"\b(?:Object\.)?(?:Instantiate|Destroy|DestroyImmediate)\s*[(<]",
			RegexOptions.Compiled);

		static readonly Regex k_CameraMain = new Regex(
			@"\bCamera\.main\b",
			RegexOptions.Compiled);

		static readonly Regex k_TransformFind = new Regex(
			@"\btransform\.Find\s*\(",
			RegexOptions.Compiled);

		// Only match result-array/query APIs known to allocate. Single-hit Raycast/SphereCast/etc. overloads
		// are intentionally excluded because their return values do not imply managed result-array allocation.
		static readonly Regex k_AllocatingPhysicsQuery = new Regex(
			@"\b(?:Physics\.(?:RaycastAll|SphereCastAll|CapsuleCastAll|BoxCastAll|LinecastAll|OverlapBox|OverlapSphere|OverlapCapsule)|" +
			@"Physics2D\.(?:RaycastAll|CircleCastAll|BoxCastAll|CapsuleCastAll|LinecastAll|OverlapCircle|OverlapBox|OverlapCapsule|GetRayIntersectionAll))\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_NewAllocation = new Regex(
			@"\bnew\s+(?!(?:WaitForSeconds|WaitForSecondsRealtime|WaitUntil|WaitWhile)\b)[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^;{}()]+>)?(?:\s*\[[^\]]*\])?",
			RegexOptions.Compiled);

		static readonly Regex k_Foreach = new Regex(
			@"\bforeach\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_StartCoroutine = new Regex(
			@"\bStartCoroutine(?:<[^>]+>)?\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_WaitAllocation = new Regex(
			@"\bnew\s+(?:WaitForSeconds|WaitForSecondsRealtime|WaitUntil|WaitWhile)\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_StringFormatting = new Regex(
			@"\b(?:string|String)\.(?:Format|Concat|Join)\s*\(|\bAppendFormat\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_InterpolatedString = new Regex(
			"\\${1,}(?:@?\"|\"{3,})|@\\${1,}\"",
			RegexOptions.Compiled);

		static readonly Regex k_HashInHotPath = new Regex(
			@"\b(?:Animator\.StringToHash|Shader\.PropertyToID)\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_PhysicsSync = new Regex(
			@"\bPhysics(?:2D)?\.SyncTransforms\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_RendererMaterialAccess = new Regex(
			@"\b(?:Renderer|renderer|SkinnedMeshRenderer|skinnedMeshRenderer)\.(?:material|materials|sharedMaterials)\b",
			RegexOptions.Compiled);

		static readonly Regex k_RendererTypeDeclaration = new Regex(
			@"\b(?:Renderer|SkinnedMeshRenderer)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
			RegexOptions.Compiled);

		static readonly Regex k_RendererComponentAccess = new Regex(
			@"\bGetComponent<(?:Renderer|SkinnedMeshRenderer)>\s*\(\s*\)\s*\.(?:material|materials|sharedMaterials)\b",
			RegexOptions.Compiled);

		static readonly Regex k_AnimatorParameters = new Regex(
			@"\b(?:Animator|animator)\.parameters\b",
			RegexOptions.Compiled);

		static readonly Regex k_InputTouches = new Regex(
			@"\bInput\.touches\b",
			RegexOptions.Compiled);

		static readonly Regex k_ToString = new Regex(
			@"\.ToString\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_Lambda = new Regex(
			@"=>",
			RegexOptions.Compiled);

		static readonly Regex k_GcCollect = new Regex(
			@"\bGC\.Collect\s*\(",
			RegexOptions.Compiled);

		static readonly Regex k_OnGui = new Regex(
			@"\b(?:public|private|protected|internal|static|virtual|override|new|sealed|\s)*" +
			@"void\s+OnGUI\s*\(\s*\)\s*(?:\{|=>)",
			RegexOptions.Compiled);

		public AssetCategory Category => AssetCategory.Script;

		public AssetKindMask SupportedKinds => AssetKindMask.Script;

		static ScriptAnalyzer()
		{
			Rules.Register(id: "SCR001", title: "Empty per-frame method", category: AssetCategory.Script,
							why: "An enabled MonoBehaviour that declares Update, LateUpdate or FixedUpdate participates in Unity's message dispatch even when the method body is empty. Removing an empty callback avoids that recurring dispatch overhead.",
							fix: "Delete the empty callback. Do not remove a non-empty callback merely because it currently appears inactive.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR002", title: "Expensive scene search inside a per-frame method", category: AssetCategory.Script,
							why: "Unity's scene-search APIs inspect loaded objects rather than using a direct field reference. In a per-frame callback, repeating a global search can scale poorly with scene size and invocation count.",
							fix: "Resolve the reference at a suitable lifecycle point, cache it, or otherwise use an explicit dependency instead of searching globally every frame.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR003", title: "GetComponent inside a per-frame method", category: AssetCategory.Script,
							why: "GetComponent-family calls perform a component lookup each time they execute. The cost of one lookup is often small, but repeated execution across many instances can become measurable.",
							fix: "Cache stable component references during initialization when profiling shows the lookup is worth removing.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR004", title: "LINQ inside a per-frame method", category: AssetCategory.Script,
							why: "LINQ introduces iterator/query machinery and, for many operators and expression shapes, allocations or additional enumeration work. The exact cost depends on the source collection, operator chain, captures, and execution path.",
							fix: "Profile the expression. Where it is hot, replace the query with an allocation-free loop and reuse destination collections as appropriate.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR005", title: "Debug logging in runtime code", category: AssetCategory.Script,
							why: "Debug logging has runtime overhead and can become significant when called frequently. The analyzer cannot determine whether a log is startup-only, conditional, or in a hot path from source text alone.",
							fix: "Remove or gate diagnostics for production builds, or route them through a logging abstraction with an explicit runtime policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR006", title: "OnGUI implemented in runtime code", category: AssetCategory.Script,
							why: "OnGUI uses Unity's immediate-mode GUI system and can be invoked multiple times during a frame. Whether that is appropriate depends on the project and whether the callback is editor-only or intentionally uses IMGUI.",
							fix: "Keep OnGUI only when its IMGUI use is intentional. Otherwise move shipping UI to the project's chosen retained-mode UI solution and keep editor tooling separate.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR007", title: "SendMessage used instead of a direct call", category: AssetCategory.Script,
							why: "SendMessage-family APIs perform runtime message lookup and are less explicit than a direct reference or interface call. The analyzer cannot establish whether the flexibility is intentional.",
							fix: "Prefer explicit references, interfaces, events, or another typed dispatch mechanism when the target relationship is known.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR008", title: "Instantiate or Destroy inside a per-frame method", category: AssetCategory.Script,
							why: "Creating and destroying objects from a per-frame callback can cause allocation, destruction, initialization, and renderer/physics bookkeeping work. Whether pooling is appropriate depends on lifetime and usage patterns.",
							fix: "Profile the path. For repeatedly spawned short-lived objects, consider pooling rather than creating and destroying them every frame.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR009", title: "Resources.Load in runtime code", category: AssetCategory.Script,
							why: "Resources.Load is an explicit dependency on Unity's Resources system. Assets in Resources are made independently loadable and can contribute to the build's resources data; widespread use can complicate memory, build-size, and asset-management control.",
							fix: "For production content, prefer explicit references or the project's asset-management system such as Addressables where appropriate. If Resources is intentional, keep loads out of hot paths and manage lifetime explicitly.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.None,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			RegisterHotPathRule("SCR010", "Camera.main in a per-frame method",
				"Camera.main performs a camera lookup each time it is queried. Repeating it from a per-frame callback can become unnecessary work across many behaviours, although the absolute cost is workload-dependent.",
				"Cache the camera reference during initialization when profiling shows repeated lookup cost.", IssueSeverity.Info);

			RegisterHotPathRule("SCR011", "Transform.Find in a per-frame method",
				"Transform.Find traverses a hierarchy by name/path. Repeating hierarchy searches every frame can scale poorly and hides a dependency that can usually be represented directly.",
				"Cache the Transform reference or serialize the dependency instead of searching the hierarchy repeatedly.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR012", "Allocating physics query in a per-frame method",
				"These Physics and Physics2D query forms return result arrays and Unity documents allocation for the allocating variants. Static inspection still cannot prove that the call is a meaningful runtime bottleneck.",
				"Prefer a corresponding non-allocating query or a reusable result buffer where the API contract permits it; verify buffer sizing and actual profiler impact.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR013", "Allocation candidate in a per-frame method",
				"A new-expression inside Update/LateUpdate/FixedUpdate may allocate managed objects or arrays. Value-type construction does not necessarily create a heap allocation, so source inspection alone cannot prove the runtime allocation cost.",
				"Profile the specific expression first; reuse managed objects or buffers only where allocation is demonstrated and the lifetime permits.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR014", "foreach loop in a per-frame method",
				"foreach is usually cheap over arrays and many concrete collections, but enumeration can introduce interface/iterator overhead in some cases. This is a diagnostic rather than a blanket claim that foreach is slow.",
				"Only replace the loop after profiling; for hot allocation-sensitive paths, use an allocation-free iteration form appropriate to the collection.", IssueSeverity.Info);

			RegisterHotPathRule("SCR015", "StartCoroutine in a per-frame method",
				"Starting a coroutine every frame creates coroutine state and scheduling work. Repeated starts can accumulate many concurrent routines or allocations when not intentionally bounded.",
				"Ensure coroutine creation is event-driven or explicitly bounded; reuse a persistent routine when appropriate.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR016", "Wait instruction allocated in a per-frame method",
				"Creating WaitForSeconds, WaitForSecondsRealtime, WaitUntil or WaitWhile instances creates coroutine yield objects. Repeated creation on hot paths can produce avoidable managed allocations.",
				"Cache reusable wait instructions where their timing/predicate semantics allow it, or restructure the routine around explicit timers.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR017", "String formatting in a per-frame method",
				"String interpolation and string.Format create formatted strings and can allocate managed memory. The cost is entirely dependent on execution frequency and whether the result is needed.",
				"Avoid formatting strings in hot paths unless the result is consumed; gate diagnostics and move UI/telemetry updates to a lower cadence where practical.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR018", "ToString in a per-frame method",
				"ToString commonly creates a new managed string. In frequently executing code this can add GC pressure even when the value itself is inexpensive to compute.",
				"Keep formatting out of hot paths unless required, especially for telemetry or UI that can update at a lower cadence.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR019", "Lambda/delegate expression in a per-frame method",
				"Lambda expressions can allocate delegate or closure objects depending on capture and conversion context. Source text alone cannot determine whether this occurrence allocates.",
				"Profile the specific expression. Avoid per-frame captured closures and reuse delegates when allocation is demonstrated.", IssueSeverity.Info);

			RegisterHotPathRule("SCR020", "Runtime hash/ID generation in a per-frame method",
				"Animator.StringToHash and Shader.PropertyToID are useful for stable identifiers, but recalculating a stable identifier in a hot path performs avoidable work.",
				"Precompute stable hashes/IDs during initialization or static setup. Keep dynamic identifiers dynamic when they legitimately vary at runtime.", IssueSeverity.Info);

			RegisterHotPathRule("SCR021", "Physics.SyncTransforms in a per-frame method",
				"Explicit physics transform synchronization can force physics to consume transform changes earlier than the normal synchronization point. Repeating it every frame can add avoidable physics work.",
				"Remove redundant synchronization where the default simulation order is sufficient. Retain it only when immediate physics visibility is required and profile the cost.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR022", "Renderer material or material-array access in a per-frame method",
				"Renderer.material can instantiate a material, while materials/sharedMaterials return material arrays and array access can create avoidable temporary work. Repeating these accesses in a hot callback can amplify CPU and managed-memory cost.",
				"Cache stable renderer state, use GetMaterials/GetSharedMaterials with reusable lists where appropriate, or use MaterialPropertyBlock for per-renderer properties. Do not replace material ownership semantics blindly.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR023", "Animator.parameters array access in a per-frame method",
				"Animator.parameters exposes an array-valued representation of the animator's parameters and is documented as an allocating API. Repeated access from a per-frame callback can therefore create avoidable managed work.",
				"Use parameterCount/GetParameter or cache the required parameter metadata outside the hot path.", IssueSeverity.Warning);

			RegisterHotPathRule("SCR024", "Input.touches array access in a per-frame method",
				"The legacy Input.touches property returns an array snapshot and can allocate temporary data. Repeated access in a per-frame callback can create recurring garbage and CPU work.",
				"Prefer Input.touchCount with Input.GetTouch for allocation-free iteration, or read Input.touches once when an array snapshot is actually required.", IssueSeverity.Warning);
			Rules.Register(id: "SCR025", title: "Explicit GC.Collect in a per-frame method", category: AssetCategory.Script,
				why: "Forcing a garbage collection from Update, LateUpdate or FixedUpdate can introduce frame-time spikes. Source inspection cannot establish how often the call executes, so the finding remains advisory.",
				fix: "Remove or move the explicit collection unless a measured, controlled collection point is intentionally required.",
				severity: IssueSeverity.Warning, fixKind: FixKind.None, confidence: OptimizationConfidence.HighConfidence, risk: FixRisk.Unspecified, backupRequired: false);

		}

		static void RegisterHotPathRule(string id, string title, string why, string fix, IssueSeverity severity)
		{
			Rules.Register(id: id, title: title, category: AssetCategory.Script, why: why, fix: fix,
				severity: severity, fixKind: FixKind.None, confidence: OptimizationConfidence.Heuristic,
				risk: FixRisk.Unspecified, backupRequired: false);
		}

		static void ReportHotPath(AssetEntry entry, string ruleId, List<string> matches, string recommended, int line)
		{
			if (matches == null || matches.Count == 0) return;
			entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended)?.Items("Called at", matches).AtLine(line);
		}

		public void BeginScan(ScanContext ctx) { }

		public bool Handles(string assetPath)
		{
			return assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
		}

		[Serializable]
		public sealed class ScriptFacts
		{
			public int factsSchema;
			public int lineCount;

			// True only when Unity's imported MonoScript resolves to a MonoBehaviour type. This prevents
			// a plain C# service/utility class containing a method named "Update" from being treated as
			// a Unity per-frame callback.
			public bool isMonoBehaviour;

			public List<string> emptyPerFrame = new List<string>();
			public int emptyPerFrameLine = -1;

			public List<string> sceneSearches = new List<string>();
			public int sceneSearchLine = -1;

			public List<string> getComponents = new List<string>();
			public int getComponentLine = -1;

			public List<string> linq = new List<string>();
			public int linqLine = -1;

			public List<string> instantiateDestroy = new List<string>();
			public int instantiateDestroyLine = -1;

			public List<string> cameraMain = new List<string>();
			public int cameraMainLine = -1;
			public List<string> transformFind = new List<string>();
			public int transformFindLine = -1;
			public List<string> physicsQueries = new List<string>();
			public int physicsQueriesLine = -1;
			public List<string> newAllocations = new List<string>();
			public int newAllocationLine = -1;
			public List<string> foreachLoops = new List<string>();
			public int foreachLine = -1;
			public List<string> startCoroutines = new List<string>();
			public int startCoroutineLine = -1;
			public List<string> waitAllocations = new List<string>();
			public int waitAllocationLine = -1;
			public List<string> stringFormatting = new List<string>();
			public int stringFormattingLine = -1;
			public List<string> toStrings = new List<string>();
			public int toStringLine = -1;
			public List<string> lambdas = new List<string>();
			public int lambdaLine = -1;

			public List<string> runtimeHashes = new List<string>();
			public int runtimeHashLine = -1;

			public List<string> physicsSyncs = new List<string>();
			public int physicsSyncLine = -1;

			public List<string> rendererMaterialAccesses = new List<string>();
			public int rendererMaterialAccessLine = -1;

			public List<string> animatorParameters = new List<string>();
			public int animatorParametersLine = -1;

			public List<string> inputTouches = new List<string>();
			public int inputTouchesLine = -1;

			public List<string> gcCollects = new List<string>();
			public int gcCollectLine = -1;

			public int debugLogs;
			public bool hasOnGui;
			public int sendMessages;
			public int resourcesLoads;
		}


		public AssetEntry Analyze(string assetPath, ScanContext ctx)
		{
			if (AssetPathUtility.IsInEditorFolder(assetPath)) return null;
			var facts = ctx.Cache != null ? ctx.GetCached(assetPath, () => Measure(assetPath)) : Measure(assetPath);
			if (facts == null) return null;
			if (facts.factsSchema != k_FactsSchema)
			{
				facts = Measure(assetPath);
				if (facts == null) return null;
				if (ctx.Cache != null)
				{
					string dependencyHash = ctx.GetDependencyHash(assetPath);
					ctx.Cache.Set(ScanCache.ScopedKey(assetPath, typeof(ScriptFacts).FullName), facts, dependencyHash);
				}
			}
			long fileSize = ctx.FileMetadata != null ? ctx.FileMetadata.GetFileSize(assetPath) : FileUtility.FileSizeOf(assetPath);
			return BuildEntry(assetPath, ctx.GetGuid(assetPath), fileSize, facts);
		}

		public void PrepareBackground(string assetPath, ScanContext mainContext, BackgroundAssetContext backgroundContext)
		{
			if (AssetPathUtility.IsInEditorFolder(assetPath)) return;
			if (mainContext.Cache != null)
			{
				string hash = backgroundContext.GetDependencyHash(assetPath);
				if (!string.IsNullOrEmpty(hash) && mainContext.Cache.TryGet(ScanCache.ScopedKey(assetPath, typeof(ScriptFacts).FullName), hash, out ScriptFacts cached))
				{
					backgroundContext.SetPreflightFlag(assetPath, cached.isMonoBehaviour);
					backgroundContext.Seed(assetPath, cached);
					return;
				}
			}
			var script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
			var type = script != null ? script.GetClass() : null;
			backgroundContext.SetPreflightFlag(assetPath, type != null && typeof(MonoBehaviour).IsAssignableFrom(type));
		}

		public AssetEntry AnalyzeBackground(string assetPath, BackgroundAssetContext context)
		{
			if (AssetPathUtility.IsInEditorFolder(assetPath)) return null;
			var facts = context.GetOrMeasure(assetPath, () => Measure(assetPath, context.GetPreflightFlag(assetPath), context.ProjectRoot));
			if (facts == null) return null;
			if (facts.factsSchema != k_FactsSchema) facts = Measure(assetPath, context.GetPreflightFlag(assetPath), context.ProjectRoot);
			return facts == null ? null : BuildEntry(assetPath, context.GetGuid(assetPath), context.GetFileSize(assetPath), facts);
		}

		static AssetEntry BuildEntry(string assetPath, string guid, long fileSize, ScriptFacts facts)
		{
			var entry = new AssetEntry
			{
				Guid = guid ?? string.Empty,
				Path = assetPath,
				Name = Path.GetFileName(assetPath),
				Directory = AssetPathUtility.Directoryish(assetPath),
				Extension = ".cs",
				Category = AssetCategory.Script,
				TypeLabel = "Script",
				FileSize = fileSize
			};

			entry.Set("lines", (long)facts.lineCount);
			entry.Set("perFrameIssues", (long)(
				facts.sceneSearches.Count + facts.getComponents.Count + facts.linq.Count + facts.instantiateDestroy.Count +
				facts.cameraMain.Count + facts.transformFind.Count + facts.physicsQueries.Count + facts.newAllocations.Count +
				facts.foreachLoops.Count + facts.startCoroutines.Count + facts.waitAllocations.Count + facts.stringFormatting.Count +
				facts.toStrings.Count + facts.lambdas.Count + facts.runtimeHashes.Count + facts.physicsSyncs.Count +
				facts.rendererMaterialAccesses.Count + facts.animatorParameters.Count + facts.inputTouches.Count + facts.gcCollects.Count));
			entry.Set("logs", (long)facts.debugLogs);

			if (facts.isMonoBehaviour)
			{
				using (RuleTiming.Begin("SCR001", entry.Path))
					if (facts.emptyPerFrame.Count > 0)
					{
						entry.AddIssue(
								"SCR001",
								string.Join(", ", facts.emptyPerFrame) + " empty",
								"removed")
							?.Items("Methods", facts.emptyPerFrame)
							.AtLine(facts.emptyPerFrameLine);

						using (RuleTiming.Begin("SCR002", entry.Path))
							if (facts.sceneSearches.Count > 0)
							{
								entry.AddIssue(
										"SCR002",
										facts.sceneSearches.Count + " call(s) in per-frame callbacks",
										"cached or explicitly referenced")
									?.Items("Called at", facts.sceneSearches)
									.AtLine(facts.sceneSearchLine);
							}

						using (RuleTiming.Begin("SCR003", entry.Path))
							if (facts.getComponents.Count > 0)
							{
								entry.AddIssue(
										"SCR003",
										facts.getComponents.Count + " call(s) in per-frame callbacks",
										"cached when appropriate")
									?.Items("Called at", facts.getComponents)
									.AtLine(facts.getComponentLine);
							}

						using (RuleTiming.Begin("SCR004", entry.Path))
							if (facts.linq.Count > 0)
							{
								entry.AddIssue(
										"SCR004",
										facts.linq.Count + " LINQ call(s) in per-frame callbacks",
										"profile / replace when hot")
									?.Items("Used at", facts.linq)
									.AtLine(facts.linqLine);
							}

						using (RuleTiming.Begin("SCR008", entry.Path))
							if (facts.instantiateDestroy.Count > 0)
							{
								entry.AddIssue(
										"SCR008",
										facts.instantiateDestroy.Count + " instantiate/destroy call(s) in per-frame callbacks",
										"profile / pool repeated lifetimes")
									?.Items("Called at", facts.instantiateDestroy)
									.AtLine(facts.instantiateDestroyLine);
							}
					}
					else
					{
						// Still report non-empty per-frame issues even when there is no SCR001 finding.
						using (RuleTiming.Begin("SCR002", entry.Path))
							if (facts.sceneSearches.Count > 0)
							{
								entry.AddIssue(
										"SCR002",
										facts.sceneSearches.Count + " call(s) in per-frame callbacks",
										"cached or explicitly referenced")
									?.Items("Called at", facts.sceneSearches)
									.AtLine(facts.sceneSearchLine);
							}

						using (RuleTiming.Begin("SCR003", entry.Path))
							if (facts.getComponents.Count > 0)
							{
								entry.AddIssue(
										"SCR003",
										facts.getComponents.Count + " call(s) in per-frame callbacks",
										"cached when appropriate")
									?.Items("Called at", facts.getComponents)
									.AtLine(facts.getComponentLine);
							}

						using (RuleTiming.Begin("SCR004", entry.Path))
							if (facts.linq.Count > 0)
							{
								entry.AddIssue(
										"SCR004",
										facts.linq.Count + " LINQ call(s) in per-frame callbacks",
										"profile / replace when hot")
									?.Items("Used at", facts.linq)
									.AtLine(facts.linqLine);
							}

						using (RuleTiming.Begin("SCR008", entry.Path))
							if (facts.instantiateDestroy.Count > 0)
							{
								entry.AddIssue(
										"SCR008",
										facts.instantiateDestroy.Count + " instantiate/destroy call(s) in per-frame callbacks",
										"profile / pool repeated lifetimes")
									?.Items("Called at", facts.instantiateDestroy)
									.AtLine(facts.instantiateDestroyLine);
							}
					}
			}

			using (RuleTiming.Begin("SCR006", entry.Path))
				if (facts.hasOnGui)
					entry.AddIssue("SCR006", "OnGUI implemented", "keep intentionally or migrate shipping UI");

			using (RuleTiming.Begin("SCR005", entry.Path))
				if (facts.debugLogs > 0)
					entry.AddIssue("SCR005", facts.debugLogs + " logging call(s)", "gate/remove production diagnostics");

			using (RuleTiming.Begin("SCR007", entry.Path))
				if (facts.sendMessages > 0)
					entry.AddIssue("SCR007", facts.sendMessages + " SendMessage-family call(s)", "explicit typed dispatch");

			using (RuleTiming.Begin("SCR009", entry.Path))
				if (facts.resourcesLoads > 0)
					entry.AddIssue("SCR009", facts.resourcesLoads + " Resources load call(s)", "explicit references or managed asset loading");

			using (RuleTiming.Begin("SCR010", entry.Path)) ReportHotPath(entry, "SCR010", facts.cameraMain, "cache camera reference", facts.cameraMainLine);
			using (RuleTiming.Begin("SCR011", entry.Path)) ReportHotPath(entry, "SCR011", facts.transformFind, "cache hierarchy reference", facts.transformFindLine);
			using (RuleTiming.Begin("SCR012", entry.Path)) ReportHotPath(entry, "SCR012", facts.physicsQueries, "profile and use reusable result buffers", facts.physicsQueriesLine);
			using (RuleTiming.Begin("SCR013", entry.Path)) ReportHotPath(entry, "SCR013", facts.newAllocations, "profile and reuse allocations when hot", facts.newAllocationLine);
			using (RuleTiming.Begin("SCR014", entry.Path)) ReportHotPath(entry, "SCR014", facts.foreachLoops, "profile before changing loop form", facts.foreachLine);
			using (RuleTiming.Begin("SCR015", entry.Path)) ReportHotPath(entry, "SCR015", facts.startCoroutines, "bound coroutine creation", facts.startCoroutineLine);
			using (RuleTiming.Begin("SCR016", entry.Path)) ReportHotPath(entry, "SCR016", facts.waitAllocations, "reuse yield instructions where valid", facts.waitAllocationLine);
			using (RuleTiming.Begin("SCR017", entry.Path)) ReportHotPath(entry, "SCR017", facts.stringFormatting, "move or gate formatting", facts.stringFormattingLine);
			using (RuleTiming.Begin("SCR018", entry.Path)) ReportHotPath(entry, "SCR018", facts.toStrings, "avoid formatting in hot paths", facts.toStringLine);
			using (RuleTiming.Begin("SCR019", entry.Path)) ReportHotPath(entry, "SCR019", facts.lambdas, "profile delegate/closure allocations", facts.lambdaLine);
			using (RuleTiming.Begin("SCR020", entry.Path)) ReportHotPath(entry, "SCR020", facts.runtimeHashes, "precompute stable hashes/IDs", facts.runtimeHashLine);
			using (RuleTiming.Begin("SCR021", entry.Path)) ReportHotPath(entry, "SCR021", facts.physicsSyncs, "avoid redundant transform synchronization", facts.physicsSyncLine);
			using (RuleTiming.Begin("SCR022", entry.Path)) ReportHotPath(entry, "SCR022", facts.rendererMaterialAccesses, "cache renderer state / use reusable material APIs deliberately", facts.rendererMaterialAccessLine);
			using (RuleTiming.Begin("SCR023", entry.Path)) ReportHotPath(entry, "SCR023", facts.animatorParameters, "cache animator parameter metadata", facts.animatorParametersLine);
			using (RuleTiming.Begin("SCR024", entry.Path)) ReportHotPath(entry, "SCR024", facts.inputTouches, "prefer touchCount/GetTouch for hot-path iteration", facts.inputTouchesLine);
			using (RuleTiming.Begin("SCR025", entry.Path)) ReportHotPath(entry, "SCR025", facts.gcCollects, "remove or move explicit garbage collection", facts.gcCollectLine);

			entry.Finalise();
			return entry;
		}


		static ScriptFacts Measure(string assetPath, bool? knownIsMonoBehaviour = null, string projectRoot = null)
		{
			string source;
			try
			{
				string fullPath = string.IsNullOrEmpty(projectRoot) ? AssetPathUtility.ToAbsolutePath(assetPath) : Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
				source = File.ReadAllText(fullPath);
			}
			catch
			{
				return null;
			}

			var facts = new ScriptFacts
			{
				factsSchema = k_FactsSchema,
				lineCount = CountLines(source)
			};

			if (knownIsMonoBehaviour.HasValue)
				facts.isMonoBehaviour = knownIsMonoBehaviour.Value;
			else
			{
				var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
				var scriptType = monoScript != null ? monoScript.GetClass() : null;
				facts.isMonoBehaviour = scriptType != null && typeof(MonoBehaviour).IsAssignableFrom(scriptType);
			}

			var code = StripCommentsAndStrings(source);

			facts.hasOnGui = k_OnGui.IsMatch(code);
			facts.debugLogs = k_DebugLog.Matches(code).Count;
			facts.sendMessages = k_SendMessage.Matches(code).Count;
			facts.resourcesLoads = k_ResourcesLoad.Matches(code).Count;

			// Per-frame callback inspection is meaningful only for a real MonoBehaviour.
			if (!facts.isMonoBehaviour)
				return facts;

			foreach (var method in k_PerFrameMethods)
			{
				foreach (var (body, bodyStart) in MethodBodies(code, method))
				{
					if (body.Trim().Length == 0)
					{
						if (!facts.emptyPerFrame.Contains(method))
						{
							facts.emptyPerFrame.Add(method);
							if (facts.emptyPerFrameLine < 0)
								facts.emptyPerFrameLine = LineOf(source, bodyStart);
						}

						continue;
					}

					AddMatches(
						facts.sceneSearches,
						method,
						k_FindCalls,
						body,
						bodyStart,
						source,
						ref facts.sceneSearchLine);

					AddMatches(
						facts.getComponents,
						method,
						k_GetComponent,
						body,
						bodyStart,
						source,
						ref facts.getComponentLine);

					AddMatches(
						facts.linq,
						method,
						k_Linq,
						body,
						bodyStart,
						source,
						ref facts.linqLine);

					AddMatches(
						facts.instantiateDestroy,
						method,
						k_InstantiateDestroy,
						body,
						bodyStart,
						source,
						ref facts.instantiateDestroyLine);

					AddMatches(facts.cameraMain, method, k_CameraMain, body, bodyStart, source, ref facts.cameraMainLine);
					AddMatches(facts.transformFind, method, k_TransformFind, body, bodyStart, source, ref facts.transformFindLine);
					AddMatches(facts.physicsQueries, method, k_AllocatingPhysicsQuery, body, bodyStart, source, ref facts.physicsQueriesLine);
					AddMatches(facts.newAllocations, method, k_NewAllocation, body, bodyStart, source, ref facts.newAllocationLine);
					AddMatches(facts.foreachLoops, method, k_Foreach, body, bodyStart, source, ref facts.foreachLine);
					AddMatches(facts.startCoroutines, method, k_StartCoroutine, body, bodyStart, source, ref facts.startCoroutineLine);
					AddMatches(facts.waitAllocations, method, k_WaitAllocation, body, bodyStart, source, ref facts.waitAllocationLine);
					AddMatches(facts.stringFormatting, method, k_StringFormatting, body, bodyStart, source, ref facts.stringFormattingLine);
					AddMatches(facts.toStrings, method, k_ToString, body, bodyStart, source, ref facts.toStringLine);
					AddMatches(facts.lambdas, method, k_Lambda, body, bodyStart, source, ref facts.lambdaLine);

					// The normal lexer masks string contents, so interpolate prefixes are recovered from the original source body.
					string rawBody = source.Substring(bodyStart, Math.Min(body.Length, source.Length - bodyStart));
					string interpolationScan = StripCommentsPreserveInterpolationPrefixes(rawBody);
					AddMatches(facts.stringFormatting, method, k_InterpolatedString, interpolationScan, bodyStart, source, ref facts.stringFormattingLine);
					AddMatches(facts.runtimeHashes, method, k_HashInHotPath, body, bodyStart, source, ref facts.runtimeHashLine);
					AddMatches(facts.physicsSyncs, method, k_PhysicsSync, body, bodyStart, source, ref facts.physicsSyncLine);
					if (ContainsRendererMaterialAccess(body))
					{
						int line = LineOf(source, bodyStart);
						if (facts.rendererMaterialAccessLine < 0)
							facts.rendererMaterialAccessLine = line;
						facts.rendererMaterialAccesses.Add("Line " + line + ": " + method + "() -> renderer material property/array access");
					}
					AddMatches(facts.animatorParameters, method, k_AnimatorParameters, body, bodyStart, source, ref facts.animatorParametersLine);
					AddMatches(facts.inputTouches, method, k_InputTouches, body, bodyStart, source, ref facts.inputTouchesLine);
					AddMatches(facts.gcCollects, method, k_GcCollect, body, bodyStart, source, ref facts.gcCollectLine);
				}
			}

			return facts;
		}

		static bool ContainsRendererMaterialAccess(string body)
		{
			if (k_RendererMaterialAccess.IsMatch(body) || k_RendererComponentAccess.IsMatch(body))
				return true;

			foreach (Match match in k_RendererTypeDeclaration.Matches(body))
			{
				string name = match.Groups["name"].Value;
				if (Regex.IsMatch(body, @"\b" + Regex.Escape(name) + @"\.(?:material|materials|sharedMaterials)\b"))
					return true;
			}

			return false;
		}

		/// <summary>
		/// Removes comments and masks string contents while preserving interpolated-string prefixes and source length.
		/// This is only used to detect interpolation syntax after the normal source lexer intentionally masks strings.
		/// </summary>
		static string StripCommentsPreserveInterpolationPrefixes(string source)
		{
			var sb = new StringBuilder(source.Length);
			int i = 0;
			int n = source.Length;

			while (i < n)
			{
				char c = source[i];

				if (c == '/' && i + 1 < n && source[i + 1] == '/')
				{
					sb.Append(' ').Append(' ');
					i += 2;
					while (i < n && source[i] != '\n') { sb.Append(' '); i++; }
					continue;
				}

				if (c == '/' && i + 1 < n && source[i + 1] == '*')
				{
					sb.Append(' ').Append(' ');
					i += 2;
					while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
					{
						sb.Append(source[i] == '\n' ? '\n' : ' ');
						i++;
					}
					if (i < n) { sb.Append(' ').Append(' '); i += 2; }
					continue;
				}

				if (c == '@' || c == '$')
				{
					int prefixEnd = i;
					while (prefixEnd < n && (source[prefixEnd] == '@' || source[prefixEnd] == '$')) prefixEnd++;

					if (prefixEnd < n && source[prefixEnd] == '"')
					{
						int quoteCount = 0;
						while (prefixEnd + quoteCount < n && source[prefixEnd + quoteCount] == '"') quoteCount++;
						bool interpolated = source.Substring(i, prefixEnd - i).IndexOf('$') >= 0;

						if (quoteCount >= 3)
						{
							for (int q = i; q < prefixEnd + quoteCount; q++) sb.Append(interpolated ? source[q] : ' ');
							i = prefixEnd + quoteCount;
							while (i < n)
							{
								if (source[i] == '"')
								{
									int endCount = 0;
									while (i + endCount < n && source[i + endCount] == '"') endCount++;
									if (endCount >= quoteCount)
									{
										for (int q = 0; q < quoteCount; q++) sb.Append(' ');
										i += quoteCount;
										break;
									}
								}

								sb.Append(source[i] == '\n' ? '\n' : ' ');
								i++;
							}
							continue;
						}

						int prefixLength = prefixEnd - i + 1;
						string prefix = source.Substring(i, prefixLength);
						for (int q = 0; q < prefixLength; q++) sb.Append(interpolated ? prefix[q] : ' ');
						i = prefixEnd + 1;
						bool verbatim = prefix.IndexOf('@') >= 0;

						while (i < n)
						{
							char current = source[i];
							if (!verbatim && current == '\\' && i + 1 < n)
							{
								sb.Append(' ').Append(source[i + 1] == '\n' ? '\n' : ' ');
								i += 2;
								continue;
							}
							if (verbatim && current == '"' && i + 1 < n && source[i + 1] == '"')
							{
								sb.Append(' ').Append(' ');
								i += 2;
								continue;
							}
							sb.Append(current == '\n' ? '\n' : ' ');
							i++;
							if (current == '"') break;
						}
						continue;
					}
				}

				if (c == '"')
				{
					int quoteCount = 1;
					while (i + quoteCount < n && source[i + quoteCount] == '"') quoteCount++;
					if (quoteCount >= 3)
					{
						for (int q = 0; q < quoteCount; q++) sb.Append(' ');
						i += quoteCount;
						while (i < n)
						{
							if (source[i] == '"')
							{
								int endCount = 0;
								while (i + endCount < n && source[i + endCount] == '"') endCount++;
								if (endCount >= quoteCount)
								{
									for (int q = 0; q < quoteCount; q++) sb.Append(' ');
									i += quoteCount;
									break;
								}
							}
							sb.Append(source[i] == '\n' ? '\n' : ' ');
							i++;
						}
						continue;
					}

					sb.Append(' ');
					i++;
					while (i < n)
					{
						char current = source[i];
						if (current == '\\' && i + 1 < n)
						{
							sb.Append(' ').Append(source[i + 1] == '\n' ? '\n' : ' ');
							i += 2;
							continue;
						}
						sb.Append(current == '\n' ? '\n' : ' ');
						i++;
						if (current == '"') break;
					}
					continue;
				}

				if (c == '\'')
				{
					sb.Append(' ');
					i++;
					while (i < n)
					{
						char current = source[i];
						if (current == '\\' && i + 1 < n)
						{
							sb.Append(' ').Append(source[i + 1] == '\n' ? '\n' : ' ');
							i += 2;
							continue;
						}
						sb.Append(current == '\n' ? '\n' : ' ');
						i++;
						if (current == '\'') break;
					}
					continue;
				}

				sb.Append(c);
				i++;
			}

			return sb.ToString();
		}

		static void AddMatches(
			List<string> into,
			string method,
			Regex pattern,
			string body,
			int bodyStart,
			string source,
			ref int firstLine)
		{
			foreach (Match match in pattern.Matches(body))
			{
				int line = LineOf(source, bodyStart + match.Index);

				if (firstLine < 0)
					firstLine = line;

				into.Add(
					"Line " + line +
					": " + method +
					"() -> " +
					match.Value.TrimEnd('(', '<', ' '));
			}
		}

		static int CountLines(string text)
		{
			if (string.IsNullOrEmpty(text))
				return 0;

			int lines = 1;
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == '\n')
					lines++;
			}

			return lines;
		}

		/// <summary>1-based line number of <paramref name="index"/> within <paramref name="text"/>.</summary>
		static int LineOf(string text, int index)
		{
			int line = 1;
			int limit = Math.Min(index, text.Length);

			for (int i = 0; i < limit; i++)
			{
				if (text[i] == '\n')
					line++;
			}

			return line;
		}

		/// <summary>
		/// Finds parameterless Unity callback declarations and yields their brace-delimited bodies.
		/// The source has already had comments and string literals blanked, so braces in those regions
		/// cannot corrupt the depth walk.
		/// </summary>
		static IEnumerable<(string body, int start)> MethodBodies(string code, string methodName)
		{
			var signature = new Regex(
				@"\b(?:void|async\s+void)\s+" +
				Regex.Escape(methodName) +
				@"\s*\(\s*\)\s*\{",
				RegexOptions.Compiled);

			foreach (Match match in signature.Matches(code))
			{
				int open = code.IndexOf('{', match.Index);
				if (open < 0)
					continue;

				int depth = 0;

				for (int i = open; i < code.Length; i++)
				{
					if (code[i] == '{')
					{
						depth++;
					}
					else if (code[i] == '}')
					{
						depth--;

						if (depth == 0)
						{
							yield return (
								code.Substring(open + 1, i - open - 1),
								open + 1);

							break;
						}
					}
				}
			}
		}

		/// <summary>
		/// Replaces comments and C# string/character literals with whitespace while preserving newlines
		/// and source length. The lexer deliberately treats interpolated/raw string contents as opaque:
		/// this avoids false positives from braces or text inside literals, at the cost of not inspecting
		/// code embedded in interpolation holes.
		/// </summary>
		static string StripCommentsAndStrings(string source)
		{
			var sb = new StringBuilder(source.Length);

			int i = 0;
			int n = source.Length;

			while (i < n)
			{
				char c = source[i];

				// Line comment.
				if (c == '/' && i + 1 < n && source[i + 1] == '/')
				{
					sb.Append(' ').Append(' ');
					i += 2;

					while (i < n && source[i] != '\n')
					{
						sb.Append(' ');
						i++;
					}

					continue;
				}

				// Block comment.
				if (c == '/' && i + 1 < n && source[i + 1] == '*')
				{
					sb.Append(' ').Append(' ');
					i += 2;

					while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
					{
						sb.Append(source[i] == '\n' ? '\n' : ' ');
						i++;
					}

					if (i < n)
					{
						sb.Append(' ').Append(' ');
						i += 2;
					}

					continue;
				}

				// C# raw string literal: """...""", """"..."""", etc.
				if (c == '"')
				{
					int quoteCount = 1;
					while (i + quoteCount < n && source[i + quoteCount] == '"')
						quoteCount++;

					if (quoteCount >= 3)
					{
						for (int q = 0; q < quoteCount; q++)
							sb.Append(' ');

						i += quoteCount;

						while (i < n)
						{
							if (source[i] == '"')
							{
								int endCount = 1;
								while (i + endCount < n && source[i + endCount] == '"')
									endCount++;

								if (endCount >= quoteCount)
								{
									for (int q = 0; q < quoteCount; q++)
										sb.Append(' ');

									i += quoteCount;
									break;
								}
							}

							sb.Append(source[i] == '\n' ? '\n' : ' ');
							i++;
						}

						continue;
					}
				}

				// Regular or verbatim/interpolated strings. We treat interpolation as opaque because
				// the goal is to prevent literal text from looking like source code.
				if (c == '"' || c == '\'' ||
					(c == '@' && i + 1 < n && source[i + 1] == '"') ||
					(c == '$' && i + 1 < n && source[i + 1] == '"') ||
					(c == '$' && i + 2 < n && source[i + 1] == '@' && source[i + 2] == '"') ||
					(c == '@' && i + 2 < n && source[i + 1] == '$' && source[i + 2] == '"'))
				{
					bool isChar = c == '\'';
					bool isVerbatim = c == '@' ||
									  (c == '$' && i + 1 < n && source[i + 1] == '@') ||
									  (c == '@' && i + 1 < n && source[i + 1] == '$');

					if (isChar)
					{
						sb.Append(' ');
						i++;

						while (i < n)
						{
							if (source[i] == '\\' && i + 1 < n)
							{
								sb.Append(' ').Append(source[i + 1] == '\n' ? '\n' : ' ');
								i += 2;
								continue;
							}

							char current = source[i];
							sb.Append(current == '\n' ? '\n' : ' ');
							i++;

							if (current == '\'')
								break;
						}

						continue;
					}

					// Prefix characters ($/@) and the opening quote.
					int prefixLength = c == '"' ? 0 :
						(c == '@' || c == '$') ? 1 : 2;

					for (int p = 0; p < prefixLength + 1; p++)
						sb.Append(' ');

					i += prefixLength + 1;

					while (i < n)
					{
						char current = source[i];

						if (!isVerbatim && current == '\\' && i + 1 < n)
						{
							sb.Append(' ').Append(source[i + 1] == '\n' ? '\n' : ' ');
							i += 2;
							continue;
						}

						if (isVerbatim && current == '"' && i + 1 < n && source[i + 1] == '"')
						{
							sb.Append(' ').Append(' ');
							i += 2;
							continue;
						}

						sb.Append(current == '\n' ? '\n' : ' ');
						i++;

						if (current == '"')
							break;
					}

					continue;
				}

				sb.Append(c);
				i++;
			}

			return sb.ToString();
		}
	}
}
