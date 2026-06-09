using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Njulf.Rendering;
using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests
{
    /// <summary>
    /// Unit tests for bindless index contracts.
    /// Verifies that C# bindless indices match shader-side constants from common.glsl.
    /// </summary>
    [TestFixture]
    public class BindlessIndexTests
    {
        private static readonly Lazy<string> CommonGlslSource = new(ReadCommonGlsl);

        [Test]
        public void StaticBufferIndices_AreUniqueAndContiguous()
        {
            var indices = GetStaticBufferIndices().ToArray();

            Assert.That(indices, Has.Length.EqualTo(BindlessIndex.StaticBufferCount));
            Assert.That(indices.Distinct().Count(), Is.EqualTo(indices.Length));
            Assert.That(indices.OrderBy(index => index).ToArray(), Is.EqualTo(Enumerable.Range(0, BindlessIndex.StaticBufferCount).ToArray()));
        }

        [Test]
        public void ShaderConstants_MatchHostBindlessIndices()
        {
            var expected = new Dictionary<string, int>
            {
                ["OBJECT_DATA_BUFFER_INDEX"] = BindlessIndex.ObjectDataBuffer,
                ["MATERIAL_DATA_BUFFER_INDEX"] = BindlessIndex.MaterialDataBuffer,
                ["SCENE_MESH_METADATA_BUFFER_INDEX"] = BindlessIndex.SceneMeshMetadataBuffer,
                ["VERTEX_BUFFER_INDEX"] = BindlessIndex.VertexBuffer,
                ["INDEX_BUFFER_INDEX"] = BindlessIndex.IndexBuffer,
                ["MESHLET_BUFFER_INDEX"] = BindlessIndex.MeshletBuffer,
                ["MESHLET_VERTEX_INDEX_BUFFER_INDEX"] = BindlessIndex.MeshletVertexIndexBuffer,
                ["MESHLET_TRIANGLE_INDEX_BUFFER_INDEX"] = BindlessIndex.MeshletTriangleIndexBuffer,
                ["INSTANCE_BUFFER_BASE_INDEX"] = BindlessIndex.InstanceBufferBase,
                ["INSTANCE_BUFFER_FRAME1_INDEX"] = BindlessIndex.InstanceBufferFrame1,
                ["MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletDrawBufferBase,
                ["MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletDrawBufferFrame1,
                ["TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.TransparentMeshletDrawBufferBase,
                ["TRANSPARENT_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.TransparentMeshletDrawBufferFrame1,
                ["LIGHT_BUFFER_INDEX"] = BindlessIndex.LightBuffer,
                ["TILED_LIGHT_HEADER_BUFFER_INDEX"] = BindlessIndex.TiledLightHeaderBuffer,
                ["TILED_LIGHT_INDICES_BUFFER_INDEX"] = BindlessIndex.TiledLightIndicesBuffer,
                ["STATIC_BUFFER_COUNT"] = BindlessIndex.StaticBufferCount,
                ["FIRST_TEXTURE_INDEX"] = BindlessIndex.FirstTextureIndex,
                ["DEPTH_TEXTURE_INDEX"] = BindlessIndex.DepthTexture,
                ["HIZ_DEPTH_TEXTURE_INDEX"] = BindlessIndex.HiZDepthTexture,
                ["FIRST_DYNAMIC_TEXTURE_INDEX"] = BindlessIndex.FirstDynamicTextureIndex,
                ["MAX_TEXTURES"] = BindlessIndex.MaxTextures,
                ["FRAMES_IN_FLIGHT"] = RenderingConstants.FramesInFlight
            };

            Assert.Multiple(() =>
            {
                foreach (var (shaderName, hostValue) in expected)
                {
                    Assert.That(ReadShaderIntConstant(shaderName), Is.EqualTo(hostValue), shaderName);
                }
            });
        }

        [Test]
        public void ShaderDescriptorLayout_MatchesBindlessHeapContract()
        {
            var source = CommonGlslSource.Value;

            Assert.Multiple(() =>
            {
                Assert.That(ReadShaderIntConstant("BINDLESS_STORAGE_SET"), Is.EqualTo(0));
                Assert.That(ReadShaderIntConstant("BINDLESS_STORAGE_BINDING"), Is.EqualTo(0));
                Assert.That(ReadShaderIntConstant("BINDLESS_TEXTURE_SET"), Is.EqualTo(1));
                Assert.That(ReadShaderIntConstant("BINDLESS_TEXTURE_BINDING"), Is.EqualTo(0));
                Assert.That(source, Does.Match(@"layout\s*\(\s*set\s*=\s*0\s*,\s*binding\s*=\s*0\s*\)\s*buffer\s+BindlessStorageBuffer"));
                Assert.That(source, Does.Match(@"layout\s*\(\s*set\s*=\s*1\s*,\s*binding\s*=\s*0\s*\)\s*uniform\s+sampler2D\s+BindlessTextures\[\]"));
                Assert.That(source, Does.Contain("BindlessStorageBuffers[]"));
                Assert.That(source, Does.Contain("BindlessTextures[]"));
                Assert.That(source, Does.Not.Match(@"layout\s*\(\s*set\s*=\s*0\s*,\s*binding\s*=\s*(?:[1-9]|1[0-4])\s*\)\s*(?:readonly\s+)?buffer"));
            });
        }

        [Test]
        public void PerFrameBufferIndices_AreDerivedFromFramesInFlight()
        {
            Assert.That(RenderingConstants.FramesInFlight, Is.EqualTo(2), "The current fixed table has two per-frame slots.");
            Assert.That(BindlessIndex.InstanceBufferFrame1, Is.EqualTo(BindlessIndex.InstanceBufferBase + 1));
            Assert.That(BindlessIndex.MeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.MeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.TransparentMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.TransparentMeshletDrawBufferBase + 1));
        }

        [Test]
        public void BindlessIndices_AreInValidVulkanRange()
        {
            const int maxVulkanBindings = 1024;

            foreach (int index in GetStaticBufferIndices())
            {
                Assert.That(index, Is.LessThan(maxVulkanBindings),
                    $"Binding index {index} ({BindlessIndex.GetIndexName(index)}) may exceed Vulkan limits");
            }
        }

        [Test]
        public void TextureIndices_AreSeparateDescriptorSetIndices()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BindlessIndex.FirstTextureIndex, Is.EqualTo(0));
                Assert.That(BindlessIndex.IsTextureIndex(0), Is.True);
                Assert.That(BindlessIndex.GetTextureIndexName(0), Is.EqualTo("Texture 0"));
                Assert.That(BindlessIndex.GetTextureIndexName(BindlessIndex.MaxTextures - 1), Is.EqualTo($"Texture {BindlessIndex.MaxTextures - 1}"));
                Assert.That(BindlessIndex.GetTextureIndexName(BindlessIndex.MaxTextures), Is.EqualTo("Unknown"));
            });
        }

        [Test]
        public void IsStaticBufferIndex_WorksCorrectly()
        {
            for (int i = 0; i < BindlessIndex.StaticBufferCount; i++)
            {
                Assert.That(BindlessIndex.IsStaticBufferIndex(i), Is.True,
                    $"Index {i} should be a static buffer index");
            }

            Assert.That(BindlessIndex.IsStaticBufferIndex(BindlessIndex.StaticBufferCount), Is.False);
            Assert.That(BindlessIndex.IsStaticBufferIndex(BindlessIndex.StaticBufferCount + 1), Is.False);
            Assert.That(BindlessIndex.IsStaticBufferIndex(-1), Is.False);
        }

        [Test]
        public void IsTextureIndex_WorksCorrectly()
        {
            Assert.That(BindlessIndex.IsTextureIndex(0), Is.True);
            Assert.That(BindlessIndex.IsTextureIndex(BindlessIndex.MaxTextures - 1), Is.True);
            Assert.That(BindlessIndex.IsTextureIndex(BindlessIndex.MaxTextures), Is.False);
            Assert.That(BindlessIndex.IsTextureIndex(-1), Is.False);
        }

        [Test]
        public void GetIndexName_ReturnsCorrectStorageNames()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.ObjectDataBuffer), Is.EqualTo(nameof(BindlessIndex.ObjectDataBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.MaterialDataBuffer), Is.EqualTo(nameof(BindlessIndex.MaterialDataBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.VertexBuffer), Is.EqualTo(nameof(BindlessIndex.VertexBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.IndexBuffer), Is.EqualTo(nameof(BindlessIndex.IndexBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.LightBuffer), Is.EqualTo(nameof(BindlessIndex.LightBuffer)));
            });
        }

        [Test]
        public void GetIndexName_HandlesUnknownIndices()
        {
            Assert.That(BindlessIndex.GetIndexName(-1), Is.EqualTo("Unknown"));

            var veryLargeIndex = BindlessIndex.FirstTextureIndex + BindlessIndex.MaxTextures + 1000;
            Assert.That(BindlessIndex.GetIndexName(veryLargeIndex), Is.EqualTo("Unknown"));
        }

        private static IEnumerable<int> GetStaticBufferIndices()
        {
            yield return BindlessIndex.ObjectDataBuffer;
            yield return BindlessIndex.MaterialDataBuffer;
            yield return BindlessIndex.SceneMeshMetadataBuffer;
            yield return BindlessIndex.VertexBuffer;
            yield return BindlessIndex.IndexBuffer;
            yield return BindlessIndex.MeshletBuffer;
            yield return BindlessIndex.MeshletVertexIndexBuffer;
            yield return BindlessIndex.MeshletTriangleIndexBuffer;
            yield return BindlessIndex.InstanceBufferBase;
            yield return BindlessIndex.InstanceBufferFrame1;
            yield return BindlessIndex.MeshletDrawBufferBase;
            yield return BindlessIndex.MeshletDrawBufferFrame1;
            yield return BindlessIndex.TransparentMeshletDrawBufferBase;
            yield return BindlessIndex.TransparentMeshletDrawBufferFrame1;
            yield return BindlessIndex.LightBuffer;
            yield return BindlessIndex.TiledLightHeaderBuffer;
            yield return BindlessIndex.TiledLightIndicesBuffer;
        }

        private static int ReadShaderIntConstant(string name)
        {
            var match = Regex.Match(
                CommonGlslSource.Value,
                $@"\bconst\s+int\s+{Regex.Escape(name)}\s*=\s*(\d+)\s*;");

            if (!match.Success)
                throw new AssertionException($"Shader constant '{name}' was not found in common.glsl.");

            return int.Parse(match.Groups[1].Value);
        }

        private static string ReadCommonGlsl()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Njulf.Shaders", "common.glsl");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate Njulf.Shaders/common.glsl from the test output directory.");
        }
    }
}
