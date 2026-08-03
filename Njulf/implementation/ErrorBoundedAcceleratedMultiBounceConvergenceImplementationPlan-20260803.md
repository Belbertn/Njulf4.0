# Error-Bounded, Accelerated Multi-Bounce Convergence Implementation Plan

- Status: Planned
- Date: 2026-08-03
- Target: Simple DDGI Transport V2
- Selected solver: coarse-to-fine red-black Gauss-Seidel with bounded cached-source sweeps
- Convergence contract: full-field remaining-tail certificate, not generation count or local residual
- Primary quality goal: remove visible high-albedo missing-bounce error without increasing primary ray queries

## 1. Required outcome

Replace the current Transport V2 retirement policy with a mathematically
defensible upper bound on energy still missing from the published irradiance
field, and converge that field faster with ordered cached-source updates.

The completed implementation must satisfy all of the following:

- A probe or field is never called converged merely because it completed a fixed
  number of solver generations.
- A small local residual cannot retire a dark downstream probe before bounce
  energy has had time to reach it.
- The reported remaining-tail value is an upper bound under an explicit,
  enforced transport-contraction contract.
- The static field retires only after a complete, generation-current audit of
  every active, source-ready participant.
- Solver acceleration performs no additional TLAS ray query, shadow ray, source
  trace, or emissive-source evaluation. It reuses the existing Transport V2
  source cache.
- Request and primary-ray budgets remain authoritative. Additional cached
  transport sweeps are charged separately to the GI GPU-time budget.
- V1 behavior remains available as a compatibility path.
- Non-finite, incomplete, stale, or generation-mismatched evidence fails closed:
  the field remains pending and rendering keeps the last complete publication.

## 2. Why the current policy is insufficient

The current implementation combines:

1. a legacy-named minimum generation count from
   SimpleDdgiTransportMaximumSolverGenerations;
2. a per-probe normalized fixed-point residual stored in
   GPUSimpleDdgiProbeState.Reserved0 / luminanceChangeEma;
3. a decaying residual envelope;
4. a stable-update count; and
5. a field barrier that releases when 95 percent of source-ready probes are
   locally converged.

The relevant paths are:

- [SimpleDdgiVolumeManager.cs](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs):
  HasLocalTransportConvergenceEvidence,
  MeetsTransportConvergenceCriteria,
  EvaluateTransportGlobalConvergenceState, and completed state readback;
- [ddgi_simple_blend.comp](../Njulf.Shaders/ddgi_simple_blend.comp):
  SimpleDdgiTransportConvergenceResidual and the residual envelope;
- [RenderSettings.cs](../Njulf.Rendering/Data/RenderSettings.cs):
  SimpleDdgiTransportResidualThreshold and
  SimpleDdgiTransportMaximumSolverGenerations;
- [SimpleDdgiBounceConvergenceTests.cs](../Njulf.Tests/SimpleDdgiBounceConvergenceTests.cs):
  the current white-enclosure, chain, residual, and retirement oracles.

There are two distinct correctness failures.

### 2.1 A residual is not a remaining-tail estimate

For the scalar white-enclosure model

    x = 1 + q x

with q = 0.95, the exact solution is 20. A Jacobi iterate is a partial geometric
series. If retirement accepts a next-step residual equal to 2.5 percent of the
current value, as much as one third of the final energy can still be missing.
The factor converting a fixed-point defect into a total tail is approximately
1 / (1 - q), which is 20 at q = 0.95. The current threshold does not include
that factor.

The fixed minimum of eight generations cannot correct this because the required
work varies with throughput, topology, propagation distance, relaxation, and
the current initial guess.

### 2.2 A local defect cannot certify a downstream probe

In a one-way probe chain, a far probe and its immediate predecessor can both be
black before an illumination wave reaches them. For that probe,
F(x) - x is locally zero even though its final value is nonzero. The existing
JacobiChain_FieldWideStabilityPreventsDistantBlackRetirement test already
demonstrates the symptom.

The correct Banach-style bound uses a norm over the complete active field, or a
separately proven propagated local bound. This plan uses a complete field norm.
Per-probe residuals remain useful for scheduling and diagnostics, but no longer
authorize retirement.

## 3. Selected design

Implement two coupled changes:

1. A full-field fixed-point audit produces a certified upper bound on the
   remaining energy tail.
2. A coarse-to-fine red-black Gauss-Seidel solver advances the cached field
   faster between audits.

Red-black ordered updates are selected over Chebyshev as the first production
solver because the current transport operator is sparse, visibility-weighted,
cross-volume, clamped, and not demonstrably symmetric. A Chebyshev method would
require a trustworthy spectral interval and previous-iterate storage, or a
restart/safeguard design with another full-field history. Red-black updates:

- reuse the canonical and existing private transport atlas;
- need no additional full irradiance atlas;
- naturally fit each volume's three-dimensional logical grid;
- preserve nonnegative under-relaxed updates;
- permit a fresh half-grid to be consumed by the other half-grid after a compute
  barrier; and
- remain correct even if acceleration is weaker than expected, because the
  independent tail audit is the convergence authority.

Coarser volumes are solved before finer volumes so fine fields can consume a
fresh low-frequency boundary/fallback estimate in the same cached sweep.

## 4. Mathematical convergence contract

### 4.1 Define the operator

For a frozen source and geometry/operator generation, express the full
irradiance field as:

    x* = F(x*) = s + K(x*)

where:

- x is every RGB irradiance texel of every active probe;
- s contains cached direct, sky, emissive, and environment-boundary terms;
- K contains recursive reflected/transmitted diffuse transport, normalized
  probe interpolation, volume blending, and directional reconstruction.

The implementation must establish a scalar q with:

    0 <= q < 1

and enforce:

    norm(F(a) - F(b), infinity) <= q * norm(a - b, infinity)

