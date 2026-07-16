# Phase 4: Directional Shadows Detailed Implementation Plan

Goal: add stable directional-light shadowing to Njulf so authored scenes gain contact, scale, and depth. The first implementation should support one shadow-casting directional light, then extend to cascaded shadow maps once the single-map path is validation-clean.

Current baseline:
- `NjulfHelloGame` uses `SampleLightingMode.DirectionalKey` with one directional light.
- Renderer uses Vulkan dynamic rendering, bindless buffers/textures, mesh/task shaders, depth prepass, Hi-Z, Forward+, transparent pass, and planned HDR composite.
- Phase 1 added debug object names and pass labels.
- Phase 2 should move scene color to HDR before Phase 4 work begins.
- There is no shadow resource model, shadow pass, light shadow metadata, or `forward.frag` shadow sampling yet.

## Target Frame Shape

Final Phase 4 pass order after Phase 2:
1. `DirectionalShadowPass`
2. `DepthPrePass`
3. `HiZBuildPass`
4. `TiledLightCullingPass`
5. `ForwardPlusPass`
6. `TransparentForwardPass`
7. `ToneMapCompositePass`
8. Present

`DirectionalShadowPass` should be explicit and visible in RenderDoc. It should run before main camera depth and color passes.

## Scope

Phase 4 includes:
- One shadow-casting directional light.
- One non-cascaded shadow map first.
- Cascaded shadow maps as the second milestone in the same phase.
- PCF filtering.
- Normal bias and slope-scaled depth bias controls.
- Shadow debug views.
- Runtime controls and diagnostics.

Phase 4 does not include:
- Spot or point light shadows.
- Contact shadows.
- Ray traced shadows.
- Transparent shadow casting.
- Shadow receiving on transparent materials unless a specific visual case requires it.

## Core Design Decisions

1. Use a dedicated depth image for directional shadows, not the main depth buffer.
2. Start with `Format.D32Sfloat` unless device format checks require fallback.
3. Use a 2D texture array for cascades from the beginning if it does not add much complexity; otherwise start with one 2D image and migrate in the cascade milestone.
4. Use orthographic light projection for directional shadows.
5. Use reverse-Z consistently only if the current depth conventions make that straightforward. Otherwise document the shadow-map compare direction explicitly.
6. Keep one shadowed directional light budget for Phase 4. If multiple directional lights exist, pick the first enabled shadow caster and report the selected light index in diagnostics.
7. Use existing mesh/task shader path where practical, but keep a separate shadow pipeline if push constants or output state differ.

## Implementation Milestones

### Milestone 1: Shadow Settings And Light Metadata

Add shadow-related settings before adding GPU resources.

Suggested files:
- `Njulf.Rendering/Data/RenderSettings.cs` if Phase 2 did not add it.
- `Njulf.Rendering/Data/ShadowSettings.cs`

Suggested properties:
```csharp
public sealed class ShadowSettings
{
    public bool DirectionalShadowsEnabled { get; set; } = true;
    public uint DirectionalShadowMapSize { get; set; } = 2048;
    public int DirectionalCascadeCount { get; set; } = 1;
    public float MaxShadowDistance { get; set; } = 80f;
    public float NormalBias { get; set; } = 0.03f;
    public float SlopeScaledDepthBias { get; set; } = 1.5f;
    public float ConstantDepthBias { get; set; } = 0.0005f;
    public int PcfRadius { get; set; } = 1;
    public ShadowDebugView DebugView { get; set; } = ShadowDebugView.None;
}
```

Add optional shadow metadata to `Light` or to a separate renderer-side shadow selection system:
- `CastsShadows`
- `ShadowStrength`
- Optional `ShadowMapSizeOverride`

Recommended: avoid expanding `GPULight` immediately unless shader needs the fields. Keep CPU-side metadata first if possible.

Tests:
- settings defaults are production-safe.
- invalid map sizes/cascade counts are clamped or rejected.
- sample directional light is selected as the shadow caster when enabled.

### Milestone 2: Shadow Resources

Create a shadow resource owner.

Suggested files:
- `DirectionalShadowResources.cs`
- `ShadowMapAtlas.cs` only if planning for future spot shadows; not required for Phase 4.

Responsibilities:
- Create depth image or depth texture array.
- Create per-cascade image views.
- Create a sampled view for shader reads.
- Track layout.
- Recreate when map size or cascade count changes.
- Assign debug names:
  - `Directional Shadow Map`
  - `Directional Shadow Cascade 0 View`
  - `Directional Shadow Sampler`

Image usage:
- `ImageUsageFlags.DepthStencilAttachmentBit`
- `ImageUsageFlags.SampledBit`

Sampler:
- Clamp to border or clamp to edge.
- Border color should produce unshadowed samples outside the map.
- Compare sampler can be deferred. Manual depth compare in shader is easier to debug initially.

Bindless:
- Reserve a static texture index for directional shadow map sampled view.
- Add corresponding C# and GLSL constants.
- Update `BindlessIndexTests`.

### Milestone 3: Light-Space Matrix Builder

