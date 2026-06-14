using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;

namespace Njulf.Rendering.Diagnostics
{
    public sealed record RendererDiagnosticsOverlaySnapshot(
        int SchemaVersion,
        IReadOnlyList<RendererDiagnosticsOverlayBar> FrameTiming,
        IReadOnlyList<RendererDiagnosticsPassOverlayRow> PassTimings,
        IReadOnlyList<RendererDiagnosticsOverlayBar> GpuMemory,
        IReadOnlyList<RendererDiagnosticsOverlayBar> Uploads,
        RendererDiagnosticsGraphOverlay Graph,
        RendererDiagnosticsVisibilityOverlay Visibility,
        RendererDiagnosticsLightingOverlay Lighting,
        IReadOnlyList<string> Warnings)
    {
        public static RendererDiagnosticsOverlaySnapshot Empty { get; } = new(
            RendererDiagnosticsSchema.CurrentVersion,
            Array.Empty<RendererDiagnosticsOverlayBar>(),
            Array.Empty<RendererDiagnosticsPassOverlayRow>(),
            Array.Empty<RendererDiagnosticsOverlayBar>(),
            Array.Empty<RendererDiagnosticsOverlayBar>(),
            RendererDiagnosticsGraphOverlay.Empty,
            RendererDiagnosticsVisibilityOverlay.Empty,
            RendererDiagnosticsLightingOverlay.Empty,
            Array.Empty<string>());
    }

    public sealed record RendererDiagnosticsOverlayBar(
        string Name,
        double Value,
        double Budget,
        string Unit,
        double BudgetFraction,
        RenderBudgetStatus Status,
        bool IsEstimate = false);

    public sealed record RendererDiagnosticsPassOverlayRow(
        string PassName,
        long CpuMicroseconds,
        long GpuMicroseconds,
        bool GpuAvailable,
        string Queue,
        bool Async,
        string SchedulingReason);

