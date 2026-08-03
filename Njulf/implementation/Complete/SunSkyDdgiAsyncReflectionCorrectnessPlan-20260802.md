# Sun, Sky, DDGI, Async Compute, and Reflection Correctness Plan

Status: Revised and ready for phased implementation\
Original date: 2026-08-02\
Revised: 2026-08-03\
Primary scene: Sponza plaza and curtain views\
Scope: The three correctness failures found while validating the moved plaza sun\
Production code changed by this document: None

## 1. Required outcome

The renderer must support a moving procedural sun without allowing low-frequency
lighting systems to chase an impossible update rate, must never submit invalid
Vulkan synchronization to a compute-only queue, and must turn authored local
reflection probes into completed, filtered, safely published cubemaps.

The finished behavior is:

1. The visible sun, direct shadows, analytic sky, and atmosphere continue to
   update immediately every frame.
2. DDGI consumes a deliberately admitted, versioned atmosphere snapshot. New
   time-of-day candidates are coalesced while the current source cohort is being
   refreshed, so every admitted cohort completes instead of being invalidated by
   the next 0.25-degree step.
3. Async compute is graphics-safe by default and is enabled per path only after
   queue-stage, ownership, layout, validation, equivalence, and profitability
   gates pass.
4. Reflection capture has a real queue consumer, a multi-frame GPU state
   machine, GGX prefiltering, completion-aware publication, and bounded
   recapture policy tied to stable environment and DDGI versions.
5. The Sponza curtains receive the moved direct sun immediately, indirect light
   follows within its declared DDGI tracking budget, and local specular
   reflections eventually publish and then remain valid during recapture.
6. The fixes preserve the current stable-capacity DDGI fast path, add no
   unbounded per-frame work, and meet explicit CPU, GPU, upload, and memory
   budgets. A correctness-clean feature that misses its production budget stays
   disabled or on its graphics fallback until it is profitable.

Correctness and performance are equal exit gates. Validation-clean output is
not sufficient if the implementation adds a frame hitch, a device-wide wait, a
steady-state allocation, an unnecessary full-field scan, or enough resident
memory to consume the target GPU's required headroom.

This plan narrows and completes the relevant work in:

- [RemainingAsyncPathsImplementationPlan-20260713.md](Complete/RemainingAsyncPathsImplementationPlan-20260713.md)
- [RealtimeGiProductionClosurePlan-20260715.md](Complete/RealtimeGiProductionClosurePlan-20260715.md)
- [Phase10ReflectionSupportPlan.md](../Plans/Complete/Phase10ReflectionSupportPlan.md)

### 1.1 Non-goals and implementation constraints

- Do not replace the post-plan Simple-DDGI persistent scheduler, stable-capacity
  fast path, compact dispatch, convergence retirement, or incremental readback.
- Do not create a new global owner for all generations. Each subsystem owns its
  versions; `VulkanRenderer` assembles a read-only diagnostic snapshot.
- Do not duplicate the Simple-DDGI source cache or add a stable atlas unless the
  measured seam escalation gate in Section 5.5 requires it.
- Do not put reflection capture on async compute in the initial closure.
- Do not add detailed GPU atomics or full-probe readbacks to
  `ShippingPerformance`. Exact investigation counters belong in validation or
  `DetailedInvestigation` builds.
- Do not use `WaitIdle` for ordinary admission, plan rejection, capture
  completion, recapture, or resource retirement.
- Do not waive an existing production-performance gate. This plan may use
  locked A/B deltas to attribute its own cost, but the combined release remains
  subject to the active production budget profile.

## 2. Confirmed baseline

### 2.1 Current-tree revision delta

The original audits were captured before the DDGI performance pass completed.
The current tree now includes the work recorded in
[NsightDdgiPerformancePassImplementationStatus-20260802.md](NsightDdgiPerformancePassImplementationStatus-20260802.md):

- a generation-owned stable-capacity fast path with no steady-state resource
  planning or device wait;
- persistent probe queues, a wake heap, incremental readback, retained scratch
  storage, and ray-count-specific dispatch batches;
- exact incremental source/convergence telemetry, converged-probe retirement,
  compact transport/blend dispatch, and a source-throughput floor;
- production shader auditing that keeps detailed DDGI atomics out of shipping
  forward and Simple-DDGI modules;
- locked benchmark identities, repeat/A-B comparison, and post-timing linear-HDR
  comparison.

The latest [`final-v13-a` stationary capture](../.tmp/nsight-ddgi/final-v13-a.json)
and its repeat set show that this newer baseline is materially faster than the
audit binary in CPU scheduling, while still leaving
the three issues in this plan open. In the current report set:

| Metric | Current result | Interpretation for this plan |
|---|---:|---|
| Renderer CPU P95 | 3.667-3.787 ms | Preserve the existing <=6 ms pass. |
| GI GPU P95 | 1.329-1.394 ms | Preserve the existing <=2.5 ms pass. |
| Transport plus blend P95 | 0.792-0.868 ms | Preserve the existing <=2.25 ms pass. |
| GI CPU P95 | 2.002-2.026 ms | Existing <=0.25 ms production gate remains open; do not regress it. |
| GPU frame P95 | 26.253-26.509 ms | Existing 10 ms production gate remains open; do not hide it with feature-specific timing. |
| Tracked memory | 78.57% of 2 GiB | New residency must preserve at least 20% headroom. |
| Reflection lifecycle | 2 queued, 0 completed | The missing capture consumer remains current. |

The stationary captures intentionally disable animated time and async compute,
so they do not supersede the moving-sun or forced-async reproductions. Phase 0
must regenerate those reports from the implementation baseline used by the
first change. Until then, the old counts are historical reproduction evidence,
not current-binary acceptance numbers.

### 2.2 What is already correct

The following should be preserved, not replaced:

- `VulkanRenderer` updates `EnvironmentManager` before uploading the light
  snapshot.
- The visible procedural atmosphere and the stepped GI atmosphere are already
  separate data products.
- `EnvironmentManager.UpdateGiAtmosphereFrame` quantizes the GI sun direction
  and creates a stable signature.
- `VulkanRenderer.CreateSimpleDdgiDirtySignature` recognizes an
  atmosphere-only cohort transition.
- `SimpleDdgiVolumeManager.UpdateLightingDirtyState` advances source generation
  and invalidates source-cache metadata.
- Simple-DDGI trace misses evaluate the GI atmosphere, and hit shading uses the
  stepped directional-light snapshot with ray-query shadows.
- Forward shading uses environment radiance only for missing DDGI ownership; it
  does not add an unconditional second sky contribution.
- Global environment specular prefiltering already uses complete-mip builds,
  ping-pong textures, and an explicit crossfade.
- `SimpleDdgiVolumeManager` already tracks source generations, source-ready and
  stale populations, cohort progress, capacity shortfall, and solver
  convergence. Extend these structures instead of building parallel state.
- `ResolveSourceRefreshThroughputTarget` and the persistent source-refresh
  queues already reserve a probe-count throughput floor. The missing extension
  is ray-equivalent capacity and admission feedback.
- Async planning already has concrete resource bindings, resource-plan
  generations, stale-plan rejection, graphics fallback, timeline segmentation,
  and distinct release/acquire recording. Preserve these foundations.
- Reflection probes already have stable authored-ID-to-layer mapping, shader
  fallback flags, and a persistent published cubemap array.
- Graphics-only execution is a validation-clean reference path.

The first issue is therefore cadence and state admission, not a missing
sun/sky-to-DDGI shader connection.

### 2.3 Evidence for the three failures

| Issue | Reproduction evidence | Consequence |
|---|---|---|
| DDGI atmosphere starvation | The historical 420-frame [sun/sky audit](../.tmp/ddgi-sun-sky-audit.json) recorded 22 cohort transitions, 9,028 stale probes, source-step P95/max age of 419 frames (13.165 seconds), 3,012 source-ready probes, zero converged probes, and 370 frames of global-convergence pending state. Current code still admits every quantized candidate immediately, while the current stationary report still resolves an eight-second source-refresh target. | A new GI source generation can arrive before the prior source sweep drains, so bounced lighting cannot enter bounded tracking. Exact current counts require the Phase 0 rerun. |
| Invalid async synchronization | The historical five-frame [forced-async audit](../.tmp/ddgi-async-validation-audit.json) recorded 30 Vulkan validation errors. The equivalent [graphics-only audit](../.tmp/ddgi-graphics-validation-audit.json) recorded zero. The long Auto audit recorded 20 errors. Current Hi-Z recording still contains a pass-local scene-depth barrier with graphics-only stages, and Auto can still select an uncertified timing probe. | Compute-only command buffers can contain graphics-only stage masks, and pass-local layout mutation can disagree with graph state. Auto timing probes are not safe. |
| Reflection capture not executed | Both the historical long audit and the post-plan `final-v13` stationary capture report two probes queued and zero completed. `TryBeginCapture`, `PublishCapture`, and `CancelCapture` still have no production caller. | Authored probes keep using the global environment fallback; the queue and diagnostics imply work that never happens. |

The current Sponza settings make the first failure deterministic:

- `TimeScale = 60` advances one simulated minute per real second.
- The sun moves approximately 0.25 degrees per real second at that scale.
- `GiSunStepDegrees = 0.25` therefore requests a new DDGI atmosphere candidate
  roughly once per second.
- `GiTargetSourceSweepSeconds = 8` budgets roughly eight seconds to refresh the
  field.

Retuning the scene alone would hide the mismatch. The engine must enforce the
cadence contract even when content authors select an aggressive time scale.

## 3. Global invariants

Every implementation phase must retain these invariants.

### 3.1 Version ownership

