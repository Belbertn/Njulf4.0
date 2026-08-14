# Ctrl+Keypad9 Debug Overlay Improvement Plan

- Date: 2026-08-07
- Status: implemented 2026-08-14; automated and startup validation complete,
  full runtime visual matrix pending
- Scope: the `Ctrl+Keypad9` overlay cycle, its renderer implementations,
  diagnostics, tests, and documentation
- Baseline: the current working tree, including GPU-resident Simple-DDGI
  scheduling, toroidal scrolling, authoritative `SparseNearRing` paging, and
  the in-progress storage ABI 6/transport changes
- Compatibility rule: preserve every existing `DebugOverlayMode` numeric value;
  append new values and retire old values from the interactive cycle without
  renumbering persisted or diagnostic identities

## 1. Required outcome

`Ctrl+Keypad9` must cycle only through overlays that have a defined visual
contract. Selecting a mode must produce either visible output or an explicit,
machine-readable reason such as `no reflection probes`, `DDGI disabled`, or
`selected object unavailable`. It must never silently select an enum value for
which the renderer has no implementation.

The completed work must also provide a dedicated `DdgiProbeSpheres` mode. It
must draw sampled probes as small world-space spheres, centred on the actual
relocated position when valid and on the logical position otherwise, so probe
placement is substantially easier to read than the current three-axis crosses.

The overlay system must remain debug-only and non-blocking:

- no synchronous GPU readback or fence wait;
- no whole-probe-pool CPU scan in the production `GpuResident` path;
- no debug allocation or shader work while debug tooling is disabled;
- bounded marker, line, instance, and text/console work;
- the normal render result is unchanged when the overlay is `None`.

## 2. Current implementation audit

### 2.1 Control and rendering path

The active path is split between:

- `NjulfHelloGame/SampleInputController.cs`, which manually advances
  `DebugOverlayMode` in `NextDebugOverlay`;
- `Njulf.Rendering/VulkanRenderer.cs`, whose
  `BuildDebugOverlayDrawCommands` switch independently decides which modes do
  work;
- `Njulf.Rendering/Debugging/DebugDrawList.cs` and
  `Pipeline/DebugDrawPass.cs`, which render CPU-built lines;
- `Njulf.Rendering/Debugging/DebugOverlayMode.cs`, whose enum contains more
  modes than either implementation or documentation accurately supports.

The two independent switches have already drifted. Tests currently verify enum
numbers and source-code substrings, but do not prove that every cycle entry has
a renderer or that its output matches its name.

### 2.2 Per-mode disposition

