# DDGI: Sky Reach and Dynamic Time-of-Day Sky — Production Plan

Date: 2026-08-01
Scope: Simple DDGI probe classification and scheduling; procedural sky architecture for continuous
time-of-day
Status: Proposed

---

## 1. Problem

The Sponza palace interior renders near-black. `DdgiForwardEstimateFinalDiffuseLuminance = 0.00409`
against Cornell's `0.0812`, while every DDGI health gate reports success — spatial coverage `0.99998`,
support `0.99983`, effective contribution `0.99996`, ownership `0.99996`.

Sky energy exists and is correctly calibrated. It never reaches receivers.

Separately, the current sky is a CPU-baked gradient behind a device wait-idle. **That architecture
cannot support dynamic time-of-day at all**, and the fix is not incremental.

---

## 2. Established — do not re-investigate

| Finding | Evidence |
| --- | --- |
| Transport solver, gather estimator, π convention, receiver composition are correct | Cornell renders correctly through the same code with `EnvironmentFallbackWeight = 0`, `SkyIntensity = 0` — every photon via the probe field |
| The GI material path reproduces authored albedo faithfully | Stone textures 0.216 linear → predicted 0.188 → `DdgiTraceOpaqueAlbedoLuminance` 0.165 across a mixed stone/curtain population |
| Albedo is ground truth | Intel Sponza is photogrammetric. **Do not raise base colour.** |
| No material is being zeroed | `DdgiTraceUnsupportedTransmissionHitCount = 0`, `DdgiTraceReflectDisabledHitCount = 0` |
| Base colour is decoded once | `Srgb` at import (`SharpGltfModelMeshConverter.cs:176`) → `Rgba8Srgb` cook (`TextureCooker.cs:728-731`) → `R8G8B8A8Srgb` upload (`TextureManager.cs:1305`) |
| The FP16 irradiance atlas is correct | 0.05% relative precision vs a 2.5% solver `residualThreshold`. **Do not change format or add encoding gamma.** |
| `EnvironmentUsesFallback = 1` is not a failure | `CreateEnvironmentPayload` returns it unconditionally on the procedural branch (`EnvironmentManager.cs:306`) |

---

## 3. Root cause of the dark interior

**3.1 No probe ray reaches the sky.** `DdgiSimpleTraceMissCount = 0` in every capture. 67–84% of probe
rays terminate on one-sided back faces (`ddgi_simple_trace.comp:349-358`) returning zero radiance and
no bounce. Only **19 of 10,976** near-cascade probes are classified inactive.

**3.2 The receiver IBL path is gated to zero.** `simpleFallback = (1 − ownership) ×
environmentFallbackIntensity` (`forward.frag:4442`) = `4.2e-05`. All sky energy must route through
probes by design — and they carry none.

---

## 4. What dynamic time-of-day changes

Three consequences, in order of difficulty.

**4.1 The bake architecture is disqualified.** `EnvironmentMapProcessor` bakes a 1024² radiance cube,
a 64² irradiance cube and a 256² prefiltered chain on the CPU, inside `BeginFrame`, behind
`vkDeviceWaitIdle` (`VulkanRenderer.cs:1196`). Sun parameters are quantised to 0.01
(`EnvironmentMapProcessor.cs:75-83`) *specifically* to stop this firing every frame. A continuously
moving sun crosses that quantisation every frame. This must move to analytic GPU evaluation.

**4.2 The authored sun/sky ratio becomes wrong at every elevation but one.**
`E_sky = f/(1−f)·E_sun_horizontal` with a fixed `DiffuseFraction = 0.15` is a static-sun
approximation. Physically, as the sun descends it reddens and dims through increasing atmospheric
path length while the sky's *relative* contribution rises. At sunset a fixed ratio gives a white sun
against a red sky. **The sun must become an output of the atmosphere model, not an independently
authored light.**

**4.3 The hard problem: DDGI cannot track a continuously moving sun at current throughput.** The V2
source cache refreshes 4–8 probes per frame out of 15,368 — a **64-second full sweep**. Under a moving
sun, probes hold source radiance captured at different times of day and the field is never
self-consistent. This is a first-order design problem, addressed in Workstream C, and it makes
Workstream A's throughput work a **prerequisite** rather than an optimisation.

---

## 5. Workstream A — Probe sky reach

Critical path. Until probes can see the sky, no sky work is visible.

### A0. Observability first

