using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Backend-owned buffer handle used by the C3 lifecycle boundary.  Zero is not
/// a valid allocated handle; the manager deliberately exposes no fake resource
/// when the effective guiding mode is off.
/// </summary>
public readonly record struct SimpleDdgiGuidingGpuBuffer(ulong Handle, ulong Bytes)
{
    public bool IsAllocated => Handle != 0UL && Bytes != 0UL;
}

/// <summary>
/// Fully allocated C3 resource set.  Native Vulkan ownership stays behind the
/// allocator boundary so this manager can be tested without a device while
/// still enforcing exact planned byte sizes and persistent descriptor counts.
/// The optional FP32 validation reference is host/readback or transient-pass
/// owned; it deliberately has no static bindless descriptor and must not steal
/// the source-cache direction/PDF sidecar slot.
/// </summary>
public sealed record SimpleDdgiGuidingGpuAllocation(
    ulong AllocationId,
    SimpleDdgiGuidingGpuBuffer DistributionBank0,
    SimpleDdgiGuidingGpuBuffer DistributionBank1,
    SimpleDdgiGuidingGpuBuffer TrainingScratch,
    SimpleDdgiGuidingGpuBuffer ValidationReference,
    uint DescriptorCount)
{
    public void Validate(in SimpleDdgiGuidingLayout layout)
    {
        if (AllocationId == 0UL)
            throw new ArgumentException("C3 allocation ID must be nonzero.", nameof(AllocationId));
        if (layout.PersistentBankCount != 2 ||
            layout.PersistentDoubleBufferedBytes == 0UL ||
            layout.PersistentDoubleBufferedBytes % 2UL != 0UL)
        {
            throw new ArgumentException("C3 layout has no valid double-buffered bank.",
                nameof(layout));
        }

        ulong bankBytes = layout.PersistentDoubleBufferedBytes / 2UL;
        ValidateBuffer(DistributionBank0, bankBytes, nameof(DistributionBank0));
        ValidateBuffer(DistributionBank1, bankBytes, nameof(DistributionBank1));
        ValidateBuffer(TrainingScratch, layout.TrainingScratchBytes,
            nameof(TrainingScratch));
        if (DistributionBank0.Handle == DistributionBank1.Handle ||
            DistributionBank0.Handle == TrainingScratch.Handle ||
            DistributionBank1.Handle == TrainingScratch.Handle)
        {
            throw new ArgumentException(
                "C3 read bank, write bank, and training scratch must be distinct native buffers.");
        }
        if (layout.ValidationReferenceAllocated)
        {
            ValidateBuffer(ValidationReference, layout.ValidationReferenceBankBytes,
                nameof(ValidationReference));
            if (ValidationReference.Handle == DistributionBank0.Handle ||
                ValidationReference.Handle == DistributionBank1.Handle ||
                ValidationReference.Handle == TrainingScratch.Handle)
            {
                throw new ArgumentException(
                    "C3 validation reference must not alias a transactional bank or training scratch.");
            }
        }
        else if (ValidationReference.Handle != 0UL || ValidationReference.Bytes != 0UL)
            throw new ArgumentException("Validation-reference buffer is present in a non-validation layout.",
                nameof(ValidationReference));

        // Bank 0, bank 1, and FP32 training partial scratch are the only
        // C3-manager-owned persistent bindless resources.  The optional
        // validation reference has no current shader consumer, while the
        // fourth reserved C3 slot belongs to the source cache sidecar.
        const uint expectedDescriptors = 3u;
        if (DescriptorCount != expectedDescriptors)
        {
            throw new ArgumentException(
                $"C3 allocation exposes {DescriptorCount} descriptors; expected {expectedDescriptors}.",
                nameof(DescriptorCount));
        }
    }

    private static void ValidateBuffer(
        in SimpleDdgiGuidingGpuBuffer buffer,
        ulong expectedBytes,
        string parameterName)
    {
        if (expectedBytes == 0UL)
        {
            if (buffer.Handle != 0UL || buffer.Bytes != 0UL)
                throw new ArgumentException("Unexpected zero-byte C3 buffer.", parameterName);
            return;
        }
        if (!buffer.IsAllocated || buffer.Bytes != expectedBytes)
        {
            throw new ArgumentException(
                $"C3 buffer must be allocated with exactly {expectedBytes} bytes.",
                parameterName);
        }
    }
}