| Value | Mode | Current behaviour | Decision |
| ---: | --- | --- | --- |
| 0 | `None` | Clears the active overlay. | Keep. |
| 1 | `LightTiles` | In the input cycle, but absent from the renderer switch. | Implement the originally intended Forward+ tile heatmap. |
| 2 | `DirectionalShadowCascades` | In the input cycle, but absent from the renderer switch. | Implement world-space cascade frusta; keep `F2`/`ShadowDebugView.CascadeOverlay` as the separate receiver-space view. |
| 3 | `ReflectionProbeVolumes` | Draws one influence sphere or oriented box per selected/all probe. | Keep and add the blend/falloff extent plus honest empty-state reporting. |
| 4 | `DdgiProbeVolumes` | Draws DDGI volume boxes and deliberately sets the probe-marker budget to zero. | Keep, but label/document it as volume bounds rather than probe locations. |
| 5 | `DecalVolumes` | Draws bounds for geometry-decal objects. | Keep. |
| 6 | `ObjectBounds` | Draws visible and culled CPU snapshot bounds. | Keep. |
| 7 | `MeshletBounds` | Draws bounded meshlet spheres from CPU snapshots. | Keep and report truncation distinctly from line-budget drops. |
| 8 | `SelectedObject` | Draws the selected object's bounds. | Keep. |
| 9 | `MaterialInspection` | Draws exactly the same selected-object box as value 8; it displays no material data. | Retain the enum value for compatibility, remove it from the cycle, and route material inspection to `/`, `Ctrl+K`, and the editor panel. |
| 10 | `PassTimings` | In the input cycle, but absent from the renderer switch. | Remove from the cycle. Timing tables belong in the diagnostics reporter/editor until a renderer-independent text HUD exists. |
| 11 | `GpuMemory` | In the input cycle, but absent from the renderer switch. | Remove from the cycle. Use the existing diagnostic snapshot/memory report rather than a blank visual mode. |
| 12 | `DdgiProbeActivity` | Draws every sampled probe as the same green cross. | Implement active/inactive/fresh/unpublished/nonresident state colours from authoritative state. |
| 13 | `DdgiUpdatedProbes` | Uses the CPU `_probeQueued` table; this is not authoritative in the default `GpuResident` scheduler. | Implement from the current admitted update records and committed frame identity. |
| 14 | `DdgiProbeRelocation` | Draws generic green crosses at logical, not relocated, positions. | Implement logical-to-relocated vectors and relocated markers. |
| 15 | `DdgiProbeAge` | Draws generic green crosses and does not read age. | Implement an age heatmap against lifecycle targets. |
| 16 | `DdgiPhysicalSlots` | Hashes a dense/global virtual index; it does not resolve a sparse physical page or slot. | Implement real virtual-to-physical residency identity and a nonresident colour. |
| 17 | `DdgiCascadeBounds` | Draws volume boxes plus generic cyan crosses. | Keep the useful ring/volume bounds and remove unrelated crosses. |
| 18 | `DdgiNewlyExposedCells` | Draws generic green crosses without checking scroll exposure. | Implement from the scroll-exposed lifecycle/reason bit. |
| 19 | `DdgiFrustumPriority` | Draws generic green crosses. The current scheduler uses a visibility/proximity class, not a literal frustum score. | Keep the numeric identity, present it as `DDGI scheduler priority`, and visualize the current visible/proximity class. |
| 20 | `DdgiSafetyRefresh` | Draws generic green crosses; there is no current scheduler concept named safety refresh. | Retain the enum value for compatibility and remove it from the cycle. `RoutineDue`, retry, and source repair belong in `DdgiUpdateReasons`. |
| 21 | `DdgiCascadeBlend` | Produces the same marker colour as cascade bounds and does not show blend weight. | Remove from this world-overlay cycle. Use the existing screen-space `GlobalIlluminationDebugView.DdgiCascadeBlendWeight` through the GI debug controls. |
| 22 | `DdgiUpdateReasons` | Shows the same updated/not-updated binary as value 13. | Implement the full current scheduler reason palette. |
| 23 | `DdgiProbeSpheres` | Does not exist. | Append and implement as the dedicated probe-location sphere view requested by this plan. |

Values 9, 10, 11, 20, and 21 remain valid enum inputs so old settings and
diagnostic files can still be read. The central catalog described below marks
them `Retired` and resolves them to `None` with an explanatory message; they are
not active cycle entries.

### 2.3 Confirmed recent-change mismatches

The DDGI overlay was largely introduced in July 2026. The DDGI renderer body
has received only a small compatibility edit since then, while the production
DDGI architecture has changed materially.

1. `SimpleDdgiSchedulerMode.GpuResident` is now the production default.
   `IsProbeScheduledForUpdate` still queries `_probeQueued`, which is populated
   by the CPU queue builder and is not the resident scheduler's authority.
2. High and Ultra use `SparseNearRing`. A virtual probe can be nonresident or
   map to a physical page/slot unrelated to its virtual index.
3. Toroidal scrolling is enabled by default. The current overlay calculates
   `firstProbe + x + y*countX + z*countX*countY` and ignores each volume's
   physical offset. The marker position is logical, but the state index must use
   the same toroidal mapping as `SimpleDdgiProbeIndex`.
4. `DdgiPhysicalSlots` calls `ResolveDdgiPhysicalSlotColor` without consulting
   `LastVolumePaging`, the page table, mapping generation, or physical probe
   address. Its name is therefore misleading in the sparse path.
5. CPU classification/age state is deliberately unavailable in GPU modes.
   Reusing managed arrays for the new state views would display stale bootstrap
   or fallback data.
