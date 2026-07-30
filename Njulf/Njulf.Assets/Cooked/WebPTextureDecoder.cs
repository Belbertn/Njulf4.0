using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Validated, bounded libwebp decode used by cooking, transport analysis, and
/// uncooked runtime texture upload. Animated WebP is intentionally rejected:
/// glTF images and Njulf texture slots represent one immutable image.
/// </summary>
public static class WebPTextureDecoder
{
    public const int DefaultMaximumEncodedBytes = 64 * 1024 * 1024;
    public const long DefaultMaximumDecodedPixels = 4096L * 4096L;
    public const string DecoderVersion =
        "libwebp/1.6.0 (Imazen.WebP.NativeRuntime.All/1.6.1 RGBA)";

    private const int RequiredDecoderVersion = 0x010600;
    private const uint WebPMaximumDimension = 16_383;

    public static bool HasWebPSignature(ReadOnlySpan<byte> encoded) =>
        encoded.Length >= 12 &&
        encoded[..4].SequenceEqual("RIFF"u8) &&
        encoded.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static bool FileHasWebPSignature(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(
            Path.GetFullPath(filePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: header.Length,
            FileOptions.SequentialScan);
        int totalRead = 0;
        while (totalRead < header.Length)
        {
            int read = stream.Read(header[totalRead..]);
            if (read == 0)
                break;
            totalRead += read;
        }

        return HasWebPSignature(header[..totalRead]);
    }

    public static bool IsDeclaredWebP(ModelTextureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ContainerKind == TextureContainerKind.WebP ||
               string.Equals(source.MimeType, "image/webp", StringComparison.OrdinalIgnoreCase) ||
               HasWebPExtension(source.FilePath) ||
               HasWebPExtension(source.DebugName);
    }

    public static bool IsDeclaredWebP(ModelTextureSource source, ReadOnlySpan<byte> encoded)
    {
        ArgumentNullException.ThrowIfNull(source);
        return IsDeclaredWebP(source) ||
               HasWebPSignature(encoded);
    }

    /// <summary>
    /// Reads an encoded WebP file without ever allocating beyond the admitted
    /// byte limit. Length changes during the read fail closed.
    /// </summary>
    public static byte[] ReadBoundedFile(
        string filePath,
        string sourceIdentity,
        int maximumEncodedBytes = DefaultMaximumEncodedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (maximumEncodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));

        string fullPath = Path.GetFullPath(filePath);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        long declaredLength = stream.Length;
        if (declaredLength <= 0)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' is empty.");
        }
        if (declaredLength > maximumEncodedBytes)
        {
            throw new NotSupportedException(
                $"WebP texture '{sourceIdentity}' contains {declaredLength} encoded bytes, exceeding " +
                $"the decode limit {maximumEncodedBytes}.");
        }

        byte[] encoded = GC.AllocateUninitializedArray<byte>(
            checked((int)declaredLength));
        int totalRead = 0;
        while (totalRead < encoded.Length)
        {
            int read = stream.Read(encoded, totalRead, encoded.Length - totalRead);
            if (read == 0)
            {
                throw new IOException(
                    $"WebP texture '{sourceIdentity}' changed during its bounded read: " +
                    $"{declaredLength} bytes were admitted but only {totalRead} remained.");
            }

            totalRead += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new IOException(
                $"WebP texture '{sourceIdentity}' grew beyond its admitted " +
                $"{declaredLength}-byte length during the bounded read.");
        }

