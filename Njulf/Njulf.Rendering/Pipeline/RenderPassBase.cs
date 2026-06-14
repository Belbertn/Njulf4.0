using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Njulf.Rendering.Data;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Abstract base class for render passes.
    /// Each render pass can execute commands and has dependencies.
    /// </summary>
    public abstract class RenderPassBase : IDisposable
    {
        protected readonly VulkanContext _context;
        protected readonly SwapchainManager _swapchain;
        protected readonly BindlessHeap _bindlessHeap;
        
        private bool _disposed;
        
        public string Name { get; }
        internal VulkanContext Context => _context;
        public virtual bool SupportsSecondaryCommandBuffer => false;
        
        public RenderPassBase(
            string name,
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _swapchain = swapchain ?? throw new ArgumentNullException(nameof(swapchain));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        }
        
        /// <summary>
        /// Initializes the render pass (pipeline creation, etc.).
        /// </summary>
        public abstract void Initialize();
        
        /// <summary>
        /// Executes the render pass commands.
        /// </summary>
        /// <param name="cmd">The command buffer to record into</param>
        /// <param name="frameIndex">Current frame index (0 or 1)</param>
        /// <param name="sceneData">Scene data for this frame</param>
        public abstract void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData);
        
        /// <summary>
        /// Gets the barriers needed before this pass executes.
        /// </summary>
        public virtual IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        protected unsafe void SetFullViewportAndScissor(CommandBuffer cmd, Extent2D extent)
        {
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = extent.Width,
                Height = extent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = extent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
        }

        protected unsafe void BindBindlessStorageAndTextures(
            CommandBuffer cmd,
            PipelineLayout layout,
            PipelineBindPoint bindPoint = PipelineBindPoint.Graphics)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(cmd, bindPoint, layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, bindPoint, layout, 1, 1, &textureSet, 0, null);
        }

        protected static RenderingAttachmentInfo ColorAttachment(
            ImageView view,
            ImageLayout layout,
            AttachmentLoadOp loadOp,
            AttachmentStoreOp storeOp,
            ClearValue clearValue = default)
        {
            return new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = layout,
                LoadOp = loadOp,
                StoreOp = storeOp,
                ClearValue = clearValue
            };
        }

        protected static RenderingAttachmentInfo DepthAttachment(
            ImageView view,
            ImageLayout layout,
            AttachmentLoadOp loadOp,
            AttachmentStoreOp storeOp,
            ClearValue clearValue = default)
        {
            return new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = layout,
                LoadOp = loadOp,
                StoreOp = storeOp,
                ClearValue = clearValue
            };
        }
        
        /// <summary>
        /// Called when the swapchain is recreated.
        /// </summary>
        public virtual void OnSwapchainRecreated()
        {
            // Recreate any swapchain-dependent resources
        }
        
        /// <summary>
        /// Cleans up the render pass.
        /// </summary>
        public virtual void Cleanup()
        {
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
            
            Cleanup();
            System.Diagnostics.Debug.WriteLine($"Render pass '{Name}' disposed.");
        }
    }
}
