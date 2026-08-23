using System;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Backend boundary for one complete C5 generation. The opaque resource value
/// owns every image, buffer, descriptor set, pipeline binding and concrete
/// graph binding needed by the supplied layout.
/// </summary>
public interface ISimpleDdgiNearFieldResidualGenerationBackend<TResources>
    where TResources : class
{
    SimpleDdgiNearFieldResidualGenerationAllocation<TResources> Allocate(
        ulong generation,
        in SimpleDdgiNearFieldResidualLayout layout);

    void Destroy(
        SimpleDdgiNearFieldResidualGenerationAllocation<TResources> allocation);
}

/// <summary>One indivisible, backend-owned C5 resource generation.</summary>
public sealed record SimpleDdgiNearFieldResidualGenerationAllocation<TResources>(
    ulong Generation,
    SimpleDdgiNearFieldResidualLayout Layout,
    ulong AllocatedBytes,
    TResources Resources)
    where TResources : class;

public readonly record struct SimpleDdgiNearFieldResidualGenerationRequestResult(
    bool Accepted,
    bool ReplacementReady,
    bool CanonicalFallbackRequired,
    string Reason);

public readonly record struct SimpleDdgiNearFieldResidualGenerationSnapshot(
    ulong ActiveGeneration,
    ulong PendingGeneration,
    ulong RetiredGeneration,
    int RequestedSourceWidth,
    int RequestedSourceHeight,
    ulong ActiveBytes,
    ulong PendingBytes,
    ulong RetiredBytes,
    ulong LiveBytes,
    ulong PeakLiveBytes,
    ulong SteadyBudgetBytes,
    ulong PeakBudgetBytes,
    ulong GreatestActiveReferenceFenceValue,
    uint LayoutEpoch,
    uint HistoryEpoch,
    ulong CoalescedRequestCount,
    ulong AllocationFailureCount,
    bool CanonicalFallbackRequired,
    bool HasQueuedRequest,
    string State)
{
    public bool HasActive => ActiveGeneration != 0UL;
    public bool HasPending => PendingGeneration != 0UL;
    public bool HasRetired => RetiredGeneration != 0UL;
}

