# Meshlet Occlusion Culling Implementation Plan

Target outcome: when the full Sponza scene is in view, meshlets hidden behind already-rendered opaque geometry are rejected before forward shading. The final implementation should reduce mesh shader expansion and fragment work for occluded meshlets, while preserving correctness for opaque, masked, and transparent materials.

Non-goals:
- Do not change input or camera behavior. `NjulfHelloGame/SampleInputController.cs` is not in the render hot path.
- Do not rely on CPU occlusion queries for per-meshlet decisions.
- Do not occlusion-cull transparent blend meshlets in the first implementation.

## Current State

1. CPU culling in `SceneDataBuilder` only removes whole render objects outside the camera frustum.
2. `depth.task` and `forward.task` only test meshlet bounding spheres against the camera frustum.
3. `DepthPrePass` writes a depth buffer, but no pass builds a Hi-Z depth pyramid.
4. `forward.task` still launches one task workgroup per visible-in-frustum meshlet, even when the meshlet is fully hidden by nearer geometry.
5. `TiledLightCullingPass` samples the depth texture, so depth is already exposed as a sampled bindless texture at `BindlessIndex.DepthTexture`.

## Implementation Strategy

Use the existing depth prepass as the occluder source, build a max-depth Hi-Z pyramid for reverse-Z, then use that pyramid to reject meshlets before forward mesh shader expansion.

Because the engine uses reverse-Z:
- Nearer depth values are larger.
- A meshlet is occluded when the nearest possible depth of the meshlet is less than or equal to the max occluder depth sampled from the Hi-Z pyramid, with a small bias.

## Phase 1: Add Diagnostics Before Changing Behavior

1. Extend `RendererDiagnostics` with:
   - `SubmittedOpaqueMeshlets`
   - `FrustumCulledMeshletsGpu` if available later
   - `OcclusionCulledMeshlets`
   - `ForwardMeshletCandidates`
   - `ForwardMeshletVisibleAfterOcclusion`
2. Print these values in `SampleDiagnosticsReporter` once available.
3. Add a debug toggle or constant to disable occlusion culling at runtime for A/B testing.
4. Validation gate:
   - Existing tests still pass.
   - The first-frame diagnostics show current meshlet counts before occlusion is enabled.

## Phase 2: Create Hi-Z Depth Resources

1. Add a small resource owner, for example `HiZDepthPyramid`, under `Njulf.Rendering/Resources` or `Njulf.Rendering/Pipeline`.
2. Allocate a sampled storage image with mip levels sized from the swapchain extent:
   - Format: start with `R32Sfloat` for implementation simplicity.
   - Usage: `StorageBit | SampledBit | TransferSrcBit | TransferDstBit` if needed.
   - Mip count: `floor(log2(max(width, height))) + 1`.
3. Create one image view for the full pyramid and per-mip image views if Silk/Vulkan storage image binding requires them.
4. Register the Hi-Z texture/image in the bindless texture table or add a storage-image descriptor path if storage images are not supported by current bindless layout.
5. Recreate the pyramid on swapchain resize.
6. Validation gate:
   - Vulkan validation layers report no image usage/layout/descriptor errors.
   - Resize destroys and recreates the pyramid cleanly.

## Phase 3: Build the Hi-Z Pyramid

1. Add `hiz_downsample.comp` in `Njulf.Shaders`.
2. Pass 0 should read the depth buffer and write mip 0 of the Hi-Z image.
3. Later passes should read mip N and write mip N+1.
4. For reverse-Z, each output texel stores the maximum of the source 2x2 region.
5. Add a `HiZBuildPass` after `DepthPrePass` and before `TiledLightCullingPass` / `ForwardPlusPass`.
6. Add image barriers:
   - depth attachment write -> shader sampled read
   - Hi-Z mip write -> shader sampled read for the next mip
   - final Hi-Z image -> task shader sampled read or compute shader sampled read
7. Start with one dispatch per mip level. Optimize later if needed.
8. Validation gate:
   - Shader compiles through `Njulf.Shaders.csproj`.
   - A debug readback or debug visualization confirms coarse mips contain conservative max depth.
   - Existing light culling still reads the normal depth texture correctly.

## Phase 4: Add Shader-Side Occlusion Test Helper

1. Add common GLSL helpers for projecting a meshlet sphere to screen bounds:
   - Transform sphere center to clip space.
   - Estimate screen-space radius conservatively.
   - Clamp to screen rectangle.
2. Compute mip level from projected screen size:
   - Large screen bounds use low mip levels.
   - Small bounds use higher mip levels.
   - Clamp between 0 and `HiZMipCount - 1`.
3. Sample the Hi-Z pyramid at representative points:
   - Start with center + 4 corners of projected bounds.
   - Use max sampled occluder depth for reverse-Z.
4. Compute conservative meshlet nearest depth.
5. Reject when `meshletNearestDepth <= sampledOccluderDepth + bias`.
6. Add push constants or scene data fields for:
   - Hi-Z texture index
   - Hi-Z dimensions
   - Hi-Z mip count
   - occlusion enable flag
   - occlusion bias
7. Validation gate:
   - With occlusion disabled, output is identical to current rendering.
   - With a high bias or forced disabled path, no meshlets are incorrectly culled.

## Phase 5: Integrate Occlusion Into `forward.task`

