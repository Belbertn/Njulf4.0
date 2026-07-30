using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Fail-closed validation shared by every release harness that consumes the
/// renderer budget stream. Optional metrics may remain unavailable when their
/// feature is inactive, but every metric required by the active renderer state
/// must be present and evaluable.
/// </summary>
internal readonly record struct SampleBudgetMetricCoverage(
    bool Passed,
    string? Failure)
{
    private static readonly string[] CoreRequiredMetricNames =
    [
        "CPU renderer",
        "GPU frame",
        RenderBudgetEvaluator.EffectiveGpuMemoryMetricName,
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
    ];

    public static SampleBudgetMetricCoverage Evaluate(
        IReadOnlyList<BudgetMetric>? metrics,
        RendererDiagnostics? diagnostics,
        string subject,
        RenderBudgetStatus? overallStatus = null)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("A diagnostic subject is required.", nameof(subject));

        if (diagnostics == null)
        {
            return new SampleBudgetMetricCoverage(
                false,
                $"{subject} renderer diagnostics are missing.");
        }

        if (metrics == null || metrics.Count == 0)
        {
            return new SampleBudgetMetricCoverage(
                false,
                $"{subject} budget metrics are missing.");
        }

        var metricsByName = new Dictionary<string, BudgetMetric>(
            StringComparer.Ordinal);
        foreach (BudgetMetric metric in metrics)
        {
            if (metric == null || string.IsNullOrWhiteSpace(metric.Name))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} contains a budget metric without a stable name.");
            }

            if (!metricsByName.TryAdd(metric.Name, metric))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} contains duplicate budget metric '{metric.Name}'.");
            }

            if (!Enum.IsDefined(metric.Status))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} metric '{metric.Name}' has an invalid status.");
            }

            if (string.IsNullOrWhiteSpace(metric.Unit))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} metric '{metric.Name}' has no stable unit.");
            }

            bool metricIsAvailable =
                metric.Status is RenderBudgetStatus.WithinBudget or
                    RenderBudgetStatus.Warning or
                    RenderBudgetStatus.OverBudget;
            if (metricIsAvailable &&
                (!double.IsFinite(metric.Value) ||
                 double.IsNaN(metric.WarningThreshold) ||
                 double.IsNegativeInfinity(metric.WarningThreshold) ||
                 double.IsNaN(metric.FailureThreshold) ||
                 double.IsNegativeInfinity(metric.FailureThreshold)))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} available metric '{metric.Name}' contains non-finite telemetry.");
            }
            if (metricIsAvailable)
            {
                if (metric.WarningThreshold < 0.0 ||
                    metric.FailureThreshold < 0.0 ||
                    metric.WarningThreshold > metric.FailureThreshold)
                {
                    return new SampleBudgetMetricCoverage(
                        false,
                        $"{subject} available metric '{metric.Name}' contains invalid or inverted thresholds.");
                }

                RenderBudgetStatus expectedStatus =
                    metric.Value > metric.FailureThreshold
                        ? RenderBudgetStatus.OverBudget
                        : metric.Value > metric.WarningThreshold
                            ? RenderBudgetStatus.Warning
                            : RenderBudgetStatus.WithinBudget;
                if (metric.Status != expectedStatus)
                {
                    return new SampleBudgetMetricCoverage(
                        false,
                        $"{subject} metric '{metric.Name}' reports status " +
                        $"'{metric.Status}' but its value and thresholds require " +
                        $"'{expectedStatus}'.");
                }
            }
        }

        if (overallStatus.HasValue)
        {
            if (!Enum.IsDefined(overallStatus.Value))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} has an invalid overall budget status.");
            }

            if (overallStatus.Value is
                RenderBudgetStatus.Unknown or
                RenderBudgetStatus.Unavailable)
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} overall budget status is " +
                    $"{overallStatus.Value.ToString().ToLowerInvariant()}.");
            }

            bool containsOverBudgetMetric = metricsByName.Values.Any(
                static metric => metric.Status == RenderBudgetStatus.OverBudget);
            if (overallStatus.Value == RenderBudgetStatus.OverBudget &&
                !containsOverBudgetMetric)
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} reports an over-budget overall status without an over-budget metric.");
            }

            if (overallStatus.Value != RenderBudgetStatus.OverBudget &&
                containsOverBudgetMetric)
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} contains an over-budget metric but its overall status is " +
                    $"{overallStatus.Value.ToString().ToLowerInvariant()}.");
            }
        }

        foreach (string requiredName in GetRequiredMetricNames(diagnostics))
        {
            if (!metricsByName.TryGetValue(requiredName, out BudgetMetric? metric))
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} is missing required budget metric '{requiredName}'.");
            }

            if (metric.Status is RenderBudgetStatus.Unavailable or RenderBudgetStatus.Unknown)
            {
                return new SampleBudgetMetricCoverage(
                    false,
                    $"{subject} required budget metric '{requiredName}' is " +
                    $"{metric.Status.ToString().ToLowerInvariant()}.");
            }
        }

        return new SampleBudgetMetricCoverage(true, null);
    }

    internal static IEnumerable<string> GetRequiredMetricNames(
        RendererDiagnostics? diagnostics)
    {
        foreach (string name in CoreRequiredMetricNames)
            yield return name;

        diagnostics ??= RendererDiagnostics.Empty;
        if (diagnostics.GlobalIlluminationEnabled == 0)
            yield break;

        yield return "Material GI non-finite values";
        yield return "Material GI clamped values";
        yield return "Material alpha candidate limit";
        yield return "GI GPU";
        yield return "GI CPU scheduling and upload";
        yield return "GI unique residency";

        if (diagnostics.AccelerationStructureMemoryBudgetBytes > 0 ||
            diagnostics.GlobalIlluminationRayQueryActive != 0)
        {
            yield return "GI resident acceleration structures";
        }

        if (diagnostics.FarFieldMemoryBudgetBytes > 0 ||
            diagnostics.FarFieldPagedFeatureEnabled != 0 ||
            diagnostics.FarFieldPagedMode != 0)
        {
            yield return "Far-field page cache";
        }

        bool materialTransportV2Active =
            (diagnostics.MaterialGiV2ActiveFeatures &
             MaterialGiV2Feature.MaterialTransport) != 0;
        if (materialTransportV2Active)
        {
            yield return RenderBudgetEvaluator.MaterialGiPrimitiveProfileMemoryMetricName;
            yield return RenderBudgetEvaluator.MaterialGiActiveV1FallbackMetricName;
            yield return RenderBudgetEvaluator.MaterialGiActiveInvalidProfileMetricName;

            if (diagnostics.MaterialGiReleaseQualificationRequired != 0)
                yield return RenderBudgetEvaluator.MaterialGiQualificationMetricName;
            if (diagnostics.MaterialCompileTimingSampleCount > 0)
                yield return RenderBudgetEvaluator.MaterialGiCompileP95MetricName;
            if (diagnostics.MaterialUploadTimingSampleCount > 0)
                yield return RenderBudgetEvaluator.MaterialGiUploadP95MetricName;
            if (diagnostics.MaterialCompileTimingSampleCount > 0 &&
                diagnostics.MaterialUploadTimingSampleCount > 0)
            {
                yield return RenderBudgetEvaluator.MaterialGiPipelineP95MetricName;
            }
        }

        bool emissiveSamplingV2Active =
            (diagnostics.MaterialGiV2ActiveFeatures &
             MaterialGiV2Feature.EmissiveMeshSampling) != 0;
        if (emissiveSamplingV2Active)
        {
            yield return RenderBudgetEvaluator.DdgiEmissiveTruncatedSourceMetricName;
            yield return RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName;
            yield return RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedObjectMetricName;
            yield return RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedImportanceMetricName;
        }

        bool ddgiActive =
            diagnostics.GlobalIlluminationDdgiActive != 0 ||
            diagnostics.SimpleDdgiActive != 0;
        if (ddgiActive)
        {
            yield return "DDGI probes";
            yield return "DDGI active probe budget";
            yield return "DDGI atlas memory";
            yield return "DDGI total memory";

            if (diagnostics.DdgiProbesUpdated > 0)
            {
                yield return "DDGI update request budget";
                yield return "DDGI probes updated";
            }
            if (diagnostics.DdgiGatherTileCount > 0)
                yield return "DDGI gather fallback tiles";
            if (diagnostics.SimpleDdgiDirtyFirstUpdateLatencySampleCount > 0)
                yield return "DDGI dirty first-update latency";
            if (diagnostics.SimpleDdgiDirtyConvergenceLatencySampleCount > 0)
                yield return "DDGI dirty convergence latency";
        }

        if (diagnostics.GlobalIlluminationSsgiActive != 0 ||
            diagnostics.SsgiRayCount > 0)
        {
            yield return "SSGI resolution scale";
            yield return "SSGI rays per pixel";
        }
    }
}
