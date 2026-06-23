# Async Compute Default Implementation Plan - 2026-06-20

## Goal

Make async compute safe enough to enable by default when the current GPU, queue topology, pass graph, and measured timings show a real benefit. The renderer must always fall back to the existing graphics-queue path when async compute is unsupported, unprofitable, or disabled by user settings.

This plan expands step 9 of `RevisedRendererArchitectureImplementationPlan-20260620.md`. The current step 9 implementation provides async settings, pass eligibility metadata, candidate diagnostics, and potential queue ownership transition diagnostics. It does not yet create a dedicated compute queue or submit graph passes on that queue.

## Non-Goals

- Do not make async compute globally forced.
- Do not move every compute pass to async immediately.
- Do not enable async by default before validation and GPU timing prove benefit.
- Do not require async compute for correctness.
- Do not block future ray tracing work on async compute, but design the scheduler so ray tracing, denoising, SSGI, DDGI probe updates, and path tracing can use it later.

## Default Policy

Async compute can become default only when all of these are true:

1. The physical device exposes a compute-capable queue family that can be used independently from graphics, or the measured same-family queue path still improves frame time.
2. The render graph can emit correct release/acquire ownership transitions for resources crossing graphics and compute queues.
3. Timeline or binary semaphore synchronization is clean under Vulkan validation.
4. Per-pass GPU timing shows actual overlap or lower total frame time for at least one enabled candidate.
5. The renderer can disable async at runtime and return to baseline behavior without changing visual output.
6. Performance snapshots clearly report requested, supported, enabled, candidate, active, timing, and fallback status.

## Phase 1. Lock Current Baseline

Implementation steps:

1. Capture startup smoke with async disabled.
2. Capture startup smoke with `--async-compute` in the current diagnostic-only state.
3. Capture at least one transparent-heavy scene, one foliage-heavy scene, and one post-processing-heavy scene.
4. Export performance snapshots for all captures.
5. Record current values for:
   - `CpuQueueSubmitMicroseconds`
   - `GpuFrameMicroseconds`
   - `GpuHiZBuildMicroseconds`
   - `GpuAmbientOcclusionBlurMicroseconds`
   - `GpuSsgiTraceMicroseconds`
   - `GpuSsgiTemporalMicroseconds`
   - `GpuSsgiDenoiseMicroseconds`
   - `GpuDdgiUpdateMicroseconds`
   - `DdgiProbesUpdated`
   - `DdgiRaysPerProbe`
   - `DdgiAsyncComputeEnabled`
   - `GpuFogMicroseconds`
   - `GpuBloomExtractMicroseconds`
   - `GpuBloomDownsampleMicroseconds`
   - `GpuBloomUpsampleMicroseconds`
   - `GraphPlannedBarrierCount`
   - `AsyncComputeCandidatePassCount`
   - `AsyncComputeQueueOwnershipTransitionCount`

Validation:

- Baseline smoke passes with validation failure enabled.
- Current async diagnostic-only mode does not change barrier count or visual output.
- Baseline artifacts are checked into `Plans/Baselines` or otherwise preserved for comparison.

## Phase 2. Add Compute Queue Discovery

Implementation steps:

1. Extend `VulkanContext` device selection to find:
   - graphics queue family
   - present-capable graphics queue family if needed
   - transfer queue family
   - compute queue family
2. Prefer a compute queue family without `GraphicsBit` when available.
3. Fall back to a graphics-capable compute queue only as `SharedGraphicsCompute`.
4. Expose diagnostics:
   - `ComputeQueueFamilyIndex`
   - `GraphicsQueueFamilyIndex`
   - `HasDedicatedComputeQueue`
   - `HasSharedGraphicsComputeQueue`
   - `AsyncComputeSupportStatus`
5. Create the logical device with the additional compute queue create info when it is a distinct family.
6. Retrieve and store `ComputeQueue`.
7. Add startup-log entries for selected queue families.

Validation:

- Device startup succeeds on GPUs with and without a dedicated compute queue.
- Device requirement reports explain missing compute queue support without failing renderer startup.
- Async remains disabled by default after queue discovery.

## Phase 3. Add Compute Command Infrastructure

Implementation steps:

1. Extend `CommandBufferManager` with per-frame compute command pools.
2. Add primary compute command buffer allocation/reset/begin/end helpers.
3. Add debug names for compute pools and command buffers.
4. Keep graphics command buffers unchanged.
5. Add diagnostics:
   - compute command buffer recorded count
   - compute command record microseconds
   - compute queue submit microseconds
6. Add unit tests for command manager queue family selection where possible.

Validation:

- Build and unit tests pass.
- Startup smoke passes with compute command pools created but unused.
- No validation messages from command pool family mismatch.

## Phase 4. Add Synchronization Primitives

Implementation steps:

1. Add per-frame async compute completion synchronization.
2. Prefer timeline semaphores if already available or practical to add.
3. Otherwise add binary semaphores for:
   - graphics-to-compute readiness
   - compute-to-graphics completion
4. Keep the frame fence on the graphics queue submission path initially.
5. Add synchronization diagnostics:
   - async wait semaphore count
   - async signal semaphore count
   - async submit count
   - async fallback reason
6. Ensure swapchain image acquisition and presentation still only depend on the final graphics work.

Validation:

- Submitting an empty compute command buffer behind the runtime flag produces no validation errors.
- Disabling async removes all compute queue submits.
- Present waits only on the final graphics completion semaphore.

## Phase 5. Extend Graph Planning For Real Queue Ownership

Implementation steps:

1. Split graph queue diagnostics into:
   - potential queue transitions
   - executed queue ownership transitions
2. Track resource owner queue intent per resource during planning.
3. For resources moving graphics-to-compute:
   - emit graphics release barrier
   - submit/wait dependency
   - emit compute acquire barrier
4. For resources moving compute-to-graphics:
   - emit compute release barrier
   - submit/wait dependency
   - emit graphics acquire barrier
5. Keep same-queue resource transitions on the current barrier path.
6. Add a graph validation pass that rejects async scheduling when a resource crosses queues without a supported ownership plan.
7. Make imported resources explicit about queue ownership assumptions.

Validation:

- Validation layers are clean for release/acquire barriers.
- Barrier diagnostics identify release and acquire sides separately.
- Disabled async still uses the previous graphics-only barrier path.

## Phase 6. Add The Scheduler Without Moving Passes

Implementation steps:

1. Add an `AsyncComputeScheduler` or equivalent internal service.
2. Inputs:
   - active production pass list
   - pass resource usages
   - pass async eligibility
   - runtime async settings
   - queue support diagnostics
   - latest per-pass GPU timing
3. Outputs:
   - graphics pass segments
   - compute pass segments
   - semaphore dependencies
   - fallback reason
4. Initially schedule every pass on graphics.
5. Add diagnostics for planned segments and why candidates were not moved.
6. Add tests for:
   - no compute queue
   - async disabled
   - candidate disabled by per-pass setting
   - feature isolation removing a candidate

Validation:

- No behavior change when scheduler is present but returns graphics-only work.
- Performance snapshots include scheduler plan details.

## Phase 7. Move One Low-Risk Pass To Async Behind A Per-Pass Flag

Preferred first candidate: `AmbientOcclusionBlurPass`.

Rationale:

- It is compute-only.
- It works on AO intermediate targets.
- It has narrower dependencies than bloom or fog.
- It is already a pass in the production render graph.

Implementation steps:

1. Add `RenderSettings.AsyncCompute.AmbientOcclusionBlurEnabled` as the only per-pass async flag used by the scheduler.
2. Record AO blur into the compute command buffer when:
   - global async is enabled
   - AO blur async flag is enabled
   - compute queue support is available
   - graph ownership transitions are valid
3. Keep AO generation and forward lighting on graphics.
4. Add graphics-to-compute handoff for `AmbientOcclusionRaw` and `AmbientOcclusionScratch`.
5. Add compute-to-graphics handoff for `AmbientOcclusionBlurred`.
6. Ensure later forward pass waits for AO blur completion.
7. Keep a per-frame fallback if scheduling fails.

Validation:

- Validation layers clean with AO blur async enabled.
- AO debug views match graphics-only path.
- GPU timing shows AO blur moved to compute queue.
- Frame time does not regress beyond an agreed threshold.
- Disabling only AO blur async returns to graphics-only behavior.

## Phase 8. Add Timing-Based Benefit Detection

Implementation steps:

1. Collect rolling timing windows for:
   - graphics-only baseline
   - async-enabled candidate
   - total GPU frame time
   - queue submit CPU overhead
2. Require warmup frames before making decisions.
3. Add a minimum improvement threshold, for example:
   - at least 3 percent GPU frame improvement, or
   - at least 0.25 ms improvement on the active budget profile
4. Add hysteresis to avoid toggling every frame.
5. Store per-pass async status:
   - `DisabledBySettings`
   - `UnsupportedQueue`
   - `PendingWarmup`
   - `NoMeasuredBenefit`
   - `EnabledMeasuredBenefit`
   - `ValidationFallback`
6. Keep user-forced async mode separate from default auto mode.

Validation:

- Timing decisions are stable across a 1000-frame long-run smoke.
- Performance snapshots show enough data to explain why async is enabled or disabled.
- Async can be manually disabled regardless of timing.

## Phase 9. Expand Candidate Set Conservatively

Candidate order:

1. `AmbientOcclusionBlurPass`
2. `DdgiUpdatePass`
3. `HiZBuildPass`
4. `FogPass`
5. `BloomPass`
6. SSGI compute chain: `SsgiTracePass`, `SsgiTemporalPass`, `SsgiDenoisePass`
7. GPU particle reset/simulate/sort

Per-candidate requirements:

- Independent command recording works.
- Queue ownership transitions are validated.
- Output waits are attached to the first true consumer.
- Debug views still work.
- Feature isolation still works.
- GPU timing shows measurable benefit in at least one relevant scene.
- Current-frame dependencies are explicit. Prefer passes whose outputs are not required by immediately following graphics work.

Validation:

- Each candidate has an individual enable flag.
- Each candidate has an isolated smoke or performance scenario.
- Any candidate that regresses frame time remains default-off.

## Phase 10. Add Global Illumination Async Candidates

DDGI should be the first global illumination async target. SSGI should be a later, conditional target because its trace, temporal, denoise, and composite chain is tightly coupled to the current frame.

### DDGI Update

Rationale:

- `DdgiUpdatePass` is compute-only and already has settings gates through `RenderSettings.AsyncCompute.DdgiUpdateEnabled` and `RenderSettings.GlobalIllumination.DdgiAsyncComputeEnabled`.
- Probe updates are latency tolerant when current-frame shading samples the previously completed DDGI state.
- The pass is ray-query-heavy and can overlap with graphics/post work when the GPU has compute and memory headroom.
- The existing diagnostics already expose DDGI update timing, probe update count, rays per probe, and DDGI async enable state.

Implementation steps:

1. Add scheduler support for moving `DdgiUpdatePass` to the compute queue when:
   - global async compute is enabled or auto-selects the pass
   - `AsyncCompute.DdgiUpdateEnabled` is true
   - `GlobalIllumination.DdgiAsyncComputeEnabled` is true
   - DDGI is active
   - ray query and acceleration structure state required by the pass are valid
   - graph ownership transitions are valid
2. Ensure current-frame forward and transparent passes sample only a stable DDGI state. If a single DDGI atlas is currently read by forward and written by `DdgiUpdatePass`, either:
   - keep `DdgiUpdatePass` after all DDGI consumers and overlap it only with later passes, or
   - introduce double-buffered probe atlas/state resources so compute writes the next-frame DDGI state while graphics reads the previous completed state.
3. Treat scene submission buffers, DDGI probe resources, and ray-query acceleration structure usage as explicit scheduler dependencies.
4. Attach compute-to-graphics waits only to the first pass that needs freshly updated DDGI data. If updates feed the next frame, the wait can be moved to the next frame's DDGI consumer or frame start.
5. Respect `GlobalIllumination.EffectiveDdgiProbeUpdateTimeBudgetMilliseconds` so async DDGI does not consume the entire compute queue budget.
6. Add performance snapshot fields for:
   - DDGI async requested/supported/enabled
   - DDGI async fallback reason
   - DDGI async overlap window
   - DDGI update wait location
7. Add a runtime fallback that records DDGI on the graphics queue if queue support, resource ownership, validation, or timing benefit fails.

Validation:

- Startup smoke with DDGI enabled and async disabled matches current output.
- Startup smoke with DDGI async forced has clean Vulkan validation.
- Sponza/plaza GI smoke confirms `DdgiUpdatePass` runs on compute when enabled and returns to graphics when disabled.
- Performance snapshots show `GpuDdgiUpdateMicroseconds`, probe update count, rays per probe, async status, and fallback reason.
- Camera-relative clipmap scrolling, relocation, classification, and debug probe views remain correct.
- If double-buffering is added, tests cover read/write buffer selection and frame-latency behavior.
- Async DDGI remains default-off until timing proves lower frame time or meaningful overlap.

### SSGI Chain

Rationale:

- `SsgiTracePass`, `SsgiTemporalPass`, and `SsgiDenoisePass` are compute passes and are technically async candidates.
- The SSGI output is consumed by `SsgiCompositePass` in the same frame, so the possible overlap window is much smaller than DDGI.
- Moving only part of the chain can add queue ownership and semaphore cost without reducing total frame time.

Implementation steps:

1. Add per-pass settings:
   - `RenderSettings.AsyncCompute.SsgiTraceEnabled`
   - `RenderSettings.AsyncCompute.SsgiTemporalEnabled`
   - `RenderSettings.AsyncCompute.SsgiDenoiseEnabled`
2. Add scheduler grouping so the SSGI compute chain can move as one compute segment. Do not split the chain across queues unless timings prove it is beneficial.
3. Keep `SsgiCompositePass` on graphics because it writes/reads scene color as part of the graphics post chain.
4. Add graphics-to-compute ownership transitions for:
   - `SsgiTraceSource`
   - scene depth
   - scene normals
   - scene material
   - motion vectors
