# Refactoring Backlog

Generated: 2026-06-09

Scope reviewed:
- `NjulfHelloGame/Program.cs`
- Core game loop, scene, and model ownership
- Asset loading and meshlet generation
- Rendering service registration, render graph, and selected resource managers

Validation performed:
- `dotnet build .\Njulf.sln --no-restore`: succeeded with 9 nullable warnings.
- `dotnet test .\Njulf.sln --no-build --verbosity minimal`: passed, 50 tests.

## Priority 0 - Correctness And Safety

### Fix `MeshletBuilder` local/global triangle index mixing

Evidence:
- `Njulf.Assets/MeshletBuilder.cs:102` stores global triangle indices in `chunkTriangles`.
- `Njulf.Assets/MeshletBuilder.cs:128` stores `chunkTriangles.Count - 1` in adjacency lists.
- `Njulf.Assets/MeshletBuilder.cs:137-139` iterates global triangle indices but indexes `usedTriangles` with them.
- `Njulf.Assets/MeshletBuilder.cs:170`, `249`, and `285` then treat local adjacency indices as if they were global triangle indices.

Impact:
- Large meshes or chunked meshes can throw `IndexOutOfRangeException`.
- Meshlets can be built from the wrong triangles.
- The renderer also has its own meshlet generation path, so this may be under-tested and silently stale.

Refactor:
- Introduce a small `ChunkTriangle` structure with explicit `GlobalTriangleIndex` and `LocalTriangleIndex`.
- Make adjacency lists store local IDs only, then resolve to global IDs exactly at index-buffer access sites.
- Add tests for meshes over `MaxVerticesPerChunk`, cross-chunk triangles, and sparse index ranges.

### Resolve nullable warnings in asset meshlet types

Evidence:
- Build warning at `Njulf.Assets/Meshlet.cs:74`: non-nullable `Name` is not initialized.
- Build warnings at `Njulf.Assets/MeshletBuilder.cs:58` and `Njulf.Assets/MeshletBuilder.cs:66`: nullable arrays are passed into non-nullable parameters.
- `BuildChunkMeshlets` takes `normals`, `tangents`, `bitangents`, and `texCoords`, but the body does not use them.

Impact:
- Nullability warnings hide real issues.
- The unused parameters make the builder look more complete than it is.

Refactor:
- Initialize `MeshletMesh.Name` to `"Unnamed"` or make it `required`.
- Remove the unused optional vertex attribute parameters from `BuildChunkMeshlets`, or implement vertex-attribute-aware meshlet output.
- Enable warnings-as-errors for projects once these are fixed.

### Fix scene ownership and clearing semantics

Evidence:
- `Njulf.Core/Scene/Scene.cs:77-80` clears render/update lists but leaves `_disposables` intact.
- `Njulf.Core/Scene/Scene.cs:93-99` later disposes everything still in `_disposables`.
- Adding the same disposable through multiple paths can also duplicate ownership.

Impact:
- `Scene.Clear()` does not clearly mean "remove only" or "remove and dispose".
- Cleared objects can be disposed later by a scene that no longer contains them.
- Duplicate registration can cause double-dispose.

Refactor:
- Split APIs into `Clear()` and `ClearAndDispose()` or make `Clear()` consistently dispose owned objects.
- Replace `_disposables` list with ownership metadata or a `HashSet<IDisposable>`.
- Add tests for add/remove/clear/dispose interactions.

### Rework finalizers on Vulkan/managed resource owners

Evidence:
- `Njulf.Rendering/Pipeline/RenderGraph.cs:66-68`, `RenderPassBase.cs:87-89`, `BindlessHeap.cs:385-387`, and several managers have finalizers.
- `Njulf.Assets/ContentManager.cs:142-144` has a finalizer that calls `Dispose()`.
- Many finalizers call into managed dependencies or Vulkan context-owned handles.

Impact:
- Finalizers can run after dependency objects have already been disposed.
- Finalizers should not generally call complex managed object graphs.
- Vulkan destruction order should stay deterministic.

Refactor:
- Remove finalizers from managed-only containers.
- For Vulkan handles, use deterministic `Dispose` with clear ownership boundaries, or introduce `SafeHandle`-style wrappers if finalization is required.
- Ensure DI-created objects are disposed exactly once by the owning service provider or renderer, not both.

