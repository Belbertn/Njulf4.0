# Phase 3: Bloom And Emissive Polish Detailed Implementation Plan

Goal: make emissive materials, highlights, and bright direct lighting feel intentional by adding a production HDR bloom pipeline. Bloom should be stable during camera movement, controllable at runtime, easy to debug in RenderDoc, and cheap enough to keep enabled in the default sample scene.

Phase 3 assumes Phase 2 is complete:
- Opaque and transparent lighting render into `HDR Scene Color`.
- Scene color format is `R16G16B16A16Sfloat`.
- `ToneMapCompositePass` writes the final LDR image to the swapchain.
- Manual exposure and tone mapping exist.
- `RenderSettings` exists and is accessible from `VulkanRenderer`.
- Render targets are owned by a `RenderTargetManager` or equivalent.

Current relevant baseline:
- glTF emissive material data is imported and uploaded.
- `forward.frag` already computes:
  - material emissive factor.
  - emissive texture sample.
  - `ambient + directLighting + emissive`.
- The renderer has pass labels and debug object names.
- `NjulfHelloGame --smoke-frames 3` exists for validation smoke testing.

## Target Frame Shape

Final Phase 3 pass order:
1. `DepthPrePass`
2. `HiZBuildPass`
3. `TiledLightCullingPass`
4. `ForwardPlusPass` into HDR scene color
5. `TransparentForwardPass` into HDR scene color
6. `BloomExtractPass`
7. `BloomDownsamplePass`
8. `BloomUpsamplePass`
9. `ToneMapCompositePass` with bloom combine
10. Present

If implementation uses one pass object that loops over mips, keep RenderDoc labels per mip:
- `BloomDownsamplePass Mip 1`
- `BloomDownsamplePass Mip 2`
- `BloomUpsamplePass Mip 3`
- etc.

## Scope

Phase 3 includes:
- Bright-pass extraction from HDR scene color.
- Bloom mip chain render targets.
- Downsample blur.
- Upsample/combine blur.
- Runtime controls for intensity, threshold, knee, radius, and enable/disable.
- Debug views for bloom mask and bloom mip levels.
- Diagnostics for bloom state and CPU pass record times.

Phase 3 does not include:
- Lens dirt.
- Starbursts/anamorphic streaks.
- Auto exposure.
- Per-material bloom masks.
- Temporal bloom stabilization beyond using stable filtering and mip-chain sizes.

## Core Design Decisions

1. Use a dual-filter bloom pipeline rather than separable Gaussian first. It gives good quality with fewer passes and naturally uses a mip chain.
2. Store bloom chain targets as `R16G16B16A16Sfloat` initially for simplicity and quality.
3. Start the bloom chain at half resolution. Full-resolution extraction is allowed, but the first blur target should be half-res to reduce cost.
4. Combine bloom before tone mapping, not after. Bloom is HDR light energy and should feed the tone mapper.
5. Keep bloom independent of exposure. Threshold should operate on HDR scene color before tone mapping, with optional exposure-aware preview only as a later refinement.
6. Use `ToneMapCompositePass` to add final bloom to scene color before tone mapping.

## Settings

Extend `RenderSettings` with bloom settings.

Suggested structure:
```csharp
public sealed class BloomSettings
{
    public bool Enabled { get; set; } = true;
    public float Intensity { get; set; } = 0.08f;
    public float Threshold { get; set; } = 1.0f;
    public float Knee { get; set; } = 0.5f;
    public float Radius { get; set; } = 0.65f;
    public int MipCount { get; set; } = 6;
    public BloomDebugView DebugView { get; set; } = BloomDebugView.None;
    public int DebugMipLevel { get; set; } = 0;
}
```

Recommended limits:
- `Intensity`: `0.0` to `2.0`
- `Threshold`: `0.0` to `20.0`
- `Knee`: `0.0` to `1.0`
- `Radius`: `0.0` to `1.0`
- `MipCount`: `1` to `8`, clamped by framebuffer size

Debug views:
```csharp
public enum BloomDebugView
{
    None,
    ExtractMask,
    DownsampleMip,
    UpsampleResult,
    BloomOnly
}
```

## Render Targets

Add bloom render targets to `RenderTargetManager`.

Suggested resources:
- `BloomExtract`: half-resolution `R16G16B16A16Sfloat`
- `BloomMipChain`: array/list of half, quarter, eighth, etc. resolution targets
- Optional `BloomScratch`: only if the chosen upsample path needs ping-pong targets

Each target must:
- validate non-zero extent.
- be recreated on swapchain resize.
- have debug names:
  - `Bloom Extract`
  - `Bloom Mip 0`
  - `Bloom Mip 1`
  - etc.
- track layout.
- be registered in bindless descriptors or passed through per-pass descriptors according to existing renderer style.

