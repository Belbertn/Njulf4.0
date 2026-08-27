# AsyncComputeCoordinator Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract the stateful async-compute policy and coordination currently embedded throughout `Njulf.Rendering/VulkanRenderer.cs` into an internal `AsyncComputeCoordinator` under `Njulf.Rendering/Pipeline`.

The completed refactor must:

- Give one renderer-lifetime object ownership of async mode resolution, Auto timing state, path isolation/probing, scheduler invocation, plan caching, projected resource state, retry/quarantine state, relative timeline bookkeeping, per-frame async counters, and async diagnostics snapshots.
- Move the private renderer `AsyncComputePlan` wrapper into an internal pipeline contract named `AsyncComputeFramePlan` because it becomes a legitimate boundary between the coordinator, the renderer's Vulkan execution shell, and diagnostics capture.
- Replace direct renderer access to approximately thirty async-specific fields with typed coordinator operations and immutable snapshots.
- Keep concrete renderer-resource discovery, command-pool/timeline-semaphore ownership, unsafe Vulkan command recording, queue submission, terminal fence recovery, swapchain image transitions, presentation, and global frame-fault ownership outside the coordinator.
- Preserve the graphics-only hot path, conditional concrete-resource refresh, scheduler validation, exact fallback/status text, Auto sample attribution, one-path isolation, relative timeline cache domain, queue-family ownership handoffs, and fail-closed submission behavior.
- Preserve all public async settings, scheduler, diagnostics, performance-capture, and serialized contracts.

The target relationship is:

```text
renderer captures frame/settings/device/resource facts
                         |
                         v
              AsyncComputeCoordinator
       policy + timing + cache + projection + plan
                         |
                         v
              AsyncComputeFramePlan
                         |
                         v
       renderer Vulkan record/submit effect shell
                         |
                         v
     coordinator execution/timing/diagnostic updates
                         |
             +-----------+-----------+
             v                       v
       next-frame Auto          diagnostics/capture
          feedback
```

`AsyncComputeCoordinator` must not depend on `VulkanRenderer`, own a Vulkan handle, submit a queue, reset a command pool, transition a swapchain image, present, or decide whether the renderer as a whole can continue after a failed submission.

## 2. Audited starting point

### 2.1 Size and distribution

`VulkanRenderer.cs` is 23,084 lines in the audited working tree. Async-compute behavior is not one contiguous method; it is distributed across the renderer lifecycle:

| Region in the audited tree | Current responsibility |
| --- | --- |
| lines 94-97 | Scheduler, timing policy, deferred submissions, and timing-frame ring. |
| lines 493-533 | Submission timings, frame counters, waits, timeline domain, plan/cache/projection/validation/retry/fallback state. |
| lines 2556-2563 | Startup audit that every async-capable pass has a production classification. |
| lines 3718 and 3793-3842 | Fence-complete timing ingestion, command-pool reset, validation quarantine, and frame-state reset. |
| lines 3883-4023 | Deferred queue submission, terminal timeline waits, submitted-segment publication, and timing finalization. |
| lines 4726-4769 | Plan construction, one-queue receiver-capture constraint, async/graphics graph routing, and DDGI execution flag. |
| lines 4815-4817 and 4848 | Overlap estimate and timing-frame capture. |
| lines 7374 and 9492-9567 | Plan fallback lookup and diagnostics projection. |
| lines 10489-10845 | Split render-graph recording, projection commit, timeline rebasing, deferred submission, and counters. |
| lines 10948-10995 | Recoverable retry state and permanent emergency fallback latch. |
| lines 11118-11633 | Timing-frame state, plan compilation, variant keys, projection, fallbacks, and settings signature. |
| lines 11639-12454 | Concrete renderer resource inventory and binding-plan construction. |
| lines 12456-12938 | Path activity, Auto selection/probing, timing feedback, identity, private plan/identity records. |
| lines 13376-13471 | Queue-busy, overlap, and first-consumer-wait estimates. |
| lines 13793-13794 | Selected-pass predicate used while recording split segments. |
| lines 21991-21992 and 22233-22234 | Timing-history resets after render-target and swapchain recreation. |

The relevant code is roughly 2,500-3,000 lines when resource inventory and Vulkan execution are included. This extraction should move the stateful coordination and pure policy, not hide all of those lines behind a new god object.

### 2.2 Existing subsystem boundaries

Several focused async-compute components already exist:

- `AsyncComputeScheduler` compiles immutable pass/resource declarations into validated graphics/compute submission segments without Vulkan calls.
- `AsyncComputeTimingPolicy` owns rolling graphics-only and async timing windows plus promotion/demotion decisions.
- `AsyncComputeResourceStateProjection` stages and atomically commits the resource state implied by a plan.
- `AsyncComputePlanVariantCache` stores accepted immutable plans in a stable relative timeline domain.
- `AsyncComputeValidationLedger` attributes validation failures and quarantines unsafe paths.
- `AsyncComputeRecoverablePlanRetryGate` retains a rejected graph/settings scope until that scope changes.
- `AsyncComputePassCatalog` is the source audit for production candidates and correctness evidence.
- `QueueOwnershipTransferRecorder` emits the validated acquire/release barriers.
- `CommandBufferManager` owns async command pools, command buffers, and the timeline semaphore.
- `VulkanContext` owns queue-family selection, queue handles, and capability facts.

The new coordinator must compose these owners. It must not reimplement their algorithms or absorb their resource lifecycles.

### 2.3 Current frame lifecycle

The existing sequence is significant:

1. `BeginFrame` waits the reused frame slot's fence and reads completed GPU timestamps.
2. The prior timing-frame record for that slot is consumed before the slot is reused.
3. Graphics/split/compute command pools are reset by `CommandBufferManager`.
4. A primary graphics command buffer begins.
5. The validation ledger starts the new frame, then any newly observed Vulkan errors are checked against whether the preceding frame submitted compute segments.
6. A validation error after active async work permanently latches graphics-only emergency fallback.
7. All current-frame async counters, waits, plans, deferred submissions, and timeline offset are reset.
8. `DrawScene` prepares every feature that influences path activity and concrete resource availability.
9. Async planning occurs immediately before render-graph execution.
10. Validated async plans record split graphics/compute segments; rejected or inactive plans execute the original graphics-only graph.
11. Completed GPU timings from the previous frame are applied to scene diagnostics, while the current frame captures a new timing record.
12. `EndFrame` submits all non-terminal segments first, then submits the terminal graphics segment with image-available and resolved timeline waits.
13. Only after the terminal submit succeeds are submitted-segment diagnostics and timing CPU costs finalized.

The coordinator API must model these phases rather than offering one ambiguous `Update` method.

### 2.4 Current mode and resource gating

The exact high-level gating is:

1. A requested `Disabled` mode returns the graphics-only plan before refreshing concrete bindings, allocating scheduler inputs, beginning projected state, or touching async command resources.
2. Otherwise, emergency fallback changes the effective mode to `Disabled` without mutating the requested setting.
3. Async support requires both an independent compute queue and a valid async timeline semaphore.
4. Concrete resource bindings are refreshed only when support is present and the effective mode is not disabled.
5. Projected state begins whenever the effective mode is not disabled, including the existing unsupported-queue path.
6. The per-frame timeline offset is `checked(_nextAsyncComputeTimelineValue - 1UL)` for an effective non-disabled mode and zero otherwise.
7. Scheduler plans always use a relative first timeline value of `1UL`; cached variant keys use timeline base zero.

These details protect the disabled hot path and allow cached plans to be reused without cloning/rebasing segment records.

### 2.5 Current Auto policy

For each `AsyncComputePath`, the renderer currently combines:

- the per-path settings toggle;
- active feature-isolation mode;
- feature/runtime activity for the current frame;
- correctness certification and validation quarantine;
- concrete resource-plan completeness;
- the timing-policy decision for the current device, driver, workload identity, and path.

Auto mode intentionally schedules at most one path at a time. It either:

- keeps one already eligible path active, selected by greatest mean graphics-only versus async benefit and then enum order; or
- rotates one isolated timing probe after a valid graphics baseline exists.

Other requested paths are paused while that path is isolated. An incomplete path must not monopolize probe rotation. `ForceEnabledForValidation` may admit the one explicitly authorized path but still observes capability, resource, certification, and validation safety.

