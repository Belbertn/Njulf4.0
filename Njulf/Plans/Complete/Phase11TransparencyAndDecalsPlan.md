# Phase 11: Transparency And Decals Detailed Implementation Plan

Goal: make alpha materials, geometry decals, and future projected decals reliable enough for production content. Phase 11 should turn the current first-generation blend path into a deterministic, inspectable, configurable transparency and decal system that handles glTF `OPAQUE`, `MASK`, and `BLEND` materials correctly, avoids decal z-fighting, preserves depth and shadow correctness, and provides a clear path to projected decals and weighted blended OIT only where they are worth the cost.

## Recommendation

Implement Phase 11 as a production hardening and feature expansion phase, not a rewrite.

The renderer already has the important first slice:
- glTF alpha mode and cutoff import.
- material alpha metadata packed into `GPUMaterialData.NormalScaleBias`.
- `MaterialRenderMode` classification.
- separate opaque/masked and transparent meshlet draw buffers.
- `TransparentForwardPass`.
- standard source-alpha blending.
- deterministic transparent meshlet sorting.
- transparent pass diagnostics and sample toggle through `SampleInputController`.

Build on that baseline in this order:
1. harden masked material depth, shadows, and alpha discard.
2. add explicit transparency/decal render settings, debug views, and sample input controls.
3. add geometry decal depth bias and layering controls.
4. add projected decals only after geometry decals are stable.
5. add weighted blended OIT only if real content demonstrates sorted transparency is insufficient.

Do not start with OIT or a projected decal renderer. Those systems solve different problems and add shader, memory, and ordering complexity. The production-critical gap is making alpha-tested depth and decal behavior stable, diagnosable, and content-authorable.

## Assumptions

Phase 11 assumes:
- Phase 2 HDR scene color and tone mapping are complete.
- Phase 3 bloom can receive transparent emissive output through the HDR scene color.
- Phases 4 and 5 shadow passes exist and currently exclude `BLEND` materials from shadow casters.
- Phase 7 AO and Phase 10 reflections may sample depth generated before transparent rendering.
- `ForwardPlusPass` renders opaque and masked meshlets into `RenderTargetManager.SceneColor`.
- `TransparentForwardPass` loads the existing HDR scene color and depth-tests against the depth prepass without writing depth.
- `DepthPrePass` currently draws `sceneData.OpaqueMeshletCount`, which includes opaque and masked materials but uses no fragment shader for masked alpha testing.
- `forward.frag` already discards masked fragments and near-zero blend fragments.
- `SampleInputController.cs` is the sample app control surface for renderer feature toggles and debug cycling.

## Current Baseline

