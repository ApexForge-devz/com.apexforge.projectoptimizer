# Facts and Invariants

## Product/runtime
- Optional Runtime collector exists for Player evidence.
- Scan settings persist in `ProjectSettings/ProjectOptimizerSettings.json`.
- Scan revision is used to identify settings-stale results.
- Scan scope supports project/folder-oriented filtering and exclusions.
- Large assets can be counted/reported without loading when over the configured size limit.

## Safety
- Verified fixes require strong evidence plus an executable default action and verifier.
- Stale findings must not be fixed.
- Destructive/reference-sensitive fixes require appropriate backup protection.
- Duplicate/unused destructive actions require complete project-wide reference evidence.
- Failed verification should trigger rollback where the fix infrastructure supports it.

## Evidence semantics
- Static evidence is not runtime proof.
- Build evidence must identify its source/provenance before being treated as current.
- Runtime evidence must distinguish Editor measurements from authoritative Player measurements.
- Estimated savings are opportunities, not guaranteed measured deltas.

## Compatibility
- Preserve public APIs and serialized data unless a change is necessary and deliberate.
- Re-check Unity APIs when version-sensitive behavior is changed.
### 2026-09-26 execution/evidence invariants

- Review execution requires the Review confidence floor; Diagnostic findings cannot reach the Review fix engine.
- Verified execution requires its automatic confidence floor and deterministic postcondition verifier.
- High-risk Review/Verified actions force backup requirements at the shared contract layer.
- Selected Review options must belong to the issue that declared them.
- Mutation preflight checks editability and blocks changes when tracked target files differ from the analyzed state.
- Effective savings come from the selected action and are capped against the applicable per-asset memory/disk pool.
- External build/runtime/Addressables evidence carries provenance/freshness state; missing identity is never presented as project-confirmed freshness.
## User preference invariants

- Rule Audit `User_Preference_*` / `User_Preferred_*` fields are authoritative product policy and take highest priority over competing recommendations.
- Never silently override, downgrade, erase, or rewrite a user-preferred fix type or its rationale during implementation, auditing, code generation, or CSV regeneration.
- Keep user-preferred target-state classification separate from current implementation state, declared/effective catalog fix type, and runtime verification status.
- If implementation cannot yet satisfy a preference, retain the preference and record the concrete blocker and required verification. Do not represent an unimplemented preference as an implemented or verified fix.
- Existing execution safeguards (fresh evidence, preflight, backup where required, executable action, deterministic postcondition verifier, and rollback where supported) remain required; report conflicts as blockers rather than silently changing the preference.

## Rule-overlap and grouped-insight invariants

- A grouped issue preserves the distinct measurements and threshold state for every displayed constituent metric; unlike measurements must not be summed or relabelled as one estimate.
- Every metric in a grouped issue must still obey its own rule enable/disable and per-asset suppression setting; a disabled constituent metric must not leak through another rule's grouped card.
- Cross-rule suppression is allowed only when the specific finding is actually reportable and covers the same underlying asset/condition. Otherwise retain independent findings.
- Material-equivalence findings are correlated with byte-identical duplicate-file findings only when every path in the material group is present in the exact hash group. Duplicate-remapper proof, backup and postcondition safeguards remain mandatory.
- BuildLayout and Addressables Analyze summaries must remain independent until a stable normalized identity proves a shared root cause; display-string resemblance is insufficient.
- ANI002 import-stage and ANI006 clip-data paths stay distinct. Potential savings that may describe the same final imported result must not be treated as additive without measured incremental evidence.
