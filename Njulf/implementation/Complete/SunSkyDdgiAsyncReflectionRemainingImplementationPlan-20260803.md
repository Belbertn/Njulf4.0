# Sun/Sky, DDGI, Async Compute, and Reflection Remaining Implementation Plan

Status: Ready for implementation  
Date: 2026-08-03  
Predecessor: [SunSkyDdgiAsyncReflectionCorrectnessPlan-20260802.md](SunSkyDdgiAsyncReflectionCorrectnessPlan-20260802.md)  
Scope: Remaining implementation and production-evidence work after the initial correctness foundations landed in the working tree

## 1. Required outcome

Finish the three renderer closures without weakening the safety and performance
contracts already established:

1. Moving procedural atmosphere remains frame-current for visible sky, direct
   lighting, and shadows, while every admitted Simple-DDGI atmosphere cohort
   drains and crosses an explicit publication/propagation release boundary.
2. Async compute remains graphics-safe by default. Each atomic path becomes
   eligible for `Auto` only after its exact queue topology, stages, accesses,
   resources, layouts, lifecycle, output equivalence, and measured benefit are
   certified.
3. Authored local reflection probes execute real, coherent scene captures,
   build complete GGX mip chains in private scratch storage, copy into stable
   published layers, and expose those layers only after GPU completion.
4. Resize, reload, deletion, recapture, and retirement use frame/timeline
   completion. None of these ordinary paths call `WaitIdle`.
5. Disabled and enabled-idle operation adds no GPU work, no metadata upload, no
   per-frame probe scan/sort, and no steady-state managed allocation.
6. The deterministic scenario matrix, Vulkan validation matrix, locked A/B
   performance captures, memory limits, and long soak all pass before a feature
   is promoted.

Correctness, bounded lifetime, and performance are equal release gates. A path
that is functionally correct but not validated or profitable remains on its
existing graphics/global-environment fallback.

## 2. Current implementation baseline

The following foundations are present and must be extended rather than
replaced.

### 2.1 Atmosphere and DDGI

- `GiAtmosphereAdmissionController` is an allocation-free latest-wins state
  machine with requested, pending, admitted, coalesced, and restart state.
- `EnvironmentManager` owns separate requested and admitted GI atmosphere
  frames and uploads the GI buffer only when admission changes.
- Global specular prefilter requested/building/published generations are
  independent of admitted GI generations.
- `VulkanRenderer.LightingVersions` exposes a read-only cross-system generation
  snapshot.
- `SimpleDdgiVolumeManager` exposes source-cohort state, stale populations,
  source generations, ray-equivalent target/shortfall telemetry, and an initial
  public tracking-state vocabulary.
- The optimized persistent scheduler, stable-capacity path, compact dispatch,
  convergence retirement, and incremental telemetry remain intact.

The current admission integration conservatively treats zero source-stale
probes as both visible publication and minimum propagation completion. That is
safe against source starvation but is not the final release contract.

### 2.2 Async compute

- `QueueStageCapabilities` rejects unsupported synchronization2 stage/access
  scopes using the actual graphics and compute queue flags.
- Async resource usages and compiled transfer scopes are validated before
  recording; structured failures include the relevant queue, stages, accesses,
  and VUIDs 09675/09676.
- `Auto` cannot time or submit an uncertified path. Every current path remains
  `Uncertified` in `AsyncComputePassCatalog`.
- `ForceEnabledForValidation` requires an explicit atomic
  `--async-compute-path` harness selector.
- A Vulkan validation-error increase following an active compute submission
  latches the process onto the graphics fallback.
- Hi-Z no longer records cross-pass depth or pyramid entry/exit transitions.
  The render graph owns those transitions; `HiZBuildPass` retains only its
  per-mip compute-write-to-compute-read dependency.

The remaining async work is transactional state projection, bounded compiled
variant caching, exact validation attribution, per-path audit/certification,
and profitability evidence.

### 2.3 Reflection probes

- `ReflectionProbeCaptureScheduler` provides a fixed-capacity, GPU-independent
  ticket state machine with latest-wins requests, coherent immutable ticket
  versions, face/mip work units, copy commit, completion-token publication,
  retry/failure state, and old-capture retention.
- `Scene.ReflectionProbeRevision` advances on add, remove, and relevant probe
  property mutation.
- `ReflectionProbeManager` caches active selection and skips unchanged metadata
  uploads.
- `RequestRecaptureAll` no longer removes a valid published capture.
- Explicit face, mip, concurrency, GPU-time, age, DDGI inclusion, and retry
  settings exist.
- Unit and source-contract tests cover the initial scheduler, revision, queue,
  and Hi-Z behavior.

The scheduler is not yet the manager's sole queue authority. There are no
production reflection capture/prefilter/publish passes, no scratch cube, and no
completion-polled publication. `ReflectionProbeManager` still exposes the old
direct-to-published capture API and still calls `WaitIdle` during ordinary
reallocation.

## 3. Invariants for all remaining work

### 3.1 Publication safety

- A sampled DDGI probe or local reflection layer is either an older complete
  result or a newer complete result. Cleared, partially written, mixed-version,
  and half-filtered data is never shader-visible.
- Command recording is not completion. Only a signaled fence/timeline token can
  authorize logical publication, layer reuse, or resource destruction.
