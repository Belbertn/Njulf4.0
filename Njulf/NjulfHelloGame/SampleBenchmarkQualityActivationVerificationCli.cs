using System.Text.Json;
using System.Text.Json.Serialization;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkQualityActivationVerificationResult(
    string Kind,
    string Schema,
    bool Passed,
    string ReportPath,
    string ReportSha256,
    string SequenceId,
    SampleBenchmarkQualitySequenceRole Role,
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
        "njulf-benchmark-quality-activation-verification";
    public const string CurrentSchema =
        "njulf-benchmark-quality-activation-verification/v1";
}

internal static class SampleBenchmarkQualityActivationEvidenceValidator
{
    public static IReadOnlyList<string> Validate(
        SampleBenchmarkQualitySequenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();
        if (!string.Equals(
                report.Kind,
                SampleBenchmarkQualitySequenceReport.CurrentKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Schema,
                SampleBenchmarkQualitySequenceReport.CurrentSchema,
                StringComparison.Ordinal) ||
            report.TimingEligible || report.ProductionTiming ||
            !report.Passed || report.Failures is not { Count: 0 })
        {
            failures.Add(
                "Quality report header, timing eligibility, or result is invalid.");
        }
        if (!Enum.IsDefined(report.Role) ||
            string.IsNullOrWhiteSpace(report.SequenceId))
        {
            failures.Add("Quality sequence role or identifier is noncanonical.");
        }

        SampleBenchmarkTrajectoryKind trajectory;
        try
        {
            trajectory = SampleBenchmarkTrajectory.Parse(report.Trajectory);
            if (!string.Equals(
                    report.Trajectory,
                    SampleBenchmarkTrajectory.GetName(trajectory),
                    StringComparison.Ordinal) ||
                report.TrajectoryFrameCount !=
                    SampleBenchmarkTrajectory.GetFrameCount(trajectory))
            {
                throw new InvalidDataException(
                    "Quality trajectory name or frame count is noncanonical.");
            }
            bool sceneMatches = SampleBenchmarkTrajectory.RequiresSponza(
                    trajectory)
                ? string.Equals(
                    report.SceneKind,
                    "Sponza",
                    StringComparison.Ordinal)
                : SampleBenchmarkTrajectory.RequiresBistro(trajectory)
                    ? string.Equals(
                        report.SceneKind,
                        "Bistro",
                        StringComparison.Ordinal)
                    : !string.IsNullOrWhiteSpace(report.SceneKind);
            if (!sceneMatches ||
                !IsSha256Identity(report.TrajectoryFingerprint) ||
                !IsSha256Identity(report.TrajectoryRouteHash) ||
                !IsSha256Identity(report.TrajectorySequenceHash) ||
                !string.Equals(
                    report.CheckpointContractFingerprint,
                    SampleBenchmarkQualityCheckpointCatalog.CreateFingerprint(
                        trajectory),
                    StringComparison.Ordinal) ||
                report.FirstRouteAbsoluteFrameIndex < 0)
            {
                throw new InvalidDataException(
                    "Quality route, scene, or checkpoint identity is invalid.");
            }
            if (SampleBenchmarkTrajectory.RequiresSponza(trajectory) &&
                (!string.Equals(
                    report.TrajectoryFingerprint,
                    SampleBenchmarkTrajectory.CreateFingerprint(
                        trajectory,
                        SampleBistroQualityCaptureVariant.SunScaleStep),
                    StringComparison.Ordinal) ||
                 !string.Equals(
                     report.TrajectoryRouteHash,
                     SampleBenchmarkTrajectory.CreateRouteHash(
                         trajectory,
                         SampleBistroQualityCaptureVariant.SunScaleStep),
                     StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "Quality Sponza trajectory fingerprint or authored route " +
                    "hash changed.");
            }
            string activation = SampleBenchmarkActivation.Normalize(
                report.Activation);
            string variant = SampleBenchmarkCaptureVariant.Normalize(
                report.CaptureVariant);
            if (!string.Equals(
                    report.Activation,
                    activation,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.CaptureVariant,
                    variant,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.ActivationFingerprint,
                    SampleBenchmarkActivation.CreateFingerprint(activation),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Quality activation or capture variant is noncanonical.");
            }
            if (!Enum.TryParse(
                    report.Scenario,
                    ignoreCase: false,
                    out SamplePerformanceScenario scenario) ||
                !string.Equals(
                    report.Scenario,
                    scenario.ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Quality activation scenario is noncanonical.");
            }
            SampleBenchmarkActivation.Validate(
                activation,
                scenario,
                trajectory,
                variant,
                report.TrajectoryFrameCount,
                qualitySequence: true);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                OverflowException)
        {
            failures.Add(
                "Quality workload identity failed: " + exception.Message);
            return Array.AsReadOnly(failures.ToArray());
        }

        IReadOnlyList<SampleBenchmarkActivationFrameState> animationFrames =
            ValidateCommonAnimation(report, trajectory, failures);
        ValidateCheckpoints(
            report,
            trajectory,
            animationFrames,
            failures);

        SampleBenchmarkActivationEvidence? activationEvidence =
            report.ActivationEvidence;
        if (activationEvidence == null)
        {
            failures.Add("Quality activation evidence is null.");
        }
        else
        {
            try
            {
                foreach (string failure in
                         SampleBenchmarkActivationEvidenceValidator.Validate(
                             activationEvidence,
                             report.Activation,
                             report.CaptureVariant,
                             report.TrajectoryFrameCount,
                             qualitySequence: true,
                             trajectory,
                             SampleBenchmarkActivation
                                 .RequiresDeterministicAnimation(
                                     report.Activation)
                                ? animationFrames
                                : Array.Empty<
                                    SampleBenchmarkActivationFrameState>()))
                {
                    failures.Add("Activation evidence: " + failure);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or
                    OverflowException)
            {
                failures.Add(
                    "Quality activation recomputation failed: " +
                    exception.Message);
            }
            if (!string.Equals(
                    activationEvidence.Fingerprint,
                    report.ActivationFingerprint,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "Quality activation evidence fingerprint differs from " +
                    "the top-level workload identity.");
            }
        }
        return Array.AsReadOnly(
            failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsSha256Identity(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;

    private static IReadOnlyList<SampleBenchmarkActivationFrameState>
        ValidateCommonAnimation(
            SampleBenchmarkQualitySequenceReport report,
            SampleBenchmarkTrajectoryKind trajectory,
            ICollection<string> failures)
    {
        SampleBenchmarkSponzaSceneAnimationEvidence? evidence =
            report.SponzaSceneAnimationEvidence;
        if (!SampleBenchmarkTrajectory.RequiresSponza(trajectory))
        {
            if (!SampleBenchmarkSponzaSceneAnimationEvidence
                    .IsCanonicalUnavailable(evidence))
            {
                failures.Add(
                    "A non-Sponza quality report lacks canonical unavailable " +
                    "scene-animation evidence.");
            }
            return Array.Empty<SampleBenchmarkActivationFrameState>();
        }
        if (evidence == null)
        {
            failures.Add("Sponza quality scene-animation evidence is null.");
            return Array.Empty<SampleBenchmarkActivationFrameState>();
        }

        SampleBenchmarkSponzaSceneAnimationMode expectedMode =
            SampleBenchmarkSponzaSceneAnimationContract.ResolveMode(
                report.Activation);
        if (!string.Equals(
                evidence.Schema,
                SampleBenchmarkSponzaSceneAnimationEvidence.CurrentSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.Fingerprint,
                SampleBenchmarkSponzaSceneAnimationContract.Fingerprint,
                StringComparison.Ordinal) ||
            evidence.Mode != expectedMode || !evidence.Passed ||
            evidence.SampleCount != report.TrajectoryFrameCount ||
            evidence.Failures is not { Count: 0 })
        {
            failures.Add(
                "Sponza quality scene-animation header or result is invalid.");
            return Array.Empty<SampleBenchmarkActivationFrameState>();
        }
        try
        {
            string path = Path.GetFullPath(evidence.SidecarPath);
            if (!string.Equals(
                    path,
                    evidence.SidecarPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Sponza quality animation sidecar path is noncanonical.");
            }
            return SampleBenchmarkSponzaSceneAnimationSidecar.Read(
                path,
                evidence.SidecarSha256,
                expectedMode,
                report.TrajectoryFrameCount,
                evidence.ConfigurationFingerprint,
                evidence.SequenceHash).Frames;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
                InvalidDataException or UnauthorizedAccessException or
                OverflowException)
        {
            failures.Add(
                "Sponza quality animation sidecar admission failed: " +
                exception.Message);
            return Array.Empty<SampleBenchmarkActivationFrameState>();
        }
    }

    private static void ValidateCheckpoints(
        SampleBenchmarkQualitySequenceReport report,
        SampleBenchmarkTrajectoryKind trajectory,
        IReadOnlyList<SampleBenchmarkActivationFrameState> animationFrames,
        ICollection<string> failures)
    {
        if (report.CheckpointIndices == null || report.Checkpoints == null)
        {
            failures.Add("Quality checkpoint evidence collections are null.");
            return;
        }
        try
        {
            SampleBenchmarkQualityCheckpointCatalog.RequireExactCheckpointOrder(
                trajectory,
                report.CheckpointIndices,
                "Quality activation verification");
        }
        catch (InvalidDataException exception)
        {
            failures.Add(exception.Message);
            return;
        }
        if (report.Checkpoints.Count != report.CheckpointIndices.Count)
        {
            failures.Add("Quality checkpoint evidence is incomplete.");
            return;
        }

        bool directional = SampleBenchmarkActivation
            .RequiresDeterministicAnimation(report.Activation);
        ulong[]? firstAbsoluteRevisions = null;
        for (int index = 0; index < report.Checkpoints.Count; index++)
        {
            SampleBenchmarkQualityCheckpointEvidence? checkpoint =
                report.Checkpoints[index];
            if (checkpoint == null || checkpoint.Ordinal != index ||
                checkpoint.RouteFrameIndex != report.CheckpointIndices[index])
            {
                failures.Add(
                    $"Quality checkpoint {index} is null, reordered, or " +
                    "mislabeled.");
                continue;
            }
            if (!directional)
            {
                if (checkpoint.ActivationFrameState != null)
                {
                    failures.Add(
                        $"Quality checkpoint {index} contains unexpected " +
                        "directional animation state.");
                }
                continue;
            }
            if ((uint)checkpoint.RouteFrameIndex >=
                    (uint)animationFrames.Count ||
                checkpoint.ActivationFrameState == null)
            {
                failures.Add(
                    $"Directional quality checkpoint {index} lacks its " +
                    "authenticated animation frame.");
                continue;
            }
            SampleBenchmarkActivationFrameState actual =
                checkpoint.ActivationFrameState;
            SampleBenchmarkActivationFrameState expected =
                animationFrames[checkpoint.RouteFrameIndex];
            try
            {
                SampleBenchmarkActivationFrameState.ValidateCanonical(
                    actual,
                    checkpoint.RouteFrameIndex);
                if (firstAbsoluteRevisions == null)
                {
                    if (checkpoint.RouteFrameIndex != 0)
                    {
                        throw new InvalidDataException(
                            "Directional checkpoint evidence does not begin " +
                            "at route frame zero.");
                    }
                    firstAbsoluteRevisions = actual.Animators
                        .Select(static animator => animator.PoseRevision)
                        .ToArray();
                }
                RequireFrameMatchesSidecar(
                    actual,
                    expected,
                    firstAbsoluteRevisions);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or OverflowException)
            {
                failures.Add(
                    $"Directional quality checkpoint {index} differs from " +
                    $"its sidecar: {exception.Message}");
            }
        }
    }

    private static void RequireFrameMatchesSidecar(
        SampleBenchmarkActivationFrameState actual,
        SampleBenchmarkActivationFrameState expected,
        IReadOnlyList<ulong> firstAbsoluteRevisions)
    {
        if (!string.Equals(
                actual.ConfigurationFingerprint,
                expected.ConfigurationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                actual.FrameHash,
                expected.FrameHash,
                StringComparison.Ordinal) ||
            actual.Animators.Count != expected.Animators.Count ||
            actual.Animators.Count != firstAbsoluteRevisions.Count)
        {
            throw new InvalidDataException(
                "animation frame identity or topology changed.");
        }
        for (int index = 0; index < actual.Animators.Count; index++)
        {
            SampleBenchmarkActivationAnimatorState left =
                actual.Animators[index];
            SampleBenchmarkActivationAnimatorState right =
                expected.Animators[index];
            ulong revision = checked(
                firstAbsoluteRevisions[index] + right.PoseRevision);
            if (!string.Equals(left.Identity, right.Identity, StringComparison.Ordinal) ||
                !string.Equals(left.ClipName, right.ClipName, StringComparison.Ordinal) ||
                BitConverter.SingleToInt32Bits(left.ClipDurationSeconds) !=
                    BitConverter.SingleToInt32Bits(right.ClipDurationSeconds) ||
                BitConverter.SingleToInt32Bits(left.TimeSeconds) !=
                    BitConverter.SingleToInt32Bits(right.TimeSeconds) ||
                left.PoseRevision != revision ||
                left.JointCount != right.JointCount ||
                left.SkinCount != right.SkinCount ||
                !string.Equals(left.PoseHash, right.PoseHash, StringComparison.Ordinal) ||
                !left.GlobalMatrixComponentBits.SequenceEqual(
                    right.GlobalMatrixComponentBits))
            {
                throw new InvalidDataException(
                    $"animator {index} pose, phase, or relative revision " +
                    "changed.");
            }
        }
    }
}

/// <summary>
/// Frozen-original-build early exit for quality-only activation semantics. It
/// recomputes activation aggregates and common Sponza animation identities
/// from the persisted raw report and compact sidecar without initializing a
/// window or renderer.
/// </summary>
public static class SampleBenchmarkQualityActivationVerificationCli
{
    public const string VerifyOption =
        "--verify-benchmark-quality-activation-report";

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
                    $"'{VerifyOption} <quality-report.json>'.");
            }

            string path = Path.GetFullPath(args[1]);
            SampleEvidenceFileContent admitted = ReadReport(path);
            SampleBenchmarkQualitySequenceReport report =
                Deserialize(admitted);
            var failures = new List<string>(
                SampleBenchmarkQualityActivationEvidenceValidator.Validate(
                    report));

            SampleEvidenceFileContent finalReport = ReadReport(path);
            if (!string.Equals(
                    finalReport.Sha256,
                    admitted.Sha256,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "Quality report changed during activation verification.");
            }
            SampleBenchmarkQualitySequenceReport finalParsed =
                Deserialize(finalReport);
            foreach (string failure in
                     SampleBenchmarkQualityActivationEvidenceValidator.Validate(
                         finalParsed))
            {
                if (!failures.Contains(failure, StringComparer.Ordinal))
                    failures.Add(failure);
            }

            SampleBenchmarkActivationEvidence activation =
                finalParsed.ActivationEvidence ??
                SampleBenchmarkActivationEvidence.Unavailable;
            SampleBenchmarkSponzaSceneAnimationEvidence animation =
                finalParsed.SponzaSceneAnimationEvidence ??
                SampleBenchmarkSponzaSceneAnimationEvidence.Unavailable;
            string[] distinct = failures
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var result =
                new SampleBenchmarkQualityActivationVerificationResult(
                    SampleBenchmarkQualityActivationVerificationResult
                        .CurrentKind,
                    SampleBenchmarkQualityActivationVerificationResult
                        .CurrentSchema,
                    distinct.Length == 0,
                    finalReport.Path,
                    finalReport.Sha256,
                    finalParsed.SequenceId,
                    finalParsed.Role,
                    finalParsed.Activation,
                    finalParsed.ActivationFingerprint,
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
                InvalidDataException or NullReferenceException or
                UnauthorizedAccessException or OverflowException)
        {
            error.WriteLine(
                "Benchmark quality activation verification failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }
        return true;
    }

    private static SampleEvidenceFileContent ReadReport(string path)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark quality activation-verification report input");
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            ReadOptions.MaxDepth,
            "Benchmark quality activation-verification report input");
        return evidence;
    }

    private static SampleBenchmarkQualitySequenceReport Deserialize(
        SampleEvidenceFileContent evidence) =>
        JsonSerializer.Deserialize<SampleBenchmarkQualitySequenceReport>(
            evidence.Bytes,
            ReadOptions) ??
        throw new InvalidDataException(
            "Benchmark quality activation-verification report deserialized " +
            "to null.");
}
