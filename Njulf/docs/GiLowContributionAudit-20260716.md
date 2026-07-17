# GI low-contribution audit and remediation report

Date: 2026-07-16  
Branch: `Simplified-SDF`  
Audited HEAD: `6c9acb9` (`NewW`)  
Supplied captures: commit `a9c1f90` (`Dark wall issue fixed`)

The remediation described below is uncommitted working-tree state on top of
`6c9acb9`; it is not part of that commit. Post-fix captures consequently still
report `6c9acb9` as their source commit and are distinguished by their shader
hashes and capture-contract fingerprints.

## Executive verdict

The low apparent global-illumination contribution was not caused by a single
gain, a broken irradiance atlas, or the Lambertian `1 / PI` term. It came from
three transport defects and several diagnostic defects that made the result
look both darker and less trustworthy than it was:

1. The simple-DDGI relocation pass could move a probe after tracing but before
   blending. Rays traced at the old world position were then published and
   sampled as if they belonged to the new position. This was the primary
   correctness defect.
2. Maintenance updates reused a fixed 97% history weight even when a probe had
   waited many frames. With thousands of probes and a bounded update queue,
   stale, under-lit transport converged too slowly in wall-clock time.
3. `CastsShadows` and `ShadowStrength` existed on authored lights but were not
   carried into DDGI hit shading. The Sponza sun requested 85% shadow strength,
   yet DDGI applied a full binary shadow and removed the intended 15% occluded
   direct-light floor.
4. The production gate, window-capture order, coverage oracle, and the metric
   named `DataConfidence` each gave misleading evidence. In particular, window
   screenshots were one debug output behind the renderer captures.

The receiver stage is behaving as a diffuse BRDF should. In the supplied data,
mean sampled irradiance was about `0.16323` and mean final diffuse GI was about
`0.01124`. Their ratio, `6.89%`, implies an average effective diffuse
reflectance of about `0.216` through `irradiance * albedo * (1 - metallic) / PI`.
Removing `1 / PI` or applying an arbitrary boost would hide the transport defect
and make energy accounting incorrect.

After the fixes, the deterministic low-camera stress endpoint increased final
diffuse GI from `0.002009` to `0.007200` (3.58x). Its traced radiance increased
60.5% and blended probe radiance increased 48.4%. The high endpoint gained
12.6% final diffuse GI. Coverage, ownership, support, and fallback diagnostics
remain healthy. These measurements demonstrate materially stronger transport;
they are not a substitute for an approved visual reference image.

## Scope and evidence

The audit covered:

- the simple-DDGI trace, relocation/classification, blend, scheduling, and
  forward-gather pipeline;
- CPU/GPU light packing and raster/DDGI shadow semantics;
- clipmap placement and the receiver-coverage oracle;
- the production gate and deterministic Sponza capture harness;
- the five supplied performance JSON files and five supplied images;
- fresh pre-fix, post-fix detailed, and post-fix production captures; and
- the repository structure, build reproducibility, tests, and operational
  documentation around this path.

The supplied JSON files were schema 3, Debug/Standard, `1600x900`, `DdgiHigh`,
and `DetailedInvestigation` captures from an RTX 3060 Laptop GPU. They came from
the previous commit, not the audited HEAD. The supplied JSONs report a camera
near `(-6.98, 7.01, 5.15)`, but image-to-JSON pairing is not encoded. The
current locked harness endpoints are different, so the before/after runs below
are controlled stress endpoints rather than an exact pixel reproduction of the
attachments.

During the audit, concurrent uncommitted backend-ownership changes appeared in
`DdgiProbeVolumeManager.cs`, `SimpleDdgiVolumeManager.cs`,
`VulkanRenderer.cs`, and `SimpleDdgiShaderMirrorTests.cs`. They were preserved.
This report attributes only the changes described in the remediation section
to this audit.

## Codebase state

The repository is a substantial renderer rather than a small sample:

| Property | Observed state |
| --- | ---: |
| .NET projects | 9, all targeting `net10.0` |
| Source files | 539 |
| Approximate source lines | 191,500 |
| `Njulf.Rendering` C# lines | approximately 101,000 |
| NUnit test attributes | more than 1,000 |
| GI-related test attributes | 382 |
| Markdown files | 110 |
| Operational documents under `docs/` before this report | 4 |

The main maintenance hotspots are `VulkanRenderer.cs` (about 11.1k lines),
`RenderSettings.cs` (about 5k), `SimpleDdgiVolumeManager.cs` (about 4.5k),
`forward.frag` (about 4.2k), and the legacy `DdgiProbeVolumeManager.cs` (about
4.1k).

