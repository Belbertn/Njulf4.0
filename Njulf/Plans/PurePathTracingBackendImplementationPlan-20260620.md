# Renderer Backend And NVIDIA Path Tracing Implementation Plan

Date: 2026-06-20

## Goal

Add a renderer backend model with three shipping-oriented modes:

1. `ForwardPlus`: the current raster Forward+/meshlet renderer.
2. `ForwardPlusRayTracing`: the current renderer plus optional hardware ray traced features such as shadows, reflections, GI validation/probe updates, or high-end debug paths.
3. `NvidiaPathTracing`: a pure path tracing backend built around NVIDIA RTXPT or an RTXPT-derived integration layer.

The goal is not to write a full production path tracer from scratch. The engine-native ray tracing work should provide shared capability detection, acceleration structure ownership, scene/material adapters, diagnostics, and hybrid Forward+ features. Pure path tracing should be delivered through the NVIDIA backend once licensing, third-party dependencies, and distribution terms are accepted.

Non-RT GPUs must continue to run `ForwardPlus`.

## Current Renderer Context

Relevant existing systems:

- `IRenderer` is a small engine-facing interface in `Njulf.Core/Interfaces/IRenderer.cs`.
- `VulkanRenderer` is currently registered as the default `IRenderer` in `Njulf.Rendering/ServiceCollectionExtensions.cs`.
- The renderer already has a Vulkan context, swapchain manager, synchronization manager, command buffer manager, buffer manager, staging ring, bindless heap, texture manager, mesh manager, material manager, light manager, scene data builder, render graph, diagnostics, GPU timings, quality presets, feature isolation, and performance snapshots.
- The current Vulkan device requirements include swapchain, dynamic rendering, synchronization2, mesh shader, buffer device address, and deferred host operations. They do not yet require acceleration structures, ray query, or ray tracing pipeline extensions.
- The revised renderer architecture plan should remain the main near-term track. Graph resource ownership, barrier planning, graph diagnostics, processed mesh assets, and production pipeline declaration all make this path tracing backend easier and safer.

## Design Principles

1. Keep `ForwardPlus`, `ForwardPlusRayTracing`, and `NvidiaPathTracing` as separate selectable modes.
2. Share device, swapchain, upload, asset, scene, texture, material, and diagnostics infrastructure where practical.
3. Do not force RT requirements into normal renderer startup.
4. Add fallback behavior before adding expensive features.
5. Use engine-native RT infrastructure for hybrid Forward+ features and for feeding the NVIDIA path tracing backend.
6. Avoid building a production path tracer from scratch unless the NVIDIA backend becomes legally or technically blocked.
7. Treat every RT/path tracing feature as debuggable through diagnostics and performance snapshots.
8. Keep RTXPT-derived code in an optional module with clear attribution and dependency boundaries.

## Proposed Module Layout

Engine-native layout:

```text
Njulf.Rendering/
  Backends/
    RendererBackendKind.cs
    IRendererBackend.cs
    RendererBackendFactory.cs
    ForwardPlusRendererBackend.cs
    ForwardPlusRayTracingRendererBackend.cs
  RayTracing/
    RayTracingCapability.cs
    RayTracingContext.cs
    AccelerationStructureManager.cs
    BlasCache.cs
    TlasBuilder.cs
    RayTracingSettings.cs
    RayTracingDiagnostics.cs
    RayTracingSceneBridge.cs
    HybridRayTracingResources.cs
```

NVIDIA pure path tracing backend layout:

```text
Njulf.Rendering.Rtxpt/
  NOTICE.md
  ThirdPartyLicenses/
  RtxptBackend.cs
  RtxptSceneAdapter.cs
  RtxptMaterialAdapter.cs
  RtxptTextureAdapter.cs
  RtxptSettingsAdapter.cs
  RtxptDiagnosticsBridge.cs
  RtxptBuildConfig.cs
```

Keep the RTXPT module out of core renderer code so the game can build and ship without it when required.

