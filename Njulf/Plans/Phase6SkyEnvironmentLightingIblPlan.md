# Phase 6: Sky, Environment Lighting, And IBL Detailed Implementation Plan

Goal: make PBR materials read correctly under indirect light by adding a sky/environment source, diffuse irradiance, prefiltered specular reflections, and a BRDF integration LUT. Phase 6 should replace the current hard-coded ambient approximation in `forward.frag` with physically plausible image-based lighting while keeping a cheap fallback for scenes without an HDR environment asset.

Phase 6 assumes:
- Phase 2 HDR scene color and tone-map composite are complete.
- Phase 3 bloom is complete.
- Phase 4 directional shadows and Phase 5 local shadows are complete or mostly complete.
- `ForwardPlusPass` and `TransparentForwardPass` render into HDR scene color.
- `forward.frag` already evaluates direct PBR lighting, shadowing, material textures, emissive, and ambient occlusion.
- `SampleLighting.cs` currently configures direct-light presets only; sky and environment lighting should become renderer or scene environment state, not another fake fill-light preset.

## Current Baseline

Relevant existing behavior:
- `forward.frag` computes:
  - material albedo, normal, metallic, roughness, ambient occlusion, and emissive.
  - direct lighting from directional, spot, and point lights.
  - shadow factors for directional and local shadows.
  - `vec3 ambient = albedo * 0.08 * ambientOcclusion;`.
  - final color as `ambient + directLighting + emissive`.
- `RenderSettings` currently owns exposure, tone mapper, shadow settings, and bloom settings.
- `RendererDiagnostics` reports HDR, bloom, shadow, culling, upload, and asset state.
- `BindlessIndexTable.cs` reserves fixed texture slots through:
  - default material textures.
  - depth and Hi-Z.
  - HDR scene color.
  - bloom mip chain.
  - directional shadow cascades.
  - spot shadow atlas.
  - point shadow cubemap array.
  - dynamic material texture range.
- `TextureManager` supports regular 2D sampled textures and dynamic bindless allocation, but not yet cubemap image creation, HDR floating-point image loading, cubemap views, or environment filtering outputs.
- `NjulfHelloGame/SampleLighting.cs` has direct-light modes:
  - `DirectionalKey`.
  - `ThreePointDemo`.
  - `SpotShadowDemo`.
  - `PointShadowDemo`.

## Target Frame Shape

Final Phase 6 pass order:
1. `DirectionalShadowPass`
2. `SpotShadowPass`
3. `PointShadowPass`
4. `DepthPrePass`
5. `HiZBuildPass`
6. `TiledLightCullingPass`
7. `ForwardPlusPass`
8. `SkyboxPass`
9. `TransparentForwardPass`
10. `BloomPass`
11. `ToneMapCompositePass`
12. Present

`SkyboxPass` should render into HDR scene color. Put it after opaque geometry so it only shades uncovered background pixels, and before transparent rendering so transparent objects blend over the sky. If reverse-Z depth makes sky depth testing awkward, document the chosen depth compare/write behavior and verify it in RenderDoc.

Environment map precomputation is not a regular frame pass. It should run at environment-load time or renderer initialization, with clear debug names and explicit image layout transitions.

## Scope

Phase 6 includes:
- Environment settings and scene environment state.
- HDR equirectangular import or conversion path.
- GPU cubemap resource support.
- Skybox rendering from an environment cubemap.
- Diffuse irradiance cubemap generation or loading.
- Prefiltered specular environment cubemap generation or loading.
- BRDF integration LUT generation or baked startup upload.
- Forward shader IBL integration.
- Roughness-aware specular environment reflections.
- Material ambient occlusion applied to indirect lighting.
- Procedural fallback sky and neutral fallback IBL.
- Debug views for skybox, irradiance, prefiltered environment mips, BRDF LUT, and IBL contribution.
- Diagnostics for active environment, texture sizes, mip counts, and fallback state.
- `NjulfHelloGame` sample controls or presets that demonstrate direct-only versus sky/IBL lighting.

