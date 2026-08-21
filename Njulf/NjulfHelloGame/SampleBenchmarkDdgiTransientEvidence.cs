using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkDdgiTransientRawFrame(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] int MeasurementSampleIndex,
    [property: JsonRequired] int RouteFrameIndex,
    [property: JsonRequired] ulong CaptureFrameSerial,
    [property: JsonRequired] int Active,
    [property: JsonRequired] uint SourceLightingGeneration,
    [property: JsonRequired] SimpleDdgiCompletedFrameEvidence CompletionObserved)
{
    public const string CurrentSchema =
        "njulf-benchmark-ddgi-transient-raw-frame/v1";
}

public sealed record SampleBenchmarkDdgiTransientRawEvidence(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] bool Applicable,
    [property: JsonRequired] int MeasurementFrameCount,
    [property: JsonRequired] IReadOnlyList<SampleBenchmarkDdgiTransientRawFrame> Frames)
{
    public const string CurrentSchema =
        MaterialGiReleaseEvidenceContract
            .BenchmarkDdgiTransientRawEvidenceSchema;

    public static SampleBenchmarkDdgiTransientRawEvidence NotApplicable { get; } =
        new(
            CurrentSchema,
            Applicable: false,
            MeasurementFrameCount: 0,
            Array.Empty<SampleBenchmarkDdgiTransientRawFrame>());

    public static bool IsCanonicalNotApplicable(
        SampleBenchmarkDdgiTransientRawEvidence? evidence) =>
        evidence != null &&
        string.Equals(evidence.Schema, CurrentSchema, StringComparison.Ordinal) &&
        !evidence.Applicable &&
        evidence.MeasurementFrameCount == 0 &&
        evidence.Frames is { Count: 0 };
}

public sealed record SampleBenchmarkDdgiTransientVerification(
    bool Passed,
    string SemanticDigest,
    int RawRowCount,
    SampleBenchmarkDdgiTransientEvidence RecomputedEvidence,
    IReadOnlyList<string> Failures);

/// <summary>
/// One deterministic implementation shared by the timing producer and the
/// immutable original-baseline verifier. Raw rows are copied only after the
/// measured window; recomputation never trusts candidate-produced windows.
/// </summary>
public static class SampleBenchmarkDdgiTransientEvidenceEvaluator
{
    public const string SemanticDigestSchema =
        "njulf-benchmark-ddgi-transient-semantic-digest/v2";

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private static readonly SimpleDdgiGpuPassMask KnownGpuPassMask =
        Enum.GetValues<SimpleDdgiGpuPassMask>()
            .Aggregate(
                SimpleDdgiGpuPassMask.None,
                static (current, value) => current | value);

    public static bool IsApplicable(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(options);
        return scenario ==
                SamplePerformanceScenario.BistroQualityMotionRelight &&
            options.Trajectory == SampleBenchmarkTrajectoryKind.BistroLoop &&
            options.TrajectoryBistroVariant ==
                SampleBistroQualityCaptureVariant.SunScaleStep;
    }

    public static SampleBenchmarkDdgiTransientRawEvidence CaptureRaw(
        IReadOnlyList<RendererDiagnostics> samples,
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        int measurementFrameCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsApplicable(options, scenario))
            return SampleBenchmarkDdgiTransientRawEvidence.NotApplicable;

        var frames = new SampleBenchmarkDdgiTransientRawFrame[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            RendererDiagnostics sample = samples[index] ??
                throw new InvalidDataException(
                    $"DDGI transient measurement row {index} is null.");
            frames[index] = new SampleBenchmarkDdgiTransientRawFrame(
                SampleBenchmarkDdgiTransientRawFrame.CurrentSchema,
                index,
                index,
                sample.CaptureFrame.FrameSerial,
                sample.SimpleDdgiActive,
                sample.SimpleDdgiSourceLightingGeneration,
                sample.SimpleDdgiCompletedFrameEvidence);
        }

