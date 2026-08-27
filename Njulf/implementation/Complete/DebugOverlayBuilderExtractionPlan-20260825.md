# DebugOverlayBuilder Extraction Implementation Plan

Last updated: 2026-08-25

Status: proposed behavior-preserving refactor.

## 1. Required outcome

Extract CPU-side debug-overlay preparation from `Njulf.Rendering/VulkanRenderer.cs` into a focused `DebugOverlayBuilder` under `Njulf.Rendering/Debugging`.

The completed refactor must:

- Remove the overlay-building region currently occupying lines 4954-5820 of `VulkanRenderer.cs` in the audited working tree, approximately 867 lines.
- Move mode dispatch, status production, CPU debug-line recording, DDGI probe-instance preparation, completed DDGI counter validation, and overlay-only math helpers into the new builder.
- Move the three overlay-owned renderer fields into the builder:
  - the `DebugDrawList`;
  - the reusable `GPUDdgiProbeDebugInstance[]` scratch array;
  - the most recently completed `DebugDdgiOverlayGpuCounters` value.
- Preserve the public `VulkanRenderer.DebugDraw` and `IRendererDebugTools.DebugDraw` contract by delegating to the builder-owned list.
- Keep `VulkanRenderer` responsible for frame ordering, settings capture, scene-data preparation, completed-fence readback, render-graph execution, diagnostics publication, and final frame cleanup.
- Preserve all active and retired overlay identities, exact status/reason semantics, line and marker budgets, DDGI generation validation, and renderer-pass inputs.
- Introduce no Vulkan command recording, resource creation, waits, readbacks, or shader behavior into the builder.

The intended dependency and data flow is:

```text
renderer settings + prepared scene data + live resource views
                         |
                         v
                DebugOverlayBuilder
                 /       |       \
                v        v        v
       DebugDrawSnapshot  status  DDGI probe inputs
                \         |         /
                 v        v        v
           existing render-graph debug passes
                         |
                         v
              renderer diagnostics/captures
```

`DebugOverlayBuilder` must not depend on, retain, or call back into `VulkanRenderer`.

## 2. Audited starting point

### 2.1 Code and state currently embedded in `VulkanRenderer`

The renderer is 23,084 lines in the audited working tree. The cohesive overlay-building block starts at `BuildDebugOverlayDrawCommands` on line 4954 and ends with `GetMaxAbsScale` on line 5820.

The block contains these responsibility groups:

| Responsibility | Current members |
| --- | --- |
| Catalog validation and routing | `BuildDebugOverlayDrawCommands` |
| Full-screen overlay admission | `PrepareLightTileOverlayStatus` |
| CPU line overlays | directional-shadow cascades, object bounds, selected object, reflection probes, DDGI volume bounds, decal volumes, and meshlet bounds |
| DDGI GPU-overlay preparation | `PrepareDdgiProbeOverlay`, `PrepareDdgiProbeDebugInstances`, and completed-counter application |
| Pure validation and sampling | frustum validation, probe-marker budgeting/sampling, probe-instance creation, volume colors, counter clamping, saturating addition, and scale extraction |

The region reads only six renderer fields:

- `_debugDraw`;
- `_ddgiProbeDebugInstanceScratch`;
- `_completedDebugDdgiOverlayCounters`;
- `_meshManager`;
- `_materialManager`;
- `_simpleDdgiVolumeManager`.

It also reads four settings values:

- `Settings.Debug.ShowXRayVolumes`;
- `Settings.Debug.ShowDepthTestedVolumes`;
- `Settings.Debug.SelectedReflectionProbeIndex`;
- `Settings.GlobalIllumination.EffectiveUseDdgi`.

This is a tractable extraction boundary. The first three fields are builder state. The three managers remain owned by the renderer and are exposed through narrow construction/build dependencies.

### 2.2 Current frame lifecycle

Overlay behavior spans several points in the renderer lifecycle; moving only the large method block is insufficient.

1. After the current frame slot's fence has completed, `BeginFrame` reads `DebugDdgiOverlayGpuCounters` from `RendererDiagnosticsBuffer` into `_completedDebugDdgiOverlayCounters` at lines 3685-3687.
2. Near the start of `DrawScene`, the renderer resolves whether debug tooling is enabled and which overlay mode was requested.
3. The renderer enables/disables `_debugDraw`, applies `MaxDebugLineSegments`, and decides whether `SceneDataBuilder` must capture CPU object snapshots at lines 4154-4162.
4. Scene, camera, light, shadow, reflection, mesh, material, and DDGI preparation populate `SceneRenderingData`.
5. `BuildDebugOverlayDrawCommands` records CPU lines or prepares GPU DDGI instances/status at line 4719.
6. `_debugDraw.Snapshot()` is assigned to `sceneData.DebugDrawSnapshot` immediately afterward.
7. `DebugOverlayPass`, `DebugDrawPass`, and `SimpleDdgiProbeDebugPass` consume the prepared fields during render-graph execution.
8. Completed GPU timings are applied and `BuildDiagnostics` publishes overlay counts, status, CPU/GPU timings, and the debug-draw snapshot.
9. `_debugDraw.ClearFrame()` runs only after `_lastDiagnostics` is built at line 4852.

