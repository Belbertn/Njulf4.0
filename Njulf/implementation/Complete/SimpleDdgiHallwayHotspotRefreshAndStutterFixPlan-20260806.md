# Simple-DDGI Hallway Hotspot, Refresh Blink, and Stutter Fix Plan

- Date: 2026-08-06
- Status: implemented and locally qualified on the target hardware; repository
  commit and broader cross-scene promotion remain owner decisions
- Target: current Simple-DDGI V2 transport with `GpuResident` scheduling and
  authoritative `SparseNearRing` paging
- Depends on:
  `Complete/SparseSurfaceAwareSimpleDdgiProbePagingImplementationPlan-20260803.md`
  and the tail-certification corrections described in `Inparalell.md`
- Primary scenarios: `GiSponzaRightWallStationary` and a deterministic
  two-to-three-metre Sponza plaza traversal

## 1. Decision and scope

The supplied evidence is sufficient to confirm transport/audit liveness defects
and to define a bounded remediation plan. It is not sufficient to select a
specific lighting-math change for the two bright hallway spots: the supplied
beauty image has no per-pixel contributor identity, direct-only comparison, or
DDGI debug planes. The plan therefore fixes the confirmed state-machine defect
first and makes the visual root cause an evidence-gated branch.

This plan owns:

- the stuck tail-audit/source-cohort lifecycle;
- exact sparse participant and audit coverage;
- ordinary-motion fine-page publication latency and visible transition quality;
- attribution of the upper-gallery bright spots;
- production performance qualification of the resulting path.

This plan does not:

- loosen the 2.5% tail tolerance;
- hide a leak with exposure, tone-mapping, arbitrary irradiance clamps, or a
  blind temporal blur;
- start the optional Phase-9 near-density experiment;
- promote Simple-DDGI async compute;
- treat Debug, Vulkan-validation, or detailed-counter timings as shipping
  performance evidence.

## 2. Frozen evidence

### 2.1 Artifact identities

| Artifact | SHA-256 |
| --- | --- |
| `ai-chat-attachment-11851341078634481344.png` | `f2cd59ea6a5514c63bf7a4383ee176d5ced107914562c5671220db254fd3a694` |
| `performance-20260805-215826-0866798-1f343c75cd954f6fb16d3a738baeddea.json` | `66d5155c43b8ed916f9d695d8391639b8a69512adb01b18d4b830bdd2e145ea9` |
| `performance-20260805-215833-0991115-a8671bfc505842cba298e023d40eb37b.json` | `a164d614a723b23c59fda76a90487da1fab4cf875963333e8e6168661b6a069d` |

Both JSON captures use:

- executable `sha256:12e89797ee09bc4b196e28ddbd028b4d394af5211bbb806d081d0345a8752eb0`;
- shader bundle `sha256:bfb5292efa4f9501a85171dbdc922967897c9422473d0e89b9f454ac37ad9e72`;
- camera `(6.0, 1.35, 0.0)`, yaw `-pi/2`, pitch `0.16`;
- 1600x900 `DdgiHigh`, `SparseNearRing`, Standard validation, Debug build;
- the same camera/view hash.

They are a longitudinal pair, not a release A/B pair. They are 218 rendered
frames apart; GPU timing was enabled only in the later capture, so settings and
live scene-state identities differ.

### 2.2 Confirmed defects

#### A. Completed audits loop while the controller remains frozen

At frames 499 and 717:

- `TailPhase = AuditFrozen`;
- `TailReason = AuditInProgress`;
- `TailAuditComplete = true`;
- solve epoch remains 54;
- canonical field generation remains 80;
- audit epoch advances from 7 to 10;
- each audit scans 61 chunks.

The system is repeatedly auditing the same solve witness and byte-equivalent
canonical field. A completed failed audit is therefore not making forward
progress. `SimpleDdgiTransportSolveController.TryAcceptAudit` returns coverage
failures to `AcceleratedSolve` without clearing the completed visit witness or
advancing the solve epoch, allowing the next frame to freeze the same field
again.

#### B. Scheduler and frozen audit disagree about the participant set

Both captures report:

- expected participants: 5,787;
- audited participants: 5,614;
- mismatch: 173 probes;
- expected texels: 370,368;
- audited texels: 359,296;
- mismatch: 11,072 texels, exactly 173 x 64;
- invalid-cache exclusions: 9,296;
- inactive exclusions: 458;
- non-finite and overflow counts: zero.

The certificate cannot be accepted until the scheduler witness and audit use
one generation-frozen eligibility predicate for resident, active, source-ready
probes.

#### C. The field is materially above the requested tail bound

The repeated audit reports:

- fixed-point defect: 0.2538395;
- field magnitude: 20.515625;
- observed contraction: 0.8784315;
- absolute tail bound: 2.0880368;
- relative tail bound: 10.1778%;
- configured relative target: 2.5%;
- resulting tolerance: 0.51289064.

The current field is not certified and a localized lighting error is therefore
plausible. The zero non-finite/firefly counters do not prove that an individual
probe is correct, particularly because per-volume energy readback is marked
invalid and no maximum-luminance probe identity is exported.

#### D. A source cohort is starved while audit freeze repeats

Across the 218-frame pair:

- source-cohort transition remains active;
- cohort elapsed time grows from 354 to 572 frames;
- source-refresh target remains 33 probes;
- refreshed source probes remain zero;
- source-ready telemetry remains zero;
- source capacity shortfall remains zero;
- scheduler feedback frame remains 164;
- hard, routine, and pending source-repair counts remain zero.

The controller is neither completing the cohort nor scheduling the work that
could complete it. A source transition must not be permanently starved by an
audit of an already failed field.

#### E. The fixed-camera hotspot is not explained by missing sparse pages

At this camera both captures are stable at:

- 181 visible demanded pages, 181 resident hits, zero missing;
- 193 resident pages and 183 fully published pages;
- zero current admissions, evictions, failures, or pressure;
- 0.1% or less primary-gather failure;
- 18.5% secondary-volume gathering;
- sampled irradiance luminance about 0.2141;
- visibility about 0.938;
- zero non-finite, forward-clamped, or reported firefly samples.

The bright upper-gallery regions can still be DDGI errors, but at this fixed
view they are not caused by a currently missing demanded fine page. Candidate
causes are the uncertified/stale transport field, visibility or relocation,
coarse-ring contribution, or direct-shadow leakage.

#### F. The earlier movement capture showed a separate publication problem

The previously supplied moving-camera snapshot reported 97 missing pages out
of 207 visible demands, only 112 published pages out of 223 residents, and an
ordinary allocation-to-publication P95 of 43 frames. That is consistent with a
coarse-to-fine lighting blink after moving several metres, but the two new
fixed-camera captures do not reproduce the transition. A deterministic temporal
capture is required before selecting a paging fix.

#### G. Diagnostic performance is over budget but now partially attributable

The later capture has valid GPU timestamps:

- GPU frame: 24.009 ms;
- forward opaque/inclusive GI gather: 18.734 ms;
- transparent: 1.639 ms;
- AO: 1.102 ms;
- standalone Simple-DDGI work: 0.579 ms;
- transport audit: 0.232 ms;
- page feedback: 0.290 ms;
- page demand/residency: 0.029/0.028 ms;
- CPU frame: 18.650 ms;
- Simple-DDGI CPU record: 0.396 ms;
- total GI CPU P95: 1.985 ms.

The inclusive forward number cannot attribute DDGI gather cost. These timings
also include Debug, Standard validation, and detailed investigation counters.
They establish where to measure, not a production regression decision.

## 3. Required invariants

1. `TailAuditComplete` may not coexist with `AuditFrozen/AuditInProgress`
   beyond the declared fence/readback latency.
2. A rejected complete audit may not re-audit an unchanged tuple of source,
   ownership, operator, canonical field, solve epoch, and participant witness.
3. Audit expected and observed participant/texel counts use one frozen
   predicate and match exactly.
4. Coverage, invalid-cache, non-finite, overflow, or stale-generation failure
   never produces a certificate.
5. Above-tolerance evidence advances a real solve epoch before another audit.
6. Coverage/cache failure enters bounded source repair or participant
   reconciliation before another audit.
7. A source cohort with a nonzero refresh target makes measurable progress or
   enters an explicit fail-closed recovery state; it cannot remain silent.
8. Audit freeze blocks canonical mutation only for an actual in-flight audit,
   never while completed evidence waits indefinitely.