Simple DDGI is the Sponza production backend, but the renderer still constructs
and partially prepares both the simple and legacy DDGI implementations. That
leaves duplicate ownership, resource-layout, diagnostics, and lifecycle paths
inside already-large orchestration files. The concurrent backend-ownership
work improves disable/switch behavior, but the architectural duplication
remains.

Test quantity is strong, especially around GI. Test quality is mixed: many
tests validate source strings or private method text rather than behavior. The
six baseline failures are examples of those contracts drifting behind real
reflection-probe, mesh-LOD, and forward-depth changes.

Reproducibility is weaker than the test volume suggests. The repository has no
`global.json`, package lock, centralized package versions, analyzer policy, or
`.editorconfig`, and the external `glslangValidator` executable is not pinned.
CI does not explicitly install it. A machine with a different SDK or shader
compiler can therefore produce a different result despite identical sources.

## What the supplied diagnostics actually show

Aggregating only the five supplied JSON files gives the following picture:

| Signal | Supplied range or mean | Interpretation |
| --- | ---: | --- |
| Sampled irradiance mean | `0.16202`-`0.16378`; mean `0.16323` | The gather is returning non-zero lighting. |
| Raw receiver diffuse mean | mean `0.01124` | Expected after material and Lambertian response. |
| Final diffuse mean | equal to raw, mean `0.01124` | Composition is not suppressing the raw DDGI term. |
| Final / irradiance | `6.89%` | Consistent with effective diffuse reflectance near `0.216`. |
| Probe support | mean `0.98749` | Good interpolation support. |
| Ownership/effective weight | `1.0` | DDGI owns the indirect result where measured. |
| Fallback | `0` | No fallback dilution. |
| Visibility | about `0.98` | Visibility weighting is not collapsing the result. |
| Zero/out-of-grid/clamped/non-finite samples | `0` | No coverage or numerical failure in the sampled pixels. |
| Final samples exactly zero | 4 of 5,700 | Negligible zero-output population. |
| Trace hit rate | about `60%`-`70%` | TLAS and ray traversal are functioning. |
| Direct-lit trace hits | about `16%`-`29%` | Direct source energy was relatively sparse. |
| Emissive trace hits | about `2%`-`3.6%` | Emissive transport is present but small. |
| Probe updates | `2,048` per frame | The configured bounded update budget is active. |
| DDGI update time | mean `6.008 ms` | Above the supplied capture's `2.25`/`2.5 ms` target range. |
| CPU GI P95 | mean `21.413 ms` | Expensive, but captured in Debug with detailed counters. |

The old capture reported roughly 4,125 relocated probes. Its `0.922` relocation
value is the mean relocation magnitude divided by maximum magnitude among
relocated probes; it is not the fraction of all probes that moved. About 1,945
of 15,368 probes were inactive, with most inactive probes in the near ring. The
coarse ring frequently supplied the missing coverage, and near-ring update-age
P95 was roughly 72-82 frames. That combination is consistent with spatially
mis-published and slow-converging transport, not absent atlas coverage.

The old trace/blend energy counters were zero because that instrumentation was
not present in the supplied revision. Those zeros contain no transport-energy
information.

The images' perceived darkness is also amplified by tone mapping. An HDR value
near `0.01126` maps through the current ACES curve to roughly `0.00446` linear,
or about `0.056` in sRGB. Physically plausible low indirect energy can therefore
look almost black even when the gather is operating.

## Pipeline and causal chain

The active path is:

`authored lights -> GPU light buffer -> ray trace/hit shading -> relocation and classification -> irradiance/depth blend -> clipmap gather -> diffuse BRDF -> tone mapping`

The low result accumulated at the source and history stages:

1. A probe was traced using relocation offset A.
2. Relocation/classification found an exit surface and committed offset B.
3. Blend stored rays from A into the atlas while probe state already advertised
   B.
4. Forward gather treated that atlas data as spatially valid at B.
5. Maintenance scheduling revisited a large near-ring population slowly, while
   fixed 97% history retention preserved the old dark value.
6. DDGI hit shading applied full binary shadows even when the authored sun
   requested partial shadow strength.
7. The diffuse receiver correctly reduced the surviving irradiance by material
   reflectance and `PI`, after which ACES made the remaining low HDR value look
   nearly black.

This explains why the atlas, support, ownership, and visibility counters could
all be healthy while the observed indirect term stayed weak.

## Ranked root causes

### P0: relocation was not an atomic trace-to-publish transaction

