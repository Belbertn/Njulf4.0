# Remaining Async Compute Paths — Production Implementation Plan

Date: 2026-07-13  
Status: Proposed  
Scope: `Njulf.Rendering`, render-graph scheduling, Vulkan synchronization, diagnostics, tests, and benchmark gates

## Goal

Safely implement and activate every useful async-compute path without changing rendered output, introducing queue-family ownership violations, stalling the graphics queue unnecessarily, or enabling paths that cost more than they save.

The final runtime must support three policies:

1. `Disabled`: the existing single-graphics-queue execution path.
2. `Auto`: enable only validated paths with a measured benefit on the current GPU and workload.
3. `ForceEnabledForValidation`: exercise every supported path for validation and testing, even when it is not profitable.

`Auto` is the intended production default after all completion gates pass. `ForceEnabledForValidation` must never bypass correctness or hardware-support checks.

## Current state and known gaps

The renderer already has:

- Dedicated compute-queue discovery and command pools.
- A timeline semaphore and split graphics/compute submissions.
- Async-candidate metadata and per-pass settings.
- A hand-written Simple DDGI trace/relocate/blend split.
- Queue-family transfers for seven Simple DDGI-owned buffers.

The remaining blockers are:

- Simple DDGI also reads TLAS, mesh/instance metadata, materials, lights, emissive sources, environment data, and far-field resources. These are not represented in its ownership handoff.
- The render graph tracks abstract queue intent but cannot resolve an abstract resource to concrete per-frame Vulkan buffers/images, subresource ranges, sharing modes, and current owners.
- Potential queue transitions are reported as if they were executable plans.
- `imageAvailable` is consumed by the setup submission even though the final graphics submission accesses the acquired swapchain image.
- Only Simple DDGI is actually recorded to the compute queue. Other candidate flags affect diagnostics but not execution.
- SSGI and GPU-particle chains are not modeled as indivisible async segments.
- There is no validation-time audit that proves every cross-queue resource has both sides of a handoff.
- There is no stable timing policy to prevent async paths from flapping or regressing frame time.

## Non-negotiable correctness invariants

Every implementation step must preserve these rules:

1. An exclusive resource crossing queue families has a matching release and acquire with identical source/destination family indices and compatible ranges.
2. A semaphore dependency connects the releasing submission to the acquiring submission. Queue-family barriers alone are insufficient.
3. Concurrently shared resources do not receive queue-family ownership transfers, but still receive the required execution and memory dependencies.
4. Image layouts and subresource ranges agree on both sides of a transfer.
5. A pass may run on compute only when every concrete resource it reads or writes has a resolved synchronization plan.
6. The first real consumer waits for completion; unrelated graphics work must remain free to overlap.
7. The acquired swapchain image is waited on by the first submission that can access it, and presentation waits on the final graphics completion semaphore.
8. Frame fences cover all work whose command pools or per-frame resources will be reused.
9. Disabling async returns to the exact graphics-only recording path and produces the same output.
10. Unsupported, incomplete, or validation-failed plans fall back for the current frame before command recording begins.

## Target execution model

Replace `ExecuteRenderGraphWithAsyncSimpleDdgi` with a compiled submission plan:

```text
graphics segment 0 --signal T1--> compute segment 0 --signal T2--+
       |                                                   wait T2|
       +---------------- graphics overlap segment 1 --------------+
                                                                   v
                                                        graphics segment 2
                                                                   |
                                                     renderFinished semaphore
                                                                   v
                                                                present
```

A frame may contain more than one compute segment, but adjacent compute passes with compatible dependencies must be grouped to reduce submissions and ownership transfers.

## Phase 0 — Lock the baseline and add a kill switch

1. Preserve the current default as `Disabled` until Phase 12 passes.
2. Capture graphics-only reference images and performance snapshots for:
   - startup/Cornell;
   - Sponza or plaza DDGI;
   - SSGI-heavy scene;
   - post-processing-heavy scene;
   - particle-heavy scene;
   - foliage/animation-heavy scene.
3. Record GPU frame time, per-pass timings, queue-submit CPU time, ownership-transfer count, DDGI convergence metrics, and final-frame hashes.
4. Add a process-wide emergency fallback that disables async after a synchronization-plan failure and records the exact reason.
5. Add a per-frame fallback that uses graphics-only execution if plan compilation fails before any command buffer for that frame is submitted.

Gate:

- Baseline artifacts are reproducible.
- `Disabled` contains no compute-queue submissions.
- Fallback reason is visible in diagnostics and snapshots.

