using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Automatic-planar work submitted by one renderer frame. Completed snapshots
/// are joined to the timestamp query from the same frame slot so capture and
/// reprojection timings cannot be phase-shifted onto a newer CPU frame.
/// </summary>
public readonly record struct AutomaticPlanarLifecycleFrameSnapshot(
    bool Valid,
    int FrameSlot,
    ulong FrameSerial,
    bool GpuTimingRecorded,
    int SelectedCount,
    int CaptureCount,
    int ReprojectionCount,
    int BitsetCaptureCount,
    int SortedListFallbackCount,
    int MetadataCapacityRejectionCount);

internal sealed class AutomaticPlanarSubmittedFrameRing
{
    private readonly AutomaticPlanarLifecycleFrameSnapshot[] _frames =
        new AutomaticPlanarLifecycleFrameSnapshot[
            RenderingConstants.FramesInFlight];
    private readonly bool[] _pending =
        new bool[RenderingConstants.FramesInFlight];

    public void MarkSubmitted(
        int frameSlot,
        in AutomaticPlanarLifecycleFrameSnapshot frame)
    {
        RenderingConstants.ValidateFrameIndex(frameSlot);
        if (!frame.Valid || frame.FrameSlot != frameSlot)
        {
            throw new ArgumentException(
                $"Automatic-planar frame identity is invalid for slot {frameSlot}.",
                nameof(frame));
        }
        if (_pending[frameSlot])
        {
            throw new InvalidOperationException(
                $"Automatic-planar frame slot {frameSlot} was reused before its submitted workload was consumed.");
        }

        _frames[frameSlot] = frame;
        _pending[frameSlot] = true;
    }

    public bool TryConsume(
        int frameSlot,
        out AutomaticPlanarLifecycleFrameSnapshot frame)
    {
        RenderingConstants.ValidateFrameIndex(frameSlot);
        if (!_pending[frameSlot])
        {
            frame = default;
            return false;
        }

        frame = _frames[frameSlot];
        _frames[frameSlot] = default;
        _pending[frameSlot] = false;
        return true;
    }
}
