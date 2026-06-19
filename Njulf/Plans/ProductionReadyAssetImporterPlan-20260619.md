# Production Ready Asset Importer Plan

Goal: make downloaded and DCC-exported production assets safe to place in a project without crashing the game, silently losing important material data, or requiring renderer-specific manual edits. The importer should either load the asset predictably or fail with actionable diagnostics that name the exact file, glTF path, extension, material, texture, mesh, primitive, or accessor involved.

## Current Situation

The asset pipeline has useful foundations:

1. `ModelImporter` is the public import facade and already imports OBJ, glTF, and glb through Assimp.
2. glTF metadata is partially parsed in `ModelImporter` for materials and texture slots.
3. `AssetImportDiagnostics`, `AssetImportMessage`, `ModelTextureSource`, `ModelTextureSlot`, `TextureSamplerDescription`, and `TextureColorSpace` already exist.
4. Tests already cover important slices: external glTF, buffer-view images, required `KHR_texture_basisu` source mapping, UV1/material texture transform upload, KTX2 metadata parsing, and texture cache sampler identity.
5. Rendering already supports texture downscaling, texture memory diagnostics, material extension slots, and default fallback textures.

The current weak points are also clear:

1. A malformed or importer-hostile file can still crash inside native Assimp instead of producing a managed error.
2. `.gltf` and `.glb` are still too dependent on Assimp for geometry and scene traversal.
3. `.glb` metadata is not treated as the same first-class glTF source as `.gltf`.
4. Asset validation is mostly in-process, so a native crash can take the sample game down.
5. Downloaded asset packages are not classified before use. The recent foliage assets showed both importer fragility and content-policy issues:
   - `low_poly_trees_free.glb` triggered a native Assimp crash during import attempts.
   - the top-level ribbon grass UE `.gltf` variants use `alphaMode: "BLEND"`, which is a poor default for dense real-time foliage.
   - the `standard/*_nonUE.gltf` variants use `alphaMode: "MASK"` and are the better real-time candidates.
   - the high-tier grass textures are heavy enough that importer validation should report memory cost before the scene uses them.

## Production Definition Of Done

The asset importer is production-ready when:

1. Importing any normal project asset cannot crash the game process.
2. `.gltf` and `.glb` assets from Blender, Maya, Substance, Megascans, Sketchfab-style packs, and common game-asset stores load without manual unpacking when they use supported features.
3. Unsupported required features fail before rendering with a precise diagnostic.
4. Unsupported optional features warn without corrupting geometry or material state.
5. External, embedded, data URI, buffer-view, and `.glb` BIN chunk resources are resolved through one tested path.
6. Material texture slots preserve source image, sampler state, color space, UV set, texture transform, alpha mode, alpha cutoff, and double-sided state.
7. Texture memory, dimensions, compression, downscaling, missing files, fallback use, and unsupported formats are visible in diagnostics.
8. Asset validation can run in CI and against a local asset folder, producing a JSON report.
9. Foliage candidate assets are classified as masked, blended, billboard, high-poly, high-texture-memory, or unsupported before they are used by the sample game.

## Architecture Direction

Move from "Assimp plus partial glTF JSON sidecar parsing" to "glTF-first import with Assimp only as a legacy/non-glTF fallback."

Target ownership:

1. `Njulf.Assets.Gltf`
   - parse `.gltf` JSON and `.glb` containers.
   - validate buffers, buffer views, accessors, images, textures, samplers, nodes, meshes, skins, animations, and extensions.
   - emit CPU-side `ModelMesh` with diagnostics.

2. `Njulf.Assets.Assimp`
   - isolate Assimp usage behind a subprocess-capable adapter.
   - keep OBJ/FBX and legacy paths here.
   - never allow Assimp native failure to kill the game process during normal asset validation.

3. `Njulf.Assets.Diagnostics`
   - own diagnostics codes, severity policy, JSON reports, and asset classification.

4. `Njulf.Rendering.Resources`
   - own GPU texture creation, sampler creation, compression capability checks, bindless registration, texture memory accounting, and renderer fallbacks.

5. `NjulfHelloGame`
   - use validated asset manifests or generated test assets only.
   - do not directly load unvalidated downloaded assets during startup.

## Phase 0: Crash Containment And Asset Validation CLI

Purpose: stop downloaded assets from taking the game down.

Tasks:

1. Add a small CLI entry point, for example `Njulf.AssetTool`, with:
   - `validate <path-or-folder>`
   - `import <path>`
   - `report <path-or-folder> --json <output>`
2. Run each asset import in a child process by default.
3. Treat process crash, non-zero exit, timeout, and native access violation as validation failures with a structured report.
4. Add timeout and memory guardrails for large assets.
5. Capture importer diagnostics, exception text, process exit code, elapsed time, loaded texture count, estimated texture memory, mesh count, triangle count, material count, animation count, and extension usage.
6. Add a content gate in `NjulfHelloGame`: sample scenes can reference only assets with a checked-in successful validation report or explicitly generated fallback content.
7. Add tests for:
   - missing file.
   - invalid glTF JSON.
   - child-process crash simulated by a test importer.
   - timeout.
   - successful report generation.