## Phase 1 — Introduce explicit async policy and settings

1. Replace `AsyncComputeSettings.Enabled` with an `AsyncComputeMode` enum:
   - `Disabled`;
   - `Auto`;
   - `ForceEnabledForValidation`.
2. Retain a settings migration path: old `Enabled=false` maps to `Disabled`; old `Enabled=true` maps to `ForceEnabledForValidation` for explicit user configurations.
3. Add individual flags for every planned path:
   - Simple DDGI update;
   - full DDGI update;
   - far-field clipmap bake;
   - AO blur;
   - Hi-Z build;
   - SSGI chain;
   - fog;
   - bloom;
   - GPU particles.
4. Add internal status per path: `DisabledByPolicy`, `DisabledByFeature`, `UnsupportedQueue`, `MissingResourcePlan`, `PendingWarmup`, `NoMeasuredBenefit`, `Enabled`, or `ValidationFallback`.
5. Persist the mode and all flags in `RenderSettingsFile` and add round-trip tests.

Gate:

- Old settings load deterministically.
- Every path can be disabled independently.
- Policy and actual execution state are reported separately.

## Phase 2 — Bind render-graph resources to concrete Vulkan resources

1. Add a concrete resource-binding layer owned by the render graph. Each binding must expose:
   - `RenderGraphResourceId`;
   - buffer or image handle;
   - byte range or image subresource range;
   - image layout;
   - Vulkan sharing mode;
   - permitted queue families;
   - current owner for exclusive resources;
   - frame/history index for ping-pong resources;
   - lifetime and aliasing generation.
2. Allow one graph resource ID to resolve to multiple concrete resources. This is required for buffer sets such as DDGI, scene submission, particles, and histories.
3. Register imported resources explicitly instead of treating `BufferSet` and `External` as opaque synchronization units.
4. Resolve transient and history images after resize/recreation so plans never retain stale handles.
5. Add dedicated graph resource IDs where a bundle hides different ownership lifetimes. At minimum split:
   - TLAS storage and ray-query metadata;
   - material buffers;
   - light buffers;
   - emissive-source buffer;
   - mesh/index/vertex buffers used by hit shading;
   - far-field parameter, voxel, instance, and jump-flood buffers;
   - Simple DDGI atlases, scratch, state, update queue, and relocation data;
   - full DDGI scheduler, ray, atlas, state, and publish resources.
6. Make pass declarations list actual stage/access masks. Replace generic `Read`/`Write` usages on compute passes with compute-specific usages.
7. Add a graph validation error for an enabled pass whose concrete resource set is incomplete.

Gate:

- A debug dump lists every pass and every concrete resource handle/range it touches.
- Resize, scene reload, and history ping-pong update bindings without stale generations.
- Unit tests reject missing, overlapping-invalid, or out-of-range bindings.

## Phase 3 — Build a reusable queue-handoff API

1. Add a `QueueOwnershipTransfer` model containing resource, range, source queue, destination queue, source stage/access, destination stage/access, and layouts.
2. Generate paired sync2 barriers:
   - release: source stage/access populated, destination stage/access `None`;
   - acquire: source stage/access `None`, destination stage/access populated;
   - matching queue-family indices, range, and layouts.
3. For same-family queues, emit ordinary memory barriers and omit ownership indices.
4. For concurrent resources, omit queue-family indices and emit only memory/layout dependencies.
5. Coalesce adjacent compatible buffer ranges and image subresources to reduce barrier count.
6. Track resource ownership only after the submission plan has been accepted. Roll back planned state on fallback.
7. Add a validator that pairs every release with exactly one acquire and verifies a semaphore edge exists between their submissions.
8. Replace the fixed seven-buffer stack allocation in `SimpleDdgiVolumeManager.RecordAsyncComputeQueueFamilyTransfer` with graph-generated transfers.

Gate:

- Synthetic graph tests cover graphics→compute, compute→graphics, same-family, concurrent, image-layout transition, and multi-segment cases.
- No cross-family transition is emitted from a single command buffer.
- Barrier diagnostics distinguish planned, emitted-release, and emitted-acquire counts.

## Phase 4 — Replace ad-hoc submissions with a generic segment scheduler

1. Introduce `AsyncComputeScheduler` with immutable input:
   - enabled pass sequence;
   - concrete resource usages;
   - queue capabilities;
   - feature isolation;
   - per-path settings;
   - timing state.
