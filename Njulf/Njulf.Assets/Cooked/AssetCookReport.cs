using System;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;


public sealed record AssetCookReport(
    string SourcePath,
    Guid AssetId,
    string Status,
    ModelImportBackend Backend,
    long ImportMilliseconds,
    long MeshMilliseconds,
    long TextureMilliseconds,
    long SerializationMilliseconds,
    int SubMeshCount,
    int MaterialCount,
    int TextureCount,
    int SkeletonCount,
    int SkinCount,
    int AnimationClipCount,
    int VertexCount,
    int IndexCount,
    int MeshletCount,
    IReadOnlyList<CookedTextureReport> Textures,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, ulong> Outputs)
{
    public int MeshletLod1Count { get; init; }
    public int MeshletLod2Count { get; init; }
}
