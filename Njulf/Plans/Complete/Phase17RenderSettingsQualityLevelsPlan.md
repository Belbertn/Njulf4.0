# Phase 17 Render Settings And Quality Levels Plan

Goal: make Njulf's renderer configurable for different hardware, user preferences, and debugging workflows without scattering feature toggles across the sample app and render passes. Phase 17 should turn the existing `RenderSettings` object into a production settings system with quality presets, runtime-safe application, resolution scaling, config persistence, diagnostics, and tests.

The renderer already has many feature settings. Phase 17 is not about adding every knob again; it is about making those knobs coherent, serializable, preset-driven, and safe to change while the application is running.

## Current Baseline

The current implementation already has a useful foundation:

1. `Njulf.Rendering/Data/RenderSettings.cs`
   - Contains `RenderSettings`.
   - Contains nested settings for:
     - shadows
     - bloom
     - environment
     - reflections
     - ambient occlusion
     - anti-aliasing
     - fog
   - Contains clamping logic for many values.
2. `Njulf.Rendering/VulkanRenderer.cs`
   - Owns `public RenderSettings Settings { get; } = new();`.
   - Passes the settings object to render passes and resource managers.
   - Has renderer-level toggles outside `RenderSettings`:
     - `EnableHiZOcclusion`
     - `EnableAdaptiveHiZOcclusion`
     - `EnableDepthPrePass`
     - `EnableTransparentPass`
     - `EnableMeshletDebugView`
   - Recreates render targets on swapchain changes.
   - Calls `RenderTargetManager.Recreate(_swapchain.Extent, Settings.AmbientOcclusion.ResolutionScale)`.
   - Uses `DirectionalShadowResources.Ensure`, `SpotShadowAtlas.Ensure`, and point shadow resources to react to some settings changes.
3. `Njulf.Rendering/Resources/RenderTargetManager.cs`
   - Owns HDR scene color, fogged scene color, AO targets, LDR target, SMAA targets, motion vectors, TAA history, and bloom mip chains.
   - Supports AO resolution scale.
   - Does not yet support whole-frame render resolution scale.
4. `Njulf.Rendering/Data/RendererDiagnostics.cs`
   - Already reports active feature state and many settings.
   - Does not report a preset name, settings revision, config source, or resolution scale.
5. `NjulfHelloGame/SampleInputController.cs`
   - Mutates renderer settings directly from key presses.
   - Has many debug and feature toggles.
   - Does not have preset switching, save/load, reset, or a clean settings apply API.
6. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
   - Prints most active feature diagnostics.
   - Does not summarize quality level or config state.
7. `Njulf.Tests/RendererDiagnosticsTests.cs`
   - Already tests settings clamping, render target sizing, anti-aliasing modes, and diagnostics.

Important current risks:

1. Some settings affect GPU resource sizes. Changing them while a frame is recording is unsafe unless the renderer applies them at a controlled point.
2. Some renderer toggles live outside `RenderSettings`, so presets cannot fully describe renderer quality today.
3. Directly mutating nested settings makes it hard to know what changed.
4. A config file can accidentally contain unsupported values unless load goes through the existing clamping and validation path.
5. Resolution scale touches many render targets and pass assumptions, not only the swapchain composite.

## Non-Goals

Do not turn Phase 17 into a complete graphics options UI or adaptive performance system.

1. Do not build a full in-game settings menu unless a UI framework already exists.
2. Do not implement dynamic resolution in this phase. Add the data model and diagnostics hooks, but leave automatic scaling for a later measured performance phase.
3. Do not add GPU timing query infrastructure here if it is not already implemented. Phase 18/19 can deepen timing coverage.
4. Do not change visual defaults unexpectedly. Current default quality should map to `High` or `Ultra`, not regress to a low preset.
5. Do not remove existing debug hotkeys until preset and config controls replace them cleanly.
6. Do not add per-platform hardware detection rules in the first slice. Use explicit presets and documented defaults.
7. Do not make debug views persist by default unless the config explicitly asks for debug persistence.
8. Do not recreate pipelines for normal quality changes unless a setting actually changes formats or shader variants.

## Target Outcome

After Phase 17:

1. `RenderSettings` is the central source of renderer feature configuration.
2. Quality presets exist:
   - `Low`
   - `Medium`
   - `High`
   - `Ultra`
   - optional `Custom`
3. Expensive features can be disabled independently:
   - directional shadows
   - local shadows
   - bloom
   - ambient occlusion
   - fog
   - reflections
   - anti-aliasing
   - environment lighting
   - Hi-Z occlusion
4. Resolution scale exists as a renderer setting.
5. Runtime changes are applied at safe frame boundaries.
6. Settings that require resource recreation are detected and applied intentionally.
7. Settings can be saved and loaded from a simple JSON file.
8. Invalid config values are clamped or rejected with clear diagnostics.
9. Render diagnostics show:
   - active quality preset
   - settings revision
   - render resolution
   - output resolution
   - resolution scale
   - enabled feature mask
   - config file path/source
