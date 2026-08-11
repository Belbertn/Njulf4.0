using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Native handle abstracted from the B1 lifetime state machine.  A zero handle
/// is never published as a usable GPU resource.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuBuffer(
    ulong Handle,
    ulong Bytes,
    ulong Offset = 0UL)
{
    public bool IsAllocated => Handle != 0UL && Bytes != 0UL;
}

/// <summary>
/// The two logical record/summary banks are contiguous in their respective
/// buffers.  Binding a single buffer at the fixed B1 slot therefore preserves
/// the append-only descriptor ABI while the push constants select a bank
/// offset.  No descriptor is rebound while a prior frame can reference it.
/// </summary>
public sealed record SimpleDdgiReceiverFeedbackGpuAllocation(
    ulong AllocationId,
    SimpleDdgiReceiverFeedbackGpuBuffer RecordBanks,
    SimpleDdgiReceiverFeedbackGpuBuffer SortScratch,
    SimpleDdgiReceiverFeedbackGpuBuffer SummaryBanks,
    SimpleDdgiReceiverFeedbackGpuBuffer CaptureCandidates,
    uint DescriptorCount)
{
    public void Validate(in SimpleDdgiReceiverFeedbackLayout layout)
    {
        if (!layout.TryGetGpuSortLayout(
                out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                out string layoutReason))
        {
            throw new ArgumentException(
                "B1 allocation requires the exact versioned GPU-sort layout: " +
                layoutReason,
                nameof(layout));
        }
        if (AllocationId == 0UL)
            throw new ArgumentException("B1 allocation requires a nonzero allocation identity.", nameof(AllocationId));

        ValidateBuffer(RecordBanks, gpuLayout.RequiredRecordBanksBytes, nameof(RecordBanks));
        ValidateBuffer(SortScratch, gpuLayout.RequiredSortScratchBytes, nameof(SortScratch));
        ValidateBuffer(SummaryBanks, gpuLayout.RequiredSummaryBanksBytes, nameof(SummaryBanks));
        ValidateBuffer(
            CaptureCandidates,
            layout.CaptureSource.RequiredBytes,
            nameof(CaptureCandidates));
        if (DescriptorCount != 4u)
        {
            throw new ArgumentException(
                "B1 exposes exactly records, sort scratch, summaries, and candidate-source descriptors.",
                nameof(DescriptorCount));
        }
    }

    private static void ValidateBuffer(
        in SimpleDdgiReceiverFeedbackGpuBuffer buffer,
        ulong expectedBytes,
        string parameterName)
    {
        bool rangeValid;
        try
        {
            _ = checked(buffer.Offset + buffer.Bytes);
            rangeValid = (buffer.Offset & (sizeof(uint) - 1UL)) == 0UL;
        }
        catch (OverflowException)
        {
            rangeValid = false;
        }
        if (expectedBytes == 0UL || !buffer.IsAllocated ||
            buffer.Bytes != expectedBytes || !rangeValid)
        {
            throw new ArgumentException(
                $"B1 buffer must be allocated with exactly {expectedBytes} bytes.",
                parameterName);
        }
    }
}

/// <summary>
/// Vulkan-specific implementations allocate storage buffers, update the four
/// fixed B1 descriptors only at a safe transition, and retire allocations
/// after all command buffers that reference them have completed.
/// </summary>
public interface ISimpleDdgiReceiverFeedbackGpuResourceAllocator
{
    SimpleDdgiReceiverFeedbackGpuAllocation Allocate(
        in SimpleDdgiReceiverFeedbackLayout layout);

    void Retire(SimpleDdgiReceiverFeedbackGpuAllocation allocation);
}

public enum SimpleDdgiReceiverFeedbackGpuResourceState : byte
{
    Disabled = 0,
    Ready = 1,
    Capturing = 2,
    Published = 3,
    RecreateRequired = 4,
    Disposed = 5
}

/// <summary>Immutable submission identity for an exact B1 write-bank.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackFrameToken(
    ulong AllocationEpoch,
    uint FeedbackGeneration,
    uint ViewportGeneration,
    ulong FrameSerial,
    int WriteBankIndex);

/// <summary>Result exposed to the scheduler at the following frame only.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackScheduleBinding(
    bool UseFeedback,
    int SummaryBankIndex,
    uint FeedbackGeneration,
    SimpleDdgiReceiverFeedbackBankValidation Validation);

