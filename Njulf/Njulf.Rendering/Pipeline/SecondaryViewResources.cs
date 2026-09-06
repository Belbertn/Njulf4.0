using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Pipeline;

/// <summary>Fence-slot-owned descriptors and buffers. No secondary view rebinds the global heap.</summary>
internal sealed unsafe class SecondaryViewResources : IDisposable
{
    internal const int MaximumViews = 8; // Two planar slots and up to six scheduled probe faces.
    private readonly VulkanContext _context;
    private readonly BufferManager _buffers;
    private readonly BindlessHeap _heap;
    private DescriptorPool _pool;
    private readonly ViewResources[] _views = new ViewResources[MaximumViews * RenderingConstants.FramesInFlight];

    internal sealed class ViewResources
    {
        internal DescriptorSet StorageSet;
        internal readonly BufferHandle[] Buffers = new BufferHandle[10];
        internal readonly ulong[] Sizes = new ulong[10];
        internal readonly SecondaryViewDrawLists Draws = new();
        internal FoliageRuntimeBuffers Foliage;
        internal ulong FrameSerial;
    }

    internal SecondaryViewResources(VulkanContext context, BufferManager buffers, BindlessHeap heap)
    {
        _context = context; _buffers = buffers; _heap = heap;
        for (int i = 0; i < _views.Length; i++) _views[i] = new();
        uint count = checked((uint)_views.Length);
        var size = new DescriptorPoolSize(DescriptorType.StorageBuffer,
            checked(count * BindlessHeap.StorageBufferDescriptorCount));
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBitExt,
            MaxSets = count, PoolSizeCount = 1, PPoolSizes = &size
        };
        Result result = context.Api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool);
        if (result != Result.Success) throw new VulkanException("Secondary view descriptor pool", result);
        try
        {
            foreach (var view in _views)
            {
                DescriptorSetLayout layout = heap.StorageBufferSetLayout;
                var info = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool,
                    DescriptorSetCount = 1, PSetLayouts = &layout
                };
                result = context.Api.AllocateDescriptorSets(context.Device, &info, out view.StorageSet);
                if (result != Result.Success) throw new VulkanException("Secondary view descriptor set", result);
            }
        }
        catch { Dispose(); throw; }
    }

    internal ViewResources Acquire(int frameIndex, int slot, ulong serial)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if ((uint)slot >= MaximumViews) throw new ArgumentOutOfRangeException(nameof(slot));
        ViewResources resources = _views[frameIndex * MaximumViews + slot];
        if (resources.FrameSerial == serial && serial != 0)
            throw new InvalidOperationException("A secondary view's recorded buffers cannot be overwritten in the same submission.");
        resources.FrameSerial = serial;
        return resources;
    }

    internal void Prepare(ViewResources target, in SecondaryViewContext view, int frameIndex,
        in FoliageRuntimeBuffers foliage)
    {
        // Copy shared scene bindings once; override only this view's draw and culling storage.
        var copy = new CopyDescriptorSet
        {
            SType = StructureType.CopyDescriptorSet, SrcSet = _heap.StorageBufferSet,
            DstSet = target.StorageSet, DescriptorCount = BindlessHeap.StorageBufferDescriptorCount
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 0, null, 1, &copy);
        ReadOnlySpan<int> drawIndices = [BindlessIndex.MeshletDrawBufferBase,
            BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase,
            BindlessIndex.FullOpaqueMeshletDrawBufferBase, BindlessIndex.TransparentMeshletDrawBufferBase];
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<GPUMeshletDrawCommand> commands = CollectionsMarshal.AsSpan(
                i < 3 ? target.Draws.Opaque[i] : target.Draws.TransparentCommands);
            Upload(target, i, commands);
            Bind(target, i, drawIndices[i] + frameIndex);
        }
        Frustum frustum = SceneDataBuilder.ExtractFrustum(view.CullingViewProjection);
        GPUMeshletTaskFrameData data = new()
        {
            FrustumPlane0 = frustum.Left, FrustumPlane1 = frustum.Right,
            FrustumPlane2 = frustum.Bottom, FrustumPlane3 = frustum.Top,
            FrustumPlane4 = frustum.Near, FrustumPlane5 = frustum.Far,
            ViewProjectionMatrix = view.ViewProjection, InverseViewMatrix = view.View.Invert(),
            PreviousHiZViewProjectionMatrix = view.ViewProjection,
            PreviousHiZInverseViewMatrix = view.View.Invert(), ScreenDimensions = new Vector2(view.Width, view.Height)
        };
        Upload(target, 4, new ReadOnlySpan<GPUMeshletTaskFrameData>(&data, 1));
        Bind(target, 4, BindlessIndex.MeshletTaskFrameDataBufferBase + frameIndex);
        target.Foliage = default;
        if (foliage.ClusterCount <= 0) return;
        ReadOnlySpan<ulong> sizes = [foliage.VisibleClusterBufferSize, foliage.AuthoredInstanceCommandBufferSize,
            foliage.MeshletDrawBufferSize, foliage.CounterBufferSize, foliage.IndirectDispatchBufferSize];
        ReadOnlySpan<int> indices = [BindlessIndex.FoliageVisibleClusterBufferBase,
            BindlessIndex.FoliageAuthoredInstanceCommandBufferBase, BindlessIndex.FoliageMeshletDrawBufferBase,
            BindlessIndex.FoliageCounterBufferBase, BindlessIndex.FoliageIndirectDispatchBufferBase];
        for (int i = 0; i < 5; i++)
        {
            Ensure(target, i + 5, sizes[i], false);
            Bind(target, i + 5, indices[i] + frameIndex);
        }
        target.Foliage = foliage with
        {
            VisibleClusterBuffer = target.Buffers[5], AuthoredInstanceCommandBuffer = target.Buffers[6],
            MeshletDrawBuffer = target.Buffers[7], CounterBuffer = target.Buffers[8],
            IndirectDispatchBuffer = target.Buffers[9]
        };
    }

    private void Upload<T>(ViewResources target, int index, ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong size = checked((ulong)data.Length * (ulong)sizeof(T));
        Ensure(target, index, Math.Max(16UL, size), true);
        if (size == 0) return;
        data.CopyTo(new Span<T>(_buffers.GetMappedPointer(target.Buffers[index]), data.Length));
        _buffers.FlushBuffer(target.Buffers[index], 0, size);
    }

    private void Ensure(ViewResources target, int index, ulong size, bool host)
    {
        if (target.Buffers[index].IsValid && target.Sizes[index] >= size) return;
        size = Math.Max(256UL, size * 2);
        var usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit |
                    BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit;
        BufferHandle replacement = host
            ? _buffers.CreateBuffer(size, usage, MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessSequentialWriteBit,
                $"Secondary View Upload {index}", MemoryBudgetCategory.ObjectAndInstanceBuffers)
            : _buffers.CreateDeviceBuffer(size, usage, false, MemoryBudgetCategory.ObjectAndInstanceBuffers,
                $"Secondary View Foliage {index}");
        // Acquire is called only after the renderer has waited for this frame's fence.
        if (target.Buffers[index].IsValid) _buffers.DestroyBuffer(target.Buffers[index]);
        target.Buffers[index] = replacement;
        target.Sizes[index] = size;
    }

    private void Bind(ViewResources view, int buffer, int descriptor)
    {
        var info = new DescriptorBufferInfo(_buffers.GetBuffer(view.Buffers[buffer]), 0, Vk.WholeSize);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = view.StorageSet,
            DstArrayElement = checked((uint)descriptor), DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    public void Dispose()
    {
        if (_pool.Handle != 0) _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _pool = default;
        foreach (var view in _views)
        for (int i = 0; i < view.Buffers.Length; i++)
        {
            if (view.Buffers[i].IsValid) _buffers.DestroyBuffer(view.Buffers[i]);
            view.Buffers[i] = default;
        }
    }
}
