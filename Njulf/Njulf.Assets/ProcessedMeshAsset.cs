using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using Njulf.Assets.Validation;
using CoreSkeleton = Njulf.Core.Animation.Skeleton;

namespace Njulf.Assets;

public enum ProcessedVertexAttribute : uint
{
    Position = 1u << 0,
    Normal = 1u << 1,
    Tangent = 1u << 2,
    Bitangent = 1u << 3,
    TexCoord0 = 1u << 4,
    TexCoord1 = 1u << 5,
    VertexColor = 1u << 6,
    Joints0 = 1u << 7,
    Weights0 = 1u << 8
}

public sealed record ProcessedVertexLayout(
    uint Attributes,
    int VertexCount,
    int PositionStrideBytes,
    int IndexStrideBytes)
{
    public bool Has(ProcessedVertexAttribute attribute)
    {
        return (Attributes & (uint)attribute) != 0;
    }
}

public sealed record ProcessedIndexLayout(
    int IndexCount,
    int IndexStrideBytes,
    bool Uses32BitIndices);

public sealed record ProcessedMaterialSlot(
    int Index,
    string Name);

public sealed record ProcessedMeshDrawRange(
    string Name,
    int MaterialSlot,
    int FirstIndex,
    int IndexCount,
    int BaseVertex);

public sealed record ProcessedMeshLodRange(
    int Level,
    int FirstMeshlet,
    int MeshletCount,
    float ScreenCoverageThreshold);

public sealed record ProcessedSubMeshAsset(
    string Name,
    int MaterialSlot,
    int NodeIndex,
    int SkinIndex,
    Matrix4x4 SkinningBindTransform,
    ProcessedVertexLayout VertexLayout,
    ProcessedIndexLayout IndexLayout,
    Vector3[] Vertices,
    Vector3[] Normals,
    Vector3[] Tangents,
    Vector3[] Bitangents,
    Vector2[] TexCoords,
    Vector2[] TexCoords1,
    Vector4[] VertexColors,
    VertexJointIndices[] JointIndices0,
    VertexJointWeights[] JointWeights0,
    uint[] Indices,
    Meshlet[] Meshlets,
    uint[] MeshletVertices,
    uint[] MeshletTriangles,
    BoundingBox BoundingBox,
    BoundingSphere BoundingSphere,
    IReadOnlyList<ProcessedMeshDrawRange> DrawRanges,
    IReadOnlyList<ProcessedMeshLodRange> LodRanges)
{
    public ModelGiCausticHeroTopologyEvidence CausticTopologyEvidence { get; init; }
    public ModelGiCausticHeroValidation CausticAuthoringValidation { get; init; }
    public string CausticTopologyDetail { get; init; } = "participation-disabled";
}

public sealed record ProcessedMeshAsset(
    string Name,
    string SourcePath,
    ProcessedVertexLayout VertexLayout,
    ProcessedIndexLayout IndexLayout,
    IReadOnlyList<ProcessedSubMeshAsset> SubMeshes,
    IReadOnlyList<ProcessedMaterialSlot> MaterialSlots,
    BoundingBox BoundingBox,
    BoundingSphere BoundingSphere,
    int TotalMeshletCount,
    int TotalTriangleCount)
{
    public IReadOnlyList<ModelMaterial> Materials { get; init; } = Array.Empty<ModelMaterial>();
    public IReadOnlyList<CoreSkeleton> Skeletons { get; init; } = Array.Empty<CoreSkeleton>();
    public IReadOnlyList<Skin> Skins { get; init; } = Array.Empty<Skin>();
    public IReadOnlyList<AnimationClip> AnimationClips { get; init; } = Array.Empty<AnimationClip>();
}

public sealed class ProcessedMeshAssetBuilder
{
    private readonly RendererMeshletLodBuilder _meshletLodBuilder;

    public ProcessedMeshAssetBuilder()
        : this(new MeshletBuilder(maxVerticesPerMeshlet: 48, maxTrianglesPerMeshlet: 64))
    {
    }

    public ProcessedMeshAssetBuilder(MeshletBuilder meshletBuilder)
    {
        _meshletLodBuilder = new RendererMeshletLodBuilder(meshletBuilder ?? throw new ArgumentNullException(nameof(meshletBuilder)));
    }