Phase 6 does not include:
- Dynamic reflection probes.
- Probe blending volumes.
- Screen-space reflections.
- Planar reflections.
- Time-of-day atmospheric scattering.
- Volumetric clouds.
- Full editor UI for environment authoring.
- KTX2/Basis compressed HDR pipeline. That belongs with the later asset-pipeline phase unless it becomes necessary immediately.

## Core Design Decisions

1. Keep environment lighting separate from `LightManager`. Direct lights remain direct-light data. Sky and IBL are scene environment data.
2. Use fixed bindless texture indices for renderer-owned environment resources. This keeps shader constants stable and avoids treating IBL maps as ordinary material textures.
3. Use cubemap arrays or regular cubemap views only where the shader needs cubemap sampling. Do not fake cubemaps with six unrelated 2D textures.
4. Prefer offline or load-time filtering over per-frame filtering. Environment filtering should not cost frame time after the environment is ready.
5. Start with one active global environment. Reflection probes and blended environments come later.
6. Keep the fallback path explicit. A missing HDRI should produce a named procedural sky and neutral IBL, not silently reintroduce arbitrary ambient constants.
7. Apply material AO to indirect diffuse and indirect specular occlusion conservatively. Do not multiply AO into direct light unless a stylized mode is intentionally added later.
8. Make every intermediate texture view inspectable in RenderDoc.

## Settings

Add `EnvironmentSettings` under `RenderSettings`.

Suggested API:
```csharp
public enum EnvironmentDebugView : uint
{
    None = 0,
    SkyboxOnly = 1,
    IrradianceCubemap = 2,
    PrefilteredEnvironmentMip = 3,
    BrdfLut = 4,
    DiffuseIblOnly = 5,
    SpecularIblOnly = 6,
    AmbientOcclusion = 7
}

public enum EnvironmentSourceKind : uint
{
    ProceduralSky = 0,
    HdrEquirectangular = 1,
    Cubemap = 2
}

public sealed class EnvironmentSettings
{
    public bool Enabled { get; set; } = true;
    public EnvironmentSourceKind SourceKind { get; set; } = EnvironmentSourceKind.ProceduralSky;
    public string? SourcePath { get; set; }
    public float SkyIntensity { get; set; } = 1.0f;
    public float DiffuseIntensity { get; set; } = 1.0f;
    public float SpecularIntensity { get; set; } = 1.0f;
    public float RotationRadians { get; set; }
    public uint EnvironmentSize { get; set; } = 1024;
    public uint IrradianceSize { get; set; } = 64;
    public uint PrefilteredSize { get; set; } = 256;
    public uint BrdfLutSize { get; set; } = 256;
    public EnvironmentDebugView DebugView { get; set; } = EnvironmentDebugView.None;
    public int DebugMipLevel { get; set; }
}
```

Recommended constraints:
- `EnvironmentSize`: power of two, `256` to `4096`.
- `IrradianceSize`: power of two, `16` to `256`.
- `PrefilteredSize`: power of two, `64` to `1024`.
- `BrdfLutSize`: power of two, `128` to `512`.
- `SkyIntensity`, `DiffuseIntensity`, and `SpecularIntensity`: clamp to `0.0` through `16.0`.
- `DebugMipLevel`: clamp to available prefiltered mip count.

Tests:
- default settings enable procedural environment.
- invalid sizes clamp to supported power-of-two ranges.
- debug mip level clamps to valid mip count.
- environment disabled results in black IBL contribution while still leaving direct lighting intact.

## Scene Environment Model

Add a small scene-level object separate from direct lights.

Suggested files:
- `Njulf.Rendering/Data/EnvironmentSettings.cs`
- `Njulf.Rendering/Resources/EnvironmentManager.cs`
- `Njulf.Rendering/Resources/EnvironmentResources.cs`
- `Njulf.Rendering/Data/GPUEnvironmentData.cs`