2. Compile passes into ordered `GraphicsSegment` and `ComputeSegment` records.
3. Group adjacent compute passes when doing so does not move a wait earlier or shorten useful overlap.
4. Determine each compute segment's latest producer and earliest consumer.
5. Insert release/acquire operations at segment boundaries, not at global frame boundaries.
6. Reject cycles and plans that require both queues to wait on one another without progress.
7. Allocate monotonically increasing timeline values from one renderer-owned counter. Do not derive values from a fixed two-values-per-frame formula once multiple compute segments exist.
8. Record all command buffers before submitting any segment, so recording failure can still use the graphics fallback.
9. Keep the graphics-only `RenderGraph.Execute` path intact and isolated.

Gate:

- Scheduler unit tests cover zero, one, and multiple compute segments; immediate consumers; independent overlap; feature-isolated passes; and cycle rejection.
- Segment dumps explain every pass placement and wait edge.

## Phase 5 — Correct submission and swapchain synchronization

1. Move the binary `imageAvailable` wait off `SubmitAsyncSetupGraphicsCommand`.
2. Attach `imageAvailable` to the first graphics submission that accesses the acquired swapchain image, normally the final/late graphics segment.
3. When that submission also waits on timeline values, provide a wait value of zero for the binary semaphore or migrate submissions to `vkQueueSubmit2`/`SemaphoreSubmitInfo` to make mixed waits explicit.
4. Wait at the earliest accurate stage for each dependency, not a broad all-commands mask.
5. Signal `renderFinished` only from the final graphics submission.
6. Attach the in-flight frame fence to the final graphics submission after it waits for all compute work that can touch per-frame resources.
7. Verify early graphics submissions cannot outlive command-pool or descriptor reuse.
8. Account for device-lost or submit failure without leaving a reset fence permanently unsignaled.

Gate:

- Vulkan synchronization validation is clean for disabled, one-segment, and multi-segment frames.
- Acquire/present smoke runs through resize, out-of-date swapchain, minimization, and recreation.

## Phase 6 — Harden and enable Simple DDGI first

1. Audit shader and descriptor reads for `SimpleDdgiTracePass`, `SimpleDdgiRelocateClassifyPass`, and `SimpleDdgiBlendPass`.
2. Add every input to the graph, including:
   - TLAS and its backing/read metadata;
   - mesh/index/vertex and instance metadata used by hit shading;
   - material and material-extension buffers;
   - light and environment buffers/textures;
   - DDGI emissive sources;
   - far-field parameters, voxels, instances, and distance data;
   - the seven existing Simple DDGI-owned buffers.
3. Choose sharing deliberately:
   - use concurrent sharing for long-lived read-mostly inputs consumed by both queues when profiling shows QFOT overhead is material;
   - retain exclusive ownership for writable DDGI atlases/scratch/state and use explicit transfers.
4. Ensure TLAS build/update completion has an acceleration-structure-write→shader-read dependency plus the graphics→compute semaphore edge.
5. Preserve the intended sampling contract: opaque forward may read the previous stable atlas while async update writes only resources that are not concurrently sampled. If the same atlas is read and written, introduce atlas/state double buffering before enabling overlap.
6. Acquire freshly published resources only at the first graphics consumer that needs them; do not stall independent shadow/depth work.
7. Remove `ExecuteRenderGraphWithAsyncSimpleDdgi` after the generic path produces the same segment plan.

Gate:

- Forced Simple DDGI async is clean under synchronization and GPU-assisted validation.
- Static-scene frame hashes and dark-region temporal variance match graphics-only tolerances.
- Camera recenter, dirty lighting, classification, relocation, and maintenance-ray tiers pass long-run smoke.
- No atlas is read by graphics while compute writes the same physical allocation.

## Phase 7 — Enable full DDGI and far-field bake

1. Model `FarFieldClipmapBakePass` and the full DDGI schedule/trace/blend/relocate/publish chain as grouped compute segments.
2. Include all scheduler feedback/readback buffers and transfer-copy stages in resource usages.
3. Keep far-field bake in the same compute segment as DDGI when DDGI consumes it immediately; otherwise publish it for a later frame to avoid a graphics→compute→graphics round trip.
4. Apply the same TLAS/material/light/emissive ownership audit used for Simple DDGI.
5. Define publication semantics explicitly: current-frame consumer, next-frame consumer, or double-buffered generation.
6. Prevent Simple DDGI and full DDGI from claiming or writing shared far-field resources simultaneously.
7. Add generation IDs so readbacks and published atlases from a prior scene/resize are discarded.

Gate:

- Both GI modes pass independently and while switching modes at runtime.
- Far-field jump-flood on/off variants pass validation.
- Readback generation and frame-latency tests remain deterministic.

