# Renderer Ultra Nsight Performance Implementation Plan

Date: 2026-06-21

Scope: implementation plan for the eight renderer performance opportunities identified from the Ultra Nsight capture. This file is planning-only and does not change renderer behavior.

Primary capture signal:

- Frame duration: about 24.02 ms.
- Main GPU hotspot: `ForwardPlusPass` / mesh pipeline, about 11.8 ms.
- Low top-level throughput: SM about 24%, L2 about 13.5%, VRAM about 12.9%.
- Likely bottleneck class: over-submission, low occupancy, divergent shader work, and avoidable per-pixel work rather than raw bandwidth saturation.

Key files:

- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
- `Njulf.Rendering/Pipeline/SceneOpaqueCompactionPass.cs`
- `Njulf.Rendering/Pipeline/TiledLightCullingPass.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/forward.mesh`
- `Njulf.Shaders/forward.frag`
- `Njulf.Shaders/lightcull.comp`
- `NjulfHelloGame/SampleInputController.cs`

## Measurement Baseline

1. Add a reproducible Ultra benchmark run before changing performance code.
2. Capture these metrics for each run:
   - `GpuFrameMicroseconds`
   - `GpuForwardOpaqueMicroseconds`
   - `GpuDepthPrePassMicroseconds`
   - `GpuHiZBuildMicroseconds`
   - `GpuAmbientOcclusionMicroseconds`
   - `GpuAmbientOcclusionBlurMicroseconds`
   - `GpuLightCullMicroseconds`
   - `GpuAntiAliasingMicroseconds`
   - `ForwardTaskInvocations`
   - `ForwardEmittedMeshletsGpu`
   - `ForwardOcclusionTestedMeshletsGpu`
   - `ForwardOcclusionCulledMeshletsGpu`
   - `SceneSubmissionActiveMode`
   - `SceneSubmissionGpuCompactionActive`
   - `SceneSubmissionIndirectTaskCount`
   - `LightTileSaturationCount`
   - `MaxLightsInAnyTile`
   - `AverageLightsPerNonEmptyTile`
3. Export a JSON performance snapshot and an Nsight capture for:
   - Ultra, normal Sponza/interior view.
   - Ultra with GPU timing enabled.
   - Ultra with meshlet counters enabled for one diagnostic run only.
4. Record the baseline path in this file or a sibling benchmark note before implementing the first optimization.

Acceptance gate:

- A stable baseline exists and can be reproduced from command line or documented sample hotkeys.
- GPU timing is valid for at least one completed frame.

## 1. Forward Meshlet Over-Submission

Goal: make GPU compaction and indirect dispatch the default effective path for dense static scenes, so the forward pass submits the visible compacted meshlet count instead of the full CPU candidate count.

Current signal:

- Existing baseline diagnostics show normal Sponza around 108k forward meshlet tasks on the CPU path.
- Existing GPU submission baseline drops this to about 34k tasks.
- The Nsight capture hotspot is the long `ForwardPlusPass` mesh-task region.

Implementation steps:

1. Inspect the runtime conditions in `VulkanRenderer.DrawScene` that set:
   - `sceneData.SceneSubmissionGpuCompactionEnabled`
   - `sceneData.SceneSubmissionIndirectMeshletDispatchEnabled`
   - `sceneData.SceneSubmissionGpuLodSelectionEnabled`
   - `sceneData.SceneSubmissionGpuCompactionActive`
2. Confirm whether Ultra enables `Settings.SceneSubmission.GpuCompactionEnabled` and `Settings.SceneSubmission.IndirectMeshletDispatchEnabled` by default after `RenderSettings.ApplyQualityPreset(RenderQualityPreset.Ultra)`.
3. If defaults are already true, find why `SceneOpaqueCompactionPass.ShouldExecute` or `ForwardPlusPass` falls back to CPU lists in the captured scenario.
4. Add diagnostics if needed:
   - `SceneSubmissionForwardPath`
   - `SceneSubmissionCompactionSkipReason`
   - `SceneSubmissionIndirectDispatchSkipReason`
5. Verify `SceneOpaqueCompactionPass` writes counters and indirect dispatch arguments before `ForwardPlusPass` consumes them.
6. Make `ForwardPlusPass.DrawForwardBucketIndirect` use the compacted emitted count and indirect task count consistently in diagnostics.
7. Keep CPU fallback available and explicit for validation, unsupported hardware, and failure cases.
8. Update `SampleInputController` printout if needed so Ctrl+F11/Ctrl+F12 clearly reports the active path.

Validation:

- `SceneSubmissionActiveMode` reports GPU or indirect GPU path on Ultra.
- `SceneSubmissionGpuCompactionActive == 1`.
- `ForwardTaskInvocations` drops significantly from the CPU-list count.
- Render output matches CPU path in a validation comparison run.
- Nsight shows reduced `ForwardPlusPass` duration.

Risk:

- Validation may expose ordering or buffer lifetime bugs between compaction and forward rendering.
- GPU compaction can add overhead on tiny scenes, so adaptive fallback or thresholds may be needed.

## 2. Task Shader Occupancy And Granularity

Goal: reduce under-occupancy from one task workgroup per meshlet and move expensive meshlet filtering away from `forward.task` when possible.

Current signal:

- `forward.task` uses `layout(local_size_x = 1)`.
- Each task invocation performs bounds, optional frustum, optional Hi-Z projection, and then emits one mesh task.
- Nsight shows low SM throughput during the main forward mesh-task region.

Implementation steps:

1. Measure forward pass with scene GPU compaction enabled first. If over-submission remains high, continue.
2. Split the work into two paths:
   - Compacted path: task shader only validates compacted emitted count and emits.
   - Legacy path: task shader performs per-meshlet frustum/Hi-Z checks.
3. For the legacy path, prototype a batched task shader:
   - Increase `local_size_x` to a practical batch size such as 32 or 64.
   - Let one task workgroup inspect multiple candidate meshlets.
   - Emit only visible meshlets if the mesh shader payload contract can support multiple outputs.
4. If multi-emission conflicts with the current one-meshlet mesh shader design, prefer compute pre-compaction instead of forcing complex task payload changes.
5. Add pipeline variants in `MeshPipeline`:
   - Current compatibility task shader.
   - Fast compacted task shader.
   - Optional batched legacy task shader.
6. Select the pipeline in `ForwardPlusPass` based on `SceneSubmissionGpuCompactionActive` and dispatch mode.
7. Keep shader debug counter paths disabled by default because atomic diagnostics can distort occupancy.

Validation:

- Nsight occupancy improves in the forward mesh-task region.
- `ForwardPlusPass` GPU time decreases without visual differences.
- Meshlet diagnostic counters still work in explicit diagnostic mode.

Risk:

- Task payload changes can be invasive. Prefer compute compaction if the payload model becomes fragile.

## 3. Cheaper Forward Ambient Occlusion Sampling

Goal: stop paying depth-aware AO reconstruction per shaded forward pixel when it is not needed, especially on Ultra full-resolution AO.

Current signal:

- `forward.frag::SampleScreenSpaceAo` performs:
  - depth texture size lookup,
  - AO texture size lookup,
  - center depth fetch,
  - view depth reconstruction,
  - 2x2 AO taps,
  - 4 additional depth samples,
  - 4 view-depth reconstructions,
  - exponential depth weighting.
- Ultra sets AO full resolution and 32 SSAO samples in `RenderSettings.ApplyQualityPreset`.

Implementation steps:

1. Add an AO forward sampling mode to settings or derive it from AO resolution:
   - `Direct` when AO is full resolution.
   - `DepthAwareUpsample` when AO resolution is lower than scene resolution.
2. Add a packed push-constant flag in `GPUForwardPushConstants.DebugAndAoFlags` or a new field if layout budget allows.
3. Implement `SampleScreenSpaceAoDirect` in `forward.frag`:
   - Compute screen UV once.
   - Sample `AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX` directly.
   - Avoid depth reconstruction and `exp`.
4. Keep the existing depth-aware path for half-resolution AO.
5. Consider a cheaper bilinear path for half-res AO before using the full depth-aware path.
6. Update diagnostics:
   - `AmbientOcclusionForwardSamplingMode`
   - `AmbientOcclusionForwardDepthAwareSamples`
7. Test Ultra with full-res direct AO first.

Validation:

- Ultra visual AO remains acceptable.
- Forward fragment shader instruction count drops.
- Nsight shows lower forward fragment cost.
- Half-resolution AO still avoids haloing where the depth-aware path is selected.

Risk:

- Direct full-res AO can expose temporal or edge artifacts if the AO pass itself is noisy.

## 4. Forward Shader Variant Splitting

Goal: avoid running the full material/reflection/shadow uber-shader path for common opaque materials that only need base PBR, global IBL, and simple shadows.

Current signal:

- `forward.frag` handles alpha modes, material extensions, normal mapping, AO, IBL, local reflection probes, tiled lights, directional shadows, spot shadows, point shadows, and many debug views.
- `ForwardPlusPass.CanUseSimpleOpaquePipeline` only selects the simple path when reflections are disabled or global-only.
- Ultra enables reflections and max probes per pixel, keeping most opaque geometry on the full path.

