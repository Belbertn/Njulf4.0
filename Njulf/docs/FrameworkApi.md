# Framework API

The framework has three access levels on the same renderer and device:

| Level | Entry point | Use |
| --- | --- | --- |
| Game | `Game`, `Content`, `Scene`, `Model` | Run a game and load scene content. |
| Graphics | `Game.GraphicsDevice`, `Njulf.Graphics` | Create typed resources, apply settings, inspect capabilities. |
| Vulkan | `Njulf.Graphics.Vulkan` | Inspect native allocations and register scoped rendering passes. |

Reference **Njulf.Framework** for `Njulf.Core.Game`. The host composes Core, Rendering,
Assets and Input; Core remains independent of those concrete implementations.
`GameTime` and resource interfaces live in Core. Owned graphics wrappers live in
Rendering. Framework and Rendering emit XML documentation for IntelliSense.

## Assemblies and namespaces

| Assembly | Responsibility | Allowed Njulf dependencies |
| --- | --- | --- |
| Core | Math, geometry, animation, time | None; BCL only |
| Graphics | Public resources, material/settings/capability data, scenes and cameras | Core |
| Input | Typed button actions and native input integration | Core |
| Assets | Runtime loading, cooked readers, bounded source decoding and shared format/report contracts | Core, Graphics |
| Rendering | Vulkan implementation and native extension points | Core, Graphics, Assets, Shaders |
| Framework | Default application/window/service composition | Core, Graphics, Input, Assets, Rendering |
| Assets.Tooling | Offline cooking, migration, asset database writing and micromap producers | Assets |
| Editor | Optional consumer of supported public framework/renderer APIs | Runtime libraries |

The shader build task remains a build-only dependency; it is not a runtime package dependency.
Runtime assemblies never depend on Assets.Tooling, AssetTool or Editor. Shared source decoders
remain in Assets because development loading and transport analysis use them at runtime.
The encoder package also supplies decoding, so its runtime dependency is intentional.

Assembly location does not dictate namespace: scene/camera namespaces remain
`Njulf.Core.Scene` and `Njulf.Core.Camera` for a focused migration. Ordinary resource,
material, sampler, settings and capability types use `Njulf.Graphics`. Import
`Njulf.Rendering` for `RenderingOptions`; only registration extensions live in
`Microsoft.Extensions.DependencyInjection`. Native commands, handles, synchronization and
advanced renderer services remain explicitly under Rendering or `Njulf.Graphics.Vulkan`.

Ordinary APIs use `Njulf.Core.Math`. Positions and lengths use scene units; mouse positions
and motion use pixels. Camera field of view is in radians. Game transforms use row vectors,
translation in M41–M43, and view * projection order. Native and importer adapters convert
math types at their boundaries; these conventions do not change GPU layouts.

XML files are generated beside assemblies and included in NuGet packages. Critical public
contracts have documentation regression checks; Input treats missing XML comments as errors.
Legacy specialist APIs still have documentation gaps and are not covered by a blanket warning
suppression removal. Run `tools/verify-framework-packages.ps1` after a Development build to
check actual local packages and compile consumers without project references.

## Typed button input

Create actions during initialization or loading, then cache the returned references:

```csharp
private Njulf.Input.InputAction jump = null!;
protected override void Load()
{
    jump = Input.CreateAction("Jump");
    jump.AddBinding(new Njulf.Input.InputBinding(Njulf.Input.InputKey.Space));
}
protected override void Update(GameTime time)
{
    base.Update(time);
    if (jump.WasPressed) { /* jump once */ }
}
```

Bindings combine with OR. IsDown is held state; WasPressed/WasReleased describe one input
update, independently of draw cadence. All action states are published before transition
events. Names are case-sensitive registration identifiers; repeated names are rejected.
The manager owns actions, invalidates them on disposal, and requires the game thread.
Bindings use InputKey, MouseButton, JoystickButton, GamepadButton, or a thresholded joystick/gamepad
axis; missing devices are inactive. Button axis thresholds remain strictly greater than +0.5 or
less than -0.5. Scroll is published once per update; mouse motion remains accumulated until
ConsumeMouseDelta. Raw native events remain available for UI adapters.
The procedural example uses Space to toggle visibility. The [gameplay input guide](GameplayInput.md)
covers float/Vector2 actions, mouse deltas, dead zones, contexts, interactive rebinding, persistence,
cursor capture and text through Game.Input. Run `--example input` for a complete small example.

