# Phase 8: Anti-Aliasing Detailed Implementation Plan

Goal: remove jagged geometry edges, high-contrast shader edges, and visible shimmer while preserving the renderer's existing HDR, bloom, shadows, sky/IBL, and AO behavior. Phase 8 should add a production anti-aliasing stack with SMAA as the preferred spatial solution, FXAA as a cheap fallback, and the camera/motion-vector groundwork required before any temporal AA is enabled.

## Recommendation

Use SMAA 1x as the default spatial anti-aliasing mode and add FXAA as the low-cost fallback.

SMAA is not too expensive for the renderer's intended indie-game target if implemented as SMAA 1x at full resolution. It is heavier than FXAA because it is normally a three-stage post process:
- edge detection.
- blend-weight calculation using search/area lookup textures.
- neighborhood blending.

FXAA is cheaper because it is a single fullscreen pass, but it is softer and tends to blur texture detail, UI-like hard edges, and high-frequency material detail. SMAA generally preserves more detail and gives cleaner geometric edge quality. The practical industry-standard stack for this renderer should be:
- `None`: exact debug output.
- `FXAA`: low-end fallback, very cheap, useful for quick comparison.
- `SMAA 1x`: default quality mode for non-temporal anti-aliasing.
- `TAA`: later opt-in mode after jitter, motion vectors, history validation, and ghosting controls are reliable.

Do not start Phase 8 with TAA as the default. TAA can solve subpixel shimmer better than SMAA/FXAA, but bad motion vectors and weak history rejection cause ghosting, crawling, and smeared detail. Build the spatial AA path first, then add TAA only after the motion-vector contract is proven.

## Assumptions

Phase 8 assumes:
- Phase 2 HDR scene color and tone-map composite are complete.
- Phase 3 bloom is complete.
- Phase 6 sky/IBL and Phase 7 AO may be complete or in progress.
- `ToneMapCompositePass` currently writes final LDR output directly to the swapchain.
- The renderer has explicit pass order validation in `VulkanRenderer.ProductionRenderPassOrder`.
- `RenderTargetManager` currently owns HDR scene color and bloom targets.
- Cameras expose view, projection, and view-projection matrices, but do not yet expose jittered projection state.
- There is no motion vector target or previous-frame transform contract yet.
- `SampleLighting.cs` provides direct-light presets useful for validating shimmer and edge quality under different lighting.

## Current Baseline

Relevant existing behavior:
- `ForwardPlusPass` and `TransparentForwardPass` render HDR scene color.
- `BloomPass` consumes HDR scene color and bloom targets.
- `ToneMapCompositePass` applies exposure, tone mapping, bloom combine, debug views, and writes directly to the swapchain.
- `RenderTargetManager.SceneColor` is `R16G16B16A16Sfloat`.
- There is no intermediate LDR color target after tone mapping.
- There are no AA settings in `RenderSettings`.
- There are no AA diagnostics in `RendererDiagnostics`.
- There are no previous-frame matrices, object previous transforms, motion vector render targets, or camera jitter offsets.

## Target Frame Shape

Final Phase 8 spatial-AA pass order:
1. `DirectionalShadowPass`
2. `SpotShadowPass`
3. `PointShadowPass`
4. `DepthPrePass`
5. `HiZBuildPass`
6. Optional `SceneNormalPass`
7. `AmbientOcclusionPass`
8. `AmbientOcclusionBlurPass`
9. `TiledLightCullingPass`
10. `ForwardPlusPass`
11. `SkyboxPass`
12. `TransparentForwardPass`
13. `BloomPass`
14. `ToneMapCompositePass`
15. `AntiAliasingPass`
16. Present

Change `ToneMapCompositePass` so it writes to a renderer-owned LDR color target when anti-aliasing is enabled. `AntiAliasingPass` then writes to the swapchain. When AA is disabled, either:
- keep `ToneMapCompositePass` writing directly to the swapchain for the shortest path, or
- always write through the LDR target and use a final copy/composite pass.

Recommended first implementation:
- when `AntiAliasingMode.None`, `ToneMapCompositePass` writes swapchain as today.
- when `FXAA` or `SMAA1x`, `ToneMapCompositePass` writes `LdrSceneColor`.
- `AntiAliasingPass` writes final swapchain output.

