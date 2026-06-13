using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Manages command pools and command buffers for graphics and transfer operations.
    /// </summary>
    public unsafe class CommandBufferManager : IDisposable
    {
        private readonly VulkanContext _context;
        
        // Graphics command pool and buffers
        private CommandPool _graphicsCommandPool;
        private CommandBuffer[] _graphicsCommandBuffers = Array.Empty<CommandBuffer>();
        private readonly CommandPool[] _secondaryGraphicsCommandPools = new CommandPool[FramesInFlight];
        private readonly List<CommandBuffer>[] _secondaryGraphicsCommandBuffers = new List<CommandBuffer>[FramesInFlight];
        private readonly int[] _secondaryGraphicsCommandBufferCursors = new int[FramesInFlight];
        
        // Transfer command pool and buffer (if dedicated queue)
        private CommandPool _transferCommandPool;
        private CommandBuffer _transferCommandBuffer;
        
        private bool _disposed;
        
        public CommandBufferManager(VulkanContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            
            CreateGraphicsCommandPool();
            AllocateGraphicsCommandBuffers();
            CreateSecondaryGraphicsCommandPools();
            
            if (context.HasDedicatedTransferQueue)
                CreateTransferCommandPool();
        }
        
        private void CreateGraphicsCommandPool()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit
            };
            
            Result result = _context.Api.CreateCommandPool(
                _context.Device, &poolInfo, null, out _graphicsCommandPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create graphics command pool", result);
            _context.SetDebugName(_graphicsCommandPool.Handle, ObjectType.CommandPool, "Graphics Command Pool");
            
            System.Diagnostics.Debug.WriteLine("Graphics command pool created.");
        }
        
        private void AllocateGraphicsCommandBuffers()
        {
            _graphicsCommandBuffers = new CommandBuffer[RenderingConstants.FramesInFlight];
            
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _graphicsCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = RenderingConstants.FramesInFlight
            };
            
            Result result;
            fixed (CommandBuffer* commandBuffersPtr = _graphicsCommandBuffers)
            {
                result = _context.Api.AllocateCommandBuffers(
                    _context.Device, &allocInfo, commandBuffersPtr);
            }
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate graphics command buffers", result);
            for (int i = 0; i < _graphicsCommandBuffers.Length; i++)
                _context.SetDebugName(_graphicsCommandBuffers[i].Handle, ObjectType.CommandBuffer, $"Graphics Command Buffer Frame {i}");
            
            System.Diagnostics.Debug.WriteLine("Graphics command buffers allocated.");
        }

        private void CreateSecondaryGraphicsCommandPools()
        {
            for (int i = 0; i < FramesInFlight; i++)
            {
                var poolInfo = new CommandPoolCreateInfo
                {
                    SType = StructureType.CommandPoolCreateInfo,
                    QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
                    Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit
                };

                Result result = _context.Api.CreateCommandPool(
                    _context.Device, &poolInfo, null, out _secondaryGraphicsCommandPools[i]);
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create secondary graphics command pool for frame {i}", result);

                _context.SetDebugName(_secondaryGraphicsCommandPools[i].Handle, ObjectType.CommandPool, $"Secondary Graphics Command Pool Frame {i}");
                _secondaryGraphicsCommandBuffers[i] = new List<CommandBuffer>();
            }
        }
        
        private void CreateTransferCommandPool()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _context.TransferQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit
            };
            
            Result result = _context.Api.CreateCommandPool(
                _context.Device, &poolInfo, null, out _transferCommandPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create transfer command pool", result);
            _context.SetDebugName(_transferCommandPool.Handle, ObjectType.CommandPool, "Transfer Command Pool");
            
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _transferCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            
            CommandBuffer transferCommandBuffer;
            result = _context.Api.AllocateCommandBuffers(
                _context.Device, &allocInfo, &transferCommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate transfer command buffer", result);
            _transferCommandBuffer = transferCommandBuffer;
            _context.SetDebugName(_transferCommandBuffer.Handle, ObjectType.CommandBuffer, "Transfer Command Buffer");
            
            System.Diagnostics.Debug.WriteLine("Transfer command pool and buffer created.");
        }
        
        /// <summary>
        /// Begins recording a primary graphics command buffer for the specified frame.
        /// </summary>
        public CommandBuffer BeginPrimaryGraphicsCommand(int frameIndex)
        {
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.None,
                PInheritanceInfo = null
            };
            
            Result result = _context.Api.BeginCommandBuffer(
                _graphicsCommandBuffers[frameIndex], &beginInfo);
            if (result != Result.Success)
                throw new VulkanException("Failed to begin command buffer recording", result);
            
            return _graphicsCommandBuffers[frameIndex];
        }
        
        /// <summary>
        /// Ends recording of a command buffer.
        /// </summary>
        public void EndCommandBuffer(CommandBuffer commandBuffer)
        {
            Result result = _context.Api.EndCommandBuffer(commandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end command buffer recording", result);
        }
        
        /// <summary>
        /// Gets the primary graphics command buffer for the specified frame.
        /// </summary>
        public CommandBuffer GetGraphicsCommandBuffer(int frameIndex)
        {
            return _graphicsCommandBuffers[frameIndex];
        }
        
        /// <summary>
        /// Resets a graphics command buffer.
        /// </summary>
        public void ResetGraphicsCommandBuffer(int frameIndex)
        {
            Result result = _context.Api.ResetCommandBuffer(
                _graphicsCommandBuffers[frameIndex], CommandBufferResetFlags.None);
            if (result != Result.Success)
                throw new VulkanException("Failed to reset command buffer", result);
        }

        public void ResetSecondaryGraphicsCommandPool(int frameIndex)
        {
            if ((uint)frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
            if (_secondaryGraphicsCommandPools[frameIndex].Handle == 0)
                return;

            Result result = _context.Api.ResetCommandPool(
                _context.Device,
                _secondaryGraphicsCommandPools[frameIndex],
                CommandPoolResetFlags.None);
            if (result != Result.Success)
                throw new VulkanException("Failed to reset secondary graphics command pool", result);

            _secondaryGraphicsCommandBufferCursors[frameIndex] = 0;
        }
        
        /// <summary>
        /// Resets all graphics command buffers.
        /// </summary>
        public void ResetAllGraphicsCommandBuffers()
        {
            for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            {
                ResetGraphicsCommandBuffer(i);
                ResetSecondaryGraphicsCommandPool(i);
            }
        }

        public CommandBuffer BeginSecondaryGraphicsCommand(int frameIndex, string debugName)
        {
            if ((uint)frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));

            List<CommandBuffer> buffers = _secondaryGraphicsCommandBuffers[frameIndex];
            int cursor = _secondaryGraphicsCommandBufferCursors[frameIndex]++;
            CommandBuffer cmd;
            if (cursor < buffers.Count)
            {
                cmd = buffers[cursor];
            }
            else
            {
                var allocInfo = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = _secondaryGraphicsCommandPools[frameIndex],
                    Level = CommandBufferLevel.Secondary,
                    CommandBufferCount = 1
                };

                Result result = _context.Api.AllocateCommandBuffers(_context.Device, &allocInfo, out cmd);
                if (result != Result.Success)
                    throw new VulkanException("Failed to allocate secondary graphics command buffer", result);

                buffers.Add(cmd);
            }

            _context.SetDebugName(cmd.Handle, ObjectType.CommandBuffer, $"Secondary Graphics Command Buffer Frame {frameIndex} {debugName}");

            var inheritanceInfo = new CommandBufferInheritanceInfo
            {
                SType = StructureType.CommandBufferInheritanceInfo,
                RenderPass = default,
                Subpass = 0,
                Framebuffer = default,
                OcclusionQueryEnable = false,
                QueryFlags = QueryControlFlags.None,
                PipelineStatistics = QueryPipelineStatisticFlags.None
            };

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                PInheritanceInfo = &inheritanceInfo
            };

            Result beginResult = _context.Api.BeginCommandBuffer(cmd, &beginInfo);
            if (beginResult != Result.Success)
                throw new VulkanException("Failed to begin secondary graphics command buffer recording", beginResult);

            return cmd;
        }

        public void ExecuteSecondaryGraphicsCommand(CommandBuffer primary, CommandBuffer secondary)
        {
            _context.Api.CmdExecuteCommands(primary, 1, &secondary);
        }
        
        /// <summary>
        /// Begins a single-time command buffer (for one-off operations).
        /// </summary>
        public CommandBuffer BeginSingleTimeCommands()
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _graphicsCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            
            CommandBuffer cmd;
            Result result = _context.Api.AllocateCommandBuffers(
                _context.Device, &allocInfo, &cmd);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate single-time command buffer", result);
            _context.SetDebugName(cmd.Handle, ObjectType.CommandBuffer, "Single Time Graphics Command Buffer");
            
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                PInheritanceInfo = null
            };
            
            result = _context.Api.BeginCommandBuffer(cmd, &beginInfo);
            if (result != Result.Success)
            {
                _context.Api.FreeCommandBuffers(_context.Device, _graphicsCommandPool, 1, &cmd);
                throw new VulkanException("Failed to begin single-time command buffer", result);
            }
            
            return cmd;
        }
        
        /// <summary>
        /// Ends a single-time command buffer and submits it.
        /// </summary>
        public void EndSingleTimeCommands(CommandBuffer cmd)
        {
            Result result = _context.Api.EndCommandBuffer(cmd);
            if (result != Result.Success)
                throw new VulkanException("Failed to end single-time command buffer", result);
            
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd
            };

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo
            };

            result = _context.Api.CreateFence(_context.Device, &fenceInfo, null, out Fence fence);
            if (result != Result.Success)
                throw new VulkanException("Failed to create single-time command fence", result);
            
            result = _context.Api.QueueSubmit(
                _context.GraphicsQueue, 1, &submitInfo, fence);
            if (result != Result.Success)
            {
                _context.Api.DestroyFence(_context.Device, fence, null);
                throw new VulkanException("Failed to submit single-time commands", result);
            }
            
            result = _context.Api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue);
            if (result != Result.Success)
            {
                _context.Api.DestroyFence(_context.Device, fence, null);
                throw new VulkanException("Failed to wait for single-time command fence", result);
            }

            _context.Api.DestroyFence(_context.Device, fence, null);
            
            _context.Api.FreeCommandBuffers(_context.Device, _graphicsCommandPool, 1, &cmd);
        }
        
        /// <summary>
        /// Begins recording the transfer command buffer.
        /// </summary>
        public CommandBuffer BeginTransferCommands()
        {
            if (!_context.HasDedicatedTransferQueue)
                throw new InvalidOperationException("No dedicated transfer queue available");
            
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.None,
                PInheritanceInfo = null
            };
            
            Result result = _context.Api.BeginCommandBuffer(
                _transferCommandBuffer, &beginInfo);
            if (result != Result.Success)
                throw new VulkanException("Failed to begin transfer command buffer", result);
            
            return _transferCommandBuffer;
        }
        
        /// <summary>
        /// Ends recording of the transfer command buffer.
        /// </summary>
        public void EndTransferCommands()
        {
            if (!_context.HasDedicatedTransferQueue)
                return;
            
            Result result = _context.Api.EndCommandBuffer(_transferCommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end transfer command buffer", result);
        }
        
        /// <summary>
        /// Submits the transfer command buffer.
        /// </summary>
        public void SubmitTransferCommands(Semaphore signalSemaphore = default)
        {
            if (!_context.HasDedicatedTransferQueue)
                return;
            
            var transferCommandBuffer = _transferCommandBuffer;
            var signalSemaphoreLocal = signalSemaphore;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &transferCommandBuffer,
                SignalSemaphoreCount = signalSemaphore.Handle != 0 ? 1u : 0u,
                PSignalSemaphores = signalSemaphore.Handle != 0 ? &signalSemaphoreLocal : null
            };
            
            Result result = _context.Api.QueueSubmit(
                _context.TransferQueue, 1, &submitInfo, default);
            if (result != Result.Success)
                throw new VulkanException("Failed to submit transfer commands", result);
        }
        
        /// <summary>
        /// Resets the transfer command buffer.
        /// </summary>
        public void ResetTransferCommandBuffer()
        {
            if (!_context.HasDedicatedTransferQueue)
                return;
            
            Result result = _context.Api.ResetCommandBuffer(
                _transferCommandBuffer, CommandBufferResetFlags.None);
            if (result != Result.Success)
                throw new VulkanException("Failed to reset transfer command buffer", result);
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
            
            // Free command buffers
            if (_graphicsCommandPool.Handle != 0)
            {
                fixed (CommandBuffer* commandBuffersPtr = _graphicsCommandBuffers)
                {
                    _context.Api.FreeCommandBuffers(
                        _context.Device, _graphicsCommandPool,
                        RenderingConstants.FramesInFlight, commandBuffersPtr);
                }
                
                _context.Api.DestroyCommandPool(_context.Device, _graphicsCommandPool, null);
            }

            for (int i = 0; i < _secondaryGraphicsCommandPools.Length; i++)
            {
                if (_secondaryGraphicsCommandPools[i].Handle == 0)
                    continue;

                List<CommandBuffer>? buffers = _secondaryGraphicsCommandBuffers[i];
                if (buffers is { Count: > 0 })
                {
                    CommandBuffer[] secondaryBuffers = buffers.ToArray();
                    fixed (CommandBuffer* secondaryBuffersPtr = secondaryBuffers)
                    {
                        _context.Api.FreeCommandBuffers(
                            _context.Device,
                            _secondaryGraphicsCommandPools[i],
                            (uint)secondaryBuffers.Length,
                            secondaryBuffersPtr);
                    }
                }

                _context.Api.DestroyCommandPool(_context.Device, _secondaryGraphicsCommandPools[i], null);
            }
            
            if (_transferCommandPool.Handle != 0)
            {
                if (_transferCommandBuffer.Handle != 0)
                {
                    var transferCommandBuffer = _transferCommandBuffer;
                    _context.Api.FreeCommandBuffers(
                        _context.Device, _transferCommandPool, 1, &transferCommandBuffer);
                }
                
                _context.Api.DestroyCommandPool(_context.Device, _transferCommandPool, null);
            }
            
            System.Diagnostics.Debug.WriteLine("Command buffer manager disposed.");
        }
    }
}