Add a CPU helper to compute shadow view/projection matrices.

Suggested file:
- `DirectionalShadowDataBuilder.cs`

For one-map milestone:
1. Use camera frustum corners up to `MaxShadowDistance`.
2. Transform corners into light view space.
3. Fit an orthographic projection around those corners.
4. Add configurable near/far padding.
5. Snap the projection to shadow texels to reduce shimmering.

For cascades:
1. Split camera depth range into cascade intervals.
2. Use practical split scheme:
   - blend uniform and logarithmic splits with lambda around `0.5`.
3. Fit each cascade independently.
4. Stabilize each cascade with texel snapping.
5. Store cascade split depths in view space.

Data needed by shaders:
- `LightViewProjection[MaxCascades]`
- `CascadeSplits[MaxCascades]`
- `CascadeCount`
- `ShadowMapTextureIndex`
- `ShadowStrength`
- `NormalBias`
- `PcfRadius`

Add layout tests if this data goes into a GPU struct.

### Milestone 4: Shadow Data Upload

Add a GPU shadow constants/storage buffer.

Options:
1. Push constants for the shadow pass and a storage/uniform buffer for forward sampling.
2. A static bindless storage buffer for `GPUShadowData`.

Recommended: static bindless storage buffer so `forward.frag` can read shadow matrices and settings without bloating existing forward push constants.

Suggested additions:
- `GPUShadowData` in `GPUStructs.cs`.
- Matching `GPUShadowData` in `common.glsl`.
- `ShadowDataBuffer` static bindless index.

Tests:
- C# and GLSL sizes match.
- field offsets match.
- bindless index contract matches.

### Milestone 5: Shadow Depth Pipeline

Add a shadow-specific mesh pipeline path.

Suggested files:
- Extend `MeshPipeline`, or add `ShadowMeshPipeline.cs`.
- Add shaders:
  - `shadow_depth.task`
  - `shadow_depth.mesh`

Recommended first version:
- Reuse most of `depth.task` and `depth.mesh`.
- Push light view-projection matrix and draw command range.
- Render only depth.
- Cull transparent/blend objects from shadow casting for the first version.
- Include masked alpha only later, after masked depth support is solid.

Pipeline state:
- No color attachment.
- Depth attachment format: shadow depth format.
- Depth write enabled.
- Depth test enabled.
- Depth compare consistent with chosen projection/depth convention.
- Rasterizer depth bias enabled.
- Cull mode initially back-face. Make it configurable if peter-panning/acne tradeoffs require front-face culling.

Debug names:
- `Directional Shadow Mesh Pipeline`
- `Directional Shadow Pipeline Layout`
- `Directional Shadow Pipeline Cache`

### Milestone 6: DirectionalShadowPass

Create `DirectionalShadowPass`.

Responsibilities:
- Skip if no shadow-casting directional light or shadows disabled.
- Transition shadow map to depth attachment layout.
- Clear shadow depth.
- Render eligible opaque shadow casters.
- Transition shadow map to shader-read layout.
- Record CPU pass time in diagnostics.

Render graph placement:
- Insert before `DepthPrePass`.

Pass label:
- `DirectionalShadowPass`

Diagnostics:
- `DirectionalShadowPassEnabled`
- `DirectionalShadowMapSize`
- `DirectionalShadowCascadeCount`
- `ShadowedDirectionalLightIndex`
- `CpuDirectionalShadowRecordMicroseconds`
- Later: GPU shadow pass time when timing queries exist.

### Milestone 7: Forward Shader Shadow Sampling

Update `forward.frag`.

Shadow sampling flow:
1. For the active directional light, compute world-space receiver position.
2. Select cascade by camera/view depth.
3. Transform world position into light clip space.
4. Convert to shadow map UV/depth.
5. Apply normal bias before transform or receiver-plane bias in light space.
6. Sample the shadow depth.
7. Apply PCF.
8. Multiply only direct directional lighting by shadow factor.

Do not shadow:
- ambient fallback
- emissive
- future IBL

PCF first implementation:
- 3x3 kernel for `PcfRadius = 1`.
- Use texel size from shadow map size.
- Keep radius selectable for diagnostics/perf testing.

Debug views:
- cascade color overlay.
- shadow receiver factor.
- raw shadow coordinates or out-of-bounds mask if useful.

### Milestone 8: Bias Controls

Expose controls in settings and sample input.

Suggested runtime keys:
- Toggle directional shadows.
- Increase/decrease normal bias.
- Increase/decrease slope bias.
- Cycle shadow debug view.
- Cycle cascade count once cascades exist.

Console output should print current values, not spam every frame.

Bias behavior:
- Normal bias should offset receiver along interpolated normal.
- Slope-scaled depth bias should be applied in raster state for the shadow depth pass.
- Constant depth bias should be small and explicit.

### Milestone 9: Cascaded Shadow Maps

Add cascades after the single-map path is correct.

Resource shape:
- Prefer a 2D array depth image with one layer per cascade.
- Create per-layer depth attachment views.
- Create one sampled array view for shader reads.

