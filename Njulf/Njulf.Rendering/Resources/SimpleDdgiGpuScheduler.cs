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
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiSchedulerCommitFailureBreakdown(
    uint Transaction,
    uint PublicGeneration,
    uint SourceEpoch,
    uint PayloadIdentity,
    uint CachePrecondition,
    uint CacheAddress,
    uint CacheClassification,
    uint CacheGeneration,
    uint CacheEpochOrCardinality,
    uint ReceiverPack,
    uint TransactionPredicateMask,
    uint MissingCompletionMask,
    uint ProducerFailureMask,
    uint CacheReadFailureMask)
{
    public ulong Total =>
        (ulong)Transaction + PublicGeneration + SourceEpoch + PayloadIdentity +
        CachePrecondition + CacheAddress + CacheClassification +
        CacheGeneration + CacheEpochOrCardinality + ReceiverPack;

    public override string ToString() =>
        $"transaction={Transaction},publicGeneration={PublicGeneration}," +
        $"sourceEpoch={SourceEpoch},payloadIdentity={PayloadIdentity}," +
        $"cachePrecondition={CachePrecondition},cacheAddress={CacheAddress}," +
        $"cacheClassification={CacheClassification}," +
        $"cacheGeneration={CacheGeneration}," +
        $"cacheEpochOrCardinality={CacheEpochOrCardinality}," +
        $"receiverPack={ReceiverPack}," +
        $"transactionPredicateMask=0x{TransactionPredicateMask:x}," +
        $"missingCompletionMask=0x{MissingCompletionMask:x}," +
        $"producerFailureMask=0x{ProducerFailureMask:x}," +
        $"cacheReadFailureMask=0x{CacheReadFailureMask:x}";
}

/// <summary>
/// Owns the resident Simple-DDGI scheduler arena and its delayed, bounded
/// feedback channel.  The class intentionally contains no CPU queue or
/// per-probe authority; those remain in <see cref="SimpleDdgiVolumeManager"/>
/// only while <see cref="SimpleDdgiSchedulerMode.CpuReference"/> is active.
/// </summary>
public sealed unsafe class SimpleDdgiGpuScheduler : IDisposable
{
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
    private readonly BufferHandle[] _auditReadbackBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly bool[] _auditReadbackRecorded =
        new bool[RenderingConstants.FramesInFlight];
    private readonly ulong[] _auditSubmittedFrameSerial =
        new ulong[RenderingConstants.FramesInFlight];
    private readonly uint[] _auditSubmittedResourceGeneration =
        new uint[RenderingConstants.FramesInFlight];
    private readonly uint[] _auditSubmittedEpoch =
        new uint[RenderingConstants.FramesInFlight];
    private const int RetirementCapacity = 512;
    private readonly GpuCompletionRetirementQueue _retirement =
        new(RetirementCapacity);
    private readonly GpuRetirementRecord[] _retirementScratch =
        new GpuRetirementRecord[RetirementCapacity];
    private BufferHandle _fallbackExportReadbackBuffer;
    private ulong _fallbackExportReadbackBytes;
    private bool _fallbackExportRecorded;
    private GpuCompletionToken _fallbackExportCompletion;
    private SimpleDdgiSchedulerStateExportTag _fallbackExportTag;
    private ulong _fallbackExportPublicStateOffset;

    private BufferHandle _arenaBuffer;
    private SimpleDdgiGpuSchedulerLayout? _layout;
    private BindlessHeap? _registeredBindlessHeap;
    private SimpleDdgiSchedulerMode _mode = SimpleDdgiSchedulerMode.CpuReference;
    private uint _resourceGeneration = 1;
    private ulong _staleFeedbackCount;
    private readonly uint[] _lastFeedbackLaneCursors =
        new uint[SimpleDdgiSchedulerAbi.MaxLaneCount];
    private bool _hasLastFeedbackLaneCursors;
    private SimpleDdgiSchedulerCommitFailureBreakdown
        _lastCommitFailureBreakdown;
    private uint _lastActiveSourceMutationCount;
    private uint _lastActiveCanonicalMutationCount;
    private ulong _currentPolicyHash;
    private ulong _previousPolicyHash;
    private bool _policiesInitialized;
    private bool _disposed;