- A ticket that has recorded its destination copy pins the destination layer
  and resource generation until completion, deletion-safe cleanup, or terminal
  device loss.
- A stale completion can release its owned resources but cannot mutate current
  metadata.

### 3.2 Version ownership

- `EnvironmentManager` owns visual, requested GI, admitted GI, and global
  specular generations.
- `SimpleDdgiVolumeManager` owns source, propagation, and static-convergence
  generations and supplies immutable scalar feedback to admission.
- Scene/material/light/AS/shader owners supply monotonic component revisions.
- `ReflectionProbeCaptureScheduler` owns requested/published per-probe capture
  versions and ticket serials.
- `VulkanRenderer` only aggregates read-only snapshots and performs atomic
  frame transactions. It does not become the mutation owner of subsystem
  generations.

### 3.3 Work and allocation bounds

- Atmosphere admission remains O(1) in probe count.
- Stable async-plan use performs a bounded key comparison and no graph walk.
- Reflection enabled-idle frames do not enumerate authored probes, rebuild
  metadata, update descriptors, issue timestamps, or record empty passes.
- Reflection work is limited by selected face/mip/copy units, predicted GPU
  cost, scratch slots, queue capacity, retry count, and maximum age.
- Every retained cache, queue, variant set, scratch set, and retirement list has
  a profile-defined capacity and deterministic overflow behavior.

### 3.4 Fallback behavior

- Missing first local captures sample the current global prefiltered
  environment.
- Recapture continues sampling the old local layer.
- Failed or deferred probes retain their last complete capture when one exists.
- Rejected/quarantined/unprofitable async work executes the identical graphics
  pass list in the current frame.
- Capacity rejection is diagnostic and recoverable; it never silently exceeds
  the component or global memory budget.

## 4. Dependency order

Implementation must follow this order because later work relies on earlier
lifetime and evidence contracts.

| Order | Deliverable | Blocks |
|---:|---|---|
| 1 | Deterministic scenarios, version diagnostics, and completion/retirement service | All promotion evidence and reflection lifetime work |
| 2 | Exact DDGI publication/propagation feedback and ray-capacity reservation | Bounded moving-atmosphere admission and versioned reflection recapture |
| 3 | Transactional async layout/owner plan and bounded plan cache | Correct per-path async certification |
| 4 | Reflection scheduler/manager unification and scratch memory transaction | GPU capture passes |
| 5 | Capture-view rendering contract and conditional graph passes | Useful local probe output |
| 6 | GGX filtering, destination copy, completion polling, and publication | First complete local captures |
| 7 | Dynamic recapture policy and per-path async promotion | Combined moving-sun operation |
| 8 | Locked performance evidence and soak | Production enablement |

## 5. Phase 0: evidence harness and lifetime primitive

### 5.1 Deterministic scenarios

Add these scenarios to the existing sample smoke/capture framework rather than
creating a second runner:

- `GiSponzaAnimatedAtmosphere`
  - fixed curtain camera and exposure;
  - fixed 30, 60, and 120 Hz variants;
  - `TimeScale = 60`, `GiSunStepDegrees = 0.25`, and
    `GiTargetSourceSweepSeconds = 8`;
  - fast tier: three achievable sweep intervals;
  - PR tier: five simulated minutes at 60 Hz;
  - nightly tier: 30 minutes.
- `GiSponzaFreezeAfterAtmosphereStep`
  - animate through multiple candidates;
  - freeze immediately after a pending request;
  - verify latest admission, source completion, propagation, then static
    convergence.
- `GiSponzaReflectionProbeLifecycle`
  - two authored probes;
  - initial capture, sun-driven recapture, resize, minimize/restore, shader
    reload, scene reload, delete/re-add, and shutdown.
- Retain `GiSponzaRightWallStationary` as the feature-off and idle-performance
  control.

Every report must contain fixed timestep, camera hash, scene/content revision,
shader-bundle hash, executable/worktree identity, GPU/driver, queue-family
indices and flags, settings fingerprint, build flavor, validation mode,
detailed-counter availability, and feature-isolation variant.

### 5.2 Completion-based retirement service

Create a renderer-owned bounded `GpuCompletionRetirementQueue` (or extend an
equivalent existing owner if one appears before implementation):

- records resource-generation ID, byte charge, completion primitive/value,
  and a noncapturing typed destruction action or compact resource-kind payload;
- polls already-owned frame fences/timeline values once per frame without
  waiting;
- destroys only signaled generations;
- has fixed capacity sized for frames-in-flight plus declared feature
  coexistence;
- rejects a new generation transaction before allocation if peak live plus
  retired memory or retirement slots would exceed budget;
- drains under the normal renderer shutdown wait, not an extra feature-owned
  wait;
- exposes active count, bytes, oldest age, peak count/bytes, and capacity
  rejections.

Do not implement retirement with delegates or closures allocated per resize.
Use retained records and explicit resource-kind destruction paths.

### 5.3 Baseline evidence

Before behavioral promotion, capture current-binary reports for:

- graphics-only stationary and animated scenarios;
- normal `Auto` proving zero uncertified compute submissions;
- each forced atomic path showing current validation status;
- reflection lifecycle showing the current queue/published baseline;
- enabled-idle reflection cost;
- tracked memory before and during the maximum planned scratch/retired
  coexistence estimate.

