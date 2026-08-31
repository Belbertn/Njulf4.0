using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline;

public sealed unsafe partial class ForwardPlusPass
{
    private readonly BufferHandle[] _sparseHybridLobePayloadBuffers =
        new BufferHandle[FramesInFlight];
    private ulong _sparseHybridLobePayloadBufferBytes;
    private uint _sparseHybridLobePayloadWidth;
    private uint _sparseHybridLobePayloadHeight;
    private bool _sparseHybridLobePayloadAvailable;

    internal bool SparseHybridLobePayloadAvailable =>
        !_settings.IsPerformanceOptimizationEnabled(
            PerformanceOptimizationFeature.SparseHybridLobePayload) ||
        _sparseHybridLobePayloadAvailable;

    internal ulong SparseHybridLobePayloadTotalBytes
    {
        get
        {
            uint validBufferCount = 0u;
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (_sparseHybridLobePayloadBuffers[i].IsValid)
                    ++validBufferCount;
            }
            return checked(
                _sparseHybridLobePayloadBufferBytes * validBufferCount);
        }
    }

    private bool UsesSparseHybridLobePayload =>
        _settings.IsPerformanceOptimizationEnabled(
            PerformanceOptimizationFeature.SparseHybridLobePayload);

    private void RecreateSparseHybridLobePayloadResources(Extent2D extent)
    {
        if (!UsesSparseHybridLobePayload)
        {
            CleanupSparseHybridLobePayloadResources();
            _sparseHybridLobePayloadAvailable = true;
            return;
        }
        if (_bufferManager is null || extent.Width == 0u || extent.Height == 0u)
        {
            _sparseHybridLobePayloadAvailable = false;
            return;
        }

        ulong bytes = HybridReflectionSparseLobePayloadAbi.ResolveBufferBytes(
            extent.Width,
            extent.Height);
        bool matches =
            _sparseHybridLobePayloadWidth == extent.Width &&
            _sparseHybridLobePayloadHeight == extent.Height &&
            _sparseHybridLobePayloadBufferBytes == bytes;
        for (int i = 0; i < FramesInFlight; ++i)
            matches &= _sparseHybridLobePayloadBuffers[i].IsValid;
        if (matches)
        {
            _sparseHybridLobePayloadAvailable = true;
            return;
        }

        var replacements = new BufferHandle[FramesInFlight];
        Array.Fill(replacements, BufferHandle.Invalid);
        try
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                replacements[i] = _bufferManager.CreateDeviceBuffer(
                    bytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.RenderTargets,
                    $"Hybrid Reflection Sparse Lobe Payload Frame {i}");
            }

            for (int i = 0; i < FramesInFlight; ++i)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.HybridReflectionSparseLobeBufferBase + i,
                    _bufferManager.GetBuffer(replacements[i]),
                    0UL,
                    bytes);
            }
        }
        catch (Exception exception)
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (replacements[i].IsValid)
                    _bufferManager.DestroyBuffer(replacements[i]);
            }
            _sparseHybridLobePayloadAvailable = false;
            HybridReflectionReceiverFailureReason =
                "hybrid-reflection-sparse-lobe-allocation-failed:" +
                exception.GetType().Name;
            System.Diagnostics.Debug.WriteLine(
                $"Sparse hybrid-lobe payload unavailable; deferred " +
                $"reflections remain fail-closed: {exception.GetType().Name}: " +
                exception.Message);
            return;
        }

        for (int i = 0; i < FramesInFlight; ++i)
        {
            BufferHandle old = _sparseHybridLobePayloadBuffers[i];
            _sparseHybridLobePayloadBuffers[i] = replacements[i];
            if (old.IsValid)
                _bufferManager.DestroyBuffer(old);
        }
        _sparseHybridLobePayloadWidth = extent.Width;
        _sparseHybridLobePayloadHeight = extent.Height;
        _sparseHybridLobePayloadBufferBytes = bytes;
        _sparseHybridLobePayloadAvailable = true;
    }

    private bool PrepareSparseHybridLobePayload(
        CommandBuffer commandBuffer,
        int frameIndex,
        Extent2D extent)
    {
        if (!UsesSparseHybridLobePayload ||
            !_sparseHybridLobePayloadAvailable ||
            _bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            extent.Width != _sparseHybridLobePayloadWidth ||
            extent.Height != _sparseHybridLobePayloadHeight ||
            !_sparseHybridLobePayloadBuffers[frameIndex].IsValid)
        {
            return false;
        }

        VkBuffer buffer = _bufferManager.GetBuffer(
            _sparseHybridLobePayloadBuffers[frameIndex]);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            buffer,
            0UL,
            _sparseHybridLobePayloadBufferBytes,
            0u);
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.FragmentShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0UL,
            Size = _sparseHybridLobePayloadBufferBytes
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        return true;
    }

    private void PublishSparseHybridLobePayload(
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (!UsesSparseHybridLobePayload ||
            !_sparseHybridLobePayloadAvailable ||
            _bufferManager is null || frameIndex < 0 ||
            frameIndex >= FramesInFlight ||
            !_sparseHybridLobePayloadBuffers[frameIndex].IsValid)
        {
            return;
        }

        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.FragmentShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _bufferManager.GetBuffer(
                _sparseHybridLobePayloadBuffers[frameIndex]),
            Offset = 0UL,
            Size = _sparseHybridLobePayloadBufferBytes
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void CleanupSparseHybridLobePayloadResources()
    {
        if (_bufferManager is not null)
        {
            for (int i = 0; i < FramesInFlight; ++i)
            {
                if (_sparseHybridLobePayloadBuffers[i].IsValid)
                {
                    _bufferManager.DestroyBuffer(
                        _sparseHybridLobePayloadBuffers[i]);
                }
            }
        }
        Array.Fill(_sparseHybridLobePayloadBuffers, BufferHandle.Invalid);
        _sparseHybridLobePayloadBufferBytes = 0UL;
        _sparseHybridLobePayloadWidth = 0u;
        _sparseHybridLobePayloadHeight = 0u;
        _sparseHybridLobePayloadAvailable = !UsesSparseHybridLobePayload;
    }
}
