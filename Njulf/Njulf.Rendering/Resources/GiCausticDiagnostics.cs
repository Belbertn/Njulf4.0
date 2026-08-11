using System;
using System.Text.Json.Serialization;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Availability of one C4 runtime observation.  A readable cache is reported
/// only after the matching Vulkan submission fence has completed and the GPU
/// publication header has passed the full CPU validation contract.
/// </summary>
public enum GiCausticTelemetryState : byte
{
    Disabled = 0,
    ResourceIncomplete = 1,
    PendingGpuPublication = 2,
    Readable = 3,
    Faulted = 4
}

[Flags]
public enum GiCausticTimedStage : byte
{
    None = 0,
    Task = 1 << 0,
    Trace = 1 << 1,
    CacheBuild = 1 << 2,
    Resolve = 1 << 3,
    Composite = 1 << 4,
    All = Task | Trace | CacheBuild | Resolve | Composite
}

/// <summary>Fence-complete timing-query results for C4's independently named passes.</summary>
public readonly record struct GiCausticStageTimings(
    long TaskMicroseconds,
    long TraceMicroseconds,
    long CacheBuildMicroseconds,
    long ResolveMicroseconds,
    long CompositeMicroseconds,
    GiCausticTimedStage AvailableStages)
{
    public static GiCausticStageTimings Empty { get; } = new(
        0L, 0L, 0L, 0L, 0L, GiCausticTimedStage.None);

    public long TotalMicroseconds => checked(
        TaskMicroseconds + TraceMicroseconds + CacheBuildMicroseconds +
        ResolveMicroseconds + CompositeMicroseconds);

    public GiCausticStageTimings NormalizeForPersistence()
    {
        GiCausticTimedStage stages = AvailableStages & GiCausticTimedStage.All;
        return new GiCausticStageTimings(
            (stages & GiCausticTimedStage.Task) != 0
                ? Math.Max(0L, TaskMicroseconds)
                : 0L,
            (stages & GiCausticTimedStage.Trace) != 0
                ? Math.Max(0L, TraceMicroseconds)
                : 0L,
            (stages & GiCausticTimedStage.CacheBuild) != 0
                ? Math.Max(0L, CacheBuildMicroseconds)
                : 0L,
            (stages & GiCausticTimedStage.Resolve) != 0
                ? Math.Max(0L, ResolveMicroseconds)
                : 0L,
            (stages & GiCausticTimedStage.Composite) != 0
                ? Math.Max(0L, CompositeMicroseconds)
                : 0L,
            stages);
    }
}

/// <summary>
/// Exact counters copied from a GPU header only after
/// <see cref="GiCausticGpuResourceManager.CompleteBuild"/> accepted that
/// header.  This prevents partially written, stale, overflowed, or
/// self-described GPU data from appearing authoritative in diagnostics.
/// </summary>
public readonly record struct GiCausticPublicationTelemetry(
    bool Available,
    uint CacheGeneration,
    ulong RevisionFingerprint,
    uint BuildSerial,
    uint TaskCapacity,
    uint CandidateInputCount,
    uint CandidateCount,
    uint RetainedPhotonCount,
    uint OccupiedCellCount,
    uint OverflowCount,
    uint PhotonBankIndex,
    uint CacheBankIndex,
    GiCausticGpuCachePublicationFlags PublicationFlags)
{
    public static GiCausticPublicationTelemetry Empty { get; } = default;

    [JsonIgnore]
    public double RetentionRatio => CandidateCount == 0u
        ? 0.0
        : RetainedPhotonCount / (double)CandidateCount;

    public bool IsValid => !Available
        ? Equals(Empty)
        : CacheGeneration != 0u && RevisionFingerprint != 0UL &&
          CandidateInputCount <= TaskCapacity &&
          RetainedPhotonCount <= CandidateCount && OverflowCount == 0u &&
          PhotonBankIndex is 0u or 1u && CacheBankIndex is 0u or 1u &&
          (PublicationFlags & (GiCausticGpuCachePublicationFlags.Initialized |
                               GiCausticGpuCachePublicationFlags.BuildComplete)) ==
              (GiCausticGpuCachePublicationFlags.Initialized |
               GiCausticGpuCachePublicationFlags.BuildComplete) &&
          (PublicationFlags & (GiCausticGpuCachePublicationFlags.Invalid |
                               GiCausticGpuCachePublicationFlags.Invalidated |
                               GiCausticGpuCachePublicationFlags.TaskOverflow |
                               GiCausticGpuCachePublicationFlags.CandidateOverflow |
                               GiCausticGpuCachePublicationFlags.CellTableOverflow |
                               GiCausticGpuCachePublicationFlags
                                   .DeterministicBuildBackendUnavailable)) == 0;

    internal static GiCausticPublicationTelemetry FromValidatedHeader(
        in GPUCausticCacheHeaderV1 header) => new(
            Available: true,
            header.CacheGeneration,
            header.RevisionFingerprint,
            header.BuildSerial,
            header.TaskCapacity,
            header.CandidateInputCount,
            header.CandidateCount,
            header.RetainedPhotonCount,
            header.OccupiedCellCount,
            header.OverflowCount,
            header.PhotonBankIndex,
            header.CacheBankIndex,
            header.PublicationFlags);
}

