# Njulf Remaining Work Completion Plan

Target outcome: NjulfHelloGame loads a model, generates meshlets, uploads GPU data, renders through task/mesh/fragment shaders with Forward+ lighting, passes tests, and runs with Vulkan validation clean.

## 1. Stabilize Architecture Contracts

- Define one canonical `FramesInFlight` constant shared by renderer, sync, staging, scene buffers, and tests.
- Finalize bindless buffer index contract in `BindlessIndexTable.cs`.
- Create shader-side `common.glsl` with matching constants and GPU structs.
- Add a unit test comparing C# bindless indices against shader constants.
- Add explicit ownership rules: `VulkanRenderer` orchestrates; managers own allocation; passes only record commands.

Acceptance:
- No duplicate frame-count constants.
- Host/shader bindless indices verified by tests.

## 2. Fix Renderer Frame Lifecycle

Update `Njulf.Rendering/VulkanRenderer.cs` before adding more rendering logic.

Required changes:
- Track swapchain image layouts correctly instead of always using `ImageLayout.Undefined`.
- Transition acquired images from their known current layout to `ColorAttachmentOptimal`.
- Transition final image to `PresentSrcKhr`, not `TransferSrcOptimal`.
- Use synchronization2 barriers with correct source/destination stages and access masks.
- Handle `ErrorOutOfDateKhr` and `SuboptimalKhr` during acquire/present without recursive unsafe flows.
- Move staging-ring advancement to a consistent point after frame completion or after fence wait.
- Ensure command buffer recording has one clear ownership path: `BeginFrame -> Draw/Clear/DrawScene -> EndFrame`.

Acceptance:
- Empty-frame rendering runs without validation errors.
- Swapchain resize works.
- No layout transition uses `Undefined` except first-use initialization.

## 3. Add Rendering Service Registration

Create `Njulf.Rendering/ServiceCollectionExtensions.cs`.

Register:
- `IWindow`
- `VulkanContext`
- `SwapchainManager`
- `SynchronizationManager`
- `CommandBufferManager`
- `BufferManager`
- `StagingRing`
- `FenceBasedDeleter`
- `BindlessHeap`
- `TextureManager`
- `MeshManager`
- `LightManager`
- `SceneDataBuilder`
- `RenderGraph`
- `IRenderer -> VulkanRenderer`

Acceptance:
- A game can call `services.AddRendering(window)`.
- No manual construction needed in example code.

## 4. Replace Placeholder Shader Pipeline Creation

Current mesh/compute pipelines use `Module = default`, which is invalid production code.

Implement:
- SPIR-V shader module loader.
- Shader module lifetime management.
- Pipeline cache optional but recommended.
- Proper pipeline layout using bindless buffer set, bindless texture set, and push constants.
- Dynamic rendering pipeline state with swapchain color format and depth format.
- Reverse-Z depth state: `CompareOp.GreaterOrEqual`, clear depth `0.0`.

Required shader files:
- `Njulf.Shaders/common.glsl`
- `Njulf.Shaders/depth.task`
- `Njulf.Shaders/depth.mesh`
- `Njulf.Shaders/forward.task`
- `Njulf.Shaders/forward.mesh`
- `Njulf.Shaders/forward.frag`
- `Njulf.Shaders/lightcull.comp`

Required build work:
- Add shader compilation step using `glslangValidator`, `shaderc`, or a documented MSBuild target.
- Emit `.spv` files into output directory.
- Fail build on shader compile errors.

Acceptance:
- Pipeline creation uses real SPIR-V.
- Shader compilation is repeatable from clean checkout.
- Missing shader files fail fast with useful errors.

## 5. Implement Shader Contract

`common.glsl` should define:

Bindless indices:
- Object buffer
- Material buffer
- Mesh metadata buffer
- Vertex buffer
- Index buffer
- Meshlet buffer
- Meshlet vertex index buffer
- Meshlet triangle index buffer
- Per-frame instance buffers
- Per-frame meshlet draw buffers
- Light buffer
- Tiled light headers
- Tiled light indices

GPU structs:
- `GPUVertex`
- `GPUObjectData`
- `GPUMaterialData`
- `GPUMeshInfo`
- `GPUMeshlet`
- `GPUMeshletDrawCommand`
- `GPULight`
- `GPUTiledLightHeader`
- Push constants for depth, forward, and light culling.

Acceptance:
- Struct sizes/alignment documented.
- Unit tests validate C# struct size and offsets where practical.

## 6. Complete MeshManager GPU Upload

Current `MeshManager` generates data but does not correctly upload or track offsets.

Implement:
- Consolidated vertex buffer append.
- Consolidated index buffer append.
- Mesh metadata buffer append.
- Meshlet descriptor buffer append.
- Meshlet-local vertex index buffer append.
- Meshlet-local triangle index buffer append.
- Geometric buffer growth with GPU copy from old to new.
- Fence-based retirement of old buffers.
- Bindless descriptor updates after buffer replacement.
- Offset/count validation.

