# Phase 9: Fog And Atmospheric Depth Detailed Implementation Plan

Goal: give scenes depth, scale, and mood by adding production-safe distance fog, height fog, and optional directional inscattering that integrate with HDR lighting, sky/IBL, exposure, bloom, AO, and anti-aliasing. Phase 9 should make large spaces read better without turning fog into a flat LDR overlay.

## Recommendation

Implement analytic fog first:
- exponential distance fog.
- exponential height fog.
- optional directional inscattering from the sun/key light.
- optional sky/environment color sampling for fog color.

Do not start with volumetric fog. Volumetric fog is valuable, but it adds froxel grids, temporal reprojection, shadow integration, noise, and a much larger performance/debug surface. The industry-standard production path for this renderer is:
- Phase 9 first slice: analytic distance and height fog in HDR.
- Later extension: local fog volumes.
- Later extension: volumetric froxel fog if the game needs visible light shafts, god rays, or heavy atmospheric effects.

Fog should be applied before tone mapping and before anti-aliasing. It is part of scene radiance, so it should participate naturally in exposure and bloom. For this renderer, the cleanest first implementation is a fullscreen `FogPass` that reads HDR scene color and scene depth after opaque, sky, and transparent rendering, then writes fogged HDR scene color before bloom and tone mapping.

## Assumptions

Phase 9 assumes:
- Phase 2 HDR scene color and tone-map composite are complete.
- Phase 3 bloom is complete.
- Phase 6 sky/environment lighting and IBL are complete or planned.
- Phase 7 AO may be complete or planned.
- Phase 8 anti-aliasing may be complete or planned.
- The renderer has depth prepass output registered as a bindless texture.
- `ForwardPlusPass`, `SkyboxPass`, and `TransparentForwardPass` render into HDR scene color.
- `ToneMapCompositePass` performs exposure, bloom combine, tone mapping, and swapchain/LDR output.
- `SampleLighting.cs` direct-light presets can provide a sun/key direction for fog inscattering validation.

## Current Baseline

Relevant existing behavior:
- HDR scene color exists as `RenderTargetManager.SceneColor`.
- Bloom consumes HDR scene color before final composite.
- `ToneMapCompositePass` samples HDR scene color and bloom, applies exposure and tone mapping, and writes final output.
- Depth is registered as a fixed bindless texture.
- `SceneRenderingData` contains view/projection, camera position, screen size, and timing fields for existing passes.
- There are no fog settings, fog pass, fog diagnostics, or fog debug views.
- `SampleLightingMode.DirectionalKey` provides a directional light that can behave like a sun/key light.

## Target Frame Shape

Final Phase 9 pass order with analytic fog:
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
13. `FogPass`
14. `BloomPass`
15. `ToneMapCompositePass`
16. `AntiAliasingPass`
17. Present

Recommended first implementation:
- `ForwardPlusPass`, `SkyboxPass`, and `TransparentForwardPass` render unfogged HDR scene color.
- `FogPass` reads HDR scene color and depth, computes fog transmittance and inscattering, and writes to a separate HDR fogged color target.
- Bloom and tone mapping consume the fogged HDR scene color.

Alternative lower-pass implementation:
- apply fog directly in `ForwardPlusPass` and `TransparentForwardPass`.
- apply sky fog inside `SkyboxPass`.
- this avoids a fullscreen pass but duplicates fog code and makes debug views harder.

Use the fullscreen `FogPass` first. It is easier to debug, easier to turn on/off, and centralizes the math.

## Scope

Phase 9 includes:
- Fog settings under `RenderSettings`.
- A renderer-owned fogged HDR scene color target or ping-pong support.
- A fullscreen or compute `FogPass`.
- Exponential distance fog.
- Exponential height fog.
- Optional directional inscattering from sun/key light direction.
- Fog color sourced from explicit color, sky/environment approximation, or both.
- Depth reconstruction from the depth texture.
- Runtime controls for density, height, height falloff, start distance, max opacity, color, and inscattering.
- Debug views for fog factor, transmittance, inscattering, depth, height contribution, and final fogged scene.
- Diagnostics for fog mode, settings, target format/size, and pass cost.
- Sample app validation using existing lighting modes.