Future TAA frame shape:
1. render jittered HDR scene color.
2. write motion vectors during opaque/transparent rendering or a dedicated velocity pass.
3. `TemporalResolvePass` combines current HDR color, history, depth, and velocity before bloom or before final post depending on chosen pipeline.
4. bloom and tone mapping operate on resolved HDR color.
5. SMAA or FXAA can optionally remain as a post-TAA cleanup pass.

## Scope

Phase 8 includes:
- Anti-aliasing settings under `RenderSettings`.
- LDR post-tone-map render target support.
- FXAA pass as a cheap single-pass fallback.
- SMAA 1x pass with edge, blend-weight, and neighborhood stages.
- SMAA lookup textures for area and search patterns.
- Debug views for input color, edge mask, blend weights, and final AA output.
- Runtime controls to switch AA mode.
- Camera jitter infrastructure, but disabled by default until TAA exists.
- Motion vector target and previous-frame data plan, with implementation gated behind a stable contract.
- Diagnostics for AA mode, target format, target size, pass costs, and debug state.
- Tests for settings, pass order, render target sizing, shader compilation, and diagnostics.

Phase 8 does not include:
- Shipping TAA as the default mode.
- TAA upscaling or dynamic resolution.
- DLSS, FSR, XeSS, or vendor upscalers.
- Per-object reactive masks for particles and transparencies.
- Full UI rendering isolation. UI should eventually be drawn after AA, but this renderer does not appear to have a UI pass yet.
- MSAA for the deferred/HDR scene path. MSAA can be useful for forward-only renderers, but it is expensive with HDR color, transparencies, post-processing, and bindless material shading.

## Core Design Decisions

1. Prefer SMAA 1x over FXAA for default quality. It preserves detail better and is still lightweight compared with shadows, AO, bloom, or TAA.
2. Keep FXAA because it is valuable for low-end quality presets, debugging, and platforms where SMAA lookup textures or extra passes are not worth the cost.
3. Run spatial AA after tone mapping and bloom combine on an LDR color target. SMAA and FXAA edge detection are more predictable on display-referred color than on raw HDR.
4. Keep AA before present and before any future UI pass. UI/text should eventually render after AA to avoid softening UI.
5. Do not jitter the camera unless a temporal resolve is active. Jitter without TAA creates visible image wobble.
6. Build motion vectors deliberately. Incorrect velocity is worse than no TAA.
7. Preserve debug paths. Raw HDR, bloom debug, AO debug, and shadow debug should remain inspectable without forcing AA.

## Settings

Add `AntiAliasingSettings` under `RenderSettings`.

Suggested API:
```csharp
public enum AntiAliasingMode : uint
{
    None = 0,
    Fxaa = 1,
    Smaa1x = 2,
    Taa = 3
}

public enum AntiAliasingDebugView : uint
{
    None = 0,
    InputColor = 1,
    FxaaLuma = 2,
    SmaaEdges = 3,
    SmaaBlendWeights = 4,
    MotionVectors = 5,
    JitterPattern = 6,
    TaaHistory = 7
}

public sealed class AntiAliasingSettings
{
    public AntiAliasingMode Mode { get; set; } = AntiAliasingMode.Smaa1x;
    public AntiAliasingDebugView DebugView { get; set; } = AntiAliasingDebugView.None;
    public float FxaaContrastThreshold { get; set; } = 0.125f;
    public float FxaaRelativeThreshold { get; set; } = 0.166f;
    public float FxaaSubpixelBlending { get; set; } = 0.75f;
    public float SmaaThreshold { get; set; } = 0.1f;
    public int SmaaMaxSearchSteps { get; set; } = 16;
    public int SmaaMaxSearchStepsDiagonal { get; set; } = 8;
    public float SmaaCornerRounding { get; set; } = 25.0f;
    public bool SmaaPredicationEnabled { get; set; }
    public bool JitterEnabled { get; set; }
    public int JitterSampleCount { get; set; } = 8;
    public float TaaFeedbackMin { get; set; } = 0.85f;
    public float TaaFeedbackMax { get; set; } = 0.95f;
    public float TaaVelocityRejectionScale { get; set; } = 1.0f;
}
```