Trace consumed the previous relocation offset, relocation committed a new
offset, and blend published the trace results in the same frame. A material
movement can be as large as 45% of a cell. With thousands of relocated probes,
this allowed rays from inside or near geometry to be labeled as irradiance at a
different world position.

The relocation direction and logical-grid addressing were correct. Reversing
the sign would have introduced a new defect. The missing invariant was: a probe
must not become gatherable at a new position until it has been retraced there.

### P1: fixed per-update hysteresis ignored wall-clock age

Maintenance probes used a base hysteresis of `0.97` regardless of how long they
had waited. At an age of 32 frames, one maintenance update still replaced only
3% of history. Because the near ring had thousands of pending probes but only a
small maintenance budget per frame, temporal convergence was much slower than
the nominal per-update value suggested.

### P1: authored shadow semantics were discarded

The CPU `Light` model exposed `CastsShadows` and `ShadowStrength`, but the
64-byte `GPULight` layout treated the corresponding words as padding. DDGI hit
shading therefore traced and applied full shadows for every selected light. The
same strength was also absent from the directional raster-shadow result. In
Sponza, the configured directional strength is `0.85`, so fully occluded hits
should retain 15% of that direct term.

### P2: diagnostic and capture defects obscured the result

- The production criterion divided by `max(raw, 0.05)`. With raw and final both
  near `0.011`, it reported a low raw-to-final ratio even though suppression was
  exactly 1.0.
- The capture callback ran before `EndFrame` submitted and presented Vulkan
  work. Win32 `BitBlt` therefore captured the previously presented debug output.
- `DataConfidence` on the simple path is normalized half-Lambert directional
  selection support, not the fraction of valid DDGI data. Values near `0.38`
  were misread as 38% confidence even though ownership was 1.0.
- The receiver-coverage oracle clamped far-ring spacing to 8 m while runtime
  used `1.25 * 3^2 = 11.25 m`.
- A manifest labeled as production timing could still enable detailed forward
  counters, invalidating the intended timing mode and leaving GPU timing absent.

## Findings explicitly ruled out

The following are not causes of the observed low contribution:

- receiver `1 / PI` diffuse normalization;
- raw-to-final composition suppression in the supplied captures;
- missing clipmap coverage, ownership, or fallback;
- a general zero atlas, invalid atlas address, non-finite output, or clamp;
- collapsed visibility weighting;
- unavailable TLAS or a universal trace miss;
- relocation sign or octahedral-direction encoding; and
- the sun-direction sign fix, which was already present in the supplied commit.

The debug label `direct-only` is also not a pure direct-light reference: when GI
is disabled, the forward path still adds unoccluded diffuse IBL. It can therefore
be brighter than a beauty render in which DDGI replaces ambient IBL with
physically occluded transport.

## Remediation implemented

### 1. Pending-retrace state for relocation

A new `SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING` state spans the CPU mirror and
the shared, relocation, blend, and gather shaders.

- A material relocation sets `FRESH | RELOCATION_PENDING`.
- Gather rejects pending probes.
- Blend refuses to publish pending traces.
- The new relocation persists rather than decaying back toward the old offset.
- A subsequent stable full retry clears pending only after tracing from the new
  position; fresh state keeps history weight at zero for the first valid blend.
- Inactive fresh/pending probes bypass normal maintenance throttling so the
  scheduler can complete the transaction.

This establishes the invariant that a probe is never gatherable at position B
with radiance traced from position A.

### 2. Cadence-adjusted history retention

Maintenance hysteresis now accounts for the probe's wall-clock update age. The
effective value is `pow(baseHysteresis, clamp(age / referenceCadence, 1, 8))`,
using reference cadences of 8 frames for full updates and 16 for maintenance.
For example, a maintenance probe aged 32 frames uses `0.97^2`, allowing the same
amount of change per unit time instead of per queue visit.

### 3. End-to-end shadow semantics

Two padding words in `GPULight` now carry shadow flags and normalized shadow
strength while retaining the 64-byte ABI. `LightManager` packs them,
`common.glsl` mirrors them, and DDGI hit shading skips rays for non-casters and
uses `mix(1, tracedVisibility, strength)` for partial shadows. Directional
raster-shadow construction and both single-sample and PCF forward paths use the
same strength.

Legacy shadow-casting lights with a non-positive strength retain full-strength
behavior, matching the existing local-shadow compatibility rule.

### 4. Correct production contribution gate

