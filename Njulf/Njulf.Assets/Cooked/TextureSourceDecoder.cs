using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using Ktx;
using Njulf.Graphics;
using StbImageSharp;
using ZstdSharp;

namespace Njulf.Assets.Cooked;

/// <summary>Shared bounded source decoding and texture-container inspection used by loading and cooking.</summary>
public static class TextureSourceDecoder
{
    /// <summary>Default encoded input limit for runtime analysis: 64 MiB.</summary>
    public const int DefaultMaximumRuntimeTransportEncodedBytes = 64 * 1024 * 1024;
    /// <summary>Default decoded pixel limit for runtime analysis: 2048 squared.</summary>
    public const long DefaultMaximumRuntimeTransportPixels = 2048L * 2048L;

    internal static ReadOnlySpan<byte> Ktx2Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
    internal const uint KtxSupercompressionNone = 0;
    internal const uint KtxSupercompressionBasisLz = 1;
    internal const uint KtxSupercompressionZstandard = 2;
    internal const uint KtxSupercompressionZlib = 3;
    internal const byte KtxTransferLinear = 1;
    internal const byte KtxTransferSrgb = 2;
    internal const uint R8Unorm = 9;
    internal const uint Rg8Unorm = 16;
    internal const uint Rgba8Unorm = 37;
    internal const uint Rgba8Srgb = 43;
    internal const uint Bgra8Unorm = 44;
    internal const uint Bgra8Srgb = 50;
    internal const uint Bc1RgbUnorm = 131;
    internal const uint Bc1RgbSrgb = 132;
    internal const uint Bc1RgbaUnorm = 133;
    internal const uint Bc1RgbaSrgb = 134;
    internal const uint Bc2Unorm = 135;
    internal const uint Bc2Srgb = 136;
    internal const uint Bc3Unorm = 137;
    internal const uint Bc3Srgb = 138;
    internal const uint Bc4Unorm = 139;
    internal const uint Bc5Unorm = 141;
    internal const uint Bc6HUfloat = 143;
    internal const uint Bc6HSfloat = 144;
    internal const uint Bc7Unorm = 145;
    internal const uint Bc7Srgb = 146;