6. The renderer exposes only a volume count for DDGI overlay diagnostics. It
   cannot currently distinguish boxes, sampled markers, filtered markers,
   nonresident probes, or budget truncation.

### 2.4 Additional dead or misleading contracts

- `DebugOverlaySettings.ShowLabels` has no consumer.
- `DebugOverlaySettings.SelectedLightIndex` has no consumer.
- `RequiresCpuSnapshots` in `SampleInputController` duplicates renderer policy
  and can drift from the authoritative snapshot requirements.
- `RendererSettingsReference.md` lists only the original non-DDGI modes and
  does not document the actual cycle.
- `PrintDebugSettings` samples the previous debug draw list immediately after
  changing modes, so its line count does not prove that the newly selected mode
  rendered.

## 3. Final interactive contract

### 3.1 Curated forward cycle

The default forward cycle is:

```text
None
LightTiles
DirectionalShadowCascades
ReflectionProbeVolumes
DdgiProbeVolumes
DdgiProbeSpheres
DdgiProbeActivity
DdgiUpdatedProbes
DdgiProbeRelocation
DdgiProbeAge
DdgiPhysicalSlots
DdgiCascadeBounds
DdgiNewlyExposedCells
DdgiFrustumPriority
DdgiUpdateReasons
DecalVolumes
ObjectBounds
MeshletBounds
SelectedObject
None
```

Add `Ctrl+Shift+Keypad9` for reverse traversal. This makes the longer DDGI
section practical without changing the established forward shortcut.

Do not silently skip a mode merely because the current scene has no data. A
stable cycle is easier to learn and test. Instead, publish and print a concise
reason after the first frame using that mode, for example:

```text
Debug overlay: Reflection probe volumes — no-data (scene has 0 probes)
Debug overlay: DDGI probe age — unavailable (Simple DDGI is disabled)
Debug overlay: Selected object — no-data (select with Ctrl+Left/Right)
```

### 3.2 One source of truth

Add `DebugOverlayCatalog` under `Njulf.Rendering/Debugging`. Each descriptor
contains:

- stable enum value and display name;
- active-cycle/retired state and ordering;
- renderer kind: line, full-screen, DDGI probe, or none;
- whether CPU object snapshots are required;
- subsystem/data preconditions;
- a short legend and no-data guidance.

`SampleInputController`, `VulkanRenderer`, diagnostics formatting, and tests all
consume this catalog. Delete `NextDebugOverlay` and the sample-level
`RequiresCpuSnapshots` switch. Renderer code can still dispatch to specialized
handlers, but a catalog test must prove that every active descriptor has one.

### 3.3 Explicit per-frame result

Add a small immutable result, for example:

```csharp
public readonly record struct DebugOverlayFrameStatus(
    DebugOverlayMode Mode,
    DebugOverlayAvailability Availability,
    int PrimaryItemCount,
    int SecondaryItemCount,
    int DroppedItemCount,
    string Reason);
```

`DebugOverlayAvailability` is `Disabled`, `Rendered`, `NoData`, `Unavailable`,
or `Retired`. Store the result in `SceneRenderingData` and
`RendererDiagnostics`. The sample reporter prints only mode/status transitions,
not one line per frame.

This status replaces inference from a stale `DebugDraw.Snapshot().LineCount`.
Retain the existing detailed debug-line counters for performance diagnostics.

## 4. Implementation phases

### Phase 0: lock the contract before changing visuals

1. Add tests that enumerate the current cycle and demonstrate the four wholly
   unhandled values and the misleading DDGI aliases listed in this audit.
2. Add numeric compatibility tests for values 0 through 22.
3. Add value 23 as `DdgiProbeSpheres`; never insert it in the middle of the
   enum.
4. Introduce `DebugOverlayCatalog` and move cycling/snapshot requirements to it.
5. Mark values 9, 10, 11, 20, and 21 retired in the catalog. Avoid `[Obsolete]`
   warnings on persisted enum deserialization; use catalog metadata and comments
   instead.