Pass behavior:
- Render each cascade separately.
- Use cascade-specific light view-projection.
- Either loop cascades inside `DirectionalShadowPass` or create sub-labels:
  - `DirectionalShadowPass Cascade 0`
  - `DirectionalShadowPass Cascade 1`

Cascade stabilization:
- Snap orthographic projection center to shadow texel increments.
- Avoid fitting to exact near-plane slivers if that produces shimmering.
- Keep split distances stable unless camera near/far/settings change.

Initial cascade counts:
- Low: 1
- Medium: 2
- High: 3
- Ultra: 4

Do not wire full quality presets in Phase 4 unless Phase 17 settings already exist.

### Milestone 10: Debug Views

Add debug view enum values:
- `None`
- `CascadeOverlay`
- `ShadowMapPreview`
- `ReceiverFactor`

Implementation options:
- Cascade overlay in `forward.frag`.
- Receiver factor in `forward.frag`.
- Shadow map preview in composite pass if Phase 2 exists; otherwise a temporary fullscreen debug pass.

Recommended after Phase 2:
- Let `ToneMapCompositePass` preview shadow maps in a debug mode, because it already owns fullscreen output.

### Milestone 11: Diagnostics And Sample Reporting

Extend `RendererDiagnostics` and `SampleDiagnosticsReporter`.

Print:
- shadows enabled
- cascade count
- map size
- selected light index
- shadow pass CPU time
- debug view
- normal bias
- slope bias

Keep output grouped with other frame diagnostics.

### Milestone 12: Tests

Add CPU tests for:
- shadow settings defaults.
- invalid shadow settings validation.
- cascade split generation is monotonic.
- light-space matrices are finite.
- texel snapping is stable for small camera movements.
- bindless shadow indices match shader constants.
- `GPUShadowData` C# and GLSL layout.
- render pass order includes `DirectionalShadowPass` before `DepthPrePass`.

Shader tests:
- `shadow_depth.task` compiles.
- `shadow_depth.mesh` compiles.
- updated `forward.frag` compiles.

### Milestone 13: Validation And QA

Required commands:
```powershell
dotnet build Njulf.sln
dotnet test Njulf.sln
dotnet run --project NjulfHelloGame -- --smoke-frames 3
```

Runtime checks:
- startup validation-clean.
- resize validation-clean.
- shutdown validation-clean.
- toggling shadows validation-clean.
- changing light direction validation-clean.
- changing map size/cascade count recreates resources safely.

RenderDoc checklist:
- `DirectionalShadowPass` appears before camera depth/color passes.
- Shadow map image and cascade views are named.
- Shadow depth clear and draw calls are visible.
- Shadow map transitions from depth attachment to shader read.
- `forward.frag` samples the shadow map.
- No accidental per-object CPU draw loop is introduced.

Visual checklist:
- Sponza directional light casts visible shadows.
- Shadow edges are stable during small camera movement.
- Acne can be reduced with bias settings.
- Peter-panning can be reduced with lower bias.
- Cascade transitions are not obvious in normal mode.
- Cascade overlay clearly shows selected cascades.

## Acceptance Criteria

Phase 4 is done when:
1. One directional light can cast shadows in `NjulfHelloGame`.
2. The shadow pass uses named shadow resources and appears in RenderDoc.
3. Forward lighting applies shadowing only to direct directional light.
4. PCF filtering is available.
5. Normal bias, constant bias, and slope-scaled depth bias are exposed.
6. Cascaded shadow maps are available for larger scenes.
7. Cascade color overlay, shadow map preview, and receiver-factor debug views exist.
8. Resize, light direction changes, startup, rendering, and shutdown are validation-clean.
9. Diagnostics report shadow state, map size, cascade count, selected light, and pass timing.
10. Build, tests, shader compilation, and `NjulfHelloGame --smoke-frames 3` pass.

## Risks And Mitigations

- Shadow acne: add normal bias and slope-scaled depth bias early.
- Peter-panning: keep bias values visible and tunable at runtime.
- Shimmering: implement texel snapping before declaring cascades done.
- Pipeline format mistakes: add tests and explicit debug names for shadow pipeline/resources.
- Layout mismatch between C# and GLSL: add struct layout tests before shader sampling.
- Too much scope: ship one-map directional shadows first, then cascades.
- Transparent/masked materials: defer alpha-tested shadow caster support unless the sample scene visibly needs it.

## Suggested Implementation Order

1. Add `ShadowSettings` and defaults.
2. Add shadow bindless indices and layout tests.
3. Add `DirectionalShadowResources`.
4. Add `DirectionalShadowDataBuilder` for one shadow map.
5. Add `GPUShadowData` and upload path.
6. Add shadow depth shaders.
7. Add shadow mesh pipeline.
8. Add `DirectionalShadowPass` before `DepthPrePass`.
9. Integrate shadow sampling into `forward.frag`.
10. Add runtime controls and diagnostics.
11. Validate one-map shadows with Sponza.
12. Add cascades and stabilization.
13. Add debug views.
14. Run build/tests/smoke and capture in RenderDoc.