Suggested CPU data:
```csharp
public sealed class SceneEnvironment
{
    public EnvironmentSourceKind SourceKind { get; set; }
    public string? SourcePath { get; set; }
    public float SkyIntensity { get; set; } = 1.0f;
    public float DiffuseIntensity { get; set; } = 1.0f;
    public float SpecularIntensity { get; set; } = 1.0f;
    public float RotationRadians { get; set; }
}
```

Suggested GPU data:
```csharp
public struct GPUEnvironmentData
{
    public int EnvironmentTextureIndex;
    public int IrradianceTextureIndex;
    public int PrefilteredTextureIndex;
    public int BrdfLutTextureIndex;
    public float SkyIntensity;
    public float DiffuseIntensity;
    public float SpecularIntensity;
    public float RotationRadians;
    public uint PrefilteredMipCount;
    public uint Enabled;
    public uint DebugView;
    public uint DebugMipLevel;
}
```

Keep this data in a fixed bindless storage buffer if it grows, or push constants if it stays small. Prefer a fixed buffer if `forward.frag`, `skybox.frag`, and `tonemap_composite.frag` all need the same environment state.

## Bindless Index Additions

Extend `BindlessIndexTable.cs` and matching shader constants.

Recommended texture slots:
```csharp
public const int EnvironmentCubemapTexture = PointShadowCubemapArrayTexture + 1;
public const int IrradianceCubemapTexture = EnvironmentCubemapTexture + 1;
public const int PrefilteredEnvironmentTexture = IrradianceCubemapTexture + 1;
public const int BrdfLutTexture = PrefilteredEnvironmentTexture + 1;
public const int FirstDynamicTextureIndex = BrdfLutTexture + 1;
```

Recommended buffer slot:
```csharp
public const int EnvironmentDataBuffer = LocalShadowMeshletDrawBufferBase + LocalShadowMeshletDrawBufferCount;
public const int StaticBufferCount = EnvironmentDataBuffer + 1;
```

Update tests:
- `BindlessIndexTests.ShaderConstants_MatchHostBindlessIndices`.
- `PerFrameBufferIndices_AreDerivedFromFramesInFlight`.
- texture first-dynamic-index assertions.
- static buffer uniqueness and contiguity.

Migration note:
- This changes the first dynamic material texture index. Existing saved material references should not persist raw bindless indices across runs. If they do, add a compatibility note before merging.

## Texture And Image Resource Support

Extend `TextureManager` or add `EnvironmentTextureManager` if keeping cubemap code separate is cleaner.

Required image support:
- `ImageCreateFlags.CubeCompatibleBit`.
- six array layers.
- cubemap image views.
- per-mip image views where prefiltering dispatches write one mip at a time.
- `R16G16B16A16Sfloat` or `R32G32B32A32Sfloat` HDR formats.
- sampled, storage, transfer source, and transfer destination usage flags as needed.
- sampler support for:
  - cubemap linear filtering.
  - mipmapped specular sampling.
  - clamped BRDF LUT sampling.

Suggested resource names:
- `Environment Cubemap`
- `Environment Cubemap View`
- `Diffuse Irradiance Cubemap`
- `Diffuse Irradiance Cubemap View`
- `Prefiltered Environment Cubemap`
- `Prefiltered Environment Cubemap View`
- `Prefiltered Environment Mip {n} View`
- `BRDF Integration LUT`
- `BRDF Integration LUT View`
- `Environment Sampler`
- `BRDF LUT Sampler`

Tests:
- cubemap image creation rejects non-six-layer inputs.
- mip count calculation for prefiltered cubemap is stable.
- fixed environment textures are registered at the expected bindless indices.
- disposing environment resources releases image views and images without double-free.

## HDR Import

Add HDR image loading for `.hdr` first. Add `.exr` later only if needed.

Implementation options:
- Use an existing HDR-capable decoder if available in the project stack.
- If `StbImageSharp` supports float HDR decode in the project version, use that path and upload linear float pixels.
- If it does not, add a small dependency with clear licensing and isolate it in the asset layer.

Color-space rules:
- HDR environment maps are linear radiance data.
- Do not upload HDR environment as sRGB.
- Do not apply tone mapping or exposure during import.
- Sky intensity and exposure happen during rendering.

