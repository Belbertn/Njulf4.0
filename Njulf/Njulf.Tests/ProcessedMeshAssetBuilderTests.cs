using System;
using System.IO;
using System.Linq;
using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProcessedMeshAssetBuilderTests
{
    [Test]
    public void Build_CapturesLayoutBoundsMaterialSlotsDrawRangesAndMeshlets()
    {
        ModelMesh mesh = CreateTwoSubMeshModel();

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(mesh, "memory://two-submesh");

        Assert.Multiple(() =>
        {
            Assert.That(asset.Name, Is.EqualTo(mesh.Name));
            Assert.That(asset.SourcePath, Is.EqualTo("memory://two-submesh"));
            Assert.That(asset.SubMeshes, Has.Count.EqualTo(2));
            Assert.That(asset.MaterialSlots, Has.Count.EqualTo(2));
            Assert.That(asset.MaterialSlots[1].Name, Is.EqualTo("mat-1"));
            Assert.That(asset.VertexLayout.Has(ProcessedVertexAttribute.Position), Is.True);
            Assert.That(asset.VertexLayout.Has(ProcessedVertexAttribute.Normal), Is.True);
            Assert.That(asset.VertexLayout.Has(ProcessedVertexAttribute.TexCoord0), Is.True);
            Assert.That(asset.VertexLayout.Has(ProcessedVertexAttribute.VertexColor), Is.True);
            Assert.That(asset.IndexLayout.IndexCount, Is.EqualTo(6));
            Assert.That(asset.TotalTriangleCount, Is.EqualTo(2));
            Assert.That(asset.TotalMeshletCount, Is.EqualTo(asset.SubMeshes.Sum(subMesh => subMesh.Meshlets.Length)));
            Assert.That(asset.BoundingBox, Is.EqualTo(mesh.BoundingBox));
        });

        ProcessedSubMeshAsset first = asset.SubMeshes[0];
        Assert.Multiple(() =>
        {
            Assert.That(first.DrawRanges, Has.Count.EqualTo(1));
            Assert.That(first.DrawRanges[0].MaterialSlot, Is.EqualTo(0));
            Assert.That(first.DrawRanges[0].IndexCount, Is.EqualTo(3));
            Assert.That(first.LodRanges, Has.Count.EqualTo(1));
            Assert.That(first.LodRanges[0].Level, Is.EqualTo(0));
            Assert.That(first.LodRanges[0].MeshletCount, Is.EqualTo(first.Meshlets.Length));
            Assert.That(first.MeshletTriangles, Has.Length.EqualTo(first.Indices.Length));
        });
    }

    [Test]
    public void Build_ProducesSameMeshletCountsAsAssetRuntimeMeshletBuilder()
    {
        ModelMesh mesh = CreateTwoSubMeshModel();
        var meshletBuilder = new MeshletBuilder();

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder(meshletBuilder).Build(mesh);

        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            ModelSubMesh subMesh = mesh.SubMeshes[i];
            MeshletMesh runtimeMeshlets = meshletBuilder.BuildMeshlets(
                subMesh.Vertices,
                subMesh.Indices,
                subMesh.Normals,
                subMesh.Tangents,
                subMesh.Bitangents,
                subMesh.TexCoords,
                subMesh.Name);

            Assert.That(asset.SubMeshes[i].Meshlets, Has.Length.EqualTo(runtimeMeshlets.Meshlets.Length));
            Assert.That(asset.SubMeshes[i].MeshletTriangles, Has.Length.EqualTo(runtimeMeshlets.MeshletTriangles.Length));
        }
    }

    [Test]
    public void Build_RepresentativeSampleScenePreservesSubmeshMaterialsAndMeshletCounts()
    {
        string path = FindRepoFile("NjulfHelloGame", "NewSponza_Main_glTF_003.gltf");
        using var importer = new ModelImporter();
        ModelMesh imported = importer.Import(path, new ImporterOptions { Backend = ModelImportBackend.SharpGltf });

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(imported, path);

        Assert.Multiple(() =>
        {
            Assert.That(asset.SubMeshes, Has.Count.EqualTo(imported.SubMeshes.Count));
            Assert.That(asset.MaterialSlots, Has.Count.EqualTo(imported.Materials.Count));
            Assert.That(asset.TotalTriangleCount, Is.EqualTo(imported.SubMeshes.Sum(subMesh => subMesh.Indices.Length / 3)));
            Assert.That(asset.TotalMeshletCount, Is.EqualTo(asset.SubMeshes.Sum(subMesh => subMesh.Meshlets.Length)));
            Assert.That(asset.SubMeshes.Select(subMesh => subMesh.MaterialSlot), Is.EquivalentTo(imported.SubMeshes.Select(subMesh => subMesh.MaterialIndex)));
            Assert.That(asset.SubMeshes, Has.All.Matches<ProcessedSubMeshAsset>(subMesh => subMesh.LodRanges.Count == 1));
        });
    }

    [Test]
    public void Build_RejectsInvalidOptionalStreamLength()
    {
        ModelMesh mesh = CreateTwoSubMeshModel();
        mesh.SubMeshes[0].Normals = new[] { Vector3.UnitZ };

        Assert.That(
            () => new ProcessedMeshAssetBuilder().Build(mesh),
            Throws.ArgumentException.With.Message.Contains(nameof(ModelSubMesh.Normals)));
    }

    private static ModelMesh CreateTwoSubMeshModel()
    {
        var mesh = new ModelMesh
        {
            Name = "TwoSubMesh",
            BoundingBox = new BoundingBox(new Vector3(0f, 0f, 0f), new Vector3(3f, 1f, 0f)),
            BoundingSphere = new BoundingSphere(new Vector3(1.5f, 0.5f, 0f), 1.6f)
        };
        mesh.Materials.Add(new ModelMaterial { Name = "mat-0" });
        mesh.Materials.Add(new ModelMaterial { Name = "mat-1" });
        mesh.SubMeshes.Add(CreateTriangleSubMesh("left", 0, 0f, includeColor: true));
        mesh.SubMeshes.Add(CreateTriangleSubMesh("right", 1, 2f, includeColor: false));
        return mesh;
    }

    private static ModelSubMesh CreateTriangleSubMesh(string name, int materialIndex, float xOffset, bool includeColor)
    {
        var vertices = new[]
        {
            new Vector3(xOffset, 0f, 0f),
            new Vector3(xOffset + 1f, 0f, 0f),
            new Vector3(xOffset, 1f, 0f)
        };

        return new ModelSubMesh
        {
            Name = name,
            MaterialIndex = materialIndex,
            Vertices = vertices,
            Normals = Enumerable.Repeat(Vector3.UnitZ, vertices.Length).ToArray(),
            Tangents = Enumerable.Repeat(Vector3.UnitX, vertices.Length).ToArray(),
            Bitangents = Enumerable.Repeat(Vector3.UnitY, vertices.Length).ToArray(),
            TexCoords =
            [
                Vector2.Zero,
                Vector2.UnitX,
                Vector2.UnitY
            ],
            VertexColors = includeColor
                ?
                [
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(0f, 1f, 0f, 1f),
                    new Vector4(0f, 0f, 1f, 1f)
                ]
                : Array.Empty<Vector4>(),
            Indices = [0, 1, 2],
            BoundingBox = new BoundingBox(vertices[0], vertices[1]),
            BoundingSphere = new BoundingSphere(vertices[0], 1f)
        };
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        string directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(pathParts)}'.");
    }
}
