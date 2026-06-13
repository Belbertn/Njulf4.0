# Phase 19 Performance And Memory Budgets Plan

Target outcome: Njulf can keep visual quality inside explicit CPU, GPU, memory, upload, and content budgets. Performance regressions should be measurable, content budgets should be documented, and runtime stalls should be visible without relying on an external profiler for every investigation.

Phase 19 is not just "add more counters." It should establish a production budget system:
- target platform profiles with frame, memory, upload, draw, light, shadow, reflection, and texture budgets.
- runtime telemetry that reports actual cost by category.
- budget evaluation that marks warning and failure states.
- stress scenes that reproduce common production bottlenecks.
- automated CPU-side tests for budget math and diagnostics.
- manual GPU validation steps for timing, memory, stalls, and RenderDoc captures.

## Current Baseline

The renderer already has useful diagnostics and resource ownership points:

1. `RendererDiagnostics` reports object counts, meshlet counts, CPU timings, some GPU timings, texture counts, upload bytes, shadow counts, render target formats, AO, AA, environment, and reflection data.
2. `SceneRenderingData` carries per-frame upload bytes, object and meshlet CPU culling stats, CPU pass record timings, GPU timing fields, buffer sizes, and active feature settings.
3. `RenderGraph` already records CPU pass timings and Vulkan debug labels.
4. `BufferManager` owns Vulkan/VMA buffers but does not yet expose active allocation totals by usage/category.
5. `TextureManager` estimates texture bytes and tracks file texture count, fallback mip count, and downscaled texture count.
6. `RenderTargetManager` owns HDR scene color, fogged scene color, AO targets, AA targets, motion vectors, TAA histories, and bloom chains.
7. `DirectionalShadowResources`, `SpotShadowAtlas`, `PointShadowCubemapArray`, `EnvironmentManager`, and `ReflectionProbeManager` own large GPU images that need budget attribution.
8. `StagingRing` has a fixed per-frame staging size and throws on overflow, but it does not expose high-water marks or budget pressure.
9. `SynchronizationManager` waits on in-flight fences each frame, but wait duration and stall reason are not reported.
10. `SampleReflectionTestSpheres.cs` provides a small, deterministic material stress fixture for reflective materials and probe validation.

The missing layer is a coherent budget model that connects those counters to production targets and makes regressions obvious.

## Industry Standard Definition

Phase 19 is industry-standard when:

1. Target platform budgets are explicit, versioned, and visible at runtime.
2. GPU memory is reported by category:
   - mesh buffers.
   - material buffers.
   - object and instance buffers.
   - light buffers.
   - texture assets.
   - render targets.
   - shadow maps.
   - environment maps.
   - reflection probes.
   - staging and upload buffers.
   - debug tooling resources.
3. CPU frame time is split into scene build, culling, uploads, material upload, render pass recording, present/acquire waits, and total draw time.
4. GPU pass time is split by render pass and collected without synchronous GPU waits.
5. Upload budget is enforced or at least reported with clear over-budget diagnostics.
6. Normal gameplay avoids hidden `DeviceWaitIdle`, queue idle waits, and long fence waits.
7. Stress scenes exist for many lights, many materials, many transparent objects, large texture sets, large meshlet counts, reflection-heavy content, and upload bursts.
8. Sample diagnostics can print compact budget status, not just raw counters.
9. Budget failures are testable on CPU-side math and manually validated on GPU.
10. The system supports future CI performance snapshots without depending on a specific local GPU result.

## Non-Goals

Do not optimize every bottleneck in this phase. This phase makes bottlenecks measurable and enforces budget contracts.

Do not add a new renderer architecture or rewrite the render graph.

Do not require a full editor UI. Console output and structured diagnostics are enough for this phase, provided the data model is editor-ready.

Do not make GPU timings block the frame loop. If timing data is pending, report it as pending.

Do not make every budget a hard failure in runtime builds. Hard failures belong in tests, CI checks, or explicit stress validation modes.

Do not depend on vendor-specific memory extensions for the first implementation. Use VMA allocation sizes and renderer-owned estimates first; add vendor-specific heap telemetry later if needed.

## Proposed Files

New renderer files:

1. `Njulf.Rendering/Diagnostics/RenderBudgetProfile.cs`
2. `Njulf.Rendering/Diagnostics/RenderBudgetSettings.cs`
3. `Njulf.Rendering/Diagnostics/RenderBudgetCategory.cs`
4. `Njulf.Rendering/Diagnostics/RenderBudgetStatus.cs`
5. `Njulf.Rendering/Diagnostics/RenderBudgetSnapshot.cs`
6. `Njulf.Rendering/Diagnostics/RenderBudgetEvaluator.cs`
7. `Njulf.Rendering/Diagnostics/MemoryBudgetSnapshot.cs`
8. `Njulf.Rendering/Diagnostics/MemoryBudgetCategory.cs`
9. `Njulf.Rendering/Diagnostics/GpuAllocationTracker.cs`
10. `Njulf.Rendering/Diagnostics/FrameTimingSnapshot.cs`
11. `Njulf.Rendering/Diagnostics/UploadBudgetTracker.cs`
12. `Njulf.Rendering/Diagnostics/RuntimeStallTracker.cs`
13. `Njulf.Rendering/Diagnostics/PerformanceSampleWindow.cs`
14. `Njulf.Rendering/Diagnostics/PerformanceSnapshotWriter.cs`
15. `NjulfHelloGame/SampleStressSceneBuilder.cs`
16. `NjulfHelloGame/SamplePerformanceScenario.cs`
17. `NjulfHelloGame/SamplePerformanceScenarioRunner.cs`

Existing files to update:

1. `Njulf.Rendering/VulkanRenderer.cs`
2. `Njulf.Rendering/Data/RenderSettings.cs`
3. `Njulf.Rendering/Data/RendererDiagnostics.cs`
4. `Njulf.Rendering/Data/SceneRenderingData.cs`
5. `Njulf.Rendering/Memory/BufferManager.cs`
6. `Njulf.Rendering/Memory/StagingRing.cs`
7. `Njulf.Rendering/Memory/FenceBasedDeleter.cs`
8. `Njulf.Rendering/Resources/TextureManager.cs`
9. `Njulf.Rendering/Resources/RenderTargetManager.cs`
10. `Njulf.Rendering/Resources/RenderTarget.cs`
11. `Njulf.Rendering/Resources/MeshManager.cs`
12. `Njulf.Rendering/Resources/MaterialManager.cs`
13. `Njulf.Rendering/Resources/LightManager.cs`
14. `Njulf.Rendering/Resources/DirectionalShadowResources.cs`
15. `Njulf.Rendering/Resources/SpotShadowAtlas.cs`
16. `Njulf.Rendering/Resources/PointShadowCubemapArray.cs`
17. `Njulf.Rendering/Resources/EnvironmentManager.cs`
18. `Njulf.Rendering/Resources/ReflectionProbeManager.cs`
19. `Njulf.Rendering/Core/SynchronizationManager.cs`
20. `Njulf.Rendering/Core/SwapchainManager.cs`
21. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
22. `NjulfHelloGame/SampleInputController.cs`
23. `NjulfHelloGame/Program.cs`
24. `Njulf.Tests/RendererDiagnosticsTests.cs`
25. `Njulf.Tests/RenderBudgetEvaluatorTests.cs`
26. `Njulf.Tests/MemoryBudgetSnapshotTests.cs`
27. `Njulf.Tests/UploadBudgetTrackerTests.cs`