    public SimpleDdgiGpuScheduler(VulkanContext context, BufferManager bufferManager)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
    }

    public SimpleDdgiSchedulerMode Mode
    {
        get
        {
            lock (_lock)
                return _mode;
        }
    }

    public bool IsGpuMode => Mode.IsGpuMode();
    public bool IsReady => _arenaBuffer.IsValid && _layout != null;
    public BufferHandle ArenaBuffer => _arenaBuffer;
    public SimpleDdgiGpuSchedulerLayout? Layout => _layout;
    public uint ResourceGeneration => _resourceGeneration;
    public ulong ArenaBytes => _layout?.TotalBytes ?? 0UL;
    public ulong FeedbackReadbackBytes =>
        CountValidFeedbackReadbackBytes();
    public ulong AuditReadbackBytes =>
        CountValidAuditReadbackBytes();
    public ulong FallbackStateExportBytes => _fallbackExportReadbackBytes;
    public ulong RetiredBytes => _retirement.ActiveBytes;
    public ulong StaleFeedbackCount => _staleFeedbackCount;
    public SimpleDdgiSchedulerCommitFailureBreakdown
        LastCommitFailureBreakdown
    {
        get
        {
            lock (_lock)
                return _lastCommitFailureBreakdown;
        }
    }
    public uint LastActiveSourceMutationCount
    {
        get
        {
            lock (_lock)
                return _lastActiveSourceMutationCount;
        }
    }
    public uint LastActiveCanonicalMutationCount
    {
        get
        {
            lock (_lock)
                return _lastActiveCanonicalMutationCount;
        }
    }

    public static ulong ResolveFallbackStateExportBytes(int probeCount)
    {
        if (probeCount <= 0 ||
            probeCount > GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount)
        {
            return 0UL;
        }

        ulong privateBytes = checked(
            (ulong)probeCount *
            (ulong)Marshal.SizeOf<GPUSimpleDdgiSchedulerProbeState>());
        ulong publicOffset = Align16(privateBytes);
        ulong publicBytes = checked(
            (ulong)probeCount *
            (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>());
        return checked(publicOffset + publicBytes);
    }

    /// <summary>
    /// Changes authority without allocating anything for the CPU reference
    /// mode. A GPU mode only becomes active after a resident arena is ready.
    /// </summary>
    public void SetMode(SimpleDdgiSchedulerMode mode, ulong frameSerial = 0)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _mode = mode.Sanitize();
            if (!_mode.IsGpuMode())
            {
                _policiesInitialized = false;
                ReleaseArena(frameSerial, force: false);
                ReleaseFeedbackReadbackBuffers(frameSerial, force: false);
                ReleaseAuditReadbackBuffers(frameSerial, force: false);
            }
        }
    }

    /// <summary>
    /// Ensures a single arena exactly describes the requested active field.
    /// Ordinary camera travel leaves these topology capacities unchanged;
    /// real topology/mode changes replace the complete generation so byte
    /// accounting and private target bounds remain exact.
    /// </summary>
    public bool EnsureCapacity(
        int activeProbeCount,
        int requestCapacity,
        int activeVolumeCount,
        int dirtyRegionCapacity = SimpleDdgiGpuSchedulerLayout.MaxDirtyRegionCapacity,
        bool validationEnabled = false,
        ulong frameSerial = 0,
        ulong maxStorageBufferRange = ulong.MaxValue)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || activeProbeCount <= 0)
                return false;

            if (_layout != null && _arenaBuffer.IsValid &&
                activeProbeCount == _layout.ActiveProbeCount &&
                requestCapacity == _layout.RequestCapacity &&
                activeVolumeCount == _layout.ActiveVolumeCount &&
                dirtyRegionCapacity == _layout.DirtyRegionCapacity &&
                validationEnabled == _layout.ValidationEnabled)
            {
                return false;
            }

            SimpleDdgiGpuSchedulerLayout nextLayout = SimpleDdgiGpuSchedulerLayout.Create(
                activeProbeCount,
                requestCapacity,
                activeVolumeCount,
                dirtyRegionCapacity,
                validationEnabled,
                maxStorageBufferRange);

            BufferHandle nextArena = _bufferManager.CreateDeviceBuffer(
                nextLayout.TotalBytes,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.IndirectBufferBit |
                BufferUsageFlags.TransferSrcBit |
                BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Simple DDGI GPU Scheduler Arena");

            BufferHandle priorArena = _arenaBuffer;
            if (priorArena.IsValid)
                ReleaseAuditReadbackBuffers(frameSerial, force: false);
            _arenaBuffer = nextArena;
            _layout = nextLayout;
            // A replacement arena has no policy contents.  Do not rely on a
            // hash from the retired resource to suppress its first upload.
            _policiesInitialized = false;
            _resourceGeneration = NextGeneration(_resourceGeneration);
            if (priorArena.IsValid)
            {
                ulong priorBytes = 0;
                try
                {
                    priorBytes = _bufferManager.GetBufferSize(priorArena);
                }
                catch (InvalidOperationException)
                {
                    // The handle can only be stale if an owner already retired
                    // it. It is safe to omit it from accounting in that case.
                }
                RetireArena(priorArena, priorBytes, frameSerial);
            }

            EnsureFeedbackReadbackBuffers();
            EnsureAuditReadbackBuffers();
            RegisterArenaIfPossible();
            return true;
        }
    }

    public void Register(BindlessHeap bindlessHeap)
    {
        if (bindlessHeap == null)
            throw new ArgumentNullException(nameof(bindlessHeap));

        lock (_lock)
        {
            ThrowIfDisposed();
            _registeredBindlessHeap = bindlessHeap;
            RegisterArenaIfPossible();
        }
    }

    /// <summary>
    /// Uploads only the bounded CPU-authored control/delta records. Candidate,
    /// lane, outcome, indirect-command, and feedback regions are GPU-owned and
    /// are never populated by this method.
    /// </summary>
    public void UploadFrame(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        in GPUSimpleDdgiSchedulerFrame frame,
        ReadOnlySpan<GPUSimpleDdgiSchedulerVolumePolicy> volumePolicies,
        ReadOnlySpan<GPUSimpleDdgiSchedulerVolumePolicy> previousVolumePolicies,
        ReadOnlySpan<GPUSimpleDdgiSchedulerDirtyRegion> dirtyRegions)
    {
        if (stagingRing == null)
            throw new ArgumentNullException(nameof(stagingRing));
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return;
            if (volumePolicies.Length > GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount ||
                previousVolumePolicies.Length > GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            {
                throw new ArgumentException("The scheduler arena has room for at most sixteen volume policies.");
            }
            if (dirtyRegions.Length > _layout.DirtyRegionCapacity)
                throw new ArgumentException("The dirty-region delta exceeds the resident scheduler capacity.");

            UploadValue(stagingRing, commandBuffer, frame, _layout.Frame.Offset);
            ulong currentPolicyHash = HashSpan(volumePolicies);
            ulong previousPolicyHash = HashSpan(previousVolumePolicies);
            if (!_policiesInitialized || currentPolicyHash != _currentPolicyHash)
            {
                UploadSpan(stagingRing, commandBuffer, volumePolicies,
                    _layout.VolumePolicies.Offset);
                _currentPolicyHash = currentPolicyHash;
            }
            if (!_policiesInitialized || previousPolicyHash != _previousPolicyHash)
            {
                UploadSpan(stagingRing, commandBuffer, previousVolumePolicies,
                    _layout.PreviousVolumePolicies.Offset);
                _previousPolicyHash = previousPolicyHash;
            }
            _policiesInitialized = true;
            if (!dirtyRegions.IsEmpty)
            {
                UploadSpan(stagingRing, commandBuffer, dirtyRegions,
                    _layout.DirtyRegions.Offset);
            }
        }
    }

    /// <summary>
    /// Initializes the scheduler-owned probe mirror and persistent lane cursors
    /// after a resident arena is first created or replaced. These records are a
    /// different ABI from the public probe-state buffer; callers must not use a
    /// public-state upload as proof that this bootstrap completed.
    /// </summary>
    public bool UploadResidentBootstrap(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        ReadOnlySpan<GPUSimpleDdgiSchedulerProbeState> probeStates,
        ReadOnlySpan<uint> laneCursors)
    {
        if (stagingRing == null)
            throw new ArgumentNullException(nameof(stagingRing));
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return false;
            if (probeStates.Length > _layout.ActiveProbeCount)
                throw new ArgumentException("The scheduler bootstrap exceeds the resident probe-state region.", nameof(probeStates));
            if (laneCursors.Length != SimpleDdgiSchedulerAbi.MaxLaneCount)
                throw new ArgumentException("The scheduler bootstrap must contain one cursor per lane.", nameof(laneCursors));

            UploadSpan(stagingRing, commandBuffer, probeStates, _layout.ProbeState.Offset);
            UploadSpan(stagingRing, commandBuffer, laneCursors, _layout.LaneCursors.Offset);
            return true;
        }
    }

    public GPUSimpleDdgiSchedulePushConstants BuildPushConstants()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_arenaBuffer.IsValid || _layout == null)
                throw new InvalidOperationException("The GPU scheduler arena is not resident.");

            return new GPUSimpleDdgiSchedulePushConstants
            {
                ArenaBufferIndex = (uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer,
                ParamsBufferIndex = (uint)BindlessIndex.SimpleDdgiParamsBuffer,
                ProbeStateBufferIndex = (uint)BindlessIndex.SimpleDdgiProbeStateBuffer,
                UpdateQueueBufferIndex = (uint)BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
                RelocationBufferIndex = (uint)BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
                FrameOffsetWords = _layout.Frame.OffsetWords,
                VolumePolicyOffsetWords = _layout.VolumePolicies.OffsetWords,
                PreviousVolumePolicyOffsetWords = _layout.PreviousVolumePolicies.OffsetWords,
                DirtyRegionOffsetWords = _layout.DirtyRegions.OffsetWords,
                SchedulerProbeStateOffsetWords = _layout.ProbeState.OffsetWords,
                CandidateInputOffsetWords = _layout.CandidateInput.OffsetWords,
                CandidateGroupLaneCountsOffsetWords = _layout.CandidateGroupLaneCounts.OffsetWords,
                CandidateOutputOffsetWords = _layout.CandidateOutput.OffsetWords,
                LaneCandidateCountsOffsetWords = _layout.LaneCandidateCounts.OffsetWords,
                LanePrefixesOffsetWords = _layout.LanePrefixes.OffsetWords,
                LaneTotalsOffsetWords = _layout.LaneTotals.OffsetWords,
                LaneCursorsOffsetWords = _layout.LaneCursors.OffsetWords,
                LaneAdmissionOffsetWords = _layout.LaneAdmission.OffsetWords,
                CountersOffsetWords = _layout.Counters.OffsetWords,
                UpdateRecordsOffsetWords = _layout.UpdateRecords.OffsetWords,
                RayBucketCommandsOffsetWords = _layout.RayBucketCommands.OffsetWords,
                RayBucketMetadataOffsetWords = _layout.RayBucketMetadata.OffsetWords,
                IndirectCommandsOffsetWords = _layout.IndirectCommands.OffsetWords,
                OutcomesOffsetWords = _layout.Outcomes.OffsetWords,
                FeedbackOffsetWords = _layout.FeedbackSummary.OffsetWords,
                IrradianceAtlasBufferIndex = (uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                VisibilityAtlasBufferIndex = (uint)BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
                TransportIrradianceAtlasBufferIndex = (uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer,
                ReceiverProbeBufferIndex = (uint)BindlessIndex.SimpleDdgiReceiverProbeBuffer
            };
        }
    }

    /// <summary>Returns the resident Vulkan buffer for indirect dispatch recording.</summary>
    public VkBuffer GetArenaVkBuffer()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _arenaBuffer.IsValid ? _bufferManager.GetBuffer(_arenaBuffer) : default;
        }
    }

    public ulong GetIndirectCommandOffset(SimpleDdgiSchedulerDispatchSlot slot)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _layout?.GetIndirectCommand(slot).Offset ?? 0UL;
        }
    }

    public ulong GetRayBucketCommandOffset(int bucketIndex)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_layout == null)
                return 0UL;
            return _layout.GetRayBucketIndirectCommand(bucketIndex).Offset;
        }
    }

    /// <summary>
    /// Records a 4 KiB feedback copy. The caller must invoke
    /// <see cref="TryReadCompletedFeedback"/> only after the frame fence has
    /// completed; no same-frame feedback path exists here.
    /// </summary>
    public bool RecordFeedbackReadback(CommandBuffer commandBuffer, int frameIndex, ulong frameSerial)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return false;
            EnsureFeedbackReadbackBuffers();
            if (_feedbackRecorded[frameIndex])
                return false;

            VkBuffer source = _bufferManager.GetBuffer(_arenaBuffer);
            VkBuffer destination = _bufferManager.GetBuffer(_feedbackReadbackBuffers[frameIndex]);
            ulong bytes = SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes;
            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                _layout.FeedbackSummary.Offset,
                bytes);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = _layout.FeedbackSummary.Offset,
                DstOffset = 0,
                Size = bytes
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

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
            _feedbackSubmittedResourceGeneration[frameIndex] = _resourceGeneration;
            return true;
        }
    }

    /// <summary>
    /// Records one exceptional, complete resident-state export. The copy is
    /// ordered before the quiesced scheduler dispatches in the same submission;
    /// the CPU may consume it only after the exact graphics-fence token signals.
    /// </summary>
    public bool RecordFallbackStateExport(
        CommandBuffer commandBuffer,
        BufferHandle publicProbeStateBuffer,
        in SimpleDdgiSchedulerStateExportTag tag,
        ulong pendingFrameFenceValue)
    {
        if (commandBuffer.Handle == 0 ||
            !publicProbeStateBuffer.IsValid ||
            pendingFrameFenceValue == 0UL)
        {
            return false;
        }

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_mode != SimpleDdgiSchedulerMode.GpuResident ||
                !_arenaBuffer.IsValid ||
                _layout == null ||
                _fallbackExportRecorded ||
                tag.ProbeCount <= 0 ||
                tag.ProbeCount > _layout.ActiveProbeCount ||
                tag.SchedulerResourceGeneration != _resourceGeneration)
            {
                return false;
            }

            ulong privateBytes = checked(
                (ulong)tag.ProbeCount *
                (ulong)Marshal.SizeOf<GPUSimpleDdgiSchedulerProbeState>());
            ulong publicOffset = Align16(privateBytes);
            ulong publicBytes = checked(
                (ulong)tag.ProbeCount *
                (ulong)Marshal.SizeOf<GPUSimpleDdgiProbeState>());
            ulong totalBytes = ResolveFallbackStateExportBytes(tag.ProbeCount);
            EnsureFallbackExportReadbackBuffer(totalBytes);
            if (!_fallbackExportReadbackBuffer.IsValid)
                return false;

            VkBuffer arena = _bufferManager.GetBuffer(_arenaBuffer);
            VkBuffer publicState = _bufferManager.GetBuffer(publicProbeStateBuffer);
            VkBuffer destination = _bufferManager.GetBuffer(
                _fallbackExportReadbackBuffer);

            BufferMemoryBarrier2 privateBarrier = BarrierBuilder.BufferBarrier(
                arena,
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageWriteBit |
                    AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                _layout.ProbeState.Offset,
                privateBytes);
            ExecuteBufferBarrier(commandBuffer, privateBarrier);
            BufferMemoryBarrier2 publicBarrier = BarrierBuilder.BufferBarrier(
                publicState,
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageWriteBit |
                    AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0UL,
                publicBytes);
            ExecuteBufferBarrier(commandBuffer, publicBarrier);

            BufferCopy privateCopy = new()
            {
                SrcOffset = _layout.ProbeState.Offset,
                DstOffset = 0UL,
                Size = privateBytes
            };
            _context.Api.CmdCopyBuffer(
                commandBuffer,
                arena,
                destination,
                1,
                &privateCopy);
            BufferCopy publicCopy = new()
            {
                SrcOffset = 0UL,
                DstOffset = publicOffset,
                Size = publicBytes
            };
            _context.Api.CmdCopyBuffer(
                commandBuffer,
                publicState,
                destination,
                1,
                &publicCopy);

            BufferMemoryBarrier2 hostBarrier = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0UL,
                totalBytes);
            ExecuteBufferBarrier(commandBuffer, hostBarrier);

            _fallbackExportRecorded = true;
            _fallbackExportCompletion =
                GpuCompletionToken.ForFrameFence(pendingFrameFenceValue);
            _fallbackExportTag = tag;
            _fallbackExportPublicStateOffset = publicOffset;
            return true;
        }
    }

    public SimpleDdgiSchedulerStateExportReadStatus TryReadFallbackStateExport(
        ulong completedFrameFenceValue,
        Span<GPUSimpleDdgiSchedulerProbeState> schedulerStates,
        Span<GPUSimpleDdgiProbeState> publicStates,
        out SimpleDdgiSchedulerStateExportTag tag)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            tag = default;
            if (!_fallbackExportRecorded)
                return SimpleDdgiSchedulerStateExportReadStatus.Unavailable;
            if (completedFrameFenceValue < _fallbackExportCompletion.Value)
                return SimpleDdgiSchedulerStateExportReadStatus.Pending;

            int probeCount = _fallbackExportTag.ProbeCount;
            if (!_fallbackExportReadbackBuffer.IsValid ||
                schedulerStates.Length < probeCount ||
                publicStates.Length < probeCount)
            {
                _fallbackExportRecorded = false;
                return SimpleDdgiSchedulerStateExportReadStatus.Invalid;
            }

            _bufferManager.InvalidateBuffer(
                _fallbackExportReadbackBuffer,
                0UL,
                _fallbackExportReadbackBytes);
            byte* mapped = (byte*)_bufferManager.GetMappedPointer(
                _fallbackExportReadbackBuffer);
            if (mapped == null)
            {
                _fallbackExportRecorded = false;
                return SimpleDdgiSchedulerStateExportReadStatus.Invalid;
            }

            new ReadOnlySpan<GPUSimpleDdgiSchedulerProbeState>(
                mapped,
                probeCount).CopyTo(schedulerStates);
            new ReadOnlySpan<GPUSimpleDdgiProbeState>(
                mapped + checked((nint)_fallbackExportPublicStateOffset),
                probeCount).CopyTo(publicStates);
            tag = _fallbackExportTag;
            _fallbackExportRecorded = false;
            return SimpleDdgiSchedulerStateExportReadStatus.Complete;
        }
    }

    /// <summary>
    /// Clears the epoch reduction before the first audit chunk.  The clear is
    /// recorded on the same command buffer as the audit and is followed by a
    /// transfer-to-compute barrier, so no workgroup can observe a prior epoch.
    /// </summary>
    public bool ResetTransportAuditSummary(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return false;

            Silk.NET.Vulkan.Buffer arena = _bufferManager.GetBuffer(_arenaBuffer);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                arena,
                _layout.AuditSummary.Offset,
                _layout.AuditSummary.ByteSize,
                0u);
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                arena,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
                _layout.AuditSummary.Offset,
                _layout.AuditSummary.ByteSize);
            ExecuteBufferBarrier(commandBuffer, barrier);
            return true;
        }
    }

    /// <summary>
    /// Clears the bounded per-chunk audit status words before cached-ray
    /// evaluation. A transfer-to-compute barrier makes the zeroed fail-closed
    /// flags visible before ray invocations begin their atomic status updates.
    /// </summary>
    public bool ResetTransportAuditWorkspace(CommandBuffer commandBuffer)
    {
        if (commandBuffer.Handle == 0)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return false;

            Silk.NET.Vulkan.Buffer arena = _bufferManager.GetBuffer(_arenaBuffer);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                arena,
                _layout.AuditWorkspace.Offset,
                _layout.AuditWorkspace.ByteSize,
                0u);
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                arena,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
                _layout.AuditWorkspace.Offset,
                _layout.AuditWorkspace.ByteSize);
            ExecuteBufferBarrier(commandBuffer, barrier);
            return true;
        }
    }

    /// <summary>
    /// Copies only the compact audit header into a delayed host-visible slot.
    /// The full 1 KiB arena region remains GPU-resident and is not read back.
    /// </summary>
    public bool RecordTransportAuditReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong frameSerial,
        uint auditEpoch)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_mode.IsGpuMode() || !_arenaBuffer.IsValid || _layout == null)
                return false;
            EnsureAuditReadbackBuffers();
            if (_auditReadbackRecorded[frameIndex])
                return false;

            Silk.NET.Vulkan.Buffer source = _bufferManager.GetBuffer(_arenaBuffer);
            Silk.NET.Vulkan.Buffer destination = _bufferManager.GetBuffer(
                _auditReadbackBuffers[frameIndex]);
            ulong summaryBytes = checked((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());
            BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
                source,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                _layout.AuditSummary.Offset,
                summaryBytes);
            ExecuteBufferBarrier(commandBuffer, beforeCopy);

            BufferCopy copy = new()
            {
                SrcOffset = _layout.AuditSummary.Offset,
                DstOffset = 0,
                Size = summaryBytes
            };
            _context.Api.CmdCopyBuffer(commandBuffer, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                summaryBytes);
            ExecuteBufferBarrier(commandBuffer, afterCopy);
            _auditReadbackRecorded[frameIndex] = true;
            _auditSubmittedFrameSerial[frameIndex] = frameSerial;
            _auditSubmittedResourceGeneration[frameIndex] = _resourceGeneration;
            _auditSubmittedEpoch[frameIndex] = auditEpoch;
            return true;
        }
    }

    public bool TryReadCompletedTransportAudit(
        int frameIndex,
        ulong completedFrameSerial,
        out SimpleDdgiTransportAuditReadback readback)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_lock)
        {
            ThrowIfDisposed();
            readback = default;
            if (!_auditReadbackRecorded[frameIndex] ||
                !_auditReadbackBuffers[frameIndex].IsValid ||
                completedFrameSerial <= _auditSubmittedFrameSerial[frameIndex])
            {
                return false;
            }

            BufferHandle handle = _auditReadbackBuffers[frameIndex];
            ulong summaryBytes = checked((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());
            _bufferManager.InvalidateBuffer(handle, 0, summaryBytes);
            GPUSimpleDdgiTransportAuditSummary summary =
                *(GPUSimpleDdgiTransportAuditSummary*)_bufferManager.GetMappedPointer(handle);
            ulong expectedFrameSerial = _auditSubmittedFrameSerial[frameIndex];
            bool resourceMatches =
                _auditSubmittedResourceGeneration[frameIndex] == _resourceGeneration;
            bool serialMatches =
                summary.LastChunkIndex != uint.MaxValue &&
                expectedFrameSerial <= completedFrameSerial;
            _auditReadbackRecorded[frameIndex] = false;
            if (!resourceMatches || !serialMatches)
            {
                _staleFeedbackCount++;
                return false;
            }

            readback = new SimpleDdgiTransportAuditReadback(
                _auditSubmittedEpoch[frameIndex],
                expectedFrameSerial,
                summary);
            return true;
        }
    }

    /// <summary>
    /// Reads a previously submitted feedback copy. Requiring a strictly later
    /// completed serial makes an accidental same-frame poll fail closed.
    /// </summary>
    public bool TryReadCompletedFeedback(
        int frameIndex,
        ulong completedFrameSerial,
        out GPUSimpleDdgiSchedulerFeedback feedback)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_lock)
        {
            ThrowIfDisposed();
            feedback = default;
            if (!_feedbackRecorded[frameIndex] ||
                !_feedbackReadbackBuffers[frameIndex].IsValid ||
                completedFrameSerial <= _feedbackSubmittedFrameSerial[frameIndex])
            {
                return false;
            }

            BufferHandle readback = _feedbackReadbackBuffers[frameIndex];
            _bufferManager.InvalidateBuffer(
                readback,
                0,
                SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes);
            uint* feedbackWords = (uint*)_bufferManager.GetMappedPointer(readback);
            feedback = *(GPUSimpleDdgiSchedulerFeedback*)feedbackWords;

            uint expectedLow = checked((uint)_feedbackSubmittedFrameSerial[frameIndex]);
            uint expectedHigh = checked((uint)(_feedbackSubmittedFrameSerial[frameIndex] >> 32));
            bool generationMatches = feedback.SchedulerResourceGeneration ==
                _feedbackSubmittedResourceGeneration[frameIndex];
            // A resize, mode transition, or descriptor replacement invalidates
            // feedback that was recorded against the retired arena.  It may be
            // fence-complete and internally self-consistent, but it cannot
            // affect policy for the new resource generation.
            generationMatches &= feedback.SchedulerResourceGeneration ==
                _resourceGeneration;
            bool serialMatches = feedback.FrameSerialLow == expectedLow &&
                feedback.FrameSerialHigh == expectedHigh;
            _feedbackRecorded[frameIndex] = false;
            if (!generationMatches || !serialMatches)
            {
                _staleFeedbackCount++;
                feedback = default;
                return false;
            }

            int failureBase =
                SimpleDdgiSchedulerAbi.FeedbackCommitFailureOffsetWords;
            _lastCommitFailureBreakdown = new(
                feedbackWords[failureBase + 0],
                feedbackWords[failureBase + 1],
                feedbackWords[failureBase + 2],
                feedbackWords[failureBase + 3],
                feedbackWords[failureBase + 4],
                feedbackWords[failureBase + 5],
                feedbackWords[failureBase + 6],
                feedbackWords[failureBase + 7],
                feedbackWords[failureBase + 8],
                feedbackWords[failureBase + 9],
                feedbackWords[
                    SimpleDdgiSchedulerAbi
                        .FeedbackTransactionPredicateOffsetWords],
                feedbackWords[
                    SimpleDdgiSchedulerAbi
                        .FeedbackMissingCompletionOffsetWords],
                feedbackWords[
                    SimpleDdgiSchedulerAbi
                        .FeedbackProducerFailureOffsetWords],
                feedbackWords[
                    SimpleDdgiSchedulerAbi
                        .FeedbackCacheReadFailureOffsetWords]);
            _lastActiveSourceMutationCount = feedbackWords[
                SimpleDdgiSchedulerAbi.FeedbackActiveSourceMutationOffsetWords];
            _lastActiveCanonicalMutationCount = feedbackWords[
                SimpleDdgiSchedulerAbi.FeedbackActiveCanonicalMutationOffsetWords];

            // The fixed feedback header remains the CPU control-plane ABI.
            // The following 896 words carry the persistent lane cursors so an
            // arena replacement can seed the new resource from the last
            // fence-complete GPU state instead of silently restarting every
            // lane at zero. They are accepted only with a matching summary.
            for (int lane = 0; lane < SimpleDdgiSchedulerAbi.MaxLaneCount; lane++)
                _lastFeedbackLaneCursors[lane] = feedbackWords[64 + lane];
            _hasLastFeedbackLaneCursors = true;

            return true;
        }
    }

    /// <summary>
    /// Copies the most recent fence-complete persistent lane cursors. The
    /// values are deliberately advisory until a new arena bootstrap uploads
    /// them; ordinary frames leave the GPU-owned cursor region untouched.
    /// </summary>
    public bool TryCopyLastFeedbackLaneCursors(Span<uint> destination)
    {
        if (destination.Length < SimpleDdgiSchedulerAbi.MaxLaneCount)
            return false;

        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_hasLastFeedbackLaneCursors)
                return false;
            _lastFeedbackLaneCursors.AsSpan().CopyTo(destination);
            return true;
        }
    }

    public void CollectRetired(ulong completedFrameFenceValue, bool force = false)
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
                if (retired.Resource.Kind != GpuRetirementResourceKind.Buffer)
                {
                    throw new InvalidOperationException(
                        "Simple-DDGI scheduler retirement contained a non-buffer record.");
                }
                BufferHandle buffer = UnpackBufferHandle(retired.Resource.Handle);
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
            for (int i = 0; i < _feedbackReadbackBuffers.Length; i++)
            {
                if (_feedbackReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_feedbackReadbackBuffers[i]);
                _feedbackReadbackBuffers[i] = BufferHandle.Invalid;
                _feedbackRecorded[i] = false;
            }
            for (int i = 0; i < _auditReadbackBuffers.Length; i++)
            {
                if (_auditReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_auditReadbackBuffers[i]);
                _auditReadbackBuffers[i] = BufferHandle.Invalid;
                _auditReadbackRecorded[i] = false;
            }
            if (_fallbackExportReadbackBuffer.IsValid)
                _bufferManager.DestroyBuffer(_fallbackExportReadbackBuffer);
            _fallbackExportReadbackBuffer = BufferHandle.Invalid;
            _fallbackExportReadbackBytes = 0UL;
            _fallbackExportRecorded = false;
            int retiredCount = _retirement.DrainAfterExternalDeviceIdle(
                _retirementScratch);
            for (int index = 0; index < retiredCount; index++)
            {
                BufferHandle buffer = UnpackBufferHandle(
                    _retirementScratch[index].Resource.Handle);
                if (buffer.IsValid)
                    _bufferManager.DestroyBuffer(buffer);
                _retirementScratch[index] = default;
            }
        }
    }

    private void UploadValue(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        in GPUSimpleDdgiSchedulerFrame value,
        ulong destinationOffset)
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

    private void UploadSpan<T>(
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        ReadOnlySpan<T> values,
        ulong destinationOffset)
        where T : unmanaged
    {
        if (values.IsEmpty)
            return;
        GpuBufferUploader.UploadSpanToBuffer(
            _context,
            _bufferManager,
            stagingRing,
            commandBuffer,
            _arenaBuffer,
            values,
            destinationOffset,
            UploadBarrier());
    }

    private static UploadBarrierDescription UploadBarrier() =>
        new(
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

    private static ulong HashSpan<T>(ReadOnlySpan<T> values)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }
        return hash;
    }

    private void EnsureFeedbackReadbackBuffers()
    {
        for (int frameIndex = 0; frameIndex < _feedbackReadbackBuffers.Length; frameIndex++)
        {
            if (_feedbackReadbackBuffers[frameIndex].IsValid)
                continue;
            _feedbackReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Simple DDGI Scheduler Feedback Frame {frameIndex}",
                MemoryBudgetCategory.GlobalIllumination);
        }
    }

    private void EnsureAuditReadbackBuffers()
    {
        for (int frameIndex = 0; frameIndex < _auditReadbackBuffers.Length; frameIndex++)
        {
            if (_auditReadbackBuffers[frameIndex].IsValid)
                continue;
            _auditReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                checked((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>()),
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"Simple DDGI Transport Audit Frame {frameIndex}",
                MemoryBudgetCategory.GlobalIllumination);
        }
    }

    private void EnsureFallbackExportReadbackBuffer(ulong requiredBytes)
    {
        if (_fallbackExportReadbackBuffer.IsValid &&
            _fallbackExportReadbackBytes >= requiredBytes)
        {
            return;
        }
        if (_fallbackExportRecorded)
            throw new InvalidOperationException(
                "A pending Simple-DDGI state export cannot be resized.");

        if (_fallbackExportReadbackBuffer.IsValid)
            _bufferManager.DestroyBuffer(_fallbackExportReadbackBuffer);
        _fallbackExportReadbackBuffer = _bufferManager.CreateBuffer(
            requiredBytes,
            BufferUsageFlags.TransferDstBit,
            MemoryUsage.AutoPreferHost,
            AllocationCreateFlags.MappedBit |
                AllocationCreateFlags.HostAccessRandomBit,
            "Simple DDGI Scheduler Fallback State Export",
            MemoryBudgetCategory.GlobalIllumination);
        _fallbackExportReadbackBytes = requiredBytes;
    }

    private ulong CountValidFeedbackReadbackBytes()
    {
        ulong bytes = 0;
        for (int frameIndex = 0; frameIndex < _feedbackReadbackBuffers.Length; frameIndex++)
        {
            if (_feedbackReadbackBuffers[frameIndex].IsValid)
                bytes = checked(bytes + SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes);
        }
        return bytes;
    }

    private ulong CountValidAuditReadbackBytes()
    {
        ulong bytes = 0;
        ulong summaryBytes = checked((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());
        for (int frameIndex = 0; frameIndex < _auditReadbackBuffers.Length; frameIndex++)
        {
            if (_auditReadbackBuffers[frameIndex].IsValid)
                bytes = checked(bytes + summaryBytes);
        }
        return bytes;
    }

    private void RegisterArenaIfPossible()
    {
        if (_registeredBindlessHeap == null || !_arenaBuffer.IsValid || _layout == null)
            return;
        _registeredBindlessHeap.RegisterStorageBuffer(
            BindlessIndex.SimpleDdgiSchedulerArenaBuffer,
            _bufferManager.GetBuffer(_arenaBuffer),
            0,
            _layout.TotalBytes);
    }

    private void ReleaseArena(ulong frameSerial, bool force)
    {
        if (!_arenaBuffer.IsValid)
            return;
        ulong bytes = _layout?.TotalBytes ?? 0UL;
        BufferHandle arena = _arenaBuffer;
        _arenaBuffer = BufferHandle.Invalid;
        _layout = null;
        if (force)
        {
            _bufferManager.DestroyBuffer(arena);
            return;
        }
        RetireArena(arena, bytes, frameSerial);
    }

    private void ReleaseFeedbackReadbackBuffers(ulong frameSerial, bool force)
    {
        for (int frameIndex = 0; frameIndex < _feedbackReadbackBuffers.Length; frameIndex++)
        {
            BufferHandle readback = _feedbackReadbackBuffers[frameIndex];
            _feedbackReadbackBuffers[frameIndex] = BufferHandle.Invalid;
            _feedbackRecorded[frameIndex] = false;
            _feedbackSubmittedFrameSerial[frameIndex] = 0;
            _feedbackSubmittedResourceGeneration[frameIndex] = 0;
            if (!readback.IsValid)
                continue;

            ulong bytes = SimpleDdgiGpuSchedulerLayout.ShippingFeedbackBytes;
            if (force)
                _bufferManager.DestroyBuffer(readback);
            else
                RetireArena(readback, bytes, frameSerial);
        }
    }

    private void ReleaseAuditReadbackBuffers(ulong frameSerial, bool force)
    {
        ulong bytes = checked((ulong)Marshal.SizeOf<GPUSimpleDdgiTransportAuditSummary>());
        for (int frameIndex = 0; frameIndex < _auditReadbackBuffers.Length; frameIndex++)
        {
            BufferHandle readback = _auditReadbackBuffers[frameIndex];
            _auditReadbackBuffers[frameIndex] = BufferHandle.Invalid;
            _auditReadbackRecorded[frameIndex] = false;
            _auditSubmittedFrameSerial[frameIndex] = 0;
            _auditSubmittedResourceGeneration[frameIndex] = 0;
            _auditSubmittedEpoch[frameIndex] = 0;
            if (!readback.IsValid)
                continue;
            if (force)
                _bufferManager.DestroyBuffer(readback);
            else
                RetireArena(readback, bytes, frameSerial);
        }
    }

    private void RetireArena(BufferHandle arena, ulong bytes, ulong frameSerial)
    {
        if (!arena.IsValid)
            return;
        if (frameSerial == 0UL)
        {
            _bufferManager.DestroyBuffer(arena);
            return;
        }
        GpuRetirementRecord record = new(
            ResourceGeneration: arena.Generation,
            ByteCharge: bytes,
            EnqueuedFrame: frameSerial,
            Completion: GpuCompletionToken.ForFrameFence(frameSerial),
            Resource: new GpuRetirementResource(
                GpuRetirementResourceKind.Buffer,
                PackBufferHandle(arena)));
        if (!_retirement.TryEnqueue(
                record,
                liveBytes: 0UL,
                out GpuRetirementAdmissionFailure failure))
        {
            throw new InvalidOperationException(
                $"Simple-DDGI scheduler retirement admission failed: {failure}.");
        }
    }

    private static ulong PackBufferHandle(BufferHandle buffer) =>
        ((ulong)buffer.Generation << 32) | unchecked((uint)buffer.Index);

    private static BufferHandle UnpackBufferHandle(ulong packed) =>
        new(unchecked((int)(uint)packed), (uint)(packed >> 32));

    private static uint NextGeneration(uint generation) =>
        generation == uint.MaxValue ? 1u : generation + 1u;

    private static ulong Align16(ulong value) =>
        checked((value + 15UL) & ~15UL);

    private void ExecuteBufferBarrier(CommandBuffer commandBuffer, BufferMemoryBarrier2 barrier)
    {
        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiGpuScheduler));
    }

}

public readonly record struct SimpleDdgiTransportAuditReadback(
    uint AuditEpoch,
    ulong FrameSerial,
    GPUSimpleDdgiTransportAuditSummary Summary);

public enum SimpleDdgiSchedulerStateExportReadStatus : byte
{
    Unavailable,
    Pending,
    Complete,
    Invalid
}

public readonly record struct SimpleDdgiSchedulerStateExportTag(
    int ProbeCount,
    uint SchedulerResourceGeneration,
    uint VolumeTableGeneration,
    uint PhysicalOwnershipGeneration,
    uint SourceLightingGeneration,
    uint SourceEpochGeneration,
    uint TransportGeneration,
    ulong RequestedFrameSerial)
{
    public bool IsInitialized =>
        ProbeCount > 0 &&
        SchedulerResourceGeneration != 0u &&
        VolumeTableGeneration != 0u &&
        PhysicalOwnershipGeneration != 0u &&
        SourceLightingGeneration != 0u &&
        SourceEpochGeneration != 0u &&
        TransportGeneration != 0u;
}