## Phase 1: Backend Selection Boundary

Implementation steps:

1. Add `RendererBackendKind`:

```csharp
public enum RendererBackendKind
{
    ForwardPlus = 0,
    ForwardPlusRayTracing = 1,
    NvidiaPathTracing = 2
}
```

2. Add backend selection to `RenderingOptions`.
3. Add environment variable support, such as `NJULF_RENDERER_BACKEND=ForwardPlus|ForwardPlusRayTracing|NvidiaPathTracing`.
4. Add an `IRendererBackend` internal interface that mirrors frame lifecycle needs but allows backend-specific diagnostics.
5. Keep public `IRenderer` stable.
6. Convert existing `VulkanRenderer` registration into a factory that creates the selected backend.
7. Keep `ForwardPlus` as the default.
8. Add startup diagnostics reporting requested backend, active backend, fallback reason, RT capability status, and RTXPT module availability.

Validation:

- Existing sample runs unchanged with `ForwardPlus`.
- `ForwardPlusRayTracing` falls back to `ForwardPlus` on unsupported hardware.
- `NvidiaPathTracing` falls back to `ForwardPlus` when RTXPT is unavailable or unsupported.
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
5. If `ForwardPlusRayTracing` is requested without required RT support, fall back to `ForwardPlus`.
6. If `NvidiaPathTracing` is requested without RTXPT support, fall back to `ForwardPlus` or `ForwardPlusRayTracing` based on user preference.

Validation:

- Non-RT GPUs still initialize.
- RT GPUs report support without changing active renderer behavior.
- Tests cover simulated missing extensions and missing features through `DeviceRequirementOverride`-style hooks.

## Phase 3: Shared Ray Tracing And Path Tracing Settings

Implementation steps:

1. Add `RayTracingSettings` and `PathTracingSettings` to render settings.
2. Include:
   - `Enabled`
   - `BackendKind`
   - `HybridRayTracedShadowsEnabled`
   - `HybridRayTracedReflectionsEnabled`
   - `HybridRayTracedGiEnabled`
   - `HybridRayQueryValidationEnabled`
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
3. Add backend-specific quality defaults:
   - Low: `ForwardPlus`, RT disabled.
   - Medium: `ForwardPlus`, RT disabled by default.
   - High: `ForwardPlusRayTracing` optional features allowed when supported.
   - Ultra: `ForwardPlusRayTracing` with high-end RT features allowed when supported.
   - Cinematic/Photo: `NvidiaPathTracing` if the backend and hardware are available.
4. Extend `SampleInputController.cs` with controls for backend mode, hybrid RT features, accumulation reset, samples, bounces, denoiser, and debug views.
5. Add setting serialization if the existing render settings save/load path is used.

Validation:

- Settings clamp to safe ranges.
- Quality presets produce predictable raster, hybrid RT, and NVIDIA path tracing settings.
- Sample controls print active backend, RT support, RTXPT support, active hybrid features, samples, bounces, accumulation count, denoiser state, and fallback reason.

## Phase 4: Render Graph Resources For Hybrid RT And NVIDIA Path Tracing

Implementation steps:

1. Add render graph resource IDs for:
   - `RayTracedShadowMask`
   - `RayTracedReflectionColor`
   - `RayTracedGiValidation`
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
3. Add memory estimates for all hybrid RT and path tracing images and buffers.
4. Add resize handling and accumulation invalidation.
5. Add resource inventory and barrier diagnostics for hybrid RT and path tracing resources.

Validation:

- Resize recreates RT/path tracing resources.
- Accumulation resets on resize, camera cut, FOV change, scene reload, backend switch, material changes, and relevant light changes.
- Render graph diagnostics include hybrid RT images, path tracing images, AS buffers, scratch buffers, and SBT memory.

## Phase 5: Acceleration Structure Manager

Implementation steps:

1. Add `AccelerationStructureManager`.
2. Add static BLAS creation for processed opaque meshes.
3. Add a BLAS cache keyed by processed mesh asset, vertex/index buffer identity, geometry flags, and build flags.
4. Add TLAS build per frame from visible and ray-traceable scene instances.
5. Track AS memory through the existing GPU allocation/memory diagnostics.
6. Use the existing fence-based deletion model for delayed AS and scratch destruction.
7. Add instance masks:
   - static opaque
   - dynamic opaque
   - alpha masked approximate
   - emissive
   - foliage approximate
   - excluded
8. Start with static opaque only for shared infrastructure; leave dynamic/skinned/foliage flags disabled until later phases.
9. Expose the AS manager to `ForwardPlusRayTracing`; expose scene/geometry handles to `NvidiaPathTracing` through the RTXPT adapter only if sharing is practical.

Validation:

- TLAS contains expected static opaque instances.
- BLAS cache reuses geometry across instances.
- Scene reload releases old AS memory after fences retire.
- Non-ray-traceable geometry is excluded with diagnostics.

## Phase 6: NVIDIA Path Tracing Backend Bring-Up

Implementation steps:

1. Create `Njulf.Rendering.Rtxpt` as an optional module.
2. Add build switches so the engine can build without RTXPT.
3. Add required NVIDIA and third-party license notices before vendoring or linking any RTXPT code.
4. Add `NvidiaPathTracingRendererBackend`.
5. Initialize the RTXPT/Donut/NVIDIA backend against the existing Vulkan device and swapchain if supported. If RTXPT requires its own device ownership model, isolate that behind the backend and document the lifetime boundary.
6. Add adapters for camera, swapchain output, environment map, textures, mesh buffers, material buffers, and analytic lights.
7. Render a static opaque sample scene through RTXPT into `PathTraceColor`.
8. Composite `PathTraceColor` to swapchain through the existing composite path or a dedicated path tracing composite pass.
9. Add hard fallback to `ForwardPlus` if RTXPT initialization, dependency loading, pipeline creation, or scene translation fails.

Validation:

- A static opaque sample scene renders through the NVIDIA path tracing backend.
- Vulkan validation is clean.
- RenderDoc shows RTXPT resources, path trace output, and swapchain composite.
- Performance snapshot reports NVIDIA path tracing CPU/GPU cost and zero costs for inactive raster passes.

## Phase 7: RTXPT Scene, Material, And Texture Parity

Implementation steps:

1. Build `RtxptSceneAdapter` that converts existing scene data, mesh buffers, material buffers, and texture references into RTXPT-compatible inputs.
2. Support core material properties:
   - base color
   - metallic
   - roughness
   - normal map
   - emissive
   - alpha mode
   - alpha cutoff
3. Map alpha-masked geometry through RTXPT's supported opacity/alpha path.
4. Keep blended transparency excluded initially.
5. Match texture coordinate, tangent frame, normal map convention, and material factors against the existing forward shader.
6. Add a material debug view comparing raster and path tracing material classification.

Validation:

- Opaque and alpha-masked glTF materials render consistently.
- Bindless texture indices match host/shader tests.
- Alpha-masked curtains/foliage can be enabled behind a quality setting after correctness is established.

## Phase 8: ForwardPlus Ray Tracing Features

Implementation steps:

1. Use the shared AS manager to add opt-in RT features to the current renderer.
2. Start with one low-risk feature:
   - ray traced hard/soft shadow validation for selected lights, or
   - ray traced reflection debug/quality mode, or
   - ray query validation for DDGI/probe updates.
3. Keep each feature controlled by `RenderSettings` and feature isolation.
4. Reuse existing light, material, environment, and scene buffers where possible.
5. Composite hybrid RT outputs inside the existing Forward+ frame.
6. Add quality and performance budget controls for each feature.

Validation:

- `ForwardPlus` remains unchanged when hybrid RT is disabled.
- `ForwardPlusRayTracing` visibly enables only the requested RT features.
- Hybrid RT outputs have debug views and GPU timings.
- Feature isolation can disable each hybrid RT feature.

