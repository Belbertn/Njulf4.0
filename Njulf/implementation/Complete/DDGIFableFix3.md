# DDGI Artifact & Instability Fix Plan

## Context

The DDGI work from `DDGIProductionBounceLightingPlan-20260703.md` (Phases 2–9, commits up to `3dc86df` on `claude/ddgi-artifacts-lighting-rpxjnz`) shows three regressions in the enclosed colored-room validation scene: probe-shaped dark blobs + a black band at ceiling/wall junctions in the irradiance debug view, a washed-out/overexposed lit view, and strong temporal instability (blend luminance swinging 0.98→1.56, trace hit fraction 0.3%→13%, updated probes 256→768 with a static camera). Exploration confirmed five root causes; the fixes below keep the existing architecture (GPU scheduler, adaptive ray counts, clipmap cascades, classification/relocation, confidence gates).

## Root causes (all confirmed in code)

1. **RC1 — biased partial-sphere estimator.** Trace rays sample a contiguous rotating window of the 16×16=256 visibility octahedral texels (`ddgi_update_shared.glsl:1881-1891`), but probes trace only 8–128 rays (adaptive buckets), so each update integrates a directional *band* (½…1/32 of the sphere) that `AccumulateProbeIrradianceTexel` rescales by `sampleCoverageScale = 256/rayCount` (×2…×32) (`:1630-1634`). Per-update irradiance is directionally biased and the bias rotates every frame → blotches + energy swings. The probe-state luminance (band mean, `:2054`) feeds the inconsistency metric, driving RC4.
2. **RC2 — visibility weight only in numerator** (`forward.frag:1223-1225`): `accumulated += irr·radianceWeight·visibilityAttenuation` but `totalWeight += radianceWeight` → occluded probes paint black instead of being renormalized away → junction bands.
3. **RC3 — inactive-probe resurrection** (`forward.frag:1146-1148`): `probeActive = max(probeActive, irradianceConfidence)` re-admits probes classification buried in geometry → dark blobs.
4. **RC4 — variance feedback loop.** Blend inconsistency (0.65 persistence, `:483-500`) → catch-up blend alpha up to 0.55 (`:461-473`) → scheduler `highVariance` (>0.25) reschedules with a *different* ray bucket (`ddgi_schedule_score.comp:63-75,126`) → RC1 gives a different biased estimate → loop. Inconsistency also depresses `irradianceConfidence` (`:2261-2263`) feeding the low-confidence trigger.
5. **RC5 — sky fallback added indoors.** Room is served 100% by clipmap probes (no local volume); `warmupFallbackFloor` + `environmentTrust=1-supportTrust` with scene `EnvironmentFallbackIntensity=0.65`, `IndirectIntensity=1.5` (`SampleGlobalIlluminationValidation.cs:304-305`) add sky IBL inside a closed room.

Verified NOT causes: albedo/PI is applied exactly once per bounce; octahedral border addressing is a consistent gutterless mirror; no publish/gather race (deliberate 1-frame latency).

## New evidence (in-room bounce collapse, Diag.txt 2026-07-03)

Symptom: bounce looks decent from the start camera outside the room, collapses to flat gray when the camera moves inside. Diagnostics across the transition:
- `sampledIrrLum` 4.8 → 1.97, `hybridFinalLum` 0.52 → 0.20, data confidence 0.44 → 0.31, effective weight 0.64 → 0.47, avgEdgeFade 0.836 → 0.755, updated-probe `qx` (ray-hit confidence) 0.24 → 0.10.
- In BOTH states, ~97% of probe ray energy is sky: `skyLum ≈ 0.28` of `rayLum ≈ 0.29`; per-ray mean direct ≤ 0.006 (per-hit direct is healthy — `directLum == directNoShadowLum`, so shadow rays are fine — but only ~1-5% of rays hit anything).

Diagnosis:
1. The probe field never contained much real point-light bounce. The Cornell room is 6×4×6 m with one point light (`SampleStressSceneBuilder.cs:641-685`), served by 24 192 camera-clipmap probes spanning 30–120 m of empty space — <1% of probes are near the room, so nearly all traced rays miss to sky. The "decent" outside image is sky-fed exterior probes leaking ambient through the walls (the leak that RC2/RC3 permit), not real bounce.
2. `DdgiClipmapVerticalCenterOffset = 6.25` (DdgiHigh default, `RenderSettings.cs:2034`) centers cascade 0 (14×1.25 = 17.5 m tall) 6.25 m above the camera. With an eye-height camera inside the room, cascade 0's bottom edge is at y ≈ −0.8, putting the room floor deep inside the 15% edge-fade band (2.6 m) → edgeFade/support collapse on floor and lower walls.
3. From inside, the frustum-driven scheduler mostly selects far empty-space probes beyond the walls (qx drops to 0.10), so interior probes update sporadically; sparse-data trust ramps (`DdgiSparseDataTrust`) then halve what remains.

