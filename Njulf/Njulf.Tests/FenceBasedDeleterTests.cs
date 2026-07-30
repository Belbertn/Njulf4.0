using Njulf.Rendering.Memory;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class FenceBasedDeleterTests
{
    [Test]
    public void ZeroFence_ExecutesImmediatelyAndPropagatesFailure()
    {
        var queue = new DurableFenceDeletionQueue();
        int calls = 0;

        queue.QueueDeletion(default, () => calls++);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => queue.QueueDeletion(
                default,
                () => throw new InvalidOperationException(
                    "injected immediate failure")))!;

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(failure.Message, Does.Contain("immediate failure"));
            Assert.That(queue.PendingFenceCount, Is.Zero);
            Assert.That(queue.PendingActionCount, Is.Zero);
        });
    }

    [Test]
    public void Dispose_DrainsReentrantSameAndNewFenceWorkExactlyOnce()
    {
        var queue = new DurableFenceDeletionQueue();
        var firstFence = new Fence(1);
        var secondFence = new Fence(2);
        int rootCalls = 0;
        int sameFenceCalls = 0;
        int newFenceCalls = 0;
        int immediateCalls = 0;

        queue.QueueDeletion(firstFence, () =>
        {
            rootCalls++;
            queue.QueueDeletion(firstFence, () => sameFenceCalls++);
            queue.QueueDeletion(secondFence, () => newFenceCalls++);
            queue.QueueDeletion(default, () => immediateCalls++);
            queue.Cleanup();
            queue.Dispose();
        });

        queue.Dispose();
        queue.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(rootCalls, Is.EqualTo(1));
            Assert.That(sameFenceCalls, Is.EqualTo(1));
            Assert.That(newFenceCalls, Is.EqualTo(1));
            Assert.That(immediateCalls, Is.EqualTo(1));
            Assert.That(queue.PendingActionCount, Is.Zero);
            Assert.That(
                () => queue.QueueDeletion(firstFence, () => { }),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void ConcurrentEnqueue_IsRejectedOnceDisposalStarts()
    {
        var queue = new DurableFenceDeletionQueue();
        var fence = new Fence(3);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        queue.QueueDeletion(fence, () =>
        {
            callbackEntered.Set();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("test callback was not released");
        });

        Task dispose = Task.Run(queue.Dispose);
        Assert.That(
            callbackEntered.Wait(TimeSpan.FromSeconds(5)),
            Is.True);

        Task<Exception?> enqueue = Task.Run(() =>
        {
            try
            {
                queue.QueueDeletion(new Fence(4), () => { });
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        });

        releaseCallback.Set();
        Assert.That(
            Task.WaitAll([dispose, enqueue], TimeSpan.FromSeconds(5)),
            Is.True);
        Assert.That(enqueue.Result, Is.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void Cleanup_AttemptsEveryFenceAndRetainsOnlyFailedHeads()
    {
        var queue = new DurableFenceDeletionQueue();
        var firstFence = new Fence(5);
        var secondFence = new Fence(6);
        bool retrySucceeds = false;
        int firstSuccessCalls = 0;
        int firstFailureCalls = 0;
        int firstTailCalls = 0;
        int secondSuccessCalls = 0;
        int secondFailureCalls = 0;
        int secondTailCalls = 0;

        queue.QueueDeletion(firstFence, () => firstSuccessCalls++);
        queue.QueueDeletion(firstFence, () =>
        {
            firstFailureCalls++;
            if (!retrySucceeds)
                throw new InvalidOperationException("first fence failure");
        });
        queue.QueueDeletion(firstFence, () => firstTailCalls++);
        queue.QueueDeletion(secondFence, () => secondSuccessCalls++);
        queue.QueueDeletion(secondFence, () =>
        {
            secondFailureCalls++;
            if (!retrySucceeds)
                throw new InvalidOperationException("second fence failure");
        });
        queue.QueueDeletion(secondFence, () => secondTailCalls++);

        AggregateException failure = Assert.Throws<AggregateException>(
            queue.Cleanup)!;
        Assert.Multiple(() =>
        {
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(firstSuccessCalls, Is.EqualTo(1));
            Assert.That(firstFailureCalls, Is.EqualTo(1));
            Assert.That(firstTailCalls, Is.Zero);
            Assert.That(secondSuccessCalls, Is.EqualTo(1));
            Assert.That(secondFailureCalls, Is.EqualTo(1));
            Assert.That(secondTailCalls, Is.Zero);
            Assert.That(queue.PendingFenceCount, Is.EqualTo(2));
            Assert.That(queue.PendingActionCount, Is.EqualTo(4));
        });

        retrySucceeds = true;
        queue.Cleanup();

        Assert.Multiple(() =>
        {
            Assert.That(firstSuccessCalls, Is.EqualTo(1));
            Assert.That(firstFailureCalls, Is.EqualTo(2));
            Assert.That(firstTailCalls, Is.EqualTo(1));
            Assert.That(secondSuccessCalls, Is.EqualTo(1));
            Assert.That(secondFailureCalls, Is.EqualTo(2));
            Assert.That(secondTailCalls, Is.EqualTo(1));
            Assert.That(queue.PendingActionCount, Is.Zero);
        });
    }

    [Test]
    public void DeferredActions_RetryFromFirstFailureWithoutRepeatingSuccess()
    {
        int firstCalls = 0;
        int retryableCalls = 0;
        int lastCalls = 0;
        bool retrySucceeds = false;
        var actions = new Queue<Action>();
        actions.Enqueue(() => firstCalls++);
        actions.Enqueue(() =>
        {
            retryableCalls++;
            if (!retrySucceeds)
                throw new InvalidOperationException("injected deletion failure");
        });
        actions.Enqueue(() => lastCalls++);

        Assert.That(
            () => FenceBasedDeleter.ExecutePendingActions(actions),
            Throws.TypeOf<InvalidOperationException>());
        Assert.Multiple(() =>
        {
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(retryableCalls, Is.EqualTo(1));
            Assert.That(lastCalls, Is.Zero);
            Assert.That(actions, Has.Count.EqualTo(2));
        });

        retrySucceeds = true;
        FenceBasedDeleter.ExecutePendingActions(actions);

        Assert.Multiple(() =>
        {
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(retryableCalls, Is.EqualTo(2));
            Assert.That(lastCalls, Is.EqualTo(1));
            Assert.That(actions, Is.Empty);
        });
    }
}