/// <summary>Diagnostic state with no inferred allocation success.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuResourceSnapshot(
    SimpleDdgiReceiverFeedbackGpuResourceState State,
    bool IsEffectivelyEnabled,
    ulong AllocationEpoch,
    ulong AllocatedBytes,
    uint DescriptorCount,
    int PublishedBankIndex,
    uint PublishedGeneration,
    string Reason);

/// <summary>
/// Transactional, one-frame-late B1 resource/lifetime controller.  It accepts
/// only the exact compacted effective mode, never wraps a generation, and
/// refuses a whole write bank on any layout, overflow, or validation error.
/// The class intentionally has no scheduler-side partial data path: fallback
/// is ordinary quota scheduling.
/// </summary>
public sealed class SimpleDdgiReceiverFeedbackGpuResourceManager : IDisposable
{
    private readonly object _sync = new();
    private ISimpleDdgiReceiverFeedbackGpuResourceAllocator? _allocator;
    private SimpleDdgiReceiverFeedbackGpuAllocation? _allocation;
    private SimpleDdgiReceiverFeedbackLayout _layout;
    private readonly SimpleDdgiReceiverFeedbackFrameToken?[] _captures =
        new SimpleDdgiReceiverFeedbackFrameToken?[2];
    private SimpleDdgiReceiverFeedbackBankHeader? _publishedHeader;
    private ulong _allocationEpoch;
    private uint _lastGeneration;
    private uint _lastIssuedGeneration;
    private int _lastIssuedBankIndex = -1;
    private int _publishedBankIndex = -1;
    private SimpleDdgiReceiverFeedbackGpuResourceState _state;
    private string _reason = "disabled";
    private bool _disposed;

