# DDGI: locate and fix the missing indirect light

## Context

Five performance captures (2026-07-31 19:04–19:15, HEAD `4b95691`) plus the matching debug
screenshots show a Sponza-style palace interior lit by **one directional light** (`LightCount = 1`)
and a dim fallback sky, rendering almost black indoors while DDGI reports itself healthy.

The engine's own counters localise this precisely. Two independent measurements agree, and they do
not point where the last plan assumed:

| Measurement | Value | Implies |
| --- | --- | --- |
| `DdgiForwardEstimateRawDiffuseLuminance / …SampledIrradianceLuminance` | `0.00375 / 0.0957 = 0.0392` | `ρ_eff · AO / π` → **ρ_eff ≈ 0.12–0.15** |
| `SimpleDdgiTransportBounceLuminanceAverage / …SourceLuminanceAverage` | `0.0164 / 0.2455 = 0.067` (cascade 0: `0.149`) | Jacobi fixed point `ρ/(1−ρ)` → **ρ_eff ≈ 0.06–0.13** |

The effective GI diffuse reflectance across the frame is **≈0.06–0.15**. Because ρ enters twice —
once in the equilibrium field (`ρ/(1−ρ)`) and once again at the receiver (`E·ρ/π`) — moving ρ from
0.13 to 0.40 multiplies delivered indirect by roughly **13×**. That single parameter accounts for
essentially the whole deficit. `DdgiForwardEstimateFinalDiffuseLuminance = 0.0038` is the result.

The intended outcome: know with evidence whether that reflectance is authored truth or a defect in
the material/cook path, fix whichever it is, and clear three genuine but secondary defects found
alongside it.

## Diagnosis by pipeline stage

**Transport — not the fault.** The V2 solver is a correct Jacobi iteration. The radiometric
convention is right: `ddgi_simple_blend.comp:296-298` multiplies by π, `gi_material_transport.glsl:229-233`
divides by π, exactly once each. Per-ray bounce is added from the previously published field
(`ddgi_simple_transport.comp:143-195`), material AO is deliberately not re-applied per bounce, the
receiver leak clamp is deliberately not applied per bounce, and `indirectIntensity` is applied only
once at the receiver. Solver ownership `0.9999`, fallback weight `0.0001`, zero source-cache misses.
It is converging to the correct fixed point for the albedo it is handed.

**Probes / gather — not the fault, but the reporting is.** Coverage `0.9999`, support `0.994`,
visibility confidence `0.995`, leak attenuation `1.0`, ownership `0.9999`, 15331/15368 converged.
The gather estimator normalises visibility and directional weight out (`ddgi_simple_shared.glsl:1727-1729`),
which is standard. Nothing here is eating the light.

**Material system — the dominant cause.** See the table above.

**Three secondary defects, independently real:**

1. *Outer-cascade source starvation.* All five captures show `ScheduledPrimaryRayCount = 0` for
   cascades 1 and 2 while cascade 0 gets 768–1024. Every one of the 6–8 per-frame source refreshes
   lands in cascade 0. Cascades 1/2 never re-trace their source cache; their bounce ratios are
   `0.005` and `0.0013` against cascade 0's `0.149`, and `SolverGatherSampleCount` collapses to
   `535` and `18` (vs `38255`). Compounding this, `SimpleDdgiTransportSourceRefreshFrames = 600` is
   only a floor — `SimpleDdgiVolumeManager.cs:3533-3561` raises the effective interval to
   `sweepFrames × (generations + stableUpdates) × 2`, far beyond 600 at 15k probes.

2. *Relocation treats thin cloth as solid geometry.* `ddgi_simple_relocate_classify.comp:116` tests
   `hitKind > 1.5`, which captures **both** `BACK_FACE (2.0)` — the back of a legitimately
   double-sided curtain — and `ONE_SIDED_BACK_FACE (3.0)` — genuinely embedded in a wall. The
   irradiance blend already distinguishes them (`ddgi_simple_blend.comp:268-269` skips only `3.0`);
   relocation does not. In a scene dense with double-sided banners this inflates `backfaceRatio`,
   pushes probes *through* the curtain (`:162-191`), and re-arms `RELOCATION_PENDING | FRESH`, which
   makes the gather reject them (`ddgi_simple_shared.glsl:1536`). `DdgiAverageRelocationFractionEstimate = 0.65`
   is *not* "65% relocated" — per `SimpleDdgiVolumeManager.cs:6552-6572` it is mean displacement
   normalised by the `0.45 × spacing` clamp, so the average moved probe sits at `0.29 × spacing`,
   near saturation. This is the "curtains weird" symptom in commit `d77ecc2`, and it matches the
   `DdgiGatherBlendWeight` screenshot: white = coarse-ring share, concentrated on the drapes.