`ddgi_simple_relocate_classify.comp` computes `backfaceRatio`, `closeRatio`, `missRatio`, `hitRatio`,
`softInvalidProbeScore` and per-probe `activeWeight`, and writes them to
`SimpleDdgiRelocationClassificationBuffer` (`:53-59`, `:250`). **Nothing reads that buffer** — bound
write-only (`SimpleDdgiPasses.cs:421`). None of it is observable, so A1's thresholds would be guesses.

1. Make the buffer readable; surface `backfaceRatio`, `closeRatio`, `hardInvalidProbeScore`.
2. Fix `DdgiRelocatedProbeFractionEstimate` — gated on `giUsesDdgi` in `VulkanRenderer.cs`, reads `0`
   under Simple DDGI.
3. Surface `OneSidedBackFaceRayCount`, already collected (`RendererDiagnosticsBuffer.cs:685`), never
   printed.
4. `GlobalIlluminationDebugView.DdgiProbeState = 10` already works under Simple DDGI
   (`forward.frag:4884-4906`) and shows the rejection bits. Use it now.

### A1. Classification rule — two independent defects

Inverting the smoothstep, deactivation via the back-face path needs `localBackfaceRatio ≥ 0.685`,
where `localBackfaceRatio = backfaceRatio × backfaceProximity`.

**Defect 1 — the threshold sits above the observed population.** A probe at `backfaceRatio = 0.67`
can never be deactivated *at any distance* — even at `backfaceProximity = 1.0`, 0.67 < 0.685. The
measured population is 67–84%.

**Defect 2 — the distance gate.** `backfaceProximity` reaches zero at `0.45 × spacing` (0.394 m at
cascade-0 spacing 0.875 m). Deeper probes score zero on that path; `closeRatio` needs hits within
`0.25 × spacing` and must reach 0.835. A deeply embedded probe scores zero on both — and is
permanently stuck, because relocation is gated on the same window
(`nearestBackfaceDistance ≤ 0.35 × spacing`, `:165-167`).

The fix needs no new data. `missRatio` and `hitRatio` are computed at `:128-129` and discarded.

```glsl
// A probe enclosed in solid geometry sees almost only back faces and never
// escapes. A distant one-sided shell fails this test because an open probe
// still resolves front faces and sky misses. Escape, not distance, is the
// discriminator.
float enclosureScore =
    smoothstep(0.60, 0.85, backfaceRatio) *
    (1.0 - smoothstep(0.0, 0.05, missRatio));
hardInvalidProbeScore = max(hardInvalidProbeScore, enclosureScore);
```

Thresholds calibrate against A0. **Risk:** a sealed room also has `missRatio == 0`; it is
distinguished only because interior walls present *front* faces. Cornell is the regression gate.

### A2. Classification must survive camera movement

`MarkProbeFresh` (`SimpleDdgiVolumeManager.cs:4255-4325`) unconditionally clears
`_probeInactive[probeIndex]` and on generation advance resets `activeWeight = 1.0`,
`classification = 0` (`:4276-4280`). Toroidal scroll and dirty regions wipe near-cascade classification
continuously. Without this, A1 re-deactivates the same probes forever and never settles while moving.

### A3. Scheduling — now a time-of-day prerequisite

Deactivation alone frees no budget. `ShouldSkipInactiveProbe` (`:3747-3774`) skips only while
`age < InactiveProbeRetryFrames (8)`; past that `IsProbeDataUnavailable` returns true and
`TryResolveProbeWorkClass` assigns **`VisibleZeroSupport` — the highest-priority work class**
(`:3676-3677`), granted `quality.FullRays` (`:4221-4245`). Neither trace nor blend short-circuits on
INACTIVE, so full ray cost and full atlas write are paid.

Separate "confidently inactive" from "lacking data" and retry the former on a slow cadence.
Currently `SimpleDdgiTransportSourceRefreshProbeCount = 4`/frame with `DeferredRequestCount = 13,309`
at `PressureReason = RequestCap`. **Workstream C's sweep-time budget depends directly on this.**

### A4. Inactive probes must stop publishing

`ddgi_simple_blend.comp` reads none of `INACTIVE`, `activeWeight`, `classification` — only
`RELOCATION_PENDING` gates publication (`:557-576`). Worse: when all rays are one-sided back faces,
`weightSum == 0` and the blend **preserves the previous irradiance verbatim at alpha 1** (`:286`), so
the probe is gathered forever. Only `FRESH` writes zero alpha (`:291`).

### A5. Confirm max-ray-distance termination is a miss

