# Nsight VRAM Residency Performance Plan

Target outcome: reduce the 71 ms frame by eliminating VRAM demotion, reducing per-frame upload churn, and adding enough markers and memory telemetry to prove whether the remaining cost is geometry, shading, synchronization, or residency.

This plan combines two evidence sources:
- Nsight Graphics GPU Trace from 2026-06-10 showing a 71.18 ms frame.
- Runtime `RendererDiagnostics` output for `NewSponza_Main_glTF_003` with 334 visible objects, 68,214 visible meshlets, 75 textures, and 1,187,616 uploaded bytes in the sampled frame.

## Observed Evidence

Nsight evidence:
- Frame duration is 71.18 ms.
- The selected Vulkan command buffer lasts 71.05 ms.
- Two `vkCmdDrawMeshTasksEXT` regions dominate the frame:
  - First mesh task draw: 26.90 ms.
  - Second mesh task draw: 41.64 ms.
- Nsight reports 1.49 GB of VRAM demoted to system memory.
- Top-level throughput shows PCIe as the dominant bandwidth path:
  - PCIe throughput: 44.4%.
  - L2 throughput: 12.4%.
  - SM throughput: 3.3%.
  - VRAM throughput: 0.8%.
- Input dependency attribution is dominated by global memory loads:
  - Global memory load dependency: 95.24%.
- Nsight reports no perf markers, so pass attribution is currently inferred rather than explicit.

Renderer diagnostics evidence:
- Scene size is moderate by object count but large by meshlet count:
  - visibleObjects=334.
  - visibleMeshlets=68,214.
  - opaqueMeshlets=67,974.
  - transparentMeshlets=240.
- Texture pressure is meaningful for a sample scene:
  - textures=75.
  - loadedFileTextures=72.
  - modelTextures=72.
  - mipFallbacks=0.
- CPU path is not the primary explanation for 71 ms GPU frame time, but it is not free:
  - totalDrawUs=17,250.
  - sceneBuildUs=7,212.
  - meshletCullUs=6,183.
  - hizRecordUs=8,588.
- GPU timing diagnostics are currently unusable:
  - depthUs=0.
  - hizUs=0.
  - lightCullUs=0.
  - forwardUs=0.
  - transparentUs=0.
- Occlusion diagnostics indicate the occlusion path is enabled but not producing useful measured results:
  - depthPrePass=1.
  - hiz=1.
  - occlusion=1.
  - occlusionTested=0.
  - occlusionCulled=0.
  - depthEmitted=0.
  - forwardEmitted=0.
- Per-frame upload volume is significant and appears tied mostly to meshlet draw payloads:
  - uploadedBytes=1,187,616.
  - meshletDrawBytes=1,087,584.
  - objectBytes=48,096.
  - instanceBytes=48,096.
  - transparentMeshletDrawBytes=3,840.
  - uploads=4.
  - uploadSkipped=0.
- The frame submits one task per opaque meshlet for both major mesh task phases:
  - depthTasks=67,974.
  - forwardTasks=67,974.
- Meshlet quality is mixed:
  - avgTris=45.7.
  - avgVerts=47.6.
  - under32Tris=36,472, which is about 44.5% of total meshlets.

## Working Diagnosis

Primary diagnosis: the 71 ms frame is caused first by VRAM residency failure, not by raw ALU or raster throughput. Nsight explicitly reports 1.49 GB demoted to system memory, PCIe throughput dominates, and shader progress is blocked by global memory loads. The mesh task draws then become extremely expensive because they read scene, meshlet, material, vertex, index, and texture data through a memory path that is behaving like PCIe-backed paging rather than local VRAM.

Secondary diagnosis: the renderer is doubling the pain by launching about 68k opaque meshlet tasks in both depth and forward phases. If the depth prepass and Hi-Z path are not actually rejecting forward meshlets, the renderer pays an additional 26.90 ms pass before the 41.64 ms forward pass. The current diagnostics say occlusion is enabled, but `occlusionTested`, `occlusionCulled`, `depthEmitted`, and `forwardEmitted` are all zero, so the implementation cannot prove that Hi-Z is saving work.

