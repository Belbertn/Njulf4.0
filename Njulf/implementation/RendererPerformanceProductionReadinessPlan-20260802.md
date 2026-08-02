# Renderer Performance Production-Readiness Plan

Status: Proposed  
Date: 2026-08-02  
Scope: Sponza Right Wall stationary workload, Simple DDGI, forward/decal rendering, renderer CPU submission, profiling infrastructure, and release qualification

## 1. Objective

Bring the renderer to a production-ready performance state without reducing visual quality, silently discarding lighting energy, weakening correctness, or relying on scene-specific shortcuts.

This is an architecture program rather than a collection of local optimizations. The work must:

- Remove the frame-sized CPU/driver synchronization stall.
- Stop rebuilding or rehashing stable renderer state every frame.
- Move sparse DDGI publication work off the CPU.
- Preserve scheduler behavior while replacing repeated all-probe scans.
- Eliminate emissive-energy truncation.
- Make forward, decal, DDGI gather, and transport GPU work attributable and optimizable.
- Establish repeatable Release profiling and quality/performance release gates.
- Enable async compute only where measured overlap produces a net frame-time win.

The implementation must not use reduced resolution, lower probe or ray budgets, reduced atlas precision, blind fallback thresholds, discarded emissive sources, higher sparse-copy thresholds, or forced async compute as substitutes for fixing the underlying architecture.

## 2. Evidence and Current Baseline

Evidence reviewed:

- runtime-gi-sponza-right-wall-stationary-20260802-001408.json
- NjulfHelloGame - [2026-08-02 00-54-30].dtp
- Nsight Systems screenshot from the same workload
- Nsight Graphics shader-profiler screenshot from the same workload
- Release and Debug validation-off benchmark captures
- Renderer, DDGI, shader, render-graph, descriptor, benchmark, and validation source

Relevant observed values:

| Signal | Observation |
| --- | --- |
| Original CPU frame | 117.39 ms average, 119.06 ms p95 |
| Original GPU frame | 83.91 ms average, 85.29 ms p95 |
| Release, validation off CPU | Approximately 86.64 ms |
| Nsight Systems CPU frame | Approximately 92.36 ms average |
| Nsight Systems GPU work | Approximately 83–85.5 ms |
| ForwardPlusPass | Approximately 60.92 ms |
| Pass reported as TransparentPasses | Approximately 11.44 ms; workload is primarily geometry decals |
| DDGI blend/update | Approximately 8.20 ms |
| Cached transport solve | Approximately 6.84 ms |
| DDGI probes | 15,368 total; 2,048 updated per frame |
| Cached transport rays | 212,960 per frame |
| Converged probes | Zero after 332 frames in the captured stationary run |
| Sampled-atlas publication | 2,048 sparse regions in the captured frame |
| Emissive table | 8,098 candidates reduced to 256 |
| Emissive energy excluded | Approximately 24.15 percent |
| Second-volume gather | Approximately 52.7 percent of gather samples |
| Environment fallback mean | Approximately 0.0001158 |

The original benchmark enabled detailed investigation counters. It is valid for attribution but not a shipping timing baseline. The Release validation-off run confirms that validation and Debug code generation are not the root cause.

The dotTrace steady-state interval shows:

- The main thread is waiting for approximately 46.37 seconds of a 52-second interval.
- Approximately 89.2 percent of wall time is under NVIDIA driver code reached from SimpleDdgiVolumeManager.Upload and SimpleDdgiSampledAtlas.EnsureCapacity.
- The stable-capacity branch re-registers the sampled atlas every frame.
- Registration produces repeated vkUpdateDescriptorSets calls even when image views, samplers, layouts, and indices are unchanged.

The Nsight Graphics capture shows two mesh-task indirect actions at approximately 42.0 ms and 34.4 ms. Their exact source attribution is not yet trustworthy because the shaders lack usable source/function debug information and validation-off currently disables the debug-utils labels.

## 3. Product Performance Contract

