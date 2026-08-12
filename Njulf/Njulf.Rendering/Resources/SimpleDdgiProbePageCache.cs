using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources;

internal readonly record struct SimpleDdgiProbePageTransactionPolicy(
    int RetentionFrames,
    int MaximumAdmissionsPerFrame,
    int MaximumReceiverFeedbackRequests,
    int InactiveRetryFrames)
{
    public SimpleDdgiProbePageTransactionPolicy Validate(
        int physicalPageCapacity)
    {
        if (RetentionFrames < 1 || RetentionFrames > 3_600)
            throw new ArgumentOutOfRangeException(nameof(RetentionFrames));
        if (MaximumAdmissionsPerFrame < 1 ||
            MaximumAdmissionsPerFrame > physicalPageCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumAdmissionsPerFrame));
        }
        if (MaximumReceiverFeedbackRequests < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReceiverFeedbackRequests));
        }
        if (InactiveRetryFrames < 1 || InactiveRetryFrames > 36_000)
            throw new ArgumentOutOfRangeException(nameof(InactiveRetryFrames));
        return this;
    }
}

/// <summary>
/// Owns the Simple-DDGI residency transaction: one fixed-capacity GPU arena,
/// one bounded delayed feedback slot per frame in flight, and completion-token
/// retirement for replaced generations. Page selection and mapping remain GPU
/// authority; this class never builds or reads back a page list.
/// </summary>
public sealed unsafe class SimpleDdgiProbePageCache : IDisposable
{
    private const int RetirementCapacity = 32;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly object _lock = new();
    private readonly BufferHandle[] _feedbackReadbackBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly bool[] _feedbackRecorded =
        new bool[RenderingConstants.FramesInFlight];
    private readonly ulong[] _feedbackSubmittedFrameSerial =
        new ulong[RenderingConstants.FramesInFlight];
    private readonly uint[] _feedbackSubmittedResourceGeneration =
        new uint[RenderingConstants.FramesInFlight];
    private readonly GpuCompletionRetirementQueue _retirement =
        new(RetirementCapacity);
    private readonly GpuRetirementRecord[] _retirementScratch =
        new GpuRetirementRecord[RetirementCapacity];

    private BufferHandle _arenaBuffer;
    private SimpleDdgiProbePageLayout? _layout;
    private SimpleDdgiProbeResidencyMode _mode =
        SimpleDdgiProbeResidencyMode.Dense;
    private BindlessHeap? _registeredBindlessHeap;
    private BufferHandle _placeholderBuffer;
    private ulong _placeholderBytes;
    private uint _resourceGeneration = 1u;
    private ulong _topologyFingerprint;
    private SimpleDdgiProbePageTransactionPolicy _transactionPolicy;
    private bool _bootstrapRequired;
    private bool _runtimeFrozen;
    private bool _developmentFrozen;
    private bool _residencyValid;
    private bool _forceReplacement;
    private string _failureReason = string.Empty;
    private GPUSimpleDdgiPageDevelopmentControl _pendingDevelopmentControl;
    private bool _developmentControlPending;
    private uint _developmentControlSerial;
    private ulong _developmentControlCommandCount;
    private int _lastDevelopmentControlledVirtualPage = -1;
    private bool _lastDevelopmentPinState;
    private ulong _staleFeedbackCount;
    private bool _disposed;

    public SimpleDdgiProbePageCache(
        VulkanContext context,
        BufferManager bufferManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
    }