Phase 0 exits when the harness is deterministic, graphics-only is validation
clean, report identity locks work, and the retirement service passes unit tests
without an ordinary-path wait.

## 6. Phase 1: finish the DDGI atmosphere contract

### 6.1 Publish one immutable cohort-feedback snapshot

Add an allocation-free `SimpleDdgiAtmosphereCohortFeedback` value snapshot
created from the manager's already-maintained incremental state after each
completed scheduler update. It must include:

- volume-table/resource generation;
- admitted source generation;
- participating and source-stale probe counts;
- visible-priority participating, source-ready, and published counts;
- whether every visible-priority current-generation probe has published once;
- propagation generation and whether the configured minimum propagation
  boundary completed after source readiness;
- source-cohort start/completion frame and cumulative starts/completions;
- target probes/rays per frame, admitted probe/ray budget, scheduled source
  probes/rays, capacity shortfall, and minimum achievable sweep seconds;
- static convergence pending/completed generation;
- stale-readback/resource-generation rejection count.

Build this snapshot from counters, queues, histograms, and generation records
already updated incrementally. Do not scan `_probeSourceLightingGenerations` or
any full probe array on the render thread.

### 6.2 Use real release conditions

Replace the renderer's current zero-stale proxy with the feedback snapshot.
Admission may release the latest candidate only when:

1. the admitted cohort has zero stale participating probes;
2. every current-generation visible-priority participant has published at
   least once;
3. the configured minimum propagation boundary has completed;
4. the feedback resource/source generation matches the currently admitted
   transaction.

Stale feedback holds admission for one more frame. It never authorizes an
advance. Disabled/nonparticipating/nonconsuming backends retain immediate
admission.

Add an explicit quiet-period input so a stopped candidate stream moves through
`TrackingPropagation` to `StaticConverging` and `StaticConverged`; continuous
motion ends in `TrackingBounded`, not a permanent failed-convergence state.

### 6.3 Reserve ray-equivalent capacity

Move ray-equivalent capacity from telemetry into scheduler admission:

- derive the source-cache ABI ray count for each participating ring/tier;
- calculate cohort work as exact required source rays, not probe count times an
  unrelated maximum when tiers differ;
- reserve visible repair first, then a guaranteed remaining-cohort ray share,
  before background maintenance;
- never count maintenance-ray work as source completion unless it satisfies the
  cache ABI for that probe;
- preserve persistent queues and retained dispatch batches;
- expose target infeasibility before dispatch;
- calculate minimum achievable sweep seconds from bounded observed FPS and
  admitted ray capacity;
- hold the current generation while capacity-limited, then admit the newest
  pending candidate when it drains.

Observed FPS uses a bounded EWMA and ignores one-frame hitches for target
expansion. Deterministic scenarios use their authored nominal rate.

### 6.4 Generation enforcement

Audit trace, transport, blend, and publish transactions so that:

- source-cache reuse requires exact admitted source generation;
- publication requires exact source, transport, queue-transaction, and volume
  generation;
- relocation, scrolling, and slot reuse clear all relevant markers before the
  physical slot becomes sampleable;
- stale incremental readbacks cannot decrement the current cohort;
- mismatches fail closed in every build.

Add exact rejected-reuse and rejected-publication GPU counters only to
validation and `DetailedInvestigation`. Shipping retains CPU assertions and
aggregate state already required for scheduling, with no new diagnostic
atomics/readback.

### 6.5 DDGI acceptance

- Every admitted animated Sponza cohort completes.
- `starts - completions` never exceeds one.
- No ordinary atmosphere change causes a hard restart.
- Achievable cohorts finish within target plus 5% and two frames.
- First visible response occurs within two rendered frames after admission.
- Frozen input reaches static convergence within the existing convergence
  budget.
- Reuse/publication mismatch counters remain zero.
- Held candidates cause zero GI upload bytes and no dirty-signature advance.
- Admission/version handling adds at most 0.05 ms CPU P95 and allocates zero
  bytes on the stable path.

## 7. Phase 2: finish transactional async planning

### 7.1 Projected resource state

Add immutable committed and mutable projected state per concrete binding:

- allocation identity and resource generation;
- owner family/queue;
- current layout for exact image subresource ranges;
- last stage/access scope;
- active readers/writer needed by plan compilation.

Plan compilation clones state into retained scratch arrays indexed by binding
ID, not dictionaries allocated per frame. It produces entry barriers, internal
cross-segment transfers, exit barriers, semaphore edges, and the final projected
state. Commit occurs only after all command buffers for the matching accepted
plan are recorded successfully. Rejection, stale generation, skipped producer,
resize, or recording failure discards projection without changing committed
state.

Pass-local layout mirrors become debug-only callbacks from committed binding
state. A mirror mismatch is a validation failure; it is never repaired by
silently adopting the pass-local value.

### 7.2 Compiled transfer records

Represent release and acquire halves separately in the accepted plan:

- release: exact source stage/access and ignored destination scope;
- acquire: ignored source scope and exact first-consumer stage/access;
- immutable transfer identity;
- matching allocation generation, ranges, families, layouts, and timeline
  edge;
