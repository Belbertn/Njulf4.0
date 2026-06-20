# Pure Path Tracing Backend Implementation Plan

Date: 2026-06-20

## Goal

Add a separate optional pure path tracing backend that can be selected by a game at runtime or startup without replacing the existing Forward+/meshlet renderer.

The backend should be suitable for shipping a commercial game path tracing mode, but it should not make hardware ray tracing mandatory for the engine. Non-RT GPUs must continue to run the existing renderer.

RTXPT should initially be treated as a reference architecture and optional integration target, not as code to copy directly into core renderer modules. Any vendored or derived RTXPT code must stay isolated behind a dedicated backend boundary until licensing, third-party dependency, and distribution terms are reviewed.

## Current Renderer Context

Relevant existing systems:

- `IRenderer` is a small engine-facing interface in `Njulf.Core/Interfaces/IRenderer.cs`.
- `VulkanRenderer` is currently registered as the default `IRenderer` in `Njulf.Rendering/ServiceCollectionExtensions.cs`.
- The renderer already has a Vulkan context, swapchain manager, synchronization manager, command buffer manager, buffer manager, staging ring, bindless heap, texture manager, mesh manager, material manager, light manager, scene data builder, render graph, diagnostics, GPU timings, quality presets, feature isolation, and performance snapshots.
- The current Vulkan device requirements include swapchain, dynamic rendering, synchronization2, mesh shader, buffer device address, and deferred host operations. They do not yet require acceleration structures, ray query, or ray tracing pipeline extensions.
- The revised renderer architecture plan should remain the main near-term track. Graph resource ownership, barrier planning, graph diagnostics, processed mesh assets, and production pipeline declaration all make this path tracing backend easier and safer.

## Design Principles

1. Keep the raster renderer and pure path tracer separate.
2. Share device, swapchain, upload, asset, scene, texture, material, and diagnostics infrastructure where practical.
3. Do not force RT requirements into normal renderer startup.
4. Add fallback behavior before adding expensive features.
5. Start with static opaque geometry and one direct environment/light path before adding full material, transparency, denoising, and RTXPT-style integrations.
6. Treat every path tracing feature as debuggable through diagnostics and performance snapshots.
7. Keep RTXPT-derived code, if used, in an optional module with clear attribution and dependency boundaries.

## Proposed Module Layout

Initial engine-native layout:

```text
Njulf.Rendering/
  Backends/
    RendererBackendKind.cs
    IRendererBackend.cs
    RendererBackendFactory.cs
    ForwardPlusRendererBackend.cs
    PathTracingRendererBackend.cs
  RayTracing/
    RayTracingCapability.cs
    RayTracingContext.cs
    AccelerationStructureManager.cs
    BlasCache.cs
    TlasBuilder.cs
    RayTracingShaderTable.cs
    PathTracingResources.cs
    PathTracingSceneBridge.cs
    PathTracingSettings.cs
    PathTracingDiagnostics.cs
    PathTracingAccumulation.cs
    PathTracingDenoiserBridge.cs
```

Optional RTXPT integration layout, only after the engine-native boundary exists:

```text
Njulf.Rendering.Rtxpt/
  NOTICE.md
  ThirdPartyLicenses/
  RtxptBackend.cs
  RtxptSceneAdapter.cs
  RtxptMaterialAdapter.cs
  RtxptTextureAdapter.cs
  RtxptSettingsAdapter.cs
```

Keep the optional RTXPT module out of core renderer code so the game can build without it.

## Phase 1: Backend Selection Boundary

Implementation steps:

1. Add `RendererBackendKind`:

```csharp
public enum RendererBackendKind
{
    ForwardPlus = 0,
    PurePathTracing = 1,
    Rtxpt = 2
}
```