Phase 9 does not include:
- Froxel volumetric fog.
- Volumetric shadowing.
- Participating media per light.
- Cloud rendering.
- Weather systems.
- Local fog volumes as a required first implementation.
- Fog-of-war or gameplay visibility fog.
- Underwater rendering.

## Core Design Decisions

1. Fog is HDR scene radiance. Apply it before bloom, tone mapping, and anti-aliasing.
2. Use physically plausible transmittance math, but expose art-directable controls.
3. Start with global analytic fog. Local fog volumes come later if content requires them.
4. Keep direct light and shadow behavior independent from fog in the first slice. Directional inscattering can use key-light direction and color, but do not attempt full volumetric shadowing yet.
5. Integrate fog with sky color. Fog should converge toward the horizon/sky/environment color, not arbitrary gray, unless the scene deliberately chooses a stylized fog color.
6. Preserve debug modes and RenderDoc inspectability. Fog density and transmittance should be verifiable without guessing from the final image.
7. Make transparent behavior explicit. First implementation can fog the composed transparent result as a screen-space approximation; later transparent materials can apply per-fragment fog for more accurate layering.

## Settings

Add `FogSettings` under `RenderSettings`.

Suggested API:
```csharp
public enum FogMode : uint
{
    Disabled = 0,
    Distance = 1,
    Height = 2,
    DistanceAndHeight = 3
}

public enum FogColorMode : uint
{
    ConstantColor = 0,
    SkyColor = 1,
    SkyAndConstantBlend = 2
}

public enum FogDebugView : uint
{
    None = 0,
    FogFactor = 1,
    Transmittance = 2,
    DistanceFog = 3,
    HeightFog = 4,
    Inscattering = 5,
    LinearDepth = 6,
    WorldHeight = 7,
    FoggedScene = 8
}

public sealed class FogSettings
{
    public bool Enabled { get; set; } = true;
    public FogMode Mode { get; set; } = FogMode.DistanceAndHeight;
    public FogColorMode ColorMode { get; set; } = FogColorMode.SkyAndConstantBlend;
    public Vector3 Color { get; set; } = new(0.62f, 0.72f, 0.82f);
    public float ColorBlend { get; set; } = 0.5f;
    public float Density { get; set; } = 0.015f;
    public float StartDistance { get; set; } = 5.0f;
    public float EndDistance { get; set; } = 250.0f;
    public float Height { get; set; } = 0.0f;
    public float HeightFalloff { get; set; } = 0.12f;
    public float HeightDensity { get; set; } = 0.04f;
    public float MaxOpacity { get; set; } = 0.85f;
    public bool DirectionalInscatteringEnabled { get; set; } = true;
    public Vector3 DirectionalInscatteringColor { get; set; } = new(1.0f, 0.88f, 0.68f);
    public float DirectionalInscatteringIntensity { get; set; } = 0.35f;
    public float DirectionalInscatteringExponent { get; set; } = 8.0f;
    public FogDebugView DebugView { get; set; } = FogDebugView.None;
}
```

Recommended constraints:
- `Density`: `0.0` to `1.0`.
- `StartDistance`: `0.0` to `10000.0`.
- `EndDistance`: at least `StartDistance + 0.01`.
- `HeightFalloff`: `0.001` to `10.0`.
- `HeightDensity`: `0.0` to `1.0`.
- `MaxOpacity`: `0.0` to `1.0`.
- `ColorBlend`: `0.0` to `1.0`.
- `DirectionalInscatteringIntensity`: `0.0` to `8.0`.
- `DirectionalInscatteringExponent`: `1.0` to `128.0`.

