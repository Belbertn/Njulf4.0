using System;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Availability of one B1 observation. A readable publication is reported
/// only after the matching submission fence completed and the exact 64-byte
/// GPU header passed the versioned CPU validation contract.
/// </summary>
public enum SimpleDdgiReceiverFeedbackTelemetryState : byte
{
    Disabled = 0,
    ResourceIncomplete = 1,
    PendingGpuPublication = 2,
    Readable = 3,
    Faulted = 4
}

[Flags]
public enum SimpleDdgiReceiverFeedbackTimedStage : byte
{
    None = 0,
    Reset = 1 << 0,
    Capture = 1 << 1,
    RawRadix = 1 << 2,
    PartialBuildAndRadix = 1 << 3,
    ReduceAndFinalize = 1 << 4,
    All = Reset | Capture | RawRadix | PartialBuildAndRadix |
        ReduceAndFinalize
}

/// <summary>
/// Stable timestamp names for the five contiguous regions of the bounded B1
/// GPU transaction. The partial region includes construction and sorting of
/// both probe and fallback partials; this avoids hundreds of timestamp queries
/// in the 52-pass stable-radix sequence while preserving useful attribution.
/// </summary>
public static class SimpleDdgiReceiverFeedbackGpuTimingNames
{
    public const string Reset = "SimpleDdgiReceiverFeedback.Reset";
    public const string Capture = "SimpleDdgiReceiverFeedback.Capture";
    public const string RawRadix = "SimpleDdgiReceiverFeedback.RawRadix";
    public const string PartialBuildAndRadix =
        "SimpleDdgiReceiverFeedback.PartialBuildAndRadix";
    public const string ReduceAndFinalize =
        "SimpleDdgiReceiverFeedback.ReduceAndFinalize";
}

/// <summary>
/// Fence-complete timestamp-query results. A zero duration is meaningful only
/// when the corresponding bit is present in <see cref="AvailableStages"/>.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackStageTimings(
    long ResetMicroseconds,
    long CaptureMicroseconds,
    long RawRadixMicroseconds,
    long PartialBuildAndRadixMicroseconds,
    long ReduceAndFinalizeMicroseconds,
    SimpleDdgiReceiverFeedbackTimedStage AvailableStages)
{
    public static SimpleDdgiReceiverFeedbackStageTimings Empty { get; } = new(
        0L,
        0L,
        0L,
        0L,
        0L,
        SimpleDdgiReceiverFeedbackTimedStage.None);

    public long TotalMicroseconds => checked(
        ResetMicroseconds + CaptureMicroseconds + RawRadixMicroseconds +
        PartialBuildAndRadixMicroseconds + ReduceAndFinalizeMicroseconds);

    public SimpleDdgiReceiverFeedbackStageTimings NormalizeForPersistence()
    {
        SimpleDdgiReceiverFeedbackTimedStage stages =
            AvailableStages & SimpleDdgiReceiverFeedbackTimedStage.All;
        return new SimpleDdgiReceiverFeedbackStageTimings(
            (stages & SimpleDdgiReceiverFeedbackTimedStage.Reset) != 0
                ? Math.Max(0L, ResetMicroseconds)
                : 0L,
            (stages & SimpleDdgiReceiverFeedbackTimedStage.Capture) != 0
                ? Math.Max(0L, CaptureMicroseconds)
                : 0L,
            (stages & SimpleDdgiReceiverFeedbackTimedStage.RawRadix) != 0
                ? Math.Max(0L, RawRadixMicroseconds)
                : 0L,
            (stages &
                SimpleDdgiReceiverFeedbackTimedStage.PartialBuildAndRadix) != 0
                ? Math.Max(0L, PartialBuildAndRadixMicroseconds)
                : 0L,
            (stages &
                SimpleDdgiReceiverFeedbackTimedStage.ReduceAndFinalize) != 0
                ? Math.Max(0L, ReduceAndFinalizeMicroseconds)
                : 0L,
            stages);
    }
}

