## 2026-10-11 — Approved high-potential AutoFixes (1.1.7)
- Promoted PRJ009, PRJ030, PRJ032, PRJ050, and PRJ064 to Verified with direct actions, stale-state preflight and deterministic postcondition checks.
- PRJ030 now explicitly orders Android APIs as Vulkan, OpenGLES3 fallback, then other unique configured APIs; it detects duplicates/non-canonical order, turns off automatic API selection and verifies the resulting order.
- PRJ032 changes Android Blit Type from Always to Auto only when automatic API selection or a configured non-Vulkan API means the setting can apply; the action rechecks the scanned API configuration and refuses stale/no-op mutations.
- PRJ050 now documents and mutates URP's exact `m_UseFastSRGBLinearConversion` property, with backup and read-back verification; this is distinct from Android SRGB Write Mode.
- SCR006 defaults to Verified only for the common safe rewrite: an exactly empty, unannotated private OnGUI callback in a single unambiguous MonoBehaviour class. Unsupported instances are downgraded to None and have no action/verifier.
- SCR016 and SCR025 were explicitly excluded and their analyzer behavior remains unchanged.
- Added regression tests for catalog contracts, exact Quality Level mutation/selection restoration and stale-state rejection, action/verifier wiring, API-order preservation/deduplication, PRJ032 applicability/stale-state preflight, PRJ064 profile-owner backup/rollback, and SCR006 rewrite/downgrade safety. Updated the master FixKind audit CSVs and source references.
- Extended FixEngine atomic rollback so ProjectSettings-phase failures also restore additional assets captured through `FixContext.RequireBackup`, protecting profile-local Dedicated Server settings.
- Static checks passed; Unity compilation, EditMode execution, and Android build/device validation remain pending.

## 2026-10-11 — Merge approved FixKind analyzer sessions (1.1.6)
- Integrated analyzer-owned source changes and session-specific regression tests from sessions 01 and 03–11; Session 02 was intentionally skipped and its 30 rules remain unreviewed.
- Merged all three rule audit CSVs by canonical rule ID, reconciled current FixKind values against active C# registrations, and added a complete merged FixKind assessment and source-reconciliation table.
- Resolved the OG029 overlap-label collision by preserving the texture group at OG029 and assigning OG033 to the distinct Project Settings relationship; expanded OG028 to reflect its updated membership.
- Reconciled the shared regression assertions for PRE004, AUD008 and AUD010, which are now Verified in their session-owned analyzer registrations.
- Retained shared-infrastructure proposals that require broader integration under the Rule Audit Session_Merge notes instead of applying unowned changes blindly.
- Source/static package consistency checks passed. Unity compilation and the full EditMode suite remain required in the Unity Editor.

## 2026-10-11 — Flatten PAS/PAA/PAP rule IDs (1.1.5)
- Flattened PAS, PAA and PAP into one contiguous sequence per prefix; all canonical rule IDs now use exactly three numeric suffix digits.
- PAS runs `PAS001`–`PAS028`; PAA runs `PAA001`–`PAA005`; PAP runs `PAP001`–`PAP002`.
- Added schema 1 → 2 migration so saved settings/baselines distinguish the active `PAS027` URP MSAA rule from schema-0 `PAS1007`, which migrates to `PRJ008`.
- Extended the migration ledger with source/target schema versions, updated audit rows and overlap references, and strengthened contiguous-numbering/migration regression tests.
- Unity compilation and EditMode verification remain pending for this package revision.

# Change Log

## 2026-10-11 — Canonical rule ID normalization (1.1.4)
- Earlier version compacted numbering within the then-separate PAS and PAA ranges; version 1.1.5 removes those separate ranges.
- Added an explicit migration ledger for 328 prior catalog IDs plus 21 unassigned numbers reclaimed by compaction; 322 canonical RuleDefs now use compact IDs and four non-conflicting runtime aliases remain.
- Added one-time migrations for saved rule preferences and baselines. Unknown old values that collide with reclaimed numbering holes are discarded instead of being applied to a different rule. Old scan snapshots are invalidated and require a fresh scan.
- Schema 0 historically reused `PAS1007` and `VID007` as aliases to `PRJ008` and `VID001`; schema 1 assigns `PAS1007` to URP MSAA and `VID007` to WebGL compatibility. Version-aware migration preserves that distinction.
- Updated the master audit CSVs, overlap matrix, source references and regression tests. Unity compilation and EditMode test execution remain separate verification checkpoints.

## 2026-10-10 — Rule catalog and finding deduplication
- Removed obsolete source placeholders for retired registrations and shortened comments to retain only active compatibility behavior.
- Preserved six historical IDs as aliases and removed their duplicate active registrations; `SHD011` remains compatibility-resolvable but emits no standalone finding.
- Consolidated duplicate findings, grouped related metrics without combining unlike values, shared Font/TextMeshPro policy, and retained evidence gates and mutation safeguards.
- Kept `ADR001`/`ADR007` separate because there is no reliable shared root-cause identity. The overlap matrix is the single detailed record of group-specific dispositions and required tests.
- Updated the three rule-audit CSVs and maintainer docs. Unity compilation has since been confirmed by the package owner; EditMode tests and real-asset/build validation remain unconfirmed.

