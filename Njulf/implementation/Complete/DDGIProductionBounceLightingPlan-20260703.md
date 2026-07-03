# DDGI Production Bounce Lighting Plan

Date: 2026-07-03

## Goal

Make DDGI bounce lighting visibly strong, physically coherent, leak-resistant, and performant enough to be the default production GI path.

The immediate problem is that bounce lighting still reads too weak. Do not solve this first by raising `IndirectIntensity`. The first milestone is to prove where energy is lost: probe tracing, probe blending, atlas storage, forward gather confidence, visibility/leak suppression, or final composition.

## References

- WickedEngine DDGI shaders:
  - `ShaderInterop_DDGI.h`
  - `ddgi_rayallocationCS.hlsl`
  - `ddgi_raytraceCS.hlsl`
  - `ddgi_updateCS.hlsl`
  - `ddgi_updateCS_depth.hlsl`
- NVIDIA RTXGI DDGI docs:
  - `RTXGI-DDGI/docs/Algorithms.md`
  - `RTXGI-DDGI/docs/DDGIVolume.md`
- JCGT:
  - `Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields`
  - `Scaling Probe-Based Real-Time Dynamic Global Illumination for Production`

## Useful Findings

WickedEngine is simpler than our scheduler and clipmap system, but it has useful production energy behavior:

- Per-probe ray count is driven by probe inconsistency/variance.
- Off-frustum probes still update, but with reduced ray count.
- First frame uses max rays per probe for fast convergence.
- Probe rays use spherical Fibonacci directions with a random orientation.
- Hit shading feeds previous probe lighting back into the ray result for infinite bounce.
- Probe update uses firefly suppression and half-float overflow guards before storing.
- Irradiance is compacted through SH/L1 and the gather weights the whole representation before evaluation.
- Visibility stores distance and squared distance and uses Chebyshev-style moment visibility.

RTXGI/JCGT reinforces the same core model:

- Trace and shade rays from active probes.
- Use previous probe data during hit shading for recursive bounce.
- Blend transported radiance into probe irradiance.
- Blend distance and distance-squared into visibility.
- At shading time, query the eight surrounding probes using trilinear, backface, and visibility weights.
- Bias visibility queries away from the surface with a self-shadow bias.
- Use probe state/classification and cascaded volumes to control cost in large worlds.

## Local Risk Areas

These are the most likely causes of weak bounce in the current codebase.

1. Radiance/irradiance convention may be inconsistent.
   - `Njulf.Shaders/ddgi_update_shared.glsl`
     - `EvaluateSelectedDdgiLight()` applies `albedo / PI`.
     - `EvaluateStableDiffuseAtHit()` applies `albedo / PI`.
     - `TraceProbeRay()` stores the sum of direct diffuse, emissive, and stable diffuse into ray radiance.
   - `Njulf.Shaders/forward.frag`
     - `SampleDdgiDiffuse()` applies `ddgi.irradiance * (albedo / PI)` again.
   - If the atlas stores already-BRDF-weighted outgoing diffuse, forward shading can divide energy by albedo/PI a second time.

2. Probe atlas update may be under-normalized.
   - `AccumulateProbeIrradianceTexel()` accumulates cosine-weighted ray radiance and applies sample coverage scaling.
   - Confirm expected energy against the JCGT update equation and a reference path-traced/CPU integration for simple test scenes.

3. Confidence gates may suppress valid energy.
   - Forward gather multiplies probe activity, atlas confidence, ray-hit confidence, irradiance confidence, visibility confidence, visibility attenuation, leak clamp, AO, and support coverage.
   - This is good for leak prevention, but a single systematically low confidence channel can make bounce appear weak even when the atlas contains energy.

4. Lighting source selection may under-sample energy.
   - DDGI hit shading currently uses bounded selected lights.
   - For scenes with many local lights or emissives, adaptive or stochastic light selection needs to preserve expected energy.

