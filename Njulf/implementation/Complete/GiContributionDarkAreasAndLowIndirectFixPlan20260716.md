# GI Contribution: Dark Top-Left Region + Globally Low Indirect Lighting — Diagnosis and Fix Plan (2026-07-16)

> Design correction: the proposed Sponza-specific authored receiver layout was rejected. The production
> Sponza scene must use the generic camera-relative `DdgiHigh` rings with zero authored volumes. Authored
> volumes remain standalone, explicitly placed local quality overrides. Coverage failures found here must
> therefore be fixed in the generic ring placement, gather, fallback, or transport path.

## Inputs

Five paired captures from the Sponza courtyard (2026-07-16 06:35:27–06:35:39, `Development 1080p60`,
quality `DdgiHigh`, scheduler `Gpu`):

| Capture | Debug view |
| --- | --- |
| 06:35:27 | `None` (final render) |
| 06:35:32 | `DdgiGatherClipmap` |
| 06:35:35 | `DdgiGatherBlendWeight` |
| 06:35:37 | `DdgiGatherFallback` |
| 06:35:39 | `DdgiSupportCoverage` |

Reported symptoms:
1. The top-left colonnade/gallery area is far too dark (near black) in the final render.
2. Indirect lighting is too low everywhere.

## Measured evidence (from the capture diagnostics)

| Counter | Value | Reading |
| --- | --- | --- |
| `DdgiForwardEstimateSampledIrradianceLuminance` | 0.141 | Gathered probe irradiance is already low |
| `DdgiForwardEstimateFinalDiffuseLuminance` | 0.0086 | Final indirect diffuse is essentially black (consistent with irradiance × albedo/π, so composition math is fine — the input energy is what is missing) |
| `SimpleDdgiAverageSkyVisibility` | 0.0067 | The far-field sky-visibility gate reports ~0 sky almost everywhere it is sampled |
| `DdgiForwardEstimateEnvironmentFallbackWeight` | 0.00026 | Environment fallback almost never engages on the sampled pixels |
| `SimpleDdgiSecondVolumeGatherCount / GatherSampleCount` | 70% | Most pixels need the second (coarser) volume |
| `DdgiAverageDataConfidenceEstimate` | 0.41 | Directional support is weak on average |
| `SimpleDdgiInactiveProbeCount` | 6 281 / 15 383 (41%) | A large share of probes classified inactive (inside geometry) |
| `DdgiSimpleTraceMissCount` | ~13–19% of rays | Sky misses are a meaningful share of probe rays |
| `scheduler.policy.effectiveRequestBudget` | 207 (configured 2048), reason `FeedbackReducedBudget` | Probe refresh is heavily throttled; P95 probe age ≈ 120–154 frames |

Debug view reading (colors decoded from `forward.frag` debug writers):
- `DdgiGatherBlendWeight` (grayscale = `secondaryContributionWeight`): **white almost frame-wide** —
  the coarser fallback volume carries ~100% of the gathered energy nearly everywhere. The fine
  (1 m authored / near-ring) volumes contribute almost nothing anywhere.
- `DdgiGatherFallback` (red = second volume used): red across most receiver surfaces, matching the 70% counter.
- `DdgiSupportCoverage` (grayscale = `validSupport`): **black exactly over the too-dark colonnade region**
  (and other dark pockets) — zero valid probe support there, in either gathered volume.
- `DdgiGatherClipmap`: near ring (volume 4, pale blue-white) and mid ring (volume 5, salmon) split the
  frame; the dark colonnade is mostly **black = no ring contributed at all** there.

## Root causes

### A. GI sees the sky at 0.10× while the camera sees it at 0.45× (primary cause of globally low indirect)

The scene profile (`NjulfHelloGame/SampleSponzaGlobalIlluminationProfile.cs:330-332`) sets:

```
settings.Environment.SkyIntensity     = 0.45f;  // what the camera sees (skybox.frag:45: sky * SkyIntensity)
settings.Environment.DiffuseIntensity = 0.10f;  // IBL ambient knob, deliberately low to avoid double-counting with DDGI
```

But every GI energy path samples the environment with **`DiffuseIntensity`**, not `SkyIntensity`:

- Probe-ray **miss radiance**: `ddgi_hit_shading.glsl:256-270`
  (`SampleDdgiEnvironmentMissRadianceWithFallback` → `environmentRadiance * environment.DiffuseIntensity`),
  called from `ddgi_simple_trace.comp:115`.
- Probe **bounce fallback** at hit points: `ddgi_simple_shared.glsl:1101-1113`
  (`SimpleDdgiEnvironmentIrradianceFallback` → irradiance cubemap × `DiffuseIntensity`).
- Forward **environment fallback** where DDGI lacks ownership: `forward.frag:2709`
  (`diffuseIbl = diffuseWeight * albedo * irradiance * environment.DiffuseIntensity`).

The procedural sky cubemaps are baked at unit intensity (`EnvironmentManager.cs:257-259`), so the sky
radiance feeding GI is 0.10/0.45 ≈ **22% of the sky radiance the viewer actually sees**. In this courtyard
the shadowed surfaces are lit almost entirely by sky + bounce, so the entire indirect field is ~4–5× too dark
before any other losses. Multi-bounce compounding makes the deficit worse than linear.

`DiffuseIntensity = 0.1` is correct **as a screen-space ambient-IBL knob** (its job is to not double-count
ambient where DDGI owns the pixel). It is wrong as the physical sky radiance for rays: the ray *is* the
transport, there is nothing to double count.

### B. Second-bounce feedback is starved (secondary cause of low indirect)

At probe-ray hit points, bounce = probe irradiance × ownership + environment fallback × (1 − ownership),
and the environment share is additionally multiplied by `EstimateFarFieldSkyVisibility(worldPos)`
(`ddgi_simple_shared.glsl:1396-1408`). Measured average sky visibility is **0.0067** — the 3 upward cones
(`ddgi_simple_shared.glsl:1115-1159`) traced through the coarse far-field voxelization (base voxel
0.75–2 m) from a point offset only 3 cm–maxDistance·2% above the hit surface are blocked essentially always.
Consequence: wherever a hit point has low probe ownership (outside ring coverage, unsupported cells,
Y above/below ring spans), its bounce contribution is ~0, so multi-bounce light never accumulates and probes
in shadowed pockets converge to near-black. This also explains the high
`DdgiSimpleTraceZeroRadianceHitCount` (7–11% of hits).

### C. Fine-volume support collapse (primary cause of the black top-left region, and of coarse-only GI everywhere)

Per-corner acceptance in the gather (`ddgi_simple_shared.glsl:1005-1036`) rejects a probe entirely when
`visibilitySelectionWeight = smoothstep(0.01, 0.08, chebyshev)` is ~0. The Chebyshev estimate
(`ddgi_simple_shared.glsl:681-698`) floors variance at `spacing² × 0.0005` (= 0.0005 for the 1 m volumes), so
a probe whose visibility mean underestimates the receiver distance by only ~0.15–0.3 m already fails
selection. In dense trim-heavy geometry (columns, arches, balustrades) this rejects most 1 m-volume probes
for most receivers:

- Frame-wide effect: fine volumes contribute ~nothing (BlendWeight view ≈ white, 70% second-volume gathers,
  data confidence 0.41), so effective GI resolution degrades to the 3 m mid ring — soft, leaky, dim.
- Local catastrophic effect: in occluded pockets (the top-left colonnade) **all eight corners of both the
  primary and fallback volume fail**, `validSupport → 0` (SupportCoverage view is black there),
  `ownership → 0`, and the pixel falls through to the environment fallback…

…which is scaled by `DiffuseIntensity = 0.1` (root cause A), i.e. ~10% of intended skylight, times AO.
The region therefore renders nearly black instead of "sky-lit ambient". Two bugs compound.