10. `NjulfHelloGame` can cycle presets, save current settings, load settings, and reset to defaults.
11. Existing sample scenes, including `SampleReflectionTestSpheres`, remain useful for side-by-side quality validation.

## Production Strategy

Implement this in staged slices so runtime safety is solved before persistence or extra controls.

Mandatory slices:

1. Settings model cleanup and preset definitions.
2. Settings snapshot/diff/apply pipeline.
3. Runtime-safe resource recreation.
4. Resolution scale.
5. Config serialization.
6. Sample app controls.
7. Diagnostics and tests.

Recommended order:

1. Add preset and snapshot types without changing rendering behavior.
2. Move renderer-level toggles into settings or mirror them through a compatibility layer.
3. Add settings revision and diffing.
4. Add safe application at `BeginFrame`.
5. Add resource recreation for settings that change target sizes.
6. Add JSON load/save.
7. Add hotkeys and diagnostics.

## Slice 0: Baseline Measurement

Before implementation, record current quality and performance.

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run `NjulfHelloGame`.
4. Capture baseline diagnostics for:
   - default Sponza/sample scene
   - `SampleReflectionTestSpheres`
5. Record:
   - swapchain extent
   - HDR scene color extent
   - AO extent
   - bloom base extent
   - AA target extent
   - shadow map sizes
   - reflection probe resolution
   - feature enabled states
   - GPU/CPU pass timings already available in diagnostics
6. Capture screenshots at the current default quality.

Acceptance criteria:

1. Existing tests pass before any Phase 17 changes.
2. Baseline numbers are available for comparing preset behavior.
3. The current visual default is documented as the target for `High` or `Ultra`.

## Slice 1: Add Quality Preset Model

Add explicit preset types and make preset application deterministic.

Recommended types:

```csharp
public enum RenderQualityPreset
{
    Low,
    Medium,
    High,
    Ultra,
    Custom
}
```

```csharp
public sealed record RenderSettingsPresetDefinition(
    RenderQualityPreset Preset,
    string Name,
    string Description);
```

Add APIs:

```csharp
public sealed class RenderSettings
{
    public RenderQualityPreset ActivePreset { get; private set; }
    public ulong Revision { get; private set; }

    public void ApplyPreset(RenderQualityPreset preset);
    public RenderSettingsSnapshot CreateSnapshot();
    public void ApplySnapshot(RenderSettingsSnapshot snapshot);
}
```

Implementation notes:

1. `Custom` should mean settings no longer match one of the named presets.
2. `ApplyPreset` must go through the same property setters as manual edits so clamps stay active.
3. Manual property changes should eventually mark the preset as `Custom`. Because nested setting classes currently have simple setters, add this in a controlled way:
   - first slice may update `ActivePreset` only through explicit preset APIs
   - later slice can add owner callbacks to nested settings
4. Keep defaults explicit. Do not rely on constructor defaults as the only preset definition.
5. Add comments explaining why each preset uses each value.

Suggested preset philosophy:

1. `Low`
   - target old/integrated GPUs and CPU-limited laptops
   - shadows limited or disabled
   - no local shadows
   - bloom cheap or disabled
   - AO disabled or quarter-res with few samples
   - fog optional but cheap
   - reflections global environment only
   - FXAA or no AA
   - resolution scale around `0.67` or `0.75`
2. `Medium`
   - target mainstream hardware
   - directional shadows enabled with fewer cascades
   - limited spot shadows, no point shadows by default
   - bloom enabled
   - AO half-res
   - reflections static probes with low per-pixel count
   - SMAA low/medium
   - resolution scale `0.85` or `1.0`
3. `High`
   - current production default
   - directional shadows, local spot shadows, bloom, AO, fog, IBL, static probes
   - SMAA medium
   - resolution scale `1.0`
4. `Ultra`
   - highest stable quality without experimental features
   - larger shadow maps and probe resolution
   - more cascades and local shadow budget
   - AO full-res or high sample count
   - SMAA high or TAA only if TAA is stable
   - resolution scale `1.0`, optional supersampling only if render scale above one is supported

Files likely touched:

1. `Njulf.Rendering/Data/RenderSettings.cs`
2. new `Njulf.Rendering/Data/RenderQualityPreset.cs` if preferred
3. `Njulf.Tests/RenderSettingsPresetTests.cs`

Acceptance criteria:

1. Preset definitions are explicit and tested.
2. Applying a preset clamps values through normal property setters.
3. Current default maps to a named preset.
4. Presets do not enable incomplete experimental features by default.

## Slice 2: Centralize Renderer Feature Toggles

Move renderer-level quality toggles into `RenderSettings`, while keeping compatibility properties for existing callers.

Current external toggles:

1. `VulkanRenderer.EnableHiZOcclusion`
2. `VulkanRenderer.EnableAdaptiveHiZOcclusion`
3. `VulkanRenderer.EnableDepthPrePass`
4. `VulkanRenderer.EnableTransparentPass`
5. `VulkanRenderer.EnableMeshletDebugView`

Recommended settings additions:

```csharp
public sealed class RendererFeatureSettings
{
    public bool DepthPrePassEnabled { get; set; } = true;
    public bool HiZOcclusionEnabled { get; set; } = true;
    public bool AdaptiveHiZOcclusionEnabled { get; set; } = true;
    public bool TransparentPassEnabled { get; set; } = true;
    public bool MeshletDebugViewEnabled { get; set; }
}
```

Add to `RenderSettings`:

```csharp
public RendererFeatureSettings Features { get; } = new();
```

Compatibility plan:

1. Keep existing `VulkanRenderer.Enable*` properties for now.
2. Implement them as wrappers around `Settings.Features`.
3. Mark them with comments as compatibility shims.
4. Update internal renderer reads to use `Settings.Features`.

Acceptance criteria:

1. All production feature toggles are reachable from `RenderSettings`.
2. Existing sample input code keeps working during migration.
3. Diagnostics report the settings-driven values.

## Slice 3: Add Settings Snapshot And Diff

Runtime safety requires immutable snapshots.

Recommended snapshot:

```csharp
public sealed record RenderSettingsSnapshot
{
    public RenderQualityPreset ActivePreset { get; init; }
    public ulong Revision { get; init; }
    public float ResolutionScale { get; init; }
    public RendererFeatureSettingsSnapshot Features { get; init; }
    public ShadowSettingsSnapshot Shadows { get; init; }
    public BloomSettingsSnapshot Bloom { get; init; }
    public EnvironmentSettingsSnapshot Environment { get; init; }
    public ReflectionSettingsSnapshot Reflections { get; init; }
    public AmbientOcclusionSettingsSnapshot AmbientOcclusion { get; init; }
    public AntiAliasingSettingsSnapshot AntiAliasing { get; init; }
    public FogSettingsSnapshot Fog { get; init; }
    public float Exposure { get; init; }
    public ToneMapper ToneMapper { get; init; }
    public bool ShowRawHdrSceneColor { get; init; }
}
```

Recommended diff:

```csharp
[Flags]
public enum RenderSettingsChangeFlags
{
    None = 0,
    PassEnablement = 1 << 0,
    RenderResolution = 1 << 1,
    AmbientOcclusionTargets = 1 << 2,
    ShadowResources = 1 << 3,
    EnvironmentResources = 1 << 4,
    ReflectionResources = 1 << 5,
    AntiAliasingTargets = 1 << 6,
    TaaHistoryInvalidation = 1 << 7,
    BloomTargets = 1 << 8,
    ShaderConstantsOnly = 1 << 9,
    DiagnosticsOnly = 1 << 10
}
```

Tasks:

1. Add `RenderSettingsSnapshot`.
2. Add `RenderSettingsDiff`.
3. Implement `RenderSettingsSnapshot Capture()`.
4. Implement `RenderSettingsDiff Compare(RenderSettingsSnapshot old, RenderSettingsSnapshot next)`.
5. Add tests for each diff category.
6. Keep snapshot types simple and serializable.

Change classification examples:

1. `Exposure`, `ToneMapper`, bloom intensity, fog density:
   - `ShaderConstantsOnly`
2. Bloom enabled:
   - `PassEnablement`
3. Bloom mip count:
   - `BloomTargets`
4. AO resolution scale:
   - `AmbientOcclusionTargets`
5. Directional shadow map size or cascade count:
   - `ShadowResources`
6. Spot atlas size/tile size:
   - `ShadowResources`
7. Point shadow map size:
   - `ShadowResources`
8. Environment cubemap/irradiance/prefilter/BRDF sizes:
   - `EnvironmentResources`
9. Reflection probe resolution/max probes:
   - `ReflectionResources`
10. AA mode from FXAA to SMAA/TAA:
   - `PassEnablement`
   - `AntiAliasingTargets` only if target sizes or history requirements change
11. Resolution scale:
   - `RenderResolution`
   - `BloomTargets`
   - `AmbientOcclusionTargets`
   - `AntiAliasingTargets`
   - `TaaHistoryInvalidation`

Acceptance criteria:

1. The renderer can know which setting categories changed.
2. Tests cover representative settings from each category.
3. A no-op settings apply produces `None`.

## Slice 4: Add Runtime-Safe Apply Boundary

Renderer settings should not mutate GPU resources in the middle of command recording.

Recommended renderer API:

```csharp
public RenderSettings Settings { get; }
public RenderSettingsSnapshot ActiveSettingsSnapshot { get; }
public RenderSettingsChangeFlags PendingSettingsChanges { get; }

public void RequestSettingsApply();
public void ApplySettings(RenderSettingsSnapshot snapshot);
```