/// <summary>
/// Exact counters copied from a GPU header only after the resource manager
/// accepted the complete header for the current allocation and layout.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackPublicationTelemetry(
    bool Available,
    uint LayoutRevision,
    uint FeedbackGeneration,
    uint ViewportGeneration,
    ulong FrameSerial,
    uint AppendCount,
    uint DroppedCount,
    uint ProducerOverflowMask,
    uint RecordCapacity,
    uint ProbePartialCount,
    uint FallbackPartialCount,
    uint SummaryCount,
    uint FallbackSummaryCount,
    uint InvalidRecordCount,
    SimpleDdgiReceiverFeedbackGpuBankFlags Flags)
{
    public static SimpleDdgiReceiverFeedbackPublicationTelemetry Empty { get; } =
        default;

    [JsonIgnore]
    public double AppendUtilization => RecordCapacity == 0u
        ? 0.0
        : AppendCount / (double)RecordCapacity;

    [JsonIgnore]
    public double ProbeReductionRatio => ProbePartialCount == 0u
        ? 0.0
        : SummaryCount / (double)ProbePartialCount;

    [JsonIgnore]
    public double FallbackReductionRatio => FallbackPartialCount == 0u
        ? 0.0
        : FallbackSummaryCount / (double)FallbackPartialCount;

    public bool IsValid
    {
        get
        {
            if (!Available)
                return Equals(Empty);

            var header = new GPUSimpleDdgiReceiverFeedbackBankHeaderV2
            {
                LayoutRevision = LayoutRevision,
                EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel,
                FeedbackGeneration = FeedbackGeneration,
                ViewportGeneration = ViewportGeneration,
                FrameSerialLow = unchecked((uint)FrameSerial),
                FrameSerialHigh = unchecked((uint)(FrameSerial >> 32)),
                AppendCount = AppendCount,
                DroppedCount = DroppedCount,
                ProducerOverflowMask = ProducerOverflowMask,
                RecordCapacity = RecordCapacity,
                ProbePartialCount = ProbePartialCount,
                FallbackPartialCount = FallbackPartialCount,
                SummaryCount = SummaryCount,
                FallbackSummaryCount = FallbackSummaryCount,
                InvalidRecordCount = InvalidRecordCount,
                Flags = Flags
            };
            return SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(
                header);
        }
    }

    internal static SimpleDdgiReceiverFeedbackPublicationTelemetry
        FromValidatedHeader(
            in GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header) => new(
            Available: true,
            header.LayoutRevision,
            header.FeedbackGeneration,
            header.ViewportGeneration,
            ((ulong)header.FrameSerialHigh << 32) | header.FrameSerialLow,
            header.AppendCount,
            header.DroppedCount,
            header.ProducerOverflowMask,
            header.RecordCapacity,
            header.ProbePartialCount,
            header.FallbackPartialCount,
            header.SummaryCount,
            header.FallbackSummaryCount,
            header.InvalidRecordCount,
            header.Flags);
}

/// <summary>
/// Central B1 ownership evidence. Persistent record and summary banks remain
/// separate from the aliasable stable-sort scratch allocation.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackMemoryTelemetry(
    SimpleDdgiAdvancedMemoryUsage RecordBanks,
    SimpleDdgiAdvancedMemoryUsage SortScratch,
    SimpleDdgiAdvancedMemoryUsage ProbeSummaries)
{
    public static SimpleDdgiReceiverFeedbackMemoryTelemetry Empty { get; } = new(
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch),
        SimpleDdgiAdvancedMemoryUsage.Zero(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries));

    public ulong AllocatedBytes => checked(
        RecordBanks.AllocatedBytes + SortScratch.AllocatedBytes +
        ProbeSummaries.AllocatedBytes);

    public ulong PeakLiveBytes => checked(
        RecordBanks.PeakLiveBytes + SortScratch.PeakLiveBytes +
        ProbeSummaries.PeakLiveBytes);

    public ulong RetiredButLiveBytes => checked(
        RecordBanks.RetiredButLiveBytes + SortScratch.RetiredButLiveBytes +
        ProbeSummaries.RetiredButLiveBytes);

    public bool IsValid => RecordBanks.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackRecordBanks) &&
        SortScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackSortScratch) &&
        ProbeSummaries.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.ReceiverFeedbackProbeSummaries);

    public SimpleDdgiReceiverFeedbackMemoryTelemetry NormalizeForPersistence() =>
        IsValid ? this : Empty;
}

