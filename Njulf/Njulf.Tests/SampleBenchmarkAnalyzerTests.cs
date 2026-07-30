using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkAnalyzerTests
{
    [Test]
    public void CreateReport_RanksSlowestGpuPassAndBudgetFindings()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var budget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GPU frame",
                    Value: 17.0,
                    WarningThreshold: 13.6,
                    FailureThreshold: 16.0,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget)
            ],
            OverallStatus = RenderBudgetStatus.OverBudget
        };

        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_000,
            GpuFrameMicroseconds = 7_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuForwardOpaqueMicroseconds = 5_000,
            GpuBloomUpsampleMicroseconds = 1_000
        }, budget);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_500,
            GpuFrameMicroseconds = 8_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuForwardOpaqueMicroseconds = 6_000,
            GpuBloomUpsampleMicroseconds = 1_500
        }, budget);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 1, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 1,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 1,
            lastMeasurementFrameIndex: 2);

        Assert.Multiple(() =>
        {
            Assert.That(report.GpuFrameMilliseconds.Count, Is.EqualTo(2));
            Assert.That(report.GpuPasses[0].Name, Is.EqualTo("ForwardPlusPass"));
            Assert.That(report.GpuPasses[0].P95Milliseconds, Is.EqualTo(6.0));
            Assert.That(report.Findings.First().Subject, Is.EqualTo("ForwardPlusPass"));
            Assert.That(report.Findings.Any(finding => finding.Category == "budget" && finding.Subject == "GPU frame"), Is.True);
        });
    }

    [Test]
    public void CreateReport_IncludesGlobalIlluminationGpuPasses()
    {
        var analyzer = new SampleBenchmarkAnalyzer();

        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_000,
            GpuFrameMicroseconds = 7_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuSsgiTraceMicroseconds = 2_500,
            GpuDdgiTraceMicroseconds = 1_000,
            GpuDdgiBlendMicroseconds = 250,
            GpuDdgiRelocateClassifyMicroseconds = 200,
            GpuDdgiPublishMicroseconds = 50,
            GpuDdgiUpdateMicroseconds = 1_500,
            GpuGiCompositeMicroseconds = 500
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.GiCornellRoom,
            warmupFrameCount: 0,
            measurementFrameCount: 1,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "SsgiTracePass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "DdgiTracePass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "DdgiBlendPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "DdgiRelocateClassifyPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "DdgiPublishPass"), Is.True);
            Assert.That(report.GpuPasses.Any(pass => pass.Name == "GlobalIlluminationCompositePass"), Is.True);
            Assert.That(report.GpuPasses[0].Name, Is.EqualTo("SsgiTracePass"));
        });
    }

    [Test]
    public void CreateReport_RetainsWorstBudgetMetricAcrossAllMeasurementFrames()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var overBudget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "DDGI total memory",
                    Value: 257,
                    WarningThreshold: 200,
                    FailureThreshold: 256,
                    Unit: "MiB",
                    Status: RenderBudgetStatus.OverBudget)
            ]
        };
        var recovered = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "DDGI total memory",
                    Value: 128,
                    WarningThreshold: 200,
                    FailureThreshold: 256,
                    Unit: "MiB",
                    Status: RenderBudgetStatus.WithinBudget)
            ]
        };
        RendererDiagnostics validTiming = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000
        };

        analyzer.AddSample(validTiming, overBudget);
        analyzer.AddSample(validTiming, recovered);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        BudgetMetric metric = report.BudgetMetrics.Single(
            candidate => candidate.Name == "DDGI total memory");
        Assert.Multiple(() =>
        {
            Assert.That(metric.Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            Assert.That(metric.Value, Is.EqualTo(257));
            Assert.That(
                report.Findings.Any(
                    finding => finding.Category == "budget" &&
                               finding.Subject == "DDGI total memory"),
                Is.True);
        });
    }

    [TestCase(RenderBudgetStatus.Unavailable)]
    [TestCase(RenderBudgetStatus.Unknown)]
    public void CreateReport_RetainsIncompleteCoverageFromAnyMeasurementFrame(
        RenderBudgetStatus incompleteStatus)
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var unavailable = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GI GPU",
                    Value: 0,
                    WarningThreshold: 5,
                    FailureThreshold: 6,
                    Unit: "ms",
                    Status: incompleteStatus)
            ]
        };
        var available = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "GI GPU",
                    Value: 2,
                    WarningThreshold: 5,
                    FailureThreshold: 6,
                    Unit: "ms",
                    Status: RenderBudgetStatus.WithinBudget)
            ]
        };
        RendererDiagnostics validTiming = RendererDiagnostics.Empty with
        {
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuFrameMicroseconds = 1_000
        };

        analyzer.AddSample(validTiming, unavailable);
        analyzer.AddSample(validTiming, available);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        BudgetMetric metric = report.BudgetMetrics.Single(
            candidate => candidate.Name == "GI GPU");
        Assert.That(metric.Status, Is.EqualTo(incompleteStatus));
    }
}