Tests:
- defaults produce subtle outdoor fog.
- disabled fog produces identical HDR scene color.
- invalid settings clamp safely.
- `EndDistance` never becomes less than or equal to `StartDistance`.
- debug view values map to shader constants.

## Fog Math

Use physically inspired transmittance:
```glsl
vec3 foggedColor = sceneColor * transmittance + fogRadiance * (1.0 - transmittance);
```

Distance fog:
```glsl
float distancePastStart = max(viewDistance - startDistance, 0.0);
float distanceOpticalDepth = distancePastStart * density;
float distanceTransmittance = exp(-distanceOpticalDepth);
```

Optional range-limited distance fog:
```glsl
float linearRangeFactor = smoothstep(startDistance, endDistance, viewDistance);
```

Height fog:
- Reconstruct world position from depth.
- Integrate an exponential density field along the camera-to-pixel ray.
- Use an approximation that is stable and cheap:
```glsl
float cameraHeight = cameraPosition.y - fogHeight;
float pointHeight = worldPosition.y - fogHeight;
float averageDensity = exp(-heightFalloff * max(min(cameraHeight, pointHeight), 0.0));
float heightOpticalDepth = viewDistance * heightDensity * averageDensity;
float heightTransmittance = exp(-heightOpticalDepth);
```

Combine:
```glsl
float transmittance = distanceTransmittance * heightTransmittance;
float fogFactor = clamp(1.0 - transmittance, 0.0, maxOpacity);
```

Directional inscattering:
```glsl
float sunAmount = pow(max(dot(viewDirection, -sunDirection), 0.0), exponent);
vec3 inscattering = directionalColor * directionalIntensity * sunAmount * fogFactor;
```

Final:
```glsl
vec3 result = mix(sceneColor, fogColor + inscattering, fogFactor);
```

The first implementation should prioritize stability and art direction over strict atmospheric scattering. Keep the math compact and well documented so volumetric fog can replace or augment it later.

## Color Integration

Fog color options:
- constant color for art-directed interiors.
- sky/environment color for outdoor horizon matching.
- blend between constant and sky color.

Recommended first implementation:
- pass a constant fog color from settings.
- if Phase 6 sky/environment exists, sample an environment approximation using the view direction for `SkyColor` mode.
- if environment sampling is not available, use `ClearColor` or a configured fallback sky color.

Guidance:
- keep fog color in linear HDR space.
- fog color should be multiplied by exposure only in the existing tone-map path, not inside fog math.
- directional inscattering may exceed `1.0` before tone mapping, allowing bright sun haze to feed bloom naturally.

## Depth And Position Reconstruction

`FogPass` needs:
- depth texture.
- inverse view-projection matrix.
- camera position.
- near/far or reverse-Z convention.
- screen dimensions.

Implementation:
- sample depth at current UV.
- if depth indicates sky/background, either:
  - apply sky/horizon fog using a far virtual distance, or
  - leave skybox output unchanged if sky already contains atmospheric color.
- reconstruct world position for geometry pixels.
- compute view direction and view distance.

Reverse-Z note:
- document whether depth is reverse-Z or regular-Z.
- add shader helpers in `common.glsl` rather than duplicating depth conversion.
- test near, far, and sky/background depth values explicitly.

Debug views:
- linear depth.
- world height.
- fog factor.
- transmittance.

## Render Targets

Extend `RenderTargetManager`.

Recommended target:
- `FoggedSceneColor`
  - format: same as `SceneColor`, `R16G16B16A16Sfloat`.
  - extent: swapchain extent.
  - usage: sampled, storage or color attachment, transfer source if needed.

Implementation options:
1. Ping-pong HDR scene color:
   - input: `SceneColor`.
   - output: `FoggedSceneColor`.
   - bloom and tone mapping sample `FoggedSceneColor` when fog is enabled.