/// <summary>
/// Persisted C4 observability.  It reports live allocation and fence-complete
/// publication facts, but is never accepted as promotion evidence in place of
/// an authenticated advanced-GI qualification manifest.
/// </summary>
public sealed record GiCausticDiagnostics
{
    public GiCausticTelemetryState State { get; init; } =
        GiCausticTelemetryState.Disabled;

    public GiCausticVulkanRuntimeDiagnostics Runtime { get; init; } =
        GiCausticVulkanRuntimeDiagnostics.Disabled;

    public GiCausticPublicationTelemetry Publication { get; init; } =
        GiCausticPublicationTelemetry.Empty;

    public GiCausticStageTimings Timings { get; init; } =
        GiCausticStageTimings.Empty;

    public GiCausticGpuMemoryRequirements Memory { get; init; } =
        GiCausticGpuMemoryRequirements.Empty;

    public string Reason { get; init; } = "caustic-disabled";

    [JsonIgnore]
    public bool HasAuthoritativePublication =>
        State == GiCausticTelemetryState.Readable && Publication.IsValid &&
        Runtime.Resource.HasReadableCache &&
        Runtime.Resource.ReadableGeneration == Publication.CacheGeneration;

    public static GiCausticDiagnostics Disabled { get; } = new();

    public GiCausticDiagnostics NormalizeForPersistence()
    {
        if (!Enum.IsDefined(State) || !IsRuntimeSane(Runtime) ||
            !IsMemorySane(Memory) || !Publication.IsValid)
        {
            return Disabled with { Reason = "caustic-telemetry-invalid" };
        }

        GiCausticTelemetryState state = State;
        if (state == GiCausticTelemetryState.Readable &&
            (!Runtime.Resource.HasReadableCache || !Publication.Available ||
             Runtime.Resource.ReadableGeneration !=
                 Publication.CacheGeneration))
        {
            state = GiCausticTelemetryState.Faulted;
        }
        else if (state != GiCausticTelemetryState.Readable &&
                 Publication.Available && !Runtime.Resource.HasReadableCache)
        {
            return Disabled with { Reason = "caustic-publication-is-stale" };
        }

        return this with
        {
            State = state,
            Runtime = Runtime with
            {
                Detail = NormalizeReason(Runtime.Detail, "unknown")
            },
            Timings = Timings.NormalizeForPersistence(),
            Reason = NormalizeReason(Reason, "unknown")
        };
    }

    private static bool IsRuntimeSane(
        in GiCausticVulkanRuntimeDiagnostics runtime)
    {
        GiCausticGpuRuntimeSnapshot resource = runtime.Resource;
        if (!Enum.IsDefined(runtime.CapabilityReason) ||
            !Enum.IsDefined(resource.State) || resource.DescriptorCount > 4u ||
            resource.PhotonReadBankIndex is < -1 or > 1 ||
            resource.PhotonWriteBankIndex is < 0 or > 1 ||
            resource.CacheReadBankIndex is < -1 or > 1 ||
            resource.CacheWriteBankIndex is < 0 or > 1)
        {
            return false;
        }

        return resource.IsEffectivelyEnabled
            ? resource.AllocationEpoch != 0UL && resource.AllocatedBytes != 0UL &&
              resource.DescriptorCount == 4u
            : resource.AllocatedBytes == 0UL && resource.DescriptorCount == 0u;
    }

    private static bool IsMemorySane(
        in GiCausticGpuMemoryRequirements memory) =>
        memory.PhotonRecords.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticPhotonRecords) &&
        memory.CellTableAndSortScratch.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticCellTableAndSortScratch) &&
        memory.History.IsValidFor(
            SimpleDdgiAdvancedMemoryCategory.CausticHistory) &&
        memory.AllocatedBytes <= memory.RequiredBytes;

    private static string NormalizeReason(string? value, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512];
    }
}
