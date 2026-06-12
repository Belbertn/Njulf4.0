# Renderer Performance Focused Remediation Plan

This plan keeps the highest-confidence, highest-impact items from `Plans/Performance.md`.
`RenderSettings.cs` is not itself a performance problem; the relevant work is in the renderer hot path, mesh upload path, mesh import path, and Vulkan resource lifecycle.

## Goals

1. Remove avoidable per-frame managed allocations from `VulkanRenderer.DrawScene`.
2. Remove load-time GPU stalls caused by one-submit-per-mesh uploads.
3. Reduce model import and meshlet build memory churn.
4. Avoid redundant descriptor writes, shadow uploads, and scene payload rebuilds.
5. Fix Vulkan swapchain and single-time command inefficiencies that can create stalls.

## Phase 1: Light and Shadow Hot Path

### 1. Add a single light frame snapshot

Files:
- `Njulf.Rendering/Resources/LightManager.cs`
- `Njulf.Rendering/VulkanRenderer.cs`

Current issue:
- `DrawScene` asks `LightManager` separately for `LightCount`, `DirectionalLightCount`, `LocalLightCount`, `GetLightSnapshot`, and shadow-casting directional light data.
- This causes several lock acquisitions, O(n) count scans, and a fresh `Light[]` allocation each frame.

Plan:
1. Add a `LightFrameSnapshot` value type containing:
   - `ReadOnlyMemory<Light>` or a cached `Light[]` plus `Count`
   - `DirectionalLightCount`
   - `LocalLightCount`
   - first shadow-casting directional light index and value
   - revision number
2. Maintain a cached snapshot in `LightManager`.
3. Invalidate the snapshot when lights are added, removed, updated, or cleared.
4. Replace the separate `DrawScene` calls with one snapshot call.

Acceptance criteria:
- `DrawScene` performs one `LightManager` snapshot operation.
- No per-frame `Light[]` allocation when lights are unchanged.
- Local shadow selection receives the same light data as before.

### 2. Remove `GPULight[]` upload allocation

Files:
- `Njulf.Rendering/Resources/LightManager.cs`

Current issue:
- `UploadToGPU` allocates a `GPULight[]` whenever lights need upload, then copies it to the mapped staging buffer.

Plan:
1. Convert each `Light` directly into the mapped staging pointer.
2. Keep the same staging allocation, flush, copy, and barrier behavior.
3. Add a focused test or diagnostic assertion around `LastUploadBytes` behavior.

Acceptance criteria:
- Light upload does not allocate a managed `GPULight[]`.
- Uploaded GPU light data is byte-equivalent to the old path.

### 3. Reuse and skip local shadow uploads

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Data/LocalShadowDataBuilder.cs`
- `Njulf.Rendering/Resources/SpotShadowAtlas.cs`
- `Njulf.Rendering/Resources/PointShadowCubemapArray.cs`

Current issue:
- `PrepareLocalShadows` builds new `GPUSpotShadow[]`, `GPUPointShadow[]`, and `GPULocalLightShadowIndex[]` arrays every frame.
- Shadow buffers are uploaded even when the selected lights and settings are unchanged.

Plan:
1. Introduce reusable shadow scratch arrays or pooled arrays owned by the renderer.
2. Add stable signatures for selected spot lights, selected point lights, shadow settings, and `lightCount`.
3. Skip spot, point, and index buffer uploads when the corresponding signature is unchanged.
4. Keep scene diagnostics accurate even when upload is skipped.

Acceptance criteria:
- No local shadow data arrays are allocated on unchanged frames.
- Local shadow buffers are uploaded only when selection, light data, or relevant settings change.

### 4. Avoid redundant shadow descriptor registration and data upload

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Resources/DirectionalShadowResources.cs`
- `Njulf.Rendering/Resources/SpotShadowAtlas.cs`
- `Njulf.Rendering/Resources/PointShadowCubemapArray.cs`

Current issue:
- Shadow resources call `Register(_bindlessHeap)` every frame.
- Directional shadow data is uploaded every frame.

Plan:
1. Make `Ensure(...)` return whether resources were recreated.
2. Register shadow resources only after creation, resize, or actual recreation.
3. Cache the last uploaded `GPUShadowData` and skip upload when unchanged.

Acceptance criteria:
- Per-frame bindless descriptor writes for shadow resources are gone.
- Directional shadow data upload is skipped for bit-identical data.

## Phase 2: Scene Payload Caching

### 5. Split static scene signatures from camera-dependent work

Files:
- `Njulf.Rendering/Data/SceneDataBuilder.cs`

