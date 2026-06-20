# Realtime Global Illumination Implementation Plan

Date: 2026-06-20

## Goal

Implement performant, high quality realtime global illumination for Njulf using a production-oriented hybrid:

1. Screen-space global illumination for near-field visible bounce and contact color bleeding.
2. DDGI-style irradiance probe volumes for stable world-space indirect diffuse.
3. Optional Vulkan ray-query acceleration for probe updates, dynamic geometry support, and high-end validation.

The first production target is not a full path tracer. The target is stable, shippable indirect diffuse that works with the existing Vulkan forward+ renderer, depth prepass, Hi-Z pyramid, motion vectors, reflection probes, GPU scene submission, shadows, AO, and diagnostics.

## Current Renderer Context

Relevant existing systems:

- Vulkan renderer with dynamic rendering, bindless textures/storage buffers, mesh/task shaders, and buffer device address.
- Depth prepass with reverse-Z scene depth.
- Hi-Z pyramid built from scene depth.
- Forward+ opaque pass.
- Tiled light culling using scene depth.
- AO pass already sampling depth and optionally planned scene normals.
- Motion vector pass and TAA infrastructure.
- Static reflection probes and environment diffuse/specular IBL.
- Directional, spot, and point shadow maps.
- GPU scene compaction and foliage rendering.
- Mature diagnostics, GPU timings, performance snapshots, quality presets, and sample input toggles.

This means GI should reuse depth/Hi-Z and motion vectors immediately. Hardware ray tracing should be optional because the current required Vulkan extension list does not require acceleration structures or ray queries.

## High-Level Architecture

Final diffuse indirect lighting should be composed as:

```text
IndirectDiffuse =
    EnvironmentDiffuseFallback
  + DDGIWorldDiffuse
  + SSGINearFieldDiffuse
```

Application rules:

- SSGI handles camera-visible near-field bounce, contact bounce, and high-frequency local detail.
- DDGI handles off-screen, room-scale, and low-frequency world-space bounce.
- AO/contact terms suppress impossible indirect light and hide remaining DDGI leaks.
- Environment diffuse remains the fallback for sky/outdoor/background lighting.
- GI affects diffuse lighting first. Specular GI/reflections remain the reflection probe/IBL/SSR/RT reflection domain.

## Phase 1: Settings, Modes, and Sample Controls

Add a `GlobalIlluminationSettings` class to `Njulf.Rendering/Data/RenderSettings.cs`.

Suggested enums:

```csharp
public enum GlobalIlluminationMode : uint
{
    Disabled = 0,
    Ssgi = 1,
    Ddgi = 2,
    Hybrid = 3,
    RayQueryHybrid = 4
}

public enum GlobalIlluminationDebugView : uint
{
    None = 0,
    FinalIndirect = 1,
    SsgiRaw = 2,
    SsgiFiltered = 3,
    SsgiHistory = 4,
    SsgiRayHitMask = 5,
    SsgiHistoryRejection = 6,
    DdgiIrradiance = 7,
    DdgiVisibility = 8,
    DdgiProbeIndex = 9,
    DdgiProbeState = 10,
    DdgiProbeRelocation = 11,
    DdgiLeakClamp = 12,
    RayQueryCost = 13
}
```

Suggested settings:

- `Enabled`
- `Mode`
- `DebugView`
- `IndirectIntensity`
- `EnvironmentFallbackIntensity`
- `UseSsgi`
- `UseDdgi`
- `UseRayQueryBackend`
- `ResolutionScale`
- `MaxBounceDistance`
- `TemporalEnabled`
- `DenoiserEnabled`
- `HistoryResponsiveness`
- `NormalRejectionThreshold`
- `DepthRejectionThreshold`
- `LeakClampStrength`

Add quality preset defaults:

- Low: GI disabled, environment fallback only.
- Medium: half-res SSGI, low sample count, DDGI optional/off by default.
- High: half-res hybrid SSGI + DDGI.
- Ultra: higher SSGI samples, larger DDGI update budget, ray-query backend if supported.

Extend `NjulfHelloGame/SampleInputController.cs` using the existing AO/reflection input pattern.

Suggested actions:

- `toggle_global_illumination`
- `cycle_global_illumination_mode`
- `cycle_global_illumination_debug`
- `global_illumination_intensity_down`
- `global_illumination_intensity_up`
- `global_illumination_distance_down`
- `global_illumination_distance_up`

