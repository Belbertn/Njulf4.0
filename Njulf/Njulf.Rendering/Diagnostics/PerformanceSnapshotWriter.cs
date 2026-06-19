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
            var snapshot = new PerformanceSnapshot(capturedAt, budget.Profile, diagnostics, CreateFoliageSnapshot(diagnostics), budget);
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

        private static void UpdateMax(long value, string label, ref long max, ref string maxLabel)
        {
            if (value <= max)
                return;

            max = value;
            maxLabel = label;
        }
    }
}
