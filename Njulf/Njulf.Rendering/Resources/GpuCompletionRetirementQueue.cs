using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// The kind of completion primitive that makes a renderer-owned resource retirement record safe
/// to reclaim.  A frame-fence value is interpreted by the renderer's frame-fence ring; a timeline
/// value is paired with one specific semaphore identity.
/// </summary>
public enum GpuCompletionPrimitiveKind : byte
{
    FrameFence,
    TimelineSemaphore
}

public readonly record struct GpuCompletionToken(
    GpuCompletionPrimitiveKind Kind,
    ulong Identity,
    ulong Value)
{
    public bool IsValid => Value != 0UL &&
                           (Kind == GpuCompletionPrimitiveKind.FrameFence || Identity != 0UL);

    public static GpuCompletionToken ForFrameFence(ulong fenceValue) =>
        new(GpuCompletionPrimitiveKind.FrameFence, 0UL, fenceValue);

    public static GpuCompletionToken ForTimeline(ulong semaphoreIdentity, ulong timelineValue) =>
        new(GpuCompletionPrimitiveKind.TimelineSemaphore, semaphoreIdentity, timelineValue);
}

/// <summary>Monotonic completion values observed by the renderer during a non-blocking poll.</summary>
public readonly record struct GpuCompletionProgress(
    ulong CompletedFrameFenceValue,
    ulong TimelineSemaphoreIdentity,
    ulong CompletedTimelineValue);

/// <summary>
/// A typed, allocation-free description of one Vulkan object or allocation to reclaim.  The queue
/// intentionally does not own Vulkan delegates: destruction remains on the renderer thread after
/// the record has been proven complete.
/// </summary>
public enum GpuRetirementResourceKind : byte
{
    None,
    Image,
    ImageView,
    Buffer,
    BufferView,
    Sampler,
    Pipeline,
    PipelineLayout,
    AccelerationStructure,
    Allocation
}

public readonly record struct GpuRetirementResource(
    GpuRetirementResourceKind Kind,
    ulong Handle,
    ulong AllocationHandle = 0UL,
    ulong AuxiliaryHandle = 0UL);

public readonly record struct GpuRetirementRecord(
    ulong ResourceGeneration,
    ulong ByteCharge,
    ulong EnqueuedFrame,
    GpuCompletionToken Completion,
    GpuRetirementResource Resource);

public enum GpuRetirementAdmissionFailure : byte
{
    None,
    Capacity,
    MemoryBudget,
    InvalidCompletionToken,
    InvalidRecord
}

public readonly record struct GpuCompletionRetirementSnapshot(
    int ActiveCount,
    ulong ActiveBytes,
    ulong OldestAgeFrames,
    int PeakCount,
    ulong PeakBytes,
    ulong CapacityRejectionCount,
    ulong MemoryBudgetRejectionCount,
    ulong InvalidRecordCount,
    ulong RetiredCount);

/// <summary>
/// Bounded renderer-owned retirement storage.  Resize/streaming paths enqueue typed records and
/// return immediately; the renderer polls completion values and destroys only records reported by
/// <see cref="Poll"/>.  The steady-state path performs no managed allocation and does not invoke a
/// callback or wait for the device.
/// </summary>
public sealed class GpuCompletionRetirementQueue
{
    private readonly GpuRetirementRecord[] _records;
    private readonly ulong _memoryBudgetBytes;
    private int _count;
    private ulong _activeBytes;
    private int _peakCount;
    private ulong _peakBytes;
    private ulong _capacityRejections;
    private ulong _memoryBudgetRejections;
    private ulong _invalidRecords;
    private ulong _retiredCount;

