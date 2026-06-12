# Phase 7: Ambient Occlusion Detailed Implementation Plan

Goal: restore small-scale contact shading that direct lighting, shadows, and IBL do not capture. Phase 7 should add screen-space ambient occlusion that darkens indirect lighting in corners, creases, and contact areas without making direct lights look dirty or creating obvious halos around silhouettes.

Phase 7 assumes:
- Phase 2 HDR scene color and tone-map composite are complete.
- Phase 3 bloom is complete.
- Phase 4 and Phase 5 shadow infrastructure are complete or mostly complete.
- Phase 6 sky, environment lighting, and IBL are complete or actively being implemented.
- The renderer has a depth prepass, Hi-Z depth pyramid, Forward+ lighting, HDR scene color, and explicit render graph pass order.
- `forward.frag` resolves material albedo, normal, roughness, metallic, material AO, direct light, shadows, emissive, and indirect lighting.
- `SampleLighting.cs` provides direct-light presets that can be reused for AO validation. AO should not be implemented as extra fill or shadow lights.

## Current Baseline

Relevant existing behavior:
- `RenderTargetManager` owns:
  - `SceneColor`.
  - bloom mip chain.
  - bloom scratch mip chain.
- `BindlessIndexTable.cs` reserves fixed texture indices for:
  - default material textures.
  - depth.
  - Hi-Z.
  - HDR scene color.
  - bloom.
  - shadows.
  - dynamic material textures.
- `SceneRenderingData` tracks CPU pass record timings for depth, Hi-Z, light culling, forward, transparent, bloom, and composite.
- `RendererDiagnostics` exposes those timing and feature fields to `NjulfHelloGame`.
- The production master plan says Phase 7 should:
  - add depth and normal inputs for screen-space AO.
  - implement SSAO or GTAO.
  - add bilateral blur.
  - composite AO into indirect lighting, not direct light unless intentionally stylized.
  - add radius, intensity, bias, and sample count controls.
  - add debug views for raw and blurred AO.

## Target Frame Shape

Final Phase 7 pass order:
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
15. Present

`AmbientOcclusionPass` should run after depth is available and before `ForwardPlusPass`, because forward shading needs the final AO texture when computing indirect lighting. `TiledLightCullingPass` can remain before or after AO as long as both consume depth in stable read-only layouts. Prefer AO before light culling only if it simplifies barriers; otherwise keep light culling near forward rendering.

If Phase 6 has not landed yet, Phase 7 can still produce and debug AO, but the final shader integration should be framed as "multiply indirect lighting" rather than permanently multiplying the old ambient fallback.

## Scope

Phase 7 includes:
- AO settings under `RenderSettings`.
- AO render targets in `RenderTargetManager`.
- Fixed bindless texture indices for raw and blurred AO.
- A screen-space AO compute pass.
- A bilateral blur compute pass.
- Optional scene normal target if depth-derived normals are not good enough.
- Forward shader integration that applies AO only to indirect lighting.
- Runtime controls for radius, intensity, bias, sample count, resolution scale, and debug view.
- Diagnostics for AO state, target size, settings, and CPU/GPU pass cost.
- Sample app diagnostics and input/debug hooks.
- Automated tests for settings, render target sizing, bindless constants, diagnostics defaults, and shader compilation.

Phase 7 does not include:
- Ray traced AO.
- Multi-bounce AO.
- Bent normals for IBL. This can be added later if the basic AO path is stable.
- Temporal AO accumulation. That should wait until Phase 8 motion vectors and TAA work are more mature.
- AO for transparent objects.
- Per-material authored AO import changes. Existing material AO remains separate and continues to participate in indirect lighting.

## Core Design Decisions

1. Implement half-resolution SSAO first, with a path that can evolve toward GTAO. Half resolution is the best first default for an indie renderer because AO is blur-friendly and should not dominate frame time.
2. Generate raw AO into a single-channel render target, blur into a separate single-channel target, then sample the blurred texture in `forward.frag`.
3. Use depth as the authoritative geometry input. Add a normal input only if reconstructed normals are visibly unstable on the sample scene.
4. Apply screen-space AO to indirect diffuse and indirect specular occlusion. Do not multiply direct lighting or shadowed direct lighting by AO in the default mode.
5. Keep material AO and screen-space AO separate until final indirect-light composition. Material AO captures authored texture detail; SSAO/GTAO captures screen-space contact.
6. Clamp AO aggressively enough to avoid black dirt. The default should be subtle and production-safe.
7. Make debug views cheap and inspectable. Raw AO, blurred AO, depth, normal, and final AO factor should be easy to verify in RenderDoc.

