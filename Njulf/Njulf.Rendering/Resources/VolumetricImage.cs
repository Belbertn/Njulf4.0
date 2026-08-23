using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Renderer-owned layered image used by camera-relative volume passes. The
/// image remains a logical 3D grid while using 2D-array layers so every Vulkan
/// backend has identical addressing and explicit Z interpolation.
/// </summary>
public sealed unsafe class VolumetricImage : IDisposable, IRenderGraphLayoutTrackedImage
{
    private readonly VulkanContext _context;
    private GpuAllocator.Allocation* _allocation;
    private ImageView[] _mipViews = Array.Empty<ImageView>();
    private bool _disposed;

    public VolumetricImage(
        VulkanContext context,
        string name,
        Format format,
        Extent3D extent,
        uint mipLevels = 1,
        bool true3D = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A volume image name is required.", nameof(name))
            : name;
        if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(extent));
        if (mipLevels == 0)
            throw new ArgumentOutOfRangeException(nameof(mipLevels));

        Format = format;
        Extent = extent;
        MipLevels = mipLevels;
        IsTrue3D = true3D;
        Create();
    }

    public string Name { get; }
    public Format Format { get; }
    public Extent3D Extent { get; }
    public uint MipLevels { get; }
    public bool IsTrue3D { get; }
    public Image Image { get; private set; }
    public ImageView FullView { get; private set; }
    public IReadOnlyList<ImageView> MipViews => _mipViews;
    public ImageLayout Layout { get; private set; } = ImageLayout.Undefined;
    public ulong AllocationByteSize { get; private set; }
    public ulong AllocationGeneration => Image.Handle;

    public ImageSubresourceRange SubresourceRange => new()
    {
        AspectMask = ImageAspectFlags.ColorBit,
        BaseMipLevel = 0,
        LevelCount = MipLevels,
        BaseArrayLayer = 0,
        LayerCount = IsTrue3D ? 1u : Extent.Depth
    };

    public void SetTrackedLayout(ImageLayout layout) => Layout = layout;

    public void TransitionToLayout(
        CommandBuffer cmd,
        ImageLayout newLayout,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess,
        PipelineStageFlags2? srcStage = null,
        AccessFlags2? srcAccess = null,
        bool force = false)
    {
        if (Layout == newLayout && !force)
            return;

        var barrier = BarrierBuilder.CreateImageBarrier(
            Image,
            srcStage ?? RenderTarget.GetSourceStageForLayout(Layout),
            srcAccess ?? RenderTarget.GetSourceAccessForLayout(Layout),
            dstStage,
            dstAccess,
            Layout,
            newLayout,
            Vk.QueueFamilyIgnored,
            Vk.QueueFamilyIgnored,
            SubresourceRange);
        BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        Layout = newLayout;
    }

    private void Create()
    {
        ValidateFormatSupport();
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = IsTrue3D ? ImageType.Type3D : ImageType.Type2D,
            Format = Format,
            Extent = new Extent3D
            {
                Width = Extent.Width,
                Height = Extent.Height,
                Depth = IsTrue3D ? Extent.Depth : 1u
            },
            MipLevels = MipLevels,
            ArrayLayers = IsTrue3D ? 1u : Extent.Depth,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit |
                ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
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
            throw new VulkanException($"Failed to create volumetric image '{Name}'.", result);

        Image = image;
        _allocation = allocation;
        AllocationByteSize = checked((ulong)allocationInfo.Size);
        try
        {
            _context.SetDebugName(
                Image.Handle,
                ObjectType.Image,
                $"{Name} {Extent.Width}x{Extent.Height}x{Extent.Depth} {Format}");
            FullView = CreateView(0, MipLevels);
            _context.SetDebugName(FullView.Handle, ObjectType.ImageView, $"{Name} Full View");
            _mipViews = new ImageView[MipLevels];
            for (uint mip = 0; mip < MipLevels; mip++)
            {
                _mipViews[mip] = CreateView(mip, 1);
                _context.SetDebugName(
                    _mipViews[mip].Handle,
                    ObjectType.ImageView,
                    $"{Name} Mip {mip} View");
            }
        }
        catch
        {
            Destroy();
            throw;
        }
    }

    private ImageView CreateView(uint baseMip, uint levelCount)
    {
        var createInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = IsTrue3D ? ImageViewType.Type3D : ImageViewType.Type2DArray,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMip,
                LevelCount = levelCount,
                BaseArrayLayer = 0,
                LayerCount = IsTrue3D ? 1u : Extent.Depth
            }
        };
        Result result = _context.Api.CreateImageView(
            _context.Device,
            &createInfo,
            null,
            out ImageView view);
        if (result != Result.Success)
            throw new VulkanException($"Failed to create volumetric image view '{Name}'.", result);
        return view;
    }

    private void ValidateFormatSupport()
    {
        FormatProperties properties;
        _context.Api.GetPhysicalDeviceFormatProperties(
            _context.PhysicalDevice,
            Format,
            &properties);
        const FormatFeatureFlags required =
            FormatFeatureFlags.SampledImageBit |
            FormatFeatureFlags.StorageImageBit;
        if ((properties.OptimalTilingFeatures & required) != required)
        {
            throw new VulkanException(
                $"Format {Format} does not support sampled storage images for '{Name}'.");
        }
    }

    private void Destroy()
    {
        foreach (ImageView view in _mipViews)
            if (view.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, view, null);
        _mipViews = Array.Empty<ImageView>();

        if (FullView.Handle != 0)
        {
            _context.Api.DestroyImageView(_context.Device, FullView, null);
            FullView = default;
        }
        if (_allocation != null)
        {
            GpuAllocator.Apis.DestroyImage(_context.Allocator, Image, _allocation);
            _allocation = null;
            Image = default;
        }
        AllocationByteSize = 0;
        Layout = ImageLayout.Undefined;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Destroy();
        GC.SuppressFinalize(this);
    }
}
