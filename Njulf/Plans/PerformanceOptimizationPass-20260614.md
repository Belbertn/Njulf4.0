# Performance Optimization Pass - 2026-06-14

## Scope And Research Basis

This pass focused on VRAM, upload/processing time, CPU frame time, and GPU frame time in the renderer, asset import path, and runtime resource managers. I reviewed the local Vulkan renderer and cross-checked the recommendations against common Vulkan/GPU guidance:

- NVIDIA Vulkan Dos and Don'ts recommends parallelizing command buffer recording, image/buffer creation, descriptor updates, pipeline creation, and memory allocation/binding; it also calls out queue-submit cost, avoiding waits, and using pipeline caches. <https://developer.nvidia.com/blog/vulkan-dos-donts/>
- Khronos Vulkan Samples show secondary command buffers recorded concurrently, per-thread pools, command-pool reset strategies, and the tradeoff between too many tiny secondary buffers and CPU/GPU overhead. <https://docs.vulkan.org/samples/latest/samples/performance/command_buffer_usage/README.html>
- Vulkan Memory Allocator recommends `VK_EXT_memory_budget` and `vmaGetHeapBudgets()` for current usage and budget queries, with `vmaSetCurrentFrameIndex()` used each frame. <https://gpuopen-librariesandsdks.github.io/VulkanMemoryAllocator/html/staying_within_budget.html>
- Khronos Vulkan Samples describe framebuffer/image compression as a way to reduce memory footprint and bandwidth where supported. <https://docs.vulkan.org/samples/latest/samples/performance/image_compression_control/README.html>
- Khronos Vulkan Samples and glTF `KHR_texture_basisu` document KTX2/Basis workflows for transcoding to GPU-native compressed formats and reducing GPU memory footprint. <https://docs.vulkan.org/samples/latest/samples/performance/texture_compression_basisu/README.html> and <https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Khronos/KHR_texture_basisu/README.md>
- Khronos Vulkan Guide notes that pipeline caches can be saved and reused between runs to avoid repeated costly pipeline creation work. <https://docs.vulkan.org/guide/latest/pipeline_cache.html>

## Optimizations With No Fidelity Impact

These items should not change intended visual output. Some still need image-diff validation because precision, barriers, and aliasing changes can expose bugs.

### 1. Use real GPU memory budgets instead of only static renderer budgets

Current code tracks estimated categories and compares them to static profiles in `Njulf.Rendering/Diagnostics/RenderBudgetProfile.cs` and `Njulf.Rendering/VulkanRenderer.cs:1861`, but I did not find VMA heap budget integration. VMA supports frame-updated budget queries and can use `VK_EXT_memory_budget`.

Recommended work:

- Enable `VK_EXT_memory_budget` and `VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT` when available.
- Call VMA's current-frame update once per frame and expose `vmaGetHeapBudgets()` in diagnostics.
- Add budget-aware allocation policy: noncritical resources such as optional texture mips, reflection probes, local shadow images, and debug buffers should fail/degrade before render targets or core scene buffers.
- Keep the existing estimated category breakdown, but show actual heap usage/residency beside it. This will make VRAM regressions visible and prevent hidden over-budget paging stalls.

Expected impact: better VRAM residency control and fewer hard-to-diagnose stalls when the driver spills resources to system memory.

### 2. Tailor render target image usage flags and allow driver compression

`RenderTarget` currently creates every target with color attachment, sampled, transfer source, transfer destination, and any extra usage (`Njulf.Rendering/Resources/RenderTarget.cs:58`). Many targets do not need all of these flags all the time. Overbroad usage can prevent optimal layouts/compression paths or force unnecessary layout transitions.

Recommended work:

- Replace the generic `RenderTarget` constructor with a descriptor that explicitly states whether the image needs color attachment, sampled, storage, transfer source, and transfer destination usage.
- Keep transfer flags only for targets that are actually copied or blitted. Today AO blur and bloom scratch copies need transfer, but most long-lived post targets should not.
- Where supported, test `VK_EXT_image_compression_control` for framebuffer attachments. The most relevant targets are full-resolution color/fog/LDR history images, because they are written then sampled later.

