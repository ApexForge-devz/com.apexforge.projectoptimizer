# Shared change request — Session 05 MaterialShaderAnalyzer

## Request: provide fresh target-player performance evidence for MAT003

**Approved target:** `Verified` / AutoFix, subject to positive measured benefit.

**Current implementation:** `None` / NoFix. MAT003's actual `Rules.Register` value is deliberately `FixKind.None` until the shared evidence capability exists. The finding remains diagnostic-only; the approved target has not been falsely represented as implemented.

**Why required:** `ScanContext.MaterialUsage` exposes static scene/prefab renderer-slot counts only. The assigned analyzer has no target-player profile capture contract, stable profile identity, before/after measurements, or profile freshness checks. Static counts alone do not establish that enabling GPU instancing improves performance for the actual target workload. Automatically changing the catalog to `Verified` would violate the product's AutoFix contract while the action remains gated.

## Requested shared capability

Add a read-only profile-evidence contract exposed through `ScanContext` (or a shared evidence provider) with at least:

- build/profile identity and target platform/render-pipeline identity;
- matching material + shader + compatible mesh cohort identity;
- before/after draw-call/batch or CPU render-thread measurements and sample counts;
- evidence timestamp/revision and a deterministic signature tied to the assets under evaluation;
- an explicit `Fresh`, `Stale`, `Insufficient`, or `Inconclusive` state;
- regression threshold and confidence rule so an instance is eligible only when the measured benefit is repeatable and material.

## Session 05 behavior until this request is merged

- Registration remains `FixKind.None` and the issue has no automatic action.
- The analyzer may display static scene/prefab slot counts for context, but these counts do not trigger a mutation.
- `EnableGpuInstancing` and its verifier remain gated helper code and are not reachable through the action callback.
- The FixKind assessment preserves the approved target `Verified` and marks the implementation as blocked by this dependency.

## Ownership boundary

This session did not modify `ScanContext`, profile storage, the shared fix engine, global catalog mappings or any shared test file. The shared integration must be implemented and merged separately. Once available, Session 05 should expose the guarded action only with fresh matching positive profile evidence.

## Acceptance tests requested

1. Fresh positive profile evidence for the exact build target, render pipeline, shader/material and compatible mesh cohort.
2. No evidence, stale evidence and incomplete profile data.
3. Wrong target, wrong pipeline, and material/shader/mesh cohort mismatch.
4. Inconclusive or non-repeatable measurements and measured regression.
5. SRP Batcher-compatible or otherwise ineligible material.
6. Successful setting mutation, persisted-state verifier, failed postcondition and rollback.