        return encoded;
    }

    public static unsafe WebPDecodedImage DecodeRgba8(
        byte[] encoded,
        string sourceIdentity,
        int maximumEncodedBytes = DefaultMaximumEncodedBytes,
        long maximumPixels = DefaultMaximumDecodedPixels)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        if (maximumEncodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEncodedBytes));
        if (maximumPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        if (encoded.Length > maximumEncodedBytes)
        {
            throw new NotSupportedException(
                $"WebP texture '{sourceIdentity}' contains {encoded.Length} encoded bytes, exceeding " +
                $"the decode limit {maximumEncodedBytes}.");
        }

        WebPContainerFeatures container = ValidateContainer(encoded, sourceIdentity);
        ValidateNativeDecoder(sourceIdentity);

        int width = 0;
        int height = 0;
        try
        {
            fixed (byte* input = encoded)
            {
                if (NativeMethods.WebPGetInfo(
                        input,
                        checked((nuint)encoded.Length),
                        &width,
                        &height) == 0)
                {
                    throw new InvalidDataException(
                        $"WebP texture '{sourceIdentity}' has an invalid or unsupported bitstream.");
                }
            }
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw CreateNativeRuntimeException(sourceIdentity, ex);
        }

        if (width <= 0 ||
            height <= 0 ||
            (uint)width > WebPMaximumDimension ||
            (uint)height > WebPMaximumDimension)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' has invalid dimensions {width}x{height}.");
        }
        if (container.ExtendedWidth.HasValue &&
            (container.ExtendedWidth.Value != width ||
             container.ExtendedHeight!.Value != height))
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' has inconsistent VP8X and bitstream dimensions.");
        }

        long pixels;
        long decodedBytes;
        try
        {
            pixels = checked((long)width * height);
            decodedBytes = checked(pixels * 4L);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' dimensions overflow the RGBA8 decode layout.",
                ex);
        }
        if (pixels > maximumPixels)
        {
            throw new NotSupportedException(
                $"WebP texture '{sourceIdentity}' contains {pixels} decoded pixels, exceeding the " +
                $"decode limit {maximumPixels}.");
        }
        if (decodedBytes > Array.MaxLength)
        {
            throw new NotSupportedException(
                $"WebP texture '{sourceIdentity}' requires {decodedBytes} decoded RGBA8 bytes, exceeding " +
                "the managed-array limit.");
        }

        byte[] rgba = GC.AllocateUninitializedArray<byte>(checked((int)decodedBytes));
        try
        {
            fixed (byte* input = encoded)
            fixed (byte* output = rgba)
            {
                nint result = NativeMethods.WebPDecodeRGBAInto(
                    input,
                    checked((nuint)encoded.Length),
                    output,
                    checked((nuint)rgba.Length),
                    checked(width * 4));
                if (result != (nint)output)
                {
                    throw new InvalidDataException(
                        $"WebP texture '{sourceIdentity}' could not be decoded to RGBA8.");
                }
            }
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw CreateNativeRuntimeException(sourceIdentity, ex);
        }

        bool hasAlpha = false;
        for (int offset = 3; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset] == byte.MaxValue)
                continue;
            hasAlpha = true;
            break;
        }

        return new WebPDecodedImage(
            rgba,
            width,
            height,
            hasAlpha,
            container.IsLossless);
    }

    private static WebPContainerFeatures ValidateContainer(
        ReadOnlySpan<byte> encoded,
        string sourceIdentity)
    {
        if (!HasWebPSignature(encoded))
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' is missing the RIFF/WEBP signature.");
        }

        uint riffPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(encoded.Slice(4, 4));
        long declaredFileLength = checked(8L + riffPayloadLength);
        if (declaredFileLength != encoded.Length)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' declares {declaredFileLength} RIFF bytes but contains " +
                $"{encoded.Length}; truncated and trailing payloads are rejected.");
        }
        if ((riffPayloadLength & 1u) != 0u)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' declares an unaligned RIFF payload length.");
        }

        bool hasLossyPayload = false;
        bool hasLosslessPayload = false;
        int imagePayloadCount = 0;
        bool hasVp8X = false;
        int? extendedWidth = null;
        int? extendedHeight = null;
        int offset = 12;
        while (offset < encoded.Length)
        {
            if (encoded.Length - offset < 8)
            {
                throw new InvalidDataException(
                    $"WebP texture '{sourceIdentity}' ends inside a RIFF chunk header.");
            }

            ReadOnlySpan<byte> fourCc = encoded.Slice(offset, 4);
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
                encoded.Slice(offset + 4, 4));
            long payloadStart = offset + 8L;
            long payloadEnd = checked(payloadStart + payloadLength);
            long paddedEnd = checked(payloadEnd + (payloadLength & 1u));
            if (payloadEnd > encoded.Length || paddedEnd > encoded.Length)
            {
                throw new InvalidDataException(
                    $"WebP texture '{sourceIdentity}' contains a truncated RIFF chunk.");
            }

            if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8))
            {
                throw new NotSupportedException(
                    $"Animated WebP texture '{sourceIdentity}' is not supported; texture slots require " +
                    "one deterministic still image.");
            }
            if (fourCc.SequenceEqual("VP8 "u8))
            {
                hasLossyPayload = true;
                imagePayloadCount++;
            }
            else if (fourCc.SequenceEqual("VP8L"u8))
            {
                hasLosslessPayload = true;
                imagePayloadCount++;
            }
            else if (fourCc.SequenceEqual("VP8X"u8))
            {
                if (hasVp8X || payloadLength != 10)
                {
                    throw new InvalidDataException(
                        $"WebP texture '{sourceIdentity}' has an invalid VP8X chunk.");
                }
                hasVp8X = true;
                ReadOnlySpan<byte> payload = encoded.Slice(
                    checked((int)payloadStart),
                    checked((int)payloadLength));
                if ((payload[0] & 0x02) != 0)
                {
                    throw new NotSupportedException(
                        $"Animated WebP texture '{sourceIdentity}' is not supported; texture slots " +
                        "require one deterministic still image.");
                }
                extendedWidth =
                    1 + payload[4] + (payload[5] << 8) + (payload[6] << 16);
                extendedHeight =
                    1 + payload[7] + (payload[8] << 8) + (payload[9] << 16);
            }

            offset = checked((int)paddedEnd);
        }

        if (offset != encoded.Length)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' has an invalid RIFF chunk boundary.");
        }
        if (imagePayloadCount != 1 || hasLossyPayload == hasLosslessPayload)
        {
            throw new InvalidDataException(
                $"WebP texture '{sourceIdentity}' must contain exactly one lossy or lossless image " +
                "payload.");
        }

        return new WebPContainerFeatures(
            IsLossless: hasLosslessPayload,
            extendedWidth,
            extendedHeight);
    }

    private static void ValidateNativeDecoder(string sourceIdentity)
    {
        int decoderVersion;
        try
        {
            decoderVersion = NativeMethods.WebPGetDecoderVersion();
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw CreateNativeRuntimeException(sourceIdentity, ex);
        }

        if (decoderVersion != RequiredDecoderVersion)
        {
            throw new PlatformNotSupportedException(
                $"Texture '{sourceIdentity}' resolved libwebp version " +
                $"{FormatDecoderVersion(decoderVersion)}, but the reviewed runtime is " +
                $"{FormatDecoderVersion(RequiredDecoderVersion)}.");
        }
    }

    private static bool IsNativeLoadFailure(Exception exception) =>
        exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException;

    private static PlatformNotSupportedException CreateNativeRuntimeException(
        string sourceIdentity,
        Exception innerException) =>
        new(
            $"The pinned libwebp 1.6.0 runtime from Imazen.WebP.NativeRuntime.All 1.6.1 is " +
            $"unavailable or incompatible with the " +
            $"current process for texture '{sourceIdentity}'.",
            innerException);

    private static string FormatDecoderVersion(int encodedVersion) =>
        FormattableString.Invariant(
            $"{(encodedVersion >> 16) & 0xff}.{(encodedVersion >> 8) & 0xff}.{encodedVersion & 0xff}");

    private static bool HasWebPExtension(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(
            Path.GetExtension(path),
            ".webp",
            StringComparison.OrdinalIgnoreCase);

    private readonly record struct WebPContainerFeatures(
        bool IsLossless,
        int? ExtendedWidth,
        int? ExtendedHeight);

    private static unsafe class NativeMethods
    {
        private const string LibraryName = "libwebp";

        [DllImport(
            LibraryName,
            EntryPoint = "WebPGetDecoderVersion",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPGetDecoderVersion();

        [DllImport(
            LibraryName,
            EntryPoint = "WebPGetInfo",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int WebPGetInfo(
            byte* data,
            nuint dataSize,
            int* width,
            int* height);

        [DllImport(
            LibraryName,
            EntryPoint = "WebPDecodeRGBAInto",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint WebPDecodeRGBAInto(
            byte* data,
            nuint dataSize,
            byte* outputBuffer,
            nuint outputBufferSize,
            int outputStride);
    }
}

public readonly record struct WebPDecodedImage(
    byte[] Rgba8,
    int Width,
    int Height,
    bool HasAlpha,
    bool IsLossless);
