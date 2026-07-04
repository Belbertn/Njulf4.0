using System;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public readonly struct VolumeTextureDescriptor
    {
        public VolumeTextureDescriptor(
            bool sampled,
            bool storage = false,
            bool transferSource = false,
            bool transferDestination = false,
            bool generateFullMipChain = false)
        {
            Sampled = sampled;
            Storage = storage;
            TransferSource = transferSource;
            TransferDestination = transferDestination;
            GenerateFullMipChain = generateFullMipChain;

            if (Usage == ImageUsageFlags.None)
                throw new ArgumentException("Volume texture usage cannot be empty.");
        }

        public bool Sampled { get; }
        public bool Storage { get; }
        public bool TransferSource { get; }
        public bool TransferDestination { get; }
        public bool GenerateFullMipChain { get; }

        public ImageUsageFlags Usage
        {
            get
            {
                ImageUsageFlags usage = ImageUsageFlags.None;
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