Acceptance criteria:

1. The tree `.glb` can fail validation without shutting down the sample game.
2. The asset tool writes a JSON report for the whole `NjulfHelloGame/Assets` folder.
3. CI can run validation on fixture assets.
4. The sample game never directly imports an unknown asset during startup without a controlled failure path.

## Phase 1: glTF/glb Reader Foundation

Purpose: remove Assimp as the source of truth for glTF containers and resource resolution.

Tasks:

1. Add `GltfAssetReader` for `.gltf` and `.glb`.
2. Parse and validate:
   - GLB header, chunk types, chunk lengths, JSON chunk, BIN chunk.
   - glTF version.
   - `buffers`, `bufferViews`, `accessors`.
   - `images`, `textures`, `samplers`.
   - `materials`, `meshes`, `nodes`, `scenes`.
3. Support resource sources:
   - external file URI.
   - data URI.
   - buffer view image.
   - `.glb` BIN chunk.
4. Validate accessor bounds before reading bytes.
5. Support first-slice accessor component types:
   - positions, normals, tangents, UV0, UV1, vertex colors, joints, weights, indices.
6. Add clear diagnostics for unsupported sparse accessors if not implemented immediately.
7. Keep `ModelImporter.Import` as the public facade, but route `.gltf` and `.glb` through the new reader when feature coverage is sufficient.

Acceptance criteria:

1. `.gltf` with external `.bin` imports through the new reader.
2. `.glb` with embedded geometry imports without Assimp.
3. data URI buffers import.
4. buffer-view images and data URI images are resolved.
5. malformed GLB files fail with path and byte offset.

## Phase 2: Mesh, Node, Skin, And Animation Parity

Purpose: replace Assimp geometry dependency without regressing existing sample assets.

Tasks:

1. Implement glTF scene traversal:
   - node transforms.
   - matrix, TRS composition.
   - hierarchy transforms.
   - multi-scene handling.
2. Implement mesh primitive import:
   - triangles only for first shipping slice.
   - reject or triangulate other modes deliberately.
   - preserve primitive material index.
3. Import vertex streams:
   - position.
   - normal.
   - tangent.
   - UV0.
   - UV1.
   - vertex color.
   - joints and weights.
4. Preserve submesh boundaries by primitive.
5. Preserve bounds from accessors when valid, and compute fallback bounds otherwise.
6. Preserve existing animation test coverage:
   - skins.
   - inverse bind matrices.
   - clips.
   - node animation channels.
7. Compare the new reader against current Assimp output on existing fixtures and Sponza.

Acceptance criteria:

1. Existing content and animation tests pass on the new glTF path.
2. Sponza imports with stable object/material/submesh counts.
3. The low-poly tree `.glb` either imports or fails with a managed diagnostic, never a native crash.
4. Meshlet generation works on imported glTF/glb geometry.

## Phase 3: Materials, Texture Slots, And Foliage Classification

Purpose: preserve the information that real assets need for correct rendering and performance decisions.

Tasks:

1. Populate `ModelTextureSlot` for:
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
4. Add importer classification:
   - `Opaque`
   - `Masked`
   - `Blended`
   - `Billboard`
   - `FoliageCandidate`
   - `HighTextureMemory`
   - `UnsupportedRequiredFeature`
5. Add foliage-specific warnings:
   - dense foliage uses `BLEND`.
   - alpha cutoff missing for masked foliage.
   - double-sided disabled for leaf/grass cards.
   - texture dimensions exceed active budget.
   - material requires unsupported translucency/subsurface behavior.
6. Prefer masked `standard/*_nonUE.gltf` style variants for grass validation reports.

Acceptance criteria:

1. Ribbon grass top-level UE files are reported as blended foliage candidates and not selected by default.
2. Ribbon grass `standard/*_nonUE.gltf` files are reported as masked foliage candidates.
3. Texture transform and UV set tests pass through import and GPU material upload.
4. Diagnostics identify fallback texture usage by material and slot.

## Phase 4: Texture System Hardening

Purpose: make texture import predictable, memory-aware, and compatible with real asset packs.

Tasks:

1. Add texture load requests that accept `ModelTextureSource`, not just file paths.
2. Support PNG/JPEG from:
   - file path.
   - memory bytes.
   - buffer-view image.
   - GLB BIN image.
3. Keep KTX2 metadata parsing and add renderer upload for uncompressed Vulkan formats.
4. Add clear fallback or rejection for supercompressed Basis/KTX2 until a transcoder exists.
5. Add compressed format capability checks:
   - BC1/BC3/BC5/BC7.
   - fallback to RGBA8 when policy allows.