Breaking migration: replace `Njulf.Input.Action` and string polling with cached InputAction
references. Replace raw binding constructors with typed enums. Use `Njulf.Graphics.TextureColorSpace`
everywhere (Linear=0, Srgb=1, HdrLinear=2). Offline clients reference Assets.Tooling for
TextureCooker/ModelAssetCooker; runtime analysis uses TextureSourceDecoder and TextureDecodeOptions.
No compatibility wrappers are provided.

## Ordinary game setup

```csharp
using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Data;

using var game = new TriangleGame();
game.Run();

sealed class TriangleGame : Game
{
    protected override void Load()
    {
        using var mesh = GraphicsDevice.CreateMesh(
            [new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0)],
            [0u, 1u, 2u]);
        using var texture = GraphicsDevice.CreateTexture2D(
            1, 1, [255, 128, 64, 255], TextureColorSpace.Srgb);
        using var material = GraphicsDevice.CreateMaterial(
            MaterialDefinition.Default,
            [new(MaterialTextureSlot.BaseColor, texture)]);
        Scene.Add(new Njulf.Core.Scene.RenderObject(mesh, material));
    }
}
```

Game supplies a Vulkan window, renderer, content manager, input manager, default camera,
and one owned scene. Default Draw renders `Scene` through `Camera`. Default Update
updates the scene; overrides call `base.Update(gameTime)` if they want that behavior.
Input polling and resize propagation are host responsibilities and do not depend on
calling base hooks. ContentRoot defaults to `AppContext.BaseDirectory`; configure it
before Run. Source asset loading retains the existing development opt-in policy.

Override `ConfigureRendering(RenderingOptions)` to change renderer creation settings.
Override `ConfigureServices(IServiceCollection)` to replace or extend the default
registrations before the provider is built. Override `CreateDefaultCamera` for a custom
camera, or register `ICamera`. Do not repeat AddRendering/AddAssets/AddInput in a game.
Scene is owned by Game rather than registered as a second DI singleton.

## Lifecycle and time

1. Construction creates Scene. Other services throw a clear InvalidOperationException
   until host initialization; the window is available during configuration.
2. Run creates the window, registers defaults, calls ConfigureServices, resolves services
   and initializes the renderer. ConfigureRendererBeforeInitialize remains the advanced
   pre-initialization settings hook.
3. Initialize, Load, and then LoadAsync run with graphics, content, input, camera and window available.
   Progressive startup may present bootstrap frames before Load.
   Awaiting in LoadAsync resumes on the device thread through the host synchronization
   context. Input, bootstrap frames, and content uploads continue while loading.
4. Update and Draw receive GameTime with independent scaled elapsed intervals and unscaled
   wall-time fields. Timing begins after LoadAsync completes; first frame intervals are zero.
   Variable simulation remains the default. Optional FixedUpdate provides bounded catch-up
   and interpolation; pause/time scaling also controls renderer animation.
   See [simulation timing and pause](GameTiming.md) for the complete contracts and example.
5. Exit or Dispose during a callback requests exit. After callbacks return, shutdown
   stops content admission and pumps cancellation/continuations until outstanding loads and
   GPU uploads settle, drains preparation, calls Unload once if Initialize started, releases Scene,
   disposes the renderer/provider/input, and finally destroys the native window.
   Services remain available in Unload. Callback exceptions are rethrown from Run after
   cleanup; a cleanup failure is attached to the original exception.

A Game instance runs once. Dispose is idempotent, including before Run. Service access
following disposal throws ObjectDisposedException. Game owns the current scene;
advanced ExchangeScene callers own and must dispose the returned previous scene.

## Content and scene ownership

`Game.Content` exposes `Njulf.Assets.IContentManager`: `Load`, `LoadAsync`,
`PreloadAsync`, `Unload`, and `UnloadAll`. Loading options accept
`IProgress<ContentLoadProgressEvent>`; preload requests retain priority and admission limits.
Progress observers do not affect cache identity. A loaded `Model` is a cache-owned template;
`CreateInstance()` returns an independently owned `ModelInstance`. Unload rejects foreign
assets and instances. Unloading a template preserves resources retained by its instances.

`CreateScope()` returns an independent `IContentScope` sharing the same cache and device.
Equivalent loads acquire one claim per scope, including repeated loads and preloads. Direct
`Game.Content` loads belong to the root lifetime. `Unload` and `UnloadAll` release only that
owner's claims; root unloading preserves other scopes. `UnloadAll` cancels pending loads and
leaves its owner reusable. Scope disposal cancels pending loads and permanently closes that
scope; creating a scope through another scope still creates an independent lifetime.
Dispose/unload on the device thread. Failed releases retain ownership for retry.