Tertiary diagnosis: per-frame upload churn is likely too high for the current architecture. Uploading about 1.19 MB every frame is not enough by itself to explain 71 ms, but it is a symptom that scene payloads are rebuilt and copied frequently. The `Buffer created` log lines also suggest some buffers may be created during runtime paths. Buffer creation and residency churn should be treated as suspect until proven otherwise.

## Priority Order

1. Stop VRAM demotion.
2. Add memory budget and allocation telemetry so residency regressions are visible immediately.
3. Add NSight markers and real GPU timestamps so pass costs are attributable.
4. Remove avoidable texture residency pressure.
5. Remove avoidable per-frame upload and buffer creation churn.
6. Validate whether depth prepass and Hi-Z occlusion pay for themselves.
7. Reduce meshlet task count or improve meshlet batching only after memory residency is under control.

## Phase 1: Memory Budget Telemetry

Goal: make VRAM pressure visible in normal diagnostics before using NSight.

Implementation steps:
- Enable and query `VK_EXT_memory_budget` during Vulkan device setup if available.
- Log per-heap values once per second or when they change materially:
  - heap budget.
  - heap usage.
  - heap size.
  - memory property flags.
  - whether the heap is device-local.
- Add renderer-side allocation counters grouped by resource class:
  - textures.
  - vertex buffers.
  - index buffers.
  - meshlet buffers.
  - material buffers.
  - scene/object/instance buffers.
  - staging buffers.
  - transient render targets.
  - Hi-Z images.
- Add `RendererDiagnostics` fields or a sibling diagnostics record for:
  - deviceLocalBudgetBytes.
  - deviceLocalUsageBytes.
  - deviceLocalAvailableBytes.
  - totalTextureBytes.
  - totalBufferBytes.
  - transientBytes.
  - stagingBytes.
- Treat usage above about 85% of budget as a warning and above 95% as a hard performance fault.

Validation gate:
- A normal run prints the local VRAM budget and usage.
- The diagnostics can explain whether the 1.49 GB demotion happened because allocated memory exceeded budget.
- The log identifies which resource class is consuming the most memory.

## Phase 2: Texture Memory Reduction

Goal: reduce loaded texture memory first, because Nsight points to residency failure and the sample loads 72 file textures.

Implementation steps:
- Print texture dimensions, format, mip count, byte size estimate, and material usage for every loaded file texture.
- Sort texture logs by estimated byte size so the largest offenders are obvious.
- Verify that each of the 72 model textures is actually referenced by a currently used material slot.
- Skip loading textures for material slots not consumed by the current shaders or disabled features.
- Add a texture residency summary:
  - loaded textures.
  - referenced textures.
  - unreferenced textures.
  - default substitutions.
  - total estimated texture bytes.
- Add a temporary quality cap for diagnosis:
  - max base dimension 2048 or 1024.
  - keep this as a runtime/debug setting, not a permanent silent asset mutation.
- Prefer compressed GPU formats where practical:
  - BC1/BC3/BC5/BC7 for desktop targets.
  - Keep normal maps in a format appropriate for normals.
- Keep default fallback textures shared globally and ensure duplicated defaults do not create multiple GPU images.

Validation gate:
- Texture memory total decreases materially.
- Nsight no longer reports VRAM demotion for the same camera and scene.
- Frame time improves before any meshlet algorithm changes.

## Phase 3: Mipmaps and Sampling

Goal: ensure texture sampling does not force high-resolution memory traffic at distance or cause cache-hostile access patterns.

Current evidence:
- `mipFallbacks=0`, which means the loader reports mips are present or generated.
- This still needs verification because correct mip count does not prove correct upload, image layout, sampler minification, or material binding.

Implementation steps:
- For every loaded texture, log:
  - width.
  - height.
  - format.
  - mip levels.
  - whether mip data came from the file or was generated.
- Validate Vulkan image creation uses the full mip level count.
- Validate all mip levels are uploaded or generated before shader use.
- Validate samplers use mip filtering rather than forcing base level sampling.
- Add a debug view or one-frame log that confirms min/max LOD is not clamped to mip 0.
- Use anisotropy conservatively during diagnosis. Test with anisotropy disabled or capped to 4x to isolate texture bandwidth.

Validation gate:
- Captured textures in NSight show valid mip chains.
- Sampler state allows minification mips.
- Reducing max texture dimension or anisotropy produces predictable bandwidth and frame-time changes.