2. In-place storage write:
   - read and write `SceneColor` in one compute pass is unsafe without careful barriers and separate images.
   - avoid this for the first implementation.

Recommended first path:
- use `FoggedSceneColor`.
- register it at a fixed bindless index.
- keep `SceneColor` as the unfogged scene for debug comparison.

Debug names:
- `Fogged HDR Scene Color`
- `Fog Pass Pipeline`
- `Fog Pass Pipeline Layout`
- `Fog Pass Pipeline Cache`

Tests:
- fog target matches scene color format and swapchain extent.
- fog target recreates on resize.
- fog disabled does not leave bloom/composite sampling an undefined target.

## Bindless Index Additions

Extend `BindlessIndexTable.cs` and `common.glsl`.

If Phase 6, 7, and 8 fixed texture slots have landed, place fog after AA targets:
```csharp
public const int FoggedSceneColorTexture = TaaHistoryTexture + 1;
public const int FirstDynamicTextureIndex = FoggedSceneColorTexture + 1;
```

If earlier phase fixed texture additions are still unmerged, add fog after the current final renderer-owned texture and reconcile when phases are merged. Keep fixed renderer textures contiguous and update tests.

Update:
- `BindlessIndexTests.ShaderConstants_MatchHostBindlessIndices`.
- first dynamic texture index assertions.
- texture index name coverage if needed.

## Pass Implementation

Add:
- `Njulf.Rendering/Pipeline/FogPass.cs`
- `Njulf.Shaders/fog.comp` or `Njulf.Shaders/fog.frag`

Recommended first implementation:
- compute shader.
- input sampled textures:
  - HDR scene color.
  - depth.
  - optional environment/sky texture if Phase 6 provides it.
- output storage image:
  - `FoggedSceneColor`.
- dispatch over swapchain extent.

Why compute:
- no render-pass attachment setup.
- simple storage target writes.
- easy debug labels.
- easy future extension for low-resolution volumetric composition.

`FogPass` should:
- early-out when disabled.
- transition `SceneColor` to shader read.
- transition depth to shader read.
- transition `FoggedSceneColor` to storage write.
- bind fog compute pipeline and descriptors.
- push fog settings.
- dispatch full resolution.
- transition `FoggedSceneColor` to shader read.
- update `SceneRenderingData` with fog settings and CPU record timing.

Disabled behavior:
- either make bloom/composite sample `SceneColor` directly, or copy `SceneColor` to `FoggedSceneColor`.
- recommended: route active scene color texture index through scene data/settings so no copy is needed.

RenderDoc labels:
- `FogPass`
- `FogPass Disabled`
- `FogPass Debug Fog Factor`

## Shader Data

Add push constants or a fixed environment/fog buffer.

Suggested push constants:
```csharp
public struct GPUFogPushConstants
{
    public Matrix4x4 InverseViewProjectionMatrix;
    public Vector4 CameraPositionAndTime;
    public Vector4 ScreenDimensions;
    public Vector4 FogColorAndDensity;
    public Vector4 FogHeightParams;
    public Vector4 FogDistanceParams;
    public Vector4 DirectionalInscatteringColorAndIntensity;
    public Vector4 DirectionalInscatteringDirectionAndExponent;
    public uint SceneColorTextureIndex;
    public uint DepthTextureIndex;
    public uint OutputTextureIndex;
    public uint Mode;
    public uint ColorMode;
    public uint DebugView;
    public uint DirectionalInscatteringEnabled;
    public uint Padding0;
}
```

If this exceeds push-constant limits, move fog settings to a fixed storage/uniform buffer and keep only texture indices plus flags in push constants.

Tests:
- struct layout matches GLSL constants if added to layout tests.
- push constant size is under device limit.
- mode/debug enum values match shader constants.

## Forward Shader Integration Alternative