## Settings

Add `AmbientOcclusionSettings` under `RenderSettings`.

Suggested API:
```csharp
public enum AmbientOcclusionMode : uint
{
    Disabled = 0,
    Ssao = 1,
    Gtao = 2
}

public enum AmbientOcclusionDebugView : uint
{
    None = 0,
    RawAo = 1,
    BlurredAo = 2,
    FinalAo = 3,
    ReconstructedNormal = 4,
    LinearDepth = 5
}

public sealed class AmbientOcclusionSettings
{
    public bool Enabled { get; set; } = true;
    public AmbientOcclusionMode Mode { get; set; } = AmbientOcclusionMode.Ssao;
    public float ResolutionScale { get; set; } = 0.5f;
    public float Radius { get; set; } = 0.75f;
    public float Intensity { get; set; } = 1.0f;
    public float Bias { get; set; } = 0.03f;
    public float Power { get; set; } = 1.2f;
    public int SampleCount { get; set; } = 16;
    public int BlurRadius { get; set; } = 2;
    public float DepthSigma { get; set; } = 2.0f;
    public float NormalSigma { get; set; } = 32.0f;
    public bool UseSceneNormals { get; set; }
    public AmbientOcclusionDebugView DebugView { get; set; } = AmbientOcclusionDebugView.None;
}
```

Recommended constraints:
- `ResolutionScale`: clamp to `0.25`, `0.5`, or `1.0`.
- `Radius`: `0.05` to `5.0` world units.
- `Intensity`: `0.0` to `4.0`.
- `Bias`: `0.0` to `0.5`.
- `Power`: `0.25` to `4.0`.
- `SampleCount`: `4`, `8`, `16`, or `32`.
- `BlurRadius`: `0` to `4`.
- `DepthSigma`: `0.1` to `16.0`.
- `NormalSigma`: `1.0` to `128.0`.

Tests:
- defaults enable a conservative half-resolution AO path.
- invalid settings clamp to supported ranges.
- disabling AO makes render targets remain valid but produces neutral AO.
- debug view values map to shader constants.

## Render Targets

Extend `RenderTargetManager`.

Recommended targets:
- `AmbientOcclusionRaw`
  - format: `R8Unorm` for first implementation, or `R16Sfloat` if banding appears.
  - usage: sampled, storage, transfer source/destination if debug copies are needed.
  - default extent: half swapchain resolution.
- `AmbientOcclusionBlurred`
  - same format and extent as raw AO.
  - sampled by `forward.frag`.
- Optional `SceneNormal`
  - format: `R16G16B16A16Sfloat` or packed `R10G10B10A2Unorm`.
  - full resolution first if used by forward shading or future effects.
  - half resolution only if used exclusively by AO and generated in the AO pass.

Add sizing helpers:
```csharp
public static Extent2D CalculateAmbientOcclusionExtent(Extent2D swapchainExtent, float resolutionScale)
```

Rules:
- never produce zero width or height.
- odd swapchain extents round up.
- AO target recreation should happen on swapchain resize or resolution-scale change.
- AO targets should have clear debug names:
  - `Ambient Occlusion Raw`
  - `Ambient Occlusion Blurred`
  - `Scene Normal`

Tests:
- AO extents are correct for `0.25`, `0.5`, and `1.0`.
- odd sizes round to at least one pixel.
- render target recreation preserves expected format and usage flags.

## Bindless Index Additions

Extend `BindlessIndexTable.cs` and `common.glsl`.

If Phase 6 has added environment texture slots, place AO after those renderer-owned fixed textures:
```csharp
public const int AmbientOcclusionRawTexture = BrdfLutTexture + 1;
public const int AmbientOcclusionBlurredTexture = AmbientOcclusionRawTexture + 1;
public const int SceneNormalTexture = AmbientOcclusionBlurredTexture + 1;
public const int FirstDynamicTextureIndex = SceneNormalTexture + 1;
```

