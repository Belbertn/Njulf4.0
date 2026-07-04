using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class VolumeTexture : IDisposable
    {
        private readonly VulkanContext _context;
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView _view;
        private ImageView _storageView;
        private ImageView[] _singleMipViews = Array.Empty<ImageView>();
        private bool _disposed;

        public VolumeTexture(
            VulkanContext context,
            string name,
            Format format,
            Extent3D extent,
            VolumeTextureDescriptor descriptor)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Volume texture name is required.", nameof(name)) : name;
            Format = format;
            Descriptor = descriptor;
            Recreate(extent);
        }

        public string Name { get; }
        public Image Image => _image;
        public ImageView View => _view;
        public ImageView StorageView => _storageView.Handle != 0 ? _storageView : _view;
        public Format Format { get; }
        public VolumeTextureDescriptor Descriptor { get; }
        public ImageUsageFlags Usage => Descriptor.Usage;
        public Extent3D Extent { get; private set; }
        public uint MipLevels { get; private set; }
        public ImageLayout Layout { get; private set; } = ImageLayout.Undefined;
        public ulong EstimatedByteSize => CalculateByteSize(Extent, Format, MipLevels);

        public void Recreate(Extent3D extent)
        {
            if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
                throw new ArgumentOutOfRangeException(nameof(extent), "Volume texture extent must be non-zero.");

            DestroyResources();
            ValidateFormatSupport();

            Extent = extent;
            MipLevels = Descriptor.GenerateFullMipChain
                ? CalculateFullMipCount(extent)
                : 1u;

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type3D,
                Format = Format,
                Extent = extent,
                MipLevels = MipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = Usage,
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
                throw new VulkanException($"Failed to create volume texture '{Name}'", result);

            _image = image;
            _allocation = allocation;
            try
            {
                _context.SetDebugName(_image.Handle, ObjectType.Image, $"{Name} {extent.Width}x{extent.Height}x{extent.Depth} {Format}");
                _view = CreateImageView(0, MipLevels);
                _context.SetDebugName(_view.Handle, ObjectType.ImageView, $"{Name} View");
                _storageView = Descriptor.Storage && MipLevels > 1
                    ? CreateImageView(0, 1)
                    : _view;
                if (_storageView.Handle != _view.Handle)
                    _context.SetDebugName(_storageView.Handle, ObjectType.ImageView, $"{Name} Storage View");
                _singleMipViews = new ImageView[MipLevels];
                _singleMipViews[0] = _storageView;
            }
            catch
            {
                if (_storageView.Handle != 0 && _storageView.Handle != _view.Handle)
                    _context.Api.DestroyImageView(_context.Device, _storageView, null);
                if (_view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, _view, null);
                _storageView = default;
                _view = default;
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
                throw;
            }

            Layout = ImageLayout.Undefined;
        }

        public void TransitionToShaderRead(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.SampledBit, ImageLayout.ShaderReadOnlyOptimal);
            Transition(
                cmd,
                ImageLayout.ShaderReadOnlyOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit);
        }

        public void GenerateMipChain(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, ImageLayout.ShaderReadOnlyOptimal);
            if (MipLevels <= 1)
            {
                TransitionToShaderRead(cmd);
                return;
            }

            if (Layout != ImageLayout.General)
                TransitionToStorageReadWrite(cmd);

            uint mipWidth = Extent.Width;
            uint mipHeight = Extent.Height;
            uint mipDepth = Extent.Depth;

            TransitionMip(
                cmd,
                0,
                ImageLayout.General,
                ImageLayout.TransferSrcOptimal,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);

            for (uint mip = 1; mip < MipLevels; mip++)
            {
                TransitionMip(
                    cmd,
                    mip,
                    ImageLayout.General,
                    ImageLayout.TransferDstOptimal,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit);

                uint nextWidth = Math.Max(1u, mipWidth >> 1);
                uint nextHeight = Math.Max(1u, mipHeight >> 1);
                uint nextDepth = Math.Max(1u, mipDepth >> 1);
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip - 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
                blit.SrcOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
                blit.SrcOffsets[1] = new Offset3D { X = (int)mipWidth, Y = (int)mipHeight, Z = (int)mipDepth };
                blit.DstOffsets[0] = new Offset3D { X = 0, Y = 0, Z = 0 };
                blit.DstOffsets[1] = new Offset3D { X = (int)nextWidth, Y = (int)nextHeight, Z = (int)nextDepth };
                _context.Api.CmdBlitImage(
                    cmd,
                    _image,
                    ImageLayout.TransferSrcOptimal,
                    _image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &blit,
                    Filter.Linear);

                TransitionMip(
                    cmd,
                    mip - 1,
                    ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderSampledReadBit);

                TransitionMip(
                    cmd,
                    mip,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.TransferSrcOptimal,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit);

                mipWidth = nextWidth;
                mipHeight = nextHeight;
                mipDepth = nextDepth;
            }

            TransitionMip(
                cmd,
                MipLevels - 1,
                ImageLayout.TransferSrcOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderSampledReadBit);
            Layout = ImageLayout.ShaderReadOnlyOptimal;
        }

        public void TransitionToStorageReadWrite(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.StorageBit, ImageLayout.General);
            Transition(
                cmd,
                ImageLayout.General,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        }

        public void TransitionToTransferSource(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.TransferSrcBit, ImageLayout.TransferSrcOptimal);
            Transition(
                cmd,
                ImageLayout.TransferSrcOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferReadBit);
        }

        public void TransitionToTransferDestination(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.TransferDstBit, ImageLayout.TransferDstOptimal);
            Transition(
                cmd,
                ImageLayout.TransferDstOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit);
        }

        public ImageView GetSingleMipView(uint mipLevel)
        {
            if (mipLevel >= MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mipLevel), $"Mip level {mipLevel} is outside volume texture '{Name}' mip count {MipLevels}.");

            ImageView existing = _singleMipViews[mipLevel];
            if (existing.Handle != 0)
                return existing;

            ImageView view = CreateImageView(mipLevel, 1);
            _context.SetDebugName(view.Handle, ObjectType.ImageView, $"{Name} Mip {mipLevel} View");
            _singleMipViews[mipLevel] = view;
            return view;
        }

        private void Transition(
            CommandBuffer cmd,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess)
        {
            if (Layout == newLayout)
                return;

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStage,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStage,
                DstAccessMask = dstAccess,
                OldLayout = Layout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = MipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
            Layout = newLayout;
        }

        private static PipelineStageFlags2 GetSourceStage(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.General => PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags2.TransferBit,
                _ => PipelineStageFlags2.AllCommandsBit
            };
        }

        private static AccessFlags2 GetSourceAccess(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => AccessFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit,
                ImageLayout.General => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit,
                _ => AccessFlags2.MemoryReadBit
            };
        }

        private void TransitionMip(
            CommandBuffer cmd,
            uint mipLevel,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess)
        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStage,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStage,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = mipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private ImageView CreateImageView(uint baseMipLevel, uint levelCount)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type3D,
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

            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create volume texture view '{Name}'", result);

            return view;
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            FormatFeatureFlags required = 0;
            if (Descriptor.Sampled)
                required |= FormatFeatureFlags.SampledImageBit;
            if (Descriptor.Storage)
                required |= FormatFeatureFlags.StorageImageBit;
            if (Descriptor.GenerateFullMipChain)
                required |= FormatFeatureFlags.BlitSrcBit |
                    FormatFeatureFlags.BlitDstBit |
                    FormatFeatureFlags.SampledImageFilterLinearBit;
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support required volume texture features {required}.");
        }

        private void EnsureUsage(ImageUsageFlags required, ImageLayout layout)
        {
            if ((Usage & required) == required)
                return;

            throw new InvalidOperationException(
                $"Volume texture '{Name}' was not created with {required} usage and cannot transition to {layout}.");
        }

        private void DestroyResources()
        {
            for (int i = 0; i < _singleMipViews.Length; i++)
            {
                ImageView mipView = _singleMipViews[i];
                if (mipView.Handle == 0 || mipView.Handle == _view.Handle || mipView.Handle == _storageView.Handle)
                    continue;

                _context.Api.DestroyImageView(_context.Device, mipView, null);
            }
            _singleMipViews = Array.Empty<ImageView>();

            if (_storageView.Handle != 0 && _storageView.Handle != _view.Handle)
            {
                _context.Api.DestroyImageView(_context.Device, _storageView, null);
                _storageView = default;
            }

            if (_view.Handle != 0)
            {
                _context.Api.DestroyImageView(_context.Device, _view, null);
                _view = default;
            }
            _storageView = default;

            if (_allocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
            }

            Layout = ImageLayout.Undefined;
        }

        public static uint CalculateFullMipCount(Extent3D extent)
        {
            uint largestAxis = Math.Max(extent.Width, Math.Max(extent.Height, extent.Depth));
            uint levels = 1;
            while (largestAxis > 1)
            {
                largestAxis >>= 1;
                levels++;
            }

            return levels;
        }

        public static ulong CalculateByteSize(Extent3D extent, Format format, uint mipLevels = 1)
        {
            if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
                throw new ArgumentOutOfRangeException(nameof(extent));

            ulong bytes = 0;
            uint width = extent.Width;
            uint height = extent.Height;
            uint depth = extent.Depth;
            for (uint mip = 0; mip < Math.Max(1u, mipLevels); mip++)
            {
                bytes += RenderTarget.CalculateByteSize(width, height, format) * depth;
                width = Math.Max(1u, width >> 1);
                height = Math.Max(1u, height >> 1);
                depth = Math.Max(1u, depth >> 1);
            }

            return bytes;
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