9. A fine page becomes receiver-authoritative only after every valid probe in
   its publication cohort is generation-current and coherently published.
10. Missing or warming fine data continues to use the last coherent coarse
    result; no black, stale, or partially initialized page is sampled.
11. Diagnostic collection is bounded and fence-complete and creates no
    stable-frame per-probe CPU enumeration.

## 4. Implementation phases

### Phase 0: lock deterministic reproducers and temporal evidence

Use the existing `SampleSponzaGiCaptureHarness` camera and output matrix before
adding new rendering behavior.

1. Add a named upper-gallery hotspot ROI small enough that two bright regions
   cannot be diluted by the existing broad `central-upper-facade` average.
2. Add a deterministic plaza path that starts at the locked camera, translates
   two to three metres through the reported trigger region, pauses, and returns.
3. Export a fixed-size per-frame trace covering at least the preceding 512
   frames:
   - camera and recenter/cut state;
   - demand, admission, eviction, resident, initialized, and published pages;
   - visible hit/miss counts and publication latency cohorts;
   - scheduler feedback frame/resource generation and work/ray counts;
   - source-cohort target, completed work, and age;
   - tail phase, reason, solve/audit epochs, participant coverage, defect,
     tolerance, and audit readback age;
   - GPU pass timestamps, CPU record time, and fence waits.
4. Capture beauty, direct-only, final indirect, source-cache radiance, sampled
   irradiance, final diffuse, visibility/moments, probe state/relocation,
   clipmap contributor/blend, residency fallback, page age, and physical page.
5. Run three repetitions before any algorithm change and retain exact binary,
   shader, settings, scene, camera, and HDR identities.

If the existing debug matrix identifies the exact source, do not add more
instrumentation. Otherwise add a development-only selected-pixel gather trace
that records, for the primary and fallback volume, the eight virtual probes,
page/physical addresses, generation stamps, weights, irradiance RGB/luminance,
visibility moments/weight, classification/relocation, source and solve epochs,
and final contribution. Read back one selected pixel or one bounded ROI only.

Exit criteria:

- the stable bright spots and the motion blink are reproducible three times;
- every event has a frame-indexed state/timing trace;
- direct-only versus DDGI ownership of the spots is known.

### Phase 1: make failed tail audits progress

Update `SimpleDdgiTransportSolveController` and its manager integration.

1. Replace the generic failed-audit return with explicit transitions:
   - incomplete participant/texel coverage or invalid cache -> source repair or
     participant reconciliation, with the visit witness cleared;
   - finite evidence above tolerance -> advance solve epoch and clear visits;
   - non-finite or overflow -> fail closed, discard private results, and start
     a fresh bounded recovery generation;
   - quantization-limited -> report the explicit unsupported tolerance floor;
     never spin or silently certify.
2. Require every rejected complete audit to change phase and invalidate its
   completed witness before `TryBeginAudit` may succeed again.
3. Add an audit-readback deadline:
   `ceil(probeCount / chunkSize) + framesInFlight + readbackMargin`.
   On expiry, cancel the frozen audit once, record the exact reason, and resume
   repair/solve without `vkDeviceWaitIdle`.
4. Reject a new audit when the previous summary is complete but unconsumed.
5. Export counters for same-tuple re-audit attempts, completed-audit readback
   age, recovery count, and no-progress frames.

Focused tests:

- `CoverageFailure_ClearsWitnessAndCannotReauditSameTuple`;
- `AboveTolerance_AdvancesSolveEpochBeforeNextAudit`;
- `NonFiniteAudit_EntersFailClosedRecovery`;
- `CompleteAudit_LeavesFrozenPhaseWithinReadbackDeadline`;
- generation change during every audit chunk/readback boundary;
- 1,000-frame state-machine model with no terminal frozen loop.

### Phase 2: unify the sparse participant snapshot

1. Define one shared scheduler/audit eligibility contract for an active,
   resident, source-complete, generation-current transport participant.
2. Apply it identically in `ddgi_simple_schedule_feedback.comp` and
   `ddgi_simple_transport_audit.comp`, including sparse nonresident,
   suppressed, inactive, fresh, relocation, and source-repair states.
