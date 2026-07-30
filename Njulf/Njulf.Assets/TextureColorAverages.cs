using Njulf.Core.Math;
using Njulf.Assets.Cooked;

namespace Njulf.Assets;

/// <summary>
/// Computes texture-wide color statistics in the same linear-light space used by
/// lighting. Texture coordinates and sampler state intentionally do not affect
/// this whole-image fallback.
/// </summary>
public static class TextureColorAverages
{
    private static readonly float[] SrgbToLinearLookup = BuildSrgbToLinearLookup();

    public static Vector4 CalculateRgba8Linear(ReadOnlySpan<byte> rgba, bool srgb)
    {
        if (rgba.IsEmpty || rgba.Length % 4 != 0)
            throw new ArgumentException("RGBA8 texture data must contain one or more complete pixels.", nameof(rgba));
        TextureTransportImage image = TextureTransportImage.FromRgba8(
            rgba,
            rgba.Length / 4,
            1,
            srgb ? TextureColorSpace.Srgb : TextureColorSpace.Linear,
            TextureSemantic.Color,
            CookedHash.Bytes(rgba),
            "TextureColorAverages compatibility adapter");
        return image.Statistics.LinearChannelMean.ToVector4();
    }

    internal static float SrgbByteToLinear(byte value) => SrgbToLinearLookup[value];

    private static float[] BuildSrgbToLinearLookup()
    {
        var lookup = new float[256];
        for (int value = 0; value < lookup.Length; value++)
        {
            double channel = value / 255.0;
            lookup[value] = (float)(channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4));
        }

        return lookup;
    }
}
