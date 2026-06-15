using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;

namespace Njulf.Rendering.Diagnostics
{
    public enum RendererDiagnosticsCategory
    {
        FrameTiming,
        CpuPassTiming,
        GpuPassTiming,
        GpuMemory,
        UploadStaging,
        RenderGraph,
        GpuScene,
        VisibilityCulling,
        LodImpostorsFoliage,
        Lighting,
        Shadows,
        Particles,
        PostResolution,
        DebugOverlays
    }

    public sealed record RendererDiagnosticsMetric(
        string Name,
        double Value,
        string Unit,
        RenderBudgetStatus Status = RenderBudgetStatus.Unknown,
        bool IsEstimate = false,
        string Text = "");

    public sealed record RendererDiagnosticsCategorySnapshot(
        RendererDiagnosticsCategory Category,
        int SchemaVersion,
        RenderBudgetStatus Status,
        IReadOnlyList<RendererDiagnosticsMetric> Metrics,
        IReadOnlyList<string> Warnings);

    public sealed record RendererDiagnosticsSnapshot(
        int SchemaVersion,
        IReadOnlyList<RendererDiagnosticsCategorySnapshot> Categories,
        IReadOnlyList<string> CompatibilityWarnings)
    {
        public static RendererDiagnosticsSnapshot Empty { get; } = new(
            RendererDiagnosticsSchema.CurrentVersion,
            Array.Empty<RendererDiagnosticsCategorySnapshot>(),
            Array.Empty<string>());

        public RendererDiagnosticsCategorySnapshot GetCategory(RendererDiagnosticsCategory category)
        {
            foreach (RendererDiagnosticsCategorySnapshot snapshot in Categories)
            {
                if (snapshot.Category == category)
                    return snapshot;
            }

            throw new InvalidOperationException($"Diagnostics category '{category}' is missing.");
        }
    }

    public sealed record PerformanceSnapshotMetadata(
        int SchemaVersion,
        int RendererDiagnosticsSchemaVersion,
        IReadOnlyList<string> CompatibilityWarnings);

    public static class RendererDiagnosticsSchema
    {
        public const int CurrentVersion = 1;

        public static RendererDiagnosticsSnapshot Build(
            RendererDiagnostics diagnostics,
            RenderBudgetSnapshot? budget = null,
            RenderGraphResourceInventorySnapshot? resourceInventory = null,
            RenderGraphDiagnosticSnapshot? graphDiagnostics = null,
            FrameOrderAudit? frameAudit = null,
            AsyncSchedulePlan? asyncSchedule = null)
        {
            if (diagnostics == null)
                throw new ArgumentNullException(nameof(diagnostics));

            budget ??= RenderBudgetSnapshot.Empty;
            resourceInventory ??= RenderGraphResourceInventorySnapshot.Empty;

            var categories = new List<RendererDiagnosticsCategorySnapshot>
            {
                BuildFrameTiming(diagnostics, budget),
                BuildCpuPassTiming(diagnostics),
                BuildGpuPassTiming(diagnostics),
                BuildGpuMemory(diagnostics, budget),
                BuildUploadStaging(diagnostics, budget),
                BuildRenderGraph(diagnostics, resourceInventory, graphDiagnostics, frameAudit, asyncSchedule),
                BuildGpuScene(diagnostics),
                BuildVisibility(diagnostics),
                BuildLodImpostorsFoliage(diagnostics),
                BuildLighting(diagnostics),
                BuildShadows(diagnostics),
                BuildParticles(diagnostics),
                BuildPostResolution(diagnostics),
                BuildDebugOverlays(diagnostics)
            };

            return new RendererDiagnosticsSnapshot(CurrentVersion, categories, Array.Empty<string>());
        }