Expected impact: less bandwidth on compatible GPUs, fewer unnecessary barriers, and a cleaner basis for render-target aliasing.

### 3. Add render-target aliasing for disjoint transient targets

`RenderTargetManager` allocates all major targets up front: scene color, fogged scene color, AO raw/blurred/scratch, LDR, SMAA edges/blend, TAA history A/B, and both bloom mip chains (`Njulf.Rendering/Resources/RenderTargetManager.cs:31`, `Njulf.Rendering/Resources/RenderTargetManager.cs:36`, `Njulf.Rendering/Resources/RenderTargetManager.cs:39`, `Njulf.Rendering/Resources/RenderTargetManager.cs:159`). Disabled features get placeholders, which is good, but enabled transient lifetimes still never alias.

Recommended work:

- Introduce a simple render-resource allocator with declared pass lifetimes. Start with manual alias groups before a full graph compiler.
- Good first alias candidates:
  - AO scratch after AO blur.
  - Bloom scratch images after bloom is rewritten to avoid persistent scratch, or while that work is pending.
  - SMAA edges/blend when SMAA is disabled or after the final neighborhood pass.
  - Fogged scene color when fog is disabled, already handled by active-scene selection but still represented as a target.
- Keep TAA history out of aliasing because it persists between frames.

Expected impact: lower transient VRAM. At 4K, full-resolution HDR targets are tens of MiB each, and even half-resolution chains add up quickly.

### 4. Remove the bloom scratch mip chain and copy-back pass

Bloom allocates both `_bloomMipChain` and `_bloomScratchChain` (`Njulf.Rendering/Resources/RenderTargetManager.cs:159`). `BloomPass` writes upsample results to the scratch chain and then copies scratch back to the mip chain (`Njulf.Rendering/Pipeline/BloomPass.cs:112`, `Njulf.Rendering/Pipeline/BloomPass.cs:121`, `Njulf.Rendering/Pipeline/BloomPass.cs:221`, `Njulf.Rendering/Pipeline/BloomPass.cs:253`).

Recommended work:

- Rewrite upsample to ping-pong through one temporary image or write directly to the destination mip when source/destination hazards are valid.
- For the common bloom ladder, process from smallest mip upward and bind previous smaller mip plus current destination with explicit read/write separation.
- Delete or reduce `_bloomScratchChain` once the pass no longer needs copy-back.

Expected impact: saves one bloom chain of VRAM and removes per-mip image copy bandwidth. At 4K, a half-resolution 6-mip HDR16 chain is roughly 16 to 17 MiB, so removing the duplicate chain is meaningful.

### 5. Skip AO work completely when AO is disabled

`AmbientOcclusionPass` computes `enabled = ao.Enabled && sceneData.DepthPrePassEnabled`, but still transitions/writes/dispatches with disabled mode (`Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs:57`, `Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs:111`). `AmbientOcclusionBlurPass` also copies raw to blurred when radius is zero and does not early-out on `sceneData.AmbientOcclusionEnabled` (`Njulf.Rendering/Pipeline/AmbientOcclusionBlurPass.cs:57`, `Njulf.Rendering/Pipeline/AmbientOcclusionBlurPass.cs:59`, `Njulf.Rendering/Pipeline/AmbientOcclusionBlurPass.cs:142`).

Recommended work:

- If AO is disabled, bind/register a neutral 1x1 AO texture and skip AO and AO blur passes entirely.
- If blur radius is zero and raw and blurred AO are equivalent for sampling, skip the copy and sample raw directly, or register blurred to raw for that frame.
- Keep diagnostics explicit: CPU/GPU AO timings should be zero when the pass is bypassed.

Expected impact: lower compute and transfer work in profiles where AO is off or depth prepass is unavailable. No visual change because the shader already treats disabled AO as neutral.

### 6. Make directional shadow image allocation conditional like local shadows