If the fullscreen fog pass is not acceptable, integrate fog into shaders:
- `forward.frag` computes fog per opaque pixel.
- `transparent_forward.frag` computes fog per transparent pixel.
- `skybox.frag` applies horizon/sun haze.

Pros:
- no extra HDR target.
- accurate per-transparent-layer fog if implemented carefully.

Cons:
- duplicated code.
- harder to compare unfogged/fogged scene.
- more shader variants/debug branches.
- transparent and sky integration becomes easy to get inconsistent.

Keep this as a later optimization only if the fullscreen pass cost is measurable and significant.

## Transparency

First implementation:
- apply fog after transparent composition as a screen-space approximation.
- this fogs the final transparent color based on the front-most depth.
- acceptable for simple transparent props and early production validation.

Known limitation:
- layered transparency will not fog each transparent layer at its own depth.
- alpha-blended particles may need per-particle fog later.

Future improvement:
- add per-fragment fog to `TransparentForwardPass`.
- soft particles and volumetric VFX in Phase 13 should sample or compute fog consistently.

## Sky And Environment Interaction

Skybox handling options:
1. Fog geometry only; leave skybox unchanged.
2. Fog skybox using a far virtual distance.
3. Rely on sky/environment itself to represent atmospheric color.

Recommended first behavior:
- geometry fog converges toward sky/environment color.
- skybox is not heavily fogged unless debug/art settings request it.
- horizon color should visually match fog color to avoid a hard seam.

If Phase 6 environment data exists:
- use sun/key direction from the first directional light or environment settings.
- use sky/environment color for `FogColorMode.SkyColor`.
- keep explicit fallback color for scenes without sky.

## Directional Inscattering

Use `SampleLightingMode.DirectionalKey` as a sun/key validation mode.

Data source options:
- first directional light from `LightManager`.
- environment sun direction if Phase 6 exposes one.
- explicit `FogSettings.DirectionalInscatteringDirection`.

Recommended priority:
1. explicit fog sun direction if set.
2. environment sun direction.
3. first directional light direction.
4. fallback direction from settings.

Behavior:
- color should default warm but be controllable.
- intensity should be HDR-capable.
- exponent controls forward-scattering lobe tightness.
- no shadowed volumetric shafts in first implementation.

## Diagnostics

Extend `SceneRenderingData`:
```csharp
public bool FogEnabled { get; set; }
public FogMode FogMode { get; set; }
public FogColorMode FogColorMode { get; set; }
public FogDebugView FogDebugView { get; set; }
public float FogDensity { get; set; }
public float FogStartDistance { get; set; }
public float FogEndDistance { get; set; }
public float FogHeight { get; set; }
public float FogHeightFalloff { get; set; }
public float FogHeightDensity { get; set; }
public float FogMaxOpacity { get; set; }
public int FogDirectionalInscatteringEnabled { get; set; }
public uint FogWidth { get; set; }
public uint FogHeightPixels { get; set; }
public string FogFormat { get; set; }
public long CpuFogRecordMicroseconds { get; set; }
public long GpuFogMicroseconds { get; set; }
```

Extend `RendererDiagnostics` with matching fields.

Update:
- `RendererDiagnostics.Empty`.
- diagnostics construction in `VulkanRenderer`.
- `SampleDiagnosticsReporter.PrintFirstFrameDiagnostics`.
- `RendererDiagnosticsTests`.

Sample diagnostics line:
```text
Frame diagnostics fog: enabled=1, mode=DistanceAndHeight, colorMode=SkyAndConstantBlend, density=0.015, start=5.0, end=250.0, height=0.0, falloff=0.12, maxOpacity=0.85, inscatter=1, size=1920x1080, format=R16G16B16A16Sfloat, debug=None, fogRecordUs=...
```

## Sample App Validation

