# SMAA Quality Preset Redo Plan

## Goal

Replace the current `Smaa1x` through `Smaa16x` user-facing modes with three SMAA quality modes: `SmaaLow`, `SmaaMedium`, and `SmaaHigh`.

The implementation must stop treating SMAA quality as a fake sample count. Each quality level should:

1. Apply real SMAA preset behavior: threshold, search steps, diagonal detection, and corner detection.
2. Use a concrete internal anti-aliasing resolution so the low/medium/high setting has a measurable render-target-size effect.
3. Keep `SMAA_RT_METRICS`-equivalent data honest: shader dimensions and inverse dimensions must always match the actual LDR/SMAA targets being processed.

## Research Notes

Primary reference: https://github.com/iryoku/smaa/blob/master/SMAA.hlsl

Supporting paper: https://www.iryoku.com/smaa/downloads/SMAA-Enhanced-Subpixel-Morphological-Antialiasing.pdf

Important findings:

- The official SMAA `LOW`, `MEDIUM`, and `HIGH` presets are quality presets, not sample-count labels.
- Official preset values:
  - `LOW`: threshold `0.15`, max search steps `4`, diagonal detection disabled, corner detection disabled.
  - `MEDIUM`: threshold `0.1`, max search steps `8`, diagonal detection disabled, corner detection disabled.
  - `HIGH`: threshold `0.1`, max search steps `16`, diagonal search steps `8`, corner rounding `25`.
- `ULTRA` exists upstream but is intentionally out of scope because the new product surface is only low/medium/high.
- The old SMAA `1x`, `T2x`, `S2x`, and `4x` modes are not simple shader sample-count multipliers:
  - `T2x` requires jitter, temporal resolve, reprojection, and per-frame subsample indices.
  - `S2x` requires rendering with 2x MSAA, separating subsamples, running the full SMAA 1x pipeline per separated sample, and blending.
  - `4x` is temporal jitter on top of `S2x`.
- Current code does not implement those real subpixel modes. It uses one LDR target size and passes `SmaaSampleCount` to heuristic shader math, so `Smaa2x` through `Smaa16x` are misleading.
- If we intentionally vary internal resolution by quality level, that should be represented as an engine render-target scale, not as SMAA sample count.

## Current Local State

Relevant files:

- `Njulf.Rendering/Data/RenderSettings.cs`
  - `AntiAliasingMode` currently exposes `Smaa1x`, `Smaa2x`, `Smaa4x`, `Smaa8x`, and `Smaa16x`.
  - `AntiAliasingSettings` defaults to `Smaa1x`.
  - `EffectiveSmaaSampleCount`, `IsSmaaMode`, and `GetSmaaSampleCount` encode the old model.
- `Njulf.Rendering/Pipeline/AntiAliasingPass.cs`
  - Runs edge, blend-weight, and neighborhood passes against the existing SMAA targets.
  - Push constants include `SmaaSampleCount` and derive shader quality from it.
  - Source dimensions come from `LdrSceneColor.Extent`.
- `Njulf.Rendering/Resources/RenderTargetManager.cs`
  - `LdrSceneColor`, `SmaaEdges`, and `SmaaBlendWeights` are always recreated at swapchain extent.
- `Njulf.Shaders/smaa_edge.frag`
  - Uses `SmaaSampleCount` to lower threshold heuristically.
- `Njulf.Shaders/smaa_blend_weight.frag`
  - Uses max search steps, but also uses `SmaaSampleCount` to inflate minimum weights.
- `Njulf.Shaders/smaa_neighborhood.frag`
  - Uses `SmaaSampleCount` to scale final blend strength.
- `NjulfHelloGame/SampleInputController.cs`
  - Cycles through all old SMAA modes.
- `Njulf.Tests/RendererDiagnosticsTests.cs`
  - Tests old defaults, old sample counts, and old SMAA mode list.

## Proposed Quality Table

Use explicit presets in `AntiAliasingSettings` and derive effective values from the mode.

