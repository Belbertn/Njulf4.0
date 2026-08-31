# Quality-Locked GPU Backpressure and Opaque Path Plan

## Goal and invariants

- Remove the measured GPU bottleneck behind `beginFence` stalls; do not treat moving the wait, adding queued frames, or renaming a timing scope as an FPS improvement.
- Retain the quality contract and ABBA acceptance process in [QualityLocked1080p60PerformanceCampaignPlan-20260830.md](QualityLocked1080p60PerformanceCampaignPlan-20260830.md). Do not reduce resolution, samples, rays, probe budgets, material features, shadows, AO, GI, or reflection quality.
- Preserve exact gathers and existing rendering as reference/oracle paths. Keep public rendering APIs unchanged; new diagnostics and ownership flags remain internal and backward-compatible.
- Implement and qualify one independently measurable change at a time. Retain only changes that improve exclusive GPU/CPU time without failing image, temporal, correctness, or stability gates.

## Implementation order

### 1. Establish exclusive evidence and correctness baselines

- Split per-frame and lifetime diagnostics for invalid mappings, cache acceptance, exact fallbacks, directional-cache evaluations, emitted/shaded work, fence owner/submit serial, acquire wait, and resource-recycle wait. Diagnostic counters must be optional and must not add contended production atomics.
- Use compile-time forward variants and identical ABBA captures to isolate compact directional DDGI, exact gather, receiver-cache lookup, direct lighting, and material shading; the current inclusive `ForwardGiGather` scope must not be used as exclusive attribution.
- Record SPIR-V instruction/register counts and, where supported, pipeline statistics for fragment invocations, primitives, early-depth survival, helper lanes, and occupancy. Verify that disabled work is absent from SPIR-V rather than merely hidden behind a uniform branch.
- First audit physical meshlet publication and lifetime: validate page/header/range-ready ordering, frame-slot immutability, fallback readiness, generation ownership, and per-frame invalid counts under traversal, eviction, resize, and frames-in-flight reuse.

### 2. Remove duplicate opaque DDGI ownership

- Add an internal compile-time ownership contract for cache-required opaque hybrid variants: receiver cache owns accepted diffuse/visibility data, the hybrid DDGI-base/composite owns directional indirect specular, and exact gather owns rejected or exceptional pixels.
- For accepted ordinary opaque pixels, do not call compact directional L2 reconstruction; mark that requirement resolved and consume the existing cached diffuse/environment and rough-specular visibility. Ensure the hybrid base remains the sole directional/specular contributor.
- Preserve the existing exact path for cache rejection, bent-normal/Ultra modes, surviving alpha masking and attribution, transparent/thin-glass materials, reflection capture/oracle variants, and every non-hybrid fallback.
- Prove the specialized variants remove coefficient loads and SH evaluation from SPIR-V, then require HDR/FLIP/ROI and temporal equivalence before retention.
- If retained directional users remain hot, reorganize without changing coefficients or equations: separate hot validation/visibility headers from coefficient payloads, use aligned vector loads, and share identical cell decodes within a quad/subgroup or workgroup tile.

### 3. Repair meshlet mapping and reduce compaction bandwidth

- Publish page-table/header data before the ready state with a precise transfer/compute-to-consumer dependency; retire or overwrite mappings only after every referencing submission completes. Include a generation check wherever slot reuse can otherwise alias an old mapping.
- When mappings are immutable for a submitted frame, resolve and validate the physical meshlet address once during compaction and carry it in the emitted payload used by depth, forward, motion, and shadow consumers. Retain the validated lookup path for mutable/diagnostic configurations.
- Deduplicate identical streaming requests per subgroup/workgroup. Accumulate invalid occurrences locally and issue one exact `atomicAdd(N)` per group instead of one contended atomic per failure.
- On proven indirect-only frames, reset only counters and indirect arguments; retain full payload clears for validation/direct fallback. Clear only active shadow cascades and publish only emitted buffers/ranges to their actual consumer stages.

### 4. Narrow synchronization and frame-resource ownership

- Replace global and whole-buffer barriers in receiver cache, DDGI scheduling/trace/blend, scene upload, and opaque compaction with exact buffer ranges, producer/consumer stages, and access masks. Publish each unchanged DDGI indirect-command arena once before its six-dispatch batch.
- Decouple command-buffer reuse, frame-resource slices, and swapchain-image ownership. Acquire an image, wait only for the prior submission associated with that image, and select any completed command buffer/resource context; if none is safe, wait for the oldest required submission and report that as GPU backpressure.
- Associate readbacks, descriptor/transient pools, per-frame uploads, acquired/render-complete semaphores, and deferred destruction with the submission serial that owns them. Reclaim only after that submission completes; resize/out-of-date paths must drain or transfer ownership explicitly.
- Do not increase frames in flight as the fix. This phase is accepted only for reduced avoidable serialization/CPU variance after GPU throughput improves; a 100 ms GPU frame must still report honest backpressure.

