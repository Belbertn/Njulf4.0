using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using GpuAllocator = Vma;
using Vma;

namespace Njulf.Rendering.Memory
{
    public sealed class StagingRing : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly object _lock = new object();
        
        private readonly BufferHandle[] _stagingBuffers;
        private readonly ulong[] _currentOffsets;
        private readonly ulong _bufferSize;
        private readonly uint _minAlignment;
        
        private int _currentFrame = 0;
        private bool _disposed;
        

        public const ulong DefaultStagingBufferSize = 64 * 1024 * 1024;
        public const uint DefaultMinAlignment = 256;
        
        public StagingRing(
            VulkanContext context,
            BufferManager bufferManager,
            ulong bufferSize = DefaultStagingBufferSize,
            uint minAlignment = DefaultMinAlignment)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bufferSize = bufferSize;
            _minAlignment = minAlignment;
            
            _stagingBuffers = new BufferHandle[FramesInFlight];
            _currentOffsets = new ulong[FramesInFlight];
            
            for (int i = 0; i < FramesInFlight; i++)
            {
                _stagingBuffers[i] = bufferManager.CreateStagingBuffer(bufferSize);
            }
            
            Console.WriteLine("Staging ring created");
        }
        
        public (BufferHandle Buffer, ulong Offset) Allocate(ulong size)
        {
            lock (_lock)
            {
                int frameIndex = _currentFrame % FramesInFlight;
                ulong offset = _currentOffsets[frameIndex];
                
                offset = AlignUp(offset, _minAlignment);
                
                if (offset + size > _bufferSize)
                {
                    throw new InvalidOperationException(
                        $"Staging buffer overflow: trying to allocate {size} bytes at offset {offset}, " +
                        $"buffer size is {_bufferSize}");
                }
                
                _currentOffsets[frameIndex] = offset + size;
                
                return (_stagingBuffers[frameIndex], offset);
            }
        }
        
        public void BeginFrame(int frameIndex)
        {
            lock (_lock)
            {
                _currentFrame = frameIndex % FramesInFlight;
                _currentOffsets[_currentFrame] = 0;
            }
        }

        public void AdvanceFrame()
        {
            lock (_lock)
            {
                _currentFrame++;
                int resetIndex = _currentFrame % FramesInFlight;
                _currentOffsets[resetIndex] = 0;
            }
        }
        
        public void Flush(BufferHandle stagingBuffer, ulong offset, ulong size)
        {
            _bufferManager.FlushBuffer(stagingBuffer, offset, size);
        }
        
        public void FlushCurrentFrame()
        {
            int frameIndex = _currentFrame % FramesInFlight;
            if (_currentOffsets[frameIndex] > 0)
            {
                Flush(_stagingBuffers[frameIndex], 0, _currentOffsets[frameIndex]);
            }
        }

        public int CurrentFrameIndex => _currentFrame % FramesInFlight;
        
        public BufferHandle GetCurrentStagingBuffer()
        {
            return _stagingBuffers[CurrentFrameIndex];
        }
        
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
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
                foreach (var handle in _stagingBuffers)
                {
                    if (handle.IsValid)
                        _bufferManager.DestroyBuffer(handle);
                }
            }
            
            Console.WriteLine("Staging ring disposed.");
        }
        
        ~StagingRing()
        {
            Dispose(false);
        }
    }
}
