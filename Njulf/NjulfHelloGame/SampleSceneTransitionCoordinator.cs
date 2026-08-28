using System.Diagnostics;
using Njulf.Assets;

namespace NjulfHelloGame;

internal enum SampleSceneTransitionPhase
{
    Idle,
    WaitingForLoadingFrame,
    Decoding,
    WaitingForUpload,
    Committing,
    Completed,
    Cancelled,
    Failed
}

internal sealed record SampleSceneTransitionSnapshot(
    long Generation,
    SampleSceneKind Target,
    SampleSceneTransitionPhase Phase,
    double Progress,
    long ElapsedMicroseconds,
    string Detail,
    Exception? Failure = null)
{
    public bool Active => Phase is
        SampleSceneTransitionPhase.WaitingForLoadingFrame or
        SampleSceneTransitionPhase.Decoding or
        SampleSceneTransitionPhase.WaitingForUpload or
        SampleSceneTransitionPhase.Committing;

    public static SampleSceneTransitionSnapshot Idle { get; } = new(
        0,
        default,
        SampleSceneTransitionPhase.Idle,
        0,
        0,
        string.Empty);
}

/// <summary>
/// Owns request generations and asynchronous preload state. The host calls
/// <see cref="Advance"/> from its update thread, so scene publication itself
/// remains deterministic and cannot race rendering.
/// </summary>
internal sealed class SampleSceneTransitionCoordinator : IDisposable
{
    internal static readonly TimeSpan NoProgressWarning =
        TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NoProgressFailure =
        TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan AbsoluteFailure =
        TimeSpan.FromMinutes(5);
    private readonly Func<
        SampleSceneKind,
        IContentLoadProgressSink,
        CancellationToken,
        Task> _prepare;
    private readonly Action<SampleSceneKind> _commit;
    private readonly Func<long> _getTimestamp;
    private readonly Func<long, long, TimeSpan> _getElapsedTime;
    private readonly object _progressGate = new();
    private readonly Dictionary<string, ContentLoadProgressEvent>
        _assetProgress =
        new(StringComparer.OrdinalIgnoreCase);
    private ActiveTransition? _active;
    private long _generation;
    private bool _disposed;

    public SampleSceneTransitionCoordinator(
        Func<
            SampleSceneKind,
            IContentLoadProgressSink,
            CancellationToken,
            Task> prepare,
        Action<SampleSceneKind> commit)
        : this(
            prepare,
            commit,
            Stopwatch.GetTimestamp,
            static (started, ended) =>
                Stopwatch.GetElapsedTime(started, ended))
    {
    }