    public BufferHandle ArenaBuffer => _arenaBuffer;
    public SimpleDdgiProbePageLayout? Layout => _layout;
    public SimpleDdgiProbeResidencyMode Mode => _mode;
    public uint ResourceGeneration => _resourceGeneration;
    public ulong TopologyFingerprint => _topologyFingerprint;
    public ulong ArenaBytes => _layout?.TotalBytes ?? 0UL;
    public ulong FeedbackReadbackBytes => CountFeedbackReadbackBytes();
    public ulong RetiredBytes => _retirement.ActiveBytes;
    public bool IsReady => _arenaBuffer.IsValid && _layout != null;
    public bool BootstrapRequired => _bootstrapRequired;
    public bool Frozen => _runtimeFrozen || _developmentFrozen;
    public bool RuntimeFrozen => _runtimeFrozen;
    public bool DevelopmentFrozen => _developmentFrozen;
    public bool ResidencyValid => _residencyValid;
    public string FailureReason => !string.IsNullOrWhiteSpace(_failureReason)
        ? _failureReason
        : _developmentFrozen
            ? "development-residency-freeze"
            : string.Empty;
    public ulong DevelopmentControlCommandCount =>
        _developmentControlCommandCount;
    public int LastDevelopmentControlledVirtualPage =>
        _lastDevelopmentControlledVirtualPage;
    public bool LastDevelopmentPinState => _lastDevelopmentPinState;
    public ulong StaleFeedbackCount => _staleFeedbackCount;

