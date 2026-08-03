# Revised Nsight and DDGI Performance Pass Implementation Plan

Status: Revised; partially implemented  
Date: 2026-08-02  
Target profile: Development 1080p60  
Primary scenario: GiSponzaRightWallStationary  
Scope: Remaining performance investigation, implementation, and verification work

## 1. Purpose

This document revises NsightDdgiPerformancePassPlan-20260802.md using the later
timestamped snapshot:

C:\Users\njaal\Downloads\performance-20260802-185539-2707838-d79aeee018ca4f6d83ad631eacb057ad.json

It supersedes the original plan for implementation order and open-work tracking.
The original plan remains useful as the record of the first capture and the
reasoning that led to the completed changes.

The later snapshot shows a major directional improvement, especially in forward
and decal fragment cost, but it is not a controlled production benchmark. This
revision therefore separates:

- Work that is implemented and needs verification.
- Work whose root cause is now more precisely attributed.
- Work that remains directly supported by the latest capture.
- Work that should remain deferred.

No renderer or shader changes are made by this document.

## 2. Evidence and comparison limits

### 2.1 Evidence used

This revision uses:

- The original plan and its source snapshot:
  C:\Users\njaal\Downloads\performance-20260802-061609-0773994-17ba47cf61c8456aa706cfe8162b8e01.json
- The later timestamped snapshot listed above.
- Source inspection of SimpleDdgiVolumeManager.Upload and EnsureCapacity.
- Source inspection of the new far-field fallback gate and sparse diagnostic
  sampling in forward.frag and ddgi_simple_shared.glsl.
- The current uncommitted implementation diff.

### 2.2 The captures are not a controlled A/B pair

The old and new captures share the same scenario label, GPU, driver, resolution,
quality tier, and general feature state, but their locked state does not match:

| Capture state | Earlier snapshot | Later snapshot |
|---|---:|---:|
| Camera position | 8.626134, 1.1293472, approximately 0 | 6, 1.35, 0 |
| Camera pitch | 0.13715655 | 0.16 |
| View hash | 502f2a...c7053 | b1f80e...c4bab |
| Scene content revision | 4 | 3 |
| Frame serial | 756 | 1881 |
| Frames since scene load | 425 | 676 |
| Shader bundle hash | 8fd8b5...c9db7 | 2fc135...fb990 |

Both snapshots are also:

- Debug builds.
- Standard validation.
- DetailedInvestigation captures.
- Detailed GPU counters and readback enabled.

The reported old-to-new reductions are therefore directional evidence, not an
accepted optimization result. No implementation phase may claim its target from
these two files alone.

### 2.3 What is trustworthy within the later snapshot

The later snapshot is still useful for current attribution:

- Independent GPU pass timestamps sum to 29.272 ms.
- Reported GPU frame time is 29.271 ms.
- The one-microsecond difference is within timestamp precision.
- Simple-DDGI Upload stages account for essentially all of their parent scope.
- The scene is reported as steady state and has valid detailed counter readback.

The current pass split and stage proportions are actionable even though the
absolute timings are not a shipping baseline.

## 3. Current result summary

### 3.1 Directional old-to-new timing movement

| Metric | Earlier | Later | Directional change |
|---|---:|---:|---:|
| Renderer CPU | 92.679 ms | 38.100 ms | -58.9% |
| CPU GI scope | 86.306 ms | 31.328 ms | -63.7% |
| CPU GI P95 | 92.563 ms | 33.902 ms | -63.4% |
| Simple-DDGI CPU Upload | 84.587 ms | 29.572 ms | -65.0% |
| GPU frame | 85.909 ms | 29.271 ms | -65.9% |
| Opaque forward | 62.567 ms | 17.660 ms | -71.8% |
| Decal/transparent pass | 11.956 ms | 1.530 ms | -87.2% |
| DDGI update | 7.507 ms | 6.894 ms | -8.2% |
| DDGI transport | 6.241 ms | 5.676 ms | -9.1% |
| DDGI blend | 0.930 ms | 0.868 ms | -6.7% |

