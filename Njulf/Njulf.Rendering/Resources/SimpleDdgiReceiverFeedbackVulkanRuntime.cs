using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

/// <summary>
/// The only capability outcomes accepted by the B1 Vulkan integration.  The
/// default is deliberately <see cref="ExactCaptureProducerUnavailable"/>:
/// no legacy receiver buffer is reinterpreted or sampled as a 48-byte source.
/// </summary>
public enum SimpleDdgiReceiverFeedbackGpuCapabilityReason : byte
{
    None = 0,
    ExactCaptureProducerUnavailable = 1,
    ExactCaptureProducerContractInvalid = 2,
    GlobalPrerequisiteGateRejected = 3,
    ExactModeNotAdmitted = 4,
    BindlessDescriptorContextUnavailable = 5,
    GpuSortPipelineUnavailable = 6,
    ResourceAllocationFailed = 7,
    CaptureSubmissionRejected = 8,
    HeaderReadbackRejected = 9,
    SchedulerBindingRejected = 10,
    Disposed = 11,
    RequiredProducerCoverageIncomplete = 12
}

/// <summary>
/// A real producer-owned source for B1 capture.  The producer must bind the
/// same <see cref="CandidateBuffer"/> at <see cref="CandidateBufferBindlessIndex"/>
/// before recording B1, and must use exactly the frozen 48-byte candidate
/// record ABI.  This is intentionally not constructible from the legacy
/// 16-byte forward receiver-gather records.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackCaptureProducerContract(
    bool IsAvailable,
    uint GpuSortAbiVersion,
    uint CaptureSourceAbiVersion,
    BufferHandle CandidateBuffer,
    uint CandidateBufferBindlessIndex,
    ulong CandidateBufferDescriptorBytes,
    uint CandidateControlOffsetWords,
    uint CandidateRecordOffsetWords,
    uint CandidateRecordCount,
    uint CandidateRecordStrideBytes,
    uint ScreenSamplingPeriod,
    uint ScreenSamplingPhase,
    uint MaximumUniqueGatherOwnersPerTile,
    PipelineStageFlags2 ProducerWriteStageMask,
    AccessFlags2 ProducerWriteAccessMask,
    uint RequiredProducerMask)
{
    public static SimpleDdgiReceiverFeedbackCaptureProducerContract Unavailable { get; } =
        new(
            IsAvailable: false,
            GpuSortAbiVersion: 0u,
            CaptureSourceAbiVersion: 0u,
            CandidateBuffer: BufferHandle.Invalid,
            CandidateBufferBindlessIndex: 0u,
            CandidateBufferDescriptorBytes: 0UL,
            CandidateControlOffsetWords: 0u,
            CandidateRecordOffsetWords: 0u,
            CandidateRecordCount: 0u,
            CandidateRecordStrideBytes: 0u,
            ScreenSamplingPeriod: 0u,
            ScreenSamplingPhase: 0u,
            MaximumUniqueGatherOwnersPerTile: 0u,
            ProducerWriteStageMask: 0,
            ProducerWriteAccessMask: 0,
            RequiredProducerMask: 0u);

    /// <summary>
    /// Performs only contract arithmetic and ABI checks.  The runtime also
    /// checks the managed buffer's physical size immediately before dispatch.
    /// </summary>
    public bool TryValidate(uint admittedRecordCapacity, out string reason)
    {
        reason = string.Empty;
        if (!IsAvailable)
        {
            reason = "exact-capture-producer-unavailable";
            return false;
        }
        if (GpuSortAbiVersion != SimpleDdgiReceiverFeedbackGpuSortAbi.Version)
        {
            reason = "exact-capture-producer-gpu-sort-abi-version-mismatch";
            return false;
        }
        if (CaptureSourceAbiVersion != SimpleDdgiReceiverFeedbackCaptureSourceAbi.Version)
        {
            reason = "exact-capture-producer-source-abi-version-mismatch";
            return false;
        }
        if (!CandidateBuffer.IsValid ||
            CandidateBufferBindlessIndex !=
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot ||
            CandidateBufferBindlessIndex !=
                (uint)BindlessIndex.SimpleDdgiReceiverFeedbackCandidateBuffer)
        {
            reason = "exact-capture-producer-buffer-or-static-bindless-index-invalid";
            return false;
        }
        if (CandidateBufferBindlessIndex is
            SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot or
            SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot or
            SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot)
        {
            reason = "exact-capture-producer-aliases-b1-output-slot";
            return false;
        }
        if (CandidateRecordStrideBytes !=
            SimpleDdgiReceiverFeedbackGpuSortAbi.CaptureCandidateByteCount)
        {
            reason = "exact-capture-producer-record-stride-is-not-48-bytes";
            return false;
        }
        if (ScreenSamplingPeriod == 0u ||
            ScreenSamplingPhase >= ScreenSamplingPeriod ||
            MaximumUniqueGatherOwnersPerTile == 0u ||
            MaximumUniqueGatherOwnersPerTile >
                SimpleDdgiReceiverFeedbackCaptureSourceAbi
                    .MaximumUniqueGatherOwnersPerTile)
        {
            reason = "exact-capture-producer-screen-sampling-policy-invalid";
            return false;
        }
        if (!SimpleDdgiReceiverFeedbackCaptureSourceAbi.IsValidProducerMask(
                RequiredProducerMask))
        {
            reason = "exact-capture-producer-required-mask-invalid";
            return false;
        }
        if (admittedRecordCapacity == 0u ||
            CandidateRecordCount != admittedRecordCapacity)
        {
            reason = "exact-capture-producer-count-does-not-match-admitted-capacity";
            return false;
        }
        if (CandidateControlOffsetWords <
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords ||
            (CandidateControlOffsetWords &
                (SimpleDdgiReceiverFeedbackCaptureSourceAbi.ControlAlignmentWords - 1u)) != 0u ||
            CandidateRecordOffsetWords < CandidateControlOffsetWords ||
            CandidateRecordOffsetWords - CandidateControlOffsetWords !=
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.ControlWords)
        {
            reason = "exact-capture-producer-control-or-record-offset-invalid";
            return false;
        }
        if (CandidateBufferDescriptorBytes == 0UL ||
            (CandidateBufferDescriptorBytes & (sizeof(uint) - 1UL)) != 0UL ||
            ProducerWriteStageMask == 0 || ProducerWriteAccessMask == 0)
        {
            reason = "exact-capture-producer-descriptor-range-or-write-visibility-invalid";
            return false;
        }
        const AccessFlags2 WriteAccesses =
            AccessFlags2.ShaderStorageWriteBit |
            AccessFlags2.TransferWriteBit |
            AccessFlags2.HostWriteBit |
            AccessFlags2.MemoryWriteBit;
        if ((ProducerWriteAccessMask & WriteAccesses) == 0)
        {
            reason = "exact-capture-producer-access-mask-does-not-name-a-write";
            return false;
        }

        try
        {
            ulong requiredBytes = checked(
                (ulong)CandidateRecordOffsetWords * sizeof(uint) +
                (ulong)CandidateRecordCount *
                    SimpleDdgiReceiverFeedbackGpuSortAbi.CaptureCandidateByteCount);
            if (requiredBytes > CandidateBufferDescriptorBytes)
            {
                reason = "exact-capture-producer-range-exceeds-bound-descriptor";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "exact-capture-producer-range-overflow";
            return false;
        }

        return true;
    }
}

/// <summary>
/// The only scheduler-facing representation of a B1 bank.  It exposes a
/// fixed descriptor slot and offsets only after the resource manager has
/// validated a completed write-bank header; callers cannot bind a live write
/// bank through this API.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuSchedulingBinding(
    bool UseFeedback,
    uint SummaryBufferBindlessSlot,
    ulong SummaryBankOffsetBytes,
    uint SummaryBankStrideWords,
    uint RecordCapacity,
    uint SummaryCapacity,
    uint FallbackCapacity,
    uint FeedbackGeneration,
    uint ViewportGeneration,
    ulong SourceFrameSerial,
    SimpleDdgiReceiverFeedbackBankValidation Validation)
{
    public static SimpleDdgiReceiverFeedbackGpuSchedulingBinding Disabled(
        string detail) => new(
        false,
        SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
        0UL,
        0u,
        0u,
        0u,
        0u,
        0u,
        0u,
        0UL,
        new SimpleDdgiReceiverFeedbackBankValidation(
            false,
            GiExperimentFallbackReason.ResourceIncomplete,
            detail));
}

/// <summary>Inspectable B1 runtime state.  A capability failure always means
/// no B1-owned device buffers are retained for a disabled feature.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics(
    SimpleDdgiReceiverFeedbackGpuCapabilityReason CapabilityReason,
    bool ExactCaptureProducerAvailable,
    bool DescriptorContextRegistered,
    bool HeaderReadbackPending,
    SimpleDdgiReceiverFeedbackGpuResourceSnapshot Resource,
    string Detail)
{
    /// <summary>
    /// Last fence-complete header accepted for the current allocation. This
    /// remains available while the other bank is being captured, but is
    /// cleared on any allocation, generation, readback, or submission fault.
    /// </summary>
    public SimpleDdgiReceiverFeedbackPublicationTelemetry Publication
    {
        get;
        init;
    } = SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty;

    /// <summary>
    /// Highest positive exact receiver-contribution mass selected by the same
    /// validated bank transaction. Null means that the frame carried no
    /// positive measured refinement demand.
    /// </summary>
    public SimpleDdgiReceiverFeedbackRefinementWitness? RefinementWitness
    {
        get;
        init;
    }

    public static SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics Disabled { get; } =
        new(
            SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerUnavailable,
            false,
            false,
            false,
            new SimpleDdgiReceiverFeedbackGpuResourceSnapshot(
                SimpleDdgiReceiverFeedbackGpuResourceState.Disabled,
                false,
                0UL,
                0UL,
                0u,
                -1,
                0u,
                "exact-capture-producer-unavailable"),
            "exact-capture-producer-unavailable");
}

