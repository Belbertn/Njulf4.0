using System;
using System.IO;
using System.Linq;
using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class ProcessedMeshAssetBuilderTests
{
    [Test]
    public void Build_PreservesAuthoredLodChainFromNames()
    {
        ModelMesh mesh = CreateAuthoredLodMesh();

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(mesh, new ProcessedMeshBuildOptions
        {
            AssetId = "test/authored",
            GenerateFallbackLods = true
        });

        Assert.That(asset.Lods, Has.Count.EqualTo(2));
        Assert.That(asset.Lods[0].Provenance, Is.EqualTo(MeshLodProvenance.Authored));
        Assert.That(asset.Lods[1].Provenance, Is.EqualTo(MeshLodProvenance.Authored));
        Assert.That(asset.Lods[1].TriangleCount, Is.LessThan(asset.Lods[0].TriangleCount));
        Assert.That(asset.Meshlets.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(asset.LodDiagnostics.Select(d => d.TriangleCount), Is.EqualTo(new[] { 2u, 1u }));
    }

    [Test]
    public void Build_GeneratesDeterministicFallbackLods()
    {
        ModelMesh mesh = CreateSingleLodMesh("Rock");
        var builder = new ProcessedMeshAssetBuilder();

        ProcessedMeshAsset first = builder.Build(mesh, new ProcessedMeshBuildOptions { AssetId = "test/generated" });
        ProcessedMeshAsset second = builder.Build(mesh, new ProcessedMeshBuildOptions { AssetId = "test/generated" });

        Assert.That(first.Lods, Has.Count.EqualTo(3));
        Assert.That(first.Lods[1].Provenance, Is.EqualTo(MeshLodProvenance.GeneratedFallback));
        Assert.That(first.Lods.Select(l => l.IndexCount), Is.EqualTo(second.Lods.Select(l => l.IndexCount)));
        Assert.That(first.MeshletTriangles, Is.EqualTo(second.MeshletTriangles));
        Assert.That(first.Lods[1].Quality.TriangleReduction, Is.GreaterThan(0f));
    }

    [Test]
    public void Build_UsesExplicitProjectMetadataForLodLevelsAndSwitchDistances()
    {
        string metadataPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, "lod-metadata-test.json");
        File.WriteAllText(metadataPath, """
        {
          "submeshes": {
            "High": 0,
            "Low": 1
          },
          "lods": [
            { "level": 0, "switchDistance": 0, "screenRelativeTransitionHeight": 0.7 },
            { "level": 1, "switchDistance": 55, "screenRelativeTransitionHeight": 0.25 }
          ]
        }
        """);
        ModelMesh mesh = new() { Name = "MetadataLod" };
        mesh.Materials.Add(ModelMaterial.Default);
        mesh.SubMeshes.Add(CreateQuadSubmesh("High", 0));
        mesh.SubMeshes.Add(CreateTriangleSubmesh("Low", 0));
        mesh.Vertices = mesh.SubMeshes[0].Vertices;
        mesh.Indices = mesh.SubMeshes[0].Indices;
        mesh.BoundingBox = UnitBox();
        mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(mesh, new ProcessedMeshBuildOptions
        {
            ProjectMetadataPath = metadataPath
        });

        Assert.That(asset.Lods, Has.Count.EqualTo(2));
        Assert.That(asset.Lods[1].SwitchDistance, Is.EqualTo(55f));
        Assert.That(asset.Lods[1].ScreenRelativeTransitionHeight, Is.EqualTo(0.25f));
    }

    [Test]
    public void Build_UsesImporterProvidedGltfExtrasLodLevels()
    {
        ModelMesh mesh = new() { Name = "GltfExtrasLod" };
        mesh.Materials.Add(ModelMaterial.Default);
        ModelSubMesh high = CreateQuadSubmesh("AnyHighName", 0);
        ModelSubMesh low = CreateTriangleSubmesh("AnyLowName", 0);
        high.LodLevel = 0;
        low.LodLevel = 1;
        mesh.SubMeshes.Add(high);
        mesh.SubMeshes.Add(low);
        mesh.Vertices = high.Vertices;
        mesh.Indices = high.Indices;
        mesh.BoundingBox = UnitBox();
        mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(mesh);

        Assert.That(asset.Lods, Has.Count.EqualTo(2));
        Assert.That(asset.Lods[0].Provenance, Is.EqualTo(MeshLodProvenance.Authored));
        Assert.That(asset.Lods[1].TriangleCount, Is.EqualTo(1));
    }

    [Test]
    public void Build_RejectsInvalidAuthoredLodMaterialSlots()
    {
        ModelMesh mesh = CreateAuthoredLodMesh();
        mesh.SubMeshes[1].MaterialIndex = 1;
        mesh.Materials.Add(ModelMaterial.Default);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new ProcessedMeshAssetBuilder().Build(mesh))!;

        Assert.That(ex.Message, Does.Contain("material slots"));
    }

    [Test]
    public void Build_EmitsFoliageAndImpostorMetadataWhenRequested()
    {
        ModelMesh mesh = CreateSingleLodMesh("GrassPatch");
        mesh.Materials[0].AlphaMode = ModelAlphaMode.Mask;
        mesh.Materials[0].DoubleSided = true;

        ProcessedMeshAsset asset = new ProcessedMeshAssetBuilder().Build(mesh, new ProcessedMeshBuildOptions
        {
            AssetId = "test/grass",
            IsFoliage = true,
            FoliageInstancesPerCluster = 64,
            GenerateImpostorMetadata = true,
            ImpostorAtlasWidth = 1024,
            ImpostorAtlasHeight = 1024
        });

        Assert.That(asset.Flags.HasFlag(ProcessedMeshFlags.Foliage), Is.True);
        Assert.That(asset.Flags.HasFlag(ProcessedMeshFlags.SupportsImpostor), Is.True);
        Assert.That(asset.Foliage!.Clusters[0].InstanceCount, Is.EqualTo(64));
        Assert.That(asset.Impostor!.Textures[0].Kind, Is.EqualTo(ImpostorTextureKind.Albedo));
    }

    [Test]
    public void Validate_RejectsMalformedImpostorAtlas()
    {
        ProcessedMeshAsset asset = new ProcessedMeshAsset(
            "test/bad-impostor",
            "source",
            ProcessedMeshAssetValidator.ComputeSourceHash("source"),
            ProcessedMeshVersion.Current,
            new[] { new ProcessedSubmesh(0, 0, 3, UnitBox()) },
            new[] { new ProcessedMeshLod(0, MeshLodProvenance.Authored, 0, 3, 0, 3, 0, 1, UnitBox(), new ProcessedLodQualityMetrics(0f, 0f, 0f, 0f, 0f, true)) },
            new[] { 0 },
            ProcessedMeshFlags.SupportsImpostor,
            null,
            new ImpostorAssetMetadata("", 0, 512, 0, UnitBox(), Vector3.Zero, 2f),
            ContentBudgetStatus.Pass);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(asset.Validate)!;
        Assert.That(ex.Message, Does.Contain("atlas"));
    }

    private static ModelMesh CreateAuthoredLodMesh()
    {
        ModelMesh mesh = new() { Name = "Crate" };
        mesh.Materials.Add(ModelMaterial.Default);
        mesh.SubMeshes.Add(CreateQuadSubmesh("Crate_LOD0", 0));
        mesh.SubMeshes.Add(CreateTriangleSubmesh("Crate_LOD1", 0));
        mesh.Vertices = mesh.SubMeshes[0].Vertices;
        mesh.Indices = mesh.SubMeshes[0].Indices;
        mesh.BoundingBox = UnitBox();
        mesh.BoundingSphere = BoundingSphere.FromBox(mesh.BoundingBox);
        return mesh;
    }

    private static ModelMesh CreateSingleLodMesh(string name)
    {
        ModelSubMesh submesh = CreateQuadSubmesh(name, 0);
        ModelMesh mesh = new()
        {
            Name = name,
            Vertices = submesh.Vertices,
            Normals = submesh.Normals,
            Tangents = submesh.Tangents,
            TexCoords = submesh.TexCoords,
            Indices = submesh.Indices,
            BoundingBox = submesh.BoundingBox,
            BoundingSphere = submesh.BoundingSphere
        };
        mesh.Materials.Add(ModelMaterial.Default);
        mesh.SubMeshes.Add(submesh);
        return mesh;
    }

    private static ModelSubMesh CreateQuadSubmesh(string name, int material)
    {
        var vertices = new[]
        {
            new Vector3(-1f, -1f, 0f),
            new Vector3(1f, -1f, 0f),
            new Vector3(1f, 1f, 0f),
            new Vector3(-1f, 1f, 0f)
        };
        return new ModelSubMesh
        {
            Name = name,
            MaterialIndex = material,
            Vertices = vertices,
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(),
            Tangents = Enumerable.Repeat(Vector3.UnitX, 4).ToArray(),
            TexCoords = new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY },
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
            BoundingBox = BoundingBox.FromPoints(vertices),
            BoundingSphere = BoundingSphere.FromBox(BoundingBox.FromPoints(vertices))
        };
    }

    private static ModelSubMesh CreateTriangleSubmesh(string name, int material)
    {
        var vertices = new[]
        {
            new Vector3(-1f, -1f, 0f),
            new Vector3(1f, -1f, 0f),
            new Vector3(0f, 1f, 0f)
        };
        return new ModelSubMesh
        {
            Name = name,
            MaterialIndex = material,
            Vertices = vertices,
            Normals = Enumerable.Repeat(Vector3.UnitZ, 3).ToArray(),
            Tangents = Enumerable.Repeat(Vector3.UnitX, 3).ToArray(),
            TexCoords = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
            Indices = new uint[] { 0, 1, 2 },
            BoundingBox = BoundingBox.FromPoints(vertices),
            BoundingSphere = BoundingSphere.FromBox(BoundingBox.FromPoints(vertices))
        };
    }

    private static BoundingBox UnitBox() => new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
}
