# Rules and Automation

## Rule intent
A rule should identify a real optimization opportunity, explain the evidence, and reduce developer work.

## Preferred rule contract
`Detect → classify evidence/confidence → detect exceptions → choose action → fix → verify.`

## Fix categories
- `Verified`: automatic action is sufficiently deterministic and justified.
- `Review`: a genuine project/policy/workload choice remains.
- `None`: diagnostic/opportunity only; no safe automatic mutation is established.

`Verified` does not mean universally optimal; it means the evidence supports the configured default policy.

## Rule authoring and scale
- State evidence, assumptions, and machine-detectable exceptions.
- Batch equivalent safe operations; isolate exceptions programmatically.
- Keep Review for genuine ambiguity, policy choices, insufficient evidence, and high-risk mutations.
- Require deterministic postcondition verification where practical; back up destructive/reference-sensitive changes.
- Test the common case, exceptions, stale evidence, and rollback behavior before promoting a rule to `Verified`.

## Execution contract
Review execution requires the Review confidence floor and a concrete action. High-risk Review options require a verifier and backup. Verified execution requires the automatic confidence floor, a concrete default action, fresh required evidence/signatures, preflight, and verifier. Failed postconditions roll back where supported. Savings are calculated for the selected action and capped by the affected resource pool.

## Audit source of truth
`Documentation/Engineering/RuleAudit/` contains the rule audit:
- `ProjectOptimizer_Rule_Audit_Overview.csv` — per-rule rationale, evidence, applicability, and proposed changes.
- `ProjectOptimizer_Rule_Audit_Technical_Reference.csv` — source/detector details and fix/verification contracts.
- `ProjectOptimizer_Rule_Audit_Checklist.csv` — compact status view.
- `ProjectOptimizer_Rule_Overlap_Matrix.csv` — canonical decisions and the only detailed record of overlap conditions, preserved coverage, and required tests.
- `README.md` — interpretation and validation boundaries.

The three master CSVs retain 328 historical IDs in consistent order: 322 registered RuleDefs plus six legacy aliases (mapped in the Rule Audit README). `SHD011` remains resolvable for compatibility but emits no independent finding. Historical rows support lookup/preference provenance, not an assertion that an independent detector remains active. For group details, follow `Overlap_Group` to the matrix; master CSV cells intentionally reference it rather than repeat long explanations.

## Authoritative user preferences
`User_Preference_*` and `User_Preferred_*` columns are authoritative product policy. Never silently override or delete the preference or its rationale. Keep preferred target state separate from current executable fix type and test status. If implementation is not safe or complete, record the blocker while preserving the preference and all normal evidence, preflight, backup, verification, and rollback safeguards.

The `Verified` preferences on canonical rules `PRJ014` and `PRJ015` remain authoritative. Their automatic execution must stay blocked until the required collision-lifetime/escape evidence (`PRJ014`) or sufficiently complete sensor-consumer absence evidence (`PRJ015`), action, and verifier are available.
