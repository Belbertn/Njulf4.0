# Renderer Snapshot Remaining Implementation Plan - 2026-06-13

## Goal

Finish the remaining renderer snapshot optimization work with measurable diagnostics, lower default memory pressure, and reduced CPU command recording cost while preserving existing rendering behavior and tests.

## Step 1: GPU Timestamp Timing Backend

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Debugging/FrameTimingSnapshot.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/Core/VulkanContext.cs`
- `NjulfHelloGame/SampleInputController.cs`

Implementation:
1. Add a per-frame Vulkan timestamp query pool when `timestampComputeAndGraphics` is supported.
2. Reset/write timestamp pairs around render graph passes and explicit passes not owned by the graph, including skinning, shadows, Hi-Z, depth, forward, particles, fog, bloom, anti-aliasing, and composite.
3. Resolve/read timestamps from the completed frame after the frame fence signals.
4. Populate `FrameTimingSnapshot` and all `Gpu*Microseconds` diagnostics from query results.
5. Keep the existing GPU timing toggle; when disabled or unsupported, emit a precise snapshot reason.

Acceptance:
- Snapshots report `GpuTimingSupported`, `GpuTimingEnabled`, `GpuTimingPending`, `GpuTimingValid`, and `GpuTimingUnavailableReason` correctly.
- Supported hardware can produce non-zero per-pass GPU timings after the frame latency window.

## Step 2: Finish Occlusion Diagnostics

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/depth.task`

Implementation:
1. Keep explicit CPU-submitted, GPU candidate, GPU frustum culled, GPU occlusion tested, GPU rejected, and GPU emitted counters.
2. Preserve legacy fields only as compatibility aliases.
3. Add impossible-combination sanity text and reconciliation status.

Acceptance:
- Hi-Z disabled reports zero tested/rejected counters.
- Hi-Z enabled reports `emitted + rejected == tested` after frustum culling.

## Step 3: Reduce Hi-Z Recording Cost

Files:
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf.Rendering/Resources/HiZDepthPyramid.cs`
- `Njulf.Rendering/VulkanRenderer.cs`

Implementation:
1. Avoid descriptor writes during frame recording; allocate/update mip descriptor sets only when the pyramid is recreated.
2. Collapse redundant transitions in the Hi-Z pass.
3. Keep adaptive Hi-Z suppression, but base it on corrected rejected/tested counters.
4. Limit low-resolution pyramids to the useful mip count.

Acceptance:
- Hi-Z descriptor churn is zero during steady-state frames.
- Hi-Z is automatically suppressed when it rejects too few meshlets.

## Step 4: Reduce Shadow Recording And Memory

Files:
- `Njulf.Rendering/Pipeline/DirectionalShadowPass.cs`
- `Njulf.Rendering/Pipeline/PointShadowPass.cs`
- `Njulf.Rendering/Pipeline/SpotShadowPass.cs`
- `Njulf.Rendering/Resources/DirectionalShadowResources.cs`
- `Njulf.Rendering/Resources/PointShadowCubemapArray.cs`
- `Njulf.Rendering/Resources/SpotShadowAtlas.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`

Implementation:
1. Keep local shadow images lazily allocated only when budgets and feature flags require them.
2. Add directional, spot, and point static shadow signatures.
3. Skip re-recording static shadow passes when light settings and local-shadow caster inputs are unchanged.
4. For point shadows, skip faces whose selected light/caster signature has not changed.
5. Report skipped shadow update counts in diagnostics.

Acceptance:
- Default sample has no spot atlas allocation unless spot shadows are active.
- Shadow diagnostics report skipped updates.
- Static shadow maps do not re-record every frame.

## Step 5: Move Independent Pass Recording To Secondary Command Buffers

Files:
- `Njulf.Rendering/Core/CommandBufferManager.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/VulkanRenderer.cs`

Implementation:
1. Add reusable per-frame secondary command buffer pools.
2. Record independent passes into secondary command buffers when their inputs are frame-immutable.
3. Execute secondary buffers from the primary command buffer while preserving pass order and debug labels.
4. Keep primary recording as fallback when secondary recording is unavailable.

Acceptance:
- CPU pass record time can be measured separately for primary and secondary recording.
- RenderDoc-visible pass order remains readable.

## Step 6: Texture Budgeting And Snapshot Contributors

Files:
- `Njulf.Rendering/Resources/TextureManager.cs`
- `Njulf.Rendering/ServiceCollectionExtensions.cs`
- `Njulf.Assets/ModelImporter.cs`

Implementation:
1. Keep profile-based default imported texture dimension.
2. Add a texture budget profile enum for development, high quality, and cinematic limits.
3. Keep top-N texture asset diagnostics with source path, dimensions, mip count, and estimated bytes.
4. Defer compressed GPU formats and streaming unless a compressed asset pipeline exists; implementing fake compression would not improve the framework.

Acceptance:
- Development default loads at `1024`.
- Snapshot identifies largest texture contributors.

## Step 7: Reflection, Render Target, And Staging Allocation

Files:
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
- `Njulf.Rendering/Resources/RenderTargetManager.cs`
- `Njulf.Rendering/Memory/StagingRing.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`

Implementation:
1. Keep reflection probe cubemap capacity based on active/configured runtime capacity.
2. Lazily allocate AO, SMAA, TAA, and motion-vector targets according to enabled modes.
3. Keep bloom mip allocation clamped to configured mip count.
4. Keep small staging rings with grow-on-demand upload buffers.

Acceptance:
- Disabled AO/SMAA/TAA/motion-vector features report zero render-target bytes.
- Large uploads succeed without manually increasing staging size.

## Step 8: Mesh And Scene Buffer Right-Sizing

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/Memory/BufferManager.cs`

