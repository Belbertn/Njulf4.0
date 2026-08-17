using System.Security.Cryptography;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelAssetCookerTransactionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "model-cooker-transaction-tests",
            TestContext.CurrentContext.Test.ID,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Test]
    public void CookModel_WhenPrepublicationSigningFails_PreservesPreviousPackageGeneration()
    {
        string sourcePath = Path.Combine(_directory, "transaction.gltf");
        WriteTriangleGltf(sourcePath, extent: 1.0f);
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            Force = true,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            }
        };
        using var cooker = new ModelAssetCooker();
        cooker.CookModel(sourcePath, _directory, options);

        string modelPath = Path.Combine(
            _directory,
            "models",
            "transaction.njmodel");
        byte[] publishedBefore = File.ReadAllBytes(modelPath);
        CookedModelAsset oldPackage = CookedPackage.LoadModel(
            modelPath,
            CookedAssetReaderFlags.StrictSourceHash);
        string oldMeshPath = ResolveReference(
            modelPath,
            oldPackage.Manifest.Mesh.RelativePath);
        string oldMaterialPath = ResolveReference(
            modelPath,
            oldPackage.Manifest.Material.RelativePath);

        WriteTriangleGltf(sourcePath, extent: 2.0f);
        ModelCookOptions failingOptions = options with
        {
            SigningPrivateKey = "not-a-valid-private-key"
        };

        Assert.That(
            () => cooker.CookModel(
                sourcePath,
                _directory,
                failingOptions),
            Throws.Exception);

        byte[] publishedAfter = File.ReadAllBytes(modelPath);
        CookedModelAsset stillPublished = CookedPackage.LoadModel(
            modelPath,
            CookedAssetReaderFlags.StrictSourceHash);
        Assert.Multiple(() =>
        {
            Assert.That(
                SHA256.HashData(publishedAfter),
                Is.EqualTo(SHA256.HashData(publishedBefore)));
            Assert.That(
                stillPublished.Manifest.Mesh.RelativePath,
                Is.EqualTo(oldPackage.Manifest.Mesh.RelativePath));
            Assert.That(
                stillPublished.Manifest.Material.RelativePath,
                Is.EqualTo(oldPackage.Manifest.Material.RelativePath));
            Assert.That(File.Exists(oldMeshPath), Is.True);
            Assert.That(File.Exists(oldMaterialPath), Is.True);
            Assert.That(
                Directory.EnumerateFiles(
                    Path.GetDirectoryName(oldMeshPath)!,
                    "transaction.*.meshes.njmesh").Count(),
                Is.EqualTo(1));
            Assert.That(
                Directory.EnumerateFiles(
                    Path.GetDirectoryName(oldMaterialPath)!,
                    "transaction.*.materials.njmat").Count(),
                Is.EqualTo(1));
        });
    }

    [Test]
    public void CookModel_OpaqueMaterialNamedFoliage_DoesNotPreserveAlphaCoverage()
    {
        string sourcePath = Path.Combine(_directory, "foliage-trunk.gltf");
        WriteTexturedFoliageNamedTriangleGltf(sourcePath);
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            Force = true,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            },
            TextureOptions = new TextureCookOptions(
                MaxDimension: 16,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8)
        };
        using var cooker = new ModelAssetCooker();
        cooker.CookModel(sourcePath, _directory, options);

        CookedModelAsset cooked = CookedPackage.LoadModel(
            Path.Combine(_directory, "models", "foliage-trunk.njmodel"));
        ModelMaterial material = cooked.Materials.Materials.Single(
            candidate => candidate.Name == "Foliage_Trunk");
        CookedTextureMeta texture = CookedPackage.LoadTextureMeta(
            Path.ChangeExtension(
                material.BaseColorTexture!.Source!.FilePath!,
                ".njtex"));

        Assert.Multiple(() =>
        {
            Assert.That(cooked.Materials.Pipelines.Single(), Is.EqualTo(CookedMaterialPipeline.Opaque));
            Assert.That(texture.AlphaCoveragePreserved, Is.False);
            Assert.That(texture.AlphaCoverageCutoff, Is.Null);
        });
    }

    [Test]
    public void CookModel_ExplicitFoliage_UsesFoliagePipelineWithoutAlphaCoverage()
    {
        string sourcePath = Path.Combine(_directory, "explicit-foliage.gltf");
        WriteTexturedFoliageNamedTriangleGltf(sourcePath);
        string json = File.ReadAllText(sourcePath).Replace(
            "\"name\": \"Foliage_Trunk\",",
            "\"name\": \"Foliage_Trunk\", \"extras\": { \"NJULF_foliage\": true },",
            StringComparison.Ordinal);
        File.WriteAllText(sourcePath, json);
        var options = new ModelCookOptions
        {
            UsePlatformSubdirectory = false,
            Force = true,
            ImporterOptions = new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            },
            TextureOptions = new TextureCookOptions(
                MaxDimension: 16,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8)
        };
        using var cooker = new ModelAssetCooker();
        cooker.CookModel(sourcePath, _directory, options);

        CookedModelAsset cooked = CookedPackage.LoadModel(
            Path.Combine(_directory, "models", "explicit-foliage.njmodel"));
        ModelMaterial material = cooked.Materials.Materials.Single(
            candidate => candidate.Name == "Foliage_Trunk");
        CookedTextureMeta texture = CookedPackage.LoadTextureMeta(
            Path.ChangeExtension(
                material.BaseColorTexture!.Source!.FilePath!,
                ".njtex"));

        Assert.Multiple(() =>
        {
            Assert.That(cooked.Materials.Pipelines.Single(), Is.EqualTo(CookedMaterialPipeline.Foliage));
            Assert.That(texture.AlphaCoveragePreserved, Is.False);
            Assert.That(texture.AlphaCoverageCutoff, Is.Null);
        });
    }

    private static string ResolveReference(
        string modelPath,
        string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(modelPath)!,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void WriteTriangleGltf(
        string gltfPath,
        float extent)
    {
        string directory = Path.GetDirectoryName(gltfPath)!;
        Directory.CreateDirectory(directory);
        string binPath = Path.ChangeExtension(gltfPath, ".bin");
        byte[] positions =
        [
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(extent),
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(extent),
            .. BitConverter.GetBytes(0f)
        ];
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        File.WriteAllBytes(
            binPath,
            [.. positions, .. indices]);
        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf cooker transaction test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0 }],
                "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "mode": 4 }] }],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{positions.Length + indices.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  {
                    "bufferView": 0,
                    "componentType": 5126,
                    "count": 3,
                    "type": "VEC3",
                    "min": [0, 0, 0],
                    "max": [{{extent.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{extent.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, 0]
                  },
                  {
                    "bufferView": 1,
                    "componentType": 5123,
                    "count": 3,
                    "type": "SCALAR",
                    "min": [0],
                    "max": [2]
                  }
                ]
              }
              """);
    }

    private static void WriteTexturedFoliageNamedTriangleGltf(string gltfPath)
    {
        WriteTriangleGltf(gltfPath, extent: 1.0f);
        string directory = Path.GetDirectoryName(gltfPath)!;
        string texturePath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(gltfPath) + ".png");
        File.WriteAllBytes(
            texturePath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII="));

        string json = File.ReadAllText(gltfPath)
            .Replace(
                "\"mode\": 4",
                "\"mode\": 4, \"material\": 0",
                StringComparison.Ordinal)
            .Replace(
                "\"buffers\":",
                $$"""
                "materials": [
                  {
                    "name": "Foliage_Trunk",
                    "pbrMetallicRoughness": {
                      "baseColorTexture": { "index": 0 }
                    }
                  }
                ],
                "images": [
                  { "uri": "{{Path.GetFileName(texturePath)}}", "mimeType": "image/png" }
                ],
                "textures": [{ "source": 0 }],
                "buffers":
                """,
                StringComparison.Ordinal);
        File.WriteAllText(gltfPath, json);
    }
}
