using System;
using System.Collections.Generic;

namespace Njulf.Rendering.GpuScene;

public sealed class GpuSceneDirtyRangeTracker
{
    private readonly SortedSet<int> _dirtySlots = new();

    public int DirtySlotCount => _dirtySlots.Count;

    public void MarkDirty(int slot)
    {
        if (slot < 0)
            throw new ArgumentOutOfRangeException(nameof(slot));

        _dirtySlots.Add(slot);
    }

    public void MarkRangeDirty(int start, int count)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        for (int i = 0; i < count; i++)
            _dirtySlots.Add(start + i);
    }

    public IReadOnlyList<GpuSceneUploadRange> BuildRangesAndClear()
    {
        if (_dirtySlots.Count == 0)
            return Array.Empty<GpuSceneUploadRange>();

        var ranges = new List<GpuSceneUploadRange>();
        int rangeStart = -1;
        int previous = -1;
        foreach (int slot in _dirtySlots)
        {
            if (rangeStart < 0)
            {
                rangeStart = slot;
                previous = slot;
                continue;
            }

            if (slot == previous + 1)
            {
                previous = slot;
                continue;
            }

            ranges.Add(new GpuSceneUploadRange(rangeStart, previous - rangeStart + 1));
            rangeStart = slot;
            previous = slot;
        }

        ranges.Add(new GpuSceneUploadRange(rangeStart, previous - rangeStart + 1));
        _dirtySlots.Clear();
        return ranges;
    }

    public void Clear() => _dirtySlots.Clear();
}
