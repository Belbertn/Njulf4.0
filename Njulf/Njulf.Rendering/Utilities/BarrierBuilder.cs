using System;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Utilities
{
    /// <summary>
    /// Helper for creating and immediately executing synchronization2 barriers.
    /// Barrier arrays must be pinned at the callsite; this type intentionally does not
    /// return DependencyInfo values backed by temporary managed arrays.
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

        public static unsafe void ExecuteBarrier(CommandBuffer cmd, DependencyInfo depInfo)
        {
            ValidateDependencyInfo(depInfo);
            Vk.GetApi().CmdPipelineBarrier2(cmd, &depInfo);
        }

        public static unsafe void ExecuteImageBarrier(CommandBuffer cmd, ImageMemoryBarrier2 imageBarrier)
        {
            var depInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &imageBarrier
            };

            ValidateDependencyInfo(depInfo);
            Vk.GetApi().CmdPipelineBarrier2(cmd, &depInfo);
        }

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

            if (imageBarriers is { Length: > 0 })
            {
                fixed (ImageMemoryBarrier2* pImageBarriers = imageBarriers)
                {
                    depInfo.PImageMemoryBarriers = pImageBarriers;
                    ExecuteWithOptionalBufferBarriers(vk, cmd, &depInfo, bufferBarriers);
                }
            }
            else
            {
                ExecuteWithOptionalBufferBarriers(vk, cmd, &depInfo, bufferBarriers);
            }
        }

        public static unsafe void ExecuteBarrier(
            CommandBuffer cmd,
            ReadOnlySpan<ImageMemoryBarrier2> imageBarriers,
            ReadOnlySpan<BufferMemoryBarrier2> bufferBarriers)
        {
            if (imageBarriers.IsEmpty && bufferBarriers.IsEmpty)
                return;

            Vk vk = Vk.GetApi();
            var depInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)imageBarriers.Length,
                BufferMemoryBarrierCount = (uint)bufferBarriers.Length
            };

            fixed (ImageMemoryBarrier2* pImageBarriers = imageBarriers)
            fixed (BufferMemoryBarrier2* pBufferBarriers = bufferBarriers)
            {
                depInfo.PImageMemoryBarriers = imageBarriers.IsEmpty ? null : pImageBarriers;
                depInfo.PBufferMemoryBarriers = bufferBarriers.IsEmpty ? null : pBufferBarriers;
                ValidateDependencyInfo(depInfo);
                vk.CmdPipelineBarrier2(cmd, &depInfo);
            }
        }

        private static unsafe void ExecuteWithOptionalBufferBarriers(
            Vk vk,
            CommandBuffer cmd,
            DependencyInfo* depInfo,
            BufferMemoryBarrier2[]? bufferBarriers)
        {
            if (bufferBarriers is { Length: > 0 })
            {
                fixed (BufferMemoryBarrier2* pBufferBarriers = bufferBarriers)
                {
                    depInfo->PBufferMemoryBarriers = pBufferBarriers;
                    ValidateDependencyInfo(*depInfo);
                    vk.CmdPipelineBarrier2(cmd, depInfo);
                }
            }
            else
            {
                ValidateDependencyInfo(*depInfo);
                vk.CmdPipelineBarrier2(cmd, depInfo);
            }
        }

        private static unsafe void ValidateDependencyInfo(DependencyInfo depInfo)
        {
            if (depInfo.ImageMemoryBarrierCount > 0 && depInfo.PImageMemoryBarriers == null)
                throw new InvalidOperationException("DependencyInfo has image barrier count but no image barrier pointer.");
            if (depInfo.BufferMemoryBarrierCount > 0 && depInfo.PBufferMemoryBarriers == null)
                throw new InvalidOperationException("DependencyInfo has buffer barrier count but no buffer barrier pointer.");
            if (depInfo.MemoryBarrierCount > 0 && depInfo.PMemoryBarriers == null)
                throw new InvalidOperationException("DependencyInfo has memory barrier count but no memory barrier pointer.");
        }
    }
}