If Phase 6 has not landed yet, place AO after the current last fixed renderer texture and update Phase 6 during merge so all fixed indices remain contiguous and intentional.

Update tests:
- `BindlessIndexTests.ShaderConstants_MatchHostBindlessIndices`.
- `PerFrameBufferIndices_AreDerivedFromFramesInFlight`.
- first dynamic texture index assertions.
- texture index naming where relevant.

Migration note:
- Raw bindless texture indices should not be serialized into assets. If any sample or cache persists material texture indices, confirm it is rebuilt when fixed renderer indices move.

## Normal Input Strategy

AO needs a normal estimate. Choose the simplest reliable option after visual testing.

Option A: reconstruct normals from depth in `ao.comp`.
- Pros:
  - no extra geometry pass.
  - no extra full-resolution normal target.
  - good enough for many scenes.
- Cons:
  - unstable around depth discontinuities.
  - loses normal-map detail.
  - can create faceted or noisy AO on low-poly meshes.

Option B: write scene normals in an explicit `SceneNormalPass`.
- Pros:
  - stable geometric normals.
  - easier bilateral blur weighting.
  - reusable by later SSR, decals, GTAO, and debug tools.
- Cons:
  - extra pass and target.
  - requires mesh/task path variant or forward/depth pipeline output changes.

Recommended first path:
1. Start with depth-reconstructed normals.
2. Add the `SceneNormalPass` only if reconstruction fails visual acceptance on Sponza-like content.
3. Keep `UseSceneNormals` in settings so the implementation can switch without changing public API.

If adding `SceneNormalPass`:
- Run it after `DepthPrePass`.
- Reuse mesh/task shader infrastructure.
- Write view-space or world-space normals consistently.
- Register `SceneNormalTexture` at a fixed bindless index.
- Add debug view for normal output.

## AO Algorithm

Start with SSAO, structure it so GTAO can replace the sampling function later.

Recommended shader:
- `ambient_occlusion.comp`

Inputs:
- depth texture.
- optional scene normal texture.
- camera projection/inverse projection.
- screen dimensions.
- near/far planes.
- AO settings.
- small sample kernel.
- per-pixel noise or deterministic rotation.

Output:
- raw AO factor where `1.0` means unoccluded and `0.0` means fully occluded.

Sampling guidance:
- Reconstruct view-space position from depth.
- Use a hemisphere around the view-space normal.
- Scale sample radius by view depth so AO is stable across distance.
- Use a small blue-noise or tiled random rotation texture if available.
- If no noise texture exists, use a deterministic hash from pixel coordinates for the first implementation.
- Clamp depth comparisons with `Bias`.
- Fade AO by distance to reduce far-field crawling.
- Use `Power` and `Intensity` only after the raw occlusion estimate is computed.

Pseudo output:
```glsl
float occlusion = ComputeSsao(...);
float ao = pow(clamp(1.0 - occlusion * intensity, 0.0, 1.0), power);
imageStore(AoRaw, pixel, vec4(ao, 0.0, 0.0, 1.0));
```

Future GTAO path:
- Keep `AmbientOcclusionMode.Gtao` reserved.
- It can initially route to SSAO.
- Implement horizon-search GTAO only after the SSAO target, blur, diagnostics, and shader integration are proven.

## Bilateral Blur

Add a compute blur pass.

Recommended shader:
- `ambient_occlusion_blur.comp`

Inputs:
- raw AO.
- depth.
- optional normals.

Output:
- blurred AO.

Implementation:
- Two-pass separable blur is preferred:
  - horizontal into scratch or blurred target.
  - vertical into final blurred AO.
- For the first implementation, one compute shader can use a push constant direction.
- Weight taps by:
  - pixel distance.
  - depth difference.
  - optional normal similarity.
- Preserve silhouettes by rejecting taps whose depth differs too much.

Targets:
- If using separable blur, add `AmbientOcclusionScratch`.
- If using a single bilateral kernel, raw-to-blurred is enough.

Debug:
- Raw AO view should show expected noisy contact.
- Blurred AO view should smooth noise while preserving object edges.

## Shader Integration

Update `forward.frag`.

