using Njulf.Graphics;
namespace Njulf.Assets.Cooked;

public enum TextureSemantic
{
    Color,
    Normal,
    Scalar,
    Data,
    Hdr
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

/// <summary>Source interpretation for decoding without resizing, encoding or writing files.</summary>
/// <param name="ColorSpace">Fallback interpretation when the container does not declare it.</param>
/// <param name="Semantic">Meaning of decoded channels.</param>
/// <param name="ForceHdr">Decode floating-point channels even without an HDR semantic.</param>
public sealed record TextureDecodeOptions(TextureColorSpace ColorSpace = TextureColorSpace.Srgb, TextureSemantic Semantic = TextureSemantic.Color, bool ForceHdr = false);