1. Keep the existing frustum test first because it is cheap.
2. After frustum pass, run the Hi-Z occlusion test.
3. If occluded, call `EmitMeshTasksEXT(0, 0, 0)`.
4. Keep transparent pass conservative:
   - Opaque/masked forward pass may use occlusion culling.
   - Transparent blend pass should initially skip occlusion culling or only test against opaque depth if visual issues are acceptable.
5. Add a push constant flag so `TransparentForwardPass` can disable the occlusion test while `ForwardPlusPass` enables it.
6. Validation gate:
   - Sponza renders without missing walls/floors/columns when moving the camera.
   - Close-up geometry does not pop incorrectly at frustum edges.
   - Alpha-tested materials are checked carefully because they write depth only where alpha passes.

## Phase 6: Add Runtime Metrics For Occlusion Effectiveness

1. Add a GPU counter buffer for task shader culling counters or a compute culling counter path.
2. Counters to collect:
   - tested meshlets
   - frustum-rejected meshlets
   - occlusion-rejected meshlets
   - emitted meshlets
3. Read counters back with at least one-frame latency to avoid GPU stalls.
4. Show counters in `SampleDiagnosticsReporter` periodically, not every frame.
5. Validation gate:
   - Looking at the whole Sponza model reports non-zero occlusion-rejected meshlets.
   - The counter readback does not introduce frame stalls.

## Phase 7: Move From Task-Shader Rejection To Compacted Draws

Task-shader occlusion avoids mesh shader expansion, but still launches one task workgroup per candidate meshlet. If framerate still tanks, compact visible meshlets before rendering.

1. Add `meshlet_cull.comp`.
2. Input:
   - opaque meshlet draw buffer
   - instance buffer
   - meshlet buffer
   - camera/frustum data
   - Hi-Z pyramid
3. Output:
   - compacted visible meshlet draw buffer
   - indirect draw command buffer for `vkCmdDrawMeshTasksIndirectEXT`
   - counters/diagnostics buffer
4. Update bindless indices for compacted buffers.
5. Update `ForwardPlusPass` to use indirect mesh task draw if supported:
   - Check extension support for `VK_EXT_mesh_shader` indirect path and Silk binding availability.
   - Fall back to direct task draw with task-shader rejection if unsupported.
6. Barriers:
   - cull compute shader writes compacted draw buffer and indirect args
   - forward task/mesh reads compacted draw buffer
   - indirect command read barrier before draw
7. Validation gate:
   - The direct and indirect paths render the same image with occlusion disabled.
   - With occlusion enabled, indirect task group count drops when viewing occluded Sponza regions.

## Phase 8: Improve Occluder Correctness

1. Ensure depth prepass handles alpha-mask materials correctly:
   - If masked materials currently render in depth without alpha testing, fix depth fragment behavior or route masked materials carefully.
2. Exclude blend materials from depth occluders unless explicitly desired.
3. Consider two occlusion modes:
   - Conservative: opaque-only occluders.
   - Aggressive: opaque + mask occluders with alpha-tested depth.
4. Add a small bias and possibly dilate Hi-Z mip 0 to avoid flicker.
5. Validation gate:
   - No visible popping on thin pillars, grates, curtains, or alpha-tested edges.

## Phase 9: Performance Tuning

1. Profile these timings separately:
   - depth prepass
   - Hi-Z build
   - meshlet cull compute or task shader rejection
   - forward pass
   - transparent pass
2. Tune meshlet generation if task counts remain too high:
   - Current meshlets are simple sequential chunks.
   - Better spatial meshlet clustering improves both frustum and occlusion efficiency.
3. Add an optional near-camera skip:
   - Do not occlusion-test very large projected meshlets where test cost may exceed benefit.
4. Add a debug overlay showing:
   - Hi-Z mips
   - culled meshlet bounding boxes
   - visible meshlet count over time
5. Validation gate:
   - Full Sponza view has materially better frame time than baseline.
   - GPU capture confirms fewer mesh shader invocations and lower fragment workload.

## Recommended First PR Scope

Implement only these pieces first:
1. Hi-Z resource allocation and resize handling.
2. Hi-Z build compute pass after `DepthPrePass`.
3. Shader-side occlusion test in `forward.task` behind a push constant flag.
4. Diagnostics showing tested and occluded meshlet counts if practical.
5. Keep compacted indirect drawing for a second PR.

Reasoning: this gets visible benefit with less engine churn. If task dispatch overhead remains high, Phase 7 becomes the next required step.

## Files Expected To Change

Likely new files:
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- `Njulf.Rendering/Resources/HiZDepthPyramid.cs`
- `Njulf.Shaders/hiz_downsample.comp`
- Optional: `Njulf.Shaders/meshlet_cull.comp` in Phase 7

Likely changed files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs` only if pass ordering/barriers need richer handling
- `Njulf.Rendering/Pipeline/PipelineObjects/ComputePipeline.cs`
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
- `Njulf.Rendering/Descriptors/BindlessHeap.cs`
- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`
- `Njulf.Shaders/common.glsl`
- `Njulf.Shaders/forward.task`
- `Njulf.Tests/BindlessIndexTests.cs`
- `Njulf.Tests/GPUStructLayoutTests.cs`
- `Njulf.Tests/ShaderBuildTests.cs`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`

## Definition Of Done

1. `dotnet test Njulf.sln` passes.
2. Shader compilation passes for all new shaders.
3. Vulkan validation reports no descriptor, layout, or barrier errors.
4. Occlusion can be toggled off for debugging.
5. Full Sponza view shows reduced forward meshlet work in diagnostics or GPU capture.
6. No obvious missing geometry or camera-motion popping in the sample.