## Priority 1 - Remove Workarounds And Transitional Code

### Remove obsolete game-loop shims

Evidence:
- `Njulf.Core/Game.cs:113-126` contains obsolete `RunMainLoop`, `UpdateFrame`, and `DrawFrame`.
- References search found no production callers except the current event-driven loop.

Impact:
- They preserve an old architecture and make the game-loop contract harder to reason about.
- `UpdateFrame` hardcodes `1f / 60f`, which conflicts with the Silk.NET event delta path.

Refactor:
- Delete the obsolete methods if no external API compatibility is required.
- If compatibility matters, move them behind a legacy adapter package or keep them only until the next public breaking-change boundary.

### Remove no-op bindless static-buffer registration

Evidence:
- `Njulf.Rendering/Descriptors/BindlessHeap.cs:347-351` defines `RegisterStaticBuffers()` as an empty method.
- Direct buffer registration is already performed by `SceneDataBuilder` and `MeshManager`.

Impact:
- The method suggests there is a required initialization step when there is not.
- Future callers may rely on a no-op and miss real descriptor registration.

Refactor:
- Delete `RegisterStaticBuffers()`.
- Keep only explicit `RegisterStorageBuffer(...)` calls with validation.

### Replace commented-out sample lighting with explicit presets

Evidence:
- `NjulfHelloGame/Program.cs:317-340` contains three commented point lights.
- `NjulfHelloGame/Program.cs:342-349` keeps only one directional light active.

Impact:
- Commented code is stale configuration, not maintainable runtime behavior.
- It hides whether Forward+ point-light rendering is intentionally disabled or temporarily bypassed.

Refactor:
- Replace the commented block with a `SampleLightingMode` enum or a small `AddDemoPointLights(...)` helper.
- If point lights are not wanted, delete the commented block and cover point lights in tests or a separate sample.

### Remove DI window/input replacement workaround

Evidence:
- `Njulf.Core/Game.cs:80-88` registers `_window` and `_inputContext`, calls `ConfigureServices`, then removes and re-adds them.

Impact:
- This is an ordering workaround that makes service registration surprising.
- Extension methods can register services against one window instance and then have that registration replaced.

Refactor:
- Establish a single composition rule: core owns `IWindow` and `IInputContext`, and extension methods should use `TryAddSingleton` or consume existing registrations.
- Remove the post-configuration `RemoveAll` block once extensions follow that contract.

## Priority 2 - Simplify Unnecessary Complexity

### Split `NjulfHelloGame.Program` into sample systems

Evidence:
- `NjulfHelloGame/Program.cs` contains app startup, DI configuration, asset validation, input mapping, model upload validation, diagnostics, lighting, camera movement, and animation.
- Sample-specific checks include `ValidateSampleFiles()` at `Program.cs:150`, `ValidateUploadedModel()` at `Program.cs:206`, and diagnostics at `Program.cs:246` and `Program.cs:289`.

Impact:
- `Program.cs` is acting as an integration test, demo scene, diagnostics reporter, and game implementation.
- The sample is difficult to evolve without touching unrelated concerns.

Refactor:
- Keep `Program.Main` and `HelloGame` minimal.
- Extract:
  - `SampleAssetManifest`
  - `SampleSceneLoader`
  - `SampleInputController`
  - `SampleDiagnosticsReporter`
  - `SampleLighting`

### Remove hardcoded sample asset manifest from runtime startup

Evidence:
- `NjulfHelloGame/Program.cs:45-52` hardcodes the glTF file, bin file, and three texture filenames.
- `ValidateSampleFiles()` at `Program.cs:150-158` duplicates asset dependency knowledge already present in the glTF and importer.

Impact:
- Renaming or changing the sample model requires edits in multiple places.
- Runtime startup is coupled to one exact asset package.

Refactor:
- Let the importer report missing glTF dependencies with contextual paths.
- If a preflight check is desired, generate it from the glTF manifest rather than maintaining it by hand.

### Relax sample-specific material validation

Evidence:
- `NjulfHelloGame/Program.cs:224-226` requires imported base-color, normal, and ARM textures for every render object.
- `NjulfHelloGame/Program.cs:228-232` separately checks emissive texture index validity.

