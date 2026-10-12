# Session 10 — FixKind Phase 2 Implementation Report

## Scope and approval

- Session: `10_Addressables_Unused_WorldData_BuildProfile_Assembly_22Rules`
- Approval: user approved all Phase 1 recommendations.
- Assigned analyzers inspected/modified: `AddressablesBuildLayoutAnalyzer.cs`, `UnusedAnalyzer.cs`, `WorldDataAnalyzer.cs`, `BuildProfileAnalyzer.cs`, `AssemblyDefinitionAnalyzer.cs`.
- Updated owned data: `FixKind_Assessment.csv`, the three session CSV subsets, and `Relevant_Overlap_Matrix.csv`.
- Added `Source_Snapshot/Tests/Editor/FixKindAuditSession10Tests.cs` plus its `.meta` file.
- No shared source, existing shared tests, global masters, rule ID mappings, or catalog bootstrap files were edited.

## FixKind counts: baseline, approved target, and actual current source state

| FixKind | Before Phase 2 source state | Approved target | Actual current source state |
|---|---:|---:|---:|
| `Verified` / AutoFix | 0 | 3 | 2 |
| `Review` / ReviewFix | 4 | 13 | 13 |
| `None` / NoFix | 18 | 6 | 7 |
| Unknown / unassigned | 0 | 0 | 0 |
| **Total** | **22** | **22** | **22** |

Actual current state reflects the in-source rule registration, not the approved target column. **ADR008 is the sole approved target not implemented**: its target remains `Verified`, but actual catalog state remains `None` because the evidence store and refresh contract referenced by the analyzer are absent from this archive. Do not merge a label-only promotion.

## Implemented changes

### Addressables

- **ADR001**: marked `Review`; exposes selectable packing modes for configured groups. The duplicate record cannot be correlated to a specific group with the available stable evidence, so nothing is preselected and the full-group impact is warned.
- **ADR002, ADR004, ADR005, ADR006**: marked `Review`; expose per-group `BundledAssetGroupSchema.BundleMode` alternatives only when the BuildLayout bundle can be associated with a configured group. Each option preflights the group/schema and Assets path, requires backup, records Undo, marks the schema dirty, requests save, and verifies the selected mode.
- **ADR003, ADR009**: marked `Review`; expose a navigation-only option that opens Unity Preferences/Settings. The user must manually enable Addressables > Debug Build Layout, perform a full Addressables content build, and rescan. The action does not toggle a global editor preference, run a build, or claim a new report exists. Unity documents this as an Addressables Editor Preferences setting and notes that report generation increases build time.
- **ADR007**: marked `Review`; opens Unity's documented Addressables Analyze window as a navigation-only option and verifies that an Addressables Analyze window is present. It does not automatically run Analyze or fix individual results.
- **ADR008**: approved target stays `Verified`, actual stays `None`. See `Shared_Changes_Proposed/Session_10_Addressables_Unused_WorldData_BuildProfile_Assembly.md`.

The packing-mode, Preferences navigation, and Analyze-window actions have not been tested against real installed Addressables package versions in Unity. The Preferences action only verifies that a Preferences/Settings window is open; a user still needs to enable the checkbox and produce a full content build. When the relevant package/API or window cannot be resolved, the action fails closed or is omitted.

### Unused asset cleanup

- **UNU001, UNU004**: registrations promoted from `Review` to `Verified` with concrete guarded trash-move actions. Normal-case deletion is only offered when the reference graph is complete and project-wide, the dynamic-loader source index is complete, no recognizable candidate literal is found in source using known loader markers, explicit Addressables registration is absent, the importer is not assigned to an AssetBundle, and asset identity/path checks pass.
- The apply path rechecks current project-wide reference-index evidence, GUID, size, last-write timestamp, source-set/timestamps, candidate dynamic-loader tokens, Addressables membership and AssetBundle assignment. It requests a backup, moves the asset to Unity/OS trash, and verifies that neither the file nor imported main asset remains. Source indexing includes `Assets`, embedded `Packages`, and `Library/PackageCache`, is invalidated on project changes, and reads source contents in batches of 32 files while yielding to the editor between batches. File-list discovery still occurs as a pre-pass and has not been performance-profiled.
- **Residual limitation**: calculated/encrypted/generated keys, external catalogs and loaders compiled only into DLLs cannot be proven absent by this source scan. They remain an explicit safety limitation; incomplete scans fail closed. Unity tests and rollback behavior remain unverified in this environment.
- **UNU003**: remains `None` and diagnostic-only. Removed the misleading destructive risk/backup default because no mutation is offered.
- **UNU002, UNU005, UNU006** and **ASM001, ASM002** stay diagnostic-only in accordance with the approved targets.