Importer behavior:
- Missing HDR file falls back to procedural sky and records diagnostics.
- Unsupported extension reports the source path and supported extensions.
- Images larger than `EnvironmentSize` are downsampled or converted to the configured cubemap size.

Tests:
- unsupported HDR source path fails gracefully.
- successful import reports width, height, format, and estimated bytes.
- color-space metadata or chosen Vulkan format is explicitly linear float.

## Equirectangular To Cubemap Conversion

Add a compute or fullscreen conversion path.

Recommended shader:
- `equirect_to_cubemap.comp`

Inputs:
- HDR equirectangular 2D texture.
- output cubemap storage image.
- target face index or dispatch z dimension.

Coordinate behavior:
- Use consistent world-space convention with `forward.frag`.
- Account for Vulkan coordinate orientation once and document it.
- Apply `RotationRadians` during sky and IBL sampling, not by baking each time the user changes rotation.

Validation:
- Render a known HDRI and verify:
  - horizon is level.
  - no face seams at cube edges.
  - +X, -X, +Y, -Y, +Z, -Z faces align with camera movement.

Fallback:
- If compute storage image support for the chosen HDR format is unavailable, convert by rendering each cubemap face through a graphics pass.

## Procedural Sky Fallback

Implement a simple procedural sky that can produce:
- skybox cubemap.
- diffuse irradiance approximation.
- prefiltered specular fallback.

First version can be simple:
- zenith color.
- horizon color.
- optional sun direction aligned with the first directional light from `SampleLighting`.
- optional sun disk for skybox only.

Suggested settings:
```csharp
public Vector3 ZenithColor { get; set; } = new(0.12f, 0.35f, 0.85f);
public Vector3 HorizonColor { get; set; } = new(0.85f, 0.92f, 1.0f);
public Vector3 GroundColor { get; set; } = new(0.03f, 0.035f, 0.04f);
public float SunDiskIntensity { get; set; } = 8.0f;
```

Keep these defaults physically plausible enough for material review, not stylized by default. The fallback must make metallic and rough materials readable without three artificial fill lights.

## Irradiance Cubemap

Diffuse IBL should sample a low-frequency irradiance cubemap.

Implementation path:
1. Generate from the environment cubemap at load time.
2. Use a compute shader or six-face render pass.
3. Store as `R16G16B16A16Sfloat`.
4. Size default: `64x64` per face.
5. Use enough samples for stable diffuse color; performance is acceptable because this is not per-frame.

Shader:
- `irradiance_convolution.comp`

Sampling:
```glsl
vec3 diffuseIbl = albedo * (1.0 - metallic) * irradiance * ambientOcclusion;
```

Notes:
- Use the diffuse term consistent with the existing BRDF implementation. If direct lighting already divides diffuse albedo by pi, keep IBL conventions consistent.
- Do not use the old `albedo * 0.08` ambient when irradiance is available.

## Prefiltered Specular Environment

Specular IBL should sample a mipmapped prefiltered cubemap by roughness.

Implementation path:
1. Generate a cubemap with full mip chain.
2. Each mip represents increasing roughness.
3. Use GGX importance sampling.
4. Default size: `256x256` per face.
5. Store as `R16G16B16A16Sfloat`.

Shader:
- `prefilter_environment.comp`

Mip behavior:
```glsl
float lod = roughness * float(prefilteredMipCount - 1u);
vec3 prefiltered = textureLod(PrefilteredEnvironment, reflectionDirection, lod).rgb;
```

Sampling rules:
- Reflection vector is `reflect(-viewDirection, normal)`.
- Apply environment rotation to sample direction.
- Use roughness from the material after clamping.
- Metallic surfaces should rely heavily on specular IBL.
- Non-metallic surfaces should combine diffuse IBL with Fresnel-weighted specular IBL.

## BRDF Integration LUT

Generate or ship a 2D BRDF integration LUT.

Implementation choices:
- Generate at renderer startup with `brdf_integration.comp`.
- Or include a baked LUT asset if startup compute is undesirable.

