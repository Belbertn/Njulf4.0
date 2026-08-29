using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public sealed record VulkanMeshletPhysicalResidencySnapshot(
    bool Initialized,
    int AllocatedBankCount,
    ulong AllocatedBankBytes,
    ulong LastRecordedUploadBytes,
    long RecordedPageCount,
    long FailedPageRecordCount,
    long CompletedDemandKeyCount,
    long DemandOverflowCount,
    long InvalidShaderMappingCount,
    string LatestFailure);

/// <summary>
/// Mirrors the backend-neutral managed meshlet cache into stable Vulkan
/// bindless buffers. Page bytes are copied before a mapping is eligible for
/// publication, while the two page/range tables are rewritten only in the
/// frame slot whose fence has completed.
/// </summary>
public sealed class VulkanMeshletPhysicalResidencyResources : IDisposable
{
    private const ulong MinimumStorageBufferBytes = 16;
    private const ulong FeedbackCounterBytes = 16;
    private const int FrameCount = 2;
    private const int DemandHeaderWordCount = 4;
    private const int FeedbackOverflowWord = 0;
    private const int FeedbackAcceptedDemandWord = 1;
    private const int FeedbackInvalidMappingWord = 2;

    private static readonly PipelineStageFlags2 ConsumerStages =
        PipelineStageFlags2.ComputeShaderBit |
        PipelineStageFlags2.TaskShaderBitExt |
        PipelineStageFlags2.MeshShaderBitExt |
        PipelineStageFlags2.FragmentShaderBit;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly StagingRing _stagingRing;
    private readonly BindlessHeap _bindlessHeap;
    private readonly FenceBasedDeleter? _deleter;
    private readonly MeshletPhysicalPageCacheUploader _uploader;
    private readonly MeshletStreamingResidencyCoordinator _coordinator;
    private readonly MeshletStreamingResidencyOptions _options;
    private readonly BufferHandle[] _pageTables =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly ulong[] _pageTableBytes = new ulong[FrameCount];
    private readonly BufferHandle[] _rangeStates =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly ulong[] _rangeStateBytes = new ulong[FrameCount];
    private readonly BufferHandle[] _demandBuffers =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly ulong[] _demandBufferBytes = new ulong[FrameCount];
    private readonly BufferHandle[] _feedbackBuffers =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly BufferHandle[] _demandReadbackBuffers =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly BufferHandle[] _feedbackReadbackBuffers =
        [BufferHandle.Invalid, BufferHandle.Invalid];
    private readonly bool[] _readbackRecorded = new bool[FrameCount];
    private readonly BufferHandle[] _banks =
        new BufferHandle[MeshletPhysicalBankAllocator.MaximumBankCount];
    private readonly List<BufferHandle> _immediateRetirements = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _tickLock = new();

    private BufferHandle _virtualMappings = BufferHandle.Invalid;
    private ulong _virtualMappingBytes;
    private BufferHandle _streamingRanges = BufferHandle.Invalid;
    private ulong _streamingRangeBytes;
    private Task? _tickTask;
    private bool _initialized;
    private bool _disposed;
    private long _recordedPageCount;
    private long _failedPageRecordCount;
    private long _completedDemandKeyCount;
    private long _demandOverflowCount;
    private long _invalidShaderMappingCount;
    private ulong _lastRecordedUploadBytes;
    private int _streamingRangeCount;
    private string _latestFailure = string.Empty;

    public VulkanMeshletPhysicalResidencyResources(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        BindlessHeap bindlessHeap,
        MeshletPhysicalPageCacheUploader uploader,
        MeshletStreamingResidencyCoordinator coordinator,
        FenceBasedDeleter? deleter = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ??
            throw new ArgumentNullException(nameof(stagingRing));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        _coordinator = coordinator ??
            throw new ArgumentNullException(nameof(coordinator));
        _deleter = deleter;
        _options = coordinator.Options;
        _uploader.RequireExplicitGpuRecording();
        Array.Fill(_banks, BufferHandle.Invalid);
    }

