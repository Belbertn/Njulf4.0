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

### Step 6 — RC5: environment fallback discipline
- **Keep** `environmentTrust = 1 - supportTrust` (reverting to `1-ddgiTrust` would re-add sky exactly where leak clamping suppresses DDGI).
- `ComposeHybridDiffuseGi` (`forward.frag:~1794-1799`): `warmupFallbackFloor = DdgiCacheValid() ? (1.0 - cacheReadiness) * (1.0 - supportTrust) : 1.0` (replace `1-dataTrust`) so the floor vanishes once warmed. Update pins (`ShaderBuildTests.cs:242-246`, `DdgiShaderModelMirrorTests.cs:81`).
- Scene: `SampleGlobalIlluminationValidation.cs:305` — drop `EnvironmentFallbackIntensity` 0.65 → ~0.2 for the enclosed-room validation. Keep `IndirectIntensity=1.5` for now; re-evaluate against `SampleDdgiProductionGate` after Steps 1–4.
- Optional follow-up (note in plan doc, don't block): authored local volume or ≤1.0 m cascade-0 spacing for the room scene (currently 1.25 m clipmap-only).
- Verify: interior `fallbackWeight ≈ 0` once warmed; `hybridFinalLum ≈ ddgiDiffuseLum` indoors; lit view exposure sane.

### Step 7 — Cleanup/docs
- No push-constant or GPU-struct changes anywhere (verified). No new RenderSettings knobs initially — ship thresholds as shader constants; add knobs only if the production gate needs per-scene overrides.
- Append an addendum to `Njulf/implementation/DDGIProductionBounceLightingPlan-20260703.md` recording the estimator change (the doc's own reference notes already prescribe "spherical Fibonacci directions with a random orientation").

## Verification (end-to-end)

- `dotnet test` on Njulf.Tests: `ShaderBuildTests` (shader compile + updated pins), `DdgiShaderModelMirrorTests` (+ new Fibonacci estimator test), `GPUStructLayoutTests` (unchanged), scheduler tests, `SampleDdgiBenchmarkSuiteTests`.
- Run the colored-room scene per step and compare with Step 0 baseline:
  - stability: blend irrLum σ/μ < 3% over 200 static frames; `ddgiUpdated` steady; variance reason → ~0;
  - artifacts: irradiance debug + confidence-bypass debug (view 119) show no blobs/bands;
  - energy: interior fallbackWeight ≈ 0; convergence < ~8 frames on light toggle; thin-wall scenario still leak-protected; `gpuDdgiUs` within tier budget (watch Step 1c's extra blend loop);
  - `SampleDdgiProductionGate` green.
- Commit each step separately on `claude/ddgi-artifacts-lighting-rpxjnz`; push with `git push -u origin claude/ddgi-artifacts-lighting-rpxjnz`.