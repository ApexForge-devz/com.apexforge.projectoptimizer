# Product Intent

## Automation philosophy
- High-confidence, low-risk optimizations should normally be `Verified` and automatic.
- `Review` is for genuine ambiguity, policy choice, or insufficient evidence.
- Detect exceptions programmatically and downgrade only affected cases.
- Do not make normal cases manual merely because unusual cases exist.
- Every automatic mutation should have a deterministic verifier when practical.

## `AUD002` intent
Short clips using Streaming should normally be changed automatically to Decompress On Load when they satisfy the configured short-clip policy.

The default policy is intentionally optimized for the common case, not rare severe-memory scenarios.

Future work may detect exceptional cases automatically using decoded size, format, platform policy, or other evidence.

Do not change `AUD002` to Review-only solely because Streaming can be useful on memory-constrained devices.

## User-workload principle
If the tool can safely make a decision from project evidence, making the user decide is a product failure, not extra safety.

## Scope principle
Prefer broad useful coverage with trustworthy automation over hundreds of shallow checks.
## Authoritative user-preference precedence

Rule Audit fields prefixed `User_Preference_` or `User_Preferred_` are authoritative and have the highest priority for product policy. They must never be silently overridden by generic best practices, an agent's independent recommendation, generated audit output, or a later refactor. Preserve the preferred fix type and its reason across code, documentation, and CSV regeneration.

This precedence governs the intended product classification; it does not permit false claims about implementation status or bypass execution safeguards. If the preferred type is not yet implementable, preserve it and record the detector/action/verifier work as a blocker. Do not downgrade it to `Review` or `None` just because implementation is incomplete or a rare exception exists. Isolate detectable exceptions while keeping the user's preferred default policy.
