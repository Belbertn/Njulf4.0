# Phase 18 Editor And Debug Tooling Hooks Plan

Target outcome: Njulf has a production-grade renderer tooling layer that lets artists and programmers inspect the scene, renderer state, material data, culling decisions, lighting volumes, reflection probes, frame timings, screenshots, and RenderDoc captures without editing shaders or adding one-off debug code.

This phase should turn the existing scattered debug toggles into a coherent toolkit:
- World-space debug draw for lines, boxes, spheres, frustums, bounds, light volumes, and probe volumes.
- Screen-space overlays for tiled lighting, shadow cascades, reflection probes, decal volumes, object bounds, selected-object material data, and timing data.
- Reliable GPU pass timings through Vulkan timestamp queries.
- Screenshot capture from the final rendered image.
- Optional RenderDoc capture triggers when RenderDoc is available.
- A clean shipping gate so debug tooling can be disabled or compiled out.

## Current Baseline

The renderer already has several strong foundations:

1. `VulkanContext` supports Vulkan debug names and command labels through `VK_EXT_debug_utils`.
2. `RenderGraph` wraps passes with CPU timing and debug labels.
3. `RendererDiagnostics` and `SceneRenderingData` already carry many CPU timing, GPU timing, culling, lighting, shadow, AO, AA, environment, and reflection fields.
4. `RenderSettings` already contains feature-specific debug view enums for shadows, bloom, environment, AO, AA, fog, and reflections.
5. `SampleInputController` already exposes runtime key controls for several debug views.
6. `SampleDiagnosticsReporter` already prints detailed console diagnostics.
7. `SampleReflectionTestSpheres.cs` provides a useful material-inspection validation scene with named high-metallic and glossy dielectric objects.

The missing part is not isolated debug features. The missing part is a durable tooling contract that editor code, sample code, and future game code can use consistently.

## Industry Standard Definition

Phase 18 is industry-standard when the renderer provides:

1. Stable public APIs for debug primitives, selection inspection, capture requests, and timing data.
2. Debug rendering that is deterministic, bounded, validation-clean, and isolated from shipping rendering.
3. Debug tools that work in normal gameplay builds when enabled, but can be compiled out or disabled in shipping builds.
4. GPU timings that do not stall the frame loop.
5. Screenshots and captures that preserve color correctness and file metadata.
6. Diagnostics that are useful from a console sample and from a future editor UI.
7. RenderDoc captures that can be triggered programmatically without hard dependencies on RenderDoc.
8. Tests for CPU-side contracts and clear manual validation steps for GPU-visible behavior.

## Non-Goals

Do not build a full editor UI in this phase.

Do not add ImGui unless the project explicitly adopts it. The API should be editor-ready, but `NjulfHelloGame` can remain console and keyboard driven.

Do not rewrite the render graph or frame graph. Add timing hooks and debug passes around the existing `RenderGraph`.

Do not add a permanent gameplay dependency on debug rendering. Keep debug draw and overlays opt-in.

Do not add screen-space reflection, decal rendering, animation, particles, or new material extensions here. Phase 18 only adds tooling hooks for systems that either already exist or are expected to exist.

## Proposed Files

New renderer files:

1. `Njulf.Rendering/Debug/DebugDrawCommand.cs`
2. `Njulf.Rendering/Debug/DebugDrawList.cs`
3. `Njulf.Rendering/Debug/DebugDrawService.cs`
4. `Njulf.Rendering/Debug/DebugOverlaySettings.cs`
5. `Njulf.Rendering/Debug/DebugOverlayMode.cs`
6. `Njulf.Rendering/Debug/SelectedObjectInspection.cs`
7. `Njulf.Rendering/Debug/MaterialInspectionResult.cs`
8. `Njulf.Rendering/Debug/FrameCaptureRequest.cs`
9. `Njulf.Rendering/Debug/ScreenshotCaptureService.cs`
10. `Njulf.Rendering/Debug/RenderDocCaptureService.cs`
11. `Njulf.Rendering/Debug/GpuTimingQueryManager.cs`
12. `Njulf.Rendering/Pipeline/DebugDrawPass.cs`
13. `Njulf.Rendering/Pipeline/DebugOverlayPass.cs`
14. `Njulf.Rendering/Pipeline/PipelineObjects/DebugDrawPipeline.cs`
15. `Njulf.Rendering/Pipeline/PipelineObjects/DebugOverlayPipeline.cs`
16. `Njulf.Shaders/debug_draw.vert`
17. `Njulf.Shaders/debug_draw.frag`
18. `Njulf.Shaders/debug_overlay.vert`
19. `Njulf.Shaders/debug_overlay.frag`

Existing files to update:

1. `Njulf.Rendering/VulkanRenderer.cs`
2. `Njulf.Rendering/Pipeline/RenderGraph.cs`
3. `Njulf.Rendering/Pipeline/RenderPassBase.cs`
4. `Njulf.Rendering/Data/RenderSettings.cs`
5. `Njulf.Rendering/Data/RendererDiagnostics.cs`
6. `Njulf.Rendering/Data/SceneRenderingData.cs`
7. `Njulf.Rendering/Data/GPUStructs.cs`
8. `Njulf.Rendering/Descriptors/BindlessIndexTable.cs`
9. `Njulf.Rendering/Resources/RenderTargetManager.cs`
10. `Njulf.Rendering/Resources/MeshManager.cs`
11. `Njulf.Rendering/Resources/MaterialManager.cs`
12. `Njulf.Core/Interfaces/IRenderer.cs`
13. `Njulf.Core/Scene/RenderObject.cs`
14. `Njulf.Core/Scene/Scene.cs`
15. `NjulfHelloGame/SampleInputController.cs`
16. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
17. `NjulfHelloGame/Program.cs`
18. `NjulfHelloGame/SampleReflectionTestSpheres.cs`
19. `Njulf.Tests/RendererDiagnosticsTests.cs`
20. `Njulf.Tests/GPUStructLayoutTests.cs`
21. `Njulf.Tests/SceneTests.cs`

If the project prefers not to create a `Debug` folder, place these under `Njulf.Rendering/Data` and `Njulf.Rendering/Resources`, but keep the namespaces explicit.

## Public API Shape

Add a renderer-facing debug interface that can be implemented by `VulkanRenderer` without forcing every renderer backend to expose Vulkan details.

```csharp
public interface IRendererDebugTools
{
    DebugDrawList DebugDraw { get; }
    DebugOverlaySettings DebugOverlays { get; }
    SelectedObjectInspection? SelectedObject { get; set; }
    RendererDiagnostics LastDiagnostics { get; }

    void RequestScreenshot(string? outputPath = null);
    void RequestRenderDocCapture();
}
```

Keep `IRenderer` small. Either:

1. Add `IRendererDebugTools? DebugTools { get; }` to `IRenderer`, or
2. Let sample/editor code pattern-match `renderer as IRendererDebugTools`.

Prefer option 2 for lower churn unless the project is ready to standardize renderer tooling in `Njulf.Core`.

## Debug Draw Contract

Debug draw should be a frame-local command buffer with optional persistent commands.

```csharp
public enum DebugDrawDepthMode
{
    DepthTested,
    AlwaysVisible,
    XRay
}

public enum DebugDrawLifetime
{
    OneFrame,
    Persistent
}

public readonly record struct DebugLine(Vector3 A, Vector3 B, Vector4 Color);

public sealed class DebugDrawList
{
    public int MaxLineSegments { get; set; }
    public bool Enabled { get; set; }

    public void Line(Vector3 a, Vector3 b, Vector4 color, DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested);
    public void Box(BoundingBox bounds, Vector4 color, DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested);
    public void OrientedBox(Matrix4x4 transform, Vector3 extents, Vector4 color, DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested);
    public void Sphere(Vector3 center, float radius, Vector4 color, int segments = 24, DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested);
    public void Frustum(Matrix4x4 viewProjection, Vector4 color, DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested);
    public void ClearFrame();
    public void ClearPersistent();
}
```

Rules:

1. The API must clamp command counts and report dropped command counts in diagnostics.
2. `DebugDrawList` must be thread-safe enough for game systems to enqueue debug primitives during update.
3. The renderer must snapshot the draw list at the start of `DrawScene`.
4. Persistent commands must be explicitly cleared, not silently kept forever without visibility in diagnostics.
5. Every generated primitive should resolve to lines. Avoid adding triangle debug geometry until there is a clear need.

## Debug Draw Rendering

Add `DebugDrawPass`.

Pass placement:

1. Render world-space depth-tested debug primitives after opaque rendering and before transparent rendering when the primitive should be occluded by scene geometry.
2. Render always-visible and x-ray debug primitives after transparent rendering but before fog, anti-aliasing, and tone mapping.
3. If splitting this is too much for the first slice, start with one pass after transparent rendering using the scene depth target for optional depth testing.

Pipeline:

1. Vertex shader reads a packed dynamic vertex buffer of line endpoints.
2. Fragment shader outputs HDR linear color into the active scene color target.
3. Use dynamic rendering, no mesh shader requirement for debug lines.
4. Use line-list topology first.
5. If line width greater than 1 is required, add camera-facing quad expansion later. Vulkan wide lines are not portable enough to depend on.
6. Use two pipelines or dynamic depth state:
   - depth test on, depth write off.
   - depth test off, depth write off.
7. Use alpha blending for x-ray lines.

Buffers:

1. Add a host-visible staging ring or per-frame debug vertex buffer.
2. Default budget: 65,536 line segments per frame.
3. Vertex format:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUDebugLineVertex
{
    public Vector3 Position;
    public float Padding0;
    public Vector4 Color;
}
```

Diagnostics:

1. `DebugDrawEnabled`
2. `DebugDrawLineCount`
3. `DebugDrawDroppedLineCount`
4. `DebugDrawPersistentLineCount`
5. `CpuDebugDrawBuildMicroseconds`
6. `CpuDebugDrawRecordMicroseconds`
7. `GpuDebugDrawMicroseconds`

## Debug Overlay Contract

Add a central overlay enum instead of relying only on feature-specific debug modes.

```csharp
public enum DebugOverlayMode : uint
{
    None = 0,
    LightTiles = 1,
    DirectionalShadowCascades = 2,
    ReflectionProbeVolumes = 3,
    DecalVolumes = 4,
    ObjectBounds = 5,
    MeshletBounds = 6,
    SelectedObject = 7,
    MaterialInspection = 8,
    PassTimings = 9,
    GpuMemory = 10
}
```

Add settings:

```csharp
public sealed class DebugOverlaySettings
{
    public bool Enabled { get; set; }
    public DebugOverlayMode Mode { get; set; }
    public bool ShowLabels { get; set; }
    public bool ShowDepthTestedVolumes { get; set; }
    public bool ShowXRayVolumes { get; set; }
    public int SelectedObjectIndex { get; set; } = -1;
    public int SelectedLightIndex { get; set; } = -1;
    public int SelectedReflectionProbeIndex { get; set; } = -1;
}
```

Wire `DebugOverlaySettings` into `RenderSettings`:

```csharp
public DebugOverlaySettings Debug { get; } = new();
```

Rules:

1. Only one full-screen overlay mode should be active at a time.
2. World volume overlays can coexist with full-screen overlays only if explicitly allowed.
3. Overlay modes must be serialized later by Phase 17 settings, but can remain runtime-only in this phase if Phase 17 has not been implemented yet.
4. Debug overlay state must be included in `RendererDiagnostics`.

## Overlay Implementations

### Light Tiles

Purpose: inspect Forward+ tile distribution and local light pressure.

Implementation:

1. Add a full-screen `DebugOverlayPass` mode that reads tiled light headers from the existing bindless storage buffer.
2. Draw a heatmap using light count per tile.
3. Add optional grid lines at tile boundaries.
4. Use color scale:
   - black: 0 lights.
   - blue/green: low count.
   - yellow/red: near max.
   - magenta: saturated or overflow.
5. Add diagnostics for `DebugLightTileMaxCount` and `DebugLightTileAverageCount` if inexpensive.

Validation:

1. Spawn the sample lights from `SampleLighting`.
2. Move camera near clustered lights.
3. Verify tile heatmap changes correctly and aligns to screen tiles.

### Directional Shadow Cascades

Purpose: inspect cascade split coverage and cascade stability.

Implementation:

1. Reuse `ShadowDebugView.CascadeOverlay` for final shader color when possible.
2. Add debug draw frustums for each cascade from `ShadowData`.
3. Show each cascade with a consistent color.
4. Include per-cascade meshlet count in diagnostics.

Validation:

1. Move the camera forward/backward and rotate.
2. Verify cascade overlays are stable and do not flicker.
3. Capture in RenderDoc and confirm shadow pass labels are preserved.

### Reflection Probe Volumes

Purpose: inspect probe placement, influence volumes, blending, and captured cubemap selection.

Implementation:

1. Use `Scene.ReflectionProbes` or `ReflectionProbeManager` probe snapshots as the source of truth.
2. Draw box probes as oriented boxes.
3. Draw sphere probes as wire spheres.
4. Draw blend distance as a second faded volume.
5. For selected probe, show cubemap face/mip using existing reflection debug texture path.
6. Integrate with existing `ReflectionDebugView` values:
   - `ProbeInfluence`
   - `ProbeIndex`
   - `ProbeBlendWeights`
   - `ProbeCubemapFace`
   - `ProbePrefilterMip`
   - `BoxProjectionDirection`

Validation:

1. Use the sample reflection probe scene.
2. Use `SampleReflectionTestSpheres.Configure` to validate material response on chrome, gold, brushed metal, and glossy dielectric spheres.
3. Toggle probe volume overlay and reflection debug views.
4. Confirm probe influence matches material reflections on the sample spheres.

### Decal Volumes

Purpose: reserve the tooling contract for Phase 11 decals even if decal rendering is not currently implemented.

Implementation:

1. Add the enum and diagnostics now.
2. Render no decal volumes when no decal system exists.
3. Expose `DebugDecalVolumeCount = 0`.
4. Document that the future decal system should enqueue oriented boxes through `DebugDrawList`.

Validation:

1. Toggle decal overlay and verify it is a no-op with a clear diagnostics count of zero.

### Object Bounds

Purpose: inspect CPU object culling, mesh bounds, and selected object identity.

Implementation:

1. Add stable render object indexing or object IDs during scene data build.
2. Store per-object name, mesh handle, material handle, world bounds, visibility, and culling status in a CPU-only debug snapshot.
3. Draw visible object bounds in one color and culled object bounds in another when CPU culling snapshots exist.
4. Draw selected object bounds in a stronger color.
5. Keep this optional because CPU snapshots can be memory-heavy.

Validation:

1. Use sample scene objects.
2. Select `ReflectionTest.Chrome`, `ReflectionTest.SmoothGold`, `ReflectionTest.BrushedMetal`, and `ReflectionTest.GlossyDielectric`.
3. Verify names and material properties are inspected correctly.

### Meshlet Bounds

Purpose: inspect meshlet distribution and LOD/culling behavior.

Implementation:

1. Use `SceneRenderingData.MeshletDrawCommands` when `HasCpuSnapshots` is true.
2. Query meshlet bounds from `MeshManager.GetMeshlet`.
3. Transform meshlet bounding spheres by the instance world matrix.
4. Draw a capped number of meshlet spheres for visible meshlets.
5. Default cap: 4,096 meshlet bounds per frame.
6. Report dropped meshlet bounds in diagnostics.

Validation:

1. Toggle existing meshlet debug view and new meshlet bounds overlay.
2. Confirm the two debug paths agree on visible meshlet distribution.

### Pass Timings

Purpose: show CPU and GPU pass timings without an external profiler.

Implementation:

1. Add a compact diagnostics overlay data model.
2. Since no in-app text renderer exists, start by printing a structured timing table from `SampleDiagnosticsReporter`.
3. Optional first screen overlay can be bars without text using `DebugOverlayPass`.
4. When an editor UI exists, it should consume `RendererDiagnostics` and `FrameTimingSnapshot`.

Minimum console output:

```text
Frame timings CPU us: depth=..., hiz=..., lightCull=..., forward=..., transparent=..., fog=..., composite=..., debug=...
Frame timings GPU us: depth=..., hiz=..., lightCull=..., forward=..., transparent=..., fog=..., composite=..., debug=...
```

Validation:

1. Timings should be non-zero after enough frames have completed.
2. Timing values should not require a synchronous GPU wait.

## Material Inspection

Add selected-object material inspection output.

Data model:

```csharp
public sealed record SelectedObjectInspection(
    int ObjectIndex,
    string ObjectName,
    MeshHandle Mesh,
    MaterialHandle Material,
    BoundingBox WorldBounds,
    bool Visible,
    bool CpuCulled,
    MaterialInspectionResult MaterialInfo);

