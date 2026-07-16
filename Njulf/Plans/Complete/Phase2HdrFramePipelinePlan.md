# Phase 2: HDR Frame Pipeline Detailed Implementation Plan

Goal: move Njulf from direct swapchain lighting to a production HDR frame pipeline. Opaque and transparent scene lighting should render into an HDR scene color target, then a final composite pass should tone-map and write LDR output to the swapchain.

Current baseline:
- `NjulfHelloGame` loads Sponza through `SampleSceneLoader` and has a bounded smoke mode.
- The renderer uses Vulkan dynamic rendering.
- Phase 1 pass order is guarded as:
  - `DepthPrePass`
  - `HiZBuildPass`
  - `TiledLightCullingPass`
  - `ForwardPlusPass`
  - `TransparentForwardPass`
- `ForwardPlusPass` and `TransparentForwardPass` currently render to swapchain image views.
- `VulkanRenderer.Clear` also clears the swapchain directly.
- Debug names and pass labels exist, so new resources and passes must be named.

## Target Frame Shape

Final Phase 2 pass order:
1. `DepthPrePass`
2. `HiZBuildPass`
3. `TiledLightCullingPass`
4. `ForwardPlusPass` into HDR scene color
5. `TransparentForwardPass` into HDR scene color
6. `ToneMapCompositePass` into swapchain
7. Present

The swapchain should no longer receive scene lighting directly. It should only receive the final composited LDR image.

## Core Design Decisions

1. Use `Format.R16G16B16A16Sfloat` for the main scene color target.
2. Keep the first implementation single-sampled. Add MSAA later if needed.
3. Keep exposure manual for Phase 2. Auto exposure belongs in a later slice because it needs luminance reduction, temporal smoothing, and more debugging.
4. Use ACES fitted as the default tone mapper, with a second simple operator available for comparison.
5. Keep final composite as a fullscreen triangle pass. Avoid adding a full post-processing graph until bloom and AA require it.
6. Preserve current depth and Hi-Z behavior. Phase 2 should change color ownership only.

## Implementation Steps

### 1. Add Render Target Ownership

Create a renderer-owned render target abstraction, likely under `Njulf.Rendering/Resources`.

Suggested files:
- `RenderTarget.cs`
- `RenderTargetManager.cs`

Initial responsibilities:
- Create 2D color images with:
  - `ImageUsageFlags.ColorAttachmentBit`
  - `ImageUsageFlags.SampledBit`
  - `ImageUsageFlags.TransferSrcBit` for debugging/screenshots later
- Create image views.
- Track current layout.
- Recreate targets on swapchain resize.
- Assign debug names to images and image views.
- Destroy resources deterministically.

Minimum API:
```csharp
public sealed class RenderTargetManager : IDisposable
{
    public RenderTarget SceneColor { get; }
    public void Recreate(Extent2D extent);
}
```

`SceneColor` should be named `HDR Scene Color` in Vulkan debug utils.

### 2. Register HDR Scene Color For Sampling

Add a static bindless texture index for scene color in `BindlessIndexTable`.

Example:
- `HdrSceneColorTexture`

Register the HDR scene color image view after creation and after resize:
- image layout for descriptor should be `ShaderReadOnlyOptimal`
- actual render layout should transition to and from `ColorAttachmentOptimal`

Add or update tests in `BindlessIndexTests` so C# and shader-side constants remain aligned.

### 3. Add Layout Transition Helpers For Render Targets

Current swapchain layout transitions are localized in `VulkanRenderer`. Phase 2 needs equivalent handling for owned render targets.

Add a small utility method or render-target method that transitions:
- `Undefined` or `ShaderReadOnlyOptimal` to `ColorAttachmentOptimal` before lighting.
- `ColorAttachmentOptimal` to `ShaderReadOnlyOptimal` before composite.

Keep barriers explicit and readable in RenderDoc.

Required access/stage pairs:
- Before color write:
  - source: previous layout dependent
  - destination stage: `ColorAttachmentOutputBit`
  - destination access: `ColorAttachmentWriteBit`
