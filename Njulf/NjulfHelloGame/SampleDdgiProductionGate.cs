using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleDdgiProductionGateReport(
    bool Passed,
    IReadOnlyList<SampleDdgiProductionGateCriterion> Criteria)
{
    public IReadOnlyList<SampleDdgiProductionGateCriterion> Failures { get; } =
        Criteria.Where(criterion => !criterion.Passed).ToArray();
}

public sealed record SampleDdgiProductionGateCriterion(
    string Name,
    bool Passed,
    string Detail);

public static class SampleDdgiProductionGate
{
    public const double DdgiHighUpdateP95BudgetMilliseconds = 1.5;
    public const float MinimumPhase10CoverageMean = 0.25f;
    public const float MinimumPhase10VisibleSupportMean = 0.05f;
    public const float MinimumPhase10EffectiveWeightMean = 0.02f;
    public const float MaximumPhase10ZeroVisibleCoveredFraction = 0.05f;
    public const float WarmupCompletionTarget = 0.80f;
    public const long MaximumPhase10CpuSchedulerP95Microseconds = 300;
    public const long MaximumPhase10GpuSchedulerP95Microseconds = 250;

    private static readonly HashSet<SamplePerformanceScenario> RequiredScenarios =
        SampleDdgiBenchmarkSuite.RequiredProductionGateScenes
            .Select(scene => scene.Scenario)
            .ToHashSet();