Relevant current files:
- `Njulf.Assets/ModelImporter.cs`: reads glTF `alphaMode`, `alphaCutoff`, and `doubleSided`.
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`: packs alpha mode, cutoff, and double-sided flag into `GPUMaterialData.NormalScaleBias`.
- `Njulf.Rendering/Data/MaterialRenderMode.cs`: decodes material render mode from GPU material data.
- `Njulf.Rendering/Data/SceneDataBuilder.cs`: classifies visible objects, builds opaque and transparent meshlet command lists, excludes `BLEND` materials from shadow meshlet lists, and sorts transparent meshlets back-to-front with stable tie-breakers.
- `Njulf.Rendering/Pipeline/DepthPrePass.cs`: records opaque/masked depth tasks.
- `Njulf.Rendering/Pipeline/ForwardPlusPass.cs`: records opaque/masked forward tasks.
- `Njulf.Rendering/Pipeline/TransparentForwardPass.cs`: records transparent blend tasks after opaque lighting.
- `Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs`: creates depth, shadow-depth, opaque forward, and transparent forward pipelines.
- `Njulf.Shaders/forward.frag`: handles alpha cutoff and blend alpha output.
- `Njulf.Shaders/depth.task` and `Njulf.Shaders/depth.mesh`: provide depth-only mesh shader path.
- `NjulfHelloGame/SampleInputController.cs`: includes `F9` transparent pass toggle but no decal, mask, OIT, or transparency debug controls yet.

The older completed plan `Plans/Complete/BlendMaterialsAndDecalsImplementationPlan.md` should be treated as historical context for the first blend-material slice. Phase 11 should supersede it with production-grade details.

## Target Frame Shape

Target normal frame order after Phase 11:
1. `DirectionalShadowPass`
2. `SpotShadowPass`
3. `PointShadowPass`
4. `DepthPrePass`
5. optional `MaskedDepthPass` if split from solid depth
6. `HiZBuildPass`
7. optional AO normal/depth preparation
8. `AmbientOcclusionPass`
9. `AmbientOcclusionBlurPass`
10. `TiledLightCullingPass`
11. `ForwardPlusPass`
12. `SkyboxPass`
13. optional `GeometryDecalPass` if decals are split from transparent geometry
14. `ProjectedDecalPass` if enabled
15. `TransparentForwardPass`
16. optional `WeightedOitCompositePass` if weighted OIT is enabled
17. `FogPass`
18. `BloomPass`
19. `ToneMapCompositePass`
20. `AntiAliasingPass`
21. Present

Recommended first implementation keeps geometry decals inside `TransparentForwardPass` and adds a separate projected decal pass later.

## Scope

Phase 11 includes:
- correct masked-material depth prepass behavior.
- correct masked-material shadow caster behavior.
- transparent pass production settings and debug views.
- deterministic transparent sorting policy with documented limits.
- geometry decal depth bias controls.
- material-level blend/decal metadata that is explicit on CPU, not only inferred from packed GPU vectors.
- sample app controls for transparency, masked alpha, decal bias, and debug views.
- diagnostics for alpha modes, decal counts, sort strategy, OIT mode, and pass timings.
- optional projected decals for dynamic bullet holes, grime, blood, scorch marks, and gameplay markings.
- optional weighted blended OIT after regular sorted transparency has proven insufficient.
- validation scenes and tests for alpha-mask foliage, fences, glass panes, Sponza dirt decals, and overlapping transparent objects.

Phase 11 does not include:
- full physically correct order-independent transparency.
- ray traced transparency.
- transmissive glass from `KHR_materials_transmission`.
- refractive caustics.
- volumetric transparency or particles. Particle rendering belongs in Phase 13.
- full editor UI for decal placement. This phase should expose data and debug hooks that an editor can use later.

## Core Design Decisions

1. Keep `OPAQUE` and `MASK` in the opaque draw list. `MASK` should write correct depth after alpha testing and should be usable for foliage, chain-link fences, grates, and cutout props.
2. Keep `BLEND` out of the depth prepass, Hi-Z occlusion depth, AO occluder depth, and normal shadow caster lists unless a specific transparent-shadow approximation is enabled.
3. Treat geometry decals as transparent geometry first. They are already authored in glTF and can use the existing transparent pass.
4. Add decal-specific controls as material metadata instead of asset-name hacks.
5. Preserve deterministic sorting. Equal-depth transparent content must not flicker between frames.
6. Prefer sorted alpha blending for normal game content. Add weighted blended OIT only as an optional quality mode.
7. Make every mode visible in diagnostics and RenderDoc through named passes, named pipelines, and debug views.

## Settings

Add `TransparencySettings` and `DecalSettings` under `RenderSettings`.

Suggested API:

```csharp
public enum TransparencyMode : uint
{
    SortedAlphaBlend = 0,
    WeightedBlendedOit = 1
}

public enum TransparencyDebugView : uint
{
    None = 0,
    AlphaMode = 1,
    AlphaValue = 2,
    AlphaCutoff = 3,
    TransparentSortOrder = 4,
    Overdraw = 5,
    WeightedOitAccumulation = 6,
    WeightedOitRevealage = 7
}

public enum DecalDebugView : uint
{
    None = 0,
    GeometryDecalMask = 1,
    DecalLayer = 2,
    DecalDepthBias = 3,
    ProjectedDecalVolume = 4,
    ProjectedDecalAtlas = 5
}

public sealed class TransparencySettings
{
    public bool Enabled { get; set; } = true;
    public TransparencyMode Mode { get; set; } = TransparencyMode.SortedAlphaBlend;
    public TransparencyDebugView DebugView { get; set; } = TransparencyDebugView.None;
    public bool ReceiveShadows { get; set; } = true;
    public bool SampleReflections { get; set; } = true;
    public bool SortPerMeshlet { get; set; } = true;
    public int MaxTransparentMeshlets { get; set; } = 262144;
    public float AlphaDiscardThreshold { get; set; } = 0.001f;
}

