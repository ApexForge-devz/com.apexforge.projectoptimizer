# Architecture

## Main flow
`Scan Scope → ProjectScanner/BackgroundScan → analyzers → AssetEntry/OptimizationIssue → UI/reporting`

## Evidence/indexing
- `ReferenceIndex`: project-wide references and completeness state.
- `ProjectProofIndex`: explicit project-wide proof pass for evidence-gated rules.
- `AssetCatalog`: asset inventory and metadata.
- `ScanCache` / `ScanHistoryStore`: reusable scan state/history.
- `BuildReportStore`: persisted build evidence.
- `RuntimeEvidenceStore` / `RuntimeReportImporter`: runtime evidence.

## Fix flow
`FixRequest/ReviewBatch → FixEngine → backup → mutation → reimport/refresh → verifier → rollback on failure`

## Core subsystems
- Analyzers: domain-specific detection and issue creation.
- Core domain: issue, entry, rule, risk, savings, evidence models.
- Settings: thresholds, platform profiles, scan policy.
- UI: overview, categories, rules, scope, review, runtime evidence, commands.
- CI/reporting: batch scan, baseline comparison, JSON/Markdown/SARIF outputs.

## Change rule
Reuse working infrastructure; fix root causes; avoid gratuitous rewrites.
