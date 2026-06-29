using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceSnapshotWriterTests
{
    [Test]
    public void PerformanceSnapshotWriter_IncludesFoliageSummary()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            FoliagePatchCount = 1,
            FoliageClusterCount = 32,
            FoliageVisibleClusterCount = 24,
            FoliageVisibleMeshletDrawCount = 96,
            FoliageDdgiSampleCount = 120,
            FoliageInstanceBufferBytes = 1024,
            GpuFoliageForwardMicroseconds = 250,
            HiZEnabled = 1,
            OcclusionEnabled = 1,
            HiZConsumerCount = 1,
            HiZConsumerSummary = "SceneSubmissionPreviousHiZ",
            HiZBuildSkippedBecauseNoConsumer = 0,
            HiZCounterSource = HiZCounterSource.SceneSubmissionCompaction,
            ForwardHiZTestedCount = 128,
            ForwardHiZCulledCount = 32,
            ForwardHiZCullRate = 0.25f,
            HiZFallbackPath = HiZFallbackPaths.PreviousFrameSceneSubmission,
            HiZFallbackReason = "previous valid",
            HiZValidateAgainstLegacyPath = 1,
            PreviousHiZFrameValid = 1,
            PreviousHiZSkippedInvalidHistory = 0,
            PreviousHiZSkippedCameraMotion = 1,
            PreviousHiZTested = 128,
            PreviousHiZCulled = 32,
            CpuHiZDepthTransitionMicroseconds = 2,
            CpuHiZPyramidTransitionMicroseconds = 3,
            CpuHiZDescriptorBindMicroseconds = 4,
            CpuHiZPushDispatchMicroseconds = 5,
            CpuHiZFinalBarrierMicroseconds = 6,
            HiZPolicyCounterSource = HiZCounterSource.SceneSubmissionCompaction,
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDebugView = GlobalIlluminationDebugView.FinalIndirect,
            GlobalIlluminationRayQuerySupported = 1,
            GlobalIlluminationRayQueryActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationDdgiActive = 1,
            SsgiWidth = 960,
            SsgiHeight = 540,
            SsgiResolutionScale = 0.5f,
            SsgiRayCount = 6,
            DdgiProbeCount = 128,
            DdgiActiveProbeCount = 96,
            DdgiProbesUpdated = 8,
            DdgiProbeUpdatePrimaryRayBudget = 32768,
            DdgiSchedulerMode = DdgiSchedulerMode.CpuReference,
            DdgiQualityTier = DdgiQualityTier.DdgiHigh,
            DdgiAdaptiveBudgetScale = 0.75f,
            DdgiAdaptiveBudgetReduced = 1,
            DdgiEmergencyDegradeActive = 1,
            DdgiEffectiveMaxShadedLights = 4,
            DdgiAdaptiveBudgetReason = "emergency-degrade",
            DdgiScheduledPrimaryRayCount = 768,
            DdgiEstimatedShadowRayUpperBound = 1_536,
            DdgiSelectedDirectionalHitCount = 768,
            DdgiSelectedLocalHitCount = 768,
            DdgiVisibilityRayCount = 1_536,
            DdgiSkippedLocalLightCount = 23_040,
            DdgiLightSelectionMode = "bounded-directional-local",
            DdgiEmissiveSourceCount = 3,
            DdgiEmissiveSourceRevision = 7,
            ParticleDdgiSampleCount = 5,
            VfxDdgiDirtyProbeEventCount = 2,
            DdgiDirtyBoundsProbeUpdateCount = 2,
            DdgiHighVarianceProbeUpdateCount = 4,
            DdgiLowConfidenceProbeUpdateCount = 3,
            DdgiStableProbeUpdateCount = 1,
            DdgiAverageProbeVariability = 0.42f,
            DdgiAverageProbeConfidence = 0.67f,
            CpuDdgiSchedulerMicroseconds = 104,
            CpuDdgiSchedulerP95Microseconds = 231,
            CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds = 11,
            CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds = 12,
            CpuDdgiSchedulerPhaseUninitializedMicroseconds = 13,
            CpuDdgiSchedulerPhaseFrustumMicroseconds = 14,
            CpuDdgiSchedulerPhaseSafetyMicroseconds = 15,
            CpuDdgiSchedulerPhaseRoundRobinMicroseconds = 16,
            CpuDdgiSchedulerCandidateInsertCount = 17,
            CpuDdgiSchedulerCandidateMaxShiftCount = 18,
            DdgiSchedulerTimingSampleCount = 17,
            DdgiSchedulerP95OverBudget = 0,
            GlobalIlluminationRenderTargetBytes = 2048,
            SsgiRenderTargetBytes = 2048,
            SceneSurfaceRenderTargetBytes = 4096,
            DdgiCurrentIrradianceAtlasBytes = 1024,
            DdgiGpuSchedulerBufferBytes = 4096,
            DdgiGpuSchedulerDirtyRegionCapacity = 64,
            DdgiGpuSchedulerCandidateCapacity = 128,
            DdgiGpuSchedulerGroupCountCapacity = 32,
            DdgiGpuSchedulerPrefixCapacity = 48,
            DdgiGpuSchedulerDirtyRegionCount = 7,
            DdgiGpuSchedulerDirtyRegionOverflowCount = 2,
            DdgiGpuSchedulerResourceReinitializationCount = 1,
            DdgiGpuSchedulerTotalResourceReinitializationCount = 3,
            DdgiGpuSchedulerUploadBytes = 2304,
            DdgiGpuSchedulerReadbackValid = 1,
            DdgiGpuSchedulerReadbackLatencyFrames = 2,
            DdgiGpuSchedulerFallbackActive = 1,
            DdgiGpuSchedulerFallbackReason = "compare-mode-cpu-queue",
            DdgiGpuSchedulerConsideredProbeCount = 23040,
            DdgiGpuSchedulerRequestCount = 19,
            DdgiGpuSchedulerPrimaryRayCount = 608,
            DdgiGpuSchedulerCandidateCount = 31,
            DdgiGpuSchedulerOverflowCount = 3,
            DdgiGpuSchedulerDuplicateRequestCount = 4,
            DdgiGpuSchedulerBudgetRejectedCount = 5,
            DdgiGpuSchedulerInvalidProbeCount = 6,
            DdgiGpuSchedulerVisibleFrustumCandidateCount = 11,
            DdgiGpuSchedulerSafetyShellCandidateCount = 12,
            DdgiGpuSchedulerAgeRefreshCandidateCount = 13,
            DdgiGpuSchedulerHighVarianceCandidateCount = 14,
            DdgiGpuSchedulerLowConfidenceCandidateCount = 15,
            DdgiGpuSchedulerStableSkippedCount = 16,
            DdgiGpuSchedulerPriority0RequestCount = 7,
            DdgiGpuSchedulerPriority1RequestCount = 8,
            DdgiGpuSchedulerPriority2RequestCount = 3,
            DdgiGpuSchedulerPriority3RequestCount = 1,
            DdgiGpuSchedulerRequestBudgetSaturated = 1,
            DdgiGpuSchedulerPrimaryRayBudgetSaturated = 0,
            DdgiGpuSchedulerValidationValid = 1,
            DdgiGpuSchedulerValidationStatus = "mismatch",
            DdgiGpuSchedulerValidationCpuRequestCount = 21,
            DdgiGpuSchedulerValidationGpuRequestCount = 19,
            DdgiGpuSchedulerValidationComparedRequestCount = 19,
            DdgiGpuSchedulerValidationMismatchCount = 2,
            DdgiGpuSchedulerValidationSampleLimit = 4096,
            DdgiGpuSchedulerValidationFirstMismatch = "request count drift exceeds 10%",
            DdgiRayScratchBytes = 20_480,
            DdgiUpdatedAtlasBytes = 12_288,
            DdgiPublishedCacheLatencyFrames = 1,
            AccelerationStructureBlasBuildCount = 1,
            AccelerationStructureTlasBuildCount = 1,
            GpuSsgiTraceMicroseconds = 350,
            GpuSsgiDenoiseMicroseconds = 150,
            GpuDdgiScheduleMicroseconds = 4,
            GpuDdgiScheduleP95Microseconds = 240,
            GpuDdgiScheduleOverBudget = 0,
            GpuDdgiTraceMicroseconds = 20,
            GpuDdgiBlendMicroseconds = 3,
            GpuDdgiRelocateClassifyMicroseconds = 2,
            GpuDdgiPublishMicroseconds = 1,
            GpuDdgiUpdateMicroseconds = 30,
            GpuAccelerationStructureTlasMicroseconds = 75,
            Graph = new RenderGraphDiagnostics(
                ResourceCount: 2,
                PassCount: 1,
                PlannedBarrierCount: 3,
                ExecutedBarrierCount: 3,
                TransientResourceCount: 1,
                PersistentResourceCount: 1,
                AliasableResourceCount: 1,
                ImportedResourceCount: 1,
                OwnedRenderTargetCount: 1,
                AsyncComputeCandidatePassCount: 1,
                AsyncComputeEnabledPassCount: 0,
                QueueOwnershipTransitionCount: 1,
                ResourceMemoryEstimateBytes: 4096,
                Resources:
                [
                    new RenderGraphResourceDiagnostics(
                        "LdrSceneColor",
                        "LDR scene color",
                        "Image",
                        "R16G16B16A16Sfloat",
                        "Swapchain",
                        "Persistent",
                        true,
                        true,
                        1,
                        4096)
                ],
                Passes:
                [
                    new RenderGraphPassDiagnostics(
                        "ToneMapCompositePass",
                        EnabledByFeatureIsolation: true,
                        QueueIntent: "Graphics",
                        AsyncComputeCandidate: false,
                        AsyncComputeEnabled: false,
                        AsyncComputeReason: "Pass is not marked safe for async compute scheduling.",
                        Reads: ["SceneColor"],
                        Writes: ["LdrSceneColor"],
                        ReadWrites: [])
                ],
                Barriers:
                [
                    new RenderGraphBarrierDiagnostics(
                        "ToneMapCompositePass",
                        "LdrSceneColor",
                        "Read",
                        "Write",
                        "ShaderReadOnlyOptimal",
                        "ColorAttachmentOptimal",
                        "FragmentShaderBit",
                        "ShaderSampledReadBit",
                        "ColorAttachmentOutputBit",
                        "ColorAttachmentWriteBit",
                        "Compute",
                        "Graphics",
                        QueueOwnershipTransition: true,
                        Executed: true)
                ])
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        string path = new PerformanceSnapshotWriter().Write(directory, diagnostics, budget);
        string json = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Foliage\""));
            Assert.That(json, Does.Contain("\"GlobalIllumination\""));
            Assert.That(json, Does.Contain("\"ActiveQualityPreset\": 4"));
            Assert.That(json, Does.Contain("\"Mode\": 2"));
            Assert.That(json, Does.Contain("\"RayQueryActive\": true"));
            Assert.That(json, Does.Contain("\"SsgiActive\": false"));
            Assert.That(json, Does.Contain("\"DdgiActive\": true"));
            Assert.That(json, Does.Contain("\"Graph\""));
            Assert.That(json, Does.Contain("\"Warnings\": []"));
            Assert.That(json, Does.Contain("\"HiZConsumerCount\": 1"));
            Assert.That(json, Does.Contain("\"HiZConsumerSummary\": \"SceneSubmissionPreviousHiZ\""));
            Assert.That(json, Does.Contain("\"HiZBuildSkippedBecauseNoConsumer\": 0"));
            Assert.That(json, Does.Contain("\"HiZCounterSource\": 2"));
            Assert.That(json, Does.Contain("\"ForwardHiZTestedCount\": 128"));
            Assert.That(json, Does.Contain("\"ForwardHiZCulledCount\": 32"));
            Assert.That(json, Does.Contain("\"ForwardHiZCullRate\": 0.25"));
            Assert.That(json, Does.Contain("\"HiZFallbackPath\": \"PreviousFrameSceneSubmission\""));
            Assert.That(json, Does.Contain("\"HiZFallbackReason\": \"previous valid\""));
            Assert.That(json, Does.Contain("\"HiZValidateAgainstLegacyPath\": 1"));
            Assert.That(json, Does.Contain("\"PreviousHiZFrameValid\": 1"));
            Assert.That(json, Does.Contain("\"PreviousHiZSkippedInvalidHistory\": 0"));
            Assert.That(json, Does.Contain("\"PreviousHiZSkippedCameraMotion\": 1"));
            Assert.That(json, Does.Contain("\"PreviousHiZTested\": 128"));
            Assert.That(json, Does.Contain("\"PreviousHiZCulled\": 32"));
            Assert.That(json, Does.Contain("\"CpuHiZDepthTransitionMicroseconds\": 2"));
            Assert.That(json, Does.Contain("\"CpuHiZPyramidTransitionMicroseconds\": 3"));
            Assert.That(json, Does.Contain("\"CpuHiZDescriptorBindMicroseconds\": 4"));
            Assert.That(json, Does.Contain("\"CpuHiZPushDispatchMicroseconds\": 5"));
            Assert.That(json, Does.Contain("\"CpuHiZFinalBarrierMicroseconds\": 6"));
            Assert.That(json, Does.Contain("\"HiZPolicyCounterSource\": 2"));
            Assert.That(json, Does.Contain("\"ResourceCount\": 2"));
            Assert.That(json, Does.Contain("\"LdrSceneColor\""));
            Assert.That(json, Does.Contain("\"VisibleMeshletDrawCount\": 96"));
            Assert.That(json, Does.Contain("\"DdgiSampleCount\": 120"));
            Assert.That(json, Does.Contain("\"BufferBytes\": 1024"));
            Assert.That(json, Does.Contain("\"SsgiWidth\": 960"));
            Assert.That(json, Does.Contain("\"SsgiRayCount\": 6"));
            Assert.That(json, Does.Contain("\"DdgiProbeUpdatePrimaryRayBudget\": 32768"));
            Assert.That(json, Does.Contain("\"DdgiSchedulerMode\": 0"));
            Assert.That(json, Does.Contain("\"DdgiQualityTier\": 2"));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetScale\": 0.75"));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetReduced\": 1"));
            Assert.That(json, Does.Contain("\"DdgiEmergencyDegradeActive\": 1"));
            Assert.That(json, Does.Contain("\"DdgiEffectiveMaxShadedLights\": 4"));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetReason\": \"emergency-degrade\""));
            Assert.That(json, Does.Contain("\"DdgiScheduledPrimaryRayCount\": 768"));
            Assert.That(json, Does.Contain("\"DdgiEstimatedShadowRayUpperBound\": 1536"));
            Assert.That(json, Does.Contain("\"DdgiSelectedDirectionalHitCount\": 768"));
            Assert.That(json, Does.Contain("\"DdgiSelectedLocalHitCount\": 768"));
            Assert.That(json, Does.Contain("\"DdgiVisibilityRayCount\": 1536"));
            Assert.That(json, Does.Contain("\"DdgiSkippedLocalLightCount\": 23040"));
            Assert.That(json, Does.Contain("\"DdgiLightSelectionMode\": \"bounded-directional-local\""));
            Assert.That(json, Does.Contain("\"DdgiEmissiveSourceCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiEmissiveSourceRevision\": 7"));
            Assert.That(json, Does.Contain("\"ParticleDdgiSampleCount\": 5"));
            Assert.That(json, Does.Contain("\"VfxDirtyProbeEventCount\": 2"));
            Assert.That(json, Does.Contain("\"DdgiHighVarianceProbeUpdateCount\": 4"));
            Assert.That(json, Does.Contain("\"DdgiLowConfidenceProbeUpdateCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiStableProbeUpdateCount\": 1"));
            Assert.That(json, Does.Contain("\"DdgiAverageProbeVariability\": 0.42"));
            Assert.That(json, Does.Contain("\"DdgiAverageProbeConfidence\": 0.67"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerMicroseconds\": 104"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerP95Microseconds\": 231"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds\": 11"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds\": 12"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseUninitializedMicroseconds\": 13"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseFrustumMicroseconds\": 14"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseSafetyMicroseconds\": 15"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerPhaseRoundRobinMicroseconds\": 16"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerCandidateInsertCount\": 17"));
            Assert.That(json, Does.Contain("\"CpuDdgiSchedulerCandidateMaxShiftCount\": 18"));
            Assert.That(json, Does.Contain("\"DdgiSchedulerTimingSampleCount\": 17"));
            Assert.That(json, Does.Contain("\"DdgiSchedulerP95OverBudget\": 0"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerBufferBytes\": 4096"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerDirtyRegionCapacity\": 64"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerCandidateCapacity\": 128"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerGroupCountCapacity\": 32"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPrefixCapacity\": 48"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerDirtyRegionCount\": 7"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerDirtyRegionOverflowCount\": 2"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerResourceReinitializationCount\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerTotalResourceReinitializationCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerUploadBytes\": 2304"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerReadbackValid\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerReadbackLatencyFrames\": 2"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerFallbackActive\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerFallbackReason\": \"compare-mode-cpu-queue\""));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerConsideredProbeCount\": 23040"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerRequestCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPrimaryRayCount\": 608"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerCandidateCount\": 31"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerOverflowCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerDuplicateRequestCount\": 4"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerBudgetRejectedCount\": 5"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerInvalidProbeCount\": 6"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerVisibleFrustumCandidateCount\": 11"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerSafetyShellCandidateCount\": 12"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerAgeRefreshCandidateCount\": 13"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerHighVarianceCandidateCount\": 14"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerLowConfidenceCandidateCount\": 15"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerStableSkippedCount\": 16"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPriority0RequestCount\": 7"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPriority1RequestCount\": 8"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPriority2RequestCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPriority3RequestCount\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerRequestBudgetSaturated\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPrimaryRayBudgetSaturated\": 0"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationValid\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationStatus\": \"mismatch\""));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationCpuRequestCount\": 21"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationGpuRequestCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationComparedRequestCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationMismatchCount\": 2"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationSampleLimit\": 4096"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerValidationFirstMismatch\": \"request count drift exceeds 10%\""));
            Assert.That(json, Does.Contain("\"DdgiRayScratchBytes\": 20480"));
            Assert.That(json, Does.Contain("\"DdgiUpdatedAtlasBytes\": 12288"));
            Assert.That(json, Does.Contain("\"DdgiPublishedCacheLatencyFrames\": 1"));
            Assert.That(json, Does.Contain("\"SceneSurfaceRenderTargetBytes\": 4096"));
            Assert.That(json, Does.Contain("\"AccelerationStructureTlasBuildCount\": 1"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleMicroseconds\": 4"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleP95Microseconds\": 240"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleOverBudget\": 0"));
            Assert.That(json, Does.Contain("\"GpuMicroseconds\": 605"));
            Assert.That(json, Does.Contain("\"LikelyBottleneck\": \"fragment-alpha-overdraw-or-forward-shading\""));
        });
    }

    [Test]
    public void PerformanceSnapshotWriter_WarnsAboutHiZBuildWithoutConsumersOrCounters()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-warning-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            HiZEnabled = 1,
            OcclusionEnabled = 1,
            HiZConsumerCount = 0,
            HiZCounterSource = HiZCounterSource.Unavailable,
            ForwardHiZTestedCount = 0,
            ForwardVisibilityCompactionEnabled = 1,
            ForwardVisibilityCompactionActive = 0,
            ForwardVisibilityCompactionSkipReason = "previous forward visibility compaction overflowed; using pre-Hi-Z compacted forward buffers this frame",
            SceneSubmissionGpuOpaqueOverflowCount = 2,
            SceneSubmissionValidationMismatchCount = 1
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        string path = new PerformanceSnapshotWriter().Write(directory, diagnostics, budget);
        string json = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("Hi-Z build is enabled but no active Hi-Z consumers were reported."));
            Assert.That(json, Does.Contain("Hi-Z build is enabled but no Hi-Z counter source is available."));
            Assert.That(json, Does.Contain("Hi-Z occlusion is enabled but no forward Hi-Z tests were reported."));
            Assert.That(json, Does.Contain("Current-frame forward visibility compaction fell back: previous forward visibility compaction overflowed"));
            Assert.That(json, Does.Contain("Scene-submission GPU opaque compaction overflowed."));
            Assert.That(json, Does.Contain("Scene-submission CPU/GPU validation reported mismatches."));
        });
    }
}
