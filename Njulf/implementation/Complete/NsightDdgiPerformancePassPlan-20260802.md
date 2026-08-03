# Nsight and DDGI Performance Pass Implementation Plan

Status: Proposed  
Date: 2026-08-02  
Target profile: Development 1080p60  
Primary scenario: GiSponzaRightWallStationary  
Scope: Performance investigation and implementation sequence only

## 1. Purpose

This plan turns the supplied Nsight Graphics captures, console diagnostics, and the later timestamped performance snapshot into an ordered performance pass. The timestamped snapshot materially improves attribution and therefore supersedes several assumptions that could only be made from the first capture.

The plan is deliberately measurement-led. It first establishes a clean production-like baseline, then addresses the confirmed CPU and GPU hotspots in dependency order, and finally considers scheduling and memory changes. It does not authorize visual-quality reductions merely to satisfy a timing number.

No renderer or shader changes are part of this document change.

## 2. Evidence used

The plan is based on:

- Four supplied Nsight Graphics screenshots from the fixed-camera Sponza GI scenario.
- The supplied frame diagnostics and 120-frame movement-pacing summaries.
- The timestamped snapshot at C:\Users\njaal\Downloads\performance-20260802-061609-0773994-17ba47cf61c8456aa706cfe8162b8e01.json.
- Source inspection of the forward DDGI gather, far-field sky visibility, detailed diagnostics, and SimpleDdgiVolumeManager upload scope.

The timestamped snapshot was captured with:

- Debug build.
- Standard validation.
- DetailedInvestigation measurement mode.
- Detailed DDGI counters and readback enabled.
- Valid GPU timestamps with a two-frame result latency.

The snapshot explicitly identifies its detailed counters as capture-specific overhead. Its pass ordering and relative costs are actionable, but its absolute frame time is not yet the shipping baseline.

## 3. What the new snapshot changes

### 3.1 Better GPU attribution

The timestamped frame reports 85.909 ms of GPU work and provides a complete pass split:

| GPU work | Time | Share of GPU frame |
|---|---:|---:|
| Opaque forward | 62.567 ms | 72.8% |
| Transparent/layered/decal | 11.956 ms | 13.9% |
| DDGI update, total | 7.507 ms | 8.7% |
| AO plus blur | 2.128 ms | 2.5% |
| Depth pre-pass | 0.963 ms | 1.1% |
| SMAA | 0.344 ms | 0.4% |
| Directional shadows | 0.293 ms | 0.3% |
| Hi-Z | 0.040 ms | less than 0.1% |
| Composite | 0.062 ms | less than 0.1% |
| Skinning and foliage | 0.050 ms | less than 0.1% |

The original Nsight frame showed 109.67 ms total and 76.24 ms in ForwardPlusPass. The newer 62.567 ms opaque-forward result is directionally consistent: forward fragment shading remains the dominant problem. The difference is capture mode, instrumentation, and frame variance; it is not evidence of a 17.9% optimization.

Opaque forward plus the decal pass consume 74.523 ms, or 86.7% of the timestamped GPU frame. This makes fragment-work reduction the main GPU objective.

### 3.2 Confirmed CPU hotspot

The new snapshot attributes:

- 92.679 ms to the renderer CPU frame.
- 86.306 ms to the CPU GI scope.
- 84.587 ms to the Simple-DDGI CPU scope.
- 0.814 ms to primary command recording.
- 1.011 ms to secondary command recording.
- 0.007 ms each to swapchain acquire and fence wait.

The Simple-DDGI CPU value maps to SimpleDdgiVolumeManager.LastUploadMicroseconds and surrounds the broad Upload operation in Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs. The root operation is now known, but the expensive substage inside Upload is not. It may include readback invalidation and scanning, state/scheduler work, atlas maintenance, uploads, or hidden synchronization. Phase 1 must split and profile this scope before changing its behavior.

The 120-sample CPU GI P95 is 92.563 ms, so this is sustained rather than a single loading spike.

### 3.3 Corrected DDGI ray interpretation

The previous diagnostics exposed a configured primary-ray budget of 262,144 and 128 maximum rays per updated probe. That configuration must not be interpreted as 262,144 new ray queries every frame.

