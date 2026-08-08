using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkPairMetric(
    string Name,
    double BaselineP95Milliseconds,
    double VariantP95Milliseconds,
    double DeltaMilliseconds,
    double RelativeDifference,
    bool WithinTolerance);

public sealed record SampleBenchmarkPairedTimingEstimate(
    string Name,
    GiTimingAttribution Attribution,
    double EnabledP95Milliseconds,
    double DisabledP95Milliseconds,
    double SignedDeltaMilliseconds,
    double IncrementalP95Milliseconds);

public sealed record SampleBenchmarkPairComparison(
    bool Comparable,
    bool RepeatabilityPassed,
    double P95Tolerance,
    IReadOnlyList<SampleBenchmarkPairMetric> Metrics,
    IReadOnlyList<string> Failures)
{
    public SampleBenchmarkPairedTimingEstimate? ForwardGiGatherEstimate { get; init; }
}

/// <summary>
/// Fail-closed comparison for repeated runs and deterministic A/B variants.
/// Identity excludes the variant label, so a decal/far-field switch can share a
/// pair while camera, scene, executable, shader, hardware, and GI state remain
/// byte-for-byte locked.
/// </summary>
public static class SampleBenchmarkPairComparer
{
    private const string ForwardGiGatherPassName = "ForwardGiGatherPass";
    public const double DefaultP95Tolerance = 0.05;
    // The performance profile gives the GPU 10 ms. A pass below 0.50 ms cannot
    // consume five percent of that budget and is therefore reported, but is not
    // classified as a "major pass" for the plan's per-pass repeatability gate.
    // CPU frame and GPU frame remain mandatory regardless of duration.
    public const double MinimumMajorPassP95Milliseconds = 0.50;