If Phase 18 has already introduced `Njulf.Rendering/Debug`, keep performance and budget data under `Diagnostics` to avoid mixing debug drawing with runtime telemetry.

## Budget Profiles

Add explicit target profiles. These are not guesses for final shipping platforms; they are starting contracts that can be tuned once real target hardware is chosen.

```csharp
public enum RenderBudgetProfileKind
{
    Development,
    LowSpec1080p30,
    MidSpec1080p60,
    HighSpec1440p60,
    Ultra4k60,
    StressUnlimited
}
```

Budget profile model:

```csharp
public sealed record RenderBudgetProfile(
    RenderBudgetProfileKind Kind,
    string Name,
    uint OutputWidth,
    uint OutputHeight,
    float ResolutionScale,
    double TargetFrameMilliseconds,
    double CpuFrameBudgetMilliseconds,
    double GpuFrameBudgetMilliseconds,
    ulong GpuMemoryBudgetBytes,
    ulong UploadBudgetBytesPerFrame,
    int ObjectBudget,
    int MeshletBudget,
    int MaterialBudget,
    int TextureBudget,
    int LightBudget,
    int ShadowedLightBudget,
    int ReflectionProbeBudget,
    int TransparentObjectBudget);
```

Initial recommended budgets:

1. `Development`
   - 1920x1080, 60 FPS target.
   - 16.67 ms total frame.
   - 10 ms GPU render budget.
   - 6 ms CPU renderer budget.
   - 2 GiB renderer GPU memory budget.
   - 32 MiB upload budget per frame.
2. `LowSpec1080p30`
   - 1920x1080, 30 FPS target.
   - 33.33 ms total frame.
   - 18 ms GPU render budget.
   - 10 ms CPU renderer budget.
   - 1 GiB renderer GPU memory budget.
   - 8 MiB upload budget per frame.
3. `MidSpec1080p60`
   - 1920x1080, 60 FPS target.
   - 16.67 ms total frame.
   - 11 ms GPU render budget.
   - 5 ms CPU renderer budget.
   - 2 GiB renderer GPU memory budget.
   - 16 MiB upload budget per frame.
4. `HighSpec1440p60`
   - 2560x1440, 60 FPS target.
   - 16.67 ms total frame.
   - 12 ms GPU render budget.
   - 5 ms CPU renderer budget.
   - 4 GiB renderer GPU memory budget.
   - 24 MiB upload budget per frame.
5. `Ultra4k60`
   - 3840x2160, 60 FPS target.
   - 16.67 ms total frame.
   - 14 ms GPU render budget.
   - 5 ms CPU renderer budget.
   - 6 GiB renderer GPU memory budget.
   - 32 MiB upload budget per frame.
6. `StressUnlimited`
   - no warning thresholds except catastrophic allocation or validation failures.
   - used to find limits, not to assert production quality.

Rules:

1. Keep budget profiles data-driven in code first.
2. Save/load from config later if Phase 17 settings persistence is ready.
3. Every diagnostic warning should name the active profile.
4. Budget profiles should be independent of quality presets, but the sample can pair them for validation.

## Budget Status Model

Add a uniform status model:

```csharp
public enum RenderBudgetStatus
{
    Unknown,
    WithinBudget,
    Warning,
    OverBudget,
    Unavailable
}

public sealed record BudgetMetric(
    string Name,
    double Value,
    double WarningThreshold,
    double FailureThreshold,
    string Unit,
    RenderBudgetStatus Status);
```

Threshold policy:

1. `WithinBudget`: value <= 85 percent of failure threshold.
2. `Warning`: value > 85 percent and <= 100 percent.
3. `OverBudget`: value > 100 percent.
4. `Unavailable`: data cannot be trusted this frame.
5. `Unknown`: startup state before enough frames have been sampled.

For highly variable timings, evaluate both:

1. last completed frame.
2. rolling average over 120 frames.
3. 95th percentile over 300 frames.

Do not let a single shader compile hitch or asset load frame permanently mark runtime as bad. Keep transient spikes visible, but separate them from steady-state budget status.

## Memory Categories

Add `MemoryBudgetCategory`:

```csharp
public enum MemoryBudgetCategory
{
    Unknown,
    MeshBuffers,
    ObjectAndInstanceBuffers,
    MaterialBuffers,
    LightBuffers,
    TextureAssets,
    RenderTargets,
    ShadowMaps,
    EnvironmentMaps,
    ReflectionProbes,
    StagingBuffers,
    DiagnosticsAndDebug,
    Swapchain,
    SamplersAndDescriptors
}
```

Add `MemoryBudgetSnapshot`:

```csharp
public sealed record MemoryBudgetSnapshot(
    ulong TotalTrackedBytes,
    ulong BudgetBytes,
    IReadOnlyList<MemoryBudgetEntry> Entries);

public sealed record MemoryBudgetEntry(
    MemoryBudgetCategory Category,
    ulong Bytes,
    int AllocationCount,
    string Description);
```

Rules:

1. Track explicit allocated size, not only used bytes.
2. Where useful, report both allocated bytes and used bytes.
3. All byte values must be `ulong`.
4. Do not use decimal MB in stored diagnostics. Store bytes and format only in reporters.
5. Never double count the same allocation.

## Allocation Tracking

Add a lightweight tracker behind `BufferManager`, texture/image resource classes, and render target creation.

```csharp
public sealed class GpuAllocationTracker
{
    public void RegisterBuffer(BufferHandle handle, ulong size, BufferUsageFlags usage, MemoryBudgetCategory category, string name);
    public void RegisterImage(ulong nativeHandle, ulong estimatedBytes, Format format, Extent3D extent, uint mipLevels, uint arrayLayers, MemoryBudgetCategory category, string name);
    public void RetireBuffer(BufferHandle handle);
    public void RetireImage(ulong nativeHandle);
    public MemoryBudgetSnapshot CreateSnapshot(RenderBudgetProfile profile);
}
```

Implementation notes:

1. Track by stable renderer handles when available.
2. Track images by native image handle when no managed handle exists.
3. Use estimated image sizes based on format, extent, mip count, array layers, and samples.
4. Record allocation count and total bytes by category.
5. Make tracker thread-safe.
6. Avoid forcing every allocation callsite to manually pass a category immediately; add category hints in wrapper methods first.
7. Provide `Unknown` category as an interim safety net, but make `UnknownBytes` a warning in diagnostics.

VMA integration:

1. Keep per-allocation estimates in the first slice.
2. Later, add optional VMA heap/budget statistics if the binding exposes `vmaGetHeapBudgets`.
3. If VMA heap budget is available, report both:
   - renderer tracked bytes.
   - physical heap usage/budget.
