using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkDdgiTransientVerificationResult(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Schema,
    [property: JsonRequired] bool Passed,
    [property: JsonRequired] string ReportPath,
    [property: JsonRequired] string ReportSha256,
    [property: JsonRequired] long ReportByteLength,
    [property: JsonRequired] bool Applicable,
    [property: JsonRequired] bool Available,
    [property: JsonRequired] int RawRowCount,
    [property: JsonRequired] int RecomputedWindowCount,
    [property: JsonRequired] int RecomputedWindowFrameCount,
    [property: JsonRequired] string SemanticDigest,
    [property: JsonRequired] IReadOnlyList<string> Failures)
{
    public const string CurrentKind =
        "njulf-benchmark-ddgi-transient-verification";
    public const string CurrentSchema =
        "njulf-benchmark-ddgi-transient-verification/v1";
}

/// <summary>
/// Frozen-original-build early exit for independently verifying the inline
/// DDGI transient raw evidence and its derived windows. It never initializes
/// a window or renderer. The report is admitted and semantically verified
/// twice, and stdout describes only stable bytes.
/// </summary>
public static class SampleBenchmarkDdgiTransientVerificationCli
{
    public const string VerifyOption =
        "--verify-benchmark-ddgi-transient-report";

    private const string EvidenceRole =
        "Benchmark DDGI transient-verification report input";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode) =>
        TryRun(
            args,
            output,
            error,
            static path => SampleEvidenceFileIo.Read(
                path,
                SampleEvidenceFileIo.MaximumJsonBytes,
                EvidenceRole),
            out exitCode);

    /// <summary>
    /// Admission seam used only to prove mutation handling without racing the
    /// host filesystem. Strict JSON admission still runs inside this method
    /// for both delegate results.
    /// </summary>
    internal static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, SampleEvidenceFileContent> readReport,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(readReport);
        exitCode = 0;

        int index = Array.FindIndex(
            args,
            static argument => string.Equals(
                argument,
                VerifyOption,
                StringComparison.Ordinal));
        if (index < 0)
            return false;

        try
        {
            if (Array.FindLastIndex(
                    args,
                    static argument => string.Equals(
                        argument,
                        VerifyOption,
                        StringComparison.Ordinal)) != index ||
                args.Length != 2 || index != 0 ||
                string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException(
                    $"{VerifyOption} must appear once as " +
                    $"'{VerifyOption} <report.json>'.");
            }

            string path = Path.GetFullPath(args[1]);
            SampleEvidenceFileContent admitted =
                ReadAndValidate(path, readReport);
            SampleBenchmarkReport report = Deserialize(admitted);
            VerificationSnapshot initial = Verify(report);

            SampleEvidenceFileContent finalReport =
                ReadAndValidate(path, readReport);
            SampleBenchmarkReport finalParsed = Deserialize(finalReport);
            VerificationSnapshot final = Verify(finalParsed);

            var failures = new List<string>(initial.Failures);
            AppendDistinct(failures, final.Failures);
            if (!string.Equals(
                    admitted.Path,
                    path,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    finalReport.Path,
                    path,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    admitted.Path,
                    finalReport.Path,
                    StringComparison.Ordinal) ||
                admitted.Bytes.LongLength != finalReport.Bytes.LongLength ||
                !string.Equals(
                    admitted.Sha256,
                    finalReport.Sha256,
                    StringComparison.Ordinal) ||
                !admitted.Bytes.AsSpan().SequenceEqual(finalReport.Bytes))
            {
                AppendDistinct(
                    failures,
                    "Benchmark report changed during DDGI transient verification.");
            }

            string[] distinct = failures
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            bool passed = distinct.Length == 0;
            var result = new SampleBenchmarkDdgiTransientVerificationResult(
                SampleBenchmarkDdgiTransientVerificationResult.CurrentKind,
                SampleBenchmarkDdgiTransientVerificationResult.CurrentSchema,
                passed,
                finalReport.Path,
                finalReport.Sha256,
                finalReport.Bytes.LongLength,
                final.Applicable,
                final.Available,
                final.RawRowCount,
                final.RecomputedWindowCount,
                final.RecomputedWindowFrameCount,
                passed ? final.SemanticDigest : "unavailable",
                Array.AsReadOnly(distinct));
            output.WriteLine(JsonSerializer.Serialize(result, WriteOptions));
            exitCode = passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or JsonException or
                InvalidDataException or UnauthorizedAccessException or
                OverflowException)
        {
            error.WriteLine(
                "Benchmark DDGI transient verification failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }
        return true;
    }

    private static SampleEvidenceFileContent ReadAndValidate(
        string path,
        Func<string, SampleEvidenceFileContent> readReport)
    {
        SampleEvidenceFileContent evidence = readReport(path);
        if (evidence.Bytes is null || evidence.Bytes.Length == 0 ||
            evidence.Bytes.LongLength > SampleEvidenceFileIo.MaximumJsonBytes)
        {
            throw new InvalidDataException(
                $"{EvidenceRole} is empty or exceeds the 16 MiB bound.");
        }
        string expectedPath = Path.GetFullPath(path);
        if (!string.Equals(
                evidence.Path,
                expectedPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{EvidenceRole} returned a different canonical path.");
        }
        string computedSha = Convert.ToHexString(
                SHA256.HashData(evidence.Bytes))
            .ToLowerInvariant();
        if (!string.Equals(
                evidence.Sha256,
                computedSha,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{EvidenceRole} returned a hash that does not bind its bytes.");
        }
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            ReadOptions.MaxDepth,
            EvidenceRole);
        SampleBenchmarkDdgiTransientWireShape.Validate(evidence.Bytes);
        return evidence;
    }

    private static SampleBenchmarkReport Deserialize(
        SampleEvidenceFileContent evidence) =>
        JsonSerializer.Deserialize<SampleBenchmarkReport>(
            evidence.Bytes,
            ReadOptions) ??
        throw new InvalidDataException(
            "Benchmark DDGI transient-verification report deserialized to null.");

    private static VerificationSnapshot Verify(SampleBenchmarkReport report)
    {
        try
        {
            SampleBenchmarkDdgiTransientVerification verification =
                SampleBenchmarkDdgiTransientEvidenceEvaluator.Verify(report);
            SampleBenchmarkDdgiTransientEvidence recomputed =
                verification.RecomputedEvidence;
            IReadOnlyList<SampleBenchmarkDdgiTransientWindow>? windows =
                recomputed.Windows;
            var failures = new List<string>(
                verification.Failures ?? Array.Empty<string>());
            if (verification.Passed != (failures.Count == 0))
            {
                failures.Add(
                    "DDGI transient semantic verdict shape is noncanonical.");
            }
            if (failures.Count == 0 &&
                !IsSha256Identity(verification.SemanticDigest))
            {
                failures.Add(
                    "DDGI transient semantic digest is noncanonical.");
            }
            return new VerificationSnapshot(
                recomputed.Applicable,
                recomputed.Available,
                verification.RawRowCount,
                windows?.Count ?? 0,
                CountFrames(windows),
                failures.Count == 0
                    ? verification.SemanticDigest
                    : "unavailable",
                Array.AsReadOnly(failures.ToArray()));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                InvalidOperationException or JsonException or
                NotSupportedException or NullReferenceException or
                OverflowException)
        {
            bool applicable =
                report.DdgiTransientRawEvidence?.Applicable ?? false;
            return new VerificationSnapshot(
                applicable,
                Available: false,
                RawRowCount: report.DdgiTransientRawEvidence?.Frames?.Count ?? 0,
                RecomputedWindowCount: 0,
                RecomputedWindowFrameCount: 0,
                SemanticDigest: "unavailable",
                Failures:
                [
                    "DDGI transient semantic verification failed: " +
                    $"{exception.GetType().Name}: {exception.Message}"
                ]);
        }
    }

    private static int CountFrames(
        IReadOnlyList<SampleBenchmarkDdgiTransientWindow>? windows)
    {
        if (windows is null)
            return 0;
        int count = 0;
        foreach (SampleBenchmarkDdgiTransientWindow? window in windows)
            count = checked(count + (window?.Frames?.Count ?? 0));
        return count;
    }

    private static bool IsSha256Identity(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;

    private static void AppendDistinct(
        ICollection<string> failures,
        IEnumerable<string> additions)
    {
        foreach (string failure in additions)
            AppendDistinct(failures, failure);
    }

    private static void AppendDistinct(
        ICollection<string> failures,
        string failure)
    {
        if (!failures.Contains(failure, StringComparer.Ordinal))
            failures.Add(failure);
    }

    private sealed record VerificationSnapshot(
        bool Applicable,
        bool Available,
        int RawRowCount,
        int RecomputedWindowCount,
        int RecomputedWindowFrameCount,
        string SemanticDigest,
        IReadOnlyList<string> Failures);
}