### 5. Parallelize and amortize secondary GPU work

- Replace serial DDGI bucket rescans with a deterministic parallel pipeline: classify each accepted update once, perform stable per-bucket exclusive scans, scatter once to `bucketBase + stableRank`, and emit the six commands from final counts. Preserve admission and byte order exactly.
- Keep policy-heavy DDGI admission unchanged. Trace active probes only; spread ordinary cascade updates across frames with a maximum of two cascades only when temporal quality gates pass, while urgent relight, teleport/reset, and invalid-history work bypass the cap.
- Permit distance/irradiance update overlap only for disjoint writes with no dependency, followed by exact consumer barriers. This Flax-derived scheduling is spike protection, not a fix for captures with zero probe updates.
- Let overlapping adaptive receiver-cache cells emit the authoritative exact feedback they already represent, then compact only missing coarse-lattice cells and the directional tail. Preserve exact lattice samples, attribution, order, and final cache contents.
- Tile GTAO temporal reconstruction with an `8x8` workgroup and `12x12` shared halo of the same depth/view-position/normal inputs; retain every envelope equation, threshold, sample, and temporal decision.

### 6. Remove renderer CPU variance

- Pool `SceneRenderingData` by safe submission/frame ownership, clear and reuse collections without stale state, and retain an immutable/latest diagnostics snapshot.
- Make animation statistics, point-shadow masks, previous transforms, volumetric sorting, and unchanged uploads revision/dirty driven. Preserve update ordering and force refresh on scene, camera, topology, or relevant settings revisions.
- Profile GC and CPU contention only after GPU backpressure falls; accept changes on renderer CPU p95/allocation improvements with identical uploaded bytes and rendered output.

## Conditional work

- Partition direct-fallback draws by sidedness only when fallback counters show material use; keep optimized depth-equality/back-culling behavior unchanged.
- Implement stable parallel light-cluster top-K only when saturation telemetry proves the serial path material. Add shader families only after SPIR-V/register evidence identifies divergence or occupancy loss.
- If the specialized opaque path remains over budget, use the broader Flax ownership model as a separately approved architecture phase: opaque materials write a compact G-buffer, diffuse DDGI and reflections run as screen-space consumers, and forward remains for transparent/special materials. Do not begin this migration without measured residual cost and a material-feature compatibility matrix.

## Qualification

- Add contract tests for mapping generations/publication, resolved-address payloads, reset modes, active cascades, stable DDGI order, pooled-state clearing, and submission/image ownership. Shader tests must compile every protected variant and assert ownership macros and forbidden duplicate work.
- Stress traversal/eviction, missing pages, two- and three-image swapchains, resize/out-of-date/suboptimal acquisition, minimized windows, device loss, camera cuts, DDGI reset/urgent relight, alpha/transparent materials, and validation/direct fallback.
- For each retained step, run fixed-camera and traversal ABBA performance captures plus converged HDR, FLIP, ROI, temporal-repeatability, and supplied-screenshot review. Compare both total frame time and exclusive target-pass time; reject wait displacement, counter removal, or increased queue latency presented as a speedup.
- Finish with sustained 1080p thermal runs and report p50/p95/p99 GPU frame, opaque pass, renderer CPU, acquire/recycle/fence waits, allocations, memory headroom, image metrics, and any remaining conditional bottlenecks.

## Flax patterns intentionally adopted

- Explicit separation of opaque surface production, diffuse DDGI, and reflection ownership: [Renderer.cpp](https://github.com/FlaxEngine/FlaxEngine/blob/master/Source/Engine/Renderer/Renderer.cpp) and [DynamicDiffuseGlobalIllumination.cpp](https://github.com/FlaxEngine/FlaxEngine/blob/master/Source/Engine/Renderer/GI/DynamicDiffuseGlobalIllumination.cpp).
- Acquire-image-specific submission tracking and ready-command-buffer reuse: [GPUSwapChainVulkan.cpp](https://github.com/FlaxEngine/FlaxEngine/blob/master/Source/Engine/GraphicsDevice/Vulkan/GPUSwapChainVulkan.cpp) and [CmdBufferVulkan.cpp](https://github.com/FlaxEngine/FlaxEngine/blob/master/Source/Engine/GraphicsDevice/Vulkan/CmdBufferVulkan.cpp).
- Active-only, bounded DDGI update scheduling and safe update overlap. Full deferred conversion is explicitly not assumed necessary.