`Load<Texture>` / `LoadAsync<Texture>` support PNG/JPEG as one-mip RGBA8 textures.
`ContentLoadOptions.TextureColorSpace` defaults to sRGB; use `Linear` for data maps. Color
space participates in texture identity. Decoding runs on a worker for async loads, while
creation runs through the host upload dispatcher. Standalone image decoding remains available
in Release builds; model cooked-package policy is unchanged. Limits are 256 MiB encoded and
64 megapixels decoded. Without an upload dispatcher, async calls retain synchronous behavior.
Texture creation and scene population each use an indivisible device callback; the frame
budget cannot preempt these operations.

`Load<Material>` / `LoadAsync<Material>` read version-1 `.njmaterial.json` assets:

```json
{
  "schemaVersion": 1,
  "name": "Paint",
  "baseColor": { "r": 0.2, "g": 0.4, "b": 1, "a": 1 },
  "metallic": 0,
  "roughness": 0.65,
  "baseColorTexturePath": "paint.png"
}
```

Optional fields also include `emissive` (`x`, `y`, `z`), `emissiveStrength`, `alphaMode`
(`Opaque`, `Mask`, `Blend`), `alphaCutoff`, `doubleSided`, `normalTexturePath`,
`metallicRoughnessTexturePath`, `occlusionTexturePath`, and `emissiveTexturePath`.
Omitted values use `MaterialDefinition` defaults. Unknown fields/versions are rejected;
documents are limited to 1 MiB. Texture paths are relative to the material file; base-color
and emissive textures use sRGB, other slots use linear. Dependencies are retained by the
material's content entry. Graphics registration still validates authored values.

`LoadScene` / `LoadSceneAsync` create a fresh, scope-owned scene on every call using the
existing scene document format. Model paths retain their Content-root-relative meaning.
Async loading reads the document and prepares its actual model references before population
on the device thread. `SceneLoadOptions` supplies model `ContentOptions` and optional
`Materials` / `ParticleEffects` stores; those stores keep their synchronous device-thread
contracts. Missing stores produce the existing loader errors when a document needs them.

For level transitions, prepare a new scope before releasing the old one:

```csharp
IContentScope nextContent = Content.CreateScope();
Scene next;
try
{
    next = await nextContent.LoadSceneAsync("Scenes/next.njscene.json",
        cancellationToken: cancellationToken);
}
catch
{
    nextContent.Dispose(); // Preparation failed; the old level remains active.
    throw;
}
IContentScope oldContent = levelContent;
ExchangeScene(next);
levelContent = nextContent;
oldContent.Dispose(); // Keep this owner for retry if release fails.
```

The complete runnable example is `dotnet run --project Njulf.ApiExamples -c Development --
--example content --frames 180 --validation`. It demonstrates standalone textures/materials,
two scene files, shared asset reuse, and final root release. Content results are borrowed:
do not directly dispose cached textures/materials or scope-owned scenes. Render objects retain
their own graphics references when assigned a material. Removing the last Content claim does
not destroy resources still retained by an object or in-flight GPU work. The manager owns final
cleanup during shutdown; hosts keep pumping until `IContentLifetime.ActiveOperationCount` settles.
Custom hosts can supply `ContentManager.GraphicsDeviceProvider`; `AddRendering` + `AddAssets`
wire this provider automatically without introducing an Assets-to-Rendering dependency.

The host pumps uploads before gameplay callbacks, including during startup. Defaults are
2 ms, one callback, and 8 MiB per submission. Customize `ContentUploadCpuBudget`,
`ContentUploadMaximumCallbacks`, and `ContentUploadMaximumSubmissionBytes`; override
`OnContentUploadsProcessed` to observe results. `ConfigureAwait(false)` intentionally leaves
the host context; graphics mutation must remain on the device thread.

`Scene.Add` transfers ownership. Duplicate or cross-scene membership is rejected.
An object can have separate rendering and updating roles; `Remove` releases one role,
disposing after the last role. `Detach` removes all roles without disposal so the caller
can transfer the object to another scene. `Clear` disposes owned contents and permits reuse;
failed releases retain ownership for retry. `Dispose` permanently closes the scene.