    /// <summary>
    /// Computes source-resolution transport statistics without resizing,
    /// compressing, or writing files. Unsupported KTX2/Basis encodings and
    /// malformed decoded pixels return explicit invalid statistics.
    /// </summary>
    public static TextureTransportStatistics AnalyzeTransportStatistics(
        ModelTextureSource source,
        TextureDecodeOptions options)
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
        TextureDecodeOptions options) =>
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
        TextureDecodeOptions options,
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
            options.ForceHdr;
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

    internal static TextureTransportSourceAnalysis InvalidSourceAnalysis(
        TextureTransportStatisticsStatus status,
        string reason,
        ulong sourceHash,
        TextureDecodeOptions options,
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

    internal static void EnsureRuntimeTransportPixelBudget(
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

    /// <summary>Reads KTX2 container dimensions, mip count and numeric Vulkan format without GPU allocation.</summary>
    public static (int Width, int Height, int MipCount, uint Format) Inspect(ReadOnlySpan<byte> data, string sourceName)
    {
        Ktx2Description value = ParseKtx2(data, sourceName);
        return (value.Width, value.Height, value.MipCount, value.Format);
    }

    internal static byte[] ReadSource(ModelTextureSource source)
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

    internal static string ResolveSourceIdentity(ModelTextureSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.CacheIdentity))
            return source.CacheIdentity;
        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return Path.GetFullPath(source.FilePath);
        if (!string.IsNullOrWhiteSpace(source.DebugName))
            return source.DebugName;
        return "UnnamedWebPTexture";
    }

    internal static bool IsKtx2(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..12].SequenceEqual(Ktx2Identifier);

    internal static Ktx2Description ParseKtx2(ReadOnlySpan<byte> data, string sourceName)
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

    internal static Ktx2Analysis AnalyzeKtx2(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description,
        ulong sourceHash,
        TextureDecodeOptions options)
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

    internal static unsafe DecodedBasisTexture DecodeBasisKtx2(
        ReadOnlySpan<byte> encoded,
        Ktx2Description description,
        ulong sourceHash,
        TextureDecodeOptions options)
    {
        if (options.ColorSpace == TextureColorSpace.HdrLinear ||
            options.Semantic == TextureSemantic.Hdr ||
            options.ForceHdr)
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

    internal static TextureColorSpace ResolveBasisColorSpace(
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

    internal static void EnsureBasisTranscoderPlatform()
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

    internal static bool IsNativeBasisLoadFailure(Exception exception)
    {
        if (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            return true;
        return exception is TypeInitializationException { InnerException: Exception innerException } &&
               IsNativeBasisLoadFailure(innerException);
    }

    internal static PlatformNotSupportedException CreateBasisCapabilityException(Exception innerException) =>
        new(
            $"{TextureTransportStatistics.BasisDecoderVersion} native libktx is unavailable for " +
            $"{RuntimeInformation.RuntimeIdentifier}. Restore the pinned package runtime asset and run the cooker " +
            "in a win-x64 or linux-x64 process.",
            innerException);

    internal static void EnsureBasisResult(Ktx2.ErrorCode result, string operation)
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

    internal static byte[] InflateKtxLevel(
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

    internal static int InflateZlib(ReadOnlySpan<byte> compressed, Span<byte> destination)
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

    internal static string GetKtxContainerDecoderName(uint supercompression) =>
        supercompression switch
        {
            KtxSupercompressionNone => TextureTransportStatistics.KtxContainerDecoderVersion,
            KtxSupercompressionZstandard =>
                $"{TextureTransportStatistics.KtxContainerDecoderVersion}; {TextureTransportStatistics.ZstdDecoderVersion}",
            KtxSupercompressionZlib =>
                $"{TextureTransportStatistics.KtxContainerDecoderVersion}; {TextureTransportStatistics.ZlibDecoderVersion}",
            _ => TextureTransportStatistics.KtxStatisticsDecoderVersion
        };

    internal static string DescribeSupercompression(uint supercompression) =>
        supercompression switch
        {
            KtxSupercompressionNone => "none",
            KtxSupercompressionBasisLz => "BasisLZ",
            KtxSupercompressionZstandard => "Zstandard",
            KtxSupercompressionZlib => "ZLIB",
            _ => $"scheme {supercompression}"
        };

    internal static Ktx2Section ReadKtx2Section(
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

    internal static void AddRange(
        ICollection<(Ktx2Section Section, string Name)> ranges,
        Ktx2Section section,
        string name)
    {
        if (section.Length != 0)
            ranges.Add((section, name));
    }

    internal static void ValidateNoOverlaps(
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

    internal static int GetMaximumMipCount(int width, int height)
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

    internal static bool IsBlockCompressedFormat(uint format) =>
        format is
            Bc1RgbUnorm or Bc1RgbSrgb or Bc1RgbaUnorm or Bc1RgbaSrgb or
            Bc2Unorm or Bc2Srgb or Bc3Unorm or Bc3Srgb or Bc4Unorm or
            Bc5Unorm or Bc6HUfloat or Bc6HSfloat or Bc7Unorm or Bc7Srgb;

    internal static int GetRequiredLevelAlignment(uint format) =>
        format switch
        {
            Bc1RgbUnorm or Bc1RgbSrgb or Bc1RgbaUnorm or Bc1RgbaSrgb or Bc4Unorm => 8,
            Bc2Unorm or Bc2Srgb or Bc3Unorm or Bc3Srgb or Bc5Unorm or
                Bc6HUfloat or Bc6HSfloat or Bc7Unorm or Bc7Srgb => 16,
            _ => 4
        };

    internal static bool TryGetExpectedLevelLength(
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

    internal static bool TryDecodeRawKtx(
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

    internal static bool TryDecodeBcKtx(
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

    internal static bool TryResolveBcFormat(uint format, out CompressionFormat compressionFormat, out bool hdr)
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

    internal static TextureColorSpace ResolveKtxColorSpace(uint format, TextureColorSpace fallback) =>
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

    internal readonly record struct Ktx2Description(
        int Width,
        int Height,
        int MipCount,
        uint Format,
        uint TypeSize,
        uint Supercompression,
        Ktx2Level[] Levels,
        Ktx2Section DataFormatDescriptor,
        Ktx2Section KeyValueData);

    internal readonly record struct Ktx2Level(
        int Offset,
        int Length,
        ulong UncompressedLength,
        int Width,
        int Height)
    {
        public Ktx2Section Payload => new(Offset, Length);
    }

    internal readonly record struct Ktx2Section(int Offset, int Length);

    internal readonly record struct Ktx2Analysis(
        TextureTransportStatistics Statistics,
        TextureTransportImage? Image,
        byte[]? DecodedBasisRgba8);

    internal readonly record struct DecodedBasisTexture(
        byte[] Rgba8,
        TextureTransportImage Image);
}