2. Add backend selection to `RenderingOptions`.
3. Add environment variable support, such as `NJULF_RENDERER_BACKEND=ForwardPlus|PurePathTracing|Rtxpt`.
4. Add an `IRendererBackend` internal interface that mirrors frame lifecycle needs but allows backend-specific diagnostics.
5. Keep public `IRenderer` stable.
6. Convert existing `VulkanRenderer` registration into a factory that creates the selected backend.
7. Keep `ForwardPlus` as the default.
8. Add startup diagnostics reporting requested backend, active backend, fallback reason, and RT capability status.

Validation:

- Existing sample runs unchanged with `ForwardPlus`.
- Invalid backend names fall back to `ForwardPlus` with a clear startup message.
- Tests cover option parsing, default selection, and fallback reason reporting.

## Phase 2: Optional Ray Tracing Capability Detection

Implementation steps:

1. Add optional detection for:
   - `VK_KHR_acceleration_structure`
   - `VK_KHR_ray_tracing_pipeline`
   - `VK_KHR_ray_query`
   - `VK_KHR_pipeline_library`
   - `VK_KHR_spirv_1_4`
   - `VK_KHR_shader_float_controls`
   - `VK_EXT_descriptor_indexing` if not already effectively required through existing bindless paths
2. Detect required feature structs without adding them to the mandatory renderer requirement list.
3. Add `RayTracingCapability` with supported features, limits, shader group handle size, recursion depth, AS build support, compaction support, and descriptor indexing details.
4. Report capability in `RendererStartupLog`, `RendererDiagnostics`, and performance snapshots.
5. If `PurePathTracing` is requested without required support, fall back to `ForwardPlus`.

Validation:

- Non-RT GPUs still initialize.
- RT GPUs report support without changing active renderer behavior.
- Tests cover simulated missing extensions and missing features through `DeviceRequirementOverride`-style hooks.

## Phase 3: Shared Path Tracing Settings And Controls

Implementation steps:

1. Add `PathTracingSettings` to render settings.
2. Include:
   - `Enabled`
   - `BackendKind`
   - `SamplesPerPixel`
   - `MaxBounces`
   - `MaxDiffuseBounces`
   - `MaxSpecularBounces`
   - `RussianRouletteStartBounce`
   - `AccumulationEnabled`
   - `DenoiserEnabled`
   - `UseNextEventEstimation`
   - `UseMultipleImportanceSampling`
   - `UseEnvironmentLighting`
   - `UseEmissiveMeshes`
   - `UseAnalyticLights`
   - `ClampDirectRadiance`
   - `ClampIndirectRadiance`
   - `DebugView`
3. Add `PathTracingQualityPreset` defaults:
   - Low: disabled or 1 spp preview.
   - Medium: 1 spp realtime accumulation, low bounce count.
   - High: 1-2 spp realtime accumulation, denoiser on.
   - Ultra: higher bounces and optional higher spp.
   - Offline: uncapped accumulation target, not default for gameplay.
4. Extend `SampleInputController.cs` with controls for backend mode, accumulation reset, samples, bounces, denoiser, and debug views.
5. Add setting serialization if the existing render settings save/load path is used.

Validation:

- Settings clamp to safe ranges.
- Quality presets produce predictable path tracing settings.
- Sample controls print active backend, samples, bounces, accumulation count, denoiser state, and fallback reason.

## Phase 4: Path Tracing Render Graph Resources

Implementation steps:

1. Add render graph resource IDs for:
   - `PathTraceColor`
   - `PathTraceAccumulation`
   - `PathTraceSampleCount`
   - `PathTraceAlbedo`
   - `PathTraceNormal`
   - `PathTraceMotion`
   - `PathTraceDepth`
   - `PathTraceDenoised`
   - `RayTracingAccelerationStructures`
   - `RayTracingScratchBuffers`
   - `ShaderBindingTable`
2. Register persistent size-dependent targets through the render graph.
3. Add memory estimates for all path tracing images and buffers.
4. Add resize handling and accumulation invalidation.
5. Add resource inventory and barrier diagnostics for path tracing resources.

Validation:

- Resize recreates path tracing resources.
- Accumulation resets on resize, camera cut, FOV change, scene reload, backend switch, material changes, and relevant light changes.
- Render graph diagnostics include path tracing images, AS buffers, scratch buffers, and SBT memory.

## Phase 5: Acceleration Structure Manager

Implementation steps:

1. Add `AccelerationStructureManager`.
2. Add static BLAS creation for processed opaque meshes.
3. Add a BLAS cache keyed by processed mesh asset, vertex/index buffer identity, geometry flags, and build flags.
4. Add TLAS build per frame from visible and path-traceable scene instances.
5. Track AS memory through the existing GPU allocation/memory diagnostics.
6. Use the existing fence-based deletion model for delayed AS and scratch destruction.
7. Add instance masks:
   - static opaque
   - dynamic opaque
   - alpha masked approximate
   - emissive
   - foliage approximate
   - excluded
8. Start with static opaque only; leave dynamic/skinned/foliage flags disabled until later phases.

Validation:

- TLAS contains expected static opaque instances.
- BLAS cache reuses geometry across instances.
- Scene reload releases old AS memory after fences retire.
- Non-path-traceable geometry is excluded with diagnostics.

## Phase 6: Minimal Pure Path Tracing Pass

Implementation steps:

1. Add `PathTracingRendererBackend`.
2. Add ray generation, miss, closest-hit, and optional any-hit shaders.
3. Add shader binding table creation.
4. Dispatch one ray per pixel into `PathTraceColor`.
5. Support:
   - camera ray generation
   - environment miss color
   - closest-hit barycentric interpolation
   - base color texture lookup
   - normal mapping disabled initially
   - direct analytic light disabled initially
   - one diffuse bounce or environment-only shading for first bring-up
6. Composite `PathTraceColor` to swapchain through the existing tone/composite path or a minimal path tracing composite pass.
7. Add hard fallback to `ForwardPlus` if pipeline or SBT creation fails.

Validation:

- A static opaque sample scene renders through pure path tracing.
- Vulkan validation is clean.
- RenderDoc shows TLAS, SBT, path trace output, and swapchain composite.
- Performance snapshot reports path tracing CPU/GPU cost and zero costs for inactive raster passes.

## Phase 7: Material And Texture Parity

Implementation steps:

1. Build `PathTracingSceneBridge` that converts existing `SceneRenderingData`, mesh buffers, material buffers, and bindless textures into path tracing shader inputs.
2. Support core material properties:
   - base color
   - metallic
   - roughness
   - normal map
   - emissive
   - alpha mode
   - alpha cutoff
3. Add any-hit shader support for alpha-masked geometry.
4. Keep blended transparency excluded initially.
5. Match texture coordinate, tangent frame, normal map convention, and material factors against the existing forward shader.
6. Add a material debug view comparing raster and path tracing material classification.

Validation:

- Opaque and alpha-masked glTF materials render consistently.
- Bindless texture indices match host/shader tests.
- Alpha-masked curtains/foliage can be enabled behind a quality setting after correctness is established.

## Phase 8: Direct Lighting

Implementation steps:

1. Add direct lighting for:
   - directional lights
   - point lights
   - spot lights
   - environment map
2. Add next event estimation for analytic lights.
3. Add multiple importance sampling for environment and emissive sampling when ready.
4. Reuse existing light buffers where possible.
5. Add shadow rays through TLAS.
6. Add radiance clamps to prevent fireflies.

Validation:

- Direct lighting roughly matches the raster renderer for simple scenes.
- Shadow rays respect opaque and alpha-masked blockers.
- Debug views show direct, indirect, environment, and emissive contributions.

## Phase 9: Accumulation And Reprojection Policy

Implementation steps:

1. Add accumulation buffer and sample-count buffer.
2. Reset accumulation on:
   - camera movement beyond threshold
   - camera cut
   - projection/FOV change
   - resolution change
   - scene reload
   - material or texture change
   - light movement/change, unless temporal reuse policy explicitly allows it
