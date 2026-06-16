# Nsight Visibility Renderer Optimization Plan - 2026-06-16

Source: fresh Nsight Graphics capture `NjulfHelloGame_2026_06_16_01_46_07.ngfx-gputrace` screenshot.

## Capture Summary

- Matched frame: about 75.45 ms.
- Active Vulkan GPU time: about 49.69 ms.
- Dominant passes:
  - `GpuVisibilityPass`: about 21.57 ms.
  - `DepthPrePass`: about 7.28 ms.
- Throughput counters remain low:
  - SM throughput: about 17.1%.
  - VRAM throughput: about 8.4%.
  - L2 throughput: about 7.8%.
  - CS warp occupancy: about 1.2%.
  - Unallocated warp occupancy: about 48.3%.

The renderer is still not bandwidth-bound. The main issue is low-occupancy, serialized GPU work in visibility generation, followed by a depth prepass whose ROI is not yet proven for this scene.

## Current Bottleneck

`gpu_visibility.comp` still performs too much work in one object-based dispatch:

- one invocation per object,
- object frustum test,
- LOD selection,
- serial meshlet expansion inside the object invocation,
- opaque/depth/transparent list writes,
- directional shadow list generation,
- spot shadow list generation,
- point shadow cube-face list generation.

This is poorly balanced. Large objects or shadow-heavy objects can keep a single invocation busy while other lanes finish early. The pass also combines unrelated costs, making shadows, camera visibility, and list finalization hard to optimize independently.

## Goal

Build a faster renderer path without preserving slow legacy behavior. The new path should prioritize GPU occupancy, deterministic list generation, independently measurable passes, and aggressive defaults. Legacy compatibility can be removed when it conflicts with performance.

Targets for this scene:

- visibility pass family below 5 ms,
- skip `DepthPrePass` and `HiZBuildPass` unless they prove positive ROI,
- eliminate normal-path duplicate forward culling,
- split shadow list costs into separately timed passes,
- avoid global append atomics for primary draw-list generation,
- keep indirect draw counts clamped to buffer capacity.

## Phase 1 - Replace Monolithic Visibility With Staged Visibility

Create a staged visibility pipeline:

1. `GpuObjectVisibilityPass`
   - One thread per object.
   - Performs object visibility, material classification, and LOD selection.
   - Outputs compact visible object records:
     - object index,
     - instance index,
     - material index,
     - selected meshlet offset,
     - selected meshlet count,
     - LOD,
     - render class: solid, masked, transparent,
     - coarse directional/spot/point shadow masks.

2. `GpuVisibilityCountPass`
   - Consumes visible object records.
   - Produces per-record counts for:
     - opaque forward meshlets,
     - solid depth meshlets,
     - masked depth meshlets,
     - transparent meshlets.

3. `GpuVisibilityPrefixPass`
   - Prefix sums the per-record counts.
   - Produces deterministic output offsets.
   - Computes final exact list sizes.

4. `GpuMeshletExpandPass`
   - Expands visible object ranges into final `GPUMeshletDrawCommand` lists.
   - Uses direct writes into prefix-summed offsets.
   - Does not use global append atomics for primary camera/depth/transparent lists.

5. `GpuVisibilityFinalizePass`
   - Writes indirect commands from final counts.
   - Clamps every indirect group count to output capacity.
   - Sets overflow flags when required counts exceed capacity.

Likely files:

- `Njulf.Rendering/Pipeline/GpuVisibilityPass.cs`
- `Njulf.Rendering/GpuScene/GpuVisibilityBufferSet.cs`
- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Shaders/gpu_visibility.comp`
- new shaders:
  - `gpu_object_visibility.comp`
  - `gpu_visibility_count.comp`
  - `gpu_visibility_prefix.comp`
  - `gpu_meshlet_expand.comp`
  - `gpu_visibility_finalize.comp`

## Phase 2 - Split Shadow List Generation Out Of Camera Visibility

Remove directional, spot, and point shadow list generation from the camera visibility shader.

Add separate passes:

1. `GpuDirectionalShadowListPass`
   - Consumes visible object records.
   - Uses directional cascade masks from object visibility.
   - Expands only objects that intersect each cascade.
   - Writes per-cascade indirect commands.

2. `GpuSpotShadowListPass`
   - Consumes visible object records.
   - Uses selected spot shadow count and coarse object/light masks.
   - Uses count/prefix/expand instead of global append atomics.
   - Has independent timing and overflow counters.

3. `GpuPointShadowListPass`
   - Consumes visible object records.
   - Tests point light range first.
   - Tests cube faces only for range-passing objects.
   - Generates per-face counts and indirect commands.
   - Supports face masks so disabled faces do no work.

This makes local shadow cost independently measurable and tunable.

## Phase 3 - Make Depth Prepass And Hi-Z Truly Adaptive

The fresh capture pays about 7.28 ms for `DepthPrePass`. The previous optimization means normal forward rendering no longer depends on task-shader Hi-Z, so this cost should be skipped unless it is clearly useful.

Policy:

- Do not run `DepthPrePass` or `HiZBuildPass` by default just for forward occlusion.
- Run depth only when required by enabled features:
  - ambient occlusion,
  - fog/depth effects,
  - soft particles,
  - debug validation,
  - explicit user/debug request.
- Add rolling ROI tracking:
  - depth prepass GPU cost,
  - Hi-Z build GPU cost,
  - forward opaque GPU cost,
  - visible opaque meshlets,
  - validation-mode occlusion rejected meshlets.
- Run occasional probe frames when adaptive Hi-Z is enabled.
- Suppress depth/Hi-Z if estimated savings are lower than `DepthPrePass + HiZBuildPass`.

Implementation files:

- `Njulf.Rendering/Pipeline/AdaptiveHiZPolicy.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/FramePassRuntimePolicy.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- tests in `Njulf.Tests/AdaptiveHiZPolicyTests.cs`