### 2026-09-26 — Product identity/menu consolidation (1.1.3)
- Centralized package/product identity, package resolution, version lookup, and package-owned paths in `ProjectOptimizerConstants`.
- Package version is now sourced from Unity Package Manager metadata rather than a duplicated semantic-release literal.
- Reduced Project Optimizer to one Unity `MenuItem`: `Tools/Project Optimizer`.
- Moved Addressables evidence refresh, project-state diagnostics, and integrity audit actions into the EditorWindow/Command Palette.
- Added an integrity invariant that the package contains exactly one Project Optimizer `MenuItem`.

### 2026-09-26 — Unity 6 Compile/Integration Fix Release (1.1.2)

- Fixed test construction against readonly `OptimizationIssue.Options`; tests now populate the existing collection instead of assigning it.
- Fixed Unity compiler contract drift in Addressables BuildLayout rule registration and collection handling.
- Restored missing proof-pattern API references with explicit diagnostic source-pattern naming.
- Fixed runtime evidence state/import API routing, editor build-target namespace usage, and private-path access.
- Fixed package metadata ambiguity, build provenance member drift, FixEngine collection/indexing/scope errors, and persistence utility namespace references.
- Tightened cross-asset freshness to fail closed when required persisted identities are absent.
- Added/updated regression coverage for the corrected runtime evidence path.

### 2026-09-26 — Evidence/Fix Contract Hardening (1.1.0)
- Persisted findings now retain rule/analyzer/fix semantic signatures plus per-asset and cross-asset evidence identity.
- Verified execution is fail-closed on non-Fresh evidence, incomplete proof coverage, stale cross-asset state, or changed semantics.
- Addressables Analyze records per-rule execution and explicit completeness/freshness.
- Runtime evidence is tied to build identity and content/toolchain context.
- MDL013 mapping is verified by exact renderer/submesh and SourceAssetIdentifier remaps.
- Workload-dependent fixes remain Review.
- Added Android 16 KB/native-toolchain diagnostics and regression coverage.

# Change Log

## 2026-09-26
- Added persistent `AI_Context` documentation.
- Recorded automation-first product intent.
- Recorded `AUD002` as an intentional Verified/common-case optimization.
- Recorded exception-detection as the preferred response to rare edge cases.
- Recorded current architecture, invariants, rule contract, decisions, and known audit limitations.
### 2026-09-26 — Evidence/Execution Hardening (1.0.8)

- Centralized fix-contract enforcement for Review/Verified confidence, verifier, risk and backup requirements.
- Added VCS/file editability and change-window preflight before mutation.
- Corrected option-aware and overlap-aware savings accounting.
- Added Addressables Analyze evidence capture and freshness/provenance handling.
- Extended runtime evidence provenance with scenario, build GUID, optional commit ID, capture mode and warm-up metadata.
- Added build/project fingerprint compatibility checks.
- Added editor tests for the core execution contract.
- Added Unity 6 GPU-driven rendering compatibility diagnostics for MaterialPropertyBlock and per-instance rendering callbacks.


### 2026-09-26 — Verification/Evidence Hardening (1.0.9)

- Preserved verifier presence across persisted scan snapshots and prevented missing verifiers from being reconstructed as successful checks.
- Preserved executable-apply presence across persisted scan snapshots so diagnostic/manual actions cannot become executable after restoration.
- Enforced option-specific postcondition verifiers for High/Destructive Review actions.
- Corrected inherited-risk backup evaluation and strengthened runtime contract tests.
- Replaced the historical package-version integrity literal with live package metadata.
- Compared persisted BuildReport evidence with Unity's actual latest report and preserved build-start provenance when available.
- Added Addressables Analyze completeness/failure/unavailable states and explicit documented-built-in rule scope.
- Suppressed Addressables BuildLayout optimization findings when report provenance is incomplete or mismatched.
- Suppressed build-size/build-time optimization findings when BuildReport provenance is incomplete or does not match the current Unity/project/Build Profile context.
- Replaced multiplicative savings rollups with per-asset capped resource-pool accounting to avoid both impossible totals and avoidable undercounting.
- Prevented incomplete or context-unconfirmed BuildReport data from generating build-size/time optimization findings.
- Separated BuildReport evidence quality from provenance explanation and versioned the persisted schema accordingly.
- Added explicit target-platform freshness checks for runtime and Addressables evidence, and aligned the editor runtime recorder and runtime report schema to version 6.
- Added Addressables configuration data to the project evidence fingerprint.
- Added self-audit guards for persistence, build evidence, Addressables evidence, and runtime capture completeness.
- Restricted the persisted BuildReport pipeline to Unity Player build reports so a later AssetBundle build cannot silently replace the Player build baseline.
- Tightened mutation preflight to check every known affected path and re-check editability immediately before mutation.
- Made previous-build baselines require Build Profile identity instead of silently accepting profile-less reports.
- Surfaced unavailable build-context identity explicitly when suppressing build optimization findings.
