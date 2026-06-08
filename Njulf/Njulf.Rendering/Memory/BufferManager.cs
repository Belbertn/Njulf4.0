using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using Vma;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Memory
{
    public sealed unsafe class BufferManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly List<BufferInfo> _buffers = new List<BufferInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly object _lock = new object();
        private bool _disposed;
        
        public BufferManager(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }
        
        private class BufferInfo
        {
            public Buffer Buffer;
            public Allocation* Allocation;
            public AllocationInfo AllocationInfo;
            public ulong Size;
            public BufferUsageFlags Usage;
            public uint Generation;
        }
        
        public BufferHandle CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryUsage memoryUsage,
            AllocationCreateFlags allocFlags = default)
        {
            lock (_lock)
            {
                int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _buffers.Count;
                
                var bufferInfo = new BufferInfo
                {
                    Size = size,
                    Usage = usage,
                    Generation = (uint)(_buffers.Count + 1)
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
                
                Result result = GpuAllocator.Apis.CreateBuffer(
                    _context.Allocator,
                    &bufferCreateInfo,
                    &allocCreateInfo,
                    out bufferInfo.Buffer,
                    out bufferInfo.Allocation,
                    out bufferInfo.AllocationInfo);
                
                if (result != Result.Success)
                    throw new VulkanException("Failed to create buffer", result);
                
                if (index == _buffers.Count)
                    _buffers.Add(bufferInfo);
                else
                    _buffers[index] = bufferInfo;
                
                Console.WriteLine("Buffer created");
                
                return new BufferHandle(index, bufferInfo.Generation);
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
        
        public (Buffer Buffer, Allocation Allocation) GetBufferAndAllocation(BufferHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _buffers.Count)
                    throw new InvalidOperationException("Invalid buffer handle");
                
                var bufferInfo = _buffers[handle.Index];
                if (bufferInfo.Generation != handle.Generation)
                    throw new InvalidOperationException("Buffer handle generation mismatch");
                
                return (bufferInfo.Buffer, bufferInfo.Allocation);
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
        
        public void* GetMappedPointer(BufferHandle handle)
        {
            lock (_lock)
            {
                var (buffer, allocation) = GetBufferAndAllocation(handle);
                return allocation.GetMappedData();
            }
        }
        
        public void FlushBuffer(BufferHandle handle, ulong offset, ulong size)
        {
            lock (_lock)
            {
                var (_, allocation) = GetBufferAndAllocation(handle);
                GpuAllocator.Apis.FlushAllocation(_context.Allocator, allocation, offset, size);
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
                
                bufferInfo.Generation++;
                _freeIndices.Push(handle.Index);
            }
        }
        
        public BufferHandle CreateStagingBuffer(ulong size)
        {
            var allocFlags = AllocationCreateFlags.MappedBit | 
                           AllocationCreateFlags.HostAccessSequentialWriteBit;
            
            return CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit,
                MemoryUsage.AutoPreferHost,
                allocFlags);
        }
        
        public BufferHandle CreateDeviceBuffer(
            ulong size,
            BufferUsageFlags usage,
            bool requireDeviceAddress = false)
        {
            var allocFlags = requireDeviceAddress ? 
                AllocationCreateFlags.BitBufferDeviceAddress : 
                default;
            
            var memoryUsage = MemoryUsage.AutoPreferDevice;
            
            // Add required usage flags
            usage |= BufferUsageFlags.TransferDstBit;
            
            return CreateBuffer(size, usage, memoryUsage, allocFlags);
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
                        GpuAllocator.Apis.DestroyBuffer(
                            _context.Allocator,
                            bufferInfo.Buffer,
                            bufferInfo.Allocation);
                }
                _buffers.Clear();
                _freeIndices.Clear();
            }
            
            Console.WriteLine("Buffer manager disposed.");
        }
        
        ~BufferManager()
        {
            Dispose(false);
        }
    }

}