- same-family separate queues use ignored ownership indices but retain the
  semaphore and one layout transition;
- different families use a paired ownership transfer;
- adjacent ranges coalesce only when every scope/layout/family field matches.

`QueueOwnershipTransferRecorder` consumes these validated records directly and
does not reinterpret logical usages while recording.

### 7.3 Bounded plan-variant cache

Cache compiled plans by:

- queue topology/capabilities generation;
- async settings and certification revision;
- graph declaration generation;
- concrete binding generation;
- selected atomic path set;
- bounded executable-pass mask variant.

Use fixed-capacity enum-indexed/bitset storage with deterministic LRU or clock
eviction. Stable frames compare scalar generations/key data only. Record hits,
misses, evictions, rebuild time, and fallback reason. Zero-work masks must not
reuse transfers from an executing producer, and incidental frame history must
not grow the cache.

### 7.4 Validation attribution and quarantine

Extend validation-only message capture with monotonically sequenced events,
object handles/names, command-buffer labels, and submission-segment identity.
Retain a bounded registry of in-flight async segments keyed by frame slot,
timeline range, command-buffer identity, and active atomic path bitset.

- Attribute a message to an exact segment when labels/object identity permit.
- Quarantine every path in that segment for the process.
- If exact attribution is unavailable but async work was concurrently active,
  conservatively quarantine the active path set and record `AmbiguousSegment`.
- Validation-error frames never enter timing windows.
- Quarantine changes invalidate cached plans and all later frames use graphics
  for affected paths.
- Shipping builds retain only the compact quarantine latch/reason ID needed for
  fail-closed behavior; detailed strings/events are validation-only.

### 7.5 Path certification sequence

Certify one atomic unit at a time:

1. `HiZBuild`
2. `AmbientOcclusionBlur`
3. `Bloom`
4. `FarFieldClipmapBake`
5. `Fog`
6. `GpuParticles`
7. `SimpleDdgiUpdate`
8. `FullDdgiUpdate`
9. `SsgiChain`

For each path, add a checked-in audit record containing declaration/shader
evidence revision, atomic pass membership, concrete resources and ranges,
producer/first consumer, initial/final layouts and owners, queue topologies,
lifecycle report IDs, validation result, and linear-HDR/buffer equivalence
result. A relevant source/declaration/shader change invalidates certification.

Only after correctness certification collect at least three locked
graphics/async timing pairs. `Auto` promotion requires:

- at least 3% median GPU-frame improvement;
- no GPU-frame P95 regression;
- no material pass regression beyond existing comparer tolerance;
- no meaningful CPU record/submit regression;
- no first-consumer wait that consumes the predicted overlap.

Correct but unprofitable paths remain graphics-only.

### 7.6 Async acceptance

- Zero Vulkan warnings/errors for each path alone and certified combinations.
- No compute-only command buffer names a graphics-only stage.
- Planned and emitted release/acquire counts and identities match exactly.
- Committed state matches the next frame's first use.
- Stale/rejected projections never mutate committed state.
- Stable cache hits add at most 0.05 ms CPU P95 and allocate zero bytes.
- Inactive paths emit no compute submission, transfer, wait, or timestamp.
- Graphics and async output pass the existing oracle plus locked linear-HDR
  comparisons.

## 8. Phase 3: unify reflection logical ownership

### 8.1 Make the scheduler authoritative

Remove the parallel manager queue/in-flight/captured authority. The manager
retains stable authored-ID-to-layer mapping and Vulkan resources; the scheduler
becomes the sole authority for requested, queued, active, awaiting-completion,
published, retry, deferred, and failed state.

Required integration:

- register/unregister scheduler slots with stable layer allocation;
- translate load/manual/versioned recapture into one latest-wins request;
- retain old `HasPublishedCapture` until completion of a replacement;
- remove or internalize the old direct-to-published `TryBeginCapture` /
  `PublishCapture` API so no production caller can bypass completion;
- include ticket/layer/resource generation in every manager operation;
- implement bounded retries/backoff and `DeferredChangingScene` without queue
  duplication or per-frame spin;
- expose scheduler scalar diagnostics without constructing per-probe lists.

Before work starts, fix and test queue-slot replacement, deletion while queued,
deletion while copy-committed, layer recycling, serial wrap, retry exhaustion,
and capacity-full re-add behavior.

### 8.2 Version construction

Build `ReflectionCaptureVersion` from owner-maintained revisions:

- scene radiance/content revision;
- relevant static/dynamic light revision;
- admitted environment generation;
- completed DDGI tracking generation when DDGI capture is enabled;
- material/emissive revision;
- capture AS/resource generation;
- capture shader/settings revision.

Do not enumerate lights/materials or hash the scene per request. Cache the
current composite scalar version once per frame, then request only probes
affected by a changed revision where influence information exists.

### 8.3 Idle-path cache

Cache active selection, layer mapping, authored snapshots, metadata payload,
published flags, and descriptor generation. Rebuild only when scene probe
revision, settings signature, resource generation, or publication availability
changes. Compare retained payload bytes or a collision-safe revision tuple
before upload.

Enabled-idle acceptance is no authored-probe enumeration/sort, no allocation,
no metadata upload, no descriptor update, no graph pass, and no timestamp.