Implementation:
1. Replace uniform mesh buffer initial sizes with per-buffer initial capacities.
2. Add post-load mesh buffer compaction where safe.
3. Split scene buffer initial capacity by object/draw category.
4. Add high-water diagnostics for scene buffers.

Acceptance:
- Mesh allocation overhead is under one growth step after scene load.
- Scene buffer allocation reflects category usage rather than opaque worst-case everywhere.

## Step 9: Meshlet Quality And Import Diagnostics

Files:
- `Njulf.Assets/MeshletBuilder.cs`
- `Njulf.Assets/ModelImporter.cs`
- `Njulf.Rendering/Resources/MeshManager.cs`

Implementation:
1. Increase static LOD0 target triangle count where compatible with shader limits.
2. Add import diagnostics for meshlet count, small meshlet count, average triangles, and average vertices.
3. Defer a full spatial clustering rewrite unless the current builder lacks stable triangle grouping extension points; an unsafe rewrite would risk asset correctness more than it improves this pass.

Acceptance:
- Imported model diagnostics show small meshlet counts per model.
- Submitted meshlet count can be compared before and after import settings changes.

## Step 10: Quality Profiles And Snapshot Reproducibility

Files:
- `Njulf.Rendering/Data/RenderSettings.cs`
- `NjulfHelloGame/SampleInputController.cs`
- `NjulfHelloGame/SamplePerformanceScenario.cs`
- `NjulfHelloGame/SamplePerformanceScenarioRunner.cs`

Implementation:
1. Keep `Development`, `PerformanceCapture`, and `Cinematic` presets.
2. Add feature-group isolation modes for geometry, shadows, post, reflections, animation, and particles.
3. Persist active preset and feature group in snapshots.
4. Add sample key controls for cycling preset and feature group.

Acceptance:
- Performance snapshots encode active quality preset and feature group.
- User can isolate major renderer systems without source edits.

## Verification

1. Run `dotnet build Njulf.sln --no-restore`.
2. Run `dotnet test Njulf.sln --no-restore`.
3. Export one performance snapshot with Hi-Z enabled and one with Hi-Z disabled.
4. Confirm memory diagnostics for texture, reflection, shadow, render target, and staging categories.

## Final Implementation Status

Completed:
1. GPU timestamps are implemented through per-frame Vulkan query pools, gated by device support and the runtime timing toggle. Render graph passes and explicit skinning/shadow passes write pass timings into `FrameTimingSnapshot` and renderer diagnostics with unsupported/disabled/pending reason text.
2. Forward occlusion diagnostics now distinguish CPU-submitted meshlets, GPU task invocations, frustum culls, occlusion-tested meshlets, occlusion-rejected meshlets, emitted meshlets, reconciliation state, and sanity text.
3. Hi-Z descriptor writes are confirmed to be recreate-only, not per-frame. Adaptive suppression now relies on the corrected occlusion counters.
4. Default shadow memory and recording cost were reduced: local shadow images are lazily allocated, sample lighting only enables point/spot shadow budgets for lighting modes that need them, directional defaults are lower-cost, and static shadow passes skip recording when their settings, selected lights, and caster draw-list signatures are unchanged.
5. Texture budgeting uses a 1024 development default and snapshots report the largest loaded texture contributors.
6. Reflection probe defaults and diagnostics use runtime active/configured capacity instead of a max-capacity estimate.
7. Render targets for AO, SMAA, TAA, motion vectors, and bloom are allocated according to active settings and re-created/re-registered when those feature modes change.
8. Staging defaults are smaller and large uploads use dedicated grow-on-demand upload buffers.
9. Mesh buffers now use per-buffer initial sizes, support post-load compaction, and expose compaction counters. The sample compacts after scene load.
10. Scene draw buffers now use category-specific initial capacities and report high-water bytes by object, opaque, depth, transparent, and shadow categories.
11. Meshlet LOD0 import targets were increased within shader limits, and snapshots include top meshlet-quality contributors with meshlet count, small-meshlet count, and average triangle/vertex counts.
12. Quality presets and feature-isolation modes are encoded in snapshots and can be cycled from the sample input controller.