Recommended constraints:
- `FxaaContrastThreshold`: `0.0312` to `0.333`.
- `FxaaRelativeThreshold`: `0.063` to `0.333`.
- `FxaaSubpixelBlending`: `0.0` to `1.0`.
- `SmaaThreshold`: `0.03` to `0.2`.
- `SmaaMaxSearchSteps`: `4` to `32`.
- `SmaaMaxSearchStepsDiagonal`: `0` to `16`.
- `SmaaCornerRounding`: `0.0` to `100.0`.
- `JitterSampleCount`: `2`, `4`, `8`, or `16`.
- `TaaFeedbackMin`: `0.5` to `0.98`.
- `TaaFeedbackMax`: `TaaFeedbackMin` to `0.99`.

Tests:
- defaults select `Smaa1x`.
- values clamp to valid ranges.
- `Taa` mode falls back to `Smaa1x` or `None` until motion vectors and history are enabled.
- debug views map to shader constants.

## Render Targets

Extend `RenderTargetManager`.

Required for spatial AA:
- `LdrSceneColor`
  - format: match swapchain-compatible LDR format where practical, normally `R8G8B8A8Unorm` or `B8G8R8A8Unorm`.
  - usage: color attachment, sampled, transfer source if screenshots or debug copies need it.
  - extent: swapchain extent.
- `AntiAliasedColor` only if the final AA pass cannot write directly to the swapchain. Prefer writing directly to swapchain to avoid an extra copy.

Required for SMAA:
- `SmaaEdges`
  - format: `R8G8Unorm`.
  - usage: color attachment or storage, sampled.
- `SmaaBlendWeights`
  - format: `R8G8B8A8Unorm`.
  - usage: color attachment or storage, sampled.
- `SmaaAreaTexture`
  - static lookup texture.
  - loaded/generated at renderer initialization.
- `SmaaSearchTexture`
  - static lookup texture.
  - loaded/generated at renderer initialization.

Required for future TAA:
- `MotionVector`
  - format: `R16G16Sfloat`.
  - usage: color attachment, sampled, storage optional.
- `ResolvedHistoryA`
  - format: HDR if TAA resolves before tone mapping, LDR if it resolves after tone mapping.
- `ResolvedHistoryB`
  - ping-pong history target.

Recommended initial formats:
- `LdrSceneColor`: `R8G8B8A8Unorm`.
- `SmaaEdges`: `R8G8Unorm`.
- `SmaaBlendWeights`: `R8G8B8A8Unorm`.
- `MotionVector`: `R16G16Sfloat`.

Debug names:
- `LDR Scene Color`
- `SMAA Edges`
- `SMAA Blend Weights`
- `SMAA Area Texture`
- `SMAA Search Texture`
- `Motion Vectors`
- `TAA History A`
- `TAA History B`

Tests:
- AA targets resize with swapchain.
- SMAA targets match swapchain extent.
- lookup textures are present before SMAA pass initialization.
- disabled AA does not require SMAA targets to be sampled.

## Bindless Index Additions

Extend `BindlessIndexTable.cs` and `common.glsl`.

If Phase 6 and Phase 7 fixed texture slots have landed, place AA after AO:
```csharp
public const int LdrSceneColorTexture = SceneNormalTexture + 1;
public const int SmaaEdgesTexture = LdrSceneColorTexture + 1;
public const int SmaaBlendWeightsTexture = SmaaEdgesTexture + 1;
public const int SmaaAreaTexture = SmaaBlendWeightsTexture + 1;
public const int SmaaSearchTexture = SmaaAreaTexture + 1;
public const int MotionVectorTexture = SmaaSearchTexture + 1;
public const int TaaHistoryTexture = MotionVectorTexture + 1;
public const int FirstDynamicTextureIndex = TaaHistoryTexture + 1;
```

If Phase 6 or Phase 7 are still unmerged, add AA after the current final renderer-owned texture and reconcile the fixed table when those phases land. Keep fixed renderer textures contiguous and document the ordering.

Update:
- `BindlessIndexTests.ShaderConstants_MatchHostBindlessIndices`.
- first dynamic texture index tests.
- texture index name coverage if needed.

## Pipeline Placement

### Tone Mapping Output

Modify `ToneMapCompositePass`:
- if AA mode is `None`, it may write directly to swapchain.
- if AA mode is `FXAA` or `SMAA1x`, it writes to `LdrSceneColor`.
- output encoding must be explicit:
  - `LdrSceneColor` should contain linear or display-referred color consistently.
  - if SMAA/FXAA operate on LDR linear, final swapchain pass handles sRGB conversion.
  - if SMAA/FXAA operate on already sRGB-like values, document it and avoid double conversion.

