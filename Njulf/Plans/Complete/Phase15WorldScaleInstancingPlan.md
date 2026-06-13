# Phase 15 World Scale, Instancing, And LOD Hardening Plan

Goal: make large static and semi-static scenes affordable without redoing work the renderer already has. Phase 15 should focus on measurable CPU submission, upload, and draw-list efficiency for repeated content. Do not add new LOD systems, impostors, foliage renderers, or GPU-driven compaction unless profiling proves the existing meshlet LOD and Hi-Z culling path cannot meet the target.

## Current Baseline

The main production plan describes Phase 15 as "Add LOD, Instancing, And World Scale Systems". The implementation has already moved past part of that baseline:

1. `MeshManager` already generates multiple meshlet ranges per mesh:
   - LOD0: smaller meshlets for near geometry.
   - LOD1: medium meshlets.
   - LOD2: larger meshlets.
2. `SceneDataBuilder` already selects a meshlet LOD range from camera distance and object radius.
3. `SceneDataBuilder` already reports:
   - `MeshletLodSkippedCpu`
   - `MeshletLod0SubmittedCpu`
   - `MeshletLod1SubmittedCpu`
   - `MeshletLod2SubmittedCpu`
4. Hi-Z occlusion culling already exists:
   - `HiZBuildPass`
   - `HiZDepthPyramid`
   - task-shader occlusion checks in `forward.task`
   - adaptive suppression when measured cull rate is too low.
5. Mesh GPU resources are already shared through `MeshHandle`.
6. Repeated render objects still appear to upload one object transform and emit per-object, per-meshlet draw commands. That is the useful remaining Phase 15 gap.

Important distinction: the current "meshlet LOD" is a submission and culling granularity optimization. It does not appear to be geometric simplification because all LOD ranges are generated from the same source vertices and indices with different meshlet sizes. That means visual LOD popping and geometric LOD hysteresis are not currently a problem to solve.

## Non-Goals

Do not implement these in the default Phase 15 slice:

1. Do not add a second mesh LOD import path unless there is a real authored LOD asset format requirement.
2. Do not add mesh simplification or generated geometric LODs. The renderer already has meshlet LOD for the current need.
3. Do not add LOD hysteresis for current meshlet LOD. Since geometry is unchanged, hysteresis adds state without solving visible popping.
4. Do not add impostors by default. They are only justified for distant complex objects after a stress scene shows geometry cost remains too high after instancing and Hi-Z.
5. Do not add a special foliage renderer by default. Add it only when there is real foliage content with alpha, wind, density, and sorting requirements.
6. Do not add GPU-driven compaction by default. Add it only if CPU draw-list building or meshlet draw buffer upload remains a measured bottleneck after instancing.
7. Do not change reflection probe, particle, transparency, or animation behavior except where repeated-object submission must preserve their existing render lists.

## Target Outcome

After Phase 15, the renderer should handle thousands of repeated props such as crates, stones, pillars, modular walls, lamps, trees, and debris without duplicating mesh resources or rebuilding excessive per-object command streams.

The practical target is:

1. Many repeated objects share one mesh and material payload.
2. Static repeated objects can be grouped into stable batches.
3. Per-frame upload work is proportional to changed instances, not total scene size.
4. Draw command generation avoids avoidable per-object recomputation for unchanged static batches.
5. Existing meshlet LOD, CPU frustum culling, GPU task-shader frustum culling, Hi-Z occlusion, shadows, transparency sorting, and diagnostics continue to work.
6. The sample app has a repeatable stress scene that proves the cost reduction.

## Production Strategy

Implement Phase 15 in two mandatory slices and keep all other work behind metrics.

Mandatory:

1. Static repeated-object batching.
2. Diagnostics, tests, and a stress scene.

Conditional:

1. Per-batch cached meshlet command templates if initial batching still spends too much CPU on command building.
2. Dirty-range instance uploads if full instance buffer upload is too expensive.
3. Foliage-specific batching only when real foliage content exists.
4. Authored geometric LOD only when assets ship with LOD variants or a game scene proves triangle cost is the bottleneck.
5. GPU-driven compaction only after CPU batching has been measured and found insufficient.

## Slice 0: Baseline Measurement Before Changes

Before implementing anything, capture current numbers so Phase 15 can reject unnecessary work.

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Add or use a temporary local stress setup with repeated instances of the same mesh and material.
4. Record diagnostics with approximately:
   - 100 repeated objects.
   - 1,000 repeated objects.
   - 5,000 repeated objects if memory allows.
