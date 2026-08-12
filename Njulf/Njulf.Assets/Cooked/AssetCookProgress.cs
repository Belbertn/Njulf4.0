namespace Njulf.Assets.Cooked;

/// <summary>
/// Stable, presentation-independent events emitted while cooking model assets.
/// Consumers must treat every member other than <see cref="Kind"/> as optional:
/// an event only carries facts that are known at the point it is emitted.
/// </summary>
public enum AssetCookProgressEventKind
{
    RunStarted,
    RunCompleted,
    RunFailed,
    RunCancelled,
    DiscoveryStarted,
    DiscoveryCompleted,
    AssetStarted,
    AssetSkipped,
    AssetCompleted,
    AssetFailed,
    AssetCancelled,
    StageStarted,
    StageCompleted,
    IncrementalCompleted,
    MaterialStarted,
    MaterialCompleted,
    TextureStarted,
    TextureCompleted
}

/// <summary>Major, externally observable model-cook stages.</summary>
public enum AssetCookStage
{
    Prepare,
    IncrementalCheck,
    Import,
    Mesh,
    MaterialsTextures,
    Serialize,
    Sign,
    Publish,
    ReportDatabase
}

/// <summary>Terminal or item outcome carried by a progress event.</summary>
public enum AssetCookProgressOutcome
{
    Succeeded,
    Skipped,
    Failed,
    Cancelled,
    Cooked,
    Reused,
    Deduplicated
}

/// <summary>Authoritative result of the existing incremental decision.</summary>
public enum AssetCookIncrementalDecision
{
    Cook,
    Skip
}

/// <summary>
/// The first authoritative reason an asset did not qualify for an incremental
/// skip. Values intentionally describe existing checks rather than performing
/// extra validation only for diagnostics.
/// </summary>
public enum AssetCookIncrementalReason
{
    Unchanged,
    Forced,
    DatabaseMiss,
    PreviousStatus,
    SourceChanged,
    SettingsChanged,
    DependencyChanged,
    ToolChanged,
    OutputMissing,
    OutputHashMismatch
}

/// <summary>
/// A typed cooker event. Durations are monotonic integer milliseconds and are
/// deliberately named by scope so terminal and automation consumers cannot
/// confuse stage, item, asset, and run elapsed time.
/// </summary>
public sealed record AssetCookProgressEvent
{
    public AssetCookProgressEvent(AssetCookProgressEventKind kind) => Kind = kind;

    public AssetCookProgressEventKind Kind { get; }
    public string? SourcePath { get; init; }
    public string? OutputPath { get; init; }
    public string? CookMode { get; init; }
    public int? Jobs { get; init; }
    public long? MaxInflightBytes { get; init; }
    public int? AssetIndex { get; init; }
    public int? AssetCount { get; init; }
    public AssetCookStage? Stage { get; init; }
    public AssetCookProgressOutcome? Outcome { get; init; }
    public AssetCookIncrementalDecision? IncrementalDecision { get; init; }
    public AssetCookIncrementalReason? IncrementalReason { get; init; }
    public int? ItemIndex { get; init; }
    public int? ItemCount { get; init; }
    public string? ItemName { get; init; }
    public int? MaterialCount { get; init; }
    public int? TextureSlotCount { get; init; }
    public int? MeshCount { get; init; }
    public int? TextureCount { get; init; }
    public int? WarningCount { get; init; }
    public int? CookedCount { get; init; }
    public int? SkippedCount { get; init; }
    public int? FailedCount { get; init; }
    public string? Backend { get; init; }
    public string? Message { get; init; }
    public long? StageElapsedMilliseconds { get; init; }
    public long? ItemElapsedMilliseconds { get; init; }
    public long? AssetElapsedMilliseconds { get; init; }
    public long? TotalElapsedMilliseconds { get; init; }
}

/// <summary>
/// Receives progress without imposing a terminal, logging, or synchronization
/// policy on the cooker. Implementations must be safe for concurrent calls
/// when a folder cook uses more than one worker.
/// </summary>
public interface IAssetCookProgressSink
{
    void Report(AssetCookProgressEvent progress);
}

/// <summary>
/// Scheduling controls for one folder cook. They are intentionally separate
/// from <see cref="ModelCookOptions"/> because worker count and memory budget
/// are operational policy, never part of a package's cook identity.
/// </summary>
public sealed record AssetCookFolderOptions
{
    /// <summary>Serial cooking remains the conservative default.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Maximum estimated source bytes admitted to concurrently active work.
    /// One input larger than the budget is admitted exclusively so a valid
    /// asset cannot remain permanently queued.
    /// </summary>
    public long MaxInflightBytes { get; init; } = 512L * 1024L * 1024L;
}
