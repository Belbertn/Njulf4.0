using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    internal sealed class Ktx2Texture
    {
        private static readonly byte[] Identifier =
        [
            0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
        ];

        private Ktx2Texture(
            ReadOnlyMemory<byte> bytes,
            Format format,
            uint width,
            uint height,
            uint mipLevels,
            Ktx2MipLevel[] levels)
        {
            Bytes = bytes;
            Format = format;
            Width = width;
            Height = height;
            MipLevels = mipLevels;
            Levels = levels;
        }

        public ReadOnlyMemory<byte> Bytes { get; }
        public Format Format { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint MipLevels { get; }
        public IReadOnlyList<Ktx2MipLevel> Levels { get; }

        public static Ktx2Texture Parse(ReadOnlyMemory<byte> bytes, string sourceName)
        {
            ReadOnlySpan<byte> span = bytes.Span;
            if (span.Length < 80)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' is too small to contain a valid header.");

            if (!span[..Identifier.Length].SequenceEqual(Identifier))
                throw new InvalidDataException($"Texture '{sourceName}' is not a KTX2 file.");

            uint vkFormat = ReadUInt32(span, 12);
            uint typeSize = ReadUInt32(span, 16);
            uint pixelWidth = ReadUInt32(span, 20);
            uint pixelHeight = ReadUInt32(span, 24);
            uint pixelDepth = ReadUInt32(span, 28);
            uint layerCount = ReadUInt32(span, 32);
            uint faceCount = ReadUInt32(span, 36);
            uint levelCount = ReadUInt32(span, 40);
            uint supercompressionScheme = ReadUInt32(span, 44);

            if (pixelWidth == 0 || pixelHeight == 0)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' has invalid 2D dimensions.");
            if (pixelDepth > 0)
                throw new NotSupportedException($"KTX2 texture '{sourceName}' is 3D; only 2D textures are supported.");
            if (layerCount > 1)
                throw new NotSupportedException($"KTX2 texture '{sourceName}' is an array texture; only single-layer material textures are supported.");
            if (faceCount != 1)
                throw new NotSupportedException($"KTX2 texture '{sourceName}' is a cubemap; only 2D material textures are supported.");
            if (levelCount == 0)
                levelCount = 1;
            if (vkFormat == 0 || supercompressionScheme != 0)
            {
                throw new NotSupportedException(
                    $"KTX2 texture '{sourceName}' requires Basis/UASTC transcoding or decompression. " +
                    "Install a native KTX2/Basis transcoder before loading supercompressed KTX2 assets.");
            }
            if (typeSize == 0)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' has invalid typeSize 0.");

            Format format = (Format)vkFormat;
            if (!Enum.IsDefined(typeof(Format), format))
                throw new NotSupportedException($"KTX2 texture '{sourceName}' uses unsupported Vulkan format value {vkFormat}.");

            int levelIndexOffset = 80;
            int levelIndexSize = checked((int)levelCount * 24);
            if (span.Length < levelIndexOffset + levelIndexSize)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' has a truncated level index.");

            var levels = new Ktx2MipLevel[levelCount];
            for (int i = 0; i < levels.Length; i++)
            {
                int entryOffset = levelIndexOffset + (i * 24);
                ulong byteOffset = ReadUInt64(span, entryOffset);
                ulong byteLength = ReadUInt64(span, entryOffset + 8);
                ulong uncompressedByteLength = ReadUInt64(span, entryOffset + 16);
                if (byteLength == 0)
                    throw new InvalidDataException($"KTX2 texture '{sourceName}' has an empty mip level {i}.");
                if (byteOffset > int.MaxValue || byteLength > int.MaxValue || byteOffset + byteLength > (ulong)span.Length)
                    throw new InvalidDataException($"KTX2 texture '{sourceName}' has an out-of-bounds mip level {i}.");

                uint mipWidth = Math.Max(1u, pixelWidth >> i);
                uint mipHeight = Math.Max(1u, pixelHeight >> i);
                levels[i] = new Ktx2MipLevel(
                    checked((long)byteOffset),
                    checked((long)byteLength),
                    checked((long)uncompressedByteLength),
                    mipWidth,
                    mipHeight);
            }

            return new Ktx2Texture(bytes, format, pixelWidth, pixelHeight, levelCount, levels);
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, sizeof(uint)));
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> span, int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, sizeof(ulong)));
        }
    }

    internal readonly record struct Ktx2MipLevel(
        long ByteOffset,
        long ByteLength,
        long UncompressedByteLength,
        uint Width,
        uint Height);
}
