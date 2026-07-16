using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics;

public sealed record LongRunStabilitySample(
    int FrameIndex,
    long ManagedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    DescriptorPressureSnapshot DescriptorPressure);

public sealed class LongRunStabilityTracker
{
    private readonly List<LongRunStabilitySample> _samples = new();

    public IReadOnlyList<LongRunStabilitySample> Samples => _samples;

    public void Sample(int frameIndex, DescriptorPressureSnapshot descriptorPressure)
    {
        _samples.Add(new LongRunStabilitySample(
            frameIndex,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            descriptorPressure));
    }

    public bool HasSustainedManagedGrowth(double tolerance = 0.10)
    {
        if (_samples.Count < 2)
            return false;

        long baseline = _samples[0].ManagedBytes;
        long latest = _samples[^1].ManagedBytes;
        return latest > baseline + (long)Math.Ceiling(baseline * tolerance);
    }
}