## 9. Phase 4: reflection scratch resources and memory transaction

### 9.1 Memory plan

Extend the reflection component memory plan with queried Vulkan allocation
requirements and allocator-attributed bytes for:

- one default RGBA16F cube scratch image with full mip chain;
- reusable depth target(s);
- slot-owned capture frame/light/environment snapshot buffer;
- face and mip views;
- descriptor/pipeline/query objects and host bookkeeping;
- published array generation;
- simultaneously retired published/scratch generations.

The payload lower bound remains:

```text
scratch bytes = 6 * 8 * sum(max(1, resolution >> mip)^2)
depth bytes   = depth target count * resolution^2 * depth bytes per pixel
```

The allocation decision uses actual memory requirements and allocator charge,
not only this formula. Reject or downscale before crossing either the reflection
component budget or 80% total tracked memory. Default to one scratch slot; more
requires explicit measured overlap benefit and peak-memory evidence.

### 9.2 Resource transaction

Create all images, views, reusable descriptors, pipelines, and timestamp ranges
when the reflection resource generation changes. Ordinary ticket advancement
creates none of them.

For published-array resize/reallocation:

1. calculate peak old + new + scratch + retired residency;
2. allocate and initialize the replacement privately;
3. copy compatible complete layers where possible;
4. wait by polling the transaction completion token in later frames;
5. switch descriptors and scheduler resource generation atomically;
6. retire the old generation through `GpuCompletionRetirementQueue`;
7. requeue only layers whose content could not be preserved.

Remove reflection-owned `WaitIdle` from allocation, resize, reload, deletion,
and disposal paths other than the renderer's normal terminal shutdown wait.

### 9.3 Scratch layout contract

- Mip 0: six raw captured radiance faces.
- Mips 1..N-1: GGX-prefiltered radiance.
- Scratch usage: color attachment, sampled, storage, transfer source/destination
  only where actually used.
- Published array: sampled plus transfer destination/source required for
  preservation; never used as filter scratch.
- Exact face/mip/layer subresource views are precreated and generation-owned.
- A scratch slot cannot start another ticket until its prior copy completion is
  signaled and publication/discard handling finishes.

## 10. Phase 5: capture-view rendering contract

### 10.1 Reusable render-view input

Refactor the minimum main-view rendering inputs into an immutable
`RenderViewContext`/`CaptureViewContext` value contract rather than cloning
`SceneRenderingData` or mutating the camera's frame state. It contains:

- view/projection/frustum and viewport/scissor;
- capture flags and recursion mask;
- pinned environment/direct-light snapshot generation;
- scene/material/light/AS generations;
- target color/depth views/layouts;
- retained visibility submission/list handle;
- feature mask excluding screen/camera-only effects.

Reuse prepared scene/material/light/AS data. A face may run retained GPU culling
or a bounded retained CPU visibility scratch list. It must not rebuild
materials, BLASes, or whole-scene uploads.

### 10.2 Capture shading

Add an explicit reflection-capture shader/view flag to the existing material
path. Capture includes:

- opaque and alpha-masked geometry;
- base color, metalness/roughness, normal maps, and emission;
- directional and local direct lighting;
- valid shadow visibility, preferring current ray-query visibility when
  camera shadow maps do not cover the probe;
- the ticket-pinned admitted atmosphere/global environment;
- optional DDGI only after a stable sampled generation can be pinned for all
  faces.

Capture excludes local reflection probes, SSR, SSGI, TAA, exposure, bloom, tone
mapping, UI, and transparency until a separate ordered contract exists. Output
is linear HDR.

If a non-snapshotted scene/material/light/AS generation changes before copy
commit, cancel at the next work-unit boundary, retain the old published layer,
and keep only the latest pending version. Repeated churn enters bounded
`DeferredChangingScene` state.

### 10.3 Cube convention

Extract one canonical face direction/up table shared with point-shadow cube
views and environment cube addressing:

| Face | Direction | Up |
|---:|---|---|
| +X | +X | -Y |
| -X | -X | -Y |
| +Y | +Y | +Z |
| -Y | -Y | -Z |
| +Z | +Z | -Y |
| -Z | -Z | -Y |

Unit-test view matrices, center/corner directions, handedness, and colored-axis
rendering. Do not leave duplicated face tables that can drift.

## 11. Phase 6: conditional graph passes and GPU publication

### 11.1 Graph resources

Add explicit resources for scratch radiance, scratch depth, capture snapshot
buffer, and published reflection array. Declarations name exact current-frame
readers and the tail destination write. Resource bindings include generation,
owner, layout, and exact subresource ranges.

Reflection work remains graphics-only for this closure. It is not an async
candidate.

### 11.2 `ReflectionProbeCapturePass`

- `WillExecute` only when one or more face units were selected within the CPU,
  face, scratch, and predicted GPU budgets.
- Runs after latency-critical main-view work where dependencies permit.
- Renders at most the selected bounded face count into scratch mip 0.
- Uses one ticket-pinned capture snapshot for every face.
- Reports actual recorded face count and timestamp range.
- Completing command recording advances only scheduler face progress, not
  publication.

### 11.3 `ReflectionProbePrefilterPass`