One physical time-of-day state may have several deliberately lagged consumers.
Their versions must be explicit rather than inferred from frame number.

| Product | Update policy | May lag visible sun? | Publication rule |
|---|---|---:|---|
| Visual atmosphere and direct sun | Every frame | No | Immediately uploaded for the current frame. |
| Global specular prefilter | Latest-wins complete build plus crossfade, versioned independently from DDGI admission | Yes | A full mip chain must finish before becoming the next sampled texture. Holding a DDGI cohort must not hold the next specular candidate. |
| DDGI atmosphere source | Latest-wins admitted snapshots | Yes | An admitted signature stays immutable until its source cohort reaches the release condition. |
| Local reflection probe | Budgeted capture of a composite scene/environment version | Yes | All six faces and every mip must be GPU-complete before metadata exposes the new capture. |

The renderer's read-only version snapshot should expose at least:

- `VisualEnvironmentGeneration`
- `RequestedSpecularEnvironmentGeneration`
- `PublishedSpecularEnvironmentGeneration`
- `RequestedGiEnvironmentGeneration`
- `AdmittedGiEnvironmentGeneration`
- `CompletedGiSourceCohortGeneration`
- `StaticGiConvergedGeneration`
- `SceneRadianceRevision`
- per-probe requested and published `ReflectionCaptureVersion`

These are monotonic identifiers. Wraparound uses the same nonzero wrap policy as
the current DDGI generations. Equality is meaningful; ordering across wrap is
not required. The snapshot aggregates versions for diagnostics and capture
requests; it does not move mutation authority out of the owning subsystem.

### 3.2 No mixed authority

- The environment controller owns requested versus admitted atmosphere state.
- The global specular prefilter owns its requested/building/published snapshot
  independently. It may copy the current visual atmosphere when a build starts,
  but it must not key new work from `AdmittedGiEnvironmentGeneration`.
- `SimpleDdgiVolumeManager` owns source-cohort progress and reports feedback; it
  must not silently select a different environment buffer than the admitted
  signature describes.
- The render graph owns cross-pass image layouts and queue-family ownership.
  Pass-local code owns only barriers wholly internal to one pass.
- `ReflectionProbeManager` owns logical capture state. The render passes own GPU
  recording. Fence/timeline completion, not command recording, authorizes
  publication.

### 3.3 Safe fallback

- A stale but complete DDGI probe remains readable until its replacement is
  published.
- A stale but complete reflection probe remains readable during recapture.
- A missing local reflection capture uses the current global prefiltered
  environment.
- A rejected async plan executes the identical pass list on graphics in the
  current frame.
- A failure must never expose cleared, half-filtered, or wrong-layout storage.

### 3.4 State vocabulary

`Converged` must remain reserved for a stationary field that satisfied the
existing solver criterion. A continuously moving sun should report a bounded
`Tracking` state, not an indefinitely failed convergence state.

Recommended DDGI public states are:

- `Bootstrapping`
- `TrackingSourceCohort`
- `TrackingPropagation`
- `TrackingBounded`
- `StaticConverging`
- `StaticConverged`
- `CapacityLimited`

### 3.5 Stable-path and performance invariants

- A frame with no new atmosphere candidate, no async plan-generation change,
  and no reflection work performs no managed allocation, no full-probe scan, no
  descriptor rewrite, no new GPU readback, and no device-wide wait for these
  features.
- DDGI admission is O(1) in probe count. It consumes the previous completed
  incremental cohort summary; it never calculates admission by scanning probe
  arrays on the render thread.
- Async queue/stage/resource validation runs when queue capabilities, settings,
  pass declarations, or concrete binding generation changes. An accepted plan
  is cached; stable frames validate its generation with constant work.
- Reflection scheduling work is proportional to changed tickets and the bounded
  work selected for the frame. Initial scene synchronization may inspect active
  probes once; steady-state scheduling must not rebuild or sort the full probe
  set every frame.
- Conditional graph passes with no selected work emit no capture commands,
  ownership transfers, or timestamp queries.
- Production diagnostics reuse already-owned CPU state. Exact GPU mismatch and
  attribution counters are compiled into validation or
  `DetailedInvestigation`, not shipping shaders.

Initial incremental budgets, measured against a locked same-binary feature-off
variant, are:

| Work | CPU P95 delta | GPU/frame delta | Additional constraints |
|---|---:|---:|---|
| GI admission/version snapshot | <=0.05 ms | No new pass | Zero steady-state allocation; metadata residency <=64 KiB. |
| Stable async-plan cache hit | <=0.05 ms | N/A | Full validation only on plan-generation changes. |
| Reflection enabled, no queued/in-flight work | <=0.02 ms | 0 ms | No empty capture/prefilter/copy command recording. |
| Reflection capture work | <=0.25 ms record time | 0.50 ms scheduling target; 1.00 ms profile ceiling | Default one scratch slot and one face or bounded mip group per frame. |

The numbers above are initial hard engineering targets, not permission to relax
the active production profile. If a single reflection face cannot meet the
profile ceiling, reduce resolution/work quality through the memory/quality plan
or keep dynamic capture disabled on that profile. Async work has its separate
profitability gate in Section 6.7.

## 4. Phase 0: freeze evidence and install safety rails

This phase must land before any behavior change. It prevents the fixes from
being judged against moving inputs and keeps invalid async work out of normal
runs.

### 4.1 Deterministic Sponza scenarios

Add three scripted scenarios to the existing sample smoke/capture harness:

1. `GiSponzaAnimatedAtmosphere`
   - Fixed camera at the curtain view.
   - Fixed `DeltaTime` runs at 30, 60, and 120 Hz.
   - `TimeScale = 60`, `GiSunStepDegrees = 0.25`, and
     `GiTargetSourceSweepSeconds = 8`.
   - The fast contract tier runs at least three configured source-sweep
     intervals at each rate. The PR integration tier runs five simulated
     minutes at 60 Hz. The nightly tier runs 30 minutes.
2. `GiSponzaFreezeAfterAtmosphereStep`
   - Run the animated sun through several candidates.
   - Freeze time immediately after a candidate is requested.
   - Verify latest-state admission and eventual static convergence.
3. `GiSponzaReflectionProbeLifecycle`
   - Two authored probes.
   - Initial load, capture, sun change, recapture, resize, shader reload, and
      scene reload.

Retain `GiSponzaRightWallStationary` as the performance control. For each
feature change, capture a same-binary feature-off/feature-on pair after the
timing window has settled. The moving-sun baseline must also retain a
development-only `LegacyImmediateGiAdmission` variant until PR 3 is accepted so
the new controller's work reduction and visual lag are attributable.

Every report records fixed timestep, camera/view hash, scene revision, shader
bundle hash, GPU/driver identity, queue-family flags, settings fingerprint, and
the validation configuration. It also records executable/dirty-worktree
identity, build flavor, active feature-isolation variant, source cohort state,
and whether detailed diagnostics are compiled. Reuse the existing benchmark
identity and pair-comparison contracts rather than inventing another report
format.

### 4.2 Async quarantine

Until path certification in Section 6:

- `AsyncComputeMode.Auto` must not launch an uncertified path, including a timing
  probe.
- Add `Uncertified` and `QuarantinedAfterValidationError` path statuses.
- `ForceEnabledForValidation` may run an uncertified path only through an
  explicit validation harness invocation with a selected atomic path such as
  `--async-compute-path HiZBuild`. Merely loading a settings file with Force mode
  is not sufficient authority. It still performs all static plan validation.
- The default user-facing result may remain `Auto`, but with zero active paths
  until they are certified. If that distinction cannot be made without a large
  refactor, temporarily default to `Disabled`.
- A validation error attributed to an active async segment quarantines every
  path in that segment for the rest of the process and forces subsequent frames
  to graphics.

This is a safety mechanism, not the synchronization fix.

### 4.3 Required observability before behavior changes

Add counters and snapshot fields for:

- requested, coalesced, admitted, source-completed, and statically converged GI
  environment generations;
- current admitted snapshot age in frames and seconds;
- cohort starts, completions, cancellations, and hard restarts;
- first visible-probe response, P50/P95/full source sweep, and first propagation
  completion;
- async path certification and session quarantine reason;
- validation messages attributed to submission segment and active path set;
- reflection queued/in-flight/completion-wait/published/failed counts and
  cumulative totals.

Derive scalar counts from the existing incremental scheduler and ticket state.
Do not add a per-frame full-probe reduction, synchronous query readback, or
shipping shader atomic for observability. Exact GPU generation-mismatch and
per-path attribution counters are validation-build evidence; shipping reports
retain coarse fail-closed state and timing.

Before behavior changes, capture these current-binary controls:

- stationary graphics-only ProductionTiming repeat pair;
- animated graphics-only admission baseline;
- forced Hi-Z-only validation reproduction, followed by the currently failing
  candidate combination only if the single-path harness is not yet available;
- reflection lifecycle baseline showing queue state and zero consumer work;
- tracked-memory and upload baseline with reflection scratch absent.

### 4.4 Phase 0 exit criteria

- The three deterministic scenarios produce schema-valid reports.
- The historical failure reports have current-binary replacements with locked
  identity. Historical counts remain linked only as provenance.
- Graphics-only Sponza remains at zero validation warnings and errors.
- Normal `Auto` does not record an uncertified compute segment.
- The reports can distinguish requested from admitted atmosphere changes and
  queued from actually submitted reflection capture work.
- Stationary graphics-only timing passes the existing repeat-comparison rules;
  Phase 0 instrumentation adds no production DDGI shader atomics, no new device
  wait, and no tracked-memory category without an owner.

## 5. Issue 1: bounded procedural-sky tracking in DDGI