Required behavior:
- Sample blurred screen-space AO using `gl_FragCoord.xy / ScreenDimensions`.
- If AO is disabled or texture is missing, use `1.0`.
- Combine material AO and screen-space AO for indirect lighting only.

Recommended combination:
```glsl
float materialAo = ambientOcclusion;
float screenAo = SampleScreenSpaceAo();
float indirectAo = clamp(materialAo * screenAo, 0.0, 1.0);
```

With Phase 6 IBL:
```glsl
vec3 indirectLighting = EvaluateIbl(
    albedo,
    metallic,
    roughness,
    normal,
    viewDirection,
    indirectAo);
```

If Phase 6 is not finished:
```glsl
vec3 ambient = albedo * fallbackAmbientStrength * indirectAo;
```

Default rule:
- `directLighting` is not multiplied by screen-space AO.
- shadow receiver factors are not multiplied by screen-space AO.
- emissive is not multiplied by AO.
- transparent forward shading can ignore AO in the first implementation.

Debug modes:
- raw AO.
- blurred AO.
- final combined AO.
- reconstructed normal.
- linear depth.

## Render Pass Implementation

Add:
- `Njulf.Rendering/Pipeline/AmbientOcclusionPass.cs`
- `Njulf.Rendering/Pipeline/AmbientOcclusionBlurPass.cs`
- optional `Njulf.Rendering/Pipeline/SceneNormalPass.cs`
- optional `Njulf.Rendering/Pipeline/PipelineObjects/AmbientOcclusionPipeline.cs`

`AmbientOcclusionPass` should:
- early-out when disabled, but leave diagnostics in a predictable neutral state.
- transition depth to shader-read layout.
- transition raw AO target to storage write.
- bind compute pipeline.
- dispatch over AO target extent.
- transition raw AO to shader read.
- write CPU record time into `SceneRenderingData`.

`AmbientOcclusionBlurPass` should:
- early-out when disabled or blur radius is zero.
- if blur radius is zero, copy or alias raw AO as final AO.
- transition blurred AO target to storage write.
- bind blur compute pipeline.
- dispatch over AO target extent.
- transition blurred AO to shader read for `ForwardPlusPass`.
- write CPU record time into `SceneRenderingData`.

RenderDoc labels:
- `AmbientOcclusionPass`
- `AmbientOcclusionBlurPass Horizontal`
- `AmbientOcclusionBlurPass Vertical`
- `SceneNormalPass` if used.

Pipeline debug names:
- `Ambient Occlusion Compute Pipeline`
- `Ambient Occlusion Blur Compute Pipeline`
- `Ambient Occlusion Pipeline Layout`
- `Ambient Occlusion Pipeline Cache`

## Data And Push Constants

Add push constants for AO compute.

Suggested struct:
```csharp
public struct GPUAmbientOcclusionPushConstants
{
    public Vector2 SourceDimensions;
    public Vector2 DestinationDimensions;
    public float Radius;
    public float Intensity;
    public float Bias;
    public float Power;
    public int SampleCount;
    public int FrameIndex;
    public int UseSceneNormals;
    public int Mode;
}
```

Blur push constants:
```csharp
public struct GPUAmbientOcclusionBlurPushConstants
{
    public Vector2 Dimensions;
    public Vector2 Direction;
    public int Radius;
    public float DepthSigma;
    public float NormalSigma;
    public int UseSceneNormals;
}
```

Tests:
- struct sizes and alignment match GLSL expectations if included in `GPUStructLayoutTests`.
- sample count values map to shader loops safely.
- disabling blur does not leave the final AO texture in an undefined layout.

## Diagnostics

Extend `SceneRenderingData`:
```csharp
public bool AmbientOcclusionEnabled { get; set; }
public AmbientOcclusionMode AmbientOcclusionMode { get; set; }
public AmbientOcclusionDebugView AmbientOcclusionDebugView { get; set; }
public uint AmbientOcclusionWidth { get; set; }
public uint AmbientOcclusionHeight { get; set; }
public string AmbientOcclusionFormat { get; set; }
public float AmbientOcclusionResolutionScale { get; set; }
public float AmbientOcclusionRadius { get; set; }
public float AmbientOcclusionIntensity { get; set; }
public float AmbientOcclusionBias { get; set; }
public int AmbientOcclusionSampleCount { get; set; }
public int AmbientOcclusionBlurRadius { get; set; }
public long CpuAmbientOcclusionRecordMicroseconds { get; set; }
public long CpuAmbientOcclusionBlurRecordMicroseconds { get; set; }
public long GpuAmbientOcclusionMicroseconds { get; set; }
public long GpuAmbientOcclusionBlurMicroseconds { get; set; }
```

