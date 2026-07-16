# Njulf Textured glTF Rendering Completion Plan

Target outcome: `dotnet run --project NjulfHelloGame` opens a Silk.NET Vulkan window and renders `vintage_video_camera_2k.gltf` with imported mesh geometry, meshlets, material factors, diffuse/base-color texture, normal texture, ARM/metallic-roughness-occlusion texture, lights, task/mesh/fragment shaders, Forward+ light culling, and zero Vulkan validation errors.

This file supersedes the material/texture-related parts of `NjulfRemainingWorkPlan.md`. Keep the older plan for broad project tracking, but use this file as the authoritative checklist for the textured glTF runtime path.

## 1. Runtime Example Contract

Current target file:
- `NjulfHelloGame/Program.cs`

Required behavior:
- `HelloGame` derives from `Game`.
- Services register core, rendering, assets, input, camera, and any model upload bridge before content loading.
- The example loads `vintage_video_camera_2k.gltf` through `Content.Load<Model>()`.
- The loaded `Model` contains render objects with GPU mesh handles and real GPU material data or stable material handles.
- The model's `.bin` and `textures/**` files are copied to output.
- Scene contains the model, a camera, and several lights.
- `Draw()` calls `Renderer.DrawScene(Scene, Camera)`.
- Camera movement works with keyboard and mouse.
- The sample fails fast with clear errors when the model, texture files, renderer upload service, or Vulkan requirements are missing.

Acceptance:
- `dotnet run --project NjulfHelloGame` renders the bundled glTF, not just a clear color or white/default material.
- The rendered model visibly uses base color, normal, and ARM/metallic-roughness-occlusion textures.
- Missing optional textures use deterministic default descriptors and do not trigger validation errors.

## 2. Asset Import Contract

Files:
- `Njulf.Assets/ModelImporter.cs`
- `Njulf.Assets/ContentManager.cs`
- `Njulf.Assets/IModelRenderUploadService.cs`

Required behavior:
- Import glTF/OBJ mesh vertices, indices, normals, tangents, bitangents, UVs, submesh boundaries, and material index per submesh.
- Import material factors: base color, metallic, roughness, ambient occlusion where available, normal scale, emissive color, alpha mode where practical.
- Import texture paths: base color/diffuse, normal, metallic-roughness/ARM/occlusion, emissive.
- Resolve texture paths relative to the model file, including URI escaping and platform separators.
- Detect unsupported embedded/buffer-view textures explicitly if not implemented; do not silently substitute defaults for required source data.
- `Content.Load<Model>()` requires an upload service and returns renderer-ready scene objects.
- `Content.Load<ModelMesh>()` remains available for CPU-only asset data without rendering dependencies.

Acceptance:
- A test imports the sample glTF and verifies material count, submesh material assignment, and resolved texture paths.
- A missing model or missing required external file produces a specific exception with the absolute path.

## 3. Renderer Model Upload Contract

Files:
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Resources/TextureManager.cs`

Required behavior:
- Convert imported submeshes into `GPUVertex[]` and indexed mesh data.
- Register each submesh through `MeshManager.RegisterMesh(..., generateMeshlets: true)` exactly once per loaded model instance unless a content cache supplies the existing model.
- Initialize default white, normal, and black textures before building GPU materials.
- Load material textures through `TextureManager` with correct sRGB choice:
  - base color: sRGB
  - emissive: sRGB
  - normal: linear
  - metallic-roughness/ARM/occlusion: linear
- Use `TextureManager` cache so re-loading the same resolved texture path reuses the same GPU texture and bindless index.
- Populate `GPUMaterialData` with imported factors and bindless texture indices.
- Store the correct material for each `RenderObject`; do not replace imported materials with a default material unless the source material is genuinely missing.
- Track loaded texture count, loaded material count, and default substitutions for diagnostics.

Acceptance:
- Re-loading the same model or texture does not duplicate texture GPU resources.
- Render objects produced from a multi-material model preserve per-submesh material assignment.
- Texture indices in `GPUMaterialData` are valid bindless texture indices and point at initialized descriptors.

## 4. Texture Manager Contract

Files:
- `Njulf.Rendering/Resources/TextureManager.cs`
- `Njulf.Rendering/Descriptors/BindlessHeap.cs`
- `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
- `Njulf.Shaders/common.glsl`

Required behavior:
- Create default white, normal, and black textures at canonical bindless indices from `BindlessIndex`.
- Load image files through the approved image loader.
- Upload texture data through staging memory.
- Transition images with synchronization2:
  - `Undefined -> TransferDstOptimal`
  - `TransferDstOptimal -> ShaderReadOnlyOptimal`