### 5.1 Failure mechanism to remove

`EnvironmentManager.UpdateGiAtmosphereFrame` currently turns each quantized sun
step directly into the GI frame and signature. `CreateSimpleDdgiDirtySignature`
then advances the source generation, and `UpdateLightingDirtyState` marks the
entire source cache stale. At the Sponza cadence, the next generation arrives in
about one second while the declared refresh budget is eight seconds.

The post-plan scheduler improvements do not change that admission boundary.
They already provide persistent source-refresh queues, an incremental stale
population, a throughput target, source generations, and convergence retirement.
Those are the feedback producer for this fix. Do not introduce a second cohort
tracker or scan `_probeSourceLightingGenerations` from `VulkanRenderer`.

The repair is to separate candidate creation from admission. It must not slow
the visible atmosphere or direct lighting.

### 5.2 Add a pure admission controller

Create a GPU-independent `GiAtmosphereAdmissionController` with deterministic
inputs and outputs so it can be exhaustively unit tested.

Implement the controller as allocation-free value state. Candidate coefficients
live in preallocated environment-owned snapshots; controller decisions carry
signatures/generations and indices or immutable value records, not cloned arrays
or per-frame collections. One call is made per rendered frame using the latest
candidate and the previous completed DDGI cohort summary. It must not wait for a
readback or call into Vulkan.

Inputs:

- latest quantized candidate frame and signature;
- current admitted signature and generation;
- DDGI enabled/backend/transport mode;
- source cohort active/completed state;
- current-generation participating and stale probe counts;
- whether the field completed one propagation boundary after source readiness;
- measured frames per second and source-refresh capacity;
- hard invalidation flag and reason;
- pause/freeze/editor-scrub state.

Outputs:

- `Hold`
- `ReplacePendingCandidate`
- `AdmitPendingCandidate`
- `HardRestartWithCandidate`
- a machine-readable reason and predicted lag/capacity values.

The controller owns one admitted snapshot and at most one pending snapshot. A
new candidate replaces the pending snapshot; intermediate candidates are
counted as coalesced, not queued.

Candidate replacement and `Hold` are O(1), perform zero managed allocation, and
do not rewrite the GI buffer, DDGI signature, source generation, or specular
prefilter state.

The normal state flow is:

```text
visual candidate
      |
      v
latest pending --(DDGI release condition)--> admitted snapshot
                                              |
                                              v
                                    source cohort refresh
                                              |
                                              v
                                    bounded propagation
                                              |
                           pending? -----------+----------- no pending
                              |                              |
                              v                              v
                         admit latest              continue static solve
```

### 5.3 Admission and release rules

Use these rules in priority order:

1. On bootstrap, admit the first valid candidate immediately.
2. If DDGI is disabled, no participating probes exist, or the active backend
   does not consume the stepped atmosphere, admit the latest candidate without
   waiting for a DDGI cohort.
3. While the admitted source cohort contains stale participating probes, hold
   the admitted snapshot and replace only the pending candidate.
4. Do not advance the environment buffer, light signature, or DDGI source
   generation for a held candidate.
5. Admit the latest pending candidate only after:
   - the current source cohort has zero stale participating probes; and
   - all current-generation visible-priority probes have published at least one
     valid result; and
   - any configured minimum publication transition has completed.
6. Do not require full static solver convergence to admit a moving sun. That
   would trade source starvation for time-of-day starvation.
7. If time stops and no pending candidate remains, transition to
   `StaticConverging`; preserve the admitted signature until the existing
   convergence criterion completes.
8. A source-contract change that makes old cache entries semantically unusable
   may perform a hard restart. Examples are transport ABI/calibration changes,
   backend changes, or an environment model enable/disable transition.
9. Ordinary time-of-day motion, radiance drift, and continuous editor scrubbing
   are coalescible. Editor scrubbing should debounce until release when possible.
10. Geometry, emissive, and authored non-atmosphere light invalidations retain
   their existing regional/global policies. They do not masquerade as
   atmosphere cohort transitions.

### 5.4 Wire candidate and admitted frames correctly

Refactor `EnvironmentManager` to hold distinct records:

- `_visualAtmosphereFrame`
- `_requestedSpecularAtmosphereFrame` or an equivalent independently versioned
  build candidate
- `_requestedGiAtmosphereFrame`
- `_admittedGiAtmosphereFrame`
- requested/published specular signatures and generations
- requested/admitted GI signatures and generations

`Update` continues computing visual and requested state every frame. A new
admission call, made by `VulkanRenderer` using the previous completed DDGI
feedback, is the only operation that changes the admitted GI frame.

Compute the visual atmosphere once per frame as today. Compute a requested GI
coefficient snapshot only when its quantized signature changes, and copy the
current visual coefficients into a specular build snapshot only when a build
actually starts. Do not evaluate the sky model independently for three consumers
on every frame.

The admitted record must drive all of the following together:

- `EnvironmentGiDataBuffer`
- GI atmosphere signature used by `CreateSimpleDdgiDirtySignature`
- the atmosphere-owned directional-light snapshot used by DDGI trace/hit
  shading
- transport fallback radiance for exceptional GI paths

No frame may upload a new GI buffer while retaining the old DDGI signature, or
advance the signature while retaining the old GI buffer.

The visible environment buffer, visible directional light, shadow maps, and
sky rendering continue to use the current visual frame.

Global specular prefiltering is deliberately separate. A new prefilter build
captures the latest visual/specular candidate when its scratch target becomes
available and retains that immutable snapshot until every mip finishes. It must
not use the admitted GI signature as its requested-work key. This preserves the
existing latest-wins ping-pong/crossfade behavior even while DDGI holds an
eight-second cohort.

Upload `_giEnvironmentBuffer` only when the admitted snapshot changes or the
buffer/bindless resource generation is recreated. A held candidate must not
produce identical staging traffic every frame. If a frames-in-flight hazard
requires a per-slot buffer instead, make that ownership explicit and measure the
upload; do not retain an unconditional upload by accident.

The admission call consumes completed feedback from an earlier frame and never
blocks for current GPU work. The admitted CPU record, GI buffer upload, dirty
signature, and atmosphere-owned DDGI light substitution become one renderer
transaction for the frame in which admission occurs.

### 5.5 Make source generation safety explicit

Keep the current canonical irradiance atlas, private V2 transport target, and
source cache. Do not duplicate the 70,815,744-byte source cache merely to solve
cadence. The audited canonical and private irradiance atlases are only
7,868,416 bytes each, but a third atlas is still not required unless visual
generation seams remain after the admission fix.

Enforce these contracts in CPU state, push constants, and shaders:

- A source-cache entry is current only after all rays for that probe were
  written for the admitted source generation.
- A solver dispatch may reuse a cache entry only when its source generation
  equals the admitted generation.
- A probe with stale source retains its previously published irradiance; it is
  not cleared to black and is not relabeled current.
- `SimpleDdgiPublishPass` may publish a result only when the probe's source and
  transport generation match the transaction in the update queue.
- A stale readback from another frame or volume-table generation cannot advance
  source-cohort completion.
- Relocation, scrolling, and slot reuse clear the relevant generation markers
  before the physical slot can be sampled as current.

Fail closed in every build when cache reuse or publication generations do not
match. Add exact GPU counters for rejected reuse/publication to validation and
`DetailedInvestigation` builds, plus CPU-side transaction assertions in all
builds. Do not reintroduce production diagnostic atomics. The acceptance value
in validation evidence is zero; a nonzero value is a correctness bug, not
normal pressure.

If the curtain and checkerboard transition captures still show unacceptable
generation seams after these rules, add one `SimpleDdgiStableIrradianceAtlas`
snapshot and per-probe transition alpha. That escalation costs about 7.5 MiB at
the audited layout and must be admitted by `SimpleDdgiMemoryPlan`; it must not be
implemented by silently exceeding the 192 MiB component budget.

### 5.6 Reserve real source-sweep capacity

`ResolveSourceRefreshThroughputTarget` already derives the required probes per
frame. Extend its contract:

- Calculate required probe and ray work, not just probe count. Maintenance tiers
  with fewer rays must not be counted as a complete source refresh unless they
  satisfy the source-cache ABI.
- Extend the existing persistent source-refresh queues and incremental
  histograms; do not rebuild a cohort list or scan all probes each frame.
- Reserve the current admitted cohort before background maintenance.
- Keep visible repair ahead of nonvisible throughput work, but guarantee the
  remaining cohort its declared minimum share each frame.
- Report capacity shortfall before dispatch.
- If the hard update/ray budget cannot meet the requested sweep time, retain the
  current admitted generation while its cohort drains, enter `CapacityLimited`,
  and expose the minimum achievable sweep time. Once that cohort completes,
  admit the latest pending candidate; capacity pressure may lengthen the
  declared lag but must not permanently freeze time-of-day. Never create
  unbounded generations to imitate responsiveness.
- Deterministic benchmark scenarios derive targets from their nominal fixed
  rate. Interactive rendering uses a bounded, slowly varying observed-rate
  estimate so a hitch cannot suddenly multiply work. Test 30, 60, 120, variable
  frame rate, and long-hitch sequences.

### 5.7 Cadence diagnostics and authoring guidance

Expose both requested and effective cadence:

- requested quantization step;
- apparent sun angular speed in degrees per real second;
- predicted candidate interval;
- configured and minimum achievable source-sweep seconds;
- latest pending angular delta from the admitted sun;
- admitted snapshot age;
- coalescing ratio;
- first visible response and full-cohort completion time.

For authoring warnings, estimate:

```text
minimum non-coalesced step degrees
    = apparent sun degrees per real second
    * achievable source sweep seconds
    * safety factor
```

