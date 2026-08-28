using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record PerformanceMetricSemanticEntry(
        string Path,
        PerformanceMetricSemantic Semantic,
        string Description);

    public sealed record PerformanceMemoryOwnershipAudit(
        ulong TrackedBytes,
        ulong BudgetBytes,
        ulong HeadroomBytes,
        double HeadroomFraction,
        bool MeetsTwentyPercentHeadroom,
        ulong CanonicalDdgiAtlasBytes,
        ulong SampledAtlasMirrorBytes,
        ulong TransportBytes,
        ulong ReadbackBytes,
        ulong ScratchAndQueueBytes,
        ulong RetiredGenerationBytes,
        int RetiredGenerationCount,
        ulong DisabledFeatureRetainedBytes,
        ulong DuplicateOwnershipBytes,
        IReadOnlyList<string> Findings)
    {
        public static PerformanceMemoryOwnershipAudit Unavailable { get; } = new(
            0, 0, 0, 0.0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Array.Empty<string>());
    }

    /// <summary>
    /// Immutable context required to compare captures meaningfully. This is deliberately
    /// separate from the frame diagnostics so snapshot consumers can reject incompatible
    /// hardware, resolution, scene, or feature configurations before comparing timings.
    /// </summary>
    public sealed record PerformanceCaptureMetadata(
        string GpuDeviceName,
        string DriverVersion,
        uint RenderWidth,
        uint RenderHeight,
        RenderQualityPreset QualityPreset,
        ulong SceneContentRevision,
        string DebugState,
        IReadOnlyList<string> FeatureFlags,
        string GiTimingCoverage)
    {
        public static PerformanceCaptureMetadata Unknown { get; } = new(
            "unknown-device",
            "unknown-driver",
            0,
            0,
            RenderQualityPreset.DdgiHigh,
            0,
            "unknown",
            Array.Empty<string>(),
            "unavailable");

        public PerformanceCaptureRunMetadata Run { get; init; } = PerformanceCaptureRunMetadata.Unknown;
        public PerformanceCaptureCameraMetadata Camera { get; init; } = PerformanceCaptureCameraMetadata.Unknown;
        public PerformanceCaptureFrameMetadata Frame { get; init; } = PerformanceCaptureFrameMetadata.Unknown;
        public ResolvedGiSettingsMetadata ResolvedGiSettings { get; init; } = ResolvedGiSettingsMetadata.Unknown;
        public GiMeasurementMetadata Measurement { get; init; } = GiMeasurementMetadata.Unknown;
        public IReadOnlyList<GiFeatureState> FeatureStates { get; init; } = Array.Empty<GiFeatureState>();
        public string SceneStateHash { get; init; } = "unknown-scene-state";
        public string SceneAssetHash { get; init; } = "unknown-scene-asset";
        public string ValidationState { get; init; } = "unknown-validation";
        public string PairedCaptureIdentity { get; init; } = string.Empty;
        public IReadOnlyList<PerformanceMetricSemanticEntry> CounterSemantics { get; init; } =
            Array.Empty<PerformanceMetricSemanticEntry>();
    }

    public sealed record PerformanceSnapshot(
        DateTimeOffset CapturedAt,
        RenderBudgetProfile Profile,
        RendererDiagnostics Diagnostics,
        PerformanceFoliageSnapshot Foliage,
        PerformanceGlobalIlluminationSnapshot GlobalIllumination,
        IReadOnlyList<string> Warnings,
        RenderBudgetSnapshot Budget)
    {
        /// <summary>
        /// Increment when changing the persisted performance-capture contract in an
        /// incompatible way. Version 3 adds reproducible capture contracts, named enum JSON,
        /// explicit timing attribution, unique residency, and structured GI warnings. Version 4
        /// adds requested/supported/admitted/effective advanced-GI mode states. Version 5 adds
        /// the fail-closed C5 near-field residual observability/readback contract. Version 6
        /// adds fence-complete C1 micromap lifecycle and memory telemetry. Version 7
        /// adds immutable C1 content evidence. Version 8 adds fence-complete C3
        /// runtime/workload/timing/central-memory telemetry. Version 9 adds
        /// fence-validated C4 cache publication, timing, and memory telemetry.
        /// Version 10 adds fence-validated B1 publication counters, bounded
        /// stage timings, and exact central-memory telemetry.
        /// </summary>
        public const int CurrentSchemaVersion = 11;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        /// <summary>Persisted source version before migration, useful when opening baselines.</summary>
        public int OriginalSchemaVersion { get; init; } = CurrentSchemaVersion;
        public PerformanceCaptureMetadata Capture { get; init; } = PerformanceCaptureMetadata.Unknown;
        public IReadOnlyList<GiDiagnosticWarning> StructuredWarnings { get; init; } = Array.Empty<GiDiagnosticWarning>();
        public GiTimingAttributionSnapshot GiTiming { get; init; } = GiTimingAttributionSnapshot.Unavailable;
        public GiResidencySnapshot GiResidency { get; init; } = GiResidencySnapshot.Unavailable;
        public PerformanceMemoryOwnershipAudit MemoryAudit { get; init; } =
            PerformanceMemoryOwnershipAudit.Unavailable;
        public DdgiContentRuntimeSnapshot ContentDependentDdgi { get; init; } =
            DdgiContentRuntimeSnapshot.Disabled;
    }

    public sealed record PerformanceFoliageSnapshot(
        int PatchCount,
        int PrototypeCount,
        int ClusterCount,
        int VisibleClusterCount,
        int VisibleMeshletDrawCount,
        int DdgiSampleCount,
        int DdgiTransportExcludedClusterCount,
        string DdgiTransportExclusionReason,
        int GrassBladeEstimate,
        int FarImpostorVisibleCount,
        int OverflowCount,
        ulong BufferBytes,
        long CpuBuildMicroseconds,
        long CpuUploadMicroseconds,
        long GpuCullMicroseconds,
        long GpuDepthMicroseconds,
        long GpuForwardMicroseconds,
        long GpuShadowMicroseconds,
        string LikelyBottleneck)
    {
        public DdgiFoliageGeometryMode DdgiGeometryMode { get; init; }
        public int DdgiProxyCardCount { get; init; }
        public int DdgiProxyTriangleCount { get; init; }
        public int DdgiAuthoredInstanceCount { get; init; }
        public int DdgiGeneratedInstanceCount { get; init; }
        public int DdgiDroppedTriangleCount { get; init; }
        public int DdgiRepresentedBladeCount { get; init; }
        public int DdgiProxyUpdatedThisFrame { get; init; }
        public ulong DdgiProxyUploadBytes { get; init; }
        public ulong DdgiProxyBufferBytes { get; init; }
        public ulong DdgiProxyPatchBufferBytes { get; init; }
        public ulong DdgiProxyContentSignature { get; init; }
        public ulong DdgiProxyCadenceGeneration { get; init; }
        public long CpuDdgiProxyBuildMicroseconds { get; init; }
        public long CpuDdgiProxyUploadMicroseconds { get; init; }
        public long CpuDdgiProxyGenerationRecordMicroseconds { get; init; }
        public long GpuDdgiProxyGenerationMicroseconds { get; init; }
        public int DdgiProxyRequestedRepresentedInstanceCount { get; init; }
        public float DdgiProxyDensityError { get; init; }
        public float DdgiProxyWindAgeSeconds { get; init; }
        public int DdgiProxyNearCardCount { get; init; }
        public int DdgiProxyMidCardCount { get; init; }
        public int DdgiProxyFarCardCount { get; init; }
        public int DdgiProxyExcludedPatchCount { get; init; }
        public uint DdgiProxyLodPolicyVersion { get; init; }
        public string DdgiProxyFallbackReason { get; init; } = string.Empty;
    }

    public sealed record PerformanceGlobalIlluminationSnapshot(
        bool Enabled,
        RenderQualityPreset ActiveQualityPreset,
        GlobalIlluminationMode Mode,
        GlobalIlluminationDebugView DebugView,
        bool RayQuerySupported,
        bool RayQueryActive,
        bool DdgiActive,
        bool SimpleDdgiActive,
        int SimpleDdgiProbeCount,
        int SimpleDdgiProbesUpdated,
        ulong SimpleDdgiRaysPerFrame,
        bool SimpleDdgiTransportV2Active,
        bool SimpleDdgiAutomaticProbeDensityActive,
        int SimpleDdgiTransportSourceRefreshProbeCount,
        int SimpleDdgiTransportSourceRefreshTargetProbeCount,
        int SimpleDdgiTransportSourceRefreshCapacityShortfall,
        bool SimpleDdgiTransportSourceCohortTransitionActive,
        ulong SimpleDdgiTransportSourceCohortTransitionCount,
        int SimpleDdgiTransportSourceCohortElapsedFrames,
        int SimpleDdgiTransportSourceStepStaleProbeCount,
        int SimpleDdgiTransportSourceStepAgeP95Frames,
        int SimpleDdgiTransportSourceStepAgeMaximumFrames,
        float SimpleDdgiTransportSourceStepAgeP95Seconds,
        float SimpleDdgiTransportSourceStepAgeMaximumSeconds,
        int SimpleDdgiTransportSourceCacheReuseProbeCount,
        ulong SimpleDdgiTransportSourceRayCount,
        ulong SimpleDdgiTransportSolveRayCount,
        int SimpleDdgiTransportPublishedProbeCount,
        int SimpleDdgiTransportPublishRegionCount,
        ulong SimpleDdgiTransportPublishedProbeTotal,
        ulong SimpleDdgiTransportPublishRegionTotal,
        ulong SimpleDdgiUpdateTransactionAbortCount,
        ulong SimpleDdgiTransportSourceCacheInvalidationCount,
        int SimpleDdgiTransportSolverInvalidationCount,
        float SimpleDdgiTransportSolverInvalidationsPerSourceRefresh,
        uint SimpleDdgiSourceLightingGeneration,
        uint SimpleDdgiTransportGeneration,
        int SimpleDdgiTransportSourceReadyProbeCount,
        int SimpleDdgiTransportSourceStaleProbeCount,
        int SimpleDdgiTransportConvergedProbeCount,
        int SimpleDdgiTransportPendingSolverProbeCount,
        bool SimpleDdgiTransportGlobalConvergencePending,
        int SimpleDdgiTransportGlobalConvergenceElapsedFrames,
        ulong SimpleDdgiTransportCalibrationChangeCount,
        float SimpleDdgiTransportSolverRelaxation,
        float SimpleDdgiTransportAlbedoClamp,
        float SimpleDdgiTransportResidualThreshold,
        int SimpleDdgiTransportMaximumSolverGenerations,
        int SimpleDdgiTransportSourceRefreshFrames,
        int SimpleDdgiTransportConfiguredSourceRefreshFrames,
        ulong SimpleDdgiTransportIrradianceAtlasBytes,
        ulong SimpleDdgiTransportSourceCacheBytes,
        int SimpleDdgiInactiveProbeCount,
        int SimpleDdgiInactiveProbeSkipCount,
        ulong SimpleDdgiSavedRaysPerFrame,
        int SimpleDdgiLightingDirtyFrames,
        int SimpleDdgiLightingDirtyBoostedCapacity,
        uint SimpleDdgiDirtyReasonFlags,
        int SimpleDdgiFullRayProbeUpdateCount,
        int SimpleDdgiMaintenanceRayProbeUpdateCount,
        ulong SimpleDdgiAdaptiveRaySavedRaysPerFrame,
        ulong SimpleDdgiAtlasBytes,
        bool SimpleDdgiSampledAtlasRequested,
        bool SimpleDdgiSampledAtlasActive,
        int SimpleDdgiSampledAtlasGroupCount,
        int SimpleDdgiSampledAtlasLayersPerTexture,
        ulong SimpleDdgiSampledAtlasImageBytes,
        string SimpleDdgiSampledAtlasFallbackReason,
        long GpuSimpleDdgiTraceMicroseconds,
        long GpuSimpleDdgiTransportMicroseconds,
        long GpuSimpleDdgiDirectionalRadianceMicroseconds,
        long GpuSimpleDdgiBlendMicroseconds,
        uint SimpleDdgiTransportEnergySampleCount,
        uint SimpleDdgiTransportSourceCacheHitCount,
        uint SimpleDdgiTransportSourceCacheMissCount,
        float SimpleDdgiTransportBounceLuminanceAverage,
        float SimpleDdgiTransportSourceLuminanceAverage,
        float SimpleDdgiTransportTotalLuminanceAverage,
        float DdgiReceiverDiffuseReflectanceLuminance,
        uint DdgiReceiverDiffuseReflectanceSampleCount,
        float DdgiTraceOneSidedBackFaceAlbedoLuminance,
        uint DdgiTraceOneSidedBackFaceHitCount,
        float DdgiTraceOpaqueAlbedoLuminance,
        uint DdgiTraceOpaqueHitCount,
        float DdgiTraceThinSurfaceAlbedoLuminance,
        uint DdgiTraceThinSurfaceHitCount,
        float DdgiTraceUnsupportedTransmissionAlbedoLuminance,
        uint DdgiTraceUnsupportedTransmissionHitCount,
        float DdgiTraceReflectDisabledAlbedoLuminance,
        uint DdgiTraceReflectDisabledHitCount,
        int DdgiProbeVolumeCount,
        int DdgiProbeCount,
        int DdgiActiveProbeCount,
        int DdgiProbesUpdated,
        int DdgiProbeUpdatePrimaryRayBudget,
        float DdgiAverageSpatialCoverageEstimate,
        float DdgiAverageSupportCoverageEstimate,
        float DdgiAverageDataConfidenceEstimate,
        float DdgiAverageVisibilityConfidenceEstimate,
        float DdgiAverageLeakAttenuationEstimate,
        float DdgiAverageEffectiveContributionEstimate,
        float DdgiAverageOwnershipConsumedEstimate,
        float DdgiAverageRelocationFractionEstimate,
        float DdgiRelocatedProbeFractionEstimate,
        float DdgiAverageRelocationDisplacementFractionEstimate,
        int DdgiClassifiedInactiveProbeCountEstimate,
        DdgiQualityTier DdgiQualityTier,
        float DdgiAdaptiveBudgetScale,
        int DdgiAdaptiveBudgetReduced,
        int DdgiEmergencyDegradeActive,
        int DdgiEffectiveMaxShadedLights,
        string DdgiAdaptiveBudgetReason,
        ulong DdgiScheduledPrimaryRayCount,
        ulong DdgiEstimatedShadowRayUpperBound,
        ulong DdgiSelectedDirectionalHitCount,
        ulong DdgiSelectedLocalHitCount,
        ulong DdgiVisibilityRayCount,
        ulong DdgiSkippedLocalLightCount,
        string DdgiLightSelectionMode,
        int DdgiEmissiveSourceCount,
        uint DdgiEmissiveSourceRevision,
        int ParticleDdgiSampleCount,
        int VfxDirtyProbeEventCount,
        int DdgiNewProbeCount,
        int DdgiDirtyBoundsProbeUpdateCount,
        ulong SimpleDdgiMutationJournalLastConsumedSerial,
        ulong SimpleDdgiMutationJournalEnqueuedEventCount,
        ulong SimpleDdgiMutationJournalCoalescedEventCount,
        ulong SimpleDdgiMutationJournalOverflowCount,
        ulong SimpleDdgiMutationJournalConservativeFallbackCount,
        ulong SimpleDdgiMutationJournalAttachScanCount,
        ulong SimpleDdgiMutationJournalAttachObjectCount,
        ulong SimpleDdgiMutationJournalOracleComparisonCount,
        ulong SimpleDdgiMutationJournalOracleMismatchCount,
        int SimpleDdgiMutationJournalPendingEventCount,
        int SimpleDdgiMutationJournalOutputRegionCount,
        int SimpleDdgiMutationJournalOverflowedThisFrame,
        int DdgiVisibleFrustumProbeUpdateCount,
        int DdgiOutsideFrustumSafetyProbeUpdateCount,
        int DdgiAgeRefreshProbeUpdateCount,
        int DdgiHighVarianceProbeUpdateCount,
        int DdgiLowConfidenceProbeUpdateCount,
        int DdgiStableProbeUpdateCount,
        float DdgiAverageProbeVariability,
        float DdgiAverageProbeConfidence,
        ulong DdgiTextureBytes,
        ulong DdgiBufferBytes,
        ulong DdgiProbeVolumeBufferBytes,
        ulong DdgiProbeStateBufferBytes,
        ulong DdgiProbeUpdateQueueBytes,
        ulong DdgiProbeRelocationClassificationBytes,
        uint DdgiTraceDispatchGroupCount,
        uint DdgiTraceProbeCount,
        uint DdgiTraceRayCount,
        uint DdgiBlendProbeCount,
        uint DdgiRelocateClassifyProbeCount,
        uint DdgiPublishProbeCount,
        ulong DdgiCurrentIrradianceAtlasBytes,
        ulong DdgiCurrentVisibilityAtlasBytes,
        int DdgiUpdateExecuted,
        string DdgiUpdateSkipReason,
        ulong DdgiRayScratchBytes,
        ulong DdgiUpdatedAtlasBytes,
        int DdgiPublishExecuted,
        string DdgiPublishSkipReason,
        int DdgiPublishedCacheLatencyFrames,
        uint DdgiCacheGeneration,
        ulong DdgiLastUpdatedFrameSerial,
        DdgiRuntimeWarmupState DdgiCacheWarmupState,
        int DdgiActiveLocalSlotCount,
        string DdgiCacheClearReason,
        ulong AccelerationStructureBytes,
        ulong AccelerationStructureScratchBytes,
        ulong AccelerationStructureInstanceBufferBytes,
        ulong AccelerationStructureRayQueryMetadataBytes,
        int AccelerationStructureBlasBuildCount,
        int AccelerationStructureBlasCompactionQueryCount,
        int AccelerationStructureBlasCompactionCount,
        ulong AccelerationStructureBlasCompactionSourceBytes,
        ulong AccelerationStructureBlasCompactionBytesSaved,
        ulong AccelerationStructureBlasCompactedResidentBytesSaved,
        int AccelerationStructureBlasCompactionPendingCount,
        int AccelerationStructureBlasCompactionQueryOverflowCount,
        int AccelerationStructureBlasCompactionQueryReadbackFailureCount,
        int AccelerationStructureTlasBuildCount,
        int AccelerationStructureTlasUpdateCount,
        int AccelerationStructureTlasSkipCount,
        ulong AccelerationStructureInstanceUploadBytes,
        ulong AccelerationStructureRayQueryMetadataUploadBytes,
        long CpuRecordMicroseconds,
        long CpuRecordP95Microseconds,
        int CpuTimingSampleCount,
        long CpuAccelerationStructureBuildMicroseconds,
        long CpuAccelerationStructureBlasBuildMicroseconds,
        long CpuAccelerationStructureBlasCompactionMicroseconds,
        long CpuAccelerationStructureTlasBuildMicroseconds,
        long CpuAccelerationStructureInstanceUploadMicroseconds,
        long GpuDdgiTraceMicroseconds,
        long GpuDdgiBlendMicroseconds,
        long GpuDdgiRelocateClassifyMicroseconds,
        long GpuDdgiPublishMicroseconds,
        long GpuAccelerationStructureBlasMicroseconds,
        long GpuAccelerationStructureTlasMicroseconds,
        long GpuMicroseconds,
        IReadOnlyList<DdgiVolumeDiagnosticsEntry> DdgiVolumes,
        string LikelyBottleneck)
    {
        /// <summary>Raw whole-forward-draw timing retained for attribution only.</summary>
        public long ForwardGiInclusiveMicroseconds { get; init; }
        public GiTimingAttribution ForwardGiInclusiveAttribution { get; init; } = GiTimingAttribution.Unavailable;
        /// <summary>Only populated by an isolated scope or deterministic paired capture.</summary>
        public long ForwardGiIncrementalMicroseconds { get; init; }
        public GiTimingAttribution ForwardGiIncrementalAttribution { get; init; } = GiTimingAttribution.Unavailable;
        public string ForwardGiIncrementalReason { get; init; } = "No isolated or paired forward-GI timing is available.";
        /// <summary>Hard-cap and latency evidence for the active Simple-DDGI scheduler.</summary>
        public SimpleDdgiSchedulingTelemetry SimpleDdgiScheduling { get; init; } =
            SimpleDdgiSchedulingTelemetry.Unavailable("Simple DDGI scheduling was not captured.");
        /// <summary>
        /// Fence-complete maximum directional error and actual saved source-ray
        /// work, split by ring and stable DDGI volume kind.
        /// </summary>
        public SimpleDdgiAdaptiveRayEvidence SimpleDdgiAdaptiveRayEvidence
        {
            get;
            init;
        }
        /// <summary>
        /// Mutation-to-first-visible and mutation-to-certified latency for all
        /// six production edit classes.
        /// </summary>
        public SimpleDdgiMutationLatencyTelemetry SimpleDdgiMutationLatency
        {
            get;
            init;
        } = SimpleDdgiMutationLatencyTelemetry.Empty;
        /// <summary>Authored GI intent, retained even when a live fallback suppresses rendering.</summary>
        public bool Requested { get; init; }
        public GlobalIlluminationMode RequestedMode { get; init; } = GlobalIlluminationMode.Disabled;
        public GlobalIlluminationDebugView RequestedDebugView { get; init; } = GlobalIlluminationDebugView.None;
        public bool EmergencyGiFallbackActive { get; init; }
        public string FallbackReason { get; init; } = string.Empty;
        /// <summary>Requested/accepted layout evidence, including rejected source volumes.</summary>
        public SimpleDdgiLayoutTelemetry SimpleDdgiLayout { get; init; } =
            SimpleDdgiLayoutTelemetry.Unavailable("Simple DDGI layout was not captured.");
        public SimpleDdgiProbeResidencyTelemetry SimpleDdgiProbeResidency { get; init; } =
            SimpleDdgiProbeResidencyTelemetry.Unavailable(
                "Simple DDGI probe residency was not captured.");
        /// <summary>Visible-first scheduler class and pressure evidence.</summary>
        public SimpleDdgiSchedulerPolicyTelemetry SimpleDdgiSchedulerPolicy { get; init; } =
            SimpleDdgiSchedulerPolicyTelemetry.Unavailable("Simple DDGI scheduler policy was not captured.");
        /// <summary>Generation-aligned producer evidence and bounded watchdog result.</summary>
        public SimpleDdgiLivenessTelemetry SimpleDdgiLivenessTelemetry { get; init; } =
            SimpleDdgiLivenessTelemetry.Empty;
        public SimpleDdgiLivenessWatchdogResult SimpleDdgiLivenessWatchdog { get; init; } =
            SimpleDdgiLivenessWatchdogResult.Empty;
        public bool GiPipelineCacheLoaded { get; init; }
        public bool GiPipelineCacheRejected { get; init; }
        public bool GiPipelineCacheSaved { get; init; }
        public ulong GiPipelineCacheLoadedPayloadBytes { get; init; }
        public ulong GiPipelineCacheSavedPayloadBytes { get; init; }
        public ulong GiPipelineCreationCount { get; init; }
        public long GiPipelineCreationMicroseconds { get; init; }
        public ulong GiRenderCriticalPipelineCreationCount { get; init; }
        public string GiPipelineCachePath { get; init; } = string.Empty;
        public string GiPipelineCacheStatus { get; init; } = string.Empty;
        public string GiLastCreatedPipeline { get; init; } = string.Empty;
        public SimpleDdgiSchedulerMode SimpleDdgiSchedulerMode { get; init; } =
            SimpleDdgiSchedulerMode.CpuReference;
        public int SimpleDdgiTraceContentProfile { get; init; }
        public int SimpleDdgiTraceDistanceProfile { get; init; }
        public bool SimpleDdgiTraceSpecialized { get; init; }
        public int SimpleDdgiTraceWorkgroupSize { get; init; } = 64;
        public bool SimpleDdgiCostAwareSchedulingActive { get; init; }
        public ulong SimpleDdgiSchedulerCostSampleCount { get; init; }
        public float SimpleDdgiSchedulerVisibilityPerPrimary { get; init; }
        public float SimpleDdgiSchedulerAlphaCandidatesPerPrimary { get; init; }
        public float SimpleDdgiSchedulerMaterialEvaluationsPerPrimary { get; init; }
        public float SimpleDdgiSchedulerFarFieldStepsPerPrimary { get; init; }
        public bool SimpleDdgiSparseResidualPropagationActive { get; init; }
        public uint SimpleDdgiResidualSeededCount { get; init; }
        public uint SimpleDdgiResidualDependentWakeCount { get; init; }
        public uint SimpleDdgiResidualThresholdRejectedCount { get; init; }
        public uint SimpleDdgiResidualCompleteSweepFallbackCount { get; init; }
        public bool SimpleDdgiUrgentRelightActive { get; init; }
        public uint SimpleDdgiUrgentRelightAcceptedCount { get; init; }
        public uint SimpleDdgiUrgentRelightCommittedCount { get; init; }
        public uint SimpleDdgiUrgentRelightRejectedCount { get; init; }
        public long GpuSimpleDdgiUrgentRelightMicroseconds { get; init; }
        public bool SimpleDdgiSchedulerFallbackLatched { get; init; }
        public bool SimpleDdgiSchedulerFallbackFreshResetPending { get; init; }
        public ulong SimpleDdgiSchedulerFallbackCount { get; init; }
        public string SimpleDdgiSchedulerFallbackReason { get; init; } = string.Empty;
        public bool SimpleDdgiSchedulerFallbackExportPending { get; init; }
        public ulong SimpleDdgiSchedulerFallbackExportBytes { get; init; }
        public ulong SimpleDdgiSchedulerStateExportSuccessCount { get; init; }
        public ulong SimpleDdgiSchedulerStateExportFailureCount { get; init; }
        public int SimpleDdgiSchedulerReentryStableFrameCount { get; init; }
        public ulong SimpleDdgiSchedulerReentryCount { get; init; }
        public string SimpleDdgiTailCertificationFallbackReason { get; init; } = string.Empty;
        /// <summary>Compact receiver publication/resource evidence for this frame.</summary>
        public ulong SimpleDdgiReceiverProbeBytes { get; init; }
        public int SimpleDdgiReceiverProbeCapacity { get; init; }
        public ulong SimpleDdgiReceiverInvalidationBytes { get; init; }
        public int SimpleDdgiReceiverInvalidationRangeCount { get; init; }
        public bool SimpleDdgiReceiverFullClear { get; init; }
        public uint SimpleDdgiReceiverResourceGeneration { get; init; }
        public int SimpleDdgiReceiverRecordsPublished { get; init; }
        /// <summary>
        /// Surface-aware receiver-cache mode, ABI, memory, fallback, and
        /// optional fence-complete rejection evidence.
        /// </summary>
        public SimpleDdgiReceiverCacheDiagnostics SimpleDdgiReceiverCache
        {
            get;
            init;
        } = SimpleDdgiReceiverCacheDiagnostics.Exact(
            SimpleDdgiReceiverCacheMode.Exact,
            SimpleDdgiReceiverCacheFallbackReason.ExactRequested,
            "receiver-cache diagnostics unavailable");
        /// <summary>Exact canonical, cache, scratch, and optional mirror allocation contract.</summary>
        public SimpleDdgiStorageDiagnostics SimpleDdgiStorage { get; init; } =
            SimpleDdgiStorageDiagnostics.Unavailable;
        public SimpleDdgiWarmStartTelemetry SimpleDdgiWarmStart { get; init; } =
            SimpleDdgiWarmStartTelemetry.Disabled(
                "Persistent Simple-DDGI warm-start telemetry is unavailable.");
        public SimpleDdgiRefinementBrickDiagnostics SimpleDdgiRefinement { get; init; } =
            new(false, 0, 0, 0, 0, 0, 0, false, "disabled");
        public SimpleDdgiRefinementEmissiveDemandDiagnostics
            SimpleDdgiRefinementEmissiveDemand { get; init; }
        public SimpleDdgiNearVisibilityDiagnostics
            SimpleDdgiNearVisibility { get; init; } =
                SimpleDdgiNearVisibilityDiagnostics.Disabled();
        /// <summary>Fail-closed C5 timing/counter readback and allocation evidence.</summary>
        public SimpleDdgiNearFieldResidualDiagnostics
            SimpleDdgiNearFieldResidual { get; init; } =
                SimpleDdgiNearFieldResidualDiagnostics.Disabled();
        public GiRoadmapExperimentDiagnostics GiRoadmapExperiments { get; init; } =
            GiRoadmapExperimentDiagnostics.Disabled;
        public SimpleDdgiContentMemoryPlan SimpleDdgiContentMemory { get; init; } =
            SimpleDdgiContentMemoryPlan.Empty;
    }

    public sealed class PerformanceSnapshotWriter
    {
        private static readonly IReadOnlyList<PerformanceMetricSemanticEntry>
            CounterSemanticManifest = BuildCounterSemanticManifest();
        internal static readonly JsonSerializerOptions SerializerOptions = new()
        {
            AllowTrailingCommas = false,
            MaxDepth = 64,
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                // The converter writes stable names for human-facing captures and continues to
                // accept numeric enum values from schema-v2 baseline files.
                new JsonStringEnumConverter()
            }
        };

        public string Write(string directory, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Snapshot directory is required.", nameof(directory));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (budget == null)
                throw new ArgumentNullException(nameof(budget));

            // The diagnostics object is persisted alongside Capture. Normalize the run identity
            // once at the boundary so both representations agree and legacy generic placeholders
            // cannot survive in a release artifact through the diagnostics copy.
            SimpleDdgiNearFieldResidualDiagnostics nearFieldResidual =
                (diagnostics.SimpleDdgiNearFieldResidual ??
                 SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                     "C5 telemetry was not supplied by renderer diagnostics."))
                .NormalizeForPersistence();
            SimpleDdgiReceiverFeedbackDiagnostics receiverFeedback =
                (diagnostics.GiRoadmapExperiments.ReceiverFeedbackRuntime ??
                 SimpleDdgiReceiverFeedbackDiagnostics.Disabled)
                .NormalizeForPersistence();
            diagnostics = diagnostics with
            {
                CaptureRun = NormalizeCaptureRunMetadata(diagnostics.CaptureRun),
                SimpleDdgiNearFieldResidual = nearFieldResidual,
                GiRoadmapExperiments = diagnostics.GiRoadmapExperiments with
                {
                    ReceiverFeedbackRuntime = receiverFeedback
                }
            };

            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            string path = Path.Combine(
                directory,
                $"performance-{capturedAt:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
            var snapshot = new PerformanceSnapshot(
                capturedAt,
                budget.Profile,
                diagnostics,
                CreateFoliageSnapshot(diagnostics),
                CreateGlobalIlluminationSnapshot(diagnostics),
                CreateWarnings(diagnostics, budget.Profile),
                budget)
            {
                SchemaVersion = PerformanceSnapshot.CurrentSchemaVersion,
                OriginalSchemaVersion = PerformanceSnapshot.CurrentSchemaVersion,
                Capture = CreateCaptureMetadata(diagnostics),
                StructuredWarnings = CreateStructuredWarnings(diagnostics),
                GiTiming = CreateGiTimingAttributionSnapshot(diagnostics),
                GiResidency = budget.GiResidency.UniqueMeasurementAvailable
                    ? budget.GiResidency
                    : GiResidencyReporter.Create(
                        diagnostics,
                        budget.Memory,
                        RenderBudgetProfile.GetDefault(
                            diagnostics.ActiveBudgetProfile)),
                MemoryAudit = CreateMemoryOwnershipAudit(diagnostics, budget.Memory),
                ContentDependentDdgi = diagnostics.ContentDependentDdgi
            };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                SerializerOptions);
            return DurableJsonFileWriter.Write(
                path,
                payload,
                "performance snapshot");
        }

        private static IReadOnlyList<GiDiagnosticWarning> CreateStructuredWarnings(RendererDiagnostics diagnostics)
        {
            if (diagnostics.GiWarnings.Count > 0)
                return diagnostics.GiWarnings;

            // VulkanRenderer supplies the stateful evaluation for live frames. This fallback
            // keeps direct writer callers truthful as well: it can report feature/counter
            // availability, but intentionally cannot fabricate persistence from one snapshot.
            IReadOnlyList<GiFeatureState> featureStates = diagnostics.GiFeatureStates.Count > 0
                ? diagnostics.GiFeatureStates
                : GiFeatureStateFactory.Create(diagnostics);
            GiWarningEvaluationResult evaluation = new GiWarningEvaluator().Evaluate(diagnostics);
            return GiDiagnosticWarningFactory.Create(diagnostics, evaluation, featureStates);
        }

        private static IReadOnlyList<string> CreateWarnings(
            RendererDiagnostics diagnostics,
            RenderBudgetProfile profile)
        {
            var warnings = new List<string>(7);
            if (diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0)
                warnings.Add("Emergency GI fallback is active; dynamic GI is suppressed for this capture.");
            DdgiContentRuntimeSnapshot content =
                diagnostics.ContentDependentDdgi;
            if (content.LightTree.AllocationFailureCount > 0)
            {
                warnings.Add(
                    $"DDGI light-tree allocation failed " +
                    $"{content.LightTree.AllocationFailureCount} time(s); " +
                    "the exact estimator remains authoritative.");
            }
            if (content.LightTree.PublicationValidationFailureCount > 0)
            {
                warnings.Add(
                    $"DDGI light-tree publication validation failed " +
                    $"{content.LightTree.PublicationValidationFailureCount} " +
                    "time(s); invalid candidates were rejected.");
            }
            if (content.RequestedDirectionalRadianceMode !=
                    SimpleDdgiDirectionalRadianceMode.Off &&
                content.EffectiveDirectionalRadianceMode ==
                    SimpleDdgiDirectionalRadianceMode.Off)
            {
                warnings.Add(
                    "DDGI directional radiance fell back: " +
                    content.DirectionalRadianceFallbackReason);
            }
            if (content.RequestedFoliageGeometryMode !=
                    DdgiFoliageGeometryMode.Excluded &&
                content.EffectiveFoliageGeometryMode ==
                    DdgiFoliageGeometryMode.Excluded)
            {
                warnings.Add(
                    "DDGI foliage geometry fell back: " +
                    (string.IsNullOrWhiteSpace(content.FoliageFallbackReason)
                        ? "no qualified proxy representation was active"
                        : content.FoliageFallbackReason));
            }
            if (diagnostics.PendingMaterialTextureFanoutCount != 0)
            {
                warnings.Add(
                    $"{diagnostics.PendingMaterialTextureFanoutCount} texture-to-material fan-out publication(s) are pending; rendering remains fail-closed until retry.");
            }
            if (diagnostics.MaterialBindingRepairPending != 0)
            {
                warnings.Add(
                    "Material buffer descriptor publication requires repair; rendering remains fail-closed.");
            }
            if (diagnostics.PendingRetiredMaterialBufferCount != 0 ||
                diagnostics.QuarantinedMaterialBufferCount != 0)
            {
                warnings.Add(
                    $"{diagnostics.PendingRetiredMaterialBufferCount} retired material buffer(s) await destruction and " +
                    $"{diagnostics.QuarantinedMaterialBufferCount} candidate buffer(s) remain quarantined.");
            }
            if (diagnostics.MaterialRetiredBufferCleanupFailureCount != 0)
            {
                warnings.Add(
                    $"{diagnostics.MaterialRetiredBufferCleanupFailureCount} retired material-buffer cleanup attempt(s) failed and were retained for retry.");
            }
            if (diagnostics.MeshRetainedDeadByteBudgetRejectionCount != 0)
            {
                warnings.Add(
                    $"Mesh stream fragmentation admission has rejected {diagnostics.MeshRetainedDeadByteBudgetRejectionCount} registration request(s); " +
                    $"retained dead bytes are {diagnostics.MeshRetainedDeadBytes} of {diagnostics.MeshRetainedDeadByteBudget}.");
            }
            if (diagnostics.MeshPostCommitCleanupFailureCount != 0)
            {
                warnings.Add(
                    $"{diagnostics.MeshPostCommitCleanupFailureCount} post-commit mesh cleanup operation(s) were deferred or quarantined.");
            }
            if (diagnostics.SimpleDdgiLayout.IsAvailable &&
                diagnostics.SimpleDdgiLayout.HasRequiredDegradation)
                warnings.Add("Simple DDGI layout was degraded before allocation: " + diagnostics.SimpleDdgiLayout.Summary);
            if (diagnostics.SimpleDdgiLayout.IsAvailable &&
                diagnostics.SimpleDdgiLayout.HasRequiredDegradation &&
                (diagnostics.SimpleDdgiLayout.RequestedProbeCount > diagnostics.SimpleDdgiLayout.ProbeBudget ||
                 diagnostics.SimpleDdgiLayout.RequestedPersistentBytes > diagnostics.SimpleDdgiLayout.PersistentMemoryBudgetBytes ||
                 diagnostics.SimpleDdgiLayout.RequestedVolumeCount > diagnostics.SimpleDdgiLayout.VolumeBudget))
            {
                warnings.Add("Requested Simple DDGI layout exceeds one or more resolved tier budgets.");
            }
            if (diagnostics.HiZEnabled != 0 && diagnostics.HiZConsumerCount == 0)
                warnings.Add("Hi-Z build is enabled but no active Hi-Z consumers were reported.");
            if (diagnostics.HiZEnabled != 0 && diagnostics.HiZCounterSource == HiZCounterSource.Unavailable)
                warnings.Add("Hi-Z build is enabled but no Hi-Z counter source is available.");
            if (diagnostics.OcclusionEnabled != 0 && diagnostics.ForwardHiZTestedCount == 0)
                warnings.Add("Hi-Z occlusion is enabled but no forward Hi-Z tests were reported.");
            if (diagnostics.ForwardVisibilityCompactionEnabled != 0 &&
                diagnostics.ForwardVisibilityCompactionActive == 0 &&
                !string.IsNullOrWhiteSpace(diagnostics.ForwardVisibilityCompactionSkipReason))
            {
                warnings.Add("Current-frame forward visibility compaction fell back: " +
                    diagnostics.ForwardVisibilityCompactionSkipReason);
            }
            if (diagnostics.SceneSubmissionGpuOpaqueOverflowCount > 0)
                warnings.Add("Scene-submission GPU opaque compaction overflowed.");
            if (diagnostics.SceneSubmissionValidationMismatchCount > 0)
                warnings.Add("Scene-submission CPU/GPU validation reported mismatches.");
            if (diagnostics.GlobalIlluminationDdgiActive != 0 &&
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0UL &&
                diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes > diagnostics.DdgiAtlasMemoryBudgetBytes)
            {
                warnings.Add("DDGI total memory exceeds the configured tier budget.");
            }
            bool forwardGiRequired = diagnostics.GlobalIlluminationDdgiActive != 0 ||
                diagnostics.SimpleDdgiActive != 0;
            if (forwardGiRequired && !HasForwardGiIncrementalTiming(diagnostics))
            {
                warnings.Add("Forward GI incremental timing is unavailable; the inclusive forward draw is attribution-only and total GI GPU cost is not release-gate ready.");
            }
            if (diagnostics.GlobalIlluminationEnabled != 0 &&
                diagnostics.GlobalIlluminationCpuTimingSampleCount > 0 &&
                diagnostics.CpuGlobalIlluminationRecordP95Microseconds >
                    profile.GlobalIlluminationCpuBudgetMilliseconds * 1000.0)
            {
                warnings.Add("GI CPU scheduling and upload P95 exceeds the configured tier budget.");
            }
            if (diagnostics.MaterialGiReleaseQualificationRequired != 0 &&
                diagnostics.MaterialGiReleaseQualificationFailureCount > 0)
            {
                warnings.Add(
                    "Material-GI V2 is active without a valid shipping qualification: " +
                    diagnostics.MaterialGiReleaseQualificationSummary);
            }
            if ((diagnostics.MaterialGiV2ActiveFeatures &
                 MaterialGiV2Feature.MaterialTransport) != 0)
            {
                if (diagnostics.MaterialActiveLegacyV1FallbackCount > 0)
                {
                    warnings.Add(
                        $"{diagnostics.MaterialActiveLegacyV1FallbackCount} active material(s) still use the V1 compatibility path.");
                }
                if (diagnostics.MaterialActiveInvalidProfileCount > 0)
                {
                    warnings.Add(
                        $"{diagnostics.MaterialActiveInvalidProfileCount} active material(s) have invalid compact transport profiles.");
                }
                if (diagnostics.MaterialCompileTimingSampleCount > 0 &&
                    diagnostics.MaterialUploadTimingSampleCount > 0 &&
                    diagnostics.MaterialCompileP95Microseconds +
                    diagnostics.MaterialUploadP95Microseconds >
                        profile.GlobalIlluminationCpuBudgetMilliseconds * 1000.0)
                {
                    warnings.Add(
                        "Material compile/upload P95 exceeds the configured GI CPU scheduling/upload budget.");
                }
                ulong primitiveProfileTierBudget =
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                        diagnostics.ActiveQualityPreset);
                if (diagnostics.MaterialPrimitiveProfileGpuBytes > primitiveProfileTierBudget)
                {
                    warnings.Add(
                        $"Primitive transport profiles use {diagnostics.MaterialPrimitiveProfileGpuBytes} GPU bytes, " +
                        $"exceeding the {primitiveProfileTierBudget}-byte {diagnostics.ActiveQualityPreset} tier cap.");
                }
            }
            if (diagnostics.SimpleDdgiSampledAtlasRequested != 0 &&
                diagnostics.SimpleDdgiSampledAtlasActive == 0 &&
                !string.IsNullOrWhiteSpace(diagnostics.SimpleDdgiSampledAtlasFallbackReason))
            {
                warnings.Add("Sampled Simple DDGI atlas fell back to the canonical SSBO path: " +
                    diagnostics.SimpleDdgiSampledAtlasFallbackReason);
            }
            if (diagnostics.DdgiDetailedCountersEnabled != 0 &&
                diagnostics.DdgiInvestigationCountersReadbackValid == 0)
            {
                warnings.Add("Detailed GI counter readback is unavailable; counter-based quality diagnostics are unavailable.");
            }
            if (diagnostics.FoliageDdgiTransportExcludedClusterCount > 0)
            {
                warnings.Add(
                    $"{diagnostics.FoliageDdgiTransportExcludedClusterCount} foliage cluster(s) are excluded " +
                    $"from DDGI occlusion/source transport: {diagnostics.FoliageDdgiTransportExclusionReason}.");
            }
            if (diagnostics.DdgiFoliageDroppedTriangleCount > 0)
            {
                warnings.Add(
                    $"The DDGI foliage proxy triangle budget dropped " +
                    $"{diagnostics.DdgiFoliageDroppedTriangleCount} requested triangle(s).");
            }
            if (diagnostics.SimpleDdgiDirtyFirstUpdateLatencySampleCount > 0 &&
                diagnostics.SimpleDdgiDirtyFirstUpdateLatencyP95Frames > 1)
            {
                warnings.Add("Simple DDGI dirty-to-first-update P95 exceeds the one-frame response target.");
            }
            if (diagnostics.SimpleDdgiDirtyConvergenceLatencySampleCount > 0 &&
                diagnostics.SimpleDdgiDirtyConvergenceLatencyP95Frames > 8)
            {
                warnings.Add("Simple DDGI dirty-to-convergence P95 exceeds the eight-frame target.");
            }
            foreach (SimpleDdgiMutationLatencySnapshot latency in
                     diagnostics.SimpleDdgiMutationLatency.Enumerate())
            {
                if (latency.FirstVisibleResponse.SampleCount > 0 &&
                    latency.FirstVisibleResponse.P95Frames > 1)
                {
                    warnings.Add(
                        $"Simple DDGI {latency.MutationClass} mutation-to-first-visible P95 " +
                        "exceeds the one-frame target.");
                }
                if (latency.CertifiedConvergence.SampleCount > 0 &&
                    latency.CertifiedConvergence.P95Frames > 8)
                {
                    warnings.Add(
                        $"Simple DDGI {latency.MutationClass} mutation-to-certified P95 " +
                        "exceeds the eight-frame target.");
                }
            }
            if (diagnostics.StreamedGiAccelerationStructuresFeatureEnabled != 0 &&
                diagnostics.AccelerationStructureMemoryBudgetBytes > 0UL &&
                diagnostics.AccelerationStructureResidentBytes > diagnostics.AccelerationStructureMemoryBudgetBytes)
            {
                warnings.Add("Resident GI acceleration structures exceed the configured hard memory limit.");
            }
            if (diagnostics.AccelerationStructureBlasBudgetRejectedCount > 0)
                warnings.Add("GI acceleration-structure residency rejected the complete resident set under the active budget; no partial TLAS was published.");
            if (diagnostics.FarFieldMemoryBudgetBytes > 0UL &&
                diagnostics.FarFieldCacheBytes > diagnostics.FarFieldMemoryBudgetBytes)
            {
                warnings.Add("Far-field page cache exceeds the configured hard memory limit.");
            }
            if (diagnostics.DdgiBlackFrameSuspect != 0)
                warnings.Add("DDGI black-frame suspect was reported; inspect support and environment-fallback diagnostics.");
            return warnings;
        }

        private static PerformanceCaptureMetadata CreateCaptureMetadata(RendererDiagnostics diagnostics)
        {
            IReadOnlyList<GiFeatureState> featureStates = diagnostics.GiFeatureStates.Count > 0
                ? diagnostics.GiFeatureStates
                : GiFeatureStateFactory.Create(diagnostics);
            return new PerformanceCaptureMetadata(
                string.IsNullOrWhiteSpace(diagnostics.CaptureGpuDeviceName)
                    ? "unknown-device"
                    : diagnostics.CaptureGpuDeviceName,
                string.IsNullOrWhiteSpace(diagnostics.CaptureGpuDriverVersion)
                    ? "unknown-driver"
                    : diagnostics.CaptureGpuDriverVersion,
                diagnostics.CaptureRenderWidth,
                diagnostics.CaptureRenderHeight,
                diagnostics.ActiveQualityPreset,
                diagnostics.CaptureSceneContentRevision,
                CreateDebugState(diagnostics),
                CreateFeatureFlags(diagnostics),
                CreateGiTimingCoverage(diagnostics))
            {
                Run = NormalizeCaptureRunMetadata(diagnostics.CaptureRun),
                Camera = diagnostics.CaptureCamera,
                Frame = diagnostics.CaptureFrame,
                ResolvedGiSettings = diagnostics.ResolvedGiSettings.StableHash == "unknown"
                    ? ResolvedGiSettingsMetadataFactory.Create(diagnostics)
                    : diagnostics.ResolvedGiSettings,
                Measurement = diagnostics.GiMeasurement,
                FeatureStates = featureStates,
                SceneStateHash = NormalizeRunMetadataValue(
                    diagnostics.CaptureSceneStateHash,
                    "unavailable:scene-state-hash-not-reported"),
                SceneAssetHash = NormalizeRunMetadataValue(
                    diagnostics.CaptureSceneAssetHash,
                    "unavailable:scene-asset-hash-not-reported"),
                ValidationState = diagnostics.ValidationMode.ToString(),
                PairedCaptureIdentity = CreatePairedCaptureIdentity(diagnostics),
                CounterSemantics = CreateCounterSemantics()
            };
        }

        internal static string CreatePairedCaptureIdentity(
            RendererDiagnostics diagnostics)
        {
            string canonical = string.Join("|", new[]
            {
                diagnostics.CaptureGpuDeviceName,
                diagnostics.CaptureGpuDriverVersion,
                diagnostics.CaptureRenderWidth.ToString(),
                diagnostics.CaptureRenderHeight.ToString(),
                diagnostics.ActiveQualityPreset.ToString(),
                diagnostics.CaptureSceneAssetHash,
                diagnostics.CaptureCamera.ViewHash,
                diagnostics.CaptureCamera.ProjectionHash,
                diagnostics.CaptureRun.ExecutableHash,
                diagnostics.CaptureRun.Commit,
                diagnostics.CaptureRun.DirtyWorktreeState,
                diagnostics.CaptureRun.ShaderBundleHash
            });
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal static PerformanceMemoryOwnershipAudit CreateMemoryOwnershipAudit(
            RendererDiagnostics diagnostics,
            MemoryBudgetSnapshot memory)
        {
            ulong tracked = memory.TotalTrackedBytes;
            ulong budget = memory.BudgetBytes;
            ulong headroom = budget > tracked ? budget - tracked : 0UL;
            double headroomFraction = budget > 0
                ? headroom / (double)budget
                : 0.0;
            SimpleDdgiStorageDiagnostics storage = diagnostics.SimpleDdgiStorage;
            ulong transportBytes = SaturatingAdd(
                diagnostics.SimpleDdgiTransportIrradianceAtlasBytes,
                storage.IsAvailable
                    ? storage.SourceCacheBytes
                    : diagnostics.SimpleDdgiTransportSourceCacheBytes);
            ulong scratchAndQueueBytes = SaturatingAdd(
                diagnostics.SimpleDdgiRayScratchBytes,
                diagnostics.SimpleDdgiProbeStateBytes,
                diagnostics.SimpleDdgiReceiverProbeBytes,
                diagnostics.SimpleDdgiProbeUpdateQueueBytes,
                diagnostics.SimpleDdgiRelocationClassificationBytes);
            var findings = new List<string>();
            if (budget == 0)
                findings.Add("Configured tracked-memory budget is unavailable.");
            else if (headroomFraction < 0.20)
                findings.Add($"Tracked-memory headroom is {headroomFraction:P2}; the contract requires at least 20%.");
            if (diagnostics.SimpleDdgiDuplicateMirrorBytes > 0)
                findings.Add("The optional sampled DDGI atlas duplicates canonical SSBO atlas content.");
            if (diagnostics.SimpleDdgiRetiredBufferBytes > 0)
                findings.Add("Fence-retired DDGI generations are still resident in this frame.");
            if (diagnostics.SimpleDdgiDisabledRetainedBytes > 0)
                findings.Add("Disabled Simple-DDGI retains graph-safe placeholder resources.");

            return new PerformanceMemoryOwnershipAudit(
                tracked,
                budget,
                headroom,
                headroomFraction,
                budget > 0 && headroomFraction >= 0.20,
                storage.IsAvailable
                    ? checked(
                        storage.CanonicalIrradianceBytes +
                        storage.CanonicalVisibilityBytes)
                    : diagnostics.SimpleDdgiAtlasBytes >=
                      diagnostics.SimpleDdgiSampledAtlasImageBytes
                        ? diagnostics.SimpleDdgiAtlasBytes -
                          diagnostics.SimpleDdgiSampledAtlasImageBytes
                        : 0UL,
                storage.IsAvailable
                    ? storage.MirrorAllocatedBytes
                    : diagnostics.SimpleDdgiSampledAtlasImageBytes,
                transportBytes,
                diagnostics.SimpleDdgiProbeStateReadbackBytes,
                scratchAndQueueBytes,
                diagnostics.SimpleDdgiRetiredBufferBytes,
                diagnostics.SimpleDdgiRetiredBufferCount,
                diagnostics.SimpleDdgiDisabledRetainedBytes,
                diagnostics.SimpleDdgiDuplicateMirrorBytes,
                Array.AsReadOnly(findings.ToArray()));
        }

        private static ulong SaturatingAdd(params ulong[] values)
        {
            ulong total = 0UL;
            foreach (ulong value in values)
                total = ulong.MaxValue - total < value ? ulong.MaxValue : total + value;
            return total;
        }

        internal static PerformanceCaptureRunMetadata NormalizeCaptureRunMetadata(PerformanceCaptureRunMetadata? run)
        {
            PerformanceCaptureRunMetadata value = run ?? PerformanceCaptureRunMetadata.Unknown;
            return value with
            {
                SceneKind = NormalizeRunMetadataValue(value.SceneKind, "unavailable:scene-kind-not-reported"),
                Scenario = NormalizeRunMetadataValue(value.Scenario, "unavailable:scenario-not-reported"),
                BuildConfiguration = NormalizeRunMetadataValue(value.BuildConfiguration, "unavailable:build-configuration-not-reported"),
                ApplicationVersion = NormalizeRunMetadataValue(value.ApplicationVersion, "unavailable:application-version-not-reported"),
                Commit = NormalizeRunMetadataValue(value.Commit, "unavailable:source-revision-not-reported"),
                ShaderBundleHash = NormalizeRunMetadataValue(value.ShaderBundleHash, "unavailable:shader-bundle-hash-not-reported"),
                ExecutableHash = NormalizeRunMetadataValue(value.ExecutableHash, "unavailable:executable-hash-not-reported"),
                DirtyWorktreeState = NormalizeRunMetadataValue(value.DirtyWorktreeState, "unavailable:dirty-worktree-state-not-reported")
            };
        }

        internal static IReadOnlyList<PerformanceMetricSemanticEntry> CreateCounterSemantics() =>
            CounterSemanticManifest;

        private static IReadOnlyList<PerformanceMetricSemanticEntry> BuildCounterSemanticManifest()
        {
            var entries = new List<PerformanceMetricSemanticEntry>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            AppendCounterSemanticEntries(
                typeof(RendererDiagnostics),
                "Diagnostics",
                entries,
                paths,
                new HashSet<Type>(),
                depth: 0);
            return Array.AsReadOnly(entries
                .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
                .ToArray());
        }

        private static void AppendCounterSemanticEntries(
            Type contractType,
            string parentPath,
            ICollection<PerformanceMetricSemanticEntry> entries,
            ISet<string> paths,
            ISet<Type> ancestors,
            int depth)
        {
            if (depth > 6 || !ancestors.Add(contractType))
                return;

            foreach (PropertyInfo property in contractType
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(static property => property.GetIndexParameters().Length == 0)
                         .Where(static property => property.GetCustomAttribute<ObsoleteAttribute>() == null)
                         .OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                Type propertyType = Nullable.GetUnderlyingType(property.PropertyType) ??
                    property.PropertyType;
                string path = parentPath + "." + property.Name;
                if (IsIntegralCounterType(propertyType) && IsCounterPropertyName(property.Name))
                {
                    AddCounterSemanticEntry(path, property.Name, entries, paths);
                    continue;
                }

                if (TryGetCollectionElementType(propertyType, out Type elementType))
                {
                    Type effectiveElementType = Nullable.GetUnderlyingType(elementType) ??
                        elementType;
                    string collectionPath = path + "[*]";
                    if (IsIntegralCounterType(effectiveElementType) &&
                        IsCounterPropertyName(property.Name))
                    {
                        AddCounterSemanticEntry(collectionPath, property.Name, entries, paths);
                    }
                    else if (ShouldTraverseCounterContract(effectiveElementType))
                    {
                        AppendCounterSemanticEntries(
                            effectiveElementType,
                            collectionPath,
                            entries,
                            paths,
                            ancestors,
                            depth + 1);
                    }

                    continue;
                }

                if (ShouldTraverseCounterContract(propertyType))
                {
                    AppendCounterSemanticEntries(
                        propertyType,
                        path,
                        entries,
                        paths,
                        ancestors,
                        depth + 1);
                }
            }

            ancestors.Remove(contractType);
        }

        private static void AddCounterSemanticEntry(
            string path,
            string propertyName,
            ICollection<PerformanceMetricSemanticEntry> entries,
            ISet<string> paths)
        {
            if (!paths.Add(path))
                return;

            PerformanceMetricSemantic semantic = ResolveCounterSemantic(path, propertyName);
            entries.Add(new PerformanceMetricSemanticEntry(
                path,
                semantic,
                DescribeCounterSemantic(semantic)));
        }

        private static bool IsIntegralCounterType(Type type) =>
            type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong);

        private static bool IsCounterPropertyName(string name) =>
            name.Contains("Count", StringComparison.Ordinal) ||
                name.Contains("Counts", StringComparison.Ordinal) ||
                name.Contains("Capacity", StringComparison.Ordinal) ||
                name.Contains("Budget", StringComparison.Ordinal) ||
                name.Contains("Invocations", StringComparison.Ordinal) ||
                name.Contains("Bytes", StringComparison.Ordinal) ||
                name.Contains("Lane", StringComparison.Ordinal) ||
                name.Contains("ScheduledRay", StringComparison.Ordinal) ||
                name.Contains("RaysPerFrame", StringComparison.Ordinal) ||
                name.Contains("ProbesUpdated", StringComparison.Ordinal);

        private static bool TryGetCollectionElementType(Type type, out Type elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            Type? enumerableType = type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                    ? type
                    : type.GetInterfaces().FirstOrDefault(static candidate =>
                        candidate.IsGenericType &&
                        candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableType != null)
            {
                elementType = enumerableType.GetGenericArguments()[0];
                return true;
            }

            elementType = typeof(void);
            return false;
        }

        private static bool ShouldTraverseCounterContract(Type type) =>
            type != typeof(string) &&
            !type.IsPrimitive &&
            !type.IsEnum &&
            type.Namespace?.StartsWith("Njulf.Rendering", StringComparison.Ordinal) == true;

        private static PerformanceMetricSemantic ResolveCounterSemantic(
            string path,
            string name)
        {
            if (string.Equals(
                    name,
                    nameof(RendererDiagnostics.ForwardShadowReceiverMeshletCapacity),
                    StringComparison.Ordinal) ||
                path.Contains(".CapacityDetails.", StringComparison.Ordinal) &&
                (name.Contains("Bytes", StringComparison.Ordinal) ||
                 name.Contains("Capacity", StringComparison.Ordinal)) ||
                name.Contains("Capacity", StringComparison.Ordinal) ||
                name.EndsWith("BufferBytes", StringComparison.Ordinal) ||
                name.EndsWith("AtlasBytes", StringComparison.Ordinal) ||
                name.EndsWith("ScratchBytes", StringComparison.Ordinal) ||
                name.EndsWith("RetainedBytes", StringComparison.Ordinal) ||
                name.EndsWith("MirrorBytes", StringComparison.Ordinal))
            {
                return PerformanceMetricSemantic.Capacity;
            }

            if (name.Contains("Estimated", StringComparison.Ordinal) ||
                name.Contains("Estimate", StringComparison.Ordinal) ||
                name.Contains("Sampled", StringComparison.Ordinal) ||
                path.Contains(".DecalFragmentAttribution.", StringComparison.Ordinal) ||
                path.Contains(".SimpleDdgiGatherMultiplicity.", StringComparison.Ordinal) ||
                path.Contains(".DirectionalShadowReceiverCounters.", StringComparison.Ordinal) ||
                name.StartsWith("SimpleDdgiGather", StringComparison.Ordinal) ||
                name.StartsWith("SimpleDdgiSecondVolumeGather", StringComparison.Ordinal) ||
                name.StartsWith("SimpleDdgiSkyVisibility", StringComparison.Ordinal))
            {
                return PerformanceMetricSemantic.SampledEstimate;
            }

            if (name.EndsWith("Budget", StringComparison.Ordinal) ||
                name.EndsWith("BudgetBytes", StringComparison.Ordinal) ||
                name.Contains("Configured", StringComparison.Ordinal))
            {
                return PerformanceMetricSemantic.ConfiguredBudget;
            }

            if (name.Contains("Emitted", StringComparison.Ordinal) ||
                name.Contains("Scheduled", StringComparison.Ordinal) ||
                name.Contains("Dispatch", StringComparison.Ordinal) ||
                name.Contains("Trace", StringComparison.Ordinal) ||
                name.Contains("ProbesUpdated", StringComparison.Ordinal) ||
                name.Contains("RaysPerFrame", StringComparison.Ordinal))
            {
                return PerformanceMetricSemantic.EmittedWork;
            }

            return PerformanceMetricSemantic.Exact;
        }

        private static string DescribeCounterSemantic(
            PerformanceMetricSemantic semantic) => semantic switch
            {
                PerformanceMetricSemantic.SampledEstimate =>
                    "Sparse or weighted estimate; inspect Measurement for stride and weight.",
                PerformanceMetricSemantic.Capacity =>
                    "Provisioned or addressable capacity; not emitted work.",
                PerformanceMetricSemantic.ConfiguredBudget =>
                    "Configured limit or admission budget; not emitted work.",
                PerformanceMetricSemantic.EmittedWork =>
                    "Exact work emitted or scheduled by the named producer.",
                _ => "Exact counter in the stated capture/readback domain."
            };

        private static string NormalizeRunMetadataValue(string? value, string unavailableValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return unavailableValue;

            string normalized = value.Trim();
            return normalized.StartsWith("unknown", StringComparison.OrdinalIgnoreCase)
                ? unavailableValue
                : normalized;
        }

        private static string CreateDebugState(RendererDiagnostics diagnostics)
        {
            return $"gi={diagnostics.GlobalIlluminationDebugView}; " +
                   $"featureIsolation={diagnostics.ActiveFeatureIsolation}; " +
                   $"validation={diagnostics.ValidationMode}; " +
                   $"gpuTiming={(diagnostics.GpuTimingValid != 0 ? "available" : "unavailable")}";
        }

        private static IReadOnlyList<string> CreateFeatureFlags(RendererDiagnostics diagnostics)
        {
            var flags = new List<string>(13);
            if (IsFeatureActive(diagnostics, "emergency-gi-fallback"))
                flags.Add("emergency-gi-fallback");
            if (diagnostics.GlobalIlluminationDdgiActive != 0)
                flags.Add("ddgi");
            if (diagnostics.SimpleDdgiActive != 0)
                flags.Add("simple-ddgi");
            if (diagnostics.SimpleDdgiTransportV2Active != 0)
                flags.Add("ddgi-transport-v2");
            if (diagnostics.GlobalIlluminationRayQueryActive != 0)
                flags.Add("ray-query");
            if (diagnostics.SimpleDdgiStructuredGatherEnabled != 0)
                flags.Add("structured-gather");
            flags.Add(
                $"receiver-cache-requested-{diagnostics.SimpleDdgiReceiverCache.RequestedMode}");
            flags.Add(
                $"receiver-cache-effective-{diagnostics.SimpleDdgiReceiverCache.EffectiveMode}");
            if (diagnostics.SimpleDdgiReducedBlendEnabled != 0)
                flags.Add("reduced-blend");
            if (IsFeatureActive(diagnostics, "sampled-simple-ddgi-atlas"))
                flags.Add("sampled-atlas");
            if (diagnostics.SimpleDdgiToroidalScrollingEnabled != 0)
                flags.Add("toroidal-scrolling");
            if (diagnostics.SimpleDdgiRegionalInvalidationEnabled != 0)
                flags.Add("regional-invalidation");
            if (IsFeatureActive(diagnostics, "paged-far-field"))
                flags.Add("paged-far-field");
            if (IsFeatureActive(diagnostics, "gi-acceleration-structure-streaming"))
                flags.Add("streamed-gi-acceleration-structures");
            if (IsFeatureActive(diagnostics, "detailed-gi-counters"))
                flags.Add("detailed-gi-counters");
            if (IsFeatureActive(diagnostics, "async-compute"))
                flags.Add("async-compute");
            if (flags.Count == 0)
                flags.Add("none");
            return flags;
        }

        private static string CreateGiTimingCoverage(RendererDiagnostics diagnostics)
        {
            if (diagnostics.GlobalIlluminationEnabled == 0)
                return "inactive";

            var scopes = new List<string>(5)
            {
                diagnostics.GpuTimingValid != 0 ? "update=available" : "update=unavailable",
                "forward-gather-inclusive=" + ResolveForwardInclusiveCoverage(diagnostics),
                "forward-gather-incremental=" + ResolveForwardIncrementalCoverage(diagnostics),
                diagnostics.GpuFarFieldUpdateTimingValid != 0
                    ? "far-field-update=available"
                    : "far-field-update=unavailable",
                "acceleration-structures=separate-blas-tlas-scopes"
            };
            return string.Join("; ", scopes);
        }

        private static bool IsFeatureActive(RendererDiagnostics diagnostics, string name)
        {
            IReadOnlyList<GiFeatureState> states = diagnostics.GiFeatureStates.Count > 0
                ? diagnostics.GiFeatureStates
                : GiFeatureStateFactory.Create(diagnostics);
            foreach (GiFeatureState state in states)
            {
                if (string.Equals(state.Name, name, StringComparison.Ordinal))
                    return state.Active;
            }
            return false;
        }

        private static bool HasForwardGiIncrementalTiming(RendererDiagnostics diagnostics) =>
            diagnostics.GpuForwardGiIncrementalAttribution is GiTimingAttribution.Exclusive or GiTimingAttribution.PairedEstimate;

        private static string ResolveForwardInclusiveCoverage(RendererDiagnostics diagnostics)
        {
            GiTimingAttribution attribution = ResolveForwardInclusiveAttribution(diagnostics);
            return attribution == GiTimingAttribution.Unavailable ? "unavailable" : attribution.ToString();
        }

        private static string ResolveForwardIncrementalCoverage(RendererDiagnostics diagnostics) =>
            HasForwardGiIncrementalTiming(diagnostics)
                ? diagnostics.GpuForwardGiIncrementalAttribution.ToString()
                : "unavailable";

        private static GiTimingAttribution ResolveForwardInclusiveAttribution(RendererDiagnostics diagnostics)
        {
            if (diagnostics.GpuForwardGiGatherTimingAttribution != GiTimingAttribution.Unavailable)
                return diagnostics.GpuForwardGiGatherTimingAttribution;
            // Schema-v2 diagnostics only had a coverage bit; it was explicitly documented as
            // inclusive, so preserve that meaning during write/migration compatibility.
            return diagnostics.GpuForwardGiGatherTimingCoverage != 0
                ? GiTimingAttribution.Inclusive
                : GiTimingAttribution.Unavailable;
        }

        internal static GiTimingAttributionSnapshot CreateGiTimingAttributionSnapshot(RendererDiagnostics diagnostics)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            return new GiTimingAttributionSnapshot(
                diagnostics.GpuForwardOpaqueMicroseconds,
                diagnostics.GpuForwardGiGatherMicroseconds,
                ResolveForwardInclusiveAttribution(diagnostics),
                diagnostics.GpuForwardGiIncrementalMicroseconds,
                diagnostics.GpuForwardGiIncrementalAttribution,
                diagnostics.GpuForwardGiIncrementalTimingReason);
        }

        private static PerformanceFoliageSnapshot CreateFoliageSnapshot(RendererDiagnostics diagnostics)
        {
            ulong bufferBytes = diagnostics.FoliageInstanceBufferBytes +
                diagnostics.FoliageClusterBufferBytes +
                diagnostics.FoliageDrawBufferBytes +
                diagnostics.FoliageImpostorAtlasBytes +
                diagnostics.DdgiFoliageProxyVertexBufferBytes +
                diagnostics.DdgiFoliageProxyIndexBufferBytes +
                diagnostics.DdgiFoliageProxyPatchBufferBytes;

            return new PerformanceFoliageSnapshot(
                diagnostics.FoliagePatchCount,
                diagnostics.FoliagePrototypeCount,
                diagnostics.FoliageClusterCount,
                diagnostics.FoliageVisibleClusterCount,
                diagnostics.FoliageVisibleMeshletDrawCount,
                diagnostics.FoliageDdgiSampleCount,
                diagnostics.FoliageDdgiTransportExcludedClusterCount,
                diagnostics.FoliageDdgiTransportExclusionReason,
                diagnostics.FoliageGrassBladeEstimate,
                diagnostics.FoliageFarImpostorVisibleCount,
                diagnostics.FoliageOverflowCount,
                bufferBytes,
                diagnostics.CpuFoliageBuildMicroseconds,
                diagnostics.CpuFoliageUploadMicroseconds,
                diagnostics.GpuFoliageCullMicroseconds,
                diagnostics.GpuFoliageDepthMicroseconds,
                diagnostics.GpuFoliageForwardMicroseconds,
                diagnostics.GpuFoliageShadowMicroseconds,
                IdentifyFoliageBottleneck(diagnostics, bufferBytes))
            {
                DdgiGeometryMode = diagnostics.DdgiFoliageGeometryMode,
                DdgiProxyCardCount = diagnostics.DdgiFoliageProxyCardCount,
                DdgiProxyTriangleCount =
                    diagnostics.DdgiFoliageProxyTriangleCount,
                DdgiAuthoredInstanceCount =
                    diagnostics.DdgiFoliageAuthoredInstanceCount,
                DdgiGeneratedInstanceCount =
                    diagnostics.DdgiFoliageGeneratedInstanceCount,
                DdgiDroppedTriangleCount =
                    diagnostics.DdgiFoliageDroppedTriangleCount,
                DdgiRepresentedBladeCount =
                    diagnostics.DdgiFoliageRepresentedBladeCount,
                DdgiProxyUpdatedThisFrame =
                    diagnostics.DdgiFoliageProxyUpdatedThisFrame,
                DdgiProxyUploadBytes = diagnostics.DdgiFoliageProxyUploadBytes,
                DdgiProxyBufferBytes =
                    diagnostics.DdgiFoliageProxyVertexBufferBytes +
                    diagnostics.DdgiFoliageProxyIndexBufferBytes +
                    diagnostics.DdgiFoliageProxyPatchBufferBytes,
                DdgiProxyPatchBufferBytes =
                    diagnostics.DdgiFoliageProxyPatchBufferBytes,
                DdgiProxyContentSignature =
                    diagnostics.DdgiFoliageProxyContentSignature,
                DdgiProxyCadenceGeneration =
                    diagnostics.DdgiFoliageProxyCadenceGeneration,
                CpuDdgiProxyBuildMicroseconds =
                    diagnostics.CpuDdgiFoliageProxyBuildMicroseconds,
                CpuDdgiProxyUploadMicroseconds =
                    diagnostics.CpuDdgiFoliageProxyUploadMicroseconds,
                CpuDdgiProxyGenerationRecordMicroseconds =
                    diagnostics.CpuDdgiFoliageProxyGenerationRecordMicroseconds,
                GpuDdgiProxyGenerationMicroseconds =
                    diagnostics.GpuDdgiFoliageProxyGenerationMicroseconds,
                DdgiProxyRequestedRepresentedInstanceCount =
                    diagnostics.DdgiFoliageProxyRequestedRepresentedInstanceCount,
                DdgiProxyDensityError =
                    diagnostics.DdgiFoliageProxyDensityError,
                DdgiProxyWindAgeSeconds =
                    diagnostics.DdgiFoliageProxyWindAgeSeconds,
                DdgiProxyNearCardCount =
                    diagnostics.DdgiFoliageProxyNearCardCount,
                DdgiProxyMidCardCount =
                    diagnostics.DdgiFoliageProxyMidCardCount,
                DdgiProxyFarCardCount =
                    diagnostics.DdgiFoliageProxyFarCardCount,
                DdgiProxyExcludedPatchCount =
                    diagnostics.DdgiFoliageProxyExcludedPatchCount,
                DdgiProxyLodPolicyVersion =
                    diagnostics.DdgiFoliageProxyLodPolicyVersion,
                DdgiProxyFallbackReason =
                    diagnostics.DdgiFoliageProxyFallbackReason
            };
        }

        private static string IdentifyFoliageBottleneck(RendererDiagnostics diagnostics, ulong bufferBytes)
        {
            if (diagnostics.FoliagePatchCount == 0 &&
                diagnostics.FoliageClusterCount == 0 &&
                bufferBytes == 0)
            {
                return "none";
            }

            if (diagnostics.FoliageOverflowCount > 0 || diagnostics.FoliageMeshletDrawOverflowCount > 0)
                return "capacity";

            long max = diagnostics.CpuFoliageBuildMicroseconds;
            string label = "cpu-build";
            UpdateMax(diagnostics.CpuFoliageUploadMicroseconds, "cpu-upload", ref max, ref label);
            UpdateMax(diagnostics.GpuFoliageCullMicroseconds, "gpu-cull", ref max, ref label);
            UpdateMax(diagnostics.GpuFoliageDepthMicroseconds, "depth-alpha-overdraw", ref max, ref label);
            UpdateMax(diagnostics.GpuFoliageForwardMicroseconds, "fragment-alpha-overdraw-or-forward-shading", ref max, ref label);
            UpdateMax(diagnostics.GpuFoliageShadowMicroseconds, "shadows", ref max, ref label);

            if (max > 0)
                return label;
            return bufferBytes > 0 ? "memory" : "no-timing";
        }

        internal static PerformanceGlobalIlluminationSnapshot CreateGlobalIlluminationSnapshot(RendererDiagnostics diagnostics)
        {
            SimpleDdgiNearFieldResidualDiagnostics nearFieldResidual =
                (diagnostics.SimpleDdgiNearFieldResidual ??
                 SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                     "C5 telemetry was not supplied by renderer diagnostics."))
                .NormalizeForPersistence();
            long cpuRecordMicroseconds = diagnostics.GlobalIlluminationCpuTimingSampleCount > 0
                ? diagnostics.CpuGlobalIlluminationRecordMicroseconds
                : diagnostics.CpuDdgiRecordMicroseconds +
                  diagnostics.CpuSimpleDdgiRecordMicroseconds +
                  diagnostics.CpuFarFieldRecordMicroseconds +
                  diagnostics.CpuAccelerationStructureBuildMicroseconds;
            long gpuMicroseconds = diagnostics.GpuDdgiUpdateMicroseconds +
                diagnostics.GpuGiCompositeMicroseconds +
                diagnostics.GpuFarFieldUpdateMicroseconds +
                diagnostics.GpuAccelerationStructureBlasMicroseconds +
                diagnostics.GpuAccelerationStructureTlasMicroseconds +
                (HasForwardGiIncrementalTiming(diagnostics)
                    ? diagnostics.GpuForwardGiIncrementalMicroseconds
                    : 0);
            ulong memoryBytes = diagnostics.DdgiTextureBytes +
                diagnostics.DdgiBufferBytes +
                diagnostics.AccelerationStructureBytes;

            return new PerformanceGlobalIlluminationSnapshot(
                diagnostics.GlobalIlluminationEnabled != 0,
                diagnostics.ActiveQualityPreset,
                diagnostics.GlobalIlluminationMode,
                diagnostics.GlobalIlluminationDebugView,
                diagnostics.GlobalIlluminationRayQuerySupported != 0,
                diagnostics.GlobalIlluminationRayQueryActive != 0,
                diagnostics.GlobalIlluminationDdgiActive != 0,
                diagnostics.SimpleDdgiActive != 0,
                diagnostics.SimpleDdgiProbeCount,
                diagnostics.SimpleDdgiProbesUpdated,
                diagnostics.SimpleDdgiRaysPerFrame,
                diagnostics.SimpleDdgiTransportV2Active != 0,
                diagnostics.SimpleDdgiAutomaticProbeDensityActive != 0,
                diagnostics.SimpleDdgiTransportSourceRefreshProbeCount,
                diagnostics.SimpleDdgiTransportSourceRefreshTargetProbeCount,
                diagnostics.SimpleDdgiTransportSourceRefreshCapacityShortfall,
                diagnostics.SimpleDdgiTransportSourceCohortTransitionActive != 0,
                diagnostics.SimpleDdgiTransportSourceCohortTransitionCount,
                diagnostics.SimpleDdgiTransportSourceCohortElapsedFrames,
                diagnostics.SimpleDdgiTransportSourceStepStaleProbeCount,
                diagnostics.SimpleDdgiTransportSourceStepAgeP95Frames,
                diagnostics.SimpleDdgiTransportSourceStepAgeMaximumFrames,
                diagnostics.SimpleDdgiTransportSourceStepAgeP95Seconds,
                diagnostics.SimpleDdgiTransportSourceStepAgeMaximumSeconds,
                diagnostics.SimpleDdgiTransportSourceCacheReuseProbeCount,
                diagnostics.SimpleDdgiTransportSourceRayCount,
                diagnostics.SimpleDdgiTransportSolveRayCount,
                diagnostics.SimpleDdgiTransportPublishedProbeCount,
                diagnostics.SimpleDdgiTransportPublishRegionCount,
                diagnostics.SimpleDdgiTransportPublishedProbeTotal,
                diagnostics.SimpleDdgiTransportPublishRegionTotal,
                diagnostics.SimpleDdgiUpdateTransactionAbortCount,
                diagnostics.SimpleDdgiTransportSourceCacheInvalidationCount,
                diagnostics.SimpleDdgiTransportSolverInvalidationCount,
                diagnostics.SimpleDdgiTransportSolverInvalidationsPerSourceRefresh,
                diagnostics.SimpleDdgiSourceLightingGeneration,
                diagnostics.SimpleDdgiTransportGeneration,
                diagnostics.SimpleDdgiTransportSourceReadyProbeCount,
                diagnostics.SimpleDdgiTransportSourceStaleProbeCount,
                diagnostics.SimpleDdgiTransportConvergedProbeCount,
                diagnostics.SimpleDdgiTransportPendingSolverProbeCount,
                diagnostics.SimpleDdgiTransportGlobalConvergencePending != 0,
                diagnostics.SimpleDdgiTransportGlobalConvergenceElapsedFrames,
                diagnostics.SimpleDdgiTransportCalibrationChangeCount,
                diagnostics.SimpleDdgiTransportSolverRelaxation,
                diagnostics.SimpleDdgiTransportAlbedoClamp,
                diagnostics.SimpleDdgiTransportResidualThreshold,
                diagnostics.SimpleDdgiTransportMaximumSolverGenerations,
                diagnostics.SimpleDdgiTransportSourceRefreshFrames,
                diagnostics.SimpleDdgiTransportConfiguredSourceRefreshFrames,
                diagnostics.SimpleDdgiTransportIrradianceAtlasBytes,
                diagnostics.SimpleDdgiTransportSourceCacheBytes,
                diagnostics.SimpleDdgiInactiveProbeCount,
                diagnostics.SimpleDdgiInactiveProbeSkipCount,
                diagnostics.SimpleDdgiSavedRaysPerFrame,
                diagnostics.SimpleDdgiLightingDirtyFrames,
                diagnostics.SimpleDdgiLightingDirtyBoostedCapacity,
                diagnostics.SimpleDdgiDirtyReasonFlags,
                diagnostics.SimpleDdgiFullRayProbeUpdateCount,
                diagnostics.SimpleDdgiMaintenanceRayProbeUpdateCount,
                diagnostics.SimpleDdgiAdaptiveRaySavedRaysPerFrame,
                diagnostics.SimpleDdgiAtlasBytes,
                diagnostics.SimpleDdgiSampledAtlasRequested != 0,
                diagnostics.SimpleDdgiSampledAtlasActive != 0,
                diagnostics.SimpleDdgiSampledAtlasGroupCount,
                diagnostics.SimpleDdgiSampledAtlasLayersPerTexture,
                diagnostics.SimpleDdgiSampledAtlasImageBytes,
                diagnostics.SimpleDdgiSampledAtlasFallbackReason,
                diagnostics.GpuSimpleDdgiTraceMicroseconds,
                diagnostics.GpuSimpleDdgiTransportMicroseconds,
                diagnostics.GpuSimpleDdgiDirectionalRadianceMicroseconds,
                diagnostics.GpuSimpleDdgiBlendMicroseconds,
                diagnostics.SimpleDdgiTransportEnergySampleCount,
                diagnostics.SimpleDdgiTransportSourceCacheHitCount,
                diagnostics.SimpleDdgiTransportSourceCacheMissCount,
                diagnostics.SimpleDdgiTransportBounceLuminanceAverage,
                diagnostics.SimpleDdgiTransportSourceLuminanceAverage,
                diagnostics.SimpleDdgiTransportTotalLuminanceAverage,
                diagnostics.DdgiReceiverDiffuseReflectanceLuminance,
                diagnostics.DdgiReceiverDiffuseReflectanceSampleCount,
                diagnostics.DdgiTraceOneSidedBackFaceAlbedoLuminance,
                diagnostics.DdgiTraceOneSidedBackFaceHitCount,
                diagnostics.DdgiTraceOpaqueAlbedoLuminance,
                diagnostics.DdgiTraceOpaqueHitCount,
                diagnostics.DdgiTraceThinSurfaceAlbedoLuminance,
                diagnostics.DdgiTraceThinSurfaceHitCount,
                diagnostics.DdgiTraceUnsupportedTransmissionAlbedoLuminance,
                diagnostics.DdgiTraceUnsupportedTransmissionHitCount,
                diagnostics.DdgiTraceReflectDisabledAlbedoLuminance,
                diagnostics.DdgiTraceReflectDisabledHitCount,
                diagnostics.DdgiProbeVolumeCount,
                diagnostics.DdgiProbeCount,
                diagnostics.DdgiActiveProbeCount,
                diagnostics.DdgiProbesUpdated,
                diagnostics.DdgiProbeUpdatePrimaryRayBudget,
                diagnostics.DdgiAverageSpatialCoverageEstimate,
                diagnostics.DdgiAverageSupportCoverageEstimate,
                diagnostics.DdgiAverageDataConfidenceEstimate,
                diagnostics.DdgiAverageVisibilityConfidenceEstimate,
                diagnostics.DdgiAverageLeakAttenuationEstimate,
                diagnostics.DdgiAverageEffectiveContributionEstimate,
                diagnostics.DdgiAverageOwnershipConsumedEstimate,
                diagnostics.DdgiAverageRelocationFractionEstimate,
                diagnostics.DdgiRelocatedProbeFractionEstimate,
                diagnostics.DdgiAverageRelocationDisplacementFractionEstimate,
                diagnostics.DdgiClassifiedInactiveProbeCountEstimate,
                diagnostics.DdgiQualityTier,
                diagnostics.DdgiAdaptiveBudgetScale,
                diagnostics.DdgiAdaptiveBudgetReduced,
                diagnostics.DdgiEmergencyDegradeActive,
                diagnostics.DdgiEffectiveMaxShadedLights,
                diagnostics.DdgiAdaptiveBudgetReason,
                diagnostics.DdgiScheduledPrimaryRayCount,
                diagnostics.DdgiEstimatedShadowRayUpperBound,
                diagnostics.DdgiSelectedDirectionalHitCount,
                diagnostics.DdgiSelectedLocalHitCount,
                diagnostics.DdgiVisibilityRayCount,
                diagnostics.DdgiSkippedLocalLightCount,
                diagnostics.DdgiLightSelectionMode,
                diagnostics.DdgiEmissiveSourceCount,
                diagnostics.DdgiEmissiveSourceRevision,
                diagnostics.ParticleDdgiSampleCount,
                diagnostics.VfxDdgiDirtyProbeEventCount,
                diagnostics.DdgiNewProbeCount,
                diagnostics.DdgiDirtyBoundsProbeUpdateCount,
                diagnostics.SimpleDdgiMutationJournalLastConsumedSerial,
                diagnostics.SimpleDdgiMutationJournalEnqueuedEventCount,
                diagnostics.SimpleDdgiMutationJournalCoalescedEventCount,
                diagnostics.SimpleDdgiMutationJournalOverflowCount,
                diagnostics.SimpleDdgiMutationJournalConservativeFallbackCount,
                diagnostics.SimpleDdgiMutationJournalAttachScanCount,
                diagnostics.SimpleDdgiMutationJournalAttachObjectCount,
                diagnostics.SimpleDdgiMutationJournalOracleComparisonCount,
                diagnostics.SimpleDdgiMutationJournalOracleMismatchCount,
                diagnostics.SimpleDdgiMutationJournalPendingEventCount,
                diagnostics.SimpleDdgiMutationJournalOutputRegionCount,
                diagnostics.SimpleDdgiMutationJournalOverflowedThisFrame,
                diagnostics.DdgiVisibleFrustumProbeUpdateCount,
                diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount,
                diagnostics.DdgiAgeRefreshProbeUpdateCount,
                diagnostics.DdgiHighVarianceProbeUpdateCount,
                diagnostics.DdgiLowConfidenceProbeUpdateCount,
                diagnostics.DdgiStableProbeUpdateCount,
                diagnostics.DdgiAverageProbeVariability,
                diagnostics.DdgiAverageProbeConfidence,
                diagnostics.DdgiTextureBytes,
                diagnostics.DdgiBufferBytes,
                diagnostics.DdgiProbeVolumeBufferBytes,
                diagnostics.DdgiProbeStateBufferBytes,
                diagnostics.DdgiProbeUpdateQueueBytes,
                diagnostics.DdgiProbeRelocationClassificationBytes,
                diagnostics.DdgiTraceDispatchGroupCount,
                diagnostics.DdgiTraceProbeCount,
                diagnostics.DdgiTraceRayCount,
                diagnostics.DdgiBlendProbeCount,
                diagnostics.DdgiRelocateClassifyProbeCount,
                diagnostics.DdgiPublishProbeCount,
                diagnostics.DdgiCurrentIrradianceAtlasBytes,
                diagnostics.DdgiCurrentVisibilityAtlasBytes,
                diagnostics.DdgiUpdateExecuted,
                diagnostics.DdgiUpdateSkipReason,
                diagnostics.DdgiRayScratchBytes,
                diagnostics.DdgiUpdatedAtlasBytes,
                diagnostics.DdgiPublishExecuted,
                diagnostics.DdgiPublishSkipReason,
                diagnostics.DdgiPublishedCacheLatencyFrames,
                diagnostics.DdgiCacheGeneration,
                diagnostics.DdgiLastUpdatedFrameSerial,
                diagnostics.DdgiCacheWarmupState,
                diagnostics.DdgiActiveLocalSlotCount,
                diagnostics.DdgiCacheClearReason,
                diagnostics.AccelerationStructureBytes,
                diagnostics.AccelerationStructureScratchBytes,
                diagnostics.AccelerationStructureInstanceBufferBytes,
                diagnostics.AccelerationStructureRayQueryMetadataBytes,
                diagnostics.AccelerationStructureBlasBuildCount,
                diagnostics.AccelerationStructureBlasCompactionQueryCount,
                diagnostics.AccelerationStructureBlasCompactionCount,
                diagnostics.AccelerationStructureBlasCompactionSourceBytes,
                diagnostics.AccelerationStructureBlasCompactionBytesSaved,
                diagnostics.AccelerationStructureBlasCompactedResidentBytesSaved,
                diagnostics.AccelerationStructureBlasCompactionPendingCount,
                diagnostics.AccelerationStructureBlasCompactionQueryOverflowCount,
                diagnostics.AccelerationStructureBlasCompactionQueryReadbackFailureCount,
                diagnostics.AccelerationStructureTlasBuildCount,
                diagnostics.AccelerationStructureTlasUpdateCount,
                diagnostics.AccelerationStructureTlasSkipCount,
                diagnostics.AccelerationStructureInstanceUploadBytes,
                diagnostics.AccelerationStructureRayQueryMetadataUploadBytes,
                cpuRecordMicroseconds,
                diagnostics.CpuGlobalIlluminationRecordP95Microseconds,
                diagnostics.GlobalIlluminationCpuTimingSampleCount,
                diagnostics.CpuAccelerationStructureBuildMicroseconds,
                diagnostics.CpuAccelerationStructureBlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureBlasCompactionMicroseconds,
                diagnostics.CpuAccelerationStructureTlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureInstanceUploadMicroseconds,
                diagnostics.GpuDdgiTraceMicroseconds,
                diagnostics.GpuDdgiBlendMicroseconds,
                diagnostics.GpuDdgiRelocateClassifyMicroseconds,
                diagnostics.GpuDdgiPublishMicroseconds,
                diagnostics.GpuAccelerationStructureBlasMicroseconds,
                diagnostics.GpuAccelerationStructureTlasMicroseconds,
                gpuMicroseconds,
                diagnostics.DdgiVolumes,
                IdentifyGlobalIlluminationBottleneck(diagnostics, memoryBytes, cpuRecordMicroseconds, gpuMicroseconds))
            {
                ForwardGiInclusiveMicroseconds = diagnostics.GpuForwardGiGatherMicroseconds,
                ForwardGiInclusiveAttribution = ResolveForwardInclusiveAttribution(diagnostics),
                ForwardGiIncrementalMicroseconds = diagnostics.GpuForwardGiIncrementalMicroseconds,
                ForwardGiIncrementalAttribution = diagnostics.GpuForwardGiIncrementalAttribution,
                ForwardGiIncrementalReason = diagnostics.GpuForwardGiIncrementalTimingReason,
                SimpleDdgiReceiverCache = diagnostics.SimpleDdgiReceiverCache,
                SimpleDdgiScheduling = diagnostics.SimpleDdgiScheduling,
                SimpleDdgiAdaptiveRayEvidence =
                    diagnostics.SimpleDdgiAdaptiveRayEvidence,
                SimpleDdgiMutationLatency =
                    diagnostics.SimpleDdgiMutationLatency,
                GiPipelineCacheLoaded = diagnostics.GiPipelineCacheLoaded != 0,
                GiPipelineCacheRejected = diagnostics.GiPipelineCacheRejected != 0,
                GiPipelineCacheSaved = diagnostics.GiPipelineCacheSaved != 0,
                GiPipelineCacheLoadedPayloadBytes = diagnostics.GiPipelineCacheLoadedPayloadBytes,
                GiPipelineCacheSavedPayloadBytes = diagnostics.GiPipelineCacheSavedPayloadBytes,
                GiPipelineCreationCount = diagnostics.GiPipelineCreationCount,
                GiPipelineCreationMicroseconds = diagnostics.GiPipelineCreationMicroseconds,
                GiRenderCriticalPipelineCreationCount = diagnostics.GiRenderCriticalPipelineCreationCount,
                GiPipelineCachePath = diagnostics.GiPipelineCachePath,
                GiPipelineCacheStatus = diagnostics.GiPipelineCacheStatus,
                GiLastCreatedPipeline = diagnostics.GiLastCreatedPipeline,
                SimpleDdgiSchedulerMode = diagnostics.SimpleDdgiSchedulerMode,
                SimpleDdgiTraceContentProfile = diagnostics.SimpleDdgiTraceContentProfile,
                SimpleDdgiTraceDistanceProfile = diagnostics.SimpleDdgiTraceDistanceProfile,
                SimpleDdgiTraceSpecialized = diagnostics.SimpleDdgiTraceSpecialized != 0,
                SimpleDdgiTraceWorkgroupSize = diagnostics.SimpleDdgiTraceWorkgroupSize,
                SimpleDdgiCostAwareSchedulingActive =
                    diagnostics.SimpleDdgiCostAwareSchedulingActive != 0,
                SimpleDdgiSchedulerCostSampleCount =
                    diagnostics.SimpleDdgiSchedulerCostSampleCount,
                SimpleDdgiSchedulerVisibilityPerPrimary =
                    diagnostics.SimpleDdgiSchedulerVisibilityPerPrimary,
                SimpleDdgiSchedulerAlphaCandidatesPerPrimary =
                    diagnostics.SimpleDdgiSchedulerAlphaCandidatesPerPrimary,
                SimpleDdgiSchedulerMaterialEvaluationsPerPrimary =
                    diagnostics.SimpleDdgiSchedulerMaterialEvaluationsPerPrimary,
                SimpleDdgiSchedulerFarFieldStepsPerPrimary =
                    diagnostics.SimpleDdgiSchedulerFarFieldStepsPerPrimary,
                SimpleDdgiSparseResidualPropagationActive =
                    diagnostics.SimpleDdgiSparseResidualPropagationActive != 0,
                SimpleDdgiResidualSeededCount =
                    diagnostics.SimpleDdgiResidualSeededCount,
                SimpleDdgiResidualDependentWakeCount =
                    diagnostics.SimpleDdgiResidualDependentWakeCount,
                SimpleDdgiResidualThresholdRejectedCount =
                    diagnostics.SimpleDdgiResidualThresholdRejectedCount,
                SimpleDdgiResidualCompleteSweepFallbackCount =
                    diagnostics.SimpleDdgiResidualCompleteSweepFallbackCount,
                SimpleDdgiUrgentRelightActive =
                    diagnostics.SimpleDdgiUrgentRelightActive != 0,
                SimpleDdgiUrgentRelightAcceptedCount =
                    diagnostics.SimpleDdgiUrgentRelightAcceptedCount,
                SimpleDdgiUrgentRelightCommittedCount =
                    diagnostics.SimpleDdgiUrgentRelightCommittedCount,
                SimpleDdgiUrgentRelightRejectedCount =
                    diagnostics.SimpleDdgiUrgentRelightRejectedCount,
                GpuSimpleDdgiUrgentRelightMicroseconds =
                    diagnostics.GpuSimpleDdgiUrgentRelightMicroseconds,
                SimpleDdgiSchedulerFallbackLatched = diagnostics.SimpleDdgiSchedulerFallbackLatched != 0,
                SimpleDdgiSchedulerFallbackFreshResetPending = diagnostics.SimpleDdgiSchedulerFallbackFreshResetPending != 0,
                SimpleDdgiSchedulerFallbackCount = diagnostics.SimpleDdgiSchedulerFallbackCount,
                SimpleDdgiSchedulerFallbackReason = diagnostics.SimpleDdgiSchedulerFallbackReason,
                SimpleDdgiSchedulerFallbackExportPending =
                    diagnostics.SimpleDdgiSchedulerFallbackExportPending != 0,
                SimpleDdgiSchedulerFallbackExportBytes =
                    diagnostics.SimpleDdgiSchedulerFallbackExportBytes,
                SimpleDdgiSchedulerStateExportSuccessCount =
                    diagnostics.SimpleDdgiSchedulerStateExportSuccessCount,
                SimpleDdgiSchedulerStateExportFailureCount =
                    diagnostics.SimpleDdgiSchedulerStateExportFailureCount,
                SimpleDdgiSchedulerReentryStableFrameCount =
                    diagnostics.SimpleDdgiSchedulerReentryStableFrameCount,
                SimpleDdgiSchedulerReentryCount =
                    diagnostics.SimpleDdgiSchedulerReentryCount,
                SimpleDdgiTailCertificationFallbackReason =
                    diagnostics.SimpleDdgiTransportTailCertificationFallbackReason,
                SimpleDdgiReceiverProbeBytes = diagnostics.SimpleDdgiReceiverProbeBytes,
                SimpleDdgiReceiverProbeCapacity = diagnostics.SimpleDdgiReceiverProbeCapacity,
                SimpleDdgiReceiverInvalidationBytes = diagnostics.SimpleDdgiReceiverInvalidationBytes,
                SimpleDdgiReceiverInvalidationRangeCount = diagnostics.SimpleDdgiReceiverInvalidationRangeCount,
                SimpleDdgiReceiverFullClear = diagnostics.SimpleDdgiReceiverFullClear != 0,
                SimpleDdgiReceiverResourceGeneration = diagnostics.SimpleDdgiReceiverResourceGeneration,
                SimpleDdgiReceiverRecordsPublished = diagnostics.SimpleDdgiReceiverRecordsPublished,
                SimpleDdgiStorage = diagnostics.SimpleDdgiStorage,
                SimpleDdgiWarmStart = diagnostics.SimpleDdgiWarmStart,
                SimpleDdgiRefinement = diagnostics.SimpleDdgiRefinement,
                SimpleDdgiRefinementEmissiveDemand =
                    diagnostics.SimpleDdgiRefinementEmissiveDemand,
                SimpleDdgiNearVisibility =
                    diagnostics.SimpleDdgiNearVisibility,
                SimpleDdgiNearFieldResidual =
                    nearFieldResidual,
                GiRoadmapExperiments = diagnostics.GiRoadmapExperiments,
                SimpleDdgiContentMemory =
                    diagnostics.SimpleDdgiContentMemory
                        .NormalizeForPersistence(),
                Requested = diagnostics.GlobalIlluminationRequested != 0,
                RequestedMode = diagnostics.GlobalIlluminationRequestedMode,
                RequestedDebugView = diagnostics.GlobalIlluminationRequestedDebugView,
                EmergencyGiFallbackActive = diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0,
                FallbackReason = diagnostics.GlobalIlluminationFallbackReason,
                SimpleDdgiLayout = diagnostics.SimpleDdgiLayout,
                SimpleDdgiProbeResidency = diagnostics.SimpleDdgiProbeResidency,
                SimpleDdgiSchedulerPolicy = diagnostics.SimpleDdgiSchedulerPolicy,
                SimpleDdgiLivenessTelemetry = diagnostics.SimpleDdgiLivenessTelemetry,
                SimpleDdgiLivenessWatchdog = diagnostics.SimpleDdgiLivenessWatchdog
            };
        }

        private static string IdentifyGlobalIlluminationBottleneck(
            RendererDiagnostics diagnostics,
            ulong memoryBytes,
            long cpuRecordMicroseconds,
            long gpuMicroseconds)
        {
            if (diagnostics.GlobalIlluminationEnabled == 0)
                return "disabled";
            if (diagnostics.GlobalIlluminationRayQueryActive != 0 && diagnostics.AccelerationStructureBytes > memoryBytes / 2)
                return "acceleration-structure-memory";
            if (diagnostics.DdgiProbeCount > 0 && diagnostics.DdgiProbesUpdated == 0)
                return "probe-update-budget";
            if (gpuMicroseconds > cpuRecordMicroseconds && gpuMicroseconds > 0)
                return "gpu";
            if (cpuRecordMicroseconds > 0)
                return "cpu-record";
            return memoryBytes > 0 ? "memory" : "no-active-passes";
        }

        private static void UpdateMax(long value, string label, ref long max, ref string maxLabel)
        {
            if (value <= max)
                return;

            max = value;
            maxLabel = label;
        }
    }

    /// <summary>
    /// Reads current captures and migrates schema-v2 baselines in-memory. Migration is kept in
    /// the reader rather than mutating historical evidence on disk, so source hashes and
    /// baseline provenance remain intact.
    /// </summary>
    public sealed class PerformanceSnapshotReader
    {
        private static readonly UTF8Encoding StrictUtf8 =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public PerformanceSnapshot Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Snapshot path is required.", nameof(path));
            byte[] json = BoundedFileReader.ReadStable(
                path,
                DurableJsonFileWriter.MaximumPayloadBytes,
                "Performance snapshot");
            return ReadUtf8(json);
        }

        public PerformanceSnapshot ReadJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Snapshot JSON is required.", nameof(json));

            byte[] utf8;
            try
            {
                int byteCount = StrictUtf8.GetByteCount(json);
                if (byteCount <= 0 ||
                    byteCount > DurableJsonFileWriter.MaximumPayloadBytes)
                {
                    throw new InvalidDataException(
                        "Performance snapshot JSON has an invalid bounded length.");
                }
                utf8 = new byte[byteCount];
                StrictUtf8.GetBytes(json, utf8);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "Performance snapshot JSON contains invalid Unicode.",
                    exception);
            }
            return ReadUtf8(utf8);
        }

        private static PerformanceSnapshot ReadUtf8(byte[] json)
        {
            try
            {
                StrictJsonContract.RejectDuplicateProperties(
                    json,
                    PerformanceSnapshotWriter.SerializerOptions.MaxDepth,
                    "Performance snapshot");
                using JsonDocument document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth =
                            PerformanceSnapshotWriter.SerializerOptions.MaxDepth
                    });
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty(
                        "SchemaVersion",
                        out JsonElement schemaElement) ||
                    schemaElement.ValueKind != JsonValueKind.Number ||
                    !schemaElement.TryGetInt32(out int schemaVersion))
                {
                    throw new InvalidDataException(
                        "Performance snapshot does not contain a valid SchemaVersion.");
                }
                if (schemaVersion is not 2 and not 3 and not 4 and not 5 and
                    not 6 and not 7 and not 8 and not 9 and not 10 and
                    not PerformanceSnapshot.CurrentSchemaVersion)
                {
                    throw new NotSupportedException(
                        $"Performance snapshot schema {schemaVersion} is not supported. " +
                        $"Supported schemas are 2, 3, 4, 5, 6, 7, 8, 9, 10, and " +
                        $"{PerformanceSnapshot.CurrentSchemaVersion}.");
                }

                PerformanceSnapshot? deserialized =
                    JsonSerializer.Deserialize<PerformanceSnapshot>(
                        json,
                        PerformanceSnapshotWriter.SerializerOptions);
                if (deserialized == null)
                {
                    throw new InvalidDataException(
                        "Performance snapshot could not be deserialized.");
                }

                return schemaVersion switch
                {
                    PerformanceSnapshot.CurrentSchemaVersion =>
                        NormalizeCurrentSchema(deserialized),
                    10 => MigrateSchemaV10(deserialized),
                    9 => MigrateSchemaV9(deserialized),
                    8 => MigrateSchemaV8(deserialized),
                    7 => MigrateSchemaV7(deserialized),
                    6 => MigrateSchemaV6(deserialized),
                    5 => MigrateSchemaV5(deserialized),
                    4 => MigrateSchemaV4(deserialized),
                    3 => MigrateSchemaV3(deserialized),
                    _ => MigrateSchemaV2(deserialized)
                };
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "Performance snapshot JSON is invalid.",
                    exception);
            }
        }

        private static PerformanceSnapshot MigrateSchemaV2(PerformanceSnapshot legacy)
        {
            RendererDiagnostics diagnostics = legacy.Diagnostics;
            IReadOnlyList<GiFeatureState> featureStates = diagnostics.GiFeatureStates.Count > 0
                ? diagnostics.GiFeatureStates
                : GiFeatureStateFactory.Create(diagnostics);
            ResolvedGiSettingsMetadata resolvedGi = diagnostics.ResolvedGiSettings.StableHash == "unknown"
                ? ResolvedGiSettingsMetadataFactory.Create(diagnostics)
                : diagnostics.ResolvedGiSettings;
            PerformanceCaptureMetadata capture = legacy.Capture with
            {
                Run = legacy.Capture.Run,
                Camera = legacy.Capture.Camera,
                Frame = legacy.Capture.Frame,
                ResolvedGiSettings = resolvedGi,
                Measurement = legacy.Capture.Measurement,
                FeatureStates = featureStates
            };
            GiResidencySnapshot residency = legacy.GiResidency.UniqueMeasurementAvailable
                ? legacy.GiResidency
                : GiResidencyReporter.Create(
                    diagnostics,
                    legacy.Budget.Memory,
                    RenderBudgetProfile.GetDefault(
                        diagnostics.ActiveBudgetProfile));
            GiTimingAttributionSnapshot timing = PerformanceSnapshotWriter.CreateGiTimingAttributionSnapshot(diagnostics);
            PerformanceSnapshot migrated = legacy with
            {
                Capture = capture,
                GiTiming = timing,
                GiResidency = residency,
                StructuredWarnings = diagnostics.GiWarnings
            };
            return WithDisabledAdvancedGiModes(migrated, originalSchemaVersion: 2);
        }

        private static PerformanceSnapshot MigrateSchemaV3(
            PerformanceSnapshot legacy) =>
            WithDisabledAdvancedGiModes(legacy, originalSchemaVersion: 3);

        /// <summary>
        /// Schema v4 predates the C5 observability/readback contract.  It may
        /// contain a C5 experiment mode, but contains no trustworthy resource,
        /// timestamp, counter, or capture-ID evidence, so all C5 telemetry is
        /// explicitly disabled rather than inferred from the mode.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV4(
            PerformanceSnapshot legacy) =>
            WithDisabledNearFieldResidualTelemetry(legacy, originalSchemaVersion: 4);

        /// <summary>
        /// Schema v5 has complete C5 telemetry but predates the native C1
        /// transaction snapshot. Never infer live micromap objects from a mode
        /// or physical extension bit in an older capture.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV5(
            PerformanceSnapshot legacy) =>
            WithDisabledOpacityMicromapTelemetry(legacy, originalSchemaVersion: 5);

        /// <summary>
        /// Schema v6 records native C1 transactions and memory but predates
        /// immutable content/classification/subdivision evidence. Preserve the
        /// trustworthy transaction counters and explicitly mark only that new
        /// content evidence unavailable.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV6(
            PerformanceSnapshot legacy)
        {
            OpacityMicromapGpuRuntimeSnapshot runtime = legacy.Diagnostics
                .GiRoadmapExperiments
                .OpacityMicromapRuntime with
                {
                    Content = OpacityMicromapContentDiagnostics.Unavailable
                };
            GiRoadmapExperimentDiagnostics roadmap = legacy.Diagnostics
                .GiRoadmapExperiments with
                {
                    OpacityMicromapRuntime = runtime
                };
            RendererDiagnostics diagnostics = legacy.Diagnostics with
            {
                GiRoadmapExperiments = roadmap
            };
            PerformanceGlobalIlluminationSnapshot globalIllumination =
                legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap
                };
            return NormalizeCurrentSchema(legacy with
            {
                OriginalSchemaVersion = 6,
                Diagnostics = diagnostics,
                GlobalIllumination = globalIllumination
            });
        }

        /// <summary>
        /// Schema v7 has immutable C1 content evidence but predates the C3
        /// fence-complete observability contract. Never infer sampled rays,
        /// timings, or allocation from a requested/effective mode alone.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV7(
            PerformanceSnapshot legacy)
        {
            GiRoadmapExperimentDiagnostics roadmap = legacy.Diagnostics
                .GiRoadmapExperiments with
                {
                    DirectionalGuidingRuntime =
                        SimpleDdgiDirectionalGuidingDiagnostics.Disabled
                };
            return NormalizeCurrentSchema(legacy with
            {
                OriginalSchemaVersion = 7,
                Diagnostics = legacy.Diagnostics with
                {
                    GiRoadmapExperiments = roadmap
                },
                GlobalIllumination = legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap
                }
            });
        }

        /// <summary>
        /// Schema v8 has authoritative C3 telemetry but predates C4's
        /// fence-validated publication contract.  Never infer a readable
        /// caustic cache from an active mode, allocated byte count, or GPU
        /// resource state in such a capture.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV8(
            PerformanceSnapshot legacy)
        {
            GiRoadmapExperimentDiagnostics roadmap = legacy.Diagnostics
                .GiRoadmapExperiments with
                {
                    CausticRuntime = GiCausticDiagnostics.Disabled
                };
            return NormalizeCurrentSchema(legacy with
            {
                OriginalSchemaVersion = 8,
                Diagnostics = legacy.Diagnostics with
                {
                    GiRoadmapExperiments = roadmap
                },
                GlobalIllumination = legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap
                }
            });
        }

        /// <summary>
        /// Schema v9 has authoritative C4 telemetry but predates B1's
        /// fence-validated publication contract. Never infer emitted records,
        /// compaction counts, timings, or allocation from an exact mode bit.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV9(
            PerformanceSnapshot legacy)
        {
            GiRoadmapExperimentDiagnostics roadmap = legacy.Diagnostics
                .GiRoadmapExperiments with
                {
                    ReceiverFeedbackRuntime =
                        SimpleDdgiReceiverFeedbackDiagnostics.Disabled
                };
            return NormalizeCurrentSchema(legacy with
            {
                OriginalSchemaVersion = 9,
                Diagnostics = legacy.Diagnostics with
                {
                    GiRoadmapExperiments = roadmap
                },
                GlobalIllumination = legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap
                }
            });
        }

        /// <summary>
        /// Schema v10 has complete Advanced-GI telemetry but predates C5's
        /// explicit packed-history and physical scratch-alias counters. The
        /// exact live byte totals remain valid; the new plan-savings fields
        /// stay zero rather than being reconstructed from incomplete layout
        /// metadata in an archived capture.
        /// </summary>
        private static PerformanceSnapshot MigrateSchemaV10(
            PerformanceSnapshot legacy)
        {
            SimpleDdgiNearFieldResidualDiagnostics diagnosticsTelemetry =
                RemoveC5PlanSavings(
                    legacy.Diagnostics.SimpleDdgiNearFieldResidual);
            SimpleDdgiNearFieldResidualDiagnostics snapshotTelemetry =
                RemoveC5PlanSavings(
                    legacy.GlobalIllumination.SimpleDdgiNearFieldResidual);
            return NormalizeCurrentSchema(legacy with
            {
                OriginalSchemaVersion = 10,
                Diagnostics = legacy.Diagnostics with
                {
                    SimpleDdgiNearFieldResidual = diagnosticsTelemetry
                },
                GlobalIllumination = legacy.GlobalIllumination with
                {
                    SimpleDdgiNearFieldResidual = snapshotTelemetry
                }
            });
        }

        private static SimpleDdgiNearFieldResidualDiagnostics
            RemoveC5PlanSavings(
                SimpleDdgiNearFieldResidualDiagnostics? telemetry)
        {
            SimpleDdgiNearFieldResidualDiagnostics safeTelemetry = telemetry ??
                SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                    "C5 telemetry was absent from a schema-v10 snapshot.");
            return safeTelemetry with
            {
                Memory = safeTelemetry.Memory with
                {
                    PackedValidityAndNormalBytes = 0UL,
                    AliasedFilterScratchBytes = 0UL,
                    PhysicalFilterScratchImageCount = 0
                }
            };
        }

        private static PerformanceSnapshot NormalizeCurrentSchema(
            PerformanceSnapshot snapshot)
        {
            SimpleDdgiNearFieldResidualDiagnostics nearFieldResidual =
                (snapshot.Diagnostics.SimpleDdgiNearFieldResidual ??
                 SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                     "C5 telemetry was absent from a schema-v7 snapshot."))
                .NormalizeForPersistence();
            SimpleDdgiContentMemoryPlan contentMemory =
                snapshot.Diagnostics.SimpleDdgiContentMemory
                    .NormalizeForPersistence();
            SimpleDdgiDirectionalGuidingDiagnostics directionalGuiding =
                (snapshot.Diagnostics.GiRoadmapExperiments
                    .DirectionalGuidingRuntime ??
                 SimpleDdgiDirectionalGuidingDiagnostics.Disabled)
                .NormalizeForPersistence();
            GiCausticDiagnostics caustic =
                (snapshot.Diagnostics.GiRoadmapExperiments.CausticRuntime ??
                 GiCausticDiagnostics.Disabled)
                .NormalizeForPersistence();
            SimpleDdgiReceiverFeedbackDiagnostics receiverFeedback =
                (snapshot.Diagnostics.GiRoadmapExperiments
                    .ReceiverFeedbackRuntime ??
                 SimpleDdgiReceiverFeedbackDiagnostics.Disabled)
                .NormalizeForPersistence();
            SimpleDdgiMutationLatencyTelemetry mutationLatency =
                snapshot.Diagnostics.SimpleDdgiMutationLatency
                    .NormalizeForPersistence();
            RendererDiagnostics diagnostics = snapshot.Diagnostics with
            {
                SimpleDdgiNearFieldResidual = nearFieldResidual,
                SimpleDdgiContentMemory = contentMemory,
                SimpleDdgiMutationLatency = mutationLatency,
                GiRoadmapExperiments = snapshot.Diagnostics.GiRoadmapExperiments with
                {
                    OpacityMicromapRuntime = snapshot.Diagnostics
                        .GiRoadmapExperiments
                        .OpacityMicromapRuntime
                        .NormalizeForPersistence(),
                    ReceiverFeedbackRuntime = receiverFeedback,
                    DirectionalGuidingRuntime = directionalGuiding,
                    CausticRuntime = caustic
                }
            };
            PerformanceGlobalIlluminationSnapshot globalIllumination =
                snapshot.GlobalIllumination with
                {
                    SimpleDdgiNearFieldResidual = nearFieldResidual,
                    GiRoadmapExperiments = diagnostics.GiRoadmapExperiments,
                    SimpleDdgiContentMemory = contentMemory,
                    SimpleDdgiMutationLatency = mutationLatency
                };
            return snapshot with
            {
                SchemaVersion = PerformanceSnapshot.CurrentSchemaVersion,
                OriginalSchemaVersion = snapshot.OriginalSchemaVersion == 0
                    ? PerformanceSnapshot.CurrentSchemaVersion
                    : snapshot.OriginalSchemaVersion,
                Diagnostics = diagnostics,
                GlobalIllumination = globalIllumination
            };
        }

        /// <summary>
        /// Schema-v2/v3 snapshots predate the versioned experiment-mode object.
        /// Preserve their legacy admission text for historical inspection, but
        /// never infer a requested or active mode from it.
        /// </summary>
        private static PerformanceSnapshot WithDisabledAdvancedGiModes(
            PerformanceSnapshot legacy,
            int originalSchemaVersion)
        {
            GiRoadmapExperimentDiagnostics roadmap =
                legacy.Diagnostics.GiRoadmapExperiments with
                {
                    Modes = GiRoadmapExperimentModeDiagnostics.Disabled
                };
            RendererDiagnostics diagnostics = legacy.Diagnostics with
            {
                GiRoadmapExperiments = roadmap,
                SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty
            };
            PerformanceGlobalIlluminationSnapshot globalIllumination =
                legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap,
                    SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty
                };
            PerformanceSnapshot migrated = legacy with
            {
                Diagnostics = diagnostics,
                GlobalIllumination = globalIllumination
            };
            return WithDisabledNearFieldResidualTelemetry(
                migrated,
                originalSchemaVersion);
        }

        private static PerformanceSnapshot WithDisabledNearFieldResidualTelemetry(
            PerformanceSnapshot legacy,
            int originalSchemaVersion)
        {
            SimpleDdgiNearFieldResidualDiagnostics nearFieldResidual =
                SimpleDdgiNearFieldResidualDiagnostics.Disabled(
                    "C5 telemetry is unavailable in this legacy performance snapshot.");
            RendererDiagnostics diagnostics = legacy.Diagnostics with
            {
                SimpleDdgiNearFieldResidual = nearFieldResidual
            };
            PerformanceGlobalIlluminationSnapshot globalIllumination =
                legacy.GlobalIllumination with
                {
                    SimpleDdgiNearFieldResidual = nearFieldResidual
                };
            PerformanceSnapshot migrated = legacy with
            {
                Diagnostics = diagnostics,
                GlobalIllumination = globalIllumination
            };
            return WithDisabledOpacityMicromapTelemetry(
                migrated,
                originalSchemaVersion);
        }

        private static PerformanceSnapshot WithDisabledOpacityMicromapTelemetry(
            PerformanceSnapshot legacy,
            int originalSchemaVersion)
        {
            GiRoadmapExperimentDiagnostics roadmap =
                legacy.Diagnostics.GiRoadmapExperiments with
                {
                    OpacityMicromapRuntime =
                        OpacityMicromapGpuRuntimeSnapshot.Disabled,
                    DirectionalGuidingRuntime =
                        SimpleDdgiDirectionalGuidingDiagnostics.Disabled
                };
            RendererDiagnostics diagnostics = legacy.Diagnostics with
            {
                GiRoadmapExperiments = roadmap,
                SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty
            };
            PerformanceGlobalIlluminationSnapshot globalIllumination =
                legacy.GlobalIllumination with
                {
                    GiRoadmapExperiments = roadmap,
                    SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty
                };
            return legacy with
            {
                SchemaVersion = PerformanceSnapshot.CurrentSchemaVersion,
                OriginalSchemaVersion = originalSchemaVersion,
                Diagnostics = diagnostics,
                GlobalIllumination = globalIllumination
            };
        }
    }
}
