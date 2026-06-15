using System;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.GpuScene;

public sealed class GpuSceneBufferSet : IDisposable
{
    private readonly VulkanContext? _context;
    private readonly BufferManager? _bufferManager;
    private readonly StagingRing? _stagingRing;
    private readonly object _lock = new();
    private readonly List<RetiredBuffer> _retiredBuffers = new();
    private BindlessHeap? _bindlessHeap;
    private bool _disposed;

    public GpuSceneBufferSet(int initialObjectCapacity = 1024, int initialInstanceCapacity = 2048)
    {
        ObjectCapacity = Math.Max(1, initialObjectCapacity);
        InstanceCapacity = Math.Max(1, initialInstanceCapacity);
    }

    public GpuSceneBufferSet(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        int initialObjectCapacity = 1024,
        int initialInstanceCapacity = 2048)
        : this(initialObjectCapacity, initialInstanceCapacity)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        CreateBuffers();
    }

    public int ObjectCapacity { get; private set; }
    public int InstanceCapacity { get; private set; }
    public int ObjectHighWaterMark { get; private set; }
    public int InstanceHighWaterMark { get; private set; }
    public int ObjectResizeCount { get; private set; }
    public int InstanceResizeCount { get; private set; }
    public ulong LastUploadBytes { get; private set; }
    public ulong TotalUploadBytes { get; private set; }
    public BufferHandle ObjectBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle InstanceBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle TransformBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle PreviousTransformBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle BoundsBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle VisibilityBuffer { get; private set; } = BufferHandle.Invalid;
    public BufferHandle CompactedIndexBuffer { get; private set; } = BufferHandle.Invalid;
    public ulong AllocatedBytes => ObjectBufferBytes + InstanceBufferBytes + TransformBufferBytes + PreviousTransformBufferBytes + BoundsBufferBytes + VisibilityBufferBytes + CompactedIndexBufferBytes;
    public ulong ObjectBufferBytes => ByteSize<GPUSceneObject>(ObjectCapacity);
    public ulong InstanceBufferBytes => ByteSize<GPUSceneInstance>(InstanceCapacity);
    public ulong TransformBufferBytes => ByteSize<GPUTransform>(InstanceCapacity);
    public ulong PreviousTransformBufferBytes => ByteSize<GPUPreviousTransform>(InstanceCapacity);
    public ulong BoundsBufferBytes => ByteSize<GPUObjectBounds>(ObjectCapacity);
    public ulong VisibilityBufferBytes => ByteSize<GPUVisibilityState>(ObjectCapacity);
    public ulong CompactedIndexBufferBytes => ByteSize<uint>(InstanceCapacity);

    public GpuSceneBufferSetStats Stats => new(
        ObjectCapacity,
        InstanceCapacity,
        ObjectHighWaterMark,
        InstanceHighWaterMark,
        ObjectResizeCount,
        InstanceResizeCount,
        LastUploadBytes,
        TotalUploadBytes,
        AllocatedBytes);

    public void EnsureCapacity(int objectCount, int instanceCount)
    {
        if (objectCount < 0)
            throw new ArgumentOutOfRangeException(nameof(objectCount));
        if (instanceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(instanceCount));

        lock (_lock)
        {
            bool objectResized = EnsureObjectCapacityLocked(objectCount);
            bool instanceResized = EnsureInstanceCapacityLocked(instanceCount);
            ObjectHighWaterMark = Math.Max(ObjectHighWaterMark, objectCount);
            InstanceHighWaterMark = Math.Max(InstanceHighWaterMark, instanceCount);
            if ((objectResized || instanceResized) && _context != null)
            {
                RetireLiveBuffersLocked();
                CreateBuffers();
                RegisterBuffers(_bindlessHeap);
            }
        }
    }

    public GpuSceneBufferUploadResult ApplyUploadPlan(GpuSceneManager scene, GpuSceneUploadPlan uploadPlan, CommandBuffer commandBuffer = default)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (uploadPlan == null)
            throw new ArgumentNullException(nameof(uploadPlan));

        lock (_lock)
        {
            ProcessRetiredBuffersLocked();
            GPUSceneObject[] objects = scene.GetObjectDataSnapshot();
            GPUSceneInstance[] instances = scene.GetInstanceDataSnapshot();
            GPUTransform[] transforms = scene.GetTransformDataSnapshot();
            GPUPreviousTransform[] previousTransforms = scene.GetPreviousTransformDataSnapshot();
            GPUObjectBounds[] bounds = scene.GetBoundsDataSnapshot();
            GPUVisibilityState[] visibility = scene.GetVisibilityDataSnapshot();

            EnsureCapacity(objects.Length, instances.Length);
            ulong uploaded = 0;
            uploaded += UploadRanges(objects, uploadPlan.ObjectRanges, ObjectBuffer, commandBuffer);
            uploaded += UploadRanges(instances, uploadPlan.InstanceRanges, InstanceBuffer, commandBuffer);
            uploaded += UploadRanges(transforms, uploadPlan.TransformRanges, TransformBuffer, commandBuffer);
            uploaded += UploadRanges(previousTransforms, uploadPlan.PreviousTransformRanges, PreviousTransformBuffer, commandBuffer);
            uploaded += UploadRanges(bounds, uploadPlan.BoundsRanges, BoundsBuffer, commandBuffer);
            uploaded += UploadRanges(visibility, uploadPlan.VisibilityRanges, VisibilityBuffer, commandBuffer);
            LastUploadBytes = uploaded;
            TotalUploadBytes = checked(TotalUploadBytes + uploaded);
            return new GpuSceneBufferUploadResult(uploaded, ObjectResizeCount, InstanceResizeCount);
        }
    }

    public unsafe void CopyCurrentTransformsToPrevious(CommandBuffer commandBuffer, int instanceCount)
    {
        if (instanceCount <= 0 || _context == null || _bufferManager == null)
            return;
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required to advance GPU scene transform history.", nameof(commandBuffer));

        ulong copyBytes = checked((ulong)Math.Min(instanceCount, InstanceCapacity) * (ulong)Unsafe.SizeOf<GPUTransform>());
        if (copyBytes == 0)
            return;

        VkBuffer transformBuffer = _bufferManager.GetBuffer(TransformBuffer);
        VkBuffer previousTransformBuffer = _bufferManager.GetBuffer(PreviousTransformBuffer);
        BufferMemoryBarrier2[] beforeCopy =
        [
            BarrierBuilder.BufferBarrier(
                transformBuffer,
                PipelineStageFlags2.TransferBit |
                PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.TaskShaderBitExt |
                PipelineStageFlags2.MeshShaderBitExt |
                PipelineStageFlags2.VertexShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageReadBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit),
            BarrierBuilder.BufferBarrier(
                previousTransformBuffer,
                PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.TaskShaderBitExt |
                PipelineStageFlags2.MeshShaderBitExt |
                PipelineStageFlags2.VertexShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit)
        ];
        BarrierBuilder.ExecuteBarrier(commandBuffer, bufferBarriers: beforeCopy);

        var copy = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = copyBytes
        };
        _context.Api.CmdCopyBuffer(commandBuffer, transformBuffer, previousTransformBuffer, 1, &copy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            previousTransformBuffer,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit |
            PipelineStageFlags2.TaskShaderBitExt |
            PipelineStageFlags2.MeshShaderBitExt |
            PipelineStageFlags2.VertexShaderBit |
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        BarrierBuilder.ExecuteBarrier(commandBuffer, bufferBarriers: [afterCopy]);
    }

    public void RegisterBuffers(BindlessHeap? bindlessHeap)
    {
        _bindlessHeap = bindlessHeap;
        if (bindlessHeap == null || _bufferManager == null || !ObjectBuffer.IsValid)
            return;

        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneObjectBuffer, ObjectBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneInstanceBuffer, InstanceBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneTransformBuffer, TransformBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuScenePreviousTransformBuffer, PreviousTransformBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneBoundsBuffer, BoundsBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneVisibilityBuffer, VisibilityBuffer);
        RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuSceneCompactedIndexBuffer, CompactedIndexBuffer);
    }

    private bool EnsureObjectCapacityLocked(int objectCount)
    {
        if (objectCount <= ObjectCapacity)
            return false;

        while (ObjectCapacity < objectCount)
            ObjectCapacity = checked(ObjectCapacity * 2);
        ObjectResizeCount++;
        return true;
    }

    private bool EnsureInstanceCapacityLocked(int instanceCount)
    {
        if (instanceCount <= InstanceCapacity)
            return false;

        while (InstanceCapacity < instanceCount)
            InstanceCapacity = checked(InstanceCapacity * 2);
        InstanceResizeCount++;
        return true;
    }

    private ulong UploadRanges<T>(
        T[] snapshot,
        IReadOnlyList<GpuSceneUploadRange> ranges,
        BufferHandle destination,
        CommandBuffer commandBuffer)
        where T : unmanaged
    {
        ulong bytes = 0;
        foreach (GpuSceneUploadRange range in ranges)
        {
            if (range.Count == 0)
                continue;
            if (range.Start < 0 || range.EndExclusive > snapshot.Length)
                throw new InvalidOperationException($"GPU scene upload range [{range.Start}, {range.EndExclusive}) exceeds snapshot length {snapshot.Length}.");

            ulong rangeBytes = checked((ulong)range.Count * (ulong)Unsafe.SizeOf<T>());
            bytes = checked(bytes + rangeBytes);
            if (_context == null || _bufferManager == null || _stagingRing == null)
                continue;
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for GPU scene buffer uploads.", nameof(commandBuffer));

            ReadOnlySpan<T> slice = snapshot.AsSpan(range.Start, range.Count);
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                destination,
                slice,
                checked((ulong)range.Start * (ulong)Unsafe.SizeOf<T>()),
                new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.MeshShaderBitExt | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        return bytes;
    }

    private void CreateBuffers()
    {
        if (_bufferManager == null)
            return;

        ObjectBuffer = CreateBuffer(ObjectBufferBytes, "GPU Scene Object Buffer");
        InstanceBuffer = CreateBuffer(InstanceBufferBytes, "GPU Scene Instance Buffer");
        TransformBuffer = CreateBuffer(TransformBufferBytes, "GPU Scene Transform Buffer");
        PreviousTransformBuffer = CreateBuffer(PreviousTransformBufferBytes, "GPU Scene Previous Transform Buffer");
        BoundsBuffer = CreateBuffer(BoundsBufferBytes, "GPU Scene Bounds Buffer");
        VisibilityBuffer = CreateBuffer(VisibilityBufferBytes, "GPU Scene Visibility Buffer");
        CompactedIndexBuffer = CreateBuffer(CompactedIndexBufferBytes, "GPU Scene Compacted Index Buffer");
    }

    private BufferHandle CreateBuffer(ulong size, string debugName)
    {
        if (_bufferManager == null)
            return BufferHandle.Invalid;

        return _bufferManager.CreateDeviceBuffer(
            size,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            true,
            MemoryBudgetCategory.ObjectAndInstanceBuffers,
            debugName);
    }

    private void RegisterStorageBuffer(BindlessHeap bindlessHeap, int index, BufferHandle handle)
    {
        if (_bufferManager == null || !handle.IsValid)
            return;

        VkBuffer buffer = _bufferManager.GetBuffer(handle);
        bindlessHeap.RegisterStorageBuffer(index, buffer, 0, Vk.WholeSize);
    }

    private void DestroyBuffers()
    {
        DestroyBuffer(ObjectBuffer);
        DestroyBuffer(InstanceBuffer);
        DestroyBuffer(TransformBuffer);
        DestroyBuffer(PreviousTransformBuffer);
        DestroyBuffer(BoundsBuffer);
        DestroyBuffer(VisibilityBuffer);
        DestroyBuffer(CompactedIndexBuffer);
        for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            DestroyBuffer(_retiredBuffers[i].Handle);
        _retiredBuffers.Clear();
        ClearLiveHandles();
    }

    private void RetireLiveBuffersLocked()
    {
        RetireBuffer(ObjectBuffer);
        RetireBuffer(InstanceBuffer);
        RetireBuffer(TransformBuffer);
        RetireBuffer(PreviousTransformBuffer);
        RetireBuffer(BoundsBuffer);
        RetireBuffer(VisibilityBuffer);
        RetireBuffer(CompactedIndexBuffer);
        ClearLiveHandles();
    }

    private void RetireBuffer(BufferHandle handle)
    {
        if (handle.IsValid)
            _retiredBuffers.Add(new RetiredBuffer(handle, FramesInFlight));
    }

    private void ProcessRetiredBuffersLocked()
    {
        for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
        {
            RetiredBuffer retired = _retiredBuffers[i] with
            {
                FramesRemaining = _retiredBuffers[i].FramesRemaining - 1
            };
            if (retired.FramesRemaining > 0)
            {
                _retiredBuffers[i] = retired;
                continue;
            }

            DestroyBuffer(retired.Handle);
            _retiredBuffers.RemoveAt(i);
        }
    }

    private void ClearLiveHandles()
    {
        ObjectBuffer = BufferHandle.Invalid;
        InstanceBuffer = BufferHandle.Invalid;
        TransformBuffer = BufferHandle.Invalid;
        PreviousTransformBuffer = BufferHandle.Invalid;
        BoundsBuffer = BufferHandle.Invalid;
        VisibilityBuffer = BufferHandle.Invalid;
        CompactedIndexBuffer = BufferHandle.Invalid;
    }

    private void DestroyBuffer(BufferHandle handle)
    {
        if (_bufferManager != null && handle.IsValid)
            _bufferManager.DestroyBuffer(handle);
    }

    private static ulong ByteSize<T>(int count)
        where T : unmanaged
    {
        return checked((ulong)Math.Max(0, count) * (ulong)Unsafe.SizeOf<T>());
    }

    private readonly record struct RetiredBuffer(BufferHandle Handle, int FramesRemaining);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_lock)
            DestroyBuffers();
    }
}

public sealed record GpuSceneBufferSetStats(
    int ObjectCapacity,
    int InstanceCapacity,
    int ObjectHighWaterMark,
    int InstanceHighWaterMark,
    int ObjectResizeCount,
    int InstanceResizeCount,
    ulong LastUploadBytes,
    ulong TotalUploadBytes,
    ulong AllocatedBytes);

public readonly record struct GpuSceneBufferUploadResult(
    ulong UploadedBytes,
    int ObjectResizeCount,
    int InstanceResizeCount);
