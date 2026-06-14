using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Math;

namespace Njulf.Assets;

public static class RendererAssetSchemaVersions
{
    public const int AssetPipelineVersion = 1;
    public const int MeshletBuilderVersion = 1;
    public const int SimplifierVersion = 1;
    public const int MaterialClassificationVersion = 1;
}

[Flags]
public enum ProcessedMeshFlags : uint
{
    None = 0,
    AlphaTested = 1u << 0,
    Transparent = 1u << 1,
    Skinned = 1u << 2,
    Foliage = 1u << 3,
    SupportsImpostor = 1u << 4
}

public enum MeshLodProvenance
{
    Authored,
    GeneratedFallback
}

public sealed record ProcessedMeshAsset(
    string AssetId,
    string SourcePath,
    string SourceContentHash,
    ProcessedMeshVersion Version,
    IReadOnlyList<ProcessedSubmesh> Submeshes,
    IReadOnlyList<ProcessedMeshLod> Lods,
    IReadOnlyList<int> MaterialSlots,
    ProcessedMeshFlags Flags,
    FoliageAssetMetadata? Foliage,
    ImpostorAssetMetadata? Impostor,
    ContentBudgetStatus BudgetStatus,
    IReadOnlyList<ProcessedMeshVertex>? Vertices = null,
    IReadOnlyList<uint>? Indices = null,
    IReadOnlyList<ProcessedMeshlet>? Meshlets = null,
    IReadOnlyList<uint>? MeshletVertices = null,
    IReadOnlyList<uint>? MeshletTriangles = null,
    IReadOnlyList<ProcessedLodDiagnostic>? LodDiagnostics = null)
{
    public IReadOnlyList<ProcessedMeshVertex> Vertices { get; } = Vertices ?? Array.Empty<ProcessedMeshVertex>();
    public IReadOnlyList<uint> Indices { get; } = Indices ?? Array.Empty<uint>();
    public IReadOnlyList<ProcessedMeshlet> Meshlets { get; } = Meshlets ?? Array.Empty<ProcessedMeshlet>();
    public IReadOnlyList<uint> MeshletVertices { get; } = MeshletVertices ?? Array.Empty<uint>();
    public IReadOnlyList<uint> MeshletTriangles { get; } = MeshletTriangles ?? Array.Empty<uint>();
    public IReadOnlyList<ProcessedLodDiagnostic> LodDiagnostics { get; } = LodDiagnostics ?? Array.Empty<ProcessedLodDiagnostic>();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AssetId))
            throw new InvalidOperationException("Processed mesh asset is missing an asset ID.");
        if (string.IsNullOrWhiteSpace(SourceContentHash))
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' is missing a source content hash.");
        Version.ValidateCurrent(AssetId);
        if (MaterialSlots.Count == 0)
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' has no material slots.");
        if (Submeshes.Count == 0)
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' has no submeshes.");
        if (Lods.Count == 0)
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' has no LODs.");

        if (Vertices.Count > 0)
            ValidateVertexBuffer();
        if (Indices.Count > 0)
            ValidateIndexBuffer();

        uint previousTriangles = uint.MaxValue;
        for (int i = 0; i < Lods.Count; i++)
        {
            ProcessedMeshLod lod = Lods[i];
            lod.Validate(AssetId, i, MaterialSlots.Count);
            ValidateLodRanges(lod);
            if (lod.TriangleCount > previousTriangles)
                throw new InvalidOperationException($"Processed mesh asset '{AssetId}' LOD{i} has more triangles than the previous LOD.");
            previousTriangles = lod.TriangleCount;
        }

        foreach (ProcessedSubmesh submesh in Submeshes)
            submesh.Validate(AssetId, MaterialSlots.Count);

        if (Foliage != null)
            Foliage.Validate(AssetId);
        if (Impostor != null)
            Impostor.Validate(AssetId);

        BudgetStatus.ThrowIfHardFailed(AssetId);
    }

    public bool IsStale(string currentSourceHash) =>
        !string.Equals(SourceContentHash, currentSourceHash, StringComparison.OrdinalIgnoreCase) ||
        !Version.IsCurrent;

    private void ValidateVertexBuffer()
    {
        for (int i = 0; i < Vertices.Count; i++)
        {
            if (!Vertices[i].IsFinite)
                throw new InvalidOperationException($"Processed mesh asset '{AssetId}' vertex {i} contains non-finite data.");
        }
    }

    private void ValidateIndexBuffer()
    {
        if (Indices.Count % 3 != 0)
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' index buffer is not triangle aligned.");

        for (int i = 0; i < Indices.Count; i++)
        {
            if (Indices[i] >= Vertices.Count)
                throw new InvalidOperationException($"Processed mesh asset '{AssetId}' index {i} references vertex {Indices[i]}, but vertex count is {Vertices.Count}.");
        }
    }

    private void ValidateLodRanges(ProcessedMeshLod lod)
    {
        if (Vertices.Count > 0)
            ValidateRange($"LOD{lod.Level} vertices", lod.VertexOffset, lod.VertexCount, (uint)Vertices.Count);
        if (Indices.Count > 0)
            ValidateRange($"LOD{lod.Level} indices", lod.IndexOffset, lod.IndexCount, (uint)Indices.Count);
        if (Meshlets.Count > 0)
            ValidateRange($"LOD{lod.Level} meshlets", lod.MeshletOffset, lod.MeshletCount, (uint)Meshlets.Count);
    }

    private void ValidateRange(string name, uint offset, uint count, uint capacity)
    {
        if ((ulong)offset + count > capacity)
            throw new InvalidOperationException($"Processed mesh asset '{AssetId}' {name} range [{offset}, {(ulong)offset + count}) exceeds capacity {capacity}.");
    }
}