    public SimpleDdgiReceiverFeedbackGpuResourceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Returns the currently admitted native-allocation identity without
    /// publishing it to a scheduler.  Vulkan recording owns this handle only
    /// while the manager remains effectively enabled; scheduler consumers must
    /// instead use <see cref="AcquireForScheduling"/> after a complete,
    /// previous-frame header has been validated.
    /// </summary>
    public bool TryGetActiveAllocation(
        out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
        out SimpleDdgiReceiverFeedbackLayout layout)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null ||
                _state is SimpleDdgiReceiverFeedbackGpuResourceState.Disabled or
                    SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired)
            {
                allocation = default!;
                layout = default;
                return false;
            }

            allocation = _allocation;
            layout = _layout;
            return true;
        }
    }

    /// <summary>
    /// Applies a plan at a safe transition.  Failed replacements leave a
    /// prior allocation intact but make it unusable until a compatible plan is
    /// applied; this avoids dangling descriptors and accidental old-layout
    /// interpretation.
    /// </summary>
    public SimpleDdgiReceiverFeedbackGpuResourceSnapshot Configure(
        in SimpleDdgiReceiverFeedbackPlan plan,
        ISimpleDdgiReceiverFeedbackGpuResourceAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (plan.Mode.EffectiveMode != SimpleDdgiReceiverFeedbackMode.ExactCompacted)
            {
                DisableNoLock(plan.Mode.FallbackDetail);
                return CreateSnapshotNoLock();
            }
            if (!plan.Layout.TryGetGpuSortLayout(out _, out string layoutReason))
            {
                DisableNoLock("receiver-feedback-gpu-sort-layout-invalid:" + layoutReason);
                return CreateSnapshotNoLock();
            }

            SimpleDdgiReceiverFeedbackGpuAllocation? replacement = null;
            try
            {
                replacement = allocator.Allocate(plan.Layout);
                replacement.Validate(plan.Layout);
            }
            catch (Exception exception)
            {
                if (replacement is not null)
                {
                    try
                    {
                        allocator.Retire(replacement);
                    }
                    catch
                    {
                        // The allocator contract owns reporting a native
                        // retirement fault.  Never continue into a partial
                        // B1 allocation merely because validation failed.
                    }
                }
                Array.Clear(_captures);
                _publishedHeader = null;
                _publishedBankIndex = -1;
                _state = _allocation is null
                    ? SimpleDdgiReceiverFeedbackGpuResourceState.Disabled
                    : SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired;
                _reason = "receiver-feedback-allocation-failed:" + exception.GetType().Name;
                return CreateSnapshotNoLock();
            }

            SimpleDdgiReceiverFeedbackGpuAllocation? prior = _allocation;
            ISimpleDdgiReceiverFeedbackGpuResourceAllocator? priorAllocator = _allocator;
            _allocation = replacement;
            _allocator = allocator;
            _layout = plan.Layout;
            Array.Clear(_captures);
            _publishedHeader = null;
            _publishedBankIndex = -1;
            _lastGeneration = 0u;
            _lastIssuedGeneration = 0u;
            _lastIssuedBankIndex = -1;
            _allocationEpoch = NextNonZero(_allocationEpoch);
            _state = SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
            _reason = "allocated-awaiting-capture";

            if (prior is not null && priorAllocator is not null)
            {
                try
                {
                    priorAllocator.Retire(prior);
                }
                catch
                {
                    // The new allocation is valid but a deferred-retirement
                    // implementation must surface its own fault; don't risk
                    // reactivating an old descriptor/bank transaction here.
                    _reason = "allocated-prior-retirement-failed";
                    throw;
                }
            }
            return CreateSnapshotNoLock();
        }
    }

    public bool TryBeginCapture(
        uint viewportGeneration,
        ulong frameSerial,
        out SimpleDdgiReceiverFeedbackFrameToken token,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            token = default;
            if (_allocation is null || _state is SimpleDdgiReceiverFeedbackGpuResourceState.Disabled or
                SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired)
            {
                reason = "receiver-feedback-not-effectively-enabled";
                return false;
            }
            if (viewportGeneration == 0u || frameSerial == ulong.MaxValue)
            {
                reason = "receiver-feedback-frame-or-viewport-generation-invalid";
                return false;
            }
            if (!SimpleDdgiReceiverFeedbackBankValidator.TryGetNextGeneration(
                    _lastIssuedGeneration,
                    out uint nextGeneration))
            {
                _state = SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired;
                _reason = "receiver-feedback-generation-wrap-requires-recreate";
                reason = _reason;
                return false;
            }

            int writeBank = _lastIssuedBankIndex == 0 ? 1 : 0;
            if (_captures[writeBank].HasValue)
            {
                reason = "receiver-feedback-write-bank-still-in-flight";
                return false;
            }
            token = new SimpleDdgiReceiverFeedbackFrameToken(
                _allocationEpoch,
                nextGeneration,
                viewportGeneration,
                frameSerial,
                writeBank);
            _captures[writeBank] = token;
            _lastIssuedGeneration = nextGeneration;
            _lastIssuedBankIndex = writeBank;
            _state = SimpleDdgiReceiverFeedbackGpuResourceState.Capturing;
            _reason = "capturing-exact-receiver-feedback";
            reason = "valid";
            return true;
        }
    }

    /// <summary>
    /// Publishes only a complete GPU reduction header.  The expected scheduler
    /// serial is frame N+1, encoding the invariant that B1 is a one-frame-late
    /// priority signal rather than same-frame visibility authority.
    /// </summary>
    public SimpleDdgiReceiverFeedbackBankValidation CompleteCapture(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackBankHeader header)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return CompleteCaptureNoLock(token, header);
        }
    }

    /// <summary>
    /// Publishes a native B1 summary-bank header only after its versioned
    /// counts are checked against the admitted GPU partition.  The managed
    /// overload remains for CPU-reference callers; a Vulkan integration must
    /// use this overload after a visibility/readback boundary.
    /// </summary>
    public SimpleDdgiReceiverFeedbackBankValidation CompleteGpuCapture(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!IsCaptureTokenNoLock(token))
            {
                return RejectNoLock(
                    token,
                    "receiver-feedback-capture-token-mismatch");
            }
            if (!_layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out string layoutReason))
            {
                return RejectGpuHeaderNoLock(
                    token,
                    "receiver-feedback-gpu-sort-layout-invalid:" + layoutReason);
            }
            if (!SimpleDdgiReceiverFeedbackGpuSortAbi.IsCompleteAndReadable(
                    header,
                    gpuLayout))
            {
                return RejectGpuHeaderNoLock(
                    token,
                    "receiver-feedback-gpu-header-is-not-complete-for-admitted-layout");
            }

            return CompleteCaptureNoLock(
                token,
                SimpleDdgiReceiverFeedbackGpuSortAbi.ToManagedBankHeader(header));
        }
    }

    public void AbortCapture(string reason = "receiver-feedback-capture-aborted")
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            bool hadCapture = HasCaptureNoLock();
            if (!hadCapture)
                return;
            Array.Clear(_captures);
            _state = _allocation is null
                ? SimpleDdgiReceiverFeedbackGpuResourceState.Disabled
                : SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
            _reason = string.IsNullOrWhiteSpace(reason)
                ? "receiver-feedback-capture-aborted"
                : reason.Trim();
        }
    }

    /// <summary>
    /// Aborts one bank transaction without invalidating the other in-flight
    /// bank. This is required when two frame-ring submissions overlap and a
    /// recording/readback failure is local to only one of them.
    /// </summary>
    public void AbortCapture(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        string reason = "receiver-feedback-capture-aborted")
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!IsCaptureTokenNoLock(token))
                return;
            ClearCaptureTokenNoLock(token);
            _state = _allocation is null
                ? SimpleDdgiReceiverFeedbackGpuResourceState.Disabled
                : HasCaptureNoLock()
                    ? SimpleDdgiReceiverFeedbackGpuResourceState.Capturing
                    : _publishedHeader.HasValue
                        ? SimpleDdgiReceiverFeedbackGpuResourceState.Published
                        : SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
            _reason = string.IsNullOrWhiteSpace(reason)
                ? "receiver-feedback-capture-aborted"
                : reason.Trim();
        }
    }

    public SimpleDdgiReceiverFeedbackScheduleBinding AcquireForScheduling(
        uint viewportGeneration,
        ulong frameSerial)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null ||
                _state == SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired ||
                !_publishedHeader.HasValue ||
                _publishedBankIndex is < 0 or > 1)
            {
                return new SimpleDdgiReceiverFeedbackScheduleBinding(
                    false, -1, 0u,
                    new SimpleDdgiReceiverFeedbackBankValidation(
                        false, GiExperimentFallbackReason.ResourceIncomplete,
                        "receiver-feedback-previous-bank-unavailable"));
            }

            SimpleDdgiReceiverFeedbackBankValidation validation =
                SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                    _publishedHeader.Value,
                    SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                    _lastGeneration,
                    viewportGeneration,
                    frameSerial);
            if (!validation.UseFeedback)
            {
                _publishedHeader = null;
                _publishedBankIndex = -1;
                _state = SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
                _reason = validation.Detail;
                return new SimpleDdgiReceiverFeedbackScheduleBinding(
                    false, -1, 0u, validation);
            }
            return new SimpleDdgiReceiverFeedbackScheduleBinding(
                true,
                _publishedBankIndex,
                _lastGeneration,
                validation);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            RetireActiveNoLock();
            _state = SimpleDdgiReceiverFeedbackGpuResourceState.Disposed;
            _reason = "disposed";
        }
    }

    private bool IsCaptureTokenNoLock(
        in SimpleDdgiReceiverFeedbackFrameToken token) =>
        token.AllocationEpoch == _allocationEpoch &&
        token.WriteBankIndex is >= 0 and < 2 &&
        _captures[token.WriteBankIndex].HasValue &&
        _captures[token.WriteBankIndex]!.Value.Equals(token);

    private void ClearCaptureTokenNoLock(
        in SimpleDdgiReceiverFeedbackFrameToken token)
    {
        if (token.WriteBankIndex is >= 0 and < 2 &&
            _captures[token.WriteBankIndex].HasValue &&
            _captures[token.WriteBankIndex]!.Value.Equals(token))
        {
            _captures[token.WriteBankIndex] = null;
        }
    }

    private bool HasCaptureNoLock() =>
        _captures[0].HasValue || _captures[1].HasValue;

    private SimpleDdgiReceiverFeedbackBankValidation RejectNoLock(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        string detail)
    {
        ClearCaptureTokenNoLock(token);
        _state = _allocation is null
            ? SimpleDdgiReceiverFeedbackGpuResourceState.Disabled
            : HasCaptureNoLock()
                ? SimpleDdgiReceiverFeedbackGpuResourceState.Capturing
                : SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
        _reason = detail;
        return new SimpleDdgiReceiverFeedbackBankValidation(
            false,
            GiExperimentFallbackReason.GenerationMismatch,
            detail);
    }

    private SimpleDdgiReceiverFeedbackBankValidation RejectGpuHeaderNoLock(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        string detail)
    {
        ClearCaptureTokenNoLock(token);
        _state = _allocation is null
            ? SimpleDdgiReceiverFeedbackGpuResourceState.Disabled
            : HasCaptureNoLock()
                ? SimpleDdgiReceiverFeedbackGpuResourceState.Capturing
                : SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
        _reason = detail;
        return new SimpleDdgiReceiverFeedbackBankValidation(
            false,
            GiExperimentFallbackReason.FeedbackBankInvalid,
            detail);
    }

    private SimpleDdgiReceiverFeedbackBankValidation CompleteCaptureNoLock(
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackBankHeader header)
    {
        if (!IsCaptureTokenNoLock(token))
        {
            return RejectNoLock(
                token,
                "receiver-feedback-capture-token-mismatch");
        }
        if (!_layout.TryGetGpuSortLayout(
                out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                out string layoutReason))
        {
            return RejectGpuHeaderNoLock(
                token,
                "receiver-feedback-gpu-sort-layout-invalid:" + layoutReason);
        }
        if (header.RecordCapacity != gpuLayout.RecordCapacity)
        {
            return RejectGpuHeaderNoLock(
                token,
                "receiver-feedback-bank-record-capacity-does-not-match-admitted-layout");
        }

        SimpleDdgiReceiverFeedbackBankValidation validation =
            SimpleDdgiReceiverFeedbackBankValidator.ValidateForScheduling(
                header,
                SimpleDdgiReceiverFeedbackV2Abi.LayoutRevision,
                token.FeedbackGeneration,
                token.ViewportGeneration,
                checked(token.FrameSerial + 1UL));
        ClearCaptureTokenNoLock(token);
        if (!validation.UseFeedback)
        {
            _state = HasCaptureNoLock()
                ? SimpleDdgiReceiverFeedbackGpuResourceState.Capturing
                : SimpleDdgiReceiverFeedbackGpuResourceState.Ready;
            _reason = validation.Detail;
            return validation;
        }

        _lastGeneration = token.FeedbackGeneration;
        _publishedHeader = header;
        _publishedBankIndex = token.WriteBankIndex;
        _state = HasCaptureNoLock()
            ? SimpleDdgiReceiverFeedbackGpuResourceState.Capturing
            : SimpleDdgiReceiverFeedbackGpuResourceState.Published;
        _reason = "published-for-next-frame-scheduling";
        return validation;
    }

    private void DisableNoLock(string reason)
    {
        RetireActiveNoLock();
        _layout = default;
        Array.Clear(_captures);
        _publishedHeader = null;
        _publishedBankIndex = -1;
        _lastGeneration = 0u;
        _lastIssuedGeneration = 0u;
        _lastIssuedBankIndex = -1;
        _state = SimpleDdgiReceiverFeedbackGpuResourceState.Disabled;
        _reason = string.IsNullOrWhiteSpace(reason) ? "disabled" : reason.Trim();
    }

    private void RetireActiveNoLock()
    {
        if (_allocation is not null && _allocator is not null)
            _allocator.Retire(_allocation);
        _allocation = null;
        _allocator = null;
    }

    private SimpleDdgiReceiverFeedbackGpuResourceSnapshot CreateSnapshotNoLock()
    {
        ulong bytes = _allocation is null ? 0UL : _layout.TotalBytes;
        uint descriptors = _allocation?.DescriptorCount ?? 0u;
        return new SimpleDdgiReceiverFeedbackGpuResourceSnapshot(
            _state,
            _allocation is not null &&
            _state is not SimpleDdgiReceiverFeedbackGpuResourceState.Disabled and
                not SimpleDdgiReceiverFeedbackGpuResourceState.RecreateRequired and
                not SimpleDdgiReceiverFeedbackGpuResourceState.Disposed,
            _allocationEpoch,
            bytes,
            descriptors,
            _publishedBankIndex,
            _lastGeneration,
            _reason);
    }

    private static ulong NextNonZero(ulong value) =>
        value == ulong.MaxValue ? 1UL : value + 1UL;

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiReceiverFeedbackGpuResourceManager));
    }
}