## Phase 4: Avoid Loading Unused Material Textures

Goal: keep only textures that the active renderer path can sample.

Implementation steps:
- Build a material texture usage table from the active shader interface:
  - base color.
  - normal.
  - metallic/roughness or packed PBR texture.
  - emissive only if used.
  - occlusion only if used.
  - alpha/mask only if material mode requires it.
- In the glTF material import path, distinguish between:
  - texture declared by asset.
  - texture referenced by material.
  - texture required by active renderer.
  - texture actually uploaded to GPU.
- Do not upload unused textures for disabled features.
- Report unused declared textures in diagnostics instead of silently uploading them.
- Confirm defaultWhite/defaultNormal/defaultBlack substitutions are shared and not per-material allocations.

Validation gate:
- `modelTextures=72` can be compared against `textures actually uploaded`.
- Texture memory drops when unused slots exist.
- Visual output remains correct for active material features.

## Phase 5: Upload and Buffer Lifetime Audit

Goal: determine whether the renderer is rebuilding GPU-visible scene buffers every frame and whether any runtime path creates buffers repeatedly.

Current evidence:
- The sample prints `Buffer created` near the frame diagnostics.
- `payloadRebuilt=1`.
- `uploadedBytes=1,187,616`.
- `meshletDrawBytes=1,087,584`.
- `uploads=4` and `uploadSkipped=0`.

Implementation steps:
- Add creation-site labels to all buffer creation logs:
  - resource name.
  - size.
  - usage flags.
  - memory type.
  - call-site or owner class.
- Replace generic `Buffer created` with structured logs that can identify repeated creation.
- Track persistent buffer identity across frames:
  - object buffer handle/id.
  - instance buffer handle/id.
  - meshlet draw buffer handle/id.
  - material buffer handle/id.
- Track whether each buffer was created, resized, or reused this frame.
- Keep per-category upload bytes in diagnostics every frame. Existing values should remain visible:
  - UploadedBytes.
  - ObjectUploadBytes.
  - InstanceUploadBytes.
  - MeshletDrawUploadBytes.
  - TransparentMeshletDrawUploadBytes.
  - MaterialUploadBytes.
  - LightUploadBytes.
- Add high-water marks for buffer capacities so normal camera movement does not reallocate buffers.
- Change per-frame scene payload uploads to reuse persistently allocated buffers where possible.
- Only rebuild and upload meshlet draw payloads when the visible meshlet set or sort/material grouping changes.
- Investigate whether CPU meshlet culling output can be compacted or referenced indirectly instead of uploading one large draw payload every frame.

Validation gate:
- Buffer creation logs disappear from steady-state frames.
- `SceneUploadSkipped` increments when the camera and scene are unchanged.
- `uploadedBytes` drops to near zero for static camera/static scene frames.
- No frame allocates or destroys large GPU buffers in the steady state.

## Phase 6: NSight Perf Markers and GPU Timestamp Queries

Goal: make the next NSight capture directly attributable to renderer passes and make in-engine diagnostics match GPU reality.

Implementation steps:
- Add Vulkan debug labels around command buffer regions using `VK_EXT_debug_utils`:
  - DepthPrePass.
  - HiZBuild.
  - TiledLightCull.
  - ForwardOpaque.
  - TransparentForward.
  - SceneUpload or TransferUpload if represented in the captured command stream.
- Add labels for major resources:
  - scene/object buffer.
  - instance buffer.
  - meshlet draw buffer.
  - material buffer.
  - loaded textures with asset names.
  - Hi-Z image.
- Add timestamp queries around each GPU pass:
  - depth.
  - Hi-Z.
  - light cull.
  - forward opaque.
  - transparent.
- Fix `RendererDiagnostics` GPU timing fields so zeros mean pass did not run, not missing instrumentation.
- If timestamp data is delayed by frames, report the frame index for the timing sample.

Validation gate:
- NSight no longer warns that no perf markers were detected.
- The two long mesh task regions are clearly named as depth and forward, or the assumption is corrected.
- `GpuDepthPrePassMicroseconds`, `GpuHiZBuildMicroseconds`, `GpuForwardOpaqueMicroseconds`, and `GpuTransparentMicroseconds` contain non-zero values when those passes run.

