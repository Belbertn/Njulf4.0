using System;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Diagnostics
{
    public static class ImageByteEstimator
    {
        public static ulong EstimateBytes(Format format, Extent3D extent, uint mipLevels = 1, uint arrayLayers = 1, SampleCountFlags samples = SampleCountFlags.Count1Bit)
        {
            if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
                throw new ArgumentOutOfRangeException(nameof(extent));
            if (mipLevels == 0)
                throw new ArgumentOutOfRangeException(nameof(mipLevels));
            if (arrayLayers == 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayers));

            ulong bytesPerPixel = GetBytesPerPixel(format);
            ulong sampleCount = GetSampleCount(samples);
            ulong totalPixels = 0;
            uint width = extent.Width;
            uint height = extent.Height;
            uint depth = extent.Depth;

            for (uint mip = 0; mip < mipLevels; mip++)
            {
                totalPixels = checked(totalPixels + (ulong)width * height * depth);
                width = Math.Max(1u, width / 2u);
                height = Math.Max(1u, height / 2u);
                depth = Math.Max(1u, depth / 2u);
            }

            return checked(totalPixels * bytesPerPixel * arrayLayers * sampleCount);
        }

        public static ulong GetBytesPerPixel(Format format)
        {
            return format switch
            {
                Format.R8Unorm => 1,
                Format.R8G8Unorm => 2,
                Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => 4,
                Format.R16G16Sfloat => 4,
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32B32A32Sfloat => 16,
                Format.D16Unorm => 2,
                Format.D24UnormS8Uint => 4,
                Format.D32Sfloat => 4,
                Format.D32SfloatS8Uint => 8,
                _ => throw new NotSupportedException($"Format {format} does not have a known byte size.")
            };
        }

        private static ulong GetSampleCount(SampleCountFlags samples)
        {
            return samples switch
            {
                SampleCountFlags.Count1Bit => 1,
                SampleCountFlags.Count2Bit => 2,
                SampleCountFlags.Count4Bit => 4,
                SampleCountFlags.Count8Bit => 8,
                SampleCountFlags.Count16Bit => 16,
                SampleCountFlags.Count32Bit => 32,
                SampleCountFlags.Count64Bit => 64,
                _ => 1
            };
        }
    }
}