Cascade 0 `MaxRayDistance = 24.5 m`. If exhausting the ray budget is recorded as a hit with zero
radiance rather than a sky miss, that is a **second, independent** starvation mechanism A1 will not
fix. Verify in `ddgi_simple_trace.comp`. Related: far-field hits emit
`hitKind = dot(direction, normal) > 0 ? 2.0 : 1.0` (`:518`), inflating `backfaceRatio` at long range.

### A6. Leak safety

Deactivation changes which neighbours the receiver interpolates; the gather renormalises onto
survivors, possibly across a wall. Locked-ROI boundary comparison before landing.

### A7. Test surface — brittle, and one test encodes the bug

- `SimpleDdgiShaderMirrorTests.RelocateAndClassify` (`:1773-1872`) is a line-by-line CPU mirror.
- **`distantBackfaces` (`:962-971`, `:996-997`) asserts `Active == true`** — the exact behaviour being
  fixed. Invert deliberately. Review `deeplyEmbedded` (`:937-946`) too.
- ~20 literal shader-source assertions (`:1319-1322`, `:1374-1392`, `:834-838`) break on any edit.
- `SimpleDdgiVolumeManagerTests` (`:216-268`) pins `RelocationPendingMaximumRetryAge = 32` and
  `InactiveProbeRetryFrames = 8`; `:232-245` derives from both.
- CPU mirrors: `SimpleDdgiVolumeManager.cs:146,150`, `:1515-1531`, `:6434`.
- New counters touch `RendererDiagnosticsBuffer.cs:82-89,652-690`, `DdgiRuntimeSnapshot.cs:6-28`,
  `DebugToolingContractsTests.cs:59,92-96`.

`ddgi_simple_relocate_classify.comp` has no `ShaderBuildTests` coverage; add it.

---

## 6. Workstream B — Dynamic sky architecture

The governing principle: **stop baking, start evaluating.** Everything the sky feeds should derive
from a small per-frame coefficient set, evaluated analytically where it is consumed.

### B1. Analytic sky evaluated in-shader, no radiance cubemap

Compute the sky model's coefficients on the CPU once per frame — table interpolation over turbidity,
ground albedo and solar elevation, on the order of microseconds — and upload ~30 floats. Evaluate the
closed-form radiance function wherever the sky is needed:

- `skybox.frag` — per-pixel. Higher quality than a 1024² cube (no filtering on the sun disc).
- DDGI miss (`SampleDdgiEnvironmentMissRadianceWithFallback`) — per-ray, always current.
- SH projection (B2).

**Deletes:** the radiance cubemap, its CPU bake, the `vkDeviceWaitIdle`, and the 0.01 quantisation.

### B2. Replace the irradiance cubemap with SH-9

Sky diffuse irradiance is smooth once the sun disc is excluded. Spherical harmonics to order 2 (9
coefficients) reconstruct it to roughly 1% (Ramamoorthi & Hanrahan) — well inside the solver's 2.5%
residual tolerance.

- Project once per frame in a single small GPU dispatch, or analytically.
- 27 floats replace a 64² × 6 cube and its 128-sample-per-texel CPU convolution.
- **Linearly interpolatable**, which makes temporal amortisation trivial and artefact-free.

Consumers to migrate: `forward.frag:3300-3317` diffuse IBL, and
`SimpleDdgiEnvironmentIrradianceFallback` (`ddgi_simple_shared.glsl:1784-1798`).

### B3. Prefiltered specular — GPU, amortised, double-buffered

The only remaining cubemap. Port the existing correct GGX integrator
(`GeneratePrefilteredEnvironmentCubemap:190-227`) to compute at 128² with 5 mips, regenerated across N
frames into a double buffer so there is never a stall or a visible pop. Today's procedural "prefilter"
is a lerp toward constant grey (`EnvironmentManager.cs:301-305`) and is not a prefilter at all.

### B4. Model choice — Hosek-Wilkie, behind a swappable interface

**Preetham is disqualified for time-of-day**: it degenerates at low solar elevation, producing wrong
twilight colour and negative radiance — exactly the interesting case. Use **Hosek-Wilkie**.

Hosek-Wilkie is a clear-sky *daytime* model and is undefined below the horizon, so B6 is mandatory,
not optional.

Keep evaluation behind one interface (`EvaluateSkyRadiance(direction, coefficients)`) so a
Bruneton-style precomputed atmospheric-scattering model can replace it later without touching DDGI,
IBL, or the skybox. That is the quality ceiling — it adds aerial perspective and correct all-elevation
behaviour — but it supersedes the whole environment system and is a separate project. Do not start
there.

