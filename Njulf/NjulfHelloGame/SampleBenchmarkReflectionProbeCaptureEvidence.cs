using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// One bounded, post-measurement copy of the exact renderer diagnostics used
/// to rank reflection-probe capture work. These rows are deliberately kept in
/// the benchmark report so an immutable baseline executable can reproduce the
/// derived top-eight evidence without trusting candidate-produced aggregates.
/// </summary>
public sealed record SampleBenchmarkReflectionProbeRawFrame(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] int MeasurementSampleIndex,
    [property: JsonRequired] int RouteFrameIndex,
    [property: JsonRequired] ulong CaptureFrameSerial,
    [property: JsonRequired] int CaptureFrameSlot,
    [property: JsonRequired] int GpuTimingValid,
    [property: JsonRequired] long GpuCaptureMicroseconds,
    [property: JsonRequired] long GpuPrefilterMicroseconds,
    [property: JsonRequired] long GpuPublishMicroseconds,
    [property: JsonRequired]
        ReflectionProbeLifecycleFrameSnapshot CurrentLifecycle,
    [property: JsonRequired]
        ReflectionProbeLifecycleFrameSnapshot CompletedLifecycle,
    [property: JsonRequired] ReflectionProbeGpuBudgetSnapshot CurrentBudget)
{
    public const string CurrentSchema =
        "njulf-benchmark-reflection-probe-raw-frame/v1";
}

public sealed record SampleBenchmarkReflectionProbeRawEvidence(
    [property: JsonRequired] string Schema,
    [property: JsonRequired] bool Applicable,
    [property: JsonRequired] int MeasurementFrameCount,
    [property: JsonRequired]
        IReadOnlyList<SampleBenchmarkReflectionProbeRawFrame> Frames)
{
    public const string CurrentSchema =
        "njulf-benchmark-reflection-probe-raw-evidence/v1";

    public static SampleBenchmarkReflectionProbeRawEvidence NotApplicable
        { get; } = new(
            CurrentSchema,
            Applicable: false,
            MeasurementFrameCount: 0,
            Array.Empty<SampleBenchmarkReflectionProbeRawFrame>());

    public static bool IsCanonicalNotApplicable(
        SampleBenchmarkReflectionProbeRawEvidence? evidence) =>
        evidence != null &&
        string.Equals(evidence.Schema, CurrentSchema, StringComparison.Ordinal) &&
        !evidence.Applicable &&
        evidence.MeasurementFrameCount == 0 &&
        evidence.Frames is { Count: 0 };
}

public sealed record SampleBenchmarkReflectionProbeVerification(
    bool Passed,
    string Digest,
    int RawRowCount,
    SampleReflectionProbeCaptureEvidence RecomputedEvidence,
    IReadOnlyList<string> Failures);

/// <summary>
/// Shared producer/verifier implementation for C3 reflection evidence. The
/// producer calls <see cref="CaptureRaw"/> only after the measurement window;
/// the immutable verifier reloads these rows and executes the same pure rank
/// and backward-join algorithm.
/// </summary>
public static class SampleBenchmarkReflectionProbeCaptureEvaluator
{
    private const int RequiredBudgetMicroseconds = 500;
    private const int DefaultFaceEstimateMicroseconds = 100;
    private const int DefaultPrefilterEstimateMicroseconds = 125;
    private const int DefaultCopyEstimateMicroseconds = 25;
    private const int MaximumEstimateMicroseconds = 1_000_000;

    public const string VerificationDigestSchema =
        "njulf-benchmark-reflection-probe-verification-digest/v1";

