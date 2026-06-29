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
            Assert.That(scenes.Select(scene => scene.Scenario), Does.Contain(SamplePerformanceScenario.GiBrightExteriorRoom));
            Assert.That(requiredNames, Has.Length.EqualTo(10));
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

    private static SampleBenchmarkReport CreateGateReport(
        RendererDiagnostics diagnostics,
        IReadOnlyList<SampleBenchmarkTimingStats>? gpuPasses = null)
    {
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