Before changing the hot paths, lock a whole-frame service-level objective. The provisional recommendation is:

- DdgiHigh at 1600 by 900 on the RTX 3060 Laptop reference system.
- 16.67 ms p95 whole-frame budget if DdgiHigh is a 60 Hz shipping tier.
- 33.3 ms p95 if DdgiHigh is explicitly a 30 Hz tier.
- Fixed camera bookmarks, deterministic seeds, fixed quality settings, and fixed warmup/sample windows.
- No target changes while this program is in progress.

Existing component gates remain authoritative and should be extended to the Simple-DDGI path where necessary:

- DdgiLow update p95 at or below 0.75 ms.
- DdgiMedium update p95 at or below 1.0 ms.
- DdgiHigh update p95 at or below 1.5 ms.
- DdgiUltra update p95 at or below 2.5 ms.
- CPU DDGI scheduler p95 at or below 300 microseconds.
- GPU DDGI scheduler p95 at or below 250 microseconds.
- DdgiHigh atlas memory at or below 192 MiB.
- No candidate-buffer overflow in steady state.
- Warmed visible/local/cascade-0 probe fractions at or above 0.80.

## 4. Priority Map

| Priority | Problem | Production solution | Quality risk |
| --- | --- | --- | --- |
| P0 | Repeated bindless descriptor updates cause a frame-sized NVIDIA driver wait | Safe-point, generational descriptor publication | None |
| P0 | Profiling markers and shader symbols depend on validation or alter build flags | Separate shipping, profile-symbol, and investigation configurations | None |
| P1 | Render-graph resource bindings and async plans are rebuilt every frame | Immutable, generation-keyed frame resource plans | None |
| P1 | Six draw-command lists are rehashed every frame | Revisioned immutable draw packets | None |
| P1 | Simple-DDGI scheduler repeatedly scans every probe | Event-driven state, SoA metadata, persistent work-class queues | None if equivalence-gated |
| P1 | Thousands of CPU-generated sparse atlas copies | GPU publication pass driven directly by the update queue | None if value-equivalent |
| P1 | Emissive selection discards lighting energy | Hierarchical full-energy emissive distribution | Quality improvement |
| P2 | Forward and decal draws dominate GPU time | Typed render queues, bounded shader variants, coherent DDGI evaluation | Requires image gates |
| P2 | Transport solve and blend exceed tier budgets | Coherent GPU work generation and real convergence retirement | Requires temporal gates |
| P3 | Async compute has no useful eligible overlap | Enable after ownership is immutable and only on measured net benefit | Synchronization risk |

## 5. Phase 0 — Trustworthy Profiling and Release Evidence

### 5.1 Build configurations

Create three deliberate configurations:

#### ShippingPerformance

- Release managed code.
- Shipping shader optimization policy.
- Validation disabled.
- Detailed investigation counters compiled out.
- Vulkan object names and command-buffer labels enabled.
- No overlays, screenshot readbacks, or profiler-specific work in timed windows.

#### ProfileSymbols

- Same managed and shader optimization policy as ShippingPerformance.
- Matching portable PDBs for all Njulf assemblies.
- SPIR-V NonSemantic debug information and embedded source.
- Vulkan object names and pass labels enabled.
- Used for source attribution.

#### DetailedInvestigation

- Validation and synchronization validation selectable.
- Expensive diagnostic atomics and readbacks allowed.
- Explicitly marked timing-ineligible in artifacts and benchmark gates.

### 5.2 Shader build integrity

The current NJULF_SHADER_PROFILE option combines -gVS with -Os. Separate debug information from optimization policy.

For every shader artifact, record:

- Compiler executable and version.
- Full compiler command line.
- Source and include dependency hashes.
- SPIR-V hash.
- SPIR-V hash after stripping debug/non-semantic information.
- Build configuration and Git commit.

A ProfileSymbols shader must reduce to the same semantic SPIR-V as ShippingPerformance after debug information is stripped. If it does not, the profile build may be used for attribution but never as the absolute performance baseline.

### 5.3 Debug-utils architecture

