# Production Ready Asset Importer Plan

Goal: make downloaded and DCC-exported production assets safe to place in a project without crashing the game, silently losing important material data, or requiring renderer-specific manual edits. The importer should either load the asset predictably or fail with actionable diagnostics that name the exact file, glTF path, extension, material, texture, mesh, primitive, or accessor involved.

This revision is library-first. Njulf should use SharpGLTF as the primary `.gltf` and `.glb` implementation, keep Assimp for non-glTF formats, and write custom importer code only when the upside is clear: renderer-specific mapping, diagnostics, validation policy, texture budget reporting, or a feature gap that blocks a real asset.

## Implementation Status

1. Phase 0 is implemented:
   - SharpGLTF Core, Runtime, and Toolkit package references are installed.
   - `SharpGltfCapabilityInspector` loads `.gltf` and `.glb` assets through SharpGLTF and reports document/runtime capabilities.
   - tests cover a generated external `.gltf`, Sponza, Strut `.glb`, ribbon grass variants, missing files, and the tree `.glb` crash candidate.
2. Phase 1 facade and contracts are implemented:
   - `ModelImporter.Import` remains the stable production API and preserves current Assimp behavior by default.
   - `ModelImporter.ImportDetailed` returns backend, status, diagnostics, version, and optional SharpGLTF capability data.
   - `ImporterOptions.Backend` can explicitly select Assimp or SharpGLTF.
3. Phase 2 mesh, skin, and animation conversion is implemented:
   - explicit SharpGLTF selection imports `ModelMesh` geometry for `.gltf` and `.glb`.
   - scene traversal, node transforms, primitive/submesh boundaries, positions, normals, tangents, bitangents, UV0, UV1, vertex colors, indices, materials, bounds, and basic skin containers are mapped into Njulf types.
   - SharpGLTF animation clips now map typed translation, rotation, and scale samplers into Njulf `AnimationClip`, `AnimationChannel`, and `AnimationSampler` data, including `STEP`, `LINEAR`, and `CUBICSPLINE` interpolation labels.
   - the default production backend remains Assimp until SharpGLTF parity is broader.
   - morph target animation remains out of scope because the current engine animation model does not expose a morph animation target.
4. Phase 3 renderer-supported material parity is implemented:
   - SharpGLTF material channels now populate Njulf base color, normal, metallic-roughness, occlusion, and emissive texture slots.
   - texture source identity, sampler wrap/filter state, color space, UV set, and `KHR_texture_transform` offset/scale/rotation are preserved for renderer upload.
   - material alpha mode, alpha cutoff, double-sided state, unlit flag, base color, emissive color/strength, metallic, roughness, normal scale, occlusion strength, IOR, and dispersion are mapped.
   - clearcoat, sheen, anisotropy, transmission, volume, specular, and iridescence scalar values and renderer-supported extension texture slots are mapped through SharpGLTF channels.
   - unsupported required glTF extensions are rejected before conversion with an actionable diagnostic; unsupported optional extensions are surfaced as warnings.
5. Phase 4 texture source transport and budget metadata are implemented:
   - `ModelTextureSource` now records source kind and encoded byte length for external files, data URIs, buffer-view images, GLB BIN images, and generic embedded memory.
   - SharpGLTF external PNG/JPEG, data URI images, `.gltf` buffer-view images, and `.glb` BIN images are preserved as renderer-ready `ModelTextureSource` values.
   - renderer texture load requests accept `ModelTextureSource` for file-backed and memory-backed standard images and KTX2 containers.
   - `TextureManager` budget entries now include source kind, original decoded dimensions, imported dimensions, mip count, estimated bytes, downscale status, encoded byte length, format, and compressed-format flag.
   - `TextureManager.InspectTextureSourceBudget` reports PNG/JPEG and KTX2 texture budget data before scene upload, including large external asset textures such as the grass tiers.
   - texture cache keys include source identity, sampler state, color-space state, mip policy, max dimension, and container kind.
   - uncompressed Vulkan-compatible KTX2 metadata/upload is preserved; BC compressed KTX2 sources report compressed budget metadata; supercompressed Basis/KTX2 sources fail with an explicit transcoder-required rejection.
   - folder-level JSON report emission remains part of the validation CLI phase, but the Phase 4 runtime and pre-upload budget surface it needs is in place.
