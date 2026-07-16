# Phase 10: Reflection Support Detailed Implementation Plan

Goal: improve metals, wet surfaces, windows, water, polished floors, and glossy props by adding stable local reflection support. Phase 10 should extend Phase 6 environment IBL from one global environment into authored reflection probes, with box projection, blending, diagnostics, and clear rules for when screen-space or planar reflections are worth the cost.

## Recommendation

Implement static reflection probes first:
- local cubemap probes.
- GGX-prefiltered specular mip chains.
- box-projected sampling for indoor rooms and bounded spaces.
- volume-based probe selection and blending.
- debug volumes and texture previews.

This is the standard production-first path because reflection probes are stable, predictable, inspectable, and cheap at runtime. They work for off-screen objects, do not require temporal reconstruction, and fit the renderer's existing PBR/IBL direction.

Do not start Phase 10 with SSR. Screen-space reflections are useful for dynamic contact reflections and wet surfaces, but they only reflect what is visible on screen, break at screen edges, need roughness-aware tracing/resolve, and often require temporal filtering. SSR should be added only after probe reflections are working and only if the target game needs it.

Do not start Phase 10 with planar reflections unless there is a specific water or mirror requirement. Planar reflections are excellent for flat water, mirrors, and polished floors, but they add extra scene renders and strict clipping/camera rules. They should be opt-in and budgeted per surface.

## Assumptions

Phase 10 assumes:
- Phase 2 HDR scene color and tone-map composite are complete.
- Phase 6 sky/environment lighting and global specular IBL are complete or actively planned.
- Phase 7 AO, Phase 8 anti-aliasing, and Phase 9 fog may be complete or planned.
- The renderer has bindless textures, storage buffers, and PBR shader integration.
- Cubemap image creation, BRDF LUT, and prefiltered environment cubemap generation from Phase 6 can be reused or generalized.
- `forward.frag` has a function similar to `EvaluateIbl(...)` or will have one after Phase 6.
- `SampleLighting.cs` direct-light presets are available for validating reflective surfaces under different direct-light conditions, but reflection probes are environment data, not `LightManager` lights.

## Current Baseline

Relevant existing behavior:
- The production master plan has Phase 6 global sky/IBL before Phase 10.
- Current code has point-shadow cubemap-array infrastructure, but not color reflection cubemap arrays.
- Existing fixed bindless textures include depth, Hi-Z, HDR scene color, bloom, and shadow textures.
- `RenderTargetManager` owns frame render targets, not persistent local reflection probe resources.
- There is no reflection probe authoring data, capture pass, probe buffer, probe debug overlay, or probe blending shader path.
- Metallic materials currently depend on direct lighting and, after Phase 6, global environment specular IBL.

## Target Frame Shape

Static reflection probes do not require per-frame capture in the normal frame. Their runtime use is in forward shading.

Normal frame pass order after Phase 10:
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

Probe capture/filter flow:
1. capture scene cubemap faces for selected probe.
2. generate mip chain.
3. prefilter specular GGX mips.
4. optionally generate diffuse irradiance if probes are allowed to affect diffuse ambient.
5. register probe cubemap array and metadata.
6. runtime forward shading samples selected/blended probes.

Probe capture can initially be offline/load-time tooling. It does not need to run every frame.

## Scope

Phase 10 includes:
- Reflection settings under `RenderSettings`.
- Scene-level reflection probe authoring data.
- Persistent reflection probe resource manager.
- Color cubemap or cubemap-array texture support for probes.
- Static probe loading and/or runtime capture.
- GGX prefiltering for probe specular mips.
- Probe metadata buffer for shader sampling.
- Probe selection by position and influence volume.
- Box-projected reflection sampling.
- Blending between multiple probes.
- Fallback to global environment IBL when no local probe applies.
- Debug draw/overlay for probe influence volumes.
- Debug views for probe cubemap faces, mip levels, selected probe index, blend weights, and box projection.
- Diagnostics for probe count, resolution, memory, selected probes, and capture/filter cost.