        return new SampleBenchmarkDdgiTransientRawEvidence(
            SampleBenchmarkDdgiTransientRawEvidence.CurrentSchema,
            Applicable: true,
            measurementFrameCount,
            Array.AsReadOnly(frames));
    }

    public static SampleBenchmarkDdgiTransientEvidence Recompute(
        SampleBenchmarkDdgiTransientRawEvidence raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string[] envelopeFailures = ValidateRawEnvelope(raw).ToArray();
        if (!raw.Applicable)
        {
            return envelopeFailures.Length == 0
                ? SampleBenchmarkDdgiTransientEvidence.NotApplicable
                : SampleBenchmarkDdgiTransientEvidence.Failed(
                    applicable: false,
                    envelopeFailures);
        }

        bool canReplay = raw.MeasurementFrameCount ==
                SampleBistroQualityCaptureContract.LoopFrameCount &&
            raw.Frames is { Count: SampleBistroQualityCaptureContract.LoopFrameCount } &&
            raw.Frames.All(static frame => frame is not null);
        if (!canReplay)
        {
            return SampleBenchmarkDdgiTransientEvidence.Failed(
                applicable: true,
                envelopeFailures);
        }

        SampleBenchmarkDdgiTransientEvidence replayed =
            SampleBenchmarkAnalyzer.RecomputeDdgiTransientEvidenceCore(raw);
        if (envelopeFailures.Length == 0)
            return replayed;
        return SampleBenchmarkDdgiTransientEvidence.Failed(
            applicable: true,
            envelopeFailures
                .Concat(replayed.Failures)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    public static SampleBenchmarkDdgiTransientVerification Verify(
        SampleBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();
        if (!string.Equals(
                report.Kind,
                MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Schema,
                MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema,
                StringComparison.Ordinal))
        {
            failures.Add("Benchmark report kind/schema is not canonical.");
        }

        SampleBenchmarkOptions? options = report.Options;
        SampleBenchmarkDdgiTransientRawEvidence? raw =
            report.DdgiTransientRawEvidence;
        SampleBenchmarkDdgiTransientEvidence? stored =
            report.DdgiTransientEvidence;
        if (options is null)
            failures.Add("Benchmark report options are null.");
        if (raw is null)
            failures.Add("DDGI transient raw evidence is null.");
        if (stored is null)
            failures.Add("DDGI transient derived evidence is null.");

        bool expectedApplicable = options != null &&
            IsApplicable(options, report.Scenario);
        if (options != null &&
            (!Enum.IsDefined(options.Trajectory) ||
             !Enum.IsDefined(options.TrajectoryBistroVariant)))
        {
            failures.Add(
                "Benchmark trajectory or Bistro variant is undefined.");
        }
        SampleBenchmarkDdgiTransientEvidence recomputed =
            SampleBenchmarkDdgiTransientEvidence.Failed(
                expectedApplicable,
                ["DDGI transient raw evidence is unavailable."]);
        if (raw != null)
        {
            if (raw.Applicable != expectedApplicable)
            {
                failures.Add(
                    "DDGI transient raw applicability does not match the " +
                    "benchmark trajectory/variant.");
            }
            if (expectedApplicable &&
                (report.MeasurementFrameCount !=
                     SampleBistroQualityCaptureContract.LoopFrameCount ||
                 options!.MeasureFrameCount !=
                     SampleBistroQualityCaptureContract.LoopFrameCount ||
                 raw.MeasurementFrameCount != report.MeasurementFrameCount))
            {
                failures.Add(
                    "Applicable DDGI transient evidence is not bound to the " +
                    "exact 240-frame report/options window.");
            }
            recomputed = Recompute(raw);
            foreach (string failure in recomputed.Failures)
                failures.Add(failure);
        }

        ValidateReportIdentity(report, raw, expectedApplicable, failures);

        if (stored != null)
        {
            if (!HasCanonicalShape(stored))
                failures.Add("Stored DDGI transient evidence shape is noncanonical.");
            bool exact = false;
            try
            {
                exact = StructuralEquals(stored, recomputed);
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException or
                    InvalidOperationException)
            {
                failures.Add(
                    "Stored DDGI transient evidence comparison failed: " +
                    exception.Message);
            }
            if (!exact)
            {
                failures.Add(
                    "Stored DDGI transient evidence does not exactly match " +
                    "immutable recomputation from raw rows.");
            }
        }

        string[] distinct = failures
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string digest = "unavailable";
        if (distinct.Length == 0 && raw != null)
        {
            try
            {
                digest = CreateSemanticDigest(raw, recomputed);
            }
            catch (Exception exception) when (
                exception is JsonException or NotSupportedException or
                    InvalidOperationException)
            {
                distinct =
                [
                    $"DDGI transient semantic digest failed: " +
                    $"{exception.GetType().Name}: {exception.Message}"
                ];
            }
        }

        return new SampleBenchmarkDdgiTransientVerification(
            distinct.Length == 0,
            digest,
            raw?.Frames?.Count ?? 0,
            recomputed,
            Array.AsReadOnly(distinct));
    }

    private static IEnumerable<string> ValidateRawEnvelope(
        SampleBenchmarkDdgiTransientRawEvidence raw)
    {
        if (!string.Equals(
                raw.Schema,
                SampleBenchmarkDdgiTransientRawEvidence.CurrentSchema,
                StringComparison.Ordinal))
        {
            yield return "DDGI transient raw evidence schema is not canonical.";
        }
        if (!raw.Applicable)
        {
            if (!SampleBenchmarkDdgiTransientRawEvidence
                    .IsCanonicalNotApplicable(raw))
            {
                yield return "DDGI transient non-applicable raw shape is noncanonical.";
            }
            yield break;
        }

        int expected = SampleBistroQualityCaptureContract.LoopFrameCount;
        if (raw.MeasurementFrameCount != expected ||
            raw.Frames is not { Count: var count } || count != expected)
        {
            yield return
                $"DDGI transient raw evidence requires exactly {expected} ordered rows.";
            yield break;
        }

        for (int index = 0; index < raw.Frames.Count; index++)
        {
            SampleBenchmarkDdgiTransientRawFrame? frame = raw.Frames[index];
            if (frame is null)
            {
                yield return $"DDGI transient raw row {index} is null.";
                continue;
            }
            if (!string.Equals(
                    frame.Schema,
                    SampleBenchmarkDdgiTransientRawFrame.CurrentSchema,
                    StringComparison.Ordinal) ||
                frame.MeasurementSampleIndex != index ||
                frame.RouteFrameIndex != index)
            {
                yield return
                    $"DDGI transient raw row {index} schema/index identity is noncanonical.";
            }
            if (frame.Active != 1)
            {
                yield return
                    $"DDGI transient route frame {index} is not Simple-DDGI active.";
            }
            foreach (string failure in ValidateCompletion(
                         frame.CompletionObserved,
                         index,
                         frame.CaptureFrameSerial))
            {
                yield return failure;
            }
        }
    }

    private static void ValidateReportIdentity(
        SampleBenchmarkReport report,
        SampleBenchmarkDdgiTransientRawEvidence? raw,
        bool expectedApplicable,
        ICollection<string> failures)
    {
        SampleBenchmarkOptions? options = report.Options;
        SampleBenchmarkCaptureContract? contract = report.CaptureContract;
        if (options is null || contract is null)
        {
            failures.Add(
                "DDGI transient report options/capture contract is null.");
            return;
        }

        if (!Enum.IsDefined(report.Scenario))
            failures.Add("DDGI transient report scenario is undefined.");

        RendererDiagnostics? last = report.LastDiagnostics;
        if (last is null)
        {
            failures.Add("DDGI transient report last diagnostics are null.");
        }
        else if (!string.Equals(
                     last.CaptureRun.Scenario,
                     report.Scenario.ToString(),
                     StringComparison.Ordinal))
        {
            failures.Add(
                "DDGI transient report scenario does not match the final " +
                "renderer capture-run scenario.");
        }

        string expectedTrajectoryName;
        string expectedFingerprint;
        string expectedRouteHash;
        int expectedTrajectoryFrameCount;
        try
        {
            expectedTrajectoryName = SampleBenchmarkTrajectory.GetName(
                options.Trajectory);
            expectedTrajectoryFrameCount = SampleBenchmarkTrajectory.GetFrameCount(
                options.Trajectory);
            expectedFingerprint = SampleBenchmarkTrajectory.CreateFingerprint(
                options.Trajectory,
                options.TrajectoryBistroVariant);
            expectedRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
                options.Trajectory,
                options.TrajectoryBistroVariant,
                options.Trajectory == SampleBenchmarkTrajectoryKind.Stationary
                    ? last?.CaptureCamera
                    : null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            failures.Add(
                "DDGI transient authored route identity could not be " +
                "recomputed: " + exception.Message);
            return;
        }

        if (!string.Equals(
                options.TrajectoryFingerprint,
                expectedFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                contract.Trajectory,
                expectedTrajectoryName,
                StringComparison.Ordinal) ||
            contract.TrajectoryFrameCount != expectedTrajectoryFrameCount ||
            !string.Equals(
                contract.TrajectoryFingerprint,
                expectedFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                contract.TrajectoryRouteHash,
                expectedRouteHash,
                StringComparison.Ordinal) ||
            !IsSha256Identity(contract.TrajectorySequenceHash))
        {
            failures.Add(
                "DDGI transient options/capture contract does not match its " +
                "exact authored trajectory and Bistro variant.");
        }
        if (SampleBenchmarkTrajectory.IsMoving(options.Trajectory) &&
            (report.MeasurementFrameCount != expectedTrajectoryFrameCount ||
             options.MeasureFrameCount != expectedTrajectoryFrameCount))
        {
            failures.Add(
                "DDGI transient moving trajectory does not cover exactly one " +
                "authored route cycle.");
        }

        if (!expectedApplicable)
            return;

        if (report.MeasurementFrameCount < 0 ||
            options.MeasureFrameCount != report.MeasurementFrameCount ||
            report.FirstMeasurementFrameIndex < 0 ||
            report.LastMeasurementFrameIndex <
                report.FirstMeasurementFrameIndex)
        {
            failures.Add(
                "DDGI transient report measurement identity is invalid.");
        }
        else
        {
            int expectedLast;
            try
            {
                expectedLast = checked(
                    report.FirstMeasurementFrameIndex +
                    report.MeasurementFrameCount - 1);
            }
            catch (OverflowException)
            {
                expectedLast = int.MinValue;
            }
            if (report.LastMeasurementFrameIndex != expectedLast)
            {
                failures.Add(
                    "DDGI transient report measurement bounds are not exact.");
            }
        }

        int expectedCount = SampleBistroQualityCaptureContract.LoopFrameCount;
        if (report.MeasurementFrameCount != expectedCount ||
            options.MeasureFrameCount != expectedCount ||
            options.Trajectory != SampleBenchmarkTrajectoryKind.BistroLoop ||
            options.TrajectoryBistroVariant !=
                SampleBistroQualityCaptureVariant.SunScaleStep ||
            report.Scenario !=
                SamplePerformanceScenario.BistroQualityMotionRelight)
        {
            failures.Add(
                "DDGI transient report/options/capture contract is not the " +
                "exact authored 240-frame Bistro SunScaleStep route.");
        }

        if (last is not null &&
            (report.GpuTimingSupported != 1 ||
             report.GpuTimingValidSampleCount != expectedCount ||
             last.GpuTimingSupported != report.GpuTimingSupported ||
             last.GpuTimingValid != 1 ||
             !string.IsNullOrEmpty(report.GpuTimingUnavailableReason) ||
             !string.IsNullOrEmpty(last.GpuTimingUnavailableReason)))
        {
            failures.Add(
                "Applicable DDGI transient evidence requires exact GPU timing " +
                "support and all 240 valid measured samples.");
        }

        if (last is not null &&
            raw?.Frames is { Count: var rawCount } && rawCount == expectedCount)
        {
            SampleBenchmarkDdgiTransientRawFrame final = raw.Frames[^1];
            if (last.CaptureFrame.FrameSerial != final.CaptureFrameSerial ||
                last.SimpleDdgiActive != final.Active ||
                last.SimpleDdgiSourceLightingGeneration !=
                    final.SourceLightingGeneration ||
                !StructuralEquals(
                    last.SimpleDdgiCompletedFrameEvidence,
                    final.CompletionObserved))
            {
                failures.Add(
                    "DDGI transient raw row 239 does not exactly match the " +
                    "report's final renderer diagnostics.");
            }
        }
    }

    private static IEnumerable<string> ValidateCompletion(
        SimpleDdgiCompletedFrameEvidence completed,
        int rowIndex,
        ulong observationFrameSerial)
    {
        string prefix = $"DDGI transient raw row {rowIndex} completion";
        var payloadFailures = new List<string>();
        ValidateRequiredPayload(
            completed,
            prefix,
            payloadFailures);
        foreach (string failure in payloadFailures)
            yield return failure;
        ulong frameDelay = (ulong)RenderingConstants.FramesInFlight;
        if (!completed.Valid)
        {
            if (completed != default)
            {
                yield return
                    $"{prefix} is invalid but does not have the exact " +
                    "canonical default payload.";
            }
            else if (observationFrameSerial >= frameDelay)
            {
                yield return
                    $"{prefix} is missing the exact FramesInFlight-delayed " +
                    "predecessor submission.";
            }
            yield break;
        }

        SimpleDdgiSubmittedFrameEvidence submitted = completed.Submitted;
        if (!submitted.Valid)
        {
            yield return
                $"{prefix} is valid but its submitted identity is invalid.";
            yield break;
        }
        if (submitted.FrameSerial == ulong.MaxValue ||
            submitted.FrameSerial > ulong.MaxValue - frameDelay ||
            submitted.FrameSerial + frameDelay != observationFrameSerial)
        {
            yield return
                $"{prefix} does not have the exact overflow-safe " +
                $"FramesInFlight observation delay.";
        }
        int expectedSlot = checked((int)(submitted.FrameSerial % frameDelay));
        if (submitted.FrameSlot != expectedSlot)
        {
            yield return
                $"{prefix} retained frame slot {submitted.FrameSlot}; " +
                $"expected {expectedSlot}.";
        }
        foreach (string failure in ValidateCompletedFactoryShape(
                     completed,
                     prefix))
        {
            yield return failure;
        }
        if (completed.GpuAcceleratedSolveMicroseconds < 0 ||
            completed.GpuSchedulerTailAdmitMicroseconds < 0 ||
            completed.GpuSchedulerEmitMicroseconds < 0 ||
            completed.GpuSchedulerCommitMicroseconds < 0 ||
            completed.GpuTransportAuditMicroseconds < 0 ||
            completed.GpuUrgentRelightMicroseconds < 0 ||
            completed.GpuDdgiTotalMicroseconds < 0)
        {
            yield return $"{prefix} has a negative GPU timing.";
        }
    }

    private static IEnumerable<string> ValidateCompletedFactoryShape(
        SimpleDdgiCompletedFrameEvidence completed,
        string prefix)
    {
        SimpleDdgiSubmittedFrameEvidence submitted = completed.Submitted;
        if (!submitted.GpuTimingRecorded)
        {
            yield return
                $"{prefix} did not record the exact submitted GPU timing set.";
        }

        bool expectedAligned = submitted.GpuTimingRecorded &&
            submitted.AdmittedGpuTimingPasses == submitted.IntendedGpuPasses &&
            completed.CompletedGpuTimingPasses ==
                submitted.AdmittedGpuTimingPasses;
        if (completed.GpuTimingPassSetAligned != expectedAligned)
        {
            yield return
                $"{prefix} GPU timing pass-set alignment alias is noncanonical.";
        }

        SimpleDdgiGpuPassMask completedPasses =
            completed.CompletedGpuTimingPasses;
        bool totalAvailable = expectedAligned &&
            (submitted.IntendedGpuPasses &
                (SimpleDdgiGpuPassMask.Schedule |
                 SimpleDdgiGpuPassMask.TransportAudit)) != 0;
        if (completed.GpuTimingAvailable != totalAvailable ||
            completed.GpuDdgiTotalTimingAvailable != totalAvailable)
        {
            yield return
                $"{prefix} DDGI total timing availability aliases are noncanonical.";
        }

        foreach ((SimpleDdgiGpuPassMask Pass, bool Available, long Timing,
                     string Name) item in new[]
                 {
                     (SimpleDdgiGpuPassMask.AcceleratedSolve,
                         completed.GpuAcceleratedSolveTimingAvailable,
                         completed.GpuAcceleratedSolveMicroseconds,
                         "accelerated-solve"),
                     (SimpleDdgiGpuPassMask.ScheduleTailAdmit,
                         completed.GpuSchedulerTailAdmitTimingAvailable,
                         completed.GpuSchedulerTailAdmitMicroseconds,
                         "tail-admit"),
                     (SimpleDdgiGpuPassMask.ScheduleEmit,
                         completed.GpuSchedulerEmitTimingAvailable,
                         completed.GpuSchedulerEmitMicroseconds,
                         "emit"),
                     (SimpleDdgiGpuPassMask.SchedulerCommit,
                         completed.GpuSchedulerCommitTimingAvailable,
                         completed.GpuSchedulerCommitMicroseconds,
                         "commit"),
                     (SimpleDdgiGpuPassMask.TransportAudit,
                         completed.GpuTransportAuditTimingAvailable,
                         completed.GpuTransportAuditMicroseconds,
                         "transport-audit"),
                     (SimpleDdgiGpuPassMask.UrgentRelight,
                         completed.GpuUrgentRelightTimingAvailable,
                         completed.GpuUrgentRelightMicroseconds,
                         "urgent-relight")
                 })
        {
            bool expectedAvailable = (completedPasses & item.Pass) != 0;
            if (item.Available != expectedAvailable ||
                (!expectedAvailable && item.Timing != 0))
            {
                yield return
                    $"{prefix} {item.Name} availability/timing does not " +
                    "match the exact completed pass mask.";
            }
        }
        bool scheduleAvailable =
            (completedPasses & SimpleDdgiGpuPassMask.Schedule) != 0;
        if (completed.GpuScheduleTimingAvailable != scheduleAvailable)
        {
            yield return
                $"{prefix} schedule availability does not match the exact " +
                "completed pass mask.";
        }
        if (!totalAvailable && completed.GpuDdgiTotalMicroseconds != 0)
        {
            yield return
                $"{prefix} retained a DDGI total without an available exact " +
                "top-level pass set.";
        }

        bool expectedFrameAligned = completed.SchedulerFeedbackAvailable &&
            submitted.FrameSerialsValid &&
            completed.SchedulerFeedbackFrameSerial ==
                submitted.SchedulerFrameSerial;
        bool expectedGenerationAligned = expectedFrameAligned &&
            completed.SchedulerFeedbackVolumeResourceGeneration ==
                submitted.VolumeResourceGeneration &&
            completed.SchedulerFeedbackTransportTopologyGeneration ==
                submitted.TransportTopologyGeneration &&
            completed.SchedulerFeedbackSchedulerResourceGeneration ==
                submitted.SchedulerResourceGeneration &&
            completed.SchedulerFeedbackQueueTransactionGeneration ==
                submitted.QueueTransactionGeneration &&
            completed.SchedulerFeedbackSourceLightingGeneration ==
                submitted.SourceLightingGeneration;
        if (completed.SchedulerFeedbackFrameAligned != expectedFrameAligned ||
            completed.SchedulerFeedbackGenerationAligned !=
                expectedGenerationAligned)
        {
            yield return
                $"{prefix} scheduler feedback alignment aliases are noncanonical.";
        }

        ulong sourceParticipants =
            (ulong)completed.SchedulerHardSourceParticipantCount +
            completed.SchedulerRoutineSourceParticipantCount;
        ulong acceptedParticipants =
            (ulong)completed.SchedulerSourceParticipantCount +
            completed.SchedulerCachedParticipantCount;
        uint expectedActive = SaturatingAdd(
            completed.SchedulerSourceParticipantCount,
            completed.SchedulerCachedParticipantCount);
        uint expectedCachedRays =
            completed.SchedulerTransportRayCount >
                completed.SchedulerSourceRayCount
                ? completed.SchedulerTransportRayCount -
                  completed.SchedulerSourceRayCount
                : 0u;
        if (sourceParticipants !=
                completed.SchedulerSourceParticipantCount ||
            acceptedParticipants != completed.SchedulerAcceptedWorkCount ||
            completed.SchedulerActiveWorkCount != expectedActive ||
            completed.SchedulerConsideredCandidateCount <
                completed.SchedulerCompactedCandidateCount ||
            completed.SchedulerCompactedCandidateCount <
                completed.SchedulerAcceptedWorkCount ||
            completed.SchedulerAcceptedWorkCount <
                completed.SchedulerCommittedWorkCount ||
            completed.SchedulerCommittedWorkCount <
                completed.SchedulerPublishedWorkCount ||
            completed.SchedulerCachedRayCount != expectedCachedRays ||
            completed.SchedulerSolveVisitedCount >
                completed.SchedulerSolveParticipantCount)
        {
            yield return
                $"{prefix} scheduler counter equations/order are noncanonical.";
        }
        if (submitted.ActiveProbeCount < 0 ||
            submitted.AuditPhysicalProbeCount < 0 ||
            submitted.CachedSweepCount < 0)
        {
            yield return
                $"{prefix} submitted bounded counts are negative.";
        }
    }

    private static uint SaturatingAdd(uint left, uint right) =>
        uint.MaxValue - left < right ? uint.MaxValue : left + right;

    private static void ValidateRequiredPayload(
        object payload,
        string path,
        ICollection<string> failures)
    {
        Type type = payload.GetType();
        foreach (PropertyInfo property in type.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetCustomAttribute<JsonRequiredAttribute>() is null)
                continue;
            object? value = property.GetValue(payload);
            string propertyPath = path + "." + property.Name;
            if (value is null)
            {
                failures.Add($"{propertyPath} is null.");
                continue;
            }

            Type propertyType = property.PropertyType;
            if (propertyType == typeof(float))
            {
                float number = (float)value;
                if (!float.IsFinite(number) ||
                    BitConverter.SingleToUInt32Bits(number) == 0x80000000u)
                {
                    failures.Add(
                        $"{propertyPath} is not a canonical finite float.");
                }
            }
            else if (propertyType == typeof(SimpleDdgiGpuPassMask))
            {
                var mask = (SimpleDdgiGpuPassMask)value;
                if ((mask & ~KnownGpuPassMask) != 0)
                    failures.Add($"{propertyPath} contains unknown pass bits.");
            }
            else if (propertyType.IsEnum && !Enum.IsDefined(propertyType, value))
            {
                failures.Add($"{propertyPath} has an undefined enum value.");
            }
            else if (SampleBenchmarkDdgiTransientWireShape
                         .IsNestedPayloadType(propertyType))
            {
                ValidateRequiredPayload(value, propertyPath, failures);
            }
        }
    }

    private static bool IsSha256Identity(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;

    private static bool HasCanonicalShape(
        SampleBenchmarkDdgiTransientEvidence evidence)
    {
        if (!string.Equals(
                evidence.Schema,
                SampleBenchmarkDdgiTransientEvidence.CurrentSchema,
                StringComparison.Ordinal) ||
            evidence.Failures is null ||
            evidence.Windows is null)
        {
            return false;
        }
        if (!evidence.Applicable)
        {
            return !evidence.Available &&
                evidence.Failures.Count == 0 &&
                evidence.Windows.Count == 0;
        }
        if (!evidence.Available)
        {
            return evidence.Failures.Count > 0 &&
                evidence.Failures.All(static failure =>
                    !string.IsNullOrWhiteSpace(failure)) &&
                evidence.Windows.Count == 0;
        }
        if (evidence.Failures.Count != 0 || evidence.Windows.Count != 2)
            return false;

        for (int windowIndex = 0; windowIndex < evidence.Windows.Count;
             windowIndex++)
        {
            SampleBenchmarkDdgiTransientWindow window =
                evidence.Windows[windowIndex];
            if (window is null ||
                window.WindowIndex != windowIndex ||
                !SampleBenchmarkDdgiTransientClosureKind.IsCanonical(
                    window.ClosureKind) ||
                window.Frames is null ||
                window.ObservedGenerationEdgeRouteFrameIndex <
                    window.AuthoredEventRouteFrameIndex ||
                window.ResponseClosureRouteFrameIndex <
                    window.ObservedGenerationEdgeRouteFrameIndex)
            {
                return false;
            }

            long responseLatency =
                (long)window.ResponseClosureRouteFrameIndex -
                window.ObservedGenerationEdgeRouteFrameIndex;
            if (responseLatency != window.ResponseLatencyFrames ||
                responseLatency + 1L != window.Frames.Count ||
                window.Frames.Count == 0 ||
                window.Frames[0].RouteFrameIndex !=
                    window.ObservedGenerationEdgeRouteFrameIndex ||
                window.Frames[^1].RouteFrameIndex !=
                    window.ResponseClosureRouteFrameIndex ||
                window.FirstSubmittedFrameSerial !=
                    window.Frames[0].Completed.Submitted.FrameSerial ||
                window.LastSubmittedFrameSerial !=
                    window.Frames[^1].Completed.Submitted.FrameSerial ||
                window.FirstSubmittedSchedulerFrameSerial !=
                    window.Frames[0].Completed.Submitted.SchedulerFrameSerial ||
                window.LastSubmittedSchedulerFrameSerial !=
                    window.Frames[^1].Completed.Submitted.SchedulerFrameSerial)
            {
                return false;
            }
        }

        return true;
    }

    private static bool StructuralEquals<T>(T left, T right)
    {
        byte[] leftBytes = JsonSerializer.SerializeToUtf8Bytes(
            left,
            CanonicalJsonOptions);
        byte[] rightBytes = JsonSerializer.SerializeToUtf8Bytes(
            right,
            CanonicalJsonOptions);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }

    private static string CreateSemanticDigest(
        SampleBenchmarkDdgiTransientRawEvidence raw,
        SampleBenchmarkDdgiTransientEvidence recomputed)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            new SemanticDigestPayload(
                SemanticDigestSchema,
                raw,
                recomputed),
            CanonicalJsonOptions);
        return "sha256:" +
            Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private sealed record SemanticDigestPayload(
        string Schema,
        SampleBenchmarkDdgiTransientRawEvidence Raw,
        SampleBenchmarkDdgiTransientEvidence Derived);
}