Current issue:
- `ScenePayloadSignature` includes the view-projection matrix, so any camera movement invalidates the cached payload.
- The call to `ScenePayloadSignature.Create(...)` currently omits the `buildLocalShadowMeshlets` argument, even though the signature type supports it. This can make cached local-shadow meshlet payloads stale when local shadow rendering toggles.

Plan:
1. Fix the existing signature call to include `buildLocalShadowMeshlets`.
2. Split the signature into:
   - static payload signature: render objects, visibility flags, transforms, mesh handles, material handles, material revision, local-shadow meshlet mode
   - camera/shadow culling signature: view-projection and directional shadow matrices
3. Rebuild static object data only when the static signature changes.
4. Keep per-frame culling outputs separate so camera movement does not force static buffer re-upload.

Acceptance criteria:
- Camera-only movement no longer causes static object/instance payload rebuilds.
- Local shadow meshlet cache invalidates correctly when `buildLocalShadowMeshlets` changes.
- Existing diagnostics still report rebuilds and upload skips accurately.

## Phase 3: Mesh Upload Stalls

### 6. Batch mesh uploads per imported model

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`

Current issue:
- `RegisterMeshInternal` creates a command pool, staging buffer, fence, submit, and `WaitForFences` per mesh while holding the mesh-manager lock.
- Multi-submesh model load serializes many CPU/GPU round-trips.

Plan:
1. Add a batch registration API that accepts all submesh vertex/index payloads for one imported model.
2. Precompute total required GPU buffer growth and one staging allocation for the batch.
3. Record all buffer copies into a single command buffer.
4. Submit once and wait once, or defer the fence until the model is needed for rendering.
5. Keep the existing single-mesh API as a wrapper over the batch path.

Acceptance criteria:
- A model with N submeshes performs one upload submit instead of N submits.
- The mesh-manager lock is not held across repeated fence waits.
- Existing model upload tests continue to pass.

### 7. Narrow mesh upload barriers and bindless re-registration

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`

Current issue:
- Upload barriers cover all six mesh buffers with `AllCommandsBit` and `WholeSize`.
- `UpdateRegisteredBindlessBuffers` re-registers all six bindless slots after every mesh registration, even if no buffer handle changed.

Plan:
1. Track written ranges for each buffer during upload.
2. Emit barriers only for buffers and byte ranges written in the current upload.
3. Use consumer stages required by the mesh, task, fragment, and compute shader paths instead of `AllCommandsBit`.
4. Track which buffer handles changed in `EnsureBufferCapacity`.
5. Re-register only changed bindless buffer slots.

Acceptance criteria:
- Mesh upload barriers use exact written ranges.
- Bindless descriptor updates happen only for buffers whose handles changed.

## Phase 4: Import and Meshlet Memory

### 8. Reduce `ModelImporter` duplication and passes