- Generate mipmaps when requested and supported; otherwise use one mip level and report the fallback.
- Allocate bindless texture indices through one allocator; update descriptor sets immediately after image view creation.
- Destroy textures fence-safely; descriptors must not dangle while in-flight frames can still read them.
- Do not hardcode default texture indices in renderer code; use `BindlessIndex.DefaultWhiteTexture`, `DefaultNormalTexture`, and `DefaultBlackTexture`.

Acceptance:
- Default texture descriptors are valid before any material upload.
- Missing optional material texture paths resolve to default texture handles.
- Texture descriptors remain valid across frames and after swapchain recreation.

## 5. SceneDataBuilder Material Contract

File:
- `Njulf.Rendering/Data/SceneDataBuilder.cs`

Required behavior:
- Resolve render-object materials through a production contract, not placeholder integers.
- Accept renderer material handles or `GPUMaterialData` during the transition period, but define one canonical material representation before final hardening.
- Deduplicate uploaded materials without losing per-submesh assignments.
- Default material uses canonical bindless default texture constants, not raw integers.
- Upload material buffer every frame when scene material data changes.
- Populate `SceneRenderingData.MaterialCount` and `TextureCount` from real renderer diagnostics.
- Remove unused `_externalMaterialIndexMap` unless stable external material indices are fully implemented.
- Reject unsupported material object types with clear messages.

Acceptance:
- Empty scenes upload only the default material and zero draw commands.
- A scene with two objects sharing identical material data uploads one material entry and both objects reference it.
- A scene with two submeshes using different texture indices uploads two material entries.
- Non-zero raw integer material indices are either resolved through a real material registry or are not part of the public contract.

## 6. Shader Material Contract

Files:
- `Njulf.Shaders/common.glsl`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/forward.mesh`
- `Njulf.Shaders/forward.frag`

Required behavior:
- Host and shader structs match for `GPUVertex`, `GPUObjectData`, `GPUMaterialData`, mesh metadata, meshlet data, and push constants.
- `forward.task` dispatches visible meshlet draw commands.
- `forward.mesh` emits world position, normal, tangent, UV, material index, and object index.
- `forward.frag` samples material textures through bindless descriptors using non-uniform indexing where needed.
- Material shading combines:
  - base color factor * base color texture
  - normal map with tangent-space normal reconstruction
  - ARM/metallic-roughness-occlusion channels consistently documented
  - emissive factor * emissive texture
  - Forward+ tiled light lists
- Texture index validation is defensive but must not hide invalid material upload bugs; invalid indices should be diagnosed in debug builds where practical.

Acceptance:
- RenderDoc shows task/mesh dispatches and fragment shader sampling the sample model textures.
- No descriptor indexing validation errors occur.
- Missing optional textures sample default descriptors, not uninitialized descriptor slots.

## 7. Forward+ And Lighting Contract

Files:
- `Njulf.Rendering/Resources/LightManager.cs`
- `Njulf.Rendering/Pipeline/TiledLightCullingPass.cs`
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`
- `Njulf.Shaders/lightcull.comp`
- `Njulf.Shaders/forward.frag`

Required behavior:
- Upload lights to the canonical light buffer before light culling.
- Clear per-tile header/index buffers each frame.
- `lightcull.comp` writes tile light headers and light indices.
- Barriers are valid and pointer-safe:
  - depth prepass write -> compute shader read
  - compute shader tile writes -> fragment shader reads
- Forward shader reads tile light lists and applies lights to material-shaded surfaces.

Acceptance:
- Multiple point lights visibly affect only relevant screen regions.
- Light count cap is enforced with a clear exception.
- Vulkan validation reports no storage buffer read/write hazards.

## 8. Vulkan Validation And Runtime Hardening