Timing history is cleared when:

- scene payload is rebuilt;
- the Hi-Z scene changes;
- the Hi-Z policy reports a camera cut;
- requested async mode changes;
- render targets are recreated;
- the swapchain is recreated.

Scene/mode resets also reset `_nextAutoTimingProbePath` to zero. The two recreation call sites currently clear timing policy and in-flight timing frames without explicitly resetting the probe index; preserve that distinction during the move.

### 2.6 Path-activity dependencies

Current path activity is feature-specific:

- `SimpleDdgiUpdate` requires effective DDGI, the DDGI async setting, an active Simple DDGI frame, update/scheduler work, and no sampled-atlas image mirror.
- `FarFieldClipmapBake` requires effective DDGI, far-field clipmaps, and pending bake work.
- `AmbientOcclusionBlur` requires enabled AO, a depth prepass, and a positive blur radius.
- `HiZBuild` follows `SceneRenderingData.HiZBuildEnabled`.
- `Fog` requires active non-animation-debug fog, excludes a possible froxel path, and excludes the sampled DDGI atlas mirror.
- `Bloom` requires enabled bloom and a non-empty mip chain, and remains on graphics for the existing fog-debug combination.
- `GpuParticles` requires the GPU-particle frame flag, at least one emitter, and positive capacity.

The coordinator may read borrowed `RenderSettings` and `SceneRenderingData` synchronously, plus small external facts such as far-field bake pending and bloom mip count. It must not retain either mutable object or receive the feature managers themselves.

### 2.7 Pass mapping and graph inputs

`VulkanRenderer.GetAsyncComputePath` currently maps:

- the ten Simple DDGI schedule/trace/relocate/directional-radiance/solve/transport/blend/publish/audit/commit passes to `SimpleDdgiUpdate`;
- `FarFieldClipmapBakePass`, `AmbientOcclusionBlurPass`, `HiZBuildPass`, `FogPass`, and `BloomPass` to their single paths;
- particle reset/simulate/sort to `GpuParticles`.

`GetRepresentativeAsyncPass` supplies the feature-isolation representative for each path. Both mappings are catalog policy and belong beside `AsyncComputePassCatalog`, not in the renderer or duplicated in the coordinator.

The renderer currently asks `RenderGraph` for pass names, usages, feature-isolation state, `WillExecutePass`, concrete plan validation, graph diagnostics, and live concrete bindings. A coordinator may borrow the existing `RenderGraph` for these focused pipeline queries, but it must never initialize, dispose, mutate pass registration, or own the graph.

### 2.8 Plan validation, caching, and projected state

The variant key includes:

- concrete binding generation;
- async settings signature;
- independent queue/family/flag signature;
- pass name/path/isolation/will-execute signature;
- five path-selection bits per path;
- concrete synchronization-state generation;
- a zero relative timeline base.

Only accepted plans enter the 16-entry cache. The fixed projected-state capacity is 4,096 bindings. An otherwise accepted async plan becomes rejected when projection capacity is unavailable or a transfer/pass usage cannot be staged.

Projection covers both explicit transfers and every concrete pass usage so cached plans cannot rely on stale owner/layout state. It commits only after all segments have been recorded. After projection commit, concrete binding owner/layout state is committed in immutable transfer order, then the absolute timeline domain advances.

No rejected, constrained, stale, or partially recorded plan may publish projected resource state.

### 2.9 Recoverable versus terminal fallback

There are two different failure classes and they must not be merged:

**Recoverable before recording/submission**

- scheduler or projection validation rejects the plan;
- the resource-binding generation changes after compilation;
- a non-terminal segment would access the acquired swapchain image.

These retain the exact graph-generation/settings-signature scope on the graphics path through `AsyncComputeRecoverablePlanRetryGate`. A changed scope can try again. The fallback counter increments only when a newly rejected scope is recorded.

The exact receiver-feedback capture constraint also converts a compiled plan to graphics-only, but it is a deliberate one-queue completion-domain constraint. It must not poison the retry gate or be misreported as a synchronization-plan failure.

**Terminal after recording/submission has started**

- split-plan recording or projection commit fails after graph work has been appended;
- a non-terminal queue submit fails;
- terminal graphics submission/fence recovery fails;
- validation errors are observed after active async segments.

These latch permanent renderer-lifetime emergency fallback, discard projected state, clear cached variants, increment the fallback count, and participate in the renderer's global frame-submission fault path. There is no public reset API.

### 2.10 Timeline and submission behavior

Scheduler wait/signal values are relative. The renderer currently:

- resolves nonzero values with checked addition to the frame offset;
- stores resolved waits from the terminal graphics segment;
- advances `_nextAsyncComputeTimelineValue` to one past the largest resolved signal only after successful recording/projection commit;
- submits every non-terminal segment before resetting and using the terminal frame fence;
- uses the async timeline semaphore for all inter-segment waits/signals;
- attaches `TimelineSemaphoreSubmitInfo` whose arrays cover binary semaphores with zero values as required;
- waits `imageAvailable` only in the terminal graphics submission;
- records one submitted graphics/compute count only after each successful queue submit.

Terminal graphics submission, image ownership, fence reset/recovery, screenshots, and present remain renderer responsibilities. The coordinator owns the relative/absolute timeline bookkeeping and submitted-count state used by those effects.

### 2.11 Timing and diagnostics lifecycle

The timing-frame ring is indexed by frames in flight. A captured record retains the frame plan plus a timing key for every requested path.

- Disabled and forced-validation frames do not create Auto timing records.
- Graphics-only Auto frames record baselines only for requested paths that actually execute as features.
- Async frames record only active paths.
- Total positive GPU pass durations are summed with checked arithmetic.
- Async frame time subtracts the explicitly labeled overlap estimate.
- CPU queue-submit and barrier-record costs are attached only after terminal submission succeeds.
- A non-positive completed GPU frame is ignored.

Diagnostics currently combine plan fields, queue topology, planned/emitted/submitted counts, transfer bytes/subresources, retry/fallback state, absolute timeline values, and timing estimates. `BuildDiagnostics` publishes most values during `DrawScene`; `EndFrame` later patches submitted graphics/compute segment counts.

The coordinator should expose one immutable `AsyncComputeDiagnosticsSnapshot` plus a small terminal `AsyncComputeSubmissionPatch`. It must not construct or patch `RendererDiagnostics` itself.

### 2.12 Existing tests and gaps

Existing tests cover:

- scheduler segmentation, waits/signals, swapchain restrictions, atomic groups, optional passes, exact resource ranges, and ownership transfers;
- resource bindings, immutable plans, generation invalidation, aliases, live layouts, and resource kinds;
- projected-state discard/commit behavior;
- variant-cache acceptance, staleness, and eviction;
- validation-ledger attribution/quarantine;
- recoverable retry on graph/settings scope change;
- settings migration and round-trip behavior;
- timing warmup, promotion, regression demotion, probe rules, and CPU-cost inclusion.

There is no focused fixture for the renderer-owned coordination around those pieces: mode/scene reset sequencing, one-path orchestration, graphics-only constraints, relative timeline rebasing, frame counter publication, permanent fallback latching, timing-frame ring lifecycle, or diagnostics snapshot construction.

## 3. Scope

### In scope

- An internal renderer-lifetime `AsyncComputeCoordinator` in `Njulf.Rendering/Pipeline`.
- Internal planning, recording-decision, execution-summary, diagnostics-snapshot, and submission-patch contracts.
- Moving the private `AsyncComputePlan` wrapper to `AsyncComputeFramePlan`.
- Moving scheduler/timing/cache/projection/validation/retry ownership from `VulkanRenderer`.
- Moving mode changes, Auto isolation/probing, path eligibility/status, plan compilation, fallback conversion, variant-key creation, projected-state staging/commit, relative timeline resolution, timing feedback, and async estimates.
- Moving async-specific frame counters, waits, fallback state, validation baseline, and current plan behind the coordinator.
- Moving pass-to-path and representative-pass mappings beside `AsyncComputePassCatalog`.
- Replacing direct async diagnostics field reads with a coherent coordinator snapshot.
- Keeping a narrow renderer execution shell that applies the coordinator's plan using existing Vulkan owners.
- Characterization and coordinator tests for all cross-frame state machines and failure classes.

