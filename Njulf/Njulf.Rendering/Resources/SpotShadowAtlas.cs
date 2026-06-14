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
    public sealed unsafe class SpotShadowAtlas : IDisposable
    {
        private const int MaxSpotShadowRecords = 32;
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView _view;
        private Sampler _sampler;
        private BufferHandle _shadowDataBuffer;
        private BufferHandle _shadowIndexBuffer;
        private bool _disposed;

        public SpotShadowAtlas(VulkanContext context, BufferManager bufferManager, ShadowSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Format = Format.D32Sfloat;
            CreateSampler();
            _shadowDataBuffer = _bufferManager.CreateDeviceBuffer(
                (ulong)(MaxSpotShadowRecords * Marshal.SizeOf<GPUSpotShadow>()),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.ShadowMaps,
                "Spot Shadow Data Buffer");
            _context.SetDebugName(_bufferManager.GetBuffer(_shadowDataBuffer).Handle, ObjectType.Buffer, "Spot Shadow Data Buffer");
            _shadowIndexBuffer = _bufferManager.CreateDeviceBuffer(
                (ulong)(LightManager.MaxLights * Marshal.SizeOf<GPULocalLightShadowIndex>()),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.ShadowMaps,
                "Local Light Shadow Index Buffer");
            _context.SetDebugName(_bufferManager.GetBuffer(_shadowIndexBuffer).Handle, ObjectType.Buffer, "Local Light Shadow Index Buffer");
            Ensure(settings);
        }

        public uint AtlasSize { get; private set; }
        public uint TileSize { get; private set; }
        public int Capacity => AtlasSize == 0 || TileSize == 0 ? 0 : LocalShadowAllocator.CalculateSpotAtlasCapacity(AtlasSize, TileSize);
        public Format Format { get; }
        public Image Image => _image;
        public ImageView View => _view;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;
        public ulong EstimatedImageBytes => _image.Handle == 0
            ? 0
            : ImageByteEstimator.EstimateBytes(
                Format,
                new Extent3D { Width = AtlasSize, Height = AtlasSize, Depth = 1 });
        public ulong EstimatedBytes => EstimatedImageBytes +
            (ulong)(MaxSpotShadowRecords * Marshal.SizeOf<GPUSpotShadow>()) +
            (ulong)(LightManager.MaxLights * Marshal.SizeOf<GPULocalLightShadowIndex>());

        public bool Ensure(ShadowSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            bool shouldAllocateImage = settings.SpotShadowsEnabled && settings.MaxShadowedSpotLights > 0;
            if (!shouldAllocateImage)
            {
                if (_image.Handle == 0)
                    return false;

                DestroyImageResources();
                AtlasSize = 0;
                TileSize = settings.SpotShadowTileSize;
                return true;
            }

            if (_image.Handle != 0 && AtlasSize == settings.SpotShadowAtlasSize && TileSize == settings.SpotShadowTileSize)
                return false;

            Recreate(settings.SpotShadowAtlasSize, settings.SpotShadowTileSize);
            return true;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            VkBuffer spotBuffer = _bufferManager.GetBuffer(_shadowDataBuffer);
            VkBuffer indexBuffer = _bufferManager.GetBuffer(_shadowIndexBuffer);
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.SpotShadowDataBuffer, spotBuffer, 0, Vk.WholeSize);
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.LocalLightShadowIndexBuffer, indexBuffer, 0, Vk.WholeSize);
            if (_view.Handle != 0)
                bindlessHeap.RegisterTexture(BindlessIndex.SpotShadowAtlasTexture, _view, _sampler, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        public void Upload(
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            ReadOnlySpan<GPUSpotShadow> spotShadows,
            ReadOnlySpan<GPULocalLightShadowIndex> shadowIndices)
        {
            UploadSpotShadows(stagingRing, commandBuffer, spotShadows);
            UploadShadowIndices(stagingRing, commandBuffer, shadowIndices);
        }

        public void UploadSpotShadows(StagingRing stagingRing, CommandBuffer commandBuffer, ReadOnlySpan<GPUSpotShadow> spotShadows)
        {
            UploadArray(stagingRing, commandBuffer, _shadowDataBuffer, spotShadows, MaxSpotShadowRecords);
        }

        public void UploadShadowIndices(StagingRing stagingRing, CommandBuffer commandBuffer, ReadOnlySpan<GPULocalLightShadowIndex> shadowIndices)
        {
            UploadArray(stagingRing, commandBuffer, _shadowIndexBuffer, shadowIndices, LightManager.MaxLights);
        }

        public void Recreate(uint atlasSize, uint tileSize)
        {
            LocalShadowAllocator.ValidateSpotAtlas(atlasSize, tileSize);
            DestroyImageResources();
            ValidateFormatSupport();

            AtlasSize = atlasSize;
            TileSize = tileSize;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format,
                Extent = new Extent3D { Width = atlasSize, Height = atlasSize, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            var allocInfo = new GpuAllocator.AllocationCreateInfo { Usage = GpuAllocator.MemoryUsage.AutoPreferDevice };
            Image image;
            GpuAllocator.Allocation* allocation;
            GpuAllocator.AllocationInfo allocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(_context.Allocator, &imageInfo, &allocInfo, &image, &allocation, &allocationInfo);
            if (result != Result.Success)
                throw new VulkanException("Failed to create spot shadow atlas image", result);

            _image = image;
            _allocation = allocation;
            _context.SetDebugName(_image.Handle, ObjectType.Image, "Spot Shadow Atlas");
            _view = CreateView(ImageViewType.Type2D, baseLayer: 0, layerCount: 1);
            _context.SetDebugName(_view.Handle, ObjectType.ImageView, "Spot Shadow Atlas View");
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
                throw new VulkanException("Failed to create spot shadow atlas view", result);
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
                throw new VulkanException("Failed to create spot shadow sampler", result);
            _context.SetDebugName(_sampler.Handle, ObjectType.Sampler, "Spot Shadow Sampler");
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            const FormatFeatureFlags required = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support sampled spot shadow atlases.");
        }

        private void DestroyImageResources()
        {
            if (_view.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _view, null);
            _view = default;
            if (_allocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
            }
            Layout = ImageLayout.Undefined;
        }

        private void UploadArray<T>(StagingRing stagingRing, CommandBuffer commandBuffer, BufferHandle destination, ReadOnlySpan<T> data, int capacity)
            where T : unmanaged
        {
            if (data.Length > capacity)
                throw new InvalidOperationException($"Local shadow upload has {data.Length} records, but capacity is {capacity}.");

            GpuBufferUploader.UploadPaddedSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                destination,
                data,
                capacity,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
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
            if (_shadowIndexBuffer.IsValid)
                _bufferManager.DestroyBuffer(_shadowIndexBuffer);
            GC.SuppressFinalize(this);
        }
    }
}
