using System;
using System.Text.Json.Serialization;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Availability of one C3 diagnostic sample. Admission, allocation, command
/// recording, and fence-complete GPU readback are deliberately distinct.
/// </summary>
public enum SimpleDdgiGuidingTelemetryState : byte
{
    Disabled = 0,
    ResourceIncomplete = 1,
    PendingGpuReadback = 2,
    Available = 3,
    Faulted = 4
}

[Flags]
public enum SimpleDdgiGuidingTimedStage : byte
{
    None = 0,
    Sample = 1 << 0,
    Train = 1 << 1,
    Build = 1 << 2,
    Validate = 1 << 3,
    All = Sample | Train | Build | Validate
}

/// <summary>
/// Fence-complete timestamp-query durations. A zero duration is meaningful
/// only when the corresponding bit is present in <see cref="AvailableStages"/>.
/// </summary>
public readonly record struct SimpleDdgiGuidingStageTimings(
    long SampleMicroseconds,
    long TrainMicroseconds,
    long BuildMicroseconds,
    long ValidateMicroseconds,
    SimpleDdgiGuidingTimedStage AvailableStages)
{
    public static SimpleDdgiGuidingStageTimings Empty { get; } =
        new(0L, 0L, 0L, 0L, SimpleDdgiGuidingTimedStage.None);

    public long TotalMicroseconds => checked(
        SampleMicroseconds + TrainMicroseconds + BuildMicroseconds +
        ValidateMicroseconds);

    public SimpleDdgiGuidingStageTimings NormalizeForPersistence()
    {
        SimpleDdgiGuidingTimedStage stages =
            AvailableStages & SimpleDdgiGuidingTimedStage.All;
        return new SimpleDdgiGuidingStageTimings(
            (stages & SimpleDdgiGuidingTimedStage.Sample) != 0
                ? Math.Max(0L, SampleMicroseconds)
                : 0L,
            (stages & SimpleDdgiGuidingTimedStage.Train) != 0
                ? Math.Max(0L, TrainMicroseconds)
                : 0L,
            (stages & SimpleDdgiGuidingTimedStage.Build) != 0
                ? Math.Max(0L, BuildMicroseconds)
                : 0L,
            (stages & SimpleDdgiGuidingTimedStage.Validate) != 0
                ? Math.Max(0L, ValidateMicroseconds)
                : 0L,
            stages);
    }
}

/// <summary>
/// Central ownership evidence for C3. Persistent banks and the source-cache
/// sidecar are reported separately from the aliasable transaction workspace.
/// </summary>
public readonly record struct SimpleDdgiGuidingMemoryTelemetry(
    SimpleDdgiAdvancedMemoryUsage HistoryBanks,
    SimpleDdgiAdvancedMemoryUsage BuildScratch)
{
    public static SimpleDdgiGuidingMemoryTelemetry Empty { get; } = new(
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch));

    public ulong AllocatedBytes => checked(
        HistoryBanks.AllocatedBytes + BuildScratch.AllocatedBytes);

    public ulong PeakLiveBytes => checked(
        HistoryBanks.PeakLiveBytes + BuildScratch.PeakLiveBytes);

    public ulong RetiredButLiveBytes => checked(
        HistoryBanks.RetiredButLiveBytes +
        BuildScratch.RetiredButLiveBytes);

    public bool IsValid => HistoryBanks.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingHistoryBanks) &&
        BuildScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch);

    public SimpleDdgiGuidingMemoryTelemetry NormalizeForPersistence() =>
        IsValid ? this : Empty;
}

/// <summary>
/// Persisted C3 runtime evidence. This record reports what actually existed,
/// recorded, and completed; it is never used as qualification evidence and
/// cannot promote <c>AutoQualified</c> by itself.
/// </summary>
public sealed record SimpleDdgiDirectionalGuidingDiagnostics
{
    public SimpleDdgiGuidingTelemetryState State { get; init; } =
        SimpleDdgiGuidingTelemetryState.Disabled;

