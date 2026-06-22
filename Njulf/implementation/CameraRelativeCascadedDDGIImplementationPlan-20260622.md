# Camera-Relative Cascaded DDGI Implementation Plan

Date: 2026-06-22
Status: Planning

## Goal

Replace the current scene-bounds DDGI behavior with a camera-relative, bounded-memory DDGI system:

- Probes follow the camera, so world size does not determine probe count.
- Probe density is highest near the camera and becomes progressively sparser with distance.
- Update quality is biased toward the camera frustum with an expanded safety region.
- Areas behind and outside the frustum retain enough coverage and update cadence to avoid obvious quality drops during turns, strafes, backtracking, and camera cuts.
- Normal camera movement does not clear or reallocate DDGI resources every frame.

## Current Repo Context

The existing DDGI path is already present but is authored-volume oriented:

- `Njulf.Core/Scene/GlobalIlluminationProbeVolume.cs` defines a fixed world-space probe volume.
- `Njulf.Rendering/VulkanRenderer.cs` calls `ResolveDdgiProbeVolumes`, falls back to `CreateDefaultForBounds(EstimateSceneProbeBounds(scene))`, uploads volumes, and schedules a contiguous probe update range.
- `Njulf.Rendering/Resources/DdgiProbeVolumeManager.cs` owns DDGI buffers and atlases. Its resource signature includes volume origin and size, so a moving volume would currently trigger persistent resource initialization.
- `Njulf.Rendering/Pipeline/DdgiUpdatePass.cs` dispatches update work using `StartProbeIndex` and `ProbesToUpdate`.
- `Njulf.Shaders/ddgi_update.comp` maps a global probe index to a probe position by scanning volumes and assuming a static linear grid.
- `Njulf.Shaders/forward.frag` samples the first containing volume, uses trilinear sampling inside that one volume, and fades out near volume edges.

This means simply changing the default volume origin to the camera each frame would be incorrect: probe slots would represent different world positions, history would be reused for the wrong cells, and resources would be cleared or reinitialized too often.

## Architecture Decision

Implement camera-relative DDGI as world-axis-aligned clipmap cascades with toroidal probe storage.

Key rules:

1. Use several uniform DDGI grids, called cascades or clipmap levels.
2. The nearest cascade has the smallest spacing. Each farther cascade has larger spacing and covers a larger world region.
3. Cascade origins are snapped to their probe spacing. The lattice follows the camera in cell-sized increments, not continuous sub-cell movement.
4. Do not rotate the probe grids with the camera. Camera rotation should affect update priority, not probe coordinates. Rotating DDGI grids causes avoidable history churn, shimmer, and large update bursts.
5. Store each cascade in fixed physical probe slots and use toroidal addressing when the camera crosses grid cells.
6. Only newly exposed probe slabs are invalidated and prioritized for update. Overlapping cells keep their existing irradiance and visibility history.
7. Sampling uses the finest valid cascade and blends to coarser cascades near cascade edges, when fine probes are not initialized, or when fine coverage is poor.
8. Frustum focus is primarily an update-budget policy. Dense near-camera spacing handles local quality; frustum weighting determines which probes converge first and refresh most often.

## Recommended Initial Presets

Start conservative, then tune with captures:

| Preset | Cascades | Probe Grid Per Cascade | Base Spacing | Spacing Scale | Approx Probes | Intended Use |
| --- | ---: | --- | ---: | ---: | ---: | --- |
| Low | 3 | 16 x 8 x 16 | 1.5 m | 2.5 | 6,144 | Low-end fallback |
| High | 4 | 24 x 10 x 24 | 1.25 m | 2.0 | 23,040 | Default target |
| Ultra | 4 | 32 x 12 x 32 | 1.0 m | 2.0 | 49,152 | High-end target |

Keep total active probes below `DdgiProbeVolumeManager.AbsoluteMaxProbeCount` until the manager is intentionally resized. Keep the number of generated cascades plus authored volumes below `AbsoluteMaxVolumeCount`.

## Step-by-Step Implementation

### Step 1 - Add Settings And Feature Flags

Add camera-relative DDGI settings under `GlobalIlluminationSettings`.