    public Silk.NET.Vulkan.Buffer GetArenaVkBuffer()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_arenaBuffer.IsValid)
                return default;
            return _bufferManager.GetBuffer(_arenaBuffer);
        }
    }

    public ulong GetInitializationIndirectOffset() =>
        _layout?.IndirectCommandsOffset ?? 0UL;

    /// <summary>
    /// Binds a graph-safe existing buffer when paging is inactive. The
    /// placeholder is descriptor-only and is therefore not charged as another
    /// residency allocation.
    /// </summary>
    public void Register(
        BindlessHeap bindlessHeap,
        BufferHandle placeholderBuffer,
        ulong placeholderBytes)
    {
        if (bindlessHeap == null)
            throw new ArgumentNullException(nameof(bindlessHeap));
        if (!placeholderBuffer.IsValid)
            throw new ArgumentException(
                "A valid graph-safe placeholder buffer is required.",
                nameof(placeholderBuffer));

        lock (_lock)
        {
            ThrowIfDisposed();
            _registeredBindlessHeap = bindlessHeap;
            _placeholderBuffer = placeholderBuffer;
            _placeholderBytes = Math.Max(
                SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes,
                placeholderBytes);
            RegisterArenaOrPlaceholder();
        }
    }

    /// <summary>
    /// Reports whether the next capacity request will publish a different
    /// arena (or the dense placeholder) into the shared bindless descriptor.
    /// The owner uses this before recording the replacement so every submitted
    /// reader can be completed under Vulkan's update-after-bind rules.
    /// </summary>
    public bool RequiresReplacement(
        SimpleDdgiProbeResidencyMode mode,
        int virtualPageCount,
        int sparsePhysicalPageCapacity,
        int maximumAdmissionsPerFrame,
        int retentionFrames,
        int maximumReceiverFeedbackRequests,
        int inactiveRetryFrames,
        ulong topologyFingerprint)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            SimpleDdgiProbeResidencyMode resolvedMode = mode.Sanitize();
            if (!resolvedMode.CollectsDemand() || virtualPageCount <= 0)
                return IsReady || _mode != resolvedMode;

            var transactionPolicy =
                new SimpleDdgiProbePageTransactionPolicy(
                    retentionFrames,
                    maximumAdmissionsPerFrame,
                    maximumReceiverFeedbackRequests,
                    inactiveRetryFrames).Validate(
                        sparsePhysicalPageCapacity);
            return _forceReplacement ||
                _layout == null ||
                !_arenaBuffer.IsValid ||
                RequiresTopologyReplacement(
                    _topologyFingerprint,
                    topologyFingerprint) ||
                _mode != resolvedMode ||
                _layout.VirtualPageCount != virtualPageCount ||
                _layout.SparsePhysicalPageCapacity !=
                    sparsePhysicalPageCapacity ||
                _layout.MaximumAdmissionsPerFrame !=
                    maximumAdmissionsPerFrame ||
                _transactionPolicy != transactionPolicy;
        }
    }

    /// <summary>
    /// Resolves an exact immutable arena capacity. Stable calls are O(1) and do
    /// not query Vulkan allocation sizes or touch descriptors.
    /// </summary>
    public bool EnsureCapacity(
        SimpleDdgiProbeResidencyMode mode,
        int virtualPageCount,
        int sparsePhysicalPageCapacity,
        int maximumAdmissionsPerFrame,
        int retentionFrames,
        int maximumReceiverFeedbackRequests,
        int inactiveRetryFrames,
        ulong topologyFingerprint,
        CommandBuffer commandBuffer,
        ulong lastUseFrameFenceValue,
        ulong maxStorageBufferRange = ulong.MaxValue)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            SimpleDdgiProbeResidencyMode resolvedMode = mode.Sanitize();
            if (!resolvedMode.CollectsDemand() || virtualPageCount <= 0)
            {
                bool changed = IsReady || _mode != resolvedMode;
                _mode = resolvedMode;
                ReleaseArena(lastUseFrameFenceValue, force: false);
                ReleaseFeedbackReadbacks(lastUseFrameFenceValue, force: false);
                _bootstrapRequired = false;
                _runtimeFrozen = false;
                _developmentFrozen = false;
                _residencyValid = false;
                _failureReason = string.Empty;
                _developmentControlPending = false;
                _pendingDevelopmentControl = default;
                _lastDevelopmentControlledVirtualPage = -1;
                _lastDevelopmentPinState = false;
                _forceReplacement = false;
                _topologyFingerprint = 0UL;
                _transactionPolicy = default;
                RegisterArenaOrPlaceholder();
                return changed;
            }

            var transactionPolicy =
                new SimpleDdgiProbePageTransactionPolicy(
                    retentionFrames,
                    maximumAdmissionsPerFrame,
                    maximumReceiverFeedbackRequests,
                    inactiveRetryFrames).Validate(
                        sparsePhysicalPageCapacity);

            if (commandBuffer.Handle == 0)
            {
                throw new ArgumentException(
                    "A valid command buffer is required to initialize a residency arena.",
                    nameof(commandBuffer));
            }

            if (!_forceReplacement &&
                _layout != null && _arenaBuffer.IsValid &&
                !RequiresTopologyReplacement(
                    _topologyFingerprint,
                    topologyFingerprint) &&
                _mode == resolvedMode &&
                _layout.VirtualPageCount == virtualPageCount &&
                _layout.SparsePhysicalPageCapacity ==
                    sparsePhysicalPageCapacity &&
                _layout.MaximumAdmissionsPerFrame == maximumAdmissionsPerFrame &&
                _transactionPolicy == transactionPolicy)
            {
                return false;
            }

            SimpleDdgiProbePageLayout nextLayout =
                SimpleDdgiProbePageLayout.Create(
                    virtualPageCount,
                    sparsePhysicalPageCapacity,
                    maximumAdmissionsPerFrame,
                    maxStorageBufferRange);
            BufferHandle nextArena = _bufferManager.CreateDeviceBuffer(
                nextLayout.TotalBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.IndirectBufferBit |
                BufferUsageFlags.TransferSrcBit |
                BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Simple DDGI Probe Residency Arena");

            // Undefined allocation contents must never become a valid mapping.
            // Zeroing the complete arena publishes only fail-closed table and
            // reverse entries until the bootstrap header is uploaded.
            Silk.NET.Vulkan.Buffer nextVkBuffer =
                _bufferManager.GetBuffer(nextArena);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                nextVkBuffer,
                0,
                nextLayout.TotalBytes,
                0u);
            BufferMemoryBarrier2 clearBarrier = BarrierBuilder.BufferBarrier(
                nextVkBuffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.TransferBit |
                PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.TransferWriteBit |
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
                0,
                nextLayout.TotalBytes);
            ExecuteBufferBarrier(commandBuffer, clearBarrier);

            BufferHandle previousArena = _arenaBuffer;
            ulong previousBytes = _layout?.TotalBytes ?? 0UL;
            _arenaBuffer = nextArena;
            _layout = nextLayout;
            _mode = resolvedMode;
            _topologyFingerprint = topologyFingerprint;
            _transactionPolicy = transactionPolicy;
            _resourceGeneration = NextGeneration(_resourceGeneration);
            _bootstrapRequired = true;
            _runtimeFrozen = false;
            _residencyValid = false;
            _failureReason = string.Empty;
            _developmentControlPending = false;
            _pendingDevelopmentControl = default;
            _lastDevelopmentControlledVirtualPage = -1;
            _lastDevelopmentPinState = false;
            _forceReplacement = false;

            if (previousArena.IsValid)
            {
                RetireBuffer(
                    previousArena,
                    previousBytes,
                    lastUseFrameFenceValue);
            }
            EnsureFeedbackReadbacks();
            RegisterArenaOrPlaceholder();
            return true;
        }
    }

    internal static bool RequiresTopologyReplacement(
        ulong residentTopologyFingerprint,
        ulong requestedTopologyFingerprint) =>
        residentTopologyFingerprint == 0UL ||
        requestedTopologyFingerprint == 0UL ||
        residentTopologyFingerprint != requestedTopologyFingerprint;

    /// <summary>
    /// Uploads only immutable transaction identity and the fixed per-volume
    /// paging records after a new arena generation has been cleared.
    /// </summary>
    public void UploadBootstrap(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        in GPUSimpleDdgiResidencyHeader header,
        ReadOnlySpan<GPUSimpleDdgiVolumePaging> volumePaging)
    {
        if (stagingRing == null)
            throw new ArgumentNullException(nameof(stagingRing));
        if (commandBuffer.Handle == 0)
            throw new ArgumentException(
                "A valid command buffer is required.",
                nameof(commandBuffer));

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_bootstrapRequired || !IsReady || _layout == null)
                return;
            if (header.ResidencyResourceGeneration != _resourceGeneration)
            {
                throw new InvalidOperationException(
                    "Residency bootstrap generation does not match the live arena.");
            }
            if (volumePaging.Length >
                GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            {
                throw new ArgumentException(
                    "Residency bootstrap exceeds the fixed volume paging table.",
                    nameof(volumePaging));
            }

            UploadValue(
                stagingRing,
                commandBuffer,
                header,
                _layout.HeaderOffset);
            if (!volumePaging.IsEmpty)
            {
                GpuBufferUploader.UploadSpanToBuffer(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    _arenaBuffer,
                    volumePaging,
                    _layout.VolumePagingOffset,
                    UploadBarrier());
            }
            _bootstrapRequired = false;
            _residencyValid = true;
        }
    }

    public void FreezeForRuntimeFailure(
        bool residencyStateValid,
        string reason)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _runtimeFrozen = true;
            _residencyValid &= residencyStateValid;
            _failureReason = string.IsNullOrWhiteSpace(reason)
                ? "runtime-residency-failure"
                : reason;
        }
    }

    /// <summary>
    /// Explicit development-only mutation freeze. Unlike runtime failure this
    /// can be released in-place and never changes mapping validity.
    /// </summary>
    public void SetDevelopmentMutationFrozen(bool frozen)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _developmentFrozen = frozen;
        }
    }

    /// <summary>
    /// Queues one explicit transaction-local pin/unpin command. The command is
    /// consumed by GPU classification and does not provide a CPU page list or
    /// influence ordinary shipping demand.
    /// </summary>
    public bool TryQueueDevelopmentPagePin(
        int virtualPageIndex,
        bool pinned)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!IsReady || _layout == null || !_mode.CollectsDemand() ||
                _runtimeFrozen || virtualPageIndex < 0 ||
                virtualPageIndex >= _layout.VirtualPageCount)
            {
                return false;
            }

            _developmentControlSerial = NextGeneration(
                _developmentControlSerial);
            _pendingDevelopmentControl =
                new GPUSimpleDdgiPageDevelopmentControl
                {
                    CommandSerial = _developmentControlSerial,
                    VirtualPagePlusOne = checked((uint)virtualPageIndex + 1u),
                    Flags = SimpleDdgiProbePageLayout
                        .DevelopmentControlValidFlag |
                        (pinned
                            ? SimpleDdgiProbePageLayout
                                .DevelopmentControlPinFlag
                            : 0u)
                };
            _developmentControlPending = true;
            _developmentControlCommandCount = checked(
                _developmentControlCommandCount + 1UL);
            _lastDevelopmentControlledVirtualPage = virtualPageIndex;
            _lastDevelopmentPinState = pinned;
            return true;
        }
    }

    /// <summary>
    /// Records the pending development command before the serial residency
    /// segment. Stable frames with no command perform no upload.
    /// </summary>
    public bool UploadPendingDevelopmentControl(
        StagingRing stagingRing,
        CommandBuffer commandBuffer)
    {
        if (stagingRing == null)
            throw new ArgumentNullException(nameof(stagingRing));
        if (commandBuffer.Handle == 0)
            throw new ArgumentException(
                "A valid command buffer is required.",
                nameof(commandBuffer));

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_developmentControlPending || !IsReady || _layout == null ||
                _bootstrapRequired)
            {
                return false;
            }

            ulong destinationOffset = checked(
                _layout.DemandCountersOffset +
                (ulong)SimpleDdgiProbePageLayout
                    .DevelopmentControlCounterWord * sizeof(uint));
            UploadValue(
                stagingRing,
                commandBuffer,
                _pendingDevelopmentControl,
                destinationOffset);
            _developmentControlPending = false;
            return true;
        }
    }

    /// <summary>
    /// Re-entry never resumes mutation in an old transaction. The next
    /// capacity call must install and bootstrap a new resource generation.
    /// </summary>
    public void RequireFreshTransactionForReentry()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _runtimeFrozen = false;
            _residencyValid = false;
            _failureReason = string.Empty;
            if (IsReady)
            {
                _forceReplacement = true;
                _bootstrapRequired = true;
            }
        }
    }

    /// <summary>
    /// Forces the next capacity resolution to install a cleared arena before
    /// the packed demand epoch is reused. This is a normal serialized resource
    /// transaction and does not alter runtime-failure state.
    /// </summary>
    public void RequireFreshTransactionForDemandEpochWrap()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (IsReady)
            {
                _forceReplacement = true;
                _bootstrapRequired = true;
                _residencyValid = false;
            }
        }
    }

    public bool RecordFeedbackReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong frameSerial)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!IsReady || _layout == null || _bootstrapRequired ||
                _feedbackRecorded[frameIndex])
            {
                return false;
            }

            EnsureFeedbackReadbacks();
            Silk.NET.Vulkan.Buffer source =
                _bufferManager.GetBuffer(_arenaBuffer);
            Silk.NET.Vulkan.Buffer destination = _bufferManager.GetBuffer(
                _feedbackReadbackBuffers[frameIndex]);
            ulong bytes = SimpleDdgiProbePageLayout.FeedbackSummaryBytes;
            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                _layout.FeedbackOffset,
                bytes);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);
            var copy = new BufferCopy(
                _layout.FeedbackOffset,
                0,
                bytes);
            _context.Api.CmdCopyBuffer(
                commandBuffer,
                source,
                destination,
                1,
                &copy);
            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                bytes);
            ExecuteBufferBarrier(commandBuffer, afterCopy);
            _feedbackRecorded[frameIndex] = true;
            _feedbackSubmittedFrameSerial[frameIndex] = frameSerial;
            _feedbackSubmittedResourceGeneration[frameIndex] =
                _resourceGeneration;
            return true;
        }
    }

    public bool TryReadCompletedFeedback(
        int frameIndex,
        ulong completedFrameSerial,
        out GPUSimpleDdgiResidencyFeedback feedback)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_lock)
        {
            ThrowIfDisposed();
            feedback = default;
            if (!_feedbackRecorded[frameIndex] ||
                !_feedbackReadbackBuffers[frameIndex].IsValid ||
                completedFrameSerial <=
                    _feedbackSubmittedFrameSerial[frameIndex])
            {
                return false;
            }

            BufferHandle readback = _feedbackReadbackBuffers[frameIndex];
            _bufferManager.InvalidateBuffer(
                readback,
                0,
                SimpleDdgiProbePageLayout.FeedbackSummaryBytes);
            feedback = *(GPUSimpleDdgiResidencyFeedback*)
                _bufferManager.GetMappedPointer(readback);
            ulong expectedSerial =
                _feedbackSubmittedFrameSerial[frameIndex];
            uint expectedGeneration =
                _feedbackSubmittedResourceGeneration[frameIndex];
            _feedbackRecorded[frameIndex] = false;
            bool valid =
                feedback.FrameSerialLow == (uint)expectedSerial &&
                feedback.FrameSerialHigh == (uint)(expectedSerial >> 32) &&
                feedback.ResidencyResourceGeneration ==
                    expectedGeneration &&
                feedback.ResidencyResourceGeneration ==
                    _resourceGeneration;
            if (!valid)
            {
                _staleFeedbackCount++;
                feedback = default;
                return false;
            }
            return true;
        }
    }

    public void CollectRetired(
        ulong completedFrameFenceValue,
        bool force = false)
    {
        lock (_lock)
        {
            int count = force
                ? _retirement.DrainAfterExternalDeviceIdle(_retirementScratch)
                : _retirement.Poll(
                    new GpuCompletionProgress(
                        completedFrameFenceValue,
                        0UL,
                        0UL),
                    _retirementScratch,
                    completedFrameFenceValue);
            for (int index = 0; index < count; index++)
            {
                GpuRetirementRecord retired = _retirementScratch[index];
                if (retired.Resource.Kind !=
                    GpuRetirementResourceKind.Buffer)
                {
                    throw new InvalidOperationException(
                        "Simple-DDGI residency retirement contained a non-buffer record.");
                }
                BufferHandle buffer = UnpackBufferHandle(
                    retired.Resource.Handle);
                if (buffer.IsValid)
                    _bufferManager.DestroyBuffer(buffer);
                _retirementScratch[index] = default;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_arenaBuffer.IsValid)
                _bufferManager.DestroyBuffer(_arenaBuffer);
            _arenaBuffer = BufferHandle.Invalid;
            _layout = null;
            for (int index = 0;
                index < _feedbackReadbackBuffers.Length;
                index++)
            {
                if (_feedbackReadbackBuffers[index].IsValid)
                {
                    _bufferManager.DestroyBuffer(
                        _feedbackReadbackBuffers[index]);
                }
                _feedbackReadbackBuffers[index] = BufferHandle.Invalid;
                _feedbackRecorded[index] = false;
            }
            int retiredCount = _retirement.DrainAfterExternalDeviceIdle(
                _retirementScratch);
            for (int index = 0; index < retiredCount; index++)
            {
                BufferHandle retired = UnpackBufferHandle(
                    _retirementScratch[index].Resource.Handle);
                if (retired.IsValid)
                    _bufferManager.DestroyBuffer(retired);
                _retirementScratch[index] = default;
            }
        }
    }

    private void UploadValue<T>(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        in T value,
        ulong destinationOffset)
        where T : unmanaged
    {
        GpuBufferUploader.UploadValueToBuffer(
            _context,
            _bufferManager,
            stagingRing,
            commandBuffer,
            _arenaBuffer,
            value,
            destinationOffset,
            UploadBarrier());
    }

    private static UploadBarrierDescription UploadBarrier() => new(
        PipelineStageFlags2.ComputeShaderBit |
        PipelineStageFlags2.FragmentShaderBit,
        AccessFlags2.ShaderStorageReadBit |
        AccessFlags2.ShaderStorageWriteBit);

    private void EnsureFeedbackReadbacks()
    {
        for (int frameIndex = 0;
            frameIndex < _feedbackReadbackBuffers.Length;
            frameIndex++)
        {
            if (_feedbackReadbackBuffers[frameIndex].IsValid)
                continue;
            _feedbackReadbackBuffers[frameIndex] =
                _bufferManager.CreateBuffer(
                    SimpleDdgiProbePageLayout.FeedbackSummaryBytes,
                    BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit |
                        AllocationCreateFlags.HostAccessRandomBit,
                    $"Simple DDGI Residency Feedback Frame {frameIndex}",
                    MemoryBudgetCategory.GlobalIllumination);
        }
    }

    private ulong CountFeedbackReadbackBytes()
    {
        ulong bytes = 0UL;
        for (int index = 0; index < _feedbackReadbackBuffers.Length; index++)
        {
            if (_feedbackReadbackBuffers[index].IsValid)
            {
                bytes = checked(
                    bytes +
                    SimpleDdgiProbePageLayout.FeedbackSummaryBytes);
            }
        }
        return bytes;
    }

    private void ReleaseArena(ulong completionFenceValue, bool force)
    {
        if (!_arenaBuffer.IsValid)
            return;
        BufferHandle arena = _arenaBuffer;
        ulong bytes = _layout?.TotalBytes ?? 0UL;
        _arenaBuffer = BufferHandle.Invalid;
        _layout = null;
        if (force)
            _bufferManager.DestroyBuffer(arena);
        else
            RetireBuffer(arena, bytes, completionFenceValue);
    }

    private void ReleaseFeedbackReadbacks(
        ulong completionFenceValue,
        bool force)
    {
        for (int frameIndex = 0;
            frameIndex < _feedbackReadbackBuffers.Length;
            frameIndex++)
        {
            BufferHandle readback = _feedbackReadbackBuffers[frameIndex];
            _feedbackReadbackBuffers[frameIndex] = BufferHandle.Invalid;
            _feedbackRecorded[frameIndex] = false;
            _feedbackSubmittedFrameSerial[frameIndex] = 0UL;
            _feedbackSubmittedResourceGeneration[frameIndex] = 0u;
            if (!readback.IsValid)
                continue;
            if (force)
                _bufferManager.DestroyBuffer(readback);
            else
            {
                RetireBuffer(
                    readback,
                    SimpleDdgiProbePageLayout.FeedbackSummaryBytes,
                    completionFenceValue);
            }
        }
    }

    private void RetireBuffer(
        BufferHandle buffer,
        ulong bytes,
        ulong completionFenceValue)
    {
        if (!buffer.IsValid)
            return;
        if (completionFenceValue == 0UL)
        {
            _bufferManager.DestroyBuffer(buffer);
            return;
        }

        GpuRetirementRecord record = new(
            ResourceGeneration: buffer.Generation,
            ByteCharge: bytes,
            EnqueuedFrame: completionFenceValue,
            Completion: GpuCompletionToken.ForFrameFence(
                completionFenceValue),
            Resource: new GpuRetirementResource(
                GpuRetirementResourceKind.Buffer,
                PackBufferHandle(buffer)));
        if (!_retirement.TryEnqueue(
                record,
                liveBytes: 0UL,
                out GpuRetirementAdmissionFailure failure))
        {
            throw new InvalidOperationException(
                $"Simple-DDGI residency retirement admission failed: {failure}.");
        }
    }

    private void RegisterArenaOrPlaceholder()
    {
        if (_registeredBindlessHeap == null)
            return;
        BufferHandle handle = _arenaBuffer.IsValid
            ? _arenaBuffer
            : _placeholderBuffer;
        ulong bytes = _arenaBuffer.IsValid && _layout != null
            ? _layout.TotalBytes
            : _placeholderBytes;
        if (!handle.IsValid || bytes == 0UL)
            return;
        _registeredBindlessHeap.RegisterStorageBuffer(
            BindlessIndex.SimpleDdgiResidencyArenaBuffer,
            _bufferManager.GetBuffer(handle),
            0,
            bytes);
    }

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private static uint NextGeneration(uint generation) =>
        generation == uint.MaxValue ? 1u : generation + 1u;

    private static ulong PackBufferHandle(BufferHandle buffer) =>
        ((ulong)buffer.Generation << 32) |
        unchecked((uint)buffer.Index);

    private static BufferHandle UnpackBufferHandle(ulong packed) => new(
        unchecked((int)(uint)packed),
        (uint)(packed >> 32));

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiProbePageCache));
    }
}
