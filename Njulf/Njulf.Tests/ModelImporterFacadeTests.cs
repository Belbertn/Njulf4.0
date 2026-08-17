using System;
using System.IO;
using System.Linq;
using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelImporterFacadeTests
{
    [Test]
    public void ImportDetailed_DefaultBackendPreservesAssimpForObj()
    {
        string path = WriteTriangleObj();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(path);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(result.Status, Is.EqualTo(ModelImportStatus.Imported));
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.Assimp));
            Assert.That(result.BackendName, Is.EqualTo("Assimp"));
            Assert.That(result.BackendVersion, Is.Not.Empty);
            Assert.That(result.Mesh, Is.Not.Null);
            Assert.That(result.Mesh!.Vertices, Has.Length.EqualTo(3));
            Assert.That(result.Mesh.Indices, Has.Length.EqualTo(3));
            Assert.That(result.Diagnostics, Is.SameAs(result.Mesh.ImportDiagnostics));
        });
    }

    [Test]
    public void ImportDetailed_AssimpPackedRoughnessMetallicConventionDoesNotAliasOcclusion()
    {
        string path = WriteTexturedTriangleObj();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.Assimp,
                AssimpMaterialTextureConvention =
                    AssimpMaterialTextureConvention.SpecularGbIsRoughnessMetallic
            });
        ModelMaterial material = result.Mesh!.Materials.Single(
            candidate => candidate.BaseColorTexture?.Source?.FilePath?.EndsWith(
                "base.png",
                StringComparison.OrdinalIgnoreCase) == true);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(material.BaseColorTexture?.Source?.FilePath, Does.EndWith("base.png").IgnoreCase);
            Assert.That(material.NormalTexture?.Source?.FilePath, Does.EndWith("normal.png").IgnoreCase);
            Assert.That(
                material.MetallicRoughnessTexture?.Source?.FilePath,
                Does.EndWith("packed-rm.png").IgnoreCase);
            Assert.That(material.OcclusionTexture, Is.Null);
            Assert.That(material.OcclusionTexturePath, Is.Null);
            Assert.That(material.BaseColorTexture?.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));
            Assert.That(material.MetallicRoughnessTexture?.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
        });
    }

    [Test]
    public void ImportDetailed_AmazonBistroConventionMarksDirectXNormalMaps()
    {
        string path = WriteTexturedTriangleObj();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.Assimp,
                AssimpMaterialTextureConvention =
                    AssimpMaterialTextureConvention.AmazonBistro
            });
        ModelMaterial material = result.Mesh!.Materials.Single(
            candidate => candidate.NormalTexture?.Source?.FilePath?.EndsWith(
                "normal.png",
                StringComparison.OrdinalIgnoreCase) == true);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(material.FeatureFlags & (1u << 25), Is.Not.Zero);
            Assert.That(material.Roughness, Is.EqualTo(1f));
            Assert.That(material.Metallic, Is.EqualTo(1f));
            Assert.That(
                material.MetallicRoughnessTexture?.Source?.FilePath,
                Does.EndWith("packed-rm.png").IgnoreCase);
            Assert.That(material.OcclusionTexture, Is.Null);
        });
    }

    [Test]
    public void ImportDetailed_DefaultGltfBackendUsesSharpGltfAndReturnsCapabilityReport()
    {
        string path = CreateMinimalExternalGltf();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(path);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(result.Status, Is.EqualTo(ModelImportStatus.Imported));
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(result.BackendName, Is.EqualTo("SharpGLTF"));
            Assert.That(result.BackendVersion, Is.Not.Empty);
            Assert.That(result.Mesh, Is.Not.Null);
            Assert.That(result.Mesh!.Vertices, Has.Length.EqualTo(3));
            Assert.That(result.Mesh.Indices, Has.Length.EqualTo(3));
            Assert.That(result.Mesh.SubMeshes, Has.Count.EqualTo(1));
            Assert.That(result.Mesh.SubMeshes[0].Vertices, Has.Length.EqualTo(3));
            Assert.That(result.Mesh.SubMeshes[0].Indices, Has.Length.EqualTo(3));
            Assert.That(result.SharpGltfCapability, Is.Not.Null);
            Assert.That(result.SharpGltfCapability!.LoadedSuccessfully, Is.True);
            Assert.That(result.SharpGltfCapability.Document!.MeshCount, Is.EqualTo(1));
            Assert.That(result.SharpGltfCapability.Runtime!.DecodedTriangleCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ImportDetailed_ExplicitAssimpBackendCanStillImportGltfForComparison()
    {
        string path = CreateMinimalExternalGltf();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.Assimp
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(result.Status, Is.EqualTo(ModelImportStatus.Imported));
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.Assimp));
            Assert.That(result.Mesh, Is.Not.Null);
            Assert.That(result.Mesh!.Vertices, Has.Length.EqualTo(3));
            Assert.That(result.Mesh.Indices, Has.Length.EqualTo(3));
        });
    }

    [Test]
    public void Import_ExplicitSharpGltfBackendReturnsModelMesh()
    {
        string path = CreateMinimalExternalGltf();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        Assert.Multiple(() =>
        {
            Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
            Assert.That(mesh.Indices, Has.Length.EqualTo(3));
            Assert.That(mesh.SubMeshes, Has.Count.EqualTo(1));
            Assert.That(mesh.BoundingBox.Min.X, Is.EqualTo(0f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Max.X, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Max.Y, Is.EqualTo(1f).Within(0.00001f));
        });
    }

    [TestCase(ModelImportBackend.SharpGltf)]
    [TestCase(ModelImportBackend.Assimp)]
    public void ImportDetailed_ExtTextureWebPSelectsBoundedWebPSourceOverFallback(
        ModelImportBackend backend)
    {
        string path = CreateMinimalExtTextureWebPGltf(
            $"-{backend.ToString().ToLowerInvariant()}-webp");
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions { Backend = backend });
        ModelTextureSource? source = result.Mesh?.Materials
            .Select(static material => material.BaseColorTexture?.Source)
            .FirstOrDefault(static candidate => candidate is not null);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(source, Is.Not.Null);
            Assert.That(source!.ContainerKind, Is.EqualTo(TextureContainerKind.WebP));
            Assert.That(source.MimeType, Is.EqualTo("image/webp"));
            Assert.That(source.FilePath, Does.EndWith(".webp").IgnoreCase);
            Assert.That(source.EncodedByteLength, Is.EqualTo(WebPTestFixtures.Lossless.Length));
            Assert.That(result.Diagnostics.UnsupportedRequiredExtensionCount, Is.Zero);
        });
    }

    [TestCase(ModelImportBackend.SharpGltf)]
    [TestCase(ModelImportBackend.Assimp)]
    public void ImportDetailed_AlphaCutoffRejectsNegativeAndPreservesValueAboveOne(
        ModelImportBackend backend)
    {
        string backendSuffix = backend.ToString().ToLowerInvariant();
        string negativePath = CreateMinimalExternalGltfWithAlphaCutoff(
            -0.25f,
            $"-{backendSuffix}-negative-alpha");
        string aboveOnePath = CreateMinimalExternalGltfWithAlphaCutoff(
            1.25f,
            $"-{backendSuffix}-above-one-alpha");
        using var importer = new ModelImporter();
        var options = new ImporterOptions { Backend = backend };

        ModelImportResult negative = importer.ImportDetailed(negativePath, options);
        ModelImportResult aboveOne = importer.ImportDetailed(aboveOnePath, options);
        ModelMaterial? authoredMaskedMaterial = aboveOne.Mesh?.Materials
            .SingleOrDefault(static material => material.AlphaMode == ModelAlphaMode.Mask);

        Assert.Multiple(() =>
        {
            Assert.That(negative.ImportedSuccessfully, Is.False);
            Assert.That(negative.Mesh, Is.Null);
            Assert.That(aboveOne.ImportedSuccessfully, Is.True, aboveOne.FailureMessage);
            Assert.That(aboveOne.Mesh, Is.Not.Null);
            Assert.That(
                authoredMaskedMaterial,
                Is.Not.Null,
                "The importer must retain the authored MASK material even when a backend also emits a default material.");
            Assert.That(authoredMaskedMaterial!.AlphaCutoff, Is.EqualTo(1.25f));
        });
    }

    [TestCase(ModelImportBackend.SharpGltf)]
    [TestCase(ModelImportBackend.Assimp)]
    public void ImportDetailed_ExplicitFoliageExtraSetsPersistentMaterialFeature(
        ModelImportBackend backend)
    {
        string path = CreateMinimalExternalGltf($"-{backend.ToString().ToLowerInvariant()}-foliage");
        string json = File.ReadAllText(path)
            .Replace(
                "\"mode\": 4",
                "\"mode\": 4,\n                        \"material\": 0",
                StringComparison.Ordinal)
            .Replace(
                "\"buffers\":",
                "\"materials\": [{ \"name\": \"ExplicitFoliage\", \"extras\": { \"NJULF_foliage\": true } }],\n                \"buffers\":",
                StringComparison.Ordinal);
        File.WriteAllText(path, json);
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions { Backend = backend });
        ModelMaterial? material = result.Mesh?.Materials
            .SingleOrDefault(candidate => candidate.Name == "ExplicitFoliage");

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(material, Is.Not.Null);
            Assert.That(material!.FeatureFlags & (1u << 22), Is.Not.EqualTo(0u));
        });
    }

    [Test]
    public void ImportDetailed_MissingFileReturnsFailureResultWithoutThrowing()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "missing-import-facade.gltf");
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(path);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ModelImportStatus.Failed));
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(result.Mesh, Is.Null);
            Assert.That(result.FailureType, Does.Contain(nameof(FileNotFoundException)));
            Assert.That(result.FailureMessage, Does.Contain(Path.GetFullPath(path)));
            Assert.That(
                result.Diagnostics.Messages,
                Has.Some.Matches<AssetImportMessage>(message =>
                    message.Code == AssetImportMessageCode.MissingModelFile &&
                    message.Severity == AssetImportSeverity.Error));
        });
    }

    [Test]
    public void ImportDetailed_SharpGltfBackendRejectsUnsupportedRequiredExtension()
    {
        string path = CreateUnsupportedRequiredExtensionGltf();
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ModelImportStatus.Unsupported));
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(result.Mesh, Is.Null);
            Assert.That(result.FailureMessage, Does.Contain("VENDOR_required_unknown"));
            Assert.That(result.Diagnostics.UnsupportedRequiredExtensionCount, Is.EqualTo(1));
            Assert.That(
                result.Diagnostics.Messages,
                Has.Some.Matches<AssetImportMessage>(message =>
                    message.Code == AssetImportMessageCode.UnsupportedRequiredExtension &&
                    message.Severity == AssetImportSeverity.Error &&
                    message.Message.Contains("VENDOR_required_unknown", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void ResolveBackend_ExplicitPreferenceOverridesAuto()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ModelImporter.ResolveBackend("asset.gltf"),
                Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(
                ModelImporter.ResolveBackend("asset.glb"),
                Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(
                ModelImporter.ResolveBackend("asset.gltf", new ImporterOptions { Backend = ModelImportBackend.SharpGltf }),
                Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(
                ModelImporter.ResolveBackend("asset.gltf", new ImporterOptions { Backend = ModelImportBackend.Assimp }),
                Is.EqualTo(ModelImportBackend.Assimp));
            Assert.That(
                ModelImporter.ResolveBackend("asset.obj", new ImporterOptions { Backend = ModelImportBackend.Assimp }),
                Is.EqualTo(ModelImportBackend.Assimp));
        });
    }

    private static string WriteTriangleObj()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.obj");
        File.WriteAllText(
            path,
            """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 0 1
            vn 0 0 1
            f 1/1/1 2/2/1 3/3/1
            """);

        return path;
    }

    private static string WriteTexturedTriangleObj()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}-textured.obj");
        string materialPath = Path.Combine(directory, "BistroStyle.mtl");
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nWQAAAAASUVORK5CYII=");
        File.WriteAllBytes(Path.Combine(directory, "base.png"), png);
        File.WriteAllBytes(Path.Combine(directory, "normal.png"), png);
        File.WriteAllBytes(Path.Combine(directory, "packed-rm.png"), png);
        File.WriteAllText(
            materialPath,
            """
            newmtl BistroStyle
            Kd 1.0 1.0 1.0
            map_Kd base.png
            map_Bump normal.png
            map_Ks packed-rm.png
            """);
        File.WriteAllText(
            path,
            """
            mtllib BistroStyle.mtl
            usemtl BistroStyle
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 0 1
            vn 0 0 1
            f 1/1/1 2/2/1 3/3/1
            """);

        return path;
    }

    private static string CreateMinimalExternalGltf(string suffix = "")
    {
        string directory = CreateTestDirectory();
        string binPath = Path.Combine(
            directory,
            $"{TestContext.CurrentContext.Test.ID}{suffix}.bin");
        string gltfPath = Path.Combine(
            directory,
            $"{TestContext.CurrentContext.Test.ID}{suffix}.gltf");

        byte[] positions =
        [
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f)
        ];
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        File.WriteAllBytes(binPath, positions.Concat(indices).ToArray());

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf Phase 1 facade test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0 }],
                "meshes": [
                  {
                    "primitives": [
                      {
                        "attributes": { "POSITION": 0 },
                        "indices": 1,
                        "mode": 4
                      }
                    ]
                  }
                ],
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
                    "max": [1, 1, 0]
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

        return gltfPath;
    }

    private static string CreateMinimalExternalGltfWithAlphaCutoff(
        float alphaCutoff,
        string suffix)
    {
        string path = CreateMinimalExternalGltf(suffix);
        string serializedCutoff = alphaCutoff.ToString(
            "R",
            System.Globalization.CultureInfo.InvariantCulture);
        string json = File.ReadAllText(path)
            .Replace(
                "\"mode\": 4",
                "\"mode\": 4,\n                        \"material\": 0",
                StringComparison.Ordinal)
            .Replace(
                "\"buffers\":",
                $"\"materials\": [{{ \"alphaMode\": \"MASK\", \"alphaCutoff\": {serializedCutoff} }}],\n                \"buffers\":",
                StringComparison.Ordinal);
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateMinimalExtTextureWebPGltf(string suffix)
    {
        string path = CreateMinimalExternalGltf(suffix);
        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string fallbackPath = Path.Combine(directory, $"{stem}-fallback.png");
        string webPPath = Path.Combine(directory, $"{stem}-primary.webp");
        File.WriteAllBytes(
            fallbackPath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="));
        File.WriteAllBytes(webPPath, WebPTestFixtures.Lossless);

        string json = File.ReadAllText(path)
            .Replace(
                "\"scene\": 0",
                "\"extensionsUsed\": [\"EXT_texture_webp\"],\n" +
                "                \"extensionsRequired\": [\"EXT_texture_webp\"],\n" +
                "                \"scene\": 0",
                StringComparison.Ordinal)
            .Replace(
                "\"mode\": 4",
                "\"mode\": 4,\n                        \"material\": 0",
                StringComparison.Ordinal)
            .Replace(
                "\"buffers\":",
                $$"""
                "materials": [
                  { "name": "WebPMaterial", "pbrMetallicRoughness": { "baseColorTexture": { "index": 0 } } }
                ],
                "images": [
                  { "name": "FallbackPng", "uri": "{{Path.GetFileName(fallbackPath)}}", "mimeType": "image/png" },
                  { "name": "PrimaryWebP", "uri": "{{Path.GetFileName(webPPath)}}", "mimeType": "image/webp" }
                ],
                "textures": [
                  {
                    "source": 0,
                    "extensions": { "EXT_texture_webp": { "source": 1 } }
                  }
                ],
                "buffers":
                """,
                StringComparison.Ordinal);
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateUnsupportedRequiredExtensionGltf()
    {
        string directory = CreateTestDirectory();
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        File.WriteAllText(
            gltfPath,
            """
            {
              "asset": { "version": "2.0", "generator": "Njulf SharpGLTF unsupported extension test" },
              "extensionsUsed": [ "VENDOR_required_unknown" ],
              "extensionsRequired": [ "VENDOR_required_unknown" ],
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "name": "EmptyNode" }]
            }
            """);

        return gltfPath;
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "model-importer-facade-tests");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
