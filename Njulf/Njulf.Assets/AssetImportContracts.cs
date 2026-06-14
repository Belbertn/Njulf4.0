using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Assets;

public enum AssetImportSeverity
{
    Info,
    Warning,
    Error
}

public enum AssetImportMessageCode
{
    UnsupportedRequiredExtension,
    UnsupportedOptionalExtension,
    MissingExternalBuffer,
    MissingExternalImage,
    EmbeddedBufferLoaded,
    EmbeddedImageLoaded,
    SamplerDefaulted,
    TextureTransformApplied,
    TextureTransformUnsupported,
    ColorSpaceAssigned,
    TextureFallbackUsed,
    VertexColorImported,
    UvSetImported,
    AccessorBoundsInvalid,
    CompressedTextureUnsupported,
    MorphTargetsUnsupported
}

public sealed record AssetImportMessage(
    AssetImportSeverity Severity,
    AssetImportMessageCode Code,
    string AssetPath,
    string? Source,
    string Message);

public sealed class AssetImportDiagnostics
{
    private readonly List<AssetImportMessage> _messages = new();

    public static AssetImportDiagnostics Empty { get; } = new();

    public IReadOnlyList<AssetImportMessage> Messages => _messages;
    public int ImportedGltfCount { get; set; }
    public int ImportedGlbCount { get; set; }
    public int ExternalBufferCount { get; set; }
    public int EmbeddedBufferCount { get; set; }
    public int DataUriBufferCount { get; set; }
    public int ExternalImageCount { get; set; }
    public int EmbeddedImageCount { get; set; }
    public int DataUriImageCount { get; set; }
    public int BufferViewImageCount { get; set; }
    public int ImportedSamplerCount { get; set; }
    public int TextureTransformCount { get; set; }
    public int UnsupportedOptionalExtensionCount { get; set; }
    public int UnsupportedRequiredExtensionCount { get; set; }
    public int VertexColorMeshCount { get; set; }
    public int Uv0MeshCount { get; set; }
    public int Uv1MeshCount { get; set; }
    public int UnsupportedCompressedTextureCount { get; set; }
    public int UnsupportedMorphTargetMeshCount { get; set; }

    public void Add(AssetImportSeverity severity, AssetImportMessageCode code, string assetPath, string? source, string message)
    {
        _messages.Add(new AssetImportMessage(severity, code, assetPath, source, message));
    }
}

public enum TextureColorSpace
{
    Linear,
    Srgb,
    HdrLinear
}

public enum TextureWrapMode
{
    Repeat,
    ClampToEdge,
    MirroredRepeat
}

public enum TextureFilterMode
{
    Nearest,
    Linear
}

public enum TextureMipFilterMode
{
    Nearest,
    Linear
}

public readonly record struct TextureSamplerDescription(
    TextureWrapMode WrapU,
    TextureWrapMode WrapV,
    TextureFilterMode MinFilter,
    TextureFilterMode MagFilter,
    TextureMipFilterMode MipFilter,
    float MaxAnisotropy)
{
    public static TextureSamplerDescription Default { get; } = new(
        TextureWrapMode.Repeat,
        TextureWrapMode.Repeat,
        TextureFilterMode.Linear,
        TextureFilterMode.Linear,
        TextureMipFilterMode.Linear,
        16f);
}

public sealed class ModelTextureSource
{
    public string DebugName { get; init; } = string.Empty;
    public string? FilePath { get; init; }
    public byte[]? Bytes { get; init; }
    public string? MimeType { get; init; }
    public string CacheIdentity { get; init; } = string.Empty;

    public bool IsMemorySource => Bytes is { Length: > 0 };
}

public sealed class ModelTextureSlot
{
    public ModelTextureSource? Source { get; init; }
    public TextureSamplerDescription Sampler { get; init; } = TextureSamplerDescription.Default;
    public TextureColorSpace ColorSpace { get; init; } = TextureColorSpace.Linear;
    public int TexCoordSet { get; init; }
    public Vector2 Offset { get; init; } = Vector2.Zero;
    public Vector2 Scale { get; init; } = new(1f, 1f);
    public float RotationRadians { get; init; }
}
