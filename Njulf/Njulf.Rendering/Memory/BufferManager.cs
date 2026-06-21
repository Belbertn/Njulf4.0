using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using Vma;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Memory
{
    public sealed unsafe class BufferManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly GpuAllocationTracker _allocationTracker;
        private readonly List<BufferInfo> _buffers = new List<BufferInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly object _lock = new object();
        private bool _disposed;
        
        public BufferManager(VulkanContext context)
            : this(context, new GpuAllocationTracker())
        {
        }

        public BufferManager(VulkanContext context, GpuAllocationTracker allocationTracker)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _allocationTracker = allocationTracker ?? throw new ArgumentNullException(nameof(allocationTracker));
        }

        public GpuAllocationTracker AllocationTracker => _allocationTracker;
        
        private class BufferInfo
        {
            public Buffer Buffer;
            public Allocation* Allocation;
            public AllocationInfo AllocationInfo;
            public ulong Size;
            public BufferUsageFlags Usage;
            public uint Generation;
        }

        public readonly struct BufferAllocation
        {
            public BufferAllocation(Buffer buffer, Allocation* allocation, AllocationInfo allocationInfo)
            {
                Buffer = buffer;
                Allocation = allocation;
                AllocationInfo = allocationInfo;
            }

            public Buffer Buffer { get; }
            public Allocation* Allocation { get; }
            public AllocationInfo AllocationInfo { get; }
        }
        
        public BufferHandle CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryUsage memoryUsage,
            AllocationCreateFlags allocFlags = default,
            string? debugName = null,
            MemoryBudgetCategory category = MemoryBudgetCategory.Unknown)
        {
            lock (_lock)
            {
                int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _buffers.Count;
                
                var bufferInfo = new BufferInfo
                {
                    Size = size,
                    Usage = usage,
                    Generation = AllocateGeneration(index)
                };
                
                var bufferCreateInfo = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = size,
                    Usage = usage,
                    SharingMode = SharingMode.Exclusive
                };
                
                var allocCreateInfo = new AllocationCreateInfo
                {
                    Usage = memoryUsage,
                    Flags = allocFlags
                };
                
                Buffer buffer;
                Allocation* allocation;
                AllocationInfo allocationInfo;
                Result result = GpuAllocator.Apis.CreateBuffer(
                    _context.Allocator,
                    &bufferCreateInfo,
                    &allocCreateInfo,
                    &buffer,
                    &allocation,
                    &allocationInfo);
                
                if (result != Result.Success)
                    throw new VulkanException("Failed to create buffer", result);

                bufferInfo.Buffer = buffer;
                bufferInfo.Allocation = allocation;
                bufferInfo.AllocationInfo = allocationInfo;
                
                if (index == _buffers.Count)
                    _buffers.Add(bufferInfo);
                else
                    _buffers[index] = bufferInfo;
                
                var handle = new BufferHandle(index, bufferInfo.Generation);
                _context.SetDebugName(
                    buffer.Handle,
                    ObjectType.Buffer,
                    debugName ?? $"Buffer[{handle.Index}] {usage} {size} bytes");
                _allocationTracker.RegisterBuffer(
                    handle,
                    size,
                    usage,
                    category,
                    debugName ?? $"Buffer[{handle.Index}]");

                System.Diagnostics.Debug.WriteLine("Buffer created");
                
                return handle;
            }
        }
        
        public Buffer GetBuffer(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    throw new InvalidOperationException("Invalid buffer handle");
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    throw new InvalidOperationException("Buffer handle generation mismatch - use after free detected");
                
                return bufferInfo.Buffer;
            }
        }
        
        public BufferAllocation GetBufferAndAllocation(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    throw new InvalidOperationException("Invalid buffer handle");
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    throw new InvalidOperationException("Buffer handle generation mismatch");
                
                return new BufferAllocation(bufferInfo.Buffer, bufferInfo.Allocation, bufferInfo.AllocationInfo);
            }
        }
        
        public ulong GetBufferSize(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    throw new InvalidOperationException("Invalid buffer handle");
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    throw new InvalidOperationException("Buffer handle generation mismatch");
                
                return bufferInfo.Size;
            }
        }

        public ulong GetBufferDeviceAddress(BufferHandle handle)
        {
            Buffer buffer = GetBuffer(handle);
            var addressInfo = new BufferDeviceAddressInfo
            {
                SType = StructureType.BufferDeviceAddressInfo,
                Buffer = buffer
            };

            return _context.Api.GetBufferDeviceAddress(_context.Device, &addressInfo);
        }
        
        public void* GetMappedPointer(BufferHandle handle)
        {
            lock (_lock)
            {
                return GetBufferAndAllocation(handle).AllocationInfo.PMappedData;
            }
        }
        
        public void FlushBuffer(BufferHandle handle, ulong offset, ulong size)
        {
            lock (_lock)
            {
                GpuAllocator.Apis.FlushAllocation(_context.Allocator, GetBufferAndAllocation(handle).Allocation, offset, size);
            }
        }

        public void InvalidateBuffer(BufferHandle handle, ulong offset, ulong size)
        {
            lock (_lock)
            {
                GpuAllocator.Apis.InvalidateAllocation(_context.Allocator, GetBufferAndAllocation(handle).Allocation, offset, size);
            }
        }
        
        public void RetireBuffer(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    return;
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    return;
                
                bufferInfo.Generation++;
                _freeIndices.Push(handle.Index);
            }
        }
        
        public void DestroyBuffer(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    return;
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    return;
                
                GpuAllocator.Apis.DestroyBuffer(
                    _context.Allocator,
                    bufferInfo.Buffer,
                    bufferInfo.Allocation);
                _allocationTracker.RetireBuffer(handle);
                
                bufferInfo.Buffer = default;
                bufferInfo.Allocation = null;
                bufferInfo.AllocationInfo = default;
                bufferInfo.Size = 0;
                bufferInfo.Usage = default;
                bufferInfo.Generation = NextGeneration(bufferInfo.Generation);
                _freeIndices.Push(handle.Index);
            }
        }

        private uint AllocateGeneration(int bufferIndex)
        {
            if (bufferIndex >= _buffers.Count)
                return checked((uint)(_buffers.Count + 1));

            return NextGeneration(_buffers[bufferIndex].Generation);
        }

        private static uint NextGeneration(uint generation)
        {
            generation++;
            return generation == 0 ? 1 : generation;
        }
        
        public BufferHandle CreateStagingBuffer(ulong size, string? debugName = null)
        {
            var allocFlags = AllocationCreateFlags.MappedBit | 
                           AllocationCreateFlags.HostAccessSequentialWriteBit;
            
            return CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit,
                MemoryUsage.AutoPreferHost,
                allocFlags,
                debugName,
                MemoryBudgetCategory.StagingBuffers);
        }
        
        public BufferHandle CreateDeviceBuffer(
            ulong size,
            BufferUsageFlags usage,
            bool requireDeviceAddress = false,
            MemoryBudgetCategory category = MemoryBudgetCategory.Unknown,
            string? debugName = null)
        {
            var allocFlags = default(AllocationCreateFlags);
            
            var memoryUsage = MemoryUsage.AutoPreferDevice;
            
            // Add required usage flags
            usage |= BufferUsageFlags.TransferDstBit;
            if (requireDeviceAddress)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;
            
            return CreateBuffer(size, usage, memoryUsage, allocFlags, debugName, category);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                foreach (var bufferInfo in _buffers)
                {
                    if (bufferInfo.Buffer.Handle != 0)
                    {
                        GpuAllocator.Apis.DestroyBuffer(
                            _context.Allocator,
                            bufferInfo.Buffer,
                            bufferInfo.Allocation);
                        var handle = new BufferHandle(_buffers.IndexOf(bufferInfo), bufferInfo.Generation);
                        _allocationTracker.RetireBuffer(handle);
                        bufferInfo.Buffer = default;
                        bufferInfo.Allocation = null;
                        bufferInfo.AllocationInfo = default;
                    }
                }
                _buffers.Clear();
                _freeIndices.Clear();
            }
            
            System.Diagnostics.Debug.WriteLine("Buffer manager disposed.");
        }
    }

}
