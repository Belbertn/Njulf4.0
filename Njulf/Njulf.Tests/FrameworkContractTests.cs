using Njulf.Core;
using Njulf.Graphics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FrameworkContractTests
{
    [Test]
    public void NativeRetirementRemainsRetryableWhenNoSubmissionIsOutstanding()
    {
        var queue = new GraphicsReleaseQueue();
        bool fail = true;
        int releases = 0;
        queue.EnqueueRetirement(() => { if (fail) throw new InvalidOperationException("retry"); releases++; }, false);
        Assert.Throws<InvalidOperationException>(() => queue.Complete(0));
        Assert.That(queue.Count, Is.EqualTo(1));
        fail = false;
        queue.Complete(0);
        Assert.That(releases, Is.EqualTo(1));
        Assert.That(queue.Count, Is.Zero);
    }
    private sealed class EmptyGame : Game { }

    [Test]
    public void ServicesFailClearlyBeforeRunAndAfterDisposal()
    {
        var game = new EmptyGame();
        Assert.That(game.Scene, Is.Not.Null);
        Assert.That(() => game.GraphicsDevice, Throws.InvalidOperationException);
        Assert.That(() => game.Content, Throws.InvalidOperationException);
        game.Dispose();
        Assert.That(() => game.Scene, Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => game.Run(), Throws.TypeOf<ObjectDisposedException>());
        Assert.DoesNotThrow(game.Dispose);
    }

    [Test]
    public void UpdateAndDrawClocksHaveIndependentElapsedTimesAndSharedOrigin()
    {
        var clock = new GameClock();
        clock.Start(TimeSpan.FromSeconds(10));
        Assert.That(clock.Update(TimeSpan.FromSeconds(11)), Is.EqualTo(new GameTime(TimeSpan.FromSeconds(1), TimeSpan.Zero)));
        Assert.That(clock.Draw(TimeSpan.FromSeconds(12)), Is.EqualTo(new GameTime(TimeSpan.FromSeconds(2), TimeSpan.Zero)));
        Assert.That(clock.Update(TimeSpan.FromSeconds(14)), Is.EqualTo(new GameTime(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3))));
        Assert.That(clock.Draw(TimeSpan.FromSeconds(15)), Is.EqualTo(new GameTime(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3))));
    }

    [Test]
    public void ReleasesWaitForSubmittedFrameCompletionAndFaultsWaitForIdle()
    {
        var queue = new GraphicsReleaseQueue();
        int released = 0;
        queue.Submitted(1);
        queue.Enqueue(() => released++, false);
        queue.Enqueue(() => released++, true);
        queue.Complete(1);
        Assert.That(released, Is.EqualTo(1));
        queue.Submitted(2);
        queue.Complete(1);
        Assert.That(released, Is.EqualTo(1));
        queue.Complete(2);
        Assert.That(released, Is.EqualTo(2));
        queue.Enqueue(() => released++, true);
        queue.Complete(ulong.MaxValue);
        Assert.That(released, Is.EqualTo(2));
        queue.Complete(ulong.MaxValue, deviceIdle: true);
        Assert.That(released, Is.EqualTo(3));
    }

    [Test]
    public void FailedRetirementRemainsAvailableForRetry()
    {
        var queue = new GraphicsReleaseQueue();
        bool fail = true;
        int released = 0;
        queue.Submitted(1);
        queue.Enqueue(() => { if (fail) throw new InvalidOperationException(); released++; }, false);
        Assert.Throws<InvalidOperationException>(() => queue.Complete(1));
        Assert.That(queue.Count, Is.EqualTo(1));
        fail = false;
        queue.Complete(1);
        Assert.That(released, Is.EqualTo(1));
        Assert.That(queue.Count, Is.Zero);
    }
}