4. Do not use heap budget values for tests because they are hardware-specific.

## Category Sources

### Mesh Buffers

Source: `MeshManager`.

Existing fields:

1. `VertexBytesUsed`
2. `IndexBytesUsed`
3. `MeshMetadataBytesUsed`
4. `MeshletBytesUsed`
5. `MeshletVertexIndexBytesUsed`
6. `MeshletTriangleIndexBytesUsed`

Required additions:

1. allocated sizes for each mesh buffer.
2. used ratio for each mesh buffer.
3. meshlet LOD memory split if available.

Diagnostics:

```csharp
ulong MeshBufferAllocatedBytes;
ulong MeshBufferUsedBytes;
float MeshBufferUtilization;
```

### Object And Instance Buffers

Source: scene upload buffers in `VulkanRenderer` and `SceneRenderingData`.

Existing fields:

1. `ObjectBufferSize`
2. `InstanceBufferSize`
3. `MeshletDrawBufferSize`
4. `TransparentMeshletDrawBufferSize`
5. `ObjectUploadBytes`
6. `InstanceUploadBytes`
7. `MeshletDrawUploadBytes`
8. `TransparentMeshletDrawUploadBytes`

Required additions:

1. explicit category mapping for object, instance, and draw command buffers.
2. peak size this session.
3. resize count.

Diagnostics:

```csharp
ulong SceneBufferAllocatedBytes;
ulong SceneBufferPeakBytes;
int SceneBufferResizeCount;
```

### Material Buffers

Source: `MaterialManager`.

Existing fields:

1. `MaterialBufferSize`
2. `MaterialUploadBytes`
3. `CpuMaterialUploadMicroseconds`
4. material counts.

Required additions:

1. material buffer allocated size.
2. active material count vs uploaded slot count.
3. material buffer utilization.

Diagnostics:

```csharp
ulong MaterialBufferAllocatedBytes;
float MaterialBufferUtilization;
```

### Light Buffers

Source: `LightManager` and tiled light buffers in renderer.

Existing fields:

1. `LightCount`
2. `LightUploadBytes`
3. `TiledLightHeaderBufferSize`
4. `TiledLightIndexBufferSize`
5. `TileCountX`
6. `TileCountY`

Required additions:

1. GPU light buffer allocated size.
2. tiled light buffer utilization.
3. maximum lights per tile pressure.
4. overflow or clamping events if light lists saturate.

Diagnostics:

```csharp
ulong LightBufferAllocatedBytes;
ulong TiledLightBufferAllocatedBytes;
int LightTileSaturationCount;
int MaxLightsInAnyTile;
float AverageLightsPerNonEmptyTile;
```

### Texture Assets

Source: `TextureManager`.

Existing fields:

1. `TextureCount`
2. `LoadedFileTextureCount`
3. `MipmapFallbackCount`
4. `DownscaledTextureCount`
5. `MaxLoadedTextureDimension`
6. `EstimatedTextureBytes`

Required additions:

1. active texture estimated bytes by format.
2. default texture bytes vs file texture bytes.
3. cached texture reference count summary.
4. texture bindless index pressure.

Diagnostics:

```csharp
ulong TextureAssetBytes;
ulong DefaultTextureBytes;
ulong FileTextureBytes;
int TextureCacheEntryCount;
int TextureBindlessUsedCount;
int TextureBindlessFreeCount;
```

### Render Targets

Source: `RenderTargetManager` and `RenderTarget`.

Required additions:

1. estimated bytes for every target:
   - `SceneColor`
   - `FoggedSceneColor`
   - `AmbientOcclusionRaw`
   - `AmbientOcclusionBlurred`
   - `AmbientOcclusionScratch`
   - `LdrSceneColor`
   - `SmaaEdges`
   - `SmaaBlendWeights`
   - `MotionVectors`
   - `TaaHistoryA`
   - `TaaHistoryB`
   - bloom mip chain
   - bloom scratch chain
2. target count.
3. resize count.
4. current extent and resolution scale.

Diagnostics:

```csharp
ulong RenderTargetBytes;
int RenderTargetCount;
int RenderTargetResizeCount;
ulong BloomRenderTargetBytes;
ulong AmbientOcclusionRenderTargetBytes;
ulong AntiAliasingRenderTargetBytes;
```

### Shadow Maps

Sources:

1. `DirectionalShadowResources`
2. `SpotShadowAtlas`
3. `PointShadowCubemapArray`

Required additions:

1. directional shadow image bytes.
2. spot atlas image bytes.
3. point cubemap array bytes.
4. shadow data buffer bytes.
5. atlas utilization and face count already partly reported.

Diagnostics:

```csharp
ulong ShadowMapBytes;
ulong DirectionalShadowBytes;
ulong SpotShadowAtlasBytes;
ulong PointShadowBytes;
float SpotShadowAtlasUtilization;
float PointShadowFaceUtilization;
```

### Environment Maps

Source: `EnvironmentManager`.

Existing fields:

1. `EnvironmentTextureBytes`
2. environment, irradiance, prefiltered, BRDF sizes.

Required additions:

1. environment map allocation count.
2. bytes split:
   - source environment.
   - irradiance.
   - prefiltered specular.
   - BRDF LUT.
3. reload count if environment changes at runtime.

Diagnostics:

```csharp
ulong EnvironmentMapBytes;
ulong IrradianceMapBytes;
ulong PrefilteredEnvironmentBytes;
ulong BrdfLutBytes;
```

### Reflection Probes

Source: `ReflectionProbeManager`.

Existing fields:

1. `ReflectionProbeEstimatedBytes`
2. probe count/capacity.
3. capture and prefilter CPU/GPU timings.

Required additions:

1. capture target bytes.
2. cubemap array bytes.
3. debug texture bytes.
4. queued capture pressure.
5. per-frame capture budget consumed.

Diagnostics:

```csharp
ulong ReflectionProbeBytes;
ulong ReflectionProbeCaptureTargetBytes;
ulong ReflectionProbeCubemapArrayBytes;
int ReflectionProbeCaptureBudgetUsed;
int ReflectionProbeCaptureBudgetExceeded;
```

### Staging Buffers

Source: `StagingRing`.

Existing behavior:

1. fixed `DefaultStagingBufferSize`.
2. per-frame offset reset.
3. throws on overflow.

Required additions:

1. current frame allocated bytes.
2. peak bytes per frame.
3. overflow count.
4. upload budget bytes.
5. warning threshold.

Diagnostics:

```csharp
ulong StagingBufferAllocatedBytes;
ulong StagingBytesUsedThisFrame;
ulong StagingBytesPeakThisSession;
int StagingOverflowCount;
int UploadBudgetExceeded;
float UploadBudgetUtilization;
```

### Swapchain

Source: `SwapchainManager`.

Required additions:

1. swapchain image count.
2. swapchain format.
3. estimated swapchain image bytes.
4. depth buffer bytes if owned by swapchain manager.

Diagnostics:

```csharp
ulong SwapchainEstimatedBytes;
int SwapchainImageCount;
string SwapchainFormat;
```

## CPU Timing

The existing CPU timings are a good start. Phase 19 should make them budget-aware and include waits/stalls.

Add timings:

```csharp
long CpuAcquireImageMicroseconds;
long CpuWaitForFrameFenceMicroseconds;
long CpuQueueSubmitMicroseconds;
long CpuPresentMicroseconds;
long CpuFenceResetMicroseconds;
long CpuSwapchainRecreateMicroseconds;
long CpuAssetLoadMicroseconds;
long CpuTextureUploadMicroseconds;
long CpuMeshUploadMicroseconds;
long CpuTotalFrameMicroseconds;
```

Rules:

1. Use `Stopwatch.GetTimestamp()` consistently.
2. Do not allocate when measuring hot paths.
3. Record wait durations separately from work durations.
4. A long fence wait is a stall, not "CPU render work."
5. Present/acquire time should be reported separately because vsync and OS scheduling can dominate it.

Budget interpretation:

1. CPU renderer work budget includes scene build, culling, uploads, and command recording.
2. CPU total frame budget includes waits and present/acquire.
3. Report both because they answer different questions.

## GPU Timing

If Phase 18 has already implemented `GpuTimingQueryManager`, reuse it. If not, implement the timing subset needed for Phase 19.

Required pass timings:

1. directional shadow.
2. spot shadows.
3. point shadows.
4. depth prepass.
5. Hi-Z build.
6. AO.
7. AO blur.
8. tiled light culling.
9. opaque Forward+.
10. transparent Forward+.
11. bloom extract.
12. bloom downsample.
13. bloom upsample.
14. fog.
15. anti-aliasing passes.
16. tone map composite.
17. debug draw and overlay if Phase 18 is implemented.
18. reflection probe capture/prefilter.

Rules:

1. Use timestamp queries.
2. Read results only after the frame fence for that frame is complete.
3. Report timing data as pending until available.
4. Do not call `vkQueueWaitIdle`, `vkDeviceWaitIdle`, or block on query results in normal rendering.
5. Use pass names from `RenderGraph.PassNames` where possible to avoid hardcoded drift.

Diagnostics:

```csharp
int GpuTimingSupported;
int GpuTimingEnabled;
int GpuTimingPending;
int GpuTimingValid;
long GpuTotalFrameMicroseconds;
long GpuMainSceneMicroseconds;
long GpuPostProcessingMicroseconds;
long GpuShadowMicroseconds;
long GpuReflectionMicroseconds;
```

## Upload Budget

Add an upload budget tracker that consumes all per-frame upload sources:

1. object data.
2. instance data.
3. meshlet draw commands.
4. transparent meshlet draw commands.
5. material data.
6. light data.
7. shadow data.
8. texture uploads.
9. mesh uploads.
10. reflection probe uploads.
11. environment uploads.
12. screenshots/readbacks should be reported separately and should not count as asset upload.

Model:

```csharp
public sealed class UploadBudgetTracker
{
    public void BeginFrame(ulong budgetBytes);
    public void AddUpload(string name, ulong bytes, UploadBudgetCategory category);
    public UploadBudgetSnapshot EndFrame();
}
```

Categories:

```csharp
public enum UploadBudgetCategory
{
    Scene,
    Materials,
    Lights,
    Meshes,
    Textures,
    Shadows,
    Environment,
    Reflections,
    Debug,
    Readback
}
```

Diagnostics:

```csharp
ulong UploadBudgetBytes;
ulong UploadBytesThisFrame;
ulong UploadBytesPeakThisSession;
int UploadBudgetExceededFrameCount;
UploadBudgetCategory LargestUploadCategory;
ulong LargestUploadBytes;
```

Policy:

1. Phase 19 should report over-budget uploads first.
2. Add throttling later only for asset streaming paths that can safely defer work.
3. Do not split required per-frame scene uploads unless the scene upload system supports partial updates.
4. Texture and mesh streaming should be the first candidates for future throttling.

## Runtime Stall Tracking

Add `RuntimeStallTracker`.

Stall sources:

1. frame fence wait.
2. transfer fence wait.
3. swapchain acquire wait.
4. present wait.
5. explicit `VulkanContext.WaitIdle`.
6. synchronous texture upload.
7. synchronous mesh upload.
8. material buffer resize that waits for other frames.
9. mesh buffer resize/copy.
10. swapchain recreation.

Model:

```csharp
public enum RuntimeStallReason
{
    Unknown,
    FrameFence,
    TransferFence,
    SwapchainAcquire,
    Present,
    DeviceWaitIdle,
    TextureUpload,
    MeshUpload,
    MaterialBufferResize,
    MeshBufferResize,
    SwapchainRecreate
}

public sealed record RuntimeStallEvent(
    RuntimeStallReason Reason,
    long DurationMicroseconds,
    string Detail);
```

Rules:

1. Track count and total duration per reason.
2. Keep a small ring buffer of recent stall events.
3. Do not allocate strings in the hot path every frame unless a stall exceeds a threshold.
4. Default stall warning threshold: 1000 microseconds.
5. Report maximum stall this session and maximum stall in the last sample window.

Diagnostics:

```csharp
int RuntimeStallCountThisFrame;
long RuntimeStallMicrosecondsThisFrame;
RuntimeStallReason WorstRuntimeStallReason;
long WorstRuntimeStallMicroseconds;
int DeviceWaitIdleCount;
```

Also make `VulkanContext.WaitIdle()` increment a diagnostic counter when called from normal runtime paths. It is acceptable for shutdown and explicit validation modes, but it should be visible.

## Performance Sample Windows

Add rolling sample windows for human-readable performance:

```csharp
public sealed class PerformanceSampleWindow
{
    public int Capacity { get; }
    public void AddSample(double value);
    public double Average { get; }
    public double Min { get; }
    public double Max { get; }
    public double Percentile95 { get; }
}
```

Track windows for:

1. CPU renderer work.
2. CPU total frame.
3. GPU total frame.
4. upload bytes.
5. tracked GPU memory.
6. frame fence wait.
7. draw submitted meshlets.
8. visible objects.
9. light count.
10. transparent object count.

Recommended window sizes:

1. 60 frames for fast feedback.
2. 300 frames for smoother reporting.

Diagnostics should report both current value and rolling status.

## RendererDiagnostics Additions

Add these fields to `RendererDiagnostics` and initialize them in `RendererDiagnostics.Empty`:

```csharp
RenderBudgetProfileKind BudgetProfile;
RenderBudgetStatus CpuFrameBudgetStatus;
RenderBudgetStatus GpuFrameBudgetStatus;
RenderBudgetStatus GpuMemoryBudgetStatus;
RenderBudgetStatus UploadBudgetStatus;
double TargetFrameMilliseconds;
double CpuFrameBudgetMilliseconds;
double GpuFrameBudgetMilliseconds;
ulong GpuMemoryBudgetBytes;
ulong UploadBudgetBytes;
long CpuTotalFrameMicroseconds;
long CpuRendererWorkMicroseconds;
long CpuAcquireImageMicroseconds;
long CpuWaitForFrameFenceMicroseconds;
long CpuQueueSubmitMicroseconds;
long CpuPresentMicroseconds;
long CpuSwapchainRecreateMicroseconds;
long GpuTotalFrameMicroseconds;
long GpuMainSceneMicroseconds;
long GpuPostProcessingMicroseconds;
long GpuShadowMicroseconds;
long GpuReflectionMicroseconds;
int GpuTimingSupported;
int GpuTimingEnabled;
int GpuTimingPending;
int GpuTimingValid;
ulong TotalTrackedGpuBytes;
ulong TotalTrackedGpuPeakBytes;
ulong UnknownGpuBytes;
ulong MeshBufferAllocatedBytes;
ulong MeshBufferUsedBytes;
float MeshBufferUtilization;
ulong SceneBufferAllocatedBytes;
ulong SceneBufferPeakBytes;
int SceneBufferResizeCount;
ulong MaterialBufferAllocatedBytes;
float MaterialBufferUtilization;
ulong LightBufferAllocatedBytes;
ulong TiledLightBufferAllocatedBytes;
int LightTileSaturationCount;
int MaxLightsInAnyTile;
float AverageLightsPerNonEmptyTile;
ulong TextureAssetBytes;
ulong DefaultTextureBytes;
ulong FileTextureBytes;
int TextureCacheEntryCount;
int TextureBindlessUsedCount;
int TextureBindlessFreeCount;
ulong RenderTargetBytes;
int RenderTargetCount;
int RenderTargetResizeCount;
ulong BloomRenderTargetBytes;
ulong AmbientOcclusionRenderTargetBytes;
ulong AntiAliasingRenderTargetBytes;
ulong ShadowMapBytes;
ulong DirectionalShadowBytes;
ulong SpotShadowAtlasBytes;
ulong PointShadowBytes;
float SpotShadowAtlasUtilization;
float PointShadowFaceUtilization;
ulong EnvironmentMapBytes;
ulong IrradianceMapBytes;
ulong PrefilteredEnvironmentBytes;
ulong BrdfLutBytes;
ulong ReflectionProbeBytes;
ulong ReflectionProbeCaptureTargetBytes;
ulong ReflectionProbeCubemapArrayBytes;
int ReflectionProbeCaptureBudgetUsed;
int ReflectionProbeCaptureBudgetExceeded;
ulong StagingBufferAllocatedBytes;
ulong StagingBytesUsedThisFrame;
ulong StagingBytesPeakThisSession;
int StagingOverflowCount;
ulong UploadBytesThisFrame;
ulong UploadBytesPeakThisSession;
int UploadBudgetExceededFrameCount;
int RuntimeStallCountThisFrame;
long RuntimeStallMicrosecondsThisFrame;
RuntimeStallReason WorstRuntimeStallReason;
long WorstRuntimeStallMicroseconds;
int DeviceWaitIdleCount;
```

Do not add large arrays or dictionaries to `RendererDiagnostics`. Use side-channel snapshots for detailed per-category breakdowns:

1. `MemoryBudgetSnapshot`
2. `FrameTimingSnapshot`
3. `UploadBudgetSnapshot`
4. `RuntimeStallSnapshot`

## SceneRenderingData Additions

Add frame-local budget data:

```csharp
public RenderBudgetProfileKind BudgetProfile { get; set; }
public ulong UploadBudgetBytes { get; set; }
public ulong UploadBytesThisFrame { get; set; }
public bool UploadBudgetExceeded { get; set; }
public long CpuAcquireImageMicroseconds { get; set; }
public long CpuWaitForFrameFenceMicroseconds { get; set; }
public long CpuQueueSubmitMicroseconds { get; set; }
public long CpuPresentMicroseconds { get; set; }
public long CpuTotalFrameMicroseconds { get; set; }
public long RuntimeStallMicrosecondsThisFrame { get; set; }
public int RuntimeStallCountThisFrame { get; set; }
```

Keep persistent budget state outside `SceneRenderingData`. The scene data object should represent one frame, while budget evaluators and sample windows maintain history.

## RenderSettings Additions

Add:

```csharp
public sealed class PerformanceBudgetSettings
{
    public bool Enabled { get; set; } = true;
    public RenderBudgetProfileKind Profile { get; set; } = RenderBudgetProfileKind.Development;
    public bool GpuTimingEnabled { get; set; } = true;
    public bool MemoryTrackingEnabled { get; set; } = true;
    public bool StallTrackingEnabled { get; set; } = true;
    public bool PrintBudgetWarnings { get; set; } = true;
    public bool ExportSnapshots { get; set; }
    public string SnapshotDirectory { get; set; } = "PerformanceSnapshots";
}
```

Add to `RenderSettings`:

```csharp
public PerformanceBudgetSettings Performance { get; } = new();
```

If Phase 17 has settings serialization, include these fields there. If not, keep runtime-only first and document persistence as a follow-up.

## Sample Diagnostics Reporter

Add compact budget lines to `SampleDiagnosticsReporter`.

Recommended output:

```text
Budget profile=Development cpu=4.2/6.0ms ok gpu=8.6/10.0ms warn mem=612/2048MiB ok upload=3.4/32MiB ok stalls=0
Memory mesh=64MiB scene=12MiB materials=1MiB textures=420MiB rt=86MiB shadows=80MiB env=32MiB refl=64MiB staging=128MiB unknown=0MiB
Frame p95 cpu=5.1ms gpu=9.4ms upload=8.0MiB fenceWait=0.2ms visible=1250 meshlets=18300 lights=96
```

Rules:

1. Print compact budget status every 180 frames in the sample, like existing diagnostics.
2. Print a warning immediately when a metric transitions into `OverBudget`.
3. Avoid dumping every metric every frame.
4. Keep detailed output behind a keybinding.

Sample input additions:

1. cycle budget profile.
2. toggle GPU timing.
3. toggle budget warning output.
4. export current performance snapshot.
5. cycle stress scenario.

## Performance Snapshot Export

Add optional JSON snapshot export:

```csharp
public sealed record PerformanceSnapshot(
    DateTimeOffset Timestamp,
    string RendererVersion,
    string DeviceName,
    RenderBudgetProfileKind Profile,
    RendererDiagnostics Diagnostics,
    MemoryBudgetSnapshot Memory,
    FrameTimingSnapshot Timings,
    UploadBudgetSnapshot Uploads,
    RuntimeStallSnapshot Stalls);
```

File naming:

```text
PerformanceSnapshots/NjulfPerf_yyyyMMdd_HHmmss_ProfileName.json
```

Use cases:

1. local before/after comparison.
2. manual regression reports.
3. future CI artifact.

Rules:

1. Snapshot export must be optional.
2. Avoid serializing native handles.
3. Include device name and driver version when available.
4. Include active render settings summary.

## Stress Scenes

Add `SamplePerformanceScenario`:

```csharp
public enum SamplePerformanceScenario
{
    NormalScene,
    ManyLights,
    ManyMaterials,
    ManyTransparentObjects,
    LargeTextureSet,
    LargeMeshletCount,
    ReflectionHeavy,
    UploadBurst,
    CombinedWorstCase
}
```

Add `SampleStressSceneBuilder` with deterministic generation. Use fixed seeds for repeatable layouts.

### Normal Scene

Purpose: baseline production-like frame.

Requirements:

1. Representative model content.
2. default shadows, AO, AA, fog, bloom, IBL, and reflections.
3. no stress multipliers.

Acceptance:

1. Fits `Development` budget on the developer machine after prior phases are implemented.
2. Used as the default comparison snapshot.

### Many Lights

Purpose: stress Forward+ tile culling and light list pressure.

Implementation:

1. Generate 128, 256, 512, and 1024 local lights in deterministic grids/clusters.
2. Include mixed point and spot lights.
3. Include a few shadowed local lights within budget.
4. Use camera positions that create both sparse and saturated tile patterns.

Metrics:

1. `LightCount`
2. `TileCount`
3. `MaxLightsInAnyTile`
4. `LightTileSaturationCount`
5. `GpuLightCullMicroseconds`
6. `GpuForwardOpaqueMicroseconds`

Acceptance:

1. Over-budget states clearly identify light culling or forward shading as the pressure source.
2. Light budget can be adjusted and warnings follow the active profile.

### Many Materials

Purpose: stress material buffer size, material upload, shader branching, and material deduplication.

Implementation:

1. Generate many spheres or cubes with unique `GPUMaterialData`.
2. Use `SampleReflectionTestSpheres` as the first fixed set of known reflective materials.
3. Add larger grids:
   - 128 materials.
   - 512 materials.
   - 2048 materials.
4. Vary metallic, roughness, emissive, alpha mode, and texture bindings where supported.

Metrics:

1. `MaterialCount`
2. `LoadedMaterialCount`
3. `MaterialBufferAllocatedBytes`
4. `MaterialUploadBytes`
5. `CpuMaterialUploadMicroseconds`
6. `MaterialBufferUtilization`

Acceptance:

1. Material upload spikes are visible.
2. Material buffer growth and utilization are visible.
3. Known `SampleReflectionTestSpheres` materials remain inspectable and visually useful.

### Many Transparent Objects

Purpose: stress sorted transparency, transparent meshlet draw buffers, overdraw, and pass timing.

Implementation:

1. Generate layered transparent quads, planes, or thin boxes.
2. Use deterministic depth ordering patterns.
3. Include 128, 512, 2048 transparent objects.
4. Keep object bounds and transforms deterministic.

Metrics:

1. `TransparentObjectCount`
2. `TransparentMeshletCount`
3. `TransparentMeshletDrawUploadBytes`
4. `GpuTransparentMicroseconds`
5. `CpuTransparentRecordMicroseconds`

Acceptance:

1. Transparent pressure appears separately from opaque pressure.
2. Warnings distinguish transparent object count from total object count.

### Large Texture Set

Purpose: stress texture memory, bindless descriptor pressure, upload spikes, mip fallback, and downscale policy.

Implementation:

1. Load or generate a configurable set of textures.
2. Use different dimensions:
   - 512.
   - 1024.
   - 2048.
   - 4096 when available.
3. Use both sRGB and linear textures.
4. Validate downscale behavior through `TextureManager.MaxLoadedTextureDimension`.
5. If asset files are unavailable, generate deterministic procedural textures in memory only if the existing texture manager gets a safe API for it.

Metrics:

1. `TextureCount`
2. `LoadedFileTextureCount`
3. `TextureAssetBytes`
4. `TextureBindlessUsedCount`
5. `MipmapFallbackCount`
6. `DownscaledTextureCount`
7. `UploadBytesThisFrame`

Acceptance:

1. Texture memory budget warnings are clear.
2. Downscaled texture counts match configured max dimension.
3. Bindless pressure is visible.

### Large Meshlet Count

Purpose: stress meshlet generation, culling, draw command upload, and mesh buffers.

Implementation:

1. Generate a grid of meshes with many triangles.
2. Include repeated meshes and unique meshes to separate instancing from raw mesh memory.
3. Use LOD variants if Phase 15 is implemented.
4. Camera paths should include:
   - mostly visible.
   - mostly frustum culled.
   - occlusion-heavy.

Metrics:

1. `MeshletCountTotal`
2. `MeshletCountSubmittedCpu`
3. `MeshletCandidatesCpu`
4. `MeshletFrustumCulledCpu`
5. `ForwardOcclusionTestedMeshletsGpu`
6. `OcclusionCulledMeshlets`
7. `MeshBufferAllocatedBytes`
8. `MeshletDrawUploadBytes`
9. `GpuDepthPrePassMicroseconds`
10. `GpuForwardOpaqueMicroseconds`

Acceptance:

1. Culling effectiveness is visible.
2. Mesh buffer memory and draw upload pressure are visible.

### Reflection Heavy

Purpose: stress reflection probes, reflective materials, capture cost, and prefilter cost.

Implementation:

1. Place multiple static reflection probes.
2. Add `SampleReflectionTestSpheres` at multiple positions inside overlapping probes.
3. Enable capture-on-load or controlled recapture if supported.
4. Vary probe resolution and probe count.
5. Validate probe capture budget.

Metrics:

1. `ReflectionProbeCount`
2. `ReflectionProbeEstimatedBytes`
3. `ReflectionProbeBytes`
4. `ReflectionProbeCapturesQueued`
5. `ReflectionProbeCapturesCompleted`
6. `CpuReflectionProbeCaptureRecordMicroseconds`
7. `GpuReflectionProbeCaptureMicroseconds`
8. `GpuReflectionProbePrefilterMicroseconds`

Acceptance:

1. Probe memory is categorized under reflection probes.
2. Probe capture cost does not hide inside the main forward pass.
3. Budget warnings identify reflection capture pressure.

### Upload Burst

Purpose: stress staging ring pressure and upload budget behavior.

Implementation:

1. Queue many mesh, material, light, and texture changes in one frame.
2. Repeat with controlled spread over multiple frames.
3. Use a fixed sequence so before/after comparison is meaningful.

Metrics:

1. `UploadBytesThisFrame`
2. `UploadBudgetExceeded`
3. `StagingBytesUsedThisFrame`
4. `StagingBytesPeakThisSession`
5. `StagingOverflowCount`
6. `CpuUploadMicroseconds`
7. `RuntimeStallMicrosecondsThisFrame`

Acceptance:

1. One-frame burst reports over-budget.
2. Spread upload reduces over-budget frames.
3. Staging overflow reports a clear error before throwing.

### Combined Worst Case

Purpose: validate that diagnostics remain useful under heavy combined load.

Implementation:

1. Combine many lights, transparent objects, many materials, large meshlets, and reflection probes.
2. Keep deterministic content counts.
3. Use `StressUnlimited` profile by default.

Acceptance:

1. App remains stable or fails with clear allocation/budget messages.
2. Diagnostics identify the dominant pressure categories.

