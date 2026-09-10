# Custom shader effects

Effects use the existing renderer and graph. Applications supply shader assets, named values and
resource bindings; the framework creates pipelines, descriptors, fullscreen draws and compute
dispatches. `AddVulkanPass` remains available for advanced recording.

## Color grading example

```csharp
using Njulf.Graphics;
using Njulf.Core.Math;

var asset = Content.Load<ShaderEffectAsset>("Assets/Effects/color_grade.njeffect.json");
foreach (var parameter in asset.Parameters)
    Console.WriteLine($"{parameter.Name}: {parameter.Type}, default {parameter.DefaultValue}");

// Keep this registration until Unload, then dispose it.
var grade = GraphicsDevice.AddPostProcessEffect("Game.ColorGrade", asset);
grade.SetParameter("Saturation", .5f);
grade.SetParameter("Contrast", 1.1f);
grade.SetParameter("Tint", new Vector3(1, .95f, .9f));
grade.Enabled = true;
```

Run `dotnet run --project Njulf.ApiExamples -c Development -- --example effects`.
**G** switches identity/warm desaturated grading; **E** disables/enables the effect.
Finite runs apply grading after 60 full-quality frames:

```powershell
$env:NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY = Join-Path (Get-Location) 'artifacts/effects/runtime-cache/pipelines'
$env:NJULF_PIPELINE_BINARY_CACHE_DIRECTORY = Join-Path (Get-Location) 'artifacts/effects/runtime-cache/binaries'
$env:NJULF_DDGI_WARM_CACHE_DIR = Join-Path (Get-Location) 'artifacts/effects/runtime-cache/ddgi'
dotnet run --project Njulf.ApiExamples -c Development -- --example effects --frames 180 --validation --capture artifacts/effects/graded.png
dotnet run --project Njulf.ApiExamples -c Development -- --example effects --frames 180 --validation --no-aa --capture artifacts/effects/graded-no-aa.png
```

Capture paths must be new. The complete asset/GLSL example is in `Njulf.ApiExamples/Assets/Effects`.

## Asset build and interface

Import the build-only task and declare application GLSL files with unique basenames:

```xml
<ItemGroup>
  <NjulfEffectShader Include="Assets\Effects\*.frag;Assets\Effects\*.comp" />
  <None Update="Assets\Effects\*.njeffect.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
<Import Project="..\Njulf.ShaderBuild\Njulf.Effects.targets" />
```

This reuses `CompileNjulfShaderArtifacts`, its include/source hashing and cache. Override
`NjulfGlslangValidator` for the compiler location and `NjulfEffectShaderCacheDirectory` for the cache.
Compiled `.spv` files are copied beside manifests in build/publish output. The SDK is needed only at
build time; runtime never compiles GLSL or references the build task.

```json
{
  "schemaVersion": 1,
  "kind": "Fullscreen",
  "shader": "grade.frag.spv",
  "parameters": [{ "name": "Saturation", "type": "Float", "default": 1, "minimum": 0, "maximum": 2 }],
  "resources": [{ "name": "SourceColor", "binding": 0, "kind": "SampledTexture2D", "access": "Read" }]
}
```

- `kind` is `Fullscreen` or `Compute`; `shader` is a relative `.spv` path. Compute also declares literal
  `workgroupSize` `{ "x": 8, "y": 8, "z": 1 }` matching GLSL.
- Entry point is `main`. Fullscreen input is `vec2` UV at location 0; output is one `vec4` at location 0.
- Parameter types are `Float`, `Int`, `UInt`, `Vector2`, `Vector3`, `Vector4`. Defaults are numbers or
  arrays of the declared length. Setters require exact C# `float`, `int`, `uint`, or `Njulf.Core.Math`
  vector types. Range hints do not clamp values; non-finite values are rejected.
- Parameters occupy **16-byte push-constant slots in manifest order**, at offsets 0, 16, 32, etc.,
  with at most eight slots. Declare explicit GLSL offsets; unused slot bytes are zero.