Phase 10 does not include:
- Unlimited dynamic reflection probes.
- Full editor UI for probe placement.
- Real-time recursive probe reflections.
- SSR as the first implementation.
- Planar reflections as the first implementation.
- Ray traced reflections.
- Probe-based global illumination.
- Parallax-corrected diffuse irradiance as a required first slice.

## Core Design Decisions

1. Treat reflection probes as local specular IBL. They complement the global environment; they do not replace direct lighting or shadows.
2. Store probe metadata separately from `LightManager`. Reflection probes are environment/rendering data, not light sources.
3. Use prefiltered cubemaps with mip levels by roughness. Runtime shader work should be just selection, box projection, blending, and sampling.
4. Start with static probes. Dynamic capture can be explicit and budgeted later.
5. Use box projection for probes with box influence volumes. This is critical for indoor rooms where infinite cubemap reflections look wrong.
6. Blend at most two to four probes per pixel. More probes increase shader cost quickly.
7. Fall back to Phase 6 global prefiltered environment when no local probe has influence.
8. Make probe behavior visible. Artists and programmers need to see probe volumes, selected probes, and blend weights.

## Settings

Add `ReflectionSettings` under `RenderSettings`.

Suggested API:
```csharp
public enum ReflectionMode : uint
{
    Disabled = 0,
    GlobalEnvironmentOnly = 1,
    StaticProbes = 2,
    StaticProbesAndSsr = 3,
    StaticProbesAndPlanar = 4
}

public enum ReflectionDebugView : uint
{
    None = 0,
    ProbeInfluence = 1,
    ProbeIndex = 2,
    ProbeBlendWeights = 3,
    ProbeCubemapFace = 4,
    ProbePrefilterMip = 5,
    BoxProjectionDirection = 6,
    SsrMask = 7,
    PlanarReflection = 8
}

public sealed class ReflectionSettings
{
    public bool Enabled { get; set; } = true;
    public ReflectionMode Mode { get; set; } = ReflectionMode.StaticProbes;
    public int MaxProbes { get; set; } = 64;
    public int MaxProbesPerPixel { get; set; } = 2;
    public uint ProbeResolution { get; set; } = 256;
    public float Intensity { get; set; } = 1.0f;
    public float GlobalFallbackIntensity { get; set; } = 1.0f;
    public bool BoxProjectionEnabled { get; set; } = true;
    public bool ProbeBlendingEnabled { get; set; } = true;
    public bool CaptureOnLoad { get; set; } = false;
    public int MaxProbeCapturesPerFrame { get; set; } = 0;
    public ReflectionDebugView DebugView { get; set; } = ReflectionDebugView.None;
    public int DebugProbeIndex { get; set; }
    public int DebugCubemapFace { get; set; }
    public int DebugMipLevel { get; set; }
}
```

Recommended constraints:
- `MaxProbes`: `0` to `256`.
- `MaxProbesPerPixel`: `1` to `4`.
- `ProbeResolution`: power of two, `64` to `1024`.
- `Intensity`: `0.0` to `4.0`.
- `GlobalFallbackIntensity`: `0.0` to `4.0`.
- `MaxProbeCapturesPerFrame`: `0` to `4`.
- `DebugProbeIndex`: clamp to active probe count.
- `DebugCubemapFace`: `0` to `5`.
- `DebugMipLevel`: clamp to probe mip count.

Tests:
- defaults enable static probes with conservative runtime cost.
- invalid values clamp to supported ranges.
- disabled reflections preserve direct lighting and diffuse IBL.
- `MaxProbesPerPixel` never exceeds shader-supported constant.
- debug indices clamp to available resources.

## Scene Probe Model

Add scene-level reflection probe authoring data.

Suggested files:
- `Njulf.Core/Scene/ReflectionProbe.cs`
- `Njulf.Rendering/Data/ReflectionProbeData.cs`
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
- `Njulf.Rendering/Resources/ReflectionProbeResources.cs`