`SampleLighting.cs` does not need new light types for fog. Use the existing modes:
- `DirectionalKey`: primary outdoor sun/haze validation.
- `ThreePointDemo`: indoor or studio fog should be subtle or disabled; validates that fog is art-directable.
- `SpotShadowDemo`: validates that local lighting still reads under fog and that no fake volumetric shafts are implied.
- `PointShadowDemo`: validates bright local lights in hazy scenes without full volumetric scattering.

Suggested sample controls:
- toggle fog.
- cycle fog debug view.
- increase/decrease density.
- increase/decrease height density.
- increase/decrease start distance.
- toggle directional inscattering.

Suggested sample scenes:
- long corridor or Sponza hallway for distance depth separation.
- outdoor plaza with skybox for horizon matching.
- low camera in a ground fog scene for height fog.
- high camera above fog layer to verify height falloff.

## Render Graph And Resource Lifetime

Renderer initialization should:
1. Create `FoggedSceneColor`.
2. Register fogged scene texture at a fixed bindless index.
3. Initialize fog pipeline.
4. Insert `FogPass` after transparent scene rendering and before bloom.
5. Route bloom and tone mapping to the active scene color:
   - fog disabled: `SceneColor`.
   - fog enabled: `FoggedSceneColor`.

Resize behavior:
- recreate `FoggedSceneColor`.
- recreate descriptor sets that reference it.
- keep fog settings unchanged.

Runtime settings behavior:
- density/color/debug/mode changes should not recreate resources.
- enabling/disabling fog should only affect pass execution and active scene color routing.

Shutdown behavior:
- fog target, descriptor pool, descriptor set layout, pipeline, pipeline layout, and pipeline cache are destroyed once.

## Barriers And Layouts

Expected layouts:
- `SceneColor`
  - color attachment during forward/sky/transparent.
  - shader read during `FogPass`.
- depth texture
  - depth attachment during `DepthPrePass`.
  - shader read during `FogPass`.
- `FoggedSceneColor`
  - storage write during `FogPass`.
  - shader read during `BloomPass` and `ToneMapCompositePass`.

Validation targets:
- no read/write hazard between scene color and fog output.
- bloom samples fogged color only after fog pass completes.
- tone map samples the same active scene color as bloom.
- disabled fog path does not sample undefined `FoggedSceneColor`.
- resize and shutdown are validation-clean.

## Quality Tuning

Initial outdoor defaults:
- mode: `DistanceAndHeight`.
- color mode: `SkyAndConstantBlend`.
- density: `0.015`.
- start distance: `5.0`.
- end distance: `250.0`.
- height: `0.0`.
- height density: `0.04`.
- height falloff: `0.12`.
- max opacity: `0.85`.
- directional inscattering: enabled.
- inscattering intensity: `0.35`.
- inscattering exponent: `8.0`.

Tuning guidance:
- lower density for material review scenes.
- increase start distance indoors to avoid washing out nearby assets.
- reduce max opacity if horizon becomes flat.
- use height fog to ground valleys, floors, and low outdoor spaces.
- keep inscattering subtle until the scene has a strong sun direction.
- verify fog under different exposure values.

## Debug Views

Required views:
- fog factor.
- transmittance.
- distance fog contribution.
- height fog contribution.
- directional inscattering.
- linear depth.
- world height.
- final fogged scene.

Display behavior:
- fog factor: black no fog, white max fog.
- transmittance: black fully attenuated, white clear.
- inscattering: HDR clamped or tone-mapped for display.
- depth: remapped for visible range.
- world height: remapped around configured fog height.

Debug views should bypass bloom where appropriate. For example, a fog-factor debug view should not bloom simply because it contains white pixels.

## Performance

First implementation cost:
- one full-resolution compute pass.
- one extra HDR render target.
- a few texture reads and exponential operations per pixel.

Expected cost:
- much cheaper than shadow rendering, AO, SMAA+TAA, or volumetric fog.
- measurable in pass diagnostics.

