using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBenchmarkGateEvaluationTests
{
    [Test]
    public void Evaluate_PassesWithGpuTimingAndNoHardBudgetFailure()
    {
        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(CreateReport(
                metrics:
                [
                    new BudgetMetric(
                        "GPU frame",
                        15,
                        14,
                        16,
                        "ms",
                        RenderBudgetStatus.Warning)
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.True);
            Assert.That(evaluation.Failure, Is.Null);
        });
    }

    [Test]
    public void Evaluate_RealtimeTargetPassesAtLocked1080p60AndMemoryLimits()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            Options = CreateReport(metrics: []).Options with
            {
                RequireRealtime1080p60Target = true
            },
            CpuFrameMilliseconds = Timing("CPU", 6.0, 16.0),
            GpuFrameMilliseconds = Timing("GPU", 10.0, 16.0),
            LastDiagnostics = RendererDiagnostics.Empty with
            {
                CaptureRenderWidth = 1920,
                CaptureRenderHeight = 1080,
                TrackedGpuMemoryBytes =
                    SampleRealtimePerformanceTarget.MaximumTrackedGpuMemoryBytes
            }
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.That(evaluation.Passed, Is.True, evaluation.Failure);
    }

    [Test]
    public void RealtimeTarget_UsesSixGiBWithTenPercentHeadroom()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleRealtimePerformanceTarget.TargetGpuMemoryBytes,
                Is.EqualTo(6UL * 1024UL * 1024UL * 1024UL));
            Assert.That(
                SampleRealtimePerformanceTarget.MinimumMemoryHeadroomFraction,
                Is.EqualTo(0.10));
            Assert.That(
                SampleRealtimePerformanceTarget.MaximumTrackedGpuMemoryBytes,
                Is.EqualTo(5_798_205_849UL));
        });
    }

    [TestCase(6.01, 10.0, 16.0, 16.0, 0UL, "CPU frame P95")]
    [TestCase(6.0, 10.01, 16.0, 16.0, 0UL, "GPU frame P95")]
    [TestCase(6.0, 10.0, 16.68, 16.0, 0UL, "CPU frame P99")]
    [TestCase(6.0, 10.0, 16.0, 16.68, 0UL, "GPU frame P99")]
    [TestCase(6.0, 10.0, 16.0, 16.0, 5798205850UL,
        "Tracked GPU memory")]
    public void Evaluate_RealtimeTargetFailsClosedAtEachAbsoluteLimit(
        double cpuP95,
        double gpuP95,
        double cpuP99,
        double gpuP99,
        ulong trackedBytes,
        string expectedFailure)
    {
        SampleBenchmarkReport baseline = CreateReport(metrics: []);
        SampleBenchmarkReport report = baseline with
        {
            Options = baseline.Options with
            {
                RequireRealtime1080p60Target = true
            },
            CpuFrameMilliseconds = Timing("CPU", cpuP95, cpuP99),
            GpuFrameMilliseconds = Timing("GPU", gpuP95, gpuP99),
            LastDiagnostics = RendererDiagnostics.Empty with
            {
                CaptureRenderWidth = 1920,
                CaptureRenderHeight = 1080,
                TrackedGpuMemoryBytes = trackedBytes
            }
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain(expectedFailure));
        });
    }

    [TestCase(900UL, true)]
    [TestCase(901UL, false)]
    public void Evaluate_RealtimeTargetEnforcesDriverReportedTenPercentHeadroom(
        ulong actualUsageBytes,
        bool expectedPass)
    {
        SampleBenchmarkReport baseline = CreateReport(metrics: []);
        SampleBenchmarkReport report = baseline with
        {
            Options = baseline.Options with
            {
                RequireRealtime1080p60Target = true
            },
            CpuFrameMilliseconds = Timing("CPU", 6.0, 16.0),
            GpuFrameMilliseconds = Timing("GPU", 10.0, 16.0),
            LastDiagnostics = RendererDiagnostics.Empty with
            {
                CaptureRenderWidth = 1920,
                CaptureRenderHeight = 1080,
                TrackedGpuMemoryBytes = 0,
                GpuMemoryBudgetQueryAvailable = 1,
                ActualGpuMemoryUsageBytes = actualUsageBytes,
                ActualGpuMemoryBudgetBytes = 1_000
            }
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.That(
            evaluation.Passed,
            Is.EqualTo(expectedPass),
            evaluation.Failure);
    }

    [Test]
    public void Evaluate_FailsWhenRequiredMetricIsMissing()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []);
        report = report with
        {
            BudgetMetrics = report.BudgetMetrics
                .Where(metric => metric.Name != "GPU memory")
                .ToArray()
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("GPU memory"));
        });
    }

    [TestCase(RenderBudgetStatus.Unavailable, "unavailable")]
    [TestCase(RenderBudgetStatus.Unknown, "unknown")]
    public void Evaluate_FailsWhenRequiredMetricIsIncomplete(
        RenderBudgetStatus incompleteStatus,
        string expectedDiagnostic)
    {
        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(CreateReport(
                metrics:
                [
                    new BudgetMetric(
                        "CPU renderer",
                        0,
                        0,
                        0,
                        "ms",
                        incompleteStatus)
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("CPU renderer"));
            Assert.That(evaluation.Failure, Does.Contain(expectedDiagnostic));
        });
    }

    [Test]
    public void Evaluate_RequiresDdgiTierMetricsWhenDdgiIsActive()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            LastDiagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationDdgiActive = 1,
                SimpleDdgiActive = 1
            }
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("Material GI non-finite values"));
        });
    }

    [TestCase(GiTimingAttribution.Unavailable, false)]
    [TestCase(GiTimingAttribution.Inclusive, false)]
    [TestCase(GiTimingAttribution.Exclusive, true)]
    [TestCase(GiTimingAttribution.PairedEstimate, true)]
    public void RequiredMetrics_GiGpuMatchesIncrementalAttributionAvailability(
        GiTimingAttribution attribution,
        bool expectedRequired)
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            GpuTimingValid = 1,
            GpuForwardGiIncrementalAttribution = attribution
        };

        string[] required = SampleBudgetMetricCoverage
            .GetRequiredMetricNames(diagnostics)
            .ToArray();

        Assert.That(required.Contains("GI GPU"), Is.EqualTo(expectedRequired));
    }

    [Test]
    public void RequiredMetrics_DoesNotRequireGiGpuWithoutValidGpuTiming()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            GpuTimingValid = 0,
            GpuForwardGiIncrementalAttribution = GiTimingAttribution.Exclusive
        };

        string[] required = SampleBudgetMetricCoverage
            .GetRequiredMetricNames(diagnostics)
            .ToArray();

        Assert.That(required, Does.Not.Contain("GI GPU"));
    }

    [Test]
    public void Evaluate_FailsWhenADeclaredHardBudgetIsExceeded()
    {
        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(CreateReport(
                metrics:
                [
                    new BudgetMetric(
                        "DDGI total memory",
                        257,
                        200,
                        256,
                        "bytes",
                        RenderBudgetStatus.OverBudget)
                ]));

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("DDGI total memory"));
        });
    }

    [Test]
    public void Evaluate_AcceptsExactZeroEventMaterialTimingWindow()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            MaterialGiV2ActiveFeatures =
                MaterialGiV2Feature.MaterialTransport,
            MaterialCompileTimingSampleCount = 7,
            MaterialUploadTimingSampleCount = 3
        };
        BudgetMetric[] metrics = SampleBudgetMetricCoverage
            .GetRequiredMetricNames(diagnostics)
            .Select(name => new BudgetMetric(
                name,
                0,
                1,
                2,
                "count",
                name is RenderBudgetEvaluator.MaterialGiCompileP95MetricName or
                    RenderBudgetEvaluator.MaterialGiUploadP95MetricName or
                    RenderBudgetEvaluator.MaterialGiPipelineP95MetricName
                        ? RenderBudgetStatus.Unavailable
                        : RenderBudgetStatus.WithinBudget))
            .ToArray();
        SampleBenchmarkReport report = CreateReport(metrics) with
        {
            LastDiagnostics = diagnostics,
            MaterialTimingEvidence = new SampleBenchmarkMaterialTimingEvidence(
                SampleBenchmarkTimingStats.Empty(
                    RenderBudgetEvaluator.MaterialGiCompileP95MetricName),
                SampleBenchmarkTimingStats.Empty(
                    RenderBudgetEvaluator.MaterialGiUploadP95MetricName),
                SampleBenchmarkTimingStats.Empty(
                    RenderBudgetEvaluator.MaterialGiPipelineP95MetricName),
                CompileSequenceExact: true,
                UploadSequenceExact: true)
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.True, evaluation.Failure);
            Assert.That(evaluation.Failure, Is.Null);
        });
    }

    [Test]
    public void Evaluate_RejectsInexactMaterialTimingWindow()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            MaterialGiV2ActiveFeatures =
                MaterialGiV2Feature.MaterialTransport
        };
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            LastDiagnostics = diagnostics,
            MaterialTimingEvidence =
                SampleBenchmarkMaterialTimingEvidence.Unavailable
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("not an exact"));
        });
    }

    [Test]
    public void Evaluate_FailsClosedWithoutGpuTiming()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            GpuTimingSupported = 1,
            GpuTimingValidSampleCount = 0,
            GpuTimingUnavailableReason = "timestamp readback unavailable"
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("timestamp readback unavailable"));
        });
    }

    [Test]
    public void Evaluate_FailsWhenAnyMeasurementFrameLacksGpuTiming()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            Options = new SampleBenchmarkOptions(
                Enabled: true,
                WarmupFrameCount: 1,
                MeasureFrameCount: 120,
                ReportPath: null),
            MeasurementFrameCount = 120,
            GpuTimingValidSampleCount = 119,
            GpuFrameMilliseconds =
                new SampleBenchmarkTimingStats("GPU", 119, 1, 1, 1, 1)
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("119/120"));
        });
    }

    [Test]
    public void Evaluate_FailsWhenMeasurementReportIsIncomplete()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []) with
        {
            Options = new SampleBenchmarkOptions(
                Enabled: true,
                WarmupFrameCount: 1,
                MeasureFrameCount: 120,
                ReportPath: null),
            MeasurementFrameCount = 119
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("119/120"));
        });
    }

    [Test]
    public void DdgiGate_AuthenticatesTheExactCompleteMovingRoute()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []);
        string trajectoryFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
            SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            SampleBistroQualityCaptureVariant.SunScaleStep);
        report = report with
        {
            Options = report.Options with
            {
                MeasureFrameCount = 300,
                Trajectory = SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                TrajectoryFingerprint = trajectoryFingerprint
            },
            MeasurementFrameCount = 300,
            CaptureContract = SampleBenchmarkCaptureContract.Unavailable with
            {
                Comparable = true,
                ProductionTiming = true,
                Mismatches = Array.Empty<string>(),
                Trajectory = SampleBenchmarkTrajectory.SponzaHorizontalName,
                TrajectoryFingerprint = trajectoryFingerprint,
                TrajectoryFrameCount = 300
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                SampleDdgiProductionGate.IsAuthenticatedMovingTrajectory(report),
                Is.True);
            Assert.That(
                SampleDdgiProductionGate.IsAuthenticatedMovingTrajectory(
                    report with
                    {
                        CaptureContract = report.CaptureContract with
                        {
                            TrajectoryFingerprint = "sha256:forged"
                        }
                    }),
                Is.False);
            Assert.That(
                SampleDdgiProductionGate.IsAuthenticatedMovingTrajectory(
                    report with { MeasurementFrameCount = 299 }),
                Is.False);
        });
    }

    [Test]
    public void Evaluate_AcceptsExactNonShippingQualificationCandidateTuple()
    {
        SampleBenchmarkReport report =
            CreateQualificationCandidateReport();

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(report);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.True);
            Assert.That(evaluation.Failure, Is.Null);
        });
    }

    [TestCase(MaterialGiRolloutMode.Conformance, MaterialGiV2Feature.All)]
    [TestCase(
        MaterialGiRolloutMode.QualificationCandidate,
        MaterialGiV2Feature.MaterialTransport)]
    public void Evaluate_RejectsImpersonatedOrPartialQualificationCandidate(
        MaterialGiRolloutMode mode,
        MaterialGiV2Feature features)
    {
        SampleBenchmarkReport candidate =
            CreateQualificationCandidateReport();
        candidate = candidate with
        {
            LastDiagnostics = candidate.LastDiagnostics with
            {
                MaterialGiRolloutMode = mode,
                MaterialGiV2ActiveFeatures = features
            }
        };

        SampleBenchmarkGateEvaluation evaluation =
            SampleBenchmarkGateEvaluation.Evaluate(candidate);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Passed, Is.False);
            Assert.That(evaluation.Failure, Does.Contain("provenance tuple"));
        });
    }

    private static SampleBenchmarkReport CreateQualificationCandidateReport()
    {
        SampleBenchmarkReport report = CreateReport(metrics: []);
        return report with
        {
            Options = report.Options with
            {
                MaterialGiQualificationCandidate = true
            },
            LastDiagnostics = RendererDiagnostics.Empty with
            {
                MaterialGiRolloutMode =
                    MaterialGiRolloutMode.QualificationCandidate,
                MaterialGiV2ActiveFeatures = MaterialGiV2Feature.All,
                MaterialGiReleaseQualificationRequired = 1,
                MaterialGiReleaseQualified = 0,
                MaterialGiReleaseQualificationFailureCount = 0,
                MaterialGiQualifiedDeviceCount = 0,
                MaterialGiReleaseApprovalId = string.Empty,
                MaterialGiReleaseEvidenceSha256 = string.Empty
            }
        };
    }

    private static SampleBenchmarkReport CreateReport(
        IReadOnlyList<BudgetMetric> metrics)
    {
        var requiredMetrics = new Dictionary<string, BudgetMetric>(
            StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "CPU renderer",
                     "GPU frame",
                     "GPU memory",
                     "Upload",
                     "Objects",
                     "Meshlets",
                     "Foliage clusters",
                     "Foliage meshlet draws",
                     "Foliage grass blades",
                     "Foliage memory",
                     "Materials",
                     "Textures",
                     "Lights",
                     "Shadowed lights",
                     "Reflection probes",
                     "Transparent objects"
                 })
        {
            requiredMetrics[name] = new BudgetMetric(
                name,
                0,
                1,
                2,
                "count",
                RenderBudgetStatus.WithinBudget);
        }

        foreach (BudgetMetric metric in metrics)
            requiredMetrics[metric.Name] = metric;

        return new(
            "test",
            DateTimeOffset.UnixEpoch,
            new SampleBenchmarkOptions(
                Enabled: true,
                WarmupFrameCount: 1,
                MeasureFrameCount: 1,
                ReportPath: null),
            SamplePerformanceScenario.Normal,
            1,
            1,
            1,
            1,
            new SampleBenchmarkTimingStats("CPU", 1, 1, 1, 1, 1),
            new SampleBenchmarkTimingStats("GPU", 1, 1, 1, 1, 1),
            GpuTimingSupported: 1,
            GpuTimingValidSampleCount: 1,
            GpuTimingUnavailableReason: string.Empty,
            GpuPasses: [],
            CpuStages: [],
            Findings: [],
            BudgetMetrics: requiredMetrics.Values.ToArray(),
            LastDiagnostics: RendererDiagnostics.Empty);
    }

    private static SampleBenchmarkTimingStats Timing(
        string name,
        double p95,
        double p99) =>
        new(name, 1, p95, p95, p99, p95)
        {
            MedianMilliseconds = p95,
            P50Milliseconds = p95,
            P99Milliseconds = p99
        };
}