Consequences for the plan (folded into Steps 5-6 below):
- Fixing RC2/RC3 removes the flattering-but-wrong outside leak, so the room will read darker until interior probe energy is real — Step 6's scene/volume item is therefore NOT optional for the Cornell scene.
- `DdgiClipmapVerticalCenterOffset` must not push the ground band into the cascade-0 edge fade for eye-height cameras (set 0, or clamp so the bottom edge stays ≥ ~½ cascade height below the camera).
- Scheduler should prefer geometry-proximate probes (high historical hit-ratio) over empty-space probes when budgeting visible-frustum updates.

## Wicked Engine cross-check (verified against source)

- `ddgi_raytraceCS.hlsl`: full-sphere `spherical_fibonacci(rayIndex, rayCount)` with per-frame `random_orientation` matrix — exactly Step 1a. Hit shading samples ONE random light per ray and multiplies by `light_count` (stochastic all-light estimator, preserves expected energy) — superior to our fixed "1 directional + 1 selected local" cap for many-light scenes; fine to defer for Cornell.
- `ddgi_updateCS.hlsl`: irradiance texel = Σ(cosθ⁺·radiance)/Σ(cosθ⁺) (`result /= total_weight`) — ratio estimator, our optional Step 1b refinement; fixed low blend `lerp(prev, result, 0.02)` for color (no catch-up oscillator — supports Step 2); depth texels use `pow(cosθ, 64)` gather + border copy (Step 1c uses the same shape); backface hits shorten depth (`hit_depth *= 0.9`) and contribute zero radiance.
- `ddgi_rayallocationCS.hlsl`: rays = `saturate(inconsistency) * budget`, bucket-aligned, out-of-frustum ×0.1, first frame max — our Phase 5 already mirrors this; the difference is Wicked's estimator is unbiased at any ray count, which is why theirs is stable and ours isn't until Step 1 lands.
- Volume placement: Wicked fits its single DDGI grid to the scene bounds, so every probe is near geometry — reinforcing the Step 6 scene/volume item for small interior scenes.

Test caveat: `ShaderBuildTests.cs` (182, 474-476, 534, 984, 1144-1146) and `DdgiShaderModelMirrorTests.cs` (46-48, 79-81, 110, 147+) **string-pin the buggy lines** — they must be updated in lockstep with each step.

## Implementation steps (one commit each, in order)

### Step 0 — Baseline capture
Run the colored-room validation scene, record ~200 static frames of `Frame diagnostics GI` / `DDGI TRIAGE VALUES` (blend irrLum series, trace hit fraction, ddgiUpdated, estimate chain, fallbackWeight) + screenshots of irradiance debug and lit views.

### Step 1 — RC1: full-sphere spherical-Fibonacci estimator (`ddgi_update_shared.glsl`)
- Add `DdgiSphericalFibonacci(i, n)` and a per-(probe,frame) random rotation `DdgiProbeRayRotation(probeIndex, pc.FrameIndex)` (reuse `Hash22`/`Hash11` helpers near `:372-390`; `pc.FrameIndex` is monotonic).
- Trace main (`:1877-1891`): replace the texel-window direction with `direction = rotation * DdgiSphericalFibonacci(rayIndex, raysPerProbe)`, `raySolidAngle = 4π/raysPerProbe`. `DdgiRayResult` layout unchanged (solid angle still stored per ray) → no C# struct change.
- `AccumulateProbeIrradianceTexel` (`:1613-1642`): delete `sampleCoverageScale` and `directionalTexelCount`; keep `weight = cosθ⁺·raySolidAngle·rayConfidence`. With uniform sphere sampling E[Σcosθ⁺·4π/N] = π, so `irradiance = weightedRadiance` is unbiased and `confidence = clamp(weightSum/PI,0,1)·activeProbe` stays valid unchanged.
- Blend visibility write (`:2015-2036`): replace the ray-index==texel scatter with an RTXGI-style per-texel gather: add `shared vec2 SharedRayVisibility[256]`, fill in the ray loop; after the barrier, loop visibility texels (256 texels / 64 threads), weight rays by `pow(cosθ⁺, 50.0)·rayValid`, write `moments/weightSum` via existing `WriteVisibilityAtlasSample` only when `weightSum > 1e-4` (uncovered texels keep history). No write races (one workgroup per probe, one writer per texel). Fallback if GPU time regresses: scatter via `DirectionToAtlasTexel(result.direction)` accepting benign last-write races.
- Relocate/classify pass needs no change (consumes only hit/backface/relocation fields from the ray buffer — verified `:2170-2184`).
- Tests: update `ShaderBuildTests.cs:463-476` pins; add a C# mirror test (new `DdgiFibonacciDirectionTests` or in `DdgiShaderModelMirrorTests`) asserting unit length and `Σcosθ⁺·4π/N ≈ π ±5%` for N ∈ {32,64,128,256}.
- Verify: blend irrLum swing collapses to a few %, trace hit fraction steady, rotating blotches gone.

