using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Microsoft.Extensions.DependencyInjection;
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
                Assert.That(diagnostics.CpuHiZDepthTransitionMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZPyramidTransitionMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZDescriptorBindMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZPushDispatchMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuHiZFinalBarrierMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomExtractRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomDownsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuBloomUpsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuFogRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuCompositeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDepthPrePassMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuHiZBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuLightCullMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.TiledLightHeaderBufferClearBytes, Is.EqualTo(0));
                Assert.That(diagnostics.TiledLightIndexBufferClearBytes, Is.EqualTo(0));
                Assert.That(diagnostics.MaxLightsInAnyTile, Is.EqualTo(0));
                Assert.That(diagnostics.AverageLightsPerNonEmptyTile, Is.EqualTo(0));
                Assert.That(diagnostics.LightTileSaturationCount, Is.EqualTo(0));
                Assert.That(diagnostics.LightCullRejectedPointCount, Is.EqualTo(0));
                Assert.That(diagnostics.LightCullRejectedSpotCount, Is.EqualTo(0));
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
                Assert.That(diagnostics.ForwardMeshletsSubmittedCpu, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardGpuOcclusionRejectedMeshlets, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardGpuOcclusionCountersReconciled, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardGpuOcclusionSanity, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.HiZConsumerCount, Is.EqualTo(0));
                Assert.That(diagnostics.HiZConsumerSummary, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.HiZBuildSkippedBecauseNoConsumer, Is.EqualTo(0));
                Assert.That(diagnostics.HiZCounterSource, Is.EqualTo(HiZCounterSource.Unavailable));
                Assert.That(diagnostics.ForwardHiZTestedCount, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardHiZCulledCount, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardHiZCullRate, Is.EqualTo(0));
                Assert.That(diagnostics.HiZFallbackPath, Is.EqualTo(HiZFallbackPaths.Disabled));
                Assert.That(diagnostics.HiZFallbackReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.HiZValidateAgainstLegacyPath, Is.EqualTo(0));
                Assert.That(diagnostics.PreviousHiZFrameValid, Is.EqualTo(0));
                Assert.That(diagnostics.PreviousHiZSkippedInvalidHistory, Is.EqualTo(0));
                Assert.That(diagnostics.PreviousHiZSkippedCameraMotion, Is.EqualTo(0));
                Assert.That(diagnostics.PreviousHiZTested, Is.EqualTo(0));
                Assert.That(diagnostics.PreviousHiZCulled, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardVisibilityCompactionEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardVisibilityCompactionActive, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardVisibilityCompactionSkipReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.CurrentFrameHiZTested, Is.EqualTo(0));
                Assert.That(diagnostics.CurrentFrameHiZCulled, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardSimpleMeshletCount, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardFullMaterialMeshletCount, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardLocalProbeMeshletCount, Is.EqualTo(0));
                Assert.That(diagnostics.SceneSubmissionActiveMode, Is.EqualTo(SceneSubmissionMode.Cpu));
                Assert.That(diagnostics.SceneSubmissionForwardPath, Is.EqualTo(SceneSubmissionDiagnosticsPolicy.ForwardPathCpu));
                Assert.That(diagnostics.SceneSubmissionForwardTaskShader, Is.EqualTo(SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderLegacyCull));
                Assert.That(diagnostics.SceneSubmissionCpuCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.SceneSubmissionGpuEmittedCount, Is.EqualTo(0));
                Assert.That(diagnostics.SceneSubmissionIndirectTaskCount, Is.EqualTo(0));
                Assert.That(diagnostics.SceneSubmissionCompactionSkipReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.SceneSubmissionIndirectDispatchSkipReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.LargestTextureAssets, Is.Empty);
                Assert.That(diagnostics.MeshletQualityEntries, Is.Empty);
                Assert.That(diagnostics.SecondaryCommandBufferEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.SecondaryCommandBufferPassCount, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeRequested, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeSupported, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeCandidatePassCount, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeEnabledPassCount, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeQueueOwnershipTransitionCount, Is.EqualTo(0));
                Assert.That(diagnostics.AsyncComputeStatus, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.AsyncComputeCandidatePasses, Is.Empty);
                Assert.That(diagnostics.AsyncComputeEnabledPasses, Is.Empty);
                Assert.That(diagnostics.CpuPrimaryCommandRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSecondaryCommandRecordMicroseconds, Is.EqualTo(0));
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
                Assert.That(diagnostics.StaticInstanceBatchCount, Is.EqualTo(0));
                Assert.That(diagnostics.StaticInstanceCount, Is.EqualTo(0));
                Assert.That(diagnostics.VisibleStaticInstanceCount, Is.EqualTo(0));
                Assert.That(diagnostics.CulledStaticInstanceCount, Is.EqualTo(0));
                Assert.That(diagnostics.StaticBatchMeshletDrawCommandCount, Is.EqualTo(0));
                Assert.That(diagnostics.CpuStaticBatchBuildMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.MaterialUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.MaterialDebugView, Is.EqualTo(MaterialDebugView.None));
                Assert.That(diagnostics.LightUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DepthPrePassEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.HiZEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.OcclusionEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.HiZMipCount, Is.EqualTo(0));
                Assert.That(diagnostics.HiZWidth, Is.EqualTo(0));
                Assert.That(diagnostics.HiZHeight, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyStatus, Is.EqualTo(HiZVisibilityPolicyStatus.Disabled));
                Assert.That(diagnostics.HiZPolicyReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.HiZPolicyWarmupFramesRemaining, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicySceneChanged, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyCameraCut, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyPyramidInvalidated, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveSuppressed, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveProbe, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveProbeCountdown, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveMeasuredOcclusionTests, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveMeasuredOcclusionCulled, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveCullRate, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyCounterSource, Is.EqualTo(HiZCounterSource.Unavailable));
                Assert.That(diagnostics.HiZPolicyAdaptiveEstimatedSavedMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveEstimatedCostMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveEstimatedNetMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveSmoothedCullRate, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveSmoothedSavedToCostRatio, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveSuppressedFrameCount, Is.EqualTo(0));
                Assert.That(diagnostics.HiZPolicyAdaptiveStatus, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DirectionalShadowsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowMapSize, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowCascadeCount, Is.EqualTo(0));
                Assert.That(diagnostics.ShadowedDirectionalLightIndex, Is.EqualTo(-1));
                Assert.That(diagnostics.ShadowDebugView, Is.EqualTo(ShadowDebugView.None));
                Assert.That(diagnostics.ShadowNormalBias, Is.EqualTo(0));
                Assert.That(diagnostics.ShadowSlopeScaledDepthBias, Is.EqualTo(0));
                Assert.That(diagnostics.DirectionalShadowPcfRadius, Is.EqualTo(0));
                Assert.That(diagnostics.SpotShadowPcfRadius, Is.EqualTo(0));
                Assert.That(diagnostics.PointShadowPcfRadius, Is.EqualTo(0));
                Assert.That(diagnostics.ForwardShadowReceiverMeshletCount, Is.EqualTo(0));
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
                Assert.That(diagnostics.PointShadowSkippedFaceCount, Is.EqualTo(0));
                Assert.That(diagnostics.DownscaledTextureCount, Is.EqualTo(0));
                Assert.That(diagnostics.MaxLoadedTextureDimension, Is.EqualTo(0));
                Assert.That(diagnostics.ActiveTextureBudgetProfile, Is.EqualTo(TextureBudgetProfile.Development));
                Assert.That(diagnostics.EstimatedTextureBytes, Is.EqualTo(0));
                Assert.That(diagnostics.RequestedDynamicResolutionScale, Is.EqualTo(1.0f));
                Assert.That(diagnostics.CommittedRenderTargetScale, Is.EqualTo(1.0f));
                Assert.That(diagnostics.LastRenderTargetRecreateReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.StagingOverflowCountThisFrame, Is.EqualTo(0));
                Assert.That(diagnostics.StagingRetainedOverflowBufferCount, Is.EqualTo(0));
                Assert.That(diagnostics.StagingRetainedOverflowBytes, Is.EqualTo(0));
                Assert.That(diagnostics.StagingPeakOverflowBytes, Is.EqualTo(0));
                Assert.That(diagnostics.StagingLargestOverflowAllocationBytes, Is.EqualTo(0));
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
                Assert.That(diagnostics.FogEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.FogMode, Is.EqualTo(FogMode.Disabled));
                Assert.That(diagnostics.FogColorMode, Is.EqualTo(FogColorMode.ConstantColor));
                Assert.That(diagnostics.FogDebugView, Is.EqualTo(FogDebugView.None));
                Assert.That(diagnostics.FogDensity, Is.EqualTo(0));
                Assert.That(diagnostics.FogStartDistance, Is.EqualTo(0));
                Assert.That(diagnostics.FogEndDistance, Is.EqualTo(0));
                Assert.That(diagnostics.FogHeight, Is.EqualTo(0));
                Assert.That(diagnostics.FogHeightFalloff, Is.EqualTo(0));
                Assert.That(diagnostics.FogHeightDensity, Is.EqualTo(0));
                Assert.That(diagnostics.FogMaxOpacity, Is.EqualTo(0));
                Assert.That(diagnostics.FogDirectionalInscatteringEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.FogWidth, Is.EqualTo(0));
                Assert.That(diagnostics.FogHeightPixels, Is.EqualTo(0));
                Assert.That(diagnostics.FogFormat, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.GpuFogMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.AmbientOcclusionMode, Is.EqualTo(AmbientOcclusionMode.Disabled));
                Assert.That(diagnostics.AmbientOcclusionDebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(diagnostics.AmbientOcclusionForwardSamplingMode, Is.EqualTo(AmbientOcclusionForwardSamplingMode.Disabled));
                Assert.That(diagnostics.AmbientOcclusionForwardDepthAwareSamples, Is.EqualTo(0));
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
                Assert.That(diagnostics.ReflectionsEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionMode, Is.EqualTo(ReflectionMode.Disabled));
                Assert.That(diagnostics.ReflectionDebugView, Is.EqualTo(ReflectionDebugView.None));
                Assert.That(diagnostics.ReflectionProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.MaxReflectionProbesPerPixel, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeResolution, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeMipCount, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeEstimatedBytes, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeCapturesQueued, Is.EqualTo(0));
                Assert.That(diagnostics.ReflectionProbeCapturesCompleted, Is.EqualTo(0));
                Assert.That(diagnostics.CpuReflectionProbeUploadMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuReflectionProbeCaptureRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuReflectionProbePrefilterRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuReflectionProbeCaptureMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuReflectionProbePrefilterMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GlobalIlluminationEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.GlobalIlluminationMode, Is.EqualTo(GlobalIlluminationMode.Disabled));
                Assert.That(diagnostics.GlobalIlluminationDebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(diagnostics.GlobalIlluminationRayQuerySupported, Is.EqualTo(0));
                Assert.That(diagnostics.GlobalIlluminationRayQueryActive, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiWidth, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiHeight, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiResolutionScale, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiRayCount, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiHistoryValid, Is.EqualTo(0));
                Assert.That(diagnostics.SsgiRejectedHistoryPixelCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbeVolumeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiActiveProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbesUpdated, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiRaysPerProbe, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiMaxActiveProbeBudget, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbeUpdateRequestBudget, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAsyncComputeEnabled, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGatherSelectedLocalTileFraction, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGatherSelectedClipmapTileFraction, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGatherFallbackTileFraction, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageCoverageEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageVisibleSupportEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageEffectiveContributionEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageRelocationFractionEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiClassifiedInactiveProbeCountEstimate, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbeRelocationCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiProbeClassificationCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiHighVarianceProbeUpdateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiLowConfidenceProbeUpdateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiStableProbeUpdateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageProbeVariability, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiAverageProbeConfidence, Is.EqualTo(0));
                Assert.That(diagnostics.CpuSsgiRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuDdgiRecordMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuDdgiSchedulerMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.CpuDdgiSchedulerP95Microseconds, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiSchedulerTimingSampleCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiSchedulerP95OverBudget, Is.EqualTo(0));
                Assert.That(diagnostics.GpuSsgiTraceMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuSsgiTemporalMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuSsgiDenoiseMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDdgiScheduleMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDdgiScheduleP95Microseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDdgiScheduleOverBudget, Is.EqualTo(0));
                Assert.That(diagnostics.GpuDdgiUpdateMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GpuGiCompositeMicroseconds, Is.EqualTo(0));
                Assert.That(diagnostics.GlobalIlluminationRenderTargetBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiTextureBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerBufferBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerDirtyRegionCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerCandidateCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerGroupCountCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPrefixCapacity, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerDirtyRegionCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerDirtyRegionOverflowCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerTotalResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerUploadBytes, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerReadbackValid, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerReadbackLatencyFrames, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerFallbackActive, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerFallbackReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DdgiGpuSchedulerConsideredProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerRequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPrimaryRayCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerOverflowCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerDuplicateRequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerBudgetRejectedCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerInvalidProbeCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerVisibleFrustumCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerSafetyShellCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerAgeRefreshCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerHighVarianceCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerLowConfidenceCandidateCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerStableSkippedCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPriority0RequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPriority1RequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPriority2RequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPriority3RequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerRequestBudgetSaturated, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerPrimaryRayBudgetSaturated, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationValid, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationStatus, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationCpuRequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationGpuRequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationComparedRequestCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationMismatchCount, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationSampleLimit, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiGpuSchedulerValidationFirstMismatch, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DdgiUpdateExecuted, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiUpdateSkipReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.DdgiPublishExecuted, Is.EqualTo(0));
                Assert.That(diagnostics.DdgiPublishSkipReason, Is.EqualTo(string.Empty));
                Assert.That(diagnostics.AccelerationStructureBytes, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleCountersReadbackValid, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleAliveCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleDeadCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleSpawnedCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleKilledCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleCulledCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleRenderedCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleDroppedSpawnCount, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleBlendBucket0Count, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleBlendBucket1Count, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleBlendBucket2Count, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleBlendBucket3Count, Is.EqualTo(0));
                Assert.That(diagnostics.GpuParticleBlendBucket4Count, Is.EqualTo(0));
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
                CpuHiZDepthTransitionMicroseconds = 101,
                CpuHiZPyramidTransitionMicroseconds = 102,
                CpuHiZDescriptorBindMicroseconds = 103,
                CpuHiZPushDispatchMicroseconds = 104,
                CpuHiZFinalBarrierMicroseconds = 105,
                CpuLightCullRecordMicroseconds = 9,
                CpuForwardOpaqueRecordMicroseconds = 10,
                CpuTransparentRecordMicroseconds = 11,
                CpuBloomExtractRecordMicroseconds = 12,
                CpuBloomDownsampleRecordMicroseconds = 13,
                CpuBloomUpsampleRecordMicroseconds = 14,
                CpuFogRecordMicroseconds = 41,
                CpuCompositeRecordMicroseconds = 15,
                GpuDepthPrePassMicroseconds = 13,
                GpuHiZBuildMicroseconds = 14,
                HiZFallbackPath = HiZFallbackPaths.CurrentFrameForwardVisibility,
                HiZFallbackReason = "active",
                HiZValidateAgainstLegacyPath = true,
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
                StaticInstanceBatchCount = 2,
                StaticInstanceCount = 100,
                VisibleStaticInstanceCount = 80,
                CulledStaticInstanceCount = 20,
                StaticBatchMeshletDrawCommandCount = 320,
                CpuStaticBatchBuildMicroseconds = 33,
                MaterialUploadBytes = 30,
                LightUploadBytes = 31,
                HiZWidth = 32,
                HiZHeight = 33,
                BloomEnabled = true,
                BloomMipCount = 6,
                BloomBaseWidth = 960,
                BloomBaseHeight = 540,
                ActiveSceneColorTextureIndex = BindlessIndex.FoggedSceneColorTexture,
                FogEnabled = true,
                FogMode = FogMode.DistanceAndHeight,
                FogColorMode = FogColorMode.SkyAndConstantBlend,
                FogDebugView = FogDebugView.FogFactor,
                FogDensity = 0.015f,
                FogStartDistance = 5.0f,
                FogEndDistance = 250.0f,
                FogHeight = 0.0f,
                FogHeightFalloff = 0.12f,
                FogHeightDensity = 0.04f,
                FogMaxOpacity = 0.85f,
                FogDirectionalInscatteringEnabled = 1,
                FogWidth = 1920,
                FogHeightPixels = 1080,
                FogFormat = "R16G16B16A16Sfloat",
                GpuFogMicroseconds = 42,
                ReflectionsEnabled = true,
                ReflectionMode = ReflectionMode.StaticProbes,
                ReflectionDebugView = ReflectionDebugView.ProbeInfluence,
                ReflectionProbeCount = 2,
                ReflectionProbeCapacity = 64,
                MaxReflectionProbesPerPixel = 2,
                ReflectionProbeResolution = 256,
                ReflectionProbeMipCount = 9,
                ReflectionProbeEstimatedBytes = 128,
                ReflectionProbeCapturesQueued = 1,
                ReflectionProbeCapturesCompleted = 2,
                CpuReflectionProbeUploadMicroseconds = 43,
                CpuReflectionProbeCaptureRecordMicroseconds = 44,
                CpuReflectionProbePrefilterRecordMicroseconds = 45,
                GpuReflectionProbeCaptureMicroseconds = 46,
                GpuReflectionProbePrefilterMicroseconds = 47,
                AmbientOcclusionEnabled = true,
                AmbientOcclusionMode = AmbientOcclusionMode.Ssao,
                AmbientOcclusionDebugView = AmbientOcclusionDebugView.RawAo,
                AmbientOcclusionForwardSamplingMode = AmbientOcclusionForwardSamplingMode.DepthAwareUpsample,
                AmbientOcclusionForwardDepthAwareSamples = 4,
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
                CpuSsgiRecordMicroseconds = 48,
                SsgiHistoryValid = 1,
                SsgiRejectedHistoryPixelCount = 32,
                GpuSsgiTraceMicroseconds = 49,
                GpuSsgiTemporalMicroseconds = 50,
                AntiAliasingMode = AntiAliasingMode.SmaaMedium,
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
                GpuParticleCountersReadbackValid = 1,
                GpuParticleAliveCount = 11,
                GpuParticleDeadCount = 12,
                GpuParticleSpawnedCount = 13,
                GpuParticleKilledCount = 14,
                GpuParticleCulledCount = 15,
                GpuParticleRenderedCount = 16,
                GpuParticleDroppedSpawnCount = 17,
                GpuParticleBlendBucket0Count = 18,
                GpuParticleBlendBucket1Count = 19,
                GpuParticleBlendBucket2Count = 20,
                GpuParticleBlendBucket3Count = 21,
                GpuParticleBlendBucket4Count = 22,
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
                Assert.That(sceneData.CpuHiZDepthTransitionMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZPyramidTransitionMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZDescriptorBindMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZPushDispatchMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuHiZFinalBarrierMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuLightCullRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuForwardOpaqueRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuTransparentRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomExtractRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomDownsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuBloomUpsampleRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuFogRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuCompositeRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuDepthPrePassMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuHiZBuildMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.HiZFallbackPath, Is.EqualTo(HiZFallbackPaths.Disabled));
                Assert.That(sceneData.HiZFallbackReason, Is.EqualTo(string.Empty));
                Assert.That(sceneData.HiZValidateAgainstLegacyPath, Is.False);
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
                Assert.That(sceneData.StaticInstanceBatchCount, Is.EqualTo(0));
                Assert.That(sceneData.StaticInstanceCount, Is.EqualTo(0));
                Assert.That(sceneData.VisibleStaticInstanceCount, Is.EqualTo(0));
                Assert.That(sceneData.CulledStaticInstanceCount, Is.EqualTo(0));
                Assert.That(sceneData.StaticBatchMeshletDrawCommandCount, Is.EqualTo(0));
                Assert.That(sceneData.CpuStaticBatchBuildMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.MaterialUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.LightUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.HiZWidth, Is.EqualTo(0));
                Assert.That(sceneData.HiZHeight, Is.EqualTo(0));
                Assert.That(sceneData.BloomEnabled, Is.False);
                Assert.That(sceneData.BloomMipCount, Is.EqualTo(0));
                Assert.That(sceneData.BloomBaseWidth, Is.EqualTo(0));
                Assert.That(sceneData.BloomBaseHeight, Is.EqualTo(0));
                Assert.That(sceneData.ActiveSceneColorTextureIndex, Is.EqualTo(0));
                Assert.That(sceneData.FogEnabled, Is.False);
                Assert.That(sceneData.FogMode, Is.EqualTo(FogMode.Disabled));
                Assert.That(sceneData.FogColorMode, Is.EqualTo(FogColorMode.ConstantColor));
                Assert.That(sceneData.FogDebugView, Is.EqualTo(FogDebugView.None));
                Assert.That(sceneData.FogDensity, Is.EqualTo(0));
                Assert.That(sceneData.FogStartDistance, Is.EqualTo(0));
                Assert.That(sceneData.FogEndDistance, Is.EqualTo(0));
                Assert.That(sceneData.FogHeight, Is.EqualTo(0));
                Assert.That(sceneData.FogHeightFalloff, Is.EqualTo(0));
                Assert.That(sceneData.FogHeightDensity, Is.EqualTo(0));
                Assert.That(sceneData.FogMaxOpacity, Is.EqualTo(0));
                Assert.That(sceneData.FogDirectionalInscatteringEnabled, Is.EqualTo(0));
                Assert.That(sceneData.FogWidth, Is.EqualTo(0));
                Assert.That(sceneData.FogHeightPixels, Is.EqualTo(0));
                Assert.That(sceneData.FogFormat, Is.EqualTo(string.Empty));
                Assert.That(sceneData.GpuFogMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionsEnabled, Is.False);
                Assert.That(sceneData.ReflectionMode, Is.EqualTo(ReflectionMode.Disabled));
                Assert.That(sceneData.ReflectionDebugView, Is.EqualTo(ReflectionDebugView.None));
                Assert.That(sceneData.ReflectionProbeCount, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeCapacity, Is.EqualTo(0));
                Assert.That(sceneData.MaxReflectionProbesPerPixel, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeResolution, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeMipCount, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeEstimatedBytes, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeCapturesQueued, Is.EqualTo(0));
                Assert.That(sceneData.ReflectionProbeCapturesCompleted, Is.EqualTo(0));
                Assert.That(sceneData.CpuReflectionProbeUploadMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuReflectionProbeCaptureRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuReflectionProbePrefilterRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuReflectionProbeCaptureMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuReflectionProbePrefilterMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.ForwardSimpleMeshletCount, Is.EqualTo(0));
                Assert.That(sceneData.ForwardFullMaterialMeshletCount, Is.EqualTo(0));
                Assert.That(sceneData.ForwardLocalProbeMeshletCount, Is.EqualTo(0));
                Assert.That(sceneData.TiledLightHeaderBufferClearBytes, Is.EqualTo(0));
                Assert.That(sceneData.TiledLightIndexBufferClearBytes, Is.EqualTo(0));
                Assert.That(sceneData.MaxLightsInAnyTile, Is.EqualTo(0));
                Assert.That(sceneData.AverageLightsPerNonEmptyTile, Is.EqualTo(0));
                Assert.That(sceneData.LightTileSaturationCount, Is.EqualTo(0));
                Assert.That(sceneData.LightCullRejectedPointCount, Is.EqualTo(0));
                Assert.That(sceneData.LightCullRejectedSpotCount, Is.EqualTo(0));
                Assert.That(sceneData.AmbientOcclusionEnabled, Is.False);
                Assert.That(sceneData.AmbientOcclusionMode, Is.EqualTo(AmbientOcclusionMode.Disabled));
                Assert.That(sceneData.AmbientOcclusionDebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(sceneData.AmbientOcclusionForwardSamplingMode, Is.EqualTo(AmbientOcclusionForwardSamplingMode.Disabled));
                Assert.That(sceneData.AmbientOcclusionForwardDepthAwareSamples, Is.EqualTo(0));
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
                Assert.That(sceneData.CpuSsgiRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuDdgiRecordMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGatherSelectedLocalTileFraction, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGatherSelectedClipmapTileFraction, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGatherFallbackTileFraction, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageCoverageEstimate, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageVisibleSupportEstimate, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageEffectiveContributionEstimate, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageRelocationFractionEstimate, Is.EqualTo(0));
                Assert.That(sceneData.DdgiClassifiedInactiveProbeCountEstimate, Is.EqualTo(0));
                Assert.That(sceneData.DdgiHighVarianceProbeUpdateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiLowConfidenceProbeUpdateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiStableProbeUpdateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageProbeVariability, Is.EqualTo(0));
                Assert.That(sceneData.DdgiAverageProbeConfidence, Is.EqualTo(0));
                Assert.That(sceneData.CpuDdgiSchedulerMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.CpuDdgiSchedulerP95Microseconds, Is.EqualTo(0));
                Assert.That(sceneData.DdgiSchedulerTimingSampleCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiSchedulerP95OverBudget, Is.EqualTo(0));
                Assert.That(sceneData.SsgiHistoryValid, Is.EqualTo(0));
                Assert.That(sceneData.SsgiRejectedHistoryPixelCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuSsgiTraceMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuSsgiTemporalMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuSsgiDenoiseMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuDdgiScheduleMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuDdgiScheduleP95Microseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuDdgiScheduleOverBudget, Is.EqualTo(0));
                Assert.That(sceneData.GpuDdgiUpdateMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.GpuGiCompositeMicroseconds, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerBufferBytes, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerDirtyRegionCapacity, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerCandidateCapacity, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerGroupCountCapacity, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPrefixCapacity, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerDirtyRegionCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerDirtyRegionOverflowCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerTotalResourceReinitializationCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerUploadBytes, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerReadbackValid, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerReadbackLatencyFrames, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerFallbackActive, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerFallbackReason, Is.EqualTo(string.Empty));
                Assert.That(sceneData.DdgiGpuSchedulerConsideredProbeCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerRequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPrimaryRayCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerOverflowCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerDuplicateRequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerBudgetRejectedCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerInvalidProbeCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerVisibleFrustumCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerSafetyShellCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerAgeRefreshCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerHighVarianceCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerLowConfidenceCandidateCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerStableSkippedCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPriority0RequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPriority1RequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPriority2RequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPriority3RequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerRequestBudgetSaturated, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerPrimaryRayBudgetSaturated, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationValid, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationStatus, Is.EqualTo(string.Empty));
                Assert.That(sceneData.DdgiGpuSchedulerValidationCpuRequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationGpuRequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationComparedRequestCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationMismatchCount, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationSampleLimit, Is.EqualTo(0));
                Assert.That(sceneData.DdgiGpuSchedulerValidationFirstMismatch, Is.EqualTo(string.Empty));
                Assert.That(sceneData.DdgiUpdateExecuted, Is.EqualTo(0));
                Assert.That(sceneData.DdgiUpdateSkipReason, Is.EqualTo(string.Empty));
                Assert.That(sceneData.DdgiPublishExecuted, Is.EqualTo(0));
                Assert.That(sceneData.DdgiPublishSkipReason, Is.EqualTo(string.Empty));
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
                Assert.That(sceneData.GpuParticleCountersReadbackValid, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleAliveCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleDeadCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleSpawnedCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleKilledCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleCulledCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleRenderedCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleDroppedSpawnCount, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleBlendBucket0Count, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleBlendBucket1Count, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleBlendBucket2Count, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleBlendBucket3Count, Is.EqualTo(0));
                Assert.That(sceneData.GpuParticleBlendBucket4Count, Is.EqualTo(0));
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
        public void SelectMeshletLodLevel_UsesHysteresisAroundThresholds()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(12.5f, previousLodLevel: 0, hysteresisFraction: 0.15f), Is.EqualTo(0));
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(14.0f, previousLodLevel: 0, hysteresisFraction: 0.15f), Is.EqualTo(1));
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(29.0f, previousLodLevel: 2, hysteresisFraction: 0.15f), Is.EqualTo(2));
                Assert.That(SceneDataBuilder.SelectMeshletLodLevel(26.0f, previousLodLevel: 2, hysteresisFraction: 0.15f), Is.EqualTo(1));
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
                    "SceneOpaqueCompactionPass",
                    "DirectionalShadowPass",
                    "SpotShadowPass",
                    "PointShadowPass",
                    "DepthPrePass",
                    "MotionVectorPass",
                    "HiZBuildPass",
                    "ForwardVisibilityCompactionPass",
                    "SceneSurfacePass",
                    "AmbientOcclusionPass",
                    "AmbientOcclusionBlurPass",
                    "TiledLightCullingPass",
                    "ForwardPlusPass",
                    "SsgiTracePass",
                    "SsgiTemporalPass",
                    "SsgiDenoisePass",
                    "SsgiCompositePass",
                    "DdgiSchedulePass",
                    "DdgiTracePass",
                    "DdgiBlendPass",
                    "DdgiRelocateClassifyPass",
                    "DdgiPublishPass",
                    "SkyboxPass",
                    "TransparentForwardPass",
                    "WeightedTransparentPass",
                    "WeightedOitCompositePass",
                    "ParticlePass",
                    "DebugDrawPass",
                    "FogPass",
                    "AutoExposurePass",
                    "BloomPass",
                    "ToneMapCompositePass",
                    "AntiAliasingPass"
                }));
        }

        [Test]
        public void RenderSettings_DefaultsUseDdgiProductionProfileAndAcesHdrPipeline()
        {
            var settings = new RenderSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Exposure, Is.EqualTo(1.0f));
                Assert.That(settings.ToneMapper, Is.EqualTo(ToneMapper.AcesFitted));
                Assert.That(settings.ShowRawHdrSceneColor, Is.False);
                Assert.That(settings.AutoExposure.Enabled, Is.False);
                Assert.That(settings.AutoExposure.TargetLuminance, Is.EqualTo(0.18f));
                Assert.That(settings.AutoExposure.MinExposure, Is.EqualTo(0.05f));
                Assert.That(settings.AutoExposure.MaxExposure, Is.EqualTo(16.0f));
                Assert.That(settings.AutoExposure.AdaptationSpeed, Is.EqualTo(3.0f));
                Assert.That(settings.AutoExposure.MinLogLuminance, Is.EqualTo(-10.0f));
                Assert.That(settings.AutoExposure.MaxLogLuminance, Is.EqualTo(4.0f));
                Assert.That(settings.AutoExposure.SamplingStride, Is.EqualTo(4));
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
                Assert.That(settings.Reflections.Enabled, Is.True);
                Assert.That(settings.Reflections.Mode, Is.EqualTo(ReflectionMode.StaticProbes));
                Assert.That(settings.Reflections.MaxProbes, Is.EqualTo(8));
                Assert.That(settings.Reflections.MaxProbesPerPixel, Is.EqualTo(2));
                Assert.That(settings.Reflections.ProbeResolution, Is.EqualTo(128));
                Assert.That(settings.Reflections.Intensity, Is.EqualTo(1.0f));
                Assert.That(settings.Reflections.GlobalFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.Reflections.BoxProjectionEnabled, Is.True);
                Assert.That(settings.Reflections.ProbeBlendingEnabled, Is.True);
                Assert.That(settings.Reflections.CaptureOnLoad, Is.False);
                Assert.That(settings.Reflections.MaxProbeCapturesPerFrame, Is.EqualTo(0));
                Assert.That(settings.Reflections.DebugView, Is.EqualTo(ReflectionDebugView.None));
                Assert.That(settings.Reflections.DebugProbeIndex, Is.EqualTo(0));
                Assert.That(settings.Reflections.DebugCubemapFace, Is.EqualTo(0));
                Assert.That(settings.Reflections.DebugMipLevel, Is.EqualTo(0));
                Assert.That(settings.AmbientOcclusion.Enabled, Is.True);
                Assert.That(settings.AmbientOcclusion.Mode, Is.EqualTo(AmbientOcclusionMode.Ssao));
                Assert.That(settings.AmbientOcclusion.ResolutionScale, Is.EqualTo(1.0f));
                Assert.That(settings.AmbientOcclusion.Radius, Is.EqualTo(0.75f));
                Assert.That(settings.AmbientOcclusion.Intensity, Is.EqualTo(1.0f));
                Assert.That(settings.AmbientOcclusion.Bias, Is.EqualTo(0.03f));
                Assert.That(settings.AmbientOcclusion.Power, Is.EqualTo(1.2f));
                Assert.That(settings.AmbientOcclusion.SampleCount, Is.EqualTo(32));
                Assert.That(settings.AmbientOcclusion.BlurRadius, Is.EqualTo(2));
                Assert.That(settings.AmbientOcclusion.DepthSigma, Is.EqualTo(2.0f));
                Assert.That(settings.AmbientOcclusion.NormalSigma, Is.EqualTo(32.0f));
                Assert.That(settings.AmbientOcclusion.UseSceneNormals, Is.False);
                Assert.That(settings.AmbientOcclusion.DebugView, Is.EqualTo(AmbientOcclusionDebugView.None));
                Assert.That(settings.GlobalIllumination.Enabled, Is.True);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiProbeClassificationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiProbeRelocationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiAsyncComputeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiExhaustiveGatherFallbackEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
                Assert.That(settings.GlobalIllumination.DdgiSchedulerMode, Is.EqualTo(DdgiSchedulerMode.Gpu));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerReadbackValidationEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiClipmapCascadeCount, Is.EqualTo(3));
                Assert.That(settings.GlobalIllumination.DdgiClipmapProbeCountX, Is.EqualTo(24));
                Assert.That(settings.GlobalIllumination.DdgiClipmapProbeCountY, Is.EqualTo(10));
                Assert.That(settings.GlobalIllumination.DdgiClipmapProbeCountZ, Is.EqualTo(24));
                Assert.That(settings.GlobalIllumination.DdgiClipmapBaseSpacing, Is.EqualTo(1.25f));
                Assert.That(settings.GlobalIllumination.DdgiClipmapSpacingScale, Is.EqualTo(2.0f));
                Assert.That(settings.GlobalIllumination.DdgiClipmapVerticalCenterOffset, Is.EqualTo(0.0f));
                Assert.That(settings.GlobalIllumination.DdgiClipmapEdgeBlendFraction, Is.EqualTo(0.15f));
                Assert.That(settings.GlobalIllumination.DdgiClipmapSafetyMarginCells, Is.EqualTo(2));
                Assert.That(settings.GlobalIllumination.DdgiFrustumPriorityWeight, Is.EqualTo(2.0f));
                Assert.That(settings.GlobalIllumination.DdgiOutOfFrustumMinimumUpdateFraction, Is.EqualTo(0.2f));
                Assert.That(settings.GlobalIllumination.DdgiNewProbeUpdateBoost, Is.EqualTo(4.0f));
                Assert.That(settings.GlobalIllumination.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(1.5f));
                Assert.That(settings.GlobalIllumination.DdgiGpuScheduleTimeBudgetMilliseconds, Is.EqualTo(0.25f));
                Assert.That(settings.GlobalIllumination.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(1.5f));
                Assert.That(settings.GlobalIllumination.DdgiTeleportResetDistance, Is.EqualTo(50.0f));
                Assert.That(settings.GlobalIllumination.DdgiCameraCutResetEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(1024));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions, Is.EqualTo(1024));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount, Is.EqualTo(16));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerFallbackOnValidationFailure, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerForceCpuFallback, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerAutoRetryAfterFallback, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerValidationFailureThreshold, Is.EqualTo(3));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerFallbackRetryStableFrames, Is.EqualTo(300));
                Assert.That(settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(32768));
                Assert.That(settings.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(512));
                Assert.That(settings.GlobalIllumination.DdgiColdStartPrimaryRayBudget, Is.EqualTo(65536));
                Assert.That(settings.GlobalIllumination.DdgiMaxRaysPerProbe, Is.EqualTo(96));
                Assert.That(settings.GlobalIllumination.DdgiMaxShadedLights, Is.EqualTo(8));
                Assert.That(settings.GlobalIllumination.DdgiMaterialTextureMaxCascade, Is.EqualTo(1));
                Assert.That(settings.GlobalIllumination.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(10.0f));
                Assert.That(settings.GlobalIllumination.SsgiMaxDistance, Is.EqualTo(3.0f));
                Assert.That(settings.GlobalIllumination.SsgiThickness, Is.EqualTo(0.04f));
                Assert.That(settings.GlobalIllumination.SsgiHitNormalThreshold, Is.EqualTo(0.15f));
                Assert.That(settings.GlobalIllumination.TemporalEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.DenoiserEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.HistoryResponsiveness, Is.EqualTo(0.18f));
                Assert.That(settings.GlobalIllumination.NormalRejectionThreshold, Is.EqualTo(0.85f));
                Assert.That(settings.GlobalIllumination.DepthRejectionThreshold, Is.EqualTo(0.08f));
                Assert.That(settings.GlobalIllumination.LeakClampStrength, Is.EqualTo(0.75f));
                Assert.That(settings.GlobalIllumination.DdgiThinWallPolicyEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiRoomSpacingScaledBiasEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiThinWallProxyThickness, Is.EqualTo(0.12f));
                Assert.That(settings.GlobalIllumination.DdgiThinWallLeakClampStrength, Is.EqualTo(0.9f));
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
                Assert.That(settings.AntiAliasing.Mode, Is.EqualTo(AntiAliasingMode.SmaaHigh));
                Assert.That(settings.AntiAliasing.EffectiveMode, Is.EqualTo(AntiAliasingMode.SmaaHigh));
                Assert.That(settings.AntiAliasing.EffectiveSmaaQuality, Is.EqualTo(2));
                Assert.That(settings.AntiAliasing.EffectiveSmaaSpatialSampleCount, Is.EqualTo(4));
                Assert.That(settings.AntiAliasing.EffectiveSmaaUsesSpatialMultisampling, Is.True);
                Assert.That(settings.AntiAliasing.DebugView, Is.EqualTo(AntiAliasingDebugView.None));
                Assert.That(settings.AntiAliasing.FxaaContrastThreshold, Is.EqualTo(0.125f));
                Assert.That(settings.AntiAliasing.FxaaRelativeThreshold, Is.EqualTo(0.166f));
                Assert.That(settings.AntiAliasing.FxaaSubpixelBlending, Is.EqualTo(0.75f));
                Assert.That(settings.AntiAliasing.EffectiveSmaaThreshold, Is.EqualTo(0.1f));
                Assert.That(settings.AntiAliasing.EffectiveSmaaMaxSearchSteps, Is.EqualTo(16));
                Assert.That(settings.AntiAliasing.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(8));
                Assert.That(settings.AntiAliasing.EffectiveSmaaCornerRounding, Is.EqualTo(25.0f));
                Assert.That(settings.AntiAliasing.EffectiveSmaaDiagonalEnabled, Is.True);
                Assert.That(settings.AntiAliasing.EffectiveSmaaCornerEnabled, Is.True);
                Assert.That(settings.AntiAliasing.JitterEnabled, Is.True);
                Assert.That(settings.AntiAliasing.JitterSampleCount, Is.EqualTo(8));
                Assert.That(settings.AntiAliasing.TaaFeedbackMin, Is.EqualTo(0.32f));
                Assert.That(settings.AntiAliasing.TaaFeedbackMax, Is.EqualTo(0.64f));
                Assert.That(settings.Fog.Enabled, Is.False);
                Assert.That(settings.Fog.Mode, Is.EqualTo(FogMode.DistanceAndHeight));
                Assert.That(settings.Fog.ColorMode, Is.EqualTo(FogColorMode.SkyAndConstantBlend));
                Assert.That(settings.Fog.Color, Is.EqualTo(new Vector3(0.62f, 0.72f, 0.82f)));
                Assert.That(settings.Fog.ColorBlend, Is.EqualTo(0.5f));
                Assert.That(settings.Fog.Density, Is.EqualTo(0.015f));
                Assert.That(settings.Fog.StartDistance, Is.EqualTo(5.0f));
                Assert.That(settings.Fog.EndDistance, Is.EqualTo(250.0f));
                Assert.That(settings.Fog.Height, Is.EqualTo(0.0f));
                Assert.That(settings.Fog.HeightFalloff, Is.EqualTo(0.12f));
                Assert.That(settings.Fog.HeightDensity, Is.EqualTo(0.04f));
                Assert.That(settings.Fog.MaxOpacity, Is.EqualTo(0.85f));
                Assert.That(settings.Fog.DirectionalInscatteringEnabled, Is.True);
                Assert.That(settings.Fog.DirectionalInscatteringColor, Is.EqualTo(new Vector3(1.0f, 0.88f, 0.68f)));
                Assert.That(settings.Fog.DirectionalInscatteringDirection, Is.EqualTo(Vector3.Zero));
                Assert.That(settings.Fog.DirectionalInscatteringIntensity, Is.EqualTo(0.35f));
                Assert.That(settings.Fog.DirectionalInscatteringExponent, Is.EqualTo(8.0f));
                Assert.That(settings.Fog.DebugView, Is.EqualTo(FogDebugView.None));
                Assert.That(settings.Shadows.DirectionalShadowsEnabled, Is.True);
                Assert.That(settings.Shadows.DirectionalShadowMapSize, Is.EqualTo(2048));
                Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(3));
                Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(80f));
                Assert.That(settings.Shadows.NormalBias, Is.EqualTo(0.03f));
                Assert.That(settings.Shadows.SlopeScaledDepthBias, Is.EqualTo(1.5f));
                Assert.That(settings.Shadows.ConstantDepthBias, Is.EqualTo(0.0005f));
                Assert.That(settings.Shadows.PcfRadius, Is.EqualTo(1));
                Assert.That(settings.Shadows.SpotShadowsEnabled, Is.True);
                Assert.That(settings.Shadows.MaxShadowedSpotLights, Is.EqualTo(3));
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
                Assert.That(settings.Materials.DebugView, Is.EqualTo(MaterialDebugView.None));
                Assert.That(settings.Foliage.Enabled, Is.True);
                Assert.That(settings.Foliage.GpuDrivenEnabled, Is.True);
                Assert.That(settings.Foliage.HiZCullingEnabled, Is.True);
                Assert.That(settings.Foliage.CastShadows, Is.True);
                Assert.That(settings.Foliage.IndirectMeshletDispatchEnabled, Is.True);
                Assert.That(settings.Foliage.FarImpostorsEnabled, Is.True);
                Assert.That(settings.Foliage.MotionVectorsEnabled, Is.True);
                Assert.That(settings.Foliage.LocalShadowsEnabled, Is.True);
                Assert.That(settings.Foliage.GrassShadowDistance, Is.EqualTo(25f));
                Assert.That(settings.Foliage.GrassShadowDensityScale, Is.EqualTo(0.5f));
                Assert.That(settings.Foliage.MaxDrawDistance, Is.EqualTo(250f));
                Assert.That(settings.Foliage.DensityScale, Is.EqualTo(1.0f));
                Assert.That(settings.Foliage.MaxVisibleClusters, Is.EqualTo(262144));
                Assert.That(settings.Foliage.MaxVisibleMeshletDraws, Is.EqualTo(524288));
                Assert.That(settings.Foliage.MaxLocalShadowedSpotLights, Is.EqualTo(1));
                Assert.That(settings.Foliage.MaxLocalShadowedPointLights, Is.EqualTo(1));
                Assert.That(settings.Foliage.MaxLocalShadowClusters, Is.EqualTo(4096));
                Assert.That(settings.Foliage.MaxLocalShadowMeshletDraws, Is.EqualTo(8192));
                Assert.That(settings.Foliage.DebugView, Is.EqualTo(FoliageDebugView.None));
                Assert.That(settings.GlobalIllumination.Enabled, Is.True);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(10.0f));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
                Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
                Assert.That(settings.ResolutionScale, Is.EqualTo(1.0f));
                Assert.That(settings.EffectiveResolutionScale, Is.EqualTo(1.0f));
                Assert.That(settings.DynamicResolution.Enabled, Is.False);
                Assert.That(settings.FeatureIsolation, Is.EqualTo(RenderFeatureIsolationMode.FullFrame));
                Assert.That(settings.UseSecondaryCommandBuffers, Is.True);
                Assert.That(settings.UseCameraDependentCpuScenePayload, Is.True);
                Assert.That(settings.UseCpuMeshletFrustumCulling, Is.True);
                Assert.That(settings.SceneSubmission.GpuCompactionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.IndirectMeshletDispatchEnabled, Is.True);
                Assert.That(settings.SceneSubmission.GpuLodSelectionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.GpuShadowCompactionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.ValidationCompareCpuGpuLists, Is.False);
            });
        }

        [Test]
        public void RenderSettings_QualityPresetsApplyExpectedBudgets()
        {
            var settings = new RenderSettings();

            settings.ApplyQualityPreset(RenderQualityPreset.Low);
            Assert.Multiple(() =>
            {
                Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.Low));
                Assert.That(settings.ResolutionScale, Is.EqualTo(0.75f));
                Assert.That(settings.DynamicResolution.Enabled, Is.True);
                Assert.That(settings.EffectiveResolutionScale, Is.EqualTo(0.75f));
                Assert.That(settings.Bloom.Enabled, Is.False);
                Assert.That(settings.GlobalIllumination.Enabled, Is.False);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Disabled));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(0.0f));
                Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(3.0f));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiLow));
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.False);
                Assert.That(settings.AntiAliasing.Mode, Is.EqualTo(AntiAliasingMode.Fxaa));
                Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(1));
                Assert.That(settings.Foliage.DensityScale, Is.EqualTo(0.45f));
                Assert.That(settings.Foliage.MaxDrawDistance, Is.EqualTo(90f));
                Assert.That(settings.Foliage.GrassShadowDistance, Is.EqualTo(0f));
                Assert.That(settings.Foliage.GrassShadowDensityScale, Is.EqualTo(0f));
                Assert.That(settings.Foliage.CastShadows, Is.False);
                Assert.That(settings.Foliage.LocalShadowsEnabled, Is.False);
                Assert.That(settings.Foliage.MaxVisibleClusters, Is.EqualTo(65536));
                Assert.That(settings.Foliage.MaxVisibleMeshletDraws, Is.EqualTo(131072));
            });

            settings.ApplyQualityPreset(RenderQualityPreset.Medium);
            Assert.Multiple(() =>
            {
                Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.Medium));
                Assert.That(settings.Fog.Enabled, Is.False);
                Assert.That(settings.GlobalIllumination.Enabled, Is.True);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ssgi));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.GlobalIllumination.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(0.75f));
                Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(4.0f));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiMedium));
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.False);
            });

            settings.ApplyQualityPreset(RenderQualityPreset.Ultra);
            Assert.Multiple(() =>
            {
                Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.Ultra));
                Assert.That(settings.Fog.Enabled, Is.False);
                Assert.That(settings.ResolutionScale, Is.EqualTo(1.0f));
                Assert.That(settings.DynamicResolution.Enabled, Is.False);
                Assert.That(settings.AmbientOcclusion.Enabled, Is.True);
                Assert.That(settings.AmbientOcclusion.ResolutionScale, Is.EqualTo(1.0f));
                Assert.That(settings.AmbientOcclusion.SampleCount, Is.EqualTo(32));
                Assert.That(settings.GlobalIllumination.Enabled, Is.True);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.RayQueryHybrid));
                Assert.That(settings.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.None));
                Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
                Assert.That(settings.GlobalIllumination.ResolutionScale, Is.EqualTo(0.5f));
                Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(10.0f));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiUltra));
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
                Assert.That(settings.Bloom.Enabled, Is.True);
                Assert.That(settings.Bloom.MipCount, Is.EqualTo(8));
                Assert.That(settings.AntiAliasing.Mode, Is.EqualTo(AntiAliasingMode.SmaaHigh));
                Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(ShadowSettings.MaxDirectionalCascades));
                Assert.That(settings.Foliage.DensityScale, Is.EqualTo(1.5f));
                Assert.That(settings.Foliage.MaxDrawDistance, Is.EqualTo(400f));
                Assert.That(settings.Foliage.GrassShadowDistance, Is.EqualTo(45f));
                Assert.That(settings.Foliage.GrassShadowDensityScale, Is.EqualTo(0.75f));
                Assert.That(settings.Foliage.CastShadows, Is.True);
                Assert.That(settings.Foliage.LocalShadowsEnabled, Is.True);
                Assert.That(settings.Foliage.MaxLocalShadowedSpotLights, Is.EqualTo(2));
                Assert.That(settings.Foliage.MaxLocalShadowedPointLights, Is.EqualTo(1));
                Assert.That(settings.Foliage.MaxVisibleClusters, Is.EqualTo(524288));
                Assert.That(settings.Foliage.MaxVisibleMeshletDraws, Is.EqualTo(1048576));
                Assert.That(settings.SceneSubmission.GpuCompactionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.IndirectMeshletDispatchEnabled, Is.True);
                Assert.That(settings.SceneSubmission.GpuLodSelectionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.GpuShadowCompactionEnabled, Is.True);
                Assert.That(settings.SceneSubmission.ValidationCompareCpuGpuLists, Is.False);
            });

            settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
            Assert.Multiple(() =>
            {
                Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
                Assert.That(settings.GlobalIllumination.Enabled, Is.True);
                Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
                Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiProbeClassificationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiProbeRelocationEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiAsyncComputeEnabled, Is.True);
                Assert.That(settings.GlobalIllumination.DdgiExhaustiveGatherFallbackEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
                Assert.That(settings.GlobalIllumination.DdgiSchedulerMode, Is.EqualTo(DdgiSchedulerMode.Gpu));
                Assert.That(settings.GlobalIllumination.DdgiGpuSchedulerReadbackValidationEnabled, Is.False);
                Assert.That(settings.GlobalIllumination.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(1024));
                Assert.That(settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(32768));
                Assert.That(settings.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(512));
                Assert.That(settings.GlobalIllumination.DdgiColdStartPrimaryRayBudget, Is.EqualTo(65536));
                Assert.That(settings.GlobalIllumination.DdgiMaxRaysPerProbe, Is.EqualTo(96));
                Assert.That(settings.GlobalIllumination.DdgiCascade0MaxRayDistance, Is.EqualTo(18.0f));
                Assert.That(settings.GlobalIllumination.DdgiCascade1MaxRayDistance, Is.EqualTo(32.0f));
                Assert.That(settings.GlobalIllumination.DdgiCascade2MaxRayDistance, Is.EqualTo(56.0f));
                Assert.That(settings.GlobalIllumination.DdgiCascade3MaxRayDistance, Is.EqualTo(96.0f));
                Assert.That(settings.GlobalIllumination.DdgiMaxShadedLights, Is.EqualTo(8));
                Assert.That(settings.GlobalIllumination.DdgiMaterialTextureMaxCascade, Is.EqualTo(1));
                Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
                Assert.That(settings.AmbientOcclusion.Enabled, Is.True);
                Assert.That(settings.Reflections.Enabled, Is.True);
            });
        }

        [Test]
        public void GlobalIlluminationSettings_DdgiQualityTiersAreMonotonic()
        {
            GlobalIlluminationSettings low = CreateTierSettings(DdgiQualityTier.DdgiLow);
            GlobalIlluminationSettings medium = CreateTierSettings(DdgiQualityTier.DdgiMedium);
            GlobalIlluminationSettings high = CreateTierSettings(DdgiQualityTier.DdgiHigh);
            GlobalIlluminationSettings ultra = CreateTierSettings(DdgiQualityTier.DdgiUltra);

            Assert.Multiple(() =>
            {
                Assert.That(low.DdgiMaxActiveProbes, Is.LessThan(medium.DdgiMaxActiveProbes));
                Assert.That(medium.DdgiMaxActiveProbes, Is.LessThan(high.DdgiMaxActiveProbes));
                Assert.That(high.DdgiMaxActiveProbes, Is.LessThan(ultra.DdgiMaxActiveProbes));
                Assert.That(low.DdgiMaxProbeUpdatesPerFrame, Is.LessThan(medium.DdgiMaxProbeUpdatesPerFrame));
                Assert.That(medium.DdgiMaxProbeUpdatesPerFrame, Is.LessThan(high.DdgiMaxProbeUpdatesPerFrame));
                Assert.That(high.DdgiMaxProbeUpdatesPerFrame, Is.LessThan(ultra.DdgiMaxProbeUpdatesPerFrame));
                Assert.That(low.DdgiProbeUpdatePrimaryRayBudget, Is.LessThan(medium.DdgiProbeUpdatePrimaryRayBudget));
                Assert.That(medium.DdgiProbeUpdatePrimaryRayBudget, Is.LessThan(high.DdgiProbeUpdatePrimaryRayBudget));
                Assert.That(high.DdgiProbeUpdatePrimaryRayBudget, Is.LessThan(ultra.DdgiProbeUpdatePrimaryRayBudget));
                Assert.That(low.DdgiMaxRaysPerProbe, Is.LessThan(medium.DdgiMaxRaysPerProbe));
                Assert.That(medium.DdgiMaxRaysPerProbe, Is.LessThan(high.DdgiMaxRaysPerProbe));
                Assert.That(high.DdgiMaxRaysPerProbe, Is.LessThanOrEqualTo(ultra.DdgiMaxRaysPerProbe));
                Assert.That(low.DdgiProbeUpdateTimeBudgetMilliseconds, Is.LessThan(medium.DdgiProbeUpdateTimeBudgetMilliseconds));
                Assert.That(medium.DdgiProbeUpdateTimeBudgetMilliseconds, Is.LessThan(high.DdgiProbeUpdateTimeBudgetMilliseconds));
                Assert.That(high.DdgiProbeUpdateTimeBudgetMilliseconds, Is.LessThan(ultra.DdgiProbeUpdateTimeBudgetMilliseconds));
                Assert.That(low.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.LessThan(medium.DdgiGpuTotalUpdateTimeBudgetMilliseconds));
                Assert.That(medium.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.LessThan(high.DdgiGpuTotalUpdateTimeBudgetMilliseconds));
                Assert.That(high.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.LessThan(ultra.DdgiGpuTotalUpdateTimeBudgetMilliseconds));
                Assert.That(low.DdgiMaxShadedLights, Is.LessThan(medium.DdgiMaxShadedLights));
                Assert.That(medium.DdgiMaxShadedLights, Is.LessThan(high.DdgiMaxShadedLights));
                Assert.That(high.DdgiMaxShadedLights, Is.LessThan(ultra.DdgiMaxShadedLights));
                Assert.That(low.DdgiAtlasMemoryBudgetBytes, Is.LessThan(medium.DdgiAtlasMemoryBudgetBytes));
                Assert.That(medium.DdgiAtlasMemoryBudgetBytes, Is.LessThan(high.DdgiAtlasMemoryBudgetBytes));
                Assert.That(high.DdgiAtlasMemoryBudgetBytes, Is.LessThan(ultra.DdgiAtlasMemoryBudgetBytes));
                Assert.That(low.DdgiMinimumProbeRefreshFrames, Is.GreaterThan(medium.DdgiMinimumProbeRefreshFrames));
                Assert.That(medium.DdgiMinimumProbeRefreshFrames, Is.GreaterThan(high.DdgiMinimumProbeRefreshFrames));
                Assert.That(high.DdgiMinimumProbeRefreshFrames, Is.GreaterThan(ultra.DdgiMinimumProbeRefreshFrames));
                Assert.That(low.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(64UL * 1024UL * 1024UL));
                Assert.That(medium.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(128UL * 1024UL * 1024UL));
                Assert.That(high.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(192UL * 1024UL * 1024UL));
                Assert.That(ultra.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(384UL * 1024UL * 1024UL));
                Assert.That(high.DdgiClipmapCascadeCount, Is.EqualTo(3));
                Assert.That(high.DdgiClipmapProbeCountX, Is.EqualTo(24));
                Assert.That(high.DdgiClipmapProbeCountY, Is.EqualTo(10));
                Assert.That(high.DdgiClipmapProbeCountZ, Is.EqualTo(24));
                Assert.That(high.DdgiMaxActiveProbes, Is.EqualTo(20480));
                Assert.That(high.DdgiMaxActiveProbes, Is.InRange(16_384, 24_576));
                Assert.That(ultra.DdgiMaxActiveProbes, Is.EqualTo(32_768));
                Assert.That(high.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(1024));
                Assert.That(high.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(32768));
                Assert.That(high.DdgiMaxRaysPerProbe, Is.EqualTo(96));
                Assert.That(high.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(1.5f));
                Assert.That(high.DdgiGpuScheduleTimeBudgetMilliseconds, Is.EqualTo(0.25f));
                Assert.That(high.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(1.5f));
            });
        }

        private static GlobalIlluminationSettings CreateTierSettings(DdgiQualityTier tier)
        {
            var settings = new GlobalIlluminationSettings();
            settings.ApplyDdgiQualityTier(tier);
            return settings;
        }

        [Test]
        public void RenderSettings_SaveLoadRoundTripsShippingOptions()
        {
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"render-settings-{Guid.NewGuid():N}.json");
            try
            {
                var settings = new RenderSettings
                {
                    Exposure = 1.5f,
                    ResolutionScale = 0.8f,
                    ToneMapper = ToneMapper.Reinhard
                };
                settings.ApplyQualityPreset(RenderQualityPreset.Medium);
                settings.ResolutionScale = 0.8f;
                settings.DynamicResolution.Enabled = true;
                settings.DynamicResolution.MinimumScale = 0.6f;
                settings.DynamicResolution.MaximumScale = 0.95f;
                settings.AutoExposure.Enabled = true;
                settings.Bloom.Enabled = false;
                settings.Foliage.Enabled = false;
                settings.Foliage.GpuDrivenEnabled = false;
                settings.Foliage.HiZCullingEnabled = false;
                settings.Foliage.CastShadows = false;
                settings.Foliage.IndirectMeshletDispatchEnabled = false;
                settings.Foliage.FarImpostorsEnabled = false;
                settings.Foliage.MotionVectorsEnabled = true;
                settings.Foliage.LocalShadowsEnabled = true;
                settings.Foliage.GrassShadowDistance = 12.5f;
                settings.Foliage.GrassShadowDensityScale = 0.25f;
                settings.Foliage.MaxDrawDistance = 123.0f;
                settings.Foliage.DensityScale = 0.65f;
                settings.Foliage.MaxVisibleClusters = 12345;
                settings.Foliage.MaxVisibleMeshletDraws = 67890;
                settings.Foliage.MaxLocalShadowedSpotLights = 3;
                settings.Foliage.MaxLocalShadowedPointLights = 2;
                settings.Foliage.MaxLocalShadowClusters = 4321;
                settings.Foliage.MaxLocalShadowMeshletDraws = 8765;
                settings.Foliage.DebugView = FoliageDebugView.WindStrength;
                settings.GlobalIllumination.Enabled = true;
                settings.GlobalIllumination.Mode = GlobalIlluminationMode.RayQueryHybrid;
                settings.GlobalIllumination.DebugView = GlobalIlluminationDebugView.DdgiVisibility;
                settings.GlobalIllumination.IndirectIntensity = 1.25f;
                settings.GlobalIllumination.EnvironmentFallbackIntensity = 0.35f;
                settings.GlobalIllumination.UseSsgi = true;
                settings.GlobalIllumination.UseDdgi = false;
                settings.GlobalIllumination.UseRayQueryBackend = true;
                settings.GlobalIllumination.DdgiProbeClassificationEnabled = false;
                settings.GlobalIllumination.DdgiProbeRelocationEnabled = true;
                settings.GlobalIllumination.DdgiCameraRelativeEnabled = true;
                settings.GlobalIllumination.DdgiQualityTier = DdgiQualityTier.DdgiMedium;
                settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled = false;
                settings.GlobalIllumination.DdgiSchedulerMode = DdgiSchedulerMode.CpuGpuCompare;
                settings.GlobalIllumination.DdgiGpuSchedulerReadbackValidationEnabled = true;
                settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering = false;
                settings.GlobalIllumination.DdgiExhaustiveGatherFallbackEnabled = false;
                settings.GlobalIllumination.DdgiRawAtlasRadianceConventionEnabled = false;
                settings.GlobalIllumination.DdgiAllowForwardWithoutDepthPrePass = false;
                settings.GlobalIllumination.DdgiDebugForceProbeActive = true;
                settings.GlobalIllumination.DdgiAdaptiveBudgetHysteresisFraction = 0.25f;
                settings.GlobalIllumination.DdgiEmergencyDegradeGpuTimeMultiplier = 3.0f;
                settings.GlobalIllumination.DdgiMinimumProbeRefreshFrames = 333;
                settings.GlobalIllumination.DdgiClipmapCascadeCount = 3;
                settings.GlobalIllumination.DdgiClipmapProbeCountX = 16;
                settings.GlobalIllumination.DdgiClipmapProbeCountY = 8;
                settings.GlobalIllumination.DdgiClipmapProbeCountZ = 16;
                settings.GlobalIllumination.DdgiClipmapBaseSpacing = 1.5f;
                settings.GlobalIllumination.DdgiClipmapSpacingScale = 2.5f;
                settings.GlobalIllumination.DdgiClipmapVerticalCenterOffset = 7.5f;
                settings.GlobalIllumination.DdgiClipmapEdgeBlendFraction = 0.2f;
                settings.GlobalIllumination.DdgiClipmapSafetyMarginCells = 3;
                settings.GlobalIllumination.DdgiFrustumPriorityWeight = 3.5f;
                settings.GlobalIllumination.DdgiOutOfFrustumMinimumUpdateFraction = 0.25f;
                settings.GlobalIllumination.DdgiNewProbeUpdateBoost = 6.0f;
                settings.GlobalIllumination.DdgiProbeUpdateTimeBudgetMilliseconds = 2.25f;
                settings.GlobalIllumination.DdgiGpuScheduleTimeBudgetMilliseconds = 0.5f;
                settings.GlobalIllumination.DdgiGpuTotalUpdateTimeBudgetMilliseconds = 2.75f;
                settings.GlobalIllumination.DdgiTeleportResetDistance = 125.0f;
                settings.GlobalIllumination.DdgiCameraCutResetEnabled = false;
                settings.GlobalIllumination.DdgiAsyncComputeEnabled = false;
                settings.GlobalIllumination.DdgiMaxActiveProbes = 1234;
                settings.GlobalIllumination.DdgiMaxProbeUpdatesPerFrame = 345;
                settings.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions = 77;
                settings.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount = 32;
                settings.GlobalIllumination.DdgiGpuSchedulerFallbackOnValidationFailure = false;
                settings.GlobalIllumination.DdgiGpuSchedulerForceCpuFallback = true;
                settings.GlobalIllumination.DdgiGpuSchedulerAutoRetryAfterFallback = true;
                settings.GlobalIllumination.DdgiGpuSchedulerValidationFailureThreshold = 5;
                settings.GlobalIllumination.DdgiGpuSchedulerFallbackRetryStableFrames = 450;
                settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget = 45678;
                settings.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame = 234;
                settings.GlobalIllumination.DdgiColdStartPrimaryRayBudget = 56789;
                settings.GlobalIllumination.DdgiMaxRaysPerProbe = 96;
                settings.GlobalIllumination.DdgiCascade0RaysPerProbe = 96;
                settings.GlobalIllumination.DdgiCascade1RaysPerProbe = 64;
                settings.GlobalIllumination.DdgiCascade2RaysPerProbe = 48;
                settings.GlobalIllumination.DdgiCascade3RaysPerProbe = 32;
                settings.GlobalIllumination.DdgiCascade0MaxRayDistance = 20.0f;
                settings.GlobalIllumination.DdgiCascade1MaxRayDistance = 36.0f;
                settings.GlobalIllumination.DdgiCascade2MaxRayDistance = 72.0f;
                settings.GlobalIllumination.DdgiCascade3MaxRayDistance = 144.0f;
                settings.GlobalIllumination.DdgiMaxShadedLights = 6;
                settings.GlobalIllumination.DdgiMaterialTextureMaxCascade = 2;
                settings.GlobalIllumination.DdgiThinWallPolicyEnabled = false;
                settings.GlobalIllumination.DdgiRoomSpacingScaledBiasEnabled = false;
                settings.GlobalIllumination.DdgiThinWallProxyThickness = 0.22f;
                settings.GlobalIllumination.DdgiThinWallLeakClampStrength = 0.55f;
                settings.GlobalIllumination.DdgiRelocationTargetSurfaceDistanceFraction = 0.22f;
                settings.GlobalIllumination.DdgiRelocationMinSurfaceDistance = 0.12f;
                settings.GlobalIllumination.DdgiRelocationMaxDistanceFraction = 0.33f;
                settings.GlobalIllumination.DdgiRelocationBlendAlpha = 0.45f;
                settings.GlobalIllumination.ResolutionScale = 1.0f;
                settings.GlobalIllumination.MaxBounceDistance = 12.5f;
                settings.GlobalIllumination.SsgiMaxDistance = 2.5f;
                settings.GlobalIllumination.SsgiThickness = 0.035f;
                settings.GlobalIllumination.SsgiHitNormalThreshold = 0.2f;
                settings.GlobalIllumination.TemporalEnabled = false;
                settings.GlobalIllumination.DenoiserEnabled = false;
                settings.GlobalIllumination.HistoryResponsiveness = 0.42f;
                settings.GlobalIllumination.NormalRejectionThreshold = 0.7f;
                settings.GlobalIllumination.DepthRejectionThreshold = 0.25f;
                settings.GlobalIllumination.LeakClampStrength = 0.6f;
                settings.HiZOcclusion.Enabled = false;
                settings.HiZOcclusion.AdaptiveEnabled = false;
                settings.HiZOcclusion.SceneSubmissionPreviousFrameCullingEnabled = false;
                settings.HiZOcclusion.CurrentFrameForwardVisibilityCompactionEnabled = false;
                settings.HiZOcclusion.ForceOn = true;
                settings.HiZOcclusion.ForceProbe = true;
                settings.HiZOcclusion.ValidateAgainstLegacyPath = true;
                settings.HiZOcclusion.PreviousFrameUvPaddingPixels = 12;
                settings.HiZOcclusion.OcclusionBias = 0.0015f;
                settings.HiZOcclusion.DisablePreviousFrameCullingDuringFastCameraMotion = false;
                settings.HiZOcclusion.FastCameraMotionDistanceThreshold = 2.5f;
                settings.HiZOcclusion.FastCameraMotionForwardDotThreshold = 0.95f;
                settings.HiZOcclusion.CameraMotionSuppressionFrames = 3;
                settings.Save(path);

                RenderSettings loaded = RenderSettings.Load(path);

                Assert.Multiple(() =>
                {
                    Assert.That(loaded.QualityPreset, Is.EqualTo(RenderQualityPreset.Medium));
                    Assert.That(loaded.ResolutionScale, Is.EqualTo(0.8f));
                    Assert.That(loaded.DynamicResolution.Enabled, Is.True);
                    Assert.That(loaded.DynamicResolution.MinimumScale, Is.EqualTo(0.6f));
                    Assert.That(loaded.DynamicResolution.MaximumScale, Is.EqualTo(0.95f));
                    Assert.That(loaded.EffectiveResolutionScale, Is.EqualTo(0.8f));
                    Assert.That(loaded.Exposure, Is.EqualTo(1.5f));
                    Assert.That(loaded.ToneMapper, Is.EqualTo(ToneMapper.Reinhard));
                    Assert.That(loaded.AutoExposure.Enabled, Is.True);
                    Assert.That(loaded.Bloom.Enabled, Is.False);
                    Assert.That(loaded.Foliage.Enabled, Is.False);
                    Assert.That(loaded.Foliage.GpuDrivenEnabled, Is.False);
                    Assert.That(loaded.Foliage.HiZCullingEnabled, Is.False);
                    Assert.That(loaded.Foliage.CastShadows, Is.False);
                    Assert.That(loaded.Foliage.IndirectMeshletDispatchEnabled, Is.False);
                    Assert.That(loaded.Foliage.FarImpostorsEnabled, Is.False);
                    Assert.That(loaded.Foliage.MotionVectorsEnabled, Is.True);
                    Assert.That(loaded.Foliage.LocalShadowsEnabled, Is.True);
                    Assert.That(loaded.Foliage.GrassShadowDistance, Is.EqualTo(12.5f));
                    Assert.That(loaded.Foliage.GrassShadowDensityScale, Is.EqualTo(0.25f));
                    Assert.That(loaded.Foliage.MaxDrawDistance, Is.EqualTo(123.0f));
                    Assert.That(loaded.Foliage.DensityScale, Is.EqualTo(0.65f));
                    Assert.That(loaded.Foliage.MaxVisibleClusters, Is.EqualTo(12345));
                    Assert.That(loaded.Foliage.MaxVisibleMeshletDraws, Is.EqualTo(67890));
                    Assert.That(loaded.Foliage.MaxLocalShadowedSpotLights, Is.EqualTo(3));
                    Assert.That(loaded.Foliage.MaxLocalShadowedPointLights, Is.EqualTo(2));
                    Assert.That(loaded.Foliage.MaxLocalShadowClusters, Is.EqualTo(4321));
                    Assert.That(loaded.Foliage.MaxLocalShadowMeshletDraws, Is.EqualTo(8765));
                    Assert.That(loaded.Foliage.DebugView, Is.EqualTo(FoliageDebugView.WindStrength));
                    Assert.That(loaded.GlobalIllumination.Enabled, Is.True);
                    Assert.That(loaded.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.RayQueryHybrid));
                    Assert.That(loaded.GlobalIllumination.DebugView, Is.EqualTo(GlobalIlluminationDebugView.DdgiVisibility));
                    Assert.That(loaded.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.25f));
                    Assert.That(loaded.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(0.35f));
                    Assert.That(loaded.GlobalIllumination.UseSsgi, Is.True);
                    Assert.That(loaded.GlobalIllumination.UseDdgi, Is.False);
                    Assert.That(loaded.GlobalIllumination.UseRayQueryBackend, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiProbeClassificationEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiProbeRelocationEnabled, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiMedium));
                    Assert.That(loaded.GlobalIllumination.DdgiAdaptiveBudgetingEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiSchedulerMode, Is.EqualTo(DdgiSchedulerMode.CpuGpuCompare));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerReadbackValidationEnabled, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiExhaustiveGatherFallbackEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiRawAtlasRadianceConventionEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiAllowForwardWithoutDepthPrePass, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiDebugForceProbeActive, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiAdaptiveBudgetHysteresisFraction, Is.EqualTo(0.25f));
                    Assert.That(loaded.GlobalIllumination.DdgiEmergencyDegradeGpuTimeMultiplier, Is.EqualTo(3.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiMinimumProbeRefreshFrames, Is.EqualTo(333));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapCascadeCount, Is.EqualTo(3));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapProbeCountX, Is.EqualTo(16));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapProbeCountY, Is.EqualTo(8));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapProbeCountZ, Is.EqualTo(16));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapBaseSpacing, Is.EqualTo(1.5f));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapSpacingScale, Is.EqualTo(2.5f));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapVerticalCenterOffset, Is.EqualTo(7.5f));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapEdgeBlendFraction, Is.EqualTo(0.2f));
                    Assert.That(loaded.GlobalIllumination.DdgiClipmapSafetyMarginCells, Is.EqualTo(3));
                    Assert.That(loaded.GlobalIllumination.DdgiFrustumPriorityWeight, Is.EqualTo(3.5f));
                    Assert.That(loaded.GlobalIllumination.DdgiOutOfFrustumMinimumUpdateFraction, Is.EqualTo(0.25f));
                    Assert.That(loaded.GlobalIllumination.DdgiNewProbeUpdateBoost, Is.EqualTo(6.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(2.25f));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuScheduleTimeBudgetMilliseconds, Is.EqualTo(0.5f));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(2.75f));
                    Assert.That(loaded.GlobalIllumination.DdgiTeleportResetDistance, Is.EqualTo(125.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiCameraCutResetEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiAsyncComputeEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiMaxActiveProbes, Is.EqualTo(1234));
                    Assert.That(loaded.GlobalIllumination.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(345));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerMaxDirtyRegions, Is.EqualTo(77));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerCandidateBucketCount, Is.EqualTo(32));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerFallbackOnValidationFailure, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerForceCpuFallback, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerAutoRetryAfterFallback, Is.True);
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerValidationFailureThreshold, Is.EqualTo(5));
                    Assert.That(loaded.GlobalIllumination.DdgiGpuSchedulerFallbackRetryStableFrames, Is.EqualTo(450));
                    Assert.That(loaded.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(45678));
                    Assert.That(loaded.GlobalIllumination.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(234));
                    Assert.That(loaded.GlobalIllumination.DdgiColdStartPrimaryRayBudget, Is.EqualTo(56789));
                    Assert.That(loaded.GlobalIllumination.DdgiMaxRaysPerProbe, Is.EqualTo(96));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade0RaysPerProbe, Is.EqualTo(96));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade1RaysPerProbe, Is.EqualTo(64));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade2RaysPerProbe, Is.EqualTo(48));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade3RaysPerProbe, Is.EqualTo(32));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade0MaxRayDistance, Is.EqualTo(20.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade1MaxRayDistance, Is.EqualTo(36.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade2MaxRayDistance, Is.EqualTo(72.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiCascade3MaxRayDistance, Is.EqualTo(144.0f));
                    Assert.That(loaded.GlobalIllumination.DdgiMaxShadedLights, Is.EqualTo(6));
                    Assert.That(loaded.GlobalIllumination.DdgiMaterialTextureMaxCascade, Is.EqualTo(2));
                    Assert.That(loaded.GlobalIllumination.DdgiThinWallPolicyEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiRoomSpacingScaledBiasEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DdgiThinWallProxyThickness, Is.EqualTo(0.22f));
                    Assert.That(loaded.GlobalIllumination.DdgiThinWallLeakClampStrength, Is.EqualTo(0.55f));
                    Assert.That(loaded.GlobalIllumination.DdgiRelocationTargetSurfaceDistanceFraction, Is.EqualTo(0.22f));
                    Assert.That(loaded.GlobalIllumination.DdgiRelocationMinSurfaceDistance, Is.EqualTo(0.12f));
                    Assert.That(loaded.GlobalIllumination.DdgiRelocationMaxDistanceFraction, Is.EqualTo(0.33f));
                    Assert.That(loaded.GlobalIllumination.DdgiRelocationBlendAlpha, Is.EqualTo(0.45f));
                    Assert.That(loaded.GlobalIllumination.ResolutionScale, Is.EqualTo(1.0f));
                    Assert.That(loaded.GlobalIllumination.MaxBounceDistance, Is.EqualTo(12.5f));
                    Assert.That(loaded.GlobalIllumination.SsgiMaxDistance, Is.EqualTo(2.5f));
                    Assert.That(loaded.GlobalIllumination.SsgiThickness, Is.EqualTo(0.035f));
                    Assert.That(loaded.GlobalIllumination.SsgiHitNormalThreshold, Is.EqualTo(0.2f));
                    Assert.That(loaded.GlobalIllumination.TemporalEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.DenoiserEnabled, Is.False);
                    Assert.That(loaded.GlobalIllumination.HistoryResponsiveness, Is.EqualTo(0.42f));
                    Assert.That(loaded.GlobalIllumination.NormalRejectionThreshold, Is.EqualTo(0.7f));
                    Assert.That(loaded.GlobalIllumination.DepthRejectionThreshold, Is.EqualTo(0.25f));
                    Assert.That(loaded.GlobalIllumination.LeakClampStrength, Is.EqualTo(0.6f));
                    Assert.That(loaded.HiZOcclusion.Enabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.AdaptiveEnabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.SceneSubmissionPreviousFrameCullingEnabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.CurrentFrameForwardVisibilityCompactionEnabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.PreviousFrameSceneSubmissionEnabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.CurrentFrameForwardVisibilityEnabled, Is.False);
                    Assert.That(loaded.HiZOcclusion.ForceOn, Is.True);
                    Assert.That(loaded.HiZOcclusion.ForceProbe, Is.True);
                    Assert.That(loaded.HiZOcclusion.ValidateAgainstLegacyPath, Is.True);
                    Assert.That(loaded.HiZOcclusion.PreviousFrameUvPaddingPixels, Is.EqualTo(12));
                    Assert.That(loaded.HiZOcclusion.OcclusionBias, Is.EqualTo(0.0015f));
                    Assert.That(loaded.HiZOcclusion.DisablePreviousFrameCullingDuringFastCameraMotion, Is.False);
                    Assert.That(loaded.HiZOcclusion.FastCameraMotionDistanceThreshold, Is.EqualTo(2.5f));
                    Assert.That(loaded.HiZOcclusion.FastCameraMotionForwardDotThreshold, Is.EqualTo(0.95f));
                    Assert.That(loaded.HiZOcclusion.CameraMotionSuppressionFrames, Is.EqualTo(3));
                });
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void RenderSettings_LoadAcceptsEitherHiZConsumerAlias()
        {
            string defaultPath = Path.Combine(Path.GetTempPath(), $"njulf-hiz-default-{Guid.NewGuid():N}.json");
            string previousAliasPath = Path.Combine(Path.GetTempPath(), $"njulf-hiz-previous-{Guid.NewGuid():N}.json");
            string currentAliasPath = Path.Combine(Path.GetTempPath(), $"njulf-hiz-current-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(defaultPath, "{}");
                File.WriteAllText(previousAliasPath, """
                {
                  "HiZOcclusion": {
                    "SceneSubmissionPreviousFrameCullingEnabled": true
                  }
                }
                """);
                File.WriteAllText(currentAliasPath, """
                {
                  "HiZOcclusion": {
                    "CurrentFrameForwardVisibilityEnabled": true
                  }
                }
                """);

                RenderSettings defaultSettings = RenderSettings.Load(defaultPath);
                RenderSettings previousAlias = RenderSettings.Load(previousAliasPath);
                RenderSettings currentAlias = RenderSettings.Load(currentAliasPath);

                Assert.Multiple(() =>
                {
                    Assert.That(defaultSettings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled, Is.True);
                    Assert.That(defaultSettings.HiZOcclusion.SceneSubmissionPreviousFrameCullingEnabled, Is.True);
                    Assert.That(defaultSettings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled, Is.True);
                    Assert.That(defaultSettings.HiZOcclusion.CurrentFrameForwardVisibilityCompactionEnabled, Is.True);
                    Assert.That(previousAlias.HiZOcclusion.PreviousFrameSceneSubmissionEnabled, Is.True);
                    Assert.That(previousAlias.HiZOcclusion.SceneSubmissionPreviousFrameCullingEnabled, Is.True);
                    Assert.That(currentAlias.HiZOcclusion.CurrentFrameForwardVisibilityEnabled, Is.True);
                    Assert.That(currentAlias.HiZOcclusion.CurrentFrameForwardVisibilityCompactionEnabled, Is.True);
                });
            }
            finally
            {
                if (File.Exists(defaultPath))
                    File.Delete(defaultPath);
                if (File.Exists(previousAliasPath))
                    File.Delete(previousAliasPath);
                if (File.Exists(currentAliasPath))
                    File.Delete(currentAliasPath);
            }
        }

        [Test]
        public void RenderSettings_ResolutionScaleClampsToSupportedRange()
        {
            var settings = new RenderSettings { ResolutionScale = 0.1f };
            Assert.That(settings.ResolutionScale, Is.EqualTo(0.5f));

            settings.ResolutionScale = 2.0f;
            Assert.That(settings.ResolutionScale, Is.EqualTo(1.0f));

            settings.DynamicResolution.Enabled = true;
            settings.DynamicResolution.MinimumScale = 0.7f;
            settings.DynamicResolution.MaximumScale = 0.9f;
            settings.ResolutionScale = 1.0f;
            Assert.That(settings.EffectiveResolutionScale, Is.EqualTo(0.9f));
        }

        [Test]
        public void RenderingOptions_TextureBudgetProfilesSetExpectedMaxDimensions()
        {
            var options = new RenderingOptions();

            options.ApplyTextureBudgetProfile(TextureBudgetProfile.HighQuality);
            Assert.Multiple(() =>
            {
                Assert.That(options.TextureBudgetProfile, Is.EqualTo(TextureBudgetProfile.HighQuality));
                Assert.That(options.MaxImportedTextureDimension, Is.EqualTo(2048));
            });

            options.ApplyTextureBudgetProfile(TextureBudgetProfile.Cinematic);
            Assert.Multiple(() =>
            {
                Assert.That(options.TextureBudgetProfile, Is.EqualTo(TextureBudgetProfile.Cinematic));
                Assert.That(options.MaxImportedTextureDimension, Is.EqualTo(4096));
            });

            options.MaxImportedTextureDimension = 1536;
            Assert.Multiple(() =>
            {
                Assert.That(options.TextureBudgetProfile, Is.EqualTo(TextureBudgetProfile.Custom));
                Assert.That(options.MaxImportedTextureDimension, Is.EqualTo(1536));
            });
        }

        [Test]
        public void MaterialDebugView_UsesDedicatedForwardDebugRange()
        {
            Assert.Multiple(() =>
            {
                Assert.That((uint)MaterialDebugView.FeatureFlags, Is.EqualTo(32u));
                Assert.That((uint)MaterialDebugView.BaseColor, Is.GreaterThan((uint)ShadowDebugView.LocalShadowSelection));
                Assert.That((uint)MaterialDebugView.SubsurfaceStrength, Is.LessThanOrEqualTo(255u));
            });
        }

        [Test]
        public void AnimationDebugView_UsesDedicatedForwardDebugRange()
        {
            Assert.Multiple(() =>
            {
                Assert.That((uint)AnimationDebugView.SkinnedObjects, Is.EqualTo(64u));
                Assert.That((uint)AnimationDebugView.SkinnedObjects, Is.GreaterThan((uint)MaterialDebugView.Dispersion));
                Assert.That((uint)AnimationDebugView.ClipTime, Is.LessThanOrEqualTo(255u));
            });
        }

        [Test]
        public void RenderSettings_ClampsNegativeExposure()
        {
            var settings = new RenderSettings { Exposure = -1.0f };

            Assert.That(settings.Exposure, Is.EqualTo(0.0f));
        }

        [Test]
        public void AsyncComputeSettings_DefaultDisabledWithCandidateTogglesEnabled()
        {
            var settings = new RenderSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.AsyncCompute.Enabled, Is.False);
                Assert.That(settings.AsyncCompute.HiZBuildEnabled, Is.True);
                Assert.That(settings.AsyncCompute.AmbientOcclusionBlurEnabled, Is.True);
                Assert.That(settings.AsyncCompute.FogEnabled, Is.True);
                Assert.That(settings.AsyncCompute.BloomEnabled, Is.True);
                Assert.That(settings.AsyncCompute.GpuParticlesEnabled, Is.True);
                Assert.That(settings.HiZVisibilityPolicy.WarmupFrameCount, Is.EqualTo(1));
                Assert.That(settings.HiZVisibilityPolicy.CameraCutDistance, Is.EqualTo(5.0f));
                Assert.That(settings.HiZVisibilityPolicy.CameraCutForwardDotThreshold, Is.EqualTo(0.5f));
                Assert.That(settings.HiZVisibilityPolicy.MinMeasuredOcclusionTests, Is.EqualTo(512));
                Assert.That(settings.HiZVisibilityPolicy.MinUsefulOcclusionCullRate, Is.EqualTo(0.03f));
                Assert.That(settings.HiZVisibilityPolicy.AdaptiveProbeIntervalFrames, Is.EqualTo(60));
                Assert.That(settings.HiZVisibilityPolicy.UnprofitableFrameThreshold, Is.EqualTo(3));
                Assert.That(settings.HiZVisibilityPolicy.MinEstimatedSavedMicroseconds, Is.EqualTo(50));
                Assert.That(settings.HiZVisibilityPolicy.MinEstimatedSavedToCostRatio, Is.EqualTo(1.10f));
                Assert.That(settings.HiZVisibilityPolicy.AdaptiveSmoothingFrameWindow, Is.EqualTo(30));
                Assert.That(settings.HiZVisibilityPolicy.ForceHiZOcclusionOn, Is.False);
                Assert.That(settings.HiZVisibilityPolicy.ForceAdaptiveProbe, Is.False);
                Assert.That(settings.HiZOcclusion.Enabled, Is.True);
                Assert.That(settings.HiZOcclusion.AdaptiveEnabled, Is.True);
                Assert.That(settings.HiZOcclusion.SceneSubmissionPreviousFrameCullingEnabled, Is.True);
                Assert.That(settings.HiZOcclusion.CurrentFrameForwardVisibilityCompactionEnabled, Is.True);
                Assert.That(settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled, Is.True);
                Assert.That(settings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled, Is.True);
                Assert.That(settings.HiZOcclusion.ForceOn, Is.False);
                Assert.That(settings.HiZOcclusion.ForceProbe, Is.False);
                Assert.That(settings.HiZOcclusion.ValidateAgainstLegacyPath, Is.False);
                Assert.That(settings.HiZOcclusion.PreviousFrameUvPaddingPixels, Is.EqualTo(8));
                Assert.That(settings.HiZOcclusion.OcclusionBias, Is.EqualTo(0.0005f));
                Assert.That(settings.HiZOcclusion.DisablePreviousFrameCullingDuringFastCameraMotion, Is.True);
                Assert.That(settings.HiZOcclusion.FastCameraMotionDistanceThreshold, Is.EqualTo(1.0f));
                Assert.That(settings.HiZOcclusion.FastCameraMotionForwardDotThreshold, Is.EqualTo(0.985f));
                Assert.That(settings.HiZOcclusion.CameraMotionSuppressionFrames, Is.EqualTo(1));
            });
        }

        [Test]
        public void HiZOcclusionSettings_ClampToSupportedRanges()
        {
            var settings = new HiZOcclusionSettings
            {
                PreviousFrameUvPaddingPixels = -4,
                OcclusionBias = -1.0f,
                FastCameraMotionDistanceThreshold = -1.0f,
                FastCameraMotionForwardDotThreshold = 2.0f,
                CameraMotionSuppressionFrames = 99
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.PreviousFrameUvPaddingPixels, Is.EqualTo(0));
                Assert.That(settings.OcclusionBias, Is.EqualTo(0.0f));
                Assert.That(settings.FastCameraMotionDistanceThreshold, Is.EqualTo(0.0f));
                Assert.That(settings.FastCameraMotionForwardDotThreshold, Is.EqualTo(1.0f));
                Assert.That(settings.CameraMotionSuppressionFrames, Is.EqualTo(8));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_FirstFrameWarmsBeforeOcclusion()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState();
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: true,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: false,
                CounterSource: HiZCounterSource.Unavailable,
                CompletedHiZTested: 0,
                CompletedHiZCulled: 0);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.WarmingUp));
                Assert.That(decision.BuildHiZ, Is.True);
                Assert.That(decision.UseHiZForOcclusion, Is.False);
                Assert.That(decision.SceneChanged, Is.True);
                Assert.That(decision.AdaptiveStatus, Is.EqualTo("CountersUnavailable"));
                Assert.That(state.WarmupFramesRemaining, Is.EqualTo(0));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_CameraCutWarmsBeforeOcclusion()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: true,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: false,
                CounterSource: HiZCounterSource.Unavailable,
                CompletedHiZTested: 0,
                CompletedHiZCulled: 0);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.WarmingUp));
                Assert.That(decision.BuildHiZ, Is.True);
                Assert.That(decision.UseHiZForOcclusion, Is.False);
                Assert.That(decision.CameraCut, Is.True);
            });
        }

        [Test]
        public void HiZVisibilityPolicy_AdaptiveSuppressionWaitsForRepeatedBadMeasurements()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.LegacyTaskShader,
                CompletedHiZTested: 1024,
                CompletedHiZCulled: 1,
                CompletedDepthPrePassMicroseconds: 2000,
                CompletedHiZBuildMicroseconds: 200,
                CompletedForwardOpaqueMicroseconds: 10000);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.Active));
                Assert.That(decision.BuildHiZ, Is.True);
                Assert.That(decision.UseHiZForOcclusion, Is.True);
                Assert.That(decision.AdaptiveSuppressed, Is.False);
                Assert.That(decision.AdaptiveStatus, Is.EqualTo("Active"));
                Assert.That(decision.CounterSource, Is.EqualTo(HiZCounterSource.LegacyTaskShader));
                Assert.That(state.ConsecutiveUnprofitableFrames, Is.EqualTo(1));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_AdaptiveSkipDisablesOcclusionAndHiZBuild()
        {
            var settings = new HiZVisibilityPolicySettings { UnprofitableFrameThreshold = 1 };
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.LegacyTaskShader,
                CompletedHiZTested: 1024,
                CompletedHiZCulled: 1,
                CompletedDepthPrePassMicroseconds: 2000,
                CompletedHiZBuildMicroseconds: 200,
                CompletedForwardOpaqueMicroseconds: 10000);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.Suppressed));
                Assert.That(decision.BuildHiZ, Is.False);
                Assert.That(decision.UseHiZForOcclusion, Is.False);
                Assert.That(decision.AdaptiveSuppressed, Is.True);
                Assert.That(decision.AdaptiveProbeCountdown, Is.EqualTo(settings.AdaptiveProbeIntervalFrames));
                Assert.That(decision.AdaptiveCullRate, Is.EqualTo(1.0f / 1024.0f).Within(0.0001f));
                Assert.That(decision.AdaptiveEstimatedSavedMicroseconds, Is.EqualTo(10));
                Assert.That(decision.AdaptiveEstimatedCostMicroseconds, Is.EqualTo(2200));
                Assert.That(decision.AdaptiveEstimatedNetMicroseconds, Is.EqualTo(-2190));
                Assert.That(decision.AdaptiveStatus, Is.EqualTo("Suppressed"));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_AdaptiveProbeReenablesOcclusion()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState
            {
                PyramidValid = true,
                AdaptiveSuppressed = true,
                AdaptiveProbeCountdown = 1
            };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.LegacyTaskShader,
                CompletedHiZTested: 0,
                CompletedHiZCulled: 0);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.Probing));
                Assert.That(decision.BuildHiZ, Is.True);
                Assert.That(decision.UseHiZForOcclusion, Is.True);
                Assert.That(decision.AdaptiveProbe, Is.True);
                Assert.That(decision.AdaptiveProbeCountdown, Is.EqualTo(settings.AdaptiveProbeIntervalFrames));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_ForcedOnBypassesAdaptiveSuppression()
        {
            var settings = new HiZVisibilityPolicySettings
            {
                ForceHiZOcclusionOn = true,
                UnprofitableFrameThreshold = 1
            };
            var state = new HiZVisibilityPolicyRuntimeState
            {
                PyramidValid = true,
                AdaptiveSuppressed = true,
                AdaptiveProbeCountdown = 10,
                ConsecutiveUnprofitableFrames = 7
            };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.SceneSubmissionCompaction,
                CompletedHiZTested: 2048,
                CompletedHiZCulled: 1,
                CompletedDepthPrePassMicroseconds: 2000,
                CompletedHiZBuildMicroseconds: 200,
                CompletedForwardOpaqueMicroseconds: 10000);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Status, Is.EqualTo(HiZVisibilityPolicyStatus.ForcedOn));
                Assert.That(decision.BuildHiZ, Is.True);
                Assert.That(decision.UseHiZForOcclusion, Is.True);
                Assert.That(decision.AdaptiveStatus, Is.EqualTo("ForcedOn"));
                Assert.That(decision.AdaptiveSuppressed, Is.False);
                Assert.That(state.ConsecutiveUnprofitableFrames, Is.EqualTo(0));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_ExcludesDepthPrePassCostWhenOtherFeaturesNeedIt()
        {
            var settings = new HiZVisibilityPolicySettings { UnprofitableFrameThreshold = 1 };
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.SceneSubmissionCompaction,
                CompletedHiZTested: 4096,
                CompletedHiZCulled: 512,
                DepthPrePassRequiredByOtherFeatures: true,
                CompletedDepthPrePassMicroseconds: 3000,
                CompletedHiZBuildMicroseconds: 200,
                CompletedSceneSubmissionCompactionMicroseconds: 80,
                CompletedForwardOpaqueMicroseconds: 1000);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.AdaptiveEstimatedCostMicroseconds, Is.EqualTo(280));
                Assert.That(decision.AdaptiveEstimatedSavedMicroseconds, Is.EqualTo(143));
                Assert.That(decision.AdaptiveEstimatedNetMicroseconds, Is.EqualTo(-137));
                Assert.That(decision.AdaptiveSmoothedCullRate, Is.EqualTo(0.125f).Within(0.0001f));
                Assert.That(decision.AdaptiveSmoothedSavedToCostRatio, Is.EqualTo(143.0f / 280.0f).Within(0.0001f));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_SceneSubmissionCountersDriveAdaptivePolicyWithoutDiagnostics()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: true,
                CounterSource: HiZCounterSource.SceneSubmissionCompaction,
                CompletedHiZTested: 2048,
                CompletedHiZCulled: 256,
                CompletedDepthPrePassMicroseconds: 400,
                CompletedHiZBuildMicroseconds: 100,
                CompletedForwardOpaqueMicroseconds: 2000);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.AdaptiveStatus, Is.Not.EqualTo("CountersUnavailable"));
                Assert.That(decision.CounterSource, Is.EqualTo(HiZCounterSource.SceneSubmissionCompaction));
                Assert.That(decision.AdaptiveMeasuredOcclusionTests, Is.EqualTo(2048));
                Assert.That(decision.AdaptiveMeasuredOcclusionCulled, Is.EqualTo(256));
            });
        }

        [Test]
        public void HiZVisibilityPolicy_NoCountersReportsCountersUnavailable()
        {
            var settings = new HiZVisibilityPolicySettings();
            var state = new HiZVisibilityPolicyRuntimeState { PyramidValid = true };
            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: true,
                HiZOcclusionEnabled: true,
                FeatureIsolationDisablesHiZ: false,
                RequestedTestMode: HiZTestMode.Bounds4Tap,
                SceneChanged: false,
                CameraCut: false,
                AdaptiveEnabled: true,
                ProductionCountersAvailable: false,
                CounterSource: HiZCounterSource.Unavailable,
                CompletedHiZTested: 0,
                CompletedHiZCulled: 0);

            HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(input, settings, state);

            Assert.Multiple(() =>
            {
                Assert.That(decision.AdaptiveStatus, Is.EqualTo("CountersUnavailable"));
                Assert.That(decision.CounterSource, Is.EqualTo(HiZCounterSource.Unavailable));
            });
        }

        [Test]
        public void AutoExposureSettings_ClampToSupportedRanges()
        {
            var settings = new AutoExposureSettings
            {
                TargetLuminance = -1.0f,
                MinExposure = -4.0f,
                MaxExposure = 0.0001f,
                AdaptationSpeed = 99.0f,
                MinLogLuminance = 100.0f,
                MaxLogLuminance = -100.0f,
                SamplingStride = 99
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.TargetLuminance, Is.EqualTo(0.01f));
                Assert.That(settings.MinExposure, Is.EqualTo(0.001f));
                Assert.That(settings.MaxExposure, Is.EqualTo(0.001f));
                Assert.That(settings.AdaptationSpeed, Is.EqualTo(30.0f));
                Assert.That(settings.MinLogLuminance, Is.EqualTo(16.0f));
                Assert.That(settings.MaxLogLuminance, Is.EqualTo(16.01f).Within(0.0001f));
                Assert.That(settings.SamplingStride, Is.EqualTo(8));
            });
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
        public void ReflectionSettings_ClampToSupportedRanges()
        {
            var settings = new ReflectionSettings
            {
                MaxProbes = 999,
                MaxProbesPerPixel = 99,
                ProbeResolution = 300,
                Intensity = -1.0f,
                GlobalFallbackIntensity = 99.0f,
                MaxProbeCapturesPerFrame = 99,
                DebugProbeIndex = -1,
                DebugCubemapFace = 99,
                DebugMipLevel = 99
            };

            settings.ClampDebugResources(activeProbeCount: 2, mipCount: 4);

            Assert.Multiple(() =>
            {
                Assert.That(settings.MaxProbes, Is.EqualTo(256));
                Assert.That(settings.MaxProbesPerPixel, Is.EqualTo(ReflectionSettings.ShaderMaxProbesPerPixel));
                Assert.That(settings.ProbeResolution, Is.EqualTo(512));
                Assert.That(settings.Intensity, Is.EqualTo(0.0f));
                Assert.That(settings.GlobalFallbackIntensity, Is.EqualTo(4.0f));
                Assert.That(settings.MaxProbeCapturesPerFrame, Is.EqualTo(4));
                Assert.That(settings.DebugProbeIndex, Is.EqualTo(0));
                Assert.That(settings.DebugCubemapFace, Is.EqualTo(5));
                Assert.That(settings.DebugMipLevel, Is.EqualTo(3));
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
        public void ResolveDdgiGpuSchedulerCounterFailureReason_DetectsImpossibleCounters()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                        new GPUDdgiSchedulerCounters { RequestCount = 33 },
                        requestBudget: 64,
                        primaryRayBudget: 4096,
                        queueCapacity: 32),
                    Is.EqualTo("counter-request-count-exceeds-queue-capacity"));
                Assert.That(
                    VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                        new GPUDdgiSchedulerCounters { RequestCount = 65 },
                        requestBudget: 64,
                        primaryRayBudget: 4096,
                        queueCapacity: 128),
                    Is.EqualTo("counter-request-count-exceeds-budget"));
                Assert.That(
                    VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                        new GPUDdgiSchedulerCounters { PrimaryRayCount = 4097 },
                        requestBudget: 64,
                        primaryRayBudget: 4096,
                        queueCapacity: 128),
                    Is.EqualTo("counter-primary-ray-count-exceeds-budget"));
                Assert.That(
                    VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                        new GPUDdgiSchedulerCounters { DuplicateRequestCount = 1 },
                        requestBudget: 64,
                        primaryRayBudget: 4096,
                        queueCapacity: 128),
                    Is.EqualTo("counter-duplicate-request"));
                Assert.That(
                    VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                        new GPUDdgiSchedulerCounters { InvalidProbeCount = 1 },
                        requestBudget: 64,
                        primaryRayBudget: 4096,
                        queueCapacity: 128),
                    Is.EqualTo("counter-invalid-probe"));
            });
        }

        [Test]
        public void ResolveDdgiGpuSchedulerCounterFailureReason_AllowsValidCounters()
        {
            string reason = VulkanRenderer.ResolveDdgiGpuSchedulerCounterFailureReason(
                new GPUDdgiSchedulerCounters
                {
                    RequestCount = 64,
                    PrimaryRayCount = 4096
                },
                requestBudget: 64,
                primaryRayBudget: 4096,
                queueCapacity: 128);

            Assert.That(reason, Is.EqualTo(string.Empty));
        }

        [Test]
        public void GlobalIlluminationSettings_ClampToSupportedRanges()
        {
            var settings = new GlobalIlluminationSettings
            {
                Mode = GlobalIlluminationMode.RayQueryHybrid,
                IndirectIntensity = 99f,
                EnvironmentFallbackIntensity = -1f,
                UseRayQueryBackend = true,
                ResolutionScale = 0.1f,
                MaxBounceDistance = 999f,
                SsgiMaxDistance = -1f,
                SsgiThickness = 99f,
                SsgiHitNormalThreshold = 2f,
                HistoryResponsiveness = 0f,
                NormalRejectionThreshold = 2f,
                DepthRejectionThreshold = -1f,
                LeakClampStrength = 2f,
                DdgiClipmapCascadeCount = 99,
                DdgiClipmapProbeCountX = 99,
                DdgiClipmapProbeCountY = 99,
                DdgiClipmapProbeCountZ = 99,
                DdgiClipmapBaseSpacing = float.PositiveInfinity,
                DdgiClipmapSpacingScale = 99f,
                DdgiClipmapVerticalCenterOffset = 999f,
                DdgiClipmapEdgeBlendFraction = 99f,
                DdgiClipmapSafetyMarginCells = 99,
                DdgiFrustumPriorityWeight = 99f,
                DdgiOutOfFrustumMinimumUpdateFraction = 99f,
                DdgiNewProbeUpdateBoost = 99f,
                DdgiProbeUpdateTimeBudgetMilliseconds = 99f,
                DdgiGpuScheduleTimeBudgetMilliseconds = 99f,
                DdgiGpuTotalUpdateTimeBudgetMilliseconds = 99f,
                DdgiGpuSchedulerValidationFailureThreshold = 99,
                DdgiGpuSchedulerFallbackRetryStableFrames = 99_999,
                DdgiAdaptiveBudgetHysteresisFraction = 99f,
                DdgiEmergencyDegradeGpuTimeMultiplier = 99f,
                DdgiMinimumProbeRefreshFrames = 99_999,
                DdgiTeleportResetDistance = 99_999f,
                DdgiMaxProbeUpdatesPerFrame = 99_999,
                DdgiProbeUpdatePrimaryRayBudget = 99_999_999,
                DdgiColdStartMaxProbeUpdatesPerFrame = 99_999,
                DdgiColdStartPrimaryRayBudget = 99_999_999,
                DdgiMaxRaysPerProbe = 99_999,
                DdgiCascade0MaxRayDistance = float.PositiveInfinity,
                DdgiCascade1MaxRayDistance = 999f,
                DdgiCascade2MaxRayDistance = 999f,
                DdgiCascade3MaxRayDistance = 999f,
                DdgiMaxShadedLights = 99_999,
                DdgiMaterialTextureMaxCascade = 99_999,
                DdgiThinWallProxyThickness = 99f,
                DdgiThinWallLeakClampStrength = 99f,
                DdgiRelocationTargetSurfaceDistanceFraction = 99f,
                DdgiRelocationMinSurfaceDistance = 99f,
                DdgiRelocationMaxDistanceFraction = 99f,
                DdgiRelocationBlendAlpha = 99f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.IndirectIntensity, Is.EqualTo(8.0f));
                Assert.That(settings.EnvironmentFallbackIntensity, Is.EqualTo(0.0f));
                Assert.That(settings.ResolutionScale, Is.EqualTo(0.25f));
                Assert.That(settings.MaxBounceDistance, Is.EqualTo(100.0f));
                Assert.That(settings.SsgiMaxDistance, Is.EqualTo(0.1f));
                Assert.That(settings.SsgiThickness, Is.EqualTo(1.0f));
                Assert.That(settings.SsgiHitNormalThreshold, Is.EqualTo(1.0f));
                Assert.That(settings.HistoryResponsiveness, Is.EqualTo(0.01f));
                Assert.That(settings.NormalRejectionThreshold, Is.EqualTo(1.0f));
                Assert.That(settings.DepthRejectionThreshold, Is.EqualTo(0.0001f));
                Assert.That(settings.LeakClampStrength, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiProbeClassificationEnabled, Is.True);
                Assert.That(settings.DdgiProbeRelocationEnabled, Is.False);
                Assert.That(settings.DdgiCameraRelativeEnabled, Is.True);
                Assert.That(settings.DdgiClipmapCascadeCount, Is.EqualTo(4));
                Assert.That(settings.DdgiClipmapProbeCountX, Is.EqualTo(32));
                Assert.That(settings.DdgiClipmapProbeCountY, Is.EqualTo(16));
                Assert.That(settings.DdgiClipmapProbeCountZ, Is.EqualTo(32));
                Assert.That(settings.DdgiClipmapBaseSpacing, Is.EqualTo(0.25f));
                Assert.That(settings.DdgiClipmapSpacingScale, Is.EqualTo(8.0f));
                Assert.That(settings.DdgiClipmapVerticalCenterOffset, Is.EqualTo(64.0f));
                Assert.That(settings.DdgiClipmapEdgeBlendFraction, Is.EqualTo(0.5f));
                Assert.That(settings.DdgiClipmapSafetyMarginCells, Is.EqualTo(16));
                Assert.That(settings.DdgiFrustumPriorityWeight, Is.EqualTo(16.0f));
                Assert.That(settings.DdgiOutOfFrustumMinimumUpdateFraction, Is.EqualTo(0.5f));
                Assert.That(settings.DdgiNewProbeUpdateBoost, Is.EqualTo(32.0f));
                Assert.That(settings.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(16.0f));
                Assert.That(settings.DdgiGpuScheduleTimeBudgetMilliseconds, Is.EqualTo(16.0f));
                Assert.That(settings.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(16.0f));
                Assert.That(settings.DdgiGpuSchedulerValidationFailureThreshold, Is.EqualTo(60));
                Assert.That(settings.DdgiGpuSchedulerFallbackRetryStableFrames, Is.EqualTo(10_000));
                Assert.That(settings.DdgiAdaptiveBudgetHysteresisFraction, Is.EqualTo(0.75f));
                Assert.That(settings.DdgiEmergencyDegradeGpuTimeMultiplier, Is.EqualTo(8.0f));
                Assert.That(settings.DdgiMinimumProbeRefreshFrames, Is.EqualTo(3600));
                Assert.That(settings.DdgiTeleportResetDistance, Is.EqualTo(10000.0f));
                Assert.That(settings.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget));
                Assert.That(settings.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiProbeUpdatePrimaryRayBudget));
                Assert.That(settings.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget));
                Assert.That(settings.DdgiColdStartPrimaryRayBudget, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiProbeUpdatePrimaryRayBudget));
                Assert.That(settings.DdgiMaxRaysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolumeData.ShaderMaxRaysPerProbe));
                Assert.That(settings.DdgiCascade0MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiCascade1MaxRayDistance, Is.EqualTo(512.0f));
                Assert.That(settings.DdgiCascade2MaxRayDistance, Is.EqualTo(512.0f));
                Assert.That(settings.DdgiCascade3MaxRayDistance, Is.EqualTo(512.0f));
                Assert.That(settings.DdgiMaxShadedLights, Is.EqualTo(64));
                Assert.That(settings.DdgiMaterialTextureMaxCascade, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount - 1));
                Assert.That(settings.DdgiThinWallProxyThickness, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiThinWallLeakClampStrength, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiRelocationTargetSurfaceDistanceFraction, Is.EqualTo(0.35f));
                Assert.That(settings.DdgiRelocationMinSurfaceDistance, Is.EqualTo(0.5f));
                Assert.That(settings.DdgiRelocationMaxDistanceFraction, Is.EqualTo(0.40f));
                Assert.That(settings.DdgiRelocationBlendAlpha, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiCameraCutResetEnabled, Is.True);
                Assert.That(settings.EffectiveUseSsgi, Is.True);
                Assert.That(settings.EffectiveUseDdgi, Is.True);
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.True);
            });

            settings.DdgiClipmapCascadeCount = -1;
            settings.DdgiClipmapProbeCountX = -1;
            settings.DdgiClipmapProbeCountY = -1;
            settings.DdgiClipmapProbeCountZ = -1;
            settings.DdgiClipmapBaseSpacing = -1f;
            settings.DdgiClipmapSpacingScale = -1f;
            settings.DdgiClipmapVerticalCenterOffset = -999f;
            settings.DdgiClipmapEdgeBlendFraction = -1f;
            settings.DdgiClipmapSafetyMarginCells = -1;
            settings.DdgiFrustumPriorityWeight = -1f;
            settings.DdgiOutOfFrustumMinimumUpdateFraction = -1f;
            settings.DdgiNewProbeUpdateBoost = -1f;
            settings.DdgiProbeUpdateTimeBudgetMilliseconds = -1f;
            settings.DdgiGpuScheduleTimeBudgetMilliseconds = -1f;
            settings.DdgiGpuTotalUpdateTimeBudgetMilliseconds = -1f;
            settings.DdgiGpuSchedulerValidationFailureThreshold = -1;
            settings.DdgiGpuSchedulerFallbackRetryStableFrames = -1;
            settings.DdgiAdaptiveBudgetHysteresisFraction = -1f;
            settings.DdgiEmergencyDegradeGpuTimeMultiplier = -1f;
            settings.DdgiMinimumProbeRefreshFrames = -1;
            settings.DdgiTeleportResetDistance = -1f;
            settings.DdgiMaxProbeUpdatesPerFrame = -1;
            settings.DdgiProbeUpdatePrimaryRayBudget = -1;
            settings.DdgiColdStartMaxProbeUpdatesPerFrame = -1;
            settings.DdgiColdStartPrimaryRayBudget = -1;
            settings.DdgiMaxRaysPerProbe = -1;
            settings.DdgiCascade0MaxRayDistance = -1f;
            settings.DdgiCascade1MaxRayDistance = -1f;
            settings.DdgiCascade2MaxRayDistance = -1f;
            settings.DdgiCascade3MaxRayDistance = -1f;
            settings.DdgiMaxShadedLights = -1;
            settings.DdgiMaterialTextureMaxCascade = -99;
            settings.DdgiThinWallProxyThickness = -1f;
            settings.DdgiThinWallLeakClampStrength = -1f;
            settings.DdgiRelocationTargetSurfaceDistanceFraction = -1f;
            settings.DdgiRelocationMinSurfaceDistance = -1f;
            settings.DdgiRelocationMaxDistanceFraction = -1f;
            settings.DdgiRelocationBlendAlpha = -1f;

            Assert.Multiple(() =>
            {
                Assert.That(settings.DdgiClipmapCascadeCount, Is.EqualTo(1));
                Assert.That(settings.DdgiClipmapProbeCountX, Is.EqualTo(2));
                Assert.That(settings.DdgiClipmapProbeCountY, Is.EqualTo(2));
                Assert.That(settings.DdgiClipmapProbeCountZ, Is.EqualTo(2));
                Assert.That(settings.DdgiClipmapBaseSpacing, Is.EqualTo(0.25f));
                Assert.That(settings.DdgiClipmapSpacingScale, Is.EqualTo(1.25f));
                Assert.That(settings.DdgiClipmapVerticalCenterOffset, Is.EqualTo(-64.0f));
                Assert.That(settings.DdgiClipmapEdgeBlendFraction, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiClipmapSafetyMarginCells, Is.EqualTo(0));
                Assert.That(settings.DdgiFrustumPriorityWeight, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiOutOfFrustumMinimumUpdateFraction, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiNewProbeUpdateBoost, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiGpuScheduleTimeBudgetMilliseconds, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiGpuSchedulerValidationFailureThreshold, Is.EqualTo(1));
                Assert.That(settings.DdgiGpuSchedulerFallbackRetryStableFrames, Is.EqualTo(1));
                Assert.That(settings.DdgiAdaptiveBudgetHysteresisFraction, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiEmergencyDegradeGpuTimeMultiplier, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiMinimumProbeRefreshFrames, Is.EqualTo(1));
                Assert.That(settings.DdgiTeleportResetDistance, Is.EqualTo(1.0f));
                Assert.That(settings.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(0));
                Assert.That(settings.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(0));
                Assert.That(settings.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(0));
                Assert.That(settings.DdgiColdStartPrimaryRayBudget, Is.EqualTo(0));
                Assert.That(settings.DdgiMaxRaysPerProbe, Is.EqualTo(GlobalIlluminationProbeVolume.MinRaysPerProbe));
                Assert.That(settings.DdgiCascade0MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiCascade1MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiCascade2MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiCascade3MaxRayDistance, Is.EqualTo(0.1f));
                Assert.That(settings.DdgiMaxShadedLights, Is.EqualTo(0));
                Assert.That(settings.DdgiMaterialTextureMaxCascade, Is.EqualTo(-1));
                Assert.That(settings.DdgiThinWallProxyThickness, Is.EqualTo(0.01f));
                Assert.That(settings.DdgiThinWallLeakClampStrength, Is.EqualTo(0.0f));
                Assert.That(settings.DdgiRelocationTargetSurfaceDistanceFraction, Is.EqualTo(0.02f));
                Assert.That(settings.DdgiRelocationMinSurfaceDistance, Is.EqualTo(0.01f));
                Assert.That(settings.DdgiRelocationMaxDistanceFraction, Is.EqualTo(0.05f));
                Assert.That(settings.DdgiRelocationBlendAlpha, Is.EqualTo(0.02f));
            });

            settings.Enabled = false;

            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveUseSsgi, Is.False);
                Assert.That(settings.EffectiveUseDdgi, Is.False);
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.False);
            });
        }

        [Test]
        public void GlobalIlluminationSettings_RayQueryBackendFollowsDdgiModes()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                UseRayQueryBackend = true,
                UseDdgi = true,
                UseSsgi = true
            };

            Assert.Multiple(() =>
            {
                settings.Mode = GlobalIlluminationMode.Ssgi;
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.False);

                settings.Mode = GlobalIlluminationMode.Ddgi;
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.True);

                settings.Mode = GlobalIlluminationMode.Hybrid;
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.True);

                settings.Mode = GlobalIlluminationMode.RayQueryHybrid;
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.True);

                settings.UseDdgi = false;
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.False);
            });
        }

        [Test]
        public void GlobalIlluminationSettings_EffectiveModesRespectFeatureToggles()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Hybrid,
                UseSsgi = false,
                UseDdgi = false,
                UseRayQueryBackend = true
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveUseSsgi, Is.False);
                Assert.That(settings.EffectiveUseDdgi, Is.False);
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.False);

                settings.UseDdgi = true;
                Assert.That(settings.EffectiveUseDdgi, Is.True);
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.True);

                settings.UseSsgi = true;
                settings.UseDdgi = false;
                Assert.That(settings.EffectiveUseSsgi, Is.True);
                Assert.That(settings.EffectiveUseDdgi, Is.False);
                Assert.That(settings.EffectiveUseRayQueryBackend, Is.False);
            });
        }

        [Test]
        public void AntiAliasingSettings_ClampToSupportedRanges()
        {
            var settings = new AntiAliasingSettings
            {
                Mode = AntiAliasingMode.Taa,
                FxaaContrastThreshold = 99f,
                FxaaRelativeThreshold = -1f,
                FxaaSubpixelBlending = 2f,
                JitterSampleCount = 99,
                TaaFeedbackMin = 0f,
                TaaFeedbackMax = 2f,
                TaaVelocityRejectionScale = -1f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveMode, Is.EqualTo(AntiAliasingMode.Taa));
                Assert.That(settings.EffectiveSmaaSpatialSampleCount, Is.EqualTo(0));
                Assert.That(settings.EffectiveSmaaUsesSpatialMultisampling, Is.False);
                Assert.That(settings.EffectiveSmaaThreshold, Is.EqualTo(0.0f));
                Assert.That(settings.EffectiveSmaaMaxSearchSteps, Is.EqualTo(0));
                Assert.That(settings.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(0));
                Assert.That(settings.EffectiveSmaaCornerRounding, Is.EqualTo(0.0f));
                Assert.That(settings.EffectiveSmaaDiagonalEnabled, Is.False);
                Assert.That(settings.EffectiveSmaaCornerEnabled, Is.False);
                Assert.That(settings.FxaaContrastThreshold, Is.EqualTo(0.333f));
                Assert.That(settings.FxaaRelativeThreshold, Is.EqualTo(0.063f));
                Assert.That(settings.FxaaSubpixelBlending, Is.EqualTo(1.0f));
                Assert.That(settings.JitterSampleCount, Is.EqualTo(16));
                Assert.That(settings.TaaFeedbackMin, Is.EqualTo(0.2f));
                Assert.That(settings.TaaFeedbackMax, Is.EqualTo(0.99f));
                Assert.That(settings.TaaVelocityRejectionScale, Is.EqualTo(0.0f));
            });
        }

        [Test]
        public void FogSettings_ClampToSupportedRanges()
        {
            var settings = new FogSettings
            {
                ColorBlend = 9f,
                Density = -1f,
                StartDistance = 100f,
                EndDistance = 50f,
                HeightFalloff = 0f,
                HeightDensity = 9f,
                MaxOpacity = -1f,
                DirectionalInscatteringIntensity = 99f,
                DirectionalInscatteringExponent = 0f
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.ColorBlend, Is.EqualTo(1.0f));
                Assert.That(settings.Density, Is.EqualTo(0.0f));
                Assert.That(settings.StartDistance, Is.EqualTo(100.0f));
                Assert.That(settings.EndDistance, Is.EqualTo(100.01f).Within(0.0001f));
                Assert.That(settings.HeightFalloff, Is.EqualTo(0.001f));
                Assert.That(settings.HeightDensity, Is.EqualTo(1.0f));
                Assert.That(settings.MaxOpacity, Is.EqualTo(0.0f));
                Assert.That(settings.DirectionalInscatteringIntensity, Is.EqualTo(8.0f));
                Assert.That(settings.DirectionalInscatteringExponent, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void FoliageSettings_ClampToSupportedRanges()
        {
            var settings = new FoliageSettings
            {
                GrassShadowDistance = -1f,
                MaxDrawDistance = float.PositiveInfinity,
                DensityScale = 99f,
                MaxVisibleClusters = -5,
                MaxVisibleMeshletDraws = 99_000_000,
                DebugView = FoliageDebugView.AlphaCutoff
            };

            Assert.Multiple(() =>
            {
                Assert.That(settings.GrassShadowDistance, Is.EqualTo(0.0f));
                Assert.That(settings.MaxDrawDistance, Is.EqualTo(0.0f));
                Assert.That(settings.DensityScale, Is.EqualTo(8.0f));
                Assert.That(settings.MaxVisibleClusters, Is.EqualTo(0));
                Assert.That(settings.MaxVisibleMeshletDraws, Is.EqualTo(8_388_608));
                Assert.That(settings.DebugView, Is.EqualTo(FoliageDebugView.AlphaCutoff));
            });
        }

        [Test]
        public void AntiAliasingSettings_SupportsSmaaQualityModes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.SmaaLow), Is.True);
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.SmaaMedium), Is.True);
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.SmaaHigh), Is.True);
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.Fxaa), Is.False);
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.Taa), Is.False);
                Assert.That(AntiAliasingSettings.IsSmaaMode(AntiAliasingMode.None), Is.False);
            });
        }

        [Test]
        public void AntiAliasingSettings_SmaaPresetsUseExpectedValues()
        {
            var settings = new AntiAliasingSettings();

            settings.Mode = AntiAliasingMode.SmaaLow;
            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveSmaaSpatialSampleCount, Is.EqualTo(1));
                Assert.That(settings.EffectiveSmaaUsesSpatialMultisampling, Is.False);
                Assert.That(settings.EffectiveSmaaThreshold, Is.EqualTo(0.10f));
                Assert.That(settings.EffectiveSmaaMaxSearchSteps, Is.EqualTo(16));
                Assert.That(settings.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(8));
                Assert.That(settings.EffectiveSmaaCornerRounding, Is.EqualTo(25.0f));
                Assert.That(settings.EffectiveSmaaDiagonalEnabled, Is.True);
                Assert.That(settings.EffectiveSmaaCornerEnabled, Is.True);
            });

            settings.Mode = AntiAliasingMode.SmaaMedium;
            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveSmaaSpatialSampleCount, Is.EqualTo(2));
                Assert.That(settings.EffectiveSmaaUsesSpatialMultisampling, Is.True);
                Assert.That(settings.EffectiveSmaaThreshold, Is.EqualTo(0.10f));
                Assert.That(settings.EffectiveSmaaMaxSearchSteps, Is.EqualTo(16));
                Assert.That(settings.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(8));
                Assert.That(settings.EffectiveSmaaCornerRounding, Is.EqualTo(25.0f));
                Assert.That(settings.EffectiveSmaaDiagonalEnabled, Is.True);
                Assert.That(settings.EffectiveSmaaCornerEnabled, Is.True);
            });

            settings.Mode = AntiAliasingMode.SmaaHigh;
            Assert.Multiple(() =>
            {
                Assert.That(settings.EffectiveSmaaSpatialSampleCount, Is.EqualTo(4));
                Assert.That(settings.EffectiveSmaaUsesSpatialMultisampling, Is.True);
                Assert.That(settings.EffectiveSmaaThreshold, Is.EqualTo(0.10f));
                Assert.That(settings.EffectiveSmaaMaxSearchSteps, Is.EqualTo(16));
                Assert.That(settings.EffectiveSmaaMaxSearchStepsDiagonal, Is.EqualTo(8));
                Assert.That(settings.EffectiveSmaaCornerRounding, Is.EqualTo(25.0f));
                Assert.That(settings.EffectiveSmaaDiagonalEnabled, Is.True);
                Assert.That(settings.EffectiveSmaaCornerEnabled, Is.True);
            });
        }

        [Test]
        public void HdrSceneColorFormat_UsesHalfFloatRgba()
        {
            Assert.That(RenderTargetManager.SceneColorFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
        }

        [Test]
        public void FoggedSceneColorFormat_MatchesHdrSceneColor()
        {
            Assert.That(RenderTargetManager.FoggedSceneColorFormat, Is.EqualTo(RenderTargetManager.SceneColorFormat));
        }

        [Test]
        public void AmbientOcclusionFormat_UsesSingleChannelUnorm()
        {
            Assert.That(RenderTargetManager.AmbientOcclusionFormat, Is.EqualTo(Format.R8Unorm));
        }

        [Test]
        public void GlobalIlluminationFormats_UseHalfFloatTargets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderTargetManager.SceneNormalFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
                Assert.That(RenderTargetManager.SceneMaterialFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
                Assert.That(RenderTargetManager.SsgiFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
                Assert.That(RenderTargetManager.SsgiHitDistanceFormat, Is.EqualTo(Format.R16Sfloat));
                Assert.That(RenderTargetManager.SsgiMomentsFormat, Is.EqualTo(Format.R16G16Sfloat));
                Assert.That(RenderTargetManager.SsgiHistoryLengthFormat, Is.EqualTo(Format.R16Sfloat));
                Assert.That(RenderTargetManager.GiFinalDiffuseFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
            });
        }

        [Test]
        public void AntiAliasingFormats_UseExpectedTargets()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderTargetManager.LdrSceneColorFormat, Is.EqualTo(Format.R16G16B16A16Sfloat));
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
            Assert.That(
                RenderTarget.CalculateByteSize(1920, 1080, Format.R16Sfloat),
                Is.EqualTo(1920UL * 1080UL * 2UL));
            Assert.That(
                ImageByteEstimator.EstimateBytes(Format.R16Sfloat, new Extent3D(1920, 1080, 1)),
                Is.EqualTo(1920UL * 1080UL * 2UL));
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
                Assert.That(Math.Abs(sample0.X), Is.LessThanOrEqualTo(1.0f / 1920f));
                Assert.That(Math.Abs(sample0.Y), Is.LessThanOrEqualTo(1.0f / 1080f));
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
        public void GlobalIlluminationExtents_ClampScaleAndRoundUpOddSizes()
        {
            var quarter = RenderTargetManager.CalculateGlobalIlluminationExtent(new Extent2D { Width = 1919, Height = 1079 }, 0.25f);
            var half = RenderTargetManager.CalculateGlobalIlluminationExtent(new Extent2D { Width = 1919, Height = 1079 }, 0.5f);
            var full = RenderTargetManager.CalculateGlobalIlluminationExtent(new Extent2D { Width = 1919, Height = 1079 }, 1.0f);

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
        public void GlobalIlluminationRenderTargetBytes_FollowEffectiveSsgiMode()
        {
            var extent = new Extent2D { Width = 1920, Height = 1080 };
            var ddgiOnly = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseSsgi = false,
                UseDdgi = true
            };
            var ssgiOnly = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ssgi,
                UseSsgi = true,
                UseDdgi = false
            };
            var hybrid = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Hybrid,
                UseSsgi = true,
                UseDdgi = true
            };
            var disabled = new GlobalIlluminationSettings
            {
                Enabled = false,
                Mode = GlobalIlluminationMode.Disabled,
                UseSsgi = true,
                UseDdgi = true
            };

            ulong ddgiOnlySsgiBytes = RenderTargetManager.CalculateGlobalIlluminationRenderTargetBytes(
                extent,
                resolutionScale: 0.5f,
                ddgiOnly.EffectiveUseSsgi);
            ulong ddgiOnlySurfaceBytes = RenderTargetManager.CalculateSceneSurfaceRenderTargetBytes(
                extent,
                ddgiOnly.EffectiveUseSsgi);
            ulong ssgiOnlyBytes = RenderTargetManager.CalculateGlobalIlluminationRenderTargetBytes(
                extent,
                resolutionScale: 0.5f,
                ssgiOnly.EffectiveUseSsgi);
            ulong hybridBytes = RenderTargetManager.CalculateGlobalIlluminationRenderTargetBytes(
                extent,
                resolutionScale: 0.5f,
                hybrid.EffectiveUseSsgi);
            ulong disabledBytes = RenderTargetManager.CalculateGlobalIlluminationRenderTargetBytes(
                extent,
                resolutionScale: 0.5f,
                disabled.EffectiveUseSsgi);

            Assert.Multiple(() =>
            {
                Assert.That(ddgiOnly.EffectiveUseDdgi, Is.True);
                Assert.That(ddgiOnly.EffectiveUseSsgi, Is.False);
                Assert.That(ddgiOnlySsgiBytes, Is.EqualTo(0));
                Assert.That(ddgiOnlySurfaceBytes, Is.EqualTo(0));
                Assert.That(ssgiOnly.EffectiveUseSsgi, Is.True);
                Assert.That(ssgiOnlyBytes, Is.GreaterThan(0));
                Assert.That(hybrid.EffectiveUseSsgi, Is.True);
                Assert.That(hybrid.EffectiveUseDdgi, Is.True);
                Assert.That(hybridBytes, Is.EqualTo(ssgiOnlyBytes));
                Assert.That(disabled.EffectiveUseSsgi, Is.False);
                Assert.That(disabled.EffectiveUseDdgi, Is.False);
                Assert.That(disabledBytes, Is.EqualTo(0));
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

        [Test]
        public void BloomRenderTargetBytes_UsesSingleMipChain()
        {
            var extent = new Extent2D { Width = 3840, Height = 2160 };
            var mipExtents = RenderTargetManager.CalculateBloomMipExtents(extent, 6);
            ulong expectedBytes = 0;
            for (int i = 0; i < mipExtents.Count; i++)
                expectedBytes += RenderTarget.CalculateByteSize(mipExtents[i].Width, mipExtents[i].Height, RenderTargetManager.SceneColorFormat);

            ulong actualBytes = RenderTargetManager.CalculateBloomRenderTargetBytes(extent, 6);

            Assert.Multiple(() =>
            {
                Assert.That(actualBytes, Is.EqualTo(expectedBytes));
                Assert.That(actualBytes, Is.LessThan(expectedBytes * 2UL));
            });
        }

        [Test]
        public void FoggedSceneColorExtent_UsesPlaceholderWhenFogDisabled()
        {
            var extent = new Extent2D { Width = 1920, Height = 1080 };
            var enabled = RenderTargetManager.CalculateFoggedSceneColorExtent(extent, enabled: true);
            var disabled = RenderTargetManager.CalculateFoggedSceneColorExtent(extent, enabled: false);

            Assert.Multiple(() =>
            {
                Assert.That(enabled.Width, Is.EqualTo(1920));
                Assert.That(enabled.Height, Is.EqualTo(1080));
                Assert.That(disabled.Width, Is.EqualTo(1));
                Assert.That(disabled.Height, Is.EqualTo(1));
            });
        }

        [TestCase(8)]
        [TestCase(32)]
        [TestCase(64)]
        [TestCase(128)]
        public void EstimateDdgiShadowRayUpperBound_IsBoundedForLargeLocalLightCounts(int localLightCount)
        {
            const ulong primaryRayCount = 4096UL;

            ulong estimate = VulkanRenderer.EstimateDdgiShadowRayUpperBound(
                primaryRayCount,
                directionalLightCount: 1,
                localLightCount,
                maxShadedLights: 64);

            Assert.That(estimate, Is.EqualTo(primaryRayCount * 2UL));
        }

        [Test]
        public void EstimateDdgiShadowRayUpperBound_RespectsSelectionCapacityAndDisabledCap()
        {
            const ulong primaryRayCount = 1024UL;

            Assert.Multiple(() =>
            {
                Assert.That(
                    VulkanRenderer.EstimateDdgiShadowRayUpperBound(primaryRayCount, 1, 128, maxShadedLights: 1),
                    Is.EqualTo(primaryRayCount));
                Assert.That(
                    VulkanRenderer.EstimateDdgiShadowRayUpperBound(primaryRayCount, 0, 128, maxShadedLights: 1),
                    Is.EqualTo(primaryRayCount));
                Assert.That(
                    VulkanRenderer.EstimateDdgiShadowRayUpperBound(primaryRayCount, 1, 128, maxShadedLights: 0),
                    Is.Zero);
                Assert.That(
                    VulkanRenderer.EstimateDdgiShadowRayUpperBound(primaryRayCount, 0, 0, maxShadedLights: 64),
                    Is.Zero);
            });
        }

        [Test]
        public void SelectPrimaryDdgiDirectionalLight_ChoosesHighestRadianceDirectional()
        {
            Light[] lights =
            [
                new Light
                {
                    Type = LightType.Directional,
                    Color = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f),
                    Intensity = 1.0f
                },
                new Light
                {
                    Type = LightType.Point,
                    Color = new System.Numerics.Vector3(10.0f, 10.0f, 10.0f),
                    Intensity = 10.0f,
                    Range = 8.0f
                },
                new Light
                {
                    Type = LightType.Directional,
                    Color = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f),
                    Intensity = 4.0f
                }
            ];
            var snapshot = new LightFrameSnapshot(lights, lights.Length, 2, 1, -1, default, revision: 1);

            int selected = VulkanRenderer.SelectPrimaryDdgiDirectionalLight(snapshot);

            Assert.That(selected, Is.EqualTo(2));
        }

        [Test]
        public void DdgiPassExecutionDiagnostics_ReportsPreciseInactiveReason()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                UseRayQueryBackend = true
            };
            var sceneData = new SceneRenderingData
            {
                DdgiProbeVolumeCount = 1,
                DdgiProbesUpdated = 8
            };

            Assert.Multiple(() =>
            {
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolveUpdateSkipReason(true, settings, true, sceneData),
                    Is.Empty);
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.Empty);
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolveUpdateSkipReason(false, settings, true, sceneData),
                    Is.EqualTo("pipeline-unavailable"));

                settings.Enabled = false;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.EqualTo("global-illumination-disabled"));

                settings.Enabled = true;
                settings.UseDdgi = false;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.EqualTo("ddgi-disabled"));

                settings.UseDdgi = true;
                settings.UseRayQueryBackend = false;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.EqualTo("ray-query-backend-disabled"));

                settings.UseRayQueryBackend = true;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, false, sceneData),
                    Is.EqualTo("acceleration-structure-inactive"));

                sceneData.DdgiProbeVolumeCount = 0;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.EqualTo("no-ddgi-volumes"));

                sceneData.DdgiProbeVolumeCount = 1;
                sceneData.DdgiProbesUpdated = 0;
                Assert.That(
                    DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(settings, true, sceneData),
                    Is.EqualTo("no-ddgi-updates"));
            });
        }

        [Test]
        public void SelectPrimaryDdgiLocalLight_ChoosesHighestEnergyLocalAndReportsWeights()
        {
            Light[] lights =
            [
                new Light
                {
                    Type = LightType.Directional,
                    Color = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f),
                    Intensity = 100.0f
                },
                new Light
                {
                    Type = LightType.Point,
                    Color = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f),
                    Intensity = 1.0f,
                    Range = 2.0f
                },
                new Light
                {
                    Type = LightType.Point,
                    Color = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f),
                    Intensity = 1.0f,
                    Range = 4.0f
                }
            ];
            var snapshot = new LightFrameSnapshot(lights, lights.Length, 1, 2, -1, default, revision: 1);

            int selected = VulkanRenderer.SelectPrimaryDdgiLocalLight(snapshot, out float selectedWeight, out float totalWeight);

            Assert.Multiple(() =>
            {
                Assert.That(selected, Is.EqualTo(2));
                Assert.That(selectedWeight, Is.EqualTo(16.0f).Within(0.0001f));
                Assert.That(totalWeight / selectedWeight, Is.EqualTo(1.25f).Within(0.0001f));
            });
        }
    }
}
