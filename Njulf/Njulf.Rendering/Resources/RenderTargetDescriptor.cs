using System;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public readonly struct RenderTargetDescriptor
    {
        public RenderTargetDescriptor(
            bool colorAttachment,
            bool sampled,
            bool depthAttachment = false,
            bool storage = false,
            bool transferSource = false,
            bool transferDestination = false,
            bool allowDriverCompression = false)
        {
            ColorAttachment = colorAttachment;
            Sampled = sampled;
            DepthAttachment = depthAttachment;
            Storage = storage;
            TransferSource = transferSource;
            TransferDestination = transferDestination;
            AllowDriverCompression = allowDriverCompression;

            if (Usage == ImageUsageFlags.None)
                throw new ArgumentException("Render target usage cannot be empty.");
        }

        public bool ColorAttachment { get; }
        public bool Sampled { get; }
        public bool DepthAttachment { get; }
        public bool Storage { get; }
        public bool TransferSource { get; }
        public bool TransferDestination { get; }
        public bool AllowDriverCompression { get; }

        public ImageUsageFlags Usage
        {
            get
            {
                ImageUsageFlags usage = ImageUsageFlags.None;
                if (ColorAttachment)
                    usage |= ImageUsageFlags.ColorAttachmentBit;
                if (DepthAttachment)
                    usage |= ImageUsageFlags.DepthStencilAttachmentBit;
                if (Sampled)
                    usage |= ImageUsageFlags.SampledBit;
                if (Storage)
                    usage |= ImageUsageFlags.StorageBit;
                if (TransferSource)
                    usage |= ImageUsageFlags.TransferSrcBit;
                if (TransferDestination)
                    usage |= ImageUsageFlags.TransferDstBit;
                return usage;
            }
        }
    }
}