`Scene.Add(instance)` owns the model group and exposes borrowed render children. Remove or
detach the group together. Its child list and disposal are guarded while attached;
individual transforms and graphics bindings remain editable. Scene lights use `SceneLight`
with typed kinds/attenuation and stable IDs. `Scene.Environment` is an immutable authored
description; absent descriptions use renderer defaults. Scene schema 13 persists environments and texture overrides
and continues to accept older scenes. Renderer quality/allocation settings remain separate.

## Typed resources and ownership

- `RenderObject.Mesh` is `IMesh`; `Material` is `IMaterial`. These are borrowed,
  non-disposable views with no allocation on reads. `ITexture` provides dimensions and
  color space. Creation returns owned, disposable Mesh, Material and Texture wrappers.
- Object construction and assignment retain independent references. Disposing a caller's
  wrapper does not invalidate scene objects, template instances or siblings. A saved
  borrowed view must not outlive its owning object/binding; do not cast it to dispose it.
- Assignment checks disposal, device identity and generation, retains the replacement
  before releasing the old reference, and publishes geometry/material invalidation.
  Mesh replacement updates local bounds. Failed acquisition leaves the old binding intact.
- Model upload adopts initial registrations once. Cloning retains each resource once.
  Scene removal and disposal release owned objects; material copy-on-write preserves siblings.
- The original CreateTexture2D overload accepts exact RGBA8 bytes, explicit Linear/Srgb interpretation and one
  mip. The description overload supports native formats and authored/generated mip chains. Data is consumed synchronously. Typed material assignments select existing binding
  slots; UV and sampler settings come from MaterialDefinition. Each bound occurrence owns
  a texture reference. Callers retain their texture ownership, including after failure.
  Deduplication and the existing material compiler remain in use.
- Public disposal invalidates the owned wrapper immediately. Manager references, mesh
  ranges, material slots and texture descriptors are reclaimed only after the last submitted
  graphics completion serial. Releases during recording wait for that submission; unresolved
  submission faults wait for terminal device idle. No per-resource DeviceWaitIdle is added.
- Graphics operations require the device thread after renderer initialization. Disposed,
  stale and foreign resources fail clearly. Disposal after completed renderer shutdown is
  harmless. Failed release can be retried.

See [resource updates and readback](GraphicsResourceUpdates.md) for standard vertices, dynamic
meshes, formats, mipmaps, buffer uploads and asynchronous transfers.

Advanced manager APIs still accept generation-checked raw handles. GetResourceView exposes
an existing caller-owned handle for typed assignment; RetainResource acquires an owned
wrapper. Raw manager registrations retain their original transfer/accounting contracts.
Static batches and other advanced renderer-specific APIs are outside this RenderObject migration.

## Inspecting and editing materials

`IMaterial.Definition` returns an immutable authored snapshot. Use `with` expressions and
an explicit editing operation on the device thread:

```csharp
IMaterial shared = renderObject.Material!;
shared.UpdateShared(shared.Definition with { RoughnessFactor = 0.25f, EmissiveStrength = 2f });

// Only this object changes. Re-read Material afterwards: copy-on-write may replace its borrowed view.
renderObject.UpdateMaterial(renderObject.Material!.Definition with
{
    BaseColorFactor = new Vector4(0.3f, 0.6f, 1f, 1f)
}, [new(MaterialTextureSlot.BaseColor, replacementTexture)]);

using Texture? snapshot = renderObject.Material!.RetainTexture(MaterialTextureSlot.BaseColor);
// snapshot has independent ownership and survives a later binding change.
renderObject.UpdateMaterial(renderObject.Material.Definition,
    [new(MaterialTextureSlot.BaseColor, null)]); // clear
```

`UpdateShared` changes every alias of the same material, including identical materials
deduplicated during creation/import. `RenderObject.UpdateMaterial` isolates one object or model
mesh part; no-op edits do not create a copy. The permanent default rejects shared editing but
can be edited through an object. Both operations validate and compile before publication.

Typed assignments support all existing slots. Omitted slots retain their textures; a null
assignment clears a slot during updates (creation still rejects null). Preserve raw texture
handles in the definition for omitted slots; use typed assignments to replace them. Sampler
and UV settings remain authored in `MaterialDefinition`. Retained textures must be disposed;
input texture wrappers remain caller-owned, and materials retain their own dependencies.

The editor uses these same operations, defaults to **This object**, and resets that scope on
selection changes. **Shared material** is explicit. The five common PBR texture slots have
file-path Assign/Clear controls. The rendering adapter `MaterialManager.LoadMaterialTexture`
returns an owned texture using existing file loading, caching and mip generation. Base color
and emission use sRGB; normal, metallic/roughness and occlusion use linear data.

