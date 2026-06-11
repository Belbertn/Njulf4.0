# Phase 5: Spot And Point Light Shadows Detailed Implementation Plan

Goal: support authored local-light shadows for gameplay lighting such as torches, lamps, flashlights, projectors, and magic effects. Phase 5 should extend the Phase 4 shadow infrastructure without letting local shadows silently explode GPU cost.

Phase 5 assumes:
- Phase 2 HDR frame pipeline is complete.
- Phase 4 directional shadow infrastructure is complete or mostly complete.
- Shadow settings, shadow debug views, shadow depth pipeline conventions, shadow data upload, and shadow sampling patterns exist.
- Forward+ tiled light culling is already active for local lights.
- `LightType.Point` and `LightType.Spot` already exist.
- `NjulfHelloGame` can switch to `SampleLightingMode.ThreePointDemo` for local-light testing.

## Target Frame Shape

Final Phase 5 pass order:
1. `DirectionalShadowPass`
2. `SpotShadowPass`
3. `PointShadowPass`
4. `DepthPrePass`
5. `HiZBuildPass`
6. `TiledLightCullingPass`
7. `ForwardPlusPass`
8. `TransparentForwardPass`
9. Bloom and composite passes if Phase 3 is active
10. Present

Local shadow passes should be stable in RenderDoc even when no local shadows are enabled. They can early-out, but their pass labels and diagnostics should remain predictable.

## Scope

Phase 5 includes:
- Optional shadow state per spot light.
- Spot shadow atlas allocation.
- Spot shadow depth rendering.
- Forward shader sampling for shadowed spot lights.
- A strict budget for shadowed local lights.
- Screen-importance sorting/prioritization.
- Diagnostics for atlas usage and selected shadowed light count.
- Point light cubemap shadow resources.
- Point light shadow selection and budgeting.
- Point light shadow depth rendering for all six cube faces.
- Forward shader sampling for shadowed point lights.

Phase 5 does not include:
- Unlimited local shadow casting.
- Transparent shadow casting.
- Area light shadows.
- Ray traced local shadows.
- Volumetric shadowing.
- Full editor UI for shadow authoring.

## Core Design Decisions

1. Implement spot shadows first, then point shadows in the same phase. Spot shadows validate the local-shadow metadata and budget path before the more expensive point-light path.
2. Point shadows are mandatory for Phase 5. They should be disabled only by settings, not omitted from the implementation.
3. Use a shadow atlas for spot lights.
4. Use cubemap array shadows for point lights. Dual-paraboloid shadows can be evaluated later but should not replace the initial cubemap implementation.
5. Enforce visible budgets. Never silently render every shadow-casting local light.
6. Prioritize local shadows by expected screen impact, not light insertion order.
7. Local shadow sampling should integrate with the existing Forward+ light loop so only lights affecting the tile are evaluated.
8. Keep unshadowed local lights cheap and avoid adding significant per-light work when shadows are disabled.

## Settings

Extend `ShadowSettings`.

Suggested properties:
```csharp
public sealed class LocalShadowSettings
{
    public bool SpotShadowsEnabled { get; set; } = true;
    public int MaxShadowedSpotLights { get; set; } = 8;
    public uint SpotShadowAtlasSize { get; set; } = 4096;
    public uint SpotShadowTileSize { get; set; } = 512;
    public float SpotNormalBias { get; set; } = 0.02f;
    public float SpotConstantDepthBias { get; set; } = 0.0005f;
    public float SpotSlopeScaledDepthBias { get; set; } = 1.5f;
    public int SpotPcfRadius { get; set; } = 1;
    public bool PointShadowsEnabled { get; set; } = true;
    public int MaxShadowedPointLights { get; set; } = 1;
    public uint PointShadowMapSize { get; set; } = 512;
    public float PointNormalBias { get; set; } = 0.03f;
    public float PointConstantDepthBias { get; set; } = 0.001f;
    public float PointSlopeScaledDepthBias { get; set; } = 1.5f;
    public int PointPcfRadius { get; set; } = 1;
}
```

Recommended constraints:
- `MaxShadowedSpotLights`: `0` to `32`
- `SpotShadowAtlasSize`: power of two, `1024` to `8192`
- `SpotShadowTileSize`: power of two, `128` to `2048`
- `SpotShadowTileSize <= SpotShadowAtlasSize`
- `MaxShadowedPointLights`: `0` to `4`
- `PointShadowMapSize`: power of two, `128` to `2048`

Tests:
- defaults are conservative.
- invalid atlas/tile sizes are rejected or clamped.
- budget cannot exceed atlas capacity.
- point shadow cubemap array layer count matches `MaxShadowedPointLights * 6`.

