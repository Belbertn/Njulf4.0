# Environment and Reflection-Probe Descriptor Repair Implementation Plan

Last updated: 2026-08-24

Design reference: reverted commit `65c3dfb97bfd3bee2c53b4c55d8dac743ceac8c5`. Reimplement the repair against the current tree; do not cherry-pick the commit because its renderer changes are coupled to unrelated reflection, transmission, fog, and startup work.

## 1. Required outcome

Runtime environment changes and swapchain recreation must leave every fixed environment and reflection-probe descriptor backed by a live, type-compatible image view.

The final descriptor precedence is:

1. A live local reflection-probe cubemap array owns `ReflectionProbeCubemapArrayTexture` and `ReflectionProbeDebugTexture`.
2. If no local array is available, the current global prefiltered environment owns those fallback slots.
3. Procedural-sky mode still publishes valid one-pixel source cubemaps at `EnvironmentCubemapTexture` and `IrradianceCubemapTexture`, even though normal analytic lighting uses coefficients rather than those textures.

The repair must not add steady-state per-frame Vulkan descriptor writes, invalidate captured probes, or change reflection-source energy ownership.

## 2. Verified current defect

### 2.1 Procedural sky can leave stale source descriptors

`EnvironmentManager.RecreateResources` begins by destroying all current environment textures. The HDR path recreates environment, irradiance, prefiltered, and BRDF resources. The procedural path currently recreates only the two prefiltered ping-pong cubemaps and BRDF LUT.

`TextureManager.DestroyTexture` does not clear fixed bindless indices. Consequently, a live switch from HDR to procedural sky can leave `EnvironmentCubemapTexture` and `IrradianceCubemapTexture` referring to destroyed HDR image views. Most analytic lighting branches avoid these slots, but `tonemap_composite.frag` directly samples the fixed irradiance slot for its environment debug view, and statically present shader paths should never rely on dead descriptors.

The current resource diagnostics also describe the requested source kind instead of the resources actually created after an HDR-load failure falls back to the analytic sky.

### 2.2 Environment fallback publication can displace local probes

`EnvironmentManager.RegisterReflectionProbeFallback` writes the global prefiltered cubemap into the two fixed reflection-probe slots. A live environment rebuild invokes that method after creating its new textures.

`ReflectionProbeManager.Register` republishes its local cubemap views only when `_descriptorDirty` is true. An environment-only rebuild does not dirty the probe manager, so an already-published local array does not reclaim the slots. `PrepareReflectionProbes` calls `Register`, but the clean descriptor state makes that call a no-op. The same ordering exists in the swapchain-recreation path: fallback first, ordinary non-forced probe registration second.

The result is metadata that still reports captured local probes while the shader-visible cubemap-array index refers to the global fallback. In addition to wrong reflections, that can pair a `samplerCubeArray` shader access with a non-array fallback view.

### 2.3 Existing synchronization boundary

The live environment rebuild already performs and records a device-idle wait before destroying resources. The repair should preserve that boundary and complete all replacement descriptor publication before the next primary graphics command buffer begins. This plan does not broaden the change into asynchronous environment-resource retirement.

## 3. Scope

### In scope

- Valid procedural source fallback cubemaps for the fixed environment and irradiance slots.
- Type-compatible reflection fallback views.
- An explicit environment-resource-recreated result.
- Forced local-probe descriptor restoration after any fallback publication that can displace it.
- Correct accounting for the actual HDR, procedural, and failed-HDR-fallback resource sets.
- Focused contract/unit tests and Vulkan validation smoke coverage.

### Out of scope

- Changing environment shading, procedural-sky coefficients, prefilter generation, or reflection blending.
- Recapturing probes solely because descriptor bindings were repaired. Existing lighting-version logic remains authoritative for recapture.
- Replacing the current device-idle environment rebuild with deferred destruction.
- Restoring automatic reflections or any other feature from `65c3dfb`.
- Making the procedural irradiance debug view visualize analytic SH; the safe source fallback may render black in that debug mode.

## 4. Non-negotiable invariants