    public MeshletStreamingResidencyCoordinator Coordinator => _coordinator;

    public MeshletPhysicalPageCacheUploader Uploader => _uploader;

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return;

        ulong initialPageTableBytes = Math.Max(
            MinimumStorageBufferBytes,
            checked((ulong)_options.PhysicalPageCapacity *
                (ulong)Marshal.SizeOf<GPUMeshletPageTableEntry>()));
        for (int frame = 0; frame < FrameCount; frame++)
        {
            _pageTables[frame] = CreateStorageBuffer(
                initialPageTableBytes,
                $"Meshlet Physical Page Table [{frame}]");
            _pageTableBytes[frame] = initialPageTableBytes;
            RegisterBuffer(
                BindlessIndex.MeshletPhysicalPageTableBufferBase + frame,
                _pageTables[frame],
                initialPageTableBytes);

            _rangeStates[frame] = CreateStorageBuffer(
                MinimumStorageBufferBytes,
                $"Meshlet Streaming Range State [{frame}]");
            _rangeStateBytes[frame] = MinimumStorageBufferBytes;
            RegisterBuffer(
                BindlessIndex.MeshletStreamingRangeStateBufferBase + frame,
                _rangeStates[frame],
                MinimumStorageBufferBytes);

            ulong demandBytes = CalculateDemandBufferBytes(
                _options.MaximumRequestsPerSerial,
                rangeCount: 0);
            _demandBuffers[frame] = CreateStorageBuffer(
                demandBytes,
                $"Meshlet Streaming Demand [{frame}]",
                BufferUsageFlags.TransferSrcBit);
            _demandBufferBytes[frame] = demandBytes;
            RegisterBuffer(
                BindlessIndex.MeshletStreamingDemandBufferBase + frame,
                _demandBuffers[frame],
                demandBytes);

            _feedbackBuffers[frame] = CreateStorageBuffer(
                FeedbackCounterBytes,
                $"Meshlet Streaming Feedback [{frame}]",
                BufferUsageFlags.TransferSrcBit);
            RegisterBuffer(
                BindlessIndex.MeshletStreamingFeedbackCounterBufferBase + frame,
                _feedbackBuffers[frame],
                FeedbackCounterBytes);
        }

        _virtualMappings = CreateStorageBuffer(
            MinimumStorageBufferBytes,
            "Meshlet Virtual Mapping Table");
        _virtualMappingBytes = MinimumStorageBufferBytes;
        RegisterBuffer(
            BindlessIndex.MeshletVirtualMappingBuffer,
            _virtualMappings,
            _virtualMappingBytes);

