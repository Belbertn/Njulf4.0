# Material Production Readiness Plan

Target outcome: renderer-visible materials use a stable production contract instead of storing raw `GPUMaterialData` on `RenderObject`. Imported glTF materials become renderer-owned material handles, scene upload resolves those handles through one canonical material registry, and material/texture lifetime is deterministic and validation-clean.

## 1. Add A Stable Material Handle

Create a renderer material handle, similar to `MeshHandle`.

Required behavior:
- Store an index and generation.
- Provide `Invalid`, `IsValid`, equality, and hashing.
- Reject invalid and stale handles with clear exceptions.
- Keep the handle lightweight enough to store on render objects and models.

Acceptance:
- `MaterialHandle.Invalid.IsValid == false`.
- Destroying/reusing a material invalidates old generation handles.
- Unit tests cover invalid, valid, stale, and equality behavior.

## 2. Add MaterialManager

Create `Njulf.Rendering/Resources/MaterialManager.cs`.

Responsibilities:
- Own CPU-side material registry.
- Own GPU material buffer allocation, upload, growth, and bindless registration.
- Register the canonical default material.
- Register imported materials and return `MaterialHandle`.
- Deduplicate identical material data where intended.
- Track diagnostics: registered material count, uploaded material count, default substitutions.
- Support fence-safe retirement if material buffers are replaced.

Acceptance:
- Materials are not passed around as raw `GPUMaterialData` outside renderer internals.
- GPU material buffer grows without losing existing materials.
- Bindless material buffer descriptor is updated after replacement.
- Invalid or stale material handles throw clear errors.

## 3. Integrate MaterialManager Into Rendering Services

Update `Njulf.Rendering/ServiceCollectionExtensions.cs`.

Register:
- `MaterialManager`
- Any material diagnostics service if separated from the manager.

Update renderer construction:
- Inject `MaterialManager` into `VulkanRenderer`, `SceneDataBuilder`, and `ModelRenderUploadService` as needed.

Acceptance:
- `services.AddRendering(window)` registers all material services.
- No sample or content path manually constructs material infrastructure.

## 4. Update Model Upload

Update `Njulf.Rendering/Resources/ModelRenderUploadService.cs`.

Required behavior:
- Continue resolving imported texture paths through `TextureManager`.
- Build renderer material data internally.
- Register each imported material through `MaterialManager`.
- Store `MaterialHandle` per submesh/render object.
- Preserve per-submesh material assignment.
- Do not store raw `GPUMaterialData` on `RenderObject`.

Acceptance:
- `Content.Load<Model>("...")` returns render objects with valid `MeshHandle` and `MaterialHandle`.
- Multi-material models preserve distinct submesh materials.
- Re-loading the same texture path reuses the existing texture resource.
- Missing optional textures resolve to default material descriptors deterministically.

## 5. Update Scene Upload

Update `Njulf.Rendering/Data/SceneDataBuilder.cs`.

Required behavior:
- Resolve `RenderObject.Material` through `MaterialManager`.
- Use `MaterialHandle` as the production material contract.
- Remove raw non-zero integer material index support from production paths.
- Remove direct `GPUMaterialData` support, or keep it only in narrowly scoped test helpers during transition.
- Populate `GPUObjectData.MaterialIndex` from the canonical material registry.
- Validate all material handles before writing draw commands.

Acceptance:
- Scene upload rejects unsupported material object types.
- Invalid or stale material handles fail before command recording reaches the GPU.
- Two objects sharing one material handle reference the same GPU material index.
- Two submeshes with different material handles produce distinct material references.

## 6. Tighten RenderObject Material Contract

Current issue:
- `RenderObject.Mesh` and `RenderObject.Material` are `object?`, which allows invalid runtime states.

Production options:
- Preferred: introduce typed renderer-neutral handles in a shared abstraction layer and change `RenderObject` to typed properties.
- Transitional: keep `object?` only temporarily, but enforce `MeshHandle` and `MaterialHandle` in renderer upload paths.

Acceptance:
- Public sample code does not assign raw `GPUMaterialData`.
- Renderer errors are explicit when a render object has an unsupported mesh or material type.
- Final production path avoids broad `object?` material usage where practical.

## 7. Update NjulfHelloGame Validation

Update `NjulfHelloGame/Program.cs`.

Required behavior:
- `ValidateUploadedModel()` checks for valid `MaterialHandle`, not `GPUMaterialData`.
- Texture-index validation moves to renderer diagnostics or `MaterialManager` inspection APIs.
- The sample can still fail fast if imported textures were replaced by defaults unexpectedly.

Acceptance:
- The sample validates renderability without depending on internal GPU upload structs.
- Diagnostics still prove base-color, normal, and ARM textures were imported.

## 8. Add Material Lifetime Management

Required behavior:
- Define ownership for model-uploaded materials and textures.
- Decide whether `ContentManager` owns uploaded model resources, or whether renderer resources are reference-counted and shared.
- Releasing/unloading a model decrements material and texture references where applicable.
- Destroyed materials must not leave dangling GPU references for in-flight frames.

Acceptance:
- `Content.Unload(model)` has deterministic behavior.
- Re-loading the same model does not duplicate material/texture resources unnecessarily.
- Destroying a material used by an in-flight frame is fence-safe.

## 9. Add Tests

Required CPU tests:
- `MaterialHandleTests`: invalid, valid, equality, generation mismatch.
- `MaterialManagerTests`: registration, lookup, dedup, stale handle rejection.
- `MaterialManagerBufferGrowthTests`: growth preserves existing material data.
- `ModelRenderUploadServiceTests`: imported materials become `MaterialHandle`s.
- `SceneDataBuilderTests`: scene upload resolves `MaterialHandle` and rejects raw invalid material objects.
- `ContentRendererIntegrationTests`: `Content.Load<Model>()` returns render objects with valid mesh and material handles.

Optional GPU tests gated by `NJULF_RUN_GPU_TESTS=1`:
- Material buffer upload smoke test.
- Textured model material-handle render smoke test.
- Material buffer growth render smoke test.
- Validation-clean unload/reload test.

Acceptance:
- `dotnet test Njulf.sln` passes CPU tests without a GPU.
- GPU tests validate the material handle path on local Vulkan hardware.

## 10. Cleanup Rules

Remove or replace:
- Raw `GPUMaterialData` assignment in sample/runtime code.
- Raw non-zero integer material indices in production scene upload.
- Any default-only substitution that hides a missing imported material.
- Broad unsupported material object paths without clear exceptions.

Harden:
- Material buffer disposal and replacement.
- Descriptor updates after material buffer growth.
- Partial initialization disposal.
- Diagnostics for material count, texture count, and default substitutions.

## Final Definition Of Done

The material path is production-ready when:

1. `RenderObject.Material` no longer stores raw `GPUMaterialData` in runtime/sample code.
2. Imported glTF materials are registered through `MaterialManager`.
3. `Content.Load<Model>()` produces render objects with stable `MaterialHandle`s.
4. `SceneDataBuilder` resolves material handles through one canonical material registry.
5. Invalid/stale material handles fail clearly before GPU command submission.
6. Re-loading the same model or texture avoids unnecessary duplicate GPU resources.
7. Material buffer growth preserves existing material data and updates bindless descriptors.
8. `NjulfHelloGame` still renders the sample glTF with base-color, normal, and ARM textures.
9. CPU tests cover handle, manager, upload, and scene material behavior.
10. Vulkan validation is clean during startup, rendering, resize, unload/reload, and shutdown.
