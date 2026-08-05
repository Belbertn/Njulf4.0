using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Converts a completed benchmark into an executable release gate. Reports are
/// still written on failure, but the host must return non-zero when timing is
/// unavailable, a hard budget is exceeded, or the DDGI production gate fails.
/// </summary>
internal readonly record struct SampleBenchmarkGateEvaluation(
    bool Passed,
    string? Failure)
{
    public static SampleBenchmarkGateEvaluation Evaluate(SampleBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.MeasurementFrameCount <= 0 ||
            report.MeasurementFrameCount != report.Options.MeasureFrameCount)
        {
            return new SampleBenchmarkGateEvaluation(
                false,
                $"Benchmark measurement is incomplete: captured " +
                $"{report.MeasurementFrameCount}/{report.Options.MeasureFrameCount} frames.");
        }

        if (report.GpuTimingSupported == 0 ||
            report.GpuTimingValidSampleCount != report.MeasurementFrameCount)
        {
            return new SampleBenchmarkGateEvaluation(
                false,
                $"Benchmark GPU timing coverage is incomplete: valid " +
                $"{report.GpuTimingValidSampleCount}/{report.MeasurementFrameCount} samples" +
                (string.IsNullOrWhiteSpace(report.GpuTimingUnavailableReason)
                    ? "."
                    : $": {report.GpuTimingUnavailableReason}"));
        }

        if (report.GpuFrameMilliseconds.Count != report.GpuTimingValidSampleCount)
        {
            return new SampleBenchmarkGateEvaluation(
                false,
                "Benchmark GPU timing statistics do not match the declared valid sample count.");
        }

        if (report.Options.MaterialGiQualificationCandidate)
        {
            RendererDiagnostics diagnostics = report.LastDiagnostics;
            if (diagnostics.MaterialGiRolloutMode !=
                    MaterialGiRolloutMode.QualificationCandidate ||
                diagnostics.MaterialGiV2ActiveFeatures !=
                    MaterialGiV2Feature.All ||
                diagnostics.MaterialGiReleaseQualificationRequired != 1 ||
                diagnostics.MaterialGiReleaseQualified != 0 ||
                diagnostics.MaterialGiReleaseQualificationFailureCount != 0 ||
                diagnostics.MaterialGiQualifiedDeviceCount != 0 ||
                !string.IsNullOrEmpty(
                    diagnostics.MaterialGiReleaseApprovalId) ||
                !string.IsNullOrEmpty(
                    diagnostics.MaterialGiReleaseEvidenceSha256))
            {
                return new SampleBenchmarkGateEvaluation(
                    false,
                    "Qualification-candidate benchmark diagnostics do not prove " +
                    "the exact non-shipping Candidate/All/Required=1/" +
                    "Qualified=0/Failures=0 provenance tuple.");
            }
        }

        RendererDiagnostics coverageDiagnostics = report.LastDiagnostics;
        bool materialTimingApplicable =
            coverageDiagnostics.GlobalIlluminationEnabled != 0 &&
            (coverageDiagnostics.MaterialGiV2ActiveFeatures &
             MaterialGiV2Feature.MaterialTransport) != 0;
        if (materialTimingApplicable)
        {
            SampleBenchmarkMaterialTimingEvidence materialTiming =
                report.MaterialTimingEvidence;
            if (!materialTiming.CompileSequenceExact ||
                !materialTiming.UploadSequenceExact)
            {
                return new SampleBenchmarkGateEvaluation(
                    false,
                    "Benchmark material compile/upload timing deltas are not an exact measurement-window sequence.");
            }

            coverageDiagnostics = coverageDiagnostics with
            {
                MaterialCompileTimingSampleCount =
                    materialTiming.Compile.Count,
                MaterialUploadTimingSampleCount =
                    materialTiming.Upload.Count
            };
        }

        SampleBudgetMetricCoverage metricCoverage =
            SampleBudgetMetricCoverage.Evaluate(
                report.BudgetMetrics,
                coverageDiagnostics,
                "Benchmark");
        if (!metricCoverage.Passed)
            return new SampleBenchmarkGateEvaluation(false, metricCoverage.Failure);

        foreach (BudgetMetric metric in report.BudgetMetrics)
        {
            if (metric.Status != RenderBudgetStatus.OverBudget)
                continue;

            return new SampleBenchmarkGateEvaluation(
                false,
                $"Benchmark exceeded '{metric.Name}': " +
                $"{metric.Value:R} {metric.Unit} > " +
                $"{metric.FailureThreshold:R} {metric.Unit}.");
        }

        if (report.DdgiProductionGate is { Passed: false } ddgiGate)
        {
            string detail = ddgiGate.Failures.Count > 0
                ? ddgiGate.Failures[0].Detail
                : "The DDGI production gate failed without a detailed finding.";
            return new SampleBenchmarkGateEvaluation(false, detail);
        }

        return new SampleBenchmarkGateEvaluation(true, null);
    }
}