That order is observable. The builder may encapsulate the calls, but it must not move their lifecycle boundaries.

### 2.3 Existing renderer kinds and preparation work

`DebugOverlayCatalog` remains the source of truth for stable identity, active/retired state, cycle order, renderer kind, CPU-snapshot requirements, legend, and guidance.

The current builder-like code prepares different outputs according to `DebugOverlayRendererKind`:

| Kind | Modes | CPU preparation |
| --- | --- | --- |
| `None` | `None` and retired/unknown inputs | Status only; retired and unknown modes retain their explicit reason. |
| `FullScreen` | `LightTiles` | Copies existing tile counts and publishes admission/status; `DebugOverlayPass` renders. |
| `Line` | cascade, reflection, DDGI bounds, decal, object, meshlet, and selected-object modes | Records shapes into `DebugDrawList`; `DebugDrawPass` renders the snapshot. |
| `DdgiProbe` | sphere, activity, updated, relocation, age, physical-slot, newly-exposed, priority, and update-reason modes | Prepares bounded instance/update-record metadata; `SimpleDdgiProbeDebugPass` reads authoritative GPU state and renders. |

The extraction must not merge these GPU passes or turn the builder into a render pass.

### 2.4 DDGI-specific state and contracts

The DDGI path has several correctness rules that are easy to lose in a mechanical move:

- Detailed sampled overlays are bounded to 768 markers per frame.
- Remaining marker capacity is divided among every remaining admitted volume rather than consumed by the first volume.
- Sampling spans all three logical axes.
- Logical coordinates are converted through `SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex`, preserving toroidal addressing.
- Each `GPUDdgiProbeDebugInstance` contains the split 64-bit frame serial, volume/scheduler/residency generations, logical and virtual identities, radius clamp, and scheduler-priority flags.
- `DdgiUpdatedProbes` and `DdgiUpdateReasons` use update-record capacity rather than the sampled-instance array.
- GPU-resident update capacity comes from the current scheduler layout; CPU-reference capacity comes from `ProbesToUpdate`.
- Completed GPU counters are accepted only when mode, volume-table generation, scheduler generation, and residency generation all match the current frame preparation.
- Accepted completed counters replace provisional marker counts and publish update-reason counts. Stale or mismatched counters are ignored.
- The reusable instance array is intentionally shared with `SceneRenderingData` for the duration of the draw. It is not an immutable retained snapshot.

### 2.5 Downstream consumers

The extraction must preserve the inputs observed by:

- `DebugOverlayPass.ShouldExecuteForFrame` for the Forward+ light-tile view;
- `DebugDrawPass` for line commands and depth modes;
- `SimpleDdgiProbeDebugPass` for instance/update capacity, scheduler offsets, generation tags, and depth mode;
- `RendererDiagnostics` construction for status, counts, timings, and draw-list telemetry;
- the sample diagnostics reporter and snapshot/capture writers;
- editor and sample code that records custom commands through `IRendererDebugTools.DebugDraw`.

`VulkanRenderer.BuildDiagnostics` currently reads `_debugDraw.Enabled` directly while all other debug-draw values come from `SceneRenderingData`. That final direct read must be changed to the builder-owned list, or captured as an explicit diagnostics input if `RendererDiagnosticsAssembler` is extracted first.

### 2.6 Existing tests and gaps

Current coverage includes:

- `DebugDrawListTests` for line primitives, persistence, limits, and clearing;
- `DebugOverlayCatalogTests` for catalog identity/cycling, status defaults, pass admission, frustum validation, DDGI instance layout, counter decoding, and render-graph resource declarations;
- `DebugToolingContractsTests` for settings defaults, input shortcuts, enum stability, marker sampling, and marker-budget distribution;
- shader mirror and GPU-struct layout coverage.

Existing tests currently call helpers through `VulkanRenderer`:

- `VulkanRenderer.IsValidDebugFrustumMatrix`;
- `VulkanRenderer.CreateDdgiProbeDebugInstance`;
- `VulkanRenderer.CalculateDdgiProbeMarkerSampling`, `ShouldDrawDdgiProbeMarker`, and `CalculateDdgiProbeMarkerBudget`.

`DebugToolingContractsTests.VulkanRenderer_DebugOverlaySupportsSimpleDdgiProbeVolume` also verifies implementation by reading `VulkanRenderer.cs` and matching source substrings. It will fail for the correct architectural reason after extraction and should be replaced with executable builder behavior rather than redirected to another source file.

There is no focused test fixture for mode dispatch, exact status reasons, external-line composition, completed-counter generation rejection, or the builder's frame lifecycle.

## 3. Scope

### In scope