Recommended behavior:

1. User code may mutate `Settings` freely between frames.
2. At `BeginFrame`, before command buffer recording:
   - capture current `Settings`
   - compare to the active snapshot
   - apply resource changes if needed
   - update diagnostics
3. If settings are changed while `_frameInProgress` is true:
   - do not recreate resources immediately
   - mark them pending
   - apply at the next `BeginFrame`
4. If a setting requires device idle or all in-flight frames idle, explicitly wait at the boundary.
5. Avoid waiting for shader-constant-only changes.

Implementation notes:

1. Add private method in `VulkanRenderer`:

```csharp
private void ApplyPendingRenderSettings()
```

2. Call it in `BeginFrame` after waiting for the current frame fence and before scene build.
3. Reuse existing resource `Ensure` methods where possible:
   - `DirectionalShadowResources.Ensure(Settings.Shadows)`
   - `SpotShadowAtlas.Ensure(Settings.Shadows)`
   - point shadow cubemap array equivalent
4. Re-register bindless descriptors after resource recreation.
5. Notify `RenderGraph.OnSwapchainRecreated()` or add a narrower `OnRenderTargetsRecreated()` if only render targets change.
6. Reset TAA history when:
   - resolution scale changes
   - AA mode changes to/from TAA
   - camera jitter is toggled
   - render target size changes
7. Make settings application idempotent.

Acceptance criteria:

1. Changing any setting during normal update does not throw.
2. Resource-recreating settings apply at frame boundaries.
3. No command buffer records against destroyed resources.
4. Descriptor registrations update after every recreated resource.
5. No-op settings changes avoid resource work.

## Slice 5: Add Resolution Scale

Resolution scale is the highest-risk Phase 17 feature because it affects render target sizes, projection jitter, UV mapping, post-processing, and diagnostics.

Recommended setting:

```csharp
public sealed class RenderResolutionSettings
{
    public float Scale { get; set; } = 1.0f;
    public float MinimumScale { get; set; } = 0.5f;
    public float MaximumScale { get; set; } = 1.0f;
    public bool DynamicResolutionEnabled { get; set; } = false;
    public float DynamicTargetFrameMilliseconds { get; set; } = 16.67f;
}
```

Add to `RenderSettings`:

```csharp
public RenderResolutionSettings Resolution { get; } = new();
```

Initial static scale support:

1. Clamp `Scale` to `[0.5, 1.0]` in the first production slice.
2. Do not support supersampling until all post effects handle arbitrary scale cleanly.
3. Calculate internal render extent from swapchain extent:
   - `renderWidth = ceil(swapchainWidth * scale)`
   - `renderHeight = ceil(swapchainHeight * scale)`
   - never below `1x1`
4. Use render extent for:
   - HDR scene color
   - fogged scene color
   - LDR scene color
   - SMAA targets
   - motion vectors
   - TAA history
   - bloom base/mips
   - AO base calculation
5. Keep swapchain extent for final present.
6. Composite or AA pass must upscale from internal LDR/HDR output to swapchain.
7. Update screen dimension push constants:
   - geometry/forward passes should use internal render dimensions for `gl_FragCoord`
   - projection aspect should still match the output aspect
8. Tiled light culling must use internal render dimensions because it indexes screen tiles and depth.
9. Hi-Z extent should derive from internal render extent.
10. Diagnostics should report both output and internal extents.

Render target changes:

1. Change `RenderTargetManager` constructor and `Recreate` to accept internal render extent.
2. Add helper:

```csharp
public static Extent2D CalculateRenderExtent(Extent2D outputExtent, float resolutionScale)
```

3. Use internal extent for render targets.
4. Keep swapchain extent in `SwapchainManager`.
5. Review passes that assume target size equals swapchain size:
   - `TiledLightCullingPass`
   - `ForwardPlusPass`
   - `SkyboxPass`
   - `TransparentForwardPass`
   - `FogPass`
   - `BloomPass`
   - `ToneMapCompositePass`
   - `AntiAliasingPass`

Acceptance criteria:

1. Resolution scale `1.0` matches baseline.
2. Resolution scale `0.75` renders the full image without cropped or stretched post effects.
3. Debug views still fill the swapchain.
4. Tiled light culling uses correct tile counts.
5. AO, bloom, SMAA/FXAA, fog, and final composite use correct texture sizes.
6. TAA history resets after scale changes.
7. Diagnostics report output resolution and internal render resolution.

## Slice 6: Preset Values

Define concrete values after baseline measurements. Use these as initial targets and adjust with measured results.

Low:

1. Resolution scale: `0.67` or `0.75`
2. Depth prepass: enabled
3. Hi-Z occlusion: enabled
4. Adaptive Hi-Z: enabled
5. Directional shadows: enabled only if affordable
6. Directional shadow map: `1024`
7. Directional cascades: `1`
8. Max shadow distance: `40`
9. Spot shadows: disabled
10. Point shadows: disabled
11. Bloom: disabled or low intensity with `3` mips
12. AO: disabled
13. Fog: enabled if cheap, otherwise disabled
14. Reflections: global environment only
15. Environment: enabled with smaller maps
16. AA: FXAA