6. Add `DebugOverlayFrameStatus` with a default `Disabled/None` state and wire it
   through diagnostics before implementing new drawing.

Exit criteria:

- active cycle order is tested as data rather than a source string;
- every active mode has a registered renderer kind;
- retired values deserialize and resolve safely;
- forward and reverse traversal are exact inverses.

### Phase 1: fix the missing non-DDGI visual modes

#### 1A. Forward+ light tiles

Add the originally planned full-screen debug overlay path rather than trying to
represent screen tiles with world-space lines.

1. Add a small `DebugOverlayPass` and `debug_overlay.vert/frag`, placed after
   the scene's opaque/transparent lighting data is available and before final
   tone mapping.
2. Read the existing tiled-light header buffer and use the same 16x16 tile
   coordinate calculation as Forward+ shading.
3. Render:
   - black/transparent for zero lights;
   - blue to green for low occupancy;
   - yellow to red near `MaxLightsPerTile`;
   - magenta for saturation/overflow;
   - a subtle one-pixel tile grid that remains correct after resize and
     resolution-scale changes.
4. Do not add a diagnostic readback just for the overlay. Populate the existing
   dead `DebugLightTileMaxCount` and `DebugLightTileAverageCount` fields from the
   already-computed `MaxLightsInAnyTile` and
   `AverageLightsPerNonEmptyTile`, or remove the duplicate fields.
5. Return `NoData` with `no local lights` when appropriate; the screen may then
   remain unmodified without appearing broken in diagnostics.

#### 1B. Directional shadow cascade frusta

1. In `BuildDebugOverlayDrawCommands`, read the active cascade count and the
   four matrices in `sceneData.ShadowData`.
2. Submit each valid light view-projection matrix to `DebugDrawList.Frustum`
   with the same stable cascade palette used by
   `ShadowDebugView.CascadeOverlay`.
3. Use the normal depth/x-ray setting and draw no invalid identity matrix.
4. Report cascade count and the existing per-cascade meshlet counts.
5. Describe `F2` as the receiver-space ownership/debug view and
   `Ctrl+Keypad9` value 2 as world-space light frusta so the two controls are not
   mistaken for duplicates.

#### 1C. Existing world overlays

1. Reflection probes: draw the influence shape and, where the data model exposes
   it, a second faded blend/falloff shape. Preserve selected-probe filtering.
2. Object, decal, meshlet, and selected-object modes: return `NoData` rather
   than a silent zero-command result.
3. Give meshlet bounds an explicit item cap in addition to the line cap and
   report both dropped counts.
4. Remove `MaterialInspection`, `PassTimings`, and `GpuMemory` from the active
   cycle and update their guidance:
   - `/` and the editor/material debug views for material inspection;
   - `Ctrl+F4` plus the diagnostics reporter for timestamps;
   - `Ctrl+F2` or `Ctrl+Keypad0` snapshots for GPU memory.

Exit criteria:

- values 1 and 2 visibly render in a suitable scene;
- all retained CPU-line modes report `Rendered`, `NoData`, or `Unavailable`;
- no active non-DDGI value falls through the renderer switch.

### Phase 2: add the probe-sphere location view

Implement `DdgiProbeSpheres` before the state-specific DDGI overhaul so the
requested placement tool is available as an independently useful slice.

1. Reuse the deterministic, per-volume marker-budget distribution, but make the
   budget account for segments per sphere rather than markers alone.
2. Use a low-cost wire sphere (8 to 12 segments per great circle) through the
   existing debug draw path for the first slice. A 768-marker cap with 8
   segments consumes 18,432 line segments, leaving room beneath the default
   65,536-line budget for volume boxes and x-ray duplication.
3. Set radius from spacing using the existing baseline
   `clamp(spacing * 0.08, 0.04, 0.20)`. Add one setting only if visual testing
   proves a scale override is necessary.
4. Distribute samples across every admitted volume and all three axes; do not
   exhaust the budget on the first/near volume.
5. Colour authored volumes distinctly and use stable near/mid/far ring colours.
6. Initially place spheres at logical positions. When Phase 3's authoritative
   state path is available, move a valid probe to its relocated centre and use
   logical position as the fallback. The dedicated relocation mode continues
   to show both endpoints and the connecting vector.