3. Freeze either:
   - a compact participant index buffer plus count; or
   - the exact metadata generations and predicate inputs used by both passes.
   Do not combine a delayed host count with newer GPU metadata.
4. Derive expected texels from the frozen participant count with checked math.
5. Add bounded mismatch evidence: counts per exclusion reason and the first
   small set of mismatching virtual/physical probe identities.
6. Verify all six prerequisite tail fixes from `Inparalell.md`; reuse them and
   their tests rather than implementing competing predicates.

Exit criteria:

- expected/audited participants are 5,787/5,787 or a newly justified identical
  pair for the locked scene;
- expected/audited texels match exactly;
- invalid-cache, identity, generation, stale, non-finite, and overflow counts
  are zero;
- no same-field audit repetition occurs.

### Phase 3: unstarve source cohorts and converge the field

1. Do not enter audit while current feedback reports source repair, a lighting
   cohort transition, fresh/exposed probes, or incomplete source cardinality.
2. When the source generation changes during or immediately after audit,
   cancel the audit and reserve source-refresh work before cached solver work.
3. Make the nonzero target observable and enforce progress: target 33 with zero
   capacity shortfall must schedule at least one source-refresh cohort within
   two fence-complete scheduler feedback periods.
4. Advance scheduler feedback while mutable work is allowed; a frozen feedback
   serial must have an explicit audit/readback reason and bounded age.
5. Add a computed convergence deadline from source sweep, solve epoch, audit
   chunks, frames in flight, and scheduling margin. On no progress, keep the
   last coherent field visible while rebuilding private source/solve state.
6. Publish the new canonical field only after the source cohort and certificate
   are current. Never expose a partly transitioned source generation.

Exit criteria:

- source cohort becomes inactive within its computed deadline;
- source-ready and refresh-completion counts advance monotonically;
- the tail certificate is current and at or below the 2.5% relative target;
- stable frames perform no audit dispatch until a real invalidation/periodic
  certification event requires one.

### Phase 4: re-evaluate and fix the bright spots by proven ownership

Re-run the locked capture after Phases 1-3 before changing visibility, shadow
bias, or gather composition. Use these mutually exclusive branches:

1. **Present in direct-only:** fix directional-shadow/geometry leakage. Inspect
   receiver-plane bias, cascade selection/blending, thin or one-sided geometry,
   and raster/ray-query sidedness. Do not change DDGI to compensate.
2. **Present in Dense, Shadow, and Sparse DDGI only:** use the selected-pixel
   contributors to fix the identified source-cache, visibility-moment,
   relocation/classification, emissive, or transport publication error. Verify
   first-sweep-only visibility updates. Preserve energy conservation; do not
   add an image-space clamp.
3. **Present only in Sparse:** correct sparse physical identity, page-cohort
   publication, or fine/coarse contributor blending. Dense remains the same-
   topology oracle.
4. **Present only while fallback contributes:** correct the coarse-ring
   visibility/ownership input or its cross-ring blend. Do not allocate an
   unbounded fine pool to hide the defect.

Extend detailed diagnostics so V2 reports valid per-volume energy evidence,
including maximum irradiance and its virtual/page/physical probe identity,
P95/P99 luminance, visibility moments, and source generation.

Visual gates:

- `DdgiSampledIrradiance` and `DdgiFinalDiffuse` stay within the existing 5%
  relative / 0.006 absolute golden tolerances;
- hotspot ROI P95 and maximum stay within the approved Dense/reference bound;
- right-wall relative-luma standard deviation is at most 0.02;
- thin-wall leakage is at most 0.03 relative luma;
- invalid HDR pixel ratio is zero;
- three human-reviewed motion runs show no isolated hotspot, black flash, or
  stale-cell flash.

### Phase 5: make ordinary-motion page publication coherent and prompt

Only perform this phase if the deterministic traversal still reproduces the
43-frame publication tail after transport liveness is fixed.

1. Schedule demanded unpublished pages page-major: complete all valid probes
   of the oldest/current-visible page cohort before spreading work across new
   background pages.
2. Reserve bounded request/ray lanes for newly admitted visible pages and
   return unused capacity to ordinary maintenance.
3. Bound the number of simultaneously partial visible pages by the amount of
   work that can be coherently published within the latency target.
