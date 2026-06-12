using System;
using System.IO;
using System.Linq;
using System.Text;
using Njulf.Assets;
using Njulf.Core.Animation;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class AnimationImportTests
    {
        [Test]
        public void ImportGltf_PreservesSkinsJointWeightsInverseBindMatricesAndClips()
        {
            string path = WriteSkinnedTriangleGltf();
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path, new ImporterOptions
            {
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });

            AssertSkinnedTriangleAnimationData(model);
        }

        [Test]
        public void ImportGlb_PreservesSkinsJointWeightsInverseBindMatricesAndClips()
        {
            string path = WriteSkinnedTriangleGlb();
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path, new ImporterOptions
            {
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });

            AssertSkinnedTriangleAnimationData(model);
        }

        [Test]
        public void ImportGltf_ConvertsRootAnimationRotationThroughNonJointParent()
        {
            string path = WriteSkinnedTriangleWithParentRotationGltf();
            using var importer = new ModelImporter();

            ModelMesh model = importer.Import(path, new ImporterOptions
            {
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false,
                JoinIdenticalVertices = false,
                SortByPrimitiveType = false
            });

            var animator = new Animator(model.Skeletons[0], model.Skins, model.AnimationClips);
            animator.Play(model.AnimationClips[0], loop: false);
            animator.Seek(0f);

            Matrix4x4 rootSkinMatrix = animator.GetSkinMatrices(0)[0];
            AssertIdentity(rootSkinMatrix);
        }

        private static string WriteSkinnedTriangleGltf()
        {
            string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "animation-import-tests");
            Directory.CreateDirectory(directory);
            string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
            string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

            int positionOffset;
            int jointsOffset;
            int weightsOffset;
            int indicesOffset;
            int inverseBindOffset;
            int animationInputOffset;
            int animationOutputOffset;

            using (var stream = File.Create(binPath))
            using (var writer = new BinaryWriter(stream))
            {
                positionOffset = checked((int)stream.Position);
                WriteVec3(writer, 0f, 0f, 0f);
                WriteVec3(writer, 1f, 0f, 0f);
                WriteVec3(writer, 0f, 1f, 0f);

                jointsOffset = checked((int)stream.Position);
                WriteUShort4(writer, 0, 0, 0, 0);
                WriteUShort4(writer, 1, 0, 0, 0);
                WriteUShort4(writer, 0, 0, 0, 0);

                weightsOffset = checked((int)stream.Position);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);

                indicesOffset = checked((int)stream.Position);
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)2);

                inverseBindOffset = checked((int)stream.Position);
                WriteMatrix(writer, tx: 0f);
                WriteMatrix(writer, tx: -1f);

                animationInputOffset = checked((int)stream.Position);
                writer.Write(0f);
                writer.Write(1f);

                animationOutputOffset = checked((int)stream.Position);
                WriteVec4(writer, 0f, 0f, 0f, 1f);
                WriteVec4(writer, 0.5f, 0.5f, 0.5f, 0.5f);
            }

            int byteLength = checked((int)new FileInfo(binPath).Length);
            File.WriteAllText(
                path,
                $$"""
                {
                  "asset": { "version": "2.0" },
                  "scene": 0,
                  "scenes": [{ "nodes": [0, 2] }],
                  "nodes": [
                    { "name": "Root", "children": [1] },
                    { "name": "Tip", "translation": [1, 0, 0] },
                    { "name": "SkinnedTriangle", "mesh": 0, "skin": 0, "translation": [10, 0, 0] }
                  ],
                  "skins": [
                    { "name": "TriangleSkin", "skeleton": 0, "joints": [0, 1], "inverseBindMatrices": 4 }
                  ],
                  "meshes": [
                    {
                      "name": "TriangleMesh",
                      "primitives": [
                        {
                          "attributes": { "POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2 },
                          "indices": 3,
                          "mode": 4
                        }
                      ]
                    }
                  ],
                  "animations": [
                    {
                      "name": "Turn",
                      "samplers": [{ "input": 5, "output": 6, "interpolation": "LINEAR" }],
                      "channels": [{ "sampler": 0, "target": { "node": 1, "path": "rotation" } }]
                    }
                  ],
                  "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{byteLength}} }],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": {{positionOffset}}, "byteLength": 36, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{jointsOffset}}, "byteLength": 24, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{weightsOffset}}, "byteLength": 48, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{indicesOffset}}, "byteLength": 6, "target": 34963 },
                    { "buffer": 0, "byteOffset": {{inverseBindOffset}}, "byteLength": 128 },
                    { "buffer": 0, "byteOffset": {{animationInputOffset}}, "byteLength": 8 },
                    { "buffer": 0, "byteOffset": {{animationOutputOffset}}, "byteLength": 32 }
                  ],
                  "accessors": [
                    { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                    { "bufferView": 1, "componentType": 5123, "count": 3, "type": "VEC4" },
                    { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC4" },
                    { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" },
                    { "bufferView": 4, "componentType": 5126, "count": 2, "type": "MAT4" },
                    { "bufferView": 5, "componentType": 5126, "count": 2, "type": "SCALAR" },
                    { "bufferView": 6, "componentType": 5126, "count": 2, "type": "VEC4" }
                  ]
                }
                """);

            return path;
        }

        private static string WriteSkinnedTriangleGlb()
        {
            string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "animation-import-tests");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.glb");

            byte[] binary;
            int positionOffset;
            int jointsOffset;
            int weightsOffset;
            int indicesOffset;
            int inverseBindOffset;
            int animationInputOffset;
            int animationOutputOffset;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                positionOffset = checked((int)stream.Position);
                WriteVec3(writer, 0f, 0f, 0f);
                WriteVec3(writer, 1f, 0f, 0f);
                WriteVec3(writer, 0f, 1f, 0f);

                jointsOffset = checked((int)stream.Position);
                WriteUShort4(writer, 0, 0, 0, 0);
                WriteUShort4(writer, 1, 0, 0, 0);
                WriteUShort4(writer, 0, 0, 0, 0);

                weightsOffset = checked((int)stream.Position);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);

                indicesOffset = checked((int)stream.Position);
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)2);

                inverseBindOffset = checked((int)stream.Position);
                WriteMatrix(writer, tx: 0f);
                WriteMatrix(writer, tx: -1f);

                animationInputOffset = checked((int)stream.Position);
                writer.Write(0f);
                writer.Write(1f);

                animationOutputOffset = checked((int)stream.Position);
                WriteVec4(writer, 0f, 0f, 0f, 1f);
                WriteVec4(writer, 0.5f, 0.5f, 0.5f, 0.5f);
                binary = stream.ToArray();
            }

            string json =
                $$"""
                {
                  "asset": { "version": "2.0" },
                  "scene": 0,
                  "scenes": [{ "nodes": [0, 2] }],
                  "nodes": [
                    { "name": "Root", "children": [1] },
                    { "name": "Tip", "translation": [1, 0, 0] },
                    { "name": "SkinnedTriangle", "mesh": 0, "skin": 0, "translation": [10, 0, 0] }
                  ],
                  "skins": [{ "name": "TriangleSkin", "skeleton": 0, "joints": [0, 1], "inverseBindMatrices": 4 }],
                  "meshes": [
                    {
                      "name": "TriangleMesh",
                      "primitives": [
                        {
                          "attributes": { "POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2 },
                          "indices": 3,
                          "mode": 4
                        }
                      ]
                    }
                  ],
                  "animations": [
                    {
                      "name": "Turn",
                      "samplers": [{ "input": 5, "output": 6, "interpolation": "LINEAR" }],
                      "channels": [{ "sampler": 0, "target": { "node": 1, "path": "rotation" } }]
                    }
                  ],
                  "buffers": [{ "byteLength": {{binary.Length}} }],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": {{positionOffset}}, "byteLength": 36, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{jointsOffset}}, "byteLength": 24, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{weightsOffset}}, "byteLength": 48, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{indicesOffset}}, "byteLength": 6, "target": 34963 },
                    { "buffer": 0, "byteOffset": {{inverseBindOffset}}, "byteLength": 128 },
                    { "buffer": 0, "byteOffset": {{animationInputOffset}}, "byteLength": 8 },
                    { "buffer": 0, "byteOffset": {{animationOutputOffset}}, "byteLength": 32 }
                  ],
                  "accessors": [
                    { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                    { "bufferView": 1, "componentType": 5123, "count": 3, "type": "VEC4" },
                    { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC4" },
                    { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" },
                    { "bufferView": 4, "componentType": 5126, "count": 2, "type": "MAT4" },
                    { "bufferView": 5, "componentType": 5126, "count": 2, "type": "SCALAR" },
                    { "bufferView": 6, "componentType": 5126, "count": 2, "type": "VEC4" }
                  ]
                }
                """;

            WriteGlb(path, json, binary);
            return path;
        }

        private static string WriteSkinnedTriangleWithParentRotationGltf()
        {
            string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "animation-import-tests");
            Directory.CreateDirectory(directory);
            string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
            string path = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

            int positionOffset;
            int jointsOffset;
            int weightsOffset;
            int indicesOffset;
            int inverseBindOffset;
            int animationInputOffset;
            int animationOutputOffset;

            using (var stream = File.Create(binPath))
            using (var writer = new BinaryWriter(stream))
            {
                positionOffset = checked((int)stream.Position);
                WriteVec3(writer, 0f, 0f, 0f);
                WriteVec3(writer, 1f, 0f, 0f);
                WriteVec3(writer, 0f, 1f, 0f);

                jointsOffset = checked((int)stream.Position);
                WriteUShort4(writer, 0, 0, 0, 0);
                WriteUShort4(writer, 0, 0, 0, 0);
                WriteUShort4(writer, 0, 0, 0, 0);

                weightsOffset = checked((int)stream.Position);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);
                WriteVec4(writer, 1f, 0f, 0f, 0f);

                indicesOffset = checked((int)stream.Position);
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)2);

                inverseBindOffset = checked((int)stream.Position);
                WriteMatrix(writer, tx: 0f);

                animationInputOffset = checked((int)stream.Position);
                writer.Write(0f);
                writer.Write(1f);

                animationOutputOffset = checked((int)stream.Position);
                WriteVec4(writer, -0.70710677f, 0f, 0f, 0.70710677f);
                WriteVec4(writer, -0.70710677f, 0f, 0f, 0.70710677f);
            }

            int byteLength = checked((int)new FileInfo(binPath).Length);
            File.WriteAllText(
                path,
                $$"""
                {
                  "asset": { "version": "2.0" },
                  "scene": 0,
                  "scenes": [{ "nodes": [0] }],
                  "nodes": [
                    { "name": "Armature", "children": [1, 2], "rotation": [0.70710677, 0, 0, 0.70710677] },
                    { "name": "Root", "rotation": [-0.70710677, 0, 0, 0.70710677] },
                    { "name": "SkinnedTriangle", "mesh": 0, "skin": 0 }
                  ],
                  "skins": [
                    { "name": "TriangleSkin", "skeleton": 1, "joints": [1], "inverseBindMatrices": 4 }
                  ],
                  "meshes": [
                    {
                      "name": "TriangleMesh",
                      "primitives": [
                        {
                          "attributes": { "POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2 },
                          "indices": 3,
                          "mode": 4
                        }
                      ]
                    }
                  ],
                  "animations": [
                    {
                      "name": "RootStill",
                      "samplers": [{ "input": 5, "output": 6, "interpolation": "LINEAR" }],
                      "channels": [{ "sampler": 0, "target": { "node": 1, "path": "rotation" } }]
                    }
                  ],
                  "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{byteLength}} }],
                  "bufferViews": [
                    { "buffer": 0, "byteOffset": {{positionOffset}}, "byteLength": 36, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{jointsOffset}}, "byteLength": 24, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{weightsOffset}}, "byteLength": 48, "target": 34962 },
                    { "buffer": 0, "byteOffset": {{indicesOffset}}, "byteLength": 6, "target": 34963 },
                    { "buffer": 0, "byteOffset": {{inverseBindOffset}}, "byteLength": 64 },
                    { "buffer": 0, "byteOffset": {{animationInputOffset}}, "byteLength": 8 },
                    { "buffer": 0, "byteOffset": {{animationOutputOffset}}, "byteLength": 32 }
                  ],
                  "accessors": [
                    { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                    { "bufferView": 1, "componentType": 5123, "count": 3, "type": "VEC4" },
                    { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC4" },
                    { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" },
                    { "bufferView": 4, "componentType": 5126, "count": 1, "type": "MAT4" },
                    { "bufferView": 5, "componentType": 5126, "count": 2, "type": "SCALAR" },
                    { "bufferView": 6, "componentType": 5126, "count": 2, "type": "VEC4" }
                  ]
                }
                """);

            return path;
        }

        private static void AssertSkinnedTriangleAnimationData(ModelMesh model)
        {
            Assert.Multiple(() =>
            {
                Assert.That(model.Skeletons, Has.Count.EqualTo(1));
                Assert.That(model.Skins, Has.Count.EqualTo(1));
                Assert.That(model.AnimationClips, Has.Count.EqualTo(1));
                Assert.That(model.AnimationDiagnostics.SkeletonCount, Is.EqualTo(1));
                Assert.That(model.AnimationDiagnostics.JointCount, Is.EqualTo(2));
                Assert.That(model.AnimationDiagnostics.SkinnedSubMeshCount, Is.EqualTo(1));
                Assert.That(model.AnimationDiagnostics.AnimationChannelCount, Is.EqualTo(3));
                Assert.That(model.SubMeshes, Has.Count.EqualTo(1));
                Assert.That(model.SubMeshes[0].SkinIndex, Is.EqualTo(0));
                Assert.That(model.SubMeshes[0].JointIndices0, Has.Length.EqualTo(3));
                Assert.That(model.SubMeshes[0].JointWeights0, Has.Length.EqualTo(3));
                Assert.That(model.SubMeshes[0].JointIndices0[1].X, Is.EqualTo(1));
                Assert.That(model.SubMeshes[0].JointWeights0[1].X, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(model.SubMeshes[0].Vertices[0].X, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(model.SubMeshes[0].Vertices[1].X, Is.EqualTo(1f).Within(0.0001f));

                AnimationChannel channel = model.AnimationClips[0].Channels.Single(c => c.Path == AnimationChannelPath.Rotation);
                Assert.That(channel.Path, Is.EqualTo(AnimationChannelPath.Rotation));
                Assert.That(channel.Sampler.OutputValues[1].X, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(channel.Sampler.OutputValues[1].Y, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(channel.Sampler.OutputValues[1].Z, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(channel.Sampler.OutputValues[1].W, Is.EqualTo(-0.5f).Within(0.0001f));
            });
        }

        private static void AssertIdentity(Matrix4x4 matrix)
        {
            Assert.Multiple(() =>
            {
                Assert.That(matrix.M11, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(matrix.M22, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(matrix.M33, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(matrix.M44, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(matrix.M12, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M13, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M14, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M21, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M23, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M24, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M31, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M32, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M34, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M41, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M42, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(matrix.M43, Is.EqualTo(0f).Within(0.0001f));
            });
        }

        private static void WriteGlb(string path, string json, byte[] binary)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int paddedJsonLength = Align4(jsonBytes.Length);
            int paddedBinaryLength = Align4(binary.Length);
            int totalLength = 12 + 8 + paddedJsonLength + 8 + paddedBinaryLength;

            using FileStream stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(0x46546C67u);
            writer.Write(2u);
            writer.Write((uint)totalLength);
            writer.Write((uint)paddedJsonLength);
            writer.Write(0x4E4F534Au);
            writer.Write(jsonBytes);
            for (int i = jsonBytes.Length; i < paddedJsonLength; i++)
                writer.Write((byte)0x20);
            writer.Write((uint)paddedBinaryLength);
            writer.Write(0x004E4942u);
            writer.Write(binary);
            for (int i = binary.Length; i < paddedBinaryLength; i++)
                writer.Write((byte)0x00);
        }

        private static int Align4(int value)
        {
            return (value + 3) & ~3;
        }

        private static void WriteVec3(BinaryWriter writer, float x, float y, float z)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
        }

        private static void WriteVec4(BinaryWriter writer, float x, float y, float z, float w)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(w);
        }

        private static void WriteUShort4(BinaryWriter writer, ushort x, ushort y, ushort z, ushort w)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(w);
        }

        private static void WriteMatrix(BinaryWriter writer, float tx)
        {
            writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(0f);
            writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(0f);
            writer.Write(tx); writer.Write(0f); writer.Write(0f); writer.Write(1f);
        }
    }
}