5. Record these diagnostics:
   - `CpuSceneBuildMicroseconds`
   - `CpuPayloadSignatureMicroseconds`
   - `CpuObjectCullMicroseconds`
   - `CpuMeshletCullMicroseconds`
   - `CpuUploadMicroseconds`
   - `ObjectUploadBytes`
   - `InstanceUploadBytes`
   - `MeshletDrawUploadBytes`
   - `VisibleObjectCount`
   - `VisibleMeshletCount`
   - `MeshletLod0SubmittedCpu`
   - `MeshletLod1SubmittedCpu`
   - `MeshletLod2SubmittedCpu`
   - `ForwardTaskInvocations`
   - `ForwardOcclusionTestedMeshletsGpu`
   - `ForwardEmittedMeshletsGpu`
6. Capture a RenderDoc frame and verify existing pass order remains:
   - depth prepass
   - Hi-Z build when enabled
   - light culling
   - opaque forward
   - transparent forward
   - post/composite passes.

Acceptance criteria:

1. Baseline numbers are written into the PR or implementation notes.
2. Any Phase 15 feature beyond batching has a measured reason.
3. Existing tests pass before changes.

## Slice 1: Add Static Instance Batch Authoring

Add a small scene-level abstraction for repeated static content. This should be an authoring and CPU-build optimization, not a new rendering path at first.

Recommended API:

```csharp
public sealed class StaticInstanceBatch
{
    public string Name { get; set; } = "StaticInstanceBatch";
    public object? Mesh { get; set; }
    public object? Material { get; set; }
    public bool Visible { get; set; } = true;
    public IReadOnlyList<Matrix4x4> WorldMatrices { get; }
}
```

Implementation notes:

1. Prefer adding a separate collection to `Scene`, for example `Scene.StaticInstanceBatches`, instead of overloading `RenderObject`.
2. Keep `RenderObject` behavior unchanged for normal authored objects, animated objects, transparent one-offs, and existing samples.
3. A batch must have one mesh handle and one material handle.
4. Each instance still gets its own `GPUObjectData` initially. This preserves the current shader contract, `GPUMeshletDrawCommand.InstanceId`, material lookup, and transform handling.
5. Do not introduce a new GPU instance struct in the first slice unless it clearly removes complexity. `GPUObjectData` is already the shader-readable instance payload.
6. Make batch instances immutable or versioned for static content. The scene builder must be able to know when the batch changed.
7. Validate input early:
   - null mesh falls back or rejects the batch consistently with `RenderObject`.
   - null material uses the default material, matching `RenderObject`.
   - non-invertible matrices throw a named error with the batch name and instance index.

Files likely touched:

1. `Njulf.Core/Scene/Scene.cs`
2. new `Njulf.Core/Scene/StaticInstanceBatch.cs`
3. `Njulf.Rendering/Data/SceneDataBuilder.cs`
4. tests in `Njulf.Tests/SceneTests.cs` or a new dedicated test file.

Acceptance criteria:

1. Existing `RenderObject` scenes render the same.
2. A scene can add one batch with many transforms and render it.
3. Batch objects share mesh and material handles.
4. Batch visibility disables all instances without removing the batch.
5. Diagnostics distinguish normal render objects from batched instances.

## Slice 2: Build Batched Payloads Efficiently

Update `SceneDataBuilder` so static batches avoid repeated high-level object work.

The first implementation may still emit one `GPUMeshletDrawCommand` per visible meshlet per instance, because that preserves the current task/mesh shader path. The improvement should come from grouping repeated state and avoiding repeated mesh/material resolution, range lookup, and cache churn.

Tasks:

1. Extend static scene signatures to include batches:
   - batch identity
   - batch visibility
   - mesh handle
   - material handle
   - transform count
   - transform/version hash.
2. Extend culling signatures to include batches and camera-dependent data.
3. Resolve mesh info once per batch.
4. Resolve material and render mode once per batch.
5. Compute local bounding data once per batch.
6. Loop transforms inside the batch and emit object data plus commands.
7. Preserve existing classification:
   - opaque
   - masked
   - transparent.
8. Preserve existing shadow rules:
   - blended materials do not cast current shadow meshlet lists.
   - opaque and masked batched instances can cast directional and local shadows.
9. Preserve existing transparent sorting:
   - transparent batched instances must sort deterministically with normal transparent meshlets.
   - tie-break by material, instance id, meshlet index, and batch instance order where needed.
10. Preserve existing meshlet LOD selection:
   - use the same `SelectMeshletLodLevel`.
   - do not add hysteresis for current meshlet LOD.