        public static PerformanceSnapshotMetadata ReadMetadata(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Snapshot JSON is required.", nameof(json));

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            int snapshotSchema = ReadInt(root, "SchemaVersion", 0);
            int diagnosticsSchema = 0;

            if (root.TryGetProperty("Diagnostics", out JsonElement diagnostics))
                diagnosticsSchema = ReadInt(diagnostics, "SchemaVersion", 0);

            var warnings = new List<string>();
            if (snapshotSchema == 0)
                warnings.Add("Snapshot schema version is missing; treating file as legacy.");
            if (diagnosticsSchema == 0)
                warnings.Add("Renderer diagnostics schema version is missing; category data must be rebuilt from flat fields.");

            return new PerformanceSnapshotMetadata(snapshotSchema, diagnosticsSchema, warnings);
        }

        private static RendererDiagnosticsCategorySnapshot BuildFrameTiming(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
        {
            return Category(
                RendererDiagnosticsCategory.FrameTiming,
                StatusFor(budget, "CPU renderer", diagnostics.CpuFrameBudgetStatus),
                [
                    Metric("CpuFrame", diagnostics.CpuTotalDrawSceneMicroseconds, "us", diagnostics.CpuFrameBudgetStatus),
                    Metric("GpuFrame", diagnostics.GpuFrameMicroseconds, "us", diagnostics.GpuFrameBudgetStatus),
                    Metric("Acquire", diagnostics.CpuAcquireImageMicroseconds, "us"),
                    Metric("FenceWait", diagnostics.CpuWaitForFrameFenceMicroseconds, "us"),
                    Metric("GraphicsSubmit", diagnostics.CpuGraphicsQueueSubmitMicroseconds, "us"),
                    Metric("ComputeSubmit", diagnostics.CpuComputeQueueSubmitMicroseconds, "us"),
                    Metric("TransferSubmit", diagnostics.CpuTransferQueueSubmitMicroseconds, "us"),
                    Metric("Present", diagnostics.CpuPresentMicroseconds, "us"),
                    Metric("FenceReset", diagnostics.CpuFenceResetMicroseconds, "us"),
                    Metric("RuntimeStall", diagnostics.RuntimeStallMicrosecondsThisFrame, "us")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildCpuPassTiming(RendererDiagnostics diagnostics)
        {
            long recordTotal =
                diagnostics.CpuDirectionalShadowRecordMicroseconds +
                diagnostics.CpuSpotShadowRecordMicroseconds +
                diagnostics.CpuPointShadowRecordMicroseconds +
                diagnostics.CpuDepthPrePassRecordMicroseconds +
                diagnostics.CpuHiZBuildRecordMicroseconds +
                diagnostics.CpuLightCullRecordMicroseconds +
                diagnostics.CpuForwardOpaqueRecordMicroseconds +
                diagnostics.CpuTransparentRecordMicroseconds +
                diagnostics.CpuWeightedOitCompositeRecordMicroseconds +
                diagnostics.CpuBloomExtractRecordMicroseconds +
                diagnostics.CpuBloomDownsampleRecordMicroseconds +
                diagnostics.CpuBloomUpsampleRecordMicroseconds +
                diagnostics.CpuFogRecordMicroseconds +
                diagnostics.CpuCompositeRecordMicroseconds +
                diagnostics.CpuAmbientOcclusionRecordMicroseconds +
                diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds +
                diagnostics.CpuFxaaRecordMicroseconds +
                diagnostics.CpuSmaaEdgeRecordMicroseconds +
                diagnostics.CpuSmaaBlendRecordMicroseconds +
                diagnostics.CpuSmaaNeighborhoodRecordMicroseconds +
                diagnostics.CpuParticleRecordMicroseconds +
                diagnostics.CpuTrailBeamRecordMicroseconds +
                diagnostics.CpuDebugDrawRecordMicroseconds +
                diagnostics.CpuDebugOverlayRecordMicroseconds +
                diagnostics.CpuRenderGraphBarrierMicroseconds;

            return Category(
                RendererDiagnosticsCategory.CpuPassTiming,
                RenderBudgetStatus.Unknown,
                [
                    Metric("SceneBuild", diagnostics.CpuSceneBuildMicroseconds, "us"),
                    Metric("ObjectCull", diagnostics.CpuObjectCullMicroseconds, "us"),
                    Metric("MeshletCull", diagnostics.CpuMeshletCullMicroseconds, "us"),
                    Metric("Upload", diagnostics.CpuUploadMicroseconds, "us"),
                    Metric("GraphOrPassRecording", recordTotal, "us"),
                    Metric("PrimaryCommandRecord", diagnostics.CpuPrimaryCommandRecordMicroseconds, "us"),
                    Metric("SecondaryCommandRecord", diagnostics.CpuSecondaryCommandRecordMicroseconds, "us"),
                    Metric("RenderGraphBarriers", diagnostics.CpuRenderGraphBarrierMicroseconds, "us")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildGpuPassTiming(RendererDiagnostics diagnostics)
        {
            long passTotal =
                diagnostics.GpuDepthPrePassMicroseconds +
                diagnostics.GpuHiZBuildMicroseconds +
                diagnostics.GpuLightCullMicroseconds +
                diagnostics.GpuForwardOpaqueMicroseconds +
                diagnostics.GpuTransparentMicroseconds +
                diagnostics.GpuWeightedOitCompositeMicroseconds +
                diagnostics.GpuFogMicroseconds +
                diagnostics.GpuAmbientOcclusionMicroseconds +
                diagnostics.GpuAmbientOcclusionBlurMicroseconds +
                diagnostics.GpuAntiAliasingMicroseconds +
                diagnostics.GpuCompositeMicroseconds +
                diagnostics.GpuBloomExtractMicroseconds +
                diagnostics.GpuBloomDownsampleMicroseconds +
                diagnostics.GpuBloomUpsampleMicroseconds +
                diagnostics.GpuDirectionalShadowMicroseconds +
                diagnostics.GpuSpotShadowMicroseconds +
                diagnostics.GpuPointShadowMicroseconds +
                diagnostics.GpuSkinningMicroseconds +
                diagnostics.GpuParticleMicroseconds +
                diagnostics.GpuTrailBeamMicroseconds +
                diagnostics.GpuDebugDrawMicroseconds +
                diagnostics.GpuDebugOverlayMicroseconds;

            var warnings = new List<string>();
            if (diagnostics.GpuTimingSupported == 0)
                warnings.Add("GPU timestamps are not supported.");
            else if (diagnostics.GpuTimingEnabled == 0)
                warnings.Add("GPU timestamps are disabled.");
            else if (diagnostics.GpuTimingPending != 0)
                warnings.Add("GPU timestamps are pending asynchronous readback.");
            else if (diagnostics.GpuTimingValid == 0)
                warnings.Add(string.IsNullOrWhiteSpace(diagnostics.GpuTimingUnavailableReason)
                    ? "GPU timestamps are unavailable."
                    : diagnostics.GpuTimingUnavailableReason);

            RenderBudgetStatus status = diagnostics.GpuTimingValid == 0
                ? RenderBudgetStatus.Unavailable
                : RenderBudgetStatus.Unknown;

            return Category(
                RendererDiagnosticsCategory.GpuPassTiming,
                status,
                [
                    Metric("GpuTimingSupported", diagnostics.GpuTimingSupported, "bool"),
                    Metric("GpuTimingEnabled", diagnostics.GpuTimingEnabled, "bool"),
                    Metric("GpuTimingPending", diagnostics.GpuTimingPending, "bool"),
                    Metric("GpuTimingValid", diagnostics.GpuTimingValid, "bool"),
                    Metric("GpuTimingFrameLatency", diagnostics.GpuTimingFrameLatency, "frames"),
                    Metric("PassTotal", passTotal, "us")
                ],
                warnings);
        }

        private static RendererDiagnosticsCategorySnapshot BuildGpuMemory(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
        {
            return Category(
                RendererDiagnosticsCategory.GpuMemory,
                StatusFor(budget, "GPU memory", diagnostics.GpuMemoryBudgetStatus),
                [
                    Metric("TrackedGpuMemory", diagnostics.TrackedGpuMemoryBytes, "bytes", diagnostics.GpuMemoryBudgetStatus),
                    Metric("ActualGpuMemoryUsage", diagnostics.ActualGpuMemoryUsageBytes, "bytes"),
                    Metric("ActualGpuMemoryBudget", diagnostics.ActualGpuMemoryBudgetBytes, "bytes"),
                    Metric("MeshBuffersAllocated", diagnostics.MeshBufferAllocatedBytes, "bytes"),
                    Metric("MaterialBuffersAllocated", diagnostics.MaterialBufferAllocatedBytes, "bytes"),
                    Metric("SceneBuffersAllocated", diagnostics.SceneBufferAllocatedBytes, "bytes"),
                    Metric("TextureAssets", diagnostics.TextureAssetBytes, "bytes"),
                    Metric("RenderTargets", diagnostics.RenderTargetBytes, "bytes"),
                    Metric("ShadowMaps", diagnostics.ShadowMapBytes, "bytes"),
                    Metric("Environment", diagnostics.EnvironmentMapBytes + diagnostics.IrradianceMapBytes + diagnostics.PrefilteredEnvironmentBytes + diagnostics.BrdfLutBytes, "bytes"),
                    Metric("ReflectionProbes", diagnostics.ReflectionProbeBytes, "bytes"),
                    Metric("Staging", diagnostics.StagingBufferAllocatedBytes, "bytes")
                ],
                diagnostics.GpuMemoryBudgetQueryAvailable == 0
                    ? ["Actual heap budget is unavailable; using tracked estimates where present."]
                    : []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildUploadStaging(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
        {
            return Category(
                RendererDiagnosticsCategory.UploadStaging,
                StatusFor(budget, "Upload", diagnostics.UploadBudgetStatus),
                [
                    Metric("UploadedBytes", diagnostics.UploadedBytes, "bytes", diagnostics.UploadBudgetStatus),
                    Metric("ObjectUploadBytes", diagnostics.ObjectUploadBytes, "bytes"),
                    Metric("InstanceUploadBytes", diagnostics.InstanceUploadBytes, "bytes"),
                    Metric("MeshletDrawUploadBytes", diagnostics.MeshletDrawUploadBytes + diagnostics.TransparentMeshletDrawUploadBytes, "bytes"),
                    Metric("MaterialUploadBytes", diagnostics.MaterialUploadBytes + diagnostics.MaterialExtensionUploadBytes, "bytes"),
                    Metric("LightUploadBytes", diagnostics.LightUploadBytes, "bytes"),
                    Metric("ParticleUploadBytes", diagnostics.ParticleInstanceUploadBytes + diagnostics.TrailBeamUploadBytes, "bytes"),
                    Metric("SkinningUploadBytes", diagnostics.SkinningUploadBytes, "bytes"),
                    Metric("StagingUsedThisFrame", diagnostics.StagingBytesUsedThisFrame, "bytes"),
                    Metric("StagingPeakThisSession", diagnostics.StagingBytesPeakThisSession, "bytes"),
                    Metric("StagingOverflowCount", diagnostics.StagingOverflowCount, "count")
                ],
                diagnostics.UploadBudgetExceeded != 0 ? ["Upload budget exceeded this frame."] : []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildRenderGraph(
            RendererDiagnostics diagnostics,
            RenderGraphResourceInventorySnapshot resourceInventory,
            RenderGraphDiagnosticSnapshot? graphDiagnostics,
            FrameOrderAudit? frameAudit,
            AsyncSchedulePlan? asyncSchedule)
        {
            var metrics = new List<RendererDiagnosticsMetric>
            {
                Metric("PassCount", resourceInventory.PassOrder.Count, "count"),
                Metric("ImageResourceCount", resourceInventory.Images.Count, "count"),
                Metric("BufferResourceCount", resourceInventory.Buffers.Count, "count"),
                Metric("EstimatedImageBytes", resourceInventory.EstimatedImageBytes, "bytes", isEstimate: true),
                Metric("EstimatedBufferBytes", resourceInventory.EstimatedBufferBytes, "bytes", isEstimate: true),
                Metric("GraphCompile", diagnostics.CpuRenderGraphCompileMicroseconds, "us")
            };

            var warnings = new List<string>();
            if (graphDiagnostics != null)
            {
                metrics.Add(Metric("CompiledPassCount", graphDiagnostics.CompiledPassOrder.Count, "count"));
                metrics.Add(Metric("CulledPassCount", graphDiagnostics.CulledPasses.Count, "count"));
                metrics.Add(Metric("GeneratedBarrierCount", graphDiagnostics.BarrierCount, "count"));
                metrics.Add(Metric("AliasGroupCount", graphDiagnostics.AliasGroups.Count, "count"));
                metrics.Add(Metric("LiveResourceLifetimeCount", graphDiagnostics.ResourceLifetimes.Count, "count"));
            }

            if (frameAudit != null)
            {
                metrics.Add(Metric("VisibilityFirstAuditValid", frameAudit.IsValid ? 1 : 0, "bool"));
                metrics.Add(Metric("VisibilityFirstAuditEntryCount", frameAudit.Entries.Count, "count"));
                metrics.Add(Metric("VisibilityFirstAuditErrorCount", frameAudit.Errors.Count, "count"));
                warnings.AddRange(frameAudit.Errors);
            }

            if (asyncSchedule != null)
            {
                metrics.Add(Metric("AsyncScheduledPassCount", asyncSchedule.Passes.Count(pass => pass.Async), "count"));
                metrics.Add(Metric("AsyncSyncEdgeCount", asyncSchedule.SyncEdges.Count, "count"));
                metrics.Add(Metric("AsyncGraphicsWaitCount", asyncSchedule.QueueDiagnostics.GraphicsWaitCount, "count"));
                metrics.Add(Metric("AsyncComputeWaitCount", asyncSchedule.QueueDiagnostics.ComputeWaitCount, "count"));
                metrics.Add(Metric("AsyncOwnershipTransferCount", asyncSchedule.QueueDiagnostics.OwnershipTransferCount, "count"));
                metrics.Add(Metric("AsyncBandwidthHeavyPassCount", asyncSchedule.QueueDiagnostics.BandwidthHeavyAsyncPassCount, "count"));
                metrics.Add(Metric("AsyncImmediateWaitAvoidedCount", asyncSchedule.QueueDiagnostics.ImmediateGraphicsWaitAvoidedCount, "count"));
                metrics.Add(Metric("AsyncTinyDispatchAvoidedCount", asyncSchedule.QueueDiagnostics.TinyDispatchAvoidedCount, "count"));
                if (!string.IsNullOrWhiteSpace(asyncSchedule.Diagnostic))
                    warnings.Add(asyncSchedule.Diagnostic);
            }

            return Category(
                RendererDiagnosticsCategory.RenderGraph,
                frameAudit is { IsValid: false } ? RenderBudgetStatus.OverBudget : RenderBudgetStatus.Unknown,
                metrics,
                warnings);
        }

        private static RendererDiagnosticsCategorySnapshot BuildGpuScene(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.GpuScene,
                RenderBudgetStatus.Unknown,
                [
                    Metric("SceneBufferAllocatedBytes", diagnostics.SceneBufferAllocatedBytes, "bytes"),
                    Metric("SceneBufferPeakBytes", diagnostics.SceneBufferPeakBytes, "bytes"),
                    Metric("SceneBufferResizeCount", diagnostics.SceneBufferResizeCount, "count"),
                    Metric("SceneObjectBufferHighWaterBytes", diagnostics.SceneObjectBufferHighWaterBytes, "bytes"),
                    Metric("SceneUploadCount", diagnostics.SceneUploadCount, "count"),
                    Metric("SceneUploadSkipped", diagnostics.SceneUploadSkipped, "count"),
                    Metric("ScenePayloadRebuilt", diagnostics.ScenePayloadRebuilt, "bool"),
                    Metric("GpuDrivenVisibilityEnabled", diagnostics.GpuDrivenVisibilityEnabled, "bool"),
                    Metric("GpuVisibilityDrawCapacity", diagnostics.GpuVisibilityDrawCapacity, "draws"),
                    Metric("GpuVisibilityResizeCount", diagnostics.GpuVisibilityResizeCount, "count"),
                    Metric("GpuVisibilityAllocatedBytes", diagnostics.GpuVisibilityAllocatedBytes, "bytes"),
                    Metric("StaticInstanceBatchCount", diagnostics.StaticInstanceBatchCount, "count"),
                    Metric("StaticInstanceCount", diagnostics.StaticInstanceCount, "count")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildVisibility(RendererDiagnostics diagnostics)
        {
            var warnings = new List<string>();
            if (diagnostics.ForwardMeshletVisibleAfterOcclusion > diagnostics.ForwardMeshletCandidates)
                warnings.Add("Forward visible meshlets exceed candidates.");
            if (diagnostics.ForwardGpuOcclusionRejectedMeshlets > diagnostics.ForwardOcclusionTestedMeshletsGpu)
                warnings.Add("Forward occlusion rejections exceed tested meshlets.");
            if (diagnostics.VisibleStaticInstanceCount + diagnostics.CulledStaticInstanceCount > diagnostics.StaticInstanceCount)
                warnings.Add("Static instance visibility counters exceed total instances.");

            RenderBudgetStatus status = warnings.Count == 0 ? RenderBudgetStatus.Unknown : RenderBudgetStatus.OverBudget;

            return Category(
                RendererDiagnosticsCategory.VisibilityCulling,
                status,
                [
                    Metric("ObjectCandidatesCpu", diagnostics.ObjectCandidatesCpu, "count"),
                    Metric("ObjectFrustumCulledCpu", diagnostics.ObjectFrustumCulledCpu, "count"),
                    Metric("VisibleObjects", diagnostics.VisibleObjectCount, "count"),
                    Metric("MeshletCandidatesCpu", diagnostics.MeshletCandidatesCpu, "count"),
                    Metric("MeshletFrustumCulledCpu", diagnostics.MeshletFrustumCulledCpu, "count"),
                    Metric("ForwardMeshletCandidates", diagnostics.ForwardMeshletCandidates, "count"),
                    Metric("ForwardVisibleAfterOcclusion", diagnostics.ForwardMeshletVisibleAfterOcclusion, "count"),
                    Metric("ForwardGpuOcclusionRejected", diagnostics.ForwardGpuOcclusionRejectedMeshlets, "count"),
                    Metric("GpuDrivenVisibilityEnabled", diagnostics.GpuDrivenVisibilityEnabled, "bool"),
                    Metric("GpuVisibilityDrawCapacity", diagnostics.GpuVisibilityDrawCapacity, "draws"),
                    Metric("GpuVisibilityResizeCount", diagnostics.GpuVisibilityResizeCount, "count"),
                    Metric("TransparentSortCandidates", diagnostics.TransparentSortCandidateCount, "count"),
                    Metric("TransparentOverflowCount", diagnostics.TransparentOverflowCount, "count"),
                    Metric("WeightedOitEnabled", diagnostics.WeightedOitEnabled, "bool"),
                    Metric("WeightedOitRenderTargetBytes", diagnostics.WeightedOitRenderTargetBytes, "bytes")
                ],
                warnings);
        }

        private static RendererDiagnosticsCategorySnapshot BuildLodImpostorsFoliage(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.LodImpostorsFoliage,
                RenderBudgetStatus.Unknown,
                [
                    Metric("Lod0Submitted", diagnostics.MeshletLod0Submitted, "count"),
                    Metric("Lod1Submitted", diagnostics.MeshletLod1Submitted, "count"),
                    Metric("Lod2Submitted", diagnostics.MeshletLod2Submitted, "count"),
                    Metric("LodSkipped", diagnostics.MeshletLodSkippedCpu, "count"),
                    Metric("StaticInstances", diagnostics.StaticInstanceCount, "count"),
                    Metric("VisibleStaticInstances", diagnostics.VisibleStaticInstanceCount, "count"),
                    Metric("CulledStaticInstances", diagnostics.CulledStaticInstanceCount, "count")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildLighting(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.Lighting,
                RenderBudgetStatus.Unknown,
                [
                    Metric("LightCount", diagnostics.LightCount, "count"),
                    Metric("TileCount", diagnostics.TileCount, "count"),
                    Metric("LightTileCapacity", diagnostics.LightTileCapacity, "count"),
                    Metric("LightTileSaturationCount", diagnostics.LightTileSaturationCount, "count"),
                    Metric("MaxLightsInAnyTile", diagnostics.MaxLightsInAnyTile, "count"),
                    Metric("AverageLightsPerNonEmptyTile", diagnostics.AverageLightsPerNonEmptyTile, "count"),
                    Metric("DebugLightTileMaxCount", diagnostics.DebugLightTileMaxCount, "count"),
                    Metric("DebugLightTileAverageCount", diagnostics.DebugLightTileAverageCount, "count")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildShadows(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.Shadows,
                RenderBudgetStatus.Unknown,
                [
                    Metric("DirectionalShadowsEnabled", diagnostics.DirectionalShadowsEnabled, "bool"),
                    Metric("DirectionalCascadeCount", diagnostics.DirectionalShadowCascadeCount, "count"),
                    Metric("SpotShadowSelectedCount", diagnostics.SpotShadowSelectedCount, "count"),
                    Metric("SpotShadowRejectedByBudgetCount", diagnostics.SpotShadowRejectedByBudgetCount, "count"),
                    Metric("SpotShadowAtlasUtilization", diagnostics.SpotShadowAtlasUtilization, "ratio"),
                    Metric("PointShadowSelectedCount", diagnostics.PointShadowSelectedCount, "count"),
                    Metric("PointShadowRenderedFaceCount", diagnostics.PointShadowRenderedFaceCount, "count"),
                    Metric("PointShadowSkippedFaceCount", diagnostics.PointShadowSkippedFaceCount, "count"),
                    Metric("ShadowMapBytes", diagnostics.ShadowMapBytes, "bytes")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildParticles(RendererDiagnostics diagnostics)
        {
            RenderBudgetStatus status = diagnostics.ParticleBudgetExceeded != 0 || diagnostics.ParticleUploadBudgetExceeded != 0
                ? RenderBudgetStatus.OverBudget
                : RenderBudgetStatus.Unknown;

            return Category(
                RendererDiagnosticsCategory.Particles,
                status,
                [
                    Metric("ParticlesEnabled", diagnostics.ParticlesEnabled, "bool"),
                    Metric("ParticleEmitterCount", diagnostics.ParticleEmitterCount, "count"),
                    Metric("LiveParticleCount", diagnostics.LiveParticleCount, "count"),
                    Metric("SimulatedParticleCount", diagnostics.SimulatedParticleCount, "count"),
                    Metric("CulledParticleCount", diagnostics.CulledParticleCount, "count"),
                    Metric("RenderedParticleCount", diagnostics.RenderedParticleCount, "count"),
                    Metric("ParticleDrawCallCount", diagnostics.ParticleDrawCallCount, "count"),
                    Metric("ParticleBudgetExceeded", diagnostics.ParticleBudgetExceeded, "bool")
                ],
                diagnostics.ParticleBudgetExceeded != 0 ? ["Particle budget exceeded."] : []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildPostResolution(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.PostResolution,
                RenderBudgetStatus.Unknown,
                [
                    Metric("HdrEnabled", diagnostics.HdrEnabled, "bool"),
                    Metric("BloomEnabled", diagnostics.BloomEnabled, "bool"),
                    Metric("BloomMipCount", diagnostics.BloomMipCount, "count"),
                    Metric("AmbientOcclusionEnabled", diagnostics.AmbientOcclusionEnabled, "bool"),
                    Metric("AmbientOcclusionWidth", diagnostics.AmbientOcclusionWidth, "pixels"),
                    Metric("AmbientOcclusionHeight", diagnostics.AmbientOcclusionHeight, "pixels"),
                    Metric("AntiAliasingMode", (int)diagnostics.AntiAliasingMode, "enum", text: diagnostics.AntiAliasingMode.ToString()),
                    Metric("AntiAliasingWidth", diagnostics.AntiAliasingWidth, "pixels"),
                    Metric("AntiAliasingHeight", diagnostics.AntiAliasingHeight, "pixels"),
                    Metric("FogEnabled", diagnostics.FogEnabled, "bool"),
                    Metric("FogWidth", diagnostics.FogWidth, "pixels"),
                    Metric("FogHeight", diagnostics.FogHeightPixels, "pixels")
                ],
                []);
        }

        private static RendererDiagnosticsCategorySnapshot BuildDebugOverlays(RendererDiagnostics diagnostics)
        {
            return Category(
                RendererDiagnosticsCategory.DebugOverlays,
                RenderBudgetStatus.Unknown,
                [
                    Metric("DebugToolingEnabled", diagnostics.DebugToolingEnabled, "bool"),
                    Metric("DebugOverlayEnabled", diagnostics.DebugOverlayEnabled, "bool"),
                    Metric("DebugOverlayMode", (int)diagnostics.DebugOverlayMode, "enum", text: diagnostics.DebugOverlayMode.ToString()),
                    Metric("CpuDebugSnapshotsEnabled", diagnostics.CpuDebugSnapshotsEnabled, "bool"),
                    Metric("DebugDrawEnabled", diagnostics.DebugDrawEnabled, "bool"),
                    Metric("DebugDrawLineCount", diagnostics.DebugDrawLineCount, "count"),
                    Metric("DebugDrawDroppedLineCount", diagnostics.DebugDrawDroppedLineCount, "count"),
                    Metric("DebugObjectBoundsDrawn", diagnostics.DebugObjectBoundsDrawn, "count"),
                    Metric("DebugMeshletBoundsDrawn", diagnostics.DebugMeshletBoundsDrawn, "count"),
                    Metric("DebugMeshletBoundsDropped", diagnostics.DebugMeshletBoundsDropped, "count")
                ],
                diagnostics.DebugDrawDroppedLineCount > 0 || diagnostics.DebugMeshletBoundsDropped > 0
                    ? ["Debug overlay draw budget dropped primitives."]
                    : []);
        }

        private static RendererDiagnosticsCategorySnapshot Category(
            RendererDiagnosticsCategory category,
            RenderBudgetStatus status,
            IReadOnlyList<RendererDiagnosticsMetric> metrics,
            IReadOnlyList<string> warnings)
        {
            return new RendererDiagnosticsCategorySnapshot(category, CurrentVersion, status, metrics, warnings);
        }

        private static RendererDiagnosticsMetric Metric(
            string name,
            double value,
            string unit,
            RenderBudgetStatus status = RenderBudgetStatus.Unknown,
            bool isEstimate = false,
            string text = "")
        {
            return new RendererDiagnosticsMetric(name, value, unit, status, isEstimate, text);
        }

        private static RenderBudgetStatus StatusFor(
            RenderBudgetSnapshot budget,
            string metricName,
            RenderBudgetStatus fallback)
        {
            BudgetMetric? metric = budget.Metrics.FirstOrDefault(m => string.Equals(m.Name, metricName, StringComparison.Ordinal));
            if (metric != null)
                return metric.Status;
            return fallback;
        }

        private static int ReadInt(JsonElement element, string propertyName, int fallback)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement property))
                return fallback;
            return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value)
                ? value
                : fallback;
        }
    }
}
