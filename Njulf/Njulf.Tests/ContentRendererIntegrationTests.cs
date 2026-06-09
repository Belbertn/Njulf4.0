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
            string path = FindRepoFile("NjulfHelloGame", "vintage_video_camera_2k.gltf");
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path);

            Assert.Multiple(() =>
            {
                Assert.That(model.SubMeshes, Has.Count.EqualTo(2));
                Assert.That(model.Materials, Has.Count.GreaterThanOrEqualTo(2));
                Assert.That(model.Materials.Select(m => m.Name), Does.Contain("vintage_video_camera"));
                Assert.That(model.Materials.Select(m => m.Name), Does.Contain("vintage_video_camera_glass"));
                Assert.That(model.SubMeshes.Select(m => m.MaterialIndex), Is.EquivalentTo(new[] { 0, 1 }));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.AlbedoTexturePath)));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.NormalTexturePath)));
                Assert.That(model.Materials, Has.Some.Matches<ModelMaterial>(m => File.Exists(m.MetallicRoughnessTexturePath)));
                Assert.That(model.Materials[0].DoubleSided, Is.True);
                Assert.That(model.Materials[0].AlphaMode, Is.EqualTo(ModelAlphaMode.Opaque));
                Assert.That(model.Materials[1].AlphaMode, Is.EqualTo(ModelAlphaMode.Blend));
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
        public void ImportGltf_BufferViewTexture_ThrowsUnsupportedFeature()
        {
            string directory = CreateTestDirectory();
            string bufferPath = Path.Combine(directory, "mesh.bin");
            string path = Path.Combine(directory, "embedded-texture.gltf");
            File.WriteAllBytes(bufferPath, new byte[64]);
            File.WriteAllText(
                path,
                """
                {
                  "asset": { "version": "2.0" },
                  "buffers": [
                    { "byteLength": 64, "uri": "mesh.bin" }
                  ],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": 0, "byteLength": 64 }
                  ],
                  "images": [
                    { "bufferView": 0, "mimeType": "image/png" }
                  ]
                }
                """);

            using var importer = new ModelImporter();

            Assert.That(
                () => importer.Import(path),
                Throws.TypeOf<NotSupportedException>()
                    .With.Message.Contains("bufferView")
                    .And.Message.Contains("textures"));
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
                new ModelRenderUploadDiagnostics(string.Empty, 0, 0, 0, 0, 0, 0, 0);

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
                LastUploadDiagnostics = new ModelRenderUploadDiagnostics(model.Name, 1, 1, 1, 0, 0, 0, 0);
                return model;
            }
        }
    }
}
