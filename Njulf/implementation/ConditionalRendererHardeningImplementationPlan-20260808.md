# Conditional Renderer Hardening Implementation Plan

- Date: 2026-08-08
- Status: proposed; this document does not implement the rendering changes
- Scope: conditional directional-shadow and Simple-DDGI correctness hardening
- Explicitly excluded: diagnosis or remediation of the Sponza dark-wall/decal issue
- Related plans:
  - [HighQualityDirectionalShadowsProductionImplementationPlan-20260807.md](HighQualityDirectionalShadowsProductionImplementationPlan-20260807.md)
  - [ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md](ContentDependentDdgiAdditionsProductionImplementationPlan-20260805.md)
  - [FurtherGlobalIlluminationImprovementsRoadmap-20260807.md](FurtherGlobalIlluminationImprovementsRoadmap-20260807.md)

## 1. Decision

Implement this work as independently promotable, evidence-gated tracks. Diagnostics
and regression coverage are unconditional. Rendering behavior changes only after
their stated confirmation gates pass.

| Track | Confirmation gate | Intended result |
| --- | --- | --- |
| Directional-shadow culling | An exact eligible caster is rejected by the GPU while an independent CPU clip test accepts the same sphere and matrix | Correct missing casters and cascade discontinuities |
| Static shadow-cache validity | A layer from the wrong signature or resource generation is demonstrably reused | Prevent stale depth during invalidation and resource transitions |
| Simple-DDGI scheduler liveness | Eligible work and budget exist, but no work commits within bounded pipeline latency | Restore probe convergence and sparse-residency progress |
| Simple-DDGI local confidence | Valid published probes are locally radiometrically immature despite a functioning scheduler | Provide graceful startup and recenter degradation |
| Tests and diagnostics | Unconditional | Make every behavioral gate observable and reproducible |

The required implementation order is:

1. independent diagnostics and deterministic fixtures;
2. directional-shadow culling, if confirmed;
3. static shadow-cache validity, if confirmed;
4. Simple-DDGI scheduler and residency liveness, if confirmed;
5. local radiometric confidence and fallback, if justified after liveness is
   healthy;
6. cross-track qualification and independent promotion.

Do not implement fail-bright behavior from per-frame caster counts. Do not use a
fallback to conceal a producer that has stopped making progress.

## 2. Scope and invariants

### 2.1 In scope

- Independent CPU/GPU directional-shadow containment validation.
- Exact caster attribution and rejection-reason diagnostics.
- Per-cascade static-cache validity and working-map provenance.
- Generation-aligned Simple-DDGI scheduling, residency, publication, and
  convergence telemetry.
- A bounded liveness watchdog based on eligible work and actual progress.
- Optional local radiometric-confidence data for receiver composition.
- Unit, shader-contract, integration, visual, and performance qualification.

### 2.2 Out of scope

- The Sponza dark-wall/decal problem.
- Replacing cascaded shadow maps with another shadow backend.
- Changing cascade count, split policy, or quality presets.
- Treating all empty-looking cascades as invalid.
- Admitting intentionally suppressed empty DDGI pages.
- Deriving local receiver confidence from a ring-wide convergence percentage.
- Weakening the qualified Simple-DDGI convergence target.

### 2.3 Required invariants

- Caster rendering, caster culling, and receiver sampling use the same cascade
  matrix.
- A receiver is never assumed to be a caster without proving that its exact
  meshlet belongs to an eligible caster stream.
- Cascades are treated as adjacent fitted frustum slices, not nested volumes.
- A correctly cleared empty shadow layer is valid and naturally resolves lit.
- Static-cache validity is never inferred from current-frame emitted counts.
- Every DDGI counter used in a liveness decision belongs to an explicitly
  compatible frame serial and resource generation.
- A scheduler watchdog requires eligible work, budget, and elapsed pipeline
  latency; global convergence pending alone is insufficient.
- Leak attenuation is not replenished by environment fallback.
- Fully converged steady-state rendering remains unchanged within numerical
  tolerance.

## 3. Phase 1 — Independent diagnostics and deterministic fixtures

This phase changes no lighting or visibility policy.

### 3.1 Directional-shadow CPU reference

Add an independent CPU sphere/clip reference using the direct row-vector Vulkan
clip inequalities:

    -w <= x <= w
    -w <= y <= w
     0 <= z <= w