5. Scene settings may hide DDGI.
   - Plaza config uses low environment diffuse and fallback values.
   - Validate default `DdgiHigh` independently from the plaza tuning so scene art controls do not mask algorithmic issues.

## Phase 1: Prove The Energy Chain

Add diagnostics before changing visual constants.

Required debug views/counters:

- Ray trace output luminance:
  - direct diffuse
  - unshadowed direct diffuse
  - emissive
  - recursive stable DDGI
  - sky/environment miss
  - final ray radiance
- Blend output:
  - average ray radiance per updated probe
  - per-texel atlas irradiance
  - atlas confidence
  - visibility moments
- Forward gather:
  - sampled irradiance before albedo
  - final DDGI diffuse after albedo/metallic
  - probe support coverage
  - data confidence
  - visibility confidence
  - leak attenuation
  - final effective DDGI weight
  - environment fallback weight

Acceptance:

- In an enclosed white room with a single colored wall and white light, raw ray radiance, atlas irradiance, sampled irradiance, and final diffuse must have explainable ratios.
- If raw atlas energy is high but final image is weak, the debug chain must identify the suppressing factor.
- If raw atlas energy is low, the trace/update path must identify whether direct, recursive, emissive, or sky contribution is missing.

## Phase 2: Lock The Radiance Convention

Pick one convention and enforce it in names, comments, tests, and debug labels.

Recommended convention:

- Probe ray results store transported incoming radiance along the ray.
- Probe atlas stores irradiance for a surface normal direction.
- Forward shading applies the diffuse BRDF exactly once: `irradiance * albedo / PI`.
- Recursive DDGI hit shading should sample incoming irradiance and apply the hit surface diffuse BRDF exactly once.

Implementation work:

1. Audit `EvaluateSelectedDdgiLight()`, `EvaluateStableDiffuseAtHit()`, `TraceProbeRay()`, `AccumulateProbeIrradianceTexel()`, `SampleDdgiVolumeIrradiance()`, and `SampleDdgiDiffuse()`.
2. Rename misleading variables where needed:
   - `radiance` for incoming radiance.
   - `irradiance` for cosine-integrated incoming light.
   - `diffuse` for BRDF-applied outgoing diffuse.
3. Add shader mirror tests that prevent double application of `albedo / PI`.
4. Add a white furnace-style DDGI test:
   - white albedo
   - constant environment
   - no direct local lights
   - expected diffuse should be stable and close to the known analytic value after exposure.

Acceptance:

- Doubling `IndirectIntensity` is not required to make bounce visible in the simple room.
- Debug views show consistent energy progression from trace to final composition.

## Phase 3: Fix Atlas Integration And History

Bring probe blending closer to the production DDGI model.

Implementation work:

1. Validate cosine normalization in `AccumulateProbeIrradianceTexel()`.
   - Compare against JCGT equation: sum `max(0, n dot w) * L(w)` with correct sampling PDF/solid angle handling.
   - Verify the current `sampleCoverageScale` is not dimming high ray count or low ray count probes.
2. Add variance-aware irradiance history.
   - Port the useful part of Wicked's multiscale mean estimator:
     - long mean
     - short mean
     - variance
     - inconsistency
     - firefly suppression
     - catch-up blend when lighting changes
   - Store only the minimum extra data needed for production budgets.
3. Split irradiance hysteresis from visibility hysteresis.
   - Lighting changes should converge faster than stable geometry.
   - Geometry changes should refresh visibility more aggressively.
4. Add explicit half-float safety.
   - Clamp before packing.
   - Detect NaN/Inf in debug.
   - Keep high dynamic range enough to avoid flattening bright bounce.

Acceptance:

- Large emissive or directional light changes visibly converge within a small number of frames.
- Stable scenes do not shimmer.
- Bright sky/sun/emissive cases cannot poison the probe field.

## Phase 4: Make Confidence Gates Energy-Preserving

Keep leak protection, but avoid turning valid low-frequency GI black.

Implementation work:

1. Audit each confidence factor in forward gather.
   - `probeActive`
   - `irradianceConfidence`
   - `rayHitConfidence`
   - `stateIrradianceConfidence`
   - `visibilityConfidence`
   - `visibilityAttenuation`
   - `leakAttenuation`
   - `indirectAo`
2. Convert hard products into soft ramps where appropriate.
   - Ray-hit confidence should reduce trust, not necessarily remove all radiance.
   - Visibility confidence should control how much the visibility moments are trusted.
   - Atlas confidence should remain strict only for genuinely uninitialized data.
3. Add a debug mode that bypasses only confidence suppression, not visibility.
   - This isolates "probe has light but confidence killed it" from "probe is occluded."
4. Tune leak clamp separately from brightness.
   - Do not use leak prevention as an implicit global intensity control.

Acceptance:

- Thin-wall tests still pass.
- Enclosed-room bounce remains visible with classification and relocation enabled.
- Debug counters show no single confidence channel stuck near zero across valid visible probes.

## Phase 5: Adaptive Ray Allocation

Use Wicked's best performance idea without replacing our scheduler.

Status: implemented on 2026-07-03.

Notes:

- Probe luminance inconsistency from Phase 3 is consumed by the GPU scheduler.
- GPU candidates now emit capacity-clamped adaptive primary-ray costs:
  - high-inconsistency, low-confidence, dirty, new-cell, and long-unseen visible probes are boosted;
  - stable visible, age-refresh, and off-frustum safety probes are reduced but remain refreshable;
  - requested rays are rounded into small buckets for predictable dispatch.
- The finalized GPU update queue packs the adaptive ray count into the high bits of the existing priority word. CPU queues still write plain priorities and therefore fall back to volume ray counts.
- Trace, blend, and relocate/classify passes all resolve the same per-request ray count and clamp it to `RayCapacityPerProbe`.

Implementation work:

1. Add per-probe inconsistency or variability data.
   - Source can be luminance delta, atlas variance, or a compact history statistic from Phase 3.
2. Feed variability into GPU scheduling.
   - High-inconsistency probes get more rays.
   - Stable probes get fewer rays but still age-refresh.
   - Newly visible/new-cell probes get a temporary ray boost.
   - Off-frustum probes get a reduced ray budget rather than zero.
3. Align rays into small buckets for predictable dispatch.
4. Preserve existing priority floors:
   - visible/local/new-cell
   - dirty regions
   - safety shell
   - age refresh

Acceptance:

- Same or better visual quality at lower average ray count.
- Dynamic lighting/emissive changes converge faster at the same total primary ray budget.
- No starvation in camera-relative clipmap scrolling.

## Phase 6: Improve Probe Representation

Evaluate whether current octahedral atlas is enough or whether Wicked-style SH helps.

Status: hybrid prototype implemented on 2026-07-03.

Decision:

- Keep the current 8x8 irradiance atlas as the production diffuse representation.
- Add optional compact L1-style per-probe metadata behind `DdgiProbeL1MetadataEnabled`.
- Store `vec4(dominantDirection * anisotropy, meanLuminance)` in `GPUDdgiProbeState.RepresentationMetadata`.
- Do not consume this metadata in final lighting yet; it is reserved for diagnostics, directional-bias experiments, adaptive scheduling, and a future SH/L1 A/B benchmark.

Options:

1. Keep current 8x8 irradiance atlas.
   - Lower risk.
   - Focus on normalization, confidence, and history.
2. Add optional SH/L1 representation.
   - Potentially better gather stability.
   - Wicked notes better quality when weighting the whole SH before evaluating.
   - Useful for dominant direction and future glossy approximation.
3. Hybrid:
   - Keep atlas for diffuse.
   - Store compact L1 metadata per probe for diagnostics, directional bias, and adaptive scheduling.

Recommendation:

- Do not switch representation until Phases 1-5 prove the current atlas cannot meet quality targets.
- Prototype SH/L1 behind a setting and benchmark memory, visual stability, and shader cost.

