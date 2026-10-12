using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>
	/// Conservative static checks and narrowly scoped source fixes for runtime C# source.
	///
	/// The diagnostic layer remains heuristic. Only four canonical rules expose default source
	/// transformations, each restricted to a source shape that can be checked exactly and verified
	/// after writing. Other findings remain Review or diagnostic-only because a source pattern alone
	/// does not establish runtime behaviour, object lifetime, or project policy.
	/// </summary>
	public sealed class ScriptAnalyzer : IBackgroundAssetAnalyzer
	{
		// Bumped when ScriptFacts gains/changes measurements. This local version lets the analyzer
		// refresh stale rows even when the host project's global cache schema has not yet been bumped.
		const int k_FactsSchema = 3;

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
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "SCR002", title: "Expensive scene search inside a per-frame method", category: AssetCategory.Script,
							why: "Unity's scene-search APIs inspect loaded objects rather than using a direct field reference. In a per-frame callback, repeating a global search can scale poorly with scene size and invocation count.",
							fix: "Resolve the reference at a suitable lifecycle point, cache it, or otherwise use an explicit dependency instead of searching globally every frame.",
							severity: IssueSeverity.Critical,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR003", title: "GetComponent inside a per-frame method", category: AssetCategory.Script,
							why: "GetComponent-family calls perform a component lookup each time they execute. The cost of one lookup is often small, but repeated execution across many instances can become measurable.",
							fix: "Cache stable component references during initialization when profiling shows the lookup is worth removing.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.High,
							backupRequired: true
							);

			Rules.Register(id: "SCR004", title: "LINQ inside a per-frame method", category: AssetCategory.Script,
							why: "LINQ introduces iterator/query machinery and, for many operators and expression shapes, allocations or additional enumeration work. The exact cost depends on the source collection, operator chain, captures, and execution path.",
							fix: "Profile the expression. Where it is hot, replace the query with an allocation-free loop and reuse destination collections as appropriate.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR005", title: "Debug logging in runtime code", category: AssetCategory.Script,
							why: "Debug logging has runtime overhead and can become significant when called frequently. The analyzer cannot determine whether a log is startup-only, conditional, or in a hot path from source text alone.",
							fix: "Remove or gate diagnostics for production builds, or route them through a logging abstraction with an explicit runtime policy.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR006", title: "OnGUI implemented in runtime code", category: AssetCategory.Script,
							why: "An exactly empty, unannotated private OnGUI callback performs no work and can be removed safely when the source class is unambiguous. Non-empty or ambiguous callbacks remain diagnostic-only because their purpose is project-specific.",
							fix: "Remove only an exactly empty, unannotated private OnGUI callback. Keep non-empty, annotated, protected, or structurally ambiguous callbacks unchanged.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Verified,
							confidence: OptimizationConfidence.HighConfidence,
							recommendationConfidence: OptimizationConfidence.HighConfidence,
							risk: FixRisk.Medium,
							backupRequired: true
							);

			Rules.Register(id: "SCR007", title: "SendMessage used instead of a direct call", category: AssetCategory.Script,
							why: "SendMessage-family APIs perform runtime message lookup and are less explicit than a direct reference or interface call. The analyzer cannot establish whether the flexibility is intentional.",
							fix: "Prefer explicit references, interfaces, events, or another typed dispatch mechanism when the target relationship is known.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR008", title: "Instantiate or Destroy inside a per-frame method", category: AssetCategory.Script,
							why: "Creating and destroying objects from a per-frame callback can cause allocation, destruction, initialization, and renderer/physics bookkeeping work. Whether pooling is appropriate depends on lifetime and usage patterns.",
							fix: "Profile the path. For repeatedly spawned short-lived objects, consider pooling rather than creating and destroying them every frame.",
							severity: IssueSeverity.Warning,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Heuristic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			Rules.Register(id: "SCR009", title: "Resources.Load in runtime code", category: AssetCategory.Script,
							why: "Resources.Load is an explicit dependency on Unity's Resources system. Assets in Resources are made independently loadable and can contribute to the build's resources data; widespread use can complicate memory, build-size, and asset-management control.",
							fix: "For production content, prefer explicit references or the project's asset-management system such as Addressables where appropriate. If Resources is intentional, keep loads out of hot paths and manage lifetime explicitly.",
							severity: IssueSeverity.Info,
							fixKind: FixKind.Review,
							confidence: OptimizationConfidence.Diagnostic,
							risk: FixRisk.Unspecified,
							backupRequired: false
							);

			RegisterHotPathRule("SCR010", "Camera.main in a per-frame method",
				"Camera.main performs a camera lookup each time it is queried. Repeating it from a per-frame callback can become unnecessary work across many behaviours, although the absolute cost is workload-dependent.",
				"Cache the camera reference during initialization when profiling shows repeated lookup cost.", IssueSeverity.Info, fixKind: FixKind.None);

			RegisterHotPathRule("SCR011", "Transform.Find in a per-frame method",
				"Transform.Find traverses a hierarchy by name/path. Repeating hierarchy searches every frame can scale poorly and hides a dependency that can usually be represented directly.",
				"Cache the Transform reference or serialize the dependency instead of searching the hierarchy repeatedly.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR012", "Allocating physics query in a per-frame method",
				"These Physics and Physics2D query forms return result arrays and Unity documents allocation for the allocating variants. Static inspection still cannot prove that the call is a meaningful runtime bottleneck.",
				"Prefer a corresponding non-allocating query or a reusable result buffer where the API contract permits it; verify buffer sizing and actual profiler impact.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR013", "Allocation candidate in a per-frame method",
				"A new-expression inside Update/LateUpdate/FixedUpdate may allocate managed objects or arrays. Value-type construction does not necessarily create a heap allocation, so source inspection alone cannot prove the runtime allocation cost.",
				"Profile the specific expression first; reuse managed objects or buffers only where allocation is demonstrated and the lifetime permits.", IssueSeverity.Warning, fixKind: FixKind.None);

			RegisterHotPathRule("SCR014", "foreach loop in a per-frame method",
				"foreach is usually cheap over arrays and many concrete collections, but enumeration can introduce interface/iterator overhead in some cases. This is a diagnostic rather than a blanket claim that foreach is slow.",
				"Only replace the loop after profiling; for hot allocation-sensitive paths, use an allocation-free iteration form appropriate to the collection.", IssueSeverity.Info, fixKind: FixKind.None);

			RegisterHotPathRule("SCR015", "StartCoroutine in a per-frame method",
				"Starting a coroutine every frame creates coroutine state and scheduling work. Repeated starts can accumulate many concurrent routines or allocations when not intentionally bounded.",
				"Ensure coroutine creation is event-driven or explicitly bounded; reuse a persistent routine when appropriate.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR016", "Wait instruction allocated in a per-frame method",
				"Creating WaitForSeconds, WaitForSecondsRealtime, WaitUntil or WaitWhile instances creates coroutine yield objects. Repeated creation on hot paths can produce avoidable managed allocations.",
				"Cache reusable wait instructions where their timing/predicate semantics allow it, or restructure the routine around explicit timers.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR017", "String formatting in a per-frame method",
				"String interpolation and string.Format create formatted strings and can allocate managed memory. The cost is entirely dependent on execution frequency and whether the result is needed.",
				"Avoid formatting strings in hot paths unless the result is consumed; gate diagnostics and move UI/telemetry updates to a lower cadence where practical.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR018", "ToString in a per-frame method",
				"ToString commonly creates a new managed string. In frequently executing code this can add GC pressure even when the value itself is inexpensive to compute.",
				"Keep formatting out of hot paths unless required, especially for telemetry or UI that can update at a lower cadence.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR019", "Lambda/delegate expression in a per-frame method",
				"Lambda expressions can allocate delegate or closure objects depending on capture and conversion context. Source text alone cannot determine whether this occurrence allocates.",
				"Profile the specific expression. Avoid per-frame captured closures and reuse delegates when allocation is demonstrated.", IssueSeverity.Info, fixKind: FixKind.None);

			RegisterHotPathRule("SCR020", "Runtime hash/ID generation in a per-frame method",
				"Animator.StringToHash and Shader.PropertyToID are useful for stable identifiers, but recalculating a stable identifier in a hot path performs avoidable work.",
				"Precompute stable hashes/IDs during initialization or static setup. Keep dynamic identifiers dynamic when they legitimately vary at runtime.", IssueSeverity.Info, fixKind: FixKind.Verified);

			RegisterHotPathRule("SCR021", "Physics.SyncTransforms in a per-frame method",
				"Explicit physics transform synchronization can force physics to consume transform changes earlier than the normal synchronization point. Repeating it every frame can add avoidable physics work.",
				"Remove redundant synchronization where the default simulation order is sufficient. Retain it only when immediate physics visibility is required and profile the cost.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR022", "Renderer material or material-array access in a per-frame method",
				"Renderer.material can instantiate a material, while materials/sharedMaterials return material arrays and array access can create avoidable temporary work. Repeating these accesses in a hot callback can amplify CPU and managed-memory cost.",
				"Cache stable renderer state, use GetMaterials/GetSharedMaterials with reusable lists where appropriate, or use MaterialPropertyBlock for per-renderer properties. Do not replace material ownership semantics blindly.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR023", "Animator.parameters array access in a per-frame method",
				"Animator.parameters exposes an array-valued representation of the animator's parameters and is documented as an allocating API. Repeated access from a per-frame callback can therefore create avoidable managed work.",
				"Use parameterCount/GetParameter or cache the required parameter metadata outside the hot path.", IssueSeverity.Warning, fixKind: FixKind.Review);

			RegisterHotPathRule("SCR024", "Input.touches array access in a per-frame method",
				"The legacy Input.touches property returns an array snapshot and can allocate temporary data. Repeated access in a per-frame callback can create recurring garbage and CPU work.",
				"Prefer Input.touchCount with Input.GetTouch for allocation-free iteration, or read Input.touches once when an array snapshot is actually required.", IssueSeverity.Warning, fixKind: FixKind.Verified);
			Rules.Register(id: "SCR025", title: "Explicit GC.Collect in a per-frame method", category: AssetCategory.Script,
				why: "Forcing a garbage collection from Update, LateUpdate or FixedUpdate can introduce frame-time spikes. Source inspection cannot establish how often the call executes, so the finding remains advisory.",
				fix: "Remove or move the explicit collection unless a measured, controlled collection point is intentionally required.",
				severity: IssueSeverity.Warning, fixKind: FixKind.Review, confidence: OptimizationConfidence.HighConfidence, risk: FixRisk.Unspecified, backupRequired: false);

		}

		static void RegisterHotPathRule(string id, string title, string why, string fix, IssueSeverity severity, FixKind fixKind = FixKind.None)
		{
			bool verifiedSourceFix = fixKind == FixKind.Verified;
			Rules.Register(id: id, title: title, category: AssetCategory.Script, why: why, fix: fix,
				severity: severity, fixKind: fixKind, confidence: verifiedSourceFix ? OptimizationConfidence.HighConfidence : OptimizationConfidence.Heuristic,
				risk: verifiedSourceFix ? FixRisk.High : FixRisk.Unspecified, backupRequired: verifiedSourceFix);
		}

		static void ReportHotPath(AssetEntry entry, string ruleId, List<string> matches, string recommended, int line, List<string> supportedMatches = null)
		{
			if (matches == null || matches.Count == 0) return;
			OptimizationIssue issue;
			switch (ruleId)
			{
				case "SCR020":
					issue = supportedMatches != null && supportedMatches.Count > 0
						? entry.AddIssue(ruleId, matches.Count + " hash/ID call(s) in per-frame callbacks", recommended,
							fix: ApplyLiteralHashFix, verify: VerifyLiteralHashFix)
						: entry.AddIssue(ruleId, matches.Count + " hash/ID call(s) in per-frame callbacks", recommended);
					if (issue != null && (supportedMatches == null || supportedMatches.Count == 0)) issue.FixKind = FixKind.None;
					break;
				case "SCR024":
					issue = supportedMatches != null && supportedMatches.Count > 0
						? entry.AddIssue(ruleId, matches.Count + " Input.touches access(es) in per-frame callbacks", recommended,
							fix: ApplyInputTouchesFix, verify: VerifyInputTouchesFix)
						: entry.AddIssue(ruleId, matches.Count + " Input.touches access(es) in per-frame callbacks", recommended);
					if (issue != null && (supportedMatches == null || supportedMatches.Count == 0)) issue.FixKind = FixKind.None;
					break;
				case "SCR011":
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended,
						options: BuildTransformFindReviewOptions(entry.Path));
					break;
				case "SCR016":
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended,
						options: BuildWaitInstructionReviewOptions(entry.Path));
					break;
				case "SCR021":
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended,
						options: BuildRemoveCallReviewOptions(entry.Path, "SCR021"));
					break;
				case "SCR022":
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended,
						options: BuildSharedMaterialReviewOptions(entry.Path));
					break;
				case "SCR025":
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended,
						options: BuildRemoveCallReviewOptions(entry.Path, "SCR025"));
					break;
				default:
					issue = entry.AddIssue(ruleId, matches.Count + " occurrence(s) in per-frame callbacks", recommended);
					break;
			}
			issue?.Items("Called at", matches).AtLine(line);
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
			public List<string> supportedEmptyPrivateCallbacks = new List<string>();
			public int supportedEmptyPrivateCallbackLine = -1;

			public List<string> sceneSearches = new List<string>();
			public int sceneSearchLine = -1;

			public List<string> getComponents = new List<string>();
			public int getComponentLine = -1;
			public List<string> supportedGetComponents = new List<string>();
			public int supportedGetComponentLine = -1;

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
			public List<string> supportedRuntimeHashes = new List<string>();
			public int supportedRuntimeHashLine = -1;

			public List<string> physicsSyncs = new List<string>();
			public int physicsSyncLine = -1;

			public List<string> rendererMaterialAccesses = new List<string>();
			public int rendererMaterialAccessLine = -1;

			public List<string> animatorParameters = new List<string>();
			public int animatorParametersLine = -1;

			public List<string> inputTouches = new List<string>();
			public int inputTouchesLine = -1;
			public List<string> supportedInputTouches = new List<string>();
			public int supportedInputTouchesLine = -1;

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
						var emptyCallbackIssue = entry.AddIssue(
								"SCR001",
								string.Join(", ", facts.emptyPerFrame) + " empty per-frame callback(s)",
								"remove supported empty private callbacks; review other declarations",
								fix: facts.supportedEmptyPrivateCallbacks.Count > 0 ? (Action<FixContext>)ApplyEmptyCallbackFix : null,
								verify: facts.supportedEmptyPrivateCallbacks.Count > 0 ? (Func<FixContext, bool>)VerifyEmptyCallbackFix : null);
						if (emptyCallbackIssue != null && facts.supportedEmptyPrivateCallbacks.Count == 0) emptyCallbackIssue.FixKind = FixKind.None;
						emptyCallbackIssue?.Items("Methods", facts.emptyPerFrame).AtLine(facts.emptyPerFrameLine);

						using (RuleTiming.Begin("SCR002", entry.Path))
							if (facts.sceneSearches.Count > 0)
							{
								entry.AddIssue(
										"SCR002",
										facts.sceneSearches.Count + " call(s) in per-frame callbacks",
										"cached or explicitly referenced",
										options: BuildSceneSearchReviewOptions(entry.Path))
									?.Items("Called at", facts.sceneSearches)
									.AtLine(facts.sceneSearchLine);
							}

						using (RuleTiming.Begin("SCR003", entry.Path))
							if (facts.getComponents.Count > 0)
							{
								var getComponentIssue = entry.AddIssue(
										"SCR003",
										facts.getComponents.Count + " GetComponent-family call(s) in per-frame callbacks",
										"cache supported direct self-component lookups; review other lookup forms",
										fix: facts.supportedGetComponents.Count > 0 ? (Action<FixContext>)ApplyGetComponentCacheFix : null,
										verify: facts.supportedGetComponents.Count > 0 ? (Func<FixContext, bool>)VerifyGetComponentCacheFix : null);
								if (getComponentIssue != null && facts.supportedGetComponents.Count == 0) getComponentIssue.FixKind = FixKind.None;
								getComponentIssue?.Items("Called at", facts.getComponents).AtLine(facts.getComponentLine);
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
										"cached or explicitly referenced",
										options: BuildSceneSearchReviewOptions(entry.Path))
									?.Items("Called at", facts.sceneSearches)
									.AtLine(facts.sceneSearchLine);
							}

						using (RuleTiming.Begin("SCR003", entry.Path))
							if (facts.getComponents.Count > 0)
							{
								var getComponentIssue = entry.AddIssue(
										"SCR003",
										facts.getComponents.Count + " GetComponent-family call(s) in per-frame callbacks",
										"cache supported direct self-component lookups; review other lookup forms",
										fix: facts.supportedGetComponents.Count > 0 ? (Action<FixContext>)ApplyGetComponentCacheFix : null,
										verify: facts.supportedGetComponents.Count > 0 ? (Func<FixContext, bool>)VerifyGetComponentCacheFix : null);
								if (getComponentIssue != null && facts.supportedGetComponents.Count == 0) getComponentIssue.FixKind = FixKind.None;
								getComponentIssue?.Items("Called at", facts.getComponents).AtLine(facts.getComponentLine);
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
				{
					bool canRemoveEmptyCallback = TryReadSourceDocument(entry.Path, out var onGuiDocument)
						&& TryRewriteEmptyOnGui(onGuiDocument.Text, out _);
					OptimizationIssue issue = canRemoveEmptyCallback
						? entry.AddIssue("SCR006", "OnGUI implemented", "remove exactly empty private callback", 0,
							c => ApplySourceRewrite(c, "SCR006", TryRewriteEmptyOnGui),
							"Only an unannotated, exactly empty private callback in one unambiguous MonoBehaviour class is removed. Non-empty callbacks remain untouched.",
							verify: c => VerifySourceRewrite(c, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && !TryRewriteEmptyOnGui(source, out _)))
						: entry.AddIssue("SCR006", "OnGUI implemented", "inspect callback purpose", 0, null,
							"No automatic fix is offered because the callback is non-empty, annotated, or cannot be isolated safely. The analyzer does not migrate intentional IMGUI behavior.");
					if (issue != null && !canRemoveEmptyCallback)
						RouteOnGuiIssueToDiagnosticOnly(issue);
				}

			using (RuleTiming.Begin("SCR005", entry.Path))
				if (facts.debugLogs > 0)
					entry.AddIssue("SCR005", facts.debugLogs + " logging call(s)", "gate/remove production diagnostics", options: BuildDebugLogReviewOptions(entry.Path));

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
			using (RuleTiming.Begin("SCR020", entry.Path)) ReportHotPath(entry, "SCR020", facts.runtimeHashes, "precompute stable hashes/IDs", facts.runtimeHashLine, facts.supportedRuntimeHashes);
			using (RuleTiming.Begin("SCR021", entry.Path)) ReportHotPath(entry, "SCR021", facts.physicsSyncs, "avoid redundant transform synchronization", facts.physicsSyncLine);
			using (RuleTiming.Begin("SCR022", entry.Path)) ReportHotPath(entry, "SCR022", facts.rendererMaterialAccesses, "cache renderer state / use reusable material APIs deliberately", facts.rendererMaterialAccessLine);
			using (RuleTiming.Begin("SCR023", entry.Path)) ReportHotPath(entry, "SCR023", facts.animatorParameters, "cache animator parameter metadata", facts.animatorParametersLine);
			using (RuleTiming.Begin("SCR024", entry.Path)) ReportHotPath(entry, "SCR024", facts.inputTouches, "prefer touchCount/GetTouch for hot-path iteration", facts.inputTouchesLine, facts.supportedInputTouches);
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

					AddMatches(facts.getComponents, method, k_GetComponent, body, bodyStart, source, ref facts.getComponentLine);

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

			// Verified rules report only source shapes for which this file contains a deterministic rewrite.
			facts.supportedEmptyPrivateCallbacks.Clear();
			facts.supportedEmptyPrivateCallbackLine = -1;
			foreach (var candidate in FindEmptyPrivateCallbackEdits(source))
			{
				if (!facts.supportedEmptyPrivateCallbacks.Contains(candidate.Name)) facts.supportedEmptyPrivateCallbacks.Add(candidate.Name);
				if (facts.supportedEmptyPrivateCallbackLine < 0) facts.supportedEmptyPrivateCallbackLine = candidate.Line;
			}

			facts.supportedGetComponents.Clear();
			facts.supportedGetComponentLine = -1;
			foreach (var call in FindSupportedGetComponentCalls(source))
			{
				facts.supportedGetComponents.Add("Line " + call.Line + ": " + call.Method + "() -> GetComponent<" + call.TypeName + ">()");
				if (facts.supportedGetComponentLine < 0) facts.supportedGetComponentLine = call.Line;
			}

			facts.supportedRuntimeHashes.Clear();
			facts.supportedRuntimeHashLine = -1;
			foreach (var call in FindLiteralHashCalls(source))
			{
				facts.supportedRuntimeHashes.Add("Line " + call.Line + ": " + call.Method + "() -> " + call.Api + "(" + call.Argument + ")");
				if (facts.supportedRuntimeHashLine < 0) facts.supportedRuntimeHashLine = call.Line;
			}

			facts.supportedInputTouches.Clear();
			facts.supportedInputTouchesLine = -1;
			foreach (var loop in FindSupportedInputTouchesLoops(source))
			{
				facts.supportedInputTouches.Add("Line " + loop.Line + ": " + loop.Method + "() -> foreach over Input.touches");
				if (facts.supportedInputTouchesLine < 0) facts.supportedInputTouchesLine = loop.Line;
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

		// ----- Script source fix infrastructure (kept local to this analyzer) -----------------

		sealed class SourceDocument
		{
			public string Text;
			public Encoding Encoding;
			public byte[] Bom;
		}

		sealed class SourceClassRange
		{
			public int OpenBrace;
			public int CloseBrace;
		}

		sealed class PerFrameBody
		{
			public string Method;
			public int BodyStart;
			public int BodyEnd;
		}

		sealed class SourceEdit
		{
			public int Start;
			public int Length;
			public string Replacement;
			public string Name;
			public int Line;
		}

		sealed class ComponentCall
		{
			public int Start;
			public int Length;
			public string TypeName;
			public string Method;
			public int Line;
		}

		sealed class HashCall
		{
			public int Start;
			public int Length;
			public string Api;
			public string Argument;
			public string FieldName;
			public string Method;
			public int Line;
		}

		sealed class TouchLoop
		{
			public int HeaderStart;
			public int HeaderLength;
			public int OpenBrace;
			public string ElementType;
			public string VariableName;
			public string IndexName;
			public string Method;
			public int Line;
		}

		delegate bool SourceRewrite(string source, out string rewritten);

		static string AbsoluteSourcePath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) throw new InvalidOperationException("The source asset path is missing.");
			return AssetPathUtility.ToAbsolutePath(assetPath);
		}

		static bool TryReadSourceDocument(string assetPath, out SourceDocument document)
		{
			document = null;
			try
			{
				byte[] bytes = File.ReadAllBytes(AbsoluteSourcePath(assetPath));
				Encoding encoding;
				byte[] bom = Array.Empty<byte>();
				int offset = 0;
				if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				{
					encoding = new UTF8Encoding(false, true);
					bom = new byte[] { 0xEF, 0xBB, 0xBF };
					offset = 3;
				}
				else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
				{
					encoding = Encoding.Unicode;
					bom = new byte[] { 0xFF, 0xFE };
					offset = 2;
				}
				else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
				{
					encoding = Encoding.BigEndianUnicode;
					bom = new byte[] { 0xFE, 0xFF };
					offset = 2;
				}
				else
					encoding = new UTF8Encoding(false, true);

				document = new SourceDocument { Text = encoding.GetString(bytes, offset, bytes.Length - offset), Encoding = encoding, Bom = bom };
				return true;
			}
			catch
			{
				return false;
			}
		}

		static void ApplySourceRewrite(FixContext context, string ruleId, SourceRewrite rewrite)
		{
			if (context == null) throw new InvalidOperationException(ruleId + ": fix context is missing.");
			if (!context.BackupEnabled) throw new InvalidOperationException(ruleId + ": a source backup is required; enable fix backups and retry.");
			string path = AbsoluteSourcePath(context.AssetPath);
			if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException(ruleId + ": target is not an existing C# source file.");
			if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
				throw new InvalidOperationException(ruleId + ": source file is read-only.");
			if (!TryReadSourceDocument(context.AssetPath, out var document))
				throw new InvalidOperationException(ruleId + ": source could not be read in a supported encoding.");
			if (!rewrite(document.Text, out string rewritten) || string.IsNullOrEmpty(rewritten) || string.Equals(rewritten, document.Text, StringComparison.Ordinal))
				throw new InvalidOperationException(ruleId + ": the source no longer matches the supported fix pattern; no change was written.");

			byte[] content = document.Encoding.GetBytes(rewritten);
			byte[] bytes = new byte[document.Bom.Length + content.Length];
			if (document.Bom.Length > 0) Buffer.BlockCopy(document.Bom, 0, bytes, 0, document.Bom.Length);
			Buffer.BlockCopy(content, 0, bytes, document.Bom.Length, content.Length);
			File.WriteAllBytes(path, bytes);
			context.NeedsReimport = true;
		}

		static bool VerifySourceRewrite(FixContext context, Func<string, bool> condition)
		{
			return context != null && TryReadSourceDocument(context.AssetPath, out var document) && condition(document.Text);
		}

		static void ApplyEmptyCallbackFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR001", TryRewriteEmptyPrivateCallbacks);

		static bool VerifyEmptyCallbackFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindEmptyPrivateCallbackEdits(source).Count == 0);

		static void ApplyGetComponentCacheFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR003", TryRewriteGetComponentCache);

		static bool VerifyGetComponentCacheFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindSupportedGetComponentCalls(source).Count == 0);

		static void ApplyLiteralHashFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR020", TryRewriteLiteralHashCalls);

		static bool VerifyLiteralHashFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindLiteralHashCalls(source).Count == 0);

		static void ApplyInputTouchesFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR024", TryRewriteInputTouchesLoops);

		static bool VerifyInputTouchesFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindSupportedInputTouchesLoops(source).Count == 0);

		static bool TryGetSingleMonoBehaviourClass(string source, out string code, out SourceClassRange range)
		{
			code = StripCommentsAndStrings(source ?? string.Empty);
			range = null;
			var classes = Regex.Matches(code, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*(?<bases>\s*:[^{;]+)?\s*\{");
			Match selected = null;
			int selectedCount = 0;
			foreach (Match match in classes)
			{
				string bases = match.Groups["bases"].Value.Trim();
				if (bases.StartsWith(":", StringComparison.Ordinal)) bases = bases.Substring(1).Trim();
				if (!Regex.IsMatch(bases, @"(?:^|,)\s*(?:UnityEngine\s*\.\s*)?MonoBehaviour\b")) continue;
				selected = match;
				selectedCount++;
			}
			if (selectedCount != 1 || selected == null) return false;

			int open = selected.Index + selected.Length - 1;
			int close = FindMatchingBrace(code, open);
			if (close <= open) return false;
			// Nested type bodies complicate determining which class owns a callback. Do not rewrite them.
			if (Regex.IsMatch(code.Substring(open + 1, close - open - 1), @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*")) return false;
			range = new SourceClassRange { OpenBrace = open, CloseBrace = close };
			return true;
		}

		static int FindMatchingBrace(string code, int openBrace)
		{
			if (openBrace < 0 || openBrace >= code.Length || code[openBrace] != '{') return -1;
			int depth = 0;
			for (int i = openBrace; i < code.Length; i++)
			{
				if (code[i] == '{') depth++;
				else if (code[i] == '}' && --depth == 0) return i;
			}
			return -1;
		}

		static List<PerFrameBody> GetPerFrameBodies(string code, SourceClassRange range)
		{
			var result = new List<PerFrameBody>();
			if (range == null) return result;
			int start = range.OpenBrace + 1;
			string classBody = code.Substring(start, range.CloseBrace - start);
			foreach (string method in k_PerFrameMethods)
			{
				foreach (var body in MethodBodies(classBody, method))
				{
					int absoluteBodyStart = start + body.start;
					int closingBrace = FindMatchingBrace(code, absoluteBodyStart - 1);
					if (closingBrace <= absoluteBodyStart) continue;
					result.Add(new PerFrameBody { Method = method, BodyStart = absoluteBodyStart, BodyEnd = closingBrace });
				}
			}
			return result;
		}

		static bool HasAttributeOrXmlDocAbove(string source, string code, int declarationStart)
		{
			int previousLineEnd = code.LastIndexOf('\n', Math.Max(0, declarationStart - 1));
			while (previousLineEnd >= 0)
			{
				int previousLineStart = code.LastIndexOf('\n', Math.Max(0, previousLineEnd - 1)) + 1;
				string codeLine = code.Substring(previousLineStart, previousLineEnd - previousLineStart).Trim();
				string rawLine = source.Substring(previousLineStart, Math.Min(previousLineEnd, source.Length) - previousLineStart).TrimStart();
				// Comments are blanked by StripCommentsAndStrings, so inspect raw text before deciding
				// that a line is an ignorable blank. Preserve commented lifecycle intent conservatively.
				if (rawLine.StartsWith("///", StringComparison.Ordinal) || rawLine.StartsWith("//", StringComparison.Ordinal)
					|| rawLine.StartsWith("/*", StringComparison.Ordinal) || rawLine.StartsWith("*", StringComparison.Ordinal)) return true;
				if (codeLine.Length == 0)
				{
					previousLineEnd = previousLineStart - 1;
					continue;
				}
				if (codeLine.StartsWith("[") || codeLine.StartsWith("#")) return true;
				return false;
			}
			return false;
		}

		static List<SourceEdit> FindEmptyPrivateCallbackEdits(string source)
		{
			var edits = new List<SourceEdit>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return edits;
			string body = code.Substring(range.OpenBrace + 1, range.CloseBrace - range.OpenBrace - 1);
			int bodyOffset = range.OpenBrace + 1;
			var declaration = new Regex(@"(?m)^(?<indent>[ \t]*)private[ \t]+void[ \t]+(?<name>Update|LateUpdate|FixedUpdate)[ \t]*\([ \t]*\)[ \t]*\{");
			foreach (Match match in declaration.Matches(body))
			{
				if (HasAttributeOrXmlDocAbove(source, code, bodyOffset + match.Index)) continue;
				int open = bodyOffset + match.Index + match.Length - 1;
				int close = FindMatchingBrace(code, open);
				if (close <= open || !string.IsNullOrWhiteSpace(code.Substring(open + 1, close - open - 1))) continue;

				int lineStart = source.LastIndexOf('\n', Math.Max(0, bodyOffset + match.Index - 1)) + 1;
				int end = close + 1;
				while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
				if (end < source.Length && source[end] == '\r') end++;
				if (end < source.Length && source[end] == '\n') end++;
				edits.Add(new SourceEdit { Start = lineStart, Length = end - lineStart, Replacement = string.Empty, Name = match.Groups["name"].Value, Line = LineOf(source, bodyOffset + match.Index) });
			}
			return edits;
		}

		static bool TryRewriteEmptyPrivateCallbacks(string source, out string rewritten)
		{
			rewritten = source;
			var edits = FindEmptyPrivateCallbackEdits(source);
			if (edits.Count == 0) return false;
			foreach (var edit in edits.OrderByDescending(e => e.Start))
				rewritten = rewritten.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
			return true;
		}

		static List<ComponentCall> FindSupportedGetComponentCalls(string source)
		{
			var calls = new List<ComponentCall>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return calls;
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				var pattern = new Regex(@"(?<![A-Za-z0-9_.])(?:\bthis\s*\.\s*)?\bGetComponent\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*>\s*\(\s*\)");
				foreach (Match match in pattern.Matches(body))
				{
					int absolute = method.BodyStart + match.Index;
					calls.Add(new ComponentCall { Start = absolute, Length = match.Length, TypeName = match.Groups["type"].Value, Method = method.Method, Line = LineOf(source, absolute) });
				}
			}
			// The generated member names must be guaranteed not to shadow user members or locals.
			foreach (var type in calls.Select(c => c.TypeName).Distinct(StringComparer.Ordinal))
			{
				string field = ComponentCacheField(type);
				string accessor = ComponentCacheAccessor(type);
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(field) + @"\b") || Regex.IsMatch(code, @"\b" + Regex.Escape(accessor) + @"\b")) return new List<ComponentCall>();
			}
			return calls;
		}

		static string ComponentCacheField(string typeName) => "__poCachedComponent_" + typeName;
		static string ComponentCacheAccessor(string typeName) => "__poGetComponent_" + typeName;

		static bool TryRewriteGetComponentCache(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var calls = FindSupportedGetComponentCalls(source);
			if (calls.Count == 0) return false;

			var types = calls.Select(c => c.TypeName).Distinct(StringComparer.Ordinal).ToList();
			var members = new StringBuilder();
			foreach (string type in types)
			{
				string field = ComponentCacheField(type);
				string accessor = ComponentCacheAccessor(type);
				members.Append("\t\tprivate ").Append(type).Append(' ').Append(field).AppendLine(";");
				members.Append("\t\tprivate ").Append(type).Append(' ').Append(accessor).AppendLine("()");
				members.AppendLine("\t\t{");
				members.Append("\t\t\tif ((object)").Append(field).Append(" == null || ((UnityEngine.Object)(object)").Append(field).AppendLine(") == null)");
				members.Append("\t\t\t\t").Append(field).Append(" = GetComponent<").Append(type).AppendLine(">();");
				members.Append("\t\t\treturn ").Append(field).AppendLine(";");
				members.AppendLine("\t\t}");
			}

			int insertion = range.CloseBrace;
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			string prefix = insertion > 0 && source[insertion - 1] != '\n' ? newline : string.Empty;
			string membersText = members.ToString().Replace("\r\n", "\n").Replace("\n", newline);
			rewritten = rewritten.Insert(insertion, prefix + newline + membersText);
			foreach (var call in calls.OrderByDescending(c => c.Start))
				rewritten = rewritten.Remove(call.Start, call.Length).Insert(call.Start, ComponentCacheAccessor(call.TypeName) + "()");
			return true;
		}

		static List<HashCall> FindLiteralHashCalls(string source)
		{
			var calls = new List<HashCall>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return calls;
			var usedNames = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				foreach (Match match in k_HashInHotPath.Matches(body))
				{
					int start = method.BodyStart + match.Index;
					int open = start + match.Length - 1;
					int close = FindMatchingDelimiter(code, open, '(', ')');
					if (close <= open) continue;
					string argument = source.Substring(open + 1, close - open - 1);
					Match literal = Regex.Match(argument, @"^\s*(?<literal>""(?:\\.|[^""\\])*"")\s*$");
					if (!literal.Success) continue;
					string api = match.Value.StartsWith("Shader.PropertyToID", StringComparison.Ordinal) ? "Shader.PropertyToID" : "Animator.StringToHash";
					string field = HashFieldName(api, literal.Groups["literal"].Value);
					string key = api + "\n" + literal.Groups["literal"].Value;
					if (usedNames.TryGetValue(field, out string otherKey) && !string.Equals(otherKey, key, StringComparison.Ordinal)) return new List<HashCall>();
					usedNames[field] = key;
					calls.Add(new HashCall { Start = start, Length = close - start + 1, Api = api, Argument = literal.Groups["literal"].Value, FieldName = field, Method = method.Method, Line = LineOf(source, start) });
				}
			}

			foreach (var field in usedNames.Keys)
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(field) + @"\b")) return new List<HashCall>();
			return calls;
		}

		static int FindMatchingDelimiter(string code, int openIndex, char open, char close)
		{
			if (openIndex < 0 || openIndex >= code.Length || code[openIndex] != open) return -1;
			int depth = 0;
			for (int i = openIndex; i < code.Length; i++)
			{
				if (code[i] == open) depth++;
				else if (code[i] == close && --depth == 0) return i;
			}
			return -1;
		}

		static string HashFieldName(string api, string literal)
		{
			string kind = api.StartsWith("Shader.", StringComparison.Ordinal) ? "ShaderId" : "AnimatorHash";
			uint hash = 2166136261;
			unchecked
			{
				foreach (char c in literal) hash = (hash ^ c) * 16777619;
			}
			string fragment = Regex.Replace(literal.Trim('"'), @"[^A-Za-z0-9]+", "_").Trim('_');
			if (fragment.Length > 16) fragment = fragment.Substring(0, 16);
			if (fragment.Length == 0) fragment = "Literal";
			return "__po" + kind + "_" + fragment + "_" + hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
		}

		static bool TryRewriteLiteralHashCalls(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var calls = FindLiteralHashCalls(source);
			if (calls.Count == 0) return false;
			var unique = calls.GroupBy(c => c.Api + "\n" + c.Argument, StringComparer.Ordinal).Select(g => g.First()).ToList();
			var members = new StringBuilder();
			foreach (var call in unique)
				members.Append("\t\tprivate static readonly int ").Append(call.FieldName).Append(" = ").Append(call.Api).Append('(').Append(call.Argument).AppendLine(");");
			int insertion = range.CloseBrace;
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			string prefix = insertion > 0 && source[insertion - 1] != '\n' ? newline : string.Empty;
			string membersText = members.ToString().Replace("\r\n", "\n").Replace("\n", newline);
			rewritten = rewritten.Insert(insertion, prefix + newline + membersText);
			foreach (var call in calls.OrderByDescending(c => c.Start))
				rewritten = rewritten.Remove(call.Start, call.Length).Insert(call.Start, call.FieldName);
			return true;
		}

		static List<TouchLoop> FindSupportedInputTouchesLoops(string source)
		{
			var loops = new List<TouchLoop>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return loops;
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				var candidates = Regex.Matches(body, @"\bforeach\s*\(\s*(?<type>Touch|var)\s+(?<var>[A-Za-z_][A-Za-z0-9_]*)\s+in\s+Input\s*\.\s*touches\s*\)");
				int arrayReferences = k_InputTouches.Matches(body).Count;
				if (candidates.Count == 0 || arrayReferences != candidates.Count) continue;
				int candidateIndex = 0;
				foreach (Match match in candidates)
				{
					int headerStart = method.BodyStart + match.Index;
					int headerEnd = headerStart + match.Length;
					int openBrace = headerEnd;
					while (openBrace < method.BodyEnd && char.IsWhiteSpace(code[openBrace])) openBrace++;
					if (openBrace >= method.BodyEnd || code[openBrace] != '{') { candidateIndex++; continue; }
					int closeBrace = FindMatchingBrace(code, openBrace);
					if (closeBrace <= openBrace || closeBrace > method.BodyEnd) { candidateIndex++; continue; }
					string loopBody = code.Substring(openBrace + 1, closeBrace - openBrace - 1);
					if (k_Lambda.IsMatch(loopBody) || Regex.IsMatch(loopBody, @"\bdelegate\b|\byield\b|\bgoto\b")) { candidateIndex++; continue; }
					string indexName = "__projectOptimizerTouchIndex" + (candidateIndex == 0 ? string.Empty : "_" + candidateIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
					while (Regex.IsMatch(body, @"\b" + Regex.Escape(indexName) + @"\b")) indexName += "_";
					loops.Add(new TouchLoop { HeaderStart = headerStart, HeaderLength = match.Length, OpenBrace = openBrace, ElementType = match.Groups["type"].Value, VariableName = match.Groups["var"].Value, IndexName = indexName, Method = method.Method, Line = LineOf(source, headerStart) });
					candidateIndex++;
				}
			}
			return loops;
		}

		static bool TryRewriteInputTouchesLoops(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var loops = FindSupportedInputTouchesLoops(source);
			if (loops.Count == 0) return false;
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			foreach (var loop in loops.OrderByDescending(l => l.OpenBrace))
			{
				string header = "for (int " + loop.IndexName + " = 0; " + loop.IndexName + " < Input.touchCount; " + loop.IndexName + "++)";
				rewritten = rewritten.Remove(loop.HeaderStart, loop.HeaderLength).Insert(loop.HeaderStart, header);
				// Insert the per-iteration touch local immediately after the opening brace. The existing
				// whitespace is retained; conventional multi-line bodies remain correctly indented.
				int open = loop.OpenBrace + (header.Length - loop.HeaderLength);
				int cursor = open + 1;
				while (cursor < rewritten.Length && (rewritten[cursor] == ' ' || rewritten[cursor] == '\t')) cursor++;
				string declaration = loop.ElementType + " " + loop.VariableName + " = Input.GetTouch(" + loop.IndexName + ");";
				if (cursor < rewritten.Length && rewritten[cursor] == '\r')
				{
					cursor++;
					if (cursor < rewritten.Length && rewritten[cursor] == '\n') cursor++;
					int indentStart = cursor;
					while (cursor < rewritten.Length && (rewritten[cursor] == ' ' || rewritten[cursor] == '\t')) cursor++;
					string indent = rewritten.Substring(indentStart, cursor - indentStart);
					rewritten = rewritten.Insert(cursor, declaration + newline + indent);
				}
				else if (cursor < rewritten.Length && rewritten[cursor] == '\n')
				{
					cursor++;
					int indentStart = cursor;
					while (cursor < rewritten.Length && (rewritten[cursor] == ' ' || rewritten[cursor] == '\t')) cursor++;
					string indent = rewritten.Substring(indentStart, cursor - indentStart);
					rewritten = rewritten.Insert(cursor, declaration + newline + indent);
				}
				else
					rewritten = rewritten.Insert(open + 1, " " + declaration + " ");
			}
			return true;
		}

		// Review actions are offered only for precise, explicitly selected edits. They remain
		// Review-kind options: the user must choose them, and the engine's source backup/rollback
		// contract remains in force. Unsupported patterns get no option instead of a fabricated fix.
		static List<FixOption> BuildSceneSearchReviewOptions(string assetPath)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document) || !TryRewriteSimpleSceneSearchCache(document.Text, out _)) return options;
			options.Add(new FixOption {
				Label = "Cache supported generic scene-search calls",
				ResultDescription = "Adds lazy cached accessors for supported zero-argument generic Unity object searches in per-frame callbacks.",
				Warning = "Review Fix: caching preserves the first selected instance rather than repeating Unity's selection each call. Confirm that retaining/reacquiring this object matches scene lifetime and selection requirements.",
				Risk = FixRisk.High,
				Apply = ApplySimpleSceneSearchCacheFix,
				Verify = VerifySimpleSceneSearchCacheFix
			});
			return options;
		}

		static List<SourceEdit> FindSimpleSceneSearchCandidates(string source)
		{
			var edits = new List<SourceEdit>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return edits;
			var pattern = new Regex(@"(?<![A-Za-z0-9_.])(?<call>(?:(?:UnityEngine\s*\.\s*)?Object\s*\.\s*)?(?<method>FindObjectOfType|FindFirstObjectByType|FindAnyObjectByType)\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_]*)\s*>\s*\(\s*\))");
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				foreach (Match match in pattern.Matches(body))
				{
					string call = match.Groups["call"].Value;
					string type = match.Groups["type"].Value;
					string key = type + "|" + call;
					uint hash = 2166136261;
					unchecked { foreach (char c in key) hash = (hash ^ c) * 16777619; }
					edits.Add(new SourceEdit { Start = method.BodyStart + match.Index, Length = match.Length, Replacement = call, Name = type + "|" + hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), Line = LineOf(source, method.BodyStart + match.Index) });
				}
			}
			var generatedNames = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var edit in edits)
			{
				string name = "__poSceneSearch_" + edit.Name.Replace('|', '_') + "_Get";
				if (generatedNames.TryGetValue(name, out string existing) && !string.Equals(existing, edit.Name, StringComparison.Ordinal)) return new List<SourceEdit>();
				generatedNames[name] = edit.Name;
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(name) + @"\b")) return new List<SourceEdit>();
			}
			return edits;
		}

		static bool TryRewriteSimpleSceneSearchCache(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var calls = FindSimpleSceneSearchCandidates(source);
			if (calls.Count == 0) return false;
			var unique = calls.GroupBy(e => e.Name, StringComparer.Ordinal).Select(g => g.First()).ToList();
			var members = new StringBuilder();
			foreach (var call in unique)
			{
				string[] identity = call.Name.Split('|');
				string safeName = call.Name.Replace('|', '_');
				string field = "__poSceneSearch_" + safeName + "_Value";
				string accessor = "__poSceneSearch_" + safeName + "_Get";
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(field) + @"\b")) return false;
				members.Append("\t\tprivate ").Append(identity[0]).Append(' ').Append(field).AppendLine(";");
				members.Append("\t\tprivate ").Append(identity[0]).Append(' ').Append(accessor).AppendLine("()");
				members.AppendLine("\t\t{");
				members.Append("\t\t\tif ((object)").Append(field).Append(" == null || ((UnityEngine.Object)(object)").Append(field).AppendLine(") == null)");
				members.Append("\t\t\t\t").Append(field).Append(" = ").Append(call.Replacement).AppendLine(";");
				members.Append("\t\t\treturn ").Append(field).AppendLine(";");
				members.AppendLine("\t\t}");
			}
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			string prefix = range.CloseBrace > 0 && source[range.CloseBrace - 1] != '\n' ? newline : string.Empty;
			string memberText = members.ToString().Replace("\r\n", "\n").Replace("\n", newline);
			rewritten = rewritten.Insert(range.CloseBrace, prefix + newline + memberText);
			foreach (var call in calls.OrderByDescending(e => e.Start))
				rewritten = rewritten.Remove(call.Start, call.Length).Insert(call.Start, "__poSceneSearch_" + call.Name.Replace('|', '_') + "_Get()");
			return true;
		}

		static void ApplySimpleSceneSearchCacheFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR002", TryRewriteSimpleSceneSearchCache);

		static bool VerifySimpleSceneSearchCacheFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindSimpleSceneSearchCandidates(source).Count == 0);

		static List<FixOption> BuildDebugLogReviewOptions(string assetPath)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document) || !TryRewriteStandaloneDebugLogLines(document.Text, out _)) return options;
			options.Add(new FixOption {
				Label = "Remove standalone Debug logging statements",
				ResultDescription = "Removes only standalone, single-line Debug.Log-family statements within a supported MonoBehaviour class.",
				Warning = "Review Fix: method arguments can have side effects and the messages may be operationally important. Inspect every selected line before applying.",
				Risk = FixRisk.High,
				Apply = ApplyStandaloneDebugLogFix,
				Verify = VerifyStandaloneDebugLogFix
			});
			return options;
		}

		static bool TryRewriteStandaloneDebugLogLines(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var edits = new List<SourceEdit>();
			var callPattern = new Regex(@"(?<![A-Za-z0-9_.])(?:(?:UnityEngine\s*\.\s*)?Debug\s*\.\s*)(?:Log|LogWarning|LogError|LogException|LogFormat)\s*\(");
			foreach (Match match in callPattern.Matches(code, range.OpenBrace + 1))
			{
				if (match.Index >= range.CloseBrace) break;
				int open = match.Index + match.Length - 1;
				int close = FindMatchingDelimiter(code, open, '(', ')');
				if (close <= open || close >= range.CloseBrace) continue;
				int after = close + 1;
				while (after < range.CloseBrace && (code[after] == ' ' || code[after] == '\t' || code[after] == '\r')) after++;
				if (after >= range.CloseBrace || code[after] != ';') continue;
				int lineStart = source.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
				int lineEnd = after + 1;
				while (lineEnd < source.Length && (source[lineEnd] == ' ' || source[lineEnd] == '\t')) lineEnd++;
				if (lineEnd < source.Length && source[lineEnd] == '\r') lineEnd++;
				if (lineEnd < source.Length && source[lineEnd] == '\n') lineEnd++;
				// A standalone statement occupies its whole line; don't delete multiple statements together.
				if (!string.IsNullOrWhiteSpace(code.Substring(lineStart, match.Index - lineStart))) continue;
				if (!string.IsNullOrWhiteSpace(code.Substring(after + 1, (lineEnd > after + 1 && source[lineEnd - 1] == '\n' ? lineEnd - 1 : lineEnd) - (after + 1)))) continue;
				edits.Add(new SourceEdit { Start = lineStart, Length = lineEnd - lineStart });
			}
			if (edits.Count == 0) return false;
			foreach (var edit in edits.OrderByDescending(e => e.Start)) rewritten = rewritten.Remove(edit.Start, edit.Length);
			return true;
		}

		static void ApplyStandaloneDebugLogFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR005", TryRewriteStandaloneDebugLogLines);

		static bool VerifyStandaloneDebugLogFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && !TryRewriteStandaloneDebugLogLines(source, out _));

		static List<FixOption> BuildTransformFindReviewOptions(string assetPath)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document)) return options;
			if (!TryRewriteTransformFindCache(document.Text, out _)) return options;
			options.Add(new FixOption {
				Label = "Cache exact literal Transform.Find paths",
				ResultDescription = "Adds lazy cached Transform accessors for supported literal paths in per-frame callbacks.",
				Warning = "Review Fix: cached paths do not automatically track arbitrary runtime hierarchy edits. Confirm the target hierarchy is stable.",
				Risk = FixRisk.High,
				Apply = ApplyTransformFindCacheFix,
				Verify = VerifyTransformFindCacheFix
			});
			return options;
		}

		static List<SourceEdit> FindTransformFindCacheCandidates(string source)
		{
			var edits = new List<SourceEdit>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return edits;
			var callStartPattern = new Regex(@"\btransform\s*\.\s*Find\s*\(");
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				foreach (Match match in callStartPattern.Matches(body))
				{
					int start = method.BodyStart + match.Index;
					int open = start + match.Length - 1;
					int close = FindMatchingDelimiter(code, open, '(', ')');
					if (close <= open || close >= method.BodyEnd) continue;
					string argument = source.Substring(open + 1, close - open - 1);
					Match literal = Regex.Match(argument, @"^\s*(?<literal>""(?:\\.|[^""\\])*"")\s*$");
					if (!literal.Success) continue;
					edits.Add(new SourceEdit { Start = start, Length = close - start + 1, Replacement = literal.Groups["literal"].Value, Name = method.Method, Line = LineOf(source, start) });
				}
			}
			return edits;
		}

		static bool TryRewriteTransformFindCache(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var calls = FindTransformFindCacheCandidates(source);
			if (calls.Count == 0) return false;
			var unique = calls.GroupBy(c => c.Replacement, StringComparer.Ordinal).Select(g => g.First()).ToList();
			var members = new StringBuilder();
			var names = new Dictionary<string, string>(StringComparer.Ordinal);
			var fieldOwners = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var call in unique)
			{
				string field = TransformFindField(call.Replacement);
				if (fieldOwners.TryGetValue(field, out string owner) && !string.Equals(owner, call.Replacement, StringComparison.Ordinal)) return false;
				fieldOwners[field] = call.Replacement;
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(field) + @"\b") || Regex.IsMatch(code, @"\b" + Regex.Escape(field + "_Get") + @"\b")) return false;
				names[call.Replacement] = field;
				members.Append("\t\tprivate Transform ").Append(field).AppendLine(";");
				members.Append("\t\tprivate Transform ").Append(field).AppendLine("_Get()");
				members.AppendLine("\t\t{");
				members.Append("\t\t\tif ((object)").Append(field).Append(" == null || ").Append(field).AppendLine(" == null)");
				members.Append("\t\t\t\t").Append(field).Append(" = transform.Find(").Append(call.Replacement).AppendLine(");");
				members.Append("\t\t\treturn ").Append(field).AppendLine(";");
				members.AppendLine("\t\t}");
			}
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			string prefix = range.CloseBrace > 0 && source[range.CloseBrace - 1] != '\n' ? newline : string.Empty;
			string memberText = members.ToString().Replace("\r\n", "\n").Replace("\n", newline);
			rewritten = rewritten.Insert(range.CloseBrace, prefix + newline + memberText);
			foreach (var call in calls.OrderByDescending(c => c.Start))
				rewritten = rewritten.Remove(call.Start, call.Length).Insert(call.Start, names[call.Replacement] + "_Get()");
			return true;
		}

		static string TransformFindField(string literal)
		{
			uint hash = 2166136261;
			unchecked { foreach (char c in literal) hash = (hash ^ c) * 16777619; }
			return "__poTransformFind_" + hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
		}

		static void ApplyTransformFindCacheFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR011", TryRewriteTransformFindCache);

		static bool VerifyTransformFindCacheFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindTransformFindCacheCandidates(source).Count == 0);

		static List<FixOption> BuildWaitInstructionReviewOptions(string assetPath)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document)) return options;
			if (!TryRewriteConstantWaitInstructions(document.Text, out _)) return options;
			options.Add(new FixOption {
				Label = "Cache constant WaitForSeconds instructions",
				ResultDescription = "Replaces literal-duration WaitForSeconds/WaitForSecondsRealtime constructions with static readonly instances.",
				Warning = "Review Fix: apply only when the wait is a constant duration and sharing the wait instruction across coroutine instances matches the intended timing.",
				Risk = FixRisk.Medium,
				Apply = ApplyConstantWaitFix,
				Verify = VerifyConstantWaitFix
			});
			return options;
		}

		static List<SourceEdit> FindConstantWaitEdits(string source)
		{
			var edits = new List<SourceEdit>();
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return edits;
			var validNumber = @"(?:\d+(?:\.\d*)?|\.\d+)(?:[fF])?";
			var candidates = new Regex(@"new\s+(?<type>WaitForSeconds|WaitForSecondsRealtime)\s*\(\s*(?<number>" + validNumber + @")\s*\)");
			foreach (var method in GetPerFrameBodies(code, range))
			{
				string body = code.Substring(method.BodyStart, method.BodyEnd - method.BodyStart);
				foreach (Match match in candidates.Matches(body))
				{
					int start = method.BodyStart + match.Index;
					string raw = source.Substring(start, match.Length);
					string key = match.Groups["type"].Value + "_" + match.Groups["number"].Value;
					edits.Add(new SourceEdit { Start = start, Length = match.Length, Replacement = key, Name = method.Method, Line = LineOf(source, start) });
				}
			}
			return edits;
		}

		static bool TryRewriteConstantWaitInstructions(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var edits = FindConstantWaitEdits(source);
			if (edits.Count == 0) return false;
			var keys = edits.Select(e => e.Replacement).Distinct(StringComparer.Ordinal).ToList();
			var names = new Dictionary<string, string>(StringComparer.Ordinal);
			var members = new StringBuilder();
			foreach (string key in keys)
			{
				var match = Regex.Match(key, @"^(?<type>WaitForSeconds(?:Realtime)?)_(?<number>(?:\d+(?:\.\d*)?|\.\d+)(?:[fF])?)$");
				if (!match.Success) return false;
				string field = "__po" + match.Groups["type"].Value + "_" + match.Groups["number"].Value.Replace('.', '_').Replace('-', '_');
				if (Regex.IsMatch(code, @"\b" + Regex.Escape(field) + @"\b")) return false;
				names[key] = field;
				members.Append("\t\tprivate static readonly ").Append(match.Groups["type"].Value).Append(' ').Append(field).Append(" = new ").Append(match.Groups["type"].Value).Append('(').Append(match.Groups["number"].Value).AppendLine(");");
			}
			string newline = source.Contains("\r\n") ? "\r\n" : "\n";
			string prefix = source[range.CloseBrace - 1] != '\n' ? newline : string.Empty;
			string memberText = members.ToString().Replace("\r\n", "\n").Replace("\n", newline);
			rewritten = rewritten.Insert(range.CloseBrace, prefix + newline + memberText);
			foreach (var edit in edits.OrderByDescending(e => e.Start))
				rewritten = rewritten.Remove(edit.Start, edit.Length).Insert(edit.Start, names[edit.Replacement]);
			return true;
		}

		static void ApplyConstantWaitFix(FixContext context)
			=> ApplySourceRewrite(context, "SCR016", TryRewriteConstantWaitInstructions);

		static bool VerifyConstantWaitFix(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && FindConstantWaitEdits(source).Count == 0);

		static List<FixOption> BuildRemoveCallReviewOptions(string assetPath, string ruleId)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document)) return options;
			if (!TryRewriteReviewCallLines(document.Text, ruleId, out _)) return options;
			string label = ruleId == "SCR021" ? "Remove standalone Physics.SyncTransforms calls" : "Remove standalone GC.Collect calls";
			string warning = ruleId == "SCR021"
				? "Review Fix: physics queries may depend on transform synchronization. Verify the physics visibility/order contract before applying."
				: "Review Fix: explicit collection timing may be intentional. Apply only after reviewing the collection policy and measured frame-time impact.";
			options.Add(new FixOption { Label = label, ResultDescription = "Removes only standalone direct-call statements that occupy their own source line.", Warning = warning, Risk = FixRisk.High,
				Apply = c => ApplySourceRewrite(c, ruleId, (string source, out string rewritten) => TryRewriteReviewCallLines(source, ruleId, out rewritten)),
				Verify = c => VerifySourceRewrite(c, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && !TryRewriteReviewCallLines(source, ruleId, out _)) });
			return options;
		}

		static bool TryRewriteReviewCallLines(string source, string ruleId, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var bodies = GetPerFrameBodies(code, range);
			var edits = new List<SourceEdit>();
			var pattern = ruleId == "SCR021"
				? new Regex(@"(?m)^[ \t]*(?:Physics|Physics2D)\s*\.\s*SyncTransforms\s*\(\s*\)\s*;[ \t]*(?:\r?\n|$)")
				: new Regex(@"(?m)^[ \t]*GC\s*\.\s*Collect\s*\(\s*\)\s*;[ \t]*(?:\r?\n|$)");
			foreach (var body in bodies)
			{
				string fragment = code.Substring(body.BodyStart, body.BodyEnd - body.BodyStart);
				foreach (Match match in pattern.Matches(fragment))
					edits.Add(new SourceEdit { Start = body.BodyStart + match.Index, Length = match.Length });
			}
			if (edits.Count == 0) return false;
			foreach (var edit in edits.OrderByDescending(e => e.Start))
				rewritten = rewritten.Remove(edit.Start, edit.Length);
			return true;
		}

		static List<FixOption> BuildSharedMaterialReviewOptions(string assetPath)
		{
			var options = new List<FixOption>();
			if (!TryReadSourceDocument(assetPath, out var document) || !TryRewriteToSharedMaterialAccess(document.Text, out _)) return options;
			options.Add(new FixOption {
				Label = "Use shared material accessors",
				ResultDescription = "Replaces .material/.materials reads and writes with their sharedMaterial/sharedMaterials counterparts.",
				Warning = "Review Fix: shared material writes can affect every renderer using the asset. Confirm shared ownership semantics before applying.",
				Risk = FixRisk.High,
				Apply = c => ApplySourceRewrite(c, "SCR022", TryRewriteToSharedMaterialAccess),
				Verify = VerifySharedMaterialAccess
			});
			return options;
		}

		static bool TryRewriteToSharedMaterialAccess(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			var bodies = GetPerFrameBodies(code, range);
			var edits = new List<SourceEdit>();
			var propertyPattern = new Regex(@"(?<receiver>\b(?:Renderer|renderer|SkinnedMeshRenderer|skinnedMeshRenderer)|\bGetComponent\s*<\s*(?:Renderer|SkinnedMeshRenderer)\s*>\s*\(\s*\))\.(?<property>materials|material)\b");
			foreach (var body in bodies)
			{
				string fragment = code.Substring(body.BodyStart, body.BodyEnd - body.BodyStart);
				foreach (Match match in propertyPattern.Matches(fragment))
				{
					string property = match.Groups["property"].Value;
					string replacement = match.Groups["receiver"].Value + (property == "materials" ? ".sharedMaterials" : ".sharedMaterial");
					edits.Add(new SourceEdit { Start = body.BodyStart + match.Index, Length = match.Length, Replacement = replacement });
				}
			}
			if (edits.Count == 0) return false;
			foreach (var edit in edits.OrderByDescending(e => e.Start))
				rewritten = rewritten.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
			return true;
		}

		static bool VerifySharedMaterialAccess(FixContext context)
			=> VerifySourceRewrite(context, source => TryGetSingleMonoBehaviourClass(source, out _, out _) && !TryRewriteToSharedMaterialAccess(source, out _));

		static void RouteOnGuiIssueToDiagnosticOnly(OptimizationIssue issue)
		{
			if (issue == null) return;
			issue.FixKind = FixKind.None;
			issue.Fix = null;
			issue.Verify = null;
			issue.Options.Clear();
			issue.Risk = FixRisk.Unspecified;
			issue.BackupRequired = false;
			issue.Confidence = OptimizationConfidence.Diagnostic;
			issue.RecommendationConfidence = OptimizationConfidence.Diagnostic;
			issue.AssetEvidencePath = null;
			issue.AssetEvidenceSignature = null;
			issue.EvidenceSignature = null;
			issue.EvidenceState = EvidenceState.Unconfirmed;
			issue.FixContractSignature = null;
			issue.FixHint = "No generic automatic fix for non-empty or structurally ambiguous OnGUI callbacks.";
			issue.Note("The source does not prove a safe empty callback rewrite; no action is offered for this issue instance.", "Automatic-fix safety gate");
		}

		static bool TryRewriteEmptyOnGui(string source, out string rewritten)
		{
			rewritten = source;
			if (!TryGetSingleMonoBehaviourClass(source, out string code, out var range)) return false;
			string classBody = code.Substring(range.OpenBrace + 1, range.CloseBrace - range.OpenBrace - 1);
			int bodyOffset = range.OpenBrace + 1;
			var matches = Regex.Matches(classBody, @"(?m)^[ \t]*private\s+void\s+OnGUI\s*\(\s*\)\s*\{");
			var edits = new List<SourceEdit>();
			foreach (Match match in matches)
			{
				int absolute = bodyOffset + match.Index;
				if (HasAttributeOrXmlDocAbove(source, code, absolute)) continue;
				int open = absolute + match.Length - 1;
				int close = FindMatchingBrace(code, open);
				if (close <= open || close > range.CloseBrace) continue;
				// Use original source here: comments inside an otherwise empty callback can document
				// intentional lifecycle/reflection behavior and must not be deleted as if they were code whitespace.
				if (!string.IsNullOrWhiteSpace(source.Substring(open + 1, close - open - 1))) continue;
				// Preprocessor branches make the callback's effective presence configuration-dependent.
				if (Regex.IsMatch(classBody, @"(?m)^[ \t]*#(?:if|elif|else|endif)\b")) return false;
				int trailingLineEnd = source.IndexOf('\n', close + 1);
				if (trailingLineEnd < 0) trailingLineEnd = source.Length;
				if (!string.IsNullOrWhiteSpace(source.Substring(close + 1, trailingLineEnd - close - 1))) continue;
				int lineStart = source.LastIndexOf('\n', Math.Max(0, absolute - 1)) + 1;
				int end = close + 1;
				while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
				if (end < source.Length && source[end] == '\r') end++;
				if (end < source.Length && source[end] == '\n') end++;
				edits.Add(new SourceEdit { Start = lineStart, Length = end - lineStart });
			}
			if (edits.Count == 0) return false;
			foreach (var edit in edits.OrderByDescending(e => e.Start)) rewritten = rewritten.Remove(edit.Start, edit.Length);
			return true;
		}

		// ----- end analyzer-local helpers -----------------------------------------------------

	}
}
