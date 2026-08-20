using System.Text.Json;
using System.Text.Json.Serialization;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkActivationVerificationResult(
    string Kind,
    string Schema,
    bool Passed,
    string ReportPath,
    string ReportSha256,
    string Activation,
    string ActivationFingerprint,
    string ActivationStructuralSequenceHash,
    string ActivationExecutionSequenceHash,
    string SponzaSceneAnimationFingerprint,
    SampleBenchmarkSponzaSceneAnimationMode SponzaSceneAnimationMode,
    string SponzaSceneAnimationConfigurationFingerprint,
    string SponzaSceneAnimationSequenceHash,
    string SponzaSceneAnimationSidecarPath,
    string SponzaSceneAnimationSidecarSha256,
    IReadOnlyList<string> Failures)
{
    public const string CurrentKind =
        "njulf-benchmark-activation-verification";
    public const string CurrentSchema =
        "njulf-benchmark-activation-verification/v1";
}

/// <summary>
/// Protected early-exit verifier used from the immutable original-baseline
/// build. It never initializes a window or renderer. The exact report and
/// compact animation sidecar are admitted twice around semantic recomputation
/// so stdout describes only stable bytes.
/// </summary>
public static class SampleBenchmarkActivationVerificationCli
{
    public const string VerifyOption =
        "--verify-benchmark-activation-report";

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
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
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
            SampleEvidenceFileContent admitted = ReadReportBytes(path);
            SampleBenchmarkReport report = Deserialize(admitted);
            var failures = new List<string>(
                SampleBenchmarkPairComparer.ValidateAuthenticatedEvidence(
                    report));

            SampleEvidenceFileContent finalReport = ReadReportBytes(path);
            if (!string.Equals(
                    finalReport.Sha256,
                    admitted.Sha256,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "benchmark report changed during activation verification.");
            }
            SampleBenchmarkReport finalParsed = Deserialize(finalReport);
            foreach (string failure in
                     SampleBenchmarkPairComparer.ValidateAuthenticatedEvidence(
                         finalParsed))
            {
                if (!failures.Contains(failure, StringComparer.Ordinal))
                    failures.Add(failure);
            }

            SampleBenchmarkActivationEvidence activation =
                finalParsed.ActivationEvidence;
            SampleBenchmarkSponzaSceneAnimationEvidence animation =
                finalParsed.SponzaSceneAnimationEvidence;
            string[] distinct = failures
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var result = new SampleBenchmarkActivationVerificationResult(
                SampleBenchmarkActivationVerificationResult.CurrentKind,
                SampleBenchmarkActivationVerificationResult.CurrentSchema,
                distinct.Length == 0,
                finalReport.Path,
                finalReport.Sha256,
                activation.Activation,
                activation.Fingerprint,
                activation.ActivationStructuralSequenceHash,
                activation.ActivationExecutionSequenceHash,
                animation.Fingerprint,
                animation.Mode,
                animation.ConfigurationFingerprint,
                animation.SequenceHash,
                animation.SidecarPath,
                animation.SidecarSha256,
                Array.AsReadOnly(distinct));
            output.WriteLine(JsonSerializer.Serialize(result, WriteOptions));
            exitCode = result.Passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or JsonException or
                InvalidDataException or UnauthorizedAccessException or
                OverflowException)
        {
            error.WriteLine(
                "Benchmark activation verification failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }
        return true;
    }

    private static SampleEvidenceFileContent ReadReportBytes(string path)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark activation-verification report input");
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            ReadOptions.MaxDepth,
            "Benchmark activation-verification report input");
        return evidence;
    }

    private static SampleBenchmarkReport Deserialize(
        SampleEvidenceFileContent evidence) =>
        JsonSerializer.Deserialize<SampleBenchmarkReport>(
            evidence.Bytes,
            ReadOptions) ??
        throw new InvalidDataException(
            "Benchmark activation-verification report deserialized to null.");
}
