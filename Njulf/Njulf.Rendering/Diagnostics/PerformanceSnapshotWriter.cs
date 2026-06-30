using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record PerformanceSnapshot(
        DateTimeOffset CapturedAt,
        RenderBudgetProfile Profile,
        RendererDiagnostics Diagnostics,
        PerformanceFoliageSnapshot Foliage,
        PerformanceGlobalIlluminationSnapshot GlobalIllumination,
        IReadOnlyList<string> Warnings,
        RenderBudgetSnapshot Budget);

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
        float DdgiAverageCoverageEstimate,
        float DdgiAverageVisibleSupportEstimate,
        float DdgiAverageEffectiveContributionEstimate,
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
        uint DdgiGpuSchedulerDuplicateRequestCount,
        uint DdgiGpuSchedulerBudgetRejectedCount,
        uint DdgiGpuSchedulerInvalidProbeCount,
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
        ulong DdgiCurrentIrradianceAtlasBytes,
        ulong DdgiCurrentVisibilityAtlasBytes,
        int DdgiUpdateExecuted,
        string DdgiUpdateSkipReason,
        ulong DdgiRayScratchBytes,
        ulong DdgiUpdatedAtlasBytes,
        int DdgiPublishExecuted,
        string DdgiPublishSkipReason,
        int DdgiPublishedCacheLatencyFrames,
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
            string path = Path.Combine(directory, $"performance-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
            var capturedAt = DateTimeOffset.Now;
            var snapshot = new PerformanceSnapshot(
                capturedAt,
                budget.Profile,
                diagnostics,
                CreateFoliageSnapshot(diagnostics),
                CreateGlobalIlluminationSnapshot(diagnostics),
                CreateWarnings(diagnostics),
                budget);
            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json);
            return path;
        }

        private static IReadOnlyList<string> CreateWarnings(RendererDiagnostics diagnostics)
        {
            var warnings = new List<string>(3);
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
            return warnings;
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
            long cpuRecordMicroseconds = diagnostics.CpuSsgiRecordMicroseconds + diagnostics.CpuDdgiRecordMicroseconds;
            long gpuMicroseconds = diagnostics.GpuSsgiTraceMicroseconds +
                diagnostics.GpuSsgiTemporalMicroseconds +
                diagnostics.GpuSsgiDenoiseMicroseconds +
                diagnostics.GpuDdgiUpdateMicroseconds +
                diagnostics.GpuGiCompositeMicroseconds +
                diagnostics.GpuAccelerationStructureBlasMicroseconds +
                diagnostics.GpuAccelerationStructureTlasMicroseconds;
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
                diagnostics.DdgiAverageCoverageEstimate,
                diagnostics.DdgiAverageVisibleSupportEstimate,
                diagnostics.DdgiAverageEffectiveContributionEstimate,
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
                diagnostics.DdgiGpuSchedulerDuplicateRequestCount,
                diagnostics.DdgiGpuSchedulerBudgetRejectedCount,
                diagnostics.DdgiGpuSchedulerInvalidProbeCount,
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
                diagnostics.DdgiCurrentIrradianceAtlasBytes,
                diagnostics.DdgiCurrentVisibilityAtlasBytes,
                diagnostics.DdgiUpdateExecuted,
                diagnostics.DdgiUpdateSkipReason,
                diagnostics.DdgiRayScratchBytes,
                diagnostics.DdgiUpdatedAtlasBytes,
                diagnostics.DdgiPublishExecuted,
                diagnostics.DdgiPublishSkipReason,
                diagnostics.DdgiPublishedCacheLatencyFrames,
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