These changes must be reproduced with identical view and scene hashes before
being accepted.

### 3.2 Later GPU pass split

| GPU work | Time | Share |
|---|---:|---:|
| Opaque forward | 17.660 ms | 60.3% |
| DDGI update, total | 6.894 ms | 23.6% |
| AO plus blur | 1.735 ms | 5.9% |
| Decal/transparent | 1.530 ms | 5.2% |
| Depth pre-pass | 0.663 ms | 2.3% |
| SMAA | 0.347 ms | 1.2% |
| Directional shadows | 0.296 ms | 1.0% |
| Composite | 0.061 ms | 0.2% |
| Skinning | 0.050 ms | 0.2% |
| Hi-Z | 0.035 ms | 0.1% |

Opaque forward remains the largest GPU target. Cached DDGI transport remains the
second material target. Decals are no longer a first-order bottleneck.

### 3.3 Later Simple-DDGI CPU breakdown

| Upload stage | Time | Share of Upload |
|---|---:|---:|
| Capacity | 25.396 ms | 85.9% |
| Scheduler refresh | 2.363 ms | 8.0% |
| Queue build | 1.351 ms | 4.6% |
| Layout | 0.287 ms | 1.0% |
| Importance | 0.078 ms | 0.3% |
| Buffer upload | 0.069 ms | 0.2% |
| Invalidation | 0.008 ms | less than 0.1% |
| Lifecycle telemetry | 0.010 ms | less than 0.1% |
| Atlas maintenance | 0.001 ms | less than 0.1% |
| Readback | 0 ms | 0% |
| Unclassified | 0.009 ms | less than 0.1% |

The earlier broad CPU investigation has succeeded in identifying a new root
scope. Readback scanning is not the current hotspot. Capacity handling is.

### 3.4 Remaining gather and solver evidence

The later detailed capture reports:

- 1,914,777 base Simple-DDGI gather samples.
- 1,006,899 second-volume gathers.
- A 52.59% second-gather rate, up from 49.86%.
- 2,921,676 base-plus-second gathers.
- Zero fast-gather attempts and zero accepted fast gathers.
- Average DDGI ownership of 0.9997458.
- Average environment fallback weight of 0.00025493422.
- An estimated 10,240 far-field sky-visibility samples.
- Zero newly traced primary rays.
- 212,960 cached transport rays solved.
- 0 of 15,368 probes converged.
- 15,368 probes pending solver work.
- 660 DDGI frames of global convergence.
- Dirty first-update P50/P95/max of 3/8/8 frames.

The far-field counter is now a weighted estimate from a sparse 16-by-16
diagnostic sample. It must be labeled as estimated in future exports.

### 3.5 Memory

The later capture reports:

- 1,820.25 MiB tracked out of 2,048 MiB.
- 88.88% tracked usage and 11.12% headroom.
- Approximately 574.77 MiB of unique GI residency.
- Device-local heap usage within the driver-reported heap budget.

Tracked memory still fails the plan's 20% headroom requirement, but memory is not
the timing root cause in this stationary frame.

## 4. Performance contract

The active Development 1080p60 profile remains the contract unless explicitly
changed:

- Frame-time P95 at or below 16.67 ms.
- GPU frame at or below 10 ms.
- Renderer CPU at or below 6 ms.
- GI GPU at or below the 2.5 ms profile budget.
- DDGI scheduling target at or below 2.25 ms where that target applies.
- GI CPU scheduling/upload at or below 0.25 ms.
- At least 20% tracked-memory headroom.

Intermediate gates:

- Stationary Simple-DDGI Upload below 2 ms at P95 before final sub-budget work.
- No hidden device-wide synchronization in stable per-frame capacity handling.
- Opaque forward assigned a measured sub-budget that leaves room for the rest of
  the 10 ms GPU frame.