public sealed record ProcessedMeshVersion(
    int AssetPipelineVersion,
    int MeshletBuilderVersion,
    int SimplifierVersion,
    int MaterialClassificationVersion)
{
    public static ProcessedMeshVersion Current { get; } = new(
        RendererAssetSchemaVersions.AssetPipelineVersion,
        RendererAssetSchemaVersions.MeshletBuilderVersion,
        RendererAssetSchemaVersions.SimplifierVersion,
        RendererAssetSchemaVersions.MaterialClassificationVersion);

    public bool IsCurrent =>
        AssetPipelineVersion == RendererAssetSchemaVersions.AssetPipelineVersion &&
        MeshletBuilderVersion == RendererAssetSchemaVersions.MeshletBuilderVersion &&
        SimplifierVersion == RendererAssetSchemaVersions.SimplifierVersion &&
        MaterialClassificationVersion == RendererAssetSchemaVersions.MaterialClassificationVersion;

    public void ValidateCurrent(string assetId)
    {
        if (!IsCurrent)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' was built for schema {this}, but runtime expects {Current}.");
    }
}

public sealed record ProcessedSubmesh(
    int MaterialSlot,
    uint FirstIndex,
    uint IndexCount,
    BoundingBox Bounds)
{
    public void Validate(string assetId, int materialSlotCount)
    {
        if (MaterialSlot < 0 || MaterialSlot >= materialSlotCount)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' submesh material slot {MaterialSlot} is outside material slot count {materialSlotCount}.");
        if (IndexCount == 0 || IndexCount % 3u != 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' submesh has invalid index count {IndexCount}.");
        ProcessedMeshValidation.ValidateFinite(Bounds, $"Processed mesh asset '{assetId}' submesh bounds");
    }
}

public sealed record ProcessedMeshVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Tangent,
    Vector2 TexCoord,
    Vector2 TexCoord1,
    Vector4 Color,
    VertexSkinningPayload Skinning)
{
    public bool IsFinite =>
        ProcessedMeshValidation.IsFinite(Position) &&
        ProcessedMeshValidation.IsFinite(Normal) &&
        ProcessedMeshValidation.IsFinite(Tangent) &&
        ProcessedMeshValidation.IsFinite(TexCoord) &&
        ProcessedMeshValidation.IsFinite(TexCoord1) &&
        ProcessedMeshValidation.IsFinite(Color);
}

