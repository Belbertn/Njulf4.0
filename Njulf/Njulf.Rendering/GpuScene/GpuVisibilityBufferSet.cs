using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using static Njulf.Rendering.RenderingConstants;
using Vma;

namespace Njulf.Rendering.GpuScene;

public sealed unsafe class GpuVisibilityBufferSet : IDisposable
{
    private static readonly ulong DrawStride = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUMeshletDrawCommand>();
    private static readonly ulong CounterStride = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUVisibilityCounters>();
    private static readonly ulong VisibleObjectRecordStride = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUVisibleObjectRecord>();
    private static readonly ulong VisibilityRecordCountStride = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUVisibilityRecordCounts>();
    private static readonly ulong IndirectCommandBytes = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<GPUMeshTaskIndirectCommand>();
    private static readonly ulong PerListCounterBytes = (ulong)GpuVisibilityLayout.PerListCounterCount * sizeof(uint);
    private static readonly ulong IndirectBytes = (ulong)GpuVisibilityLayout.IndirectListCount * IndirectCommandBytes;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly FenceBasedDeleter? _deleter;
    private readonly Func<int, Fence>? _getFrameFence;
    private readonly object _lock = new();
    private BindlessHeap? _bindlessHeap;
    private bool _disposed;

    public GpuVisibilityBufferSet(
        VulkanContext context,
        BufferManager bufferManager,
        FenceBasedDeleter? deleter = null,
        Func<int, Fence>? getFrameFence = null,
        GpuVisibilityCapacity? initialCapacity = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _deleter = deleter;
        _getFrameFence = getFrameFence;
        Capacity = initialCapacity ?? GpuVisibilityCapacity.Initial;
        DrawCapacity = Math.Max(
            Math.Max(Capacity.OpaqueMeshlets, Capacity.TransparentMeshlets),
            Math.Max(Capacity.MaskedMeshlets, Capacity.ShadowMeshlets));
        CreateBuffers();
    }

