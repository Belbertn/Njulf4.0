using Njulf.Core.Geometry;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using System.Diagnostics;

namespace Njulf.Assets.Cooked;

public static class CookedMeshBuilder
{
    public static CookedMeshPayload Build(ProcessedMeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var positions = new List<CookedVertexPositionStream>(asset.VertexLayout.VertexCount);
        var normalTangents = new List<CookedVertexNormalTangentStream>(asset.VertexLayout.VertexCount);
        var uvColors = new List<CookedVertexUvColorStream>(asset.VertexLayout.VertexCount);
        var skinning = new List<CookedVertexSkinningData>();
        var indices = new List<uint>(asset.IndexLayout.IndexCount);
        var meshletsLod0 = new List<Meshlet>();
        var meshletsLod1 = new List<Meshlet>();
        var meshletsLod2 = new List<Meshlet>();
        var meshletVertices = new List<uint>();
        var meshletTriangles = new List<uint>();
        var records = new List<CookedSubMeshRecord>(asset.SubMeshes.Count);

        foreach (ProcessedSubMeshAsset subMesh in asset.SubMeshes)
        {
            int vertexOffset = positions.Count;
            int indexOffset = indices.Count;
            int skinningOffset = skinning.Count;
            int meshletOffset = meshletsLod0.Count;
            int meshletLod1Offset = meshletsLod1.Count;
            int meshletLod2Offset = meshletsLod2.Count;
            int meshletVertexOffset = meshletVertices.Count;
            int meshletTriangleOffset = meshletTriangles.Count;

            Vector3[] normals = subMesh.Normals.Length == subMesh.Vertices.Length
                ? subMesh.Normals
                : ComputeNormals(subMesh.Vertices, subMesh.Indices);
            for (int i = 0; i < subMesh.Vertices.Length; i++)
            {
                Vector3 normal = NormalizeOrDefault(normals[i], Vector3.UnitZ);
                Vector3 tangent = subMesh.Tangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Tangents[i], Vector3.UnitX)
                    : Vector3.UnitX;
                Vector3 bitangent = subMesh.Bitangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Bitangents[i], Vector3.Zero)
                    : Vector3.Zero;
                float handedness = bitangent.LengthSquared() <= 1e-12f
                    ? 1f
                    : Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0f ? -1f : 1f;
                positions.Add(new CookedVertexPositionStream { Position = new Vector4(subMesh.Vertices[i], 1f) });
                normalTangents.Add(new CookedVertexNormalTangentStream
                {
                    Normal = new Vector4(normal, 0f),
                    Tangent = new Vector4(tangent.X, tangent.Y, tangent.Z, handedness)
                });
                uvColors.Add(new CookedVertexUvColorStream
                {
                    TexCoord = subMesh.TexCoords.Length == subMesh.Vertices.Length ? subMesh.TexCoords[i] : Vector2.Zero,
                    TexCoord2 = subMesh.TexCoords1.Length == subMesh.Vertices.Length ? subMesh.TexCoords1[i] : Vector2.Zero,
                    Color = subMesh.VertexColors.Length == subMesh.Vertices.Length ? subMesh.VertexColors[i] : Vector4.One
                });
            }

            if (subMesh.SkinIndex >= 0)
            {
                if (subMesh.JointIndices0.Length != subMesh.Vertices.Length || subMesh.JointWeights0.Length != subMesh.Vertices.Length)
                    throw new InvalidOperationException($"Skinned submesh '{subMesh.Name}' does not provide complete joint and weight streams.");
                for (int i = 0; i < subMesh.Vertices.Length; i++)
                {
                    VertexJointIndices joints = subMesh.JointIndices0[i];
                    VertexJointWeights weights = subMesh.JointWeights0[i].Normalized();
                    skinning.Add(new CookedVertexSkinningData
                    {
                        Joint0 = joints.X, Joint1 = joints.Y, Joint2 = joints.Z, Joint3 = joints.W,
                        Weight0 = weights.X, Weight1 = weights.Y, Weight2 = weights.Z, Weight3 = weights.W
                    });
                }
            }

            indices.AddRange(subMesh.Indices);
            if (subMesh.LodRanges.Count != 3)
                throw new InvalidOperationException($"Submesh '{subMesh.Name}' must contain exactly three meshlet LOD ranges.");
            for (int level = 0; level < 3; level++)
            {
                ProcessedMeshLodRange range = subMesh.LodRanges.Single(item => item.Level == level);
                List<Meshlet> destination = level switch
                {
                    0 => meshletsLod0,
                    1 => meshletsLod1,
                    2 => meshletsLod2,
                    _ => throw new UnreachableException()
                };
                foreach (Meshlet sourceMeshlet in subMesh.Meshlets.AsSpan(range.FirstMeshlet, range.MeshletCount))
                {
                    Meshlet cookedMeshlet = sourceMeshlet;
                    cookedMeshlet.VertexOffset = 0;
                    cookedMeshlet.IndexOffset = 0;
                    destination.Add(cookedMeshlet);
                }
            }
            meshletVertices.AddRange(subMesh.MeshletVertices);
            meshletTriangles.AddRange(subMesh.MeshletTriangles);
            ProcessedMeshLodRange lod0 = subMesh.LodRanges.Single(item => item.Level == 0);
            ProcessedMeshLodRange lod1 = subMesh.LodRanges.Single(item => item.Level == 1);
            ProcessedMeshLodRange lod2 = subMesh.LodRanges.Single(item => item.Level == 2);
            records.Add(new CookedSubMeshRecord(
                subMesh.Name,
                subMesh.MaterialSlot,
                subMesh.NodeIndex,
                subMesh.SkinIndex,
                subMesh.SkinningBindTransform,
                vertexOffset,
                subMesh.Vertices.Length,
                indexOffset,
                subMesh.Indices.Length,
                skinningOffset,
                subMesh.SkinIndex >= 0 ? subMesh.Vertices.Length : 0,
                meshletOffset,
                lod0.MeshletCount,
                meshletVertexOffset,
                subMesh.MeshletVertices.Length,
                meshletTriangleOffset,
                subMesh.MeshletTriangles.Length,
                subMesh.LodRanges,
                subMesh.DrawRanges,
                subMesh.BoundingBox,
                subMesh.BoundingSphere,
                subMesh.VertexLayout.Attributes)
            {
                MeshletLod1Offset = meshletLod1Offset,
                MeshletLod1Count = lod1.MeshletCount,
                MeshletLod2Offset = meshletLod2Offset,
                MeshletLod2Count = lod2.MeshletCount
            });
        }

        return new CookedMeshPayload(
            records,
            positions.ToArray(),
            normalTangents.ToArray(),
            uvColors.ToArray(),
            skinning.ToArray(),
            indices.ToArray(),
            meshletsLod0.ToArray(),
            meshletsLod1.ToArray(),
            meshletsLod2.ToArray(),
            meshletVertices.ToArray(),
            meshletTriangles.ToArray());
    }

    private static Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int i0 = checked((int)indices[i]);
            int i1 = checked((int)indices[i + 1]);
            int i2 = checked((int)indices[i + 2]);
            Vector3 face = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            if (face.LengthSquared() > 1e-20f)
                face = face.Normalized();
            normals[i0] += face;
            normals[i1] += face;
            normals[i2] += face;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = NormalizeOrDefault(normals[i], Vector3.UnitZ);
        return normals;
    }

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-20f ? value.Normalized() : fallback;
}
