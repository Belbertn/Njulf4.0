using System;
using System.Linq;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class LongRunStabilityTrackerTests
{
    [Test]
    public void Tracker_RetainsOnlyBoundedChronologicalWindow()
    {
        var tracker = new LongRunStabilityTracker(capacity: 3);

        for (int frame = 0; frame < 5; frame++)
        {
            tracker.Add(CreateSample(
                frame,
                managedBytes: 1_000 + frame,
                trackedGpuBytes: 2_000UL + (ulong)frame));
        }

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Capacity, Is.EqualTo(3));
            Assert.That(tracker.Count, Is.EqualTo(3));
            Assert.That(tracker.TotalSampleCount, Is.EqualTo(5));
            Assert.That(
                tracker.Samples.Select(sample => sample.FrameIndex),
                Is.EqualTo(new[] { 2, 3, 4 }));
        });
    }

    [Test]
    public void Trend_RequiresPositiveSlopeAndGrowthBeyondNoiseTolerance()
    {
        var tracker = new LongRunStabilityTracker(capacity: 8);
        tracker.Add(CreateSample(10, 1_000, 10_000));
        tracker.Add(CreateSample(20, 1_100, 10_512));
        tracker.Add(CreateSample(30, 1_200, 11_024));

        LongRunMemoryTrend managed =
            tracker.EvaluateManagedMemoryTrend(warmupFrame: 10, noiseToleranceBytes: 50);
        LongRunMemoryTrend gpuWithinNoise =
            tracker.EvaluateTrackedGpuMemoryTrend(warmupFrame: 10, noiseToleranceBytes: 2_048);
        LongRunMemoryTrend gpuStrict =
            tracker.EvaluateTrackedGpuMemoryTrend(warmupFrame: 10, noiseToleranceBytes: 0);

        Assert.Multiple(() =>
        {
            Assert.That(managed.HasPositiveTrend, Is.True);
            Assert.That(managed.SlopeBytesPerFrame, Is.GreaterThan(0));
            Assert.That(gpuWithinNoise.HasPositiveTrend, Is.False);
            Assert.That(gpuStrict.HasPositiveTrend, Is.True);
        });
    }

    [Test]
    public void Tracker_RejectsNonMonotonicFrames()
    {
        var tracker = new LongRunStabilityTracker(capacity: 2);
        tracker.Add(CreateSample(2, 100, 100));

        Assert.That(
            () => tracker.Add(CreateSample(2, 100, 100)),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void OnlineTrend_UsesAllSamplesWithoutRetainingHistory()
    {
        var trend = new LongRunMemoryTrendAccumulator();
        for (int frame = 0; frame < 10_000; frame++)
            trend.Add(frame, checked((ulong)(1_000 + frame * 4)));

        LongRunMemoryTrend result = trend.Evaluate(
            "managed-memory",
            noiseToleranceBytes: 100);

        Assert.Multiple(() =>
        {
            Assert.That(result.SampleCount, Is.EqualTo(10_000));
            Assert.That(result.SlopeBytesPerFrame, Is.EqualTo(4.0).Within(1e-9));
            Assert.That(result.HasPositiveTrend, Is.True);
        });
    }

    private static LongRunStabilitySample CreateSample(
        int frame,
        long managedBytes,
        ulong trackedGpuBytes) =>
        new(
            frame,
            managedBytes,
            0,
            0,
            0,
            new DescriptorPressureSnapshot(64, 4, 4, 64, 4, 4, 10))
        {
            TrackedGpuMemoryBytes = trackedGpuBytes
        };
}
