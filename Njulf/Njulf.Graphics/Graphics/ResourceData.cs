using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Position, authored normal and primary texture coordinates.</summary>
public readonly record struct VertexPositionNormalTexture(Vector3 Position, Vector3 Normal, Vector2 TextureCoordinate);

/// <summary>Standard surface vertex. Tangent.W is the bitangent handedness (+1 or -1).</summary>
public readonly record struct VertexPositionNormalTextureTangent(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TextureCoordinate,
    Vector4 Tangent);

/// <summary>Dynamic meshes allow whole-stream vertex updates with fixed topology.</summary>
public enum MeshUsage
{
    Static,
    Dynamic
}

/// <summary>Portable resource formats. Unknown preserves views of other content-loaded formats.</summary>
public enum TextureFormat
{
    Unknown,
    R8Unorm,
    Rg8Unorm,
    Rgba8Unorm,
    Rgba8Srgb,
    Bgra8Unorm,
    Bgra8Srgb,
    Rgba16Float,
    R32Float
}

/// <summary>Immutable dimensions, storage format and number of mip levels.</summary>
public readonly record struct Texture2DDescription(int Width, int Height, TextureFormat Format, int MipLevels = 1);

/// <summary>A texel rectangle within one mip level.</summary>
public readonly record struct TextureRectangle(int X, int Y, int Width, int Height);

/// <summary>Owned, tightly packed native-format bytes; no gamma conversion is applied.</summary>
public sealed record TextureReadback(int Width, int Height, TextureFormat Format, int RowPitch, byte[] Data);