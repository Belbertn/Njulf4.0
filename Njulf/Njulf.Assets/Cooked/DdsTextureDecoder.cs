using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Validated, bounded DDS decode shared by cooking, transport analysis, and
/// uncooked runtime texture upload. Runtime DDS sources are decoded to RGBA8;
/// arrays, cubemaps, and HDR formats are intentionally rejected.
/// </summary>
public static class DdsTextureDecoder
{
    public const int DefaultMaximumEncodedBytes =
        TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes;
    public const long DefaultMaximumDecodedPixels =
        WebPTextureDecoder.DefaultMaximumDecodedPixels;

    private static ReadOnlySpan<byte> DdsIdentifier => "DDS "u8;

    public static bool HasDdsSignature(ReadOnlySpan<byte> encoded) =>
        encoded.Length >= DdsIdentifier.Length &&
        encoded[..DdsIdentifier.Length].SequenceEqual(DdsIdentifier);

    public static DdsDecodedImage DecodeRgba8(
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
                $"DDS texture '{sourceIdentity}' contains {encoded.Length} encoded bytes, " +
                $"exceeding the decode limit {maximumEncodedBytes}.");
        }
        if (!HasDdsSignature(encoded))
        {
            throw new InvalidDataException(
                $"DDS texture '{sourceIdentity}' is missing the DDS signature.");
        }

        using var stream = new MemoryStream(encoded, writable: false);
        DdsFile dds = DdsFile.Load(stream);
        if (dds.Faces.Count != 1)
        {
            throw new NotSupportedException(
                $"DDS texture '{sourceIdentity}' has {dds.Faces.Count} faces; only 2D textures are supported.");
        }

        DdsFace face = dds.Faces[0];
        if (face.Width is 0 or > int.MaxValue || face.Height is 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"DDS texture '{sourceIdentity}' has unsupported dimensions {face.Width}x{face.Height}.");
        }

        int width = checked((int)face.Width);
        int height = checked((int)face.Height);
        long pixelCount;
        long decodedByteCount;
        try
        {
            pixelCount = checked((long)width * height);
            decodedByteCount = checked(pixelCount * 4L);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                $"DDS texture '{sourceIdentity}' dimensions overflow the RGBA8 decode layout.",
                ex);
        }
        if (pixelCount > maximumPixels)
        {
            throw new NotSupportedException(
                $"DDS texture '{sourceIdentity}' contains {pixelCount} decoded pixels, " +
                $"exceeding the decode limit {maximumPixels}.");
        }
        if (decodedByteCount > Array.MaxLength)
        {
            throw new NotSupportedException(
                $"DDS texture '{sourceIdentity}' requires {decodedByteCount} decoded RGBA8 bytes, " +
                "exceeding the managed-array limit.");
        }

        var decoder = new BcDecoder();
        if (!decoder.IsSupportedFormat(dds))
        {
            throw new NotSupportedException(
                $"DDS texture '{sourceIdentity}' uses an unsupported pixel format.");
        }
        if (decoder.IsHdrFormat(dds))
        {
            throw new NotSupportedException(
                $"DDS texture '{sourceIdentity}' is HDR; use a supported HDR source container instead.");
        }

        ColorRgba32[] colors = decoder.Decode(dds);
        if (colors.LongLength != pixelCount)
        {
            throw new InvalidDataException(
                $"DDS decoder produced {colors.LongLength} pixels, expected {pixelCount} for '{sourceIdentity}'.");
        }

        byte[] rgba8 = GC.AllocateUninitializedArray<byte>(checked((int)decodedByteCount));
        for (int pixel = 0; pixel < colors.Length; pixel++)
        {
            int offset = pixel * 4;
            rgba8[offset] = colors[pixel].r;
            rgba8[offset + 1] = colors[pixel].g;
            rgba8[offset + 2] = colors[pixel].b;
            rgba8[offset + 3] = colors[pixel].a;
        }

        return new DdsDecodedImage(rgba8, width, height);
    }
}

public readonly record struct DdsDecodedImage(
    byte[] Rgba8,
    int Width,
    int Height);
