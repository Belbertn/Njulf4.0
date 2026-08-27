using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using Ktx;
using StbImageSharp;
using ZstdSharp;

namespace Njulf.Assets.Cooked;

public enum TextureMipFilter
{
    Box
}

public enum TextureTargetFormatPolicy
{
    AutoBc,
    Rgba8,
    Bc7,
    Bc5,
    Bc4,
    Bc6H
}

public enum TextureSemantic
{
    Color,
    Normal,
    Scalar,
    Data,
    Hdr
}

public sealed record TextureCookOptions(
    int MaxDimension = 2048,
    TextureColorSpace ColorSpace = TextureColorSpace.Srgb,
    TextureMipFilter MipFilter = TextureMipFilter.Box,
    TextureTargetFormatPolicy TargetFormatPolicy = TextureTargetFormatPolicy.AutoBc,
    TextureSemantic Semantic = TextureSemantic.Color,
    bool PreserveAlphaCoverage = false,
    float AlphaCutoff = 0.5f);

public sealed record CookedTextureReport(
    string SourceIdentity,
    int OriginalWidth,
    int OriginalHeight,
    int CookedWidth,
    int CookedHeight,
    uint VulkanFormat,
    int MipCount,
    long SourceBytes,
    long CookedBytes,
    bool PassedThrough)
{
    public TextureTransportStatistics TransportStatistics { get; init; } =
        TextureTransportStatistics.Invalid(
            TextureTransportStatisticsStatus.InvalidData,
            "Texture cooker did not publish transport statistics.",
            0,
            TextureSemantic.Data,
            TextureColorSpace.Linear);
    public bool AlphaCoveragePreserved { get; init; }
    public float AlphaCutoff { get; init; }

    /// <summary>
    /// Compatibility projection retained for existing cook-report JSON and V1
    /// callers. New code should consume <see cref="TransportStatistics"/>.
    /// </summary>
    public Njulf.Core.Math.Vector4? LinearAverageColor { get; init; }

    [JsonIgnore]
    internal TextureTransportImage? SourceTransportImage { get; init; }
}

/// <summary>
/// Source-resolution transport analysis for runtime uncooked assets.  A valid
/// image is returned only when every source texel was decoded within the
/// caller-supplied work limits.  Unsupported or malformed encodings retain an
/// authenticated statistics record and never expose guessed pixels.
/// </summary>
public sealed record TextureTransportSourceAnalysis(
    TextureTransportStatistics Statistics,
    TextureTransportImage? Image)
{
    public bool IsSampleable => Image is not null && Statistics.IsValid;
}

public interface ITextureCooker
{
    CookedTextureReport Cook(ModelTextureSource source, string ktx2Path, TextureCookOptions options);
}

public sealed class TextureCooker : ITextureCooker
{
    public const int DefaultMaximumRuntimeTransportEncodedBytes = 64 * 1024 * 1024;
    public const long DefaultMaximumRuntimeTransportPixels = 2048L * 2048L;

    private static ReadOnlySpan<byte> Ktx2Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
    private const uint KtxSupercompressionNone = 0;
    private const uint KtxSupercompressionBasisLz = 1;
    private const uint KtxSupercompressionZstandard = 2;
    private const uint KtxSupercompressionZlib = 3;
    private const byte KtxTransferLinear = 1;
    private const byte KtxTransferSrgb = 2;
    private const uint R8Unorm = 9;
    private const uint Rg8Unorm = 16;
    private const uint Rgba8Unorm = 37;
    private const uint Rgba8Srgb = 43;
    private const uint Bgra8Unorm = 44;
    private const uint Bgra8Srgb = 50;
    private const uint Bc1RgbUnorm = 131;
    private const uint Bc1RgbSrgb = 132;
    private const uint Bc1RgbaUnorm = 133;
    private const uint Bc1RgbaSrgb = 134;
    private const uint Bc2Unorm = 135;
    private const uint Bc2Srgb = 136;
    private const uint Bc3Unorm = 137;
    private const uint Bc3Srgb = 138;
    private const uint Bc4Unorm = 139;
    private const uint Bc5Unorm = 141;
    private const uint Bc6HUfloat = 143;
    private const uint Bc6HSfloat = 144;
    private const uint Bc7Unorm = 145;
    private const uint Bc7Srgb = 146;

    /// <summary>
    /// Computes source-resolution transport statistics without resizing,
    /// compressing, or writing files. Unsupported KTX2/Basis encodings and
    /// malformed decoded pixels return explicit invalid statistics.
    /// </summary>
    public static TextureTransportStatistics AnalyzeTransportStatistics(
        ModelTextureSource source,
        TextureCookOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        byte[] encoded = ReadSource(source);
        return AnalyzeTransportSource(
            encoded,
            source.ContainerKind,
            ResolveSourceIdentity(source),
            options,
            int.MaxValue,
            int.MaxValue).Statistics;
    }

    public static TextureTransportStatistics AnalyzeTransportStatistics(
        ReadOnlySpan<byte> encoded,
        TextureContainerKind containerKind,
        string sourceIdentity,
        TextureCookOptions options) =>
        AnalyzeTransportSource(
            encoded,
            containerKind,
            sourceIdentity,
            options,
            int.MaxValue,
            int.MaxValue).Statistics;

    /// <summary>
    /// Decodes immutable source texels for deterministic primitive integration.
    /// Runtime callers must provide finite encoded-byte and pixel limits; the
    /// default limits deliberately fail closed before an oversized decode.
    /// </summary>
    public static TextureTransportSourceAnalysis AnalyzeTransportSource(
        ReadOnlySpan<byte> encoded,
        TextureContainerKind containerKind,
        string sourceIdentity,
        TextureCookOptions options,
        int maximumEncodedBytes = DefaultMaximumRuntimeTransportEncodedBytes,
        long maximumPixels = DefaultMaximumRuntimeTransportPixels)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (maximumEncodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
        if (maximumPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPixels));

        ulong sourceHash = CookedHash.Bytes(encoded);
        if (encoded.IsEmpty)
        {
            return InvalidSourceAnalysis(
                TextureTransportStatisticsStatus.InvalidData,
                "Source image is empty.",
                sourceHash,
                options);
        }
        if (encoded.Length > maximumEncodedBytes)
        {
            return InvalidSourceAnalysis(
                TextureTransportStatisticsStatus.UnsupportedEncoding,
                $"Source image contains {encoded.Length} encoded bytes, exceeding the runtime " +
                $"transport-analysis limit {maximumEncodedBytes}.",
                sourceHash,
                options);
        }

        if (containerKind == TextureContainerKind.Ktx2 || IsKtx2(encoded))
        {
            try
            {
                Ktx2Description description = ParseKtx2(encoded, sourceIdentity);
                EnsureRuntimeTransportPixelBudget(
                    description.Width,
                    description.Height,
                    maximumPixels,
                    sourceIdentity);
                Ktx2Analysis analysis = AnalyzeKtx2(
                    encoded,
                    description,
                    sourceHash,
                    options);
                return new TextureTransportSourceAnalysis(
                    analysis.Statistics,
                    analysis.Statistics.IsValid ? analysis.Image : null);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException or ArgumentException)
            {
                return InvalidSourceAnalysis(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"KTX2 container analysis failed: {ex.Message}",
                    sourceHash,
                    options,
                    "KTX2 container parser");
            }
        }

        if (containerKind == TextureContainerKind.WebP ||
            WebPTextureDecoder.HasWebPSignature(encoded))
        {
            try
            {
                WebPDecodedImage decoded = WebPTextureDecoder.DecodeRgba8(
                    encoded.ToArray(),
                    sourceIdentity,
                    maximumEncodedBytes,
                    maximumPixels);
                TextureTransportImage webPTransportImage = TextureTransportImage.FromRgba8(
                    decoded.Rgba8,
                    decoded.Width,
                    decoded.Height,
                    options.ColorSpace,
                    options.Semantic,
                    sourceHash,
                    TextureTransportStatistics.WebPDecoderVersion);
                return new TextureTransportSourceAnalysis(
                    webPTransportImage.Statistics,
                    webPTransportImage);
            }
            catch (NotSupportedException ex)
            {
                return InvalidSourceAnalysis(
                    TextureTransportStatisticsStatus.UnsupportedEncoding,
                    $"WebP source analysis failed: {ex.Message}",
                    sourceHash,
                    options,
                    TextureTransportStatistics.WebPDecoderVersion);
            }
            catch (Exception ex) when (
                ex is InvalidDataException or
                    OverflowException or
                    ArgumentException)
            {
                return InvalidSourceAnalysis(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"WebP source analysis failed: {ex.Message}",
                    sourceHash,
                    options,
                    TextureTransportStatistics.WebPDecoderVersion);
            }
        }

