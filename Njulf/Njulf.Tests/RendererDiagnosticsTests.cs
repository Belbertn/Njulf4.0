using Njulf.Core.Math;
using Njulf.Rendering.Data;
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
                Assert.That(diagnostics.CpuDepthPrePassRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZBuildRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
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
                Assert.That(diagnostics.DownscaledTextureCount, Is.EqualTo(0));
                Assert.That(diagnostics.MaxLoadedTextureDimension, Is.EqualTo(0));
                Assert.That(diagnostics.EstimatedTextureBytes, Is.EqualTo(0));
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
                CpuDepthPrePassRecordMicroseconds = 7,
                CpuHiZBuildRecordMicroseconds = 8,
                CpuLightCullRecordMicroseconds = 9,
                CpuForwardOpaqueRecordMicroseconds = 10,
                CpuTransparentRecordMicroseconds = 11,
                GpuDepthPrePassMicroseconds = 12,
                GpuHiZBuildMicroseconds = 13,
                GpuLightCullMicroseconds = 14,
                GpuForwardOpaqueMicroseconds = 15,
                GpuTransparentMicroseconds = 16,
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
                Assert.That(sceneData.CpuDepthPrePassRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZBuildRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
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
    }
}
