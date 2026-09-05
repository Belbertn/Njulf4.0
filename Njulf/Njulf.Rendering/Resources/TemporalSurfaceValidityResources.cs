using System;
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
/// Lazy, double-buffered full-resolution surface-validity history. Allocation
/// is owned independently from any one temporal consumer.
/// </summary>
public sealed unsafe class TemporalSurfaceValidityResources : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferHandle[] _buffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private bool _disposed;

    public TemporalSurfaceValidityResources(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
    }

    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint ResourceGeneration { get; private set; }
    public ulong BufferBytes { get; private set; }
    public ulong EstimatedBytes => BufferBytes * RenderingConstants.FramesInFlight;
    public bool IsAllocated => Width != 0u && Height != 0u && AllValid(_buffers);

    public bool IsCompatible(uint width, uint height) =>
        IsAllocated && Width == width && Height == height;

    public BufferHandle GetBuffer(int frameIndex) =>
        (uint)frameIndex < (uint)_buffers.Length
            ? _buffers[frameIndex]
            : BufferHandle.Invalid;

    public bool Ensure(uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width == 0u || height == 0u)
            return false;
        if (IsCompatible(width, height))
            return true;

        ulong bytes = checked(
            (ulong)width * height * TemporalSurfaceValidityCodec.BytesPerPixel);
        if (bytes == 0UL ||
            _context.MaximumStorageBufferRange != 0UL &&
            bytes > _context.MaximumStorageBufferRange)
        {
            throw new InvalidOperationException(
                $"Temporal surface validity requires {bytes} bytes per bank; " +
                $"maximum storage-buffer range is {_context.MaximumStorageBufferRange} bytes.");
        }

        var replacements = new BufferHandle[RenderingConstants.FramesInFlight];
        try
        {
            for (int frame = 0; frame < replacements.Length; frame++)
            {
                replacements[frame] = _bufferManager.CreateBuffer(
                    bytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferDevice,
                    debugName: $"Temporal surface validity frame {frame}",
                    category: MemoryBudgetCategory.RenderTargets);
            }

            if (IsAllocated)
            {
                Result idle = _context.Api.DeviceWaitIdle(_context.Device);
                if (idle != Result.Success)
                    throw new VulkanException(
                        "Failed to wait before replacing temporal surface validity history",
                        idle);
            }

            Destroy(_buffers);
            Array.Copy(replacements, _buffers, replacements.Length);
            Array.Fill(replacements, BufferHandle.Invalid);
            Width = width;
            Height = height;
            BufferBytes = bytes;
            ResourceGeneration = ResourceGeneration == uint.MaxValue
                ? 1u
                : Math.Max(1u, ResourceGeneration + 1u);
            for (int frame = 0; frame < _buffers.Length; frame++)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.TemporalSurfaceValidityBufferBase + frame,
                    _bufferManager.GetBuffer(_buffers[frame]),
                    0UL,
                    bytes);
            }
            return true;
        }
        catch
        {
            Destroy(replacements);
            throw;
        }
    }

    /// <summary>Clears the current seed bank before motion fragments publish it.</summary>
    public void PrepareForMotionSeed(CommandBuffer commandBuffer, int frameIndex)
    {
        BufferHandle handle = GetBuffer(frameIndex);
        if (!handle.IsValid || BufferBytes == 0UL)
            return;

        VkBuffer buffer = _bufferManager.GetBuffer(handle);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            BufferBytes);
        Execute(commandBuffer, barrier);
        _context.Api.CmdFillBuffer(commandBuffer, buffer, 0UL, BufferBytes, 0u);
        barrier = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            0UL,
            BufferBytes);
        Execute(commandBuffer, barrier);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Destroy(_buffers);
        Width = 0u;
        Height = 0u;
        BufferBytes = 0UL;
    }

    private void Destroy(Span<BufferHandle> buffers)
    {
        for (int index = 0; index < buffers.Length; index++)
        {
            if (buffers[index].IsValid)
                _bufferManager.DestroyBuffer(buffers[index]);
            buffers[index] = BufferHandle.Invalid;
        }
    }

    private static bool AllValid(ReadOnlySpan<BufferHandle> buffers)
    {
        foreach (BufferHandle buffer in buffers)
        {
            if (!buffer.IsValid)
                return false;
        }
        return true;
    }

    private void Execute(
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
}