    public ProcessedMeshAsset Build(ModelMesh modelMesh, string? sourcePath = null)
    {
        if (modelMesh == null)
            throw new ArgumentNullException(nameof(modelMesh));

        IReadOnlyList<ModelSubMesh> sourceSubMeshes = modelMesh.SubMeshes.Count > 0
            ? modelMesh.SubMeshes
            : new[]
            {
                new ModelSubMesh
                {
                    Name = string.IsNullOrWhiteSpace(modelMesh.Name) ? "Mesh" : modelMesh.Name,
                    MaterialIndex = 0,
                    Vertices = modelMesh.Vertices,
                    Normals = modelMesh.Normals,
                    Tangents = modelMesh.Tangents,
                    Bitangents = modelMesh.Bitangents,
                    TexCoords = modelMesh.TexCoords,
                    TexCoords1 = modelMesh.TexCoords1,
                    VertexColors = modelMesh.VertexColors,
                    JointIndices0 = modelMesh.JointIndices0,
                    JointWeights0 = modelMesh.JointWeights0,
                    Indices = modelMesh.Indices,
                    BoundingBox = modelMesh.BoundingBox,
                    BoundingSphere = modelMesh.BoundingSphere
                }
            };

        var processedSubMeshes = new List<ProcessedSubMeshAsset>(sourceSubMeshes.Count);
        uint aggregateAttributes = 0;
        int totalVertexCount = 0;
        int totalIndexCount = 0;
        int totalMeshletCount = 0;

        foreach (ModelSubMesh subMesh in sourceSubMeshes)
        {
            ModelMaterial material = ResolveMaterial(modelMesh.Materials, subMesh);
            ProcessedSubMeshAsset processed = BuildSubMesh(subMesh, material);
            processedSubMeshes.Add(processed);
            aggregateAttributes |= processed.VertexLayout.Attributes;
            totalVertexCount += processed.VertexLayout.VertexCount;
            totalIndexCount += processed.IndexLayout.IndexCount;
            totalMeshletCount += processed.Meshlets.Length;
        }

        return new ProcessedMeshAsset(
            string.IsNullOrWhiteSpace(modelMesh.Name) ? "ProcessedMesh" : modelMesh.Name,
            sourcePath ?? string.Empty,
            new ProcessedVertexLayout(aggregateAttributes, totalVertexCount, sizeof(float) * 3, sizeof(uint)),
            new ProcessedIndexLayout(totalIndexCount, sizeof(uint), Uses32BitIndices(totalVertexCount)),
            processedSubMeshes,
            BuildMaterialSlots(modelMesh.Materials),
            modelMesh.BoundingBox,
            modelMesh.BoundingSphere,
            totalMeshletCount,
            totalIndexCount / 3)
        {
            Materials = modelMesh.Materials.ToArray(),
            Skeletons = modelMesh.Skeletons.ToArray(),
            Skins = modelMesh.Skins.ToArray(),
            AnimationClips = modelMesh.AnimationClips.ToArray()
        };
    }

    private ProcessedSubMeshAsset BuildSubMesh(
        ModelSubMesh subMesh,
        ModelMaterial material)
    {
        ValidateSubMesh(subMesh);
        RendererMeshletLodBuild meshletMesh = _meshletLodBuilder.Build(subMesh.Vertices, subMesh.Indices, subMesh.Name);

        ModelGiCausticHeroTopologyEvidence topologyEvidence = default;
        string topologyDetail = "participation-disabled";
        ModelGiCausticHeroValidation causticValidation;
        if (material.GiCausticParticipation ==
            ModelGiCausticParticipationMode.None)
        {
            causticValidation = ModelGiCausticHeroValidator.Validate(
                material.GiCausticParticipation,
                material.AlphaMode,
                material.GiTransmissionPolicy,
                material.Roughness,
                material.Ior,
                material.ThicknessFactor,
                material.AttenuationDistance,
                ToNumerics(material.AttenuationColor),
                topologyEvidence);
        }
        else if (!ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
                     subMesh.Vertices,
                     subMesh.Indices,
                     isSkinned: subMesh.SkinIndex >= 0,
                     out topologyEvidence,
                     out topologyDetail))
        {
            causticValidation = new ModelGiCausticHeroValidation(
                false,
                ModelGiCausticHeroValidationReason.MalformedTopologyEvidence,
                topologyDetail);
        }
        else
        {
            topologyDetail = "topology-evidence-authenticated";
            causticValidation = ModelGiCausticHeroValidator.Validate(
                material.GiCausticParticipation,
                material.AlphaMode,
                material.GiTransmissionPolicy,
                material.Roughness,
                material.Ior,
                material.ThicknessFactor,
                material.AttenuationDistance,
                ToNumerics(material.AttenuationColor),
                topologyEvidence);
        }

