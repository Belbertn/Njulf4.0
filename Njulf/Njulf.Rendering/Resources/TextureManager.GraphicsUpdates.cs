using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Graphics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

public sealed unsafe partial class TextureManager
{
    internal static Format NativeFormat(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => Format.R8Unorm, TextureFormat.Rg8Unorm => Format.R8G8Unorm,
        TextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm, TextureFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
        TextureFormat.Bgra8Unorm => Format.B8G8R8A8Unorm, TextureFormat.Bgra8Srgb => Format.B8G8R8A8Srgb,
        TextureFormat.Rgba16Float => Format.R16G16B16A16Sfloat, TextureFormat.R32Float => Format.R32Sfloat,
        _ => throw new NotSupportedException($"Texture format {format} is not supported by resource transfers.")
    };

    internal static int PixelSize(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => 1, TextureFormat.Rg8Unorm => 2, TextureFormat.Rgba16Float => 8,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Unorm or TextureFormat.Bgra8Srgb
            or TextureFormat.R32Float => 4,
        _ => throw new NotSupportedException($"Texture format {format} is not supported by resource transfers.")
    };

    internal Texture2DDescription GetGraphicsDescription(TextureHandle handle)
    {
        lock (_lock)
        {
            var t = GetTextureInfoLocked(handle);
            var format = TextureFormat.Unknown;
            foreach (TextureFormat candidate in Enum.GetValues<TextureFormat>())
                if (candidate != TextureFormat.Unknown && NativeFormat(candidate) == t.Format)
                {
                    format = candidate;
                    break;
                }

            return new(checked((int)t.Extent.Width), checked((int)t.Extent.Height), format, checked((int)t.MipLevels));
        }
    }