7. Draw the volume bounds faintly behind the spheres and report sphere count,
   sampled-out count, line count, and dropped count.

If CPU-expanded sphere lines exceed the measured debug CPU/GPU budget, replace
only the marker body with an instanced procedural three-ring sphere in the DDGI
probe pass from Phase 3. Do not increase the global debug-line budget to hide a
poor representation.

Exit criteria:

- the mode makes near, mid, far, and authored probe placement legible from a
  stationary and moving camera;
- sphere centres match the logical lattice before relocation integration;
- no default-budget line drops occur in the High and Ultra sample layouts;
- `None` and non-DDGI modes pay no sphere cost.

### Phase 3: build one authoritative DDGI probe-overlay data path

Do not add public accessors for each managed array. Create one bounded,
generation-tagged debug path that works for CPU-reference, GPU-resident, dense,
sparse, and toroidally scrolled configurations.

#### 3A. Canonical sampled identity

Add a compact per-frame instance record containing at least:

- volume index and logical x/y/z coordinate;
- virtual probe index after applying the volume's toroidal physical offset;
- logical world position and marker radius;
- snapshot frame serial, volume-table generation, scheduler-resource
  generation, and residency-resource generation.

Build at most the configured marker budget. Share the C#/GLSL coordinate rules
with the existing `CalculatePhysicalProbeLocalIndex`,
`SimpleDdgiProbeIndex`, and paging helpers; do not copy a fourth untested
formula into `VulkanRenderer`.

#### 3B. GPU-resident state resolution

Prefer a debug-only GPU probe pass over full-state CPU readback:

1. Upload the bounded sampled identities to a per-frame instance buffer.
2. In `SimpleDdgiProbeDebugPass`, read the canonical public probe state,
   resident scheduler state, volume/paging records, and residency page table.
3. Resolve sparse virtual-to-physical addresses with the same shader helpers and
   generation checks as the receiver/scheduler. A failed or stale mapping is
   nonresident/invalid; never fall back to an identity slot.
4. Render depth-tested and x-ray instances with the existing debug depth-mode
   semantics. A procedural wire sphere or small octahedron avoids CPU expansion
   and supports state colour in the same frame.
5. For `CpuReference`, bind the same public probe/update ABI and bypass only the
   resident scheduler-specific fields. Visual semantics must remain identical.
6. Declare all reads in `ProductionRenderPipelineDeclaration` so scheduler,
   publication, and residency writes receive explicit barriers before the
   graphics debug read.

If the chosen Vulkan path cannot safely expose a required state to graphics,
add a bounded compute gather into the instance buffer. Do not copy all 32,768
probe and scheduler records to the CPU every frame. Any optional CPU diagnostic
readback must be asynchronous, frames-in-flight ringed, fence-complete, and
generation-rejected when stale.

#### 3C. Current-update instances

`DdgiUpdatedProbes` and `DdgiUpdateReasons` should use the bounded admitted
update records, not a lookup against `_probeQueued`:

- CPU and GPU scheduler paths already share `GPUSimpleDdgiProbeUpdate` identity;
- validate queue, volume, physical mapping, and generation fields before draw;
- distinguish admitted/in-flight from successfully committed this frame;
- make full-ray, maintenance, and source-refresh work visually distinct;
- reject an old queue transaction instead of displaying it as current.

The GPU pass may draw directly from the admitted queue/indirect count or compact
valid debug instances first. It must not perform `sampleCount * queueCount`
linear searches in a fragment or vertex shader.

#### 3D. State-specific visual contracts

Use one documented palette with an explicit precedence order when a probe has
multiple flags.

