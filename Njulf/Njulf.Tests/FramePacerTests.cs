using Njulf.Core;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FramePacerTests
{
    [Test]
    public void Wait_FirstFrameDoesNotDelayAndSecondFrameUsesAbsoluteDeadline()
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();

        Assert.That(pacer.Wait(60.0), Is.Zero);
        clock.Timestamp = 5_000L;

        long waitedMicroseconds = pacer.Wait(60.0);

        Assert.Multiple(() =>
        {
            Assert.That(clock.Timestamp, Is.InRange(16_667L, 16_766L));
            Assert.That(waitedMicroseconds, Is.InRange(11_667L, 11_766L));
            Assert.That(clock.SleepCallCount, Is.GreaterThan(0));
            Assert.That(clock.YieldCallCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Wait_LongStallRebasesInsteadOfEmittingCatchUpFrames()
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();
        Assert.That(pacer.Wait(60.0), Is.Zero);

        clock.Timestamp = 100_000L;
        Assert.That(pacer.Wait(60.0), Is.Zero);

        clock.Timestamp = 101_000L;
        long waitedMicroseconds = pacer.Wait(60.0);

        Assert.That(clock.Timestamp, Is.InRange(116_667L, 116_766L));
        Assert.That(waitedMicroseconds, Is.InRange(15_667L, 15_766L));
    }

    [Test]
    public void Wait_ChangingLimitStartsANewScheduleWithoutImmediateDelay()
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();
        Assert.That(pacer.Wait(60.0), Is.Zero);

        clock.Timestamp = 5_000L;
        Assert.That(pacer.Wait(120.0), Is.Zero);

        clock.Timestamp = 6_000L;
        long waitedMicroseconds = pacer.Wait(120.0);

        Assert.That(clock.Timestamp, Is.InRange(13_333L, 13_432L));
        Assert.That(waitedMicroseconds, Is.InRange(7_333L, 7_432L));
    }

    [Test]
    public void Wait_ZeroDisablesAndResetsPacing()
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();
        Assert.That(pacer.Wait(60.0), Is.Zero);
        clock.Timestamp = 1_000L;

        Assert.That(pacer.Wait(0.0), Is.Zero);
        Assert.That(pacer.Wait(60.0), Is.Zero);
        Assert.That(clock.SleepCallCount, Is.Zero);
        Assert.That(clock.YieldCallCount, Is.Zero);
    }

    [TestCase(-1.0)]
    [TestCase(0.5)]
    [TestCase(1000.1)]
    public void Wait_RejectsUnsupportedLimits(double maximumFramesPerSecond)
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => pacer.Wait(maximumFramesPerSecond));
    }

    [Test]
    public void Wait_RejectsNonFiniteLimit()
    {
        var clock = new FakeClock();
        FramePacer pacer = clock.CreatePacer();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => pacer.Wait(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => pacer.Wait(double.PositiveInfinity));
    }

    private sealed class FakeClock
    {
        public long Timestamp { get; set; }
        public int SleepCallCount { get; private set; }
        public int YieldCallCount { get; private set; }

        public FramePacer CreatePacer() => new(
            () => Timestamp,
            frequency: 1_000_000L,
            Sleep,
            Yield);

        private void Sleep(int milliseconds)
        {
            SleepCallCount++;
            Timestamp += milliseconds * 1_000L;
        }

        private void Yield()
        {
            YieldCallCount++;
            Timestamp += 100L;
        }
    }
}
