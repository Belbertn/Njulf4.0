using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class PointShadowCubemapArray : IDisposable
    {
        private const int MaxPointShadowRecords = 4;
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView _sampledView;
        private ImageView[] _faceViews = [];
        private Sampler _sampler;
        private BufferHandle _shadowDataBuffer;
        private bool _disposed;

        public PointShadowCubemapArray(VulkanContext context, BufferManager bufferManager, ShadowSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Format = Format.D32Sfloat;
            CreateSampler();
            _shadowDataBuffer = _bufferManager.CreateDeviceBuffer(
                (ulong)(MaxPointShadowRecords * Marshal.SizeOf<GPUPointShadow>()),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.ShadowMaps,
                "Point Shadow Data Buffer");
            _context.SetDebugName(_bufferManager.GetBuffer(_shadowDataBuffer).Handle, ObjectType.Buffer, "Point Shadow Data Buffer");
            Ensure(settings);
        }

        public uint MapSize { get; private set; }
        public int PointCapacity { get; private set; }
        public int LayerCount => PointCapacity * 6;
        public Format Format { get; }
        public Image Image => _image;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;
        public ulong EstimatedImageBytes => _image.Handle == 0
            ? 0
            : ImageByteEstimator.EstimateBytes(
                Format,
                new Extent3D { Width = MapSize, Height = MapSize, Depth = 1 },
                mipLevels: 1,
                arrayLayers: (uint)PointCapacity * 6);
        public ulong EstimatedBytes => EstimatedImageBytes + (ulong)(MaxPointShadowRecords * Marshal.SizeOf<GPUPointShadow>());

        public ImageView GetFaceView(int pointIndex, int faceIndex)
        {
            if (pointIndex < 0 || pointIndex >= PointCapacity)
                throw new ArgumentOutOfRangeException(nameof(pointIndex));
            if (faceIndex < 0 || faceIndex >= 6)
                throw new ArgumentOutOfRangeException(nameof(faceIndex));
            return _faceViews[pointIndex * 6 + faceIndex];
        }

        public bool Ensure(ShadowSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            bool shouldAllocateImage = settings.PointShadowsEnabled && settings.MaxShadowedPointLights > 0;
            if (!shouldAllocateImage)
            {
                if (_image.Handle == 0)
                    return false;

                WaitForOutstandingImageUse();
                DestroyImageResources();
                MapSize = settings.PointShadowMapSize;
                PointCapacity = 0;
                return true;
            }

            if (_image.Handle != 0 && MapSize == settings.PointShadowMapSize && PointCapacity == settings.MaxShadowedPointLights)
                return false;

            Recreate(settings.PointShadowMapSize, settings.MaxShadowedPointLights);
            return true;
        }

        public void Register(
            BindlessHeap bindlessHeap,
            ImageView fallbackDepthView = default,
            ImageLayout fallbackDepthLayout = ImageLayout.DepthStencilReadOnlyOptimal)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            VkBuffer buffer = _bufferManager.GetBuffer(_shadowDataBuffer);
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.PointShadowDataBuffer, buffer, 0, Vk.WholeSize);
            if (_sampledView.Handle != 0)
                bindlessHeap.RegisterTexture(BindlessIndex.PointShadowCubemapArrayTexture, _sampledView, _sampler, ImageLayout.DepthStencilReadOnlyOptimal);
            else if (fallbackDepthView.Handle != 0)
                bindlessHeap.RegisterTexture(BindlessIndex.PointShadowCubemapArrayTexture, fallbackDepthView, _sampler, fallbackDepthLayout);
        }

        public void Upload(StagingRing stagingRing, CommandBuffer commandBuffer, ReadOnlySpan<GPUPointShadow> pointShadows)
        {
            if (pointShadows.Length > MaxPointShadowRecords)
                throw new InvalidOperationException($"Point shadow upload has {pointShadows.Length} records, but capacity is {MaxPointShadowRecords}.");

            GpuBufferUploader.UploadPaddedSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _shadowDataBuffer,
                pointShadows,
                MaxPointShadowRecords,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        public void Recreate(uint mapSize, int pointCapacity)
        {
            if (mapSize < 128 || mapSize > 2048 || (mapSize & (mapSize - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(mapSize));
            if (pointCapacity < 0 || pointCapacity > MaxPointShadowRecords)
                throw new ArgumentOutOfRangeException(nameof(pointCapacity));

            WaitForOutstandingImageUse();
            DestroyImageResources();
            ValidateFormatSupport();
            MapSize = mapSize;
            PointCapacity = pointCapacity;
            uint layerCount = (uint)(pointCapacity * 6);
            if (layerCount == 0)
            {
                MapSize = mapSize;
                PointCapacity = 0;
                return;
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = Format,
                Extent = new Extent3D { Width = mapSize, Height = mapSize, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = layerCount,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            var allocInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice,
                Flags = _context.MemoryBudgetExtensionEnabled
                    ? GpuAllocator.AllocationCreateFlags.WithinBudgetBit
                    : default
            };
            Image image;
            GpuAllocator.Allocation* allocation;
            GpuAllocator.AllocationInfo allocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(_context.Allocator, &imageInfo, &allocInfo, &image, &allocation, &allocationInfo);
            if (result != Result.Success)
            {
                if (_context.IsMemoryBudgetExceeded(result))
                {
                    MapSize = mapSize;
                    PointCapacity = 0;
                    Layout = ImageLayout.Undefined;
                    System.Diagnostics.Debug.WriteLine("Point shadow cubemap array allocation skipped because the GPU memory budget is exhausted.");
                    return;
                }

                throw new VulkanException("Failed to create point shadow cubemap array", result);
            }

            _image = image;
            _allocation = allocation;
            _context.SetDebugName(_image.Handle, ObjectType.Image, "Point Shadow Cubemap Array");
            _sampledView = CreateView(ImageViewType.Type2DArray, 0, layerCount);
            _context.SetDebugName(_sampledView.Handle, ObjectType.ImageView, "Point Shadow 2D Array View");

            _faceViews = new ImageView[layerCount];
            for (uint i = 0; i < layerCount; i++)
            {
                _faceViews[i] = CreateView(ImageViewType.Type2D, i, 1);
                _context.SetDebugName(_faceViews[i].Handle, ObjectType.ImageView, $"Point Shadow Light {i / 6} Face {FaceName((int)(i % 6))} View");
            }

            Layout = ImageLayout.Undefined;
        }

        private ImageView CreateView(ImageViewType viewType, uint baseLayer, uint layerCount)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = viewType,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = baseLayer,
                    LayerCount = layerCount
                }
            };
            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create point shadow image view", result);
            return view;
        }

        private void CreateSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = 0f,
                MaxLod = 0f,
                MaxAnisotropy = 1f,
                BorderColor = BorderColor.FloatOpaqueWhite
            };
            Result result = _context.Api.CreateSampler(_context.Device, &samplerInfo, null, out _sampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create point shadow sampler", result);
            _context.SetDebugName(_sampler.Handle, ObjectType.Sampler, "Point Shadow Sampler");
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            const FormatFeatureFlags required = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support sampled point shadow cubemaps.");
        }

        private void DestroyImageResources()
        {
            foreach (ImageView faceView in _faceViews)
            {
                if (faceView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, faceView, null);
            }
            _faceViews = [];
            if (_sampledView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _sampledView, null);
            _sampledView = default;
            if (_allocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
            }
            Layout = ImageLayout.Undefined;
        }

        private void WaitForOutstandingImageUse()
        {
            if (_image.Handle != 0)
                _context.WaitIdle();
        }

        private static string FaceName(int faceIndex)
        {
            return faceIndex switch
            {
                0 => "+X",
                1 => "-X",
                2 => "+Y",
                3 => "-Y",
                4 => "+Z",
                _ => "-Z"
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DestroyImageResources();
            if (_sampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _sampler, null);
            if (_shadowDataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_shadowDataBuffer);
            GC.SuppressFinalize(this);
        }
    }
}