4. Keep coarse lighting authoritative until a complete fine page generation is
   ready; switch ownership atomically at the page level.
5. If an abrupt but correct coarse/fine energy delta remains, add a short,
   generation-stamped cross-fade only after both endpoints are coherent. Cancel
   the fade on eviction, recenter, or source-generation change. Never blend
   stale or private payload.
6. Keep camera-cut admissions bounded and preserve the declared coarse result.

Exit criteria:

- ordinary-motion allocation-to-first-publication P95 <= 2 rendered frames;
- cut/teleport P95 <= 8 frames;
- no visible demanded page stays partial beyond the declared cohort deadline;
- zero black/stale flashes, duplicate owners, mapping disagreements, stale
  mutations, range errors, overflows, or in-flight eviction;
- no stable-frame allocation, rebind, resource resize, or device-wide idle.

### Phase 6: production performance and stutter qualification

1. Separate correctness and performance runs:
   - Standard and synchronization validation with detailed counters for
     correctness;
   - identity-locked Release/ShippingPerformance, validation off, detailed
     counters off for timing.
2. Produce paired forward variants so incremental DDGI gather time is an
   `Exclusive` or `PairedEstimate`, not the 18.734 ms inclusive forward draw.
3. Capture stationary, ordinary traversal, recenter, cut, and source-change
   windows, including P50/P95/P99 and single-frame maxima.
4. Confirm the audit-loop fix removes stable 0.232 ms audit dispatches. Profile
   page feedback only if its production P95 still exceeds budget.
5. Optimize measured bottlenecks only. Do not tune against the Debug detailed-
   counter frame.

Performance gates:

- total GI GPU P95 <= 2.5 ms for the selected profile;
- page-management GPU P95 <= 0.25 ms;
- added sparse forward-gather P95 <= 0.15 ms and <= 5% of Dense;
- added sparse CPU P95 <= 0.10 ms and total GI CPU P95 <= 0.25 ms;
- no ordinary-motion frame-time spike attributable to audit, source repair, or
  page publication;
- stable certified frames dispatch no transport audit and perform no resource
  churn;
- three locked repetitions pass on the target RTX 3060 Laptop GPU.

### Phase 7: regression, failure injection, and promotion

Run:

- full Release test suite and shader-contract verification;
- Standard and synchronization validation;
- Dense -> Shadow -> Sparse -> Dense live transitions;
- sparse resize, reload, shutdown, and frames-in-flight retirement;
- slow translation on every axis, page/ring boundaries, recenter, cut, and
  teleport;
- source/light/environment changes during source repair, solve, every audit
  chunk, final readback, and publication;
- missing cache, incomplete cardinality, stale generation, non-finite evidence,
  counter overflow, delayed readback, and pool pressure;
- Sponza, Cornell, thin-wall corridor, emissive room, outdoor, transparent,
  foliage, particle, and fog receivers;
- at least one long soak exceeding the source-refresh interval.

Update the paging and tail implementation-status documents with exact commit,
binary/shader hashes, reports, HDR references, memory, timing, and any remaining
unqualified configurations. Promote no tier until every applicable gate passes.

## 5. Required implementation order

1. Reproducer and bounded temporal evidence.
2. Failed-audit liveness and readback deadline.
3. Exact frozen participant coverage.
4. Source-cohort progress and current certificate.
5. Re-capture and select the proven visual branch.
6. Page-cohort latency fix, only if still reproduced.
7. Production profiling and measured optimization.
8. Full regression and promotion evidence.

Each phase must be independently reviewable and reversible. Do not combine the
tail state-machine fix, a speculative visibility/shadow adjustment, temporal
cross-fading, and performance tuning into one change.

## 6. Definition of done

The work is complete only when:

- no completed audit remains frozen or re-audits an unchanged failed field;
- participant and texel coverage are exact and a current <=2.5% certificate is
  reached within the computed deadline;
- source cohorts make progress and complete without exposing mixed generations;
- the locked hallway hotspot is absent or demonstrated to be physically/directly
  lit using the direct-only and contributor evidence;
- the deterministic two-to-three-metre traversal has no visible blink or
  measurable refresh stutter;
- publication latency, performance, memory, validation, and lifecycle gates all
  pass three identity-locked repetitions;