Spot and point shadow images are allocated only when their feature is enabled and capacity is positive (`Njulf.Rendering/Resources/SpotShadowAtlas.cs:73`, `Njulf.Rendering/Resources/PointShadowCubemapArray.cs:74`). Directional shadows allocate a D32 image in the constructor regardless of `DirectionalShadowsEnabled` (`Njulf.Rendering/Resources/DirectionalShadowResources.cs:34`, `Njulf.Rendering/Resources/DirectionalShadowResources.cs:43`).

Recommended work:

- Mirror the local-shadow resource pattern: keep the small data buffer and sampler if needed, but allocate the directional depth array only when directional shadows are enabled.
- On disable, release the image through the fence-based deleter and register fallback/null shadow views.

Expected impact: saves `mapSize * mapSize * cascades * 4` bytes. With the default 2048 map and 2 cascades, that is about 32 MiB.

### 7. Change environment and IBL resources from float32 RGBA to float16 RGBA after validation

`EnvironmentManager` uses `R32G32B32A32Sfloat` for the environment cubemap, irradiance cubemap, prefiltered cubemap, and BRDF LUT (`Njulf.Rendering/Resources/EnvironmentManager.cs:15`, `Njulf.Rendering/Resources/EnvironmentManager.cs:153`, `Njulf.Rendering/Resources/EnvironmentManager.cs:166`, `Njulf.Rendering/Resources/EnvironmentManager.cs:179`, `Njulf.Rendering/Resources/EnvironmentManager.cs:195`). Default sizes are 1024 environment, 64 irradiance, 256 prefiltered, and 256 BRDF LUT (`Njulf.Rendering/Data/RenderSettings.cs:909`, `Njulf.Rendering/Data/RenderSettings.cs:921`).

Recommended work:

- Add `R16G16B16A16Sfloat` as the default environment format and keep `R32G32B32A32Sfloat` as a debug/ultra-precision option.
- Convert generated float arrays to half before upload, or generate half-byte payloads directly.
- Validate with HDR skyboxes, bright emissive scenes, and specular roughness sweeps. Keep this in the no-fidelity bucket only if image-diff and visual tests pass.

Expected impact: roughly halves IBL VRAM. With current defaults, environment-related textures are about 105 MiB at float32 RGBA and about 52 MiB at float16 RGBA.

### 8. Add a native KTX2/Basis and block-compressed texture path

The asset validator currently reports/rejects `KHR_texture_basisu` because KTX2/Basis decode is not implemented (`Njulf.Assets/GltfUnsupportedFeatureValidator.cs:10`, `Njulf.Assets/GltfUnsupportedFeatureValidator.cs:30`, `Njulf.Assets/GltfUnsupportedFeatureValidator.cs:48`). The runtime texture path decodes all loaded images to RGBA8 via StbImageSharp (`Njulf.Rendering/Resources/TextureManager.cs:559`, `Njulf.Rendering/Resources/TextureManager.cs:567`) and calculates only uncompressed byte sizes (`Njulf.Rendering/Resources/TextureManager.cs:1344`, `Njulf.Rendering/Diagnostics/ImageByteEstimator.cs:48`).

Recommended work:

- Support glTF `KHR_texture_basisu` and `image/ktx2` sources.
- Integrate libktx or a BasisU transcoder and select BC7/BC3/BC5/BC4/ETC2/ASTC targets based on device support.
- Upload precomputed mip levels directly instead of decoding to RGBA8 and generating mips by runtime blits.
- Extend `TextureManager.CreateTexture`, staging upload offsets, byte estimators, diagnostics, and cache keys for block-compressed formats.
- Classify this as no-fidelity when the asset already provides KTX2 as the chosen authored texture. Recompressing existing PNG/JPEG assets is listed in the fidelity-impact section.

Expected impact: large texture VRAM and bandwidth reduction, faster uploads for assets that already ship GPU-native mip payloads, and support for modern glTF texture packages.

### 9. Batch uploads and stop waiting synchronously for each texture/mesh upload

