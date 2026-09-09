using static Njulf.Assets.Cooked.TextureSourceDecoder;
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

public sealed record TextureCookOptions(
    int MaxDimension = 2048,
    TextureColorSpace ColorSpace = TextureColorSpace.Srgb,
    TextureMipFilter MipFilter = TextureMipFilter.Box,
    TextureTargetFormatPolicy TargetFormatPolicy = TextureTargetFormatPolicy.AutoBc,
    TextureSemantic Semantic = TextureSemantic.Color,
    bool PreserveAlphaCoverage = false,
    float AlphaCutoff = 0.5f)
{
    /// <summary>Preserves the cooker's source interpretation, including BC6H's forced HDR decoding.</summary>
    public TextureDecodeOptions ToDecodeOptions() => new(ColorSpace, Semantic, TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H);
}

public interface ITextureCooker
{
    CookedTextureReport Cook(ModelTextureSource source, string ktx2Path, TextureCookOptions options);
}

public sealed class TextureCooker : ITextureCooker
{

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
            Ktx2Analysis analysis = AnalyzeKtx2(encoded, description, sourceHash, options.ToDecodeOptions());
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

}