Extend `RendererDiagnostics` with matching fields.

Update:
- `RendererDiagnostics.Empty`.
- diagnostics construction in `VulkanRenderer`.
- `SampleDiagnosticsReporter.PrintFirstFrameDiagnostics`.
- `RendererDiagnosticsTests`.

Sample diagnostics line:
```text
Frame diagnostics AO: enabled=1, mode=Ssao, size=960x540, format=R8Unorm, radius=0.75, intensity=1.00, bias=0.03, samples=16, blur=2, debug=None, aoRecordUs=..., blurRecordUs=...
```

## Sample App Validation

`SampleLighting.cs` does not need new light types for AO. Use existing modes deliberately:
- `DirectionalKey`: validates contact AO with direct sun/key plus IBL.
- `ThreePointDemo`: validates that AO does not over-darken brightly lit contact areas.
- `SpotShadowDemo`: validates that shadowed spot lighting and AO do not double-darken direct light.
- `PointShadowDemo`: validates local point shadows plus AO around nearby objects.

Suggested sample additions:
- Add AO toggles in `SampleInputController`:
  - enable/disable AO.
  - cycle AO debug view.
  - increase/decrease AO radius.
  - increase/decrease AO intensity.
- Print AO diagnostics from `SampleDiagnosticsReporter`.
- If there is a sample material scene, include:
  - a sphere close to a floor plane.
  - a box corner.
  - rough and metallic test materials after Phase 6.

Avoid changing direct light intensities to hide AO artifacts. If AO looks wrong, fix radius, bias, blur, or normal reconstruction.

## Render Graph And Resource Lifetime

Renderer initialization should:
1. Create AO render targets with the current swapchain extent and settings resolution scale.
2. Register raw and blurred AO textures at fixed bindless indices.
3. Initialize AO compute pipelines.
4. Insert AO passes after depth/Hi-Z and before forward shading.
5. Ensure `ForwardPlusPass` sees blurred AO in shader-read layout.

Resize behavior:
- Recreate AO targets when swapchain extent changes.
- Recreate AO descriptor sets when target views change.
- Preserve AO settings across resize.
- Keep fixed bindless indices stable.

Settings-change behavior:
- Resolution scale changes require AO target recreation.
- radius, intensity, bias, sample count, blur radius, and debug view should be safe at runtime.
- switching `UseSceneNormals` should be safe if the normal target/pass exists.

Shutdown behavior:
- AO render targets, descriptor sets, descriptor pools, pipelines, pipeline layouts, and pipeline caches are destroyed once.
- No AO target should outlive `RenderTargetManager`.

## Barriers And Layouts

Expected resource layouts:
- Depth texture:
  - written by `DepthPrePass`.
  - read by `HiZBuildPass`, AO, light culling, and forward debug paths.
- AO raw:
  - undefined or shader-read at frame start.
  - storage write during AO pass.
  - shader read during blur pass and debug preview.
- AO blurred:
  - storage write during blur pass.
  - shader read during forward shading.
- AO scratch if used:
  - storage write/read during blur.

Validation targets:
- no read-after-write hazards between AO and forward.
- no storage image write while sampled.
- raw and blurred AO targets transition cleanly during resize.
- debug views sample from shader-read layouts.

## Quality Tuning

Initial default target:
- half resolution.
- 16 samples.
- radius `0.75`.
- bias `0.03`.
- intensity `1.0`.
- power `1.2`.
- bilateral blur radius `2`.

Tuning guidance:
- Increase bias if flat surfaces self-occlude.
- Decrease radius if halos appear around silhouettes.
- Increase blur radius only after depth-aware edge preservation works.
- Keep intensity below `1.5` unless the scene is intentionally stylized.
- Add distance fade if far surfaces shimmer.
- Use full resolution only for screenshots or high-quality preset.

## Debug Views