Implementation steps:

1. Classify opaque materials into shader buckets during scene data build:
   - Simple opaque, no material extension, global reflection only.
   - Opaque with normal map.
   - Opaque with material extensions.
   - Opaque requiring local reflection probes.
   - Opaque receiving local shadows.
2. Add or reuse separate meshlet draw buffers per bucket if the existing simple/full split is not enough.
3. Add `MeshPipeline` variants:
   - `ForwardSimpleGlobalIblPipeline`
   - `ForwardSimpleLocalProbePipeline` if local probe loop can be limited.
   - `ForwardFullMaterialPipeline`
4. Move debug modes to force the full/debug pipeline only when active.
5. In `ForwardPlusPass`, draw cheapest buckets first while preserving depth test behavior.
6. Add diagnostics:
   - `ForwardSimpleMeshletCount`
   - `ForwardFullMaterialMeshletCount`
   - `ForwardLocalProbeMeshletCount`
7. Keep the existing full shader as fallback for correctness.

Validation:

- Output matches full path for materials without extensions.
- Forward shader instruction count and register pressure decrease for simple buckets.
- Nsight shows improved SM occupancy or lower forward duration.

Risk:

- More pipelines increase shader compilation and pipeline management complexity.
- Material classification must stay synchronized with importer/material feature flags.

## 5. Shadow Cost In Forward Lighting

Goal: reduce per-fragment shadow sampling cost in the forward pass, especially under Ultra's higher cascade and local shadow budgets.

Current signal:

- Ultra sets directional cascades to 4.
- `EvaluateDirectionalShadow` performs PCF per shaded fragment.
- `EvaluatePointShadow` can sample adjacent cube faces near face edges, multiplying shadow taps.

Implementation steps:

1. Add shadow receiver classification:
   - Receives directional shadows.
   - Receives local shadows.
   - No shadows.
2. Use material/object flags to skip shadow work for objects that do not need it.
3. Add shadow quality modes:
   - `Hard`
   - `PcfLow`
   - `PcfMedium`
   - `PcfHigh`
4. Map Ultra to high visual quality but avoid unconditional high tap counts for all shadow types.
5. Add early exits:
   - Skip local shadow functions if selected local shadow count is zero.
   - Skip point shadow face-edge adjacent sampling when PCF radius is zero or when outside configurable edge width.
6. Consider hardware comparison samplers or gather-based PCF where Vulkan format/sampler support allows.
7. Add diagnostics:
   - `DirectionalShadowPcfRadius`
   - `SpotShadowPcfRadius`
   - `PointShadowPcfRadius`
   - `ForwardShadowReceiverMeshletCount`

Validation:

- Visual shadow quality remains acceptable on Ultra.
- Shader instruction mix and texture sample count drop.
- Local shadow demos still render correctly.

Risk:

- Shadow artifacts are visually sensitive. Each quality change needs side-by-side screenshots.

## 6. Real Tiled Light Culling

Goal: replace the current conservative all-lights-per-visible-tile assignment with actual tile/light intersection to reduce fragment light loops as scene light count grows.

Current signal:

- `lightcull.comp` currently assigns every valid light to every visible tile.
- Per-fragment code later performs range and cone rejection.
- This is acceptable for a few lights, but it does not scale and wastes forward shader work.

Implementation steps:

1. Reconstruct tile min/max depth correctly for reverse-Z.
2. Build per-tile frustum planes in view or world space.
3. For point lights:
   - Test sphere against tile frustum.
   - Reject lights outside tile depth range.
4. For spot lights:
   - Start with conservative sphere test using range.
   - Add cone direction/angle test after sphere path is stable.
5. Always include directional lights separately or outside the tiled local-light list.
6. Change `ForwardPlusPass` / shader expectations so directional lights are handled without bloating every tile local list.
7. Add diagnostics:
   - `AverageLightsPerNonEmptyTile`
   - `MaxLightsInAnyTile`
   - `LightTileSaturationCount`
   - `LightCullRejectedPointCount`
   - `LightCullRejectedSpotCount`
8. Add a high-light-count benchmark scene to prove scaling.

Validation:

- Current sample scenes render identically.
- High-light-count scene shows lower average/max lights per tile.
- Forward shader light loop cost drops in Nsight.

Risk:

- Incorrect tile frustum math causes visible light popping or tile-shaped holes. Start conservative and tighten.

## 7. Tiled Light Buffer Clear Reduction

Goal: remove unnecessary per-frame clearing of the full tiled light index buffer.