public sealed record MaterialInspectionResult(
    int MaterialIndex,
    Vector4 Albedo,
    Vector4 Emissive,
    float Metallic,
    float Roughness,
    float AmbientOcclusion,
    float NormalStrength,
    MaterialRenderMode RenderMode,
    int AlbedoTextureIndex,
    int NormalTextureIndex,
    int MetallicRoughnessTextureIndex,
    int EmissiveTextureIndex);
```

Implementation:

1. Add optional object debug snapshots in `SceneDataBuilder`.
2. Capture:
   - object index.
   - object name.
   - mesh handle.
   - material handle.
   - world matrix.
   - world bounds.
   - visibility and culling state.
3. Add `VulkanRenderer.TryInspectObject(int index, out SelectedObjectInspection inspection)`.
4. Add `VulkanRenderer.TryFindObjectByName(string name, out int objectIndex)` for sample/editor use.
5. Decode `GPUMaterialData` into a human-readable material result.
6. Include bindless texture indices and default texture substitution flags.

Sample validation:

1. Add a keybinding to cycle selected object.
2. Add optional direct selection by known sample names:
   - `ReflectionTest.Chrome`
   - `ReflectionTest.SmoothGold`
   - `ReflectionTest.BrushedMetal`
   - `ReflectionTest.GlossyDielectric`
3. Print selected material properties through `SampleDiagnosticsReporter`.
4. Verify values match `SampleReflectionTestSpheres.CreateMaterial`:
   - chrome metallic 1.0, roughness 0.04.
   - smooth gold metallic 1.0, roughness 0.16.
   - brushed metal metallic 1.0, roughness 0.42.
   - glossy dielectric metallic 0.0, roughness 0.08.

## GPU Timing Queries

Add real Vulkan timestamp query support. Existing `RendererDiagnostics` already has GPU timing fields, but the query lifecycle must be production-safe.

New class:

```csharp
public sealed class GpuTimingQueryManager : IDisposable
{
    public bool Enabled { get; set; }
    public bool Supported { get; }
    public void BeginFrame(int frameIndex);
    public void BeginPass(CommandBuffer cmd, string passName);
    public void EndPass(CommandBuffer cmd, string passName);
    public FrameTimingSnapshot GetLastCompletedFrame(int frameIndex);
}
```

Design:

1. Create one `VkQueryPool` per frame in flight.
2. Allocate two timestamp queries per timed pass.
3. Reset query ranges before use with `vkCmdResetQueryPool`.
4. Write timestamps with `vkCmdWriteTimestamp2`:
   - begin: `TopOfPipe` or first relevant stage.
   - end: `BottomOfPipe` or last relevant stage.
5. Read query results only after the frame fence for that frame has completed.
6. Convert timestamp deltas with `PhysicalDeviceProperties.Limits.TimestampPeriod`.
7. Store results in a `FrameTimingSnapshot`.
8. If timestamps are unsupported or invalid, mark timings unavailable rather than reporting fake zeros as successful data.

Render graph integration:

1. Add optional timing hooks to `RenderGraph.Execute`.
2. Begin pass timing before barriers only if pass cost should include barriers.
3. Prefer timing barriers and pass execution together for artist-facing pass cost.
4. Keep CPU timing as it is today.
5. Add pass names to the timing snapshot dynamically instead of hardcoding only the original pass list.

Diagnostics additions:

1. `GpuTimingSupported`
2. `GpuTimingEnabled`
3. `GpuTimingFrameLatency`
4. `GpuTimingDroppedFrameCount`
5. `GpuDebugDrawMicroseconds`
6. `GpuDebugOverlayMicroseconds`
7. `GpuCompositeMicroseconds`
8. `GpuBloomExtractMicroseconds`
9. `GpuBloomDownsampleMicroseconds`
10. `GpuBloomUpsampleMicroseconds`
11. `GpuDirectionalShadowMicroseconds`
12. `GpuSpotShadowMicroseconds`
13. `GpuPointShadowMicroseconds`

Acceptance:

1. GPU pass timings become non-zero on hardware that supports timestamp queries.
2. No `vkDeviceWaitIdle` is used during normal frame timing.
3. Disabling timing removes timestamp writes from command buffers.
4. Query pools are recreated safely on device teardown, not swapchain resize.

## Screenshot Capture

Add screenshot capture from the final presented image.

API:

```csharp
public sealed record ScreenshotRequest(string OutputPath, ScreenshotColorSpace ColorSpace);

