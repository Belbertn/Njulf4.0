# Renderer Performance Remediation Plan

Target outcome: fix the current slideshow behavior when the whole model fits on screen, reduce general renderer overhead, and make `RendererDiagnostics` report enough real data to prove each fix worked.

Primary symptom:
- When the entire model is inside the camera frustum, frame time collapses.

Current likely cause:
- CPU object culling rejects almost nothing.
- `SceneDataBuilder` rebuilds and uploads full per-frame scene draw lists.
- Depth and forward passes each launch one mesh task workgroup per opaque meshlet.
- Current task-shader culling rejects after workgroups have already launched.
- Hi-Z occlusion adds per-meshlet work but currently has no real diagnostics proving it saves enough work.
- Forward shading is texture/PBR heavy when large amounts of geometry cover the screen.

Non-goals:
- Do not rewrite the renderer in one pass.
- Do not remove mesh shaders.
- Do not optimize blindly without diagnostics.
- Do not make transparent/blend occlusion aggressive until opaque correctness is stable.

## Phase 0: Establish Baseline Measurements

Goal: capture repeatable numbers before changing behavior.

1. Add a repeatable sample camera position for "full model in view" in `NjulfHelloGame`.
2. Add a second sample camera position for "close interior view".
3. Capture baseline diagnostics for both views:
   - visible objects
   - visible meshlets
   - opaque meshlets
   - transparent meshlets
   - uploaded bytes
   - material count
   - texture count
   - local light count
   - tile count
4. Capture GPU timing if available:
   - scene upload/build CPU time
   - depth prepass GPU time
   - Hi-Z build GPU time
   - tiled light culling GPU time
   - forward opaque GPU time
   - transparent GPU time
5. Add a temporary manual A/B switch for:
   - Hi-Z occlusion enabled/disabled
   - depth prepass enabled/disabled if possible
   - transparent pass enabled/disabled

Validation gate:
- A single test run can reproduce the slideshow camera state.
- Baseline numbers are recorded in a comment or markdown note before fixes begin.

## Phase 1: Make `RendererDiagnostics` Truthful

Goal: stop reporting placeholder values that hide the real bottleneck.

Current issue:
- `VulkanRenderer.BuildDiagnostics` fills `SubmittedOpaqueMeshlets`, `ForwardMeshletCandidates`, and `ForwardMeshletVisibleAfterOcclusion` from `sceneData.OpaqueMeshletCount`.
- `FrustumCulledMeshletsGpu` and `OcclusionCulledMeshlets` are always `0`.

Steps:
1. Keep the existing fields in `Njulf.Rendering/Data/RendererDiagnostics.cs`.
2. Add fields if needed:
   - `CpuSceneBuildMicroseconds`
   - `GpuDepthPrePassMicroseconds`
   - `GpuHiZBuildMicroseconds`
   - `GpuForwardOpaqueMicroseconds`
   - `GpuTransparentMicroseconds`
   - `SceneUploadCount`
   - `SceneUploadSkipped`
3. Add a GPU diagnostics buffer for meshlet counters:
   - depth candidates
   - depth frustum culled
   - depth emitted
   - forward candidates
   - forward frustum culled
   - forward occlusion culled
   - forward emitted
4. Update `depth.task` and `forward.task` to increment counters with atomics behind a debug/diagnostics flag.
5. Read counters back with at least one-frame latency.
6. Do not block the GPU to read diagnostics.
7. Update `VulkanRenderer.BuildDiagnostics` to use real counter values when available.
8. Update `NjulfHelloGame/SampleDiagnosticsReporter.cs` to print periodic or first-frame diagnostics without spamming the console.

Validation gate:
- Diagnostics show `ForwardMeshletVisibleAfterOcclusion <= ForwardMeshletCandidates`.
- Diagnostics show non-zero culled counts when culling actually rejects work.
- Counter readback does not add visible stutter.

## Phase 2: Remove Avoidable Per-Frame CPU Allocations And Copies

Goal: reduce CPU cost before changing rendering architecture.

Current issue:
- `SceneDataBuilder.Build` creates a new `SceneRenderingData` and copies large lists into it every frame.
- These copied lists are mostly diagnostics/debug data and are not needed by render passes.