Scene schema 13 adds nullable texture paths: absent/null inherits the model binding, an empty
string clears it, and a file path assigns it. Saved file paths are absolute and included in
scene dependencies; relative input paths resolve from the current directory. Embedded model
textures remain inherited, and generated textures are not serialized. Older scenes still load.
Shared edits are saved as per-object values; shared identity is not restored across reload.

The procedural API example uses **M** for shared roughness/emission edits and **N** to isolate
the left object and replace its color texture.

## Native inspection

```csharp
using Njulf.Graphics.Vulkan;
var native = GraphicsDevice.GetVulkanDeviceInfo();
var bindings = GraphicsDevice.GetVulkanMeshBindings(renderObject.Mesh!);
```

Handles are borrowed. Do not destroy them or dispose the returned Vk API. Mesh slices
identify split position, normal/tangent, UV/color and index allocations. The index range
includes generated hierarchy indices and is not a source draw count. Query immediately
before use; growth, compaction, mutation and shutdown can invalidate cached values.
Inspection does not grant command recording or synchronization ownership.

Custom recording is available through `GraphicsDevice.AddVulkanPass`, described below.

## Custom rendering

For ordinary fullscreen/compute shaders, use `ShaderEffectAsset` and
`GraphicsDevice.AddFullscreenEffect`, `AddComputeEffect`, or `AddPostProcessEffect`.
They provide discoverable typed parameters, named bindings, automatic Vulkan setup and the existing
graph lifetime. See [custom effects](CustomEffects.md) and the `--example effects` color-grading example.
The native API below remains available for advanced recording.

Create targets with `GraphicsDevice.CreateRenderTarget2D(width, height)` and storage/transfer
buffers with `GraphicsDevice.CreateBuffer(sizeInBytes)`. Targets are linear RGBA16F, one mip,
one sample, with sampled, storage, color-attachment and transfer usage. Contents start undefined;
initialize them before reading. TextureManager owns images and sampled descriptors. Materials
and pass registrations retain independent references. The graph borrows allocations.
Resize by creating a replacement and calling `registration.Rebind(resources)`; release the
old wrapper when its caller-owned reference is no longer needed.

`AddVulkanPass(description, pass)` returns an owned `VulkanPassRegistration`. Describe every
image and buffer use with a unique local name, access type, Vulkan stages/access masks and
layout or byte range. Compute passes run on the graphics queue. Registration order is stable
within each stage; built-in pass order stays unchanged.

| Stage | Position | View images available |
| --- | --- | --- |
| BeforeScene | Before the first production pass | None; use custom resources or loaded textures |
| AfterScene | Immediately before FogPass | HDR SceneColor, read-only SceneDepth |
| AfterToneMapping | Immediately before AntiAliasingPass | Linear RGBA16F PostProcessColor |
| AfterPostProcessing | After AA, before ImGuiRenderPass | Backbuffer as a color attachment |

These stages apply to the current single view. A target is not a second render view.
The backbuffer is the actual current swapchain image with AA enabled or disabled.
Undeclared, disposed, foreign, incompatible and duplicate image uses are rejected.
Loaded textures permit sampled reads. Attachment/sampling feedback is unsupported; express
storage read/write explicitly. Buffer ranges may overlap across passes; graph binding plans
partition them into shared physical ranges. Custom IDs use a separate internal range.

The registration owns `IVulkanRenderPass` after successful registration. `Initialize` runs
on the device thread after production initialization. `ResourcesChanged` receives declared
image metadata before first recording and after rebind, image-generation or format/extent
changes. Use it to prepare pipelines; use `Record` for commands. Add, remove and rebind apply
before production command recording. Bootstrap frames never run custom passes. Initialization
and view-availability failures surface at that boundary. Dispose must tolerate initialization
not having run or having failed partway through.

`VulkanPassContext` is stack-scoped: its device, command buffer, matrices, frame slot and named
resource lookups are borrowed for that callback. Do not save raw handles for later recording,
submit/present, reset the command buffer, or destroy framework-owned resources. Enter and leave
outside a Vulkan rendering scope. The graph owns inter-pass and cross-frame dependencies,
queue handoffs and submission. The callback owns internal barriers and must leave each image
in `FinalLayout`, or `Layout` when no final layout is specified. Raw Vulkan misuse cannot be
fully checked by this API; descriptor layouts, ranges and native device limits still apply.

