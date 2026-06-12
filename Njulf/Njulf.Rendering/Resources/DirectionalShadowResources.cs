using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
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
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView[] _cascadeViews = Array.Empty<ImageView>();
        private Sampler _sampler;
        private BufferHandle _shadowDataBuffer;
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
                true);
            _context.SetDebugName(_bufferManager.GetBuffer(_shadowDataBuffer).Handle, ObjectType.Buffer, "Directional Shadow Data Buffer");
            Recreate(settings.DirectionalShadowMapSize, settings.DirectionalCascadeCount);
        }

        public uint MapSize { get; private set; }
        public int CascadeCount { get; private set; }
        public Format Format { get; }
        public Image Image => _image;
        public ImageLayout Layout { get; set; } = ImageLayout.Undefined;
        public BufferHandle ShadowDataBuffer => _shadowDataBuffer;

        public ImageView GetCascadeView(int cascadeIndex)
        {
            if (cascadeIndex < 0 || cascadeIndex >= CascadeCount)
                throw new ArgumentOutOfRangeException(nameof(cascadeIndex));
            return _cascadeViews[cascadeIndex];
        }

        public void Ensure(ShadowSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (MapSize != settings.DirectionalShadowMapSize || CascadeCount != settings.DirectionalCascadeCount)
                Recreate(settings.DirectionalShadowMapSize, settings.DirectionalCascadeCount);
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            VkBuffer buffer = _bufferManager.GetBuffer(_shadowDataBuffer);
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.DirectionalShadowDataBuffer, buffer, 0, Vk.WholeSize);

            for (int i = 0; i < ShadowSettings.MaxDirectionalCascades; i++)
            {
                ImageView view = _cascadeViews.Length == 0
                    ? default
                    : _cascadeViews[Math.Min(i, _cascadeViews.Length - 1)];
                if (view.Handle != 0)
                {
                    bindlessHeap.RegisterTexture(
                        BindlessIndex.DirectionalShadowTextureBase + i,
                        view,
                        _sampler,
                        ImageLayout.DepthStencilReadOnlyOptimal);
                }
            }
        }

        public void UploadShadowData(StagingRing stagingRing, CommandBuffer commandBuffer, in GPUShadowData shadowData)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for shadow data upload.", nameof(commandBuffer));

            ulong dataSize = (ulong)Marshal.SizeOf<GPUShadowData>();
            var (stagingHandle, stagingOffset) = stagingRing.Allocate(dataSize);
            void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);

            fixed (GPUShadowData* source = &shadowData)
            {
                System.Buffer.MemoryCopy(source, (byte*)mappedData + stagingOffset, dataSize, dataSize);
            }

            _bufferManager.FlushBuffer(stagingHandle, stagingOffset, dataSize);

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = 0,
                Size = dataSize
            };

            _context.Api.CmdCopyBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(_shadowDataBuffer),
                1,
                &copy);

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt |
                               PipelineStageFlags2.MeshShaderBitExt |
                               PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_shadowDataBuffer),
                Offset = 0,
                Size = dataSize
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }

        public void Recreate(uint mapSize, int cascadeCount)
        {
            if (mapSize == 0)
                throw new ArgumentOutOfRangeException(nameof(mapSize));
            if (cascadeCount < 1 || cascadeCount > ShadowSettings.MaxDirectionalCascades)
                throw new ArgumentOutOfRangeException(nameof(cascadeCount));

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
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
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
                throw new VulkanException("Failed to create directional shadow map image", result);

            _image = image;
            _allocation = allocation;
            _context.SetDebugName(_image.Handle, ObjectType.Image, "Directional Shadow Map");

            _cascadeViews = new ImageView[cascadeCount];
            for (int i = 0; i < cascadeCount; i++)
            {
                _cascadeViews[i] = CreateCascadeView((uint)i);
                _context.SetDebugName(_cascadeViews[i].Handle, ObjectType.ImageView, $"Directional Shadow Cascade {i} View");
            }

            Layout = ImageLayout.Undefined;
        }

        private ImageView CreateCascadeView(uint cascadeIndex)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
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
            foreach (ImageView view in _cascadeViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }

            _cascadeViews = Array.Empty<ImageView>();

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
            DestroyImageResources();
            if (_sampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _sampler, null);
            if (_shadowDataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_shadowDataBuffer);
            GC.SuppressFinalize(this);
        }
    }
}