        return new ProcessedSubMeshAsset(
            string.IsNullOrWhiteSpace(subMesh.Name) ? "SubMesh" : subMesh.Name,
            subMesh.MaterialIndex,
            subMesh.NodeIndex,
            subMesh.SkinIndex,
            subMesh.SkinningBindTransform,
            new ProcessedVertexLayout(GetAttributes(subMesh), subMesh.Vertices.Length, sizeof(float) * 3, sizeof(uint)),
            new ProcessedIndexLayout(subMesh.Indices.Length, sizeof(uint), Uses32BitIndices(subMesh.Vertices.Length)),
            subMesh.Vertices.ToArray(),
            subMesh.Normals.ToArray(),
            subMesh.Tangents.ToArray(),
            subMesh.Bitangents.ToArray(),
            subMesh.TexCoords.ToArray(),
            subMesh.TexCoords1.ToArray(),
            subMesh.VertexColors.ToArray(),
            subMesh.JointIndices0.ToArray(),
            subMesh.JointWeights0.ToArray(),
            subMesh.Indices.ToArray(),
            meshletMesh.Meshlets.ToArray(),
            meshletMesh.MeshletVertices.ToArray(),
            meshletMesh.MeshletTriangles.ToArray(),
            subMesh.BoundingBox,
            subMesh.BoundingSphere,
            new[]
            {
                new ProcessedMeshDrawRange(
                    string.IsNullOrWhiteSpace(subMesh.Name) ? "SubMesh" : subMesh.Name,
                    subMesh.MaterialIndex,
                    FirstIndex: 0,
                    IndexCount: subMesh.Indices.Length,
                    BaseVertex: 0)
            },
            meshletMesh.Ranges)
        {
            CausticTopologyEvidence = topologyEvidence,
            CausticAuthoringValidation = causticValidation,
            CausticTopologyDetail = topologyDetail
        };
    }

    private static ModelMaterial ResolveMaterial(
        IReadOnlyList<ModelMaterial> materials,
        ModelSubMesh subMesh)
    {
        if (materials.Count == 0 && subMesh.MaterialIndex == 0)
            return ModelMaterial.Default;
        if ((uint)subMesh.MaterialIndex >= (uint)materials.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(subMesh),
                $"Submesh '{subMesh.Name}' references material {subMesh.MaterialIndex}, " +
                $"but the model contains {materials.Count} materials.");
        }
        return materials[subMesh.MaterialIndex];
    }

    private static System.Numerics.Vector4 ToNumerics(Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);

    private static IReadOnlyList<ProcessedMaterialSlot> BuildMaterialSlots(IReadOnlyList<ModelMaterial> materials)
    {
        if (materials.Count == 0)
            return new[] { new ProcessedMaterialSlot(0, ModelMaterial.Default.Name) };

        var slots = new ProcessedMaterialSlot[materials.Count];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = new ProcessedMaterialSlot(i, string.IsNullOrWhiteSpace(materials[i].Name) ? $"Material {i}" : materials[i].Name);

        return slots;
    }

    private static void ValidateSubMesh(ModelSubMesh subMesh)
    {
        if (subMesh.Vertices.Length == 0)
            throw new ArgumentException("Processed submesh requires vertices.", nameof(subMesh));
        if (subMesh.Indices.Length == 0 || subMesh.Indices.Length % 3 != 0)
            throw new ArgumentException("Processed submesh requires triangle indices.", nameof(subMesh));
        ValidateOptionalStream(subMesh.Normals, subMesh.Vertices.Length, nameof(subMesh.Normals));
        ValidateOptionalStream(subMesh.Tangents, subMesh.Vertices.Length, nameof(subMesh.Tangents));
        ValidateOptionalStream(subMesh.Bitangents, subMesh.Vertices.Length, nameof(subMesh.Bitangents));
        ValidateOptionalStream(subMesh.TexCoords, subMesh.Vertices.Length, nameof(subMesh.TexCoords));
        ValidateOptionalStream(subMesh.TexCoords1, subMesh.Vertices.Length, nameof(subMesh.TexCoords1));
        ValidateOptionalStream(subMesh.VertexColors, subMesh.Vertices.Length, nameof(subMesh.VertexColors));
        ValidateOptionalStream(subMesh.JointIndices0, subMesh.Vertices.Length, nameof(subMesh.JointIndices0));
        ValidateOptionalStream(subMesh.JointWeights0, subMesh.Vertices.Length, nameof(subMesh.JointWeights0));

        for (int i = 0; i < subMesh.Indices.Length; i++)
        {
            if (subMesh.Indices[i] >= subMesh.Vertices.Length)
                throw new ArgumentOutOfRangeException(nameof(subMesh), $"Index {i} references vertex {subMesh.Indices[i]}, but vertex count is {subMesh.Vertices.Length}.");
        }
    }

    private static void ValidateOptionalStream<T>(T[] stream, int vertexCount, string streamName)
    {
        if (stream.Length != 0 && stream.Length != vertexCount)
            throw new ArgumentException($"{streamName} length must either be zero or match vertex count.", streamName);
    }

    private static uint GetAttributes(ModelSubMesh subMesh)
    {
        uint attributes = (uint)ProcessedVertexAttribute.Position;
        AddIfPresent(ref attributes, subMesh.Normals.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.Normal);
        AddIfPresent(ref attributes, subMesh.Tangents.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.Tangent);
        AddIfPresent(ref attributes, subMesh.Bitangents.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.Bitangent);
        AddIfPresent(ref attributes, subMesh.TexCoords.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.TexCoord0);
        AddIfPresent(ref attributes, subMesh.TexCoords1.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.TexCoord1);
        AddIfPresent(ref attributes, subMesh.VertexColors.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.VertexColor);
        AddIfPresent(ref attributes, subMesh.JointIndices0.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.Joints0);
        AddIfPresent(ref attributes, subMesh.JointWeights0.Length, subMesh.Vertices.Length, ProcessedVertexAttribute.Weights0);
        return attributes;
    }

    private static void AddIfPresent(ref uint attributes, int streamLength, int vertexCount, ProcessedVertexAttribute attribute)
    {
        if (streamLength == vertexCount)
            attributes |= (uint)attribute;
    }

    private static bool Uses32BitIndices(int vertexCount)
    {
        return vertexCount > ushort.MaxValue;
    }
}
