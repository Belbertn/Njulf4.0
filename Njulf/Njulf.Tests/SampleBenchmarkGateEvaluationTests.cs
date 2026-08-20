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
}