    public GpuVisibilityCapacity Capacity { get; private set; }
    public int DrawCapacity { get; private set; }
    public int ResizeCount { get; private set; }
    public GPUVisibilityCounters LastCompletedCounters { get; private set; }
    public BufferHandle[] OpaqueDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] SolidDepthDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] MaskedDepthDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] TransparentDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] DirectionalShadowDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] LocalShadowDrawBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] CounterBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] CounterReadbackBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] VisibleObjectRecordBuffers { get; } = new BufferHandle[FramesInFlight];
    public BufferHandle[] VisibilityRecordCountBuffers { get; } = new BufferHandle[FramesInFlight];
    public int SolidDepthCapacity => Math.Max(1, Capacity.OpaqueMeshlets);
    public int MaskedDepthCapacity => Math.Max(1, Capacity.MaskedMeshlets);
    public int OpaqueCapacity => Math.Max(1, Capacity.OpaqueMeshlets);
    public int TransparentCapacity => Math.Max(1, Capacity.TransparentMeshlets);
    public int DirectionalShadowListCapacity => Math.Max(1, Capacity.ShadowMeshlets);
    public int LocalShadowListCapacity => Math.Max(1, Capacity.ShadowMeshlets);
    public int VisibleObjectRecordCapacity => Math.Max(1, Capacity.VisibleObjectRecords);
    public ulong DrawBufferBytes => checked((ulong)Math.Max(1, DrawCapacity) * DrawStride);
    public ulong OpaqueDrawBufferBytes => checked((ulong)OpaqueCapacity * DrawStride);
    public ulong SolidDepthDrawBufferBytes => checked((ulong)SolidDepthCapacity * DrawStride);
    public ulong MaskedDepthDrawBufferBytes => checked((ulong)MaskedDepthCapacity * DrawStride);
    public ulong TransparentDrawBufferBytes => checked((ulong)TransparentCapacity * DrawStride);
    public ulong DirectionalShadowDrawBufferBytes => checked((ulong)DirectionalShadowListCapacity * (ulong)GpuVisibilityLayout.ShadowSettingsMaxDirectionalCascades * DrawStride);
    public ulong LocalShadowDrawBufferBytes => checked((ulong)LocalShadowListCapacity * (ulong)GpuVisibilityLayout.MaxLocalShadowLists * DrawStride);
    public ulong VisibleObjectRecordBufferBytes => checked((ulong)VisibleObjectRecordCapacity * VisibleObjectRecordStride);
    public ulong VisibilityRecordCountBufferBytes => checked((ulong)VisibleObjectRecordCapacity * VisibilityRecordCountStride);
    public ulong CounterBufferBytes => checked(CounterStride + PerListCounterBytes + IndirectBytes);
    public ulong IndirectCommandOffset => checked(CounterStride + PerListCounterBytes);
    public ulong AllocatedBytes => checked((OpaqueDrawBufferBytes + SolidDepthDrawBufferBytes + MaskedDepthDrawBufferBytes + TransparentDrawBufferBytes + DirectionalShadowDrawBufferBytes + LocalShadowDrawBufferBytes + VisibleObjectRecordBufferBytes + VisibilityRecordCountBufferBytes + CounterBufferBytes + CounterBufferBytes) * FramesInFlight);

    public void ApplyCounters(GPUVisibilityCounters counters)
    {
        lock (_lock)
        {
            GpuVisibilityCapacity next = Capacity;
            bool resized = false;
            resized |= GrowIfNeeded(counters.InputObjectCount, next.VisibleObjectRecords, value => next = next with { VisibleObjectRecords = value });
            resized |= GrowIfNeeded(Math.Max(counters.RequiredOpaqueCapacity, counters.RequiredSolidDepthCapacity), next.OpaqueMeshlets, value => next = next with { OpaqueMeshlets = value });
            resized |= GrowIfNeeded(counters.RequiredMaskedCapacity, next.MaskedMeshlets, value => next = next with { MaskedMeshlets = value });
            resized |= GrowIfNeeded(counters.RequiredTransparentCapacity, next.TransparentMeshlets, value => next = next with { TransparentMeshlets = value });
            resized |= GrowIfNeeded(Math.Max(counters.RequiredShadowCapacity, Math.Max(counters.RequiredDirectionalShadowCapacity, counters.RequiredLocalShadowCapacity)), next.ShadowMeshlets, value => next = next with { ShadowMeshlets = value });
            if (!resized)
                return;

            DestroyBuffers(defer: true);
            Capacity = next;
            DrawCapacity = Math.Max(
                Math.Max(Capacity.OpaqueMeshlets, Capacity.TransparentMeshlets),
                Math.Max(Capacity.MaskedMeshlets, Capacity.ShadowMeshlets));
            ResizeCount++;
            CreateBuffers();
            RegisterBuffers(_bindlessHeap);
        }
    }

    public void RegisterBuffers(BindlessHeap? bindlessHeap)
    {
        _bindlessHeap = bindlessHeap;
        if (bindlessHeap == null)
            return;

        for (int frame = 0; frame < FramesInFlight; frame++)
        {
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletDrawBufferBase + frame, OpaqueDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.SolidDepthMeshletDrawBufferBase + frame, SolidDepthDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.MaskedDepthMeshletDrawBufferBase + frame, MaskedDepthDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.TransparentMeshletDrawBufferBase + frame, TransparentDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.DirectionalShadowMeshletDrawBufferBase + frame, DirectionalShadowDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.LocalShadowMeshletDrawBufferBase + frame, LocalShadowDrawBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuVisibilityCounterBufferBase + frame, CounterBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuVisibleObjectRecordBufferBase + frame, VisibleObjectRecordBuffers[frame]);
            RegisterStorageBuffer(bindlessHeap, BindlessIndex.GpuVisibilityRecordCountBufferBase + frame, VisibilityRecordCountBuffers[frame]);
        }
    }

    public BufferHandle GetOpaqueDrawBuffer(int frameIndex) => OpaqueDrawBuffers[frameIndex];
    public BufferHandle GetSolidDepthDrawBuffer(int frameIndex) => SolidDepthDrawBuffers[frameIndex];
    public BufferHandle GetMaskedDepthDrawBuffer(int frameIndex) => MaskedDepthDrawBuffers[frameIndex];
    public BufferHandle GetTransparentDrawBuffer(int frameIndex) => TransparentDrawBuffers[frameIndex];
    public BufferHandle GetDirectionalShadowDrawBuffer(int frameIndex) => DirectionalShadowDrawBuffers[frameIndex];
    public BufferHandle GetLocalShadowDrawBuffer(int frameIndex) => LocalShadowDrawBuffers[frameIndex];
    public BufferHandle GetCounterBuffer(int frameIndex) => CounterBuffers[frameIndex];
    public BufferHandle GetCounterReadbackBuffer(int frameIndex) => CounterReadbackBuffers[frameIndex];
    public BufferHandle GetVisibleObjectRecordBuffer(int frameIndex) => VisibleObjectRecordBuffers[frameIndex];
    public BufferHandle GetVisibilityRecordCountBuffer(int frameIndex) => VisibilityRecordCountBuffers[frameIndex];
    public ulong GetIndirectCommandOffset(GpuVisibilityIndirectList list) => checked(IndirectCommandOffset + (ulong)(int)list * IndirectCommandBytes);
    public ulong GetDirectionalShadowFirstDrawIndex(int cascade) => checked((ulong)cascade * (ulong)DirectionalShadowListCapacity);
    public ulong GetSpotShadowFirstDrawIndex(int spotIndex) => checked((ulong)spotIndex * (ulong)LocalShadowListCapacity);
    public ulong GetPointShadowFaceFirstDrawIndex(int pointIndex, int faceIndex) =>
        checked((ulong)(GpuVisibilityLayout.MaxSpotShadowLists + pointIndex * GpuVisibilityLayout.PointShadowFaces + faceIndex) * (ulong)LocalShadowListCapacity);
    public GpuVisibilityIndirectList GetDirectionalShadowIndirectList(int cascade) => (GpuVisibilityIndirectList)GpuVisibilityLayout.DirectionalShadowListIndex(cascade);
    public GpuVisibilityIndirectList GetSpotShadowIndirectList(int spotIndex) => (GpuVisibilityIndirectList)GpuVisibilityLayout.SpotShadowListIndex(spotIndex);
    public GpuVisibilityIndirectList GetPointShadowFaceIndirectList(int pointIndex, int faceIndex) => (GpuVisibilityIndirectList)GpuVisibilityLayout.PointShadowFaceListIndex(pointIndex, faceIndex);

    public void ReadCompletedFrame(int frameIndex)
    {
        ValidateFrameIndex(frameIndex);
        BufferHandle readback = CounterReadbackBuffers[frameIndex];
        if (!readback.IsValid)
            return;

        _bufferManager.InvalidateBuffer(readback, 0, CounterStride);
        LastCompletedCounters = *(GPUVisibilityCounters*)_bufferManager.GetMappedPointer(readback);
    }

    private static bool GrowIfNeeded(uint required, int current, Action<int> setValue)
    {
        if (required <= current)
            return false;

        int next = Math.Max(1, current);
        while (next < required)
            next = checked(next * 2);
        setValue(next);
        return true;
    }

    private void CreateBuffers()
    {
        for (int frame = 0; frame < FramesInFlight; frame++)
        {
            OpaqueDrawBuffers[frame] = CreateDrawBuffer(OpaqueDrawBufferBytes, $"GPU Visibility Opaque Draw Buffer Frame {frame}");
            SolidDepthDrawBuffers[frame] = CreateDrawBuffer(SolidDepthDrawBufferBytes, $"GPU Visibility Solid Depth Draw Buffer Frame {frame}");
            MaskedDepthDrawBuffers[frame] = CreateDrawBuffer(MaskedDepthDrawBufferBytes, $"GPU Visibility Masked Depth Draw Buffer Frame {frame}");
            TransparentDrawBuffers[frame] = CreateDrawBuffer(TransparentDrawBufferBytes, $"GPU Visibility Transparent Draw Buffer Frame {frame}");
            DirectionalShadowDrawBuffers[frame] = CreateDrawBuffer(DirectionalShadowDrawBufferBytes, $"GPU Visibility Directional Shadow Draw Buffer Frame {frame}");
            LocalShadowDrawBuffers[frame] = CreateDrawBuffer(LocalShadowDrawBufferBytes, $"GPU Visibility Local Shadow Draw Buffer Frame {frame}");
            VisibleObjectRecordBuffers[frame] = CreateDrawBuffer(VisibleObjectRecordBufferBytes, $"GPU Visible Object Record Buffer Frame {frame}");
            VisibilityRecordCountBuffers[frame] = CreateDrawBuffer(VisibilityRecordCountBufferBytes, $"GPU Visibility Record Count Buffer Frame {frame}");
            CounterBuffers[frame] = _bufferManager.CreateDeviceBuffer(
                CounterBufferBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit,
                true,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                $"GPU Visibility Counters Frame {frame}");
            CounterReadbackBuffers[frame] = _bufferManager.CreateBuffer(
                CounterBufferBytes,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.AutoPreferHost,
                AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                $"GPU Visibility Counter Readback Frame {frame}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }
    }

    private BufferHandle CreateDrawBuffer(ulong size, string name)
    {
        return _bufferManager.CreateDeviceBuffer(
            size,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.IndirectBufferBit,
            true,
            MemoryBudgetCategory.ObjectAndInstanceBuffers,
            name);
    }

    private void RegisterStorageBuffer(BindlessHeap bindlessHeap, int index, BufferHandle handle)
    {
        if (!handle.IsValid)
            return;

        VkBuffer buffer = _bufferManager.GetBuffer(handle);
        bindlessHeap.RegisterStorageBuffer(index, buffer, 0, Vk.WholeSize);
    }

    private void DestroyBuffers(bool defer = false)
    {
        for (int frame = 0; frame < FramesInFlight; frame++)
        {
            Destroy(OpaqueDrawBuffers[frame], frame, defer);
            Destroy(SolidDepthDrawBuffers[frame], frame, defer);
            Destroy(MaskedDepthDrawBuffers[frame], frame, defer);
            Destroy(TransparentDrawBuffers[frame], frame, defer);
            Destroy(DirectionalShadowDrawBuffers[frame], frame, defer);
            Destroy(LocalShadowDrawBuffers[frame], frame, defer);
            Destroy(VisibleObjectRecordBuffers[frame], frame, defer);
            Destroy(VisibilityRecordCountBuffers[frame], frame, defer);
            Destroy(CounterBuffers[frame], frame, defer);
            Destroy(CounterReadbackBuffers[frame], frame, defer);
            OpaqueDrawBuffers[frame] = BufferHandle.Invalid;
            SolidDepthDrawBuffers[frame] = BufferHandle.Invalid;
            MaskedDepthDrawBuffers[frame] = BufferHandle.Invalid;
            TransparentDrawBuffers[frame] = BufferHandle.Invalid;
            DirectionalShadowDrawBuffers[frame] = BufferHandle.Invalid;
            LocalShadowDrawBuffers[frame] = BufferHandle.Invalid;
            VisibleObjectRecordBuffers[frame] = BufferHandle.Invalid;
            VisibilityRecordCountBuffers[frame] = BufferHandle.Invalid;
            CounterBuffers[frame] = BufferHandle.Invalid;
            CounterReadbackBuffers[frame] = BufferHandle.Invalid;
        }
    }

    private void Destroy(BufferHandle handle, int frame, bool defer)
    {
        if (!handle.IsValid)
            return;

        if (defer && _deleter != null && _getFrameFence != null)
        {
            _deleter.QueueBufferDeletion(_getFrameFence(frame), handle, _bufferManager);
            return;
        }

        _bufferManager.DestroyBuffer(handle);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DestroyBuffers();
    }
}