At the current Sponza values the unsmoothed minimum is approximately two
degrees, or the time scale would need to fall to approximately 7.5x for a
0.25-degree step. This is diagnostic guidance only. `AutoCoalesced` admission
remains the production solution and keeps the precise latest candidate.

### 5.8 DDGI tests

Add unit tests covering:

- candidate equality and replacement;
- latest-wins coalescing;
- no admission while stale count is nonzero;
- admission immediately after the release condition;
- hard restart classification;
- generation wraparound;
- 30/60/120 Hz and variable `DeltaTime`;
- dawn/noon/sunset and azimuth wrap boundaries;
- rapid parameter scrubbing followed by release;
- time freeze with a pending candidate;
- cache reuse and publish generation matching;
- scrolling or slot reuse during a cohort;
- source-capacity shortfall and recovery;
- zero allocations over a long steady-state controller loop;
- held candidates causing no GI upload or dirty-signature change;
- specular requested/published generations advancing independently while the GI
  generation is held.

Extend the deterministic integration scenario with these assertions:

- every admitted generation reaches zero stale participating probes;
- `cohort starts - cohort completions` is never greater than one;
- no ordinary atmosphere candidate causes a hard restart;
- source cohort completion is within configured target plus 5% and two frames
  when capacity telemetry says the target is achievable;
- first visible curtain-region response is within two rendered frames after an
  admitted change;
- admitted snapshot age remains bounded by one achieved cohort interval plus
  the publication-transition allowance;
- generation-mismatch counters remain zero;
- freezing time leads to `StaticConverged` within the existing static budget;
- direct sun/shadow motion remains frame-current throughout the run.

Visual tests use locked-exposure linear HDR crops of both curtains and a diffuse
interior patch. The accepted sequence must change smoothly without black
flashes, energy spikes, or a permanent old-sun imprint.

Performance acceptance for this issue:

- admission plus version-snapshot CPU P95 delta is <=0.05 ms;
- the steady held path allocates zero bytes and adds zero GI upload bytes;
- no new GPU pass, device wait, full-probe readback, or per-frame full-field scan
  is introduced;
- the moving-sun candidate materially reduces source traces and invalidations
  relative to legacy immediate admission; additional useful propagation is
  allowed, but GI GPU P95 remains within the active budget and no-op dispatch
  lanes do not increase;
- the stationary graphics-only repeat remains within the existing benchmark
  comparer's tolerance for every material pass at or above 0.50 ms.

### 5.9 Issue 1 implementation map

Primary files:

- `Njulf.Rendering/Resources/EnvironmentManager.cs`
- proposed `Njulf.Rendering/Resources/GiAtmosphereAdmissionController.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs`
- `Njulf.Shaders/ddgi_simple_trace.comp`
- `Njulf.Shaders/ddgi_simple_transport.comp`
- `Njulf.Shaders/ddgi_simple_publish.comp`
- `Njulf.Rendering/Data/RenderSettings.cs`
- diagnostics/snapshot contracts and the Sponza reporter

Primary tests:

- proposed `GiAtmosphereAdmissionControllerTests.cs`
- `ProceduralSkyModelTests.cs`
- `SimpleDdgiBounceConvergenceTests.cs`
- `SimpleDdgiVolumeManagerTests.cs`
- `SampleGlobalIlluminationValidationSettingsTests.cs`

## 6. Issue 2: correct Vulkan async-compute synchronization

### 6.1 Failure mechanism to remove

The forced plan ran Hi-Z, AO blur, far-field bake, and bloom asynchronously. The
validation layer reported graphics-only stages such as `FragmentShader` in
barriers recorded on a compute-only queue. `HiZBuildPass` also maintains local
layout state and constructs a scene-depth barrier whose destination includes
fragment and early-fragment-test stages even though its work is compute.

This exposes two architectural problems:

1. The compiled submission plan does not prove that every emitted stage mask is
   supported by the command buffer's queue family.
2. The render graph and individual passes can both believe they own the same
   cross-pass image layout transition.

Changing one mask to `AllCommands` is not a sufficient fix. The planner must
make invalid synchronization unrepresentable, and each concrete image must have
one layout authority.

Preserve the current async foundation: `AsyncComputeScheduler`, concrete
resource bindings and their generations, stale-plan rejection,
`AsyncComputeRecoverablePlanRetryGate`, timeline segments, isolated Auto timing,
graphics fallback, and `QueueOwnershipTransferRecorder`. The missing work is
queue-stage proof, transactional layout authority, certification/quarantine,
and removal of cross-pass barriers still recorded by individual passes.

### 6.2 Add queue-stage capability validation

Create a pure `QueueStageCapabilities` helper from the actual Vulkan queue flags
and use it at three boundaries:

1. Validate every async pass usage against the queue on which the pass will be
   recorded.
2. Validate each source and destination scope of every compiled transfer against
   its recording queue.
3. Validate the final emitted release/acquire barrier records before command
   recording.

The helper must understand synchronization2 stage aliases and extension stages.
It may accept `AllCommands` because Vulkan defines it as all operations supported
by the queue on which it is used, but exact stages remain preferred. It must
never repair a bad declaration by silently masking unsupported bits; that would
discard the dependency the declaration claimed to need. Reject the async plan
and run graphics instead.

Validate access masks against both the selected stages and the concrete resource
usage. Queue support alone is insufficient: a stage-supported barrier with an
incompatible access bit is still invalid. Treat `None`/ignored scopes explicitly
for release and acquire halves instead of passing them through the ordinary
stage-access compatibility table.

Add structured errors containing pass, resource, allocation identity, queue
family/flags, stage/access mask, layout, segment, and release/acquire side.

Build and cache capabilities from the actual queue-family flags. Run full stage
validation when the compiled submission-plan generation changes, not by walking
every pass/resource on every stable frame. The final recorder consumes already
validated barrier records; validation-build assertions may recheck individual
records without allocating diagnostic strings on success.

Replace per-frame topology construction with retained enum-indexed arrays,
bitsets, and compiled segment templates. The cache key includes queue/settings,
declaration and concrete-resource generations, selected atomic paths, and the
executable pass mask. A changed zero-work/pass mask may select or compile a
bounded cached variant, but it must not reuse transfers for a skipped producer.
Give this variant cache a fixed, profile-declared capacity and deterministic
eviction; expose hits, misses, rebuild cost, and fallback reason. It must never
grow with frame history or compile an unbounded set of incidental masks. Stable
eligibility/timing updates mutate retained scalar state and allocate nothing.

### 6.3 Audit and complete the existing paired transfer operations

`QueueOwnershipTransferRecorder` already emits distinct release and acquire
halves with ignored opposite scopes, and the scheduler already creates timeline
edges. Keep that implementation and make its compiled record the only input to
recording; do not build a parallel transfer system.

Represent the two recorded halves of a transfer separately in the compiled
plan:

- source release: exact source stage/access; ignored destination scope;
- destination acquire: ignored source scope; exact destination stage/access;
- matching queue-family indices and image subresource range;
- one declared old/new layout transition;
- a timeline semaphore signal/wait edge between different queues;
- wait stage equal to the first real destination consumer.

For different queues in the same family, ownership indices remain ignored but
the semaphore edge and any one-time layout transition are still explicit. Do
not emit a pretend ownership transfer.

`QueueOwnershipTransferRecorder` should consume these prevalidated barrier
records. It should not reinterpret broad logical usages while recording.

Add an immutable transfer identity and validate that release/acquire records
match resource generation, allocation identity, byte/subresource range,
families, layouts, and semaphore edge. Coalesce only adjacent ranges with
identical scopes; never widen a range merely to reduce barrier count.

### 6.4 Make render-graph layout state authoritative

Replace independent cross-pass layout mutation with a transaction:

1. Resource bindings expose the current committed layout and owner.
2. Plan compilation works on a private projected state.
3. Pass opening/closing barriers and transfers are generated from that projected
   state.
4. The state is committed only after the plan is accepted and the matching
   submission is recorded.
5. Fallback, stale-plan rejection, resize, or pass cancellation discards the
   projected state without mutating the committed tracker.

Pass-local layout fields may remain as debug mirrors, but they must be updated
only through the binding's `LayoutTracker` callback after commit. A mismatch
between the mirror and committed graph state is a validation failure.

Internal barriers are limited to subresources used entirely inside one pass,
such as Hi-Z mip N write to mip N+1 read.

Compile projected state into the cached submission plan. Commit owner/layout
state only after all matching command buffers have been recorded successfully;
submission failure follows the existing terminal fault path. Stable frames do
only the plan-generation check. Resource recreation creates a new generation
with explicit initial layout/owner and cannot inherit projected state from the
retired generation.

### 6.5 Repair Hi-Z first

Hi-Z is the first certification target because it reproduces both categories of
failure and has a small pass surface.

Required changes:

- Declare scene depth as a compute-sampled read in
  `ProductionRenderPipelineDeclaration` with final input layout
  `DepthStencilReadOnlyOptimal`.
- The graphics producer releases scene depth after depth attachment writes.
- The compute segment acquires it with destination `ComputeShader` and sampled
  read access.
- Remove the cross-pass scene-depth transition from
  `HiZBuildPass.TransitionDepthAndPyramidToGeneral`; the graph has already
  established the pass entry state.
- Make graph entry establish the entire pyramid in `General` for storage writes.
- Keep only per-mip compute-write to compute-read dependencies inside
  `HiZBuildPass`.
- Make graph exit transition the full written mip range to
  `ShaderReadOnlyOptimal`, release it to graphics when required, and acquire it
  before the first visibility consumer.