Suggested CPU data:
```csharp
public enum ReflectionProbeShape
{
    Box,
    Sphere
}

public sealed class ReflectionProbe
{
    public string Name { get; set; } = string.Empty;
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public ReflectionProbeShape Shape { get; set; } = ReflectionProbeShape.Box;
    public Vector3 BoxExtents { get; set; } = new(5.0f, 5.0f, 5.0f);
    public float Radius { get; set; } = 5.0f;
    public float BlendDistance { get; set; } = 1.0f;
    public float Intensity { get; set; } = 1.0f;
    public int Priority { get; set; }
    public string? CubemapPath { get; set; }
    public bool BoxProjection { get; set; } = true;
}
```

Suggested GPU data:
```csharp
public struct GPUReflectionProbe
{
    public Matrix4x4 WorldToProbe;
    public Vector4 PositionAndRadius;
    public Vector4 BoxMin;
    public Vector4 BoxMax;
    public Vector4 BlendParams;
    public int CubemapArrayIndex;
    public int Shape;
    public int Flags;
    public int Priority;
}
```

Keep authoring data deterministic. Probe order should not depend on dictionary enumeration or asset load race conditions.

## Resource Model

Recommended runtime resources:
- one prefiltered reflection probe cubemap array.
- one reflection probe metadata storage buffer.
- optional staging/capture cubemap for runtime captures.
- optional irradiance probe cubemap array later if local diffuse ambient is needed.

Cubemap array:
- format: `R16G16B16A16Sfloat`.
- usage:
  - sampled.
  - storage or color attachment for capture/filtering.
  - transfer source/destination for upload/copy.
- layers: `MaxProbes * 6`.
- mips: full mip chain or `floor(log2(ProbeResolution)) + 1`.

Debug names:
- `Reflection Probe Cubemap Array`
- `Reflection Probe Cubemap Array View`
- `Reflection Probe Metadata Buffer`
- `Reflection Probe Capture Cubemap`
- `Reflection Probe Prefilter Pipeline`
- `Reflection Probe Sampler`

Memory note:
- `64` probes at `256x256`, `RGBA16F`, full mip chain, 6 faces is significant but manageable for a desktop renderer.
- Add diagnostics before raising defaults.

## Bindless Index Additions

Extend `BindlessIndexTable.cs` and `common.glsl`.

Recommended buffer slot:
```csharp
public const int ReflectionProbeBuffer = EnvironmentDataBuffer + 1;
public const int StaticBufferCount = ReflectionProbeBuffer + 1;
```

Recommended texture slots after Phase 6-9 fixed renderer textures:
```csharp
public const int ReflectionProbeCubemapArrayTexture = FoggedSceneColorTexture + 1;
public const int ReflectionProbeDebugTexture = ReflectionProbeCubemapArrayTexture + 1;
public const int FirstDynamicTextureIndex = ReflectionProbeDebugTexture + 1;
```

If earlier phase fixed texture additions are not merged yet, place reflection textures after the current final renderer-owned texture and reconcile the fixed table during merge. Keep fixed renderer textures contiguous and covered by tests.

Update:
- `BindlessIndexTests.ShaderConstants_MatchHostBindlessIndices`.
- static buffer uniqueness and contiguity tests.
- first dynamic texture index assertions.
- shader constants in `common.glsl`.

## Probe Capture

Phase 10 can support two paths:
1. load authored/prebaked probe cubemaps.
2. capture static probes from the current scene at load time or by explicit command.

Recommended first implementation:
- support loading prefiltered probe cubemaps if available.
- add runtime capture tooling as a second slice using existing mesh/task rendering.

Runtime capture flow:
1. For each selected probe, render six cubemap faces from probe position.
2. Use 90 degree FOV and correct face orientations.
3. Capture HDR color without including the target probe's own reflection result.
4. Include skybox/global environment.
5. Include static opaque and masked geometry first.
6. Include transparent objects only if needed and budgeted.
7. Apply fog only if desired for the captured environment; default should match main scene atmosphere.
8. Generate mip chain.
9. Run GGX prefiltering for specular.
10. Store result into cubemap array layer for the probe.

Avoid recursive reflection:
- during probe capture, disable local reflection probe sampling or use only global environment fallback.
- document this clearly so captured probes are stable.