    public GpuCompletionRetirementQueue(int capacity, ulong memoryBudgetBytes = ulong.MaxValue)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _records = new GpuRetirementRecord[capacity];
        _memoryBudgetBytes = memoryBudgetBytes;
    }

    public int Capacity => _records.Length;
    public int ActiveCount => _count;
    public ulong ActiveBytes => _activeBytes;
    public bool IsEmpty => _count == 0;

    public GpuCompletionRetirementSnapshot GetSnapshot(ulong currentFrame)
    {
        ulong oldestAge = 0UL;
        for (int index = 0; index < _count; index++)
        {
            ulong age = currentFrame >= _records[index].EnqueuedFrame
                ? currentFrame - _records[index].EnqueuedFrame
                : 0UL;
            if (age > oldestAge)
                oldestAge = age;
        }

        return new GpuCompletionRetirementSnapshot(
            _count,
            _activeBytes,
            oldestAge,
            _peakCount,
            _peakBytes,
            _capacityRejections,
            _memoryBudgetRejections,
            _invalidRecords,
            _retiredCount);
    }

    /// <summary>
    /// Checks admission without changing queue state.  <paramref name="liveBytes"/> is the
    /// renderer's current live allocation charge and is supplied by the owning resource manager.
    /// </summary>
    public bool CanAdmit(ulong liveBytes, ulong incomingBytes, out GpuRetirementAdmissionFailure failure)
        => CanAdmit(liveBytes, incomingBytes, 1, out failure);

    public bool CanAdmit(
        ulong liveBytes,
        ulong incomingBytes,
        int incomingRecordCount,
        out GpuRetirementAdmissionFailure failure)
    {
        if (incomingRecordCount <= 0)
        {
            failure = GpuRetirementAdmissionFailure.InvalidRecord;
            return false;
        }
        if (incomingBytes > ulong.MaxValue - liveBytes ||
            liveBytes + incomingBytes > ulong.MaxValue - _activeBytes)
        {
            failure = GpuRetirementAdmissionFailure.MemoryBudget;
            return false;
        }

        if (incomingRecordCount > _records.Length - _count)
        {
            failure = GpuRetirementAdmissionFailure.Capacity;
            return false;
        }

        ulong total = liveBytes + incomingBytes + _activeBytes;
        if (total > _memoryBudgetBytes)
        {
            failure = GpuRetirementAdmissionFailure.MemoryBudget;
            return false;
        }

        failure = GpuRetirementAdmissionFailure.None;
        return true;
    }

    public bool TryEnqueue(
        in GpuRetirementRecord record,
        ulong liveBytes,
        out GpuRetirementAdmissionFailure failure)
    {
        if (!record.Completion.IsValid || record.Resource.Kind == GpuRetirementResourceKind.None)
        {
            _invalidRecords++;
            failure = !record.Completion.IsValid
                ? GpuRetirementAdmissionFailure.InvalidCompletionToken
                : GpuRetirementAdmissionFailure.InvalidRecord;
            return false;
        }

        if (!CanAdmit(liveBytes, record.ByteCharge, out failure))
        {
            if (failure == GpuRetirementAdmissionFailure.Capacity)
                _capacityRejections++;
            else if (failure == GpuRetirementAdmissionFailure.MemoryBudget)
                _memoryBudgetRejections++;
            return false;
        }

        _records[_count++] = record;
        _activeBytes += record.ByteCharge;
        if (_count > _peakCount)
            _peakCount = _count;
        if (_activeBytes > _peakBytes)
            _peakBytes = _activeBytes;
        failure = GpuRetirementAdmissionFailure.None;
        return true;
    }

    /// <summary>
    /// Atomically admits a fixed batch of related resources. This is used for image/view
    /// generations: a failed preflight must leave every old object owned by the live generation,
    /// rather than enqueuing a prefix that would be duplicated on the next retry.
    /// </summary>
    public bool TryEnqueueBatch(
        ReadOnlySpan<GpuRetirementRecord> records,
        ulong liveBytes,
        out GpuRetirementAdmissionFailure failure)
    {
        if (records.Length == 0)
        {
            _invalidRecords++;
            failure = GpuRetirementAdmissionFailure.InvalidRecord;
            return false;
        }

        ulong incomingBytes = 0UL;
        for (int index = 0; index < records.Length; index++)
        {
            GpuRetirementRecord record = records[index];
            if (!record.Completion.IsValid || record.Resource.Kind == GpuRetirementResourceKind.None)
            {
                _invalidRecords++;
                failure = !record.Completion.IsValid
                    ? GpuRetirementAdmissionFailure.InvalidCompletionToken
                    : GpuRetirementAdmissionFailure.InvalidRecord;
                return false;
            }

            if (record.ByteCharge > ulong.MaxValue - incomingBytes)
            {
                _memoryBudgetRejections++;
                failure = GpuRetirementAdmissionFailure.MemoryBudget;
                return false;
            }
            incomingBytes += record.ByteCharge;
        }

        if (!CanAdmit(liveBytes, incomingBytes, records.Length, out failure))
        {
            if (failure == GpuRetirementAdmissionFailure.Capacity)
                _capacityRejections++;
            else if (failure == GpuRetirementAdmissionFailure.MemoryBudget)
                _memoryBudgetRejections++;
            return false;
        }

        records.CopyTo(_records.AsSpan(_count));
        _count += records.Length;
        _activeBytes += incomingBytes;
        if (_count > _peakCount)
            _peakCount = _count;
        if (_activeBytes > _peakBytes)
            _peakBytes = _activeBytes;
        failure = GpuRetirementAdmissionFailure.None;
        return true;
    }

    /// <summary>
    /// Moves completed records into the caller-owned span and compacts the fixed storage in place.
    /// The returned records are now owned by the caller and may be destroyed on the renderer
    /// thread.  If the span is too small, completed records remain queued and no record is lost.
    /// </summary>
    public int Poll(
        in GpuCompletionProgress progress,
        Span<GpuRetirementRecord> completed,
        ulong currentFrame)
    {
        int written = 0;
        int survivorCount = 0;
        ulong survivorBytes = 0UL;

        for (int index = 0; index < _count; index++)
        {
            GpuRetirementRecord record = _records[index];
            bool signaled = IsSignaled(record.Completion, progress);
            if (signaled && written < completed.Length)
            {
                completed[written++] = record;
                _retiredCount++;
                continue;
            }

            _records[survivorCount++] = record;
            survivorBytes += record.ByteCharge;
        }

        for (int index = survivorCount; index < _count; index++)
            _records[index] = default;

        _count = survivorCount;
        _activeBytes = survivorBytes;
        _ = currentFrame; // Kept in the API so polling and diagnostics share one frame boundary.
        return written;
    }

    /// <summary>
    /// Terminal renderer shutdown is allowed to establish device idle externally.  This method
    /// then returns every remaining typed record without invoking Vulkan itself.
    /// </summary>
    public int DrainAfterExternalDeviceIdle(Span<GpuRetirementRecord> completed)
    {
        int count = Math.Min(completed.Length, _count);
        for (int index = 0; index < count; index++)
        {
            completed[index] = _records[index];
            _retiredCount++;
        }

        int remaining = _count - count;
        for (int index = 0; index < remaining; index++)
            _records[index] = _records[index + count];
        for (int index = remaining; index < _count; index++)
            _records[index] = default;

        _count = remaining;
        ulong bytes = 0UL;
        for (int index = 0; index < _count; index++)
            bytes += _records[index].ByteCharge;
        _activeBytes = bytes;
        return count;
    }

    private static bool IsSignaled(in GpuCompletionToken token, in GpuCompletionProgress progress)
    {
        return token.Kind switch
        {
            GpuCompletionPrimitiveKind.FrameFence => progress.CompletedFrameFenceValue >= token.Value,
            GpuCompletionPrimitiveKind.TimelineSemaphore =>
                token.Identity == progress.TimelineSemaphoreIdentity &&
                progress.CompletedTimelineValue >= token.Value,
            _ => false
        };
    }
}
