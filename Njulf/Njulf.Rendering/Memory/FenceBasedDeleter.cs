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
        private readonly DurableFenceDeletionQueue _deletions = new();

        public FenceBasedDeleter(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void QueueDeletion(Fence fence, Action deletionAction)
            => _deletions.QueueDeletion(fence, deletionAction);

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
            => _deletions.ProcessCompletedFence(fence);

        public void WaitAndProcess(Fence fence)
        {
            _deletions.ThrowIfDisposed();
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
            => _deletions.Cleanup();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            _deletions.Dispose();

            System.Diagnostics.Debug.WriteLine("Fence-based deleter disposed.");
        }

        internal static void ExecutePendingActions(Queue<Action> deletions)
            => DurableFenceDeletionQueue.ExecutePendingActions(deletions);
    }

    /// <summary>
    /// Context-independent durable queue behind <see cref="FenceBasedDeleter"/>.
    /// Successful heads are removed immediately, failed heads are retained,
    /// and disposal can safely be retried without repeating completed work.
    /// </summary>
    internal sealed class DurableFenceDeletionQueue : IDisposable
    {
        private readonly Dictionary<Fence, Queue<Action>> _pending = new();
        private readonly HashSet<Fence> _processingFences = new();
        private readonly object _gate = new();
        private bool _cleanupInProgress;
        private bool _disposeStarted;
        private bool _disposed;
        private int _cleanupOwnerThreadId;
        private int _callbackDepth;

        internal int PendingFenceCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        internal int PendingActionCount
        {
            get
            {
                lock (_gate)
                {
                    int count = 0;
                    foreach (Queue<Action> actions in _pending.Values)
                        count = checked(count + actions.Count);
                    return count;
                }
            }
        }

        internal void QueueDeletion(Fence fence, Action deletionAction)
        {
            ArgumentNullException.ThrowIfNull(deletionAction);
            lock (_gate)
            {
                EnsureQueueAcceptedLocked();

                // No fence means no deferred ownership boundary. Execute now
                // so a caller can neither silently leak the resource nor
                // mistake a dropped callback for successful retirement.
                if (fence.Handle == 0)
                {
                    ExecuteCallbackLocked(deletionAction);
                    return;
                }

                if (!_pending.TryGetValue(fence, out Queue<Action>? actions))
                {
                    actions = new Queue<Action>();
                    _pending.Add(fence, actions);
                }

                actions.Enqueue(deletionAction);
            }
        }

        internal void ProcessCompletedFence(Fence fence)
        {
            if (fence.Handle == 0)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;

                ExecuteAsCleanupOwnerLocked(
                    () => ProcessFenceLocked(fence));
            }
        }

        internal void Cleanup()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                ExecuteAsCleanupOwnerLocked(CleanupLocked);
            }
        }

        internal void ThrowIfDisposed()
        {
            lock (_gate)
            {
                if (_disposeStarted || _disposed)
                    throw new ObjectDisposedException(nameof(FenceBasedDeleter));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (_cleanupInProgress &&
                    _cleanupOwnerThreadId ==
                    Environment.CurrentManagedThreadId)
                {
                    // The owning outer cleanup will observe every action
                    // queued by this callback and complete disposal.
                    return;
                }

                // This state is intentionally monotonic. If cleanup fails, new
                // external work remains rejected while Cleanup/Dispose can
                // resume the retained failed heads.
                _disposeStarted = true;
                ExecuteAsCleanupOwnerLocked(CleanupLocked);
                _disposed = true;
            }
        }

        private void CleanupLocked()
        {
            if (_cleanupInProgress)
                return;

            _cleanupInProgress = true;
            var failedFences = new HashSet<Fence>();
            List<Exception>? failures = null;
            try
            {
                while (true)
                {
                    List<Fence> candidates = [];
                    foreach (Fence fence in _pending.Keys)
                    {
                        if (!failedFences.Contains(fence))
                            candidates.Add(fence);
                    }

                    if (candidates.Count == 0)
                        break;

                    foreach (Fence fence in candidates)
                    {
                        try
                        {
                            ProcessFenceLocked(fence);
                        }
                        catch (Exception exception)
                        {
                            failedFences.Add(fence);
                            (failures ??= []).Add(exception);
                        }
                    }
                }
            }
            finally
            {
                _cleanupInProgress = false;
            }

            ThrowCleanupFailures(failures);
        }

        private void ProcessFenceLocked(Fence fence)
        {
            // A callback may reentrantly process its own fence. The outer call
            // still owns the head action, so the nested request is a no-op.
            if (!_processingFences.Add(fence))
                return;

            try
            {
                if (!_pending.TryGetValue(
                        fence,
                        out Queue<Action>? deletions))
                {
                    return;
                }

                ExecutePendingActionsLocked(deletions);
                if (deletions.Count == 0)
                    _pending.Remove(fence);
            }
            finally
            {
                _processingFences.Remove(fence);
            }
        }

        private void ExecutePendingActionsLocked(Queue<Action> deletions)
        {
            while (deletions.Count > 0)
            {
                Action deletion = deletions.Peek();
                ExecuteCallbackLocked(deletion);
                _ = deletions.Dequeue();
            }
        }

        private void ExecuteCallbackLocked(Action deletion)
        {
            _callbackDepth++;
            try
            {
                deletion();
            }
            finally
            {
                _callbackDepth--;
            }
        }

        private void EnsureQueueAcceptedLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FenceBasedDeleter));

            if (_disposeStarted &&
                (_callbackDepth == 0 ||
                 _cleanupOwnerThreadId != Environment.CurrentManagedThreadId))
            {
                throw new ObjectDisposedException(
                    nameof(FenceBasedDeleter),
                    "Deletion queue disposal has started; only reentrant cleanup callbacks may enqueue follow-up work.");
            }
        }

        private void ExecuteAsCleanupOwnerLocked(Action action)
        {
            int currentThreadId = Environment.CurrentManagedThreadId;
            int previousOwner = _cleanupOwnerThreadId;
            if (previousOwner != 0 && previousOwner != currentThreadId)
            {
                throw new InvalidOperationException(
                    "Deletion cleanup ownership changed while its gate was held.");
            }

            _cleanupOwnerThreadId = currentThreadId;
            try
            {
                action();
            }
            finally
            {
                _cleanupOwnerThreadId = previousOwner;
            }
        }

        private static void ThrowCleanupFailures(List<Exception>? failures)
        {
            if (failures is not { Count: > 0 })
                return;
            if (failures.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(failures[0])
                    .Throw();
            }

            throw new AggregateException(
                "One or more deferred deletion fences remain incomplete.",
                failures);
        }

        internal static void ExecutePendingActions(Queue<Action> deletions)
        {
            ArgumentNullException.ThrowIfNull(deletions);
            while (deletions.Count > 0)
            {
                Action deletion = deletions.Peek();
                deletion();
                _ = deletions.Dequeue();
            }
        }
    }
}