## Phase 4 - Remove Forward Validation From The Normal Pipeline

The normal forward task shader should become minimal:

- read compact draw command,
- emit one mesh task,
- no frustum test,
- no Hi-Z test,
- no diagnostic atomics.

Move validation into a separate debug pipeline variant:

- `forward_validate.task`
- selected only when debug validation is enabled.

This avoids carrying branch-heavy diagnostic logic in the production path.

Implementation files:

- `Njulf.Shaders/forward.task`
- new `Njulf.Shaders/forward_validate.task`
- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs`
- `Njulf.Rendering/Pipeline/PipelineObjects/GraphicsPipelineFactory.cs`
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`

## Phase 5 - Replace Remaining Global Append Atomics

Use count/prefix/expand for every high-volume list:

- opaque forward,
- solid depth,
- masked depth,
- transparent,
- directional shadow cascades,
- spot shadow lists,
- point shadow faces.

Global atomics should be limited to:

- debug counters,
- overflow flags,
- small pass-level aggregate counters.

Required capacity should be final exact counts, not per-meshlet atomic increments.

## Phase 6 - Aggressive Runtime Budgets

Because legacy behavior is not a constraint, reduce work before it reaches GPU list generation.

Default policy:

- lower `MaxShadowedSpotLights`,
- lower `MaxShadowedPointLights`,
- disable point shadows below intensity/range thresholds,
- reduce point shadow faces when influence is low,
- cap transparent meshlets independently,
- prefer directional shadows over local shadows under load,
- disable local shadows for tiny or low-intensity lights.

Implementation files:

- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Data/LocalShadowSelector.cs`
- `Njulf.Rendering/Data/LocalShadowDataBuilder.cs`
- `NjulfHelloGame/SampleLighting.cs`
- `NjulfHelloGame/SampleInputController.cs`

## Phase 7 - Stabilize Nsight Marker Hierarchy

Add explicit labels around the new stages:

- object visibility,
- visibility count,
- prefix/scan,
- camera meshlet expansion,
- directional shadow list generation,
- spot shadow list generation,
- point shadow list generation,
- visibility finalize,
- forward opaque,
- depth prepass,
- Hi-Z build.

Every label must have a guaranteed matching end label. The goal is to remove Nsight mismatched marker warnings and make regressions easy to compare.

Implementation files:

- `Njulf.Rendering/Pipeline/GpuVisibilityPass.cs`
- split shadow list pass files,
- `Njulf.Rendering/Core/VulkanContext.cs` if helper improvements are needed.

## Phase 8 - Validation Captures

After implementation, capture these scenarios from the same camera:

1. baseline new renderer,
2. depth/Hi-Z off,
3. depth/Hi-Z probe frame,
4. all shadows off,
5. point shadows off only,
6. spot shadows off only,
7. directional shadows only,
8. forward validation pipeline on,
9. forward validation pipeline off.

Record:

- total frame GPU time,
- active Vulkan GPU time,
- object visibility time,
- meshlet expansion time,
- directional shadow list time,
- spot shadow list time,
- point shadow list time,
- depth prepass time,
- Hi-Z build time,
- forward opaque time,
- opaque meshlet count,
- depth meshlet count,
- transparent meshlet count,
- directional shadow meshlet count,
- spot shadow meshlet count,
- point shadow face meshlet counts.

## Execution Order

1. Add visible object record buffers and staged visibility shader structs.
2. Implement object visibility pass.
3. Implement count/prefix/expand for camera/depth/transparent lists.
4. Delete camera meshlet expansion from the old monolithic shader.
5. Split directional shadow list generation.
6. Split spot and point shadow list generation.
7. Switch normal forward to minimal task shader.
8. Move validation to separate forward validation pipeline.
9. Tighten adaptive depth/Hi-Z pass policy.
10. Add aggressive local shadow budgets.
11. Fix Nsight marker hierarchy.
12. Run tests and capture validation scenarios.

## Immediate Next Coding Step

Start with Phase 1:

- add `GPUVisibleObjectRecord`,
- extend `GpuVisibilityBufferSet` with visible-object and prefix/count buffers,
- create `gpu_object_visibility.comp`,
- update `GpuVisibilityPass` to dispatch object visibility before the existing path,
- then replace camera meshlet list generation with count/prefix/expand.

