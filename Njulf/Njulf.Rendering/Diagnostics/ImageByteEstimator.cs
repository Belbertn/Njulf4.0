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

            ulong sampleCount = GetSampleCount(samples);
            ulong totalBytes = 0;
            uint width = extent.Width;
            uint height = extent.Height;
            uint depth = extent.Depth;

            for (uint mip = 0; mip < mipLevels; mip++)
            {
                totalBytes = checked(totalBytes + EstimateMipBytes(format, width, height, depth));
                width = Math.Max(1u, width / 2u);
                height = Math.Max(1u, height / 2u);
                depth = Math.Max(1u, depth / 2u);
            }

            return checked(totalBytes * arrayLayers * sampleCount);
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
                Format.R32Sfloat => 4,
                Format.R32G32B32A32Sfloat => 16,
                Format.D16Unorm => 2,
                Format.D24UnormS8Uint => 4,
                Format.D32Sfloat => 4,
                Format.D32SfloatS8Uint => 8,
                _ => throw new NotSupportedException($"Format {format} does not have a known byte size.")
            };
        }

        public static ulong EstimateMipBytes(Format format, uint width, uint height, uint depth = 1)
        {
            FormatBlockInfo block = GetBlockInfo(format);
            if (block.IsBlockCompressed)
            {
                ulong blockColumns = ((ulong)width + block.BlockWidth - 1UL) / block.BlockWidth;
                ulong blockRows = ((ulong)height + block.BlockHeight - 1UL) / block.BlockHeight;
                return checked(blockColumns * blockRows * depth * block.BytesPerBlock);
            }

            return checked((ulong)width * height * depth * block.BytesPerBlock);
        }

        public static FormatBlockInfo GetBlockInfo(Format format)
        {
            return format switch
            {
                Format.BC1RgbUnormBlock or Format.BC1RgbSrgbBlock or
                    Format.BC1RgbaUnormBlock or Format.BC1RgbaSrgbBlock or
                    Format.BC4UnormBlock or Format.BC4SNormBlock => new FormatBlockInfo(4, 4, 8),
                Format.BC2UnormBlock or Format.BC2SrgbBlock or
                    Format.BC3UnormBlock or Format.BC3SrgbBlock or
                    Format.BC5UnormBlock or Format.BC5SNormBlock or
                    Format.BC6HUfloatBlock or Format.BC6HSfloatBlock or
                    Format.BC7UnormBlock or Format.BC7SrgbBlock => new FormatBlockInfo(4, 4, 16),
                Format.Etc2R8G8B8UnormBlock or Format.Etc2R8G8B8SrgbBlock or
                    Format.Etc2R8G8B8A1UnormBlock or Format.Etc2R8G8B8A1SrgbBlock => new FormatBlockInfo(4, 4, 8),
                Format.Etc2R8G8B8A8UnormBlock or Format.Etc2R8G8B8A8SrgbBlock => new FormatBlockInfo(4, 4, 16),
                Format.Astc4x4UnormBlock or Format.Astc4x4SrgbBlock => new FormatBlockInfo(4, 4, 16),
                Format.Astc5x4UnormBlock or Format.Astc5x4SrgbBlock => new FormatBlockInfo(5, 4, 16),
                Format.Astc5x5UnormBlock or Format.Astc5x5SrgbBlock => new FormatBlockInfo(5, 5, 16),
                Format.Astc6x5UnormBlock or Format.Astc6x5SrgbBlock => new FormatBlockInfo(6, 5, 16),
                Format.Astc6x6UnormBlock or Format.Astc6x6SrgbBlock => new FormatBlockInfo(6, 6, 16),
                Format.Astc8x5UnormBlock or Format.Astc8x5SrgbBlock => new FormatBlockInfo(8, 5, 16),
                Format.Astc8x6UnormBlock or Format.Astc8x6SrgbBlock => new FormatBlockInfo(8, 6, 16),
                Format.Astc8x8UnormBlock or Format.Astc8x8SrgbBlock => new FormatBlockInfo(8, 8, 16),
                Format.Astc10x5UnormBlock or Format.Astc10x5SrgbBlock => new FormatBlockInfo(10, 5, 16),
                Format.Astc10x6UnormBlock or Format.Astc10x6SrgbBlock => new FormatBlockInfo(10, 6, 16),
                Format.Astc10x8UnormBlock or Format.Astc10x8SrgbBlock => new FormatBlockInfo(10, 8, 16),
                Format.Astc10x10UnormBlock or Format.Astc10x10SrgbBlock => new FormatBlockInfo(10, 10, 16),
                Format.Astc12x10UnormBlock or Format.Astc12x10SrgbBlock => new FormatBlockInfo(12, 10, 16),
                Format.Astc12x12UnormBlock or Format.Astc12x12SrgbBlock => new FormatBlockInfo(12, 12, 16),
                _ => new FormatBlockInfo(1, 1, GetBytesPerPixel(format))
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

    public readonly record struct FormatBlockInfo(uint BlockWidth, uint BlockHeight, ulong BytesPerBlock)
    {
        public bool IsBlockCompressed => BlockWidth > 1 || BlockHeight > 1;
    }
}