public enum ScreenshotColorSpace
{
    FinalLdrSrgb,
    HdrLinear
}
```

Initial scope:

1. Support final LDR screenshot first.
2. Capture after tone mapping and anti-aliasing, before present.
3. Copy the final color image to a host-visible staging buffer.
4. Write PNG from CPU memory.
5. Default output folder: `Screenshots`.
6. Default file name: `Njulf_yyyyMMdd_HHmmss_fff.png`.

Color and format rules:

1. Preserve the final visible image.
2. Handle swapchain formats explicitly:
   - BGRA to RGBA swizzle if needed.
   - sRGB transfer expectations documented.
3. If capturing HDR later, use EXR or float-compatible output. Do not write tonemapped PNG and call it HDR.

Synchronization:

1. Use a copy command after rendering completes for the frame.
2. Use a fence or deferred readback queue.
3. Avoid blocking the render loop except for an explicit blocking screenshot mode.
4. Track in-flight screenshot requests and complete them after their readback fence signals.

Diagnostics:

1. `ScreenshotRequested`
2. `ScreenshotPendingCount`
3. `ScreenshotCompletedCount`
4. `LastScreenshotPath`
5. `LastScreenshotError`

Validation:

1. Press a sample key and verify a PNG appears in `Screenshots`.
2. Verify dimensions match swapchain extent.
3. Verify the screenshot matches the visible frame after tone mapping.
4. Test resize before and after screenshot request.

## RenderDoc Capture Trigger

Add optional RenderDoc API integration. This must not make RenderDoc a required dependency.

Approach:

1. Dynamically load RenderDoc only if the module is already present or discoverable.
2. On Windows, try `renderdoc.dll` through `NativeLibrary.TryLoad`.
3. Resolve `RENDERDOC_GetAPI`.
4. Store function pointers for `StartFrameCapture`, `EndFrameCapture`, `TriggerCapture`, and `SetCaptureFilePathTemplate`.
5. Expose `RenderDocCaptureService.IsAvailable`.
6. If unavailable, log once and make `RequestRenderDocCapture()` a no-op with diagnostics.

API:

```csharp
public sealed class RenderDocCaptureService
{
    public bool IsAvailable { get; }
    public bool CaptureRequested { get; private set; }
    public void RequestCapture();
    public void BeginFrame(IntPtr deviceHandle, IntPtr windowHandle);
    public void EndFrame(IntPtr deviceHandle, IntPtr windowHandle);
}
```

Integration:

1. Request capture from `SampleInputController`.
2. At the next frame boundary, start and end capture around the renderer draw call.
3. Prefer `TriggerCapture` if it works reliably with the RenderDoc API version in use.
4. Record capture availability and request state in diagnostics.

Diagnostics:

1. `RenderDocAvailable`
2. `RenderDocCaptureRequested`
3. `RenderDocCaptureCompletedCount`
4. `LastRenderDocCaptureMessage`

Validation:

1. Run without RenderDoc: key press logs a clear unavailable message and does not fail.
2. Run launched from RenderDoc: key press captures one frame.
3. Verify pass labels are visible in the capture:
   - `DepthPrePass`
   - `DirectionalShadowPass`
   - `SpotShadowPass`
   - `PointShadowPass`
   - `HiZBuildPass`
   - `AmbientOcclusionPass`
   - `TiledLightCullingPass`
   - `ForwardPlusPass`
   - `TransparentForwardPass`
   - `DebugDrawPass`
   - `FogPass`
   - `ToneMapCompositePass`
   - `DebugOverlayPass`

## Shipping Gate

Add a clear debug tooling gate.

Recommended compile symbols:

1. `NJULF_RENDER_DEBUG`
2. `NJULF_RENDER_CAPTURE`

Runtime settings:

1. `RenderSettings.Debug.Enabled`
2. `RenderSettings.Debug.AllowGpuTiming`
3. `RenderSettings.Debug.AllowScreenshots`
4. `RenderSettings.Debug.AllowRenderDocCapture`
5. `RenderSettings.Debug.MaxDebugLineSegments`
6. `RenderSettings.Debug.CpuSnapshotsEnabled`

Rules:

1. Shipping builds should default all debug features off.
2. If compiled without `NJULF_RENDER_DEBUG`, debug draw APIs should become cheap no-ops.
3. If compiled without `NJULF_RENDER_CAPTURE`, screenshot and RenderDoc services should be unavailable.
4. The renderer must never allocate large debug buffers when debug tooling is disabled.
5. Diagnostics should clearly say debug tooling is disabled, unavailable, or active.

## Implementation Slices

### Slice 18.1: Contracts And Settings

Goal: establish stable CPU-side contracts with no GPU rendering changes.

Tasks:

1. Add `DebugOverlayMode`, `DebugOverlaySettings`, and debug feature gates.
2. Add `IRendererDebugTools` or renderer pattern-match support.
3. Add `DebugDrawList` with line, box, sphere, frustum APIs.
4. Add unit tests for debug draw command expansion:
   - box creates 12 line segments.
   - sphere respects segment count and clamps invalid input.
   - frustum rejects non-invertible matrices or reports no lines.
   - max line budget drops extra commands and increments dropped count.
5. Add renderer diagnostics fields with default values in `RendererDiagnostics.Empty`.
6. Add sample keybindings for overlay mode cycling, screenshot request, RenderDoc request, and selected-object cycling.

Acceptance:

1. `dotnet test Njulf.sln` passes.
2. No Vulkan behavior changes yet.
3. Debug settings can be toggled at runtime and appear in diagnostics.

### Slice 18.2: CPU Debug Snapshots And Material Inspection

Goal: make selected object and material state inspectable from sample/editor code.

Tasks:

1. Add optional CPU debug snapshot generation in `SceneDataBuilder`.
2. Include object index, object name, mesh handle, material handle, world matrix, world bounds, and visibility/culling status.
3. Add material decoding from `GPUMaterialData`.
4. Add `TryInspectObject` and `TryFindObjectByName` on `VulkanRenderer` or debug service.
5. Extend `SampleDiagnosticsReporter` with selected-object output.
6. Add named-object validation for `SampleReflectionTestSpheres`.

Acceptance:

1. Selecting each reflection test sphere prints the expected metallic and roughness values.
2. Missing or invalid selected object indices fail gracefully.
3. CPU snapshots can be disabled and then produce no inspection result.

### Slice 18.3: Debug Draw GPU Pass

Goal: render world-space debug primitives.

Tasks:

1. Add `GPUDebugLineVertex`.
2. Add per-frame debug line vertex buffers.
3. Add `DebugDrawPipeline`.
4. Add `debug_draw.vert` and `debug_draw.frag`.
5. Add `DebugDrawPass`.
6. Register the pass in `VulkanRenderer` after transparent rendering.
7. Add barriers for the active scene color and depth target.
8. Add debug names for all buffers, pipeline layouts, shader modules, and pipelines.
9. Add diagnostics for line counts, dropped counts, CPU build time, and CPU record time.

Acceptance:

1. Boxes, spheres, and frustums draw over the sample scene.
2. Depth-tested and always-visible modes work.
3. Resize remains validation-clean.
4. Debug pass is absent or no-op when disabled.

### Slice 18.4: Core Overlays

Goal: expose the most useful overlays without a full editor UI.

Tasks:

1. Implement light tile heatmap in `DebugOverlayPass`.
2. Implement object bounds overlay using `DebugDrawList`.
3. Implement reflection probe volume overlay using `DebugDrawList`.
4. Implement directional cascade frustums using `DebugDrawList`.
5. Implement no-op decal overlay diagnostics.
6. Add sample controls to cycle overlays and selected targets.

Acceptance:

1. Light tile overlay aligns to screen tiles.
2. Object bounds and selected object bounds are stable while the camera moves.
3. Reflection probe volumes align with probe influence behavior.
4. Directional cascade frustums are visible and color-coded.
5. No-op decal overlay reports zero volumes without errors.

### Slice 18.5: GPU Timestamp Queries

Goal: provide non-stalling per-pass GPU timings.

Tasks:

1. Add `GpuTimingQueryManager`.
2. Query `timestampPeriod` from physical device properties.
3. Add query pools per frame in flight.
4. Add render graph begin/end timing hooks.
5. Read results only after the relevant frame fence is signaled.
6. Map pass timing names into `SceneRenderingData` and `RendererDiagnostics`.
7. Extend `SampleDiagnosticsReporter` to print GPU timings including debug passes.
8. Add tests for timing snapshot mapping and unavailable-state behavior.

Acceptance:

1. GPU timings populate after the expected frame latency.
2. Timing disabled removes query writes.
3. No device idle wait is introduced.
4. Diagnostics distinguish unavailable, disabled, pending, and valid timings.

### Slice 18.6: Screenshot Capture

Goal: capture the final rendered image safely.

Tasks:

1. Add screenshot request queue.
2. Add final image copy support to a staging buffer.
3. Add BGRA/RGBA conversion if required.
4. Add PNG writer dependency or use an existing image writer if already present.
5. Write screenshots to `Screenshots`.
6. Add sample keybinding.
7. Add diagnostics and error reporting.

Acceptance:

1. Screenshot file appears on request.
2. Dimensions and colors match the final visible frame.
3. Capture works after swapchain resize.
4. Failed writes report the path and exception message without crashing the renderer.

### Slice 18.7: RenderDoc Integration

Goal: allow one-frame captures without hard dependency on RenderDoc.

Tasks:

1. Add dynamic RenderDoc loader.
2. Add capture request API.
3. Hook capture begin/end around a rendered frame.
4. Add sample keybinding.
5. Add diagnostics for availability and capture count.
6. Verify command labels and debug object names in a capture.

Acceptance:

1. Running without RenderDoc is a clean no-op.
2. Running under RenderDoc captures exactly one requested frame.
3. The captured frame shows useful pass labels and resource names.

### Slice 18.8: Hardening, Tests, And Documentation

Goal: make the tooling reliable enough to stay in the renderer.

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Add tests:
   - `DebugDrawListTests`
   - `DebugOverlaySettingsTests`
   - `RendererDiagnosticsTests` updates
   - `SelectedObjectInspectionTests`
   - `GpuTimingSnapshotTests`
4. Add manual validation checklist to this plan or a `Plans/Complete` note after implementation.
5. Capture RenderDoc frames for:
   - debug draw disabled.
   - object bounds overlay.
   - reflection probe overlay with `SampleReflectionTestSpheres`.
   - light tile overlay.
   - GPU timing enabled.
6. Verify validation layers are clean during startup, normal rendering, screenshot, RenderDoc request, resize, and shutdown.

Acceptance:

1. CPU tests pass.
2. Renderer remains validation-clean.
3. Debug tooling can be disabled at runtime.
4. Debug tooling can be compiled out or left as no-op in shipping configuration.

## Diagnostics Additions

Add these fields to `RendererDiagnostics` and reset them in `RendererDiagnostics.Empty`:

```csharp
int DebugToolingEnabled;
int DebugOverlayEnabled;
DebugOverlayMode DebugOverlayMode;
int CpuDebugSnapshotsEnabled;
int DebugSelectedObjectIndex;
string DebugSelectedObjectName;
int DebugDrawEnabled;
int DebugDrawLineCount;
int DebugDrawPersistentLineCount;
int DebugDrawDroppedLineCount;
long CpuDebugDrawBuildMicroseconds;
long CpuDebugDrawRecordMicroseconds;
long GpuDebugDrawMicroseconds;
long CpuDebugOverlayRecordMicroseconds;
long GpuDebugOverlayMicroseconds;
int DebugLightTileMaxCount;
float DebugLightTileAverageCount;
int DebugObjectBoundsDrawn;
int DebugMeshletBoundsDrawn;
int DebugMeshletBoundsDropped;
int DebugReflectionProbeVolumesDrawn;
int DebugDecalVolumesDrawn;
int GpuTimingSupported;
int GpuTimingEnabled;
int GpuTimingPending;
int GpuTimingValid;
int GpuTimingFrameLatency;
long GpuCompositeMicroseconds;
long GpuBloomExtractMicroseconds;
long GpuBloomDownsampleMicroseconds;
long GpuBloomUpsampleMicroseconds;
long GpuDirectionalShadowMicroseconds;
long GpuSpotShadowMicroseconds;
long GpuPointShadowMicroseconds;
int ScreenshotRequested;
int ScreenshotPendingCount;
int ScreenshotCompletedCount;
string LastScreenshotPath;
string LastScreenshotError;
int RenderDocAvailable;
int RenderDocCaptureRequested;
int RenderDocCaptureCompletedCount;
string LastRenderDocCaptureMessage;
```

Keep fields plain and serializable. Avoid storing large arrays in `RendererDiagnostics`; use a separate `FrameTimingSnapshot` for detailed pass lists.

## Scene Data Additions

Add to `SceneRenderingData`:

```csharp
public bool DebugToolingEnabled { get; set; }
public DebugOverlayMode DebugOverlayMode { get; set; }
public bool CpuDebugSnapshotsEnabled { get; set; }
public int DebugSelectedObjectIndex { get; set; } = -1;
public string DebugSelectedObjectName { get; set; } = string.Empty;
public IReadOnlyList<ObjectDebugSnapshot> ObjectDebugSnapshots { get; set; } = Array.Empty<ObjectDebugSnapshot>();
public DebugDrawFrameSnapshot DebugDrawSnapshot { get; set; } = DebugDrawFrameSnapshot.Empty;
```

Add `ObjectDebugSnapshot`:

```csharp
public sealed record ObjectDebugSnapshot(
    int ObjectIndex,
    string Name,
    MeshHandle Mesh,
    MaterialHandle Material,
    Matrix4x4 WorldMatrix,
    BoundingBox WorldBounds,
    bool Visible,
    bool CpuCulled);