3. *Source-side waste.* `DdgiSimpleTraceHitCount = 1022`, `MissCount = 2`,
   `ZeroRadianceHitCount = 959` (94%), `DirectLightHitCount = 63`, `EmissiveHitCount = 0` despite
   `DdgiEmissiveSourceCount = 256`. `MaterialEstimatedDetailedTransportHitCount = 448` against 1022
   hits means ~56% of rays terminate as one-sided back faces at `ddgi_simple_trace.comp:349-358`
   with hard-zero radiance and no bounce, yet only **30 of 15368** probes are classified inactive.

**Two debug views are misleading and must be fixed before anyone else reads a capture:**
`DdgiConfidenceBypass` performs no bypass in Simple DDGI mode — `forward.frag:4518` sets
`hybridDebugDiffuse = finalDiffuseIndirect`, so it is byte-identical to `FinalIndirect` (which is
why that screenshot is near-black — it is the real delivered indirect). `DdgiGatherBlendWeight`
shows `secondaryContributionWeight`, the coarse-volume share, not a gather weight.

## Phase 0 — Close the reporting gaps that make captures ambiguous

Small, safe, and a prerequisite for trusting anything downstream.

- `Njulf.Rendering/VulkanRenderer.cs:8358-8367` — `DdgiGatherTileCount`, `DdgiGatherSelected*TileCount`,
  and `DdgiForwardGatherFallbackUsed` are gated on `ddgiActive` (`EffectiveUseDdgi`), which is
  **false** whenever Simple DDGI is on (`RenderSettings.cs:3081-3091` makes them mutually exclusive).
  The tile table is genuinely populated by `UploadSimple` (`:8250-8258`). Report the simple-mode
  values instead of force-zeroing. This also un-breaks the `Unavailable` budget row at
  `Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs:316`.
- `VulkanRenderer.cs:8838-8840` vs `SimpleDdgiVolumeManager.cs:6552-6572` — `DdgiAverageRelocationFractionEstimate`
  has two incompatible definitions (count ratio vs displacement magnitude) and the simple path
  silently overwrites the legacy one. Split into two named fields.
- `forward.frag:4795-4799` — make `DdgiConfidenceBypass` either genuinely bypass suppression on the
  simple path or be removed from the simple-mode debug list in
  `Njulf.Rendering/Pipeline/GlobalIlluminationPassExecutionPolicy.cs:41-50`.
- Rename `DdgiGatherBlendWeight`'s description to state it is the coarse-ring contribution share.

## Phase 1 — Prove or disprove the albedo hypothesis (decisive)

This is the one measurement that decides everything after it. Do not change any shading maths until
it lands.

**1a. Measure effective GI albedo directly instead of inferring it.**
Add two counters alongside the existing `DdgiForwardEstimate*` bank
(`Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs:480`, `Njulf.Rendering/Data/DdgiRuntimeSnapshot.cs:155`):

- Receiver side, at the `DdgiForwardEstimateDiagnosticPixel()` sample points in `forward.frag:4506-4510`:
  mean luminance of `diffuseReflectance` (`canonicalDiffuseReflectance`, built at `forward.frag:3926`).
- Trace side, in `ddgi_simple_trace.comp:420-424`: mean luminance of `surface.DiffuseReflectance`
  per hit, **split by hit class** — one-sided backface, opaque front, thin surface,
  unsupported-transmission, and "zero because `GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE` unset".
  The last bucket is the one that distinguishes a defect from dark art. The existing
  `DDGI_THIN_ZERO_RADIANCE_*` counters at `ddgi_simple_trace.comp:546-566` are the pattern to follow.

**1b. Compare against the cook.** `Njulf.Assets/Cooked/TextureTransportStatistics.cs:555-560` decodes
sRGB→linear correctly for the mean; `Njulf.Rendering/Resources/MaterialTransportCompiler.cs:216-227`
turns that into `DdgiAverageAlbedo`. Dump per-material cooked mean base colour for the palace scene
and compare to the GPU-measured means from 1a.

- GPU mean ≈ cooked mean ≈ 0.1 → the assets are genuinely that dark; go to Phase 2B.
- GPU mean ≪ cooked mean → a defect in the material transport path; go to Phase 2A.

**1c. Controlled diffuse oracle.** Extend `NjulfHelloGame/SampleGlobalIlluminationValidation.cs` and
`Njulf.Tests/SimpleDdgiBounceConvergenceTests.cs` with a white-enclosure scene at ρ = 0.2 / 0.5 / 0.8
and assert converged `bounce/source ≈ ρ/(1−ρ)` within 5–10%. Reuse the existing CPU mirror
`Njulf.Rendering/Data/GiMaterialReferenceEvaluator.cs` and the ABI lock in
`Njulf.Shaders/gi_material_conformance.comp` / `Njulf.Tests/MaterialTransportV2Tests.cs`. If the
oracle passes, the solver is exonerated by construction and every remaining watt is an input problem.