## Implementation Slices

### Slice 19.1: Budget Contracts And Profiles

Goal: add the data model without changing renderer behavior.

Tasks:

1. Add `RenderBudgetProfileKind`.
2. Add `RenderBudgetProfile`.
3. Add `RenderBudgetStatus`.
4. Add `RenderBudgetSettings`.
5. Add default profile definitions.
6. Add `PerformanceBudgetSettings` to `RenderSettings`.
7. Add budget status fields to `RendererDiagnostics`.
8. Initialize all new diagnostics in `RendererDiagnostics.Empty`.
9. Add `RenderBudgetEvaluator`.
10. Add CPU tests for threshold classification.

Acceptance:

1. `dotnet test Njulf.sln` passes.
2. Budget profile can be changed at runtime.
3. Diagnostics report the active profile and initial unavailable/unknown statuses.

### Slice 19.2: Memory Tracking Infrastructure

Goal: track active GPU memory by category.

Tasks:

1. Add `MemoryBudgetCategory`.
2. Add `MemoryBudgetSnapshot` and `MemoryBudgetEntry`.
3. Add `GpuAllocationTracker`.
4. Wire buffer allocations in `BufferManager`.
5. Add category hints to renderer-owned buffer creation sites.
6. Add image tracking helpers for `RenderTarget`, `TextureManager`, shadow resources, environment, and reflection probes.
7. Add estimated image byte calculation utility that supports:
   - color formats currently used.
   - depth formats currently used.
   - mip levels.
   - array layers.
   - sample count.
8. Add tests for byte estimation.
9. Add diagnostics aggregation in `VulkanRenderer.BuildDiagnostics`.

Acceptance:

1. Memory snapshot total equals sum of entries.
2. Destroyed resources are removed from active totals.
3. Unknown category is reported when a category is missing.
4. Render target, texture, shadow, environment, and reflection bytes are visible separately.

### Slice 19.3: Upload Budget Tracking

Goal: make upload bytes actionable.

Tasks:

1. Add `UploadBudgetCategory`.
2. Add `UploadBudgetTracker`.
3. Add staging ring high-water tracking.
4. Add upload category calls around scene, material, light, mesh, texture, shadow, environment, and reflection uploads.
5. Add upload budget evaluation against active profile.
6. Add diagnostics for upload budget utilization, peak, and exceeded frame count.
7. Add tests for aggregation and threshold status.

Acceptance:

1. Current upload bytes match or exceed existing `UploadedBytes` because category tracking includes all known sources.
2. Per-category upload summary identifies largest contributor.
3. Staging ring high-water marks are visible.

### Slice 19.4: CPU Stall And Wait Tracking

Goal: expose runtime stalls that currently hide inside frame pacing or synchronous resource operations.

Tasks:

1. Add `RuntimeStallReason`.
2. Add `RuntimeStallTracker`.
3. Measure `SynchronizationManager.WaitForFence`.
4. Measure swapchain acquire and present durations.
5. Measure `VulkanContext.WaitIdle`.
6. Measure material buffer resize waits.
7. Measure mesh buffer resize/copy waits where applicable.
8. Add recent stall ring buffer.
9. Add diagnostics fields.
10. Add tests for stall aggregation.

Acceptance:

1. Fence waits are visible separately from CPU work.
2. `WaitIdle` calls are counted.
3. Long synchronous uploads produce stall diagnostics.
4. Shutdown and explicit validation waits can be excluded from runtime over-budget status or marked as lifecycle waits.

### Slice 19.5: GPU Timing Completion

Goal: provide non-stalling GPU pass timings if Phase 18 has not already completed them.

Tasks:

1. Reuse or implement `GpuTimingQueryManager`.
2. Add per-pass timing query hooks in `RenderGraph`.
3. Add non-render-graph timings for reflection probe capture/prefilter if those are outside the graph.
4. Convert timestamps to microseconds.
5. Add frame-latency handling.
6. Add grouped GPU timings:
   - shadows.
   - main scene.
   - post-processing.
   - reflections.
7. Add diagnostics and sample reporter output.
8. Add tests for timestamp delta conversion using fake values.

Acceptance:

1. GPU timings become valid after frame latency.
2. No query result wait stalls the frame.
3. Disabled GPU timing writes no queries.
4. Pass timings are visible in RenderDoc and diagnostics.

### Slice 19.6: Budget Evaluation And Rolling Windows

Goal: turn raw metrics into stable budget status.

Tasks:

1. Add `PerformanceSampleWindow`.
2. Track current, rolling average, max, and p95 for CPU, GPU, upload, memory, and waits.
3. Add `RenderBudgetSnapshot`.
4. Evaluate:
   - CPU renderer work.
   - GPU frame.
   - tracked GPU memory.
   - upload bytes.
   - object count.
   - meshlet count.
   - material count.
   - texture count.
   - light count.
   - transparent count.
   - reflection probe count.
5. Add budget status transition detection.
6. Add concise sample diagnostics.

Acceptance:

1. Budget status transitions are visible.
2. Short spikes and sustained over-budget states are distinguishable.
3. Rolling p95 is available for performance review.

### Slice 19.7: Stress Scene Builder

Goal: produce deterministic content pressure.

Tasks:

1. Add `SamplePerformanceScenario`.
2. Add `SampleStressSceneBuilder`.
3. Implement scenarios:
   - normal scene.
   - many lights.
   - many materials.
   - many transparent objects.
   - large texture set.
   - large meshlet count.
   - reflection heavy.
   - upload burst.
   - combined worst case.
4. Add sample input for cycling scenarios.
5. Use deterministic seeds and counts.
6. Use `SampleReflectionTestSpheres` inside many-material and reflection-heavy scenarios.
7. Add concise scenario summary output.

Acceptance:

1. Every scenario can be selected from `NjulfHelloGame`.
2. Scenario counts appear in diagnostics.
3. Stress scenarios are deterministic enough for before/after comparisons.

### Slice 19.8: Snapshot Export And Manual Regression Workflow

Goal: make before/after performance reviews repeatable.

Tasks:

1. Add `PerformanceSnapshot`.
2. Add `PerformanceSnapshotWriter`.
3. Include:
   - active budget profile.
   - device name.
   - driver/API version.
   - active render settings.
   - renderer diagnostics.
   - memory snapshot.
   - timing snapshot.
   - upload snapshot.
   - stall snapshot.
4. Add sample keybinding to export snapshot.
5. Add README-style instructions inside this plan or future docs.

Acceptance:

1. Snapshot JSON writes successfully.
2. Snapshot has no native handles.
3. Two snapshots can be compared manually.

### Slice 19.9: Hardening And Validation

Goal: make budget telemetry reliable and low overhead.

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run normal sample with validation layers.
4. Run each stress scenario for at least 300 frames.
5. Resize during each major category:
   - render targets.
   - shadows.
   - reflection probes.
   - stress scene.