6. Phase 5 crash containment and validation CLI are implemented:
   - `Njulf.AssetTool` provides `validate`, `import`, and `report` commands and emits structured JSON reports.
   - validation defaults glTF/glb to SharpGLTF in-process and runs Assimp imports in a child process when Assimp is selected.
   - child-process non-zero exits, native access violations, invalid output, timeouts, and file-size guardrail failures become structured validation entries instead of process-wide sample failures.
   - reports include backend, process kind, process exit code, elapsed time, file size, importer diagnostics, failure text, mesh/material/animation counts, texture counts, estimated texture bytes when budget probing is available, and extension usage.
   - `NjulfHelloGame` now gates sample manifest assets through a checked-in successful validation report, with `NJULF_SAMPLE_ALLOW_UNVALIDATED_ASSETS=true` as the explicit local bypass.
   - tests cover missing files, invalid glTF JSON, unsupported required extensions, simulated child-process crash, timeout, report serialization, and CLI report generation.
   - generated validation reports for `NjulfHelloGame/Assets` confirm the default SharpGLTF path validates all current folder assets and the explicit Assimp path records native crash failures without taking down the parent process.
7. Phase 6 import diagnostics, failure policy, and classification are implemented:
   - `AssetImportMessageCode` now includes native crash, child timeout, managed exception, unsupported compressed mesh/texture, unsupported primitive, invalid accessor/buffer, alpha performance, excessive texture memory, and foliage-policy warning codes.
   - validation supports `Strict`, `GameDefault`, and `Permissive` policy levels; strict policy rejects warnings that affect rendering or selection.
   - each validation entry reports classifications: `Opaque`, `Masked`, `Blended`, `Billboard`, `FoliageCandidate`, `HighTextureMemory`, and `UnsupportedRequiredFeature`.
   - foliage warnings include BLEND alpha on dense foliage, missing/invalid masked cutoff, double-sided concerns, high texture memory, and translucency/subsurface usage outside the default foliage path.
   - diagnostics added by the classifier include source paths such as `/materials/0` and `/textures`.
   - `Njulf.AssetTool` prints a human-readable classification summary and decision notes while preserving machine-readable JSON.
   - generated Phase 6 reports classify `NjulfHelloGame/Assets`: top-level ribbon grass files are blended foliage candidates and not selected by default; `standard/*_nonUE.gltf` files are masked preferred foliage candidates.
   - tests cover classification, decision notes, strict policy behavior, high texture memory classification, and diagnostic source paths.
8. Phase 7 regression corpus and CI gates are implemented:
   - `AssetRegressionCorpusTests` generates and imports SharpGLTF fixtures for external PNG `.gltf`, data URI buffers, data URI images, `.gltf` buffer-view images, `.glb` embedded geometry, `.glb` embedded images, UV1, vertex colors, and `KHR_texture_transform`.
   - malformed `.glb` input is checked as a deterministic rejection with stable status, failure type, and failure message.
   - validation report schema tests lock in elapsed time, file size, texture budget, classifications, decision notes, and diagnostic source paths.
   - the checked-in sample asset allowlist locks expected assets, acceptance status, diagnostic count, and classification count.
   - existing CI coverage continues to exercise SharpGLTF adapter behavior, Assimp non-glTF import, facade routing, texture source handling, and child-process validation.
   - `dotnet test Njulf.sln` passes with 299 tests.
9. Phase 8 migration and cleanup are implemented:
   - `ModelImporter.ResolveBackend` now routes `.gltf` and `.glb` to SharpGLTF by default, while OBJ/FBX and explicit legacy imports continue to use Assimp.
   - explicit Assimp glTF imports remain available for comparison/debugging; the old partial glTF sidecar manifest parser is quarantined to that explicit Assimp path.
   - SharpGLTF skin import now computes joint bind transforms relative to the nearest ancestor joint and converts skipped non-joint ancestor rotation into joint animation channels.
   - SharpGLTF animation import completes missing TRS channels for animated joints so runtime pose evaluation remains deterministic.
   - `ContentManager` has options-aware loading and cache keys that include importer backend, import policy, texture budget threshold, and importer-affecting options.
   - `SampleAssetManifest` now references validated assets with expected backends, and `SampleAssetValidationGate` rejects reports that validate a manifest asset with the wrong backend.
   - `NjulfHelloGame/sample-asset-validation-report.json` was regenerated from source assets only, with `bin` and `obj` folders excluded from validation discovery.
   - artist-facing export recommendations are documented in `Plans/AssetExportRecommendations.md`.
   - `dotnet test Njulf.sln` passes with 301 tests.