1. No descriptor used by a submitted frame may refer to an image view destroyed by an environment transition.
2. Local probe descriptors always win when both local probes and a global fallback exist.
3. The two local slots move together: never publish the local array slot without its matching debug cube view, or clear `_descriptorDirty` after a partial pair.
4. `ReflectionProbeCubemapArrayTexture` receives a `TypeCubeArray` view; `ReflectionProbeDebugTexture`, `EnvironmentCubemapTexture`, and `IrradianceCubemapTexture` receive `TypeCube` views.
5. If no local cubemap array exists, both reflection slots remain valid through the global fallback.
6. An unchanged environment signature returns “not recreated” and causes no forced probe-descriptor write.
7. Forced restoration occurs only at explicit descriptor-displacement boundaries. Stable frames continue using `_descriptorDirty` and `BindlessHeap` publication deduplication.
8. Resource counts and byte totals use `_usesAnalyticSky`, not only the requested `SourceKind`, so a failed HDR load reports the resources that actually exist.

## 5. Chosen implementation

### 5.1 Publish valid analytic source textures

In `Njulf.Rendering/Resources/EnvironmentManager.cs`:

- Add a helper that creates two 1x1 cubemaps when `useAnalyticSky` is true:
  - RGB = 0 and alpha = 1 for all six faces.
  - Use the active environment precision (`R16G16B16A16Sfloat` or `R32G32B32A32Sfloat`).
  - Register them at `BindlessIndex.EnvironmentCubemapTexture` and `BindlessIndex.IrradianceCubemapTexture` during creation.
  - Give them unambiguous debug names such as `Procedural Environment Source Fallback` and `Procedural Irradiance Source Fallback`.
- Store them in the existing `_environmentCubemap` and `_irradianceCubemap` handles so `GetSampledTextureHandles`, destruction, and ownership-transfer planning automatically include them.
- Create and own a `TypeCubeArray` non-owning view over the current fallback prefiltered cubemap for `ReflectionProbeCubemapArrayTexture`. Continue using the normal `TypeCube` view for `ReflectionProbeDebugTexture`.
- Destroy the extra cube-array view before destroying its underlying prefiltered texture in `DestroyEnvironmentTextures`.

Black is intentional: these images are descriptor-validity fallbacks, not a second implementation of analytic lighting. Main environment evaluation remains controlled by `GPUEnvironmentData.AtmosphereFlags` and the procedural coefficients.

### 5.2 Report whether environment resources were recreated

Change `EnvironmentManager.EnsureResourcesCurrent` to return `bool`:

- Return `false` without waiting, rebuilding, or republishing when the resource signature is unchanged.
- Preserve the existing wait-idle callback and recreation sequence when it changes.
- Re-register the current global reflection fallback after recreation.
- Return `true` only after resource creation and fallback descriptor publication both succeed.

Do not use a settings revision or source-kind comparison in the renderer as a substitute. `EnvironmentManager` owns the complete resource signature, including path, dimensions, and precision, and is therefore the authoritative change detector.

### 5.3 Add an explicit local-descriptor restoration path

In `Njulf.Rendering/Resources/ReflectionProbeManager.cs`:

- Keep ordinary `Register(BindlessHeap)` behavior for first registration and `_descriptorDirty` updates.
- Add an explicit method such as `RestoreTextureDescriptorsAfterFallback(BindlessHeap)` rather than making arbitrary call sites manipulate `_descriptorDirty`.
- The method returns `false` and leaves the global fallback intact unless both `_cubemapArrayView` and `_debugCubemapView` are valid.
- When both are valid, publish both fixed slots unconditionally, clear `_descriptorDirty` only after both calls succeed, and return `true`.
- Factor the common two-descriptor publication into one private helper so normal dirty registration and forced restoration cannot drift apart.

`BindlessHeap.RegisterTexture` already suppresses identical native writes. The forced method is still restricted to transition boundaries so descriptor no-op metrics and locks do not become per-frame work.

### 5.4 Restore precedence at every displacement site

In `Njulf.Rendering/VulkanRenderer.cs`:

1. Capture the return value from `EnsureResourcesCurrent` before beginning the primary graphics command buffer.
2. If resources were recreated, immediately call `RestoreTextureDescriptorsAfterFallback`. A live local array then reclaims both reflection slots before any pass can consume them; when no local array exists, the just-published environment fallback remains authoritative.
3. In swapchain recreation, keep fallback registration before local restoration and use the explicit forced restoration method instead of ordinary clean-state registration.
4. Keep initial scene registration ordered as environment fallback followed by normal probe registration. At startup the probe manager is dirty when a local array first becomes available.
5. Keep `PrepareReflectionProbes` on ordinary registration. Probe allocation/reallocation already sets `_descriptorDirty`; environment-only changes must use the explicit restoration path above.

Add comments at the live-rebuild and swapchain sites stating the ownership rule, not merely that descriptors are being “refreshed.” This prevents a future cleanup from reversing the order.

### 5.5 Correct resource diagnostics

Update `EnvironmentManager` accounting for the actual resource set:

- Procedural/analytic: five texture images — two 1x1 source fallbacks, two prefiltered ping-pong cubemaps, and one BRDF LUT.
- HDR: four texture images — environment, irradiance, prefiltered, and BRDF.
- `EnvironmentMapBytes` and `IrradianceMapBytes` report one 1x1 six-face cube each in analytic mode.
- `PrefilteredEnvironmentBytes` reports two prefiltered cubes in analytic mode and one in HDR mode.
- `_estimatedBytes` includes both analytic source fallbacks.
- Base all four decisions on `_usesAnalyticSky`, covering both requested procedural sky and automatic fallback after an HDR load failure.

The additional cube-array image view has no image-memory byte charge and should not be counted as another texture image.

## 6. Implementation phases

### Phase 1: Lock the contracts in focused tests

Add focused tests before changing production code:

- `EnvironmentRecreation_ReportsWhetherResourcesChanged` — contract-check the boolean recreation result and unchanged fast path.
- `ProceduralEnvironment_PublishesLiveFixedSourceDescriptors` — require both analytic fallback textures, their fixed indices, and both supported precisions.
- `ReflectionProbeFallback_UsesCubeArrayCompatibleView` — require a `TypeCubeArray` fallback view for the cubemap-array slot and a cube view for the debug slot.
- `ReflectionProbeRestoration_RequiresACompleteLocalViewPair` — a small internal decision helper should reject no-view and partial-view states, accept dirty normal publication, and accept clean forced publication.
- `Renderer_RestoresLocalProbeDescriptorsAfterEnvironmentAndSwapchainRecreation` — source-contract test both displacement boundaries explicitly.

Use a dedicated `Njulf.Tests/EnvironmentProbeDescriptorRepairTests.cs` fixture unless the implementation can reuse an existing test helper cleanly. Avoid hiding these contracts among unrelated GI/debug assertions.

Exit gate: tests fail against the current implementation for the intended reasons and do not require a Vulkan device for the policy/wiring checks.

### Phase 2: Make procedural descriptors and accounting valid

- Implement the 1x1 source cubemap helper and upload.
- Add the cube-array-compatible reflection fallback view and its destruction ordering.
- Update actual-mode resource counts and byte accounting.
- Verify HDR creation remains unchanged and `GetSampledTextureHandles` contains no duplicate handles.

Exit gate: focused fallback, view-type, lifetime-order, and accounting tests pass for Float16, Float32, requested procedural sky, and failed-HDR analytic fallback.

### Phase 3: Restore local-probe ownership deterministically

- Return the recreation result from `EnsureResourcesCurrent`.
- Add the explicit ReflectionProbeManager forced restoration method and shared publication helper.
- Wire live environment recreation and swapchain recreation.
- Confirm the forced path is reached before command-buffer recording/submission and only on transitions.

Exit gate: all renderer wiring tests pass, ordinary probe upload still uses `_descriptorDirty`, and no stable-frame call site invokes forced restoration.

### Phase 4: Runtime qualification

Run a validation-layer smoke sequence with a scene containing captured local probes:

