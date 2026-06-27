using System;
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
        RenderBudgetSnapshot Budget);

    public sealed record PerformanceFoliageSnapshot(
        int PatchCount,
        int PrototypeCount,
        int ClusterCount,
        int VisibleClusterCount,
        int VisibleMeshletDrawCount,
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
        DdgiQualityTier DdgiQualityTier,
        float DdgiAdaptiveBudgetScale,
        int DdgiAdaptiveBudgetReduced,
        int DdgiEmergencyDegradeActive,
        int DdgiEffectiveMaxShadedLights,
        string DdgiAdaptiveBudgetReason,
        ulong DdgiScheduledPrimaryRayCount,
        ulong DdgiEstimatedShadowRayUpperBound,
        int DdgiNewProbeCount,
        int DdgiDirtyBoundsProbeUpdateCount,
        int DdgiVisibleFrustumProbeUpdateCount,
        int DdgiOutsideFrustumSafetyProbeUpdateCount,
        int DdgiAgeRefreshProbeUpdateCount,
        ulong RenderTargetBytes,
        ulong SsgiRenderTargetBytes,
        ulong SceneSurfaceRenderTargetBytes,
        ulong DdgiTextureBytes,
        ulong DdgiBufferBytes,
        ulong DdgiCurrentIrradianceAtlasBytes,
        ulong DdgiCurrentVisibilityAtlasBytes,
        ulong DdgiRecursiveIrradianceAtlasBytes,
        ulong DdgiRecursiveVisibilityAtlasBytes,
        ulong DdgiRecursiveProbeStateBytes,
        int DdgiRecursiveCommitProbeCount,
        int DdgiRecursiveCommitCopyCount,
        ulong DdgiRecursiveCommitBytes,
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
        long CpuAccelerationStructureBuildMicroseconds,
        long CpuAccelerationStructureBlasBuildMicroseconds,
        long CpuAccelerationStructureTlasBuildMicroseconds,
        long CpuAccelerationStructureInstanceUploadMicroseconds,
        long GpuDdgiSnapshotMicroseconds,
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
                budget);
            string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(path, json);
            return path;
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
                diagnostics.GpuDdgiSnapshotMicroseconds +
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
                diagnostics.DdgiQualityTier,
                diagnostics.DdgiAdaptiveBudgetScale,
                diagnostics.DdgiAdaptiveBudgetReduced,
                diagnostics.DdgiEmergencyDegradeActive,
                diagnostics.DdgiEffectiveMaxShadedLights,
                diagnostics.DdgiAdaptiveBudgetReason,
                diagnostics.DdgiScheduledPrimaryRayCount,
                diagnostics.DdgiEstimatedShadowRayUpperBound,
                diagnostics.DdgiNewProbeCount,
                diagnostics.DdgiDirtyBoundsProbeUpdateCount,
                diagnostics.DdgiVisibleFrustumProbeUpdateCount,
                diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount,
                diagnostics.DdgiAgeRefreshProbeUpdateCount,
                diagnostics.GlobalIlluminationRenderTargetBytes,
                diagnostics.SsgiRenderTargetBytes,
                diagnostics.SceneSurfaceRenderTargetBytes,
                diagnostics.DdgiTextureBytes,
                diagnostics.DdgiBufferBytes,
                diagnostics.DdgiCurrentIrradianceAtlasBytes,
                diagnostics.DdgiCurrentVisibilityAtlasBytes,
                diagnostics.DdgiRecursiveIrradianceAtlasBytes,
                diagnostics.DdgiRecursiveVisibilityAtlasBytes,
                diagnostics.DdgiRecursiveProbeStateBytes,
                diagnostics.DdgiRecursiveCommitProbeCount,
                diagnostics.DdgiRecursiveCommitCopyCount,
                diagnostics.DdgiRecursiveCommitBytes,
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
                diagnostics.CpuAccelerationStructureBuildMicroseconds,
                diagnostics.CpuAccelerationStructureBlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureTlasBuildMicroseconds,
                diagnostics.CpuAccelerationStructureInstanceUploadMicroseconds,
                diagnostics.GpuDdgiSnapshotMicroseconds,
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
