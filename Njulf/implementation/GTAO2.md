# Fix GTAO noise / crisscross pattern (after 8f93578)

## Context

`8f93578` ("GTAO not blurry but has crisscross pattern") completed the anti-blur
work: `gtao_spatial.comp` gained a real depth-aware upsample keyed on full-res
`SceneDepth`, `TryResolveIndirectDiffuseNormal` became a bounded Rodrigues
rotation instead of a normal replacement, and the temporal pass was tightened
(`MaximumHistoryAge` 32→8, `StableHistoryWeight` 0.92→0.85, point-sampled motion,
bent-normal neighbourhood clamp). The blur is gone.

What replaced it is lattice-aligned speckle, worst on the curtains. That is not a
coincidence: **the blur was hiding the raw trace's noise, and every change in
that series either sharpened the reconstruction or cut the averaging budget.**
`gtao.comp` — where the noise is actually generated — was never touched.

The curtains are the worst case because they are high-curvature cloth lit almost
entirely by indirect light. Every gate below keys on a half-resolution,
depth-reconstructed geometric normal, which on folded cloth swings fast and
lands right on the rejection thresholds.

## Why it is noisy

Ranked by contribution.

### 1. The new upsample makes hard, discontinuous decisions on half-res data

`gtao_spatial.comp` now takes three *binary* decisions per full-res pixel, all
driven by half-res geometry. Binary decisions on a half-res lattice produce
lattice-aligned speckle — the crisscross:

- **Winner-take-all reference normal.** `ResolveFootprint()` (`:218-250`) picks
  `footprint.referenceNormal` by argmax over the 2×2 tap set. As the bilinear
  `fraction` crosses 0.5, a different tap wins, so adjacent full-res pixels
  inside one half-res quad can adopt *different* reference normals — and every
  later gate is measured against that reference.
- **Hard neutral-AO fallback.** When no tap survives depth rejection,
  `EmitNeutralAo()` (`:183-188`, called at `:394`, `:401`, `:409`) writes
  `AO = 1.0, confidence = 0` — a fully-white pixel adjacent to fully-occluded
  ones. There is no soft fallback.
- **Hard tap rejects** in the accumulation pass (`:276-284`): depth, then
  `normalAgreement < GTAO_NORMAL_REJECTION`, then `dot(tapBent, ref) <= 0`.

And the thresholds were tightened at the same time, so these gates fire far more
often than before (`gtao_spatial.comp:36-40`):

| Constant | Before | Now | Effect |
|---|---|---|---|
| `GTAO_NORMAL_REJECTION` | 0.50 | **0.80** | rejects taps >37° apart |
| `GTAO_DEPTH_REJECTION_SCALE` | 4.0 | **2.0** | half the depth tolerance |
| `GTAO_DEPTH_RELATIVE_SCALE` | 0.04 | **0.015** | ~2.7× tighter with distance |
| `GTAO_NORMAL_WEIGHT_EXPONENT` | 0.125 | **0.25** | `NormalSigma 32` ⇒ `agreement^8` (was `^4`) |

`agreement^8` is 0.27 at 0.85 agreement and 0.43 at 0.9 — on a curtain fold the
surviving taps are down-weighted almost to nothing, so the filter degenerates
into a passthrough of one noisy sample.

### 2. The averaging budget was cut in the same commit

`GtaoPasses.cs:549-556`: `MaximumHistoryAge` 32→8, `StableHistoryWeight`
0.92→0.85. `NormalThreshold` stayed at **0.85**, which on curved half-res
normals rejects history outright (`gtao_temporal.comp:270-274`). Less temporal
accumulation + a sharper spatial filter = the raw trace now reaches the screen.

### 3. The raw trace's dither is lattice-prone and under-sampled (`gtao.comp`, untouched)

- **`Hash()` (`:73-83`) is `x*A ^ y*B ^ z*C` plus a finalizer.** XOR-of-products
  correlates every pixel sharing a row or a column — this construction is a
  known source of axis-aligned grid structure. It is the most literal candidate
  for a "crisscross" pattern.
- **Only the slice angle is dithered** (`:356-357`). The radial step positions
  are identical for every pixel — `fraction = (step + 1) * inverseStepCount`,
  `pixelDistance = projectedRadiusPixels * fraction²` (`:237-239`) — so ring and
  band structure never averages out. Prowl dithers both: `dither.x` drives
  `slicePhi`, `dither.y` drives the step (`GTAO.shader:111`, `:131`).
- **Only 8 temporal phases**, and `pc.FrameIndex & 7u` feeds *both* the hash's
  `z` and the golden-ratio addend — the spatial and temporal terms are
  correlated, not independent.
- **Balanced preset = 4 directions × 6 steps** (`AmbientOcclusionSettings.cs:115-127`).

Prowl uses a blue-noise texture tiled 1:1 per pixel and scrolled by a per-frame
Halton offset (`GTAO.shader:42-44`, `:171-173`) precisely to avoid this.

### 4. The bend measures against the wrong reference normal

`TryResolveIndirectDiffuseNormal` (`forward_surface_shading.glsl:1424-1479`)
computes `bendAxis = cross(geometricNormal, worldBentNormal)` using the forward
pass's **full-res interpolated mesh normal**. But the bent normal was produced
relative to the **half-res depth-reconstructed** geometric normal
(`gtao.comp:330`). The difference between those two vectors is not an occlusion
bend — it is reconstruction error, largest exactly on curved geometry — and the
rotation feeds it straight into the indirect-diffuse lobe.