/// <summary>
/// GPU allocation/retirement boundary.  A Vulkan implementation must defer
/// retirement until every descriptor/command buffer referencing an allocation
/// has passed its submission fence.  It must dispose partial native work if
/// <see cref="Allocate"/> throws.
/// </summary>
public interface ISimpleDdgiGuidingGpuResourceAllocator
{
    SimpleDdgiGuidingGpuAllocation Allocate(
        in SimpleDdgiGuidingLayout layout);

    void Retire(SimpleDdgiGuidingGpuAllocation allocation);
}

/// <summary>
/// Effective, already-admitted C3 request.  Requested settings do not appear
/// here on purpose: an unqualified or rejected setting passes false and cannot
/// cause allocation, descriptors, graph passes, or shader dispatch.
/// </summary>
public readonly record struct SimpleDdgiGuidingRuntimeRequest(
    bool IsEffectivelyEnabled,
    SimpleDdgiGuidingLayout Layout)
{
    /// <summary>
    /// Trace scratch representation consumed by the specialized production
    /// training extractor. A mode change recreates its pipeline at the same
    /// safe transition as the guiding allocation.
    /// </summary>
    public SimpleDdgiStoragePackingMode SourceStoragePackingMode { get; init; } =
        SimpleDdgiStoragePackingMode.Legacy;
}

public enum SimpleDdgiGuidingResourceState : byte
{
    Disabled = 0,
    ReadyForBuild = 1,
    Building = 2,
    Readable = 3
}

/// <summary>Visible state used by graph planning and diagnostics.</summary>
public readonly record struct SimpleDdgiGuidingRuntimeSnapshot(
    SimpleDdgiGuidingResourceState State,
    bool IsEffectivelyEnabled,
    ulong AllocationEpoch,
    ulong AllocatedBytes,
    uint DescriptorCount,
    int ReadBankIndex,
    int WriteBankIndex,
    uint ReadBankGeneration,
    uint PendingBankGeneration,
    int PublishedProbeCount,
    string Reason)
{
    /// <summary>
    /// Counts dispatches that can actually appear for this lifecycle state:
    /// train/build while a transaction is in flight, plus sample after a
    /// validated bank exists.  The optional validation dispatch is planned
    /// only in validation builds and is intentionally excluded here.
    /// </summary>
    public int ProductionPassCount => !IsEffectivelyEnabled
        ? 0
        : (State == SimpleDdgiGuidingResourceState.Building ? 2 : 0) +
            (HasReadableDistribution ? 1 : 0);

    public bool HasResources => IsEffectivelyEnabled && AllocatedBytes != 0UL;

    /// <summary>
    /// A validated read bank remains immutable and sampleable while the other
    /// bank is in <see cref="SimpleDdgiGuidingResourceState.Building"/>.  The
    /// state therefore describes the candidate transaction, not whether the
    /// last published distribution may still be consumed.
    /// </summary>
    public bool HasReadableDistribution =>
        IsEffectivelyEnabled && (ReadBankIndex is 0 or 1) &&
        ReadBankGeneration != 0u && PublishedProbeCount > 0;
}

/// <summary>Token returned after the write bank has been cleared for a build.</summary>
public readonly record struct SimpleDdgiGuidingBuildToken(
    ulong AllocationEpoch,
    int ReadBankIndex,
    int WriteBankIndex,
    uint ExpectedReadBankGeneration,
    uint CandidateBankGeneration,
    uint TargetProposalEpoch,
    uint GuidingAbiVersion,
    int LeafResolution)
{
    public bool IsDefault => AllocationEpoch == 0UL;
}

