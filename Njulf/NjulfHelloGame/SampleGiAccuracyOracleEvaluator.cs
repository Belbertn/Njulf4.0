using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public enum SampleGiAccuracyOracleStatus
{
    Passed,
    Failed,
    InsufficientData,
    NotApplicable
}

public sealed record SampleGiAccuracyOracleResult(
    string Name,
    SamplePerformanceScenario Scenario,
    string Metric,
    SampleGiAccuracyOracleStatus Status,
    float MeasuredValue,
    float? ReferenceValue,
    float RelativeError,
    int? LatencyFrames,
    string Detail);

public static class SampleGiAccuracyOracleEvaluator
{
    public static IReadOnlyList<SampleGiAccuracyOracleResult> Evaluate(
        SamplePerformanceScenario scenario,
        IReadOnlyList<RendererDiagnostics> samples)
    {
        if (samples == null)
            throw new ArgumentNullException(nameof(samples));

        SampleGiAccuracyOracle[] oracles = SampleGlobalIlluminationValidation.AccuracyOracles
            .Where(oracle => oracle.Scenario == scenario)
            .ToArray();
        if (oracles.Length == 0)
            return Array.Empty<SampleGiAccuracyOracleResult>();

        var results = new List<SampleGiAccuracyOracleResult>(oracles.Length);
        foreach (SampleGiAccuracyOracle oracle in oracles)
            results.Add(EvaluateOracle(oracle, samples));
        return results;
    }

    private static SampleGiAccuracyOracleResult EvaluateOracle(
        SampleGiAccuracyOracle oracle,
        IReadOnlyList<RendererDiagnostics> samples)
    {
        float[] values = samples
            .Select(sample => ReadMetric(sample, oracle.Metric))
            .Where(float.IsFinite)
            .ToArray();
        if (values.Length == 0)
        {
            return Result(
                oracle,
                SampleGiAccuracyOracleStatus.InsufficientData,
                measuredValue: 0.0f,
                referenceValue: oracle.ReferenceValue,
                relativeError: float.PositiveInfinity,
                latencyFrames: null,
                detail: "metric samples unavailable");
        }

        float measured = AverageTail(values);
        if (oracle.ReferenceValue.HasValue)
        {
            float reference = oracle.ReferenceValue.Value;
            float relativeError = MathF.Abs(measured - reference) / MathF.Max(MathF.Abs(reference), 0.0001f);
            return Result(
                oracle,
                relativeError <= oracle.MaximumRelativeError ? SampleGiAccuracyOracleStatus.Passed : SampleGiAccuracyOracleStatus.Failed,
                measured,
                reference,
                relativeError,
                latencyFrames: null,
                detail: $"measured={measured:F4}, reference={reference:F4}, relError={relativeError:F4}");
        }

        return oracle.Name switch
        {
            "simple-ddgi-light-toggle" => EvaluateResponsivenessOracle(
                oracle,
                samples,
                values,
                VulkanRenderer.SimpleDdgiDirtyReasonLight,
                "light dirty boost"),
            "simple-ddgi-emissive-panel" => EvaluateEmissiveOracle(oracle, samples, measured, values),
            "simple-ddgi-moving-occluder" => EvaluateResponsivenessOracle(
                oracle,
                samples,
                values,
                VulkanRenderer.SimpleDdgiDirtyReasonDynamicGeometry,
                "dynamic-geometry dirty boost"),
            _ => Result(
                oracle,
                measured > 0.0001f ? SampleGiAccuracyOracleStatus.Passed : SampleGiAccuracyOracleStatus.Failed,
                measured,
                referenceValue: null,
                relativeError: 0.0f,
                latencyFrames: null,
                detail: $"finite nonzero reference metric measured={measured:F4}")
        };
    }