### Out of scope

- Queue-family/device selection or changing what counts as independent/dedicated compute support.
- Creating, destroying, or moving the async timeline semaphore or command pools out of `CommandBufferManager`.
- Changing `AsyncComputeScheduler`, ownership-transfer validation, barrier scopes, pass grouping, or atomicity rules except for call-site relocation needed by the coordinator.
- Moving the 600-plus-line concrete resource inventory into the coordinator or passing every renderer manager to it.
- Changing render-graph pass registration, declaration order, resource declarations, or pass `SupportsAsyncCompute` flags.
- Moving unsafe `QueueSubmit`, terminal graphics submission, fence recovery, acquired-image handling, screenshot/readback submission, or present into the coordinator.
- Changing Auto thresholds, window capacity, probe order, workload identity fields, timing equations, or sample attribution.
- Changing public async settings, enum values, scheduler records, diagnostics properties, JSON keys, or capture schema.
- Automatically resetting permanent emergency fallback after resize, reload, settings changes, or a successful graphics frame.
- Adding a worker thread, parallel command recording, a new queue, queue priorities, semaphore type, or synchronization algorithm.
- Combining this move with performance tuning, allocation cleanup, status-text edits, or pass certification changes.

## 4. Non-negotiable invariants

1. The requested mode remains distinct from the effective mode and is never rewritten by fallback.
2. Requested `Disabled` retains the early graphics-only hot path and does not refresh concrete async bindings or begin projected state.
3. Support still requires `VulkanContext.HasIndependentComputeQueue` and a nonzero `CommandBufferManager.AsyncComputeTimelineSemaphore` handle.
4. Concrete binding refresh remains gated by support and effective non-disabled mode.
5. An emergency fallback latch persists for the coordinator/renderer lifetime and has no public reset.
6. Validation errors are compared at the same `BeginFrame` boundary before prior submitted-segment counters are reset.
7. Only errors observed after a frame that submitted compute segments trigger the current coarse emergency quarantine.
8. Scene/mode timing resets clear the policy, all in-flight timing frames, and the Auto probe cursor; target/swapchain recreation keeps its current narrower reset behavior.
9. Auto schedules at most one path or timing probe per frame.
10. Auto selection keeps benefit-descending then enum-order tie breaking.
11. Incomplete resource plans do not monopolize timing-probe rotation.
12. Force mode bypasses profitability only; it does not bypass certification, validation authorization, capabilities, resources, or scheduler validation.
13. Pass-to-path membership and representative pass names remain byte-for-byte equivalent to the current switches.
14. Scheduler compilation uses relative `FirstTimelineValue: 1UL`; variant keys use timeline base zero.
15. Timeline offset/subtraction/addition and next-value advancement remain checked.
16. Cache hits do not clone or rewrite immutable segment wait/signal records.
17. Variant keys retain every current generation, capability, pass, path, and synchronization-state component.
18. Only accepted plans enter the variant cache.
19. Projected state covers transfers and final state for every executed pass usage.
20. Projected state commits only after every split segment has recorded successfully.
21. Concrete binding owner/layout commits retain transfer order and occur after projection commit.
22. A stale binding generation records `RecordStalePlanRejection`, stays graphics-only, and is recoverable only through a changed retry scope.
23. Any non-terminal swapchain access is rejected before split recording begins.
24. The receiver-feedback one-queue constraint produces graphics fallback without recording a recoverable plan failure.
25. Explicit plan validation failures increment the fallback counter only when the retry gate accepts a new rejected scope.
26. Recording or submission faults never replay the graphics graph into a partially recorded command buffer.
27. Fatal faults discard projected state, clear cached variants, and leave global frame-abandonment/fence safety with the renderer.
28. The acquired image is first consumed by the terminal graphics submission, which alone waits `imageAvailable`.
29. Non-terminal submissions are completed before the terminal frame fence is reset.
30. Submitted segment counters increment only after a successful `QueueSubmit`.
31. Terminal wait values exposed to the renderer are already rebased to the current absolute timeline domain.
32. Planned, emitted, ownership-transfer, byte, image-subresource, and barrier-record counters retain their current units and update points.
33. `SceneRenderingData.DdgiAsyncComputeEnabled` remains an actual-execution flag, not a request or eligibility flag.
34. Disabled and forced-validation frames do not contribute Auto timing records.
35. Completed timing is consumed only after the reused frame-slot fence and timestamp readback complete.
36. Graphics baselines and async samples retain their current path attribution and checked sums.
37. Queue-busy, overlap, and first-consumer-wait values remain explicitly estimated from pass durations rather than reported as measured queue intervals.
38. Diagnostics published during `DrawScene` retain planned/executed state; the post-submit segment-count patch remains at the current `EndFrame` boundary.
39. The coordinator never retains `RenderSettings` or `SceneRenderingData` after a synchronous planning call.
40. No new steady-frame delegate, LINQ pass beyond the current behavior, lock, reflection, file I/O, logging, Vulkan query, or unbounded collection is introduced.
41. Public async settings, enums, diagnostics, capture, scheduler, and serialization contracts remain source- and schema-compatible.
42. Unrelated dirty working-tree changes are preserved.

## 5. Chosen architecture

### 5.1 Stateful pipeline coordinator

Add `Njulf.Rendering/Pipeline/AsyncComputeCoordinator.cs` with this conceptual surface:

```csharp
internal sealed class AsyncComputeCoordinator
{
    internal AsyncComputeCoordinator(
        RenderGraph renderGraph,
        int framesInFlight);

    internal bool RequiresConcreteResourceBindings(
        AsyncComputeMode requestedMode,
        bool independentQueueAvailable,
        bool timelineSemaphoreAvailable);

    internal void ConsumeCompletedTiming(
        int frameIndex,
        FrameTimingSnapshot timings);

    internal void BeginFrame(in AsyncComputeFrameBoundaryInput input);

    internal AsyncComputeFramePlan PlanFrame(
        in AsyncComputePlanningInput input);

    internal AsyncComputeRecordingDecision ValidateForRecording(
        AsyncComputeFramePlan plan,
        ulong currentResourcePlanGeneration);

    internal ulong ResolveTimelineValue(ulong relativeValue);

    internal void RegisterComputeSegment(
        int segmentId,
        AsyncComputePath path,
        ulong commandBufferIdentity);

    internal AsyncComputeRecordingPublication CommitRecording(
        AsyncComputeFramePlan plan,
        in AsyncComputeRecordingSummary summary);

    internal void RecordSubmittedNonTerminalSegment(
        AsyncComputeQueue queue,
        long cpuSubmitMicroseconds);

    internal void CaptureTiming(
        int frameIndex,
        AsyncComputeFramePlan plan,
        in AsyncComputeTimingCaptureInput input);

    internal AsyncComputeSubmissionPatch CompleteTerminalSubmission(
        int frameIndex,
        long totalCpuSubmitMicroseconds);

    internal void ResetTimingHistory(
        AsyncComputeTimingResetKind kind);

    internal void LatchEmergencyFallback(string reason);
    internal void AbortFrame(int frameIndex);

    internal AsyncComputeDiagnosticsSnapshot CreateDiagnosticsSnapshot(
        FrameTimingSnapshot completedTimings,
        in AsyncComputeDiagnosticsContext context);

    internal AsyncComputeFramePlan? CurrentPlan { get; }
    internal IReadOnlyList<AsyncComputeTimelineWait> TerminalWaits { get; }
}
```

Names can be adjusted to project conventions, but phase boundaries must remain explicit. Do not replace the surface with `Update(VulkanRenderer renderer)` or a generic property bag.

The coordinator borrows `RenderGraph` because pass inventory, resource usages, concrete binding state, resource-plan validation, and graph diagnostics are its focused pipeline inputs. It does not initialize, dispose, or register passes on the graph.

### 5.2 Ownership moved into the coordinator

Move these current renderer fields:

- `_asyncComputeScheduler`;
- `_asyncComputeTimingPolicy`;
- `_asyncComputeTimingFrames`;
- `_asyncComputePlanRecordedThisFrame`;
- submitted graphics/compute segment counts;
- terminal timeline waits;
- planned/emitted barrier counts;
- ownership-transfer, barrier-record, byte, and image-subresource counters;
- `_nextAsyncComputeTimelineValue` and `_asyncComputeTimelineOffsetThisFrame`;
- `_nextAutoTimingProbePath` and `_lastAsyncComputeTimingMode`;
- `_frameAsyncComputePlan`;
- `_asyncComputeStateProjection`;
- `_asyncComputePlanVariantCache`;
- `_asyncComputeValidationLedger`;
- `_asyncComputeRecoverablePlanRetryGate`;
- emergency latch, last fallback reason, fallback count, and validation-error baseline.

The fixed capacities remain 120 timing samples, 4,096 projected bindings, 16 cached variants, 32 validation segments, and 64 validation events unless the existing constructors define them differently. Inject `framesInFlight` rather than duplicating the renderer constant inside the coordinator.

`_frameAsyncComputeSubmissionPlan` currently has only reset/assignment usages. Use semantic Find Usages and safe-delete it instead of moving dead state.

`_lastAsyncComputeSubmitMicroseconds` is currently reset and accumulated but not published. Preserve it inside the coordinator until semantic usage review confirms it can be safe-deleted as a separate cleanup; do not add a public/schema field merely to justify it.

### 5.3 Ownership retained by the renderer

Keep these fields and effects with their current owners:

- `_asyncComputeResourcePlanGeneration`, `_asyncComputeResourcePlan`, and the resource identity records used to avoid rebuilding concrete bindings;
- `_deferredAsyncSubmissions` and `DeferredAsyncSubmission`, because they contain live Vulkan command buffers awaiting submission;
- `_asyncComputePassNameScratch`, because it is the renderer's allocation-free `RenderGraph.ExecuteSelected` predicate state;
- `_lastQueueSubmitMicroseconds`, because it includes terminal graphics and fence-recovery submission costs outside async coordination;
- command buffers, command pools, queues, semaphore handles, fences, swapchain image state, timestamps, and presentation state;
- `MarkFrameSubmissionFault`, `TryRecoverFrameFenceAfterTerminalSubmitFailure`, and the global device-lost/frame-in-progress state.

The renderer should retain narrow methods with names such as:

```csharp
private void EnsureAsyncComputeResourceBindings();

private AsyncComputeFramePlan ExecuteAsyncComputeFrame(
    AsyncComputeFramePlan plan,
    SceneRenderingData sceneData);

private bool RecordRenderGraphFromAsyncPlan(
    AsyncComputeFramePlan plan,
    SceneRenderingData sceneData,
    out string fallbackReason);

private unsafe void SubmitRecordedAsyncSegments();
```

These are Vulkan effect adapters. They should contain no Auto selection, retry-scope policy, fallback text construction, diagnostics assembly, timing-window decisions, or cache-key logic.

### 5.4 Internal contracts

Place small contracts in `AsyncComputeCoordinatorContracts.cs` if keeping them beside the class would make the coordinator file difficult to review.

`AsyncComputeFramePlan` is the relocated private wrapper:

```csharp
internal sealed record AsyncComputeFramePlan(
    AsyncComputeMode RequestedMode,
    AsyncComputeMode EffectiveMode,
    bool Supported,
    AsyncComputeSubmissionPlan SubmissionPlan,
    RenderGraphDiagnostics GraphDiagnostics,
    string Status)
{
    internal bool Requested => RequestedMode != AsyncComputeMode.Disabled;
    internal bool Enabled => SubmissionPlan.ContainsAsyncCompute;
    internal bool IsPathActive(AsyncComputePath path);
    internal IReadOnlyList<string> CandidatePasses { get; }
    internal IReadOnlyList<string> EnabledPasses { get; }
    internal int QueueOwnershipTransitionCount { get; }
}
```

Move the current computed-property expressions without optimizing or changing allocation behavior in the mechanical phase.

`AsyncComputeFrameBoundaryInput` should contain only boundary evidence:

- frame serial;
- current Vulkan validation error count.

The coordinator already owns whether the preceding frame actually submitted compute segments; it evaluates that state before resetting current-frame counters. The explicit input documents the ordering contract without duplicating coordinator-owned state.

`AsyncComputePlanningInput` may borrow:

- `RenderSettings` and `SceneRenderingData` for the synchronous call;
- frame index and clamped timing frame number;
- queue capabilities and timeline-semaphore availability;
- device and driver identity strings;
- far-field bake-pending and bloom-mip external facts;
- an optional one-queue completion-domain constraint reason.

Do not pass `VulkanContext`, `CommandBufferManager`, render targets, far-field manager, DDGI manager, particle managers, or the renderer itself.

`AsyncComputeRecordingSummary` should be a value containing the results the renderer measured while recording:

- emitted acquire/release counts;
- ownership-transfer count;
- barrier-record microseconds;
- transferred bytes and image subresources;
- the terminal segment identity/waits are derivable from the immutable plan and need not be duplicated.

`AsyncComputeRecordingPublication` returns values that must be copied to `SceneRenderingData`, including ownership-transition counts and the exact graph-barrier summary text.

### 5.5 Planning pipeline

`PlanFrame` should preserve the current statement order:

1. Clear timing state for scene/camera or requested-mode transitions.
2. Return the exact disabled plan immediately when requested mode is `Disabled`.
3. Resolve requested versus emergency-latched effective mode.
4. Derive support and settings signature.
5. Begin projected state under the current existing condition.
6. Establish the stable relative timeline offset.
7. Observe the graph-generation/settings retry scope.
8. Build per-path requested/activity/timing decisions.
9. Select at most one Auto enabled path or isolated probe.
10. Build path eligibility and render-graph pass requests.
11. Return the generation-scoped graphics fallback when the retry gate blocks this scope.
12. Build the full variant key and use the accepted cached plan when available.
13. Otherwise call `AsyncComputeScheduler.Compile` with `FirstTimelineValue: 1UL`.
14. Apply projection-capacity and projected-state validation.
15. Cache only a newly compiled accepted plan.
16. Record recoverable rejection or validated scope.
17. Build graph diagnostics and exact status text.
18. Apply the optional exact-receiver one-queue constraint without modifying retry state.
19. Store and return one `AsyncComputeFramePlan`.

`RequiresConcreteResourceBindings` is called immediately before `PlanFrame` and must implement only the audited resource-refresh predicate. The two calls are one synchronous renderer transaction; no settings mutation or frame interleave is permitted between them.

### 5.6 Pass catalog consolidation

Add focused mapping APIs beside `AsyncComputePassCatalog`, for example:

```csharp
internal static bool TryGetPath(
    string passName,
    out AsyncComputePath path);

internal static string GetRepresentativePass(
    AsyncComputePath path);
```

Keep these mappings internal unless an existing external consumer proves a public surface is required; do not widen them merely for convenience. Use the mappings from the current renderer switches exactly. Add a test that every mapped pass is present in the audit catalog and every production candidate expected by a path maps to exactly one atomic path.

The startup `SupportsAsyncCompute` classification audit can remain in renderer initialization or become a small coordinator/catalog validation call. It must execute at the same point after pass registration and before render-graph initialization.

### 5.7 Recording authorization and recoverable fallback

Before any split command is recorded, call `ValidateForRecording`.

It owns these pre-record checks:

- accepted plan with actual compute segments;
- exact current concrete binding generation;
- no swapchain access before the terminal graphics segment.

For stale generation:

- discard projected state at the current boundary;
- call `RenderGraphResourceBindings.RecordStalePlanRejection()`;
- record the recoverable retry scope/reason;
- return a graphics-fallback `AsyncComputeFramePlan`.

For early swapchain access, preserve the exact segment id, queue, pass-list, and acquired-image wording, record the recoverable scope, and return a fallback plan.

For either rejection, `ValidateForRecording` stores the returned fallback as `CurrentPlan`. The renderer then uses the decision's effective plan, sets `DdgiAsyncComputeEnabled` to zero, ensures the swapchain color attachment, and executes the original graphics-only graph. The coordinator does not execute that fallback itself.

### 5.8 Recording commit and execution bookkeeping

The renderer's split-recording loop retains:

- command-buffer selection/begin/end;
- graphics viewport/scissor setup;
- GPU timestamp queue begin;
- acquire/release barrier emission through `QueueOwnershipTransferRecorder`;
- swapchain color-attachment transition;
- selected render-graph pass execution;
- deferred Vulkan command-buffer storage.