for every pair of valid fields a and b under the same operator generation.

### 4.2 Enforce energy-conserving recursive throughput

The existing transport shader clamps reflected and transmitted diffuse lobes
independently. That is not sufficient for a contraction proof because their
sum can exceed the configured ceiling.

Add one shared CPU/GPU helper that:

1. decodes reflected and transmitted diffuse reflectance;
2. disables transmission when the feature is off;
3. computes the component-wise lobe sum;
4. finds its maximum RGB component;
5. scales both lobes by the same factor when that maximum exceeds
   SimpleDdgiTransportAlbedoClamp; and
6. preserves the reflected/transmitted ratio.

After this operation:

    maxComponent(reflected + transmitted) <= qConfigured

where qConfigured is the clamped transport-albedo setting and never exceeds
0.99.

Apply the helper at transport evaluation time, not source-cache write time.
Changing the solver contraction limit must therefore restart the recursive
solve but must not force source rays to be retraced.

Add detailed diagnostics for:

- rays whose lobe sum required renormalization;
- maximum decoded lobe sum before renormalization;
- configured q;
- maximum enforced throughput observed by an audit; and
- malformed/non-finite material throughput.

Any malformed throughput produces zero recursive throughput for rendering,
marks the audit invalid, and requests a source/operator repair. It must not be
silently interpreted as converged.

### 4.3 Preserve a nonnegative normalized field operator

The proof depends on every interpolation/reconstruction stage being a
nonnegative normalized combination:

- trilinear probe weights;
- visibility/directional selection;
- cross-volume composition;
- octahedral bilinear filtering; and
- ray-radiance to irradiance projection.

The full blend estimator satisfies this shape. The reduced SH path can contain
signed basis lobes and therefore must not be used by a certified Transport V2
solve until it has a separately proven operator norm below one.

Initial production rule:

- Transport V2 accelerated solve and audit always use the exact positive
  irradiance estimator.
- SimpleDdgiReducedBlendEnabled remains available to V1.
- If low/medium performance later requires a reduced V2 estimator, implement a
  nonnegative partition-of-unity basis and prove its CPU/GPU norm before enabling
  it. Do not grandfather the signed SH reconstruction into certification.

Clamps to finite nonnegative radiance remain allowed because scalar clamping is
nonexpansive. Probe validity, visibility, relocation, classification, source
cache contents, and volume ownership must be frozen for the duration of an
audit.

### 4.4 Compute the tail certificate

For a frozen published field x, evaluate the unrelaxed fixed-point candidate
F(x) in FP32 and reduce:

    D = max over active probes, texels, and RGB of abs(F(x) - x)
    M = max over active probes, texels, and RGB of abs(x)

The remaining absolute field error is bounded by:

    B = D / (1 - qCertified)

qCertified is:

- the configured enforced q in the general case; or
- an upward-rounded maximum throughput observed across every current cache
  entry when the audit visited the complete participant/cache population.

The observed value may tighten the configured bound but may never exceed or
replace the configured safety ceiling. A residual-ratio estimate is useful
telemetry but is not a safe substitute for qCertified.

Use the existing near-black tolerance convention at the tail level:

    tolerance = max(
        SIMPLE_DDGI_TRANSPORT_ABSOLUTE_TAIL_TOLERANCE,
        tailRelativeTolerance * M)

The first implementation keeps the absolute tolerance at 0.0001 so the shader
and CPU oracle remain exact mirrors. A field is tail-certified only when:

    B <= tolerance

This means the authored relative value now limits total missing energy relative
to the peak active-field signal, rather than only limiting the next Jacobi
change.

The audit compares the FP32 candidate to the unpacked FP16 canonical value.
Therefore atlas quantization remains visible in D. If the configured tolerance
is below the representable fixed-point floor, report QuantizationLimited and
remain pending; never raise the tolerance silently.

### 4.5 Certification population

A certificate covers every probe that is:

- active;
- current for the frozen volume-table generation;
- backed by a complete current source cache;
- not fresh or scroll-exposed;
- not relocation-pending;
- not source-cache-invalid; and
- part of receiver-visible Simple DDGI transport.

Confidently inactive probes are excluded. An active source-repair probe, missing
cache entry, invalid material/operator record, or unvisited participant prevents
certification.

Remove the current 95-percent completion allowance from the static convergence
decision. Percentiles remain diagnostics; they are not error bounds.

## 5. Solver and audit state machine

Create a dedicated SimpleDdgiTransportSolveController rather than expanding the
already large SimpleDdgiVolumeManager with another implicit collection of
booleans.

Use these explicit phases:

| Phase | Meaning | Allowed work | Exit |
|---|---|---|---|
| SourceRepair | Source/operator population is incomplete or changed | Required source traces, relocation/classification, visibility publication | Every participant is current and the operator generation is frozen |
| AcceleratedSolve | Published field is not certified | Cached coarse-to-fine red-black sweeps; no extra source rays | A complete solve epoch has visited every participant |
| AuditFrozen | Canonical field and operator are frozen | Cached audit chunks only; forward rendering continues from the frozen field | Complete valid summary, cancellation, or invalid evidence |
| Certified | Tail bound passes | No routine cached-solver dispatch | A real source/operator/layout change invalidates the certificate |
| Tracking | Inputs change continuously and cannot freeze | Bounded cached sweeps and normal source cohorts | Input stream becomes quiet, then enter SourceRepair or AcceleratedSolve |

### 5.1 Required generations

Track independent non-zero generations for:

- volume table/layout;
- physical probe ownership;
- source lighting;
- per-probe source epoch;
- transport operator;
- canonical published field;
- solve epoch;
- audit epoch;
- queue transaction; and
- scheduler resources when GPU-resident scheduling is active.