    public SimpleDdgiGuidingGpuRuntimeDiagnostics Runtime { get; init; } =
        SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;

    public SimpleDdgiGuidingFrameCoordinatorDiagnostics Frame { get; init; } =
        SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled;

    public SimpleDdgiGuidingStageTimings Timings { get; init; } =
        SimpleDdgiGuidingStageTimings.Empty;

    public SimpleDdgiGuidingMemoryTelemetry Memory { get; init; } =
        SimpleDdgiGuidingMemoryTelemetry.Empty;

    public string Reason { get; init; } = "directional-guiding-disabled";

    [JsonIgnore]
    public bool HasAuthoritativeSampleReadback =>
        State == SimpleDdgiGuidingTelemetryState.Available &&
        Frame.CompletedFrameSerial != 0UL && Frame.SampleReadbackValid;

    public static SimpleDdgiDirectionalGuidingDiagnostics Disabled { get; } =
        new();

    public SimpleDdgiDirectionalGuidingDiagnostics NormalizeForPersistence()
    {
        if (!Enum.IsDefined(State) || !IsRuntimeSane(Runtime) ||
            !IsFrameSane(Frame))
        {
            return Disabled with
            {
                Reason = "directional-guiding-telemetry-invalid"
            };
        }

        SimpleDdgiGuidingTelemetryState state = State;
        if (state == SimpleDdgiGuidingTelemetryState.Available &&
            (Frame.CompletedFrameSerial == 0UL || !Frame.SampleReadbackValid))
        {
            state = SimpleDdgiGuidingTelemetryState.Faulted;
        }

        return this with
        {
            State = state,
            Runtime = Runtime with
            {
                Detail = NormalizeReason(Runtime.Detail, "unknown")
            },
            Frame = Frame with
            {
                State = NormalizeReason(Frame.State, "unknown")
            },
            Timings = Timings.NormalizeForPersistence(),
            Memory = Memory.NormalizeForPersistence(),
            Reason = NormalizeReason(Reason, "unknown")
        };
    }

    private static bool IsRuntimeSane(
        in SimpleDdgiGuidingGpuRuntimeDiagnostics runtime)
    {
        SimpleDdgiGuidingRuntimeSnapshot resource = runtime.Resource;
        if (!Enum.IsDefined(runtime.CapabilityReason) ||
            !Enum.IsDefined(resource.State) || resource.AllocatedBytes > 0UL &&
            resource.AllocationEpoch == 0UL || resource.DescriptorCount > 3U ||
            resource.ReadBankIndex is < -1 or > 1 ||
            resource.WriteBankIndex is < 0 or > 1)
        {
            return false;
        }

        return resource.IsEffectivelyEnabled
            ? resource.AllocatedBytes > 0UL && resource.AllocationEpoch > 0UL &&
                resource.DescriptorCount == 3U
            : resource.AllocatedBytes == 0UL && resource.DescriptorCount == 0U;
    }

    private static bool IsFrameSane(
        in SimpleDdgiGuidingFrameCoordinatorDiagnostics frame)
    {
        if (frame.GuidedProbeCount < 0 || frame.SampleRequestCount < 0 ||
            frame.CompletedSampleCount < 0 ||
            frame.UploadedBytes > frame.WorkspaceBytes)
        {
            return false;
        }

        if (!frame.SampleReadbackValid)
            return true;

        // Current-frame work and the most recent fence-complete result are
        // intentionally reported together. Once the next ring slot is
        // prepared, SampleRequestCount belongs to that newer frame and may be
        // zero (or simply different); validate the retained completion against
        // its own immutable telemetry instead.
        return frame.CompletedFrameSerial != 0UL &&
            frame.SampleTelemetry.RequestCount <= int.MaxValue &&
            frame.CompletedSampleCount ==
                (int)frame.SampleTelemetry.RequestCount &&
            frame.SampleTelemetry.IsConsistent(
                frame.SampleValidationCounters);
    }

    private static string NormalizeReason(string? value, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.Length <= 512
            ? normalized
            : normalized[..512];
    }
}