### B5. Couple the sun to the atmosphere

Retire the fixed `DiffuseFraction = 0.15`. Derive:

- **Sun colour and intensity** from atmospheric transmittance along the sun direction, so it reddens
  and dims correctly toward the horizon.
- **Sky irradiance** from the model rather than an authored ratio.

**Preserve the existing invariant:** the sun disc stays out of the diffuse irradiance term
(`EnvironmentMapProcessor.cs:289-291`) because the directional light owns direct solar transport.
Hosek-Wilkie radiance includes the solar aureole — projecting it into SH would double-count the sun.
This is the single easiest way to break this workstream.

The directional light becomes a *derived* quantity. Authoring moves to time of day, latitude, date,
and turbidity.

### B6. Twilight and night policy

Hosek-Wilkie is undefined below the horizon. A day/night cycle needs an explicit policy: moon as a
second directional source, star field, airglow floor, and a blend through civil and nautical twilight.

This is where most time-of-day implementations look wrong, and it interacts with DDGI: at very low
light levels the probe field's absolute values approach the `SIMPLE_DDGI_TRANSPORT_ABSOLUTE_RESIDUAL_TOLERANCE`
floor of `0.0001`, so convergence behaviour changes. Validate the night end explicitly.

### B7. Authoring surface

`DiffuseFraction` and `GroundAlbedo` are hard-coded in two places
(`EnvironmentMapProcessor.cs:60-61`, `EnvironmentManager.cs:111-112`). Replace with
`EnvironmentSettings` fields for turbidity, ground albedo, sun angular diameter (currently a fixed
`pow(·,2048)`), and a time-of-day driver (time, latitude, date, or a direct sun vector).

Also de-duplicate horizon `(0.85,0.92,1.0)` and zenith `(0.12,0.35,0.85)`, currently literals at both
`EnvironmentMapProcessor.cs:422-423` and `:480-481` — the evaluator and the calibration integrator.
Changing one without the other breaks the energy contract silently.

### B8. Remove two dead, inconsistent fallbacks

- `SimpleDdgiVolumeManager.cs:1341`: `EnvironmentRadianceAndIntensity = new Vector4(0, 0, 0, intensity)`
  — RGB hard-zeroed, so the Simple DDGI fallback returns black.
- `DdgiPipelinePasses.cs:306-310`: the RT path uses `ClearColor.rgb × SkyIntensity` instead.

Two backends, two different wrong answers. Unify or delete.

`EnvironmentSourceKind.Cubemap` is declared (`RenderSettings.cs:572`) but unhandled — it silently
falls through to procedural. Implement or reject explicitly.

---

## 7. Workstream C — DDGI under a moving sun

The hard part, and the one that decides whether time-of-day looks right.

### C1. The consistency problem

Source refresh runs at 4–8 probes/frame against 15,368 — a 64-second sweep. Under a moving sun,
probes hold source radiance captured minutes apart. The field is never consistent with the current
sun, and the error is spatially structured (whole regions lag), which reads as blotching rather than
noise.

### C2. Sun quantisation for GI, continuous everywhere else

Drive the probe field from a **stepped** sun — quantised solar elevation/azimuth — while the visible
sky, the directional light, and shadows track continuously. GI is low-frequency, so the step is
invisible if the step size is bounded relative to sweep time and the solver relaxation smooths the
transition.

Choose the step from the sweep budget, not by taste: `step ≤ sunAngularRate × sweepSeconds × tolerance`.

### C3. Throughput budget — A3 is a prerequisite

Define the target explicitly: a full source sweep in **≤ N seconds**, where N bounds the GI lag the
art direction accepts. Reclaiming budget from permanently-inactive probes (A3) is what makes any N
achievable. Measure before choosing N.

### C4. Invalidation policy

`SimpleDdgiSourceLightingGeneration` currently advances on any lighting-signature change and restarts
global convergence (`SimpleDdgiVolumeManager.cs:2073-2087`). Under a continuously moving sun that
fires constantly and convergence never completes.

Move to a **cohort model**: each sun step defines a generation; probes carry the step they were traced
under; the solver tolerates a bounded spread of steps rather than invalidating globally. The existing
per-probe `_probeSourceLightingGenerations` machinery already supports this shape.

Also revisit `SimpleDdgiTransportSourceRefreshFrames = 600` and its effective interval
(`sweepFrames × (generations + stableUpdates) × 2`, `:3533-3561`) — designed for a static scene, it
must instead be driven by the sun's rate of change.