Acceptance:

- Representation change must beat the fixed atlas in visible quality or performance.
- It must not regress thin-wall visibility or authored local volumes.

## Phase 7: Production Scene And Art Controls

Status: implemented on 2026-07-03.

Notes:

- `IndirectIntensity` and `EnvironmentFallbackIntensity` remain the production art controls for final DDGI scale and environment fallback scale.
- `DdgiSelfShadowBiasScale` scales authored normal/view probe self-shadow bias before GPU upload. Default `1.0` is behavior-preserving; the supported range is `0.25..4.0`.
- `DdgiHysteresisResponse` scales probe blend responsiveness before GPU upload by remapping authored hysteresis to blend alpha. Default `1.0` is behavior-preserving; values above `1.0` converge lighting changes faster, while values below `1.0` favor stability.
- No emissive boost was added; emissive validation stays tied to physically authored material/proxy energy rather than a hidden compensation multiplier.
- Sample validation now exposes an explicit Phase 7 production-scene matrix covering Sponza interior, sunlit courtyard, colored room, thin-wall corridor, emissive room, moving rigid object, moving local light, camera teleport/scroll, and outdoor foliage/plaza.
- Optional follow-up: improve the enclosed-room validation scene with an authored local DDGI volume or cascade-0 spacing at or below 1.0 m; it currently relies on camera-relative clipmap coverage.

Expose controls that are useful without encouraging broken tuning.

Controls:

- `IndirectIntensity`: final artistic scale, default 1.0.
- `EnvironmentFallbackIntensity`: fallback only, not a substitute for DDGI.
- `SelfShadowBias`: single artist-facing leak/bias knob if practical.
- `DdgiHysteresisResponse`: optional high-level responsiveness control.
- `DdgiEmissiveBoost`: only if emissive assets are not physically authored.
- Debug-only confidence bypass toggles.

Scene validation:

- Sponza interior.
- Sunlit courtyard.
- Enclosed colored-bounce room.
- Thin-wall corridor.
- Emissive room.
- Moving rigid object.
- Moving local light.
- Camera-relative teleport/scroll.
- Outdoor foliage/plaza.

Acceptance:

- Default `DdgiHigh` is usable without per-scene hidden multipliers.
- Scene-specific settings should change mood, not compensate for missing energy.

## Phase 8: Performance Targets

Status: implemented on 2026-07-03.

Notes:

- Production validation resolves DDGI update P95 budgets from the active DDGI quality tier:
  - `DdgiLow`: 0.75 ms.
  - `DdgiMedium`: 1.0 ms.
  - `DdgiHigh`: 1.5 ms.
  - `DdgiUltra`: 2.5 ms reference/validation tier.
- The DDGI update P95 gate now includes `DdgiSchedulePass`, `DdgiTracePass`, `DdgiBlendPass`, `DdgiRelocateClassifyPass`, and `DdgiPublishPass`.
- Tier memory validation fails if the configured DDGI atlas budget exceeds the tier target: 64 MB, 128 MB, 192 MB, and 384 MB.
- Emergency degradation validation requires reduced work, preserved visible/dirty/new-probe near-field updates, and bounded off-frustum safety work so far-cascade/off-frustum refresh cannot black out near-field bounce.
- Gather-tile costs are not a standalone GPU pass in the current renderer; validation tracks gather tile counts, fallback counts/fractions, and gather tile buffer bytes.

Production budgets should be measured with GPU timestamps and P95, not average-only numbers.

Target budgets:

- `DdgiLow`: less than or equal to 0.75 ms update target.
- `DdgiMedium`: less than or equal to 1.0 ms update target.
- `DdgiHigh`: less than or equal to 1.5-2.0 ms update target for normal scenes.
- `DdgiUltra`: reference/validation tier, allowed up to 2.5 ms.

Implementation work:

1. Keep async compute enabled where overlap is real.
2. Track schedule, trace, blend, relocate/classify, and gather-tile costs separately.
3. Make adaptive budgeting respond to P95 and over-budget streaks.
4. Ensure memory targets remain tier-bounded.
5. Add a "visual emergency degrade" rule:
   - reduce far-cascade rays first
   - reduce off-frustum refresh next
   - preserve near visible probes and dirty/emissive updates

Acceptance:

- No tier exceeds its DDGI memory budget.
- No persistent over-budget DDGI update in default sample scenarios.
- Emergency degradation does not visibly black out near-field bounce.

## Phase 9: Regression Gates

Status: implemented on 2026-07-03.

Notes:

- Forward estimate diagnostics now include average environment fallback weight alongside sampled irradiance, raw DDGI diffuse, final diffuse, effective weight, leak attenuation, and ownership.
- Production validation exposes the Phase 9 regression metric contract in `SampleGlobalIlluminationValidation.Phase9RegressionMetrics` and the required direct/DDGI, confidence-bypass, raw/final, and High/Ultra comparison matrix in `Phase9RequiredComparisons`.
- The production gate fails when healthy raw atlas/sample/blend energy collapses before final diffuse, when visible final diffuse is primarily environment fallback while DDGI raw/effective contribution is weak, when emissive validation scenes have no emissive bounce signal, or when thin-wall/leak scenarios bypass leak attenuation to recover brightness.
- Phase 9 gates are intentionally energy-chain checks. They do not retune DDGI intensity or fallback intensity.

Add gates that catch weak bounce specifically.

Metrics:

- Mean shadowed indirect luminance.
- Mean sunlit indirect luminance.
- Colored bounce chroma ratio.
- Emissive bounce luminance.
- Raw atlas luminance.
- Sampled irradiance before albedo.
- Final DDGI diffuse after albedo.
- Effective DDGI weight.
- Environment fallback weight.
- Thin-wall leak ratio.
- Probe cache warmup frames.
- DDGI GPU P95.
- DDGI memory.

Required comparisons:

- Direct-only vs DDGI.
- DDGI confidence bypass vs normal DDGI.
- Raw atlas vs final indirect.
- `DdgiHigh` vs `DdgiUltra` reference.

Acceptance:

- A change fails if raw atlas luminance is healthy but final DDGI luminance collapses.
- A change fails if final DDGI is bright only because fallback/environment weight increased.
- A change fails if thin-wall leak protection is bypassed to recover brightness.

## Suggested Implementation Order

1. Add the energy-chain debug counters and views.
2. Run the enclosed colored room and Sponza interior.
3. Fix radiance/irradiance convention and double-BRDF issues.
4. Validate atlas integration normalization.
5. Rebalance confidence gates.
6. Add variance-aware history.
7. Add adaptive per-probe ray allocation.
8. Tune production tiers and sample scene settings.
9. Add regression gates.
10. Prototype SH/L1 only if the fixed atlas still cannot meet quality targets.

## Immediate First Patch

The first code patch should not change visual tuning. It should add enough diagnostics to answer:

- Is ray radiance strong?
- Is atlas irradiance strong?
- Is sampled irradiance strong before albedo?
- Is final DDGI diffuse weak only after final composition?
- Which confidence or visibility term suppresses the result?

Only after that data exists should brightness-affecting shader math or settings be changed.

## Addendum: RC Cleanup Estimator Discipline

Status: implemented on 2026-07-03.

The RC stabilization pass did not add push constants, GPU struct fields, or new public render-setting knobs. Thresholds and scheduler weights remain shader constants unless the production gate proves that per-scene overrides are required.

The visibility atlas estimator now preserves the plan's reference requirement that probe rays use spherical Fibonacci directions with a random orientation. The gather path keeps the same cosine-power lobe but evaluates the `cosTheta^50` weight with explicit multiplies instead of `pow`, avoiding driver-dependent transcendental cost in the hot inner loop while retaining the estimator shape and normalization behavior.