Recommended first implementation:
- `LdrSceneColor` stores linear LDR color in `R8G8B8A8Unorm`.
- final AA pass writes swapchain with the same sRGB handling currently owned by `ToneMapCompositePass`.
- keep `OutputToSrgb` logic centralized or move it to a small final-output helper to avoid two different gamma paths.

### FXAA

Add:
- `Njulf.Rendering/Pipeline/FxaaPass.cs`
- `Njulf.Shaders/fxaa.frag`

Behavior:
- sample `LdrSceneColor`.
- run luminance-based edge search.
- write directly to swapchain.
- single fullscreen triangle.

Use FXAA for:
- low quality preset.
- platforms where extra SMAA passes are too expensive.
- quick comparisons against SMAA.

### SMAA 1x

Add:
- `Njulf.Rendering/Pipeline/SmaaEdgeDetectionPass.cs`
- `Njulf.Rendering/Pipeline/SmaaBlendWeightPass.cs`
- `Njulf.Rendering/Pipeline/SmaaNeighborhoodPass.cs`
- or one `SmaaPass` that records all three stages with clear debug labels.
- `Njulf.Shaders/smaa_edge.frag`
- `Njulf.Shaders/smaa_blend_weight.frag`
- `Njulf.Shaders/smaa_neighborhood.frag`

Stages:
1. Edge detection:
   - input: `LdrSceneColor`.
   - output: `SmaaEdges`.
   - use color/luma edge detection first.
   - depth predication can be added later if Phase 7 depth/normal inputs make it useful.
2. Blend weight calculation:
   - input: `SmaaEdges`, `SmaaAreaTexture`, `SmaaSearchTexture`.
   - output: `SmaaBlendWeights`.
3. Neighborhood blending:
   - input: `LdrSceneColor`, `SmaaBlendWeights`.
   - output: swapchain.

RenderDoc labels:
- `SMAA Edge Detection`
- `SMAA Blend Weights`
- `SMAA Neighborhood Blend`

Pipeline names:
- `SMAA Edge Pipeline`
- `SMAA Blend Weight Pipeline`
- `SMAA Neighborhood Pipeline`
- `SMAA Pipeline Layout`
- `SMAA Pipeline Cache`

## SMAA Lookup Textures

SMAA requires area and search lookup textures.

Recommended approach:
- Store canonical SMAA area/search texture data as small binary or generated assets under the renderer/shader asset pipeline.
- Upload them through `TextureManager` or a dedicated `SmaaResources` owner.
- Register them at fixed bindless indices.
- Use point or linear filtering exactly as required by the SMAA shader implementation.

Implementation rule:
- Do not approximate SMAA lookup textures with ad hoc gradients. Proper area/search textures are part of the algorithm's quality.

Tests:
- lookup textures load at renderer initialization.
- expected dimensions and formats are validated.
- missing lookup texture fails clearly or falls back to FXAA with diagnostics.

## Jittered Projection

Add camera jitter support, but keep it disabled until temporal resolve is implemented.

Required state:
- current jitter in pixels.
- current jitter in normalized clip/projection units.
- previous jitter.
- jitter sample index.
- jitter sequence length.

Recommended sequence:
- Halton 2,3 sequence for first implementation.
- centered around zero.
- scaled by inverse render resolution.

Camera contract:
- Add a way for the renderer to request a jittered projection without permanently mutating the camera's base projection.
- Store both:
  - unjittered view-projection for culling and stable systems.
  - jittered view-projection for rasterization when temporal AA is active.

Important:
- Do not use jittered matrices for CPU frustum culling, Hi-Z culling, shadow cascade fitting, or light selection unless intentionally designed.
- For TAA, rasterization uses jittered projection; motion vectors compare current and previous clip positions with jitter handled consistently.

Tests:
- jitter sequence is centered over its period.
- jitter offsets are resolution-scaled.
- jitter disabled produces exactly zero offset.
- culling matrix remains unjittered.

## Motion Vectors

Motion vectors are required before TAA can be considered production-ready.