/// <summary>
/// Exact DDGI subtree shape admission. JsonRequired rejects omissions while
/// this pass rejects ignored/computed legacy getters and any other property
/// that is not a versioned payload member.
/// </summary>
internal static class SampleBenchmarkDdgiTransientWireShape
{
    private static readonly HashSet<Type> NestedPayloadTypes =
    [
        typeof(SimpleDdgiCompletedFrameEvidence),
        typeof(SimpleDdgiSubmittedFrameEvidence),
        typeof(SimpleDdgiTailCertificateFrameEvidence),
        typeof(SimpleDdgiTransportTailSummary),
        typeof(SimpleDdgiTransportGenerations),
        typeof(SimpleDdgiTransportMismatchIdentity),
        typeof(SimpleDdgiTransportRgbBounds)
    ];

    public static void Validate(byte[] reportBytes)
    {
        ArgumentNullException.ThrowIfNull(reportBytes);
        using JsonDocument document = JsonDocument.Parse(
            reportBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Benchmark DDGI transient report root is not an object.");
        }

        ValidateNamedProperty(
            root,
            nameof(SampleBenchmarkReport.DdgiTransientRawEvidence),
            typeof(SampleBenchmarkDdgiTransientRawEvidence),
            "$.DdgiTransientRawEvidence");
        ValidateNamedProperty(
            root,
            nameof(SampleBenchmarkReport.DdgiTransientEvidence),
            typeof(SampleBenchmarkDdgiTransientEvidence),
            "$.DdgiTransientEvidence");
        ValidateOptionApplicabilityIdentity(root);
    }