Recommended first implementation:
- Generate once during renderer initialization.
- Size: `256x256`.
- Format: `R16G16Sfloat`.
- Bind at fixed `BrdfLutTexture`.

Shader use:
```glsl
vec2 brdf = texture(BrdfLut, vec2(NoV, roughness)).rg;
vec3 specularIbl = prefilteredColor * (F * brdf.x + brdf.y);
```

Tests:
- LUT resource exists after renderer initialization.
- LUT dimensions and format match settings.
- LUT debug view displays non-zero gradient content.

## Forward Shader Integration

Update `forward.frag`.

Required additions:
- Environment texture constants.
- Environment data access.
- cubemap sampling helpers.
- environment rotation helper.
- Fresnel/BRDF helper reuse where possible.
- diffuse IBL function.
- specular IBL function.
- debug branches for IBL-only outputs.

Replace:
```glsl
vec3 ambient = albedo * 0.08 * ambientOcclusion;
```

With:
```glsl
vec3 indirectLighting = EvaluateIbl(
    albedo,
    metallic,
    roughness,
    normal,
    viewDirection,
    ambientOcclusion);
```

Final color:
```glsl
vec3 color = indirectLighting + directLighting + emissive;
```

Fallback behavior:
- If environment is disabled, `indirectLighting = vec3(0.0)`.
- If environment resources are missing but fallback is enabled, sample procedural/neutral IBL textures.
- The old `albedo * 0.08` path can remain behind an explicit debug/fallback flag only. It should not be the normal path.

AO behavior:
- Apply material AO to diffuse IBL.
- Apply a conservative specular occlusion approximation if desired:
```glsl
float specularOcclusion = clamp(pow(ambientOcclusion, 1.0 + roughness), 0.0, 1.0);
```
- Do not multiply AO into direct lighting.

Debug modes:
- `DiffuseIblOnly`.
- `SpecularIblOnly`.
- `AmbientOcclusion`.
- `IrradianceCubemap`.
- `PrefilteredEnvironmentMip`.
- `BrdfLut`.

Shader tests:
- `ShaderBuildTests` compiles all new shaders.
- `forward.frag` compiles with the expanded bindless constants.
- material with metallic `1.0` and roughness `0.05` visibly reflects the sky.
- material with roughness `1.0` uses blurred specular and stable diffuse irradiance.

## Skybox Pass

Add a graphics pass:
- `Njulf.Rendering/Pipeline/SkyboxPass.cs`
- `Njulf.Rendering/Pipeline/PipelineObjects/SkyboxPipeline.cs`
- `Njulf.Shaders/skybox.vert`
- `Njulf.Shaders/skybox.frag`

Skybox implementation:
- Draw a fullscreen triangle or cube.
- Reconstruct view direction from inverse view-projection, or use a cube with camera translation removed.
- Sample environment cubemap.
- Apply `SkyIntensity`.
- Write HDR scene color.
- Do not write depth.
- Depth test should only pass where no opaque geometry was written, or draw before opaque only if the clear/depth behavior is proven correct.

Recommended pass position:
- After `ForwardPlusPass`.
- Before `TransparentForwardPass`.

RenderDoc names:
- `SkyboxPass`
- `Skybox Pipeline`
- `Skybox Pipeline Layout`
- `Skybox Pipeline Cache`

Tests:
- render graph pass order includes `SkyboxPass` in the expected location.
- skybox pass early-outs when environment is disabled.
- resizing swapchain does not require rebuilding static environment textures.

## Composite Debug Views

Some debug views are easier in `ToneMapCompositePass` than in `forward.frag`.

Recommended split:
- `forward.frag` debug views for material-related IBL contribution:
  - diffuse IBL only.
  - specular IBL only.
  - AO.
- `ToneMapCompositePass` debug views for fullscreen texture inspection:
  - BRDF LUT.
  - prefiltered environment mip.
  - irradiance cubemap face.
  - skybox-only output if using scene color is insufficient.

