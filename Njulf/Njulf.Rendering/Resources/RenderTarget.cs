using System;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class RenderTarget : IDisposable
    {
        private readonly VulkanContext _context;
        private GpuAllocator.Allocation* _allocation;
        private Image _image;
        private ImageView _view;
        private bool _disposed;

        internal RenderTarget(
            VulkanContext context,
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Render target name is required.", nameof(name)) : name;
            Format = format;
            Descriptor = descriptor;
            Recreate(extent);
        }

        public string Name { get; }
        public Image Image => _image;
        public ImageView View => _view;
        public Format Format { get; }
        public RenderTargetDescriptor Descriptor { get; }
        public ImageUsageFlags Usage => Descriptor.Usage;
        public Extent2D Extent { get; private set; }
        public ImageLayout Layout { get; private set; } = ImageLayout.Undefined;
        public ulong EstimatedByteSize => CalculateByteSize(Extent.Width, Extent.Height, Format);
        private ImageAspectFlags AspectMask => Descriptor.DepthAttachment
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.ColorBit;

        internal void Recreate(Extent2D extent)
        {
            if (extent.Width == 0 || extent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(extent), "Render target extent must be non-zero.");

            DestroyResources();
            ValidateFormatSupport();

            Extent = extent;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format,
                Extent = new Extent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = Usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            ImageCompressionControlEXT compressionControl = default;
            if (Descriptor.AllowDriverCompression && _context.ImageCompressionControlEnabled)
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
                throw new VulkanException($"Failed to create render target '{Name}'", result);

            _image = image;
            _allocation = allocation;
            try
            {
                _context.SetDebugName(_image.Handle, ObjectType.Image, $"{Name} {extent.Width}x{extent.Height} {Format}");
                _view = CreateImageView();
                _context.SetDebugName(_view.Handle, ObjectType.ImageView, $"{Name} View");
            }
            catch
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
                throw;
            }

            Layout = ImageLayout.Undefined;
        }

        public void TransitionToColorAttachment(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.ColorAttachmentBit, ImageLayout.ColorAttachmentOptimal);
            Transition(
                cmd,
                ImageLayout.ColorAttachmentOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit);
        }

        public void TransitionToDepthAttachment(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.DepthStencilAttachmentBit, ImageLayout.DepthStencilAttachmentOptimal);
            Transition(
                cmd,
                ImageLayout.DepthStencilAttachmentOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit);
        }

        public void TransitionToDepthReadOnly(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.SampledBit, ImageLayout.DepthStencilReadOnlyOptimal);
            Transition(
                cmd,
                ImageLayout.DepthStencilReadOnlyOptimal,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit);
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

        public void TransitionToStorageWrite(CommandBuffer cmd)
        {
            EnsureUsage(ImageUsageFlags.StorageBit, ImageLayout.General);
            Transition(
                cmd,
                ImageLayout.General,
                GetSourceStage(Layout),
                GetSourceAccess(Layout),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
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

        public void TransitionToLayout(
            CommandBuffer cmd,
            ImageLayout newLayout,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            PipelineStageFlags2? srcStage = null,
            AccessFlags2? srcAccess = null,
            bool force = false)
        {
            EnsureLayoutUsage(newLayout);
            Transition(
                cmd,
                newLayout,
                srcStage ?? GetSourceStage(Layout),
                srcAccess ?? GetSourceAccess(Layout),
                dstStage,
                dstAccess,
                force);
        }

        private void Transition(
            CommandBuffer cmd,
            ImageLayout newLayout,
            PipelineStageFlags2 srcStage,
            AccessFlags2 srcAccess,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            bool force = false)
        {
            if (Layout == newLayout && !force)
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
                    AspectMask = AspectMask,
                    BaseMipLevel = 0,
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
            Layout = newLayout;
        }

        private static PipelineStageFlags2 GetSourceStage(ImageLayout layout)
        {
            return layout switch
            {
                ImageLayout.Undefined => PipelineStageFlags2.None,
                ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                ImageLayout.ColorAttachmentOptimal => PipelineStageFlags2.ColorAttachmentOutputBit,
                ImageLayout.DepthStencilAttachmentOptimal => PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                ImageLayout.DepthStencilReadOnlyOptimal => PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.EarlyFragmentTestsBit,
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
                ImageLayout.ColorAttachmentOptimal => AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit,
                ImageLayout.DepthStencilAttachmentOptimal => AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                ImageLayout.General => AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit,
                _ => AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
            };
        }

        private ImageView CreateImageView()
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = AspectMask,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create render target view '{Name}'", result);

            return view;
        }

        private void ValidateFormatSupport()
        {
            FormatProperties properties;
            _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, Format, &properties);
            FormatFeatureFlags required = 0;
            if (Descriptor.ColorAttachment)
                required |= FormatFeatureFlags.ColorAttachmentBit;
            if (Descriptor.DepthAttachment)
                required |= FormatFeatureFlags.DepthStencilAttachmentBit;
            if (Descriptor.Sampled)
                required |= FormatFeatureFlags.SampledImageBit;
            if (Descriptor.Storage)
                required |= FormatFeatureFlags.StorageImageBit;
            if ((properties.OptimalTilingFeatures & required) != required)
                throw new VulkanException($"Format {Format} does not support required render target features {required}.");
        }

        private void EnsureUsage(ImageUsageFlags required, ImageLayout layout)
        {
            if ((Usage & required) == required)
                return;

            throw new InvalidOperationException(
                $"Render target '{Name}' was not created with {required} usage and cannot transition to {layout}.");
        }

        private void EnsureLayoutUsage(ImageLayout layout)
        {
            switch (layout)
            {
                case ImageLayout.ColorAttachmentOptimal:
                    EnsureUsage(ImageUsageFlags.ColorAttachmentBit, layout);
                    break;
                case ImageLayout.DepthStencilAttachmentOptimal:
                    EnsureUsage(ImageUsageFlags.DepthStencilAttachmentBit, layout);
                    break;
                case ImageLayout.DepthStencilReadOnlyOptimal:
                case ImageLayout.ShaderReadOnlyOptimal:
                    EnsureUsage(ImageUsageFlags.SampledBit, layout);
                    break;
                case ImageLayout.General:
                    EnsureUsage(ImageUsageFlags.StorageBit, layout);
                    break;
                case ImageLayout.TransferSrcOptimal:
                    EnsureUsage(ImageUsageFlags.TransferSrcBit, layout);
                    break;
                case ImageLayout.TransferDstOptimal:
                    EnsureUsage(ImageUsageFlags.TransferDstBit, layout);
                    break;
            }
        }

        private void DestroyResources()
        {
            if (_view.Handle != 0)
            {
                _context.Api.DestroyImageView(_context.Device, _view, null);
                _view = default;
            }

            if (_allocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _image, _allocation);
                _allocation = null;
                _image = default;
            }

            Layout = ImageLayout.Undefined;
        }

        public static ulong CalculateByteSize(uint width, uint height, Format format)
        {
            if (width == 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height == 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            ulong bytesPerPixel = format switch
            {
                Format.R16G16B16A16Sfloat => 8,
                Format.R16G16Sfloat => 4,
                Format.R16Sfloat => 2,
                Format.D16Unorm => 2,
                Format.D24UnormS8Uint => 4,
                Format.D32Sfloat => 4,
                Format.D32SfloatS8Uint => 5,
                Format.R32Sfloat => 4,
                Format.R32G32B32A32Sfloat => 16,
                Format.R8Unorm => 1,
                Format.R8G8Unorm => 2,
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4,
                _ => throw new NotSupportedException($"Render target format {format} does not have a known byte size.")
            };

            return checked((ulong)width * height * bytesPerPixel);
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