    internal void ValidateGraphicsFormat(TextureFormat format, bool generateMips)
    {
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, NativeFormat(format),
            out var properties);
        var required = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.TransferSrcBit |
                       FormatFeatureFlags.TransferDstBit;
        if (generateMips)
            required |= FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit |
                        FormatFeatureFlags.SampledImageFilterLinearBit;
        if ((properties.OptimalTilingFeatures & required) != required)
            throw new NotSupportedException(
                $"The device does not support {format} with the requested transfer/mipmap operations.");
    }

    internal void RecordGraphicsUpdate(CommandBuffer cmd, TextureHandle handle, int mip, TextureRectangle region,
        BufferHandle staging)
    {
        var image = GetGraphImage(handle);
        image.TransitionToLayout(cmd, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit, force: true);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new(ImageAspectFlags.ColorBit, (uint)mip, 0, 1),
            ImageOffset = new(region.X, region.Y, 0), ImageExtent = new((uint)region.Width, (uint)region.Height, 1)
        };
        _context.Api.CmdCopyBufferToImage(cmd, _bufferManager.GetBuffer(staging), image.Image.Image,
            ImageLayout.TransferDstOptimal, 1, &copy);
        image.TransitionToLayout(cmd, ImageLayout.ShaderReadOnlyOptimal, PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.ShaderReadBit);
    }

    internal void RecordGraphicsMipmaps(CommandBuffer cmd, TextureHandle handle)
    {
        var image = GetGraphImage(handle);
        image.TransitionToLayout(cmd, ImageLayout.TransferDstOptimal, PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit, force: true);
        lock (_lock)
        {
            var info = GetTextureInfoLocked(handle);
            RecordMipGeneration(cmd, info, info.Extent.Width, info.Extent.Height);
        }

        image.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    internal void RecordGraphicsReadback(CommandBuffer cmd, TextureHandle handle, int mip, BufferHandle destination)
    {
        var image = GetGraphImage(handle);
        ImageLayout previous = image.Layout;
        if (previous == ImageLayout.Undefined)
            throw new InvalidOperationException("Cannot read an uninitialized texture.");
        var d = GetGraphicsDescription(handle);
        image.TransitionToLayout(cmd, ImageLayout.TransferSrcOptimal, PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit, force: true);
        var region = new BufferImageCopy
        {
            ImageSubresource = new(ImageAspectFlags.ColorBit, (uint)mip, 0, 1),
            ImageExtent = new((uint)Math.Max(1, d.Width >> mip), (uint)Math.Max(1, d.Height >> mip), 1)
        };
        _context.Api.CmdCopyImageToBuffer(cmd, image.Image.Image, ImageLayout.TransferSrcOptimal,
            _bufferManager.GetBuffer(destination), 1, &region);
        image.TransitionToLayout(cmd, previous, PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
    }

    // Attached to the physical image so aliases share the shadow and it dies with image storage.
    private sealed class GraphicsPixelShadow
    {
        public byte[] Bytes = [];
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SharedTextureImage, GraphicsPixelShadow>
        _graphicsPixels = new();

    internal void PublishGraphicsMipChange(TextureHandle handle)
    {
        TextureTransportStatistics? statistics;
        lock (_lock) statistics = GetTextureInfoLocked(handle).SharedImage!.TransportStatistics;
        if (statistics != null) PublishTextureTransportStatistics(handle, statistics);
    }

    internal void PublishGraphicsPixels(TextureHandle handle, Texture2DDescription d, TextureRectangle region,
        ReadOnlySpan<byte> bytes)
    {
        // Content-loaded images may not retain decoded source pixels. Invalidate their
        // old statistics instead of publishing invented values for untouched texels.
        bool missing;
        lock (_lock) missing = !_graphicsPixels.TryGetValue(GetTextureInfoLocked(handle).SharedImage!, out _);
        if (missing && region != new TextureRectangle(0, 0, d.Width, d.Height))
        {
            PublishTextureTransportStatistics(handle, TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.LegacyMissing,
                "Partial GPU texture update without retained source pixels.", CookedHash.Bytes(bytes),
                TextureSemantic.Data,
                d.Format is TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Srgb
                    ? TextureColorSpace.Srgb
                    : TextureColorSpace.Linear, "Graphics upload"));
            return;
        }

        byte[] full;
        lock (_lock)
        {
            var image = GetTextureInfoLocked(handle).SharedImage!;
            var shadow = _graphicsPixels.GetValue(image, _ => new());
            int stride = checked(d.Width * PixelSize(d.Format));
            if (shadow.Bytes.Length == 0)
            {
                shadow.Bytes = new byte[checked(stride * d.Height)];
            }

            int rowBytes = checked(region.Width * PixelSize(d.Format));
            for (int y = 0; y < region.Height; y++)
                bytes.Slice(y * rowBytes, rowBytes)
                    .CopyTo(shadow.Bytes.AsSpan((region.Y + y) * stride + region.X * PixelSize(d.Format), rowBytes));
            full = shadow.Bytes;
            image.Srgb = d.Format is TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Srgb;
        }

        var rgba = new float[checked(d.Width * d.Height * 4)];
        int pixelSize = PixelSize(d.Format);
        for (int i = 0; i < d.Width * d.Height; i++)
        {
            var source = full.AsSpan(i * pixelSize, pixelSize);
            int target = i * 4;
            rgba[target + 3] = 1;
            if (d.Format == TextureFormat.Rgba16Float)
                for (int c = 0; c < 4; c++)
                    rgba[target + c] =
                        (float)BitConverter.UInt16BitsToHalf(
                            BinaryPrimitives.ReadUInt16LittleEndian(source[(c * 2)..]));
            else if (d.Format == TextureFormat.R32Float) rgba[target] = BinaryPrimitives.ReadSingleLittleEndian(source);
            else
            {
                int channels = Math.Min(pixelSize, 4);
                for (int c = 0; c < channels; c++)
                {
                    int sourceChannel = d.Format is TextureFormat.Bgra8Unorm or TextureFormat.Bgra8Srgb && c < 3
                        ? 2 - c
                        : c;
                    byte value = source[sourceChannel];
                    rgba[target + c] = d.Format is TextureFormat.Rgba8Srgb or TextureFormat.Bgra8Srgb && c < 3
                        ? (value / 255f <= .04045f
                            ? value / 255f / 12.92f
                            : MathF.Pow((value / 255f + .055f) / 1.055f, 2.4f))
                        : value / 255f;
                }
            }
        }

        var statistics = rgba.Any(v => !float.IsFinite(v))
            ? TextureTransportStatistics.Invalid(TextureTransportStatisticsStatus.InvalidData,
                "Non-finite raw texture data.", CookedHash.Bytes(full), TextureSemantic.Data, TextureColorSpace.Linear,
                "Graphics upload")
            : TextureTransportImage.FromRgbaFloat(rgba, d.Width, d.Height, TextureColorSpace.Linear,
                TextureSemantic.Data, CookedHash.Bytes(full), "Graphics upload").Statistics;
        PublishTextureTransportStatistics(handle, statistics);
    }
}