`FrameIndex` selects a completed reusable frame slot; `FrameSlotCount` gives its capacity.
Use separate descriptor sets per slot when updating bindings. When replacing user-owned
pipelines or descriptor objects, use `context.Retire(releaseAction)` to defer native destruction.
Registration disposal stops future recording at the next boundary and retires pass disposal
and its retained resources after submitted work completes. Pass `Dispose` destroys its native
objects directly and must be retryable if destruction throws. Recording failure uses the
renderer’s existing fault/abandonment path; cleanup waits for terminal device idle.

See `Njulf.ApiExamples/CustomRenderingExample.cs` for compute writing an image and buffer,
graphics sampling them into HDR scene color, material sampling, target replacement, and an
overlay after post processing. It also applies a scene-resolution rebuild while passes are active.
It uses the existing shader build pipeline. `--async-validation` explicitly exercises the existing
Bloom async path and fails if that path does not actually execute.

## Settings and capabilities

```csharp
var change = new GraphicsSettingsChange { Exposure = 0.01f, AutoExposureEnabled = false };
var preview = GraphicsDevice.Settings.Preview(change);
var result = await GraphicsDevice.Settings.ApplyAsync(change, cancellationToken);
```

`Preview` is pure and returns per-field impacts/reasons. Presets apply before explicit overrides.
Accepted changes wait for a pre-record frame boundary; resource changes complete after existing
resource preparation. Outcomes are `NoChange`, `Applied`, `Rebuilt`, `RestartRequired`, `Rejected`
and `Failed`. A restart request changes nothing. Presets that alter startup-admitted graph
branches or their profiles require restart. Existing rebuild waits and fault handling remain
in force; application is not a general native-resource transaction or rollback system.

Only one request can be pending. A second request is rejected as busy. Cancellation applies
before processing begins; shutdown settles pending requests. Requests can be awaited in
`Game.LoadAsync`: bootstrap continues until worker initialization finishes, then the host
processes settings. Do not synchronously wait for a queued result on the device thread.
`Current` and `Requested` are immutable snapshots of common setting values; `Requested` shows
the pending request while one exists. Normalization follows RenderSettings. Settings values
describe requests; feature activation is reported separately by `GraphicsDevice.Capabilities`.

| Category | API |
| --- | --- |
| Startup device/window/validation configuration | `ConfigureRendering(RenderingOptions)` and host properties, before Run; restart to change |
| Common quality and diagnostics | `GraphicsDevice.Settings`: preset, scale, exposure, tone mapper, auto exposure, AA/AO modes, shadow enable/size, reflection/GI modes, async mode, timing, CPU snapshots and debug overlay |
| Authored scene state | `Scene.Lights` and `Scene.Environment` |
| Specialist controls | Existing advanced mutable `VulkanRenderer.Settings` |

Legacy direct writes remain supported without receipts, and remain visible in current settings
and subsequent execution diagnostics. JSON schema, defaults and migrations are unchanged.
The editor’s corresponding common controls use the controller and display pending/result status.

Capabilities distinguish hardware support, device enablement, requested and latest-frame active
state with reasons. They cover custom resources/passes, mesh shaders, ray queries, async compute,
VRS, AO, shadows, reflections, GI and opacity micromaps. No production frame means no claimed
active execution. Existing renderer admission and diagnostic policies remain authoritative;
hardware support does not imply qualified or active experimental features.

## Runnable examples and verification

```powershell
dotnet build Njulf.sln -c Development
dotnet run --project Njulf.ApiExamples -c Development -- --example model --frames 120 --validation --capture artifacts/framework-api/model-new.png
dotnet run --project Njulf.ApiExamples -c Development -- --example procedural --frames 120 --validation --native-inspect --capture artifacts/framework-api/procedural-new.png
dotnet run --project Njulf.ApiExamples -c Development -- --example custom --frames 360 --validation --capture artifacts/framework-api/custom-new.png
dotnet run --project Njulf.ApiExamples -c Development -- --example custom --frames 360 --validation --no-aa --capture artifacts/framework-api/custom-no-aa-new.png
```

The examples use automatic services and default Draw. ExampleGame adds only lighting,
fixed exposure, capture and finite-run controls. The model example loads an original
source fixture with a process-local development opt-in. The procedural example uses a
shared typed texture and disposes creation wrappers after adding the object.

Captures require fresh paths and wait for 120 full-quality frames. Runs reject validation
errors, premature exit and missing captures. Cold pipeline compilation is allowed ten minutes.
Focused tests cover framework clocks, early lifecycle exit/failure, typed resource ownership,
material transactions, upload rollback, scene invalidation and deferred retirement.