`TextureManager.UploadTextureData` creates a staging buffer and uses `BeginSingleTimeCommands()` for each upload (`Njulf.Rendering/Resources/TextureManager.cs:693`, `Njulf.Rendering/Resources/TextureManager.cs:704`, `Njulf.Rendering/Resources/TextureManager.cs:743`, `Njulf.Rendering/Resources/TextureManager.cs:754`). `CommandBufferManager.EndSingleTimeCommands` waits on a fence before returning. Mesh registration also submits upload work and waits through `EndUploadCommands` (`Njulf.Rendering/Resources/MeshManager.cs:422`, `Njulf.Rendering/Resources/MeshManager.cs:928`).

Recommended work:

- Route texture uploads through `StagingRing` plus the existing frame command buffer or a batched transfer command buffer.
- Use the dedicated transfer queue when available and synchronize with timeline semaphores or per-frame fences instead of immediate waits.
- Batch all textures for a model into one upload command and one queue submission.
- Retire staging allocations through the fence-based deleter after GPU completion.

Expected impact: much lower load/import time, less CPU idle time, fewer Vulkan allocation/free operations, and smoother frames when streaming or hot-loading resources.

### 10. Reclaim or pool staging-ring overflow buffers

`StagingRing` has per-frame buffers, but oversized allocations go into `_largeUploadBuffers` and are only released at `Dispose()` (`Njulf.Rendering/Memory/StagingRing.cs:20`, `Njulf.Rendering/Memory/StagingRing.cs:70`, `Njulf.Rendering/Memory/StagingRing.cs:171`, `Njulf.Rendering/Memory/StagingRing.cs:204`). A single large asset upload can leave large host-visible allocations resident for the rest of the renderer lifetime.

Recommended work:

- Track each overflow buffer with the frame/fence that last used it.
- Reuse overflow buffers of compatible size for future large uploads.
- Destroy old overflow buffers after N frames of no use, once their fence has passed.
- Add diagnostics for current ring bytes, active overflow bytes, and peak overflow bytes.

Expected impact: less persistent host-visible memory and fewer long-tail spikes after asset import.

### 11. Actually parallelize secondary command buffer recording

`UseSecondaryCommandBuffers` defaults to true (`Njulf.Rendering/Data/RenderSettings.cs:1419`), and several passes support secondary command buffers. However, `RenderGraph.Execute` records each secondary pass immediately and serially, then executes it into the primary (`Njulf.Rendering/Pipeline/RenderGraph.cs:47`, `Njulf.Rendering/Pipeline/RenderGraph.cs:71`, `Njulf.Rendering/Pipeline/RenderGraph.cs:80`).

Recommended work:

- Use a worker scheduler to record independent secondary command buffers concurrently.
- Start with draw-heavy passes: depth prepass, shadow passes, forward opaque, transparent, and particles.
- Split large draw lists within a pass into a small number of balanced secondary buffers. Do not make one secondary command buffer per tiny dispatch/draw group.
- Give each worker thread its own command pool and descriptor scratch/cache as recommended by the Vulkan Samples guidance.

Expected impact: lower CPU frame time in object-heavy scenes. The current implementation pays some secondary-command overhead without getting the main CPU benefit.

### 12. Persist pipeline caches and build pipelines asynchronously

Many passes create their own empty pipeline cache at startup, such as bloom, AO, HiZ, AA, mesh, skybox, particles, and composite (`Njulf.Rendering/Pipeline/BloomPass.cs:296`, `Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs:192`, `Njulf.Rendering/Pipeline/HiZBuildPass.cs:178`, `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs:78`). The caches are not serialized, shared, or warmed in parallel.

Recommended work:

- Add a renderer-wide pipeline cache service that loads cache blobs keyed by GPU vendor/device/driver/shader hash.
- Pass a shared cache into every graphics/compute pipeline creation.
- Serialize updated cache data on clean shutdown.
- Compile noncritical pipelines asynchronously during level load or renderer initialization. Keep swapchain-dependent recreation minimal.

Expected impact: faster startup, less hitching after shader/settings changes, and more useful pipeline cache behavior across runs.

### 13. Replace O(N) scene signature hashing with dirty revisions

