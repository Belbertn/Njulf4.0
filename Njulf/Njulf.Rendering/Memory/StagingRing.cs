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
        private readonly ulong[] _largeUploadBytesThisFrame;
        private readonly int[] _overflowCountThisFrame;
        private readonly List<OverflowBufferEntry> _overflowBuffers = new();
        private readonly ulong _bufferSize;
        private readonly int _maxRetainedOverflowBufferCount;
        private readonly ulong _maxRetainedOverflowBytes;
        private readonly ulong _maxSingleRetainedOverflowBufferSize;
        private readonly uint _minAlignment;
        private ulong _overflowAllocatedBytes;
        private ulong _peakOverflowBytesThisSession;
        private ulong _largestOverflowAllocationBytes;
        private ulong _peakBytesThisSession;
        private int _overflowCount;
        
        private int _currentFrame = 0;
        private bool _disposed;
        

        public const ulong DefaultStagingBufferSize = 16 * 1024 * 1024;
        public const uint DefaultMinAlignment = 256;
        public const int DefaultMaxRetainedOverflowBufferCount = 4;
        public const ulong DefaultMaxRetainedOverflowBytes = 64 * 1024 * 1024;
        public const ulong DefaultMaxSingleRetainedOverflowBufferSize = 64 * 1024 * 1024;
        
        public StagingRing(
            VulkanContext context,
            BufferManager bufferManager,
            ulong bufferSize = DefaultStagingBufferSize,
            uint minAlignment = DefaultMinAlignment,
            int maxRetainedOverflowBufferCount = DefaultMaxRetainedOverflowBufferCount,
            ulong maxRetainedOverflowBytes = DefaultMaxRetainedOverflowBytes,
            ulong maxSingleRetainedOverflowBufferSize = DefaultMaxSingleRetainedOverflowBufferSize)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _bufferSize = bufferSize;
            _minAlignment = minAlignment;
            _maxRetainedOverflowBufferCount = Math.Max(0, maxRetainedOverflowBufferCount);
            _maxRetainedOverflowBytes = maxRetainedOverflowBytes;
            _maxSingleRetainedOverflowBufferSize = maxSingleRetainedOverflowBufferSize;
            
            _stagingBuffers = new BufferHandle[FramesInFlight];
            _currentOffsets = new ulong[FramesInFlight];
            _frameHighWater = new ulong[FramesInFlight];
            _largeUploadBytesThisFrame = new ulong[FramesInFlight];
            _overflowCountThisFrame = new int[FramesInFlight];
            
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
                RetireCompletedOverflowBuffers(_currentFrame);
                _currentOffsets[_currentFrame] = 0;
                _frameHighWater[_currentFrame] = 0;
                _largeUploadBytesThisFrame[_currentFrame] = 0;
                _overflowCountThisFrame[_currentFrame] = 0;
            }
        }

        public void AdvanceFrame()
        {
            lock (_lock)
            {
                _currentFrame++;
                int resetIndex = _currentFrame % FramesInFlight;
                RetireCompletedOverflowBuffers(resetIndex);
                _currentOffsets[resetIndex] = 0;
                _frameHighWater[resetIndex] = 0;
                _largeUploadBytesThisFrame[resetIndex] = 0;
                _overflowCountThisFrame[resetIndex] = 0;
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
                    return checked(_bufferSize * FramesInFlight + _overflowAllocatedBytes);
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
        public int CurrentFrameOverflowCount
        {
            get
            {
                lock (_lock)
                    return _overflowCountThisFrame[CurrentFrameIndex];
            }
        }
        public int RetainedOverflowBufferCount
        {
            get
            {
                lock (_lock)
                    return _overflowBuffers.Count;
            }
        }
        public ulong RetainedOverflowBytes
        {
            get
            {
                lock (_lock)
                    return _overflowAllocatedBytes;
            }
        }
        public ulong PeakOverflowBytesThisSession
        {
            get
            {
                lock (_lock)
                    return _peakOverflowBytesThisSession;
            }
        }
        public ulong LargestOverflowAllocationBytes
        {
            get
            {
                lock (_lock)
                    return _largestOverflowAllocationBytes;
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
            _overflowCountThisFrame[frameIndex]++;
            ulong dedicatedSize = AlignUp(Math.Max(size, _bufferSize), _minAlignment);
            OverflowBufferEntry entry = RentOverflowBuffer(dedicatedSize, frameIndex);
            _largeUploadBytesThisFrame[frameIndex] = checked(_largeUploadBytesThisFrame[frameIndex] + size);
            _largestOverflowAllocationBytes = Math.Max(_largestOverflowAllocationBytes, dedicatedSize);
            _peakBytesThisSession = Math.Max(_peakBytesThisSession, _currentOffsets[frameIndex] + _largeUploadBytesThisFrame[frameIndex]);
            _peakOverflowBytesThisSession = Math.Max(_peakOverflowBytesThisSession, _overflowAllocatedBytes);
            return (entry.Buffer, 0);
        }

        private OverflowBufferEntry RentOverflowBuffer(ulong dedicatedSize, int frameIndex)
        {
            OverflowBufferEntry? bestEntry = null;
            foreach (OverflowBufferEntry entry in _overflowBuffers)
            {
                if (entry.InUse || entry.Size < dedicatedSize)
                    continue;

                if (bestEntry == null || entry.Size < bestEntry.Size)
                    bestEntry = entry;
            }

            if (bestEntry == null)
            {
                BufferHandle buffer = _bufferManager.CreateStagingBuffer(
                    dedicatedSize,
                    $"Staging Ring Overflow {dedicatedSize} bytes #{_overflowCount}");
                bestEntry = new OverflowBufferEntry(buffer, dedicatedSize);
                _overflowBuffers.Add(bestEntry);
                _overflowAllocatedBytes = checked(_overflowAllocatedBytes + dedicatedSize);
            }

            bestEntry.InUse = true;
            bestEntry.FrameIndex = frameIndex;
            bestEntry.LastUsedFrameIndex = _currentFrame;
            return bestEntry;
        }

        private void RetireCompletedOverflowBuffers(int frameIndex)
        {
            for (int i = _overflowBuffers.Count - 1; i >= 0; i--)
            {
                OverflowBufferEntry entry = _overflowBuffers[i];
                if (!entry.InUse || entry.FrameIndex != frameIndex)
                    continue;

                entry.InUse = false;
                entry.LastUsedFrameIndex = _currentFrame;
                if (entry.Size > _maxSingleRetainedOverflowBufferSize)
                    DestroyOverflowBufferAt(i);
            }

            TrimRetainedOverflowBuffers();
        }

        private void TrimRetainedOverflowBuffers()
        {
            while (_overflowBuffers.Count > _maxRetainedOverflowBufferCount ||
                   _overflowAllocatedBytes > _maxRetainedOverflowBytes)
            {
                int trimIndex = FindLargestFreeOverflowBufferIndex();
                if (trimIndex < 0)
                    return;

                DestroyOverflowBufferAt(trimIndex);
            }
        }

        private int FindLargestFreeOverflowBufferIndex()
        {
            int bestIndex = -1;
            ulong bestSize = 0;
            for (int i = 0; i < _overflowBuffers.Count; i++)
            {
                OverflowBufferEntry entry = _overflowBuffers[i];
                if (entry.InUse || entry.Size < bestSize)
                    continue;

                bestIndex = i;
                bestSize = entry.Size;
            }

            return bestIndex;
        }

        private void DestroyOverflowBufferAt(int index)
        {
            OverflowBufferEntry entry = _overflowBuffers[index];
            if (entry.Buffer.IsValid)
                _bufferManager.DestroyBuffer(entry.Buffer);
            _overflowAllocatedBytes -= entry.Size;
            _overflowBuffers.RemoveAt(index);
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

                foreach (OverflowBufferEntry entry in _overflowBuffers)
                {
                    if (entry.Buffer.IsValid)
                        _bufferManager.DestroyBuffer(entry.Buffer);
                }

                _overflowBuffers.Clear();
                _overflowAllocatedBytes = 0;
            }
            
            System.Diagnostics.Debug.WriteLine("Staging ring disposed.");
        }

        private sealed class OverflowBufferEntry
        {
            public OverflowBufferEntry(BufferHandle buffer, ulong size)
            {
                Buffer = buffer;
                Size = size;
            }

            public BufferHandle Buffer { get; }
            public ulong Size { get; }
            public bool InUse { get; set; }
            public int FrameIndex { get; set; } = -1;
            public int LastUsedFrameIndex { get; set; } = -1;
        }
    }
}