Impact:
- This is valuable as an integration assertion but too strict for a general sample application.
- It prevents using simpler models that correctly rely on default material textures.

Refactor:
- Move strict material assertions into tests.
- In the sample, log material diagnostics and render with defaults when valid fallback textures are used.

### Remove unused `ContentManager` type cache

Evidence:
- `Njulf.Assets/ContentManager.cs:13` defines `_typeCache`.
- `ContentManager.cs:45-46` writes the first loaded object per type.
- `ContentManager.cs:106-107` removes from it during unload.
- No read path uses `_typeCache` to serve content.

Impact:
- It is dead state with no behavior.
- It makes cache invalidation look more complex than it is.

Refactor:
- Delete `_typeCache`.
- Keep the path/type cache only, or introduce a real typed query API if needed.

### Tighten `ContentManager.Unload`

Evidence:
- `Njulf.Assets/ContentManager.cs:93-110` removes from `_cache` while enumerating it.

Impact:
- It works only because the method breaks immediately after removal.
- The pattern is brittle and easy to break during future edits.

Refactor:
- First find the cache key, then remove outside the enumeration.
- Add an unload test for cached assets.

### Clarify `RenderObject` responsibilities

Evidence:
- `Njulf.Core/Scene/RenderObject.cs:92-101` has no-op `Draw()` and `Update(...)`.
- `RenderObject.cs:104-111` exposes `GetWorldMatrix()` cache, but renderer-facing code primarily uses `WorldMatrix`.
- `Mesh` and `Material` are `object?`, forcing downstream type checks like `Program.cs:217-220`.

Impact:
- The object mixes scene transform, update behavior, renderability, and weakly typed GPU handles.
- Dirty caching adds complexity without clear benefit.

Refactor:
- Split data-only render instances from behavior/update components.
- Replace `object? Mesh` and `object? Material` with typed handles or a generic abstraction.
- Remove no-op interface implementations unless there is a concrete caller.

### Sort updateables before update, not after

Evidence:
- `Njulf.Core/Scene/Scene.cs:83-90` updates all objects and then sorts them.

Impact:
- `UpdateOrder` changes take effect one frame late.
- Sorting every frame is unnecessary when order rarely changes.

Refactor:
- Sort before updating when dirty.
- Mark order dirty when objects are added, removed, or `UpdateOrder` changes.

### Avoid per-call `AsReadOnly()` wrapper allocation

Evidence:
- `Njulf.Core/Scene/Scene.cs:17-18` returns `_renderObjects.AsReadOnly()` and `_updateables.AsReadOnly()`.
- `Njulf.Core/Scene/Model.cs:16` returns `_renderObjects.AsReadOnly()`.

Impact:
- These properties allocate wrappers when called.
- Rendering code can call scene/model object lists frequently.

Refactor:
- Store cached `ReadOnlyCollection<T>` wrappers, or expose `IReadOnlyList<T>` directly through a backing field initialized once.

## Priority 3 - Performance And Diagnostics

### Replace direct console logging with structured logging

Evidence:
- `NjulfHelloGame/Program.cs:284` and `Program.cs:299` write sample diagnostics directly.
- `Njulf.Rendering/VulkanRenderer.cs`, `VulkanContext.cs`, managers, render graph, and memory classes contain many `Console.WriteLine(...)` calls.

Impact:
- Logs cannot be filtered by category or severity.
- Render/resource code writes to stdout even when embedded in another application.
- Test output and sample output are coupled to engine internals.

Refactor:
- Introduce a small logging abstraction or use `Microsoft.Extensions.Logging`.
- Gate verbose lifecycle logs behind debug/trace level.
- Keep sample diagnostics in a sample-specific reporter.

### Reduce `Program.cs` one-off LINQ allocations

Evidence:
- `NjulfHelloGame/Program.cs:258-274` builds arrays and enumerables to count distinct material handles and dynamic textures.
- This is one-time sample code, so it is not urgent.

Impact:
- Low runtime impact today.
- It contributes to the broader pattern of diagnostics work being mixed into the sample startup path.

Refactor:
- Move diagnostics into `SampleDiagnosticsReporter`.
- Use pooled or stack-based fixed collection logic only if this becomes frame-time code.

### Simplify model transform calculation