## Light Metadata

Add optional shadow state per local light.

Suggested CPU-side fields:
```csharp
public bool CastsShadows;
public float ShadowStrength;
public uint ShadowMapSizeOverride;
public float ShadowNearPlane;
public float ShadowFarPlane;
public int ShadowPriority;
```

For spot lights, the renderer needs:
- cone angle.
- range/far plane.
- position.
- direction.
- chosen atlas rect.
- light view-projection matrix.

For point lights, the renderer needs:
- cubemap face matrices or dual-paraboloid matrices.
- range/far plane.
- chosen cubemap or atlas allocation.

Keep CPU authoring metadata separate from `GPULight` unless shader sampling needs it directly. Add compact GPU shadow metadata in a separate shadow buffer.

## Spot Shadow Atlas

Create a spot shadow atlas resource owner.

Suggested files:
- `SpotShadowAtlas.cs`
- `LocalShadowResources.cs`
- `LocalShadowAllocator.cs`

Atlas resource:
- depth image.
- format: `D32Sfloat` unless fallback is required.
- usage:
  - `DepthStencilAttachmentBit`
  - `SampledBit`
- one sampled atlas view.
- per-tile rendering uses viewport/scissor, not per-tile image views.

Debug names:
- `Spot Shadow Atlas`
- `Spot Shadow Atlas View`
- `Spot Shadow Sampler`

Allocation:
- fixed grid first.
- tile count = `(AtlasSize / TileSize)^2`.
- one tile per selected spot light.
- no packing fragmentation in first implementation.

Atlas rect data:
```csharp
public readonly struct SpotShadowAtlasRect
{
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
}
```

Tests:
- atlas capacity calculation.
- tile rect calculation.
- atlas rejects non-power-of-two or invalid tile sizes.
- selected light count never exceeds atlas capacity or configured budget.

## Spot Shadow Selection And Budgeting

Create a selection system that runs each frame before shadow rendering.

Suggested file:
- `LocalShadowSelector.cs`

Candidate requirements:
- light type is `Spot`.
- light is enabled.
- `CastsShadows == true`.
- intensity > 0.
- range > 0.
- spot angle valid.
- light intersects or could affect camera view.

Priority score:
```text
score =
  authorPriorityWeight +
  screenSizeWeight +
  intensityWeight +
  distanceWeight +
  recency/stabilityWeight
```

Start simple:
- prefer explicit `ShadowPriority`.
- then projected screen size estimate.
- then intensity.
- then distance to camera.

Stability:
- keep previous frame selected lights when scores are close.
- add hysteresis to avoid rapid shadow-map allocation changes.
- do not implement temporal caching of shadow maps yet unless needed.

Diagnostics:
- candidate count.
- selected count.
- rejected-by-budget count.
- atlas capacity.
- atlas used tiles.

## Spot Light Matrix Builder

Create:
- `SpotShadowDataBuilder.cs`

For each selected spot light:
1. Build a view matrix from light position and direction.
2. Build a perspective projection using spot cone angle.
3. Near plane:
   - default `0.05` or author-provided.
4. Far plane:
   - light range or author-provided max.
5. Store `LightViewProjection`.
6. Store atlas UV scale/offset.

Depth convention:
- Use the same projection depth convention as the existing renderer where practical.
- Document whether shadow compare is reverse-Z or normal-Z.
- Add tests that generated matrices are finite and stable.

## GPU Data

Add a local shadow metadata buffer.

Suggested structures:
```csharp
public struct GPUSpotShadow
{
    public Matrix4x4 LightViewProjection;
    public Vector4 AtlasScaleOffset; // scale.xy, offset.zw
    public Vector4 BiasStrengthTexelSize; // normalBias, depthBias, strength, texelSize
    public int LightIndex;
    public int AtlasLayerOrTile;
    public int PcfRadius;
    public int Enabled;
}
```

The shader needs a way to map light index to shadow data.

Options:
1. Add `ShadowDataIndex` to `GPULight`.
2. Add a separate light-index-to-shadow-index buffer.

Recommended first implementation:
- Add a separate `LocalLightShadowIndexBuffer`.
- It maps global light index to spot shadow metadata index, or `-1`.
- This avoids expanding `GPULight` if the current 64-byte layout is important.

Bindless/static buffers:
- `SpotShadowDataBuffer`
- `LocalLightShadowIndexBuffer`

Tests:
- C# and GLSL struct layout.
- bindless indices match.
- unshadowed lights map to `-1`.

## Spot Shadow Depth Pass

Create:
- `SpotShadowPass`