    public static SampleBenchmarkReflectionProbeRawEvidence CaptureRaw(
        IReadOnlyList<RendererDiagnostics> samples,
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        int measurementFrameCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        string activation = SampleBenchmarkActivation.Normalize(
            options.Activation);
        if (activation != SampleBenchmarkActivation.ReflectionRecapture)
            return SampleBenchmarkReflectionProbeRawEvidence.NotApplicable;

        SampleBenchmarkActivation.Validate(
            activation,
            scenario,
            options.Trajectory,
            options.CaptureVariant,
            measurementFrameCount,
            qualitySequence: false);
        if (measurementFrameCount != samples.Count ||
            measurementFrameCount !=
                SampleBenchmarkActivation.SponzaActivationFrameCount)
        {
            throw new InvalidDataException(
                "Reflection raw evidence requires the exact complete " +
                $"{SampleBenchmarkActivation.SponzaActivationFrameCount}-frame " +
                $"route; report={measurementFrameCount}, samples={samples.Count}.");
        }

        var frames = new SampleBenchmarkReflectionProbeRawFrame[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            RendererDiagnostics sample = samples[index];
            frames[index] = new SampleBenchmarkReflectionProbeRawFrame(
                SampleBenchmarkReflectionProbeRawFrame.CurrentSchema,
                index,
                index,
                sample.CaptureFrame.FrameSerial,
                sample.ReflectionProbeCurrentLifecycle.FrameSlot,
                sample.GpuTimingValid,
                sample.GpuReflectionProbeCaptureMicroseconds,
                sample.GpuReflectionProbePrefilterMicroseconds,
                sample.GpuReflectionProbePublishMicroseconds,
                sample.ReflectionProbeCurrentLifecycle,
                sample.ReflectionProbeCompletedLifecycle,
                sample.ReflectionProbeCurrentCaptureBudget);
        }
        return new SampleBenchmarkReflectionProbeRawEvidence(
            SampleBenchmarkReflectionProbeRawEvidence.CurrentSchema,
            Applicable: true,
            measurementFrameCount,
            Array.AsReadOnly(frames));
    }

    public static SampleReflectionProbeCaptureEvidence Recompute(
        SampleBenchmarkReflectionProbeRawEvidence raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (!raw.Applicable)
        {
            if (!SampleBenchmarkReflectionProbeRawEvidence
                    .IsCanonicalNotApplicable(raw))
            {
                throw new InvalidDataException(
                    "Non-applicable reflection raw evidence is noncanonical.");
            }
            return SampleReflectionProbeCaptureEvidence.NotApplicable;
        }

        var candidates = new List<SampleReflectionProbeSlowFrame>();
        for (int index = 0; index < raw.Frames.Count; index++)
        {
            SampleReflectionProbeSlowFrame? candidate = CreateSlowFrame(
                raw.Frames,
                index);
            if (candidate != null)
                candidates.Add(candidate);
        }
        SampleReflectionProbeSlowFrame[] slowest = candidates
            .OrderByDescending(static frame => frame.CompletedGpuMicroseconds)
            .ThenBy(static frame => frame.MeasurementSampleIndex)
            .Take(SampleReflectionProbeCaptureEvidence.SlowFrameLimit)
            .ToArray();
        return new SampleReflectionProbeCaptureEvidence(
            Array.AsReadOnly(slowest))
        {
            Schema = SampleReflectionProbeCaptureEvidence.CurrentSchema,
            Applicable = true
        };
    }

