# SSAO Angle Recurrence Implementation Plan

Last updated: 2026-08-24

Design reference: isolated candidate commit `5c61d54eadb4b2f1edeb8aac48776d345cb89a5c` (`Use recurrence for SSAO spiral samples`). Reimplement the optimization against the current tree rather than cherry-picking it. The current tree already contains the prerequisite center-depth/view-position reuse from `dc12fca`.

## 1. Required outcome

Replace the per-sample sine/cosine evaluation used to generate SSAO golden-angle spiral directions with a two-dimensional rotation recurrence.

The optimized shader must:

- preserve the current sample radius, elevation, ordering, spatial jitter, bias, rejection, weighting, and AO output contracts;
- remove trigonometric evaluation from `HemisphereSample` and from the per-tap loop;
- retain only the existing once-per-pixel sine/cosine pair used by `BuildBasis`;
- remain numerically equivalent for every supported sample count: 4, 8, 16, and 32;
- produce no visible temporal instability or material AO change;
- be retained only if the existing Bistro/Sponza performance campaign proves a target-pass or whole-frame win without a quality or non-target regression.

This is a shader micro-optimization. It must not introduce a runtime toggle, new setting, shader variant, push constant, render-graph change, or public API.

## 2. Current cost and equivalence contract

`ambient_occlusion.comp` currently evaluates:

```text
theta(i) = (i + radialJitter) * goldenAngle
direction(i) = (cos(theta(i)), sin(theta(i)))
```

inside `HemisphereSample` for every active tap. At the 32-sample full-resolution quality setting, this creates up to 32 sine/cosine evaluations per AO pixel in addition to the one basis rotation.

The replacement must be mathematically equivalent:

```text
pixelBasisAngle = spatialAngle + radialJitter * goldenAngle
direction(0) = (1, 0)
direction(i + 1) = rotate(direction(i), goldenAngle)
```

Applying `pixelBasisAngle` through `BuildBasis` and `direction(i)` through the recurrence yields the same total azimuth as the current `spatialAngle + (i + radialJitter) * goldenAngle`.

Non-negotiable invariants:

1. `radialJitter` remains in the radial sample index, elevation, and sample-scale calculations.
2. Its angular phase is moved into the once-per-pixel basis angle; it must not be dropped or applied twice.
3. The recurrence advances exactly once for every active tap.
4. The advance occurs immediately after the current direction is consumed and before any depth/projection rejection can execute `continue`.
5. The `i >= sampleCount` break remains before sampling or advancing, so inactive taps do not participate.
6. No per-tap recurrence renormalization is added. The existing three-dimensional sample-direction normalization remains authoritative, while tests bound the two-dimensional drift over 32 rotations.

## 3. Shader implementation

Modify only `Njulf.Shaders/ambient_occlusion.comp`:

### 3.1 Define the fixed rotation

Add:

```glsl
const float AO_GOLDEN_ANGLE = 2.39996323;
const vec2 AO_GOLDEN_ROTATION =
    vec2(-0.7373688783, 0.6754902941);
```

The vector stores `cos(AO_GOLDEN_ANGLE)` and `sin(AO_GOLDEN_ANGLE)` at sufficient precision for the bounded 32-step recurrence. Keep the values literal so the shader compiler cannot leave a trigonometric instruction in the loop setup.

### 3.2 Make hemisphere sampling consume a direction

Change `HemisphereSample` to accept `vec2 spiralDirection`.

- Keep `sampleIndex = float(index) + radialJitter`.
- Keep `u`, `r`, and `z` unchanged.
- Return `vec3(spiralDirection * r, z)`.
- Remove `theta`, `sin(theta)`, and `cos(theta)` from the function.

This isolates the optimization to azimuth generation and leaves the radial/elevation distribution unchanged.

### 3.3 Fold radial phase into the basis

After `SampleJitter`:

- assign `radialJitter = jitter.y`;
- compute `randomAngle = jitter.x * 2.0 * PI + radialJitter * AO_GOLDEN_ANGLE`;
- build the basis once with that angle.

Do not involve `FrameIndex` or alter the hash inputs. The current sampling pattern is spatially stable and this optimization is not a temporal rotation feature.