11. Add batch diagnostics:
   - static batch count
   - static batch instance count
   - visible static batch instance count
   - culled static batch instance count
   - batched meshlet draw command count
   - batch object upload bytes if separated, otherwise include in existing object/instance upload bytes and report the count.

Potential internal helper:

```csharp
private void BuildObjectPayload(
    string objectName,
    MeshHandle meshHandle,
    MaterialHandle materialHandle,
    Matrix4x4 worldMatrix,
    bool visible,
    Vector3 cameraPosition,
    Frustum frustum,
    ...)
```

Use a helper only if it removes duplication between `RenderObject` and `StaticInstanceBatch`. Do not create a complex abstraction just to avoid a few lines.

Acceptance criteria:

1. One 1,000-instance batch has lower `CpuPayloadSignatureMicroseconds` and `CpuObjectCullMicroseconds` than 1,000 separate `RenderObject`s with the same mesh/material.
2. Draw command counts stay correct and explainable.
3. Meshlet LOD diagnostics still update for batched instances.
4. Shadow meshlet counts include visible shadow-casting batch instances.
5. Transparent batched instances remain sorted deterministically.
6. No existing diagnostics regress to zero or stale values.

## Slice 3: Upload Stability And Dirty Data

Only implement dirty-range uploads if Slice 2 shows upload bytes or upload time are still a bottleneck. If the existing upload cache already skips unchanged content well enough, document that and stop.

Current behavior to verify:

1. Static object data is uploaded when the static payload changes.
2. Per-frame instance buffers are derived from `_objectData`.
3. Meshlet draw buffers are uploaded when culling payload changes.

Possible tasks if needed:

1. Add per-batch revision numbers so unchanged static batches do not invalidate unrelated batches.
2. Track changed instance ranges inside a static batch.
3. Support partial buffer copies for changed ranges only.
4. Keep full-buffer upload fallback for simplicity and resize cases.
5. Add diagnostics for:
   - full instance upload count
   - partial instance upload count
   - partial upload bytes
   - dirty static batch count.

Do not implement partial uploads if the baseline and Slice 2 measurements show uploads are not material to frame time.

Acceptance criteria if implemented:

1. Moving one instance in a large batch uploads a small range instead of the whole batch.
2. Buffer growth and frame-in-flight synchronization remain validation-clean.
3. Static unchanged scenes report skipped uploads as expected.

## Slice 4: Sample World Scale Stress Scene

Add a controlled sample scene to `NjulfHelloGame` that validates Phase 15 without relying on external content.

Recommended sample:

1. Use existing procedural meshes or already-loaded sample meshes.
2. Create repeated batches:
   - grid of crates or pillars.
   - scattered rocks or spheres.
   - some opaque.
   - a small number of masked objects if existing material support makes that easy.
3. Keep `SampleReflectionTestSpheres` in the scene as a visual material sanity check, but do not use it as the main stress workload.
4. Add input or startup option to toggle between:
   - separate `RenderObject` stress mode.
   - `StaticInstanceBatch` stress mode.
5. Print diagnostics that make the comparison obvious:
   - object count
   - static batch count
   - static batch instance count
   - CPU build time
   - upload bytes
   - submitted meshlets
   - LOD distribution
   - Hi-Z cull counts.

Acceptance criteria:

1. The sample can demonstrate repeated content without new assets.
2. The batched mode is measurably cheaper than separate-object mode at 1,000 repeated objects.
3. Hi-Z and meshlet LOD toggles still work.
4. Reflection test spheres still render correctly, proving the sample did not break material and reflection validation.

## Slice 5: Tests

Add focused CPU-side tests. Do not attempt GPU correctness tests unless the project already has a stable GPU smoke-test path.

Scene and batch tests:

1. `Scene_AddStaticInstanceBatch_StoresBatch`
2. `Scene_Clear_RemovesStaticInstanceBatches`
3. `StaticInstanceBatch_RejectsNullTransformCollection`
4. `StaticInstanceBatch_TracksRevisionWhenTransformsChange` if revisions are added.

Scene data builder tests:

1. Static scene signature changes when:
   - batch visibility changes
   - mesh changes
   - material changes
   - transform count changes
   - transform value changes.
2. Culling signature changes with camera movement.
3. Material resolution for batched instances matches `RenderObject`.
4. `SelectMeshletLodLevel` remains unchanged and covered by existing tests.

Diagnostics tests:

1. `RendererDiagnostics.Empty` initializes new batch counters to zero.
2. populated diagnostics copy new batch counters from `SceneRenderingData`.
3. sample diagnostics reporter includes batch counters without breaking existing output.

Shader layout tests:

1. No shader layout changes are expected in the mandatory slices.
2. If a new GPU batch or instance struct is added later, extend `GPUStructLayoutTests` and shader constants in the same change.

Acceptance criteria:

1. `dotnet test Njulf.sln` passes.
2. Tests fail before the relevant implementation when practical.
3. No test depends on GPU availability unless explicitly marked as a GPU smoke test.

## Slice 6: Conditional Shadow LOD Policy

The notes file mentions "Add a cheaper shadow LOD policy." This is potentially useful, but it should not be part of the mandatory batching slice.

Only implement if shadow diagnostics show shadow meshlet submission is expensive in large scenes.

Possible policy:

1. Keep camera rendering on existing meshlet LOD selection.
2. Use a coarser meshlet range for distant shadow casters.
3. Use directional cascade distance as the main decision input.
4. Never use coarser shadow LOD for nearby cascade 0 unless artifacts are acceptable.
5. Add settings:
   - `ShadowSettings.EnableMeshletLodPolicy`
   - `ShadowSettings.Cascade1MinLod`
   - `ShadowSettings.Cascade2MinLod`
   - `ShadowSettings.Cascade3MinLod`
6. Add diagnostics:
   - shadow LOD0 meshlets
   - shadow LOD1 meshlets
   - shadow LOD2 meshlets.

Acceptance criteria if implemented:

1. Shadow pass CPU/GPU cost drops in large scenes.
2. Cascade 0 visual quality is unchanged by default.
3. Shadow acne and peter-panning controls continue to work.
4. The setting can be disabled for comparison.

## Slice 7: Conditional Foliage Batching

Do not add this unless real foliage content is in scope.

Add a foliage path only if the renderer needs thousands of repeated alpha-masked plants with wind or per-instance variation. If needed, build it on top of `StaticInstanceBatch` rather than creating a separate renderer first.

Potential additions:

1. Per-instance color/variation data.
2. Wind parameters.
3. Masked-material depth-pass validation.
4. Optional density culling.
5. Optional cell-based bounds for faster culling.

Acceptance criteria if implemented:

1. Masked foliage writes depth correctly.
2. Alpha test works in depth, shadow, and forward passes.
3. Foliage batching reduces CPU submission cost versus separate objects.
4. No new transparent sorting behavior is introduced for masked foliage.

## Slice 8: Conditional Authored Geometric LOD

Do not add this unless assets provide true LOD variants or triangle cost remains the bottleneck after batching and occlusion.

If required later, implement authored LODs rather than automatic simplification first. Authored LODs are predictable, artist-controllable, and easier to validate.

Possible model:

```csharp
public sealed class MeshLodSet
{
    public MeshHandle[] Meshes { get; }
    public float[] SwitchScreenHeights { get; }
}
```

Rules:

1. LOD0 is always the highest quality mesh.
2. Switch thresholds should be screen-size based, not raw distance only.
3. Add hysteresis only for true geometric LOD, because geometry changes can pop.
4. Add diagnostics for selected geometric LODs.
5. Keep current meshlet LOD inside each selected geometric LOD if it remains useful.

Acceptance criteria if implemented:

1. True lower-triangle meshes reduce forward and shadow cost.
2. LOD transitions are stable for normal gameplay cameras.
3. Missing lower LODs fall back to the best available mesh.
4. Existing meshlet LOD remains valid and does not double-count diagnostics confusingly.

## Slice 9: Conditional GPU-Driven Compaction

Do not implement GPU-driven compaction in the default Phase 15 work. It is a larger architecture change and should be justified by metrics.

Consider it only if all are true:

1. Static batching is implemented.
2. CPU culling and draw-list generation are still a measurable bottleneck.
3. Meshlet draw buffer upload remains large for mostly static scenes.
4. Existing task-shader culling still launches too many task invocations.

Likely design if needed:

1. Keep CPU object/batch upload.
2. Upload compact per-object or per-batch metadata.
3. Run compute culling and prefix/append visible meshlet commands.
4. Dispatch mesh shaders indirectly from compacted counts.
5. Preserve CPU fallback for unsupported devices or debugging.
6. Add GPU counters and debug readback guarded by settings.

Acceptance criteria if implemented:

1. CPU scene build and upload time drop in high-object-count scenes.
2. GPU culling pass cost is lower than the CPU work it replaces.
3. RenderDoc remains inspectable with named buffers and passes.
4. Validation is clean across resize, scene reload, and shutdown.

## Diagnostics Additions

Add only the counters needed to evaluate batching:

1. `StaticInstanceBatchCount`
2. `StaticInstanceCount`
3. `VisibleStaticInstanceCount`
4. `CulledStaticInstanceCount`
5. `StaticBatchMeshletDrawCommandCount`
6. `CpuStaticBatchBuildMicroseconds`

Optional only if implemented:

1. `StaticBatchFullUploadCount`
2. `StaticBatchPartialUploadCount`
3. `StaticBatchPartialUploadBytes`
4. `DirtyStaticBatchCount`
5. `ShadowLod0SubmittedCpu`
6. `ShadowLod1SubmittedCpu`
7. `ShadowLod2SubmittedCpu`

Acceptance criteria:

1. `RendererDiagnostics.Empty` initializes every new counter.
2. `VulkanRenderer` copies every new counter from `SceneRenderingData`.
3. `SampleDiagnosticsReporter` prints the counters in a compact line.
4. Diagnostics make it clear whether batching helped.

## Data Model Constraints

Keep contracts conservative:

1. `RenderObject` remains the flexible per-object path.
2. `StaticInstanceBatch` is for repeated static mesh/material pairs.
3. Batched instances do not carry unique materials in the first version.
4. Batched instances do not animate in the first version.
5. Batched instances do not have per-instance custom shader data in the first version.
6. Batched instances can be removed or rebuilt as a whole.
7. If per-instance variation is needed later, add a separate small variation buffer rather than bloating `GPUObjectData` prematurely.

## Validation Matrix

Validate these scene types:

1. Empty scene.
2. Existing sample scene with no batches.
3. One batch with one instance.
4. One batch with 1,000 opaque instances.
5. Ten batches with 100 opaque instances each.
6. One transparent batch if blended materials are supported through the same material path.
7. One masked batch if alpha-mask material support is available.
8. Batched instances inside and outside the camera frustum.
9. Batched instances casting directional shadows.
10. Batched instances with Hi-Z enabled and disabled.
11. Batched stress scene with reflection test spheres still present.

## Performance Acceptance Criteria

Use the same machine and resolution for before/after comparisons.

Minimum expected result:

1. At 1,000 repeated opaque instances, batched mode is measurably cheaper than separate `RenderObject` mode in CPU scene build time.
2. Mesh and material GPU resource counts do not grow with instance count for repeated content.
3. Upload bytes do not increase beyond what the extra transforms and draw commands require.
4. Existing Hi-Z and meshlet LOD counters still behave normally.
5. Frame rendering remains validation-clean.

Stretch result:

1. At 5,000 repeated opaque instances, the sample remains responsive.
2. CPU build time scales primarily with visible or changed instances.
3. Static unchanged scenes skip uploads across frames.

## Implementation Order

1. Capture baseline diagnostics.
2. Add `StaticInstanceBatch` scene data model.
3. Extend `Scene` add/remove/clear behavior and tests.
4. Extend `SceneRenderingData` and `RendererDiagnostics` with batch counters.
5. Extend `SceneDataBuilder` signatures to account for batches.
6. Build batch payloads using the existing meshlet LOD and culling path.
7. Preserve transparent sorting and shadow command generation.
8. Add sample stress scene toggle in `NjulfHelloGame`.
9. Add diagnostics reporter output.
10. Run `dotnet test Njulf.sln`.
11. Run `dotnet build Njulf.sln`.
12. Run the sample with Vulkan validation layers.
13. Compare before/after diagnostics.
14. Decide whether any conditional slice is justified.

## Definition Of Done

Phase 15 is complete when:

1. Static repeated mesh/material content can be represented as batches.
2. Batched scenes render through the existing meshlet, material, shadow, transparency, and Hi-Z systems.
3. Existing meshlet LOD is preserved and not duplicated by an unnecessary new LOD system.
4. Diagnostics show static batch counts, visible/cull counts, and CPU build cost.
5. A sample stress scene demonstrates the improvement over separate render objects.
6. Tests cover the scene model, signatures, diagnostics defaults, and unchanged LOD selection behavior.
7. `dotnet test Njulf.sln` passes.
8. `dotnet build Njulf.sln` passes.
9. The sample runs validation-clean.
10. Any unimplemented Phase 15 items are explicitly documented as unnecessary or conditional based on measurements.

## Stop Conditions

Stop Phase 15 early and do not add more systems if:

1. Static batching gives the needed performance improvement.
2. Existing meshlet LOD and Hi-Z culling keep large scenes inside budget.
3. Upload bytes and CPU build time are no longer material bottlenecks.
4. There is no real content requiring foliage, impostors, authored LODs, or GPU-driven compaction.

This is a valid production outcome. Avoid filling Phase 15 with speculative renderer systems that are not required by the measured scene.