**1d. Lock a baseline.** Run `NjulfHelloGame/SampleSponzaGiCaptureHarness.cs` twice from current HEAD,
record commit + shader + cook fingerprints, and add three world-space ROIs: lit-side curtain,
shadowed receiver directly behind a curtain, adjacent wall expected to show colour bleed. Keep the
existing outdoor reference patch so exposure changes cannot masquerade as transport changes.

## Phase 2 — Fix what Phase 1 proves

**Branch A — material/cook defect.** Likely suspects, in order:
`MaterialTransportCompiler.cs:774-786` sets `TransmissionRemovesOpaqueDiffuse` for *any* transmission
> 0 but sets `ThinSurfaceTransmission` only for `GiTransmissionPolicy.ThinSurface` — a
transmissive-but-not-thin material ends up with reflectance ≈ 0 **and** transmittance 0, black on both
sides; `MaterialTransportContracts.cs:238-241` leaves `ReflectsIndirectDiffuse` false for non-PBR
shading models, which zeroes albedo outright at `gi_material_transport.glsl:290-292` and `:412`;
`Njulf.Rendering/Resources/ModelRenderUploadService.cs` maps imported transmission to `Unsupported`.
Fix at the compiler/import layer, not in the shader.

**Branch B — authored darkness.** State it plainly in the capture notes, then address it as a
lighting/authoring problem: one directional light plus a ~0.8-radiance fallback sky
(`EnvironmentUsesFallback = 1`, `Exposure = 1`, `AutoExposureEnabled = 0`) genuinely cannot light a
0.1-albedo interior. Options are a real environment map, additional authored lights, or corrected
base colours — **not** raising `IndirectIntensity`, removing the `/π`, or raising generations. That
guardrail from `implementation/DdgiBounceAndCurtainThinTransmissionFixPlan-20260731.md` §12 stands.

## Phase 3 — The three secondary defects

Independent of Phase 1's outcome; each is worth landing on its own.

**3a. Split back-face classes in relocation.** `ddgi_simple_relocate_classify.comp:92-125` — treat
`hitKind == 2.0` (double-sided sheet backface) separately from `3.0` (one-sided shell backface).
Only `3.0`, and `2.0` from far-field voxels (`ddgi_simple_trace.comp:518`), is evidence of a probe
embedded in solid geometry. Expect `DdgiAverageRelocationFractionEstimate` to fall well below 0.65
and the `RELOCATION_PENDING`/`FRESH` churn on drape probes to stop. Mirror the change in
`Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`.

**3b. Distribute source refresh across cascades.** In `SimpleDdgiVolumeManager.cs:3386-3446`
(`NeedsSourceRefresh` / `IsRoutinePeriodicTransportSourceRefresh`), reserve a per-cascade share of
the 6–8 per-frame source-refresh slots so cascades 1 and 2 stop reporting
`ScheduledPrimaryRayCount = 0`. Separately, make the effective refresh interval at `:3533-3561`
observable — report it next to the configured `SimpleDdgiTransportSourceRefreshFrames` so a 600-frame
setting that actually behaves like 5000 is visible in the snapshot.

**3c. Probe classification.** With 56% one-sided-backface rays and only 30/15368 probes inactive,
re-check the `hardInvalidProbeScore >= 0.75` deactivation threshold and the `activeFloor = 0.35` at
`ddgi_simple_relocate_classify.comp:127-157, 235-245`. Land this *after* 3a, since 3a changes the
`backfaceRatio` these thresholds consume — retuning first would bake in the wrong evidence.

Thin-surface transmission (`RenderSettings.cs:2007`, defaulted false with no initializer) stays out
of scope here; it is already covered by the existing curtain plan and is a feature, not this bug.

## Verification

1. `dotnet test Njulf.Tests` — including the new oracle, the shader-mirror tests, and the
   `MaterialTransportV2Tests` ABI locks.
2. Oracle scenes converge to `ρ/(1−ρ)` within 5–10% at ρ = 0.2 / 0.5 / 0.8.
3. Re-run `SampleSponzaGiCaptureHarness` and compare against the Phase 1d baseline:
   - new effective-albedo counters agree with the cooked material means;
   - `ScheduledPrimaryRayCount > 0` for all three cascades;
   - `DdgiAverageRelocationFractionEstimate` well below 0.65 with the displacement definition;
   - `DdgiForwardEstimateFinalDiffuseLuminance` risen materially from `0.0038`, with the outdoor
     reference patch unchanged within tolerance;
   - no new wall, floor, or cascade-boundary leaks; ownership/support/visibility gates unregressed.
4. Visual A/B at the same camera and light as the supplied screenshots, with `DdgiSampledIrradiance`
   and `DdgiRawDiffuse` side by side — those two now discriminate "no light arrived" from
   "albedo is zero".

## Guardrails

No global `IndirectIntensity` increase, no removal of the `/π`, no raising
`transportMaximumSolverGenerations`, and no brightening of base colour to mask a transport defect.
Every change lands with the counter or oracle that justifies it.