The timestamped frame reports:

- Actual scheduled primary rays: 0.
- Source rays traced: 0.
- Source-cache hits/misses: 53,216 / 0.
- Cached transport rays consumed by the solve: 212,960.
- Ray-trace GPU time: 0.083 ms.
- Simple transport solve: 6.241 ms.
- Blend: 0.930 ms.
- Publish: 0.162 ms.
- Relocate/classify: 0.091 ms.

Ray-query reduction is therefore not a current priority. The DDGI update problem is the persistent cached-transport solve and blend workload.

### 3.4 Decals are a separate confirmed hotspot

There are no transparent scene objects in this scenario. The 11.956 ms transparent/layered pass processes four decal objects and 481 decal meshlets. It must be investigated as decal fragment cost rather than dismissed as ordinary alpha transparency.

### 3.5 Existing forward-shader conclusions become stronger

The detailed capture reports:

- 1,897,852 primary Simple-DDGI volume gathers.
- 946,352 second-volume gathers, a 49.9% second-gather rate.
- 2,844,204 total volume gathers.
- Approximately 22.75 million eight-corner probe evaluations.
- 1,740,462 far-field sky visibility samples.
- Three cones per far-field estimate, implying approximately 5.22 million detailed far-field traces.
- Average fallback weight of 0.000233, or 0.0233%.
- Average DDGI ownership of 0.999765.
- Zero attempted or accepted fast gathers.

The shader currently computes far-field sky visibility when the feature is enabled and only afterwards multiplies the result by the tiny fallback weight. That makes the far-field visibility path the clearest confirmed source of avoidable forward work.

The Nsight shader view also reported:

- forward.frag main at 73.94% of sampled shader activity.
- 166 live registers.
- 14.4% pixel-warp occupancy.
- 71.9% unallocated warp slots.
- 23.7 active threads per warp, or 74.2% thread coherence.
- Dependency samples dominated by global loads at 63.0% and local loads at 15.9%.

Those values are consistent with register pressure, spills or local-memory traffic, divergent helper paths, and repeated storage/atlas access. They identify what to measure after removing the known wasted work; they do not by themselves prescribe a shader rewrite.

## 4. Performance contract

The active profile is Development 1080p60. Unless the product owner assigns this scene to another hardware tier, the final pass must use the existing profile contract:

- Frame-time P95 at or below 16.67 ms.
- GPU time at or below the profile GPU budget of 10 ms.
- Renderer CPU time at or below the profile CPU budget of 6 ms.
- Simple-DDGI GPU update at or below its configured target of 2.25 ms.
- No tracked-memory warning and at least 20% tracked-memory headroom.

Because the current frame is far outside the budget, workstream exit criteria also include intermediate targets. These are prioritization gates, not permission to stop before the profile passes:

- Simple-DDGI CPU Upload below 2 ms as the first milestone, then brought within its production sub-budget.
- Far-field traces proportional to meaningful fallback use instead of screen coverage.
- Decal pass below 1 ms for this four-decal fixed-camera case, unless a documented quality requirement proves that allocation unrealistic.
- At least a three-times reduction in opaque forward time before considering low-value micro-optimizations.

Any change to the budget or target hardware must be made explicitly in the performance profile, not inferred from a slow capture.

## 5. Priority issue register