Required fields:

- `DdgiCameraRelativeEnabled`
- `DdgiClipmapCascadeCount`
- `DdgiClipmapProbeCountX`
- `DdgiClipmapProbeCountY`
- `DdgiClipmapProbeCountZ`
- `DdgiClipmapBaseSpacing`
- `DdgiClipmapSpacingScale`
- `DdgiClipmapEdgeBlendFraction`
- `DdgiClipmapSafetyMarginCells`
- `DdgiFrustumPriorityWeight`
- `DdgiOutOfFrustumMinimumUpdateFraction`
- `DdgiNewProbeUpdateBoost`
- `DdgiProbeUpdateTimeBudgetMilliseconds`
- `DdgiTeleportResetDistance`
- `DdgiCameraCutResetEnabled`

Production requirements:

- Clamp all values defensively.
- Add JSON serialization in `RenderSettings.RenderSettingsFile.GlobalIlluminationFile`.
- Preserve existing authored-volume behavior behind the feature flag.
- Default the feature on only after validation. During implementation, default it off or behind a quality preset switch.

### Step 2 - Introduce CPU Clipmap State

Create a new runtime component, for example `CameraRelativeDdgiClipmapController`, owned by `VulkanRenderer` or `DdgiProbeVolumeManager`.

Per cascade state:

- Cascade index.
- Probe counts.
- Probe spacing.
- World-space logical grid minimum cell.
- Snapped world origin.
- Previous snapped origin.
- Ring offset in X/Y/Z.
- Physical first probe index.
- Per-cell initialization state or age.
- Per-cell last update frame.
- Scroll delta for the current frame.
- Dirty slab ranges created by camera movement.

Camera movement behavior:

1. Convert camera world position to cascade cell coordinates with `floor(cameraPosition / spacing)`.
2. Select a logical grid minimum so the camera is centered in the cascade, with configurable vertical bias if needed.
3. Snap origin to `gridMinCell * spacing`.
4. If `gridMinCell` changes, update the ring offset by the cell delta.
5. Mark only newly exposed slabs as uninitialized and dirty.
6. If movement exceeds the teleport threshold, invalidate the affected cascade or all cascades and enter warm-up mode.

Do not recalculate or clear all probes for ordinary camera movement.

### Step 3 - Replace Scene-Bounds Default Volumes With Frame Layout Generation

Replace `ResolveDdgiProbeVolumes(scene, includeDefaultVolume)` with a layout builder:

- `BuildDdgiFrameLayout(scene, camera, settings)`

The frame layout should output:

- Authored DDGI volumes that remain enabled.
- Camera-relative cascades when `DdgiCameraRelativeEnabled` is true.
- A stable total physical probe count.
- Per-volume metadata required by shaders.
- Dirty probe requests caused by scene changes, light changes, material changes, and camera scrolling.

Rules:

- Camera-relative cascades should be default fallback coverage.
- Authored volumes should remain supported for hero areas, special interiors, or manually placed high-quality GI.
- Authored volumes and camera clipmaps must blend predictably. Authored local volumes should generally win when they are more detailed and valid.
- The fallback `CreateDefaultForBounds(EstimateSceneProbeBounds(scene))` should be disabled when camera-relative DDGI is active.

### Step 4 - Make DDGI Resource Lifetime Independent From Camera Origin

Update `DdgiProbeVolumeManager.CreateResourceSignature` logic so normal camera-relative origin changes do not reinitialize persistent resources.

The signature should include:

- Physical probe capacity.
- Cascade count.
- Probe counts per cascade.
- Atlas texel layout.
- Rays-per-probe policy.
- Enabled relocation/classification modes.
- Any shader data layout version.

The signature should not include:

- Snapped origin.
- Logical grid minimum cell.
- Ring offset.
- Per-frame frustum priority.

Production requirement:

- Ordinary camera walking should not call `InitializePersistentResources`.
- Resource reinitialization is allowed for quality preset changes, probe grid dimension changes, atlas format changes, and DDGI mode changes.

### Step 5 - Extend GPU DDGI Metadata For Clipmaps

Extend `GPUDdgiProbeVolume` and matching GLSL layout constants to include clipmap-specific data.