6. Capture RenderDoc frames for normal, many lights, large meshlet count, reflection heavy, and upload burst.
7. Verify no hidden `WaitIdle` in normal frames.
8. Check budget tracking overhead by comparing with tracking disabled.

Acceptance:

1. Tests pass.
2. Validation layers stay clean.
3. Budget telemetry overhead is measurable and acceptable.
4. Over-budget reports point to actionable categories.

## Automated Tests

Add or update tests:

1. `RenderBudgetEvaluator_WithinWarningAndOverBudgetThresholds`
2. `RenderBudgetEvaluator_UsesActiveProfile`
3. `RenderBudgetEvaluator_UnavailableMetricsDoNotFailBudget`
4. `MemoryBudgetSnapshot_TotalEqualsEntrySum`
5. `MemoryBudgetSnapshot_UnknownCategoryIsReported`
6. `GpuAllocationTracker_RegisterAndRetireBufferUpdatesTotals`
7. `GpuAllocationTracker_RegisterAndRetireImageUpdatesTotals`
8. `GpuAllocationTracker_DoubleRetireIsIgnored`
9. `ImageByteEstimator_HandlesRgba8Rgba16DepthAndMips`
10. `UploadBudgetTracker_AggregatesByCategory`
11. `UploadBudgetTracker_OverBudgetSetsStatus`
12. `RuntimeStallTracker_RecordsWorstStall`
13. `RuntimeStallTracker_RingBufferKeepsRecentEvents`
14. `PerformanceSampleWindow_ComputesAverageMinMaxP95`
15. `RendererDiagnostics_EmptyInitializesPerformanceFields`
16. `SampleStressSceneBuilder_DeterministicCounts`
17. `SampleReflectionTestSpheres_PerfFixtureCreatesExpectedObjectCount`

Avoid GPU-dependent assertions in unit tests. GPU timings, memory heap budgets, and actual frame times belong in manual validation or future hardware-specific smoke tests.

## Manual Validation Checklist

Baseline:

1. `dotnet build Njulf.sln`
2. `dotnet test Njulf.sln`
3. Run `NjulfHelloGame`.
4. Confirm budget line prints after diagnostics warm up.
5. Confirm active budget profile appears in output.
6. Confirm tracked memory categories sum to total.
7. Confirm upload budget line reports scene/material/light upload bytes.

Stress:

1. Run many lights scenario.
2. Run many materials scenario.
3. Run many transparent objects scenario.
4. Run large texture set scenario.
5. Run large meshlet count scenario.
6. Run reflection-heavy scenario with `SampleReflectionTestSpheres`.
7. Run upload burst scenario.
8. Run combined worst case.

Stall validation:

1. Trigger texture or mesh upload burst.
2. Confirm upload bytes and stall diagnostics increase.
3. Resize the window.
4. Confirm swapchain recreation time is reported.
5. Confirm normal steady-state frames do not report `DeviceWaitIdle`.

GPU validation:

1. Enable GPU timings.
2. Wait several frames for query latency.
3. Confirm GPU timings become valid.
4. Disable GPU timings.
5. Confirm query output changes to disabled.
6. Capture RenderDoc frame and verify pass labels.

Snapshot:

1. Export performance snapshot.
2. Verify JSON file is written.
3. Verify it contains active profile, device name, settings, memory categories, timings, uploads, and stalls.

## Performance Overhead Budget

The budget system itself must be cheap.

Targets:

1. Memory tracking overhead when enabled: less than 0.1 ms CPU per frame in steady state.
2. Budget evaluation and sample windows: less than 0.05 ms CPU per frame.
3. Diagnostics snapshot creation: less than 0.1 ms every 180-frame print interval.
4. GPU timing enabled: acceptable query writes, no CPU waits.
5. Tracking disabled: near-zero per-frame overhead.

Implementation rules:

1. Avoid per-frame LINQ in hot paths.
2. Avoid per-frame dictionary allocations.
3. Aggregate into reusable structs/classes.
4. Keep detailed strings in reporter/export paths, not in core frame tracking.
5. Use `Array.Empty<T>()` and fixed small buffers where possible.

## Failure Handling

1. Memory tracking missing category:
   - count under `Unknown`.
   - warn in diagnostics.
2. Upload budget exceeded:
   - report category and size.
   - do not drop required uploads in this phase.
3. Staging overflow:
   - increment overflow count before throwing.
   - include requested size, offset, buffer size, and active budget in exception.
4. GPU timing unsupported:
   - set timing status to unavailable.
   - keep CPU budgets active.
5. Snapshot export failure:
   - report error path/message.
   - do not crash renderer.
6. Stress scene allocation failure:
   - report scenario and counts.
   - fail gracefully where possible.
7. VMA heap stats unavailable:
   - continue with renderer-owned estimates.

## Documentation Requirements

Add budget documentation either in this plan after implementation or in a future `Docs` file:

1. Active profiles and their default values.
2. Meaning of each memory category.
3. Meaning of CPU vs GPU timing fields.
4. How upload budget is calculated.
5. How to run stress scenarios.
6. How to export and compare snapshots.
7. Known limitations:
   - estimated image bytes may differ from driver allocation.
   - swapchain memory is estimated unless queried.
   - GPU timings are delayed by frame latency.
   - present/acquire timings include OS and vsync behavior.

## Implementation Order

1. Add budget profiles, settings, statuses, and evaluator tests.
2. Add memory categories and allocation tracker.
3. Wire memory tracking into buffers, textures, render targets, shadows, environment, and reflections.
4. Add upload budget tracker and staging high-water diagnostics.
5. Add CPU wait/stall tracking.
6. Complete or reuse GPU pass timings.
7. Add rolling sample windows and compact sample output.
8. Add deterministic stress scenes.
9. Add snapshot export.
10. Run validation and update this plan with final notes if implementation uncovers differences.

This order gives useful memory and upload telemetry before adding the broader stress-scene workflow.

## Final Acceptance Criteria

Phase 19 is complete when:

1. Target platform profiles and budgets are defined in code and reported at runtime.
2. `RendererDiagnostics` reports budget status for CPU frame, GPU frame, GPU memory, and upload bytes.
3. GPU memory is tracked by category without double counting known allocations.
4. Render target, texture, mesh, material, light, shadow, environment, reflection, staging, and swapchain memory are visible.
5. Per-pass GPU timings are available without synchronous waits.
6. CPU timing reports renderer work separately from fence/acquire/present stalls.
7. Upload budget pressure and staging high-water marks are visible.
8. Runtime stalls, including `WaitIdle`, are counted and categorized.
9. Stress scenes exist for many lights, many materials, many transparent objects, large texture sets, large meshlet counts, reflection-heavy scenes, upload bursts, and combined worst case.
10. `SampleReflectionTestSpheres` is used as a deterministic fixture for reflective material and reflection-heavy budget validation.
11. Performance snapshots can be exported for before/after comparison.
12. `dotnet build Njulf.sln` and `dotnet test Njulf.sln` pass.
13. Validation layers remain clean during normal rendering, stress scenes, resize, snapshot export, and shutdown.
14. Budget telemetry can be disabled or reduced for shipping builds.

