using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
    public sealed class RenderBudgetEvaluator
    {
        public const double WarningRatio = 0.85;

        public static RenderBudgetStatus Classify(double value, double failureThreshold)
        {
            if (double.IsNaN(value) || double.IsNaN(failureThreshold))
                return RenderBudgetStatus.Unavailable;
            if (double.IsPositiveInfinity(failureThreshold))
                return RenderBudgetStatus.WithinBudget;
            if (failureThreshold <= 0)
                return RenderBudgetStatus.Unavailable;
            if (value > failureThreshold)
                return RenderBudgetStatus.OverBudget;
            if (value > failureThreshold * WarningRatio)
                return RenderBudgetStatus.Warning;
            return RenderBudgetStatus.WithinBudget;
        }

        public RenderBudgetSnapshot Evaluate(
            RenderBudgetProfile profile,
            RendererDiagnostics diagnostics,
            MemoryBudgetSnapshot memory,
            UploadBudgetSnapshot upload,
            RuntimeStallSnapshot stalls)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));
            if (memory == null)
                throw new ArgumentNullException(nameof(memory));
            if (upload == null)
                throw new ArgumentNullException(nameof(upload));
            if (stalls == null)
                throw new ArgumentNullException(nameof(stalls));

            bool hasActualGpuMemoryBudget = memory.HeapBudget.IsAvailable && memory.HeapBudget.PrimaryBudgetBytes > 0;
            var metrics = new List<BudgetMetric>(hasActualGpuMemoryBudget ? 13 : 12)
            {
                CreateMetric("CPU renderer", diagnostics.CpuTotalDrawSceneMicroseconds / 1000.0, profile.CpuFrameBudgetMilliseconds, "ms"),
                CreateMetric("GPU frame", diagnostics.GpuFrameMicroseconds / 1000.0, profile.GpuFrameBudgetMilliseconds, "ms",
                    diagnostics.GpuTimingValid == 0 ? RenderBudgetStatus.Unavailable : null),
                CreateMetric("GPU memory", memory.EffectiveMemoryBytes, memory.EffectiveBudgetBytes, "bytes"),
                CreateMetric("Upload", upload.TotalBytes, profile.UploadBudgetBytesPerFrame, "bytes"),
                CreateMetric("Objects", diagnostics.VisibleObjectCount, profile.ObjectBudget, "count"),
                CreateMetric("Meshlets", diagnostics.MeshletCountTotal + diagnostics.FoliageVisibleMeshletDrawCount, profile.MeshletBudget, "count"),
                CreateMetric("Materials", diagnostics.MaterialCount, profile.MaterialBudget, "count"),
                CreateMetric("Textures", diagnostics.TextureCount, profile.TextureBudget, "count"),
                CreateMetric("Lights", diagnostics.LightCount, profile.LightBudget, "count"),
                CreateMetric("Shadowed lights", diagnostics.SpotShadowSelectedCount + diagnostics.PointShadowSelectedCount + (diagnostics.ShadowedDirectionalLightIndex >= 0 ? 1 : 0), profile.ShadowedLightBudget, "count"),
                CreateMetric("Reflection probes", diagnostics.ReflectionProbeCount, profile.ReflectionProbeBudget, "count"),
                CreateMetric("Transparent objects", diagnostics.TransparentObjectCount, profile.TransparentObjectBudget, "count")
            };

            if (hasActualGpuMemoryBudget)
                metrics.Add(CreateMetric("Tracked GPU memory", memory.TotalTrackedBytes, profile.GpuMemoryBudgetBytes, "bytes"));

            RenderBudgetStatus overall = Combine(metrics);
            return new RenderBudgetSnapshot(profile, metrics, memory, upload, stalls, overall);
        }

        private static BudgetMetric CreateMetric(
            string name,
            double value,
            double failureThreshold,
            string unit,
            RenderBudgetStatus? forcedStatus = null)
        {
            return new BudgetMetric(
                name,
                value,
                failureThreshold * WarningRatio,
                failureThreshold,
                unit,
                forcedStatus ?? Classify(value, failureThreshold));
        }

        private static RenderBudgetStatus Combine(IReadOnlyList<BudgetMetric> metrics)
        {
            bool sawWarning = false;
            bool sawAvailable = false;
            foreach (BudgetMetric metric in metrics)
            {
                if (metric.Status == RenderBudgetStatus.OverBudget)
                    return RenderBudgetStatus.OverBudget;
                if (metric.Status == RenderBudgetStatus.Warning)
                    sawWarning = true;
                if (metric.Status is RenderBudgetStatus.WithinBudget or RenderBudgetStatus.Warning)
                    sawAvailable = true;
            }

            if (sawWarning)
                return RenderBudgetStatus.Warning;
            return sawAvailable ? RenderBudgetStatus.WithinBudget : RenderBudgetStatus.Unavailable;
        }
    }
}