The audit summary is accepted only when all frozen generations match. Any
publication during AuditFrozen increments the canonical generation and cancels
the audit.

### 5.2 Complete solve epochs

A solve epoch starts after source repair drains or after an audit reports a tail
above tolerance. Every active source-ready participant must receive at least one
ordered cached update before the controller can freeze for an audit.

High-priority probes may receive extra updates, but their extra work cannot
replace a complete fair sweep. Maintain an epoch visitation stamp/counter so
visible near probes cannot starve far maintenance probes.

There is no required minimum number of epochs. A black field can certify after
one complete audit; a 0.99-throughput enclosure remains pending until its bound
passes.

### 5.3 Audit cancellation

Cancel an in-progress audit when any of the following occurs:

- canonical publication;
- source, operator, volume, or queue-resource generation change;
- probe activation/deactivation that changes the participant set;
- relocation or toroidal ownership change;
- cache invalidation;
- feature-mode or estimator change; or
- non-finite audit state.

Discard the partial reduction by advancing the audit epoch. Do not synchronously
wait for old chunks; their epoch mismatch makes their writes irrelevant.

## 6. Coarse-to-fine red-black solve

### 6.1 Logical color

For each volume, derive the current logical coordinate with the existing
SimpleDdgiProbeCoord(localProbeIndex, volume) toroidal mapping. Define:

    color = (logicalX + logicalY + logicalZ) & 1

Use logical rather than physical slot coordinates so adjacent world cells retain
opposite colors after toroidal scrolling. Add CPU mirror tests for positive and
negative scroll deltas, wrap, full recenter, authored volumes, and all grid
dimensions.

### 6.2 Volume order

Order solver phases by:

1. larger spacing before smaller spacing;
2. outer/far ring before mid and near ring;
3. lower-priority fallback volume before a finer volume that consumes it;
4. stable volume index as the final deterministic tie-break.

Volumes with no admitted work produce no dispatch. If per-volume dispatch
overhead is material, group equal-spacing independent volumes only after a
dependency test proves they do not consume one another in that phase.

### 6.3 One accelerated sweep

For each ordered volume or safe volume group:

1. Dispatch color A transport using the canonical SSBO field and cached source.
2. Insert compute storage write-to-read visibility.
3. Run irradiance blend for color A into the existing private transport atlas.
4. Insert a storage barrier.
5. Publish only color A from the private atlas into the canonical SSBO atlas.
6. Insert a canonical storage write-to-read barrier.
7. Repeat transport, blend, and canonical publication for color B.

Alternate the starting color by solve epoch to remove a permanent color bias.

The receiver-visible sampled atlas is not updated between colors. Transport
solver gathers must therefore use the canonical SSBO explicitly and must not
take the sampled-image fast path during an accelerated sweep. Publish the
sampled mirror once, after the final color and final optional inner sweep.

The whole sequence remains one indivisible Simple-DDGI update segment. A
next-frame graphics or compute reader cannot begin until the segment's timeline
completion is visible.

### 6.4 Cached inner sweeps

Allow a bounded number of cached sweeps per admitted transaction:

- deterministic validation default: 2;
- proposed production default: 2 while AcceleratedSolve is active and 1 while
  Tracking;
- supported range: 1 through 4;
- low-GPU-headroom fallback: reduce the count, never loosen the tail tolerance.

Trace, relocate/classify, and source-cache writes execute once. Subsequent
sweeps execute only transport, irradiance blend, and intermediate canonical
publication.

Only the first sweep may:

- blend visibility;
- consume source-refresh scratch;
- clear fresh state;
- commit relocation/classification-related publication state; or
- advance one-update lifecycle counters.

Later sweeps update irradiance only. They increment dedicated cached-solver
iteration counters but do not pretend to be additional source updates or
primary rays.

### 6.5 Relaxation

For V2, use one fixed solver relaxation for a sweep. Bypass adaptive temporal
hysteresis in the recursive solver; it is a history filter, not part of the
fixed-point equation.

Keep SimpleDdgiTransportSolverRelaxation in the safe interval 0 < omega <= 1.
Start rollout at the current 0.70 value, then qualify 1.0 for frozen cached
sources. Do not add over-relaxation above one in the first implementation:
negative/overshoot states would complicate the nonnegative contraction contract.

The tail audit always evaluates raw F(x) - x and is independent of omega.

### 6.6 Queue and dispatch layout

Retain the existing source trace ray-count batches. Extend solver batch metadata
with:

- queue offset and probe count;
- ray cardinality;
- volume/spacing phase;
- color;
- sweep index; and
- first-sweep/final-sweep flags.

Prefer a stable compound queue key of ray tier, coarse-to-fine phase, and color
so trace can still dispatch broad ray-count ranges while solver subranges remain
contiguous. If this conflicts with the GPU-resident scheduler's lane layout, add
a bounded solver-offset indirection array rather than duplicating update records.

No solver batch may cause the trace pass to replay a ray query.

## 7. Full-field audit implementation

### 7.1 Dedicated cached audit kernel

Add ddgi_simple_transport_audit.comp. One workgroup handles one physical probe:

1. Validate the frozen global generations and per-probe ownership/source epoch.
2. Resolve volume and source cardinality.
3. Evaluate each cached source direction with the same shared recursive
   transport helper used by ddgi_simple_transport.comp.
4. Store per-ray total radiance in shared memory.
5. Reconstruct all 8 by 8 irradiance texels with the exact positive blend
   estimator.
6. Compare the FP32 candidate with the canonical FP16 field.
7. Workgroup-reduce maximum defect, field magnitude, and recursive throughput.
8. Atomically merge nonnegative float bit patterns into the epoch summary.
9. Increment audited, excluded, cache-invalid, and non-finite counters.

The shader must contain no rayQueryEXT operation and no source-light evaluation.
Extract shared source-cache transport and exact irradiance-estimator helpers so
normal solve and audit cannot drift into different equations.

