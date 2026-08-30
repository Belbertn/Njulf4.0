# Restore Bistro Textures and Enforce Current Cooks

## Summary

- Root cause: both Bistro packages contain import contract `0x20dea9bca4df0beb`, while the current runtime expects `0x90acdcc31796cbf4`. The contract changed when GI primitive transport advanced from schema 5/algorithm 6 to schema 6/algorithm 7.
- Development therefore rejected the cooks and silently imported the FBX/DDS sources. The capture shows 495 runtime-decoded RGBA textures using about 385 MB, instead of the normal compressed BC5/BC7 cooked path using roughly 78–114 MB.
- The source files, albedo bindings, and UV streams are present; shader selection and default-white substitutions are not the cause.
- Bistro will be made cooked-only so this degraded fallback cannot recur.

## Implementation Changes

- Add a `tools/cook-bistro.ps1` workflow that force-cooks both `BistroExterior.fbx` and `BistroInterior.fbx` for `win-x64` using Assimp, the `AmazonBistro` convention, AutoBC textures, and full offline mip chains.
- Have the workflow run the existing Bistro contract/material tests, then rebuild the game so the regenerated packages and KTX2 textures reach the runtime output.
- Update the reflection-qualification workflow and cooked-assets documentation to use this shared Bistro cook command instead of cooking only the exterior.
- Add `ContentLoadOptions.RequireCooked`, defaulting to `false` for compatibility. When enabled, synchronous, asynchronous, and preload model paths must reject missing or invalid cooks before invoking the source importer.
- Mark both Bistro manifest assets as `RequireCooked = true`. Include the per-request policy in model-load gate identity.
- Report missing packages with `FileNotFoundException` and invalid contracts with `InvalidDataException`; include the resolver's exact reason and an AssetTool command containing the requested backend and Amazon Bistro convention.

## Tests and Acceptance

- Confirm `BothBistroCooks_ResolveUnderExactRuntimeImportContracts` passes for both packages without hardcoding the current contract hash.
- Run `ExteriorCook_PreservesThinGlassAndImportSemantics` and add coverage that cooked Bistro materials retain their base-color texture references and compressed texture semantics.
- Add ContentManager tests proving `RequireCooked` overrides Development fallback in synchronous, asynchronous, and preload paths, while ordinary assets retain existing fallback behavior.
- Add a manifest test proving both Bistro tiers are cooked-only and use Assimp/AmazonBistro.
- Run the Bistro transition smoke and quality capture through full exterior/interior residency; require zero source fallbacks and cooked BC7/BC5 texture entries.
- Capture the locked beauty view and `material-base-color` debug view. Acceptance requires restored facade, sign, foliage, brick, and painted-surface detail and no return to the flat screenshot supplied with this issue.

## Assumptions

- `Cooked/` remains generated local output and is not committed.
- No shader, UV, material-binding, or DDS-decoder changes are planned because current evidence does not implicate them.
- Raw Bistro source-fallback visual parity is intentionally out of scope; Bistro will fail with an actionable recook diagnostic instead.
