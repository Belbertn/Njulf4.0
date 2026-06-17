using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Njulf.Core.Geometry;
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
                ["SIZEOF_GPU_VERTEX_POSITION_STREAM"] = Marshal.SizeOf<GPUVertexPositionStream>(),
                ["SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM"] = Marshal.SizeOf<GPUVertexNormalTangentStream>(),
                ["SIZEOF_GPU_VERTEX_UV_COLOR_STREAM"] = Marshal.SizeOf<GPUVertexUvColorStream>(),
                ["SIZEOF_GPU_MESH_INFO"] = Marshal.SizeOf<GPUMeshInfo>(),
                ["SIZEOF_GPU_VERTEX_SKINNING_DATA"] = Marshal.SizeOf<GPUVertexSkinningData>(),
                ["SIZEOF_GPU_SKINNING_DISPATCH"] = Marshal.SizeOf<GPUSkinningDispatch>(),
                ["SIZEOF_GPU_SKINNING_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUSkinningPushConstants>(),
                ["SIZEOF_GPU_PARTICLE_INSTANCE"] = Marshal.SizeOf<GPUParticleInstance>(),
                ["SIZEOF_GPU_PARTICLE_BATCH"] = Marshal.SizeOf<GPUParticleBatch>(),
                ["SIZEOF_GPU_PARTICLE_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUParticlePushConstants>(),
                ["SIZEOF_GPU_MESHLET"] = Marshal.SizeOf<GPUMeshlet>(),
                ["SIZEOF_GPU_OBJECT_DATA"] = Marshal.SizeOf<GPUObjectData>(),
                ["SIZEOF_GPU_DEBUG_LINE_VERTEX"] = Marshal.SizeOf<GPUDebugLineVertex>(),
                ["SIZEOF_GPU_MATERIAL_DATA"] = Marshal.SizeOf<GPUMaterialData>(),
                ["SIZEOF_GPU_MATERIAL_EXTENSION_DATA"] = Marshal.SizeOf<GPUMaterialExtensionData>(),
                ["SIZEOF_GPU_LIGHT"] = Marshal.SizeOf<GPULight>(),
                ["SIZEOF_GPU_SCENE_DATA"] = Marshal.SizeOf<GPUSceneData>(),
                ["SIZEOF_GPU_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUMeshletDrawCommand>(),
                ["SIZEOF_GPU_PACKED_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUPackedMeshletDrawCommand>(),
                ["SIZEOF_GPU_MESHLET_TASK_FRAME_DATA"] = Marshal.SizeOf<GPUMeshletTaskFrameData>(),
                ["SIZEOF_GPU_TILED_LIGHT_HEADER"] = Marshal.SizeOf<GPUTiledLightHeader>(),
                ["SIZEOF_GPU_LIGHT_INDEX"] = Marshal.SizeOf<GPULightIndex>(),
                ["SIZEOF_GPU_SCREEN_TO_VIEW_PARAMS"] = Marshal.SizeOf<GPUScreenToViewParams>(),
                ["SIZEOF_GPU_LIGHT_CULLING_PARAMS"] = Marshal.SizeOf<GPULightCullingParams>(),
                ["SIZEOF_GPU_DEPTH_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUDepthPushConstants>(),
                ["SIZEOF_GPU_FORWARD_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUForwardPushConstants>(),
                ["SIZEOF_GPU_MOTION_VECTOR_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUMotionVectorPushConstants>(),
                ["SIZEOF_GPU_LIGHT_CULL_PUSH_CONSTANTS"] = Marshal.SizeOf<GPULightCullPushConstants>(),
                ["SIZEOF_GPU_SHADOW_DATA"] = Marshal.SizeOf<GPUShadowData>(),
                ["SIZEOF_GPU_SPOT_SHADOW"] = Marshal.SizeOf<GPUSpotShadow>(),
                ["SIZEOF_GPU_POINT_SHADOW"] = Marshal.SizeOf<GPUPointShadow>(),
                ["SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX"] = Marshal.SizeOf<GPULocalLightShadowIndex>(),
                ["SIZEOF_GPU_REFLECTION_PROBE_HEADER"] = Marshal.SizeOf<GPUReflectionProbeHeader>(),
                ["SIZEOF_GPU_REFLECTION_PROBE"] = Marshal.SizeOf<GPUReflectionProbe>(),
                ["SIZEOF_GPU_FOG_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUFogPushConstants>(),
                ["SIZEOF_GPU_ANTI_ALIASING_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUAntiAliasingPushConstants>(),
                ["SIZEOF_GPU_AMBIENT_OCCLUSION_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUAmbientOcclusionPushConstants>(),
                ["SIZEOF_GPU_AMBIENT_OCCLUSION_BLUR_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>()
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
                Assert.That(Marshal.SizeOf<GPUVertex>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUVertexPositionStream>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUVertexNormalTangentStream>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUVertexUvColorStream>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMeshInfo>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUVertexSkinningData>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningDispatch>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningPushConstants>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleInstance>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUParticleBatch>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticlePushConstants>(), Is.EqualTo(248));
                Assert.That(Marshal.SizeOf<GPUMeshlet>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUObjectData>(), Is.EqualTo(208));
                Assert.That(Marshal.SizeOf<GPUDebugLineVertex>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMaterialData>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUMaterialExtensionData>(), Is.EqualTo(548));
                Assert.That(Marshal.SizeOf<GPULight>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUSceneData>(), Is.EqualTo(400));
                Assert.That(Marshal.SizeOf<GPUMeshletDrawCommand>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUPackedMeshletDrawCommand>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMeshletTaskFrameData>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUTiledLightHeader>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPULightIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUScreenToViewParams>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPULightCullingParams>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUDepthPushConstants>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(), Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPUMotionVectorPushConstants>(), Is.EqualTo(156));
                Assert.That(Marshal.SizeOf<GPULightCullPushConstants>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUShadowData>(), Is.EqualTo(304));
                Assert.That(Marshal.SizeOf<GPUSpotShadow>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUPointShadow>(), Is.EqualTo(432));
                Assert.That(Marshal.SizeOf<GPULocalLightShadowIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUReflectionProbeHeader>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUReflectionProbe>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUFogPushConstants>(), Is.EqualTo(224));
                Assert.That(Marshal.SizeOf<GPUAntiAliasingPushConstants>(), Is.EqualTo(100));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionPushConstants>(), Is.EqualTo(176));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>(), Is.EqualTo(96));
            });
        }

        [Test]
        public void AllGPUStructs_AreNonEmpty()
        {
            var types = new[]
            {
                typeof(GPUVertex),
                typeof(GPUVertexPositionStream),
                typeof(GPUVertexNormalTangentStream),
                typeof(GPUVertexUvColorStream),
                typeof(GPUMeshInfo),
                typeof(GPUVertexSkinningData),
                typeof(GPUSkinningDispatch),
                typeof(GPUSkinningPushConstants),
                typeof(GPUParticleInstance),
                typeof(GPUParticleBatch),
                typeof(GPUParticlePushConstants),
                typeof(GPUMeshlet),
                typeof(GPUObjectData),
                typeof(GPUDebugLineVertex),
                typeof(GPUMaterialData),
                typeof(GPUMaterialExtensionData),
                typeof(GPULight),
                typeof(GPUSceneData),
                typeof(GPUMeshletDrawCommand),
                typeof(GPUPackedMeshletDrawCommand),
                typeof(GPUMeshletTaskFrameData),
                typeof(GPUTiledLightHeader),
                typeof(GPULightIndex),
                typeof(GPUScreenToViewParams),
                typeof(GPULightCullingParams),
                typeof(GPUDepthPushConstants),
                typeof(GPUForwardPushConstants),
                typeof(GPUMotionVectorPushConstants),
                typeof(GPULightCullPushConstants),
                typeof(GPUShadowData),
                typeof(GPUSpotShadow),
                typeof(GPUPointShadow),
                typeof(GPULocalLightShadowIndex),
                typeof(GPUReflectionProbeHeader),
                typeof(GPUReflectionProbe),
                typeof(GPUFogPushConstants),
                typeof(GPUAntiAliasingPushConstants),
                typeof(GPUAmbientOcclusionPushConstants),
                typeof(GPUAmbientOcclusionBlurPushConstants)
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
        public void GPUPackedMeshletTaskStructs_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.MeshletIndex), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MESHLET_INDEX");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.InstanceId), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_INSTANCE_ID");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.MaterialIndex), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MATERIAL_INDEX");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.Flags), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_FLAGS");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.WorldCenterRadius), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.FrustumPlane0), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE0");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.FrustumPlane5), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE5");
            });
        }

        [Test]
        public void SharedMeshlet_HasStableRendererLayout()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<Meshlet>(), Is.EqualTo(48));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.BoundingSphereCenter)).ToInt32(), Is.EqualTo(0));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.BoundingSphereRadius)).ToInt32(), Is.EqualTo(12));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.VertexOffset)).ToInt32(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.VertexCount)).ToInt32(), Is.EqualTo(20));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.IndexOffset)).ToInt32(), Is.EqualTo(24));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.IndexCount)).ToInt32(), Is.EqualTo(28));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.LocalVertexOffset)).ToInt32(), Is.EqualTo(32));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.LocalVertexCount)).ToInt32(), Is.EqualTo(36));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.LocalTriangleOffset)).ToInt32(), Is.EqualTo(40));
                Assert.That(Marshal.OffsetOf<Meshlet>(nameof(Meshlet.LocalTriangleCount)).ToInt32(), Is.EqualTo(44));
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
                AssertFieldOffset<GPUVertex>(nameof(GPUVertex.Color), "OFFSET_GPU_VERTEX_COLOR");
            });
        }

        [Test]
        public void GPUSkinningStructs_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUVertexSkinningData>(nameof(GPUVertexSkinningData.Joint0), "OFFSET_GPU_VERTEX_SKINNING_DATA_JOINT0");
                AssertFieldOffset<GPUVertexSkinningData>(nameof(GPUVertexSkinningData.Weight0), "OFFSET_GPU_VERTEX_SKINNING_DATA_WEIGHT0");
                AssertFieldOffset<GPUSkinningDispatch>(nameof(GPUSkinningDispatch.SourceVertexOffset), "OFFSET_GPU_SKINNING_DISPATCH_SOURCE_VERTEX_OFFSET");
                AssertFieldOffset<GPUSkinningDispatch>(nameof(GPUSkinningDispatch.SourceSkinningDataOffset), "OFFSET_GPU_SKINNING_DISPATCH_SOURCE_SKINNING_DATA_OFFSET");
                AssertFieldOffset<GPUSkinningDispatch>(nameof(GPUSkinningDispatch.DestinationVertexOffset), "OFFSET_GPU_SKINNING_DISPATCH_DESTINATION_VERTEX_OFFSET");
                AssertFieldOffset<GPUSkinningDispatch>(nameof(GPUSkinningDispatch.VertexCount), "OFFSET_GPU_SKINNING_DISPATCH_VERTEX_COUNT");
                AssertFieldOffset<GPUSkinningDispatch>(nameof(GPUSkinningDispatch.SkinMatrixOffset), "OFFSET_GPU_SKINNING_DISPATCH_SKIN_MATRIX_OFFSET");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.PositionSize), "OFFSET_GPU_PARTICLE_INSTANCE_POSITION_SIZE");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.VelocityRotation), "OFFSET_GPU_PARTICLE_INSTANCE_VELOCITY_ROTATION");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.Color), "OFFSET_GPU_PARTICLE_INSTANCE_COLOR");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.EmissiveLifetimeSoftClip), "OFFSET_GPU_PARTICLE_INSTANCE_EMISSIVE_LIFETIME_SOFT_CLIP");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.TextureIndex), "OFFSET_GPU_PARTICLE_INSTANCE_TEXTURE_INDEX");
                AssertFieldOffset<GPUParticleInstance>(nameof(GPUParticleInstance.BlendMode), "OFFSET_GPU_PARTICLE_INSTANCE_BLEND_MODE");
                AssertFieldOffset<GPUParticleBatch>(nameof(GPUParticleBatch.Start), "OFFSET_GPU_PARTICLE_BATCH_START");
                AssertFieldOffset<GPUParticleBatch>(nameof(GPUParticleBatch.Count), "OFFSET_GPU_PARTICLE_BATCH_COUNT");
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
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.SkinnedVertexOffset), "OFFSET_GPU_OBJECT_DATA_SKINNED_VERTEX_OFFSET");
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.SkinningEnabled), "OFFSET_GPU_OBJECT_DATA_SKINNING_ENABLED");
                AssertFieldOffset<GPUObjectData>(nameof(GPUObjectData.PreviousWorldMatrix), "OFFSET_GPU_OBJECT_DATA_PREVIOUS_WORLD_MATRIX");
            });
        }

        [Test]
        public void PushConstants_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUDepthPushConstants>(nameof(GPUDepthPushConstants.ViewProjectionMatrix), "OFFSET_GPU_DEPTH_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUDepthPushConstants>(nameof(GPUDepthPushConstants.ScreenDimensions), "OFFSET_GPU_DEPTH_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUDepthPushConstants>(nameof(GPUDepthPushConstants.MeshletDrawBufferBaseIndex), "OFFSET_GPU_DEPTH_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX");

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
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.DebugAndAoFlags), "OFFSET_GPU_FORWARD_PUSH_DEBUG_AND_AO_FLAGS");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.ViewProjectionMatrix), "OFFSET_GPU_MOTION_VECTOR_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousViewProjectionMatrix), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.ScreenDimensions), "OFFSET_GPU_MOTION_VECTOR_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.CurrentFrameIndex), "OFFSET_GPU_MOTION_VECTOR_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.MeshletDrawCount), "OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_COUNT");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.MeshletDrawBufferBaseIndex), "OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousFrameValid), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_FRAME_VALID");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.ViewProjectionMatrix), "OFFSET_GPU_PARTICLE_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.InverseViewMatrix), "OFFSET_GPU_PARTICLE_PUSH_INVERSE_VIEW_MATRIX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.InverseProjectionMatrix), "OFFSET_GPU_PARTICLE_PUSH_INVERSE_PROJECTION_MATRIX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.CameraPosition), "OFFSET_GPU_PARTICLE_PUSH_CAMERA_POSITION");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.ScreenDimensions), "OFFSET_GPU_PARTICLE_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.InstanceOffset), "OFFSET_GPU_PARTICLE_PUSH_INSTANCE_OFFSET");

                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.ViewProjectionMatrix), "OFFSET_GPU_LIGHT_CULL_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.InverseViewProjectionMatrix), "OFFSET_GPU_LIGHT_CULL_PUSH_INVERSE_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.CameraPosition), "OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_POSITION");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.ScreenDimensions), "OFFSET_GPU_LIGHT_CULL_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.NearPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_NEAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.FarPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_FAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.LightCount), "OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_COUNT");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.TileCountY), "OFFSET_GPU_LIGHT_CULL_PUSH_TILE_COUNT_Y");

                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection0), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection1), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION1");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection2), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION2");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection3), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION3");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.CascadeSplits), "OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.Settings), "OFFSET_GPU_SHADOW_DATA_SETTINGS");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.Indices), "OFFSET_GPU_SHADOW_DATA_INDICES");

                AssertFieldOffset<GPUSpotShadow>(nameof(GPUSpotShadow.LightViewProjection), "OFFSET_GPU_SPOT_SHADOW_LIGHT_VIEW_PROJECTION");
                AssertFieldOffset<GPUSpotShadow>(nameof(GPUSpotShadow.AtlasScaleOffset), "OFFSET_GPU_SPOT_SHADOW_ATLAS_SCALE_OFFSET");
                AssertFieldOffset<GPUSpotShadow>(nameof(GPUSpotShadow.BiasStrengthTexelSize), "OFFSET_GPU_SPOT_SHADOW_BIAS_STRENGTH_TEXEL_SIZE");
                AssertFieldOffset<GPUSpotShadow>(nameof(GPUSpotShadow.LightIndex), "OFFSET_GPU_SPOT_SHADOW_LIGHT_INDEX");

                AssertFieldOffset<GPUPointShadow>(nameof(GPUPointShadow.FaceViewProjection0), "OFFSET_GPU_POINT_SHADOW_FACE_VIEW_PROJECTION0");
                AssertFieldOffset<GPUPointShadow>(nameof(GPUPointShadow.PositionRange), "OFFSET_GPU_POINT_SHADOW_POSITION_RANGE");
                AssertFieldOffset<GPUPointShadow>(nameof(GPUPointShadow.BiasStrengthTexelSize), "OFFSET_GPU_POINT_SHADOW_BIAS_STRENGTH_TEXEL_SIZE");
                AssertFieldOffset<GPUPointShadow>(nameof(GPUPointShadow.LightIndex), "OFFSET_GPU_POINT_SHADOW_LIGHT_INDEX");

                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.WorldToProbe), "OFFSET_GPU_REFLECTION_PROBE_WORLD_TO_PROBE");
                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.PositionAndRadius), "OFFSET_GPU_REFLECTION_PROBE_POSITION_AND_RADIUS");
                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.BoxMin), "OFFSET_GPU_REFLECTION_PROBE_BOX_MIN");
                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.BoxMax), "OFFSET_GPU_REFLECTION_PROBE_BOX_MAX");
                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.BlendParams), "OFFSET_GPU_REFLECTION_PROBE_BLEND_PARAMS");
                AssertFieldOffset<GPUReflectionProbe>(nameof(GPUReflectionProbe.CubemapArrayIndex), "OFFSET_GPU_REFLECTION_PROBE_CUBEMAP_ARRAY_INDEX");
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
