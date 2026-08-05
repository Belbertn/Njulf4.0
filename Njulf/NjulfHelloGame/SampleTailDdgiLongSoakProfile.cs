using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Owns the render identity and telemetry projection used by the production
/// tail-certified DDGI soak. It intentionally does not arm benchmark mode: the
/// long-run lifecycle owns all 3,600 frames and its memory trend window.
/// </summary>
internal static class SampleTailDdgiLongSoakProfile
{
    public const string Name = "tail-ddgi-accelerated";
    public const string GiGpuMetricSource = "GpuDdgiUpdateMicroseconds";
    public const int RequiredFrameCount = 3_600;
    public const int MinimumWarmupFrameCount = 1_200;
    public const ulong MaximumMemoryGrowthToleranceBytes = 1_048_576;

    private static readonly HashSet<string> NonApplicableBudgetMetricNames =
        new(StringComparer.Ordinal)
        {
            RenderBudgetEvaluator.MaterialGiCompileP95MetricName,
            RenderBudgetEvaluator.MaterialGiUploadP95MetricName,
            RenderBudgetEvaluator.MaterialGiPipelineP95MetricName
        };

    private static readonly HashSet<string> PercentileTimingMetricNames =
        new(StringComparer.Ordinal)
        {
            "CPU renderer",
            "GPU frame",
            "GI GPU",
            "GI CPU scheduling and upload"
        };

    public static IReadOnlyList<string> NonApplicableBudgetMetrics { get; } =
        NonApplicableBudgetMetricNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> RequiredPercentileTimingMetrics { get; } =
        PercentileTimingMetricNames
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    public static void Apply(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.PerformanceBudgets.ActiveProfile =
            RenderBudgetProfileKind.HighSpec1440p60;
        settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled = false;
        settings.Particles.FixedSimulationDeltaSeconds =
            HelloGame.BenchmarkSimulationDeltaSeconds;
        SampleBenchmarkCaptureVariant.Apply(
            settings,
            SampleBenchmarkCaptureVariant.TailAccelerated);
    }

    public static SampleTailDdgiLongSoakBudgetProjection ProjectBudget(
        RenderBudgetSnapshot budget,
        RendererDiagnostics diagnostics,
        bool materialStressMetricsNotApplicable = true)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IReadOnlyList<BudgetMetric> source =
            budget.Metrics ?? Array.Empty<BudgetMetric>();
        var projected = new BudgetMetric[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            BudgetMetric metric = source[i];
            if (string.Equals(metric.Name, "GI GPU", StringComparison.Ordinal))
            {
                bool available =
                    diagnostics.GpuTimingValid != 0 &&
                    diagnostics.SimpleDdgiActive != 0 &&
                    diagnostics.SimpleDdgiTransportV2Active != 0 &&
                    diagnostics.SimpleDdgiTransportTailCertificationEnabled;
                double value =
                    diagnostics.GpuDdgiUpdateMicroseconds / 1_000.0;
                projected[i] = metric with
                {
                    Value = value,
                    Status = available
                        ? Classify(
                            value,
                            metric.WarningThreshold,
                            metric.FailureThreshold)
                        : RenderBudgetStatus.Unavailable
                };
                continue;
            }

            projected[i] = materialStressMetricsNotApplicable &&
                NonApplicableBudgetMetricNames.Contains(metric.Name)
                ? metric with { Status = RenderBudgetStatus.Unavailable }
                : metric;
        }

        RenderBudgetSnapshot projectedBudget = budget with
        {
            Metrics = projected,
            OverallStatus = Combine(projected)
        };
        RendererDiagnostics coverageDiagnostics =
            materialStressMetricsNotApplicable
                ? diagnostics with
                {
                    MaterialCompileTimingSampleCount = 0,
                    MaterialUploadTimingSampleCount = 0
                }
                : diagnostics;
        return new SampleTailDdgiLongSoakBudgetProjection(
            projectedBudget,
            coverageDiagnostics);
    }

    public static bool IsNonApplicableBudgetMetric(string name) =>
        NonApplicableBudgetMetricNames.Contains(name);

    public static bool IsPercentileTimingMetric(string name) =>
        PercentileTimingMetricNames.Contains(name);

    private static RenderBudgetStatus Classify(
        double value,
        double warningThreshold,
        double failureThreshold)
    {
        if (!double.IsFinite(value) ||
            double.IsNaN(warningThreshold) ||
            double.IsNaN(failureThreshold))
        {
            return RenderBudgetStatus.Unavailable;
        }
        if (value > failureThreshold)
            return RenderBudgetStatus.OverBudget;
        if (value > warningThreshold)
            return RenderBudgetStatus.Warning;
        return RenderBudgetStatus.WithinBudget;
    }

    private static RenderBudgetStatus Combine(
        IReadOnlyList<BudgetMetric> metrics)
    {
        bool warning = false;
        bool available = false;
        foreach (BudgetMetric metric in metrics)
        {
            if (metric.Status == RenderBudgetStatus.OverBudget)
                return RenderBudgetStatus.OverBudget;
            if (metric.Status == RenderBudgetStatus.Warning)
                warning = true;
            if (metric.Status is RenderBudgetStatus.WithinBudget or
                RenderBudgetStatus.Warning)
            {
                available = true;
            }
        }

        if (warning)
            return RenderBudgetStatus.Warning;
        return available
            ? RenderBudgetStatus.WithinBudget
            : RenderBudgetStatus.Unavailable;
    }
}

internal sealed record SampleTailDdgiLongSoakBudgetProjection(
    RenderBudgetSnapshot Budget,
    RendererDiagnostics CoverageDiagnostics);