- Cannot execute until all six mip-0 faces for one ticket are complete.
- Reuse the existing environment GGX importance-sampling shader/include and
  sample sequence; factor its source/destination binding contract so local cube
  scratch uses the same kernel.
- Map roughness monotonically from mip 1 through the final mip.
- Process only selected bounded mip units.
- Keep all intermediate scratch layouts graph-authoritative.

### 11.4 `ReflectionProbePublishPass`

- Executes only after every face and mip is complete for the same ticket.
- Runs after all current-frame readers of the old published layer.
- Copies the exact six-face/full-mip scratch range into the pinned stable layer.
- Transitions the destination range to shader-read for a future frame.
- Records ticket serial, probe ID, layer, resource generation, frame slot, and
  fence/timeline value in a retained completion record.
- Marks the ticket copy-committed but does not call logical publication.

All three no-work paths emit no commands, barriers, ownership transfers,
queries, or submissions.

### 11.5 Completion polling

At the start of a later frame, poll already-owned completion primitives:

1. find signaled completion records in bounded order;
2. verify ticket serial, probe existence, stable layer ownership, and resource
   generation;
3. publish the completed version even if a newer version is pending;
4. mark metadata dirty and upload it in the normal frame transaction;
5. release the scratch slot;
6. retain/requeue the one latest pending version;
7. for deletion/stale generation, skip metadata mutation and release pinned
   lifetime only after completion.

No render-thread fence wait or synchronous query readback is allowed.

## 12. Phase 7: dynamic reflection policy

Replace dirty-edge recapture calls with version comparisons and coalesced
requests.

- Initial capture waits until scene/material/light/capture-AS resources are
  ready.
- Environment recapture keys from admitted environment generation, not every
  visible sun frame.
- If DDGI capture is enabled, key from the completed bounded-tracking
  generation and require a pinned sampled generation; otherwise record why
  DDGI is excluded.
- Enforce `MinimumEnvironmentRecaptureIntervalSeconds` and
  `MaximumEnvironmentCaptureAgeSeconds`.
- Prioritize visible/influential probes, then distance, authored priority, age,
  and stable ID tie-break.
- Update camera-dependent priority lazily only for bounded queued candidates;
  do not sort all authored probes per frame.
- Merge reason flags and retain exactly one pending version per probe.
- Geometry/material changes use influence bounds where available and
  conservatively request all otherwise.

Timestamp-history EWMA predicts face, mip, and copy unit cost per resolution.
With no history, schedule one smallest legal unit. If a single unit repeatedly
exceeds the 1.00 ms ceiling, explicitly downscale or disable dynamic capture for
that profile; never publish partial data or overrun every frame.

## 13. Diagnostics

### 13.1 Atmosphere/DDGI

- requested/coalesced/admitted/completed/static-converged generations;
- pending/admitted angular delta and age in frames/seconds;
- cohort starts, completions, hard restarts, and stale-feedback rejections;
- first visible publication, P50/P95/full sweep, first propagation boundary;
- target/admitted/scheduled source probes and rays;
- configured versus minimum achievable sweep seconds;
- tracking state and capacity-limited reason.

### 13.2 Async

- certification/evidence revision and topology scope per path;
- quarantine path bitset, reason, validation sequence, and attributed segment;
- plan-cache hit/miss/eviction/rebuild time;
- projected-state rollback/commit counts;
- planned/emitted release/acquire identities and counts;
- graphics/async timing windows and promotion/demotion reason.

### 13.3 Reflection

- authored, active, fallback-only, published, queued, in-flight,
  awaiting-completion, retry, deferred, and failed counts;
- requested/published version and age per probe only on explicit detailed
  snapshot requests;
- active face/mip progress and ticket serial;
- started/copied/published/failed counts this frame and cumulatively;
- face/prefilter/copy CPU record and GPU timing;
- scratch, published, metadata, views/descriptors, and retired memory;
- metadata upload bytes and idle skipped-upload count;
- last cancellation/failure/deferred reason;
- frames sampling global fallback because no first capture exists.

Shipping diagnostics use existing CPU state and timestamp history. Exact
mismatch/attribution counters and detailed progress lists remain validation or
explicit investigation features.

## 14. Test matrix

### 14.1 Unit/contract tests

- admission release with exact visible/propagation feedback;
- stale feedback generation rejection;
- ray-equivalent capacity across mixed rings and maintenance tiers;
- tracking/static state transitions and generation wrap;
- projected async state commit/rollback and stale-plan rejection;
- queue-stage/access aliases and ignored transfer halves;
- bounded plan-cache eviction and executable-mask changes;
- validation attribution/quarantine and timing exclusion;
- scheduler transition legality, coalescing, retry/backoff, and deferred churn;
- queue-full delete/re-add and stale completion;
- stable layer pinning during copy and resource retirement;
- reflection memory math versus mocked Vulkan requirements;
- capture-version construction from owner revisions;
- six cube orientations and subresource ranges;
- no metadata upload when probe revision/publication state is unchanged;
- serialized legacy capture-count migration to new budgets.

### 14.2 Shader/graph tests

