using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class HiZDepthPyramid : IDisposable
    {
        private readonly VulkanContext _context;
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView _fullView;
        private ImageView[] _mipViews = Array.Empty<ImageView>();
        private bool _disposed;

        public HiZDepthPyramid(VulkanContext context, Extent2D extent)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Recreate(extent);
        }

        public Image Image => _image;
        public ImageView FullView => _fullView;
        public IReadOnlyList<ImageView> MipViews => _mipViews;
        public Extent2D Extent { get; private set; }
        public uint MipLevels { get; private set; }
        public Format Format => Format.R32Sfloat;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;

        public void Recreate(Extent2D extent)
        {
            if (extent.Width == 0 || extent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(extent), "Hi-Z extent must be non-zero.");

            DestroyResources();

            Extent = extent;
            MipLevels = CalculateMipLevels(extent.Width, extent.Height);
            ValidateFormatSupport();

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format,
                Extent = new Extent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                MipLevels = MipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            var allocInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice
            };

            Image image;
            GpuAllocator.Allocation* allocation;
            GpuAllocator.AllocationInfo allocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator,
                &imageInfo,
                &allocInfo,
                &image,
                &allocation,
                &allocationInfo);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z depth pyramid image", result);

            _image = image;
            _allocation = allocation;
            _context.SetDebugName(_image.Handle, ObjectType.Image, $"Hi-Z Depth Pyramid {extent.Width}x{extent.Height} Mips {MipLevels}");
            _fullView = CreateImageView(0, MipLevels);
            _context.SetDebugName(_fullView.Handle, ObjectType.ImageView, "Hi-Z Depth Pyramid Full View");
            _mipViews = new ImageView[MipLevels];
            for (uint mip = 0; mip < MipLevels; mip++)
            {
                _mipViews[mip] = CreateImageView(mip, 1);
                _context.SetDebugName(_mipViews[mip].Handle, ObjectType.ImageView, $"Hi-Z Depth Pyramid Mip {mip} View");
            }

            Layout = ImageLayout.Undefined;
        }

        public Extent2D GetMipExtent(uint mipLevel)
        {
            if (mipLevel >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mipLevel));

            uint width = Math.Max(1u, Extent.Width >> (int)mipLevel);
            uint height = Math.Max(1u, Extent.Height >> (int)mipLevel);
            return new Extent2D { Width = width, Height = height };
        }

        private ImageView CreateImageView(uint baseMipLevel, uint levelCount)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = baseMipLevel,
                    LevelCount = levelCount,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            Result result = _context.Api.CreateImageView(
                _context.Device,
                &viewInfo,
                null,
                out ImageView view);

            if (result != Result.Success)
                throw new VulkanException("Failed to create Hi-Z image view", result);

            return view;
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            const FormatFeatureFlags required =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit;

            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support sampled storage images for Hi-Z.");
        }

        private static uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint dimension = Math.Max(width, height);
            while (dimension > 1)
            {
                dimension >>= 1;
                levels++;
            }

            return levels;
        }

        private void DestroyResources()
        {
            foreach (ImageView mipView in _mipViews)
            {
                if (mipView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, mipView, null);
            }

            _mipViews = Array.Empty<ImageView>();

            if (_fullView.Handle != 0)
            {
                _context.Api.DestroyImageView(_context.Device, _fullView, null);
                _fullView = default;
            }

            if (_allocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
            }

            Layout = ImageLayout.Undefined;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyResources();
            GC.SuppressFinalize(this);
        }
    }
}