Optimization options if needed:
- half-resolution fog with bilateral upsample.
- merge fog with tone-map composite only if debug/ordering constraints remain clean.
- analytic fog inside forward shaders if fullscreen cost dominates on target hardware.

Do not optimize before diagnostics show a problem.

## Future Volumetric Fog Extension

If the game needs light shafts or dense atmosphere, add a later volumetric path:
- froxel grid aligned to camera frustum.
- low resolution, e.g. `160x90x64` or quality-scaled.
- density injection from global fog plus local volumes.
- lighting injection from directional and local lights.
- optional shadow map sampling.
- temporal reprojection with history rejection.
- bilateral upsample/composite into HDR scene color.

This should not block Phase 9. Analytic fog provides most of the depth and mood improvement for much lower complexity.

## Automated Tests

Unit tests:
- `FogSettings` defaults and clamping.
- `EndDistance` is always greater than `StartDistance`.
- fog target format/extent matches scene color.
- bindless fog constants match shader constants.
- active scene color routing selects fogged target only when fog is enabled.
- `RendererDiagnostics.Empty` contains disabled fog defaults.
- populated diagnostics copy fog fields from `SceneRenderingData`.
- render graph pass order contains `FogPass` before `BloomPass`.

Shader build tests:
- `fog.comp` or `fog.frag`.
- updated `common.glsl`.
- updated `tonemap_composite.frag` if active scene color routing changes there.

Optional GPU smoke tests:
- disabled fog output matches unfogged scene color.
- enabled fog produces non-zero difference on far geometry.
- fog factor debug view is non-empty in a deep scene.
- resize recreates fog target and remains validation-clean.

## Implementation Sequence

1. Add `FogSettings`, `FogMode`, `FogColorMode`, and `FogDebugView`.
2. Add fog fields to `SceneRenderingData` and `RendererDiagnostics`.
3. Extend `RenderTargetManager` with `FoggedSceneColor`.
4. Add fixed bindless texture index for `FoggedSceneColor`.
5. Add tests for settings, target creation, bindless constants, diagnostics, and pass order.
6. Add `fog.comp` with depth reconstruction, distance fog, height fog, and debug outputs.
7. Add `FogPass`.
8. Register fog target in `VulkanRenderer`.
9. Insert `FogPass` after transparent rendering and before bloom.
10. Route bloom and tone mapping to `SceneColor` or `FoggedSceneColor` depending on fog state.
11. Add directional inscattering using explicit settings, environment sun, or first directional light.
12. Add sample input controls and diagnostics output.
13. Run shader compilation and CPU tests.
14. Run `NjulfHelloGame` with Vulkan validation enabled.
15. Capture RenderDoc frames with fog disabled, distance fog, height fog, and debug views.
16. Tune defaults against outdoor, corridor, and low-ground-fog sample scenes.

## Acceptance Criteria

Phase 9 is complete when:
1. Distance fog and height fog are available and configurable.
2. Fog is applied in HDR before bloom, tone mapping, and anti-aliasing.
3. Fog integrates with sky/environment color or has a clear fallback color.
4. Directional inscattering from a sun/key direction is available and controllable.
5. Large scenes gain depth separation without washing out nearby gameplay space.
6. Fog can be enabled, disabled, and debugged at runtime.
7. Debug views expose fog factor, transmittance, distance contribution, height contribution, inscattering, depth, and world height.
8. Renderer diagnostics report fog settings, target size/format, and pass cost.
9. Resize, mode switching, and shutdown are validation-clean.
10. RenderDoc shows `FogPass` between scene rendering and bloom, with named resources and clear image layout transitions.

## Phase 9 Definition Of Done

The renderer should be able to show outdoor and indoor scenes where fog adds believable depth, horizon cohesion, and mood without hiding material quality or breaking lighting. Distant geometry should recede naturally, low areas can hold height fog, sun-facing views can show subtle warm haze, and all fog behavior should remain measurable, debuggable, and art-directable.