Separate these concerns in VulkanContext:

- Instance validation layers.
- Debug messenger and validation message policy.
- VK_EXT_debug_utils loading.
- Object naming and command-buffer labels.

Debug-utils labels must work in optimized validation-off captures. The runtime flag should control label emission without controlling validation-layer enablement.

References:

- https://docs.nvidia.com/nsight-graphics/UserGuide/configure-application.html
- https://docs.nvidia.com/nsight-graphics/UserGuide/shader-debugger-overview.html

### 5.4 Capture provenance

Every benchmark/capture manifest must include:

- Git commit and dirty-state identity.
- Build and capture configuration.
- Shader manifest identity.
- GPU vendor/device/driver and Vulkan capabilities.
- Resolution and quality tier.
- Validation, layer, counter, and marker status.
- Scene, bookmark, random seed, warmup frames, sample frames.
- CPU/GPU timing source and timestamp mode.
- Clock, power, temperature, and thermal-throttling evidence where available.
- Paths and hashes for raw benchmark, dotTrace, Nsight Systems, and Nsight Graphics artifacts.

### 5.5 Phase 0 exit criteria

- Nsight Systems displays named renderer passes with validation disabled.
- Nsight Graphics displays shader source, functions, and call-site attribution.
- Profile and shipping semantic SPIR-V parity is verified or explicitly rejected.
- ProductionTiming cannot enable detailed counters.
- A clean Release validation-off baseline is captured for every required scene.
- Instrumentation overhead is measured and remains within an approved bound.

## 6. Phase 1 — Safe, Generational Descriptor Publication

### 6.1 Target architecture

Introduce a DescriptorPublicationService that owns all descriptor mutation.

For every descriptor slot, track a typed identity containing:

- Descriptor kind.
- Allocation generation.
- Buffer or image-view handle.
- Sampler.
- Image layout.
- Buffer offset and range.
- Logical owner and retirement generation.

Registration APIs become idempotent. If the desired identity equals the published identity, the API returns Unchanged and performs no Vulkan call.

### 6.2 Frame-safe publication

- Maintain a descriptor set per frame in flight for fixed engine bindings.
- Track desired and applied generation per set and slot.
- Flush changed fixed descriptors only after the corresponding frame fence/timeline safe point.
- Keep dynamic texture slots immutable after publication.
- Replace a dynamic resource by allocating a fresh generational slot and publishing its index through frame data.
- Retain old resources and slots until the last timeline value that can reference them has completed.
- Remove ordinary resource-transition calls to device-wide WaitIdle.

This follows Vulkan descriptor lifetime rules and avoids modifying descriptor state while prior work may consume it:

- https://docs.vulkan.org/spec/latest/chapters/descriptorsets.html
- https://docs.vulkan.org/tutorial/latest/Building_a_Simple_Engine/Advanced_Topics/Descriptor_Indexing_UpdateAfterBind.html

### 6.3 Simple-DDGI integration

- Make SimpleDdgiSampledAtlas.EnsureCapacity a pure capacity/resource-generation operation.
- The stable-capacity branch must not call Register.
- Publish sampled-atlas descriptors only after actual creation/reallocation.
- Replace WaitIdle-based atlas replacement with allocation, publication, and deferred retirement.
- Keep centralized descriptor idempotence even after fixing the local caller so future callers cannot reproduce the problem.

### 6.4 Diagnostics

Add counters for:

- Desired descriptor changes.
- No-op registration requests.
- Actual Vulkan descriptor writes.
- Deferred writes.
- Per-frame-set generation lag.
- Retired and reclaimed slots/resources.
- Safe-point flush duration.

### 6.5 Tests

Add or extend tests for:

- Zero actual descriptor writes after warmup.
- Stable capacity over at least 10,000 stationary frames.
- Resource resize and quality-tier transition.
- Texture and material hot reload.
- Heap recreation.
- Two and three frames in flight.
- Failed descriptor write rollback.
- Old-resource lifetime until the last consumer completes.
- Device loss/disposal retry behavior.

