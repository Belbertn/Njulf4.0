using System;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Allocation-free rolling nearest-rank latency distribution. Values above
/// the configured maximum share the overflow bucket. The caller supplies
/// synchronization.
/// </summary>
internal sealed class RollingLatencyHistogram
{
    private readonly int[] _samples;
    private readonly int[] _fenwickTree;
    private readonly int _maximumBucket;
    private int _count;
    private int _nextSample;

    public RollingLatencyHistogram(int capacity, int maximumBucket)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumBucket <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBucket));

        _samples = new int[capacity];
        _fenwickTree = new int[maximumBucket + 2];
        _maximumBucket = maximumBucket;
    }

    public int Count => _count;
    public int Capacity => _samples.Length;
    public int MaximumBucket => _maximumBucket;

    public void Add(long value)
    {
        int bucket = (int)Math.Clamp(value, 0, _maximumBucket);
        if (_count == _samples.Length)
            AddToFenwick(_samples[_nextSample], -1);
        else
            _count++;

        _samples[_nextSample] = bucket;
        AddToFenwick(bucket, 1);
        _nextSample++;
        if (_nextSample == _samples.Length)
            _nextSample = 0;
    }

    public long GetPercentile(double percentile)
    {
        if (_count == 0)
            return 0;
        if (!double.IsFinite(percentile))
            throw new ArgumentOutOfRangeException(nameof(percentile));

        int rank = Math.Max(
            1,
            (int)Math.Ceiling(_count * Math.Clamp(percentile, 0.0, 1.0)));
        int index = 0;
        int prefix = 0;
        int step = HighestPowerOfTwoAtMost(_fenwickTree.Length - 1);
        for (; step != 0; step >>= 1)
        {
            int candidate = index + step;
            if (candidate < _fenwickTree.Length &&
                prefix + _fenwickTree[candidate] < rank)
            {
                index = candidate;
                prefix += _fenwickTree[candidate];
            }
        }

        // Fenwick index one represents latency bucket zero.
        return Math.Min(index, _maximumBucket);
    }

    private void AddToFenwick(int bucket, int delta)
    {
        for (int index = bucket + 1;
             index < _fenwickTree.Length;
             index += index & -index)
        {
            _fenwickTree[index] += delta;
        }
    }

    private static int HighestPowerOfTwoAtMost(int value)
    {
        int result = 1;
        while (result <= value / 2)
            result <<= 1;
        return result;
    }
}