| Priority | Issue | Evidence | Required response |
|---|---|---|---|
| P0 | No clean production-like benchmark exists | Debug, Standard validation, detailed atomic counters | Establish the capture contract before accepting gains |
| P0 | Simple-DDGI Upload dominates CPU | 84.587 ms current, 92.563 ms GI P95 | Profile and split the broad scope, then remove repeated or blocking work |
| P0 | Opaque forward fragment shading dominates GPU | 62.567 ms and 72.8% of GPU frame | Remove known wasted GI work, then reduce gather cost and pressure |
| P0 | Far-field sky visibility is computed for almost-zero fallback | 1.740 M samples, about 5.22 M traces, 0.0233% fallback | Gate the computation before entering the expensive helper |
| P0 | Second-volume gathering is frequent | 946,352 second gathers, 49.9% rate | Reduce unnecessary dual-volume sampling without creating ring seams |
| P0 | Decal rendering is unexpectedly expensive | 11.956 ms for four objects and 481 meshlets | Isolate pixel cost and create an appropriately cheap decal path |
| P1 | Cached DDGI transport never settles | 0 of 15,368 converged after 278 frames; 6.241 ms solve | Fix convergence/retirement and compact the active solve set |
| P1 | Detailed instrumentation perturbs the measured workload | Counter atomics forced in the validation scenario | Compile or gate detailed counters out of benchmark variants |
| P1 | Dirty-probe latency already misses its target | P50/P95/max 4/7/13 frames against target 1 | Preserve or improve priority latency while reducing maintenance |
| P1 quality | Emissive sampling is truncated | 8,098 candidates, 256 budget, 24.15% skipped energy | Prevent performance changes from worsening emissive contribution |
| P2 | Memory headroom is low | 1,819.8 of 2,048 MiB tracked; about 574 MiB unique GI residency | Remove the warning after timing hotspots are understood |
| P2 | Async compute is requested but ineligible | Independent queue exists; no path eligible | Revisit only after work and dependencies are simplified |
| P3 | Diagnostics conflate capacity with emitted work | forwardTasks and forwardReceivers show 197,632 capacity | Correct labels so future decisions use actual counts |

## 6. Phase 0: establish a trustworthy baseline

### 6.1 Define two capture modes

Create and preserve two explicit modes:

1. ProductionTiming
   - Release or the actual production configuration.
   - Validation disabled.
   - Detailed DDGI and shadow diagnostics compiled out or disabled.
   - GPU timestamp queries enabled.
   - No debug overlay, screenshots, readbacks, or shader diagnostic atomics.
   - Same quality features as the target profile.

2. DetailedInvestigation
   - Detailed counters and readbacks allowed.
   - Clearly stamped as non-comparable to shipping timing.
   - Used to explain work counts, never to sign off a budget.

GPU timestamp queries should remain available in ProductionTiming because they provide the required pass split. If their overhead is measurable, quantify it with a one-time paired capture.

### 6.2 Make captures repeatable

For every capture:

- Use GiSponzaRightWallStationary and the identical fixed camera.
- Record executable/build hash, shader hash, driver, GPU, resolution, profile, validation state, and all GI feature flags.
- Load the scene once and warm it until streaming, pipeline compilation, and scene rebuild counters are quiet.
- Record the DDGI cache generation, frames since load/recenter/clear, and convergence state.
- Capture at least 120 steady frames and report median, P95, and maximum for CPU frame, GPU frame, and each major pass.
- Retain one representative GPU trace and one representative CPU sampling trace.
- Store an HDR reference image and diagnostic values for visual comparison.

Do not compare the original 109.67 ms Nsight frame directly against the new 85.909 ms detailed frame as if either represented an optimization.

### 6.3 Produce paired attribution captures

Use identical production builds and warm state for the following A/B pairs:

- Full DDGI versus DDGI disabled.
- Full forward DDGI versus only far-field fallback disabled.
- Full volume gathering versus second-volume gathering disabled for diagnosis only.
- Decals enabled versus decals disabled.
- Decal DDGI reception enabled versus disabled.
- Detailed counters compiled out versus enabled.

The current Forward GI timestamp is inclusive of opaque forward and cannot identify incremental GI cost. These controlled pairs are required before assigning a millisecond saving to a particular helper.

### Phase 0 exit criteria

- ProductionTiming produces valid CPU and GPU median/P95 values.
- Its pass timestamps sum to the GPU frame within query precision.
- Two repeated runs agree within 5% for the major passes.
- The fixed-camera image and scenario state hashes agree.
- DetailedInvestigation is visibly labeled and cannot be mistaken for a shipping benchmark.

## 7. Phase 1: resolve the Simple-DDGI CPU Upload hotspot

### 7.1 Split the broad scope

Add low-overhead nested timing around these Upload stages:

- Scene-bounds estimation and clipmap/recenter decisions.
- Volume table construction.
- CPU state-capacity management.
- Completed probe-state readback invalidation, mapping, and scan.
- Scheduler state refresh and importance evaluation.
- Update-queue construction and upload.
- Atlas preserve, clear, and synchronization decisions.
- Parameter and probe-state uploads.
- Telemetry aggregation and detailed readback preparation.
- Any wait, map, unmap, flush, or invalidate operation.

Capture both elapsed time and work counts: probes visited, probes changed, bytes invalidated/read, queue entries, atlas regions, allocations, and generation changes.

Use a CPU sampling or timeline profiler to verify the nested timers. In particular, inspect ReadCompletedProbeStateReadback in SimpleDdgiVolumeManager.cs, which can scan all 15,368 probes and update extensive convergence/classification state.

### 7.2 Test the likely causes independently

Run controlled experiments that preserve rendered output:

- Disable completed-state readback consumption for one diagnostic run.
- Consume readback only when its completion serial changes.
- Freeze scheduler recomputation while replaying the same update queue.
- Freeze scene-bounds and volume-table reconstruction with a stationary camera.
- Disable detailed telemetry aggregation while retaining rendering.
- Measure invalidation/mapping separately from the CPU scan.
- Record managed allocations and garbage collection inside Upload.

These tests determine whether the 84.587 ms is computation, memory scanning, validation overhead, a driver mapping cost, or hidden synchronization.

### 7.3 Implement only the confirmed remedies

Expected remedies, subject to profiling evidence:

- Treat topology, bounds, and immutable volume tables as generation-owned data rather than per-frame data.
- Process readback once per completed serial and at the lowest cadence required by scheduling and correctness.
- Replace full-probe scans with compact changed-probe lists or bitsets where the GPU can provide them cheaply.
- Maintain incremental scheduler state instead of rebuilding stable entries.
- Reuse queue, staging, and temporary storage without per-frame allocation.
- Upload dirty ranges rather than whole stable tables.
- Separate optional diagnostic readback from the render-critical upload path.
- Preserve explicit synchronization; never hide a required wait on another thread.

### Phase 1 exit criteria

- Every major Upload substage has a count and median/P95 time.
- No unexplained gap larger than 10% remains in the parent scope.
- Stationary-scene Upload is below 2 ms at P95 as an intermediate gate.
- The final CPU frame meets the profile budget after subsequent phases.
- Camera recenter, scene reload, clear/preserve, and device-loss recovery still rebuild all required state.
- Scheduler decisions and rendered output match the reference within defined tolerances.

## 8. Phase 2: remove wasted far-field work from forward shading

### 8.1 Gate before the expensive call

In Njulf.Shaders/forward.frag, far-field sky visibility is currently estimated before the result is multiplied by simpleFallback. Change the control flow so EstimateFarFieldSkyVisibility is called only when:

- Far-field fallback is enabled for the shader variant.
- The resolved fallback weight is above a numerically safe threshold.
- The pixel is otherwise eligible for that fallback.

The threshold must preserve the existing transition band. Prefer an exact zero/feature-variant fast path and use a small threshold only after image testing demonstrates that it is safe.

### 8.2 Keep production shaders free of detailed atomics

The Sponza validation profile enables detailed DDGI and shadow counters. Make the ProductionTiming shader variant compile out counter atomics and diagnostic-only branches. The detailed variant remains available for work-count validation.

### 8.3 Verify the effect

Use paired captures to report:

- Far-field call count and detailed trace count.
- Opaque-forward time.
- Register count and local-load samples.
- Pixel occupancy and thread coherence.
- DDGI/fallback image difference at ring transitions, scene edges, and uncovered pixels.

### Phase 2 exit criteria

- Far-field trace count follows nontrivial fallback pixels rather than total shaded pixels.
- The nearly full-ownership fixed-camera view performs close to zero far-field traces.
- No black pixels, ring seams, discontinuities, or sky-lighting loss appear in camera-motion/recenter tests.
- Opaque forward improves materially in ProductionTiming, not only in the detailed capture.

## 9. Phase 3: reduce gather multiplicity and forward-shader pressure