        _streamingRanges = CreateStorageBuffer(
            MinimumStorageBufferBytes,
            "Meshlet Streaming Range Table");
        _streamingRangeBytes = MinimumStorageBufferBytes;
        RegisterBuffer(
            BindlessIndex.MeshletStreamingRangeBuffer,
            _streamingRanges,
            _streamingRangeBytes);
        _initialized = true;
    }

    /// <summary>
    /// Called only after the selected frame slot's fence has completed.
    /// Disk reads and page repacking continue on workers and are observed by a
    /// later frame without blocking the render thread.
    /// </summary>
    public void BeginFenceSafeFrame(
        int frameSlot,
        ulong submissionSerial,
        ulong completedSerial)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Initialize();
        ValidateFrame(frameSlot);
        ConsumeCompletedFeedback(frameSlot, submissionSerial);
        _uploader.PrepareFrameSlot(frameSlot, 1 - frameSlot);

        lock (_tickLock)
        {
            if (_tickTask is { IsCompleted: true })
            {
                try
                {
                    _tickTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _latestFailure =
                        $"residency-tick:{ex.GetType().Name}:{ex.Message}";
                }
                _tickTask = null;
            }
            if (_tickTask != null)
                return;

            CancellationToken token = _lifetimeCancellation.Token;
            _tickTask = Task.Run(
                async () => await _coordinator.TickAsync(
                        submissionSerial,
                        completedSerial,
                        token)
                    .ConfigureAwait(false),
                token);
        }
    }

    public void RecordFrameUploads(
        CommandBuffer commandBuffer,
        int frameSlot,
        ulong submissionSerial,
        Fence immutableRetirementFence = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
        Initialize();
        ValidateFrame(frameSlot);

        var writtenBuffers = new HashSet<BufferHandle>();
        ulong recordedBytes = 0;
        IReadOnlyList<MeshletPackedPageUpload> uploads =
            _uploader.CaptureUnrecordedUploads();
        foreach (MeshletPackedPageUpload upload in uploads)
        {
            try
            {
                (uint bankIndex, uint pageInBank) =
                    MeshletPhysicalBankAllocator.DecodeSlot(
                        upload.PhysicalSlot);
                BufferHandle bank = EnsurePhysicalBank(
                    checked((int)bankIndex));
                ulong destinationOffset = checked(
                    (ulong)pageInBank *
                    MeshletPhysicalBankAllocator.PageSizeBytes);
                GpuBufferUploader.UploadSpanToBuffer(
                    _context,
                    _bufferManager,
                    _stagingRing,
                    commandBuffer,
                    bank,
                    upload.PageBytes.Span,
                    destinationOffset);
                _uploader.MarkUploadRecorded(
                    upload.GlobalPageId,
                    upload.PhysicalSlot,
                    SaturatingAdd(submissionSerial, 1));
                writtenBuffers.Add(bank);
                recordedBytes = checked(
                    recordedBytes + (ulong)upload.PageBytes.Length);
                Interlocked.Increment(ref _recordedPageCount);
            }
            catch (Exception ex) when (
                ex is not StackOverflowException and
                not OutOfMemoryException)
            {
                _uploader.MarkUploadFailed(
                    upload.GlobalPageId,
                    upload.PhysicalSlot,
                    ex);
                _latestFailure =
                    $"physical-page-record:{ex.GetType().Name}:{ex.Message}";
                Interlocked.Increment(ref _failedPageRecordCount);
            }
        }

        MeshletPhysicalFrameStateSnapshot frameState =
            _uploader.CaptureFrameStateForRecording(frameSlot);
        GPUMeshletPageTableEntry[] pageTable = frameState.PageTable;
        ulong requiredPageTableBytes = Math.Max(
            MinimumStorageBufferBytes,
            checked((ulong)Math.Max(1, pageTable.Length) *
                (ulong)Marshal.SizeOf<GPUMeshletPageTableEntry>()));
        EnsureResizableBuffer(
            ref _pageTables[frameSlot],
            ref _pageTableBytes[frameSlot],
            requiredPageTableBytes,
            BindlessIndex.MeshletPhysicalPageTableBufferBase + frameSlot,
            $"Meshlet Physical Page Table [{frameSlot}]",
            immutableRetirementFence);
        FillZero(commandBuffer, _pageTables[frameSlot], _pageTableBytes[frameSlot]);
        if (pageTable.Length != 0)
        {
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _pageTables[frameSlot],
                pageTable);
        }
        writtenBuffers.Add(_pageTables[frameSlot]);

        uint[] rangeState = frameState.RangeStateWords;
        ulong requiredRangeStateBytes = Math.Max(
            MinimumStorageBufferBytes,
            checked((ulong)Math.Max(1, rangeState.Length) * sizeof(uint)));
        EnsureResizableBuffer(
            ref _rangeStates[frameSlot],
            ref _rangeStateBytes[frameSlot],
            requiredRangeStateBytes,
            BindlessIndex.MeshletStreamingRangeStateBufferBase + frameSlot,
            $"Meshlet Streaming Range State [{frameSlot}]",
            immutableRetirementFence);
        FillZero(commandBuffer, _rangeStates[frameSlot], _rangeStateBytes[frameSlot]);
        if (rangeState.Length != 0)
        {
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _rangeStates[frameSlot],
                rangeState);
        }
        writtenBuffers.Add(_rangeStates[frameSlot]);

        if (_uploader.TryCaptureImmutableContracts(
                out GPUMeshletVirtualMapping[] mappings,
                out GPUMeshletStreamingRange[] ranges,
                out ulong contractRevision))
        {
            try
            {
                UploadImmutableContracts(
                    commandBuffer,
                    mappings,
                    ranges,
                    immutableRetirementFence,
                    writtenBuffers);
                _uploader.MarkImmutableContractsRecorded(
                    contractRevision,
                    SaturatingAdd(submissionSerial, 1));
            }
            catch
            {
                _uploader.RestoreImmutableContractsDirty();
                throw;
            }
        }

        FillZero(
            commandBuffer,
            _demandBuffers[frameSlot],
            _demandBufferBytes[frameSlot]);
        uint[] demandHeader =
        [
            0u,
            checked((uint)_options.MaximumRequestsPerSerial),
            checked((uint)_streamingRangeCount),
            0u
        ];
        GpuBufferUploader.UploadSpanToBuffer(
            _context,
            _bufferManager,
            _stagingRing,
            commandBuffer,
            _demandBuffers[frameSlot],
            demandHeader);
        FillZero(
            commandBuffer,
            _feedbackBuffers[frameSlot],
            FeedbackCounterBytes);
        writtenBuffers.Add(_demandBuffers[frameSlot]);
        writtenBuffers.Add(_feedbackBuffers[frameSlot]);

        BufferMemoryBarrier2[] barriers = writtenBuffers
            .Select(handle => BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(handle),
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                ConsumerStages,
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit))
            .ToArray();
        if (barriers.Length != 0)
            BarrierBuilder.ExecuteBarrier(commandBuffer, bufferBarriers: barriers);
        _lastRecordedUploadBytes = recordedBytes;
    }

    private void UploadImmutableContracts(
        CommandBuffer commandBuffer,
        GPUMeshletVirtualMapping[] mappings,
        GPUMeshletStreamingRange[] ranges,
        Fence retirementFence,
        HashSet<BufferHandle> writtenBuffers)
    {
        _streamingRangeCount = ranges.Length;
        EnsureDemandBuffers(
            ranges.Length,
            retirementFence);
        ulong mappingBytes = Math.Max(
            MinimumStorageBufferBytes,
            checked((ulong)Math.Max(1, mappings.Length) *
                (ulong)Marshal.SizeOf<GPUMeshletVirtualMapping>()));
        EnsureResizableBuffer(
            ref _virtualMappings,
            ref _virtualMappingBytes,
            mappingBytes,
            BindlessIndex.MeshletVirtualMappingBuffer,
            "Meshlet Virtual Mapping Table",
            retirementFence);
        FillZero(commandBuffer, _virtualMappings, _virtualMappingBytes);
        if (mappings.Length != 0)
        {
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _virtualMappings,
                mappings);
        }
        writtenBuffers.Add(_virtualMappings);

        ulong rangeBytes = Math.Max(
            MinimumStorageBufferBytes,
            checked((ulong)Math.Max(1, ranges.Length) *
                (ulong)Marshal.SizeOf<GPUMeshletStreamingRange>()));
        EnsureResizableBuffer(
            ref _streamingRanges,
            ref _streamingRangeBytes,
            rangeBytes,
            BindlessIndex.MeshletStreamingRangeBuffer,
            "Meshlet Streaming Range Table",
            retirementFence);
        FillZero(commandBuffer, _streamingRanges, _streamingRangeBytes);
        if (ranges.Length != 0)
        {
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _streamingRanges,
                ranges);
        }
        writtenBuffers.Add(_streamingRanges);
    }

    private void EnsureDemandBuffers(
        int rangeCount,
        Fence retirementFence)
    {
        ulong requiredBytes = CalculateDemandBufferBytes(
            _options.MaximumRequestsPerSerial,
            rangeCount);
        for (int frame = 0; frame < FrameCount; frame++)
        {
            EnsureResizableBuffer(
                ref _demandBuffers[frame],
                ref _demandBufferBytes[frame],
                requiredBytes,
                BindlessIndex.MeshletStreamingDemandBufferBase + frame,
                $"Meshlet Streaming Demand [{frame}]",
                retirementFence,
                BufferUsageFlags.TransferSrcBit);
        }
    }

    /// <summary>
    /// Records the compact demand header/keys and feedback counters after all
    /// request-producing passes. The mapped buffers are consumed only after
    /// this frame slot's fence completes on its next reuse.
    /// </summary>
    public unsafe void RecordFeedbackReadback(
        CommandBuffer commandBuffer,
        int frameSlot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (commandBuffer.Handle == 0)
            throw new ArgumentException(
                "A valid command buffer is required.",
                nameof(commandBuffer));
        Initialize();
        ValidateFrame(frameSlot);
        EnsureReadbackBuffers(frameSlot);

        VkBuffer demandSource = _bufferManager.GetBuffer(
            _demandBuffers[frameSlot]);
        VkBuffer feedbackSource = _bufferManager.GetBuffer(
            _feedbackBuffers[frameSlot]);
        ulong demandReadbackBytes = CalculateDemandReadbackBytes(
            _options.MaximumRequestsPerSerial);
        BufferMemoryBarrier2[] beforeCopy =
        [
            BarrierBuilder.BufferBarrier(
                demandSource,
                ConsumerStages,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                demandReadbackBytes),
            BarrierBuilder.BufferBarrier(
                feedbackSource,
                ConsumerStages,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                0,
                FeedbackCounterBytes)
        ];
        BarrierBuilder.ExecuteBarrier(
            commandBuffer,
            bufferBarriers: beforeCopy);

        BufferCopy demandCopy = new()
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = demandReadbackBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            demandSource,
            _bufferManager.GetBuffer(_demandReadbackBuffers[frameSlot]),
            1,
            &demandCopy);
        BufferCopy feedbackCopy = new()
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = FeedbackCounterBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            feedbackSource,
            _bufferManager.GetBuffer(_feedbackReadbackBuffers[frameSlot]),
            1,
            &feedbackCopy);

        BufferMemoryBarrier2[] hostBarriers =
        [
            BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(
                    _demandReadbackBuffers[frameSlot]),
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                demandReadbackBytes),
            BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(
                    _feedbackReadbackBuffers[frameSlot]),
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                FeedbackCounterBytes)
        ];
        BarrierBuilder.ExecuteBarrier(
            commandBuffer,
            bufferBarriers: hostBarriers);
        _readbackRecorded[frameSlot] = true;
    }

    private unsafe void ConsumeCompletedFeedback(
        int frameSlot,
        ulong requestSerial)
    {
        if (!_readbackRecorded[frameSlot])
            return;

        ulong demandBytes = CalculateDemandReadbackBytes(
            _options.MaximumRequestsPerSerial);
        _bufferManager.InvalidateBuffer(
            _demandReadbackBuffers[frameSlot],
            0,
            demandBytes);
        _bufferManager.InvalidateBuffer(
            _feedbackReadbackBuffers[frameSlot],
            0,
            FeedbackCounterBytes);
        uint* demandWords = (uint*)_bufferManager.GetMappedPointer(
            _demandReadbackBuffers[frameSlot]);
        uint* feedbackWords = (uint*)_bufferManager.GetMappedPointer(
            _feedbackReadbackBuffers[frameSlot]);
        uint rawCount = demandWords[0];
        uint encodedCapacity = demandWords[1];
        uint capacity = Math.Min(
            encodedCapacity,
            checked((uint)_options.MaximumRequestsPerSerial));
        uint count = Math.Min(rawCount, capacity);
        var uniqueKeys = new HashSet<uint>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            uint rangeIndex = demandWords[
                DemandHeaderWordCount + index];
            if (!uniqueKeys.Add(rangeIndex))
                continue;
            _coordinator.RequestGlobalRange(
                rangeIndex,
                MeshletStreamingResidencyCoordinator.VisiblePriority,
                requestSerial);
        }

        uint shaderOverflow = feedbackWords[FeedbackOverflowWord];
        uint countOverflow = rawCount > capacity
            ? rawCount - capacity
            : 0u;
        Interlocked.Add(
            ref _completedDemandKeyCount,
            uniqueKeys.Count);
        Interlocked.Add(
            ref _demandOverflowCount,
            Math.Max(shaderOverflow, countOverflow));
        Interlocked.Add(
            ref _invalidShaderMappingCount,
            feedbackWords[FeedbackInvalidMappingWord]);
        _readbackRecorded[frameSlot] = false;
    }

    private void EnsureReadbackBuffers(int frameSlot)
    {
        if (_demandReadbackBuffers[frameSlot].IsValid)
            return;
        ulong demandBytes = CalculateDemandReadbackBytes(
            _options.MaximumRequestsPerSerial);
        _demandReadbackBuffers[frameSlot] = _bufferManager.CreateBuffer(
            demandBytes,
            BufferUsageFlags.TransferDstBit,
            Vma.MemoryUsage.AutoPreferHost,
            Vma.AllocationCreateFlags.MappedBit |
            Vma.AllocationCreateFlags.HostAccessRandomBit,
            $"Meshlet Streaming Demand Readback [{frameSlot}]",
            MemoryBudgetCategory.DiagnosticsAndDebug);
        _feedbackReadbackBuffers[frameSlot] = _bufferManager.CreateBuffer(
            FeedbackCounterBytes,
            BufferUsageFlags.TransferDstBit,
            Vma.MemoryUsage.AutoPreferHost,
            Vma.AllocationCreateFlags.MappedBit |
            Vma.AllocationCreateFlags.HostAccessRandomBit,
            $"Meshlet Streaming Feedback Readback [{frameSlot}]",
            MemoryBudgetCategory.DiagnosticsAndDebug);
    }

    private BufferHandle EnsurePhysicalBank(int bankIndex)
    {
        if ((uint)bankIndex >= (uint)_banks.Length)
            throw new ArgumentOutOfRangeException(nameof(bankIndex));
        if (_banks[bankIndex].IsValid)
            return _banks[bankIndex];

        BufferHandle bank = CreateStorageBuffer(
            MeshletPhysicalBankAllocator.BankSizeBytes,
            $"Meshlet Physical Page Bank [{bankIndex}]");
        RegisterBuffer(
            BindlessIndex.MeshletPhysicalPageBankBufferBase + bankIndex,
            bank,
            MeshletPhysicalBankAllocator.BankSizeBytes);
        _banks[bankIndex] = bank;
        return bank;
    }

    private void EnsureResizableBuffer(
        ref BufferHandle handle,
        ref ulong capacityBytes,
        ulong requiredBytes,
        int bindlessIndex,
        string name,
        Fence retirementFence,
        BufferUsageFlags additionalUsage = BufferUsageFlags.None)
    {
        if (handle.IsValid && capacityBytes >= requiredBytes)
            return;
        ulong newBytes = NextPowerOfTwo(Math.Max(
            MinimumStorageBufferBytes,
            requiredBytes));
        BufferHandle replacement = CreateStorageBuffer(
            newBytes,
            name,
            additionalUsage);
        RegisterBuffer(bindlessIndex, replacement, newBytes);
        BufferHandle previous = handle;
        handle = replacement;
        capacityBytes = newBytes;
        Retire(previous, retirementFence);
    }

    private BufferHandle CreateStorageBuffer(
        ulong bytes,
        string name,
        BufferUsageFlags additionalUsage = BufferUsageFlags.None) =>
        _bufferManager.CreateDeviceBuffer(
            bytes,
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.TransferDstBit |
            additionalUsage,
            requireDeviceAddress: false,
            MemoryBudgetCategory.MeshBuffers,
            name);

    private void RegisterBuffer(int index, BufferHandle handle, ulong bytes)
    {
        _bindlessHeap.RegisterStorageBuffer(
            index,
            _bufferManager.GetBuffer(handle),
            0,
            bytes);
    }

    private void FillZero(
        CommandBuffer commandBuffer,
        BufferHandle handle,
        ulong bytes)
    {
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(handle),
            0,
            bytes,
            0);
    }

    private void Retire(BufferHandle handle, Fence fence)
    {
        if (!handle.IsValid)
            return;
        if (_deleter != null && fence.Handle != 0)
        {
            _deleter.QueueBufferDeletion(fence, handle, _bufferManager);
            return;
        }
        _immediateRetirements.Add(handle);
    }

    public VulkanMeshletPhysicalResidencySnapshot CreateSnapshot()
    {
        int bankCount = _banks.Count(static handle => handle.IsValid);
        return new VulkanMeshletPhysicalResidencySnapshot(
            _initialized,
            bankCount,
            checked((ulong)bankCount *
                MeshletPhysicalBankAllocator.BankSizeBytes),
            _lastRecordedUploadBytes,
            Interlocked.Read(ref _recordedPageCount),
            Interlocked.Read(ref _failedPageRecordCount),
            Interlocked.Read(ref _completedDemandKeyCount),
            Interlocked.Read(ref _demandOverflowCount),
            Interlocked.Read(ref _invalidShaderMappingCount),
            _latestFailure);
    }

    private static void ValidateFrame(int frameSlot)
    {
        if ((uint)frameSlot >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
    }

    private static ulong NextPowerOfTwo(ulong value)
    {
        if (value <= 1)
            return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return checked(value + 1);
    }

    private static ulong CalculateDemandBufferBytes(
        int maximumRequests,
        int rangeCount)
    {
        if (maximumRequests <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        if (rangeCount < 0)
            throw new ArgumentOutOfRangeException(nameof(rangeCount));
        ulong stampWords = checked(((ulong)rangeCount + 31UL) / 32UL);
        ulong words = checked(
            (ulong)DemandHeaderWordCount +
            (ulong)maximumRequests +
            stampWords);
        return Math.Max(
            MinimumStorageBufferBytes,
            checked(words * sizeof(uint)));
    }

    private static ulong CalculateDemandReadbackBytes(
        int maximumRequests) =>
        checked(((ulong)DemandHeaderWordCount +
            (ulong)maximumRequests) * sizeof(uint));

    private static ulong SaturatingAdd(ulong value, ulong addition) =>
        value > ulong.MaxValue - addition
            ? ulong.MaxValue
            : value + addition;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        Task? tick;
        lock (_tickLock)
            tick = _tickTask;
        try
        {
            tick?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _lifetimeCancellation.Dispose();

        foreach (BufferHandle handle in _pageTables)
            Destroy(handle);
        foreach (BufferHandle handle in _rangeStates)
            Destroy(handle);
        foreach (BufferHandle handle in _demandBuffers)
            Destroy(handle);
        foreach (BufferHandle handle in _feedbackBuffers)
            Destroy(handle);
        foreach (BufferHandle handle in _demandReadbackBuffers)
            Destroy(handle);
        foreach (BufferHandle handle in _feedbackReadbackBuffers)
            Destroy(handle);
        foreach (BufferHandle handle in _banks)
            Destroy(handle);
        Destroy(_virtualMappings);
        Destroy(_streamingRanges);
        foreach (BufferHandle handle in _immediateRetirements)
            Destroy(handle);
        _immediateRetirements.Clear();
    }

    private void Destroy(BufferHandle handle)
    {
        if (handle.IsValid)
            _bufferManager.DestroyBuffer(handle);
    }
}
