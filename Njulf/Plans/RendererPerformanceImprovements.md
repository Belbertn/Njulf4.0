# Renderer Performance Improvements

## Priority: Highest Impact

### 1. Blocking per-mesh GPU uploads (load time)
Each mesh registration creates a command pool, staging buffer, and fence, submits, then calls `WaitForFences` — blocking the CPU inside a lock for every submesh. For a complex scene this serializes dozens of full GPU round-trips at load time. **Fix:** batch all submesh uploads into a single submit, or queue uploads asynchronously on the dedicated transfer queue and defer fence waiting to first frame.

### 2. Meshlet build allocation churn (load time)
Three LOD meshlet sets are built per mesh using `Dictionary<int,int>`, `HashSet<int>`, and `List<int>` allocations per-meshlet, plus a greedy candidate search. For large meshes this generates thousands of short-lived heap objects. **Fix:** replace with flat-array scratch buffers reused across meshlets, or integrate meshoptimizer bindings.

### 3. Model importer vertex duplication (load time)
Every vertex attribute is written to both global and per-submesh `List`s, then both are `.ToArray()`'d — approximately 4× the working memory, plus heavy `List.Add` churn. **Fix:** pre-allocate arrays sized to `MNumVertices`/`MNumFaces`, write directly into them, and store submesh ranges into the global arrays instead of copying data.

### 4. Validation layers enabled in non-debug builds
`VulkanContext` defaults to `debug: true` and subscribes the debug messenger to Verbose and Info severity. Validation layers alone can cost 2–10× CPU overhead. **Fix:** ensure Release builds pass `debug: false`, and restrict messenger severity to Warning/Error at minimum.

---

## Priority: Per-frame Steady-State

### 5. Multiple LightManager lock round-trips per frame
`DrawScene` acquires the lock separately for `LightCount`, `DirectionalLightCount`, `LocalLightCount`, `GetLightSnapshot`, and `TryGetFirstDirectionalLight` — and the count properties do O(n) linear scans. **Fix:** add a single snapshot method that returns all required counts and the light array under one lock acquisition.

### 6. Per-frame light snapshot allocation
`GetLightSnapshot` allocates a new `Light[]` array every frame. **Fix:** cache the snapshot and invalidate only when `_needsUpload` is set, or expose a `ReadOnlySpan<Light>` view directly into `_cpuLights`.

### 7. Per-frame GPULight[] intermediate allocation in UploadToGPU
`UploadToGPU` allocates a `GPULight[]` on each upload to convert then `MemoryCopy`. **Fix:** convert each light directly into the mapped staging pointer using `Unsafe.Write` or pointer arithmetic, eliminating the intermediate array.

### 8. Per-frame local shadow array allocations
`PrepareLocalShadows` allocates `GPUSpotShadow[]`, `GPUPointShadow[]`, and `GPULocalLightShadowIndex[lightCount]` arrays every frame, even when the shadow selection is unchanged. **Fix:** apply the same `UploadState` skip-on-match pattern used elsewhere, and reuse pooled arrays.

### 9. Shadow resources re-registered every frame
`CreateDirectionalShadowData` and `PrepareLocalShadows` call `Ensure(...)` and `Register(_bindlessHeap)` on every frame, issuing descriptor writes unconditionally. **Fix:** register once at init/resize and only re-register on actual resource recreation.

### 10. Scene payload signature invalidated by camera movement
`ScenePayloadSignature` includes the view-projection matrix, so any camera movement invalidates the full payload and re-runs CPU culling and upload. **Fix:** separate the static-scene signature (object transforms, materials) from per-frame camera-dependent data so static data upload can be skipped when only the camera changes.

### 11. Directional shadow data uploaded unconditionally
`UploadShadowData` runs every frame regardless of whether the data changed. **Fix:** compare against the last uploaded value and skip if bit-identical.

### 12. `BuildDiagnostics` per-frame allocation
`BuildDiagnostics` constructs a large record with ~130 fields every frame. If `RendererDiagnostics` is a reference type, this is a sizable heap allocation per frame. **Fix:** make it a struct, or only populate/construct it when diagnostics are actually consumed (e.g., on demand or at a reduced rate).

### 13. `FenceBasedDeleter` closure allocations
`QueueBufferDeletion`, `QueueImageDeletion`, etc. each capture a closure (`Action`) per resource, allocating a delegate object per queued deletion. Additionally, empty `List<Action>` entries for completed fences are never removed from the dictionary. **Fix:** replace `Action` delegates with a struct-based pending entry (handle + type enum + fence), and prune completed fence entries.