## Phase 8 — Enable low-risk image paths

Implement one path at a time in this order:

### 8.1 AO blur

1. Release `AmbientOcclusionRaw` and `AmbientOcclusionScratch` after AO generation.
2. Acquire them on compute, execute the full blur chain, and release `AmbientOcclusionBlurred`.
3. Acquire the output immediately before the first forward consumer.
4. Verify both blur ping-pong directions and AO debug views.

### 8.2 Hi-Z build

1. Release the completed depth image with the correct depth subresource/layout.
2. Build every Hi-Z mip on compute with intra-chain barriers.
3. Release the complete mip range and acquire it before visibility compaction/culling.
4. Keep this path off in `Auto` unless measured overlap exceeds the immediate-consumer wait cost.

### 8.3 Fog

1. Release scene color/depth only after their final pre-fog producers.
2. Execute fog on compute and release `FogOutput`.
3. Acquire it before auto exposure, bloom, and tone mapping according to their actual queue placement.

### 8.4 Bloom

1. Acquire its chosen scene/fog input after auto-exposure dependencies are satisfied.
2. Keep extract/downsample/upsample in one compute segment with per-mip barriers.
3. Release the full bloom chain and acquire it before tone mapping.
4. Keep this path off in `Auto` when the tone-map wait eliminates the benefit.

Gate for each path:

- Enable only that path in forced mode.
- Compare its debug view and final image against graphics-only.
- Pass resize and dynamic-resolution tests.
- Record transfer overhead separately from dispatch time.

## Phase 9 — Add the SSGI chain as one atomic async path

1. Mark trace, temporal, and denoise as an indivisible scheduler group.
2. Add a single `SsgiChainEnabled` setting; do not permit partial cross-queue placement initially.
3. Resolve all current and history images by frame index, including depth/normal/motion histories, moments, and history length.
4. Release scene color trace source, depth, normals, materials, and motion vectors after their final producers.
5. Execute trace→temporal→denoise on compute with ordinary intra-queue barriers.
6. Release `GiFinalDiffuse` and acquire it immediately before `SsgiCompositePass`.
7. Reset or invalidate history ownership on resize, camera cut, GI mode switch, feature isolation, and scene reload.
8. Add a scheduler rule that rejects the path if the immediate composite wait leaves no independent graphics work to overlap.

Gate:

- Raw, hit-distance, temporal, rejection, denoised, and final debug views match.
- History ping-pong remains stable for at least 1,000 frames.
- `Auto` enables the group only with measured frame-time improvement.

## Phase 10 — Add GPU particles as one atomic async path

1. Register reset, simulation, sort keys/indices, counters, indirect arguments, emitter data, and particle output buffers as concrete graph resources.
2. Model reset→simulate→sort as one compute segment.
3. Acquire emitter/upload inputs after graphics-side uploads are complete.
4. Release particle vertex/indirect buffers and acquire them before `ParticlePass`.
5. Handle zero-emitter and zero-particle frames without producing an empty ownership cycle.
6. Preserve per-frame buffer rotation and ensure the final frame fence covers compute writes before reuse.
7. Validate deterministic seeds and sort ordering against the graphics-queue path.

Gate:

- Spawn/despawn, capacity growth, scene reload, and long-run sorting pass.
- No particle buffer is reused before compute completion.

## Phase 11 — Audit all remaining compute-only passes

1. Enumerate every pass that records only compute commands, including compaction, culling, skinning, tiled-light culling, auto exposure, far-field work, and future denoisers.
2. For each pass, document:
   - producers and consumers;
   - concrete resources;
   - earliest start and latest completion;
   - expected overlap window;
   - ownership-transfer count;
   - whether grouping with a neighboring path is required.
3. Classify each as:
   - production async candidate;
   - validation-only candidate with no useful overlap;
   - graphics-queue compute by design.
4. Add only production candidates to `Auto`; forced validation may exercise the others when synchronization is fully modeled.
5. Make the diagnostics candidate list reflect executable candidates, not merely `SupportsAsyncCompute` metadata.

Gate:

- No compute-only pass remains accidentally unclassified.
- Every reported enabled pass is actually submitted on the compute queue.

## Phase 12 — Add timing-based auto selection

1. Maintain rolling graphics-only and async timing windows per path and GPU/device/driver identity.
2. Require warmup and a minimum sample count before enabling a path automatically.
3. Include compute dispatch time, transfer/barrier cost, graphics wait time, and CPU submission overhead.
4. Require both:
   - at least 0.25 ms absolute or 3% frame-time improvement;
   - no material increase in p95/p99 frame time.