## Phase 9: NVIDIA Path Tracing Accumulation And Reprojection Policy

Implementation steps:

1. Use RTXPT accumulation where available; expose an engine-facing accumulation buffer and sample-count view for diagnostics and compositing.
2. Reset accumulation on:
   - camera movement beyond threshold
   - camera cut
   - projection/FOV change
   - resolution change
   - scene reload
   - material or texture change
   - light movement/change, unless temporal reuse policy explicitly allows it
3. Use RTXPT/NVIDIA reprojection support where available; only add engine-side reprojection glue for reset policy, diagnostics, or composite integration.
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

## Phase 10: NVIDIA Denoiser Boundary

Implementation steps:

1. Add denoiser guide outputs:
   - albedo
   - normal
   - depth
   - motion
   - roughness if needed
2. Add a backend-neutral `IPathTracingDenoiser` interface only for engine settings, diagnostics, and fallback.
3. Prefer RTXPT/NVIDIA denoiser integration for `NvidiaPathTracing`.
4. Provide a no-op denoiser fallback when the NVIDIA denoiser path is unavailable.
5. Keep denoiser failure non-fatal.

Validation:

- Denoiser off path is always available.
- Guide buffers are inspectable.
- Denoiser memory and GPU time appear in diagnostics.

## Phase 11: Dynamic Geometry Support For Hybrid RT And NVIDIA Path Tracing

Implementation steps:

1. Add TLAS refit/update for dynamic rigid transforms in the shared AS path.
2. Add BLAS rebuild/refit policy for dynamic meshes where used by hybrid RT.
3. Add skinned mesh support:
   - initial path: consume GPU-skinned vertex buffers if available.
   - fallback: exclude skinned meshes with clear diagnostics.
4. Add foliage approximation:
   - start with authored mesh instances.
   - add alpha-mask any-hit only after cost is measured.
   - add impostor or simplified proxy geometry if needed.
5. Add per-category AS update budgets.
6. Mirror the same scene category support through the RTXPT adapter, using RTXPT-native update paths where possible.

Validation:

- Moving rigid objects update transforms correctly.
- Dynamic update cost is bounded.
- Unsupported dynamic categories are reported rather than silently missing.

## Phase 12: Production Runtime Modes

Implementation steps:

1. Add runtime modes:
   - `ForwardPlus`
   - `ForwardPlusRayTracing`
   - `NvidiaPathTracingPreview`
   - `NvidiaPathTracingGameplay`
   - `NvidiaPathTracingPhoto`
   - `NvidiaPathTracingOfflineAccumulation`
2. Define defaults:
   - `ForwardPlus`: broad hardware support.
   - `ForwardPlusRayTracing`: selected RT effects, strict frame budget.
   - NVIDIA preview: low spp, low bounces, no expensive transparent paths.
   - NVIDIA gameplay: realtime RTXPT settings plus denoiser, strict frame budget.
   - NVIDIA photo: pause-friendly high accumulation.
   - NVIDIA offline: high sample target, not intended for realtime gameplay.
3. Add budget controls:
   - max AS build time
   - max path tracing GPU time
   - dynamic resolution scale
   - max samples per frame
   - max emissive lights sampled
4. Add automatic fallback or quality reduction when budgets are exceeded.

Validation:

- Mode changes are visible and deterministic.
- Gameplay modes do not silently enter offline-quality cost.
- Performance snapshots record active mode and budget status.

## Phase 13: RTXPT Dependency And Packaging Hardening

Perform this before treating `NvidiaPathTracing` as shippable.

Implementation steps:

1. Review RTXPT license and all third-party dependencies with the intended commercial distribution model.
2. Decide whether RTXPT is vendored as source in a closed optional module or linked as a native dependency.
3. Add required notices and third-party license files.
4. Keep all RTXPT-specific types out of `Njulf.Core` and the default renderer path.
5. Add CI/build switches so the engine can build without RTXPT.
6. Add packaging checks so a commercial build includes all required notices and runtime dependencies.
7. Add a documented fallback path for builds where RTXPT is disabled.