Recommended extent calculation:
1. Base bloom size = `max(1, swapchainExtent / 2)`.
2. Each next mip = `max(1, previous / 2)`.
3. Stop when either:
   - requested mip count is reached.
   - width and height are both 1.

Tests:
- mip chain extent generation for odd sizes.
- min-size handling.
- mip count clamping.
- resize recreation keeps stable target count where possible.

## Shader Passes

### 1. Bloom Extract

Shader:
- `bloom_extract.comp` or fullscreen fragment pass.

Recommended: compute shader, because it avoids extra graphics pipeline boilerplate and writes directly to storage image.

Inputs:
- HDR scene color sampled image.
- bloom threshold.
- bloom knee.

Output:
- `BloomExtract` / first bloom mip.

Soft threshold function:
```glsl
float brightness = max(max(color.r, color.g), color.b);
float soft = brightness - threshold + knee;
soft = clamp(soft, 0.0, 2.0 * knee);
soft = soft * soft / max(4.0 * knee, 0.0001);
float contribution = max(brightness - threshold, soft) / max(brightness, 0.0001);
vec3 extracted = color * contribution;
```

If `Knee == 0`, use hard threshold.

Pass behavior:
- transition HDR scene color to shader read.
- transition extract target to storage write or color attachment.
- clear or fully overwrite output.

### 2. Bloom Downsample

Shader:
- `bloom_downsample.comp`

Inputs:
- previous bloom mip.

Output:
- next bloom mip.

Filter:
- Use a weighted 13-tap downsample or equivalent dual-filter downsample.
- Sample in linear HDR space.
- Use bilinear texture sampling.
- Clamp UVs to avoid edge artifacts.

Dispatch:
- one thread per output pixel.
- local size such as `8x8` or `16x16`.

Debug labels:
- label each mip dispatch.

### 3. Bloom Upsample

Shader:
- `bloom_upsample.comp`

Inputs:
- current lower-resolution mip.
- next higher-resolution mip.

Output:
- higher-resolution mip or scratch target.

Filter:
- Tent filter around the lower mip.
- Additively blend lower mip into the higher mip using radius.

Implementation options:
1. Storage image output with read from two sampled images.
2. Graphics pass with additive blending.

Recommended: compute shader with explicit read/write targets. If read/write aliasing becomes a hazard, use a scratch target or ping-pong target.

Final output:
- `BloomFinal`, usually mip 0 after upsample accumulation.

## Composite Integration

Update `ToneMapCompositePass` to sample bloom final texture.

Combine order:
```glsl
vec3 hdr = sceneColor.rgb;
vec3 bloom = texture(bloomFinal, uv).rgb;
hdr += bloom * bloomIntensity;
vec3 ldr = ToneMap(hdr * exposure);
```

Debug modes:
- `BloomOnly`: show bloom final before tone mapping.
- `ExtractMask`: show extract target.
- `DownsampleMip`: show selected mip.
- `UpsampleResult`: show final accumulated bloom.

If bloom is disabled:
- skip bloom passes.
- composite should not sample invalid bloom textures.
- use a default black texture or branch on `BloomEnabled`.

## Bindless And Descriptor Strategy

Preferred for consistency:
- Reserve static bindless texture indices for bloom resources only if the number is fixed.
- For a variable mip chain, either:
  - allocate dynamic texture indices per bloom target and store them in pass state.
  - use pass-local descriptor sets for storage images if this is cleaner for compute.

Given the current bindless design, recommended first path:
- Add dynamic texture allocation for sampled bloom targets.
- Add a small storage-image descriptor layout for bloom compute outputs if bindless storage images are not already supported.

Do not force bloom storage images into the existing storage-buffer bindless heap.

Tests:
- descriptor allocation is stable across resize.
- texture indices are valid before pass execution.
- bloom disabled path does not require bloom texture descriptors.

## Pipeline Objects

Create:
- `BloomPipeline.cs`
- `BloomExtractPass.cs`
- `BloomDownsamplePass.cs`
- `BloomUpsamplePass.cs`

Or one coordinator:
- `BloomPass.cs`

Recommended structure:
- `BloomPipeline` owns compute pipeline layouts and pipelines.
- `BloomPass` coordinates extract, downsample, and upsample dispatches.
- Render graph still exposes explicit labels for extract/downsample/upsample, even if one object handles all dispatches.

Debug names:
- `Bloom Extract Compute Pipeline`
- `Bloom Downsample Compute Pipeline`
- `Bloom Upsample Compute Pipeline`
- `Bloom Compute Pipeline Layout`
- `Bloom Pipeline Cache`

## Render Graph Integration