Contributing coverage seams (to verify at runtime, see Phase 3):
- Authored volumes (`SampleSponzaGlobalIlluminationProfile.cs:68-117`): the hero volumes only start at
  **Y = 9**, and the `BalconyArcade` transition volume spans only the central band
  (X −6.75…9.75, Z −6.5…11.5, Y 4.5…9). The left/right gallery interiors (X < −7.5 or X > 9.75, Y < 9)
  have **no authored coverage** and rely entirely on the camera rings.
- Near ring (19×10×19 @ 1 m) is receiver-anchored at Y = 4.5 → covers roughly Y 0…9; geometry above 9
  outside hero boxes and below the ring's XZ extent drops to the 3 m mid ring immediately.
- 41% of probes are inactive (inside geometry). Correct per se, but with hard gather rejection they
  combine into zero-support cells.

### D. Secondary observations (not this fix, but track)

- The GPU scheduler feedback (`FeedbackReducedBudget`, target 1875 µs) collapses probe updates
  2048 → ~207/frame with 11.5 k probes pending visible-retry; P95 probe age is ~150 frames. Slow
  convergence amplifies every issue above during lighting/camera changes.
- 70% double gathers + detailed-counter atomics inflate the forward GI cost
  (`GpuForwardGiGatherMicroseconds` ≈ 10 ms inclusive). Fixing support collapse raises the early-out rate
  (`secondVolumeOwnershipEarlyOutThreshold = 0.95`) and pays some of the fix cost back.

## Fix plan

### Phase 1 — Radiometric sky consistency (small diff, biggest win)

1. Add a distinct GI sky scale, plumb `SkyIntensity` to the GI paths:
   - `GPUEnvironmentData` already carries `SkyIntensity`; change
     `SampleDdgiEnvironmentMissRadianceWithFallback` (`ddgi_hit_shading.glsl`) to multiply the cubemap by
     `SkyIntensity` (the radiance the camera sees) instead of `DiffuseIntensity`.
   - Same change in `SimpleDdgiEnvironmentIrradianceFallback` (`ddgi_simple_shared.glsl:1101-1113`) — this
     is bounce/probe-side energy, not screen ambient.
   - `SimpleDdgiVolumeManager.cs:1054` and `DdgiPipelinePasses.cs:290` currently upload
     `Environment.DiffuseIntensity` as `environmentIntensity` for the no-cubemap fallback path — switch to
     `SkyIntensity` for parity.
2. Keep `forward.frag` `diffuseIbl` (screen-space ambient fallback) on `DiffuseIntensity` — that is the
   knob's actual job — but see Phase 2/3 for reducing how often pixels land there.
3. Re-tune check: with sky ×4.5, `AverageSampledIrradianceLuminance` should rise from ~0.14 to roughly
   0.35–0.6 in this scene. If the scene then reads too bright, adjust `SkyIntensity` (which also brightens
   the visible sky, keeping GI consistent) — never re-dim GI through `DiffuseIntensity`.

### Phase 2 — Stop hard-rejecting partially occluded fine probes

Goal: fine volumes should degrade smoothly (leak-aware weighting) instead of dropping to zero support.

1. In `SampleSimpleDdgiVolumeGather` (`ddgi_simple_shared.glsl:1005-1036`):
   - Convert `visibilitySelectionWeight` from a de-facto validity gate into a weight with a floor, e.g.
     `max(smoothstep(LOW, HIGH, v), 0.05)` for corners that pass the state checks
     (`SimpleDdgiProbeSupportsGather`), so a cell with only partially-visible probes retains support
     instead of collapsing to environment fallback. Keep `transportVisibility` itself as the leak
     suppressor inside `transportWeight` (it already multiplies the irradiance share).
   - Raise the Chebyshev variance floor for fine spacings: `spacingFloor = max(0.0005, spacing² * 0.005)`
     (or scale `meanBound` up); today a 0.2 m mean error at 1 m spacing yields weight ≈ 0.002 which is
     below the selection band.
