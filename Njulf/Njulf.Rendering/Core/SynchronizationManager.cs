using System;
using System.Collections.Generic;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Manages synchronization primitives for frame rendering.
    /// Uses canonical FramesInFlight constant from RenderingConstants.
    /// </summary>
    public unsafe class SynchronizationManager : IDisposable
    {
        private readonly VulkanContext _context;
        
        // Per-frame synchronization primitives
        private readonly Semaphore[] _imageAvailableSemaphores;
        private readonly List<Semaphore> _renderFinishedSemaphores = new List<Semaphore>();
        private readonly Fence[] _inFlightFences;
        private readonly Semaphore[] _computeFinishedSemaphores;
        private readonly Fence[] _computeFences;
        private readonly Semaphore[] _frameTimelineSemaphores;
        private readonly ulong[] _frameTimelineValues;
        
        // Transfer synchronization
        private Semaphore _transferFinishedSemaphore;
        private Fence _transferFence;
        
        private int _currentFrame = 0;
        private bool _disposed;
        private long _lastFenceWaitMicroseconds;
        private long _lastFenceResetMicroseconds;
        

        
        public SynchronizationManager(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            
            _imageAvailableSemaphores = new Semaphore[FramesInFlight];
            _inFlightFences = new Fence[FramesInFlight];
            _computeFinishedSemaphores = new Semaphore[FramesInFlight];
            _computeFences = new Fence[FramesInFlight];
            _frameTimelineSemaphores = new Semaphore[FramesInFlight];
            _frameTimelineValues = new ulong[FramesInFlight];
            
            CreateSynchronizationPrimitives();
            CreateTransferSynchronization();
        }
        
        private unsafe void CreateSynchronizationPrimitives()
        {
            for (int i = 0; i < FramesInFlight; i++)
            {
                var semaphoreInfo = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo
                };
                Result result = _context.Api.CreateSemaphore(
                    _context.Device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create image available semaphore", result);
                _context.SetDebugName(_imageAvailableSemaphores[i].Handle, ObjectType.Semaphore, $"Image Available Semaphore Frame {i}");
                
                // In-flight fence (signaled by graphics queue submit, waited by CPU)
                var fenceInfo = new FenceCreateInfo
                {
                    SType = StructureType.FenceCreateInfo,
                    Flags = FenceCreateFlags.SignaledBit
                };
                result = _context.Api.CreateFence(
                    _context.Device, &fenceInfo, null, out _inFlightFences[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create in-flight fence", result);
                _context.SetDebugName(_inFlightFences[i].Handle, ObjectType.Fence, $"In Flight Fence Frame {i}");

                result = _context.Api.CreateSemaphore(
                    _context.Device, &semaphoreInfo, null, out _computeFinishedSemaphores[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create async compute finished semaphore", result);
                _context.SetDebugName(_computeFinishedSemaphores[i].Handle, ObjectType.Semaphore, $"Async Compute Finished Semaphore Frame {i}");

                result = _context.Api.CreateFence(
                    _context.Device, &fenceInfo, null, out _computeFences[i]);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create async compute fence", result);
                _context.SetDebugName(_computeFences[i].Handle, ObjectType.Fence, $"Async Compute Fence Frame {i}");

                if (_context.AsyncComputeProfile.TimelineSemaphoresSupported)
                {
                    var timelineInfo = new SemaphoreTypeCreateInfo
                    {
                        SType = StructureType.SemaphoreTypeCreateInfo,
                        SemaphoreType = SemaphoreType.Timeline,
                        InitialValue = 0
                    };
                    var timelineSemaphoreInfo = new SemaphoreCreateInfo
                    {
                        SType = StructureType.SemaphoreCreateInfo,
                        PNext = &timelineInfo
                    };

                    result = _context.Api.CreateSemaphore(
                        _context.Device, &timelineSemaphoreInfo, null, out _frameTimelineSemaphores[i]);
                    if (result != Result.Success)
                        throw new VulkanException("Failed to create frame timeline semaphore", result);
                    _context.SetDebugName(_frameTimelineSemaphores[i].Handle, ObjectType.Semaphore, $"Frame Timeline Semaphore {i}");
                }
            }
            
            System.Diagnostics.Debug.WriteLine("Per-frame synchronization primitives created.");
        }
        
        private void CreateTransferSynchronization()
        {
            // Transfer finished semaphore
            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo
            };
            Result result = _context.Api.CreateSemaphore(
                _context.Device, &semaphoreInfo, null, out _transferFinishedSemaphore);
            if (result != Result.Success)
                throw new VulkanException("Failed to create transfer finished semaphore", result);
            _context.SetDebugName(_transferFinishedSemaphore.Handle, ObjectType.Semaphore, "Transfer Finished Semaphore");
            
            // Transfer fence
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo
            };
            result = _context.Api.CreateFence(
                _context.Device, &fenceInfo, null, out _transferFence);
            if (result != Result.Success)
                throw new VulkanException("Failed to create transfer fence", result);
            _context.SetDebugName(_transferFence.Handle, ObjectType.Fence, "Transfer Fence");
            
            System.Diagnostics.Debug.WriteLine("Transfer synchronization primitives created.");
        }
        
        /// <summary>
        /// Gets the current frame index.
        /// </summary>
        public int CurrentFrame => _currentFrame;
        public long LastFenceWaitMicroseconds => _lastFenceWaitMicroseconds;
        public long LastFenceResetMicroseconds => _lastFenceResetMicroseconds;
        
        /// <summary>
        /// Gets the image available semaphore for the current frame.
        /// </summary>
        public Semaphore GetImageAvailableSemaphore(int frameIndex = -1)
        {
            return _imageAvailableSemaphores[frameIndex < 0 ? _currentFrame : frameIndex];
        }
        
        /// <summary>
        /// Gets the render finished semaphore for the current frame.
        /// </summary>
        public Semaphore GetRenderFinishedSemaphore(int frameIndex = -1)
        {
            int index = frameIndex < 0 ? _currentFrame : frameIndex;
            if ((uint)index >= (uint)_renderFinishedSemaphores.Count)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), "Render-finished semaphore index has not been initialized.");

            return _renderFinishedSemaphores[index];
        }

        /// <summary>
        /// Gets the render-finished semaphore associated with a swapchain image.
        /// Presentation can keep this semaphore in use until that exact image is reacquired.
        /// </summary>
        public Semaphore GetRenderFinishedSemaphoreForImage(uint imageIndex)
        {
            if (imageIndex >= _renderFinishedSemaphores.Count)
                throw new ArgumentOutOfRangeException(nameof(imageIndex), "Render-finished semaphore for swapchain image has not been initialized.");

            return _renderFinishedSemaphores[(int)imageIndex];
        }

        public void EnsureRenderFinishedSemaphoreCapacity(uint swapchainImageCount)
        {
            while (_renderFinishedSemaphores.Count < swapchainImageCount)
            {
                var semaphoreInfo = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo
                };

                Result result = _context.Api.CreateSemaphore(
                    _context.Device,
                    &semaphoreInfo,
                    null,
                    out Semaphore semaphore);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create render finished semaphore", result);

                _renderFinishedSemaphores.Add(semaphore);
                _context.SetDebugName(semaphore.Handle, ObjectType.Semaphore, $"Render Finished Semaphore Image {_renderFinishedSemaphores.Count - 1}");
            }
        }
        
        /// <summary>
        /// Gets the in-flight fence for the current frame.
        /// </summary>
        public Fence GetInFlightFence(int frameIndex = -1)
        {
            return _inFlightFences[frameIndex < 0 ? _currentFrame : frameIndex];
        }

        public Semaphore GetComputeFinishedSemaphore(int frameIndex = -1)
        {
            return _computeFinishedSemaphores[frameIndex < 0 ? _currentFrame : frameIndex];
        }

        public Fence GetComputeFence(int frameIndex = -1)
        {
            return _computeFences[frameIndex < 0 ? _currentFrame : frameIndex];
        }

        public bool TimelineSemaphoresEnabled => _context.AsyncComputeProfile.TimelineSemaphoresSupported &&
                                                 _frameTimelineSemaphores[0].Handle != 0;

        public Semaphore GetFrameTimelineSemaphore(int frameIndex = -1)
        {
            return _frameTimelineSemaphores[frameIndex < 0 ? _currentFrame : frameIndex];
        }

        public ulong NextFrameTimelineValue(int frameIndex = -1)
        {
            int index = frameIndex < 0 ? _currentFrame : frameIndex;
            return ++_frameTimelineValues[index];
        }

        public ulong GetCurrentFrameTimelineValue(int frameIndex = -1)
        {
            return _frameTimelineValues[frameIndex < 0 ? _currentFrame : frameIndex];
        }
        
        /// <summary>
        /// Gets the transfer finished semaphore.
        /// </summary>
        public Semaphore TransferFinishedSemaphore => _transferFinishedSemaphore;
        
        /// <summary>
        /// Gets the transfer fence.
        /// </summary>
        public Fence TransferFence => _transferFence;
        
        /// <summary>
        /// Waits for the current frame's in-flight fence.
        /// </summary>
        public void WaitForInFlightFence()
        {
            WaitForFence(_currentFrame);
        }

        /// <summary>
        /// Waits for the specified frame's in-flight fence.
        /// </summary>
        public void WaitForFence(int frameIndex)
        {
            var fence = _inFlightFences[frameIndex];
            long start = Stopwatch.GetTimestamp();
            Result result = _context.Api.WaitForFences(
                _context.Device, 1, &fence, true, ulong.MaxValue);
            _lastFenceWaitMicroseconds = ElapsedMicroseconds(start);
            if (result != Result.Success)
                throw new VulkanException("Failed to wait for in-flight fence", result);
        }
        
        /// <summary>
        /// Resets the current frame's in-flight fence.
        /// </summary>
        public void ResetFence(int frameIndex = -1)
        {
            int index = frameIndex < 0 ? _currentFrame : frameIndex;
            var fence = _inFlightFences[index];
            long start = Stopwatch.GetTimestamp();
            Result result = _context.Api.ResetFences(_context.Device, 1, &fence);
            _lastFenceResetMicroseconds = ElapsedMicroseconds(start);
            if (result != Result.Success)
                throw new VulkanException("Failed to reset fence", result);
        }
        
        /// <summary>
        /// Advances to the next frame.
        /// </summary>
        public void AdvanceFrame()
        {
            _currentFrame = (_currentFrame + 1) % FramesInFlight;
        }
        
        /// <summary>
        /// Gets the current frame index.
        /// </summary>
        public int GetCurrentFrameIndex() => _currentFrame;

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            // Destroy per-frame primitives
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (_imageAvailableSemaphores[i].Handle != 0)
                    _context.Api.DestroySemaphore(_context.Device, _imageAvailableSemaphores[i], null);
                
                if (_inFlightFences[i].Handle != 0)
                    _context.Api.DestroyFence(_context.Device, _inFlightFences[i], null);

                if (_computeFinishedSemaphores[i].Handle != 0)
                    _context.Api.DestroySemaphore(_context.Device, _computeFinishedSemaphores[i], null);

                if (_computeFences[i].Handle != 0)
                    _context.Api.DestroyFence(_context.Device, _computeFences[i], null);

                if (_frameTimelineSemaphores[i].Handle != 0)
                    _context.Api.DestroySemaphore(_context.Device, _frameTimelineSemaphores[i], null);
            }

            foreach (Semaphore semaphore in _renderFinishedSemaphores)
            {
                if (semaphore.Handle != 0)
                    _context.Api.DestroySemaphore(_context.Device, semaphore, null);
            }
            
            // Destroy transfer primitives
            if (_transferFinishedSemaphore.Handle != 0)
                _context.Api.DestroySemaphore(_context.Device, _transferFinishedSemaphore, null);
            
            if (_transferFence.Handle != 0)
                _context.Api.DestroyFence(_context.Device, _transferFence, null);
            
            System.Diagnostics.Debug.WriteLine("Synchronization manager disposed.");
        }
    }
}