- Stationary transport plus blend below 2.25 ms after convergence settles.
- Four-decal pass below 1 ms unless a quality-approved budget replaces it.
- Dirty first-update P95 at or below one frame.

## 5. Revised issue register

| Priority | Issue | Latest evidence | Required response |
|---|---|---|---|
| P0 | No controlled ProductionTiming benchmark | Debug, validation, detailed counters, mismatched view and scene revision | Lock the capture contract before accepting gains |
| P0 | Capacity handling dominates CPU Upload | 25.396 of 29.572 ms | Attribute transition, lock, and wait costs; make stable capacity a true no-op |
| P0 | Opaque forward remains over the total GPU budget | 17.660 ms, 60.3% of GPU frame | Reduce unnecessary second gathers and shader pressure |
| P0 | Cached transport never converges or retires | 0/15,368 converged after 660 frames; 6.544 ms transport plus blend | Fix convergence state and compact active work |
| P1 | Scheduler and queue CPU work remain material | 2.363 plus 1.351 ms | Make stable scheduling incremental after capacity is fixed |
| P1 | Dirty update latency misses target | P95 eight frames against one | Preserve urgent priority while reducing steady work |
| P1 | Production shaders still contain detailed investigation work | Detailed counters active | Compile diagnostic atomics and branches out of ProductionTiming |
| P1 | Forward incremental attribution is unavailable | Forward timestamp remains inclusive | Add a deterministic pair or isolated timing method |
| P1 | Decals remain slightly over their provisional target | 1.530 ms for four decals | Recheck after forward changes; optimize only the measured remainder |
| P2 | Tracked memory headroom is low | 11.12% headroom | Perform a later residency and ownership audit |
| P3 | Async compute is requested but ineligible | No eligible path | Revisit only after work reduction |

## 6. Phase 0: establish a controlled ProductionTiming baseline

This phase remains mandatory and precedes further performance claims.

### 6.1 Capture modes

Preserve two explicit modes:

ProductionTiming:

- Release or actual production configuration.
- Validation disabled.
- Detailed shader counters and diagnostic readbacks compiled out.
- GPU timestamps enabled.
- No overlay, screenshot capture, or diagnostic atomic traffic.
- Identical quality and rendering features to the target profile.

DetailedInvestigation:

- Detailed counters and readbacks allowed.
- Sparse and estimated counters explicitly labeled.
- Never used to sign off absolute budgets.

### 6.2 Locked capture identity

Every comparable run must record and match:

- Exact camera position, orientation, projection, and view hash.
- Scene content revision and a stable scene-state hash.
- Executable hash, commit, dirty-worktree state, and shader bundle hash.
- GPU, driver, resolution, profile, validation, and feature flags.
- DDGI cache generation, frames since load/recenter/clear, and convergence state.

The scenario label alone is insufficient.

### 6.3 Required runs

For the locked fixed camera:

- Capture at least 120 steady ProductionTiming frames.
- Report median, P95, and maximum for CPU frame, GPU frame, and every major pass.
- Repeat the run twice and require major pass P95 values to agree within 5%.
- Capture one DetailedInvestigation run with matching view and scene hashes.
- Store an HDR reference image and image-difference result.

### Phase 0 exit criteria

- ProductionTiming is reproducible and visibly distinguished from investigation.
- Repeated runs agree within 5%.
- View hash, scene hash, executable hash, and shader hash match.
- GPU pass timestamps sum to the GPU frame within query precision.
- CPU and GPU P95 distributions exist; a single current frame is not used as P95.

## 7. Phase 1: remove the stable-capacity CPU stall

### 7.1 Split the Capacity timer

The current Capacity category combines more than one operation. Add separate
low-overhead timing and counts for:

- EnsureCpuProbeStateCapacity.
- SimpleDdgiMemoryPlan creation.
- Capacity-transition predicate evaluation.
- Buffer-manager size lookups and lock acquisition.
- Device-idle wait duration.
- Each buffer transition, including old and required sizes.
- Readback-buffer reconciliation.
- Sampled-atlas budget evaluation.
- Sampled-atlas EnsureCapacity.
- Descriptor or bindless re-registration.
- Retired-resource destruction attributable to capacity.

Record a capacity-transition reason bitmask and transition count. A stable frame
must report zero transitions and zero waits.

### 7.2 Investigate the hidden synchronization hypothesis

SimpleDdgiVolumeManager.EnsureCapacity can call VulkanContext.WaitIdle when it
believes a synchronized transition is required. That direct call is not included
in the renderer's RuntimeDeviceWaitIdleCount.

The hypothesis is supported, but not proven, by:

- Earlier Upload at 84.587 ms beside an 85.909 ms GPU frame.
- Later Capacity at 25.396 ms beside a 29.271 ms GPU frame.
- A nominal runtime device-idle count of zero.
- A stationary scene whose stable capacity should not require a transition.

Confirm with explicit branch, wait, and mismatch telemetry plus a CPU sampling
trace. Also test buffer-manager lock contention before concluding that the time
is entirely a GPU wait.

### 7.3 Make stable capacity generation-owned

Subject to confirmation:

- Compute a stable capacity key from tier, topology, probe count, ray capacity,
  request capacity, readback mode, and sampled-atlas mode.
- Reconcile Vulkan resources only when that key changes.
- Assert in validation mode that the cached plan still matches all live sizes.
- Keep transition handling synchronous and explicit when a real tier or topology
  change requires it.
- Route every device-idle wait through shared stall telemetry.
- Preserve complete rebuild behavior for load, recenter requiring reallocation,
  tier change, device loss, and feature-mode transition.

Do not move a required wait to another thread merely to hide it.

### 7.4 Address remaining scheduler CPU work

After capacity is fixed, remeasure:

- Scheduler refresh: currently 2.363 ms and 2,048 refreshed entries.
- Queue build: currently 1.351 ms.
- Visibility refresh: currently 2,048 entries.

Then:

- Refresh only entries whose scheduler state actually changed.
- Avoid repeating visibility work when the locked camera and volume generations
  are unchanged.
- Preserve persistent queues and heaps without rebuilding stable membership.
- Build only the active dispatch list.
- Reuse all scratch storage without per-frame allocation.

### Phase 1 exit criteria

- Stable Capacity is below 0.1 ms and reports zero transitions and waits.
- No unexplained gap exceeds 10% of Upload.
- Stationary Upload P95 is below 2 ms as an intermediate gate.
- The final GI CPU scope meets its configured sub-budget.
- Recenter, tier transition, reload, clear, and device-loss tests rebuild safely.

## 8. Phase 2: verify and productionize far-field gating

The core far-field gate is implemented. This phase is verification-only unless
the controlled captures show a regression.

### 8.1 Required verification

Using identical ProductionTiming and DetailedInvestigation state:

- Compare gated fallback against a diagnostic forced-old-path variant.
- Confirm far-field calls track pixels above the fallback threshold.
- Confirm the near-full-ownership view performs negligible far-field work.
- Test coverage edges, ring transitions, slow movement, and fast recenter.
- Compare HDR output for darkness, seams, discontinuity, and lost sky lighting.

### 8.2 Production instrumentation

- Compile sparse counter atomics out of ProductionTiming.
- Label sky-visibility sample count as an estimated full-frame count.
- Export its sample stride and weight.
- Retain the exact debug-view path when explicitly requested.

### Phase 2 exit criteria

- Controlled ProductionTiming confirms a material forward improvement.
- Estimated far-field work is proportional to meaningful fallback.
- Motion and forced-fallback image tests pass.
- No detailed far-field diagnostic atomic remains in ProductionTiming.

## 9. Phase 3: reduce forward gather multiplicity and shader pressure

This is the highest remaining GPU optimization phase.

### 9.1 Attribute every second gather

Add mutually exclusive reasons:

- Ring transition blend.
- Missing or invalid primary support.
- Recovery gather.
- Coverage edge.
- Primary ownership below threshold.
- Debug or diagnostic-only request.

Report pixel counts and percentages for one, two, and recovery gathers.

### 9.2 Explain the zero-use fast path

Determine whether fast gathering is:

- Disabled by configuration.
- Ineligible because of feature flags.
- Rejected by support or ownership.
- More expensive than the standard path.
- Dead code.

Either make it correct and useful for a measured common case or remove it. Zero
attempts must not remain unexplained.

### 9.3 Re-profile the shader after far-field gating

Collect a new Nsight shader profile for opaque and decal fragments:

- Live registers.
- Spills and local-memory traffic.
- Occupancy.
- Thread coherence and divergence.
- Texture and storage loads.
- Instructions and sampled dependency reasons.

Do not reuse the original 166-register result as the post-change value.

### 9.4 Implement only measured gather reductions

Candidate remedies:

- Select the primary ring before gathering.
- Gather a second ring only within a documented transition band.
- Skip recovery when primary support and ownership are complete.
- Cache stable ring selection per tile or cluster when cheaper than per-pixel
  resolution.
- Hoist invariant volume and atlas values.
- Avoid decoding the same probe-state words more than once.
- Shorten helper live ranges and specialize uncommon debug/recovery behavior.

Do not introduce a hard ring edge or reduce lighting quality without approval.

### Phase 3 exit criteria

- Every second gather has a measured reason.
- The second-gather rate is reduced without seams or instability.
- Fast-path status is explicit.
- ProductionTiming forward P95 fits its assigned sub-budget.
- Register pressure, local loads, and occupancy are remeasured and improved where
  they are limiting.

## 10. Phase 4: make cached transport converge and retire

### 10.1 Diagnose why no probe converges

Instrument:

- Residual distribution by ring, source epoch, and solver generation.
- Probes meeting the residual threshold but not marked converged.
- Exact reason each probe remains or re-enters the pending set.
- Stable, dirty, visible, maintenance, and refresh populations.
- Dispatch lanes that perform no useful solve.
- Transport and blend time per ring.
- Time between source refresh and completed solver generations.

The effective refresh period is now 200 frames, yet no probe is converged after
660 frames. Determine whether the residual threshold is unreachable, convergence
state is overwritten, all probes are continuously requeued, or retirement is
missing.

### 10.2 Correct convergence and active-set scheduling

Subject to diagnosis:

- Retire probes that meet residual and validity requirements.
- Preserve compatible convergence across source-cache reuse.
- Requeue only probes affected by source, geometry, relocation, lighting, age,
  or explicit maintenance.
- Compact transport and blend dispatches to the active set.
- Stop stable no-op solver and blend work.
- Separate urgent dirty and visible work from background maintenance.
- Restore adaptive ProductionTiming budgeting after deterministic reproduction.

Do not lower the 2,048 update count as a standalone fix. Dirty first-update P95
already misses its target.

### Phase 4 exit criteria

- A stationary scene reaches a bounded settled state.
- At least 95% of eligible stable probes converge or wait only for scheduled
  refresh.
- Settled transport plus blend is below 2.25 ms.
- Dirty first-update P95 is at or below one frame.
- Light, geometry, movement, recenter, and reload invalidate the correct probes.

## 11. Phase 5: recheck the decal path

The earlier 11.956 ms decal concern has been reduced to 1.530 ms. Do not begin a
large decal redesign without a controlled ProductionTiming confirmation.

### 11.1 Paired captures

Use the locked camera with:

- All decals disabled.
- Decal DDGI reception disabled.
- Decal shadow reception disabled.
- Each decal material enabled independently.
- Early rejection variants where correctness permits.

Collect pixel invocations, killed pixels, coverage, overdraw, DDGI gathers,
registers, and occupancy.

### 11.2 Limited implementation scope

If the remaining cost is confirmed:

- Reject outside-decal pixels before lighting.
- Use tighter projected bounds or depth/stencil rejection.
- Avoid repeating full opaque lighting for overlay-only semantics.
- Use a decal-specific variant only when the measured saving justifies it.

### Phase 5 exit criteria

- The four-decal pass is below 1 ms or has an approved alternative budget.
- Remaining cost has explicit coverage, overdraw, material, or GI attribution.
- Material channels, depth bias, shadow reception, and DDGI reception match.

## 12. Phase 6: finish performance observability

Several original observability items are implemented:

- Forward task invocations now report the actual compacted count.
- Actual scheduled requests and primary rays are separately exposed.
- Upload has a detailed stage split.
- GPU pass timestamps reconcile with the frame.

Complete the remaining work:

- Route direct VulkanContext waits through runtime stall telemetry.
- Report capacity transition count, reason, old bytes, and required bytes.
- Mark every counter as exact, sampled estimate, capacity, configured budget, or
  emitted work.
- Rename or relabel ForwardShadowReceiverMeshletCount if it remains capacity.
- Add ProductionTiming CPU and GPU pass P95 values.
- Add an executable hash and dirty-worktree state.
- Add forward incremental timing or a first-class paired-capture identity.
- Report pass-time sum and unexplained time.
- Clamp negligible floating-point noise in zero-tolerance quality metrics.

The emissive truncation issue itself is closed:

- 8,098 candidates.
- Budget increased to 8,192.
- Skipped energy is approximately 1.65e-16, effectively zero.

The current OverBudget status for that negligible value is an observability
tolerance bug, not a material lighting loss.

### Phase 6 exit criteria

- Stable hidden waits cannot escape the runtime stall report.
- A snapshot cannot confuse capacity, estimates, and emitted work.
- ProductionTiming contains no detailed shader atomics.
- Exported metadata reproduces the executable, shaders, scene, and camera.
- Numerically negligible quality noise does not produce a false failure.

## 13. Phase 7: memory-headroom pass

Begin only after the main CPU and GPU timing work is understood.

Audit:

- Duplicate ownership and retained resource generations.
- DDGI canonical, sampled, transport, readback, and scratch capacities.
- Far-field and acceleration-structure residency.
- Mesh, texture, shadow, and GI allocations as one tracked budget.
- Disabled-feature resources.
- Aliasing opportunities for graph resources with disjoint lifetimes.

Do not reduce memory by increasing forward shader traffic or introducing
streaming stalls.

### Phase 7 exit criteria

- Tracked memory is below 80% of the 2 GiB budget.
- Load, recenter, and steady state have no staging overflow or allocation spike.
- No timing regression is caused by packing or streaming.

## 14. Phase 8: optional secondary work

Revisit only after Phases 1 through 7:

- Async compute overlap.
- AO optimization.
- Meshlet reconstruction.
- Additional culling changes.
- Ray-query or acceleration-structure build optimization.

Current evidence continues to deprioritize:

- Ray tracing: zero new primary rays and 0.111 ms trace time.
- AS builds: no BLAS or TLAS build/update in the stationary frame.
- Hi-Z: 0.035 ms with useful visibility compaction.
- Material upload P95: only two samples despite a 3.435 ms reported P95.

Async compute remains conditional on a true dependency analysis and a repeatable
total-frame P95 improvement.

## 15. Revised implementation order

1. Produce a locked ProductionTiming baseline and matching detailed capture.
2. Split Capacity, confirm the wait or contention source, and make stable capacity
   a no-op.
3. Reduce remaining scheduler-refresh and queue-build CPU work.
4. Verify far-field gating and compile detailed counters out of production.
5. Attribute and reduce second-volume gathers; re-profile forward shader pressure.
6. Fix transport convergence, retirement, and active dispatch compaction.
7. Recheck the remaining decal cost.
8. Complete observability semantics and reproducibility metadata.
9. Restore 20% tracked-memory headroom.
10. Only then test async overlap, AO, meshlets, culling, or ray-query work.