## Is it just a settings adjustment?

Partly, and it is worth trying first because it is nearly free — but settings
alone cannot remove the lattice-aligned component, which comes from the argmax /
hard-reject structure (§1) and the hash (§3). Do Step 0, then Steps 1–4.

## Plan


### Step 1 — Make the upsample continuous

In `Njulf.Shaders/gtao_spatial.comp`, replace each hard decision with a soft one.
The goal is that no output pixel's value can flip discontinuously as the
bilinear `fraction` crosses a tap boundary.

- **`ResolveFootprint()` (`:218-250`)** — drop the argmax. Accumulate the
  reference normal as a weighted sum over the surviving taps (`bilinear ×
  Gaussian(depthDifference, depthSigma)`), normalize, and use that as
  `footprint.referenceNormal`. Keep the per-tap depth rejection, but the
  *reference* must vary continuously across the quad.
- **`EmitNeutralAo()` fallback at `:406-415`** — when no tap survives, fall back
  to the nearest tap's payload with `confidence = 0` instead of white
  `AO = 1.0`. Confidence 0 already disables the bend downstream
  (`forward_surface_shading.glsl:1444`) and reduces temporal trust, so the
  fallback need not also punch a hole in the AO. Keep the true sky/invalid-depth
  path at `:392-396` and `:399-404` as-is.
- **Accumulation rejects (`:276-284`)** — convert the `normalAgreement` hard
  floor into a smooth falloff (`smoothstep(floor, floor + 0.2, agreement)`) so a
  tap fades out instead of vanishing. Same for the depth gate: keep a generous
  hard cutoff for genuinely different surfaces, but let the Gaussian do the work
  inside it.

### Step 2 — Fix the dither in `gtao.comp`

- Replace `Hash()` (`:73-83`) with interleaved gradient noise, which is
  lattice-free, needs no texture, and is one short instruction chain:
  `fract(52.9829189 * fract(dot(vec2(pixel) + temporalOffset, vec2(0.06711056, 0.00583715))))`.
  (A blue-noise texture as Prowl uses would be better still, but it needs a new
  binding and an asset; IGN is the minimal change that removes the grid.)
- **Dither the radial step as well as the slice angle.** Derive a second,
  decorrelated value and pass it into `SearchHorizonCos` so
  `fraction = (float(step) + stepDither) * inverseStepCount` (`:237`), matching
  `GTAO.shader:131`. Without this the sampling radii are identical for every
  pixel and the residual is structured, not noise.
- **Decorrelate the temporal term** (`:356-357`): stop feeding
  `pc.FrameIndex & 7u` into both the hash input and the golden-ratio addend, and
  widen the period (16 or 64 phases) now that the temporal filter accumulates
  again.

### Step 3 — Measure the bend against the normal GTAO actually used

Remove the reference-normal mismatch without adding a binding or growing the
payload: have `gtao_spatial.comp` encode the filtered bent normal **in the local
frame of `footprint.referenceNormal`** (which it already holds in registers)
before writing `GtaoFiltered.xy`, and have
`TryResolveIndirectDiffuseNormal` decode that as a tangent-space delta and apply
it to the fragment's own shading normal.

This makes the `geometricNormal` argument added to the signature in `8f93578`
unnecessary — the delta is already relative — so revert that parameter and the
`forward.frag:1552-1555` call site along with it. Update the debug views that
decode `GtaoFiltered.xy` (`FilteredGtaoBentNormal`, view 9) to match the new
encoding.

If Step 3 looks too invasive after Step 0, the interim mitigation is to add a
`smoothstep` deadzone to `bendAngle` so lightly-occluded (and therefore noisiest)
pixels get no bend at all.

### Step 4 — Re-validate the temporal gates on curved geometry

`gtao_temporal.comp:264-274` rejects history on `abs(previousDepth - viewDepth) >
max(0.02, viewDepth * 0.03)` or `dot(previousNormal, normal) < NormalThreshold`.
Both compare half-res depth-reconstructed quantities. Make the normal threshold
curvature-aware — derive it from the local neighbourhood spread already computed
in `NeighborhoodEnvelope()` (`:145-200`) rather than using one global constant —
so flat walls stay strict while cloth folds keep accumulating.

## Files

| File | Steps |
|---|---|
| `Njulf/Njulf.Shaders/gtao_spatial.comp` | 0 (constants `:36-40`), 1 (`ResolveFootprint` `:218-250`, `EmitNeutralAo` `:183-188`, accumulation `:276-289`), 3 (payload encoding) |
| `Njulf/Njulf.Shaders/gtao.comp` | 2 (`Hash` `:73-83`, `SearchHorizonCos` `:237`, rotation `:356-357`) |
| `Njulf/Njulf.Rendering/Pipeline/GtaoPasses.cs` | 0 (temporal push constants `:549-556`) |
| `Njulf/Njulf.Shaders/gtao_temporal.comp` | 4 (`:264-274`, `NeighborhoodEnvelope` `:145-200`) |
| `Njulf/Njulf.Shaders/forward_surface_shading.glsl` | 3 (`TryResolveIndirectDiffuseNormal` `:1424-1479`) |
| `Njulf/Njulf.Shaders/forward.frag` | 3 (call site `:1552-1555`) |

## Verification

1. `dotnet test Njulf/Njulf.Tests` — `GtaoImplementationTests` was updated in
   `8f93578` and pins shader substrings; re-check it after Steps 2 and 3.


All work on branch `Simplified-SDF`.