## Current Situation

The asset pipeline has useful foundations:

1. `ModelImporter` is the public import facade and currently imports OBJ, glTF, and glb through Assimp.
2. glTF metadata is partially parsed in `ModelImporter` for materials and texture slots.
3. `AssetImportDiagnostics`, `AssetImportMessage`, `ModelTextureSource`, `ModelTextureSlot`, `TextureSamplerDescription`, and `TextureColorSpace` already exist.
4. Tests already cover important slices: external glTF, buffer-view images, required `KHR_texture_basisu` source mapping, UV1/material texture transform upload, KTX2 metadata parsing, and texture cache sampler identity.
5. Rendering already supports texture downscaling, texture memory diagnostics, material extension slots, and default fallback textures.

The current weak points are also clear:

1. A malformed or importer-hostile file can still crash inside native Assimp instead of producing a managed error.
2. `.gltf` and `.glb` are too dependent on Assimp even though glTF has a strong .NET-native library option.
3. `.glb` metadata is not treated as the same first-class glTF source as `.gltf`.
4. Asset validation is mostly in-process, so native importer failures can take the sample game down.
5. Downloaded asset packages are not classified before use. The recent foliage assets showed both importer fragility and content-policy issues:
   - `low_poly_trees_free.glb` triggered a native Assimp crash during import attempts.
   - the top-level ribbon grass UE `.gltf` variants use `alphaMode: "BLEND"`, which is a poor default for dense real-time foliage.
   - the `standard/*_nonUE.gltf` variants use `alphaMode: "MASK"` and are the better real-time candidates.
   - the high-tier grass textures are heavy enough that importer validation should report memory cost before the scene uses them.

## Production Definition Of Done

The asset importer is production-ready when:

1. Importing any normal project asset cannot crash the game process.
2. `.gltf` and `.glb` assets from Blender, Maya, Substance, Megascans, Sketchfab-style packs, and common game-asset stores load through SharpGLTF without manual unpacking when they use supported features.
3. OBJ, FBX, and other non-glTF formats continue to load through Assimp where Assimp supports them.
4. Unsupported required features fail before rendering with a precise diagnostic.
5. Unsupported optional features warn without corrupting geometry or material state.
6. External, embedded, data URI, buffer-view, and `.glb` BIN chunk resources are resolved through SharpGLTF or a small Njulf adapter over SharpGLTF data.
7. Material texture slots preserve source image, sampler state, color space, UV set, texture transform, alpha mode, alpha cutoff, and double-sided state.
8. Texture memory, dimensions, compression, downscaling, missing files, fallback use, and unsupported formats are visible in diagnostics.
9. Asset validation can run in CI and against a local asset folder, producing a JSON report.
10. Foliage candidate assets are classified as masked, blended, billboard, high-poly, high-texture-memory, or unsupported before they are used by the sample game.

## Architecture Direction

Move from "Assimp plus partial glTF JSON sidecar parsing" to "SharpGLTF for glTF/glb, Assimp for non-glTF, Njulf policy on top."

Target ownership:

1. `Njulf.Assets.Importing`
   - owns `ModelImporter` as the stable public facade.
   - routes `.gltf` and `.glb` to the SharpGLTF adapter.
   - routes OBJ, FBX, and legacy formats to the Assimp adapter.
   - exposes import options and policy without leaking library-specific types into renderer code.

2. `Njulf.Assets.Gltf`
   - uses `SharpGLTF.Core` for glTF/GLB document loading, schema access, buffers, images, textures, samplers, extension data, and low-level inspection.
   - uses `SharpGLTF.Runtime` for runtime scene, mesh, skin, and animation extraction where it preserves the data Njulf needs.
   - uses `SharpGLTF.Toolkit` for fixture generation, conversion helpers, scene/mesh builder workflows, and tests where appropriate.
   - avoids custom JSON, GLB, buffer, accessor, or scene traversal code unless SharpGLTF cannot expose data required by Njulf.
   - emits CPU-side `ModelMesh` with diagnostics.

3. `Njulf.Assets.Assimp`
   - isolates Assimp usage behind an adapter.
   - keeps OBJ, FBX, and legacy paths here.
   - supports child-process validation for native crash containment.
   - can remain as an optional comparison backend for glTF during migration, but not as the default glTF path.