The gate now compares final diffuse against the actual raw receiver energy when
raw is non-zero. A raw value near zero is treated separately. It no longer
mixes a global absolute-energy floor into a stage-suppression ratio. A supplied-
like case with sampled `0.163`, raw/final `0.011`, blend support `0.5`, and full
ownership now passes; a true collapse from `0.35` to `0.002` still fails.

### 5. Capture schema 4 and settled-state screenshots

Each endpoint/debug output is held for three rendered frames. The first presents
the output, the second spans the two-frame renderer timing latency, and the third
captures a settled window and complete performance state. The renderer capture
request is deduplicated on the first held frame. Path verification is normalized
across Windows separator forms.

Production timing now disables detailed counters; detailed-investigation mode
enables and later restores them. The completed schema-4 production run verified
all 57 manifest artifacts. Image comparison shows every window debug image is
closer to its same-named renderer capture than to the previous renderer output,
which directly validates removal of the one-output shift.

### 6. Shared runtime/oracle ring spacing

`SimpleDdgiVolumeManager.ResolveRingSpacing` is now the single spacing rule for
runtime placement and the receiver-coverage validator. The Sponza regression
asserts ring spacings `1.25`, `3.75`, and `11.25` metres. The final oracle covers
86,400 receiver samples, while the layout admits all 15,368 requested probes
without degradation. The oracle predicts zero recenter events on its trajectory,
whereas the high runtime snapshot reports one cumulative recenter. Spacing is
now shared, but exact recenter-model parity is not yet established.

## Validation results

### Transport evidence

The controlled pre-fix run is in `artifacts/gi-audit-current`; the post-fix
detailed run is in `artifacts/gi-audit-postfix`.

| Endpoint metric | Pre-fix | Post-fix | Change |
| --- | ---: | ---: | ---: |
| Low: trace radiance | `0.13827` | `0.22190` | +60.5% |
| Low: blended radiance | `0.50025` | `0.74270` | +48.4% |
| Low: final diffuse | `0.002009` | `0.007200` | 3.58x |
| Low: support | `0.96489` | `0.98630` | +0.02141 |
| Low: inactive probes | `19` | `12` | -36.8% |
| Low: second-volume contribution | `49.02%` | `40.19%` | less coarse-ring dependence |
| High: trace radiance | `0.13147` | `0.19630` | +49.3% |
| High: blended radiance | `0.52179` | `0.71210` | +36.5% |
| High: sampled irradiance | `1.31381` | `1.49526` | +13.8% |
| High: final diffuse | `0.097084` | `0.109309` | +12.6% |
| High: support | `0.98040` | `0.99710` | +0.01670 |
| High: inactive probes | `29` | `10` | -65.5% |

These receiver metrics sample every sixteenth screen pixel and are not declared
world-space regions of interest. Material, normal, and visibility distributions
therefore differ between endpoints, and stochastic probe scheduling introduces
run-to-run variance. The table is strong directional evidence, not a pixelwise
image-quality score.

The average normalized relocation magnitude increased after the fix. That is
expected: relocation now persists until retrace instead of decaying prematurely.
The more meaningful health signals—support, inactive count, fallback, and
transport energy—all improved.

### Production timing

The corrected production capture is in
`artifacts/gi-audit-production-final-v4b`.

| Endpoint | GPU frame | GPU DDGI | Trace | Blend | CPU GI record P95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Low | `12.640 ms` | `2.810 ms` | `1.110 ms` | `1.518 ms` | `23.837 ms` |
| High | `7.131 ms` | `0.336 ms` | `0.105 ms` | `0.098 ms` | `15.028 ms` |

Both are Debug-build endpoint captures. The GPU columns are single settled-frame
observations, while the CPU column is the renderer's rolling GI-record P95 at
capture time; this is not a rigorous repeated performance sample. The low
endpoint remains above its reported scheduler-policy target of `2.25 ms`; the
high endpoint is below it. Update, forward-inclusive, trace, and blend timing
were available, while incremental/far sub-phase timing was not. Performance
therefore remains an open optimization concern even though the correctness and
telemetry defects are fixed.

### Tests and build checks

Baseline before these changes: 1,045 tests, 1,039 passed and 6 failed.  
Final assembly run: 1,066 tests, 1,060 passed and the same 6 failed.

All affected GI, shadow-packing, capture-harness, gate, coverage, and shader
tests pass. The full shader set compiles. `git diff --check` reports no errors.

The six remaining failures predate and are unrelated to this remediation:

1. `GlobalIlluminationQualityFeatureSwitches_DefaultOnAndDebugIdsStayMapped`
   expects the removed `DrainCaptureQueue();` source text.