It delegates state updates:

1. Register compute segments with the coordinator's validation ledger before pass recording.
2. Accumulate barrier counts/timing into a local `AsyncComputeRecordingSummary`.
3. After every segment records, call `CommitRecording`.
4. `CommitRecording` commits projected state, then concrete owner/layout state, derives resolved terminal waits, advances the absolute timeline domain, publishes counters/current-plan state, and returns scene-data values.
5. Only then call `RenderGraph.CompleteSplitExecution`.

If projection commit fails, the renderer marks the global frame submission fault and abandons the frame. If recording throws after graph work was appended, the renderer must not replay the graphics graph. It calls coordinator abort/latch through the existing single `MarkFrameSubmissionFault` path and rethrows.

Do not have both the renderer catch and coordinator increment the fatal fallback count for the same failure.

### 5.9 Queue submission integration

`SubmitRecordedAsyncSegments` remains unsafe renderer code because it owns live command buffers and calls Vulkan. Replace its direct field access with:

- `ResolveTimelineValue` for waits/signals;
- `RecordSubmittedNonTerminalSegment(queue, elapsedMicroseconds)` only after each successful non-terminal submit;
- `TerminalWaits` for the terminal graphics submit;
- `AbortFrame` when the global fault path clears unsubmitted command buffers/timing state.

The renderer continues to accumulate every queue submit into `_lastQueueSubmitMicroseconds`. After a successful terminal graphics submit it calls:

```csharp
AsyncComputeSubmissionPatch patch =
    _asyncComputeCoordinator.CompleteTerminalSubmission(
        _currentFrame,
        _lastQueueSubmitMicroseconds);
```

`CompleteTerminalSubmission` increments the terminal graphics segment exactly once when an async split plan was recorded, finalizes the timing-frame CPU costs, and returns the final submitted graphics/compute counts. It is called only after the terminal `QueueSubmit` succeeds. Apply the patch through `RendererDiagnosticsAssembler.ApplyAsyncSubmission` if that extraction has landed; otherwise preserve the current narrow renderer `with` patch until it does.

### 5.10 Timing ownership

Move `AsyncComputeTimingFrame`, timing-key creation, workload identity formatting, selection/probe helpers, capture/finalize/completed-frame methods, and the three estimate helpers into the coordinator.

The renderer call sites become:

```csharp
_asyncComputeCoordinator.ConsumeCompletedTiming(
    _currentFrame,
    _gpuTimestamps.LastCompletedSnapshot);

_asyncComputeCoordinator.CaptureTiming(
    _currentFrame,
    framePlan,
    timingCaptureInput);

_asyncComputeCoordinator.CompleteTerminalSubmission(
    _currentFrame,
    _lastQueueSubmitMicroseconds);
```

The exact workload identity components and invariant-culture formatting remain unchanged. Device and driver strings arrive as values from renderer input so the coordinator does not query `VulkanContext` or depend on `PerformanceCaptureMetadataProvider`.

### 5.11 Diagnostics snapshot

Define one immutable snapshot containing coherent async state:

```csharp
internal sealed record AsyncComputeDiagnosticsSnapshot(
    AsyncComputeFramePlan Plan,
    bool IndependentQueueAvailable,
    bool DedicatedQueueFamilyAvailable,
    uint GraphicsQueueFamily,
    uint ComputeQueueFamily,
    int PlannedGraphicsSegments,
    int PlannedComputeSegments,
    int SubmittedGraphicsSegments,
    int SubmittedComputeSegments,
    int PlannedReleaseBarriers,
    int PlannedAcquireBarriers,
    int EmittedReleaseBarriers,
    int EmittedAcquireBarriers,
    int OwnershipTransfers,
    long BarrierRecordMicroseconds,
    ulong TransferredBytes,
    int TransferredImageSubresources,
    int ValidationFallbackCount,
    string LastFallbackReason,
    ulong ResourcePlanGeneration,
    ulong StalePlanRejectionCount,
    long QueueBusyMicroseconds,
    long EstimatedOverlapMicroseconds,
    long FirstConsumerWaitEstimateMicroseconds,
    IReadOnlyList<AsyncComputePathDiagnostic> Paths,
    IReadOnlyList<AsyncComputeSegmentDiagnostic> Segments);
```

The exact constructor layout is flexible, but it must provide every value currently mapped at renderer lines 9492-9567. Segment timeline values are materialized in the current absolute domain before the snapshot leaves the coordinator.

The snapshot contains no raw queue, semaphore, command-buffer, image, buffer, or fence handle.

### 5.12 Resource-plan boundary

Keep these methods in `VulkanRenderer` for this extraction:

- `CaptureAsyncComputeResourcePlanGeneration`;
- `EnsureAsyncComputeResourceBindings`;
- `AddRenderTargetBinding`;
- `AddAsyncComputeBufferBinding`;
- `AddAsyncComputeBufferRangeBinding`;
- `AddTextureBindings`;
- the typed resource-identity records used by the generation key.

They read a wide set of live managers and Vulkan allocations. Moving them into the coordinator would replace one renderer dependency hub with another and make policy tests require the whole backend.

The renderer calls them only when `RequiresConcreteResourceBindings` says the current mode/capability/latch state needs them. The coordinator then borrows `RenderGraph.ConcreteResourceBindings` for planning and projection.

A future `AsyncComputeResourcePlanProvider` may extract this inventory behind a typed resource snapshot. That is deliberately deferred.

### 5.13 Coordination with the other extraction plans

**RendererDiagnosticsAssembler**

Its plan currently treats `VulkanRenderer.AsyncComputePlan` as private and requests a diagnostics-only projection. Once the coordinator extraction legitimately moves that plan to `AsyncComputeFramePlan`, feed `AsyncComputeDiagnosticsSnapshot` into `RendererDiagnosticsExecutionInput`. Keep `ApplyAsyncSubmission` at `EndFrame`; do not let the coordinator construct `RendererDiagnostics`.

**PerformanceCaptureMetadataProvider**

The coordinator needs stable device/driver strings for Auto timing keys but no capture hashes, commit/worktree identity, file I/O, or provider dependency. Renderer input may source those two strings from the provider after it lands, provided their values remain identical.

**ShadowFramePlanner**

Directional shadow and DDGI preparation complete before async planning. The coordinator consumes the resulting `SceneRenderingData` activity/resource facts and must not call the shadow planner or mutate its frame plan.

**DebugOverlayBuilder**

Debug overlay preparation also completes before async plan compilation. The coordinator sees only render-graph pass activity; neither class depends on the other.

Because `VulkanRenderer.cs`, pipeline files, tests, and existing implementation plans contain unrelated working-tree changes, implementation must use narrow patches and semantic usage tools. Never replace, regenerate, or revert the whole renderer file.

### 5.14 Exact symbol relocation map

Move these existing symbols into `AsyncComputeCoordinator`, adapting parameters to the typed contracts without changing their statement order:

| Current renderer symbol | Target responsibility |
| --- | --- |
| `BuildAsyncComputePlan` | `PlanFrame` orchestration. |
| `CreateDisabledAsyncComputePlan` | Disabled-plan builder. |
| `CreateGraphicsFallbackAsyncPlan` | Constraint/pre-record graphics fallback conversion. |
| `CreateGenerationScopedGraphicsFallbackAsyncPlan` | Retry-gated fallback builder. |
| `CreateAsyncComputePlanVariantKey` | Stable relative-domain cache key. |
| `ProjectAsyncComputeSubmission` | Projected transfer/pass final state. |
| `CommitAsyncComputeProjection` | Recording commit transaction. |
| `ComputeAsyncComputeSettingsSignature` and `MixAsyncComputeSettingsSignature` | Retry/cache settings identity. |
| `IsAsyncComputePathFeatureActive` | Path activity policy over borrowed frame/settings inputs. |
| `GetAsyncComputeTimingDecision` | Timing/certification gate. |
| `SelectAutoEnabledPath` and `SelectAutoTimingProbe` | One-path Auto selection. |
| `HasCompleteAsyncResourcePlan` | Focused borrowed `RenderGraph` validation. |
| `CreateAsyncComputeTimingKey` | Device/driver/workload/path timing identity. |
| `DescribeInactiveAsyncComputePath` and `BuildAsyncComputeWorkloadIdentity` | Exact status and timing workload text. |
| `CaptureAsyncComputeTimingFrame` | Current-frame timing-ring capture. |
| `FinalizeAsyncComputeTimingFrame` | Terminal CPU-cost finalization. |
| `RecordCompletedAsyncComputeTimingFrame` | Fence-complete feedback ingestion. |
| `ResolveAsyncTimelineWait` and `ResolveAsyncTimelineValue` | Relative-to-absolute timeline domain. |
| `RecordRecoverableAsyncComputePlanFallback`, `ObserveAsyncComputePlanRetryScope`, and `RecordValidatedAsyncComputePlan` | Retry-scope lifecycle. |
| `LatchAsyncComputeEmergencyFallback` | Permanent async emergency state; invoked once by the renderer fault owner. |
| `RecordSubmittedAsyncComputeSegment` | Successful segment counters, split between non-terminal and terminal APIs. |
| `IsDdgiAsyncComputeActuallyEnabled` overloads | `AsyncComputeFramePlan.IsPathActive`. |
| `EstimateAsyncComputeQueueBusyMicroseconds`, `EstimateAsyncComputeOverlapMicroseconds`, `EstimateAsyncComputeFirstConsumerWaitMicroseconds`, and `SumGpuPassMicroseconds` | Timing/diagnostics estimates. |
| nested `AsyncComputeTimingFrame` | Private coordinator timing-slot state. |
| nested `AsyncComputePlan` | Internal `AsyncComputeFramePlan` contract. |

Move `GetAsyncComputePath` and `GetRepresentativeAsyncPass` to `AsyncComputePassCatalog` as described above.

Keep these symbols in `VulkanRenderer`, with narrow renames or body reduction where indicated:

| Current renderer symbol | Retained responsibility |
| --- | --- |
| `ExecuteRenderGraphWithAsyncPlan` | Rename to `RecordRenderGraphFromAsyncPlan`; retain Vulkan command recording and graph execution effects. |
| `SubmitDeferredAsyncSubmissions` | Rename to `SubmitRecordedAsyncSegments`; retain unsafe non-terminal `QueueSubmit` calls. |
| `IncludeAsyncComputePass` | Allocation-free selected-pass predicate over renderer scratch state. |
| `CaptureAsyncComputeResourcePlanGeneration` | Live manager/allocation generation capture. |
| `EnsureAsyncComputeResourceBindings` | Concrete resource inventory, plan creation, and activation. |
| `AddRenderTargetBinding`, `AddAsyncComputeBufferBinding`, `AddAsyncComputeBufferRangeBinding`, and `AddTextureBindings` | Resource binding adapters. |
| `ToLegacyPipelineStage` | Vulkan 1.0 submit-stage conversion used by renderer submissions. |
| `MarkFrameSubmissionFault` and `TryRecoverFrameFenceAfterTerminalSubmitFailure` | Global frame/device/fence safety. |
| nested `DeferredAsyncSubmission` | Live command buffer plus immutable segment awaiting submission. |

Replace `UpdateAsyncComputeSubmissionDiagnostics` with the coordinator submission patch plus the diagnostics assembler integration. Do not move it as a method that mutates `_lastDiagnostics` from inside the coordinator.

## 6. Delivery order and change isolation

### Phase 0: Freeze coordination behavior

1. Add characterization helpers that compare complete async frame plans, path statuses, segment waits/signals, fallback text, and diagnostics fields.
2. Add sequence tests around scene/mode/recreation timing resets, Auto probe rotation, retry scopes, validation quarantine, timeline rebasing, and late submitted-count publication.
3. Record graphics-only, Auto baseline/probe/promoted, forced validation, recoverable fallback, and emergency-latched runtime snapshots.
4. Capture allocation and disabled-mode call-count baselines for concrete resource refresh and scheduler compilation.

Exit gate: tests detect a reordered reset, extra resource refresh, changed retry scope, changed timeline value, changed sample attribution, or changed diagnostics field.

### Phase 1: Add contracts and catalog mappings

1. Add `AsyncComputeCoordinatorContracts.cs` and `AsyncComputeCoordinatorTests.cs`.
2. Rename the private nested `AsyncComputePlan` to `AsyncComputeFramePlan` with semantic rename, then move it to the pipeline namespace and update all IDE-resolved usages.
3. Add pass-to-path and representative-pass APIs to `AsyncComputePassCatalog` and retarget the renderer switches semantically.
4. Add boundary, planning, recording-summary, diagnostics, and submission-patch records without changing runtime ownership yet.
5. Semantic-safe-delete `_frameAsyncComputeSubmissionPlan` only after confirming it has no read usage.

Exit gate: contracts compile, catalog tests cover every current mapping, and the renderer still produces plans exactly as before.

### Phase 2: Move timing and pure policy

1. Create `AsyncComputeCoordinator` with scheduler/timing policy and frame-slot timing storage.
2. Move settings signature, workload identity, timing key, path activity/status, Auto selection/probe, timing capture/completion, and estimate helpers.
3. Replace target/swapchain timing clears with typed `ResetTimingHistory` calls at the same lifecycle points.
4. Keep settings/scene references synchronous and verify they are not retained.
5. Move `IsDdgiAsyncComputeActuallyEnabled` to `AsyncComputeFramePlan.IsPathActive` usage.

Exit gate: timing-policy and new coordinator sequence tests pass, including reset/probe distinctions and forced/disabled exclusions.

### Phase 3: Move planning, cache, projection, and fallback state

1. Move scheduler, variant cache, projected state, retry gate, validation ledger, timeline domain, and fallback fields into the coordinator.
2. Move `BuildAsyncComputePlan`, disabled/scoped fallback builders, variant-key construction, projection staging/commit, and fallback-latch methods.
3. Keep concrete resource refresh in the renderer behind `RequiresConcreteResourceBindings`.
4. Replace the exact receiver-feedback local fallback with the typed graphics-only constraint input.
5. Make the coordinator the only owner of the current plan.

Exit gate: the full plan value, cache hit/miss behavior, retry counter/reason, projection state, and fallback class match the baseline.

### Phase 4: Wire recording and submission bookkeeping

1. Rename the remaining renderer execution methods to names that describe Vulkan effects rather than policy.
2. Route stale/early-swapchain preflight through `ValidateForRecording`.
3. Accumulate a local recording summary and publish it once through `CommitRecording`.
4. Replace direct timeline/counter/wait/fallback-field access with coordinator APIs.
5. Preserve the exact order of projection commit, concrete owner/layout commit, timeline advancement, scene-data publication, and split-execution completion.
6. Route global frame faults through one coordinator latch/abort call to avoid double increments.

Exit gate: renderer async methods contain Vulkan recording/submission and scene publication only; no scheduler, Auto, cache, retry, timing-window, or diagnostics policy remains.

### Phase 5: Integrate diagnostics and lifecycle

1. Replace async field mapping in `BuildDiagnostics` or `CaptureDiagnosticsInput` with `CreateDiagnosticsSnapshot`.
2. Preserve the defensive current-plan resolution as `_asyncComputeCoordinator.CurrentPlan ?? PlanFrame(CaptureAsyncComputePlanningInput(sceneData))`; normal `DrawScene` must still plan exactly once, while the previously supported null-plan fallback remains available.
3. Apply `AsyncComputeSubmissionPatch` at the existing successful terminal-submit boundary.
4. Retarget `BeginFrame`, `DrawScene`, `EndFrame`, render-target recreation, swapchain recreation, and global frame-fault call sites.
5. Verify no diagnostics/capture schema or requested/effective semantics changed.

Exit gate: diagnostics before and after `EndFrame` differ only in the same late submitted-count fields as today.

### Phase 6: Semantic audit and runtime validation

1. Use IDE semantic Find Usages for every moved field/method/type before deleting old declarations.
2. Use semantic rename for `AsyncComputePlan` and any renderer execution method renames; use safe-delete for confirmed dead fields only.
3. Confirm all current async downstream consumers read coordinator plan/snapshot data and no compatibility wrapper remains.
4. Run focused tests, full tests, solution build, validation-layer scenarios, and queue-topology runtime cases.
5. Compare plan, submission, timings, diagnostics, and performance-capture output with the Phase 0 baseline.

