# Known Limitations

- Unity compilation is confirmed by the package owner for the current package. EditMode/PlayMode Test Runner execution and real-project fixture validation are not yet confirmed.
- Structural duplicate/remap fixes still need Unity fixtures to verify serialized-reference preservation across representative prefabs/scenes.
- BuildReport grouping needs tests for size-only, share-only, both/neither, and missing or mismatched provenance.
- Font and shader grouping need real assets; shader complexity metrics do not replace build-time variant/stripping analysis or establish exact final-build variant counts.
- Material/file correlation is intentionally conservative: it runs only when every material in an equivalence group is proven byte-identical. Render-equivalent but non-byte-identical materials remain separate.
- `ADR001` and `ADR007` remain separate until the Addressables evidence exposes a stable shared dependency/bundle/root-cause identity.
- ANI002 importer-stage and ANI006 clip-data savings are not treated as additive; incremental savings still require reimported/model and standalone-clip fixtures.
- Runtime performance/A-B benchmarking is not yet a fully automated statistical experiment runner. Texture mip-streaming findings remain primarily static, and rendering source analysis does not establish actual GPU cost without profiling/RenderGraph evidence.
- Some Review actions depend on project intent and therefore cannot have a universal deterministic target state.

Re-check these limits before treating them as resolved. The overlap matrix identifies the specific remaining verification for each rule group.