`SceneDataBuilder` computes `SceneCullingSignature` each build and hashes every render object plus every static instance matrix (`Njulf.Rendering/Data/SceneDataBuilder.cs:323`, `Njulf.Rendering/Data/SceneDataBuilder.cs:2181`, `Njulf.Rendering/Data/SceneDataBuilder.cs:2239`). This can be expensive in large static scenes even when no content changed.

Recommended work:

- Add scene/render-object/static-batch revision counters that increment on transform, visibility, mesh, material, skinning, or instance-list changes.
- Hash compact revisions and camera/shadow state instead of every matrix.
- Keep a debug validation mode that occasionally compares the revision signature to the full matrix hash to catch missed invalidations.

Expected impact: lower CPU frame time in large scenes with many static instances and stable content.

### 14. Avoid full tiled-light index-buffer clears

Tiled light culling uses 16x16 tiles and 128 light indices per tile (`Njulf.Rendering/Data/SceneDataBuilder.cs:30`, `Njulf.Rendering/Data/SceneDataBuilder.cs:31`). `GPULightIndex` is 16 bytes (`Njulf.Rendering/Data/GPUStructs.cs:280`), and `ClearTiledLightBuffers` clears the full header and full index buffer for every tiled-light frame (`Njulf.Rendering/Data/SceneDataBuilder.cs:1622`, `Njulf.Rendering/Data/SceneDataBuilder.cs:1625`).

Recommended work:

- Clear only tile headers/counters if the shader only reads indices up to `LightCount`.
- Have the compute culling shader write/overwrite the contiguous valid index range for each tile.
- If a full index clear is still needed for debug safety, gate it behind validation mode.

Expected impact: at 1080p, the index clear is about 16 MiB per frame; at 4K it is about 66 MiB per frame. Removing it should reduce bandwidth without changing lighting.

### 15. Cache reflection probe metadata and avoid per-frame sorting/upload when unchanged

`ReflectionProbeManager.Upload` rebuilds and uploads probe metadata every call (`Njulf.Rendering/Resources/ReflectionProbeManager.cs:73`). `ReflectionProbeData.BuildProbes` sorts authored probes (`Njulf.Rendering/Data/ReflectionProbeData.cs:51`, `Njulf.Rendering/Data/ReflectionProbeData.cs:73`). The manager already has scratch storage, so this is mostly a missing dirty/revision check.

Recommended work:

- Add a reflection probe settings/content revision.
- Skip `BuildProbes` and buffer upload when authored probes and reflection settings have not changed.
- Reuse the previous active probe count and metrics until dirty.

Expected impact: lower CPU work and upload bandwidth in static scenes.

### 16. Make mesh buffer reservations profile-aware and actively compact after import

`MeshManager` starts with 16 MiB vertex, 16 MiB index, 1 MiB metadata, 4 MiB meshlet, 4 MiB meshlet vertex-index, 4 MiB meshlet triangle-index, and 1 MiB skinning buffers (`Njulf.Rendering/Resources/MeshManager.cs:57`). Compaction helpers exist (`Njulf.Rendering/Resources/MeshManager.cs:712`, `Njulf.Rendering/Resources/MeshManager.cs:723`, `Njulf.Rendering/Resources/MeshManager.cs:1551`) but default startup still reserves about 46 MiB before any scene-specific need.

Recommended work:

- Choose initial mesh-buffer sizes from the active render budget profile or from project asset manifest estimates.
- Run compaction after model import batches and after scene loads once transient registration work is done.
- Add diagnostics for buffer capacity, used bytes, waste bytes, and compaction count per mesh buffer.

Expected impact: lower baseline VRAM, especially in small scenes and tools that load only a few meshes.

### 17. Cache or prebuild meshlet data outside the runtime hot path

`MeshManager.RegisterMeshes` builds meshlets during registration (`Njulf.Rendering/Resources/MeshManager.cs:239`). The local meshlet builder is greedy and allocation-heavy by design, which is acceptable for import but not ideal for runtime loading.

Recommended work:

- Serialize meshlet data, LOD metadata, and optimized index/vertex order into an asset cache keyed by source mesh hash and meshlet settings.
- Consider using meshoptimizer in the asset pipeline for vertex cache optimization, overdraw optimization, meshlet building, and remapping.
- At runtime, prefer loading cached GPU-ready payloads and only rebuild when the source asset or meshlet settings change.

Expected impact: faster model loading and less CPU allocation pressure. No runtime fidelity impact because generated meshlets encode the same source geometry.

### 18. Reduce hot-path CPU allocations in diagnostics and scene analysis

`CountAnimationSceneStats` allocates a `HashSet<object>` each scene build (`Njulf.Rendering/Data/SceneDataBuilder.cs:443`, `Njulf.Rendering/Data/SceneDataBuilder.cs:1778`). Material snapshots also allocate when dirty, which is fine for edits but should not occur accidentally every frame.

Recommended work:

- Move reusable sets/lists into builder scratch storage and clear them each frame.
- Make diagnostics opt-in for expensive object graph scans, or update counts from scene revision events.
- Audit LINQ/array snapshots in renderer hot paths and keep them off steady-state frames.

Expected impact: reduced GC pressure and more stable CPU frame times.

## Optimizations With Fidelity Impact

These trade visual quality, precision, or content density for VRAM and frame-time wins. They should be exposed as settings/profiles and validated with screenshots/captures.

### 1. Compress existing PNG/JPEG material textures and channel-pack data maps

The no-fidelity section recommends supporting already-authored KTX2/Basis textures. A separate, fidelity-impacting step is to convert current PNG/JPEG assets into GPU block-compressed textures.

Recommended policy:

- Base color: BC7 sRGB when available; BC3/BC1 only where acceptable.
- Normal maps: BC5 or high-quality UASTC/ASTC. Validate tangent-space artifacts.
- Metallic/roughness/occlusion: pack channels into BC1/BC7/BC4/BC5-compatible layouts.
- Alpha cutouts/transparency: use BC7/BC3 or keep uncompressed where artifacts are unacceptable.

Expected impact: often the largest VRAM and texture bandwidth reduction in content-heavy scenes. Fidelity risk is compression artifacts, especially on normals, gradients, masks, and alpha edges.

### 2. Stream or drop high texture mips based on camera distance and budget

Texture loading has a max imported texture dimension and texture budget profile (`Njulf.Rendering/Resources/TextureManager.cs:121`, `Njulf.Rendering/ServiceCollectionExtensions.cs:82`, `Njulf.Rendering/ServiceCollectionExtensions.cs:88`), but it is still a static import-time cap rather than runtime residency.

Recommended work:

- Keep only needed mip levels resident per texture based on distance, projected screen size, material priority, and actual VMA heap budget.
- Add a per-profile texture-pool budget and evict lower mips first.
- Prefer KTX2 files with full mip pyramids so streaming does not require runtime mip generation.

Expected impact: large VRAM savings in large scenes. Fidelity risk is texture softness or visible mip pop without hysteresis/fading.

### 3. Use dynamic resolution more aggressively

Dynamic resolution exists (`Njulf.Rendering/Data/RenderSettings.cs:532`) and overall resolution scale is profile-controlled (`Njulf.Rendering/Data/RenderSettings.cs:1391`). Lowering the internal render scale reduces most full-screen pass cost and render target memory.

Recommended work:

- Enable dynamic resolution for performance profiles by default.
- Use GPU frame time, not CPU frame time, as the primary feedback signal.
- Add hysteresis and a minimum dwell time to prevent visible oscillation.

Expected impact: broad GPU frame-time and render-target VRAM reduction. Fidelity risk is softer image reconstruction and temporal shimmer.

### 4. Lower AO cost by profile

AO has resolution scale, sample count, and blur settings (`Njulf.Rendering/Data/RenderSettings.cs:1093`, `Njulf.Rendering/Data/RenderSettings.cs:1123`, `Njulf.Rendering/Data/RenderSettings.cs:1129`). High profiles can keep current defaults; low/mid profiles can trade quality.

Recommended work:

- Low: quarter or half-res AO, 4 to 8 samples, smaller radius.
- Mid: half-res AO, 8 to 16 samples.
- High: current or full-res AO, 16 to 32 samples.

Expected impact: lower compute cost and AO target bandwidth. Fidelity risk is noisier AO, haloing, or less contact shadowing.

### 5. Reduce shadow map resolution, cascades, local shadow capacity, or depth precision

Defaults are directional shadows on, 2048 map, 2 cascades (`Njulf.Rendering/Data/RenderSettings.cs:30`, `Njulf.Rendering/Data/RenderSettings.cs:59`, `Njulf.Rendering/Data/RenderSettings.cs:65`). All shadow image formats are D32 (`Njulf.Rendering/Resources/DirectionalShadowResources.cs:34`, `Njulf.Rendering/Resources/SpotShadowAtlas.cs:34`, `Njulf.Rendering/Resources/PointShadowCubemapArray.cs:34`).

Recommended knobs:

- Directional map: 2048 -> 1024 for low/mid profiles.
- Cascades: 2 -> 1 for low profiles; 4 only for high/ultra.
- Spot atlas/tile: reduce atlas or tile size before reducing all lighting.
- Point shadows: lower capacity or map size first, because cubemap arrays multiply by 6 faces.
- D32 -> D16 or D24 where supported and visually acceptable.
- PCF radius: lower first for cost, raise only for close-up quality.

Expected impact: direct VRAM and shadow pass time reduction. Fidelity risk is aliasing, acne/peter-panning changes, weaker contact detail, or shorter shadow distance.

### 6. Lower environment and reflection quality settings

Environment defaults are 1024 environment, 64 irradiance, 256 prefiltered, and 256 BRDF LUT (`Njulf.Rendering/Data/RenderSettings.cs:909`, `Njulf.Rendering/Data/RenderSettings.cs:921`). Reflection settings expose max probes, probes per pixel, and probe resolution (`Njulf.Rendering/Data/RenderSettings.cs:986`, `Njulf.Rendering/Data/RenderSettings.cs:992`, `Njulf.Rendering/Data/RenderSettings.cs:998`).

Recommended knobs:

- Environment: 1024 -> 512 for low/mid, keep 1024 only for high/ultra or high-frequency HDRIs.
- Prefiltered environment: 256 -> 128 where glossy reflection sharpness is less important.
- Reflection probes: lower `MaxProbes`, `MaxProbesPerPixel`, and `ProbeResolution` by profile.
- Disable probe captures or use only the environment fallback on low profiles.

Expected impact: lower VRAM and reflection sampling cost. Fidelity risk is softer/specularly inaccurate reflections and less localized lighting.

### 7. Use narrower formats for post-tonemap LDR and history buffers

`LdrSceneColor`, TAA history A/B, and some AA intermediates are allocated from `LdrSceneColorFormat`, currently `R16G16B16A16Sfloat` (`Njulf.Rendering/Resources/RenderTargetManager.cs:15`, `Njulf.Rendering/Resources/RenderTargetManager.cs:39`, `Njulf.Rendering/Resources/RenderTargetManager.cs:43`). If these are strictly post-tonemap/LDR, this is likely more precision than needed.

Recommended options:

- `R8G8B8A8Unorm/Srgb` for lowest memory.
- `B10G11R11UfloatPack32` or `A2B10G10R10UnormPack32` where alpha requirements allow.
- Keep HDR16 only for paths that genuinely need HDR post data before tone mapping.

Expected impact: halves or quarters LDR/TAA history VRAM and bandwidth. Fidelity risk is banding, TAA history precision loss, and slightly different post-AA output.

### 8. Reduce HiZ precision or skip HiZ more aggressively

HiZ uses a depth pyramid and is adaptively suppressed when occlusion benefit is low. Reducing the pyramid format or skipping more often can reduce bandwidth, but bad precision/conservatism can create culling artifacts.

Recommended options:

- Test `R16Sfloat` or `R16Unorm` for the HiZ pyramid instead of `R32Sfloat`.
- Increase the suppression threshold for scenes where occlusion culling has low payoff.
- Keep conservative max-depth/min-depth semantics exact and add validation to prevent false occlusion.

Expected impact: lower HiZ VRAM and build bandwidth. Fidelity risk is missing geometry if precision or suppression is wrong.

### 9. Pack vertex attributes and skinning data

`GPUVertex` stores position, normal, two UVs, tangent, and color as float-heavy fields (`Njulf.Rendering/Data/GPUStructs.cs:12`). This is simple but bandwidth-heavy.

Recommended options:

- Normals/tangents: signed normalized 10_10_10_2 or snorm16.
- UVs: half floats where texture coordinate range allows.
- Colors: UNorm8.
- Skin weights: UNorm16 or UNorm8 plus normalized decode.
- Positions: quantized local-space formats per mesh where bounds allow.

Expected impact: lower mesh VRAM and vertex/mesh shader bandwidth. Fidelity risk is precision artifacts, UV seams, normal errors, and skinning jitter on poorly scaled assets.

### 10. Make mesh LOD and meshlet LOD more aggressive

The renderer already has meshlet LOD concepts in `MeshManager` and scene selection. More aggressive thresholds reduce draw/meshlet count but change geometry.

Recommended knobs:

- Increase LOD switch distances for low/mid profiles.
- Bias toward coarser meshlets for small projected objects.
- Add hysteresis to avoid popping.
- Consider impostors or simplified meshes for far repeated instances.

Expected impact: lower draw payload, mesh shader work, and shadow work. Fidelity risk is geometry popping, silhouette loss, and reduced close-range detail if thresholds are too aggressive.

### 11. Reduce transparency, particles, fog, and bloom quality by profile

The frame graph includes transparent forward, particles, fog, bloom, auto exposure, tone map, and AA. These are content and presentation features, so reducing them is a quality tradeoff.

Recommended knobs:

- Transparency: limit sorted transparent objects or disable per-meshlet sorting in low profiles.
- Particles: cap global live particles and upload bytes per frame.
- Fog: lower internal resolution if implemented, or disable volumetric-looking fog features on low profiles.
- Bloom: fewer mips, lower resolution base, or lower precision format.
- Auto exposure: reduce histogram/update frequency if it causes compute cost.

Expected impact: lower post-processing and overdraw cost. Fidelity risk is visibly simpler effects and different tone/lighting feel.

### 12. Lower anisotropic filtering and sampler quality on constrained profiles

Imported samplers default to high quality behavior, and material textures can be numerous. Reducing anisotropy is a classic bandwidth/texture-cache tradeoff.

Recommended work:

- Expose anisotropy cap by quality profile: 16x high, 8x mid, 4x low, 1x very low.
- Preserve high anisotropy for ground/terrain materials if they dominate the camera view.

Expected impact: lower texture sampling cost in angled-surface scenes. Fidelity risk is blurrier oblique textures.

## Suggested Implementation Order

1. No-fidelity quick wins: skip disabled AO work, conditional directional shadow allocation, staging overflow reclamation, reflection probe dirty checks.
2. High-value memory work: environment float16 validation, bloom scratch removal, render-target usage tailoring, render-target aliasing.
3. Upload/load work: batched async uploads, KTX2/Basis support, compressed-format byte accounting.
4. CPU frame-time work: real parallel secondary command buffer recording, scene dirty revisions, pipeline cache persistence.
5. Fidelity-impact profiles: texture compression policy, dynamic resolution, shadow/AO/reflection quality ladders, vertex packing.

## Measurement Checklist

- Add before/after captures for `RendererDiagnostics` memory categories, actual VMA heap budgets, CPU renderer time, GPU frame time, and per-pass timestamps.
- Capture representative scenes: small static scene, many static instances, many material textures, heavy shadows, heavy transparency/particles, HDR environment/reflections.
- For no-fidelity items, use image-diff captures plus debug views for AO, bloom, shadows, motion vectors, TAA history, and reflection probes.
- For fidelity-impact items, store profile-specific screenshots and document accepted artifacts.