Steps:
1. Make `SceneRenderingData` a lightweight per-frame metadata object for render passes.
2. Stop copying full draw command lists into `SceneRenderingData` by default:
   - avoid `ObjectData.AddRange`
   - avoid `MaterialData.AddRange`
   - avoid `MeshletDrawCommands.AddRange`
   - avoid `OpaqueMeshletDrawCommands.AddRange`
   - avoid `TransparentMeshletDrawCommands.AddRange`
3. If CPU-side snapshots are still useful for tests/debugging, guard them behind an explicit diagnostics flag.
4. Pre-size reusable lists in `SceneDataBuilder` based on previous frame high-water marks.
5. Keep `_meshInfoCache` if useful, but avoid clearing stable mesh metadata every frame unless scene contents changed.
6. Add CPU timing around `SceneDataBuilder.Build`.

Validation gate:
- Full model view allocates significantly less memory per frame.
- `UploadedBytes` and visible counts remain unchanged from baseline.
- Rendering output is unchanged.

## Phase 3: Skip Static Scene Uploads When Nothing Changed

Goal: stop uploading full object and meshlet draw buffers every frame for static scenes.

Current issue:
- `SceneDataBuilder.Build` uploads object data, instance data, opaque meshlet draw commands, and transparent meshlet draw commands every frame.
- For the sample, most scene data is static unless camera-dependent culling or object transforms change.

Steps:
1. Add dirty tracking to `Scene` or a renderer-side scene cache:
   - scene object list changed
   - object transform changed
   - object visibility changed
   - object material changed
   - camera frustum changed enough to require CPU-visible set rebuild
2. Split static and dynamic uploads:
   - static object/material/meshlet draw data
   - per-frame camera/light/push constants
   - dynamic object transforms if needed
3. Cache per-object meshlet draw command ranges.
4. Rebuild CPU visible-object list only when camera or visibility changes.
5. Re-upload meshlet draw buffers only when the visible set changes.
6. Re-upload object data only when transforms/material bindings change.
7. Keep material uploads dirty-only. `MaterialManager.UploadMaterials` already has a dirty flag, so preserve that behavior.

Validation gate:
- With a stationary camera and static scene, `UploadedBytes` drops near zero after warmup.
- With camera movement, uploads happen only for data that truly changes.
- Scene mutation still updates rendering correctly.

## Phase 4: Improve CPU Culling Granularity

Goal: reduce candidate meshlets before GPU dispatch.

Current issue:
- CPU frustum culling is per render object only.
- If an object intersects the frustum, every meshlet from that object is emitted.

Steps:
1. Store meshlet local bounding spheres in CPU-accessible mesh metadata or a compact CPU cache.
2. During scene build, frustum-test meshlets for visible objects when the object is large enough to justify it.
3. Emit only CPU-frustum-visible meshlets into draw command buffers.
4. Add diagnostics:
   - object candidates
   - object frustum culled
   - meshlet CPU candidates
   - meshlet CPU frustum culled
5. Use a threshold to avoid CPU meshlet culling for tiny objects:
   - if object has fewer than N meshlets, emit all after object cull.
6. Keep GPU frustum culling as a safety net initially.

Validation gate:
- Full model view may still have high counts, but partial views should emit fewer meshlets.
- CPU build time does not exceed the saved GPU time.
- Visual output is unchanged.

## Phase 5: Fix Meshlet Generation Quality

Goal: make meshlets spatially coherent so frustum and occlusion culling are effective.

Current issue:
- `MeshManager.GenerateMeshlets` groups triangles sequentially until limits are reached.
- Sequential meshlets can be spatially poor, which reduces culling efficiency.

Steps:
1. Add diagnostics during model upload:
   - meshlet count per mesh
   - average triangles per meshlet
   - average vertices per meshlet
   - bounding sphere radius distribution
2. Replace sequential meshlet generation with a spatial/adjacency-aware builder.
3. Prefer preserving triangle locality and material/submesh boundaries.
4. Keep max limits:
   - max 64 vertices
   - max 126 triangles
5. Validate meshlet local vertex and triangle ranges after generation.
6. Compare old and new meshlet counts and bounding sphere sizes.

Validation gate:
- Meshlet bounding spheres are smaller or more spatially coherent.
- CPU/GPU frustum rejection improves in partial views.
- No mesh corruption or missing triangles.