### 6.6 Phase 1 exit criteria

- Zero steady-state Vulkan descriptor writes.
- No nvoglv64.dll wait below sampled-atlas capacity handling.
- No device-wide idle during ordinary resize, hot reload, or quality transition.
- Clean standard, synchronization, and GPU-assisted validation.
- Exact image equivalence in stationary scenes.
- CPU frame pacing falls toward actual managed/recording work and no longer serializes frame submission behind the previous GPU frame.

## 7. Phase 2 — Immutable Frame Resource and Draw Plans

### 7.1 Frame resource catalog

Create a FrameResourceCatalog populated by resource managers when allocations change.

Each entry has:

- Typed resource identity.
- Vulkan handle and exact range/subresource.
- Allocation generation.
- Frame/history index.
- Lifetime and permitted queue families.
- Initial ownership, layout, stage, and access state.

Resource managers publish new catalog generations only after a meaningful allocation or ownership-capability change.

### 7.2 Immutable render-graph plans

Construct immutable RenderGraphResourcePlan variants from the catalog:

- Prebuild plans for every frame/history slot.
- Rebuild only on resize, allocation, feature, history, or queue-configuration generation changes.
- Replace string-generated hot-path keys with typed value identities.
- Deduplicate through typed hash sets.
- Group ranges by physical handle and sort them for O(n log n) overlap validation.
- Perform exhaustive validation when the plan is constructed.
- Consume prevalidated arrays in the Release frame path.

### 7.3 Async planning

Separate:

- Stable resource ownership plan.
- Feature/path eligibility.
- Timing-policy state.
- Per-frame selection.

BuildAsyncComputePlan should select from cached candidates. It must not enumerate every render target, manager buffer, particle resource, material texture, or environment texture every frame.

### 7.4 Revisioned draw packets

Introduce immutable DrawPacketSet objects:

- Increment topology/classification revisions when object membership, geometry, materials, shadow classification, or pass classification changes.
- Keep dynamic transforms and skinning data separate from command topology.
- Compute an integrity hash only when a packet set is built.
- Store and reuse the signature instead of hashing six draw-command lists every frame.

### 7.5 Phase 2 exit criteria

- Zero steady-state concrete resource-plan rebuilds.
- Zero steady-state draw-command rehashes.
- Zero hot-path allocations from planning.
- Async plan selection below 100 microseconds p95 when enabled but ineligible.
- Complete invalidation tests for resize, material reclassification, mesh compaction, history rotation, frame-resource replacement, and queue-family changes.
- Stale plans are rejected without mutating ownership state.

## 8. Phase 3 — Persistent Simple-DDGI Scheduling

### 8.1 Data model

Replace repeated all-probe scans with:

- Structure-of-arrays probe runtime state.
- Dirty/state-transition bitsets.
- Persistent per-volume, per-work-class queues.
- Incremental convergence and lifecycle counters.
- lastUpdatedFrame values instead of incrementing every probe age every frame.
- Stable round-robin cursors and deterministic ordering keys.

### 8.2 Event-driven transitions

Events move probes between queues:

- New/fresh probe.
- Clipmap scroll exposure.
- Regional or global lighting dirtiness.
- Geometry/material/emissive revision.
- Visibility importance transition.
- Relocation retry.
- Source refresh.
- Cached-solver maintenance.
- Converged/stable transition.
- Watchdog/safety refresh.

### 8.3 Visibility handling

- Reuse visibility classification until the camera/frustum generation changes.
- Update affected clipmap slabs and frustum boundary tiles during normal movement.
- Retain a vectorized single-pass classification fallback for large camera changes or invalidated spatial metadata.
- Classify each affected probe once and derive work class, reservations, telemetry, and convergence state from that snapshot.

### 8.4 Equivalence rollout

Add a CPU shadow mode that runs legacy and new schedulers against identical state.

Compare:

- Exact probe selection and order.
- Per-volume quotas.
- Work-class reservations and use.
- Source-refresh and cached-solver cohorts.
- Invalid and duplicate probes.
- First-update latency.
- Dirty convergence latency.
- Maintenance fairness.
- Lifecycle and convergence counters.

Use deterministic, randomized long-running sequences covering scroll, teleports, moving lights, moving geometry, emissive revisions, quality changes, and low budgets.

### 8.5 Phase 3 exit criteria

- Exact selection equivalence for the compatibility rollout.
- Zero scheduling allocations.
- CPU scheduler p95 at or below 300 microseconds.
- No worsening of first-update or convergence latency.
- No starvation of maintenance, retry, dirty, or visible-zero-support classes.
- Periodic diagnostic audits confirm incremental counters match a full reference scan.

Only after exact parity is established may scheduling policy itself be changed, and those changes require separate quality and latency evidence.

## 9. Phase 4 — GPU-Driven DDGI Publication

### 9.1 Initial production design

Keep the canonical SSBO representation as the reference during migration and add an explicit SimpleDdgiPublishPass.

The pass:

- Consumes the GPU-visible probe update queue and update count.
- Dispatches one workgroup per updated probe.
- Reads completed trace/blend transport data.
- Publishes canonical irradiance/visibility values.
- Writes the storage/sampled image representation for forward filtering.
- Leaves untouched probes unchanged.
- Runs without CPU readback, sorting, or region construction.

### 9.2 Synchronization

Declare source, canonical, and sampled resources in the render graph.

Use synchronization2 with:

- Exact compute-write source scope.
- Exact subsequent compute/fragment read destination scope.
- Explicit queue-family ownership only when queues differ.
- No all-commands barriers unless an audited fallback requires them.

Reference:

- https://docs.vulkan.org/guide/latest/synchronization.html

### 9.3 Remove legacy publication paths

After parity:

- Remove CPU sorting of updated probe indices.
- Remove 2,048-region buffer copies.
- Remove sparse buffer-to-image region arrays.
- Remove MaxPartialCopyRegionsPerGroup and the whole-group fallback.
- Remove associated scratch arrays and CPU diagnostics.

### 9.4 Canonical storage-image ADR

After the dual-write publication pass is stable, evaluate whether storage images should become canonical.

The ADR must compare:

- GPU time and barrier cost.
- Forward sampling performance.
- Jacobi/history semantics.
- Frames-in-flight hazards and versioning.
- Total memory against tier budgets.
- Device format/storage-image capabilities across vendors.
- Value parity and temporal stability.

Do not remove the canonical SSBO merely to simplify the code unless the replacement produces a measured total-frame win.

### 9.5 Phase 4 exit criteria

- Zero steady-state sparse VkBufferCopy and VkBufferImageCopy region lists.
- Zero CPU update-index sorting.
- No whole-group copy fallback.
- Canonical and sampled values agree within the approved representation tolerance.
- Tests cover duplicate indices, group boundaries, untouched probes, empty queues, full queues, frame overlap, resize, quality rollback, and queue transfer.
- Publication CPU cost is negligible and DDGI update p95 moves toward the tier budget.

## 10. Phase 5 — Full-Energy Emissive Sampling

### 10.1 Replace the fixed global table

Replace the fixed 256-entry top-power table with a hierarchical distribution:

- Cook a per-primitive or per-mesh emissive triangle table.
- Record stable IDs, area, covered radiance, total emitted power, flags, and exact triangle PDF.
- Build a runtime instance/cluster alias table.
- Sample the instance/cluster first and triangle second.
- Use the product of both probabilities in the estimator.
- Use 32-bit offsets/counts instead of a packed 16-bit global index.

### 10.2 Static and dynamic ownership

- Cache static cooked tables.
- Rebuild only affected runtime instance distributions.
- Give dynamic and skinned emitters an explicit dynamic/proxy representation.
- Preserve the existing estimator-ownership contract so direct hits, next-event estimation, proxy fallback, and cached multi-bounce remain mutually correct.
- Defer old table/buffer retirement through the descriptor/resource lifetime system.