- A renderer-lifetime `DebugOverlayBuilder` in namespace `Njulf.Rendering.Debug`.
- Builder ownership of the draw list, DDGI probe-instance scratch array, and latest completed DDGI overlay counters.
- A small immutable per-build options contract containing only the settings values used by overlay construction.
- A narrow geometry/material lookup seam for decal and meshlet overlays.
- Moving every method in the current 4954-5820 overlay region when it is overlay-only.
- Preserving `VulkanRenderer.DebugDraw` as a delegation.
- Updating the completed-counter handoff, build/snapshot call, diagnostics read, and end-of-frame clear call.
- Retargeting internal helper tests to the new semantic owner.
- Adding focused builder characterization and lifecycle tests.

### Out of scope

- Changing any `DebugOverlayMode` numeric identity, active cycle order, retirement decision, renderer kind, legend, or guidance.
- Adding or removing overlay modes.
- Changing `DebugOverlaySettings`, `SceneRenderingData`, `RendererDiagnostics`, performance snapshot, or persisted settings schemas.
- Refactoring `DebugOverlayCatalog`, `DebugDrawList`, `DebugOverlayPass`, `DebugDrawPass`, or `SimpleDdgiProbeDebugPass` beyond the minimal call-site changes needed for the extraction.
- Changing shaders, render-graph declarations, pipeline layouts, GPU structs, descriptor bindings, or timestamp names.
- Changing DDGI scheduling, paging, toroidal addressing, generation ownership, readback cadence, or GPU-counter decoding.
- Optimizing the existing meshlet `Snapshot().LineCount` query, determinant validation, line generation, or scratch strategy during the move.
- Moving screen-space material, shadow, animation, GI, or forward debug-view policy such as `ResolveForwardDebugViewMode`.
- Moving `OverlayDrawData`, `QueueOverlayDrawData`, `CreateOverlayTexture`, or ImGui rendering. Those are editor/UI overlays, not world-debug-overlay construction.
- Making `VulkanRenderer` partial as the final design.
- Passing `VulkanRenderer`, the full `RenderSettings`, or a general service locator into the builder.

## 4. Non-negotiable invariants

1. Overlay construction remains synchronous and occurs exactly once at the existing `DrawScene` boundary after scene/shadow/reflection/DDGI preparation and before render-graph execution.
2. Completed DDGI overlay counters are observed only after the matching frame-slot fence completes, at the same `BeginFrame` boundary as today.
3. Draw-list `Enabled` and `MaxLineSegments` are set at the same early-`DrawScene` point. Do not move this configuration to startup or after external one-frame commands have already been snapshotted.
4. `VulkanRenderer.DebugDraw` returns the same renderer-lifetime `DebugDrawList` instance for the renderer's lifetime.
5. A frame snapshot contains persistent commands, externally submitted one-frame commands, and builder-generated commands in the same order as today.
6. `ClearFrame` remains after render-graph execution and diagnostics assembly. It clears one-frame commands and the dropped-line count but retains persistent commands.
7. Debug-disabled frames still produce an empty draw snapshot and default overlay status without allocating DDGI scratch or optional GPU resources.
8. A valid `None` request produces `DebugOverlayFrameStatus.Disabled`; unknown values, retired modes, and active modes without a handler preserve their existing availability and exact reason text.
9. `DebugOverlayCatalog` remains authoritative. The builder does not maintain a second list of cycle order, labels, preconditions, or retirement policy.
10. Depth-mode precedence remains x-ray first, then depth-tested, then always-visible.
11. The line budget applies to the shared draw list. Dropped counts in overlay status continue to include drops caused by the aggregate of external, persistent, and builder commands.
12. CPU overlay record timing starts only for a recognized active non-`None` mode and is written to `sceneData.CpuDebugOverlayRecordMicroseconds` with the same units.
13. `LightTiles` remains status-only CPU preparation and never emits world-space lines.
14. Directional cascade validation continues to reject identity, zero, non-finite, singular, and near-singular matrices and to sum the existing per-cascade meshlet counts.
15. Reflection-probe selection, colors, shape transforms, radii/extents, blend-volume alpha, and empty-state reasons remain unchanged.
16. Decal and mesh lookup continues to suppress only `ArgumentException` and `InvalidOperationException`; unexpected exceptions must still propagate.
17. Meshlet bounds retain the 2,048-item cap, 8-segment/24-line sphere cost, existing-line accounting, separate item-cap and line-budget drop counters, saturating arithmetic, world transform, and maximum-absolute-scale radius.
18. DDGI sampled markers retain the 768 cap, per-volume redistribution, axis sampling, toroidal index mapping, instance layout, radius clamp, generation tags, and scheduler flags.
19. Completed DDGI counters are applied only on an exact mode plus three-generation match. A mismatch leaves provisional current-frame counts intact.
20. Update-record modes remain exactly `DdgiUpdatedProbes` and `DdgiUpdateReasons`; they continue to execute the GPU pass for a bounded zero-result diagnostic header when capacity is nonzero.
21. The builder may retain reusable scratch and the most recent completed counter value, but it must not retain `Scene`, `SceneRenderingData`, mutable settings objects, or a per-frame build-options instance after `Build` returns.
22. The builder records no Vulkan commands, allocates no GPU resources, performs no waits/readbacks, and does no filesystem, logging, or serialization work.
23. The extraction introduces no new steady-frame LINQ, reflection, delegate allocation, dictionary dispatch, or unbounded collection growth.
24. Existing public debug interfaces and persisted diagnostics remain source- and schema-compatible.

