using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
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
        /// explicit timing attribution, unique residency, and structured GI warnings.
        /// </summary>
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        /// <summary>Persisted source version before migration, useful when opening baselines.</summary>
        public int OriginalSchemaVersion { get; init; } = CurrentSchemaVersion;
        public PerformanceCaptureMetadata Capture { get; init; } = PerformanceCaptureMetadata.Unknown;
        public IReadOnlyList<GiDiagnosticWarning> StructuredWarnings { get; init; } = Array.Empty<GiDiagnosticWarning>();
        public GiTimingAttributionSnapshot GiTiming { get; init; } = GiTimingAttributionSnapshot.Unavailable;
        public GiResidencySnapshot GiResidency { get; init; } = GiResidencySnapshot.Unavailable;
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
        string LikelyBottleneck);

    public sealed record PerformanceGlobalIlluminationSnapshot(
        bool Enabled,
        RenderQualityPreset ActiveQualityPreset,
        GlobalIlluminationMode Mode,
        GlobalIlluminationDebugView DebugView,
        bool RayQuerySupported,
        bool RayQueryActive,
        bool SsgiActive,
        bool DdgiActive,
        bool SimpleDdgiActive,
        int SimpleDdgiProbeCount,
        int SimpleDdgiProbesUpdated,
        ulong SimpleDdgiRaysPerFrame,
        bool SimpleDdgiTransportV2Active,
        bool SimpleDdgiAutomaticProbeDensityActive,
        int SimpleDdgiTransportSourceRefreshProbeCount,
        int SimpleDdgiTransportSourceCacheReuseProbeCount,
        ulong SimpleDdgiTransportSourceRayCount,
        ulong SimpleDdgiTransportSolveRayCount,
        int SimpleDdgiTransportPublishedProbeCount,
        int SimpleDdgiTransportPublishRegionCount,
        ulong SimpleDdgiTransportSourceCacheInvalidationCount,
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
        long GpuSimpleDdgiBlendMicroseconds,
        uint SimpleDdgiTransportEnergySampleCount,
        uint SimpleDdgiTransportSourceCacheHitCount,
        uint SimpleDdgiTransportSourceCacheMissCount,
        float SimpleDdgiTransportBounceLuminanceAverage,
        float SimpleDdgiTransportSourceLuminanceAverage,
        float SimpleDdgiTransportTotalLuminanceAverage,
        uint SsgiWidth,
        uint SsgiHeight,
        float SsgiResolutionScale,
        int SsgiRayCount,
        int DdgiProbeVolumeCount,
        int DdgiProbeCount,
        int DdgiActiveProbeCount,
        int DdgiProbesUpdated,
        int DdgiProbeUpdatePrimaryRayBudget,
        int DdgiGatherTileCount,
        int DdgiGatherTileCountX,
        int DdgiGatherTileCountY,
        int DdgiGatherSelectedLocalTileCount,
        int DdgiGatherSelectedClipmapTileCount,
        int DdgiGatherFallbackTileCount,
        float DdgiGatherSelectedLocalTileFraction,
        float DdgiGatherSelectedClipmapTileFraction,
        float DdgiGatherFallbackTileFraction,
        int DdgiForwardGatherFallbackUsed,
        int DdgiForwardGatherFallbackDisabled,
        int DdgiForwardGatherTileEmpty,
        float DdgiAverageSpatialCoverageEstimate,
        float DdgiAverageSupportCoverageEstimate,
        float DdgiAverageDataConfidenceEstimate,
        float DdgiAverageVisibilityConfidenceEstimate,
        float DdgiAverageLeakAttenuationEstimate,
        float DdgiAverageEffectiveContributionEstimate,
        float DdgiAverageOwnershipConsumedEstimate,
        float DdgiAverageRelocationFractionEstimate,
        int DdgiClassifiedInactiveProbeCountEstimate,
        DdgiSchedulerMode DdgiSchedulerMode,
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
        int DdgiVisibleFrustumProbeUpdateCount,
        int DdgiOutsideFrustumSafetyProbeUpdateCount,
        int DdgiAgeRefreshProbeUpdateCount,
        int DdgiHighVarianceProbeUpdateCount,
        int DdgiLowConfidenceProbeUpdateCount,
        int DdgiStableProbeUpdateCount,
        float DdgiAverageProbeVariability,
        float DdgiAverageProbeConfidence,
        ulong RenderTargetBytes,
        ulong SsgiRenderTargetBytes,
        ulong SceneSurfaceRenderTargetBytes,
        ulong DdgiTextureBytes,
        ulong DdgiBufferBytes,
        ulong DdgiProbeVolumeBufferBytes,
        ulong DdgiProbeStateBufferBytes,
        ulong DdgiProbeUpdateQueueBytes,
        ulong DdgiProbeRelocationClassificationBytes,
        ulong DdgiGpuSchedulerBufferBytes,
        int DdgiGpuSchedulerDirtyRegionCapacity,
        int DdgiGpuSchedulerCandidateCapacity,
        int DdgiGpuSchedulerGroupCountCapacity,
        int DdgiGpuSchedulerPrefixCapacity,
        int DdgiGpuSchedulerDirtyRegionCount,
        int DdgiGpuSchedulerDirtyRegionOverflowCount,
        int DdgiGpuSchedulerResourceReinitializationCount,
        int DdgiGpuSchedulerTotalResourceReinitializationCount,
        ulong DdgiGpuSchedulerUploadBytes,
        int DdgiGpuSchedulerReadbackValid,
        int DdgiGpuSchedulerReadbackLatencyFrames,
        int DdgiGpuSchedulerFallbackActive,
        string DdgiGpuSchedulerFallbackReason,
        int DdgiGpuSchedulerConsideredProbeCount,
        uint DdgiGpuSchedulerRequestCount,
        uint DdgiGpuSchedulerPrimaryRayCount,
        uint DdgiGpuSchedulerCandidateCount,
        uint DdgiGpuSchedulerOverflowCount,
        uint DdgiGpuSchedulerCandidateBufferOverflowCount,
        uint DdgiGpuSchedulerPerBucketOverflowCount,
        uint DdgiGpuSchedulerDuplicateRequestCount,
        uint DdgiGpuSchedulerBudgetRejectedCount,
        uint DdgiGpuSchedulerRequestBudgetRejectedCount,
        uint DdgiGpuSchedulerPrimaryRayBudgetRejectedCount,
        uint DdgiGpuSchedulerInvalidProbeCount,
        int DdgiGpuSchedulerCandidateOutputCapacity,
        int DdgiGpuSchedulerFullScan,
        uint DdgiGpuSchedulerVisibleFrustumCandidateCount,
        uint DdgiGpuSchedulerSafetyShellCandidateCount,
        uint DdgiGpuSchedulerAgeRefreshCandidateCount,
        uint DdgiGpuSchedulerHighVarianceCandidateCount,
        uint DdgiGpuSchedulerLowConfidenceCandidateCount,
        uint DdgiGpuSchedulerStableSkippedCount,
        uint DdgiGpuSchedulerPriority0RequestCount,
        uint DdgiGpuSchedulerPriority1RequestCount,
        uint DdgiGpuSchedulerPriority2RequestCount,
        uint DdgiGpuSchedulerPriority3RequestCount,
        int DdgiGpuSchedulerRequestBudgetSaturated,
        int DdgiGpuSchedulerPrimaryRayBudgetSaturated,
        int DdgiGpuSchedulerValidationValid,
        string DdgiGpuSchedulerValidationStatus,
        int DdgiGpuSchedulerValidationCpuRequestCount,
        uint DdgiGpuSchedulerValidationGpuRequestCount,
        int DdgiGpuSchedulerValidationComparedRequestCount,
        int DdgiGpuSchedulerValidationMismatchCount,
        int DdgiGpuSchedulerValidationSampleLimit,
        string DdgiGpuSchedulerValidationFirstMismatch,
        uint DdgiTraceDispatchGroupCount,
        uint DdgiTraceProbeCount,
        uint DdgiTraceRayCount,
        uint DdgiBlendProbeCount,
        uint DdgiRelocateClassifyProbeCount,
        uint DdgiPublishProbeCount,
        ulong DdgiCurrentIrradianceAtlasBytes,
        ulong DdgiCurrentVisibilityAtlasBytes,
        ulong DdgiGatherTileBufferBytes,
        ulong DdgiLocalSlotReservedPoolBytes,
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
        int DdgiLocalSlotGeneration,
        ulong DdgiLocalSlotInitBytes,
        string DdgiLocalVolumeEvictionReason,
        string DdgiCacheClearReason,
        ulong AccelerationStructureBytes,
        ulong AccelerationStructureScratchBytes,
        ulong AccelerationStructureInstanceBufferBytes,
        ulong AccelerationStructureRayQueryMetadataBytes,
        int AccelerationStructureBlasBuildCount,
        int AccelerationStructureTlasBuildCount,
        int AccelerationStructureTlasUpdateCount,
        int AccelerationStructureTlasSkipCount,
        ulong AccelerationStructureInstanceUploadBytes,
        ulong AccelerationStructureRayQueryMetadataUploadBytes,
        long CpuRecordMicroseconds,
        long CpuRecordP95Microseconds,
        int CpuTimingSampleCount,
        long CpuDdgiSchedulerMicroseconds,
        long CpuDdgiSchedulerP95Microseconds,
        long CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds,
        long CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds,
        long CpuDdgiSchedulerPhaseUninitializedMicroseconds,
        long CpuDdgiSchedulerPhaseFrustumMicroseconds,
        long CpuDdgiSchedulerPhaseSafetyMicroseconds,
        long CpuDdgiSchedulerPhaseRoundRobinMicroseconds,
        int CpuDdgiSchedulerCandidateInsertCount,
        int CpuDdgiSchedulerCandidateMaxShiftCount,
        int DdgiSchedulerTimingSampleCount,
        int DdgiSchedulerP95OverBudget,
        long CpuAccelerationStructureBuildMicroseconds,
        long CpuAccelerationStructureBlasBuildMicroseconds,
        long CpuAccelerationStructureTlasBuildMicroseconds,
        long CpuAccelerationStructureInstanceUploadMicroseconds,
        long GpuDdgiScheduleMicroseconds,
        long GpuDdgiScheduleP95Microseconds,
        int GpuDdgiScheduleOverBudget,
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
        /// <summary>Authored GI intent, retained even when a live fallback suppresses rendering.</summary>
        public bool Requested { get; init; }
        public GlobalIlluminationMode RequestedMode { get; init; } = GlobalIlluminationMode.Disabled;
        public GlobalIlluminationDebugView RequestedDebugView { get; init; } = GlobalIlluminationDebugView.None;
        public bool EmergencyGiFallbackActive { get; init; }
        public string FallbackReason { get; init; } = string.Empty;
        /// <summary>Requested/accepted layout evidence, including rejected source volumes.</summary>
        public SimpleDdgiLayoutTelemetry SimpleDdgiLayout { get; init; } =
            SimpleDdgiLayoutTelemetry.Unavailable("Simple DDGI layout was not captured.");
        /// <summary>Visible-first scheduler class and pressure evidence.</summary>
        public SimpleDdgiSchedulerPolicyTelemetry SimpleDdgiSchedulerPolicy { get; init; } =
            SimpleDdgiSchedulerPolicyTelemetry.Unavailable("Simple DDGI scheduler policy was not captured.");
    }

    public sealed class PerformanceSnapshotWriter
    {
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
            diagnostics = diagnostics with
            {
                CaptureRun = NormalizeCaptureRunMetadata(diagnostics.CaptureRun)
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
                            diagnostics.ActiveBudgetProfile))
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
            if (diagnostics.SimpleDdgiLayout.IsAvailable && diagnostics.SimpleDdgiLayout.WasDegraded)
                warnings.Add("Simple DDGI layout was degraded before allocation: " + diagnostics.SimpleDdgiLayout.Summary);
            if (diagnostics.SimpleDdgiLayout.IsAvailable &&
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
                FeatureStates = featureStates
            };
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
                ShaderBundleHash = NormalizeRunMetadataValue(value.ShaderBundleHash, "unavailable:shader-bundle-hash-not-reported")
            };
        }

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
            if (diagnostics.GlobalIlluminationSsgiActive != 0)
                flags.Add("ssgi");
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
                diagnostics.FoliageImpostorAtlasBytes;

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
                IdentifyFoliageBottleneck(diagnostics, bufferBytes));
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

        private static PerformanceGlobalIlluminationSnapshot CreateGlobalIlluminationSnapshot(RendererDiagnostics diagnostics)
        {
            long cpuRecordMicroseconds = diagnostics.GlobalIlluminationCpuTimingSampleCount > 0
                ? diagnostics.CpuGlobalIlluminationRecordMicroseconds
                : diagnostics.CpuSsgiRecordMicroseconds +
                  diagnostics.CpuDdgiRecordMicroseconds +
                  diagnostics.CpuDdgiSchedulerMicroseconds +
                  diagnostics.CpuSimpleDdgiRecordMicroseconds +
                  diagnostics.CpuFarFieldRecordMicroseconds +
                  diagnostics.CpuAccelerationStructureBuildMicroseconds;
            long gpuMicroseconds = diagnostics.GpuSsgiTraceMicroseconds +
                diagnostics.GpuSsgiTemporalMicroseconds +
                diagnostics.GpuSsgiDenoiseMicroseconds +
                diagnostics.GpuDdgiUpdateMicroseconds +
                diagnostics.GpuGiCompositeMicroseconds +
                diagnostics.GpuFarFieldUpdateMicroseconds +
                diagnostics.GpuAccelerationStructureBlasMicroseconds +
                diagnostics.GpuAccelerationStructureTlasMicroseconds +
                (HasForwardGiIncrementalTiming(diagnostics)
                    ? diagnostics.GpuForwardGiIncrementalMicroseconds
                    : 0);
            ulong memoryBytes = diagnostics.GlobalIlluminationRenderTargetBytes +
                diagnostics.DdgiTextureBytes +
                diagnostics.DdgiBufferBytes +
                diagnostics.AccelerationStructureBytes;

            return new PerformanceGlobalIlluminationSnapshot(
                diagnostics.GlobalIlluminationEnabled != 0,
                diagnostics.ActiveQualityPreset,
                diagnostics.GlobalIlluminationMode,
                diagnostics.GlobalIlluminationDebugView,
                diagnostics.GlobalIlluminationRayQuerySupported != 0,
                diagnostics.GlobalIlluminationRayQueryActive != 0,
                diagnostics.GlobalIlluminationSsgiActive != 0,
                diagnostics.GlobalIlluminationDdgiActive != 0,
                diagnostics.SimpleDdgiActive != 0,
                diagnostics.SimpleDdgiProbeCount,
                diagnostics.SimpleDdgiProbesUpdated,
                diagnostics.SimpleDdgiRaysPerFrame,
                diagnostics.SimpleDdgiTransportV2Active != 0,
                diagnostics.SimpleDdgiAutomaticProbeDensityActive != 0,
                diagnostics.SimpleDdgiTransportSourceRefreshProbeCount,
                diagnostics.SimpleDdgiTransportSourceCacheReuseProbeCount,
                diagnostics.SimpleDdgiTransportSourceRayCount,
                diagnostics.SimpleDdgiTransportSolveRayCount,
                diagnostics.SimpleDdgiTransportPublishedProbeCount,
                diagnostics.SimpleDdgiTransportPublishRegionCount,
                diagnostics.SimpleDdgiTransportSourceCacheInvalidationCount,
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
                diagnostics.GpuSimpleDdgiBlendMicroseconds,
                diagnostics.SimpleDdgiTransportEnergySampleCount,
                diagnostics.SimpleDdgiTransportSourceCacheHitCount,
                diagnostics.SimpleDdgiTransportSourceCacheMissCount,
                diagnostics.SimpleDdgiTransportBounceLuminanceAverage,
                diagnostics.SimpleDdgiTransportSourceLuminanceAverage,
                diagnostics.SimpleDdgiTransportTotalLuminanceAverage,
                diagnostics.SsgiWidth,
                diagnostics.SsgiHeight,
                diagnostics.SsgiResolutionScale,
                diagnostics.SsgiRayCount,
                diagnostics.DdgiProbeVolumeCount,
                diagnostics.DdgiProbeCount,
                diagnostics.DdgiActiveProbeCount,
                diagnostics.DdgiProbesUpdated,
                diagnostics.DdgiProbeUpdatePrimaryRayBudget,
                diagnostics.DdgiGatherTileCount,
                diagnostics.DdgiGatherTileCountX,
                diagnostics.DdgiGatherTileCountY,
                diagnostics.DdgiGatherSelectedLocalTileCount,
                diagnostics.DdgiGatherSelectedClipmapTileCount,
                diagnostics.DdgiGatherFallbackTileCount,
                diagnostics.DdgiGatherSelectedLocalTileFraction,
                diagnostics.DdgiGatherSelectedClipmapTileFraction,
                diagnostics.DdgiGatherFallbackTileFraction,
                diagnostics.DdgiForwardGatherFallbackUsed,
                diagnostics.DdgiForwardGatherFallbackDisabled,
                diagnostics.DdgiForwardGatherTileEmpty,
                diagnostics.DdgiAverageSpatialCoverageEstimate,
                diagnostics.DdgiAverageSupportCoverageEstimate,
                diagnostics.DdgiAverageDataConfidenceEstimate,
                diagnostics.DdgiAverageVisibilityConfidenceEstimate,
                diagnostics.DdgiAverageLeakAttenuationEstimate,
                diagnostics.DdgiAverageEffectiveContributionEstimate,
                diagnostics.DdgiAverageOwnershipConsumedEstimate,
                diagnostics.DdgiAverageRelocationFractionEstimate,
                diagnostics.DdgiClassifiedInactiveProbeCountEstimate,
                diagnostics.DdgiSchedulerMode,
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
                diagnostics.DdgiVisibleFrustumProbeUpdateCount,
                diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount,
                diagnostics.DdgiAgeRefreshProbeUpdateCount,
                diagnostics.DdgiHighVarianceProbeUpdateCount,
                diagnostics.DdgiLowConfidenceProbeUpdateCount,
                diagnostics.DdgiStableProbeUpdateCount,
                diagnostics.DdgiAverageProbeVariability,
                diagnostics.DdgiAverageProbeConfidence,
                diagnostics.GlobalIlluminationRenderTargetBytes,
                diagnostics.SsgiRenderTargetBytes,
                diagnostics.SceneSurfaceRenderTargetBytes,
                diagnostics.DdgiTextureBytes,
                diagnostics.DdgiBufferBytes,
                diagnostics.DdgiProbeVolumeBufferBytes,
                diagnostics.DdgiProbeStateBufferBytes,
                diagnostics.DdgiProbeUpdateQueueBytes,
                diagnostics.DdgiProbeRelocationClassificationBytes,
                diagnostics.DdgiGpuSchedulerBufferBytes,
                diagnostics.DdgiGpuSchedulerDirtyRegionCapacity,
                diagnostics.DdgiGpuSchedulerCandidateCapacity,
                diagnostics.DdgiGpuSchedulerGroupCountCapacity,
                diagnostics.DdgiGpuSchedulerPrefixCapacity,
                diagnostics.DdgiGpuSchedulerDirtyRegionCount,
                diagnostics.DdgiGpuSchedulerDirtyRegionOverflowCount,
                diagnostics.DdgiGpuSchedulerResourceReinitializationCount,
                diagnostics.DdgiGpuSchedulerTotalResourceReinitializationCount,
                diagnostics.DdgiGpuSchedulerUploadBytes,
                diagnostics.DdgiGpuSchedulerReadbackValid,
                diagnostics.DdgiGpuSchedulerReadbackLatencyFrames,
                diagnostics.DdgiGpuSchedulerFallbackActive,
                diagnostics.DdgiGpuSchedulerFallbackReason,
                diagnostics.DdgiGpuSchedulerConsideredProbeCount,
                diagnostics.DdgiGpuSchedulerRequestCount,
                diagnostics.DdgiGpuSchedulerPrimaryRayCount,
                diagnostics.DdgiGpuSchedulerCandidateCount,
                diagnostics.DdgiGpuSchedulerOverflowCount,
                diagnostics.DdgiGpuSchedulerCandidateBufferOverflowCount,
                diagnostics.DdgiGpuSchedulerPerBucketOverflowCount,
                diagnostics.DdgiGpuSchedulerDuplicateRequestCount,
                diagnostics.DdgiGpuSchedulerBudgetRejectedCount,
                diagnostics.DdgiGpuSchedulerRequestBudgetRejectedCount,
                diagnostics.DdgiGpuSchedulerPrimaryRayBudgetRejectedCount,
                diagnostics.DdgiGpuSchedulerInvalidProbeCount,
                diagnostics.DdgiGpuSchedulerCandidateOutputCapacity,
                diagnostics.DdgiGpuSchedulerFullScan,
                diagnostics.DdgiGpuSchedulerVisibleFrustumCandidateCount,
                diagnostics.DdgiGpuSchedulerSafetyShellCandidateCount,
                diagnostics.DdgiGpuSchedulerAgeRefreshCandidateCount,
                diagnostics.DdgiGpuSchedulerHighVarianceCandidateCount,
                diagnostics.DdgiGpuSchedulerLowConfidenceCandidateCount,
                diagnostics.DdgiGpuSchedulerStableSkippedCount,
                diagnostics.DdgiGpuSchedulerPriority0RequestCount,
                diagnostics.DdgiGpuSchedulerPriority1RequestCount,
                diagnostics.DdgiGpuSchedulerPriority2RequestCount,
                diagnostics.DdgiGpuSchedulerPriority3RequestCount,
                diagnostics.DdgiGpuSchedulerRequestBudgetSaturated,
                diagnostics.DdgiGpuSchedulerPrimaryRayBudgetSaturated,
                diagnostics.DdgiGpuSchedulerValidationValid,
                diagnostics.DdgiGpuSchedulerValidationStatus,
                diagnostics.DdgiGpuSchedulerValidationCpuRequestCount,
                diagnostics.DdgiGpuSchedulerValidationGpuRequestCount,
                diagnostics.DdgiGpuSchedulerValidationComparedRequestCount,
                diagnostics.DdgiGpuSchedulerValidationMismatchCount,
                diagnostics.DdgiGpuSchedulerValidationSampleLimit,
                diagnostics.DdgiGpuSchedulerValidationFirstMismatch,
                diagnostics.DdgiTraceDispatchGroupCount,
                diagnostics.DdgiTraceProbeCount,
                diagnostics.DdgiTraceRayCount,
                diagnostics.DdgiBlendProbeCount,
                diagnostics.DdgiRelocateClassifyProbeCount,
                diagnostics.DdgiPublishProbeCount,
                diagnostics.DdgiCurrentIrradianceAtlasBytes,
                diagnostics.DdgiCurrentVisibilityAtlasBytes,
                diagnostics.DdgiGatherTileBufferBytes,
                diagnostics.DdgiLocalSlotReservedPoolBytes,
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
                diagnostics.DdgiLocalSlotGeneration,
                diagnostics.DdgiLocalSlotInitBytes,
                diagnostics.DdgiLocalVolumeEvictionReason,
                diagnostics.DdgiCacheClearReason,
                diagnostics.AccelerationStructureBytes,
                diagnostics.AccelerationStructureScratchBytes,
                diagnostics.AccelerationStructureInstanceBufferBytes,
                diagnostics.AccelerationStructureRayQueryMetadataBytes,
                diagnostics.AccelerationStructureBlasBuildCount,
                diagnostics.AccelerationStructureTlasBuildCount,
                diagnostics.AccelerationStructureTlasUpdateCount,
                diagnostics.AccelerationStructureTlasSkipCount,
                diagnostics.AccelerationStructureInstanceUploadBytes,
                diagnostics.AccelerationStructureRayQueryMetadataUploadBytes,
                cpuRecordMicroseconds,
                diagnostics.CpuGlobalIlluminationRecordP95Microseconds,
                diagnostics.GlobalIlluminationCpuTimingSampleCount,
                diagnostics.CpuDdgiSchedulerMicroseconds,
                diagnostics.CpuDdgiSchedulerP95Microseconds,
                diagnostics.CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds,
                diagnostics.CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds,
                diagnostics.CpuDdgiSchedulerPhaseUninitializedMicroseconds,
                diagnostics.CpuDdgiSchedulerPhaseFrustumMicroseconds,
                diagnostics.CpuDdgiSchedulerPhaseSafetyMicroseconds,
                diagnostics.CpuDdgiSchedulerPhaseRoundRobinMicroseconds,
                diagnostics.CpuDdgiSchedulerCandidateInsertCount,
                diagnostics.CpuDdgiSchedulerCandidateMaxShiftCount,
                diagnostics.DdgiSchedulerTimingSampleCount,
                diagnostics.DdgiSchedulerP95OverBudget,
                diagnostics.CpuAccelerationStructureBuildMicroseconds,
                diagnostics.CpuAccelerationStructureBlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureTlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureInstanceUploadMicroseconds,
                diagnostics.GpuDdgiScheduleMicroseconds,
                diagnostics.GpuDdgiScheduleP95Microseconds,
                diagnostics.GpuDdgiScheduleOverBudget,
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
                SimpleDdgiScheduling = diagnostics.SimpleDdgiScheduling,
                Requested = diagnostics.GlobalIlluminationRequested != 0,
                RequestedMode = diagnostics.GlobalIlluminationRequestedMode,
                RequestedDebugView = diagnostics.GlobalIlluminationRequestedDebugView,
                EmergencyGiFallbackActive = diagnostics.GlobalIlluminationEmergencyFallbackEnabled != 0,
                FallbackReason = diagnostics.GlobalIlluminationFallbackReason,
                SimpleDdgiLayout = diagnostics.SimpleDdgiLayout,
                SimpleDdgiSchedulerPolicy = diagnostics.SimpleDdgiSchedulerPolicy
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
                if (schemaVersion is not 2 and
                    not PerformanceSnapshot.CurrentSchemaVersion)
                {
                    throw new NotSupportedException(
                        $"Performance snapshot schema {schemaVersion} is not supported. " +
                        $"Supported schemas are 2 and {PerformanceSnapshot.CurrentSchemaVersion}.");
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

                return schemaVersion ==
                        PerformanceSnapshot.CurrentSchemaVersion
                    ? deserialized with
                    {
                        SchemaVersion =
                            PerformanceSnapshot.CurrentSchemaVersion,
                        OriginalSchemaVersion =
                            deserialized.OriginalSchemaVersion == 0
                                ? PerformanceSnapshot.CurrentSchemaVersion
                                : deserialized.OriginalSchemaVersion
                    }
                    : MigrateSchemaV2(deserialized);
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
            return legacy with
            {
                SchemaVersion = PerformanceSnapshot.CurrentSchemaVersion,
                OriginalSchemaVersion = 2,
                Capture = capture,
                GiTiming = timing,
                GiResidency = residency,
                StructuredWarnings = diagnostics.GiWarnings
            };
        }
    }
}