- capture path disables local reflection recursion and camera-only effects;
- capture remains linear HDR;
- alpha-mask/material/emission inputs match the supported main material path;
- GGX roughness increases monotonically and uses the shared kernel;
- prefilter waits for six coherent mip-0 faces;
- publish waits for every mip and copies exact ranges;
- old-layer reads precede the destination copy;
- no-work reflection declarations record nothing;
- reflection passes are graphics-only;
- every async declaration validates against its selected queue.

### 14.3 Vulkan/lifecycle tests

- first frame, warm frames, and frames-in-flight wrap;
- same-family separate queues and distinct queue families;
- each forced async path alone and certified combinations;
- standard, synchronization, and GPU-assisted validation;
- resize, minimize/restore, swapchain recreate, shader reload, scene reload,
  quality change, probe deletion/re-add, and shutdown;
- resource generation change during every reflection state;
- validation message during active async work causes process quarantine;
- no `WaitIdle` attribution to reflection or ordinary async/GI operations.

### 14.4 Visual tests

- locked-exposure curtain and diffuse-interior HDR sequences;
- chrome and rough spheres for two local Sponza probes;
- colored-axis cube orientation and seam oracle;
- local geometry visible in captures;
- box projection and overlap blending;
- no recursive probe hall-of-mirrors;
- no exposure/post-processing baked into captures;
- old reflection remains visible throughout recapture;
- failure/stale completion never creates black or partial output;
- graphics/async linear-HDR equivalence.

## 15. Performance and memory gates

Measure `ShippingPerformance`, validation off, detailed counters absent, after
warmup, using at least three identity-locked feature-off/on pairs.

| Work | CPU P95 delta | GPU requirement | Additional gate |
|---|---:|---:|---|
| GI admission/version snapshot | <=0.05 ms | no new pass | zero stable allocation and held-path GI upload |
| Stable async plan-cache hit | <=0.05 ms | N/A | zero allocation and no graph walk |
| Reflection enabled-idle | <=0.02 ms | exactly 0 ms | no scan/sort/upload/descriptor/pass/query |
| Reflection active record | <=0.25 ms | <=0.50 ms target, <=1.00 ms ceiling | bounded face/mip/copy units |

Additional requirements:

- no ordinary-path device wait or synchronous GPU readback;
- no image/view/descriptor/pipeline/shader creation while advancing a normal
  ticket;
- no unbounded retirement, plan, ticket, or completion queue;
- reflection published + scratch + peak retired residency stays within its
  component budget;
- total tracked memory remains at or below 80% on the target 2 GiB profile;
- unchanged feature-off stationary repeats remain within existing comparer
  tolerances;
- global production gates remain renderer CPU <=6 ms, GPU frame <=10 ms, GI CPU
  <=0.25 ms, GI GPU <=2.5 ms, and tracked memory <=80%, unless the profile owner
  explicitly revises them.

## 16. Recommended PR sequence

### PR 1: Harness, diagnostics, and retirement

- Add deterministic scenarios and report identity fields.
- Add completion-based retirement service and tests.
- Capture current graphics/forced/idle baselines.

Gate: deterministic reports, graphics validation clean, no retirement wait or
unbounded storage.

### PR 2: DDGI exact release and ray capacity

- Publish exact cohort feedback.
- Wire visible publication and propagation release conditions.
- Reserve ray-equivalent cohort capacity.
- Finish tracking/static state and mismatch diagnostics.

Gate: every admitted moving cohort drains; frozen scenario converges; mismatch
counters are zero; CPU/upload budgets pass.

### PR 3: Async projected state and plan cache

- Add transactional state projection and separate compiled transfer halves.
- Add bounded variant cache and validation attribution.
- Keep every path uncertified.

Gate: unit/lifecycle rollback tests pass; normal Auto submits zero paths;
stable cache-hit budget passes.

### PR 4: Hi-Z certification

- Audit exact Hi-Z resources/transfers on supported topologies.
- Run validation, lifecycle, equivalence, and timing evidence.
- Promote Hi-Z only if correct and profitable.

Gate: zero validation output and checked-in evidence revision; otherwise remain
graphics-only.

### PR 5: Reflection scheduler and resource transaction

- Make scheduler authoritative.
- Add memory planning, one scratch slot, completion records, and retirement.
- Remove reflection ordinary-path `WaitIdle` and bypass publication API.
- Add conditional graph declarations with no executable rendering yet.

Gate: lifetime/state/memory/idle tests pass; fallback remains expected.

### PR 6: Capture-view rendering

- Add reusable view contract, cube orientations, capture shader flag, bounded
  visibility, and face capture pass.
- Capture opaque/alpha-masked linear HDR with direct lighting and valid shadows.

Gate: six coherent faces show correctly oriented local geometry with no local
reflection recursion and within face budget.

### PR 7: GGX filtering and completion publication

- Add shared-kernel local prefilter, destination copy, completion polling, and
  metadata publication.
- Add old-layer recapture and stale/deletion lifecycle tests.

Gate: both Sponza probes publish complete mip chains; zero validation errors;
record/GPU/memory budgets pass.

### PR 8: Dynamic recapture and remaining async paths

- Replace GI dirty edges with versioned recapture policy.
- Tune interval/age/budget behavior.
- Certify remaining async paths one atomic unit at a time.

Gate: moving sun remains coalesced and bounded; only profitable certified paths
enter Auto.

### PR 9: Production soak and defaults

