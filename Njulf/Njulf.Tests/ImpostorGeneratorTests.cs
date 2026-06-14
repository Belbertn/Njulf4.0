using System.Collections.Generic;
using System.IO;
using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class ImpostorGeneratorTests
{
    [Test]
    public void Generate_UsesRenderBackendAndReturnsValidatedMetadata()
    {
        ModelMesh mesh = CreateMesh();
        var generator = new ImpostorGenerator(new FakeImpostorBackend(new ImpostorRenderResult(
            new[]
            {
                new ImpostorAtlasTexture(ImpostorTextureKind.Albedo, "tree/impostor/albedo", TextureColorSpace.Srgb),
                new ImpostorAtlasTexture(ImpostorTextureKind.Normal, "tree/impostor/normal", TextureColorSpace.Linear),
                new ImpostorAtlasTexture(ImpostorTextureKind.Depth, "tree/impostor/depth", TextureColorSpace.Linear)
            },
            0.8f,
            12f,
            -1f,
            "octahedral")));

        ImpostorAssetMetadata metadata = generator.Generate(mesh, new ImpostorGenerationSettings
        {
            AtlasAssetId = "tree/impostor",
            AtlasWidth = 1024,
            AtlasHeight = 1024,
            ViewDirectionCount = 32
        });

        metadata.Validate("tree");
        Assert.That(metadata.AtlasAssetId, Is.EqualTo("tree/impostor"));
        Assert.That(metadata.Textures, Has.Count.EqualTo(3));
        Assert.That(metadata.ViewDirectionCount, Is.EqualTo(32));
        Assert.That(metadata.AlphaCoverage, Is.EqualTo(0.8f));
    }

    [Test]
    public void Generate_RejectsBackendWithoutAlbedoAtlas()
    {
        var generator = new ImpostorGenerator(new FakeImpostorBackend(new ImpostorRenderResult(
            new[] { new ImpostorAtlasTexture(ImpostorTextureKind.Depth, "tree/impostor/depth", TextureColorSpace.Linear) },
            0.8f,
            1f,
            0f,
            "octahedral")));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => generator.Generate(CreateMesh(), new ImpostorGenerationSettings
        {
            AtlasAssetId = "tree/impostor"
        }))!;

        Assert.That(ex.Message, Does.Contain("albedo"));
    }

    [Test]
    public void SoftwareBackend_WritesRealAtlasFiles()
    {
        string outputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "impostor-atlas-output");
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);

        var generator = new ImpostorGenerator(new SoftwareImpostorRenderBackend(outputDirectory));

        ImpostorAssetMetadata metadata = generator.Generate(CreateMesh(), new ImpostorGenerationSettings
        {
            AtlasAssetId = "tree/software",
            AtlasWidth = 16,
            AtlasHeight = 16,
            ViewDirectionCount = 8,
            IncludeRoughnessMetalnessAtlas = true
        });

        metadata.Validate("tree/software");
        Assert.That(metadata.Textures, Has.Count.EqualTo(4));
        foreach (ImpostorAtlasTexture texture in metadata.Textures)
        {
            Assert.That(File.Exists(texture.AssetId), Is.True, texture.AssetId);
            Assert.That(new FileInfo(texture.AssetId).Length, Is.GreaterThan(16 * 16 * 3));
        }
    }

    private static ModelMesh CreateMesh()
    {
        var bounds = new BoundingBox(new Vector3(-1f), new Vector3(1f));
        return new ModelMesh
        {
            Name = "Tree",
            Vertices = new[] { new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 2f, 0f) },
            Indices = new uint[] { 0, 1, 2 },
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds)
        };
    }

    private sealed class FakeImpostorBackend : IImpostorRenderBackend
    {
        private readonly ImpostorRenderResult _result;

        public FakeImpostorBackend(ImpostorRenderResult result)
        {
            _result = result;
        }

        public ImpostorRenderResult Render(ModelMesh source, ImpostorGenerationSettings settings) => _result;
    }
}