| Mode | Required source and visual meaning |
| --- | --- |
| `DdgiProbeActivity` | Green active/published; red inactive; amber fresh; orange relocation pending; magenta unpublished/invalid generation; grey nonresident. Higher-risk invalid states take precedence over active green. |
| `DdgiUpdatedProbes` | Draw only current admitted or just-committed records. Blue full update, cyan maintenance/cached solve, violet source refresh; failed/stale transaction red. |
| `DdgiProbeRelocation` | Faint marker at logical centre, bright sphere at `logical + relocation`, and a line between them. Yellow means pending, red means inactive/invalid, green means valid relocated. |
| `DdgiProbeAge` | Use `Age` or resident `LastCommittedUpdateFrame` against the configured lifecycle latency target: green recent, yellow approaching target, red beyond target, grey unavailable/nonresident. Do not normalize independently per frame. |
| `DdgiPhysicalSlots` | Hash the resolved physical page and within-page slot. Grey means nonresident, magenta means stale mapping generation, and a stable colour must survive camera motion while ownership is unchanged. |
| `DdgiNewlyExposedCells` | Draw only probes carrying the current scroll-exposed lifecycle/reason state; optionally fade other probes to very low alpha. |
| `DdgiFrustumPriority` | Display the current scheduler visible/proximity classification. Rename only the user-facing label; preserve enum value/name compatibility. |
| `DdgiUpdateReasons` | Colour admitted records by `Fresh`, `ScrollExposed`, `RegionalDirty`, `GlobalDirty`, `Visible`, `Retry`, `RelocationRetry`, `SourceCacheInvalid`, `RoutineDue`, `ConvergencePending`, `InactiveRetry`, and current topology invalidation. Define deterministic precedence and report multi-reason records in counters. |

`DdgiCascadeBounds` remains CPU line geometry based on `LastVolumes`; it does
not require probe state and should not enter the GPU marker path.

Exit criteria:

- every state mode displays authoritative data in both `GpuResident` and
  `CpuReference`;
- toroidal movement does not swap a marker's state onto the wrong world cell;
- sparse nonresident cells never masquerade as dense physical slots;
- an old volume/scheduler/residency generation produces an explicit unavailable
  or stale result, not plausible-looking colours;
- `DdgiUpdatedProbes` agrees with admitted/committed scheduler counters.

### Phase 4: diagnostics, legends, and dead-setting cleanup

1. Add counters for DDGI volume boxes, requested samples, drawn markers,
   filtered markers, nonresident markers, stale mappings, state-unavailable
   markers, sphere segments/instances, and dropped markers.
2. Add per-update-reason counts for the currently visualized admitted records.
3. Store the active palette/legend text in `DebugOverlayCatalog` and print it
   once when the mode changes.
4. Do not make renderer correctness depend on text rendering. The console is
   the universal legend; the debug editor may show the same catalog/status data.
5. Remove `ShowLabels` and `SelectedLightIndex` from the active settings contract
   if no consumer is added in this work. If settings JSON compatibility requires
   them, retain them as ignored legacy properties with documentation rather than
   implying they work.
6. Update:
   - `RendererSettingsReference.md`;
   - `implementation/DDGIShortcuts-20260628.md`;
   - sample startup/help output;
   - editor debug controls if they enumerate overlay modes.
7. Correct terminology everywhere to `Ctrl+Keypad9`, while accepting
   `Ctrl+Num9` as the user-facing synonym.

Exit criteria:

- a screenshot plus its console/status record identifies the mode and legend;
- zero visual items always has a reason;
- documentation and the catalog enumerate the same active order.

## 5. Detailed validation plan

### 5.1 Unit and contract tests

Replace or supplement source-substring assertions in
`DebugToolingContractsTests` with executable contracts:

1. Every active catalog mode is unique, ordered, non-retired, and has a
   renderer kind/handler.
2. Every retired enum value resolves safely and is absent from forward/reverse
   traversal.
3. Existing values 0 through 22 and new value 23 remain fixed.
4. CPU snapshot requirements come from the catalog and cover object, meshlet,
   selected-object, and decal modes exactly once.
5. Forward and reverse cycles wrap through `None` correctly.
6. `DebugOverlayFrameStatus` defaults are shipping-safe.
7. Tile heatmap thresholds cover empty, low, near-capacity, saturated, and
   overflow tiles.
8. Shadow cascade matrix selection respects the active count and rejects
   invalid matrices.