### C5. Measure the lag

Add a counter for the maximum and P95 age of probe source radiance relative to the current sun step,
and gate on it. Without this, time-of-day GI quality is unfalsifiable.

---

## 8. Sequencing

```
A0 observability          ─┐
A1 classification rule     │  critical path — this is what makes the image change
A2 survive scroll          │
A3 scheduling throughput  ─┴─→ prerequisite for C3
A4 blend gating
A5 ray-distance check      ── parallel with A1
A6 leak validation         ── gates A1 landing
A7 tests                   ── lockstep with A1

B7 de-duplicate + settings ── independent, do early
B1 analytic in-shader sky  ─┐
B2 SH-9 irradiance          │  removes the wait-idle; unblocks time-of-day
B3 GPU prefilter            │
B4 Hosek-Wilkie             │
B5 sun/atmosphere coupling  │
B6 twilight + night        ─┘
B8 dead fallbacks          ── anytime

C1..C5                     ── after A3 and B1/B5; needs both to be meaningful
```

Do not evaluate B against the palace image until A has landed. Do not start C before A3 gives it a
throughput budget to work with.

---

## 9. Verification

**Standing gate — before the palace, every time.** Cornell: `SampledIrradiance ≈ 0.698`,
`ReceiverAlbedo ≈ 0.436`, `FinalDiffuse ≈ 0.0812`, colour bleeding intact, `backfaceRatio` low enough
that A1 does not deactivate its probes.

**Workstream A:**

| Signal | Now | Target |
| --- | --- | --- |
| `DdgiSimpleTraceMissCount` | 0 | > 0 — the primary signal |
| `SimpleDdgiInactiveProbeCount` | 20 | thousands |
| `OneSidedBackFaceHitCount / TraceHitCount` | ~0.7 | materially lower |
| `DdgiForwardEstimateFinalDiffuseLuminance` | 0.00409 | materially higher |
| `DeferredRequestCount` | 13,309 | materially lower |
| `DdgiAverageSupportCoverageEstimate` | 0.9998 | **falls** — that is the fix working |
| `DdgiAverageLeakAttenuationEstimate` | 1.0 | unchanged |

Inactive count stable under continuous camera motion, not only parked.

**Workstream B:** no `vkDeviceWaitIdle` in the sky path at any sun rate; frame time flat across a full
sunrise-to-sunset sweep; SH-9 reconstruction within tolerance of a reference cosine convolution; sun
disc absent from the diffuse term; an unoccluded outdoor up-facing ROI matches the model's predicted
irradiance at several solar elevations.

**Workstream C:** locked-camera capture at several times of day with no blotching; source-age P95
inside the declared budget; no convergence restart storm; night end validated against the residual
floor.

**Both:** `SimpleDdgiBounceConvergenceTests` within tolerance; no NaN/Inf/negative radiance.

---

## 10. Risk register

| Risk | Control |
| --- | --- |
| A1 deactivates probes in sealed rooms | Cornell gate; `backfaceRatio` recorded before/after |
| A1 creates light leaks | A6 locked-ROI boundary comparison |
| Classification thrashes under camera motion | A2; measure stability while moving |
| Deactivation frees no budget | A3 — inactive probes currently run at top priority |
| Buried probes keep publishing stale irradiance | A4 — blend preserves previous value at alpha 1 |
| Shader edit silently breaks the CPU mirror | A7 — line-by-line duplicate, ~20 literal assertions |
| **Sun double-counted in diffuse** | B5 — sun disc must stay out of the SH projection |
| Preetham used and twilight looks wrong | B4 — Hosek-Wilkie, explicitly |
| Night undefined below horizon | B6 — mandatory, not optional |
| GI blotches as the sun moves | C2 sun quantisation + C3 throughput budget + C5 lag counter |
| Convergence never completes under a moving sun | C4 — cohort generations, not global invalidation |
| Frame spikes from environment regeneration | B1/B2 remove the bake; B3 double-buffers the remainder |

---

## 11. Out of scope

Raising `IndirectIntensity`, removing the `/π`, raising `transportMaximumSolverGenerations`, or
brightening base colour. Changing the irradiance atlas format or adding encoding gamma.

Bruneton-style precomputed atmospheric scattering and aerial perspective — B4 keeps the interface
ready for it; it is a separate project.

HDRI environment authoring. The BRDF LUT curve fit (`EnvironmentManager.cs:309-329`).
`skybox.frag`'s unread `DebugView` push constant.
