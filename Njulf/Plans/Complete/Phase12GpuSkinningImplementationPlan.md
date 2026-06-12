# Phase 12 GPU Skinning Implementation Plan

Goal: make GPU compute skinning the production path for skinned glTF meshes while preserving the existing static mesh path and the Phase 12 CPU animation/import contracts.

This plan starts from the current Phase 12 foundation:
- `Njulf.Core.Animation` owns skeleton, skin, clip, animator, pose, and CPU skinning contracts.
- `ModelImporter` imports glTF skins, `JOINTS_0`, `WEIGHTS_0`, inverse bind matrices, and clips.
- `SkinnedRenderObject` exists, and `Model.CreateInstance()` creates independent animator state.
- `RenderSettings.Animation` and animation diagnostics fields exist.
- Static mesh rendering still uses `GPUVertex`, `MeshManager`, scene mesh metadata, meshlet draw buffers, and mesh/task shader vertex fetch.

## Non-Goals For This Slice

- No full animation graph, IK, ragdoll, retargeting, morph targets, or root motion.
- No skeletal LOD or per-meshlet animated bounds beyond a conservative first pass.
- No CPU skinning as the normal render path. CPU skinning remains test/debug reference only.
- No redesign of the current meshlet renderer.

## Architecture Decision

Use compute skinning before all passes that read vertex buffers.

Why:
- The renderer already has depth, shadow, forward, transparent, fog, bloom, and composite sequencing.
- Compute skinning lets all geometry passes read one skinned vertex stream instead of duplicating skinning in every mesh shader.
- It keeps static `GPUVertex` unchanged, so static meshes do not pay extra vertex stride.
- It gives RenderDoc one explicit `SkinningPass` to inspect and time.

Frame order after this slice:
1. Scene/game update.
2. CPU animation sampling updates per-instance skin matrices.
3. Skin matrix upload.
4. `SkinningPass` writes skinned vertices.
5. Directional/local shadow passes.
6. Depth prepass.
7. Hi-Z, AO, tiled light culling.
8. Opaque/transparent forward passes.
9. Fog, bloom, composite, anti-aliasing, present.

## Data Contracts

### GPU Structs

