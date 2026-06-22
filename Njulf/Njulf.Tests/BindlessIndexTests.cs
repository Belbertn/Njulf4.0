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
        private static readonly Lazy<string> BindlessHeapSource = new(ReadBindlessHeapSource);

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
                ["MATERIAL_EXTENSION_DATA_BUFFER_INDEX"] = BindlessIndex.MaterialExtensionDataBuffer,
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
                ["RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX"] = BindlessIndex.RendererDiagnosticsBufferBase,
                ["RENDERER_DIAGNOSTICS_BUFFER_FRAME1_INDEX"] = BindlessIndex.RendererDiagnosticsBufferFrame1,
                ["DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX"] = BindlessIndex.DirectionalShadowDataBuffer,
                ["DIRECTIONAL_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.DirectionalShadowMeshletDrawBufferBase,
                ["DIRECTIONAL_SHADOW_MESHLET_DRAW_BUFFER_COUNT"] = BindlessIndex.DirectionalShadowMeshletDrawBufferCount,
                ["SPOT_SHADOW_DATA_BUFFER_INDEX"] = BindlessIndex.SpotShadowDataBuffer,
                ["POINT_SHADOW_DATA_BUFFER_INDEX"] = BindlessIndex.PointShadowDataBuffer,
                ["LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX"] = BindlessIndex.LocalLightShadowIndexBuffer,
                ["LOCAL_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.LocalShadowMeshletDrawBufferBase,
                ["LOCAL_SHADOW_MESHLET_DRAW_BUFFER_COUNT"] = BindlessIndex.LocalShadowMeshletDrawBufferCount,
                ["ENVIRONMENT_DATA_BUFFER_INDEX"] = BindlessIndex.EnvironmentDataBuffer,
                ["REFLECTION_PROBE_BUFFER_INDEX"] = BindlessIndex.ReflectionProbeBuffer,
                ["SOLID_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.SolidDepthMeshletDrawBufferBase,
                ["SOLID_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.SolidDepthMeshletDrawBufferFrame1,
                ["MASKED_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.MaskedDepthMeshletDrawBufferBase,
                ["MASKED_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.MaskedDepthMeshletDrawBufferFrame1,
                ["SKINNING_VERTEX_DATA_BUFFER_INDEX"] = BindlessIndex.SkinningVertexDataBuffer,
                ["SKIN_MATRIX_BUFFER_BASE_INDEX"] = BindlessIndex.SkinMatrixBufferBase,
                ["SKIN_MATRIX_BUFFER_FRAME1_INDEX"] = BindlessIndex.SkinMatrixBufferFrame1,
                ["SKINNED_VERTEX_BUFFER_BASE_INDEX"] = BindlessIndex.SkinnedVertexBufferBase,
                ["SKINNED_VERTEX_BUFFER_FRAME1_INDEX"] = BindlessIndex.SkinnedVertexBufferFrame1,
                ["SKINNING_DISPATCH_BUFFER_BASE_INDEX"] = BindlessIndex.SkinningDispatchBufferBase,
                ["SKINNING_DISPATCH_BUFFER_FRAME1_INDEX"] = BindlessIndex.SkinningDispatchBufferFrame1,
                ["PARTICLE_INSTANCE_BUFFER_BASE_INDEX"] = BindlessIndex.ParticleInstanceBufferBase,
                ["PARTICLE_INSTANCE_BUFFER_FRAME1_INDEX"] = BindlessIndex.ParticleInstanceBufferFrame1,
                ["PARTICLE_BATCH_BUFFER_BASE_INDEX"] = BindlessIndex.ParticleBatchBufferBase,
                ["PARTICLE_BATCH_BUFFER_FRAME1_INDEX"] = BindlessIndex.ParticleBatchBufferFrame1,
                ["PARTICLE_FRAME_DATA_BUFFER_BASE_INDEX"] = BindlessIndex.ParticleFrameDataBufferBase,
                ["PARTICLE_FRAME_DATA_BUFFER_FRAME1_INDEX"] = BindlessIndex.ParticleFrameDataBufferFrame1,
                ["GPU_PARTICLE_STATE_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleStateBufferBase,
                ["GPU_PARTICLE_STATE_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleStateBufferFrame1,
                ["GPU_PARTICLE_ALIVE_INDEX_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleAliveIndexBufferBase,
                ["GPU_PARTICLE_ALIVE_INDEX_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleAliveIndexBufferFrame1,
                ["GPU_PARTICLE_DEAD_INDEX_BUFFER_INDEX"] = BindlessIndex.GpuParticleDeadIndexBuffer,
                ["GPU_PARTICLE_EMITTER_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleEmitterBufferBase,
                ["GPU_PARTICLE_EMITTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleEmitterBufferFrame1,
                ["GPU_PARTICLE_COUNTER_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleCounterBufferBase,
                ["GPU_PARTICLE_COUNTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleCounterBufferFrame1,
                ["GPU_PARTICLE_RENDER_INSTANCE_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleRenderInstanceBufferBase,
                ["GPU_PARTICLE_RENDER_INSTANCE_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleRenderInstanceBufferFrame1,
                ["GPU_PARTICLE_INDIRECT_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleIndirectDrawBufferBase,
                ["GPU_PARTICLE_INDIRECT_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleIndirectDrawBufferFrame1,
                ["GPU_PARTICLE_CURVE_SAMPLE_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleCurveSampleBufferBase,
                ["GPU_PARTICLE_CURVE_SAMPLE_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleCurveSampleBufferFrame1,
                ["GPU_PARTICLE_UNSORTED_RENDER_INSTANCE_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleUnsortedRenderInstanceBufferBase,
                ["GPU_PARTICLE_UNSORTED_RENDER_INSTANCE_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleUnsortedRenderInstanceBufferFrame1,
                ["GPU_PARTICLE_SORT_KEY_BUFFER_BASE_INDEX"] = BindlessIndex.GpuParticleSortKeyBufferBase,
                ["GPU_PARTICLE_SORT_KEY_BUFFER_FRAME1_INDEX"] = BindlessIndex.GpuParticleSortKeyBufferFrame1,
                ["FOLIAGE_PROTOTYPE_BUFFER_INDEX"] = BindlessIndex.FoliagePrototypeBuffer,
                ["FOLIAGE_PATCH_BUFFER_INDEX"] = BindlessIndex.FoliagePatchBuffer,
                ["FOLIAGE_CLUSTER_BUFFER_INDEX"] = BindlessIndex.FoliageClusterBuffer,
                ["FOLIAGE_INSTANCE_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageInstanceBufferBase,
                ["FOLIAGE_INSTANCE_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageInstanceBufferFrame1,
                ["FOLIAGE_VISIBLE_CLUSTER_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageVisibleClusterBufferBase,
                ["FOLIAGE_VISIBLE_CLUSTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageVisibleClusterBufferFrame1,
                ["FOLIAGE_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageMeshletDrawBufferBase,
                ["FOLIAGE_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageMeshletDrawBufferFrame1,
                ["FOLIAGE_COUNTER_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageCounterBufferBase,
                ["FOLIAGE_COUNTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageCounterBufferFrame1,
                ["FOLIAGE_INDIRECT_DISPATCH_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageIndirectDispatchBufferBase,
                ["FOLIAGE_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageIndirectDispatchBufferFrame1,
                ["SCENE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase,
                ["SCENE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferFrame1,
                ["SCENE_SUBMISSION_COUNTER_BUFFER_BASE_INDEX"] = BindlessIndex.SceneSubmissionCounterBufferBase,
                ["SCENE_SUBMISSION_COUNTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneSubmissionCounterBufferFrame1,
                ["SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_BASE_INDEX"] = BindlessIndex.SceneOpaqueIndirectDispatchBufferBase,
                ["SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneOpaqueIndirectDispatchBufferFrame1,
                ["SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                ["SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferFrame1,
                ["SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase,
                ["SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferFrame1,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferBase,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferFrame1,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade1BufferBase,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade1BufferFrame1,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade2BufferBase,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade2BufferFrame1,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade3BufferBase,
                ["SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalStaticShadowCompactedCascade3BufferFrame1,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferBase,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferFrame1,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade1BufferBase,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade1BufferFrame1,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade2BufferBase,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade2BufferFrame1,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade3BufferBase,
                ["SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade3BufferFrame1,
                ["DDGI_PROBE_VOLUME_BUFFER_INDEX"] = BindlessIndex.DdgiProbeVolumeBuffer,
                ["DDGI_PROBE_STATE_BUFFER_INDEX"] = BindlessIndex.DdgiProbeStateBuffer,
                ["DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX"] = BindlessIndex.DdgiProbeUpdateQueueBuffer,
                ["DDGI_PROBE_RELOCATION_CLASSIFICATION_BUFFER_INDEX"] = BindlessIndex.DdgiProbeRelocationClassificationBuffer,
                ["DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX"] = BindlessIndex.DdgiIrradianceAtlasBuffer,
                ["DDGI_VISIBILITY_ATLAS_BUFFER_INDEX"] = BindlessIndex.DdgiVisibilityAtlasBuffer,
                ["DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX"] = BindlessIndex.DdgiRayQueryInstanceBuffer,
                ["AUTO_EXPOSURE_HISTOGRAM_BUFFER_BASE_INDEX"] = BindlessIndex.AutoExposureHistogramBufferBase,
                ["AUTO_EXPOSURE_HISTOGRAM_BUFFER_FRAME1_INDEX"] = BindlessIndex.AutoExposureHistogramBufferFrame1,
                ["AUTO_EXPOSURE_STATE_BUFFER_BASE_INDEX"] = BindlessIndex.AutoExposureStateBufferBase,
                ["AUTO_EXPOSURE_STATE_BUFFER_FRAME1_INDEX"] = BindlessIndex.AutoExposureStateBufferFrame1,
                ["PACKED_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.PackedMeshletDrawBufferBase,
                ["PACKED_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.PackedMeshletDrawBufferFrame1,
                ["PACKED_SOLID_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.PackedSolidDepthMeshletDrawBufferBase,
                ["PACKED_SOLID_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.PackedSolidDepthMeshletDrawBufferFrame1,
                ["PACKED_MASKED_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.PackedMaskedDepthMeshletDrawBufferBase,
                ["PACKED_MASKED_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.PackedMaskedDepthMeshletDrawBufferFrame1,
                ["MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletTaskFrameDataBufferBase,
                ["MESHLET_TASK_FRAME_DATA_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletTaskFrameDataBufferFrame1,
                ["FULL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.FullOpaqueMeshletDrawBufferBase,
                ["FULL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.FullOpaqueMeshletDrawBufferFrame1,
                ["PACKED_FULL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.PackedFullOpaqueMeshletDrawBufferBase,
                ["PACKED_FULL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.PackedFullOpaqueMeshletDrawBufferFrame1,
                ["SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase,
                ["SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferFrame1,
                ["PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase,
                ["PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferFrame1,
                ["VERTEX_POSITION_BUFFER_INDEX"] = BindlessIndex.VertexPositionBuffer,
                ["VERTEX_NORMAL_TANGENT_BUFFER_INDEX"] = BindlessIndex.VertexNormalTangentBuffer,
                ["VERTEX_UV_COLOR_BUFFER_INDEX"] = BindlessIndex.VertexUvColorBuffer,
                ["DIRECTIONAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.DirectionalStaticShadowMeshletDrawBufferBase,
                ["DIRECTIONAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.DirectionalStaticShadowMeshletDrawBufferFrame1,
                ["DIRECTIONAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase,
                ["DIRECTIONAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferFrame1,
                ["LOCAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.LocalStaticShadowMeshletDrawBufferBase,
                ["LOCAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.LocalStaticShadowMeshletDrawBufferFrame1,
                ["LOCAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX"] = BindlessIndex.LocalDynamicShadowMeshletDrawBufferBase,
                ["LOCAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX"] = BindlessIndex.LocalDynamicShadowMeshletDrawBufferFrame1,
                ["STATIC_BUFFER_COUNT"] = BindlessIndex.StaticBufferCount,
                ["FIRST_TEXTURE_INDEX"] = BindlessIndex.FirstTextureIndex,
                ["DEPTH_TEXTURE_INDEX"] = BindlessIndex.DepthTexture,
                ["HIZ_DEPTH_TEXTURE_INDEX"] = BindlessIndex.HiZDepthTexture,
                ["HDR_SCENE_COLOR_TEXTURE_INDEX"] = BindlessIndex.HdrSceneColorTexture,
                ["BLOOM_MIP_TEXTURE_BASE"] = BindlessIndex.BloomMipTextureBase,
                ["MAX_BLOOM_MIP_TEXTURES"] = BindlessIndex.MaxBloomMipTextures,
                ["DIRECTIONAL_SHADOW_TEXTURE_BASE"] = BindlessIndex.DirectionalShadowTextureBase,
                ["MAX_DIRECTIONAL_SHADOW_TEXTURES"] = BindlessIndex.MaxDirectionalShadowTextures,
                ["SPOT_SHADOW_ATLAS_TEXTURE_INDEX"] = BindlessIndex.SpotShadowAtlasTexture,
                ["POINT_SHADOW_CUBEMAP_ARRAY_TEXTURE_INDEX"] = BindlessIndex.PointShadowCubemapArrayTexture,
                ["ENVIRONMENT_CUBEMAP_TEXTURE_INDEX"] = BindlessIndex.EnvironmentCubemapTexture,
                ["IRRADIANCE_CUBEMAP_TEXTURE_INDEX"] = BindlessIndex.IrradianceCubemapTexture,
                ["PREFILTERED_ENVIRONMENT_TEXTURE_INDEX"] = BindlessIndex.PrefilteredEnvironmentTexture,
                ["BRDF_LUT_TEXTURE_INDEX"] = BindlessIndex.BrdfLutTexture,
                ["AMBIENT_OCCLUSION_RAW_TEXTURE_INDEX"] = BindlessIndex.AmbientOcclusionRawTexture,
                ["AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX"] = BindlessIndex.AmbientOcclusionBlurredTexture,
                ["SCENE_NORMAL_TEXTURE_INDEX"] = BindlessIndex.SceneNormalTexture,
                ["SCENE_MATERIAL_TEXTURE_INDEX"] = BindlessIndex.SceneMaterialTexture,
                ["SSGI_TRACE_SOURCE_TEXTURE_INDEX"] = BindlessIndex.SsgiTraceSourceTexture,
                ["SSGI_RAW_TEXTURE_INDEX"] = BindlessIndex.SsgiRawTexture,
                ["SSGI_FILTERED_TEXTURE_INDEX"] = BindlessIndex.SsgiFilteredTexture,
                ["SSGI_HISTORY_TEXTURE_INDEX"] = BindlessIndex.SsgiHistoryTexture,
                ["SSGI_PREVIOUS_DEPTH_TEXTURE_INDEX"] = BindlessIndex.SsgiPreviousDepthTexture,
                ["SSGI_PREVIOUS_NORMAL_TEXTURE_INDEX"] = BindlessIndex.SsgiPreviousNormalTexture,
                ["GI_FINAL_DIFFUSE_TEXTURE_INDEX"] = BindlessIndex.GiFinalDiffuseTexture,
                ["LDR_SCENE_COLOR_TEXTURE_INDEX"] = BindlessIndex.LdrSceneColorTexture,
                ["SMAA_EDGES_TEXTURE_INDEX"] = BindlessIndex.SmaaEdgesTexture,
                ["SMAA_BLEND_WEIGHTS_TEXTURE_INDEX"] = BindlessIndex.SmaaBlendWeightsTexture,
                ["SMAA_AREA_TEXTURE_INDEX"] = BindlessIndex.SmaaAreaTexture,
                ["SMAA_SEARCH_TEXTURE_INDEX"] = BindlessIndex.SmaaSearchTexture,
                ["MOTION_VECTOR_TEXTURE_INDEX"] = BindlessIndex.MotionVectorTexture,
                ["TAA_HISTORY_TEXTURE_INDEX"] = BindlessIndex.TaaHistoryTexture,
                ["FOGGED_SCENE_COLOR_TEXTURE_INDEX"] = BindlessIndex.FoggedSceneColorTexture,
                ["REFLECTION_PROBE_CUBEMAP_ARRAY_TEXTURE_INDEX"] = BindlessIndex.ReflectionProbeCubemapArrayTexture,
                ["REFLECTION_PROBE_DEBUG_TEXTURE_INDEX"] = BindlessIndex.ReflectionProbeDebugTexture,
                ["WEIGHTED_OIT_ACCUMULATION_TEXTURE_INDEX"] = BindlessIndex.WeightedOitAccumulationTexture,
                ["WEIGHTED_OIT_REVEALAGE_TEXTURE_INDEX"] = BindlessIndex.WeightedOitRevealageTexture,
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
        public void BindlessHeap_ExposesStorageBuffersToVertexShaders()
        {
            string source = BindlessHeapSource.Value;

            Assert.That(source, Does.Contain("ShaderStageFlags.VertexBit"));
        }

        [Test]
        public void PerFrameBufferIndices_AreDerivedFromFramesInFlight()
        {
            Assert.That(RenderingConstants.FramesInFlight, Is.EqualTo(2), "The current fixed table has two per-frame slots.");
            Assert.That(BindlessIndex.InstanceBufferFrame1, Is.EqualTo(BindlessIndex.InstanceBufferBase + 1));
            Assert.That(BindlessIndex.MeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.MeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.TransparentMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.TransparentMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.SolidDepthMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.SolidDepthMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.MaskedDepthMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.MaskedDepthMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.RendererDiagnosticsBufferFrame1, Is.EqualTo(BindlessIndex.RendererDiagnosticsBufferBase + 1));
            Assert.That(BindlessIndex.SkinMatrixBufferFrame1, Is.EqualTo(BindlessIndex.SkinMatrixBufferBase + 1));
            Assert.That(BindlessIndex.SkinnedVertexBufferFrame1, Is.EqualTo(BindlessIndex.SkinnedVertexBufferBase + 1));
            Assert.That(BindlessIndex.SkinningDispatchBufferFrame1, Is.EqualTo(BindlessIndex.SkinningDispatchBufferBase + 1));
            Assert.That(BindlessIndex.ParticleInstanceBufferFrame1, Is.EqualTo(BindlessIndex.ParticleInstanceBufferBase + 1));
            Assert.That(BindlessIndex.ParticleBatchBufferFrame1, Is.EqualTo(BindlessIndex.ParticleBatchBufferBase + 1));
            Assert.That(BindlessIndex.ParticleFrameDataBufferFrame1, Is.EqualTo(BindlessIndex.ParticleFrameDataBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleStateBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleStateBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleAliveIndexBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleAliveIndexBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleEmitterBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleEmitterBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleCounterBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleCounterBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleRenderInstanceBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleRenderInstanceBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleIndirectDrawBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleIndirectDrawBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleCurveSampleBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleCurveSampleBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleUnsortedRenderInstanceBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleUnsortedRenderInstanceBufferBase + 1));
            Assert.That(BindlessIndex.GpuParticleSortKeyBufferFrame1, Is.EqualTo(BindlessIndex.GpuParticleSortKeyBufferBase + 1));
            Assert.That(BindlessIndex.FoliageInstanceBufferFrame1, Is.EqualTo(BindlessIndex.FoliageInstanceBufferBase + 1));
            Assert.That(BindlessIndex.FoliageVisibleClusterBufferFrame1, Is.EqualTo(BindlessIndex.FoliageVisibleClusterBufferBase + 1));
            Assert.That(BindlessIndex.FoliageMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.FoliageMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.FoliageCounterBufferFrame1, Is.EqualTo(BindlessIndex.FoliageCounterBufferBase + 1));
            Assert.That(BindlessIndex.FoliageIndirectDispatchBufferFrame1, Is.EqualTo(BindlessIndex.FoliageIndirectDispatchBufferBase + 1));
            Assert.That(BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.SceneSubmissionCounterBufferFrame1, Is.EqualTo(BindlessIndex.SceneSubmissionCounterBufferBase + 1));
            Assert.That(BindlessIndex.SceneOpaqueIndirectDispatchBufferFrame1, Is.EqualTo(BindlessIndex.SceneOpaqueIndirectDispatchBufferBase + 1));
            Assert.That(BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferFrame1, Is.EqualTo(BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferBase + 1));
            Assert.That(BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferFrame1, Is.EqualTo(BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferBase + 1));
            Assert.That(BindlessIndex.AutoExposureHistogramBufferFrame1, Is.EqualTo(BindlessIndex.AutoExposureHistogramBufferBase + 1));
            Assert.That(BindlessIndex.AutoExposureStateBufferFrame1, Is.EqualTo(BindlessIndex.AutoExposureStateBufferBase + 1));
            Assert.That(BindlessIndex.PackedMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.PackedMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.PackedSolidDepthMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.PackedSolidDepthMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.PackedMaskedDepthMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.PackedMaskedDepthMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.MeshletTaskFrameDataBufferFrame1, Is.EqualTo(BindlessIndex.MeshletTaskFrameDataBufferBase + 1));
            Assert.That(BindlessIndex.FullOpaqueMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.FullOpaqueMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.PackedFullOpaqueMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.PackedFullOpaqueMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferFrame1, Is.EqualTo(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase + 1));
            Assert.That(BindlessIndex.DirectionalShadowMeshletDrawBufferCount, Is.EqualTo(RenderingConstants.FramesInFlight));
            Assert.That(BindlessIndex.LocalShadowMeshletDrawBufferCount, Is.EqualTo(RenderingConstants.FramesInFlight));
            Assert.That(BindlessIndex.DirectionalShadowTextureBase, Is.EqualTo(BindlessIndex.BloomMipTextureBase + BindlessIndex.MaxBloomMipTextures));
            Assert.That(BindlessIndex.SpotShadowAtlasTexture, Is.EqualTo(BindlessIndex.DirectionalShadowTextureBase + BindlessIndex.MaxDirectionalShadowTextures));
            Assert.That(BindlessIndex.PointShadowCubemapArrayTexture, Is.EqualTo(BindlessIndex.SpotShadowAtlasTexture + 1));
            Assert.That(BindlessIndex.EnvironmentCubemapTexture, Is.EqualTo(BindlessIndex.PointShadowCubemapArrayTexture + 1));
            Assert.That(BindlessIndex.IrradianceCubemapTexture, Is.EqualTo(BindlessIndex.EnvironmentCubemapTexture + 1));
            Assert.That(BindlessIndex.PrefilteredEnvironmentTexture, Is.EqualTo(BindlessIndex.IrradianceCubemapTexture + 1));
            Assert.That(BindlessIndex.BrdfLutTexture, Is.EqualTo(BindlessIndex.PrefilteredEnvironmentTexture + 1));
            Assert.That(BindlessIndex.AmbientOcclusionRawTexture, Is.EqualTo(BindlessIndex.BrdfLutTexture + 1));
            Assert.That(BindlessIndex.AmbientOcclusionBlurredTexture, Is.EqualTo(BindlessIndex.AmbientOcclusionRawTexture + 1));
            Assert.That(BindlessIndex.SceneNormalTexture, Is.EqualTo(BindlessIndex.AmbientOcclusionBlurredTexture + 1));
            Assert.That(BindlessIndex.SceneMaterialTexture, Is.EqualTo(BindlessIndex.SceneNormalTexture + 1));
            Assert.That(BindlessIndex.SsgiTraceSourceTexture, Is.EqualTo(BindlessIndex.SceneMaterialTexture + 1));
            Assert.That(BindlessIndex.SsgiRawTexture, Is.EqualTo(BindlessIndex.SsgiTraceSourceTexture + 1));
            Assert.That(BindlessIndex.SsgiFilteredTexture, Is.EqualTo(BindlessIndex.SsgiRawTexture + 1));
            Assert.That(BindlessIndex.SsgiHistoryTexture, Is.EqualTo(BindlessIndex.SsgiFilteredTexture + 1));
            Assert.That(BindlessIndex.SsgiPreviousDepthTexture, Is.EqualTo(BindlessIndex.SsgiHistoryTexture + 1));
            Assert.That(BindlessIndex.SsgiPreviousNormalTexture, Is.EqualTo(BindlessIndex.SsgiPreviousDepthTexture + 1));
            Assert.That(BindlessIndex.GiFinalDiffuseTexture, Is.EqualTo(BindlessIndex.SsgiPreviousNormalTexture + 1));
            Assert.That(BindlessIndex.LdrSceneColorTexture, Is.EqualTo(BindlessIndex.GiFinalDiffuseTexture + 1));
            Assert.That(BindlessIndex.SmaaEdgesTexture, Is.EqualTo(BindlessIndex.LdrSceneColorTexture + 1));
            Assert.That(BindlessIndex.SmaaBlendWeightsTexture, Is.EqualTo(BindlessIndex.SmaaEdgesTexture + 1));
            Assert.That(BindlessIndex.SmaaAreaTexture, Is.EqualTo(BindlessIndex.SmaaBlendWeightsTexture + 1));
            Assert.That(BindlessIndex.SmaaSearchTexture, Is.EqualTo(BindlessIndex.SmaaAreaTexture + 1));
            Assert.That(BindlessIndex.MotionVectorTexture, Is.EqualTo(BindlessIndex.SmaaSearchTexture + 1));
            Assert.That(BindlessIndex.TaaHistoryTexture, Is.EqualTo(BindlessIndex.MotionVectorTexture + 1));
            Assert.That(BindlessIndex.FoggedSceneColorTexture, Is.EqualTo(BindlessIndex.TaaHistoryTexture + 1));
            Assert.That(BindlessIndex.ReflectionProbeCubemapArrayTexture, Is.EqualTo(BindlessIndex.FoggedSceneColorTexture + 1));
            Assert.That(BindlessIndex.ReflectionProbeDebugTexture, Is.EqualTo(BindlessIndex.ReflectionProbeCubemapArrayTexture + 1));
            Assert.That(BindlessIndex.WeightedOitAccumulationTexture, Is.EqualTo(BindlessIndex.ReflectionProbeDebugTexture + 1));
            Assert.That(BindlessIndex.WeightedOitRevealageTexture, Is.EqualTo(BindlessIndex.WeightedOitAccumulationTexture + 1));
            Assert.That(BindlessIndex.FirstDynamicTextureIndex, Is.EqualTo(BindlessIndex.WeightedOitRevealageTexture + 1));
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
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.MaterialExtensionDataBuffer), Is.EqualTo(nameof(BindlessIndex.MaterialExtensionDataBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.VertexBuffer), Is.EqualTo(nameof(BindlessIndex.VertexBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase), Is.EqualTo(nameof(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.VertexPositionBuffer), Is.EqualTo(nameof(BindlessIndex.VertexPositionBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.VertexNormalTangentBuffer), Is.EqualTo(nameof(BindlessIndex.VertexNormalTangentBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.VertexUvColorBuffer), Is.EqualTo(nameof(BindlessIndex.VertexUvColorBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.IndexBuffer), Is.EqualTo(nameof(BindlessIndex.IndexBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.LightBuffer), Is.EqualTo(nameof(BindlessIndex.LightBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.FoliagePrototypeBuffer), Is.EqualTo(nameof(BindlessIndex.FoliagePrototypeBuffer)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.FoliageMeshletDrawBufferBase), Is.EqualTo(nameof(BindlessIndex.FoliageMeshletDrawBufferBase)));
                Assert.That(BindlessIndex.GetIndexName(BindlessIndex.DdgiRayQueryInstanceBuffer), Is.EqualTo(nameof(BindlessIndex.DdgiRayQueryInstanceBuffer)));
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
            yield return BindlessIndex.RendererDiagnosticsBufferBase;
            yield return BindlessIndex.RendererDiagnosticsBufferFrame1;
            yield return BindlessIndex.DirectionalShadowDataBuffer;
            for (int i = 0; i < BindlessIndex.DirectionalShadowMeshletDrawBufferCount; i++)
                yield return BindlessIndex.DirectionalShadowMeshletDrawBufferBase + i;
            yield return BindlessIndex.SpotShadowDataBuffer;
            yield return BindlessIndex.PointShadowDataBuffer;
            yield return BindlessIndex.LocalLightShadowIndexBuffer;
            for (int i = 0; i < BindlessIndex.LocalShadowMeshletDrawBufferCount; i++)
                yield return BindlessIndex.LocalShadowMeshletDrawBufferBase + i;
            yield return BindlessIndex.EnvironmentDataBuffer;
            yield return BindlessIndex.ReflectionProbeBuffer;
            yield return BindlessIndex.SolidDepthMeshletDrawBufferBase;
            yield return BindlessIndex.SolidDepthMeshletDrawBufferFrame1;
            yield return BindlessIndex.MaskedDepthMeshletDrawBufferBase;
            yield return BindlessIndex.MaskedDepthMeshletDrawBufferFrame1;
            yield return BindlessIndex.SkinningVertexDataBuffer;
            yield return BindlessIndex.SkinMatrixBufferBase;
            yield return BindlessIndex.SkinMatrixBufferFrame1;
            yield return BindlessIndex.SkinnedVertexBufferBase;
            yield return BindlessIndex.SkinnedVertexBufferFrame1;
            yield return BindlessIndex.SkinningDispatchBufferBase;
            yield return BindlessIndex.SkinningDispatchBufferFrame1;
            yield return BindlessIndex.ParticleInstanceBufferBase;
            yield return BindlessIndex.ParticleInstanceBufferFrame1;
            yield return BindlessIndex.ParticleBatchBufferBase;
            yield return BindlessIndex.ParticleBatchBufferFrame1;
            yield return BindlessIndex.ParticleFrameDataBufferBase;
            yield return BindlessIndex.ParticleFrameDataBufferFrame1;
            yield return BindlessIndex.GpuParticleStateBufferBase;
            yield return BindlessIndex.GpuParticleStateBufferFrame1;
            yield return BindlessIndex.GpuParticleAliveIndexBufferBase;
            yield return BindlessIndex.GpuParticleAliveIndexBufferFrame1;
            yield return BindlessIndex.GpuParticleDeadIndexBuffer;
            yield return BindlessIndex.GpuParticleEmitterBufferBase;
            yield return BindlessIndex.GpuParticleEmitterBufferFrame1;
            yield return BindlessIndex.GpuParticleCounterBufferBase;
            yield return BindlessIndex.GpuParticleCounterBufferFrame1;
            yield return BindlessIndex.GpuParticleRenderInstanceBufferBase;
            yield return BindlessIndex.GpuParticleRenderInstanceBufferFrame1;
            yield return BindlessIndex.GpuParticleIndirectDrawBufferBase;
            yield return BindlessIndex.GpuParticleIndirectDrawBufferFrame1;
            yield return BindlessIndex.GpuParticleCurveSampleBufferBase;
            yield return BindlessIndex.GpuParticleCurveSampleBufferFrame1;
            yield return BindlessIndex.GpuParticleUnsortedRenderInstanceBufferBase;
            yield return BindlessIndex.GpuParticleUnsortedRenderInstanceBufferFrame1;
            yield return BindlessIndex.GpuParticleSortKeyBufferBase;
            yield return BindlessIndex.GpuParticleSortKeyBufferFrame1;
            yield return BindlessIndex.FoliagePrototypeBuffer;
            yield return BindlessIndex.FoliagePatchBuffer;
            yield return BindlessIndex.FoliageClusterBuffer;
            yield return BindlessIndex.FoliageInstanceBufferBase;
            yield return BindlessIndex.FoliageInstanceBufferFrame1;
            yield return BindlessIndex.FoliageVisibleClusterBufferBase;
            yield return BindlessIndex.FoliageVisibleClusterBufferFrame1;
            yield return BindlessIndex.FoliageMeshletDrawBufferBase;
            yield return BindlessIndex.FoliageMeshletDrawBufferFrame1;
            yield return BindlessIndex.FoliageCounterBufferBase;
            yield return BindlessIndex.FoliageCounterBufferFrame1;
            yield return BindlessIndex.FoliageIndirectDispatchBufferBase;
            yield return BindlessIndex.FoliageIndirectDispatchBufferFrame1;
            yield return BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase;
            yield return BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferFrame1;
            yield return BindlessIndex.SceneSubmissionCounterBufferBase;
            yield return BindlessIndex.SceneSubmissionCounterBufferFrame1;
            yield return BindlessIndex.SceneOpaqueIndirectDispatchBufferBase;
            yield return BindlessIndex.SceneOpaqueIndirectDispatchBufferFrame1;
            yield return BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase;
            yield return BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferFrame1;
            yield return BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase;
            yield return BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferFrame1;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferBase;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferFrame1;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade1BufferBase;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade1BufferFrame1;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade2BufferBase;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade2BufferFrame1;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade3BufferBase;
            yield return BindlessIndex.SceneDirectionalStaticShadowCompactedCascade3BufferFrame1;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferBase;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferFrame1;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade1BufferBase;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade1BufferFrame1;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade2BufferBase;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade2BufferFrame1;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade3BufferBase;
            yield return BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade3BufferFrame1;
            yield return BindlessIndex.DdgiProbeVolumeBuffer;
            yield return BindlessIndex.DdgiProbeStateBuffer;
            yield return BindlessIndex.DdgiProbeUpdateQueueBuffer;
            yield return BindlessIndex.DdgiProbeRelocationClassificationBuffer;
            yield return BindlessIndex.DdgiIrradianceAtlasBuffer;
            yield return BindlessIndex.DdgiVisibilityAtlasBuffer;
            yield return BindlessIndex.DdgiRayQueryInstanceBuffer;
            yield return BindlessIndex.MaterialExtensionDataBuffer;
            yield return BindlessIndex.AutoExposureHistogramBufferBase;
            yield return BindlessIndex.AutoExposureHistogramBufferFrame1;
            yield return BindlessIndex.AutoExposureStateBufferBase;
            yield return BindlessIndex.AutoExposureStateBufferFrame1;
            yield return BindlessIndex.PackedMeshletDrawBufferBase;
            yield return BindlessIndex.PackedMeshletDrawBufferFrame1;
            yield return BindlessIndex.PackedSolidDepthMeshletDrawBufferBase;
            yield return BindlessIndex.PackedSolidDepthMeshletDrawBufferFrame1;
            yield return BindlessIndex.PackedMaskedDepthMeshletDrawBufferBase;
            yield return BindlessIndex.PackedMaskedDepthMeshletDrawBufferFrame1;
            yield return BindlessIndex.MeshletTaskFrameDataBufferBase;
            yield return BindlessIndex.MeshletTaskFrameDataBufferFrame1;
            yield return BindlessIndex.FullOpaqueMeshletDrawBufferBase;
            yield return BindlessIndex.FullOpaqueMeshletDrawBufferFrame1;
            yield return BindlessIndex.PackedFullOpaqueMeshletDrawBufferBase;
            yield return BindlessIndex.PackedFullOpaqueMeshletDrawBufferFrame1;
            yield return BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase;
            yield return BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferFrame1;
            yield return BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase;
            yield return BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferFrame1;
            yield return BindlessIndex.VertexPositionBuffer;
            yield return BindlessIndex.VertexNormalTangentBuffer;
            yield return BindlessIndex.VertexUvColorBuffer;
            yield return BindlessIndex.DirectionalStaticShadowMeshletDrawBufferBase;
            yield return BindlessIndex.DirectionalStaticShadowMeshletDrawBufferFrame1;
            yield return BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase;
            yield return BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferFrame1;
            yield return BindlessIndex.LocalStaticShadowMeshletDrawBufferBase;
            yield return BindlessIndex.LocalStaticShadowMeshletDrawBufferFrame1;
            yield return BindlessIndex.LocalDynamicShadowMeshletDrawBufferBase;
            yield return BindlessIndex.LocalDynamicShadowMeshletDrawBufferFrame1;
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

        private static string ReadBindlessHeapSource()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Njulf.Rendering", "Descriptors", "BindlessHeap.cs");
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                directory = directory.Parent;
            }

            throw new FileNotFoundException("Could not locate Njulf.Rendering/Descriptors/BindlessHeap.cs from the test output directory.");
        }
    }
}