    public static SampleBenchmarkReflectionProbeVerification Verify(
        SampleBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();
        SampleBenchmarkCaptureContract? captureContract =
            report.CaptureContract;
        SampleBenchmarkOptions? options = report.Options;
        if (captureContract == null)
            failures.Add("Reflection capture contract is null.");
        if (options == null)
            failures.Add("Reflection benchmark options are null.");
        string activation;
        try
        {
            activation = SampleBenchmarkActivation.Normalize(
                captureContract?.Activation);
        }
        catch (ArgumentException exception)
        {
            failures.Add(
                "Reflection capture-contract activation is invalid: " +
                exception.Message);
            activation = string.Empty;
        }

        bool applicable = string.Equals(
            activation,
            SampleBenchmarkActivation.ReflectionRecapture,
            StringComparison.Ordinal);
        SampleBenchmarkReflectionProbeRawEvidence? raw =
            report.ReflectionProbeCaptureRawEvidence;
        SampleReflectionProbeCaptureEvidence? stored =
            report.ReflectionProbeCaptureEvidence;
        if (!applicable)
        {
            if (!SampleBenchmarkReflectionProbeRawEvidence
                    .IsCanonicalNotApplicable(raw))
            {
                failures.Add(
                    "A non-reflection workload does not contain the exact " +
                    "canonical unavailable reflection raw-evidence shape.");
            }
            if (!SampleReflectionProbeCaptureEvidence
                    .IsCanonicalNotApplicable(stored))
            {
                failures.Add(
                    "A non-reflection workload does not contain the exact " +
                    "canonical unavailable reflection result shape.");
            }
            SampleBenchmarkReflectionProbeRawEvidence canonicalRaw =
                raw ?? SampleBenchmarkReflectionProbeRawEvidence.NotApplicable;
            SampleReflectionProbeCaptureEvidence canonicalResult =
                stored ?? SampleReflectionProbeCaptureEvidence.NotApplicable;
            string digest = failures.Count == 0
                ? CreateDigest(canonicalRaw, canonicalResult)
                : "unavailable";
            return new SampleBenchmarkReflectionProbeVerification(
                failures.Count == 0,
                digest,
                0,
                SampleReflectionProbeCaptureEvidence.NotApplicable,
                Array.AsReadOnly(failures.ToArray()));
        }

        ValidateReflectionIdentity(report, failures);
        ValidateRawRows(report, raw, failures);
        SampleReflectionProbeCaptureEvidence recomputed =
            SampleReflectionProbeCaptureEvidence.NotApplicable;
        if (failures.Count == 0 && raw != null)
        {
            try
            {
                recomputed = Recompute(raw);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or
                    OverflowException)
            {
                failures.Add(
                    "Reflection evidence recomputation failed: " +
                    exception.Message);
            }
        }
        if (failures.Count == 0 &&
            !EvidenceEqual(stored, recomputed))
        {
            failures.Add(
                "Stored reflection top-frame evidence does not exactly match " +
                "the independently recomputed rank, order, timing, lifecycle, " +
                "or submitted-budget join.");
        }

        string verificationDigest = failures.Count == 0 && raw != null
            ? CreateDigest(raw, recomputed)
            : "unavailable";
        return new SampleBenchmarkReflectionProbeVerification(
            failures.Count == 0,
            verificationDigest,
            raw?.Frames?.Count ?? 0,
            recomputed,
            Array.AsReadOnly(
                failures.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static void ValidateReflectionIdentity(
        SampleBenchmarkReport report,
        ICollection<string> failures)
    {
        SampleBenchmarkCaptureContract? captureContract =
            report.CaptureContract;
        SampleBenchmarkOptions? options = report.Options;
        if (captureContract == null || options == null)
            return;

        SampleBenchmarkTrajectoryKind trajectory;
        string normalizedCaptureVariant;
        string expectedTrajectoryFingerprint;
        string expectedTrajectoryRouteHash;
        try
        {
            trajectory = SampleBenchmarkTrajectory.Parse(
                captureContract.Trajectory);
            SampleBenchmarkActivation.Validate(
                captureContract.Activation,
                report.Scenario,
                trajectory,
                captureContract.Variant,
                report.MeasurementFrameCount,
                qualitySequence: false);
            normalizedCaptureVariant = SampleBenchmarkCaptureVariant.Normalize(
                options.CaptureVariant);
            expectedTrajectoryFingerprint =
                SampleBenchmarkTrajectory.CreateFingerprint(
                    SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                    SampleBistroQualityCaptureVariant.SunScaleStep);
            expectedTrajectoryRouteHash =
                SampleBenchmarkTrajectory.CreateRouteHash(
                    SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                    SampleBistroQualityCaptureVariant.SunScaleStep);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            failures.Add(
                "Reflection capture contract is invalid: " + exception.Message);
            return;
        }

        string expectedActivationFingerprint =
            SampleBenchmarkActivation.CreateFingerprint(
                SampleBenchmarkActivation.ReflectionRecapture);
        if (report.Scenario !=
                SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle ||
            report.MeasurementFrameCount !=
                SampleBenchmarkActivation.SponzaActivationFrameCount ||
            options.MeasureFrameCount != report.MeasurementFrameCount ||
            options.Trajectory !=
                SampleBenchmarkTrajectoryKind.SponzaHorizontal ||
            !string.Equals(
                options.TrajectoryFingerprint,
                expectedTrajectoryFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                options.Activation,
                SampleBenchmarkActivation.ReflectionRecapture,
                StringComparison.Ordinal) ||
            !string.Equals(
                options.ActivationFingerprint,
                expectedActivationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                options.CaptureVariant,
                normalizedCaptureVariant,
                StringComparison.Ordinal) ||
            !string.Equals(
                normalizedCaptureVariant,
                SampleBenchmarkCaptureVariant.Baseline,
                StringComparison.Ordinal) ||
            !string.Equals(
                captureContract.Activation,
                SampleBenchmarkActivation.ReflectionRecapture,
                StringComparison.Ordinal) ||
            !string.Equals(
                captureContract.ActivationFingerprint,
                expectedActivationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                captureContract.Variant,
                SampleBenchmarkCaptureVariant.Baseline,
                StringComparison.Ordinal) ||
            !string.Equals(
                captureContract.Trajectory,
                SampleBenchmarkTrajectory.SponzaHorizontalName,
                StringComparison.Ordinal) ||
            trajectory != SampleBenchmarkTrajectoryKind.SponzaHorizontal ||
            captureContract.TrajectoryFrameCount !=
                SampleBenchmarkActivation.SponzaActivationFrameCount ||
            !string.Equals(
                captureContract.TrajectoryFingerprint,
                expectedTrajectoryFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                captureContract.TrajectoryRouteHash,
                expectedTrajectoryRouteHash,
                StringComparison.Ordinal) ||
            !IsSha256Identity(captureContract.TrajectorySequenceHash))
        {
            failures.Add(
                "Reflection report/options/capture-contract route identity is " +
                "not the exact authored 300-frame Sponza activation.");
        }
    }

    private static void ValidateRawRows(
        SampleBenchmarkReport report,
        SampleBenchmarkReflectionProbeRawEvidence? raw,
        ICollection<string> failures)
    {
        if (report.GpuTimingSupported != 1 ||
            report.GpuTimingValidSampleCount !=
                SampleBenchmarkActivation.SponzaActivationFrameCount)
        {
            failures.Add(
                "Reflection evidence requires GPU timing support and one valid " +
                "GPU timing sample for every authored route frame.");
        }
        if (raw == null ||
            !string.Equals(
                raw.Schema,
                SampleBenchmarkReflectionProbeRawEvidence.CurrentSchema,
                StringComparison.Ordinal) ||
            !raw.Applicable ||
            raw.MeasurementFrameCount != report.MeasurementFrameCount ||
            raw.Frames == null ||
            raw.Frames.Count != report.MeasurementFrameCount)
        {
            failures.Add(
                "Reflection raw evidence schema, applicability, or exact row " +
                "count is invalid.");
            return;
        }
        SampleBenchmarkActivationEvidence? activation =
            report.ActivationEvidence;
        if (activation?.ExecutionFrames == null ||
            activation.ExecutionFrames.Count != raw.Frames.Count)
        {
            failures.Add(
                "Reflection raw rows cannot be cross-bound to one exact " +
                "activation execution row per measured route frame.");
            return;
        }

        ulong firstSerial = 0;
        int firstSlot = -1;
        int validGpuTimingCount = 0;
        for (int index = 0; index < raw.Frames.Count; index++)
        {
            SampleBenchmarkReflectionProbeRawFrame? frame = raw.Frames[index];
            if (frame == null)
            {
                failures.Add($"Reflection raw row {index} is null.");
                continue;
            }
            if (!string.Equals(
                frame.Schema,
                    SampleBenchmarkReflectionProbeRawFrame.CurrentSchema,
                    StringComparison.Ordinal) ||
                frame.MeasurementSampleIndex != index ||
                frame.RouteFrameIndex != index ||
                frame.GpuTimingValid != 1 ||
                frame.GpuCaptureMicroseconds < 0 ||
                frame.GpuPrefilterMicroseconds < 0 ||
                frame.GpuPublishMicroseconds < 0 ||
                !frame.CurrentLifecycle.Valid ||
                !frame.CurrentLifecycle.GpuTimingRecorded ||
                frame.CaptureFrameSerial == ulong.MaxValue ||
                frame.CaptureFrameSlot < 0 ||
                frame.CaptureFrameSlot >= RenderingConstants.FramesInFlight ||
                frame.CurrentLifecycle.FrameSerial != frame.CaptureFrameSerial ||
                frame.CurrentLifecycle.FrameSlot != frame.CaptureFrameSlot ||
                !BudgetCanonical(frame.CurrentBudget))
            {
                failures.Add(
                    $"Reflection raw row {index} is noncanonical, mislabeled, " +
                    "or lacks exact current-frame timing/budget identity.");
            }
            if (index == 0)
            {
                firstSerial = frame.CaptureFrameSerial;
                firstSlot = frame.CaptureFrameSlot;
            }
            else
            {
                ulong expectedSerial;
                try
                {
                    expectedSerial = checked(firstSerial + (ulong)index);
                }
                catch (OverflowException)
                {
                    expectedSerial = ulong.MaxValue;
                }
                int expectedSlot =
                    (firstSlot + index) % RenderingConstants.FramesInFlight;
                if (frame.CaptureFrameSerial != expectedSerial ||
                    frame.CaptureFrameSlot != expectedSlot)
                {
                    failures.Add(
                        $"Reflection raw row {index} is not the exact contiguous " +
                        "route frame serial/slot.");
                }
            }

            SampleBenchmarkActivationExecutionFrameEvidence authored =
                activation.ExecutionFrames[index];
            if (authored == null || authored.RouteFrameIndex != index ||
                authored.ReflectionProbeCurrentLifecycle !=
                    frame.CurrentLifecycle ||
                authored.ReflectionProbeCompletedLifecycle !=
                    frame.CompletedLifecycle)
            {
                failures.Add(
                    $"Reflection raw row {index} lifecycle evidence does not " +
                    "match the protected activation execution sequence.");
            }
            if (frame.GpuTimingValid == 1)
                validGpuTimingCount++;
        }
        if (validGpuTimingCount != report.GpuTimingValidSampleCount)
        {
            failures.Add(
                "Reflection raw GPU-valid row count does not match the report.");
        }
        ValidatePlannerReplay(raw.Frames, failures);
        ValidateLastDiagnosticsBinding(report, raw.Frames[^1], failures);
    }

    private static void ValidatePlannerReplay(
        IReadOnlyList<SampleBenchmarkReflectionProbeRawFrame> frames,
        ICollection<string> failures)
    {
        int expectedFaceEstimate = 0;
        int expectedPrefilterEstimate = 0;
        int expectedCopyEstimate = 0;
        bool expectedHistory = false;
        for (int index = 0; index < frames.Count; index++)
        {
            SampleBenchmarkReflectionProbeRawFrame? frame = frames[index];
            if (frame == null)
                continue;
            ReflectionProbeGpuBudgetSnapshot budget = frame.CurrentBudget;
            ReflectionProbeLifecycleSnapshot current =
                frame.CurrentLifecycle.Lifecycle;
            ReflectionProbeLifecycleFrameSnapshot completed =
                frame.CompletedLifecycle;
            ReflectionProbeLifecycleSnapshot completedWork =
                completed.Lifecycle;

            if (budget.BudgetMicroseconds != RequiredBudgetMicroseconds ||
                !EstimateCanonical(budget.FaceEstimateMicroseconds) ||
                !EstimateCanonical(budget.PrefilterEstimateMicroseconds) ||
                !EstimateCanonical(budget.CopyEstimateMicroseconds))
            {
                failures.Add(
                    $"Reflection raw row {index} does not contain the exact " +
                    "500us planner budget and bounded estimates.");
            }
            bool workUnitsCanonical =
                current.CaptureFaceUnitsThisFrame >= 0 &&
                current.PrefilterMipUnitsThisFrame >= 0 &&
                current.PublishCopyUnitsThisFrame >= 0 &&
                completedWork.CaptureFaceUnitsThisFrame >= 0 &&
                completedWork.PrefilterMipUnitsThisFrame >= 0 &&
                completedWork.PublishCopyUnitsThisFrame >= 0;
            if (!workUnitsCanonical)
            {
                failures.Add(
                    $"Reflection raw row {index} contains negative work units.");
            }

            if (!completed.Valid || !completed.GpuTimingRecorded)
            {
                if (frame.GpuCaptureMicroseconds != 0 ||
                    frame.GpuPrefilterMicroseconds != 0 ||
                    frame.GpuPublishMicroseconds != 0)
                {
                    failures.Add(
                        $"Reflection raw row {index} attributes GPU timings to " +
                        "an invalid or non-timed completed frame.");
                }
            }

            if (index == 0)
            {
                expectedFaceEstimate = budget.FaceEstimateMicroseconds;
                expectedPrefilterEstimate =
                    budget.PrefilterEstimateMicroseconds;
                expectedCopyEstimate = budget.CopyEstimateMicroseconds;
                expectedHistory = budget.HasTimingHistory;
                if (!expectedHistory &&
                    (expectedFaceEstimate != DefaultFaceEstimateMicroseconds ||
                     expectedPrefilterEstimate !=
                         DefaultPrefilterEstimateMicroseconds ||
                     expectedCopyEstimate != DefaultCopyEstimateMicroseconds))
                {
                    failures.Add(
                        "Reflection raw row 0 claims a cold planner with " +
                        "non-default estimates.");
                }
            }
            else
            {
                bool updated = false;
                if (completed.Valid && completed.GpuTimingRecorded)
                {
                    expectedFaceEstimate = UpdateEstimate(
                        expectedFaceEstimate,
                        completedWork.CaptureFaceUnitsThisFrame,
                        frame.GpuCaptureMicroseconds,
                        ref updated);
                    expectedPrefilterEstimate = UpdateEstimate(
                        expectedPrefilterEstimate,
                        completedWork.PrefilterMipUnitsThisFrame,
                        frame.GpuPrefilterMicroseconds,
                        ref updated);
                    expectedCopyEstimate = UpdateEstimate(
                        expectedCopyEstimate,
                        completedWork.PublishCopyUnitsThisFrame,
                        frame.GpuPublishMicroseconds,
                        ref updated);
                }
                expectedHistory |= updated;
                if (budget.FaceEstimateMicroseconds != expectedFaceEstimate ||
                    budget.PrefilterEstimateMicroseconds !=
                        expectedPrefilterEstimate ||
                    budget.CopyEstimateMicroseconds != expectedCopyEstimate ||
                    budget.HasTimingHistory != expectedHistory)
                {
                    failures.Add(
                        $"Reflection raw row {index} does not exactly replay " +
                        "the prior completed timing into the planner state.");
                }
            }

            if (!workUnitsCanonical)
                continue;
            try
            {
                long expectedReserved = checked(
                    (long)current.CaptureFaceUnitsThisFrame *
                        budget.FaceEstimateMicroseconds +
                    (long)current.PrefilterMipUnitsThisFrame *
                        budget.PrefilterEstimateMicroseconds +
                    (long)current.PublishCopyUnitsThisFrame *
                        budget.CopyEstimateMicroseconds);
                bool expectedExhausted =
                    budget.BudgetMicroseconds > 0 &&
                    expectedReserved >= budget.BudgetMicroseconds;
                if (expectedReserved > int.MaxValue ||
                    budget.ReservedMicroseconds != (int)expectedReserved ||
                    budget.BudgetExhausted != expectedExhausted)
                {
                    failures.Add(
                        $"Reflection raw row {index} reservation or exhausted " +
                        "state does not match its current work units and estimates.");
                }
            }
            catch (OverflowException)
            {
                failures.Add(
                    $"Reflection raw row {index} planner reservation overflowed.");
            }
        }
    }

    private static void ValidateLastDiagnosticsBinding(
        SampleBenchmarkReport report,
        SampleBenchmarkReflectionProbeRawFrame? last,
        ICollection<string> failures)
    {
        RendererDiagnostics? diagnostics = report.LastDiagnostics;
        if (last == null || diagnostics == null ||
            diagnostics.GpuTimingSupported != 1 ||
            last.CaptureFrameSerial != diagnostics.CaptureFrame.FrameSerial ||
            last.CaptureFrameSlot !=
                diagnostics.ReflectionProbeCurrentLifecycle.FrameSlot ||
            last.GpuTimingValid != diagnostics.GpuTimingValid ||
            last.GpuCaptureMicroseconds !=
                diagnostics.GpuReflectionProbeCaptureMicroseconds ||
            last.GpuPrefilterMicroseconds !=
                diagnostics.GpuReflectionProbePrefilterMicroseconds ||
            last.GpuPublishMicroseconds !=
                diagnostics.GpuReflectionProbePublishMicroseconds ||
            last.CurrentLifecycle !=
                diagnostics.ReflectionProbeCurrentLifecycle ||
            last.CompletedLifecycle !=
                diagnostics.ReflectionProbeCompletedLifecycle ||
            last.CurrentBudget !=
                diagnostics.ReflectionProbeCurrentCaptureBudget)
        {
            failures.Add(
                "Reflection raw final row does not exactly match every C3 field " +
                "in report LastDiagnostics.");
        }
    }

    private static int UpdateEstimate(
        int previous,
        int unitCount,
        long measuredMicroseconds,
        ref bool updated)
    {
        if (unitCount <= 0 || measuredMicroseconds <= 0)
            return previous;
        long perUnit = measuredMicroseconds / unitCount;
        if (measuredMicroseconds % unitCount != 0)
            perUnit++;
        int sample = (int)Math.Clamp(
            perUnit,
            1L,
            MaximumEstimateMicroseconds);
        updated = true;
        return (int)Math.Clamp(
            (previous * 3L + sample + 2L) / 4L,
            1L,
            MaximumEstimateMicroseconds);
    }

    private static bool EstimateCanonical(int estimate) =>
        estimate is >= 1 and <= MaximumEstimateMicroseconds;

    private static SampleReflectionProbeSlowFrame? CreateSlowFrame(
        IReadOnlyList<SampleBenchmarkReflectionProbeRawFrame> rows,
        int rowIndex)
    {
        SampleBenchmarkReflectionProbeRawFrame row = rows[rowIndex];
        ReflectionProbeLifecycleFrameSnapshot completed =
            row.CompletedLifecycle;
        ReflectionProbeLifecycleSnapshot lifecycle = completed.Lifecycle;
        bool hasSubmittedWork = HasWork(lifecycle);
        if (row.GpuTimingValid == 0 ||
            !completed.Valid ||
            !completed.GpuTimingRecorded ||
            !hasSubmittedWork)
        {
            return null;
        }

        long capture = Math.Max(0L, row.GpuCaptureMicroseconds);
        long prefilter = Math.Max(0L, row.GpuPrefilterMicroseconds);
        long publish = Math.Max(0L, row.GpuPublishMicroseconds);
        SubmittedBudget submitted = FindSubmittedBudget(
            row.MeasurementSampleIndex,
            completed,
            rows);
        return new SampleReflectionProbeSlowFrame(
            row.MeasurementSampleIndex,
            checked(capture + prefilter + publish),
            capture,
            prefilter,
            publish,
            completed,
            submitted.Available,
            submitted.MeasurementSampleIndex,
            submitted.FrameSlot,
            submitted.FrameSerial,
            submitted.Budget);
    }

    private static SubmittedBudget FindSubmittedBudget(
        int completedMeasurementSampleIndex,
        in ReflectionProbeLifecycleFrameSnapshot completed,
        IReadOnlyList<SampleBenchmarkReflectionProbeRawFrame> rows)
    {
        for (int index = completedMeasurementSampleIndex - 1;
             index >= 0;
             index--)
        {
            SampleBenchmarkReflectionProbeRawFrame candidate = rows[index];
            ReflectionProbeLifecycleFrameSnapshot current =
                candidate.CurrentLifecycle;
            if (!current.Valid ||
                current.FrameSerial != completed.FrameSerial ||
                current.FrameSlot != completed.FrameSlot)
            {
                continue;
            }
            return new SubmittedBudget(
                Available: true,
                MeasurementSampleIndex: index,
                FrameSlot: current.FrameSlot,
                FrameSerial: current.FrameSerial,
                Budget: candidate.CurrentBudget);
        }
        return SubmittedBudget.Unavailable;
    }

    private static bool EvidenceEqual(
        SampleReflectionProbeCaptureEvidence? stored,
        SampleReflectionProbeCaptureEvidence recomputed)
    {
        if (stored == null ||
            !string.Equals(
                stored.Schema,
                recomputed.Schema,
                StringComparison.Ordinal) ||
            stored.Applicable != recomputed.Applicable ||
            stored.SlowestFrames == null ||
            stored.SlowestFrames.Count != recomputed.SlowestFrames.Count)
        {
            return false;
        }
        for (int index = 0; index < stored.SlowestFrames.Count; index++)
        {
            if (stored.SlowestFrames[index] != recomputed.SlowestFrames[index])
                return false;
        }
        return true;
    }

    private static string CreateDigest(
        SampleBenchmarkReflectionProbeRawEvidence raw,
        SampleReflectionProbeCaptureEvidence recomputed)
    {
        var canonical = new StringBuilder(64 * 1024);
        canonical.Append(VerificationDigestSchema).Append('\n')
            .Append(raw.Schema).Append('|')
            .Append(raw.Applicable ? '1' : '0').Append('|')
            .Append(raw.MeasurementFrameCount.ToString(
                CultureInfo.InvariantCulture)).Append('|')
            .Append(raw.Frames.Count.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        foreach (SampleBenchmarkReflectionProbeRawFrame frame in raw.Frames)
        {
            canonical.Append(frame.Schema).Append('|')
                .Append(Invariant(frame.MeasurementSampleIndex)).Append('|')
                .Append(Invariant(frame.RouteFrameIndex)).Append('|')
                .Append(Invariant(frame.CaptureFrameSerial)).Append('|')
                .Append(Invariant(frame.CaptureFrameSlot)).Append('|')
                .Append(Invariant(frame.GpuTimingValid)).Append('|')
                .Append(Invariant(frame.GpuCaptureMicroseconds)).Append('|')
                .Append(Invariant(frame.GpuPrefilterMicroseconds)).Append('|')
                .Append(Invariant(frame.GpuPublishMicroseconds)).Append('|');
            AppendLifecycle(canonical, frame.CurrentLifecycle);
            canonical.Append('|');
            AppendLifecycle(canonical, frame.CompletedLifecycle);
            canonical.Append('|');
            AppendBudget(canonical, frame.CurrentBudget);
            canonical.Append('\n');
        }
        canonical.Append(recomputed.Schema).Append('|')
            .Append(recomputed.Applicable ? '1' : '0').Append('|')
            .Append(Invariant(recomputed.SlowestFrames.Count)).Append('\n');
        foreach (SampleReflectionProbeSlowFrame frame in
                 recomputed.SlowestFrames)
        {
            canonical.Append(Invariant(frame.MeasurementSampleIndex)).Append('|')
                .Append(Invariant(frame.CompletedGpuMicroseconds)).Append('|')
                .Append(Invariant(frame.GpuCaptureMicroseconds)).Append('|')
                .Append(Invariant(frame.GpuPrefilterMicroseconds)).Append('|')
                .Append(Invariant(frame.GpuPublishMicroseconds)).Append('|');
            AppendLifecycle(canonical, frame.CompletedLifecycle);
            canonical.Append('|')
                .Append(frame.SubmittedBudgetAvailable ? '1' : '0').Append('|')
                .Append(Invariant(frame.SubmittedBudgetMeasurementSampleIndex))
                .Append('|')
                .Append(Invariant(frame.SubmittedBudgetFrameSlot)).Append('|')
                .Append(Invariant(frame.SubmittedBudgetFrameSerial)).Append('|');
            AppendBudget(canonical, frame.SubmittedBudget);
            canonical.Append('\n');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendLifecycle(
        StringBuilder canonical,
        in ReflectionProbeLifecycleFrameSnapshot frame)
    {
        ReflectionProbeLifecycleSnapshot value = frame.Lifecycle;
        canonical.Append(frame.Valid ? '1' : '0').Append(':')
            .Append(Invariant(frame.FrameSlot)).Append(':')
            .Append(Invariant(frame.FrameSerial)).Append(':')
            .Append(frame.GpuTimingRecorded ? '1' : '0').Append(':')
            .Append(Invariant(value.QueuedCount)).Append(':')
            .Append(Invariant(value.ActiveCount)).Append(':')
            .Append(Invariant((int)value.State)).Append(':')
            .Append(Invariant(value.AwaitingGpuCompletionCount)).Append(':')
            .Append(Invariant(value.PublishedCount)).Append(':')
            .Append(Invariant(value.CapturesStartedThisFrame)).Append(':')
            .Append(Invariant(value.CapturesCompletedThisFrame)).Append(':')
            .Append(Invariant(value.CaptureFaceUnitsThisFrame)).Append(':')
            .Append(Invariant(value.PrefilterMipUnitsThisFrame)).Append(':')
            .Append(Invariant(value.PublishCopyUnitsThisFrame)).Append(':')
            .Append(Invariant(value.CapturesStartedTotal)).Append(':')
            .Append(Invariant(value.CapturesCompletedTotal)).Append(':')
            .Append(Invariant(value.CapturesPublishedTotal)).Append(':')
            .Append(Invariant(value.CaptureFaceUnitsTotal)).Append(':')
            .Append(Invariant(value.PrefilterMipUnitsTotal)).Append(':')
            .Append(Invariant(value.PublishCopyUnitsTotal));
    }

    private static void AppendBudget(
        StringBuilder canonical,
        in ReflectionProbeGpuBudgetSnapshot budget) =>
        canonical.Append(Invariant(budget.BudgetMicroseconds)).Append(':')
            .Append(Invariant(budget.ReservedMicroseconds)).Append(':')
            .Append(Invariant(budget.FaceEstimateMicroseconds)).Append(':')
            .Append(Invariant(budget.PrefilterEstimateMicroseconds)).Append(':')
            .Append(Invariant(budget.CopyEstimateMicroseconds)).Append(':')
            .Append(budget.HasTimingHistory ? '1' : '0').Append(':')
            .Append(budget.BudgetExhausted ? '1' : '0');

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(ulong value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static bool BudgetCanonical(
        in ReflectionProbeGpuBudgetSnapshot budget) =>
        budget.BudgetMicroseconds >= 0 &&
        budget.ReservedMicroseconds >= 0 &&
        budget.FaceEstimateMicroseconds >= 0 &&
        budget.PrefilterEstimateMicroseconds >= 0 &&
        budget.CopyEstimateMicroseconds >= 0;

    private static bool HasWork(
        in ReflectionProbeLifecycleSnapshot lifecycle) =>
        lifecycle.CaptureFaceUnitsThisFrame > 0 ||
        lifecycle.PrefilterMipUnitsThisFrame > 0 ||
        lifecycle.PublishCopyUnitsThisFrame > 0;

    private static bool IsSha256Identity(string? value) =>
        value != null &&
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;

    private readonly record struct SubmittedBudget(
        bool Available,
        int MeasurementSampleIndex,
        int FrameSlot,
        ulong FrameSerial,
        ReflectionProbeGpuBudgetSnapshot Budget)
    {
        public static SubmittedBudget Unavailable { get; } = new(
            Available: false,
            MeasurementSampleIndex: -1,
            FrameSlot: -1,
            FrameSerial: 0UL,
            Budget: default);
    }
}