Validation:

- Core engine builds without RTXPT.
- `NvidiaPathTracing` can be enabled only when dependencies are present.
- Packaged game includes required notices and license materials.
- The backend can be removed without breaking `ForwardPlus` or engine asset loading.

## Phase 14: Diagnostics, Tooling, And Capture

Implementation steps:

1. Extend `RendererDiagnostics` with:
   - requested backend
   - active backend
   - backend fallback reason
   - RT supported/active
   - RTXPT available/active
   - hybrid RT features active
   - TLAS instance count
   - BLAS count
   - BLAS cache hit count
   - AS memory bytes
   - AS scratch bytes
   - SBT bytes
   - hybrid RT target bytes
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
   - ray traced shadow mask
   - ray traced reflection color
   - ray traced GI/probe validation
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
- Performance snapshots explain hybrid RT and NVIDIA path tracing cost and memory.
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
- Vulkan validation clean with `ForwardPlusRayTracing`.
- Vulkan validation clean with `NvidiaPathTracing`.
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
- RenderDoc capture for AS, hybrid RT dispatches, NVIDIA path tracing resources, denoiser, and composite.

## Recommended Milestone Order

1. Keep following the revised renderer architecture plan through graph resource ownership, barrier planning, diagnostics, and production pipeline declaration.
2. Add backend selection and fallback reporting.
3. Add optional RT capability detection.
4. Add RT/path tracing settings, controls, diagnostics placeholders, and performance snapshot fields.
5. Add hybrid RT and NVIDIA path tracing graph resources.
6. Add acceleration structure manager for `ForwardPlusRayTracing` static opaque geometry.
7. Create the optional `Njulf.Rendering.Rtxpt` module and build switches.
8. Bring up `NvidiaPathTracing` with a static opaque scene through RTXPT.
9. Add RTXPT material and texture parity for opaque geometry.
10. Add RTXPT accumulation, denoiser guide buffers, and reset policy.
11. Add the first `ForwardPlusRayTracing` feature using shared AS infrastructure.
12. Add dynamic rigid geometry support for both hybrid RT and RTXPT adapter paths.
13. Add alpha-masked geometry and foliage approximations.
14. Harden RTXPT dependency packaging and license notices.
15. Tune production runtime modes and budgets.

## Non-Goals For The First Shipping Slice

- Replacing the existing Forward+ renderer.
- Making RT extensions required for all users.
- Full transparent path tracing.
- Caustics.
- Spectral rendering.
- Bidirectional path tracing.
- Neural denoising as a hard dependency.
- RTXPT source vendoring or binary shipping before licensing and dependency review.
- Building a full engine-native production path tracer unless the NVIDIA backend becomes blocked.
- Skinned mesh contribution to indirect lighting before static/dynamic-rigid support is stable.

## First Definition Of Done

The first production-usable backend split is ready when:

- The game can choose `ForwardPlus`, `ForwardPlusRayTracing`, or `NvidiaPathTracing` at startup.
- Unsupported hardware falls back cleanly to `ForwardPlus`.
- `ForwardPlusRayTracing` can enable at least one hardware RT feature while preserving normal `ForwardPlus` behavior when disabled.
- Static opaque glTF scenes render through the NVIDIA path tracing backend.
- Opaque material textures, normals, metallic, roughness, emissive, and alpha mask are represented correctly enough in the NVIDIA backend for content review.
- NVIDIA path tracing direct lighting, environment lighting, shadow rays, and indirect bounce behavior are usable through RTXPT.
- NVIDIA path tracing accumulation converges and resets predictably.
- NVIDIA denoiser integration is optional, with a no-op fallback.
- Hybrid RT memory, path tracing memory, AS memory, CPU/GPU timings, sample count, bounces, and fallback reasons appear in diagnostics and performance snapshots.
- Vulkan validation and RenderDoc captures are clean.
- The existing Forward+ renderer remains unchanged in behavior when selected.