Use chorded bindings to avoid conflicts with existing AO and reflection keys:

- `Ctrl+Number5`: toggle GI.
- `Ctrl+Number6`: cycle GI debug.
- `Ctrl+Y`: cycle GI mode.
- `Ctrl+J/U`: max bounce distance down/up.
- `Ctrl+M/I`: GI intensity down/up.

Add `PrintGlobalIlluminationSettings(string prefix)` next to the existing AO and reflection print helpers.

Acceptance criteria:

- Settings serialize through `RenderSettings.Save/Load`.
- Quality presets configure GI predictably.
- Sample controls print current mode, debug view, resolution scale, intensity, distance, SSGI state, DDGI state, and ray-query availability.
- Tests cover clamps, defaults, preset changes, and serialization.

## Phase 2: Diagnostics and Budgeting

Add GI fields to `RendererDiagnostics` and performance snapshots before implementing expensive passes.

Suggested diagnostics:

- `GlobalIlluminationEnabled`
- `GlobalIlluminationMode`
- `GlobalIlluminationDebugView`
- `GlobalIlluminationRayQuerySupported`
- `GlobalIlluminationRayQueryActive`
- `SsgiWidth`
- `SsgiHeight`
- `SsgiResolutionScale`
- `SsgiRayCount`
- `SsgiHistoryValid`
- `SsgiRejectedHistoryPixelCount`
- `DdgiProbeVolumeCount`
- `DdgiProbeCount`
- `DdgiActiveProbeCount`
- `DdgiProbesUpdated`
- `DdgiRaysPerProbe`
- `DdgiProbeRelocationCount`
- `DdgiProbeClassificationCount`
- `CpuSsgiRecordMicroseconds`
- `CpuDdgiRecordMicroseconds`
- `GpuSsgiTraceMicroseconds`
- `GpuSsgiTemporalMicroseconds`
- `GpuSsgiDenoiseMicroseconds`
- `GpuDdgiUpdateMicroseconds`
- `GpuGiCompositeMicroseconds`
- `GlobalIlluminationRenderTargetBytes`
- `DdgiTextureBytes`
- `DdgiBufferBytes`
- `AccelerationStructureBytes`

Extend memory budgeting:

- Add a GI memory category if useful, or track GI render target/probe/AS bytes in existing renderer resource diagnostics.
- Add GI metrics to `RenderBudgetEvaluator` so High/Ultra budgets catch runaway probe counts or render target sizes.

Acceptance criteria:

- GI off reports zero pass costs and zero active probes.
- GI on reports pass timings and memory.
- Performance snapshots include GI fields.
- Budget status warns when GI memory or frame cost exceeds profile limits.

## Phase 3: Render Targets and Bindless Indices

Extend `RenderTargetManager` with GI resources.

Required targets:

- `SceneNormal`: full-resolution, sampled. Prefer `R16G16B16A16_SFLOAT` for initial correctness; optimize to packed format later.
- `SceneMaterial`: optional packed material data, such as roughness, metallic, flags, and albedo/material id.
- `SsgiRaw`: scaled, storage + sampled.
- `SsgiFiltered`: scaled, storage + sampled.
- `SsgiHistoryA`
- `SsgiHistoryB`
- `GiFinalDiffuse`: full or half-res depending on composition approach.
- Optional `GiVariance` or `SsgiMoments` for denoising.

Fixed bindless texture slots should be added near existing scene-space resources:

```text
DepthTexture
HiZDepthTexture
HdrSceneColorTexture
AmbientOcclusionRawTexture
AmbientOcclusionBlurredTexture
SceneNormalTexture
SceneMaterialTexture
SsgiRawTexture
SsgiFilteredTexture
SsgiHistoryTexture
GiFinalDiffuseTexture
...
FirstDynamicTextureIndex
```

Update:

- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
- `Njulf.Shaders/common.glsl`
- `Njulf.Tests/BindlessIndexTests.cs`
- texture registration in `VulkanRenderer.RegisterSceneRenderTextures`
- render target recreation and memory accounting

Acceptance criteria:

- Resize recreates GI targets safely.
- Disabled GI uses placeholder or default textures where needed.
- Bindless host/shader tests pass.
- RenderDoc shows correct image layouts and descriptor bindings.

## Phase 4: Scene Surface Pass

Implement a real scene surface data pass before serious GI.

Rationale:

