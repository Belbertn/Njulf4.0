using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Immediate admission result for one explicit all-probe recapture request.
/// This describes CPU scheduler admission only; submitted GPU work and
/// completion remain owned by <see cref="ReflectionProbeLifecycleSnapshot"/>.
/// </summary>
public readonly record struct ReflectionProbeRecaptureRequestSummary(
    int RequestedProbeCount,
    int AdmittedProbeCount,
    int DeferredProbeCount,
    int CoalescedProbeCount,
    int RejectedProbeCount,
    ReflectionProbeLifecycleSnapshot BeforeLifecycle,
    ReflectionProbeLifecycleSnapshot AfterLifecycle)
{
    public static ReflectionProbeRecaptureRequestSummary Empty => default;
}

/// <summary>
/// Exclusive current lifecycle counts plus same-frame work pulses. This is a
/// compact value snapshot: reading it is O(1) and allocation-free.
/// </summary>
public readonly record struct ReflectionProbeLifecycleSnapshot(
    int QueuedCount,
    int ActiveCount,
    ReflectionProbeCaptureState State,
    int AwaitingGpuCompletionCount,
    int PublishedCount,
    int CapturesStartedThisFrame,
    int CapturesCompletedThisFrame,
    int CaptureFaceUnitsThisFrame,
    int PrefilterMipUnitsThisFrame,
    int PublishCopyUnitsThisFrame,
    ulong CapturesStartedTotal,
    ulong CapturesCompletedTotal,
    ulong CapturesPublishedTotal,
    ulong CaptureFaceUnitsTotal,
    ulong PrefilterMipUnitsTotal,
    ulong PublishCopyUnitsTotal);

/// <summary>
/// Lifecycle evidence pinned to an explicit renderer frame-slot identity.
/// Current and completed snapshots use the same shape so consumers cannot
/// accidentally interpret a completed workload as current-frame state.
/// </summary>
public readonly record struct ReflectionProbeLifecycleFrameSnapshot(
    bool Valid,
    int FrameSlot,
    ulong FrameSerial,
    bool GpuTimingRecorded,
    ReflectionProbeLifecycleSnapshot Lifecycle);

/// <summary>
/// Work and lifecycle state owned by one successfully submitted renderer frame
/// slot. The matching completed timestamp query must consume this exact slot.
/// </summary>
internal readonly record struct ReflectionProbeSubmittedFrameTelemetry(
    int FrameSlot,
    ulong FrameSerial,
    int CaptureFaceUnitCount,
    int PrefilterMipUnitCount,
    int PublishCopyUnitCount,
    bool GpuTimingRecorded,
    ReflectionProbeLifecycleSnapshot Lifecycle)
{
    public bool HasGpuWork =>
        CaptureFaceUnitCount > 0 ||
        PrefilterMipUnitCount > 0 ||
        PublishCopyUnitCount > 0;

    public ReflectionProbeLifecycleFrameSnapshot ToLifecycleFrameSnapshot() =>
        new(
            Valid: true,
            FrameSlot,
            FrameSerial,
            GpuTimingRecorded,
            Lifecycle);
}

/// <summary>
/// Fixed frame-slot ring. Entries become pending only after a successful queue
/// submission and are consumed when that same slot's fence/timestamps complete.
/// </summary>
internal sealed class ReflectionProbeSubmittedFrameRing
{
    private readonly ReflectionProbeSubmittedFrameTelemetry[] _frames;
    private readonly bool[] _pending;

    public ReflectionProbeSubmittedFrameRing()
    {
        _frames = new ReflectionProbeSubmittedFrameTelemetry[
            RenderingConstants.FramesInFlight];
        _pending = new bool[RenderingConstants.FramesInFlight];
    }

    public int FrameSlotCount => _frames.Length;

    public void MarkSubmitted(
        int frameSlot,
        in ReflectionProbeSubmittedFrameTelemetry frame)
    {
        ValidateFrameSlot(frameSlot);
        if (frame.FrameSlot != frameSlot)
        {
            throw new ArgumentException(
                $"Reflection frame identity names slot {frame.FrameSlot}, but was submitted to slot {frameSlot}.",
                nameof(frame));
        }
        if (_pending[frameSlot])
        {
            throw new InvalidOperationException(
                $"Reflection frame slot {frameSlot} was reused before its submitted workload was consumed.");
        }

        _frames[frameSlot] = frame;
        _pending[frameSlot] = true;
    }