9. Sphere sampling:
   - stays inside marker and line budgets;
   - distributes samples over x/y/z and every volume;
   - uses the expected radius clamps;
   - reports truncation.
10. Logical-to-state addressing matches the shader for non-zero toroidal
    offsets on every axis.
11. Dense, sparse-resident, sparse-nonresident, stale-generation, and remapped
    physical-page cases produce the expected identities/colours.
12. Update reason precedence and multi-reason accounting are deterministic.

### 5.2 Shader and render-graph tests

1. Compile the new debug shaders through the normal shader project/build path.
2. Add C#/GLSL struct and constant mirrors for any new instance/push-constant
   ABI.
3. Assert that the DDGI debug pass declares reads of params, probe state,
   scheduler/update records, receiver/publication state, and residency resources
   that it actually accesses.
4. Assert that the pass writes only scene colour and diagnostics and never
   mutates canonical DDGI state.
5. Verify disabled/`None` mode prevents pass execution and resource growth.

### 5.3 Runtime matrix

Run at minimum:

| Case | Required coverage |
| --- | --- |
| DDGI Low/Medium | Dense residency and `CpuReference` fallback semantics. |
| DDGI High/Ultra | Default `GpuResident` plus authoritative `SparseNearRing`. |
| Stationary camera | Stable sphere centres, slot colours, age, and status. |
| Multi-cell camera traversal | Toroidal scroll identity and newly exposed cells. |
| Sparse pressure | Resident/nonresident/stale-mapping colours and no invalid payload reads. |
| Dirty light/geometry event | Regional/global/source update-reason colours. |
| No DDGI, shadows, probes, decals, or selected object | Explicit `NoData`/`Unavailable` statuses for each relevant mode. |
| Resize and resolution-scale change | Light-tile grid remains aligned. |
| Debug depth modes | Depth-tested, always-visible, and x-ray sphere/frustum behaviour. |

For each active cycle value, capture the mode, availability, primary/secondary
item counts, dropped count, CPU record time, GPU time, and one image. The run
passes only if every zero-item frame has a non-empty reason.

### 5.4 Performance and safety gates

1. Measure the new light-tile and DDGI probe passes separately with completed
   GPU timestamps; do not fold them into an unattributed frame number.
2. At the default 768-marker cap, the probe overlay must remain bounded without
   line-buffer overflow or an all-probe CPU enumeration.
3. Cycling into a mode may allocate bounded debug resources lazily. Cycling
   back to `None` must stop their per-frame uploads/dispatches; normal startup
   with debug disabled allocates none of the new optional buffers.
4. No debug path may call `WaitForFences`, queue-idle, device-idle, or map a
   buffer before its frame completion token.
5. Vulkan validation must remain clean across mode changes, resize, DDGI
   reconfiguration, scheduler fallback/re-entry, and sparse resource
   replacement.

## 6. Primary implementation targets

Expected files to add or change:

- `Njulf.Rendering/Debugging/DebugOverlayMode.cs`
- `Njulf.Rendering/Debugging/DebugOverlaySettings.cs`
- `Njulf.Rendering/Debugging/DebugOverlayCatalog.cs` (new)
- `Njulf.Rendering/Debugging/DebugOverlayFrameStatus.cs` (new)
- `Njulf.Rendering/Debugging/DebugDrawList.cs`
- `Njulf.Rendering/Pipeline/DebugOverlayPass.cs` (new, light tiles)
- `Njulf.Rendering/Pipeline/SimpleDdgiProbeDebugPass.cs` (new if the
  GPU-resident path is selected)
- `Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs`
- `Njulf.Rendering/Pipeline/RenderGraphResource.cs` if a bounded probe-instance
  resource is declared explicitly
- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Rendering/Data/SceneRenderingData.cs`
- `Njulf.Rendering/Data/RendererDiagnostics.cs`
- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs` only for shared
  debug-address/snapshot exposure; do not expose its mutable arrays
- `Njulf.Rendering/Resources/SimpleDdgiGpuScheduler.cs` only if a bounded
  debug instance/gather binding is needed