public sealed class DecalSettings
{
    public bool GeometryDecalsEnabled { get; set; } = true;
    public bool ProjectedDecalsEnabled { get; set; }
    public DecalDebugView DebugView { get; set; } = DecalDebugView.None;
    public float GeometryDepthBias { get; set; } = 0.0005f;
    public float GeometrySlopeScaledDepthBias { get; set; } = 0.0f;
    public int MaxProjectedDecals { get; set; } = 256;
    public int MaxProjectedDecalsPerTile { get; set; } = 64;
    public int MaxProjectedDecalsPerPixel { get; set; } = 8;
}
```

Recommended constraints:
- `MaxTransparentMeshlets`: `0` to a documented memory budget.
- `AlphaDiscardThreshold`: `0.0` to `0.05`.
- `GeometryDepthBias`: `0.0` to `0.01`.
- `GeometrySlopeScaledDepthBias`: `0.0` to `4.0`.
- `MaxProjectedDecals`: `0` to `4096`.
- `MaxProjectedDecalsPerTile`: `0` to `256`.
- `MaxProjectedDecalsPerPixel`: `0` to `32`.

Tests:
- defaults preserve current sorted transparent rendering.
- disabled transparency skips `TransparentForwardPass`.
- invalid settings clamp to supported values.
- OIT mode has no effect unless OIT resources and passes are initialized.
- decal bias values remain inside conservative ranges.

## Material Metadata

Current alpha policy is encoded in `GPUMaterialData.NormalScaleBias`. That works for shaders but is too implicit for production authoring. Add explicit CPU-side material metadata while keeping the GPU packing stable for compatibility.

Suggested types:

```csharp
public enum MaterialBlendMode : uint
{
    Opaque = 0,
    Mask = 1,
    AlphaBlend = 2,
    PremultipliedAlpha = 3,
    Additive = 4,
    Multiply = 5
}

public enum MaterialSurfaceFlags : uint
{
    None = 0,
    DoubleSided = 1 << 0,
    GeometryDecal = 1 << 1,
    ReceivesShadows = 1 << 2,
    WritesMotionVectors = 1 << 3
}