## Phase 6: Make Hi-Z Occlusion Cost-Effective

Goal: keep Hi-Z only when it saves more than it costs.

Current issue:
- Hi-Z is built every frame.
- `forward.task` performs multiple projections and five texture samples per candidate meshlet.
- If most meshlets are visible, occlusion tests add cost with little benefit.

Steps:
1. Use diagnostics from Phase 1 to measure:
   - Hi-Z build cost
   - forward task occlusion rejection rate
   - forward emitted meshlets
2. Add an adaptive occlusion toggle:
   - disable Hi-Z for frames/views where rejection rate is below a threshold
   - re-enable periodically or when camera moves significantly
3. Skip occlusion tests for meshlets that are too large on screen.
4. Skip occlusion tests for very near meshlets.
5. Reduce `forward.task` occlusion sample count if quality allows:
   - start with center + 4 corners
   - test center-only or 2x2 conservative variants behind a debug option
6. Verify reverse-Z math:
   - Hi-Z downsample must preserve conservative occluder depth for reverse-Z.
   - The cull comparison must reject only when the meshlet is safely behind occluders.

Validation gate:
- Hi-Z enabled improves or matches frame time in occluded views.
- Hi-Z disabled wins or matches frame time when the whole scene is visible and rejection is low.
- No missing geometry or popping during camera movement.

## Phase 7: Compact Visible Meshlets Before Forward Rendering

Goal: avoid launching one forward task workgroup per candidate meshlet.

Current issue:
- Task-shader culling avoids mesh shader expansion, but task workgroups are still launched for every candidate.

Steps:
1. Add a compute culling pass for opaque meshlets.
2. Inputs:
   - opaque meshlet draw buffer
   - instance/object buffer
   - meshlet buffer
   - camera frustum data
   - Hi-Z pyramid if enabled
3. Outputs:
   - compacted visible opaque meshlet draw buffer
   - indirect mesh task draw command
   - diagnostics counters
4. Use append/atomic counter to compact visible meshlets.
5. Update `ForwardPlusPass` to draw compacted meshlets using indirect mesh task drawing if supported.
6. Keep a fallback path:
   - direct `CmdDrawMeshTask` with task-shader rejection.
7. Add barriers:
   - compute shader write to mesh/task shader read
   - compute shader write to indirect command read
8. Keep depth prepass direct initially unless depth dispatch is also proven expensive.

Validation gate:
- Full model view launches fewer forward task workgroups when culling rejects meshlets.
- Direct and compacted paths render identical output with culling disabled.
- GPU capture confirms lower task/mesh shader invocation counts.

## Phase 8: Optimize Depth Prepass Strategy

Goal: avoid paying for depth work that does not help.

Current issue:
- Depth prepass dispatches all opaque meshlets.
- Forward pass uses the resulting depth and Hi-Z, but the prepass may be expensive for fully visible scenes.

Steps:
1. Measure depth prepass cost independently.
2. Add a mode switch:
   - depth prepass on
   - depth prepass off
   - depth prepass only when occlusion is expected to help
3. If depth prepass remains enabled, consider compacting depth meshlets too.
4. Ensure masked materials are handled correctly:
   - alpha-test in depth if masked objects write depth
   - avoid using blend materials as occluders by default
5. Consider a cheaper depth-only mesh shader variant:
   - no unnecessary descriptor bindings
   - minimal payload
   - no color attachment setup

Validation gate:
- Depth prepass is only enabled when it improves total frame time.
- Masked/transparent material behavior remains visually correct.

## Phase 9: Reduce Forward Fragment Cost

Goal: reduce cost when lots of pixels are shaded.

Current issue:
- `forward.frag` samples several material textures and evaluates PBR lighting for every covered pixel.

Steps:
1. Add material feature flags:
   - has albedo texture
   - has normal texture
   - has metallic/roughness texture
   - has emissive texture
2. Skip texture samples for missing/default textures instead of sampling default textures.
3. Add a no-normal-map shader branch or pipeline variant for materials without normal maps.
4. Avoid emissive sampling when emissive texture is default black and emissive factor is zero.
5. Keep directional-only lighting path simple.
6. Profile whether branches or variants are better for the target GPUs.

