using System.Runtime.InteropServices;
using Njulf.Assets;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Diagnostics;

internal sealed class RendererDiagnosticsAssembler
{
    private const long LocalShadowGpuCompactionRecordThresholdMicroseconds =
        750;
    private const int LocalShadowGpuCompactionWorkThreshold = 8192;

    private readonly RenderBudgetEvaluator _budgetEvaluator = new();
    private readonly GiWarningEvaluator _giWarningEvaluator = new();
    private readonly DdgiDiagnosticWarningTracker
        _ddgiDiagnosticWarningTracker = new();

    internal void ResetSceneHistory()
    {
        _giWarningEvaluator.Reset();
    }

    internal RendererDiagnostics ApplyAsyncSubmission(
        RendererDiagnostics current,
        in AsyncComputeSubmissionPatch patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            AsyncComputeSubmittedGraphicsSegmentCount =
                patch.SubmittedGraphicsSegmentCount,
            AsyncComputeSubmittedComputeSegmentCount =
                patch.SubmittedComputeSegmentCount
        };
    }

    internal RendererDiagnostics ApplyValidationMessages(
        RendererDiagnostics current,
        in RendererValidationMessageSnapshot validation)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            ValidationVerboseMessageCount = validation.VerboseCount,
            ValidationInfoMessageCount = validation.InformationCount,
            ValidationWarningMessageCount = validation.WarningCount,
            ValidationErrorMessageCount = validation.ErrorCount,
            ValidationFirstWarningMessage =
                validation.FirstWarningMessage,
            ValidationLastWarningMessage =
                validation.LastWarningMessage,
            ValidationFirstErrorMessage = validation.FirstErrorMessage,
            ValidationLastErrorMessage = validation.LastErrorMessage
        };
    }

    internal RendererDiagnosticsAssemblyResult Assemble(
        in RendererDiagnosticsAssemblyInput input)
    {
        ArgumentNullException.ThrowIfNull(input.SceneData);
        ArgumentNullException.ThrowIfNull(input.Settings);

        SceneRenderingData sceneData = input.SceneData;
        RenderSettings Settings = input.Settings;
        var _context = input.Resources.Context;
        var _swapchain = input.Resources.Swapchain;
        var _sync = input.Resources.Synchronization;
        var _textureManager = input.Resources.TextureManager;
        var _meshManager = input.Resources.MeshManager;
        var _materialManager = input.Resources.MaterialManager;
        var _lightManager = input.Resources.LightManager;
        var _renderGraph = input.Resources.RenderGraph;
        var _stagingRing = input.Resources.StagingRing;
        var _modelUploadService = input.Resources.ModelUploadService;
        var _renderTargets = input.Resources.RenderTargets;
        var _directionalShadowResources =
            input.Resources.DirectionalShadowResources;
        var _spotShadowAtlas = input.Resources.SpotShadowAtlas;
        var _pointShadowCubemapArray =
            input.Resources.PointShadowCubemapArray;
        var _environmentManager = input.Resources.EnvironmentManager;
        var _iesPhotometricProfileManager =
            input.Resources.IesPhotometricProfileManager;
        var _reflectionProbeManager =
            input.Resources.ReflectionProbeManager;
        var _forwardPlusPass = input.Resources.ForwardPlusPass;
        var _meshPipeline = input.Resources.MeshPipeline;
        var _meshletPhysicalResidencyResources =
            input.Resources.MeshletPhysicalResidencyResources;
        var _dynamicResolutionScaleController =
            input.Resources.DynamicResolutionScaleController;
        var _simpleDdgiVolumeManager =
            input.GlobalIllumination.SimpleDdgiVolumeManager;
        var _farFieldClipmapManager =
            input.GlobalIllumination.FarFieldClipmapManager;
        var _accelerationStructureManager =
            input.GlobalIllumination.AccelerationStructureManager;
        var _giPipelineCacheService =
            input.GlobalIllumination.PipelineCacheService;
        FarFieldMaterialV2Counters
            _completedFarFieldMaterialV2Counters =
                input.GlobalIllumination
                    .CompletedFarFieldMaterialV2Counters;
        MaterialGiGpuCounters _completedMaterialGiCounters =
            input.GlobalIllumination.CompletedMaterialGiCounters;
        ThinSurfaceTransportCounters
            _completedThinSurfaceTransportCounters =
                input.GlobalIllumination
                    .CompletedThinSurfaceTransportCounters;
        DdgiGeometryParticipationGpuCounters
            _completedDdgiGeometryParticipationCounters =
                input.GlobalIllumination
                    .CompletedGeometryParticipationCounters;
        DdgiAreaLightGpuCounters _completedDdgiAreaLightCounters =
            input.GlobalIllumination.CompletedAreaLightCounters;
        var _screenshotCaptureService =
            input.Tooling.ScreenshotCaptureService;
        var _renderDocCaptureService =
            input.Tooling.RenderDocCaptureService;
        var _gpuTimestamps = input.Tooling.GpuTimestamps;
        var _performanceCaptureMetadataProvider =
            input.Capture.MetadataProvider;
        long _lastAcquireImageMicroseconds =
            input.Frame.AcquireImageMicroseconds;
        long _lastSwapchainImageOwnerWaitMicroseconds =
            input.Frame.SwapchainImageOwnerWaitMicroseconds;
        long _lastFrameResourceRecycleWaitMicroseconds =
            input.Frame.FrameResourceRecycleWaitMicroseconds;
        long _lastQueueSubmitMicroseconds =
            input.Frame.QueueSubmitMicroseconds;
        long _lastPresentMicroseconds =
            input.Frame.PresentMicroseconds;
        double _maximumFramesPerSecond =
            input.Frame.MaximumFramesPerSecond;
        long _framePacingWaitMicroseconds =
            input.Frame.FramePacingWaitMicroseconds;
        string _lastRenderTargetRecreateReason =
            input.Frame.LastRenderTargetRecreateReason;
        bool MeshletDiagnosticCountersActive =
            _meshPipeline.GpuMeshletCountersEnabled;
        RenderBudgetSnapshot _lastBudgetSnapshot =
            RenderBudgetSnapshot.Empty;

        ModelRenderUploadDiagnostics uploadDiagnostics = _modelUploadService.LastUploadDiagnostics;
        bool gpuMeshletCountersEnabled = MeshletDiagnosticCountersActive;
        int submittedOpaqueMeshlets = sceneData.ForwardTaskInvocations > 0
            ? sceneData.ForwardTaskInvocations
            : sceneData.OpaqueMeshletCount;
        int forwardCandidates = sceneData.ForwardTaskInvocations > 0
            ? sceneData.ForwardTaskInvocations
            : sceneData.OpaqueMeshletCount;
        int forwardVisibleAfterOcclusion = sceneData.ForwardTaskInvocations > 0
            ? sceneData.ForwardEmittedMeshletsGpu
            : Math.Max(0, forwardCandidates - sceneData.ForwardFrustumCulledMeshletsGpu - sceneData.ForwardOcclusionCulledMeshletsGpu);
        int forwardOcclusionRejected = sceneData.ForwardOcclusionCulledMeshletsGpu;
        bool forwardOcclusionCountersReconciled = !gpuMeshletCountersEnabled || ForwardOcclusionCountersReconcile(sceneData);
        string forwardOcclusionSanity = BuildForwardOcclusionSanity(sceneData, gpuMeshletCountersEnabled, forwardOcclusionCountersReconciled);
        string gpuMeshletCountersStatus = gpuMeshletCountersEnabled
            ? "GPU meshlet counters enabled."
            : "GPU meshlet counters disabled.";
        SceneSubmissionMode sceneSubmissionActiveMode = SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData);
        int spotShadowMeshletLightTests = CalculateSpotShadowMeshletLightTests(sceneData);
        int pointShadowMeshletFaceTests = CalculatePointShadowMeshletFaceTests(sceneData);
        bool spotShadowGpuCompactionJustified = IsSpotShadowGpuCompactionJustified(sceneData, spotShadowMeshletLightTests);
        bool pointShadowGpuCompactionJustified = IsPointShadowGpuCompactionJustified(sceneData, pointShadowMeshletFaceTests);
        ProductionRenderPipelineDeclaration productionPipeline = ProductionRenderPipelineDeclaration.Instance;
        AsyncComputeDiagnosticsSnapshot asyncComputePlan =
            input.Execution.AsyncCompute;
        DeviceRequirementReport? captureDevice = _context.SelectedDeviceRequirementReport;
        GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
        bool giRayQuerySupported = _context.RayQuerySupported && _accelerationStructureManager?.Supported == true;
        bool giAccelerationStructuresActive = _accelerationStructureManager?.Active == true;
        GlobalIlluminationMode effectiveGiMode =
            input.GlobalIllumination.EffectiveMode;
        // Preserve authored intent separately from the live path.  The emergency switch and
        // capability fallbacks must never make a capture look as though a feature was simply
        // not requested.
        bool giRequested = giSettings.Enabled && giSettings.Mode != GlobalIlluminationMode.Disabled;
        bool ddgiRequested = giRequested && giSettings.UseDdgi;
        bool simpleDdgiRequested = ddgiRequested;
        bool rayQueryGiRequested = ddgiRequested && giSettings.UseRayQueryBackend;
        string globalIlluminationFallbackReason = giSettings.EmergencyGiFallbackEnabled && giRequested
            ? "Emergency GI fallback is enabled; dynamic GI paths are intentionally suppressed."
            : string.Empty;
        // The emergency switch is a live rollback control: it must make the
        // renderer report GI as inactive immediately while leaving stable
        // environment/reflection lighting intact. The authored settings stay
        // untouched so clearing the switch restores the prior configuration.
        bool giEnabled = !giSettings.EmergencyGiFallbackEnabled &&
            giSettings.Enabled && effectiveGiMode != GlobalIlluminationMode.Disabled;
        GlobalIlluminationDebugView effectiveGiDebugView = giEnabled
            ? RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                giSettings.DebugView)
            : GlobalIlluminationDebugView.None;
        bool requestedGiDebugViewAvailable =
            RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(
                giSettings.DebugView);
        bool giUsesSimpleDdgi = giSettings.EffectiveUseDdgi;
        bool giUsesDdgi = giUsesSimpleDdgi;
        bool ddgiAsyncComputeActuallyEnabled =
            giUsesDdgi && asyncComputePlan.DdgiActuallyEnabled;
        IReadOnlyList<string> activeProductionPipelinePasses = productionPipeline.GetActivePasses(
            sceneData.ActiveFeatureIsolation,
            sceneData.TransparencyMode);
        bool giRayQueryActive = giEnabled &&
                                giSettings.EffectiveUseRayQueryBackend &&
                                giRayQuerySupported &&
                                giAccelerationStructuresActive;
        string localShadowGpuCompactionStatus = BuildLocalShadowGpuCompactionStatus(
            sceneData,
            spotShadowMeshletLightTests,
            pointShadowMeshletFaceTests,
            spotShadowGpuCompactionJustified,
            pointShadowGpuCompactionJustified);
        string localShadowOverflowSummary = BuildLocalShadowOverflowSummary(
            spotShadowGpuCompactionJustified,
            pointShadowGpuCompactionJustified);
        DdgiContentRuntimeSnapshot contentDependentDdgi =
            input.GlobalIllumination.ContentRuntime;
        DdgiRuntimeSnapshot ddgiRuntimeSnapshot = giUsesDdgi
            ? input.GlobalIllumination.Runtime with
            {
                ContentDependent = contentDependentDdgi
            }
            : DdgiRuntimeSnapshot.Empty with
            {
                ContentDependent = contentDependentDdgi
            };
        IReadOnlyList<string> ddgiDiagnosticWarnings = _ddgiDiagnosticWarningTracker.Update(
            ddgiRuntimeSnapshot,
            schedulerOverBudget: false);
        GpuCompletionRetirementSnapshot reflectionRetirement = _reflectionProbeManager?.ResourceRetirementSnapshot ??
            default;
        MaterialManagerDiagnostics materialDiagnostics = _materialManager.Diagnostics;
        MeshletStreamingCoordinatorSnapshot? meshletResidency =
            _meshletPhysicalResidencyResources?.Coordinator.CreateSnapshot();
        MeshletPhysicalPageCacheSnapshot? meshletPageCache =
            _meshletPhysicalResidencyResources?.Uploader.CreateSnapshot();
        VulkanMeshletPhysicalResidencySnapshot? meshletVulkanResidency =
            _meshletPhysicalResidencyResources?.CreateSnapshot();
        SceneSubmissionSettings sceneSubmissionSettings =
            Settings.SceneSubmission;
        MeshletStreamingResidencyOptions? residencyOptions =
            _meshletPhysicalResidencyResources?.Coordinator.Options;
        bool meshletResidencyReloadRequired =
            meshletResidency?.Active == true &&
            (residencyOptions is null ||
             !sceneSubmissionSettings.GpuMeshletStreamingEnabled ||
             sceneSubmissionSettings
                 .GpuMeshletStreamingPhysicalPageCount !=
                residencyOptions.PhysicalPageCapacity ||
             checked(sceneSubmissionSettings
                 .GpuMeshletStreamingUploadBudgetMiB * 1024 * 1024) !=
                residencyOptions.MaximumUploadBytesPerTick ||
             sceneSubmissionSettings
                 .GpuMeshletStreamingMaximumRequestsPerFrame !=
                residencyOptions.MaximumRequestsPerSerial ||
             sceneSubmissionSettings.GpuMeshletStreamingConcurrentReads !=
                residencyOptions.MaximumConcurrentReads);
        long residencyResolvedRequests = meshletResidency is null
            ? 0
            : meshletResidency.ResidentHitCount +
              meshletResidency.FallbackHitCount;
        float meshletResidencyHitRate = residencyResolvedRequests <= 0
            ? 0.0f
            : meshletResidency!.ResidentHitCount /
              (float)residencyResolvedRequests;
        float meshletResidencyFallbackRate = residencyResolvedRequests <= 0
            ? 0.0f
            : meshletResidency!.FallbackHitCount /
              (float)residencyResolvedRequests;
        string meshletResidencyFallbackSummary = meshletResidency is null
            ? string.Empty
            : string.Join(
                ", ",
                meshletResidency.FallbackReasons
                    .OrderByDescending(static pair => pair.Value)
                    .ThenBy(static pair => pair.Key,
                        StringComparer.Ordinal)
                    .Select(static pair => $"{pair.Key}={pair.Value}"));
        string meshletResidencyLatestFailure =
            meshletResidency?.LastFailure?.Detail ??
            meshletVulkanResidency?.LatestFailure ??
            string.Empty;
        MaterialGiRolloutEvaluation materialGiRollout =
            giSettings.EvaluateMaterialGiRollout();
        GiPipelineCacheTelemetry giPipelineCacheTelemetry =
            _giPipelineCacheService?.Telemetry ??
            GiPipelineCacheTelemetry.Empty;
        RendererValidationMessageSnapshot validationMessages = _context.ValidationMessageSnapshot;
        RendererDiagnostics diagnostics = new RendererDiagnostics(
            sceneData.ObjectCount,
            sceneData.MeshletCount,
            sceneData.OpaqueObjectCount,
            sceneData.MaskedObjectCount,
            sceneData.TransparentObjectCount,
            sceneData.OpaqueMeshletCount,
            sceneData.TransparentMeshletCount,
            submittedOpaqueMeshlets,
            sceneData.ForwardFrustumCulledMeshletsGpu,
            sceneData.ForwardOcclusionCulledMeshletsGpu,
            forwardCandidates,
            forwardVisibleAfterOcclusion,
            sceneData.BlendMaterialCount,
            sceneData.UploadedBytes,
            sceneData.LightCount,
            sceneData.TileCountX,
            sceneData.TileCountY,
            sceneData.MaterialCount,
            _textureManager.TextureCount,
            _textureManager.LoadedFileTextureCount,
            _textureManager.MipmapFallbackCount,
            _textureManager.DownscaledTextureCount,
            _textureManager.MaxLoadedTextureDimension,
            _textureManager.EstimatedTextureBytes,
            uploadDiagnostics.ModelName,
            uploadDiagnostics.RenderObjectCount,
            uploadDiagnostics.RegisteredMeshCount,
            uploadDiagnostics.LoadedMaterialCount,
            uploadDiagnostics.LoadedTextureCount,
            uploadDiagnostics.DefaultWhiteSubstitutions,
            uploadDiagnostics.DefaultNormalSubstitutions,
            uploadDiagnostics.DefaultBlackSubstitutions,
            sceneData.CpuSceneBuildMicroseconds,
            sceneData.GpuDepthPrePassMicroseconds,
            sceneData.GpuHiZBuildMicroseconds,
            sceneData.GpuForwardOpaqueMicroseconds,
            sceneData.GpuTransparentMicroseconds,
            sceneData.SceneUploadCount,
            sceneData.SceneUploadSkipped,
            sceneData.ObjectCandidatesCpu,
            sceneData.ObjectFrustumCulledCpu,
            sceneData.MeshletCandidatesCpu,
            sceneData.MeshletFrustumCulledCpu,
            sceneData.MeshletLodSkippedCpu,
            sceneData.MeshletLod0SubmittedCpu,
            sceneData.MeshletLod1SubmittedCpu,
            sceneData.MeshletLod2SubmittedCpu,
            sceneData.CpuPayloadSignatureMicroseconds,
            sceneData.CpuObjectCullMicroseconds,
            sceneData.CpuMeshletCullMicroseconds,
            sceneData.CpuUploadMicroseconds,
            sceneData.CpuMaterialUploadMicroseconds,
            sceneData.CpuTotalDrawSceneMicroseconds,
            sceneData.CpuDirectionalShadowRecordMicroseconds,
            sceneData.CpuSpotShadowRecordMicroseconds,
            sceneData.CpuPointShadowRecordMicroseconds,
            sceneData.CpuDepthPrePassRecordMicroseconds,
            sceneData.CpuHiZBuildRecordMicroseconds,
            sceneData.CpuLightCullRecordMicroseconds,
            sceneData.CpuForwardOpaqueRecordMicroseconds,
            sceneData.CpuTransparentRecordMicroseconds,
            sceneData.CpuBloomExtractRecordMicroseconds,
            sceneData.CpuBloomDownsampleRecordMicroseconds,
            sceneData.CpuBloomUpsampleRecordMicroseconds,
            sceneData.CpuFogRecordMicroseconds,
            sceneData.CpuCompositeRecordMicroseconds,
            sceneData.GpuLightCullMicroseconds,
            sceneData.DepthTaskInvocations,
            sceneData.DepthFrustumCulledMeshletsGpu,
            sceneData.DepthEmittedMeshletsGpu,
            sceneData.ForwardTaskInvocations,
            sceneData.ForwardFrustumCulledMeshletsGpu,
            sceneData.ForwardOcclusionTestedMeshletsGpu,
            sceneData.ForwardEmittedMeshletsGpu,
            sceneData.MeshletCountTotal,
            sceneData.MeshletCountSubmittedCpu,
            sceneData.AvgTrianglesPerSubmittedMeshlet,
            sceneData.AvgVerticesPerSubmittedMeshlet,
            sceneData.SmallMeshletsUnder16Triangles,
            sceneData.SmallMeshletsUnder32Triangles,
            sceneData.ScenePayloadRebuilt,
            sceneData.ObjectUploadBytes,
            sceneData.InstanceUploadBytes,
            sceneData.MeshletDrawUploadBytes,
            sceneData.TransparentMeshletDrawUploadBytes,
            sceneData.MaterialUploadBytes,
            sceneData.LightUploadBytes,
            sceneData.DepthPrePassEnabled ? 1 : 0,
            sceneData.HiZBuildEnabled ? 1 : 0,
            sceneData.OcclusionCullingEnabled ? 1 : 0,
            sceneData.HiZMipCount,
            sceneData.HiZWidth,
            sceneData.HiZHeight,
            sceneData.DirectionalShadowPassEnabled ? 1 : 0,
            sceneData.DirectionalShadowMapSize,
            sceneData.DirectionalShadowCascadeCount,
            sceneData.ShadowedDirectionalLightIndex,
            sceneData.ShadowDebugView,
            sceneData.ShadowNormalBias,
            sceneData.ShadowSlopeScaledDepthBias,
            sceneData.DirectionalShadowPcfRadius,
            sceneData.SpotShadowPcfRadius,
            sceneData.PointShadowPcfRadius,
            sceneData.ForwardShadowReceiverMeshletCapacity,
            sceneData.SpotShadowsEnabled ? 1 : 0,
            sceneData.SpotShadowCandidateCount,
            sceneData.SpotShadowSelectedCount,
            sceneData.SpotShadowRejectedByBudgetCount,
            sceneData.SpotShadowAtlasSize,
            sceneData.SpotShadowTileSize,
            sceneData.SpotShadowAtlasCapacity,
            sceneData.SpotShadowAtlasUsedTiles,
            sceneData.PointShadowsEnabled ? 1 : 0,
            sceneData.PointShadowCandidateCount,
            sceneData.PointShadowSelectedCount,
            sceneData.PointShadowRejectedByBudgetCount,
            sceneData.PointShadowMapSize,
            sceneData.PointShadowRenderedFaceCount,
            HdrEnabled: 1,
            SceneColorFormat: RenderTargetManager.SceneColorFormat.ToString(),
            Exposure: sceneData.EffectiveExposure,
            ToneMapper: Settings.ToneMapper,
            BloomEnabled: sceneData.BloomEnabled ? 1 : 0,
            BloomMipCount: sceneData.BloomMipCount,
            BloomBaseWidth: sceneData.BloomBaseWidth,
            BloomBaseHeight: sceneData.BloomBaseHeight,
            BloomFormat: RenderTargetManager.SceneColorFormat.ToString(),
            BloomIntensity: Settings.Bloom.Intensity,
            BloomThreshold: Settings.Bloom.Threshold,
            BloomKnee: Settings.Bloom.Knee,
            BloomRadius: Settings.Bloom.Radius,
            BloomDebugView: Settings.Bloom.DebugView,
            BloomDebugMipLevel: Settings.Bloom.DebugMipLevel,
            FogEnabled: sceneData.FogEnabled ? 1 : 0,
            FogMode: sceneData.FogMode,
            FogColorMode: sceneData.FogColorMode,
            FogDebugView: sceneData.FogDebugView,
            FogDensity: sceneData.FogDensity,
            FogStartDistance: sceneData.FogStartDistance,
            FogEndDistance: sceneData.FogEndDistance,
            FogHeight: sceneData.FogHeight,
            FogHeightFalloff: sceneData.FogHeightFalloff,
            FogHeightDensity: sceneData.FogHeightDensity,
            FogMaxOpacity: sceneData.FogMaxOpacity,
            FogDirectionalInscatteringEnabled: sceneData.FogDirectionalInscatteringEnabled,
            FogWidth: sceneData.FogWidth,
            FogHeightPixels: sceneData.FogHeightPixels,
            FogFormat: sceneData.FogFormat,
            GpuFogMicroseconds: sceneData.GpuFogMicroseconds,
            AmbientOcclusionEnabled: sceneData.AmbientOcclusionEnabled ? 1 : 0,
            RequestedAmbientOcclusionMode:
                sceneData.RequestedAmbientOcclusionMode,
            AmbientOcclusionMode: sceneData.AmbientOcclusionMode,
            GtaoRuntimeSupported: sceneData.GtaoRuntimeSupported ? 1 : 0,
            AmbientOcclusionDebugView: sceneData.AmbientOcclusionDebugView,
            AmbientOcclusionBentNormalMode:
                sceneData.AmbientOcclusionBentNormalMode,
            GtaoQualityPreset: sceneData.GtaoQualityPreset,
            GtaoHistoryValid: sceneData.GtaoHistoryValid,
            AmbientOcclusionForwardSamplingMode: sceneData.AmbientOcclusionForwardSamplingMode,
            AmbientOcclusionForwardDepthAwareSamples: sceneData.AmbientOcclusionForwardDepthAwareSamples,
            AmbientOcclusionWidth: sceneData.AmbientOcclusionWidth,
            AmbientOcclusionHeight: sceneData.AmbientOcclusionHeight,
            AmbientOcclusionFormat: sceneData.AmbientOcclusionFormat,
            AmbientOcclusionResolutionScale: sceneData.AmbientOcclusionResolutionScale,
            AmbientOcclusionRadius: sceneData.AmbientOcclusionRadius,
            AmbientOcclusionIntensity: sceneData.AmbientOcclusionIntensity,
            AmbientOcclusionBias: sceneData.AmbientOcclusionBias,
            AmbientOcclusionSampleCount: sceneData.AmbientOcclusionSampleCount,
            AmbientOcclusionBlurRadius: sceneData.AmbientOcclusionBlurRadius,
            CpuAmbientOcclusionRecordMicroseconds: sceneData.CpuAmbientOcclusionRecordMicroseconds,
            CpuAmbientOcclusionBlurRecordMicroseconds: sceneData.CpuAmbientOcclusionBlurRecordMicroseconds,
            GpuAmbientOcclusionMicroseconds: sceneData.GpuAmbientOcclusionMicroseconds,
            GpuAmbientOcclusionBlurMicroseconds: sceneData.GpuAmbientOcclusionBlurMicroseconds,
            AntiAliasingMode: sceneData.AntiAliasingMode,
            AntiAliasingDebugView: sceneData.AntiAliasingDebugView,
            AntiAliasingWidth: sceneData.AntiAliasingWidth,
            AntiAliasingHeight: sceneData.AntiAliasingHeight,
            AntiAliasingInputFormat: sceneData.AntiAliasingInputFormat,
            AntiAliasingOutputFormat: sceneData.AntiAliasingOutputFormat,
            CpuFxaaRecordMicroseconds: sceneData.CpuFxaaRecordMicroseconds,
            CpuSmaaEdgeRecordMicroseconds: sceneData.CpuSmaaEdgeRecordMicroseconds,
            CpuSmaaBlendRecordMicroseconds: sceneData.CpuSmaaBlendRecordMicroseconds,
            CpuSmaaNeighborhoodRecordMicroseconds: sceneData.CpuSmaaNeighborhoodRecordMicroseconds,
            GpuAntiAliasingMicroseconds: sceneData.GpuAntiAliasingMicroseconds,
            SmaaLookupTexturesReady: sceneData.SmaaLookupTexturesReady,
            MotionVectorsEnabled: sceneData.MotionVectorsEnabled,
            JitterEnabled: sceneData.JitterEnabled,
            JitterX: sceneData.JitterX,
            JitterY: sceneData.JitterY,
            EnvironmentEnabled: Settings.Environment.Enabled ? 1 : 0,
            EnvironmentSourceKind: Settings.Environment.SourceKind,
            EnvironmentSourcePath: Settings.Environment.SourcePath ?? string.Empty,
            EnvironmentUsesFallback: _environmentManager?.UsesFallback == true ? 1 : 0,
            EnvironmentCubemapSize: _environmentManager?.EnvironmentSize ?? 0,
            IrradianceCubemapSize: _environmentManager?.IrradianceSize ?? 0,
            PrefilteredEnvironmentSize: _environmentManager?.PrefilteredSize ?? 0,
            PrefilteredEnvironmentMipCount: _environmentManager?.PrefilteredMipCount ?? 0,
            BrdfLutSize: _environmentManager?.BrdfLutSize ?? 0,
            SkyIntensity: Settings.Environment.SkyIntensity,
            DiffuseIblIntensity: Settings.Environment.DiffuseIntensity,
            SpecularIblIntensity: Settings.Environment.SpecularIntensity,
            EnvironmentDebugView: Settings.Environment.DebugView,
            EnvironmentDebugMipLevel: Settings.Environment.DebugMipLevel,
            EnvironmentTextureBytes: _environmentManager?.EstimatedBytes ?? 0,
            ReflectionsEnabled: sceneData.ReflectionsEnabled ? 1 : 0,
            ReflectionMode: sceneData.ReflectionMode,
            ReflectionDebugView: sceneData.ReflectionDebugView,
            ReflectionProbeCount: sceneData.ReflectionProbeCount,
            ReflectionProbeCapacity: sceneData.ReflectionProbeCapacity,
            MaxReflectionProbesPerPixel: sceneData.MaxReflectionProbesPerPixel,
            ReflectionProbeResolution: sceneData.ReflectionProbeResolution,
            ReflectionProbeMipCount: sceneData.ReflectionProbeMipCount,
            ReflectionProbeEstimatedBytes: sceneData.ReflectionProbeEstimatedBytes,
            ReflectionProbeCapturesQueued: sceneData.ReflectionProbeCapturesQueued,
            ReflectionProbeCapturesCompleted: sceneData.ReflectionProbeCapturesCompleted,
            CpuReflectionProbeUploadMicroseconds: sceneData.CpuReflectionProbeUploadMicroseconds,
            CpuReflectionProbeCaptureRecordMicroseconds: sceneData.CpuReflectionProbeCaptureRecordMicroseconds,
            CpuReflectionProbePrefilterRecordMicroseconds: sceneData.CpuReflectionProbePrefilterRecordMicroseconds,
            GpuReflectionProbeCaptureMicroseconds: sceneData.GpuReflectionProbeCaptureMicroseconds,
            GpuReflectionProbePrefilterMicroseconds: sceneData.GpuReflectionProbePrefilterMicroseconds,
            GpuReflectionProbePublishMicroseconds: sceneData.GpuReflectionProbePublishMicroseconds)
        {
            DirectionalLightCount = sceneData.DirectionalLightCount,
            LocalLightCount = sceneData.LocalLightCount,
            PointLightCount = sceneData.PointLightCount,
            SpotLightCount = sceneData.SpotLightCount,
            RectangleLightCount = sceneData.RectangleLightCount,
            DiskLightCount = sceneData.DiskLightCount,
            TubeLightCount = sceneData.TubeLightCount,
            AreaLightCount = sceneData.AreaLightCount,
            AreaLightLtcTablesAvailable =
                _environmentManager?.LtcLookupTablesAvailable == true ? 1 : 0,
            AreaLightLtcTableBytes =
                _environmentManager?.LtcLookupTableBytes ?? 0UL,
            AreaRayShadowPassEnabled =
                sceneData.AreaRayShadowPassEnabled ? 1 : 0,
            AreaShadowCandidateCount = sceneData.AreaShadowCandidateCount,
            AreaShadowSelectedCount = sceneData.AreaShadowSelectedCount,
            AreaShadowRejectedByBudgetCount =
                sceneData.AreaShadowRejectedByBudgetCount,
            AreaShadowSampleCount = sceneData.AreaShadowSampleCount,
            AreaShadowMaximumRayDistance =
                sceneData.AreaShadowMaximumRayDistance,
            AreaRayShadowMaskWidth = sceneData.AreaRayShadowMaskWidth,
            AreaRayShadowMaskHeight = sceneData.AreaRayShadowMaskHeight,
            AreaRayShadowMaskBytes = sceneData.AreaRayShadowMaskBytes,
            AreaRayShadowResourceGeneration =
                sceneData.AreaRayShadowResourceGeneration,
            AreaRayShadowFailureDetail =
                sceneData.AreaRayShadowFailureDetail,
            IesPhotometricProfileCount =
                _iesPhotometricProfileManager.ProfileCount,
            IesPhotometricProfileBytes =
                _iesPhotometricProfileManager.EstimatedBytes,
            IesPhotometricProfileLoadSuccessCount =
                _iesPhotometricProfileManager.LoadSuccessCount,
            IesPhotometricProfileLoadFailureCount =
                _iesPhotometricProfileManager.LoadFailureCount,
            IesPhotometricProfileLastFailure =
                _iesPhotometricProfileManager.LastFailure ?? string.Empty,
            DdgiAreaLightSampleAttemptCount = giUsesSimpleDdgi
                ? _completedDdgiAreaLightCounters.SampleAttemptCount
                : 0u,
            DdgiAreaLightSampleAcceptCount = giUsesSimpleDdgi
                ? _completedDdgiAreaLightCounters.SampleAcceptCount
                : 0u,
            DdgiAreaLightInvalidPdfCount = giUsesSimpleDdgi
                ? _completedDdgiAreaLightCounters.InvalidPdfCount
                : 0u,
            DdgiAreaLightVisibilityRayCount = giUsesSimpleDdgi
                ? _completedDdgiAreaLightCounters.VisibilityRayCount
                : 0u,
            RequestedReflectionMode = sceneData.RequestedReflectionMode,
            EffectiveReflectionMode = sceneData.EffectiveReflectionMode,
            RequestedReflectionImplementation =
                sceneData.RequestedReflectionImplementation,
            EffectiveReflectionImplementation =
                sceneData.EffectiveReflectionImplementation,
            ReflectionImplementationFallbackReason =
                sceneData.ReflectionImplementationFallbackReason,
            ReflectionImplementationFallbackDetail =
                sceneData.ReflectionImplementationFallbackDetail,
            ReflectionFallbackReason = sceneData.ReflectionFallbackReason,
            ReflectionFallbackDetail = sceneData.ReflectionFallbackDetail,
            HybridReflectionPassEnabled =
                sceneData.HybridReflectionPassEnabled ? 1 : 0,
            HybridReflectionWidth = sceneData.HybridReflectionWidth,
            HybridReflectionHeight = sceneData.HybridReflectionHeight,
            HybridReflectionRayQueryCapacity =
                sceneData.HybridReflectionRayQueryCapacity,
            HybridReflectionHistoryValid =
                sceneData.HybridReflectionHistoryValid,
            HybridReflectionHistoryResetReason =
                sceneData.HybridReflectionHistoryResetReason,
            HybridReflectionSourceInvalidation =
                sceneData.HybridReflectionSourceInvalidation,
            HybridReflectionEstimatedBytes =
                sceneData.HybridReflectionEstimatedBytes,
            HybridReflectionCountersReadbackValid =
                sceneData.HybridReflectionCountersReadbackValid,
            HybridReflectionSsrHitCount =
                sceneData.HybridReflectionSsrHitCount,
            HybridReflectionRayQueryRequestCount =
                sceneData.HybridReflectionRayQueryRequestCount,
            HybridReflectionRayQueryCount =
                sceneData.HybridReflectionRayQueryCount,
            HybridReflectionRayQueryOverflowCount =
                sceneData.HybridReflectionRayQueryOverflowCount,
            HybridReflectionRayQueryHitCount =
                sceneData.HybridReflectionRayQueryHitCount,
            HybridReflectionRayQueryMissCount =
                sceneData.HybridReflectionRayQueryMissCount,
            HybridReflectionDdgiFallbackCount =
                sceneData.HybridReflectionDdgiFallbackCount,
            HybridReflectionProbeFallbackCount =
                sceneData.HybridReflectionProbeFallbackCount,
            HybridReflectionEnvironmentFallbackCount =
                sceneData.HybridReflectionEnvironmentFallbackCount,
            HybridReflectionFullRateTileCount =
                sceneData.HybridReflectionFullRateTileCount,
            HybridReflectionHalfRateTileCount =
                sceneData.HybridReflectionHalfRateTileCount,
            HybridReflectionQuarterRateTileCount =
                sceneData.HybridReflectionQuarterRateTileCount,
            HybridReflectionAnalyticTileCount =
                sceneData.HybridReflectionAnalyticTileCount,
            HybridReflectionReuseTileCount =
                sceneData.HybridReflectionReuseTileCount,
            HybridReflectionActiveTileCount =
                sceneData.HybridReflectionActiveTileCount,
            HybridReflectionTileOverflowCount =
                sceneData.HybridReflectionTileOverflowCount,
            AutomaticPlanarReflectionActive =
                sceneData.AutomaticPlanarReflectionActive ? 1 : 0,
            AutomaticPlanarCandidateCount =
                sceneData.AutomaticPlanarCandidateCount,
            AutomaticPlanarSelectedCount =
                sceneData.AutomaticPlanarSelectedCount,
            AutomaticPlanarCaptureCount =
                sceneData.AutomaticPlanarCaptureCount,
            AutomaticPlanarReprojectionCount =
                sceneData.AutomaticPlanarReprojectionCount,
            AutomaticPlanarRejectedCount =
                sceneData.AutomaticPlanarRejectedCount,
            AutomaticPlanarRejectionReason =
                sceneData.AutomaticPlanarRejectionReason,
            AutomaticPlanarRejectionDetail =
                sceneData.AutomaticPlanarRejectionDetail,
            AutomaticPlanarCaptureGeneration =
                sceneData.AutomaticPlanarCaptureGeneration,
            AutomaticPlanarEstimatedBytes =
                sceneData.AutomaticPlanarEstimatedBytes,
            AutomaticPlanarResolutionScale =
                sceneData.AutomaticPlanarResolutionScale,
            AutomaticPlanarMaximumCaptureAge =
                sceneData.AutomaticPlanarMaximumCaptureAge,
            GpuAutomaticPlanarCaptureMicroseconds =
                sceneData.GpuAutomaticPlanarCaptureMicroseconds,
            TransparentReflectionReceiverObjectCount =
                sceneData.TransparentReflectionReceiverObjectCount,
            TransparentReflectionReceiverMeshletCount =
                sceneData.TransparentReflectionReceiverMeshletCount,
            TransparentSampleReflections =
                sceneData.TransparentSampleReflections ? 1 : 0,
            OpaqueSceneColorSnapshotAvailable =
                sceneData.OpaqueSceneColorSnapshotAvailable ? 1 : 0,
            TransparentSceneReflectionRayTaskBudget =
                sceneData.TransparentSceneReflectionRayTaskBudget,
            TransparentSceneReflectionSsrSampleBudget =
                sceneData.TransparentSceneReflectionSsrSampleBudget,
            TransparentReflectionRayRequestCount =
                sceneData.TransparentReflectionRayRequestCount,
            TransparentReflectionEstimatedSsrHitCount =
                sceneData.TransparentReflectionEstimatedSsrHitCount,
            TransparentReflectionEstimatedRayHitCount =
                sceneData.TransparentReflectionEstimatedRayHitCount,
            TransparentReflectionEstimatedRayMissCount =
                sceneData.TransparentReflectionEstimatedRayMissCount,
            TransparentReflectionEstimatedBudgetRejectedCount =
                sceneData.TransparentReflectionEstimatedBudgetRejectedCount,
            TransparentReflectionEstimatedDdgiFallbackCount =
                sceneData.TransparentReflectionEstimatedDdgiFallbackCount,
            TransparentReflectionEstimatedProbeFallbackCount =
                sceneData.TransparentReflectionEstimatedProbeFallbackCount,
            TransparentReflectionEstimatedEnvironmentFallbackCount =
                sceneData
                    .TransparentReflectionEstimatedEnvironmentFallbackCount,
            TransparentReflectionExactSsrEligibleCount =
                sceneData.TransparentReflectionExactSsrEligibleCount,
            TransparentReflectionExactSsrAdmittedCount =
                sceneData.TransparentReflectionExactSsrAdmittedCount,
            TransparentReflectionExactSsrReservedSampleCount =
                sceneData.TransparentReflectionExactSsrReservedSampleCount,
            TransparentReflectionExactSsrActualSampleCount =
                sceneData.TransparentReflectionExactSsrActualSampleCount,
            TransparentReflectionExactSsrHitCount =
                sceneData.TransparentReflectionExactSsrHitCount,
            TransparentReflectionExactSsrBudgetRejectedCount =
                sceneData.TransparentReflectionExactSsrBudgetRejectedCount,
            TransparentReflectionExactRayAdmittedCount =
                sceneData.TransparentReflectionExactRayAdmittedCount,
            TransparentReflectionExactRayBudgetRejectedCount =
                sceneData.TransparentReflectionExactRayBudgetRejectedCount,
            GpuHybridReflectionSsrMicroseconds =
                sceneData.GpuHybridReflectionSsrMicroseconds,
            GpuHybridReflectionRayQueryMicroseconds =
                sceneData.GpuHybridReflectionRayQueryMicroseconds,
            GpuHybridReflectionDdgiBaseMicroseconds =
                sceneData.GpuHybridReflectionDdgiBaseMicroseconds,
            GpuHybridReflectionResolveMicroseconds =
                sceneData.GpuHybridReflectionResolveMicroseconds,
            GpuHybridReflectionTemporalMicroseconds =
                sceneData.GpuHybridReflectionTemporalMicroseconds,
            GpuHybridReflectionSpatialMicroseconds =
                sceneData.GpuHybridReflectionSpatialMicroseconds,
            GpuHybridReflectionCompositeMicroseconds =
                sceneData.GpuHybridReflectionCompositeMicroseconds,
            StableSceneInputUploadBytes = sceneData.StableSceneInputUploadBytes,
            CpuCandidateListUploadBytes = sceneData.CpuCandidateListUploadBytes,
            SceneInstanceCandidateUploadBytes =
                sceneData.SceneInstanceCandidateUploadBytes,
            SceneInstanceCandidateBufferSize =
                sceneData.SceneInstanceCandidateBufferSize,
            CameraDrivenCpuDrawListRebuilt = sceneData.CameraDrivenCpuDrawListRebuilt,
            SolidObjectCount = sceneData.SolidObjectCount,
            GeometryDecalObjectCount = sceneData.GeometryDecalObjectCount,
            ThinGlassObjectCount = sceneData.ThinGlassObjectCount,
            SolidMeshletCount = sceneData.SolidMeshletCount,
            MaskedMeshletCount = sceneData.MaskedMeshletCount,
            GeometryDecalMeshletCount = sceneData.GeometryDecalMeshletCount,
            ThinGlassMeshletCount = sceneData.ThinGlassMeshletCount,
            ForwardSimpleMeshletCount = sceneData.ForwardSimpleMeshletCount,
            ForwardFullMaterialMeshletCount = sceneData.ForwardFullMaterialMeshletCount,
            ForwardLocalProbeMeshletCount = sceneData.ForwardLocalProbeMeshletCount,
            MaskMaterialCount = sceneData.MaskMaterialCount,
            GeometryDecalMaterialCount = sceneData.GeometryDecalMaterialCount,
            TransparentSortCandidateCount = sceneData.TransparentSortCandidateCount,
            TransparentSortMicroseconds = sceneData.TransparentSortMicroseconds,
            TransparentOverflowCount = sceneData.TransparentOverflowCount,
            TransparentPipelinePartitioningEnabled =
                sceneData.TransparentPipelinePartitioningEnabled ? 1 : 0,
            TransparentPipelinePartitioningEffective =
                sceneData.TransparentPipelinePartitioningEffective ? 1 : 0,
            TransparentPipelineRunCount =
                sceneData.TransparentPipelineRunCount,
            TransparentPipelineAverageRunLength =
                sceneData.TransparentPipelineAverageRunLength,
            TransparentPipelineMaximumRunLength =
                sceneData.TransparentPipelineMaximumRunLength,
            TransparentPipelineBindCount =
                sceneData.TransparentPipelineBindCount,
            TransparentPipelineUniversalFallbackCount =
                sceneData.TransparentPipelineUniversalFallbackCount,
            TransparentPipelineRayMeshletsAvoided =
                sceneData.TransparentPipelineRayMeshletsAvoided,
            TransparentPipelineDecalCacheMeshlets =
                sceneData.TransparentPipelineDecalCacheMeshlets,
            TransparentPipelineFallbackReason =
                sceneData.TransparentPipelineFallbackReason,
            StaticInstanceBatchCount = sceneData.StaticInstanceBatchCount,
            StaticInstanceCount = sceneData.StaticInstanceCount,
            VisibleStaticInstanceCount = sceneData.VisibleStaticInstanceCount,
            CulledStaticInstanceCount = sceneData.CulledStaticInstanceCount,
            StaticBatchMeshletDrawCommandCount = sceneData.StaticBatchMeshletDrawCommandCount,
            CpuStaticBatchBuildMicroseconds = sceneData.CpuStaticBatchBuildMicroseconds,
            TransparencyMode = sceneData.TransparencyMode,
            TransparencyDebugView = sceneData.TransparencyDebugView,
            DecalDebugView = sceneData.DecalDebugView,
            TransparentReceiveShadows = sceneData.TransparentReceiveShadows ? 1 : 0,
            TransparentReceiveGlobalIllumination =
                sceneData.TransparentReceiveGlobalIllumination ? 1 : 0,
            ThinGlassDirectionalOnlyPipelineEnabled =
                sceneData.ThinGlassDirectionalOnlyPipelineEnabled,
            WeightedOitEnabled = sceneData.TransparentPassEnabled && sceneData.TransparencyMode == TransparencyMode.WeightedBlendedOit ? 1 : 0,
            WeightedOitRenderTargetBytes = _renderTargets?.WeightedOitRenderTargetBytes ?? 0,
            WeightedOitRenderTargetCount = _renderTargets == null ? 0 : 2,
            GlobalIlluminationRequested = giRequested ? 1 : 0,
            GlobalIlluminationRequestedMode = giSettings.Mode,
            GlobalIlluminationRequestedDebugView = giSettings.DebugView,
            GlobalIlluminationRequestedDebugViewAvailable =
                requestedGiDebugViewAvailable ? 1 : 0,
            GlobalIlluminationDebugViewAvailabilityReason =
                RendererBuildFeatures.GetGlobalIlluminationDebugViewAvailabilityReason(
                    giSettings.DebugView),
            GlobalIlluminationEmergencyFallbackEnabled = giSettings.EmergencyGiFallbackEnabled ? 1 : 0,
            GlobalIlluminationFallbackReason = globalIlluminationFallbackReason,
            GlobalIlluminationDdgiRequested = ddgiRequested ? 1 : 0,
            SimpleDdgiRequested = simpleDdgiRequested ? 1 : 0,
            GlobalIlluminationRayQueryRequested = rayQueryGiRequested ? 1 : 0,
            GlobalIlluminationIndirectIntensity = giSettings.IndirectIntensity,
            GlobalIlluminationEnvironmentFallbackIntensity = giSettings.EnvironmentFallbackIntensity,
            GlobalIlluminationEnabled = giEnabled ? 1 : 0,
            GlobalIlluminationMode = giEnabled ? effectiveGiMode : GlobalIlluminationMode.Disabled,
            GlobalIlluminationDebugView = effectiveGiDebugView,
            GlobalIlluminationRayQuerySupported = giRayQuerySupported ? 1 : 0,
            GlobalIlluminationRayQueryActive = giRayQueryActive ? 1 : 0,
            GlobalIlluminationDdgiActive = giUsesDdgi ? 1 : 0,
            SimpleDdgiActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiActive : 0,
            SimpleDdgiStructuredGatherEnabled = giUsesSimpleDdgi &&
                sceneData.SimpleDdgiActive != 0 &&
                giRayQueryActive &&
                giSettings.SimpleDdgiStructuredGatherEnabled ? 1 : 0,
            SimpleDdgiReducedBlendEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiReducedBlendEnabled ? 1 : 0,
            SimpleDdgiToroidalScrollingEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiToroidalScrollingEnabled ? 1 : 0,
            SimpleDdgiRegionalInvalidationEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiRegionalInvalidationEnabled ? 1 : 0,
            FarFieldPagedFeatureEnabled = simpleDdgiRequested &&
                giSettings.FarFieldClipmapEnabled &&
                giSettings.FarFieldPagedEnabled ? 1 : 0,
            StreamedGiAccelerationStructuresFeatureEnabled = ddgiRequested && giSettings.StreamedGiAccelerationStructuresEnabled ? 1 : 0,
            DdgiDetailedCountersRequested = ddgiRequested &&
                (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                 giSettings.DebugView != GlobalIlluminationDebugView.None) ? 1 : 0,
            DdgiDetailedCountersCompiled =
                RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled ? 1 : 0,
            DdgiDetailedCountersEnabled = (giUsesDdgi || giUsesSimpleDdgi) &&
                RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
                (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                 giSettings.DebugView != GlobalIlluminationDebugView.None) ? 1 : 0,
            SimpleDdgiProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeCount : 0,
            SimpleDdgiProbesUpdated = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbesUpdated : 0,
            SimpleDdgiRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiRaysPerFrame : 0UL,
            SimpleDdgiTransportV2Active = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportV2Active : 0,
            SimpleDdgiAutomaticProbeDensityActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiAutomaticProbeDensityActive : 0,
            SimpleDdgiTransportSourceRefreshProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshProbeCount : 0,
            SimpleDdgiTransportSourceRefreshTargetProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshTargetProbeCount : 0,
            SimpleDdgiTransportSourceRefreshCapacityShortfall = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshCapacityShortfall : 0,
            SimpleDdgiTransportSourceCohortTransitionActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCohortTransitionActive : 0,
            SimpleDdgiTransportSourceCohortTransitionCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCohortTransitionCount : 0UL,
            SimpleDdgiTransportSourceCohortElapsedFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCohortElapsedFrames : 0,
            SimpleDdgiTransportSourceStepStaleProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStepStaleProbeCount : 0,
            SimpleDdgiTransportSourceStepAgeP95Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStepAgeP95Frames : 0,
            SimpleDdgiTransportSourceStepAgeMaximumFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStepAgeMaximumFrames : 0,
            SimpleDdgiTransportSourceStepAgeP95Seconds = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStepAgeP95Seconds : 0.0f,
            SimpleDdgiTransportSourceStepAgeMaximumSeconds = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStepAgeMaximumSeconds : 0.0f,
            SimpleDdgiTransportSourceCacheReuseProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheReuseProbeCount : 0,
            SimpleDdgiTransportSourceRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRayCount : 0UL,
            SimpleDdgiTransportSolveRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolveRayCount : 0UL,
            SimpleDdgiTransportPublishedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishedProbeCount : 0,
            SimpleDdgiTransportPublishRegionCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishRegionCount : 0,
            SimpleDdgiTransportPublishedProbeTotal = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishedProbeTotal : 0UL,
            SimpleDdgiTransportPublishRegionTotal = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishRegionTotal : 0UL,
            SimpleDdgiUpdateTransactionAbortCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiUpdateTransactionAbortCount : 0UL,
            SimpleDdgiTransportSourceCacheInvalidationCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheInvalidationCount : 0UL,
            SimpleDdgiTransportSolverInvalidationCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolverInvalidationCount : 0,
            SimpleDdgiTransportSolverInvalidationsPerSourceRefresh = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolverInvalidationsPerSourceRefresh : 0.0f,
            SimpleDdgiVolumeResourceGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiVolumeResourceGeneration : 0u,
            SimpleDdgiTransportTopologyGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportTopologyGeneration : 0u,
            SimpleDdgiVolumeRemapKind = giUsesSimpleDdgi ? sceneData.SimpleDdgiVolumeRemapKind : SimpleDdgiVolumeRemapKind.None,
            SimpleDdgiCompatibleToroidalScrollCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiCompatibleToroidalScrollCount : 0UL,
            SimpleDdgiIncompatibleTopologyChangeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiIncompatibleTopologyChangeCount : 0UL,
            SimpleDdgiGlobalConvergenceRestartCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiGlobalConvergenceRestartCount : 0UL,
            SimpleDdgiWholeReadbackDropCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiWholeReadbackDropCount : 0UL,
            SimpleDdgiSourceLightingGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiSourceLightingGeneration : 0u,
            SimpleDdgiAdmittedSourceCohortGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiAdmittedSourceCohortGeneration : 0u,
            SimpleDdgiTransportGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGeneration : 0u,
            SimpleDdgiPublishedPropagationGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiPublishedPropagationGeneration : 0u,
            SimpleDdgiLivePropagationSourceGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiLivePropagationSourceGeneration : 0u,
            SimpleDdgiVisiblePriorityParticipatingProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisiblePriorityParticipatingProbeCount : 0,
            SimpleDdgiVisiblePrioritySourceReadyProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisiblePrioritySourceReadyProbeCount : 0,
            SimpleDdgiVisiblePriorityPublishedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisiblePriorityPublishedProbeCount : 0,
            SimpleDdgiQuietPeriodComplete = giUsesSimpleDdgi ? sceneData.SimpleDdgiQuietPeriodComplete : 0,
            SimpleDdgiTransportSourceReadyProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceReadyProbeCount : 0,
            SimpleDdgiTransportSourceStaleProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStaleProbeCount : 0,
            SimpleDdgiTransportConvergedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportConvergedProbeCount : 0,
            SimpleDdgiTransportPendingSolverProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPendingSolverProbeCount : 0,
            SimpleDdgiTransportGlobalConvergencePending = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGlobalConvergencePending : 0,
            SimpleDdgiTransportGlobalConvergenceElapsedFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGlobalConvergenceElapsedFrames : 0,
            SimpleDdgiTransportConvergence = giUsesSimpleDdgi
                ? AttributeSimpleDdgiTransportRingTimings(
                    sceneData.SimpleDdgiTransportConvergence,
                    sceneData.GpuSimpleDdgiTransportMicroseconds +
                    sceneData.GpuSimpleDdgiAcceleratedSolveMicroseconds,
                    sceneData.GpuSimpleDdgiBlendMicroseconds)
                : SimpleDdgiTransportConvergenceTelemetry.Empty,
            SimpleDdgiTrackingState = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTrackingState
                : global::Njulf.Rendering.Resources.SimpleDdgiTrackingState.Bootstrapping,
            SimpleDdgiTransportCachedSweepCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportCachedSweepCount
                : 0,
            SimpleDdgiTransportAuditChunkCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportAuditChunkCount
                : 0,
            SimpleDdgiTransportCalibrationChangeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportCalibrationChangeCount : 0UL,
            SimpleDdgiTransportIrradianceAtlasBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportIrradianceAtlasBytes : 0UL,
            SimpleDdgiTransportSourceCacheBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheBytes : 0UL,
            SimpleDdgiTransportSolverRelaxation = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolverRelaxation : 0.0f,
            SimpleDdgiTransportAlbedoClamp = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportAlbedoClamp : 0.0f,
            SimpleDdgiTransportTailRelativeTolerance = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportTailRelativeTolerance : 0.0f,
            SimpleDdgiTransportAcceleratedSweepCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportAcceleratedSweepCount : 0,
            SimpleDdgiTransportAccelerationEnabled = giUsesSimpleDdgi && sceneData.SimpleDdgiTransportAccelerationEnabled,
            SimpleDdgiTransportAccelerationRuntimeAvailable = giUsesSimpleDdgi &&
                sceneData.SimpleDdgiTransportAccelerationRuntimeAvailable,
            SimpleDdgiTransportAcceleratedDispatchCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportAcceleratedDispatchCount
                : 0,
            SimpleDdgiTransportAcceleratedCanonicalPublicationCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportAcceleratedCanonicalPublicationCount
                : 0,
            SimpleDdgiTransportAcceleratedFinalPublicationCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportAcceleratedFinalPublicationCount
                : 0,
            SimpleDdgiTransportTailCertificationEnabled = giUsesSimpleDdgi && sceneData.SimpleDdgiTransportTailCertificationEnabled,
            SimpleDdgiTransportTailCertificationFallbackReason = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTransportTailCertificationFallbackReason
                : string.Empty,
            SimpleDdgiTransportResidualThreshold = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportTailRelativeTolerance : 0.0f,
            SimpleDdgiTransportMaximumSolverGenerations = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportMaximumSolverGenerations : 0,
            SimpleDdgiTransportSourceRefreshFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshFrames : 0,
            SimpleDdgiTransportConfiguredSourceRefreshFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportConfiguredSourceRefreshFrames : 0,
            SimpleDdgiInactiveProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiInactiveProbeCount : 0,
            SimpleDdgiInactiveProbeSkipCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiInactiveProbeSkipCount : 0,
            SimpleDdgiSavedRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiSavedRaysPerFrame : 0UL,
            SimpleDdgiLightingDirtyFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiLightingDirtyFrames : 0,
            SimpleDdgiLightingDirtyBoostedCapacity = giUsesSimpleDdgi ? sceneData.SimpleDdgiLightingDirtyBoostedCapacity : 0,
            SimpleDdgiDirtyReasonFlags = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyReasonFlags : 0,
            SimpleDdgiFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFullRayProbeUpdateCount : 0,
            SimpleDdgiMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaintenanceRayProbeUpdateCount : 0,
            SimpleDdgiAdaptiveRaySavedRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiAdaptiveRaySavedRaysPerFrame : 0UL,
            SimpleDdgiAdaptiveRayEvidence = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiAdaptiveRayEvidence
                : default,
            SimpleDdgiNearFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearFullRayProbeUpdateCount : 0,
            SimpleDdgiMidFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidFullRayProbeUpdateCount : 0,
            SimpleDdgiFarFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarFullRayProbeUpdateCount : 0,
            SimpleDdgiNearMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearMaintenanceRayProbeUpdateCount : 0,
            SimpleDdgiMidMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidMaintenanceRayProbeUpdateCount : 0,
            SimpleDdgiFarMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarMaintenanceRayProbeUpdateCount : 0,
            SimpleDdgiNearScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearScheduledPrimaryRayCount : 0UL,
            SimpleDdgiMidScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidScheduledPrimaryRayCount : 0UL,
            SimpleDdgiFarScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarScheduledPrimaryRayCount : 0UL,
            SimpleDdgiDirtyFirstUpdateLatencySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencySampleCount : 0,
            SimpleDdgiDirtyFirstUpdateLatencyP50Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyP50Frames : 0,
            SimpleDdgiDirtyFirstUpdateLatencyP95Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyP95Frames : 0,
            SimpleDdgiDirtyFirstUpdateLatencyMaxFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyMaxFrames : 0,
            SimpleDdgiOldestVisibleUnsupportedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge : 0,
            SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget : 0,
            SimpleDdgiVisibleZeroSupportRepairUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisibleZeroSupportRepairUpdateCount : 0,
            SimpleDdgiProbeLifecycleLatencyTargetFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames : 0,
            SimpleDdgiMaximumFreshProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumFreshProbeAge : 0,
            SimpleDdgiMaximumScrollExposedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumScrollExposedProbeAge : 0,
            SimpleDdgiMaximumRelocationPendingProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumRelocationPendingProbeAge : 0,
            SimpleDdgiMaximumUnpublishedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumUnpublishedProbeAge : 0,
            SimpleDdgiProbeLifecycleBoundExceededCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeLifecycleBoundExceededCount : 0,
            SimpleDdgiDirtyConvergenceLatencySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencySampleCount : 0,
            SimpleDdgiDirtyConvergenceLatencyP50Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyP50Frames : 0,
            SimpleDdgiDirtyConvergenceLatencyP95Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyP95Frames : 0,
            SimpleDdgiDirtyConvergenceLatencyMaxFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyMaxFrames : 0,
            SimpleDdgiMutationLatency = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationLatency
                : SimpleDdgiMutationLatencyTelemetry.Empty,
            SimpleDdgiAtlasBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasBytes : 0UL,
            SimpleDdgiSampledAtlasRequested = simpleDdgiRequested && giSettings.SimpleDdgiSampledAtlasEnabled ? 1 : 0,
            SimpleDdgiSampledAtlasActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasActive : 0,
            SimpleDdgiSampledAtlasGroupCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasGroupCount : 0,
            SimpleDdgiSampledAtlasLayersPerTexture = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasLayersPerTexture : 0,
            SimpleDdgiSampledAtlasImageBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasImageBytes : 0UL,
            SimpleDdgiStorage = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiStorage
                : SimpleDdgiStorageDiagnostics.Unavailable,
            SimpleDdgiWarmStart = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiWarmStart
                : SimpleDdgiWarmStartTelemetry.Disabled(
                    "Simple DDGI is inactive."),
            SimpleDdgiRefinement = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiRefinement
                : new SimpleDdgiRefinementBrickDiagnostics(
                    false,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false,
                    "inactive"),
            SimpleDdgiRefinementEmissiveDemand = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiRefinementEmissiveDemand
                : default,
            SimpleDdgiNearVisibility = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiNearVisibility
                : SimpleDdgiNearVisibilityDiagnostics.Disabled(
                    "Simple DDGI is inactive."),
            GiRoadmapExperiments = sceneData.GiRoadmapExperiments,
            SimpleDdgiNearFieldResidual =
                input.GlobalIllumination.NearFieldResidual,
            SimpleDdgiContentMemory =
                sceneData.SimpleDdgiContentMemory,
            SimpleDdgiRayScratchBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.RayScratchBytes ?? 0UL
                : 0UL,
            SimpleDdgiProbeStateBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ProbeStateBytes ?? 0UL
                : 0UL,
            SimpleDdgiReceiverProbeBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ReceiverProbeBytes ?? 0UL
                : 0UL,
            SimpleDdgiReceiverProbeCapacity = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ReceiverProbeCapacity ?? 0
                : 0,
            SimpleDdgiReceiverInvalidationBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ReceiverProbeInvalidationBytesThisFrame ?? 0UL
                : 0UL,
            SimpleDdgiReceiverInvalidationRangeCount = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ReceiverProbeInvalidationRunCountThisFrame ?? 0
                : 0,
            SimpleDdgiReceiverFullClear = giUsesSimpleDdgi &&
                _simpleDdgiVolumeManager?.ReceiverProbeFullClearThisFrame == true
                    ? 1
                    : 0,
            SimpleDdgiReceiverResourceGeneration = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiVolumeResourceGeneration
                : 0u,
            SimpleDdgiReceiverRecordsPublished = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ReceiverRecordsPublishedCount ?? 0
                : 0,
            SimpleDdgiProbeUpdateQueueBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ProbeUpdateQueueBytes ?? 0UL
                : 0UL,
            SimpleDdgiRelocationClassificationBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.RelocationClassificationBytes ?? 0UL
                : 0UL,
            SimpleDdgiProbeStateReadbackBytes = giUsesSimpleDdgi
                ? _simpleDdgiVolumeManager?.ProbeStateReadbackBytes ?? 0UL
                : 0UL,
            SimpleDdgiRetiredBufferCount =
                _simpleDdgiVolumeManager?.RetiredBufferCount ?? 0,
            SimpleDdgiRetiredBufferBytes =
                _simpleDdgiVolumeManager?.RetiredBufferBytes ?? 0UL,
            SimpleDdgiDuplicateMirrorBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSampledAtlasImageBytes
                : 0UL,
            SimpleDdgiDisabledRetainedBytes = !giUsesSimpleDdgi &&
                _simpleDdgiVolumeManager != null
                    ? _simpleDdgiVolumeManager.BufferBytes +
                      _simpleDdgiVolumeManager.SampledAtlasImageBytes
                    : 0UL,
            SimpleDdgiSampledAtlasFallbackReason = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSampledAtlasFallbackReason
                : simpleDdgiRequested && giSettings.EmergencyGiFallbackEnabled
                    ? "Emergency GI fallback is active."
                    : string.Empty,
            FarFieldPagedMode = giUsesSimpleDdgi ? sceneData.FarFieldPagedMode : 0,
            FarFieldPagePoolCapacity = giUsesSimpleDdgi ? sceneData.FarFieldPagePoolCapacity : 0,
            FarFieldResidentPageCount = giUsesSimpleDdgi ? sceneData.FarFieldResidentPageCount : 0,
            FarFieldPendingPageCount = giUsesSimpleDdgi ? sceneData.FarFieldPendingPageCount : 0,
            FarFieldPageRequestCount = giUsesSimpleDdgi ? sceneData.FarFieldPageRequestCount : 0,
            FarFieldPageMissCount = giUsesSimpleDdgi ? sceneData.FarFieldPageMissCount : 0,
            FarFieldPageRebuildCount = giUsesSimpleDdgi ? sceneData.FarFieldPageRebuildCount : 0,
            FarFieldPageEvictionCount = giUsesSimpleDdgi ? sceneData.FarFieldPageEvictionCount : 0,
            FarFieldScheduledPageBakeCount = giUsesSimpleDdgi ? sceneData.FarFieldScheduledPageBakeCount : 0,
            FarFieldCacheBytes = giUsesSimpleDdgi ? sceneData.FarFieldCacheBytes : 0UL,
            FarFieldMemoryBudgetBytes = simpleDdgiRequested &&
                giSettings.FarFieldClipmapEnabled
                ? giSettings.FarFieldMemoryBudgetBytes
                : 0UL,
            FarFieldInstanceBufferBytes = giUsesSimpleDdgi ? sceneData.FarFieldInstanceBufferBytes : 0UL,
            FarFieldPageTableBytes = giUsesSimpleDdgi ? sceneData.FarFieldPageTableBytes : 0UL,
            SimpleDdgiRecentered = giUsesSimpleDdgi ? sceneData.SimpleDdgiRecentered : 0,
            SimpleDdgiAtlasPreservedOnRecenter = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasPreservedOnRecenter : 0,
            SimpleDdgiScrollCommittedCascadeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollCommittedCascadeCount : 0,
            SimpleDdgiScrollDeferredCascadeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollDeferredCascadeCount : 0,
            SimpleDdgiScrollExposedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollExposedProbeCount : 0,
            SimpleDdgiScrollRepairExpectedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollRepairExpectedProbeCount : 0,
            SimpleDdgiScrollReservedPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollReservedPrimaryRayCount : 0UL,
            SimpleDdgiScrollEmergencyRebaseCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollEmergencyRebaseCount : 0UL,
            SimpleDdgiFrameRayBucket0 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket0 : 0u,
            SimpleDdgiFrameRayBucket1 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket1 : 0u,
            SimpleDdgiFrameRayBucket2 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket2 : 0u,
            SimpleDdgiFrameRayBucket3 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket3 : 0u,
            SimpleDdgiFrameRayBucket4 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket4 : 0u,
            SimpleDdgiFrameRayBucket5 = giUsesSimpleDdgi ? sceneData.SimpleDdgiFrameRayBucket5 : 0u,
            SimpleDdgiNearScrollCardinality = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearScrollCardinality : 0,
            SimpleDdgiMidScrollCardinality = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidScrollCardinality : 0,
            SimpleDdgiFarScrollCardinality = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarScrollCardinality : 0,
            SimpleDdgiScrollGpuExpectedCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollGpuExpectedCount : 0u,
            SimpleDdgiScrollGpuAcceptedCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollGpuAcceptedCount : 0u,
            SimpleDdgiScrollGpuTracedCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollGpuTracedCount : 0u,
            SimpleDdgiScrollGpuCommittedCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollGpuCommittedCount : 0u,
            SimpleDdgiScrollUnbucketedCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiScrollUnbucketedCount : 0u,
            SimpleDdgiScrollCohortFailure = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiScrollCohortFailure
                : SimpleDdgiScrollCohortFailureReason.None,
            SimpleDdgiRebuildingRingMask = giUsesSimpleDdgi ? sceneData.SimpleDdgiRebuildingRingMask : 0u,
            SimpleDdgiNearRebaseState = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearRebaseState : SimpleDdgiRebaseState.Stable,
            SimpleDdgiMidRebaseState = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidRebaseState : SimpleDdgiRebaseState.Stable,
            SimpleDdgiFarRebaseState = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarRebaseState : SimpleDdgiRebaseState.Stable,
            SimpleDdgiNearRebaseFadeFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearRebaseFadeFrame : 0,
            SimpleDdgiMidRebaseFadeFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidRebaseFadeFrame : 0,
            SimpleDdgiFarRebaseFadeFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarRebaseFadeFrame : 0,
            SimpleDdgiAtlasCleared = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasCleared : 0,
            SimpleDdgiAtlasFresh = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasFresh : 0,
            SimpleDdgiRecenterCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRecenterCount : 0,
            SimpleDdgiAtlasClearCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasClearCount : 0,
            SimpleDdgiAtlasPreserveOnRecenterCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasPreserveOnRecenterCount : 0,
            SimpleDdgiFramesSinceLastClear = giUsesSimpleDdgi ? sceneData.SimpleDdgiFramesSinceLastClear : 0,
            SimpleDdgiFramesSinceLastRecenter = giUsesSimpleDdgi ? sceneData.SimpleDdgiFramesSinceLastRecenter : 0,
            DdgiInvestigationCountersReadbackValid = giUsesDdgi ? sceneData.DdgiInvestigationCountersReadbackValid : 0,
            SimpleDdgiFreshAtlasForwardSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFreshAtlasForwardSampleCount : 0,
            SimpleDdgiZeroIrradianceSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiZeroIrradianceSampleCount : 0,
            SimpleDdgiNonzeroIrradianceSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNonzeroIrradianceSampleCount : 0,
            SimpleDdgiAverageSampledIrradianceLuminance = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageSampledIrradianceLuminance : 0.0f,
            SimpleDdgiAverageVisibility = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageVisibility : 0.0f,
            SimpleDdgiLowVisibilitySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiLowVisibilitySampleCount : 0,
            SimpleDdgiGatherSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiGatherSampleCount : 0,
            SimpleDdgiSecondVolumeGatherCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSecondVolumeGatherCount : 0,
            SimpleDdgiGatherMultiplicity = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherMultiplicity
                : SimpleDdgiGatherMultiplicityCounters.Empty,
            DecalFragmentAttribution = giUsesDdgi
                ? sceneData.DecalFragmentAttribution
                : DecalFragmentAttributionCounters.Empty,
            SimpleDdgiGatherPrimaryRejectionCounts = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherPrimaryRejectionCounts
                : Array.Empty<uint>(),
            SimpleDdgiGatherFallbackRejectionCounts = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherFallbackRejectionCounts
                : Array.Empty<uint>(),
            SimpleDdgiGatherRecoveryRejectionCounts = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherRecoveryRejectionCounts
                : Array.Empty<uint>(),
            SimpleDdgiGatherPrimaryAllFailedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherPrimaryAllFailedCount
                : 0,
            SimpleDdgiGatherFallbackAllFailedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherFallbackAllFailedCount
                : 0,
            SimpleDdgiGatherRecoveryAllFailedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiGatherRecoveryAllFailedCount
                : 0,
            DdgiFullRefreshFrameCount = giUsesDdgi ? sceneData.DdgiFullRefreshFrameCount : 0,
            DdgiPartialRefreshFrameCount = giUsesDdgi ? sceneData.DdgiPartialRefreshFrameCount : 0,
            DdgiUpdatedProbeFraction = giUsesDdgi ? sceneData.DdgiUpdatedProbeFraction : 0.0f,
            DdgiProbeUpdateStartIndex = giUsesDdgi ? sceneData.DdgiProbeUpdateStartIndex : 0,
            DdgiProbeUpdateEndIndex = giUsesDdgi ? sceneData.DdgiProbeUpdateEndIndex : 0,
            DdgiSkippedProbeCount = giUsesDdgi ? sceneData.DdgiSkippedProbeCount : 0,
            DdgiFramesSinceProbeUpdatedP50 = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedP50 : 0.0f,
            DdgiFramesSinceProbeUpdatedP95 = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedP95 : 0.0f,
            DdgiFramesSinceProbeUpdatedMax = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedMax : 0.0f,
            DdgiNewlyInvalidatedProbeCount = giUsesDdgi ? sceneData.DdgiNewlyInvalidatedProbeCount : 0,
            DdgiRefreshReasonRecenterProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonRecenterProbeCount : 0,
            DdgiRefreshReasonDirtyProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonDirtyProbeCount : 0,
            DdgiRefreshReasonAgeProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonAgeProbeCount : 0,
            DdgiRefreshReasonVisibilityProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonVisibilityProbeCount : 0,
            DdgiRefreshReasonFullRefreshProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonFullRefreshProbeCount : 0,
            DdgiForwardSimplePathSampleCount = giUsesDdgi ? sceneData.DdgiForwardSimplePathSampleCount : 0,
            DdgiForwardLegacyPathSampleCount = giUsesDdgi ? sceneData.DdgiForwardLegacyPathSampleCount : 0,
            DdgiForwardZeroFinalIndirectCount = giUsesDdgi ? sceneData.DdgiForwardZeroFinalIndirectCount : 0,
            DdgiForwardZeroDdgiButNonzeroIblCount = giUsesDdgi ? sceneData.DdgiForwardZeroDdgiButNonzeroIblCount : 0,
            DdgiForwardZeroDdgiAndZeroIblCount = giUsesDdgi ? sceneData.DdgiForwardZeroDdgiAndZeroIblCount : 0,
            DdgiForwardOutOfGridSampleCount = giUsesDdgi ? sceneData.DdgiForwardOutOfGridSampleCount : 0,
            DdgiForwardClampedProbeSampleCount = giUsesDdgi ? sceneData.DdgiForwardClampedProbeSampleCount : 0,
            DdgiForwardNanOrInfSampleCount = giUsesDdgi ? sceneData.DdgiForwardNanOrInfSampleCount : 0,
            DdgiIrradianceAtlasZeroTexelSampleCount = giUsesDdgi ? sceneData.DdgiIrradianceAtlasZeroTexelSampleCount : 0,
            DdgiVisibilityAtlasZeroMomentSampleCount = giUsesDdgi ? sceneData.DdgiVisibilityAtlasZeroMomentSampleCount : 0,
            DdgiAtlasWriteProbeCount = giUsesDdgi ? sceneData.DdgiAtlasWriteProbeCount : 0,
            DdgiAtlasWriteTexelCount = giUsesDdgi ? sceneData.DdgiAtlasWriteTexelCount : 0,
            DdgiBlendZeroRayWeightProbeCount = giUsesDdgi ? sceneData.DdgiBlendZeroRayWeightProbeCount : 0,
            DdgiBlendNonzeroIrradianceProbeCount = giUsesDdgi ? sceneData.DdgiBlendNonzeroIrradianceProbeCount : 0,
            DdgiBlendPreviousAtlasUsedCount = giUsesDdgi ? sceneData.DdgiBlendPreviousAtlasUsedCount : 0,
            DdgiBlendHysteresisZeroFrameCount = giUsesDdgi ? sceneData.DdgiBlendHysteresisZeroFrameCount : 0,
            DdgiSimpleTraceHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceHitCount : 0,
            DdgiSimpleTraceMissCount = giUsesDdgi ? sceneData.DdgiSimpleTraceMissCount : 0,
            DdgiSimpleTraceZeroRadianceHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceZeroRadianceHitCount : 0,
            DdgiSimpleTraceDirectLightHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceDirectLightHitCount : 0,
            DdgiSimpleTraceEmissiveHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceEmissiveHitCount : 0,
            DdgiSimpleTraceFarFieldHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldHitCount : 0,
            DdgiSimpleTraceFarFieldMissCount = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldMissCount : 0,
            DdgiSimpleTraceTlasUnavailableFrameCount = giUsesDdgi ? sceneData.DdgiSimpleTraceTlasUnavailableFrameCount : 0,
            SimpleDdgiSkyVisibilitySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSkyVisibilitySampleCount : 0,
            SimpleDdgiAverageSkyVisibility = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageSkyVisibility : 0.0f,
            FarFieldSunShadowSampleCount = giUsesSimpleDdgi ? sceneData.FarFieldSunShadowSampleCount : 0,
            FarFieldSunShadowOccludedCount = giUsesSimpleDdgi ? sceneData.FarFieldSunShadowOccludedCount : 0,
            SimpleDdgiRoughSpecularSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRoughSpecularSampleCount : 0,
            SimpleDdgiRoughSpecularNonzeroCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRoughSpecularNonzeroCount : 0,
            DdgiSimpleTraceFarFieldStepBucket0Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket0Count : 0,
            DdgiSimpleTraceFarFieldStepBucket1Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket1Count : 0,
            DdgiSimpleTraceFarFieldStepBucket2Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket2Count : 0,
            DdgiSimpleTraceFarFieldStepBucket3Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket3Count : 0,
            DdgiSimpleTraceFarFieldStepBucket4Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket4Count : 0,
            DdgiBlackFrameSuspect = giUsesDdgi ? sceneData.DdgiBlackFrameSuspect : 0,
            DdgiBlackFrameAfterRecenter = giUsesDdgi ? sceneData.DdgiBlackFrameAfterRecenter : 0,
            DdgiBlackFrameAfterAtlasClear = giUsesDdgi ? sceneData.DdgiBlackFrameAfterAtlasClear : 0,
            DdgiBlackFrameDuringFreshAtlas = giUsesDdgi ? sceneData.DdgiBlackFrameDuringFreshAtlas : 0,
            GpuForwardGiGatherMicroseconds = giEnabled ? sceneData.GpuForwardGiGatherMicroseconds : 0,
            GpuSimpleDdgiReceiverCacheMicroseconds = giUsesSimpleDdgi
                ? sceneData.GpuSimpleDdgiReceiverCacheMicroseconds
                : 0,
            SimpleDdgiReceiverCache = _forwardPlusPass?
                .SimpleDdgiReceiverCacheDiagnostics ??
                SimpleDdgiReceiverCacheDiagnostics.Exact(
                    Settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
                    SimpleDdgiReceiverCacheFallbackReason.ResourceUnavailable,
                    "forward pass unavailable"),
            GpuForwardGiGatherTimingCoverage = giEnabled ? sceneData.GpuForwardGiGatherTimingCoverage : 0,
            GpuForwardGiGatherTimingAttribution = giEnabled && sceneData.GpuForwardGiGatherTimingCoverage != 0
                ? GiTimingAttribution.Inclusive
                : GiTimingAttribution.Unavailable,
            // ForwardPlusPass currently records a whole forward draw. Preserve that raw
            // inclusive scope above, but never pretend it is an isolated GI timer.
            GpuForwardGiIncrementalMicroseconds = 0,
            GpuForwardGiIncrementalAttribution = GiTimingAttribution.Unavailable,
            GpuForwardGiIncrementalTimingReason = giUsesDdgi
                ? "Forward GI gather is inside the inclusive forward draw; use a deterministic paired capture until an isolated scope exists."
                : "Forward GI gather is inactive.",
            GpuFarFieldUpdateMicroseconds = giUsesSimpleDdgi ? sceneData.GpuFarFieldUpdateMicroseconds : 0,
            GpuFarFieldUpdateTimingValid = giUsesSimpleDdgi ? sceneData.GpuFarFieldUpdateTimingValid : 0,
            GpuSimpleDdgiTraceMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiTraceMicroseconds : 0,
            GpuSimpleDdgiPageDemandMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiPageDemandMicroseconds : 0,
            GpuSimpleDdgiPageResidencyMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiPageResidencyMicroseconds : 0,
            GpuSimpleDdgiPageFeedbackMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiPageFeedbackMicroseconds : 0,
            GpuSimpleDdgiScheduleMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleMicroseconds : 0,
            GpuSimpleDdgiScheduleResetMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleResetMicroseconds : 0,
            GpuSimpleDdgiScheduleClassifyMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleClassifyMicroseconds : 0,
            GpuSimpleDdgiSchedulePrefixMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiSchedulePrefixMicroseconds : 0,
            GpuSimpleDdgiScheduleLaneBaseMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleLaneBaseMicroseconds : 0,
            GpuSimpleDdgiScheduleCompactMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleCompactMicroseconds : 0,
            GpuSimpleDdgiScheduleTailAdmitMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleTailAdmitMicroseconds : 0,
            GpuSimpleDdgiScheduleAdmitMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleAdmitMicroseconds : 0,
            GpuSimpleDdgiScheduleMaterializeMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleMaterializeMicroseconds : 0,
            GpuSimpleDdgiScheduleEmitMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiScheduleEmitMicroseconds : 0,
            GpuSimpleDdgiTransportMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiTransportMicroseconds : 0,
            GpuSimpleDdgiAcceleratedSolveMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiAcceleratedSolveMicroseconds : 0,
            GpuSimpleDdgiDirectionalRadianceMicroseconds = giUsesSimpleDdgi
                ? sceneData.GpuSimpleDdgiDirectionalRadianceMicroseconds
                : 0,
            GpuSimpleDdgiBlendMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiBlendMicroseconds : 0,
            GpuSimpleDdgiRelocateClassifyMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiRelocateClassifyMicroseconds : 0,
            GpuSimpleDdgiPublishMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiPublishMicroseconds : 0,
            GpuSimpleDdgiTransportAuditMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiTransportAuditMicroseconds : 0,
            GpuSimpleDdgiCommitMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiCommitMicroseconds : 0,
            GpuSimpleDdgiUrgentRelightMicroseconds = giUsesSimpleDdgi
                ? sceneData.GpuSimpleDdgiUrgentRelightMicroseconds
                : 0,
            SimpleDdgiSchedulerMode = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerMode
                : SimpleDdgiSchedulerMode.CpuReference,
            SimpleDdgiSchedulerReady = giUsesSimpleDdgi ? sceneData.SimpleDdgiSchedulerReady : 0,
            SimpleDdgiCompletedFrameEvidence =
                sceneData.SimpleDdgiCompletedFrameEvidence,
            SimpleDdgiSchedulerFeedbackValid = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackValid
                : 0,
            SimpleDdgiSchedulerFeedbackFrameSerial = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackFrameSerial
                : 0UL,
            SimpleDdgiSchedulerFeedbackConsideredCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackConsideredCount
                : 0u,
            SimpleDdgiSchedulerFeedbackEligibleCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackEligibleCount
                : 0u,
            SimpleDdgiSchedulerFeedbackAcceptedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackAcceptedCount
                : 0u,
            SimpleDdgiSchedulerFeedbackCommittedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackCommittedCount
                : 0u,
            SimpleDdgiSchedulerFeedbackFailedCommitCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackFailedCommitCount
                : 0u,
            SimpleDdgiSchedulerCommitFailureBreakdown = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerCommitFailureBreakdown
                : string.Empty,
            SimpleDdgiSchedulerFeedbackPendingFreshCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingFreshCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingExposedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingExposedCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingRelocationCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingRelocationCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingSourceCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingSourceCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingSourceInvalidFlagCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingSourceInvalidFlagCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingSourcePrivateRepairCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingSourcePrivateRepairCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingSourceCardinalityCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingSourceCardinalityCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPendingSourceGenerationCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPendingSourceGenerationCount
                : 0u,
            SimpleDdgiSchedulerFeedbackSolveParticipantCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackSolveParticipantCount
                : 0u,
            SimpleDdgiSchedulerFeedbackSolveVisitedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackSolveVisitedCount
                : 0u,
            SimpleDdgiSchedulerFeedbackSolveEpoch = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackSolveEpoch
                : 0u,
            SimpleDdgiSchedulerFeedbackPrimaryRayCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPrimaryRayCount
                : 0u,
            SimpleDdgiSchedulerFeedbackSourceRayCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackSourceRayCount
                : 0u,
            SimpleDdgiSchedulerFeedbackTransportRayCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackTransportRayCount
                : 0u,
            SimpleDdgiSchedulerFeedbackSourceProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackSourceProbeCount
                : 0u,
            SimpleDdgiSchedulerFeedbackHardSourceProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackHardSourceProbeCount
                : 0u,
            SimpleDdgiSchedulerFeedbackRoutineSourceProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackRoutineSourceProbeCount
                : 0u,
            SimpleDdgiSchedulerFeedbackCachedSolverProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackCachedSolverProbeCount
                : 0u,
            SimpleDdgiSchedulerFeedbackPublishedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackPublishedCount
                : 0u,
            SimpleDdgiSchedulerResourceGeneration = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerResourceGeneration
                : 0u,
            SimpleDdgiSchedulerArenaBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerArenaBytes
                : 0UL,
            SimpleDdgiCostAwareSchedulingActive = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiCostAwareSchedulingActive
                : 0,
            SimpleDdgiSchedulerCostSampleCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerCostSampleCount
                : 0UL,
            SimpleDdgiSchedulerVisibilityPerPrimary = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerVisibilityPerPrimary
                : 0.0f,
            SimpleDdgiSchedulerAlphaCandidatesPerPrimary = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerAlphaCandidatesPerPrimary
                : 0.0f,
            SimpleDdgiSchedulerMaterialEvaluationsPerPrimary = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerMaterialEvaluationsPerPrimary
                : 0.0f,
            SimpleDdgiSchedulerFarFieldStepsPerPrimary = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFarFieldStepsPerPrimary
                : 0.0f,
            SimpleDdgiSparseResidualPropagationActive = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSparseResidualPropagationActive
                : 0,
            SimpleDdgiResidualSeededCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiResidualSeededCount
                : 0u,
            SimpleDdgiResidualDependentWakeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiResidualDependentWakeCount
                : 0u,
            SimpleDdgiResidualThresholdRejectedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiResidualThresholdRejectedCount
                : 0u,
            SimpleDdgiResidualCompleteSweepFallbackCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiResidualCompleteSweepFallbackCount
                : 0u,
            SimpleDdgiReceiverContributionFeedbackActive = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiReceiverContributionFeedbackActive
                : 0,
            SimpleDdgiReceiverContributingProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiReceiverContributingProbeCount
                : 0u,
            SimpleDdgiReceiverCoverageBucketCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiReceiverCoverageBucketCount
                : 0u,
            SimpleDdgiReceiverFallbackProbeCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiReceiverFallbackProbeCount
                : 0u,
            SimpleDdgiReceiverConsumerMask = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiReceiverConsumerMask
                : 0u,
            SimpleDdgiUrgentRelightActive = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiUrgentRelightActive
                : 0,
            SimpleDdgiUrgentRelightAcceptedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiUrgentRelightAcceptedCount
                : 0u,
            SimpleDdgiUrgentRelightCommittedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiUrgentRelightCommittedCount
                : 0u,
            SimpleDdgiUrgentRelightRejectedCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiUrgentRelightRejectedCount
                : 0u,
            SimpleDdgiSchedulerFeedbackReadbackBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackReadbackBytes
                : 0UL,
            SimpleDdgiSchedulerAuditReadbackBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerAuditReadbackBytes
                : 0UL,
            SimpleDdgiSchedulerRetiredBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerRetiredBytes
                : 0UL,
            SimpleDdgiSchedulerStaleFeedbackCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerStaleFeedbackCount
                : 0UL,
            SimpleDdgiSchedulerFeedbackGenerationRejectionCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFeedbackGenerationRejectionCount
                : 0UL,
            SimpleDdgiSchedulerFallbackLatched = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackLatched
                : 0,
            SimpleDdgiSchedulerFallbackFreshResetPending = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackFreshResetPending
                : 0,
            SimpleDdgiSchedulerFallbackCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackCount
                : 0UL,
            SimpleDdgiSchedulerFallbackReason = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackReason
                : string.Empty,
            SimpleDdgiSchedulerFallbackExportPending = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackExportPending
                : 0,
            SimpleDdgiSchedulerFallbackExportBytes = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerFallbackExportBytes
                : 0UL,
            SimpleDdgiSchedulerStateExportSuccessCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerStateExportSuccessCount
                : 0UL,
            SimpleDdgiSchedulerStateExportFailureCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerStateExportFailureCount
                : 0UL,
            SimpleDdgiSchedulerReentryStableFrameCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerReentryStableFrameCount
                : 0,
            SimpleDdgiSchedulerReentryCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiSchedulerReentryCount
                : 0UL,
            DdgiProbeVolumeCount = giUsesDdgi ? sceneData.DdgiProbeVolumeCount : 0,
            DdgiProbeCount = giUsesDdgi ? sceneData.DdgiProbeCount : 0,
            DdgiActiveProbeCount = giUsesDdgi ? sceneData.DdgiActiveProbeCount : 0,
            DdgiProbesUpdated = giUsesDdgi ? sceneData.DdgiProbesUpdated : 0,
            DdgiRaysPerProbe = giUsesDdgi ? sceneData.DdgiRaysPerProbe : 0,
            DdgiMaxActiveProbeBudget = giUsesDdgi ? sceneData.DdgiMaxActiveProbeBudget : 0,
            DdgiMaxProbeUpdatesPerFrame = giUsesDdgi ? sceneData.DdgiMaxProbeUpdatesPerFrame : 0,
            DdgiProbeUpdateRequestBudget = giUsesDdgi ? sceneData.DdgiProbeUpdateRequestBudget : 0,
            DdgiProbeUpdatePrimaryRayBudget = giUsesDdgi ? sceneData.DdgiProbeUpdatePrimaryRayBudget : 0,
            DdgiScheduledRequestBudget = giUsesDdgi ? sceneData.DdgiScheduledRequestBudget : 0,
            DdgiScheduledPrimaryRayBudget = giUsesDdgi ? sceneData.DdgiScheduledPrimaryRayBudget : 0,
            DdgiAverageSpatialCoverageEstimate = giUsesDdgi ? sceneData.DdgiAverageSpatialCoverageEstimate : 0.0f,
            DdgiAverageSupportCoverageEstimate = giUsesDdgi ? sceneData.DdgiAverageSupportCoverageEstimate : 0.0f,
            DdgiAverageDataConfidenceEstimate = giUsesDdgi ? sceneData.DdgiAverageDataConfidenceEstimate : 0.0f,
            DdgiAverageVisibilityConfidenceEstimate = giUsesDdgi ? sceneData.DdgiAverageVisibilityConfidenceEstimate : 0.0f,
            DdgiAverageLeakAttenuationEstimate = giUsesDdgi ? sceneData.DdgiAverageLeakAttenuationEstimate : 0.0f,
            DdgiAverageEffectiveContributionEstimate = giUsesDdgi ? sceneData.DdgiAverageEffectiveContributionEstimate : 0.0f,
            DdgiAverageOwnershipConsumedEstimate = giUsesDdgi ? sceneData.DdgiAverageOwnershipConsumedEstimate : 0.0f,
            DdgiWarmupState = giUsesDdgi ? sceneData.DdgiWarmupState : DdgiRuntimeWarmupState.Disabled,
            DdgiWarmedVisibleProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedVisibleProbeFraction : 0.0f,
            DdgiWarmedLocalProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedLocalProbeFraction : 0.0f,
            DdgiWarmedCascade0ProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedCascade0ProbeFraction : 0.0f,
            DdgiForwardEstimateCountersReadbackValid = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateCountersReadbackValid : 0,
            DdgiForwardEstimateSampleCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateSampleCount : 0u,
            DdgiForwardEstimateZeroVisibleButCoveredCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateZeroVisibleButCoveredCount : 0u,
            DdgiForwardEstimateZeroEffectiveButCoveredCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateZeroEffectiveButCoveredCount : 0u,
            DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount : 0u,
            DdgiForwardEstimateSampledIrradianceLuminance = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateSampledIrradianceLuminance : 0.0f,
            DdgiForwardEstimateRawDiffuseLuminance = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateRawDiffuseLuminance : 0.0f,
            DdgiForwardEstimateFinalDiffuseLuminance = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateFinalDiffuseLuminance : 0.0f,
            DdgiForwardEstimateEnvironmentFallbackWeight = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiForwardEstimateEnvironmentFallbackWeight : 0.0f,
            DdgiReceiverDiffuseReflectanceLuminance = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiReceiverDiffuseReflectanceLuminance : 0.0f,
            DdgiReceiverDiffuseReflectanceSampleCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiReceiverDiffuseReflectanceSampleCount : 0u,
            DdgiTraceOneSidedBackFaceAlbedoLuminance = giUsesSimpleDdgi ? sceneData.DdgiTraceOneSidedBackFaceAlbedoLuminance : 0.0f,
            DdgiTraceOneSidedBackFaceHitCount = giUsesSimpleDdgi ? sceneData.DdgiTraceOneSidedBackFaceHitCount : 0u,
            DdgiTraceOpaqueAlbedoLuminance = giUsesSimpleDdgi ? sceneData.DdgiTraceOpaqueAlbedoLuminance : 0.0f,
            DdgiTraceOpaqueHitCount = giUsesSimpleDdgi ? sceneData.DdgiTraceOpaqueHitCount : 0u,
            DdgiTraceThinSurfaceAlbedoLuminance = giUsesSimpleDdgi ? sceneData.DdgiTraceThinSurfaceAlbedoLuminance : 0.0f,
            DdgiTraceThinSurfaceHitCount = giUsesSimpleDdgi ? sceneData.DdgiTraceThinSurfaceHitCount : 0u,
            DdgiTraceUnsupportedTransmissionAlbedoLuminance = giUsesSimpleDdgi ? sceneData.DdgiTraceUnsupportedTransmissionAlbedoLuminance : 0.0f,
            DdgiTraceUnsupportedTransmissionHitCount = giUsesSimpleDdgi ? sceneData.DdgiTraceUnsupportedTransmissionHitCount : 0u,
            DdgiTraceReflectDisabledAlbedoLuminance = giUsesSimpleDdgi ? sceneData.DdgiTraceReflectDisabledAlbedoLuminance : 0.0f,
            DdgiTraceReflectDisabledHitCount = giUsesSimpleDdgi ? sceneData.DdgiTraceReflectDisabledHitCount : 0u,
            DdgiSupportRejectedInactiveCount = giUsesDdgi ? sceneData.DdgiSupportRejectedInactiveCount : 0u,
            DdgiSupportRejectedZeroIrradianceAlphaCount = giUsesDdgi ? sceneData.DdgiSupportRejectedZeroIrradianceAlphaCount : 0u,
            DdgiSupportRejectedLowQualityCount = giUsesDdgi ? sceneData.DdgiSupportRejectedLowQualityCount : 0u,
            DdgiProbeIrradianceAlphaAverage = giUsesDdgi ? sceneData.DdgiProbeIrradianceAlphaAverage : 0.0f,
            DdgiProbeQualityXAverage = giUsesDdgi ? sceneData.DdgiProbeQualityXAverage : 0.0f,
            DdgiProbeQualityYAverage = giUsesDdgi ? sceneData.DdgiProbeQualityYAverage : 0.0f,
            DdgiProbeQualityZAverage = giUsesDdgi ? sceneData.DdgiProbeQualityZAverage : 0.0f,
            DdgiProbeQualitySampleCount = giUsesDdgi ? sceneData.DdgiProbeQualitySampleCount : 0u,
            DdgiSampledProbeCurrentFrustumCount = giUsesDdgi ? sceneData.DdgiSampledProbeCurrentFrustumCount : 0u,
            DdgiSampledProbeSideRearCount = giUsesDdgi ? sceneData.DdgiSampledProbeSideRearCount : 0u,
            DdgiSampledProbeStaleAgeCount = giUsesDdgi ? sceneData.DdgiSampledProbeStaleAgeCount : 0u,
            DdgiClipmapInfoPrimaryAttemptCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryAttemptCount : 0u,
            DdgiClipmapInfoPrimaryOkCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryOkCount : 0u,
            DdgiClipmapInfoPrimaryFailedCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryFailedCount : 0u,
            DdgiClipmapInfoPrimaryEdgeFadeAverage = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryEdgeFadeAverage : 0.0f,
            DdgiClipmapInfoPrimaryBlendWeightAverage = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryBlendWeightAverage : 0.0f,
            DdgiFastGatherAttemptCount = giUsesDdgi ? sceneData.DdgiFastGatherAttemptCount : 0u,
            DdgiFastGatherAcceptedCount = giUsesDdgi ? sceneData.DdgiFastGatherAcceptedCount : 0u,
            DdgiFastGatherRejectedZeroSpatialCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroSpatialCount : 0u,
            DdgiFastGatherRejectedZeroSupportCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroSupportCount : 0u,
            DdgiFastGatherRejectedZeroDataCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroDataCount : 0u,
            DdgiFastGatherRejectedZeroOwnershipCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroOwnershipCount : 0u,
            DdgiFastGatherStatus = giUsesSimpleDdgi
                ? "not-applicable:simple-ddgi-uses-structured-volume-gather"
                : !giUsesDdgi
                    ? "disabled:ddgi-not-active"
                    : sceneData.DdgiForwardEstimateCountersReadbackValid == 0
                        ? "unavailable:detailed-counter-readback-disabled"
                        : sceneData.DdgiFastGatherAttemptCount == 0
                            ? "legacy-eligible:no-fast-gather-attempts"
                            : sceneData.DdgiFastGatherAcceptedCount == 0
                                ? "legacy-attempted:all-rejected"
                                : "legacy-active:accepted",
            DdgiShaderGatherFallbackAttemptCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackAttemptCount : 0u,
            DdgiShaderGatherFallbackAcceptedCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackAcceptedCount : 0u,
            DdgiShaderGatherFallbackEmptyCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackEmptyCount : 0u,
            DdgiTraceEnergySampleCount = giUsesDdgi ? sceneData.DdgiTraceEnergySampleCount : 0u,
            DdgiTraceEnergyHitCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitCount : 0u,
            DdgiTraceEnergyMissCount = giUsesDdgi ? sceneData.DdgiTraceEnergyMissCount : 0u,
            DdgiTraceEnergyRayLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyRayLuminanceAverage : 0.0f,
            DdgiTraceEnergyDirectLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyDirectLuminanceAverage : 0.0f,
            DdgiTraceEnergyEmissiveLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyEmissiveLuminanceAverage : 0.0f,
            DdgiTraceEnergyStableLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyStableLuminanceAverage : 0.0f,
            DdgiTraceEnergySkyLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergySkyLuminanceAverage : 0.0f,
            DdgiTraceEnergyHitZeroDirectCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitZeroDirectCount : 0u,
            DdgiTraceEnergyHitWithDirectCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitWithDirectCount : 0u,
            DdgiTraceEnergyDirectNoShadowLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyDirectNoShadowLuminanceAverage : 0.0f,
            DdgiShadowVisibilityRayCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityRayCount : 0u,
            DdgiShadowVisibilityOccludedCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityOccludedCount : 0u,
            DdgiShadowVisibilityNearHitCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityNearHitCount : 0u,
            DdgiShadowVisibilityCommittedHitDistanceAverage = giUsesDdgi ? sceneData.DdgiShadowVisibilityCommittedHitDistanceAverage : 0.0f,
            DdgiThinDetailedHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.DetailedHitCount : 0u,
            DdgiThinCompactHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.CompactHitCount : 0u,
            DdgiThinFarFieldExcludedCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.FarFieldExcludedCount : 0u,
            DdgiThinReflectedDirectLuminance = giUsesDdgi ? _completedThinSurfaceTransportCounters.ReflectedDirectLuminance : 0.0f,
            DdgiThinTransmittedDirectLuminance = giUsesDdgi ? _completedThinSurfaceTransportCounters.TransmittedDirectLuminance : 0.0f,
            DdgiThinReflectedRecursiveLuminance = giUsesDdgi ? _completedThinSurfaceTransportCounters.ReflectedRecursiveLuminance : 0.0f,
            DdgiThinTransmittedRecursiveLuminance = giUsesDdgi ? _completedThinSurfaceTransportCounters.TransmittedRecursiveLuminance : 0.0f,
            DdgiThinColoredShadowTransmissionRayCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.ColoredShadowTransmissionRayCount : 0u,
            DdgiThinTotalLayersTraversed = giUsesDdgi ? _completedThinSurfaceTransportCounters.TotalThinLayersTraversed : 0u,
            DdgiThinMaximumLayersTraversed = giUsesDdgi ? _completedThinSurfaceTransportCounters.MaximumThinLayersTraversed : 0u,
            DdgiThinLayerLimitTerminationCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.LayerLimitTerminationCount : 0u,
            DdgiThinLowTransmittanceTerminationCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.LowTransmittanceTerminationCount : 0u,
            DdgiThinZeroRadianceOpaqueHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.ZeroRadianceOpaqueHitCount : 0u,
            DdgiThinZeroRadianceThinHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.ZeroRadianceThinHitCount : 0u,
            DdgiThinZeroRadianceUnsupportedHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.ZeroRadianceUnsupportedHitCount : 0u,
            DdgiThinUnsupportedTransmissionHitCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.UnsupportedTransmissionHitCount : 0u,
            DdgiThinEnergyClampCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.EnergyClampCount : 0u,
            DdgiThinInvalidTransmissionCount = giUsesDdgi ? _completedThinSurfaceTransportCounters.InvalidTransmissionCount : 0u,
            DdgiTransparentVisibilityLayerCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.TransparentVisibilityLayerCount
                : 0u,
            DdgiTransparentVisibilityLimitCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.TransparentVisibilityLimitCount
                : 0u,
            DdgiRayDecalCandidateCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalCandidateCount
                : 0u,
            DdgiRayDecalRetainedCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalRetainedCount
                : 0u,
            DdgiRayDecalAssociatedCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalAssociatedCount
                : 0u,
            DdgiRayDecalDepthRejectCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalDepthRejectCount
                : 0u,
            DdgiRayDecalFacingRejectCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalFacingRejectCount
                : 0u,
            DdgiRayDecalCandidateLimitCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.DecalCandidateLimitCount
                : 0u,
            DdgiFoliageProxyHitCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.FoliageProxyHitCount
                : 0u,
            DdgiInvalidRayMetadataCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.InvalidRayMetadataCount
                : 0u,
            DdgiStochasticAlphaAcceptCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.StochasticAlphaAcceptCount
                : 0u,
            DdgiStochasticAlphaRejectCount = giUsesDdgi
                ? _completedDdgiGeometryParticipationCounters.StochasticAlphaRejectCount
                : 0u,
            DdgiTraceEarlyOutDisabledCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutDisabledCount : 0u,
            DdgiTraceEarlyOutBeyondRequestCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutBeyondRequestCount : 0u,
            DdgiTraceEarlyOutResolveBoundsCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveBoundsCount : 0u,
            DdgiTraceEarlyOutResolveProbeRangeCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveProbeRangeCount : 0u,
            DdgiTraceEarlyOutResolveClipmapCellCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveClipmapCellCount : 0u,
            DdgiTraceEarlyOutResolveClipmapRingCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveClipmapRingCount : 0u,
            DdgiTraceRingMismatchCorrectedCount = giUsesDdgi ? sceneData.DdgiTraceRingMismatchCorrectedCount : 0u,
            DdgiTraceRingMismatchSample = giUsesDdgi ? sceneData.DdgiTraceRingMismatchSample : string.Empty,
            DdgiBlendEnergySampleCount = giUsesDdgi ? sceneData.DdgiBlendEnergySampleCount : 0u,
            DdgiBlendEnergyIrradianceLuminanceAverage = giUsesDdgi ? sceneData.DdgiBlendEnergyIrradianceLuminanceAverage : 0.0f,
            DdgiBlendEnergyConfidenceAverage = giUsesDdgi ? sceneData.DdgiBlendEnergyConfidenceAverage : 0.0f,
            DdgiBlendEnergyLowConfidenceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyLowConfidenceCount : 0u,
            DdgiBlendEnergyNonzeroIrradianceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyNonzeroIrradianceCount : 0u,
            DdgiBlendEnergyNonFiniteIrradianceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyNonFiniteIrradianceCount : 0u,
            DdgiBlendEnergyFireflySuppressedCount = giUsesDdgi ? sceneData.DdgiBlendEnergyFireflySuppressedCount : 0u,
            SimpleDdgiTransportEnergySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportEnergySampleCount : 0u,
            SimpleDdgiTransportSourceCacheHitCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheHitCount : 0u,
            SimpleDdgiTransportSourceCacheMissCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheMissCount : 0u,
            SimpleDdgiTransportBounceLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportBounceLuminanceAverage : 0.0f,
            SimpleDdgiTransportSourceLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceLuminanceAverage : 0.0f,
            SimpleDdgiTransportTotalLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportTotalLuminanceAverage : 0.0f,
            DdgiTransparentReceiverSampleCount = giUsesDdgi ? sceneData.DdgiTransparentReceiverSampleCount : 0u,
            DdgiTransparentReceiverIrradianceLuminanceAverage = giUsesDdgi ? sceneData.DdgiTransparentReceiverIrradianceLuminanceAverage : 0.0f,
            DdgiTransparentReceiverFinalLuminanceAverage = giUsesDdgi ? sceneData.DdgiTransparentReceiverFinalLuminanceAverage : 0.0f,
            DdgiDecalReceiverSampleCount = giUsesDdgi ? sceneData.DdgiDecalReceiverSampleCount : 0u,
            DdgiDecalReceiverIrradianceLuminanceAverage = giUsesDdgi ? sceneData.DdgiDecalReceiverIrradianceLuminanceAverage : 0.0f,
            DdgiDecalReceiverFinalLuminanceAverage = giUsesDdgi ? sceneData.DdgiDecalReceiverFinalLuminanceAverage : 0.0f,
            DdgiVisibilityMomentMeanAverage = giUsesDdgi ? sceneData.DdgiVisibilityMomentMeanAverage : 0.0f,
            DdgiVisibilityMomentVarianceAverage = giUsesDdgi ? sceneData.DdgiVisibilityMomentVarianceAverage : 0.0f,
            DdgiVisibilityProbeDistanceAverage = giUsesDdgi ? sceneData.DdgiVisibilityProbeDistanceAverage : 0.0f,
            DdgiVisibilityMomentSampleCount = giUsesDdgi ? sceneData.DdgiVisibilityMomentSampleCount : 0u,
            DdgiVisibilityLargeDistanceMarginCount = giUsesDdgi ? sceneData.DdgiVisibilityLargeDistanceMarginCount : 0u,
            DdgiVisibilityZeroTransportCount = giUsesDdgi ? sceneData.DdgiVisibilityZeroTransportCount : 0u,
            DdgiVisibilityZeroTransportWithIrradianceCount = giUsesDdgi ? sceneData.DdgiVisibilityZeroTransportWithIrradianceCount : 0u,
            DdgiAverageRelocationFractionEstimate = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiAverageRelocationFractionEstimate : 0.0f,
            DdgiRelocatedProbeFractionEstimate = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiRelocatedProbeFractionEstimate : 0.0f,
            DdgiAverageRelocationDisplacementFractionEstimate = giUsesSimpleDdgi ? sceneData.DdgiAverageRelocationDisplacementFractionEstimate : 0.0f,
            SimpleDdgiAverageBackfaceRatioEstimate = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageBackfaceRatioEstimate : 0.0f,
            SimpleDdgiAverageCloseRatioEstimate = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageCloseRatioEstimate : 0.0f,
            SimpleDdgiAverageHardInvalidProbeScoreEstimate = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageHardInvalidProbeScoreEstimate : 0.0f,
            DdgiClassifiedInactiveProbeCountEstimate = giUsesDdgi ? sceneData.DdgiClassifiedInactiveProbeCountEstimate : 0,
            DdgiQualityTier = ddgiRequested ? giSettings.DdgiQualityTier : DdgiQualityTier.DdgiHigh,
            DdgiAdaptiveBudgetScale = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetScale : 1.0f,
            DdgiAdaptiveBudgetReduced = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReduced : 0,
            DdgiEmergencyDegradeActive = giUsesDdgi ? sceneData.DdgiEmergencyDegradeActive : 0,
            DdgiEffectiveMaxShadedLights = giUsesDdgi ? sceneData.DdgiEffectiveMaxShadedLights : 0,
            DdgiAdaptiveBudgetReason = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReason : string.Empty,
            DdgiAsyncComputeEnabled = ddgiAsyncComputeActuallyEnabled ? 1 : 0,
            DdgiAtlasMemoryBudgetBytes = ddgiRequested ? giSettings.DdgiAtlasMemoryBudgetBytes : 0,
            DdgiProbeRelocationCount = giUsesDdgi ? sceneData.DdgiProbeRelocationCount : 0,
            DdgiProbeClassificationCount = giUsesDdgi ? sceneData.DdgiProbeClassificationCount : 0,
            DdgiCascadeCount = giUsesDdgi ? sceneData.DdgiCascadeCount : 0,
            DdgiScrollCount = giUsesDdgi ? sceneData.DdgiScrollCount : 0,
            DdgiNewProbeCount = giUsesDdgi ? sceneData.DdgiNewProbeCount : 0,
            DdgiDirtyBoundsProbeUpdateCount = giUsesDdgi ? sceneData.DdgiDirtyBoundsProbeUpdateCount : 0,
            SimpleDdgiMutationJournalLastConsumedSerial = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalLastConsumedSerial
                : 0UL,
            SimpleDdgiMutationJournalEnqueuedEventCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalEnqueuedEventCount
                : 0UL,
            SimpleDdgiMutationJournalCoalescedEventCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalCoalescedEventCount
                : 0UL,
            SimpleDdgiMutationJournalOverflowCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalOverflowCount
                : 0UL,
            SimpleDdgiMutationJournalConservativeFallbackCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalConservativeFallbackCount
                : 0UL,
            SimpleDdgiMutationJournalAttachScanCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalAttachScanCount
                : 0UL,
            SimpleDdgiMutationJournalAttachObjectCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalAttachObjectCount
                : 0UL,
            SimpleDdgiMutationJournalOracleComparisonCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalOracleComparisonCount
                : 0UL,
            SimpleDdgiMutationJournalOracleMismatchCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalOracleMismatchCount
                : 0UL,
            SimpleDdgiMutationJournalPendingEventCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalPendingEventCount
                : 0,
            SimpleDdgiMutationJournalOutputRegionCount = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalOutputRegionCount
                : 0,
            SimpleDdgiMutationJournalOverflowedThisFrame = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiMutationJournalOverflowedThisFrame
                : 0,
            GiPipelineCacheLoaded = giPipelineCacheTelemetry.CacheLoaded ? 1 : 0,
            GiPipelineCacheRejected = giPipelineCacheTelemetry.CacheRejected ? 1 : 0,
            GiPipelineCacheSaved = giPipelineCacheTelemetry.CacheSaved ? 1 : 0,
            GiPipelineCacheLoadedPayloadBytes =
                giPipelineCacheTelemetry.LoadedPayloadBytes,
            GiPipelineCacheSavedPayloadBytes =
                giPipelineCacheTelemetry.SavedPayloadBytes,
            GiPipelineCreationCount =
                giPipelineCacheTelemetry.PipelineCreationCount,
            GiPipelineCreationMicroseconds =
                giPipelineCacheTelemetry.PipelineCreationMicroseconds,
            GiRenderCriticalPipelineCreationCount =
                giPipelineCacheTelemetry.RenderCriticalPipelineCreationCount,
            GiPipelineApplicationCacheHitCount =
                giPipelineCacheTelemetry.ApplicationCacheHitCount,
            GiPipelineCompileMissCount =
                giPipelineCacheTelemetry.PipelineCompileMissCount,
            GiPipelineFeedbackUnavailableCount =
                giPipelineCacheTelemetry.PipelineFeedbackUnavailableCount,
            GiPipelinePeakConcurrentCreationCount =
                giPipelineCacheTelemetry.PeakConcurrentPipelineCreationCount,
            GiPipelineBinaryCacheEnabled =
                giPipelineCacheTelemetry.PipelineBinaryCacheEnabled ? 1 : 0,
            GiGraphicsPipelineLibraryEligible =
                giPipelineCacheTelemetry.GraphicsPipelineLibraryEligible ? 1 : 0,
            GiPipelineWritableBinaryHitCount =
                giPipelineCacheTelemetry.WritableBinaryHitCount,
            GiPipelineSeedBinaryHitCount =
                giPipelineCacheTelemetry.SeedBinaryHitCount,
            GiCapturedPipelineBinaryCount =
                giPipelineCacheTelemetry.CapturedPipelineBinaryCount,
            GiPipelineCachePath = giPipelineCacheTelemetry.CachePath,
            GiPipelineBinaryStorePath =
                giPipelineCacheTelemetry.PipelineBinaryStorePath,
            GiPipelineCacheStatus = giPipelineCacheTelemetry.LoadStatus,
            GiLastCreatedPipeline = giPipelineCacheTelemetry.LastCreatedPipeline,
            DdgiVisibleFrustumProbeUpdateCount = giUsesDdgi ? sceneData.DdgiVisibleFrustumProbeUpdateCount : 0,
            DdgiOutsideFrustumSafetyProbeUpdateCount = giUsesDdgi ? sceneData.DdgiOutsideFrustumSafetyProbeUpdateCount : 0,
            DdgiAgeRefreshProbeUpdateCount = giUsesDdgi ? sceneData.DdgiAgeRefreshProbeUpdateCount : 0,
            DdgiHighVarianceProbeUpdateCount = giUsesDdgi ? sceneData.DdgiHighVarianceProbeUpdateCount : 0,
            DdgiLowConfidenceProbeUpdateCount = giUsesDdgi ? sceneData.DdgiLowConfidenceProbeUpdateCount : 0,
            DdgiStableProbeUpdateCount = giUsesDdgi ? sceneData.DdgiStableProbeUpdateCount : 0,
            DdgiAverageProbeVariability = giUsesDdgi ? sceneData.DdgiAverageProbeVariability : 0.0f,
            DdgiAverageProbeConfidence = giUsesDdgi ? sceneData.DdgiAverageProbeConfidence : 0.0f,
            DdgiScheduledPrimaryRayCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiScheduledPrimaryRayCount : 0UL,
            DdgiEstimatedShadowRayUpperBound = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiEstimatedShadowRayUpperBound : 0UL,
            DdgiSelectedDirectionalHitCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiSelectedDirectionalHitCount : 0UL,
            DdgiSelectedLocalHitCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiSelectedLocalHitCount : 0UL,
            DdgiVisibilityRayCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiVisibilityRayCount : 0UL,
            DdgiSkippedLocalLightCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiSkippedLocalLightCount : 0UL,
            DdgiLightSelectionMode = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiLightSelectionMode : string.Empty,
            DdgiEmissiveSourceCount = giUsesDdgi ? sceneData.DdgiEmissiveSourceCount : 0,
            SimpleDdgiTraceContentProfile = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTraceContentProfile
                : 0,
            SimpleDdgiTraceDistanceProfile = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTraceDistanceProfile
                : 0,
            SimpleDdgiTraceSpecialized = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTraceSpecialized
                : 0,
            SimpleDdgiTraceWorkgroupSize = giUsesSimpleDdgi
                ? sceneData.SimpleDdgiTraceWorkgroupSize
                : 64,
            DdgiEmissiveSourceRevision = giUsesDdgi ? sceneData.DdgiEmissiveSourceRevision : 0,
            DdgiEmissiveSamplingMode = giUsesDdgi ? sceneData.DdgiEmissiveSamplingMode : string.Empty,
            DdgiEmissiveTriangleCandidateCount = giUsesDdgi ? sceneData.DdgiEmissiveTriangleCandidateCount : 0,
            DdgiEmissiveTriangleBudget = giUsesDdgi ? sceneData.DdgiEmissiveTriangleBudget : 0,
            DdgiEmissiveSkippedEnergyFraction = giUsesDdgi ? sceneData.DdgiEmissiveSkippedEnergyFraction : 0.0f,
            DdgiEmissiveSkippedSkinnedObjectCount = giUsesDdgi ? sceneData.DdgiEmissiveSkippedSkinnedObjectCount : 0,
            DdgiEmissiveSkippedSkinnedImportance = giUsesDdgi ? sceneData.DdgiEmissiveSkippedSkinnedImportance : 0.0,
            DdgiEmissiveAverageRadianceRed = giUsesDdgi ? sceneData.DdgiEmissiveAverageRadiance.X : 0.0f,
            DdgiEmissiveAverageRadianceGreen = giUsesDdgi ? sceneData.DdgiEmissiveAverageRadiance.Y : 0.0f,
            DdgiEmissiveAverageRadianceBlue = giUsesDdgi ? sceneData.DdgiEmissiveAverageRadiance.Z : 0.0f,
            DdgiEmissivePeakLuminanceNits = giUsesDdgi ? sceneData.DdgiEmissivePeakLuminanceNits : 0.0f,
            DdgiEmissiveCoveredAreaSquareMeters = giUsesDdgi ? sceneData.DdgiEmissiveCoveredAreaSquareMeters : 0.0,
            DdgiEmissiveIntegratedPowerRed = giUsesDdgi ? sceneData.DdgiEmissiveIntegratedPowerRed : 0.0,
            DdgiEmissiveIntegratedPowerGreen = giUsesDdgi ? sceneData.DdgiEmissiveIntegratedPowerGreen : 0.0,
            DdgiEmissiveIntegratedPowerBlue = giUsesDdgi ? sceneData.DdgiEmissiveIntegratedPowerBlue : 0.0,
            DdgiEmissiveIntegratedPowerLuminance = giUsesDdgi ? sceneData.DdgiEmissiveIntegratedPowerLuminance : 0.0,
            DdgiEmissiveSelectedProbability = giUsesDdgi ? sceneData.DdgiEmissiveSelectedProbability : 0.0f,
            DdgiEmissiveEnergyWarningCount = giUsesDdgi ? sceneData.DdgiEmissiveEnergyWarningCount : 0UL,
            DdgiEmissiveLastEnergyWarning = giUsesDdgi ? sceneData.DdgiEmissiveLastEnergyWarning : string.Empty,
            DdgiEmissiveSamplingInvocationCount = giUsesDdgi
                ? _completedMaterialGiCounters.EstimatedEmissiveSamplingInvocationCount
                : 0u,
            DdgiEmissiveTableCacheHit = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheHit : 0,
            DdgiEmissiveTableCacheHitCount = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheHitCount : 0UL,
            DdgiEmissiveTableCacheMissCount = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheMissCount : 0UL,
            DdgiEmissiveTableRebuildCount = giUsesDdgi ? sceneData.DdgiEmissiveTableRebuildCount : 0UL,
            DdgiEmissiveTableInvalidationCount = giUsesDdgi ? sceneData.DdgiEmissiveTableInvalidationCount : 0UL,
            DdgiEmissiveTableUploadCount = giUsesDdgi ? sceneData.DdgiEmissiveTableUploadCount : 0UL,
            DdgiEmissiveHierarchyNodeCount = giUsesDdgi ? sceneData.DdgiEmissiveHierarchyNodeCount : 0,
            DdgiEmissiveHierarchyBuildCount = giUsesDdgi ? sceneData.DdgiEmissiveHierarchyBuildCount : 0UL,
            DdgiEmissiveHierarchyRefitCount = giUsesDdgi ? sceneData.DdgiEmissiveHierarchyRefitCount : 0UL,
            DdgiEmissiveHierarchyUpdatedNodeCount = giUsesDdgi ? sceneData.DdgiEmissiveHierarchyUpdatedNodeCount : 0,
            DdgiVfxMacroSourceCount = giUsesDdgi ? sceneData.DdgiVfxMacroSourceCount : 0,
            DdgiVfxMacroEligibleEmitterCount = giUsesDdgi ? sceneData.DdgiVfxMacroEligibleEmitterCount : 0,
            DdgiVfxMacroRejectedTransientCount = giUsesDdgi ? sceneData.DdgiVfxMacroRejectedTransientCount : 0,
            DdgiVfxMacroOverflowCount = giUsesDdgi ? sceneData.DdgiVfxMacroOverflowCount : 0,
            DdgiVfxMacroAuthoredPowerCount = giUsesDdgi ? sceneData.DdgiVfxMacroAuthoredPowerCount : 0,
            DdgiVfxMacroAutoPowerCount = giUsesDdgi ? sceneData.DdgiVfxMacroAutoPowerCount : 0,
            DdgiVfxMacroRevision = giUsesDdgi ? sceneData.DdgiVfxMacroRevision : 0UL,
            DdgiVfxMacroRefitCount = giUsesDdgi ? sceneData.DdgiVfxMacroRefitCount : 0UL,
            DdgiProbeVolumeBufferBytes = giUsesDdgi ? sceneData.DdgiProbeVolumeBufferBytes : 0UL,
            DdgiProbeStateBufferBytes = giUsesDdgi ? sceneData.DdgiProbeStateBufferBytes : 0UL,
            DdgiProbeUpdateQueueBytes = giUsesDdgi ? sceneData.DdgiProbeUpdateQueueBytes : 0UL,
            DdgiProbeRelocationClassificationBytes = giUsesDdgi ? sceneData.DdgiProbeRelocationClassificationBytes : 0UL,
            DdgiCurrentIrradianceAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentIrradianceAtlasBytes : 0UL,
            DdgiCurrentVisibilityAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentVisibilityAtlasBytes : 0UL,
            DdgiTraceDispatchGroupCount = giUsesDdgi ? sceneData.DdgiTraceDispatchGroupCount : 0u,
            DdgiTraceProbeCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiTraceProbeCount : 0u,
            DdgiTraceRayCount = giUsesDdgi || giUsesSimpleDdgi ? sceneData.DdgiTraceRayCount : 0u,
            DdgiBlendProbeCount = giUsesDdgi ? sceneData.DdgiBlendProbeCount : 0u,
            DdgiRelocateClassifyProbeCount = giUsesDdgi ? sceneData.DdgiRelocateClassifyProbeCount : 0u,
            DdgiPublishProbeCount = giUsesDdgi ? sceneData.DdgiPublishProbeCount : 0u,
            DdgiUpdateExecuted = sceneData.DdgiUpdateExecuted,
            DdgiUpdateSkipReason = sceneData.DdgiUpdateSkipReason,
            DdgiRayScratchBytes = giUsesDdgi ? sceneData.DdgiRayScratchBytes : 0UL,
            DdgiUpdatedAtlasBytes = giUsesDdgi ? sceneData.DdgiUpdatedAtlasBytes : 0UL,
            DdgiPublishExecuted = sceneData.DdgiPublishExecuted,
            DdgiPublishSkipReason = sceneData.DdgiPublishSkipReason,
            DdgiPublishedCacheLatencyFrames = giUsesDdgi ? sceneData.DdgiPublishedCacheLatencyFrames : 0,
            DdgiCacheGeneration = giUsesDdgi ? sceneData.DdgiCacheGeneration : 0u,
            DdgiLastUpdatedFrameSerial = giUsesDdgi ? sceneData.DdgiLastUpdatedFrameSerial : 0UL,
            DdgiCacheWarmupState = giUsesDdgi ? sceneData.DdgiCacheWarmupState : DdgiRuntimeWarmupState.Disabled,
            DdgiStaleProbeCount = giUsesDdgi ? sceneData.DdgiStaleProbeCount : 0,
            DdgiAverageProbeAge = giUsesDdgi ? sceneData.DdgiAverageProbeAge : 0.0f,
            DdgiMaxProbeAge = giUsesDdgi ? sceneData.DdgiMaxProbeAge : 0UL,
            DdgiFrustumUpdatePercentage = giUsesDdgi ? sceneData.DdgiFrustumUpdatePercentage : 0.0f,
            DdgiOutsideFrustumUpdatePercentage = giUsesDdgi ? sceneData.DdgiOutsideFrustumUpdatePercentage : 0.0f,
            DdgiResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiResourceReinitializationCount : 0,
            DdgiTotalResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiTotalResourceReinitializationCount : 0,
            DdgiActiveLocalSlotCount = giUsesDdgi ? sceneData.DdgiActiveLocalSlotCount : 0,
            DdgiCacheClearReason = giUsesDdgi ? sceneData.DdgiCacheClearReason : string.Empty,
            CpuDdgiRecordMicroseconds = giUsesDdgi ? sceneData.CpuDdgiRecordMicroseconds : 0,
            CpuSimpleDdgiRecordMicroseconds = giUsesSimpleDdgi ? sceneData.CpuSimpleDdgiRecordMicroseconds : 0,
            SimpleDdgiUploadTiming = giUsesSimpleDdgi ? sceneData.SimpleDdgiUploadTiming : default,
            CpuFarFieldRecordMicroseconds = giUsesSimpleDdgi ? sceneData.CpuFarFieldRecordMicroseconds : 0,
            CpuGlobalIlluminationRecordMicroseconds = giEnabled ? sceneData.CpuGlobalIlluminationRecordMicroseconds : 0,
            CpuGlobalIlluminationRecordP95Microseconds = giEnabled ? sceneData.CpuGlobalIlluminationRecordP95Microseconds : 0,
            GlobalIlluminationCpuTimingSampleCount = giEnabled ? sceneData.GlobalIlluminationCpuTimingSampleCount : 0,
            GpuDdgiTraceMicroseconds = giUsesDdgi ? sceneData.GpuDdgiTraceMicroseconds : 0,
            GpuDdgiBlendMicroseconds = giUsesDdgi ? sceneData.GpuDdgiBlendMicroseconds : 0,
            GpuDdgiRelocateClassifyMicroseconds = giUsesDdgi ? sceneData.GpuDdgiRelocateClassifyMicroseconds : 0,
            GpuDdgiPublishMicroseconds = giUsesDdgi ? sceneData.GpuDdgiPublishMicroseconds : 0,
            GpuDdgiUpdateMicroseconds = giUsesDdgi ? sceneData.GpuDdgiUpdateMicroseconds : 0,
            GpuGiCompositeMicroseconds = giEnabled ? sceneData.GpuGiCompositeMicroseconds : 0,
            DdgiTextureBytes = giUsesDdgi ? sceneData.DdgiTextureBytes : 0,
            DdgiBufferBytes = giUsesDdgi ? sceneData.DdgiBufferBytes : 0,
            AccelerationStructureBytes = sceneData.AccelerationStructureBytes,
            AccelerationStructureScratchBytes = sceneData.AccelerationStructureScratchBytes,
            AccelerationStructureInstanceBufferBytes = sceneData.AccelerationStructureInstanceBufferBytes,
            AccelerationStructureRayQueryMetadataBytes = sceneData.AccelerationStructureRayQueryMetadataBytes,
            AccelerationStructureBottomLevelCount = sceneData.AccelerationStructureBottomLevelCount,
            AccelerationStructureTopLevelInstanceCount = sceneData.AccelerationStructureTopLevelInstanceCount,
            AccelerationStructureBlasBuildCount = sceneData.AccelerationStructureBlasBuildCount,
            AccelerationStructureBlasCompactionQueryCount = sceneData.AccelerationStructureBlasCompactionQueryCount,
            AccelerationStructureBlasCompactionCount = sceneData.AccelerationStructureBlasCompactionCount,
            AccelerationStructureBlasCompactionSourceBytes = sceneData.AccelerationStructureBlasCompactionSourceBytes,
            AccelerationStructureBlasCompactionBytesSaved = sceneData.AccelerationStructureBlasCompactionBytesSaved,
            AccelerationStructureBlasCompactedResidentBytesSaved = sceneData.AccelerationStructureBlasCompactedResidentBytesSaved,
            AccelerationStructureBlasCompactionPendingCount = sceneData.AccelerationStructureBlasCompactionPendingCount,
            AccelerationStructureBlasCompactionQueryOverflowCount = sceneData.AccelerationStructureBlasCompactionQueryOverflowCount,
            AccelerationStructureBlasCompactionQueryReadbackFailureCount = sceneData.AccelerationStructureBlasCompactionQueryReadbackFailureCount,
            AccelerationStructureTlasBuildCount = sceneData.AccelerationStructureTlasBuildCount,
            AccelerationStructureTlasUpdateCount = sceneData.AccelerationStructureTlasUpdateCount,
            AccelerationStructureTlasSkipCount = sceneData.AccelerationStructureTlasSkipCount,
            AccelerationStructureStreamingEnabled = giUsesDdgi
                ? sceneData.AccelerationStructureStreamingEnabled
                : 0,
            AccelerationStructureStaticInstanceCandidateCount = sceneData.AccelerationStructureStaticInstanceCandidateCount,
            AccelerationStructureStaticInstanceResidentCount = sceneData.AccelerationStructureStaticInstanceResidentCount,
            AccelerationStructureStaticInstanceCulledCount = sceneData.AccelerationStructureStaticInstanceCulledCount,
            AccelerationStructureBlasEvictionCount = sceneData.AccelerationStructureBlasEvictionCount,
            AccelerationStructureBlasEvictionBytes = sceneData.AccelerationStructureBlasEvictionBytes,
            AccelerationStructureBlasBudgetRejectedCount = sceneData.AccelerationStructureBlasBudgetRejectedCount,
            AccelerationStructureDynamicBottomLevelCount =
                sceneData.AccelerationStructureDynamicBottomLevelCount,
            AccelerationStructureDynamicBlasBytes =
                sceneData.AccelerationStructureDynamicBlasBytes,
            AccelerationStructureDynamicBlasPeakBytes =
                sceneData.AccelerationStructureDynamicBlasPeakBytes,
            AccelerationStructureDynamicFullBuildCount =
                sceneData.AccelerationStructureDynamicFullBuildCount,
            AccelerationStructureDynamicRefitCount =
                sceneData.AccelerationStructureDynamicRefitCount,
            AccelerationStructureDynamicProxyFallbackCount =
                sceneData.AccelerationStructureDynamicProxyFallbackCount,
            AccelerationStructureDynamicExcludedCount =
                sceneData.AccelerationStructureDynamicExcludedCount,
            AccelerationStructureDynamicBudgetDeferredCount =
                sceneData.AccelerationStructureDynamicBudgetDeferredCount,
            AccelerationStructureDynamicTopologyMismatchCount =
                sceneData.AccelerationStructureDynamicTopologyMismatchCount,
            AccelerationStructureDynamicScratchBytes =
                sceneData.AccelerationStructureDynamicScratchBytes,
            AccelerationStructureDynamicPrimitiveCount =
                sceneData.AccelerationStructureDynamicPrimitiveCount,
            AccelerationStructureBlasBytes = sceneData.AccelerationStructureBlasBytes,
            AccelerationStructureTlasBytes = sceneData.AccelerationStructureTlasBytes,
            AccelerationStructureRetiredBytes = sceneData.AccelerationStructureRetiredBytes,
            AccelerationStructureResidentBytes = sceneData.AccelerationStructureResidentBytes,
            AccelerationStructureMemoryBudgetBytes = ddgiRequested && giSettings.StreamedGiAccelerationStructuresEnabled
                ? giSettings.GiAccelerationStructureMemoryBudgetBytes
                : 0UL,
            AccelerationStructureInstanceUploadBytes = sceneData.AccelerationStructureInstanceUploadBytes,
            AccelerationStructureRayQueryMetadataUploadBytes = sceneData.AccelerationStructureRayQueryMetadataUploadBytes,
            CpuAccelerationStructureBuildMicroseconds = sceneData.CpuAccelerationStructureBuildMicroseconds,
            CpuAccelerationStructureBlasBuildMicroseconds = sceneData.CpuAccelerationStructureBlasBuildMicroseconds,
            CpuAccelerationStructureBlasCompactionMicroseconds = sceneData.CpuAccelerationStructureBlasCompactionMicroseconds,
            CpuAccelerationStructureTlasBuildMicroseconds = sceneData.CpuAccelerationStructureTlasBuildMicroseconds,
            CpuAccelerationStructureInstanceUploadMicroseconds = sceneData.CpuAccelerationStructureInstanceUploadMicroseconds,
            GpuAccelerationStructureBlasMicroseconds = sceneData.GpuAccelerationStructureBlasMicroseconds,
            GpuAccelerationStructureTlasMicroseconds = sceneData.GpuAccelerationStructureTlasMicroseconds,
            AccelerationStructureFallbackReason = giUsesDdgi
                ? sceneData.AccelerationStructureFallbackReason
                : ddgiRequested && giSettings.EmergencyGiFallbackEnabled
                    ? "Emergency GI fallback is active."
                    : sceneData.AccelerationStructureFallbackReason,
            GeometryDecalsEnabled = sceneData.GeometryDecalsEnabled ? 1 : 0,
            DecalReceiveShadows = sceneData.DecalReceiveShadows ? 1 : 0,
            DecalReceiveGlobalIllumination =
                sceneData.DecalReceiveGlobalIllumination ? 1 : 0,
            GeometryDecalDepthBias = sceneData.GeometryDecalDepthBias,
            GeometryDecalSlopeScaledDepthBias = sceneData.GeometryDecalSlopeScaledDepthBias,
            SolidDepthMeshletDrawUploadBytes = sceneData.SolidDepthMeshletDrawUploadBytes,
            MaskedDepthMeshletDrawUploadBytes = sceneData.MaskedDepthMeshletDrawUploadBytes,
            MaterialExtensionUploadBytes = sceneData.MaterialExtensionUploadBytes,
            MaterialExtensionDataCount = sceneData.MaterialExtensionData.Count,
            MaterialDebugView = Settings.Materials.DebugView,
            MaterialCompileCount = materialDiagnostics.MaterialCompileCount,
            MaterialLastCompileMicroseconds = materialDiagnostics.LastCompileMicroseconds,
            MaterialTotalCompileMicroseconds = materialDiagnostics.TotalCompileMicroseconds,
            MaterialCompileP95Microseconds = materialDiagnostics.CompileP95Microseconds,
            MaterialCompileTimingSampleCount = materialDiagnostics.CompileTimingSampleCount,
            MaterialLastUploadMicroseconds = materialDiagnostics.LastUploadMicroseconds,
            MaterialUploadP95Microseconds = materialDiagnostics.UploadP95Microseconds,
            MaterialUploadTimingSampleCount = materialDiagnostics.UploadTimingSampleCount,
            MaterialLegacyV1FallbackCount = materialDiagnostics.LegacyV1FallbackCount,
            MaterialInvalidStatisticsCompileCount = materialDiagnostics.InvalidStatisticsCompileCount,
            MaterialActiveLegacyV1FallbackCount =
                materialDiagnostics.ActiveLegacyV1FallbackCount,
            MaterialActiveInvalidProfileCount =
                materialDiagnostics.ActiveInvalidProfileCount,
            MaterialActivePrimitiveProfileCount =
                materialDiagnostics.ActivePrimitiveProfileCount,
            MaterialPrimitiveProfileGpuBytes =
                materialDiagnostics.PrimitiveProfileGpuBytes,
            MaterialPrimitiveProfileAbsoluteBudgetBytes =
                materialDiagnostics.PrimitiveProfileGpuBudgetBytes,
            MaterialRevision = materialDiagnostics.MaterialRevision,
            MaterialTextureContentRevision =
                materialDiagnostics.TextureContentRevision,
            MaterialMaximumTransportProfileRevision =
                materialDiagnostics.MaximumTransportProfileRevision,
            MaterialGiV2ActiveFeatures = materialGiRollout.ActiveFeatures,
            MaterialGiRolloutMode = materialGiRollout.Mode,
            MaterialGiReleaseQualificationRequired =
                materialGiRollout.ReleaseQualificationRequired ? 1 : 0,
            MaterialGiReleaseQualified = materialGiRollout.ReleaseQualified ? 1 : 0,
            MaterialGiReleaseQualificationFailureCount =
                materialGiRollout.QualificationFailureCount,
            MaterialGiReleaseQualificationSummary =
                materialGiRollout.QualificationSummary,
            MaterialGiReleaseApprovalId = materialGiRollout.ApprovalId,
            MaterialGiReleaseEvidenceSha256 = materialGiRollout.EvidenceSha256,
            MaterialGiQualifiedDeviceCount = materialGiRollout.QualifiedDeviceCount,
            MaterialGiV1RemovalOwner = materialGiRollout.V1RemovalOwner,
            MaterialGiV1RemovalTargetDate =
                materialGiRollout.V1RemovalTargetDate.ToString("yyyy-MM-dd"),
            MaterialTrackedTextureDependencyCount = materialDiagnostics.TrackedTextureDependencyCount,
            MaterialEstimatedAlphaCandidateTestCount = _completedMaterialGiCounters.EstimatedAlphaCandidateTestCount,
            MaterialEstimatedAlphaCandidateRejectCount = _completedMaterialGiCounters.EstimatedAlphaCandidateRejectCount,
            MaterialNonFiniteValueCount = _completedMaterialGiCounters.NonFiniteMaterialOrRadianceCount,
            MaterialClampedValueCount = _completedMaterialGiCounters.ClampedMaterialOrRadianceCount,
            FogRequestedTechnique = sceneData.FogRequestedTechnique,
            FogEffectiveTechnique = sceneData.FogEffectiveTechnique,
            VolumetricFogStatus = sceneData.VolumetricFogStatus,
            VolumetricFogGridWidth = sceneData.VolumetricFogGridWidth,
            VolumetricFogGridHeight = sceneData.VolumetricFogGridHeight,
            VolumetricFogGridDepth = sceneData.VolumetricFogGridDepth,
            VolumetricFogClusterCount = sceneData.VolumetricFogClusterCount,
            VolumetricFogAllocatedBytes = sceneData.VolumetricFogAllocatedBytes,
            VolumetricFogLocalVolumeCount = sceneData.VolumetricFogLocalVolumeCount,
            VolumetricFogParticleSourceCount = sceneData.VolumetricFogParticleSourceCount,
            VolumetricFogParticleCandidateCount =
                sceneData.VolumetricFogParticleCandidateCount,
            VolumetricFogParticleAdmittedCount =
                sceneData.VolumetricFogParticleAdmittedCount,
            VolumetricFogMultipleScatteringIterations =
                sceneData.VolumetricFogMultipleScatteringIterations,
            VolumetricFogHistoryValid = sceneData.VolumetricFogHistoryValid ? 1 : 0,
            VolumetricFogHistoryRejected = sceneData.VolumetricFogHistoryRejected ? 1 : 0,
            VolumetricFogDirectionalL2Active =
                sceneData.VolumetricFogDirectionalL2Active ? 1 : 0,
            VolumetricFogEnergyOwnershipSeparated =
                sceneData.VolumetricFogEnergyOwnershipSeparated ? 1 : 0,
            VolumetricFogOutputReadbackValid =
                sceneData.VolumetricFogOutputReadbackValid ? 1 : 0,
            VolumetricFogOutputProduced =
                sceneData.VolumetricFogOutputProduced ? 1 : 0,
            VolumetricFogDiagnosticSampleCount =
                sceneData.VolumetricFogDiagnosticSampleCount,
            VolumetricFogMediumNonEmptyFroxelCount =
                sceneData.VolumetricFogMediumNonEmptyFroxelCount,
            VolumetricFogDirectNonZeroFroxelCount =
                sceneData.VolumetricFogDirectNonZeroFroxelCount,
            VolumetricFogIndirectNonZeroFroxelCount =
                sceneData.VolumetricFogIndirectNonZeroFroxelCount,
            VolumetricFogDdgiSupportedFroxelCount =
                sceneData.VolumetricFogDdgiSupportedFroxelCount,
            VolumetricFogHistoryAcceptedFroxelCount =
                sceneData.VolumetricFogHistoryAcceptedFroxelCount,
            VolumetricFogHistoryRejectedFroxelCount =
                sceneData.VolumetricFogHistoryRejectedFroxelCount,
            VolumetricFogHistoryRejectedInvalidFroxelCount =
                sceneData.VolumetricFogHistoryRejectedInvalidFroxelCount,
            VolumetricFogHistoryRejectedBoundsFroxelCount =
                sceneData.VolumetricFogHistoryRejectedBoundsFroxelCount,
            VolumetricFogHistoryRejectedExtinctionFroxelCount =
                sceneData.VolumetricFogHistoryRejectedExtinctionFroxelCount,
            VolumetricFogHistoryRejectedRadianceFroxelCount =
                sceneData.VolumetricFogHistoryRejectedRadianceFroxelCount,
            VolumetricFogHistoryRejectedVelocityFroxelCount =
                sceneData.VolumetricFogHistoryRejectedVelocityFroxelCount,
            VolumetricFogClusterOverflowCount =
                sceneData.VolumetricFogClusterOverflowCount,
            VolumetricFogNonFiniteCount =
                sceneData.VolumetricFogNonFiniteCount,
            VolumetricFogMaximumExtinction =
                sceneData.VolumetricFogMaximumExtinction,
            VolumetricFogMeanExtinction =
                sceneData.VolumetricFogMeanExtinction,
            VolumetricFogMaximumDirectLuminance =
                sceneData.VolumetricFogMaximumDirectLuminance,
            VolumetricFogMeanDirectLuminance =
                sceneData.VolumetricFogMeanDirectLuminance,
            VolumetricFogMaximumIndirectLuminance =
                sceneData.VolumetricFogMaximumIndirectLuminance,
            VolumetricFogMeanIndirectLuminance =
                sceneData.VolumetricFogMeanIndirectLuminance,
            VolumetricFogMinimumTransmittance =
                sceneData.VolumetricFogMinimumTransmittance,
            VolumetricFogMeanTransmittance =
                sceneData.VolumetricFogMeanTransmittance,
            GpuVolumetricFogNoiseMicroseconds =
                sceneData.GpuVolumetricFogNoiseMicroseconds,
            GpuVolumetricFogSourceCullMicroseconds =
                sceneData.GpuVolumetricFogSourceCullMicroseconds,
            GpuVolumetricFogMediumMicroseconds =
                sceneData.GpuVolumetricFogMediumMicroseconds,
            GpuVolumetricFogTransmittanceMicroseconds =
                sceneData.GpuVolumetricFogTransmittanceMicroseconds,
            GpuVolumetricFogDdgiBounceMicroseconds =
                sceneData.GpuVolumetricFogDdgiBounceMicroseconds,
            GpuVolumetricFogLightingCacheMicroseconds =
                sceneData.GpuVolumetricFogLightingCacheMicroseconds,
            GpuVolumetricFogMultipleScatteringMicroseconds =
                sceneData.GpuVolumetricFogMultipleScatteringMicroseconds,
            GpuVolumetricFogTemporalMicroseconds =
                sceneData.GpuVolumetricFogTemporalMicroseconds,
            GpuVolumetricFogIntegrateMicroseconds =
                sceneData.GpuVolumetricFogIntegrateMicroseconds,
            GpuVolumetricFogResolveMicroseconds =
                sceneData.GpuVolumetricFogResolveMicroseconds,
            GpuVolumetricFogCompositeMicroseconds =
                sceneData.GpuVolumetricFogCompositeMicroseconds,
            MaterialAlphaCandidateLimitReachedCount = _completedMaterialGiCounters.AlphaCandidateLimitReachedCount,
            MaterialEstimatedDetailedTransportHitCount =
                _completedMaterialGiCounters.EstimatedDetailedTransportHitCount,
            MaterialEstimatedCompactTransportHitCount =
                _completedMaterialGiCounters.EstimatedCompactTransportHitCount,
            MaterialEstimatedCorrectnessFallbackHitCount =
                _completedMaterialGiCounters.EstimatedCorrectnessFallbackHitCount,
            MaterialEstimatedFarFieldTransportHitCount =
                _completedMaterialGiCounters.EstimatedFarFieldTransportHitCount,
            FarFieldMaterialConflictCount = _completedFarFieldMaterialV2Counters.ConflictCount,
            FarFieldStalePublicationRejectCount = (uint)Math.Min(
                (ulong)_completedFarFieldMaterialV2Counters.StalePublicationRejectCount +
                (ulong)Math.Max(_farFieldClipmapManager?.StalePublicationRejectCount ?? 0, 0),
                uint.MaxValue),
            AutoExposureEnabled = sceneData.AutoExposureEnabled ? 1 : 0,
            AutoExposureAverageLuminance = sceneData.AutoExposureAverageLuminance,
            AutoExposureTargetExposure = sceneData.AutoExposureTargetExposure,
            AutoExposureSampleCount = sceneData.AutoExposureSampleCount,
            CpuAutoExposureRecordMicroseconds = sceneData.CpuAutoExposureRecordMicroseconds,
            GpuAutoExposureMicroseconds = sceneData.GpuAutoExposureMicroseconds,
            AnimationEnabled = Settings.Animation.Enabled ? 1 : 0,
            AnimationSkinningMode = Settings.Animation.Enabled ? Settings.Animation.SkinningMode : AnimationSkinningMode.Disabled,
            AnimationDebugView = Settings.Animation.DebugView,
            AnimatedModelCount = sceneData.AnimatedModelCount,
            SkinnedObjectCount = sceneData.SkinnedObjectCount,
            SkeletonCount = sceneData.SkeletonCount,
            SkinCount = sceneData.SkinCount,
            AnimationClipCount = sceneData.AnimationClipCount,
            ActiveAnimatorCount = sceneData.ActiveAnimatorCount,
            PlayingAnimatorCount = sceneData.PlayingAnimatorCount,
            PausedAnimatorCount = sceneData.PausedAnimatorCount,
            SkinnedVertexCount = sceneData.SkinnedVertexCount,
            SkinningDispatchCount = sceneData.SkinningDispatchCount,
            JointMatrixCount = sceneData.JointMatrixCount,
            MaxJointsPerSkeleton = Settings.Animation.MaxJointsPerSkeleton,
            CpuAnimationSampleMicroseconds = sceneData.CpuAnimationSampleMicroseconds,
            CpuSkinMatrixUploadMicroseconds = sceneData.CpuSkinMatrixUploadMicroseconds,
            CpuSkinningRecordMicroseconds = sceneData.CpuSkinningRecordMicroseconds,
            GpuSkinningMicroseconds = sceneData.GpuSkinningMicroseconds,
            SkinningUploadBytes = sceneData.SkinningUploadBytes,
            SkinMatrixBufferSize = sceneData.SkinMatrixBufferSize,
            SkinnedVertexBufferSize = sceneData.SkinnedVertexBufferSize,
            AnimatedBoundsMode = sceneData.AnimatedBoundsMode,
            ParticlesEnabled = sceneData.ParticlesEnabled ? 1 : 0,
            ParticleSimulationMode = sceneData.ParticleSimulationMode,
            ParticleDebugView = sceneData.ParticleDebugView,
            ParticleEffectCount = sceneData.ParticleEffectCount,
            ParticleEmitterCount = sceneData.ParticleEmitterCount,
            LiveParticleCount = sceneData.LiveParticleCount,
            SimulatedParticleCount = sceneData.SimulatedParticleCount,
            CulledParticleCount = sceneData.CulledParticleCount,
            RenderedParticleCount = sceneData.RenderedParticleCount,
            ParticleBatchCount = sceneData.ParticleBatchCount,
            AlphaParticleCount = sceneData.AlphaParticleCount,
            AdditiveParticleCount = sceneData.AdditiveParticleCount,
            SoftParticleCount = sceneData.SoftParticleCount,
            FlipbookParticleCount = sceneData.FlipbookParticleCount,
            TrailCount = sceneData.TrailCount,
            TrailSegmentCount = sceneData.TrailSegmentCount,
            BeamCount = sceneData.BeamCount,
            ParticleBudgetExceeded = sceneData.ParticleBudgetExceeded,
            ParticleUploadBudgetExceeded = sceneData.ParticleUploadBudgetExceeded,
            ParticleInstanceUploadBytes = sceneData.ParticleInstanceUploadBytes,
            TrailBeamUploadBytes = sceneData.TrailBeamUploadBytes,
            CpuParticleSimulationMicroseconds = sceneData.CpuParticleSimulationMicroseconds,
            CpuParticleBuildMicroseconds = sceneData.CpuParticleBuildMicroseconds,
            CpuParticleRecordMicroseconds = sceneData.CpuParticleRecordMicroseconds,
            CpuGpuParticleResetRecordMicroseconds = sceneData.CpuGpuParticleResetRecordMicroseconds,
            CpuGpuParticleEmitterUploadMicroseconds = sceneData.CpuGpuParticleEmitterUploadMicroseconds,
            CpuGpuParticleSimulateRecordMicroseconds = sceneData.CpuGpuParticleSimulateRecordMicroseconds,
            CpuTrailBeamRecordMicroseconds = sceneData.CpuTrailBeamRecordMicroseconds,
            GpuParticleMicroseconds = sceneData.GpuParticleMicroseconds,
            GpuTrailBeamMicroseconds = sceneData.GpuTrailBeamMicroseconds,
            ParticleDrawCallCount = sceneData.ParticleDrawCallCount,
            ParticleInstanceBufferSize = sceneData.ParticleInstanceBufferSize,
            ParticleBatchBufferSize = sceneData.ParticleBatchBufferSize,
            ParticleFrameDataBufferSize = sceneData.ParticleFrameDataBufferSize,
            GpuParticlesEnabled = sceneData.GpuParticlesEnabled,
            GpuParticleCapacity = sceneData.GpuParticleCapacity,
            GpuParticleEmitterCapacity = sceneData.GpuParticleEmitterCapacity,
            GpuParticleDrawCapacity = sceneData.GpuParticleDrawCapacity,
            GpuParticleResetRequired = sceneData.GpuParticleResetRequired,
            GpuParticleEmitterCount = sceneData.GpuParticleEmitterCount,
            GpuParticleMaxSpawnPerEmitter = sceneData.GpuParticleMaxSpawnPerEmitter,
            GpuParticleDeltaSeconds = sceneData.GpuParticleDeltaSeconds,
            GpuParticleEmitterUploadBytes = sceneData.GpuParticleEmitterUploadBytes,
            GpuParticleCountersReadbackValid = sceneData.GpuParticleCountersReadbackValid,
            GpuParticleAliveCount = sceneData.GpuParticleAliveCount,
            GpuParticleDeadCount = sceneData.GpuParticleDeadCount,
            GpuParticleSpawnedCount = sceneData.GpuParticleSpawnedCount,
            GpuParticleKilledCount = sceneData.GpuParticleKilledCount,
            GpuParticleCulledCount = sceneData.GpuParticleCulledCount,
            GpuParticleRenderedCount = sceneData.GpuParticleRenderedCount,
            GpuParticleDroppedSpawnCount = sceneData.GpuParticleDroppedSpawnCount,
            GpuParticleBlendBucket0Count = sceneData.GpuParticleBlendBucket0Count,
            GpuParticleBlendBucket1Count = sceneData.GpuParticleBlendBucket1Count,
            GpuParticleBlendBucket2Count = sceneData.GpuParticleBlendBucket2Count,
            GpuParticleBlendBucket3Count = sceneData.GpuParticleBlendBucket3Count,
            GpuParticleBlendBucket4Count = sceneData.GpuParticleBlendBucket4Count,
            ParticleDdgiSampleCount = sceneData.ParticleDdgiSampleCount,
            VfxDdgiDirtyProbeEventCount = sceneData.VfxDdgiDirtyProbeEventCount,
            FoliagePatchCount = sceneData.FoliagePatchCount,
            FoliagePrototypeCount = sceneData.FoliagePrototypeCount,
            FoliageClusterCount = sceneData.FoliageClusterCount,
            FoliageVisibleClusterCount = sceneData.FoliageVisibleClusterCount,
            FoliageCulledClusterCount = sceneData.FoliageCulledClusterCount,
            FoliageVisibleMeshletDrawCount = sceneData.FoliageVisibleMeshletDrawCount,
            FoliageDdgiSampleCount = sceneData.FoliageDdgiSampleCount,
            FoliageDdgiTransportExcludedClusterCount = giUsesDdgi &&
                (sceneData.DdgiFoliageGeometryMode ==
                    DdgiFoliageGeometryMode.Excluded ||
                 !string.IsNullOrWhiteSpace(
                     sceneData.DdgiFoliageProxyFallbackReason))
                ? sceneData.FoliageClusterCount
                : 0,
            FoliageDdgiTransportExclusionReason = !giUsesDdgi ||
                sceneData.FoliageClusterCount == 0
                    ? string.Empty
                    : !string.IsNullOrWhiteSpace(
                        sceneData.DdgiFoliageProxyFallbackReason)
                        ? sceneData.DdgiFoliageProxyFallbackReason
                        : sceneData.DdgiFoliageGeometryMode ==
                            DdgiFoliageGeometryMode.Excluded
                            ? AccelerationStructureManager
                                .FoliageDdgiExclusionReason
                            : string.Empty,
            FoliageGrassBladeEstimate = sceneData.FoliageGrassBladeEstimate,
            FoliageLod0VisibleCount = sceneData.FoliageLod0VisibleCount,
            FoliageLod1VisibleCount = sceneData.FoliageLod1VisibleCount,
            FoliageLod2VisibleCount = sceneData.FoliageLod2VisibleCount,
            FoliageHiZTestedCount = sceneData.FoliageHiZTestedCount,
            FoliageHiZRejectedCount = sceneData.FoliageHiZRejectedCount,
            FoliageOverflowCount = sceneData.FoliageOverflowCount,
            FoliageMeshletDrawOverflowCount = sceneData.FoliageMeshletDrawOverflowCount,
            FoliageFarImpostorVisibleCount = sceneData.FoliageFarImpostorVisibleCount,
            FoliageDensityRejectedCount = sceneData.FoliageDensityRejectedCount,
            FoliageMissingDensityTextureCount =
                sceneData.FoliageMissingDensityTextureCount,
            FoliageMissingImpostorCount =
                sceneData.FoliageMissingImpostorCount,
            FoliageResidentCellCount = sceneData.FoliageResidentCellCount,
            FoliagePendingCellCount = sceneData.FoliagePendingCellCount,
            FoliageRetiringCellCount = sceneData.FoliageRetiringCellCount,
            FoliageNearCellCount = sceneData.FoliageNearCellCount,
            FoliageMidCellCount = sceneData.FoliageMidCellCount,
            FoliageFarCellCount = sceneData.FoliageFarCellCount,
            FoliageCellLoadsThisFrame =
                sceneData.FoliageCellLoadsThisFrame,
            FoliageCellRetirementsThisFrame =
                sceneData.FoliageCellRetirementsThisFrame,
            FoliageCellStreamingOverflowCount =
                sceneData.FoliageCellStreamingOverflowCount,
            FoliageCellStreamingUploadBytes =
                sceneData.FoliageCellStreamingUploadBytes,
            FoliageIndirectMeshletDispatchEnabled = sceneData.FoliageIndirectMeshletDispatchEnabled,
            FoliageInstanceBufferBytes = sceneData.FoliageInstanceBufferBytes,
            FoliageClusterBufferBytes = sceneData.FoliageClusterBufferBytes,
            DdgiFoliageGeometryMode = sceneData.DdgiFoliageGeometryMode,
            DdgiFoliageProxyVertexCount =
                sceneData.DdgiFoliageProxyVertexCount,
            DdgiFoliageProxyCardCount =
                sceneData.DdgiFoliageProxyCardCount,
            DdgiFoliageProxyTriangleCount =
                sceneData.DdgiFoliageProxyTriangleCount,
            DdgiFoliageAuthoredInstanceCount =
                sceneData.DdgiFoliageAuthoredInstanceCount,
            DdgiFoliageGeneratedInstanceCount =
                sceneData.DdgiFoliageGeneratedInstanceCount,
            DdgiFoliageDroppedTriangleCount =
                sceneData.DdgiFoliageDroppedTriangleCount,
            DdgiFoliageRepresentedBladeCount =
                sceneData.DdgiFoliageRepresentedBladeCount,
            DdgiFoliageProxyUpdatedThisFrame =
                sceneData.DdgiFoliageProxyUpdatedThisFrame,
            DdgiFoliageProxyUploadBytes =
                sceneData.DdgiFoliageProxyUploadBytes,
            DdgiFoliageProxyVertexBufferBytes =
                sceneData.DdgiFoliageProxyVertexBufferBytes,
            DdgiFoliageProxyIndexBufferBytes =
                sceneData.DdgiFoliageProxyIndexBufferBytes,
            DdgiFoliageProxyPatchBufferBytes =
                sceneData.DdgiFoliageProxyPatchBufferBytes,
            DdgiFoliageProxyContentSignature =
                sceneData.DdgiFoliageProxyContentSignature,
            DdgiFoliageProxyCadenceGeneration =
                sceneData.DdgiFoliageProxyCadenceGeneration,
            CpuDdgiFoliageProxyBuildMicroseconds =
                sceneData.CpuDdgiFoliageProxyBuildMicroseconds,
            CpuDdgiFoliageProxyUploadMicroseconds =
                sceneData.CpuDdgiFoliageProxyUploadMicroseconds,
            CpuDdgiFoliageProxyGenerationRecordMicroseconds =
                sceneData.CpuDdgiFoliageProxyGenerationRecordMicroseconds,
            GpuDdgiFoliageProxyGenerationMicroseconds =
                sceneData.GpuDdgiFoliageProxyGenerationMicroseconds,
            DdgiFoliageProxyRequestedRepresentedInstanceCount =
                sceneData.DdgiFoliageProxyRequestedRepresentedInstanceCount,
            DdgiFoliageProxyDensityError =
                sceneData.DdgiFoliageProxyDensityError,
            DdgiFoliageProxyWindAgeSeconds =
                sceneData.DdgiFoliageProxyWindAgeSeconds,
            DdgiFoliageProxyNearCardCount =
                sceneData.DdgiFoliageProxyNearCardCount,
            DdgiFoliageProxyMidCardCount =
                sceneData.DdgiFoliageProxyMidCardCount,
            DdgiFoliageProxyFarCardCount =
                sceneData.DdgiFoliageProxyFarCardCount,
            DdgiFoliageProxyExcludedPatchCount =
                sceneData.DdgiFoliageProxyExcludedPatchCount,
            DdgiFoliageProxyLodPolicyVersion =
                sceneData.DdgiFoliageProxyLodPolicyVersion,
            DdgiFoliageProxyFallbackReason =
                sceneData.DdgiFoliageProxyFallbackReason,
            FoliageDrawBufferBytes = sceneData.FoliageDrawBufferBytes,
            FoliageImpostorAtlasBytes = sceneData.FoliageImpostorAtlasBytes,
            FoliageDensityTextureBytes = sceneData.FoliageDensityTextureBytes,
            CpuFoliageBuildMicroseconds = sceneData.CpuFoliageBuildMicroseconds,
            CpuFoliageUploadMicroseconds = sceneData.CpuFoliageUploadMicroseconds,
            GpuFoliageCullMicroseconds = sceneData.GpuFoliageCullMicroseconds,
            GpuFoliageDepthMicroseconds = sceneData.GpuFoliageDepthMicroseconds,
            GpuFoliageForwardMicroseconds = sceneData.GpuFoliageForwardMicroseconds,
            GpuFoliageShadowMicroseconds = sceneData.GpuFoliageShadowMicroseconds,
            GpuParticleStateBufferSize = sceneData.GpuParticleStateBufferSize,
            GpuParticleAliveIndexBufferSize = sceneData.GpuParticleAliveIndexBufferSize,
            GpuParticleDeadIndexBufferSize = sceneData.GpuParticleDeadIndexBufferSize,
            GpuParticleEmitterBufferSize = sceneData.GpuParticleEmitterBufferSize,
            GpuParticleCurveSampleBufferSize = sceneData.GpuParticleCurveSampleBufferSize,
            GpuParticleCounterBufferSize = sceneData.GpuParticleCounterBufferSize,
            GpuParticleUnsortedRenderInstanceBufferSize = sceneData.GpuParticleUnsortedRenderInstanceBufferSize,
            GpuParticleRenderInstanceBufferSize = sceneData.GpuParticleRenderInstanceBufferSize,
            GpuParticleIndirectDrawBufferSize = sceneData.GpuParticleIndirectDrawBufferSize,
            GpuParticleSortKeyBufferSize = sceneData.GpuParticleSortKeyBufferSize,
            DebugToolingEnabled = sceneData.DebugToolingEnabled ? 1 : 0,
            DebugOverlayEnabled = sceneData.DebugToolingEnabled &&
                DebugOverlayCatalog.ResolveRendererMode(sceneData.DebugOverlayMode) !=
                    DebugOverlayMode.None
                        ? 1
                        : 0,
            DebugOverlayMode = sceneData.DebugOverlayMode,
            DebugOverlayStatus = sceneData.DebugOverlayStatus,
            CpuDebugSnapshotsEnabled = sceneData.CpuDebugSnapshotsEnabled ? 1 : 0,
            DebugSelectedObjectIndex = sceneData.DebugSelectedObjectIndex,
            DebugSelectedObjectName = sceneData.DebugSelectedObjectName,
            DebugDrawEnabled = input.Tooling.DebugDrawEnabled ? 1 : 0,
            DebugDrawLineCount = sceneData.DebugDrawSnapshot.LineCount,
            DebugDrawPersistentLineCount = sceneData.DebugDrawSnapshot.PersistentLineCount,
            DebugDrawDroppedLineCount = sceneData.DebugDrawSnapshot.DroppedLineCount,
            CpuDebugDrawBuildMicroseconds = sceneData.CpuDebugDrawBuildMicroseconds,
            CpuDebugDrawRecordMicroseconds = sceneData.CpuDebugDrawRecordMicroseconds,
            GpuDebugDrawMicroseconds = sceneData.GpuDebugDrawMicroseconds,
            CpuDebugOverlayRecordMicroseconds = sceneData.CpuDebugOverlayRecordMicroseconds,
            GpuDebugOverlayMicroseconds = sceneData.GpuDebugOverlayMicroseconds,
            GpuDebugDdgiProbeMicroseconds =
                sceneData.GpuDebugDdgiProbeMicroseconds,
            GpuDebugLightTileMicroseconds =
                sceneData.GpuDebugLightTileMicroseconds,
            DebugLightTileMaxCount = sceneData.MaxLightsInAnyTile,
            DebugLightTileAverageCount = sceneData.AverageLightsPerNonEmptyTile,
            DebugDirectionalShadowCascadesDrawn = sceneData.DebugDirectionalShadowCascadesDrawn,
            DebugObjectBoundsDrawn = sceneData.DebugObjectBoundsDrawn,
            DebugMeshletBoundsDrawn = sceneData.DebugMeshletBoundsDrawn,
            DebugMeshletBoundsDropped = sceneData.DebugMeshletBoundsDropped,
            DebugMeshletBoundsItemCapDropped =
                sceneData.DebugMeshletBoundsItemCapDropped,
            DebugMeshletBoundsLineBudgetDropped =
                sceneData.DebugMeshletBoundsLineBudgetDropped,
            DebugReflectionProbeVolumesDrawn = sceneData.DebugReflectionProbeVolumesDrawn,
            DebugDdgiProbeVolumesDrawn = sceneData.DebugDdgiProbeVolumesDrawn,
            DebugDdgiRequestedSamples = sceneData.DebugDdgiRequestedSamples,
            DebugDdgiProbeMarkersDrawn = sceneData.DebugDdgiProbeMarkersDrawn,
            DebugDdgiProbeMarkersFiltered = sceneData.DebugDdgiProbeMarkersFiltered,
            DebugDdgiNonresidentMarkers = sceneData.DebugDdgiNonresidentMarkers,
            DebugDdgiStaleMappings = sceneData.DebugDdgiStaleMappings,
            DebugDdgiStateUnavailableMarkers = sceneData.DebugDdgiStateUnavailableMarkers,
            DebugDdgiInvalidTransactions = sceneData.DebugDdgiInvalidTransactions,
            DebugDdgiSphereLineSegments = sceneData.DebugDdgiSphereLineSegments,
            DebugDdgiProbeMarkersDropped = sceneData.DebugDdgiProbeMarkersDropped,
            DebugDdgiGpuCountersValid = sceneData.DebugDdgiGpuCountersValid ? 1 : 0,
            DebugDdgiUpdateReasonCounts = sceneData.DebugDdgiUpdateReasonCounts,
            DebugDecalVolumesDrawn = sceneData.DebugDecalVolumesDrawn,
            GpuTimingSupported = _gpuTimestamps.Supported ? 1 : 0,
            GpuTimingEnabled = Settings.Debug.AllowGpuTiming ? 1 : 0,
            GpuTimingPending = _gpuTimestamps.PendingThisFrame ? 1 : 0,
            GpuTimestampPeriodNanoseconds = _context.TimestampPeriodNanoseconds,
            GpuTimingFrameLatency = FramesInFlight,
            GpuTimingUnavailableReason = input.Execution.GpuTimingReason,
            CpuHiZDepthTransitionMicroseconds = sceneData.CpuHiZDepthTransitionMicroseconds,
            CpuHiZPyramidTransitionMicroseconds = sceneData.CpuHiZPyramidTransitionMicroseconds,
            CpuHiZDescriptorBindMicroseconds = sceneData.CpuHiZDescriptorBindMicroseconds,
            CpuHiZPushDispatchMicroseconds = sceneData.CpuHiZPushDispatchMicroseconds,
            CpuHiZFinalBarrierMicroseconds = sceneData.CpuHiZFinalBarrierMicroseconds,
            ForwardMeshletsSubmittedCpu = sceneData.MeshletCountSubmittedCpu,
            ForwardGpuOcclusionRejectedMeshlets = forwardOcclusionRejected,
            ForwardGpuOcclusionCountersReconciled = forwardOcclusionCountersReconciled ? 1 : 0,
            ForwardGpuOcclusionSanity = forwardOcclusionSanity,
            HiZConsumerCount = sceneData.HiZConsumerCount,
            HiZConsumerSummary = sceneData.HiZConsumerSummary,
            HiZBuildSkippedBecauseNoConsumer = sceneData.HiZBuildSkippedBecauseNoConsumer ? 1 : 0,
            HiZCounterSource = sceneData.HiZCounterSource,
            ForwardHiZTestedCount = sceneData.ForwardHiZTestedCount,
            ForwardHiZCulledCount = sceneData.ForwardHiZCulledCount,
            ForwardHiZCullRate = sceneData.ForwardHiZCullRate,
            HiZFallbackPath = sceneData.HiZFallbackPath,
            HiZFallbackReason = sceneData.HiZFallbackReason,
            HiZValidateAgainstLegacyPath = sceneData.HiZValidateAgainstLegacyPath ? 1 : 0,
            PreviousHiZFrameValid = sceneData.PreviousHiZFrameValid ? 1 : 0,
            PreviousHiZSkippedInvalidHistory = sceneData.PreviousHiZSkippedInvalidHistory,
            PreviousHiZSkippedCameraMotion = sceneData.PreviousHiZSkippedCameraMotion,
            PreviousHiZTested = sceneData.PreviousHiZTested,
            PreviousHiZCulled = sceneData.PreviousHiZCulled,
            ForwardVisibilityCompactionEnabled = sceneData.ForwardVisibilityCompactionEnabled ? 1 : 0,
            ForwardVisibilityCompactionActive = sceneData.ForwardVisibilityCompactionActive ? 1 : 0,
            ForwardVisibilityCompactionSkipReason = sceneData.ForwardVisibilityCompactionSkipReason,
            ForwardVisibilitySimpleCapacity =
                sceneData.ForwardVisibilitySimpleCapacity,
            ForwardVisibilitySimpleNormalCapacity =
                sceneData.ForwardVisibilitySimpleNormalCapacity,
            ForwardVisibilityFullCapacity =
                sceneData.ForwardVisibilityFullCapacity,
            ForwardVisibilityCounterReadbackValid =
                sceneData.ForwardVisibilityCounterReadbackValid,
            ForwardVisibilityCandidateCount =
                sceneData.ForwardVisibilityCandidateCount,
            ForwardVisibilityEmittedCount =
                sceneData.ForwardVisibilityEmittedCount,
            ForwardVisibilityOverflowCount =
                sceneData.ForwardVisibilityOverflowCount,
            CurrentFrameHiZTested = sceneData.CurrentFrameHiZTested,
            CurrentFrameHiZCulled = sceneData.CurrentFrameHiZCulled,
            HiZPolicyStatus = sceneData.HiZPolicyStatus,
            HiZPolicyReason = sceneData.HiZPolicyReason,
            HiZPolicyWarmupFramesRemaining = sceneData.HiZPolicyWarmupFramesRemaining,
            HiZPolicySceneChanged = sceneData.HiZPolicySceneChanged,
            HiZPolicyCameraCut = sceneData.HiZPolicyCameraCut,
            HiZPolicyPyramidInvalidated = sceneData.HiZPolicyPyramidInvalidated,
            HiZPolicyAdaptiveSuppressed = sceneData.HiZPolicyAdaptiveSuppressed,
            HiZPolicyAdaptiveProbe = sceneData.HiZPolicyAdaptiveProbe,
            HiZPolicyAdaptiveProbeCountdown = sceneData.HiZPolicyAdaptiveProbeCountdown,
            HiZPolicyAdaptiveMeasuredOcclusionTests = sceneData.HiZPolicyAdaptiveMeasuredOcclusionTests,
            HiZPolicyAdaptiveMeasuredOcclusionCulled = sceneData.HiZPolicyAdaptiveMeasuredOcclusionCulled,
            HiZPolicyAdaptiveCullRate = sceneData.HiZPolicyAdaptiveCullRate,
            HiZPolicyCounterSource = sceneData.HiZPolicyCounterSource,
            HiZPolicyAdaptiveEstimatedSavedMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedSavedMicroseconds,
            HiZPolicyAdaptiveEstimatedCostMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedCostMicroseconds,
            HiZPolicyAdaptiveEstimatedNetMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedNetMicroseconds,
            HiZPolicyAdaptiveSmoothedCullRate = sceneData.HiZPolicyAdaptiveSmoothedCullRate,
            HiZPolicyAdaptiveSmoothedSavedToCostRatio = sceneData.HiZPolicyAdaptiveSmoothedSavedToCostRatio,
            HiZPolicyAdaptiveSuppressedFrameCount = sceneData.HiZPolicyAdaptiveSuppressedFrameCount,
            HiZPolicyAdaptiveStatus = sceneData.HiZPolicyAdaptiveStatus,
            GpuMeshletCountersEnabled = gpuMeshletCountersEnabled ? 1 : 0,
            GpuMeshletCountersStatus = gpuMeshletCountersStatus,
            MeshShaderRequestedMode =
                _meshPipeline.MeshShaderSelection.RequestedMode,
            MeshShaderSelectedMode =
                _meshPipeline.MeshShaderSelection.Permutation.Mode,
            MeshShaderTaskless =
                _meshPipeline.TasklessSubmissionEnabled ? 1 : 0,
            MeshShaderMaximumVertices =
                _meshPipeline.MeshShaderSelection.Permutation.MaximumVertices,
            MeshShaderMaximumPrimitives =
                _meshPipeline.MeshShaderSelection.Permutation.MaximumPrimitives,
            MeshShaderWorkgroupSize =
                _meshPipeline.MeshShaderSelection.Permutation.WorkgroupSize,
            MeshShaderFallbackReason =
                _meshPipeline.MeshShaderSelection.FallbackReason,
            DeviceMaximumMeshWorkgroupInvocations =
                _context.MeshShaderDeviceProperties
                    .MaximumMeshWorkGroupInvocations,
            DeviceMaximumMeshOutputVertices =
                _context.MeshShaderDeviceProperties.MaximumMeshOutputVertices,
            DeviceMaximumMeshOutputPrimitives =
                _context.MeshShaderDeviceProperties.MaximumMeshOutputPrimitives,
            DevicePrefersLocalInvocationVertexOutput =
                _context.MeshShaderDeviceProperties
                    .PrefersLocalInvocationVertexOutput ? 1 : 0,
            DevicePrefersLocalInvocationPrimitiveOutput =
                _context.MeshShaderDeviceProperties
                    .PrefersLocalInvocationPrimitiveOutput ? 1 : 0,
            DevicePrefersCompactVertexOutput =
                _context.MeshShaderDeviceProperties
                    .PrefersCompactVertexOutput ? 1 : 0,
            DevicePrefersCompactPrimitiveOutput =
                _context.MeshShaderDeviceProperties
                    .PrefersCompactPrimitiveOutput ? 1 : 0,
            MeshletPhysicalResidencyConfigured =
                sceneSubmissionSettings.GpuMeshletStreamingEnabled ? 1 : 0,
            MeshletPhysicalResidencyAvailable =
                meshletResidency?.Available == true &&
                meshletVulkanResidency?.Initialized == true ? 1 : 0,
            MeshletPhysicalResidencyActive =
                meshletResidency?.Active == true ? 1 : 0,
            MeshletPhysicalResidencyDegraded =
                meshletResidency?.Degraded == true ||
                meshletResidencyReloadRequired ||
                meshletVulkanResidency?.FailedPageRecordCount > 0 ||
                meshletVulkanResidency?
                    .LastCompletedFrameInvalidShaderMappingCount > 0
                    ? 1
                    : 0,
            MeshletPhysicalResidencyReloadRequired =
                meshletResidencyReloadRequired ? 1 : 0,
            MeshletPhysicalResidencyActivePackageCount =
                meshletResidency?.PackageCount ?? 0,
            MeshletPhysicalResidencyActiveSubMeshCount =
                meshletResidency?.ActiveSubMeshCount ?? 0,
            MeshletPhysicalResidencyFallbackPackageCount =
                meshletResidency?.FallbackReasons.Values.Sum() ?? 0,
            MeshletPhysicalResidencyReferencedPackageCount =
                meshletResidency?.ReferencedPackageCount ?? 0,
            MeshletPhysicalResidencyPhysicalPageCapacity =
                meshletResidency?.PhysicalPageCapacity ?? 0,
            MeshletPhysicalResidencyAllocatedBankCount =
                meshletVulkanResidency?.AllocatedBankCount ?? 0,
            MeshletPhysicalResidencyPinnedPageCount =
                meshletResidency?.PinnedPageCount ?? 0,
            MeshletPhysicalResidencyPinnedResidentPageCount =
                meshletResidency?.PinnedResidentPageCount ?? 0,
            MeshletPhysicalResidencyResidentPageCount =
                meshletResidency?.ResidentPageCount ?? 0,
            MeshletPhysicalResidencyQueuedPageCount =
                meshletResidency?.QueuedPageCount ?? 0,
            MeshletPhysicalResidencyReadingPageCount =
                meshletResidency?.ReadingPageCount ?? 0,
            MeshletPhysicalResidencyUploadingPageCount =
                meshletResidency?.UploadingPageCount ?? 0,
            MeshletPhysicalResidencyFailedPageCount =
                meshletResidency?.FailedPageCount ?? 0,
            MeshletPhysicalResidencyRetiredPageCount =
                meshletResidency?.RetiredPhysicalPageCount ?? 0,
            MeshletPhysicalResidencyCommittedBytes =
                meshletVulkanResidency?.AllocatedBankBytes ?? 0UL,
            MeshletPhysicalResidencyRequestCount =
                meshletResidency?.RequestCount ?? 0L,
            MeshletPhysicalResidencyDemandKeyCount =
                meshletVulkanResidency?.CompletedDemandKeyCount ?? 0L,
            MeshletPhysicalResidencyRequestOverflowCount =
                (meshletResidency?.RequestOverflowCount ?? 0L) +
                (meshletVulkanResidency?.DemandOverflowCount ?? 0L),
            MeshletPhysicalResidencyHitRate = meshletResidencyHitRate,
            MeshletPhysicalResidencyFallbackRate =
                meshletResidencyFallbackRate,
            MeshletPhysicalResidencyUploadedBytes =
                meshletResidency?.UploadedBytes ?? 0L,
            MeshletPhysicalResidencyLastFrameUploadBytes =
                meshletVulkanResidency?.LastRecordedUploadBytes ?? 0UL,
            MeshletPhysicalResidencyEvictionCount =
                meshletResidency?.EvictionCount ?? 0L,
            MeshletPhysicalResidencyRetryCount =
                meshletResidency?.RetryCount ?? 0L,
            MeshletPhysicalResidencyInvalidMappingCount =
                (meshletPageCache?.InvalidMappingCount ?? 0L) +
                (meshletVulkanResidency?.InvalidShaderMappingCount ?? 0L),
            MeshletPhysicalResidencyCpuInvalidMappingTotal =
                meshletPageCache?.InvalidMappingCount ?? 0L,
            MeshletPhysicalResidencyGpuInvalidMappingTotal =
                meshletVulkanResidency?.InvalidShaderMappingCount ?? 0L,
            MeshletPhysicalResidencyLastFrameInvalidMappingCount =
                meshletVulkanResidency?
                    .LastCompletedFrameInvalidShaderMappingCount ?? 0U,
            MeshletPhysicalResidencyMissingPageMappingTotal =
                meshletVulkanResidency?
                    .InvalidShaderMissingPageMappingCount ?? 0L,
            MeshletPhysicalResidencyInvalidPageHeaderTotal =
                meshletVulkanResidency?.InvalidShaderPageHeaderCount ?? 0L,
            MeshletPhysicalResidencyRecordBoundsFailureTotal =
                meshletVulkanResidency?.InvalidShaderRecordBoundsCount ?? 0L,
            MeshletPhysicalResidencyLocalAddressFailureTotal =
                meshletVulkanResidency?.InvalidShaderLocalAddressCount ?? 0L,
            MeshletPhysicalResidencyResolvedMappingFailureTotal =
                meshletVulkanResidency?.InvalidShaderResolvedMappingCount ?? 0L,
            MeshletPhysicalResidencyRangePublicationMismatchTotal =
                meshletVulkanResidency?.RangePublicationMismatchCount ?? 0L,
            MeshletPhysicalResidencyFeedbackFrameSerial =
                meshletVulkanResidency?.LastCompletedFeedbackFrameSerial ?? 0UL,
            MeshletPhysicalResidencyFeedbackFrameSlot =
                meshletVulkanResidency?.LastCompletedFeedbackFrameSlot ?? -1,
            MeshletPhysicalResidencyFallbackReasonSummary =
                meshletResidencyFallbackSummary,
            MeshletPhysicalResidencyLatestFailure =
                meshletResidencyLatestFailure,
            SceneSubmissionActiveMode = sceneSubmissionActiveMode,
            SceneSubmissionForwardPath = sceneData.SceneSubmissionForwardPath,
            SceneSubmissionForwardTaskShader = sceneData.SceneSubmissionForwardTaskShader,
            SceneSubmissionCpuCandidateCount = sceneData.MeshletCandidatesCpu,
            SceneSubmissionGpuEmittedCount = sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount,
            SceneSubmissionIndirectTaskCount = sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
            SceneSubmissionGpuCompactionEnabled = sceneData.SceneSubmissionGpuCompactionEnabled ? 1 : 0,
            SceneSubmissionIndirectMeshletDispatchEnabled = sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ? 1 : 0,
            SceneSubmissionGpuLodSelectionEnabled = sceneData.SceneSubmissionGpuLodSelectionEnabled ? 1 : 0,
            SceneSubmissionGpuInstanceExpansionEnabled =
                sceneData.SceneSubmissionGpuInstanceExpansionEnabled ? 1 : 0,
            SceneSubmissionGpuInstanceExpansionActive =
                sceneData.SceneSubmissionGpuInstanceExpansionActive ? 1 : 0,
            SceneSubmissionInstanceCandidateCount =
                sceneData.SceneInstanceCandidateCount,
            SceneSubmissionGpuLodDitherTransitionsEnabled =
                sceneData.SceneSubmissionGpuLodDitherTransitionsEnabled ? 1 : 0,
            SceneSubmissionGpuLodDitherTransitionsActive =
                sceneData.SceneSubmissionGpuLodDitherTransitionsActive ? 1 : 0,
            SceneSubmissionGpuLodTransitionFrameCount =
                sceneData.SceneSubmissionGpuLodTransitionFrameCount,
            SceneSubmissionGpuHierarchicalLodEnabled =
                sceneData.SceneSubmissionGpuHierarchicalLodEnabled ? 1 : 0,
            SceneSubmissionGpuHierarchicalLodActive =
                sceneData.SceneSubmissionGpuHierarchicalLodActive ? 1 : 0,
            SceneSubmissionGpuHierarchicalInstanceCount =
                sceneData.SceneSubmissionGpuHierarchicalInstanceCount,
            SceneSubmissionGpuHierarchySelectedNodeCount =
                sceneData.SceneSubmissionGpuHierarchySelectedNodeCount,
            SceneSubmissionGpuHierarchyTraversalFallbackCount =
                sceneData.SceneSubmissionGpuHierarchyTraversalFallbackCount,
            SceneSubmissionGpuShadowCompactionEnabled = sceneData.SceneSubmissionGpuShadowCompactionEnabled ? 1 : 0,
            SceneSubmissionValidationCompareCpuGpuLists = sceneData.SceneSubmissionValidationCompareCpuGpuLists ? 1 : 0,
            SceneSubmissionGpuCompactionActive = sceneData.SceneSubmissionGpuCompactionActive ? 1 : 0,
            SceneSubmissionCompactionSkipReason = sceneData.SceneSubmissionCompactionSkipReason,
            SceneSubmissionIndirectDispatchSkipReason = sceneData.SceneSubmissionIndirectDispatchSkipReason,
            SceneSubmissionFallbackReason = sceneData.SceneSubmissionFallbackReason,
            SceneSubmissionGpuOpaqueCandidateCount = sceneData.SceneSubmissionGpuOpaqueCandidateCount,
            SceneSubmissionGpuOpaqueFrustumRejectedCount = sceneData.SceneSubmissionGpuOpaqueFrustumRejectedCount,
            SceneSubmissionGpuOpaqueOverflowCount = sceneData.SceneSubmissionGpuOpaqueOverflowCount,
            SceneSubmissionGpuCompactedOpaqueCapacity = sceneData.SceneSubmissionGpuCompactedOpaqueCapacity,
            SceneSubmissionGpuCompactedOpaqueMeshletCount = sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount,
            SceneSubmissionGpuIndirectMeshletTaskCount = sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
            SceneSubmissionGpuCompactedShadowMeshletCount = sceneData.SceneSubmissionGpuCompactedShadowMeshletCount,
            SceneSubmissionGpuDepthSolidCandidateCount = sceneData.SceneSubmissionGpuDepthSolidCandidateCount,
            SceneSubmissionGpuDepthMaskedCandidateCount = sceneData.SceneSubmissionGpuDepthMaskedCandidateCount,
            SceneSubmissionGpuCompactedSolidDepthMeshletCount = sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount,
            SceneSubmissionGpuCompactedMaskedDepthMeshletCount = sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount,
            SceneSubmissionGpuCompactedSolidDepthCapacity = sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity,
            SceneSubmissionGpuCompactedMaskedDepthCapacity = sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity,
            SceneSubmissionGpuDepthOverflowCount = sceneData.SceneSubmissionGpuDepthOverflowCount,
            SceneSubmissionGpuDirectionalShadowCandidateCount = sceneData.SceneSubmissionGpuDirectionalShadowCandidateCount,
            SceneSubmissionGpuCompactedDirectionalShadowMeshletCount = sceneData.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount,
            SceneSubmissionGpuDirectionalShadowOverflowCount = sceneData.SceneSubmissionGpuDirectionalShadowOverflowCount,
            SceneSubmissionGpuDirectionalShadowLodFallbackCount = sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount,
            SceneSubmissionGpuDirectionalShadowCascadeSummary = BuildDirectionalShadowCompactionSummary(sceneData),
            DirectionalShadowRuntime = CreateDirectionalShadowRuntimeDiagnostics(sceneData),
            DirectionalDynamicShadowMeshletCount =
                sceneData.DirectionalDynamicShadowMeshletCount,
            DirectionalShadowSkinnedObjectCount =
                sceneData.DirectionalShadowSkinnedObjectCount,
            SceneSubmissionLocalShadowGpuCompactionJustified =
                spotShadowGpuCompactionJustified || pointShadowGpuCompactionJustified ? 1 : 0,
            SceneSubmissionSpotShadowGpuCompactionJustified = spotShadowGpuCompactionJustified ? 1 : 0,
            SceneSubmissionPointShadowGpuCompactionJustified = pointShadowGpuCompactionJustified ? 1 : 0,
            SceneSubmissionLocalShadowCpuRecordMicroseconds =
                sceneData.CpuSpotShadowRecordMicroseconds + sceneData.CpuPointShadowRecordMicroseconds,
            SceneSubmissionSpotShadowMeshletLightTests = spotShadowMeshletLightTests,
            SceneSubmissionPointShadowMeshletFaceTests = pointShadowMeshletFaceTests,
            SceneSubmissionLocalShadowGpuCompactionStatus = localShadowGpuCompactionStatus,
            SceneSubmissionLocalShadowOverflowSummary = localShadowOverflowSummary,
            SceneSubmissionGpuLod0EmittedCount = sceneData.SceneSubmissionGpuLod0EmittedCount,
            SceneSubmissionGpuLod1EmittedCount = sceneData.SceneSubmissionGpuLod1EmittedCount,
            SceneSubmissionGpuLod2EmittedCount = sceneData.SceneSubmissionGpuLod2EmittedCount,
            SceneSubmissionGpuMissingLodFallbackCount = sceneData.SceneSubmissionGpuMissingLodFallbackCount,
            SceneSubmissionGpuOpaqueLodDecimatedCount = sceneData.SceneSubmissionGpuOpaqueLodDecimatedCount,
            SceneSubmissionValidationValid = sceneData.SceneSubmissionValidationValid,
            SceneSubmissionValidationStatus = sceneData.SceneSubmissionValidationStatus,
            SceneSubmissionValidationCpuOpaqueCount = sceneData.SceneSubmissionValidationCpuOpaqueCount,
            SceneSubmissionValidationGpuOpaqueCount = sceneData.SceneSubmissionValidationGpuOpaqueCount,
            SceneSubmissionValidationComparedSampleCount = sceneData.SceneSubmissionValidationComparedSampleCount,
            SceneSubmissionValidationMismatchCount = sceneData.SceneSubmissionValidationMismatchCount,
            SceneSubmissionValidationSampleLimit = sceneData.SceneSubmissionValidationSampleLimit,
            SceneSubmissionValidationFirstMismatch = sceneData.SceneSubmissionValidationFirstMismatch,
            SceneSubmissionOpaqueCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize,
            SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize,
            SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize,
            SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize,
            SceneSubmissionCounterBufferSize = sceneData.SceneSubmissionCounterBufferSize,
            SceneSubmissionOpaqueIndirectDispatchBufferSize = sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize,
            SceneSubmissionCompactionFullPayloadClear =
                sceneData.SceneSubmissionCompactionFullPayloadClear ? 1 : 0,
            SceneSubmissionCompactionClearedBytes =
                sceneData.SceneSubmissionCompactionClearedBytes,
            SceneSubmissionCompactionResetBarrierCount =
                sceneData.SceneSubmissionCompactionResetBarrierCount,
            SceneSubmissionCompactionOutputBarrierCount =
                sceneData.SceneSubmissionCompactionOutputBarrierCount,
            GpuCompositeMicroseconds = sceneData.GpuCompositeMicroseconds,
            GpuBloomExtractMicroseconds = sceneData.GpuBloomExtractMicroseconds,
            GpuBloomDownsampleMicroseconds = sceneData.GpuBloomDownsampleMicroseconds,
            GpuBloomUpsampleMicroseconds = sceneData.GpuBloomUpsampleMicroseconds,
            GpuDirectionalShadowMicroseconds = sceneData.GpuDirectionalShadowMicroseconds,
            GpuDirectionalRayShadowMicroseconds =
                sceneData.GpuDirectionalRayShadowMicroseconds,
            GpuAreaRayShadowMicroseconds =
                sceneData.GpuAreaRayShadowMicroseconds,
            GpuDirectionalShadowTemporalMicroseconds =
                sceneData.GpuDirectionalShadowTemporalMicroseconds,
            GpuDirectionalShadowSpatialMicroseconds =
                sceneData.GpuDirectionalShadowSpatialMicroseconds,
            GpuSpotShadowMicroseconds = sceneData.GpuSpotShadowMicroseconds,
            GpuPointShadowMicroseconds = sceneData.GpuPointShadowMicroseconds,
            DirectionalShadowRecordSkipped = sceneData.DirectionalShadowRecordSkipped ? 1 : 0,
            SpotShadowRecordSkipped = sceneData.SpotShadowRecordSkipped ? 1 : 0,
            PointShadowRecordSkipped = sceneData.PointShadowRecordSkipped ? 1 : 0,
            ScreenshotRequested = _screenshotCaptureService.PendingCount > 0 ? 1 : 0,
            ScreenshotPendingCount = _screenshotCaptureService.PendingCount,
            ScreenshotCompletedCount = _screenshotCaptureService.CompletedCount,
            TemporalSampleIndex = sceneData.TemporalSampleIndex,
            LastScreenshotPath = _screenshotCaptureService.LastScreenshotPath,
            LastScreenshotError = _screenshotCaptureService.LastScreenshotError,
            RenderDocAvailable = _renderDocCaptureService.IsAvailable ? 1 : 0,
            RenderDocCaptureRequested = _renderDocCaptureService.CaptureRequested ? 1 : 0,
            RenderDocCaptureCompletedCount = _renderDocCaptureService.CompletedCount,
            LastRenderDocCaptureMessage = _renderDocCaptureService.LastMessage,
            DdgiVolumes = giUsesDdgi
                ? sceneData.DdgiVolumeDiagnostics.ToArray()
                : Array.Empty<DdgiVolumeDiagnosticsEntry>(),
            DdgiRuntimeSnapshot = ddgiRuntimeSnapshot,
            ContentDependentDdgi = contentDependentDdgi,
            SimpleDdgiLivenessTelemetry = sceneData.SimpleDdgiLivenessTelemetry,
            SimpleDdgiLivenessWatchdog = sceneData.SimpleDdgiLivenessWatchdog,
            DdgiDiagnosticWarnings = ddgiDiagnosticWarnings,
            LargestTextureAssets = _textureManager.GetLargestFileTextures(10),
            MeshletQualityEntries = _meshManager.GetMeshletQualityEntries(10)
        };

        long gpuFrameMicroseconds =
            CalculateGpuFrameMicroseconds(sceneData);
        SimpleDdgiLayoutTelemetry simpleDdgiLayout = simpleDdgiRequested
            ? SimpleDdgiLayoutTelemetryFactory.Create(
                _simpleDdgiVolumeManager?.LastLayoutReport,
                giSettings.SimpleDdgiSampledAtlasEnabled,
                // The graph reserves V2 resources even under the V1
                // compatibility switch, so admission telemetry must account
                // for the same fixed allocation as the live manager.
                transportV2Enabled: true,
                transportRayCapacity: Math.Max(
                    giSettings.SimpleDdgiNearFullRaysPerProbe,
                    Math.Max(
                        giSettings.SimpleDdgiMidFullRaysPerProbe,
                        giSettings.SimpleDdgiFarFullRaysPerProbe)))
            : SimpleDdgiLayoutTelemetry.Unavailable("Simple DDGI was not requested by the resolved GI settings.");
        SimpleDdgiSchedulingTelemetry simpleDdgiScheduling = giUsesSimpleDdgi && _simpleDdgiVolumeManager != null
            ? _simpleDdgiVolumeManager.GetSchedulingTelemetry()
            : SimpleDdgiSchedulingTelemetry.Unavailable("Simple DDGI is inactive for this capture.");
        SimpleDdgiSchedulerPolicyTelemetry simpleDdgiSchedulerPolicy = giUsesSimpleDdgi && _simpleDdgiVolumeManager != null
            ? SimpleDdgiSchedulerPolicyTelemetryFactory.Create(_simpleDdgiVolumeManager.SchedulerTelemetry)
            : SimpleDdgiSchedulerPolicyTelemetry.Unavailable("Simple DDGI scheduler is inactive for this capture.");
        SimpleDdgiProbeResidencyTelemetry simpleDdgiProbeResidency = simpleDdgiRequested
            ? SimpleDdgiProbeResidencyTelemetryFactory.Create(
                _simpleDdgiVolumeManager)
            : SimpleDdgiProbeResidencyTelemetry.Unavailable(
                "Simple DDGI was not requested by the resolved GI settings.");
        diagnostics = diagnostics with
        {
            GpuFrameMicroseconds = gpuFrameMicroseconds,
            GpuTimingValid = gpuFrameMicroseconds > 0 ? 1 : 0,
            SimpleDdgiLayout = simpleDdgiLayout,
            SimpleDdgiProbeResidency = simpleDdgiProbeResidency,
            SimpleDdgiScheduling = simpleDdgiScheduling,
            SimpleDdgiSchedulerPolicy = simpleDdgiSchedulerPolicy
        };

        RenderBudgetProfile profile = Settings.PerformanceBudgets.Profile;
        UploadBudgetSnapshot uploadSnapshot =
            input.Execution.UploadBudget;
        MemoryBudgetSnapshot memorySnapshot =
            input.Execution.MemoryBudget;
        RuntimeStallSnapshot stallSnapshot =
            input.Execution.RuntimeStalls;
        _lastBudgetSnapshot = _budgetEvaluator.Evaluate(profile, diagnostics, memorySnapshot, uploadSnapshot, stallSnapshot);
        MemoryHeapBudgetSnapshot heapBudget = memorySnapshot.HeapBudget;
        ulong actualGpuMemoryBudgetBytes = heapBudget.PrimaryBudgetBytes;
        ulong actualGpuMemoryUsageBytes = heapBudget.PrimaryUsageBytes;
        ulong sceneObjectHighWaterBytes = checked((ulong)Math.Max(sceneData.ObjectCount, sceneData.ObjectData.Count) * (ulong)Marshal.SizeOf<GPUObjectData>());
        ulong sceneOpaqueHighWaterBytes = checked((ulong)sceneData.MeshletDrawCommands.Count * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
        ulong sceneDepthHighWaterBytes = checked(
            ((ulong)sceneData.SolidDepthMeshletDrawCommands.Count + (ulong)sceneData.MaskedDepthMeshletDrawCommands.Count) *
            (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
        ulong sceneTransparentHighWaterBytes = checked((ulong)sceneData.TransparentMeshletDrawCommands.Count * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
        ulong sceneShadowHighWaterBytes = checked(
            ((ulong)sceneData.LocalShadowMeshletCount + SumDirectionalShadowMeshlets(sceneData)) *
            (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
        bool detailedGiCapture =
            RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
            (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                giSettings.DebugView != GlobalIlluminationDebugView.None);
        bool productionTimingCapture =
            !RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
            _context.ValidationSettings.Mode == RendererValidationMode.Off &&
            !Settings.Diagnostics.DdgiForwardEstimateCountersEnabled &&
            giSettings.DebugView == GlobalIlluminationDebugView.None;
        PerformanceCaptureIdentitySnapshot performanceCaptureIdentity =
            _performanceCaptureMetadataProvider.CreateFrameIdentity(
                sceneData,
                _context.ValidationSettings.Mode,
                RenderSettings.SerializationVersion);

        RendererDiagnostics finalDiagnostics = diagnostics with
        {
            ActiveBudgetProfile = profile.Kind,
            ActiveBudgetProfileName = profile.Name,
            ActiveQualityPreset = Settings.QualityPreset,
            CaptureGpuDeviceName = string.IsNullOrWhiteSpace(captureDevice?.DeviceName)
                ? "unknown-device"
                : captureDevice.DeviceName,
            CaptureGpuDriverVersion = string.IsNullOrWhiteSpace(captureDevice?.DriverVersion)
                ? "unknown-driver"
                : captureDevice.DriverVersion,
            CaptureRenderWidth = sceneData.ScreenWidth,
            CaptureRenderHeight = sceneData.ScreenHeight,
            CaptureSceneContentRevision = sceneData.SceneContentRevision,
            CaptureSceneAssetHash =
                performanceCaptureIdentity.SceneAssetHash,
            CaptureSceneStateHash =
                performanceCaptureIdentity.SceneStateHash,
            CaptureRun = performanceCaptureIdentity.Run,
            CaptureCamera = performanceCaptureIdentity.Camera,
            CaptureFrame = performanceCaptureIdentity.Frame,
            ResolvedGiSettings = ResolvedGiSettingsMetadata.Unknown,
            GiMeasurement = new GiMeasurementMetadata(
                productionTimingCapture
                    ? GiMeasurementMode.Production
                    : detailedGiCapture
                        ? GiMeasurementMode.DetailedInvestigation
                        : GiMeasurementMode.NormalTelemetry,
                detailedGiCapture ? 256 : 0,
                detailedGiCapture
                    ? "Detailed GPU investigation counters enabled; overhead is capture-specific."
                    : productionTimingCapture
                        ? "Production timing; detailed GI branches and atomics are compiled out."
                        : "Normal telemetry; detailed GI investigation counters disabled.",
                detailedGiCapture,
                detailedGiCapture &&
                    sceneData.DdgiInvestigationCountersReadbackValid != 0)
            {
                DiagnosticSampleStrideX = detailedGiCapture ? 16 : 0,
                DiagnosticSampleStrideY = detailedGiCapture ? 16 : 0,
                DiagnosticSampleWeight = detailedGiCapture ? 256 : 0,
                SkyVisibilityCountSemantic = detailedGiCapture
                    ? PerformanceMetricSemantic.SampledEstimate
                    : PerformanceMetricSemantic.Unavailable
            },
            ActiveFeatureIsolation = sceneData.ActiveFeatureIsolation,
            SkippedRenderPassCount = sceneData.SkippedRenderPassCount,
            GraphPlannedBarrierCount = sceneData.GraphPlannedBarrierCount,
            GraphExecutedBarrierCount = sceneData.GraphExecutedBarrierCount,
            GraphQueueOwnershipTransitionCount = asyncComputePlan.QueueOwnershipTransitionCount,
            GraphBarrierSummary = sceneData.GraphBarrierSummary,
            Graph = asyncComputePlan.GraphDiagnostics,
            ProductionPipelineName = productionPipeline.Name,
            ProductionPipelineDeclaredPasses = productionPipeline.PassOrder,
            ProductionPipelineDeclaredPassCount = productionPipeline.PassOrder.Count,
            ProductionPipelineActivePasses = activeProductionPipelinePasses,
            ProductionPipelineActivePassCount = activeProductionPipelinePasses.Count,
            SecondaryCommandBufferEnabled = sceneData.SecondaryCommandBufferEnabled,
            SecondaryCommandBufferPassCount = sceneData.SecondaryCommandBufferPassCount,
            AsyncComputeRequested = asyncComputePlan.Requested ? 1 : 0,
            AsyncComputeEnabled = asyncComputePlan.Enabled ? 1 : 0,
            AsyncComputeSupported = asyncComputePlan.Supported ? 1 : 0,
            AsyncComputeIndependentQueueAvailable = asyncComputePlan.IndependentQueueAvailable ? 1 : 0,
            AsyncComputeDedicatedQueueFamilyAvailable = asyncComputePlan.DedicatedQueueFamilyAvailable ? 1 : 0,
            AsyncComputeGraphicsQueueFamily = asyncComputePlan.GraphicsQueueFamily,
            AsyncComputeComputeQueueFamily = asyncComputePlan.ComputeQueueFamily,
            AsyncComputeCandidatePassCount = asyncComputePlan.CandidatePasses.Count,
            AsyncComputeEnabledPassCount = asyncComputePlan.EnabledPasses.Count,
            AsyncComputeQueueOwnershipTransitionCount = asyncComputePlan.QueueOwnershipTransitionCount,
            AsyncComputeOwnershipTransferCount = sceneData.AsyncComputeOwnershipTransferCount,
            AsyncComputeEstimatedOverlapMicroseconds = sceneData.AsyncComputeEstimatedOverlapMicroseconds,
            AsyncComputeQueueBusyMicroseconds =
                asyncComputePlan.QueueBusyMicroseconds,
            AsyncComputeFirstConsumerWaitEstimateMicroseconds =
                asyncComputePlan.FirstConsumerWaitEstimateMicroseconds,
            AsyncComputeBarrierRecordMicroseconds = asyncComputePlan.BarrierRecordMicroseconds,
            AsyncComputeStatus = asyncComputePlan.Status,
            AsyncComputeCandidatePasses = asyncComputePlan.CandidatePasses,
            AsyncComputeEnabledPasses = asyncComputePlan.EnabledPasses,
            AsyncComputeRequestedMode = asyncComputePlan.RequestedMode,
            AsyncComputeEffectiveMode = asyncComputePlan.EffectiveMode,
            AsyncComputeGraphicsSegmentCount = asyncComputePlan.PlannedGraphicsSegments,
            AsyncComputeComputeSegmentCount = asyncComputePlan.PlannedComputeSegments,
            AsyncComputePlannedGraphicsSegmentCount = asyncComputePlan.PlannedGraphicsSegments,
            AsyncComputePlannedComputeSegmentCount = asyncComputePlan.PlannedComputeSegments,
            AsyncComputeSubmittedGraphicsSegmentCount = asyncComputePlan.SubmittedGraphicsSegments,
            AsyncComputeSubmittedComputeSegmentCount = asyncComputePlan.SubmittedComputeSegments,
            AsyncComputePlannedReleaseBarrierCount = asyncComputePlan.PlannedReleaseBarriers,
            AsyncComputePlannedAcquireBarrierCount = asyncComputePlan.PlannedAcquireBarriers,
            AsyncComputeEmittedReleaseBarrierCount = asyncComputePlan.EmittedReleaseBarriers,
            AsyncComputeEmittedAcquireBarrierCount = asyncComputePlan.EmittedAcquireBarriers,
            AsyncComputeTransferredBytes = asyncComputePlan.TransferredBytes,
            AsyncComputeTransferredImageSubresources = asyncComputePlan.TransferredImageSubresources,
            AsyncComputeValidationFallbackCount = asyncComputePlan.ValidationFallbackCount,
            AsyncComputeLastFallbackReason = asyncComputePlan.LastFallbackReason,
            AsyncComputeResourcePlanGeneration = asyncComputePlan.ResourcePlanGeneration,
            AsyncComputeStalePlanRejectionCount = asyncComputePlan.StalePlanRejectionCount,
            AsyncComputePaths = asyncComputePlan.Paths,
            AsyncComputeSegments = asyncComputePlan.Segments,
            CpuPrimaryCommandRecordMicroseconds = sceneData.CpuPrimaryCommandRecordMicroseconds,
            CpuSecondaryCommandRecordMicroseconds = sceneData.CpuSecondaryCommandRecordMicroseconds,
            BudgetOverallStatus = _lastBudgetSnapshot.OverallStatus,
            CpuFrameBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "CPU renderer"),
            GpuFrameBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "GPU frame"),
            GpuMemoryBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "GPU memory"),
            UploadBudgetStatus = uploadSnapshot.Status,
            GpuMemoryBudgetBytes = profile.GpuMemoryBudgetBytes,
            TrackedGpuMemoryBytes = memorySnapshot.TotalTrackedBytes,
            GpuMemoryBudgetQueryAvailable = heapBudget.IsAvailable ? 1 : 0,
            ActualGpuMemoryUsageBytes = actualGpuMemoryUsageBytes,
            ActualGpuMemoryBudgetBytes = actualGpuMemoryBudgetBytes,
            ActualGpuMemoryAllocationBytes = heapBudget.PrimaryAllocationBytes,
            ActualGpuMemoryBlockBytes = heapBudget.PrimaryBlockBytes,
            ActualGpuMemoryUtilization = actualGpuMemoryBudgetBytes == 0
                ? 0f
                : (float)((double)actualGpuMemoryUsageBytes / actualGpuMemoryBudgetBytes),
            GpuMemoryHeapCount = heapBudget.Entries.Count,
            GpuMemoryHeapBudgets = heapBudget.Entries,
            UnknownGpuMemoryBytes = GetMemoryCategoryBytes(memorySnapshot, MemoryBudgetCategory.Unknown),
            MeshBufferAllocatedBytes = _meshManager.MeshBufferAllocatedBytes,
            MeshBufferUsedBytes = _meshManager.MeshBufferUsedBytes,
            MeshBufferUtilization = _meshManager.MeshBufferUtilization,
            MeshBufferCompactionCount = _meshManager.MeshBufferCompactionCount,
            MeshBufferCompactedBytesSaved = _meshManager.MeshBufferCompactedBytesSaved,
            MeshBufferGrowthRetryCount =
                _meshManager.MeshBufferGrowthRetryCount,
            MeshBufferGrowthRetrySuccessCount =
                _meshManager.MeshBufferGrowthRetrySuccessCount,
            MeshBufferCompactionOutOfDeviceMemorySkipCount =
                _meshManager
                    .MeshBufferCompactionOutOfDeviceMemorySkipCount,
            MeshRetainedDeadBytes =
                _meshManager.RetainedDeadMeshBytes,
            MeshRetainedDeadByteBudget =
                MeshManager.MaximumRetainedDeadMeshBytes,
            MeshRetainedDeadByteBudgetRejectionCount =
                _meshManager
                    .RetainedDeadMeshBudgetRejectionCount,
            MeshPostCommitCleanupFailureCount =
                _meshManager.PostCommitCleanupFailureCount,
            PendingMaterialTextureFanoutCount =
                materialDiagnostics.PendingTextureFanoutCount,
            MaterialTextureFanoutFailureCount =
                materialDiagnostics.TextureFanoutFailureCount,
            PendingRetiredMaterialBufferCount =
                materialDiagnostics.PendingRetiredBufferCount,
            QuarantinedMaterialBufferCount =
                materialDiagnostics.QuarantinedBufferCount,
            MaterialRetiredBufferCleanupFailureCount =
                materialDiagnostics
                    .RetiredBufferCleanupFailureCount,
            MaterialBindingRepairPending =
                materialDiagnostics
                    .MaterialBindingRepairPending
                    ? 1
                    : 0,
            SceneBufferAllocatedBytes = sceneData.ObjectBufferSize +
                sceneData.InstanceBufferSize +
                sceneData.MeshletDrawBufferSize +
                sceneData.FullOpaqueMeshletDrawBufferSize +
                sceneData.SolidDepthMeshletDrawBufferSize +
                sceneData.MaskedDepthMeshletDrawBufferSize +
                sceneData.TransparentMeshletDrawBufferSize +
                sceneData.DirectionalShadowMeshletDrawBufferSize +
                sceneData.LocalShadowMeshletDrawBufferSize +
                sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize +
                sceneData.SceneSubmissionCounterBufferSize +
                sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize +
                sceneData.GpuParticleStateBufferSize +
                sceneData.GpuParticleAliveIndexBufferSize +
                sceneData.GpuParticleDeadIndexBufferSize +
                sceneData.GpuParticleEmitterBufferSize +
                sceneData.GpuParticleCurveSampleBufferSize +
                sceneData.GpuParticleCounterBufferSize +
                sceneData.GpuParticleUnsortedRenderInstanceBufferSize +
                sceneData.GpuParticleRenderInstanceBufferSize +
                sceneData.GpuParticleIndirectDrawBufferSize +
                sceneData.GpuParticleSortKeyBufferSize,
            SceneBufferPeakBytes = sceneObjectHighWaterBytes +
                sceneOpaqueHighWaterBytes +
                sceneDepthHighWaterBytes +
                sceneTransparentHighWaterBytes +
                sceneShadowHighWaterBytes,
            MaterialBufferAllocatedBytes = _materialManager.MaterialBufferSize +
                _materialManager.ForwardMaterialBufferSize +
                _materialManager.MaterialExtensionBufferSize,
            MaterialBufferUtilization = _materialManager.MaterialBufferUtilization,
            LightBufferAllocatedBytes = _lightManager.LightBufferAllocatedBytes,
            TiledLightBufferAllocatedBytes = sceneData.TiledLightHeaderBufferSize + sceneData.TiledLightIndexBufferSize,
            TiledLightHeaderBufferClearBytes = sceneData.TiledLightHeaderBufferClearBytes,
            TiledLightIndexBufferClearBytes = sceneData.TiledLightIndexBufferClearBytes,
            ForwardClusterDepthSliceCount = sceneData.ClusterCountZ,
            ForwardClusterCount = sceneData.LocalLightCount > 0
                ? RenderingConstants.CalculateForwardClusterCount(
                    sceneData.TileCountX,
                    sceneData.TileCountY)
                : 0u,
            ForwardOpaquePipelineCacheEntryCount =
                _meshPipeline?.ForwardOpaquePipelineCacheEntryCount ?? 0,
            NormalConeEligibleOpaqueMeshletCount =
                sceneData.NormalConeEligibleOpaqueMeshletCount,
            DoubleSidedOpaqueMeshletCount =
                sceneData.DoubleSidedOpaqueMeshletCount,
            MeshletNormalConeCullingEnabled =
                sceneData.MeshletNormalConeCullingEnabled,
            MeshletNormalConeCandidateCount =
                sceneData.MeshletNormalConeCandidateCount,
            MeshletNormalConeTestedCount =
                sceneData.MeshletNormalConeTestedCount,
            MeshletNormalConeRejectedCount =
                sceneData.MeshletNormalConeRejectedCount,
            MeshletNormalConeInvalidCount =
                sceneData.MeshletNormalConeInvalidCount,
            ForwardMeshOnlyIndirectDrawCount =
                sceneData.ForwardMeshOnlyIndirectDrawCount,
            DepthMeshOnlyIndirectDrawCount =
                sceneData.DepthMeshOnlyIndirectDrawCount,
            DirectionalShadowMeshOnlyIndirectDrawCount =
                sceneData.DirectionalShadowMeshOnlyIndirectDrawCount,
            LightTileSaturationCount = sceneData.LightTileSaturationCount,
            MaxLightsInAnyTile = sceneData.MaxLightsInAnyTile,
            AverageLightsPerNonEmptyTile = sceneData.AverageLightsPerNonEmptyTile,
            LightCullRejectedPointCount = sceneData.LightCullRejectedPointCount,
            LightCullRejectedSpotCount = sceneData.LightCullRejectedSpotCount,
            LightCullRejectedAreaCount = sceneData.LightCullRejectedAreaCount,
            TextureAssetBytes = _textureManager.FileTextureBytes + _textureManager.DefaultTextureBytes,
            DefaultTextureBytes = _textureManager.DefaultTextureBytes,
            FileTextureBytes = _textureManager.FileTextureBytes,
            TextureCacheEntryCount = _textureManager.TextureCacheEntryCount,
            TextureBindlessUsedCount = _textureManager.TextureBindlessUsedCount,
            TextureBindlessFreeCount = _textureManager.TextureBindlessFreeCount,
            ActiveTextureBudgetProfile = _textureManager.ActiveTextureBudgetProfile,
            RenderTargetBytes = _renderTargets?.TotalEstimatedBytes ?? 0,
            RenderTargetCount = _renderTargets?.RenderTargetCount ?? 0,
            RenderTargetResizeCount = _renderTargets?.ResizeCount ?? 0,
            RequestedDynamicResolutionScale = _dynamicResolutionScaleController.RequestedScale,
            CommittedRenderTargetScale = _dynamicResolutionScaleController.CommittedScale,
            LastRenderTargetRecreateReason = _lastRenderTargetRecreateReason,
            BloomRenderTargetBytes = _renderTargets?.BloomRenderTargetBytes ?? 0,
            AmbientOcclusionRenderTargetBytes = _renderTargets?.AmbientOcclusionRenderTargetBytes ?? 0,
            AntiAliasingRenderTargetBytes = _renderTargets?.AntiAliasingRenderTargetBytes ?? 0,
            WeightedOitRenderTargetBytes = _renderTargets?.WeightedOitRenderTargetBytes ?? 0,
            WeightedOitRenderTargetCount = _renderTargets == null ? 0 : 2,
            DirectionalShadowBytes = _directionalShadowResources?.EstimatedImageBytes ?? 0,
            SpotShadowAtlasBytes = _spotShadowAtlas?.EstimatedImageBytes ?? 0,
            PointShadowBytes = _pointShadowCubemapArray?.EstimatedImageBytes ?? 0,
            PointShadowSkippedFaceCount = sceneData.PointShadowSkippedFaceCount,
            ShadowMapBytes = (_directionalShadowResources?.EstimatedImageBytes ?? 0) +
                (_spotShadowAtlas?.EstimatedImageBytes ?? 0) +
                (_pointShadowCubemapArray?.EstimatedImageBytes ?? 0),
            SpotShadowAtlasUtilization = sceneData.SpotShadowAtlasCapacity <= 0
                ? 0f
                : (float)sceneData.SpotShadowAtlasUsedTiles / sceneData.SpotShadowAtlasCapacity,
            PointShadowFaceUtilization = Settings.Shadows.MaxShadowedPointLights <= 0
                ? 0f
                : (float)sceneData.PointShadowRenderedFaceCount / (Settings.Shadows.MaxShadowedPointLights * 6),
            EnvironmentMapBytes = _environmentManager?.EnvironmentMapBytes ?? 0,
            IrradianceMapBytes = _environmentManager?.IrradianceMapBytes ?? 0,
            PrefilteredEnvironmentBytes = _environmentManager?.PrefilteredEnvironmentBytes ?? 0,
            BrdfLutBytes = _environmentManager?.BrdfLutBytes ?? 0,
            ReflectionProbeBytes = _reflectionProbeManager?.EstimatedBytes ?? 0,
            ReflectionProbeCaptureTargetBytes = (_reflectionProbeManager?.ScratchCaptureBytes ?? 0UL) +
                 (_reflectionProbeManager?.CaptureDepthBytes ?? 0UL),
            ReflectionProbeCubemapArrayBytes = _reflectionProbeManager?.CubemapArrayBytes ?? 0,
            ReflectionProbeRetirementActiveCount = reflectionRetirement.ActiveCount,
            ReflectionProbeRetirementActiveBytes = reflectionRetirement.ActiveBytes,
            ReflectionProbeRetirementOldestAgeFrames = reflectionRetirement.OldestAgeFrames,
            ReflectionProbeRetirementPeakCount = reflectionRetirement.PeakCount,
            ReflectionProbeRetirementPeakBytes = reflectionRetirement.PeakBytes,
            ReflectionProbeRetirementCapacityRejections = reflectionRetirement.CapacityRejectionCount,
            ReflectionProbeRetirementMemoryBudgetRejections = reflectionRetirement.MemoryBudgetRejectionCount,
            ReflectionProbeRetirementInvalidRecordCount = reflectionRetirement.InvalidRecordCount,
            ReflectionProbeRetiredCount = reflectionRetirement.RetiredCount,
            ReflectionProbeCapturesCompletedTotal =
                 sceneData.ReflectionProbeCapturesCompletedTotal,
            ReflectionProbePublishedCount =
                 sceneData.ReflectionProbePublishedCount,
            ReflectionProbeCurrentLifecycle =
                 sceneData.ReflectionProbeCurrentLifecycle,
            ReflectionProbeCompletedLifecycle =
                 sceneData.ReflectionProbeCompletedLifecycle,
            ReflectionProbeCurrentCaptureBudget =
                 sceneData.ReflectionProbeCaptureBudget,
            ReflectionProbeCaptureBudgetUsed =
                 ReflectionProbeTelemetryValueMapper.CaptureBudgetUsedMicroseconds(
                     sceneData.ReflectionProbeCaptureBudget),
            ReflectionProbeCaptureBudgetExceeded =
                 ReflectionProbeTelemetryValueMapper.CaptureBudgetExceeded(
                     sceneData.ReflectionProbeCaptureBudget),
            ForwardGiBenchmarkSuppressed =
                 Settings.Diagnostics.SuppressForwardGiGatherForBenchmark ? 1 : 0,
            ForwardGiBenchmarkForcedExact =
                 Settings.Diagnostics.ForceExactForwardGiGatherForBenchmark ? 1 : 0,
            ForwardGiReceiverCacheConsumed =
                 _forwardPlusPass?.ConsumedSimpleDdgiReceiverCacheForCurrentView == true
                     ? 1
                     : 0,
            ForwardGiReceiverCacheGenerated =
                 _forwardPlusPass?.GeneratedSimpleDdgiReceiverCacheForCurrentView == true
                     ? 1
                     : 0,
            GiCausticReceiverPayloadCompleted =
                 sceneData.GiCausticReceiverPayloadCompleted ? 1 : 0,
            GiCausticReceiverPayloadFrameSerial =
                 sceneData.GiCausticReceiverPayloadFrameSerial,
            ForwardGiDisabledPipelineUsed =
                 _forwardPlusPass?.UsedForwardGiDisabledPipelineForCurrentView == true
                     ? 1
                     : 0,
            ForwardGiExactGatherUsed =
                 _forwardPlusPass?.UsedForwardGiExactGatherForCurrentView == true
                     ? 1
                     : 0,
            StagingBufferAllocatedBytes = _stagingRing.TotalAllocatedBytes,
            StagingBytesUsedThisFrame = _stagingRing.CurrentFrameBytesUsed,
            StagingBytesPeakThisSession = _stagingRing.PeakBytesThisSession,
            StagingOverflowCount = _stagingRing.OverflowCount,
            StagingOverflowCountThisFrame = _stagingRing.CurrentFrameOverflowCount,
            StagingRetainedOverflowBufferCount = _stagingRing.RetainedOverflowBufferCount,
            StagingRetainedOverflowBytes = _stagingRing.RetainedOverflowBytes,
            StagingPeakOverflowBytes = _stagingRing.PeakOverflowBytesThisSession,
            StagingLargestOverflowAllocationBytes = _stagingRing.LargestOverflowAllocationBytes,
            UploadBudgetExceeded = uploadSnapshot.BudgetExceededFrameCount,
            UploadBudgetUtilization = profile.UploadBudgetBytesPerFrame == 0 || profile.UploadBudgetBytesPerFrame == ulong.MaxValue
                ? 0f
                : (float)((double)uploadSnapshot.TotalBytes / profile.UploadBudgetBytesPerFrame),
            UploadBudgetBytesPerFrame = profile.UploadBudgetBytesPerFrame,
            SwapchainEstimatedBytes = _swapchain.EstimatedBytes,
            SwapchainImageCount = (int)_swapchain.ImageCount,
            SwapchainFormat = _swapchain.SurfaceFormat.ToString(),
            SwapchainPresentMode = _swapchain.PresentMode.ToString(),
            MaximumFramesPerSecond = _maximumFramesPerSecond,
            CpuFramePacingWaitMicroseconds =
                _framePacingWaitMicroseconds,
            CpuAcquireImageMicroseconds = _lastAcquireImageMicroseconds,
            CpuWaitForFrameFenceMicroseconds =
                _lastSwapchainImageOwnerWaitMicroseconds +
                _lastFrameResourceRecycleWaitMicroseconds,
            CpuSwapchainImageOwnerWaitMicroseconds =
                _lastSwapchainImageOwnerWaitMicroseconds,
            CpuFrameResourceRecycleWaitMicroseconds =
                _lastFrameResourceRecycleWaitMicroseconds,
            FrameResourceContext = input.Frame.FrameResourceContext,
            FrameResourceOwnerSubmissionSerial =
                input.Frame.FrameResourceOwnerSubmissionSerial,
            SwapchainImageIndex = input.Frame.SwapchainImageIndex,
            SwapchainImageOwnerSubmissionSerial =
                input.Frame.SwapchainImageOwnerSubmissionSerial,
            SwapchainImageOwnerFrameContext =
                input.Frame.SwapchainImageOwnerFrameContext,
            AcquireSemaphoreSlot = input.Frame.AcquireSemaphoreSlot,
            PendingSubmissionSerial = input.Frame.PendingSubmissionSerial,
            CpuQueueSubmitMicroseconds = _lastQueueSubmitMicroseconds,
            CpuPresentMicroseconds = _lastPresentMicroseconds,
            CpuFenceResetMicroseconds = _sync.LastFenceResetMicroseconds,
            RuntimeStallMicrosecondsThisFrame = stallSnapshot.TotalMicrosecondsThisFrame,
            RuntimeWorstStallMicroseconds = stallSnapshot.WorstMicrosecondsThisFrame,
            RuntimeWorstStallReason = stallSnapshot.WorstReasonThisFrame,
            RuntimeDeviceWaitIdleCount = stallSnapshot.DeviceWaitIdleCount,
            GpuMotionVectorMicroseconds = sceneData.GpuMotionVectorMicroseconds,
            GpuFrameMicroseconds = gpuFrameMicroseconds,
            ValidationMode = _context.ValidationSettings.Mode,
            ValidationVerboseMessageCount = validationMessages.VerboseCount,
            ValidationInfoMessageCount = validationMessages.InformationCount,
            ValidationWarningMessageCount = validationMessages.WarningCount,
            ValidationErrorMessageCount = validationMessages.ErrorCount,
            ValidationFirstWarningMessage = validationMessages.FirstWarningMessage,
            ValidationLastWarningMessage = validationMessages.LastWarningMessage,
            ValidationFirstErrorMessage = validationMessages.FirstErrorMessage,
            ValidationLastErrorMessage = validationMessages.LastErrorMessage,
            SceneObjectBufferHighWaterBytes = sceneObjectHighWaterBytes,
            SceneOpaqueMeshletBufferHighWaterBytes = sceneOpaqueHighWaterBytes,
            SceneDepthMeshletBufferHighWaterBytes = sceneDepthHighWaterBytes,
            SceneTransparentMeshletBufferHighWaterBytes = sceneTransparentHighWaterBytes,
            SceneShadowMeshletBufferHighWaterBytes = sceneShadowHighWaterBytes
        };

        IReadOnlyList<GiFeatureState> giFeatureStates = GiFeatureStateFactory.Create(finalDiagnostics);
        finalDiagnostics = finalDiagnostics with
        {
            GiFeatureStates = giFeatureStates
        };
        finalDiagnostics = finalDiagnostics with
        {
            ResolvedGiSettings = ResolvedGiSettingsMetadataFactory.Create(finalDiagnostics)
        };
        GiWarningEvaluationResult giWarningEvaluation = _giWarningEvaluator.Evaluate(finalDiagnostics);
        IReadOnlyList<GiDiagnosticWarning> giWarnings = GiDiagnosticWarningFactory.Create(
            finalDiagnostics,
            giWarningEvaluation,
            giFeatureStates);
        GiBlackFrameMetrics blackFrameMetrics = giWarningEvaluation.BlackFrame;
        RendererDiagnostics completedDiagnostics = finalDiagnostics with
        {
            GiWarnings = giWarnings,
            GiBlackFrameMetrics = blackFrameMetrics,
            // Retain legacy fields for existing overlays/capture consumers, but source them
            // from the calibrated stateful evaluator rather than the old one-pixel rule.
            DdgiBlackFrameSuspect = blackFrameMetrics.LargeAreaBlackout ? 1 : 0,
            DdgiBlackFrameAfterRecenter = blackFrameMetrics.LargeAreaBlackout &&
                finalDiagnostics.SimpleDdgiRecentered != 0 ? 1 : 0,
            DdgiBlackFrameAfterAtlasClear = blackFrameMetrics.LargeAreaBlackout &&
                finalDiagnostics.SimpleDdgiAtlasCleared != 0 ? 1 : 0,
            DdgiBlackFrameDuringFreshAtlas = blackFrameMetrics.LargeAreaBlackout &&
                finalDiagnostics.SimpleDdgiAtlasFresh != 0 ? 1 : 0,
        };

        return new RendererDiagnosticsAssemblyResult(
            completedDiagnostics,
            _lastBudgetSnapshot);
    }

    internal static SimpleDdgiTransportConvergenceTelemetry
        AttributeSimpleDdgiTransportRingTimings(
            SimpleDdgiTransportConvergenceTelemetry telemetry,
            long transportMicroseconds,
            long blendMicroseconds)
    {
        if (telemetry.Rings.Count == 0)
            return telemetry;

        ulong scheduledRays = 0UL;
        long scheduledProbes = 0L;
        foreach (SimpleDdgiTransportRingConvergenceTelemetry ring in telemetry.Rings)
        {
            scheduledRays = ulong.MaxValue - scheduledRays < ring.ScheduledRayCount
                ? ulong.MaxValue
                : scheduledRays + ring.ScheduledRayCount;
            scheduledProbes = Math.Min(
                int.MaxValue,
                scheduledProbes + Math.Max(0, ring.ScheduledProbeCount));
        }

        var attributed = new SimpleDdgiTransportRingConvergenceTelemetry[
            telemetry.Rings.Count];
        for (int index = 0; index < attributed.Length; index++)
        {
            SimpleDdgiTransportRingConvergenceTelemetry ring = telemetry.Rings[index];
            double transportShare = scheduledRays > 0
                ? ring.ScheduledRayCount / (double)scheduledRays
                : 0.0;
            double blendShare = scheduledProbes > 0
                ? ring.ScheduledProbeCount / (double)scheduledProbes
                : 0.0;
            attributed[index] = ring with
            {
                EstimatedTransportMilliseconds =
                    Math.Max(0L, transportMicroseconds) / 1000.0 * transportShare,
                EstimatedBlendMilliseconds =
                    Math.Max(0L, blendMicroseconds) / 1000.0 * blendShare
            };
        }
        return telemetry with { Rings = Array.AsReadOnly(attributed) };
    }

    private static ulong SumDirectionalShadowMeshlets(SceneRenderingData sceneData)
    {
        ulong sum = 0;
        for (int i = 0; i < sceneData.DirectionalShadowMeshletCounts.Length; i++)
            sum += (ulong)Math.Max(0, sceneData.DirectionalShadowMeshletCounts[i]);
        return sum;
    }

    private static RenderBudgetStatus FindMetricStatus(RenderBudgetSnapshot snapshot, string metricName)
    {
        foreach (BudgetMetric metric in snapshot.Metrics)
        {
            if (string.Equals(metric.Name, metricName, StringComparison.Ordinal))
                return metric.Status;
        }

        return RenderBudgetStatus.Unknown;
    }

    private static ulong GetMemoryCategoryBytes(MemoryBudgetSnapshot snapshot, MemoryBudgetCategory category)
    {
        foreach (MemoryBudgetEntry entry in snapshot.Entries)
        {
            if (entry.Category == category)
                return entry.Bytes;
        }

        return 0;
    }

    internal static long CalculateGpuFrameMicroseconds(SceneRenderingData sceneData)
    {
        return sceneData.GpuDepthPrePassMicroseconds +
            sceneData.GpuDirectionalShadowMicroseconds +
            sceneData.GpuDirectionalRayShadowMicroseconds +
            sceneData.GpuAreaRayShadowMicroseconds +
            sceneData.GpuDirectionalShadowTemporalMicroseconds +
            sceneData.GpuDirectionalShadowSpatialMicroseconds +
            sceneData.GpuSpotShadowMicroseconds +
            sceneData.GpuPointShadowMicroseconds +
            sceneData.GpuHiZBuildMicroseconds +
            sceneData.GpuMotionVectorMicroseconds +
            sceneData.GpuAmbientOcclusionMicroseconds +
            sceneData.GpuAmbientOcclusionBlurMicroseconds +
            sceneData.GpuAccelerationStructureBlasMicroseconds +
            sceneData.GpuAccelerationStructureTlasMicroseconds +
            sceneData.GpuDdgiUpdateMicroseconds +
            sceneData.GpuGiCompositeMicroseconds +
            sceneData.GpuLightCullMicroseconds +
            sceneData.GpuForwardOpaqueMicroseconds +
            sceneData.GpuTransparentMicroseconds +
            sceneData.GpuParticleMicroseconds +
            sceneData.GpuTrailBeamMicroseconds +
            sceneData.GpuFogMicroseconds +
            sceneData.GpuAutoExposureMicroseconds +
            sceneData.GpuAntiAliasingMicroseconds +
            sceneData.GpuBloomExtractMicroseconds +
            sceneData.GpuBloomDownsampleMicroseconds +
            sceneData.GpuBloomUpsampleMicroseconds +
            sceneData.GpuCompositeMicroseconds +
            sceneData.GpuSkinningMicroseconds +
            sceneData.GpuReflectionProbeCaptureMicroseconds +
            sceneData.GpuReflectionProbePrefilterMicroseconds +
            sceneData.GpuReflectionProbePublishMicroseconds +
            sceneData.GpuAutomaticPlanarCaptureMicroseconds +
            sceneData.GpuHybridReflectionSsrMicroseconds +
            sceneData.GpuHybridReflectionRayQueryMicroseconds +
            sceneData.GpuHybridReflectionDdgiBaseMicroseconds +
            sceneData.GpuHybridReflectionResolveMicroseconds +
            sceneData.GpuHybridReflectionTemporalMicroseconds +
            sceneData.GpuHybridReflectionSpatialMicroseconds +
            sceneData.GpuHybridReflectionCompositeMicroseconds;
    }

    private static bool ForwardOcclusionCountersReconcile(SceneRenderingData sceneData)
    {
        if (sceneData.SceneSubmissionGpuCompactionActive &&
            sceneData.SceneSubmissionFallbackReason.Length == 0 &&
            sceneData.ForwardOcclusionTestedMeshletsGpu > 0)
        {
            return sceneData.ForwardOcclusionCulledMeshletsGpu <= sceneData.ForwardOcclusionTestedMeshletsGpu;
        }

        if (sceneData.ForwardTaskInvocations <= 0)
            return true;

        if (!sceneData.OcclusionCullingEnabled || sceneData.HiZMipCount == 0)
            return sceneData.ForwardOcclusionTestedMeshletsGpu == 0 &&
                sceneData.ForwardOcclusionCulledMeshletsGpu == 0;

        int visibleAfterFrustum = Math.Max(0, sceneData.ForwardTaskInvocations - sceneData.ForwardFrustumCulledMeshletsGpu);
        return sceneData.ForwardOcclusionTestedMeshletsGpu == visibleAfterFrustum &&
            sceneData.ForwardOcclusionCulledMeshletsGpu + sceneData.ForwardEmittedMeshletsGpu == sceneData.ForwardOcclusionTestedMeshletsGpu;
    }

    private static string BuildForwardOcclusionSanity(
        SceneRenderingData sceneData,
        bool gpuMeshletCountersEnabled,
        bool reconciled)
    {
        if (!gpuMeshletCountersEnabled)
            return "GPU meshlet counters disabled.";

        if (sceneData.SceneSubmissionGpuCompactionActive &&
            sceneData.SceneSubmissionFallbackReason.Length == 0 &&
            sceneData.ForwardOcclusionTestedMeshletsGpu > 0)
        {
            return reconciled
                ? "Scene submission Hi-Z occlusion counters reconcile: rejected is within tested."
                : "Scene submission Hi-Z occlusion counters do not reconcile.";
        }

        if (sceneData.ForwardTaskInvocations <= 0)
            return "No completed forward GPU counters are available yet.";

        if (!sceneData.OcclusionCullingEnabled || sceneData.HiZMipCount == 0)
        {
            return reconciled
                ? "Hi-Z occlusion disabled; tested and rejected counters are zero."
                : "Hi-Z occlusion disabled, but tested or rejected counters are non-zero.";
        }

        if (reconciled)
            return "Forward occlusion counters reconcile: emitted plus rejected equals tested.";

        return "Forward occlusion counters do not reconcile; inspect shader diagnostics and frame latency.";
    }

    private static int CalculateSpotShadowMeshletLightTests(SceneRenderingData sceneData)
    {
        int selectedSpotLights = Math.Max(0, sceneData.SpotShadowSelectedCount);
        int meshlets = Math.Max(0, sceneData.LocalStaticShadowMeshletCount) +
            Math.Max(0, sceneData.LocalDynamicShadowMeshletCount);
        return SaturatingMultiply(selectedSpotLights, meshlets);
    }

    private static int CalculatePointShadowMeshletFaceTests(SceneRenderingData sceneData)
    {
        int renderedFaces = Math.Max(0, sceneData.PointShadowRenderedFaceCount);
        int meshlets = Math.Max(0, sceneData.LocalStaticShadowMeshletCount) +
            Math.Max(0, sceneData.LocalDynamicShadowMeshletCount);
        return SaturatingMultiply(renderedFaces, meshlets);
    }

    private static bool IsSpotShadowGpuCompactionJustified(
        SceneRenderingData sceneData,
        int meshletLightTests)
    {
        return sceneData.SpotShadowsEnabled &&
               sceneData.SpotShadowSelectedCount > 0 &&
               !sceneData.SpotShadowRecordSkipped &&
               sceneData.CpuSpotShadowRecordMicroseconds >= LocalShadowGpuCompactionRecordThresholdMicroseconds &&
               meshletLightTests >= LocalShadowGpuCompactionWorkThreshold;
    }

    private static bool IsPointShadowGpuCompactionJustified(
        SceneRenderingData sceneData,
        int meshletFaceTests)
    {
        return sceneData.PointShadowsEnabled &&
               sceneData.PointShadowSelectedCount > 0 &&
               !sceneData.PointShadowRecordSkipped &&
               sceneData.CpuPointShadowRecordMicroseconds >= LocalShadowGpuCompactionRecordThresholdMicroseconds &&
               meshletFaceTests >= LocalShadowGpuCompactionWorkThreshold;
    }

    private static string BuildLocalShadowGpuCompactionStatus(
        SceneRenderingData sceneData,
        int spotShadowMeshletLightTests,
        int pointShadowMeshletFaceTests,
        bool spotShadowGpuCompactionJustified,
        bool pointShadowGpuCompactionJustified)
    {
        if (spotShadowGpuCompactionJustified)
        {
            return
                $"spot candidate: cpu={sceneData.CpuSpotShadowRecordMicroseconds}us tests={spotShadowMeshletLightTests}; CPU fallback active until GPU spot-list path is validated.";
        }

        if (pointShadowGpuCompactionJustified)
        {
            return
                $"point candidate: cpu={sceneData.CpuPointShadowRecordMicroseconds}us tests={pointShadowMeshletFaceTests}; deferred until spot-list GPU path validates.";
        }

        if (sceneData.SpotShadowRecordSkipped && sceneData.PointShadowRecordSkipped)
            return "not justified: local shadow command recording was skipped by stable signatures.";

        long localShadowCpuRecordMicroseconds =
            sceneData.CpuSpotShadowRecordMicroseconds + sceneData.CpuPointShadowRecordMicroseconds;
        int localShadowWork = Math.Max(spotShadowMeshletLightTests, pointShadowMeshletFaceTests);
        return
            $"not justified: cpu={localShadowCpuRecordMicroseconds}us tests={localShadowWork}, thresholds={LocalShadowGpuCompactionRecordThresholdMicroseconds}us/{LocalShadowGpuCompactionWorkThreshold}; CPU fallback active.";
    }

    private static string BuildLocalShadowOverflowSummary(
        bool spotShadowGpuCompactionJustified,
        bool pointShadowGpuCompactionJustified)
    {
        return spotShadowGpuCompactionJustified || pointShadowGpuCompactionJustified
            ? "none: local shadow GPU compaction is not enabled, so CPU fallback has no GPU output overflow."
            : string.Empty;
    }

    private static int SaturatingMultiply(int left, int right)
    {
        long product = (long)Math.Max(0, left) * Math.Max(0, right);
        return product > int.MaxValue ? int.MaxValue : (int)product;
    }

    private static DirectionalShadowRuntimeDiagnostics CreateDirectionalShadowRuntimeDiagnostics(
        SceneRenderingData sceneData)
    {
        if (!sceneData.DirectionalShadowPassEnabled)
            return DirectionalShadowRuntimeDiagnostics.Empty;

        int cascadeCount = Math.Min(
            Math.Max(0, sceneData.DirectionalShadowCascadeCount),
            ShadowSettings.MaxDirectionalCascades);
        float[] splits = new float[cascadeCount];
        for (int cascade = 0; cascade < cascadeCount; cascade++)
            splits[cascade] = GetDirectionalShadowSplit(sceneData.ShadowData, cascade);

        DirectionalShadowReceiverCounters receiverCounters =
            sceneData.DirectionalShadowReceiverCountersReadbackValid != 0
                ? new DirectionalShadowReceiverCounters(
                    ReadbackValid: 1,
                    PrimarySelectionCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimarySelectionCounts),
                    ProjectionRejectedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverProjectionRejectedCounts),
                    UvDepthRejectedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverUvDepthRejectedCounts),
                    FallbackCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFallbackCounts),
                    TransitionBlendCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverTransitionBlendCounts),
                    PrimaryResolvedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryResolvedCounts),
                    ClearDepthFootprintCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverClearDepthFootprintCounts),
                    PrimaryFullyLitCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts),
                    PrimaryPartiallyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts),
                    PrimaryFullyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts),
                    FinalFullyLitCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalFullyLitCounts),
                    FinalPartiallyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts),
                    FinalFullyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts),
                    AverageReceiverDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageDepths),
                    AverageMinimumSampledDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths),
                    AverageMaximumSampledDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths),
                    UnresolvedCount: unchecked((uint)Math.Max(0, sceneData.DirectionalShadowReceiverUnresolvedCount)))
                : DirectionalShadowReceiverCounters.Empty;

        return new DirectionalShadowRuntimeDiagnostics(
            Enabled: 1,
            ConfiguredMaxDistance: sceneData.DirectionalShadowMaxDistance,
            EffectiveNearDistance: sceneData.ShadowData.CascadeTransitionData.Y,
            EffectiveFarDistance: sceneData.ShadowData.CascadeTransitionData.Z,
            CascadeBlendFraction: sceneData.ShadowData.CascadeTransitionData.X,
            CascadeSplits: splits,
            StaticCacheActiveMask: sceneData.DirectionalShadowStaticCacheActiveMask,
            StaticCacheValidMask: sceneData.DirectionalShadowStaticCacheValidMask,
            StaticCacheRefreshMask: sceneData.DirectionalShadowStaticCacheRefreshMask,
            StaticCacheReuseMask: sceneData.DirectionalShadowStaticCacheReuseMask,
            StaticCandidateCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts),
            StaticEmittedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts),
            StaticRejectedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowRejectedCounts),
            StaticOverflowCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowOverflowCounts),
            DynamicCandidateCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts),
            DynamicEmittedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts),
            DynamicRejectedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts),
            DynamicOverflowCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts),
            ConservativeLodFallbackCount: sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount,
            ReceiverCounters: receiverCounters)
        {
            CacheLayerProvenance = CopyDirectionalShadowCacheLayerProvenance(
                sceneData.DirectionalShadowCacheLayerProvenance),
            CasterDiagnostics = sceneData.DirectionalShadowCasterDiagnosticReadback,
            CascadeFitDiagnostics =
                (DirectionalShadowCascadeFitDiagnostics[])
                sceneData.DirectionalShadowCascadeFitDiagnostics.Clone(),
            RequestedMode =
                sceneData.DirectionalShadowFramePlan.RequestedMode,
            EffectiveMode =
                sceneData.DirectionalShadowFramePlan.EffectiveMode,
            FallbackReason =
                sceneData.DirectionalShadowFramePlan.FallbackReason,
            FallbackDetail =
                sceneData.DirectionalShadowFramePlan.FallbackDetail,
            RayMaskEnabled = sceneData.DirectionalRayShadowPassEnabled
                ? 1
                : 0,
            CascadedReceiverFallbackRequired =
                sceneData.DirectionalShadowFramePlan
                    .CascadedReceiverFallbackRequired
                    ? 1
                    : 0,
            RayMaskFormat = sceneData.DirectionalRayShadowPassEnabled
                ? "PackedR8UnormStorageBuffer"
                : string.Empty,
            RayMaskWidth = sceneData.DirectionalRayShadowMaskWidth,
            RayMaskHeight = sceneData.DirectionalRayShadowMaskHeight,
            RayMaskBytes = sceneData.DirectionalRayShadowMaskBytes,
            RayMaskResourceGeneration =
                sceneData.DirectionalRayShadowResourceGeneration,
            RaySceneResourceGeneration =
                sceneData.DirectionalShadowFramePlan
                    .RaySceneResourceGeneration,
            RaySceneContentEpoch =
                sceneData.DirectionalShadowFramePlan.RaySceneContentEpoch,
            QualificationLevel =
                sceneData.DirectionalShadowFramePlan.QualificationLevel,
            QualificationId =
                sceneData.DirectionalShadowFramePlan.QualificationId,
            QualificationDetail =
                sceneData.DirectionalShadowFramePlan.QualificationDetail,
            QualificationDeviceRuleId =
                sceneData.DirectionalShadowFramePlan
                    .QualificationDeviceRuleId,
            QualificationTrackId =
                sceneData.DirectionalShadowFramePlan.QualificationTrackId,
            QualifiedGpuBudgetMicroseconds =
                sceneData.DirectionalShadowFramePlan
                    .QualifiedGpuBudgetMicroseconds,
            QualifiedMemoryBudgetBytes =
                sceneData.DirectionalShadowFramePlan
                    .QualifiedMemoryBudgetBytes,
            OpaqueReceiverPolicy =
                sceneData.DirectionalShadowFramePlan.OpaqueReceiverPolicy,
            TransparentReceiverPolicy =
                sceneData.DirectionalShadowFramePlan.TransparentReceiverPolicy,
            DecalReceiverPolicy =
                sceneData.DirectionalShadowFramePlan.DecalReceiverPolicy,
            CsmTemporalEnabled =
                sceneData.DirectionalShadowFramePlan.UsesCsmTemporal ? 1 : 0,
            SoftTemporalEnabled =
                sceneData.DirectionalShadowFramePlan.UsesSoftHistory ? 1 : 0,
            SoftSpatialEnabled =
                sceneData.DirectionalShadowSpatialPassEnabled ? 1 : 0,
            HistoryValid = sceneData.DirectionalShadowHistoryValid,
            HistoryResetReason =
                sceneData.DirectionalShadowFramePlan.HistoryResetReason,
            HistoryBytes = sceneData.DirectionalShadowHistoryBytes,
            GpuCsmMicroseconds = sceneData.GpuDirectionalShadowMicroseconds,
            GpuRayTraceMicroseconds =
                sceneData.GpuDirectionalRayShadowMicroseconds,
            GpuTemporalMicroseconds =
                sceneData.GpuDirectionalShadowTemporalMicroseconds,
            GpuSpatialMicroseconds =
                sceneData.GpuDirectionalShadowSpatialMicroseconds,
            RaySceneExactCategories = sceneData.RaySceneReadiness.ExactCategories,
            RaySceneProxyCategories = sceneData.RaySceneReadiness.ProxyCategories,
            RaySceneCompleteCategories =
                sceneData.RaySceneReadiness.CompleteCategories,
            RaySceneCoverageMinimum =
                sceneData.RaySceneReadiness.CoverageMinimum,
            RaySceneCoverageMaximum =
                sceneData.RaySceneReadiness.CoverageMaximum,
            RayCounters = sceneData.DirectionalShadowRayCountersReadback
        };
    }

    private static DirectionalShadowCacheLayerProvenance[] CopyDirectionalShadowCacheLayerProvenance(
        DirectionalShadowCacheLayerProvenance[] source)
    {
        var copy = new DirectionalShadowCacheLayerProvenance[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private static float GetDirectionalShadowSplit(in GPUShadowData shadowData, int cascade)
    {
        return cascade switch
        {
            0 => shadowData.CascadeSplits.X,
            1 => shadowData.CascadeSplits.Y,
            2 => shadowData.CascadeSplits.Z,
            _ => shadowData.CascadeSplits.W
        };
    }

    private static int[] CopyDiagnosticCounters(int[] source)
    {
        var copy = new int[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private static uint[] CopyDiagnosticCountersAsUInt(int[] source)
    {
        var copy = new uint[source.Length];
        for (int i = 0; i < source.Length; i++)
            copy[i] = unchecked((uint)Math.Max(0, source[i]));
        return copy;
    }

    private static float[] CopyDiagnosticValues(float[] source)
    {
        var copy = new float[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    private static string BuildDirectionalShadowCompactionSummary(SceneRenderingData sceneData)
    {
        int cascadeCount = Math.Min(
            Math.Max(0, sceneData.DirectionalShadowCascadeCount),
            ShadowSettings.MaxDirectionalCascades);
        if (cascadeCount == 0)
            return string.Empty;

        string summary = string.Empty;
        for (int cascade = 0; cascade < cascadeCount; cascade++)
        {
            if (summary.Length > 0)
                summary += ", ";
            summary +=
                $"c{cascade}:s={sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts[cascade]}/{sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade]} " +
                $"d={sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts[cascade]}/{sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade]}";
        }

        return summary;
    }

}