Acceptance:
- Registering a mesh returns valid `MeshHandle`.
- All offsets are non-zero/accurate where appropriate.
- All meshlet references are in bounds.
- Buffer growth preserves old mesh data.

## 7. Complete SceneRenderingData

Implement actual per-frame GPU scene buffers.

Buffers:
- Object data buffer
- Material data buffer
- Instance buffer per frame
- Meshlet draw command buffer per frame
- Light tile header buffer
- Light tile index buffer

Required behavior:
- Allocate initial capacity.
- Grow when scene exceeds capacity.
- Upload through staging ring.
- Record copy commands into transfer or graphics command buffer.
- Use barriers from transfer write to shader read.
- Register all fixed bindless slots.

Acceptance:
- `SceneDataBuilder.Build()` creates real uploaded GPU data.
- No placeholder upload comments remain.
- Scene buffer sizes are queryable for diagnostics.

## 8. Implement SceneDataBuilder Correctly

Replace placeholder frustum and upload code.

Required:
- Extract frustum planes from view-projection matrix.
- CPU frustum cull render objects.
- Generate one meshlet draw command per visible meshlet.
- Deduplicate mesh/material references.
- Validate meshlet offsets against `MeshManager`.
- Write object, material, instance, and draw-command arrays into staging memory.
- Emit upload copy commands.

Acceptance:
- Empty scenes produce zero draw commands.
- Visible mesh produces expected meshlet draw count.
- Culled mesh produces zero draw commands.
- Unit tests cover these cases.

## 9. Implement Task/Mesh Shaders

Depth path:
- `depth.task` reads `GPUMeshletDrawCommand`, performs meshlet culling/work distribution, and emits mesh workgroups.
- `depth.mesh` reads vertex/index/meshlet buffers through bindless SSBOs, emits meshlet triangles, and writes clip-space position only.

Forward path:
- `forward.task` uses the same draw-command distribution as depth path.
- `forward.mesh` emits positions, normals, UVs, material indices, and object indices.
- `forward.frag` reads material data, samples bindless textures, reads tiled light lists, and performs baseline PBR shading.
- Missing textures must use default material factors/default textures, not invalid descriptors.

Acceptance:
- Rendering uses `CmdDrawMeshTasksEXT`.
- No per-object draw calls.
- RenderDoc shows mesh/task dispatches, not one draw per object.

## 10. Implement Forward+ Light Culling

`TiledLightCullingPass` dispatch exists, but the complete data contract needs finishing.

Implement:
- Light buffer upload in `LightManager`.
- Per-tile header buffer.
- Per-tile light index buffer.
- `lightcull.comp` with one workgroup per tile.
- Depth-range read from the depth prepass.
- Point-light-vs-tile-frustum tests.
- Compact per-tile light index output.
- Barriers: depth prepass write -> compute shader read; compute shader write -> fragment shader read.

Acceptance:
- Multiple dynamic lights affect only relevant screen tiles.
- Light count cap is enforced with clear errors.
- Validation clean for storage buffer writes/reads.

## 11. Complete Texture Loading

Replace dummy texture path.

Implement:
- Image file loading using an approved library or existing content path.
- Staging upload.
- Image layout transitions: `Undefined -> TransferDstOptimal -> ShaderReadOnlyOptimal`.
- Mipmap generation if requested and supported.
- Default white, normal, and black textures.
- Bindless texture index allocation and descriptor update.
- Texture lifetime and fence-safe deletion.

Acceptance:
- Model material textures load.
- Missing optional texture uses default texture.
- Texture descriptors remain valid across frames.

## 12. Complete Content-To-Renderer Integration

Current assets import meshlets independently, but renderer-side mesh registration must be the canonical GPU path.

Implement:
- Convert imported `ModelMesh`/`MeshletMesh` into renderer mesh registration data.
- Store `MeshHandle` and material handles in `Model`.
- Add API to register content with renderer or content manager dependency injection.
- Avoid direct rendering dependencies in `Njulf.Assets` if preserving module boundaries; use interfaces or a renderer upload step.

Acceptance:
- `Content.Load<Model>("...")` can produce renderable model data.
- Adding model to scene results in GPU mesh registration once.

## 13. Complete Material And Texture Integration

Low-level texture upload is not enough; imported materials must become renderer-visible `GPUMaterialData` with valid bindless texture indices.

Implement:
- Extend asset import to read glTF/OBJ material properties: base color, metallic, roughness, normal scale, emissive color, alpha mode where supported, and texture paths.
- Resolve texture paths relative to the model file, including glTF external files and embedded or buffer-view texture sources where practical.
- Register textures through `TextureManager` exactly once per unique resolved asset and reuse bindless texture indices across materials.
- Create a renderer-side material registration path that returns stable material handles or indices instead of passing placeholder integer materials.
- Store material references per model mesh or submesh so each `RenderObject` uses the correct material.
- Populate `GPUMaterialData` with imported factors and texture indices, using default white, normal, and black textures for missing optional maps.
- Update `SceneDataBuilder` material resolution so it uploads real material data and deduplicates by material identity/content without replacing external material indices with defaults.
- Ensure `forward.frag` combines material factors and sampled textures correctly for base color, normal, metallic-roughness-AO, and emissive contribution.
- Add diagnostics that report loaded material count, texture count, and default-texture substitutions for the sample model.