## 5. Chosen architecture

### 5.1 Stateful CPU preparation service

Add `Njulf.Rendering/Debugging/DebugOverlayBuilder.cs`:

```csharp
internal readonly record struct DebugOverlayBuildOptions(
    bool SimpleDdgiEnabled,
    bool ShowXRayVolumes,
    bool ShowDepthTestedVolumes,
    int SelectedReflectionProbeIndex);

internal sealed class DebugOverlayBuilder
{
    private const int MaxDetailedProbeMarkersPerFrame = 768;

    private readonly DebugDrawList _drawList = new();
    private readonly IDebugOverlayResourceLookup _resources;
    private GPUDdgiProbeDebugInstance[]? _ddgiProbeInstanceScratch;
    private DebugDdgiOverlayGpuCounters _completedDdgiCounters;

    internal DebugDrawList DrawList => _drawList;

    internal DebugOverlayBuilder(IDebugOverlayResourceLookup resources);

    internal void ConfigureDrawList(bool enabled, int maxLineSegments);

    internal void ObserveCompletedDdgiCounters(
        in DebugDdgiOverlayGpuCounters counters);

    internal DebugDrawFrameSnapshot Build(
        Scene scene,
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager? ddgi,
        in DebugOverlayBuildOptions options);

    internal void ClearFrame();
}
```

Names may be adjusted to project conventions, but the ownership and call boundaries should remain explicit.

`Build` should use a private dispatch method with the existing early-return behavior, then always return `_drawList.Snapshot()`. This guarantees that custom external lines are captured even when the selected overlay is `None`, retired, unknown, or otherwise produces no internal primitives.

`ConfigureDrawList` and `ClearFrame` are intentionally separate from `Build`; collapsing them into one call would move observable frame lifecycle behavior.

The builder should contain its own private elapsed-time helper rather than call a static method on `VulkanRenderer`.

### 5.2 Narrow resource lookup seam

Add a small internal lookup abstraction for the two CPU overlays that currently query heavyweight managers:

```csharp
internal interface IDebugOverlayResourceLookup
{
    bool TryGetMaterialMetadata(
        MaterialHandle handle,
        out MaterialRenderMetadata metadata);

    bool TryGetMeshInfo(MeshHandle handle, out MeshInfo meshInfo);

    bool TryGetMeshlet(uint index, out Njulf.Core.Geometry.Meshlet meshlet);
}
```

The production `RendererDebugOverlayResourceLookup` holds `MeshManager` and `MaterialManager`. It converts only the currently suppressed `ArgumentException` and `InvalidOperationException` cases into `false`. This keeps builder tests deterministic without exposing manager internals or constructing Vulkan-backed resource managers.

Do not use per-frame delegates. The one adapter is allocated with the renderer and reused.

The live `SimpleDdgiVolumeManager?` remains an explicit per-build input for this extraction. Its volume, scheduler, feedback, residency, and generation data form one tightly coupled runtime source; mirroring that large surface into a speculative interface would broaden the mechanical move. The builder must not retain the manager after the call.

### 5.3 Mode dispatch remains explicit

Keep the current explicit switch inside the builder for the first extraction. It is bounded, allocation-free, easy to compare against the catalog, and preserves current fallthrough behavior.

The dispatch sequence remains:

1. Return default status when debug tooling is disabled.
2. Resolve the descriptor through `DebugOverlayCatalog.TryGet`.
3. Publish `Unavailable` for an unknown numeric value.
4. Publish `Retired` for an inactive descriptor.
5. Publish `Disabled` for `None`.
6. Resolve and store the depth mode.
7. Execute the existing per-mode handler.
8. Publish CPU record time.
9. Return the draw-list snapshot from the outer `Build` wrapper.

Do not add a dictionary of delegates or duplicate catalog metadata in the builder. A catalog-to-handler completeness assertion in tests is sufficient for this refactor.

### 5.4 DDGI state ownership

Move `_ddgiProbeDebugInstanceScratch` and `_completedDebugDdgiOverlayCounters` into the builder.

At `BeginFrame`, the renderer transfers the fence-complete counter snapshot:

```csharp
_debugOverlayBuilder.ObserveCompletedDdgiCounters(
    _diagnosticsBuffer.GetLastCompletedDebugDdgiOverlayCounters(_currentFrame));
```

The builder lazily allocates exactly one 768-element instance array on the first sampled DDGI overlay. Update-record modes and disabled/non-DDGI modes must not allocate it.

When the builder assigns the scratch array to `sceneData.DebugDdgiProbeInstances`, preserve the current count field and same-frame consumption contract. Do not return the buffer to a pool or clear/reallocate it before `SimpleDdgiProbeDebugPass` records the frame.

