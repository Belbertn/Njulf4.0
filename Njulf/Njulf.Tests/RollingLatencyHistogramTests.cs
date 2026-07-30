using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RollingLatencyHistogramTests
{
    [Test]
    public void Percentile_UsesNearestRankAndClampsOverflow()
    {
        var histogram = new RollingLatencyHistogram(capacity: 8, maximumBucket: 100);
        foreach (long sample in new long[] { -5, 10, 20, 30, 40, 50, 60, 1_000 })
            histogram.Add(sample);

        Assert.Multiple(() =>
        {
            Assert.That(histogram.Count, Is.EqualTo(8));
            Assert.That(histogram.GetPercentile(0), Is.Zero);
            Assert.That(histogram.GetPercentile(0.5), Is.EqualTo(30));
            Assert.That(histogram.GetPercentile(0.95), Is.EqualTo(100));
        });
    }

    [Test]
    public void Window_EvictsOldSamplesWithoutGrowing()
    {
        var histogram = new RollingLatencyHistogram(capacity: 4, maximumBucket: 100);
        foreach (long sample in new long[] { 100, 100, 100, 100, 1, 2, 3, 4 })
            histogram.Add(sample);

        Assert.Multiple(() =>
        {
            Assert.That(histogram.Count, Is.EqualTo(4));
            Assert.That(histogram.GetPercentile(0.5), Is.EqualTo(2));
            Assert.That(histogram.GetPercentile(0.95), Is.EqualTo(4));
        });
    }
}