Capture pass names:
- `ReflectionProbeCapturePass Face +X`
- `ReflectionProbeCapturePass Face -X`
- `ReflectionProbePrefilterPass Mip {n}`

## Prefiltering

Reuse or generalize Phase 6 prefiltered environment generation.

Requirements:
- GGX importance sampling.
- roughness mapped to mip level.
- BRDF LUT remains shared from Phase 6.
- output cubemap array mips.

Recommended shader:
- `reflection_probe_prefilter.comp`

Inputs:
- captured probe cubemap or loaded source cubemap.
- probe index.
- mip level.
- roughness.

Output:
- cubemap array layer/mip for that probe.

Tests:
- mip count calculation.
- prefilter dispatch covers all faces and mips.
- roughness `0.0` samples sharp mip.
- roughness `1.0` samples final diffuse-looking mip.

## Probe Selection And Blending

Selection should be deterministic and cheap.

First implementation:
- CPU uploads all active probes.
- Shader evaluates all probes up to `MaxProbes` only if the number is small.
- Better: CPU or compute builds a compact visible/probe list later.

Recommended first runtime shader path:
- cap active probes to `MaxProbes`.
- in `forward.frag`, evaluate influence for each probe.
- keep the best `MaxProbesPerPixel` probes by weight/priority.
- normalize weights.
- fall back to global environment with remaining weight.

Influence for box probe:
- transform world position into probe-local space.
- compute distance to box boundary and blend region.
- inside volume: weight based on blend distance and priority.
- outside volume: weight zero.

Influence for sphere probe:
- weight = `1 - smoothstep(radius - blendDistance, radius, distance)`.

Priority:
- higher-priority probes should win when overlapping.
- priority should not cause discontinuous popping; fold it into sorting, not raw weight only.

Performance warning:
- Looping all probes per fragment is acceptable only for small first tests.
- Add a path to cull/provide nearby probes per object or tile if active probe count grows.

## Box Projection

Box projection corrects reflections inside bounded rooms by intersecting the reflection ray with the probe's box.

Shader behavior:
1. Transform world position and reflection vector into probe-local space.
2. Ray-box intersect from local position along local reflection direction.
3. Compute hit point on probe box.
4. Direction from probe center to hit point becomes cubemap sample direction.
5. Transform back to cubemap/probe orientation if needed.

Pseudo:
```glsl
vec3 localPos = TransformPoint(WorldToProbe, worldPosition);
vec3 localDir = TransformDirection(WorldToProbe, reflectionDirection);
vec3 rbmax = (boxMax - localPos) / localDir;
vec3 rbmin = (boxMin - localPos) / localDir;
vec3 rb = mix(rbmax, rbmin, lessThan(localDir, vec3(0.0)));
float fa = min(min(rb.x, rb.y), rb.z);
vec3 hit = localPos + localDir * fa;
vec3 sampleDir = normalize(hit);
```

Edge cases:
- if outside box, fall back to non-box-projected direction or zero weight.
- guard division by near-zero direction components.
- ensure coordinate conventions match cubemap face orientation.

Debug:
- visualize box-projected sample direction.
- compare box projection on/off in a rectangular room.

## Forward Shader Integration

Update `forward.frag` or the Phase 6 IBL helper.

Reflection contribution should use:
- material normal.
- view direction.
- roughness.
- metallic/specular Fresnel.
- BRDF LUT from Phase 6.
- material AO and screen-space AO specular occlusion if Phase 7 exists.
- probe prefiltered cubemap sample.

High-level function:
```glsl
vec3 EvaluateSpecularReflectionProbes(
    vec3 worldPosition,
    vec3 normal,
    vec3 viewDirection,
    float roughness,
    vec3 fresnel,
    float specularOcclusion);
```

Composition:
- local probe specular should replace or blend with global specular IBL based on probe weights.
- global environment remains fallback.
- direct lighting remains unchanged.
- diffuse IBL can remain global in first implementation.