- Dense rollback remains exact and no safety invariant is weakened.

## 7. Implemented result and qualification

This worktree implements the plan's production path. The implementation is
based on Git `HEAD eaa88dbf9cfac707eae06f87f8cc2d19b17a7e68`; the changes are not
committed, so there is deliberately no implementation commit hash to report.

### 7.1 Implemented behavior

- The locked capture contract now includes deterministic stationary and motion
  windows, fixed camera/ROI evidence, linear-HDR planes, direct-only and
  contributor views, a 512-entry bounded temporal trace, and SHA-256 artifact
  verification. The high endpoint waits through the periodic source refresh
  and a bounded 640-frame tail-certification settle window.
- Tail progress is generation-stamped. Completed failed audits cannot refreeze
  the same tuple indefinitely; audit readback and convergence deadlines have
  bounded recovery, and certification uses one shared exact transport
  participant predicate for solve, scheduler feedback, and texel audit.
- Source refresh and accelerated-solve cohorts make explicit bounded progress.
  Cache identity, cardinality, source epoch/generation, physical generation,
  non-finite, and counter-overflow failures fail closed and are counted.
- Sparse paging keeps dense physical payload for `Shadow` while preserving the
  sparse virtual topology used as its oracle. Authoritative sparse mode admits
  and schedules visible pages page-major, initializes a new resident probe with
  active weight, and publishes a page cohort only after its valid probes have
  current source/classification results.
- The GPU-resident scheduler uses the shared 11-word private-state ABI in every
  reset, page-initialization, and feedback shader. Materialization is a bounded
  GPU pass; no per-frame CPU queue walk or variable readback is required.
- Forward diffuse DDGI uses a half-resolution receiver cache populated at 12x
  gather scale and resolved with centered bilinear sampling. One aligned
  `uvec4` fragment load supplies FP16 DDGI and diffuse-environment irradiance;
  specular environment sampling remains full fidelity.

### 7.2 Three locked target-hardware repetitions

Three clean Release/production runs used validation off, detailed timing
counters off, async disabled, the NVIDIA GeForce RTX 3060 Laptop GPU, Vulkan
1.4.341, and driver 610.248.0. Every run has contract fingerprint
`e761924d1b914048439c67ac1c3f4030a50f7dc5bebcfdce669eea06e8a7f902`,
status `completed`, 121/121 manifest artifacts marked verified, and zero
mismatches in an independent post-run SHA-256/byte-length pass.

| Run | Manifest SHA-256 | Motion pages resident/published/initializing | Ordinary publication P95 | Current tail bound | GI GPU / CPU P95 |
| --- | --- | ---: | ---: | ---: | ---: |
| `final-production-capture-certified` | `95581c001db5c425b40b3e5d39b6f58bb2e27834c26ac6648bc3261a7ce1c9e3` | 223/223/0 | 1 frame | 1.4959423% | 0.614 / 0.058 ms |
| `final-production-capture-certified-r3` | `b426c4de8a2d4a2275254968bc8587b1f1126a96976fd288449083a751b89681` | 223/223/0 | 1 frame | 1.4959423% | 0.620 / 0.066 ms |
| `final-production-capture-certified-r4` | `bd78503d98bd7621ce408d8abfe2ff9d031f516ffe660b62fa8c5fcf1ab4b4ab` | 223/223/0 | 1 frame | 1.4959423% | 0.618 / 0.059 ms |

All motion endpoints have zero pending-fresh and pending-source probes. The
high endpoint in every run is `Certified` and current with 6,063/6,063 exact
participants and 388,032/388,032 exact texels. The fixed-point defect is
0.03702545, the certified absolute bound is 0.3045645, and the 1.4959423%
relative bound is below the unchanged 2.5% requirement. Cache/generation/
identity/non-finite/overflow failures, same-tuple re-audits, audit-readback
timeouts, source no-progress recovery, and convergence-deadline recovery are
all zero.

The final sparse high endpoint reports 291 resident and 291 published pages,
zero initializing pages, zero retired bytes, and zero mapping disagreement,
duplicate owner, stale request, out-of-range request, and receiver-request
overflow counters. Ordinary allocation-to-publication P50/P95/maximum is
1/1/1 frames; allocation-to-schedule P95 is 2 frames and maximum is 6.
Physical payload is 97,259,520 bytes; allocated sparse capacity is 111,474,128
bytes versus 138,383,040 dense-equivalent bytes, avoiding 26,908,912 bytes.