- Never use fragment, early-fragment-test, or color-output stages in a barrier
  recorded on the compute-only command buffer.
- Initial allocation may start at `Undefined`; subsequent frames must start from
  the committed prior layout. Resize/recreation resets generation and layout
  together.
- Subresource ranges must cover the exact aspect, mip count, and array layers;
  per-mip dependencies must not accidentally transition untouched mips.

The repaired pass must not add a replacement opening/closing barrier locally.
Its command recording owns only dispatches and the per-mip compute dependency;
entry and exit state come from the accepted graph plan.

### 6.6 Audit and certify every path independently

Add a harness selector such as `--async-compute-path <name>` that forces exactly
one scheduling unit while all other candidates stay on graphics. Certify in this
order:

1. `HiZBuild`
2. `AmbientOcclusionBlur`
3. `Bloom`
4. `FarFieldClipmapBake`
5. `Fog`
6. `GpuParticles`
7. atomic `SimpleDdgiUpdate`
8. atomic `FullDdgiUpdate`
9. atomic `SsgiChain`

For each path:

- list every concrete resource, byte/subresource range, producer, first
  consumer, initial owner/layout, release, acquire, and final owner/layout;
- remove pass-local barriers that duplicate graph entry or exit;
- keep the graphics execution order unchanged as the fallback;
- run the path alone with standard validation, synchronization validation, and
  GPU-assisted validation where supported;
- run same-family separate queues and distinct queue families when hardware or
  the test device exposes both;
- compare the linear HDR result and relevant buffers against graphics-only;
- record timing only after all correctness gates pass.

After individual certification, test combinations in dependency order, then
all certified paths together.

### 6.7 Auto policy and certification

Extend `AsyncComputePassCatalog` or a companion catalog with a static
certification state and evidence revision.

Every path starts `Uncertified`. Promoting it to `CertifiedCorrectness` is a
reviewed source change that names an evidence revision, atomic path membership,
validated queue topologies, lifecycle report IDs, and output-equivalence result.
Correctness certification is hardware/topology scoped where required and is
separate from the runtime timing decision. Changing a pass's declared usages,
barriers, relevant shader bundle, scheduler recording, or atomic path membership
invalidates its evidence revision.

`Auto` may activate a path only when all are true:

- every pass in the atomic path is statically certified;
- queue capabilities satisfy every stage and operation;
- every concrete resource binding is current and permitted on both families;
- the projected ownership/layout plan validates;
- no active path in the segment is session-quarantined;
- warmup is complete and isolated timing shows a stable benefit;
- the first-consumer wait does not erase the expected overlap.

Any failure selects graphics before recording and emits one structured fallback
reason. Validation-error frames are excluded from timing statistics and can
never promote a path.

Certification is not profitability. A validation-clean path with no measured
benefit remains on graphics in `Auto`.

Auto may collect timing only for `CertifiedCorrectness` paths. Use at least
three locked graphics/async pairs after warmup. The initial profitability gate
remains at least 3% median GPU-frame improvement, no GPU-frame P95 regression,
no material-pass P95 regression above the existing comparer tolerance, and no
meaningful CPU submit/record regression. A path that later regresses may be
demoted without losing correctness certification.

### 6.8 Async tests and acceptance gates

Unit tests:

- supported stage sets for graphics, compute-only, transfer-only, and combined
  families;
- rejection of fragment/attachment stages on compute-only queues;
- release/acquire ignored-side masks;
- same-family separate-queue versus distinct-family transfers;
- semaphore wait at first consumer stage;
- image subresource and buffer-range coalescing boundaries;
- initial `Undefined` and steady-state layout plans;
- projected layout rollback after rejected/stale plans;
- stage/access compatibility, including the ignored side of ownership transfer;
- bounded compiled-plan variant eviction and rebuild after an executable-mask
  change;
- path quarantine and Auto timing exclusion.

Integration matrix for every path alone and all paths together:

- first frame, warm frame, and 10,000-frame run;
- resize, minimize/restore, swapchain recreation, shader reload, scene reload;
- GI backend and quality-tier changes;
- zero-work and feature-disabled frames;
- frames in flight wrapping repeatedly;
- standard, synchronization, and GPU-assisted validation.

Acceptance:

- zero Vulkan validation warnings and errors;
- planned release/acquire counts equal emitted counts;
- committed layout/owner matches the first use of the next frame;
- no stale-plan resource is submitted;
- graphics and async outputs pass the existing accuracy oracle and new linear
  HDR comparison;
- a two-hour Sponza soak has no device loss, timeout, flicker, or ownership
  drift;
- a path is production-enabled only if median GPU frame time improves by a
  predeclared threshold and P95 does not regress. Use 3% as the initial threshold
  unless a profile-specific budget supersedes it.
- stable accepted-plan CPU P95 delta is <=0.05 ms and performs zero managed
  allocation;
- a plan-generation rebuild records its compilation/validation cost separately
  and does not cause a device-wide wait;
- an inactive or unprofitable async path emits no compute submission, ownership
  transfer, or timing query;
- validation/certification telemetry uses compact reason IDs on the hot path and
  formats detailed text only when exporting a snapshot or reporting failure.

### 6.9 Issue 2 implementation map

Primary files:

- `Njulf.Rendering/Pipeline/AsyncComputeScheduler.cs`
- `Njulf.Rendering/Pipeline/QueueOwnershipTransferRecorder.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResourceBindings.cs`
- `Njulf.Rendering/Pipeline/RenderGraph.cs`
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf.Rendering/Pipeline/HiZBuildPass.cs`
- the remaining candidate pass implementations
- `Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Data/AsyncComputePolicy.cs`

Primary tests:

- `AsyncComputePhase3Tests.cs`
- proposed `QueueStageCapabilitiesTests.cs`
- `RenderGraphResourceDeclarationTests.cs`
- `ProductionRenderPipelineDeclarationTests.cs`
- `ShaderBuildTests.cs`
- sample smoke options and lifecycle tests

## 7. Issue 3: execute and safely publish reflection probes

### 7.1 Failure mechanism to remove

`ReflectionProbeManager` allocates the cubemap array, maps probe IDs to layers,
queues captures, and exposes a capture API. No production pass calls that API.
`RequestRecaptureAll` also removes IDs from `_capturedProbeIds` immediately,
which would discard a valid old probe before its replacement exists.

The repair needs more than a call to `TryBeginCapture`: capture and prefilter
span GPU work and possibly several frames, and publication must be tied to GPU
completion.

### 7.2 Split logical scheduling from Vulkan resources

Extract a GPU-independent `ReflectionProbeCaptureScheduler` so state transitions
can be unit tested without a Vulkan device.

The scheduler owns compact per-probe state plus a dirty queue and a bounded
priority structure. Requests update one probe in O(log N) or better and allocate
nothing after capacity initialization. Camera-dependent priority may be updated
lazily for queued candidates; do not sort every authored probe every frame.

Cache active-probe selection, layer mapping, GPU metadata, and authored
revision. Rebuild them only when the scene/probe revision, reflection settings,
resource generation, or published-availability flags change. Replace the
current per-frame temporary lists/sets with retained scratch storage, and skip
metadata upload when its payload is unchanged. Editor probe mutation must
advance an explicit scene-owned `ReflectionProbeRevision`; add/remove and every
semantically relevant `ReflectionProbe` setter notify that owner, and removal or
scene disposal detaches notification. Detecting mutation by hashing or scanning
every probe each frame is not acceptable.

Each capture ticket contains:

- unique ticket serial;
- probe ID and stable published layer;
- immutable capture transform/shape snapshot;
- reason flags;
- requested `ReflectionCaptureVersion`;
- resource generation and scene revision;
- next face and next prefilter mip;
- retry count and last failure;
- submission frame/timeline value when awaiting completion.

Use this state machine:

```text
Queued
  -> CapturingFaces (0..5)
  -> PrefilteringMips (1..N-1)
  -> CopyReady
  -> AwaitingGpuCompletion
  -> Published

