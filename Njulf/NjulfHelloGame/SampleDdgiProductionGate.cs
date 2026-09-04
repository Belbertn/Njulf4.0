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
    public const double SimpleDdgiTransportBlendP95BudgetMilliseconds = 2.25;
    public const double SimpleDdgiUploadP95BudgetMilliseconds = 2.0;
    public const double SimpleDdgiCapacityP95BudgetMilliseconds = 0.1;
    public const double SimpleDdgiPagingP95BudgetMilliseconds = 0.20;
    public const double MaximumTrackedMemoryBudgetFraction = 0.80;
    public const float MinimumPhase10CoverageMean = 0.25f;
    public const float MinimumPhase10VisibleSupportMean = 0.05f;
    public const float MinimumPhase10EffectiveWeightMean = 0.02f;
    public const float MaximumPhase10ZeroVisibleCoveredFraction = 0.001f;
    public const float MinimumPhase9HealthyRawDiffuseLuminance = 0.05f;
    public const float MinimumPhase9HealthyFinalDiffuseLuminance = 0.015f;
    public const float MinimumPhase9FinalToRawLuminanceRatio = 0.25f;
    public const float MaximumPhase9FallbackWeightForHealthyDdgi = 1.0f;
    public const float MinimumPhase9EmissiveBounceLuminance = 0.01f;
    public const float MaximumPhase9ThinWallLeakAttenuation = 0.98f;
    public const float WarmupCompletionTarget = 0.80f;

    private static readonly HashSet<SamplePerformanceScenario> RequiredScenarios =
        SampleDdgiBenchmarkSuite.RequiredProductionGateScenes
            .Select(scene => scene.Scenario)
            .ToHashSet();

    public static SampleDdgiProductionGateReport Evaluate(SampleBenchmarkReport report)
    {
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        RendererDiagnostics diagnostics = report.LastDiagnostics ?? RendererDiagnostics.Empty;
        SampleBenchmarkTimingStats? simpleDdgiTracePass = FindGpuPass(report, "SimpleDdgiTracePass");
        SampleBenchmarkTimingStats? simpleDdgiPageDemandPass = FindGpuPass(report, "SimpleDdgiPageDemandPass");
        SampleBenchmarkTimingStats? simpleDdgiPageResidencyPass = FindGpuPass(report, "SimpleDdgiPageResidencyPass");
        SampleBenchmarkTimingStats? simpleDdgiPageFeedbackPass = FindGpuPass(report, "SimpleDdgiPageFeedbackPass");
        SampleBenchmarkTimingStats? simpleDdgiTransportPass = FindGpuPass(report, "SimpleDdgiTransportPass");
        SampleBenchmarkTimingStats? simpleDdgiBlendPass = FindGpuPass(report, "SimpleDdgiBlendPass");
        SampleBenchmarkTimingStats? simpleDdgiRelocateClassifyPass = FindGpuPass(report, "SimpleDdgiRelocateClassifyPass");
        SampleBenchmarkTimingStats? simpleDdgiPublishPass = FindGpuPass(report, "SimpleDdgiPublishPass");
        bool simpleDdgiActive = diagnostics.SimpleDdgiActive != 0;
        bool movingTrajectory =
            SampleBenchmarkTrajectory.IsMoving(report.Options.Trajectory);
        bool authenticatedMovingTrajectory =
            IsAuthenticatedMovingTrajectory(report);
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
                diagnostics.GlobalIlluminationRayQueryActive != 0,
                $"enabled={diagnostics.GlobalIlluminationEnabled}, mode={diagnostics.GlobalIlluminationMode}, ddgi={diagnostics.GlobalIlluminationDdgiActive}, rayQuery={diagnostics.GlobalIlluminationRayQueryActive}"),
            Criterion(
                "ddgi-split-passes-present",
                diagnostics.DdgiProbesUpdated <= 0 ||
                (simpleDdgiTracePass != null &&
                 simpleDdgiTransportPass != null &&
                 simpleDdgiBlendPass != null &&
                 simpleDdgiRelocateClassifyPass != null &&
                 simpleDdgiPublishPass != null),
                $"simple={simpleDdgiActive}, trace={simpleDdgiTracePass != null}, " +
                $"transport={simpleDdgiTransportPass != null}, blend={simpleDdgiBlendPass != null}, " +
                $"relocateClassify={simpleDdgiRelocateClassifyPass != null}, publish={simpleDdgiPublishPass != null}"),
            Criterion(
                "no-recursive-ddgi-copy",
                diagnostics.DdgiRayScratchBytes == 0 ||
                diagnostics.DdgiUpdatedAtlasBytes > 0,
                $"updates={diagnostics.DdgiProbesUpdated}, rayScratchBytes={diagnostics.DdgiRayScratchBytes}, updatedAtlasBytes={diagnostics.DdgiUpdatedAtlasBytes}, latencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}, publishExec={diagnostics.DdgiPublishExecuted}, publishSkip='{diagnostics.DdgiPublishSkipReason}'"),
            Criterion(
                "ddgi-async-compute-state-consistent",
                diagnostics.DdgiAsyncComputeEnabled == 0 ||
                diagnostics.AsyncComputeEnabled != 0,
                $"optional=true, async={diagnostics.DdgiAsyncComputeEnabled}, rendererAsync={diagnostics.AsyncComputeEnabled}, requested={diagnostics.AsyncComputeRequested}, supported={diagnostics.AsyncComputeSupported}, latencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}"),
            Criterion(
                "no-static-frame-full-as-rebuild",
                !IsStaticScene(report.Scenario) ||
                (diagnostics.AccelerationStructureBlasBuildCount == 0 &&
                 diagnostics.AccelerationStructureTlasBuildCount == 0),
                $"scenario={report.Scenario}, blasBuilds={diagnostics.AccelerationStructureBlasBuildCount}, tlasBuilds={diagnostics.AccelerationStructureTlasBuildCount}"),
            Criterion(
                "blas-compaction-settled-and-lossless",
                diagnostics.GlobalIlluminationRayQueryActive == 0 ||
                (diagnostics.AccelerationStructureBlasCompactionPendingCount == 0 &&
                 diagnostics.AccelerationStructureBlasCompactionQueryOverflowCount == 0 &&
                 diagnostics.AccelerationStructureBlasCompactionQueryReadbackFailureCount == 0 &&
                 diagnostics.AccelerationStructureRetiredBytes == 0),
                $"savedResident={diagnostics.AccelerationStructureBlasCompactedResidentBytesSaved}, " +
                $"pending={diagnostics.AccelerationStructureBlasCompactionPendingCount}, " +
                $"queryOverflow={diagnostics.AccelerationStructureBlasCompactionQueryOverflowCount}, " +
                $"readbackFailure={diagnostics.AccelerationStructureBlasCompactionQueryReadbackFailureCount}, " +
                $"retiredBytes={diagnostics.AccelerationStructureRetiredBytes}"),
            Criterion(
                "ddgi-ray-query-scene-complete",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                (diagnostics.GlobalIlluminationRayQueryActive != 0 &&
                 diagnostics.AccelerationStructureTopLevelInstanceCount > 0 &&
                 diagnostics.AccelerationStructureBlasBudgetRejectedCount == 0 &&
                 diagnostics.AccelerationStructureTopLevelInstanceCount >= diagnostics.AccelerationStructureStaticInstanceResidentCount &&
                 string.IsNullOrWhiteSpace(diagnostics.AccelerationStructureFallbackReason)),
                $"rayQuery={diagnostics.GlobalIlluminationRayQueryActive}, tlasInstances={diagnostics.AccelerationStructureTopLevelInstanceCount}, staticResident={diagnostics.AccelerationStructureStaticInstanceResidentCount}, blasRejected={diagnostics.AccelerationStructureBlasBudgetRejectedCount}, fallback='{diagnostics.AccelerationStructureFallbackReason}'"),
            Criterion(
                "ddgi-static-ray-coverage-complete",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                diagnostics.AccelerationStructureStaticInstanceCulledCount == 0 ||
                (diagnostics.FarFieldPagedMode != 0 &&
                 diagnostics.FarFieldPagePoolCapacity > 0 &&
                 diagnostics.FarFieldResidentPageCount > 0 &&
                 diagnostics.FarFieldPendingPageCount == 0),
                $"staticCandidates={diagnostics.AccelerationStructureStaticInstanceCandidateCount}, staticResident={diagnostics.AccelerationStructureStaticInstanceResidentCount}, staticCulled={diagnostics.AccelerationStructureStaticInstanceCulledCount}, farFieldMode={diagnostics.FarFieldPagedMode}, farFieldPool={diagnostics.FarFieldPagePoolCapacity}, residentPages={diagnostics.FarFieldResidentPageCount}, pendingPages={diagnostics.FarFieldPendingPageCount}"),
            Criterion(
                "requested-paged-far-field-active",
                diagnostics.FarFieldPagedFeatureEnabled == 0 ||
                (diagnostics.FarFieldPagedMode != 0 &&
                 diagnostics.FarFieldPagePoolCapacity > 0 &&
                 diagnostics.FarFieldResidentPageCount > 0 &&
                 diagnostics.FarFieldPendingPageCount == 0),
                $"requested={diagnostics.FarFieldPagedFeatureEnabled}, active={diagnostics.FarFieldPagedMode}, pagePool={diagnostics.FarFieldPagePoolCapacity}, residentPages={diagnostics.FarFieldResidentPageCount}, pendingPages={diagnostics.FarFieldPendingPageCount}"),
            Criterion(
                "clipmaps-preserved-with-authored-volumes",
                simpleDdgiActive ||
                diagnostics.DdgiProbeVolumeCount <= diagnostics.DdgiCascadeCount ||
                diagnostics.DdgiCascadeCount > 0,
                $"simple={simpleDdgiActive}, volumes={diagnostics.DdgiProbeVolumeCount}, cascades={diagnostics.DdgiCascadeCount}"),
            Criterion(
                "phase10-forward-metrics-valid",
                IsPhase10ForwardMetricsHealthy(diagnostics),
                $"detailedCompiled={diagnostics.DdgiDetailedCountersCompiled}, readback={diagnostics.DdgiForwardEstimateCountersReadbackValid}, spatial={diagnostics.DdgiAverageSpatialCoverageEstimate:F3}, support={diagnostics.DdgiAverageSupportCoverageEstimate:F3}, data={diagnostics.DdgiAverageDataConfidenceEstimate:F3}, visibility={diagnostics.DdgiAverageVisibilityConfidenceEstimate:F3}, effective={diagnostics.DdgiAverageEffectiveContributionEstimate:F3}, zeroSupportSpatial={GetZeroVisibleCoveredFraction(diagnostics):F3}, sampledIrrLuma={diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F3}, ddgiDiffuseLuma={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}, hybridFinalLuma={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}"),
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
                IsPhase10CacheWarmupReady(
                    diagnostics,
                    movingTrajectory,
                    authenticatedMovingTrajectory),
                $"trajectory={report.Options.Trajectory}, authenticatedMoving={authenticatedMovingTrajectory}, cacheGeneration={diagnostics.DdgiCacheGeneration}, warmup={diagnostics.DdgiWarmupState}, cacheWarmup={diagnostics.DdgiCacheWarmupState}, stableKey={diagnostics.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit}, transitions={diagnostics.SimpleDdgiUploadTiming.CapacityDetails.TransitionCount}"),
            Criterion(
                "phase10-warmup-progress-valid",
                IsPhase10WarmupProgressValid(
                    diagnostics,
                    movingTrajectory,
                    authenticatedMovingTrajectory),
                $"trajectory={report.Options.Trajectory}, authenticatedMoving={authenticatedMovingTrajectory}, warmup={diagnostics.DdgiWarmupState}, visible/local/cascade0={diagnostics.DdgiWarmedVisibleProbeFraction:F3}/{diagnostics.DdgiWarmedLocalProbeFraction:F3}/{diagnostics.DdgiWarmedCascade0ProbeFraction:F3}"),
            Criterion(
                "simple-ddgi-probe-lifecycle-bounded",
                IsSimpleDdgiProbeLifecycleBounded(diagnostics),
                $"target={diagnostics.SimpleDdgiProbeLifecycleLatencyTargetFrames}, oldestUnsupported={diagnostics.SimpleDdgiOldestVisibleUnsupportedProbeAge}, overTarget={diagnostics.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget}, maxFresh={diagnostics.SimpleDdgiMaximumFreshProbeAge}, maxScroll={diagnostics.SimpleDdgiMaximumScrollExposedProbeAge}, maxRelocation={diagnostics.SimpleDdgiMaximumRelocationPendingProbeAge}, maxUnpublished={diagnostics.SimpleDdgiMaximumUnpublishedProbeAge}, findings={diagnostics.SimpleDdgiProbeLifecycleBoundExceededCount}"),
            Criterion(
                "gpu-timing-valid",
                report.GpuTimingSupported != 0 &&
                report.GpuTimingValidSampleCount > 0 &&
                report.GpuTimingValidSampleCount >= Math.Max(1, report.MeasurementFrameCount),
                $"supported={report.GpuTimingSupported}, validSamples={report.GpuTimingValidSampleCount}, measured={report.MeasurementFrameCount}, reason={report.GpuTimingUnavailableReason}"),
            Criterion(
                "simple-ddgi-transport-blend-p95-budget",
                IsSimpleDdgiTransportBlendWithinBudget(report, diagnostics),
                $"active={diagnostics.SimpleDdgiActive}, p95={CalculateSimpleDdgiTransportBlendP95Milliseconds(report):F3}ms, budget={SimpleDdgiTransportBlendP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "simple-ddgi-paging-p95-budget",
                !diagnostics.SimpleDdgiProbeResidency.Mode.CollectsDemand() ||
                (simpleDdgiPageDemandPass != null &&
                 simpleDdgiPageResidencyPass != null &&
                 simpleDdgiPageFeedbackPass != null &&
                 simpleDdgiPageDemandPass.P95Milliseconds +
                     simpleDdgiPageResidencyPass.P95Milliseconds <=
                     SimpleDdgiPagingP95BudgetMilliseconds),
                $"mode={diagnostics.SimpleDdgiProbeResidency.Mode}, " +
                $"demandP95={simpleDdgiPageDemandPass?.P95Milliseconds:F3}ms, " +
                $"residencyP95={simpleDdgiPageResidencyPass?.P95Milliseconds:F3}ms, " +
                $"feedbackP95={simpleDdgiPageFeedbackPass?.P95Milliseconds:F3}ms, " +
                $"demandPlusResidencyBudget={SimpleDdgiPagingP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "simple-ddgi-upload-p95-budget",
                IsSimpleDdgiCpuStageWithinBudget(
                    report,
                    diagnostics,
                    "SimpleDdgiUpload",
                    SimpleDdgiUploadP95BudgetMilliseconds),
                $"active={diagnostics.SimpleDdgiActive}, p95={FindCpuStage(report, "SimpleDdgiUpload")?.P95Milliseconds:F3}ms, budget={SimpleDdgiUploadP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "simple-ddgi-capacity-p95-budget",
                IsSimpleDdgiCpuStageWithinBudget(
                    report,
                    diagnostics,
                    "SimpleDdgiUpload.Capacity",
                    SimpleDdgiCapacityP95BudgetMilliseconds),
                $"active={diagnostics.SimpleDdgiActive}, stableKey={diagnostics.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit}, p95={FindCpuStage(report, "SimpleDdgiUpload.Capacity")?.P95Milliseconds:F3}ms, budget={SimpleDdgiCapacityP95BudgetMilliseconds:F3}ms"),
            Criterion(
                "simple-ddgi-transport-settled",
                IsSimpleDdgiTransportQualified(
                    diagnostics,
                    movingTrajectory,
                    authenticatedMovingTrajectory),
                $"trajectory={report.Options.Trajectory}, authenticatedMoving={authenticatedMovingTrajectory}, " +
                CreateSimpleDdgiTransportSettlementDetail(diagnostics)),
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
                "phase10-ddgi-memory-diagnostics",
                diagnostics.GlobalIlluminationDdgiActive == 0 ||
                diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes > 0,
                $"textureBytes={diagnostics.DdgiTextureBytes}, bufferBytes={diagnostics.DdgiBufferBytes}, atlasBytes={diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes}"),
            Criterion(
                "tracked-memory-headroom-20-percent",
                HasRequiredTrackedMemoryHeadroom(diagnostics),
                $"tracked={diagnostics.TrackedGpuMemoryBytes}, budget={diagnostics.GpuMemoryBudgetBytes}, utilization={CalculateTrackedMemoryBudgetFraction(diagnostics):P2}, requiredMaximum={MaximumTrackedMemoryBudgetFraction:P0}"),
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

    public static ulong GetDdgiAtlasMemoryBudgetBytes(DdgiQualityTier tier)
    {
        return tier switch
        {
            DdgiQualityTier.DdgiLow => 64UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiMedium => 128UL * 1024UL * 1024UL,
            DdgiQualityTier.DdgiUltra => 384UL * 1024UL * 1024UL,
            _ => 288UL * 1024UL * 1024UL
        };
    }

    private static SampleDdgiProductionGateCriterion Criterion(string name, bool passed, string detail) =>
        new(name, passed, detail);

    private static bool IsStaticScene(SamplePerformanceScenario scenario)
    {
        return scenario is not SamplePerformanceScenario.GiMovingPointLight
            and not SamplePerformanceScenario.GiMovingRigidObject
            and not SamplePerformanceScenario.ForestFoliage;
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
        if (diagnostics.DdgiDetailedCountersCompiled == 0)
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
            GetZeroVisibleCoveredFraction(diagnostics) < MaximumPhase10ZeroVisibleCoveredFraction;
    }

    private static bool IsPhase9RawAtlasToFinalEnergyHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;
        if (diagnostics.DdgiDetailedCountersCompiled == 0)
            return true;
        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return false;

        float rawDiffuseLuminance = Math.Max(diagnostics.DdgiForwardEstimateRawDiffuseLuminance, 0.0f);
        float finalDiffuseLuminance = Math.Max(diagnostics.DdgiForwardEstimateFinalDiffuseLuminance, 0.0f);

        // Atlas and forward metrics average different populations. A bright global probe field can
        // legitimately land on dark receivers, so absolute atlas/receiver luminance is not evidence
        // of composition loss. Once raw receiver energy is measurable, final/raw is the invariant
        // that diagnoses suppression between the raw DDGI term and the composed result.
        if (rawDiffuseLuminance <= 0.000001f)
            return true;

        return Ratio(finalDiffuseLuminance, rawDiffuseLuminance) >= MinimumPhase9FinalToRawLuminanceRatio &&
            diagnostics.DdgiAverageEffectiveContributionEstimate >= MinimumPhase10EffectiveWeightMean;
    }

    private static bool IsPhase9FallbackHealthy(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;
        if (diagnostics.DdgiDetailedCountersCompiled == 0)
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
        if (diagnostics.DdgiDetailedCountersCompiled == 0)
            return true;

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
        if (diagnostics.DdgiDetailedCountersCompiled == 0)
            return true;

        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return false;

        return diagnostics.DdgiAverageLeakAttenuationEstimate <= MaximumPhase9ThinWallLeakAttenuation ||
            diagnostics.DdgiForwardEstimateFinalDiffuseLuminance < MinimumPhase9HealthyFinalDiffuseLuminance;
    }

    internal static bool IsPhase10CacheWarmupReady(
        RendererDiagnostics diagnostics,
        bool movingTrajectory,
        bool authenticatedMovingTrajectory)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        if (movingTrajectory)
        {
            return authenticatedMovingTrajectory &&
                diagnostics.DdgiCacheGeneration > 0 &&
                IsDynamicWarmupState(diagnostics.DdgiWarmupState) &&
                IsDynamicWarmupState(diagnostics.DdgiCacheWarmupState) &&
                diagnostics.SimpleDdgiUploadTiming.CapacityDetails.StableKeyHit &&
                diagnostics.SimpleDdgiUploadTiming.CapacityDetails.TransitionCount == 0;
        }

        return diagnostics.DdgiCacheGeneration > 0 &&
            diagnostics.DdgiWarmupState == DdgiRuntimeWarmupState.SteadyState &&
            diagnostics.DdgiCacheWarmupState == DdgiRuntimeWarmupState.SteadyState;
    }

    internal static bool IsPhase10WarmupProgressValid(
        RendererDiagnostics diagnostics,
        bool movingTrajectory,
        bool authenticatedMovingTrajectory)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        bool validState = movingTrajectory
            ? authenticatedMovingTrajectory &&
                IsDynamicWarmupState(diagnostics.DdgiWarmupState)
            : diagnostics.DdgiWarmupState == DdgiRuntimeWarmupState.SteadyState;
        return validState &&
            diagnostics.DdgiWarmedVisibleProbeFraction >= WarmupCompletionTarget &&
            diagnostics.DdgiWarmedLocalProbeFraction >= WarmupCompletionTarget &&
            diagnostics.DdgiWarmedCascade0ProbeFraction >= WarmupCompletionTarget;
    }

    private static bool IsDynamicWarmupState(DdgiRuntimeWarmupState state) =>
        state is DdgiRuntimeWarmupState.LocalVolumeWarmup or
            DdgiRuntimeWarmupState.NearCascadeWarmup or
            DdgiRuntimeWarmupState.SteadyState;

    internal static bool IsAuthenticatedMovingTrajectory(
        SampleBenchmarkReport report)
    {
        SampleBenchmarkTrajectoryKind trajectory = report.Options.Trajectory;
        SampleBenchmarkCaptureContract capture = report.CaptureContract;
        return SampleBenchmarkTrajectory.IsMoving(trajectory) &&
            capture.Comparable &&
            capture.ProductionTiming &&
            capture.Mismatches.Count == 0 &&
            capture.TrajectoryFrameCount == report.MeasurementFrameCount &&
            string.Equals(
                capture.Trajectory,
                SampleBenchmarkTrajectory.GetName(trajectory),
                StringComparison.Ordinal) &&
            string.Equals(
                capture.TrajectoryFingerprint,
                report.Options.TrajectoryFingerprint,
                StringComparison.Ordinal);
    }

    private static bool IsSimpleDdgiProbeLifecycleBounded(RendererDiagnostics diagnostics)
    {
        if (diagnostics.GlobalIlluminationDdgiActive == 0)
            return true;

        return diagnostics.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget == 0 &&
            diagnostics.SimpleDdgiProbeLifecycleBoundExceededCount == 0;
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

    private static bool IsSimpleDdgiTransportBlendWithinBudget(
        SampleBenchmarkReport report,
        RendererDiagnostics diagnostics)
    {
        if (diagnostics.SimpleDdgiActive == 0)
            return true;

        return report.SimpleDdgiTransportBlendMilliseconds.Count > 0 &&
            CalculateSimpleDdgiTransportBlendP95Milliseconds(report) <=
                SimpleDdgiTransportBlendP95BudgetMilliseconds;
    }

    private static double CalculateSimpleDdgiTransportBlendP95Milliseconds(
        SampleBenchmarkReport report)
    {
        if (report.SimpleDdgiTransportBlendMilliseconds.Count > 0)
            return report.SimpleDdgiTransportBlendMilliseconds.P95Milliseconds;

        // Compatibility for reports written before the grouped distribution was
        // introduced. New reports always use the real per-frame combined P95.
        return (FindGpuPass(report, "SimpleDdgiTransportPass")?.P95Milliseconds ?? 0.0) +
            (FindGpuPass(report, "SimpleDdgiBlendPass")?.P95Milliseconds ?? 0.0);
    }

    private static bool IsSimpleDdgiCpuStageWithinBudget(
        SampleBenchmarkReport report,
        RendererDiagnostics diagnostics,
        string stageName,
        double budgetMilliseconds)
    {
        if (diagnostics.SimpleDdgiActive == 0)
            return true;

        SampleBenchmarkTimingStats? stage = FindCpuStage(report, stageName);
        return stage != null &&
            stage.Count >= Math.Max(1, report.MeasurementFrameCount) &&
            stage.P95Milliseconds <= budgetMilliseconds;
    }

    internal static bool IsSimpleDdgiTransportQualified(
        RendererDiagnostics diagnostics,
        bool movingTrajectory,
        bool authenticatedMovingTrajectory)
    {
        if (diagnostics.SimpleDdgiActive == 0 ||
            diagnostics.SimpleDdgiTransportV2Active == 0)
        {
            return true;
        }

        if (movingTrajectory)
        {
            SimpleDdgiCapacityTiming capacity =
                diagnostics.SimpleDdgiUploadTiming.CapacityDetails;
            return authenticatedMovingTrajectory &&
                diagnostics.GpuTimingValid != 0 &&
                capacity.StableKeyHit &&
                capacity.TransitionCount == 0 &&
                IsSimpleDdgiProbeLifecycleBounded(diagnostics);
        }

        SimpleDdgiTransportConvergenceTelemetry convergence =
            diagnostics.SimpleDdgiTransportConvergence;
        if (diagnostics.SimpleDdgiTransportTailCertificationEnabled)
        {
            return SampleBenchmarkRunner
                .HasAcceptedCurrentSimpleDdgiTailCertificate(diagnostics);
        }

        // A scheduled refresh and its bounded propagation neighborhood are valid
        // settled-state maintenance; urgent repair and unexplained pending work
        // are not. The benchmark helper applies that exact population contract.
        return diagnostics.SimpleDdgiTransportGlobalConvergencePending == 0 &&
            SampleBenchmarkRunner.HasSourceReadySimpleDdgiTransportPopulation(
                diagnostics) &&
            convergence.NoOpDispatchLaneCount <=
                (ulong)Math.Max(0, convergence.DispatchBatchCount) * 63UL;
    }

    private static string CreateSimpleDdgiTransportSettlementDetail(
        RendererDiagnostics diagnostics)
    {
        SimpleDdgiTransportConvergenceTelemetry convergence =
            diagnostics.SimpleDdgiTransportConvergence;
        int sourceReady = Math.Max(
            0,
            convergence.ParticipatingProbeCount - convergence.SourceRepairProbeCount);
        int qualified = Math.Min(
            Math.Max(0, convergence.ParticipatingProbeCount),
            Math.Max(0, convergence.ConvergedProbeCount) +
                Math.Max(0, convergence.RoutineSourceRepairProbeCount) +
                Math.Max(0, convergence.RoutineMaintenancePendingProbeCount));
        double qualifiedFraction = convergence.ParticipatingProbeCount > 0
            ? qualified / (double)convergence.ParticipatingProbeCount
            : 1.0;
        return $"active={diagnostics.SimpleDdgiActive}, v2={diagnostics.SimpleDdgiTransportV2Active}, " +
            $"tailEnabled={(diagnostics.SimpleDdgiTransportTailCertificationEnabled ? 1 : 0)}, " +
            $"tailCurrent={(convergence.TailCertificateCurrent ? 1 : 0)}, " +
            $"tailAudit={(convergence.TailAuditComplete ? 1 : 0)}, " +
            $"tailParticipants={convergence.TailAuditedParticipantCount}/{convergence.TailExpectedParticipantCount}, " +
            $"tailTexels={convergence.TailAuditedTexelCount}/{convergence.TailExpectedTexelCount}, " +
            $"readback={convergence.ReadbackValid}, globalPending={diagnostics.SimpleDdgiTransportGlobalConvergencePending}, " +
            $"sourceReady={sourceReady}, converged={convergence.ConvergedProbeCount}, " +
            $"routineSource={convergence.RoutineSourceRepairProbeCount}, " +
            $"routinePropagation={convergence.RoutineMaintenancePendingProbeCount}, " +
            $"qualified={qualified} ({qualifiedFraction:P2}), " +
            $"pending={convergence.PendingConvergenceProbeCount}, " +
            $"noOpLanes={convergence.NoOpDispatchLaneCount}, batches={convergence.DispatchBatchCount}";
    }

    private static bool HasRequiredTrackedMemoryHeadroom(RendererDiagnostics diagnostics)
    {
        return diagnostics.GpuMemoryBudgetBytes > 0 &&
            diagnostics.TrackedGpuMemoryBytes <=
                (decimal)diagnostics.GpuMemoryBudgetBytes *
                (decimal)MaximumTrackedMemoryBudgetFraction;
    }

    private static double CalculateTrackedMemoryBudgetFraction(RendererDiagnostics diagnostics)
    {
        return diagnostics.GpuMemoryBudgetBytes > 0
            ? diagnostics.TrackedGpuMemoryBytes /
                (double)diagnostics.GpuMemoryBudgetBytes
            : double.PositiveInfinity;
    }

    private static SampleBenchmarkTimingStats? FindGpuPass(SampleBenchmarkReport report, string name)
    {
        return report.GpuPasses.FirstOrDefault(pass => pass.Name.Equals(name, StringComparison.Ordinal));
    }

    private static SampleBenchmarkTimingStats? FindCpuStage(SampleBenchmarkReport report, string name)
    {
        return report.CpuStages.FirstOrDefault(stage =>
            stage.Name.Equals(name, StringComparison.Ordinal));
    }
}