Add a motion vector target:
- format: `R16G16Sfloat`.
- full resolution.
- cleared to zero every frame.
- sampled by future `TemporalResolvePass`.

Data contract:
- previous view-projection matrix.
- current view-projection matrix.
- previous object world matrix.
- current object world matrix.
- object ID or stable instance index if needed for history rejection.

Implementation options:
1. Write motion vectors in `ForwardPlusPass`.
   - Pros: no extra geometry pass.
   - Cons: forward shader path gets more outputs and complexity.
2. Add `MotionVectorPass`.
   - Pros: explicit target and simpler debug.
   - Cons: extra mesh/task pass.

Recommended first path:
- add the data model and target in Phase 8.
- implement a simple camera-only motion vector debug path first.
- add per-object motion vectors when animation or moving objects require them.
- do not enable TAA until object motion vectors are correct enough for the sample scenes.

Motion vector encoding:
- store screen-space velocity in pixels or normalized UV units.
- choose one convention and write it in comments and tests.
- normalized UV velocity is easier for texture reprojection.

Debug view:
- visualize velocity direction and magnitude.
- static camera should be black.
- camera pan should produce coherent horizontal velocity.

Tests:
- previous matrices update after frame completion, not before draw.
- first frame has zero motion vectors.
- resize resets history and motion state.
- static camera/object produces near-zero velocity.

## Temporal AA Plan

TAA should be a second vertical slice inside or after Phase 8.

Required before enabling:
- jittered rasterization.
- motion vectors.
- current color target.
- history target ping-pong.
- depth rejection.
- neighborhood clamp or clipping.
- disocclusion handling.
- history reset on camera cut, resize, scene reload, and large FOV changes.

Recommended resolve position:
- resolve HDR scene color before bloom and tone mapping.
- this lets bloom consume temporally stable highlights.
- it also avoids resolving already tone-mapped color and then trying to run HDR bloom afterward.

Future TAA pass order:
1. jittered `ForwardPlusPass` writes `HdrSceneColor`.
2. motion vectors are produced.
3. `TemporalResolvePass` writes `ResolvedHdrSceneColor`.
4. bloom consumes `ResolvedHdrSceneColor`.
5. tone map writes `LdrSceneColor`.
6. optional SMAA/FXAA cleanup writes swapchain.

Default TAA behavior:
- off until proven.
- explicit quality preset can enable it.
- provide a non-temporal SMAA fallback for stylized games and for content with problematic transparencies.

Anti-ghosting controls:
- feedback min/max.
- velocity rejection scale.
- luminance clamp strength.
- reactive mask later for particles/transparency.
- history reset controls.

## Transparency, Alpha Test, And Shader Shimmer

Spatial AA limitations:
- SMAA and FXAA help visible polygon edges after composition.
- They do not solve all specular aliasing, normal-map shimmer, alpha-tested foliage shimmer, or subpixel geometry flicker.

Industry-standard follow-up items:
- Alpha-tested materials need good mipmaps and alpha-to-coverage only if MSAA is introduced later.
- Normal maps need correct mip filtering and optionally roughness-from-normal-variance in a future material quality phase.
- Specular aliasing is better handled through material filtering and TAA.
- Transparent objects should be rendered before post AA, but UI should be rendered after post AA.

Phase 8 acceptance should not pretend SMAA solves temporal shimmer completely. It should clearly improve static jagged edges and high-contrast silhouettes while preparing TAA for temporal stability.

## Diagnostics

Extend `SceneRenderingData`:
```csharp
public AntiAliasingMode AntiAliasingMode { get; set; }
public AntiAliasingDebugView AntiAliasingDebugView { get; set; }
public uint AntiAliasingWidth { get; set; }
public uint AntiAliasingHeight { get; set; }
public string AntiAliasingInputFormat { get; set; }
public string AntiAliasingOutputFormat { get; set; }
public long CpuFxaaRecordMicroseconds { get; set; }
public long CpuSmaaEdgeRecordMicroseconds { get; set; }
public long CpuSmaaBlendRecordMicroseconds { get; set; }
public long CpuSmaaNeighborhoodRecordMicroseconds { get; set; }
public long GpuAntiAliasingMicroseconds { get; set; }
public int SmaaLookupTexturesReady { get; set; }
public int MotionVectorsEnabled { get; set; }
public int JitterEnabled { get; set; }
public float JitterX { get; set; }
public float JitterY { get; set; }
```

