using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using Vma;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Njulf.Rendering.Memory
{
    public sealed unsafe class FenceBasedDeleter : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly Dictionary<Fence, List<Action>> _pendingDeletions = new Dictionary<Fence, List<Action>>();
        private readonly object _lock = new object();
        private bool _disposed;
        
        public FenceBasedDeleter(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }
        
        public void QueueDeletion(Fence fence, Action deletionAction)
        {
            if (fence.Handle == 0 || deletionAction == null)
                return;
            
            lock (_lock)
            {
                if (!_pendingDeletions.TryGetValue(fence, out var list))
                {
                    list = new List<Action>();
                    _pendingDeletions[fence] = list;
                }
                list.Add(deletionAction);
            }
        }
        
        public void QueueBufferDeletion(Fence fence, Buffer buffer, Allocation* allocation)
        {
            QueueDeletion(fence, () => 
            {
                GpuAllocator.Apis.DestroyBuffer(_context.Allocator, buffer, allocation);
            });
        }
        
        public void QueueBufferDeletion(Fence fence, BufferHandle bufferHandle, BufferManager bufferManager)
        {
            QueueDeletion(fence, () => 
            {
                bufferManager.DestroyBuffer(bufferHandle);
            });
        }
        
        public void QueueImageDeletion(Fence fence, Image image, Allocation* allocation)
        {
            QueueDeletion(fence, () => 
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
            });
        }
        
        public void QueueImageViewDeletion(Fence fence, ImageView imageView)
        {
            QueueDeletion(fence, () => 
            {
                _context.Api.DestroyImageView(_context.Device, imageView, null);
            });
        }
        
        public void QueueSemaphoreDeletion(Fence fence, Semaphore semaphore)
        {
            QueueDeletion(fence, () => 
            {
                _context.Api.DestroySemaphore(_context.Device, semaphore, null);
            });
        }
        
        public void QueueFenceDeletion(Fence fence, Fence fenceToDelete)
        {
            QueueDeletion(fence, () => 
            {
                _context.Api.DestroyFence(_context.Device, fenceToDelete, null);
            });
        }
        
        public void QueueCommandBufferDeletion(Fence fence, CommandPool pool, CommandBuffer cmd)
        {
            QueueDeletion(fence, () => 
            {
                var commandBuffer = cmd;
                _context.Api.FreeCommandBuffers(_context.Device, pool, 1, &commandBuffer);
            });
        }
        
        public void QueuePipelineDeletion(Fence fence, Silk.NET.Vulkan.Pipeline pipeline)
        {
            QueueDeletion(fence, () => 
            {
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
            });
        }
        
        public void QueuePipelineLayoutDeletion(Fence fence, PipelineLayout layout)
        {
            QueueDeletion(fence, () => 
            {
                _context.Api.DestroyPipelineLayout(_context.Device, layout, null);
            });
        }
        
        public void ProcessCompletedFrame(Fence fence)
        {
            if (fence.Handle == 0)
                return;
            
            lock (_lock)
            {
                if (_pendingDeletions.TryGetValue(fence, out var deletions))
                {
                    foreach (var action in deletions)
                        action();
                    deletions.Clear();
                }
            }
        }
        
        public void WaitAndProcess(Fence fence)
        {
            Result result = _context.Api.WaitForFences(
                _context.Device, 1, &fence, true, ulong.MaxValue);
            if (result != Result.Success)
                throw new VulkanException("Failed to wait for fence", result);
            
            ProcessCompletedFrame(fence);
            
            result = _context.Api.ResetFences(_context.Device, 1, &fence);
            if (result != Result.Success)
                throw new VulkanException("Failed to reset fence", result);
        }
        
        public void Cleanup()
        {
            lock (_lock)
            {
                foreach (var kvp in _pendingDeletions)
                {
                    foreach (var action in kvp.Value)
                        action();
                }
                _pendingDeletions.Clear();
            }
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
            
            Cleanup();
            
            System.Diagnostics.Debug.WriteLine("Fence-based deleter disposed.");
        }
    }
}
