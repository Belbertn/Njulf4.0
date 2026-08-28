namespace Njulf.Assets;

/// <summary>Scheduling importance for explicit background/preload requests.</summary>
public enum ContentLoadPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>Observable transitions for asynchronous content admission.</summary>
public enum ContentLoadStage
{
    Queued,
    Started,
    Preparing,
    WaitingForUpload,
    Uploading,
    AwaitingGpu,
    Ready,
    Cancelled,
    Failed
}

public sealed record ContentLoadProgressEvent(
    string Path,
    ContentLoadPriority Priority,
    ContentLoadStage Stage,
    long EstimatedBytes = 0,
    string? Message = null,
    long CompletedBytes = 0,
    long TotalBytes = 0)
{
    /// <summary>
    /// Indicates that the event only proves the load is still active. A
    /// heartbeat must not be interpreted as measurable byte or stage
    /// advancement.
    /// </summary>
    public bool IsHeartbeat { get; init; }
}

public interface IContentLoadProgressSink
{
    void Report(ContentLoadProgressEvent progress);
}

/// <summary>
/// Host-owned bridge for renderer mutation. Implementations enqueue the
/// callback onto an approved render/device context; ContentManager never sends
/// uploads to <c>Task.Run</c> on its own.
/// </summary>
public interface IContentUploadDispatcher
{
    Task<T> DispatchAsync<T>(Func<T> callback, CancellationToken cancellationToken);

    Task<T> DispatchAsync<T>(
        IContentUploadWork<T> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-frame limits supplied to cooperative renderer upload work. A work item
/// must treat both values as upper bounds for the next indivisible step rather
/// than assuming the dispatcher can interrupt a callback already in progress.
/// </summary>
public readonly record struct ContentUploadSliceBudget(
    TimeSpan RemainingCpuTime,
    long MaximumSubmissionBytes)
{
    public static ContentUploadSliceBudget Unbounded { get; } = new(
        TimeSpan.Zero,
        long.MaxValue);
}

public enum ContentUploadStepStatus
{
    Yielded,
    Completed,
    Cancelled
}

/// <summary>Observable result of one bounded upload step.</summary>
public readonly record struct ContentUploadStepResult(
    ContentUploadStepStatus Status,
    long CompletedBytes = 0,
    long TotalBytes = 0,
    string? Detail = null)
{
    public bool IsTerminal => Status is
        ContentUploadStepStatus.Completed or
        ContentUploadStepStatus.Cancelled;

    public static ContentUploadStepResult Yield(
        long completedBytes = 0,
        long totalBytes = 0,
        string? detail = null) =>
        new(
            ContentUploadStepStatus.Yielded,
            completedBytes,
            totalBytes,
            detail);

    public static ContentUploadStepResult Complete(
        long completedBytes = 0,
        long totalBytes = 0,
        string? detail = null) =>
        new(
            ContentUploadStepStatus.Completed,
            completedBytes,
            totalBytes,
            detail);

    public static ContentUploadStepResult Cancelled(
        long completedBytes = 0,
        long totalBytes = 0,
        string? detail = null) =>
        new(
            ContentUploadStepStatus.Cancelled,
            completedBytes,
            totalBytes,
            detail);
}

/// <summary>
/// Renderer-owned upload transaction that can yield between bounded steps.
/// Cancellation is requested on the render thread and may require additional
/// steps to drain submitted GPU work before the item reports Cancelled.
/// </summary>
public interface IContentUploadWork<out T>
{
    ContentUploadStepResult ExecuteStep(
        in ContentUploadSliceBudget budget);

    T GetResult();

    void RequestCancellation();
}

/// <summary>
/// Render-loop side of an <see cref="IContentUploadDispatcher"/>. Hosts call
/// this once per frame from the thread that owns renderer mutations. The
/// callback limit is deliberately explicit: a queued model upload can be
/// expensive even when its disk/decode phase completed in the background.
/// </summary>
public interface IContentUploadPump
{
    int PendingCount { get; }

    ContentUploadPumpResult ProcessFrame(
        TimeSpan cpuBudget,
        int maximumCallbacks = 1,
        long maximumSubmissionBytes = 8L * 1024L * 1024L);
}

public readonly record struct ContentUploadPumpResult(
    int ProcessedCount,
    int CancelledCount,
    int FailedCount,
    long ElapsedMicroseconds,
    int RemainingCount)
{
    public bool BudgetExceeded(TimeSpan budget) =>
        budget > TimeSpan.Zero &&
        ElapsedMicroseconds > budget.TotalMicroseconds;
}

public sealed record ContentPreloadRequest(
    string Path,
    ContentLoadPriority Priority = ContentLoadPriority.Normal,
    long EstimatedBytes = 0);

public sealed record ContentPreloadOptions
{
    public int MaxConcurrency { get; init; } = 1;
    public long MaxInflightBytes { get; init; } = 256L * 1024L * 1024L;
    public ContentLoadOptions? LoadOptions { get; init; }
    public IContentLoadProgressSink? Progress { get; init; }
}

public sealed record ContentPreloadItemResult<T>(
    ContentPreloadRequest Request,
    T? Asset,
    Exception? Failure,
    bool Cancelled);

public sealed record ContentPreloadResult<T>(
    IReadOnlyList<ContentPreloadItemResult<T>> Items)
{
    public int ReadyCount => Items.Count(item => item.Failure is null && !item.Cancelled);
    public int FailedCount => Items.Count(item => item.Failure is not null);
    public int CancelledCount => Items.Count(item => item.Cancelled);
}

/// <summary>
/// Additional asynchronous contract. The original synchronous
/// <c>IContentManager</c> remains source compatible and continues to use the
/// same cache ownership pipeline.
/// </summary>
public interface IAsyncContentManager
{
    Task<T> LoadAsync<T>(
        string path,
        ContentLoadOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ContentPreloadResult<T>> PreloadAsync<T>(
        IEnumerable<ContentPreloadRequest> requests,
        ContentPreloadOptions? options = null,
        CancellationToken cancellationToken = default);
}