| Mode | Internal resolution scale | Threshold | Search steps | Diag steps | Corner rounding | Diag/corner behavior |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `SmaaLow` | `0.50` | `0.15` | `4` | `0` | `0` | Disabled |
| `SmaaMedium` | `0.75` | `0.10` | `8` | `0` | `0` | Disabled |
| `SmaaHigh` | `1.00` | `0.10` | `16` | `8` | `25` | Enabled |

Notes:

- The resolution scale is a project requirement layered on top of upstream SMAA presets. Upstream SMAA presets themselves do not require lower render resolutions.
- If half/three-quarter resolution AA looks too soft in practice, keep the API but change the scale table after visual testing. The implementation should make the scale table easy to tune.
- Keep FXAA and TAA at native swapchain resolution unless a separate render-scale feature is later introduced.

## Implementation Plan

### 1. Replace public SMAA modes

Edit `Njulf.Rendering/Data/RenderSettings.cs`.

- Change `AntiAliasingMode` to:
  - `None`
  - `Fxaa`
  - `SmaaLow`
  - `SmaaMedium`
  - `SmaaHigh`
  - `Taa`
- Set the default to `SmaaMedium` or `SmaaHigh`.
  - Recommended default: `SmaaMedium`, because it matches the current threshold `0.1` while reducing search cost from current high-like `16/8/25`.
- Replace `EffectiveSmaaSampleCount` with preset-oriented properties:
  - `EffectiveSmaaResolutionScale`
  - `EffectiveSmaaThreshold`
  - `EffectiveSmaaMaxSearchSteps`
  - `EffectiveSmaaMaxSearchStepsDiagonal`
  - `EffectiveSmaaCornerRounding`
  - `EffectiveSmaaDiagonalEnabled`
  - `EffectiveSmaaCornerEnabled`
- Update `IsSmaaMode` to only return true for `SmaaLow`, `SmaaMedium`, and `SmaaHigh`.
- Remove or obsolete `GetSmaaSampleCount`.

### 2. Make AA target extents quality-aware

Edit `Njulf.Rendering/Resources/RenderTargetManager.cs`.

- Add `CalculateAntiAliasingExtent(Extent2D swapchainExtent, float resolutionScale)`.
- Clamp scales to the supported table: `0.5`, `0.75`, `1.0`.
- Change `RecreateAntiAliasingTargets` to accept an AA resolution scale.
- Recreate:
  - `LdrSceneColor`
  - `SmaaEdges`
  - `SmaaBlendWeights`
at the calculated AA extent.
- Keep `MotionVectors`, `TaaHistoryA`, and `TaaHistoryB` at full swapchain extent for TAA unless TAA is intentionally made render-scale-aware later.
- Update `Recreate` to accept both AO scale and AA scale.

### 3. Feed the scaled LDR target correctly

Edit `Njulf.Rendering/Pipeline/ToneMapCompositePass.cs` and the renderer recreation path.

- When the effective AA mode is SMAA, composite HDR to `LdrSceneColor` using the scaled AA extent.
- Ensure the viewport/scissor already follows `LdrSceneColor.Extent`; if not, fix it there.
- When the mode changes between SMAA quality levels, recreate AA targets if the effective scale changed.
- Add a small state cache in `VulkanRenderer` for the last AA target scale, similar to AO target scale handling if present.

### 4. Remove fake SMAA sample-count shader behavior

Edit `Njulf.Rendering/Data/GPUStructs.cs`, `AntiAliasingPass.cs`, and all AA shaders.

- Rename or replace `SmaaSampleCount` with `SmaaQuality`.
- Add explicit push-constant fields if needed:
  - `SmaaDiagonalEnabled`
  - `SmaaCornerEnabled`
- Fill push constants from `AntiAliasingSettings` effective preset values.
- `SourceDimensions` and `InvSourceDimensions` must be the scaled `LdrSceneColor` extent.
- `smaa_edge.frag`:
  - Use `pc.SmaaThreshold` directly.
  - Remove the `log2(SmaaSampleCount)` quality multiplier.