    public static SampleBenchmarkPairComparison Compare(
        SampleBenchmarkReport baseline,
        SampleBenchmarkReport variant,
        double p95Tolerance = DefaultP95Tolerance,
        bool requireRepeatability = true)
    {
        if (baseline == null)
            throw new ArgumentNullException(nameof(baseline));
        if (variant == null)
            throw new ArgumentNullException(nameof(variant));
        if (!double.IsFinite(p95Tolerance) || p95Tolerance < 0.0)
            throw new ArgumentOutOfRangeException(nameof(p95Tolerance));

        var failures = new List<string>();
        SampleBenchmarkCaptureContract left = baseline.CaptureContract;
        SampleBenchmarkCaptureContract right = variant.CaptureContract;
        if (!left.Comparable)
            failures.Add("The baseline capture contract is not comparable.");
        if (!right.Comparable)
            failures.Add("The variant capture contract is not comparable.");
        if (string.IsNullOrWhiteSpace(left.PairId) ||
            !string.Equals(left.PairId, right.PairId, StringComparison.Ordinal))
        {
            failures.Add("Capture pair IDs are missing or different.");
        }
        if (!string.Equals(left.IdentityHash, right.IdentityHash, StringComparison.Ordinal))
            failures.Add("Locked capture identities differ.");
        if (requireRepeatability)
        {
            if (!string.Equals(left.Variant, right.Variant, StringComparison.Ordinal))
                failures.Add("Repeat captures use different variant labels.");
            if (string.IsNullOrWhiteSpace(left.FullIdentityHash) ||
                string.Equals(left.FullIdentityHash, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    left.FullIdentityHash,
                    right.FullIdentityHash,
                    StringComparison.Ordinal))
            {
                failures.Add("Repeat captures differ in exact rendered state.");
            }
        }

        var metrics = new List<SampleBenchmarkPairMetric>
        {
            CompareMetric(
                "CPU frame",
                baseline.CpuFrameMilliseconds.P95Milliseconds,
                variant.CpuFrameMilliseconds.P95Milliseconds,
                p95Tolerance),
            CompareMetric(
                "GPU frame",
                baseline.GpuFrameMilliseconds.P95Milliseconds,
                variant.GpuFrameMilliseconds.P95Milliseconds,
                p95Tolerance)
        };

        Dictionary<string, SampleBenchmarkTimingStats> leftPasses =
            baseline.GpuPasses.ToDictionary(static pass => pass.Name, StringComparer.Ordinal);
        Dictionary<string, SampleBenchmarkTimingStats> rightPasses =
            variant.GpuPasses.ToDictionary(static pass => pass.Name, StringComparer.Ordinal);
        string[] passNames = leftPasses.Keys
            .Concat(rightPasses.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        foreach (string passName in passNames)
        {
            bool hasLeft = leftPasses.TryGetValue(
                passName,
                out SampleBenchmarkTimingStats? leftPass);
            bool hasRight = rightPasses.TryGetValue(
                passName,
                out SampleBenchmarkTimingStats? rightPass);
            if (requireRepeatability && (!hasLeft || !hasRight))
            {
                double presentP95 = hasLeft
                    ? leftPass!.P95Milliseconds
                    : rightPass!.P95Milliseconds;
                if (presentP95 >= MinimumMajorPassP95Milliseconds)
                {
                    failures.Add(
                        !hasLeft
                            ? $"Baseline is missing GPU pass '{passName}' present in the repeat."
                            : $"Repeat is missing baseline GPU pass '{passName}'.");
                }
                metrics.Add(ComparePassMetric(
                    passName,
                    hasLeft ? leftPass!.P95Milliseconds : 0.0,
                    hasRight ? rightPass!.P95Milliseconds : 0.0,
                    p95Tolerance));
                continue;
            }

            metrics.Add(ComparePassMetric(
                passName,
                hasLeft ? leftPass!.P95Milliseconds : 0.0,
                hasRight ? rightPass!.P95Milliseconds : 0.0,
                p95Tolerance));
        }

        bool repeatabilityPassed = metrics.All(static metric => metric.WithinTolerance);
        if (requireRepeatability && !repeatabilityPassed)
        {
            foreach (SampleBenchmarkPairMetric metric in metrics.Where(
                         static metric => !metric.WithinTolerance))
            {
                failures.Add(
                    $"{metric.Name} P95 differs by {metric.RelativeDifference:P2}, " +
                    $"above the {p95Tolerance:P2} tolerance.");
            }
        }

        SampleBenchmarkPairedTimingEstimate? forwardGiEstimate =
            BuildForwardGiGatherEstimate(baseline, variant, failures);
        return new SampleBenchmarkPairComparison(
            failures.Count == 0,
            repeatabilityPassed,
            p95Tolerance,
            Array.AsReadOnly(metrics.ToArray()),
            Array.AsReadOnly(failures.Distinct(StringComparer.Ordinal).ToArray()))
        {
            ForwardGiGatherEstimate = forwardGiEstimate
        };
    }

    private static SampleBenchmarkPairMetric CompareMetric(
        string name,
        double baseline,
        double variant,
        double tolerance)
    {
        double denominator = Math.Max(Math.Max(Math.Abs(baseline), Math.Abs(variant)), 1e-9);
        double relative = Math.Abs(variant - baseline) / denominator;
        return new SampleBenchmarkPairMetric(
            name,
            baseline,
            variant,
            variant - baseline,
            relative,
            relative <= tolerance);
    }

    private static SampleBenchmarkPairMetric ComparePassMetric(
        string name,
        double baseline,
        double variant,
        double tolerance)
    {
        SampleBenchmarkPairMetric metric = CompareMetric(
            name,
            baseline,
            variant,
            tolerance);
        return Math.Max(Math.Abs(baseline), Math.Abs(variant)) <
            MinimumMajorPassP95Milliseconds
                ? metric with { WithinTolerance = true }
                : metric;
    }

    private static bool IsForwardGiPair(
        string leftVariant,
        string rightVariant)
    {
        bool leftEnabled = string.Equals(
            leftVariant,
            SampleBenchmarkCaptureVariant.ForwardGiEnabled,
            StringComparison.OrdinalIgnoreCase);
        bool leftDisabled = string.Equals(
            leftVariant,
            SampleBenchmarkCaptureVariant.ForwardGiDisabled,
            StringComparison.OrdinalIgnoreCase);
        bool rightEnabled = string.Equals(
            rightVariant,
            SampleBenchmarkCaptureVariant.ForwardGiEnabled,
            StringComparison.OrdinalIgnoreCase);
        bool rightDisabled = string.Equals(
            rightVariant,
            SampleBenchmarkCaptureVariant.ForwardGiDisabled,
            StringComparison.OrdinalIgnoreCase);
        return (leftEnabled && rightDisabled) || (leftDisabled && rightEnabled);
    }

    private static SampleBenchmarkPairedTimingEstimate? BuildForwardGiGatherEstimate(
        SampleBenchmarkReport left,
        SampleBenchmarkReport right,
        ICollection<string> failures)
    {
        if (!IsForwardGiPair(
                left.CaptureContract.Variant,
                right.CaptureContract.Variant))
        {
            return null;
        }

        SampleBenchmarkReport enabled = string.Equals(
            left.CaptureContract.Variant,
            SampleBenchmarkCaptureVariant.ForwardGiEnabled,
            StringComparison.OrdinalIgnoreCase)
                ? left
                : right;
        SampleBenchmarkReport disabled = ReferenceEquals(enabled, left) ? right : left;
        SampleBenchmarkTimingStats? enabledTiming = enabled.GpuPasses.FirstOrDefault(
            static pass => string.Equals(
                pass.Name,
                ForwardGiGatherPassName,
                StringComparison.Ordinal));
        SampleBenchmarkTimingStats? disabledTiming = disabled.GpuPasses.FirstOrDefault(
            static pass => string.Equals(
                pass.Name,
                ForwardGiGatherPassName,
                StringComparison.Ordinal));
        if (enabledTiming == null || disabledTiming == null)
        {
            failures.Add(
                $"A forward-GI timing pair requires '{ForwardGiGatherPassName}' " +
                "in both reports.");
            return null;
        }

        double signedDelta = enabledTiming.P95Milliseconds -
            disabledTiming.P95Milliseconds;
        return new SampleBenchmarkPairedTimingEstimate(
            "Forward GI gather P95 difference",
            GiTimingAttribution.PairedEstimate,
            enabledTiming.P95Milliseconds,
            disabledTiming.P95Milliseconds,
            signedDelta,
            Math.Max(0.0, signedDelta));
    }
}

/// <summary>
/// Standalone comparison command. Repeat mode enforces the five-percent P95
/// gate; --benchmark-pair-ab retains identity validation but permits intentional
/// timing movement between isolated variants.
/// </summary>
public static class SampleBenchmarkPairComparisonCli
{
    public const string CompareOption = "--compare-benchmark-pair";
    public const string AbOption = "--benchmark-pair-ab";
    public const string ReportOption = "--benchmark-pair-report";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true
    };

