# Renderer Architecture 02 - GPU Scene Plan - 2026-06-14

Target outcome: the renderer owns a persistent GPU scene. CPU code updates dirty scene data, while GPU passes consume stable object, instance, material, mesh, light, decal, particle, and visibility buffers. Per-frame CPU list building is reduced to change detection, uploads, and graph execution.

This is a foundational performance change. It must replace old per-frame CPU-built render lists as the GPU-driven pipeline comes online. No stub buffers, duplicate authoritative data stores, or CPU fallback render paths should remain after migration.

## Non-Negotiable Requirements

1. Every renderable object has a stable GPU scene ID and generation.
2. GPU buffers are persistent and updated incrementally.
3. CPU uploads only dirty ranges, not the full scene, except when compaction or resize requires it.
4. All GPU data layouts are versioned and covered by layout tests.
5. Materials, meshes, instances, lights, decals, particles, and animation data are addressable by GPU passes.
6. Old per-frame CPU render-command generation is removed after GPU-driven equivalents are validated.
7. The GPU scene supports multi-frame buffering without stale reads or write hazards.

## Phase 0 - Define GPU Scene Ownership

1. Add a `GpuSceneManager` under `Njulf.Rendering/Resources` or a dedicated `Njulf.Rendering/GpuScene` namespace.
2. Define ownership boundaries:
   - `MeshManager` owns mesh buffers and meshlet data.
   - `MaterialManager` owns material data and texture bindings.
   - `GpuSceneManager` owns object, instance, transform, visibility, and per-frame scene indices.
   - `LightManager` either migrates into GPU scene ownership or exposes its buffers through the GPU scene interface.
3. Define stable IDs:
   - `GpuObjectId`.
   - `GpuInstanceId`.
   - `GpuMaterialId`.
   - `GpuMeshId`.
   - `GpuLightId`.
   - `GpuDecalId`.
   - `GpuParticleEmitterId`.
4. Add generation checks so stale IDs cannot address recycled slots.
5. Add a migration map from existing `RenderObject` and `StaticInstanceBatch` to GPU IDs.

Acceptance criteria:
- CPU code can register, update, and remove objects with stable GPU scene IDs.
- Removed slots cannot be used accidentally without generation mismatch diagnostics.

## Phase 1 - GPU Data Layouts

1. Define packed GPU structs:
   - `GPUSceneObject`.
   - `GPUSceneInstance`.
   - `GPUTransform`.
   - `GPUPreviousTransform`.
   - `GPUVisibilityState`.
   - `GPUObjectBounds`.
   - `GPULodState`.
   - `GPUSceneLightReference`.
   - `GPUSceneDecalReference`.
2. Include fields needed by all downstream passes:
   - current world matrix.
   - previous world matrix.
   - normal matrix or packed scale if needed.
   - mesh index.
   - material index.
   - bounding sphere.
   - bounding box.
   - flags for static, skinned, transparent, alpha-tested, decal, foliage, impostor-capable, casts shadows, receives shadows.
   - LOD ranges.
   - visibility masks.
3. Align structs to Vulkan storage-buffer rules.
4. Add tests that compare C# struct offsets against shader include constants.
5. Generate shared layout constants for GLSL from C# or validate both from one source.

Acceptance criteria:
- All GPU scene structs have deterministic layout tests.
- Shaders and C# agree on every offset and stride.

## Phase 2 - Persistent Buffer Allocation

1. Create growable GPU buffers for:
   - scene objects.
   - instances.
   - transforms.
   - previous transforms.
   - bounds.
   - material references.
   - visibility state.
   - per-pass compacted output indices.
2. Allocate with capacity headroom and explicit resize policy.
3. Resize by allocating a new buffer, copying old contents on GPU, and retiring old buffers after fences.
4. Track used count, capacity, high-water mark, upload bytes, and resize count.
5. Register buffers in the bindless heap once per allocation generation.
6. Add debug names and memory category attribution.

Acceptance criteria:
- Adding thousands of objects does not cause per-frame full-buffer recreation.
- Buffer growth is visible in diagnostics.

## Phase 3 - Dirty Range Tracking

1. Add dirty flags for:
   - transform changed.
   - previous transform changed.
   - material changed.
   - mesh changed.
   - bounds changed.
   - visibility flags changed.
   - light/decal/particle association changed.
2. Group dirty slots into upload ranges.
3. Use staging uploads for dirty ranges only.
4. Coalesce adjacent dirty ranges to reduce copy commands.
5. Keep a full rebuild path only for verified structural changes such as buffer compaction or asset reload.
6. Report per-category upload bytes every frame.

Acceptance criteria:
- Moving one object uploads one object transform range, not the full object buffer.
- Material-only changes do not upload transform data.

## Phase 4 - Scene Registration API

1. Add API methods:
   - `RegisterObject`.
   - `UpdateObjectTransform`.
   - `UpdateObjectMaterial`.
   - `UpdateObjectMesh`.
   - `UpdateObjectFlags`.
   - `RemoveObject`.
   - `RegisterStaticBatch`.
   - `UpdateStaticInstanceRange`.
   - `RemoveStaticBatch`.
2. Validate inputs immediately:
   - valid mesh handle.
   - valid material handle.
   - finite transform.
   - finite bounds.
   - supported render mode.
3. Make scene update calls deterministic and thread-safe where scene mutation is already thread-safe.
4. Add a clear point in frame lifecycle where changes are frozen for rendering.
5. Add ID remapping support for editor/runtime object selection.

Acceptance criteria:
- Existing `Scene` objects can be mirrored into the GPU scene without changing gameplay code.
- Invalid scene data fails with actionable diagnostics before GPU upload.