Required metadata:

- Volume kind: authored or camera clipmap.
- Cascade index.
- Probe counts.
- Probe spacing.
- Snapped origin.
- Logical grid minimum cell.
- Ring offset.
- Edge blend distance or fraction.
- Physical first probe index.
- Flags for initialized, camera-relative, authored-priority, and debug display.

Implementation notes:

- Update `Njulf.Rendering/Data/GPUStructs.cs`.
- Update `Njulf.Rendering/Data/GlobalIlluminationProbeVolumeData.cs`.
- Update `Njulf.Shaders/common.glsl` sizes and offsets.
- Add tests or assertions that C# struct sizes match GLSL constants.
- Keep alignment explicit. Avoid relying on accidental `Vector4` packing.

### Step 6 - Replace Contiguous Probe Scheduling With Explicit Update Requests

The current update path uses `StartProbeIndex` plus `ProbesToUpdate`. That is not enough for clipmap scrolling, dirty bounds, frustum priority, and starvation prevention.

Implement an explicit `GPUDdgiProbeUpdateRequest` queue populated on the CPU.

Each request should include:

- Physical probe index.
- Volume or cascade index.
- Logical cell coordinate or enough data to reconstruct world position.
- Priority bucket.
- Reason flags: new cell, dirty bounds, visible frustum, age refresh, teleport warm-up, authored volume.

Scheduler order:

1. Newly exposed near-cascade cells.
2. Dirty scene/light/material bounds intersecting near cascades.
3. Probes inside expanded current frustum.
4. Probes near the camera.
5. Probes with high age or low confidence.
6. Behind-camera and outside-frustum safety refresh.
7. Far cascades.

Production requirements:

- Enforce a hard max update count per frame.
- Enforce an optional GPU time budget using existing timestamp diagnostics.
- Guarantee no-starvation: every active probe must eventually refresh even if it is outside the frustum.
- Keep a reserved update fraction for outside-frustum and behind-camera probes, for example 15 to 25 percent.
- During teleport or first activation, enter a warm-up mode that temporarily increases update budget if frame time allows.

### Step 7 - Update `ddgi_update.comp` For Request-Based Updates

Modify the update shader so each workgroup reads one update request.

Required behavior:

1. Read request `N` from `DdgiProbeUpdateQueueBuffer`.
2. Resolve cascade metadata.
3. Convert the request logical cell to world-space probe position.
4. Convert logical cell to physical probe slot through ring addressing.
5. Trace rays from the resolved world-space probe position.
6. Write irradiance, visibility, relocation, classification, confidence, and age into the physical slot.

Important detail:

- The physical probe index must be stable for atlas writes.
- The world-space probe position must come from the request and current cascade logical mapping.
- Relocation and classification state for newly exposed cells must be reset before reuse.

### Step 8 - Add Toroidal Addressing Helpers In CPU And Shader Code

Implement one authoritative mapping formula and mirror it in tests:

```
relative = logicalCell - gridMinCell
wrapped = (relative + ringOffset) mod probeCount
physicalProbeIndex = firstProbe + wrapped.x
    + wrapped.y * probeCountX
    + wrapped.z * probeCountX * probeCountY
```

Edge cases:

- Negative logical cells.
- Large world coordinates.
- Scroll deltas larger than one cell.
- Scroll deltas larger than the entire cascade dimension.
- Teleports.
- Probe counts of 2 on any axis.

Production requirement:

- Add unit tests for CPU mapping.
- Add a debug mode that visualizes logical cell age and physical slot reuse.

### Step 9 - Rewrite Forward DDGI Sampling For Cascades

Replace `ReadDdgiContainingVolume` and `SampleDdgiIrradiance` behavior in `forward.frag`.

New sampling behavior:

1. Find all candidate DDGI volumes containing the shaded point.
2. Prefer the finest spacing with valid coverage.
3. Sample the 8 neighboring logical probes using toroidal addressing.
4. Compute coverage from probe confidence, classification, visibility support, and cascade edge fade.
5. Near cascade edges or low-coverage areas, sample the next coarser cascade.
6. Blend fine and coarse cascades using normalized weights.
7. Fall back to environment or SSGI only for missing coverage.