Cubemap debug preview:
- Add settings for face index and mip level if needed.
- For first implementation, preview face `+Z` and mip from `DebugMipLevel`.
- Expand to selectable face after the base path is validated.

## Diagnostics

Extend `RendererDiagnostics`.

Suggested fields:
```csharp
int EnvironmentEnabled,
EnvironmentSourceKind EnvironmentSourceKind,
string EnvironmentSourcePath,
int EnvironmentUsesFallback,
uint EnvironmentCubemapSize,
uint IrradianceCubemapSize,
uint PrefilteredEnvironmentSize,
uint PrefilteredEnvironmentMipCount,
uint BrdfLutSize,
float SkyIntensity,
float DiffuseIblIntensity,
float SpecularIblIntensity,
EnvironmentDebugView EnvironmentDebugView,
int EnvironmentDebugMipLevel,
ulong EnvironmentTextureBytes,
long CpuEnvironmentPrepareMicroseconds
```

Update:
- `RendererDiagnostics.Empty`.
- diagnostics construction in `VulkanRenderer`.
- `SampleDiagnosticsReporter.PrintFirstFrameDiagnostics`.
- `RendererDiagnosticsTests`.

Sample diagnostics output should make it obvious whether the scene is using:
- procedural fallback sky.
- loaded HDRI.
- disabled environment.
- active debug view.

## NjulfHelloGame Sample Updates

Keep `SampleLighting.cs` focused on direct lights, but add an environment setup path next to it.

Suggested files:
- `NjulfHelloGame/SampleEnvironment.cs`
- `NjulfHelloGame/SampleEnvironmentMode.cs`

Suggested modes:
```csharp
internal enum SampleEnvironmentMode
{
    ProceduralOutdoor,
    StudioNeutral,
    HdrAsset,
    Disabled
}
```

Demonstration behavior:
- `DirectionalKey` plus `ProceduralOutdoor` should look plausible with one sun-like key light.
- `ThreePointDemo` plus `StudioNeutral` should show IBL on metallic/rough materials without over-lighting.
- `SpotShadowDemo` and `PointShadowDemo` should keep their authored local-light focus but still receive subtle sky/environment fill.
- `Disabled` should make the loss of IBL obvious and prove direct lighting still works.

Avoid adding fake fill point lights just to compensate for missing IBL. Phase 6 should reduce the need for the three-point demo in normal material review.

## Asset Pipeline Hooks

First implementation can load one environment path directly through environment settings. Keep the API compatible with later content pipeline work.

Add manifest support if the sample app already uses manifests:
```text
EnvironmentPath=Assets/Environments/studio_small_09_2k.hdr
EnvironmentMode=HdrAsset
```

Do not block Phase 6 on a complete glTF extension story. Full HDR asset-pipeline polish belongs to Phase 14, but Phase 6 needs enough HDR loading to validate IBL.

## Render Graph And Resource Lifetime

Renderer initialization should:
1. Create fallback environment resources.
2. Create or load active environment resources.
3. Register fixed environment textures and environment data buffer.
4. Build BRDF LUT if not cached.
5. Convert equirectangular HDR to cubemap if needed.
6. Build irradiance cubemap.
7. Build prefiltered specular cubemap.
8. Initialize `SkyboxPass`.

Resize behavior:
- HDR scene color and bloom targets resize as today.
- Environment cubemaps do not resize with the swapchain.
- Skybox pass uses current swapchain extent and HDR scene color view.

Environment reload behavior:
- Defer deletion through `FenceBasedDeleter`.
- Keep old environment active until new resources are fully built, or fall back explicitly.
- Avoid synchronous GPU waits during normal frame rendering.

## Validation Checklist

RenderDoc checks:
- Pass order includes `SkyboxPass`.
- `ForwardPlusPass` samples irradiance, prefiltered environment, and BRDF LUT.
- `SkyboxPass` writes HDR scene color.
- `TransparentForwardPass` blends over sky.
- `BloomPass` sees bright sky/sun/emissive values where expected.
- `ToneMapCompositePass` remains the only swapchain writer.
- Environment images and views have useful debug names.
- Cubemap face orientation has no visible seams.