/// <summary>
/// Vulkan ownership and recording boundary for B1.  It is intentionally lazy:
/// constructing or registering the runtime creates no B1 buffers or compute
/// pipelines.  A caller must pass both the global prerequisite gate and a
/// verified 48-byte producer contract before allocation can begin.
/// </summary>
public sealed unsafe class SimpleDdgiReceiverFeedbackVulkanRuntime : IDisposable
{
    /// <summary>
    /// Producer classes whose complete write protocol is currently owned by
    /// this runtime. Expanding this mask requires a real pass hook and a
    /// producer-completion witness; it is not a capability promise.
    /// </summary>
    public static uint OwnedProducerMask { get; } =
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.OpaqueForward) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.Particles) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.Fog) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.ReflectionCapture) |
        SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
            SimpleDdgiReceiverFeedbackProducer.RefinementOrBaseFallback);
    private const ulong HeaderReadbackBytes =
        SimpleDdgiReceiverFeedbackGpuSortAbi.HeaderAndRefinementWitnessByteCount;

    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly Action? _waitForDescriptorReaders;
    private readonly SimpleDdgiReceiverFeedbackGpuResourceManager _resourceManager = new();
    private readonly VulkanAllocator _allocator;
    private readonly PendingReadback?[] _pendingReadbacks =
        new PendingReadback?[RenderingConstants.FramesInFlight];
    private readonly PendingOwnedCapture?[] _pendingOwnedCaptures =
        new PendingOwnedCapture?[RenderingConstants.FramesInFlight];

    private SimpleDdgiReceiverFeedbackGpuPass? _pass;
    private SimpleDdgiReceiverFeedbackGpuSortLayout _activeGpuLayout;
    private GPUSimpleDdgiReceiverFeedbackBankHeaderV2? _publishedHeader;
    private SimpleDdgiReceiverFeedbackRefinementWitness?
        _publishedRefinementWitness;
    private bool _ownedCandidateSource;
    private bool _disposed;

    public SimpleDdgiReceiverFeedbackVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        Action? waitForDescriptorReaders = null)
        : this(context, bufferManager, waitForDescriptorReaders, null)
    {
    }

    internal SimpleDdgiReceiverFeedbackVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        Action? waitForDescriptorReaders,
        AdvancedGiTransientBufferArena? transientBufferArena)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _waitForDescriptorReaders = waitForDescriptorReaders;
        _allocator = new VulkanAllocator(bufferManager, transientBufferArena);
        Diagnostics = SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;
    }

    public SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics Diagnostics { get; private set; }

    /// <summary>
    /// True only after the owned candidate source, exact sort pipelines, and
    /// transactional GPU allocation have all been published.  Consumers use
    /// this to avoid creating the optional producer pipeline while B1 is
    /// disabled or rejected.
    /// </summary>
    public bool IsOwnedCaptureReady
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _ownedCandidateSource && _pass is not null &&
                    _resourceManager.Snapshot.IsEffectivelyEnabled;
            }
        }
    }

    /// <summary>
    /// Returns a recent, fence-complete measured receiver focus. The witness
    /// remains advisory: stale viewport/frame identities fail closed without
    /// disabling the exact GPU scheduler bank.
    /// </summary>
    public bool TryGetPublishedRefinementWitness(
        uint viewportGeneration,
        uint volumeTableGeneration,
        ulong schedulingFrameSerial,
        out SimpleDdgiReceiverFeedbackRefinementWitness witness)
    {
        lock (_sync)
        {
            witness = default;
            if (_disposed || !_publishedHeader.HasValue ||
                !_publishedRefinementWitness.HasValue)
            {
                return false;
            }

            SimpleDdgiReceiverFeedbackRefinementWitness candidate =
                _publishedRefinementWitness.Value;
            if (!candidate.IsValid ||
                candidate.ViewportGeneration != viewportGeneration ||
                candidate.VolumeTableGeneration != volumeTableGeneration ||
                candidate.FeedbackGeneration !=
                    _publishedHeader.Value.FeedbackGeneration ||
                schedulingFrameSerial <= candidate.SourceFrameSerial ||
                schedulingFrameSerial - candidate.SourceFrameSerial >
                    (ulong)RenderingConstants.FramesInFlight + 1UL)
            {
                return false;
            }

            witness = candidate;
            return true;
        }
    }

    /// <summary>
    /// Registers the four append-only B1 slots to a safe existing storage
    /// buffer while inactive.  The fallback is not B1 storage and is never
    /// interpreted by a B1 shader because all B1 recording remains disabled
    /// until an exact allocation is active.
    /// </summary>
    public bool TryRegisterDescriptors(
        BindlessHeap bindlessHeap,
        BufferHandle safeFallbackBuffer,
        ulong safeFallbackBufferBytes,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_allocator.TrySetDescriptorContext(
                    bindlessHeap,
                    safeFallbackBuffer,
                    safeFallbackBufferBytes,
                    out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    exactCaptureProducerAvailable: false);
                return false;
            }

            bool activeAllocation = _resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out _);
            if (activeAllocation)
            {
                SynchronizeDescriptorReadersNoLock();
                if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
                {
                    DisableAtSafeTransitionNoLock(
                        SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                        reason,
                        exactCaptureProducerAvailable: false);
                    return false;
                }
            }
            else if (!_allocator.TryBindFallback(out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    exactCaptureProducerAvailable: false);
                return false;
            }

            UpdateDiagnosticsNoLock(
                activeAllocation
                    ? SimpleDdgiReceiverFeedbackGpuCapabilityReason.None
                    : SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerUnavailable,
                activeAllocation
                    ? "registered-active-b1-descriptors"
                    : "exact-capture-producer-unavailable",
                exactCaptureProducerAvailable: activeAllocation);
            reason = activeAllocation
                ? "registered-active-b1-descriptors"
                : "registered-safe-b1-descriptor-fallbacks";
            return true;
        }
    }

    /// <summary>
    /// Applies an exact B1 plan only at a descriptor-safe transition.  A false
    /// result leaves no B1 allocation active; callers must continue with the
    /// ordinary scheduler path.
    /// </summary>
    public bool TryConfigure(
        in SimpleDdgiReceiverFeedbackPlan plan,
        bool globalPrerequisiteGateAdmitted,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason) => TryConfigureCore(
            plan,
            globalPrerequisiteGateAdmitted,
            producer,
            useOwnedCandidateSource: false,
            out reason);

    /// <summary>
    /// Configures the production B1 path with the candidate source owned by
    /// this runtime. Producers receive only frame-scoped contracts from
    /// <see cref="TryBeginOwnedCapture"/>; no caller can retain or rebind the
    /// fixed candidate descriptor across an allocation transition.
    /// </summary>
    public bool TryConfigureOwned(
        in SimpleDdgiReceiverFeedbackPlan plan,
        bool globalPrerequisiteGateAdmitted,
        out string reason)
    {
        SimpleDdgiReceiverFeedbackCaptureProducerContract unavailable =
            SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
        return TryConfigureCore(
            plan,
            globalPrerequisiteGateAdmitted,
            unavailable,
            useOwnedCandidateSource: true,
            out reason);
    }

    private bool TryConfigureCore(
        in SimpleDdgiReceiverFeedbackPlan plan,
        bool globalPrerequisiteGateAdmitted,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        bool useOwnedCandidateSource,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SynchronizeDescriptorReadersNoLock();
            ClearPendingReadbacksAndPublicationNoLock();
            bool producerAvailable = useOwnedCandidateSource || producer.IsAvailable;

            if (!globalPrerequisiteGateAdmitted)
            {
                reason = "receiver-feedback-global-prerequisite-gate-rejected";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.GlobalPrerequisiteGateRejected,
                    reason,
                    producerAvailable);
                return false;
            }
            if (!plan.UsesExactCompacted)
            {
                reason = "receiver-feedback-exact-mode-not-admitted";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactModeNotAdmitted,
                    reason,
                    producerAvailable);
                return false;
            }
            if (!plan.Layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out string layoutReason))
            {
                reason = "receiver-feedback-gpu-sort-layout-invalid:" + layoutReason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactModeNotAdmitted,
                    reason,
                    producerAvailable);
                return false;
            }
            if (!useOwnedCandidateSource && !producer.IsAvailable)
            {
                reason = "exact-capture-producer-unavailable";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerUnavailable,
                    reason,
                    exactCaptureProducerAvailable: false);
                return false;
            }
            if (!useOwnedCandidateSource &&
                !producer.TryValidate(gpuLayout.RecordCapacity, out string producerReason))
            {
                reason = producerReason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerContractInvalid,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            if (!useOwnedCandidateSource &&
                !TryValidatePhysicalProducerBuffer(producer, out string physicalReason))
            {
                reason = physicalReason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerContractInvalid,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            if (!_allocator.HasDescriptorContext)
            {
                reason = "receiver-feedback-bindless-descriptor-context-unavailable";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            try
            {
                _pass ??= new SimpleDdgiReceiverFeedbackGpuPass(
                    _context,
                    _allocator.BindlessHeap!,
                    _bufferManager);
            }
            catch (Exception exception)
            {
                reason = "receiver-feedback-gpu-sort-pipeline-unavailable:" +
                    exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.GpuSortPipelineUnavailable,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            // A reconfiguration first points the four descriptors at the
            // safe fallback. This prevents a failed replacement transaction
            // from leaving a descriptor that names a destroyed B1 buffer.
            if (!_allocator.TryBindFallback(out string fallbackReason))
            {
                reason = fallbackReason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            SimpleDdgiReceiverFeedbackGpuResourceSnapshot snapshot;
            try
            {
                snapshot = _resourceManager.Configure(plan, _allocator);
            }
            catch (Exception exception)
            {
                reason = "receiver-feedback-resource-configuration-failed:" +
                    exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ResourceAllocationFailed,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            if (!snapshot.IsEffectivelyEnabled ||
                !_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out _))
            {
                reason = snapshot.Reason;
                // Configure deliberately retains an old allocation on a
                // failed replacement. B1's runtime path does not: it has
                // already rebound to the safe descriptor and releases that
                // old allocation immediately at this safe transition.
                _resourceManager.Configure(
                    SimpleDdgiReceiverFeedbackPlan.Disabled(
                        GiExperimentFallbackReason.ResourceIncomplete),
                    _allocator);
                _allocator.TryBindFallback(out _);
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ResourceAllocationFailed,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
            {
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            _activeGpuLayout = gpuLayout;
            _ownedCandidateSource = useOwnedCandidateSource;
            UpdateDiagnosticsNoLock(
                SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
                "allocated-awaiting-exact-capture",
                exactCaptureProducerAvailable: true);
            reason = "allocated-awaiting-exact-capture";
            return true;
        }
    }

    /// <summary>
    /// Opens one frame-scoped producer transaction and initializes its exact
    /// control/range table. The returned contract is valid only for this
    /// command-buffer frame slot and allocation epoch. Producers must finish
    /// all writes before <see cref="TryRecordOwnedReduction"/>.
    /// </summary>
    public bool TryBeginOwnedCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        uint requiredProducerMask,
        out SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason) => TryBeginOwnedCapture(
            commandBuffer,
            frameIndex,
            viewportGeneration,
            frameSerial,
            volumeTableGeneration: 0u,
            requiredProducerMask,
            out producer,
            out reason);

    public bool TryBeginOwnedCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        uint volumeTableGeneration,
        uint requiredProducerMask,
        out SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            producer = SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
            if (commandBuffer.Handle == 0)
            {
                reason = "receiver-feedback-command-buffer-invalid";
                return false;
            }
            if (!_ownedCandidateSource)
            {
                reason = "receiver-feedback-owned-candidate-source-not-configured";
                return false;
            }
            if (!SimpleDdgiReceiverFeedbackCaptureSourceAbi.IsValidProducerMask(
                    requiredProducerMask))
            {
                reason = "receiver-feedback-required-producer-mask-invalid";
                return false;
            }
            uint missingProducerMask = requiredProducerMask & ~OwnedProducerMask;
            if (missingProducerMask != 0u)
            {
                reason = "receiver-feedback-required-producer-coverage-incomplete:0x" +
                    missingProducerMask.ToString(
                        "x8",
                        System.Globalization.CultureInfo.InvariantCulture);
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason
                        .RequiredProducerCoverageIncomplete,
                    reason,
                    exactCaptureProducerAvailable: false);
                return false;
            }
            if (_pendingReadbacks[frameIndex].HasValue ||
                _pendingOwnedCaptures[frameIndex].HasValue)
            {
                reason = "receiver-feedback-frame-slot-still-in-flight";
                return false;
            }
            string layoutReason = string.Empty;
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out NativeAllocation nativeAllocation))
            {
                reason = "receiver-feedback-owned-candidate-allocation-unavailable";
                return false;
            }
            if (!_resourceManager.TryBeginCapture(
                    viewportGeneration,
                    frameSerial,
                    out SimpleDdgiReceiverFeedbackFrameToken token,
                    out reason))
            {
                return false;
            }

            try
            {
                uint controlOffset = layout.CaptureSource.GetFrameControlOffsetWords(
                    frameIndex);
                producer = new SimpleDdgiReceiverFeedbackCaptureProducerContract(
                    IsAvailable: true,
                    GpuSortAbiVersion: SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
                    CaptureSourceAbiVersion:
                        SimpleDdgiReceiverFeedbackCaptureSourceAbi.Version,
                    CandidateBuffer: nativeAllocation.Buffers.CaptureCandidates,
                    CandidateBufferBindlessIndex:
                        SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot,
                    CandidateBufferDescriptorBytes:
                        layout.CaptureSource.RequiredBytes,
                    CandidateControlOffsetWords: controlOffset,
                    CandidateRecordOffsetWords:
                        layout.CaptureSource.GetFrameRecordOffsetWords(frameIndex),
                    CandidateRecordCount: checked((uint)layout.RecordCapacity),
                    CandidateRecordStrideBytes:
                        SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBytes,
                    ScreenSamplingPeriod: layout.ScreenSamplingPeriod,
                    ScreenSamplingPhase: checked((uint)(
                        frameSerial % layout.ScreenSamplingPeriod)),
                    MaximumUniqueGatherOwnersPerTile:
                        layout.MaximumUniqueGatherOwnersPerTile,
                    ProducerWriteStageMask:
                        PipelineStageFlags2.ComputeShaderBit |
                        PipelineStageFlags2.VertexShaderBit |
                        PipelineStageFlags2.FragmentShaderBit |
                        PipelineStageFlags2.TransferBit,
                    ProducerWriteAccessMask:
                        AccessFlags2.ShaderStorageWriteBit |
                        AccessFlags2.TransferWriteBit,
                    RequiredProducerMask: requiredProducerMask);
                if (!producer.TryValidate(
                        checked((uint)layout.RecordCapacity),
                        out string producerReason))
                {
                    throw new InvalidOperationException(producerReason);
                }

                RecordOwnedCaptureControlInitialization(
                    commandBuffer,
                    token,
                    layout,
                    nativeAllocation.Buffers.CaptureCandidates,
                    frameIndex,
                    requiredProducerMask);
                _pendingOwnedCaptures[frameIndex] = new PendingOwnedCapture(
                    allocation.AllocationId,
                    token,
                    producer,
                    RecordedProducerMask: 0u,
                    VolumeTableGeneration: volumeTableGeneration);
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
                    "receiver-feedback-owned-capture-open-for-producers",
                    exactCaptureProducerAvailable: true);
                reason = "receiver-feedback-owned-capture-open-for-producers";
                return true;
            }
            catch (Exception exception)
            {
                _resourceManager.AbortCapture(
                    token,
                    "receiver-feedback-source-initialization-failed:" +
                    exception.GetType().Name);
                producer = SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
                reason = "receiver-feedback-source-initialization-failed:" +
                    exception.GetType().Name;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.CaptureSubmissionRejected,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
        }
    }

    /// <summary>
    /// Backward-compatible single-producer entry point. It is deliberately
    /// limited to the owned sparse opaque reconstruction producer; callers
    /// that can enable layered, volumetric, capture, or refinement receivers
    /// must use the explicit required-mask overload.
    /// </summary>
    public bool TryBeginOwnedCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        out SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason) => TryBeginOwnedCapture(
            commandBuffer,
            frameIndex,
            viewportGeneration,
            frameSerial,
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                SimpleDdgiReceiverFeedbackProducer.OpaqueForward),
            out producer,
            out reason);

    /// <summary>
    /// Closes an owned producer transaction and records the exact compact,
    /// sort, reduce, publication, and diagnostic-header readback sequence.
    /// </summary>
    public bool TryRecordOwnedReduction(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason) => TryRecordOwnedReduction(
            commandBuffer,
            frameIndex,
            producer,
            timestamps: null,
            out reason);

    /// <summary>
    /// Renderer-owned overload that emits bounded per-region GPU timestamps.
    /// The public runtime contract remains independent of the diagnostics
    /// recorder so non-render-graph users do not need to construct one.
    /// </summary>
    internal bool TryRecordOwnedReduction(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        GpuTimestampRecorder? timestamps,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "receiver-feedback-command-buffer-invalid";
                return false;
            }
            PendingOwnedCapture? pendingValue = _pendingOwnedCaptures[frameIndex];
            if (!pendingValue.HasValue || !pendingValue.Value.Producer.Equals(producer))
            {
                reason = "receiver-feedback-owned-capture-token-mismatch";
                return false;
            }
            PendingOwnedCapture pending = pendingValue.Value;
            if (pending.RecordedProducerMask != producer.RequiredProducerMask)
            {
                reason = "receiver-feedback-required-producers-not-complete:required=0x" +
                    producer.RequiredProducerMask.ToString(
                        "x8",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    ",completed=0x" + pending.RecordedProducerMask.ToString(
                        "x8",
                        System.Globalization.CultureInfo.InvariantCulture);
                _pendingOwnedCaptures[frameIndex] = null;
                _resourceManager.AbortCapture(pending.Token, reason);
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason
                        .RequiredProducerCoverageIncomplete,
                    reason,
                    exactCaptureProducerAvailable: false);
                return false;
            }
            string layoutReason = string.Empty;
            SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout = default;
            NativeAllocation nativeAllocation = null!;
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout) ||
                allocation.AllocationId != pending.AllocationId ||
                !layout.TryGetGpuSortLayout(
                    out gpuLayout,
                    out layoutReason) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out nativeAllocation) ||
                _pass is null)
            {
                reason = "receiver-feedback-owned-reduction-resources-invalid" +
                    (string.IsNullOrEmpty(layoutReason) ? string.Empty : ":" + layoutReason);
                _pendingOwnedCaptures[frameIndex] = null;
                _resourceManager.AbortCapture(pending.Token, reason);
                return false;
            }

            try
            {
                _pass.Record(
                    commandBuffer,
                    gpuLayout,
                    pending.Token,
                    nativeAllocation.Buffers,
                    producer,
                    timestamps,
                    frameIndex);
                RecordHeaderReadback(
                    commandBuffer,
                    frameIndex,
                    pending.Token,
                    allocation.AllocationId,
                    gpuLayout,
                    nativeAllocation,
                    pending.VolumeTableGeneration);
                _pendingOwnedCaptures[frameIndex] = null;
                _activeGpuLayout = gpuLayout;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
                    "receiver-feedback-owned-reduction-recorded-awaiting-fence-readback",
                    exactCaptureProducerAvailable: true);
                reason = "receiver-feedback-owned-reduction-recorded-awaiting-fence-readback";
                return true;
            }
            catch (Exception exception)
            {
                _pendingOwnedCaptures[frameIndex] = null;
                _resourceManager.AbortCapture(
                    pending.Token,
                    "receiver-feedback-owned-reduction-recording-failed:" +
                    exception.GetType().Name);
                reason = "receiver-feedback-owned-reduction-recording-failed:" +
                    exception.GetType().Name;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.CaptureSubmissionRejected,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }
        }
    }

    /// <summary>
    /// Records a deterministic completion witness after one producer pass.
    /// The four-byte transfer stamp is ordered after that producer's writes;
    /// final capture also validates the same mask on the GPU. No hot fragment
    /// performs a global completion atomic.
    /// </summary>
    public bool TryRecordOwnedProducerCompletion(
        CommandBuffer commandBuffer,
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer completedProducer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0 || !Enum.IsDefined(completedProducer))
            {
                reason = "receiver-feedback-producer-completion-input-invalid";
                return false;
            }
            PendingOwnedCapture? pendingValue = _pendingOwnedCaptures[frameIndex];
            if (!pendingValue.HasValue)
            {
                reason = "receiver-feedback-owned-capture-not-pending";
                return false;
            }

            PendingOwnedCapture pending = pendingValue.Value;
            uint producerBit =
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    completedProducer);
            if ((pending.Producer.RequiredProducerMask & producerBit) == 0u)
            {
                reason = "receiver-feedback-completed-producer-was-not-required";
                return false;
            }
            if ((pending.RecordedProducerMask & producerBit) != 0u)
            {
                reason = "receiver-feedback-producer-completion-recorded-twice";
                return false;
            }
            if (!_allocator.TryGetNativeAllocation(
                    pending.AllocationId,
                    out NativeAllocation nativeAllocation))
            {
                reason = "receiver-feedback-producer-completion-allocation-unavailable";
                return false;
            }

            uint nextMask = pending.RecordedProducerMask | producerBit;
            ulong completionOffsetBytes = checked(
                ((ulong)pending.Producer.CandidateControlOffsetWords +
                 SimpleDdgiReceiverFeedbackCaptureSourceAbi
                     .CompletedProducerMaskWord) * sizeof(uint));
            VkBuffer buffer = _bufferManager.GetBuffer(
                nativeAllocation.Buffers.CaptureCandidates);
            BufferMemoryBarrier2 beforeStamp = BarrierBuilder.BufferBarrier(
                buffer,
                pending.Producer.ProducerWriteStageMask,
                pending.Producer.ProducerWriteAccessMask,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                completionOffsetBytes,
                sizeof(uint));
            ExecuteBufferBarrier(commandBuffer, beforeStamp);
            Span<uint> stamp = stackalloc uint[1];
            stamp[0] = nextMask;
            _context.Api.CmdUpdateBuffer(
                commandBuffer,
                buffer,
                completionOffsetBytes,
                stamp);
            BufferMemoryBarrier2 afterStamp = BarrierBuilder.BufferBarrier(
                buffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                completionOffsetBytes,
                sizeof(uint));
            ExecuteBufferBarrier(commandBuffer, afterStamp);

            _pendingOwnedCaptures[frameIndex] = pending with
            {
                RecordedProducerMask = nextMask
            };
            reason = "receiver-feedback-producer-completion-recorded";
            return true;
        }
    }

    /// <summary>
    /// Finalizes the frame-scoped owned transaction retained by
    /// <see cref="TryBeginOwnedCapture"/>. This is the renderer's late-frame
    /// boundary after the last enabled producer; no pass needs to retain the
    /// otherwise-private producer token.
    /// </summary>
    public bool TryRecordPendingOwnedReduction(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason) => TryRecordPendingOwnedReduction(
            commandBuffer,
            frameIndex,
            timestamps: null,
            out reason);

    internal bool TryRecordPendingOwnedReduction(
        CommandBuffer commandBuffer,
        int frameIndex,
        GpuTimestampRecorder? timestamps,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        SimpleDdgiReceiverFeedbackCaptureProducerContract producer;
        lock (_sync)
        {
            ThrowIfDisposed();
            PendingOwnedCapture? pending = _pendingOwnedCaptures[frameIndex];
            if (!pending.HasValue)
            {
                reason = "receiver-feedback-owned-capture-not-pending";
                return false;
            }
            producer = pending.Value.Producer;
        }

        return TryRecordOwnedReduction(
            commandBuffer,
            frameIndex,
            producer,
            timestamps,
            out reason);
    }

    public bool HasPendingOwnedCapture(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed && _pendingOwnedCaptures[frameIndex].HasValue;
        }
    }

    /// <summary>
    /// Reports whether the currently open frame transaction requires a named
    /// producer. Passes use this only to select their exact-feedback native
    /// program; the immutable GPU control header remains the shader authority.
    /// </summary>
    public bool IsPendingOwnedProducerRequired(
        int frameIndex,
        SimpleDdgiReceiverFeedbackProducer producer)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (!Enum.IsDefined(producer))
            throw new ArgumentOutOfRangeException(nameof(producer));
        lock (_sync)
        {
            if (_disposed || !_pendingOwnedCaptures[frameIndex].HasValue)
                return false;
            uint producerBit =
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(producer);
            return (_pendingOwnedCaptures[frameIndex]!.Value.Producer
                .RequiredProducerMask & producerBit) != 0u;
        }
    }

    /// <summary>
    /// Records reset, capture, full radix ordering, reductions, finalize, and
    /// the exact 80-byte header/witness prefix copy. It never manufactures a source
    /// from an unrelated receiver path.
    /// </summary>
    public bool TryRecord(
        CommandBuffer commandBuffer,
        int frameIndex,
        uint viewportGeneration,
        ulong frameSerial,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "receiver-feedback-command-buffer-invalid";
                return false;
            }
            if (_pendingReadbacks[frameIndex].HasValue)
            {
                reason = "receiver-feedback-frame-slot-readback-still-pending";
                return false;
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout))
            {
                reason = "receiver-feedback-runtime-not-configured-for-exact-capture";
                return false;
            }
            if (!layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out string layoutReason))
            {
                reason = "receiver-feedback-runtime-gpu-sort-layout-invalid:" + layoutReason;
                return false;
            }
            if (!_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out NativeAllocation nativeAllocation))
            {
                reason = "receiver-feedback-runtime-native-allocation-unavailable";
                return false;
            }
            if (_pass is null)
            {
                reason = "receiver-feedback-runtime-gpu-sort-pipeline-unavailable";
                return false;
            }
            if (!producer.TryValidate(gpuLayout.RecordCapacity, out string producerReason))
            {
                reason = producerReason;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerContractInvalid,
                    reason,
                    exactCaptureProducerAvailable: producer.IsAvailable);
                return false;
            }
            if (!TryValidatePhysicalProducerBuffer(producer, out string physicalReason))
            {
                reason = physicalReason;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.ExactCaptureProducerContractInvalid,
                    reason,
                    exactCaptureProducerAvailable: producer.IsAvailable);
                return false;
            }
            if (!_resourceManager.TryBeginCapture(
                    viewportGeneration,
                    frameSerial,
                    out SimpleDdgiReceiverFeedbackFrameToken token,
                    out reason))
            {
                return false;
            }

            try
            {
                _pass.Record(
                    commandBuffer,
                    gpuLayout,
                    token,
                    nativeAllocation.Buffers,
                    producer);
                RecordHeaderReadback(
                    commandBuffer,
                    frameIndex,
                    token,
                    allocation.AllocationId,
                    gpuLayout,
                    nativeAllocation,
                    volumeTableGeneration: 0u);
            }
            catch (Exception exception)
            {
                _resourceManager.AbortCapture(
                    token,
                    "receiver-feedback-gpu-recording-failed:" + exception.GetType().Name);
                _pendingReadbacks[frameIndex] = null;
                reason = "receiver-feedback-gpu-recording-failed:" + exception.GetType().Name;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.CaptureSubmissionRejected,
                    reason,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            _activeGpuLayout = gpuLayout;
            UpdateDiagnosticsNoLock(
                SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
                "receiver-feedback-capture-recorded-awaiting-fence-readback",
                exactCaptureProducerAvailable: true);
            reason = "receiver-feedback-capture-recorded-awaiting-fence-readback";
            return true;
        }
    }

    /// <summary>
    /// Consumes a fence-complete header and refinement-witness readback. Live scheduling already read
    /// the immediately preceding bank directly on the GPU; this delayed CPU
    /// read validates diagnostics and releases the frame-ring bank for reuse.
    /// A readback is rejected only if its caller is not later than the source
    /// frame, never merely because multiple frames were in flight.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        ulong expectedSchedulingFrameSerial,
        out SimpleDdgiReceiverFeedbackBankValidation validation)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            validation = new SimpleDdgiReceiverFeedbackBankValidation(
                false,
                GiExperimentFallbackReason.ResourceIncomplete,
                "receiver-feedback-no-fence-complete-header-readback");
            PendingReadback? pending = _pendingReadbacks[frameIndex];
            if (!pending.HasValue)
                return false;

            PendingReadback expected = pending.Value;
            _pendingReadbacks[frameIndex] = null;
            if (expectedSchedulingFrameSerial == ulong.MaxValue ||
                expected.Token.FrameSerial == ulong.MaxValue ||
                expectedSchedulingFrameSerial <= expected.Token.FrameSerial)
            {
                _resourceManager.AbortCapture(
                    expected.Token,
                    "receiver-feedback-readback-frame-not-later-than-source");
                validation = new SimpleDdgiReceiverFeedbackBankValidation(
                    false,
                    GiExperimentFallbackReason.GenerationMismatch,
                    "receiver-feedback-readback-frame-not-later-than-source");
                ClearPublishedHeaderNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.HeaderReadbackRejected,
                    validation.Detail,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout) ||
                allocation.AllocationId != expected.AllocationId)
            {
                _resourceManager.AbortCapture(
                    expected.Token,
                    "receiver-feedback-readback-allocation-no-longer-current");
                validation = new SimpleDdgiReceiverFeedbackBankValidation(
                    false,
                    GiExperimentFallbackReason.ResourceIncomplete,
                    "receiver-feedback-readback-allocation-no-longer-current");
                ClearPendingReadbacksAndPublicationNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.HeaderReadbackRejected,
                    validation.Detail,
                    exactCaptureProducerAvailable: true);
                return false;
            }
            string layoutReason = string.Empty;
            if (!layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out layoutReason) ||
                !_allocator.TryGetNativeAllocation(expected.AllocationId,
                    out NativeAllocation nativeAllocation))
            {
                _resourceManager.AbortCapture(
                    expected.Token,
                    "receiver-feedback-readback-layout-or-native-allocation-invalid");
                validation = new SimpleDdgiReceiverFeedbackBankValidation(
                    false,
                    GiExperimentFallbackReason.ResourceIncomplete,
                    "receiver-feedback-readback-layout-or-native-allocation-invalid" +
                    (string.IsNullOrEmpty(layoutReason) ? string.Empty : ":" + layoutReason));
                ClearPendingReadbacksAndPublicationNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.HeaderReadbackRejected,
                    validation.Detail,
                    exactCaptureProducerAvailable: true);
                return false;
            }

            try
            {
                BufferHandle readback = nativeAllocation.ReadbackBuffers[frameIndex];
                _bufferManager.InvalidateBuffer(readback, 0UL, HeaderReadbackBytes);
                byte* mapped = (byte*)_bufferManager.GetMappedPointer(readback);
                GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header =
                    *(GPUSimpleDdgiReceiverFeedbackBankHeaderV2*)mapped;
                GPUSimpleDdgiReceiverFeedbackRefinementWitnessV1 gpuWitness =
                    *(GPUSimpleDdgiReceiverFeedbackRefinementWitnessV1*)(mapped +
                        SimpleDdgiReceiverFeedbackGpuSortAbi.BankHeaderByteCount);
                validation = _resourceManager.CompleteGpuCapture(expected.Token, header);
                if (!validation.UseFeedback)
                {
                    ClearPublishedHeaderNoLock();
                    UpdateDiagnosticsNoLock(
                        SimpleDdgiReceiverFeedbackGpuCapabilityReason.HeaderReadbackRejected,
                        validation.Detail,
                        exactCaptureProducerAvailable: true);
                    return false;
                }

                _activeGpuLayout = gpuLayout;
                _publishedHeader = header;
                _publishedRefinementWitness =
                    SimpleDdgiReceiverFeedbackGpuSortAbi.TryDecodeRefinementWitness(
                        header,
                        gpuWitness,
                        expected.VolumeTableGeneration,
                        out SimpleDdgiReceiverFeedbackRefinementWitness witness)
                    ? witness
                    : null;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.None,
                    "receiver-feedback-previous-bank-published",
                    exactCaptureProducerAvailable: true);
                return true;
            }
            catch (Exception exception)
            {
                _resourceManager.AbortCapture(
                    expected.Token,
                    "receiver-feedback-header-readback-failed:" + exception.GetType().Name);
                validation = new SimpleDdgiReceiverFeedbackBankValidation(
                    false,
                    GiExperimentFallbackReason.FeedbackBankInvalid,
                    "receiver-feedback-header-readback-failed:" + exception.GetType().Name);
                ClearPublishedHeaderNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.HeaderReadbackRejected,
                    validation.Detail,
                    exactCaptureProducerAvailable: true);
                return false;
            }
        }
    }

    /// <summary>
    /// Acquires a CPU-readback-validated immutable bank. This remains useful
    /// for diagnostics and CPU/reference scheduling; the live GPU scheduler
    /// uses <see cref="AcquirePendingForGpuScheduling"/> so it never waits for
    /// a frame-ring readback.
    /// </summary>
    public SimpleDdgiReceiverFeedbackGpuSchedulingBinding AcquireForScheduling(
        uint viewportGeneration,
        ulong frameSerial)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SimpleDdgiReceiverFeedbackScheduleBinding managerBinding =
                _resourceManager.AcquireForScheduling(viewportGeneration, frameSerial);
            if (!managerBinding.UseFeedback || !_publishedHeader.HasValue)
            {
                ClearPublishedHeaderNoLock();
                return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                    managerBinding.Validation.Detail);
            }
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout))
            {
                const string detail = "receiver-feedback-published-bank-native-allocation-unavailable";
                ClearPublishedHeaderNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.SchedulerBindingRejected,
                    detail,
                    exactCaptureProducerAvailable: true);
                return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(detail);
            }
            string layoutReason = string.Empty;
            if (!layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out layoutReason) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out _))
            {
                string detail = "receiver-feedback-published-bank-native-allocation-unavailable" +
                    (string.IsNullOrEmpty(layoutReason) ? string.Empty : ":" + layoutReason);
                ClearPublishedHeaderNoLock();
                UpdateDiagnosticsNoLock(
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason.SchedulerBindingRejected,
                    detail,
                    exactCaptureProducerAvailable: true);
                return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(detail);
            }

            GPUSimpleDdgiReceiverFeedbackBankHeaderV2 header = _publishedHeader.Value;
            ulong offset = checked(
                (ulong)managerBinding.SummaryBankIndex *
                gpuLayout.SummaryBankStrideWords * sizeof(uint));
            return new SimpleDdgiReceiverFeedbackGpuSchedulingBinding(
                true,
                SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
                offset,
                gpuLayout.SummaryBankStrideWords,
                gpuLayout.RecordCapacity,
                gpuLayout.SummaryCapacity,
                gpuLayout.FallbackCapacity,
                managerBinding.FeedbackGeneration,
                header.ViewportGeneration,
                ((ulong)header.FrameSerialHigh << 32) | header.FrameSerialLow,
                managerBinding.Validation);
        }
    }

    /// <summary>
    /// Returns the immediately preceding submitted summary bank without a CPU
    /// readback dependency. The scheduler shader must validate the complete
    /// 64-byte header against every expected identity before reading records;
    /// any mismatch contributes zero priority and leaves ordinary quotas in
    /// control.
    /// </summary>
    public SimpleDdgiReceiverFeedbackGpuSchedulingBinding
        AcquirePendingForGpuScheduling(
            uint viewportGeneration,
            ulong frameSerial)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (viewportGeneration == 0u || frameSerial == 0UL)
            {
                return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                    "receiver-feedback-gpu-scheduler-frame-identity-invalid");
            }
            string layoutReason = string.Empty;
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout) ||
                !layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out layoutReason) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out _))
            {
                return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                    "receiver-feedback-gpu-scheduler-allocation-unavailable" +
                    (string.IsNullOrEmpty(layoutReason)
                        ? string.Empty
                        : ":" + layoutReason));
            }

            for (int index = 0; index < _pendingReadbacks.Length; ++index)
            {
                PendingReadback? pendingValue = _pendingReadbacks[index];
                if (!pendingValue.HasValue)
                    continue;
                PendingReadback pending = pendingValue.Value;
                SimpleDdgiReceiverFeedbackFrameToken token = pending.Token;
                if (pending.AllocationId != allocation.AllocationId ||
                    token.ViewportGeneration != viewportGeneration ||
                    token.FrameSerial == ulong.MaxValue ||
                    token.FrameSerial + 1UL != frameSerial)
                {
                    continue;
                }

                ulong offset = checked(
                    (ulong)token.WriteBankIndex *
                    gpuLayout.SummaryBankStrideWords * sizeof(uint));
                return new SimpleDdgiReceiverFeedbackGpuSchedulingBinding(
                    true,
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
                    offset,
                    gpuLayout.SummaryBankStrideWords,
                    gpuLayout.RecordCapacity,
                    gpuLayout.SummaryCapacity,
                    gpuLayout.FallbackCapacity,
                    token.FeedbackGeneration,
                    token.ViewportGeneration,
                    token.FrameSerial,
                    new SimpleDdgiReceiverFeedbackBankValidation(
                        true,
                        GiExperimentFallbackReason.None,
                        "pending-bank-requires-gpu-header-validation"));
            }

            return SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled(
                "receiver-feedback-immediately-previous-bank-unavailable");
        }
    }

    /// <summary>
    /// Records the cross-submission compute-write to scheduler-read visibility
    /// boundary for the selected immutable bank. Queue submission order alone
    /// provides execution order, not the buffer memory dependency required by
    /// Vulkan's memory model.
    /// </summary>
    public bool TryRecordSchedulingReadBarrier(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackGpuSchedulingBinding binding,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            reason = string.Empty;
            if (!binding.UseFeedback ||
                binding.SummaryBufferBindlessSlot !=
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot)
            {
                reason = "receiver-feedback-scheduler-binding-not-active";
                return false;
            }

            string layoutReason = string.Empty;
            if (!_resourceManager.TryGetActiveAllocation(
                    out SimpleDdgiReceiverFeedbackGpuAllocation allocation,
                    out SimpleDdgiReceiverFeedbackLayout layout) ||
                !layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out layoutReason) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out NativeAllocation nativeAllocation) ||
                binding.RecordCapacity != gpuLayout.RecordCapacity ||
                binding.SummaryCapacity != gpuLayout.SummaryCapacity ||
                binding.FallbackCapacity != gpuLayout.FallbackCapacity ||
                binding.SummaryBankStrideWords !=
                    gpuLayout.SummaryBankStrideWords ||
                binding.SummaryBankOffsetBytes % sizeof(uint) != 0UL ||
                (binding.SummaryBankOffsetBytes != 0UL &&
                 binding.SummaryBankOffsetBytes !=
                    gpuLayout.SummaryBankStrideWords * (ulong)sizeof(uint)))
            {
                reason = "receiver-feedback-scheduler-barrier-layout-invalid" +
                    (string.IsNullOrEmpty(layoutReason)
                        ? string.Empty
                        : ":" + layoutReason);
                return false;
            }

            ulong bankBytes = checked(
                (ulong)gpuLayout.SummaryBankStrideWords * sizeof(uint));
            if (binding.SummaryBankOffsetBytes >
                    gpuLayout.RequiredSummaryBanksBytes ||
                bankBytes > gpuLayout.RequiredSummaryBanksBytes -
                    binding.SummaryBankOffsetBytes)
            {
                reason = "receiver-feedback-scheduler-barrier-range-invalid";
                return false;
            }

            VkBuffer buffer = _bufferManager.GetBuffer(
                nativeAllocation.Buffers.SummaryBanks);
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                buffer,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                binding.SummaryBankOffsetBytes,
                bankBytes);
            ExecuteBufferBarrier(commandBuffer, barrier);
            reason = "receiver-feedback-scheduler-read-barrier-recorded";
            return true;
        }
    }

    /// <summary>
    /// Aborts a recorded-but-unsubmitted capture.  Renderer submission-failure
    /// handling should call this before attempting any recovery submission.
    /// </summary>
    public void AbortCapture(string reason = "receiver-feedback-capture-aborted")
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            Array.Clear(_pendingReadbacks);
            Array.Clear(_pendingOwnedCaptures);
            _resourceManager.AbortCapture(reason);
            ClearPublishedHeaderNoLock();
            UpdateDiagnosticsNoLock(
                SimpleDdgiReceiverFeedbackGpuCapabilityReason.CaptureSubmissionRejected,
                string.IsNullOrWhiteSpace(reason)
                    ? "receiver-feedback-capture-aborted"
                    : reason.Trim(),
                exactCaptureProducerAvailable: true);
        }
    }

    private void RecordOwnedCaptureControlInitialization(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackLayout layout,
        BufferHandle candidateBuffer,
        int frameIndex,
        uint requiredProducerMask)
    {
        SimpleDdgiReceiverFeedbackCaptureSourceLayout sourceLayout =
            layout.CaptureSource;
        GPUSimpleDdgiReceiverFeedbackCaptureGlobalHeader global =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.CreateGlobalHeader(
                sourceLayout);
        GPUSimpleDdgiReceiverFeedbackCaptureControl control =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.CreateControl(
                sourceLayout,
                token.FeedbackGeneration,
                token.ViewportGeneration,
                token.FrameSerial,
                requiredProducerMask);
        control.Flags = SimpleDdgiReceiverFeedbackCaptureSourceAbi.ReadyForCaptureFlag;

        Span<uint> globalWords = stackalloc uint[checked((int)
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords)];
        globalWords.Clear();
        globalWords[0] = global.AbiVersion;
        globalWords[1] = global.LayoutRevision;
        globalWords[2] = global.FrameCount;
        globalWords[3] = global.FrameStrideWords;
        globalWords[4] = global.GlobalHeaderWords;
        globalWords[5] = global.ControlWords;
        globalWords[6] = global.CandidateWords;
        globalWords[7] = global.RecordCapacity;
        globalWords[8] = global.RequiredBytesLow;
        globalWords[9] = global.RequiredBytesHigh;
        globalWords[10] = global.Flags;
        globalWords[11] = global.EndianSentinel;

        Span<uint> words = stackalloc uint[
            checked((int)SimpleDdgiReceiverFeedbackCaptureSourceAbi.ControlWords)];
        words.Clear();
        words[0] = control.AbiVersion;
        words[1] = control.LayoutRevision;
        words[2] = control.FeedbackGeneration;
        words[3] = control.ViewportGeneration;
        words[4] = control.FrameSerialLow;
        words[5] = control.FrameSerialHigh;
        words[6] = control.RecordCapacity;
        words[7] = control.ProducerCount;
        words[8] = control.SharedOverflowBaseRecord;
        words[9] = control.SharedOverflowCapacity;
        words[10] = control.SharedOverflowCount;
        words[11] = control.ProducerOverflowMask;
        words[12] = control.TotalReservedRecordCount;
        words[13] = control.Flags;
        words[14] = control.EndianSentinel;
        words[15] = control.RequiredProducerMask;
        words[checked((int)SimpleDdgiReceiverFeedbackCaptureSourceAbi
            .CompletedProducerMaskWord)] = 0u;
        words[checked((int)SimpleDdgiReceiverFeedbackCaptureSourceAbi
            .ScreenSamplingPeriodWord)] = checked((uint)layout.ScreenSamplingPeriod);
        words[checked((int)SimpleDdgiReceiverFeedbackCaptureSourceAbi
            .ScreenSamplingPhaseWord)] = checked((uint)(token.FrameSerial %
                layout.ScreenSamplingPeriod));
        words[checked((int)SimpleDdgiReceiverFeedbackCaptureSourceAbi
            .MaximumUniqueGatherOwnersWord)] = checked((uint)
                layout.MaximumUniqueGatherOwnersPerTile);

        for (uint ordinal = 0u;
             ordinal < SimpleDdgiReceiverFeedbackCaptureSourceAbi.ProducerCount;
             ++ordinal)
        {
            SimpleDdgiReceiverFeedbackCaptureProducerRange range =
                sourceLayout.GetProducerRange(
                    (SimpleDdgiReceiverFeedbackProducer)ordinal);
            int baseWord = checked((int)(
                SimpleDdgiReceiverFeedbackCaptureSourceAbi.HeaderWords +
                ordinal *
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.ProducerRangeWords));
            words[baseWord + 0] = range.BaseRecord;
            words[baseWord + 1] = range.Capacity;
            words[baseWord + 2] = 0u;
            words[baseWord + 3] = 0u;
        }

        ulong frameOffsetBytes = checked(
            (ulong)sourceLayout.GetFrameControlOffsetWords(frameIndex) * sizeof(uint));
        ulong frameBytes = checked((ulong)sourceLayout.FrameStrideWords * sizeof(uint));
        VkBuffer nativeBuffer = _bufferManager.GetBuffer(candidateBuffer);
        ulong globalHeaderBytes = checked(
            (ulong)SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords *
            sizeof(uint));
        // The immutable lookup header is rewritten with identical data so a
        // newly allocated descriptor becomes self-describing before any
        // producer shader runs. The narrow barriers make this safe even when
        // older frame-ring slices are still resident in the same buffer.
        BufferMemoryBarrier2 beforeGlobalHeader = BarrierBuilder.BufferBarrier(
            nativeBuffer,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.TransferBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            globalHeaderBytes);
        ExecuteBufferBarrier(commandBuffer, beforeGlobalHeader);
        _context.Api.CmdUpdateBuffer(
            commandBuffer,
            nativeBuffer,
            0UL,
            globalWords);
        BufferMemoryBarrier2 afterGlobalHeader = BarrierBuilder.BufferBarrier(
            nativeBuffer,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            0UL,
            globalHeaderBytes);
        ExecuteBufferBarrier(commandBuffer, afterGlobalHeader);
        _context.Api.CmdUpdateBuffer(
            commandBuffer,
            nativeBuffer,
            frameOffsetBytes,
            words);

        BufferMemoryBarrier2 producerBarrier = BarrierBuilder.BufferBarrier(
            nativeBuffer,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            frameOffsetBytes,
            frameBytes);
        ExecuteBufferBarrier(commandBuffer, producerBarrier);
    }

    private void RecordHeaderReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        ulong allocationId,
        in SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        NativeAllocation nativeAllocation,
        uint volumeTableGeneration)
    {
        BufferHandle readbackHandle = nativeAllocation.ReadbackBuffers[frameIndex];
        if (!readbackHandle.IsValid)
            throw new InvalidOperationException("B1 summary header readback buffer is unavailable.");

        ulong summaryOffset = checked(
            (ulong)token.WriteBankIndex * gpuLayout.SummaryBankStrideWords * sizeof(uint));
        VkBuffer source = _bufferManager.GetBuffer(nativeAllocation.Buffers.SummaryBanks);
        VkBuffer destination = _bufferManager.GetBuffer(readbackHandle);
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            summaryOffset,
            HeaderReadbackBytes);
        ExecuteBufferBarrier(commandBuffer, beforeCopy);

        var copy = new BufferCopy
        {
            SrcOffset = summaryOffset,
            DstOffset = 0UL,
            Size = HeaderReadbackBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            source,
            destination,
            1u,
            &copy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            HeaderReadbackBytes);
        ExecuteBufferBarrier(commandBuffer, afterCopy);
        _pendingReadbacks[frameIndex] = new PendingReadback(
            allocationId,
            token,
            volumeTableGeneration);
    }

    private bool TryValidatePhysicalProducerBuffer(
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        out string reason)
    {
        try
        {
            ulong physicalBytes = _bufferManager.GetBufferSize(producer.CandidateBuffer);
            if (physicalBytes < producer.CandidateBufferDescriptorBytes)
            {
                reason = "exact-capture-producer-bound-descriptor-exceeds-buffer-allocation";
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "exact-capture-producer-buffer-is-not-live:" + exception.GetType().Name;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void DisableAtSafeTransitionNoLock(
        SimpleDdgiReceiverFeedbackGpuCapabilityReason capabilityReason,
        string detail,
        bool exactCaptureProducerAvailable)
    {
        ClearPendingReadbacksAndPublicationNoLock();
        _allocator.TryBindFallback(out _);
        _resourceManager.Configure(
            SimpleDdgiReceiverFeedbackPlan.Disabled(
                GiExperimentFallbackReason.ResourceIncomplete),
            _allocator);
        _activeGpuLayout = default;
        _ownedCandidateSource = false;
        _pass?.Dispose();
        _pass = null;
        UpdateDiagnosticsNoLock(
            capabilityReason,
            detail,
            exactCaptureProducerAvailable);
    }

    private void ClearPublishedHeaderNoLock()
    {
        _publishedHeader = null;
        _publishedRefinementWitness = null;
    }

    private void ClearPendingReadbacksAndPublicationNoLock()
    {
        Array.Clear(_pendingReadbacks);
        Array.Clear(_pendingOwnedCaptures);
        ClearPublishedHeaderNoLock();
    }

    private void SynchronizeDescriptorReadersNoLock() =>
        _waitForDescriptorReaders?.Invoke();

    private void UpdateDiagnosticsNoLock(
        SimpleDdgiReceiverFeedbackGpuCapabilityReason capabilityReason,
        string detail,
        bool exactCaptureProducerAvailable)
    {
        Diagnostics = new SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics(
            capabilityReason,
            exactCaptureProducerAvailable,
            _allocator.HasDescriptorContext,
            HasPendingReadbackNoLock(),
            _resourceManager.Snapshot,
            string.IsNullOrWhiteSpace(detail) ? "unknown" : detail.Trim())
        {
            Publication = _publishedHeader.HasValue
                ? SimpleDdgiReceiverFeedbackPublicationTelemetry
                    .FromValidatedHeader(_publishedHeader.Value)
                : SimpleDdgiReceiverFeedbackPublicationTelemetry.Empty,
            RefinementWitness = _publishedRefinementWitness
        };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            // Renderer teardown calls this after DeviceWaitIdle.  Keep the
            // fallback rebinding best-effort so the resource manager can still
            // retire exact buffers if a partially initialized renderer is
            // being disposed.
            _allocator.TryBindFallback(out _);
            _resourceManager.Dispose();
            _pass?.Dispose();
            _pass = null;
            _allocator.Dispose();
            ClearPendingReadbacksAndPublicationNoLock();
            _activeGpuLayout = default;
            _ownedCandidateSource = false;
            Diagnostics = new SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics(
                SimpleDdgiReceiverFeedbackGpuCapabilityReason.Disposed,
                false,
                false,
                false,
                _resourceManager.Snapshot,
                "disposed");
        }
    }

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiReceiverFeedbackVulkanRuntime));
    }

    private bool HasPendingReadbackNoLock()
    {
        foreach (PendingReadback? pending in _pendingReadbacks)
        {
            if (pending.HasValue)
                return true;
        }
        return false;
    }

    private readonly record struct PendingReadback(
        ulong AllocationId,
        SimpleDdgiReceiverFeedbackFrameToken Token,
        uint VolumeTableGeneration);

    private readonly record struct PendingOwnedCapture(
        ulong AllocationId,
        SimpleDdgiReceiverFeedbackFrameToken Token,
        SimpleDdgiReceiverFeedbackCaptureProducerContract Producer,
        uint RecordedProducerMask,
        uint VolumeTableGeneration);

    private sealed class VulkanAllocator : ISimpleDdgiReceiverFeedbackGpuResourceAllocator,
        IDisposable
    {
        private readonly BufferManager _bufferManager;
        private readonly AdvancedGiTransientBufferArena? _transientBufferArena;
        private readonly Dictionary<ulong, NativeAllocation> _allocations = new();
        private BindlessHeap? _bindlessHeap;
        private BufferHandle _fallbackBuffer = BufferHandle.Invalid;
        private ulong _fallbackBytes;
        private ulong _nextAllocationId;
        private bool _disposed;

        public VulkanAllocator(
            BufferManager bufferManager,
            AdvancedGiTransientBufferArena? transientBufferArena)
        {
            _bufferManager = bufferManager;
            _transientBufferArena = transientBufferArena;
        }

        public bool HasDescriptorContext =>
            !_disposed && _bindlessHeap is not null && _fallbackBuffer.IsValid &&
            _fallbackBytes >= sizeof(uint) * 4UL;

        public BindlessHeap? BindlessHeap => _bindlessHeap;

        public bool TrySetDescriptorContext(
            BindlessHeap bindlessHeap,
            BufferHandle fallbackBuffer,
            ulong fallbackBytes,
            out string reason)
        {
            if (_disposed)
            {
                reason = "receiver-feedback-vulkan-allocator-disposed";
                return false;
            }
            if (!fallbackBuffer.IsValid || fallbackBytes < sizeof(uint) * 4UL)
            {
                reason = "receiver-feedback-safe-descriptor-fallback-invalid";
                return false;
            }
            try
            {
                if (_bufferManager.GetBufferSize(fallbackBuffer) < fallbackBytes)
                {
                    reason = "receiver-feedback-safe-descriptor-fallback-range-exceeds-buffer";
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = "receiver-feedback-safe-descriptor-fallback-not-live:" +
                    exception.GetType().Name;
                return false;
            }

            _bindlessHeap = bindlessHeap;
            _fallbackBuffer = fallbackBuffer;
            _fallbackBytes = fallbackBytes;
            return TryBindFallback(out reason);
        }

        public SimpleDdgiReceiverFeedbackGpuAllocation Allocate(
            in SimpleDdgiReceiverFeedbackLayout layout)
        {
            ThrowIfDisposed();
            if (!layout.TryGetGpuSortLayout(
                    out SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
                    out string reason))
            {
                throw new ArgumentException(
                    "B1 exact allocation requires the versioned GPU sort layout: " + reason,
                    nameof(layout));
            }

            BufferHandle recordBanks = BufferHandle.Invalid;
            BufferHandle sortScratch = BufferHandle.Invalid;
            ulong sortScratchOffset = 0UL;
            bool ownsSortScratch = true;
            BufferHandle summaryBanks = BufferHandle.Invalid;
            BufferHandle captureCandidates = BufferHandle.Invalid;
            var readbacks = new BufferHandle[RenderingConstants.FramesInFlight];
            try
            {
                recordBanks = _bufferManager.CreateDeviceBuffer(
                    gpuLayout.RequiredRecordBanksBytes,
                    BufferUsageFlags.StorageBufferBit,
                    requireDeviceAddress: false,
                    Njulf.Rendering.Diagnostics.MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI Receiver Feedback Record Banks");
                if (_transientBufferArena is not null)
                {
                    if (!_transientBufferArena.TryGetSlice(
                            SimpleDdgiAdvancedMemoryCategory
                                .ReceiverFeedbackSortScratch,
                            gpuLayout.RequiredSortScratchBytes,
                            sizeof(uint),
                            out AdvancedGiTransientBufferSlice arenaSlice,
                            out string arenaFailure))
                    {
                        throw new InvalidOperationException(arenaFailure);
                    }
                    sortScratch = arenaSlice.Buffer;
                    sortScratchOffset = arenaSlice.Offset;
                    ownsSortScratch = false;
                }
                else
                {
                    sortScratch = _bufferManager.CreateDeviceBuffer(
                        gpuLayout.RequiredSortScratchBytes,
                        BufferUsageFlags.StorageBufferBit,
                        requireDeviceAddress: false,
                        Njulf.Rendering.Diagnostics.MemoryBudgetCategory.GlobalIllumination,
                        "Simple DDGI Receiver Feedback Sort Scratch");
                }
                summaryBanks = _bufferManager.CreateDeviceBuffer(
                    gpuLayout.RequiredSummaryBanksBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    Njulf.Rendering.Diagnostics.MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI Receiver Feedback Summary Banks");
                captureCandidates = _bufferManager.CreateDeviceBuffer(
                    layout.CaptureSource.RequiredBytes,
                    BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.TransferDstBit |
                        BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    Njulf.Rendering.Diagnostics.MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI Receiver Feedback Exact Candidate Source");
                for (int frameIndex = 0; frameIndex < readbacks.Length; ++frameIndex)
                {
                    readbacks[frameIndex] = _bufferManager.CreateBuffer(
                        HeaderReadbackBytes,
                        BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessRandomBit,
                        $"Simple DDGI Receiver Feedback Header Readback Frame {frameIndex}",
                        Njulf.Rendering.Diagnostics.MemoryBudgetCategory.GlobalIllumination);
                }

                ulong allocationId = NextAllocationId();
                var buffers = new SimpleDdgiReceiverFeedbackVulkanBuffers(
                    recordBanks,
                    sortScratch,
                    summaryBanks,
                    captureCandidates,
                    sortScratchOffset,
                    gpuLayout.RequiredSortScratchBytes);
                var native = new NativeAllocation(
                    buffers,
                    readbacks,
                    ownsSortScratch);
                _allocations.Add(allocationId, native);
                return new SimpleDdgiReceiverFeedbackGpuAllocation(
                    allocationId,
                    new SimpleDdgiReceiverFeedbackGpuBuffer(
                        _bufferManager.GetBuffer(recordBanks).Handle,
                        gpuLayout.RequiredRecordBanksBytes),
                    new SimpleDdgiReceiverFeedbackGpuBuffer(
                        _bufferManager.GetBuffer(sortScratch).Handle,
                        gpuLayout.RequiredSortScratchBytes,
                        sortScratchOffset),
                    new SimpleDdgiReceiverFeedbackGpuBuffer(
                        _bufferManager.GetBuffer(summaryBanks).Handle,
                        gpuLayout.RequiredSummaryBanksBytes),
                    new SimpleDdgiReceiverFeedbackGpuBuffer(
                        _bufferManager.GetBuffer(captureCandidates).Handle,
                        layout.CaptureSource.RequiredBytes),
                    DescriptorCount: 4u);
            }
            catch
            {
                Destroy(recordBanks);
                if (ownsSortScratch)
                    Destroy(sortScratch);
                Destroy(summaryBanks);
                Destroy(captureCandidates);
                foreach (BufferHandle readback in readbacks)
                    Destroy(readback);
                throw;
            }
        }

        public void Retire(SimpleDdgiReceiverFeedbackGpuAllocation allocation)
        {
            if (!_allocations.Remove(allocation.AllocationId, out NativeAllocation? native))
                return;
            Destroy(native.Buffers.RecordBanks);
            if (native.OwnsSortScratch)
                Destroy(native.Buffers.SortScratch);
            Destroy(native.Buffers.SummaryBanks);
            Destroy(native.Buffers.CaptureCandidates);
            foreach (BufferHandle readback in native.ReadbackBuffers)
                Destroy(readback);
        }

        public bool TryGetNativeAllocation(
            ulong allocationId,
            out NativeAllocation allocation)
        {
            if (_allocations.TryGetValue(allocationId, out NativeAllocation? native))
            {
                allocation = native;
                return true;
            }

            allocation = null!;
            return false;
        }

        public bool TryBindAllocation(ulong allocationId, out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "receiver-feedback-bindless-descriptor-context-unavailable";
                return false;
            }
            if (!_allocations.TryGetValue(allocationId, out NativeAllocation? native))
            {
                reason = "receiver-feedback-native-allocation-not-found";
                return false;
            }

            try
            {
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot,
                    native.Buffers.RecordBanks,
                    _bufferManager.GetBufferSize(native.Buffers.RecordBanks));
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot,
                    native.Buffers.SortScratch,
                    native.Buffers.SortScratchOffset,
                    native.Buffers.SortScratchBytes);
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
                    native.Buffers.SummaryBanks,
                    _bufferManager.GetBufferSize(native.Buffers.SummaryBanks));
                Register(
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot,
                    native.Buffers.CaptureCandidates,
                    _bufferManager.GetBufferSize(native.Buffers.CaptureCandidates));
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "receiver-feedback-b1-descriptor-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }

        public bool TryBindFallback(out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "receiver-feedback-safe-descriptor-fallback-unavailable";
                return false;
            }
            try
            {
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot,
                    _fallbackBuffer,
                    _fallbackBytes);
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot,
                    _fallbackBuffer,
                    _fallbackBytes);
                Register(
                    SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot,
                    _fallbackBuffer,
                    _fallbackBytes);
                Register(
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot,
                    _fallbackBuffer,
                    _fallbackBytes);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "receiver-feedback-safe-descriptor-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (NativeAllocation allocation in _allocations.Values)
            {
                Destroy(allocation.Buffers.RecordBanks);
                if (allocation.OwnsSortScratch)
                    Destroy(allocation.Buffers.SortScratch);
                Destroy(allocation.Buffers.SummaryBanks);
                Destroy(allocation.Buffers.CaptureCandidates);
                foreach (BufferHandle readback in allocation.ReadbackBuffers)
                    Destroy(readback);
            }
            _allocations.Clear();
            _bindlessHeap = null;
            _fallbackBuffer = BufferHandle.Invalid;
            _fallbackBytes = 0UL;
        }

        private void Register(
            uint slot,
            BufferHandle buffer,
            ulong bytes) => Register(slot, buffer, 0UL, bytes);

        private void Register(
            uint slot,
            BufferHandle buffer,
            ulong offset,
            ulong bytes)
        {
            if (slot > int.MaxValue || !buffer.IsValid || bytes == 0UL ||
                offset > _bufferManager.GetBufferSize(buffer) ||
                bytes > _bufferManager.GetBufferSize(buffer) - offset)
                throw new InvalidOperationException("B1 bindless descriptor arguments are invalid.");
            _bindlessHeap!.RegisterStorageBuffer(
                (int)slot,
                _bufferManager.GetBuffer(buffer),
                offset,
                bytes);
        }

        private ulong NextAllocationId()
        {
            do
            {
                _nextAllocationId = _nextAllocationId == ulong.MaxValue
                    ? 1UL
                    : _nextAllocationId + 1UL;
            }
            while (_allocations.ContainsKey(_nextAllocationId));
            return _nextAllocationId;
        }

        private void Destroy(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VulkanAllocator));
        }
    }
}

/// <summary>Native B1 storage owned by one resource-manager allocation epoch.</summary>
internal readonly record struct SimpleDdgiReceiverFeedbackVulkanBuffers(
    BufferHandle RecordBanks,
    BufferHandle SortScratch,
    BufferHandle SummaryBanks,
    BufferHandle CaptureCandidates,
    ulong SortScratchOffset = 0UL,
    ulong SortScratchBytes = 0UL)
{
    public bool IsComplete =>
        RecordBanks.IsValid &&
        SortScratch.IsValid &&
        SummaryBanks.IsValid &&
        CaptureCandidates.IsValid;
}

internal sealed class NativeAllocation
{
    public NativeAllocation(
        SimpleDdgiReceiverFeedbackVulkanBuffers buffers,
        BufferHandle[] readbackBuffers,
        bool ownsSortScratch = true)
    {
        Buffers = buffers;
        ReadbackBuffers = readbackBuffers;
        OwnsSortScratch = ownsSortScratch;
    }

    public SimpleDdgiReceiverFeedbackVulkanBuffers Buffers { get; }
    public BufferHandle[] ReadbackBuffers { get; }
    public bool OwnsSortScratch { get; }
}
