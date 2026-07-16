using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class Ktx2TextureTests
    {
        [Test]
        public void Parse_UncompressedVkFormatKtx2_ReturnsMipMetadata()
        {
            byte[] bytes = CreateKtx2(Format.BC7UnormBlock, width: 8, height: 8, mipLengths: [64, 16]);

            Ktx2Texture texture = Ktx2Texture.Parse(bytes, "test.ktx2");

            Assert.Multiple(() =>
            {
                Assert.That(texture.Format, Is.EqualTo(Format.BC7UnormBlock));
                Assert.That(texture.Width, Is.EqualTo(8));
                Assert.That(texture.Height, Is.EqualTo(8));
                Assert.That(texture.MipLevels, Is.EqualTo(2));
                Assert.That(texture.Levels[0].Width, Is.EqualTo(8));
                Assert.That(texture.Levels[1].Width, Is.EqualTo(4));
                Assert.That(texture.Levels[0].ByteLength, Is.EqualTo(64));
                Assert.That(texture.Levels[1].ByteLength, Is.EqualTo(16));
            });
        }

        [Test]
        public void Parse_SupercompressedKtx2_ThrowsTranscoderMessage()
        {
            byte[] bytes = CreateKtx2((Format)0, width: 4, height: 4, mipLengths: [16], supercompressionScheme: 1);

            Assert.That(
                () => Ktx2Texture.Parse(bytes, "basis.ktx2"),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("transcoding"));
        }

        [Test]
        public void InspectTextureSourceBudget_MemoryPng_ReturnsDecodedBudget()
        {
            byte[] bytes = OnePixelPng();
            var source = new ModelTextureSource
            {
                Bytes = bytes,
                DebugName = "embedded.png",
                CacheIdentity = "memory#embedded-png",
                MimeType = "image/png",
                SourceKind = TextureSourceKind.GlbBinary,
                EncodedByteLength = bytes.Length
            };

            TextureAssetMemoryEntry entry = TextureManager.InspectTextureSourceBudget(
                source,
                generateMipmaps: false,
                srgb: true);

            Assert.Multiple(() =>
            {
                Assert.That(entry.SourcePath, Is.EqualTo("memory#embedded-png"));
                Assert.That(entry.SourceKind, Is.EqualTo(TextureSourceKind.GlbBinary.ToString()));
                Assert.That(entry.OriginalWidth, Is.EqualTo(1));
                Assert.That(entry.OriginalHeight, Is.EqualTo(1));
                Assert.That(entry.Width, Is.EqualTo(1));
                Assert.That(entry.Height, Is.EqualTo(1));
                Assert.That(entry.MipLevels, Is.EqualTo(1));
                Assert.That(entry.EstimatedBytes, Is.EqualTo(4));
                Assert.That(entry.EncodedByteLength, Is.EqualTo(bytes.Length));
                Assert.That(entry.Format, Is.EqualTo(Format.R8G8B8A8Srgb.ToString()));
                Assert.That(entry.IsCompressed, Is.False);
                Assert.That(entry.WasDownscaled, Is.False);
            });
        }

        [Test]
        public void InspectTextureSourceBudget_RepositoryJpeg_ReturnsPreUploadBudget()
        {
            string path = FindRepoFile("NjulfHelloGame", "Old", "textures", "vintage_video_camera_diff_2k.jpg");
            var source = new ModelTextureSource
            {
                FilePath = path,
                DebugName = Path.GetFileName(path),
                SourceKind = TextureSourceKind.ExternalFile,
                EncodedByteLength = checked((int)new FileInfo(path).Length)
            };

            TextureAssetMemoryEntry entry = TextureManager.InspectTextureSourceBudget(
                source,
                generateMipmaps: true,
                srgb: true,
                maxDimension: 1024);

            Assert.Multiple(() =>
            {
                Assert.That(entry.SourcePath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(entry.SourceKind, Is.EqualTo(TextureSourceKind.ExternalFile.ToString()));
                Assert.That(entry.OriginalWidth, Is.GreaterThanOrEqualTo(entry.Width));
                Assert.That(entry.OriginalHeight, Is.GreaterThanOrEqualTo(entry.Height));
                Assert.That(Math.Max(entry.Width, entry.Height), Is.LessThanOrEqualTo(1024));
                Assert.That(entry.MipLevels, Is.GreaterThan(1));
                Assert.That(entry.EstimatedBytes, Is.GreaterThan(0));
                Assert.That(entry.EncodedByteLength, Is.EqualTo((int)new FileInfo(path).Length));
                Assert.That(entry.Format, Is.EqualTo(Format.R8G8B8A8Srgb.ToString()));
                Assert.That(entry.IsCompressed, Is.False);
                Assert.That(entry.WasDownscaled, Is.EqualTo(Math.Max(entry.OriginalWidth, entry.OriginalHeight) > 1024));
            });
        }

        [Test]
        public void InspectTextureSourceBudget_CompressedKtx2_ReturnsCompressedBudget()
        {
            byte[] bytes = CreateKtx2(Format.BC7UnormBlock, width: 8, height: 8, mipLengths: [64, 16]);
            var source = new ModelTextureSource
            {
                Bytes = bytes,
                DebugName = "compressed.ktx2",
                CacheIdentity = "memory#compressed-ktx2",
                ContainerKind = TextureContainerKind.Ktx2,
                SourceKind = TextureSourceKind.EmbeddedMemory,
                EncodedByteLength = bytes.Length
            };

            TextureAssetMemoryEntry entry = TextureManager.InspectTextureSourceBudget(source);

            Assert.Multiple(() =>
            {
                Assert.That(entry.SourcePath, Is.EqualTo("memory#compressed-ktx2"));
                Assert.That(entry.SourceKind, Is.EqualTo(TextureSourceKind.EmbeddedMemory.ToString()));
                Assert.That(entry.Width, Is.EqualTo(8));
                Assert.That(entry.Height, Is.EqualTo(8));
                Assert.That(entry.MipLevels, Is.EqualTo(2));
                Assert.That(entry.EstimatedBytes, Is.EqualTo(80));
                Assert.That(entry.EncodedByteLength, Is.EqualTo(bytes.Length));
                Assert.That(entry.Format, Is.EqualTo(Format.BC7UnormBlock.ToString()));
                Assert.That(entry.IsCompressed, Is.True);
                Assert.That(entry.WasDownscaled, Is.False);
            });
        }

        [Test]
        public void InspectTextureSourceBudget_SupercompressedKtx2_RejectsUntilTranscoderExists()
        {
            byte[] bytes = CreateKtx2((Format)0, width: 4, height: 4, mipLengths: [16], supercompressionScheme: 1);
            var source = new ModelTextureSource
            {
                Bytes = bytes,
                DebugName = "basis.ktx2",
                ContainerKind = TextureContainerKind.Ktx2,
                SourceKind = TextureSourceKind.EmbeddedMemory,
                EncodedByteLength = bytes.Length
            };

            Assert.That(
                () => TextureManager.InspectTextureSourceBudget(source),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("transcoding"));
        }

        private static byte[] CreateKtx2(
            Format format,
            uint width,
            uint height,
            int[] mipLengths,
            uint supercompressionScheme = 0)
        {
            int levelIndexSize = mipLengths.Length * 24;
            int dataOffset = 80 + levelIndexSize;
            int totalLength = dataOffset + mipLengths.Sum();
            byte[] bytes = new byte[totalLength];
            byte[] identifier =
            [
                0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A
            ];
            identifier.CopyTo(bytes, 0);

            WriteUInt32(bytes, 12, (uint)format);
            WriteUInt32(bytes, 16, 1);
            WriteUInt32(bytes, 20, width);
            WriteUInt32(bytes, 24, height);
            WriteUInt32(bytes, 36, 1);
            WriteUInt32(bytes, 40, (uint)mipLengths.Length);
            WriteUInt32(bytes, 44, supercompressionScheme);

            int offset = dataOffset;
            for (int i = 0; i < mipLengths.Length; i++)
            {
                int levelOffset = 80 + (i * 24);
                WriteUInt64(bytes, levelOffset, (ulong)offset);
                WriteUInt64(bytes, levelOffset + 8, (ulong)mipLengths[i]);
                WriteUInt64(bytes, levelOffset + 16, (ulong)mipLengths[i]);
                for (int j = 0; j < mipLengths[i]; j++)
                    bytes[offset + j] = (byte)(j + 1);
                offset += mipLengths[i];
            }

            return bytes;
        }

        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);
        }

        private static void WriteUInt64(byte[] bytes, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)), value);
        }

        private static byte[] OnePixelPng()
        {
            return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        }

        private static string FindRepoFile(params string[] relativeParts)
        {
            string? directory = TestContext.CurrentContext.WorkDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate))
                    return candidate;

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException("Could not locate repository test asset.", Path.Combine(relativeParts));
        }
    }
}