For a sphere, test conservative distance to each homogeneous clip boundary after
accounting for the transformed radius. Do not implement the CPU oracle by copying
the shader's extracted-plane function; that could reproduce the same error.

For every deterministic fixture, retain:

- the exact GPUShadowData matrix bytes;
- camera and light inputs;
- cascade near/far distances;
- fitted light-space extents;
- world-space sphere centre and radius;
- direct clip coordinates;
- signed distances to the six normalized planes;
- independent CPU acceptance.

Primary implementation areas:

- [DirectionalShadowDataBuilder.cs](../Njulf.Rendering/Data/DirectionalShadowDataBuilder.cs)
- [common.glsl](../Njulf.Shaders/common.glsl)
- [DirectionalShadowDataBuilderTests.cs](../Njulf.Tests/DirectionalShadowDataBuilderTests.cs)

### 3.2 Exact GPU caster attribution

Extend the existing bounded directional-shadow diagnostic channel rather than
adding an unbounded readback. For selected candidates, record:

- object ID, instance ID, meshlet ID, and selected LOD;
- caster class: static, dynamic, or foliage;
- world-space centre and conservative radius;
- cascade index;
- cascade matrix hash or generation;
- acceptance or rejection;
- first rejecting plane and signed distance;
- candidate-list eligibility flags.

Sampling must be deterministic and bounded. The diagnostics must be able to prove
that the CPU and GPU compared the same candidate, bounds, matrix, cascade, and
frame generation.

Primary implementation areas:

- [scene_opaque_compact.comp](../Njulf.Shaders/scene_opaque_compact.comp)
- [shadow_depth.task](../Njulf.Shaders/shadow_depth.task)
- [RendererDiagnosticsBuffer.cs](../Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs)
- DirectionalShadowRuntimeDiagnostics.cs

### 3.3 Per-cascade working-map provenance

Expose a per-cascade provenance record containing:

- active cascade bit;
- cache signature and resource generation;
- cache state;
- copied-from-cache bit;
- refreshed-this-frame bit;
- explicitly-cleared bit;
- dynamic work appended bit;
- foliage work appended bit;
- final working-layer validity;
- command/submission serial.

This is validity evidence, not a caster-count replacement.

### 3.4 Generation-aligned DDGI telemetry

Every scheduler, residency, publication, and convergence snapshot used together
must expose:

- frame serial;
- volume-table generation;
- scheduler-arena generation;
- residency-arena generation;
- source-lighting generation;
- transport generation;
- feedback validity and rejection reason.

Add stage counters for:

- eligible probes by scheduler class and ring;
- eligibility rejection reasons;
- selected requests;
- indirect dispatch requests;
- committed updates;
- blended updates;
- coherent publications;
- admission candidates;
- visible-demand pages that are suppressed;
- visible-demand pages that are initializing or unpublished;
- transaction abort reasons;
- source-cache invalidation deltas grouped by reason.

The cumulative source-cache invalidation total remains useful, but liveness
diagnosis must use reason-coded deltas.

### 3.5 Phase 1 completion gate

Phase 1 is complete when one deterministic capture can explain:

- why a selected shadow candidate was accepted or rejected;
- exactly where every working shadow layer came from;
- whether DDGI had eligible work;
- where selected DDGI work stopped progressing;
- whether all compared telemetry belongs to compatible frame and resource
  generations.

## 4. Phase 2 — Conditional directional-shadow culling correction

### 4.1 Confirmation gate

Proceed only after finding the same eligible shadow-casting meshlet where:

1. CPU and GPU use identical matrix bytes;
2. CPU and GPU use identical world-space sphere bounds;
3. the independent CPU direct-clip test accepts the sphere;
4. the GPU rejects it.

A receiver fragment, an aggregate receiver count, or a scene-wide emitted count
does not satisfy this gate.

If the gate is not met, close this track without changing culling behavior.

### 4.2 Investigation order

Investigate in this order:

1. Candidate eligibility and stream classification.
2. Object/meshlet identity and LOD selection.
3. World-centre transformation.
4. Maximum-scale and conservative-radius calculation.
5. Matrix upload layout and row-vector multiplication.
6. Direct clip inequalities versus extracted planes.
7. Fitted frustum-corner containment.
8. Texel snapping, XY crop, and caster depth padding.