/// <summary>
/// Persisted B1 observability. This is execution evidence, not promotion
/// evidence: an AutoQualified mode still requires its authenticated manifest.
/// </summary>
public sealed record SimpleDdgiReceiverFeedbackDiagnostics
{
    public SimpleDdgiReceiverFeedbackTelemetryState State { get; init; } =
        SimpleDdgiReceiverFeedbackTelemetryState.Disabled;

    public SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics Runtime { get; init; } =
        SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;

    public SimpleDdgiReceiverFeedbackPublicationTelemetry Publication
    {
        get;
        init;
    } = SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty;

    public SimpleDdgiReceiverFeedbackStageTimings Timings { get; init; } =
        SimpleDdgiReceiverFeedbackStageTimings.Empty;

    public SimpleDdgiReceiverFeedbackMemoryTelemetry Memory { get; init; } =
        SimpleDdgiReceiverFeedbackMemoryTelemetry.Empty;

    public string Reason { get; init; } = "receiver-feedback-disabled";

    [JsonIgnore]
    public bool HasAuthoritativePublication =>
        State == SimpleDdgiReceiverFeedbackTelemetryState.Readable &&
        Publication.IsValid && Runtime.Resource.IsEffectivelyEnabled &&
        Runtime.Resource.PublishedBankIndex is 0 or 1 &&
        Runtime.Resource.PublishedGeneration == Publication.FeedbackGeneration;

    public static SimpleDdgiReceiverFeedbackDiagnostics Disabled { get; } = new();

    public SimpleDdgiReceiverFeedbackDiagnostics NormalizeForPersistence()
    {
        if (!Enum.IsDefined(State) || !IsRuntimeSane(Runtime) ||
            !Publication.IsValid || !Memory.IsValid)
        {
            return Disabled with
            {
                Reason = "receiver-feedback-telemetry-invalid"
            };
        }

        SimpleDdgiReceiverFeedbackTelemetryState state = State;
        SimpleDdgiReceiverFeedbackPublicationTelemetry publication = Publication;
        bool publicationMatchesRuntime = publication.Available &&
            Runtime.Resource.IsEffectivelyEnabled &&
            Runtime.Resource.PublishedBankIndex is 0 or 1 &&
            Runtime.Resource.PublishedGeneration == publication.FeedbackGeneration;
        if (publication.Available && !publicationMatchesRuntime)
        {
            state = SimpleDdgiReceiverFeedbackTelemetryState.Faulted;
            publication = SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty;
        }
        else if (state == SimpleDdgiReceiverFeedbackTelemetryState.Readable &&
                 !publicationMatchesRuntime)
        {
            state = SimpleDdgiReceiverFeedbackTelemetryState.Faulted;
        }

        if (Runtime.Resource.IsEffectivelyEnabled &&
            Runtime.Resource.AllocatedBytes != Memory.AllocatedBytes)
        {
            state = SimpleDdgiReceiverFeedbackTelemetryState.Faulted;
        }

        return this with
        {
            State = state,
            Runtime = Runtime with
            {
                Publication = publication,
                Detail = NormalizeReason(Runtime.Detail, "unknown")
            },
            Publication = publication,
            Timings = Timings.NormalizeForPersistence(),
            Memory = Memory.NormalizeForPersistence(),
            Reason = NormalizeReason(Reason, "unknown")
        };
    }

    private static bool IsRuntimeSane(
        in SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics runtime)
    {
        SimpleDdgiReceiverFeedbackGpuResourceSnapshot resource =
            runtime.Resource;
        if (!Enum.IsDefined(runtime.CapabilityReason) ||
            !Enum.IsDefined(resource.State) || resource.DescriptorCount > 4u ||
            resource.PublishedBankIndex is < -1 or > 1)
        {
            return false;
        }

        if (resource.PublishedBankIndex == -1 &&
            resource.PublishedGeneration != 0u)
        {
            return false;
        }

        return resource.IsEffectivelyEnabled
            ? resource.AllocationEpoch != 0UL &&
              resource.AllocatedBytes != 0UL && resource.DescriptorCount == 4u
            : resource.AllocatedBytes == 0UL && resource.DescriptorCount == 0u;
    }

    private static string NormalizeReason(string? value, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }
}