Each accepted step must have a separate before/after result with matching capture
identity. Do not combine capacity, gather, and convergence changes into one
benchmark result.

## 16. Verification matrix

| Scenario | Required validation |
|---|---|
| Locked stationary camera, warmed | Primary median/P95 timing and convergence |
| Immediate post-load camera | Allocation, warm-up, source cache, and full initialization |
| Slow ring-boundary translation | Gather seams and fallback transition |
| Fast translation/recenter | Capacity transition, invalidation, preserve, and recovery |
| Light direction/intensity change | Dirty priority and solver convergence |
| Geometry change or reload | Generations, AS state, and complete rebuild |
| Decals enabled/disabled | Incremental decal cost |
| Forced far-field fallback | Gating correctness and sky visibility |
| Emissive-heavy view | Retained energy and budget tolerance |
| Tier or probe-layout change | Deliberate synchronized capacity transition |
| Validation and device-loss recovery | Synchronization and resource rebuild |

For every applicable scenario collect:

- CPU frame and Upload median/P95/max.
- Capacity transition and wait counts.
- GPU frame and independent pass median/P95/max.
- Base, second, recovery, and fast gather counts.
- Active solver population and residual distribution.
- Dirty first-update and convergence latency.
- Register, spill, occupancy, and load metrics where shader work changes.
- Tracked and heap memory.
- HDR image difference plus targeted visual inspection.

## 17. Risks and rollback boundaries

| Risk | Mitigation |
|---|---|
| Capacity caching misses a real topology change | Generation key plus validation assertions and rebuild tests |
| Removing a wait creates use-after-free | Keep explicit synchronized transition path and fence-safe retirement |
| Single-volume reduction creates ring seams | Retain a measured blend band and run camera sweeps |
| Probe retirement leaves stale lighting | Enumerate invalidation sources and test light/geometry changes |
| Compaction worsens dirty latency | Separate urgent work and gate on one-frame P95 |
| Far-field threshold causes darkness | Forced-fallback and coverage-edge image tests |
| Decal specialization changes material response | Compare every written material channel |
| Memory packing raises shader cost | Benchmark residency and timing together |
| Production and detailed variants diverge | Run matching image and state validation captures |

Every behavioral optimization must retain a feature toggle or straightforward
revert boundary until stationary, moving, recenter, reload, and quality tests pass.

## 18. Completion criteria

The revised performance pass is complete only when:

- A reproducible ProductionTiming benchmark exists with matching executable,
  shader, camera, and scene identity.
- Renderer CPU and GPU P95 satisfy the profile.
- Stable capacity performs no transition, lock stall, or device-wide wait.
- Simple-DDGI Upload no longer dominates CPU time.
- Far-field gating is verified and diagnostic atomics are absent from production.
- Second gathers occur only for measured transition or recovery reasons.
- Opaque forward fits its assigned GPU sub-budget.
- Stable DDGI transport converges, retires, and fits its target.
- Dirty first-update P95 meets the one-frame target.
- The decal pass meets its assigned budget.
- Counter semantics and capture provenance are unambiguous.
- Tracked memory has at least 20% headroom.
- The verification matrix passes without visual or temporal regression.

## 19. Expected outcome

The implemented changes have probably removed most of the previously wasted
far-field fragment work and substantially reduced decal cost. The later snapshot
does not justify ending the performance pass:

- CPU capacity handling still consumes approximately 25 ms.
- Opaque forward still consumes approximately 18 ms.
- More than half of base gathers still trigger a second volume.
- Cached transport still consumes more than 6 ms and never converges.
- Tracked memory still lacks the required headroom.

The revised order first establishes trustworthy measurement, then removes the
likely stable-capacity stall, reduces the dominant forward gather work, and fixes
perpetual transport. It preserves the successful far-field change while avoiding
premature work on decals, async compute, culling, ray tracing, or memory packing.
