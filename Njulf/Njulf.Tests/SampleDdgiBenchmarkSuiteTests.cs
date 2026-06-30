using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleDdgiBenchmarkSuiteTests
{
    private static readonly string[] DdgiSplitPasses =
    [
        "DdgiSchedulePass",
        "DdgiTracePass",
        "DdgiBlendPass",
        "DdgiRelocateClassifyPass",
        "DdgiPublishPass"
    ];

    [Test]
    public void Scenes_CoverProductionDdgiReadinessSet()
    {
        SampleBenchmarkSceneDescriptor[] scenes = SampleDdgiBenchmarkSuite.Scenes.ToArray();
        string[] names = scenes.Select(scene => scene.Name).ToArray();
        string[] distinctNames = names.Distinct(StringComparer.Ordinal).ToArray();
        string[] phase10Names = SampleGlobalIlluminationValidation.Phase10DeterministicScenes
            .Select(scene => scene.Name)
            .ToArray();
        string[] requiredNames = SampleDdgiBenchmarkSuite.RequiredProductionGateScenes
            .Select(scene => scene.Name)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("ddgi-open-plaza"));
            Assert.That(names, Does.Contain("ddgi-closed-room"));
            Assert.That(names, Does.Contain("ddgi-thin-wall"));
            Assert.That(names, Does.Contain("ddgi-long-corridor"));
            Assert.That(names, Does.Contain("ddgi-foliage-heavy"));
            Assert.That(names, Does.Contain("ddgi-moving-object"));
            Assert.That(names, Does.Contain("ddgi-moving-light"));
            Assert.That(names, Does.Contain("ddgi-emissive-material"));
            Assert.That(names, Does.Contain("ddgi-local-volume-streaming"));
            Assert.That(names, Does.Contain("ddgi-fast-traversal-teleport"));
            Assert.That(names, Does.Contain("ddgi-bright-exterior-room"));
            Assert.That(names, Is.SupersetOf(phase10Names));
            Assert.That(requiredNames, Is.SupersetOf(phase10Names));
            Assert.That(scenes.Select(scene => scene.Scenario), Does.Contain(SamplePerformanceScenario.GiBrightExteriorRoom));
            Assert.That(requiredNames, Has.Length.GreaterThanOrEqualTo(16));
            Assert.That(scenes.Select(scene => scene.Scenario), Has.All.Not.EqualTo(SamplePerformanceScenario.Normal));
            Assert.That(distinctNames, Has.Length.EqualTo(scenes.Length));
            Assert.That(scenes.Select(scene => scene.Coverage), Has.All.Not.Empty);
        });
    }

    [Test]
    public void ProductionGate_PassesForDdgiHighReportWithoutSsgiOrSteadyChurn()
    {
        SampleBenchmarkReport report = CreateGateReport(RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationRayQueryActive = 1,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            DdgiProbeVolumeCount = 6,
            DdgiCascadeCount = 4,
            DdgiProbesUpdated = 32,
            DdgiGatherTileCount = 8160,
            DdgiGatherSelectedClipmapTileCount = 8160,
            DdgiGatherFallbackTileCount = 0,
            DdgiAtlasMemoryBudgetBytes = 128UL * 1024UL * 1024UL,
            DdgiCurrentIrradianceAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiCurrentVisibilityAtlasBytes = 8UL * 1024UL * 1024UL,
            ProductionPipelineDeclaredPasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            ProductionPipelineActivePasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            Graph = CreateGraph([.. DdgiSplitPasses, "ForwardPlusPass"], [])
        });

        SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Passed, Is.True);
            Assert.That(gate.Failures, Is.Empty);
        });
    }

    [Test]
    public void ProductionGate_FailsWhenSsgiPassOrResourcesRemainInDdgiHigh()
    {
        SampleBenchmarkReport report = CreateGateReport(RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 1,
            GlobalIlluminationRayQueryActive = 1,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            SsgiRenderTargetBytes = 4096,
            SsgiWidth = 960,
            SsgiHeight = 540,
            SsgiRayCount = 6,
            DdgiProbeVolumeCount = 4,
            DdgiCascadeCount = 4,
            DdgiProbesUpdated = 16,
            DdgiGatherTileCount = 8160,
            DdgiGatherSelectedClipmapTileCount = 8160,
            DdgiGatherFallbackTileCount = 0,
            DdgiAtlasMemoryBudgetBytes = 128UL * 1024UL * 1024UL,
            DdgiCurrentIrradianceAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiCurrentVisibilityAtlasBytes = 8UL * 1024UL * 1024UL,
            ProductionPipelineDeclaredPasses = ["SsgiTracePass", .. DdgiSplitPasses],
            ProductionPipelineActivePasses = ["SsgiTracePass", .. DdgiSplitPasses],
            Graph = CreateGraph(["SsgiTracePass", .. DdgiSplitPasses], ["SsgiRaw"])
        }, gpuPasses:
        [
            new SampleBenchmarkTimingStats("SsgiTracePass", 4, 0.4, 0.3, 0.5, 0.5),
            new SampleBenchmarkTimingStats("DdgiSchedulePass", 4, 0.1, 0.1, 0.2, 0.2),
            new SampleBenchmarkTimingStats("DdgiTracePass", 4, 0.4, 0.3, 0.5, 0.5),
            new SampleBenchmarkTimingStats("DdgiBlendPass", 4, 0.1, 0.1, 0.2, 0.2),
            new SampleBenchmarkTimingStats("DdgiRelocateClassifyPass", 4, 0.1, 0.1, 0.1, 0.1),
            new SampleBenchmarkTimingStats("DdgiPublishPass", 4, 0.0, 0.0, 0.1, 0.1)
        ]);

        SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(report);
        string[] failures = gate.Failures.Select(failure => failure.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(gate.Passed, Is.False);
            Assert.That(failures, Does.Contain("ddgi-only-ray-query-active"));
            Assert.That(failures, Does.Contain("no-ssgi-resources"));
            Assert.That(failures, Does.Contain("no-ssgi-passes"));
        });
    }

    [Test]
    public void ProductionGate_FailsWhenDdgiGatherTilesFallback()
    {
        SampleBenchmarkReport report = CreateGateReport(RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationRayQueryActive = 1,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            DdgiProbeVolumeCount = 4,
            DdgiCascadeCount = 4,
            DdgiProbesUpdated = 16,
            DdgiGatherTileCount = 8160,
            DdgiGatherSelectedClipmapTileCount = 0,
            DdgiGatherFallbackTileCount = 8160,
            DdgiAtlasMemoryBudgetBytes = 128UL * 1024UL * 1024UL,
            DdgiCurrentIrradianceAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiCurrentVisibilityAtlasBytes = 8UL * 1024UL * 1024UL,
            ProductionPipelineDeclaredPasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            ProductionPipelineActivePasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            Graph = CreateGraph([.. DdgiSplitPasses, "ForwardPlusPass"], [])
        });

        SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Passed, Is.False);
            Assert.That(gate.Failures.Select(failure => failure.Name), Does.Contain("ddgi-gather-tiles-valid"));
        });
    }

    [Test]
    public void ProductionGate_FailsWhenPhase10MetricsCollapse()
    {
        SampleBenchmarkReport report = CreateGateReport(RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationRayQueryActive = 1,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            DdgiProbeVolumeCount = 4,
            DdgiCascadeCount = 3,
            DdgiProbesUpdated = 16,
            DdgiGatherTileCount = 8160,
            DdgiGatherSelectedClipmapTileCount = 8160,
            DdgiGatherFallbackTileCount = 0,
            DdgiAtlasMemoryBudgetBytes = 192UL * 1024UL * 1024UL,
            DdgiCurrentIrradianceAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiCurrentVisibilityAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiForwardEstimateCountersReadbackValid = 1,
            DdgiForwardEstimateSampleCount = 1000,
            DdgiForwardEstimateZeroVisibleButCoveredCount = 250,
            DdgiAverageCoverageEstimate = 0.9f,
            DdgiAverageVisibleSupportEstimate = 0.0f,
            DdgiAverageEffectiveContributionEstimate = 0.0f,
            DdgiForwardEstimateRawDiffuseLuminance = 0.2f,
            DdgiForwardEstimateFinalDiffuseLuminance = 0.0f,
            DdgiWarmupState = DdgiRuntimeWarmupState.ColdStart,
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.ColdStart,
            DdgiCacheGeneration = 0,
            DdgiSchedulerTimingSampleCount = 64,
            CpuDdgiSchedulerP95Microseconds = 600,
            GpuDdgiScheduleP95Microseconds = 800,
            DdgiSchedulerP95OverBudget = 1,
            GpuDdgiScheduleOverBudget = 1,
            DdgiTextureBytes = 0,
            DdgiBufferBytes = 0,
            DdgiGpuSchedulerBufferBytes = 0,
            ProductionPipelineDeclaredPasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            ProductionPipelineActivePasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            Graph = CreateGraph([.. DdgiSplitPasses, "ForwardPlusPass"], [])
        }, includeHealthyPhase10Diagnostics: false);

        SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(report);
        string[] failures = gate.Failures.Select(failure => failure.Name).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(gate.Passed, Is.False);
            Assert.That(failures, Does.Contain("phase10-forward-metrics-valid"));
            Assert.That(failures, Does.Contain("phase10-cache-warmup-steady"));
            Assert.That(failures, Does.Contain("phase10-scheduler-p95-budget"));
            Assert.That(failures, Does.Contain("phase10-ddgi-memory-diagnostics"));
        });
    }

    [Test]
    public void ProductionGate_FailsCpuGpuCompareWhenSchedulerEquivalenceBreaks()
    {
        SampleBenchmarkReport report = CreateGateReport(RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationRayQueryActive = 1,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            DdgiProbeVolumeCount = 4,
            DdgiCascadeCount = 3,
            DdgiProbesUpdated = 16,
            DdgiGatherTileCount = 8160,
            DdgiGatherSelectedClipmapTileCount = 8160,
            DdgiGatherFallbackTileCount = 0,
            DdgiAtlasMemoryBudgetBytes = 192UL * 1024UL * 1024UL,
            DdgiCurrentIrradianceAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiCurrentVisibilityAtlasBytes = 8UL * 1024UL * 1024UL,
            DdgiSchedulerMode = DdgiSchedulerMode.CpuGpuCompare,
            DdgiGpuSchedulerReadbackValid = 1,
            DdgiGpuSchedulerValidationValid = 0,
            DdgiGpuSchedulerValidationCpuRequestCount = 32,
            DdgiGpuSchedulerValidationGpuRequestCount = 31,
            DdgiGpuSchedulerValidationComparedRequestCount = 31,
            DdgiGpuSchedulerValidationMismatchCount = 1,
            DdgiGpuSchedulerValidationFirstMismatch = "priority mismatch",
            ProductionPipelineDeclaredPasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            ProductionPipelineActivePasses = [.. DdgiSplitPasses, "ForwardPlusPass"],
            Graph = CreateGraph([.. DdgiSplitPasses, "ForwardPlusPass"], [])
        });

        SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Passed, Is.False);
            Assert.That(gate.Failures.Select(failure => failure.Name), Does.Contain("phase10-scheduler-equivalence"));
        });
    }

    private static SampleBenchmarkReport CreateGateReport(
        RendererDiagnostics diagnostics,
        IReadOnlyList<SampleBenchmarkTimingStats>? gpuPasses = null,
        bool includeHealthyPhase10Diagnostics = true)
    {
        if (includeHealthyPhase10Diagnostics)
            diagnostics = WithHealthyPhase10Diagnostics(diagnostics);

        gpuPasses ??=
        [
            new SampleBenchmarkTimingStats("DdgiSchedulePass", 4, 0.1, 0.1, 0.2, 0.2),
            new SampleBenchmarkTimingStats("DdgiTracePass", 4, 0.7, 0.6, 0.8, 0.8),
            new SampleBenchmarkTimingStats("DdgiBlendPass", 4, 0.2, 0.1, 0.3, 0.3),
            new SampleBenchmarkTimingStats("DdgiRelocateClassifyPass", 4, 0.1, 0.1, 0.1, 0.1),
            new SampleBenchmarkTimingStats("DdgiPublishPass", 4, 0.0, 0.0, 0.0, 0.0)
        ];
        return new SampleBenchmarkReport(
            "njulf-renderer-benchmark",
            DateTimeOffset.UtcNow,
            new SampleBenchmarkOptions(Enabled: true, WarmupFrameCount: 8, MeasureFrameCount: 4, ReportPath: null),
            SamplePerformanceScenario.GiCornellRoom,
            WarmupFrameCount: 8,
            MeasurementFrameCount: 4,
            FirstMeasurementFrameIndex: 8,
            LastMeasurementFrameIndex: 11,
            CpuFrameMilliseconds: new SampleBenchmarkTimingStats("CPU frame", 4, 5.0, 4.0, 6.0, 6.0),
            GpuFrameMilliseconds: new SampleBenchmarkTimingStats("GPU frame", 4, 7.0, 6.0, 8.0, 8.0),
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: 4,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: gpuPasses,
            CpuStages: [],
            Findings: [],
            BudgetMetrics:
            [
                new BudgetMetric("GI GPU", 1.4, 5.1, 6.0, "ms", RenderBudgetStatus.WithinBudget)
            ],
            LastDiagnostics: diagnostics);
    }

    private static RendererDiagnostics WithHealthyPhase10Diagnostics(RendererDiagnostics diagnostics)
    {
        return diagnostics with
        {
            DdgiForwardEstimateCountersReadbackValid = 1,
            DdgiForwardEstimateSampleCount = 1000,
            DdgiForwardEstimateZeroVisibleButCoveredCount = 10,
            DdgiAverageCoverageEstimate = 0.82f,
            DdgiAverageVisibleSupportEstimate = 0.74f,
            DdgiAverageEffectiveContributionEstimate = 0.61f,
            DdgiForwardEstimateRawDiffuseLuminance = 0.42f,
            DdgiForwardEstimateFinalDiffuseLuminance = 0.38f,
            DdgiWarmupState = DdgiRuntimeWarmupState.SteadyState,
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.SteadyState,
            DdgiCacheGeneration = 3,
            DdgiSchedulerTimingSampleCount = 64,
            CpuDdgiSchedulerP95Microseconds = 180,
            GpuDdgiScheduleP95Microseconds = 190,
            DdgiSchedulerP95OverBudget = 0,
            GpuDdgiScheduleOverBudget = 0,
            DdgiTextureBytes = 16UL * 1024UL * 1024UL,
            DdgiBufferBytes = 4UL * 1024UL * 1024UL,
            DdgiGpuSchedulerBufferBytes = 1UL * 1024UL * 1024UL,
            DdgiGpuSchedulerReadbackValid = diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && diagnostics.DdgiGpuSchedulerReadbackValid == 0 ? 1 : diagnostics.DdgiGpuSchedulerReadbackValid,
            DdgiGpuSchedulerValidationValid = diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && diagnostics.DdgiGpuSchedulerValidationMismatchCount == 0 ? 1 : diagnostics.DdgiGpuSchedulerValidationValid,
            DdgiGpuSchedulerValidationCpuRequestCount = diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && diagnostics.DdgiGpuSchedulerValidationCpuRequestCount == 0 ? 32 : diagnostics.DdgiGpuSchedulerValidationCpuRequestCount,
            DdgiGpuSchedulerValidationGpuRequestCount = diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && diagnostics.DdgiGpuSchedulerValidationGpuRequestCount == 0 ? 32u : diagnostics.DdgiGpuSchedulerValidationGpuRequestCount,
            DdgiGpuSchedulerValidationComparedRequestCount = diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && diagnostics.DdgiGpuSchedulerValidationComparedRequestCount == 0 ? 32 : diagnostics.DdgiGpuSchedulerValidationComparedRequestCount
        };
    }

    private static RenderGraphDiagnostics CreateGraph(IReadOnlyList<string> passes, IReadOnlyList<string> resources)
    {
        return new RenderGraphDiagnostics(
            resources.Count,
            passes.Count,
            PlannedBarrierCount: 0,
            ExecutedBarrierCount: 0,
            TransientResourceCount: 0,
            PersistentResourceCount: resources.Count,
            AliasableResourceCount: 0,
            ImportedResourceCount: 0,
            OwnedRenderTargetCount: 0,
            AsyncComputeCandidatePassCount: 0,
            AsyncComputeEnabledPassCount: 0,
            QueueOwnershipTransitionCount: 0,
            ResourceMemoryEstimateBytes: 0,
            Resources: resources.Select(resource => new RenderGraphResourceDiagnostics(resource, resource, "Image", "R16G16B16A16Sfloat", "Full", "Persistent", true, true, 1, 0)).ToArray(),
            Passes: passes.Select(pass => new RenderGraphPassDiagnostics(pass, true, "Graphics", false, false, string.Empty, [], [], [])).ToArray(),
            Barriers: []);
    }
}