Responsibilities:
- Early-out if spot shadows disabled or no selected spot lights.
- Transition atlas to depth attachment layout.
- Clear only used atlas tiles or clear full atlas.
- Render shadow casters into each tile using viewport/scissor.
- Transition atlas to shader-read layout.
- Upload spot shadow metadata.
- Record diagnostics.

First implementation:
- clear full atlas once per frame for simplicity.
- render one selected spot light per atlas tile.
- render opaque objects only.
- use the existing mesh/task shadow-depth path from Phase 4.

Pass labels:
- `SpotShadowPass`
- nested labels:
  - `SpotShadowPass Light 0`
  - `SpotShadowPass Light 1`

Pipeline state:
- depth-only.
- depth bias enabled.
- viewport/scissor dynamic.
- cull mode configurable, default back-face.

Important:
- The shadow pass must not use tiled light culling output. It renders shadow casters from the light point of view.

## Forward Shader Sampling

Update `forward.frag` local light loop.

Flow for each local light:
1. Read light as today.
2. Compute direct local lighting contribution.
3. Read shadow index for `lightIndex`.
4. If shadow index >= 0:
   - transform world position into spot shadow clip space.
   - convert to atlas UV.
   - reject outside [0, 1] spot projection.
   - sample atlas with PCF.
   - apply shadow strength.
5. Multiply direct local light contribution by shadow factor.

Do not shadow:
- emissive.
- ambient/IBL.
- other lights.

PCF:
- 3x3 first.
- atlas-aware texel size.
- clamp samples inside tile with a guard band.

Atlas bleeding mitigation:
- add 1-2 texel padding per tile if possible.
- clamp PCF taps to tile rect.
- clear atlas to unshadowed depth value.

## Point Shadow Cubemap Resources

Point shadows are required in Phase 5. Implement them after spot shadows are stable, but do not treat them as a future optional extension.

Create a point shadow resource owner.

Suggested files:
- `PointShadowCubemapArray.cs`
- `PointShadowDataBuilder.cs`

Resource shape:
- `ImageType.Type2D`
- `ArrayLayers = MaxShadowedPointLights * 6`
- `ImageCreateFlags.CubeCompatibleBit`
- format: `D32Sfloat` unless fallback is required.
- usage:
  - `DepthStencilAttachmentBit`
  - `SampledBit`
- one cubemap-array sampled view.
- one 2D-layer depth attachment view per point light face.

Debug names:
- `Point Shadow Cubemap Array`
- `Point Shadow Cubemap Array View`
- `Point Shadow Light 0 Face +X View`
- `Point Shadow Sampler`

Face order:
1. `+X`
2. `-X`
3. `+Y`
4. `-Y`
5. `+Z`
6. `-Z`

Point shadows should clear only selected light faces where practical. Clearing the full cubemap array is acceptable for the first implementation if it is clearly measured and diagnosed.

## Point Shadow Selection And Budgeting

Point lights use a separate budget from spot lights.

Candidate requirements:
- light type is `Point`.
- light is enabled.
- `CastsShadows == true`.
- intensity > 0.
- range > 0.
- light sphere intersects or could affect the camera view.

Priority scoring should use the same framework as spot shadows:
- explicit `ShadowPriority`.
- projected screen size of the light sphere.
- intensity.
- distance to camera.
- previous-frame selection hysteresis.

Diagnostics:
- point candidate count.
- selected point shadow count.
- rejected-by-budget count.
- cubemap size.
- rendered face count.

## Point Light Matrix Builder

For each selected point light, build six view-projection matrices.

Projection:
- 90-degree field of view.
- aspect 1.
- near plane from `ShadowNearPlane` or default `0.05`.
- far plane from light range or `ShadowFarPlane`.

Views:
- `+X`, `-X`, `+Y`, `-Y`, `+Z`, `-Z`.
- Use stable up vectors for cube faces.
- Add tests for face orientation to avoid seams and inverted faces.

GPU data per point shadow:
```csharp
public struct GPUPointShadow
{
    public Matrix4x4 FaceViewProjection0;
    public Matrix4x4 FaceViewProjection1;
    public Matrix4x4 FaceViewProjection2;
    public Matrix4x4 FaceViewProjection3;
    public Matrix4x4 FaceViewProjection4;
    public Matrix4x4 FaceViewProjection5;
    public Vector4 PositionRange; // xyz position, w range
    public Vector4 BiasStrengthTexelSize; // normalBias, depthBias, strength, texelSize
    public int LightIndex;
    public int CubemapIndex;
    public int PcfRadius;
    public int Enabled;
}
```