### Terrain ReviewFixes

- **TER001**: exposes lower supported heightmap resolutions.
- **TER002**: exposes lower alphamap resolutions rounded to valid multiples of 16.
- **TER003**: exposes lower detail resolutions while preserving the analyzed per-patch resolution.
- **TER004**: only offers explicitly selected removal of trailing terrain layers whose alphamap weights are zero; it does not auto-select a removal count.
- All terrain options declare high risk, backup, GUID/identity preflight, preflight against the analyzed current value, Undo, dirty/save, and a setting-specific postcondition verifier. Layer-removal options are never preselected. Actual terrain visual checks remain with the user.

### Build Profile shader stripping

- **PRJ071** remains `Review`; it exposes alternatives without preselecting a value, since no player-build/shader-variant baseline exists.
- The rule and each option are now High risk with explicit backup. An automated option is exposed only when both Build Profile and Graphics Settings owner are stable assets under `Assets/`. The action backs up both, validates their GUIDs/paths plus the target property/value, records Undo, checks `ApplyModifiedProperties`, marks dirty/requests save, and verifies the exact selected enum and owner identity.

## Overlap handling

- **OG013 — ADR001/ADR007** remains separate. The Analyze-window action does not suppress or correlate duplicate BuildLayout records. Stable cross-source result identity is unavailable, and ADR008's evidence refresh remains blocked.
- **OG024 — UNU001/UNU004** remains separate. Existing size-threshold routing and canonical IDs were retained; both branches share the same guarded cleanup implementation.

## Tests and checks

- Added session-unique NUnit coverage for all 22 actual registration states; lower-resolution option safety contracts and wrong-asset identity rejection; orphan gate exception reasons and missing/non-current reference evidence; dynamic-loader token normalization/marker detection; supported terrain resolutions; backup-supported Build Profile paths; and PRJ071 high-risk/backup metadata.
- Static checks completed: all 22 source registrations were parsed and matched to the assessment's actual `Declared_FixKind`; actual count is 2 `Verified`, 13 `Review`, 7 `None`; approved target count remains 3/13/6; the 22 assessment IDs are unique; required proposal fields are populated; all three owned CSV subsets contain 22 unique canonical rule IDs; the overlap subset contains its two assigned groups; the preference fields are identical to the approved Phase 1 artifact; C# lexical/bracket checks found no unmatched delimiters; all expected test/shared-change files exist. ZIP integrity and merge-scope checks are completed before delivery.
- **Unity compilation/EditMode tests/Player build tests were not run.** The available environment has no Unity Editor executable or C# compiler (`dotnet`, `csc`, `mcs`). This archive's source snapshot also omits several shared production definitions required to compile it standalone, notably the Addressables Analyze evidence store/snapshot, and other core catalog/fix contract definitions. A successful build cannot be inferred.

## Files changed/added within this session folder

- Modified four assigned analyzers where implementation was feasible; `AssemblyDefinitionAnalyzer.cs` was inspected and left diagnostic-only as approved.
- Modified `FixKind_Assessment.csv` and the three CSV subsets / overlap CSV.
- Added `Source_Snapshot/Tests/Editor/FixKindAuditSession10Tests.cs` and `.meta`.
- Added `Shared_Changes_Proposed/Session_10_Addressables_Unused_WorldData_BuildProfile_Assembly.md`.
- A standalone implementation report is provided separately and is not included in the session ZIP, preserving the session's source/CSV ownership boundary.

No other session directories or global master CSV files are part of the output ZIP.
