# Session 09 — Phase 2 implementation report

## Approval

User approved all 21 proposed target FixKinds. Approved targets remain separately recorded in `FixKind_Assessment.csv`; current declarations are not relabelled to match a target when a real action is unavailable.

## Before/after declared FixKind counts

| State | Before Phase 2 | After Phase 2 |
|---|---:|---:|
| Verified | 0 | 3 |
| Review | 3 | 3 |
| None | 18 | 15 |
| Unknown/unassigned | 0 | 0 |
| Total | 21 | 21 |

The after counts describe source declarations, not approved target counts. The approved target distribution remains Verified 12, Review 3, None 6. Nine approved Verified targets remain blocked pending shared font-generation transaction work: FNT001–FNT005, FNT007, TMP001, TMP002, TMP004. They intentionally remain declared None so no rule advertises an AutoFix without a concrete action and verifier.

## Implemented changes

- **CTL003 (Verified):** Added execution-time preflight and a deterministic postcondition verifier. A parameter is removed only after checking current graph usage, absence of StateMachineBehaviours, whole-project C# usage, dynamic Animator parameter usage, and a compact identifier index over relevant ForceText serialized assets. Binary/mixed serialization, unreadable serialized data, managed DLLs under `Assets/Plugins`, `Packages`, or `Library/PackageCache`, non-standard parameter names, and ambiguous source/asset references fail closed. Project-change events invalidate cached evidence; apply-time preflight forces fresh evidence. Ambiguous findings are demoted per issue to `None` with no callback.
- **CTL005 (Review):** Kept a user-approved ReviewFix. The action targets only analyzed layer indices, checks recorded layer names and IK flags at execution, declares High risk and backup, and verifies the selected layers are disabled.
- **CTL007 (Review):** Added explicit high-risk action with preflight and verifier, only for the affected controller. The preflight compares the exact ordered state paths currently flagged; because paths use layer/state names rather than opaque GUIDs, runtime validation remains pending.
- **FNT006 (Review):** Offers compatible source Fonts as explicit user-choice options; does not auto-guess by filename. Preflight checks that the target still lacks a source reference and the selected font exists; verifier confirms the committed reference.
- **FNT008 / TMP003 (Verified):** Added conservative fallback pruning. Null and repeated references are removable; glyph-subset pruning is allowed only when static population and complete readable character tables prove all candidate glyphs already exist in earlier covered assets. If no safe redundant entry exists, that individual issue is demoted to `None`. The changes require backup and have deterministic structural postcondition verifiers.
- **DEP002:** Clarified that direct fan-out count does not prove build inclusion or safe optionality.
- Added `FixKindAuditSession09Tests.cs` to check declared FixKinds and the high-risk backup contract, guard against falsely declaring the nine unimplemented font/TMP generation targets as Verified, test dynamic Animator parameter exception detection, and test null/duplicate/static-glyph-subset fallback analysis in both font analyzers.
- Added `Shared_Changes_Proposed/Session_09_Font_TMP_Animator_Dependency_21Rules.md` describing the missing shared candidate-generation/transaction service and required test contract.

## Approved targets still blocked

FNT001, FNT002, FNT003, FNT004, FNT005, FNT007, TMP001, TMP002 and TMP004 need supported TextCore/TMP font-asset creation/regeneration plus asset reference preservation, rollback and glyph/render verification. Changing serialized atlas dimensions, padding, population mode, texture format or multi-atlas flags alone is not a real AutoFix and could silently corrupt glyph coverage. Shared infrastructure was not modified due session ownership constraints.

## Known limitations

- **CTL003 uses conservative source and serialized-asset inspection.** A source mention of a candidate parameter, any dynamic Animator parameter API call, StateMachineBehaviour on the controller, non-ForceText serialization, unreadable serialized data, any managed DLL under `Assets/Plugins`, `Packages`, or `Library/PackageCache`, non-standard parameter names, or incomplete scanning blocks action. C# files and selected serialized text formats are searched under `Assets`, `Packages`, and `Library/PackageCache`; the serialized data is reduced to a compact identifier index rather than retaining every file's text. Controller assets are excluded from that index so a controller's own parameter declarations do not block the action. This intentionally limits automation to cases where potential consumers can be ruled out.
- CTL007's Review preflight compares the exact ordered state labels generated from controller layer/state-machine/state names. This is stronger than a count-only check but remains dependent on stable names/paths; the rule remains Review and runtime validation is pending.
- FNT008/TMP003 pruning is structural evidence only. No target-Player rendering or all-localization glyph test was run in this isolated environment.

## Checks run

- Python CSV integrity check: all 21 assessment rows retained; approval status set for all rows; approved target values preserved; source-declared types updated according to actual registrations.
- Static source checks: checked assigned rule registrations, action/verifier references, delimiters and rule IDs; no compiler was available.
- Final package archive was rebuilt and its full entry list, CRC integrity, and ownership boundaries were checked after creation.
- Unity compilation/EditMode tests: **not run**. This environment has no Unity Editor or C# compiler available; the new NUnit tests are authored but not executed.
- Runtime/PlayMode/render tests: **not run**.

## Cross-session dependencies

No shared infrastructure, global master CSV, other analyzer, registry/bootstrap, catalog type or shared test was edited. The only shared dependency is documented in the proposal file under `Shared_Changes_Proposed`.
