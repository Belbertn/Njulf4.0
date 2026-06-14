using System;
using System.IO;
using System.Linq;
using Njulf.Assets;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class ContentRendererIntegrationTests
    {
        [Test]
        public void LoadModel_RequiresRendererUploadService()
        {
            string path = WriteTriangleObj();
            using var content = new ContentManager(Path.GetDirectoryName(path));

            Assert.That(
                () => content.Load<Model>(Path.GetFileName(path)),
                Throws.InvalidOperationException.With.Message.Contains(nameof(IModelRenderUploadService)));
        }

        [Test]
        public void LoadModel_UsesRendererUploadServiceAndCachesRenderableModel()
        {
            string path = WriteTriangleObj();
            var uploader = new FakeModelRenderUploadService();
            using var content = new ContentManager(Path.GetDirectoryName(path), uploader);

            Model first = content.Load<Model>(Path.GetFileName(path));
            Model second = content.Load<Model>(Path.GetFileName(path));

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.SameAs(second));
                Assert.That(uploader.UploadCount, Is.EqualTo(1));
                Assert.That(first.RenderObjects, Has.Count.EqualTo(1));
                Assert.That(first.RenderObjects[0].Mesh, Is.EqualTo(new MeshHandle(1, 1)));
                Assert.That(first.RenderObjects[0].Material, Is.EqualTo(new MaterialHandle(1, 1)));
            });
        }

        [Test]
        public void ModelCreateInstance_CopiesRenderableHandlesWithoutSharingRenderObjects()
        {
            string path = WriteTriangleObj();
            var uploader = new FakeModelRenderUploadService();
            using var content = new ContentManager(Path.GetDirectoryName(path), uploader);

            Model asset = content.Load<Model>(Path.GetFileName(path));
            Model firstInstance = asset.CreateInstance();
            Model secondInstance = asset.CreateInstance();

            firstInstance.RenderObjects[0].Visible = false;

            Assert.Multiple(() =>
            {
                Assert.That(firstInstance, Is.Not.SameAs(asset));
                Assert.That(secondInstance, Is.Not.SameAs(asset));
                Assert.That(firstInstance.RenderObjects[0], Is.Not.SameAs(asset.RenderObjects[0]));
                Assert.That(firstInstance.RenderObjects[0], Is.Not.SameAs(secondInstance.RenderObjects[0]));
                Assert.That(firstInstance.RenderObjects[0].Mesh, Is.EqualTo(asset.RenderObjects[0].Mesh));
                Assert.That(firstInstance.RenderObjects[0].Material, Is.EqualTo(asset.RenderObjects[0].Material));
                Assert.That(secondInstance.RenderObjects[0].Visible, Is.True);
                Assert.That(asset.RenderObjects[0].Visible, Is.True);
            });
        }

        [Test]
        public void LoadModelMeshAndLoadModel_DoNotShareCacheEntry()
        {
            string path = WriteTriangleObj();
            var uploader = new FakeModelRenderUploadService();
            using var content = new ContentManager(Path.GetDirectoryName(path), uploader);

            ModelMesh importedMesh = content.Load<ModelMesh>(Path.GetFileName(path));
            Model uploadedModel = content.Load<Model>(Path.GetFileName(path));

            Assert.Multiple(() =>
            {
                Assert.That(importedMesh.Vertices, Has.Length.EqualTo(3));
                Assert.That(uploadedModel.RenderObjects, Has.Count.EqualTo(1));
                Assert.That(uploader.UploadCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void ImportGltf_PreservesMaterialsTexturesAndSubmeshAssignments()
        {
            string path = FindRepoFile("NjulfHelloGame", "NewSponza_Main_glTF_003.gltf");
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path);

            Assert.Multiple(() =>
            {
                Assert.That(model.SubMeshes, Has.Count.GreaterThan(0));
                Assert.That(model.Materials, Has.Count.GreaterThan(0));
                Assert.That(
                    model.SubMeshes.Select(m => m.MaterialIndex),
                    Is.All.InRange(0, model.Materials.Count - 1));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.AlbedoTexturePath)));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.NormalTexturePath)));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.MetallicRoughnessTexturePath)));
            });
        }

        [Test]
        public void ImportGltf_PreservesSponzaDirtDecalBlendMaterial()
        {
            string path = FindRepoFile("NjulfHelloGame", "NewSponza_Main_glTF_003.gltf");
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path);

            Assert.That(
                model.Materials,
                Has.Some.Matches<ModelMaterial>(m =>
                    string.Equals(m.Name, "dirt_decal", StringComparison.OrdinalIgnoreCase) &&
                    m.AlphaMode == ModelAlphaMode.Blend));
        }

        [Test]
        public void ImportGltf_BakesNodeTransformsIntoVerticesAndBounds()
        {
            string path = WriteTranslatedTriangleGltf();
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path, new ImporterOptions
            {
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });

            Assert.Multiple(() =>
            {
                Assert.That(model.SubMeshes, Has.Count.EqualTo(1));
                Assert.That(model.Vertices, Has.Length.EqualTo(3));
                Assert.That(model.Vertices[0].X, Is.EqualTo(10f).Within(0.00001f));
                Assert.That(model.Vertices[0].Y, Is.EqualTo(20f).Within(0.00001f));
                Assert.That(model.Vertices[0].Z, Is.EqualTo(30f).Within(0.00001f));
                Assert.That(model.Vertices[1].X, Is.EqualTo(11f).Within(0.00001f));
                Assert.That(model.Vertices[2].Y, Is.EqualTo(21f).Within(0.00001f));
                Assert.That(model.BoundingBox.Min.X, Is.EqualTo(10f).Within(0.00001f));
                Assert.That(model.BoundingBox.Min.Y, Is.EqualTo(20f).Within(0.00001f));
                Assert.That(model.BoundingBox.Min.Z, Is.EqualTo(30f).Within(0.00001f));
                Assert.That(model.BoundingBox.Max.X, Is.EqualTo(11f).Within(0.00001f));
                Assert.That(model.BoundingBox.Max.Y, Is.EqualTo(21f).Within(0.00001f));
                Assert.That(model.BoundingBox.Max.Z, Is.EqualTo(30f).Within(0.00001f));
            });
        }

        [Test]
        public void ImportObj_FlipUvs_AppliesExactlyOnce()
        {
            string path = WriteTriangleObj();
            using var importer = new ModelImporter();

            ModelMesh unflipped = importer.Import(path, new ImporterOptions
            {
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });
            ModelMesh flipped = importer.Import(path, new ImporterOptions
            {
                FlipUVs = true,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });

            Assert.Multiple(() =>
            {
                Assert.That(unflipped.TexCoords, Has.Length.EqualTo(3));
                Assert.That(flipped.TexCoords, Has.Length.EqualTo(3));
                Assert.That(flipped.TexCoords[0].Y, Is.EqualTo(1f - unflipped.TexCoords[0].Y).Within(0.00001f));
                Assert.That(flipped.TexCoords[1].Y, Is.EqualTo(1f - unflipped.TexCoords[1].Y).Within(0.00001f));
                Assert.That(flipped.TexCoords[2].Y, Is.EqualTo(1f - unflipped.TexCoords[2].Y).Within(0.00001f));
            });
        }

        [Test]
        public void ImportGltf_MissingExternalBuffer_ThrowsWithAbsolutePath()
        {
            string directory = CreateTestDirectory();
            string path = Path.Combine(directory, "missing-buffer.gltf");
            string missingBuffer = Path.Combine(directory, "missing.bin");
            File.WriteAllText(
                path,
                """
                {
                  "asset": { "version": "2.0" },
                  "buffers": [
                    { "byteLength": 12, "uri": "missing.bin" }
                  ]
                }
                """);

            using var importer = new ModelImporter();

            Assert.That(
                () => importer.Import(path),
                Throws.TypeOf<FileNotFoundException>()
                    .With.Property(nameof(FileNotFoundException.FileName)).EqualTo(missingBuffer)
                    .And.Message.Contains(missingBuffer));
        }

        [Test]
        public void ImportGltf_RequiredBasisTexture_ThrowsClearUnsupportedMessage()
        {
            string directory = CreateTestDirectory();
            string path = Path.Combine(directory, "basis-required.gltf");
            File.WriteAllText(
                path,
                """
                {
                  "asset": { "version": "2.0" },
                  "extensionsRequired": ["KHR_texture_basisu"],
                  "textures": [
                    {
                      "extensions": {
                        "KHR_texture_basisu": { "source": 0 }
                      }
                    }
                  ],
                  "images": [
                    { "uri": "albedo.ktx2" }
                  ]
                }
                """);

            using var importer = new ModelImporter();

            Assert.That(
                () => importer.Import(path),
                Throws.TypeOf<NotSupportedException>()
                    .With.Message.Contains("KTX2/Basis decode is not implemented"));
        }

        [Test]
        public void ImportGltf_BufferViewTexture_IsAccepted()
        {
            string directory = CreateTestDirectory();
            string bufferPath = Path.Combine(directory, "mesh.bin");
            string path = Path.Combine(directory, "embedded-texture.gltf");
            using (var stream = File.Create(bufferPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0f); writer.Write(0f); writer.Write(0f);
                writer.Write(1f); writer.Write(0f); writer.Write(0f);
                writer.Write(0f); writer.Write(1f); writer.Write(0f);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
                writer.Write((ushort)0);
                writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            }
            File.WriteAllText(
                path,
                $$"""
                {
                  "asset": { "version": "2.0" },
                  "scene": 0,
                  "scenes": [
                    { "nodes": [0] }
                  ],
                  "nodes": [
                    { "mesh": 0 }
                  ],
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
                  "buffers": [
                    { "byteLength": 48, "uri": "{{Path.GetFileName(bufferPath)}}" }
                  ],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
                    { "buffer": 0, "byteOffset": 36, "byteLength": 6, "target": 34963 },
                    { "buffer": 0, "byteOffset": 44, "byteLength": 4 }
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
                  ],
                  "images": [
                    { "bufferView": 2, "mimeType": "image/png" }
                  ]
                }
                """);

            using var importer = new ModelImporter();

            Assert.DoesNotThrow(() => importer.Import(path));
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
                f 1/1 2/2 3/3
                """);

            return path;
        }

        private static string WriteTranslatedTriangleGltf()
        {
            string directory = CreateTestDirectory();

            string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
            string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

            using (var stream = File.Create(binPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0f); writer.Write(0f); writer.Write(0f);
                writer.Write(1f); writer.Write(0f); writer.Write(0f);
                writer.Write(0f); writer.Write(1f); writer.Write(0f);
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
                writer.Write((ushort)0);
            }

            File.WriteAllText(
                path,
                $$"""
                {
                  "asset": { "version": "2.0" },
                  "scene": 0,
                  "scenes": [
                    { "nodes": [0] }
                  ],
                  "nodes": [
                    {
                      "name": "TranslatedTriangle",
                      "mesh": 0,
                      "translation": [10, 20, 30]
                    }
                  ],
                  "meshes": [
                    {
                      "name": "TriangleMesh",
                      "primitives": [
                        {
                          "attributes": { "POSITION": 0 },
                          "indices": 1,
                          "mode": 4
                        }
                      ]
                    }
                  ],
                  "buffers": [
                    {
                      "uri": "{{Path.GetFileName(binPath)}}",
                      "byteLength": 44
                    }
                  ],
                  "bufferViews": [
                    {
                      "buffer": 0,
                      "byteOffset": 0,
                      "byteLength": 36,
                      "target": 34962
                    },
                    {
                      "buffer": 0,
                      "byteOffset": 36,
                      "byteLength": 6,
                      "target": 34963
                    }
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

            return path;
        }

        private static string CreateTestDirectory()
        {
            string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "content-tests");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string FindRepoFile(params string[] relativeParts)
        {
            string? directory = TestContext.CurrentContext.WorkDirectory;
            while (!string.IsNullOrEmpty(directory))
            {
                string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate))
                    return candidate;

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException("Could not locate repository test asset.", Path.Combine(relativeParts));
        }

        private sealed class FakeModelRenderUploadService : IModelRenderUploadService
        {
            public int UploadCount { get; private set; }
            public ModelRenderUploadDiagnostics LastUploadDiagnostics { get; private set; } =
                new ModelRenderUploadDiagnostics(string.Empty, 0, 0, 0, 0, 0, 0, 0, 0);

            public Model UploadModel(ModelMesh modelMesh)
            {
                UploadCount++;

                var model = new Model
                {
                    Name = modelMesh.Name,
                    BoundingBox = modelMesh.BoundingBox,
                    BoundingSphere = modelMesh.BoundingSphere
                };

                model.Add(new RenderObject(new MeshHandle(1, 1), new MaterialHandle(1, 1)));
                LastUploadDiagnostics = new ModelRenderUploadDiagnostics(model.Name, 1, 1, 1, 0, 0, 0, 0, 0);
                return model;
            }
        }
    }
}
