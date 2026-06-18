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
    public sealed unsafe class DirectionalShadowResources : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private GpuAllocator.Allocation* _staticAllocation;
        private GpuAllocator.Allocation* _workingAllocation;
        private Image _staticImage;
        private Image _workingImage;
        private ImageView[] _staticCascadeViews = Array.Empty<ImageView>();
        private ImageView[] _workingCascadeViews = Array.Empty<ImageView>();
        private Sampler _sampler;
        private BufferHandle _shadowDataBuffer;
        private GPUShadowData _lastShadowData;
        private bool _hasUploadedShadowData;
        private bool _disposed;

        public DirectionalShadowResources(VulkanContext context, BufferManager bufferManager, ShadowSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            Format = Format.D32Sfloat;
            CreateSampler();
            _shadowDataBuffer = _bufferManager.CreateDeviceBuffer(
                (ulong)Marshal.SizeOf<GPUShadowData>(),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.ShadowMaps,
                "Directional Shadow Data Buffer");
            _context.SetDebugName(_bufferManager.GetBuffer(_shadowDataBuffer).Handle, ObjectType.Buffer, "Directional Shadow Data Buffer");
            Ensure(settings);
        }

        public uint MapSize { get; private set; }
        public int CascadeCount { get; private set; }
        public Format Format { get; }
        public bool HasImage => _workingImage.Handle != 0;
        public Image Image => _workingImage;
        public Image StaticImage => _staticImage;
        public Image WorkingImage => _workingImage;
        public ImageLayout StaticLayout { get; set; } = ImageLayout.Undefined;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;
        public BufferHandle ShadowDataBuffer => _shadowDataBuffer;
        public ulong EstimatedImageBytes => _workingImage.Handle == 0
            ? 0
            : 2UL * ImageByteEstimator.EstimateBytes(
                Format,
                new Extent3D { Width = MapSize, Height = MapSize, Depth = 1 },
                mipLevels: 1,
                arrayLayers: (uint)Math.Max(1, CascadeCount));
        public ulong EstimatedBytes => EstimatedImageBytes + (ulong)Marshal.SizeOf<GPUShadowData>();

        public ImageView GetCascadeView(int cascadeIndex)
        {
            if (cascadeIndex < 0 || cascadeIndex >= CascadeCount)
                throw new ArgumentOutOfRangeException(nameof(cascadeIndex));
            return _workingCascadeViews[cascadeIndex];
        }

        public ImageView GetStaticCascadeView(int cascadeIndex)
        {
            if (cascadeIndex < 0 || cascadeIndex >= CascadeCount)
                throw new ArgumentOutOfRangeException(nameof(cascadeIndex));
            return _staticCascadeViews[cascadeIndex];
        }

        public ImageView GetWorkingCascadeView(int cascadeIndex)
        {
            return GetCascadeView(cascadeIndex);
        }

        public bool Ensure(ShadowSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            bool shouldAllocateImage = settings.DirectionalShadowsEnabled && settings.DirectionalCascadeCount > 0;
            if (!shouldAllocateImage)
            {
                if (_workingImage.Handle == 0)
                    return false;

                WaitForOutstandingImageUse();
                DestroyImageResources();
                MapSize = settings.DirectionalShadowMapSize;
                CascadeCount = 0;
                return true;
            }

            if (_workingImage.Handle != 0 && MapSize == settings.DirectionalShadowMapSize && CascadeCount == settings.DirectionalCascadeCount)
                return false;

            Recreate(settings.DirectionalShadowMapSize, settings.DirectionalCascadeCount);
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
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.DirectionalShadowDataBuffer, buffer, 0, Vk.WholeSize);

            for (int i = 0; i < ShadowSettings.MaxDirectionalCascades; i++)
            {
                ImageView view = _workingCascadeViews.Length == 0
                    ? default
                    : _workingCascadeViews[Math.Min(i, _workingCascadeViews.Length - 1)];
                if (view.Handle != 0)
                {
                    bindlessHeap.RegisterTexture(
                        BindlessIndex.DirectionalShadowTextureBase + i,
                        view,
                        _sampler,
                        ImageLayout.DepthStencilReadOnlyOptimal);
                }
                else if (fallbackDepthView.Handle != 0)
                {
                    bindlessHeap.RegisterTexture(
                        BindlessIndex.DirectionalShadowTextureBase + i,
                        fallbackDepthView,
                        _sampler,
                        fallbackDepthLayout);
                }
            }
        }

        public bool UploadShadowData(StagingRing stagingRing, CommandBuffer commandBuffer, in GPUShadowData shadowData)
        {
            if (_hasUploadedShadowData && ShadowDataEquals(_lastShadowData, shadowData))
                return false;

            UploadShadowDataCore(stagingRing, commandBuffer, shadowData);
            _lastShadowData = shadowData;
            _hasUploadedShadowData = true;
            return true;
        }

        private void UploadShadowDataCore(StagingRing stagingRing, CommandBuffer commandBuffer, in GPUShadowData shadowData)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for shadow data upload.", nameof(commandBuffer));

            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _shadowDataBuffer,
                shadowData,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.TaskShaderBitExt |
                    PipelineStageFlags2.MeshShaderBitExt |
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
        }

        private static bool ShadowDataEquals(in GPUShadowData left, in GPUShadowData right)
        {
            fixed (GPUShadowData* leftPtr = &left)
            fixed (GPUShadowData* rightPtr = &right)
            {
                return new ReadOnlySpan<byte>(leftPtr, sizeof(GPUShadowData))
                    .SequenceEqual(new ReadOnlySpan<byte>(rightPtr, sizeof(GPUShadowData)));
            }
        }

        public void Recreate(uint mapSize, int cascadeCount)
        {
            if (mapSize == 0)
                throw new ArgumentOutOfRangeException(nameof(mapSize));
            if (cascadeCount < 1 || cascadeCount > ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentOutOfRangeException(nameof(cascadeCount));

            WaitForOutstandingImageUse();
            DestroyImageResources();
            ValidateFormatSupport();

            MapSize = mapSize;
            CascadeCount = cascadeCount;

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format,
                Extent = new Extent3D { Width = mapSize, Height = mapSize, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = (uint)cascadeCount,
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
                CascadeCount = 0;
                Layout = ImageLayout.Undefined;
                StaticLayout = ImageLayout.Undefined;
                System.Diagnostics.Debug.WriteLine("Directional shadow map allocation skipped because the GPU memory budget is exhausted.");
                return;
            }

            _context.SetDebugName(_staticImage.Handle, ObjectType.Image, "Directional Static Shadow Map");
            _context.SetDebugName(_workingImage.Handle, ObjectType.Image, "Directional Working Shadow Map");

            _staticCascadeViews = new ImageView[cascadeCount];
            _workingCascadeViews = new ImageView[cascadeCount];
            for (int i = 0; i < cascadeCount; i++)
            {
                _staticCascadeViews[i] = CreateCascadeView(_staticImage, (uint)i);
                _context.SetDebugName(_staticCascadeViews[i].Handle, ObjectType.ImageView, $"Directional Static Shadow Cascade {i} View");
                _workingCascadeViews[i] = CreateCascadeView(_workingImage, (uint)i);
                _context.SetDebugName(_workingCascadeViews[i].Handle, ObjectType.ImageView, $"Directional Working Shadow Cascade {i} View");
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
            throw new VulkanException("Failed to create directional shadow map image", result);
        }

        private ImageView CreateCascadeView(Image image, uint cascadeIndex)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = cascadeIndex,
                    LayerCount = 1
                }
            };

            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create directional shadow cascade image view", result);

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
                throw new VulkanException("Failed to create directional shadow sampler", result);
            _context.SetDebugName(_sampler.Handle, ObjectType.Sampler, "Directional Shadow Sampler");
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            const FormatFeatureFlags required = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support sampled depth shadow images.");
        }

        private void DestroyImageResources()
        {
            foreach (ImageView view in _staticCascadeViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }

            foreach (ImageView view in _workingCascadeViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }

            _staticCascadeViews = Array.Empty<ImageView>();
            _workingCascadeViews = Array.Empty<ImageView>();

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