Any pre-publication failure -> RetryPending, DeferredChangingScene, or Cancelled
Any superseding request     -> keep current work or replace with latest by policy
```

Only one pending version per probe is retained. New requests merge reason flags
and replace the pending target version. They do not enqueue duplicates.
Before destination-copy commit, a superseding version that invalidates a
non-snapshotted input cancels at the next work-unit boundary and requeues latest.
After commit, the current ticket finishes against its pinned destination and the
latest version remains the single pending request. No policy may mix versions
inside one ticket.

Ticket payloads use immutable value snapshots and stable handles. They must not
retain mutable `Scene`, material, or authored-probe object references across
frames.

### 7.3 Preserve old published captures

Replace `_capturedProbeIds` as the sole state flag with separate records:

- `HasPublishedCapture`
- `PublishedVersion`
- `DirtyOrQueued`
- `InFlightTicket`

`RequestRecaptureAll` marks probes dirty and queues work but leaves the old
published layer valid. It is removed only when:

- the authored probe is deleted;
- its layer is recycled;
- the array is reallocated and old content cannot be preserved; or
- device/resource loss invalidates the image.

Metadata continues exposing the old layer while recapture runs. The first-ever
capture remains on global-environment fallback until publication.

Remove the current recapture behavior that clears `_capturedProbeIds` at request
time. Resource resize/reallocation becomes a generation transaction: allocate
and initialize the replacement, copy compatible published layers when possible,
switch descriptors only after completion, and retire the old image behind the
frame/timeline completion primitive. Reflection-owned code must not call
`WaitIdle` during this path.

### 7.4 Add persistent scratch capture resources

Do not render or prefilter directly into the sampled published layer.

Allocate, through the reflection memory budget:

- one RGBA16F cube scratch image with the configured resolution and full mip
  chain per allowed concurrent capture;
- one reusable depth target per active face recorder, or a documented layered
  depth target;
- one small slot-owned capture frame-data/light snapshot buffer so all faces use
  the same admitted environment and direct-light coefficients;
- face and mip views required by raster and compute passes;
- optional timestamp queries for capture and prefilter stages.

The scratch cube includes `ColorAttachment`, `Storage`, `Sampled`,
`TransferSrc`, and `TransferDst` usage as actually required. The published array
remains sampled/transfer-addressable and is never used as prefilter scratch.
Calculate exact residency before allocation:

```text
scratch cube bytes = 6 * bytesPerPixel * sum(max(1, resolution >> mip)^2)
depth bytes        = active depth targets * resolution^2 * depth bytesPerPixel
```

The formula is a payload lower bound and a cross-check, not the allocator charge.
The budget authority is the queried Vulkan memory requirement and actual
allocator-attributed image/buffer bytes, including alignment and heap-block slack
attributable to the allocation. Track engine host/object overhead for views,
samplers, descriptors, and simultaneously retired generations separately.
Reject or downscale before binding an allocation that would cross either the
reflection component budget or the global headroom gate.

Default to one concurrent scratch cube. More slots require evidence that both
the reflection component budget and the global >=20% tracked-memory headroom
survive peak coexistence with retired generations.

Create views, reusable descriptor state, timestamp ranges, and capture/prefilter
pipelines with the scratch resource generation. No image view, descriptor pool,
pipeline, or shader compilation may occur while advancing an ordinary ticket.
Reuse the existing GGX importance-sampling implementation and sequence where its
roughness/pdf contract matches; do not maintain a drifting second filter kernel.

Mip 0 receives the six raw radiance faces. Mips 1..N-1 receive GGX-prefiltered
radiance. After all work is complete, copy the full scratch subresource range to
the probe's stable cubemap-array layer.

Populate `ReflectionProbeCaptureTargetBytes` and reject/downscale allocation
through the existing component budget; do not let capture scratch appear as
untracked memory.

### 7.5 Add three graph-visible passes

Add graph resources for reflection capture radiance/depth and declare both the
current-frame reads and the eventual published-array write. Scratch work is not
visible to the current frame and should run after the latency-critical main-view
work when dependencies allow. The destination copy is ordered after all
current-frame readers of the old layer; completion and metadata publication make
the new layer available to a future frame. No reader can interleave with the
multi-subresource destination copy.

1. `ReflectionProbeCapturePass`
   - Acquires one ticket/face within the per-frame budget.
   - Renders one or more 90-degree cube faces from the probe position.
   - Writes linear scene radiance into scratch mip 0.
2. `ReflectionProbePrefilterPass`
   - Starts only after all six faces of the coherent ticket's mip 0 are complete
     and visible to the prefilter stage.
   - Importance-samples GGX for each requested roughness mip.
   - Processes a bounded number of mips/faces per frame.
3. `ReflectionProbePublishPass`
   - Copies all faces/mips from scratch into the stable array layer.
   - Transitions the published range for shader sampling.
   - Enqueues a completion token; it does not call logical publication merely
      because commands were recorded.

All three passes are conditional. When no face, mip group, or copy is selected,
`WillExecute` is false and the graph emits no empty pass, barrier, query, or
submission. Prefer one tail graphics segment for selected reflection work so
capture does not introduce an early wait before forward shading.

Run these passes on graphics initially. Do not add them to async candidates
until Section 6 is complete and the reflection path is independently
validation-clean.

### 7.6 Capture rendering contract

Use the existing material and lighting model where possible, with an explicit
reflection-capture view flag rather than a second drifting material shader.

Reuse immutable scene/material/light/acceleration-structure data already
prepared for the frame. Build only the face-specific camera constants and
bounded visibility list. Use the existing GPU scene-submission/culling path when
it supports a cube face; otherwise use a retained scratch list and frustum-cull
per face. Do not rebuild materials, BLASes, or whole-scene upload payloads for a
probe face.

The capture must include:

- opaque and alpha-masked geometry;
- material base color, metalness/roughness, normal maps, and emission;
- directional and local direct lights;
- valid shadow visibility;
- the admitted procedural atmosphere/global environment;
- DDGI diffuse only when `CaptureIncludesDdgi` is enabled and its requested
  tracking generation is ready.

It must exclude:

- local reflection-probe sampling, including the probe being captured;
- screen-space reflections, SSGI, TAA, exposure, bloom, tone mapping, and UI;
- camera post-processing;
- transparent participation until a separate ordered-transparency contract is
  implemented and tested.

The capture is linear HDR. There is no exposure baked into the cubemap.

The capture version freezes one coherent admitted environment/direct-light
snapshot for all six faces. Global specular prefilter may publish newer visual
sky data independently while a local capture is in progress; the ticket never
mixes those versions between faces.

Materialize that small environment/light snapshot into the ticket's persistent
scratch slot before its first face. Before recording every later unit, verify the
scene-radiance, material, light, capture-AS, and relevant resource generations.
If a non-snapshotted input changed, cancel before destination-copy commit, retain
the old published probe, and requeue the latest version. Do not pin or duplicate
the whole scene merely to finish an obsolete capture.

Define the initial capture-participant contract explicitly. Continuously moving
geometry must either be excluded from the static probe capture, represented by a
ticket-stable capture snapshot/AS generation with accounted lifetime, or force a
safe retry. Repeated version churn transitions the probe to a bounded
`DeferredChangingScene`/fallback state with reason and age telemetry; it must not
spin, allocate, exceed the retry budget, or publish faces from different scene
versions. Supporting fully dynamic participants is a separate quality tier, not
an implicit source of unbounded retries in the first closure.

`CaptureIncludesDdgi` remains false unless one sampled DDGI publication
generation can be held stable for every face. Readiness of a generation alone is
not sufficient if the atlas will continue mutating underneath the ticket. If the
renderer cannot pin a stable sampled generation within the DDGI memory/lifetime
contract, keep DDGI out of local capture and record that reason.

Do not reuse camera-dependent shadow data when it lacks probe coverage. Prefer
ray-query shadow visibility in the capture view when the acceleration structure
is current; otherwise define and test a conservative shadow fallback.

Create one canonical cube-face direction/up table matching the engine's Vulkan
and shader cubemap convention. Unit-test all six view matrices with colored-axis
geometry so face swaps, vertical flips, and seams cannot regress.

### 7.7 GPU completion and atomic publication

At the end of `ReflectionProbePublishPass`, retain a completion record containing
ticket serial, resource generation, destination layer, frame slot, and timeline
or fence value.

Recording the destination copy commits that ticket for publication. Pin its
destination layer and image generation until completion; deletion, layer reuse,
or array retirement must be deferred behind the same completion primitive. A
newer request may queue, but it cannot cancel a copy that is already recorded.
The old cubemap remains intact while faces and mips are built in scratch. It is
replaced only when the final copy executes, after the complete scratch range is
available and before any later-frame graph-declared reader of the destination
range.

On a later frame, after that completion primitive is known signaled:

1. Verify the probe still exists, owns the same layer, and requested resource
   generation is current.
2. If a newer request superseded the completed version, publish the completed
   version as the valid intermediate result and leave the newer request queued.
3. Call the logical publication operation and update `PublishedVersion`.
4. Upload metadata that marks captured radiance available.
5. Increment both this-frame and cumulative completion counters.
6. Release the scratch slot for another ticket.

Completion handling polls an already-owned per-frame fence/timeline value. It
never waits on the render thread. An unsignaled ticket remains in
`AwaitingGpuCompletion` and contributes only constant-time queue bookkeeping.

A ticket may be discarded before its destination copy is recorded. After that
commit point it either completes against its pinned destination or follows the
device/resource-loss path. A completion for a probe that was deleted simply
releases the pinned layer after the fence; it must not be reassigned earlier.

### 7.8 Dynamic environment and DDGI recapture policy

Replace `ScheduleReflectionProbeRecapturesFromGi` dirty-flag edge detection with
versioned requests.

`ReflectionCaptureVersion` should include:

- scene radiance revision;
- static/dynamic light revision relevant to the probe;
- admitted GI environment generation;
- completed DDGI tracking generation when DDGI is included;
- material/emissive revision;
- capture acceleration-structure/resource generation;
- capture shader/settings revision.

Store the version as a compact value record with precomputed component
revisions. Do not hash the entire scene or enumerate all lights/materials on
each probe request; consume revisions already maintained by their owners.

Policy:

- Queue initial load after scene resources, materials, light data, and capture
  acceleration structure are ready.
- If DDGI is included, wait for the admitted source cohort and required
  visible/near propagation boundary; do not wait for full static convergence
  under a moving sun.
- Coalesce repeated sun/GI changes to the latest target version.
- Use a minimum environment recapture interval and a maximum allowed capture
  age. Two Sponza probes can refresh frequently, but production scenes must not
  recapture every quantized sun candidate.
- Prioritize visible/influential probes, then distance, authored priority, and
  age with stable tie-breaking.
- Geometry/material changes queue only affected probes when influence bounds are
  available; otherwise conservatively queue all.
- Capture direct sun and the sky from one admitted environment snapshot so the
  cubemap is internally coherent.

After the functional path is accepted, evaluate a separate optimization that
stores local-scene coverage and composites current global sky for capture misses.
Do not make that approximation part of the first correctness closure; roughness
prefiltering of visibility times environment must receive its own visual oracle.

### 7.9 Reflection budgets and settings

Replace the all-six-faces interpretation of `MaxProbeCapturesPerFrame` with
explicit budgets:

- `MaxConcurrentProbeCaptures`
- `MaxProbeCaptureFacesPerFrame`
- `MaxProbePrefilterMipsPerFrame`
- `ReflectionCaptureGpuBudgetMicroseconds`
- `MinimumEnvironmentRecaptureIntervalSeconds`
- `MaximumEnvironmentCaptureAgeSeconds`
- `CaptureIncludesDdgi`
- retry limit/backoff

Keep old serialized settings load-compatible. Map the legacy capture count to a
safe default and write the new schema on the next explicit save.

The first implementation defaults to:

- one concurrent capture;
- one face or one bounded prefilter mip group per frame;
- a 0.50 ms predicted GPU scheduling target;
- a 1.00 ms per-frame profile ceiling;
- no DDGI contribution until the non-DDGI capture path is accepted.

Use timestamp-history EWMA per work kind/resolution to predict the next unit.
When no unit fits the remaining budget, defer it. With no history, schedule at
most one smallest legal unit. A repeated single-unit ceiling miss downscales or
disables capture through an explicit quality decision; it never publishes
partial data or silently exceeds the budget.

### 7.10 Reflection diagnostics

Report:

- authored, active, fallback-only, and published probe counts;
- queue depth, active tickets, awaiting-completion tickets, and retries;
- current face/mip progress per active ticket;
- requested and published version per probe;
- oldest capture age and staleness reason;
- captures started/completed/failed this frame and cumulatively;
- CPU record and GPU capture/prefilter/copy times;
- scratch, published array, metadata, view, and sampler memory;
- last cancellation/failure reason;
- count of frames that sampled fallback because no first capture existed.

Rename or document `CapturesCompleted` so a per-frame counter cannot be mistaken
for the total lifecycle result.

Per-frame diagnostics read scheduler scalars and timestamp results already
collected for budgeting. Per-probe progress lists are built only for an explicit
snapshot/debug request, not every shipping frame.

### 7.11 Reflection tests and acceptance gates

Unit tests:

- state-machine transition legality;
- stable layer ownership;
- old capture retained during recapture;
- latest-wins request coalescing and reason merging;
- capture budget across faces/mips;
- stale completion after delete/reload/reallocation;
- scene/material/light/AS change between faces cancels before copy and requeues
  the latest version;
- continuous scene-version churn reaches bounded deferred/fallback state without
  queue growth or retry spin;
- one ticket uses identical environment/light snapshot bytes for all six faces;
- retry/backoff and permanent failure;
- six cube-view orientations;
- mip/face/layer subresource ranges;
- serialized setting migration;
- scene reflection-probe revision on add, remove, and property mutation;
- unchanged revision reuses active selection and emits no metadata upload.

Shader/graph tests:

- local probe recursion is disabled in capture view;
- all capture output stays linear HDR;
- prefilter roughness maps monotonically across mips;
- graph declares scratch writes, prefilter dependencies, copy, and published
  shader reads;
- prefilter cannot execute until all six mip-0 faces of the same ticket are
  complete;
- reflection capture remains graphics-only until separately certified.

Integration/visual tests:

- two Sponza probes progress from queued to published within the calculated
  face/mip budget;
- capture and prefilter CPU/GPU timings become nonzero;
- queue depth returns to zero after a stationary load;
- chrome and rough spheres show local geometry with correct orientation and no
  cube seams;
- box projection and overlap blending remain correct;
- a sun change keeps the old probe visible until the replacement is complete;
- a failed/stale recapture never produces black or partially filtered
  reflections;
- resize, scene reload, shader reload, probe deletion, and shutdown are
  validation-clean;
- a 30-minute moving-sun run stays within capture-age and GPU-budget limits.

Acceptance is zero Vulkan validation warnings/errors, two published Sponza
probes, complete mip chains, no recursive local reflections, no exposure baked
into captures, and no single-frame hitch above the declared capture budget plus
measurement tolerance.

Performance and lifetime acceptance additionally require:

- <=0.02 ms CPU P95 delta while enabled but idle and exactly zero GPU work;
- <=0.25 ms CPU record P95 on capture-work frames;
- predicted capture work at or below the 0.50 ms target and measured work at or
  below the 1.00 ms profile ceiling after timestamp warmup;
- no reflection-owned device wait during load, recapture, resize, reload,
  deletion, or retirement;
- no per-frame authored-probe sort, scene/material rebuild, or managed
  allocation after warmup;
- no reflection metadata upload on an enabled-idle frame;
- no image/view/descriptor/pipeline/shader creation while advancing an ordinary
  ticket after resource-generation warmup;
- peak scratch + published + retired residency keeps the reflection component
  within budget and total tracked memory at or below 80% on the target profile;
- the feature-off stationary repeat remains within the existing benchmark
  tolerance, proving that conditional pass integration has no idle regression.

### 7.12 Issue 3 implementation map

Primary files:

- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
- proposed `Njulf.Rendering/Resources/ReflectionProbeCaptureScheduler.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbeCapturePass.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbePrefilterPass.cs`
- proposed `Njulf.Rendering/Pipeline/ReflectionProbePublishPass.cs`
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResource.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Data/ReflectionProbeData.cs`
- `Njulf.Rendering/Data/RenderSettings.cs`
- `Njulf.Core/Scene/Scene.cs`
- `Njulf.Core/Scene/ReflectionProbe.cs`
- capture/prefilter shaders and pipeline objects

