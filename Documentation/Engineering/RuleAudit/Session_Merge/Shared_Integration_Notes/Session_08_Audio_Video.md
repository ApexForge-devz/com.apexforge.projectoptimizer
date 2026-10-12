# Session 08 shared integration note — Audio / Video FixKind audit

## Required merge-time reconciliation

The approved Session 08 target classifies **AUD008** and **AUD010** as `FixKind.Verified` and now provides guarded AutoFix actions, importer backups, deterministic postcondition verifiers, and session-unique regression coverage.

The existing shared test `Source_Snapshot/Tests/Editor/EvidenceExecutionRegressionTests.cs` currently includes both IDs in the `reviewOnly` array inside `WorkloadDependentRulesRemainReviewOnlyUnlessUserPreferenceTargetsAnotherPolicy()`:

```csharp
string[] reviewOnly = { "PRJ059", "PRE004", "MDL013", "ANI001", "UNU003", "AUD008", "AUD010", "PRJ064" };
```

This assertion will fail once the Session 08 analyzer registration is merged with the shared test unchanged. At merge/reconciliation time, remove `AUD008` and `AUD010` from that array or otherwise update the assertion to validate their verified action/verifier contracts. **This session deliberately does not edit the shared test file** because the audit workflow prohibits changes to shared tests during parallel session implementation.

## Superseded shared-test note (package 1.1.7)

The historical `reviewOnly` example above reflects the earlier merge snapshot. The current package promotes PRJ064 to Verified because its action is restricted to an effective Dedicated Server context and verifies the same profile-local/global owner it mutates. The current shared test removes PRJ064 from that Review-only array. This change does not alter Session 08's AUD008/AUD010 changes.

## Scope and verification state

- The note is a merge request only; no shared source or existing shared tests were changed in Session 08.
- `AUD008` covers eligible PCM/ADPCM clips above the configured threshold and uses Unity's `OptimizeSampleRate`, excluding unsupported formats and tracker modules.
- `AUD010` disables Force To Mono only when the clip is still ambisonic and Force To Mono remains enabled.
- Session-unique tests are included for catalog targets and representative audio fix paths. Unity compilation and EditMode/PlayMode execution were not available in the implementation environment; these tests remain to be run in Unity after merge.


High-potential AutoFix integration note: PRJ050 now owns a backup-backed mutation helper for `m_UseFastSRGBLinearConversion`; it saves only the affected URP asset and the ProjectSettings fix transaction includes extra asset snapshots in rollback. This cross-cutting fix-engine behavior must remain when merging analyzer-local changes.