    internal static bool IsNestedPayloadType(Type type) =>
        NestedPayloadTypes.Contains(type);

    private static void ValidateOptionApplicabilityIdentity(JsonElement root)
    {
        if (!root.TryGetProperty(
                nameof(SampleBenchmarkReport.Options),
                out JsonElement options) ||
            options.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Benchmark report $.Options is missing or not an object.");
        }

        RequireDefinedEnum<SampleBenchmarkTrajectoryKind>(
            options,
            nameof(SampleBenchmarkOptions.Trajectory),
            "$.Options.Trajectory");
        RequireDefinedEnum<SampleBistroQualityCaptureVariant>(
            options,
            nameof(SampleBenchmarkOptions.TrajectoryBistroVariant),
            "$.Options.TrajectoryBistroVariant");
    }

    private static void RequireDefinedEnum<TEnum>(
        JsonElement parent,
        string propertyName,
        string path)
        where TEnum : struct, Enum
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetByte(out byte numeric) ||
            !Enum.IsDefined(typeof(TEnum), numeric))
        {
            throw new InvalidDataException(
                $"Benchmark report {path} is missing or undefined.");
        }
    }

    private static void ValidateNamedProperty(
        JsonElement parent,
        string propertyName,
        Type propertyType,
        string path)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new InvalidDataException(
                $"Benchmark report is missing required property {path}.");
        }
        ValidateValue(value, propertyType, path);
    }

    private static void ValidateValue(
        JsonElement value,
        Type type,
        string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException($"{path} is null.");

        if (TryGetListElementType(type, out Type? elementType))
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{path} is not an array.");
            int index = 0;
            foreach (JsonElement element in value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Null)
                {
                    throw new InvalidDataException(
                        $"{path}[{index}] is null.");
                }
                if (RequiresExactObjectShape(elementType!))
                {
                    ValidateValue(
                        element,
                        elementType!,
                        $"{path}[{index}]");
                }
                index++;
            }
            return;
        }

        if (!RequiresExactObjectShape(type))
            return;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} is not an object.");

        PropertyInfo[] required = type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetCustomAttribute<JsonRequiredAttribute>() != null)
            .ToArray();
        var expected = new Dictionary<string, PropertyInfo>(
            required.Length,
            StringComparer.Ordinal);
        foreach (PropertyInfo property in required)
        {
            string name = property
                .GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                property.Name;
            expected.Add(name, property);
        }

        int observedCount = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            observedCount++;
            if (!expected.TryGetValue(property.Name, out PropertyInfo? metadata))
            {
                throw new InvalidDataException(
                    $"{path} contains noncanonical property '{property.Name}'.");
            }
            ValidateValue(
                property.Value,
                metadata.PropertyType,
                path + "." + property.Name);
        }
        if (observedCount != expected.Count)
        {
            string[] missing = expected.Keys
                .Where(name => !value.TryGetProperty(name, out _))
                .ToArray();
            throw new InvalidDataException(
                $"{path} is missing required properties: " +
                string.Join(",", missing));
        }
    }

    private static bool RequiresExactObjectShape(Type type) =>
        type == typeof(SampleBenchmarkDdgiTransientRawEvidence) ||
        type == typeof(SampleBenchmarkDdgiTransientRawFrame) ||
        type == typeof(SampleBenchmarkDdgiTransientEvidence) ||
        type == typeof(SampleBenchmarkDdgiTransientWindow) ||
        type == typeof(SampleBenchmarkDdgiTransientFrame) ||
        IsNestedPayloadType(type);

    private static bool TryGetListElementType(
        Type type,
        out Type? elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }
        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }
        elementType = null;
        return false;
    }
}
