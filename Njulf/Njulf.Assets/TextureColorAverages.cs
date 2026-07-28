using Njulf.Core.Math;

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

        double red = 0.0;
        double green = 0.0;
        double blue = 0.0;
        double alpha = 0.0;
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            red += srgb ? SrgbByteToLinear(rgba[offset]) : rgba[offset] / 255.0;
            green += srgb ? SrgbByteToLinear(rgba[offset + 1]) : rgba[offset + 1] / 255.0;
            blue += srgb ? SrgbByteToLinear(rgba[offset + 2]) : rgba[offset + 2] / 255.0;
            alpha += rgba[offset + 3] / 255.0;
        }

        double inversePixelCount = 4.0 / rgba.Length;
        return new Vector4(
            (float)(red * inversePixelCount),
            (float)(green * inversePixelCount),
            (float)(blue * inversePixelCount),
            (float)(alpha * inversePixelCount));
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