4. `Njulf.Assets.Diagnostics`
   - owns diagnostics codes, severity policy, JSON reports, asset classification, and importer feature summaries.
   - converts SharpGLTF and Assimp exceptions into Njulf diagnostics.
   - records when a feature is handled by SharpGLTF, handled by Assimp, rejected by policy, or skipped because the renderer does not support it.

5. `Njulf.Rendering.Resources`
   - owns GPU texture creation, sampler creation, compression capability checks, bindless registration, texture memory accounting, and renderer fallbacks.
   - treats imported texture data as requests with source, sampler, color space, usage, and budget policy.

6. `NjulfHelloGame`
   - uses validated asset manifests or generated test assets only.
   - does not directly load unvalidated downloaded assets during startup.
   - keeps input/view controls such as `SampleInputController` separate from importer policy.

## Library Usage Policy

Use library capabilities before adding custom importer code:

1. Prefer SharpGLTF document/runtime APIs over custom parsing for `.gltf` and `.glb`.
2. Prefer SharpGLTF extension objects or preserved extension JSON over ad hoc `JsonDocument` side paths.
3. Prefer SharpGLTF image, buffer, accessor, scene, node, mesh, skin, and animation APIs over manual byte walking.
4. Prefer Toolkit-generated fixture assets over hand-authored binary fixtures when a fixture can be expressed with Toolkit.
5. Prefer Assimp for non-glTF formats instead of building Njulf-specific readers.
6. Add custom code only for:
   - mapping SharpGLTF/Assimp output to `ModelMesh`, `ModelMaterial`, and `ModelTextureSlot`.
   - renderer policy: color space, alpha classification, texture budget, fallback behavior, material feature support.
   - diagnostics shape: source paths, severity, JSON reports, asset classification.
   - feature gaps that block real project assets, such as meshopt/Draco if SharpGLTF does not handle them.

## Phase 0: Dependency And Capability Spike

Purpose: prove the combined SharpGLTF plus Assimp architecture against the assets already in this repo before changing the public importer behavior.

Tasks:

1. Add package references to `Njulf.Assets`:
   - `SharpGLTF.Core`
   - `SharpGLTF.Runtime`
   - `SharpGLTF.Toolkit`
2. Add a small experimental loader behind tests or a private prototype class, not the production path yet.
3. Load representative assets through SharpGLTF:
   - existing Sponza glTF.
   - a simple external `.gltf` with `.bin` and PNG.
   - a `.glb` with embedded geometry.
   - a `.glb` with embedded image.
   - ribbon grass `standard/*_nonUE.gltf`.
   - the tree `.glb` that crashed Assimp.
4. Record what SharpGLTF exposes directly for:
   - document metadata.
   - buffers and buffer views.
   - accessors and vertex streams.
   - images, textures, samplers.
   - material PBR fields and extensions.
   - node transforms and scene traversal.
   - skins and animations.
   - `extensionsUsed` and `extensionsRequired`.
5. Identify real gaps before writing custom code. Expected candidates:
   - compressed mesh extensions such as Draco or meshopt if present in downloaded assets.
   - texture transcoding for KTX2/BasisU/WebP/AVIF/DDS if renderer upload support is missing.
   - source-location diagnostics when SharpGLTF reports an exception without a JSON pointer.
6. Keep Assimp unchanged for production imports during the spike.

Acceptance criteria:

1. A test or console spike can load representative `.gltf` and `.glb` files through SharpGLTF without invoking Assimp.
2. The spike report lists each unsupported or uncertain feature with a concrete asset and reason.
3. No custom parser is introduced during the spike.

## Phase 1: Facade Split And Importer Contracts

Purpose: make the importer architecture explicit while keeping existing callers stable.

Tasks:

1. Keep `ModelImporter.Import` as the public facade.
2. Add internal importer interfaces, for example:
   - `IModelAssetImporter`
   - `SharpGltfModelImporter`
   - `AssimpModelImporter`
3. Route by extension:
   - `.gltf` and `.glb` -> SharpGLTF.
   - OBJ, FBX, and known legacy formats -> Assimp.
   - unknown extension -> diagnostic failure.