Primary tests:

- proposed `ReflectionProbeCaptureSchedulerTests.cs`
- `ReflectionProbeDataTests.cs`
- `ProductionRenderPipelineDeclarationTests.cs`
- `RenderGraphResourceDeclarationTests.cs`
- `ForwardPlusPassTests.cs`
- `ShaderBuildTests.cs`
- renderer diagnostics and lifecycle smoke tests

## 8. Recommended implementation and PR sequence

Keep the changes reviewable and leave a clean rollback point after every slice.
Each PR includes its own same-binary control/candidate report; do not mix an
unrelated renderer optimization into a correctness comparison.

### PR 1: Evidence, terminology, and safety

- Add deterministic scenarios and version/capture diagnostics.
- Add path certification/quarantine state.
- Prevent uncertified Auto timing probes.
- Capture current-binary graphics stationary, animated, forced-Hi-Z, and
  reflection-lifecycle baselines.
- Reuse the existing benchmark identity/pair format and keep new observability
  out of shipping shaders.
- No lighting algorithm or capture rendering change.

Gate: graphics-only remains clean; normal Auto submits no uncertified path; the
stationary repeat passes existing comparison tolerances; instrumentation adds no
production shader atomics, device wait, or unowned memory.

### PR 2: GI atmosphere admission

- Add requested/admitted frames and pure admission controller.
- Wire the admitted frame atomically to GI environment buffer, dirty signature,
  DDGI directional snapshot, and fallback radiance.
- Add latest-wins coalescing and freeze/scrub behavior.
- Give global specular prefilter its own requested/building/published version and
  skip held GI-buffer uploads.

Gate: at 60x/0.25 degrees/8 seconds, generations no longer advance while the
source cohort is active; global specular builds still progress; admission CPU
P95 delta is <=0.05 ms with zero held-path allocation/upload.

### PR 3: DDGI tracking and generation contract

- Audit and extend the existing current-generation cache reuse/publication
  contract rather than replacing its arrays or queues.
- Convert the existing probe-count source throughput floor to ray-equivalent
  capacity and expose achievable lag.
- Split `Tracking` from static convergence diagnostics.
- Add validation-build mismatch counters and full cadence tests.

Gate: every admitted Sponza cohort drains, the frozen sequence converges,
generation mismatch evidence is zero, moving-sun source traces/invalidations
decrease relative to the legacy baseline without increasing no-op lanes, and the
stationary repeat remains within tolerance.

### PR 4: Async synchronization foundation and Hi-Z

- Add queue-stage validator and projected layout transaction.
- Audit and retain the existing paired transfer recorder/timeline edges.
- Remove duplicate Hi-Z cross-pass transitions.
- Certify Hi-Z alone.

Gate: forced Hi-Z is validation-clean through lifecycle tests and matches
graphics output; stable accepted-plan CPU P95 delta is <=0.05 ms with zero
allocation and no device wait.

### PR 5: Remaining async path certification

- Audit AO blur, bloom, far field, fog, particles, Simple DDGI, full DDGI, and
  SSGI one scheduling unit at a time.
- Enable Auto eligibility only after each path's evidence gate.
- Capture at least three locked graphics/async pairs for profitability after
  correctness certification.

Gate: all enabled combinations are validation-clean; nonprofitable paths remain
on graphics; every Auto-enabled path improves median GPU frame by at least 3%,
does not regress P95/material-pass budgets, and does not meaningfully regress CPU
record/submit time.

### PR 6: Reflection scheduler and safe resource model

- Extract/test the capture state machine.
- Preserve old published layers during recapture.
- Add one default scratch slot, exact budget accounting, completion tokens, and
  deferred generation retirement without reflection-owned `WaitIdle`.
- Add conditional graph resource/pass declarations whose no-work path records
  nothing.
- Add scene-owned reflection-probe revisioning and cache unchanged selection,
  metadata, and uploads.
- No scene capture rendering yet; fallback remains expected for first captures.

Gate: state and lifetime tests pass; no partial layer can be exposed; enabled-idle
CPU P95 delta is <=0.02 ms with zero GPU work; peak memory preserves component
budget and >=20% target-profile headroom.

### PR 7: Reflection capture, prefilter, and publication

- Add graph passes and capture-view shader mode.
- Render six coherent faces, prefilter all mips, copy, and publish on a later
  frame after the completion token signals.
