using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class RendererDiagnosticsTests
    {
        [Test]
        public void Empty_HasZeroedPerformanceCounters()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty;

            Assert.Multiple(() =>
            {
                Assert.That(diagnostics.CpuSceneBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuPayloadSignatureMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuObjectCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuMeshletCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuUploadMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuMaterialUploadMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuTotalDrawSceneMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuDirectionalShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSpotShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuPointShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuDepthPrePassRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZBuildRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomExtractRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomDownsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomUpsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuCompositeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDepthPrePassMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuHiZBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuLightCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuForwardOpaqueMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuTransparentMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.SceneUploadCount, Is.EqualTo(0));
                Assert.That(diagnostics.SceneUploadSkipped, Is.EqualTo(0));
                Assert.That(diagnostics.ObjectCandidatesCpu, Is.EqualTo(0));
                Assert.That(diagnostics.ObjectFrustumCulledCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletCandidatesCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletFrustumCulledCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletLodSkippedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletLod0SubmittedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletLod1SubmittedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletLod2SubmittedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.DepthTaskInvocations, Is.EqualTo(0));
                Assert.That(diagnostics.DepthFrustumCulledMeshletsGpu, Is.EqualTo(0));
                Assert.That(diagnostics.DepthEmittedMeshletsGpu, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardTaskInvocations, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardFrustumCulledMeshletsGpu, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardOcclusionTestedMeshletsGpu, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardEmittedMeshletsGpu, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletCountTotal, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletCountSubmittedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.AvgTrianglesPerSubmittedMeshlet, Is.EqualTo(0));
                Assert.That(diagnostics.AvgVerticesPerSubmittedMeshlet, Is.EqualTo(0));
                Assert.That(diagnostics.SmallMeshletsUnder16Triangles, Is.EqualTo(0));
                Assert.That(diagnostics.SmallMeshletsUnder32Triangles, Is.EqualTo(0));
                Assert.That(diagnostics.ScenePayloadRebuilt, Is.EqualTo(0));
                Assert.That(diagnostics.ObjectUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.InstanceUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.MeshletDrawUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.TransparentMeshletDrawUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.MaterialUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.LightUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DepthPrePassEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.HiZEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.OcclusionEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.HiZMipCount, Is.EqualTo(0));
                Assert.That(diagnostics.HiZWidth, Is.EqualTo(0));
                Assert.That(diagnostics.HiZHeight, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowMapSize, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowCascadeCount, Is.EqualTo(0));
                Assert.That(diagnostics.ShadowedDirectionalLightIndex, Is.EqualTo(-1));
                Assert.That(diagnostics.ShadowDebugView, Is.EqualTo(ShadowDebugView.None));
                Assert.That(diagnostics.ShadowNormalBias, Is.EqualTo(0));
                Assert.That(diagnostics.ShadowSlopeScaledDepthBias, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowSelectedCount, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowRejectedByBudgetCount, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowAtlasSize, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowTileSize, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowAtlasCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowAtlasUsedTiles, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowSelectedCount, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowRejectedByBudgetCount, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowMapSize, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowRenderedFaceCount, Is.EqualTo(0));
                Assert.That(diagnostics.DownscaledTextureCount, Is.EqualTo(0));
                Assert.That(diagnostics.MaxLoadedTextureDimension, Is.EqualTo(0));
                Assert.That(diagnostics.EstimatedTextureBytes, Is.EqualTo(0));
                Assert.That(diagnostics.HdrEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.SceneColorFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.Exposure, Is.EqualTo(0));
                Assert.That(diagnostics.ToneMapper, Is.EqualTo(ToneMapper.AcesFitted));
                Assert.That(diagnostics.BloomEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.BloomMipCount, Is.EqualTo(0));
                Assert.That(diagnostics.BloomBaseWidth, Is.EqualTo(0));
                Assert.That(diagnostics.BloomBaseHeight, Is.EqualTo(0));
                Assert.That(diagnostics.BloomFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.BloomIntensity, Is.EqualTo(0));
                Assert.That(diagnostics.BloomThreshold, Is.EqualTo(0));
                Assert.That(diagnostics.BloomKnee, Is.EqualTo(0));
                Assert.That(diagnostics.BloomRadius, Is.EqualTo(0));
                Assert.That(diagnostics.BloomDebugView, Is.EqualTo(BloomDebugView.None));
                Assert.That(diagnostics.BloomDebugMipLevel, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionMode, Is.EqualTo(AmbientOcclusionMode.Disabled));
                Assert.That(diagnostics.AmbientOcclusionDebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(diagnostics.AmbientOcclusionWidth, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionHeight, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.AmbientOcclusionResolutionScale, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionRadius, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionIntensity, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionBias, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionSampleCount, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionBlurRadius, Is.EqualTo(0));
                Assert.That(diagnostics.CpuAmbientOcclusionRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuAmbientOcclusionMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuAmbientOcclusionBlurMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.AntiAliasingMode, Is.EqualTo(AntiAliasingMode.None));
                Assert.That(diagnostics.AntiAliasingDebugView, Is.EqualTo(AntiAliasingDebugView.None));
                Assert.That(diagnostics.AntiAliasingWidth, Is.EqualTo(0));
                Assert.That(diagnostics.AntiAliasingHeight, Is.EqualTo(0));
                Assert.That(diagnostics.AntiAliasingInputFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.AntiAliasingOutputFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.CpuFxaaRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSmaaEdgeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSmaaBlendRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSmaaNeighborhoodRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuAntiAliasingMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.SmaaLookupTexturesReady, Is.EqualTo(0));
                Assert.That(diagnostics.MotionVectorsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.JitterEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.JitterX, Is.EqualTo(0));
                Assert.That(diagnostics.JitterY, Is.EqualTo(0));
                Assert.That(diagnostics.EnvironmentEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.EnvironmentSourceKind, Is.EqualTo(EnvironmentSourceKind.ProceduralSky));
                Assert.That(diagnostics.EnvironmentSourcePath, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.EnvironmentUsesFallback, Is.EqualTo(0));
                Assert.That(diagnostics.EnvironmentCubemapSize, Is.EqualTo(0));
                Assert.That(diagnostics.IrradianceCubemapSize, Is.EqualTo(0));
                Assert.That(diagnostics.PrefilteredEnvironmentSize, Is.EqualTo(0));
                Assert.That(diagnostics.PrefilteredEnvironmentMipCount, Is.EqualTo(0));
                Assert.That(diagnostics.BrdfLutSize, Is.EqualTo(0));
                Assert.That(diagnostics.SkyIntensity, Is.EqualTo(0));
                Assert.That(diagnostics.DiffuseIblIntensity, Is.EqualTo(0));
                Assert.That(diagnostics.SpecularIblIntensity, Is.EqualTo(0));
                Assert.That(diagnostics.EnvironmentDebugView, Is.EqualTo(EnvironmentDebugView.None));
                Assert.That(diagnostics.EnvironmentDebugMipLevel, Is.EqualTo(0));
                Assert.That(diagnostics.EnvironmentTextureBytes, Is.EqualTo(0));
            });
        }

        [Test]
        public void SceneRenderingData_ClearResetsDiagnosticsAndSnapshots()
        {
            var sceneData = new SceneRenderingData
            {
                CpuSceneBuildMicroseconds = 42,
                CpuPayloadSignatureMicroseconds = 1,
                CpuObjectCullMicroseconds = 2,
                CpuMeshletCullMicroseconds = 3,
                CpuUploadMicroseconds = 4,
                CpuMaterialUploadMicroseconds = 5,
                CpuTotalDrawSceneMicroseconds = 6,
                CpuDirectionalShadowRecordMicroseconds = 16,
                CpuSpotShadowRecordMicroseconds = 17,
                CpuPointShadowRecordMicroseconds = 18,
                CpuDepthPrePassRecordMicroseconds = 7,
                CpuHiZBuildRecordMicroseconds = 8,
                CpuLightCullRecordMicroseconds = 9,
                CpuForwardOpaqueRecordMicroseconds = 10,
                CpuTransparentRecordMicroseconds = 11,
                CpuBloomExtractRecordMicroseconds = 12,
                CpuBloomDownsampleRecordMicroseconds = 13,
                CpuBloomUpsampleRecordMicroseconds = 14,
                CpuCompositeRecordMicroseconds = 15,
                GpuDepthPrePassMicroseconds = 13,
                GpuHiZBuildMicroseconds = 14,
                GpuLightCullMicroseconds = 15,
                GpuForwardOpaqueMicroseconds = 16,
                GpuTransparentMicroseconds = 17,
                SceneUploadCount = 3,
                SceneUploadSkipped = 2,
                ObjectCandidatesCpu = 10,
                ObjectFrustumCulledCpu = 4,
                MeshletCandidatesCpu = 30,
                MeshletFrustumCulledCpu = 7,
                MeshletLodSkippedCpu = 8,
                MeshletLod0SubmittedCpu = 9,
                MeshletLod1SubmittedCpu = 10,
                MeshletLod2SubmittedCpu = 11,
                DepthTaskInvocations = 12,
                DepthFrustumCulledMeshletsGpu = 13,
                DepthEmittedMeshletsGpu = 14,
                ForwardTaskInvocations = 15,
                ForwardFrustumCulledMeshletsGpu = 16,
                ForwardOcclusionTestedMeshletsGpu = 17,
                ForwardOcclusionCulledMeshletsGpu = 18,
                ForwardEmittedMeshletsGpu = 19,
                MeshletCountTotal = 20,
                MeshletCountSubmittedCpu = 21,
                AvgTrianglesPerSubmittedMeshlet = 22,
                AvgVerticesPerSubmittedMeshlet = 23,
                SmallMeshletsUnder16Triangles = 24,
                SmallMeshletsUnder32Triangles = 25,
                ScenePayloadRebuilt = 1,
                ObjectUploadBytes = 26,
                InstanceUploadBytes = 27,
                MeshletDrawUploadBytes = 28,
                TransparentMeshletDrawUploadBytes = 29,
                MaterialUploadBytes = 30,
                LightUploadBytes = 31,
                HiZWidth = 32,
                HiZHeight = 33,
                BloomEnabled = true,
                BloomMipCount = 6,
                BloomBaseWidth = 960,
                BloomBaseHeight = 540,
                AmbientOcclusionEnabled = true,
                AmbientOcclusionMode = AmbientOcclusionMode.Ssao,
                AmbientOcclusionDebugView = AmbientOcclusionDebugView.RawAo,
                AmbientOcclusionWidth = 960,
                AmbientOcclusionHeight = 540,
                AmbientOcclusionFormat = "R8Unorm",
                AmbientOcclusionResolutionScale = 0.5f,
                AmbientOcclusionRadius = 0.75f,
                AmbientOcclusionIntensity = 1.0f,
                AmbientOcclusionBias = 0.03f,
                AmbientOcclusionSampleCount = 16,
                AmbientOcclusionBlurRadius = 2,
                CpuAmbientOcclusionRecordMicroseconds = 34,
                CpuAmbientOcclusionBlurRecordMicroseconds = 35,
                AntiAliasingMode = AntiAliasingMode.Smaa1x,
                AntiAliasingDebugView = AntiAliasingDebugView.SmaaEdges,
                AntiAliasingWidth = 1920,
                AntiAliasingHeight = 1080,
                AntiAliasingInputFormat = "R8G8B8A8Unorm",
                AntiAliasingOutputFormat = "B8G8R8A8Srgb",
                CpuFxaaRecordMicroseconds = 36,
                CpuSmaaEdgeRecordMicroseconds = 37,
                CpuSmaaBlendRecordMicroseconds = 38,
                CpuSmaaNeighborhoodRecordMicroseconds = 39,
                GpuAntiAliasingMicroseconds = 40,
                SmaaLookupTexturesReady = 1,
                MotionVectorsEnabled = 0,
                JitterEnabled = 1,
                JitterX = 0.001f,
                JitterY = -0.001f,
                HasCpuSnapshots = true
            };

            sceneData.ObjectData.Add(default);
            sceneData.MeshletDrawCommands.Add(default);
            sceneData.Clear();

            Assert.Multiple(() =>
            {
                Assert.That(sceneData.CpuSceneBuildMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuPayloadSignatureMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuObjectCullMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuMeshletCullMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuUploadMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuMaterialUploadMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuTotalDrawSceneMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuDirectionalShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuSpotShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuPointShadowRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuDepthPrePassRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZBuildRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomExtractRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomDownsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomUpsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuCompositeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuDepthPrePassMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuHiZBuildMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuLightCullMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuForwardOpaqueMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuTransparentMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.SceneUploadCount, Is.EqualTo(0));
                Assert.That(sceneData.SceneUploadSkipped, Is.EqualTo(0));
                Assert.That(sceneData.ObjectCandidatesCpu, Is.EqualTo(0));
                Assert.That(sceneData.ObjectFrustumCulledCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletCandidatesCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletFrustumCulledCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletLodSkippedCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletLod0SubmittedCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletLod1SubmittedCpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletLod2SubmittedCpu, Is.EqualTo(0));
                Assert.That(sceneData.DepthTaskInvocations, Is.EqualTo(0));
                Assert.That(sceneData.DepthFrustumCulledMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.DepthEmittedMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.ForwardTaskInvocations, Is.EqualTo(0));
                Assert.That(sceneData.ForwardFrustumCulledMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.ForwardOcclusionTestedMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.ForwardOcclusionCulledMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.ForwardEmittedMeshletsGpu, Is.EqualTo(0));
                Assert.That(sceneData.MeshletCountTotal, Is.EqualTo(0));
                Assert.That(sceneData.MeshletCountSubmittedCpu, Is.EqualTo(0));
                Assert.That(sceneData.AvgTrianglesPerSubmittedMeshlet, Is.EqualTo(0));
                Assert.That(sceneData.AvgVerticesPerSubmittedMeshlet, Is.EqualTo(0));
                Assert.That(sceneData.SmallMeshletsUnder16Triangles, Is.EqualTo(0));
                Assert.That(sceneData.SmallMeshletsUnder32Triangles, Is.EqualTo(0));
                Assert.That(sceneData.ScenePayloadRebuilt, Is.EqualTo(0));
                Assert.That(sceneData.ObjectUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.InstanceUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.MeshletDrawUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.TransparentMeshletDrawUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.MaterialUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.LightUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.HiZWidth, Is.EqualTo(0));
                Assert.That(sceneData.HiZHeight, Is.EqualTo(0));
                Assert.That(sceneData.BloomEnabled, Is.False);
                Assert.That(sceneData.BloomMipCount, Is.EqualTo(0));
                Assert.That(sceneData.BloomBaseWidth, Is.EqualTo(0));
                Assert.That(sceneData.BloomBaseHeight, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionEnabled, Is.False);
                Assert.That(sceneData.AmbientOcclusionMode, Is.EqualTo(AmbientOcclusionMode.Disabled));
                Assert.That(sceneData.AmbientOcclusionDebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(sceneData.AmbientOcclusionWidth, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionHeight, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionFormat, Is.EqualTo(string.Empty));
                Assert.That(sceneData.AmbientOcclusionResolutionScale, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionRadius, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionIntensity, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionBias, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionSampleCount, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionBlurRadius, Is.EqualTo(0));
                Assert.That(sceneData.CpuAmbientOcclusionRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuAmbientOcclusionBlurRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.AntiAliasingMode, Is.EqualTo(AntiAliasingMode.None));
                Assert.That(sceneData.AntiAliasingDebugView, Is.EqualTo(AntiAliasingDebugView.None));
                Assert.That(sceneData.AntiAliasingWidth, Is.EqualTo(0));
                Assert.That(sceneData.AntiAliasingHeight, Is.EqualTo(0));
                Assert.That(sceneData.AntiAliasingInputFormat, Is.EqualTo(string.Empty));
                Assert.That(sceneData.AntiAliasingOutputFormat, Is.EqualTo(string.Empty));
                Assert.That(sceneData.CpuFxaaRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuSmaaEdgeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuSmaaBlendRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuSmaaNeighborhoodRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuAntiAliasingMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.SmaaLookupTexturesReady, Is.EqualTo(0));
                Assert.That(sceneData.MotionVectorsEnabled, Is.EqualTo(0));
                Assert.That(sceneData.JitterEnabled, Is.EqualTo(0));
                Assert.That(sceneData.JitterX, Is.EqualTo(0));
                Assert.That(sceneData.JitterY, Is.EqualTo(0));
                Assert.That(sceneData.HasCpuSnapshots, Is.False);
                Assert.That(sceneData.ObjectData, Is.Empty);
                Assert.That(sceneData.MeshletDrawCommands, Is.Empty);
            });
        }

        [Test]
        public void SelectMeshletLodLevel_IncreasesWithCameraDistance()
        {
            Vector3 center = Vector3.Zero;

            Assert.Multiple(() =>
            {
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(new Vector3(5f, 0f, 0f), center, 1f), Is.EqualTo(0));
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(new Vector3(20f, 0f, 0f), center, 1f), Is.EqualTo(1));
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(new Vector3(40f, 0f, 0f), center, 1f), Is.EqualTo(2));
            });
        }

        [Test]
        public void ProductionRenderPassOrder_RemainsRenderDocInspectable()
        {
            Assert.That(
                VulkanRenderer.PhaseOneRenderPassOrder,
                Is.EqualTo(VulkanRenderer.ProductionRenderPassOrder));

            Assert.That(
                VulkanRenderer.ProductionRenderPassOrder,
                Is.EqualTo(new[]
                {
                    "DirectionalShadowPass",
                    "SpotShadowPass",
                    "PointShadowPass",
                    "DepthPrePass",
                    "HiZBuildPass",
                    "AmbientOcclusionPass",
                    "AmbientOcclusionBlurPass",
                    "TiledLightCullingPass",
                    "ForwardPlusPass",
                    "SkyboxPass",
                    "TransparentForwardPass",
                    "BloomPass",
                    "ToneMapCompositePass",
                    "AntiAliasingPass"
                }));
        }

        [Test]
        public void RenderSettings_DefaultsUseAcesHdrPipeline()
        {
            var settings = new RenderSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Exposure, Is.EqualTo(1.0f));
                Assert.That(settings.ToneMapper, Is.EqualTo(ToneMapper.AcesFitted));
                Assert.That(settings.ShowRawHdrSceneColor, Is.False);
                Assert.That(settings.Bloom.Enabled, Is.True);
                Assert.That(settings.Bloom.Intensity, Is.EqualTo(0.08f));
                Assert.That(settings.Bloom.Threshold, Is.EqualTo(1.0f));
                Assert.That(settings.Bloom.Knee, Is.EqualTo(0.5f));
                Assert.That(settings.Bloom.Radius, Is.EqualTo(0.65f));
                Assert.That(settings.Bloom.MipCount, Is.EqualTo(6));
                Assert.That(settings.Bloom.DebugView, Is.EqualTo(BloomDebugView.None));
                Assert.That(settings.Bloom.DebugMipLevel, Is.EqualTo(0));
                Assert.That(settings.Environment.Enabled, Is.True);
                Assert.That(settings.Environment.SourceKind, Is.EqualTo(EnvironmentSourceKind.ProceduralSky));
                Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.Environment.EnvironmentSize, Is.EqualTo(1024));
                Assert.That(settings.Environment.IrradianceSize, Is.EqualTo(64));
                Assert.That(settings.Environment.PrefilteredSize, Is.EqualTo(256));
                Assert.That(settings.Environment.BrdfLutSize, Is.EqualTo(256));
                Assert.That(settings.Environment.DebugView, Is.EqualTo(EnvironmentDebugView.None));
                Assert.That(settings.Environment.DebugMipLevel, Is.EqualTo(0));
                Assert.That(settings.AmbientOcclusion.Enabled, Is.True);
                Assert.That(settings.AmbientOcclusion.Mode, Is.EqualTo(AmbientOcclusionMode.Ssao));
                Assert.That(settings.AmbientOcclusion.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.AmbientOcclusion.Radius, Is.EqualTo(0.75f));
                Assert.That(settings.AmbientOcclusion.Intensity, Is.EqualTo(1.0f));
                Assert.That(settings.AmbientOcclusion.Bias, Is.EqualTo(0.03f));
                Assert.That(settings.AmbientOcclusion.Power, Is.EqualTo(1.2f));
                Assert.That(settings.AmbientOcclusion.SampleCount, Is.EqualTo(16));
                Assert.That(settings.AmbientOcclusion.BlurRadius, Is.EqualTo(2));
                Assert.That(settings.AmbientOcclusion.DepthSigma, Is.EqualTo(2.0f));
                Assert.That(settings.AmbientOcclusion.NormalSigma, Is.EqualTo(32.0f));
                Assert.That(settings.AmbientOcclusion.UseSceneNormals, Is.False);
                Assert.That(settings.AmbientOcclusion.DebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(settings.AntiAliasing.Mode, Is.EqualTo(AntiAliasingMode.Smaa1x));
                Assert.That(settings.AntiAliasing.EffectiveMode, Is.EqualTo(AntiAliasingMode.Smaa1x));
                Assert.That(settings.AntiAliasing.DebugView, Is.EqualTo(AntiAliasingDebugView.None));
                Assert.That(settings.AntiAliasing.FxaaContrastThreshold, Is.EqualTo(0.125f));
                Assert.That(settings.AntiAliasing.FxaaRelativeThreshold, Is.EqualTo(0.166f));
                Assert.That(settings.AntiAliasing.FxaaSubpixelBlending, Is.EqualTo(0.75f));
                Assert.That(settings.AntiAliasing.SmaaThreshold, Is.EqualTo(0.1f));
                Assert.That(settings.AntiAliasing.SmaaMaxSearchSteps, Is.EqualTo(16));
                Assert.That(settings.AntiAliasing.SmaaMaxSearchStepsDiagonal, Is.EqualTo(8));
                Assert.That(settings.AntiAliasing.SmaaCornerRounding, Is.EqualTo(25.0f));
                Assert.That(settings.AntiAliasing.JitterEnabled, Is.False);
                Assert.That(settings.AntiAliasing.JitterSampleCount, Is.EqualTo(8));
                Assert.That(settings.AntiAliasing.TaaFeedbackMin, Is.EqualTo(0.85f));
                Assert.That(settings.AntiAliasing.TaaFeedbackMax, Is.EqualTo(0.95f));
                Assert.That(settings.Shadows.DirectionalShadowsEnabled, Is.True);
                Assert.That(settings.Shadows.DirectionalShadowMapSize, Is.EqualTo(2048));
                Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(3));
                Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(80f));
                Assert.That(settings.Shadows.NormalBias, Is.EqualTo(0.03f));
                Assert.That(settings.Shadows.SlopeScaledDepthBias, Is.EqualTo(1.5f));
                Assert.That(settings.Shadows.ConstantDepthBias, Is.EqualTo(0.0005f));
                Assert.That(settings.Shadows.PcfRadius, Is.EqualTo(1));
                Assert.That(settings.Shadows.SpotShadowsEnabled, Is.True);
                Assert.That(settings.Shadows.MaxShadowedSpotLights, Is.EqualTo(8));
                Assert.That(settings.Shadows.SpotShadowAtlasSize, Is.EqualTo(4096));
                Assert.That(settings.Shadows.SpotShadowTileSize, Is.EqualTo(512));
                Assert.That(settings.Shadows.SpotNormalBias, Is.EqualTo(0.02f));
                Assert.That(settings.Shadows.SpotConstantDepthBias, Is.EqualTo(0.0005f));
                Assert.That(settings.Shadows.SpotSlopeScaledDepthBias, Is.EqualTo(1.5f));
                Assert.That(settings.Shadows.SpotPcfRadius, Is.EqualTo(1));
                Assert.That(settings.Shadows.PointShadowsEnabled, Is.True);
                Assert.That(settings.Shadows.MaxShadowedPointLights, Is.EqualTo(1));
                Assert.That(settings.Shadows.PointShadowMapSize, Is.EqualTo(512));
                Assert.That(settings.Shadows.PointNormalBias, Is.EqualTo(0.03f));
                Assert.That(settings.Shadows.PointConstantDepthBias, Is.EqualTo(0.001f));
                Assert.That(settings.Shadows.PointSlopeScaledDepthBias, Is.EqualTo(1.5f));
                Assert.That(settings.Shadows.PointPcfRadius, Is.EqualTo(1));
                Assert.That(settings.Shadows.DebugView, Is.EqualTo(ShadowDebugView.None));
            });
        }

        [Test]
        public void RenderSettings_ClampsNegativeExposure()
        {
            var settings = new RenderSettings { Exposure = -1.0f };

            Assert.That(settings.Exposure, Is.EqualTo(0.0f));
        }

        [Test]
        public void BloomSettings_ClampToSupportedRanges()
        {
            var settings = new BloomSettings
            {
                Intensity = 9f,
                Threshold = -1f,
                Knee = 2f,
                Radius = -2f,
                MipCount = 99,
                DebugMipLevel = -1
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.Intensity, Is.EqualTo(2.0f));
                Assert.That(settings.Threshold, Is.EqualTo(0.0f));
                Assert.That(settings.Knee, Is.EqualTo(1.0f));
                Assert.That(settings.Radius, Is.EqualTo(0.0f));
                Assert.That(settings.MipCount, Is.EqualTo(8));
                Assert.That(settings.DebugMipLevel, Is.EqualTo(0));
            });
        }

        [Test]
        public void EnvironmentSettings_ClampToSupportedRanges()
        {
            var settings = new EnvironmentSettings
            {
                SkyIntensity = 99f,
                DiffuseIntensity = -1f,
                SpecularIntensity = 20f,
                EnvironmentSize = 300,
                IrradianceSize = 999,
                PrefilteredSize = 1,
                BrdfLutSize = 200,
                DebugMipLevel = -3
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.SkyIntensity, Is.EqualTo(16.0f));
                Assert.That(settings.DiffuseIntensity, Is.EqualTo(0.0f));
                Assert.That(settings.SpecularIntensity, Is.EqualTo(16.0f));
                Assert.That(settings.EnvironmentSize, Is.EqualTo(512));
                Assert.That(settings.IrradianceSize, Is.EqualTo(256));
                Assert.That(settings.PrefilteredSize, Is.EqualTo(64));
                Assert.That(settings.BrdfLutSize, Is.EqualTo(256));
                Assert.That(settings.DebugMipLevel, Is.EqualTo(0));
            });
        }

        [Test]
        public void AmbientOcclusionSettings_ClampToSupportedRanges()
        {
            var settings = new AmbientOcclusionSettings
            {
                ResolutionScale = 0.1f,
                Radius = 99f,
                Intensity = -1f,
                Bias = 9f,
                Power = 0f,
                SampleCount = 99,
                BlurRadius = 99,
                DepthSigma = 0f,
                NormalSigma = 999f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.ResolutionScale, Is.EqualTo(0.25f));
                Assert.That(settings.Radius, Is.EqualTo(5.0f));
                Assert.That(settings.Intensity, Is.EqualTo(0.0f));
                Assert.That(settings.Bias, Is.EqualTo(0.5f));
                Assert.That(settings.Power, Is.EqualTo(0.25f));
                Assert.That(settings.SampleCount, Is.EqualTo(32));
                Assert.That(settings.BlurRadius, Is.EqualTo(4));
                Assert.That(settings.DepthSigma, Is.EqualTo(0.1f));
                Assert.That(settings.NormalSigma, Is.EqualTo(128.0f));
            });
        }

        [Test]
        public void AntiAliasingSettings_ClampToSupportedRangesAndTaaFallsBack()
        {
            var settings = new AntiAliasingSettings
            {
                Mode = AntiAliasingMode.Taa,
                FxaaContrastThreshold = 99f,
                FxaaRelativeThreshold = -1f,
                FxaaSubpixelBlending = 2f,
                SmaaThreshold = 0f,
                SmaaMaxSearchSteps = 99,
                SmaaMaxSearchStepsDiagonal = -1,
                SmaaCornerRounding = 999f,
                JitterSampleCount = 99,
                TaaFeedbackMin = 0f,
                TaaFeedbackMax = 2f,
                TaaVelocityRejectionScale = -1f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveMode, Is.EqualTo(AntiAliasingMode.Smaa1x));
                Assert.That(settings.FxaaContrastThreshold, Is.EqualTo(0.333f));
                Assert.That(settings.FxaaRelativeThreshold, Is.EqualTo(0.063f));
                Assert.That(settings.FxaaSubpixelBlending, Is.EqualTo(1.0f));
                Assert.That(settings.SmaaThreshold, Is.EqualTo(0.03f));
                Assert.That(settings.SmaaMaxSearchSteps, Is.EqualTo(32));
                Assert.That(settings.SmaaMaxSearchStepsDiagonal, Is.EqualTo(0));
                Assert.That(settings.SmaaCornerRounding, Is.EqualTo(100.0f));
                Assert.That(settings.JitterSampleCount, Is.EqualTo(16));
                Assert.That(settings.TaaFeedbackMin, Is.EqualTo(0.5f));
                Assert.That(settings.TaaFeedbackMax, Is.EqualTo(0.99f));
                Assert.That(settings.TaaVelocityRejectionScale, Is.EqualTo(0.0f));
            });
        }

        [Test]
        public void HdrSceneColorFormat_UsesHalfFloatRgba()
        {
            Assert.That(RenderTargetManager.SceneColorFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
        }

        [Test]
        public void AmbientOcclusionFormat_UsesSingleChannelUnorm()
        {
            Assert.That(RenderTargetManager.AmbientOcclusionFormat, Is.EqualTo(Format.R8Unorm));
        }

        [Test]
        public void AntiAliasingFormats_UseExpectedTargets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderTargetManager.LdrSceneColorFormat, Is.EqualTo(Format.R8G8B8A8Unorm));
                Assert.That(RenderTargetManager.SmaaEdgesFormat, Is.EqualTo(Format.R8G8Unorm));
                Assert.That(RenderTargetManager.SmaaBlendWeightsFormat, Is.EqualTo(Format.R8G8B8A8Unorm));
                Assert.That(RenderTargetManager.MotionVectorFormat, Is.EqualTo(Format.R16G16Sfloat));
            });
        }

        [Test]
        public void RenderTargetByteSize_UsesHalfFloatRgba()
        {
            Assert.That(
                RenderTarget.CalculateByteSize(1920, 1080, Format.R16G16B16A16Sfloat),
                Is.EqualTo(1920UL * 1080UL * 8UL));
            Assert.That(
                RenderTarget.CalculateByteSize(960, 540, Format.R8Unorm),
                Is.EqualTo(960UL * 540UL));
            Assert.That(
                RenderTarget.CalculateByteSize(1920, 1080, Format.R8G8Unorm),
                Is.EqualTo(1920UL * 1080UL * 2UL));
            Assert.That(
                RenderTarget.CalculateByteSize(1920, 1080, Format.R16G16Sfloat),
                Is.EqualTo(1920UL * 1080UL * 4UL));
        }

        [Test]
        public void AntiAliasingJitter_IsCenteredAndResolutionScaled()
        {
            Vector2 disabled = AntiAliasingJitter.GetHaltonJitter(0, 8, 1920, 1080, enabled: false);
            Vector2 sample0 = AntiAliasingJitter.GetHaltonJitter(0, 8, 1920, 1080, enabled: true);
            Vector2 sample1 = AntiAliasingJitter.GetHaltonJitter(1, 8, 1920, 1080, enabled: true);

            Assert.Multiple(() =>
            {
                Assert.That(disabled.X, Is.EqualTo(0));
                Assert.That(disabled.Y, Is.EqualTo(0));
                Assert.That(Math.Abs(sample0.X), Is.LessThanOrEqualTo(0.5f / 1920f));
                Assert.That(Math.Abs(sample0.Y), Is.LessThanOrEqualTo(0.5f / 1080f));
                Assert.That(sample1.X, Is.Not.EqualTo(sample0.X));
            });
        }

        [Test]
        public void AmbientOcclusionExtents_ClampScaleAndRoundUpOddSizes()
        {
            var quarter = RenderTargetManager.CalculateAmbientOcclusionExtent(new Extent2D { Width = 1919, Height = 1079 }, 0.25f);
            var half = RenderTargetManager.CalculateAmbientOcclusionExtent(new Extent2D { Width = 1919, Height = 1079 }, 0.5f);
            var full = RenderTargetManager.CalculateAmbientOcclusionExtent(new Extent2D { Width = 1919, Height = 1079 }, 1.0f);

            Assert.Multiple(() =>
            {
                Assert.That(quarter.Width, Is.EqualTo(480));
                Assert.That(quarter.Height, Is.EqualTo(270));
                Assert.That(half.Width, Is.EqualTo(960));
                Assert.That(half.Height, Is.EqualTo(540));
                Assert.That(full.Width, Is.EqualTo(1919));
                Assert.That(full.Height, Is.EqualTo(1079));
            });
        }

        [Test]
        public void BloomMipExtents_StartAtHalfResolutionAndHandleOddSizes()
        {
            var extents = RenderTargetManager.CalculateBloomMipExtents(new Extent2D { Width = 1919, Height = 1079 }, 4);

            Assert.Multiple(() =>
            {
                Assert.That(extents, Has.Count.EqualTo(4));
                Assert.That(extents[0].Width, Is.EqualTo(959));
                Assert.That(extents[0].Height, Is.EqualTo(539));
                Assert.That(extents[1].Width, Is.EqualTo(479));
                Assert.That(extents[1].Height, Is.EqualTo(269));
                Assert.That(extents[2].Width, Is.EqualTo(239));
                Assert.That(extents[2].Height, Is.EqualTo(134));
                Assert.That(extents[3].Width, Is.EqualTo(119));
                Assert.That(extents[3].Height, Is.EqualTo(67));
            });
        }

        [Test]
        public void BloomMipExtents_StopAtOnePixelAndClampMipCount()
        {
            var extents = RenderTargetManager.CalculateBloomMipExtents(new Extent2D { Width = 1, Height = 1 }, 99);

            Assert.Multiple(() =>
            {
                Assert.That(extents, Has.Count.EqualTo(1));
                Assert.That(extents[0].Width, Is.EqualTo(1));
                Assert.That(extents[0].Height, Is.EqualTo(1));
            });
        }
    }
}
