using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class WebPTextureDecoderTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfWebPTextureDecoderTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void LosslessWebP_DecodesExactRgbaAndMetadata()
    {
        WebPDecodedImage decoded = WebPTextureDecoder.DecodeRgba8(
            WebPTestFixtures.Lossless,
            "lossless.webp");

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(3));
            Assert.That(decoded.Height, Is.EqualTo(2));
            Assert.That(decoded.IsLossless, Is.True);
            Assert.That(decoded.HasAlpha, Is.False);
            Assert.That(decoded.Rgba8, Is.EqualTo(WebPTestFixtures.LosslessPixels));
        });
    }

    [Test]
    public void LossyWebP_DecodesOpaqueRepresentativeImageWithinAuthoredErrorBound()
    {
        WebPDecodedImage decoded = WebPTextureDecoder.DecodeRgba8(
            WebPTestFixtures.Lossy,
            "lossy.webp");

        int maximumRgbError = 0;
        for (int offset = 0; offset < decoded.Rgba8.Length; offset += 4)
        {
            maximumRgbError = Math.Max(
                maximumRgbError,
                Math.Abs(decoded.Rgba8[offset] - WebPTestFixtures.LossySourcePixels[offset]));
            maximumRgbError = Math.Max(
                maximumRgbError,
                Math.Abs(decoded.Rgba8[offset + 1] - WebPTestFixtures.LossySourcePixels[offset + 1]));
            maximumRgbError = Math.Max(
                maximumRgbError,
                Math.Abs(decoded.Rgba8[offset + 2] - WebPTestFixtures.LossySourcePixels[offset + 2]));
            Assert.That(decoded.Rgba8[offset + 3], Is.EqualTo(byte.MaxValue));
        }

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Width, Is.EqualTo(3));
            Assert.That(decoded.Height, Is.EqualTo(2));
            Assert.That(decoded.IsLossless, Is.False);
            Assert.That(decoded.HasAlpha, Is.False);
            Assert.That(maximumRgbError, Is.LessThanOrEqualTo(100));
        });
    }

    [Test]
    public void AlphaWebP_PreservesAlphaAndEveryVisibleRgbChannel()
    {
        WebPDecodedImage decoded = WebPTextureDecoder.DecodeRgba8(
            WebPTestFixtures.Alpha,
            "alpha.webp");
        TextureTransportSourceAnalysis analysis = TextureCooker.AnalyzeTransportSource(
            WebPTestFixtures.Alpha,
            TextureContainerKind.WebP,
            "alpha.webp",
            new TextureCookOptions(
                ColorSpace: TextureColorSpace.Linear,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8));
        for (int offset = 0; offset < decoded.Rgba8.Length; offset += 4)
        {
            byte expectedAlpha = WebPTestFixtures.AlphaPixels[offset + 3];
            Assert.That(decoded.Rgba8[offset + 3], Is.EqualTo(expectedAlpha));
            if (expectedAlpha == 0)
                continue;

            Assert.That(
                decoded.Rgba8.AsSpan(offset, 3).ToArray(),
                Is.EqualTo(WebPTestFixtures.AlphaPixels.AsSpan(offset, 3).ToArray()));
        }

        Assert.Multiple(() =>
        {
            Assert.That(decoded.HasAlpha, Is.True);
            Assert.That(decoded.IsLossless, Is.True);
            Assert.That(analysis.IsSampleable, Is.True);
            Assert.That(analysis.Statistics.Decoder, Is.EqualTo(TextureTransportStatistics.WebPDecoderVersion));
            Assert.That(analysis.Statistics.LinearChannelMean.X, Is.EqualTo(770.0 / (6.0 * 255.0)).Within(1e-12));
            Assert.That(analysis.Statistics.LinearChannelMean.W, Is.EqualTo(648.0 / (6.0 * 255.0)).Within(1e-12));
            Assert.That(analysis.Statistics.GetAlphaCoverage(0.5), Is.EqualTo(0.5));
            Assert.That(analysis.Statistics.GetAlphaCoverage(1.0), Is.EqualTo(1.0 / 6.0));
        });
    }

    [TestCase(WebPTestFixtures.LosslessBase64, false)]
    [TestCase(WebPTestFixtures.LossyBase64, false)]
    [TestCase(WebPTestFixtures.AlphaBase64, true)]
    public void Cooker_ProducesDeterministicKtx2WithAuthoritativeSourceStatistics(
        string fixtureBase64,
        bool expectAlpha)
    {
        byte[] encoded = Convert.FromBase64String(fixtureBase64);
        var source = new ModelTextureSource
        {
            DebugName = "fixture.webp",
            SourceKind = TextureSourceKind.EmbeddedMemory,
            Bytes = encoded,
            MimeType = "image/webp",
            ContainerKind = TextureContainerKind.WebP,
            EncodedByteLength = encoded.Length,
            CacheIdentity = "memory:webp-fixture"
        };
        string firstPath = Path.Combine(_directory, "first.ktx2");
        string secondPath = Path.Combine(_directory, "second.ktx2");
        var options = new TextureCookOptions(
            MaxDimension: 8,
            ColorSpace: TextureColorSpace.Srgb,
            TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
            PreserveAlphaCoverage: expectAlpha,
            AlphaCutoff: 0.5f);

        CookedTextureReport first = new TextureCooker().Cook(source, firstPath, options);
        CookedTextureReport second = new TextureCooker().Cook(source, secondPath, options);

        Assert.Multiple(() =>
        {
            Assert.That(first.OriginalWidth, Is.EqualTo(3));
            Assert.That(first.OriginalHeight, Is.EqualTo(2));
            Assert.That(first.TransportStatistics.IsValid, Is.True);
            Assert.That(
                first.TransportStatistics.Decoder,
                Is.EqualTo(TextureTransportStatistics.WebPDecoderVersion));
            Assert.That(first.AlphaCoveragePreserved, Is.EqualTo(expectAlpha));
            Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(File.ReadAllBytes(secondPath)));
            Assert.That(
                second.TransportStatistics.AlphaHistogram,
                Is.EqualTo(first.TransportStatistics.AlphaHistogram));
            Assert.That(
                second.TransportStatistics with
                {
                    AlphaHistogram = first.TransportStatistics.AlphaHistogram
                },
                Is.EqualTo(first.TransportStatistics));
        });
    }

    [Test]
    public void RuntimeBudgetInspection_UsesTheBoundedWebPPath()
    {
        byte[] encoded = WebPTestFixtures.Alpha;
        string path = Path.Combine(_directory, "runtime-alpha.webp");
        File.WriteAllBytes(path, encoded);
        var source = new ModelTextureSource
        {
            DebugName = "runtime-alpha.webp",
            SourceKind = TextureSourceKind.ExternalFile,
            FilePath = path,
            MimeType = "image/webp",
            ContainerKind = TextureContainerKind.WebP,
            EncodedByteLength = encoded.Length,
            CacheIdentity = Path.GetFullPath(path)
        };

        TextureAssetMemoryEntry budget = TextureManager.InspectTextureSourceBudget(
            source,
            generateMipmaps: false,
            srgb: true);

        Assert.Multiple(() =>
        {
            Assert.That(budget.OriginalWidth, Is.EqualTo(3u));
            Assert.That(budget.OriginalHeight, Is.EqualTo(2u));
            Assert.That(budget.Width, Is.EqualTo(3u));
            Assert.That(budget.Height, Is.EqualTo(2u));
            Assert.That(budget.MipLevels, Is.EqualTo(1u));
            Assert.That(budget.EstimatedBytes, Is.EqualTo(24uL));
            Assert.That(budget.Format, Is.EqualTo("R8G8B8A8Srgb"));
        });
    }

    [Test]
    public void BoundedFileRead_RejectsOversizedDeclaredWebPBeforeReadingPayload()
    {
        string path = Path.Combine(_directory, "oversized.webp");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength(WebPTextureDecoder.DefaultMaximumEncodedBytes + 1L);
        }
        var source = new ModelTextureSource
        {
            DebugName = Path.GetFileName(path),
            SourceKind = TextureSourceKind.ExternalFile,
            FilePath = path,
            MimeType = "image/webp",
            ContainerKind = TextureContainerKind.WebP,
            EncodedByteLength = int.MaxValue,
            CacheIdentity = Path.GetFullPath(path)
        };

        NotSupportedException failure = Assert.Throws<NotSupportedException>(
            () => TextureManager.InspectTextureSourceBudget(
                source,
                generateMipmaps: false))!;

        Assert.Multiple(() =>
        {
            Assert.That(failure.Message, Does.Contain("encoded bytes"));
            Assert.That(
                failure.Message,
                Does.Contain(
                    WebPTextureDecoder.DefaultMaximumEncodedBytes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
        });
    }

    [Test]
    public void DecodeLimits_RejectBeforeOutputAllocation()
    {
        byte[] encoded = WebPTestFixtures.Lossless;

        NotSupportedException encodedFailure = Assert.Throws<NotSupportedException>(
            () => WebPTextureDecoder.DecodeRgba8(
                encoded,
                "encoded-limit.webp",
                maximumEncodedBytes: encoded.Length - 1,
                maximumPixels: 6))!;
        NotSupportedException pixelFailure = Assert.Throws<NotSupportedException>(
            () => WebPTextureDecoder.DecodeRgba8(
                encoded,
                "pixel-limit.webp",
                maximumEncodedBytes: encoded.Length,
                maximumPixels: 5))!;

        Assert.Multiple(() =>
        {
            Assert.That(encodedFailure.Message, Does.Contain("encoded bytes"));
            Assert.That(pixelFailure.Message, Does.Contain("decoded pixels"));
        });
    }

    [Test]
    public void MalformedOrAnimatedContainers_FailClosed()
    {
        byte[] trailing = [.. WebPTestFixtures.Lossless, 0];
        byte[] animation = CreateAnimationHeader();
        byte[] duplicatePayload = CreateDuplicateImagePayload();

        InvalidDataException trailingFailure = Assert.Throws<InvalidDataException>(
            () => WebPTextureDecoder.DecodeRgba8(trailing, "trailing.webp"))!;
        NotSupportedException animationFailure = Assert.Throws<NotSupportedException>(
            () => WebPTextureDecoder.DecodeRgba8(animation, "animated.webp"))!;
        InvalidDataException duplicateFailure = Assert.Throws<InvalidDataException>(
            () => WebPTextureDecoder.DecodeRgba8(
                duplicatePayload,
                "duplicate-payload.webp"))!;

        Assert.Multiple(() =>
        {
            Assert.That(trailingFailure.Message, Does.Contain("trailing payloads"));
            Assert.That(animationFailure.Message, Does.Contain("Animated WebP"));
            Assert.That(duplicateFailure.Message, Does.Contain("exactly one"));
        });
    }

    [Test]
    public void DeclaredWebPWithoutSignature_DoesNotFallThroughToAnotherDecoder()
    {
        byte[] notWebP = [0x89, 0x50, 0x4e, 0x47];
        TextureTransportSourceAnalysis analysis = TextureCooker.AnalyzeTransportSource(
            notWebP,
            TextureContainerKind.WebP,
            "declared.webp",
            new TextureCookOptions(TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8));

        Assert.Multiple(() =>
        {
            Assert.That(analysis.IsSampleable, Is.False);
            Assert.That(
                analysis.Statistics.Status,
                Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(analysis.Statistics.Decoder, Is.EqualTo(TextureTransportStatistics.WebPDecoderVersion));
            Assert.That(analysis.Statistics.InvalidReason, Does.Contain("RIFF/WEBP signature"));
        });
    }

    private static byte[] CreateAnimationHeader()
    {
        var result = new byte[30];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 22);
        "WEBP"u8.CopyTo(result.AsSpan(8));
        "VP8X"u8.CopyTo(result.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 10);
        result[20] = 0x02;
        return result;
    }

    private static byte[] CreateDuplicateImagePayload()
    {
        ReadOnlySpan<byte> fixture = WebPTestFixtures.Lossless;
        ReadOnlySpan<byte> imageChunk = fixture[12..];
        byte[] result = new byte[12 + imageChunk.Length * 2];
        fixture[..12].CopyTo(result);
        imageChunk.CopyTo(result.AsSpan(12));
        imageChunk.CopyTo(result.AsSpan(12 + imageChunk.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            result.AsSpan(4, 4),
            checked((uint)(result.Length - 8)));
        return result;
    }
}