## Phase 7: Depth Prepass and Hi-Z Value Check

Goal: prove whether depth plus Hi-Z reduces forward work enough to justify its cost.

Current concern:
- The diagnostics show depth and Hi-Z enabled, but GPU culling counters are zero:
  - depthEmitted=0.
  - forwardEmitted=0.
  - occlusionTested=0.
  - occlusionCulled=0.
- CPU pass recording says Hi-Z recording costs 8,588 us, which is suspiciously high for command recording and should be investigated.

Implementation steps:
- Add runtime toggles for controlled A/B captures:
  - depth prepass on/off.
  - Hi-Z build on/off.
  - occlusion test on/off.
  - forward-only rendering.
- Fix shader-side or CPU-side counters so they report:
  - depth task invocations.
  - depth emitted meshlets.
  - forward task invocations.
  - forward frustum culled meshlets.
  - forward occlusion tested meshlets.
  - forward occlusion culled meshlets.
  - forward emitted meshlets.
- Measure the same camera with four modes:
  - forward only.
  - depth plus forward, no Hi-Z.
  - depth plus Hi-Z plus forward occlusion.
  - depth plus Hi-Z generation only, no occlusion consume.
- Investigate why Hi-Z command recording is 8.588 ms CPU. It may indicate too many per-mip barriers, command buffer churn, or expensive managed allocations during recording.

Validation gate:
- If Hi-Z does not reduce forward emitted meshlets or forward time enough to cover depth plus Hi-Z cost, disable it for this path until fixed.
- If depth prepass duplicates almost all work without reducing forward shading cost, make it conditional by material/shading cost or scene mode.

## Phase 8: Meshlet Task Count Reduction

Goal: reduce the cost of launching roughly 68k mesh tasks twice per frame after memory residency is fixed.

Current evidence:
- `depthTasks=67,974`.
- `forwardTasks=67,974`.
- `under32Tris=36,472`, about 44.5% of all meshlets.
- Average submitted meshlet size is 45.7 triangles and 47.6 vertices.

Implementation steps:
- Compare meshlet generation settings against the mesh shader workgroup design.
- Test larger meshlet targets for static environment meshes, especially Sponza-like architecture.
- Consider grouping several small meshlets per task where shader and data layout allow it.
- Add per-mesh or per-material histograms for small meshlets to identify the worst assets.
- Avoid optimizing meshlet size until VRAM demotion is fixed, because PCIe paging can hide the real cost curve.

Validation gate:
- Meshlet count decreases without unacceptable culling precision loss.
- Depth and forward task durations decrease in NSight after residency is healthy.
- Visual correctness and culling stability remain intact.

## Phase 9: Acceptance Criteria

The issue is considered fixed when all of these are true for the same `NewSponza_Main_glTF_003` camera that produced the 71.18 ms frame:
- NSight reports no VRAM demotion to system memory.
- PCIe throughput is no longer the dominant top-level throughput source during steady-state rendering.
- In-engine diagnostics report real non-zero GPU pass timings.
- NSight pass markers identify depth, Hi-Z, light cull, forward opaque, and transparent regions.
- Steady-state frames do not log buffer creation.
- Static scene/static camera frames skip avoidable scene uploads.
- Texture memory is reported by total and by largest resources.
- The renderer can explain whether depth prepass and Hi-Z are enabled because they save time, not merely because they exist.

## First Implementation Slice

Do these first, in this order:

1. Add Vulkan memory budget logging and renderer allocation totals.
2. Add texture memory inventory logging with dimensions, format, mips, usage, and estimated bytes.
3. Replace generic `Buffer created` logs with labeled creation, resize, and reuse logs.
4. Add NSight debug labels for depth, Hi-Z, light cull, forward opaque, and transparent passes.
5. Add timestamp queries or fix the existing GPU timing path so `RendererDiagnostics` stops reporting zero for active passes.
6. Capture the same camera again and compare against the original 71.18 ms NSight frame.

Expected result of the first slice:
- The next capture should answer whether the largest win is texture residency, buffer lifetime/upload churn, or ineffective depth/Hi-Z work. Do not start a large meshlet rewrite until that answer is available.