This order prevents a candidate-list or bounds defect from being misdiagnosed as
a matrix defect.

### 4.3 Fix policy

- Fix the earliest incorrect transformation, bound, or test.
- Preserve one canonical cascade matrix for culling, rasterization, and receiver
  sampling.
- Keep the sphere test conservative at floating-point boundaries.
- Share corrected helpers between compaction and task-shader paths where their
  input contracts are identical.
- Do not add a second caster-only cascade fit.
- Do not widen every cascade merely to hide a false rejection.

### 4.4 Regression tests

Extend DirectionalShadowDataBuilderTests with:

- every fitted camera-slice corner projects inside its own cascade;
- randomized equivalence between direct clip inequalities and plane extraction;
- spheres touching each clip plane remain accepted;
- spheres clearly beyond each plane are rejected;
- non-uniform instance scale produces a conservative world radius;
- captured exact meshlet regressions;
- finite results for extreme but supported camera and light orientations.

Do not add cross-cascade monotonicity. A sphere accepted by cascade i need not be
accepted by cascade i+1 because their fitted volumes are not nested.

### 4.5 Qualification

Use deterministic synthetic scenes containing:

- one caster and receiver per cascade;
- casters intersecting each cascade edge;
- off-screen casters that can shadow visible receivers;
- static, dynamic, and mixed caster classes;
- non-uniformly scaled instances;
- smooth camera motion through every split transition.

Success requires CPU/GPU decision agreement for all retained fixtures, no new
missing casters, and no material increase in shadow draw cost beyond the work
required by corrected conservative visibility.

## 5. Phase 3 — Conditional static shadow-cache hardening

### 5.1 Confirmation gate

Change cache behavior only if a capture proves that a layer is copied while any
of these differ from the data under which that layer was produced:

- cache signature;
- GPUShadowData matrix;
- scene-content revision;
- draw-command signature;
- map size or cascade count;
- shadow-resource generation.

If no invalid reuse is reproduced, retain current behavior and land only the
tests and provenance diagnostics.

### 5.2 Per-cascade cache state

Track per cascade:

- resource generation;
- complete cache signature;
- matrix hash;
- scene-content revision;
- state: Invalid, RefreshRecorded, or Valid;
- refresh command/submission serial;
- completion fence where durable cross-frame reuse requires it;
- last working-map provenance.

An empty layer that was cleared and rendered under the current signature is
Valid. Empty and Invalid are separate states.

### 5.3 State transitions

- Invalidate before a changed camera matrix, map size, cascade count, scene
  revision, draw signature, or resource generation can be observed by a copy.
- Refresh every invalid active layer with reverse-Z clear semantics before
  rendering static work.
- Promote a layer only after its refresh commands are successfully recorded and
  correctly ordered.
- Tie durable cross-frame reuse to the owning submission/fence contract.
- On command-recording failure, resource replacement, or aborted refresh, leave
  the layer Invalid.

### 5.4 Working-map composition

Compose each layer as follows:

1. Valid current cache: copy it.
2. Invalid or unavailable cache: explicitly clear the working layer.
3. Append dynamic casters.
4. Append foliage casters.
5. Transition the final working layer for sampling.

Never:

- decide cache validity from current-frame static or dynamic emitted counts;
- clear a valid reused static layer because current-frame emission is zero;
- feed delayed CPU diagnostic counts back into same-frame shadow sampling;
- treat a valid clear-only layer as unusable.

If a shader-visible working-layer validity mask is added, it represents resource
and generation validity only. It must not represent whether the layer contains a
caster.

### 5.5 Cache tests

Cover:

- all-static, all-dynamic, mixed, and empty scenes;
- stationary cache reuse;
- camera translation and rotation;
- scene-content revision;
- static transform/material changes;
- cascade-count and shadow-map-size changes;
- shadows toggled off and on;
- shadow-resource recreation;
- multiple frames in flight;
- aborted refresh or command-recording failure;
- a valid empty cascade;
- a valid populated cascade with zero current-frame emission.

The required failure mode for an invalid layer is explicit reverse-Z clear, never
stale depth.

## 6. Phase 4 — Conditional Simple-DDGI scheduler and residency liveness

### 6.1 Stall confirmation gate

A scheduler or residency stall exists only when aligned telemetry shows:

- global or local convergence is pending;
- at least one eligible probe or admission candidate exists;
- effective budget is greater than zero;
- no declared reconfiguration, publication, audit, generation-transition, or
  feedback barrier explains the idle state;
- committed or published progress remains zero beyond the maximum expected
  feedback latency.

Do not require ScheduledRequestCount to be non-zero on every individual frame.

### 6.2 Stage classification

Classify the first failed stage:

1. Demand exists, but no admission candidate.
2. Admission candidate and free page exist, but no admission is selected.
3. A resident eligible probe exists, but the scheduler does not select it.
4. A request is selected, but no dispatch is committed.
5. Dispatch commits, but blend/publication does not advance.
6. Publication advances, but convergence evidence is rejected or does not
   advance.

For each class, expose a first-cause reason rather than inferring from unrelated
aggregate counters.

### 6.3 Investigation areas

Inspect the complete path, including:

- [SimpleDdgiVolumeManager.cs](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs)
- SimpleDdgiGpuScheduler.cs
- SimpleDdgiSchedulePass.cs
- SimpleDdgiPageResidencyPass.cs
- [ddgi_simple_page_classify.comp](../Njulf.Shaders/ddgi_simple_page_classify.comp)
- [ddgi_simple_page_feedback.comp](../Njulf.Shaders/ddgi_simple_page_feedback.comp)
- scheduler selection, materialization, commit, and audit shaders

Apply the fix only at the first stage that demonstrably loses eligible work.

### 6.4 Suppressed-page policy

When demanded pages are suppressed:

- verify that the suppression came from valid geometry-empty evidence;
- keep intentionally empty pages suppressed;
- report suppression as the explicit reason that no admission occurred;
- if suppression can become stale without a geometry-generation change, schedule
  a bounded lightweight reclassification retry rather than immediate admission;
- use the configured inactive-retry policy only after its semantics are defined
  and covered by tests.

Visible demand alone must not automatically override confirmed-empty
classification.

### 6.5 Liveness watchdog

Add a diagnostic watchdog over a bounded window derived from:

- frames in flight;
- scheduler feedback latency;
- residency feedback latency;
- publication/readback latency.

The watchdog requires:

- unchanged compatible generations;
- pending work;
- eligible work;
- available budget;
- no declared barrier;
- zero committed and publication progress for longer than the derived bound.

Report:

- the first stalled stage;
- elapsed frames;
- eligible and selected counts;
- relevant budget;
- generation/serial tuple;
- blocking or rejection reason.

The watchdog is diagnostic by default. Promote it to a test failure only in
deterministic qualification scenarios.

### 6.6 Scheduler and residency tests

Add deterministic tests for:

- stationary convergence after recenter;
- visible missing pages with free capacity;
- intentionally suppressed empty pages;
- stale suppression reclassification, if that policy is enabled;
- initializing and unpublished demanded pages;
- source-generation changes;
- volume/resource-generation rejection;
- zero eligible work;
- available work with a zero budget;
- transaction abort and retry;
- tail audit/certification frames;
- convergence completion after all work publishes.

Success requires bounded progress whenever the full liveness predicate is true,
with no false watchdogs during legitimate idle phases.

## 7. Phase 5 — Conditional local radiometric confidence

This phase begins only after scheduler and residency liveness are healthy. Keep
the confidence feature disabled while diagnosing producer progress.

### 7.1 Confirmation gate

Proceed only after a generic startup or recenter scenario demonstrates:

- the relevant probes are published and accepted by validSupport;
- local irradiance remains radiometrically immature;
- the scheduler continues to commit and publish work normally;
- the transient produces a material quality failure;
- the failure is not caused by invalid, nonresident, suppressed, or unpublished
  data already represented by validSupport.

### 7.2 Confidence contract

Keep these values distinct:

- spatialCoverage: the field geometrically covers the receiver;
- validSupport: usable and coherently published probe data is available;
- radiometricConfidence: that local data has sufficient source and solver
  maturity.

Derive confidence per probe from generation-coherent evidence such as:

- current source generation;
- minimum completed solver generation;
- normalized residual or convergence classification;
- publication age;
- explicit fresh/recenter state.

Do not derive a receiver's confidence from a ring-wide converged-probe fraction.

### 7.3 Receiver-visible representation

