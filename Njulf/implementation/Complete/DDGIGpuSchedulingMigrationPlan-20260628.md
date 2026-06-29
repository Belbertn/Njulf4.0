# DDGI GPU Scheduling Migration Plan - 2026-06-28

## Context

The 2026-06-28 performance snapshot showed GPU timestamp support and GPU timing enabled, but the timestamp data was not valid yet:

- `GpuTimingSupported: 1`
- `GpuTimingEnabled: 1`
- `GpuTimingPending: 1`
- `GpuTimingValid: 0`
- `GpuTimingUnavailableReason: GPU timing is enabled; waiting for a completed frame of timestamp results.`
- `GpuTimingFrameLatency: 2`

All `Gpu*Microseconds: 0` values in that snapshot must be treated as unavailable, not free.

The hard performance loss in that capture was CPU-side DDGI scheduling:

- `CpuTotalDrawSceneMicroseconds: 91309` (91.3 ms)
- `CpuDdgiSchedulerMicroseconds: 67303` (67.3 ms)
- `CpuDdgiSchedulerP95Microseconds: 73741` (73.7 ms)
- `DdgiProbeCount: 23040`
- `DdgiProbesUpdated: 423`

The current CPU scheduler lives primarily in:

- `Njulf.Rendering/Resources/DdgiProbeUpdateScheduler.cs`
- `Njulf.Rendering/Resources/DdgiProbeVolumeManager.cs`

Existing GPU-driven patterns to reuse:

- `Njulf.Rendering/Pipeline/SceneOpaqueCompactionPass.cs`
- `Njulf.Shaders/scene_opaque_compact.comp`
- `Njulf.Rendering/Pipeline/DdgiPipelinePasses.cs`
- `Njulf.Shaders/ddgi_update_shared.glsl`

## Objective

Move the per-probe DDGI scheduling work to the GPU while keeping CPU-side policy, fallback, and validation.

The GPU scheduler must directly produce the DDGI update queue and indirect dispatch arguments. CPU code must not synchronously read back GPU scheduler output to make same-frame decisions.

## Scope

Move to GPU:

- Per-probe feedback aging.
- Per-probe frustum, expanded-frustum, predicted-frustum, and safety-shell classification.
- Dirty-region and dirty-probe eligibility checks.
- Candidate score calculation.
- Duplicate marking.
- Candidate compaction.
- Final update queue emission.
- Indirect dispatch argument generation for DDGI update passes.

Keep on CPU:

- Quality preset and adaptive budget policy.
- GPU-timing based budget scaling.
- Camera-relative clipmap layout, scroll, teleport, and dirty-region source generation.
- DDGI resource allocation and resizing.
- Production fallback path.
- Delayed diagnostics and validation reporting.

## Step 1: Add CPU Scheduler Substep Timers

Instrument `DdgiProbeUpdateScheduler.BuildRequests()` and `DdgiProbeVolumeManager.ScheduleProbeUpdates()` before moving behavior.

Measure:

- Adaptive budget selection.
- Feedback aging.
- Clipmap dirty request generation.
- Dirty region request generation.
- Uninitialized clipmap scan.
- Frustum-focused candidate scoring.
- Safety-shell candidate scoring.
- Round-robin fill.
- Schedule diagnostics build.
- Queue upload.

Acceptance:

- Performance snapshots identify which scheduler substeps contribute to the current 67 ms.
- Existing behavior is unchanged.
- Unit tests continue to pass.

## Step 2: Add Settings And Diagnostics

Add settings:

- `DdgiGpuSchedulingEnabled`
- `DdgiGpuSchedulingValidationEnabled`
- `DdgiGpuSchedulingFallbackOnInvalidCounters`
- `DdgiGpuSchedulerDebugReadbackEnabled`

Initial defaults:

- GPU scheduling disabled.
- Validation disabled.
- Fallback enabled.
- Debug readback disabled.

Add diagnostics:

- `DdgiGpuSchedulingEnabled`
- `DdgiGpuSchedulingActive`
- `DdgiGpuSchedulingFallbackReason`
- `CpuDdgiSchedulerMicroseconds`
- `GpuDdgiSchedulerMicroseconds`
- `DdgiGpuScheduledProbeCount`
- `DdgiGpuScheduledPrimaryRayCount`
- `DdgiGpuSchedulerOverflowCount`
- `DdgiGpuSchedulerDuplicateRejectCount`
- `DdgiGpuSchedulerBudgetRejectCount`
- `DdgiGpuSchedulerInvalidProbeCount`
- `DdgiGpuSchedulerValidationStatus`
- `DdgiGpuSchedulerValidationMismatchCount`