    public static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        exitCode = 0;
        int compareIndex = Array.FindIndex(
            args,
            static argument => string.Equals(argument, CompareOption, StringComparison.Ordinal));
        if (compareIndex < 0)
            return false;

        try
        {
            if (Array.FindLastIndex(
                    args,
                    static argument => string.Equals(argument, CompareOption, StringComparison.Ordinal)) !=
                compareIndex || compareIndex + 2 >= args.Length)
            {
                throw new ArgumentException(
                    $"{CompareOption} must appear once and requires <baseline.json> <variant.json>.");
            }

            string baselinePath = RequireValue(args[compareIndex + 1], "baseline report");
            string variantPath = RequireValue(args[compareIndex + 2], "variant report");
            bool abComparison = false;
            string? reportPath = null;
            var consumed = new HashSet<int>
            {
                compareIndex,
                compareIndex + 1,
                compareIndex + 2
            };
            for (int index = 0; index < args.Length; index++)
            {
                if (consumed.Contains(index))
                    continue;
                string argument = args[index];
                if (string.Equals(argument, AbOption, StringComparison.Ordinal))
                {
                    if (abComparison)
                        throw new ArgumentException($"{AbOption} may be specified only once.");
                    abComparison = true;
                    consumed.Add(index);
                    continue;
                }
                if (argument.StartsWith(ReportOption + "=", StringComparison.Ordinal))
                {
                    if (reportPath != null)
                        throw new ArgumentException($"{ReportOption} may be specified only once.");
                    reportPath = RequireValue(
                        argument[(ReportOption.Length + 1)..],
                        "pair-comparison report");
                    consumed.Add(index);
                    continue;
                }
                if (string.Equals(argument, ReportOption, StringComparison.Ordinal))
                {
                    if (reportPath != null || index + 1 >= args.Length)
                        throw new ArgumentException($"{ReportOption} requires one path.");
                    reportPath = RequireValue(args[index + 1], "pair-comparison report");
                    consumed.Add(index);
                    consumed.Add(index + 1);
                    index++;
                    continue;
                }
                throw new ArgumentException(
                    $"{CompareOption} is standalone and cannot be combined with '{argument}'.");
            }

            SampleBenchmarkReport baseline = Load(baselinePath, "baseline benchmark report");
            SampleBenchmarkReport variant = Load(variantPath, "variant benchmark report");
            SampleBenchmarkPairComparison comparison = SampleBenchmarkPairComparer.Compare(
                baseline,
                variant,
                requireRepeatability: !abComparison);
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                SampleEvidenceFileIo.WriteAtomic(
                    Path.GetFullPath(reportPath),
                    JsonSerializer.SerializeToUtf8Bytes(comparison, JsonOptions),
                    SampleEvidenceFileIo.MaximumJsonBytes,
                    "Benchmark pair-comparison report");
            }