        if (DdsTextureDecoder.HasDdsSignature(encoded))
        {
            try
            {
                DdsDecodedImage decoded = DdsTextureDecoder.DecodeRgba8(
                    encoded.ToArray(),
                    sourceIdentity,
                    maximumEncodedBytes,
                    maximumPixels);
                TextureTransportImage ddsTransportImage = TextureTransportImage.FromRgba8(
                    decoded.Rgba8,
                    decoded.Width,
                    decoded.Height,
                    options.ColorSpace,
                    options.Semantic,
                    sourceHash,
                    TextureTransportStatistics.DdsDecoderVersion);
                return new TextureTransportSourceAnalysis(
                    ddsTransportImage.Statistics,
                    ddsTransportImage);
            }
            catch (NotSupportedException ex)
            {
                return InvalidSourceAnalysis(
                    TextureTransportStatisticsStatus.UnsupportedEncoding,
                    $"DDS source analysis failed: {ex.Message}",
                    sourceHash,
                    options,
                    TextureTransportStatistics.DdsDecoderVersion);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return InvalidSourceAnalysis(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"DDS source analysis failed: {ex.Message}",
                    sourceHash,
                    options,
                    TextureTransportStatistics.DdsDecoderVersion);
            }
        }

        bool hdr =
            options.ColorSpace == TextureColorSpace.HdrLinear ||
            options.Semantic == TextureSemantic.Hdr ||
            options.TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H;
        try
        {
            byte[] encodedArray = encoded.ToArray();
            using (var stream = new MemoryStream(encodedArray, writable: false))
            {
                ImageInfo? info = ImageInfo.FromStream(stream);
                if (info is null)
                {
                    return InvalidSourceAnalysis(
                        TextureTransportStatisticsStatus.UnsupportedEncoding,
                        "Source image header is not supported by the pinned runtime decoder.",
                        sourceHash,
                        options,
                        TextureTransportStatistics.StbDecoderVersion);
                }
                EnsureRuntimeTransportPixelBudget(
                    info.Value.Width,
                    info.Value.Height,
                    maximumPixels,
                    sourceIdentity);
            }

            if (hdr)
            {
                ImageResultFloat hdrImage = ImageResultFloat.FromMemory(
                    encodedArray,
                    ColorComponents.RedGreenBlueAlpha);
                TextureTransportImage hdrTransportImage = TextureTransportImage.FromRgbaFloat(
                    hdrImage.Data,
                    hdrImage.Width,
                    hdrImage.Height,
                    TextureColorSpace.HdrLinear,
                    options.Semantic,
                    sourceHash);
                return new TextureTransportSourceAnalysis(
                    hdrTransportImage.Statistics,
                    hdrTransportImage);
            }

            ImageResult ldrImage = ImageResult.FromMemory(
                encodedArray,
                ColorComponents.RedGreenBlueAlpha);
            TextureTransportImage ldrTransportImage = TextureTransportImage.FromRgba8(
                ldrImage.Data,
                ldrImage.Width,
                ldrImage.Height,
                options.ColorSpace,
                options.Semantic,
                sourceHash);
            return new TextureTransportSourceAnalysis(
                ldrTransportImage.Statistics,
                ldrTransportImage);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return InvalidSourceAnalysis(
                TextureTransportStatisticsStatus.InvalidData,
                $"Source image analysis failed: {ex.Message}",
                sourceHash,
                options,
                TextureTransportStatistics.StbDecoderVersion);
        }
    }

    private static TextureTransportSourceAnalysis InvalidSourceAnalysis(
        TextureTransportStatisticsStatus status,
        string reason,
        ulong sourceHash,
        TextureCookOptions options,
        string decoder = "")
    {
        TextureTransportStatistics statistics = TextureTransportStatistics.Invalid(
            status,
            reason,
            sourceHash,
            options.Semantic,
            options.ColorSpace,
            decoder);
        return new TextureTransportSourceAnalysis(statistics, null);
    }

    private static void EnsureRuntimeTransportPixelBudget(
        int width,
        int height,
        long maximumPixels,
        string sourceIdentity)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException(
                $"Texture '{sourceIdentity}' has invalid dimensions {width}x{height}.");
        }
        long pixels = checked((long)width * height);
        if (pixels > maximumPixels)
        {
            throw new NotSupportedException(
                $"Texture '{sourceIdentity}' contains {pixels} source pixels, exceeding the " +
                $"runtime transport-analysis limit {maximumPixels}.");
        }
    }

    public CookedTextureReport Cook(ModelTextureSource source, string ktx2Path, TextureCookOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(ktx2Path);
        if (!float.IsFinite(options.AlphaCutoff) || options.AlphaCutoff < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Texture alpha cutoff must be finite and non-negative.");
        }
        byte[] encoded = ReadSource(source);
        ulong sourceHash = CookedHash.Bytes(encoded);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ktx2Path))!);
        if (source.ContainerKind == TextureContainerKind.Ktx2 || IsKtx2(encoded))
        {
            Ktx2Description description = ParseKtx2(encoded, source.CacheIdentity);
            Ktx2Analysis analysis = AnalyzeKtx2(encoded, description, sourceHash, options);
            EnsureKtx2Cookable(source.CacheIdentity, analysis.Statistics);

            if (analysis.DecodedBasisRgba8 is not null)
            {
                TextureCookOptions effectiveOptions = options with
                {
                    ColorSpace = analysis.Statistics.ColorSpace
                };
                return CookLdr(
                    analysis.DecodedBasisRgba8,
                    description.Width,
                    description.Height,
                    source,
                    sourceHash,
                    encoded.Length,
                    ktx2Path,
                    effectiveOptions,
                    TextureTransportStatistics.BasisDecoderVersion,
                    analysis.Image);
            }

            byte[] cooked = encoded;
            bool passedThrough = true;
            if (description.Supercompression is KtxSupercompressionZstandard or KtxSupercompressionZlib)
            {
                try
                {
                    cooked = NormalizeLosslesslySupercompressedKtx2(encoded, description);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException or ZstdException)
                {
                    throw new InvalidDataException(
                        $"KTX2 texture '{source.CacheIdentity}' could not be normalized from " +
                        $"{DescribeSupercompression(description.Supercompression)} supercompression: {ex.Message}",
                        ex);
                }
                passedThrough = false;
            }

            WriteAtomic(ktx2Path, cooked);
            return new CookedTextureReport(
                source.CacheIdentity,
                description.Width,
                description.Height,
                description.Width,
                description.Height,
                description.Format,
                description.MipCount,
                encoded.Length,
                cooked.Length,
                passedThrough)
            {
                TransportStatistics = analysis.Statistics,
                LinearAverageColor = GetCompatibilityLinearAverage(analysis.Statistics),
                AlphaCoveragePreserved = false,
                AlphaCutoff = options.AlphaCutoff,
                SourceTransportImage = analysis.Image
            };
        }

        if (WebPTextureDecoder.IsDeclaredWebP(source, encoded))
        {
            string webPSourceIdentity = ResolveSourceIdentity(source);
            WebPDecodedImage decoded;
            try
            {
                decoded = WebPTextureDecoder.DecodeRgba8(
                    encoded,
                    webPSourceIdentity);
            }
            catch (Exception ex) when (
                ex is InvalidDataException or
                    NotSupportedException or
                    ArgumentException)
            {
                throw new InvalidDataException(
                    $"WebP texture '{webPSourceIdentity}' could not be decoded for cooking: {ex.Message}",
                    ex);
            }

            if (options.ColorSpace == TextureColorSpace.HdrLinear ||
                options.Semantic == TextureSemantic.Hdr ||
                options.TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H)
            {
                float[] rgbaFloats = ConvertRgba8ToFloat(decoded.Rgba8);
                return CookHdr(
                    rgbaFloats,
                    decoded.Width,
                    decoded.Height,
                    source,
                    sourceHash,
                    encoded.Length,
                    ktx2Path,
                    options,
                    TextureTransportStatistics.WebPDecoderVersion);
            }

            return CookLdr(
                decoded.Rgba8,
                decoded.Width,
                decoded.Height,
                source,
                sourceHash,
                encoded.Length,
                ktx2Path,
                options,
                TextureTransportStatistics.WebPDecoderVersion);
        }

        if (DdsTextureDecoder.HasDdsSignature(encoded))
        {
            DdsDecodedImage decoded;
            try
            {
                decoded = DdsTextureDecoder.DecodeRgba8(
                    encoded,
                    source.CacheIdentity,
                    int.MaxValue,
                    int.MaxValue);
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException(
                    $"DDS texture '{source.CacheIdentity}' could not be decoded for cooking.",
                    ex);
            }

            return CookLdr(
                decoded.Rgba8,
                decoded.Width,
                decoded.Height,
                source,
                sourceHash,
                encoded.Length,
                ktx2Path,
                options,
                TextureTransportStatistics.DdsDecoderVersion);
        }

        if (options.ColorSpace == TextureColorSpace.HdrLinear || options.Semantic == TextureSemantic.Hdr || options.TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H)
            return CookHdr(encoded, source, sourceHash, ktx2Path, options);

        ImageResult image;
        try { image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) { throw new InvalidDataException($"Texture '{source.CacheIdentity}' could not be decoded for cooking.", ex); }
        return CookLdr(
            image.Data,
            image.Width,
            image.Height,
            source,
            sourceHash,
            encoded.Length,
            ktx2Path,
            options,
            TextureTransportStatistics.StbDecoderVersion);
    }

    private static CookedTextureReport CookLdr(
        byte[] rgba,
        int width,
        int height,
        ModelTextureSource source,
        ulong sourceHash,
        int sourceByteCount,
        string ktx2Path,
        TextureCookOptions options,
        string decoder,
        TextureTransportImage? transportImage = null)
    {
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new InvalidDataException(
                $"Texture '{source.CacheIdentity}' decoded to an invalid {width}x{height} RGBA8 image.");
        transportImage ??= TextureTransportImage.FromRgba8(
            rgba,
            width,
            height,
            options.ColorSpace,
            options.Semantic,
            sourceHash,
            decoder);
        transportImage.Statistics.EnsureValid(source.CacheIdentity);
        double sourceAlphaCoverage = options.PreserveAlphaCoverage
            ? transportImage.Statistics.GetAlphaCoverage(options.AlphaCutoff)
            : 0.0;
        int targetWidth = width;
        int targetHeight = height;
        if (options.MaxDimension > 0 && Math.Max(width, height) > options.MaxDimension)
        {
            double scale = options.MaxDimension / (double)Math.Max(width, height);
            targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        }
        byte[] level = targetWidth == width && targetHeight == height
            ? rgba.ToArray()
            : ResizeLdr(rgba, width, height, targetWidth, targetHeight, options);
        if (options.PreserveAlphaCoverage)
            AlphaCoverageMipGenerator.PreserveCoverage(level, options.AlphaCutoff, sourceAlphaCoverage);
        var levels = new List<byte[]> { level };
        int levelWidth = targetWidth;
        int levelHeight = targetHeight;
        while (levelWidth > 1 || levelHeight > 1)
        {
            int nextWidth = Math.Max(1, levelWidth / 2);
            int nextHeight = Math.Max(1, levelHeight / 2);
            level = ResizeLdr(level, levelWidth, levelHeight, nextWidth, nextHeight, options);
            if (options.PreserveAlphaCoverage)
                AlphaCoverageMipGenerator.PreserveCoverage(level, options.AlphaCutoff, sourceAlphaCoverage);
            levels.Add(level);
            levelWidth = nextWidth;
            levelHeight = nextHeight;
        }
        (uint format, CompressionFormat? bcFormat) = ResolveFormat(options);
        IReadOnlyList<byte[]> outputLevels = bcFormat.HasValue ? EncodeBc(levels, targetWidth, targetHeight, bcFormat.Value) : levels;
        byte[] ktx = BuildKtx2(targetWidth, targetHeight, format, outputLevels);
        WriteAtomic(ktx2Path, ktx);
        return new CookedTextureReport(source.CacheIdentity, width, height, targetWidth, targetHeight, format, levels.Count, sourceByteCount, ktx.Length, false)
        {
            TransportStatistics = transportImage.Statistics,
            LinearAverageColor = transportImage.Statistics.LinearChannelMean.ToVector4(),
            AlphaCoveragePreserved = options.PreserveAlphaCoverage,
            AlphaCutoff = options.AlphaCutoff,
            SourceTransportImage = transportImage
        };
    }

    private static CookedTextureReport CookHdr(
        byte[] encoded,
        ModelTextureSource source,
        ulong sourceHash,
        string ktx2Path,
        TextureCookOptions options)
    {
        ImageResultFloat image;
        try { image = ImageResultFloat.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) { throw new InvalidDataException($"HDR texture '{source.CacheIdentity}' could not be decoded for cooking.", ex); }
        return CookHdr(
            image.Data,
            image.Width,
            image.Height,
            source,
            sourceHash,
            encoded.Length,
            ktx2Path,
            options,
            TextureTransportStatistics.StbDecoderVersion);
    }

    private static CookedTextureReport CookHdr(
        float[] linearRgba,
        int originalWidth,
        int originalHeight,
        ModelTextureSource source,
        ulong sourceHash,
        int sourceByteCount,
        string ktx2Path,
        TextureCookOptions options,
        string decoder)
    {
        if (originalWidth <= 0 ||
            originalHeight <= 0 ||
            linearRgba.Length != checked(originalWidth * originalHeight * 4))
        {
            throw new InvalidDataException(
                $"HDR texture '{source.CacheIdentity}' decoded to an invalid " +
                $"{originalWidth}x{originalHeight} RGBA image.");
        }
        TextureTransportImage transportImage = TextureTransportImage.FromRgbaFloat(
            linearRgba,
            originalWidth,
            originalHeight,
            TextureColorSpace.HdrLinear,
            options.Semantic,
            sourceHash,
            decoder);
        transportImage.Statistics.EnsureValid(source.CacheIdentity);
        int targetWidth = originalWidth;
        int targetHeight = originalHeight;
        if (options.MaxDimension > 0 && Math.Max(targetWidth, targetHeight) > options.MaxDimension)
        {
            double scale = options.MaxDimension / (double)Math.Max(targetWidth, targetHeight);
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * scale));
        }
        float[] level = targetWidth == originalWidth && targetHeight == originalHeight
            ? linearRgba
            : ResizeBox(linearRgba, originalWidth, originalHeight, targetWidth, targetHeight);
        var levels = new List<float[]> { level };
        int width = targetWidth;
        int height = targetHeight;
        while (width > 1 || height > 1)
        {
            int nextWidth = Math.Max(1, width / 2);
            int nextHeight = Math.Max(1, height / 2);
            level = ResizeBox(level, width, height, nextWidth, nextHeight);
            levels.Add(level);
            width = nextWidth;
            height = nextHeight;
        }
        var encoder = CreateBcEncoder(CompressionFormat.Bc6U);
        var compressed = new byte[levels.Count][];
        width = targetWidth;
        height = targetHeight;
        for (int mip = 0; mip < levels.Count; mip++)
        {
            float[] rgba = levels[mip];
            var colors = new ColorRgbFloat[width * height];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = new ColorRgbFloat(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            compressed[mip] = encoder.EncodeToRawBytesHdr(new ReadOnlyMemory2D<ColorRgbFloat>(colors, height, width))[0];
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        byte[] ktx = BuildKtx2(targetWidth, targetHeight, Bc6HUfloat, compressed);
        WriteAtomic(ktx2Path, ktx);
        return new CookedTextureReport(
            source.CacheIdentity,
            originalWidth,
            originalHeight,
            targetWidth,
            targetHeight,
            Bc6HUfloat,
            compressed.Length,
            sourceByteCount,
            ktx.Length,
            false)
        {
            TransportStatistics = transportImage.Statistics,
            LinearAverageColor = transportImage.Statistics.LinearChannelMean.ToVector4(),
            AlphaCoveragePreserved = false,
            AlphaCutoff = options.AlphaCutoff,
            SourceTransportImage = transportImage
        };
    }

    private static float[] ConvertRgba8ToFloat(ReadOnlySpan<byte> rgba)
    {
        var result = GC.AllocateUninitializedArray<float>(rgba.Length);
        const float scale = 1.0f / 255.0f;
        for (int i = 0; i < rgba.Length; i++)
            result[i] = rgba[i] * scale;
        return result;
    }

    private static (uint VulkanFormat, CompressionFormat? BcFormat) ResolveFormat(TextureCookOptions options)
    {
        TextureTargetFormatPolicy policy = options.TargetFormatPolicy == TextureTargetFormatPolicy.AutoBc
            ? options.Semantic switch
            {
                TextureSemantic.Normal => TextureTargetFormatPolicy.Bc5,
                TextureSemantic.Scalar => TextureTargetFormatPolicy.Bc4,
                TextureSemantic.Hdr => TextureTargetFormatPolicy.Bc6H,
                _ => TextureTargetFormatPolicy.Bc7
            }
            : options.TargetFormatPolicy;
        return policy switch
        {
            TextureTargetFormatPolicy.Rgba8 => (options.ColorSpace == TextureColorSpace.Srgb ? Rgba8Srgb : Rgba8Unorm, null),
            TextureTargetFormatPolicy.Bc4 => (Bc4Unorm, CompressionFormat.Bc4),
            TextureTargetFormatPolicy.Bc5 => (Bc5Unorm, CompressionFormat.Bc5),
            TextureTargetFormatPolicy.Bc7 => (options.ColorSpace == TextureColorSpace.Srgb ? Bc7Srgb : Bc7Unorm, CompressionFormat.Bc7),
            TextureTargetFormatPolicy.Bc6H => (Bc6HUfloat, CompressionFormat.Bc6U),
            _ => throw new ArgumentOutOfRangeException(nameof(options), policy, "Unsupported texture target format.")
        };
    }

    private static IReadOnlyList<byte[]> EncodeBc(IReadOnlyList<byte[]> levels, int width, int height, CompressionFormat format)
    {
        var encoder = CreateBcEncoder(format);
        var result = new byte[levels.Count][];
        for (int i = 0; i < levels.Count; i++)
        {
            result[i] = encoder.EncodeToRawBytes(levels[i], width, height, PixelFormat.Rgba32)[0];
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return result;
    }

    private static BcEncoder CreateBcEncoder(CompressionFormat format)
    {
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Format = format;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        return encoder;
    }

    public static (int Width, int Height, int MipCount, uint Format) Inspect(ReadOnlySpan<byte> data, string sourceName)
    {
        Ktx2Description value = ParseKtx2(data, sourceName);
        return (value.Width, value.Height, value.MipCount, value.Format);
    }

    private static byte[] ReadSource(ModelTextureSource source)
    {
        if (source.Bytes is { Length: > 0 })
        {
            if (WebPTextureDecoder.IsDeclaredWebP(source, source.Bytes) &&
                source.Bytes.Length > WebPTextureDecoder.DefaultMaximumEncodedBytes)
            {
                throw new NotSupportedException(
                    $"WebP texture '{ResolveSourceIdentity(source)}' contains " +
                    $"{source.Bytes.Length} encoded bytes, exceeding the decode limit " +
                    $"{WebPTextureDecoder.DefaultMaximumEncodedBytes}.");
            }

            return source.Bytes.ToArray();
        }
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            string fullPath = Path.GetFullPath(source.FilePath);
            if (WebPTextureDecoder.IsDeclaredWebP(source) ||
                WebPTextureDecoder.FileHasWebPSignature(fullPath))
            {
                return WebPTextureDecoder.ReadBoundedFile(
                    fullPath,
                    ResolveSourceIdentity(source));
            }

            return AssetArtifactFileIo.ReadBoundedSnapshot(
                fullPath,
                AssetArtifactFileIo.MaximumCookSourceBytes,
                "Texture source");
        }
        throw new InvalidDataException($"Texture '{source.CacheIdentity}' has neither encoded bytes nor a file path.");
    }

    private static string ResolveSourceIdentity(ModelTextureSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.CacheIdentity))
            return source.CacheIdentity;
        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return Path.GetFullPath(source.FilePath);
        if (!string.IsNullOrWhiteSpace(source.DebugName))
            return source.DebugName;
        return "UnnamedWebPTexture";
    }

    private static bool IsKtx2(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..12].SequenceEqual(Ktx2Identifier);

    private static Ktx2Description ParseKtx2(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length < 80 || !IsKtx2(data))
            throw new InvalidDataException($"Texture '{sourceName}' is not a valid KTX2 container.");

        uint format = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        uint typeSize = BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]);
        uint encodedWidth = BinaryPrimitives.ReadUInt32LittleEndian(data[20..24]);
        uint encodedHeight = BinaryPrimitives.ReadUInt32LittleEndian(data[24..28]);
        if (encodedWidth is 0 or > int.MaxValue || encodedHeight is 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' has unsupported dimensions {encodedWidth}x{encodedHeight}; " +
                $"each dimension must be in [1, {int.MaxValue}].");
        }
        int width = (int)encodedWidth;
        int height = (int)encodedHeight;
        uint depth = BinaryPrimitives.ReadUInt32LittleEndian(data[28..32]);
        uint layers = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]);
        uint faces = BinaryPrimitives.ReadUInt32LittleEndian(data[36..40]);
        uint declaredMipCount = BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]);
        uint effectiveMipCount = Math.Max(1u, declaredMipCount);
        if (effectiveMipCount > int.MaxValue)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' declares an unsupported mip count {effectiveMipCount}.");
        int mips = (int)effectiveMipCount;
        uint supercompression = BinaryPrimitives.ReadUInt32LittleEndian(data[44..48]);
        if (width <= 0 || height <= 0 || depth != 0 || layers != 0 || faces != 1)
            throw new NotSupportedException($"KTX2 texture '{sourceName}' must be a non-array, single-face 2D texture.");
        if (typeSize == 0)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' has invalid typeSize 0.");
        if (format == 0 && typeSize != 1)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' has vkFormat=0 but typeSize {typeSize}; undefined formats require typeSize 1.");
        if (declaredMipCount == 0 && IsBlockCompressedFormat(format))
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' declares levelCount=0, which is not valid for block-compressed format {format}.");
        }

        int maximumMipCount = GetMaximumMipCount(width, height);
        if (mips > maximumMipCount)
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' declares {mips} mip levels, but {width}x{height} permits at most {maximumMipCount}.");
        }

        long levelIndexEndLong = checked(80L + checked((long)mips * 24L));
        if (levelIndexEndLong > data.Length)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' has a truncated level index.");
        int levelIndexEnd = checked((int)levelIndexEndLong);

        Ktx2Section dfd = ReadKtx2Section(
            BinaryPrimitives.ReadUInt32LittleEndian(data[48..52]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[52..56]),
            "data format descriptor",
            alignment: 4,
            levelIndexEnd,
            data.Length,
            sourceName);
        Ktx2Section kvd = ReadKtx2Section(
            BinaryPrimitives.ReadUInt32LittleEndian(data[56..60]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[60..64]),
            "key/value data",
            alignment: 4,
            levelIndexEnd,
            data.Length,
            sourceName);
        Ktx2Section sgd = ReadKtx2Section(
            BinaryPrimitives.ReadUInt64LittleEndian(data[64..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[72..80]),
            "supercompression global data",
            alignment: 8,
            levelIndexEnd,
            data.Length,
            sourceName);

        if (dfd.Length != 0)
        {
            if (dfd.Length < sizeof(uint) || (dfd.Length & 3) != 0)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' has an invalid data format descriptor length {dfd.Length}.");
            uint declaredDfdLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(dfd.Offset, sizeof(uint)));
            if (declaredDfdLength != (uint)dfd.Length)
            {
                throw new InvalidDataException(
                    $"KTX2 texture '{sourceName}' data format descriptor declares {declaredDfdLength} bytes, but the header indexes {dfd.Length}.");
            }
        }
        if ((kvd.Length & 3) != 0)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' key/value data length {kvd.Length} is not 4-byte aligned.");
        if (supercompression is (KtxSupercompressionZstandard or KtxSupercompressionZlib) &&
            sgd.Length != 0)
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' uses {DescribeSupercompression(supercompression)}, which must not contain supercompression global data.");
        }

        var levels = new Ktx2Level[mips];
        int levelWidth = width;
        int levelHeight = height;
        for (int levelIndex = 0; levelIndex < mips; levelIndex++)
        {
            int entryOffset = checked(80 + levelIndex * 24);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(entryOffset, 8));
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(entryOffset + 8, 8));
            ulong uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(entryOffset + 16, 8));
            if (length == 0)
                throw new InvalidDataException($"KTX2 texture '{sourceName}' mip {levelIndex} has an empty payload.");

            Ktx2Section payload = ReadKtx2Section(
                offset,
                length,
                $"mip {levelIndex}",
                alignment: supercompression == KtxSupercompressionNone
                    ? GetRequiredLevelAlignment(format)
                    : 1,
                levelIndexEnd,
                data.Length,
                sourceName);
            switch (supercompression)
            {
                case KtxSupercompressionNone when uncompressedLength != length:
                    throw new InvalidDataException(
                        $"KTX2 texture '{sourceName}' mip {levelIndex} is not supercompressed, but byteLength {length} " +
                        $"differs from uncompressedByteLength {uncompressedLength}.");
                case KtxSupercompressionBasisLz when uncompressedLength != 0:
                    throw new InvalidDataException(
                        $"KTX2 texture '{sourceName}' BasisLZ mip {levelIndex} must declare uncompressedByteLength 0.");
                case KtxSupercompressionZstandard or KtxSupercompressionZlib when uncompressedLength == 0:
                    throw new InvalidDataException(
                        $"KTX2 texture '{sourceName}' {DescribeSupercompression(supercompression)} mip {levelIndex} " +
                        "must declare a non-zero uncompressedByteLength.");
            }

            if (TryGetExpectedLevelLength(format, levelWidth, levelHeight, out ulong expectedLength))
            {
                ulong actualUncompressedLength =
                    supercompression == KtxSupercompressionBasisLz ? expectedLength : uncompressedLength;
                if (actualUncompressedLength != expectedLength)
                {
                    throw new InvalidDataException(
                        $"KTX2 texture '{sourceName}' mip {levelIndex} declares {actualUncompressedLength} decoded bytes; " +
                        $"format {format} at {levelWidth}x{levelHeight} requires exactly {expectedLength}.");
                }
                if (expectedLength > (ulong)Array.MaxLength)
                {
                    throw new InvalidDataException(
                        $"KTX2 texture '{sourceName}' mip {levelIndex} requires {expectedLength} decoded bytes, " +
                        $"which exceeds the supported managed-array limit {Array.MaxLength}.");
                }
            }

            levels[levelIndex] = new Ktx2Level(
                payload.Offset,
                payload.Length,
                uncompressedLength,
                levelWidth,
                levelHeight);
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        var occupiedRanges = new List<(Ktx2Section Section, string Name)>(mips + 3);
        AddRange(occupiedRanges, dfd, "data format descriptor");
        AddRange(occupiedRanges, kvd, "key/value data");
        AddRange(occupiedRanges, sgd, "supercompression global data");
        for (int levelIndex = 0; levelIndex < levels.Length; levelIndex++)
            AddRange(occupiedRanges, levels[levelIndex].Payload, $"mip {levelIndex}");
        ValidateNoOverlaps(occupiedRanges, sourceName);

        return new Ktx2Description(
            width,
            height,
            mips,
            format,
            typeSize,
            supercompression,
            levels,
            dfd,
            kvd);
    }

    private static Ktx2Analysis AnalyzeKtx2(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description,
        ulong sourceHash,
        TextureCookOptions options)
    {
        if (description.Format == 0)
        {
            if (description.Supercompression != KtxSupercompressionBasisLz)
            {
                string encoding = description.Supercompression switch
                {
                    KtxSupercompressionZstandard => "Zstd-supercompressed UASTC or another undefined format",
                    _ => "an undefined or transcodable format"
                };
                return new Ktx2Analysis(
                    TextureTransportStatistics.Invalid(
                        TextureTransportStatisticsStatus.UnsupportedEncoding,
                        $"KTX2 has vkFormat=0 ({encoding}); the pinned Basis path currently supports BasisLZ/ETC1S.",
                        sourceHash,
                        options.Semantic,
                        options.ColorSpace,
                        TextureTransportStatistics.KtxStatisticsDecoderVersion),
                    null,
                    null);
            }

            try
            {
                DecodedBasisTexture decoded = DecodeBasisKtx2(encoded, description, sourceHash, options);
                return new Ktx2Analysis(decoded.Image.Statistics, decoded.Image, decoded.Rgba8);
            }
            catch (NotSupportedException ex)
            {
                return new Ktx2Analysis(
                    TextureTransportStatistics.Invalid(
                        TextureTransportStatisticsStatus.UnsupportedEncoding,
                        $"KTX2 BasisLZ decoding is unavailable: {ex.Message}",
                        sourceHash,
                        options.Semantic,
                        ResolveBasisColorSpace(encoded, description, options.ColorSpace),
                        TextureTransportStatistics.BasisDecoderVersion),
                    null,
                    null);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or OverflowException)
            {
                return new Ktx2Analysis(
                    TextureTransportStatistics.Invalid(
                        TextureTransportStatisticsStatus.InvalidData,
                        $"KTX2 BasisLZ level-0 decoding failed: {ex.Message}",
                        sourceHash,
                        options.Semantic,
                        ResolveBasisColorSpace(encoded, description, options.ColorSpace),
                        TextureTransportStatistics.BasisDecoderVersion),
                    null,
                    null);
            }
        }
        if (description.Supercompression == KtxSupercompressionBasisLz)
        {
            return new Ktx2Analysis(
                TextureTransportStatistics.Invalid(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"KTX2 BasisLZ requires vkFormat=0, but the container declares Vulkan format {description.Format}.",
                    sourceHash,
                    options.Semantic,
                    ResolveKtxColorSpace(description.Format, options.ColorSpace),
                    TextureTransportStatistics.KtxStatisticsDecoderVersion),
                null,
                null);
        }
        if (description.Supercompression is not (
                KtxSupercompressionNone or
                KtxSupercompressionZstandard or
                KtxSupercompressionZlib))
        {
            return new Ktx2Analysis(
                TextureTransportStatistics.Invalid(
                    TextureTransportStatisticsStatus.UnsupportedEncoding,
                    $"KTX2 supercompression scheme {description.Supercompression} is not supported by the pinned statistics decoder.",
                    sourceHash,
                    options.Semantic,
                    ResolveKtxColorSpace(description.Format, options.ColorSpace),
                    TextureTransportStatistics.KtxStatisticsDecoderVersion),
                null,
                null);
        }
        if (description.TypeSize != 1)
        {
            return new Ktx2Analysis(
                TextureTransportStatistics.Invalid(
                    TextureTransportStatisticsStatus.UnsupportedEncoding,
                    $"KTX2 typeSize {description.TypeSize} is not supported by the pinned raw/BC statistics decoder.",
                    sourceHash,
                    options.Semantic,
                    ResolveKtxColorSpace(description.Format, options.ColorSpace),
                    TextureTransportStatistics.KtxStatisticsDecoderVersion),
                null,
                null);
        }

        try
        {
            Ktx2Level level0 = description.Levels[0];
            byte[]? inflated = null;
            ReadOnlySpan<byte> level = encoded.Slice(level0.Offset, level0.Length);
            if (description.Supercompression != KtxSupercompressionNone)
            {
                inflated = InflateKtxLevel(level, level0, description.Supercompression);
                level = inflated;
            }
            string decoder = GetKtxContainerDecoderName(description.Supercompression);
            if (TryDecodeRawKtx(
                    level,
                    description,
                    sourceHash,
                    options.Semantic,
                    decoder,
                    out TextureTransportImage? rawImage))
            {
                return new Ktx2Analysis(rawImage.Statistics, rawImage, null);
            }
            if (TryDecodeBcKtx(
                    level,
                    description,
                    sourceHash,
                    options.Semantic,
                    decoder,
                    out TextureTransportImage? bcImage))
            {
                return new Ktx2Analysis(bcImage.Statistics, bcImage, null);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or OverflowException or ZstdException)
        {
            return new Ktx2Analysis(
                TextureTransportStatistics.Invalid(
                    TextureTransportStatisticsStatus.InvalidData,
                    $"KTX2 level-0 decoding failed: {ex.Message}",
                    sourceHash,
                    options.Semantic,
                    ResolveKtxColorSpace(description.Format, options.ColorSpace),
                    GetKtxContainerDecoderName(description.Supercompression)),
                null,
                null);
        }

        return new Ktx2Analysis(
            TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.UnsupportedEncoding,
                $"KTX2 Vulkan format {description.Format} is not supported by the pinned statistics decoder.",
                sourceHash,
                options.Semantic,
                ResolveKtxColorSpace(description.Format, options.ColorSpace),
                TextureTransportStatistics.KtxStatisticsDecoderVersion),
            null,
            null);
    }

    private static unsafe DecodedBasisTexture DecodeBasisKtx2(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description,
        ulong sourceHash,
        TextureCookOptions options)
    {
        if (options.ColorSpace == TextureColorSpace.HdrLinear ||
            options.Semantic == TextureSemantic.Hdr ||
            options.TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H)
        {
            throw new NotSupportedException(
                "BasisLZ/ETC1S is an LDR encoding and cannot satisfy an HDR or BC6H cook request.");
        }
        ulong decodedByteCount = checked(
            (ulong)(uint)description.Width *
            (uint)description.Height *
            4u);
        if (decodedByteCount > (ulong)Array.MaxLength)
        {
            throw new InvalidDataException(
                $"BasisLZ level 0 requires {decodedByteCount} RGBA32 bytes, " +
                $"which exceeds the managed-array limit {Array.MaxLength}.");
        }
        int expectedLength = (int)decodedByteCount;

        EnsureBasisTranscoderPlatform();
        Ktx2.Texture* texture = null;
        try
        {
            Ktx2.ErrorCode createResult;
            try
            {
                ref byte source = ref MemoryMarshal.GetReference(encoded);
                createResult = Ktx2.CreateFromMemory(
                    in source,
                    checked((nuint)encoded.Length),
                    Ktx2.TextureCreateFlagBits.LoadImageData | Ktx2.TextureCreateFlagBits.CheckGltfBasisU,
                    out texture);
            }
            catch (Exception ex) when (IsNativeBasisLoadFailure(ex))
            {
                throw CreateBasisCapabilityException(ex);
            }
            EnsureBasisResult(createResult, "create the KTX2 texture");
            if (texture == null)
                throw new InvalidDataException("libktx returned success without creating a texture.");
            if (texture->BaseWidth != (uint)description.Width ||
                texture->BaseHeight != (uint)description.Height ||
                texture->NumLevels != (uint)description.MipCount)
            {
                throw new InvalidDataException(
                    $"libktx reported {texture->BaseWidth}x{texture->BaseHeight} with {texture->NumLevels} mips; " +
                    $"the validated container declares {description.Width}x{description.Height} with {description.MipCount} mips.");
            }
            if (!Ktx2.NeedsTranscoding(texture))
                throw new InvalidDataException("libktx did not identify the vkFormat=0 BasisLZ payload as transcodable.");

            Ktx2.ErrorCode transcodeResult = Ktx2.TranscodeBasis(
                texture,
                Ktx2.TranscodeFormat.Rgba32,
                (Ktx2.TranscodeFlagBits)0);
            EnsureBasisResult(transcodeResult, "transcode BasisLZ level 0 to RGBA32");

            Ktx2.ErrorCode offsetResult = Ktx2.GetImageOffset(texture, 0, 0, 0, out nuint imageOffset);
            EnsureBasisResult(offsetResult, "locate transcoded level 0");
            nuint imageSize = Ktx2.GetImageSize(texture, 0);
            if (imageSize != (nuint)expectedLength)
            {
                throw new InvalidDataException(
                    $"libktx produced {imageSize} RGBA32 bytes for level 0; " +
                    $"{description.Width}x{description.Height} requires exactly {expectedLength}.");
            }
            if (texture->PData == null)
                throw new InvalidDataException("libktx produced no transcoded image data.");
            if (imageOffset > texture->DataSize || imageSize > texture->DataSize - imageOffset)
            {
                throw new InvalidDataException(
                    $"libktx level-0 range [{imageOffset}, {imageOffset + imageSize}) " +
                    $"is outside its {texture->DataSize}-byte image allocation.");
            }
            if (imageOffset > int.MaxValue)
                throw new InvalidDataException($"libktx level-0 offset {imageOffset} exceeds the managed pointer range.");

            byte[] rgba = new ReadOnlySpan<byte>(
                texture->PData + checked((int)imageOffset),
                expectedLength).ToArray();
            TextureColorSpace colorSpace = ResolveBasisColorSpace(encoded, description, options.ColorSpace);
            TextureTransportImage image = TextureTransportImage.FromRgba8(
                rgba,
                description.Width,
                description.Height,
                colorSpace,
                options.Semantic,
                sourceHash,
                TextureTransportStatistics.BasisDecoderVersion);
            image.Statistics.EnsureValid("BasisLZ level 0");
            return new DecodedBasisTexture(rgba, image);
        }
        catch (Exception ex) when (IsNativeBasisLoadFailure(ex))
        {
            throw CreateBasisCapabilityException(ex);
        }
        finally
        {
            if (texture != null)
            {
                try
                {
                    Ktx2.Destroy(texture);
                }
                catch (Exception ex) when (IsNativeBasisLoadFailure(ex))
                {
                    // A successful create pins the native library for the process. A loader
                    // failure here is therefore non-actionable and must not mask the cook result.
                }
            }
        }
    }

    private static TextureColorSpace ResolveBasisColorSpace(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description,
        TextureColorSpace fallback)
    {
        Ktx2Section section = description.DataFormatDescriptor;
        if (section.Length >= 16)
        {
            ReadOnlySpan<byte> dfd = encoded.Slice(section.Offset, section.Length);
            ushort descriptorBlockSize = BinaryPrimitives.ReadUInt16LittleEndian(dfd.Slice(10, 2));
            if (descriptorBlockSize >= 12 && descriptorBlockSize <= dfd.Length - sizeof(uint))
            {
                return dfd[14] switch
                {
                    KtxTransferLinear => TextureColorSpace.Linear,
                    KtxTransferSrgb => TextureColorSpace.Srgb,
                    _ => fallback
                };
            }
        }
        return fallback;
    }

    private static void EnsureBasisTranscoderPlatform()
    {
        bool supportedOperatingSystem =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        if (supportedOperatingSystem && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return;

        throw new PlatformNotSupportedException(
            $"{TextureTransportStatistics.BasisDecoderVersion} ships native binaries only for win-x64 and linux-x64; " +
            $"the current process is {RuntimeInformation.RuntimeIdentifier}.");
    }

    private static bool IsNativeBasisLoadFailure(Exception exception)
    {
        if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            return true;
        return exception is TypeInitializationException { InnerException: Exception innerException } &&
               IsNativeBasisLoadFailure(innerException);
    }

    private static PlatformNotSupportedException CreateBasisCapabilityException(Exception innerException) =>
        new(
            $"{TextureTransportStatistics.BasisDecoderVersion} native libktx is unavailable for " +
            $"{RuntimeInformation.RuntimeIdentifier}. Restore the pinned package runtime asset and run the cooker " +
            "in a win-x64 or linux-x64 process.",
            innerException);

    private static void EnsureBasisResult(Ktx2.ErrorCode result, string operation)
    {
        if (result == Ktx2.ErrorCode.Success)
            return;
        string message =
            $"{TextureTransportStatistics.BasisDecoderVersion} could not {operation}: libktx returned {result} ({(int)result}).";
        if (result is
            Ktx2.ErrorCode.UnsupportedFeature or
            Ktx2.ErrorCode.UnsupportedTextureType or
            Ktx2.ErrorCode.LibraryNotLinked)
        {
            throw new NotSupportedException(message);
        }
        throw new InvalidDataException(message);
    }

    private static void EnsureKtx2Cookable(
        string sourceIdentity,
        TextureTransportStatistics statistics)
    {
        if (statistics.Status == TextureTransportStatisticsStatus.Valid)
        {
            statistics.EnsureValid(sourceIdentity);
            return;
        }

        string reason = string.IsNullOrWhiteSpace(statistics.InvalidReason)
            ? "the decoder did not provide a reason"
            : statistics.InvalidReason;
        if (statistics.Status == TextureTransportStatisticsStatus.UnsupportedEncoding)
        {
            throw new NotSupportedException(
                $"KTX2 texture '{sourceIdentity}' cannot be cooked because authoritative source-resolution " +
                $"transport statistics are required: {reason}");
        }

        throw new InvalidDataException(
            $"KTX2 texture '{sourceIdentity}' cannot be cooked because its source-resolution transport " +
            $"statistics are invalid: {reason}");
    }

    private static byte[] NormalizeLosslesslySupercompressedKtx2(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description)
    {
        var levels = new byte[description.Levels.Length][];
        for (int levelIndex = 0; levelIndex < description.Levels.Length; levelIndex++)
        {
            Ktx2Level level = description.Levels[levelIndex];
            levels[levelIndex] = InflateKtxLevel(
                encoded.Slice(level.Offset, level.Length),
                level,
                description.Supercompression);
        }

        ReadOnlySpan<byte> dfd = description.DataFormatDescriptor.Length == 0
            ? ReadOnlySpan<byte>.Empty
            : encoded.Slice(description.DataFormatDescriptor.Offset, description.DataFormatDescriptor.Length);
        ReadOnlySpan<byte> kvd = description.KeyValueData.Length == 0
            ? ReadOnlySpan<byte>.Empty
            : encoded.Slice(description.KeyValueData.Offset, description.KeyValueData.Length);
        return BuildKtx2(
            description.Width,
            description.Height,
            description.Format,
            description.TypeSize,
            levels,
            dfd,
            kvd);
    }

    private static byte[] InflateKtxLevel(
        ReadOnlySpan<byte> compressed,
        Ktx2Level level,
        uint supercompression)
    {
        if (level.UncompressedLength == 0 || level.UncompressedLength > (ulong)Array.MaxLength)
        {
            throw new InvalidDataException(
                $"Mip {level.Width}x{level.Height} declares unsupported decoded length {level.UncompressedLength}.");
        }

        int expectedLength = checked((int)level.UncompressedLength);
        var destination = GC.AllocateUninitializedArray<byte>(expectedLength);
        int written;
        switch (supercompression)
        {
            case KtxSupercompressionZstandard:
                using (var decompressor = new Decompressor())
                    written = decompressor.Unwrap(compressed, destination);
                break;
            case KtxSupercompressionZlib:
                written = InflateZlib(compressed, destination);
                break;
            default:
                throw new InvalidDataException(
                    $"Supercompression scheme {supercompression} cannot be losslessly inflated by the KTX2 statistics pipeline.");
        }

        if (written != expectedLength)
        {
            throw new InvalidDataException(
                $"{DescribeSupercompression(supercompression)} decompression produced {written} bytes, expected {expectedLength}.");
        }
        return destination;
    }

    private static int InflateZlib(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        using var source = new MemoryStream(compressed.ToArray(), writable: false);
        using var decompressor = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false);
        int written = 0;
        while (written < destination.Length)
        {
            int count = decompressor.Read(destination[written..]);
            if (count == 0)
                break;
            written = checked(written + count);
        }
        if (written == destination.Length && decompressor.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"ZLIB decompression exceeded the declared output length {destination.Length}.");
        }
        return written;
    }

    private static string GetKtxContainerDecoderName(uint supercompression) =>
        supercompression switch
        {
            KtxSupercompressionNone => TextureTransportStatistics.KtxContainerDecoderVersion,
            KtxSupercompressionZstandard =>
                $"{TextureTransportStatistics.KtxContainerDecoderVersion}; {TextureTransportStatistics.ZstdDecoderVersion}",
            KtxSupercompressionZlib =>
                $"{TextureTransportStatistics.KtxContainerDecoderVersion}; {TextureTransportStatistics.ZlibDecoderVersion}",
            _ => TextureTransportStatistics.KtxStatisticsDecoderVersion
        };

    private static string DescribeSupercompression(uint supercompression) =>
        supercompression switch
        {
            KtxSupercompressionNone => "none",
            KtxSupercompressionBasisLz => "BasisLZ",
            KtxSupercompressionZstandard => "Zstandard",
            KtxSupercompressionZlib => "ZLIB",
            _ => $"scheme {supercompression}"
        };

    private static Ktx2Section ReadKtx2Section(
        ulong offset,
        ulong length,
        string sectionName,
        int alignment,
        int minimumOffset,
        int containerLength,
        string sourceName)
    {
        if (length == 0)
        {
            if (offset != 0)
            {
                throw new InvalidDataException(
                    $"KTX2 texture '{sourceName}' {sectionName} has byteOffset {offset} but byteLength 0.");
            }
            return default;
        }
        if (offset < (ulong)minimumOffset)
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' {sectionName} starts at {offset}, inside the header or level index ending at {minimumOffset}.");
        }
        if (offset > (ulong)containerLength || length > (ulong)containerLength - offset)
        {
            ulong end = length > ulong.MaxValue - offset ? ulong.MaxValue : offset + length;
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' {sectionName} range [{offset}, {end}) is outside the {containerLength}-byte container.");
        }
        if (alignment > 1 && offset % (uint)alignment != 0)
        {
            throw new InvalidDataException(
                $"KTX2 texture '{sourceName}' {sectionName} offset {offset} is not aligned to {alignment} bytes.");
        }
        return new Ktx2Section(checked((int)offset), checked((int)length));
    }

    private static void AddRange(
        ICollection<(Ktx2Section Section, string Name)> ranges,
        Ktx2Section section,
        string name)
    {
        if (section.Length != 0)
            ranges.Add((section, name));
    }

    private static void ValidateNoOverlaps(
        List<(Ktx2Section Section, string Name)> ranges,
        string sourceName)
    {
        ranges.Sort(static (left, right) => left.Section.Offset.CompareTo(right.Section.Offset));
        for (int index = 1; index < ranges.Count; index++)
        {
            (Ktx2Section previous, string previousName) = ranges[index - 1];
            (Ktx2Section current, string currentName) = ranges[index];
            int previousEnd = checked(previous.Offset + previous.Length);
            if (current.Offset < previousEnd)
            {
                throw new InvalidDataException(
                    $"KTX2 texture '{sourceName}' {currentName} overlaps {previousName} at byte {current.Offset}.");
            }
        }
    }

    private static int GetMaximumMipCount(int width, int height)
    {
        int maximumDimension = Math.Max(width, height);
        int count = 1;
        while (maximumDimension > 1)
        {
            maximumDimension /= 2;
            count++;
        }
        return count;
    }

    private static bool IsBlockCompressedFormat(uint format) =>
        format is
            Bc1RgbUnorm or Bc1RgbSrgb or Bc1RgbaUnorm or Bc1RgbaSrgb or
            Bc2Unorm or Bc2Srgb or Bc3Unorm or Bc3Srgb or Bc4Unorm or
            Bc5Unorm or Bc6HUfloat or Bc6HSfloat or Bc7Unorm or Bc7Srgb;

    private static int GetRequiredLevelAlignment(uint format) =>
        format switch
        {
            Bc1RgbUnorm or Bc1RgbSrgb or Bc1RgbaUnorm or Bc1RgbaSrgb or Bc4Unorm => 8,
            Bc2Unorm or Bc2Srgb or Bc3Unorm or Bc3Srgb or Bc5Unorm or
                Bc6HUfloat or Bc6HSfloat or Bc7Unorm or Bc7Srgb => 16,
            _ => 4
        };

    private static bool TryGetExpectedLevelLength(
        uint format,
        int width,
        int height,
        out ulong byteLength)
    {
        ulong pixelCount = checked((ulong)(uint)width * (uint)height);
        switch (format)
        {
            case R8Unorm:
                byteLength = pixelCount;
                return true;
            case Rg8Unorm:
                byteLength = checked(pixelCount * 2);
                return true;
            case Rgba8Unorm:
            case Rgba8Srgb:
            case Bgra8Unorm:
            case Bgra8Srgb:
                byteLength = checked(pixelCount * 4);
                return true;
        }

        uint bytesPerBlock = format switch
        {
            Bc1RgbUnorm or Bc1RgbSrgb or Bc1RgbaUnorm or Bc1RgbaSrgb or Bc4Unorm => 8,
            Bc2Unorm or Bc2Srgb or Bc3Unorm or Bc3Srgb or Bc5Unorm or
                Bc6HUfloat or Bc6HSfloat or Bc7Unorm or Bc7Srgb => 16,
            _ => 0
        };
        if (bytesPerBlock == 0)
        {
            byteLength = 0;
            return false;
        }

        ulong blockWidth = ((ulong)(uint)width + 3) / 4;
        ulong blockHeight = ((ulong)(uint)height + 3) / 4;
        byteLength = checked(blockWidth * blockHeight * bytesPerBlock);
        return true;
    }

    private static bool TryDecodeRawKtx(
        ReadOnlySpan<byte> level,
        Ktx2Description description,
        ulong sourceHash,
        TextureSemantic semantic,
        string decoder,
        out TextureTransportImage image)
    {
        TextureColorSpace colorSpace = ResolveKtxColorSpace(description.Format, TextureColorSpace.Linear);
        int pixelCount = checked(description.Width * description.Height);
        byte[] rgba;
        switch (description.Format)
        {
            case Rgba8Unorm:
            case Rgba8Srgb:
                int rgbaBytes = checked(pixelCount * 4);
                if (level.Length < rgbaBytes)
                    throw new InvalidDataException($"RGBA8 level contains {level.Length} bytes, expected at least {rgbaBytes}.");
                rgba = level[..rgbaBytes].ToArray();
                break;
            case Bgra8Unorm:
            case Bgra8Srgb:
                int bgraBytes = checked(pixelCount * 4);
                if (level.Length < bgraBytes)
                    throw new InvalidDataException($"BGRA8 level contains {level.Length} bytes, expected at least {bgraBytes}.");
                rgba = new byte[bgraBytes];
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    int offset = pixel * 4;
                    rgba[offset] = level[offset + 2];
                    rgba[offset + 1] = level[offset + 1];
                    rgba[offset + 2] = level[offset];
                    rgba[offset + 3] = level[offset + 3];
                }
                break;
            case R8Unorm:
                if (level.Length < pixelCount)
                    throw new InvalidDataException($"R8 level contains {level.Length} bytes, expected at least {pixelCount}.");
                rgba = new byte[checked(pixelCount * 4)];
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    int offset = pixel * 4;
                    rgba[offset] = level[pixel];
                    rgba[offset + 1] = level[pixel];
                    rgba[offset + 2] = level[pixel];
                    rgba[offset + 3] = 255;
                }
                break;
            case Rg8Unorm:
                int rgBytes = checked(pixelCount * 2);
                if (level.Length < rgBytes)
                    throw new InvalidDataException($"RG8 level contains {level.Length} bytes, expected at least {rgBytes}.");
                rgba = new byte[checked(pixelCount * 4)];
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    int sourceOffset = pixel * 2;
                    int targetOffset = pixel * 4;
                    rgba[targetOffset] = level[sourceOffset];
                    rgba[targetOffset + 1] = level[sourceOffset + 1];
                    rgba[targetOffset + 2] = 0;
                    rgba[targetOffset + 3] = 255;
                }
                break;
            default:
                image = null!;
                return false;
        }

        image = TextureTransportImage.FromRgba8(
            rgba,
            description.Width,
            description.Height,
            colorSpace,
            semantic,
            sourceHash,
            $"{TextureTransportStatistics.KtxRawDecoderVersion}; {decoder}");
        return true;
    }

    private static bool TryDecodeBcKtx(
        ReadOnlySpan<byte> level,
        Ktx2Description description,
        ulong sourceHash,
        TextureSemantic semantic,
        string decoder,
        out TextureTransportImage image)
    {
        if (!TryResolveBcFormat(description.Format, out CompressionFormat format, out bool hdr))
        {
            image = null!;
            return false;
        }

        var bcDecoder = new BcDecoder();
        byte[] encodedLevel = level.ToArray();
        if (hdr)
        {
            ColorRgbFloat[] decoded = bcDecoder.DecodeRawHdr(encodedLevel, description.Width, description.Height, format);
            int expectedPixelCount = checked(description.Width * description.Height);
            if (decoded.Length != expectedPixelCount)
            {
                throw new InvalidDataException(
                    $"BC decoder produced {decoded.Length} HDR pixels, expected {expectedPixelCount}.");
            }
            var rgba = new float[checked(decoded.Length * 4)];
            for (int pixel = 0; pixel < decoded.Length; pixel++)
            {
                int offset = pixel * 4;
                rgba[offset] = decoded[pixel].r;
                rgba[offset + 1] = decoded[pixel].g;
                rgba[offset + 2] = decoded[pixel].b;
                rgba[offset + 3] = 1f;
            }
            image = TextureTransportImage.FromRgbaFloat(
                rgba,
                description.Width,
                description.Height,
                TextureColorSpace.HdrLinear,
                semantic,
                sourceHash,
                $"{TextureTransportStatistics.BcDecoderVersion}; {decoder}");
            return true;
        }

        ColorRgba32[] colors = bcDecoder.DecodeRaw(encodedLevel, description.Width, description.Height, format);
        int expectedLdrPixelCount = checked(description.Width * description.Height);
        if (colors.Length != expectedLdrPixelCount)
        {
            throw new InvalidDataException(
                $"BC decoder produced {colors.Length} pixels, expected {expectedLdrPixelCount}.");
        }
        var pixels = new byte[checked(colors.Length * 4)];
        for (int pixel = 0; pixel < colors.Length; pixel++)
        {
            int offset = pixel * 4;
            pixels[offset] = colors[pixel].r;
            pixels[offset + 1] = colors[pixel].g;
            pixels[offset + 2] = colors[pixel].b;
            pixels[offset + 3] = colors[pixel].a;
        }
        image = TextureTransportImage.FromRgba8(
            pixels,
            description.Width,
            description.Height,
            ResolveKtxColorSpace(description.Format, TextureColorSpace.Linear),
            semantic,
            sourceHash,
            $"{TextureTransportStatistics.BcDecoderVersion}; {decoder}");
        return true;
    }

    private static bool TryResolveBcFormat(uint format, out CompressionFormat compressionFormat, out bool hdr)
    {
        hdr = false;
        compressionFormat = format switch
        {
            Bc1RgbUnorm or Bc1RgbSrgb => CompressionFormat.Bc1,
            Bc1RgbaUnorm or Bc1RgbaSrgb => CompressionFormat.Bc1WithAlpha,
            Bc2Unorm or Bc2Srgb => CompressionFormat.Bc2,
            Bc3Unorm or Bc3Srgb => CompressionFormat.Bc3,
            Bc4Unorm => CompressionFormat.Bc4,
            Bc5Unorm => CompressionFormat.Bc5,
            Bc6HUfloat => CompressionFormat.Bc6U,
            Bc6HSfloat => CompressionFormat.Bc6S,
            Bc7Unorm or Bc7Srgb => CompressionFormat.Bc7,
            _ => CompressionFormat.Unknown
        };
        hdr = format is Bc6HUfloat or Bc6HSfloat;
        return compressionFormat != CompressionFormat.Unknown;
    }

    private static TextureColorSpace ResolveKtxColorSpace(uint format, TextureColorSpace fallback) =>
        format switch
        {
            Rgba8Srgb or Bgra8Srgb or Bc1RgbSrgb or Bc1RgbaSrgb or Bc2Srgb or Bc3Srgb or Bc7Srgb =>
                TextureColorSpace.Srgb,
            Bc6HUfloat or Bc6HSfloat => TextureColorSpace.HdrLinear,
            R8Unorm or Rg8Unorm or Rgba8Unorm or Bgra8Unorm or
                Bc1RgbUnorm or Bc1RgbaUnorm or Bc2Unorm or Bc3Unorm or
                Bc4Unorm or Bc5Unorm or Bc7Unorm => TextureColorSpace.Linear,
            _ => fallback
        };

    private static Njulf.Core.Math.Vector4? GetCompatibilityLinearAverage(
        TextureTransportStatistics statistics) =>
        statistics.TryGetLinearMean(out Njulf.Core.Math.Vector4 mean) ? mean : null;

    private static byte[] BuildKtx2(int width, int height, uint format, IReadOnlyList<byte[]> levels)
        => BuildKtx2(
            width,
            height,
            format,
            typeSize: 1,
            levels,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty);

    private static byte[] BuildKtx2(
        int width,
        int height,
        uint format,
        uint typeSize,
        IReadOnlyList<byte[]> levels,
        ReadOnlySpan<byte> dataFormatDescriptor,
        ReadOnlySpan<byte> keyValueData)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "KTX2 dimensions must be positive.");
        if (typeSize == 0)
            throw new ArgumentOutOfRangeException(nameof(typeSize), "KTX2 typeSize must be positive.");
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count <= 0 || levels.Count > GetMaximumMipCount(width, height))
            throw new ArgumentOutOfRangeException(nameof(levels), "KTX2 mip count is invalid for the texture dimensions.");
        if ((dataFormatDescriptor.Length & 3) != 0 || (keyValueData.Length & 3) != 0)
            throw new ArgumentException("KTX2 metadata sections must be 4-byte aligned.");

        int indexEnd = checked(80 + levels.Count * 24);
        int cursor = indexEnd;
        int dfdOffset = 0;
        if (!dataFormatDescriptor.IsEmpty)
        {
            cursor = Align(cursor, 4);
            dfdOffset = cursor;
            cursor = checked(cursor + dataFormatDescriptor.Length);
        }
        int kvdOffset = 0;
        if (!keyValueData.IsEmpty)
        {
            cursor = Align(cursor, 4);
            kvdOffset = cursor;
            cursor = checked(cursor + keyValueData.Length);
        }

        int requiredLevelAlignment = GetRequiredLevelAlignment(format);
        var offsets = new int[levels.Count];
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            if (levels[i] is not { Length: > 0 })
                throw new ArgumentException($"KTX2 mip {i} is empty.", nameof(levels));
            cursor = Align(cursor, requiredLevelAlignment);
            offsets[i] = cursor;
            cursor = checked(cursor + levels[i].Length);
        }

        var result = new byte[cursor];
        Ktx2Identifier.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), format);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), typeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), checked((uint)levels.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48, 4), checked((uint)dfdOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52, 4), checked((uint)dataFormatDescriptor.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56, 4), checked((uint)kvdOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60, 4), checked((uint)keyValueData.Length));
        for (int i = 0; i < levels.Count; i++)
        {
            int entry = 80 + i * 24;
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry, 8), checked((ulong)offsets[i]));
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry + 8, 8), checked((ulong)levels[i].Length));
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry + 16, 8), checked((ulong)levels[i].Length));
            levels[i].CopyTo(result, offsets[i]);
        }
        dataFormatDescriptor.CopyTo(result.AsSpan(dfdOffset, dataFormatDescriptor.Length));
        keyValueData.CopyTo(result.AsSpan(kvdOffset, keyValueData.Length));
        return result;
    }

    private static byte[] ResizeBox(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, bool srgb)
    {
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            int y0 = y * sourceHeight / targetHeight;
            int y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / targetHeight);
            for (int x = 0; x < targetWidth; x++)
            {
                int x0 = x * sourceWidth / targetWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / targetWidth);
                double r = 0, g = 0, b = 0, a = 0;
                int samples = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int offset = (sy * sourceWidth + sx) * 4;
                        r += srgb ? SrgbToLinear(source[offset]) : source[offset] / 255.0;
                        g += srgb ? SrgbToLinear(source[offset + 1]) : source[offset + 1] / 255.0;
                        b += srgb ? SrgbToLinear(source[offset + 2]) : source[offset + 2] / 255.0;
                        a += source[offset + 3] / 255.0;
                        samples++;
                    }
                int destination = (y * targetWidth + x) * 4;
                target[destination] = ToByte(srgb ? LinearToSrgb(r / samples) : r / samples);
                target[destination + 1] = ToByte(srgb ? LinearToSrgb(g / samples) : g / samples);
                target[destination + 2] = ToByte(srgb ? LinearToSrgb(b / samples) : b / samples);
                target[destination + 3] = ToByte(a / samples);
            }
        }
        return target;
    }

    private static byte[] ResizeLdr(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, TextureCookOptions options) =>
        options.Semantic == TextureSemantic.Normal
            ? ResizeNormalBox(source, sourceWidth, sourceHeight, targetWidth, targetHeight)
            : ResizeBox(source, sourceWidth, sourceHeight, targetWidth, targetHeight, options.ColorSpace == TextureColorSpace.Srgb);

    private static byte[] ResizeNormalBox(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
            for (int x = 0; x < targetWidth; x++)
            {
                int x0 = x * sourceWidth / targetWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / targetWidth);
                int y0 = y * sourceHeight / targetHeight;
                int y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / targetHeight);
                double nx = 0, ny = 0, nz = 0, alpha = 0;
                int samples = 0;
                for (int sy = y0; sy < Math.Min(y1, sourceHeight); sy++)
                    for (int sx = x0; sx < Math.Min(x1, sourceWidth); sx++)
                    {
                        int offset = (sy * sourceWidth + sx) * 4;
                        double vx = source[offset] / 127.5 - 1.0;
                        double vy = source[offset + 1] / 127.5 - 1.0;
                        double vz = source[offset + 2] / 127.5 - 1.0;
                        double length = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (length > 1e-12)
                        {
                            nx += vx / length;
                            ny += vy / length;
                            nz += vz / length;
                        }
                        alpha += source[offset + 3] / 255.0;
                        samples++;
                    }
                double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (normalLength <= 1e-12)
                {
                    nx = 0;
                    ny = 0;
                    nz = 1;
                }
                else
                {
                    nx /= normalLength;
                    ny /= normalLength;
                    nz /= normalLength;
                }
                int destination = (y * targetWidth + x) * 4;
                target[destination] = ToByte(nx * 0.5 + 0.5);
                target[destination + 1] = ToByte(ny * 0.5 + 0.5);
                target[destination + 2] = ToByte(nz * 0.5 + 0.5);
                target[destination + 3] = ToByte(alpha / Math.Max(1, samples));
            }
        return target;
    }

    private static float[] ResizeBox(float[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var target = new float[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            int y0 = y * sourceHeight / targetHeight;
            int y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / targetHeight);
            for (int x = 0; x < targetWidth; x++)
            {
                int x0 = x * sourceWidth / targetWidth;
                int x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / targetWidth);
                int samples = (x1 - x0) * (y1 - y0);
                int destination = (y * targetWidth + x) * 4;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int offset = (sy * sourceWidth + sx) * 4;
                        for (int channel = 0; channel < 4; channel++)
                            target[destination + channel] += source[offset + channel];
                    }
                for (int channel = 0; channel < 4; channel++)
                    target[destination + channel] /= samples;
            }
        }
        return target;
    }

    private static double SrgbToLinear(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
    private static double LinearToSrgb(double value) => value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
    private static int Align(int value, int alignment) => checked((value + alignment - 1) & ~(alignment - 1));

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        AssetArtifactFileIo.WriteAtomic(
            path,
            bytes,
            checked((int)CookedAssetReader.MaximumAssetBytes),
            "Cooked texture");
    }

    private readonly record struct Ktx2Description(
        int Width,
        int Height,
        int MipCount,
        uint Format,
        uint TypeSize,
        uint Supercompression,
        Ktx2Level[] Levels,
        Ktx2Section DataFormatDescriptor,
        Ktx2Section KeyValueData);

    private readonly record struct Ktx2Level(
        int Offset,
        int Length,
        ulong UncompressedLength,
        int Width,
        int Height)
    {
        public Ktx2Section Payload => new(Offset, Length);
    }

    private readonly record struct Ktx2Section(int Offset, int Length);

    private readonly record struct Ktx2Analysis(
        TextureTransportStatistics Statistics,
        TextureTransportImage? Image,
        byte[]? DecodedBasisRgba8);

    private readonly record struct DecodedBasisTexture(
        byte[] Rgba8,
        TextureTransportImage Image);

}