Acceptance:

- Diagnostics are visible in performance snapshots.
- CPU path remains the default.
- All fields report stable zero or inactive values when GPU scheduling is disabled.

## Step 3: Add GPU Scheduler Buffers

Extend `DdgiProbeVolumeManager` with persistent GPU buffers:

- Scheduler counter buffer.
- Scheduler candidate buffer.
- Scheduler mark buffer.
- Scheduler feedback buffer.
- Scheduler dirty region buffer.
- Scheduler dirty probe request buffer.
- DDGI update indirect dispatch buffer.

Register buffers through the existing bindless heap pattern.

Buffer rules:

- Capacity must be based on active probe count and request budget.
- All buffers need safe minimum sizes.
- Counter and indirect buffers must support transfer readback only for delayed diagnostics and validation.
- The update queue remains `DdgiProbeUpdateQueueBuffer`, but GPU scheduling writes it directly.

Acceptance:

- Buffers resize safely as DDGI probe count changes.
- Resource leak auditor remains clean.
- Bindless index tests and GPU struct layout tests are updated.

## Step 4: Create `DdgiSchedulePass`

Add a compute render graph pass before `DdgiTracePass`.

New production order:

1. `DdgiSchedulePass`
2. `DdgiTracePass`
3. `DdgiBlendPass`
4. `DdgiRelocateClassifyPass`
5. `DdgiPublishPass`

`DdgiSchedulePass` reads:

- DDGI volume metadata.
- Probe state.
- Scheduler feedback.
- Dirty regions.
- Dirty probe requests.
- Frame scheduler constants.

`DdgiSchedulePass` writes:

- Scheduler counters.
- Scheduler marks.
- Candidate buffers.
- `DdgiProbeUpdateQueueBuffer`.
- DDGI indirect dispatch arguments.

Acceptance:

- The pass can run as a no-op and report zero scheduled probes.
- Barriers are explicit and correct.
- CPU fallback still schedules and uploads the update queue when GPU scheduling is inactive.

## Step 5: Move Feedback Aging To GPU

Port `AgeProbeSchedulerFeedback()` to compute first.

This establishes:

- GPU feedback buffer layout.
- Per-probe linear dispatch.
- Counter reset and barrier flow.
- Delayed debug readback path.

Acceptance:

- CPU feedback aging is skipped when GPU scheduling is active.
- Validation mode can compare sampled feedback values after frame latency.
- CPU scheduler output remains unchanged when GPU scheduling is disabled.

## Step 6: Move Round-Robin And Age Refresh Scheduling

Implement the simplest GPU scheduler mode first:

- Scan active probes.
- Respect `updateCursor`.
- Emit age-refresh requests until request and ray budgets are reached.
- Mark duplicates.
- Generate indirect dispatch args.

Acceptance:

- No duplicate scheduled probes.
- No invalid volume or probe indices.
- Scheduled count never exceeds request budget.
- Scheduled primary rays never exceed primary ray budget.
- DDGI trace/blend/relocate can consume the GPU-written update queue.

## Step 7: Switch DDGI Update Passes To Indirect Dispatch

Change `DdgiTracePass`, `DdgiBlendPass`, and `DdgiRelocateClassifyPass` to use indirect dispatch when GPU scheduling is active.

Current direct dispatch pattern:

```csharp
CmdDispatch(cmd, (uint)sceneData.DdgiProbesUpdated, 1, 1);
```

Production GPU scheduling path should use an indirect dispatch buffer written by `DdgiSchedulePass`.

Requirements:

- No same-frame CPU readback.
- Shaders must guard against zero scheduled count.
- CPU path can still use direct dispatch.
- Diagnostics use delayed counters.

Acceptance:

- GPU scheduling path updates probes without CPU queue upload.
- CPU fallback path remains functional.
- GPU timing captures DDGI scheduling and update passes separately when timing is valid.

## Step 8: Move Frustum And Safety Candidate Scoring

Port the logic equivalent to:

- `TryCreateViewCandidate()`
- `EvaluateProbeViewMetrics()`
- `ScoreViewCandidate()`

Use one thread per active probe.

Each probe should:

- Resolve its owning volume.
- Decode physical and logical probe coordinates.
- Compute probe world position.
- Classify current, expanded, predicted, and safety-shell visibility.
- Read scheduler feedback.
- Compute a deterministic score.
- Emit a candidate into a bounded candidate buffer.

