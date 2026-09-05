using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Njulf.Assets;
using Njulf.Core.Geometry;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
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
                ["SIZEOF_GPU_MESHLET"] = Marshal.SizeOf<GPUPackedMeshlet>(),
                ["SIZEOF_GPU_OBJECT_DATA"] = Marshal.SizeOf<GPUObjectData>(),
                ["SIZEOF_GPU_DEBUG_LINE_VERTEX"] = Marshal.SizeOf<GPUDebugLineVertex>(),
                ["SIZEOF_GPU_MATERIAL_DATA"] = Marshal.SizeOf<GPUMaterialData>(),
                ["SIZEOF_GPU_FORWARD_MATERIAL_DATA"] = Marshal.SizeOf<GPUForwardMaterialData>(),
                ["SIZEOF_GPU_MATERIAL_EXTENSION_DATA"] = Marshal.SizeOf<GPUMaterialExtensionData>(),
                ["SIZEOF_GPU_LIGHT"] = Marshal.SizeOf<GPULight>(),
                ["SIZEOF_GPU_SCENE_DATA"] = Marshal.SizeOf<GPUSceneData>(),
                ["SIZEOF_GPU_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUMeshletDrawCommand>(),
                ["SIZEOF_GPU_SCENE_INSTANCE_CANDIDATE"] = Marshal.SizeOf<GPUSceneInstanceCandidate>(),
                ["SIZEOF_GPU_SCENE_LOD_TRANSITION_STATE"] = Marshal.SizeOf<GPUSceneLodTransitionState>(),
                ["SIZEOF_GPU_PACKED_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUPackedMeshletDrawCommand>(),
                ["SIZEOF_GPU_MESHLET_TASK_FRAME_DATA"] = Marshal.SizeOf<GPUMeshletTaskFrameData>(),
                ["SIZEOF_GPU_FOLIAGE_PROTOTYPE"] = Marshal.SizeOf<GPUFoliagePrototype>(),
                ["SIZEOF_GPU_FOLIAGE_IMPOSTOR"] = Marshal.SizeOf<GPUFoliageImpostor>(),
                ["SIZEOF_GPU_FOLIAGE_IMPOSTOR_VIEW"] = Marshal.SizeOf<GPUFoliageImpostorView>(),
                ["SIZEOF_GPU_FOLIAGE_PATCH"] = Marshal.SizeOf<GPUFoliagePatch>(),
                ["SIZEOF_GPU_FOLIAGE_CLUSTER"] = Marshal.SizeOf<GPUFoliageCluster>(),
                ["SIZEOF_GPU_FOLIAGE_INSTANCE"] = Marshal.SizeOf<GPUFoliageInstance>(),
                ["SIZEOF_GPU_FOLIAGE_MESHLET_DRAW_COMMAND"] = Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(),
                ["SIZEOF_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND"] = Marshal.SizeOf<GPUFoliageProceduralDrawCommand>(),
                ["SIZEOF_GPU_FOLIAGE_AUTHORED_INSTANCE_COMMAND"] = Marshal.SizeOf<GPUFoliageAuthoredInstanceCommand>(),
                ["SIZEOF_GPU_FOLIAGE_COUNTERS"] = Marshal.SizeOf<GPUFoliageCounters>(),
                ["SIZEOF_GPU_FOLIAGE_DISPATCH_ARGS"] = Marshal.SizeOf<GPUFoliageDispatchArgs>(),
                ["SIZEOF_GPU_DDGI_FOLIAGE_PROXY_PATCH"] = Marshal.SizeOf<GPUDdgiFoliageProxyPatch>(),
                ["SIZEOF_GPU_DDGI_FOLIAGE_PROXY_GENERATION_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUDdgiFoliageProxyGenerationPushConstants>(),
                ["SIZEOF_GPU_SCENE_SUBMISSION_COUNTERS"] = Marshal.SizeOf<GPUSceneSubmissionCounters>(),
                ["SIZEOF_GPU_SCENE_OPAQUE_COMPACTION_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                ["SIZEOF_GPU_FORWARD_VISIBILITY_COMPACTION_PUSH_CONSTANTS"] = Marshal.SizeOf<GPUForwardVisibilityCompactionPushConstants>(),
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
                ["SIZEOF_GPU_DIRECTIONAL_SHADOW_PARAMETERS"] = Marshal.SizeOf<GPUDirectionalShadowParameters>(),
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
                ["SIZEOF_GPU_DDGI_EMISSIVE_SOURCE"] = Marshal.SizeOf<GPUDdgiEmissiveSource>(),
                ["SIZEOF_GPU_DDGI_EMISSIVE_SURFACE"] = Marshal.SizeOf<GPUDdgiEmissiveSurface>(),
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
                Assert.That(Marshal.SizeOf<GPUMeshInfo>(), Is.EqualTo(88));
                Assert.That(Marshal.SizeOf<GPUVertexSkinningData>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningDispatch>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSkinningPushConstants>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleInstance>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUParticleBatch>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleFrameData>(), Is.EqualTo(224));
                Assert.That(Marshal.SizeOf<GPUParticlePushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleEmitter>(), Is.EqualTo(288));
                Assert.That(Marshal.SizeOf<GPUVolumetricFogFrameData>(),
                    Is.EqualTo(512));
                Assert.That(Marshal.SizeOf<GPUVolumetricDensityVolume>(),
                    Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUVolumetricFogPushConstants>(),
                    Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUVolumetricFogDiagnostics>(),
                    Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUParticleCurveSample>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleState>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUParticleCounters>(), Is.EqualTo(88));
                Assert.That(Marshal.SizeOf<GPUParticleDrawCommand>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUParticleSortKey>(), Is.EqualTo(8));
                Assert.That(Marshal.SizeOf<GPUParticleResetPushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUParticleSimulatePushConstants>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUParticleSortPushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMeshlet>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUPackedMeshlet>(), Is.EqualTo(36));
                Assert.That(Marshal.SizeOf<GPUObjectData>(), Is.EqualTo(224));
                Assert.That(Marshal.OffsetOf<GPUObjectData>(
                    nameof(GPUObjectData.NearFieldStableObjectId)).ToInt32(),
                    Is.EqualTo(208));
                Assert.That(Marshal.OffsetOf<GPUObjectData>(
                    nameof(GPUObjectData.NearFieldCoverageMotionFlags)).ToInt32(),
                    Is.EqualTo(220));
                Assert.That(Marshal.SizeOf<GPUDebugLineVertex>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMaterialData>(), Is.EqualTo(320));
                Assert.That(Marshal.SizeOf<GPUForwardMaterialData>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUMaterialExtensionData>(), Is.EqualTo(548));
                Assert.That(Marshal.SizeOf<GPULight>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUSceneData>(), Is.EqualTo(400));
                Assert.That(Marshal.SizeOf<GPUMeshletDrawCommand>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUSceneInstanceCandidate>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUSceneLodTransitionState>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUPackedMeshletDrawCommand>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUMeshletTaskFrameData>(), Is.EqualTo(376));
                Assert.That(Marshal.SizeOf<GPUFoliagePrototype>(), Is.EqualTo(104));
                Assert.That(Marshal.SizeOf<GPUFoliageImpostor>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageImpostorView>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUFoliagePatch>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUFoliageCluster>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageInstance>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUFoliageMeshletDrawCommand>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUFoliageCounters>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUDdgiFoliageProxyPatch>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUDdgiFoliageProxyGenerationPushConstants>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUFoliageCullPushConstants>(), Is.EqualTo(88));
                Assert.That(Marshal.SizeOf<GPUFoliageDrawPushConstants>(), Is.EqualTo(132));
                Assert.That(Marshal.SizeOf<GPUTiledLightHeader>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPULightIndex>(), Is.EqualTo(4));
                Assert.That(Marshal.SizeOf<GPUScreenToViewParams>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPULightCullingParams>(), Is.EqualTo(192));
                Assert.That(Marshal.SizeOf<GPUDepthPushConstants>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(), Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPUMotionVectorPushConstants>(), Is.EqualTo(208));
                Assert.That(Marshal.SizeOf<GPULightCullPushConstants>(), Is.EqualTo(208));
                Assert.That(Marshal.SizeOf<GPUShadowData>(), Is.EqualTo(320));
                Assert.That(Marshal.SizeOf<GPUDirectionalShadowParameters>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUAreaRayShadowPushConstants>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUSpotShadow>(), Is.EqualTo(112));
                Assert.That(Marshal.SizeOf<GPUPointShadow>(), Is.EqualTo(432));
                Assert.That(Marshal.SizeOf<GPULocalLightShadowIndex>(), Is.EqualTo(16));
                Assert.That(Marshal.SizeOf<GPUReflectionProbeHeader>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUReflectionProbe>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolumeHeader>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolume>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeState>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeUpdateRequest>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeRelocationClassification>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUDdgiRayQueryInstance>(), Is.EqualTo(160));
                Assert.That(Marshal.SizeOf<GPUDdgiEmissiveSource>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUDdgiEmissiveSurface>(), Is.EqualTo(64));
                Assert.That(Marshal.SizeOf<GPUDdgiUpdatePushConstants>(), Is.EqualTo(148));
                Assert.That(Marshal.SizeOf<GPUFogPushConstants>(), Is.EqualTo(224));
                Assert.That(Marshal.SizeOf<GPUAntiAliasingPushConstants>(), Is.EqualTo(120));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionPushConstants>(), Is.EqualTo(176));
                Assert.That(Marshal.SizeOf<GPUAmbientOcclusionBlurPushConstants>(), Is.EqualTo(96));
            });
        }

        [Test]
        public void GPUMaterialData_V2HasExactMeasuredOffsets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<GPUMaterialData>(), Is.EqualTo(320));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.Albedo)).ToInt32(), Is.EqualTo(0));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.Emissive)).ToInt32(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.NormalScaleBias)).ToInt32(), Is.EqualTo(32));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.MetallicRoughnessAO)).ToInt32(), Is.EqualTo(48));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.BaseColorOffsetScale)).ToInt32(), Is.EqualTo(64));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.NormalOffsetScale)).ToInt32(), Is.EqualTo(80));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.MetallicRoughnessOffsetScale)).ToInt32(), Is.EqualTo(96));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.OcclusionOffsetScale)).ToInt32(), Is.EqualTo(112));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.EmissiveOffsetScale)).ToInt32(), Is.EqualTo(128));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TextureRotations)).ToInt32(), Is.EqualTo(144));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TextureTexCoordSets)).ToInt32(), Is.EqualTo(160));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.OcclusionBinding)).ToInt32(), Is.EqualTo(176));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.AlbedoTextureIndex)).ToInt32(), Is.EqualTo(192));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.OcclusionTextureIndex)).ToInt32(), Is.EqualTo(204));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TransportFlags)).ToInt32(), Is.EqualTo(220));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TransportProfileRevision)).ToInt32(), Is.EqualTo(224));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.PackedMeanMetallicRoughness)).ToInt32(), Is.EqualTo(228));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TransportProfileQuality)).ToInt32(), Is.EqualTo(232));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.MaterialRevision)).ToInt32(), Is.EqualTo(236));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.TextureContentRevision)).ToInt32(), Is.EqualTo(240));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.PackedMeanGiDirectionalDiffuseBaseRg)).ToInt32(), Is.EqualTo(244));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.PackedMeanGiDirectionalDiffuseBaseBAndF0R)).ToInt32(), Is.EqualTo(248));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.PackedMeanGiDielectricF0Gb)).ToInt32(), Is.EqualTo(252));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.DdgiAverageAlbedo)).ToInt32(), Is.EqualTo(256));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.DdgiAverageEmissive)).ToInt32(), Is.EqualTo(272));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.DdgiAverageTransmission)).ToInt32(), Is.EqualTo(288));
                Assert.That(Marshal.OffsetOf<GPUMaterialData>(nameof(GPUMaterialData.DdgiMaterialPolicy)).ToInt32(), Is.EqualTo(304));
            });
        }

        [Test]
        public void DdgiRadianceStructs_MatchShaderLayoutAnchors()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolumeHeader>(), Is.EqualTo(ReadShaderIntConstant("SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER")));
                Assert.That(Marshal.SizeOf<GPUDdgiProbeVolume>(), Is.EqualTo(ReadShaderIntConstant("SIZEOF_GPU_DDGI_PROBE_VOLUME")));
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.RayAndUpdateParams), "OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS");
                Assert.That(Marshal.OffsetOf<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.RayAndUpdateParams)).ToInt32() / sizeof(uint), Is.EqualTo(16));
            });
        }

        [Test]
        public void ForwardPushConstants_PackTwoDirectionalIndicesWithoutGrowingAbi()
        {
            uint packed = GPUForwardPushConstants.PackLightDispatch(
                totalLightCount: 1024,
                localLightCount: 1022,
                directionalLightIndex0: 1023,
                directionalLightIndex1: 17);

            Assert.Multiple(() =>
            {
                Assert.That(
                    GPUForwardPushConstants.UnpackTotalLightCount(packed),
                    Is.EqualTo(1024));
                Assert.That(
                    GPUForwardPushConstants.UnpackDirectionalLightIndex(packed, 0),
                    Is.EqualTo(1023));
                Assert.That(
                    GPUForwardPushConstants.UnpackDirectionalLightIndex(packed, 1),
                    Is.EqualTo(17));
                Assert.That(packed & 0x8000_0000u, Is.Zero);
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(), Is.EqualTo(256));
                Assert.That(
                    () => GPUForwardPushConstants.PackLightDispatch(
                        3, 0, 0, 1),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => GPUForwardPushConstants.PackLightDispatch(
                        2, 0, 1, 1),
                    Throws.TypeOf<ArgumentException>());
            });
        }

        [Test]
        public void GPUForwardMaterialData_HasCompactPackedLayout()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    Marshal.SizeOf<GPUForwardMaterialData>(),
                    Is.EqualTo(112));
                Assert.That(
                    Marshal.OffsetOf<GPUForwardMaterialData>(
                        nameof(GPUForwardMaterialData.AlbedoTextureIndex))
                        .ToInt32(),
                    Is.EqualTo(64));
                Assert.That(
                    Marshal.OffsetOf<GPUForwardMaterialData>(
                        nameof(GPUForwardMaterialData.EmissiveTextureIndex))
                        .ToInt32(),
                    Is.EqualTo(80));
                Assert.That(
                    Marshal.OffsetOf<GPUForwardMaterialData>(
                        nameof(GPUForwardMaterialData.PackedUvSets))
                        .ToInt32(),
                    Is.EqualTo(96));
            });

            GPUMaterialData material = MaterialManager.CreateDefaultMaterial();
            GPUForwardMaterialData packed =
                GPUForwardMaterialData.FromMaterial(material);
            Assert.That(packed.IdentityTransformMask, Is.EqualTo(0x1fu));

            material.TextureTexCoordSets =
                new Njulf.Core.Math.Vector4(1f, 2f, 3f, 4f);
            material.OcclusionBinding.Y = 5f;
            material.OcclusionBinding.Z =
                (float)MaterialBlendMode.PremultipliedAlpha;
            material.TextureRotations.X = 0.25f;
            packed = GPUForwardMaterialData.FromMaterial(material);
            uint expectedUvSets =
                1u | (2u << 4) | (3u << 8) | (4u << 12) |
                (5u << 16) |
                ((uint)MaterialBlendMode.PremultipliedAlpha <<
                 GPUForwardMaterialData.BlendModeShift);
            Assert.Multiple(() =>
            {
                Assert.That(packed.PackedUvSets, Is.EqualTo(expectedUvSets));
                Assert.That(
                    (packed.PackedUvSets >>
                     GPUForwardMaterialData.BlendModeShift) &
                    GPUForwardMaterialData.BlendModeMask,
                    Is.EqualTo((uint)MaterialBlendMode.PremultipliedAlpha));
                Assert.That(
                    packed.IdentityTransformMask & 1u,
                    Is.Zero);
                Assert.That(
                    packed.IdentityTransformMask & 0x1eu,
                    Is.EqualTo(0x1eu));
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
        public void ForwardPushConstants_PackDiagnosticFlags()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(false), Is.EqualTo(0u));
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(true) & 1u, Is.EqualTo(1u));
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(false, true) & 2u, Is.EqualTo(2u));
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(true, true), Is.EqualTo(3u));
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(false, false, true) & 4u, Is.EqualTo(4u));
                Assert.That(GPUForwardPushConstants.PackDiagnosticFlags(true, true, true), Is.EqualTo(7u));
                Assert.That(
                    (GPUForwardPushConstants.PackDiagnosticFlags(false, false, false, 3u) >> 8) & 0x03u,
                    Is.EqualTo(3u));
                Assert.That(
                    (GPUForwardPushConstants.PackDiagnosticFlags(true, true, true, 5u) >> 8) & 0x03u,
                    Is.EqualTo(1u));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        false,
                        false,
                        materialTransportProvenanceEnabled: true) & 8u,
                    Is.EqualTo(8u));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        true,
                        true,
                        true,
                        3u,
                        materialTransportProvenanceEnabled: true),
                    Is.EqualTo(0x30fu));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        decalGlobalIlluminationEnabled: true) & 16u,
                    Is.EqualTo(16u));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        ddgiLayeredReceiverCountersEnabled: true) & 32u,
                    Is.EqualTo(32u));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        decalReceiveShadows: true) & 64u,
                    Is.EqualTo(64u));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        ddgiReceiverCacheEnabled: true) & (1u << 30),
                    Is.EqualTo(1u << 30));
                Assert.That(
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        ddgiReceiverCacheEnabled: true) & (1u << 31),
                    Is.Zero);
                uint transparentReflectionFlags =
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        false,
                        effectiveReflectionMode: ReflectionMode.HybridRayQuery,
                        transparentSampleReflections: true,
                        opaqueSceneColorSnapshotAvailable: true);
                Assert.That((transparentReflectionFlags >> 11) & 0x07u,
                    Is.EqualTo((uint)ReflectionMode.HybridRayQuery));
                Assert.That(transparentReflectionFlags & (1u << 14),
                    Is.EqualTo(1u << 14));
                Assert.That(transparentReflectionFlags & (1u << 15),
                    Is.EqualTo(1u << 15));
                Assert.That(
                    Marshal.SizeOf<GPUSimpleDdgiReceiverCachePushConstants>(),
                    Is.EqualTo(132));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCachePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCachePushConstants.FeedbackControlOffsetWords)).ToInt32(),
                    Is.EqualTo(112));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCachePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCachePushConstants.SurfaceBufferIndex)).ToInt32(),
                    Is.EqualTo(128));
                Assert.That(
                    Marshal.SizeOf<GPUSimpleDdgiReceiverCacheResolvePushConstants>(),
                    Is.EqualTo(124));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCacheResolvePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCacheResolvePushConstants.GatherBufferIndex)).ToInt32(),
                    Is.EqualTo(104));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCacheResolvePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCacheResolvePushConstants.GatherSurfaceBufferIndex)).ToInt32(),
                    Is.EqualTo(108));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCacheResolvePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCacheResolvePushConstants.DepthTextureIndex)).ToInt32(),
                    Is.EqualTo(116));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCacheResolvePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCacheResolvePushConstants.CurrentFrameIndex)).ToInt32(),
                    Is.EqualTo(120));
                Assert.That(
                    Marshal.SizeOf<GPUSimpleDdgiReceiverCacheLegacyResolvePushConstants>(),
                    Is.EqualTo(28));
                Assert.That(
                    Marshal.OffsetOf<GPUSimpleDdgiReceiverCacheLegacyResolvePushConstants>(
                        nameof(GPUSimpleDdgiReceiverCacheLegacyResolvePushConstants.DepthTextureIndex)).ToInt32(),
                    Is.EqualTo(24));
            });
        }

        [Test]
        public void ReceiverFeedbackCaptureFlags_PreserveLayerAndPushConstantAbi()
        {
            var forward = new GPUForwardPushConstants
            {
                DiagnosticFlags =
                    GPUForwardPushConstants.PackDiagnosticFlags(
                        true,
                        true,
                        true,
                        2u,
                        ddgiReceiverCacheEnabled: true)
            };
            forward.CaptureFlags = GPUForwardPushConstants.PackCaptureFlags(
                reflectionCaptureEnabled: true,
                reflectionCaptureLayer: 731);

            uint foliageFlags = GPUFoliageDrawPushConstants.PackFlags(
                materialTransportProvenanceEnabled: true,
                reflectionFeedbackEnabled: true,
                reflectionCaptureLayer: 731,
                reflectionCaptureEnabled: true);

            Assert.Multiple(() =>
            {
                Assert.That(forward.ReflectionCaptureEnabled, Is.True);
                Assert.That(forward.ReflectionCaptureLayer, Is.EqualTo(731u));
                Assert.That(forward.DiagnosticFlags & 0x307u,
                    Is.EqualTo(0x207u));
                Assert.That(forward.DiagnosticFlags & (1u << 30),
                    Is.EqualTo(1u << 30));
                Assert.That((foliageFlags >> 8) & 0x1fffu,
                    Is.EqualTo(731u));
                Assert.That(foliageFlags & (1u << 3),
                    Is.EqualTo(1u << 3));
                Assert.That(foliageFlags & (1u << 4),
                    Is.EqualTo(1u << 4));
                Assert.That(foliageFlags & (1u << 2),
                    Is.EqualTo(1u << 2));
                Assert.That(Marshal.SizeOf<GPUForwardPushConstants>(),
                    Is.EqualTo(256));
                Assert.That(Marshal.SizeOf<GPUFoliageDrawPushConstants>(),
                    Is.EqualTo(132));
                Assert.That(
                    () => GPUForwardPushConstants.PackCaptureFlags(
                        true,
                        GPUForwardPushConstants.MaximumReflectionCaptureLayer + 1),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => GPUFoliageDrawPushConstants.PackFlags(
                        false,
                        true,
                        -1),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
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
                typeof(GPUFoliageImpostor),
                typeof(GPUFoliageImpostorView),
                typeof(GPUFoliagePatch),
                typeof(GPUFoliageCluster),
                typeof(GPUFoliageInstance),
                typeof(GPUFoliageMeshletDrawCommand),
                typeof(GPUFoliageProceduralDrawCommand),
                typeof(GPUFoliageAuthoredInstanceCommand),
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
                typeof(GPUDirectionalShadowParameters),
                typeof(GPUDirectionalRayShadowPushConstants),
                typeof(GPUAreaRayShadowPushConstants),
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
                typeof(GPUDdgiEmissiveSource),
                typeof(GPUDdgiEmissiveSurface),
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
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.NormalConeAxis), "OFFSET_GPU_MESHLET_NORMAL_CONE_AXIS");
                AssertFieldOffset<GPUMeshlet>(nameof(GPUMeshlet.NormalConeCutoff), "OFFSET_GPU_MESHLET_NORMAL_CONE_CUTOFF");
            });
        }

        [Test]
        public void GPUPackedMeshletTaskStructs_HaveCorrectFieldOffsets()
        {
            Assert.Multiple(() =>
            {
                AssertFieldOffset<GPUMeshletDrawCommand>(nameof(GPUMeshletDrawCommand.MeshletIndex), "OFFSET_GPU_MESHLET_DRAW_COMMAND_MESHLET_INDEX");
                AssertFieldOffset<GPUMeshletDrawCommand>(nameof(GPUMeshletDrawCommand.InstanceId), "OFFSET_GPU_MESHLET_DRAW_COMMAND_INSTANCE_ID");
                AssertFieldOffset<GPUMeshletDrawCommand>(nameof(GPUMeshletDrawCommand.MaterialIndex), "OFFSET_GPU_MESHLET_DRAW_COMMAND_MATERIAL_INDEX");
                AssertFieldOffset<GPUMeshletDrawCommand>(nameof(GPUMeshletDrawCommand.Flags), "OFFSET_GPU_MESHLET_DRAW_COMMAND_FLAGS");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.MeshletIndex), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MESHLET_INDEX");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.InstanceId), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_INSTANCE_ID");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.MaterialIndex), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MATERIAL_INDEX");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.Flags), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_FLAGS");
                AssertFieldOffset<GPUPackedMeshletDrawCommand>(nameof(GPUPackedMeshletDrawCommand.WorldCenterRadius), "OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.FrustumPlane0), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE0");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.FrustumPlane5), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE5");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.ViewProjectionMatrix), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.InverseViewMatrix), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_INVERSE_VIEW_MATRIX");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.PreviousHiZViewProjectionMatrix), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.PreviousHiZInverseViewMatrix), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_INVERSE_VIEW_MATRIX");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.ScreenDimensions), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUMeshletTaskFrameData>(nameof(GPUMeshletTaskFrameData.PreviousHiZFrameValid), "OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_FRAME_VALID");
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
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.ImpostorMetadataIndex), "OFFSET_GPU_FOLIAGE_PROTOTYPE_IMPOSTOR_METADATA_INDEX");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.MeshletOutputClass), "OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_OUTPUT_CLASS");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.BladeHeight), "OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_HEIGHT");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.BladeWidth), "OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_WIDTH");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.LodDistances), "OFFSET_GPU_FOLIAGE_PROTOTYPE_LOD_DISTANCES");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.WindParams), "OFFSET_GPU_FOLIAGE_PROTOTYPE_WIND_PARAMS");
                AssertFieldOffset<GPUFoliagePrototype>(nameof(GPUFoliagePrototype.LightingParams), "OFFSET_GPU_FOLIAGE_PROTOTYPE_LIGHTING_PARAMS");

                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.AlbedoOpacityTextureIndex), "OFFSET_GPU_FOLIAGE_IMPOSTOR_ALBEDO_OPACITY_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.NormalTextureIndex), "OFFSET_GPU_FOLIAGE_IMPOSTOR_NORMAL_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.DepthTextureIndex), "OFFSET_GPU_FOLIAGE_IMPOSTOR_DEPTH_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.ViewCount), "OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_COUNT");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.SourceBoundsMinScale), "OFFSET_GPU_FOLIAGE_IMPOSTOR_SOURCE_BOUNDS_MIN_SCALE");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.SourceBoundsMax), "OFFSET_GPU_FOLIAGE_IMPOSTOR_SOURCE_BOUNDS_MAX");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.Pivot), "OFFSET_GPU_FOLIAGE_IMPOSTOR_PIVOT");
                AssertFieldOffset<GPUFoliageImpostor>(nameof(GPUFoliageImpostor.ViewDataOffset), "OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_DATA_OFFSET");

                AssertFieldOffset<GPUFoliageImpostorView>(nameof(GPUFoliageImpostorView.Direction), "OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_DIRECTION");
                AssertFieldOffset<GPUFoliageImpostorView>(nameof(GPUFoliageImpostorView.AtlasRectangle), "OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_ATLAS_RECTANGLE");

                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.BoundsMinDensity), "OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MIN_DENSITY");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.BoundsMaxSeed), "OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MAX_SEED");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.PrototypeIndex), "OFFSET_GPU_FOLIAGE_PATCH_PROTOTYPE_INDEX");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.ClusterOffset), "OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_OFFSET");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.ClusterCount), "OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_COUNT");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.NearFieldStableObjectId), "OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_STABLE_OBJECT_ID");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.Seed), "OFFSET_GPU_FOLIAGE_PATCH_SEED");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.Flags), "OFFSET_GPU_FOLIAGE_PATCH_FLAGS");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.NearFieldStableMaterialId), "OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_STABLE_MATERIAL_ID");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.NearFieldPackedObjectMaterialRevisions), "OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_PACKED_OBJECT_MATERIAL_REVISIONS");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.DensityTextureIndex), "OFFSET_GPU_FOLIAGE_PATCH_DENSITY_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.TerrainDescriptorIndex), "OFFSET_GPU_FOLIAGE_PATCH_TERRAIN_DESCRIPTOR_INDEX");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.PlacementMode), "OFFSET_GPU_FOLIAGE_PATCH_PLACEMENT_MODE");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.ContentRevision), "OFFSET_GPU_FOLIAGE_PATCH_CONTENT_REVISION");
                AssertFieldOffset<GPUFoliagePatch>(nameof(GPUFoliagePatch.DensityUvScaleOffset), "OFFSET_GPU_FOLIAGE_PATCH_DENSITY_UV_SCALE_OFFSET");

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

                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.ClusterIndex), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_CLUSTER_INDEX");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.LodBand), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_LOD_BAND");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.CandidateCount), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_CANDIDATE_COUNT");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.ActiveCount), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_ACTIVE_COUNT");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.DensityFraction), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_DENSITY_FRACTION");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.TransitionFraction), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_TRANSITION_FRACTION");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.WidthCompensation), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_WIDTH_COMPENSATION");
                AssertFieldOffset<GPUFoliageProceduralDrawCommand>(nameof(GPUFoliageProceduralDrawCommand.Flags), "OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_FLAGS");

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
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.DensityRejectedCount), "OFFSET_GPU_FOLIAGE_COUNTERS_DENSITY_REJECTED_COUNT");
                AssertFieldOffset<GPUFoliageCounters>(nameof(GPUFoliageCounters.InvalidCommandCount), "OFFSET_GPU_FOLIAGE_COUNTERS_INVALID_COMMAND_COUNT");

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
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.ScreenDimensions), "OFFSET_GPU_FOLIAGE_CULL_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.HiZTextureIndex), "OFFSET_GPU_FOLIAGE_CULL_PUSH_HIZ_TEXTURE_INDEX");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.HiZMipCount), "OFFSET_GPU_FOLIAGE_CULL_PUSH_HIZ_MIP_COUNT");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.OcclusionCullingEnabled), "OFFSET_GPU_FOLIAGE_CULL_PUSH_OCCLUSION_CULLING_ENABLED");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.OcclusionBias), "OFFSET_GPU_FOLIAGE_CULL_PUSH_OCCLUSION_BIAS");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.PreviousHiZFrameValid), "OFFSET_GPU_FOLIAGE_CULL_PUSH_PREVIOUS_HIZ_FRAME_VALID");
                AssertFieldOffset<GPUFoliageCullPushConstants>(nameof(GPUFoliageCullPushConstants.PreviousFrameUvPaddingPixels), "OFFSET_GPU_FOLIAGE_CULL_PUSH_PREVIOUS_FRAME_UV_PADDING_PIXELS");

                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ViewProjectionMatrix), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.CameraPositionTime), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CAMERA_POSITION_TIME");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ScreenDimensions), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.CurrentFrameIndex), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ClusterDrawCount), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_CLUSTER_DRAW_COUNT");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.VisibleClusterBufferBaseIndex), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_VISIBLE_CLUSTER_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.Flags), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_FLAGS");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.DebugView), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_DEBUG_VIEW");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.ShadowDensityScale), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_SHADOW_DENSITY_SCALE");
                AssertFieldOffset<GPUFoliageDrawPushConstants>(nameof(GPUFoliageDrawPushConstants.FirstDraw), "OFFSET_GPU_FOLIAGE_DRAW_PUSH_FIRST_DRAW");
            });
        }

        [Test]
        public void SharedMeshlet_HasStableRendererLayout()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<Meshlet>(), Is.EqualTo(64));
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
                AssertFieldOffset<GPUParticleInstance>(
                    nameof(GPUParticleInstance.VolumetricAlbedoAndExtinction),
                    "OFFSET_GPU_PARTICLE_INSTANCE_VOLUMETRIC_ALBEDO");
                AssertFieldOffset<GPUParticleInstance>(
                    nameof(GPUParticleInstance.VolumetricRadiusAnisotropyAndFlags),
                    "OFFSET_GPU_PARTICLE_INSTANCE_VOLUMETRIC_RADIUS");
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
                AssertFieldOffset<GPUParticleEmitter>(
                    nameof(GPUParticleEmitter.VolumetricAlbedoAndExtinction),
                    "OFFSET_GPU_PARTICLE_EMITTER_VOLUMETRIC_ALBEDO");
                AssertFieldOffset<GPUParticleEmitter>(
                    nameof(GPUParticleEmitter.VolumetricRadiusAnisotropyAndFlags),
                    "OFFSET_GPU_PARTICLE_EMITTER_VOLUMETRIC_RADIUS");
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
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.HiZMipCount), "OFFSET_GPU_FORWARD_PUSH_HIZ_MIP_COUNT");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.OcclusionCullingEnabled), "OFFSET_GPU_FORWARD_PUSH_OCCLUSION_CULLING_ENABLED");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.OcclusionBias), "OFFSET_GPU_FORWARD_PUSH_OCCLUSION_BIAS");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.DebugAndAoFlags), "OFFSET_GPU_FORWARD_PUSH_DEBUG_AND_AO_FLAGS");
                AssertFieldOffset<GPUForwardPushConstants>(nameof(GPUForwardPushConstants.DiagnosticFlags), "OFFSET_GPU_FORWARD_PUSH_DIAGNOSTIC_FLAGS");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.ViewProjectionMatrix), "OFFSET_GPU_MOTION_VECTOR_PUSH_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousViewProjectionMatrix), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_VIEW_PROJECTION_MATRIX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.ScreenDimensions), "OFFSET_GPU_MOTION_VECTOR_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.CurrentFrameIndex), "OFFSET_GPU_MOTION_VECTOR_PUSH_CURRENT_FRAME_INDEX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.MeshletDrawCount), "OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_COUNT");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.MeshletDrawBufferBaseIndex), "OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousFrameValid), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_FRAME_VALID");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.Time), "OFFSET_GPU_MOTION_VECTOR_PUSH_TIME");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousTime), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_TIME");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.FirstDraw), "OFFSET_GPU_MOTION_VECTOR_PUSH_FIRST_DRAW");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.CameraPosition), "OFFSET_GPU_MOTION_VECTOR_PUSH_CAMERA_POSITION");
                AssertFieldOffset<GPUMotionVectorPushConstants>(nameof(GPUMotionVectorPushConstants.PreviousCameraPosition), "OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_CAMERA_POSITION");
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
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.CameraForward), "OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_FORWARD");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.ScreenDimensions), "OFFSET_GPU_LIGHT_CULL_PUSH_SCREEN_DIMENSIONS");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.NearPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_NEAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.FarPlane), "OFFSET_GPU_LIGHT_CULL_PUSH_FAR_PLANE");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.LightCount), "OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_COUNT");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.TileCountY), "OFFSET_GPU_LIGHT_CULL_PUSH_TILE_COUNT_Y");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.TotalClusterCount), "OFFSET_GPU_LIGHT_CULL_PUSH_TOTAL_CLUSTER_COUNT");
                AssertFieldOffset<GPULightCullPushConstants>(nameof(GPULightCullPushConstants.LightIndexCapacity), "OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_INDEX_CAPACITY");

                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection0), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection1), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION1");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection2), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION2");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.LightViewProjection3), "OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION3");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.CascadeSplits), "OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.Settings), "OFFSET_GPU_SHADOW_DATA_SETTINGS");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.Indices), "OFFSET_GPU_SHADOW_DATA_INDICES");
                AssertFieldOffset<GPUShadowData>(nameof(GPUShadowData.CascadeTransitionData), "OFFSET_GPU_SHADOW_DATA_CASCADE_TRANSITION_DATA");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.CascadeWorldTexelSizes), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_CASCADE_WORLD_TEXEL_SIZES");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.FilterAndBias), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_FILTER_AND_BIAS");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.ModeAndRayDistance), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_MODE_AND_RAY_DISTANCE");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.TemporalAndSampling), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_TEMPORAL_AND_SAMPLING");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.RaySceneBoundsMinimum), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MINIMUM");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.RaySceneBoundsMaximum), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MAXIMUM");
                AssertFieldOffset<GPUDirectionalShadowParameters>(nameof(GPUDirectionalShadowParameters.RuntimeFlags), "OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RUNTIME_FLAGS");

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
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.ClipmapGridMinAndKind), "OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.ClipmapRingOffsetAndCascade), "OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE");
                AssertFieldOffset<GPUDdgiProbeVolume>(nameof(GPUDdgiProbeVolume.ClipmapBlendAndFlags), "OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_BLEND_AND_FLAGS");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.Irradiance), "OFFSET_GPU_DDGI_PROBE_STATE_IRRADIANCE");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.Visibility), "OFFSET_GPU_DDGI_PROBE_STATE_VISIBILITY");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.RelocationAndClassification), "OFFSET_GPU_DDGI_PROBE_STATE_RELOCATION_AND_CLASSIFICATION");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.QualityAndReason), "OFFSET_GPU_DDGI_PROBE_STATE_QUALITY_AND_REASON");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.UpdateMetadata), "OFFSET_GPU_DDGI_PROBE_STATE_UPDATE_METADATA");
                AssertFieldOffset<GPUDdgiProbeState>(nameof(GPUDdgiProbeState.RepresentationMetadata), "OFFSET_GPU_DDGI_PROBE_STATE_REPRESENTATION_METADATA");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.ProbeIndex), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PROBE_INDEX");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.VolumeIndex), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_VOLUME_INDEX");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.Flags), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FLAGS");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.Priority), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PRIORITY");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.LogicalCellX), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_X");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.LogicalCellY), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Y");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.LogicalCellZ), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Z");
                AssertFieldOffset<GPUDdgiProbeUpdateRequest>(nameof(GPUDdgiProbeUpdateRequest.RequestFrameSerial), "OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FRAME_SERIAL");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.AbiVersion), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_ABI_VERSION");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.GeometryClass), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_GEOMETRY_CLASS");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.GeometryFlags), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_GEOMETRY_FLAGS");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.StableInstanceIdentity), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_STABLE_INSTANCE_IDENTITY");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.VertexBufferIndex), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_BUFFER_INDEX");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.VertexOffset), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_OFFSET");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.VertexStride), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_STRIDE");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.VertexFormat), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_FORMAT");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.IndexBufferIndex), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_BUFFER_INDEX");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.IndexOffset), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_OFFSET");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.MaterialIndex), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_MATERIAL_INDEX");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.RepresentationGeneration), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_REPRESENTATION_GENERATION");
                AssertFieldOffset<GPUDdgiRayQueryInstance>(nameof(GPUDdgiRayQueryInstance.WorldMatrixInverseTranspose), "OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_WORLD_MATRIX_INVERSE_TRANSPOSE");
            });
        }

        [Test]
        public void SceneSubmissionDirectionalShadowCounterConstants_MatchHostLayout()
        {
            int staticBase = FieldWordOffset<GPUSceneSubmissionCounters>(
                nameof(GPUSceneSubmissionCounters.DirectionalStaticShadowCascade0CandidateCount));
            int dynamicBase = FieldWordOffset<GPUSceneSubmissionCounters>(
                nameof(GPUSceneSubmissionCounters.DirectionalDynamicShadowCascade0CandidateCount));
            int emittedOffset = FieldWordOffset<GPUSceneSubmissionCounters>(
                nameof(GPUSceneSubmissionCounters.DirectionalStaticShadowCascade0EmittedCount)) - staticBase;
            int stride = FieldWordOffset<GPUSceneSubmissionCounters>(
                nameof(GPUSceneSubmissionCounters.DirectionalStaticShadowCascade1CandidateCount)) - staticBase;

            Assert.Multiple(() =>
            {
                Assert.That(ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_STATIC_SHADOW_BASE"), Is.EqualTo(staticBase));
                Assert.That(ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_DYNAMIC_SHADOW_BASE"), Is.EqualTo(dynamicBase));
                Assert.That(ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_SHADOW_STRIDE"), Is.EqualTo(stride));
                Assert.That(ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_SHADOW_EMITTED_OFFSET"), Is.EqualTo(emittedOffset));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_SHADOW_LOD_FALLBACK"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.DirectionalShadowLodFallbackCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_OPAQUE_LOD_DECIMATED"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.OpaqueLodDecimatedCount))));
                Assert.That(ReadShaderFile("common.glsl"), Does.Contain("uint OpaqueLodDecimatedCount;"));

                Assert.That(ReadShaderUIntConstant("shadow_depth.task", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_STATIC_SHADOW_BASE"), Is.EqualTo(staticBase));
                Assert.That(ReadShaderUIntConstant("shadow_depth.task", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_DYNAMIC_SHADOW_BASE"), Is.EqualTo(dynamicBase));
                Assert.That(ReadShaderUIntConstant("shadow_depth.task", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_SHADOW_STRIDE"), Is.EqualTo(stride));
                Assert.That(ReadShaderUIntConstant("shadow_depth.task", "SCENE_SUBMISSION_COUNTER_DIRECTIONAL_SHADOW_EMITTED_OFFSET"), Is.EqualTo(emittedOffset));
            });
        }

        [Test]
        public void SceneSubmissionOpaqueBucketCounterConstants_MatchHostLayout()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_OPAQUE_APPEND"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleOpaqueAppendCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_OPAQUE_EMITTED"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleOpaqueEmittedCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_OPAQUE_OVERFLOW"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleOpaqueOverflowCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_NORMAL_OPAQUE_APPEND"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleNormalOpaqueAppendCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_NORMAL_OPAQUE_EMITTED"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleNormalOpaqueEmittedCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_SIMPLE_NORMAL_OPAQUE_OVERFLOW"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.SimpleNormalOpaqueOverflowCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_FULL_OPAQUE_APPEND"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.FullOpaqueAppendCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_FULL_OPAQUE_EMITTED"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.FullOpaqueEmittedCount))));
                Assert.That(
                    ReadShaderUIntConstant("scene_opaque_compact.comp", "SCENE_SUBMISSION_COUNTER_FULL_OPAQUE_OVERFLOW"),
                    Is.EqualTo(FieldWordOffset<GPUSceneSubmissionCounters>(nameof(GPUSceneSubmissionCounters.FullOpaqueOverflowCount))));
            });
        }

        [Test]
        public void PackedGpuMeshlet_PreservesRangesAndWidensNormalCone()
        {
            var axis = new Njulf.Core.Math.Vector3(0.3f, -0.4f, 0.8660254f)
                .Normalized();
            var meshlet = new Meshlet(
                new Njulf.Core.Math.Vector3(2f, -3f, 4f),
                5f,
                vertexOffset: 101,
                vertexCount: 48,
                indexOffset: 303,
                indexCount: 96,
                localVertexOffset: 707,
                localVertexCount: 48,
                localTriangleOffset: 909,
                localTriangleCount: 32,
                normalConeAxis: axis,
                normalConeCutoff: 0.75f);

            GPUPackedMeshlet packed = GPUPackedMeshlet.Pack(meshlet);
            packed.UnpackNormalCone(out Njulf.Core.Math.Vector3 decodedAxis,
                out float decodedCutoff);

            Assert.Multiple(() =>
            {
                Assert.That(GPUPackedMeshlet.AbiVersion, Is.EqualTo(2u));
                Assert.That(
                    ReadShaderIntConstant("GPU_MESHLET_ABI_VERSION"),
                    Is.EqualTo((int)GPUPackedMeshlet.AbiVersion));
                Assert.That(packed.BoundingSphere.X, Is.EqualTo(2f));
                Assert.That(packed.BoundingSphere.W, Is.EqualTo(5f));
                Assert.That(packed.VertexOffset, Is.EqualTo(101u));
                Assert.That(packed.LocalVertexOffset, Is.EqualTo(707u));
                Assert.That(packed.LocalTriangleOffset, Is.EqualTo(909u));
                Assert.That(packed.LocalVertexCount, Is.EqualTo(48u));
                Assert.That(packed.LocalTriangleCount, Is.EqualTo(32u));
                Assert.That(
                    Njulf.Core.Math.Vector3.Dot(axis, decodedAxis),
                    Is.GreaterThan(0.999f));
                Assert.That(decodedCutoff, Is.LessThanOrEqualTo(0.75f));
                Assert.That(decodedCutoff, Is.GreaterThan(0.73f));
            });
        }

        [Test]
        public void PackedGpuMeshlet_HierarchyRecordUsesVersionedSharedStride()
        {
            var meshlet = new Meshlet(
                Njulf.Core.Math.Vector3.Zero,
                2f,
                0,
                3,
                0,
                3,
                0,
                3,
                0,
                1);
            var node = new MeshletHierarchyNode
            {
                BoundingSphereCenter =
                    new Njulf.Core.Math.Vector3(1f, 2f, 3f),
                BoundingSphereRadius = 4f,
                GeometricError = 0.25f,
                FirstChild = uint.MaxValue,
                MeshletOffset = 0,
                MeshletCount = 1,
                ParentIndex = uint.MaxValue,
                Flags = MeshletHierarchyNodeFlags.Leaf
            };
            var meshInfo = new MeshInfo
            {
                MeshletOffset = 100,
                MeshletLodGeneratedCount = 1,
                GpuMeshletRecordCount = 2,
                HierarchyNodeOffset = 101,
                HierarchyNodeCount = 1,
                HierarchyRootNode = 101
            };

            GPUPackedMeshlet[] records = MeshManager.PackGpuMeshlets(
                [meshlet],
                [node],
                meshInfo);
            GPUPackedMeshlet packedNode = records[1];
            Assert.Multiple(() =>
            {
                Assert.That(records, Has.Length.EqualTo(2));
                Assert.That(
                    Marshal.SizeOf<GPUPackedMeshlet>(),
                    Is.EqualTo(36));
                Assert.That(packedNode.BoundingSphere.X, Is.EqualTo(1f));
                Assert.That(packedNode.BoundingSphere.W, Is.EqualTo(4f));
                Assert.That(
                    BitConverter.UInt32BitsToSingle(
                        packedNode.VertexOffset),
                    Is.EqualTo(0.25f));
                Assert.That(
                    packedNode.LocalVertexOffset,
                    Is.EqualTo(uint.MaxValue));
                Assert.That(
                    packedNode.LocalTriangleOffset & (1u << 31),
                    Is.Not.Zero);
                Assert.That(
                    (packedNode.LocalTriangleOffset >> 8) & 0x3u,
                    Is.EqualTo((uint)MeshletHierarchyNodeFlags.Leaf));
                Assert.That(packedNode.PackedCounts, Is.EqualTo(100u));
                Assert.That(packedNode.PackedNormalCone, Is.EqualTo(1u));
            });
        }

        [Test]
        public void HierarchicalLodShader_UsesBoundedTraversalAndTemporalCuts()
        {
            string shader = ReadShaderFile("scene_opaque_compact.comp");
            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain(
                    "SCENE_OPAQUE_COMPACTION_FLAG_HIERARCHICAL_LOD"));
                Assert.That(shader, Does.Contain(
                    "MESHLET_HIERARCHY_STACK_CAPACITY = 96u"));
                Assert.That(shader, Does.Contain(
                    "ResolveHierarchyProjectionTransition("));
                Assert.That(shader, Does.Contain(
                    "ProcessHierarchicalInstance("));
                Assert.That(shader, Does.Contain(
                    "nodeWorldCenter = TransformRowMajorPoint("));
                Assert.That(shader, Does.Contain(
                    "nodeSurfaceDistance = max("));
                Assert.That(shader, Does.Contain(
                    "nodeErrorPixels >"));
                Assert.That(shader, Does.Contain(
                    "float temporalProjectionScale,"));
                Assert.That(shader, Does.Contain(
                    "HierarchyTraversalSelectedMeshletCount"));
                Assert.That(shader, Does.Contain(
                    "SCENE_SUBMISSION_COUNTER_OPAQUE_LOD_DECIMATED"));
                Assert.That(shader, Does.Contain(
                    "ReadMeshletHierarchyNode("));
                Assert.That(shader, Does.Contain(
                    "SCENE_SUBMISSION_COUNTER_HIERARCHY_TRAVERSAL_FALLBACK"));
            });
        }

        [Test]
        public void SceneSubmissionInstanceExpansion_UsesOneWorkgroupPerInstanceAndSubgroupReservations()
        {
            string shader = ReadShaderFile("scene_opaque_compact.comp");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain(
                    "ProcessInstanceCandidate(gl_WorkGroupID.x, frustumPlanes)"));
                Assert.That(shader, Does.Contain(
                    "subgroupBallotExclusiveBitCount"));
                Assert.That(shader, Does.Contain(
                    "SCENE_OPAQUE_COMPACTION_FLAG_INSTANCE_EXPANSION"));
                Assert.That(shader, Does.Contain(
                    "EmitExpandedDepthCommand(command, masked, visible)"));
                Assert.That(shader, Does.Contain(
                    "EmitExpandedOpaqueCommand("));
                Assert.That(shader, Does.Contain(
                    "ProcessExpandedDirectionalShadowRange("));
                Assert.That(shader, Does.Contain(
                    "SCENE_INSTANCE_CLASSIFICATION_CASTS_DIRECTIONAL_SHADOW"));
            });
        }

        [Test]
        public void SceneCompaction_AggregateValidationOutputIsOptionalAcrossFlatAndInstancePaths()
        {
            var flatProduction = new SceneRenderingData();
            var instanceValidation = new SceneRenderingData
            {
                SceneSubmissionGpuLodSelectionEnabled = true,
                SceneSubmissionSidedRasterSpecializationActive = true,
                SceneSubmissionGpuInstanceExpansionActive = true,
                SceneSubmissionGpuLodDitherTransitionsActive = true,
                SceneSubmissionGpuHierarchicalLodActive = true,
                SceneSubmissionValidationCompareCpuGpuLists = true
            };

            uint flatFlags = SceneOpaqueCompactionPass.BuildCompactionFlags(
                flatProduction,
                compactDirectionalShadows: false);
            uint validationFlags =
                SceneOpaqueCompactionPass.BuildCompactionFlags(
                    instanceValidation,
                    compactDirectionalShadows: true);
            string shader = ReadShaderFile("scene_opaque_compact.comp");
            string expandedPath = shader[
                shader.IndexOf("void EmitExpandedOpaqueCommand(",
                    StringComparison.Ordinal)..
                shader.IndexOf("void EmitExpandedDepthCommand(",
                    StringComparison.Ordinal)];
            string flatPath = shader[
                shader.IndexOf("void ProcessOpaqueCandidate(",
                    StringComparison.Ordinal)..
                shader.IndexOf("void ProcessDepthCandidate(",
                    StringComparison.Ordinal)];

            Assert.Multiple(() =>
            {
                Assert.That(
                    flatFlags & SceneOpaqueCompactionPass
                        .AggregateValidationOutputFlag,
                    Is.Zero);
                Assert.That(
                    validationFlags & SceneOpaqueCompactionPass
                        .AggregateValidationOutputFlag,
                    Is.Not.Zero);
                Assert.That(validationFlags & 0x7fu, Is.EqualTo(0x7fu),
                    "Instance, sided, hierarchy/LOD, shadow, and base flags must remain intact.");
                Assert.That(shader, Does.Contain(
                    "SCENE_OPAQUE_COMPACTION_FLAG_AGGREGATE_VALIDATION_OUTPUT = 1u << 7u"));
                Assert.That(expandedPath, Does.Contain(
                    "bool aggregateValidationOutput = AggregateValidationOutputEnabled();"));
                Assert.That(flatPath, Does.Contain(
                    "bool aggregateValidationOutput = AggregateValidationOutputEnabled();"));
                Assert.That(expandedPath, Does.Contain(
                    "OpaqueBucketAppendCounterWord(bucket, doubleSided)"));
                Assert.That(flatPath, Does.Contain(
                    "OpaqueBucketAppendCounterWord(bucket, doubleSided)"));
                Assert.That(shader, Does.Contain(
                    "SCENE_SUBMISSION_COUNTER_HIERARCHY_TRAVERSAL_FALLBACK"));
                Assert.That(shader, Does.Contain(
                    "SCENE_SUBMISSION_COUNTER_MISSING_LOD_FALLBACK"));
            });
        }

        [Test]
        public void MeshletLodTransitions_AreConsumedByEveryCompactedTrianglePath()
        {
            string[] shaders =
            {
                "depth.mesh",
                "depth_alpha.mesh",
                "forward.mesh",
                "forward_simple.mesh",
                "motion_vector.mesh",
                "motion_vector_alpha.mesh",
                "shadow_depth.mesh",
                "shadow_depth_alpha.mesh"
            };

            Assert.Multiple(() =>
            {
                foreach (string shaderName in shaders)
                {
                    Assert.That(
                        ReadShaderFile(shaderName),
                        Does.Contain("MeshletLodTransitionTriangleVisible("),
                        shaderName);
                }
            });
        }

        [Test]
        public void MeshletLodTransitions_PreserveSourceCoverageAcrossDifferentTopology()
        {
            string shader = ReadShaderFile("common.glsl");

            Assert.Multiple(() =>
            {
                Assert.That(shader, Does.Contain(
                    "if (!target)\n        return true;"));
                Assert.That(shader, Does.Contain(
                    "return hashSample <= threshold;"));
                Assert.That(shader, Does.Not.Contain(
                    "hashSample > threshold"));
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

        private static int ReadShaderUIntConstant(string shaderFileName, string name)
        {
            string source = ReadShaderFile(shaderFileName);
            var match = Regex.Match(
                source,
                $@"\bconst\s+uint\s+{Regex.Escape(name)}\s*=\s*(\d+)u\s*;");

            if (!match.Success)
                throw new AssertionException($"Shader constant '{name}' was not found in {shaderFileName}.");

            return int.Parse(match.Groups[1].Value);
        }

        private static int FieldWordOffset<T>(string fieldName)
            where T : struct
        {
            return Marshal.OffsetOf<T>(fieldName).ToInt32() / sizeof(uint);
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
            return ReadShaderFile("common.glsl");
        }

        private static string ReadShaderFile(string fileName)
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Njulf.Shaders", fileName);
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).ReplaceLineEndings("\n");

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not locate Njulf.Shaders/{fileName} from the test output directory.");
        }
    }
}