### 7.2 Chunking and freeze behavior

A full 15k-to-32k-probe audit may exceed a single-frame GPU budget. Permit
contiguous audit chunks while keeping the canonical field frozen:

- chunk size is derived from measured cached-audit GPU time and the configured
  request capacity;
- forward rendering continues from the last complete field;
- normal source/solver publication pauses;
- a new scene/operator event cancels the audit immediately;
- the summary remains GPU-resident across chunks;
- completion is consumed only after the final chunk's fence/timeline point.

Do not create an atlas snapshot merely to allow concurrent solving. The selected
freeze trades a bounded amount of static convergence latency for roughly one
full irradiance atlas of avoided memory.

### 7.3 Audit resources

Add:

- a compact per-probe transport metadata record containing current source epoch,
  source cardinality, operator generation, and solve/audit visitation state;
- one GPU audit-summary record;
- one reset record or epoch-stamped summary region;
- frames-in-flight readback records for the small immutable summary.

Use uint atomicMax on floatBitsToUint for finite nonnegative maxima; this ordering
is exact for the permitted domain and avoids requiring float atomic extensions.
Checked counters fail the audit on overflow.

The expected new persistent storage should remain below 16 bytes per maximum
probe plus a few hundred bytes of summaries. Include it in
SimpleDdgiMemoryPlan and hard admission before allocation.

### 7.4 Accepted summary

The immutable summary must contain at least:

- audit epoch and completion flag;
- source, operator, canonical, volume-table, queue, and scheduler-resource
  generations;
- expected participant count;
- audited participant count;
- excluded inactive count;
- source-stale/cache-invalid/non-finite counts;
- maximum fixed-point defect D;
- maximum field magnitude M;
- configured and observed q;
- calculated absolute and relative tail bound;
- tolerance;
- audit chunk count and latency; and
- first/final frame serial.

The CPU and future GPU commit path must recompute or validate B from D and q; do
not trust an unchecked shader Boolean as the sole certificate.

## 8. Retirement, scheduling, and source refresh

### 8.1 Replace local retirement

For Transport V2:

- remove completedSolverGenerations from convergence eligibility;
- remove the residual envelope and stable-update count from retirement;
- remove per-probe local convergence as an authority;
- retain local defect/tail buckets only as priority hints and telemetry;
- keep all participants eligible through a complete solve epoch;
- retire cached solver work globally only after a valid tail certificate.

The existing arrays may remain temporarily for V1, A/B validation, and settings
migration, but production V2 decisions must not read them.

### 8.2 Replace the 95-percent global gate

EvaluateTransportGlobalConvergenceState becomes a controller transition based
on:

- source-repair population equals zero;
- solve epoch visitation is complete;
- audit participant coverage is exact;
- every frozen generation matches;
- invalid counters equal zero; and
- B is within tolerance.

If an audit fails only because B is too large, begin another accelerated solve
epoch without retracing sources.

### 8.3 Source and operator changes

Classify changes before deciding whether to preserve a certificate:

| Change | Source cache | Operator | Certificate |
|---|---|---|---|
| Identical periodic validation | Preserve/update age | Unchanged | Preserve |
| Direct/sky/emissive radiance changed | Refresh affected source | Source term changed | Invalidate and solve after cohort |
| Albedo/transmission/normal/hit topology changed | Refresh affected source | Changed | Invalidate |
| Visibility/classification/relocation changed | As currently required | Changed | Invalidate |
| Solver relaxation or sweep count changed | Preserve | Fixed point unchanged | Preserve field; cancel/restart in-flight solve scheduling only |
| Tail tolerance changed | Preserve | Unchanged | Re-evaluate certificate or re-audit |
| Throughput ceiling or estimator changed | Preserve raw cache where valid | Changed | Invalidate and restart solve |
| Volume topology/toroidal ownership changed | Existing generation policy | Changed | Invalidate |

Routine source validation should compare old and new cached source/operator
payloads before overwrite. An exactly unchanged payload must not manufacture a
new propagation wave. A changed operator always invalidates the certificate.
A future optimization may certify a small source-only delta with its own
delta-source bound; do not include that optimization in the first correctness
slice.

### 8.4 Periodic refresh watchdog

Remove the watchdog's dependency on minimum solver generations.

- While a tail solve is pending, defer ordinary periodic refresh cohorts so they
  cannot repeatedly preempt AuditFrozen.
- Retain a time-based maximum source-age watchdog for corruption/streaming
  detection.
- An unchanged watchdog refresh preserves the current solve epoch.
- A changed payload cancels the epoch and enters SourceRepair.
- After Certified, start the next periodic cohort from the normal age policy.

ResolveEffectiveTransportSourceRefreshFrames should be based on source sweep
capacity and a bounded solve/audit opportunity window, not a configured number
of generations. If convergence takes longer than that opportunity, the field
stays pending and the watchdog reports the condition; it does not force
retirement.

## 9. Settings and compatibility migration

### 9.1 New semantics

Add:

- SimpleDdgiTransportTailRelativeTolerance, default 0.025;
- SimpleDdgiTransportAcceleratedSweepCount, default 2, range 1 through 4;
- SimpleDdgiTransportAccelerationEnabled for staged rollout;
- SimpleDdgiTransportTailCertificationEnabled for shadow-mode rollout.

Keep SIMPLE_DDGI_TRANSPORT_ABSOLUTE_TAIL_TOLERANCE as a mirrored constant at
0.0001 for the first release.

Update existing settings:

- SimpleDdgiTransportAlbedoClamp becomes the channel-wise ceiling for the sum of
  reflected and transmitted recursive throughput.
- SimpleDdgiTransportSolverRelaxation is fixed per V2 sweep and no longer mixed
  with adaptive temporal hysteresis.