### 10.3 Capacity policy

If a memory policy prevents representing every triangle:

- Cluster triangles while preserving total power and sampling probability.
- Or use an unbiased inclusion/reservoir method with the inclusion probability in the PDF.
- Never silently discard positive-energy candidates.
- Report representation error and variance separately from skipped energy.

### 10.4 Phase 5 exit criteria

- DdgiEmissiveSkippedEnergyFraction equals zero for supported content.
- Every positive-energy source has nonzero sampling probability.
- Probability normalization and alias-table integrity tests pass.
- Monte Carlo tests confirm unbiased mean and bounded variance.
- No static-scene per-frame rebuild or upload.
- Emissive-room HDR and temporal gates pass.
- Memory remains within the selected tier budget.

## 11. Phase 6 — Forward, Decal, and DDGI Gather Architecture

### 11.1 Attribute the two dominant draws

Recapture after Phase 0 and identify:

- Pass and render-queue ownership.
- Pipeline and shader variant.
- Source functions/lines.
- Register pressure and occupancy.
- Texture/global-memory dependency chains.
- Meshlet/material/receiver composition.

Do not assume the 42.0 ms and 34.4 ms actions are particular buckets until labels and symbols prove it.

### 11.2 Typed render queues

Create explicit submission classes:

- Opaque.
- Masked.
- Transparent.
- Geometry decal.
- Foliage/specialized geometry where required.

Geometry decals must have an explicit pass, metrics, pipeline contract, and material ABI. They must not be hidden under TransparentPasses when no transparent objects are present.

### 11.3 Controlled shader variants

Use a bounded pipeline-key matrix for high-value static decisions:

- GI receiver versus no-GI receiver.
- Simple-DDGI versus other GI modes.
- Decal versus surface shading.
- Minimal versus full material inputs.
- Opaque versus masked/transparent behavior.

Requirements:

- Prevent uncontrolled permutation growth.
- Prewarm/cache shipping variants.
- Make pipeline identity visible in captures.
- Compile detailed diagnostic atomics into investigation-only variants.
- Keep the shipping variant free from those code paths and register-pressure effects.

### 11.4 Volume gather

- Build compact per-tile or per-region volume candidate metadata.
- Resolve the primary volume before the expensive gather.
- Mark the actual transition band explicitly.
- Evaluate a second volume only for pixels in that transition band.
- Preserve the current coarser-volume fallback and ownership semantics.
- Store volume constants compactly and access them coherently.

### 11.5 Environment fallback

Create a formal EnvironmentFallbackEvaluator contract:

1. Add detailed-mode histograms for fallback weight and radiometric contribution.
2. Skip far-field visibility only when the contribution is exactly zero.
3. Define a conservative upper bound using fallback weight, environment radiance, visibility, and receiver response.
4. Evaluate lower-frequency or temporally cached sky visibility only against locked HDR and temporal references.
5. Include depth/normal rejection and camera-cut invalidation for temporal data.
6. Do not reuse an arbitrary epsilon unless its maximum HDR error is proven.

### 11.6 Shader optimization policy

After source attribution:

- Hoist invariants.
- Reduce live ranges and register pressure.
- Group coherent material/GI paths.
- Remove provably dead outputs per specialized material contract.
- Improve constant/parameter locality.
- Verify generated SPIR-V and vendor occupancy after every material change.
- Preserve numeric precision unless a separate approved accuracy study permits a change.

### 11.7 Phase 6 exit criteria

- Opaque and decal GPU time is separately attributable.
- No production diagnostic atomics.
- Existing GI accuracy oracles remain within their scene-specific 2–10 percent tolerances.
- Sponza receiver ROIs use approved linear-HDR references.
- NVIDIA HDR-FLIP p95 is at or below 0.08.
- Temporal p95 is at or below 0.03 where temporal evidence is required.
- No fallback ownership, thin-wall leakage, transition seam, direct-lighting, or decal-compositing regression.
- Forward/decal p95 meets the component budget derived from the locked whole-frame SLO.