- AO can reconstruct normals from depth, but GI needs stable normals and material hints.
- Reconstructed normals are noisy around thin edges, depth discontinuities, and low-resolution GI buffers.
- SSGI history rejection and DDGI leak clamps need normal/material data.

Add `SceneSurfacePass` or `SceneNormalPass` after `HiZBuildPass` and before AO/SSGI.

Initial outputs:

- world normal
- roughness
- metallic
- material flags
- optional albedo luminance or material index

Geometry:

- Opaque and alpha-masked objects.
- Foliage only after base path is stable.
- Transparent objects excluded initially.

Pass order target:

```text
SceneOpaqueCompactionPass
DirectionalShadowPass
SpotShadowPass
PointShadowPass
DepthPrePass
MotionVectorPass
HiZBuildPass
SceneSurfacePass
AmbientOcclusionPass
AmbientOcclusionBlurPass
SsgiTracePass
SsgiTemporalPass
SsgiDenoisePass
DdgiUpdatePass
TiledLightCullingPass
ForwardPlusPass
SkyboxPass
TransparentForwardPass
ParticlePass
FogPass
PostProcessing
```

Acceptance criteria:

- Normal debug view displays stable world normals.
- Surface pass uses the same object/material classification as forward/depth.
- Masked materials are represented correctly.
- No transparent/decal geometry contaminates opaque GI inputs.

## Phase 5: SSGI Trace

Add `ScreenSpaceGlobalIlluminationPass` as compute.

Inputs:

- scene depth
- Hi-Z pyramid
- scene normal
- motion vectors
- HDR scene color or a direct-lit opaque color source
- projection, inverse projection, view, inverse view, previous view-projection
- frame index and jitter

Initial algorithm:

1. Dispatch at half resolution by default.
2. Reconstruct world/view position from depth.
3. Fetch stable world normal.
4. Generate 4 to 8 low-discrepancy hemisphere rays per pixel.
5. Importance weight rays around the surface normal.
6. March rays in screen space using Hi-Z cone stepping.
7. Reject hits on:
   - invalid depth
   - excessive depth thickness
   - normal mismatch
   - backfaces
   - sky/background
   - too close self-hit
   - screen-edge instability
8. Sample direct-lit HDR scene color at hit point.
9. Convert hit radiance to diffuse indirect contribution.
10. Output raw SSGI and hit confidence.

Important constraints:

- Do not start full resolution.
- Do not trace against transparent objects initially.
- Clamp indirect radiance to avoid fireflies.
- Fade rays near screen edges and disocclusions.
- Treat sky misses as environment fallback, not as a screen hit.

Suggested quality:

- Medium: half-res, 4 rays/pixel, short max distance.
- High: half-res, 6 rays/pixel.
- Ultra: half/full adaptive, 8 rays/pixel, better denoise.

Acceptance criteria:

- Visible local color bounce on Sponza-style interiors.
- No obvious screen-edge explosions.
- Stable cost under GPU timing.
- Debug views show raw trace, hit mask, and rejected hits.

## Phase 6: SSGI Temporal Accumulation

Add `SsgiTemporalPass`.

Inputs:

- current raw SSGI
- previous SSGI history
- motion vectors
- current/previous depth
- current/previous normal if available
- camera jitter

History rejection:

- Reject if depth delta exceeds threshold.
- Reject if normal dot falls below threshold.
- Reject on high velocity/disocclusion.
- Reject on material id or material flag mismatch if available.
- Reject sky/background.
- Reject if current confidence is too low.

Stabilization:

- Neighborhood min/max luminance clamp.
- Variance-aware clamp if `GiVariance` is added.
- Adaptive alpha:
  - fast response on lighting/object change
  - slower accumulation on stable pixels
- History reset on resolution, FOV, projection, or GI mode changes.

Acceptance criteria:

- Camera movement does not smear across silhouettes.
- Dynamic light changes converge quickly enough.
- Debug view clearly shows rejected history.
- Resize and mode changes invalidate history safely.

## Phase 7: SSGI Spatial Denoise and Upsample

Add `SsgiDenoisePass`.

Recommended path:

- Bilateral blur using depth and normal.
- 2-3 à trous iterations for High/Ultra.
- Half-res to full-res upsample with edge stopping.
- Preserve contact detail by limiting blur radius near high normal/depth gradients.

Inputs:

- temporally accumulated SSGI
- scene depth
- scene normal
- confidence/variance

Acceptance criteria:

- Low noise without waxy overblur.
- No visible bleeding over object silhouettes.
- Half-res SSGI does not shimmer during camera motion.

## Phase 8: Apply SSGI in Lighting

Integrate GI into `forward.frag`.

Application rules:

- Apply to diffuse indirect only.
- Multiply by base color/albedo.
- Respect material AO.
- Respect emissive separately.
- Do not add GI to sky pixels.
- Keep reflection/specular separate.
- Clamp final indirect energy.

Suggested diffuse composition:

```text
diffuseIndirect =
    environmentDiffuse * environmentFallbackIntensity
  + ddgiDiffuse * ddgiIntensity
  + ssgiDiffuse * ssgiIntensity;

diffuseIndirect *= aoAndLeakClamp;
```

Acceptance criteria:

- Feature isolation can disable GI independently.
- GI off is visually identical to pre-GI renderer except for expected resource defaults.
- GI debug views can be displayed through composite/debug mode.

## Phase 9: DDGI Scene Data and Probe Volumes

Add a DDGI data model.

Suggested scene component:

```csharp
public sealed class GlobalIlluminationProbeVolume
{
    public Vector3 Origin;
    public Vector3 Size;
    public Int3 ProbeCounts;
    public Vector3 ProbeSpacing;
    public float NormalBias;
    public float ViewBias;
    public float MaxRayDistance;
    public float Intensity;
    public float Hysteresis;
    public int RaysPerProbe;
    public int MaxProbeUpdatesPerFrame;
}
```

Start with one default volume around loaded sample scenes. Later, expose authored volumes.

GPU resources:

- Probe metadata buffer.
- Probe state buffer.
- Probe update queue.
- Probe relocation/classification buffer.
- Irradiance atlas using octahedral mapping.
- Visibility/depth-moments atlas using octahedral mapping.
- Optional previous irradiance/visibility atlases for history.

Acceptance criteria:

- Renderer can allocate, resize, and destroy probe resources.
- Debug overlay can draw probe bounds and probe grid points.
- Probe memory is included in diagnostics.

## Phase 10: Ray Query Capability and Fallback

Add optional Vulkan ray-query support.

Optional extension detection:

- `VK_KHR_acceleration_structure`
- `VK_KHR_ray_query`
- `VK_KHR_spirv_1_4`
- `VK_KHR_shader_float_controls`
- already required: `VK_KHR_buffer_device_address`
- already required: `VK_KHR_deferred_host_operations`

Do not make RT extensions required for renderer startup. If unavailable:

- `RayQueryHybrid` mode falls back to `Hybrid`.
- DDGI update is disabled or uses a later raster fallback.
- Diagnostics report ray-query unsupported.

Acceptance criteria:

- Non-RT GPUs still run.
- RT-capable GPUs report support and can enable ray-query paths.
- Validation layers are clean for both paths.

## Phase 11: Acceleration Structures

Add `AccelerationStructureManager`.

Responsibilities:

- Static BLAS cache per mesh.
- Dynamic BLAS path for skinned or frequently changing meshes.
- TLAS build/refit per frame.
- Instance masks:
  - static opaque
  - dynamic opaque
  - masked alpha approximation
  - foliage approximation
  - emissive
  - non-GI excluded
- AS memory tracking.
- delayed destruction through the existing fence-based deletion model.

Initial scope:

- Static opaque geometry only.
- Dynamic rigid transforms via TLAS updates.
- Skinned mesh GI participation disabled or approximated initially.
- Alpha-tested foliage excluded initially, then added with conservative opacity approximation.

Acceptance criteria:

- TLAS contains expected static opaque instances.
- Moving rigid objects update TLAS transform correctly.
- AS memory appears in diagnostics.
- Renderer falls back cleanly if AS creation fails.

## Phase 12: DDGI Ray Update

Add `DdgiUpdatePass`.

For each frame:

1. Select a budgeted set of probes to update.
2. Generate probe rays using stable blue-noise or low-discrepancy patterns.
3. Trace rays with ray queries.
4. Shade hits cheaply:
   - albedo/base color
   - emissive
   - direct light approximation
   - optional shadow map visibility
   - environment for misses
5. Write irradiance octahedral atlas.
6. Write visibility/depth-moments atlas.
7. Blend with previous probe data using hysteresis.
8. Classify inactive or invalid probes.
9. Relocate probes out of walls.

