# Session 04 — ScriptAnalyzer FixKind Implementation Report

**Approval:** User approved all Phase 1 recommendations (`approve all`).
**Scope:** `ScriptAnalyzer.cs`, this session's `FixKind_Assessment.csv`, its three assigned rule CSV subsets, `Relevant_Overlap_Matrix.csv`, and one newly created session-unique NUnit test file plus `.meta`. No shared source, shared test, global master, or other session folder was modified.

## Current package follow-up (1.1.7)

The historical SCR006 ReviewFix row below describes the earlier Session 04 snapshot. In package 1.1.7, SCR006 is Verified only for an exactly empty, unannotated private OnGUI callback in one unambiguous MonoBehaviour class. Unsupported per-instance cases are downgraded to None with no action. SCR016 and SCR025 remain unchanged and Review, per the user's explicit exclusion. The registration counts below remain as historical session results, not current package counts.

## Before/after registration counts

Counts below refer to actual `ScriptAnalyzer` rule registrations reconstructed from source, not blank cells in the CSV.

| FixKind | Before | After implementation |
|---|---:|---:|
| `Verified` (AutoFix) | 0 | 4 |
| `Review` (ReviewFix) | 0 | 17 |
| `None` (NoFix/diagnostic) | 25 | 4 |
| Unknown/unassigned | 0 | 0 |
| **Total canonical rules** | **25** | **25** |

For the four `Verified` rules, source findings with no supported candidate are downgraded to `FixKind.None` at the issue level rather than advertising a Verified issue without an executable action. If supported and unsupported matches coexist in one file, only the supported subset is rewritten; the remaining broad detector evidence is retained for subsequent scans.

## Implemented behavior by rule

| Rule | Approved target | Implemented behavior / final state |
|---|---|---|
| SCR001 — Empty per-frame method | `Verified` | AutoFix removes only provably empty, private, parameterless `Update`, `LateUpdate`, or `FixedUpdate` callbacks from a single unambiguous direct `MonoBehaviour` class. Attributed, documented/comment-adjacent, non-private and unsupported declarations are excluded. Postcondition checks that no supported candidate remains. |
| SCR002 — Expensive scene search inside a per-frame method | `Review` | Opt-in ReviewFix caches supported zero-argument generic `FindObjectOfType<T>`, `FindFirstObjectByType<T>`, and `FindAnyObjectByType<T>` calls. Warning explains changed repeat-selection semantics and object-lifetime requirements. Broader scene-search detections remain. |
| SCR003 — GetComponent inside a per-frame method | `Verified` | AutoFix introduces lazy typed caches for narrow self `GetComponent<T>()` / `this.GetComponent<T>()` forms. It retries if no component is found and re-resolves when a cached Unity object is destroyed. Other receivers and component lookup variants are not transformed. |
| SCR004 — LINQ inside a per-frame method | `Review` | Diagnostic/review guidance retained. No generic source rewrite is exposed because translating arbitrary LINQ without compiler-bound types can change overload selection, deferred execution, order, short-circuiting and side effects. |
| SCR005 — Debug logging in runtime code | `Review` | Opt-in ReviewFix removes only standalone single-line `Debug.Log`, `LogWarning`, `LogError`, `LogException`, and `LogFormat` statements in a supported `MonoBehaviour` class. The option has a verifier, backup requirement, and warning to inspect argument side effects and operational value. Rule confidence was raised from `Diagnostic` to `Heuristic` so the Review option meets the review confidence floor. |
| SCR006 — OnGUI implemented in runtime code | `Review` | Opt-in ReviewFix removes only an empty, supported `OnGUI` declaration with no nearby attributes/comments/doc intent. Non-empty or unsupported forms are left unchanged. |
| SCR007 — SendMessage used instead of a direct call | `Review` | Diagnostic/review guidance retained. A source-only replacement cannot safely infer the runtime receiver set, method signature, dispatch-to-multiple-components behavior, or intended dynamic dispatch contract. |
| SCR008 — Instantiate or Destroy inside a per-frame method | `Review` | Diagnostic/review guidance retained. Pooling needs a component-specific lifetime, reset, ownership and spawn/despawn policy; an automatic source rewrite would change object lifecycle behavior. |
| SCR009 — Resources.Load in runtime code | `Review` | Diagnostic/review guidance retained. Migration requires asset mapping, load timing, ownership and release behavior. No Addressables or serialized-reference substitution is guessed. |
| SCR010 — Camera.main in a per-frame method | `None` | Diagnostic-only; source syntax does not establish a meaningful per-site benefit or a safe camera cache lifetime. |
| SCR011 — Transform.Find in a per-frame method | `Review` | Opt-in ReviewFix caches exact literal `transform.Find("path")` calls in per-frame callbacks. Dynamic paths are not changed; generated member-name collisions fail closed. The option warns that hierarchy edits can invalidate cached references. |
| SCR012 — Allocating physics query in a per-frame method | `Review` | Diagnostic/review guidance retained. A NonAlloc conversion needs a caller-owned buffer, a defined full-buffer/overflow strategy, and proof that result-count and result-order behavior remain correct. |
| SCR013 — Allocation candidate in a per-frame method | `None` | Diagnostic-only. Generic `new` syntax does not prove managed heap allocation or establish a reusable object lifetime. |
| SCR014 — foreach loop in a per-frame method | `None` | Diagnostic-only. `foreach` syntax alone does not establish allocation or a performance bottleneck. |
| SCR015 — StartCoroutine in a per-frame method | `Review` | Diagnostic/review guidance retained. A generic guard/restart/cancel policy can change intended coroutine concurrency and lifecycle behavior. |
| SCR016 — Wait instruction allocated in a per-frame method | `Review` | Opt-in ReviewFix replaces supported literal-duration `WaitForSeconds`/`WaitForSecondsRealtime` constructors with static readonly instances. Dynamic and unsupported waits remain unchanged; the user must review shared timing semantics. |
| SCR017 — String formatting in a per-frame method | `Review` | Diagnostic/review guidance retained. Moving or gating formatting depends on its consumer and update cadence. |
| SCR018 — ToString in a per-frame method | `Review` | Diagnostic/review guidance retained. Caching or moving formatting depends on receiver type, mutable state, culture, and output consumer. |
| SCR019 — Lambda/delegate expression in a per-frame method | `None` | Diagnostic-only. `=>` syntax does not prove an allocating closure; captures and delegate conversion are required to establish that behavior. |
| SCR020 — Runtime hash/ID generation in a per-frame method | `Verified` | AutoFix precomputes only exact string-literal `Animator.StringToHash` and `Shader.PropertyToID` arguments into uniquely named `static readonly int` fields. Dynamic arguments and name collisions fail closed. |
| SCR021 — Physics.SyncTransforms in a per-frame method | `Review` | Opt-in ReviewFix removes only standalone `Physics.SyncTransforms()` / `Physics2D.SyncTransforms()` statements on their own lines inside per-frame callbacks. Warning calls out immediate physics visibility and script-order dependencies. |
| SCR022 — Renderer material or material-array access in a per-frame method | `Review` | Opt-in ReviewFix changes only narrow recognized `.material`/`.materials` access forms to `.sharedMaterial`/`.sharedMaterials`. The warning explicitly states that shared writes may affect all renderers using the material. |
| SCR023 — Animator.parameters array access in a per-frame method | `Review` | Diagnostic/review guidance retained. Caching parameter metadata depends on controller/playable lifetime and has no universal source-only equivalent. |
| SCR024 — Input.touches array access in a per-frame method | `Verified` | AutoFix converts only supported braced `foreach (Touch|var item in Input.touches)` loops with no extra array references or unsupported control-flow/lambda shapes to `touchCount`/`GetTouch` indexed iteration. Other `Input.touches` detections remain diagnostic. |
| SCR025 — Explicit GC.Collect in a per-frame method | `Review` | Opt-in ReviewFix removes only standalone `GC.Collect()` statements on their own lines inside per-frame callbacks. Warning calls out deliberate collection policy and measured frame-time needs. |

