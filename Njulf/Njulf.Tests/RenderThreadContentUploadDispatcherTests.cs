using Njulf.Assets;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderThreadContentUploadDispatcherTests
{
    [Test]
    public async Task ProcessFrame_ExecutesOnlyConfiguredCallbackCount()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        Task<int> first = dispatcher.DispatchAsync(
            () => 11,
            CancellationToken.None);
        Task<int> second = dispatcher.DispatchAsync(
            () => 22,
            CancellationToken.None);

        ContentUploadPumpResult result = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4),
            maximumCallbacks: 1);

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(result.RemainingCount, Is.EqualTo(1));
        Assert.That(await first, Is.EqualTo(11));
        Assert.That(second.IsCompleted, Is.False);

        dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4),
            maximumCallbacks: 1);
        Assert.That(await second, Is.EqualTo(22));
    }

    [Test]
    public void ProcessFrame_DoesNotInvokeCancelledCallback()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        using var cancellation = new CancellationTokenSource();
        bool invoked = false;
        Task<int> task = dispatcher.DispatchAsync(
            () =>
            {
                invoked = true;
                return 1;
            },
            cancellation.Token);
        cancellation.Cancel();

        ContentUploadPumpResult result = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(invoked, Is.False);
        Assert.That(result.CancelledCount, Is.EqualTo(1));
        Assert.That(task.IsCanceled, Is.True);
    }

    [Test]
    public void ProcessFrame_ReportsCallbackFailureWithoutThrowingFromPump()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        Task<int> task = dispatcher.DispatchAsync<int>(
            () => throw new InvalidOperationException("upload failed"),
            CancellationToken.None);

        ContentUploadPumpResult result = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(result.FailedCount, Is.EqualTo(1));
        Assert.That(task.IsFaulted, Is.True);
        Assert.That(task.Exception!.InnerException,
            Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void Dispose_FaultsQueuedWorkAndRejectsNewDispatch()
    {
        var dispatcher = new RenderThreadContentUploadDispatcher();
        Task<int> queued = dispatcher.DispatchAsync(
            () => 1,
            CancellationToken.None);

        dispatcher.Dispose();

        Assert.That(queued.IsFaulted, Is.True);
        Assert.That(
            () => dispatcher.DispatchAsync(
                () => 2,
                CancellationToken.None),
            Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public async Task ProcessFrame_DrainsCancelledItemsAheadOfLiveUpload()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        using var cancellation = new CancellationTokenSource();
        Task<int>[] cancelled = Enumerable.Range(0, 3)
            .Select(_ => dispatcher.DispatchAsync(
                () => -1,
                cancellation.Token))
            .ToArray();
        Task<int> live = dispatcher.DispatchAsync(
            () => 42,
            CancellationToken.None);
        cancellation.Cancel();

        ContentUploadPumpResult result = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(100),
            maximumCallbacks: 1);

        Assert.That(result.ProcessedCount, Is.EqualTo(4));
        Assert.That(result.CancelledCount, Is.EqualTo(3));
        Assert.That(await live, Is.EqualTo(42));
        Assert.That(cancelled, Has.All.Matches<Task<int>>(
            task => task.IsCanceled));
    }

    [Test]
    public async Task ProcessFrame_RequeuesCooperativeWorkUntilTerminalStep()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        var work = new ThreeStepUploadWork();
        Task<int> task = dispatcher.DispatchAsync(
            work,
            CancellationToken.None);

        ContentUploadPumpResult first = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));
        ContentUploadPumpResult second = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(task.IsCompleted, Is.False);
        Assert.That(first.RemainingCount, Is.EqualTo(1));
        Assert.That(second.RemainingCount, Is.EqualTo(1));

        ContentUploadPumpResult third = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(await task, Is.EqualTo(42));
        Assert.That(third.RemainingCount, Is.Zero);
        Assert.That(work.Budgets, Has.Count.EqualTo(3));
        Assert.That(work.Budgets,
            Has.All.Matches<ContentUploadSliceBudget>(budget =>
                budget.MaximumSubmissionBytes ==
                    8L * 1024L * 1024L));
    }

    [Test]
    public void ProcessFrame_CooperativeCancellationDrainsBeforeTaskCancels()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        using var cancellation = new CancellationTokenSource();
        var work = new DrainingCancellationUploadWork();
        Task<int> task = dispatcher.DispatchAsync(
            work,
            cancellation.Token);
        cancellation.Cancel();

        dispatcher.ProcessFrame(TimeSpan.FromMilliseconds(4));

        Assert.That(task.IsCompleted, Is.False);
        Assert.That(work.CancellationRequested, Is.True);

        ContentUploadPumpResult drained = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(task.IsCanceled, Is.True);
        Assert.That(drained.CancelledCount, Is.EqualTo(1));
    }

    [Test]
    public void DispatchAsync_AlreadyCancelledCooperativeWorkStillDrains()
    {
        using var dispatcher =
            new RenderThreadContentUploadDispatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var work = new DrainingCancellationUploadWork();

        Task<int> task = dispatcher.DispatchAsync(
            work,
            cancellation.Token);

        Assert.That(task.IsCompleted, Is.False);
        Assert.That(dispatcher.PendingCount, Is.EqualTo(1));
        dispatcher.ProcessFrame(TimeSpan.FromMilliseconds(4));
        ContentUploadPumpResult drained = dispatcher.ProcessFrame(
            TimeSpan.FromMilliseconds(4));

        Assert.That(work.CancellationRequested, Is.True);
        Assert.That(task.IsCanceled, Is.True);
        Assert.That(drained.CancelledCount, Is.EqualTo(1));
    }

    private sealed class ThreeStepUploadWork :
        IContentUploadWork<int>
    {
        private int _step;

        public List<ContentUploadSliceBudget> Budgets { get; } = [];

        public ContentUploadStepResult ExecuteStep(
            in ContentUploadSliceBudget budget)
        {
            Budgets.Add(budget);
            _step++;
            return _step == 3
                ? ContentUploadStepResult.Complete()
                : ContentUploadStepResult.Yield();
        }

        public int GetResult() => _step == 3
            ? 42
            : throw new InvalidOperationException();

        public void RequestCancellation()
        {
        }
    }

    private sealed class DrainingCancellationUploadWork :
        IContentUploadWork<int>
    {
        private int _drainStep;

        public bool CancellationRequested { get; private set; }

        public ContentUploadStepResult ExecuteStep(
            in ContentUploadSliceBudget budget)
        {
            if (!CancellationRequested)
                return ContentUploadStepResult.Yield();
            return ++_drainStep == 1
                ? ContentUploadStepResult.Yield()
                : ContentUploadStepResult.Cancelled();
        }

        public int GetResult() => throw new InvalidOperationException();

        public void RequestCancellation() =>
            CancellationRequested = true;
    }
}