This phase begins only after the far-field path is gated, because eliminating known work may materially change register pressure and the remaining hotspot.

### 9.1 Attribute the remaining gather cost

Measure:

- Pixels using one, two, or recovery volumes.
- Why each second gather was requested: transition blend, missing support, invalid state, or coverage edge.
- Atlas texture reads, probe-state word reads, and bytes per shaded pixel.
- Register count, spills/local loads, occupancy, divergence, and shader instruction mix.
- Whether the zero-use fast-gather path is disabled, inapplicable, or failing eligibility.

### 9.2 Reduce unnecessary second gathers

Potential changes, selected from the measurements:

- Choose a primary ring before sampling and gather a second ring only inside a documented blend band.
- Skip recovery gathers when primary support and ownership are complete.
- Precompute or cache per-tile/per-cluster ring-selection data if it is stable and cheaper than per-pixel selection.
- Narrow transition bands only if motion and seam tests remain clean.
- Make the fast-gather path eligible for common fully supported pixels, or remove it if it cannot be made correct.

Do not replace smooth ring blending with a hard selection edge.

### 9.3 Reduce storage traffic and live ranges

After gather-count reduction:

- Hoist invariant volume and atlas parameters out of nested helpers.
- Avoid repeatedly decoding the same probe-state storage words.
- Pass smaller values rather than large live structs through helper chains.
- Split diagnostic, fallback, and uncommon recovery behavior into specialized variants where variant count remains manageable.
- Remove local arrays or dynamic indexing only when disassembly confirms they cause local-memory traffic.
- Re-evaluate atlas/state packing only after measuring the bandwidth and ALU tradeoff.

### Phase 3 exit criteria

- Second-volume gathers are limited to pixels that demonstrably need transition or recovery data.
- Fast-path use is explained and tested; zero use is no longer an unexplained state.
- Local-load dependency samples and live-register count are reduced from the 166-register baseline, or evidence shows they are not limiting on target hardware.
- Pixel-warp occupancy materially improves without increasing total instructions enough to erase the gain.
- Fixed and moving-camera image comparisons show no ring seams, flicker, black samples, or energy discontinuity.

## 10. Phase 4: isolate and reduce decal-pass cost

The 11.956 ms pass contains no ordinary transparent objects, so begin with pixel attribution rather than meshlet-count tuning.

### 10.1 Controlled decal experiments

Capture the same warmed frame with:

- All decals disabled.
- Decal DDGI reception disabled.
- Decal shadow reception disabled.
- Individual decal materials enabled one at a time.
- Expensive material features disabled one at a time.
- Alpha rejection moved as early as correctness permits.

Collect pixel invocations, killed pixels, screen coverage, overdraw, texture samples, DDGI gathers, register count, and occupancy for the decal fragment shader.

### 10.2 Implement the measured solution

Likely options include:

- Reject outside-decal pixels before lighting and GI work.
- Use depth/stencil or tighter projected bounds to reduce shaded coverage.
- Reuse underlying surface lighting or indirect light when decal semantics permit it.
- Provide a decal-specific material/lighting variant that avoids re-running the entire opaque DDGI path for a material overlay.
- Sort and batch only when pipeline or descriptor churn is demonstrated to matter.

The exact design depends on whether the 11.956 ms is overdraw, duplicated DDGI, a pathological material, or shader pressure.

### Phase 4 exit criteria

- The cause of the pass time is attributed to pixel coverage, overdraw, material work, or GI with a paired capture.
- The four-decal fixed-camera case is below 1 ms or has an explicitly approved quality-based budget.
- Decal color, normals, roughness, depth bias, shadow reception, and DDGI reception match the reference.
- No new sorting artifacts, z-fighting, or edge halos appear.

## 11. Phase 5: make cached DDGI transport converge and retire

### 11.1 Diagnose perpetual work

The detailed frame shows:

- 15,368 active probes.
- 2,048 solver updates per frame.
- 0 converged probes.
- 15,368 pending solver probes.
- Global convergence still pending after 278 frames.
- Eight maximum solver generations.
- Relaxation 0.7 and residual threshold 0.025.
- Effective source refresh every 85 frames.
- 6.241 ms transport solve and 0.930 ms blend every frame.

