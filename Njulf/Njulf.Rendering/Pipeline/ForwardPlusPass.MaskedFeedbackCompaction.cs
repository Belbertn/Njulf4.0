using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe partial class ForwardPlusPass
{
    private readonly BufferHandle[] _maskedFeedbackCompactBuffers =
        new BufferHandle[FramesInFlight];
    private readonly BufferHandle[] _maskedFeedbackCompactReadbackBuffers =
        new BufferHandle[FramesInFlight];
    private readonly bool[] _maskedFeedbackCompactReadbackRecorded =
        new bool[FramesInFlight];

    private ulong _maskedFeedbackCompactBufferBytes;
    private uint _maskedFeedbackCompactPhysicalCapacity;
    private uint _maskedFeedbackCompactLogicalCapacity;
    private uint _maskedFeedbackCompactObservedHighWater;
    private bool _maskedFeedbackCompactionActiveForCurrentView;
    private SimpleDdgiMaskedFeedbackCompactionCounters
        _maskedFeedbackCompactCounters =
            SimpleDdgiMaskedFeedbackCompactionCounters.Unavailable;

    internal ulong SimpleDdgiMaskedFeedbackCompactTotalBytes
    {
        get
        {
            ulong bytes = 0UL;
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (_maskedFeedbackCompactBuffers[i].IsValid)
                    bytes = checked(bytes + _maskedFeedbackCompactBufferBytes);
            }
            return bytes;
        }
    }

    private void RecreateSimpleDdgiMaskedFeedbackCompactResources(
        Extent2D extent)
    {
        if (_bufferManager is null || extent.Width == 0u || extent.Height == 0u)
            return;

        uint tileWidth = DivideRoundUp(
            extent.Width,
            SimpleDdgiReceiverGatherScale);
        uint tileHeight = DivideRoundUp(
            extent.Height,
            SimpleDdgiReceiverGatherScale);
        uint tileCount = checked(tileWidth * tileHeight);
        uint physicalCapacity =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolvePhysicalCapacity(
                tileCount);
        ulong bufferBytes =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolveBufferBytes(
                physicalCapacity);
        bool matches =
            physicalCapacity == _maskedFeedbackCompactPhysicalCapacity &&
            bufferBytes == _maskedFeedbackCompactBufferBytes;
        for (int i = 0; i < FramesInFlight; ++i)
        {
            matches &= _maskedFeedbackCompactBuffers[i].IsValid &&
                _maskedFeedbackCompactReadbackBuffers[i].IsValid;
        }
        if (matches)
            return;

        var compactReplacements = new BufferHandle[FramesInFlight];
        var readbackReplacements = new BufferHandle[FramesInFlight];
        Array.Fill(compactReplacements, BufferHandle.Invalid);
        Array.Fill(readbackReplacements, BufferHandle.Invalid);
        try
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                compactReplacements[i] = _bufferManager.CreateDeviceBuffer(
                    bufferBytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    $"Simple DDGI Masked Feedback Compact List Frame {i}");
                readbackReplacements[i] = _bufferManager.CreateBuffer(
                    SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes,
                    BufferUsageFlags.TransferDstBit,
                    Vma.MemoryUsage.AutoPreferHost,
                    Vma.AllocationCreateFlags.MappedBit |
                    Vma.AllocationCreateFlags.HostAccessRandomBit,
                    $"Simple DDGI Masked Feedback Readback Frame {i}",
                    MemoryBudgetCategory.DiagnosticsAndDebug);
                if (_bufferManager.GetMappedPointer(readbackReplacements[i]) ==
                    null)
                {
                    throw new InvalidOperationException(
                        "Masked-feedback high-water readback is not mapped.");
                }
            }

            for (int i = 0; i < FramesInFlight; ++i)
            {
                VkBuffer compact =
                    _bufferManager.GetBuffer(compactReplacements[i]);
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.SimpleDdgiMaskedFeedbackCompactBufferBase +
                        i,
                    compact,
                    0UL,
                    bufferBytes);
            }
        }
        catch
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (compactReplacements[i].IsValid)
                    _bufferManager.DestroyBuffer(compactReplacements[i]);
                if (readbackReplacements[i].IsValid)
                    _bufferManager.DestroyBuffer(readbackReplacements[i]);
            }
            throw;
        }

        for (int i = 0; i < FramesInFlight; ++i)
        {
            BufferHandle oldCompact = _maskedFeedbackCompactBuffers[i];
            BufferHandle oldReadback =
                _maskedFeedbackCompactReadbackBuffers[i];
            _maskedFeedbackCompactBuffers[i] = compactReplacements[i];
            _maskedFeedbackCompactReadbackBuffers[i] =
                readbackReplacements[i];
            if (oldCompact.IsValid)
                _bufferManager.DestroyBuffer(oldCompact);
            if (oldReadback.IsValid)
                _bufferManager.DestroyBuffer(oldReadback);
        }

        _maskedFeedbackCompactPhysicalCapacity = physicalCapacity;
        _maskedFeedbackCompactBufferBytes = bufferBytes;
        _maskedFeedbackCompactLogicalCapacity =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolveLogicalCapacity(
                _maskedFeedbackCompactObservedHighWater,
                0u,
                physicalCapacity);
        Array.Clear(_maskedFeedbackCompactReadbackRecorded);
        _maskedFeedbackCompactCounters =
            SimpleDdgiMaskedFeedbackCompactionCounters.Unavailable;
    }

    private bool PrepareSimpleDdgiMaskedFeedbackCompaction(
        CommandBuffer commandBuffer,
        int frameIndex,
        bool alphaMaskProducerRequired)
    {
        _maskedFeedbackCompactionActiveForCurrentView = false;
        if (!_settings.IsPerformanceOptimizationEnabled(
                PerformanceOptimizationFeature.CompactMaskedFeedback) ||
            _bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_maskedFeedbackCompactBuffers[frameIndex].IsValid)
        {
            return false;
        }

        ObserveCompletedSimpleDdgiMaskedFeedbackReadback(frameIndex);
        _maskedFeedbackCompactLogicalCapacity =
            SimpleDdgiMaskedFeedbackCompactionAbi.ResolveLogicalCapacity(
                _maskedFeedbackCompactObservedHighWater,
                _maskedFeedbackCompactLogicalCapacity,
                _maskedFeedbackCompactPhysicalCapacity);
        bool active = alphaMaskProducerRequired &&
            _simpleDdgiMaskedFeedbackCompactPipeline.Handle != 0 &&
            _maskedFeedbackCompactLogicalCapacity != 0u;

        Span<uint> header = stackalloc uint[checked((int)
            SimpleDdgiMaskedFeedbackCompactionAbi.HeaderWords)];
        header.Clear();
        header[checked((int)
            SimpleDdgiMaskedFeedbackCompactionAbi.StateWord)] =
            SimpleDdgiMaskedFeedbackCompactionAbi.PackState(
                _maskedFeedbackCompactLogicalCapacity,
                active);
        VkBuffer compact = _bufferManager.GetBuffer(
            _maskedFeedbackCompactBuffers[frameIndex]);
        _context.Api.CmdUpdateBuffer(
            commandBuffer,
            compact,
            0UL,
            header);
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = compact,
            Offset = 0UL,
            Size = _maskedFeedbackCompactBufferBytes
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        _maskedFeedbackCompactionActiveForCurrentView = active;
        return active;
    }

    private void RecordSimpleDdgiMaskedFeedbackCompaction(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        Extent2D renderExtent,
        GpuTimestampRecorder? timestamps)
    {
        if (!_maskedFeedbackCompactionActiveForCurrentView ||
            _bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_maskedFeedbackCompactBuffers[frameIndex].IsValid ||
            !_maskedFeedbackCompactReadbackBuffers[frameIndex].IsValid ||
            _simpleDdgiMaskedFeedbackCompactPipeline.Handle == 0)
        {
            _maskedFeedbackCompactionActiveForCurrentView = false;
            return;
        }

        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            "SimpleDdgiMaskedFeedbackCompactionPass");
        try
        {
            BufferHandle compactHandle =
                _maskedFeedbackCompactBuffers[frameIndex];
            VkBuffer compact = _bufferManager.GetBuffer(compactHandle);
            var rasterToCompute = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.FragmentShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = compact,
                Offset = 0UL,
                Size = _maskedFeedbackCompactBufferBytes
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1u,
                PBufferMemoryBarriers = &rasterToCompute
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);

            _context.Api.CmdBindPipeline(
                commandBuffer,
                PipelineBindPoint.Compute,
                _simpleDdgiMaskedFeedbackCompactPipeline);
            BindBindlessStorageAndTextures(
                commandBuffer,
                _simpleDdgiReceiverCachePipelineLayout,
                PipelineBindPoint.Compute);
            var push =
                new GPUSimpleDdgiMaskedFeedbackCompactPushConstants
                {
                    CameraPositionAndPadding =
                        new Vector4(sceneData.CameraPosition, 0.0f),
                    CurrentFrameIndex = sceneData.CurrentFrameIndex,
                    ScreenWidth = renderExtent.Width,
                    ScreenHeight = renderExtent.Height,
                    ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
                    CompactBufferIndex = checked((uint)
                        (BindlessIndex
                            .SimpleDdgiMaskedFeedbackCompactBufferBase +
                         frameIndex))
                };
            _context.Api.CmdPushConstants(
                commandBuffer,
                _simpleDdgiReceiverCachePipelineLayout,
                ShaderStageFlags.ComputeBit,
                0u,
                (uint)Marshal.SizeOf<
                    GPUSimpleDdgiMaskedFeedbackCompactPushConstants>(),
                &push);
            _context.Api.CmdDispatch(
                commandBuffer,
                DivideRoundUp(
                    _maskedFeedbackCompactLogicalCapacity,
                    SimpleDdgiMaskedFeedbackCompactionAbi.WorkgroupSize),
                1u,
                1u);

            var compactToReadback = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.FragmentShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.TransferBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = compact,
                Offset = 0UL,
                Size = SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes
            };
            dependency.PBufferMemoryBarriers = &compactToReadback;
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);

            VkBuffer readback = _bufferManager.GetBuffer(
                _maskedFeedbackCompactReadbackBuffers[frameIndex]);
            var copy = new BufferCopy
            {
                SrcOffset = 0UL,
                DstOffset = 0UL,
                Size = SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes
            };
            _context.Api.CmdCopyBuffer(
                commandBuffer,
                compact,
                readback,
                1u,
                &copy);
            var readbackToHost = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit,
                DstAccessMask = AccessFlags2.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = readback,
                Offset = 0UL,
                Size = SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes
            };
            dependency.PBufferMemoryBarriers = &readbackToHost;
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
            _maskedFeedbackCompactReadbackRecorded[frameIndex] = true;
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
            _maskedFeedbackCompactionActiveForCurrentView = false;
        }
    }

    private void ObserveCompletedSimpleDdgiMaskedFeedbackReadback(
        int frameIndex)
    {
        if (_bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_maskedFeedbackCompactReadbackRecorded[frameIndex] ||
            !_maskedFeedbackCompactReadbackBuffers[frameIndex].IsValid)
        {
            return;
        }

        BufferHandle readback =
            _maskedFeedbackCompactReadbackBuffers[frameIndex];
        _bufferManager.InvalidateBuffer(
            readback,
            0UL,
            SimpleDdgiMaskedFeedbackCompactionAbi.HeaderBytes);
        uint* words = (uint*)_bufferManager.GetMappedPointer(readback);
        if (words == null ||
            (words[SimpleDdgiMaskedFeedbackCompactionAbi.StateWord] &
             SimpleDdgiMaskedFeedbackCompactionAbi.InitializedBit) == 0u)
        {
            _maskedFeedbackCompactCounters =
                SimpleDdgiMaskedFeedbackCompactionCounters.Unavailable;
        }
        else
        {
            uint highWater =
                words[SimpleDdgiMaskedFeedbackCompactionAbi
                    .CandidateHighWaterWord];
            _maskedFeedbackCompactObservedHighWater = Math.Max(
                _maskedFeedbackCompactObservedHighWater,
                highWater);
            _maskedFeedbackCompactCounters =
                new SimpleDdgiMaskedFeedbackCompactionCounters(
                    1,
                    words[SimpleDdgiMaskedFeedbackCompactionAbi
                        .PublishedCountWord],
                    words[SimpleDdgiMaskedFeedbackCompactionAbi
                        .OverflowFallbackCountWord],
                    highWater,
                    words[SimpleDdgiMaskedFeedbackCompactionAbi.StateWord] &
                        SimpleDdgiMaskedFeedbackCompactionAbi.CapacityMask,
                    _maskedFeedbackCompactObservedHighWater,
                    _maskedFeedbackCompactBufferBytes);
        }
        _maskedFeedbackCompactReadbackRecorded[frameIndex] = false;
    }

    private void CleanupSimpleDdgiMaskedFeedbackCompactResources()
    {
        _maskedFeedbackCompactionActiveForCurrentView = false;
        if (_bufferManager is not null)
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (_maskedFeedbackCompactBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(
                        _maskedFeedbackCompactBuffers[i]);
                if (_maskedFeedbackCompactReadbackBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(
                        _maskedFeedbackCompactReadbackBuffers[i]);
            }
        }
        Array.Fill(_maskedFeedbackCompactBuffers, BufferHandle.Invalid);
        Array.Fill(
            _maskedFeedbackCompactReadbackBuffers,
            BufferHandle.Invalid);
        Array.Clear(_maskedFeedbackCompactReadbackRecorded);
        _maskedFeedbackCompactBufferBytes = 0UL;
        _maskedFeedbackCompactPhysicalCapacity = 0u;
        _maskedFeedbackCompactLogicalCapacity = 0u;
        _maskedFeedbackCompactObservedHighWater = 0u;
        _maskedFeedbackCompactCounters =
            SimpleDdgiMaskedFeedbackCompactionCounters.Unavailable;
    }
}