- Before sampling:
  - source stage: `ColorAttachmentOutputBit`
  - source access: `ColorAttachmentWriteBit`
  - destination stage: `FragmentShaderBit`
  - destination access: `ShaderSampledReadBit`

### 4. Change Forward Pass Color Target

Modify `ForwardPlusPass` constructor or execution context so it receives the HDR scene color view instead of using `_swapchain.ImageViews[sceneData.ImageIndex]`.

Expected behavior:
- Opaque pass clears HDR scene color.
- Depth should still use `_swapchain.DepthImageView`.
- Color attachment format must be `R16G16B16A16Sfloat`.
- Pipeline creation must use the HDR color format, not the swapchain surface format.

This means `MeshPipeline` can no longer be initialized only with `_swapchain.SurfaceFormat`.

Recommended options:
1. Change `MeshPipeline` to accept the scene color format for forward pipelines.
2. Keep depth pipeline depth-only.
3. Recreate forward pipelines when HDR format changes, even though it normally will not.

### 5. Change Transparent Pass Color Target

Modify `TransparentForwardPass` to render into the same HDR scene color target.

Expected behavior:
- Load op should be `Load`, not `Clear`, so transparent objects blend over opaque HDR color.
- Store op should be `Store`.
- Blend state can initially remain existing alpha blending.
- The pass should be skipped cleanly when transparent meshlet count is zero.

### 6. Add Tone Mapping And Composite Shaders

Add a fullscreen triangle shader pair or a single vertex-less shader setup:
- `composite.vert`
- `tonemap_composite.frag`

Responsibilities:
- Sample HDR scene color from bindless texture index.
- Apply exposure.
- Apply tone mapping.
- Convert linear color to the swapchain output convention explicitly.

Recommended shader controls:
```glsl
layout(push_constant) uniform CompositePush
{
    uint SceneColorTextureIndex;
    float Exposure;
    uint ToneMapper;
    uint DebugViewMode;
} pc;
```

Tone mapper options:
- `0`: None/debug clamp
- `1`: Reinhard
- `2`: ACES fitted default

Linear-to-sRGB:
- If swapchain format is sRGB, output linear and let the format conversion happen.
- If swapchain format is UNORM, perform shader linear-to-sRGB conversion before writing.

Make this decision explicit in C# and shader push constants. Do not rely on comments or implicit assumptions.

### 7. Add Composite Pipeline And Pass

Create:
- `CompositePipeline.cs`
- `ToneMapCompositePass.cs`

`ToneMapCompositePass` should:
- Begin dynamic rendering against the current swapchain image view.
- Clear or load the swapchain color attachment as appropriate.
- Bind composite pipeline and bindless descriptors.
- Push composite settings.
- Draw a fullscreen triangle.

Debug label:
- `ToneMapCompositePass`

Debug names:
- `Tone Map Composite Pipeline`
- `Tone Map Composite Pipeline Layout`
- `Tone Map Composite Pipeline Cache`

### 8. Add Render Settings For Phase 2

Add a minimal render settings object before hard-coding more globals.

Suggested file:
- `Njulf.Rendering/Data/RenderSettings.cs`

Initial properties:
```csharp
public sealed class RenderSettings
{
    public float Exposure { get; set; } = 1.0f;
    public ToneMapper ToneMapper { get; set; } = ToneMapper.AcesFitted;
    public bool ShowRawHdrSceneColor { get; set; }
}
```

Do not implement quality presets yet. Phase 17 handles full settings and persistence.

Expose on `VulkanRenderer`:
- `RenderSettings Settings { get; }`

### 9. Add Runtime Controls In NjulfHelloGame

Update `SampleInputController` to allow quick visual validation:
- Toggle raw HDR view.
- Cycle tone mapper.
- Increase/decrease exposure.

Keep console output concise, similar to the existing meshlet debug toggle.

Suggested keys:
- `F6`: cycle tone mapper
- `F7`: toggle raw HDR scene color
- `[` and `]`: exposure down/up

### 10. Update Diagnostics

Extend diagnostics with:
- HDR enabled flag.
- Scene color format.
- Exposure.
- Tone mapper.
- Composite CPU record time.

If adding GPU timing query support is still deferred, keep GPU composite timing as zero or omit it until Phase 18/19 adds query infrastructure. Do not fake GPU timing.

