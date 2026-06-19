using System;
using Silk.NET.Core;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using GpuAllocator = Vma;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Manages the Vulkan swapchain, surface, and depth resources.
    /// </summary>
    public unsafe class SwapchainManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly IWindow _window;
        
        private SurfaceKHR _surface;
        private SwapchainKHR _swapchain;
        private Image[] _images = Array.Empty<Image>();
        private ImageView[] _imageViews = Array.Empty<ImageView>();
        private SurfaceFormatKHR _surfaceFormat;
        private Extent2D _extent;
        
        // Depth resources
        private GpuAllocator.Allocation* _depthAllocation;
        private Image _depthImage;
        private ImageView _depthImageView;
        private Format _depthFormat;
        private ImageLayout _depthImageLayout = ImageLayout.Undefined;
        
        // Layout tracking
        private ImageLayout[] _imageLayouts = Array.Empty<ImageLayout>();
        
        private bool _disposed;
        
        public SurfaceKHR Surface => _surface;
        public SwapchainKHR Swapchain => _swapchain;
        public ImageView[] ImageViews => _imageViews;
        public Extent2D Extent => _extent;
        public Format SurfaceFormat => _surfaceFormat.Format;
        public Image[] Images => _images;
        public Format DepthFormat => _depthFormat;
        public Image DepthImage => _depthImage;
        public ImageView DepthImageView => _depthImageView;
        public ImageLayout DepthImageLayout => _depthImageLayout;
        public uint ImageCount => (uint)_images.Length;
        public ulong EstimatedBytes
        {
            get
            {
                if (_extent.Width == 0 || _extent.Height == 0)
                    return 0;

                ulong colorBytes = ImageByteEstimator.EstimateBytes(
                    _surfaceFormat.Format,
                    new Extent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 });
                ulong depthBytes = ImageByteEstimator.EstimateBytes(
                    _depthFormat,
                    new Extent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 });
                return checked(colorBytes * (ulong)_images.Length + depthBytes);
            }
        }
        
        public SwapchainManager(VulkanContext context, IWindow window)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _window = window ?? throw new ArgumentNullException(nameof(window));
            
            CreateSurface();
            CreateSwapchain(default);
            CreateDepthResources();
        }
        
        private void CreateSurface()
        {
            if (_window.VkSurface == null)
                throw new InvalidOperationException("Window does not support Vulkan surface creation");
            
            _surface = _window.VkSurface.Create<Silk.NET.Vulkan.AllocationCallbacks>(
                _context.Instance.ToHandle(), null).ToSurface();
            
            if (_surface.Handle == 0)
                throw new VulkanException("Failed to create Vulkan surface");
            _context.SetDebugName(_surface.Handle, ObjectType.SurfaceKhr, "Main Window Surface");
            
            System.Diagnostics.Debug.WriteLine("Vulkan surface created.");
        }
        
        private void CreateSwapchain(SwapchainKHR oldSwapchain)
        {
            // Get surface capabilities
            SurfaceCapabilitiesKHR surfaceCapabilities;
            Result result = _context.KhrSurface.GetPhysicalDeviceSurfaceCapabilities(
                _context.PhysicalDevice, _surface, out surfaceCapabilities);
            if (result != Result.Success)
                throw new VulkanException("Failed to get surface capabilities", result);
            
            // Get surface formats
            uint formatCount = 0;
            _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(
                _context.PhysicalDevice, _surface, &formatCount, null);
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = formats)
                _context.KhrSurface.GetPhysicalDeviceSurfaceFormats(
                    _context.PhysicalDevice, _surface, &formatCount, formatsPtr);
            
            _surfaceFormat = ChooseSwapSurfaceFormat(formats);
            
            // Get present modes
            uint presentModeCount = 0;
            _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
                _context.PhysicalDevice, _surface, &presentModeCount, null);
            var presentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* modesPtr = presentModes)
                _context.KhrSurface.GetPhysicalDeviceSurfacePresentModes(
                    _context.PhysicalDevice, _surface, &presentModeCount, modesPtr);
            
            PresentModeKHR presentMode = ChooseSwapPresentMode(presentModes);
            
            // Choose extent
            _extent = ChooseSwapExtent(_window, surfaceCapabilities);
            
            // Determine image count (triple buffering preferred when supported).
            uint imageCount = Math.Max(surfaceCapabilities.MinImageCount + 1, 3);
            if (surfaceCapabilities.MaxImageCount != 0 && imageCount > surfaceCapabilities.MaxImageCount)
                imageCount = surfaceCapabilities.MaxImageCount;
            
            var swapchainCreateInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = _surfaceFormat.Format,
                ImageColorSpace = _surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                QueueFamilyIndexCount = 0,
                PQueueFamilyIndices = null,
                PreTransform = surfaceCapabilities.CurrentTransform,
                CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
                PresentMode = presentMode,
                Clipped = true,
                OldSwapchain = oldSwapchain
            };
            
            result = _context.KhrSwapchain.CreateSwapchain(
                _context.Device, &swapchainCreateInfo, null, out _swapchain);
            if (result != Result.Success)
                throw new VulkanException("Failed to create swapchain", result);
            _context.SetDebugName(_swapchain.Handle, ObjectType.SwapchainKhr, "Main Swapchain");
            
            // Get swapchain images
            uint actualImageCount = 0;
            result = _context.KhrSwapchain.GetSwapchainImages(
                _context.Device, _swapchain, &actualImageCount, null);
            if (result != Result.Success)
                throw new VulkanException("Failed to get swapchain image count", result);
            
            _images = new Image[actualImageCount];
            _imageViews = new ImageView[actualImageCount];
            _imageLayouts = new ImageLayout[actualImageCount];
            
            fixed (Image* imagesPtr = _images)
            {
                result = _context.KhrSwapchain.GetSwapchainImages(
                    _context.Device, _swapchain, &actualImageCount, imagesPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to get swapchain images", result);
            }
            
            // Create image views
            for (uint i = 0; i < actualImageCount; i++)
            {
                _context.SetDebugName(_images[i].Handle, ObjectType.Image, $"Swapchain Image {i}");
                _imageViews[i] = CreateImageView(_images[i], _surfaceFormat.Format);
                _context.SetDebugName(_imageViews[i].Handle, ObjectType.ImageView, $"Swapchain Image View {i}");
                _imageLayouts[i] = ImageLayout.Undefined;
            }
            
            System.Diagnostics.Debug.WriteLine($"Swapchain created with {actualImageCount} images ({_extent.Width}x{_extent.Height}).");
        }
        
        private SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] availableFormats)
        {
            foreach (var availableFormat in availableFormats)
            {
                if (availableFormat.Format == Format.B8G8R8A8Unorm && 
                    availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return availableFormat;
                }
            }
            
            foreach (var availableFormat in availableFormats)
            {
                if (availableFormat.Format == Format.B8G8R8A8Srgb && 
                    availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return availableFormat;
                }
            }
            
            return availableFormats[0];
        }
        
        private PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] availablePresentModes)
        {
            foreach (var availablePresentMode in availablePresentModes)
            {
                if (availablePresentMode == PresentModeKHR.MailboxKhr)
                    return availablePresentMode;
            }
            
            foreach (var availablePresentMode in availablePresentModes)
            {
                if (availablePresentMode == PresentModeKHR.FifoKhr)
                    return availablePresentMode;
            }
            
            return PresentModeKHR.ImmediateKhr;
        }
        
        private Extent2D ChooseSwapExtent(IWindow window, SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
                return capabilities.CurrentExtent;
            
            // Clamp to window size
            uint width = Math.Max(capabilities.MinImageExtent.Width, 
                           Math.Min(capabilities.MaxImageExtent.Width, (uint)window.Size.X));
            uint height = Math.Max(capabilities.MinImageExtent.Height,
                            Math.Min(capabilities.MaxImageExtent.Height, (uint)window.Size.Y));
            
            return new Extent2D { Width = width, Height = height };
        }
        
        private ImageView CreateImageView(Image image, Format format)
        {
            var viewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(),
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            
            Result result = _context.Api.CreateImageView(
                _context.Device, &viewCreateInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create image view", result);
            
            return view;
        }
        
        private void CreateDepthResources()
        {
            // Find supported depth format
            Format[] depthFormats = {
                Format.D32Sfloat,
                Format.D32SfloatS8Uint,
                Format.D24UnormS8Uint,
                Format.D16Unorm
            };
            
            _depthFormat = FindSupportedFormat(
                depthFormats,
                ImageTiling.Optimal,
                FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit);
            
            // Create depth image
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = _depthFormat,
                Extent = new Extent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
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
            
            Image depthImage;
            GpuAllocator.Allocation* depthAllocation;
            GpuAllocator.AllocationInfo allocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator,
                &imageInfo,
                &allocInfo,
                &depthImage,
                &depthAllocation,
                &allocationInfo);
            
            if (result != Result.Success)
                throw new VulkanException("Failed to create depth image", result);

            _depthImage = depthImage;
            _depthAllocation = depthAllocation;
            _context.SetDebugName(_depthImage.Handle, ObjectType.Image, "Main Depth Image");
            
            // Create depth image view
            _depthImageView = CreateDepthImageView(_depthImage, _depthFormat);
            _context.SetDebugName(_depthImageView.Handle, ObjectType.ImageView, "Main Depth Image View");
            
            // Transition depth image to DepthStencilOptimal
            TransitionImageLayout(
                _depthImage,
                ImageLayout.Undefined,
                ImageLayout.DepthStencilAttachmentOptimal);
            _depthImageLayout = ImageLayout.DepthStencilAttachmentOptimal;
            
            System.Diagnostics.Debug.WriteLine("Depth resources created.");
        }
        
        private ImageView CreateDepthImageView(Image image, Format format)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            
            Result result = _context.Api.CreateImageView(
                _context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create depth image view", result);
            
            return view;
        }
        
        private Format FindSupportedFormat(Format[] candidates, ImageTiling tiling, FormatFeatureFlags features)
        {
            foreach (var format in candidates)
            {
                var formatProps = new FormatProperties();
                _context.Api.GetPhysicalDeviceFormatProperties(
                    _context.PhysicalDevice, format, &formatProps);
                
                if (tiling == ImageTiling.Linear && 
                    (formatProps.LinearTilingFeatures & features) == features)
                    return format;
                
                if (tiling == ImageTiling.Optimal && 
                    (formatProps.OptimalTilingFeatures & features) == features)
                    return format;
            }
            
            throw new VulkanException("Failed to find supported depth format");
        }
        
        private void TransitionImageLayout(Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            var ctx = _context.BeginSingleTimeCommands();
            var cmd = ctx.CommandBuffer;
            
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.None,
                DstAccessMask = AccessFlags.None,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = (newLayout == ImageLayout.DepthStencilAttachmentOptimal) 
                        ? ImageAspectFlags.DepthBit 
                        : ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            
            PipelineStageFlags sourceStage = oldLayout == ImageLayout.Undefined
                ? PipelineStageFlags.TopOfPipeBit
                : PipelineStageFlags.ColorAttachmentOutputBit;
            
            PipelineStageFlags destinationStage = newLayout == ImageLayout.TransferDstOptimal
                ? PipelineStageFlags.TransferBit
                : PipelineStageFlags.EarlyFragmentTestsBit;
            
            _context.Api.CmdPipelineBarrier(
                cmd,
                sourceStage, destinationStage,
                0,
                null,
                null,
                1,
                &barrier);
            
            _context.EndSingleTimeCommands(ctx);
        }
        
        /// <summary>
        /// Acquires the next swapchain image.
        /// </summary>
        public uint AcquireNextImage(Semaphore imageAvailableSemaphore)
        {
            Result result = TryAcquireNextImage(imageAvailableSemaphore, out uint imageIndex);

            if (result != Result.Success)
                throw new VulkanException("Failed to acquire swapchain image", result);
            
            return imageIndex;
        }

        public Result TryAcquireNextImage(Semaphore imageAvailableSemaphore, out uint imageIndex)
        {
            fixed (uint* imageIndexPtr = &imageIndex)
            {
                return _context.KhrSwapchain.AcquireNextImage(
                    _context.Device,
                    _swapchain,
                    ulong.MaxValue,
                    imageAvailableSemaphore,
                    default,
                    imageIndexPtr);
            }
        }
        
        /// <summary>
        /// Presents the current frame.
        /// </summary>
        public Result Present(PresentInfoKHR* presentInfo)
        {
            Result result = _context.KhrSwapchain.QueuePresent(_context.GraphicsQueue, presentInfo);

            if (result != Result.Success &&
                result != Result.SuboptimalKhr &&
                result != Result.ErrorOutOfDateKhr)
            {
                throw new VulkanException("Failed to present swapchain image", result);
            }

            return result;
        }
        
        /// <summary>
        /// Recreates the swapchain (e.g., on window resize).
        /// </summary>
        public void RecreateSwapchain(Action? waitIdle = null)
        {
            if (waitIdle != null)
                waitIdle();
            else
                _context.WaitIdle();

            SwapchainKHR oldSwapchain = _swapchain;
            ImageView[] oldImageViews = _imageViews;
            DestroyDepthResources();

            // Create new swapchain
            CreateSwapchain(oldSwapchain);
            CreateDepthResources();

            foreach (var view in oldImageViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }

            if (oldSwapchain.Handle != 0)
                _context.KhrSwapchain.DestroySwapchain(_context.Device, oldSwapchain, null);
        }

        private void DestroyDepthResources()
        {
            if (_depthImageView.Handle != 0)
            {
                _context.Api.DestroyImageView(_context.Device, _depthImageView, null);
                _depthImageView = default;
            }

            if (_depthAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _depthImage, _depthAllocation);
                _depthAllocation = null;
                _depthImage = default;
                _depthImageLayout = ImageLayout.Undefined;
            }
        }
        
        /// <summary>
        /// Gets the current layout of a swapchain image.
        /// </summary>
        public ImageLayout GetImageLayout(uint imageIndex)
        {
            return _imageLayouts[imageIndex];
        }
        
        /// <summary>
        /// Sets the layout of a swapchain image.
        /// </summary>
        public void SetImageLayout(uint imageIndex, ImageLayout layout)
        {
            _imageLayouts[imageIndex] = layout;
        }

        public void SetDepthImageLayout(ImageLayout layout)
        {
            _depthImageLayout = layout;
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            // Destroy swapchain before surface
            if (_swapchain.Handle != 0)
                _context.KhrSwapchain.DestroySwapchain(_context.Device, _swapchain, null);
            
            // Destroy image views
            foreach (var view in _imageViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }
            
            // Destroy depth resources
            if (_depthImageView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _depthImageView, null);
            
            if (_depthAllocation != null)
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _depthImage, _depthAllocation);
            
            // Destroy surface
            if (_surface.Handle != 0)
                _context.KhrSurface.DestroySurface(_context.Instance, _surface, null);
            
            System.Diagnostics.Debug.WriteLine("Swapchain manager disposed.");
        }
    }
}