---

## Priority: Vulkan Correctness & Efficiency

### 14. Over-broad pipeline barriers in MeshManager
Upload barriers use `DstStageMask = AllCommandsBit` with `Size = WholeSize` on all six mesh buffers. **Fix:** restrict to actual consumer stages (task/mesh/fragment/compute) and the exact written byte ranges.

### 15. Swapchain using `SharingMode.Concurrent` unnecessarily
`Concurrent` sharing is enabled whenever a dedicated transfer queue exists, but the transfer queue never accesses swapchain images. Concurrent mode disables exclusive-ownership optimizations on some drivers. **Fix:** always use `SharingMode.Exclusive` for swapchain images with explicit queue family ownership transfers if/when needed.

### 16. `RecreateSwapchain` does not pass `OldSwapchain`
The old swapchain handle is destroyed before creating the new one, forcing a full teardown instead of letting the driver recycle resources. **Fix:** pass the old `SwapchainKHR` as `OldSwapchain` in the create info, then destroy it after the new one is successfully created.

### 17. Swapchain image count logic bug
`Math.Min(MaxImageCount, 3)` returns 0 when `MaxImageCount == 0` (which means unlimited), then falls back to `MinImageCount`, losing triple buffering. **Fix:** treat `MaxImageCount == 0` as unlimited and use `Math.Max(MinImageCount, Math.Min(MaxImageCount == 0 ? 3 : MaxImageCount, 3))`.

### 18. `QueueWaitIdle` in single-time command paths
`EndSingleTimeCommands` in both `CommandBufferManager` and `VulkanContext` stalls the entire queue. Each `VulkanContext.BeginSingleTimeCommands` call also creates and destroys a transient command pool. **Fix:** use a per-submission fence to wait only as long as necessary, and keep a persistent transient pool for one-off operations.

### 19. Double `WaitIdle` on resize
`VulkanRenderer.Resize` calls `DeviceWaitIdle`, then `RecreateSwapchain` which calls `SwapchainManager.RecreateSwapchain` which calls `WaitIdle` again. **Fix:** remove the redundant `WaitIdle` call from `Resize`.

---

## Priority: Load-time Memory

### 20. AppendMeshInstance face validation loop
All faces are iterated once to validate triangle counts, then again to emit indices. **Fix:** fold the validation check into the index-emission loop as a single pass.

### 21. All six mesh buffers start at 16 MB each
The initial 16 MB allocation is applied uniformly to vertex, index, meshlet, metadata, and index buffers. Mesh metadata is tiny by comparison. **Fix:** size each buffer independently based on its expected element size and typical mesh count.

### 22. Bindless buffers re-registered on every mesh upload
`UpdateRegisteredBindlessBuffers` re-registers all six bindless buffer slots after every mesh registration, even if no buffer handle was replaced. **Fix:** track which buffer handles were actually replaced by `EnsureBufferCapacity` and only re-register those.

### 23. `AppendCpuMeshlets` uses a slow growth loop
The CPU meshlet cache is grown with a `while (...) _meshlets.Add(default)` loop. **Fix:** use `CollectionsMarshal.SetCount` or `EnsureCapacity` followed by a direct index write.

### 24. `CalculateBoundingSphere` uses `IReadOnlyList` interface dispatch
The `GetEnumerator` call on `IReadOnlyList<int>` boxes the enumerator on each call. **Fix:** pass `List<int>` directly or use a `Span<int>` overload to avoid interface dispatch and boxing.

---

## Priority: Minor / Low-hanging

### 25. `Clear()` opens a redundant dynamic rendering pass
`Clear` begins and immediately ends a dynamic rendering pass just to record clear values. **Fix:** fold the clear into the first real render pass's `LoadOp.Clear`, removing a renderpass begin/end pair per frame.

### 26. `StagingRing.Allocate` takes a lock on every allocation
In a single-threaded frame loop this lock is uncontended but adds overhead on every allocation. **Fix:** use `Interlocked.Add` for offset bumping when called from the render thread, reserving the lock for cross-thread scenarios.

### 27. `TryGetDeviceRequirements` called twice during initialization
The method runs during both physical device selection and logical device creation for the selected device. **Fix:** cache the result for the selected physical device after the selection pass.