4. Move public DTOs and diagnostics contracts out of the large `ModelImporter.cs` file if needed for clean adapter boundaries.
5. Add an importer result type that carries:
   - `ModelMesh`.
   - diagnostics.
   - importer backend name and version when available.
   - feature summary.
   - validation summary.
6. Add an option to compare SharpGLTF and Assimp output for glTF during migration, but keep it off by default.

Acceptance criteria:

1. Existing callers still use `ModelImporter.Import`.
2. Non-glTF tests continue to use Assimp.
3. glTF tests can opt into SharpGLTF without changing renderer code.
4. The selected backend is visible in diagnostics.

## Phase 2: SharpGLTF Mesh, Scene, Skin, And Animation Path

Purpose: use SharpGLTF for glTF runtime data instead of writing a custom glTF reader.

Tasks:

1. Use SharpGLTF to load `.gltf` and `.glb` documents.
2. Use SharpGLTF scene/runtime APIs to extract:
   - scene roots.
   - node hierarchy.
   - TRS and matrix transforms.
   - mesh primitives.
   - primitive material indices.
   - vertex streams.
   - index streams.
   - skins.
   - inverse bind matrices.
   - animation clips and channels.
3. Preserve submesh boundaries by primitive.
4. Preserve or compute bounds through SharpGLTF-accessible data.
5. Preserve vertex streams supported by Njulf:
   - position.
   - normal.
   - tangent.
   - UV0.
   - UV1.
   - vertex color.
   - joints and weights.
6. Let SharpGLTF validate and decode supported accessor layouts. Add custom handling only if a supported real asset exposes data SharpGLTF leaves inaccessible.
7. Compare SharpGLTF output against current Assimp output on existing fixtures and Sponza during migration.

Acceptance criteria:

1. Existing static glTF fixtures import through SharpGLTF.
2. Existing animation and skinning tests pass through SharpGLTF or have documented deltas.
3. Sponza imports with stable object/material/submesh counts.
4. Meshlet generation works on imported SharpGLTF geometry.
5. The low-poly tree `.glb` either imports through SharpGLTF or fails with a managed diagnostic, never a native crash.

## Phase 3: SharpGLTF Materials, Textures, And Extensions

Purpose: use SharpGLTF's material and extension support as the source of truth for glTF-specific material data.

Tasks:

1. Populate `ModelMaterial` and `ModelTextureSlot` from SharpGLTF for:
   - base color.
   - normal.
   - metallic-roughness.
   - occlusion.
   - emissive.
   - material extension textures already supported by the renderer.
2. Preserve per-slot:
   - image source.
   - sampler.
   - color space.
   - UV set.
   - `KHR_texture_transform` offset, scale, rotation, and texCoord override.
3. Preserve material flags:
   - alpha mode.
   - alpha cutoff.
   - double-sided.
   - unlit.
   - material extension presence.
4. Use SharpGLTF extension data where available for:
   - clearcoat.
   - sheen.
   - anisotropy.
   - transmission.
   - volume.
   - ior.
   - specular.
   - iridescence.
   - dispersion.
   - texture transform.
   - lights and instancing if later needed by renderer features.
5. For extensions SharpGLTF can preserve but Njulf cannot render, warn or reject according to import policy.
6. For extensions SharpGLTF cannot decode and that appear in `extensionsRequired`, reject with an actionable diagnostic.

Acceptance criteria:

1. Texture transform and UV set tests pass through SharpGLTF import and GPU material upload.
2. Material extension data currently supported by the renderer is preserved.
3. Unsupported required extensions fail before rendering.
4. Diagnostics identify fallback texture usage by material and slot.

## Phase 4: Texture Source And Budget Pipeline

Purpose: make texture import predictable, memory-aware, and compatible with real asset packs while leaving decoding/transcoding responsibilities clear.

Tasks:

1. Map SharpGLTF image sources into `ModelTextureSource`:
   - external file URI.
   - data URI.
   - buffer-view image.
   - `.glb` BIN image.
2. Add texture load requests that accept `ModelTextureSource`, not just file paths.
3. Support PNG/JPEG from:
   - file path.
   - memory bytes.
   - buffer-view image.
   - GLB BIN image.
4. Preserve sampler state in the texture cache and bindless registration.
5. Preserve color-space policy by material usage:
   - base color and emissive as sRGB.
   - normal, metallic-roughness, occlusion, masks as linear.
   - HDR environment as HDR linear.