Intentionally not implemented:
1. Secondary command buffer recording was not added. The current render pass objects mutate shared layout/resource state and use dynamic rendering without a secondary-command-buffer inheritance contract. Adding a shallow secondary path would either serialize through the same state or risk invalid command buffers, so this needs a dedicated pass API refactor before it improves the framework.
2. Per-face point-shadow skipping was not added because the renderer currently builds one local-shadow caster draw list per selected point light, not per cubemap face. Whole-pass static skipping is implemented correctly; per-face skipping should follow a per-face caster classification pass.
3. Compressed texture formats, residency streaming, and a full meshlet spatial clustering rewrite were deferred. They require asset-pipeline changes outside this renderer snapshot optimization pass; fake compression or a broad clustering rewrite would add risk without a reliable framework improvement.
4. A multi-mip Hi-Z compute shader rewrite was not added. Descriptor churn was already zero in steady state, and replacing the shader path should be driven by fresh captures after the corrected counters and timing backend are available.

Verification completed:
1. `dotnet build Njulf.sln --no-restore` passed.
2. `dotnet test Njulf.sln --no-restore` passed with 188/188 tests.

Verification not completed:
1. New Hi-Z on/off performance snapshot exports were not captured in this non-interactive implementation pass. The snapshot path is ready to produce meaningful timing, memory, quality preset, feature-isolation, texture, meshlet, and allocation diagnostics when the sample is run.

## Additional Completion Pass - 2026-06-14

Implemented after the remaining-step follow-up:
1. Added reusable per-frame secondary graphics command pools and secondary command buffer allocation/reuse in `CommandBufferManager`.
2. Added explicit render-pass opt-in for secondary command buffers. Compute-only passes now support the path: Hi-Z, ambient occlusion, ambient occlusion blur, tiled light culling, fog, and bloom.
3. Updated `RenderGraph` to record eligible passes into secondary command buffers when enabled, execute them from the primary command buffer in the same pass order, keep barriers on the primary command buffer, and report primary/secondary CPU record counters in snapshots.
4. Added `RenderSettings.UseSecondaryCommandBuffers`, snapshot diagnostics, and a sample `Ctrl+F7` toggle for A/B captures.
5. Added point-shadow per-face coverage masks. The scene builder computes conservative selected-light face masks while it builds local-shadow meshlet draws, point-shadow recording skips empty faces, and snapshots report skipped face count.
6. Added explicit texture budget profiles (`Development`, `HighQuality`, `Cinematic`, `Custom`) on rendering options, preserved direct custom max-size configuration, and persisted the active profile in diagnostics.
7. Limited Hi-Z pyramid generation to useful trailing mip dimensions instead of always building down to 1x1.
8. Added tests for texture budget profiles, secondary command buffer defaults, new diagnostics defaults, and snapshot contract fields.

Still intentionally scoped out:
1. Graphics/dynamic-rendering passes are not recorded into secondary command buffers yet. They now have infrastructure to opt in, but each graphics pass needs a dynamic-rendering inheritance contract before enabling secondary recording is meaningful and validation-safe.
2. Compressed texture formats and residency streaming remain asset-pipeline features. The renderer now has explicit budget profiles and top-contributor diagnostics, but adding fake compression or unload behavior without asset residency ownership would not improve correctness.
3. A multi-mip Hi-Z shader rewrite remains capture-driven. The pyramid now avoids low-value tiny mips, and the timestamp/counter path can show whether a shader rewrite is warranted.

Verification completed:
1. `dotnet build Njulf.sln --no-restore` passed.
2. `dotnet test Njulf.sln --no-restore` passed with 189/189 tests.
