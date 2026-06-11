# Renderer Diagnostics Implementation Plan

Target outcome: make `RendererDiagnostics` explain renderer performance without guesswork. The diagnostics should separate CPU cost, GPU pass cost, GPU culling effectiveness, meshlet quality, and upload churn.

Primary questions this plan must answer:
- Is the bottleneck CPU scene preparation, GPU raster/shading, synchronization, or uploads?
- Are depth prepass and Hi-Z occlusion saving enough forward work to justify their cost?
- Are meshlets too small or too numerous for the current task-per-meshlet path?
- Which buffers are being uploaded, how often, and how many bytes each costs?
- Are diagnostics real measurements rather than placeholder values?

Non-goals:
- Do not optimize renderer behavior in this plan.
- Do not change meshlet generation except to expose quality metrics.
- Do not make shader-side diagnostic atomics mandatory in release/perf-off mode.
- Do not break existing `RendererDiagnostics` consumers without a deliberate constructor update and tests.

## Current State

Relevant files:
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/DepthPrePass.cs`
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf.Rendering/Pipeline/TiledLightCullingPass.cs`
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Shaders/depth.task`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/common.glsl`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`
- `Njulf.Tests/RendererDiagnosticsTests.cs`

Current gaps:
- `CpuSceneBuildMicroseconds` is one coarse bucket.
- GPU timing fields exist but are not backed by Vulkan timestamp queries.
- GPU culling fields are placeholders in `VulkanRenderer.BuildDiagnostics`.
- Upload bytes are aggregated, not broken down by buffer/resource.
- Meshlet quality is not visible, so high submitted meshlet counts cannot be explained.

## Data Model

Extend `RendererDiagnostics` with these groups. Prefer appending fields to reduce churn in call sites.

CPU phase timings:
- `long CpuPayloadSignatureMicroseconds`
- `long CpuObjectCullMicroseconds`
- `long CpuMeshletCullMicroseconds`
- `long CpuUploadMicroseconds`
- `long CpuMaterialUploadMicroseconds`
- `long CpuTotalDrawSceneMicroseconds`

GPU pass timings:
- keep `GpuDepthPrePassMicroseconds`
- keep `GpuHiZBuildMicroseconds`
- add `long GpuLightCullMicroseconds`
- keep `GpuForwardOpaqueMicroseconds`
- keep `GpuTransparentMicroseconds`

GPU culling counters:
- `int DepthTaskInvocations`
- `int DepthFrustumCulledMeshletsGpu`
- `int DepthEmittedMeshletsGpu`
- `int ForwardTaskInvocations`
- keep or map `FrustumCulledMeshletsGpu`
- `int ForwardOcclusionTestedMeshletsGpu`
- keep or map `OcclusionCulledMeshlets`
- keep or map `ForwardMeshletVisibleAfterOcclusion`

Meshlet quality:
- `int MeshletCountTotal`
- `int MeshletCountSubmittedCpu`
- `float AvgTrianglesPerSubmittedMeshlet`
- `float AvgVerticesPerSubmittedMeshlet`
- `int SmallMeshletsUnder16Triangles`
- `int SmallMeshletsUnder32Triangles`
- optional later: `int MaxTrianglesPerSubmittedMeshlet`, `int MaxVerticesPerSubmittedMeshlet`

Upload breakdown:
- `int ScenePayloadRebuilt`
- `ulong ObjectUploadBytes`
- `ulong InstanceUploadBytes`
- `ulong MeshletDrawUploadBytes`
- `ulong TransparentMeshletDrawUploadBytes`
- `ulong MaterialUploadBytes`
- `ulong LightUploadBytes`

Feature state:
- `int DepthPrePassEnabled`
- `int HiZEnabled`
- `int OcclusionEnabled`
- `uint HiZMipCount`
- `uint HiZWidth`
- `uint HiZHeight`

Validation:
- `RendererDiagnostics.Empty` zero-initializes every new field.
- `SceneRenderingData.Clear()` resets every new mutable diagnostic field.
- `SampleDiagnosticsReporter` prints compact groups, not one unreadable mega-line if output grows too large.

## Phase 1: CPU Phase Timers

Goal: split `sceneBuildUs` into actionable CPU buckets.

Implementation steps:
1. Add timer fields to `SceneRenderingData`:
   - `CpuPayloadSignatureMicroseconds`
   - `CpuObjectCullMicroseconds`
   - `CpuMeshletCullMicroseconds`
   - `CpuUploadMicroseconds`
   - `CpuMaterialUploadMicroseconds`
2. In `SceneDataBuilder.Build`, instrument:
   - payload signature creation
   - object loop/object visibility classification
   - meshlet loop/meshlet frustum culling
   - material upload call
   - scene buffer upload calls
3. Keep the existing `CpuSceneBuildMicroseconds` as total builder time.
4. In `VulkanRenderer.DrawScene`, measure `CpuTotalDrawSceneMicroseconds` from before light upload through diagnostics construction.
5. Map the values into `RendererDiagnostics`.
6. Update `RendererDiagnosticsTests`.

Implementation notes:
- Use `Stopwatch.GetTimestamp()` and `Stopwatch.GetElapsedTime(start)`.
- Avoid per-object `Stopwatch` allocations or heavy timer calls inside tight loops if possible.
- Use coarse timers around phases first. Add finer timers only if needed.

Validation gate:
- Tests pass.
- Stable frames show low upload/build CPU.
- Moving frames identify whether time is object culling, meshlet culling, upload, or material upload.

## Phase 2: Vulkan Timestamp Queries

Goal: get real GPU duration for each render pass.

Implementation steps:
1. Add a small `GpuTimestampQueryManager` under `Njulf.Rendering/Pipeline` or `Njulf.Rendering/Core`.
2. Allocate one query pool per frame in flight.
3. Reserve timestamp pairs for:
   - depth prepass
   - Hi-Z build
   - tiled light culling
   - forward opaque
   - transparent
4. At frame start:
   - reset the active frame query pool.
5. Around each pass execution:
   - write timestamp before pass
   - write timestamp after pass
6. At a safe later frame:
   - read previous completed frame query results after its fence has completed.
   - convert ticks to microseconds using `PhysicalDeviceProperties.Limits.TimestampPeriod`.
7. Store the latest available completed timings in `VulkanRenderer`.
8. Map timings into `RendererDiagnostics`.

Implementation notes:
- Never read query results from the frame currently being recorded.
- Prefer one-frame-late or two-frame-late timings over stalling.
- If timestamp queries are unavailable or invalid, keep values at zero and expose a boolean later if needed.

Validation gate:
- Vulkan validation reports no query pool misuse.
- GPU times become non-zero after the first completed frame.
- Disabling a pass makes its GPU time zero or near-zero.

## Phase 3: GPU Culling Counter Buffer

Goal: replace placeholder culling fields with shader-written counters.

Implementation steps:
1. Add a `GpuDiagnosticsCounterBuffer` resource:
   - one storage buffer per frame in flight or one ringed buffer with per-frame offsets.
   - host-visible readback path or staging copy after frame completion.
2. Define a C# struct and matching GLSL layout:
   - `DepthTaskInvocations`
   - `DepthFrustumCulled`
   - `DepthEmitted`
   - `ForwardTaskInvocations`
   - `ForwardFrustumCulled`
   - `ForwardOcclusionTested`
   - `ForwardOcclusionCulled`
   - `ForwardEmitted`
3. Add a bindless storage buffer index for diagnostics counters, or add a small dedicated descriptor if bindless index pressure/contract is a concern.
4. Add `DiagnosticsEnabled` and `DiagnosticsCounterBufferIndex` to relevant push constants if needed.
5. In `depth.task`:
   - increment task invocations.
   - increment frustum culled when sphere cull rejects.
   - increment emitted when mesh task is emitted.
6. In `forward.task`:
   - increment task invocations.
   - increment frustum culled.
   - increment occlusion tested before Hi-Z test when enabled.
   - increment occlusion culled when Hi-Z rejects.
   - increment emitted when mesh task is emitted.
7. Clear the counter buffer at frame start with `CmdFillBuffer`.
8. Read back completed-frame counters and map them to `RendererDiagnostics`.

Implementation notes:
- Guard atomics behind a runtime diagnostics flag so normal performance runs can disable them.
- Use 32-bit unsigned counters unless counts can exceed `uint.MaxValue`.
- Update shader layout tests if constants are added to `common.glsl`.

Validation gate:
- `ForwardTaskInvocations` equals CPU-submitted opaque meshlets when diagnostics are enabled.
- `ForwardEmitted + ForwardFrustumCulled + ForwardOcclusionCulled` is less than or equal to `ForwardTaskInvocations`.
- Current `occlusionCulled=0` becomes a real measured value.

## Phase 4: Meshlet Quality Stats

Goal: explain whether bad meshlet construction is causing high task counts.

Implementation steps:
1. Add a lightweight `MeshletQualityStats` struct in `MeshManager` or `SceneDataBuilder`.
2. Track global meshlet stats in `MeshManager` when meshes are registered:
   - total meshlets
   - total local triangles
   - total local vertices
   - count under 16 triangles
   - count under 32 triangles
3. Track submitted meshlet stats in `SceneDataBuilder` while adding visible commands:
   - submitted meshlet count
   - submitted local triangle sum
   - submitted local vertex sum
   - submitted under-16 and under-32 counts
4. Add fields to `SceneRenderingData`.
5. Map to `RendererDiagnostics`.
6. Print these in diagnostics.

Implementation notes:
- Avoid calling `MeshManager.GetMeshlet` twice for the same meshlet when CPU meshlet culling already fetched it.
- If CPU meshlet culling is skipped for fully-inside objects, stats still require meshlet data. Measure the overhead; if too expensive, compute per-mesh aggregate quality and add the aggregate when all meshlets for a mesh are submitted.
- Prefer per-mesh cached aggregates:
   - `MeshInfo.MeshletTriangleSum`
   - `MeshInfo.MeshletVertexSum`
   - `MeshInfo.SmallMeshletUnder16Count`
   - `MeshInfo.SmallMeshletUnder32Count`

Validation gate:
- Average submitted triangles and vertices are plausible.
- If Sponza has many tiny meshlets, diagnostics make that visible immediately.

## Phase 5: Upload Byte Breakdown

Goal: identify exactly which data moves each frame.

Implementation steps:
1. Add upload byte fields to `SceneRenderingData`:
   - object
   - instance
   - meshlet draw
   - transparent meshlet draw
   - material
   - light
2. Change `SceneDataBuilder.UploadSpanIfNeeded` to accept an upload category enum or return uploaded bytes.
3. Accumulate per-category bytes only when an upload happens.
4. Update `MaterialManager.UploadMaterials` to expose last upload bytes and CPU microseconds.
5. Update `LightManager.UploadToGPU` to expose last upload bytes.
6. Aggregate totals into existing `UploadedBytes`.
7. Map breakdown into `RendererDiagnostics`.
8. Print upload breakdown compactly.

Implementation notes:
- Keep `UploadedBytes` as the sum for backward compatibility.
- Count skipped uploads separately per category later if needed, but start with existing aggregate `SceneUploadSkipped`.
- Ensure buffer growth/reallocation paths force the relevant category upload state invalid.

Validation gate:
- `UploadedBytes == ObjectUploadBytes + InstanceUploadBytes + MeshletDrawUploadBytes + TransparentMeshletDrawUploadBytes + MaterialUploadBytes + LightUploadBytes`, or document any excluded staging/control bytes.
- Static camera frames show zero or near-zero scene upload bytes after warmup.

## Reporter Output Plan

Update `SampleDiagnosticsReporter` to emit grouped fields:

Line 1: scene counts
- visible objects, visible meshlets, opaque/transparent meshlets, materials, textures

Line 2: CPU timings
- total draw scene, scene build, signature, object cull, meshlet cull, material upload, upload

Line 3: GPU timings
- depth, Hi-Z, light cull, forward, transparent

Line 4: culling
- CPU object/meshlet candidates and culled
- GPU task invocations, frustum culled, occlusion tested, occlusion culled, emitted

Line 5: meshlet quality and uploads
- average triangles/vertices, under-16, under-32
- upload byte breakdown

Keep the existing first-frame plus periodic output cadence.

## Test Plan

Unit tests:
- `RendererDiagnostics.Empty` includes zero/default values for all new fields.
- `SceneRenderingData.Clear` resets all new diagnostics.
- GPU struct layout tests pass after shader push constant/common layout updates.
- Any new C# diagnostics structs match GLSL constants if mirrored in `common.glsl`.

Integration/manual tests:
- Run `NjulfHelloGame` with diagnostics enabled.
- Capture full-model camera and close-interior camera.
- Toggle depth prepass and Hi-Z occlusion.
- Confirm counters and timings move in expected directions.

Performance sanity:
- Diagnostics disabled should avoid shader atomics and readbacks.
- Timestamp queries can remain enabled if they do not stall.
- Counter readback must be one or more completed frames late.

## Suggested Implementation Order

1. CPU phase timers.
2. Upload byte breakdown.
3. Meshlet quality stats.
4. Vulkan timestamp queries.
5. GPU culling counter buffer.

Reasoning:
- Steps 1-3 are mostly CPU-side and low risk.
- Step 4 adds Vulkan synchronization complexity but no shader atomics.
- Step 5 touches descriptors, shaders, buffer clearing, and readback, so it should land after the basic diagnostics path is stable.

## Completion Criteria

The work is complete when:
- `RendererDiagnostics` no longer contains placeholder GPU culling/timing values when diagnostics are enabled.
- A single diagnostics dump can distinguish CPU build, GPU pass, meshlet quality, and upload bottlenecks.
- Existing automated tests pass.
- Manual sample runs produce stable numbers for at least two camera positions.