Files:
- `Njulf.Rendering/Core/VulkanContext.cs`
- `Njulf.Rendering/Core/SwapchainManager.cs`
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Pipeline/*`
- `Njulf.Rendering/Utilities/BarrierBuilder.cs`

Required behavior:
- Query window-required Vulkan instance extensions from Silk.NET; do not hardcode platform surface extensions.
- Enable only required instance/device extensions and features, and validate availability before use.
- Required features include mesh shader/task shader, descriptor indexing, buffer device address, synchronization2, dynamic rendering, sampler anisotropy if anisotropic samplers are used, and maintenance4 if shader SPIR-V uses `LocalSizeId`.
- Descriptor set layouts include all stages that use bindless descriptors, including task and mesh stages.
- Push constant stage masks used at command recording must include all overlapping stages declared in pipeline layout.
- `BarrierBuilder` must not return `DependencyInfo` with non-zero counts and null barrier pointers. Either execute barriers with pinned arrays immediately or return an owned/pinnable barrier object.
- Swapchain image and depth image layout tracking remains correct through resize.

Acceptance:
- `dotnet run --project NjulfHelloGame` produces zero Vulkan validation errors during startup, first rendered frame, resize, and shutdown.
- No access violations occur from invalid synchronization structs.

## 9. Diagnostics Contract

Files:
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/SceneDataBuilder.cs`
- `Njulf.Rendering/Resources/TextureManager.cs`
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
- `Njulf.Rendering/VulkanRenderer.cs`

Required diagnostics:
- Visible object count.
- Visible meshlet count.
- Uploaded bytes.
- Light count.
- Tile count.
- Material count.
- Texture count.
- Default white/normal/black substitution counts.
- Loaded model name/path.
- Optional per-pass timings for depth, light cull, and forward.

Acceptance:
- NjulfHelloGame can print or expose one frame summary proving the sample uses imported textures and not only default material data.
- Diagnostics are available without requiring RenderDoc.

## 10. Tests Required For This Plan

CPU/unit tests:
- `BindlessIndexTests`: host/shader texture and buffer indices match, including default texture indices.
- `GPUStructLayoutTests`: material, object, vertex, meshlet, and push constant layouts match shader assumptions.
- `ModelImporterTests`: sample glTF imports material factors, submesh material index, and texture paths.
- `TextureManagerTests` where GPU-free mocking is possible: cache key behavior, fallback path behavior, default index contract.
- `ModelRenderUploadServiceTests`: imported material becomes `GPUMaterialData` with expected texture indices and defaults.
- `SceneDataBuilderTests`: material upload, material dedup, culling, draw-command count, invalid material rejection.
- `ContentRendererIntegrationTests`: `Content.Load<Model>()` returns render objects with valid mesh/material payloads when renderer services are registered.

GPU integration tests gated by `NJULF_RUN_GPU_TESTS=1`:
- Vulkan device bootstrap with required features.
- Texture upload smoke test.
- Mesh upload smoke test.
- Textured model render smoke test.
- Swapchain resize smoke test.
- Validation-clean shutdown test.

Acceptance:
- `dotnet test Njulf.sln` passes CPU tests without a GPU.
- `NJULF_RUN_GPU_TESTS=1 dotnet test Njulf.sln` runs GPU tests locally and is validation-clean.

## 11. Cleanup And Removal Rules

Remove or replace:
- Raw hardcoded default texture indices outside `BindlessIndex`.
- Placeholder material paths.
- Default-only material substitutions for imported materials.
- Dummy texture loading.
- Unused `_externalMaterialIndexMap` unless stable material-index resolution is implemented.
- Any production-critical `TODO`, `placeholder`, `For now`, `Module = default`, or broad `return null` paths.

Harden:
- Disposal order for model-uploaded textures, buffers, descriptors, and pipelines.
- Fence-safe resource retirement for textures and resized buffers.
- Descriptor updates after buffer or image replacement.
- Partial initialization disposal paths.
- Clear exceptions for unsupported GPU features and unsupported asset features.

Acceptance:
- `rg "TODO|placeholder|For now|Module = default|return null"` has no production-critical hits.
- All Vulkan result codes on production paths are checked.

## 12. Final Definition Of Done

The textured glTF path is complete when:

1. `dotnet build Njulf.sln` passes without errors.
2. `dotnet test Njulf.sln` discovers and passes real CPU tests for bindless indices, GPU structs, import, upload mapping, and `SceneDataBuilder` material behavior.
3. `dotnet run --project NjulfHelloGame` opens a window and renders `vintage_video_camera_2k.gltf`.
4. The model visibly uses imported diffuse/base-color, normal, and ARM/metallic-roughness-occlusion textures.
5. RenderDoc confirms mesh/task shader rendering through `CmdDrawMeshTasksEXT`, not per-object draw calls.
6. RenderDoc or diagnostics confirm valid material count, texture count, and non-default texture indices for the sample model.
7. Forward+ light culling runs and influences the textured model.
8. Vulkan validation reports zero errors during startup, steady-state rendering, resize, and shutdown.
9. Re-loading the same model or texture does not duplicate GPU texture resources unnecessarily.
10. Missing optional textures use default bindless descriptors deterministically and validation-cleanly.