    internal SampleSceneTransitionCoordinator(
        Func<
            SampleSceneKind,
            IContentLoadProgressSink,
            CancellationToken,
            Task> prepare,
        Action<SampleSceneKind> commit,
        Func<long> getTimestamp,
        Func<long, long, TimeSpan> getElapsedTime)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        _getTimestamp = getTimestamp ??
            throw new ArgumentNullException(nameof(getTimestamp));
        _getElapsedTime = getElapsedTime ??
            throw new ArgumentNullException(nameof(getElapsedTime));
    }

    public SampleSceneTransitionSnapshot Snapshot { get; private set; } =
        SampleSceneTransitionSnapshot.Idle;

    public bool IsActive => Snapshot.Active;

    public event Action<SampleSceneTransitionSnapshot>? Changed;

    public long Request(
        SampleSceneKind target,
        bool waitForLoadingFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelActive(publish: false);
        long generation = NextGeneration();
        var cancellation = new CancellationTokenSource();
        var transition = new ActiveTransition(
            generation,
            target,
            _getTimestamp(),
            cancellation);
        _active = transition;
        lock (_progressGate)
            _assetProgress.Clear();

        if (waitForLoadingFrame)
        {
            Publish(
                transition,
                SampleSceneTransitionPhase.WaitingForLoadingFrame,
                0.02,
                "loading scene presented before releasing the previous working set");
        }
        else
        {
            StartPreparation(transition);
        }

        return generation;
    }

    public void ReleaseLoadingFrame(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActiveTransition? transition = _active;
        if (transition == null ||
            transition.Generation != generation ||
            Snapshot.Phase !=
                SampleSceneTransitionPhase.WaitingForLoadingFrame)
        {
            return;
        }

        StartPreparation(transition);
    }

    public void Advance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActiveTransition? transition = _active;
        if (transition == null)
            return;
        if (transition.Preparation is not { } preparation)
        {
            if (Snapshot.Phase ==
                SampleSceneTransitionPhase.WaitingForLoadingFrame)
            {
                AdvanceWatchdog(transition);
            }
            return;
        }
        if (!preparation.IsCompleted)
        {
            AdvanceWatchdog(transition);
            return;
        }

        if (transition.Generation != Snapshot.Generation)
            return;

        if (preparation.IsCanceled ||
            transition.Cancellation.IsCancellationRequested)
        {
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Cancelled,
                "cancelled before publication",
                null);
            return;
        }

        if (preparation.Exception is { } aggregate)
        {
            Exception failure = aggregate.InnerExceptions.Count == 1
                ? aggregate.InnerExceptions[0]
                : aggregate;
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Failed,
                failure.Message,
                failure);
            return;
        }

        Publish(
            transition,
            SampleSceneTransitionPhase.Committing,
            0.96,
            "publishing prepared scene");
        try
        {
            preparation.GetAwaiter().GetResult();
            _commit(transition.Target);
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Completed,
                "first target frame pending",
                null);
        }
        catch (OperationCanceledException) when (
            transition.Cancellation.IsCancellationRequested)
        {
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Cancelled,
                "cancelled during commit",
                null);
        }
        catch (Exception failure)
        {
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Failed,
                failure.Message,
                failure);
        }
    }

    /// <summary>
    /// Records host-side upload-pump activity for the current request without
    /// pretending that bytes or stages advanced.
    /// </summary>
    public void ObserveHostActivity(long generation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ActiveTransition? transition = _active;
        if (transition == null || transition.Generation != generation)
            return;
        transition.LastActivityTimestamp = _getTimestamp();
    }

    public bool Cancel()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CancelActive(publish: true);
    }

    public bool Fail(
        long generation,
        Exception failure,
        string detail)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ActiveTransition? transition = _active;
        if (transition == null || transition.Generation != generation)
            return false;

        transition.Cancellation.Cancel();
        PublishTerminal(
            transition,
            SampleSceneTransitionPhase.Failed,
            detail,
            failure);
        return true;
    }

    public void ResetToIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsActive)
            throw new InvalidOperationException(
                "An active transition must be cancelled before resetting it.");
        Snapshot = SampleSceneTransitionSnapshot.Idle;
        Changed?.Invoke(Snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelActive(publish: false);
    }

    private void StartPreparation(ActiveTransition transition)
    {
        if (!ReferenceEquals(_active, transition) ||
            transition.Cancellation.IsCancellationRequested)
        {
            return;
        }

        Publish(
            transition,
            SampleSceneTransitionPhase.Decoding,
            0.05,
            "resolving and decoding cooked assets");
        var sink = new ProgressSink(this, transition.Generation);
        try
        {
            transition.Preparation = _prepare(
                transition.Target,
                sink,
                transition.Cancellation.Token) ??
                throw new InvalidOperationException(
                    "The scene preparation callback returned a null task.");
        }
        catch (Exception failure)
        {
            transition.Preparation = Task.FromException(failure);
        }
    }

    private void Report(
        long generation,
        ContentLoadProgressEvent progress)
    {
        ActiveTransition? transition = _active;
        if (transition == null || transition.Generation != generation)
            return;

        double fraction;
        long now = _getTimestamp();
        lock (_progressGate)
        {
            transition.LastActivityTimestamp = now;
            if (progress.IsHeartbeat)
            {
                fraction = CalculateProgress(_assetProgress.Values);
            }
            else
            {
            bool advanced = !_assetProgress.TryGetValue(
                    progress.Path,
                    out ContentLoadProgressEvent? previous) ||
                previous.Stage != progress.Stage ||
                progress.CompletedBytes > previous.CompletedBytes;
            _assetProgress[progress.Path] = progress;
            fraction = CalculateProgress(_assetProgress.Values);
            if (advanced)
            {
                transition.LastProgressTimestamp = now;
                transition.WatchdogWarned = false;
            }
            }
        }

        SampleSceneTransitionPhase phase = progress.IsHeartbeat
            ? Snapshot.Phase is SampleSceneTransitionPhase.Decoding or
                SampleSceneTransitionPhase.WaitingForUpload
                ? Snapshot.Phase
                : SampleSceneTransitionPhase.Decoding
            : progress.Stage is
            ContentLoadStage.WaitingForUpload or
            ContentLoadStage.Uploading or
            ContentLoadStage.AwaitingGpu
                ? SampleSceneTransitionPhase.WaitingForUpload
                : SampleSceneTransitionPhase.Decoding;
        Publish(
            transition,
            phase,
            fraction,
            progress.Message ?? progress.Path);
    }

    private static double CalculateProgress(
        IEnumerable<ContentLoadProgressEvent> events)
    {
        ContentLoadProgressEvent[] snapshot = events.ToArray();
        if (snapshot.Length == 0)
            return 0.05;

        double total = 0;
        foreach (ContentLoadProgressEvent progress in snapshot)
        {
            double byteFraction = progress.TotalBytes > 0
                ? Math.Clamp(
                    progress.CompletedBytes /
                    (double)progress.TotalBytes,
                    0.0,
                    1.0)
                : 0.0;
            total += progress.Stage switch
            {
                ContentLoadStage.Queued => 0.05,
                ContentLoadStage.Started => 0.15,
                ContentLoadStage.Preparing =>
                    0.15 + byteFraction * 0.40,
                ContentLoadStage.WaitingForUpload => 0.55,
                ContentLoadStage.Uploading =>
                    0.55 + byteFraction * 0.33,
                ContentLoadStage.AwaitingGpu =>
                    0.88 + byteFraction * 0.04,
                ContentLoadStage.Ready => 0.92,
                ContentLoadStage.Cancelled => 0.92,
                ContentLoadStage.Failed => 0.92,
                _ => 0.05
            };
        }

        return Math.Clamp(total / snapshot.Length, 0.05, 0.92);
    }

    private void AdvanceWatchdog(ActiveTransition transition)
    {
        long now = _getTimestamp();
        TimeSpan absoluteElapsed = _getElapsedTime(
            transition.StartedTimestamp,
            now);
        if (absoluteElapsed >= AbsoluteFailure)
        {
            var failure = new TimeoutException(
                $"Scene transition exceeded the absolute " +
                $"{AbsoluteFailure.TotalMinutes:F0}-minute time limit.");
            transition.Cancellation.Cancel();
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Failed,
                failure.Message,
                failure);
            return;
        }

        TimeSpan inactive = _getElapsedTime(
            transition.LastActivityTimestamp,
            now);
        if (inactive >= NoProgressFailure)
        {
            var failure = new TimeoutException(
                $"Scene transition had no observable activity for " +
                $"{inactive.TotalSeconds:F1}s.");
            transition.Cancellation.Cancel();
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Failed,
                failure.Message,
                failure);
            return;
        }
        TimeSpan stalled = _getElapsedTime(
            transition.LastProgressTimestamp,
            now);
        if (stalled < NoProgressWarning ||
            transition.WatchdogWarned)
        {
            return;
        }

        transition.WatchdogWarned = true;
        Publish(
            transition,
            Snapshot.Phase,
            Snapshot.Progress,
            $"{Snapshot.Detail} (no progress for " +
            $"{stalled.TotalSeconds:F1}s)");
    }

    private bool CancelActive(bool publish)
    {
        ActiveTransition? transition = _active;
        if (transition == null)
            return false;

        transition.Cancellation.Cancel();
        if (publish)
        {
            PublishTerminal(
                transition,
                SampleSceneTransitionPhase.Cancelled,
                "cancelled by a newer request or the user",
                null);
        }
        else
        {
            RetireTransition(transition);
            _active = null;
        }
        return true;
    }

    private static void RetireTransition(
        ActiveTransition transition)
    {
        Task? preparation = transition.Preparation;
        if (preparation == null || preparation.IsCompleted)
        {
            _ = preparation?.Exception;
            transition.Cancellation.Dispose();
            return;
        }

        _ = preparation.ContinueWith(
            static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            transition.Cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishTerminal(
        ActiveTransition transition,
        SampleSceneTransitionPhase phase,
        string detail,
        Exception? failure)
    {
        Publish(
            transition,
            phase,
            phase == SampleSceneTransitionPhase.Completed ? 1.0 :
                Snapshot.Progress,
            detail,
            failure);
        if (ReferenceEquals(_active, transition))
            _active = null;
        RetireTransition(transition);
    }

    private void Publish(
        ActiveTransition transition,
        SampleSceneTransitionPhase phase,
        double progress,
        string detail,
        Exception? failure = null)
    {
        if (_active != null &&
            !ReferenceEquals(_active, transition))
        {
            return;
        }

        long elapsedMicroseconds = checked((long)Math.Round(
            _getElapsedTime(
                    transition.StartedTimestamp,
                    _getTimestamp())
                .TotalMicroseconds));
        Snapshot = new SampleSceneTransitionSnapshot(
            transition.Generation,
            transition.Target,
            phase,
            Math.Clamp(progress, 0, 1),
            elapsedMicroseconds,
            detail,
            failure);
        Changed?.Invoke(Snapshot);
    }

    private long NextGeneration()
    {
        _generation = _generation == long.MaxValue
            ? 1
            : _generation + 1;
        return _generation;
    }

    private sealed class ActiveTransition
    {
        public ActiveTransition(
            long generation,
            SampleSceneKind target,
            long startedTimestamp,
            CancellationTokenSource cancellation)
        {
            Generation = generation;
            Target = target;
            StartedTimestamp = startedTimestamp;
            LastProgressTimestamp = startedTimestamp;
            LastActivityTimestamp = startedTimestamp;
            Cancellation = cancellation;
        }

        public long Generation { get; }
        public SampleSceneKind Target { get; }
        public long StartedTimestamp { get; }
        public long LastProgressTimestamp { get; set; }
        public long LastActivityTimestamp { get; set; }
        public bool WatchdogWarned { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public Task? Preparation { get; set; }
    }

    private sealed class ProgressSink : IContentLoadProgressSink
    {
        private readonly SampleSceneTransitionCoordinator _owner;
        private readonly long _generation;

        public ProgressSink(
            SampleSceneTransitionCoordinator owner,
            long generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public void Report(ContentLoadProgressEvent progress) =>
            _owner.Report(_generation, progress);
    }
}

internal readonly record struct SampleSceneTransitionMemoryDecision(
    bool KeepCurrentScene,
    ulong EffectiveBudgetBytes,
    ulong RequiredBytes,
    ulong AdmissionCeilingBytes,
    string Reason);

internal readonly record struct SampleSceneTransitionLatencyEvaluation(
    long ElapsedMicroseconds,
    long TargetMicroseconds,
    bool MeetsTarget,
    string CacheClass);

internal static class SampleSceneTransitionLatencyPolicy
{
    internal const long WarmOrProceduralTargetMicroseconds = 1_000_000;
    internal const long ColdSponzaTargetMicroseconds = 5_000_000;
    internal const long ColdBistroTargetMicroseconds = 5_000_000;
    internal const long ColdBistroFullResidencyTargetMicroseconds =
        15_000_000;
    internal const long FeedbackTargetMicroseconds = 100_000;
    internal const long HitchTargetMicroseconds = 33_000;

    public static SampleSceneTransitionLatencyEvaluation Evaluate(
        SampleSceneKind target,
        bool residentCacheHit,
        long elapsedMicroseconds)
    {
        if (elapsedMicroseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMicroseconds));

        bool importedModelScene = target is
            SampleSceneKind.SponzaPlaza or SampleSceneKind.Bistro;
        long targetMicroseconds = residentCacheHit || !importedModelScene
            ? WarmOrProceduralTargetMicroseconds
            : target switch
            {
                SampleSceneKind.SponzaPlaza =>
                    ColdSponzaTargetMicroseconds,
                SampleSceneKind.Bistro =>
                    ColdBistroTargetMicroseconds,
                _ => WarmOrProceduralTargetMicroseconds
            };
        return new SampleSceneTransitionLatencyEvaluation(
            elapsedMicroseconds,
            targetMicroseconds,
            elapsedMicroseconds <= targetMicroseconds,
            residentCacheHit
                ? "resident"
                : importedModelScene
                    ? "cold"
                    : "procedural");
    }
}

internal static class SampleSceneTransitionMemoryPolicy
{
    internal const double AdmissionFraction = 0.80;
    internal const double TransientReserveFraction = 0.05;
    internal const ulong MinimumTransientReserveBytes =
        256UL * 1024UL * 1024UL;

    public static SampleSceneTransitionMemoryDecision Evaluate(
        ulong currentUsageBytes,
        ulong effectiveBudgetBytes,
        ulong targetIncrementalBytes)
    {
        if (effectiveBudgetBytes == 0)
        {
            return new SampleSceneTransitionMemoryDecision(
                false,
                0,
                ulong.MaxValue,
                0,
                "effective GPU budget is unavailable");
        }

        ulong reserve = Math.Max(
            MinimumTransientReserveBytes,
            (ulong)Math.Floor(
                effectiveBudgetBytes * TransientReserveFraction));
        ulong ceiling = (ulong)Math.Floor(
            effectiveBudgetBytes * AdmissionFraction);
        ulong required = SaturatingAdd(
            SaturatingAdd(currentUsageBytes, targetIncrementalBytes),
            reserve);
        bool admitted = required <= ceiling;
        return new SampleSceneTransitionMemoryDecision(
            admitted,
            effectiveBudgetBytes,
            required,
            ceiling,
            admitted
                ? "current and target working sets fit the overlap ceiling"
                : "target requires the lightweight loading-scene handoff");
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right
            ? ulong.MaxValue
            : left + right;
}
