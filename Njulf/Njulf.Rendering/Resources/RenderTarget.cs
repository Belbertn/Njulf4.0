using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class RenderTarget : IDisposable
    {
        private Image _image;
        private ImageView _view;
        private bool _disposed;

        private RenderTarget(
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Render target name is required.", nameof(name)) : name;
            Format = format;
            Descriptor = descriptor;
            if (extent.Width == 0 || extent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(extent), "Render target extent must be non-zero.");
            Extent = extent;
        }

        internal static RenderTarget CreateGraphFacade(
            VulkanContext context,
            string name,
            Format format,
            Extent2D extent,
            RenderTargetDescriptor descriptor)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            return new RenderTarget(name, format, extent, descriptor);
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
        internal void BindGraphImage(RenderGraphAllocatedImage image)
        {
            if (!string.Equals(Name, image.Name, StringComparison.Ordinal))
                throw new InvalidOperationException($"Cannot bind graph image '{image.Name}' to render target facade '{Name}'.");
            if (Format != image.Format)
                throw new InvalidOperationException($"Graph image '{image.Name}' format {image.Format} does not match render target facade format {Format}.");

            _image = image.Image;
            _view = image.View;
            Extent = image.Extent;
            Layout = ImageLayout.Undefined;
        }

        internal void SetKnownLayout(ImageLayout layout)
        {
            Layout = layout;
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
                Format.D16Unorm => 2,
                Format.D24UnormS8Uint => 4,
                Format.D32Sfloat => 4,
                Format.D32SfloatS8Uint => 5,
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
            _image = default;
            _view = default;
            Layout = ImageLayout.Undefined;
            GC.SuppressFinalize(this);
        }
    }
}