- Tail-schedule bounded graphics work, reuse existing scene/material/AS data, and
  poll completion without a CPU wait.
- Add orientation and chrome/roughness visual tests.

Gate: two Sponza probes publish complete, useful local reflections with zero
validation errors; capture record P95 is <=0.25 ms and warmed GPU work stays
within the 0.50 ms target/1.00 ms ceiling.

### PR 8: Dynamic recapture integration and production soak

- Replace GI dirty edges with versioned, coalesced recapture requests.
- Tune face/mip and age budgets.
- Run full moving-sun, async, reflection, resize, reload, and soak matrix.
- Update defaults only after the release report passes.

Gate: all Section 10 release criteria pass in one reproducible report set,
including inherited production-performance gates or an explicitly approved
profile revision. Features that are correctness-complete but not performant
remain disabled/fallback on the affected profile.

## 9. Cross-system test matrix

| Axis | Required values |
|---|---|
| Time | stationary, 60x animated, rapid scrub, freeze after pending step, dawn/noon/sunset |
| Frame rate | fixed 30/60/120 Hz, variable frame time, long hitch |
| GI | Simple V2, Simple V1 compatibility, full DDGI where supported, GI disabled |
| Async | disabled reference, each path forced alone, all certified forced, Auto |
| Queues | same family where available, dedicated compute family, no async queue |
| Reflection | none, one, two Sponza, capacity limit, delete/re-add, recapture while valid |
| Lifecycle | first load, warm run, resize, minimize/restore, shader reload, scene reload, quality change, shutdown |
| Validation | standard, synchronization validation, GPU-assisted where supported |
| Build/evidence | validation build, `DetailedInvestigation`, `ShippingPerformance` with validation and detailed counters off |
| Work state | disabled, enabled-idle, first work, steady work, capacity/resource-generation change, failure/retry |
| Memory | steady residency, scratch active, old/new generations coexisting, retirement completed, budget rejection |
| Output | locked-exposure linear HDR, curtain ROI, diffuse interior ROI, chrome/rough spheres, debug overlays |

Run fast unit/contract tests on every PR. Run five-minute deterministic scenarios
on PRs 2, 3, 4, and 7. Run the 10,000-frame and two-hour soak only after the
individual gates are clean.

Validation and `DetailedInvestigation` runs establish correctness and
attribution; they are not performance evidence. Production timing uses
`ShippingPerformance`, validation off, detailed GPU counters absent, a settled
workload, and the existing identity lock. Capture at least three repeats for a
promotion decision. Report disabled/idle cost separately from active work so an
amortized feature cannot hide a permanent per-frame tax.

## 10. Final release criteria

The three issues are closed only when one release bundle demonstrates all of
the following.

### 10.1 Sun/sky/DDGI

- Visible sun, shadows, and sky follow the current time-of-day frame.
- The admitted GI snapshot and its dirty signature always match.
- Every admitted atmosphere cohort completes within its achievable declared
  budget.
- Continuous animation reports bounded tracking; frozen time reaches static
  convergence.
- Cache-reuse and publish generation mismatch counters are zero.
- Curtain and interior HDR sequences contain no black flash, spike, persistent
  stale sun, or double-counted sky energy.

### 10.2 Async compute

- Graphics-only and every enabled async matrix cell report zero Vulkan warnings
  and errors.
- No compute-only command buffer names an unsupported stage.
- Every queue handoff has the required semaphore and release/acquire/layout
  contract.
- Resize/reload cannot submit a stale resource plan.
- Async output passes equivalence tests.
- Auto enables only certified, measured-beneficial paths.

### 10.3 Reflection probes

- The two Sponza probes leave the queue and reach a published version.
- Capture, prefilter, copy, and completion telemetry are nonzero and internally
  consistent.
- All six faces and every mip are complete before first publication.
- Old local reflections remain visible throughout recapture.
- Missing/failed first captures use the global environment fallback.
- Captures are linear, correctly oriented, recursion-free, and budgeted.
- Moving-sun recapture remains coalesced and within the declared maximum age.

### 10.4 Performance and operational cost

- The unchanged stationary feature-off control remains within the existing
  repeat-comparer tolerances.
- GI admission/version snapshot CPU P95 delta is <=0.05 ms, allocates nothing in
  steady state, and held candidates add no GI upload bytes or GPU dispatch.
- Stable async-plan validation CPU P95 delta is <=0.05 ms with zero allocation;
  each Auto-enabled path satisfies its >=3% median benefit and P95
  non-regression gates.
- Reflection enabled-idle CPU P95 delta is <=0.02 ms with zero GPU work. Active
  record P95 is <=0.25 ms and warmed capture work stays within the 0.50 ms
  target/1.00 ms profile ceiling.
- Reflection enabled-idle frames perform no probe scan/sort and no metadata
  upload when the scene-owned reflection revision is unchanged.
- No new ordinary-path `WaitIdle`, synchronous GPU readback, per-frame
  full-probe scan/sort, scene/material rebuild, or managed allocation is
  attributable to these features.
- Peak published + scratch + retired residency is fully tracked, remains within
  component budgets, and leaves at least 20% tracked-memory headroom on the
  target profile.
- `ShippingPerformance` retains the production diagnostic-atomic audit and
  performs no exact mismatch/attribution readback intended only for validation.
- The active production profile still passes its global gates. With the current
  profile these include renderer CPU <=6 ms, GPU frame <=10 ms, GI CPU <=0.25
  ms, GI GPU <=2.5 ms, and tracked memory <=80%. This plan does not reinterpret
  an inherited failure as acceptable merely because its own A/B delta is small.

### 10.5 Combined soak

- Two hours in the animated Sponza scenario.
- At least 10,000 measured frames after warmup.
- Auto mode with only certified paths.
- Two local probes and DDGI enabled.
- At least one resize, shader reload, scene reload, time freeze/resume, and
  reflection recapture.
- Zero validation warnings/errors, device loss, timeout, unbounded queue growth,
  generation starvation, or tracked-memory budget violation.

## 11. Rollback rules

- GI admission can be disabled with a development-only switch that restores the
  old immediate stepping for A/B diagnosis. It must not be a shipping default.
- Each async path retains its graphics route permanently. Quarantine or lack of
  benefit disables only that path.
- Reflection capture can be disabled while keeping authored metadata and global
  fallback. A failed capture feature must not disable global environment
  reflections.
- Disabled/fallback modes execute the same zero-work conditional graph path used
  by the performance baselines and retire feature-only scratch resources behind
  normal frame completion.
- Settings and snapshot schema changes remain backward-load-compatible.
- No phase deletes old serialized fields until at least one full release cycle
  has loaded and migrated them successfully.

## 12. Risks and explicit decisions

| Risk | Decision or mitigation |
|---|---|
| Coalescing makes bounced sun appear delayed | This is intentional and bounded. Direct light remains current; diagnostics expose exact admitted age. Increase source capacity or lower time scale when a tighter contract is required. |
| Requiring static convergence would freeze animated GI | Moving time uses bounded tracking. Static convergence begins only when the candidate stream becomes quiet. |
| Admission duplicates or regresses the optimized scheduler | Admission consumes the existing incremental cohort summary and source queues. It is O(1), owns no probe collection, and must pass the stationary and moving-work A/B gates. |
| Holding DDGI also freezes global specular updates | Specular requested/building/published versions are independent from requested/admitted GI versions; add an explicit test that advances one while the other is held. |
| Full-field atomic DDGI publication consumes memory and increases latency | Start with strict per-probe generation gating and the existing canonical/private split. Add a 7.5 MiB stable atlas only if visual seam gates fail and the memory plan admits it. |
| Async fixes merely silence validation | Require output equivalence, committed-state checks, lifecycle runs, and profitability after validation reaches zero. |
| Async validation adds permanent planner overhead | Cache the fully validated plan by queue/settings/declaration/resource generation. Stable frames perform only constant-time generation checks. |
| Reflection capture reuses camera-only data | Use an explicit capture-view lighting contract and ray-query shadows when camera shadow coverage is insufficient. |
| Recapturing on every sun step is too expensive | Request from admitted/completed versions, coalesce latest, prioritize, and enforce minimum interval plus maximum age. |
| One capture face exceeds the frame budget | Schedule only one unknown-cost unit, use timestamp history, then downscale or disable capture through an explicit quality decision rather than overrunning every frame. |
| Scratch/retired images consume the remaining memory headroom | Default to one scratch slot, budget peak coexistence before allocation, reject/downscale before crossing 80% tracked residency, and release retired generations promptly. |
| Capture publication races frames in flight | Publish metadata only after the destination-copy completion token signals and the ticket/layer/resource generation still match. |
| New diagnostics invalidate performance evidence | Keep exact counters out of `ShippingPerformance`, reuse incremental CPU state, and capture correctness and timing in separate build configurations. |
| Existing global performance gates already fail | Preserve locked A/B attribution, but do not call the combined result production-ready until the active global budget profile passes or is explicitly revised by its owner. |
| Existing uncommitted renderer work overlaps these files | Implement in the PR slices above, rebase each slice onto the then-current tree, and never discard unrelated local changes. |

## 13. Authoritative external synchronization references

Use the current Khronos Vulkan documentation while implementing and reviewing
Section 6:

- [Synchronization and cache control](https://docs.vulkan.org/spec/latest/chapters/synchronization.html)
- [Synchronization2 examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [Latest Vulkan specification](https://registry.khronos.org/vulkan/specs/latest/html/vkspec.html)

The implementation should link exact VUIDs in structured validation failures,
including the observed queue-stage VUIDs 09675 and 09676. The documentation
links above are review references; zero validation output from the executable
matrix remains the acceptance authority.
