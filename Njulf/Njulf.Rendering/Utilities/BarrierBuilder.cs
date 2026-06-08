using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Utilities
{
    /// <summary>
    /// Helper for creating Synchronization2 barriers.
    /// Simplifies barrier creation for layout transitions, queue family transfers, etc.
    /// </summary>
    public static class BarrierBuilder
    {
        private static readonly ImageSubresourceRange FullSubresourceRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit | ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            BaseMipLevel = 0,
            LevelCount = Vk.RemainingMipLevels,
            BaseArrayLayer = 0,
            LayerCount = Vk.RemainingArrayLayers
        };
        
        /// <summary>
        /// Creates an image memory barrier with Synchronization2.
        /// </summary>
        public static ImageMemoryBarrier2 CreateImageBarrier(
            Image image,
            PipelineStageFlags2 srcStageMask,
            AccessFlags2 srcAccessMask,
            PipelineStageFlags2 dstStageMask,
            AccessFlags2 dstAccessMask,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            uint srcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            uint dstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            ImageSubresourceRange? subresourceRange = null)
        {
            return new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStageMask,
                SrcAccessMask = srcAccessMask,
                DstStageMask = dstStageMask,
                DstAccessMask = dstAccessMask,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = srcQueueFamilyIndex,
                DstQueueFamilyIndex = dstQueueFamilyIndex,
                Image = image,
                SubresourceRange = subresourceRange ?? FullSubresourceRange
            };
        }
        
        /// <summary>
        /// Creates a buffer memory barrier with Synchronization2.
        /// </summary>
        public static BufferMemoryBarrier2 BufferBarrier(
            Buffer buffer,
            PipelineStageFlags2 srcStageMask,
            AccessFlags2 srcAccessMask,
            PipelineStageFlags2 dstStageMask,
            AccessFlags2 dstAccessMask,
            ulong offset = 0,
            ulong size = Vk.WholeSize,
            uint srcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            uint dstQueueFamilyIndex = Vk.QueueFamilyIgnored)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = srcStageMask,
                SrcAccessMask = srcAccessMask,
                DstStageMask = dstStageMask,
                DstAccessMask = dstAccessMask,
                SrcQueueFamilyIndex = srcQueueFamilyIndex,
                DstQueueFamilyIndex = dstQueueFamilyIndex,
                Buffer = buffer,
                Offset = offset,
                Size = size
            };
        }
        
        /// <summary>
        /// Creates a dependency info with barriers.
        /// </summary>
        public static DependencyInfo DependencyInfo(
            ImageMemoryBarrier2[]? imageBarriers = null,
            BufferMemoryBarrier2[]? bufferBarriers = null)
        {
            var info = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)(imageBarriers?.Length ?? 0),
                BufferMemoryBarrierCount = (uint)(bufferBarriers?.Length ?? 0)
            };
            
            return info;
        }
        
        /// <summary>
        /// Creates a transition barrier for an image layout change.
        /// </summary>
        public static DependencyInfo TransitionImage(
            CommandBuffer cmd,
            Image image,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            PipelineStageFlags2 dstStage,
            AccessFlags2 srcAccess = AccessFlags2.None,
            AccessFlags2 dstAccess = AccessFlags2.None,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = ImageBarrier(
                image,
                srcStage,
                srcAccess,
                dstStage,
                dstAccess,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            var depInfo = DependencyInfo(imageBarriers: new[] { barrier });
            
            // Note: This doesn't execute the barrier, just creates the info
            // Call CmdPipelineBarrier2 with this info
            return depInfo;
        }
        
        /// <summary>
        /// Executes a pipeline barrier with Synchronization2.
        /// </summary>
        public static unsafe void ExecuteBarrier(
            CommandBuffer cmd,
            DependencyInfo depInfo)
        {
            Vk vk = Vk.GetApi();
            vk.CmdPipelineBarrier2(cmd, &depInfo);
        }
        
        /// <summary>
        /// Executes a pipeline barrier with Synchronization2 from barrier arrays.
        /// </summary>
        public static unsafe void ExecuteBarrier(
            CommandBuffer cmd,
            ImageMemoryBarrier2[]? imageBarriers = null,
            BufferMemoryBarrier2[]? bufferBarriers = null)
        {
            Vk vk = Vk.GetApi();
            
            var depInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)(imageBarriers?.Length ?? 0),
                BufferMemoryBarrierCount = (uint)(bufferBarriers?.Length ?? 0)
            };
            
            if (imageBarriers != null && imageBarriers.Length > 0)
            {
                fixed (ImageMemoryBarrier2* pImageBarriers = imageBarriers)
                {
                    depInfo.PImageMemoryBarriers = pImageBarriers;
                    if (bufferBarriers != null && bufferBarriers.Length > 0)
                    {
                        fixed (BufferMemoryBarrier2* pBufferBarriers = bufferBarriers)
                        {
                            depInfo.PBufferMemoryBarriers = pBufferBarriers;
                            vk.CmdPipelineBarrier2(cmd, &depInfo);
                        }
                    }
                    else
                    {
                        vk.CmdPipelineBarrier2(cmd, &depInfo);
                    }
                }
            }
            else if (bufferBarriers != null && bufferBarriers.Length > 0)
            {
                fixed (BufferMemoryBarrier2* pBufferBarriers = bufferBarriers)
                {
                    depInfo.PBufferMemoryBarriers = pBufferBarriers;
                    vk.CmdPipelineBarrier2(cmd, &depInfo);
                }
            }
            else
            {
                vk.CmdPipelineBarrier2(cmd, &depInfo);
            }
        }
        
        /// <summary>
        /// Creates barriers for queue family ownership transfer.
        /// </summary>
        public static DependencyInfo TransferQueueFamilyOwnership(
            Image image,
            uint srcQueueFamily,
            uint dstQueueFamily,
            PipelineStageFlags2 srcStage,
            PipelineStageFlags2 dstStage,
            AccessFlags2 srcAccess,
            AccessFlags2 dstAccess,
            ImageLayout layout,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                srcStage,
                srcAccess,
                dstStage,
                dstAccess,
                layout,
                layout,
                srcQueueFamily,
                dstQueueFamily,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Creates barriers for release queue family ownership.
        /// </summary>
        public static DependencyInfo ReleaseQueueFamilyOwnership(
            Image image,
            uint queueFamily,
            PipelineStageFlags2 stage,
            AccessFlags2 access,
            ImageLayout layout,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                stage,
                access,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                layout,
                layout,
                queueFamily,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Creates barriers for acquire queue family ownership.
        /// </summary>
        public static DependencyInfo AcquireQueueFamilyOwnership(
            Image image,
            uint queueFamily,
            PipelineStageFlags2 stage,
            AccessFlags2 access,
            ImageLayout layout,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.None,
                AccessFlags2.None,
                stage,
                access,
                layout,
                layout,
                Vk.QueueFamilyIgnored,
                queueFamily,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        // ============================================
        // COMMON BARRIER PATTERNS
        // ============================================
        
        /// <summary>
        /// Transitions image from Undefined to TransferDstOptimal.
        /// </summary>
        public static DependencyInfo UndefinedToTransferDst(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.TopOfPipeBit,
                AccessFlags2.None,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Transitions image from TransferDstOptimal to ShaderReadOnlyOptimal.
        /// </summary>
        public static DependencyInfo TransferDstToShaderRead(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderReadBit,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Transitions image from Undefined to DepthStencilAttachmentOptimal.
        /// </summary>
        public static DependencyInfo UndefinedToDepthStencil(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.TopOfPipeBit,
                AccessFlags2.None,
                PipelineStageFlags2.EarlyFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                ImageLayout.Undefined,
                ImageLayout.DepthStencilAttachmentOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Transitions image from DepthStencilAttachmentOptimal to DepthStencilReadOnlyOptimal.
        /// </summary>
        public static DependencyInfo DepthStencilToReadOnly(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.EarlyFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.DepthStencilAttachmentOptimal,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Transitions image from PresentSrc to TransferSrc (for screenshot).
        /// </summary>
        public static DependencyInfo PresentToTransferSrc(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.BottomOfPipeBit,
                AccessFlags2.None,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                ImageLayout.PresentSrcKhr,
                ImageLayout.TransferSrcOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
        
        /// <summary>
        /// Transitions image from TransferSrc to PresentSrc.
        /// </summary>
        public static DependencyInfo TransferSrcToPresent(
            Image image,
            ImageSubresourceRange? subresourceRange = null)
        {
            var barrier = CreateImageBarrier(
                image,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.BottomOfPipeBit,
                AccessFlags2.None,
                ImageLayout.TransferSrcOptimal,
                ImageLayout.PresentSrcKhr,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                subresourceRange);
            
            return DependencyInfo(imageBarriers: new[] { barrier });
        }
    }
}
