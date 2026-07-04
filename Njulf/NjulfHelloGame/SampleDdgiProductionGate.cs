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
    public const double DdgiLowUpdateP95BudgetMilliseconds = 0.75;
    public const double DdgiMediumUpdateP95BudgetMilliseconds = 1.0;
    public const double DdgiHighUpdateP95BudgetMilliseconds = 1.5;
    public const double DdgiUltraUpdateP95BudgetMilliseconds = 2.5;
    public const double GlobalSdfP95BudgetMilliseconds = 0.50;
    public const double SurfaceCacheP95BudgetMilliseconds = 0.70;
    public const double DdgiTraceP95BudgetMilliseconds = 0.85;
    public const double DdgiBlendP95BudgetMilliseconds = 0.35;
    public const double DdgiRelocateClassifyP95BudgetMilliseconds = 0.20;
    public const double DdgiPublishP95BudgetMilliseconds = 0.10;
    public const float MaximumSurfaceCacheFallbackPercent = 2.0f;
    public const float MinimumPhase10CoverageMean = 0.25f;
    public const float MinimumPhase10VisibleSupportMean = 0.05f;
    public const float MinimumPhase10EffectiveWeightMean = 0.02f;
    public const float MaximumPhase10ZeroVisibleCoveredFraction = 0.05f;
    public const float MinimumPhase9HealthyRawDiffuseLuminance = 0.05f;
    public const float MinimumPhase9HealthyFinalDiffuseLuminance = 0.015f;
    public const float MinimumPhase9FinalToRawLuminanceRatio = 0.25f;
    public const float MaximumPhase9FallbackWeightForHealthyDdgi = 1.0f;
    public const float MinimumPhase9EmissiveBounceLuminance = 0.01f;
    public const float MaximumPhase9ThinWallLeakAttenuation = 0.98f;
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
        SampleBenchmarkTimingStats? ddgiSchedulePass = FindGpuPass(report, "DdgiSchedulePass");
        double updateP95BudgetMilliseconds = GetDdgiUpdateP95BudgetMilliseconds(diagnostics.DdgiQualityTier);
        var criteria = new List<SampleDdgiProductionGateCriterion>
        {
            Criterion(
                "required-production-scene",
                RequiredScenarios.Contains(report.Scenario),
                $"scenario={report.Scenario}"),
            Criterion(
                "ddgi-high-profile",
                diagnostics.ActiveQualityPreset == RenderQualityPreset.DdgiHigh &&
                Enum.IsDefined(diagnostics.DdgiQualityTier),
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
                (ddgiSchedulePass != null &&
                 ddgiTracePass != null &&
                 ddgiBlendPass != null &&
                 ddgiRelocateClassifyPass != null &&
                 ddgiPublishPass != null),
                $"schedule={ddgiSchedulePass != null}, trace={ddgiTracePass != null}, blend={ddgiBlendPass != null}, relocateClassify={ddgiRelocateClassifyPass != null}, publish={ddgiPublishPass != null}"),
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
                $"readback={diagnostics.DdgiForwardEstimateCountersReadbackValid}, spatial={diagnostics.DdgiAverageSpatialCoverageEstimate:F3}, support={diagnostics.DdgiAverageSupportCoverageEstimate:F3}, data={diagnostics.DdgiAverageDataConfidenceEstimate:F3}, visibility={diagnostics.DdgiAverageVisibilityConfidenceEstimate:F3}, effective={diagnostics.DdgiAverageEffectiveContributionEstimate:F3}, zeroSupportSpatial={GetZeroVisibleCoveredFraction(diagnostics):F3}, sampledIrrLuma={diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F3}, ddgiDiffuseLuma={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}, hybridFinalLuma={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}"),
            Criterion(
                "phase9-raw-atlas-to-final-energy",
                IsPhase9RawAtlasToFinalEnergyHealthy(diagnostics),
                $"blendIrrLum={diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage:F3}, sampledIrrLum={diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F3}, rawDiffuseLum={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}, finalLum={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}, finalRawRatio={Ratio(diagnostics.DdgiForwardEstimateFinalDiffuseLuminance, diagnostics.DdgiForwardEstimateRawDiffuseLuminance):F3}, effective={diagnostics.DdgiAverageEffectiveContributionEstimate:F3}"),
            Criterion(
                "phase9-environment-fallback-not-dominant",
                IsPhase9FallbackHealthy(diagnostics),
                $"fallbackWeight={diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight:F3}, rawDiffuseLum={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}, finalLum={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}, effective={diagnostics.DdgiAverageEffectiveContributionEstimate:F3}"),
            Criterion(
                "phase9-emissive-bounce-present",
                IsPhase9EmissiveBounceHealthy(report.Scenario, diagnostics),
                $"scenario={report.Scenario}, emissiveSources={diagnostics.DdgiEmissiveSourceCount}, traceEmissiveLum={diagnostics.DdgiTraceEnergyEmissiveLuminanceAverage:F3}, rawDiffuseLum={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}"),
            Criterion(
                "phase9-thin-wall-leak-policy-active",
                IsPhase9ThinWallLeakPolicyHealthy(report.Scenario, diagnostics),
                $"scenario={report.Scenario}, leakAttenuation={diagnostics.DdgiAverageLeakAttenuationEstimate:F3}, finalLum={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}, rawDiffuseLum={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}"),
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
                $"mode={diagnostics.DdgiSchedulerMode}, candidates={diagnostics.DdgiGpuSchedulerCandidateCount}, requests={diagnostics.DdgiGpuSchedulerRequestCount}, hardOverflow={diagnostics.DdgiGpuSchedulerOverflowCount}, candidateBufferOverflow={diagnostics.DdgiGpuSchedulerCandidateBufferOverflowCount}, bucketCapDrop={diagnostics.DdgiGpuSchedulerPerBucketOverflowCount}, stableSkipped={diagnostics.DdgiGpuSchedulerStableSkippedCount}"),
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
                $"tier={diagnostics.DdgiQualityTier}, p95={CalculateDdgiTotalUpdateP95Milliseconds(report):F3}ms, budget={updateP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "global-sdf-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "GlobalSdfPass", GlobalSdfP95BudgetMilliseconds, diagnostics.GpuGlobalSdfMicroseconds),
                $"p95={GetGpuPassP95Milliseconds(report, "GlobalSdfPass"):F3}ms, last={diagnostics.GpuGlobalSdfMicroseconds / 1000.0:F3}ms, budget={GlobalSdfP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "surface-cache-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "SurfaceCachePass", SurfaceCacheP95BudgetMilliseconds, diagnostics.GpuSurfaceCacheMicroseconds),
                $"p95={GetGpuPassP95Milliseconds(report, "SurfaceCachePass"):F3}ms, last={diagnostics.GpuSurfaceCacheMicroseconds / 1000.0:F3}ms, budget={SurfaceCacheP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-trace-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "DdgiTracePass", DdgiTraceP95BudgetMilliseconds, diagnostics.GpuDdgiTraceMicroseconds),
                $"p95={GetGpuPassP95Milliseconds(report, "DdgiTracePass"):F3}ms, last={diagnostics.GpuDdgiTraceMicroseconds / 1000.0:F3}ms, budget={DdgiTraceP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-blend-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "DdgiBlendPass", DdgiBlendP95BudgetMilliseconds, diagnostics.GpuDdgiBlendMicroseconds),
                $"p95={GetGpuPassP95Milliseconds(report, "DdgiBlendPass"):F3}ms, last={diagnostics.GpuDdgiBlendMicroseconds / 1000.0:F3}ms, budget={DdgiBlendP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-relocate-classify-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "DdgiRelocateClassifyPass", DdgiRelocateClassifyP95BudgetMilliseconds, 0),
                $"p95={GetGpuPassP95Milliseconds(report, "DdgiRelocateClassifyPass"):F3}ms, budget={DdgiRelocateClassifyP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "ddgi-publish-p95-budget",
                IsGpuPassWithinBudget(report, diagnostics, "DdgiPublishPass", DdgiPublishP95BudgetMilliseconds, 0),
                $"p95={GetGpuPassP95Milliseconds(report, "DdgiPublishPass"):F3}ms, budget={DdgiPublishP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "surface-cache-fallback-under-2-percent",
                IsSurfaceCacheFallbackWithinGate(diagnostics),
                $"hits={diagnostics.DdgiSurfaceCacheHitCount}, fallbacks={diagnostics.DdgiSurfaceCacheFallbackCount}, fallback={diagnostics.DdgiSurfaceCacheFallbackPercent:F2}%, budget={MaximumSurfaceCacheFallbackPercent:F2}%"),
            Criterion(
                "phase8-emergency-degrade-preserves-near-field",
                IsPhase8EmergencyDegradeHealthy(diagnostics),
                $"active={diagnostics.DdgiEmergencyDegradeActive}, reduced={diagnostics.DdgiAdaptiveBudgetReduced}, scale={diagnostics.DdgiAdaptiveBudgetScale:F3}, reason={diagnostics.DdgiAdaptiveBudgetReason}, visible={diagnostics.DdgiVisibleFrustumProbeUpdateCount}, dirty={diagnostics.DdgiDirtyBoundsProbeUpdateCount}, new={diagnostics.DdgiNewProbeCount}, offFrustum={diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount}, updated={diagnostics.DdgiProbesUpdated}"),
            Criterion(
                "ddgi-memory-budget",
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0 &&
                diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes <= diagnostics.DdgiAtlasMemoryBudgetBytes,
                $"currentAtlas={diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes}, budget={diagnostics.DdgiAtlasMemoryBudgetBytes}"),
            Criterion(
                "phase8-tier-memory-budget",
                diagnostics.DdgiAtlasMemoryBudgetBytes > 0 &&
                diagnostics.DdgiAtlasMemoryBudgetBytes <= GetDdgiAtlasMemoryBudgetBytes(diagnostics.DdgiQualityTier),
                $"tier={diagnostics.DdgiQualityTier}, budget={diagnostics.DdgiAtlasMemoryBudgetBytes}, target={GetDdgiAtlasMemoryBudgetBytes(diagnostics.DdgiQualityTier)}"),
            Criterion(
                "phase8-tier-hybrid-memory-budget",
                IsHybridMemoryWithinTierBudget(diagnostics),
                $"tier={diagnostics.DdgiQualityTier}, ddgi={diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes + diagnostics.DdgiGpuSchedulerBufferBytes}, globalSdf={diagnostics.GlobalSdfTextureBytes}, surfaceCache={diagnostics.SurfaceCacheAtlasBytes}, budget={GetHybridMemoryBudgetBytes(diagnostics.DdgiQualityTier)}"),
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
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSampledIrradiance) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiFinalDiffuse) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiRawDiffuse) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiEffectiveWeight) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiVisibilityMoments) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSpatialCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiSupportCoverage) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiDataConfidence) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiVisibilityConfidence) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiConfidenceChain) &&
                Enum.IsDefined(GlobalIlluminationDebugView.DdgiConfidenceBypass) &&
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

    public static double GetDdgiUpdateP95BudgetMilliseconds(DdgiQualityTier tier)
    {
        return tier switch
        {
            DdgiQualityTier.DdgiLow => DdgiLowUpdateP95BudgetMilliseconds,
            DdgiQualityTier.DdgiMedium => DdgiMediumUpdateP95BudgetMilliseconds,
            DdgiQualityTier.DdgiUltra => DdgiUltraUpdateP95BudgetMilliseconds,
            _ => DdgiHighUpdateP95BudgetMilliseconds
        };
    }

    public static ulong GetDdgiAtlasMemoryBudgetBytes(DdgiQualityTier tier)
    {
        return tier switch
        {
            DdgiQualityTier.DdgiLow => 64UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiMedium => 128UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiUltra => 384UL * 1024UL * 1024UL,
            _ => 192UL * 1024UL * 1024UL
        };
    }

    public static ulong GetHybridMemoryBudgetBytes(DdgiQualityTier tier)
    {
        return tier switch
        {
            DdgiQualityTier.DdgiLow => 112UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiMedium => 224UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiUltra => 640UL * 1024UL * 1024UL,
            _ => 384UL * 1024UL * 1024UL
        };
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
        SampleBenchmarkTimingStats? ddgiSchedule = FindGpuPass(report, "DdgiSchedulePass");
        if (diagnostics.DdgiProbesUpdated <= 0 && ddgiSchedule == null && ddgiTrace == null && ddgiBlend == null && ddgiRelocateClassify == null && ddgiPublish == null)
            return true;

        double totalP95 = CalculateDdgiTotalUpdateP95Milliseconds(report);
        return ddgiSchedule != null &&
            ddgiTrace != null &&
            ddgiBlend != null &&
            ddgiRelocateClassify != null &&
            ddgiPublish != null &&
            totalP95 <= GetDdgiUpdateP95BudgetMilliseconds(diagnostics.DdgiQualityTier);
    }

    private static bool IsGpuPassWithinBudget(
        SampleBenchmarkReport report,
        RendererDiagnostics diagnostics,
        string passName,
        double budgetMilliseconds,
        long lastFrameMicroseconds)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        SampleBenchmarkTimingStats? pass = FindGpuPass(report, passName);
        if (pass != null)
            return pass.Count > 0 && pass.P95Milliseconds <= budgetMilliseconds;

        return lastFrameMicroseconds <= 0 ||
            lastFrameMicroseconds / 1000.0 <= budgetMilliseconds;
    }

    private static double GetGpuPassP95Milliseconds(SampleBenchmarkReport report, string passName) =>
        FindGpuPass(report, passName)?.P95Milliseconds ?? 0.0;

    private static bool IsSurfaceCacheFallbackWithinGate(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        uint attempts = diagnostics.DdgiSurfaceCacheHitCount + diagnostics.DdgiSurfaceCacheFallbackCount;
        if (attempts == 0)
            return diagnostics.SurfaceCacheExecuted == 0;

        float fallbackPercent = diagnostics.DdgiSurfaceCacheFallbackPercent > 0.0f
            ? diagnostics.DdgiSurfaceCacheFallbackPercent
            : diagnostics.DdgiSurfaceCacheFallbackCount * 100.0f / attempts;
        return fallbackPercent < MaximumSurfaceCacheFallbackPercent;
    }

    private static bool IsHybridMemoryWithinTierBudget(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        ulong hybridBytes =
            diagnostics.DdgiTextureBytes +
            diagnostics.DdgiBufferBytes +
            diagnostics.DdgiGpuSchedulerBufferBytes +
            diagnostics.GlobalSdfTextureBytes +
            diagnostics.SurfaceCacheAtlasBytes;
        return hybridBytes > 0 &&
            hybridBytes <= GetHybridMemoryBudgetBytes(diagnostics.DdgiQualityTier);
    }

    private static bool IsPhase8EmergencyDegradeHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0 ||
            diagnostics.DdgiEmergencyDegradeActive == 0)
        {
            return true;
        }

        int nearProtectedUpdates = diagnostics.DdgiVisibleFrustumProbeUpdateCount +
            diagnostics.DdgiDirtyBoundsProbeUpdateCount +
            diagnostics.DdgiNewProbeCount;
        bool reducedWork = diagnostics.DdgiAdaptiveBudgetReduced != 0 &&
            diagnostics.DdgiAdaptiveBudgetScale < 1.0f;
        bool nearFieldPreserved = diagnostics.DdgiProbesUpdated <= 0 ||
            nearProtectedUpdates > 0;
        bool offFrustumNotDominant = diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount <=
            Math.Max(nearProtectedUpdates, 1);

        return reducedWork &&
            nearFieldPreserved &&
            offFrustumNotDominant;
    }

    private static bool IsPhase10ForwardMetricsHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        return diagnostics.DdgiForwardEstimateCountersReadbackValid != 0 &&
            IsFinite(diagnostics.DdgiAverageSpatialCoverageEstimate) &&
            IsFinite(diagnostics.DdgiAverageSupportCoverageEstimate) &&
            IsFinite(diagnostics.DdgiAverageDataConfidenceEstimate) &&
            IsFinite(diagnostics.DdgiAverageVisibilityConfidenceEstimate) &&
            IsFinite(diagnostics.DdgiAverageLeakAttenuationEstimate) &&
            IsFinite(diagnostics.DdgiAverageEffectiveContributionEstimate) &&
            IsFinite(diagnostics.DdgiForwardEstimateSampledIrradianceLuminance) &&
            IsFinite(diagnostics.DdgiForwardEstimateRawDiffuseLuminance) &&
            IsFinite(diagnostics.DdgiForwardEstimateFinalDiffuseLuminance) &&
            diagnostics.DdgiAverageSpatialCoverageEstimate >= MinimumPhase10CoverageMean &&
            diagnostics.DdgiAverageSupportCoverageEstimate >= MinimumPhase10VisibleSupportMean &&
            diagnostics.DdgiAverageEffectiveContributionEstimate >= MinimumPhase10EffectiveWeightMean &&
            GetZeroVisibleCoveredFraction(diagnostics) <= MaximumPhase10ZeroVisibleCoveredFraction;
    }

    private static bool IsPhase9RawAtlasToFinalEnergyHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;
        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return false;

        float rawDiffuseLuminance = Math.Max(diagnostics.DdgiForwardEstimateRawDiffuseLuminance, 0.0f);
        float sampledIrradianceLuminance = Math.Max(diagnostics.DdgiForwardEstimateSampledIrradianceLuminance, 0.0f);
        float blendIrradianceLuminance = Math.Max(diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage, 0.0f);
        float finalDiffuseLuminance = Math.Max(diagnostics.DdgiForwardEstimateFinalDiffuseLuminance, 0.0f);

        bool atlasOrSampledEnergyHealthy = rawDiffuseLuminance >= MinimumPhase9HealthyRawDiffuseLuminance ||
            sampledIrradianceLuminance >= MinimumPhase9HealthyRawDiffuseLuminance ||
            blendIrradianceLuminance >= MinimumPhase9HealthyRawDiffuseLuminance;
        if (!atlasOrSampledEnergyHealthy)
            return true;

        return finalDiffuseLuminance >= MinimumPhase9HealthyFinalDiffuseLuminance &&
            Ratio(finalDiffuseLuminance, Math.Max(rawDiffuseLuminance, MinimumPhase9HealthyRawDiffuseLuminance)) >= MinimumPhase9FinalToRawLuminanceRatio &&
            diagnostics.DdgiAverageEffectiveContributionEstimate >= MinimumPhase10EffectiveWeightMean;
    }

    private static bool IsPhase9FallbackHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;
        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return false;

        bool finalVisible = diagnostics.DdgiForwardEstimateFinalDiffuseLuminance >= MinimumPhase9HealthyFinalDiffuseLuminance;
        bool ddgiWeak = diagnostics.DdgiForwardEstimateRawDiffuseLuminance < MinimumPhase9HealthyRawDiffuseLuminance ||
            diagnostics.DdgiAverageEffectiveContributionEstimate < MinimumPhase10EffectiveWeightMean;
        bool fallbackDominant = diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight > MaximumPhase9FallbackWeightForHealthyDdgi;

        return !(finalVisible && ddgiWeak && fallbackDominant);
    }

    private static bool IsPhase9EmissiveBounceHealthy(SamplePerformanceScenario scenario, RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0 ||
            scenario != SamplePerformanceScenario.GiEmissiveMaterialRoom)
        {
            return true;
        }

        return diagnostics.DdgiTraceEnergyEmissiveLuminanceAverage >= MinimumPhase9EmissiveBounceLuminance ||
            diagnostics.DdgiEmissiveSourceCount > 0 &&
            diagnostics.DdgiForwardEstimateRawDiffuseLuminance >= MinimumPhase9HealthyRawDiffuseLuminance;
    }

    private static bool IsPhase9ThinWallLeakPolicyHealthy(SamplePerformanceScenario scenario, RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0 ||
            scenario is not (SamplePerformanceScenario.GiThinWallLeakTest or SamplePerformanceScenario.GiLongCorridorOcclusion))
        {
            return true;
        }

        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return false;

        return diagnostics.DdgiAverageLeakAttenuationEstimate <= MaximumPhase9ThinWallLeakAttenuation ||
            diagnostics.DdgiForwardEstimateFinalDiffuseLuminance < MinimumPhase9HealthyFinalDiffuseLuminance;
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

    private static float Ratio(float numerator, float denominator)
    {
        return denominator > 0.000001f ? numerator / denominator : 0.0f;
    }

    private static double CalculateDdgiTotalUpdateP95Milliseconds(SampleBenchmarkReport report)
    {
        return (FindGpuPass(report, "DdgiSchedulePass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiTracePass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiBlendPass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiRelocateClassifyPass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "DdgiPublishPass")?.P95Milliseconds ?? 0.0);
    }

    private static SampleBenchmarkTimingStats? FindGpuPass(SampleBenchmarkReport report, string name)
    {
        return report.GpuPasses.FirstOrDefault(pass => pass.Name.Equals(name, StringComparison.Ordinal));
    }
}