- `smaa_blend_weight.frag`:
  - Use preset max search steps.
  - Skip diagonal/corner logic when disabled.
  - Remove sample-count-derived minimum weight inflation.
- `smaa_neighborhood.frag`:
  - Remove sample-count-derived blend scaling.
  - Use weights from the blend-weight pass directly, with only conservative clamping.

### 5. Keep debug output sane with scaled targets

Edit `AntiAliasingPass.cs` and `fxaa.frag` if needed.

- `SmaaEdges` and `SmaaBlendWeights` debug views are now lower resolution for low/medium.
- When rendering debug to the swapchain, sample the scaled debug texture through normalized UVs so it fills the swapchain.
- Report `sceneData.AntiAliasingWidth` and `sceneData.AntiAliasingHeight` as the actual internal AA target size, not the swapchain size.
- Consider adding a diagnostic field for `AntiAliasingResolutionScale` if diagnostics currently need to explain why the dimensions differ.

### 6. Update sample controls and diagnostics

Edit `NjulfHelloGame/SampleInputController.cs`.

- Cycle order should be:
  - `None`
  - `Fxaa`
  - `SmaaLow`
  - `SmaaMedium`
  - `SmaaHigh`
  - `Taa`
- Replace `smaaSamples=` output with:
  - `smaaQuality=`
  - `smaaScale=`
  - effective preset values.

Edit `Njulf.Rendering/Data/RendererDiagnostics.cs` if diagnostics expose old assumptions.

- Keep existing timing fields.
- Add AA scale only if useful; otherwise dimensions are enough.

### 7. Update tests

Edit `Njulf.Tests/RendererDiagnosticsTests.cs`.

- Replace default assertions with the selected new default.
- Replace `AntiAliasingSettings_SupportsSmaaModesThrough16x` with a test for the three quality modes.
- Add tests for:
  - `SmaaLow` preset values.
  - `SmaaMedium` preset values.
  - `SmaaHigh` preset values.
  - `RenderTargetManager.CalculateAntiAliasingExtent` at `0.5`, `0.75`, and `1.0`.
  - `IsSmaaMode` only accepting low/medium/high.
- Update GPU struct size tests after push constant changes.

### 8. Shader compilation and runtime validation

- Recompile shaders after push-constant layout changes.
- Run the unit test suite.
- Run the sample app and verify:
  - `SmaaLow` uses half-resolution LDR/SMAA targets.
  - `SmaaMedium` uses three-quarter-resolution LDR/SMAA targets.
  - `SmaaHigh` uses native-resolution LDR/SMAA targets.
  - SMAA lookup textures still bind correctly.
  - Debug views render full-screen without viewport/scissor mismatch.
  - No old `Smaa1x` through `Smaa16x` labels remain in UI/log output/tests.

## Risks and Decisions

- Low/medium render-scale SMAA can look softer because the scene is effectively tone-mapped at lower resolution before final upsampling. This satisfies the requested different-resolution behavior, but it is not part of the original SMAA low/medium/high preset design.
- If quality loss is unacceptable, the fallback decision should be to keep all SMAA presets at native resolution and use only official preset parameters. That would match the reference more closely, but it would not satisfy the requested resolution distinction.
- Real SMAA `T2x`, `S2x`, and `4x` should not be represented by the new low/medium/high modes. If those modes are desired later, they need a separate feature plan because they require temporal reprojection and/or MSAA subsample handling.

## Done Criteria

- Public AA mode surface no longer exposes `Smaa1x`, `Smaa2x`, `Smaa4x`, `Smaa8x`, or `Smaa16x`.
- `SmaaLow`, `SmaaMedium`, and `SmaaHigh` produce different preset values and different internal AA target extents.
- Shaders no longer derive SMAA quality from a sample count.
- Diagnostics report actual AA target dimensions.
- Tests cover the new preset table and target extent calculation.
- Sample hotkey/UI output uses low/medium/high terminology only.