Keep `CreateDdgiProbeDebugInstance`, `CalculateDdgiProbeMarkerBudget`, `CalculateDdgiProbeMarkerSampling`, `ShouldDrawDdgiProbeMarker`, and the sampling record as `internal` members of the new owner so existing focused tests can target the algorithms directly. Pure private support methods remain private.

### 5.5 Renderer integration

Replace the three overlay-owned fields with one renderer-lifetime builder:

```csharp
private readonly DebugOverlayBuilder _debugOverlayBuilder;

public DebugDrawList DebugDraw => _debugOverlayBuilder.DrawList;
```

Construct it in `VulkanRenderer`'s constructor after validating the mesh and material manager arguments, using one `RendererDebugOverlayResourceLookup`. This keeps `DebugDraw` available before `Initialize`, as it is today.

At the current draw-list configuration site:

```csharp
_debugOverlayBuilder.ConfigureDrawList(
    debugEnabled,
    Settings.Debug.MaxDebugLineSegments);
```

Do not move the `SceneDataBuilder.CaptureCpuSnapshots` decision into the builder. It is an upstream scene-preparation decision and must still occur before scene snapshots are built.

At the current overlay call site, capture the four settings values once and invoke the builder:

```csharp
var overlayOptions = new DebugOverlayBuildOptions(
    Settings.GlobalIllumination.EffectiveUseDdgi,
    Settings.Debug.ShowXRayVolumes,
    Settings.Debug.ShowDepthTestedVolumes,
    Settings.Debug.SelectedReflectionProbeIndex);

sceneData.DebugDrawSnapshot = _debugOverlayBuilder.Build(
    scene,
    sceneData,
    _simpleDdgiVolumeManager,
    overlayOptions);
```

Near diagnostics construction, replace the direct `_debugDraw.Enabled` read with `_debugOverlayBuilder.DrawList.Enabled`, or capture that boolean into the diagnostics-assembler input if that extraction has already landed.

At the current end of `DrawScene`:

```csharp
_debugOverlayBuilder.ClearFrame();
```

The renderer remains the composition root and owns all call timing.

### 5.6 Methods that move together

Move the following members into `DebugOverlayBuilder` in one staged series:

- `BuildDebugOverlayDrawCommands` as the private dispatch body;
- `PrepareLightTileOverlayStatus`;
- `DrawDirectionalShadowCascadeOverlay`;
- `IsValidDebugFrustumMatrix`;
- `ResolveOverlayDepthMode`, changed to consume `DebugOverlayBuildOptions`;
- `DrawObjectBoundsOverlay`;
- `DrawSelectedObjectOverlay`;
- `DrawReflectionProbeOverlay`;
- `DrawDdgiProbeVolumeOverlay`;
- `PrepareDdgiProbeOverlay`;
- `PrepareDdgiProbeDebugInstances`;
- `TryApplyCompletedDebugDdgiOverlayCounters`;
- `ClampDebugCounter`;
- `CreateDdgiProbeDebugInstance`;
- `DrawSimpleDdgiProbeVolumeOverlay`;
- `ResolveSimpleDdgiVolumeDebugColor`;
- `DdgiProbeMarkerSampling`;
- `CalculateDdgiProbeMarkerBudget`;
- `CalculateDdgiProbeMarkerSampling`;
- `ShouldDrawDdgiProbeMarker`;
- `SampledAxisCount`;
- the `int` overload of `SaturatingAdd`;
- `DrawGeometryDecalOverlay`;
- `DrawMeshletBoundsOverlay`;
- `GetMaxAbsScale`.

The `uint SaturatingAdd` overload near the end of `VulkanRenderer` is unrelated diagnostics/GI logic and remains there.

### 5.7 Boundaries that remain outside the builder

The following stay with their current owners:

- requested-mode resolution and `DebugToolingEnabled` assignment;
- `DebugOverlayCatalog` and all user-facing catalog data;
- CPU object-snapshot admission and `SceneDataBuilder` execution;
- shadow, reflection, material, mesh, and DDGI runtime preparation;
- completed-fence acquisition from `RendererDiagnosticsBuffer`;
- render-graph pass creation, admission, execution, and GPU timestamp application;
- diagnostics assembly and publication;
- editor/sample shortcut handling and custom debug primitive recording;
- `DebugDrawList` primitive implementation and locking;
- frame reset in `SceneRenderingData.ResetFrame`.

### 5.8 Coordination with the other renderer extraction plans

The proposed `RendererDiagnosticsAssembler` plan currently describes `_debugDraw` as renderer-owned. If both extractions are implemented, prefer this order:

1. Extract `DebugOverlayBuilder` and make `VulkanRenderer.DebugDraw` delegate to it.
2. Capture `DebugDrawEnabled` and the already-built `sceneData.DebugDrawSnapshot` in the diagnostics assembler's grouped debug input.
3. Keep diagnostics assembly before `DebugOverlayBuilder.ClearFrame`.

`PerformanceCaptureMetadataProvider` has no direct dependency on this builder. Both feed values into diagnostics through separate typed boundaries and must not call one another.

