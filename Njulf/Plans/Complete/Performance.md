# Renderer Performance Review
## Per-frame GC allocations (hot path: `DrawScene`)
1. **`LightManager.GetLightSnapshot()`** allocates a new `Light[]` every frame. Cache the snapshot and invalidate only when `_needsUpload` flips, or expose a pooled/`ReadOnlySpan` view.
2. **`LightManager.UploadToGPU`** allocates a `GPULight[]` per upload. Convert directly into the mapped staging pointer instead of building an intermediate array.
3. **`PrepareLocalShadows`** allocates `GPUSpotShadow[]`, `GPUPointShadow[]`, and `GPULocalLightShadowIndex[lightCount]` arrays each frame — even when the shadow selection didn't change. Reuse pooled arrays and skip the upload when the data is unchanged (like `SceneDataBuilder.UploadState` already does).
4. **`BuildDiagnostics`** constructs a ~130-field `RendererDiagnostics` record every frame. If it's a record class, that's a sizable allocation per frame; make it a struct or populate only when diagnostics are consumed.
5. **`FenceBasedDeleter.QueueXxxDeletion`** captures a closure (`Action`) per resource — one allocation per queued deletion. Use a struct-based pending-deletion entry (handle + type enum) instead of delegates. Also, `ProcessCompletedFrame` leaves empty `List<Action>` entries keyed by dead fence handles in the dictionary forever.

## Redundant per-frame work
1. **Multiple `LightManager` lock round-trips per frame**: `DrawScene` calls `LightCount`, `DirectionalLightCount`, `LocalLightCount`, `GetLightSnapshot`, `TryGetFirstDirectionalLight` — each takes the lock and the count properties do O(n) scans. Take one snapshot under a single lock and derive everything from it.
2. **`CreateDirectionalShadowData` / `PrepareLocalShadows` call `Ensure(...)` + `Register(_bindlessHeap)` every frame**, issuing descriptor writes even when nothing changed. Register once at init/resize and only re-register on actual recreation.
3. **`ScenePayloadSignature.Create`** hashes every render object (matrix, refs) each frame and includes the view-projection matrix — so any camera motion invalidates the whole payload and re-runs CPU culling/upload. Separate the static scene signature from per-frame camera-dependent culling so static data isn't re-validated/rebuilt on every camera move.
4. **`_directionalShadowResources.UploadShadowData`** runs unconditionally each frame; skip when shadow data is bit-identical to last frame.

## Synchronization stalls
1. **`MeshManager.RegisterMeshInternal` blocks the CPU per mesh**: it creates a command pool, fence, staging buffer, submits, then `WaitForFences` — all inside a lock, once per mesh. Loading a model with many submeshes (e.g. Sponza) serializes dozens of full GPU round-trips. Batch all submesh uploads into one submit, or queue uploads on the dedicated transfer queue with deferred fencing.
2. **`QueueWaitIdle` in single-time command paths** (`CommandBufferManager.EndSingleTimeCommands`, `VulkanContext.EndSingleTimeCommands`, `SwapchainManager.TransitionImageLayout`) stalls the entire queue. Use a fence per submission instead, and avoid creating a fresh command pool per call (`VulkanContext.BeginSingleTimeCommands`) — keep a persistent transient pool.
3. **`VulkanRenderer.Resize` double-waits**: it calls `DeviceWaitIdle`, then `RecreateSwapchain` → `SwapchainManager.RecreateSwapchain` which calls `WaitIdle` again.

## Vulkan-specific inefficiencies
1. **Over-broad barriers**: `MeshManager.CreateUploadReadBarrier` uses `DstStageMask = AllCommandsBit` with `Size = WholeSize` on all six buffers; restrict to the actual consumer stages (task/mesh/fragment/compute) and the written ranges.
2. **Swapchain `SharingMode.Concurrent`** is enabled whenever a dedicated transfer queue exists, but the transfer queue never touches swapchain images. Concurrent sharing disables exclusive-ownership optimizations on some drivers — use `Exclusive`.
3. **`RecreateSwapchain` doesn't pass `OldSwapchain`**, forcing a full teardown/recreate instead of letting the driver recycle resources.
4. **`SwapchainManager` caps image count via `Math.Min(MaxImageCount, 3)`** — when `MaxImageCount == 0` (unlimited) this yields 0 and falls back to `MinImageCount`, losing triple buffering. Use `MinImageCount + 1` clamped properly.
5. **Validation/debug defaults**: `VulkanContext(window, debug = true)` plus a messenger subscribed to _Verbose_ and _Info_ severities. Ensure Release builds construct with `debug: false`; validation layers alone can cost 2–10x CPU.

## Mesh import / meshlet build (load-time CPU & memory)
1. **`MeshManager.GenerateMeshlets` builds three full LOD meshlet sets per mesh** with `Dictionary<int,int>`, `HashSet<int>`, and `List<int>` allocations per meshlet, plus a quadratic-ish greedy candidate search. Replace with flat-array based building (or meshoptimizer bindings); reuse scratch collections across meshlets.
2. **`ModelImporter` duplicates everything**: each vertex/index is appended to both global and per-submesh `List`s, then both are `ToArray()`'d — ~4x the data in memory plus heavy `List.Add` churn. Pre-size lists with `MNumVertices`/`MNumFaces` capacity, write into arrays directly, and consider storing only submesh ranges into the global arrays instead of copying.
3. **`ModelImporter.AppendMeshInstance`** iterates all faces once just to validate triangle counts, then again to emit indices — fold into one pass.
4. **`MeshManager` creates six 16 MB device buffers up front** (`InitialBufferSize`) — including for mesh metadata, which is tiny. Size each buffer by its expected stride/count.
5. **`UpdateRegisteredBindlessBuffers` re-registers all six bindless buffers after every mesh registration**, even if no buffer handle changed. Only re-register buffers that were actually replaced by `EnsureBufferCapacity`.
6. **`CalculateBoundingSphere`** does an awkward manual `GetEnumerator()` plus two full passes; one pass for min/max and one for radius is unavoidable, but the enumerator boxing/IReadOnlyList interface dispatch can be removed by passing `List<int>` or a span.

## Minor
1. **`Clear(Color)` opens and immediately ends a dynamic rendering pass just to clear** — fold the clear into the first real pass's `LoadOp.Clear` to avoid an extra renderpass begin/end.
2. **`StagingRing.Allocate` takes a lock per allocation**; in a single-threaded frame this is uncontended but still adds overhead — consider `Interlocked` offset bumping.
3. **`MeshManager.AppendCpuMeshlets`** grows `_meshlets` with a `while (...) _meshlets.Add(default)` loop — use `CollectionsMarshal.SetCount` / `EnsureCapacity`.
4. **`VulkanContext.TryGetDeviceRequirements` runs twice** (device pick + logical device creation) — cache the result for the selected device.

## Highest-impact items first
If you prioritize: **#10 (blocking per-mesh uploads)** and **#18–19 (meshlet/import allocation churn)** dominate load time; **#1–3, #6–7 (per-frame allocations and redundant descriptor/lock work)** and **#17 (validation in Release)** dominate steady-state frame cost.