### 3.4 Advance the recurrence safely

Initialize `vec2 spiralDirection = vec2(1.0, 0.0)` immediately before the bounded sample loop.

For each active tap:

1. Pass the current `spiralDirection` to `HemisphereSample`.
2. Compute the next direction with the explicit complex multiply:

```glsl
spiralDirection = vec2(
    spiralDirection.x * AO_GOLDEN_ROTATION.x -
        spiralDirection.y * AO_GOLDEN_ROTATION.y,
    spiralDirection.x * AO_GOLDEN_ROTATION.y +
        spiralDirection.y * AO_GOLDEN_ROTATION.x);
```

3. Only then proceed to sample projection and branches that may `continue`.

Keep the existing `normalize(basis * HemisphereSample(...))`, sample scale, depth reads, rejection tests, weights, normalization by `sampleCount`, intensity, radius fade, and power unchanged.

## 4. Contract and numerical tests

Extend the existing `Njulf.Tests/AmbientOcclusionShaderContractTests.cs` fixture. Do not create a second SSAO source-contract fixture.

### 4.1 Source-shape contract

Add `HemisphereSpiral_UsesOneBasisRotationAndNoPerTapTrigonometry`:

- require the golden-angle and golden-rotation constants;
- isolate the `HemisphereSample` source and require the `spiralDirection` input and `vec3(spiralDirection * r, z)` output;
- reject `theta`, `sin(`, and `cos(` from that function;
- require the radial-jitter phase in `randomAngle`;
- require initialization to `vec2(1.0, 0.0)`;
- require the recurrence after the sample call and before the first possible `continue`;
- require the existing 32-iteration bound and sample-count break to remain.

Refactor the fixture to use one `ReadShaderSource` helper for both the existing center-reconstruction test and the new recurrence tests.

### 4.2 Constant consistency

Add a small CPU assertion that the stored rotation components match `MathF.Cos(GoldenAngle)` and `MathF.Sin(GoldenAngle)` within `1e-7`, and that the rotation vector's squared length is within `1e-6` of one.

### 4.3 Float recurrence equivalence

For sample counts 4, 8, 16, and 32, evaluate representative radial jitters:

```text
0.0, 0.0000001, 0.25, 0.5, 0.999999
```

For every active tap:

- compute the old reference azimuth from `(i + jitter) * GoldenAngle`;
- compute the new direction from the radial phase multiplied by the iterated recurrence;
- compare using the signed angular difference from dot/cross products;
- require absolute angular error no greater than `1e-5` radians;
- require recurrence norm drift no greater than `1e-6`;
- advance using single-precision operations matching the GLSL expression.

Also compare the reconstructed hemisphere vector's radial length and `z` against the old formula. These values must remain equal within `1e-6`; the optimization must not change radial stratification.

The tests intentionally use float arithmetic. Double-only reference calculations would hide the rounding behavior being qualified.

## 5. Build verification

No shader-project or embedded-resource inventory change is required because `ambient_occlusion.comp` is already compiled, embedded, and listed in `ShaderBuildTests.RequiredShaders`.