AO debug views should be selectable without recompiling shaders.

Required views:
- raw AO.
- blurred AO.
- final combined AO factor.
- linear depth.
- reconstructed normal or scene normal.

Display behavior:
- Raw/blurred/final AO should show white as unoccluded and black as occluded.
- Linear depth should be remapped for visibility.
- Normals should display as `normal * 0.5 + 0.5`.
- Debug AO views should bypass tone mapping if shown in `ToneMapCompositePass`, or output neutral grayscale through the existing composite path.

Implementation options:
- Add AO debug handling to `ToneMapCompositePass` for fullscreen texture previews.
- Add final combined AO debug handling in `forward.frag` because it depends on material AO and screen-space AO together.

## Automated Tests

Unit tests:
- `AmbientOcclusionSettings` defaults and clamping.
- AO target extent calculation.
- AO render target format and usage contract.
- bindless AO constants match shader constants.
- `RendererDiagnostics.Empty` contains disabled AO defaults.
- populated diagnostics copy AO fields from `SceneRenderingData`.
- render graph pass order contains AO passes before `ForwardPlusPass`.

Shader build tests:
- `ambient_occlusion.comp`.
- `ambient_occlusion_blur.comp`.
- optional `scene_normal.task`.
- optional `scene_normal.mesh`.
- updated `forward.frag`.
- updated `tonemap_composite.frag` if AO debug previews are added there.

Optional GPU smoke tests:
- AO targets are created and registered.
- AO compute dispatch executes on a simple scene.
- disabled AO produces white final AO.
- debug raw AO view is non-empty in a corner/contact test scene.

## Implementation Sequence

1. Add `AmbientOcclusionSettings`, `AmbientOcclusionMode`, and `AmbientOcclusionDebugView`.
2. Add AO fields to `SceneRenderingData` and `RendererDiagnostics`.
3. Extend `RenderTargetManager` with AO raw, blurred, and optional scratch targets.
4. Add AO fixed bindless texture indices and update `common.glsl`.
5. Add tests for settings, extents, bindless constants, and diagnostics defaults.
6. Add `ambient_occlusion.comp` with depth reconstruction and SSAO sampling.
7. Add `ambient_occlusion_blur.comp` with bilateral blur.
8. Add `AmbientOcclusionPass`.
9. Add `AmbientOcclusionBlurPass`.
10. Register AO textures and insert AO passes in `VulkanRenderer.InitializeRenderGraph`.
11. Update pass-order validation.
12. Integrate blurred AO sampling into `forward.frag`.
13. Add AO debug views.
14. Update `SampleDiagnosticsReporter` and optional sample input controls.
15. Run shader compilation tests and CPU tests.
16. Run `NjulfHelloGame` with Vulkan validation enabled.
17. Capture a RenderDoc frame and verify AO targets, pass order, barriers, and debug views.
18. Tune defaults against `DirectionalKey`, `ThreePointDemo`, `SpotShadowDemo`, and `PointShadowDemo`.

## Acceptance Criteria

Phase 7 is complete when:
1. The renderer produces raw and blurred screen-space AO textures.
2. AO is applied to indirect lighting, not direct lighting, shadows, or emissive output.
3. Corners, creases, and contact points gain visible depth in `NjulfHelloGame`.
4. AO does not create obvious halos around silhouettes at default settings.
5. AO can be enabled and disabled at runtime.
6. Radius, intensity, bias, sample count, resolution scale, and blur radius are configurable.
7. Raw AO, blurred AO, final AO, depth, and normal debug views are available.
8. Renderer diagnostics report AO settings, target size, format, and pass cost.
9. Resize and shutdown are validation-clean.
10. RenderDoc shows AO passes before `ForwardPlusPass`, with named images, views, pipelines, and debug labels.

## Phase 7 Definition Of Done

The renderer should be able to show a material-rich sample scene where:
- sky/IBL provides believable indirect fill.
- AO adds contact grounding under objects and in architectural corners.
- direct lights and shadows stay physically readable instead of being globally dirtied.
- rough metallic and non-metallic materials keep their Phase 6 IBL behavior.
- AO cost is visible in diagnostics and cheap enough for the default quality level.
- disabling AO makes the difference obvious without breaking the lighting pipeline.