If this struct is too large for the existing style, split matrices and metadata into two buffers. Keep layout tests either way.

## PointShadowPass

Point pass:
- `PointShadowPass`
- nested labels:
  - `PointShadowPass Light 0 Face +X`
  - `PointShadowPass Light 0 Face -X`
  - `PointShadowPass Light 0 Face +Y`
  - `PointShadowPass Light 0 Face -Y`
  - `PointShadowPass Light 0 Face +Z`
  - `PointShadowPass Light 0 Face -Z`

Responsibilities:
- Early-out if point shadows are disabled or no selected point lights.
- Transition cubemap array to depth attachment layout.
- Render all six faces for each selected point light.
- Upload point shadow metadata.
- Transition cubemap array to shader-read layout.
- Record diagnostics.

Pipeline:
- reuse the Phase 4/spot shadow depth pipeline if possible.
- viewport/scissor should match point shadow map size.
- depth bias should use point-shadow settings.

Cost rule:
- rendered point shadow faces = `selectedPointLightCount * 6`.
- diagnostics must expose that count.

## Point Shadow Sampling

Update `forward.frag` point light path.

Flow:
1. Read point light as today.
2. Read local shadow index for the light.
3. If point shadow metadata exists:
   - compute vector from light to receiver.
   - select cubemap direction.
   - compute receiver distance normalized by range.
   - apply normal/depth bias.
   - sample cubemap array depth.
   - apply PCF over cubemap directions or face-local offsets.
   - multiply direct point light contribution by shadow factor.

Recommended first PCF:
- small cubemap PCF kernel using offset directions scaled by texel size.
- keep `PointPcfRadius = 1` by default.

Debug views:
- point shadow receiver factor.
- point cubemap face preview.
- selected point shadow overlay.

## Render Graph Integration

Final local-shadow pass order:
```text
DirectionalShadowPass
SpotShadowPass
PointShadowPass
DepthPrePass
HiZBuildPass
TiledLightCullingPass
ForwardPlusPass
TransparentForwardPass
BloomExtractPass
BloomDownsamplePass
BloomUpsamplePass
ToneMapCompositePass
```

Update pass-order tests so local shadow passes are before camera passes and before `TiledLightCullingPass`.

## Runtime Controls In NjulfHelloGame

Add sample controls:
- toggle spot shadows.
- toggle point shadows.
- cycle local shadow debug view.
- increase/decrease max shadowed spot lights.
- increase/decrease max shadowed point lights.
- increase/decrease spot bias.
- increase/decrease point bias.
- switch sample lighting mode to a spot-shadow test scene if added.

Add sample local shadow modes:
- `SampleLightingMode.SpotShadowDemo`
- one or more spot lights aimed at Sponza geometry.
- at least one light with `CastsShadows = true`.
- `SampleLightingMode.PointShadowDemo`
- one or more point lights near Sponza geometry.
- at least one point light with `CastsShadows = true`.

Keep `ThreePointDemo` unshadowed by default unless converted intentionally.

## Debug Views

Add local shadow debug modes:
- spot atlas preview.
- selected shadowed light overlay.
- local shadow receiver factor.
- local shadow budget heat/selection overlay.
- point cubemap face preview.

Recommended implementation:
- atlas preview in composite debug view.
- receiver factor in `forward.frag`.
- selected light overlay can be approximate if debug draw is not implemented yet.

## Diagnostics

Extend diagnostics with:
- `SpotShadowsEnabled`
- `SpotShadowCandidateCount`
- `SpotShadowSelectedCount`
- `SpotShadowRejectedByBudgetCount`
- `SpotShadowAtlasSize`
- `SpotShadowTileSize`
- `SpotShadowAtlasCapacity`
- `SpotShadowAtlasUsedTiles`
- `CpuSpotShadowRecordMicroseconds`
- `PointShadowsEnabled`
- `PointShadowCandidateCount`
- `PointShadowSelectedCount`
- `PointShadowRejectedByBudgetCount`
- `PointShadowMapSize`
- `PointShadowRenderedFaceCount`
- `CpuPointShadowRecordMicroseconds`

Update `SampleDiagnosticsReporter` to print one local-shadow line.

Do not fake GPU timing. Add GPU times only when timing queries exist.

## Tests

Add CPU tests for:
- local shadow settings defaults.
- invalid atlas settings validation.
- atlas capacity and tile rect generation.
- selected spot lights do not exceed budget.
- selection prefers higher-priority/high-impact lights.
- hysteresis keeps previous selections when scores are close.
- spot light matrices are finite.
- light index to shadow index mapping.
- bindless index contracts for local shadow buffers/textures.
- `GPUSpotShadow` C# and GLSL layout.
- point cubemap array layer/view count.
- selected point lights do not exceed budget.
- point light face matrices are finite and correctly oriented.
- `GPUPointShadow` C# and GLSL layout.
- render pass order includes `SpotShadowPass` before camera passes.
- render pass order includes `PointShadowPass` before camera passes.

