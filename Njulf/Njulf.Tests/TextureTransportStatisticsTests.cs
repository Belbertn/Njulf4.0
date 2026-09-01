using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using NUnit.Framework;
using ZstdSharp;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class TextureTransportStatisticsTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "NjulfTextureStatisticsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Rgba8Statistics_PersistDoubleMomentsHistogramAndLuminance()
    {
        TextureTransportImage image = TextureTransportImage.FromRgba8(
            [
                0, 0, 0, 0,
                255, 128, 64, 255
            ],
            width: 2,
            height: 1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            sourceContentHash: 0x1234);
        TextureTransportStatistics statistics = image.Statistics;

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.True);
            Assert.That(statistics.SchemaVersion, Is.EqualTo(TextureTransportStatistics.CurrentSchemaVersion));
            Assert.That(statistics.AlgorithmVersion, Is.EqualTo(TextureTransportStatistics.CurrentAlgorithmVersion));
            Assert.That(statistics.SourceContentHash, Is.EqualTo(0x1234));
            Assert.That(statistics.PixelCount, Is.EqualTo(2));
            Assert.That(statistics.LinearChannelMean.X, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(statistics.LinearChannelSecondMoment.X, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(statistics.LinearChannelMean.Y, Is.EqualTo(64.0 / 255.0).Within(1e-12));
            Assert.That(statistics.LinearChannelSecondMoment.Y, Is.EqualTo(0.5 * Math.Pow(128.0 / 255.0, 2)).Within(1e-12));
            Assert.That(statistics.EmissiveLuminanceMean, Is.GreaterThan(0.0));
            Assert.That(statistics.AlphaHistogram, Has.Length.EqualTo(256));
            Assert.That(statistics.AlphaHistogram[0], Is.EqualTo(1));
            Assert.That(statistics.AlphaHistogram[255], Is.EqualTo(1));
            Assert.That(statistics.GetAlphaCoverage(0.5), Is.EqualTo(0.5));
            Assert.That(statistics.GetAlphaCoverage(1.1), Is.Zero);
            Assert.That(statistics.Validate(), Is.Empty);
        });
    }

    [Test]
    public void HdrStatistics_PreserveLegalEnergyAndRejectNonFinitePixelsExplicitly()
    {
        TextureTransportImage valid = TextureTransportImage.FromRgbaFloat(
            [
                10f, 2f, 0.5f, 1f,
                0f, 4f, 20f, 1f
            ],
            2,
            1,
            TextureColorSpace.HdrLinear,
            TextureSemantic.Hdr,
            77);
        TextureTransportImage invalid = TextureTransportImage.FromRgbaFloat(
            [float.NaN, 1f, 1f, 1f],
            1,
            1,
            TextureColorSpace.HdrLinear,
            TextureSemantic.Hdr,
            78);
        TextureTransportImage negative = TextureTransportImage.FromRgbaFloat(
            [-0.01f, 1f, 1f, 1f],
            1,
            1,
            TextureColorSpace.HdrLinear,
            TextureSemantic.Hdr,
            79);

        Assert.Multiple(() =>
        {
            Assert.That(valid.Statistics.IsValid, Is.True);
            Assert.That(valid.Statistics.LinearChannelMean.X, Is.EqualTo(5.0));
            Assert.That(valid.Statistics.LinearChannelMean.Z, Is.EqualTo(10.25));
            Assert.That(valid.Statistics.EmissiveLuminanceMaximum, Is.GreaterThan(1.0));
            Assert.That(invalid.Statistics.IsValid, Is.False);
            Assert.That(invalid.Statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(invalid.Statistics.InvalidReason, Does.Contain("non-finite"));
            Assert.That(negative.Statistics.IsValid, Is.False);
            Assert.That(
                negative.Statistics.Status,
                Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(
                negative.Statistics.InvalidReason,
                Does.Contain("outside the legal linear-HDR range"));
        });
    }

    [Test]
    public void StatisticsValidation_RejectsStaleVersionsAndMalformedDiscriminators()
    {
        TextureTransportStatistics valid = TextureTransportImage.FromRgba8(
            [255, 128, 64, 255],
            1,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            0x8877).Statistics;
        TextureTransportStatistics staleSchema = valid with
        {
            SchemaVersion =
                TextureTransportStatistics.CurrentSchemaVersion - 1
        };
        TextureTransportStatistics staleAlgorithm = valid with
        {
            AlgorithmVersion =
                TextureTransportStatistics.CurrentAlgorithmVersion - 1
        };
        TextureTransportStatistics unknownValidity = valid with
        {
            Validity = valid.Validity |
                (TextureTransportStatisticsValidity)(1u << 31)
        };
        TextureTransportStatistics unknownStatus = valid with
        {
            Status = (TextureTransportStatisticsStatus)99
        };
        TextureTransportStatistics nullHistogram = valid with
        {
            AlphaHistogram = null!
        };

        Assert.Multiple(() =>
        {
            Assert.That(staleSchema.IsValid, Is.False);
            Assert.That(staleSchema.HasLinearChannelMoments, Is.False);
            Assert.That(staleSchema.Validate(), Has.Some.Contains("schema"));
            Assert.That(
                () => staleSchema.GetAlphaCoverage(0.5),
                Throws.TypeOf<InvalidOperationException>());

            Assert.That(staleAlgorithm.IsValid, Is.False);
            Assert.That(staleAlgorithm.Validate(), Has.Some.Contains("algorithm"));
            Assert.That(unknownValidity.IsValid, Is.False);
            Assert.That(
                unknownValidity.Validate(),
                Has.Some.Contains("unknown bits"));
            Assert.That(
                unknownStatus.Validate(),
                Has.Some.Contains("Unknown texture-statistics status"));
            Assert.That(nullHistogram.IsValid, Is.False);
            Assert.That(
                nullHistogram.Validate(),
                Has.Some.Contains("cannot be null"));
        });
    }

    [Test]
    public void NormalStatistics_ComputeDirectionalVariance()
    {
        TextureTransportImage image = TextureTransportImage.FromRgbaFloat(
            [
                1f, 0.5f, 0.5f, 1f,
                0f, 0.5f, 0.5f, 1f
            ],
            2,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Normal,
            42);

        Assert.Multiple(() =>
        {
            Assert.That(image.Statistics.Validity.HasFlag(TextureTransportStatisticsValidity.NormalVariance), Is.True);
            Assert.That(image.Statistics.NormalVariance, Is.EqualTo(1.0).Within(1e-12));
        });
    }

    [Test]
    public void AlphaCoveragePreservation_MatchesReachableTarget()
    {
        byte[] mip =
        [
            0, 0, 0, 30,
            0, 0, 0, 60,
            0, 0, 0, 90,
            0, 0, 0, 120
        ];

        AlphaCoverageMipGenerator.PreserveCoverage(mip, cutoff: 0.5, targetCoverage: 0.5);

        Assert.That(AlphaCoverageMipGenerator.CalculateCoverage(mip, 0.5), Is.EqualTo(0.5));
    }

    [Test]
    public void TextureCooker_RejectsNegativeAlphaCutoffAndPreservesCutoffAboveOne()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        var source = new ModelTextureSource
        {
            Bytes = png,
            CacheIdentity = "alpha-cutoff-boundary",
            DebugName = "alpha-cutoff-boundary.png"
        };
        string invalidPath = Path.Combine(_directory, "invalid-alpha.ktx2");
        string validPath = Path.Combine(_directory, "above-one-alpha.ktx2");
        var cooker = new TextureCooker();

        Assert.That(
            () => cooker.Cook(
                source,
                invalidPath,
                new TextureCookOptions(
                    MaxDimension: 16,
                    ColorSpace: TextureColorSpace.Srgb,
                    TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                    Semantic: TextureSemantic.Color,
                    PreserveAlphaCoverage: true,
                    AlphaCutoff: -0.01f)),
            Throws.InstanceOf<ArgumentOutOfRangeException>());

        CookedTextureReport aboveOne = cooker.Cook(
            source,
            validPath,
            new TextureCookOptions(
                MaxDimension: 16,
                ColorSpace: TextureColorSpace.Srgb,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                Semantic: TextureSemantic.Color,
                PreserveAlphaCoverage: true,
                AlphaCutoff: 1.25f));

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(invalidPath), Is.False);
            Assert.That(aboveOne.AlphaCutoff, Is.EqualTo(1.25f));
            Assert.That(
                aboveOne.TransportStatistics.GetAlphaCoverage(aboveOne.AlphaCutoff),
                Is.Zero);
            Assert.That(File.Exists(validPath), Is.True);
        });
    }

    [TestCase(TextureTargetFormatPolicy.Rgba8, 1e-6)]
    [TestCase(TextureTargetFormatPolicy.Bc7, 0.02)]
    public void Ktx2PassThrough_AnalyzesRawAndBcLevelZero(
        TextureTargetFormatPolicy format,
        double tolerance)
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        string firstPath = Path.Combine(_directory, $"first-{format}.ktx2");
        var cooker = new TextureCooker();
        CookedTextureReport first = cooker.Cook(
            new ModelTextureSource { Bytes = png, CacheIdentity = "source", DebugName = "source.png" },
            firstPath,
            new TextureCookOptions(
                MaxDimension: 16,
                ColorSpace: TextureColorSpace.Srgb,
                TargetFormatPolicy: format,
                Semantic: TextureSemantic.Color));
        string secondPath = Path.Combine(_directory, $"second-{format}.ktx2");
        CookedTextureReport second = cooker.Cook(
            new ModelTextureSource
            {
                Bytes = File.ReadAllBytes(firstPath),
                CacheIdentity = "pass-through",
                DebugName = Path.GetFileName(firstPath),
                ContainerKind = TextureContainerKind.Ktx2
            },
            secondPath,
            new TextureCookOptions(
                MaxDimension: 16,
                ColorSpace: TextureColorSpace.Srgb,
                TargetFormatPolicy: format,
                Semantic: TextureSemantic.Color));

        Assert.Multiple(() =>
        {
            Assert.That(second.PassedThrough, Is.True);
            Assert.That(second.TransportStatistics.IsValid, Is.True);
            Assert.That(second.TransportStatistics.Decoder, Does.Contain(format == TextureTargetFormatPolicy.Bc7 ? "BCnEncoder" : "KTX2 raw"));
            Assert.That(
                second.TransportStatistics.LinearChannelMean.X,
                Is.EqualTo(first.TransportStatistics.LinearChannelMean.X).Within(tolerance));
            Assert.That(
                second.TransportStatistics.LinearChannelMean.W,
                Is.EqualTo(first.TransportStatistics.LinearChannelMean.W).Within(tolerance));
        });
    }

    [Test]
    public void TextureCooking_IsDeterministicIncludingTransportMetadata()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        var source = new ModelTextureSource { Bytes = png, CacheIdentity = "repeat", DebugName = "repeat.png" };
        var options = new TextureCookOptions(
            MaxDimension: 16,
            ColorSpace: TextureColorSpace.Srgb,
            TargetFormatPolicy: TextureTargetFormatPolicy.Bc7,
            Semantic: TextureSemantic.Color);
        string firstPath = Path.Combine(_directory, "deterministic-first.ktx2");
        string secondPath = Path.Combine(_directory, "deterministic-second.ktx2");

        CookedTextureReport first = new TextureCooker().Cook(source, firstPath, options);
        CookedTextureReport second = new TextureCooker().Cook(source, secondPath, options);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(File.ReadAllBytes(secondPath)));
            Assert.That(second.TransportStatistics.SourceContentHash, Is.EqualTo(first.TransportStatistics.SourceContentHash));
            Assert.That(second.TransportStatistics.LinearChannelMean, Is.EqualTo(first.TransportStatistics.LinearChannelMean));
            Assert.That(second.TransportStatistics.LinearChannelSecondMoment, Is.EqualTo(first.TransportStatistics.LinearChannelSecondMoment));
            Assert.That(second.TransportStatistics.AlphaHistogram, Is.EqualTo(first.TransportStatistics.AlphaHistogram));
        });
    }

    [Test]
    public void ZstdKtx2_CooksValidStatisticsAndNormalizesForRuntimeUpload()
    {
        byte[] sourcePixels =
        [
            255, 0, 0, 255,
            0, 255, 0, 128
        ];
        byte[] compressed = CompressZstd(sourcePixels);
        byte[] ktx = CreateKtx2(
            format: 37,
            supercompression: 2,
            level: compressed,
            uncompressedLength: (ulong)sourcePixels.Length,
            width: 2,
            height: 1);
        string path = Path.Combine(_directory, "zstd-normalized.ktx2");

        CookedTextureReport report = new TextureCooker().Cook(
            new ModelTextureSource
            {
                Bytes = ktx,
                CacheIdentity = "zstd",
                DebugName = "zstd.ktx2",
                ContainerKind = TextureContainerKind.Ktx2
            },
            path,
            new TextureCookOptions(ColorSpace: TextureColorSpace.Linear));
        byte[] normalizedKtx = File.ReadAllBytes(path);
        TextureTransportStatistics normalizedStatistics = TextureCooker.AnalyzeTransportStatistics(
            normalizedKtx,
            TextureContainerKind.Ktx2,
            "normalized.ktx2",
            new TextureCookOptions(ColorSpace: TextureColorSpace.Linear));

        Assert.Multiple(() =>
        {
            Assert.That(report.PassedThrough, Is.False);
            Assert.That(report.TransportStatistics.IsValid, Is.True);
            Assert.That(report.TransportStatistics.SourceContentHash, Is.EqualTo(CookedHash.Bytes(ktx)));
            Assert.That(report.TransportStatistics.Decoder, Does.Contain(TextureTransportStatistics.ZstdDecoderVersion));
            Assert.That(report.TransportStatistics.LinearChannelMean.X, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(report.TransportStatistics.LinearChannelMean.Y, Is.EqualTo(0.5).Within(1e-12));
            Assert.That(report.TransportStatistics.LinearChannelMean.W, Is.EqualTo((255.0 + 128.0) / (2.0 * 255.0)).Within(1e-12));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(normalizedKtx.AsSpan(44, 4)), Is.Zero);
            Assert.That(normalizedKtx, Is.Not.EqualTo(ktx));
            Assert.That(normalizedStatistics.IsValid, Is.True);
            Assert.That(normalizedStatistics.LinearChannelMean, Is.EqualTo(report.TransportStatistics.LinearChannelMean));
        });
    }

    [Test]
    public void ZlibKtx2_AnalyzesWithPinnedDecoderAndExactStatistics()
    {
        byte[] sourcePixels = [32, 64, 128, 255];
        byte[] ktx = CreateKtx2(
            format: 37,
            supercompression: 3,
            level: CompressZlib(sourcePixels),
            uncompressedLength: (ulong)sourcePixels.Length);

        TextureTransportStatistics statistics = TextureCooker.AnalyzeTransportStatistics(
            ktx,
            TextureContainerKind.Ktx2,
            "zlib.ktx2",
            new TextureCookOptions(ColorSpace: TextureColorSpace.Linear));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.True);
            Assert.That(statistics.Decoder, Does.Contain(TextureTransportStatistics.ZlibDecoderVersion));
            Assert.That(statistics.LinearChannelMean.X, Is.EqualTo(32.0 / 255.0).Within(1e-12));
            Assert.That(statistics.LinearChannelMean.Y, Is.EqualTo(64.0 / 255.0).Within(1e-12));
            Assert.That(statistics.LinearChannelMean.Z, Is.EqualTo(128.0 / 255.0).Within(1e-12));
        });
    }

    [Test]
    public void MalformedBasisKtx2_IsExplicitlyInvalidAndCookingFailsBeforeWriting()
    {
        byte[] ktx = CreateKtx2(
            format: 0,
            supercompression: 1,
            level: new byte[16],
            uncompressedLength: 0);
        string path = Path.Combine(_directory, "basis.ktx2");
        var source = new ModelTextureSource
        {
            Bytes = ktx,
            CacheIdentity = "basis",
            DebugName = "basis.ktx2",
            ContainerKind = TextureContainerKind.Ktx2
        };

        TextureTransportStatistics statistics =
            TextureCooker.AnalyzeTransportStatistics(source, new TextureCookOptions());
        InvalidDataException? error = Assert.Throws<InvalidDataException>(
            () => new TextureCooker().Cook(source, path, new TextureCookOptions()));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.False);
            Assert.That(statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(statistics.InvalidReason, Does.Contain("BasisLZ level-0 decoding failed"));
            Assert.That(statistics.Decoder, Is.EqualTo(TextureTransportStatistics.BasisDecoderVersion));
            Assert.That(error!.Message, Does.Contain("source-resolution transport statistics are invalid"));
            Assert.That(error.Message, Does.Contain("libktx"));
            Assert.That(File.Exists(path), Is.False);
        });
    }

    [Test]
    public void RealBasisLzKtx2_DecodesAuthoritativeStatisticsAndCooksDeterministically()
    {
        byte[] ktx = GetKhronosBasisLzFixture();
        var source = new ModelTextureSource
        {
            Bytes = ktx,
            CacheIdentity = "alpha-simple-basis",
            DebugName = "alpha_simple_blze.ktx2",
            ContainerKind = TextureContainerKind.Ktx2
        };
        var options = new TextureCookOptions(
            MaxDimension: 8,
            ColorSpace: TextureColorSpace.Linear,
            TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
            Semantic: TextureSemantic.Color,
            PreserveAlphaCoverage: true,
            AlphaCutoff: 0.5f);
        string firstPath = Path.Combine(_directory, "basis-first.ktx2");
        string secondPath = Path.Combine(_directory, "basis-second.ktx2");

        TextureTransportStatistics sourceStatistics =
            TextureCooker.AnalyzeTransportStatistics(source, options);
        if (!SupportsPinnedBasisTranscoder())
        {
            NotSupportedException? error = Assert.Throws<NotSupportedException>(
                () => new TextureCooker().Cook(source, firstPath, options));
            Assert.Multiple(() =>
            {
                Assert.That(sourceStatistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.UnsupportedEncoding));
                Assert.That(sourceStatistics.InvalidReason, Does.Contain("win-x64 and linux-x64"));
                Assert.That(error!.Message, Does.Contain("win-x64 and linux-x64"));
                Assert.That(File.Exists(firstPath), Is.False);
            });
            return;
        }

        CookedTextureReport first = new TextureCooker().Cook(source, firstPath, options);
        CookedTextureReport second = new TextureCooker().Cook(source, secondPath, options);
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        (int width, int height, int mipCount, uint format) =
            TextureCooker.Inspect(firstBytes, "basis-first.ktx2");
        TextureTransportStatistics cookedStatistics = TextureCooker.AnalyzeTransportStatistics(
            firstBytes,
            TextureContainerKind.Ktx2,
            "basis-first.ktx2",
            options);

        Assert.Multiple(() =>
        {
            Assert.That(sourceStatistics.IsValid, Is.True);
            Assert.That(sourceStatistics.Decoder, Is.EqualTo(TextureTransportStatistics.BasisDecoderVersion));
            Assert.That(sourceStatistics.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb),
                "The fixture DFD, rather than the caller fallback, declares the transfer function.");
            Assert.That(sourceStatistics.Width, Is.EqualTo(8));
            Assert.That(sourceStatistics.Height, Is.EqualTo(8));
            Assert.That(sourceStatistics.PixelCount, Is.EqualTo(64));
            Assert.That(sourceStatistics.LinearChannelMean.X, Is.EqualTo(0.40724021196365356).Within(1e-15));
            Assert.That(sourceStatistics.LinearChannelMean.Y, Is.EqualTo(0.4969329833984375).Within(1e-15));
            Assert.That(sourceStatistics.LinearChannelMean.Z, Is.EqualTo(0.6038273572921753).Within(1e-15));
            Assert.That(sourceStatistics.LinearChannelMean.W, Is.EqualTo(128.0 / 255.0).Within(1e-15));
            Assert.That(sourceStatistics.AlphaHistogram[128], Is.EqualTo(64));
            Assert.That(sourceStatistics.GetAlphaCoverage(0.5), Is.EqualTo(1.0));

            Assert.That(first.PassedThrough, Is.False);
            Assert.That(first.VulkanFormat, Is.Not.Zero);
            Assert.That(first.VulkanFormat, Is.EqualTo(43u));
            Assert.That(first.MipCount, Is.EqualTo(4));
            Assert.That(first.AlphaCoveragePreserved, Is.True);
            Assert.That(first.TransportStatistics.SourceContentHash, Is.EqualTo(sourceStatistics.SourceContentHash));
            Assert.That(first.TransportStatistics.LinearChannelMean, Is.EqualTo(sourceStatistics.LinearChannelMean));
            Assert.That(first.TransportStatistics.AlphaHistogram, Is.EqualTo(sourceStatistics.AlphaHistogram));
            Assert.That(first.SourceBytes, Is.EqualTo(ktx.Length));
            Assert.That(second.TransportStatistics.LinearChannelMean, Is.EqualTo(first.TransportStatistics.LinearChannelMean));
            Assert.That(second.TransportStatistics.AlphaHistogram, Is.EqualTo(first.TransportStatistics.AlphaHistogram));
            Assert.That(File.ReadAllBytes(secondPath), Is.EqualTo(firstBytes));

            Assert.That(width, Is.EqualTo(8));
            Assert.That(height, Is.EqualTo(8));
            Assert.That(mipCount, Is.EqualTo(4));
            Assert.That(format, Is.EqualTo(43u));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(firstBytes.AsSpan(44, 4)), Is.Zero);
            Assert.That(cookedStatistics.IsValid, Is.True);
            Assert.That(cookedStatistics.LinearChannelMean, Is.EqualTo(sourceStatistics.LinearChannelMean));
            Assert.That(cookedStatistics.AlphaHistogram, Is.EqualTo(sourceStatistics.AlphaHistogram));
        });
    }

    [Test]
    public void BasisLzLinearDfd_OverridesSrgbFallbackAndCooksUnorm()
    {
        byte[] ktx = GetKhronosBasisLzFixture();
        int dfdOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(48, 4)));
        ktx[dfdOffset + 14] = 1; // KHR_DF_TRANSFER_LINEAR
        var options = new TextureCookOptions(
            ColorSpace: TextureColorSpace.Srgb,
            TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
            Semantic: TextureSemantic.Data);
        var source = new ModelTextureSource
        {
            Bytes = ktx,
            CacheIdentity = "basis-linear",
            ContainerKind = TextureContainerKind.Ktx2
        };
        string path = Path.Combine(_directory, "basis-linear.ktx2");

        TextureTransportStatistics statistics =
            TextureCooker.AnalyzeTransportStatistics(source, options);
        if (!SupportsPinnedBasisTranscoder())
        {
            Assert.That(statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.UnsupportedEncoding));
            Assert.Throws<NotSupportedException>(() => new TextureCooker().Cook(source, path, options));
            return;
        }

        CookedTextureReport report = new TextureCooker().Cook(source, path, options);

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.True);
            Assert.That(statistics.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
            Assert.That(statistics.LinearChannelMean.X, Is.EqualTo(171.0 / 255.0).Within(1e-15));
            Assert.That(statistics.Decoder, Is.EqualTo(TextureTransportStatistics.BasisDecoderVersion));
            Assert.That(report.VulkanFormat, Is.EqualTo(37u));
            Assert.That(report.PassedThrough, Is.False);
            Assert.That(TextureCooker.Inspect(File.ReadAllBytes(path), path).Format, Is.EqualTo(37u));
        });
    }

    [Test]
    public void Ktx2LevelLengthMismatch_IsRejectedWithoutAllocatingOrWriting()
    {
        byte[] malformed = CreateKtx2(
            format: 37,
            supercompression: 0,
            level: [0, 0, 0, 255, 99],
            uncompressedLength: 5);
        string path = Path.Combine(_directory, "bad-length.ktx2");

        TextureTransportStatistics statistics = TextureCooker.AnalyzeTransportStatistics(
            malformed,
            TextureContainerKind.Ktx2,
            "bad-length.ktx2",
            new TextureCookOptions(ColorSpace: TextureColorSpace.Linear));
        InvalidDataException? error = Assert.Throws<InvalidDataException>(
            () => new TextureCooker().Cook(
                new ModelTextureSource
                {
                    Bytes = malformed,
                    CacheIdentity = "bad-length",
                    ContainerKind = TextureContainerKind.Ktx2
                },
                path,
                new TextureCookOptions(ColorSpace: TextureColorSpace.Linear)));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(statistics.InvalidReason, Does.Contain("requires exactly 4"));
            Assert.That(error!.Message, Does.Contain("requires exactly 4"));
            Assert.That(File.Exists(path), Is.False);
        });
    }

    [Test]
    public void CorruptZstdKtx2_ReturnsInvalidAnalysisAndFailsCooking()
    {
        byte[] malformed = CreateKtx2(
            format: 37,
            supercompression: 2,
            level: [1, 2, 3, 4],
            uncompressedLength: 4);
        string path = Path.Combine(_directory, "corrupt-zstd.ktx2");
        var options = new TextureCookOptions(ColorSpace: TextureColorSpace.Linear);

        TextureTransportStatistics statistics = TextureCooker.AnalyzeTransportStatistics(
            malformed,
            TextureContainerKind.Ktx2,
            "corrupt-zstd.ktx2",
            options);
        InvalidDataException? error = Assert.Throws<InvalidDataException>(
            () => new TextureCooker().Cook(
                new ModelTextureSource
                {
                    Bytes = malformed,
                    CacheIdentity = "corrupt-zstd",
                    ContainerKind = TextureContainerKind.Ktx2
                },
                path,
                options));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(statistics.InvalidReason, Does.Contain("decoding failed"));
            Assert.That(error!.Message, Does.Contain("statistics are invalid"));
            Assert.That(File.Exists(path), Is.False);
        });
    }

    [Test]
    public void Ktx2PayloadRangeOverflow_IsReportedAsInvalidData()
    {
        byte[] malformed = CreateKtx2(format: 37, supercompression: 0, level: [0, 0, 0, 255]);
        BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(80, 8), ulong.MaxValue);

        TextureTransportStatistics statistics = TextureCooker.AnalyzeTransportStatistics(
            malformed,
            TextureContainerKind.Ktx2,
            "overflow.ktx2",
            new TextureCookOptions(ColorSpace: TextureColorSpace.Linear));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.InvalidData));
            Assert.That(statistics.InvalidReason, Does.Contain("outside"));
            Assert.That(statistics.InvalidReason, Does.Not.Contain("Arithmetic operation resulted"));
        });
    }

    [Test]
    public void KtxDecoderVersions_ArePinnedAndAlgorithmInvalidatesCookCache()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextureTransportStatistics.CurrentAlgorithmVersion, Is.GreaterThanOrEqualTo(4));
            Assert.That(TextureTransportStatistics.KtxStatisticsDecoderVersion, Does.Contain("Ktx2.NET/1.0.5"));
            Assert.That(TextureTransportStatistics.BasisDecoderVersion, Does.Contain("libktx RGBA32"));
            Assert.That(TextureTransportStatistics.KtxStatisticsDecoderVersion, Does.Contain("BCnEncoder.Net/2.3.0"));
            Assert.That(TextureTransportStatistics.KtxStatisticsDecoderVersion, Does.Contain("ZstdSharp.Port/0.8.8"));
            Assert.That(TextureTransportStatistics.KtxStatisticsDecoderVersion, Does.Contain("ZLibStream/net10.0"));
        });
    }

    [Test]
    public void TextureCooker_DecodesDdsForStatisticsAndCooking()
    {
        byte[] dds = CreateRgbaDds(width: 4, height: 4, red: 32, green: 96, blue: 160, alpha: 255);
        string path = Path.Combine(_directory, "source-dds.ktx2");
        var source = new ModelTextureSource
        {
            Bytes = dds,
            CacheIdentity = "source.dds",
            DebugName = "source.dds",
            ContainerKind = TextureContainerKind.StandardImage
        };
        var options = new TextureCookOptions(
            MaxDimension: 16,
            ColorSpace: TextureColorSpace.Linear,
            TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
            Semantic: TextureSemantic.Data);

        TextureTransportStatistics statistics =
            TextureCooker.AnalyzeTransportStatistics(source, options);
        CookedTextureReport report = new TextureCooker().Cook(source, path, options);
        (int width, int height, _, uint format) = TextureCooker.Inspect(
            File.ReadAllBytes(path),
            path);

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.True);
            Assert.That(statistics.Decoder, Is.EqualTo(TextureTransportStatistics.DdsDecoderVersion));
            Assert.That(statistics.Width, Is.EqualTo(4));
            Assert.That(statistics.Height, Is.EqualTo(4));
            Assert.That(statistics.LinearChannelMean.X, Is.EqualTo(32.0 / 255.0).Within(1e-12));
            Assert.That(report.TransportStatistics.Decoder, Is.EqualTo(TextureTransportStatistics.DdsDecoderVersion));
            Assert.That(width, Is.EqualTo(4));
            Assert.That(height, Is.EqualTo(4));
            Assert.That(format, Is.EqualTo(37u));
        });
    }

    [Test]
    public void AnalyzeTransportStatistics_DoesNotWriteAndReturnsExplicitUnsupportedState()
    {
        TextureTransportStatistics statistics = TextureCooker.AnalyzeTransportStatistics(
            [1, 2, 3, 4],
            TextureContainerKind.StandardImage,
            "broken.png",
            new TextureCookOptions(Semantic: TextureSemantic.Color));

        Assert.Multiple(() =>
        {
            Assert.That(statistics.IsValid, Is.False);
            Assert.That(
                statistics.Status,
                Is.EqualTo(
                    TextureTransportStatisticsStatus.UnsupportedEncoding));
            Assert.That(statistics.SourceContentHash, Is.Not.Zero);
            Assert.That(
                statistics.InvalidReason,
                Does.Contain("not supported by the pinned runtime decoder"));
        });
    }

    [Test]
    public void TextureMeta_RoundTripsStatisticsAndNormalizesLegacyJsonAsInvalid()
    {
        const ulong sourceHash = 0x99887766;
        TextureTransportStatistics statistics = TextureTransportImage.FromRgba8(
            [255, 128, 0, 255],
            1,
            1,
            TextureColorSpace.Srgb,
            TextureSemantic.Color,
            sourceHash).Statistics;
        var metadata = new CookedTextureMeta(
            Guid.NewGuid(),
            "texture",
            sourceHash,
            "texture.ktx2",
            TextureColorSpace.Srgb,
            TextureSamplerDescription.Default,
            1,
            1,
            1,
            1,
            1,
            43,
            4)
        {
            Ktx2ContentHash = 0x1122334455667788,
            Semantic = TextureSemantic.Color,
            TransportStatistics = statistics
        };
        string currentPath = Path.Combine(_directory, "current.njtex");
        CookedPackage.WriteTextureMeta(currentPath, metadata);
        CookedTextureMeta loaded = CookedPackage.LoadTextureMeta(currentPath);

        string legacyPath = Path.Combine(_directory, "legacy.njtex");
        byte[] legacyJson = CookedJson.Serialize(new
        {
            metadata.AssetId,
            metadata.SourceIdentity,
            metadata.SourceHash,
            metadata.Ktx2RelativePath,
            metadata.ColorSpace,
            metadata.Sampler,
            metadata.OriginalWidth,
            metadata.OriginalHeight,
            metadata.CookedWidth,
            metadata.CookedHeight,
            metadata.MipCount,
            metadata.VulkanFormat,
            metadata.EncodedBytes
        });
        using (var writer = new CookedAssetWriter(legacyPath, CookedAssetKind.Texture, sourceHash))
        {
            writer.WriteSection(CookedSectionIds.Metadata, CookedSectionFlags.Required, legacyJson);
            writer.Complete();
        }
        byte[] legacyBytes = File.ReadAllBytes(legacyPath);
        BinaryPrimitives.WriteUInt16LittleEndian(legacyBytes.AsSpan(8, 2), 1);
        File.WriteAllBytes(legacyPath, legacyBytes);
        CookedTextureMeta legacy = CookedPackage.LoadTextureMeta(legacyPath);
        string migratedPath = Path.Combine(_directory, "legacy-migrated.njtex");
        CookedAssetMigrator.MigrateFile(legacyPath, migratedPath);
        CookedTextureMeta migrated = CookedPackage.LoadTextureMeta(migratedPath);
        using var migratedReader = new CookedAssetReader(migratedPath, CookedAssetKind.Texture);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.TransportStatistics.SourceContentHash, Is.EqualTo(statistics.SourceContentHash));
            Assert.That(loaded.TransportStatistics.LinearChannelMean, Is.EqualTo(statistics.LinearChannelMean));
            Assert.That(loaded.TransportStatistics.AlphaHistogram, Is.EqualTo(statistics.AlphaHistogram));
            Assert.That(loaded.Semantic, Is.EqualTo(TextureSemantic.Color));
            Assert.That(legacy.TransportStatistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.LegacyMissing));
            Assert.That(legacy.TransportStatistics.SourceContentHash, Is.EqualTo(sourceHash));
            Assert.That(legacy.TransportStatistics.IsValid, Is.False);
            Assert.That(migrated.TransportStatistics.Status, Is.EqualTo(TextureTransportStatisticsStatus.LegacyMissing));
            Assert.That(migratedReader.Header.FormatMinor, Is.EqualTo(3));
        });
    }

    [Test]
    public void Migrator_DowngradesStaleTextureStatisticsToExplicitLegacyMissing()
    {
        const ulong sourceHash = 0x8899aabb;
        TextureTransportStatistics stale = TextureTransportImage.FromRgba8(
            [32, 64, 128, 255],
            1,
            1,
            TextureColorSpace.Linear,
            TextureSemantic.Color,
            sourceHash).Statistics with
        {
            AlgorithmVersion =
                TextureTransportStatistics.CurrentAlgorithmVersion - 1
        };
        var metadata = new CookedTextureMeta(
            Guid.NewGuid(),
            "stale.png",
            sourceHash,
            "stale.ktx2",
            TextureColorSpace.Linear,
            TextureSamplerDescription.Default,
            1,
            1,
            1,
            1,
            1,
            37,
            4)
        {
            Ktx2ContentHash = 0x12345678,
            Semantic = TextureSemantic.Color,
            TransportStatistics = stale
        };
        string stalePath = Path.Combine(_directory, "stale.njtex");
        using (var writer = new CookedAssetWriter(
                   stalePath,
                   CookedAssetKind.Texture,
                   sourceHash))
        {
            writer.WriteSection(
                CookedSectionIds.Metadata,
                CookedSectionFlags.Required,
                CookedJson.Serialize(metadata));
            writer.Complete();
        }
        string migratedPath = Path.Combine(
            _directory,
            "stale-migrated.njtex");

        Assert.That(
            () => CookedPackage.LoadTextureMeta(stalePath),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("algorithm"));
        CookedAssetMigrator.MigrateFile(stalePath, migratedPath);
        CookedTextureMeta migrated =
            CookedPackage.LoadTextureMeta(migratedPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                migrated.TransportStatistics.Status,
                Is.EqualTo(
                    TextureTransportStatisticsStatus.LegacyMissing));
            Assert.That(
                migrated.TransportStatistics.SchemaVersion,
                Is.EqualTo(
                    TextureTransportStatistics.CurrentSchemaVersion));
            Assert.That(
                migrated.TransportStatistics.AlgorithmVersion,
                Is.EqualTo(
                    TextureTransportStatistics.CurrentAlgorithmVersion));
            Assert.That(
                migrated.TransportStatistics.InvalidReason,
                Does.Contain("recooking"));
            Assert.That(migrated.TransportStatistics.Validate(), Is.Empty);
        });
    }

    [Test]
    public void TextureAndMaterialCookedFormats_AreIndependentlyVersioned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CookedFormatVersions.Texture, Is.EqualTo(new CookedFormatVersion(1, 3)));
            Assert.That(CookedFormatVersions.Material, Is.EqualTo(new CookedFormatVersion(1, 3)));
            Assert.That(CookedFormatVersions.Mesh, Is.EqualTo(new CookedFormatVersion(2, 0)));
        });
    }

    private static byte[] CreateKtx2(
        uint format,
        uint supercompression,
        byte[] level,
        ulong? uncompressedLength = null,
        uint width = 1,
        uint height = 1)
    {
        const int levelOffset = 104;
        var bytes = new byte[levelOffset + level.Length];
        byte[] identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
        identifier.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), format);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44, 4), supercompression);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80, 8), levelOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88, 8), (ulong)level.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(96, 8),
            uncompressedLength ?? (supercompression == 1 ? 0 : (ulong)level.Length));
        level.CopyTo(bytes, levelOffset);
        return bytes;
    }

    private static byte[] CreateRgbaDds(
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        const int headerLength = 128;
        const uint ddsdCaps = 0x00000001;
        const uint ddsdHeight = 0x00000002;
        const uint ddsdWidth = 0x00000004;
        const uint ddsdPitch = 0x00000008;
        const uint ddsdPixelFormat = 0x00001000;
        const uint ddpfAlphaPixels = 0x00000001;
        const uint ddpfRgb = 0x00000040;
        const uint ddsCapsTexture = 0x00001000;
        int pixelByteCount = checked(width * height * 4);
        var bytes = new byte[headerLength + pixelByteCount];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(8, 4),
            ddsdCaps | ddsdHeight | ddsdWidth | ddsdPitch | ddsdPixelFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), ddpfAlphaPixels | ddpfRgb);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), ddsCapsTexture);

        for (int offset = headerLength; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = red;
            bytes[offset + 1] = green;
            bytes[offset + 2] = blue;
            bytes[offset + 3] = alpha;
        }

        return bytes;
    }

    private static byte[] GetKhronosBasisLzFixture()
    {
        // KhronosGroup/KTX-Software tests/resources/ktx2/alpha_simple_blze.ktx2.
        // The tiny 8x8 fixture is BasisLZ/ETC1S with alpha and an sRGB DFD.
        return Convert.FromBase64String(
            "q0tUWCAyMLsNChoKAAAAAAEAAAAIAAAACAAAAAAAAAAAAAAAAQAAAAEAAAABAAAAaAAAADwAAACkAAAARAAAAOgAAAAAAAAA" +
            "jAAAAAAAAAB0AQAAAAAAAAMAAAAAAAAAAAAAAAAAAAA8AAAAAAAAAAIAOACjAQIAAwMAAAgIAAAAAAAAAAA/AAAAAAAAAAAA" +
            "/////0AAPw8AAAAAAAAAAP////9AAAAAS1RYd3JpdGVyAGt0eCBjcmVhdGUgdjUuMC5fX2RlZmF1bHRfXyAvIGxpYmt0eCB" +
            "2NS4wLl9fZGVmYXVsdF9fAAIAAgAtAAAACQAAAC4AAAAAAAAAAAAAAAAAAAABAAAAAQAAAAIAAAABwAQAAAAAAAACBJgbIAA" +
            "AAAjDNpE+kQBgAgAAAAAAAIEATAEQAAAAACBZwD2sqqqqUlVVVQUUwEQAAAAAAAASQQCYAAAAAAAAQBgCogQMAAAAg3Z7SQS" +
            "iIABMAAgAAAAAIAIBBkwO");
    }

    private static bool SupportsPinnedBasisTranscoder() =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
         RuntimeInformation.IsOSPlatform(OSPlatform.Linux));

    private static byte[] CompressZstd(ReadOnlySpan<byte> bytes)
    {
        using var compressor = new Compressor(3);
        return compressor.Wrap(bytes).ToArray();
    }

    private static byte[] CompressZlib(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            compressor.Write(bytes);
        return output.ToArray();
    }
}
