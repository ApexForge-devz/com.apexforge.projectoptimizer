# AI Context

Purpose: compact persistent context for future AI/maintainer sessions.

## Read order
1. `ProjectOptimizer_Context.md` — always.
2. `Product_Intent.md` + `Facts_and_Invariants.md` — before meaningful changes.
3. `Architecture.md` — architecture/core-pipeline changes.
4. `Rules_and_Automation.md` — analyzer/rule/fix changes.
5. `Design_Decisions.md` — before reversing an existing design choice.
6. `Known_Limitations.md` — before treating a limitation as an accidental defect.
7. `Change_Log.md` — when recent history matters.

## Authority
- Current user instructions and higher-priority instructions win.
- Current source/project state is the implementation source of truth.
- These files preserve intent, invariants, decisions, and known limitations.
- Verify version-sensitive Unity behavior against current Unity documentation.
- Update this context when a deliberate product/architecture decision changes.

## Context discipline
- Keep entries terse; one fact should normally be one line.
- Record only durable information useful to future sessions.
- Do not copy full audits, rule inventories, source files, or changelogs here.
- Prefer links/references to detail stored elsewhere.