Suggested blend:
```glsl
vec3 localSpecular = accumulatedProbeSpecular;
vec3 globalSpecular = EvaluateGlobalSpecularIbl(...);
vec3 specularIbl = localSpecular + globalSpecular * max(1.0 - accumulatedProbeWeight, 0.0);
```

Material handling:
- smooth metals should show sharp local reflections.
- rough metals should sample high mip levels and blend softly.
- non-metals receive weaker Fresnel-based specular reflections.
- emissive should not be affected by reflection probes.

Debug modes:
- selected probe index.
- probe blend weights.
- local specular only.
- global fallback only.
- box projection direction.
- prefiltered mip preview.

## Transparent And Water Materials

First implementation:
- opaque and masked materials use reflection probes.
- transparent materials can sample global environment only or reuse probe sampling if `TransparentForwardPass` shares the same shader path.

Glass/water caveat:
- correct glass and water usually need material extensions from Phase 16 and/or planar reflections.
- do not overbuild this phase for glass unless the game specifically needs it.

Later:
- planar reflections for water/mirrors.
- SSR for wet floors and glossy contact reflections.
- transparent sorting and refraction integration.

## SSR Extension Criteria

Add SSR only if probe reflections are insufficient for the target scenes.

SSR is appropriate for:
- wet floors.
- puddles.
- glossy contact reflections.
- dynamic characters reflected on nearby surfaces.
- small local reflection detail missing from probes.

SSR limitations:
- cannot reflect off-screen objects.
- unstable at screen edges.
- needs depth/normal inputs.
- rough reflections need denoise/blur.
- temporal filtering is usually required for stable quality.

SSR planned resources:
- scene depth.
- scene normal.
- HDR scene color or resolved color.
- roughness/material mask.
- SSR ray result target.
- SSR resolved/blurred target.
- optional history target.

Recommended SSR pass order if added:
1. after opaque forward shading and before transparent, or after transparent depending on desired reflected content.
2. trace against depth/Hi-Z.
3. resolve/denoise.
4. combine with reflection probes in forward shader or a separate reflection composite.

Do not make SSR required for Phase 10 acceptance.

## Planar Reflection Extension Criteria

Add planar reflections only for explicit planar reflective surfaces:
- mirrors.
- calm water.
- polished flat floors.

Planar reflection cost:
- another render of the scene from mirrored camera per active plane.
- clipping plane support.
- texture allocation per plane.
- recursive reflection control.
- per-plane update budget.

Recommended model:
- `PlanarReflectionSurface` component.
- max one or two active planes by default.
- update frequency controls.
- resolution scale controls.
- render static geometry first; dynamic objects only if required.

Do not make planar reflections required for Phase 10 acceptance unless water/mirror content is already in scope.

## Probe Authoring And Loading

Initial authoring options:
- create probes in sample code.
- load probe data from a simple JSON or scene manifest.
- later integrate into editor/asset pipeline.

Suggested sample file:
```text
ReflectionProbe:
  Name=RoomCenter
  Position=0,2,0
  Shape=Box
  Extents=6,3,8
  BlendDistance=1
  Priority=0
  CubemapPath=Assets/ReflectionProbes/RoomCenter.ktx
```

Texture format:
- prefer KTX/KTX2 or another cubemap-friendly format later.
- first implementation can use generated runtime capture or six HDR face images if simpler.
- do not block probe runtime on final asset-pipeline polish.

## Diagnostics

Extend `SceneRenderingData`:
```csharp
public bool ReflectionsEnabled { get; set; }
public ReflectionMode ReflectionMode { get; set; }
public ReflectionDebugView ReflectionDebugView { get; set; }
public int ReflectionProbeCount { get; set; }
public int ReflectionProbeCapacity { get; set; }
public int MaxReflectionProbesPerPixel { get; set; }
public uint ReflectionProbeResolution { get; set; }
public uint ReflectionProbeMipCount { get; set; }
public ulong ReflectionProbeEstimatedBytes { get; set; }
public int ReflectionProbeCapturesQueued { get; set; }
public int ReflectionProbeCapturesCompleted { get; set; }
public long CpuReflectionProbeUploadMicroseconds { get; set; }
public long CpuReflectionProbeCaptureRecordMicroseconds { get; set; }
public long CpuReflectionProbePrefilterRecordMicroseconds { get; set; }
public long GpuReflectionProbeCaptureMicroseconds { get; set; }
public long GpuReflectionProbePrefilterMicroseconds { get; set; }
```

