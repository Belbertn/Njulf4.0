using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

public sealed record CookedAssetReference(string RelativePath, ulong ContentHash);

public sealed record CookedModelSubObject(
    string Name,
    int SubMeshIndex,
    int MaterialSlot,
    int NodeIndex,
    int SkinIndex,
    Matrix4x4 SkinningBindTransform);

public sealed record CookedModelManifest(
    Guid AssetId,
    string Name,
    string SourcePath,
    ulong SourceHash,
    ulong ImportSettingsHash,
    ulong DependencyListHash,
    CookedAssetReference Mesh,
    CookedAssetReference Material,
    CookedAssetReference? Animation,
    IReadOnlyList<CookedModelSubObject> SubObjects,
    BoundingBox BoundingBox,
    BoundingSphere BoundingSphere);

public sealed record CookedSubMeshRecord(
    string Name,
    int MaterialSlot,
    int NodeIndex,
    int SkinIndex,
    Matrix4x4 SkinningBindTransform,
    int VertexOffset,
    int VertexCount,
    int IndexOffset,
    int IndexCount,
    int SkinningOffset,
    int SkinningCount,
    int MeshletOffset,
    int MeshletCount,
    int MeshletVertexOffset,
    int MeshletVertexCount,
    int MeshletTriangleOffset,
    int MeshletTriangleCount,
    IReadOnlyList<ProcessedMeshLodRange> LodRanges,
    IReadOnlyList<ProcessedMeshDrawRange> DrawRanges,
    BoundingBox BoundingBox,
    BoundingSphere BoundingSphere,
    uint VertexAttributes)
{
    public int MeshletLod1Offset { get; init; }
    public int MeshletLod1Count { get; init; }
    public int MeshletLod2Offset { get; init; }
    public int MeshletLod2Count { get; init; }
}

public sealed record CookedMeshPayload(
    IReadOnlyList<CookedSubMeshRecord> SubMeshes,
    CookedVertexPositionStream[] VertexPositions,
    CookedVertexNormalTangentStream[] VertexNormalTangents,
    CookedVertexUvColorStream[] VertexUvColors,
    CookedVertexSkinningData[] VertexSkinning,
    uint[] Indices,
    Meshlet[] MeshletsLod0,
    Meshlet[] MeshletsLod1,
    Meshlet[] MeshletsLod2,
    uint[] MeshletVertices,
    uint[] MeshletTriangles);

public enum CookedMaterialPipeline
{
    Opaque,
    Masked,
    Blended,
    Decal,
    Unlit,
    Foliage
}

[Flags]
public enum CookedMaterialFallbackFlags : uint
{
    None = 0,
    BaseColorWhite = 1u << 0,
    NormalDefault = 1u << 1,
    MetallicRoughnessWhite = 1u << 2,
    EmissiveBlack = 1u << 3,
    OcclusionWhite = 1u << 4
}

public sealed record CookedMaterialFallback(string MaterialName, CookedMaterialFallbackFlags Flags);

public sealed record CookedMaterialTable(IReadOnlyList<ModelMaterial> Materials)
{
    public IReadOnlyList<CookedMaterialPipeline> Pipelines { get; init; } = Array.Empty<CookedMaterialPipeline>();
    public IReadOnlyList<CookedMaterialFallback> Fallbacks { get; init; } = Array.Empty<CookedMaterialFallback>();
}
public sealed record CookedAnimationPayload(
    IReadOnlyList<Skeleton> Skeletons,
    IReadOnlyList<Skin> Skins,
    IReadOnlyList<AnimationClip> AnimationClips);

public sealed record CookedTextureMeta(
    Guid AssetId,
    string SourceIdentity,
    ulong SourceHash,
    string Ktx2RelativePath,
    TextureColorSpace ColorSpace,
    TextureSamplerDescription Sampler,
    int OriginalWidth,
    int OriginalHeight,
    int CookedWidth,
    int CookedHeight,
    int MipCount,
    uint VulkanFormat,
    long EncodedBytes);

public sealed record CookedModelAsset(
    CookedModelManifest Manifest,
    CookedMeshPayload Mesh,
    CookedMaterialTable Materials,
    CookedAnimationPayload Animation,
    string PackagePath,
    long BytesRead);

internal static class CookedJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Deserialize<T>(ReadOnlySpan<byte> value, string path, string section)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, Options)
                ?? throw new CookedAssetFormatException(path, $"{section} section deserialized to null");
        }
        catch (CookedAssetFormatException) { throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new CookedAssetFormatException(path, $"{section} section contains invalid metadata ({ex.Message})");
        }
    }
}