5. Add compute-to-graphics ownership transitions for:
   - `GiFinalDiffuse`
   - any SSGI debug targets consumed by graphics/debug presentation
6. Ensure `SsgiCompositePass`, fog, bloom, tone map, and anti-aliasing wait for the compute chain when SSGI async is enabled.
7. Add SSGI async status and fallback diagnostics for:
   - no overlap opportunity
   - composite wait dominates
   - queue transfer cost exceeds benefit
   - history resource hazard
   - disabled by feature isolation or GI settings
8. Keep SSGI async default-off until scenes with high SSGI cost show measured frame-time benefit.

Validation:

- SSGI raw, filtered, history, rejection, and final indirect debug views match graphics-only output.
- History ping-pong resources remain stable across resize, camera cuts, feature isolation changes, and scene reloads.
- Forced SSGI async passes Vulkan validation.
- GPU timing reports trace, temporal, denoise, composite wait cost, and total frame-time delta.
- Auto mode keeps SSGI on graphics when the composite wait erases the overlap benefit.

## Phase 11. Make Async Compute Auto-Default

Implementation steps:

1. Add an async compute mode enum:
   - `Disabled`
   - `Auto`
   - `ForceEnabledForValidation`
2. Set new renderer defaults to `Auto`, not forced enabled.
3. In `Auto`, enable only passes that satisfy:
   - queue support
   - graph ownership support
   - clean validation history
   - measured timing benefit
4. Preserve current explicit user-disable setting.
5. Add a startup diagnostic explaining whether auto async is active.
6. Add a settings migration path for saved render settings.

Validation:

- Fresh renderer defaults to `Auto`.
- On unsupported GPUs, behavior is identical to graphics-only.
- On supported GPUs without measured benefit, behavior remains graphics-only.
- On supported and beneficial GPUs, selected passes run async.
- Smoke, long-run, and feature-isolation tests pass in all modes.

## Phase 12. Ray Tracing Integration Readiness

Async compute can support future ray tracing work, but only after the base async scheduler is proven.

Candidate ray tracing workloads:

1. Ray traced AO denoise.
2. Ray traced reflection denoise.
3. Ray traced shadow denoise.
4. DDGI probe update filtering beyond the base `DdgiUpdatePass`.
5. SSGI or ray traced GI denoising beyond the base SSGI chain.
6. Progressive path tracing accumulation.
7. Acceleration structure refit/build only when synchronization cost is acceptable.

Implementation steps:

1. Add ray tracing pass metadata for:
   - queue intent
   - async eligibility
   - immediate consumer pass
   - output history resource
2. Prefer async for denoising and probe filtering before primary ray dispatch.
3. Keep ray outputs on graphics when the next pass consumes them immediately and overlap is impossible.
4. Add ray tracing timing buckets separate from raster compute timing.
5. Add memory bandwidth and RT workload saturation diagnostics where practical.

Validation:

- Ray tracing remains correct with async disabled.
- Async ray tracing candidates prove overlap or lower frame time.
- Heavy RT scenes do not enable async if RT cores, compute, or bandwidth are already saturated.

## Phase 13. CI And Regression Coverage

Implementation steps:

1. Add unit tests for:
   - queue family selection
   - scheduler decisions
   - async settings save/load
   - graph ownership transition diagnostics
   - async fallback reasons
2. Add smoke commands for:
   - async disabled
   - async auto
   - forced validation mode
   - AO blur async only
   - DDGI update async only
   - SSGI chain async only
   - bloom async only
3. Add snapshot checks for:
   - candidate pass list
   - enabled pass list
   - queue transition count
   - async status string
4. Add performance scenarios for:
   - post-processing-heavy frame
   - Sponza/plaza DDGI frame
   - SSGI-heavy frame
   - foliage-heavy frame
   - particle-heavy frame
   - future ray tracing frame

Validation:

- Full test suite passes.
- Vulkan validation remains clean.
- Long-run smoke has no synchronization, present, or fence stalls beyond baseline.
- Performance snapshots explain every async scheduling decision.

## Completion Criteria

Async compute can be considered complete and default-ready when:

1. `RenderSettings.AsyncCompute.Mode` defaults to `Auto`.
2. Unsupported devices automatically use graphics-only execution.
3. At least one production pass runs on a compute queue on supported hardware.
4. The async path has clean Vulkan validation.
5. Per-pass GPU timing proves measurable benefit.
6. Performance snapshots identify active async passes and fallback reasons.
7. Disabling async returns to the exact graphics-only scheduling path.
8. Feature isolation, debug views, smoke tests, and saved settings all work in disabled and auto modes.
9. `DdgiUpdatePass` has a validated async path or a documented default-off fallback reason on unsupported/unprofitable hardware.
10. The SSGI compute chain has explicit grouped scheduling, diagnostics, and a measured-benefit gate before it can be enabled by auto mode.
