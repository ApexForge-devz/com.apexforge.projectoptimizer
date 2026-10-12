# ProjectOptimizer

ProjectOptimizer is a Unity Editor workbench for scanning project assets and settings, reviewing evidence-backed findings, applying supported fixes, and checking the result with a rescan. Findings carry severity, confidence, fix risk, and source context. Automatic and review actions are kept separate.

## Maintainer / AI Context

Persistent product intent and engineering context are in [`AI_Context/README.md`](AI_Context/README.md). Read it before substantial maintenance or AI-assisted changes.

## Requirements

- Unity 6.0 or later. The package targets Unity 6000.0 and later APIs; the delivery environment used for this hardening pass did not contain a Unity Editor for runtime compilation/testing.
- `com.unity.ugui` 2.0.0 for the uGUI hierarchy analyzer.
- The optional `Runtime` collector is only needed when capturing Player evidence. FrameTimingManager metrics require the target Player configuration/platform to support frame timing statistics.

## Install and open

Add the package through Unity Package Manager as a local package, Git package, or embedded package. Open **Tools > Project Optimizer**. Configure **Scan Scope**, then click **Scan Project**. Use the category pages to inspect findings, **Rules & Thresholds** to tune the analysis, and the available fix actions to make reviewed changes. Rescan to compare the result.

Package Manager offers an optional **Script Analysis Example** sample. It is intentionally small and contains a diagnostic script pattern for demonstration.

See the [rule audit and overlap decisions](Documentation/Engineering/RuleAudit/README.md) for current rule behavior and verification boundaries, and the [Editor test instructions](Tests/Editor/README.md) for running package tests.

Rule catalog migrations and the current ID mapping are documented in `Documentation/Engineering/RuleAudit/ProjectOptimizer_Rule_ID_Migration.csv`.


**Rule IDs:** All canonical rule IDs use exactly three numeric suffix digits. PAS runs `PAS001`–`PAS028`, PAA runs `PAA001`–`PAA005`, and PAP runs `PAP001`–`PAP002`. Saved settings and baselines are migrated according to their recorded rule-ID schema version.
