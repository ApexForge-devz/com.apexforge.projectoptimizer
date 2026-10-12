# Design Decisions

## DEC-001 — Automation-first
The optimizer's primary product value is reducing manual optimization effort at very large project scale.

## DEC-002 — Exceptions, not blanket Review
Rare edge cases should normally be detected and isolated programmatically instead of making every normal case manual.

## DEC-003 — Verified requires verification
Automatic mutation should be paired with explicit post-change verification whenever practical.

## DEC-004 — Evidence strength matters
Do not present inference as measured build/runtime proof.

## DEC-005 — Preserve architecture
Prefer hardening the current architecture over replacing it without evidence that the architecture is the root problem.

## DEC-006 — `AUD002` stays automatic
Short streamed clips remain a normal Verified optimization under the configured policy; exceptional memory/platform cases should be isolated by evidence rather than making the common case manual.
## DEC-007 — User preferences are authoritative

Explicit `User_Preference_*` and `User_Preferred_*` values in the Rule Audit are the highest-priority product-policy input and must never be silently overridden. Conflicting recommendations are recorded as conflicts; implementation blockers are tracked separately from the preferred target state. This does not permit claiming a preferred fix is implemented or verified before its detector, action, safeguards, and verifier exist.
