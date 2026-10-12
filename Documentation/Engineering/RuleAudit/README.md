# ProjectOptimizer Rule Audit

**Updated:** 2026-10-10  
**Inventory:** 328 historical IDs in consistent order (322 registered RuleDefs and six legacy aliases). `SHD011` remains resolvable for compatibility but does not emit a standalone finding.

## Files

- `ProjectOptimizer_Rule_Audit_Overview.csv` — per-rule recommendation, rationale, evidence and applicability.
- `ProjectOptimizer_Rule_Audit_Technical_Reference.csv` — detector/source notes and fix/verification contracts.
- `ProjectOptimizer_Rule_Audit_Checklist.csv` — compact rule status view.
- `ProjectOptimizer_Rule_Overlap_Matrix.csv` — canonical disposition, retained coverage, and remaining verification for each overlap group.

The master CSVs retain one row per historical ID for stable joins and user-preference provenance. Their overlap columns point to the matrix; use the matrix as the sole detailed source for group-level conditions and disposition instead of repeating those descriptions in every rule row. Separate batch logs are not shipped because they duplicate the master data.

## Compatibility mappings

The six old IDs are aliases, not independent active rules:

- `PRJ077 → PRJ014`
- `PRJ078 → PRJ015`
- `PRJ081 → PRJ063`
- `PAS0031 → PRJ031`
- `PAS1007 → PRJ008`
- `VID007 → VID001`

`SHD011` is different: its historical definition remains resolvable, but it no longer emits a separately counted finding. These records exist for compatibility and audit traceability; use the canonical rule row for current detector/action behavior.

## Interpretation

Severity and fix recommendations are target-state proposals unless the current implementation fields explicitly say otherwise. Read external-source status literally: a Unity API reference establishes a mechanism, not the impact or safety of a mutation in a particular project. Project-specific conclusions may need target settings, reachability, serialized references, build reports, profiler evidence, or visual/platform tests.

`User_Preference_*` and `User_Preferred_*` fields are authoritative policy. Preserve the user preference and rationale; keep it separate from executable fix status and validation results. The preferred `Verified` state on `PRJ014` and `PRJ015` is not proof their automatic paths are ready: they remain gated on the evidence, action, and verifier requirements recorded on the canonical rows.

## Validation status

The package owner confirmed that the current package compiles in Unity. EditMode/PlayMode Test Runner execution, importer mutation on representative assets, BuildReport fixtures, Addressables Analyze, shader-variant builds, visual regression, runtime loading tests and target-device profiling are not confirmed. Static/source and audit-integrity checks are not substitutes for those tests.