Extend `RendererDiagnostics` with matching fields.

Update:
- `RendererDiagnostics.Empty`.
- diagnostics construction in `VulkanRenderer`.
- `SampleDiagnosticsReporter.PrintFirstFrameDiagnostics`.
- `RendererDiagnosticsTests`.

Sample diagnostics line:
```text
Frame diagnostics reflections: enabled=1, mode=StaticProbes, probes=4/64, resolution=256, mips=9, maxPerPixel=2, estimatedMiB=128.0, debug=None, capturesQueued=0, capturesCompleted=4, prefilterRecordUs=...
```

## Sample App Validation

`SampleLighting.cs` does not need new light types for reflection probes. Use existing lighting modes:
- `DirectionalKey`: validates global sky plus local probe reflections under a clean key light.
- `ThreePointDemo`: validates colored direct highlights do not replace environment reflections.
- `SpotShadowDemo`: validates local shadow demos still reflect room/environment plausibly.
- `PointShadowDemo`: validates bright point lights and metallic surfaces without confusing point-light cubemap shadows with reflection cubemaps.

Suggested sample additions:
- `SampleReflectionProbes.cs`.
- a probe at the center of the sample room.
- a second overlapping probe near a corridor/side space.
- metallic and roughness material test spheres.
- polished floor plane.
- debug input to cycle reflection debug views.
- debug input to toggle box projection.
- debug input to toggle local probes versus global fallback.

Avoid increasing direct light intensity to fake reflections. If metals are black or flat, fix probe placement, capture, filtering, or shader blending.

## Debug Tooling

Required debug views:
- draw probe volumes as boxes/spheres.
- selected probe index.
- probe blend weights.
- cubemap face preview.
- prefiltered mip preview.
- box projection direction.
- local reflection contribution only.
- global fallback contribution only.

Debug draw:
- wireframe influence volume.
- inner blend volume if blend distance creates one.
- probe position marker.
- priority label if text/debug UI exists.

RenderDoc:
- reflection probe cubemap array named.
- each capture face labeled.
- prefilter mips labeled.
- metadata buffer named.

## Render Graph And Resource Lifetime

Renderer initialization should:
1. Create `ReflectionProbeManager`.
2. Allocate reflection probe metadata buffer.
3. Allocate cubemap array for configured probe capacity.
4. Register probe buffer and cubemap texture in bindless heap.
5. Load or capture initial probes.
6. Prefilter probe cubemaps.
7. Upload probe metadata.
8. Enable forward shader probe sampling.

Normal frame:
- no capture work unless captures are queued.
- forward shader samples probe data.

Capture frame:
- respect `MaxProbeCapturesPerFrame`.
- avoid synchronous GPU waits.
- use `FenceBasedDeleter` for old probe resources.
- keep old probe data active until new capture/filter completes.

Resize behavior:
- probe resources do not resize with the swapchain.
- debug preview targets may resize if implemented as fullscreen previews.

Shutdown:
- reflection buffers, cubemap arrays, image views, samplers, capture targets, pipelines, and descriptor registrations are released once.

## Barriers And Layouts

Expected layouts:
- reflection probe cubemap array:
  - transfer destination or storage/color attachment during upload/capture/filter.
  - shader read during forward rendering.
- capture cubemap:
  - color attachment during capture.
  - shader read/storage source during prefilter.
- metadata buffer:
  - transfer destination during upload.
  - shader read during forward rendering.

Validation targets:
- no sampling while probe layer is being written.
- no descriptor points at destroyed probe view.
- capture/filter transitions are explicit per mip/layer.
- queued capture completion is fenced.
- disabled reflections do not sample uninitialized probe resources.

## Performance And Budgets

Runtime probe sampling cost depends on:
- active probe count.
- probes tested per fragment.
- probes blended per fragment.
- cubemap mip sampling.
- box projection math.

