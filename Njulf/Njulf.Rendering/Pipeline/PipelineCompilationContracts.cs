using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Stable, renderer-owned identity for a logical pipeline variant. The value is
/// also used by qualification reports and the optional binary-store manifest.
/// </summary>
internal readonly record struct PipelineArtifactId
{
    internal PipelineArtifactId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A pipeline artifact id is required.", nameof(value));
        Value = value.Trim();
    }

    internal string Value { get; }

    public override string ToString() => Value;

    public static implicit operator PipelineArtifactId(string value) => new(value);
}

internal enum PipelineArtifactSource
{
    Unknown,
    WritableBinary,
    SeedBinary,
    ApplicationCache,
    Compiled
}

internal enum PipelineCacheUsage
{
    Shared,
    Bypass
}

internal readonly record struct PipelineCreationObservation(
    PipelineArtifactId ArtifactId,
    PipelineArtifactSource Source,
    long WallMicroseconds,
    ulong DriverDurationNanoseconds,
    bool FeedbackValid,
    bool ApplicationCacheHit,
    bool CompileRequired,
    bool RenderCritical,
    int ConcurrentCreationCount,
    int StageCount);

/// <summary>
/// Exact set of pipeline artifacts that must be ready before a scene or
/// settings transaction can be published.
/// </summary>
internal sealed class PipelineStartupManifest
{
    private readonly HashSet<PipelineArtifactId> _required = new();

    internal PipelineStartupManifest(string name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
    }

    internal string Name { get; }

    internal IReadOnlyCollection<PipelineArtifactId> Required =>
        new ReadOnlyCollection<PipelineArtifactId>(_required.ToArray());

    internal PipelineStartupManifest Require(PipelineArtifactId artifactId)
    {
        _required.Add(artifactId);
        return this;
    }

    internal bool Contains(PipelineArtifactId artifactId) =>
        _required.Contains(artifactId);
}

/// <summary>
/// Bounded, de-duplicating scheduler for native pipeline compilation. Vulkan
/// calls are not cancelled after they enter the driver; cancellation prevents
/// only work that has not acquired a worker slot.
/// </summary>
internal sealed class PipelineCompilationScheduler : IDisposable
{
    internal const string WorkerCountEnvironmentVariable =
        "NJULF_PIPELINE_COMPILE_WORKERS";
    internal const int MaximumWorkerCount = 8;

    private readonly ConcurrentDictionary<PipelineArtifactId, Task> _jobs = new();
    private readonly SemaphoreSlim _workerSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private int _shutdownRequested;
    private int _disposed;

    internal PipelineCompilationScheduler(int? workerCount = null)
    {
        WorkerCount = workerCount ?? ResolveWorkerCount(
            Environment.GetEnvironmentVariable(WorkerCountEnvironmentVariable),
            Environment.ProcessorCount);
        if (WorkerCount is < 1 or > MaximumWorkerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerCount),
                $"Pipeline worker count must be between 1 and {MaximumWorkerCount}.");
        }

        _workerSlots = new SemaphoreSlim(WorkerCount, WorkerCount);
    }

    internal int WorkerCount { get; }

    internal Task Schedule(
        PipelineArtifactId artifactId,
        Action<CancellationToken> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        return _jobs.GetOrAdd(
            artifactId,
            _ => Task.Run(async () =>
            {
                await _workerSlots.WaitAsync(_shutdown.Token)
                    .ConfigureAwait(false);
                try
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    compile(_shutdown.Token);
                }
                finally
                {
                    _workerSlots.Release();
                }
            }, _shutdown.Token));
    }

    internal void Wait(PipelineStartupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Task[] requiredJobs = manifest.Required
            .Select(id => _jobs.TryGetValue(id, out Task? job)
                ? job
                : throw new InvalidOperationException(
                    $"Required pipeline artifact '{id}' was not scheduled."))
            .ToArray();
        Task.WhenAll(requiredJobs).GetAwaiter().GetResult();
    }

    internal void WaitForAll()
    {
        Task[] jobs = _jobs.Values.ToArray();
        Task.WhenAll(jobs).GetAwaiter().GetResult();
    }

    internal void CancelPending()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
            _shutdown.Cancel();
    }

    internal static int ResolveWorkerCount(
        string? configuredValue,
        int processorCount)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            if (!int.TryParse(configuredValue, out int configured) ||
                configured is < 1 or > MaximumWorkerCount)
            {
                throw new InvalidOperationException(
                    $"{WorkerCountEnvironmentVariable} must be an integer " +
                    $"between 1 and {MaximumWorkerCount}.");
            }

            return configured;
        }

        return Math.Min(
            4,
            Math.Max(1, Math.Max(1, processorCount) / 4));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelPending();
        try
        {
            WaitForAll();
        }
        catch (OperationCanceledException)
        {
            // Pending work is intentionally abandoned during renderer teardown.
        }
        finally
        {
            _shutdown.Dispose();
            _workerSlots.Dispose();
        }
    }
}