6. Keep KTX2 metadata parsing and add renderer upload for uncompressed Vulkan-compatible formats where practical.
7. Add clear fallback or rejection for supercompressed Basis/KTX2 until a transcoder exists.
8. Add compressed format capability checks:
   - BC1/BC3/BC5/BC7.
   - fallback to RGBA8 when policy allows.
9. Add texture budget reporting to the asset validation report:
   - original dimensions.
   - imported dimensions.
   - mip count.
   - estimated bytes.
   - downscaled flag.
   - compression flag.

Acceptance criteria:

1. Embedded GLB PNG/JPEG images load and upload through the SharpGLTF path.
2. Texture cache keys distinguish same bytes with different sampler or color-space state.
3. Large grass tiers report expected texture memory before scene use.
4. Unsupported compressed textures fail or fallback according to explicit policy.

## Phase 5: Crash Containment And Asset Validation CLI

Purpose: stop downloaded assets from taking the game down and make validation repeatable.

Tasks:

1. Add a small CLI entry point, for example `Njulf.AssetTool`, with:
   - `validate <path-or-folder>`
   - `import <path>`
   - `report <path-or-folder> --json <output>`
2. Use in-process validation for SharpGLTF glTF/glb imports by default, because it is managed code.
3. Run Assimp imports in a child process by default during validation.
4. Optionally allow child-process validation for all assets when validating untrusted downloaded folders.
5. Treat process crash, non-zero exit, timeout, and native access violation as validation failures with a structured report.
6. Add timeout and memory guardrails for large assets.
7. Capture importer diagnostics, exception text, process exit code, elapsed time, loaded texture count, estimated texture memory, mesh count, triangle count, material count, animation count, and extension usage.
8. Add a content gate in `NjulfHelloGame`: sample scenes can reference only assets with a checked-in successful validation report or explicitly generated fallback content.
9. Add tests for:
   - missing file.
   - invalid glTF JSON.
   - unsupported required glTF extension.
   - child-process crash simulated by a test importer.
   - timeout.
   - successful report generation.

Acceptance criteria:

1. The tree `.glb` can fail validation without shutting down the sample game.
2. The asset tool writes a JSON report for the whole `NjulfHelloGame/Assets` folder.
3. CI can run validation on fixture assets.
4. The sample game never directly imports an unknown asset during startup without a controlled failure path.

## Phase 6: Import Diagnostics, Failure Policy, And Classification

Purpose: make every import decision inspectable.

Tasks:

1. Expand `AssetImportMessageCode` for:
   - native importer crash.
   - child process timeout.
   - managed importer exception.
   - unsupported required extension.
   - optional extension ignored.
   - unsupported compressed mesh.
   - unsupported compressed texture.
   - unsupported primitive mode.
   - invalid accessor or buffer data.
   - texture fallback.
   - alpha mode performance warning.
   - excessive texture memory.
2. Add diagnostic source paths using SharpGLTF metadata where available and JSON pointers only when needed.
3. Add import policies:
   - Strict: warnings that affect rendering can fail validation.
   - GameDefault: unsupported required features fail, optional warnings allowed.
   - Permissive: best-effort import with warnings.
4. Add validation summary categories:
   - Accepted.
   - AcceptedWithWarnings.
   - RejectedUnsupported.
   - RejectedInvalid.
   - RejectedCrashed.
   - RejectedTimeout.
5. Add importer classification:
   - `Opaque`
   - `Masked`
   - `Blended`
   - `Billboard`
   - `FoliageCandidate`
   - `HighTextureMemory`
   - `UnsupportedRequiredFeature`
6. Add foliage-specific warnings:
   - dense foliage uses `BLEND`.
   - alpha cutoff missing for masked foliage.
   - double-sided disabled for leaf/grass cards.
   - texture dimensions exceed active budget.
   - material requires unsupported translucency/subsurface behavior.
7. Add a human-readable console summary and machine-readable JSON.

Acceptance criteria:

1. Asset failures are actionable without attaching a debugger.
2. The foliage asset folder can be validated and classified in one command.
3. Ribbon grass top-level UE files are reported as blended foliage candidates and not selected by default.
4. Ribbon grass `standard/*_nonUE.gltf` files are reported as masked foliage candidates.
5. The report explains why an asset is not used by a sample scene.

## Phase 7: Regression Corpus And CI Gates

Purpose: prevent importer regressions as renderer features expand.

Tasks:

1. Use SharpGLTF.Toolkit where practical to generate small fixture assets:
   - external `.gltf` with PNG.
   - `.glb` with embedded geometry.
   - `.glb` with embedded image.
   - data URI buffer.
   - data URI image.
   - buffer-view image.
   - UV1 material.
   - vertex color mesh.
   - texture transform.
   - masked foliage card.
   - blended foliage card.
   - unsupported required extension.
   - malformed GLB, if Toolkit cannot generate it then create the smallest hand-authored fixture.
2. Add optional local validation for larger downloaded assets under `NjulfHelloGame/Assets`.
3. Add CI tests:
   - SharpGLTF adapter tests.
   - Assimp adapter tests for non-glTF formats.
   - importer facade routing tests.
   - texture source tests.
   - diagnostics JSON schema tests.
   - child-process validator tests.
4. Track import times and memory in reports.
5. Add an allowlist file for sample assets with expected diagnostics.

Acceptance criteria:

1. Full test suite covers all supported glTF resource kinds through SharpGLTF.
2. Non-glTF Assimp tests continue to pass.
3. Known-bad assets fail the same way every run.
4. Sample assets cannot silently change import class or warning count without test failure.

## Phase 8: Migration And Cleanup

Purpose: make the combined library-backed path the default and reduce duplicate behavior.

Tasks:

1. Switch `.gltf` and `.glb` imports to SharpGLTF by default.
2. Keep Assimp as default for non-glTF formats.
3. Keep optional Assimp glTF comparison only for migration/debugging if it remains useful.
4. Remove or quarantine old partial glTF manifest parsing once SharpGLTF parity is proven.
5. Update `ContentManager` cache keys to include import policy, texture budget, importer backend, and relevant importer options.
6. Add sample-scene asset manifests that reference validated assets and chosen variants.
7. Document artist-facing export recommendations:
   - use glTF 2.0 for runtime assets.
   - prefer masked alpha for dense foliage.
   - prefer KTX2/BC only when the renderer/transcoder path supports the chosen format.
   - avoid required Draco/meshopt compression until supported by the importer stack.
   - include LODs or game-ready mesh density.
   - keep texture dimensions within active budget.

Acceptance criteria:

1. `ModelImporter.Import` is stable and deterministic for glTF/glb through SharpGLTF.
2. Non-glTF formats still have the Assimp path.
3. Sample game startup uses only validated assets or generated test content.
4. Documentation explains how to prepare assets for the renderer.

## Immediate Recommended First PR

Start with the SharpGLTF capability spike and facade split before replacing the production path.

Scope:

1. Add SharpGLTF package references.
2. Add internal `SharpGltfModelImporter` prototype behind tests.
3. Load representative `.gltf` and `.glb` assets with SharpGLTF.
4. Produce a capability report from tests or a small diagnostic command.
5. Add facade routing scaffolding without changing default production behavior yet.
6. Keep Assimp for current production imports until SharpGLTF parity is proven.

Why this first: it validates the library-first assumption quickly, avoids building a custom glTF reader prematurely, and gives us a concrete gap list for the tree `.glb`, grass `.gltf`, Sponza, Strut, and future downloaded assets.

## Risks

1. SharpGLTF output can differ from Assimp output in transforms, winding, tangents, UV orientation, skinning, or animation behavior. Keep comparison tests during migration.
2. SharpGLTF may not decode every compression extension used by downloaded assets. Add support only when a real asset needs it.
3. Texture memory can dominate asset acceptance. Validation reports must show budget impact before scene use.
4. glTF extension support can sprawl. Use SharpGLTF's extension support first, then add Njulf renderer policy deliberately.
5. Child-process validation adds tooling complexity, but it remains the correct boundary for native Assimp crash containment.
6. Asset packs vary in layout conventions. The importer must report package structure clearly instead of relying on guessed paths.

## Done Criteria For The Downloaded Foliage Assets

The current downloaded tree and grass assets are acceptable for sample use only when:

1. validation runs without crashing the game process.
2. `low_poly_trees_free.glb` either imports through SharpGLTF or is rejected with a managed diagnostic.
3. ribbon grass `standard/*_nonUE.gltf` variants are selected over the top-level blended UE variants for real-time foliage.
4. texture memory for the selected grass tier is within the active sample budget.
5. the asset report is checked in or generated by CI.
6. the sample scene references the validated asset variant explicitly.