Medium:

1. Resolution scale: `0.85` or `1.0`
2. Directional shadows: enabled
3. Directional shadow map: `1536` or clamped power-of-two `2048`
4. Directional cascades: `2`
5. Max shadow distance: `60`
6. Spot shadows: enabled with small budget
7. Point shadows: disabled
8. Bloom: enabled, `5` mips
9. AO: enabled, half-res, `8` or `16` samples
10. Fog: enabled
11. Reflections: static probes, `1` probe per pixel, `128` resolution
12. Environment: enabled, moderate map sizes
13. AA: SMAA low or medium

High:

1. Resolution scale: `1.0`
2. Directional shadows: enabled
3. Directional shadow map: `2048`
4. Directional cascades: `3`
5. Max shadow distance: `80`
6. Spot shadows: enabled, budget `8`
7. Point shadows: enabled, budget `1`
8. Bloom: enabled, `6` mips
9. AO: enabled, half-res, `16` samples
10. Fog: enabled
11. Reflections: static probes, `2` probes per pixel, `256` resolution
12. Environment: current defaults
13. AA: SMAA medium

Ultra:

1. Resolution scale: `1.0`
2. Directional shadow map: `4096`
3. Directional cascades: `4`
4. Max shadow distance: `120`
5. Spot shadows: enabled, higher budget if atlas capacity allows
6. Point shadows: enabled, budget `2`
7. Bloom: enabled, `8` mips
8. AO: full-res or half-res with `32` samples depending measured cost
9. Fog: enabled
10. Reflections: static probes, `4` probes per pixel, `512` resolution
11. Environment: larger prefiltered map only if memory cost is acceptable
12. AA: SMAA high, or TAA only if current TAA is validated for game camera motion

Acceptance criteria:

1. Every preset has a documented performance intent.
2. Presets are deterministic and covered by tests.
3. Presets do not exceed current hard clamps.
4. Presets avoid enabling incomplete rendering paths.

## Slice 7: Config File Serialization

Add simple JSON load/save using `System.Text.Json`.

Recommended file:

1. Default sample path: `Config/RenderSettings.json`
2. User override path for the sample app: `NjulfHelloGame/RenderSettings.local.json` or `%LOCALAPPDATA%/Njulf/RenderSettings.json`
3. Keep user-local config out of source control if generated.

Recommended DTO:

```csharp
public sealed class RenderSettingsConfig
{
    public int Version { get; set; } = 1;
    public RenderQualityPreset Preset { get; set; } = RenderQualityPreset.High;
    public RenderSettingsSnapshot Settings { get; set; }
}
```

Recommended service:

```csharp
public static class RenderSettingsSerializer
{
    public static RenderSettingsLoadResult TryLoad(string path);
    public static void Save(string path, RenderSettingsSnapshot snapshot);
}
```

Load behavior:

1. If file is missing:
   - return default preset
   - do not throw
2. If JSON is invalid:
   - return a failure result with path and exception message
   - do not crash the renderer unless the caller explicitly chooses strict mode
3. If version is unknown:
   - reject with clear message
4. If fields are missing:
   - use preset/default values
5. If values are out of range:
   - apply through normal settings setters so clamps handle them
   - report clamped values in diagnostics if practical
6. Paths such as environment maps should be:
   - absolute or relative to config directory
   - normalized on load
7. Debug views should not persist by default unless `PersistDebugViews` is true.

Save behavior:

1. Save a readable indented JSON file.
2. Include version.
3. Include active preset and snapshot.
4. Optionally omit default values later, but first implementation should favor explicitness.

Files likely touched:

1. new `Njulf.Rendering/Data/RenderSettingsSerializer.cs`
2. new `Njulf.Rendering/Data/RenderSettingsConfig.cs`
3. `Njulf.Tests/RenderSettingsSerializationTests.cs`
4. `NjulfHelloGame/Program.cs`

Acceptance criteria:

1. Missing config loads default settings.
2. Valid config round-trips.
3. Invalid JSON produces a clear failure result.
4. Out-of-range values are clamped.
5. Unknown version is rejected.

## Slice 8: Sample App Controls

Add practical controls to `NjulfHelloGame` while preserving existing debug hotkeys.

Recommended controls:

1. Cycle quality preset forward.
2. Cycle quality preset backward.
3. Reset to default preset.
4. Save current settings.
5. Reload settings from disk.
6. Increase/decrease resolution scale.
7. Print current settings summary.

Implementation notes:

1. Keep direct feature toggle hotkeys because they are useful for debugging.
2. When a direct toggle is used, mark active preset as `Custom`.
3. Print concise console lines:
   - active preset
   - resolution scale
   - enabled feature list
   - settings revision
