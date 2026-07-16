using System.Buffers.Binary;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using StbImageSharp;

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
    TextureSemantic Semantic = TextureSemantic.Color);

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
    bool PassedThrough);

public interface ITextureCooker
{
    CookedTextureReport Cook(ModelTextureSource source, string ktx2Path, TextureCookOptions options);
}

public sealed class TextureCooker : ITextureCooker
{
    private static ReadOnlySpan<byte> Ktx2Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
    private const uint Rgba8Unorm = 37;
    private const uint Rgba8Srgb = 43;
    private const uint Bc4Unorm = 139;
    private const uint Bc5Unorm = 141;
    private const uint Bc6HUfloat = 143;
    private const uint Bc7Unorm = 145;
    private const uint Bc7Srgb = 146;

    public CookedTextureReport Cook(ModelTextureSource source, string ktx2Path, TextureCookOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(ktx2Path);
        byte[] encoded = ReadSource(source);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ktx2Path))!);
        if (source.ContainerKind == TextureContainerKind.Ktx2 || IsKtx2(encoded))
        {
            Ktx2Description description = ParseKtx2(encoded, source.CacheIdentity);
            WriteAtomic(ktx2Path, encoded);
            return new CookedTextureReport(source.CacheIdentity, description.Width, description.Height, description.Width, description.Height, description.Format, description.MipCount, encoded.Length, encoded.Length, true);
        }

        if (options.ColorSpace == TextureColorSpace.HdrLinear || options.Semantic == TextureSemantic.Hdr || options.TargetFormatPolicy == TextureTargetFormatPolicy.Bc6H)
            return CookHdr(encoded, source, ktx2Path, options);

        ImageResult image;
        try { image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) { throw new InvalidDataException($"Texture '{source.CacheIdentity}' could not be decoded for cooking.", ex); }
        int width = image.Width;
        int height = image.Height;
        int targetWidth = width;
        int targetHeight = height;
        if (options.MaxDimension > 0 && Math.Max(width, height) > options.MaxDimension)
        {
            double scale = options.MaxDimension / (double)Math.Max(width, height);
            targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        }
        byte[] level = targetWidth == width && targetHeight == height
            ? image.Data
            : ResizeLdr(image.Data, width, height, targetWidth, targetHeight, options);
        var levels = new List<byte[]> { level };
        int levelWidth = targetWidth;
        int levelHeight = targetHeight;
        while (levelWidth > 1 || levelHeight > 1)
        {
            int nextWidth = Math.Max(1, levelWidth / 2);
            int nextHeight = Math.Max(1, levelHeight / 2);
            level = ResizeLdr(level, levelWidth, levelHeight, nextWidth, nextHeight, options);
            levels.Add(level);
            levelWidth = nextWidth;
            levelHeight = nextHeight;
        }
        (uint format, CompressionFormat? bcFormat) = ResolveFormat(options);
        IReadOnlyList<byte[]> outputLevels = bcFormat.HasValue ? EncodeBc(levels, targetWidth, targetHeight, bcFormat.Value) : levels;
        byte[] ktx = BuildKtx2(targetWidth, targetHeight, format, outputLevels);
        WriteAtomic(ktx2Path, ktx);
        return new CookedTextureReport(source.CacheIdentity, width, height, targetWidth, targetHeight, format, levels.Count, encoded.Length, ktx.Length, false);
    }

    private static CookedTextureReport CookHdr(byte[] encoded, ModelTextureSource source, string ktx2Path, TextureCookOptions options)
    {
        ImageResultFloat image;
        try { image = ImageResultFloat.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha); }
        catch (Exception ex) { throw new InvalidDataException($"HDR texture '{source.CacheIdentity}' could not be decoded for cooking.", ex); }
        int targetWidth = image.Width;
        int targetHeight = image.Height;
        if (options.MaxDimension > 0 && Math.Max(targetWidth, targetHeight) > options.MaxDimension)
        {
            double scale = options.MaxDimension / (double)Math.Max(targetWidth, targetHeight);
            targetWidth = Math.Max(1, (int)Math.Round(targetWidth * scale));
            targetHeight = Math.Max(1, (int)Math.Round(targetHeight * scale));
        }
        float[] level = targetWidth == image.Width && targetHeight == image.Height
            ? image.Data
            : ResizeBox(image.Data, image.Width, image.Height, targetWidth, targetHeight);
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
        return new CookedTextureReport(source.CacheIdentity, image.Width, image.Height, targetWidth, targetHeight, Bc6HUfloat, compressed.Length, encoded.Length, ktx.Length, false);
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
            return source.Bytes.ToArray();
        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return File.ReadAllBytes(Path.GetFullPath(source.FilePath));
        throw new InvalidDataException($"Texture '{source.CacheIdentity}' has neither encoded bytes nor a file path.");
    }

    private static bool IsKtx2(ReadOnlySpan<byte> data) => data.Length >= 12 && data[..12].SequenceEqual(Ktx2Identifier);

    private static Ktx2Description ParseKtx2(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length < 80 || !IsKtx2(data))
            throw new InvalidDataException($"Texture '{sourceName}' is not a valid KTX2 container.");
        uint format = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        int width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..24]));
        int height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[24..28]));
        int mips = checked((int)Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(data[40..44])));
        uint supercompression = BinaryPrimitives.ReadUInt32LittleEndian(data[44..48]);
        if (width <= 0 || height <= 0 || format == 0 || supercompression != 0)
            throw new NotSupportedException($"KTX2 texture '{sourceName}' must be a non-supercompressed, explicitly formatted 2D texture.");
        if (data.Length < 80 + mips * 24)
            throw new InvalidDataException($"KTX2 texture '{sourceName}' has a truncated level index.");
        return new Ktx2Description(width, height, mips, format);
    }

    private static byte[] BuildKtx2(int width, int height, uint format, IReadOnlyList<byte[]> levels)
    {
        int indexEnd = checked(80 + levels.Count * 24);
        int cursor = Align(indexEnd, 8);
        var offsets = new int[levels.Count];
        for (int i = 0; i < levels.Count; i++)
        {
            offsets[i] = cursor;
            cursor = checked(Align(cursor + levels[i].Length, 8));
        }
        var result = new byte[cursor];
        Ktx2Identifier.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), format);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), checked((uint)levels.Count));
        for (int i = 0; i < levels.Count; i++)
        {
            int entry = 80 + i * 24;
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry, 8), checked((ulong)offsets[i]));
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry + 8, 8), checked((ulong)levels[i].Length));
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry + 16, 8), checked((ulong)levels[i].Length));
            levels[i].CopyTo(result, offsets[i]);
        }
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
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, fullPath, overwrite: true);
    }

    private readonly record struct Ktx2Description(int Width, int Height, int MipCount, uint Format);
}