Current signal:

- `SceneDataBuilder.ClearTiledLightBuffers` clears both header and index buffers every frame.
- The shader writes header count and the forward shader should only read index entries up to header count.

Implementation steps:

1. Audit every read of `TiledLightIndicesBuffer`.
2. Confirm all readers use `tileHeader.LightCount` and never scan stale entries.
3. Change clear behavior:
   - Keep clearing header buffer if needed.
   - Stop clearing index buffer by default.
4. If debug tooling needs deterministic stale data, add a debug setting to clear indices only in validation mode.
5. Add diagnostics:
   - `TiledLightIndexBufferClearBytes`
   - `TiledLightHeaderBufferClearBytes`
6. Validate with Vulkan validation enabled and debug overlays.

Validation:

- No visual change.
- No stale-light artifacts.
- Upload/recording work decreases.
- Nsight command timeline loses the full index-buffer fill cost.

Risk:

- Hidden debug or overlay path may assume cleared indices. Audit before removing the clear.

## 8. Hi-Z Value Verification And Adaptive Policy

Goal: prove whether depth pre-pass plus Hi-Z occlusion is helping the Ultra Sponza capture, and disable or reduce it when it does not pay for itself.

Current signal:

- Capture shows depth pre-pass around 2.2 ms before forward.
- Hi-Z/occlusion only helps if it rejects enough forward work to offset depth and pyramid cost.
- Existing adaptive policy depends on counters and warmup measurements.

Implementation steps:

1. Run controlled captures:
   - Ultra, Hi-Z on.
   - Ultra, Hi-Z off.
   - Ultra, depth pre-pass off if supported.
   - Ultra, meshlet counters on for a short diagnostic run.
2. Validate counter correctness:
   - `ForwardOcclusionTestedMeshletsGpu`
   - `ForwardOcclusionCulledMeshletsGpu`
   - `DepthTaskInvocations`
   - `ForwardTaskInvocations`
3. Improve adaptive policy if needed:
   - Require minimum tested meshlets.
   - Require useful cull rate.
   - Compare estimated saved forward cost against measured depth + Hi-Z build cost.
4. Add per-scene or camera-stable suppression:
   - If Hi-Z cull rate stays below threshold for N frames, skip Hi-Z build and task occlusion.
   - Periodically re-probe after camera movement or scene changes.
5. Add diagnostics:
   - `HiZAdaptiveStatus`
   - `HiZAdaptiveCullRate`
   - `HiZAdaptiveEstimatedSavedMicroseconds`
   - `HiZAdaptiveSuppressedFrameCount`
6. Ensure feature isolation and debug modes can force Hi-Z on/off for testing.

Validation:

- Hi-Z stays enabled only when it reduces total frame time.
- Ultra Sponza frame time improves or stays neutral.
- No incorrect occlusion appears during camera movement.

Risk:

- Aggressive suppression can lose occlusion wins in scenes with sudden visibility changes. Use conservative re-probe.

## Recommended Implementation Order

1. Establish reproducible baseline and GPU timing.
2. Fix/enable scene GPU compaction and indirect dispatch.
3. Add Hi-Z measurement and adaptive verification.
4. Simplify forward AO sampling.
5. Reduce tiled light buffer clear cost.
6. Add real tiled light culling.
7. Split forward shader variants.
8. Rework task shader granularity only if compaction and variants do not solve the low-occupancy hotspot.

Rationale:

- Scene GPU compaction has the clearest existing evidence and likely the largest immediate gain.
- Hi-Z verification prevents optimizing around a feature that may be net negative in this scene.
- AO and buffer clear changes are contained and low risk.
- Light culling matters more as light count grows.
- Shader variants and task shader redesign are higher-risk and should follow measurement.

## Test Plan

For each completed workstream:

1. Run unit tests:
   - `dotnet test Njulf.sln`
2. Run sample smoke:
   - Normal Sponza/interior.
   - Forest foliage.
   - Point shadow demo.
   - Spot shadow demo.
3. Export performance snapshots before and after.
4. Capture Nsight for the same camera/view/settings.
5. Compare:
   - GPU frame time.
   - Forward pass time.
   - Submitted/emitted meshlet counts.
   - Shadow pass time.
   - AO pass and forward AO cost.
   - Visual screenshots.

Done criteria:

- Ultra frame time improves measurably from the 24 ms capture.
- `ForwardPlusPass` is no longer dominated by avoidable over-submission.
- New fast paths have explicit diagnostics and conservative fallbacks.
- No visual regressions in normal, foliage, shadow, transparency, and reflection scenarios.