4. Avoid adding visible in-app text unless a proper debug overlay is implemented in Phase 18.
5. Make save path explicit in the console output.

Files likely touched:

1. `NjulfHelloGame/SampleInputController.cs`
2. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
3. `NjulfHelloGame/Program.cs`

Acceptance criteria:

1. Presets can be changed while the sample is running.
2. Runtime changes are applied on a safe frame boundary.
3. Saved settings reload and reproduce the same diagnostics.
4. Direct toggles switch the settings to `Custom`.

## Slice 9: Diagnostics

Extend diagnostics so screenshots and logs identify the active quality state.

Add to `RendererDiagnostics`:

1. `RenderQualityPreset ActiveQualityPreset`
2. `int SettingsIsCustom`
3. `ulong SettingsRevision`
4. `string SettingsSource`
5. `int SettingsPendingApply`
6. `uint OutputWidth`
7. `uint OutputHeight`
8. `uint RenderWidth`
9. `uint RenderHeight`
10. `float RenderResolutionScale`
11. `int DynamicResolutionEnabled`
12. `float DynamicResolutionTargetFrameMilliseconds`
13. `uint EnabledFeatureMask`

Recommended feature mask:

```csharp
[Flags]
public enum RendererEnabledFeatureMask : uint
{
    None = 0,
    DepthPrePass = 1u << 0,
    HiZ = 1u << 1,
    DirectionalShadows = 1u << 2,
    SpotShadows = 1u << 3,
    PointShadows = 1u << 4,
    Bloom = 1u << 5,
    AmbientOcclusion = 1u << 6,
    Fog = 1u << 7,
    Environment = 1u << 8,
    Reflections = 1u << 9,
    AntiAliasing = 1u << 10,
    TransparentPass = 1u << 11,
    TaaJitter = 1u << 12
}
```

Update console reporting:

1. Add a first summary line:
   - preset
   - custom/default
   - revision
   - render resolution/output resolution
   - scale
   - source path
2. Keep existing detailed per-feature lines.

Acceptance criteria:

1. Logs clearly show which quality level produced a capture.
2. Diagnostics distinguish requested settings from effective settings when a feature is disabled because prerequisites are missing.
3. The feature mask agrees with individual diagnostics fields.

## Slice 10: Resource Recreation Matrix

Document and implement exact resource behavior for each setting category.

Shader-constant-only changes:

1. Exposure
2. Tone mapper
3. Bloom intensity/threshold/knee/radius
4. Fog density/color/distance/height values
5. AO radius/intensity/bias/power/sample count if target size unchanged
6. Shadow bias and PCF radius
7. Reflection intensity/global fallback/max probes per pixel if buffers do not resize
8. Environment intensities and rotation

Pass enablement changes:

1. Bloom enabled
2. Fog enabled
3. AO enabled
4. Reflections enabled
5. Shadows enabled
6. Anti-aliasing mode
7. Transparent pass enabled
8. Depth prepass or Hi-Z enabled

Resource recreation changes:

1. Resolution scale:
   - render targets
   - Hi-Z pyramid
   - TAA history
   - bindless texture registrations
2. AO resolution scale:
   - AO raw/blur/scratch targets
3. Directional shadow size/cascade count:
   - directional shadow image/views
   - bindless registrations
4. Spot atlas size/tile size:
   - spot shadow atlas image/view
   - bindless registrations
5. Point shadow size:
   - point shadow cubemap array
   - bindless registrations
6. Bloom mip count if physical target count is reduced later:
   - bloom mips/scratch mips
7. Environment map sizes/source:
   - environment resources
   - irradiance/prefilter/BRDF resources
8. Reflection probe capacity/resolution:
   - reflection probe cubemap array/resources
9. AA target size:
   - LDR, SMAA, motion vector, TAA history targets

Acceptance criteria:

1. Every setting has a known application category.
2. Tests cover diff classification.
3. Runtime recreation path updates descriptors and render graph state.

## Slice 11: Dynamic Resolution Preparation

Do not implement automatic dynamic resolution yet, but structure settings so it can be added cleanly.

Tasks:

1. Add disabled-by-default dynamic resolution fields:
   - `DynamicResolutionEnabled`
   - `TargetFrameMilliseconds`
   - `MinimumScale`
   - `MaximumScale`
   - `StepSize`
   - `StabilizationFrameCount`
2. Diagnostics should report dynamic resolution disabled.
3. Runtime should reject or ignore dynamic updates while disabled.
4. Add comments that automatic scale adjustment belongs to Phase 19 performance budgeting unless explicitly pulled forward.

Acceptance criteria:

1. Config files can contain dynamic resolution fields.
2. Dynamic resolution disabled path has no runtime behavior change.
3. The future implementation has a clear place to connect measured frame time.

## Slice 12: Tests

