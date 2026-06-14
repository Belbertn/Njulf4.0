using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class RenderGraphImageAllocator : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly GpuAllocationTracker? _allocationTracker;
        private readonly Dictionary<RenderGraphResourceHandle, RenderGraphAllocatedImage> _images = new();
        private bool _disposed;

        public RenderGraphImageAllocator(VulkanContext context, GpuAllocationTracker? allocationTracker = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _allocationTracker = allocationTracker;
        }

        public IReadOnlyDictionary<RenderGraphResourceHandle, RenderGraphAllocatedImage> Images => _images;
        public ulong AllocatedBytes { get; private set; }

        public void Recreate(RenderGraphImageAllocationPlan plan)
        {
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            Clear();
            foreach (RenderGraphImageAllocationRequest request in plan.Images)
            {
                if (!request.ShouldAllocate)
                    continue;

                RenderGraphAllocatedImage image = Allocate(request);
                _images.Add(request.Handle, image);
                AllocatedBytes = checked(AllocatedBytes + image.EstimatedBytes);
            }
        }

        public bool TryGetImage(RenderGraphResourceHandle handle, out RenderGraphAllocatedImage image)
        {
            return _images.TryGetValue(handle, out image);
        }

        private RenderGraphAllocatedImage Allocate(RenderGraphImageAllocationRequest request)
        {
            RenderGraphImageDesc desc = request.Descriptor;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = desc.Format,
                Extent = new Extent3D { Width = desc.Width, Height = desc.Height, Depth = 1 },
                MipLevels = desc.MipCount,
                ArrayLayers = desc.ArrayLayers,
                Samples = desc.Samples,
                Tiling = ImageTiling.Optimal,
                Usage = request.Usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            ImageCompressionControlEXT compressionControl = default;
            if (desc.AllowDriverCompression &&
                _context.ImageCompressionControlEnabled &&
                IsColorImage(desc.Format) &&
                (request.Usage & ImageUsageFlags.ColorAttachmentBit) != 0)
            {
                compressionControl = new ImageCompressionControlEXT
                {
                    SType = StructureType.ImageCompressionControlExt,
                    Flags = ImageCompressionFlagsEXT.DefaultExt
                };
                imageInfo.PNext = &compressionControl;
            }

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
                throw new VulkanException($"Failed to allocate graph image '{desc.Name}'", result);

            try
            {
                _context.SetDebugName(image.Handle, ObjectType.Image, $"RG {desc.Name} {desc.Width}x{desc.Height} {desc.Format}");
                ImageView view = CreateImageView(image, desc);
                _context.SetDebugName(view.Handle, ObjectType.ImageView, $"RG {desc.Name} View");
                _allocationTracker?.RegisterImage(
                    image.Handle,
                    request.EstimatedBytes,
                    desc.Format,
                    new Extent3D { Width = desc.Width, Height = desc.Height, Depth = 1 },
                    desc.MipCount,
                    desc.ArrayLayers,
                    MemoryBudgetCategory.RenderTargets,
                    $"Render graph {request.Category}: {desc.Name}");

                return new RenderGraphAllocatedImage(
                    request.Handle,
                    desc.Name,
                    image,
                    view,
                    allocation,
                    desc.Format,
                    new Extent2D { Width = desc.Width, Height = desc.Height },
                    desc.MipCount,
                    desc.ArrayLayers,
                    request.Usage,
                    request.Category,
                    request.EstimatedBytes);
            }
            catch
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, image, allocation);
                throw;
            }
        }

        private ImageView CreateImageView(Image image, RenderGraphImageDesc desc)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = desc.ArrayLayers == 6 ? ImageViewType.TypeCube : ImageViewType.Type2D,
                Format = desc.Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = GetAspectMask(desc.Format),
                    BaseMipLevel = 0,
                    LevelCount = desc.MipCount,
                    BaseArrayLayer = 0,
                    LayerCount = desc.ArrayLayers
                }
            };

            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create graph image view '{desc.Name}'", result);

            return view;
        }

        public void Clear()
        {
            foreach (RenderGraphAllocatedImage image in _images.Values)
            {
                if (image.View.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, image.View, null);
                if (image.Image.Handle != 0)
                {
                    _allocationTracker?.RetireImage(image.Image.Handle);
                    GpuAllocator.Apis.DestroyImage(_context.Allocator, image.Image, image.Allocation);
                }
            }

            _images.Clear();
            AllocatedBytes = 0;
        }

        private static ImageAspectFlags GetAspectMask(Format format)
        {
            return format switch
            {
                Format.D16Unorm or Format.D32Sfloat => ImageAspectFlags.DepthBit,
                Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                _ => ImageAspectFlags.ColorBit
            };
        }

        private static bool IsColorImage(Format format)
        {
            return (GetAspectMask(format) & ImageAspectFlags.ColorBit) != 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Clear();
            GC.SuppressFinalize(this);
        }
    }

    public readonly unsafe struct RenderGraphAllocatedImage
    {
        public RenderGraphAllocatedImage(
            RenderGraphResourceHandle handle,
            string name,
            Image image,
            ImageView view,
            GpuAllocator.Allocation* allocation,
            Format format,
            Extent2D extent,
            uint mipCount,
            uint arrayLayers,
            ImageUsageFlags usage,
            RenderGraphImageAllocationCategory category,
            ulong estimatedBytes)
        {
            Handle = handle;
            Name = name;
            Image = image;
            View = view;
            Allocation = allocation;
            Format = format;
            Extent = extent;
            MipCount = mipCount;
            ArrayLayers = arrayLayers;
            Usage = usage;
            Category = category;
            EstimatedBytes = estimatedBytes;
        }

        public RenderGraphResourceHandle Handle { get; }
        public string Name { get; }
        public Image Image { get; }
        public ImageView View { get; }
        public GpuAllocator.Allocation* Allocation { get; }
        public Format Format { get; }
        public Extent2D Extent { get; }
        public uint MipCount { get; }
        public uint ArrayLayers { get; }
        public ImageUsageFlags Usage { get; }
        public RenderGraphImageAllocationCategory Category { get; }
        public ulong EstimatedBytes { get; }
    }
}
