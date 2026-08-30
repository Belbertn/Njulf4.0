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

    [Test]
    public void ContentAnalysis_RejectsBlackAndUniformBootstrapFrames()
    {
        byte[] black = new byte[8 * 8 * 4];
        byte[] uniform = new byte[8 * 8 * 4];
        for (int offset = 0; offset < uniform.Length; offset += 4)
        {
            uniform[offset] = 64;
            uniform[offset + 1] = 64;
            uniform[offset + 2] = 64;
            uniform[offset + 3] = 255;
        }

        ScreenshotContentAnalysis blackAnalysis =
            ScreenshotContentAnalysis.Analyze(
                black,
                8,
                8,
                ScreenshotPixelFormat.Rgba8);
        ScreenshotContentAnalysis uniformAnalysis =
            ScreenshotContentAnalysis.Analyze(
                uniform,
                8,
                8,
                ScreenshotPixelFormat.Rgba8);

        Assert.Multiple(() =>
        {
            Assert.That(blackAnalysis.HasVisibleContent, Is.False);
            Assert.That(blackAnalysis.VisiblePixelCount, Is.Zero);
            Assert.That(uniformAnalysis.HasVisibleContent, Is.False);
            Assert.That(
                uniformAnalysis.MaximumLuminance -
                uniformAnalysis.MinimumLuminance,
                Is.Zero);
        });
    }

    [Test]
    public void ContentAnalysis_AcceptsSpatiallyVariedRenderedPixelsInBgraOrder()
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int pixel = 0; pixel < 64; pixel++)
        {
            int offset = pixel * 4;
            pixels[offset] = pixel < 32 ? (byte)20 : (byte)220;
            pixels[offset + 1] = pixel < 32 ? (byte)40 : (byte)180;
            pixels[offset + 2] = pixel < 32 ? (byte)80 : (byte)140;
            pixels[offset + 3] = 255;
        }

        ScreenshotContentAnalysis analysis =
            ScreenshotContentAnalysis.Analyze(
                pixels,
                8,
                8,
                ScreenshotPixelFormat.Bgra8);

        Assert.Multiple(() =>
        {
            Assert.That(analysis.HasVisibleContent, Is.True);
            Assert.That(analysis.VisiblePixelCount, Is.EqualTo(64));
            Assert.That(
                analysis.MaximumLuminance - analysis.MinimumLuminance,
                Is.GreaterThanOrEqualTo(8));
        });
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