Instrument:

- Residual distribution by ring and generation.
- Probe generation when source transport is refreshed.
- Exact reasons a probe remains or re-enters the pending set.
- Stable, dirty, visible, maintenance, and refresh populations.
- Compacted dispatch size and inactive lanes.
- Time per ring and per solver stage.

Determine whether refresh invalidates progress, the residual criterion is unreachable, stable probes are never retired, or fixed-budget detailed mode continuously reschedules maintenance.

### 11.2 Correct convergence and scheduling

Subject to the diagnosis:

- Retire probes whose residual and validity satisfy the convergence contract.
- Preserve converged state across source-cache reuse and refresh when the data remains compatible.
- Requeue only probes affected by source, geometry, relocation, lighting, or age changes.
- Compact transport and blend dispatches to active probes.
- Separate urgent dirty/visible work from low-priority maintenance.
- Stop issuing no-op solver/blend work after a stable stationary scene settles.
- Restore adaptive budgeting in ProductionTiming; retain deterministic fixed budgeting only for diagnostic reproduction.

Do not simply lower the 2,048-probe budget. Current first-update latency is already P50/P95/max 4/7/13 frames against a one-frame target. Reducing throughput without fixing prioritization would trade frame time for visible lag.

### 11.3 Treat ray tracing as a secondary concern

The current trace stage is 0.083 ms with zero new primary rays. Do not optimize configured maximum rays, TLAS traversal, or source-ray dispatch until a moving/recenter capture demonstrates they are expensive.

### Phase 5 exit criteria

- A stationary scene reaches a bounded settled state; eligible probes do not remain globally pending forever.
- At least 95% of eligible stable probes are converged or waiting only for their scheduled refresh.
- Steady-state transport plus blend fits within the 2.25 ms DDGI update target.
- Dirty/visible first-update P95 meets the one-frame target, or the profile documents a separately approved latency.
- Moving camera, recenter, light change, geometry change, and scene reload correctly invalidate and refresh affected probes.
- Indirect luminance, visibility, leak suppression, and temporal stability remain within visual tolerances.

## 12. Phase 6: fix performance observability

Observability changes are required to prevent the next pass from drawing the same incorrect conclusions:

- Report actual scheduled requests and rays separately from configured capacities.
- Label forwardTasks and forwardReceivers as capacity when they are populated from meshletCapacity.
- Expose actual opaque, shadow, and decal indirect counts when available.
- Distinguish source rays traced from cached transport rays solved.
- Separate inclusive aliases, such as Forward GI, from independently timed passes.
- Report whether a timer or counter is production-safe, delayed, unavailable, or capture-perturbed.
- Record counter-variant and validation state in every exported snapshot.
- Add a pass-time sum and unexplained-time field.

### Phase 6 exit criteria

- A snapshot cannot mistake capacity for emitted work or configured budget for actual work.
- ProductionTiming contains no detailed shader atomics.
- Every reported GPU duration is either independent or explicitly marked inclusive.
- Exported snapshots contain enough metadata to reproduce the run.

## 13. Phase 7: consider async compute only after work reduction

The graph requests async work and the device exposes an independent dedicated queue, but no path is currently eligible. Do not force queue migration while the CPU Upload and persistent solver work remain unresolved.

After Phases 1 through 5:

- Recalculate resource lifetimes and dependencies for AO and DDGI candidates.
- Identify true overlap with opaque/decal graphics work.
- Measure queue-transfer, semaphore, and cache costs.
- Enable one candidate at a time.
- Reject the change if total frame P95, not just the isolated pass timestamp, fails to improve.

### Phase 7 exit criteria

- Async work overlaps useful graphics work on the target GPU.
- Queue synchronization and ownership transfers are validated.
- Total GPU-frame P95 improves repeatably by more than capture noise.
- A safe single-queue fallback remains available.

## 14. Phase 8: memory-headroom pass

Memory is a production risk, but it is not the timing root cause in the supplied stationary frame.

Current evidence:

- Tracked memory: 1,819.8 / 2,048 MiB, warning.
- Heap memory: approximately 2.30 GiB / 5.23 GiB, within budget.
- Unique GI residency: 602,179,831 bytes, approximately 574 MiB.
- DDGI cache: 158,003,200 bytes.
- Far-field data: 53,786,240 bytes.
- Acceleration structures: 406,662,144 bytes resident.
- No BLAS or TLAS build/update in the frame; AS GPU time is 0 ms.

The snapshot's acceleration-structure-memory warning is a residency warning, not proof of a timing bottleneck. CPU AS work is 1.063 ms, which is worth later cleanup but is far below the 84.587 ms Upload scope.

After the timing phases:

- Audit duplicate ownership and retained generations.
- Pack probe metadata/state only where access cost remains acceptable.
- Reduce or stream far-field/AS data only with scene-transition tests.
- Alias graph resources whose lifetimes provably do not overlap.
- Release disabled-feature resources and stale scratch buffers.
- Revisit mesh, texture, shadow, and GI allocations as a complete budget rather than moving bytes between labels.

### Phase 8 exit criteria

- Tracked memory is below 80% of its budget in the target scene.
- No heap budget warning or staging overflow occurs during load, recenter, or steady state.
- Scene load and camera movement do not introduce allocation spikes or device loss.
- GPU time does not regress because compressed or streamed data increases shader traffic.

## 15. Deprioritized work

Do not begin with these items unless later captures change the evidence:

### Object and meshlet culling

- 112,177 GPU candidates compact to 44,942 emitted meshlets.
- Hi-Z tests 44,942 and rejects 3,922, or 8.73%.
- Depth plus Hi-Z costs about 1.003 ms before a 62.567 ms opaque pass.
- GPU LOD selection is active at 29,741 / 11,459 / 3,742.

The pre-pass and current-frame Hi-Z are likely net-positive while fragment shading is this expensive. The 197,632 forward-task diagnostic is capacity, not proof that all tasks execute.

### Meshlet reconstruction

Average meshlet size is 51.5 triangles and 39.5 vertices. There are 16,834 sub-16-triangle meshlets, but no evidence yet that task/mesh shading dominates. Revisit only after forward fragment cost is reduced.

### AO, SMAA, shadows, composite, and skinning

Together these are small compared with opaque forward, decals, and DDGI transport. AO at 2.128 ms may become relevant after the primary work, but it is not the first bottleneck.

### Ray-query and acceleration-structure build optimization

The captured frame schedules zero new primary rays and performs no AS builds. Keep moving/recenter scenarios in the benchmark matrix, but do not optimize this stationary frame as if it traced 262,144 new rays.

### Material-upload P95

The reported 3.511 ms material-upload P95 is based on only two samples, while the current value is 0.010 ms. Collect a meaningful loading/mutation distribution before acting.

## 16. Quality and correctness constraints

Every performance change must preserve:

- DDGI energy, ownership, visibility, leak suppression, and ring transitions.
- Correct behavior during recenter, clear, preserve, scene reload, and source-cache refresh.
- Directional shadows and decal shadow reception.
- Decal material layering and depth bias.
- Emissive lighting contribution.
- Animation/skinning and camera-motion stability.
- Determinism where the benchmark or validation mode requires it.

The emissive diagnostic requires a separate quality check: 8,098 candidates are truncated to a budget of 256 and 24.15% of candidate energy is skipped. A performance optimization must not conceal or increase that loss. If the emissive budget is changed, validate both timing and retained energy.

## 17. Verification matrix

Run every accepted phase against:

| Scenario | Purpose |
|---|---|
| GiSponzaRightWallStationary, warmed | Primary timing and convergence baseline |
| Same camera immediately after load | Warm-up and source-cache behavior |
| Slow camera translation across ring boundaries | Blend seams and scheduling latency |
| Fast translation/recenter | Invalidation, preserve, and recovery |
| Light intensity/direction change | Dirty-probe priority and convergence |
| Geometry or scene reload | AS/state generations and full rebuild |
| Decals disabled/enabled | Decal incremental cost |
| DDGI fallback forced at coverage edge | Far-field gating correctness |
| Emissive-heavy view | Candidate truncation and indirect-energy retention |
| Validation and device-loss recovery | Synchronization and rebuild safety |

