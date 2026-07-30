using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Diagnostics;

public sealed record LongRunStabilitySample(
    int FrameIndex,
    long ManagedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    DescriptorPressureSnapshot DescriptorPressure)
{
    public ulong TrackedGpuMemoryBytes { get; init; }
    public ulong ActualGpuMemoryUsageBytes { get; init; }
    public ulong EffectiveGpuMemoryBudgetBytes { get; init; }
    public RenderBudgetStatus BudgetStatus { get; init; } = RenderBudgetStatus.Unknown;
    public IReadOnlyList<string> OverBudgetMetrics { get; init; } = Array.Empty<string>();
}

public sealed record LongRunMemoryTrend(
    string Signal,
    int SampleCount,
    int FirstFrame,
    int LastFrame,
    ulong FirstBytes,
    ulong LastBytes,
    long NetGrowthBytes,
    double SlopeBytesPerFrame,
    ulong NoiseToleranceBytes,
    bool HasPositiveTrend);

/// <summary>
/// Constant-memory least-squares accumulator for a complete soak interval.
/// This complements the bounded diagnostic sample window: release decisions
/// use every post-warmup observation even when old sample details are evicted.
/// </summary>
public sealed class LongRunMemoryTrendAccumulator
{
    private int _sampleCount;
    private int _firstFrame = -1;
    private int _lastFrame = -1;
    private ulong _firstBytes;
    private ulong _lastBytes;
    private double _frameOrigin;
    private double _valueOrigin;
    private double _sumX;
    private double _sumY;
    private double _sumXX;
    private double _sumXY;

    public int SampleCount => _sampleCount;

    public void Add(int frameIndex, ulong bytes)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (_sampleCount > 0 && frameIndex <= _lastFrame)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex),
                "Memory-trend frame indices must be strictly increasing.");
        }

        if (_sampleCount == 0)
        {
            _firstFrame = frameIndex;
            _firstBytes = bytes;
            _frameOrigin = frameIndex;
            _valueOrigin = bytes;
        }

        double x = frameIndex - _frameOrigin;
        double y = bytes - _valueOrigin;
        _sumX += x;
        _sumY += y;
        _sumXX += x * x;
        _sumXY += x * y;
        _lastFrame = frameIndex;
        _lastBytes = bytes;
        _sampleCount++;
    }

    public LongRunMemoryTrend Evaluate(
        string signal,
        ulong noiseToleranceBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(signal))
            throw new ArgumentException("A memory-trend signal name is required.", nameof(signal));
        if (_sampleCount == 0)
        {
            return new LongRunMemoryTrend(
                signal,
                0,
                -1,
                -1,
                0,
                0,
                0,
                0,
                noiseToleranceBytes,
                HasPositiveTrend: false);
        }

        double denominator = _sampleCount * _sumXX - _sumX * _sumX;
        double slope = _sampleCount < 2 || denominator <= double.Epsilon
            ? 0.0
            : (_sampleCount * _sumXY - _sumX * _sumY) / denominator;
        long netGrowth = SaturatingDifference(_lastBytes, _firstBytes);
        bool positive =
            _sampleCount >= 2 &&
            slope > 0.0 &&
            _lastBytes > SaturatingAdd(_firstBytes, noiseToleranceBytes);
        return new LongRunMemoryTrend(
            signal,
            _sampleCount,
            _firstFrame,
            _lastFrame,
            _firstBytes,
            _lastBytes,
            netGrowth,
            slope,
            noiseToleranceBytes,
            positive);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingDifference(ulong left, ulong right)
    {
        if (left >= right)
        {
            ulong difference = left - right;
            return difference > long.MaxValue ? long.MaxValue : (long)difference;
        }

        ulong magnitude = right - left;
        return magnitude > (ulong)long.MaxValue
            ? long.MinValue
            : -(long)magnitude;
    }
}

/// <summary>
/// Retains a bounded, chronological window of long-run telemetry. The tracker
/// deliberately stores no frame-by-frame history beyond <see cref="Capacity"/>
/// so the monitoring mechanism cannot itself become a soak-test leak.
/// </summary>
public sealed class LongRunStabilityTracker
{
    public const int DefaultCapacity = 256;

    private readonly object _sync = new();
    private readonly LongRunStabilitySample?[] _samples;
    private int _start;
    private int _count;
    private long _totalSampleCount;

    public LongRunStabilityTracker(int capacity = DefaultCapacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Long-run telemetry capacity must be at least two samples.");

        _samples = new LongRunStabilitySample[capacity];
    }

    public int Capacity => _samples.Length;

    public int Count
    {
        get
        {
            lock (_sync)
                return _count;
        }
    }

    public long TotalSampleCount
    {
        get
        {
            lock (_sync)
                return _totalSampleCount;
        }
    }

    public IReadOnlyList<LongRunStabilitySample> Samples
    {
        get
        {
            lock (_sync)
                return CreateSnapshotLocked();
        }
    }

    public void Sample(int frameIndex, DescriptorPressureSnapshot descriptorPressure)
    {
        Add(new LongRunStabilitySample(
            frameIndex,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            descriptorPressure));
    }

