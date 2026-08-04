using System;
using System.Collections.Generic;
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
    private readonly List<RetiredArena> _retiredArenas = new();

    private BufferHandle _arenaBuffer;
    private SimpleDdgiGpuSchedulerLayout? _layout;
    private BindlessHeap? _registeredBindlessHeap;
    private SimpleDdgiSchedulerMode _mode = SimpleDdgiSchedulerMode.CpuReference;
    private uint _resourceGeneration = 1;
    private ulong _retiredBytes;
    private ulong _staleFeedbackCount;
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
    public ulong RetiredBytes => _retiredBytes;
    public ulong StaleFeedbackCount => _staleFeedbackCount;

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
            }
        }
    }

    /// <summary>
    /// Ensures a single arena can contain the requested active field. Capacity
    /// grows monotonically during a renderer lifetime, so ordinary camera
    /// travel does not recreate or descriptor-update the resource every frame.
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
                activeProbeCount <= _layout.ActiveProbeCount &&
                requestCapacity <= _layout.RequestCapacity &&
                activeVolumeCount <= _layout.ActiveVolumeCount &&
                dirtyRegionCapacity <= _layout.DirtyRegionCapacity)
            {
                return false;
            }

            int probeCapacity = Math.Max(activeProbeCount, _layout?.ActiveProbeCount ?? 0);
            int requestCapacityCeiling = Math.Max(requestCapacity, _layout?.RequestCapacity ?? 0);
            int volumeCapacity = Math.Max(activeVolumeCount, _layout?.ActiveVolumeCount ?? 0);
            int dirtyCapacity = Math.Max(dirtyRegionCapacity, _layout?.DirtyRegionCapacity ?? 0);
            SimpleDdgiGpuSchedulerLayout nextLayout = SimpleDdgiGpuSchedulerLayout.Create(
                probeCapacity,
                requestCapacityCeiling,
                volumeCapacity,
                dirtyCapacity,
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
                IndirectCommandsOffsetWords = _layout.IndirectCommands.OffsetWords,
                OutcomesOffsetWords = _layout.Outcomes.OffsetWords,
                FeedbackOffsetWords = _layout.FeedbackSummary.OffsetWords
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
        if ((uint)bucketIndex >= SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount)
            throw new ArgumentOutOfRangeException(nameof(bucketIndex));

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_layout == null)
                return 0UL;
            return checked(_layout.RayBucketCommands.Offset +
                (ulong)bucketIndex * SimpleDdgiGpuSchedulerLayout.IndirectCommandStrideBytes);
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
            feedback = *(GPUSimpleDdgiSchedulerFeedback*)_bufferManager.GetMappedPointer(readback);

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

            return true;
        }
    }

    public void CollectRetired(ulong completedFrameSerial, bool force = false)
    {
        lock (_lock)
        {
            for (int i = _retiredArenas.Count - 1; i >= 0; i--)
            {
                RetiredArena retired = _retiredArenas[i];
                if (!force && retired.RetireAfterFrameSerial > completedFrameSerial)
                    continue;
                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBytes -= Math.Min(_retiredBytes, retired.Bytes);
                _retiredArenas.RemoveAt(i);
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
            for (int i = 0; i < _retiredArenas.Count; i++)
            {
                if (_retiredArenas[i].Buffer.IsValid)
                    _bufferManager.DestroyBuffer(_retiredArenas[i].Buffer);
            }
            _retiredArenas.Clear();
            _retiredBytes = 0;
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

    private void RetireArena(BufferHandle arena, ulong bytes, ulong frameSerial)
    {
        if (!arena.IsValid)
            return;
        ulong retireAfter = checked(frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL);
        _retiredArenas.Add(new RetiredArena(arena, bytes, retireAfter));
        _retiredBytes = checked(_retiredBytes + bytes);
    }

    private static uint NextGeneration(uint generation) =>
        generation == uint.MaxValue ? 1u : generation + 1u;

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

    private readonly record struct RetiredArena(
        BufferHandle Buffer,
        ulong Bytes,
        ulong RetireAfterFrameSerial);
}