Add focused tests before broad runtime testing.

Preset tests:

1. `RenderSettings_ApplyPresetLow_UsesExpectedFeatureBudget`
2. `RenderSettings_ApplyPresetMedium_UsesExpectedFeatureBudget`
3. `RenderSettings_ApplyPresetHigh_MatchesCurrentDefaultIntent`
4. `RenderSettings_ApplyPresetUltra_StaysWithinClamps`
5. `RenderSettings_ManualEditMarksCustom`

Snapshot/diff tests:

1. No changes produce `RenderSettingsChangeFlags.None`.
2. Exposure change produces `ShaderConstantsOnly`.
3. AO resolution scale change produces `AmbientOcclusionTargets`.
4. Shadow map size change produces `ShadowResources`.
5. Resolution scale change produces `RenderResolution`.
6. AA mode change to TAA invalidates history.
7. Environment map size change produces `EnvironmentResources`.
8. Reflection probe resolution change produces `ReflectionResources`.

Serialization tests:

1. Missing file returns default result.
2. Valid config round-trips.
3. Invalid JSON reports failure.
4. Unknown version reports failure.
5. Out-of-range values clamp.
6. Debug views are not persisted unless explicitly requested.

Resource math tests:

1. `RenderTargetManager.CalculateRenderExtent` clamps minimum size.
2. `CalculateRenderExtent` rounds up odd sizes.
3. AO extents derive from internal render extent.
4. Bloom extents derive from internal render extent.
5. Resolution scale `1.0` preserves existing extents.

Diagnostics tests:

1. `RendererDiagnostics.Empty` includes default preset fields.
2. Enabled feature mask matches individual enabled fields.
3. Resolution diagnostics report output and render extents.

Manual validation:

1. Start sample at each preset.
2. Switch presets repeatedly while rendering.
3. Resize the window after switching presets.
4. Toggle all feature hotkeys after applying presets.
5. Save settings, restart, reload, verify diagnostics.
6. Capture RenderDoc at Low and Ultra.
7. Verify validation-clean startup, runtime switching, resize, and shutdown.

Acceptance criteria:

1. `dotnet build Njulf.sln` passes.
2. `dotnet test Njulf.sln` passes.
3. Runtime preset switching is validation-clean.

## Slice 13: SampleReflectionTestSpheres Validation

Use `SampleReflectionTestSpheres` as a visual comparison target because it clearly shows reflections, roughness, bloom response after Phase 16, and post-processing differences.

Tasks:

1. Keep the existing reflection test spheres.
2. If Phase 16 material-quality spheres exist, include them in the quality validation camera path.
3. Add a repeatable sample camera position or startup view where all spheres are visible.
4. Capture screenshots for:
   - Low
   - Medium
   - High
   - Ultra
5. Validate:
   - Low still renders readable shapes and stable reflections/environment fallback.
   - Medium keeps the scene visually acceptable.
   - High matches the current intended look.
   - Ultra improves shadow/reflection/AO detail without changing art direction.
6. Verify resolution scale changes do not stretch the spheres or shift their positions.
7. Verify AA modes do not break sphere silhouettes.

Acceptance criteria:

1. The sphere scene remains stable across all presets.
2. Resolution scaling and AA differences are visible but geometrically correct.
3. Preset screenshots can be used for future regressions.

## Slice 14: Documentation

Update project docs after implementation.

Tasks:

1. Add a render settings support table:
   - setting
   - default
   - min/max
   - preset values
   - runtime-safe category
   - resource recreation required
2. Document config file location and schema version.
3. Document preset intent.
4. Document which settings are debug-only.
5. Document dynamic resolution as planned but disabled.
6. Add troubleshooting notes:
   - invalid JSON
   - unsupported enum value
   - config path not found
   - settings applied next frame

Acceptance criteria:

1. A contributor can add a new render setting without guessing where presets, serialization, diagnostics, and diffing must be updated.
2. A user can edit the config file and know what values are valid.

## Detailed File Impact

Likely primary files:

1. `Njulf.Rendering/Data/RenderSettings.cs`
   - add presets
   - add resolution settings
   - add central feature settings
   - add snapshot creation/apply APIs
2. new `Njulf.Rendering/Data/RenderSettingsSnapshot.cs`
3. new `Njulf.Rendering/Data/RenderSettingsDiff.cs`
4. new `Njulf.Rendering/Data/RenderSettingsSerializer.cs`
5. new `Njulf.Rendering/Data/RendererEnabledFeatureMask.cs`
6. `Njulf.Rendering/VulkanRenderer.cs`
   - apply settings at frame boundary
   - centralize feature toggles
   - recreate resources on diff
   - update diagnostics
7. `Njulf.Rendering/Resources/RenderTargetManager.cs`
   - add internal render extent support
   - add resolution scale helpers
8. `Njulf.Rendering/Resources/HiZDepthPyramid.cs`
   - derive from internal render extent