/// <summary>
/// Fence-driven C5 resize transaction. It never waits for the device. A new
/// generation is compiled and allocated without mutating the active one,
/// published at a frame boundary, and the prior generation is reclaimed only
/// after its greatest referencing frame-fence value completes.
/// </summary>
public sealed class SimpleDdgiNearFieldResidualGenerationTransaction<TResources>
    : IDisposable
    where TResources : class
{
    public const ulong DefaultSteadyBudgetBytes = 96UL * 1024UL * 1024UL;
    public const ulong DefaultPeakBudgetBytes = 192UL * 1024UL * 1024UL;

    private readonly object _sync = new();
    private readonly ISimpleDdgiNearFieldResidualGenerationBackend<TResources>
        _backend;
    private readonly GpuCompletionRetirementQueue _retirement;
    private readonly ulong _steadyBudgetBytes;
    private readonly ulong _peakBudgetBytes;

    private SimpleDdgiNearFieldResidualGenerationAllocation<TResources>?
        _active;
    private SimpleDdgiNearFieldResidualGenerationAllocation<TResources>?
        _pending;
    private SimpleDdgiNearFieldResidualGenerationAllocation<TResources>?
        _retired;
    private SimpleDdgiNearFieldResidualLayout _requestedLayout;
    private SimpleDdgiNearFieldResidualLayout _queuedLayout;
    private bool _hasRequestedLayout;
    private bool _hasQueuedLayout;
    private bool _canonicalFallbackRequired;
    private ulong _greatestActiveReferenceFenceValue;
    private ulong _generation;
    private ulong _peakLiveBytes;
    private ulong _coalescedRequestCount;
    private ulong _allocationFailureCount;
    private uint _layoutEpoch;
    private uint _historyEpoch;
    private string _state = "uninitialized";
    private bool _disposed;

    public SimpleDdgiNearFieldResidualGenerationTransaction(
        ISimpleDdgiNearFieldResidualGenerationBackend<TResources> backend,
        ulong steadyBudgetBytes = DefaultSteadyBudgetBytes,
        ulong peakBudgetBytes = DefaultPeakBudgetBytes)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (steadyBudgetBytes == 0UL || peakBudgetBytes < steadyBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(steadyBudgetBytes));
        _steadyBudgetBytes = steadyBudgetBytes;
        _peakBudgetBytes = peakBudgetBytes;
        _retirement = new GpuCompletionRetirementQueue(
            capacity: 1,
            memoryBudgetBytes: peakBudgetBytes);
    }

    public SimpleDdgiNearFieldResidualGenerationSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Captures the immutable active allocation under the transaction lock.
    /// The returned owner remains valid until a later frame-boundary commit or
    /// terminal disposal; callers publish it immediately on the renderer
    /// thread rather than retaining it as an independent lifetime.
    /// </summary>
    public bool TryGetActiveAllocation(
        out SimpleDdgiNearFieldResidualGenerationAllocation<TResources>?
            allocation)
    {
        lock (_sync)
        {
            if (_disposed || _active is null)
            {
                allocation = null;
                return false;
            }
            allocation = _active;
            return true;
        }
    }

    /// <summary>Installs the first generation without creating a retiree.</summary>
    public bool TryInitialize(
        in SimpleDdgiNearFieldResidualLayout layout,
        out string failure)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_active is not null || _pending is not null ||
                _retired is not null)
            {
                failure = "near-field-generation-already-initialized";
                return false;
            }
            if (!TryValidateLayoutNoLock(layout, out failure))
                return false;

            _requestedLayout = layout;
            _hasRequestedLayout = true;
            if (!TryAllocateNoLock(layout, out var allocation, out failure))
            {
                _canonicalFallbackRequired = true;
                _state = failure;
                return false;
            }

            _active = allocation;
            _layoutEpoch = 1U;
            _historyEpoch = 1U;
            _canonicalFallbackRequired = false;
            _state = "active-history-invalid";
            UpdatePeakNoLock();
            failure = "valid";
            return true;
        }
    }

    /// <summary>
    /// Requests a replacement covered by an archived extent envelope. A valid
    /// request remains accepted when allocation must wait for a retiree; C5 is
    /// then suppressed and canonical DDGI+B3 remains authoritative.
    /// </summary>
    public SimpleDdgiNearFieldResidualGenerationRequestResult RequestReplacement(
        in SimpleDdgiNearFieldResidualLayout layout,
        in SimpleDdgiNearFieldResidualExtentEnvelope extentEnvelope)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryValidateLayoutNoLock(layout, out string failure))
                return RejectNoLock(failure);
            if (!extentEnvelope.IsValid || !extentEnvelope.Contains(layout))
            {
                return RejectNoLock(
                    "near-field-generation-evidence-extent-envelope-mismatch");
            }

            bool repeatsQueuedRequest = _hasQueuedLayout || _pending is not null;
            _requestedLayout = layout;
            _hasRequestedLayout = true;
            if (_active is { } active && active.Layout.Equals(layout))
            {
                if (_pending is not null)
                {
                    _backend.Destroy(_pending);
                    _pending = null;
                    _coalescedRequestCount++;
                }
                if (_hasQueuedLayout)
                    _coalescedRequestCount++;
                _hasQueuedLayout = false;
                _queuedLayout = default;
                _canonicalFallbackRequired = false;
                _state = "active-layout-already-current";
                return new(true, false, false, _state);
            }

            if (repeatsQueuedRequest)
                _coalescedRequestCount++;
            if (_pending is not null)
            {
                // Pending generations have never been referenced by a command
                // buffer and can be destroyed immediately while coalescing.
                _backend.Destroy(_pending);
                _pending = null;
            }

            _canonicalFallbackRequired = true;
            if (_retired is not null)
            {
                QueueNewestNoLock(layout);
                _state = "replacement-deferred-until-retirement";
                return new(true, false, true, _state);
            }

            if (!TryAllocatePendingNoLock(layout, out failure))
            {
                QueueNewestNoLock(layout);
                _state = failure;
                return new(true, false, true, failure);
            }

            _hasQueuedLayout = false;
            _queuedLayout = default;
            _state = "replacement-pending-frame-boundary";
            return new(true, true, true, _state);
        }
    }

    /// <summary>
    /// Explicit developer-mode overload. AutoQualified callers must use the
    /// envelope-taking overload above.
    /// </summary>
    public SimpleDdgiNearFieldResidualGenerationRequestResult RequestReplacement(
        in SimpleDdgiNearFieldResidualLayout layout) =>
        RequestReplacement(
            layout,
            SimpleDdgiNearFieldResidualExtentEnvelope.Exact(layout));

    /// <summary>
    /// Publishes the prepared generation atomically. The old generation is
    /// charged to exactly one fence-backed retirement record.
    /// </summary>
    public bool TryCommitAtFrameBoundary(
        ulong greatestReferencingFrameFenceValue,
        ulong currentFrame,
        out string failure)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pending is null)
            {
                failure = "near-field-generation-no-pending-replacement";
                return false;
            }
            if (_retired is not null || !_retirement.IsEmpty)
            {
                failure = "near-field-generation-retirement-slot-occupied";
                return false;
            }

            if (_active is { } prior)
            {
                ulong fenceValue = Math.Max(
                    greatestReferencingFrameFenceValue,
                    _greatestActiveReferenceFenceValue);
                if (fenceValue == 0UL)
                {
                    // A generation never named by submitted work has no GPU
                    // reader and therefore needs no retirement record.
                    _backend.Destroy(prior);
                }
                else
                {
                    var record = new GpuRetirementRecord(
                        prior.Generation,
                        prior.AllocatedBytes,
                        currentFrame,
                        GpuCompletionToken.ForFrameFence(fenceValue),
                        new GpuRetirementResource(
                            GpuRetirementResourceKind.Allocation,
                            prior.Generation));
                    if (!_retirement.TryEnqueue(
                            record,
                            _pending.AllocatedBytes,
                            out GpuRetirementAdmissionFailure admissionFailure))
                    {
                        failure = admissionFailure switch
                        {
                            GpuRetirementAdmissionFailure.MemoryBudget =>
                                "near-field-generation-peak-budget-exceeded",
                            GpuRetirementAdmissionFailure.Capacity =>
                                "near-field-generation-retirement-slot-occupied",
                            _ => "near-field-generation-retirement-admission-invalid"
                        };
                        return false;
                    }
                    _retired = prior;
                }
            }

            _active = _pending;
            _pending = null;
            _greatestActiveReferenceFenceValue = 0UL;
            _layoutEpoch = NextNonZero(_layoutEpoch);
            _historyEpoch = NextNonZero(_historyEpoch);
            _canonicalFallbackRequired = !_hasRequestedLayout ||
                !_active.Layout.Equals(_requestedLayout);
            _state = _retired is null
                ? "active-history-invalid"
                : "active-retirement-pending";
            UpdatePeakNoLock();
            failure = "valid";
            return true;
        }
    }

    public bool TryCommitAtFrameBoundary(
        ulong greatestReferencingFrameFenceValue,
        out string failure) => TryCommitAtFrameBoundary(
        greatestReferencingFrameFenceValue,
        currentFrame: greatestReferencingFrameFenceValue,
        out failure);

    /// <summary>Records a submitted frame that references the active bank.</summary>
    public bool RecordActiveReference(ulong frameFenceValue)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_active is null || frameFenceValue == 0UL)
                return false;
            _greatestActiveReferenceFenceValue = Math.Max(
                _greatestActiveReferenceFenceValue,
                frameFenceValue);
            return true;
        }
    }

    /// <summary>
    /// Non-blocking retirement poll. Completed resources are destroyed on the
    /// caller's renderer thread, then the newest coalesced request is prepared.
    /// </summary>
    public int PollCompleted(
        in GpuCompletionProgress progress,
        ulong currentFrame)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            Span<GpuRetirementRecord> completed =
                stackalloc GpuRetirementRecord[1];
            int count = _retirement.Poll(progress, completed, currentFrame);
            if (count != 0)
            {
                if (_retired is null ||
                    completed[0].ResourceGeneration != _retired.Generation)
                {
                    throw new InvalidOperationException(
                        "C5 retirement queue returned an unknown generation.");
                }
                _backend.Destroy(_retired);
                _retired = null;
                _state = "active-history-invalid";
            }

            if (_retired is null && _pending is null && _hasQueuedLayout)
            {
                SimpleDdgiNearFieldResidualLayout newest = _queuedLayout;
                if (TryAllocatePendingNoLock(
                        newest,
                        out string allocationFailure))
                {
                    _hasQueuedLayout = false;
                    _queuedLayout = default;
                    _state = "replacement-pending-frame-boundary";
                }
                else
                {
                    _state = allocationFailure;
                }
            }
            return count;
        }
    }

    public bool CanExecuteFor(
        in SimpleDdgiNearFieldResidualLayout liveLayout)
    {
        lock (_sync)
        {
            return !_disposed && !_canonicalFallbackRequired &&
                _active is { } active && active.Layout.Equals(liveLayout);
        }
    }

    /// <summary>
    /// Terminal shutdown only. The caller owns the external idle guarantee;
    /// ordinary resize and polling never invoke such a wait.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_pending is not null)
                _backend.Destroy(_pending);
            _pending = null;

            Span<GpuRetirementRecord> drained =
                stackalloc GpuRetirementRecord[1];
            _ = _retirement.DrainAfterExternalDeviceIdle(drained);
            if (_retired is not null)
                _backend.Destroy(_retired);
            _retired = null;

            if (_active is not null)
                _backend.Destroy(_active);
            _active = null;
            _state = "disposed";
        }
        GC.SuppressFinalize(this);
    }

    private bool TryAllocatePendingNoLock(
        in SimpleDdgiNearFieldResidualLayout layout,
        out string failure)
    {
        ulong chargedBeforeAllocation = _active?.AllocatedBytes ?? 0UL;
        if (layout.TotalBytes > _peakBudgetBytes - Math.Min(
                chargedBeforeAllocation,
                _peakBudgetBytes))
        {
            failure = "near-field-generation-peak-budget-exceeded";
            return false;
        }
        if (!TryAllocateNoLock(layout, out var allocation, out failure))
            return false;
        if (allocation.AllocatedBytes >
            _peakBudgetBytes - Math.Min(chargedBeforeAllocation,
                _peakBudgetBytes))
        {
            _backend.Destroy(allocation);
            _allocationFailureCount++;
            failure = "near-field-generation-peak-budget-exceeded";
            return false;
        }

        _pending = allocation;
        UpdatePeakNoLock();
        failure = "valid";
        return true;
    }

    private bool TryAllocateNoLock(
        in SimpleDdgiNearFieldResidualLayout layout,
        out SimpleDdgiNearFieldResidualGenerationAllocation<TResources>
            allocation,
        out string failure)
    {
        allocation = null!;
        ulong nextGeneration = NextNonZero(_generation);
        SimpleDdgiNearFieldResidualGenerationAllocation<TResources>?
            candidate = null;
        try
        {
            candidate = _backend.Allocate(nextGeneration, layout);
            if (candidate is null || candidate.Generation != nextGeneration ||
                !candidate.Layout.Equals(layout) || candidate.Resources is null ||
                candidate.AllocatedBytes < layout.TotalBytes ||
                candidate.AllocatedBytes > _steadyBudgetBytes)
            {
                if (candidate is not null)
                    _backend.Destroy(candidate);
                _allocationFailureCount++;
                failure =
                    "near-field-generation-backend-allocation-invalid";
                return false;
            }
        }
        catch (Exception exception)
        {
            if (candidate is not null)
            {
                try
                {
                    _backend.Destroy(candidate);
                }
                catch
                {
                    // Keep the primary allocation failure stable.
                }
            }
            _allocationFailureCount++;
            failure = "near-field-generation-allocation-failed:" +
                exception.GetType().Name;
            return false;
        }

        _generation = nextGeneration;
        allocation = candidate;
        failure = "valid";
        return true;
    }

    private bool TryValidateLayoutNoLock(
        in SimpleDdgiNearFieldResidualLayout layout,
        out string failure)
    {
        if (!layout.IsValid || layout.SourceWidth <= 0 ||
            layout.SourceHeight <= 0 || layout.TraceWidth <= 0 ||
            layout.TraceHeight <= 0 || layout.TotalBytes == 0UL)
        {
            failure = "near-field-generation-layout-invalid";
            return false;
        }
        if (layout.TotalBytes > _steadyBudgetBytes)
        {
            failure = "near-field-generation-steady-budget-exceeded";
            return false;
        }
        failure = "valid";
        return true;
    }

    private SimpleDdgiNearFieldResidualGenerationRequestResult RejectNoLock(
        string failure)
    {
        _canonicalFallbackRequired = true;
        _state = failure;
        return new(false, false, true, failure);
    }

    private void QueueNewestNoLock(
        in SimpleDdgiNearFieldResidualLayout layout)
    {
        _queuedLayout = layout;
        _hasQueuedLayout = true;
    }

    private void UpdatePeakNoLock()
    {
        ulong live = checked(
            (_active?.AllocatedBytes ?? 0UL) +
            (_pending?.AllocatedBytes ?? 0UL) +
            (_retired?.AllocatedBytes ?? 0UL));
        _peakLiveBytes = Math.Max(_peakLiveBytes, live);
        if (live > _peakBudgetBytes)
        {
            throw new InvalidOperationException(
                "C5 generation ownership exceeded its peak budget.");
        }
    }

    private SimpleDdgiNearFieldResidualGenerationSnapshot CreateSnapshotNoLock()
    {
        ulong activeBytes = _active?.AllocatedBytes ?? 0UL;
        ulong pendingBytes = _pending?.AllocatedBytes ?? 0UL;
        ulong retiredBytes = _retired?.AllocatedBytes ?? 0UL;
        return new SimpleDdgiNearFieldResidualGenerationSnapshot(
            _active?.Generation ?? 0UL,
            _pending?.Generation ?? 0UL,
            _retired?.Generation ?? 0UL,
            _hasRequestedLayout ? _requestedLayout.SourceWidth : 0,
            _hasRequestedLayout ? _requestedLayout.SourceHeight : 0,
            activeBytes,
            pendingBytes,
            retiredBytes,
            checked(activeBytes + pendingBytes + retiredBytes),
            _peakLiveBytes,
            _steadyBudgetBytes,
            _peakBudgetBytes,
            _greatestActiveReferenceFenceValue,
            _layoutEpoch,
            _historyEpoch,
            _coalescedRequestCount,
            _allocationFailureCount,
            _canonicalFallbackRequired,
            _hasQueuedLayout,
            _state);
    }

    private static uint NextNonZero(uint value)
    {
        uint next = unchecked(value + 1U);
        return next == 0U ? 1U : next;
    }

    private static ulong NextNonZero(ulong value)
    {
        ulong next = unchecked(value + 1UL);
        return next == 0UL ? 1UL : next;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(
                SimpleDdgiNearFieldResidualGenerationTransaction<TResources>));
        }
    }
}
