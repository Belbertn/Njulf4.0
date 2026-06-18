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
        private GpuAllocator.Allocation* _staticAllocation;
        private GpuAllocator.Allocation* _workingAllocation;
        private Image _staticImage;
        private Image _workingImage;
        private ImageView _staticSampledView;
        private ImageView _workingSampledView;
        private ImageView[] _staticFaceViews = [];
        private ImageView[] _workingFaceViews = [];
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
        public Image Image => _workingImage;
        public Image StaticImage => _staticImage;
        public Image WorkingImage => _workingImage;
        public ImageLayout StaticLayout { get; set; } = ImageLayout.Undefined;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;
        public ulong EstimatedImageBytes => _workingImage.Handle == 0
            ? 0
            : 2UL * ImageByteEstimator.EstimateBytes(
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
            return _workingFaceViews[pointIndex * 6 + faceIndex];
        }

        public ImageView GetStaticFaceView(int pointIndex, int faceIndex)
        {
            if (pointIndex < 0 || pointIndex >= PointCapacity)
                throw new ArgumentOutOfRangeException(nameof(pointIndex));
            if (faceIndex < 0 || faceIndex >= 6)
                throw new ArgumentOutOfRangeException(nameof(faceIndex));
            return _staticFaceViews[pointIndex * 6 + faceIndex];
        }

        public bool Ensure(ShadowSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            bool shouldAllocateImage = settings.PointShadowsEnabled && settings.MaxShadowedPointLights > 0;
            if (!shouldAllocateImage)
            {
                if (_workingImage.Handle == 0)
                    return false;

                WaitForOutstandingImageUse();
                DestroyImageResources();
                MapSize = settings.PointShadowMapSize;
                PointCapacity = 0;
                return true;
            }

            if (_workingImage.Handle != 0 && MapSize == settings.PointShadowMapSize && PointCapacity == settings.MaxShadowedPointLights)
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
            if (_workingSampledView.Handle != 0)
                bindlessHeap.RegisterTexture(BindlessIndex.PointShadowCubemapArrayTexture, _workingSampledView, _sampler, ImageLayout.DepthStencilReadOnlyOptimal);
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
                Usage = ImageUsageFlags.DepthStencilAttachmentBit |
                        ImageUsageFlags.SampledBit |
                        ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.TransferDstBit,
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
            if (!TryCreateImage(&imageInfo, &allocInfo, out _staticImage, out _staticAllocation) ||
                !TryCreateImage(&imageInfo, &allocInfo, out _workingImage, out _workingAllocation))
            {
                DestroyImageResources();
                MapSize = mapSize;
                PointCapacity = 0;
                Layout = ImageLayout.Undefined;
                StaticLayout = ImageLayout.Undefined;
                System.Diagnostics.Debug.WriteLine("Point shadow cubemap array allocation skipped because the GPU memory budget is exhausted.");
                return;
            }

            _context.SetDebugName(_staticImage.Handle, ObjectType.Image, "Point Static Shadow Cubemap Array");
            _context.SetDebugName(_workingImage.Handle, ObjectType.Image, "Point Working Shadow Cubemap Array");
            _staticSampledView = CreateView(_staticImage, ImageViewType.Type2DArray, 0, layerCount);
            _context.SetDebugName(_staticSampledView.Handle, ObjectType.ImageView, "Point Static Shadow 2D Array View");
            _workingSampledView = CreateView(_workingImage, ImageViewType.Type2DArray, 0, layerCount);
            _context.SetDebugName(_workingSampledView.Handle, ObjectType.ImageView, "Point Working Shadow 2D Array View");

            _staticFaceViews = new ImageView[layerCount];
            _workingFaceViews = new ImageView[layerCount];
            for (uint i = 0; i < layerCount; i++)
            {
                _staticFaceViews[i] = CreateView(_staticImage, ImageViewType.Type2D, i, 1);
                _context.SetDebugName(_staticFaceViews[i].Handle, ObjectType.ImageView, $"Point Static Shadow Light {i / 6} Face {FaceName((int)(i % 6))} View");
                _workingFaceViews[i] = CreateView(_workingImage, ImageViewType.Type2D, i, 1);
                _context.SetDebugName(_workingFaceViews[i].Handle, ObjectType.ImageView, $"Point Working Shadow Light {i / 6} Face {FaceName((int)(i % 6))} View");
            }

            StaticLayout = ImageLayout.Undefined;
            Layout = ImageLayout.Undefined;
        }

        private bool TryCreateImage(
            ImageCreateInfo* imageInfo,
            GpuAllocator.AllocationCreateInfo* allocInfo,
            out Image image,
            out GpuAllocator.Allocation* allocation)
        {
            GpuAllocator.AllocationInfo allocationInfo;
            Image createdImage;
            GpuAllocator.Allocation* createdAllocation;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator,
                imageInfo,
                allocInfo,
                &createdImage,
                &createdAllocation,
                &allocationInfo);

            if (result == Result.Success)
            {
                image = createdImage;
                allocation = createdAllocation;
                return true;
            }

            image = default;
            allocation = null;
            if (_context.IsMemoryBudgetExceeded(result))
                return false;
            throw new VulkanException("Failed to create point shadow cubemap array", result);
        }

        private ImageView CreateView(Image image, ImageViewType viewType, uint baseLayer, uint layerCount)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
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
            foreach (ImageView faceView in _staticFaceViews)
            {
                if (faceView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, faceView, null);
            }
            foreach (ImageView faceView in _workingFaceViews)
            {
                if (faceView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, faceView, null);
            }
            _staticFaceViews = [];
            _workingFaceViews = [];
            if (_staticSampledView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _staticSampledView, null);
            _staticSampledView = default;
            if (_workingSampledView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _workingSampledView, null);
            _workingSampledView = default;
            if (_staticAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _staticImage, _staticAllocation);
                _staticAllocation = null;
                _staticImage = default;
            }
            if (_workingAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _workingImage, _workingAllocation);
                _workingAllocation = null;
                _workingImage = default;
            }
            StaticLayout = ImageLayout.Undefined;
            Layout = ImageLayout.Undefined;
        }

        private void WaitForOutstandingImageUse()
        {
            if (_staticImage.Handle != 0 || _workingImage.Handle != 0)
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