    public void Add(LongRunStabilitySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(sample.DescriptorPressure);

        lock (_sync)
        {
            if (_count > 0)
            {
                int latestIndex = (_start + _count - 1) % _samples.Length;
                LongRunStabilitySample latest = _samples[latestIndex]!;
                if (sample.FrameIndex <= latest.FrameIndex)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(sample),
                        "Long-run frame indices must be strictly increasing.");
                }
            }

            if (_count < _samples.Length)
            {
                int index = (_start + _count) % _samples.Length;
                _samples[index] = sample;
                _count++;
            }
            else
            {
                _samples[_start] = sample;
                _start = (_start + 1) % _samples.Length;
            }

            _totalSampleCount++;
        }
    }

    public bool HasSustainedManagedGrowth(double tolerance = 0.10)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        IReadOnlyList<LongRunStabilitySample> samples = Samples;
        if (samples.Count < 2)
            return false;

        long baseline = samples[0].ManagedBytes;
        long latest = samples[^1].ManagedBytes;
        return latest > baseline + (long)Math.Ceiling(Math.Max(0, baseline) * tolerance);
    }

    public LongRunMemoryTrend EvaluateManagedMemoryTrend(
        int warmupFrame,
        ulong noiseToleranceBytes = 0) =>
        EvaluateTrend(
            "managed-memory",
            warmupFrame,
            sample => checked((ulong)Math.Max(0, sample.ManagedBytes)),
            noiseToleranceBytes);

    public LongRunMemoryTrend EvaluateTrackedGpuMemoryTrend(
        int warmupFrame,
        ulong noiseToleranceBytes = 0) =>
        EvaluateTrend(
            "tracked-gpu-memory",
            warmupFrame,
            sample => sample.TrackedGpuMemoryBytes,
            noiseToleranceBytes);

    public LongRunMemoryTrend EvaluateActualGpuMemoryTrend(
        int warmupFrame,
        ulong noiseToleranceBytes = 0) =>
        EvaluateTrend(
            "actual-gpu-memory",
            warmupFrame,
            sample => sample.ActualGpuMemoryUsageBytes,
            noiseToleranceBytes);

    private LongRunMemoryTrend EvaluateTrend(
        string signal,
        int warmupFrame,
        Func<LongRunStabilitySample, ulong> valueSelector,
        ulong noiseToleranceBytes)
    {
        LongRunStabilitySample[] samples;
        lock (_sync)
        {
            samples = CreateSnapshotLocked()
                .Where(sample => sample.FrameIndex >= warmupFrame)
                .ToArray();
        }

        if (samples.Length == 0)
        {
            return new LongRunMemoryTrend(
                signal,
                0,
                -1,
                -1,
                0,
                0,
                0,
                0,
                noiseToleranceBytes,
                HasPositiveTrend: false);
        }

        ulong first = valueSelector(samples[0]);
        ulong last = valueSelector(samples[^1]);
        long netGrowth = SaturatingDifference(last, first);
        double slope = CalculateLeastSquaresSlope(samples, valueSelector);
        bool hasPositiveTrend =
            samples.Length >= 2 &&
            slope > 0.0 &&
            last > SaturatingAdd(first, noiseToleranceBytes);

        return new LongRunMemoryTrend(
            signal,
            samples.Length,
            samples[0].FrameIndex,
            samples[^1].FrameIndex,
            first,
            last,
            netGrowth,
            slope,
            noiseToleranceBytes,
            hasPositiveTrend);
    }

    private LongRunStabilitySample[] CreateSnapshotLocked()
    {
        var snapshot = new LongRunStabilitySample[_count];
        for (int index = 0; index < _count; index++)
            snapshot[index] = _samples[(_start + index) % _samples.Length]!;
        return snapshot;
    }

    private static double CalculateLeastSquaresSlope(
        IReadOnlyList<LongRunStabilitySample> samples,
        Func<LongRunStabilitySample, ulong> valueSelector)
    {
        if (samples.Count < 2)
            return 0.0;

        // Translate both axes to the first sample. This keeps the intermediate
        // values small during long soaks and avoids precision loss from absolute
        // frame indices and byte counts.
        double frameOrigin = samples[0].FrameIndex;
        double valueOrigin = valueSelector(samples[0]);
        double sumX = 0.0;
        double sumY = 0.0;
        double sumXX = 0.0;
        double sumXY = 0.0;
        foreach (LongRunStabilitySample sample in samples)
        {
            double x = sample.FrameIndex - frameOrigin;
            double y = valueSelector(sample) - valueOrigin;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double denominator = samples.Count * sumXX - sumX * sumX;
        if (denominator <= double.Epsilon)
            return 0.0;

        return (samples.Count * sumXY - sumX * sumY) / denominator;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingDifference(ulong left, ulong right)
    {
        if (left >= right)
        {
            ulong difference = left - right;
            return difference > long.MaxValue ? long.MaxValue : (long)difference;
        }

        ulong differenceMagnitude = right - left;
        return differenceMagnitude > (ulong)long.MaxValue
            ? long.MinValue
            : -(long)differenceMagnitude;
    }
}