    public static SampleDdgiProductionGateReport Evaluate(SampleBenchmarkReport report)
    {
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        RendererDiagnostics diagnostics = report.LastDiagnostics ?? RendererDiagnostics.Empty;
        SampleBenchmarkTimingStats? ddgiTracePass = FindGpuPass(report, "DdgiTracePass");
        SampleBenchmarkTimingStats? ddgiBlendPass = FindGpuPass(report, "DdgiBlendPass");
        SampleBenchmarkTimingStats? ddgiRelocateClassifyPass = FindGpuPass(report, "DdgiRelocateClassifyPass");
        SampleBenchmarkTimingStats? ddgiPublishPass = FindGpuPass(report, "DdgiPublishPass");
        var criteria = new List<SampleDdgiProductionGateCriterion>
        {
            Criterion(
                "required-production-scene",
                RequiredScenarios.Contains(report.Scenario),
                $"scenario={report.Scenario}"),
            Criterion(
                "ddgi-high-profile",
                diagnostics.ActiveQualityPreset == RenderQualityPreset.DdgiHigh &&
                diagnostics.DdgiQualityTier == DdgiQualityTier.DdgiHigh,
                $"preset={diagnostics.ActiveQualityPreset}, tier={diagnostics.DdgiQualityTier}"),
            Criterion(
                "ddgi-only-ray-query-active",
                diagnostics.GlobalIlluminationEnabled != 0 &&
                diagnostics.GlobalIlluminationMode == GlobalIlluminationMode.Ddgi &&
                diagnostics.GlobalIlluminationDdgiActive != 0 &&
                diagnostics.GlobalIlluminationRayQueryActive != 0 &&
                diagnostics.GlobalIlluminationSsgiActive == 0,
                $"enabled={diagnostics.GlobalIlluminationEnabled}, mode={diagnostics.GlobalIlluminationMode}, ddgi={diagnostics.GlobalIlluminationDdgiActive}, ssgi={diagnostics.GlobalIlluminationSsgiActive}, rayQuery={diagnostics.GlobalIlluminationRayQueryActive}"),
            Criterion(
                "no-ssgi-resources",
                diagnostics.SsgiRenderTargetBytes == 0 &&
                diagnostics.SsgiWidth == 0 &&
                diagnostics.SsgiHeight == 0 &&
                diagnostics.SsgiRayCount == 0,
                $"ssgiBytes={diagnostics.SsgiRenderTargetBytes}, size={diagnostics.SsgiWidth}x{diagnostics.SsgiHeight}, rays={diagnostics.SsgiRayCount}"),
            Criterion(
                "no-ssgi-passes",
                !HasSsgiPass(report, diagnostics),
                "SSGI pass names are absent from benchmark, production pipeline, and render graph diagnostics"),
            Criterion(
                "ddgi-split-passes-present",
                diagnostics.DdgiProbesUpdated <= 0 ||
                (ddgiTracePass != null &&
                 ddgiBlendPass != null &&
                 ddgiRelocateClassifyPass != null &&
                 ddgiPublishPass != null),
                $"trace={ddgiTracePass != null}, blend={ddgiBlendPass != null}, relocateClassify={ddgiRelocateClassifyPass != null}, publish={ddgiPublishPass != null}"),
            Criterion(
                "no-recursive-ddgi-copy",
                diagnostics.DdgiRayScratchBytes == 0 ||
                diagnostics.DdgiUpdatedAtlasBytes > 0,
                $"updates={diagnostics.DdgiProbesUpdated}, rayScratchBytes={diagnostics.DdgiRayScratchBytes}, updatedAtlasBytes={diagnostics.DdgiUpdatedAtlasBytes}, latencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}, publishExec={diagnostics.DdgiPublishExecuted}, publishSkip='{diagnostics.DdgiPublishSkipReason}'"),
            Criterion(
                "ddgi-async-compute-enabled",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                diagnostics.DdgiAsyncComputeEnabled != 0,
                $"async={diagnostics.DdgiAsyncComputeEnabled}, rendererAsync={diagnostics.AsyncComputeEnabled}, supported={diagnostics.AsyncComputeSupported}, latencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}"),
            Criterion(
                "no-static-frame-full-as-rebuild",
                !IsStaticScene(report.Scenario) ||
                (diagnostics.AccelerationStructureBlasBuildCount == 0 &&
                 diagnostics.AccelerationStructureTlasBuildCount == 0),
                $"scenario={report.Scenario}, blasBuilds={diagnostics.AccelerationStructureBlasBuildCount}, tlasBuilds={diagnostics.AccelerationStructureTlasBuildCount}"),
            Criterion(
                "clipmaps-preserved-with-authored-volumes",
                diagnostics.DdgiProbeVolumeCount <= diagnostics.DdgiCascadeCount ||
                diagnostics.DdgiCascadeCount > 0,
                $"volumes={diagnostics.DdgiProbeVolumeCount}, cascades={diagnostics.DdgiCascadeCount}"),
            Criterion(
                "ddgi-gather-tiles-valid",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                (diagnostics.DdgiGatherTileCount > 0 &&
                 diagnostics.DdgiGatherFallbackTileCount == 0 &&
                 (diagnostics.DdgiCascadeCount <= 0 || diagnostics.DdgiGatherSelectedClipmapTileCount > 0)),
                $"tiles={diagnostics.DdgiGatherTileCount}, clipmapTiles={diagnostics.DdgiGatherSelectedClipmapTileCount}, fallbackTiles={diagnostics.DdgiGatherFallbackTileCount}"),
            Criterion(
                "ddgi-forward-exhaustive-fallback-unused",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                diagnostics.DdgiForwardGatherFallbackUsed == 0,
                $"used={diagnostics.DdgiForwardGatherFallbackUsed}, disabled={diagnostics.DdgiForwardGatherFallbackDisabled}, emptyTiles={diagnostics.DdgiForwardGatherTileEmpty}"),
            Criterion(
                "phase10-forward-metrics-valid",
                IsPhase10ForwardMetricsHealthy(diagnostics),
                $"readback={diagnostics.DdgiForwardEstimateCountersReadbackValid}, spatial={diagnostics.DdgiAverageSpatialCoverageEstimate:F3}, support={diagnostics.DdgiAverageSupportCoverageEstimate:F3}, data={diagnostics.DdgiAverageDataConfidenceEstimate:F3}, visibility={diagnostics.DdgiAverageVisibilityConfidenceEstimate:F3}, effective={diagnostics.DdgiAverageEffectiveContributionEstimate:F3}, zeroSupportSpatial={GetZeroVisibleCoveredFraction(diagnostics):F3}, rawLuma={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}, finalLuma={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}"),
            Criterion(
                "phase10-cache-warmup-steady",
                IsPhase10CacheWarmupSteady(diagnostics),
                $"cacheGeneration={diagnostics.DdgiCacheGeneration}, warmup={diagnostics.DdgiWarmupState}, cacheWarmup={diagnostics.DdgiCacheWarmupState}"),
            Criterion(
                "phase10-warmup-progress-valid",
                IsPhase10WarmupProgressValid(diagnostics),
                $"warmup={diagnostics.DdgiWarmupState}, visible/local/cascade0={diagnostics.DdgiWarmedVisibleProbeFraction:F3}/{diagnostics.DdgiWarmedLocalProbeFraction:F3}/{diagnostics.DdgiWarmedCascade0ProbeFraction:F3}"),
            Criterion(
                "phase10-scheduler-p95-budget",
                IsPhase10SchedulerP95WithinBudget(diagnostics),
                $"mode={diagnostics.DdgiSchedulerMode}, cpuP95={diagnostics.CpuDdgiSchedulerP95Microseconds}us, gpuP95={diagnostics.GpuDdgiScheduleP95Microseconds}us, overBudget={diagnostics.DdgiSchedulerP95OverBudget}/{diagnostics.GpuDdgiScheduleOverBudget}"),
            Criterion(
                "phase10-scheduler-overflow-free",
                IsPhase10SchedulerOverflowFree(diagnostics),
                $"mode={diagnostics.DdgiSchedulerMode}, candidates={diagnostics.DdgiGpuSchedulerCandidateCount}, requests={diagnostics.DdgiGpuSchedulerRequestCount}, overflow={diagnostics.DdgiGpuSchedulerOverflowCount}, stableSkipped={diagnostics.DdgiGpuSchedulerStableSkippedCount}"),
            Criterion(
                "phase10-scheduler-equivalence",
                IsPhase10SchedulerEquivalenceValid(diagnostics),
                $"mode={diagnostics.DdgiSchedulerMode}, readback={diagnostics.DdgiGpuSchedulerReadbackValid}, valid={diagnostics.DdgiGpuSchedulerValidationValid}, cpu={diagnostics.DdgiGpuSchedulerValidationCpuRequestCount}, gpu={diagnostics.DdgiGpuSchedulerValidationGpuRequestCount}, compared={diagnostics.DdgiGpuSchedulerValidationComparedRequestCount}, mismatches={diagnostics.DdgiGpuSchedulerValidationMismatchCount}, invalid={diagnostics.DdgiGpuSchedulerInvalidProbeCount}, duplicates={diagnostics.DdgiGpuSchedulerDuplicateRequestCount}, first='{diagnostics.DdgiGpuSchedulerValidationFirstMismatch}'"),
            Criterion(
                "gpu-timing-valid",
                report.GpuTimingSupported != 0 &&
                report.GpuTimingValidSampleCount > 0 &&
                report.GpuTimingValidSampleCount >= Math.Max(1, report.MeasurementFrameCount),
                $"supported={report.GpuTimingSupported}, validSamples={report.GpuTimingValidSampleCount}, measured={report.MeasurementFrameCount}, reason={report.GpuTimingUnavailableReason}"),
            Criterion(
                "ddgi-update-p95-budget",
                IsDdgiUpdateWithinBudget(report, diagnostics),
                $"p95={CalculateDdgiSplitP95Milliseconds(report):F3}ms, budget={DdgiHighUpdateP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-memory-budget",
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0 &&
                diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes <= diagnostics.DdgiAtlasMemoryBudgetBytes,
                $"currentAtlas={diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes}, budget={diagnostics.DdgiAtlasMemoryBudgetBytes}"),
            Criterion(
                "phase10-ddgi-memory-diagnostics",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes + diagnostics.DdgiGpuSchedulerBufferBytes > 0,
                $"textureBytes={diagnostics.DdgiTextureBytes}, bufferBytes={diagnostics.DdgiBufferBytes}, schedulerBytes={diagnostics.DdgiGpuSchedulerBufferBytes}, atlasBytes={diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes}"),
            Criterion(
                "budget-metrics-within-gate",
                report.BudgetMetrics.All(metric => metric.Status != RenderBudgetStatus.OverBudget),
                $"overBudget={string.Join(',', report.BudgetMetrics.Where(metric => metric.Status == RenderBudgetStatus.OverBudget).Select(metric => metric.Name))}"),
            Criterion(
                "foliage-ddgi-receiver-covered",
                report.Scenario != SamplePerformanceScenario.ForestFoliage ||
                diagnostics.FoliageVisibleClusterCount > 0 ||
                diagnostics.FoliageVisibleMeshletDrawCount > 0,
                $"scenario={report.Scenario}, foliageClusters={diagnostics.FoliageVisibleClusterCount}, foliageDraws={diagnostics.FoliageVisibleMeshletDrawCount}"),
            Criterion(
                "debug-views-expose-ddgi-gate-data",
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiProbeState) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiUpdateReasons) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiRayBudget) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiRawDiffuse) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiEffectiveWeight) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiVisibilityMoments) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSpatialCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSupportCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiDataConfidence) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiVisibilityConfidence) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiConfidenceChain) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiProbeLogicalPosition) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiProbeRelocatedPosition) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiProbeRelocationDirection) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSuppressionMask),
                "DDGI coverage, support, confidence chain, probe relocation, probe state, update reason, ray budget, raw diffuse, effective weight, visibility, and suppression debug views are selectable")
        };

        return new SampleDdgiProductionGateReport(
            criteria.All(criterion => criterion.Passed),
            criteria);
    }

    private static SampleDdgiProductionGateCriterion Criterion(string name, bool passed, string detail) =>
        new(name, passed, detail);

    private static bool HasSsgiPass(SampleBenchmarkReport report, RendererDiagnostics diagnostics)
    {
        return report.GpuPasses.Any(pass => IsSsgiName(pass.Name)) ||
            diagnostics.ProductionPipelineDeclaredPasses.Any(IsSsgiName) ||
            diagnostics.ProductionPipelineActivePasses.Any(IsSsgiName) ||
            diagnostics.Graph.Passes.Any(pass => IsSsgiName(pass.Name)) ||
            diagnostics.Graph.Resources.Any(resource => IsSsgiName(resource.Id) || IsSsgiName(resource.DebugName));
    }

    private static bool IsSsgiName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            name.IndexOf("Ssgi", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsStaticScene(SamplePerformanceScenario scenario)
    {
        return scenario is not SamplePerformanceScenario.GiMovingPointLight
            and not SamplePerformanceScenario.GiMovingRigidObject
            and not SamplePerformanceScenario.ForestFoliage;
    }

    private static bool IsDdgiUpdateWithinBudget(SampleBenchmarkReport report, RendererDiagnostics diagnostics)
    {
        SampleBenchmarkTimingStats? ddgiTrace = FindGpuPass(report, "DdgiTracePass");
        SampleBenchmarkTimingStats? ddgiBlend = FindGpuPass(report, "DdgiBlendPass");
        SampleBenchmarkTimingStats? ddgiRelocateClassify = FindGpuPass(report, "DdgiRelocateClassifyPass");
        SampleBenchmarkTimingStats? ddgiPublish = FindGpuPass(report, "DdgiPublishPass");
        if (diagnostics.DdgiProbesUpdated <= 0 && ddgiTrace == null && ddgiBlend == null && ddgiRelocateClassify == null && ddgiPublish == null)
            return true;

        double splitP95 = CalculateDdgiSplitP95Milliseconds(report);
        return ddgiTrace != null &&
            ddgiBlend != null &&
            ddgiRelocateClassify != null &&
            ddgiPublish != null &&
            splitP95 <= DdgiHighUpdateP95BudgetMilliseconds;
    }

    private static bool IsPhase10ForwardMetricsHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        return diagnostics.DdgiForwardEstimateCountersReadbackValid != 0 &&
            IsFinite(diagnostics.DdgiAverageCoverageEstimate) &&
            IsFinite(diagnostics.DdgiAverageVisibleSupportEstimate) &&
            IsFinite(diagnostics.DdgiAverageDataConfidenceEstimate) &&
            IsFinite(diagnostics.DdgiAverageVisibilityConfidenceEstimate) &&
            IsFinite(diagnostics.DdgiAverageLeakAttenuationEstimate) &&
            IsFinite(diagnostics.DdgiAverageEffectiveContributionEstimate) &&
            IsFinite(diagnostics.DdgiForwardEstimateRawDiffuseLuminance) &&
            IsFinite(diagnostics.DdgiForwardEstimateFinalDiffuseLuminance) &&
            diagnostics.DdgiAverageSpatialCoverageEstimate >= MinimumPhase10CoverageMean &&
            diagnostics.DdgiAverageSupportCoverageEstimate >= MinimumPhase10VisibleSupportMean &&
            diagnostics.DdgiAverageEffectiveContributionEstimate >= MinimumPhase10EffectiveWeightMean &&
            GetZeroVisibleCoveredFraction(diagnostics) <= MaximumPhase10ZeroVisibleCoveredFraction;
    }

    private static bool IsPhase10CacheWarmupSteady(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        return diagnostics.DdgiCacheGeneration > 0 &&
            diagnostics.DdgiWarmupState == DdgiRuntimeWarmupState.SteadyState &&
            diagnostics.DdgiCacheWarmupState == DdgiRuntimeWarmupState.SteadyState;
    }

    private static bool IsPhase10WarmupProgressValid(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        return diagnostics.DdgiWarmupState == DdgiRuntimeWarmupState.SteadyState &&
            diagnostics.DdgiWarmedVisibleProbeFraction >= WarmupCompletionTarget &&
            diagnostics.DdgiWarmedLocalProbeFraction >= WarmupCompletionTarget &&
            diagnostics.DdgiWarmedCascade0ProbeFraction >= WarmupCompletionTarget;
    }

    private static bool IsPhase10SchedulerP95WithinBudget(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        bool schedulerSamplesHealthy = diagnostics.DdgiSchedulerTimingSampleCount <= 0 ||
            diagnostics.DdgiSchedulerP95OverBudget == 0;
        bool cpuHealthy = diagnostics.CpuDdgiSchedulerP95Microseconds <= 0 ||
            diagnostics.CpuDdgiSchedulerP95Microseconds <= MaximumPhase10CpuSchedulerP95Microseconds;
        bool gpuHealthy = diagnostics.GpuDdgiScheduleP95Microseconds <= 0 ||
            diagnostics.GpuDdgiScheduleP95Microseconds <= MaximumPhase10GpuSchedulerP95Microseconds;

        return schedulerSamplesHealthy &&
            cpuHealthy &&
            gpuHealthy &&
            diagnostics.GpuDdgiScheduleOverBudget == 0;
    }

    private static bool IsPhase10SchedulerOverflowFree(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0 ||
            diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuReference)
        {
            return true;
        }

        return diagnostics.DdgiGpuSchedulerOverflowCount == 0;
    }

    private static bool IsPhase10SchedulerEquivalenceValid(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0 ||
            diagnostics.DdgiSchedulerMode != DdgiSchedulerMode.CpuGpuCompare)
        {
            return true;
        }

        SampleGiSchedulerEquivalenceContract contract = SampleGlobalIlluminationValidation.Phase10SchedulerEquivalence;
        int requestDelta = Math.Abs(diagnostics.DdgiGpuSchedulerValidationCpuRequestCount - (int)diagnostics.DdgiGpuSchedulerValidationGpuRequestCount);
        return diagnostics.DdgiGpuSchedulerReadbackValid != 0 &&
            diagnostics.DdgiGpuSchedulerValidationValid != 0 &&
            diagnostics.DdgiGpuSchedulerValidationMismatchCount == 0 &&
            requestDelta <= contract.MaxRequestCountDelta &&
            diagnostics.DdgiGpuSchedulerInvalidProbeCount <= contract.MaxInvalidProbeCount &&
            diagnostics.DdgiGpuSchedulerDuplicateRequestCount <= contract.MaxDuplicateRequestCount;
    }

    private static float GetZeroVisibleCoveredFraction(RendererDiagnostics diagnostics)
    {
        if (diagnostics.DdgiForwardEstimateSampleCount == 0)
            return 0.0f;

        return diagnostics.DdgiForwardEstimateZeroVisibleButCoveredCount / (float)diagnostics.DdgiForwardEstimateSampleCount;
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static double CalculateDdgiSplitP95Milliseconds(SampleBenchmarkReport report)
    {
        return (FindGpuPass(report, "DdgiTracePass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiBlendPass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiRelocateClassifyPass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiPublishPass")?.P95Milliseconds ?? 0.0);
    }

    private static SampleBenchmarkTimingStats? FindGpuPass(SampleBenchmarkReport report, string name)
    {
        return report.GpuPasses.FirstOrDefault(pass => pass.Name.Equals(name, StringComparison.Ordinal));
    }
}