6. Include sampler state in the texture cache and bindless registration.
7. Preserve color-space policy by material usage:
   - base color and emissive as sRGB.
   - normal, metallic-roughness, occlusion, masks as linear.
   - HDR environment as HDR linear.
8. Add texture budget reporting to the asset validation report:
   - original dimensions.
   - imported dimensions.
   - mip count.
   - estimated bytes.
   - downscaled flag.
   - compression flag.

Acceptance criteria:

1. Embedded GLB PNG/JPEG images load and upload.
2. Texture cache keys distinguish same bytes with different sampler or color-space state.
3. Large grass tiers report expected texture memory before scene use.
4. Unsupported compressed textures fail or fallback according to explicit policy.

## Phase 5: Import Diagnostics And Failure Policy

Purpose: make every import decision inspectable.

Tasks:

1. Expand `AssetImportMessageCode` for:
   - native importer crash.
   - child process timeout.
   - invalid GLB chunk.
   - invalid accessor bounds.
   - unsupported sparse accessor.
   - unsupported primitive mode.
   - unsupported required extension.
   - optional extension ignored.
   - texture fallback.
   - alpha mode performance warning.
   - excessive texture memory.
2. Add diagnostic source paths using JSON pointers where possible.
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
5. Add a human-readable console summary and machine-readable JSON.

Acceptance criteria:

1. Asset failures are actionable without attaching a debugger.
2. The foliage asset folder can be validated and classified in one command.
3. The report explains why an asset is not used by a sample scene.

## Phase 6: Regression Corpus And CI Gates

Purpose: prevent importer regressions as renderer features expand.

Tasks:

1. Add small fixture assets:
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
   - malformed GLB.
2. Add optional local validation for larger downloaded assets under `NjulfHelloGame/Assets`.
3. Add CI tests:
   - unit reader tests.
   - importer facade tests.
   - texture source tests.
   - diagnostics JSON schema tests.
   - child-process validator tests.
4. Track import times and memory in reports.
5. Add an allowlist file for sample assets with expected diagnostics.

Acceptance criteria:

1. Full test suite covers all supported glTF resource kinds.
2. Known-bad assets fail the same way every run.
3. Sample assets cannot silently change import class or warning count without test failure.

## Phase 7: Migration And Cleanup

Purpose: make the production path the default and reduce duplicate behavior.

Tasks:

1. Switch `.gltf` and `.glb` imports to the new reader by default.
2. Keep Assimp as fallback only for non-glTF formats and optionally as a comparison tool.
3. Remove or quarantine old partial glTF manifest parsing once parity is proven.
4. Update `ContentManager` cache keys to include import policy, texture budget, and relevant importer options.
5. Add sample-scene asset manifests that reference validated assets and chosen variants.
6. Document artist-facing export recommendations:
   - use glTF 2.0.
   - prefer masked alpha for foliage.
   - prefer KTX2/BC when supported.
   - include LODs or game-ready mesh density.
   - keep texture dimensions within active budget.

Acceptance criteria:

1. `ModelImporter.Import` is stable and deterministic for glTF/glb without Assimp.
2. Non-glTF formats still have a legacy path.
3. Sample game startup uses only validated assets or generated test content.
4. Documentation explains how to prepare assets for the renderer.

## Immediate Recommended First PR

Start with crash containment and validation reports before replacing the importer internals.

Scope:

1. Add `Njulf.AssetTool`.
2. Add child-process import validation.
3. Add JSON validation report model.
4. Validate `NjulfHelloGame/Assets`.
5. Classify alpha mode and texture memory for the ribbon grass files.
6. Add tests for crash, timeout, missing file, invalid glTF, and successful report.

Why this first: it makes the renderer safer immediately and gives us a repeatable way to evaluate the tree `.glb`, grass `.gltf`, Sponza, Strut, and future downloaded assets while the glTF-first importer is built.

## Risks

1. Replacing Assimp can change transforms, winding, tangents, UV orientation, or animation behavior. Keep comparison tests during migration.
2. Texture memory can dominate asset acceptance. Validation reports must show budget impact before scene use.
3. glTF extension support can sprawl. Support core transport first, then material extensions deliberately.
4. Child-process validation adds tooling complexity, but it is the correct boundary for native importer crash containment.
5. Asset packs vary in layout conventions. The importer must report package structure clearly instead of relying on guessed paths.

## Done Criteria For The Downloaded Foliage Assets

The current downloaded tree and grass assets are acceptable for sample use only when:

1. validation runs without crashing the game process.
2. `low_poly_trees_free.glb` either imports through the glTF-first path or is rejected with a managed diagnostic.
3. ribbon grass `standard/*_nonUE.gltf` variants are selected over the top-level blended UE variants for real-time foliage.
4. texture memory for the selected grass tier is within the active sample budget.
5. the asset report is checked in or generated by CI.
6. the sample scene references the validated asset variant explicitly.