## Safety and implementation details

- All four Verified registrations use high confidence, high fix risk, and require backups. The action implementations validate the `.cs` target and writable state, read only supported text encodings, preserve BOM/encoding/newline style, apply narrowly scoped transformations, request Unity reimport through `context.NeedsReimport`, and verify the source postcondition. The existing fix engine remains responsible for its backup/rollback transaction; its shared code was not changed.
- Generated member names are checked against existing code. Literal/collision checks, one-unambiguous-`MonoBehaviour` parsing, and unsupported-shape guards fail closed rather than guessing.
- Eight Review rules expose explicit ReviewFix options for patterns narrow enough to transform with a human decision: SCR002, SCR005, SCR006, SCR011, SCR016, SCR021, SCR022, and SCR025. The other Review rules remain diagnostic/review guidance only because their general fixes would require information unavailable in static source or would alter behavior without a defensible default.
- The existing SCR013/SCR016 overlap partition (`OG032`) remains intact: SCR013 excludes the dedicated Wait instruction patterns; SCR016 owns that narrower detector.
- User preference fields were checked against the approved Phase 1 assessment and preserved exactly.

## Regression coverage and checks

Added `Source_Snapshot/Tests/Editor/FixKindAuditSession04ScriptAnalyzerTests.cs` with 14 NUnit tests covering supported rewrites and negative paths for SCR001, SCR003, SCR002, SCR005, SCR011, SCR020, SCR021, SCR022, and SCR024. The `.meta` file has a new unique GUID. The four assigned rule tables contain exactly 25 canonical rows (SCR001–SCR025), each once; the overlap matrix retains the OG032 entry.

**Static checks performed:** C# lexical delimiter balance passed; source registrations reconstructed to exactly `Verified=4`, `Review=17`, `None=4`; owned CSV row identities and target types validated; approvals and user-preference preservation validated; references to source lines resolve within the source file; action/verifier wiring and `NeedsReimport` calls were checked by source inspection.

**Final validation:** C# tokenized delimiter balance passed; all 25 source registrations match the approved target types; all four owned rule tables contain SCR001–SCR025 exactly once; user-preference fields match the input archive; source line references resolve within `ScriptAnalyzer.cs`; the new test `.meta` GUID is unique against the supplied archive; expected action/verifier wiring exists; OG032 overlap preservation is recorded; and the packaged ZIP passed archive integrity and scope checks.

**Not run:** Unity compilation, NUnit/EditMode tests, full ProjectOptimizer test suites, and real-project runtime/Profiler tests. No Unity executable or C# compiler was available in this environment. Do not treat the implementation as runtime-validated until the session test file is run in the merged Unity project.


High-potential AutoFix follow-up: SCR006 now removes only exactly empty, unannotated private `OnGUI` callbacks in a uniquely identified MonoBehaviour. Comments in or beside a callback, documentation/attributes, preprocessor-conditional regions, non-empty bodies, and ambiguous source structures are preserved and routed to diagnostic-only. SCR016 and SCR025 were explicitly excluded from that follow-up.