## Phase 5 - Migrate Materials And Mesh Metadata

1. Replace per-frame material snapshot upload with dirty material updates where possible.
2. Ensure material flags are directly readable by GPU visibility and sorting passes.
3. Make mesh metadata include:
   - LOD ranges.
   - meshlet ranges per LOD.
   - bounds.
   - alpha/material flags summary.
   - meshlet cone data once available.
4. Add bindless indices for mesh metadata and material metadata through the GPU scene interface.
5. Remove duplicate CPU-side material classification from render-list building after GPU classification is implemented.

Acceptance criteria:
- GPU culling can decide opaque, masked, transparent, decal, shadow-caster, and foliage categories without CPU rebuilding lists.

## Phase 6 - Previous Frame Data

1. Store previous transforms persistently.
2. Update previous transforms at the end of a successful frame, not at scene build start.
3. Handle newly spawned objects by copying current to previous.
4. Handle teleports with explicit history reset flags.
5. Expose previous transforms to motion vectors, TAA, and future ray tracing/denoising.
6. Add tests for object spawn, removal, teleport, and normal motion.

Acceptance criteria:
- Motion vectors do not break when CPU render-list building is removed.
- TAA history invalidates correctly for new or teleported objects.

## Phase 7 - Multi-Frame Synchronization

1. Separate persistent scene buffers from per-frame scratch buffers.
2. Use ring-buffered upload staging for dirty updates.
3. Avoid CPU writes into GPU-read ranges until the owning frame fence completes.
4. Track per-frame resource generations.
5. Add assertions that prevent modifying a buffer range still in use unless the update goes through staging/copy.
6. Add stress test with rapid object creation/deletion across frames.

Acceptance criteria:
- No device idle waits are required for normal scene mutation.
- Rapid updates do not corrupt in-flight frames.

## Phase 8 - Integrate With Render Graph

1. Register GPU scene buffers as persistent imported graph buffers.
2. Declare read/write access per pass:
   - upload/copy writes.
   - culling reads objects and writes visibility.
   - render passes read compacted lists.
   - diagnostics read counters.
3. Let the graph generate buffer barriers between upload, compute, and graphics passes.
4. Remove direct pass assumptions about buffer readiness.

Acceptance criteria:
- GPU scene uploads and consumers are visible in graph diagnostics.
- Buffer hazards are graph-generated.

## Phase 9 - Remove Old Per-Frame Architecture

Delete after validated replacements exist:

1. Per-frame full object data rebuilds.
2. Per-frame CPU meshlet draw command generation for steady-state objects.
3. CPU-maintained opaque/masked/transparent render lists as authoritative data.
4. Duplicate previous-transform dictionaries once GPU scene previous transforms are authoritative.
5. CPU-only visibility fields that no longer feed rendering.
6. Any fallback path that silently reverts to CPU list generation outside explicit debug mode.

Acceptance criteria:
- The renderer has one source of truth for renderable scene data.
- CPU render-list generation exists only as a temporary migration tool or explicit comparison mode, then is deleted.

## Validation

1. Unit tests:
   - ID generation and stale ID rejection.
   - dirty range coalescing.
   - buffer resize accounting.
   - struct layout compatibility.
   - previous transform update rules.
2. Integration tests:
   - add/remove/update thousands of objects.
   - material changes while objects are visible.
   - static batch updates.
   - skinned object updates.
   - transparent/decal/foliage flags.
3. GPU validation:
   - RenderDoc buffer inspection.
   - motion vector correctness.
   - scene mutation while frames are in flight.
4. Performance validation:
   - CPU scene build time decreases as object count rises.
   - upload bytes scale with changed objects, not total objects.
   - no new frame-fence stalls.

## Definition Of Done

1. A persistent GPU scene feeds all production rendering categories.
2. Dirty updates replace full per-frame rebuilds.
3. Existing render features continue to work from GPU scene buffers.
4. Old CPU-authoritative render-list code is removed after migration.
5. Diagnostics prove reduced CPU build time and bounded upload bytes.

## Implementation Notes - 2026-06-14

- Phase 0/1 implemented through `Njulf.Rendering/GpuScene/GpuSceneManager.cs`, stable GPU scene ID structs, packed GPU scene structs in `Njulf.Rendering/Data/GPUStructs.cs`, shader constants in `Njulf.Shaders/common.glsl`, and layout/ID tests.
- Phase 2/3 implemented through `GpuSceneBufferSet`: persistent object, instance, transform, previous-transform, bounds, visibility, and compacted-index buffers with bindless registration, capacity growth, resize accounting, dirty range uploads, and tests proving transform-only uploads stay transform-only.
- Phase 4 is implemented for render-object and static-batch mirroring. GPU scene registration fails hard for invalid mesh handles before upload; remaining default-material compatibility is tracked until importer/sample paths provide explicit valid material handles everywhere.
- Phase 6 previous-transform rules are implemented in `GpuSceneManager`: spawned objects copy current to previous, normal motion advances previous data only after a successful frame, and teleports can reset history immediately.
- Phase 8 is implemented for production consumers: GPU scene buffers have fixed bindless indices, external render-graph resource declarations through `ProductionRenderGraphResources.GpuSceneBuffers`, live buffer inventory entries, and GPU visibility reads them to build production draw lists.
- Phase 9 production migration is implemented for meshlet draw-list generation: `SceneDataBuilder` no longer builds CPU meshlet lists for production, previous transforms are resolved from `GpuSceneManager`, and depth/shadow/forward/transparent passes consume GPU visibility buffers. The remaining work is validation and deletion of compatibility adapter APIs after graph ownership cleanup.