- Run the full matrix, 10,000-frame capture, and two-hour combined soak.
- Promote defaults only for profiles that pass every gate.

Gate: all Section 17 criteria pass in one reproducible release bundle.

## 17. Final release criteria

### 17.1 Sun/sky/DDGI

- Visible sun, sky, direct light, and shadows remain frame-current.
- Requested/admitted GI snapshots, buffer contents, directional snapshot, and
  dirty signature are transactionally consistent.
- Every admitted cohort completes within its achievable declared budget.
- Moving input reports bounded tracking; frozen input reaches static
  convergence.
- Cache-reuse/publication mismatch counters are zero.
- Curtain/interior HDR has no black flash, spike, permanent stale sun, or
  double-counted sky.

### 17.2 Async

- Graphics-only and every Auto-enabled matrix cell emit zero Vulkan warnings
  and errors.
- No unsupported queue stage/access scope is recorded.
- Every handoff has matching ranges/layouts/families and semaphore edge.
- Resize/reload cannot submit or commit stale projected state.
- Output equivalence passes.
- Every Auto path is correctness-certified and meets the profitability gate.

### 17.3 Reflection

- Both authored Sponza probes progress queued -> faces -> mips -> copy ->
  completion -> published.
- All six faces and all mips are complete before metadata exposure.
- Captures contain useful local geometry, correct lighting/orientation, linear
  HDR, and no local-reflection recursion.
- Old local reflections remain visible during recapture.
- Missing/failed first captures use the global fallback.
- Stale/deleted completions cannot publish into recycled layers.
- Moving-sun requests remain coalesced and under maximum capture age.
- Resize/reload/deletion/retirement use no reflection-owned device wait.

### 17.4 Combined soak

- Two hours animated Sponza after warmup.
- At least 10,000 measured frames.
- DDGI, two local probes, and Auto with certified paths enabled.
- At least one resize, minimize/restore, shader reload, scene reload, probe
  delete/re-add, time freeze/resume, and recapture.
- Zero validation warning/error, device loss, timeout, queue growth, generation
  starvation, partial publication, memory violation, or budget hitch.

## 18. Primary implementation map

Atmosphere/DDGI:

- `Njulf.Rendering/Resources/GiAtmosphereAdmissionController.cs`
- `Njulf.Rendering/Resources/EnvironmentManager.cs`
- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- Simple-DDGI trace/transport/publish shaders

Async:

- `Njulf.Rendering/Pipeline/QueueStageCapabilities.cs`
- `Njulf.Rendering/Pipeline/AsyncComputeScheduler.cs`
- `Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs`
- `Njulf.Rendering/Pipeline/QueueOwnershipTransferRecorder.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResourceBindings.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- candidate pass implementations
- `Njulf.Rendering/VulkanRenderer.cs`

Reflection:

- `Njulf.Core/Scene/Scene.cs`
- `Njulf.Core/Scene/ReflectionProbe.cs`
- `Njulf.Rendering/Resources/ReflectionProbeCaptureScheduler.cs`
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
- proposed `Njulf.Rendering/Resources/GpuCompletionRetirementQueue.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbeCapturePass.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbePrefilterPass.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbePublishPass.cs`
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResource.cs`
- `Njulf.Rendering/Data/ReflectionProbeData.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- capture/prefilter shaders and existing material shader path

Evidence/tests:

- `NjulfHelloGame/SampleSmokeOptions*.cs`
- sample scenario/capture reporters
- `Njulf.Tests/GiAtmosphereAdmissionControllerTests.cs`
- `Njulf.Tests/QueueStageCapabilitiesTests.cs`
- `Njulf.Tests/ReflectionProbeCaptureSchedulerTests.cs`
- async/render-graph/shader/reflection data tests
- new Vulkan lifecycle and visual-oracle tests

## 19. Rollback and promotion policy

- Keep the development-only immediate GI admission switch for attributable A/B
  diagnosis; production defaults to coalesced admission.
- Every async path permanently retains its graphics route. Removing
  certification or profitability disables only that path.
- Reflection capture can be disabled independently while authored metadata and
  global environment fallback remain available.
- A profile that cannot afford one legal reflection unit keeps dynamic capture
  disabled or uses an explicitly downscaled quality tier.
- Do not delete legacy serialized fields until one release cycle has loaded and
  migrated them successfully.
- Do not mark a PR or release complete from unit/source-contract tests alone.
  Hardware validation, visual usefulness, completion-aware publication,
  performance, memory, and soak evidence are required.

## 20. Explicit non-shortcuts

- Do not fake reflection completion by clearing/copying the global environment
  into local layers and calling it scene capture.
- Do not render or filter directly into a currently sampled published layer.
- Do not publish on command recording.
- Do not replace missing completion plumbing with `WaitIdle`.
- Do not enable uncertified Auto timing probes.
- Do not mask unsupported stages/accesses to make a plan validate.
- Do not infer DDGI release from frame age or zero stale probes alone.
- Do not add full-probe scans, per-frame scene hashes, per-frame authored-probe
  sorts, shipping diagnostic atomics, synchronous readback, or unbounded
  retries/caches/retirement lists.
- Do not call the closure production-ready until the combined active production
  profile passes its inherited global gates.