- All resources use descriptor set 0 at their declared binding numbers. `SampledTexture2D` uses a
  combined linear-clamp sampler at mip 0. `StorageImage2D` uses `rgba16f` and a framework render target.
  `StorageBuffer` uses a bounded byte range. Access is `Read`, `Write`, or `ReadWrite`; sampled textures
  are read-only. Fullscreen storage bindings are read-only; use compute for storage writes.
- Metadata must match compiled shaders. There is no reflection, GLSL declaration generation or proof
  of shader memory safety. Invalid metadata/ownership/ranges fail before publishing a registration or
  rebind; shader/native initialization errors follow the renderer's fault/cleanup path.

`ShaderEffectAsset` is an immutable CPU snapshot. Existing content loading, async loading, caching and
scopes work without a GPU. It can also be constructed from compiled bytes. Registrations retain their
asset snapshot, so unloading its content scope does not invalidate running effects.

## Ordinary fullscreen and compute passes

```csharp
var pass = GraphicsDevice.AddFullscreenEffect("Game.Filter", fragmentAsset, EffectStage.BeforeScene,
    [new("SourceColor", Texture: source)], new EffectImage(Texture: target));

var compute = GraphicsDevice.AddComputeEffect("Game.Compute", computeAsset, EffectStage.BeforeScene,
    [new("SourceColor", Texture: target), new("Destination", Texture: output), new("Data", Buffer: buffer)],
    new EffectDispatchSize(256, 128));
```

Bind exactly the resources declared by each asset. Compute extents are **threads**, rounded up to
workgroups: shaders must bounds-check excess invocations. Compute uses the graphics queue. The graph
derives inter-pass/cross-frame barriers from declared access. Initialize every output pixel subsequently
read. Fullscreen output replaces the destination without blending.

| Stage | View images |
| --- | --- |
| `BeforeScene` | Application resources only |
| `AfterScene` | HDR `SceneColor`; sampled read-only `SceneDepth`, before fog |
| `AfterToneMapping` | Sampled/attachment `PostProcessColor`, before AA |
| `AfterPostProcessing` | Attachment-only `Backbuffer`, before ImGui |

For view images, use `ViewImage: EffectViewImage.SceneColor` instead of `Texture`.
Sampling/writing the same image through aliases is rejected. Explicit compute storage read/write uses
one binding. `Rebind(bindings)` replaces inputs/resources; overloads additionally accept a fullscreen
`EffectImage` destination or a compute `EffectDispatchSize`. The entire replacement is validated and
retained first. Resize ordinary targets by creating replacements, rebinding producers/consumers and
then disposing the old wrappers. Failed rebinding preserves previous bindings.

## Post-processing and lifetime

Post effects require sampled `SourceColor`; compute post effects also require write-only storage
`Destination`. These bindings and dispatch dimensions are automatic: provide only additional bindings.
Each effect writes an RGBA16F scratch image, then a separate graph pass copies back into the tone-mapped
image. Scratch dimensions follow the actual AA input extent, or presentation extent with AA disabled.
Fog/exposure/bloom precede grading; AA and UI follow it. The presentation path applies encoding exactly
once. Direct backbuffer shaders instead operate in the attachment's output color space, like native passes.

Operations require the device thread after renderer initialization. Changes apply at production frame
boundaries; bootstrap frames do not execute effects. Registration order stays stable, including after
disable/re-enable. Parameter changes do not rebuild pipelines. Post-effect changes invalidate TAA history
so old grades do not bleed into new frames; editing disabled effects preserves history. With no enabled post effects, the normal presentation route
is retained. Frequently animating a post-effect parameter therefore resets TAA on those frames.

Registrations retain resources independently of caller wrappers. Explicitly supplied disposed/foreign
resources are rejected; automatic resizing preserves previously retained resources after callers dispose
their wrappers. Disabled effects leave the graph but retain resources for reuse. Dispose registrations in
`Unload`; native resources retire after submitted GPU work completes. Renderer shutdown and partial
initialization use the existing cleanup lifecycle.

This version has no generic editor panel, runtime compilation/hot reload, reflection, parameter matrices/
arrays, scene serialization, arbitrary shader stages, multi-view support or custom async scheduling.
