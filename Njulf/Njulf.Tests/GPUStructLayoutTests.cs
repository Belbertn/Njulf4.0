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
                ["SIZEOF_GPU_PARTICLE_FRAME_DATA"] = Marshal.SizeOf<GPUParticleFrameData>(),
                ["SIZEOF_GPU_PARTICLE_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUParticlePushConstants>(),
                ["SIZEOF_GPU_PARTICLE_EMITTER"] = Marshal.SizeOf<GPUParticleEmitter>(),
                ["SIZEOF_GPU_PARTICLE_CURVE_SAMPLE"] = Marshal.SizeOf<GPUParticleCurveSample>(),
                ["SIZEOF_GPU_PARTICLE_STATE"] = Marshal.SizeOf<GPUParticleState>(),
                ["SIZEOF_GPU_PARTICLE_COUNTERS"] = Marshal.SizeOf<GPUParticleCounters>(),
                ["SIZEOF_GPU_PARTICLE_DRAW_COMMAND"] = Marshal.SizeOf<GPUParticleDrawCommand>(),
                ["SIZEOF_GPU_PARTICLE_SORT_KEY"] = Marshal.SizeOf<GPUParticleSortKey>(),
                ["SIZEOF_GPU_PARTICLE_RESET_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUParticleResetPushConstants>(),
                ["SIZEOF_GPU_PARTICLE_SIMULATE_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUParticleSimulatePushConstants>(),
                ["SIZEOF_GPU_PARTICLE_SORT_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUParticleSortPushConstants>(),
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
                ["SIZEOF_GPU_FOLIAGE_PROTOTYPE"] = Marshal.SizeOf<GPUFoliagePrototype>(),
                ["SIZEOF_GPU_FOLIAGE_PATCH"] = Marshal.SizeOf<GPUFoliagePatch>(),
                ["SIZEOF_GPU_FOLIAGE_CLUSTER"] = Marshal.SizeOf<GPUFoliageCluster>(),
                ["SIZEOF_GPU_FOLIAGE_INSTANCE"] = Marshal.SizeOf<GPUFoliageInstance>(),
                ["SIZEOF_GPU_FOLIAGE_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(),
                ["SIZEOF_GPU_FOLIAGE_COUNTERS"] = Marshal.SizeOf<GPUFoliageCounters>(),
                ["SIZEOF_GPU_FOLIAGE_DISPATCH_ARGS"] = Marshal.SizeOf<GPUFoliageDispatchArgs>(),
                ["SIZEOF_GPU_SCENE_SUBMISSION_COUNTERS"] = Marshal.SizeOf<GPUSceneSubmissionCounters>(),
                ["SIZEOF_GPU_SCENE_OPAQUE_COMPACTION_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                ["SIZEOF_GPU_FOLIAGE_CULL_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUFoliageCullPushConstants>(),
                ["SIZEOF_GPU_FOLIAGE_DRAW_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
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
                ["SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER"] = Marshal.SizeOf<GPUDdgiProbeVolumeHeader>(),
                ["SIZEOF_GPU_DDGI_PROBE_VOLUME"] = Marshal.SizeOf<GPUDdgiProbeVolume>(),
                ["SIZEOF_GPU_DDGI_PROBE_STATE"] = Marshal.SizeOf<GPUDdgiProbeState>(),
                ["SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST"] = Marshal.SizeOf<GPUDdgiProbeUpdateRequest>(),
                ["SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION"] = Marshal.SizeOf<GPUDdgiProbeRelocationClassification>(),
                ["SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE"] = Marshal.SizeOf<GPUDdgiRayQueryInstance>(),
                ["SIZEOF_GPU_DDGI_UPDATE_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUDdgiUpdatePushConstants>(),
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
                Assert.That(Marshal.SizeOf<GPUMeshInfo>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUVertexSkinningData>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningDispatch>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningPushConstants>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleInstance>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUParticleBatch>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleFrameData>(), Is.EqualTo(224));
                Assert.That(Marshal.SizeOf<GPUParticlePushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleEmitter>(), Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPUParticleCurveSample>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleState>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUParticleCounters>(), Is.EqualTo(88));
                Assert.That(Marshal.SizeOf<GPUParticleDrawCommand>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleSortKey>(), Is.EqualTo(8));
                Assert.That(Marshal.SizeOf<GPUParticleResetPushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleSimulatePushConstants>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUParticleSortPushConstants>(), Is.EqualTo(32));
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
                Assert.That(Marshal.SizeOf<GPUFoliagePrototype>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUFoliagePatch>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageCluster>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageInstance>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUFoliageCounters>(), Is.EqualTo(40));
                Assert.That(Marshal.SizeOf<GPUFoliageCullPushConstants>(), Is.EqualTo(52));
                Assert.That(Marshal.SizeOf<GPUFoliageDrawPushConstants>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUTiledLightHeader>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPULightIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUScreenToViewParams>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPULightCullingParams>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUDepthPushConstants>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(), Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPUMotionVectorPushConstants>(), Is.EqualTo(160));
                Assert.That(Marshal.SizeOf<GPULightCullPushConstants>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUShadowData>(), Is.EqualTo(304));
                Assert.That(Marshal.SizeOf<GPUSpotShadow>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUPointShadow>(), Is.EqualTo(432));
                Assert.That(Marshal.SizeOf<GPULocalLightShadowIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUReflectionProbeHeader>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUReflectionProbe>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolumeHeader>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolume>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeState>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeUpdateRequest>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeRelocationClassification>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUDdgiRayQueryInstance>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUDdgiUpdatePushConstants>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUFogPushConstants>(), Is.EqualTo(224));
                Assert.That(Marshal.SizeOf<GPUAntiAliasingPushConstants>(), Is.EqualTo(100));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionPushConstants>(), Is.EqualTo(176));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>(), Is.EqualTo(96));
            });
        }

        [Test]
        public void ForwardPushConstants_PackAmbientOcclusionSamplingMode()
        {
            uint flags = GPUForwardPushConstants.PackDebugAndAoFlags(
                debugViewMode: 3,
                ambientOcclusionEnabled: true,
                ambientOcclusionDebugView: 5,
                transparentReceiveShadows: true,
                transparencyDebugView: 7,
                ambientOcclusionForwardSamplingMode: (uint)AmbientOcclusionForwardSamplingMode.DepthAwareUpsample,
                globalIlluminationEnabled: true,
                screenSpaceGlobalIlluminationEnabled: true);

            Assert.Multiple(() =>
            {
                Assert.That(flags & 0xffu, Is.EqualTo(3u));
                Assert.That((flags >> 8) & 1u, Is.EqualTo(1u));
                Assert.That((flags >> 16) & 0xffu, Is.EqualTo(5u));
                Assert.That((flags >> 24) & 1u, Is.EqualTo(1u));
                Assert.That((flags >> 25) & 0x07u, Is.EqualTo(7u));
                Assert.That((flags >> 28) & 1u, Is.EqualTo(1u));
                Assert.That((flags >> 29) & 0x03u, Is.EqualTo((uint)AmbientOcclusionForwardSamplingMode.DepthAwareUpsample));
                Assert.That((flags >> 31) & 1u, Is.EqualTo(1u));
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
                typeof(GPUParticleFrameData),
                typeof(GPUParticlePushConstants),
                typeof(GPUParticleEmitter),
                typeof(GPUParticleCurveSample),
                typeof(GPUParticleState),
                typeof(GPUParticleCounters),
                typeof(GPUParticleDrawCommand),
                typeof(GPUParticleSortKey),
                typeof(GPUParticleResetPushConstants),
                typeof(GPUParticleSimulatePushConstants),
                typeof(GPUParticleSortPushConstants),
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
                typeof(GPUFoliagePrototype),
                typeof(GPUFoliagePatch),
                typeof(GPUFoliageCluster),
                typeof(GPUFoliageInstance),
                typeof(GPUFoliageMeshletDrawCommand),
                typeof(GPUFoliageCounters),
                typeof(GPUFoliageDispatchArgs),
                typeof(GPUFoliageCullPushConstants),
                typeof(GPUFoliageDrawPushConstants),
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
                typeof(GPUDdgiProbeVolumeHeader),
                typeof(GPUDdgiProbeVolume),
                typeof(GPUDdgiProbeState),
                typeof(GPUDdgiProbeUpdateRequest),
                typeof(GPUDdgiProbeRelocationClassification),
                typeof(GPUDdgiRayQueryInstance),
                typeof(GPUDdgiUpdatePushConstants),
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
        public void GPUFoliageStructs_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshMetadataIndex), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESH_METADATA_INDEX");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletOffset), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_OFFSET");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletCount), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_COUNT");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletLod1Offset), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD1_OFFSET");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletLod1Count), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD1_COUNT");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletLod2Offset), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD2_OFFSET");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletLod2Count), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD2_COUNT");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MaterialIndex), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MATERIAL_INDEX");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.GeometryMode), "OFFSET_GPU_FOLIAGE_PROTOTYPE_GEOMETRY_MODE");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.Flags), "OFFSET_GPU_FOLIAGE_PROTOTYPE_FLAGS");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.BladeHeight), "OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_HEIGHT");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.BladeWidth), "OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_WIDTH");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.LodDistances), "OFFSET_GPU_FOLIAGE_PROTOTYPE_LOD_DISTANCES");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.WindParams), "OFFSET_GPU_FOLIAGE_PROTOTYPE_WIND_PARAMS");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.LightingParams), "OFFSET_GPU_FOLIAGE_PROTOTYPE_LIGHTING_PARAMS");

                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.BoundsMinDensity), "OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MIN_DENSITY");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.BoundsMaxSeed), "OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MAX_SEED");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.PrototypeIndex), "OFFSET_GPU_FOLIAGE_PATCH_PROTOTYPE_INDEX");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.ClusterOffset), "OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_OFFSET");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.ClusterCount), "OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_COUNT");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.DensityTextureIndex), "OFFSET_GPU_FOLIAGE_PATCH_DENSITY_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.Seed), "OFFSET_GPU_FOLIAGE_PATCH_SEED");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.Flags), "OFFSET_GPU_FOLIAGE_PATCH_FLAGS");

                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.WorldCenterRadius), "OFFSET_GPU_FOLIAGE_CLUSTER_WORLD_CENTER_RADIUS");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.BoundsMinDensity), "OFFSET_GPU_FOLIAGE_CLUSTER_BOUNDS_MIN_DENSITY");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.BoundsMaxLod), "OFFSET_GPU_FOLIAGE_CLUSTER_BOUNDS_MAX_LOD");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.PatchIndex), "OFFSET_GPU_FOLIAGE_CLUSTER_PATCH_INDEX");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.FirstInstance), "OFFSET_GPU_FOLIAGE_CLUSTER_FIRST_INSTANCE");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.InstanceCount), "OFFSET_GPU_FOLIAGE_CLUSTER_INSTANCE_COUNT");
                AssertFieldOffset<GPUFoliageCluster>(nameof(GPUFoliageCluster.RandomSeed), "OFFSET_GPU_FOLIAGE_CLUSTER_RANDOM_SEED");

                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.PositionScale), "OFFSET_GPU_FOLIAGE_INSTANCE_POSITION_SCALE");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.RotationWind), "OFFSET_GPU_FOLIAGE_INSTANCE_ROTATION_WIND");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.ColorVariation), "OFFSET_GPU_FOLIAGE_INSTANCE_COLOR_VARIATION");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.PrototypeIndex), "OFFSET_GPU_FOLIAGE_INSTANCE_PROTOTYPE_INDEX");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.PatchIndex), "OFFSET_GPU_FOLIAGE_INSTANCE_PATCH_INDEX");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.ClusterIndex), "OFFSET_GPU_FOLIAGE_INSTANCE_CLUSTER_INDEX");
                AssertFieldOffset<GPUFoliageInstance>(nameof(GPUFoliageInstance.Flags), "OFFSET_GPU_FOLIAGE_INSTANCE_FLAGS");

                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.MeshletIndex), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_MESHLET_INDEX");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.InstanceIndex), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_INSTANCE_INDEX");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.PrototypeIndex), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_PROTOTYPE_INDEX");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.MaterialIndex), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_MATERIAL_INDEX");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.WorldCenterRadius), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.Flags), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_FLAGS");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.LodLevel), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_LOD_LEVEL");
                AssertFieldOffset<GPUFoliageMeshletDrawCommand>(nameof(GPUFoliageMeshletDrawCommand.ClusterIndex), "OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_CLUSTER_INDEX");

                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.VisibleClusterCount), "OFFSET_GPU_FOLIAGE_COUNTERS_VISIBLE_CLUSTER_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.CulledClusterCount), "OFFSET_GPU_FOLIAGE_COUNTERS_CULLED_CLUSTER_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.Lod0VisibleCount), "OFFSET_GPU_FOLIAGE_COUNTERS_LOD0_VISIBLE_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.Lod1VisibleCount), "OFFSET_GPU_FOLIAGE_COUNTERS_LOD1_VISIBLE_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.Lod2VisibleCount), "OFFSET_GPU_FOLIAGE_COUNTERS_LOD2_VISIBLE_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.HiZTestedCount), "OFFSET_GPU_FOLIAGE_COUNTERS_HIZ_TESTED_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.HiZRejectedCount), "OFFSET_GPU_FOLIAGE_COUNTERS_HIZ_REJECTED_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.VisibleMeshletDrawCount), "OFFSET_GPU_FOLIAGE_COUNTERS_VISIBLE_MESHLET_DRAW_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.MeshletDrawOverflowCount), "OFFSET_GPU_FOLIAGE_COUNTERS_MESHLET_DRAW_OVERFLOW_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.FarImpostorVisibleCount), "OFFSET_GPU_FOLIAGE_COUNTERS_FAR_IMPOSTOR_VISIBLE_COUNT");

                AssertFieldOffset<GPUFoliageDispatchArgs>(nameof(GPUFoliageDispatchArgs.GroupCountX), "OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_X");
                AssertFieldOffset<GPUFoliageDispatchArgs>(nameof(GPUFoliageDispatchArgs.GroupCountY), "OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_Y");
                AssertFieldOffset<GPUFoliageDispatchArgs>(nameof(GPUFoliageDispatchArgs.GroupCountZ), "OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_Z");
                AssertFieldOffset<GPUFoliageDispatchArgs>(nameof(GPUFoliageDispatchArgs.Padding0), "OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_PADDING0");

                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.CameraPositionMaxDistance), "OFFSET_GPU_FOLIAGE_CULL_PUSH_CAMERA_POSITION_MAX_DISTANCE");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.CurrentFrameIndex), "OFFSET_GPU_FOLIAGE_CULL_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.ClusterCount), "OFFSET_GPU_FOLIAGE_CULL_PUSH_CLUSTER_COUNT");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.VisibleClusterCapacity), "OFFSET_GPU_FOLIAGE_CULL_PUSH_VISIBLE_CLUSTER_CAPACITY");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.MeshletDrawCapacity), "OFFSET_GPU_FOLIAGE_CULL_PUSH_MESHLET_DRAW_CAPACITY");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.IndirectDispatchBufferBaseIndex), "OFFSET_GPU_FOLIAGE_CULL_PUSH_INDIRECT_DISPATCH_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.Flags), "OFFSET_GPU_FOLIAGE_CULL_PUSH_FLAGS");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.AuthoredMeshletWorkItemCount), "OFFSET_GPU_FOLIAGE_CULL_PUSH_AUTHORED_MESHLET_WORK_ITEM_COUNT");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.FirstAuthoredClusterIndex), "OFFSET_GPU_FOLIAGE_CULL_PUSH_FIRST_AUTHORED_CLUSTER_INDEX");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.AuthoredClusterCount), "OFFSET_GPU_FOLIAGE_CULL_PUSH_AUTHORED_CLUSTER_COUNT");

                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ViewProjectionMatrix), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.CameraPositionTime), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CAMERA_POSITION_TIME");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ScreenDimensions), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.CurrentFrameIndex), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ClusterDrawCount), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CLUSTER_DRAW_COUNT");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.VisibleClusterBufferBaseIndex), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_VISIBLE_CLUSTER_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.Flags), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_FLAGS");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.DebugView), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_DEBUG_VIEW");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ShadowDensityScale), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_SHADOW_DENSITY_SCALE");
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
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.WorldMatrix), "OFFSET_GPU_PARTICLE_EMITTER_WORLD_MATRIX");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.SpawnShape0), "OFFSET_GPU_PARTICLE_EMITTER_SPAWN_SHAPE0");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.SpawnShape1), "OFFSET_GPU_PARTICLE_EMITTER_SPAWN_SHAPE1");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.InitialVelocityMin), "OFFSET_GPU_PARTICLE_EMITTER_INITIAL_VELOCITY_MIN");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.InitialVelocityMax), "OFFSET_GPU_PARTICLE_EMITTER_INITIAL_VELOCITY_MAX");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.AccelerationDrag), "OFFSET_GPU_PARTICLE_EMITTER_ACCELERATION_DRAG");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.LifetimeSize), "OFFSET_GPU_PARTICLE_EMITTER_LIFETIME_SIZE");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.Color), "OFFSET_GPU_PARTICLE_EMITTER_COLOR");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.MaterialIndex), "OFFSET_GPU_PARTICLE_EMITTER_MATERIAL_INDEX");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.ColorEnd), "OFFSET_GPU_PARTICLE_EMITTER_COLOR_END");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.EmissiveAngularVelocity), "OFFSET_GPU_PARTICLE_EMITTER_EMISSIVE_ANGULAR_VELOCITY");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.RotationParams), "OFFSET_GPU_PARTICLE_EMITTER_ROTATION_PARAMS");
                AssertFieldOffset<GPUParticleEmitter>(nameof(GPUParticleEmitter.TimingParams), "OFFSET_GPU_PARTICLE_EMITTER_TIMING_PARAMS");
                AssertFieldOffset<GPUParticleCurveSample>(nameof(GPUParticleCurveSample.Color), "OFFSET_GPU_PARTICLE_CURVE_SAMPLE_COLOR");
                AssertFieldOffset<GPUParticleCurveSample>(nameof(GPUParticleCurveSample.Properties), "OFFSET_GPU_PARTICLE_CURVE_SAMPLE_PROPERTIES");
                AssertFieldOffset<GPUParticleState>(nameof(GPUParticleState.PositionAge), "OFFSET_GPU_PARTICLE_STATE_POSITION_AGE");
                AssertFieldOffset<GPUParticleState>(nameof(GPUParticleState.VelocityLifetime), "OFFSET_GPU_PARTICLE_STATE_VELOCITY_LIFETIME");
                AssertFieldOffset<GPUParticleState>(nameof(GPUParticleState.Color), "OFFSET_GPU_PARTICLE_STATE_COLOR");
                AssertFieldOffset<GPUParticleState>(nameof(GPUParticleState.SizeRotation), "OFFSET_GPU_PARTICLE_STATE_SIZE_ROTATION");
                AssertFieldOffset<GPUParticleState>(nameof(GPUParticleState.EmitterIndex), "OFFSET_GPU_PARTICLE_STATE_EMITTER_INDEX");
                AssertFieldOffset<GPUParticleCounters>(nameof(GPUParticleCounters.AliveCount), "OFFSET_GPU_PARTICLE_COUNTERS_ALIVE_COUNT");
                AssertFieldOffset<GPUParticleCounters>(nameof(GPUParticleCounters.DeadCount), "OFFSET_GPU_PARTICLE_COUNTERS_DEAD_COUNT");
                AssertFieldOffset<GPUParticleCounters>(nameof(GPUParticleCounters.RenderedCount), "OFFSET_GPU_PARTICLE_COUNTERS_RENDERED_COUNT");
                AssertFieldOffset<GPUParticleDrawCommand>(nameof(GPUParticleDrawCommand.VertexCount), "OFFSET_GPU_PARTICLE_DRAW_COMMAND_VERTEX_COUNT");
                AssertFieldOffset<GPUParticleDrawCommand>(nameof(GPUParticleDrawCommand.InstanceCount), "OFFSET_GPU_PARTICLE_DRAW_COMMAND_INSTANCE_COUNT");
                AssertFieldOffset<GPUParticleSortKey>(nameof(GPUParticleSortKey.Key), "OFFSET_GPU_PARTICLE_SORT_KEY_KEY");
                AssertFieldOffset<GPUParticleSortKey>(nameof(GPUParticleSortKey.InstanceIndex), "OFFSET_GPU_PARTICLE_SORT_KEY_INSTANCE_INDEX");
                AssertFieldOffset<GPUParticleResetPushConstants>(nameof(GPUParticleResetPushConstants.CurrentFrameIndex), "OFFSET_GPU_PARTICLE_RESET_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUParticleResetPushConstants>(nameof(GPUParticleResetPushConstants.ParticleCapacity), "OFFSET_GPU_PARTICLE_RESET_PUSH_PARTICLE_CAPACITY");
                AssertFieldOffset<GPUParticleResetPushConstants>(nameof(GPUParticleResetPushConstants.DrawCapacity), "OFFSET_GPU_PARTICLE_RESET_PUSH_DRAW_CAPACITY");
                AssertFieldOffset<GPUParticleResetPushConstants>(nameof(GPUParticleResetPushConstants.Flags), "OFFSET_GPU_PARTICLE_RESET_PUSH_FLAGS");
                AssertFieldOffset<GPUParticleSimulatePushConstants>(nameof(GPUParticleSimulatePushConstants.CurrentFrameIndex), "OFFSET_GPU_PARTICLE_SIMULATE_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUParticleSimulatePushConstants>(nameof(GPUParticleSimulatePushConstants.ParticleCapacity), "OFFSET_GPU_PARTICLE_SIMULATE_PUSH_PARTICLE_CAPACITY");
                AssertFieldOffset<GPUParticleSimulatePushConstants>(nameof(GPUParticleSimulatePushConstants.EmitterCount), "OFFSET_GPU_PARTICLE_SIMULATE_PUSH_EMITTER_COUNT");
                AssertFieldOffset<GPUParticleSimulatePushConstants>(nameof(GPUParticleSimulatePushConstants.DeltaSeconds), "OFFSET_GPU_PARTICLE_SIMULATE_PUSH_DELTA_SECONDS");
                AssertFieldOffset<GPUParticleSimulatePushConstants>(nameof(GPUParticleSimulatePushConstants.TimeSeconds), "OFFSET_GPU_PARTICLE_SIMULATE_PUSH_TIME_SECONDS");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.CurrentFrameIndex), "OFFSET_GPU_PARTICLE_SORT_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.ParticleCapacity), "OFFSET_GPU_PARTICLE_SORT_PUSH_PARTICLE_CAPACITY");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.Mode), "OFFSET_GPU_PARTICLE_SORT_PUSH_MODE");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.Bucket), "OFFSET_GPU_PARTICLE_SORT_PUSH_BUCKET");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.SortLevel), "OFFSET_GPU_PARTICLE_SORT_PUSH_SORT_LEVEL");
                AssertFieldOffset<GPUParticleSortPushConstants>(nameof(GPUParticleSortPushConstants.SortStage), "OFFSET_GPU_PARTICLE_SORT_PUSH_SORT_STAGE");
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
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.Time), "OFFSET_GPU_MOTION_VECTOR_PUSH_TIME");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousTime), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_TIME");
                AssertFieldOffset<GPUParticleFrameData>(nameof(GPUParticleFrameData.ViewProjectionMatrix), "OFFSET_GPU_PARTICLE_FRAME_DATA_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUParticleFrameData>(nameof(GPUParticleFrameData.InverseViewMatrix), "OFFSET_GPU_PARTICLE_FRAME_DATA_INVERSE_VIEW_MATRIX");
                AssertFieldOffset<GPUParticleFrameData>(nameof(GPUParticleFrameData.InverseProjectionMatrix), "OFFSET_GPU_PARTICLE_FRAME_DATA_INVERSE_PROJECTION_MATRIX");
                AssertFieldOffset<GPUParticleFrameData>(nameof(GPUParticleFrameData.CameraPosition), "OFFSET_GPU_PARTICLE_FRAME_DATA_CAMERA_POSITION");
                AssertFieldOffset<GPUParticleFrameData>(nameof(GPUParticleFrameData.ScreenDimensions), "OFFSET_GPU_PARTICLE_FRAME_DATA_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.CurrentFrameIndex), "OFFSET_GPU_PARTICLE_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.ParticleInstanceBufferBaseIndex), "OFFSET_GPU_PARTICLE_PUSH_INSTANCE_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.ParticleFrameDataBufferBaseIndex), "OFFSET_GPU_PARTICLE_PUSH_FRAME_DATA_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.DepthTextureIndex), "OFFSET_GPU_PARTICLE_PUSH_DEPTH_TEXTURE_INDEX");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.DebugView), "OFFSET_GPU_PARTICLE_PUSH_DEBUG_VIEW");
                AssertFieldOffset<GPUParticlePushConstants>(nameof(GPUParticlePushConstants.SoftParticlesEnabled), "OFFSET_GPU_PARTICLE_PUSH_SOFT_PARTICLES_ENABLED");
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

                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.OriginAndFirstProbeIndex), "OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.SizeAndProbeCountX), "OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.ProbeSpacingAndProbeCountY), "OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.BiasAndProbeCountZ), "OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.RayAndUpdateParams), "OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.DebugColorAndFlags), "OFFSET_GPU_DDGI_PROBE_VOLUME_DEBUG_COLOR_AND_FLAGS");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.VertexOffset), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_OFFSET");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.IndexOffset), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_OFFSET");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.MaterialIndex), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_MATERIAL_INDEX");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.WorldMatrixInverseTranspose), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_WORLD_MATRIX_INVERSE_TRANSPOSE");
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