9. `Njulf.Rendering/Resources/DirectionalShadowResources.cs`
   - verify runtime `Ensure` path is complete
10. `Njulf.Rendering/Resources/SpotShadowAtlas.cs`
   - verify runtime `Ensure` path is complete
11. `Njulf.Rendering/Resources/PointShadowCubemapArray.cs`
   - verify runtime `Ensure` path is complete
12. `Njulf.Rendering/Resources/EnvironmentManager.cs`
   - add settings diff handling for source and size changes
13. `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
   - add settings diff handling for capacity/resolution changes
14. `Njulf.Rendering/Pipeline/*`
   - audit pass size assumptions
15. `Njulf.Rendering/Data/RendererDiagnostics.cs`
   - add preset/config/resolution diagnostics
16. `NjulfHelloGame/SampleInputController.cs`
   - add preset/config controls
17. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
   - print settings summary
18. `NjulfHelloGame/Program.cs`
   - load config on startup
19. `NjulfHelloGame/SampleReflectionTestSpheres.cs`
   - no required code change for Phase 17, but use it for validation; update only if a preset comparison layout is needed

Likely tests:

1. new `Njulf.Tests/RenderSettingsPresetTests.cs`
2. new `Njulf.Tests/RenderSettingsSerializationTests.cs`
3. update `Njulf.Tests/RendererDiagnosticsTests.cs`
4. update render target extent tests in `RendererDiagnosticsTests` or a new `RenderTargetManagerTests.cs`

## Risk Register

Risk: settings changes recreate resources while command buffers still reference old resources.

Mitigation:

1. Apply changes only at `BeginFrame` after waiting for the frame fence.
2. Wait for all in-flight frames only for resources that can still be referenced by other frames.
3. Re-register descriptors immediately after recreation.
4. Keep no-op diff path cheap.

Risk: resolution scale breaks pass coordinate assumptions.

Mitigation:

1. Introduce internal render extent explicitly.
2. Audit every pass that uses swapchain extent.
3. Add tests for target size calculations.
4. Validate low scale in RenderDoc.

Risk: config files become a second source of truth.

Mitigation:

1. Load config into `RenderSettings` through normal setters.
2. Save snapshots generated from live settings.
3. Version the config.
4. Document schema.

Risk: presets silently enable incomplete features.

Mitigation:

1. Preset tests assert exact feature modes.
2. Ultra uses only stable features.
3. TAA remains opt-in unless validated as stable.

Risk: direct hotkey mutation bypasses preset tracking.

Mitigation:

1. Add mutation helpers or owner callbacks.
2. At minimum, sample input marks preset as `Custom` whenever it changes settings manually.

Risk: low preset degrades visual quality too much.

Mitigation:

1. Use `SampleReflectionTestSpheres` and the main sample scene for visual comparisons.
2. Keep IBL or a cheap environment fallback active where possible.
3. Prefer reducing resolution and sample counts before removing all lighting context.

Risk: diagnostics bloat.

Mitigation:

1. Add one concise settings summary line.
2. Keep detailed per-feature lines as they already exist.
3. Use feature mask for compact machine-readable state.

## Definition Of Done

Phase 17 is complete when:

1. `RenderSettings` owns all production feature toggles.
2. Low, Medium, High, and Ultra presets are implemented and tested.
3. Manual setting changes can produce a `Custom` state.
4. Runtime settings changes are applied safely at frame boundaries.
5. Settings diffs classify resource recreation requirements.
6. Resolution scale works at `1.0` and at least one sub-native scale such as `0.75`.
7. Config load/save works with a versioned JSON file.
8. Invalid config files fail gracefully with clear messages.
9. Diagnostics report active preset, revision, source, feature mask, output resolution, internal render resolution, and resolution scale.
10. `NjulfHelloGame` can switch presets, reset, save, reload, and adjust resolution scale.
11. `SampleReflectionTestSpheres` and the main sample scene render correctly at every preset.
12. `dotnet build Njulf.sln` passes.
13. `dotnet test Njulf.sln` passes.
14. Runtime preset switching, resize, and shutdown are Vulkan validation-clean.

## Recommended First Implementation PR

Keep the first PR focused on low-risk data-model work:

1. Add `RenderQualityPreset`.
2. Add preset definitions.
3. Add resolution and feature settings objects.
4. Add settings snapshots and diffs.
5. Add unit tests for presets and diffs.
6. Do not change render target sizing yet.

Recommended second PR:

1. Add renderer frame-boundary apply.
2. Move renderer toggles into settings.
3. Add diagnostics summary fields.
4. Add sample hotkeys for preset switching.

Recommended third PR:

1. Add static resolution scale.
2. Update render target sizing and pass extent handling.
3. Add resource recreation tests and manual validation.

Recommended fourth PR:

1. Add JSON load/save.
2. Add sample startup load and runtime save/reload.
3. Finalize docs and preset screenshots.
