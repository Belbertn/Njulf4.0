using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Njulf.Rendering.Data;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;

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

        public virtual bool ShouldExecute(int frameIndex, Data.SceneRenderingData sceneData)
        {
            return true;
        }
        
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

        public virtual void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));
        }
        
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

        protected void TransitionGraphTarget(
            CommandBuffer cmd,
            RenderTarget target,
            ImageLayout newLayout,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            TransitionGraphImage(
                cmd,
                target.Image,
                target.Format,
                target.Layout,
                newLayout,
                ResolveSourceStage(target.Layout),
                ResolveSourceAccess(target.Layout),
                dstStage,
                dstAccess,
                0,
                1,
                0,
                1);
            target.SetKnownLayout(newLayout);
        }

        protected void TransitionGraphImage(
            CommandBuffer cmd,
            Image image,
            Format format,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            uint baseMipLevel,
            uint levelCount,
            uint baseArrayLayer,
            uint layerCount)
        {
            if (oldLayout == newLayout && srcAccess == dstAccess)
                return;

            var range = new ImageSubresourceRange
            {
                AspectMask = ResolveAspectMask(format),
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = layerCount
            };

            var barrier = BarrierBuilder.CreateImageBarrier(
                image,
                srcStage,
                srcAccess,
                dstStage,
                dstAccess,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private static PipelineStageFlags2 ResolveSourceStage(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags2.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal => PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                ImageLayout.DepthStencilReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                ImageLayout.General => PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags2.TransferBit,
                _ => PipelineStageFlags2.AllCommandsBit
            };
        }

        private static AccessFlags2 ResolveSourceAccess(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => AccessFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit,
                ImageLayout.ColorAttachmentOptimal => AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
                ImageLayout.DepthStencilAttachmentOptimal => AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.General => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit,
                _ => AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
            };
        }

        private static ImageAspectFlags ResolveAspectMask(Format format)
        {
            return format switch
            {
                Format.D16Unorm or Format.D32Sfloat => ImageAspectFlags.DepthBit,
                Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.ColorBit
            };
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
