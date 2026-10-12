# High-potential AutoFix implementation — 1.1.7

## Scope

Implemented only the six candidates the package owner approved: PRJ009, PRJ030, PRJ032, PRJ050, PRJ064, and SCR006. SCR016 and SCR025 were explicitly excluded; their runtime behavior and FixKind remain unchanged.

| Rule | Implemented common-case action | Verification / safety boundary |
|---|---|---|
| PRJ009 | Change `ForceEnable` to `Enable` (Per Texture) on the exact analyzed Quality Level. | Preflights that the exact named Quality Level is still Force Enable, validates index/name, restores the Editor's selected level, and reads the setting back; unresolved target-level fallback is diagnostic-only. |
| PRJ030 | Disable automatic Android API selection and order Vulkan first, OpenGLES3 second, then remaining unique APIs. | Preflights the exact auto-selection flag and API list captured during analysis. It also detects duplicate entries or any non-canonical ordering. Postcondition checks the full explicit list against the expected order. |
| PRJ032 | Change Android Blit Type `Always` to `Auto`. | Report only if Blit Type is `Always` and Android uses automatic API selection or has an explicit non-Vulkan API. Before mutation, rechecks the Blit Type and exact graphics API selection/list captured during analysis. No no-op fix for Vulkan-only or stale configurations. Verifies `Auto`. |
| PRJ050 | Enable URP `m_UseFastSRGBLinearConversion` on the affected pipeline asset. | Validates path, editability, clean pre-state, and Boolean property type; snapshots and saves the exact asset, then verifies the same serialized property. Shared pipeline assets affect every target using that asset; color-quality comparison remains a Unity validation item. |
| PRJ064 | Enable Dedicated Server optimizations only in the effective Dedicated Server context. | Supports profile-local PlayerSettings ownership, blocks unsaved/readonly owners, rejects an already-changed target value, snapshots and saves the exact asset, and includes additional asset snapshots in atomic ProjectSettings rollback. The verifier reads the same owner. Real Dedicated Server build smoke test remains pending. |
| SCR006 | Remove exactly empty, unannotated private `OnGUI` callback(s) in one unambiguous MonoBehaviour source. | Uses source rewrite preflight/backup/reimport/verifier. Non-empty, protected, annotated, commented, preprocessor-conditional, or structurally ambiguous instances become `FixKind.None` with no action. |

## Regression coverage

`Tests/Editor/HighPotentialAutoFixTests.cs` verifies the six catalog contracts, exact PRJ009 Quality Level mutation/selection restoration, the expected action/verifier wiring, API order/deduplication, the PRJ032 non-Vulkan applicability and stale-state preflight predicates, PRJ064 profile-owner backup/rollback and no-op preflight, safe SCR006 rewrite shapes, and per-instance downgrade to diagnostic-only.

## Verification status

Static CSV/source/metadata checks pass. `SCR016` and `SCR025` implementation blocks were not modified. `FixEngine` now restores extra assets captured by a ProjectSettings action if the ProjectSettings phase fails or any verifier rejects the change. Unity compilation and EditMode execution have not been run in this environment; PRJ030/PRJ032 Android device matrix and PRJ064 Dedicated Server build validation are also pending.