### 9.2 Legacy properties

- Retain SimpleDdgiTransportResidualThreshold as an obsolete serialization alias
  to TailRelativeTolerance for one compatibility cycle. Its numerical value is
  preserved, but diagnostics must label the new stricter tail semantics.
- Retain SimpleDdgiTransportMaximumSolverGenerations only as a legacy
  deserialization property. It must not gate convergence and must not be silently
  reinterpreted as the inner sweep count. Emit a migration diagnostic when a
  non-default authored value is encountered.
- Do not use SimpleDdgiStableMaintenanceUpdateCount or
  SimpleDdgiStableMaintenanceEmaThreshold for V2 retirement. Preserve their V1
  and maintenance-estimator meanings until separately cleaned up.

Reuse the fourth TransportControls component for accelerated sweep count after
the compatibility migration. Rename shader-side
transportMaximumSolverGenerations accordingly without changing the 208-byte
params-header size.

### 9.3 Fingerprints

Split solver fingerprints by effect:

- fixed-point/operator fingerprint: throughput ceiling, transmission mode,
  positive estimator mode, and any gather equation that changes F;
- convergence-policy fingerprint: tail tolerance and certification mode;
- execution-policy fingerprint: relaxation, sweep count, audit chunk budget,
  and acceleration enablement.

Only the first invalidates the current fixed point. Policy changes cancel or
re-evaluate certification as appropriate. Execution-only changes must not
discard a valid source cache or converged field.

## 10. ABI, render graph, and synchronization

### 10.1 Struct and push-constant changes

Update [GPUStructs.cs](../Njulf.Rendering/Data/GPUStructs.cs) and shader mirrors
with:

- solver batch queue offset/count, color, volume phase, sweep flags;
- audit summary and per-probe audit metadata;
- canonical/operator/solve/audit generations;
- audit chunk start/count;
- explicit solver read-atlas and write-atlas indices if a pass cannot use the
  canonical/private defaults.

Keep every struct size and field offset covered by tests. Do not encode ownership
generations as float values.

### 10.2 Pass structure

Refactor the V2 update segment so transport, irradiance blend, and intermediate
canonical publication can alternate inside an ordered solver pass.

Recommended production order:

1. SimpleDdgiTracePass;
2. SimpleDdgiRelocateClassifyPass;
3. SimpleDdgiAcceleratedSolvePass;
4. SimpleDdgiPublishPass for final sampled-mirror publication and transaction
   completion;
5. optional SimpleDdgiTransportAuditPass on audit-only frames;
6. scheduler commit/feedback when GPU-resident scheduling is enabled.

SimpleDdgiAcceleratedSolvePass owns or shares the transport, irradiance-blend,
and canonical-publish pipelines and records the red/black loop with explicit
barriers. Keep the old SimpleDdgiBlendPass for V1 or make its execution strategy
explicit; do not record both V2 blend paths.

### 10.3 Required barriers

Within every color:

- transport scratch write to blend scratch read;
- blend private-atlas write to canonical-publish read;
- canonical publish write to next transport canonical read.

After the final color:

- canonical write to sampled publication read;
- sampled storage-image write to next receiver sampled read;
- probe/audit state write to transfer/readback or scheduler commit;
- compute write to indirect read where GPU scheduler commands are involved.

Extend
[ProductionRenderPipelineDeclaration.cs](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs),
[AsyncComputePassCatalog.cs](../Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs),
and render-graph resource declaration tests. Treat scheduler, trace,
relocate/classify, accelerated solve, final publish, audit, and commit as one
serialized Simple-DDGI family. Recertify async compute after the topology change.

## 11. Implementation phases

### Phase 0: lock the failing baseline and analytic oracles

Tasks:

- Add a deterministic 0.95 and 0.99 white enclosure oracle that records current
  retirement generation, actual missing energy, and proposed tail bound.
- Extend the one-way chain to show that zero local residual does not imply zero
  downstream error.
- Add a thin-sheet reflected-plus-transmitted chain and a chromatic enclosure.
- Capture locked baseline HDR images for Cornell/colored bounce, emissive room,
  long corridor, curtain/thin transmission, and Sponza interior.
- Record primary rays, cached solver iterations, convergence frames, transport
  GPU time, final/raw DDGI luminance, and fallback weight.

Exit criteria:

- At least one deterministic case demonstrates old-policy retirement with actual
  error above the authored tolerance.
- Long-run reference fields and capture identity are stored for A/B comparison.

### Phase 1: establish the contraction contract

Tasks:

- Add SimpleDdgiTransportTailEstimator as a pure CPU reference.
- Implement shared CPU/GPU reflected-plus-transmitted throughput normalization.
- Move V2 to the exact positive irradiance estimator.
- Add operator-norm, lobe-ratio, non-finite, thin transmission, and clamp tests.
- Add diagnostics for renormalized rays and observed q.
- Update solver fingerprints so the source cache remains reusable.

Exit criteria:

- Scalar, chain, chromatic, and thin-sheet CPU operators satisfy the configured
  contraction bound.
- Shader/CPU mirrors agree at boundary values and malformed inputs fail closed.
- Existing scenes do not lose energy except where a previously nonphysical lobe
  sum exceeded the configured ceiling.

### Phase 2: implement tail audit in shadow mode

Tasks:

- Add audit metadata, summary, memory-plan accounting, descriptors, reset, and
  frames-in-flight readback.
- Extract shared transport and positive irradiance projection helpers.
- Implement the cached audit shader and chunked frozen-field controller.
- Publish D, M, q, B, tolerance, generations, coverage, invalid counters, and
  latency.
- Keep old retirement authoritative while recording old-retired/new-would-retire
  disagreement.

Exit criteria:

- Analytic actual error never exceeds the accepted audit bound beyond documented
  FP tolerance.
