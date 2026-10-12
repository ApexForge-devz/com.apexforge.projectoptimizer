## Merged FixKind session audit (2026-10-11)

The approved analyzer implementation sessions 01 and 03–11 were merged into package version 1.1.6. Session 02 was intentionally skipped; its 30 rules remain unreviewed. See `Session_Merge/SESSION_MERGE_REPORT.md`, `FixKind_Assessment_Merged.csv`, `FixKind_Source_Reconciliation.csv`, and `Session_Merge_Status.csv` for the merge decisions, ownership and current-versus-target FixKind state. The active catalog registration is authoritative for current FixKind; proposed target classifications do not imply missing shared prerequisites are implemented. Unity compilation and EditMode verification are still required after the merge.

# ProjectOptimizer Rule Audit

**Updated:** 2026-10-11
**Current inventory:** 322 canonical RuleDefs and four retained runtime aliases. PAS, PAA and PAP each use one contiguous, three-digit suffix sequence; every canonical rule ID has exactly three numeric suffix digits. The migration ledger is versioned and distinguishes schema 0 → 2 history from schema 1 → 2 normalization. `SHD011` remains resolvable for compatibility but does not emit a standalone finding.

## Files

- `ProjectOptimizer_Rule_Audit_Overview.csv` — per-rule recommendation, rationale, evidence and applicability.
- `ProjectOptimizer_Rule_Audit_Technical_Reference.csv` — detector/source notes and fix/verification contracts.
- `ProjectOptimizer_Rule_Audit_Checklist.csv` — compact rule status view.
- `ProjectOptimizer_Rule_Overlap_Matrix.csv` — canonical disposition, retained coverage, and remaining verification for each overlap group.

The master CSVs contain one row per current canonical rule plus the four retained runtime aliases. The migration ledger separately preserves the prior catalog IDs and records schema-specific IDs that must be discarded when their numeric forms are reused, including the short forms introduced by the current PAS/PAA/PAP compaction. The master overlap columns point to the matrix; use the matrix as the sole detailed source for group-level conditions and disposition. Separate batch logs are not shipped because they duplicate the master data.

## Compatibility mappings

The four runtime aliases resolve previous IDs that have not been reassigned:

- `PRJ077 → PRJ013`
- `PRJ078 → PRJ014`
- `PRJ081 → PRJ059`
- `PAS0031 → PRJ030`

`PAS1007` is schema-sensitive: in schema 0 it identifies a retired mipmap alias that migrates to `PRJ008`; in schema 1 it identified the URP MSAA rule and migrates to `PAS027`. In schema 0, `VID007` migrates to `VID001`; in schema 1, `VID007` is the WebGL VideoClip rule and remains `VID007`. The versioned migration ledger distinguishes these meanings. Schema-0 reclaimed numbers with no prior rule are discarded rather than applied to newly assigned rules. Old scan snapshots are discarded and require a fresh scan.

`SHD011` remains in the catalog for compatibility but emits no standalone finding. Use the active canonical rule row for current detector/action behavior.

## Interpretation

Severity and fix recommendations are target-state proposals unless the current implementation fields explicitly say otherwise. Read external-source status literally: a Unity API reference establishes a mechanism, not the impact or safety of a mutation in a particular project. Project-specific conclusions may need target settings, reachability, serialized references, build reports, profiler evidence, or visual/platform tests.

`User_Preference_*` and `User_Preferred_*` fields are authoritative policy. Preserve the user preference and rationale; keep it separate from executable fix status and validation results. The preferred `Verified` state on `PRJ013` and `PRJ014` is not proof their automatic paths are ready: they remain gated on the evidence, action, and verifier requirements recorded on the canonical rows.

## Validation status

The immediately preceding package compiled in Unity, but this flattened rule-ID migration has not yet been compiled or tested in Unity. Static migration and audit-integrity checks passed; EditMode/PlayMode Test Runner execution, importer mutation on representative assets, BuildReport fixtures, Addressables Analyze, shader-variant builds, visual regression, runtime loading tests and target-device profiling remain unverified.