```

Do not store this snapshot unless debug snapshots are enabled.

## Sample App Controls

Suggested bindings in `SampleInputController`:

1. `F9`: cycle debug overlay mode.
2. `Shift+F9`: toggle overlay labels if labels exist later.
3. `F10`: keep existing meshlet debug toggle or migrate to overlay mode.
4. `F11`: request screenshot.
5. `F12`: request RenderDoc capture.
6. `[` and `]`: previous/next selected object.
7. `O`: print selected object inspection.
8. `P`: toggle reflection probe volume overlay.

Avoid overloading already-used bindings if these conflict with current sample controls. Keep console output concise and include the active mode after each toggle.

## Validation Scenes

Use these scenes during implementation:

1. Default sample scene:
   - validates camera movement, light tiles, object bounds, and culling.
2. Shadow sample:
   - validates cascade overlays and frustums.
3. Reflection probe sample:
   - validates probe volumes, probe influence, and cubemap face/mip debug views.
4. `SampleReflectionTestSpheres`:
   - validates selected-object material inspection against known authored values.
   - validates reflection debug behavior on highly reflective materials.
5. Stress scene with many lights:
   - validates light tile overlay saturation and timing query stability.

## Testing Plan

Automated CPU tests:

1. `DebugDrawList_BoxProducesTwelveLines`
2. `DebugDrawList_SphereClampsSegments`
3. `DebugDrawList_RespectsLineBudget`
4. `DebugDrawList_ClearFrameKeepsPersistentLines`
5. `DebugDrawList_ClearPersistentRemovesPersistentLines`
6. `DebugOverlaySettings_DefaultsAreShippingSafe`
7. `RendererDiagnostics_EmptyInitializesDebugFields`
8. `SelectedObjectInspection_DecodesMaterialRenderMode`
9. `SelectedObjectInspection_DecodesMetallicRoughnessAo`
10. `GpuTimingSnapshot_MissingPassReturnsUnavailable`
11. `GpuTimingSnapshot_ConvertsTimestampDeltaToMicroseconds`
12. `FrameCaptureRequest_DefaultPathIsStableAndUnique`

Shader tests:

1. Existing `ShaderBuildTests` must include new debug shaders.
2. Debug shaders must compile in Debug and Release.
3. If `NJULF_RENDER_DEBUG` removes shader use from shipping builds, shader compilation can still remain in tests to prevent bit rot.

Manual GPU validation:

1. `dotnet build Njulf.sln`
2. `dotnet test Njulf.sln`
3. Run `NjulfHelloGame` with Vulkan validation layers.
4. Toggle every debug overlay.
5. Request screenshot.
6. Request RenderDoc capture without RenderDoc.
7. Request RenderDoc capture under RenderDoc.
8. Resize the window with overlays active.
9. Disable debug tooling at runtime and verify debug passes stop recording work.
10. Shutdown with pending screenshot/capture requests and verify no validation errors.

## Performance Requirements

1. Debug disabled:
   - no debug vertex upload.
   - no debug overlay pass.
   - no timestamp writes unless GPU timing is independently enabled.
   - no CPU debug snapshots.
2. Debug enabled:
   - CPU debug snapshot generation should be bounded and measurable.
   - debug draw upload should be linear in emitted line count.
   - overlay passes should be visible in diagnostics and RenderDoc.
3. GPU timings:
   - no per-frame GPU waits.
   - query result collection must tolerate frames where data is not ready.
4. Screenshots:
   - one request should not permanently stall rendering.
   - repeated requests should be queued or rate-limited.
5. Debug text:
   - until a real text renderer exists, prefer console output and structured data over improvised glyph rendering.

## Failure Handling

1. If debug buffers overflow, draw what fits and report dropped counts.
2. If selected object index is invalid, clear selection and report no selected object.
3. If GPU timestamp query creation fails, disable GPU timing and report unavailable.
4. If screenshot path cannot be written, report the exception and keep rendering.
5. If RenderDoc is unavailable, report once and keep rendering.
6. If a debug shader fails to compile, fail build/tests like any other shader.
7. If debug tooling is disabled by compile symbol, public APIs should remain callable but cheap.

## Implementation Order

1. Add CPU contracts, settings, diagnostics defaults, and tests.
2. Add selected-object/material inspection using `SampleReflectionTestSpheres`.
3. Add debug draw command expansion and CPU snapshots.
4. Add debug draw GPU pass and shaders.
5. Add light tile, bounds, cascade, and reflection probe overlays.
6. Add GPU timestamp query manager.
7. Add screenshot capture.
8. Add RenderDoc trigger integration.
9. Harden shipping gates, diagnostics, tests, and manual validation.

This order keeps risky Vulkan readback/capture work until after the simpler debug rendering and inspection contracts are already useful.

## Final Acceptance Criteria

Phase 18 is complete when:

1. Artists and programmers can inspect lighting, culling, material values, reflection probes, and selected objects during content creation.
2. Debug primitives render correctly as lines, boxes, spheres, frustums, bounds, light volumes, and probe volumes.
3. Renderer overlays include light tiles, cascades, reflection probes, decal volume no-op state, object bounds, and selected-object state.
4. GPU pass timings are available without an external profiler and without stalling the frame loop.
5. Screenshot capture writes a correct final-frame PNG.
6. RenderDoc capture can be requested when RenderDoc is available and fails gracefully when it is not.
7. Debug passes and resources have useful Vulkan debug names.
8. `dotnet build Njulf.sln` and `dotnet test Njulf.sln` pass.
9. Vulkan validation remains clean during startup, rendering, overlay toggles, screenshot capture, RenderDoc trigger, resize, and shutdown.
10. Debug features can be disabled or compiled out for shipping.

