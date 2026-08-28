using System.Collections.Concurrent;
using System.Diagnostics;
using Njulf.Assets;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Queues renderer-owned publication callbacks from background content work
/// and executes them on the render host thread. Cancellation is generation
/// safe: a cancelled item is never invoked, while a callback already running
/// is allowed to finish and reports its real outcome.
/// </summary>
public sealed class RenderThreadContentUploadDispatcher :
    IContentUploadDispatcher,
    IContentUploadPump,
    IDisposable
{
    private readonly ConcurrentQueue<IUploadWorkItem> _pending = new();
    private readonly object _lifecycleGate = new();
    private int _pendingCount;
    private int _disposed;

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public Task<T> DispatchAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var item = new UploadWorkItem<T>(callback, cancellationToken);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _pending.Enqueue(item);
            Interlocked.Increment(ref _pendingCount);
        }
        return item.Task;
    }

    public Task<T> DispatchAsync<T>(
        IContentUploadWork<T> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested)
        {
            work.RequestCancellation();
        }

        var item = new CooperativeUploadWorkItem<T>(
            work,
            cancellationToken);
        Enqueue(item);
        return item.Task;
    }

    public ContentUploadPumpResult ProcessFrame(
        TimeSpan cpuBudget,
        int maximumCallbacks = 1,
        long maximumSubmissionBytes = 8L * 1024L * 1024L)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (cpuBudget < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cpuBudget));
        if (maximumCallbacks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCallbacks));
        if (maximumSubmissionBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSubmissionBytes));
        }

        long started = Stopwatch.GetTimestamp();
        int processed = 0;
        int invoked = 0;
        int cancelled = 0;
        int failed = 0;
        while (invoked < maximumCallbacks)
        {
            IUploadWorkItem? item;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!_pending.TryDequeue(out item))
                    break;
                Interlocked.Decrement(ref _pendingCount);
            }

            bool invokeCallback = !item.IsCancellationRequested ||
                                  item.RequiresCancellationDrain;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            TimeSpan remaining = cpuBudget <= TimeSpan.Zero
                ? TimeSpan.Zero
                : elapsed >= cpuBudget
                    ? TimeSpan.Zero
                    : cpuBudget - elapsed;
            UploadWorkItemOutcome outcome = item.Execute(
                new ContentUploadSliceBudget(
                    remaining,
                    maximumSubmissionBytes));
            processed++;
            if (invokeCallback)
                invoked++;
            if (outcome == UploadWorkItemOutcome.Yielded)
            {
                Enqueue(item);
            }
            else if (outcome == UploadWorkItemOutcome.Cancelled)
                cancelled++;
            else if (outcome == UploadWorkItemOutcome.Failed)
                failed++;

            if (cpuBudget > TimeSpan.Zero &&
                Stopwatch.GetElapsedTime(started) >= cpuBudget)
            {
                break;
            }
        }

        long elapsedMicroseconds = checked((long)Math.Round(
            Stopwatch.GetElapsedTime(started).TotalMicroseconds));
        return new ContentUploadPumpResult(
            processed,
            cancelled,
            failed,
            elapsedMicroseconds,
            PendingCount);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
                return;
            Volatile.Write(ref _disposed, 1);

            while (_pending.TryDequeue(out IUploadWorkItem? item))
            {
                Interlocked.Decrement(ref _pendingCount);
                item.CancelForShutdown();
            }
        }
    }

    private interface IUploadWorkItem
    {
        bool IsCancellationRequested { get; }
        bool RequiresCancellationDrain { get; }
        UploadWorkItemOutcome Execute(
            in ContentUploadSliceBudget budget);
        void CancelForShutdown();
    }

    private enum UploadWorkItemOutcome
    {
        Yielded,
        Completed,
        Cancelled,
        Failed
    }

    private sealed class UploadWorkItem<T> : IUploadWorkItem
    {
        private readonly Func<T> _callback;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public UploadWorkItem(
            Func<T> callback,
            CancellationToken cancellationToken)
        {
            _callback = callback;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public bool IsCancellationRequested =>
            _cancellationToken.IsCancellationRequested;

        public bool RequiresCancellationDrain => false;

        public UploadWorkItemOutcome Execute(
            in ContentUploadSliceBudget budget)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return UploadWorkItemOutcome.Cancelled;
            }

            try
            {
                T result = _callback();
                _completion.TrySetResult(result);
                return UploadWorkItemOutcome.Completed;
            }
            catch (OperationCanceledException) when (
                _cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return UploadWorkItemOutcome.Cancelled;
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                return UploadWorkItemOutcome.Failed;
            }
        }

        public void CancelForShutdown() =>
            _completion.TrySetException(new ObjectDisposedException(
                nameof(RenderThreadContentUploadDispatcher)));
    }

    private sealed class CooperativeUploadWorkItem<T> : IUploadWorkItem
    {
        private readonly IContentUploadWork<T> _work;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _cancellationForwarded;

        public CooperativeUploadWorkItem(
            IContentUploadWork<T> work,
            CancellationToken cancellationToken)
        {
            _work = work;
            _cancellationToken = cancellationToken;
        }

        public Task<T> Task => _completion.Task;

        public bool IsCancellationRequested =>
            _cancellationToken.IsCancellationRequested;

        public bool RequiresCancellationDrain => true;

        public UploadWorkItemOutcome Execute(
            in ContentUploadSliceBudget budget)
        {
            try
            {
                ForwardCancellationIfNeeded();
                ContentUploadStepResult step =
                    _work.ExecuteStep(budget);
                switch (step.Status)
                {
                    case ContentUploadStepStatus.Yielded:
                        return UploadWorkItemOutcome.Yielded;
                    case ContentUploadStepStatus.Cancelled:
                        _completion.TrySetCanceled(
                            ResolveCancellationToken());
                        return UploadWorkItemOutcome.Cancelled;
                    case ContentUploadStepStatus.Completed:
                        _completion.TrySetResult(_work.GetResult());
                        return UploadWorkItemOutcome.Completed;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown cooperative upload step status " +
                            $"'{step.Status}'.");
                }
            }
            catch (OperationCanceledException) when (
                _cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return UploadWorkItemOutcome.Cancelled;
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                return UploadWorkItemOutcome.Failed;
            }
        }

        public void CancelForShutdown()
        {
            try
            {
                if (!_cancellationForwarded)
                {
                    _work.RequestCancellation();
                    _cancellationForwarded = true;
                }
            }
            finally
            {
                _completion.TrySetException(new ObjectDisposedException(
                    nameof(RenderThreadContentUploadDispatcher)));
            }
        }

        private void ForwardCancellationIfNeeded()
        {
            if (_cancellationForwarded ||
                !_cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _work.RequestCancellation();
            _cancellationForwarded = true;
        }

        private CancellationToken ResolveCancellationToken() =>
            _cancellationToken.CanBeCanceled
                ? _cancellationToken
                : new CancellationToken(canceled: true);
    }

    private void Enqueue(IUploadWorkItem item)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _pending.Enqueue(item);
            Interlocked.Increment(ref _pendingCount);
        }
    }
}
