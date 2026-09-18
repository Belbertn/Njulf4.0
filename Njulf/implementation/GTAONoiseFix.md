# Fix the GTAO noise fix (after a1468e0)

## Context

`a1468e0` implemented the four steps from `implementation/Complete/GTAO2.md`. The
noise remains. Review found Steps 0 and 1 correct, Step 2 and Step 3 incorrect,
Step 4 half-right.

The dominant defect is Step 2: **the new dither is not random.** Evaluating
`GtaoDither` (`gtao.comp:88-95`) over a 40x40 block:

```
dither.y vs dither.x : mean offset 0.605, stdev 0.008 - 2 distinct values (0.598, 0.616)
per-frame advance    : mean 0.294,  stdev 0.003 - 2 distinct values (0.293, 0.310)
```

Both "decorrelations" are constants:

- `dot((5.588238, 5.588238), k) = 0.4076`, which after the outer `x52.98`
  becomes a fixed +0.598, so `dither.y == fract(dither.x + 0.598)` at every
  pixel. The slice angle and the radial offset are one number, not two.
  `5.588238` is Jimenez's *temporal* animation constant; used as a spatial
  offset it only shifts phase.
- `dot(phase * (0.618034, 0.309017), k) = 0.04328 * phase` is a global constant,
  so every pixel's dither advances by the same 0.293 each frame. The spatial
  pattern is rigidly translated in value-space; pixel-to-pixel *relative*
  ordering never changes. A temporal filter cannot average out a pattern that
  is identical every frame, so Step 0's restored `MaximumHistoryAge = 32` /
  `StableHistoryWeight = 0.92` buys nothing against it.

Step 3 then converts whatever variance survives into shading-normal noise,
because its frame transfer is not rotation-consistent.

## Plan

### Step A — Replace the dither with a stratified spatiotemporal pattern

`Njulf.Shaders/gtao.comp`. Drop `InterleavedGradientNoise` / `GtaoDither`
(`:77-95`) entirely. IGN is a gradient noise with strong diagonal structure,
built for 1-bit dithering under a spatial filter; mapped onto a 4-slice angle it
prints angular banding. Use the Activision/XeGTAO scheme instead — no texture,
no asset, and exactly stratified over the denoiser kernel:

```glsl
// Spatial: a 4x4 tile in which all 16 pixels take distinct direction and
// offset indices, so any 4x4 neighbourhood the spatial filter covers is a
// complete stratum. Temporal: independent rotation and offset sequences so
// each pixel walks a different path through the set frame to frame.
float directionNoise = (1.0 / 16.0) *
    float((((pixel.x + pixel.y) & 3) << 2) + (pixel.x & 3));
float offsetNoise = (1.0 / 4.0) * float((pixel.y - pixel.x) & 3);
uint rotationIndex = temporalIndex % 6u;   // 6 rotations
uint offsetIndex   = (temporalIndex / 6u) % 4u;  // x 4 offsets = 24-frame period
directionNoise = fract(directionNoise + float(rotationIndex) * (1.0 / 6.0));
offsetNoise    = fract(offsetNoise    + float(offsetIndex)  * 0.25);
```

`directionNoise` drives `rotation` (`:370`), `offsetNoise` drives `stepDither`.
The two are independent by construction. Feed `temporalIndex` from
`sceneData.TemporalSampleIndex` unmasked (drop `& 63u` at `:369`) so the 24-frame
period is the only one in play.

**Add a guard test so this class of bug cannot recur.** `GtaoImplementationTests`
cannot run GLSL, but a C# mirror of the index arithmetic can assert what the
review had to measure by hand: over a 64x64 block, (a) the direction and offset
channels have low rank correlation, (b) every 4x4 tile contains 16 distinct
direction indices, (c) consecutive frames do not advance every pixel by the same
delta. Two failed attempts justify the fixture.

### Step B — Fix the radial step distribution

Same file, `SearchHorizonCos` (`gtao.comp:250-252`).

- **Range.** `fraction = (float(step) + stepDither) * inverseStepCount` with
  `step` in `[0, n)` tops out at `(n-1+dither)/n`, so the outermost sample never
  reaches the configured radius; since `pixelDistance` is quadratic in
  `fraction`, the effective AO radius swings per pixel between ~69% and 100% at
  `stepCount = 6` — a per-pixel radius modulation that is itself noise. Use a
  stratified jitter that keeps the original outer anchor:
  `fraction = (float(step) + 1.0 - stepDither) * inverseStepCount`
  — each step jitters inside its own `[step/n, (step+1)/n]` interval, mean
  `(step + 0.5)/n`, and the outer step still reaches 1.0.
- **Near-field clamp.** `pixelDistance = max(1.0, projectedRadiusPixels * fraction * fraction)`
  clamps the *sample distance*, so for small dither values the first one or two
  steps land on the identical texel for many pixels and the near field — where
  AO contrast is highest — is effectively undithered. Floor the *step spacing*
  instead, as Prowl does (`GTAO.shader:100-105`):
  `pixelDistance = max(projectedRadiusPixels * fraction * fraction, (float(step) + stepDither) * 1.5)`
  This keeps successive samples >=1.5 texels apart while scaling with the dither
  rather than collapsing it.

### Step C — Make the bend transfer rotation-consistent

