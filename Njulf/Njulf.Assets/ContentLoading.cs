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
    WaitingForUpload,
    Ready,
    Cancelled,
    Failed
}

public sealed record ContentLoadProgressEvent(
    string Path,
    ContentLoadPriority Priority,
    ContentLoadStage Stage,
    long EstimatedBytes = 0,
    string? Message = null);

public interface IContentLoadProgressSink
{
    void Report(ContentLoadProgressEvent progress);
}

/// <summary>
/// Host-owned bridge for renderer mutation. Implementations enqueue the
/// callback onto an approved render/device context; ContentManager never sends
/// uploads to <see cref="Task.Run"/> on its own.
/// </summary>
public interface IContentUploadDispatcher
{
    Task<T> DispatchAsync<T>(Func<T> callback, CancellationToken cancellationToken);
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