    public bool TryConsume(
        int frameSlot,
        out ReflectionProbeSubmittedFrameTelemetry frame)
    {
        ValidateFrameSlot(frameSlot);
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

    public bool IsPending(int frameSlot)
    {
        ValidateFrameSlot(frameSlot);
        return _pending[frameSlot];
    }

    private void ValidateFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
    }
}

internal static class ReflectionProbeTelemetryValueMapper
{
    public static int CaptureBudgetUsedMicroseconds(
        in ReflectionProbeGpuBudgetSnapshot budget) =>
        Math.Max(0, budget.ReservedMicroseconds);

    public static int CaptureBudgetExceeded(
        in ReflectionProbeGpuBudgetSnapshot budget) =>
        budget.BudgetExhausted ? 1 : 0;
}

/// <summary>
/// Mutable, allocation-free counters whose reset boundary is BeginCaptureFrame.
/// Upload and completion polling intentionally have no reset authority.
/// </summary>
internal struct ReflectionProbeCaptureFrameCounters
{
    public int CapturesStartedThisFrame { get; private set; }
    public int CapturesCompletedThisFrame { get; private set; }
    public int CaptureFaceUnitsThisFrame { get; private set; }
    public int PrefilterMipUnitsThisFrame { get; private set; }
    public int PublishCopyUnitsThisFrame { get; private set; }
    public ulong CaptureFaceUnitsTotal { get; private set; }
    public ulong PrefilterMipUnitsTotal { get; private set; }
    public ulong PublishCopyUnitsTotal { get; private set; }

    public void BeginCaptureFrame()
    {
        CapturesStartedThisFrame = 0;
        CapturesCompletedThisFrame = 0;
        CaptureFaceUnitsThisFrame = 0;
        PrefilterMipUnitsThisFrame = 0;
        PublishCopyUnitsThisFrame = 0;
    }

    public void RecordStartedUnit(
        ReflectionProbeWorkKind kind,
        bool startsCapture)
    {
        switch (kind)
        {
            case ReflectionProbeWorkKind.CaptureFace:
                CaptureFaceUnitsThisFrame++;
                CaptureFaceUnitsTotal++;
                if (startsCapture)
                    CapturesStartedThisFrame++;
                break;
            case ReflectionProbeWorkKind.PrefilterMip:
                PrefilterMipUnitsThisFrame++;
                PrefilterMipUnitsTotal++;
                break;
            case ReflectionProbeWorkKind.PublishCopy:
                PublishCopyUnitsThisFrame++;
                PublishCopyUnitsTotal++;
                break;
        }
    }

    public void RecordCompletedCapture() => CapturesCompletedThisFrame++;
}

internal static class ReflectionProbeLifecycleSnapshotFactory
{
    public static ReflectionProbeLifecycleSnapshot Create(
        ReflectionProbeCaptureScheduler scheduler,
        int publishedCount,
        ulong capturesCompletedTotal,
        in ReflectionProbeCaptureFrameCounters counters)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        return new ReflectionProbeLifecycleSnapshot(
            scheduler.QueueDepth,
            scheduler.ActiveWorkCount,
            ResolveState(scheduler, publishedCount),
            scheduler.RetainedCompletionCount,
            Math.Max(0, publishedCount),
            counters.CapturesStartedThisFrame,
            counters.CapturesCompletedThisFrame,
            counters.CaptureFaceUnitsThisFrame,
            counters.PrefilterMipUnitsThisFrame,
            counters.PublishCopyUnitsThisFrame,
            scheduler.CapturesStartedTotal,
            capturesCompletedTotal,
            scheduler.CapturesPublishedTotal,
            counters.CaptureFaceUnitsTotal,
            counters.PrefilterMipUnitsTotal,
            counters.PublishCopyUnitsTotal);
    }

    private static ReflectionProbeCaptureState ResolveState(
        ReflectionProbeCaptureScheduler scheduler,
        int publishedCount)
    {
        ReflectionProbeCaptureState state = scheduler.CurrentState;
        if (state != ReflectionProbeCaptureState.Unregistered)
            return state;
        if (scheduler.RetainedCompletionCount > 0)
            return ReflectionProbeCaptureState.AwaitingGpuCompletion;
        return publishedCount > 0
            ? ReflectionProbeCaptureState.Published
            : ReflectionProbeCaptureState.Unregistered;
    }
}