Add to `Njulf.Rendering/Data/GPUStructs.cs` or `Njulf.Rendering/Data/AnimationData.cs`:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUVertexSkinningData
{
    public uint Joint0;
    public uint Joint1;
    public uint Joint2;
    public uint Joint3;
    public float Weight0;
    public float Weight1;
    public float Weight2;
    public float Weight3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSkinningDispatch
{
    public uint SourceVertexOffset;
    public uint SourceSkinningDataOffset;
    public uint DestinationVertexOffset;
    public uint VertexCount;
    public uint SkinMatrixOffset;
    public uint ObjectIndex;
    public uint SourceMeshMetadataIndex;
    public uint Flags;
}
```

Use `uint4` + `float4` first for correctness. Optimize to packed 16-bit joints/weights only after CPU/GPU parity and RenderDoc validation are stable.

### Mesh Metadata

Extend mesh metadata with enough information for shaders to select the vertex stream:
- `VertexFetchMode`: static or skinned.
- `SkinnedVertexOffset`: destination offset in skinned vertex buffer.
- `SkinningDataOffset`: source joint/weight stream offset.
- `SkinningDataCount`.
- Conservative bounds padding or expanded meshlet radius for skinned meshes.

Do not add skinning data to `GPUVertex`.

## Bindless Indices

Extend `BindlessIndexTable.cs` and `common.glsl` with static buffer slots:
- `SkinningVertexDataBuffer`
- `SkinMatrixBufferBase`
- `SkinMatrixBufferFrame1`
- `SkinnedVertexBufferBase`
- `SkinnedVertexBufferFrame1`
- `SkinningDispatchBufferBase`
- `SkinningDispatchBufferFrame1`

Update:
- `DescriptorSetLayouts`
- `BindlessIndexTests`
- shader constant parsing tests

Keep `StaticBufferCount` contiguous and tested.

## Mesh Upload Changes

### `MeshManager`

Add optional skinning input to `MeshRegistrationData`:
- `GPUVertexSkinningData[]? SkinningData`
- `bool IsSkinned`
- optional conservative meshlet bounds expansion value

Add owned buffers:
- skinning input buffer
- skinned output vertex buffers, double buffered per frame or one reusable storage buffer with frame-safe offsets

Static mesh path must remain unchanged:
- `RegisterMesh(GPUVertex[], uint[])` still works.
- static meshes allocate only base vertex/index/meshlet resources.
- static mesh metadata points at `BindlessIndex.VertexBuffer`.

Skinned mesh upload:
- base bind-pose `GPUVertex` still uploads to the normal vertex buffer.
- joint/weight data uploads to skinning input buffer.
- skinned output allocation reserves `GPUVertex`-sized space for each skinned mesh instance or active render object, not just per asset, because instances can animate independently.

Validation:
- reject skinned submesh upload when joint/weight streams are missing or count mismatches vertex count.
- reject joint indices outside the imported skin joint count before GPU upload.

## Runtime Skinning Ownership

Add `Njulf.Rendering/Resources/SkinningManager.cs`.

Responsibilities:
- Own per-frame skin matrix buffers.
- Own per-frame or persistent skinned output vertex buffers.
- Own per-frame skinning dispatch buffers.
- Allocate per skinned render object instance.
- Upload final skin matrices from `Animator.GetSkinMatrices()`.
- Build `GPUSkinningDispatch` records from visible or active skinned objects.
- Track resize/growth and safe retirement through existing `FenceBasedDeleter`/buffer manager patterns.
- Register bindless buffers when a heap exists or buffers grow.
- Expose diagnostics:
  - skinned object count
  - skinned vertex count
  - dispatch count
  - joint matrix count
  - upload bytes
  - buffer sizes
  - CPU upload/record time
  - GPU skinning time

Initial dispatch policy:
- Dispatch all enabled visible skinned objects.
- If `RenderSettings.Animation.UpdateWhenOffscreen` is true, keep sampling offscreen animators but only skin objects needed for rendering/shadows.
- Add offscreen skinning only if later features need readback/debug pose vertices.

## Scene Integration

### `SceneDataBuilder`

Extend static/culling signatures for skinned objects:
- include animator pose revision or skinning allocation revision.
- include animated bounds mode/padding.

Add animated bounds:
- `SkinnedRenderObject.AnimatedBoundingBox` first.
- If null, use mesh bounds expanded by `RenderSettings.Animation.BoundsPadding`.
- Later: per-clip imported bounds.

CPU culling:
- use animated bounds for object culling.
- expand skinned meshlet bounding spheres by the same conservative padding for meshlet culling.
- include skinned objects in directional and local shadow meshlet draw buffers.

Meshlet draw commands:
- carry enough metadata/object flags so shaders can fetch from static or skinned vertex buffer.
- keep existing draw command layout stable if possible; otherwise update layout tests and all mesh/task shaders together.

### `VulkanRenderer`

Add `SkinningManager` construction and disposal beside other managers.

Frame sequencing:
- after scene data build and before shadow/depth/forward passes:
  - call skin matrix upload.
  - record `SkinningPass`.
  - insert buffer barrier from compute shader write to mesh/task/vertex shader read.

Settings behavior:
- `AnimationSettings.Enabled = false`: do not advance GPU skinning; render bind pose or freeze last skinned output according to documented choice. Initial choice: render bind pose by selecting static vertex buffer.
- `SkinningMode.Disabled`: skip dispatch and use static vertices.
- `SkinningMode.CpuDebug`: optional later debug-only CPU-skinned upload; do not implement in the first GPU slice.
- `SkinningMode.GpuCompute`: production path.

## Skinning Pass

Add:
- `Njulf.Rendering/Pipeline/SkinningPass.cs`
- `Njulf.Shaders/skinning.comp`
- shader build tests for `skinning.comp`

Compute shader inputs:
- base bind-pose vertex buffer
- skinning vertex data buffer
- skin matrix buffer
- dispatch buffer
- output skinned vertex buffer

Per vertex:
1. Read bind-pose `GPUVertex`.
2. Read four joints and weights.
3. Normalize weights defensively if total is near 1 but not exact.
4. Compute weighted skinned position.
5. Transform normal and tangent directions with the 3x3 portion of weighted skin matrices.
6. Re-normalize normal/tangent.
7. Preserve UVs, tangent handedness, and padding.
8. Write `GPUVertex` to output buffer.

Dispatch shape:
- one dispatch record per skinned object/submesh instance.
- one thread per vertex.
- local size 64 or 128 after profiling; start with 64 for broad compatibility.

Barriers:
- transfer write to compute shader read for skin matrices and dispatch buffer.
- compute shader write to task/mesh/vertex/fragment shader storage read for skinned output vertices.
- do not use CPU waits.

Debug names:
- `Skinning.MatrixBuffer.Frame0/1`
- `Skinning.DispatchBuffer.Frame0/1`
- `Skinning.OutputVertexBuffer.Frame0/1`
- `SkinningPass`
- `skinning.comp`

## Shader Fetch Integration

Update shared shader include(s), likely `Njulf.Shaders/common.glsl`:
- add bindless constants for skinning buffers.
- add helper `FetchVertex(meshMetadata, localVertexIndex, objectIndex)` or equivalent.

Update all mesh shaders that fetch vertices:
- `depth.mesh`
- `depth_alpha.mesh`
- `shadow_depth.mesh`
- `shadow_depth_alpha.mesh`
- `forward.mesh`

Rule:
- Static meshes fetch from `VERTEX_BUFFER_INDEX`.
- Skinned meshes fetch from the current frame skinned vertex buffer at `SkinnedVertexOffset + localVertexIndex`.

Every pass must see the same skinned output:
- directional shadows
- spot/point shadows
- depth prepass
- opaque forward
- transparent forward

## Diagnostics

Populate existing fields:
- `AnimationEnabled`
- `AnimationSkinningMode`
- `AnimationDebugView`
- `AnimatedModelCount`
- `SkinnedObjectCount`
- `SkeletonCount`
- `SkinCount`
- `AnimationClipCount`
- `ActiveAnimatorCount`
- `PlayingAnimatorCount`
- `PausedAnimatorCount`
- `SkinnedVertexCount`
- `SkinningDispatchCount`
- `JointMatrixCount`
- `MaxJointsPerSkeleton`
- `CpuAnimationSampleMicroseconds`
- `CpuSkinMatrixUploadMicroseconds`
- `CpuSkinningRecordMicroseconds`
- `GpuSkinningMicroseconds`
- `SkinningUploadBytes`
- `SkinMatrixBufferSize`
- `SkinnedVertexBufferSize`
- `AnimatedBoundsMode`

Update `SampleDiagnosticsReporter` only if needed; it already prints a compact animation line.

## Tests

### CPU/Contract Tests

- `GPUVertexSkinningData` layout size and field offsets.
- `GPUSkinningDispatch` layout size and field offsets.
- bindless index constants match `common.glsl`.
- static mesh registration does not allocate skinning data.
- skinned mesh registration uploads joint/weight streams and validates counts.
- `ModelRenderUploadService` creates `SkinnedRenderObject` for skinned submeshes.
- two instances of the same skinned asset allocate independent skinning output ranges.

### Shader Build Tests

- `skinning.comp` compiles.
- all modified mesh/task shaders compile.

### Scene Tests

- skinned object uses animated/conservative bounds for object culling.
- skinned meshlet bounds are conservatively expanded.
- static and skinned objects coexist in scene payload.
- diagnostics default to zero with no animated content.
- diagnostics count skinned object, dispatch, joint matrices, and skinned vertices with animated content.

### GPU Validation Tests/Manual Checks

- minimal one-joint triangle matches CPU skinning output.
- two-joint bend matches CPU reference within tolerance.
- two instances of the same asset at different times produce different output vertices.
- shadows and depth pass use the same pose as forward.
- toggling animation enabled/skinning mode does not crash or leak resources.
- resize does not invalidate skinning buffers incorrectly.
- Vulkan validation is clean through startup, playback, resize, and shutdown.

## Manual Sample Strategy

Do not repurpose `SampleReflectionTestSpheres.cs` for animation. It is a good pattern for deterministic sample helper construction, but reflection test spheres are static by design.

Add a separate sample helper later:
- `SampleAnimatedSkinningScene.cs`
- create or load a tiny skinned glTF asset.
- place one or two animated instances near the existing sample scene.
- expose clip play/pause/speed controls in `SampleInputController` after GPU output is visible.

## Implementation Order

1. Add GPU structs and layout tests.
2. Add bindless indices and shader constant tests.
3. Extend `MeshManager.MeshRegistrationData` with optional `GPUVertexSkinningData`.
4. Update `ModelRenderUploadService` to pass submesh skinning data into mesh registration.
5. Add skinning input buffer upload in `MeshManager`.
6. Add `SkinningManager` allocation, matrix upload, dispatch buffer upload, and diagnostics.
7. Add `skinning.comp` and `SkinningPass`.
8. Register new skinning buffers with the bindless heap.
9. Insert `SkinningPass` before shadows/depth/forward and add barriers.
10. Update mesh shader vertex fetch to choose static or skinned vertex data.
11. Add conservative skinned object and meshlet bounds.
12. Wire diagnostics from `SkinningManager` into `SceneRenderingData` and `RendererDiagnostics`.
13. Add focused tests for layout, bindless constants, upload contracts, diagnostics, and shader builds.
14. Add a minimal animated sample scene and sample input controls.
15. Run full tests, shader build, Vulkan validation, and RenderDoc inspection.

## Acceptance Criteria

- Static mesh import/render behavior remains unchanged.
- Static meshes do not pay larger vertex stride or skinning data memory.
- Skinned meshes upload joint/weight streams and render through compute-skinned output vertices.
- GPU skinning runs before shadows, depth, opaque forward, and transparent forward.
- CPU skinning and GPU skinning match on deterministic fixtures within tolerance.
- Two instances of one animated asset can play at different times.
- Conservative animated bounds prevent culling/shadow popping for the sample.
- Diagnostics report skinning counts, bytes, buffer sizes, and timings.
- `skinning.comp` and all modified shaders compile.
- `dotnet test Njulf.sln --no-restore` passes.
- Vulkan validation is clean during startup, playback, pause/seek, resize, and shutdown.
- RenderDoc clearly shows skin matrix upload, `SkinningPass`, and later passes reading skinned vertices.

## Risks And Mitigations

- Matrix order mismatch: compare GPU output against `CpuSkinning` fixtures before visual validation.
- Accidental static mesh regression: keep `GPUVertex` unchanged and cover static upload with tests.
- Meshlet culling too tight: expand skinned meshlet bounds in the first slice.
- Buffer lifetime bugs: reuse existing `BufferManager`, `FenceBasedDeleter`, and frame-in-flight patterns.
- Bindless drift: update C# constants, GLSL constants, and tests in one change.
- Per-frame allocation churn: preallocate arrays in `SkinningManager` and grow geometrically.
- Hidden pass inconsistency: update every shader path in one PR and verify shadows/depth/forward together.