public readonly record struct SimpleDdgiGuidingBuildBeginResult(
    bool Started,
    SimpleDdgiGuidingBuildToken Token,
    string Reason);

/// <summary>
/// One validated header read back from the candidate write bank.  Entries must
/// be strictly ascending by physical slot; that makes duplicate detection
/// deterministic and allocation-free on the render thread.
/// </summary>
public readonly record struct SimpleDdgiGuidingPublishedProbeHeader(
    uint PhysicalProbeIndex,
    uint ExpectedVirtualProbeId,
    uint ExpectedPageGeneration,
    GPUSimpleDdgiGuidingDistributionHeader Header);

public enum SimpleDdgiGuidingPublicationFailure : byte
{
    None = 0,
    NotEnabled = 1,
    NoBuildInFlight = 2,
    TokenMismatch = 3,
    GpuWorkIncomplete = 4,
    EmptyPublication = 5,
    PhysicalProbeOutOfRange = 6,
    PhysicalProbeNotStrictlyAscending = 7,
    HeaderInvalid = 8,
    LayoutMismatch = 9,
    CandidateGenerationNotNewer = 10,
    CandidateProposalEpochOlder = 11
}

public readonly record struct SimpleDdgiGuidingPublicationResult(
    bool Published,
    SimpleDdgiGuidingPublicationFailure Failure,
    string Reason)
{
    public static SimpleDdgiGuidingPublicationResult Success { get; } =
        new(true, SimpleDdgiGuidingPublicationFailure.None, "published");
}

/// <summary>
/// Transactional lifecycle for C3's two persistent distribution banks.
///
/// The manager does not pretend that a C# status bit makes GPU data safe: a
/// caller must complete train/build/validate commands, read the compact header
/// set, and then call <see cref="CompleteBuild"/>.  Any stale, incomplete, or
/// malformed candidate leaves the last readable bank unchanged.  Disabling the
/// effective mode retires every C3 allocation and returns an exact zero-count
/// graph/descriptors snapshot.
/// </summary>
public sealed class SimpleDdgiGuidingManager : IDisposable
{
    private readonly object _sync = new();
    private ISimpleDdgiGuidingGpuResourceAllocator? _allocator;
    private SimpleDdgiGuidingGpuAllocation? _allocation;
    private SimpleDdgiGuidingLayout _layout;
    private SimpleDdgiGuidingProbeStamp[] _publishedProbeStamps = Array.Empty<SimpleDdgiGuidingProbeStamp>();
    private SimpleDdgiGuidingResourceState _state;
    private ulong _allocationEpoch;
    private uint _nextBankGeneration;
    private uint _readBankGeneration;
    private int _readBankIndex = -1;
    private int _writeBankIndex;
    private int _publishedProbeCount;
    private SimpleDdgiGuidingBuildToken? _pendingBuild;
    private string _reason = "disabled";
    private bool _disposed;

    public SimpleDdgiGuidingManager()
    {
        SimpleDdgiGuidingGpuAbi.VerifyManagedLayout();
    }

    public SimpleDdgiGuidingRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Returns the active native-allocation identity for the Vulkan recording
    /// boundary.  This does not publish a bank to trace or source-cache work:
    /// consumers must still use a header-validated read bank after
    /// <see cref="CompleteBuild"/> succeeds.
    /// </summary>
    public bool TryGetActiveAllocation(
        out SimpleDdgiGuidingGpuAllocation allocation,
        out SimpleDdgiGuidingLayout layout)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null ||
                _state == SimpleDdgiGuidingResourceState.Disabled)
            {
                allocation = null!;
                layout = default;
                return false;
            }

            allocation = _allocation;
            layout = _layout;
            return true;
        }
    }

    /// <summary>
    /// Reconciles a pre-admitted effective mode.  False is a hard zero-resource
    /// transition, even if a requested developer setting remains enabled.
    /// </summary>
    public SimpleDdgiGuidingRuntimeSnapshot Reconcile(
        in SimpleDdgiGuidingRuntimeRequest request,
        ISimpleDdgiGuidingGpuResourceAllocator? allocator)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!request.IsEffectivelyEnabled)
            {
                DisableNoLock("effective-mode-disabled");
                return CreateSnapshotNoLock();
            }

            ValidateActiveLayout(request.Layout);
            if (_allocation is not null && _layout.Equals(request.Layout))
                return CreateSnapshotNoLock();

            if (allocator is null)
            {
                DisableNoLock("guiding-resource-allocator-unavailable");
                return CreateSnapshotNoLock();
            }

            // Allocate the replacement before retiring a compatible old set.
            // If native allocation fails the old set is retired as well so a
            // rejected effective mode cannot retain hidden C3 residency.
            SimpleDdgiGuidingGpuAllocation? replacement = null;
            try
            {
                replacement = allocator.Allocate(request.Layout) ??
                    throw new InvalidOperationException("C3 allocator returned null allocation.");
                replacement.Validate(request.Layout);
            }
            catch (Exception exception)
            {
                if (replacement is not null)
                    allocator.Retire(replacement);
                DisableNoLock("guiding-allocation-rejected:" + exception.GetType().Name);
                return CreateSnapshotNoLock();
            }

            try
            {
                RetireActiveNoLock();
            }
            catch
            {
                allocator.Retire(replacement);
                ClearNoLock("guiding-prior-allocation-retirement-failed");
                throw;
            }
            try
            {
                _publishedProbeStamps = new SimpleDdgiGuidingProbeStamp[
                    request.Layout.PhysicalProbeCapacity];
            }
            catch
            {
                allocator.Retire(replacement);
                ClearNoLock("guiding-cpu-publication-state-allocation-failed");
                throw;
            }

            _allocator = allocator;
            _allocation = replacement;
            _layout = request.Layout;
            _allocationEpoch = NextNonZero(_allocationEpoch);
            _nextBankGeneration = 0u;
            _readBankGeneration = 0u;
            _readBankIndex = -1;
            _writeBankIndex = 0;
            _publishedProbeCount = 0;
            _pendingBuild = null;
            _state = SimpleDdgiGuidingResourceState.ReadyForBuild;
            _reason = "allocated-awaiting-first-build";
            return CreateSnapshotNoLock();
        }
    }

    /// <summary>
    /// Begins a write-bank transaction.  Call this only after clearing the
    /// selected GPU bank and recording the matching train/build commands.
    /// A second build cannot overwrite a candidate awaiting validation.
    /// </summary>
    public SimpleDdgiGuidingBuildBeginResult BeginBuild(uint targetProposalEpoch)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null || _state == SimpleDdgiGuidingResourceState.Disabled)
                return new(false, default, "guiding-not-effectively-enabled");
            if (_pendingBuild.HasValue)
                return new(false, default, "guiding-build-already-in-flight");
            if (targetProposalEpoch == 0u)
                return new(false, default, "guiding-proposal-epoch-missing");

            int writeBank = _readBankIndex == 0 ? 1 : 0;
            uint candidateGeneration = NextNonZero(_nextBankGeneration);
            _nextBankGeneration = candidateGeneration;
            var token = new SimpleDdgiGuidingBuildToken(
                _allocationEpoch,
                _readBankIndex,
                writeBank,
                _readBankGeneration,
                candidateGeneration,
                targetProposalEpoch,
                SimpleDdgiGuidingGpuAbi.Version,
                _layout.LeafResolution);
            _pendingBuild = token;
            _writeBankIndex = writeBank;
            _state = SimpleDdgiGuidingResourceState.Building;
            _reason = "building-write-bank";
            return new(true, token, "started");
        }
    }

    /// <summary>
    /// Atomically flips the readable bank only when every candidate header is
    /// complete, ABI-compatible, owner-compatible, and newer for the same
    /// physical-slot owner.  Failed publication preserves the prior readable
    /// bank and clears the in-flight token.
    /// </summary>
    public SimpleDdgiGuidingPublicationResult CompleteBuild(
        in SimpleDdgiGuidingBuildToken token,
        bool gpuWorkCompleted,
        ReadOnlySpan<SimpleDdgiGuidingPublishedProbeHeader> headers)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_allocation is null || _state == SimpleDdgiGuidingResourceState.Disabled)
                return RejectNoLock(SimpleDdgiGuidingPublicationFailure.NotEnabled,
                    "guiding-not-effectively-enabled");
            if (!_pendingBuild.HasValue)
                return RejectNoLock(SimpleDdgiGuidingPublicationFailure.NoBuildInFlight,
                    "guiding-no-build-in-flight");
            if (!_pendingBuild.Value.Equals(token) ||
                token.AllocationEpoch != _allocationEpoch ||
                token.GuidingAbiVersion != SimpleDdgiGuidingGpuAbi.Version)
            {
                // A late completion from a retired/replaced command buffer
                // must not cancel the current build for this allocation.
                return RejectNoLock(SimpleDdgiGuidingPublicationFailure.TokenMismatch,
                    "guiding-build-token-mismatch", clearPending: false);
            }
            if (!gpuWorkCompleted)
            {
                return RejectNoLock(SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                    "guiding-gpu-work-not-complete");
            }
            if (headers.IsEmpty)
            {
                return RejectNoLock(SimpleDdgiGuidingPublicationFailure.EmptyPublication,
                    "guiding-empty-publication");
            }

            uint previousPhysicalProbe = 0u;
            bool hasPreviousPhysicalProbe = false;
            for (int index = 0; index < headers.Length; index++)
            {
                ref readonly SimpleDdgiGuidingPublishedProbeHeader candidate =
                    ref headers[index];
                if (candidate.PhysicalProbeIndex >= (uint)_publishedProbeStamps.Length)
                {
                    return RejectNoLock(SimpleDdgiGuidingPublicationFailure.PhysicalProbeOutOfRange,
                        "guiding-physical-probe-out-of-range");
                }
                if (hasPreviousPhysicalProbe &&
                    candidate.PhysicalProbeIndex <= previousPhysicalProbe)
                {
                    return RejectNoLock(
                        SimpleDdgiGuidingPublicationFailure.PhysicalProbeNotStrictlyAscending,
                        "guiding-publication-physical-probes-not-strictly-ascending");
                }
                hasPreviousPhysicalProbe = true;
                previousPhysicalProbe = candidate.PhysicalProbeIndex;

                SimpleDdgiGuidingGpuHeaderValidation validation =
                    SimpleDdgiGuidingGpuHeaderValidator.Validate(
                        candidate.Header,
                        candidate.ExpectedVirtualProbeId,
                        candidate.ExpectedPageGeneration,
                        token.TargetProposalEpoch);
                if (!validation.IsValid)
                {
                    return RejectNoLock(SimpleDdgiGuidingPublicationFailure.HeaderInvalid,
                        validation.Reason);
                }
                if (candidate.Header.LeafResolution != (uint)token.LeafResolution)
                {
                    return RejectNoLock(SimpleDdgiGuidingPublicationFailure.LayoutMismatch,
                        "guiding-header-leaf-resolution-layout-mismatch");
                }

                ref readonly SimpleDdgiGuidingProbeStamp prior = ref
                    _publishedProbeStamps[(int)candidate.PhysicalProbeIndex];
                if (prior.IsValid &&
                    prior.VirtualProbeId == candidate.Header.VirtualProbeId &&
                    prior.PageGeneration == candidate.Header.PageGeneration)
                {
                    if (!IsStrictlyNewer(
                            candidate.Header.DistributionGeneration,
                            prior.DistributionGeneration))
                    {
                        return RejectNoLock(
                            SimpleDdgiGuidingPublicationFailure.CandidateGenerationNotNewer,
                            "guiding-probe-distribution-generation-not-newer");
                    }
                    if (!IsNewerOrEqual(
                            candidate.Header.DirectionProposalEpoch,
                            prior.DirectionProposalEpoch))
                    {
                        return RejectNoLock(
                            SimpleDdgiGuidingPublicationFailure.CandidateProposalEpochOlder,
                            "guiding-probe-proposal-epoch-older");
                    }
                }
            }

            // Mutate only after validating every header: partial generation
            // publication is forbidden even if a later probe is malformed.
            for (int index = 0; index < headers.Length; index++)
            {
                ref readonly SimpleDdgiGuidingPublishedProbeHeader candidate =
                    ref headers[index];
                if (!_publishedProbeStamps[(int)candidate.PhysicalProbeIndex].IsValid)
                    _publishedProbeCount++;
                _publishedProbeStamps[(int)candidate.PhysicalProbeIndex] =
                    new SimpleDdgiGuidingProbeStamp(
                        candidate.Header.VirtualProbeId,
                        candidate.Header.PageGeneration,
                        candidate.Header.DistributionGeneration,
                        candidate.Header.DirectionProposalEpoch,
                        true);
            }

            _readBankIndex = token.WriteBankIndex;
            _writeBankIndex = _readBankIndex == 0 ? 1 : 0;
            _readBankGeneration = token.CandidateBankGeneration;
            _pendingBuild = null;
            _state = SimpleDdgiGuidingResourceState.Readable;
            _reason = "published";
            return SimpleDdgiGuidingPublicationResult.Success;
        }
    }

    /// <summary>
    /// Cancels only the matching recorded transaction.  Native command
    /// recording failures must never make a partially written candidate bank
    /// readable, while a late failure from a superseded allocation must not
    /// cancel a newer build.
    /// </summary>
    public bool AbortBuild(
        in SimpleDdgiGuidingBuildToken token,
        string reason = "guiding-build-aborted")
    {
        lock (_sync)
        {
            if (_disposed || !_pendingBuild.HasValue ||
                !_pendingBuild.Value.Equals(token) ||
                token.AllocationEpoch != _allocationEpoch)
            {
                return false;
            }

            _ = RejectNoLock(
                SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                string.IsNullOrWhiteSpace(reason)
                    ? "guiding-build-aborted"
                    : reason.Trim());
            return true;
        }
    }

    /// <summary>
    /// Returns the currently readable bank only for the exact physical-slot
    /// ownership and generation that produced a cached ray.  A caller that
    /// receives false must use the ordinary uniform/canonical path rather than
    /// attempting to reconstruct a PDF from a newer distribution.
    /// </summary>
    public bool TryGetReadableBank(
        uint physicalProbeIndex,
        uint virtualProbeId,
        uint pageGeneration,
        uint distributionGeneration,
        uint proposalEpoch,
        out int readBankIndex)
    {
        lock (_sync)
        {
            readBankIndex = -1;
            if (_allocation is null ||
                _state == SimpleDdgiGuidingResourceState.Disabled ||
                physicalProbeIndex >= (uint)_publishedProbeStamps.Length)
            {
                return false;
            }

            SimpleDdgiGuidingProbeStamp stamp =
                _publishedProbeStamps[(int)physicalProbeIndex];
            if (!stamp.IsValid || stamp.VirtualProbeId != virtualProbeId ||
                stamp.PageGeneration != pageGeneration ||
                stamp.DistributionGeneration != distributionGeneration ||
                stamp.DirectionProposalEpoch != proposalEpoch)
            {
                return false;
            }

            readBankIndex = _readBankIndex;
            return readBankIndex is 0 or 1;
        }
    }

    /// <summary>
    /// Resolves the exact currently readable identity for an owner without
    /// requiring the scheduler to guess a generation. Candidate write-bank
    /// state is never returned.
    /// </summary>
    public bool TryGetReadableProbeIdentity(
        uint physicalProbeIndex,
        uint virtualProbeId,
        uint pageGeneration,
        out SimpleDdgiGuidingReadableProbeIdentity identity)
    {
        lock (_sync)
        {
            identity = default;
            if (_allocation is null || _readBankIndex is < 0 or > 1 ||
                physicalProbeIndex >= (uint)_publishedProbeStamps.Length)
            {
                return false;
            }

            SimpleDdgiGuidingProbeStamp stamp =
                _publishedProbeStamps[(int)physicalProbeIndex];
            if (!stamp.IsValid || stamp.VirtualProbeId != virtualProbeId ||
                stamp.PageGeneration != pageGeneration ||
                stamp.DistributionGeneration == 0u ||
                stamp.DirectionProposalEpoch == 0u)
            {
                return false;
            }

            identity = new(
                physicalProbeIndex,
                virtualProbeId,
                pageGeneration,
                stamp.DistributionGeneration,
                stamp.DirectionProposalEpoch,
                _readBankIndex);
            return true;
        }
    }

    public void Disable(string reason = "disabled")
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableNoLock(reason);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            DisableNoLock("disposed");
            _disposed = true;
        }
    }

    private SimpleDdgiGuidingPublicationResult RejectNoLock(
        SimpleDdgiGuidingPublicationFailure failure,
        string reason,
        bool clearPending = true)
    {
        if (clearPending)
        {
            _pendingBuild = null;
            _state = _readBankIndex is 0 or 1
                ? SimpleDdgiGuidingResourceState.Readable
                : _allocation is null
                    ? SimpleDdgiGuidingResourceState.Disabled
                    : SimpleDdgiGuidingResourceState.ReadyForBuild;
            _reason = reason;
        }
        return new(false, failure, reason);
    }

    private void DisableNoLock(string reason)
    {
        try
        {
            RetireActiveNoLock();
        }
        finally
        {
            ClearNoLock(reason);
        }
    }

    private void RetireActiveNoLock()
    {
        if (_allocation is null)
            return;
        ISimpleDdgiGuidingGpuResourceAllocator? allocator = _allocator;
        SimpleDdgiGuidingGpuAllocation allocation = _allocation;
        _allocation = null;
        _allocator = null;
        if (allocator is not null)
            allocator.Retire(allocation);
    }

    private void ClearNoLock(string reason)
    {
        _layout = default;
        _publishedProbeStamps = Array.Empty<SimpleDdgiGuidingProbeStamp>();
        _state = SimpleDdgiGuidingResourceState.Disabled;
        _readBankIndex = -1;
        _writeBankIndex = 0;
        _readBankGeneration = 0u;
        _nextBankGeneration = 0u;
        _publishedProbeCount = 0;
        _pendingBuild = null;
        _reason = reason;
    }

    private SimpleDdgiGuidingRuntimeSnapshot CreateSnapshotNoLock()
    {
        bool enabled = _allocation is not null &&
            _state != SimpleDdgiGuidingResourceState.Disabled;
        return new SimpleDdgiGuidingRuntimeSnapshot(
            _state,
            enabled,
            _allocationEpoch,
            // The source cache owns slot 203 and the central arena owns the
            // transient workspace addressed by slot 202. Report only the
            // persistent banks this manager actually allocates/retires.
            enabled ? _layout.ManagerOwnedBytes : 0UL,
            enabled ? _allocation!.DescriptorCount : 0u,
            _readBankIndex,
            _writeBankIndex,
            _readBankGeneration,
            _pendingBuild?.CandidateBankGeneration ?? 0u,
            _publishedProbeCount,
            _reason);
    }

    private static void ValidateActiveLayout(in SimpleDdgiGuidingLayout layout)
    {
        if (layout.AbiVersion != SimpleDdgiGuidingLayoutCompiler.AbiVersion ||
            layout.PhysicalProbeCapacity <= 0 ||
            layout.LeafResolution <= 0 ||
            layout.PersistentBankCount != 2 ||
            layout.PersistentDoubleBufferedBytes == 0UL ||
            layout.PersistentBankStrideBytes == 0UL ||
            layout.PersistentBankStrideBytes % sizeof(uint) != 0UL ||
            layout.TrainingScratchBytes == 0UL ||
            (layout.HasTransportSidecar &&
                !layout.TransientWorkspace.IsComplete) ||
            layout.ManagerOwnedBytes == 0UL ||
            layout.TotalBytes != checked(
                layout.PersistentDoubleBufferedBytes +
                layout.ValidationReferenceBankBytes +
                layout.DirectionPdfSidecarBytes +
                layout.TransientWorkspace.TotalBytes) ||
            layout.TotalBytes == 0UL)
        {
            throw new ArgumentException("C3 effective mode requires a complete nonzero layout.",
                nameof(layout));
        }

        if (layout.DirectionSlotsPerProbe < 0 ||
            (layout.DirectionSlotsPerProbe == 0) !=
                (layout.DirectionPayloadCapacity == 0u) ||
            (layout.DirectionSlotsPerProbe == 0) !=
                (layout.DirectionPdfSidecarBytes == 0UL) ||
            layout.DirectionSlotsPerProbe > 0 &&
                (ulong)layout.DirectionPayloadCapacity != checked(
                    (ulong)layout.PhysicalProbeCapacity *
                    (ulong)layout.DirectionSlotsPerProbe) ||
            layout.DirectionPdfSidecarBytes != checked(
                (ulong)layout.DirectionPayloadCapacity *
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount))
        {
            throw new ArgumentException(
                "C3 source-cache sidecar does not match the frozen payload addressing contract.",
                nameof(layout));
        }

        uint leafResolution = checked((uint)layout.LeafResolution);
        if (!SimpleDdgiGuidingGpuAbi.IsSupportedLeafResolution(leafResolution) ||
            layout.HierarchyWeightCount != checked((int)
                SimpleDdgiGuidingGpuAbi.GetHierarchyWeightCount(leafResolution)) ||
            layout.PersistentBankStrideBytes < checked(
                (ulong)SimpleDdgiGuidingGpuAbi.HeaderByteCount +
                (ulong)SimpleDdgiGuidingGpuAbi.GetPackedHierarchyWordCount(
                    leafResolution) * sizeof(uint)))
        {
            throw new ArgumentException("C3 layout does not match the frozen GPU hierarchy ABI.",
                nameof(layout));
        }

        // GLSL array length and every C3 push-constant address are represented
        // as positive uint words. Reject an otherwise arithmetically valid CPU
        // plan before it can allocate a buffer the shaders would wrap or
        // truncate when converting array lengths to uint.
        const ulong MaxShaderArrayWords = int.MaxValue;
        if (layout.PersistentDoubleBufferedBytes % 2UL != 0UL ||
            layout.PersistentBankStrideBytes / sizeof(uint) > MaxShaderArrayWords ||
            layout.PersistentDoubleBufferedBytes / 2UL / sizeof(uint) >
                MaxShaderArrayWords ||
            layout.TrainingScratchBytes % sizeof(uint) != 0UL ||
            layout.TrainingScratchBytes / sizeof(uint) > MaxShaderArrayWords)
        {
            throw new ArgumentException(
                "C3 layout exceeds the uint-word shader-addressing contract.",
                nameof(layout));
        }
    }

    private static uint NextNonZero(uint value)
    {
        uint next = unchecked(value + 1u);
        return next == 0u ? 1u : next;
    }

    private static ulong NextNonZero(ulong value)
    {
        ulong next = unchecked(value + 1UL);
        return next == 0UL ? 1UL : next;
    }

    private static bool IsStrictlyNewer(uint candidate, uint previous)
    {
        uint distance = unchecked(candidate - previous);
        return distance != 0u && distance < 0x8000_0000u;
    }

    private static bool IsNewerOrEqual(uint candidate, uint previous) =>
        candidate == previous || IsStrictlyNewer(candidate, previous);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiGuidingManager));
    }

    private readonly record struct SimpleDdgiGuidingProbeStamp(
        uint VirtualProbeId,
        uint PageGeneration,
        uint DistributionGeneration,
        uint DirectionProposalEpoch,
        bool IsValid);
}