Validation gate:
- Texture sample count is lower for default/missing material maps.
- Output remains equivalent for textured materials.
- Full-screen Sponza views improve when fragment-bound.

## Phase 10: Reduce Tiled Light Buffer Work

Goal: keep light culling proportional to actual lighting complexity.

Current state:
- The sample currently uses directional-only lighting, so tiled light culling is skipped.
- If local lights are enabled, tiled buffers are cleared and compute dispatch runs per tile.

Steps:
1. Keep tiled light culling disabled when `LocalLightCount == 0`.
2. If local lights are present, avoid clearing oversized tile buffers every frame when possible.
3. Track active tile count based on current swapchain extent.
4. Add diagnostics for:
   - tile count
   - local light count
   - max lights per tile
   - overflow count
5. Consider reducing `MaxLightsPerTile` or using compact per-frame allocations if memory bandwidth becomes visible.

Validation gate:
- Directional-only sample does no tiled light clear/dispatch.
- Local-light sample still renders correctly.

## Phase 11: Improve Render Pass Timing Infrastructure

Goal: make future performance regressions visible.

Steps:
1. Add timestamp query support in the renderer.
2. Wrap each render graph pass:
   - begin timestamp
   - end timestamp
3. Read timestamps back with latency.
4. Store pass timings in `RendererDiagnostics`.
5. Print concise timing summaries in the sample.
6. Keep timing optional to avoid overhead in normal runs.

Validation gate:
- Diagnostics can identify whether the frame is CPU-build-bound, depth-bound, Hi-Z-bound, forward-task-bound, or fragment-bound.

## Phase 12: Add Regression Tests And Debug Views

Goal: prevent performance fixes from breaking correctness.

Steps:
1. Add unit tests for diagnostics construction.
2. Add tests for scene data dirty-state behavior.
3. Add tests for meshlet generation invariants.
4. Add shader build tests for new compute shaders.
5. Add debug visualization options:
   - show meshlet bounds
   - show Hi-Z mip
   - show overdraw or shaded pixel heatmap if practical
6. Add a scripted camera path for manual performance comparison.

Validation gate:
- `dotnet test Njulf.sln` passes.
- Shader compilation passes.
- Vulkan validation reports no layout, descriptor, or synchronization errors.

## Recommended Implementation Order

1. Phase 0: baseline measurements.
2. Phase 1: truthful diagnostics.
3. Phase 2: remove avoidable CPU copies.
4. Phase 3: skip static uploads.
5. Phase 6: make Hi-Z adaptive and measurable.
6. Phase 7: compact visible meshlets before forward rendering.
7. Phase 5: improve meshlet generation quality.
8. Phase 8: tune depth prepass strategy.
9. Phase 9: reduce forward fragment cost.
10. Phase 10-12: polish, tests, timing, and debug views.

Reasoning:
- Diagnostics must come first, otherwise fixes cannot be proven.
- CPU copies/uploads are low-risk and likely improve all views.
- Compacted draws address the key architectural issue: launching one task group per candidate meshlet.
- Shader/material optimization should come after determining whether the workload is task/mesh-bound or fragment-bound.

## Files Expected To Change

Likely changed files:
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/DepthPrePass.cs`
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Resources/HiZDepthPyramid.cs`
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Shaders/common.glsl`
- `Njulf.Shaders/depth.task`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/forward.frag`
- `Njulf.Shaders/hiz_downsample.comp`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`
- `NjulfHelloGame/Program.cs`

Likely new files:
- `Njulf.Rendering/Pipeline/MeshletCullPass.cs`
- `Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs`
- `Njulf.Rendering/Utilities/GpuTimestampQueryPool.cs`
- `Njulf.Shaders/meshlet_cull.comp`

## Definition Of Done

1. Full-model-in-view no longer becomes a slideshow on the baseline machine.
2. `RendererDiagnostics` reports real candidate, culled, emitted, upload, and timing values.
3. Static camera/static scene avoids repeated full scene uploads after warmup.
4. Forward rendering no longer launches one task group per original candidate meshlet when compacted culling is enabled.
5. Hi-Z can be disabled automatically or manually when it is not beneficial.
6. No missing geometry, obvious popping, or alpha-material regressions in Sponza.
7. `dotnet test Njulf.sln` passes.
8. Vulkan validation is clean.
