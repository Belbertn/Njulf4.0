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
        private readonly ulong[] _frameHighWater;
        private readonly List<LargeUploadBuffer> _largeUploadBuffers = new();
        private readonly ulong[] _largeUploadBytesThisFrame;
        private readonly FenceBasedDeleter? _deleter;
        private readonly Func<int, Fence>? _getFrameFence;
        private readonly ulong _bufferSize;
        private readonly uint _minAlignment;
        private ulong _largeUploadAllocatedBytes;
        private ulong _peakBytesThisSession;
        private int _overflowCount;
        
        private int _currentFrame = 0;
        private bool _disposed;
        

        public const ulong DefaultStagingBufferSize = 16 * 1024 * 1024;
        public const uint DefaultMinAlignment = 256;
        
        public StagingRing(
            VulkanContext context,
            BufferManager bufferManager,
            ulong bufferSize = DefaultStagingBufferSize,
            uint minAlignment = DefaultMinAlignment,
            FenceBasedDeleter? deleter = null,
            Func<int, Fence>? getFrameFence = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bufferSize = bufferSize;
            _minAlignment = minAlignment;
            _deleter = deleter;
            _getFrameFence = getFrameFence;
            
            _stagingBuffers = new BufferHandle[FramesInFlight];
            _currentOffsets = new ulong[FramesInFlight];
            _frameHighWater = new ulong[FramesInFlight];
            _largeUploadBytesThisFrame = new ulong[FramesInFlight];
            
            for (int i = 0; i < FramesInFlight; i++)
            {
                _stagingBuffers[i] = bufferManager.CreateStagingBuffer(bufferSize, $"Staging Ring Frame {i}");
            }
            
            System.Diagnostics.Debug.WriteLine("Staging ring created");
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
                    return AllocateLargeUploadBuffer(size, frameIndex);
                }
                
                _currentOffsets[frameIndex] = offset + size;
                _frameHighWater[frameIndex] = Math.Max(_frameHighWater[frameIndex], _currentOffsets[frameIndex]);
                _peakBytesThisSession = Math.Max(_peakBytesThisSession, _frameHighWater[frameIndex]);
                
                return (_stagingBuffers[frameIndex], offset);
            }
        }
        
        public void BeginFrame(int frameIndex)
        {
            lock (_lock)
            {
                _currentFrame = frameIndex % FramesInFlight;
                RetireFallbackLargeUploadBuffers(_currentFrame);
                _currentOffsets[_currentFrame] = 0;
                _frameHighWater[_currentFrame] = 0;
                _largeUploadBytesThisFrame[_currentFrame] = 0;
            }
        }

        public void AdvanceFrame()
        {
            lock (_lock)
            {
                _currentFrame++;
                int resetIndex = _currentFrame % FramesInFlight;
                RetireFallbackLargeUploadBuffers(resetIndex);
                _currentOffsets[resetIndex] = 0;
                _frameHighWater[resetIndex] = 0;
                _largeUploadBytesThisFrame[resetIndex] = 0;
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
        public ulong BufferSize => _bufferSize;
        public ulong TotalAllocatedBytes
        {
            get
            {
                lock (_lock)
                    return checked(_bufferSize * FramesInFlight + _largeUploadAllocatedBytes);
            }
        }
        public ulong CurrentFrameBytesUsed
        {
            get
            {
                lock (_lock)
                    return _currentOffsets[CurrentFrameIndex] + _largeUploadBytesThisFrame[CurrentFrameIndex];
            }
        }
        public ulong CurrentFrameHighWaterBytes
        {
            get
            {
                lock (_lock)
                    return _frameHighWater[CurrentFrameIndex] + _largeUploadBytesThisFrame[CurrentFrameIndex];
            }
        }
        public ulong PeakBytesThisSession
        {
            get
            {
                lock (_lock)
                    return _peakBytesThisSession;
            }
        }
        public int OverflowCount
        {
            get
            {
                lock (_lock)
                    return _overflowCount;
            }
        }
        
        public BufferHandle GetCurrentStagingBuffer()
        {
            return _stagingBuffers[CurrentFrameIndex];
        }
        
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        private (BufferHandle Buffer, ulong Offset) AllocateLargeUploadBuffer(ulong size, int frameIndex)
        {
            _overflowCount++;
            ulong dedicatedSize = AlignUp(Math.Max(size, _bufferSize), _minAlignment);
            BufferHandle buffer = _bufferManager.CreateStagingBuffer(
                dedicatedSize,
                $"Staging Ring Large Upload Frame {frameIndex} #{_overflowCount}");
            _largeUploadAllocatedBytes = checked(_largeUploadAllocatedBytes + dedicatedSize);
            _largeUploadBytesThisFrame[frameIndex] = checked(_largeUploadBytesThisFrame[frameIndex] + size);
            _peakBytesThisSession = Math.Max(_peakBytesThisSession, _currentOffsets[frameIndex] + _largeUploadBytesThisFrame[frameIndex]);
            RetireLargeUploadBuffer(buffer, dedicatedSize, frameIndex);
            return (buffer, 0);
        }

        private void RetireLargeUploadBuffer(BufferHandle buffer, ulong size, int frameIndex)
        {
            Fence fence = _getFrameFence?.Invoke(frameIndex) ?? default;
            if (_deleter != null && fence.Handle != 0)
            {
                _deleter.QueueDeletion(fence, () =>
                {
                    _bufferManager.DestroyBuffer(buffer);
                    lock (_lock)
                        DecrementLargeUploadAllocatedBytes(size);
                });
                return;
            }

            _largeUploadBuffers.Add(new LargeUploadBuffer(buffer, size, frameIndex));
        }

        private void RetireFallbackLargeUploadBuffers(int frameIndex)
        {
            for (int i = _largeUploadBuffers.Count - 1; i >= 0; i--)
            {
                LargeUploadBuffer upload = _largeUploadBuffers[i];
                if (upload.FrameIndex != frameIndex)
                    continue;

                if (upload.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(upload.Buffer);
                DecrementLargeUploadAllocatedBytes(upload.Size);
                _largeUploadBuffers.RemoveAt(i);
            }
        }

        private void DecrementLargeUploadAllocatedBytes(ulong size)
        {
            _largeUploadAllocatedBytes = size >= _largeUploadAllocatedBytes
                ? 0
                : _largeUploadAllocatedBytes - size;
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

                foreach (LargeUploadBuffer upload in _largeUploadBuffers)
                {
                    if (upload.Buffer.IsValid)
                        _bufferManager.DestroyBuffer(upload.Buffer);
                    DecrementLargeUploadAllocatedBytes(upload.Size);
                }

                _largeUploadBuffers.Clear();
            }
            
            System.Diagnostics.Debug.WriteLine("Staging ring disposed.");
        }

        private readonly record struct LargeUploadBuffer(BufferHandle Buffer, ulong Size, int FrameIndex);
    }
}
