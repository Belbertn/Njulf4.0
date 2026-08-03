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
    public void ResolvedGiSettingsDifference_ReportsNamedFieldsAndHonorsTheBound()
    {
        var expected = new ResolvedGiSettingsMetadata(
            "first",
            string.Empty,
            ["alpha=1", "beta=two", "removed=present"]);
        var actual = new ResolvedGiSettingsMetadata(
            "second",
            string.Empty,
            ["alpha=2", "beta=two", "added=present"]);

        IReadOnlyList<string> differences =
            SampleBenchmarkAnalyzer.DescribeResolvedGiSettingsDifferences(
                expected,
                actual,
                maximumDifferenceCount: 2);

        Assert.Multiple(() =>
        {
            Assert.That(differences, Has.Count.EqualTo(2));
            Assert.That(differences[0], Is.EqualTo("'added' from <missing> to 'present'"));
            Assert.That(differences[1], Is.EqualTo("'alpha' from '1' to '2'"));
        });
    }

    [Test]
    public void MeasurementReadiness_WaitsForGpuTimingSettlementAndStableCapacity()
    {
        RendererDiagnostics ready = RendererDiagnostics.Empty with
        {
            GpuTimingValid = 1,
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    ReadbackValid = 1,
                    ParticipatingProbeCount = 1_000,
                    SourceRepairProbeCount = 50,
                    ConvergedProbeCount = 950
                },
            CaptureFrame = new PerformanceCaptureFrameMetadata(
                800,
                800,
                DdgiRuntimeWarmupState.SteadyState,
                800,
                800)
            {
                TransportConvergencePending = false
            },
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true
                }
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(SampleBenchmarkRunner.IsReadyForMeasurement(ready), Is.True);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(
                    ready with { GpuTimingValid = 0 }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    CaptureFrame = ready.CaptureFrame with
                    {
                        TransportConvergencePending = true
                    }
                }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    SimpleDdgiUploadTiming = ready.SimpleDdgiUploadTiming with
                    {
                        CapacityDetails = ready.SimpleDdgiUploadTiming.CapacityDetails with
                        {
                            StableKeyHit = false
                        }
                    }
                }),
                Is.False);
            Assert.That(
                SampleBenchmarkRunner.IsReadyForMeasurement(ready with
                {
                    SimpleDdgiTransportConvergence =
                        ready.SimpleDdgiTransportConvergence with
                        {
                            SourceRepairProbeCount = 51
                        }
                }),
                Is.False);
        });
    }

    [TestCase(1, 1_000, 50, 0, 950, 0, true)]
    [TestCase(1, 1_000, 51, 0, 949, 0, false)]
    [TestCase(1, 1_000, 200, 200, 800, 0, true)]
    [TestCase(1, 1_000, 200, 149, 800, 0, false)]
    [TestCase(1, 1_000, 0, 0, 800, 150, true)]
    [TestCase(0, 1_000, 0, 0, 1_000, 0, false)]
    [TestCase(1, 0, 0, 0, 0, 0, false)]
    public void MeasurementReadiness_RequiresNinetyFivePercentSourceReadyPopulation(
        int readbackValid,
        int participants,
        int sourceRepair,
        int routineSourceRepair,
        int converged,
        int routineMaintenancePending,
        bool expected)
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiTransportConvergence =
                SimpleDdgiTransportConvergenceTelemetry.Empty with
                {
                    ReadbackValid = readbackValid,
                    ParticipatingProbeCount = participants,
                    SourceRepairProbeCount = sourceRepair,
                    RoutineSourceRepairProbeCount = routineSourceRepair,
                    ConvergedProbeCount = converged,
                    RoutineMaintenancePendingProbeCount =
                        routineMaintenancePending
                }
        };

        Assert.That(
            SampleBenchmarkRunner.HasSourceReadySimpleDdgiTransportPopulation(
                diagnostics),
            Is.EqualTo(expected));
    }

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
            GpuFrameMicroseconds = 17_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuForwardOpaqueMicroseconds = 5_000,
            GpuBloomUpsampleMicroseconds = 1_000
        }, budget);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CpuTotalDrawSceneMicroseconds = 2_500,
            GpuFrameMicroseconds = 18_000,
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
    public void CreateReport_TimingBudgetsUseMeasurementP95InsteadOfWorstRollingValue()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        var rollingWorst = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    "CPU renderer",
                    Value: 100,
                    WarningThreshold: 8.5,
                    FailureThreshold: 10,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    "GPU frame",
                    Value: 100,
                    WarningThreshold: 8.5,
                    FailureThreshold: 10,
                    Unit: "ms",
                    Status: RenderBudgetStatus.OverBudget)
            ]
        };

        for (int index = 0; index < 20; index++)
        {
            long microseconds = index == 19 ? 100_000 : 1_000;
            analyzer.AddSample(
                RendererDiagnostics.Empty with
                {
                    CpuTotalDrawSceneMicroseconds = microseconds,
                    GpuFrameMicroseconds = microseconds,
                    GpuTimingSupported = 1,
                    GpuTimingValid = 1
                },
                rollingWorst);
        }

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 20, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 20,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 19);

        Assert.Multiple(() =>
        {
            BudgetMetric cpu = report.BudgetMetrics.Single(
                metric => metric.Name == "CPU renderer");
            BudgetMetric gpu = report.BudgetMetrics.Single(
                metric => metric.Name == "GPU frame");
            Assert.That(cpu.Value, Is.EqualTo(1.0));
            Assert.That(cpu.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(gpu.Value, Is.EqualTo(1.0));
            Assert.That(gpu.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            Assert.That(report.CpuFrameMilliseconds.MaxMilliseconds, Is.EqualTo(100.0));
            Assert.That(report.GpuFrameMilliseconds.MaxMilliseconds, Is.EqualTo(100.0));
        });
    }

    [Test]
    public void CreateReport_MaterialTimingBudgetsContainOnlyMeasurementWindowEvents()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        RendererDiagnostics baseline = RendererDiagnostics.Empty with
        {
            MaterialCompileTimingSampleCount = 2,
            MaterialUploadTimingSampleCount = 2
        };
        analyzer.SetMeasurementBaseline(baseline);
        var staleRollingBudget = RenderBudgetSnapshot.Empty with
        {
            Metrics =
            [
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
                    4.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiUploadP95MetricName,
                    4.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget),
                new BudgetMetric(
                    RenderBudgetEvaluator.MaterialGiPipelineP95MetricName,
                    8.0,
                    0.2125,
                    0.25,
                    "ms",
                    RenderBudgetStatus.OverBudget)
            ]
        };

        analyzer.AddSample(baseline, staleRollingBudget);
        analyzer.AddSample(
            baseline with
            {
                MaterialCompileTimingSampleCount = 3,
                MaterialUploadTimingSampleCount = 3,
                MaterialLastCompileMicroseconds = 100,
                MaterialLastUploadMicroseconds = 100
            },
            staleRollingBudget);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.Normal,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiCompileP95MetricName).Value,
                Is.EqualTo(0.1));
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiUploadP95MetricName).Value,
                Is.EqualTo(0.1));
            Assert.That(
                report.BudgetMetrics.Single(metric =>
                    metric.Name == RenderBudgetEvaluator.MaterialGiPipelineP95MetricName).Value,
                Is.EqualTo(0.2));
            Assert.That(
                report.BudgetMetrics.Where(metric =>
                    metric.Name.StartsWith("Material GI", StringComparison.Ordinal))
                    .All(metric => metric.Status == RenderBudgetStatus.WithinBudget),
                Is.True);
        });
    }

    [Test]
    public void CreateReport_ExportsSimpleDdgiCpuPhasesAndCombinedTransportDistribution()
    {
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            CpuTotalDrawSceneMicroseconds = 3_000,
            GpuFrameMicroseconds = 5_000,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuSimpleDdgiTransportMicroseconds = 900,
            GpuSimpleDdgiBlendMicroseconds = 200,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                TotalMicroseconds = 1_200,
                CapacityMicroseconds = 25,
                QueueBuildMicroseconds = 300,
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    PredicateMicroseconds = 4
                }
            }
        }, RenderBudgetSnapshot.Empty);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            SimpleDdgiActive = 1,
            CpuTotalDrawSceneMicroseconds = 3_100,
            GpuFrameMicroseconds = 5_200,
            GpuTimingSupported = 1,
            GpuTimingValid = 1,
            GpuSimpleDdgiTransportMicroseconds = 1_100,
            GpuSimpleDdgiBlendMicroseconds = 300,
            SimpleDdgiUploadTiming = new SimpleDdgiUploadTiming
            {
                TotalMicroseconds = 1_400,
                CapacityMicroseconds = 30,
                QueueBuildMicroseconds = 350,
                CapacityDetails = new SimpleDdgiCapacityTiming
                {
                    StableKeyHit = true,
                    PredicateMicroseconds = 5
                }
            }
        }, RenderBudgetSnapshot.Empty);

        SampleBenchmarkReport report = analyzer.CreateReport(
            new SampleBenchmarkOptions(true, 0, 2, null),
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            warmupFrameCount: 0,
            measurementFrameCount: 2,
            firstMeasurementFrameIndex: 0,
            lastMeasurementFrameIndex: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiUpload")
                    .P95Milliseconds,
                Is.EqualTo(1.4));
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiUpload.Capacity")
                    .P95Milliseconds,
                Is.EqualTo(0.03));
            Assert.That(
                report.CpuStages.Single(stage => stage.Name == "SimpleDdgiCapacity.Predicate")
                    .Count,
                Is.EqualTo(2));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.Count, Is.EqualTo(2));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.MedianMilliseconds, Is.EqualTo(1.25));
            Assert.That(report.SimpleDdgiTransportBlendMilliseconds.P95Milliseconds, Is.EqualTo(1.4));
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