    private static SampleGiAccuracyOracleResult EvaluateResponsivenessOracle(
        SampleGiAccuracyOracle oracle,
        IReadOnlyList<RendererDiagnostics> samples,
        float[] values,
        uint requiredDirtyReason,
        string dirtyDetail)
    {
        bool dirtyObserved = samples.Any(sample => (sample.SimpleDdgiDirtyReasonFlags & requiredDirtyReason) != 0);
        bool boostObserved = samples.Any(sample => sample.SimpleDdgiLightingDirtyBoostedCapacity > 0);
        int latencyFrames = EstimateLatencyFrames(values);
        bool latencyValid = !oracle.MaximumLatencyFrames.HasValue || latencyFrames <= oracle.MaximumLatencyFrames.Value;
        SampleGiAccuracyOracleStatus status = dirtyObserved && boostObserved && latencyValid
            ? SampleGiAccuracyOracleStatus.Passed
            : SampleGiAccuracyOracleStatus.Failed;
        return Result(
            oracle,
            status,
            AverageTail(values),
            referenceValue: null,
            relativeError: 0.0f,
            latencyFrames,
            detail: $"{dirtyDetail}: dirtyObserved={dirtyObserved}, boostObserved={boostObserved}, latencyFrames={latencyFrames}");
    }

    private static SampleGiAccuracyOracleResult EvaluateEmissiveOracle(
        SampleGiAccuracyOracle oracle,
        IReadOnlyList<RendererDiagnostics> samples,
        float measured,
        float[] values)
    {
        RendererDiagnostics last = samples[^1];
        bool multiEmitter = last.DdgiEmissiveSourceCount >= 2;
        bool emissiveHits = values.Any(value => value > 0.0f);
        bool dirtyObserved = samples.Any(sample => (sample.SimpleDdgiDirtyReasonFlags & VulkanRenderer.SimpleDdgiDirtyReasonEmissive) != 0) ||
            last.DdgiEmissiveSourceRevision > 0;
        SampleGiAccuracyOracleStatus status = multiEmitter && emissiveHits && dirtyObserved
            ? SampleGiAccuracyOracleStatus.Passed
            : SampleGiAccuracyOracleStatus.Failed;
        return Result(
            oracle,
            status,
            measured,
            referenceValue: null,
            relativeError: 0.0f,
            latencyFrames: oracle.MaximumLatencyFrames.HasValue ? EstimateLatencyFrames(values) : null,
            detail: $"multiEmitter={multiEmitter}, emissiveHits={emissiveHits}, dirtyObserved={dirtyObserved}");
    }

    private static float ReadMetric(RendererDiagnostics diagnostics, string metric) =>
        metric switch
        {
            "SimpleDdgiAverageSampledIrradianceLuminance" => diagnostics.SimpleDdgiAverageSampledIrradianceLuminance,
            "SimpleDdgiAverageVisibility" => diagnostics.SimpleDdgiAverageVisibility,
            "DdgiSimpleTraceEmissiveHitCount" => diagnostics.DdgiSimpleTraceEmissiveHitCount,
            _ => float.NaN
        };

    private static float AverageTail(float[] values)
    {
        int start = values.Length / 2;
        float sum = 0.0f;
        int count = 0;
        for (int i = start; i < values.Length; i++)
        {
            sum += values[i];
            count++;
        }

        return count == 0 ? 0.0f : sum / count;
    }

    private static int EstimateLatencyFrames(float[] values)
    {
        if (values.Length < 2)
            return 0;

        float first = values[0];
        float last = values[^1];
        float delta = last - first;
        if (MathF.Abs(delta) <= MathF.Max(MathF.Abs(last), 1.0f) * 0.01f)
            return 0;

        float target = first + delta * 0.9f;
        for (int i = 0; i < values.Length; i++)
        {
            if (delta > 0.0f && values[i] >= target)
                return i;
            if (delta < 0.0f && values[i] <= target)
                return i;
        }

        return values.Length;
    }

    private static SampleGiAccuracyOracleResult Result(
        SampleGiAccuracyOracle oracle,
        SampleGiAccuracyOracleStatus status,
        float measuredValue,
        float? referenceValue,
        float relativeError,
        int? latencyFrames,
        string detail) =>
        new(
            oracle.Name,
            oracle.Scenario,
            oracle.Metric,
            status,
            measuredValue,
            referenceValue,
            relativeError,
            latencyFrames,
            detail);
}