3. Add optional motion-vector-guided reprojection only after basic accumulation is stable.
4. Add per-pixel sample count diagnostics.
5. Add `PathTracingAccumulationMode`:
   - Disabled
   - CameraStillOnly
   - Reprojected
   - Offline

Validation:

- Static camera converges.
- Moving camera does not smear stale samples.
- Accumulation reset reasons are visible in diagnostics.

## Phase 10: Denoiser Boundary

Implementation steps:

1. Add denoiser guide outputs:
   - albedo
   - normal
   - depth
   - motion
   - roughness if needed
2. Add a backend-neutral `IPathTracingDenoiser`.
3. Start with a no-op denoiser implementation.
4. Add optional denoiser integrations later:
   - NRD/ReLAX/ReBLUR-style path if licensing and native integration are acceptable.
   - RTXPT/NRD bridge only inside optional backend/module.
5. Keep denoiser failure non-fatal.

Validation:

- Denoiser off path is always available.
- Guide buffers are inspectable.
- Denoiser memory and GPU time appear in diagnostics.

## Phase 11: Dynamic Geometry Support

Implementation steps:

1. Add TLAS refit/update for dynamic rigid transforms.
2. Add BLAS rebuild/refit policy for dynamic meshes.
3. Add skinned mesh support:
   - initial path: consume GPU-skinned vertex buffers if available.
   - fallback: exclude skinned meshes with clear diagnostics.
4. Add foliage approximation:
   - start with authored mesh instances.
   - add alpha-mask any-hit only after cost is measured.
   - add impostor or simplified proxy geometry if needed.
5. Add per-category AS update budgets.

Validation:

- Moving rigid objects update transforms correctly.
- Dynamic update cost is bounded.
- Unsupported dynamic categories are reported rather than silently missing.

## Phase 12: Production Runtime Modes

Implementation steps:

1. Add runtime modes:
   - `ForwardPlus`
   - `PathTracingPreview`
   - `PathTracingGameplay`
   - `PathTracingPhoto`
   - `PathTracingOfflineAccumulation`
2. Define defaults:
   - Preview: low spp, low bounces, no expensive transparent paths.
   - Gameplay: 1 spp plus denoiser, strict frame budget.
   - Photo: pause-friendly high accumulation.
   - Offline: high sample target, not intended for realtime gameplay.
3. Add budget controls:
   - max AS build time
   - max path tracing GPU time
   - dynamic resolution scale
   - max samples per frame
   - max emissive lights sampled
4. Add automatic fallback or quality reduction when budgets are exceeded.

Validation:

- Mode changes are visible and deterministic.
- Gameplay mode does not silently enter offline-quality cost.
- Performance snapshots record active mode and budget status.

## Phase 13: RTXPT Optional Backend Evaluation

Perform this only after the engine-native backend boundary, AS manager, resource model, and diagnostics exist.

Implementation steps:

1. Review RTXPT license and all third-party dependencies with the intended commercial distribution model.
2. Decide whether RTXPT is:
   - reference-only,
   - vendored as source in a closed optional module,
   - linked as a native dependency,
   - or not used directly.
3. If used directly, create `Njulf.Rendering.Rtxpt` as an optional module.
4. Add required notices and third-party license files.
5. Keep all RTXPT-specific types out of `Njulf.Core` and the default renderer path.
6. Add adapters for:
   - scene instances
   - mesh buffers
   - materials
   - textures
   - environment maps
   - analytic lights
   - camera
   - swapchain/output
7. Add CI/build switches so the engine can build without RTXPT.

Validation:

- Core engine builds without RTXPT.
- RTXPT backend can be enabled only when dependencies are present.
- Packaged game includes required notices and license materials.
- The backend can be removed without breaking `ForwardPlus` or engine asset loading.

## Phase 14: Diagnostics, Tooling, And Capture

Implementation steps:

1. Extend `RendererDiagnostics` with:
   - requested backend
   - active backend
   - backend fallback reason
   - RT supported/active
   - TLAS instance count
   - BLAS count
   - BLAS cache hit count
   - AS memory bytes
   - AS scratch bytes
   - SBT bytes
   - path trace target bytes
   - current spp
   - accumulated spp
   - max bounces
   - rays per pixel
   - primary rays
   - shadow rays
   - indirect rays
   - denoiser active
   - CPU AS build microseconds
   - GPU AS build microseconds
   - GPU path trace microseconds
   - GPU denoiser microseconds
   - GPU composite microseconds
2. Add debug views:
   - final path traced color
   - accumulated color
   - albedo guide
   - normal guide
   - depth guide
   - sample count
   - direct lighting
   - indirect lighting
   - environment contribution
   - material id/classification
   - TLAS instance mask
3. Include all fields in performance snapshots.
4. Add startup log entries for backend selection and RT capabilities.

Validation:

- Debug views can be cycled from the sample.
- Performance snapshots explain path tracing cost and memory.
- Missing/unsupported features are visible without a debugger.

## Phase 15: Tests And Validation Scenes

Unit tests:

- Backend option parsing.
- Capability fallback.
- Path tracing setting defaults and clamps.
- Quality preset mapping.
- Render graph resource registration.
- Accumulation reset policy.
- AS instance classification.
- Material classification parity.
- Diagnostics default/populated snapshots.

Manual/GPU validation:

- Vulkan validation clean with `ForwardPlus`.
- Vulkan validation clean with `PurePathTracing`.
- Resize and swapchain recreation.
- Scene reload.
- Backend switch.
- Camera movement accumulation reset.
- Static Sponza/interior scene.
- Forest/foliage stress scene with foliage initially excluded or approximated.
- Alpha-masked material scene.
- Emissive material scene.
- Moving rigid object.
- Moving point light.
- Long-run memory leak audit.
- RenderDoc capture for AS, SBT, path tracing dispatch, denoiser, and composite.

## Recommended Milestone Order

1. Keep following the revised renderer architecture plan through graph resource ownership, barrier planning, diagnostics, and production pipeline declaration.
2. Add backend selection and fallback reporting.
3. Add optional RT capability detection.
4. Add path tracing settings, controls, diagnostics placeholders, and performance snapshot fields.
5. Add path tracing graph resources.
6. Add acceleration structure manager for static opaque geometry.
7. Bring up a minimal environment-only path tracing pass.
8. Add material and texture parity for opaque geometry.
9. Add direct lighting and shadow rays.
10. Add accumulation and reset policy.
11. Add denoiser boundary and guide buffers.
12. Add dynamic rigid geometry.
13. Add alpha-masked geometry and foliage approximations.
14. Evaluate optional RTXPT backend integration.
15. Tune production runtime modes and budgets.

## Non-Goals For The First Shipping Slice

- Replacing the existing Forward+ renderer.
- Making RT extensions required for all users.
- Full transparent path tracing.
- Caustics.
- Spectral rendering.
- Bidirectional path tracing.
- Neural denoising as a hard dependency.
- RTXPT source vendoring before licensing and dependency review.
- Skinned mesh contribution to indirect lighting before static/dynamic-rigid support is stable.

## First Definition Of Done

The first production-usable pure path tracing backend is ready when:

- The game can choose `ForwardPlus` or `PurePathTracing` at startup.
- Unsupported hardware falls back cleanly to `ForwardPlus`.
- Static opaque glTF scenes render through the path tracer.
- Opaque material textures, normals, metallic, roughness, emissive, and alpha mask are represented correctly enough for content review.
- Direct lighting, environment lighting, shadow rays, and at least one indirect bounce work.
- Accumulation converges and resets predictably.
- Denoiser integration is optional, with a no-op fallback.
- Path tracing memory, AS memory, SBT memory, CPU/GPU timings, sample count, bounces, and fallback reasons appear in diagnostics and performance snapshots.
- Vulkan validation and RenderDoc captures are clean.
- The existing Forward+ renderer remains unchanged in behavior when selected.

