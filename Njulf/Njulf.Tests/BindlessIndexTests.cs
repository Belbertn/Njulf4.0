using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Njulf.Rendering;
using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BindlessIndexTests
{
    [Test]
    public void StaticBufferRange_IsContiguous()
    {
        for (int index = 0; index < BindlessIndex.StaticBufferCount; index++)
            Assert.That(BindlessIndex.IsStaticBufferIndex(index), Is.True, index.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(BindlessIndex.IsStaticBufferIndex(-1), Is.False);
            Assert.That(BindlessIndex.IsStaticBufferIndex(BindlessIndex.StaticBufferCount), Is.False);
            Assert.That(BindlessIndex.StaticBufferCount, Is.LessThanOrEqualTo(1024));
        });
    }

    [Test]
    public void SimpleDdgiDescriptors_AreContiguousAndNamed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BindlessIndex.SimpleDdgiParamsBuffer, Is.EqualTo(BindlessIndex.ForwardVisibilityIndirectDispatchBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiIrradianceAtlasBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiParamsBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiEmissiveSourceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer + 1));
            Assert.That(BindlessIndex.FarFieldClipmapParamsBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiEmissiveSourceBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverProbeBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiSchedulerArenaBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiResidencyArenaBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverProbeBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiStorageValidationBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiResidencyArenaBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1, Is.EqualTo(BindlessIndex.SimpleDdgiStorageValidationBufferBase + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverGatherBufferBase + 1));
            Assert.That(BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiLightTreeNodeBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiDirectionalRadianceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiLightTreeScratchBuffer + 1));
            Assert.That(BindlessIndex.DdgiDecalCandidateBuffer, Is.EqualTo(BindlessIndex.DdgiFoliageProxyIndexBuffer + 1));
            Assert.That(BindlessIndex.DdgiFoliageProxyVertexBufferFrame1, Is.EqualTo(BindlessIndex.DdgiDecalCandidateBuffer + 1));
            Assert.That(BindlessIndex.DdgiFoliageProxyIndexBufferFrame1, Is.EqualTo(BindlessIndex.DdgiFoliageProxyVertexBufferFrame1 + 1));
            Assert.That(BindlessIndex.DdgiFoliageProxyPatchBuffer, Is.EqualTo(BindlessIndex.DdgiFoliageProxyIndexBufferFrame1 + 1));
            Assert.That(BindlessIndex.DdgiFoliageProxyPatchBufferFrame1, Is.EqualTo(BindlessIndex.DdgiFoliageProxyPatchBuffer + 1));
            Assert.That(BindlessIndex.DirectionalRayShadowMaskBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverFeedbackCandidateBuffer + 1));
            Assert.That(BindlessIndex.DirectionalRayShadowMaskBufferFrame1, Is.EqualTo(BindlessIndex.DirectionalRayShadowMaskBufferBase + 1));
            Assert.That(BindlessIndex.AreaRayShadowMaskBufferBase, Is.EqualTo(BindlessIndex.VolumetricFogBounceRadianceBuffer + 1));
            Assert.That(BindlessIndex.AreaRayShadowMaskBufferFrame1, Is.EqualTo(BindlessIndex.AreaRayShadowMaskBufferBase + 1));
            Assert.That(BindlessIndex.ForwardMaterialDataBuffer, Is.EqualTo(BindlessIndex.AreaRayShadowMaskBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase, Is.EqualTo(BindlessIndex.ForwardMaterialDataBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferFrame1, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase + 1));
            Assert.That(BindlessIndex.SceneGpuLodHistoryBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferFrame1 + 1));
            Assert.That(BindlessIndex.SceneGpuLodHistoryBufferFrame1, Is.EqualTo(BindlessIndex.SceneGpuLodHistoryBufferBase + 1));
            Assert.That(BindlessIndex.DdgiDynamicGeometryBufferBase, Is.EqualTo(BindlessIndex.SceneGpuLodHistoryBufferFrame1 + 1));
            Assert.That(BindlessIndex.StaticBufferCount, Is.EqualTo(
                BindlessIndex.FoliageImpostorViewBuffer + 1));
            Assert.That(
                BindlessIndex.FoliageImpostorMetadataBuffer,
                Is.EqualTo(
                    BindlessIndex.MeshletPhysicalPageBankBufferBase +
                    BindlessIndex.MeshletPhysicalPageBankBufferCount));
            Assert.That(BindlessIndex.AutomaticPlanarReflectionBuffer,
                Is.EqualTo(BindlessIndex.FoliageImpostorMetadataBuffer + 1));
            Assert.That(BindlessIndex.FoliageAuthoredInstanceCommandBufferBase,
                Is.EqualTo(BindlessIndex.AutomaticPlanarReflectionBuffer + 1));
            Assert.That(BindlessIndex.FoliageImpostorViewBuffer,
                Is.EqualTo(
                    BindlessIndex.FoliageAuthoredInstanceCommandBufferFrame1 + 1));
            Assert.That(BindlessIndex.SceneInstanceCandidateBufferBase, Is.EqualTo(
                BindlessIndex.DdgiDynamicGeometryBufferBase +
                BindlessIndex.DdgiDynamicGeometryBufferCount));
            Assert.That(BindlessIndex.SceneInstanceCandidateBufferFrame1, Is.EqualTo(
                BindlessIndex.SceneInstanceCandidateBufferBase + 1));
            Assert.That(BindlessIndex.MeshletPhysicalPageTableBufferBase, Is.EqualTo(
                BindlessIndex.SceneInstanceCandidateBufferFrame1 + 1));
            Assert.That(BindlessIndex.MeshletPhysicalPageBankBufferBase, Is.EqualTo(
                BindlessIndex.MeshletStreamingFeedbackCounterBufferFrame1 + 1));
            Assert.That(BindlessIndex.AreaLightLtcMatrixTexture, Is.EqualTo(BindlessIndex.PrefilteredEnvironmentNextTexture + 1));
            Assert.That(BindlessIndex.AreaLightLtcAmplitudeTexture, Is.EqualTo(BindlessIndex.AreaLightLtcMatrixTexture + 1));
            Assert.That(BindlessIndex.SimpleDdgiSampledIrradianceTextureBase, Is.EqualTo(BindlessIndex.AreaLightLtcAmplitudeTexture + 1));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiEmissiveSourceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiEmissiveSourceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverProbeBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverProbeBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiResidencyArenaBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiResidencyArenaBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiStorageValidationBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiStorageValidationBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiLightTreeStateBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiLightTreeStateBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiDirectionalRadianceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiDirectionalRadianceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.DirectionalRayShadowMaskBufferBase), Is.EqualTo(nameof(BindlessIndex.DirectionalRayShadowMaskBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.AreaRayShadowMaskBufferBase), Is.EqualTo(nameof(BindlessIndex.AreaRayShadowMaskBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.ForwardMaterialDataBuffer), Is.EqualTo(nameof(BindlessIndex.ForwardMaterialDataBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferFrame1)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SceneGpuLodHistoryBufferBase), Is.EqualTo(nameof(BindlessIndex.SceneGpuLodHistoryBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SceneGpuLodHistoryBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SceneGpuLodHistoryBufferFrame1)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.DdgiDynamicGeometryBufferBase), Is.EqualTo(nameof(BindlessIndex.DdgiDynamicGeometryBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SceneInstanceCandidateBufferBase), Is.EqualTo(nameof(BindlessIndex.SceneInstanceCandidateBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SceneInstanceCandidateBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SceneInstanceCandidateBufferFrame1)));
            Assert.That(
                BindlessIndex.FirstDynamicTextureIndex,
                Is.EqualTo(BindlessIndex.GtaoDebugTexture + 1));
            Assert.That(BindlessIndex.GtaoFilteredTexture,
                Is.EqualTo(BindlessIndex.OpaqueSceneColorSnapshotTexture + 1));
            Assert.That(BindlessIndex.GtaoDebugTexture,
                Is.EqualTo(BindlessIndex.GtaoFilteredTexture + 1));
            Assert.That(BindlessIndex.OpaqueSceneColorSnapshotTexture,
                Is.EqualTo(
                    BindlessIndex.SimpleDdgiSampledVisibilityTextureBase +
                    BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups));
        });
    }

    [Test]
    public void ShaderConstants_MatchCurrentSimpleDdgiContract()
    {
        IReadOnlyDictionary<string, int> expected = new Dictionary<string, int>
        {
            ["SIMPLE_DDGI_PARAMS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiParamsBuffer,
            ["SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            ["SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
            ["SIMPLE_DDGI_RAY_RESULT_SCRATCH_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
            ["SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiProbeStateBuffer,
            ["SIMPLE_DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            ["SIMPLE_DDGI_RELOCATION_CLASSIFICATION_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
            ["SIMPLE_DDGI_TRANSPORT_IRRADIANCE_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer,
            ["SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
            ["SIMPLE_DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRayQueryInstanceBuffer,
            ["SIMPLE_DDGI_EMISSIVE_SOURCE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiEmissiveSourceBuffer,
            ["SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverProbeBuffer,
            ["SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiResidencyArenaBuffer,
            ["SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleDdgiStorageValidationBufferBase,
            ["SIMPLE_DDGI_RECEIVER_GATHER_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleDdgiReceiverGatherBufferBase,
            ["FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX"] = BindlessIndex.FarFieldClipmapParamsBuffer,
            ["SIMPLE_DDGI_EMISSIVE_SURFACE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiEmissiveSurfaceBuffer,
            ["SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiLightTreeNodeBuffer,
            ["SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiLightTreeLeafBuffer,
            ["SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiLightTreeStateBuffer,
            ["SIMPLE_DDGI_LIGHT_TREE_SCRATCH_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiLightTreeScratchBuffer,
            ["SIMPLE_DDGI_DIRECTIONAL_RADIANCE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiDirectionalRadianceBuffer,
            ["SIMPLE_DDGI_DIRECTIONAL_RADIANCE_PARITY_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiDirectionalRadianceParityBuffer,
            ["DDGI_FOLIAGE_PROXY_VERTEX_BUFFER_INDEX"] = BindlessIndex.DdgiFoliageProxyVertexBuffer,
            ["DDGI_FOLIAGE_PROXY_INDEX_BUFFER_INDEX"] = BindlessIndex.DdgiFoliageProxyIndexBuffer,
            ["DDGI_DECAL_CANDIDATE_BUFFER_INDEX"] = BindlessIndex.DdgiDecalCandidateBuffer,
            ["DDGI_FOLIAGE_PROXY_VERTEX_BUFFER_FRAME1_INDEX"] = BindlessIndex.DdgiFoliageProxyVertexBufferFrame1,
            ["DDGI_FOLIAGE_PROXY_INDEX_BUFFER_FRAME1_INDEX"] = BindlessIndex.DdgiFoliageProxyIndexBufferFrame1,
            ["DDGI_FOLIAGE_PROXY_PATCH_BUFFER_INDEX"] = BindlessIndex.DdgiFoliageProxyPatchBuffer,
            ["DDGI_FOLIAGE_PROXY_PATCH_BUFFER_FRAME1_INDEX"] = BindlessIndex.DdgiFoliageProxyPatchBufferFrame1,
            ["SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORDS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverFeedbackRecordsBuffer,
            ["SIMPLE_DDGI_RECEIVER_FEEDBACK_SORT_SCRATCH_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverFeedbackSortScratchBuffer,
            ["SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverFeedbackSummaryBuffer,
            ["OPACITY_MICROMAP_RESIDENT_BUFFER_INDEX"] = BindlessIndex.OpacityMicromapResidentBuffer,
            ["OPACITY_MICROMAP_BUILD_SCRATCH_BUFFER_INDEX"] = BindlessIndex.OpacityMicromapBuildScratchBuffer,
            ["OPACITY_MICROMAP_COMPACTION_BUFFER_INDEX"] = BindlessIndex.OpacityMicromapCompactionBuffer,
            ["SIMPLE_DDGI_GUIDING_DISTRIBUTION_BANK0_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiGuidingDistributionBank0Buffer,
            ["SIMPLE_DDGI_GUIDING_DISTRIBUTION_BANK1_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiGuidingDistributionBank1Buffer,
            ["SIMPLE_DDGI_GUIDING_TRAINING_SCRATCH_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiGuidingTrainingScratchBuffer,
            ["SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiGuidingDirectionPdfSidecarBuffer,
            ["GI_CAUSTIC_TASK_BUFFER_INDEX"] = BindlessIndex.GiCausticTaskBuffer,
            ["GI_CAUSTIC_PHOTON_BUFFER_INDEX"] = BindlessIndex.GiCausticPhotonBuffer,
            ["GI_CAUSTIC_CACHE_BUFFER_INDEX"] = BindlessIndex.GiCausticCacheBuffer,
            ["GI_CAUSTIC_SCRATCH_BUFFER_INDEX"] = BindlessIndex.GiCausticScratchBuffer,
            ["SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_TILE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiNearFieldResidualTileBuffer,
            ["SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverFeedbackCandidateBuffer,
            ["DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_BASE_INDEX"] = BindlessIndex.DirectionalRayShadowMaskBufferBase,
            ["DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_FRAME1_INDEX"] = BindlessIndex.DirectionalRayShadowMaskBufferFrame1,
            ["AREA_RAY_SHADOW_MASK_BUFFER_BASE_INDEX"] = BindlessIndex.AreaRayShadowMaskBufferBase,
            ["FORWARD_MATERIAL_DATA_BUFFER_INDEX"] = BindlessIndex.ForwardMaterialDataBuffer,
            ["SIMPLE_DDGI_RECEIVER_GATHER_SURFACE_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferBase,
            ["SIMPLE_DDGI_RECEIVER_GATHER_SURFACE_BUFFER_FRAME1_INDEX"] = BindlessIndex.SimpleDdgiReceiverGatherSurfaceBufferFrame1,
            ["SCENE_GPU_LOD_HISTORY_BUFFER_BASE_INDEX"] = BindlessIndex.SceneGpuLodHistoryBufferBase,
            ["SCENE_GPU_LOD_HISTORY_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneGpuLodHistoryBufferFrame1,
            ["SCENE_INSTANCE_CANDIDATE_BUFFER_BASE_INDEX"] = BindlessIndex.SceneInstanceCandidateBufferBase,
            ["SCENE_INSTANCE_CANDIDATE_BUFFER_FRAME1_INDEX"] = BindlessIndex.SceneInstanceCandidateBufferFrame1,
            ["MESHLET_PHYSICAL_PAGE_TABLE_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletPhysicalPageTableBufferBase,
            ["MESHLET_PHYSICAL_PAGE_TABLE_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletPhysicalPageTableBufferFrame1,
            ["MESHLET_STREAMING_RANGE_BUFFER_INDEX"] = BindlessIndex.MeshletStreamingRangeBuffer,
            ["MESHLET_STREAMING_RANGE_STATE_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletStreamingRangeStateBufferBase,
            ["MESHLET_STREAMING_RANGE_STATE_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletStreamingRangeStateBufferFrame1,
            ["MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX"] = BindlessIndex.MeshletVirtualMappingBuffer,
            ["MESHLET_STREAMING_DEMAND_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletStreamingDemandBufferBase,
            ["MESHLET_STREAMING_DEMAND_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletStreamingDemandBufferFrame1,
            ["MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletStreamingFeedbackCounterBufferBase,
            ["MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_FRAME1_INDEX"] = BindlessIndex.MeshletStreamingFeedbackCounterBufferFrame1,
            ["MESHLET_PHYSICAL_PAGE_BANK_BUFFER_BASE_INDEX"] = BindlessIndex.MeshletPhysicalPageBankBufferBase,
            ["AUTOMATIC_PLANAR_REFLECTION_BUFFER_INDEX"] = BindlessIndex.AutomaticPlanarReflectionBuffer,
            ["FOLIAGE_AUTHORED_INSTANCE_COMMAND_BUFFER_BASE_INDEX"] = BindlessIndex.FoliageAuthoredInstanceCommandBufferBase,
            ["FOLIAGE_AUTHORED_INSTANCE_COMMAND_BUFFER_FRAME1_INDEX"] = BindlessIndex.FoliageAuthoredInstanceCommandBufferFrame1,
            ["FOLIAGE_IMPOSTOR_VIEW_BUFFER_INDEX"] = BindlessIndex.FoliageImpostorViewBuffer,
            ["AREA_LIGHT_LTC_MATRIX_TEXTURE_INDEX"] = BindlessIndex.AreaLightLtcMatrixTexture,
            ["AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX"] = BindlessIndex.AreaLightLtcAmplitudeTexture,
            ["OPAQUE_SCENE_COLOR_SNAPSHOT_TEXTURE_INDEX"] = BindlessIndex.OpaqueSceneColorSnapshotTexture,
            ["GTAO_FILTERED_TEXTURE_INDEX"] = BindlessIndex.GtaoFilteredTexture,
            ["GTAO_DEBUG_TEXTURE_INDEX"] = BindlessIndex.GtaoDebugTexture,
            ["STATIC_BUFFER_COUNT"] = BindlessIndex.StaticBufferCount
        };

        string source = ReadCommonGlsl();
        foreach ((string name, int value) in expected)
            Assert.That(ReadShaderIntConstant(source, name), Is.EqualTo(value), name);

        Assert.That(source, Does.Not.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
    }

    private static int ReadShaderIntConstant(string source, string name)
    {
        Match match = Regex.Match(source, $@"\bconst\s+int\s+{Regex.Escape(name)}\s*=\s*(\d+)\s*;");
        if (!match.Success)
            throw new AssertionException($"Shader constant '{name}' was not found in common.glsl.");

        return int.Parse(match.Groups[1].Value);
    }

    private static string ReadCommonGlsl()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "Njulf.Shaders", "common.glsl");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate Njulf.Shaders/common.glsl from the test output directory.");
    }
}