Quality defaults:

- Medium: 64 rays/probe, low update budget.
- High: 128 rays/probe.
- Ultra: 256 rays/probe or larger update budget.

Acceptance criteria:

- Static room converges to stable indirect diffuse.
- Dynamic light changes update affected probes over time.
- No hard frame spikes when many probes need updates.
- Probe update budget is visible in diagnostics.

## Phase 13: DDGI Leak Reduction

Leak reduction is mandatory for production quality.

Implement:

- Visibility/depth moments per probe direction.
- Normal bias.
- View bias.
- Probe relocation out of walls.
- Probe classification for invalid/inside probes.
- Trilinear probe sampling with visibility weighting.
- Backface rejection during ray updates.
- Scene depth/normal clamp when applying probe lighting.
- Optional per-volume wall-thickness tuning.

Apply-time checks:

- Reject or reduce probes whose visibility moments disagree with shaded point distance.
- Reduce contribution when surface normal faces away from probe direction.
- Clamp DDGI against screen-space AO/contact shadow.
- Prefer SSGI near contact areas.

Acceptance criteria:

- Thin-wall test does not show obvious light bleeding at normal camera distances.
- Sponza interiors do not glow through adjacent rooms.
- Probe relocation debug shows probes moved out of invalid positions.

## Phase 14: DDGI Application

Add DDGI sampling helpers in `common.glsl` or a new include.

Sampling:

- Find containing probe cell.
- Fetch 8 neighboring probes.
- For each probe:
  - sample irradiance octahedral map by surface normal.
  - sample visibility by direction to shaded point.
  - apply distance/visibility/normal weights.
  - skip inactive probes.
- Normalize accumulated contribution.

Integrate into forward shader diffuse indirect path.

Acceptance criteria:

- DDGI alone gives stable room-scale indirect diffuse.
- Probe debug modes show selected probe indices and visibility.
- GI intensity is art-directable and bounded.

## Phase 15: Hybrid Composition

Combine SSGI and DDGI.

Rules:

- SSGI is high-frequency near-field contribution.
- DDGI is low-frequency world contribution.
- SSGI should override or add detail where confidence is high.
- DDGI fills misses, off-screen areas, and disocclusions.
- Environment fallback remains for outdoor/sky-lit areas.

Blend model:

```text
nearField = SSGI * SSGIConfidence;
worldField = DDGI * (1 - NearContactSuppression);
fallback = EnvironmentDiffuse * FallbackWeight;

finalGI = nearField + worldField + fallback;
```

Acceptance criteria:

- Hybrid looks better than either SSGI or DDGI alone.
- Camera movement remains stable.
- SSGI edge fade does not produce dark halos.
- DDGI leaks are hidden or strongly reduced by SSGI/AO/contact clamps.

## Phase 16: Dynamic World Support

Classify dynamic invalidation sources:

- Moved lights.
- Moved rigid meshes.
- Skinned meshes.
- Changed materials.
- Changed emissive objects.
- Destroyed/created objects.
- Probe volume movement/resizing.

Update strategy:

- Track dirty world-space bounds.
- Mark affected probes for high-priority refresh.
- Keep global rolling updates for stale probes.
- Cap max updated probes per frame.
- Apply faster hysteresis for dirty probes.
- Reset or damp SSGI history on major lighting changes.

Initial dynamic support:

- Dynamic lights affect probe refresh priority.
- Rigid moving opaque objects participate in TLAS.
- Skinned characters receive GI but do not contribute to DDGI bounce initially.
- Emissive dynamic injection is a later extension.

Acceptance criteria:

- Moving a light updates nearby indirect lighting without full-frame spikes.
- Moving rigid occluders reduce/provide bounce after bounded convergence time.
- Camera-only movement does not invalidate DDGI.

## Phase 17: Debug Views and Editor-Oriented Hooks

Extend debug overlays and sample controls.

Overlay/debug needs:

- Probe grid.
- Active/inactive probes.
- Relocated probes.
- Probes updated this frame.
- Probe volume bounds.
- SSGI ray hit mask.
- SSGI raw/filter/history.
- SSGI history rejection.
- DDGI irradiance atlas preview.
- DDGI visibility atlas preview.
- Final GI contribution.
- Leak clamp mask.

Sample controller additions should follow the existing patterns for AO, reflections, particles, foliage, and performance snapshots.