Because `VulkanRenderer.cs` already contains unrelated working-tree changes, implementation must move only the audited overlay members and narrow lifecycle call sites. Do not replace the entire file from another branch or regenerate it wholesale.

## 6. Delivery order and change isolation

### Phase 0: Freeze current behavior

Before moving production code:

1. Add focused characterization tests for every early status path and all exact reason strings.
2. Add tests proving the x-ray/depth/always-visible precedence.
3. Add a lifecycle test proving external one-frame and persistent commands are present in the returned snapshot and that clear retains only persistent commands.
4. Add completed-DDGI-counter match/mismatch tests for mode and all three generations.
5. Record the current per-mode counts for one representative input fixture.

Exit criterion: the tests fail if dispatch order, status wording, dropped counts, or clear timing changes.

### Phase 1: Add the builder shell and lookup adapter

1. Add `IDebugOverlayResourceLookup` and the production manager-backed adapter.
2. Add `DebugOverlayBuildOptions` and `DebugOverlayBuilder` with draw-list ownership, configuration, counter observation, snapshot, and clear methods.
3. Construct the builder in `VulkanRenderer` and delegate the public `DebugDraw` property.
4. Keep existing overlay methods temporarily on `VulkanRenderer` while the new lifecycle seam is compiled and tested.

Exit criterion: editor/sample callers receive the same draw-list instance and existing draw-list tests remain green.

### Phase 2: Move pure helpers and retarget semantic usages

1. Move frustum validation, DDGI marker budget/sampling, DDGI instance creation, counter clamp, color, saturating-int, and scale helpers.
2. Retarget all IDE-resolved test usages from `VulkanRenderer` to `DebugOverlayBuilder` in the same change.
3. Remove temporary renderer wrappers; the final renderer must not expose overlay algorithms.
4. Use semantic Find Usages before deleting each old member so no `nameof`, documentation reference, or test call is missed.

Exit criterion: no overlay-only helper remains on `VulkanRenderer`, and helper tests reference the new owner.

### Phase 3: Move catalog routing and non-DDGI modes

1. Move the dispatch body and depth resolution.
2. Move light-tile status and all non-DDGI line modes.
3. Replace material/mesh manager calls with the lookup adapter without changing exception behavior.
4. Keep the existing switch ordering and literal status reasons.
5. Return the draw snapshot from the builder's outer `Build` method.

Exit criterion: `None`, unknown, retired, light-tile, cascade, object, selected, reflection, decal, and meshlet tests match the baseline.

### Phase 4: Move DDGI preparation and state

1. Move DDGI bounds and sampled/update-record preparation.
2. Move the reusable scratch array and completed-counter field.
3. Replace the renderer's readback assignment with `ObserveCompletedDdgiCounters`.
4. Preserve lazy allocation and exact generation matching.
5. Run dense/sparse and CPU/GPU scheduler test coverage before removing the old methods.

Exit criterion: every DDGI overlay produces the same instance/update capacity, status, generation fields, and counter application as the baseline.

### Phase 5: Complete renderer wiring and remove remnants

1. Replace the old build plus separate snapshot calls with the builder call.
2. Replace the diagnostics `_debugDraw.Enabled` read.
3. Replace the final clear call.
4. Delete `_debugDraw`, `_ddgiProbeDebugInstanceScratch`, `_completedDebugDdgiOverlayCounters`, and the old method region.
5. Confirm the only remaining overlay-builder references in `VulkanRenderer` are construction, public delegation, configuration, counter handoff, build, diagnostics capture, and clear.

Exit criterion: the approximately 867-line block and three fields are gone from `VulkanRenderer` with no compatibility wrappers.

### Phase 6: Verification and cleanup

1. Replace the brittle source-substring DDGI test with executable builder assertions.
2. Run focused tests, the full test project, and the full solution build.
3. Run the runtime mode matrix with Vulkan validation enabled.
4. Compare diagnostics and screenshots for representative line, full-screen, sampled-DDGI, and update-record modes.
5. Inspect allocations with debug disabled and with a steady active mode.

Exit criterion: all automated/runtime gates pass and no normal frame pays new overlay cost when debug tooling is disabled.

## 7. File-level implementation map

### New files

`Njulf.Rendering/Debugging/DebugOverlayBuilder.cs`

- Builder state, lifecycle API, mode dispatch, all overlay handlers, DDGI preparation, and pure helpers.

`Njulf.Rendering/Debugging/DebugOverlayResourceLookup.cs`

- `IDebugOverlayResourceLookup` and the manager-backed production adapter.

`Njulf.Tests/DebugOverlayBuilderTests.cs`

- Focused mode, status, resource lookup, lifecycle, and completed-counter tests.

### Modified files

`Njulf.Rendering/VulkanRenderer.cs`

- Replace three fields with `_debugOverlayBuilder`.
- Construct the builder and delegate `DebugDraw`.
- Transfer completed counters, configure, build/snapshot, read enabled state, and clear through the builder.
- Delete the overlay method region.

`Njulf.Tests/DebugOverlayCatalogTests.cs`