### Step 2 — RC4a: blend damping (`ddgi_update_shared.glsl`)
- `ResolveDdgiIrradianceBlendAlpha` (`:461-473`): `catchUpResponse = mix(0.0, 0.35, smoothstep(0.20, 0.60, inconsistency))` (was 0.55 / 0.10).
- `ResolveDdgiIrradianceHistory` (`:483-500`): `inconsistency = max(meanDelta, previousInconsistency * 0.5)` — drop `instantaneousDelta` from the max (short mean at 0.35 response still registers real changes in 2-3 frames), faster decay (0.65→0.5). Keep `.w` return.
- Update `DdgiShaderModelMirrorTests.cs:147+` pin. Verify: light-toggle converges in ~5-8 frames; static scene doesn't shimmer (Phase 3 acceptance).

### Step 3 — RC2: visibility in both numerator and denominator (`forward.frag:1211-1229`)
```glsl
float visibilityWeight = max(visibilityAttenuation, 0.03);  // graceful all-occluded floor
float visibleRadianceWeight = radianceWeight * visibilityWeight;
accumulated  += clamp(probeIrradiance, 0, 64) * visibleRadianceWeight;
totalWeight  += visibleRadianceWeight;   // was radianceWeight
```
`dataWeightSum`, `visibilityWeightedSupport`, `totalVisibility`, `leakClamp` unchanged so thin-wall/leak diagnostics are preserved; low `dataWeightSum` still routes all-occluded pixels to the fallback path via `dataConfidence`. Update pins (`ShaderBuildTests.cs:180-183,534,1144-1146`; `DdgiShaderModelMirrorTests.cs:46-48,110`). Verify: junction band gone in irradiance debug; thin-wall scenario still passes.

### Step 4 — RC3: remove resurrection (`forward.frag:1146-1148`) — must land AFTER Step 3
Delete the two `probeActive = max(probeActive, irradianceConfidence)` lines. Buried probes stay at the clipmap classification floor (0.35, keep it — prevents coverage holes) and Step 3's Chebyshev renormalization neutralizes them. Update `ShaderBuildTests.cs:984`. Verify: ceiling blobs gone; `ddgiProbeConfidence alpha/qx/qy/qz` none stuck at 0.

### Step 5 — RC4b: scheduler damping (`ddgi_schedule_score.comp`)
- `highVarianceProbe`: threshold 0.25→0.35 and require `probeAge >= 2` (sustained inconsistency, survives one 0.5× decay).
- `varianceBoost`: `mix(1.25, 1.5, clamp((inconsistency-0.35)/0.65, 0, 1))` (was up to 1.75).
- `AlignDdgiScheduleRayBucket`: drop the 8/16/24 buckets — minimum 32 rays (8 rays over 4π is estimator poison; total still bounded by PrimaryRayBudget).
- Optional (only if `ddgiUpdated` still breathes visibly after Steps 1–4): make the steady-visible lane deterministic-phase instead of frame-hashed.
- Verify: static camera → `ddgiUpdated` steady, variance reason count decays to ~0, `ddgiActualPrimaryRays` steady.