- No audit dispatch contains a ray query or changes the canonical field.
- Stale/cancelled audit summaries are rejected in all generation tests.
- Audit memory is admitted and stable-frame allocations remain zero.

### Phase 3: switch convergence authority

Tasks:

- Introduce the explicit solve controller phases.
- Remove generation, local residual, stable count, and 95-percent gates from V2
  retirement.
- Add fair complete solve-epoch visitation.
- Replace periodic-generation watchdog logic with time/source-age behavior.
- Preserve local residuals only for prioritization and diagnostics.
- Change tracking state to StaticConverged only for a current accepted
  certificate.

Exit criteria:

- No V2 code path can publish converged without a matching complete certificate.
- Black fields certify without eight artificial generations.
- High-albedo fields remain pending until their total tail is within tolerance.

### Phase 4: add coarse-to-fine red-black acceleration

Tasks:

- Add toroidal logical parity and CPU mirror.
- Build deterministic solver sub-batches.
- Refactor the V2 pass sequence into ordered transport/blend/intermediate-publish
  steps.
- Force solver reads through canonical SSBO during intermediate colors.
- Run visibility/lifecycle work only on the first sweep and sampled publication
  only after the final sweep.
- Add cached sweep count and GPU-time adaptation.
- Add iteration, color, barrier, and no-extra-trace counters.

Exit criteria:

- Final fixed point matches long-run Jacobi within FP16 tolerance.
- Every selected scenario reaches the same tail tolerance in at least 30 percent
  fewer complete solve epochs or 30 percent fewer rendered convergence frames
  than tail-bounded Jacobi.
- Primary ray-query count is bit-identical for identical source-refresh input.
- No intermediate canonical value is consumed through a stale sampled mirror.

If red-black alone misses the acceleration gate, retain the same correctness
path and enable the already designed coarse-to-fine volume phases before
considering a history-bearing semi-iterative solver. Do not weaken the tail
gate to make the acceleration result look successful.

### Phase 5: source refresh, invalidation, and tracking integration

Tasks:

- Compare periodic cache payloads before overwrite.
- Preserve certificates for exactly unchanged validation samples.
- Invalidate on source/operator/layout changes with the table in section 8.
- Cancel audits safely during scroll, teleport, relocation, streaming, material
  edits, sun/sky cohort changes, and local-light changes.
- Integrate Tracking so moving inputs receive bounded work but never claim a
  static certificate.

Exit criteria:

- Static periodic refresh does not create perpetual solve waves.
- Every real change either receives a new certificate or remains explicitly
  pending/capacity-limited.
- Source refresh and dirty-response latency gates do not regress.

### Phase 6: diagnostics, persistence, and documentation

Tasks:

- Replace residual/minimum-generation reason enums with SourceRepair,
  SolveEpochIncomplete, AuditNotStarted, AuditInProgress, TailAboveTolerance,
  QuantizationLimited, InvalidCertificate, Tracking, and Certified.
- Replace residual and solver-generation distributions with defect, tail-bound,
  q, solve-epoch, and audit-latency distributions.
- Update RendererDiagnostics, SceneRenderingData, VulkanRenderer,
  PerformanceSnapshotWriter, benchmark analyzers, and JSON tests.
- Implement settings alias migration and migration diagnostics.
- Update RendererSettingsReference.md, DDGI diagnostics, and runtime validation
  documentation.

Exit criteria:

- A capture can explain exactly why a field is not certified.
- No legacy label describes a generation minimum as a maximum.
- Old settings files load deterministically and new files serialize only the new
  contract.

### Phase 7: production qualification

Tasks:

- Run deterministic unit/shader/integration suites.
- Run three identity-locked ShippingPerformance repetitions with acceleration
  off and on.
- Run High and Ultra tail-quality captures plus Low/Medium exact-estimator
  performance captures.
- Run static, motion, dirty, scroll, teleport, source-cohort, and long-soak
  matrices.
- Recertify graphics-queue correctness first, then async compute separately.
- Record binaries, shader hashes, settings, captures, timings, and final default
  decision in an implementation-status document.

Exit criteria:

- Every gate in sections 13 and 14 passes before enabling the production default.

## 12. Primary file map

### New files

- proposed Njulf.Rendering/Resources/SimpleDdgiTransportTailEstimator.cs
- proposed Njulf.Rendering/Resources/SimpleDdgiTransportSolveController.cs
- proposed Njulf.Rendering/Data/SimpleDdgiTransportTailSummary.cs
- proposed Njulf.Shaders/ddgi_simple_transport_operator.glsl
- proposed Njulf.Shaders/ddgi_simple_transport_audit.comp
- proposed tests in Njulf.Tests/SimpleDdgiTransportTailTests.cs
- proposed tests in Njulf.Tests/SimpleDdgiAcceleratedSolveTests.cs

### Existing files with material changes

