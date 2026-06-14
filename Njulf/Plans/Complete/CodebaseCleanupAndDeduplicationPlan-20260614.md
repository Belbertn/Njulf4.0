# Codebase Cleanup And Deduplication Plan - 2026-06-14

## Scope

This pass looked for duplicated code, duplicated paths/assets, oversized ownership boundaries, and organization issues that make future renderer work harder. It did not attempt to implement changes.

The highest-value cleanup is in renderer infrastructure, not small style fixes. The repo has a few very large files, but the main issue is repeated Vulkan upload, barrier, dynamic-rendering, pipeline, shader, and sample-control patterns.

## Current Hotspots

| Area | Evidence | Cleanup value |
| --- | --- | --- |
| Renderer orchestration | `Njulf.Rendering/VulkanRenderer.cs` is 2466 lines and owns pass order, frame lifecycle, shadow upload signatures, diagnostics, budget snapshots, screenshots, RenderDoc, and swapchain recovery. | High |
| Asset importing | `Njulf.Assets/ModelImporter.cs` is 2414 lines and contains importing, glTF parsing, animation conversion, DTOs, diagnostics manifests, and texture helpers. | High |
| Scene payload building | `Njulf.Rendering/Data/SceneDataBuilder.cs` is 2242 lines and has repeated buffer/list/upload-state arrays for each payload stream. | High |
| Mesh/resource management | `Njulf.Rendering/Resources/MeshManager.cs` is 2078 lines and mixes upload allocation, meshlet construction, LOD generation, metadata packing, and GPU buffer registration. | High |
| Settings buckets | `Njulf.Rendering/Data/RenderSettings.cs` is 1640 lines with 43 types. | Medium |
| Sample input | `NjulfHelloGame/SampleInputController.cs` is 1344 lines and combines action registration, key binding tables, feature toggles, setting mutation, diagnostics printing, camera input, and scenario controls. | Medium |

## Findings

### 1. Duplicate meshlet layout exists in assets and rendering

`Njulf.Assets/Meshlet.cs:7` defines `Njulf.Assets.Meshlet`, and `Njulf.Rendering/Resources/MeshManager.cs:16` defines another `Meshlet` with the same sequential layout fields.

This is a correctness risk because the asset-side meshlet contract and renderer-side GPU upload contract can drift independently. The existing duplication is small, but it sits on a core binary layout.

Recommended change:

- Move the shared meshlet data contracts into a neutral place, probably `Njulf.Core/Geometry` or a new `Njulf.Core/Meshes` namespace.
- Keep rendering-only metadata such as `MeshInfo` in `Njulf.Rendering`.
- Add a layout test that verifies `Meshlet` size/offsets once against shader expectations.

### 2. SceneDataBuilder has parallel per-stream arrays and duplicated upload flow

`SceneDataBuilder` keeps separate buffers, lists, and upload states for object data, instances, opaque meshlet draws, depth meshlet draws, masked depth meshlet draws, transparent meshlet draws, directional shadow meshlet draws, and local shadow meshlet draws.

Examples:

- Buffers: `Njulf.Rendering/Data/SceneDataBuilder.cs:60`
- Lists: `Njulf.Rendering/Data/SceneDataBuilder.cs:72`
- Upload states: `Njulf.Rendering/Data/SceneDataBuilder.cs:86`
- Capacity checks and upload calls: `Njulf.Rendering/Data/SceneDataBuilder.cs:367`

Recommended change:

- Introduce a typed `SceneBufferStream<T>` or `FrameSceneBufferStream<T>` that owns the `SceneBuffer`, per-frame buffers when needed, `UploadState`, stride, category, capacity growth, and upload-if-needed behavior.
- Replace repeated upload bookkeeping with stream registration and iteration.
- Keep the public `SceneRenderingData` shape stable initially; reduce implementation duplication before changing external contracts.

This should make adding another render stream a data-entry change instead of copying five fields and several branches.

### 3. Repeated staging-buffer upload and barrier code across resource managers

The same pattern appears in multiple places:

- Allocate from `StagingRing`
- Copy CPU span into mapped memory
- Flush mapped range
- Emit `BufferCopy`
- Call `CmdCopyBuffer`
- Emit a read barrier

Observed in:

- `Njulf.Rendering/Data/SceneDataBuilder.cs:1543`
- `Njulf.Rendering/Resources/MaterialManager.cs:616`
- `Njulf.Rendering/Resources/SkinningManager.cs:211`
- `Njulf.Rendering/Resources/ParticleSystemManager.cs:895`
- `Njulf.Rendering/Resources/LightManager.cs:306`
- `Njulf.Rendering/Resources/EnvironmentManager.cs:101`
- `Njulf.Rendering/Resources/DirectionalShadowResources.cs:112`
- `Njulf.Rendering/Resources/SpotShadowAtlas.cs:232`
- `Njulf.Rendering/Resources/PointShadowCubemapArray.cs:104`
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs:78`

Recommended change:

- Add a `GpuUploadService` or extend `StagingRing` with a helper like `UploadSpanToBuffer<T>(CommandBuffer, BufferHandle, ReadOnlySpan<T>, UploadBarrierDescription?)`.
- Return an `UploadResult` containing byte count, staging handle/offset if needed, and whether work was recorded.
- Centralize common buffer barriers in `BarrierBuilder` or a small `BufferBarrierFactory`.

Do this carefully: resource managers still own their destination resources; the helper should only own the repeated staging-copy mechanics.

### 4. Dynamic rendering setup is copied across graphics passes

Many passes repeat viewport/scissor setup, descriptor set binding, color/depth attachment creation, `RenderingInfo`, begin/end rendering, and push constants.

Examples:

- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs:43`
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs:42`
- `Njulf.Rendering/Pipeline/SkyboxPass.cs:43`
- `Njulf.Rendering/Pipeline/ParticlePass.cs:42`
- `Njulf.Rendering/Pipeline/ToneMapCompositePass.cs:55`
- `Njulf.Rendering/Pipeline/DepthPrePass.cs:44`
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs:45`
- `Njulf.Rendering/Pipeline/SpotShadowPass.cs:73`
- `Njulf.Rendering/Pipeline/PointShadowPass.cs:81`
- `Njulf.Rendering/Pipeline/AntiAliasingPass.cs:195` and `Njulf.Rendering/Pipeline/AntiAliasingPass.cs:282`

Recommended change:

- Extend `RenderPassBase` with protected helpers for:
  - `SetFullViewportAndScissor(CommandBuffer, Extent2D)`
  - Binding bindless storage/texture descriptor sets for a layout
  - Building common color/depth `RenderingAttachmentInfo`
  - `BeginRendering` wrappers for full-screen color, color+depth, depth-only, and atlas/layered depth cases
- Keep specialized render pass behavior in the pass classes.

This reduces boilerplate without hiding the pass order or render target transitions.

### 5. Graphics pipeline object creation repeats Vulkan boilerplate

`CompositePipeline`, `SkyboxPipeline`, `ParticlePipeline`, and parts of `MeshPipeline` repeat:

- Pipeline cache creation
- Pipeline layout creation
- Push constant validation
- Shader stage creation
- Dynamic viewport/scissor state
- Rasterization, multisample, color blend, dynamic rendering structs
- Disposal of pipelines/layout/cache/entry point memory

Examples:

- `Njulf.Rendering/Pipeline/PipelineObjects/CompositePipeline.cs:15`
- `Njulf.Rendering/Pipeline/PipelineObjects/SkyboxPipeline.cs:15`
- `Njulf.Rendering/Pipeline/PipelineObjects/ParticlePipeline.cs:11`
- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs:297`

Recommended change:

- Add a small `GraphicsPipelineBuilder` or `GraphicsPipelineFactory`.
- Keep pipeline classes as named owners of pipeline variants, but move common struct defaults into reusable methods.
- Add small option records for blend, depth, shader paths, render formats, and push constant stage/size.

Avoid a generic mega-builder. The goal is to remove repeated Vulkan defaults while keeping pipeline setup readable.

### 6. Depth and shadow-depth mesh shaders are near duplicates

The following shader pairs are mostly identical:

- `Njulf.Shaders/depth.mesh`
- `Njulf.Shaders/shadow_depth.mesh`
- `Njulf.Shaders/depth_alpha.mesh`
- `Njulf.Shaders/shadow_depth_alpha.mesh`

The main differences are payload names and whether alpha material outputs are written.

Recommended change:

- Extract shared depth mesh logic into a GLSL include, or generate these shader variants from a small template.
- Keep variant-specific names and outputs explicit at the top of each shader.
- Add shader build tests to cover all generated/included variants.

### 7. Static and culling scene signatures duplicate traversal logic

`StaticScenePayloadSignature.Create` and `SceneCullingSignature.Create` both walk `RenderObjects` and `StaticInstanceBatches`, hashing much of the same object identity, visibility, mesh, material, batch revision, and world matrix data.

References:

- `Njulf.Rendering/Data/SceneDataBuilder.cs:2090`
- `Njulf.Rendering/Data/SceneDataBuilder.cs:2162`

Recommended change:

- Extract shared scene traversal hashing into a helper that accepts extra per-object/per-batch hash callbacks.
- Keep separate signature types; they encode different invalidation scopes.
- Add tests for invalidation behavior before refactoring, because false cache hits here would cause stale scene buffers.

### 8. ModelImporter mixes import workflow, glTF parsing, and public contracts

`ModelImporter` contains:

- Assimp scene loading and node traversal
- Animation/skeleton conversion
- glTF JSON parsing and validation
- material extension parsing
- texture URI/buffer extraction
- public DTOs such as `ModelMesh`, `ModelSubMesh`, `ModelMaterial`
- internal glTF/Assimp manifests

Some duplicated shape exists between `ModelMaterial` and internal `GltfMaterial`, especially texture path/slot and material extension fields near `Njulf.Assets/ModelImporter.cs:2203` and `Njulf.Assets/ModelImporter.cs:2337`.

Recommended change:

- Move public asset contracts into separate files under `Njulf.Assets/Contracts` or keep them in the existing project root as one-type-per-file.
- Split importer internals into:
  - `AssimpSceneReader`
  - `GltfManifestReader`
  - `GltfMaterialReader`
  - `AnimationImportBuilder`
  - `ModelMaterialMapper`
- Replace repeated `ModelMaterial`/`GltfMaterial` field copying with a mapper and, where possible, shared material texture-slot records.

Do this in slices. `ModelImporter` has broad test coverage and should be kept behavior-stable while moving code.

### 9. Render settings and diagnostics files are type buckets

`Njulf.Rendering/Data/RenderSettings.cs` has 43 types and `Njulf.Rendering/Data/RendererDiagnostics.cs` has a large diagnostics surface. These files are not necessarily wrong, but they are hard to navigate and encourage unrelated settings to accumulate.

Recommended change:

- Split settings by feature:
  - `BloomSettings`
  - `ShadowSettings`
  - `AmbientOcclusionSettings`
  - `AntiAliasingSettings`
  - `FogSettings`
  - `ReflectionSettings`
  - `ParticleSettings`
  - `DebugOverlaySettings` already has its own folder; keep debug-related settings there where possible.
- Split diagnostics into feature-specific snapshots or partial files if public API compatibility matters.
- Keep `RenderSettings` as the aggregate root so callers do not churn.

### 10. SampleInputController is a hidden control registry

`SampleInputController` contains constants for actions, key binding creation, setting mutation, diagnostics printing, camera movement, debug selection, and performance scenario controls.

References:

- Action constants: `NjulfHelloGame/SampleInputController.cs:30`
- Key registration: `NjulfHelloGame/SampleInputController.cs:216`
- Toggle/update logic: `NjulfHelloGame/SampleInputController.cs:305`
- Settings print methods: `NjulfHelloGame/SampleInputController.cs:931`

Recommended change:

- Introduce a declarative `SampleActionBinding` table with action id, key, optional chord, and handler.
- Split feature control groups into small classes or methods:
  - camera movement
  - render feature toggles
  - quality/performance controls
  - debug/object selection
  - particle controls
- Move console formatting to `SampleDiagnosticsReporter` or a new `SampleControlReporter`.

This should reduce mistakes when adding new debug controls.

### 11. Duplicate sample assets and stale local artifacts

Exact duplicate tracked assets:

- `NjulfHelloGame/vintage_video_camera_2k.gltf`
- `NjulfHelloGame/Old/vintage_video_camera_2k.gltf`

Exact duplicate texture payloads:

- `NjulfHelloGame/textures/col_brickwall_01_Roughnesscol_brickwall_01_Metalness.png`
- `NjulfHelloGame/textures/col_brickwall_01_Roughnesscolumn_brickwall_01_Metalness.png`

Identical metalness placeholders:

- `NjulfHelloGame/textures/ceiling_plaster_01_Metalness.png`
- `NjulfHelloGame/textures/ceiling_plaster_02_Metalness.png`
- `NjulfHelloGame/textures/roof_tiles_01_Metalness.png`
- `NjulfHelloGame/textures/wood_tile_01_Metalness.png`

The `artifacts/` folder also exists locally with a published game build and copied shaders. `git ls-files artifacts` returned no tracked files, and there is no `.gitignore` at repo root.

Recommended change:

- Decide whether `NjulfHelloGame/Old` is still needed. If it is only archival, move it outside the repo or document it under a sample asset manifest.
- Remove or de-duplicate exact duplicate sample assets after checking model references.
- Add a root `.gitignore` that excludes `artifacts/`, `bin/`, `obj/`, Rider/IDE user state, and publish zips.
- Consider asset-manifest indirection for shared placeholder maps instead of storing identical files under different material names.

## Proposed Execution Order

### Phase 1 - Low-risk repository hygiene

1. Add `.gitignore` for build and publish outputs.
2. Confirm references to duplicated sample assets.
3. Remove or relocate exact duplicate assets that are not referenced.
4. Add a small asset reference check if sample manifests continue to grow.

### Phase 2 - Shared layout contracts

1. Move `Meshlet` to a shared namespace.
2. Update assets and rendering references.
3. Add or extend layout tests for meshlet fields and size.

### Phase 3 - Upload infrastructure cleanup

1. Add a common buffer upload helper around `StagingRing`.
2. Migrate one small resource manager first, such as `DirectionalShadowResources` or `PointShadowCubemapArray`.
3. Migrate `MaterialManager`, `SkinningManager`, `ParticleSystemManager`, and `SceneDataBuilder` once the helper shape is proven.
4. Keep upload byte diagnostics intact during each migration.

### Phase 4 - SceneDataBuilder stream abstraction

1. Add `SceneBufferStream<T>` for capacity, upload state, and byte accounting.
2. Convert meshlet draw streams first, since they share the same element type and behavior.
3. Convert object/instance buffers after meshlet stream tests pass.
4. Preserve existing `SceneRenderingData` outputs during the first pass.

### Phase 5 - Render pass and pipeline helpers

1. Add protected helpers to `RenderPassBase` for viewport/scissor, bindless set binding, and common rendering attachments.
2. Convert `ForwardPlusPass` and `TransparentForwardPass` together because they share the most obvious pattern.
3. Convert full-screen passes.
4. Add a lightweight graphics pipeline factory for common pipeline object defaults.
5. Convert `CompositePipeline`, `SkyboxPipeline`, and `ParticlePipeline` before touching `MeshPipeline`.

### Phase 6 - Asset importer split

1. Move public DTOs out of `ModelImporter.cs`.
2. Extract glTF manifest parsing without changing behavior.
3. Extract material mapping.
4. Extract animation conversion.
5. Leave a thin `ModelImporter.Import` facade.

### Phase 7 - Sample controls

1. Introduce a declarative action binding table.
2. Split update handlers by feature area.
3. Move console output into a reporter.
4. Keep the same key bindings unless intentionally changing sample UX.

## Validation Checklist

- `dotnet test`
- Existing shader build tests pass after shader include/template changes.
- Sample game starts and renders the main scene.
- Renderer diagnostics and performance snapshot fields remain populated after upload refactors.
- Asset importer tests still pass for glTF, GLB, animation, material extension, and missing asset diagnostics.
- A before/after smoke run confirms no regressions in pass order, shadow rendering, transparent rendering, particles, and anti-aliasing.

## Notes

- Avoid starting with `VulkanRenderer` extraction. It is tempting, but upload/pass helper work will reduce the renderer naturally and with lower risk.
- Avoid broad namespace churn in one commit. Split by behavior boundary so review can verify that moves are mechanical.
- The shader and upload refactors are the clearest duplicate-code wins.