Run:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter FullyQualifiedName~AmbientOcclusionShaderContractTests
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Debug
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c ShippingPerformance
dotnet build Njulf.sln
dotnet test Njulf.sln
```

Require successful compilation and SPIR-V validation in all three shader configurations. Do not use `NjulfShaderBuildMode=UseExisting` for the qualification build; it cannot prove the artifact matches the changed source.

## 6. Runtime and image-quality validation

Use identical baseline and candidate binaries/settings and capture the existing AO debug views:

- `RawAo`;
- `BlurredAo`;
- `FinalAo`;
- reconstructed normal and linear depth as controls.

Exercise:

1. The 4-, 8-, 16-, and 32-sample settings.
2. Quarter-, half-, and full-resolution AO.
3. Stationary and moving Bistro cameras.
4. Stationary and horizontal-motion Sponza cameras.
5. Thin geometry, depth discontinuities, broad flat walls, corners, and distant surfaces near the projected-radius fade.

Require:

- no Vulkan validation warnings or errors;
- no non-finite or out-of-range AO output;
- no new banding, directional streaks, repeating spiral artifacts, shimmer, or temporal crawl;
- the existing raw/blurred/final AO structure remains visually equivalent;
- the pinned campaign quality gates pass: relative RMSE <= `0.005`, FLIP P95 <= `0.02`, ROI mean-luminance shift <= `0.02`, and ROI P95-luminance shift <= `0.03`.

Bit-identical output is not required because replacing transcendental evaluation with a float recurrence changes rounding. Any difference beyond the numerical and image gates rejects the optimization.

## 7. Performance qualification

Treat the change as an isolated `ambient-occlusion` GPU candidate. Its product-source scope is limited to:

- `Njulf.Shaders/ambient_occlusion.comp`;
- `Njulf.Tests/AmbientOcclusionShaderContractTests.cs`.

Capture baseline and candidate with the repository's pinned Bistro/Sponza campaign identity. Use the existing target workloads:

- `bistro-motion`;
- `sponza-horizontal-motion`.

Run the existing three-cycle ABBA comparison and retain complete GPU timing for `AmbientOcclusionPass` and the whole frame. Do not change AO quality, sample counts, resolution, workload settings, compiler optimization, assets, or trajectories between slots.

Retain the optimization only when all campaign quality/non-regression checks pass and at least one approved win condition is met:

- whole-frame bottleneck P95 improves by at least 1% and 0.10 ms with a positive 95% bootstrap lower bound; or
- `AmbientOcclusionPass` P95 improves by at least 5% and 0.05 ms with a positive 95% bootstrap lower bound, while the whole-frame bottleneck does not worsen.

CPU/GPU P95 and P99 regressions must remain at or below 1%. If the targeted workloads retain the candidate, run the campaign's full Bistro/Sponza qualification matrix in Release and ShippingPerformance.

If the recurrence is neutral, noisy, or slower on the target GPU, roll back the shader/test candidate rather than adding a device-specific toggle. Record the rejected evidence so it is not repeatedly proposed without a different compiler or hardware hypothesis.

## 8. Risks and mitigations

- **Lost jitter phase:** starting the recurrence at zero without moving `radialJitter * GoldenAngle` into the basis rotates every sample set. Lock the phase formula in source and numerical tests.
- **Conditional recurrence drift:** advancing after a branch means rejected taps alter later angles. Advance before every possible `continue`.
- **Accumulated floating-point drift:** bound direction length and angular error through all 32 supported taps. Do not add a per-tap normalization unless evidence shows the bound fails.
- **Register/ALU tradeoff:** the recurrence introduces a live `vec2` and multiply-add chain. The target GPU may already optimize transcendental work effectively, so campaign evidence—not instruction-count intuition—decides retention.
- **Depth-discontinuity amplification:** tiny direction differences can select a neighboring depth texel. Use raw AO and moving-camera comparisons in addition to aggregate HDR thresholds.
- **Stacked-candidate attribution:** benchmark from the current accepted center-reconstruction implementation. Do not compare the recurrence branch against the older pre-center shader or attribute both optimizations to this change.

## 9. Explicit exclusions

- Changes to AO radius, intensity, power, bias, fade, weighting, blur, or forward upsampling.
- New temporal rotation or use of `FrameIndex`.
- Changes to the supported sample-count buckets or quality presets.
- GTAO algorithm work; the current non-disabled modes share this sampling function and retain existing behavior.
- Shader specialization, subgroup sharing, lookup textures, or precomputed direction buffers.
- AO blur shared-tile work or any other performance candidate.

## 10. Definition of done

- Per-tap SSAO trigonometry is replaced by the fixed golden-angle recurrence.
- The radial-jitter phase and all non-angular sampling math remain unchanged.
- Recurrence ordering is safe across every loop rejection path.
- Numerical tests pass for all supported sample counts and jitter boundaries.
- Debug, Release, and ShippingPerformance shaders compile and validate.
- Focused tests, full build, and full test suite pass.
- Runtime AO debug views show no instability or structural regression.
- Pinned quality thresholds pass.
- The performance campaign proves an accepted target-pass or whole-frame win with no greater than 1% non-target regression.