Default budgets:
- `MaxProbes`: `64`.
- `MaxProbesPerPixel`: `2`.
- `ProbeResolution`: `256`.
- runtime captures per frame: `0`.

Optimization path if needed:
- per-object probe assignment on CPU.
- per-tile probe lists similar to Forward+ light lists.
- clustered probe assignment.
- lower probe resolution.
- fewer blended probes.
- skip local probes for roughness above threshold if global fallback is adequate.

Do not optimize probe selection before diagnostics show shader cost is a problem.

## Automated Tests

Unit tests:
- `ReflectionSettings` defaults and clamping.
- reflection probe influence weight for box and sphere probes.
- box-projection ray-box intersection edge cases.
- probe priority/blend sorting.
- probe mip count calculation.
- reflection cubemap array layer count.
- bindless reflection constants match shader constants.
- `RendererDiagnostics.Empty` contains disabled reflection defaults.
- populated diagnostics copy reflection fields from `SceneRenderingData`.

Shader build tests:
- updated `forward.frag`.
- `reflection_probe_prefilter.comp`.
- optional `reflection_probe_capture` shaders if capture uses dedicated pipelines.
- debug preview shader if probe preview is in composite/debug pass.

Optional GPU smoke tests:
- reflection probe cubemap array allocates and registers.
- prefilter dispatch runs for all mips.
- sample metallic sphere changes when local probe is enabled.
- disabled local probes fall back to global environment.
- box projection on/off produces visible difference in a room test.

## Implementation Sequence

1. Add `ReflectionSettings`, `ReflectionMode`, and `ReflectionDebugView`.
2. Add reflection fields to `SceneRenderingData` and `RendererDiagnostics`.
3. Add `ReflectionProbe` scene data model.
4. Add `GPUReflectionProbe` and layout tests.
5. Extend bindless indices and shader constants for probe buffer and cubemap array.
6. Add `ReflectionProbeManager` and persistent probe resources.
7. Allocate and register reflection probe cubemap array.
8. Add probe metadata upload path.
9. Add static probe loading or simple generated test cubemap path.
10. Generalize Phase 6 prefiltering or add `reflection_probe_prefilter.comp`.
11. Add box/sphere influence and probe selection shader helpers.
12. Add box projection shader helper.
13. Integrate local probe specular into the Phase 6 IBL shader path.
14. Add debug views and probe volume debug draw hooks.
15. Add sample reflection probe setup in `NjulfHelloGame`.
16. Add tests for settings, influence, box projection, bindless constants, diagnostics, and shader builds.
17. Run CPU tests and shader compilation.
18. Run `NjulfHelloGame` with Vulkan validation enabled.
19. Capture RenderDoc frames for global-only, local probe, blended probes, and box projection.
20. Decide whether SSR or planar reflections are actually needed by the target content.

## Acceptance Criteria

Phase 10 is complete when:
1. Static reflection probes can be authored or loaded.
2. Reflection probe cubemaps are prefiltered for roughness-aware specular sampling.
3. Forward shading blends local probe specular with global environment fallback.
4. Box-projected reflections work for box influence volumes.
5. At least two overlapping probes blend without hard popping.
6. Metallic materials reflect plausible local surroundings.
7. Roughness controls reflection sharpness.
8. Probe debug volumes can be displayed.
9. Probe cubemap faces, prefiltered mips, selected probe index, and blend weights can be debugged.
10. Renderer diagnostics report probe count, resolution, memory estimate, mode, and capture/filter costs.
11. Disabled reflections fall back cleanly to global environment or no specular IBL according to settings.
12. Startup, probe load/capture, resize, and shutdown are validation-clean.

## Phase 10 Definition Of Done

The renderer should be able to show a room or courtyard with metallic and glossy materials reflecting local surroundings through stable reflection probes. Artists should be able to place bounded probes, see their volumes, understand which probe affects a surface, and tune blending without changing shader code. SSR and planar reflections remain optional later tools, not prerequisites for a reliable reflection baseline.