The encoder builds a tangent frame from `footprint.referenceNormal`
(`gtao_spatial.comp:562`), the decoder rebuilds it from `viewShadingNormal`
(`forward_surface_shading.glsl:1479`). `ResolveReferenceFrame`
(`gtao_spatial.comp:70-77`, mirrored at `forward_surface_shading.glsl:1431-1438`)
anchors the tangent to a global axis, so it is not parallel transport: the
frame's twist about the normal depends on the normal. Two different normals give
two differently-twisted frames, so the decoded azimuth points elsewhere — the
reconstruction error Step 3 removed from elevation, reintroduced in azimuth, and
on a normal-mapped curtain it varies per pixel. The
`abs(normal.y) < 0.99` axis switch adds a hard ~90 degree flip whenever encoder
and decoder land on opposite sides of it, which sweeps the image as the camera
pitches.

A delta cannot be transferred exactly without both normals, so publish the
reference normal:

1. **New render target `GtaoReferenceNormal`** — full res, `R16G16_SFLOAT`,
   sampled+storage. Add it in `RenderTargetManager.cs` beside `GtaoFiltered`
   (`:306-311`, recreate path `:1243-1279`) and declare it in
   `ProductionRenderPipelineDeclaration.cs` with `SceneResolution` (`:1568`
   pattern), written by `GtaoSpatialPass` and read by `ForwardPlusPass`
   (`:379-390`, `:442-443`).
2. **`gtao_spatial.comp`** — add storage binding 7 and write
   `EncodeOctahedral(footprint.referenceNormal)` to it. Revert `GtaoFiltered.xy`
   to the absolute view-space bent normal (`:562-568`), which also restores debug
   view 9 unchanged. Delete `ResolveReferenceFrame`.
3. **`GtaoPasses.cs`** — `Binding(7, StorageImage)` in the `GtaoSpatialPass`
   layout (`:645-652`), the descriptor in `RewriteDescriptors()` (`:705-738`),
   a `TransitionToShaderRead` in `Execute()` (`:700-702`), and a new
   `BindlessIndex.GtaoReferenceNormalTexture` registered in `ShouldExecute()`
   (`:668-676`).
4. **`forward_surface_shading.glsl:1424-1510`** — sample both textures, then do
   shortest-arc instead of a tangent frame:
   - `bendAngle = acos(dot(referenceNormal, bentNormal))`, clamped to
     `GTAO_BENT_NORMAL_MAX_BEND_ANGLE` and scaled by `payload.w * (1 - payload.z)`
     exactly as now.
   - `rotationAxis = normalize(cross(referenceNormal, bentNormal))` — the bend's
     own axis, in view space, independent of any global pole.
   - Rodrigues-rotate `viewShadingNormal` about that axis by `bendAngle`, then
     back to world. Keep the existing `dot(rotated, shadingNormal) <= 0`
     rejection.
   Delete `ResolveGtaoReferenceFrame` and the `localBentNormal.z <= 0` gate.

### Step D — Bound the curvature-relaxed threshold

`gtao_temporal.comp:164-186`. `relaxedNormalThreshold = min(pc.NormalThreshold,
minimumNormalAgreement)` relaxes in the right direction, but is driven by the
`min` over the 3x3 with no floor: one bad half-res normal — exactly the
reconstruction noise this is meant to tolerate — drags the threshold toward 0,
which accepts history from genuinely different surfaces, and the threshold
itself then flickers per pixel per frame.

- Replace `min` with the **mean** agreement over the depth-compatible neighbours
  (accumulate a sum and a count in the loop already there).
- Floor the result: `relaxedNormalThreshold = clamp(min(pc.NormalThreshold, meanAgreement), 0.5, pc.NormalThreshold)`.

### Step E — Tuning pass (only after A-D, and measure each)

The remaining noise is raw variance. `GtaoQualityPreset = High` (6 directions x 8
steps vs Balanced's 4 x 6, `AmbientOcclusionSettings.cs:115-127`) roughly halves
it at a real GPU cost. Treat as a default change only if A-D leave visible
residue.

## Files

| File | Steps |
|---|---|
| `Njulf/Njulf.Shaders/gtao.comp` | A (`:77-95`, `:369-370`), B (`:250-252`) |
| `Njulf/Njulf.Shaders/gtao_spatial.comp` | C (`:70-77`, `:562-568`, new binding 7) |
| `Njulf/Njulf.Shaders/forward_surface_shading.glsl` | C (`:1424-1510`) |
| `Njulf/Njulf.Shaders/gtao_temporal.comp` | D (`:164-186`) |
| `Njulf/Njulf.Rendering/Pipeline/GtaoPasses.cs` | C (layout, descriptors, transition, bindless) |
| `Njulf/Njulf.Rendering/Resources/RenderTargetManager.cs` | C (`GtaoReferenceNormal`) |
| `Njulf/Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs` | C (resource + pass edges) |
| `Njulf/Njulf.Tests/GtaoImplementationTests.cs` | A (dither guard fixture) |

Untouched: Steps 0 and 1 of `a1468e0` were implemented correctly — the restored
bilateral constants (`gtao_spatial.comp:33-40`), the temporal budget
(`GtaoPasses.cs:549-557`), the depth-weighted reference normal, the nearest-tap
confidence-0 fallback and the `smoothstep` gates all stay as they are.

## Verification

1. `dotnet test Njulf/Njulf.Tests` — the new dither fixture must fail against the
   current `GtaoDither` and pass after Step A. That is the check this round
   lacked.

All work on branch `Simplified-SDF`.