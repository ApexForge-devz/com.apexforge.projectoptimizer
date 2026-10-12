# Session 03 Shared-Test Reconciliation Request — PRE004

**Status:** Proposal only. No shared test has been changed in Session 03.

## Conflict

The approved Session 03 target for `PRE004` is `Verified`. The implementation offers AutoFix only when every targeted surplus material slot repeats the final in-range Material instance, uses a supported conventional opaque shader, has opaque render queue/blend/depth state and no alpha/blend/cutout mode, no per-renderer `MaterialPropertyBlock`, and a unique hierarchy path. Other mismatches remain unchanged and diagnostic. The fix requires a framework backup and verifies the saved target paths.

The existing shared test `Sessions/03_PrefabSceneAnalyzer/Source_Snapshot/Tests/Editor/EvidenceExecutionRegressionTests.cs` contains `PRE004` in its `reviewOnly` list (around line 315), which conflicts with the approved catalog target and the session-unique regression test. The file is out of scope for Session 03 and was deliberately not edited.

## Requested merge action

At reconciliation time, remove `PRE004` from the shared test's `reviewOnly` set and add a focused assertion for its `Verified` registration and guarded action contract. Keep the exception cases covered: transparent/alpha-clipped materials, different trailing material references, unsupported/custom shaders, renderer property blocks, missing meshes, stale evidence, and ambiguous paths must not be eligible for the AutoFix. Run the full shared EditMode suite after the change.

## Validation disclaimer

Unity compilation and tests could not be run in this environment. This request is not evidence that the shared suite currently passes.