Exit gate: no duplicated policy/state remains in `VulkanRenderer`, no new public API/schema exists, and graphics/async runtime behavior is unchanged.

## 7. File-level implementation map

### New files

`Njulf.Rendering/Pipeline/AsyncComputeCoordinator.cs`

- Stateful composition of scheduler, timing policy, cache, projection, validation ledger, retry gate, timeline domain, fallback state, frame counters, and current plan.
- Plan construction, recording authorization, commit publication, timing feedback, and diagnostics snapshot creation.

`Njulf.Rendering/Pipeline/AsyncComputeCoordinatorContracts.cs`

- `AsyncComputeFramePlan`.
- Boundary/planning/timing inputs.
- Recording decision/summary/publication.
- Diagnostics snapshot and terminal submission patch.
- Timing reset kind if needed to preserve the two reset behaviors.

`Njulf.Tests/AsyncComputeCoordinatorTests.cs`

- Stateful sequencing, plan integration, fallback classification, timeline, timing-ring, and diagnostics tests.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Construct one coordinator after assigning the borrowed render graph; completed timestamp snapshots are passed later through method inputs rather than retained as a dependency.
- Remove moved fields, nested plan/timing types, policy helpers, estimates, and direct diagnostics mapping.
- Retain concrete resource inventory and Vulkan execution/submission shells.
- Capture typed planning inputs and apply coordinator publications.
- Delegate frame/timing/reset/fault lifecycle events.

`Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs`

- Add the canonical pass-to-path and representative-pass mappings.

`Njulf.Tests/AsyncComputePhase3Tests.cs`

- Retain scheduler/resource tests and add catalog integration coverage where it fits existing fixtures.

`implementation/RendererDiagnosticsAssemblerExtractionPlan-20260825.md`

- When implementing both plans, update its integration note from the renderer-private wrapper to `AsyncComputeDiagnosticsSnapshot`; do not edit that plan merely to create this proposal.

### Files expected to remain behavior/schema stable

- `Njulf.Rendering/Data/AsyncComputePolicy.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Rendering/Pipeline/AsyncComputeScheduler.cs`
- `Njulf.Rendering/Pipeline/AsyncComputeStateContracts.cs`
- `Njulf.Rendering/Pipeline/AsyncComputeRecoverablePlanRetryGate.cs`
- `Njulf.Rendering/Pipeline/QueueOwnershipTransferRecorder.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResourceBindings.cs`
- `Njulf.Rendering/Diagnostics/AsyncComputeTimingPolicy.cs`
- `Njulf.Rendering/Core/VulkanContext.cs`
- `Njulf.Rendering/Core/CommandBufferManager.cs`
- `Njulf.Rendering/Debugging/GpuTimestampRecorder.cs`
- public renderer diagnostics and performance-snapshot records/codecs;
- render-pass declarations, shaders, queue barriers, and settings serialization.

If the extraction requires a behavioral change in one of these files, isolate it as a separately reviewed change rather than folding it into the move.

## 8. Test matrix

### Mode, capability, and resource gating

- Requested Disabled returns the exact status/path plan without resource refresh, projection begin, cache lookup, or scheduler compile.
- Auto and Force distinguish requested/effective mode when emergency fallback is latched.
- Independent same-family and dedicated-family compute queues are both supported; a single queue is unsupported.
- Missing timeline semaphore is unsupported even with an independent queue.
- Concrete bindings refresh only for supported effective non-disabled frames.
- Unsupported/effective-mode projection and timeline-offset behavior matches the audited baseline.

### Path eligibility and Auto isolation

- Every path's setting, feature isolation, feature activity, certification, quarantine, and resource completeness affect the same status/reason as today.
- Simple DDGI sampled-atlas, fog froxel, fog debug/bloom, AO depth/radius, far-field bake, Hi-Z, and particle zero-work edge cases are covered.
- At most one Auto path is active/probed.
- Best-benefit ordering and enum tie-break are stable.
- Probe rotation skips uncertified, disabled, inactive, timing-ineligible, and incomplete paths.
- Paused paths retain the exact warmup/reason behavior.
- Force mode admits only the configured authorized path and still fails closed on missing resources/capabilities.

### Plan variants and projection

- Every variant-key component changes the key independently.
- Absolute frame timeline does not change the cache key.
- Cache hits reuse immutable relative waits/signals without cloning.
- Rejected plans are never cached.
- Projection-capacity failure converts an otherwise accepted async plan to the exact rejection.
- Transfer and pass-final-state projection failures discard staged state.
- Successful recording commits projection and binding owner/layout state in the existing order.
- Constrained/fallback/failed frames never publish staged state.

### Fallback and validation state

- Scheduler rejection records one recoverable fallback for a graph/settings scope.
- Repeating the same rejected scope does not repeatedly increment the counter.
- Resource generation or settings signature change re-enables validation.
- Exact receiver-feedback one-queue constraint does not poison the retry gate or increment its rejection count.
- Stale resource generation increments the binding rejection counter and uses exact fallback text.
- Early non-terminal swapchain access uses exact segment/queue/pass detail.
- Validation errors after a frame with submitted compute latch permanent fallback.
- Validation errors after graphics-only frames do not trigger the async latch.
- Fatal recording/submission faults clear projection/cache and increment fallback state exactly once.
- Resize, scene reload, settings changes, and successful frames do not clear the permanent latch.

### Timeline and frame bookkeeping

- Relative zero remains zero; nonzero waits/signals add the frame offset with checked arithmetic.
- The first frame starts at timeline value one.
- Multiple segments and multiple frames advance monotonically to one past the largest signal.
- Cache hits rebase to a new absolute domain without changing the cached plan.
- Terminal waits come only from the terminal graphics segment and contain absolute values.
- Begin-frame resets all per-frame counters/waits/plan state but not the next timeline value or permanent fallback state.
- Planned/emitted/ownership/byte/subresource counters match the recording summary.
- Submitted counters increment only after successful submits and terminal graphics is counted only after its successful submit.

### Timing state

- Scene payload, Hi-Z scene, camera cut, and mode changes clear policy, in-flight frames, and probe cursor.
- Render-target/swapchain recreation preserves its current probe-cursor distinction.
- Disabled and Force modes leave the timing-frame slot empty.
- Auto graphics baselines include every requested active feature path key.
- Async samples include active paths only.
- Non-positive completed GPU time produces no sample.
- Checked total pass sum and overlap subtraction match the baseline.
- CPU total queue-submit and barrier costs attach only after terminal success.
- Frame-slot reuse consumes and clears exactly one matching timing record.

### Diagnostics integration

- Snapshot requested/effective/support/status fields match the full plan.
- Candidate/enabled pass arrays and evidence revisions are unchanged.
- Path diagnostics preserve every boolean/status/reason/pass list.
- Segment diagnostics contain absolute wait/signal values and exact barrier/swapchain/terminal flags.
- Queue topology, generation, stale rejection, planned/emitted/submitted counts, bytes/subresources, and fallback fields match the baseline.
- Queue-busy, overlap, and first-consumer-wait estimates match existing helpers.
- The `EndFrame` patch changes only submitted graphics/compute counts.
- Diagnostics/capture JSON property names and types remain unchanged.

### Runtime matrix

Exercise at minimum:

| Case | Required evidence |
| --- | --- |
| Disabled | Original one-queue graph, no async resource refresh or timing sample. |
| Auto warmup | Graphics baselines and one rotating isolated probe. |
| Auto promoted | One active path, correct transfers/waits, attributable timing. |
| Auto regression | Timing-policy demotion and graphics-only execution. |
| Force validation | Only the explicitly authorized path, full safety validation. |
| Independent queue in graphics family | Memory/layout dependencies without queue-family ownership transfer. |
| Dedicated compute family | Paired release/acquire ownership transfers. |
| Each of seven paths | Correct feature activity, pass group, first consumer, and diagnostics. |
| Cached versus regenerated plan | Same segment structure with newly rebased absolute timeline values. |
| Resize/resource regeneration | New plan scope, no stale cached binding reuse. |
| Exact receiver-feedback capture | One graphics completion domain without retry poisoning. |
| Stale/invalid plan before recording | Recoverable graphics fallback. |
| Validation error after compute | Permanent emergency fallback. |
| Injected non-terminal submit failure | Frame abandonment, safe global fault, no terminal reuse race. |