- Retarget frustum and DDGI-instance helper calls to `DebugOverlayBuilder`.
- Retain catalog and pass-admission coverage.

`Njulf.Tests/DebugToolingContractsTests.cs`

- Retarget marker sampling/budget helpers.
- Replace `VulkanRenderer.cs` substring assertions with builder behavior tests or move that coverage into `DebugOverlayBuilderTests`.

### Files expected to remain behavior/schema stable

- `Njulf.Rendering/Debugging/DebugOverlayMode.cs`
- `Njulf.Rendering/Debugging/DebugOverlayCatalog.cs`
- `Njulf.Rendering/Debugging/DebugOverlayFrameStatus.cs`
- `Njulf.Rendering/Debugging/DebugDrawList.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Pipeline/DebugOverlayPass.cs`
- `Njulf.Rendering/Pipeline/DebugDrawPass.cs`
- `Njulf.Rendering/Pipeline/SimpleDdgiProbeDebugPass.cs`
- `Njulf.Rendering/Resources/RendererDiagnosticsBuffer.cs`
- all debug shaders and GPU ABI structs.

If one of these stable files requires a semantic change, stop treating the work as a mechanical extraction and review the change separately.

## 8. Test matrix

### Builder routing and statuses

- Debug tooling disabled returns default status and no builder work.
- `None` returns `Disabled` and still snapshots pre-existing custom commands when the draw list is enabled.
- Unknown numeric values return `Unavailable` with `unknown overlay value N`.
- Every retired mode returns `Retired` with the catalog reason.
- Every active catalog mode reaches a registered handler; no active mode falls into `catalog renderer has no registered handler`.
- CPU record time remains zero for disabled/unknown/retired/`None`; active work writes the measured value, which may legitimately round to zero microseconds.

### Line and full-screen modes

- Light tiles distinguish no local lights from rendered tile counts and copy max/average counts.
- Cascades respect active count, directional-light availability, valid matrices, stable colors, meshlet totals, and dropped-line counts.
- Object bounds preserve visible/culled colors and CPU-snapshot guidance.
- Selected object validates negative/out-of-range indices and emits exactly one box when valid.
- Reflection probes preserve all/selected filtering, sphere versus box shape, inner blend shape, and no-data reasons.
- Decals use only geometry-decal metadata and tolerate exactly the two current lookup exceptions.
- Meshlet bounds preserve visibility filtering, fallback meshlet count, transform/scale, item cap, line budget, and separate drop counters.
- Depth mode resolves `XRay`, `DepthTested`, and `AlwaysVisible` with current precedence.

### DDGI preparation

- DDGI disabled returns `Unavailable`; missing/empty manager state returns the current `NoData` reason.
- Update-record modes select the correct CPU/GPU scheduler capacity, clamp to 768, and publish zero-result status correctly.
- Sample allocation is shared across remaining volumes and never exceeds 768 total instances.
- Sampling covers x/y/z and uses toroidal physical-local addressing.
- Instance radius clamps at 0.04 and 0.20, frame serial splits exactly, and all generation/flag fields survive.
- Sphere mode retains faint volume bounds and sphere line-segment accounting.
- Completed counters apply on exact mode plus generation match and are rejected independently for each mismatch dimension.
- Unsigned counters above `int.MaxValue` clamp rather than wrap.
- Update-reason counts are copied only from an accepted completed result.

### Draw-list lifecycle and compatibility

- `VulkanRenderer.DebugDraw` returns the builder-owned list and remains compatible with `IRendererDebugTools`.
- Custom editor/sample commands and builder commands appear in one snapshot in current ordering.
- Persistent commands survive `ClearFrame`; one-frame commands and dropped count do not.
- The snapshot remains valid for render-pass consumption after the live list is eventually cleared.
- `DebugDrawPass`, `DebugOverlayPass`, and `SimpleDdgiProbeDebugPass` admission tests remain unchanged.
- Overlay fields serialized through `RendererDiagnostics` and `PerformanceSnapshotWriter` retain names and values.

### Runtime matrix

Exercise at minimum:

| Case | Required evidence |
| --- | --- |
| Debug disabled and `None` | No optional pass work, no DDGI scratch allocation, empty internal snapshot. |
| Light tiles | Rendered and no-local-light statuses; correct full-screen pass admission. |
| Cascade/reflection/object/decal/meshlet | Representative lines, counts, depth modes, and no-data states. |
| DDGI Low/Medium | Dense and CPU-reference semantics. |
| DDGI High/Ultra | GPU-resident sparse/toroidal semantics and generation-safe counters. |
| DDGI mode change | Previous mode's completed counters are rejected. |
| DDGI resource generation change | Stale volume/scheduler/residency results are rejected. |
| External persistent debug primitives | They coexist with overlay lines and survive frame clear. |
| Resize/swapchain recreation | Existing debug passes continue without builder-owned GPU state. |

## 9. Verification commands

Run focused tests first:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~DebugOverlayBuilderTests|FullyQualifiedName~DebugOverlayCatalogTests|FullyQualifiedName~DebugToolingContractsTests|FullyQualifiedName~DebugDrawListTests"
```

Run the full test project and build:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore
dotnet build Njulf.sln --no-restore
```