Extend `RendererDiagnostics` with matching fields.

Update:
- `RendererDiagnostics.Empty`.
- diagnostics construction in `VulkanRenderer`.
- `SampleDiagnosticsReporter.PrintFirstFrameDiagnostics`.
- `RendererDiagnosticsTests`.

Sample diagnostics line:
```text
Frame diagnostics AA: mode=Smaa1x, size=1920x1080, input=R8G8B8A8Unorm, debug=None, smaaLookups=1, fxaaRecordUs=0, smaaEdgeUs=..., smaaBlendUs=..., smaaNeighborhoodUs=...
```

## Sample App Validation

`SampleLighting.cs` does not need new lights for AA. Use existing modes to expose different edge cases:
- `DirectionalKey`: check sun-lit geometry edges, sky silhouette, and shadow-map edge contrast.
- `ThreePointDemo`: check colored high-contrast highlights and material edges.
- `SpotShadowDemo`: check cone-lit edges and shadowed silhouettes.
- `PointShadowDemo`: check bright local highlights and specular shimmer.

Suggested sample input controls:
- cycle AA mode: `None -> FXAA -> SMAA1x -> TAA if available`.
- cycle AA debug view.
- adjust SMAA threshold.
- adjust FXAA subpixel amount.
- toggle jitter debug only after TAA work begins.

Validation scene suggestions:
- thin railings or fence geometry.
- high-contrast diagonal edges.
- small emissive strips.
- rough and smooth metallic spheres after Phase 6.
- normal-mapped material under grazing light.

## Render Graph And Resource Lifetime

Renderer initialization should:
1. Create `LdrSceneColor`.
2. Create SMAA edge and blend-weight targets.
3. Create or load SMAA area/search lookup textures.
4. Create optional motion vector and TAA history targets, but keep TAA disabled until ready.
5. Register fixed AA textures in the bindless heap.
6. Initialize FXAA and SMAA pipelines.
7. Insert `AntiAliasingPass` after `ToneMapCompositePass` when AA is enabled.

Resize behavior:
- recreate LDR, SMAA, motion vector, and history targets.
- reset TAA history.
- recreate descriptor sets that reference resized targets.
- preserve AA mode and settings.

Mode switching:
- `None` should not sample stale AA targets.
- `FXAA` should require only `LdrSceneColor`.
- `SMAA1x` should require LDR color, edge target, blend target, and lookup textures.
- `TAA` should refuse to activate or fall back until motion vectors and history are valid.

Shutdown behavior:
- lookup textures, render targets, pipelines, descriptor pools, layouts, and caches are destroyed exactly once.

## Barriers And Layouts

Expected spatial AA layouts:
- `LdrSceneColor`
  - color attachment during `ToneMapCompositePass`.
  - shader read during FXAA or SMAA edge/neighborhood passes.
- `SmaaEdges`
  - color attachment or storage write during edge detection.
  - shader read during blend-weight pass and debug views.
- `SmaaBlendWeights`
  - color attachment or storage write during blend-weight pass.
  - shader read during neighborhood pass and debug views.
- swapchain
  - color attachment during final AA pass.
  - present after pass completion.

Validation targets:
- no swapchain write before final pass when AA is enabled.
- no sampling from a target still in color-attachment layout.
- disabled AA path remains identical to pre-Phase-8 output path.
- RenderDoc clearly shows the final swapchain writer.

## Quality Presets

Recommended defaults:
- Low: `FXAA`.
- Medium: `SMAA1x`.
- High: `SMAA1x`.
- Ultra: `TAA + SMAA cleanup` only after TAA is proven; until then `SMAA1x`.

Reasoning:
- SMAA 1x is generally affordable and should be the default quality target.
- FXAA is still valuable for older integrated GPUs and for CPU/GPU-bound scenes where three extra post passes matter.
- TAA should be quality-gated, not shipped by default before motion-vector correctness and ghost rejection are validated.

## Debug Views

Required debug views:
- AA input color.
- FXAA luminance/edge response.
- SMAA edge mask.
- SMAA blend weights.
- final AA output.

Future debug views:
- motion vectors.
- jitter pattern.
- TAA history.
- TAA rejection mask.
- history clamp bounds.