    public sealed record RendererDiagnosticsGraphOverlay(
        int PassCount,
        int CulledPassCount,
        long CompileMicroseconds,
        int BarrierCount,
        int AliasGroupCount,
        ulong AliasBytesSaved,
        ulong TransientPeakBytes,
        IReadOnlyList<string> PassOrder,
        IReadOnlyList<string> CulledPasses,
        IReadOnlyList<RendererDiagnosticsGraphLifetimeOverlay> Lifetimes)
    {
        public static RendererDiagnosticsGraphOverlay Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<RendererDiagnosticsGraphLifetimeOverlay>());
    }

    public sealed record RendererDiagnosticsGraphLifetimeOverlay(
        string ResourceName,
        string Kind,
        int FirstUsePassIndex,
        int LastUsePassIndex,
        ulong EstimatedBytes);

    public sealed record RendererDiagnosticsVisibilityOverlay(
        int ObjectCandidates,
        int ObjectFrustumCulled,
        int VisibleObjects,
        int MeshletCandidates,
        int MeshletFrustumCulled,
        int ForwardOcclusionTested,
        int ForwardOcclusionRejected,
        int ForwardVisibleAfterOcclusion,
        int StaticInstances,
        int VisibleStaticInstances,
        int CulledStaticInstances,
        bool GpuDrivenVisibilityEnabled,
        int GpuVisibilityDrawCapacity,
        int GpuVisibilityResizeCount)
    {
        public static RendererDiagnosticsVisibilityOverlay Empty { get; } = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0);
    }

    public sealed record RendererDiagnosticsLightingOverlay(
        int LightCount,
        uint TileCountX,
        uint TileCountY,
        int LightTileCapacity,
        int LightTileSaturationCount,
        int MaxLightsInAnyTile,
        float AverageLightsPerNonEmptyTile,
        int DirectionalCascadeCount,
        int SpotShadowSelectedCount,
        int SpotShadowRejectedByBudgetCount,
        float SpotShadowAtlasUtilization,
        int PointShadowSelectedCount,
        int PointShadowRenderedFaceCount,
        int PointShadowSkippedFaceCount)
    {
        public static RendererDiagnosticsLightingOverlay Empty { get; } = new(
            0, 0, 0, 0, 0, 0, 0f, 0, 0, 0, 0f, 0, 0, 0);
    }

    public static class RendererDiagnosticsOverlayBuilder
    {
        public static RendererDiagnosticsOverlaySnapshot Build(
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot? budget = null,
            RenderGraphResourceInventorySnapshot? resourceInventory = null,
            RenderGraphDiagnosticSnapshot? graphDiagnostics = null,
            AsyncSchedulePlan? asyncSchedule = null)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            budget ??= RenderBudgetSnapshot.Empty;
            resourceInventory ??= RenderGraphResourceInventorySnapshot.Empty;

            var warnings = new List<string>();
            if (diagnostics.GpuTimingSupported == 0)
                warnings.Add("GPU timing unsupported.");
            else if (diagnostics.GpuTimingEnabled == 0)
                warnings.Add("GPU timing disabled.");
            else if (diagnostics.GpuTimingPending != 0)
                warnings.Add("GPU timing pending.");
            else if (diagnostics.GpuTimingValid == 0 && !string.IsNullOrWhiteSpace(diagnostics.GpuTimingUnavailableReason))
                warnings.Add(diagnostics.GpuTimingUnavailableReason);

            return new RendererDiagnosticsOverlaySnapshot(
                RendererDiagnosticsSchema.CurrentVersion,
                BuildFrameTiming(diagnostics, budget),
                BuildPassRows(diagnostics, asyncSchedule),
                BuildMemoryBars(diagnostics, budget),
                BuildUploadBars(diagnostics, budget),
                BuildGraphOverlay(diagnostics, resourceInventory, graphDiagnostics),
                BuildVisibilityOverlay(diagnostics),
                BuildLightingOverlay(diagnostics),
                warnings);
        }

        private static IReadOnlyList<RendererDiagnosticsOverlayBar> BuildFrameTiming(
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot budget)
        {
            double cpuBudget = FindBudget(budget, "CPU renderer", diagnostics.ActiveBudgetProfileName, diagnostics.CpuTotalDrawSceneMicroseconds / 1000.0);
            double gpuBudget = FindBudget(budget, "GPU frame", diagnostics.ActiveBudgetProfileName, diagnostics.GpuFrameMicroseconds / 1000.0);
            return
            [
                Bar("CPU frame", diagnostics.CpuTotalDrawSceneMicroseconds / 1000.0, cpuBudget, "ms", diagnostics.CpuFrameBudgetStatus),
                Bar("GPU frame", diagnostics.GpuFrameMicroseconds / 1000.0, gpuBudget, "ms", diagnostics.GpuFrameBudgetStatus),
                Bar("Acquire", diagnostics.CpuAcquireImageMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Fence wait", diagnostics.CpuWaitForFrameFenceMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Fence reset", diagnostics.CpuFenceResetMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Graphics submit", diagnostics.CpuGraphicsQueueSubmitMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Compute submit", diagnostics.CpuComputeQueueSubmitMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Transfer submit", diagnostics.CpuTransferQueueSubmitMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Present", diagnostics.CpuPresentMicroseconds, 0, "us", RenderBudgetStatus.Unknown),
                Bar("Runtime stall", diagnostics.RuntimeStallMicrosecondsThisFrame, 0, "us", RenderBudgetStatus.Unknown)
            ];
        }

        private static IReadOnlyList<RendererDiagnosticsPassOverlayRow> BuildPassRows(
            RendererDiagnostics diagnostics,
            AsyncSchedulePlan? asyncSchedule)
        {
            Dictionary<string, ScheduledPass> schedule = asyncSchedule?.Passes.ToDictionary(pass => pass.PassName, StringComparer.Ordinal) ??
                new Dictionary<string, ScheduledPass>(StringComparer.Ordinal);

            return
            [
                Pass("DirectionalShadowPass", diagnostics.CpuDirectionalShadowRecordMicroseconds, diagnostics.GpuDirectionalShadowMicroseconds, diagnostics, schedule),
                Pass("SpotShadowPass", diagnostics.CpuSpotShadowRecordMicroseconds, diagnostics.GpuSpotShadowMicroseconds, diagnostics, schedule),
                Pass("PointShadowPass", diagnostics.CpuPointShadowRecordMicroseconds, diagnostics.GpuPointShadowMicroseconds, diagnostics, schedule),
                Pass("DepthPrePass", diagnostics.CpuDepthPrePassRecordMicroseconds, diagnostics.GpuDepthPrePassMicroseconds, diagnostics, schedule),
                Pass("HiZBuildPass", diagnostics.CpuHiZBuildRecordMicroseconds, diagnostics.GpuHiZBuildMicroseconds, diagnostics, schedule),
                Pass("AmbientOcclusionPass", diagnostics.CpuAmbientOcclusionRecordMicroseconds, diagnostics.GpuAmbientOcclusionMicroseconds, diagnostics, schedule),
                Pass("AmbientOcclusionBlurPass", diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds, diagnostics.GpuAmbientOcclusionBlurMicroseconds, diagnostics, schedule),
                Pass("TiledLightCullingPass", diagnostics.CpuLightCullRecordMicroseconds, diagnostics.GpuLightCullMicroseconds, diagnostics, schedule),
                Pass("ForwardPlusPass", diagnostics.CpuForwardOpaqueRecordMicroseconds, diagnostics.GpuForwardOpaqueMicroseconds, diagnostics, schedule),
                Pass("TransparentForwardPass", diagnostics.CpuTransparentRecordMicroseconds, diagnostics.GpuTransparentMicroseconds, diagnostics, schedule),
                Pass("WeightedOitCompositePass", diagnostics.CpuWeightedOitCompositeRecordMicroseconds, diagnostics.GpuWeightedOitCompositeMicroseconds, diagnostics, schedule),
                Pass("ParticlePass", diagnostics.CpuParticleRecordMicroseconds, diagnostics.GpuParticleMicroseconds, diagnostics, schedule),
                Pass("TrailBeamPass", diagnostics.CpuTrailBeamRecordMicroseconds, diagnostics.GpuTrailBeamMicroseconds, diagnostics, schedule),
                Pass("BloomPass", diagnostics.CpuBloomExtractRecordMicroseconds + diagnostics.CpuBloomDownsampleRecordMicroseconds + diagnostics.CpuBloomUpsampleRecordMicroseconds, diagnostics.GpuBloomExtractMicroseconds + diagnostics.GpuBloomDownsampleMicroseconds + diagnostics.GpuBloomUpsampleMicroseconds, diagnostics, schedule),
                Pass("FogPass", diagnostics.CpuFogRecordMicroseconds, diagnostics.GpuFogMicroseconds, diagnostics, schedule),
                Pass("ToneMapCompositePass", diagnostics.CpuCompositeRecordMicroseconds, diagnostics.GpuCompositeMicroseconds, diagnostics, schedule),
                Pass("AntiAliasingPass", diagnostics.CpuFxaaRecordMicroseconds + diagnostics.CpuSmaaEdgeRecordMicroseconds + diagnostics.CpuSmaaBlendRecordMicroseconds + diagnostics.CpuSmaaNeighborhoodRecordMicroseconds, diagnostics.GpuAntiAliasingMicroseconds, diagnostics, schedule),
                Pass("DebugDrawPass", diagnostics.CpuDebugDrawRecordMicroseconds, diagnostics.GpuDebugDrawMicroseconds, diagnostics, schedule),
                Pass("DebugOverlayPass", diagnostics.CpuDebugOverlayRecordMicroseconds, diagnostics.GpuDebugOverlayMicroseconds, diagnostics, schedule)
            ];
        }

        private static IReadOnlyList<RendererDiagnosticsOverlayBar> BuildMemoryBars(
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot budget)
        {
            double gpuBudget = FindBudget(budget, "GPU memory", diagnostics.ActiveBudgetProfileName, diagnostics.TrackedGpuMemoryBytes);
            return
            [
                Bar("Actual heap usage", diagnostics.ActualGpuMemoryUsageBytes, diagnostics.ActualGpuMemoryBudgetBytes, "bytes", diagnostics.GpuMemoryBudgetStatus),
                Bar("Tracked GPU memory", diagnostics.TrackedGpuMemoryBytes, gpuBudget, "bytes", diagnostics.GpuMemoryBudgetStatus, isEstimate: diagnostics.GpuMemoryBudgetQueryAvailable == 0),
                Bar("Mesh buffers", diagnostics.MeshBufferAllocatedBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Scene buffers", diagnostics.SceneBufferAllocatedBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Material buffers", diagnostics.MaterialBufferAllocatedBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Light buffers", diagnostics.LightBufferAllocatedBytes + diagnostics.TiledLightBufferAllocatedBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Texture assets", diagnostics.TextureAssetBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Render targets", diagnostics.RenderTargetBytes, 0, "bytes", RenderBudgetStatus.Unknown, isEstimate: true),
                Bar("Shadow maps", diagnostics.ShadowMapBytes, 0, "bytes", RenderBudgetStatus.Unknown, isEstimate: true),
                Bar("Reflection probes", diagnostics.ReflectionProbeBytes, 0, "bytes", RenderBudgetStatus.Unknown, isEstimate: true),
                Bar("Staging ring", diagnostics.StagingBufferAllocatedBytes, 0, "bytes", RenderBudgetStatus.Unknown)
            ];
        }

        private static IReadOnlyList<RendererDiagnosticsOverlayBar> BuildUploadBars(
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot budget)
        {
            var bars = new List<RendererDiagnosticsOverlayBar>
            {
                Bar("Total upload", diagnostics.UploadedBytes, diagnostics.UploadBudgetBytesPerFrame, "bytes", diagnostics.UploadBudgetStatus),
                Bar("Object upload", diagnostics.ObjectUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Instance upload", diagnostics.InstanceUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Meshlet draw upload", diagnostics.MeshletDrawUploadBytes + diagnostics.TransparentMeshletDrawUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Material upload", diagnostics.MaterialUploadBytes + diagnostics.MaterialExtensionUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Light upload", diagnostics.LightUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Skinning upload", diagnostics.SkinningUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Particle upload", diagnostics.ParticleInstanceUploadBytes + diagnostics.TrailBeamUploadBytes, 0, "bytes", RenderBudgetStatus.Unknown),
                Bar("Staging used", diagnostics.StagingBytesUsedThisFrame, diagnostics.StagingBufferAllocatedBytes, "bytes", RenderBudgetStatus.Unknown),
                Bar("Staging peak", diagnostics.StagingBytesPeakThisSession, diagnostics.StagingBufferAllocatedBytes, "bytes", RenderBudgetStatus.Unknown)
            };

            foreach (UploadBudgetEntry entry in budget.Upload.Entries)
                bars.Add(Bar($"Budget {entry.Category}", entry.Bytes, budget.Upload.BudgetBytes, "bytes", budget.Upload.Status));

            return bars;
        }

        private static RendererDiagnosticsGraphOverlay BuildGraphOverlay(
            RendererDiagnostics diagnostics,
            RenderGraphResourceInventorySnapshot inventory,
            RenderGraphDiagnosticSnapshot? graphDiagnostics)
        {
            IReadOnlyList<string> passOrder = graphDiagnostics?.CompiledPassOrder ?? inventory.PassOrder;
            IReadOnlyList<string> culledPasses = graphDiagnostics?.CulledPasses ?? Array.Empty<string>();
            IReadOnlyList<RenderGraphAliasGroup> aliasGroups = graphDiagnostics?.AliasGroups ?? Array.Empty<RenderGraphAliasGroup>();
            IReadOnlyList<RenderGraphResourceLifetime> lifetimes = graphDiagnostics?.ResourceLifetimes ?? Array.Empty<RenderGraphResourceLifetime>();
            ulong saved = 0;
            foreach (RenderGraphAliasGroup group in aliasGroups)
                saved = checked(saved + group.AliasedBytesSaved);

            Dictionary<string, ulong> bytesByResource = inventory.Images
                .ToDictionary(image => image.Name, image => image.EstimatedBytes, StringComparer.Ordinal);
            foreach (RenderGraphBufferResourceInventory buffer in inventory.Buffers)
                bytesByResource[buffer.Name] = buffer.ByteSize;

            var lifetimeRows = new List<RendererDiagnosticsGraphLifetimeOverlay>(lifetimes.Count);
            foreach (RenderGraphResourceLifetime lifetime in lifetimes)
            {
                bytesByResource.TryGetValue(lifetime.Name, out ulong estimatedBytes);
                lifetimeRows.Add(new RendererDiagnosticsGraphLifetimeOverlay(
                    lifetime.Name,
                    lifetime.Kind.ToString(),
                    lifetime.FirstUsePassIndex,
                    lifetime.LastUsePassIndex,
                    estimatedBytes));
            }

            return new RendererDiagnosticsGraphOverlay(
                passOrder.Count,
                culledPasses.Count,
                diagnostics.CpuRenderGraphCompileMicroseconds,
                graphDiagnostics?.BarrierCount ?? 0,
                aliasGroups.Count,
                saved,
                checked(inventory.EstimatedImageBytes + inventory.EstimatedBufferBytes - Math.Min(saved, inventory.EstimatedImageBytes + inventory.EstimatedBufferBytes)),
                passOrder,
                culledPasses,
                lifetimeRows);
        }

        private static RendererDiagnosticsVisibilityOverlay BuildVisibilityOverlay(RendererDiagnostics diagnostics)
        {
            return new RendererDiagnosticsVisibilityOverlay(
                diagnostics.ObjectCandidatesCpu,
                diagnostics.ObjectFrustumCulledCpu,
                diagnostics.VisibleObjectCount,
                diagnostics.MeshletCandidatesCpu,
                diagnostics.MeshletFrustumCulledCpu,
                diagnostics.ForwardOcclusionTestedMeshletsGpu,
                diagnostics.ForwardGpuOcclusionRejectedMeshlets,
                diagnostics.ForwardMeshletVisibleAfterOcclusion,
                diagnostics.StaticInstanceCount,
                diagnostics.VisibleStaticInstanceCount,
                diagnostics.CulledStaticInstanceCount,
                diagnostics.GpuDrivenVisibilityEnabled != 0,
                diagnostics.GpuVisibilityDrawCapacity,
                diagnostics.GpuVisibilityResizeCount);
        }

        private static RendererDiagnosticsLightingOverlay BuildLightingOverlay(RendererDiagnostics diagnostics)
        {
            return new RendererDiagnosticsLightingOverlay(
                diagnostics.LightCount,
                diagnostics.TileCountX,
                diagnostics.TileCountY,
                diagnostics.LightTileCapacity,
                diagnostics.LightTileSaturationCount,
                diagnostics.MaxLightsInAnyTile,
                diagnostics.AverageLightsPerNonEmptyTile,
                diagnostics.DirectionalShadowCascadeCount,
                diagnostics.SpotShadowSelectedCount,
                diagnostics.SpotShadowRejectedByBudgetCount,
                diagnostics.SpotShadowAtlasUtilization,
                diagnostics.PointShadowSelectedCount,
                diagnostics.PointShadowRenderedFaceCount,
                diagnostics.PointShadowSkippedFaceCount);
        }

        private static RendererDiagnosticsPassOverlayRow Pass(
            string passName,
            long cpuMicroseconds,
            long gpuMicroseconds,
            RendererDiagnostics diagnostics,
            IReadOnlyDictionary<string, ScheduledPass> schedule)
        {
            bool scheduled = schedule.TryGetValue(passName, out ScheduledPass? scheduledPass);
            RenderGraphQueueClass queue = scheduledPass?.Queue ?? RenderGraphQueueClass.Graphics;
            string reason = scheduledPass?.Reason ?? string.Empty;
            return new RendererDiagnosticsPassOverlayRow(
                passName,
                cpuMicroseconds,
                gpuMicroseconds,
                diagnostics.GpuTimingValid != 0 && gpuMicroseconds > 0,
                scheduled ? queue.ToString() : "Graphics",
                scheduled && scheduledPass?.Async == true,
                scheduled ? reason : string.Empty);
        }

        private static RendererDiagnosticsOverlayBar Bar(
            string name,
            double value,
            double budget,
            string unit,
            RenderBudgetStatus status,
            bool isEstimate = false)
        {
            return new RendererDiagnosticsOverlayBar(
                name,
                value,
                budget,
                unit,
                CalculateFraction(value, budget),
                status,
                isEstimate);
        }

        private static double FindBudget(RenderBudgetSnapshot budget, string name, string profileName, double fallbackValue)
        {
            BudgetMetric? metric = budget.Metrics.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (metric != null)
                return metric.FailureThreshold;

            _ = profileName;
            _ = fallbackValue;
            return 0;
        }

        private static double CalculateFraction(double value, double budget)
        {
            if (budget <= 0 || double.IsInfinity(budget) || double.IsNaN(budget))
                return 0;
            if (double.IsNaN(value) || value <= 0)
                return 0;
            return Math.Clamp(value / budget, 0, 1);
        }
    }
}
