using System;
using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal static class SampleProceduralMeshAssets
{
    public static MeshHandle Register(
        MeshManager meshManager,
        string assetId,
        GPUVertex[] vertices,
        uint[] indices)
    {
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("Procedural mesh asset id is required.", nameof(assetId));
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (indices == null)
            throw new ArgumentNullException(nameof(indices));
        if (vertices.Length == 0)
            throw new ArgumentException("Procedural mesh must contain at least one vertex.", nameof(vertices));
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Procedural mesh indices must contain complete triangles.", nameof(indices));

        ModelMesh modelMesh = CreateModelMesh(assetId, vertices, indices);
        ProcessedMeshAsset processedAsset = new ProcessedMeshAssetBuilder().Build(
            modelMesh,
            new ProcessedMeshBuildOptions
            {
                AssetId = assetId,
                SourcePath = $"procedural://{assetId}",
                GenerateFallbackLods = false
            });

        return meshManager.RegisterProcessedMeshAsset(processedAsset);
    }

    private static ModelMesh CreateModelMesh(string assetId, GPUVertex[] vertices, uint[] indices)
    {
        Vector3[] positions = new Vector3[vertices.Length];
        Vector3[] normals = new Vector3[vertices.Length];
        Vector3[] tangents = new Vector3[vertices.Length];
        Vector2[] texCoords = new Vector2[vertices.Length];
        Vector2[] texCoords1 = new Vector2[vertices.Length];
        Vector4[] colors = new Vector4[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
        {
            GPUVertex vertex = vertices[i];
            positions[i] = vertex.Position;
            normals[i] = vertex.Normal;
            tangents[i] = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            texCoords[i] = vertex.TexCoord;
            texCoords1[i] = vertex.TexCoord2;
            colors[i] = vertex.Color;
        }

        BoundingBox bounds = BoundingBox.FromPoints(positions);
        BoundingSphere sphere = BoundingSphere.FromBox(bounds);

        var subMesh = new ModelSubMesh
        {
            Name = assetId,
            MaterialIndex = 0,
            Vertices = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = texCoords,
            TexCoords1 = texCoords1,
            VertexColors = colors,
            Indices = (uint[])indices.Clone(),
            BoundingBox = bounds,
            BoundingSphere = sphere
        };

        var modelMesh = new ModelMesh
        {
            Name = assetId,
            Vertices = positions,
            Normals = normals,
            Tangents = tangents,
            TexCoords = texCoords,
            TexCoords1 = texCoords1,
            VertexColors = colors,
            Indices = (uint[])indices.Clone(),
            BoundingBox = bounds,
            BoundingSphere = sphere
        };
        modelMesh.SubMeshes.Add(subMesh);
        modelMesh.Materials.Add(ModelMaterial.Default);
        return modelMesh;
    }
}