### Step 5b — Interior coverage (from the in-room collapse evidence)
- `RenderSettings.cs:2034` (DdgiHigh tier): set `DdgiClipmapVerticalCenterOffset = 0` (or derive it so cascade-0's bottom edge stays at least ~½ cascade height below the camera) so ground-level surfaces are not in the cascade-0 edge-fade band at eye height.
- `ddgi_schedule_score.comp`: bias visible-frustum lane selection toward geometry-proximate probes — e.g. use the stored per-probe hit ratio (`relocationBase + 8u` word `.w` written by relocate/classify) as a selection weight so interior probes out-compete empty-space probes for the visible budget.
- Verify: from an in-room camera, `avgEdgeFade` stays ≥ ~0.85, interior probes' update age stays low, `qx` of updated probes rises.

### Step 6 — RC5: environment fallback discipline
- **Keep** `environmentTrust = 1 - supportTrust` (reverting to `1-ddgiTrust` would re-add sky exactly where leak clamping suppresses DDGI).
- `ComposeHybridDiffuseGi` (`forward.frag:~1794-1799`): `warmupFallbackFloor = DdgiCacheValid() ? (1.0 - cacheReadiness) * (1.0 - supportTrust) : 1.0` (replace `1-dataTrust`) so the floor vanishes once warmed. Update pins (`ShaderBuildTests.cs:242-246`, `DdgiShaderModelMirrorTests.cs:81`).
- Scene: `SampleGlobalIlluminationValidation.cs:305` — drop `EnvironmentFallbackIntensity` 0.65 → ~0.2 for the enclosed-room validation. Keep `IndirectIntensity=1.5` for now; re-evaluate against `SampleDdgiProductionGate` after Steps 1–4.
- Room probe coverage (no longer optional given the in-room collapse): author a local DDGI volume for the Cornell room validation scene (the `DdgiRoomVolumeValidator` / `CreateThinWallRoomPreset` path in `Njulf.Rendering/Resources/DdgiRoomVolumeValidator.cs` already builds dense 0.35 m room volumes) or reduce cascade-0 spacing for interiors, so real interior bounce replaces the sky leak the RC2/RC3 fixes remove.
- Verify: interior `fallbackWeight ≈ 0` once warmed; `hybridFinalLum ≈ ddgiDiffuseLum` indoors; lit view exposure sane.

### Step 7 — Cleanup/docs
- No push-constant or GPU-struct changes anywhere (verified). No new RenderSettings knobs initially — ship thresholds as shader constants; add knobs only if the production gate needs per-scene overrides.
- Append an addendum to `Njulf/implementation/DDGIProductionBounceLightingPlan-20260703.md` recording the estimator change (the doc's own reference notes already prescribe "spherical Fibonacci directions with a random orientation").

## Round 2 (2026-07-03): status + external-engine survey

Round-1 status: Steps 1–6 (incl. 5b) are implemented and pushed on `Simplified` (55dbed6..62b44a5): Fibonacci full-sphere trace + rotation, coverage-scale removal, visibility-texel gather (cos^50), blend damping (0.35 cap, 0.5 decay, meanDelta only), gather visibility renormalization (0.03 floor), resurrection removal, scheduler damping (min 32 rays, 0.35/age≥2 variance), geometry-proximate lane bias, warmup floor on supportTrust, vertical offset 0, scene fallback 0.2, authored Cornell room probe volume (`AddValidationRoomProbeVolume`).

### FlaxEngine survey findings (verified against Source/Shaders/GI/DDGI.hlsl / DDGI.shader)

Ranked adoption candidates, mapped to remaining pain points (dim/flat interiors, residual probe-grid seams, stability/energy):

1. **Perceptual (sRGB-space) irradiance blending** — store `pow(irradiance, 1/IrradianceGamma)`, blend history in compressed space, decode on read (`pow(x, γ/2)` then square after weight normalization). Attacks dim/flat interiors and blending correctness at low luminance. Localized to `WriteProbeIrradianceAtlasTexel`/`ReadDdgiProbeIrradiance` (+ stable-sample mirror in update shader) + mirror tests.
2. **Gather weight shaping trio** (targets grid-shaped seams):
   - backface wrap weight `Square(dot(dirToProbe, normal)*0.5+0.5)` floored at 0.1;
   - low-weight crush: below 0.2, `weight *= weight²/0.04`;
   - trilinear `saturate(trilinear.x*y*z * 2.0)`.
3. **Softened normalization denominator**: `irr /= lerp(1, weightSum, saturate(weightSum²+0.9))` — stops sparse-support taps from over-brightening.
4. **Chebyshev sharpening**: visibility moment ray weight pow-10 (we use ~cos^50 — compare), Chebyshev weight **cubed** in gather, occluded-only application, distance stored ×0.5/read ×2.
5. **Inactive-probe fallback via nearest-valid-neighbor coords** (Flax uses a jump-flood pass; a cheap variant: search the 6/26-neighborhood in classify and store fallback probe index in relocation buffer) — dead probes borrow a live neighbor instead of contributing nothing at the 0.35 floor.
6. **Attention-style hysteresis curve**: `historyWeight = lerp(0.97, fastWeight, attention³)`, fast weight halved when per-texel delta > 0.5, attention rises instantly on instability and decays ×0.2 — cleaner than our smoothstep catch-up; benchmark against Step 2 values.
7. **Cascade handling**: rounded-box SDF cascade weight with dithered selection and 2×spacing blend band; environment fallback = `lerp(result, FallbackIrradiance, (1-cascadeWeight)*fallback.a)`; staggered cascade update frequencies {2,3,5,7} with ≤2 cascades/frame.
8. Flax constants for reference: 6×6 irradiance + 14×14 distance interior with 1px gutter (R11G11B10 / RG16F), rays floor 16, surface bias `(0.2·N + 0.8·V)·0.6·spacing·bias`, probe deactivation when >10% backface rays, SDF-distance classify limits {1.1,2.3,2.5,2.5}×spacing, relocation clamp {0.4..0.7}×spacing, vertical probe density ×0.8.

### Arkose / LuxGI / EvoEngine survey findings

- **ArkoseRenderer** (minimal RTXGI-lineage, stability-focused): fixed grid, round-robin probe window, fixed ray counts, hysteresis 0.85–0.98, history resets deliberately disabled; gamma-encoded EWMA (`pow(x, 1/γ)` on store); soft backface `smoothFloor 0.02 + pow(dot, 0.25)`; Chebyshev cubed with 0.05 floor; low-weight crush <0.2; self-shadow bias `(0.2N+0.8V)·0.75·spacing·0.3`; relocation damped by `exp2(-10·dt)` (anti-pop). Lesson: high hysteresis + unbiased full-sphere estimator beats aggressive catch-up.
- **LuxGI** (hybrid DDGI + SDF): fixed scene-bounds grid (+2 guard probes), ALL probes updated every frame because rays are cheap — marched against a global SDF and shaded from a lit global surface atlas (Lumen-style surface cache, contains multi-bounce); RT backend interchangeable; gamma encode/decode (γ store 1/γ, read γ/2 then square); backface wrap `((dot+1)/2)² + 0.2` additive floor (very generous, hides probe structure); probe irradiance doubles as rough-reflection fallback.
- **EvoEngine** (closest to ours — scrolling clipmap + budget scheduler): budget expressed in **ray-samples** (probes/frame = rayBudget / raysPerProbe, adaptive toward a target ms) rather than probe count; per-reason hysteresis (light vs geometry/material vs scroll-overwrite) with **darkening/brightening asymmetry** (`min_darkening_step = 1/1024`, faster on darkening, damped brightening); HDR clamp [0,64] before squared-gamma encode; smooth per-axis volume-edge falloff `axis = 1 - clamp(outsideDist/step, 0, 1)`; classification only deactivates probes *inside* geometry (relocation on, classification off by default to avoid pruning near-wall probes); probe-variability (CoV, threshold 0.2, ≥16 samples) plumbed for convergence gating.

## Round 2 implementation steps (proposed, in order)

### R2-1 — Perceptual (gamma) irradiance encoding [unanimous across Flax/Arkose/LuxGI/EvoEngine/RTXGI; biggest lever for dim/flat interiors + blend correctness] VERIFY IF DONE
- Store: clamp ray-integrated irradiance to [0, 64] (replace `DDGI_IRRADIANCE_ATLAS_MAX = 256`), then `pow(x, 1/5)` before `WriteProbeIrradianceAtlasTexel`'s history mix — EWMA now blends in perceptual space.
- Read: RTXGI squared-gamma reconstruction — decode taps with `pow(x, 5·0.5)` before weight-normalization, then square the normalized result (`forward.frag ReadDdgiProbeIrradiance` + `SampleStableDdgiIrradiance`/ambient read in `ddgi_update_shared.glsl`/`common.glsl` — all three consumers must decode identically).
- Re-tune firefly suppression to operate pre-encode; confidence channel unaffected.
- Tests: mirror test asserting encode→blend→decode round-trip vs linear reference within tolerance; update ShaderBuildTests pins.

### R2-2 — Gather weight shaping [grid-seam killer; Flax + Arkose + LuxGI agree]
In `SampleDdgiVolumeIrradiance` (`forward.frag`):
- Backface wrap weight: `Square(dot(dirToProbe, normal)·0.5 + 0.5)` floored (A/B: Flax floor 0.1 vs LuxGI `+0.2` additive vs Arkose `0.02 + pow(dot,0.25)`), replacing the current hard `normalHemisphereWeight²·grazingRejection`.
- Cube the Chebyshev visibility weight, floor 0.05 (all three engines).
- Low-weight crush: `if (w < 0.2) w *= w²/0.04` (all three engines) — applied to the combined per-probe weight.
- Trilinear `saturate(tri.x·tri.y·tri.z · 2.0)` and softened denominator `irr /= lerp(1, Σw, saturate(Σw² + 0.9))` (Flax) so sparse-support cells neither over-brighten nor band.
- Verify with the wall/ceiling debug views; thin-wall scenario must still pass.

### R2-3 — Asymmetric, reason-aware hysteresis [EvoEngine; refines Step 2's scalar catch-up]
- In `ResolveDdgiIrradianceBlendAlpha`/`WriteProbeIrradianceAtlasTexel`: faster blend when texel darkens, damped when brightening (threshold + `min darkening step 1/1024`); keep per-reason floors (light/material change) as-is; consider Flax's `lerp(0.97, fast, attention³)` shape for the inconsistency mix.
- Watch Arkose's counter-lesson: with the now-unbiased estimator, prefer raising baseline hysteresis over widening catch-up.

### R2-4 — Inactive-probe neighbor fallback [Flax]
- In relocate/classify: when a probe is inactive (buried), find the nearest active neighbor (26-neighborhood search is sufficient at our scale; Flax uses jump-flood) and store its index in the relocation/classification buffer.
- In gather: on inactive probe, sample the fallback probe's atlas instead of relying on the 0.35 clipmap floor; skip Chebyshev if the fallback is >1 cell away (Flax's `useVisibility`/`minWeight` rules).

### R2-5 — Ray-sample budget scheduling [EvoEngine; smooths cost & shimmer]
- Express the scheduler budget in primary ray-samples: probes/frame ≈ `PrimaryRayBudget / avgRaysPerProbe` with next-frame scaling from measured GPU cost toward the tier's ms target (we already track P95). Keeps adaptive per-probe rays without cost blowups; complements the min-32 floor.

### R2-6 — Cascade/volume edge smoothing [Flax rounded-box + EvoEngine per-axis falloff]
- Compare our `edgeFade` shape against Flax's rounded-box SDF weight with dithered cascade selection + 2×spacing blend band and EvoEngine's per-axis linear falloff; adopt whichever removes the remaining support-coverage banding at cascade seams; environment fallback stays weighted by (1 − cascade coverage).

### R2-7 (future milestone, not this round) — SDF + surface-cache ray backend [LuxGI/Flax architecture]
- A global SDF + lit surface atlas would make probe rays cheap enough to update all near-field probes every frame (the structural fix for interior ray scarcity). Large scope: new passes (SDF build, surface cache), new shading path. Log as a separate design doc; do not block round 2 on it.

### R2 verification
- Same harness as round 1 (colored-room + thin-wall + Sponza validation scenes, 200-frame stability capture, debug views, `SampleDdgiProductionGate`).
- R2-1 acceptance: interior `ddgiDiffuseLum` rises at equal probe energy (decode correctness), no new shimmer (blend now perceptual), furnace test still within tolerance.
- R2-2 acceptance: probe-grid banding gone on walls/ceiling in irradiance + final views; thin-wall leak ratio unchanged.
- R2-5 acceptance: `gpuDdgiUs` P95 variance shrinks at unchanged visual convergence.

## Verification (end-to-end)

- `dotnet test` on Njulf.Tests: `ShaderBuildTests` (shader compile + updated pins), `DdgiShaderModelMirrorTests` (+ new Fibonacci estimator test), `GPUStructLayoutTests` (unchanged), scheduler tests, `SampleDdgiBenchmarkSuiteTests`.
- Run the colored-room scene per step and compare with Step 0 baseline:
  - stability: blend irrLum σ/μ < 3% over 200 static frames; `ddgiUpdated` steady; variance reason → ~0;
  - artifacts: irradiance debug + confidence-bypass debug (view 119) show no blobs/bands;
  - energy: interior fallbackWeight ≈ 0; convergence < ~8 frames on light toggle; thin-wall scenario still leak-protected; `gpuDdgiUs` within tier budget (watch Step 1c's extra blend loop);
  - `SampleDdgiProductionGate` green.
- Commit each step separately on `claude/ddgi-artifacts-lighting-rpxjnz`; push with `git push -u origin claude/ddgi-artifacts-lighting-rpxjnz`.