## 12. Phase 7 — Transport Coherence and Real Convergence

### 12.1 GPU work generation

- Generate and compact transport work on the GPU.
- Bin work by volume, cell/locality, source-refresh requirement, and solve class.
- Use indirect dispatch from the final compacted count.
- Load stable volume parameters once per workgroup where profitable.
- Improve coherent atlas access and ray-query traversal.
- Preserve the sampling sequence and arithmetic during the first layout-only migration.

### 12.2 Convergence

Make convergence an explicit state machine:

- Track error/variance evidence per probe.
- Track lighting, geometry, material, emissive, volume, and clipmap generations.
- Invalidate only affected probes where the dependency graph permits it.
- Retire probes that satisfy stable evidence.
- Retain bounded maintenance and watchdog refresh.
- Define maximum staleness.
- Protect visible, new, dirty, and scroll-exposed probes before background maintenance.

Adaptive reduction is allowed only after:

- The fixed-budget reference and adaptive sequence are captured together.
- HDR and temporal comparisons pass.
- First-update and convergence latency stay within declared bounds.
- Moving lights, moving objects, teleports, scroll, and newly exposed probes recover correctly.

### 12.3 Async compute

Evaluate async compute only after descriptor publication, immutable resource plans, GPU publication, and transport optimization are complete.

An async path may ship only when:

- Ownership transfers and barriers pass synchronization validation.
- Nsight shows useful graphics/compute overlap.
- Transfer/barrier/submit overhead is included.
- Whole-frame p95 improves by the existing absolute and relative policy thresholds.
- Frame pacing and memory use do not regress.
- Auto mode can demote the path when workload conditions change.

Async enabled is not a success criterion; lower total frame time is.

### 12.4 Phase 7 exit criteria

- DdgiHigh schedule/trace/blend/relocate/publish total p95 is at or below 1.5 ms.
- Stationary probes converge and retire from the full update budget.
- Maintenance prevents stale lighting without dominating the queue.
- Recovery latency passes every dynamic validation scene.
- Async paths ship only where measured net benefit is positive.

## 13. Phase 8 — CI, Hardware Qualification, and Rollout

### 13.1 Pull-request gates

Every PR:

- Restore in locked mode.
- Release build.
- Complete unit/oracle suite.
- Shader build and semantic-parity checks.
- Scheduler equivalence tests.
- Descriptor publication/lifetime tests.
- Atlas publication value tests.
- No new unbounded allocations or diagnostic work in production paths.

### 13.2 Nightly hardware gates

On self-hosted Vulkan runners:

- ShippingPerformance capture with validation off.
- Standard validation smoke.
- Synchronization validation smoke.
- GPU-assisted validation smoke.
- Fixed Release performance scenes.
- Raw artifacts uploaded with provenance.

### 13.3 Release qualification matrix

Required device classes:

- NVIDIA reference discrete GPU.
- AMD discrete GPU.
- Intel integrated or discrete GPU.
- Lower-memory ray-query-capable system.

Required scenes:

- GiSponzaRightWallStationary.
- GiCornellRoom.
- GiLongCorridorOcclusion and thin-wall leak case.
- GiLocalVolumeStreaming.
- GiFastTraversalTeleport.
- Emissive material room.
- Moving point light.
- Moving rigid object.
- Foliage/plaza receiver case.

Required runtime stress:

- Resize and swapchain recreation.
- Minimize/restore and focus changes.
- Quality-tier changes.
- Texture/material hot reload.
- Scene reload.
- Long soak.
- Memory-pressure and budget transitions.
- Multiple frames-in-flight configurations.

### 13.4 Statistical policy

- Warm the GPU and stabilize clocks before timed runs.
- Record clock, power, temperature, and thermal state.
- Use multiple independent runs.
- Store median, p95, variance, and outlier disposition.
- Reject captures with validation, diagnostic counters, overlays, screenshot work, mismatched shaders, or thermal invalidity.
- Never replace an approved baseline without review and a recorded reason.