Shader tests:
- spot shadow depth shaders compile if new shaders are created.
- updated `forward.frag` compiles.
- point shadow depth shaders compile if new shaders are created.

## Validation And QA

Required commands:
```powershell
dotnet build Njulf.sln
dotnet test Njulf.sln
dotnet run --project NjulfHelloGame -- --smoke-frames 3
```

Runtime validation:
- startup validation-clean.
- resize validation-clean.
- shutdown validation-clean.
- toggling spot shadows validation-clean.
- toggling point shadows validation-clean.
- changing spot light count/budget validation-clean.
- changing point light count/budget validation-clean.
- changing atlas size recreates resources safely.
- changing point cubemap size recreates resources safely.
- no validation errors when zero local shadow casters exist.

RenderDoc checklist:
- `SpotShadowPass` appears before main camera passes.
- spot atlas image/view/sampler are named.
- each selected spot light renders into the expected atlas tile.
- atlas transitions from depth attachment to shader read.
- `forward.frag` samples local shadow metadata only for selected shadowed lights.
- unshadowed local lights do not sample shadow data.
- `PointShadowPass` renders six faces per selected point light.
- point cubemap array transitions from depth attachment to shader read.
- point shadow metadata is sampled only for selected shadowed point lights.
- no accidental per-object CPU draw loop is introduced beyond existing meshlet draw dispatch model.

Visual checklist:
- spot lights cast visible shadows.
- unshadowed local lights still render normally.
- atlas preview shows correct tile allocation.
- bias controls can reduce acne and peter-panning.
- shadow selection remains stable during small camera movement.
- budget pressure produces predictable selected lights.
- point lights cast omnidirectional shadows without obvious face orientation errors.

## Acceptance Criteria

Phase 5 is done when:
1. Spot lights can optionally cast shadows.
2. Spot shadows render through a named shadow atlas.
3. Point lights can cast cubemap shadows.
4. Point shadows render through a named cubemap array.
5. The renderer enforces `MaxShadowedSpotLights`, spot atlas capacity, and `MaxShadowedPointLights`.
6. Shadowed spot and point lights are prioritized by screen importance and author priority.
7. Unshadowed local lights remain cheap.
8. Forward+ local lighting samples spot and point shadows only when shadow metadata exists.
9. Diagnostics report local shadow candidates, selected counts, rejected counts, atlas use, point rendered face count, and pass timing.
10. Runtime controls can toggle and tune spot and point shadows.
11. Startup, resize, rendering, and shutdown are validation-clean.
12. Build, tests, shader compilation, and `NjulfHelloGame --smoke-frames 3` pass.

## Risks And Mitigations

- Local shadows can dominate frame time: enforce separate spot and point budgets.
- Atlas bleeding: use tile padding and clamp PCF taps within tile rect.
- Selection flicker: add hysteresis and previous-frame preference.
- Shader cost regression: branch on shadow index and avoid shadow sampling for unshadowed lights.
- Cubemap point shadows are expensive: keep the default point budget low, expose rendered face count, and make point shadows independently toggleable.
- Descriptor/layout complexity: keep local shadow metadata in dedicated buffers rather than overloading `GPULight`.
- Bias tuning varies per scene: expose runtime controls and diagnostics early.

## Suggested Implementation Order

1. Add local shadow settings and validation.
2. Add spot shadow metadata to CPU light authoring.
3. Add spot shadow atlas resource owner.
4. Add atlas capacity/tile tests.
5. Add local shadow selector and budget tests.
6. Add spot light matrix builder.
7. Add GPU spot shadow metadata buffers and layout tests.
8. Add `SpotShadowPass`.
9. Render selected spot lights into atlas.
10. Integrate spot shadow sampling into `forward.frag`.
11. Add point cubemap array resources.
12. Add point shadow selector, matrix builder, and tests.
13. Add GPU point shadow metadata buffers and layout tests.
14. Add `PointShadowPass`.
15. Render selected point lights into cubemap faces.
16. Integrate point shadow sampling into `forward.frag`.
17. Add diagnostics and sample reporter output.
18. Add `SpotShadowDemo` and `PointShadowDemo` sample lighting modes.
19. Add runtime controls.
20. Run build/tests/smoke and inspect in RenderDoc.
