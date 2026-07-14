using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public readonly record struct QueueOwnershipTransferBarrierCounts(
        int ReleaseCount,
        int AcquireCount,
        int OwnershipTransferCount,
        ulong BufferBytes,
        int ImageSubresources)
    {
        public static QueueOwnershipTransferBarrierCounts Empty => new(0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Emits the two sides of compiled cross-queue handoffs using synchronization2. Releases and
    /// acquires are deliberately recorded in different command buffers/submissions; recording both
    /// sides in a single command buffer is not a queue-family ownership transfer. For same-family
    /// or concurrently shared images, the release is a memory-only dependency and the acquire
    /// performs the layout transition. Repeating an ordinary image layout transition in both
    /// submissions would make the acquire's old layout invalid.
    /// </summary>
    public static class QueueOwnershipTransferRecorder
    {
        public static unsafe QueueOwnershipTransferBarrierCounts RecordReleases(
            VulkanContext context,
            CommandBuffer commandBuffer,
            IReadOnlyList<QueueOwnershipTransfer> transfers)
        {
            return Record(context, commandBuffer, transfers, release: true);
        }

        public static unsafe QueueOwnershipTransferBarrierCounts RecordAcquires(
            VulkanContext context,
            CommandBuffer commandBuffer,
            IReadOnlyList<QueueOwnershipTransfer> transfers)
        {
            return Record(context, commandBuffer, transfers, release: false);
        }

        public static void ValidatePair(QueueOwnershipTransfer transfer)
        {
            if (transfer == null)
                throw new ArgumentNullException(nameof(transfer));
            if (transfer.SourceQueue == transfer.DestinationQueue)
                throw new InvalidOperationException($"Transfer {transfer.Id} does not cross queues.");
            if (transfer.Binding.Kind == RenderGraphConcreteResourceKind.Image &&
                (transfer.OldLayout == ImageLayout.Undefined || transfer.NewLayout == ImageLayout.Undefined))
            {
                throw new InvalidOperationException($"Image transfer {transfer.Id} requires matching old and new layouts.");
            }
            if (transfer.RequiresQueueFamilyOwnershipTransfer &&
                (transfer.IsConcurrentResource || transfer.SourceQueueFamily == transfer.DestinationQueueFamily))
            {
                throw new InvalidOperationException($"Transfer {transfer.Id} has invalid exclusive ownership metadata.");
            }
        }

        private static unsafe QueueOwnershipTransferBarrierCounts Record(
            VulkanContext context,
            CommandBuffer commandBuffer,
            IReadOnlyList<QueueOwnershipTransfer> transfers,
            bool release)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
            if (transfers == null)
                throw new ArgumentNullException(nameof(transfers));
            if (transfers.Count == 0)
                return QueueOwnershipTransferBarrierCounts.Empty;

            var imageBarriers = new List<ImageMemoryBarrier2>();
            var bufferBarriers = new List<BufferMemoryBarrier2>();
            int ownershipCount = 0;
            ulong bytes = 0;
            int subresources = 0;
            foreach (QueueOwnershipTransfer transfer in transfers)
            {
                ValidatePair(transfer);
                uint sourceFamily = transfer.RequiresQueueFamilyOwnershipTransfer
                    ? transfer.SourceQueueFamily
                    : Vk.QueueFamilyIgnored;
                uint destinationFamily = transfer.RequiresQueueFamilyOwnershipTransfer
                    ? transfer.DestinationQueueFamily
                    : Vk.QueueFamilyIgnored;
                if (transfer.RequiresQueueFamilyOwnershipTransfer)
                    ownershipCount++;

                if (transfer.IsImage)
                {
                    // Queue-family ownership transfer barriers are a matched pair and therefore
                    // carry the same layout transition on both sides. With QueueFamilyIgnored,
                    // however, these are ordinary barriers: keep the source layout on the release
                    // and transition once on the destination acquire.
                    ImageLayout oldLayout = release ? transfer.ReleaseOldLayout : transfer.AcquireOldLayout;
                    ImageLayout newLayout = release ? transfer.ReleaseNewLayout : transfer.AcquireNewLayout;
                    imageBarriers.Add(new ImageMemoryBarrier2
                    {
                        SType = StructureType.ImageMemoryBarrier2,
                        SrcStageMask = release ? transfer.SourceStageMask : PipelineStageFlags2.None,
                        SrcAccessMask = release ? transfer.SourceAccessMask : AccessFlags2.None,
                        DstStageMask = release ? PipelineStageFlags2.None : transfer.DestinationStageMask,
                        DstAccessMask = release ? AccessFlags2.None : transfer.DestinationAccessMask,
                        OldLayout = oldLayout,
                        NewLayout = newLayout,
                        SrcQueueFamilyIndex = sourceFamily,
                        DstQueueFamilyIndex = destinationFamily,
                        Image = transfer.Binding.Image,
                        SubresourceRange = transfer.Binding.SubresourceRange
                    });
                    subresources += transfer.TransferImageSubresources;
                }
                else
                {
                    bufferBarriers.Add(BarrierBuilder.BufferBarrier(
                        transfer.Binding.Buffer,
                        release ? transfer.SourceStageMask : PipelineStageFlags2.None,
                        release ? transfer.SourceAccessMask : AccessFlags2.None,
                        release ? PipelineStageFlags2.None : transfer.DestinationStageMask,
                        release ? AccessFlags2.None : transfer.DestinationAccessMask,
                        transfer.Binding.ByteOffset,
                        transfer.Binding.ByteSize,
                        sourceFamily,
                        destinationFamily));
                    bytes = checked(bytes + transfer.Binding.ByteSize);
                }
            }

            BarrierBuilder.ExecuteBarrier(commandBuffer, imageBarriers.ToArray(), bufferBarriers.ToArray());
            if (!release)
            {
                foreach (QueueOwnershipTransfer transfer in transfers)
                {
                    if (!transfer.IsImage)
                        continue;

                    foreach (RenderGraphConcreteResourceBinding binding in transfer.AllBindings)
                        binding.LayoutTracker?.Invoke(transfer.NewLayout);
                }
            }

            return release
                ? new QueueOwnershipTransferBarrierCounts(transfers.Count, 0, ownershipCount, bytes, subresources)
                : new QueueOwnershipTransferBarrierCounts(0, transfers.Count, ownershipCount, bytes, subresources);
        }
    }
}
