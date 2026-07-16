using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Njulf.Rendering.Core;
using Njulf.Rendering.Debug;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class ScreenshotReadbackTests
{
    [Test]
    public void CapabilityAndFormatGate_AcceptsExplicitBgraAndRgbaFormats()
    {
        SwapchainScreenshotCapability transferCapability = SwapchainScreenshotCapability.Evaluate(
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit);
        ScreenshotReadbackFormatSupport bgra = ScreenshotReadbackFormatSupport.Evaluate(
            ScreenshotColorSpace.FinalLdrSrgb,
            Format.B8G8R8A8Srgb,
            transferCapability.TransferSourceSupported,
            transferCapability.Reason);
        ScreenshotReadbackFormatSupport rgba = ScreenshotReadbackFormatSupport.Evaluate(
            ScreenshotColorSpace.FinalLdrSrgb,
            Format.R8G8B8A8Unorm,
            transferCapability.TransferSourceSupported,
            transferCapability.Reason);

        Assert.Multiple(() =>
        {
            Assert.That(transferCapability.TransferSourceSupported, Is.True);
            Assert.That(bgra.Supported, Is.True);
            Assert.That(bgra.PixelFormat, Is.EqualTo(ScreenshotPixelFormat.Bgra8));
            Assert.That(rgba.Supported, Is.True);
            Assert.That(rgba.PixelFormat, Is.EqualTo(ScreenshotPixelFormat.Rgba8));
        });
    }

    [Test]
    public void CapabilityAndFormatGate_RejectsUnsupportedTransferAndFormat()
    {
        SwapchainScreenshotCapability unsupportedTransfer = SwapchainScreenshotCapability.Evaluate(
            ImageUsageFlags.ColorAttachmentBit);
        ScreenshotReadbackFormatSupport noTransfer = ScreenshotReadbackFormatSupport.Evaluate(
            ScreenshotColorSpace.FinalLdrSrgb,
            Format.B8G8R8A8Unorm,
            unsupportedTransfer.TransferSourceSupported,
            unsupportedTransfer.Reason);
        ScreenshotReadbackFormatSupport unsupportedFormat = ScreenshotReadbackFormatSupport.Evaluate(
            ScreenshotColorSpace.FinalLdrSrgb,
            Format.R16G16B16A16Sfloat,
            transferSourceSupported: true,
            transferSourceReason: "supported");

        Assert.Multiple(() =>
        {
            Assert.That(unsupportedTransfer.TransferSourceSupported, Is.False);
            Assert.That(noTransfer.Supported, Is.False);
            Assert.That(noTransfer.Reason, Does.Contain("TransferSrc"));
            Assert.That(unsupportedFormat.Supported, Is.False);
            Assert.That(unsupportedFormat.Reason, Does.Contain("B8G8R8A8"));
        });
    }

    [Test]
    public void PngEncoder_ConvertsBgraToRgbaAndMarksSrgb()
    {
        byte[] bgra =
        [
            3, 2, 1, 255,
            30, 20, 10, 40
        ];

        byte[] png = PngScreenshotEncoder.Encode(bgra, width: 2, height: 1, ScreenshotPixelFormat.Bgra8);
        IReadOnlyDictionary<string, byte[]> chunks = ReadChunks(png);
        byte[] scanlines = Inflate(chunks["IDAT"]);

        Assert.Multiple(() =>
        {
            Assert.That(png.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.That(chunks["IHDR"].Length, Is.EqualTo(13));
            Assert.That(chunks["sRGB"], Is.EqualTo(new byte[] { 0 }));
            Assert.That(scanlines, Is.EqualTo(new byte[]
            {
                0,
                1, 2, 3, 255,
                10, 20, 30, 40
            }));
            Assert.That(chunks.ContainsKey("IEND"), Is.True);
        });
    }

    [Test]
    public void PngEncoder_AtomicWriteReplacesTargetWithoutTemporaryArtifact()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshot-readback-tests", Guid.NewGuid().ToString("N"));
        string target = Path.Combine(directory, "capture.png");
        Directory.CreateDirectory(directory);
        File.WriteAllText(target, "old capture");

        try
        {
            PngScreenshotEncoder.WriteAtomic(
                target,
                new byte[] { 12, 34, 56, 255 },
                width: 1,
                height: 1,
                ScreenshotPixelFormat.Rgba8);

            byte[] completed = File.ReadAllBytes(target);
            Assert.Multiple(() =>
            {
                Assert.That(completed.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
                Assert.That(Directory.GetFiles(directory, ".capture.png.*.tmp"), Is.Empty);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ReadChunks(ReadOnlySpan<byte> png)
    {
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset, 4));
            offset += 4;
            string type = Encoding.ASCII.GetString(png.Slice(offset, 4));
            offset += 4;
            chunks[type] = png.Slice(offset, length).ToArray();
            offset += length + 4; // payload plus CRC
        }

        return chunks;
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
}