For each scenario collect:

- CPU frame and Simple-DDGI Upload median/P95/max.
- GPU frame and per-pass median/P95/max.
- Actual scheduled probes, source rays, cached solve rays, and dispatch sizes.
- Primary/second/recovery gather counts in a separate detailed capture.
- Register, occupancy, local-load, and texture/global-load metrics for forward and decal shaders.
- Tracked/heap memory, allocation spikes, and staging overflows.
- HDR image-difference metrics plus targeted visual inspection.

## 18. Implementation order and decision gates

1. Build ProductionTiming and reproduce the fixed-camera baseline.
2. Split and profile SimpleDdgiVolumeManager.Upload.
3. Fix the confirmed CPU substage and re-baseline.
4. Gate far-field sky visibility before its expensive work.
5. Re-profile forward.frag and reduce unnecessary second-volume gathers.
6. Isolate and optimize the decal fragment path.
7. Diagnose and fix perpetual cached-transport convergence.
8. Correct diagnostic semantics and preserve paired captures.
9. Re-evaluate overall budgets.
10. Only then test async overlap, memory packing, AO, meshlets, or other secondary work.

Each step must be independently measurable and reversible. Do not combine the CPU Upload fix, shader gather redesign, and solver scheduling change into one benchmark result.

## 19. Risks and rollback requirements

| Risk | Mitigation |
|---|---|
| Far-field gating causes coverage-edge darkness | Preserve exact-zero path first; test forced fallback and motion |
| Single-volume optimization creates ring seams | Keep explicit blend band and validate temporal camera sweeps |
| Incremental readback misses state changes | Use generations/serials and retain full-scan validation mode |
| Probe retirement leaves stale lighting | Define invalidation sources and test light/geometry changes |
| Lower maintenance work increases update latency | Measure dirty-probe P95 and prioritize urgent work |
| Decal cheap path changes material response | Compare each material channel and shadow/GI reception |
| Shader specialization causes variant explosion | Limit variants to measured high-value feature combinations |
| Async compute adds transfers or stalls | Accept only total-frame improvement; keep single-queue fallback |
| Memory packing raises shader cost | Benchmark timing and residency together |
| Detailed counters alter the optimized path | Keep production and investigation variants explicit |

Every behavioral optimization should have a feature toggle or a straightforward revert boundary until its fixed, moving, recenter, and reload tests pass.

## 20. Completion criteria

The performance pass is complete only when:

- A reproducible ProductionTiming capture exists for the target hardware and profile.
- Renderer CPU and GPU P95 satisfy their configured budgets.
- The Simple-DDGI CPU Upload scope is broken down and no longer dominates the frame.
- Opaque forward no longer performs far-field traces for effectively unused fallback.
- Second-volume gathering and shader pressure have measured, justified behavior.
- The decal pass has an explicit cost and fits its assigned budget.
- Stable DDGI transport converges and retires rather than consuming 6-plus ms indefinitely.
- Detailed diagnostics are excluded from production timing and all capacity/actual counters are unambiguous.
- Tracked memory has at least 20% headroom.
- The complete verification matrix passes without visual or temporal regressions.
- Before/after captures, settings, hashes, images, and analysis are stored with the implementation results.

## 21. Expected outcome

The new snapshot does not change the central conclusion that forward fragment work is the largest GPU issue. It does change the first implementation target and the DDGI update strategy:

- First investigate and fix the 84.587 ms Simple-DDGI CPU Upload scope.
- Then remove the millions of far-field traces whose result is almost entirely discarded.
- Reduce dual-volume gather frequency and forward-shader pressure.
- Treat the 11.956 ms decal path as a first-class fragment bottleneck.
- Fix persistent cached-transport convergence instead of reducing ray queries that did not occur.

This sequence attacks measured work, preserves diagnostic confidence, and avoids spending the first performance pass on low-impact culling, ray tracing, or memory changes.
