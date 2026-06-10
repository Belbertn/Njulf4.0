using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests
{
    /// <summary>
    /// Unit tests for GPU struct layout verification.
    /// Verifies that C# struct sizes and alignments match shader expectations.
    /// </summary>
    [TestFixture]
    public class GPUStructLayoutTests
    {
        private static readonly Lazy<string> CommonGlslSource = new(ReadCommonGlsl);

        [Test]
        public void GPUStructSizes_MatchShaderContract()
        {
            var expected = new Dictionary<string, int>
            {
                ["SIZEOF_GPU_VERTEX"] = Marshal.SizeOf<GPUVertex>(),
                ["SIZEOF_GPU_MESH_INFO"] = Marshal.SizeOf<GPUMeshInfo>(),
                ["SIZEOF_GPU_MESHLET"] = Marshal.SizeOf<GPUMeshlet>(),
                ["SIZEOF_GPU_OBJECT_DATA"] = Marshal.SizeOf<GPUObjectData>(),
                ["SIZEOF_GPU_MATERIAL_DATA"] = Marshal.SizeOf<GPUMaterialData>(),
                ["SIZEOF_GPU_LIGHT"] = Marshal.SizeOf<GPULight>(),
                ["SIZEOF_GPU_SCENE_DATA"] = Marshal.SizeOf<GPUSceneData>(),
                ["SIZEOF_GPU_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUMeshletDrawCommand>(),
                ["SIZEOF_GPU_TILED_LIGHT_HEADER"] = Marshal.SizeOf<GPUTiledLightHeader>(),
                ["SIZEOF_GPU_LIGHT_INDEX"] = Marshal.SizeOf<GPULightIndex>(),
                ["SIZEOF_GPU_SCREEN_TO_VIEW_PARAMS"] = Marshal.SizeOf<GPUScreenToViewParams>(),
                ["SIZEOF_GPU_LIGHT_CULLING_PARAMS"] = Marshal.SizeOf<GPULightCullingParams>(),
                ["SIZEOF_GPU_DEPTH_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUDepthPushConstants>(),
                ["SIZEOF_GPU_FORWARD_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUForwardPushConstants>(),
                ["SIZEOF_GPU_LIGHT_CULL_PUSH_CONSTANTS"] = Marshal.SizeOf<GPULightCullPushConstants>()
            };

            Assert.Multiple(() =>
            {
                foreach (var (shaderConstant, hostSize) in expected)
                {
                    Assert.That(ReadShaderIntConstant(shaderConstant), Is.EqualTo(hostSize), shaderConstant);
                }
            });
        }

        [Test]
        public void CriticalStructs_HaveExpectedCurrentSizes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<GPUVertex>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUMeshInfo>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMeshlet>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUObjectData>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUMaterialData>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPULight>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUSceneData>(), Is.EqualTo(400));
                Assert.That(Marshal.SizeOf<GPUMeshletDrawCommand>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUTiledLightHeader>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPULightIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUScreenToViewParams>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPULightCullingParams>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUDepthPushConstants>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(), Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPULightCullPushConstants>(), Is.EqualTo(192));
            });
        }

        [Test]
        public void AllGPUStructs_AreNonEmpty()
        {
            var types = new[]
            {
                typeof(GPUVertex),
                typeof(GPUMeshInfo),
                typeof(GPUMeshlet),
                typeof(GPUObjectData),
                typeof(GPUMaterialData),
                typeof(GPULight),
                typeof(GPUSceneData),
                typeof(GPUMeshletDrawCommand),
                typeof(GPUTiledLightHeader),
                typeof(GPULightIndex),
                typeof(GPUScreenToViewParams),
                typeof(GPULightCullingParams),
                typeof(GPUDepthPushConstants),
                typeof(GPUForwardPushConstants),
                typeof(GPULightCullPushConstants)
            };

            foreach (var type in types)
            {
                Assert.That(Marshal.SizeOf(type), Is.GreaterThan(0), $"{type.Name} should have non-zero size");
            }
        }

        [Test]
        public void GPUMeshlet_HasCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.BoundingSphereCenter), "OFFSET_GPU_MESHLET_BOUNDING_SPHERE_CENTER");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.BoundingSphereRadius), "OFFSET_GPU_MESHLET_BOUNDING_SPHERE_RADIUS");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.VertexOffset), "OFFSET_GPU_MESHLET_VERTEX_OFFSET");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.VertexCount), "OFFSET_GPU_MESHLET_VERTEX_COUNT");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.IndexOffset), "OFFSET_GPU_MESHLET_INDEX_OFFSET");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.IndexCount), "OFFSET_GPU_MESHLET_INDEX_COUNT");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.LocalVertexOffset), "OFFSET_GPU_MESHLET_LOCAL_VERTEX_OFFSET");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.LocalVertexCount), "OFFSET_GPU_MESHLET_LOCAL_VERTEX_COUNT");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.LocalTriangleOffset), "OFFSET_GPU_MESHLET_LOCAL_TRIANGLE_OFFSET");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.LocalTriangleCount), "OFFSET_GPU_MESHLET_LOCAL_TRIANGLE_COUNT");
            });
        }

        [Test]
        public void GPUVertex_HasCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUVertex>(nameof(GPUVertex.Position), "OFFSET_GPU_VERTEX_POSITION");
                AssertFieldOffset<GPUVertex>(nameof(GPUVertex.Normal), "OFFSET_GPU_VERTEX_NORMAL");
                AssertFieldOffset<GPUVertex>(nameof(GPUVertex.TexCoord), "OFFSET_GPU_VERTEX_TEX_COORD");
                AssertFieldOffset<GPUVertex>(nameof(GPUVertex.Tangent), "OFFSET_GPU_VERTEX_TANGENT");
            });
        }

        [Test]
        public void GPUObjectData_HasCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.WorldMatrix), "OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX");
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.WorldMatrixInverseTranspose), "OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX_INVERSE_TRANSPOSE");
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.MeshIndex), "OFFSET_GPU_OBJECT_DATA_MESH_INDEX");
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.MaterialIndex), "OFFSET_GPU_OBJECT_DATA_MATERIAL_INDEX");
            });
        }

        [Test]
        public void PushConstants_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUDepthPushConstants>(nameof(GPUDepthPushConstants.ViewProjectionMatrix), "OFFSET_GPU_DEPTH_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUDepthPushConstants>(nameof(GPUDepthPushConstants.ScreenDimensions), "OFFSET_GPU_DEPTH_PUSH_SCREEN_DIMENSIONS");

                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.ViewProjectionMatrix), "OFFSET_GPU_FORWARD_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.InverseViewMatrix), "OFFSET_GPU_FORWARD_PUSH_INVERSE_VIEW_MATRIX");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.InverseProjectionMatrix), "OFFSET_GPU_FORWARD_PUSH_INVERSE_PROJECTION_MATRIX");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.CameraPosition), "OFFSET_GPU_FORWARD_PUSH_CAMERA_POSITION");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.Time), "OFFSET_GPU_FORWARD_PUSH_TIME");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.ScreenDimensions), "OFFSET_GPU_FORWARD_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.HiZTextureIndex), "OFFSET_GPU_FORWARD_PUSH_HIZ_TEXTURE_INDEX");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.HiZMipCount), "OFFSET_GPU_FORWARD_PUSH_HIZ_MIP_COUNT");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.OcclusionCullingEnabled), "OFFSET_GPU_FORWARD_PUSH_OCCLUSION_CULLING_ENABLED");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.OcclusionBias), "OFFSET_GPU_FORWARD_PUSH_OCCLUSION_BIAS");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.DebugViewMode), "OFFSET_GPU_FORWARD_PUSH_DEBUG_VIEW_MODE");

                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.ViewProjectionMatrix), "OFFSET_GPU_LIGHT_CULL_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.InverseViewProjectionMatrix), "OFFSET_GPU_LIGHT_CULL_PUSH_INVERSE_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.CameraPosition), "OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_POSITION");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.ScreenDimensions), "OFFSET_GPU_LIGHT_CULL_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.NearPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_NEAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.FarPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_FAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.LightCount), "OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_COUNT");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.TileCountY), "OFFSET_GPU_LIGHT_CULL_PUSH_TILE_COUNT_Y");
            });
        }

        [Test]
        public void RenderingConstants_ValidationWorks()
        {
            Assert.DoesNotThrow(() => RenderingConstants.ValidateFrameIndex(0));
            Assert.DoesNotThrow(() => RenderingConstants.ValidateFrameIndex(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RenderingConstants.ValidateFrameIndex(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RenderingConstants.ValidateFrameIndex(RenderingConstants.FramesInFlight));
        }

        [Test]
        public void RenderingConstants_NextFrameIndexWorks()
        {
            Assert.That(RenderingConstants.NextFrameIndex(0), Is.EqualTo(1));
            Assert.That(RenderingConstants.NextFrameIndex(1), Is.EqualTo(0));
            Assert.That(RenderingConstants.NextFrameIndex(2), Is.EqualTo(1));
            Assert.That(RenderingConstants.NextFrameIndex(3), Is.EqualTo(0));
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

        private static void AssertFieldOffset<T>(string fieldName, string shaderOffsetConstant)
            where T : struct
        {
            Assert.That(
                Marshal.OffsetOf<T>(fieldName).ToInt32(),
                Is.EqualTo(ReadShaderIntConstant(shaderOffsetConstant)),
                $"{typeof(T).Name}.{fieldName} must match {shaderOffsetConstant}");
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