public sealed class MaterialRenderMetadata
{
    public MaterialBlendMode BlendMode { get; init; }
    public MaterialSurfaceFlags SurfaceFlags { get; init; }
    public float AlphaCutoff { get; init; } = 0.5f;
    public int DecalLayer { get; init; }
    public float DecalDepthBias { get; init; }
}
```

Implementation tasks:
1. Add CPU material metadata storage to `MaterialManager` keyed by `MaterialHandle`.
2. Register imported metadata from `ModelRenderUploadService`.
3. Keep `GPUMaterialData.NormalScaleBias` as the shader contract for alpha mode, cutoff, and double-sided flag until a deliberate GPU struct revision is planned.
4. Add material metadata snapshots for tests and diagnostics.
5. Make `SceneDataBuilder` classify materials from metadata when available and fall back to `GPUMaterialData` only for legacy/test materials.
6. Add an importer heuristic only for known geometry decal material metadata when supported by source data. Do not infer decals from names by default.

Acceptance criteria:
- code can distinguish a normal transparent mesh from a geometry decal.
- CPU diagnostics can report blend mode and decal counts without decoding vector channels.
- existing GPU struct layout tests remain stable unless a deliberate layout revision is made.

## Masked Depth And Shadows

The highest-priority correctness gap is alpha-tested depth. Masked materials must not write solid rectangular depth.

Recommended implementation:
1. Split solid depth and masked depth into separate pipelines:
   - `DepthPipeline`: no fragment shader, opaque-only.
   - `MaskedDepthPipeline`: task + mesh + small fragment shader that samples albedo alpha and discards below cutoff.
2. Split opaque draw commands into:
   - `SolidMeshletDrawCommands`: `OPAQUE`.
   - `MaskedMeshletDrawCommands`: `MASK`.
   - `TransparentMeshletDrawCommands`: `BLEND`.
3. Keep existing `OpaqueMeshletCount` as a compatibility aggregate if useful, but expose solid and masked counts separately.
4. Add bindless draw buffer indices for masked depth/forward if separate buffers are used, or add a draw-list selector in push constants.
5. Add a masked shadow-depth pipeline for directional, spot, and point shadows.
6. Include masked meshlets in shadow caster lists and alpha-test them in the shadow fragment shader.
7. Keep blend materials excluded from shadow casters unless transparent shadow receiving/casting is deliberately enabled later.

Shader tasks:
- Add `depth_alpha.frag` or equivalent.
- Add `shadow_depth_alpha.frag` or equivalent.
- Sample only the albedo texture alpha and material alpha factor.
- Discard when `alpha <= alphaCutoff`.
- Avoid normal, roughness, lighting, AO, and reflection work in depth-only alpha shaders.

Acceptance criteria:
- masked foliage and fences write cutout depth into the depth prepass.
- Hi-Z, AO, and opaque forward depth testing see cutout holes correctly.
- masked foliage casts cutout shadows.
- alpha-tested depth remains validation-clean and RenderDoc-inspectable.

## Transparent Forward Rendering

Harden the existing `TransparentForwardPass`.

Tasks:
1. Route settings through `RenderSettings.Transparency`.
2. Respect `TransparencySettings.Enabled` in `VulkanRenderer` and `SceneRenderingData`.
3. Keep current sorted alpha blend as the default path.
4. Add debug views in `forward.frag` for:
   - alpha mode.
   - final alpha value.
   - sort bucket/order visualization.
   - overdraw approximation if feasible.
5. Decide whether transparent materials sample:
   - direct lights.
   - shadow receiver factors.
   - environment/reflection probes.
   - fog.
6. Keep depth test enabled and depth writes disabled.
7. Keep `LoadOp.Load` for scene color.
8. Keep transparent output in HDR scene color before bloom and tone mapping.
9. Add a pass-level GPU timer and CPU recording timer if not already present.

Acceptance criteria:
- transparent objects blend over opaque HDR lighting.
- transparent emissive materials can contribute to bloom.
- toggling transparent rendering through sample input is reflected in diagnostics.
- debug views show material alpha policy without modifying content.

## Sorting Policy

Current sorting is per meshlet by object-center distance with stable tie-breakers. Keep it as the first production policy but document and expose it.

Tasks:
1. Add `TransparencySettings.SortPerMeshlet` and a fallback object-level sort option if CPU cost becomes too high.
2. Add diagnostics:
   - transparent sort mode.
   - transparent sort candidate count.
   - transparent sort CPU microseconds.
   - max transparent meshlets exceeded count.
3. Add a clear overflow policy:
   - either reject excess transparent meshlets with a diagnostic warning,
   - or draw excess in deterministic fallback order.
4. Preserve stable tie-breakers:
   - descending distance.
   - decal layer or material priority.
   - material index.
   - instance id.
   - meshlet index.
5. Add tests for equal-distance determinism.

Acceptance criteria:
- transparent sorting does not flicker under equal-distance or coplanar cases.
- diagnostics expose sort cost and overflow.
- behavior is deterministic across runs for the same camera and scene state.

## Geometry Decals

Geometry decals are authored as actual meshes, such as Sponza dirt overlays. They should remain supported because they are simple, predictable, and compatible with glTF content.

Tasks:
1. Add `GeometryDecal` material metadata.
2. Add decal layer and priority fields.
3. Add configurable depth bias for geometry decals.
4. Decide implementation path:
   - first: use a dedicated transparent geometry decal pipeline with dynamic depth bias.
   - later: split `GeometryDecalPass` if RenderDoc clarity or ordering requires it.
5. Sort geometry decals after opaque objects and before general transparent glass unless material priority says otherwise.
6. Add `DecalSettings.GeometryDecalsEnabled` and debug views.
7. Add sample input controls:
   - toggle geometry decals.
   - cycle decal debug view.
   - increase/decrease decal depth bias.
8. Add diagnostics:
   - geometry decal material count.
   - geometry decal object count.
   - geometry decal meshlet count.
   - depth bias values.

Acceptance criteria:
- Sponza dirt decals render without rectangular artifacts.
- geometry decals do not visibly z-fight under normal camera movement.
- decal behavior is controlled by metadata/settings, not material names.

## Projected Decals

Projected decals should be added only after geometry decals and masked depth are stable.

Recommended design:
- tiled or clustered projected decals using screen-space reconstruction.
- decal data stored in a GPU storage buffer.
- decal textures in the existing bindless texture heap.
- optional tile culling pass similar in spirit to `TiledLightCullingPass`.
- decal projection evaluated after opaque lighting and before transparent glass, or folded into forward shading if pass cost is lower.

Suggested CPU type:

```csharp
public sealed class ProjectedDecal
{
    public string Name { get; set; } = string.Empty;
    public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
    public Vector3 Size { get; set; } = new(1f, 1f, 1f);
    public MaterialHandle Material { get; set; }
    public int Layer { get; set; }
    public float NormalBlend { get; set; } = 1.0f;
    public float Opacity { get; set; } = 1.0f;
    public float FadeStartDistance { get; set; } = 50.0f;
    public float FadeEndDistance { get; set; } = 80.0f;
}
```

Implementation tasks:
1. Add projected decal authoring data to `Scene`.
2. Add `GPUProjectedDecal` layout and layout tests.
3. Add `ProjectedDecalManager` or extend `SceneDataBuilder` with decal upload buffers.
4. Add a projected decal atlas/material path or reuse bindless textures directly.
5. Reconstruct world position from depth in a fullscreen/tiled decal pass.
6. Clip by decal volume.
7. Project UVs into decal space.
8. Blend albedo, normal, roughness, metallic, and emissive using material controls.
9. Respect decal layers and priorities.
10. Add debug volume rendering or an overlay through Phase 18 debug draw hooks.

Acceptance criteria:
- dynamic decals can be spawned without adding mesh geometry.
- projected decals are clipped to their volume.
- decals do not affect sky or transparent-only pixels.
- projected decal count and tile pressure are visible in diagnostics.

## Weighted Blended OIT

Add weighted blended OIT only if sorted alpha is not good enough for target content such as many overlapping glass panes, foliage cards, VFX sheets, or transparent props.

Recommended resources:
- accumulation target: `R16G16B16A16_SFLOAT`.
- revealage target: `R8_UNORM` or `R16_SFLOAT`.
- composite pass into HDR scene color.

Implementation tasks:
1. Add OIT render targets to `RenderTargetManager`.
2. Add weighted OIT transparent pipeline:
   - color attachment 0 accumulates weighted color and alpha.
   - color attachment 1 accumulates revealage.
   - depth test enabled.
   - depth writes disabled.
3. Add `WeightedOitCompositePass`.
4. Add debug views for accumulation and revealage.
5. Keep sorted alpha as fallback and default.
6. Add settings to switch modes at runtime with safe render target recreation.

Acceptance criteria:
- overlapping transparent objects look stable without strict sorting.
- OIT mode has measurable diagnostics and memory cost.
- sorted alpha remains available for low-end hardware and simpler scenes.

## Transparent Shadows

Transparent shadow receiving should be supported before transparent shadow casting.

Tasks:
1. Ensure transparent forward shading can sample directional, spot, and point shadow data when `TransparencySettings.ReceiveShadows` is enabled.
2. Validate glass and decals under shadowed light.
3. Add an approximation for transparent shadow casting only if content requires it:
   - masked shadows already handled through masked shadow depth.
   - blend shadows can use alpha-tested shadow maps at a configurable threshold.
   - colored/transmissive shadows are out of scope for this phase.

Acceptance criteria:
- transparent objects can visibly receive shadows when enabled.
- blend materials do not unexpectedly cast solid shadows.
- masked materials cast cutout shadows.

## Reflections, Fog, Bloom, And AO Integration

Tasks:
1. Verify transparent materials sample global environment and reflection probes only when appropriate.
2. Decide whether geometry decals should alter material properties before reflection evaluation or simply alpha-blend shaded results.
3. Keep AO driven by opaque/masked depth, not transparent depth.
4. Ensure fog applies consistently to transparent output:
   - preferred: transparent objects render before `FogPass`, so fog affects the combined scene.
   - if transparent-specific fog is needed, add shader-side fog later.
5. Ensure transparent emissive surfaces contribute to bloom through HDR scene color.
6. Validate TAA/SMAA with transparent edges and decal shimmer.

Acceptance criteria:
- transparent surfaces are not accidentally unfogged or double-fogged.
- decals and transparent emissive materials behave correctly before bloom.
- AO does not use blend geometry as occluders.

## Sample App Controls

Extend `NjulfHelloGame/SampleInputController.cs` after settings exist.

Suggested controls:
- keep `F9`: toggle transparent rendering.
- add a key to cycle `TransparencyDebugView`.
- add a key to cycle `TransparencyMode`.
- add a key to toggle geometry decals.
- add a key to cycle `DecalDebugView`.
- add two keys for decal depth bias down/up.
- add a key to toggle projected decals after that system exists.
- add a key to toggle transparent shadow receiving.

Implementation notes:
- follow the existing `WasPressed` pattern.
- add concise `PrintTransparencySettings` and `PrintDecalSettings` helpers.
- avoid colliding with current Phase 1-10 controls in `SampleInputController`.
- print enough state to reproduce a visual issue from console logs.

Acceptance criteria:
- every new runtime mode can be toggled or inspected in `NjulfHelloGame`.
- console output includes current mode, debug view, bias, counts where relevant, and enabled state.

## Diagnostics

Extend `RendererDiagnostics` and `SceneRenderingData` as needed.

Add or confirm fields:
- `SolidObjectCount`
- `MaskedObjectCount`
- `TransparentObjectCount`
- `GeometryDecalObjectCount`
- `ProjectedDecalCount`
- `SolidMeshletCount`
- `MaskedMeshletCount`
- `TransparentMeshletCount`
- `GeometryDecalMeshletCount`
- `BlendMaterialCount`
- `MaskMaterialCount`
- `GeometryDecalMaterialCount`
- `TransparencyMode`
- `TransparencyDebugView`
- `DecalDebugView`
- `TransparentSortMode`
- `TransparentSortCandidateCount`
- `TransparentSortMicroseconds`
- `TransparentOverflowCount`
- `GpuMaskedDepthMicroseconds`
- `GpuTransparentMicroseconds`
- `GpuProjectedDecalMicroseconds`
- `GpuWeightedOitCompositeMicroseconds`
- `TransparentAccumulationFormat`
- `TransparentRevealageFormat`
- `TransparencyRenderTargetBytes`

Update `SampleDiagnosticsReporter` to print these in a compact transparency/decal line.

Acceptance criteria:
- diagnosing an alpha/decal bug does not require stepping through the debugger.
- RenderDoc pass names and console diagnostics agree on which path ran.

## Tests

Add focused CPU tests before visual work gets broad.

Material tests:
1. default material metadata is opaque, non-decal, single-sided.
2. imported `MASK` material produces mask metadata and cutoff.
3. imported `BLEND` material produces alpha blend metadata.
4. geometry decal metadata survives material registration.
5. material metadata snapshots remain stable.

Scene builder tests:
1. opaque material routes only to solid/opaque draw commands.
2. masked material routes to masked depth/forward commands.
3. blend material routes only to transparent commands.
4. geometry decal routes to transparent/decal commands.
5. transparent sorting is back-to-front.
6. equal-distance transparent sorting is deterministic.
7. transparent overflow policy is deterministic.
8. blend materials are excluded from shadow caster commands.
9. masked materials are included in shadow caster commands.

Settings tests:
1. transparency defaults preserve current behavior.
2. decal defaults are conservative.
3. invalid transparency and decal values clamp.
4. OIT settings do not allocate resources when OIT is disabled.

Shader/build tests:
1. shader compilation includes masked depth fragment shader.
2. shader compilation includes masked shadow fragment shader.
3. OIT shaders compile only when added.
4. GPU struct layout tests pass for any new decal structs.

Integration tests:
1. Sponza `dirt_decal` remains imported as `BLEND`.
2. imported alpha-mask sample asset produces nonzero masked object and meshlet counts.
3. imported blend sample asset produces nonzero transparent object and meshlet counts.
4. imported projected decal sample scene uploads expected decal count once projected decals exist.

Manual validation:
1. Sponza dirt decals show as subtle overlays without black/grey rectangles.
2. close-up decal camera movement does not show unacceptable z-fighting.
3. masked foliage writes cutout depth and casts cutout shadows.
4. glass or translucent panes sort deterministically during camera movement.
5. transparent pass toggle removes only blend content.
6. debug views isolate alpha mode, alpha value, and decal layers.
7. Vulkan validation remains clean during startup, resize, feature toggles, frame rendering, and shutdown.

## Performance And Memory Budgets

Initial targets:
- sorted transparent CPU sort cost under 0.5 ms for normal scenes.
- masked depth overhead proportional only to masked meshlet count.
- geometry decal bias path should not create excessive pipeline variants.
- projected decal tile culling under 0.3 ms for typical counts.
- OIT memory budget documented by resolution and format.

Tasks:
1. Track transparent sort CPU time.
2. Track masked depth GPU time separately from solid depth if split.
3. Track transparent overdraw or at least transparent submitted meshlets.
4. Track projected decal tile list pressure.
5. Track OIT render target bytes.
6. Add stress scenes:
   - many alpha-tested foliage cards.
   - many coplanar geometry decals.
   - many overlapping transparent panes.
   - many projected decals.

Acceptance criteria:
- every expensive path has a visible count, timing, and memory estimate.
- settings provide a way to disable or budget each optional feature.

## Implementation Order

1. Add transparency and decal settings with tests.
2. Add CPU material metadata to `MaterialManager` and register imported alpha/decal metadata.
3. Split solid, masked, transparent, and geometry decal draw accounting in `SceneDataBuilder`.
4. Add masked depth and masked shadow alpha-test pipelines.
5. Update depth/shadow passes to use solid and masked lists correctly.
6. Harden `TransparentForwardPass` with settings, debug views, diagnostics, and timers.
7. Add geometry decal metadata, depth bias, layer sorting, and sample controls.
8. Extend `SampleDiagnosticsReporter` and `SampleInputController`.
9. Add validation scenes/assets for masked foliage, blend glass, and decals.
10. Add projected decals as a separate vertical slice.
11. Add weighted blended OIT only after sorted alpha limitations are demonstrated.
12. Run full CPU tests, shader build tests, Vulkan validation, and RenderDoc frame inspection.

## Rollout Slices

### Slice A: Masked Production Correctness

Deliver:
- `MASK` draw list separation.
- masked depth alpha discard.
- masked shadow alpha discard.
- tests and diagnostics.

Definition of done:
- foliage/fence cutouts write correct depth and cast cutout shadows.

### Slice B: Transparent And Geometry Decal Hardening

Deliver:
- settings.
- debug views.
- geometry decal metadata.
- depth bias controls.
- sample controls.
- diagnostics.

Definition of done:
- Sponza dirt decals are stable, inspectable, and configurable.

### Slice C: Projected Decal System

Deliver:
- scene decal data.
- GPU decal buffers.
- projected decal pass.
- tile culling if needed.
- debug volumes and diagnostics.

Definition of done:
- gameplay decals can be spawned without mesh assets.

### Slice D: Optional Weighted OIT

Deliver only if needed:
- OIT render targets.
- weighted OIT pipeline.
- OIT composite pass.
- mode toggle and diagnostics.

Definition of done:
- overlapping transparent content is stable where sorted alpha is not acceptable.

## Risks

1. Alpha-tested depth requires texture sampling in a depth path, increasing cost for masked-heavy scenes.
2. Splitting draw lists can increase buffer and bindless index pressure.
3. Geometry decal depth bias can hide z-fighting but create floating decals if set too high.
4. Per-meshlet transparent sorting may become expensive in scenes with many transparent meshlets.
5. Projected decals can become a second lighting system if material blending rules are not constrained.
6. OIT can consume significant bandwidth and memory at high resolutions.
7. Transparent shadows can become visually misleading if approximated as hard alpha cutouts.

Mitigations:
- keep settings conservative and clamped.
- keep features independently toggleable.
- add diagnostics before broad content rollout.
- validate each slice in RenderDoc.
- keep sorted alpha as the default and OIT as opt-in.

## Final Acceptance Criteria

Phase 11 is complete when:
1. opaque, masked, blend, geometry decal, and projected decal content have explicit render policy.
2. masked materials write alpha-tested depth and cast alpha-tested shadows.
3. blend materials render after opaque lighting, depth-test correctly, and do not write depth.
4. geometry decals have configurable, stable depth bias and deterministic layering.
5. transparent sorting is deterministic and measurable.
6. optional weighted OIT is available only if needed and can be disabled.
7. diagnostics expose alpha/decal counts, timings, modes, memory, and debug views.
8. `NjulfHelloGame` exposes practical controls through `SampleInputController`.
9. tests cover material metadata, scene routing, sorting, settings, shader builds, and layout contracts.
10. Vulkan validation is clean during startup, resize, runtime toggles, rendering, and shutdown.
11. RenderDoc shows the expected depth, masked depth, opaque, decal, transparent, optional OIT, fog, bloom, composite, and AA passes.