2. Re-check `SIMPLE_DDGI_VISIBILITY_SELECTION_LOW/HIGH (0.01/0.08)` after the floor change with the
   leak-test scenes (`SimpleDdgiReceiverCoverageValidator`, thin-wall tests) — the goal is: no through-wall
   leak regressions, but support coverage never hits 0 on open receivers.
3. Acceptance: `DdgiGatherBlendWeight` view should show the fine volume owning the near field (mostly dark
   gray/black near camera), `SecondVolumeGatherCount` fraction well under 30%, `SupportCoverage` view no
   longer black on the colonnade, `DdgiAverageDataConfidenceEstimate` > 0.7.

### Phase 3 — Validate the generic camera-relative layout around the galleries

1. Confirm attribution at runtime with `DdgiGatherClipmap`, `DdgiGatherFallback`, and the probe-volume
   overlay. `DdgiGatherLocalVolume` must remain empty in canonical Sponza captures.
2. Keep Sponza on the unmodified `DdgiHigh` near/mid/far ring dimensions and camera-relative vertical
   policy. Do not add fixed façade, gallery, arcade, or transition volumes to make the validation pass.
3. If the first two ring candidates produce zero visible mass, improve the generic fallback chain so a
   containing coarser ring can contribute rather than falling directly to ambient.
4. Acceptance: the fixed Sponza receiver regions remain covered by near or mid camera rings throughout
   the low/high traversal, with no black pockets in `GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT`.

### Phase 4 — Unstarve the bounce feedback

1. Fix `EstimateFarFieldSkyVisibility` (`ddgi_simple_shared.glsl:1115-1159`): start the cones at least one
   far-field voxel above the surface (`worldPos + normal * max(voxelSize, …)`), or evaluate it at the probe
   position rather than the hit surface. Add a floor (e.g. 0.1) so the environment share of the bounce is
   attenuated, never annihilated. Alternative: drop the gate for the bounce fallback entirely and rely on
   `(1 − ownership)` — the far-field trace already exists for leak control elsewhere.
2. Re-measure `SimpleDdgiAverageSkyVisibility`; expect a courtyard average in the 0.2–0.6 range, and
   `DdgiSimpleTraceZeroRadianceHitCount` to drop well under 5%.
3. This phase multiplies with Phase 1 (the fallback it re-enables is the sky energy Phase 1 corrects).

### Phase 5 — Validation, tuning, and guardrails

1. Re-run the locked capture workflow (`SampleSponzaGiCaptureHarness`, `docs/RealtimeGiSponzaCapture.md`)
   at the same five debug views + `DdgiSampledIrradiance`, `DdgiFinalDiffuse`, `FinalIndirect`, and compare
   counters before/after each phase (each phase lands separately so its effect is attributable).
2. Shader-mirror tests: update `SimpleDdgiShaderMirrorTests` / `DdgiShaderModelMirrorTests` for any constant
   or params change (variance floor, selection band, new intensity plumb).
3. Perf guardrails: GI GPU budget 2.5 ms — verify `SecondVolumeGather` fraction drop offsets Phase 2/3 cost;
   re-check the scheduler feedback (`effectiveRequestBudget` 207 vs configured 2048) after gather cost falls,
   and file the scheduler-starvation investigation separately if it stays pinned at ~200.
4. Leak regression: thin-wall and room-isolation validation scenes must stay green
   (`DdgiRoomVolumeValidator`, receiver-coverage validator) since Phases 2/4 soften rejection.

## Expected outcome

- Phase 1 alone: indirect field ×3–5 brighter in sky-driven areas; colonnade goes from near-black to dim.
- Phases 2+3: colonnade and other occluded pockets are lit by actual camera-ring probe data (no support
  holes); near-field detail returns without scene-specific authored coverage.
- Phase 4: shadowed interiors gain the missing second-bounce energy; probes converge brighter and more
  stable.