Debug behavior:
- debug views should not be blurred by an additional AA pass.
- debug views should be selectable from runtime settings.
- screenshots should indicate active AA mode in diagnostics.

## Automated Tests

Unit tests:
- `AntiAliasingSettings` defaults and clamping.
- `RenderSettings` includes AA settings.
- LDR and SMAA target extent calculation.
- bindless AA constants match shader constants.
- render graph pass order with AA enabled.
- `ToneMapCompositePass` target selection for `None`, `FXAA`, and `SMAA1x`.
- `RendererDiagnostics.Empty` contains AA defaults.
- populated diagnostics copy AA fields from `SceneRenderingData`.
- jitter sequence is centered and deterministic.
- TAA activation fails or falls back when motion vectors are unavailable.

Shader build tests:
- `fxaa.frag`.
- `smaa_edge.frag`.
- `smaa_blend_weight.frag`.
- `smaa_neighborhood.frag`.
- future `motion_vector.*`.
- future `taa_resolve.comp` or `taa_resolve.frag`.
- updated `tonemap_composite.frag`.

Optional GPU smoke tests:
- `None` path writes swapchain from tone-map composite.
- `FXAA` path writes LDR color then swapchain.
- `SMAA1x` path writes edge and blend targets then swapchain.
- SMAA lookup textures are sampled without validation errors.
- resize recreates AA targets and remains validation-clean.

## Implementation Sequence

1. Add `AntiAliasingSettings`, `AntiAliasingMode`, and `AntiAliasingDebugView`.
2. Add LDR and SMAA targets to `RenderTargetManager`.
3. Add fixed bindless texture indices for LDR color, SMAA targets, SMAA lookup textures, and future motion/history targets.
4. Add diagnostics fields to `SceneRenderingData` and `RendererDiagnostics`.
5. Modify `ToneMapCompositePass` to write either swapchain or `LdrSceneColor`.
6. Add `FxaaPass` and `fxaa.frag`.
7. Add SMAA lookup texture resource owner.
8. Add SMAA edge, blend-weight, and neighborhood shaders.
9. Add `SmaaPass` or separate SMAA pass classes with clear debug labels.
10. Add `AntiAliasingPass` mode routing.
11. Insert AA into `VulkanRenderer.InitializeRenderGraph` after tone mapping.
12. Update pass-order validation.
13. Add sample input controls and diagnostics output.
14. Add unit tests and shader build tests.
15. Run CPU tests and shader compilation.
16. Run `NjulfHelloGame` with validation layers.
17. Capture RenderDoc frames for `None`, `FXAA`, and `SMAA1x`.
18. Tune default thresholds using sample scenes and high-contrast diagonal geometry.
19. Add jitter sequence infrastructure with jitter disabled by default.
20. Add motion vector target/data model as a follow-up sub-slice before any TAA resolve is enabled.

## Acceptance Criteria

Phase 8 spatial AA is complete when:
1. `None`, `FXAA`, and `SMAA1x` modes are selectable at runtime.
2. SMAA 1x is the default AA mode.
3. FXAA is available as a cheaper fallback.
4. `ToneMapCompositePass` no longer has to be the final swapchain writer when AA is enabled.
5. SMAA edge and blend-weight targets are visible and named in RenderDoc.
6. Static geometry edges are visibly cleaner with SMAA than with `None`.
7. FXAA is visibly cheaper/softer and remains useful as a low-end mode.
8. Bloom, tone mapping, AO debug views, and shadow debug views remain usable.
9. Renderer diagnostics report AA mode, target size, format, debug view, and pass timings.
10. Resize, mode switching, and shutdown are validation-clean.

Phase 8 temporal groundwork is complete when:
1. camera jitter sequence exists but is disabled unless temporal resolve is active.
2. previous/current matrix state is tracked correctly.
3. motion vector target exists or is fully planned with tests for first-frame/reset behavior.
4. TAA mode cannot silently run without valid motion vectors and history.

## Phase 8 Definition Of Done

The renderer should render `NjulfHelloGame` with visibly smoother silhouettes, diagonal edges, and high-contrast material boundaries. SMAA should be the preferred default because it preserves detail better than FXAA at acceptable cost. FXAA should remain available for low-end presets and quick comparisons. The renderer should also have enough camera and motion-vector structure that TAA can be implemented deliberately later without rewriting the post-processing chain.
