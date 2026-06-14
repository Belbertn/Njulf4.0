using System.Buffers.Binary;
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
    }
}