Check ownership after the move:

```powershell
rg -n "BuildDebugOverlayDrawCommands|PrepareDdgiProbeDebugInstances|DrawMeshletBoundsOverlay|_ddgiProbeDebugInstanceScratch|_completedDebugDdgiOverlayCounters" Njulf.Rendering/VulkanRenderer.cs
rg -n "VulkanRenderer\.(IsValidDebugFrustumMatrix|CreateDdgiProbeDebugInstance|CalculateDdgiProbeMarker)" Njulf.Tests
rg -n "DebugOverlayBuilder" Njulf.Rendering Njulf.Tests
```

The first two searches should return no old ownership references. Review all results from the third search to confirm the narrow integration surface.

For runtime validation, cycle every active `DebugOverlayCatalog.ActiveCycle` mode in `NjulfHelloGame` with Vulkan validation enabled. Record mode, status, primary/secondary/dropped counts, CPU record time, relevant GPU pass time, and a screenshot for representative modes.

## 10. Risks and mitigations

### Risk: frame-local commands are cleared too early

Keep `ClearFrame` as a renderer-invoked lifecycle call after render execution and diagnostics publication. Test persistent and one-frame behavior explicitly.

### Risk: public debug drawing receives a different list

Construct one builder per renderer, expose its one draw-list instance, and never replace that instance during initialization or swapchain recreation.

### Risk: early-return modes lose custom commands

Use an outer `Build` wrapper that always snapshots after the private dispatch body, even when dispatch returns early.

### Risk: DDGI scratch is reused before the pass consumes it

Retain the array for builder lifetime and preserve same-frame build-before-render ordering. Do not clear or resize it after assigning it to `SceneRenderingData` for that draw.

### Risk: stale completed counters overwrite current provisional counts

Keep the exact mode and three-generation validation together in one builder method, with mismatch tests for each field.

### Risk: settings are read at different times

Create `DebugOverlayBuildOptions` at the existing call site and pass values, not the mutable `RenderSettings` tree. Configure the draw list at its existing earlier boundary.

### Risk: the lookup adapter changes failure behavior

Catch only `ArgumentException` and `InvalidOperationException`, matching current code. Add a test proving an unrelated exception propagates.

### Risk: meshlet drop accounting changes

Characterize the 2,048-item cap, existing-line count, 24-line sphere cost, saturating sums, and global draw-list drops before moving the code. Do not optimize the snapshot count query in this refactor.

### Risk: diagnostics extraction and builder extraction conflict

Land builder ownership first or explicitly rebase the diagnostics input capture. In either order, diagnostics must read the builder/list state before clear and must not retain the mutable builder.

### Risk: another large class replaces the large renderer block

An approximately 850-line builder is acceptable for the first move because its state and purpose are cohesive and independently testable. Defer per-renderer-kind handler classes until change frequency or tests demonstrate a real need.

### Risk: unrelated working-tree changes are overwritten

Use narrow patches and semantic usage searches. Never replace or revert the full dirty `VulkanRenderer.cs`.

## 11. Definition of done

The extraction is complete only when all of the following are true:

- `DebugOverlayBuilder` exists under `Njulf.Rendering/Debugging` and has no dependency on `VulkanRenderer`.
- The builder owns the draw list, DDGI instance scratch, and completed DDGI counters.
- `VulkanRenderer.DebugDraw` delegates to the builder-owned list without public API change.
- The old 4954-5820 overlay method region and three renderer fields are removed.
- Renderer integration is limited to construction, configuration, counter handoff, build/snapshot, diagnostics capture, and clear.
- Every current overlay mode retains its catalog disposition, renderer kind, output, exact status/reason, counts, depth mode, and budget behavior.
- DDGI marker limits, sampling, identities, generation validation, and update-record behavior are unchanged.
- Custom and persistent debug commands retain their lifecycle.
- No GPU resource, Vulkan, render-pass, settings-schema, diagnostics-schema, or shader responsibility moved into the builder.
- Existing helper tests target `DebugOverlayBuilder`, and the source-substring renderer test has been replaced with executable behavior coverage.
- Focused tests, the full test project, the full solution build, and the runtime validation matrix pass.
- Debug-disabled frames show no new steady-frame allocation or optional GPU work.

## 12. Follow-up work explicitly deferred

After the behavior-preserving extraction is stable, separate changes may consider:

- splitting DDGI preparation into a dedicated collaborator if it evolves independently;
- replacing the explicit switch with typed handlers only if catalog/handler drift recurs;
- exposing an allocation-free current line-count query on `DebugDrawList` for meshlet budgeting;
- moving shared transform/math helpers to a neutral geometry utility;
- replacing direct `SimpleDdgiVolumeManager` access with a focused read-only debug source;
- decomposing the broad `SceneRenderingData` debug field group into an internal overlay frame record;
- adding automated golden-image coverage for representative overlay modes.

None of these follow-ups should be mixed into the initial extraction.