## 9. Verification commands

Run focused async tests:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~AsyncComputeCoordinatorTests|FullyQualifiedName~AsyncComputePhase3Tests|FullyQualifiedName~AsyncComputeStateContractTests|FullyQualifiedName~ProductionRenderPipelineDeclarationTests|FullyQualifiedName~RenderGraphResourceDeclarationTests"
```

Run settings, diagnostics, full tests, and the solution build:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~RenderSettingsFileIoTests|FullyQualifiedName~GiFeatureStateFactoryTests|FullyQualifiedName~MaterialGiRolloutPolicyTests"
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore
dotnet build Njulf.sln --no-restore
```

Audit moved ownership and usages:

```powershell
rg -n "_asyncComputeScheduler|_asyncComputeTimingPolicy|_asyncComputeTimingFrames|_asyncComputeStateProjection|_asyncComputePlanVariantCache|_asyncComputeValidationLedger|_asyncComputeRecoverablePlanRetryGate|_nextAsyncComputeTimelineValue|_asyncComputeEmergencyFallbackLatched" Njulf.Rendering/VulkanRenderer.cs
rg -n "BuildAsyncComputePlan|CreateGraphicsFallbackAsyncPlan|CreateGenerationScopedGraphicsFallbackAsyncPlan|GetAsyncComputeTimingDecision|SelectAutoTimingProbe|ResolveAsyncTimelineValue|EstimateAsyncCompute" Njulf.Rendering/VulkanRenderer.cs
rg -n "AsyncComputeCoordinator|AsyncComputeFramePlan|AsyncComputeDiagnosticsSnapshot" Njulf.Rendering Njulf.Tests
rg -n "QueueSubmit|BeginAsyncComputeCommand|AsyncComputeTimelineSemaphore|ResetAsyncComputeCommandPool" Njulf.Rendering/Pipeline/AsyncComputeCoordinator.cs
```

The first two searches should return no coordinator-owned remnants. The final search should return no Vulkan effect ownership in the coordinator. Review IDE semantic Find Usages as the authority for symbol removal; text searches are a supplementary audit for strings/comments and old private names.

For runtime validation, enable Vulkan validation and record requested/effective mode, path statuses, plan generation, relative and absolute timeline edges, queue segments, planned/emitted transfers, submitted counts, fallback reason/count, queue CPU costs, completed GPU timings, and diagnostics before/after `EndFrame` for every runtime row.

## 10. Risks and mitigations

### Risk: the coordinator becomes another god object

Keep the scheduler, timing policy, projection, cache, validation ledger, retry gate, barrier recorder, resource inventory, and Vulkan execution as existing collaborators. The coordinator sequences them; it does not duplicate their implementations.

### Risk: the disabled hot path starts doing resource or scheduler work

Characterize call counts before the move and retain the early Disabled return plus explicit `RequiresConcreteResourceBindings` predicate.

### Risk: resource effects move into policy code

Keep concrete resource discovery and plan activation in the renderer. Pass no manager or raw Vulkan handle into planning inputs.

### Risk: settings change between preflight and planning

Call resource preflight and `PlanFrame` synchronously with the same settings object at the existing draw boundary. If concurrent settings mutation is introduced later, add an immutable settings snapshot as a separate threading change.

### Risk: retryable and terminal failures are conflated

Use distinct recording decisions and global-fault APIs. Tests must prove exact receiver constraints do not poison retry state and partial recording never replays graphics work.

### Risk: fallback is counted twice

Make the renderer's global `MarkFrameSubmissionFault` the single call that tells the coordinator to latch/abort. Coordinator recording helpers discard locally but do not independently latch the same exception.

### Risk: cached plans reuse absolute timeline values

Keep scheduler/cache values relative, variant timeline base zero, and all rebasing in the coordinator's checked timeline domain.

### Risk: projected resource state commits too early

Retain the all-segments-recorded commit boundary and test staged/committed bindings after every fallback and exception path.

### Risk: terminal image/fence ordering changes

Keep deferred submits, image-available wait, terminal command buffer, fence reset/recovery, screenshots, and present in `VulkanRenderer.EndFrame` in their current order.

### Risk: Auto samples become cross-contaminated

Preserve one-path isolation, graphics baseline keys, Force exclusion, timing-frame slot ownership, and CPU cost finalization after terminal submit.

### Risk: timing reset behavior is over-normalized

Model scene/mode reset separately from target/swapchain reset so the current probe-cursor behavior does not change accidentally.

### Risk: diagnostics read a partly updated state

Create one immutable coordinator snapshot at the diagnostics capture boundary and one explicit terminal patch. Do not let the assembler read coordinator properties repeatedly.

### Risk: pass mapping diverges from the audit catalog

Move both current switches beside `AsyncComputePassCatalog` and add a one-to-one mapping/certification test.

### Risk: the current plan is rebuilt during normal diagnostics

Make the coordinator the only current-plan owner and capture the plan once during normal `DrawScene`. Preserve the existing null-plan defensive fallback, and test that it is not taken on the normal path.

### Risk: broad renderer inputs create a hidden dependency cycle

Borrow only `RenderSettings`, `SceneRenderingData`, and `RenderGraph` synchronously. Pass small external facts for manager-specific state and never pass `VulkanRenderer` or a service locator.

### Risk: concurrent renderer work is overwritten

Use semantic refactoring for the plan type and method renames, narrow patches for lifecycle call sites, and preserve all unrelated dirty files/changes.

## 11. Definition of done

The extraction is complete only when all of the following are true:

- `AsyncComputeCoordinator` exists under `Njulf.Rendering/Pipeline` and has renderer lifetime.
- It owns scheduler/timing/cache/projection/validation/retry/timeline/fallback/frame-counter/current-plan state.
- `AsyncComputeFramePlan` is an internal pipeline contract with unchanged requested/effective/status/diagnostic behavior.
- Pass-to-path and representative-pass mappings have one canonical owner beside `AsyncComputePassCatalog`.
- `VulkanRenderer` no longer contains Auto selection, timing-window feedback, variant-key creation, projected-state policy, retry/fallback builders, timeline-domain state, or async diagnostic estimates.
- Concrete renderer resource inventory remains outside the coordinator and refreshes under the exact current predicate.
- Unsafe command recording/submission, semaphore/command-pool ownership, terminal fence recovery, acquired-image handling, and present remain outside the coordinator.
- Disabled, unsupported, Auto, Force, recoverable-fallback, exact-constraint, and emergency-latched modes produce baseline-equivalent complete plans.
- Timeline values, transfer barriers, resource owner/layout commits, and segment submission order match the baseline.
- Auto timing keys, resets, samples, estimates, and promotion/demotion decisions match the baseline.
- Diagnostics and capture schemas are unchanged, and late submitted-count publication remains at `EndFrame`.
- `_frameAsyncComputeSubmissionPlan` and every other confirmed dead private remnant are semantic-safe-deleted; no compatibility wrapper duplicates policy.
- No new steady-frame allocation, device query, resource scan, lock, or queue operation is introduced by the coordinator boundary.
- Focused tests, full tests, solution build, Vulkan validation cases, and queue-topology runtime matrix pass.
- No unrelated working-tree change is reverted or reformatted.

## 12. Follow-up work explicitly deferred

After the behavior-preserving coordinator extraction is stable, separate work may consider:

- extracting the concrete binding inventory into an `AsyncComputeResourcePlanProvider` driven by typed renderer resource snapshots;
- extracting unsafe segment recording/submission into a Vulkan async-compute executor if another renderer backend needs the same coordinator;
- replacing mutable borrowed settings with an immutable async policy snapshot when settings become concurrently mutable;
- removing confirmed dead async-submit timing state or deliberately publishing it through a reviewed diagnostics-schema change;
- reducing allocations in computed plan diagnostics and per-frame path/pass request construction;
- caching device/driver timing identity through `PerformanceCaptureMetadataProvider`;
- adding precise validation-message-to-segment attribution beyond the current frame-boundary heuristic;
- evaluating multiple simultaneous Auto paths only with new attributable timing evidence;
- changing queue priorities, submission APIs, or synchronization primitives;
- promoting additional passes after correctness and profitability evidence is reviewed.

None of these follow-ups should be mixed into the initial extraction.