Avoid a full global sort. Prefer bounded selection:

- Per-workgroup top-N.
- Merge top candidates to final queue.
- Tie-break using priority, score, and probe index.

Acceptance:

- Validation mode shows matching reason buckets and similar volume distribution versus CPU reference.
- Exact ordering is not required unless debug mode requests strict comparison.
- GPU scheduling quality remains visually stable in DDGI debug views.

## Step 9: Move Dirty Clipmap And Dirty Region Scheduling

Move dirty work after frustum scheduling is validated.

GPU inputs:

- Dirty clipmap logical cell ranges.
- Dirty bounds and reason flags.
- Dirty reason priority mapping constants.

GPU work:

- Expand dirty logical ranges.
- Resolve physical probe indices.
- Emit high-priority dirty/new-cell candidates.
- Reject duplicates through the mark buffer.
- Preserve teleport and layout-change warmup flags.

Acceptance:

- Teleport, large scroll, layout change, dirty geometry, dirty material, emissive, local-light, and directional-light updates schedule correctly.
- No unbounded dirty-region expansion can overflow candidate buffers silently.
- Overflow counters are reported and trigger CPU fallback if configured.

## Step 10: Add CPU/GPU Validation Compare

When `DdgiGpuSchedulingValidationEnabled` is true:

- CPU builds the reference schedule.
- GPU builds the production schedule.
- Compare after frame latency.

Compare:

- scheduled count
- primary ray count
- duplicate count
- invalid probe count
- reason bucket counts
- per-volume scheduled counts
- sample of scheduled probe indices
- dirty/new-cell coverage
- queue overflow and budget rejection counters

Do not require exact probe order by default.

Acceptance:

- Validation fails loudly on invalid indices, duplicates, over-budget output, or missing dirty/new-cell coverage.
- Validation can be enabled in CI smoke scenarios without requiring a visual capture.

## Step 11: Production Fallback Rules

GPU scheduling must fall back to CPU scheduling when:

- Required buffers are missing or undersized.
- GPU scheduler pipeline creation fails.
- Device features are missing.
- Delayed counters report invalid probe indices.
- Delayed counters report duplicate probes.
- Overflow occurs repeatedly.
- Validation mode reports severe mismatch.

Fallback must:

- Use the existing CPU scheduler.
- Upload the CPU queue.
- Report `DdgiGpuSchedulingFallbackReason`.
- Avoid device wait idle.

Acceptance:

- Runtime fallback does not crash.
- Fallback reason appears in performance snapshots.
- User can disable GPU scheduling without restarting if settings are hot-applied.

## Step 12: Benchmarks And Gates

Required scenarios:

- Cold start.
- Steady camera.
- Slow camera movement.
- Fast camera movement.
- Camera cut.
- Teleport.
- Dirty geometry.
- Dirty lighting.
- High-variance probe scene.
- Low-confidence probe scene.
- DDGI disabled.
- Ray query unsupported fallback.

Production completion gates:

- CPU DDGI scheduler time drops from about 67 ms to under 0.5 ms in the captured scenario.
- No same-frame scheduler readback.
- No invalid scheduled probes.
- No duplicate scheduled probes.
- No normal-case queue overflow.
- Scheduled count and primary ray count always respect budgets.
- CPU fallback path remains green.
- GPU timing is valid in benchmark captures.
- DDGI visual debug views remain stable.
- Long-run stability shows no resource leaks or retained staging growth.

## Risks

- Full sorting of all probes could be expensive. Prefer bounded top-N selection.
- Same-frame readback would reintroduce stalls. Diagnostics must be delayed.
- Dirty-region expansion can produce large candidate bursts. It needs explicit capacity and overflow handling.
- Exact CPU/GPU ordering may differ due to floating point and parallel selection. Validation should compare scheduling intent, not strict order, except in explicit debug mode.
- Feedback currently lives in CPU arrays. Moving it to GPU requires careful compatibility with debug overlay and scheduled probe inspection.

## Recommended Merge Sequence

1. CPU substep instrumentation.
2. Settings and diagnostics.
3. Scheduler buffer plumbing.
4. No-op `DdgiSchedulePass`.
5. GPU feedback aging.
6. GPU round-robin scheduling.
7. Indirect DDGI update dispatch.
8. GPU frustum and safety scoring.
9. GPU dirty clipmap and dirty region scheduling.
10. CPU/GPU validation compare.
11. Production fallback hardening.
12. Benchmark gates and default enablement decision.