Store a compact confidence value or state in a generation-coherent
receiver-visible record. Prefer an existing reserved field only if its ABI and
lifetime are formally compatible; otherwise extend the receiver record and its
layout tests.

Requirements:

- publication of irradiance and confidence is atomic from the receiver's
  perspective;
- stale confidence cannot accompany new irradiance or vice versa;
- confidence resets on recenter and relevant source-generation changes;
- production receiver shaders do not add ray queries or a second gather.

### 7.4 Cross-probe and cross-ring blending

Blend confidence with the same representation weights used by the irradiance
estimate:

- trilinear probe weights inside a ring;
- availability weights across rings;
- the same primary/secondary volume ownership decisions.

The result is local represented confidence, not an independent global mask.
Apply bounded temporal hysteresis to prevent brightness pumping as probes cross
the confidence threshold.

### 7.5 Composition

The intended composition contract is:

    authority = spatial coverage * availability ramp * local confidence
    DDGI contribution = DDGI irradiance * authority * leak attenuation
    environment contribution = environment irradiance * (1 - authority)

The configured environment-fallback intensity remains part of the environment
term. Leak attenuation is not complemented because blocked transport is not
missing field coverage.

Do not claim energy is unchanged when switching between two different irradiance
estimates. Instead, require bounded output, stable transitions, and qualified
interior leakage.

### 7.6 Leakage controls

- Retain far-field sky visibility on the environment component.
- Keep a minimum confidence/authority floor for coherent published data if needed
  to prevent repeated IBL reopening.
- Bound temporal confidence change per frame.
- Add an explicit rollback flag.
- Expose spatial coverage, valid support, local confidence, final authority, and
  fallback weight as separate debug channels.

### 7.7 Confidence qualification

Use:

- an open outdoor scene;
- a fully enclosed box;
- a thin-wall interior;
- initial startup;
- ring recenter;
- a rapidly changing directional light;
- ring boundaries;
- a fully converged steady scene.

Required outcomes:

- steady-state output matches the existing path within numerical tolerance;
- confidence drops only where local evidence is immature;
- no unacceptable increase in enclosed-scene sky leakage;
- no visible lattice, ring seam, or temporal pumping;
- receiver cost remains within the active GPU and bandwidth budget.

## 8. Phase 6 — Integration and promotion

### 8.1 Commit and feature boundaries

Keep these changes independently reviewable:

1. diagnostics and CPU reference;
2. confirmed shadow-culling correction;
3. confirmed cache-validity correction;
4. scheduler/residency liveness correction;
5. local-confidence producer and ABI;
6. local-confidence receiver composition;
7. qualification fixtures and evidence.

Use feature flags for behavioral changes that need runtime A/B qualification.
Diagnostics and tests should remain available when a behavior flag is disabled.

### 8.2 Verification matrix

For each promoted track:

- run shader compilation and validation;
- run dotnet test Njulf/Njulf.Tests;
- execute deterministic non-dark-wall scenarios;
- capture performance JSON and locked screenshots;
- compare correctness, generation alignment, and progress telemetry;
- measure GPU time, CPU time, memory, readback bandwidth, and shader/record size;
- run with validation layers;
- test multiple frames in flight.

### 8.3 Promotion criteria

Promote a track only when:

- its confirmation gate has reproducible evidence;
- the fix addresses the first demonstrated failure stage;
- all new invariants are covered by tests;
- rollback restores the previous behavior cleanly;
- unrelated tracks are not required to hide regressions;
- performance remains within the active budget;
- the dark-wall scenario is not used as its acceptance criterion.

Failure of one conditional gate does not block the diagnostics or another
independent track.

## 9. Expected broader effects

If confirmed and implemented:

- A shadow-culling correction improves missing casters and cascade transitions in
  every scene using the affected bounds or matrix path.
- Cache hardening prevents stale-depth failures during camera, scene, settings,
  and resource transitions while preserving valid static reuse.
- Scheduler liveness repair improves recovery after recenter, sparse-page
  admission, publication, and eventual transport convergence.
- Local confidence provides bounded visual degradation during legitimate
  radiometric warmup without lowering the final convergence target.
- The diagnostic and test foundation makes future failures attributable to a
  specific stage instead of aggregate-counter inference.

These effects remain conditional until their corresponding gates pass.