Acceptance:
- `Content.Load<Model>("...")` preserves per-material texture assignments from the source asset.
- NjulfHelloGame renders the bundled glTF with its diffuse, normal, and ARM textures instead of a white/default material.
- Missing material textures are deterministic and validation-clean through default bindless descriptors.
- Re-loading the same model or texture does not duplicate GPU texture resources unnecessarily.
- Unit or integration tests verify material import, texture index assignment, and `SceneDataBuilder` material upload behavior.

## 14. Fix Game Loop And Window Ownership

Current `Game` loop is a tight CPU loop and does not create a Silk window.

Implement:
- Silk.NET `IWindow` creation in `Game.Run()`.
- Hook `Load`, `Update`, `Render`, `FramebufferResize`, and `Closing`.
- Create input context from window and register it.
- Initialize renderer after window creation.
- Use actual delta time from Silk events.
- Support `VSync`.

Acceptance:
- `Game.Run()` opens a window.
- `Update` and `Draw` are called by Silk window events.
- `OnResize` recreates swapchain.

## 15. Build NjulfHelloGame Example

Replace `Hello, World!`.

Example should:
- Derive from `Game`.
- Configure rendering/assets/input.
- Load a simple glTF/OBJ model.
- Add it to `Scene`.
- Add camera and several lights.
- Render with `Renderer.DrawScene(Scene, Camera)`.
- Show basic camera movement.

Acceptance:
- Example runs from `dotnet run --project NjulfHelloGame`.
- It renders a model, not just clears the screen.

## 16. Add Unit Tests

Required tests:
- `BufferManagerTests`: handle generation validation, invalid handle throws, buffer size tracking.
- `BindlessIndexTests`: unique fixed indices and host/shader index match.
- `MeshletBuilderTests`: all triangles covered, no triangle duplicated, vertex/triangle limits respected, bounds valid.
- `SceneDataBuilderTests`: culling, draw command count, offset validation.
- `GPUStructLayoutTests`: size/alignment assumptions.

Acceptance:
- `dotnet test` runs actual tests.
- Tests pass without GPU where possible.

## 17. Add GPU Integration Tests

Add optional integration tests gated by environment variable, for example `NJULF_RUN_GPU_TESTS=1`.

Tests:
- Vulkan device bootstrap.
- Swapchain creation.
- Empty frame render.
- Mesh upload.
- One triangle/one cube render smoke test.
- Fence-based deletion after frames complete.

Acceptance:
- CPU-only test suite remains reliable in CI.
- GPU tests can be run locally with validation layers.

## 18. Add Validation And Debugging

Implement:
- Debug messenger.
- Debug object names for buffers, images, pipelines, descriptor sets.
- Validation layer enablement in debug builds.
- Optional best-practices validation.
- VMA allocation statistics at shutdown.

Acceptance:
- Example runs with zero validation errors.
- Resource leaks are reported clearly.

## 19. Add Render Diagnostics

Implement frame stats:
- Visible object count.
- Visible meshlet count.
- Uploaded bytes.
- Light count.
- Tile count.

Optional timestamp queries:
- Depth pass.
- Light cull pass.
- Forward pass.

Acceptance:
- Renderer can print or expose frame statistics.
- Diagnostics verify no per-object draw behavior.

## 20. Cleanup And Hardening

Remove:
- Placeholder comments.
- Dummy shader modules.
- Dummy texture loading.
- Stub culling.
- Unused variables and dead code.

Harden:
- Disposal order.
- Swapchain recreation.
- Device idle requirements.
- Buffer resizing.
- Descriptor updates after buffer replacement.
- Clear exceptions for unsupported GPU features.

Acceptance:
- `rg "TODO|placeholder|For now|Module = default|return null"` has no production-critical hits.
- All Vulkan result codes are checked.
- Dispose paths tolerate partial initialization.

## 21. Final Definition Of Done

The implementation is complete when:

1. `dotnet build Njulf.sln` passes cleanly.
2. `dotnet test Njulf.sln` discovers and passes real tests.
3. `dotnet run --project NjulfHelloGame` opens a window and renders a loaded model.
4. Rendering uses task/mesh shaders and `CmdDrawMeshTasksEXT`.
5. Forward+ light culling runs through `lightcull.comp`.
6. Textures and materials are imported, uploaded, bound, sampled, and visible in NjulfHelloGame.
7. Vulkan validation reports zero errors.
8. RenderDoc confirms no per-object draw calls for mesh rendering.
9. Bindless host/shader indices are tested.
10. Resize, shutdown, and resource deletion are validation-clean.