public readonly record struct VertexSkinningPayload(
    ushort Joint0,
    ushort Joint1,
    ushort Joint2,
    ushort Joint3,
    float Weight0,
    float Weight1,
    float Weight2,
    float Weight3)
{
    public static VertexSkinningPayload Empty => default;
}

public sealed record ProcessedMeshlet(
    Vector3 BoundingSphereCenter,
    float BoundingSphereRadius,
    Vector3 ConeAxis,
    float ConeCutoff,
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    uint LocalVertexOffset,
    uint LocalVertexCount,
    uint LocalTriangleOffset,
    uint LocalTriangleCount,
    int MaterialSlot,
    int SubmeshIndex);

public sealed record ProcessedLodDiagnostic(
    int Level,
    MeshLodProvenance Provenance,
    uint TriangleCount,
    uint VertexCount,
    bool MaterialCompatible,
    BoundingBox Bounds,
    string Message);

public sealed record ProcessedMeshLod(
    int Level,
    MeshLodProvenance Provenance,
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    uint MeshletOffset,
    uint MeshletCount,
    BoundingBox Bounds,
    ProcessedLodQualityMetrics Quality,
    float SwitchDistance = 0f,
    float ScreenRelativeTransitionHeight = 0f)
{
    public uint TriangleCount => IndexCount / 3u;

    public void Validate(string assetId, int expectedLevel, int materialSlotCount)
    {
        if (Level != expectedLevel)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' has non-contiguous LOD level {Level}; expected {expectedLevel}.");
        ProcessedMeshValidation.ValidateFinite(Bounds, $"Processed mesh asset '{assetId}' LOD{Level} bounds");
        if (VertexCount == 0 || IndexCount == 0 || IndexCount % 3u != 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{Level} has invalid vertex/index counts.");
        if (MeshletCount == 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{Level} has no meshlets.");
        if (!float.IsFinite(SwitchDistance) || SwitchDistance < 0f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{Level} has invalid switch distance.");
        if (!float.IsFinite(ScreenRelativeTransitionHeight) || ScreenRelativeTransitionHeight < 0f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{Level} has invalid screen-relative transition height.");
        Quality.Validate(assetId, Level);
    }

}

public sealed record ProcessedLodQualityMetrics(
    float TriangleReduction,
    float VertexReduction,
    float GeometricError,
    float UvError,
    float NormalDeviationDegrees,
    bool MaterialSeamsPreserved)
{
    public void Validate(string assetId, int lod)
    {
        if (!float.IsFinite(TriangleReduction) || TriangleReduction < 0f || TriangleReduction > 1f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{lod} has invalid triangle reduction metric.");
        if (!float.IsFinite(VertexReduction) || VertexReduction < 0f || VertexReduction > 1f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{lod} has invalid vertex reduction metric.");
        if (!float.IsFinite(GeometricError) || GeometricError < 0f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' LOD{lod} has invalid geometric error metric.");
    }
}

public sealed record FoliageAssetMetadata(
    string SourceMeshAssetId,
    IReadOnlyList<int> MaterialSlots,
    float AlphaTestThreshold,
    bool TwoSided,
    bool HasNormalMap,
    float MipBias,
    Vector3 WindDirection,
    float WindStrength,
    Vector3 BendPivot,
    IReadOnlyList<FoliageCluster> Clusters)
{
    public FoliageAssetMetadata(
        float alphaTestThreshold,
        Vector3 windDirection,
        float windStrength,
        Vector3 bendPivot,
        IReadOnlyList<FoliageCluster> clusters)
        : this(string.Empty, Array.Empty<int>(), alphaTestThreshold, true, false, 0f, windDirection, windStrength, bendPivot, clusters)
    {
    }

    public void Validate(string assetId)
    {
        if (!float.IsFinite(AlphaTestThreshold) || AlphaTestThreshold < 0f || AlphaTestThreshold > 1f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage alpha test threshold is invalid.");
        if (!float.IsFinite(WindStrength) || WindStrength < 0f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage wind strength is invalid.");
        if (!float.IsFinite(MipBias) || MipBias < -4f || MipBias > 4f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage mip bias is invalid.");
        ProcessedMeshValidation.ValidateFinite(BendPivot, $"Processed mesh asset '{assetId}' foliage bend pivot");
        if (Clusters.Count == 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage metadata has no clusters.");
        foreach (FoliageCluster cluster in Clusters)
            cluster.Validate(assetId);
    }
}

public sealed record FoliageCluster(
    BoundingBox Bounds,
    int InstanceCount,
    IReadOnlyList<FoliageInstance> Instances,
    float WindPhase,
    float VariationSeed)
{
    public FoliageCluster(
        BoundingBox bounds,
        int instanceCount,
        float windPhase,
        float variationSeed)
        : this(bounds, instanceCount, Array.Empty<FoliageInstance>(), windPhase, variationSeed)
    {
    }

    public void Validate(string assetId)
    {
        ProcessedMeshValidation.ValidateFinite(Bounds, $"Processed mesh asset '{assetId}' foliage cluster bounds");
        if (InstanceCount <= 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage cluster has invalid instance count {InstanceCount}.");
        if (Instances.Count != 0 && Instances.Count != InstanceCount)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage cluster instance payload count does not match instance count.");
        if (!float.IsFinite(WindPhase) || !float.IsFinite(VariationSeed))
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' foliage cluster has non-finite wind data.");
    }
}

public sealed record FoliageInstance(
    Matrix4x4 Transform,
    Vector4 Variation,
    float WindPhase);

public sealed record ImpostorAssetMetadata(
    string AtlasAssetId,
    int AtlasWidth,
    int AtlasHeight,
    int ViewDirectionCount,
    ImpostorType Type,
    IReadOnlyList<ImpostorAtlasTexture> Textures,
    BoundingBox Bounds,
    Vector3 Pivot,
    float AlphaCoverage,
    float DepthScale = 1f,
    float DepthBias = 0f,
    string NormalEncoding = "octahedral")
{
    public ImpostorAssetMetadata(
        string atlasAssetId,
        int atlasWidth,
        int atlasHeight,
        int viewDirectionCount,
        BoundingBox bounds,
        Vector3 pivot,
        float alphaCoverage)
        : this(atlasAssetId, atlasWidth, atlasHeight, viewDirectionCount, ImpostorType.Octahedral, Array.Empty<ImpostorAtlasTexture>(), bounds, pivot, alphaCoverage)
    {
    }

    public void Validate(string assetId)
    {
        if (string.IsNullOrWhiteSpace(AtlasAssetId))
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' impostor metadata is missing an atlas asset ID.");
        if (AtlasWidth <= 0 || AtlasHeight <= 0 || ViewDirectionCount <= 0)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' impostor atlas dimensions or view count are invalid.");
        if (!float.IsFinite(AlphaCoverage) || AlphaCoverage < 0f || AlphaCoverage > 1f)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' impostor alpha coverage is invalid.");
        if (!float.IsFinite(DepthScale) || !float.IsFinite(DepthBias))
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' impostor depth reconstruction parameters are invalid.");
        ProcessedMeshValidation.ValidateFinite(Bounds, $"Processed mesh asset '{assetId}' impostor bounds");
        ProcessedMeshValidation.ValidateFinite(Pivot, $"Processed mesh asset '{assetId}' impostor pivot");
    }
}

public enum ImpostorType
{
    BillboardCross,
    Octahedral,
    CardCloud
}

public enum ImpostorTextureKind
{
    Albedo,
    Normal,
    Depth,
    RoughnessMetalness
}

public sealed record ImpostorAtlasTexture(
    ImpostorTextureKind Kind,
    string AssetId,
    TextureColorSpace ColorSpace);

public enum ContentBudgetSeverity
{
    Pass,
    Warning,
    Error
}

public sealed record ContentBudgetStatus(
    ContentBudgetSeverity Severity,
    string Message)
{
    public static ContentBudgetStatus Pass { get; } = new(ContentBudgetSeverity.Pass, string.Empty);

    public void ThrowIfHardFailed(string assetId)
    {
        if (Severity == ContentBudgetSeverity.Error)
            throw new InvalidOperationException($"Processed mesh asset '{assetId}' failed content budgets: {Message}");
    }
}

public sealed record ContentBudgetLimits(
    uint MaxTrianglesPerLod,
    uint MaxMeshletsPerAsset,
    int MaxFoliageInstancesPerCluster,
    int MaxImpostorAtlasSize,
    int MaxMaterialSlots)
{
    public static ContentBudgetLimits Default { get; } = new(250_000, 64_000, 1024, 4096, 32);
}

public static class ProcessedMeshAssetValidator
{
    public static ContentBudgetStatus EvaluateBudgets(ProcessedMeshAsset asset, ContentBudgetLimits limits)
    {
        if (asset.MaterialSlots.Count > limits.MaxMaterialSlots)
            return new ContentBudgetStatus(ContentBudgetSeverity.Error, $"material slots {asset.MaterialSlots.Count} exceed hard limit {limits.MaxMaterialSlots}");

        uint meshletSum = 0;
        foreach (ProcessedMeshLod lod in asset.Lods)
        {
            if (lod.TriangleCount > limits.MaxTrianglesPerLod)
                return new ContentBudgetStatus(ContentBudgetSeverity.Error, $"LOD{lod.Level} triangles {lod.TriangleCount} exceed hard limit {limits.MaxTrianglesPerLod}");
            meshletSum += lod.MeshletCount;
        }

        if (meshletSum > limits.MaxMeshletsPerAsset)
            return new ContentBudgetStatus(ContentBudgetSeverity.Warning, $"meshlets {meshletSum} exceed soft target {limits.MaxMeshletsPerAsset}");

        if (asset.Foliage != null)
        {
            foreach (FoliageCluster cluster in asset.Foliage.Clusters)
            {
                if (cluster.InstanceCount > limits.MaxFoliageInstancesPerCluster)
                    return new ContentBudgetStatus(ContentBudgetSeverity.Error, $"foliage cluster instances {cluster.InstanceCount} exceed hard limit {limits.MaxFoliageInstancesPerCluster}");
            }
        }

        if (asset.Impostor != null &&
            (asset.Impostor.AtlasWidth > limits.MaxImpostorAtlasSize || asset.Impostor.AtlasHeight > limits.MaxImpostorAtlasSize))
        {
            return new ContentBudgetStatus(ContentBudgetSeverity.Error, $"impostor atlas {asset.Impostor.AtlasWidth}x{asset.Impostor.AtlasHeight} exceeds hard limit {limits.MaxImpostorAtlasSize}");
        }

        return ContentBudgetStatus.Pass;
    }

    public static string ComputeSourceHash(string source)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static string ComputeFileHash(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Source asset was not found: {Path.GetFullPath(path)}", Path.GetFullPath(path));

        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}

internal static class ProcessedMeshValidation
{
    public static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    public static void ValidateFinite(Vector3 value, string name)
    {
        if (!IsFinite(value))
            throw new InvalidOperationException($"{name} contains non-finite values.");
    }

    public static void ValidateFinite(BoundingBox bounds, string name)
    {
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max))
            throw new InvalidOperationException($"{name} contains non-finite values.");
    }
}