Rules:

- Never fade to black solely because a fine cascade reaches its edge.
- Do not double-count indirect light when two cascades overlap.
- Use coarser cascades as continuity fallback, not additive light.
- Preserve authored local volume priority where applicable.

### Step 10 - Implement Frustum-Focused Update Priority

Use camera frustum focus as a scheduling heuristic, not as rotating grid geometry.

For each probe candidate, compute:

- Distance-to-camera score.
- Cascade score.
- Whether the probe is inside the current frustum.
- Whether it is inside an expanded frustum with guard bands.
- Whether it is behind the camera but within the safety radius.
- Whether it intersects recent dirty bounds.
- Probe age.
- Probe confidence.
- Camera velocity prediction score.

Recommended policy:

- Expanded frustum gets strong priority.
- Current frustum gets strongest priority.
- A side and rear shell around the camera always receives a minimum update fraction.
- Far cascades update slower but continuously.
- Fast camera motion should bias updates ahead of velocity, not only view direction.
- Camera rotation should increase priority for newly visible regions without invalidating the grid.

### Step 11 - Handle Camera Cuts, Teleports, And Large Worlds

Add camera movement classification:

- Normal movement: scroll affected cascades by integer cells.
- Fast movement: increase new-cell priority and temporarily reduce far-cascade work.
- Teleport: invalidate cascades whose overlap is too small to trust.
- Camera cut or projection change: reset view-priority history but keep world-space probe data if the camera did not teleport.

Large-world requirements:

- Use integer logical cells for clipmap coordinates.
- Avoid precision loss by computing snapped origins from integer cells.
- If the engine later adds floating origin support, make DDGI cell coordinates floating-origin aware.

### Step 12 - Preserve And Improve Dirty Bounds Integration

Keep existing scene/light/material dirty tracking, but target it to clipmap cells.

Changes:

- Convert dirty bounds to affected logical cell ranges per cascade.
- Prioritize dirty cells by cascade spacing and camera/frustum relevance.
- For directional light changes, avoid blindly dirtying all cascades at full priority every frame. Use warm-up waves and age refresh unless the light changed materially.
- For moved static objects, update cells intersecting previous and current bounds plus a padding radius based on cascade spacing and max ray distance.

### Step 13 - Add Probe State Quality Metrics

Extend probe state or diagnostics with:

- Initialized flag.
- Last update frame.
- Age in frames.
- Ray hit confidence.
- Irradiance confidence.
- Visibility confidence.
- Classification active/inactive state.
- Relocation offset magnitude.
- Last update reason.

Use these metrics for:

- Scheduler priority.
- Debug overlays.
- Budget health reports.
- Sampling coverage.

### Step 14 - Add Debug Views And Tooling

Add or extend debug overlays:

- Cascade bounds and spacing.
- Probe age heatmap.
- Newly exposed cells.
- Frustum-priority cells.
- Outside-frustum safety refresh cells.
- Physical slot reuse/ring offset.
- Cascade blend weights.
- Fine vs coarse cascade selected for the pixel.
- DDGI coverage before fallback.
- Update reason visualization.

Update diagnostics:

- `RendererDiagnostics` DDGI fields should include cascade count, scroll count, new probes, stale probes, average probe age, max probe age, frustum update percentage, outside-frustum update percentage, and resource reinitialization count.
- `RenderBudgetEvaluator` should fail or warn when DDGI does full persistent clears during ordinary camera movement.

### Step 15 - Integrate With Existing Hybrid GI

Define clear composition rules:

- DDGI provides stable low-frequency indirect diffuse.
- SSGI provides near-contact, screen-visible detail.
- Environment fallback fills DDGI coverage holes.

Forward shader behavior should remain:

- Prefer DDGI where coverage is valid.
- Blend SSGI/contact GI near the camera where available.
- Suppress SSGI double-counting if DDGI already covers the same low-frequency energy.
- Use environment fallback only when DDGI coverage is missing or intentionally disabled.

### Step 16 - Performance And Memory Budgeting

Required budget controls:

- Max active probes.
- Max update requests per frame.
- Max rays per probe by cascade.
- Optional per-cascade rays-per-probe.
- Optional update budget in milliseconds.
- Atlas memory budget.
- Async compute enablement.

Recommended initial update policy:

- Near cascade: update every probe within 1 to 2 seconds during steady movement.
- Mid cascades: update every 3 to 8 seconds.
- Far cascades: update every 8 to 20 seconds.
- New cells: update as soon as possible, prioritizing the nearest cascade.

Production requirement:

- Use GPU timings to adapt update counts downward when DDGI exceeds budget.
- Do not let DDGI starve all other async compute work.
- Keep descriptor registrations stable across camera movement.

### Step 17 - Testing Plan

Unit tests:

- Clipmap origin snapping.
- Ring offset updates for positive and negative movement.
- Logical-to-physical probe mapping.
- Newly exposed slab detection.
- Dirty bounds to cell range conversion.
- Scheduler priority ordering.
- No-starvation guarantees.
- Resource signature stability under camera movement.
- Resource signature changes under preset changes.

Shader validation:

- Compile all affected GLSL shaders.
- Validate C# and GLSL struct sizes and offsets.
- RenderDoc capture for one frame after scroll and one frame after teleport.

Integration tests:

- Slow camera walk through a small scene.
- Fast sprint through a large scene.
- 180-degree turn in place.
- Backwards movement through already visited space.
- Teleport across the scene.
- Indoor-to-outdoor transition.
- Directional light change.
- Moving emissive or bright object.
- Empty scene fallback.
- Scene with authored local DDGI volume plus camera clipmaps.

Acceptance criteria:

- No full DDGI resource clear during ordinary camera movement.
- No visible black GI band at cascade edges.
- No obvious quality collapse on quick turns.
- New near-camera probes converge within the configured warm-up budget.
- Far GI remains stable and lower-frequency, not missing.
- DDGI GPU time remains inside the selected budget profile.
- Memory remains bounded and independent of scene size.

### Step 18 - Rollout Plan

1. Land settings and CPU clipmap state behind `DdgiCameraRelativeEnabled`.
2. Generate camera-relative cascades but keep old sampling path disabled for comparison.
3. Make resource lifetime stable under camera movement.
4. Implement request-based update scheduling.
5. Implement shader toroidal addressing and update requests.
6. Implement multi-cascade sampling and edge blending.
7. Enable frustum-priority scheduling.
8. Add debug overlays and diagnostics.
9. Tune presets using captures and long camera paths.
10. Make camera-relative DDGI the default after validation.

## Main Risks And Mitigations

Risk: Probe history is reused for the wrong world cell.

Mitigation: Use explicit logical cell coordinates, ring offsets, and reset newly exposed physical slots before reuse.

Risk: Cascades pop or darken at boundaries.

Mitigation: Blend to coarser cascades in overlap regions and use coverage-aware fallback.

Risk: Frustum-focused updates make quick turns visibly worse.

Mitigation: Reserve update budget for rear and side safety shells, include camera velocity prediction, and use coarser cascades as stable fallback.

Risk: Moving the camera causes per-frame buffer clears.

Mitigation: Exclude dynamic origin and ring offset from resource signatures.

Risk: Shader and C# DDGI layouts drift.

Mitigation: Add layout tests and keep size/offset constants updated with every struct change.

Risk: Update budget is consumed by far cascades while near new probes are stale.

Mitigation: Use priority buckets with hard reservations for new near-camera cells.

## Definition Of Done

The implementation is production-ready when:

- DDGI probe count and memory are independent of world bounds.
- Probe grids follow the camera through snapped clipmap scrolling.
- Near-camera GI has visibly higher detail than far GI.
- Frustum-visible probes converge fastest.
- Behind-camera and outside-frustum probes remain good enough for natural turns and backtracking.
- Ordinary camera movement does not reinitialize DDGI persistent resources.
- Cascades blend without visible bands, flicker, or black falloff.
- Teleports and camera cuts have explicit warm-up behavior.
- Existing authored DDGI volumes still work or are intentionally deprecated with migration notes.
- Diagnostics expose enough information to debug update starvation, stale probes, cascade blending, and resource churn.