1. Start in HDR mode and wait until all authored probes are published.
2. Record the local-probe/source-ownership debug view.
3. Switch HDR -> procedural -> HDR without restarting.
4. Repeat while resizing the window between each transition.
5. Repeat with no authored probes.
6. Repeat using an invalid HDR path so the requested HDR source falls back to analytic resources.
7. Exercise the irradiance and prefiltered-environment debug views in every state.

For every step require:

- no Vulkan validation error concerning destroyed/invalid image views, descriptor image layout, or cube/cube-array view type;
- local reflection contribution remains present after each transition when probes are published;
- the no-probe path uses the global fallback without sampling a local layer;
- stable output after one transition frame, without flicker from alternating descriptor owners;
- descriptor `ActualWrites` stops increasing once the environment and probe resources are stable;
- resource diagnostics match the actual resource set and return to the HDR totals after switching back.

Exit gate: the full sequence passes twice consecutively, including swapchain recreation, with zero relevant validation messages.

## 7. File-level implementation map

- `Njulf.Rendering/Resources/EnvironmentManager.cs`
  - analytic source fallback images and payload;
  - cube-array-compatible reflection fallback view;
  - boolean recreation result;
  - fallback registration and view destruction;
  - actual-mode resource accounting.
- `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
  - complete-pair publication predicate;
  - shared local texture publication helper;
  - explicit forced restoration after fallback displacement.
- `Njulf.Rendering/VulkanRenderer.cs`
  - live environment recreation result handling;
  - swapchain restoration ordering;
  - ownership comments at both boundaries.
- `Njulf.Tests/EnvironmentProbeDescriptorRepairTests.cs`
  - policy, resource-shape, view-type, and renderer-wiring contracts.
- `Njulf.Tests/DebugToolingContractsTests.cs`
  - modify only if an existing assertion must move to the focused fixture; do not duplicate the same source contract in both files.

No shader change should be necessary. The repair supplies valid descriptors matching the declarations already present in `common.glsl` and the direct debug sampling already present in `tonemap_composite.frag`.

## 8. Verification commands

Run from the `Njulf` directory:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter FullyQualifiedName~EnvironmentProbeDescriptorRepairTests
dotnet build Njulf.sln
dotnet test Njulf.sln
```

Then run the runtime sequence from Phase 4 with Vulkan validation enabled. Keep the validation log and before/after reflection debug captures as implementation evidence.

## 9. Risks and mitigations

- **Partial local publication:** publishing one local slot and failing on the second can leave mixed ownership. Keep `_descriptorDirty` set until both calls finish; fail the frame rather than continuing with a half-published pair.
- **View-type mismatch:** the global prefiltered cubemap's normal view is `TypeCube`, while the local sampling slot is declared as `samplerCubeArray`. Own a separate `TypeCubeArray` fallback view and test its creation/destruction contract.
- **Fallback view lifetime:** the extra view is non-owning and must be destroyed before its texture. Keep it in `EnvironmentManager`, alongside the existing analytic prefilter subresource views.
- **Descriptor-write churn:** do not force restoration from `PrepareReflectionProbes` every frame. Use the recreation result and swapchain boundary only; retain normal dirty publication elsewhere.
- **HDR-load fallback accounting:** settings still say HDR when loading fails. Use `_usesAnalyticSky` after resource creation for inventory and bytes.
- **Accidental probe recapture:** descriptor restoration is not a lighting-content change. Do not increment environment generations or request recapture from the restoration method; existing environment-generation logic handles genuine lighting changes.

## 10. Definition of done

- Procedural and failed-HDR fallback modes have live fixed environment and irradiance cube descriptors.
- Reflection fallback publication uses a cube-array-compatible view for the array slot.
- `EnsureResourcesCurrent` reports recreation explicitly.
- A live local probe array reclaims both fixed reflection slots after environment and swapchain recreation.
- No stable-frame forced descriptor writes are introduced.
- Resource counts and memory totals match actual HDR and analytic allocations.
- Focused tests, full build, and full test suite pass.
- The HDR/procedural/HDR plus resize runtime sequence produces no relevant Vulkan validation errors and preserves published local reflections.