Files:
- `Njulf.Assets/ModelImporter.cs`
- `Njulf.Core/Scene/Model.cs`
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`

Current issue:
- Imported vertices and indices are stored in global lists and copied again into per-submesh lists, then both are converted with `ToArray()`.
- Face validation is a separate pass before index emission.

Plan:
1. Pre-size global lists from Assimp scene totals.
2. Pre-size per-submesh collections from `MNumVertices` and `MNumFaces * 3`.
3. Fold triangle validation into the index emission loop.
4. Evaluate replacing duplicated per-submesh arrays with submesh ranges into global arrays.
5. If range-based submeshes are introduced, update `ModelRenderUploadService` to consume ranges without copying.

Acceptance criteria:
- Import does not perform a separate face-validation pass.
- Large model import produces fewer temporary arrays and list reallocations.
- Existing model material and submesh upload behavior is preserved.

### 9. Rework meshlet builder scratch allocation

Files:
- `Njulf.Rendering/Resources/MeshManager.cs`

Current issue:
- `GenerateMeshlets` builds three LOD meshlet sets per mesh.
- It allocates `Dictionary<int,int>`, `HashSet<int>`, and multiple `List<int>` instances per meshlet.

Plan:
1. Introduce reusable scratch state for meshlet generation.
2. Replace per-meshlet dictionary/set allocations with flat arrays and generation stamps where practical.
3. Keep the current greedy behavior initially to reduce risk.
4. Add a later optional task to evaluate meshoptimizer bindings if quality or build time remains poor.
5. Replace `AppendCpuMeshlets` growth loop with `EnsureCapacity` plus direct count growth.
6. Change `CalculateBoundingSphere` to consume `List<int>` or span-like data directly instead of `IReadOnlyList<int>`.

Acceptance criteria:
- Meshlet generation allocates scratch once per mesh or per build, not once per meshlet.
- Generated meshlet counts and bounds remain compatible with existing tests.

## Phase 5: Vulkan Swapchain and One-Time Commands

### 10. Fix swapchain image count, sharing mode, and old swapchain use

Files:
- `Njulf.Rendering/Core/SwapchainManager.cs`

Current issue:
- `MaxImageCount == 0` means unlimited, but current logic treats it as zero and can lose triple buffering.
- Swapchain images use `SharingMode.Concurrent` when a dedicated transfer queue exists, although transfer does not use swapchain images.
- `OldSwapchain` is not passed during recreation.

Plan:
1. Choose desired image count as `MinImageCount + 1`, preferring 3 when supported.
2. Treat `MaxImageCount == 0` as unlimited.
3. Keep swapchain images in `SharingMode.Exclusive`.
4. During recreation, pass the old swapchain handle to `OldSwapchain`.
5. Destroy the old swapchain only after the new swapchain and image views are created successfully.

Acceptance criteria:
- Triple buffering is selected when supported.
- Swapchain images use exclusive sharing.
- Swapchain recreation passes the previous handle through `OldSwapchain`.

### 11. Replace queue-wide waits in single-time commands

Files:
- `Njulf.Rendering/Core/CommandBufferManager.cs`
- `Njulf.Rendering/Core/VulkanContext.cs`
- `Njulf.Rendering/Core/SwapchainManager.cs`
- `Njulf.Rendering/Resources/TextureManager.cs`

Current issue:
- `EndSingleTimeCommands` uses `QueueWaitIdle`, stalling the whole graphics queue.
- `VulkanContext.BeginSingleTimeCommands` creates and destroys a command pool per call.

Plan:
1. Use a fence per single-time submit instead of `QueueWaitIdle`.
2. Add a persistent transient command pool for `VulkanContext` single-time commands.
3. Keep cleanup explicit and deterministic.
4. Recheck texture upload and image layout transition call sites for lifetime assumptions.

Acceptance criteria:
- Single-time command submission no longer waits for the full queue to idle.
- No per-call command pool creation in the common single-time path.

### 12. Remove resize double wait

Files:
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Rendering/Core/SwapchainManager.cs`

Current issue:
- `VulkanRenderer.Resize` calls `DeviceWaitIdle`, then `SwapchainManager.RecreateSwapchain` calls `WaitIdle` again.

Plan:
1. Keep one wait at the swapchain recreation boundary.
2. Remove the redundant wait from `VulkanRenderer.Resize`, or make `RecreateSwapchain` accept a flag if some callers already waited.

Acceptance criteria:
- Resize performs one device idle wait, not two.

## Deferred or Lower Priority

These recommendations are valid but not first-pass priorities:

1. `RendererDiagnostics` as a struct or demand-built object.
   - It is a real per-frame allocation, but changing a very wide record has broad call-site impact. Do this after hot path light/shadow allocation work.
2. `FenceBasedDeleter` struct pending entries.
   - Delegate allocations are real, and completed one-shot fences should be removed from the dictionary. Prioritize if resource churn is visible in profiles.
3. `Clear(Color)` load-op integration.
   - Low risk but likely small impact unless `Clear` is called every frame before real rendering.
4. `StagingRing.Allocate` lock removal.
   - Do not replace this mechanically with `Interlocked`; frame reset and multi-threaded allocation semantics need a deliberate design.
5. Validation defaults.
   - Dependency injection already sets validation from build configuration (`true` in DEBUG, `false` in RELEASE). The direct `new VulkanContext(window)` default can be hardened later, but it is not a confirmed Release-path problem.

## Verification Checklist

1. Add allocation measurements around `DrawScene` before and after Phase 1.
2. Track model load submit count and total load time before and after Phase 3.
3. Run renderer diagnostics tests after each phase.
4. Run model import/upload tests after Phases 3 and 4.
5. Test swapchain recreation by resizing repeatedly after Phase 5.
6. Use Vulkan validation after barrier and swapchain changes.

## Suggested Implementation Order

1. Phase 1: light/shadow hot path allocations and descriptor skips.
2. Phase 2: scene payload cache correctness and camera-movement invalidation.
3. Phase 3: batched mesh upload and narrower mesh upload barriers.
4. Phase 4: importer and meshlet scratch memory reductions.
5. Phase 5: swapchain and single-time command stall fixes.