Fresh visual review of beauty, indirect-only, and probe-residency planes at the
low and high endpoints found no isolated hallway hotspot, black/stale cell, or
block-shaped residency artifact. Direct-only and contributor planes remain in
the locked artifact set so a future lighting change cannot hide such a defect.

### 7.3 Receiver quality and performance

The exact bilinear HDR oracle and the dual-irradiance cache candidate are
1920x1080 linear PFM captures. Relative RMSE is 4.559159%, mean absolute error
is 0.00426034, and invalid HDR pixels are zero, passing the plan's 5%/0.006
gates. The report is
`.tmp/hallway-hotspot-final/receiver-cache-dual-irradiance-quality.json`
(`sha256:536c27e2c9e33fbd05f8801e04762e4286bc7b909116333feb83e27e3e61a7f8`).

Three order-balanced production pairs measure forward-gather P95 deltas of
+0.065, +0.044, and +0.057 ms, all below the +0.15 ms gate. The receiver-cache
pass itself is 0.083-0.084 ms P95. Page demand/residency are below timer
resolution, page feedback is 0.022 ms P95, stable transport-audit dispatch is
0 ms, and total production GI remains below 0.63 ms GPU and 0.07 ms CPU P95 in
the three final captures. The comparison report hashes are:

- repetition 1: `cb59d1d1d14a7ce60f8e5cae058af6038dc008ba0b2ab87a2ead4304d28b9bef`;
- repetition 2: `e43c48d58954c8b00b85c084a28293cdc1523c311ec321b4e32de8cb53f730f4`;
- repetition 3: `3cf85fde163034e95c08e60e70f90352bc019216221f491d094c8a972b9faf88`.

The targeted forward metric passes in every pair. Whole-frame repeatability in
two reports is intentionally not used as attribution evidence because unrelated
CPU, depth, and directional-shadow timing moved outside the report's 5% noise
band; the identity-locked per-pass delta and the three final production runs are
the acceptance evidence.

### 7.4 Regression and lifecycle evidence

- The authoritative Release suite executed 2,033 non-explicit tests and passed
  2,033 with zero failures. One explicit hardware gate was not executed. TRX:
  `Njulf.Tests/TestResults/full-shipping-final-green.trx`,
  `sha256:7295bcd7d9ff69d3dbfdffd3c4bcb787470d634f7b1872ec3be81f47a85f6a60`.
- Production SPIR-V audits pass: no unexpected production atomics; exact
  receiver/update access sets; a single `uvec4` receiver-cache fragment load;
  required cache modules and cache-free controls match their contracts.
- Standard validation passes live `Dense -> Shadow -> SparseNearRing` and
  sparse rollback without renderer restart, with zero validation warnings or
  errors and zero GI errors. Report SHA-256:
  `2f7a405e35b0b85ed421f7aa29f868c3dc2cb9a888069bedae0f76638b6da5d2`.
- Synchronization validation passes three resizes, a zero-framebuffer minimize,
  restore, two scene reloads, and shutdown with zero validation warnings or
  errors and zero GI errors. Report SHA-256:
  `f9c6d7cf271679c2cf77aa8fe6c7f973d0bceec7433a6e63896752cdf23c565f`.

The qualified Release identities are:

- sample host EXE: `645ba530da459957653382cdef752238a0838ad03f177db889d13286a3ef6b3f`;
- sample assembly: `cb759a07934e989227a17323a6421e61c5b9cf6ba9046f726bc2ebe2faec229c`;
- rendering assembly: `ca38368995f10107898ae8ad9af35c020fca13f79a3615976a27971884dc9e4e`;
- shader bundle: `8ca68df3e3a775f9c4d5797f6005c87c8a20652bcfc6da34d5ebee1a37d8076f`.

This closes the targeted hallway, refresh, publication, stutter, tail-liveness,
memory, and target-GPU gates. It does not claim an owner-approved repository
commit, Nsight signoff, or the paging status document's broader Cornell,
outdoor, multi-floor, foliage, particle, fog, and transparent-scene promotion
matrix.