Acceptance criteria:

- Every major GI artifact has a debug view.
- Console prints include timings and memory.
- Debug modes do not require code changes to inspect common failure cases.

## Phase 18: Validation Scenes

Add or configure test scenes:

- Cornell-style room with colored walls.
- Sponza interior.
- Thin-wall leak test.
- Moving point light.
- Moving rigid object.
- Dynamic emissive object later.
- Outdoor/foliage stress scene.
- Small enclosed room with bright exterior.

Visual acceptance:

- Indirect bounce appears from colored walls.
- No major leaks through thin walls.
- No severe temporal shimmer.
- No overbright fireflies.
- No SSGI edge explosions.
- GI off/on comparison is predictable.

Performance acceptance:

- Medium: GI within a low single-digit millisecond budget at 1080p target hardware.
- High: stable at 1440p with dynamic resolution if needed.
- Ultra: higher quality but still budgeted.
- No unbounded memory growth.

## Phase 19: Tests

CPU/unit tests:

- `GlobalIlluminationSettings` clamp/default tests.
- Render settings save/load tests.
- Quality preset tests.
- Bindless index host/shader sync tests.
- Render target allocation and resize tests.
- Diagnostics default and populated snapshot tests.
- Ray-query capability fallback tests.
- DDGI probe volume layout tests.
- Probe update scheduling tests.

GPU/manual tests:

- Vulkan validation layers clean.
- RenderDoc captures for every GI pass.
- Resize/swapchain recreation.
- Toggling GI modes at runtime.
- Quality preset switching.
- RT unsupported fallback.
- RT supported active path.
- Long-run memory leak auditor.

## Phase 20: Performance Rules

Hard rules:

- SSGI starts half-res.
- DDGI updates are budgeted and amortized.
- Ray-query path is optional.
- No full-scene probe update in one frame during gameplay.
- No full-res multi-ray per-pixel GI as default.
- No transparent GI contribution until opaque path is stable.
- No skinned-mesh DDGI contribution until static/dynamic-rigid path is stable.

Optimization knobs:

- GI resolution scale.
- SSGI ray count.
- SSGI max distance.
- SSGI temporal alpha.
- Denoise iteration count.
- Probe spacing.
- Probe update budget.
- Rays per probe.
- DDGI hysteresis.
- Dynamic object participation flags.
- Foliage GI participation flags.

## Phase 21: Later Extensions

Do not block the first production path on these:

- Surfels as a near-field dynamic cache.
- ReSTIR GI.
- Full path tracing mode.
- Ray traced transparency.
- Ray traced caustics.
- Multi-bounce DDGI beyond temporal accumulation.
- Emissive mesh importance sampling.
- Hardware ray traced reflections replacement.
- Neural denoising or vendor-specific upscalers.

Surfels can be useful later for dynamic local bounce from moving objects, but SSGI + DDGI is the lower-risk first architecture for the current renderer.

## Recommended Milestone Order

1. Settings, sample controls, diagnostics.
2. Render targets and bindless slots.
3. Scene normal/surface pass.
4. Half-res SSGI raw trace.
5. SSGI temporal accumulation.
6. SSGI denoise and upsample.
7. Forward shader GI application.
8. DDGI data/resources/debug volume overlay.
9. Optional ray-query capability detection.
10. BLAS/TLAS manager for static opaque geometry.
11. DDGI ray update.
12. DDGI visibility, relocation, classification, and leak clamps.
13. Hybrid SSGI + DDGI composition.
14. Dynamic world invalidation/update scheduling.
15. Quality tuning, validation scenes, and performance budgets.

## Definition of Done

The feature is production-ready when:

- GI can be toggled and debugged from `SampleInputController`.
- GI resources resize and clean up correctly.
- Non-RT GPUs run with safe fallback behavior.
- RT-capable GPUs can use ray queries for DDGI updates.
- SSGI provides stable near-field bounce with low noise.
- DDGI provides stable world-space bounce with controlled leaking.
- Hybrid mode improves over SSGI-only and DDGI-only modes.
- Dynamic lights and rigid objects update indirect lighting within a bounded frame budget.
- Renderer diagnostics expose GI cost, memory, mode, support, and quality state.
- Validation layers and RenderDoc captures are clean.
- Tests cover settings, bindless indices, render target sizing, diagnostics, fallbacks, and probe scheduling.