            if (comparison.Comparable)
            {
                output.WriteLine(
                    $"Benchmark {(abComparison ? "A/B" : "repeatability")} comparison passed: " +
                    $"metrics={comparison.Metrics.Count} tolerance={comparison.P95Tolerance:P2}.");
                if (comparison.ForwardGiGatherEstimate is { } forwardGi)
                {
                    output.WriteLine(
                        $"Forward GI {forwardGi.Attribution}: " +
                        $"enabledP95={forwardGi.EnabledP95Milliseconds:F3}ms " +
                        $"disabledP95={forwardGi.DisabledP95Milliseconds:F3}ms " +
                        $"incrementalP95={forwardGi.IncrementalP95Milliseconds:F3}ms.");
                }
                exitCode = 0;
            }
            else
            {
                error.WriteLine(
                    "Benchmark pair comparison failed: " +
                    string.Join("; ", comparison.Failures));
                exitCode = 2;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                InvalidDataException or
                JsonException or
                UnauthorizedAccessException)
        {
            error.WriteLine(
                $"Benchmark pair comparison command failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 64;
            return true;
        }
    }

    private static SampleBenchmarkReport Load(string path, string role)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            Path.GetFullPath(path),
            SampleEvidenceFileIo.MaximumJsonBytes,
            role);
        SampleEvidenceFileIo.ValidateStrictJson(evidence.Bytes, JsonOptions.MaxDepth, role);
        SampleBenchmarkReport report =
            JsonSerializer.Deserialize<SampleBenchmarkReport>(evidence.Bytes, JsonOptions)
            ?? throw new InvalidDataException($"{role} deserialized to null.");
        if (!string.Equals(report.Kind, "njulf-renderer-benchmark", StringComparison.Ordinal) ||
            report.MeasurementFrameCount <= 0)
        {
            throw new InvalidDataException($"{role} is not a complete Njulf benchmark report.");
        }
        return report;
    }

    private static string RequireValue(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"A non-option {role} is required.");
        return value;
    }
}