Update pass order contract after Phase 2:
```text
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

If bloom is disabled, the pass objects can remain in the graph and early-out. This keeps RenderDoc pass structure stable and avoids dynamic graph mutation.

## Runtime Controls In NjulfHelloGame

Add controls to `SampleInputController`.

Suggested keys:
- `F5`: toggle bloom
- `F6`: cycle bloom debug view
- `PageUp/PageDown`: bloom intensity up/down
- `Home/End`: threshold up/down
- `Insert/Delete`: radius up/down
- `F7` or modifier key: cycle debug mip level

Console output should include:
- enabled/disabled
- intensity
- threshold
- knee
- radius
- debug view
- debug mip level

Avoid printing every frame.

## Diagnostics

Extend `RendererDiagnostics` with:
- `BloomEnabled`
- `BloomMipCount`
- `BloomBaseWidth`
- `BloomBaseHeight`
- `BloomFormat`
- `BloomIntensity`
- `BloomThreshold`
- `BloomRadius`
- `CpuBloomExtractRecordMicroseconds`
- `CpuBloomDownsampleRecordMicroseconds`
- `CpuBloomUpsampleRecordMicroseconds`

If GPU timing queries are not implemented, do not fake GPU bloom timing. Use CPU command-record timings and add GPU timings later in Phase 18/19.

Update `SampleDiagnosticsReporter` to print a bloom line.

## Validation And QA

Required commands:
```powershell
dotnet build Njulf.sln
dotnet test Njulf.sln
dotnet run --project NjulfHelloGame -- --smoke-frames 3
```

Runtime validation:
- startup validation-clean.
- normal frame validation-clean.
- resize validation-clean.
- bloom enabled/disabled validation-clean.
- debug view changes validation-clean.
- shutdown validation-clean.

RenderDoc checklist:
- `BloomExtractPass` appears after transparent scene rendering.
- `BloomDownsamplePass` dispatches each mip in order.
- `BloomUpsamplePass` accumulates from smallest mip back to largest bloom mip.
- HDR scene color is sampled but not overwritten by bloom.
- `ToneMapCompositePass` samples bloom final and writes swapchain.
- Bloom images and views are named.
- Layout transitions are explicit and correct.

Visual checklist:
- emissive materials visibly glow.
- bright direct lighting blooms without crushing the whole frame.
- disabling bloom gives an immediate A/B comparison.
- bloom remains stable during camera movement.
- threshold and knee can isolate highlights.
- bloom debug views show expected mask/mips.

## Tests

Add CPU tests for:
- bloom settings defaults.
- bloom settings clamping/validation.
- bloom mip extent generation.
- odd framebuffer sizes.
- minimum framebuffer sizes.
- pass order includes bloom before composite.
- bloom disabled path keeps diagnostics consistent.

Shader tests:
- `bloom_extract.comp` compiles.
- `bloom_downsample.comp` compiles.
- `bloom_upsample.comp` compiles.
- updated composite shader compiles.

## Acceptance Criteria

Phase 3 is done when:
1. Bloom extracts bright HDR scene color after opaque and transparent rendering.
2. Bloom downsample and upsample passes produce a stable blurred highlight texture.
3. Emissive textures contribute naturally to bloom.
4. Bloom combines before tone mapping.
5. Bloom can be enabled/disabled at runtime.
6. Bloom intensity, threshold, knee, and radius are adjustable.
7. Debug views exist for extract mask, selected mip, and bloom-only output.
8. Bloom resources and passes are named in RenderDoc.
9. Diagnostics report bloom settings, mip count, target size, and CPU record timings.
10. Build, tests, shader compilation, and `NjulfHelloGame --smoke-frames 3` pass.

## Risks And Mitigations

- Over-bloomed image: start with conservative intensity and threshold defaults.
- Washed out tone mapping: combine bloom before tone mapping and inspect exposure interactions.
- Compute read/write hazards: use explicit image barriers between every mip dispatch.
- Resize bugs: regenerate all bloom target extents from swapchain extent and smoke-test resize.
- Descriptor churn: allocate/re-register bloom descriptors during render-target recreation only.
- Excess cost: start half-resolution and limit default mip count to 6.
- Debug complexity: keep bloom debug views centralized in composite.

## Suggested Implementation Order

1. Add `BloomSettings` to `RenderSettings`.
2. Add bloom target allocation and mip extent tests.
3. Add bloom resource debug names and resize handling.
4. Add bloom shader files and shader compile tests.
5. Add `BloomPipeline`.
6. Add `BloomExtractPass`.
7. Add `BloomDownsamplePass`.
8. Add `BloomUpsamplePass`.
9. Integrate bloom final texture into `ToneMapCompositePass`.
10. Add runtime controls in `NjulfHelloGame`.
11. Add diagnostics and reporter output.
12. Update pass-order tests.
13. Run build/tests/smoke.
14. Capture and inspect in RenderDoc.