- `Njulf.Rendering/VulkanRenderer.cs`
- `Njulf.Shaders/debug_overlay.vert` (new)
- `Njulf.Shaders/debug_overlay.frag` (new)
- `Njulf.Shaders/debug_ddgi_probe.vert` and
  `Njulf.Shaders/debug_ddgi_probe.frag` (new if using GPU instances)
- `NjulfHelloGame/SampleInputController.cs`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`
- `Njulf.Tests/DebugToolingContractsTests.cs`
- focused new overlay/address/palette tests where that keeps the existing test
  fixture manageable
- `RendererSettingsReference.md`
- `implementation/DDGIShortcuts-20260628.md`

Do not mix the implementation with the current transport/storage ABI work
unless a shared structure genuinely needs a new debug field. Probe overlay
data is a consumer of canonical state, not a reason to alter transport
correctness or publication ownership.

## 7. Completion checklist

- [x] `Ctrl+Keypad9` and `Ctrl+Shift+Keypad9` traverse the tested catalog.
- [x] No active mode lacks a renderer implementation.
- [x] Retired values remain numerically compatible and are not cycled.
- [x] `LightTiles` is a real screen-space occupancy heatmap.
- [x] `DirectionalShadowCascades` draws actual cascade frusta.
- [x] `DdgiProbeSpheres` draws bounded, readable world-space probe spheres.
- [x] Stateful DDGI overlays use authoritative GPU/CPU state for the active
      scheduler and residency mode.
- [x] Toroidal and sparse identities match the production shader helpers.
- [x] `DdgiUpdatedProbes` and `DdgiUpdateReasons` use admitted transaction data,
      not `_probeQueued` in `GpuResident`.
- [x] Every zero-item result explains why it is empty.
- [x] Debug-disabled execution has no new allocation, upload, dispatch, or draw.
- [x] Tests cover catalog completeness, addressing, palettes, budgets, shaders,
      render-graph resources, and numeric compatibility.
- [ ] Runtime validation passes the dense/sparse, CPU/GPU, stationary/moving,
      resize, fallback, and no-data matrix.
- [x] Settings reference, shortcut guide, help text, and diagnostics agree with
      the final cycle.

Targeted validation on 2026-08-14 built the shader/C# solution with zero
warnings and passed 63 overlay, tooling, ABI, and render-graph contract tests.
The initial complete test assembly finished with 2,796 passed, one skipped, and
one unrelated failure: `AssetTool_ReportCommand_WritesJsonReport` timed out
waiting five minutes for `Njulf.AssetTool`. The initial three-frame Vulkan
startup smoke also exposed a pipeline-creation stall before the first frame.

The startup regression was corrected and revalidated on 2026-08-15. Exit code
`-1,073,741,510` is Windows `0xC000013A` (`STATUS_CONTROL_C_EXIT`), emitted when
the stalled process was stopped rather than by an application crash. Repeated
managed/native stack samples placed the stall in `Vk.CreateComputePipelines`
while `SimpleDdgiTransportAuditPass.Initialize` eagerly created all twelve
storage/guiding ray and reduction variants after the overlay shader changes
invalidated the whole GI shader-bundle cache. Debug now compiles only the audit
shader family with `-Os` while retaining detailed counters, and startup
prewarms only the active storage pair plus its guided pair when directional
guiding is admitted. Other storage variants remain available through the
existing lazy pipeline cache.

Both Debug and Release solution builds completed with zero warnings and zero
errors. `spirv-val --target-env vulkan1.3` accepted all twelve generated audit
variants and the generic audit artifact. The focused audit/trace/overlay suite
passed 45 of 45 tests, and the complete assembly passed 2,805 tests with one
hardware-gated skip and no failures. With the application GI cache removed, a
three-frame Debug startup reached `FirstFrame.End` in 7.23 seconds and exited
zero; the immediate warm run reached it in 5.04 seconds and also exited zero.
Cold/warm GI pipeline creation measured 208,700/19,682 microseconds with zero
render-critical pipeline creations. The pre-existing application cache was
restored after the check. The broader manual visual runtime matrix remains
open.