Update `SampleDiagnosticsReporter` to print:
- HDR enabled.
- tone mapper.
- exposure.
- composite record time.

### 11. Update Pass Order Contracts

Update the Phase 1 order constant or rename it to a general production pass order.

Expected new test:
```text
DepthPrePass
HiZBuildPass
TiledLightCullingPass
ForwardPlusPass
TransparentForwardPass
ToneMapCompositePass
```

The test should fail if the composite pass is missing or appears before transparency.

### 12. Resize Handling

On swapchain recreation:
1. Recreate swapchain.
2. Recreate depth resources as today.
3. Recreate HDR scene color target at the new extent.
4. Re-register HDR scene color in bindless descriptors.
5. Recreate pipelines if their formats or attachment metadata depend on target format.
6. Notify render graph passes.

The existing smoke mode should continue to resize after at least one frame.

### 13. Clear Path Cleanup

Decide what `VulkanRenderer.Clear(Color color)` means after HDR:
- Preferred: clear HDR scene color, not swapchain.
- Alternative: mark it as legacy and avoid calling it in scene rendering.

Whatever is chosen, document it in code and make sure it does not accidentally begin rendering against the swapchain before composite.

### 14. Tests

Add CPU tests for:
- Render target extent validation.
- Render target byte-size/format assumptions if exposed.
- Bindless index contract for HDR scene color.
- Pass order includes `ToneMapCompositePass`.
- `RenderSettings` defaults.
- Exposure clamping if clamping is implemented.

Keep GPU validation as smoke-run based unless a GPU test harness already exists.

### 15. Validation And Manual QA

Required commands:
```powershell
dotnet build Njulf.sln
dotnet test Njulf.sln
dotnet run --project NjulfHelloGame -- --smoke-frames 3
```

RenderDoc checklist:
- Capture `NjulfHelloGame`.
- Confirm Forward+ writes `HDR Scene Color`.
- Confirm Transparent writes `HDR Scene Color`.
- Confirm `ToneMapCompositePass` is the only pass writing the swapchain before present.
- Confirm debug labels and resource names are readable.
- Inspect HDR scene color before composite and verify bright values can exceed `1.0`.

Validation checklist:
- Startup has no validation errors.
- Resize has no validation errors.
- Three-frame smoke shutdown has no validation errors.
- Toggling raw HDR debug view has no validation errors.
- Exposure changes do not recreate pipelines.

## Acceptance Criteria

Phase 2 is done when:
1. Opaque and transparent lighting render into `R16G16B16A16Sfloat` scene color.
2. Swapchain rendering is limited to `ToneMapCompositePass`.
3. Manual exposure works at runtime.
4. ACES fitted tone mapping is available and selected by default.
5. Raw HDR debug view is available.
6. Bright emissive/direct lighting can exceed `1.0` in scene color before tone mapping.
7. `NjulfHelloGame --smoke-frames 3` passes and exercises resize.
8. CPU tests pass.
9. RenderDoc shows named passes and named HDR resources.

## Risks And Notes

- Swapchain format handling must be explicit. Writing gamma-correct output twice will make the image washed out.
- Pipeline format changes are easy to miss because dynamic rendering bakes attachment formats into pipeline creation.
- Transparent blending in HDR should usually stay linear. Do not apply tone mapping before transparency.
- HDR scene color should not be added as a dynamic imported texture; use a reserved bindless index.
- Auto exposure should not be bundled into Phase 2. It adds enough complexity to deserve its own follow-up.

## Suggested Implementation Order

1. Add `RenderSettings` and pass-order test update.
2. Add `RenderTarget` and `RenderTargetManager`.
3. Create and name HDR scene color in `VulkanRenderer.Initialize`.
4. Recreate/re-register HDR scene color on resize.
5. Change mesh pipeline color format from swapchain to HDR.
6. Change Forward+ and transparent passes to target HDR scene color.
7. Add composite shaders.
8. Add composite pipeline and pass.
9. Add exposure/tone mapper/debug-view controls.
10. Extend diagnostics.
11. Run build/tests/smoke.
12. Capture in RenderDoc.
