using Njulf.Assets;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Diagnostics;

internal readonly record struct RendererDiagnosticsAssemblyResult(
    RendererDiagnostics Diagnostics,
    RenderBudgetSnapshot Budget);

internal readonly record struct RendererDiagnosticsAssemblyInput(
    SceneRenderingData SceneData,
    RenderSettings Settings,
    RendererDiagnosticsResourceInput Resources,
    RendererDiagnosticsExecutionInput Execution,
    RendererDiagnosticsGiInput GlobalIllumination,
    RendererDiagnosticsToolingInput Tooling,
    RendererDiagnosticsCaptureInput Capture,
    RendererDiagnosticsFrameInput Frame);

internal readonly record struct RendererDiagnosticsResourceInput(
    VulkanContext Context,
    SwapchainManager Swapchain,
    SynchronizationManager Synchronization,
    TextureManager TextureManager,
    MeshManager MeshManager,
    MaterialManager MaterialManager,
    LightManager LightManager,
    RenderGraph RenderGraph,
    StagingRing StagingRing,
    IModelRenderUploadService ModelUploadService,
    RenderTargetManager? RenderTargets,
    DirectionalShadowResources? DirectionalShadowResources,
    SpotShadowAtlas? SpotShadowAtlas,
    PointShadowCubemapArray? PointShadowCubemapArray,
    EnvironmentManager? EnvironmentManager,
    IesPhotometricProfileManager IesPhotometricProfileManager,
    ReflectionProbeManager? ReflectionProbeManager,
    ForwardPlusPass? ForwardPlusPass,
    MeshPipeline MeshPipeline,
    DynamicResolutionScaleController DynamicResolutionScaleController);

internal readonly record struct RendererDiagnosticsExecutionInput(
    AsyncComputeDiagnosticsSnapshot AsyncCompute,
    UploadBudgetSnapshot UploadBudget,
    MemoryBudgetSnapshot MemoryBudget,
    RuntimeStallSnapshot RuntimeStalls,
    string GpuTimingReason);

internal readonly record struct RendererDiagnosticsGiInput(
    SimpleDdgiVolumeManager? SimpleDdgiVolumeManager,
    FarFieldClipmapManager? FarFieldClipmapManager,
    AccelerationStructureManager? AccelerationStructureManager,
    GiPipelineCacheService? PipelineCacheService,
    FarFieldMaterialV2Counters CompletedFarFieldMaterialV2Counters,
    MaterialGiGpuCounters CompletedMaterialGiCounters,
    ThinSurfaceTransportCounters CompletedThinSurfaceTransportCounters,
    DdgiGeometryParticipationGpuCounters
        CompletedGeometryParticipationCounters,
    DdgiAreaLightGpuCounters CompletedAreaLightCounters,
    DdgiContentRuntimeSnapshot ContentRuntime,
    DdgiRuntimeSnapshot Runtime,
    SimpleDdgiNearFieldResidualDiagnostics NearFieldResidual,
    GlobalIlluminationMode EffectiveMode);

internal readonly record struct RendererDiagnosticsToolingInput(
    bool DebugDrawEnabled,
    ScreenshotCaptureService ScreenshotCaptureService,
    RenderDocCaptureService RenderDocCaptureService,
    GpuTimestampRecorder GpuTimestamps);

internal readonly record struct RendererDiagnosticsCaptureInput(
    PerformanceCaptureMetadataProvider MetadataProvider);

internal readonly record struct RendererDiagnosticsFrameInput(
    long AcquireImageMicroseconds,
    long QueueSubmitMicroseconds,
    long PresentMicroseconds,
    string LastRenderTargetRecreateReason);