### 13.5 Rollout policy

Every architectural replacement receives:

- An internal feature flag.
- Old/new shadow or A/B evidence.
- A rollback boundary.
- Diagnostics identifying the selected path.
- A deletion criterion for the old path.

Feature flags must not become permanent duplicate architectures. Remove the legacy implementation after the replacement passes the complete release matrix and rollback window.

## 14. Proposed ADR and Change Sequence

1. Profiling configurations, shader manifest, and debug-utils separation.
2. Descriptor publication and lifetime ADR.
3. Central descriptor state cache and metrics.
4. Per-frame fixed descriptor sets and immutable dynamic slots.
5. Simple-DDGI sampled-atlas integration and removal of steady re-registration.
6. Frame resource catalog and immutable render-graph plan ADR.
7. Revisioned draw packet implementation.
8. Simple-DDGI scheduler state-machine ADR and shadow harness.
9. Persistent scheduler implementation and parity rollout.
10. GPU DDGI publication ADR and pass.
11. Removal of legacy sparse publication.
12. Hierarchical emissive sampling ADR and implementation.
13. Typed decal/transparent render queues.
14. Controlled forward shader variants and profiling attribution.
15. DDGI volume-gather and environment-fallback contract work.
16. GPU transport binning and convergence state machine.
17. Measured async-compute qualification.
18. Multi-vendor release qualification and legacy-path removal.

Each item should be independently reviewable and must leave the renderer in a working, gated state. Do not combine descriptor lifetime, render-graph ownership, scheduler policy, and shader math changes into one unreviewable migration.

## 15. Quality-Neutral Wins

These should be implemented before any approximation:

- Idempotent, safely published descriptors.
- Immutable render-resource plans.
- Revisioned draw packets.
- Exact scheduler-equivalent data structures.
- GPU publication of the same canonical values.
- Investigation-only counters and atomics.
- Validation-independent labels and matching symbols.
- Work reordering that preserves sampling and arithmetic.
- Deferred resource retirement instead of device-wide idle.

Potentially quality-affecting work must remain separate:

- Fallback contribution thresholds.
- Lower-frequency or temporal sky visibility.
- Second-volume gather suppression.
- Adaptive convergence and maintenance reduction.
- Emissive clustering/proxies.
- Numeric precision or format changes.

Each requires an explicit error model, approved HDR evidence, temporal evidence where applicable, and a rollback path.

## 16. Definition of Done

The renderer is production-ready for this workload only when all of the following are true:

- No frame-scale host wait occurs during steady descriptor handling.
- Steady descriptor writes are zero.
- Steady render-resource plan rebuilds are zero.
- Steady draw-command rehashes are zero.
- CPU scheduler p95 is at or below 300 microseconds.
- DdgiHigh update p95 is at or below 1.5 ms.
- DdgiHigh atlas memory is at or below 192 MiB.
- Emissive skipped-energy fraction is zero.
- Candidate and scheduler overflow gates pass.
- Warmup, first-update, convergence, ownership, leak, and recovery gates pass.
- Approved HDR-FLIP and temporal gates pass.
- Standard, synchronization, and GPU-assisted validation are clean.
- The locked whole-frame SLO passes on the reference GPU with production counters off.
- Matching symbols, shader manifests, raw captures, approved baselines, and qualification reports are retained as release evidence.
- The legacy descriptor, scheduling, publication, and shader paths have been removed after their replacements complete the rollout window.

## 17. Immediate First Milestone

Implement Phase 0 and Phase 1 first.

That milestone:

- Makes future captures trustworthy.
- Removes the known frame-sized driver wait.
- Restores legal, explicit descriptor/resource lifetime management.
- Recovers CPU/GPU overlap.
- Establishes the instrumentation needed to attribute the two dominant GPU draws.

It will not, by itself, meet the final frame target because the GPU workload remains approximately 84 ms. It is nevertheless the required foundation for every subsequent optimization and is the highest-confidence quality-neutral win.
