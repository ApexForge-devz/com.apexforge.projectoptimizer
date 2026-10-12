# ProjectOptimizer Context

## Identity
- Package: `com.apexforge.projectoptimizer`
- Version at context creation: `1.1.3`
- Unity floor declared by package: `6000.0`
- Dependency: `com.unity.ugui` `2.0.0`
- Current audit inventory: 104 C# files; rule registration count is generated from the live catalog/integrity audit.

## Primary goal
Remove as much manual optimization work from the developer as safely possible.

The tool must scale to projects with tens of thousands, hundreds of thousands, or millions of assets.

## Core behavior
`Detect → determine confidence → automate when high-confidence → verify → report.`

Prefer programmatic exception detection over unnecessary manual Review work.
A rare theoretical edge case is not, by itself, a reason to downgrade a broadly safe automatic rule.

## Quality bar
Optimize for correctness, coverage, automation rate, scalability, verification, safety, rollback, compatibility, and low user workload.

Do not optimize for rule count, UI complexity, or apparent sophistication.

## Current architecture map
Analyzers → scan/result model → evidence/reference indexes → issue/risk/fix model → FixEngine → verification/rollback → persistence/reporting/UI.

See `Architecture.md` for the compact component map.

## Important target
A developer should not need to inspect thousands of equivalent findings manually when the optimizer can safely determine the action itself.
## Mandatory rule-audit preference precedence

Treat `User_Preference_*` and `User_Preferred_*` audit columns as authoritative, highest-priority product policy. Never silently override or downgrade them. Preserve the preferred type and rationale in edits and regenerated files. If implementation is missing or unsafe under current evidence, keep the preference intact and document the blocker and required detector/action/verifier work; do not misreport current implementation or bypass normal execution safeguards.
