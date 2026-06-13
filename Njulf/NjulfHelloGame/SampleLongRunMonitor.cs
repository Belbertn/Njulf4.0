using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed class SampleLongRunMonitor
{
    private readonly LongRunStabilityTracker _tracker = new();

    public LongRunStabilityTracker Tracker => _tracker;

    public void Sample(int frameIndex)
    {
        _tracker.Sample(frameIndex, new DescriptorPressureSnapshot(0, 0, 0, 0, 0, 0, 0));
    }
}