- [SimpleDdgiVolumeManager.cs](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- [SimpleDdgiLayoutCompiler.cs](../Njulf.Rendering/Resources/SimpleDdgiLayoutCompiler.cs)
- [SimpleDdgiPasses.cs](../Njulf.Rendering/Pipeline/SimpleDdgiPasses.cs)
- [ProductionRenderPipelineDeclaration.cs](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs)
- [AsyncComputePassCatalog.cs](../Njulf.Rendering/Pipeline/AsyncComputePassCatalog.cs)
- [GPUStructs.cs](../Njulf.Rendering/Data/GPUStructs.cs)
- [RenderSettings.cs](../Njulf.Rendering/Data/RenderSettings.cs)
- [RendererDiagnostics.cs](../Njulf.Rendering/Data/RendererDiagnostics.cs)
- [SceneRenderingData.cs](../Njulf.Rendering/Data/SceneRenderingData.cs)
- [VulkanRenderer.cs](../Njulf.Rendering/VulkanRenderer.cs)
- [ddgi_simple_shared.glsl](../Njulf.Shaders/ddgi_simple_shared.glsl)
- [ddgi_simple_transport.comp](../Njulf.Shaders/ddgi_simple_transport.comp)
- [ddgi_simple_blend.comp](../Njulf.Shaders/ddgi_simple_blend.comp)
- [ddgi_simple_publish.comp](../Njulf.Shaders/ddgi_simple_publish.comp)
- [SimpleDdgiBounceConvergenceTests.cs](../Njulf.Tests/SimpleDdgiBounceConvergenceTests.cs)
- [SimpleDdgiShaderMirrorTests.cs](../Njulf.Tests/SimpleDdgiShaderMirrorTests.cs)
- [GlobalIlluminationDefaultsTests.cs](../Njulf.Tests/GlobalIlluminationDefaultsTests.cs)
- [RendererSettingsReference.md](../RendererSettingsReference.md)

## 13. Verification matrix

### 13.1 Pure math and policy

- White enclosures at q = 0, 0.2, 0.5, 0.8, 0.95, and 0.99.
- Actual analytic error is never greater than B.
- The old 2.5-percent residual counterexample reports a tail above tolerance.
- One-way chains of lengths 2, 20, and 128 do not certify black downstream
  probes early.
- Reflected plus transmitted thin-sheet chains preserve the lobe ratio and
  remain below q.
- RGB/chromatic maxima cannot hide in luminance.
- Near-black absolute tolerance behaves at both sides of 0.0001.
- q approaching one remains finite; q equal to or above one fails closed.
- NaN, positive/negative infinity, negative radiance, and counter overflow fail
  certification.
- Relaxation changes convergence speed but not the fixed point or audit bound.
- Tail tolerance changes re-evaluate/cancel policy without retracing sources.

### 13.2 Solver ordering

- Logical parity is stable across toroidal offsets and adjacent logical cells
  have opposite colors.
- Odd grid dimensions and wrap seams remain deterministic.
- Coarse phases precede dependent fine phases.
- Starting color alternates by solve epoch.
- Every participant is visited once before an epoch completes.
- Duplicate updates do not inflate visitation.
- Generation-mismatched probes are not published or counted as visited.
- RBGS and Jacobi converge to the same quantized field.
- Acceleration gate is measured on enclosure, chain, thin sheet, and scene-scale
  CPU oracles.

### 13.3 Shader and ABI

- GPU struct sizes/offsets and shader fields match.
- Throughput normalization CPU/GPU results match.
- Audit and normal transport use the same recursive helper.
- Audit and V2 blend use the same exact positive estimator.
- Audit shader contains no ray-query instruction or source-light loop.
- Solver shader cannot sample the stale image mirror between colors.
- Intermediate blend cannot update visibility or lifecycle state after sweep
  zero.
- Canonical publication is restricted to the requested solver subrange.
- Final sampled publication sees every intermediate canonical write.

### 13.4 Generation and cancellation

- Source, operator, canonical, volume, queue, audit, and resource mismatch cases
  are independently rejected.
- A cancelled multi-frame audit cannot complete after its epoch is reused.
- Frame and generation wrap behavior is explicit and tested.
- Scroll/recenter, relocation, activation, cache repair, and feature toggle
  cancel correctly.
- Idle frames preserve the last complete certificate without converting missing
  readback into failure.

### 13.5 Runtime scenarios

| Scenario | Required evidence |
|---|---|
| 0.95/0.99 white room | Analytic bounce ratio, actual error below B, no early retirement |
| Cornell/colored room | Chromatic tail retained; no desaturation or missing distant bounce |
| Long corridor/chain | Bounce reaches the final cells before certification |
| Emissive room | Emissive bounce survives all sweeps and matches long-run reference |
| Curtain/thin sheet | Reflected and transmitted energy remains bounded and visible |
| Sponza interior | Dark corners improve without leak/fallback regression |
| Static periodic refresh | Identical payload preserves certificate and bounded maintenance |
| Moving local light | Tracking remains explicit; quiet state eventually certifies |
| Sun/sky cohort | Old audit rejected; source cohort and new tail certificate are generation-correct |
| Camera scroll/teleport | No stale slot, parity, atlas, or certificate reuse |
| Relocation/classification | Operator invalidation is atomic with publication |
| Long static soak | Cached solver retires, audit work stops, memory/counters remain bounded |

## 14. Acceptance gates

### 14.1 Correctness and quality

- Reported B is never below measured analytic error outside documented FP
  tolerance.
- StaticConverged is impossible without one current complete certificate.
- Active/source-ready audit coverage is 100 percent; invalid count is zero.
- White enclosure final bounce/source ratio is within 2 percent of analytic
  reference for q through 0.95 and within 3 percent at 0.99.
- Long-chain final probe is within 2 percent of the long-run reference.
- No regression in thin-wall leak, emissive bounce, colored bounce, fallback
  ownership, relocation, or scroll quality gates.
- No non-finite canonical atlas or tail summary value.

### 14.2 Acceleration

- At least 30 percent fewer complete solve epochs or convergence frames than a
  tail-bounded Jacobi control on every selected high-albedo oracle.
- No selected oracle or runtime scene converges slower by more than 5 percent
  without a documented GPU-budget throttle.
- Final certified field matches the tail-bounded Jacobi reference within FP16
  storage tolerance.

### 14.3 Ray and work budgets

- Identical source input produces identical primary trace probe/ray counts with
  acceleration on and off.
- Additional ray queries, shadow rays, and emissive evaluations: zero.
- Cached solver and audit iterations are separately counted and bounded.
- Request queue, scratch, dispatch, and counter capacities never overflow.

### 14.4 Performance

- Total GI GPU P95 remains at or below the existing 2.50 ms production target.
- Audit is chunked when necessary and does not create a frame-time spike above
  the accepted profile budget.
- Stable Certified frames issue zero solver and audit dispatches except bounded
  source validation.
- CPU stable scheduling/upload work does not regress; after GPU-resident
  scheduling it remains O(volume count) or O(1).

### 14.5 Memory

- No additional full irradiance atlas or full-field history is allocated.
- New convergence metadata is at most 16 bytes per maximum probe.
- Summary readback is at most 1 KiB per frame in flight.
- All bytes are included in SimpleDdgiMemoryPlan and the selected tier's hard
  admission.
- Capacity transition, resize, disable/re-enable, and retirement remain bounded
  and leak-free.

## 15. Coordination with active renderer plans

The GPU-resident scheduling plan currently declares convergence criteria a
non-goal while also proposing GPU fields for solver generations, stable counts,
and residual envelopes. This feature must land, or its ABI must be merged,
before that scheduler layout is frozen.

Update
[SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md](SimpleDdgiGpuResidentSchedulingImplementationPlan-20260803.md)
during implementation so its scheduler frame, per-probe state, outcome record,
commit stages, feedback summary, tests, and quality gates use:

- solve/audit epochs;
- source/operator/canonical generations;
- complete sweep visitation;
- tail audit summary;
- certified/pending state; and
- cached-sweep/audit indirect commands.

Do not first migrate minimum-generation/residual retirement to the GPU and then
perform a second production state migration.

The sun/sky/async reflection work also introduces source-cohort and completion
generations. Use those exact immutable source versions in the audit summary.
After adding intermediate canonical writes and the audit pass, repeat the full
Simple-DDGI async resource-usage and timeline-serialization audit.

## 16. Rollout and fallback

Use four modes during development:

1. LegacyControl: current Jacobi and current retirement, diagnostics only.
2. TailShadow: current rendering/retirement plus non-authoritative tail audits.
3. TailJacobi: tail certification is authoritative; solver remains Jacobi.
4. TailAccelerated: tail certification plus coarse-to-fine red-black solve.

Promotion order is exactly the order above. TailJacobi proves the convergence
contract independently from the acceleration. TailAccelerated proves speed
without changing the accepted error.

Safe runtime fallback rules:

- If acceleration setup is unavailable, fall back to TailJacobi.
- If audit setup or evidence is invalid, remain pending and keep the last field;
  do not fall back to legacy heuristic retirement.
- If V2 resources are unavailable, use the existing explicit V1/feature fallback
  policy and report it.
- GPU-time pressure may reduce cached sweep count or audit chunk size; it may not
  raise tolerance, lower coverage, or add primary rays.

## 17. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Reflected plus transmitted lobes violate contraction | Enforce their channel-wise sum at evaluation and test the CPU/GPU mirror |
| Reduced SH path has signed amplification | Use exact positive estimator for certified V2; require a separate proof for any replacement |
| Local residual is zero before distant bounce arrives | Only a complete field norm authorizes retirement |
| Red-black graph is not perfectly bipartite because gathers are nonlocal | Correctness comes from the audit; require measured acceleration and add coarse-to-fine ordering |
| Intermediate canonical writes read a stale sampled mirror | Force solver SSBO reads and publish the image mirror only at the end |
| Multi-frame audit observes a changing field | Freeze canonical/operator generations and cancel on any change |
| Freeze adds visible response latency | Enter audit only after a complete solve epoch; cancel immediately for real dirty work |
| q near one makes the bound very conservative | Tighten with complete observed throughput, run bounded cached sweeps, never weaken the bound |
| FP16 quantization prevents a strict tolerance | Measure FP32-to-unpacked-FP16 defect and report QuantizationLimited |
| More dispatches/barriers erase solver gains | Measure per-stage timing, skip empty phases, group proven-independent volumes, keep 1-sweep throttle |
| New state conflicts with GPU scheduler migration | Merge this ABI before scheduler freeze |
| Routine source refresh causes perpetual invalidation | Compare cache payloads and preserve certification only for exactly unchanged validation |

## 18. Definition of done

The feature is complete only when:

- Transport V2 contains no generation-count or local-residual retirement path.
- The recursive operator enforces a documented q below one.
- Every convergence claim references a complete, immutable tail-audit summary.
- High-albedo analytic and runtime cases retain their visible energy tail.
- Coarse-to-fine red-black ordering meets the acceleration gate.
- No additional primary or shadow ray is traced.
- V1, source refresh, relocation, classification, scrolling, sampled publication,
  and async ownership remain correct.
- Memory and GPU-time gates pass in three locked production repetitions.
- Settings migration and diagnostics make legacy and new semantics unambiguous.
- The GPU-resident scheduler plan consumes the new state contract without a
  temporary residual/generation ABI.
- An implementation-status document links commits, tests, shader hashes,
  captures, timings, memory evidence, remaining limitations, and the final
  default decision.

## 19. Explicit non-shortcuts

- Do not rename the current residual as a tail estimate without multiplying by a
  proven contraction factor.
- Do not use a per-probe residual to certify a field with unresolved propagation.
- Do not keep the eight-generation minimum as a hidden secondary gate.
- Do not accept 95-percent population as an error certificate.
- Do not estimate q only from recent residual ratios.
- Do not assume reflected and transmitted lobes are energy-conserving without
  enforcing their sum.
- Do not certify the signed reduced SH operator without a norm proof.
- Do not update the sampled atlas after every color merely to avoid fixing solver
  reads.
- Do not count cached transport directions as new source rays, and do not hide
  extra ray queries under a solver counter.
- Do not let GPU pressure weaken tolerance, coverage, generation validation, or
  fail-closed behavior.
- Do not freeze the GPU scheduler ABI around fields this plan removes.
