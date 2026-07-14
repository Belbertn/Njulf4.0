using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        /// incompatible way. Version 2 adds explicit capture metadata and GI timing coverage.
        /// </summary>
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;
        public PerformanceCaptureMetadata Capture { get; init; } = PerformanceCaptureMetadata.Unknown;
    }

    public sealed record PerformanceFoliageSnapshot(
        int PatchCount,
        int PrototypeCount,
        int ClusterCount,
        int VisibleClusterCount,
        int VisibleMeshletDrawCount,
        int DdgiSampleCount,
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
        long GpuSimpleDdgiBlendMicroseconds,
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
        string LikelyBottleneck);

    public sealed class PerformanceSnapshotWriter
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true
        };

        public string Write(string directory, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Snapshot directory is required.", nameof(directory));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (budget == null)
                throw new ArgumentNullException(nameof(budget));

            Directory.CreateDirectory(directory);
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
                Capture = CreateCaptureMetadata(diagnostics)
            };
            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json);
            return path;
        }

        private static IReadOnlyList<string> CreateWarnings(
            RendererDiagnostics diagnostics,
            RenderBudgetProfile profile)
        {
            var warnings = new List<string>(4);
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
            if (forwardGiRequired && diagnostics.GpuForwardGiGatherTimingCoverage == 0)
                warnings.Add("Forward GI gather timing is unavailable; total GI GPU cost is not release-gate ready.");
            if (diagnostics.GlobalIlluminationEnabled != 0 &&
                diagnostics.GlobalIlluminationCpuTimingSampleCount > 0 &&
                diagnostics.CpuGlobalIlluminationRecordP95Microseconds >
                    profile.GlobalIlluminationCpuBudgetMilliseconds * 1000.0)
            {
                warnings.Add("GI CPU scheduling and upload P95 exceeds the configured tier budget.");
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
                warnings.Add("GI acceleration-structure residency rejected BLAS allocation under the active budget.");
            if (diagnostics.FarFieldPagedMode != 0 &&
                diagnostics.FarFieldMemoryBudgetBytes > 0UL &&
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
                CreateGiTimingCoverage(diagnostics));
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
            var flags = new List<string>(12);
            if (diagnostics.GlobalIlluminationSsgiActive != 0)
                flags.Add("ssgi");
            if (diagnostics.GlobalIlluminationDdgiActive != 0)
                flags.Add("ddgi");
            if (diagnostics.SimpleDdgiActive != 0)
                flags.Add("simple-ddgi");
            if (diagnostics.GlobalIlluminationRayQueryActive != 0)
                flags.Add("ray-query");
            if (diagnostics.SimpleDdgiStructuredGatherEnabled != 0)
                flags.Add("structured-gather");
            if (diagnostics.SimpleDdgiReducedBlendEnabled != 0)
                flags.Add("reduced-blend");
            if (diagnostics.SimpleDdgiSampledAtlasActive != 0)
                flags.Add("sampled-atlas");
            if (diagnostics.SimpleDdgiToroidalScrollingEnabled != 0)
                flags.Add("toroidal-scrolling");
            if (diagnostics.SimpleDdgiRegionalInvalidationEnabled != 0)
                flags.Add("regional-invalidation");
            if (diagnostics.FarFieldPagedFeatureEnabled != 0)
                flags.Add("paged-far-field");
            if (diagnostics.StreamedGiAccelerationStructuresFeatureEnabled != 0)
                flags.Add("streamed-gi-acceleration-structures");
            if (diagnostics.DdgiDetailedCountersEnabled != 0)
                flags.Add("detailed-gi-counters");
            if (diagnostics.AsyncComputeEnabled != 0)
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
                diagnostics.GpuForwardGiGatherTimingCoverage != 0
                    ? "forward-gather=inclusive-forward-draw"
                    : "forward-gather=unavailable",
                diagnostics.GpuFarFieldUpdateTimingValid != 0
                    ? "far-field-update=available"
                    : "far-field-update=unavailable",
                "acceleration-structures=separate-blas-tlas-scopes"
            };
            return string.Join("; ", scopes);
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
                (diagnostics.GpuForwardGiGatherTimingCoverage != 0
                    ? diagnostics.GpuForwardGiGatherMicroseconds
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
                diagnostics.GpuSimpleDdgiBlendMicroseconds,
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
                IdentifyGlobalIlluminationBottleneck(diagnostics, memoryBytes, cpuRecordMicroseconds, gpuMicroseconds));
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
}
