# Shared change proposal — Session 09 font/TMP generation transaction

## Request

Add a shared, version-gated Editor service for TextCore and TextMeshPro font optimization candidates. This session leaves FNT001–FNT005, FNT007, TMP001, TMP002 and TMP004 declared `None` for now even though their approved target is `Verified`. The required real mutation cannot be implemented safely inside only the four assigned analyzers without shared generation, transactional commit and validation primitives.

## Required service contract

1. Resolve supported creation/generation APIs for the exact installed Unity Editor and TextMeshPro package versions. Unsupported versions must fail closed.
2. Build a temporary candidate asset instead of modifying the live asset's serialized atlas settings in place.
3. Preserve all references and asset identity required by the package, including materials, shader/render mode settings, fallback links, glyph/character tables, atlas textures and sub-assets. GUID/reference changes must be explicit and tested.
4. Require a bounded/explicit text corpus for Static conversion; include localization tables, known formatted text, build content and explicitly declared runtime string sources. Unknown/user-generated/remote text prevents Static conversion.
5. Validate that all source glyphs are present after candidate generation, atlas dimensions/pages/format meet the requested target, no fallback order changes unexpectedly, and required SDF/outline/shadow/underlay fixture renders remain equivalent within documented tolerances.
6. Commit candidate and related sub-assets as one recoverable transaction only after validation; retain a restorable backup and roll back every changed asset on any failure.
7. Return deterministic postcondition evidence for the owning analyzer's rule-specific verifier. A request that cannot prove the relevant preconditions or postconditions must return a non-applicable result rather than writing partial settings.
8. Include Editor tests for version adapters, generated atlas candidates, static-corpus completeness, references, Unicode/language coverage, validation rejection and rollback after injected failure.

## Rule mapping

- FNT001/FNT007 and TMP001: choose a candidate atlas dimension/memory budget only when every required glyph and effect margin remains valid.
- FNT002/TMP004: reduce pages only through actual regeneration/repacking; do not just toggle multi-atlas flags or rewrite dimensions.
- FNT003/TMP002: static conversion is allowed only with complete corpus proof, then generate and verify the candidate; changing population enum alone is insufficient.
- FNT004: reduce padding only with regenerated atlas and render-fixture verification.
- FNT005: create a compatible single-channel/alpha candidate only when render mode and shader sampling support it; do not swap texture format on an existing generated atlas.

## Merge boundary

This is a proposal only. Do not alter shared source in Session 09. Once accepted and implemented centrally, the affected analyzer rules can be moved from declared `None` to `Verified` in a follow-up that implements and tests each specific action. Approved target FixKind in Session 09 remains recorded as `Verified`; implementation status must remain blocked until all checks are real.