Evidence:
- `NjulfHelloGame/Program.cs:201-203` multiplies scale, rotation, and `CreateTranslation(CoreVector3.Zero)`.

Impact:
- The translation multiply is currently a no-op.

Refactor:
- Remove the zero translation, or introduce a named `SampleModelPosition` constant if position will become configurable.

### Avoid synchronous upload stalls where possible

Evidence:
- `Njulf.Rendering/Resources/MeshManager.cs:606-691` creates upload command resources and waits on an upload fence with `ulong.MaxValue`.
- `Njulf.Rendering/VulkanRenderer.cs:478` and `VulkanRenderer.cs:621` use `DeviceWaitIdle` for resize/dispose.
- `SceneDataBuilder.cs:446-463` waits for other in-flight frame fences when growing buffers.

Impact:
- Some waits are necessary for resize/dispose and growth, but upload paths can become visible stalls as content grows.
- Per-upload command pool/fence creation adds overhead.

Refactor:
- Reuse upload command pools.
- Retire old buffers through `FenceBasedDeleter`.
- Batch mesh/texture uploads and avoid blocking the render thread where practical.

### Refactor meshlet generation duplication

Evidence:
- `Njulf.Assets/MeshletBuilder.cs` contains one meshlet builder.
- `Njulf.Rendering/Resources/MeshManager.cs:794-863` contains another meshlet generation path.

Impact:
- Bugs fixed in one path may remain in the other.
- Tests may cover renderer meshlet generation while asset meshlet generation decays.

Refactor:
- Move meshlet generation into one shared, tested service.
- Keep renderer-specific upload conversion separate from topology construction.

### Optimize meshlet builder hot paths

Evidence:
- `Njulf.Assets/MeshletBuilder.cs:163-176` creates a candidate list and repeatedly uses `Contains`.
- `MeshletBuilder.cs:184` creates a new list per candidate triangle.
- `MeshletBuilder.cs:211-224` computes a bounding sphere with nested loops over seed vertices.
- `MeshletBuilder.cs:269-283` updates all accumulated meshlets after each chunk call.

Impact:
- Mesh import time scales poorly for large models.
- Offset recalculation across all meshlets per chunk is unnecessary and may be wrong after chunking.

Refactor:
- Use boolean marks or pooled sets for candidate membership.
- Compute bounding spheres using centroid plus max distance, or a better bounded approximation, without pairwise distance checks.
- Track per-chunk offset bases and update only newly added meshlets.

### Consolidate render-pass boilerplate

Evidence:
- `DepthPrePass`, `ForwardPlusPass`, and `TiledLightCullingPass` repeat viewport/scissor setup, descriptor binding, push-constant setup, and no-op lifecycle methods.
- `Njulf.Rendering/Pipeline/RenderPassBase.cs:60-69` has default no-op hooks.

Impact:
- Render passes are verbose and harder to audit for synchronization/layout differences.
- Small fixes must be applied in multiple places.

Refactor:
- Add helpers for descriptor-set binding and viewport/scissor setup.
- Keep pass-specific code focused on attachment transitions, push constants, and draw/dispatch calls.

## Program.cs Specific Next Refactor

Recommended extraction order:
1. Create `SampleAssetManifest` for model path, scale, and optional asset preflight.
2. Create `SampleInputController` to own input actions and camera movement.
3. Create `SampleSceneLoader` to load the model, add render objects, and configure scene ambient light.
4. Create `SampleDiagnosticsReporter` and move `PrintModelSummary` and `PrintFirstFrameDiagnostics`.
5. Create `SampleLighting` and replace commented lights with explicit presets.

Expected result:
- `Program.cs` contains only startup and high-level sample composition.
- Asset/material validation moves to tests or dedicated diagnostics.
- The game sample becomes easier to swap to another model without changing validation internals.

## Verification Checklist For The Refactor

- Keep `dotnet build .\Njulf.sln --no-restore` clean.
- Make nullable warnings zero before enabling warnings-as-errors.
- Keep `dotnet test .\Njulf.sln --no-build --verbosity minimal` passing.
- Add meshlet chunking tests before changing meshlet generation.
- Run the sample manually after `Program.cs` extraction because window, input, Vulkan swapchain, and GPU upload behavior are not fully covered by unit tests.
