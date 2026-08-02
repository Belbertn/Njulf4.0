using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiPersistentProbeQueuesTests
{
    [Test]
    public void MoveAndRotate_MaintainIncrementalCountsAndStableRoundRobinOrder()
    {
        var queues = new SimpleDdgiPersistentProbeQueues(
            queueCount: 6,
            workClassCount: 3);
        queues.EnsureProbeCapacity(5);

        queues.MoveToQueue(0, 1);
        queues.MoveToQueue(1, 1);
        queues.MoveToQueue(2, 1);

        Assert.Multiple(() =>
        {
            Assert.That(queues.GetQueueCount(1), Is.EqualTo(3));
            Assert.That(queues.GetWorkClassCount(1), Is.EqualTo(3));
            Assert.That(queues.TryRotateNext(1, out int first), Is.True);
            Assert.That(first, Is.EqualTo(0));
            Assert.That(queues.TryRotateNext(1, out int second), Is.True);
            Assert.That(second, Is.EqualTo(1));
            Assert.That(queues.TryRotateNext(1, out int third), Is.True);
            Assert.That(third, Is.EqualTo(2));
            Assert.That(queues.TryRotateNext(1, out int wrapped), Is.True);
            Assert.That(wrapped, Is.EqualTo(0));
        });

        queues.MoveToQueue(1, 4);
        queues.MoveToQueue(2, SimpleDdgiPersistentProbeQueues.NoQueue);

        Assert.Multiple(() =>
        {
            Assert.That(queues.GetQueueCount(1), Is.EqualTo(1));
            Assert.That(queues.GetQueueCount(4), Is.EqualTo(1));
            Assert.That(queues.GetWorkClassCount(1), Is.EqualTo(2));
            Assert.That(queues.GetProbeQueue(2),
                Is.EqualTo(SimpleDdgiPersistentProbeQueues.NoQueue));
        });
    }

    [Test]
    public void WakeHeap_UpdatesAndRemovesOneDeadlinePerProbe()
    {
        var heap = new SimpleDdgiSchedulerWakeHeap();
        heap.EnsureProbeCapacity(5);
        heap.Schedule(3, 20);
        heap.Schedule(1, 10);
        heap.Schedule(2, 10);
        heap.Schedule(3, 5);
        heap.Remove(2);

        Assert.Multiple(() =>
        {
            Assert.That(heap.TryPopDue(4, out _), Is.False);
            Assert.That(heap.TryPopDue(5, out int first), Is.True);
            Assert.That(first, Is.EqualTo(3));
            Assert.That(heap.TryPopDue(10, out int second), Is.True);
            Assert.That(second, Is.EqualTo(1));
            Assert.That(heap.TryPopDue(ulong.MaxValue, out _), Is.False);
            Assert.That(heap.Count, Is.Zero);
        });
    }

    [Test]
    public void ProbeAge_IsDerivedFromLastUpdatedFrameAndHandlesFrameWrap()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.CalculateProbeAge(100u, 145u),
                Is.EqualTo(45u));
            Assert.That(
                SimpleDdgiVolumeManager.CalculateProbeAge(uint.MaxValue - 2u, 3u),
                Is.EqualTo(6u));
        });
    }

    [Test]
    public void IncrementalAgeHistogram_AgesEntriesByMovingOnlyItsOrigin()
    {
        var histogram = new SimpleDdgiIncrementalAgeHistogram(maximumExactAge: 4);
        histogram.Clear(frameSerial: 10);
        histogram.Add(age: 0, frameSerial: 10);
        histogram.Add(age: 2, frameSerial: 10);

        Assert.Multiple(() =>
        {
            Assert.That(histogram.SelectRank(1, frameSerial: 11), Is.EqualTo(1));
            Assert.That(histogram.SelectRank(2, frameSerial: 11), Is.EqualTo(3));
            Assert.That(histogram.CountAbove(2, frameSerial: 11), Is.EqualTo(1));
        });

        histogram.Remove(age: 3, frameSerial: 11);
        histogram.Add(age: 4, frameSerial: 11);

        Assert.Multiple(() =>
        {
            Assert.That(histogram.Count, Is.EqualTo(2));
            Assert.That(histogram.CountAbove(4, frameSerial: 12), Is.EqualTo(1));
            Assert.That(histogram.SelectRank(2, frameSerial: 12), Is.EqualTo(5));
        });
    }
}