5. Use separate promote/demote thresholds and a cooldown to prevent frame-by-frame flapping.
6. Invalidate decisions after resolution, scene, feature, GI mode, or major workload changes.
7. Do not persist a positive decision across driver/device changes.
8. Keep an explicit user disable authoritative.

Gate:

- A 1,000-frame workload does not oscillate between queues.
- Diagnostics explain the measured benefit or rejection for every path.

## Phase 13 — Diagnostics and observability

Add these fields to runtime diagnostics and performance snapshots:

- requested async mode and effective mode;
- dedicated/shared queue topology;
- planned and submitted graphics/compute segment counts;
- per-segment pass list and timeline wait/signal values;
- planned/release/acquire barrier counts;
- bytes and image subresources transferred per direction;
- per-path requested, supported, eligible, active, and fallback state;
- compute queue busy time, graphics overlap estimate, and first-consumer wait time;
- validation fallback count and last reason;
- resource-plan generation and stale-plan rejection count.

Avoid logging raw Vulkan handles in normal telemetry; include them only in opt-in debug dumps.

Gate:

- A snapshot is sufficient to reconstruct why a path ran on a particular queue.
- Potential transitions cannot be mistaken for executed ownership transfers.

## Phase 14 — Test and CI matrix

1. Unit tests:
   - settings migration and round-trip;
   - resource binding and generation invalidation;
   - barrier pairing and coalescing;
   - scheduler grouping, wait placement, cycle detection, and fallback;
   - timeline monotonicity across frames and multiple segments;
   - timing promote/demote hysteresis.
2. Contract tests must inspect structured plans, not source-code strings.
3. Vulkan validation smoke:
   - async disabled;
   - each path alone;
   - all supported paths forced;
   - same-family queue fallback;
   - dedicated queue;
   - resize/minimize/restore;
   - scene reload and GI mode switch.
4. Enable synchronization validation and GPU-assisted validation in a dedicated CI lane.
5. Visual tests:
   - final HDR and LDR image comparisons;
   - all affected debug views;
   - lights-off black-frame oracle;
   - DDGI dark-region temporal variance;
   - SSGI history stability;
   - particle deterministic replay.
6. Performance tests:
   - median, p95, and p99 frame time;
   - queue wait/busy time;
   - CPU submit overhead;
   - memory and transient-allocation deltas.
7. Long-run smoke for at least 10,000 frames with all paths forced on supported hardware.

Gate:

- No validation errors or warnings attributable to async execution.
- No output mismatch outside documented floating-point tolerance.
- No resource/fence growth or command-pool reuse hazard.

## Phase 15 — Production rollout

1. Ship the generic scheduler with mode defaulting to `Disabled` for one validation cycle.
2. Enable `ForceEnabledForValidation` in internal/nightly runs.
3. After clean validation and visual gates, change fresh-install default to `Auto`.
4. Keep each new path individually guarded by its measured-benefit policy.
5. Roll out in this order:
   - Simple DDGI;
   - full DDGI/far field;
   - AO blur;
   - GPU particles;
   - Hi-Z;
   - SSGI;
   - fog;
   - bloom;
   - additional audited compute passes.
6. Retain `Disabled` and the emergency fallback permanently.
7. Document expected support behavior for dedicated compute, same-family compute, and unsupported hardware.

## Completion criteria

The remaining async work is complete only when all of the following are true:

1. Every enabled compute-queue pass is scheduled by the generic segment planner.
2. Every concrete cross-queue resource has a validated synchronization plan.
3. The Simple DDGI TLAS/material/light/emissive/far-field ownership hole is closed.
4. Swapchain acquisition is waited on by the correct graphics submission.
5. All candidate paths can be forced independently and together without Vulkan validation findings.
6. `Disabled` exactly matches the graphics-only baseline.
7. `Auto` enables only paths with a stable measured benefit.
8. Final images, debug views, temporal histories, and DDGI convergence remain within their regression tolerances.
9. Performance snapshots distinguish requested, planned, submitted, and profitable async work.
10. Fresh installations can safely default to `Auto`, while unsupported systems transparently remain graphics-only.

## Recommended first implementation slice

The first pull request should contain Phases 1–5 only: policy/settings, concrete resource binding, generic paired handoffs, segment compilation, and corrected swapchain submission. It should keep all production paths disabled. The second pull request should migrate and validate Simple DDGI through that foundation. This ordering removes the known undefined behavior before expanding the candidate set.