2. `Build_ProducesSameMeshletCountsAsAssetRuntimeMeshletBuilder` expects one
   meshlet LOD while runtime now builds three.
3. `StaticMeshStorage_UsesOneVertexRepresentationAndDoesNotFabricateLods`
   expects fabricated-zero LOD fields rather than the current LOD builder.
4. `Build_CapturesLayoutBoundsMaterialSlotsDrawRangesAndMeshlets` expects one
   LOD.
5. `Build_RepresentativeSampleScenePreservesSubmeshMaterialsAndMeshletCounts`
   expects one LOD.
6. `ForwardPass_ClearsDepthWhenDepthPrepassIsDisabled` expects an obsolete
   forward-pass depth-layout branch.

The default parallel solution-level invocation left worker processes alive once
in this environment and hit the command timeout. A serial solution run with
`-m:1` completed in 46 seconds and produced the counts above, as did running the
actual `Njulf.Tests` assembly directly. This points to parallel runner/MSBuild
orchestration instability rather than a test-assembly hang.

## Residual risks and limitations

1. There is no approved visual baseline. The visual metric gate correctly
   remains `not-evaluated-no-approved-baseline`; this audit cannot claim a formal
   visual pass.
2. The original attached camera and frame state are not encoded in the supplied
   images. The locked low/high harness endpoints cannot recreate that exact view.
3. Relocation completion currently depends on the default-enabled classification
   readback to promptly reflect pending state into CPU scheduling. Disabling the
   readback can extend or stall convergence, although gather remains safely
   suppressed while pending.
4. Transaction latency diagnostics can still be optimistic because a skipped
   pending blend may be counted as execution completion even though publication
   was intentionally refused.
5. `DataConfidence` and `direct-only` remain semantically misleading names.
   Their behavior is now documented, not renamed, to avoid widening this patch.
6. Detailed receiver counters are sparse screen-space samples, not stable
   world-space regions of interest. They should not be used alone for release
   gating.
7. The coverage oracle now shares spacing, but it predicts zero recenter events
   while the high runtime snapshot reports one cumulative recenter. Its model
   does not yet prove parity with the runtime policy that advances at most one
   ring per frame.
8. Alpha-masked geometry is still forced opaque in the transport acceleration
   structure, which can over-occlude foliage or cutout surfaces.
9. A missing environment resource still supplies black fallback radiance. That
   is safe but can make configuration/resource failures look like weak GI.
10. Simple and legacy DDGI ownership still coexist in the renderer. This raises
    regression risk during backend switches and obscures resource ownership.
11. The low production endpoint remains over its GPU DDGI budget, and the Debug
    CPU GI-record cost is high. Correctness should be kept while optimizing queue
    selection, trace workload, and blend cost.
12. SDK, NuGet, analyzer, and shader-compiler versions are not fully pinned.

## Recommended follow-up order

1. Capture and approve a small set of HDR or linear renderer baselines at the
   exact original camera plus the two locked endpoints. Make the visual gate
   evaluate those renderer captures, not window screenshots.
2. Add stable world-space receiver regions of interest and percentile metrics;
   rename `DataConfidence` to directional selection support and split unoccluded
   IBL from the `direct-only` output.
3. Profile the low endpoint in Release with repeated settled samples. Optimize
   only after separating trace, relocation/classification, blend, gather, and
   readback costs under production telemetry.
4. Make relocation completion independent of optional CPU readback, or encode a
   GPU-only retrace acknowledgement that cannot be misreported as published.
5. Consolidate simple/legacy backend ownership behind one lifecycle interface
   and split `VulkanRenderer`/`SimpleDdgiVolumeManager` orchestration into smaller
   components.
6. Add alpha-tested any-hit transport support and a non-black, explicitly
   diagnosed environment fallback policy.
7. Replace stale source-string contracts with behavioral tests, make the full
   test suite green, and investigate the parallel solution-runner worker leak.
8. Pin the .NET SDK, NuGet graph, analyzers, and `glslangValidator` version in CI.

## Final state

The primary low-contribution correctness defects are fixed. DDGI now publishes
relocated probes only after retracing at the new position, ages history in
wall-clock terms, and honors authored shadow behavior. The production gate,
capture sequencing, telemetry modes, and far-ring coverage oracle now describe
the rendered state they claim to measure.

The result is materially stronger and diagnostically credible, but not yet a
closed production-quality story: formal visual acceptance is blocked on an
approved baseline, the low endpoint remains over GPU budget, and the dual-DDGI
architecture plus build/test reproducibility debt should be addressed next.