Vulkan validation checks:
- cubemap image creation uses compatible flags.
- storage image formats used by compute are supported.
- image layouts transition cleanly:
  - undefined to transfer or general for generation.
  - general/transfer to shader read for sampling.
  - per-mip transitions are correct during filtering.
- no descriptor is read before registration.
- environment reload and shutdown are validation-clean.

Visual checks:
- matte non-metallic surfaces pick up sky color softly.
- rough metal reflects a blurred environment.
- smooth metal reflects sharper sky detail.
- AO texture darkens only indirect contribution.
- turning environment off removes indirect fill without breaking direct lighting.
- exposure and tone mapping still control final brightness.

## Automated Tests

Unit tests:
- `EnvironmentSettings` clamping and defaults.
- bindless index constants match shader constants.
- cubemap mip count calculation.
- environment fallback selection.
- diagnostics default and populated values.
- `SampleEnvironmentMode` setup does not add direct lights.

Shader build tests:
- `skybox.vert`
- `skybox.frag`
- `equirect_to_cubemap.comp`
- `irradiance_convolution.comp`
- `prefilter_environment.comp`
- `brdf_integration.comp`
- updated `forward.frag`
- updated `tonemap_composite.frag` if debug previews are added there.

Optional GPU smoke tests:
- renderer initializes environment resources.
- BRDF LUT generation dispatch completes.
- procedural sky renders non-black pixels.
- loaded HDRI path falls back cleanly if the file is missing.

## Implementation Sequence

1. Add `EnvironmentSettings`, `EnvironmentDebugView`, and diagnostics fields.
2. Extend bindless indices and shader constants for environment textures and optional environment buffer.
3. Add cubemap image/view creation support and tests.
4. Add fallback procedural environment resources.
5. Add BRDF LUT generation and fixed bindless registration.
6. Add equirectangular HDR import and conversion to environment cubemap.
7. Add irradiance cubemap generation.
8. Add prefiltered specular cubemap generation.
9. Add `SkyboxPass` and skybox shaders.
10. Integrate IBL into `forward.frag` and remove the normal-path `albedo * 0.08` ambient.
11. Add debug views for IBL contributions and environment texture previews.
12. Update `VulkanRenderer.InitializeRenderGraph`, pass-order validation, and resource registration.
13. Add `SampleEnvironment` to `NjulfHelloGame` and wire it beside `SampleLighting`.
14. Update diagnostics reporter output.
15. Add or update tests.
16. Run shader compilation tests, CPU tests, and Vulkan validation on the sample scene.
17. Capture a RenderDoc frame and verify pass order, resource names, cubemap orientation, and debug views.

## Acceptance Criteria

Phase 6 is complete when:
1. `NjulfHelloGame` renders a sky into HDR scene color.
2. PBR materials receive diffuse IBL and roughness-aware specular IBL.
3. Metallic and rough surfaces look plausible with only a directional key light and an environment source.
4. The normal rendering path no longer uses `albedo * 0.08` as ambient lighting.
5. Missing HDRI content falls back to a procedural or neutral environment with diagnostics.
6. BRDF LUT, irradiance cubemap, and prefiltered environment cubemap are registered at fixed bindless indices.
7. Debug views can inspect irradiance, prefiltered environment mips, BRDF LUT, diffuse IBL, and specular IBL.
8. Renderer diagnostics report active environment settings and resource sizes.
9. Resize, environment reload, and shutdown are validation-clean.
10. RenderDoc shows `SkyboxPass` between opaque forward rendering and transparent forward rendering.

## Phase 6 Definition Of Done

The renderer should be able to show a material-review scene where:
- direct lights provide shape and shadows.
- sky and IBL provide stable indirect illumination.
- roughness visibly controls reflection sharpness.
- metallic materials reflect the environment instead of going black.
- non-metallic materials receive believable ambient color without fake fill lights.
- bloom and tone mapping continue to work from the HDR scene color.
- diagnostics and debug views make the environment path inspectable without changing code.
