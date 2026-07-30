using System.IO;
using System.Text.Json;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
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
            FoliageDdgiTransportExcludedClusterCount = 32,
            FoliageDdgiTransportExclusionReason =
                AccelerationStructureManager.FoliageDdgiExclusionReason,
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
            SimpleDdgiActive = 1,
            SimpleDdgiTransportV2Active = 1,
            SimpleDdgiAutomaticProbeDensityActive = 1,
            SimpleDdgiTransportSourceRefreshProbeCount = 8,
            SimpleDdgiTransportPublishedProbeCount = 37,
            SimpleDdgiTransportPublishRegionCount = 5,
            SimpleDdgiTransportPublishedProbeTotal = 9_437,
            SimpleDdgiTransportPublishRegionTotal = 1_205,
            SimpleDdgiUpdateTransactionAbortCount = 3,
            SimpleDdgiTransportSourceCacheInvalidationCount = 1_024,
            SimpleDdgiTransportSolverInvalidationCount = 2,
            SimpleDdgiTransportSolverInvalidationsPerSourceRefresh = 0.25f,
            SimpleDdgiSourceLightingGeneration = 12,
            SimpleDdgiTransportGeneration = 37,
            SimpleDdgiTransportGlobalConvergencePending = 1,
            SimpleDdgiTransportGlobalConvergenceElapsedFrames = 23,
            SimpleDdgiTransportCalibrationChangeCount = 4,
            SimpleDdgiStructuredGatherEnabled = 1,
            SimpleDdgiReducedBlendEnabled = 1,
            SimpleDdgiSampledAtlasRequested = 1,
            SimpleDdgiSampledAtlasActive = 1,
            SimpleDdgiSampledAtlasGroupCount = 2,
            SimpleDdgiSampledAtlasLayersPerTexture = 2048,
            SimpleDdgiSampledAtlasImageBytes = 3_932_160,
            SimpleDdgiToroidalScrollingEnabled = 1,
            SimpleDdgiRegionalInvalidationEnabled = 1,
            FarFieldPagedFeatureEnabled = 1,
            StreamedGiAccelerationStructuresFeatureEnabled = 1,
            CaptureGpuDeviceName = "Reference GPU",
            CaptureGpuDriverVersion = "999.1",
            CaptureRenderWidth = 1920,
            CaptureRenderHeight = 1080,
            CaptureSceneContentRevision = 42,
            GpuTimingValid = 1,
            GpuForwardGiGatherTimingCoverage = 1,
            GpuForwardGiGatherMicroseconds = 40,
            GpuFarFieldUpdateTimingValid = 1,
            GpuFarFieldUpdateMicroseconds = 10,
            CpuSsgiRecordMicroseconds = 11,
            CpuDdgiRecordMicroseconds = 13,
            CpuSimpleDdgiRecordMicroseconds = 17,
            CpuFarFieldRecordMicroseconds = 19,
            CpuGlobalIlluminationRecordMicroseconds = 164,
            CpuGlobalIlluminationRecordP95Microseconds = 200,
            GlobalIlluminationCpuTimingSampleCount = 3,
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
            SimpleDdgiInactiveProbeCount = 3,
            SimpleDdgiInactiveProbeSkipCount = 2,
            SimpleDdgiSavedRaysPerFrame = 256,
            SimpleDdgiLightingDirtyFrames = 12,
            SimpleDdgiLightingDirtyBoostedCapacity = 128,
            SimpleDdgiDirtyReasonFlags = VulkanRenderer.SimpleDdgiDirtyReasonLight | VulkanRenderer.SimpleDdgiDirtyReasonDynamicGeometry,
            SimpleDdgiFullRayProbeUpdateCount = 5,
            SimpleDdgiMaintenanceRayProbeUpdateCount = 9,
            SimpleDdgiAdaptiveRaySavedRaysPerFrame = 864,
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
            DdgiGatherSelectedLocalTileFraction = 0.25f,
            DdgiGatherSelectedClipmapTileFraction = 0.75f,
            DdgiGatherFallbackTileFraction = 0.1f,
            DdgiAverageSpatialCoverageEstimate = 0.9f,
            DdgiAverageSupportCoverageEstimate = 0.67f,
            DdgiAverageEffectiveContributionEstimate = 0.603f,
            DdgiAverageRelocationFractionEstimate = 0.125f,
            DdgiClassifiedInactiveProbeCountEstimate = 3,
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
            DdgiProbeVolumeBufferBytes = 512,
            DdgiProbeStateBufferBytes = 2048,
            DdgiProbeUpdateQueueBytes = 4096,
            DdgiProbeRelocationClassificationBytes = 8192,
            DdgiGatherTileBufferBytes = 16384,
            DdgiLocalSlotReservedPoolBytes = 32768,
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
            DdgiGpuSchedulerCandidateBufferOverflowCount = 1,
            DdgiGpuSchedulerPerBucketOverflowCount = 2,
            DdgiGpuSchedulerDuplicateRequestCount = 4,
            DdgiGpuSchedulerBudgetRejectedCount = 5,
            DdgiGpuSchedulerRequestBudgetRejectedCount = 2,
            DdgiGpuSchedulerPrimaryRayBudgetRejectedCount = 3,
            DdgiGpuSchedulerInvalidProbeCount = 6,
            DdgiGpuSchedulerCandidateOutputCapacity = 64,
            DdgiGpuSchedulerFullScan = 1,
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
            DdgiUpdateExecuted = 1,
            DdgiUpdateSkipReason = string.Empty,
            DdgiTraceDispatchGroupCount = 19,
            DdgiTraceProbeCount = 19,
            DdgiTraceRayCount = 608,
            DdgiBlendProbeCount = 19,
            DdgiRelocateClassifyProbeCount = 19,
            DdgiPublishProbeCount = 19,
            DdgiRayScratchBytes = 20_480,
            DdgiUpdatedAtlasBytes = 12_288,
            DdgiPublishExecuted = 0,
            DdgiPublishSkipReason = "no-ddgi-updates",
            DdgiPublishedCacheLatencyFrames = 1,
            DdgiCacheGeneration = 3,
            DdgiLastUpdatedFrameSerial = 42,
            DdgiCacheWarmupState = DdgiRuntimeWarmupState.NearCascadeWarmup,
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
            Assert.That(json, Does.Contain("\"SchemaVersion\": 3"));
            Assert.That(json, Does.Contain("\"Capture\""));
            Assert.That(json, Does.Contain("\"GpuDeviceName\": \"Reference GPU\""));
            Assert.That(json, Does.Contain("\"DriverVersion\": \"999.1\""));
            Assert.That(json, Does.Contain("\"RenderWidth\": 1920"));
            Assert.That(json, Does.Contain("\"RenderHeight\": 1080"));
            Assert.That(json, Does.Contain("\"SceneContentRevision\": 42"));
            Assert.That(json, Does.Contain("\"structured-gather\""));
            Assert.That(json, Does.Contain("\"sampled-atlas\""));
            Assert.That(json, Does.Contain("\"paged-far-field\""));
            Assert.That(json, Does.Contain("forward-gather-inclusive=Inclusive"));
            Assert.That(json, Does.Contain("\"ActiveQualityPreset\": \"DdgiHigh\""));
            Assert.That(json, Does.Contain("\"Mode\": \"Ddgi\""));
            Assert.That(json, Does.Contain("\"RayQueryActive\": true"));
            Assert.That(json, Does.Contain("\"SsgiActive\": false"));
            Assert.That(json, Does.Contain("\"DdgiActive\": true"));
            Assert.That(json, Does.Contain("\"Graph\""));
            Assert.That(json, Does.Contain("Forward GI incremental timing is unavailable"));
            Assert.That(json, Does.Contain("\"HiZConsumerCount\": 1"));
            Assert.That(json, Does.Contain("\"HiZConsumerSummary\": \"SceneSubmissionPreviousHiZ\""));
            Assert.That(json, Does.Contain("\"HiZBuildSkippedBecauseNoConsumer\": 0"));
            Assert.That(json, Does.Contain("\"HiZCounterSource\": \"SceneSubmissionCompaction\""));
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
            Assert.That(json, Does.Contain("\"HiZPolicyCounterSource\": \"SceneSubmissionCompaction\""));
            Assert.That(json, Does.Contain("\"ResourceCount\": 2"));
            Assert.That(json, Does.Contain("\"LdrSceneColor\""));
            Assert.That(json, Does.Contain("\"VisibleMeshletDrawCount\": 96"));
            Assert.That(json, Does.Contain("\"DdgiSampleCount\": 120"));
            Assert.That(json, Does.Contain("\"DdgiTransportExcludedClusterCount\": 32"));
            Assert.That(json, Does.Contain("requires explicit DDGI proxy cards or clusters"));
            Assert.That(json, Does.Contain("\"BufferBytes\": 1024"));
            Assert.That(json, Does.Contain("\"SsgiWidth\": 960"));
            Assert.That(json, Does.Contain("\"SsgiRayCount\": 6"));
            Assert.That(json, Does.Contain("\"DdgiProbeUpdatePrimaryRayBudget\": 32768"));
            Assert.That(json, Does.Contain("\"DdgiSchedulerMode\": \"CpuReference\""));
            Assert.That(json, Does.Contain("\"DdgiQualityTier\": \"DdgiHigh\""));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetScale\": 0.75"));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetReduced\": 1"));
            Assert.That(json, Does.Contain("\"DdgiEmergencyDegradeActive\": 1"));
            Assert.That(json, Does.Contain("\"DdgiEffectiveMaxShadedLights\": 4"));
            Assert.That(json, Does.Contain("\"DdgiAdaptiveBudgetReason\": \"emergency-degrade\""));
            Assert.That(json, Does.Contain("\"DdgiScheduledPrimaryRayCount\": 768"));
            Assert.That(json, Does.Contain("\"SimpleDdgiInactiveProbeCount\": 3"));
            Assert.That(json, Does.Contain("\"SimpleDdgiInactiveProbeSkipCount\": 2"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSavedRaysPerFrame\": 256"));
            Assert.That(json, Does.Contain("\"SimpleDdgiLightingDirtyFrames\": 12"));
            Assert.That(json, Does.Contain("\"SimpleDdgiLightingDirtyBoostedCapacity\": 128"));
            Assert.That(json, Does.Contain("\"SimpleDdgiDirtyReasonFlags\": 5"));
            Assert.That(json, Does.Contain("\"SimpleDdgiFullRayProbeUpdateCount\": 5"));
            Assert.That(json, Does.Contain("\"SimpleDdgiMaintenanceRayProbeUpdateCount\": 9"));
            Assert.That(json, Does.Contain("\"SimpleDdgiAdaptiveRaySavedRaysPerFrame\": 864"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportV2Active\": true"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportPublishedProbeCount\": 37"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportPublishRegionCount\": 5"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportPublishedProbeTotal\": 9437"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportPublishRegionTotal\": 1205"));
            Assert.That(json, Does.Contain("\"SimpleDdgiUpdateTransactionAbortCount\": 3"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportSourceCacheInvalidationCount\": 1024"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportSolverInvalidationCount\": 2"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportSolverInvalidationsPerSourceRefresh\": 0.25"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSourceLightingGeneration\": 12"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportGeneration\": 37"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportGlobalConvergencePending\": true"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportGlobalConvergenceElapsedFrames\": 23"));
            Assert.That(json, Does.Contain("\"SimpleDdgiTransportCalibrationChangeCount\": 4"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSampledAtlasRequested\": true"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSampledAtlasActive\": true"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSampledAtlasGroupCount\": 2"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSampledAtlasLayersPerTexture\": 2048"));
            Assert.That(json, Does.Contain("\"SimpleDdgiSampledAtlasImageBytes\": 3932160"));
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
            Assert.That(json, Does.Contain("\"DdgiGatherSelectedLocalTileFraction\": 0.25"));
            Assert.That(json, Does.Contain("\"DdgiGatherSelectedClipmapTileFraction\": 0.75"));
            Assert.That(json, Does.Contain("\"DdgiGatherFallbackTileFraction\": 0.1"));
            Assert.That(json, Does.Contain("\"DdgiAverageSpatialCoverageEstimate\": 0.9"));
            Assert.That(json, Does.Contain("\"DdgiAverageSupportCoverageEstimate\": 0.67"));
            Assert.That(json, Does.Contain("\"DdgiAverageEffectiveContributionEstimate\": 0.603"));
            Assert.That(json, Does.Contain("\"DdgiAverageRelocationFractionEstimate\": 0.125"));
            Assert.That(json, Does.Contain("\"DdgiClassifiedInactiveProbeCountEstimate\": 3"));
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
            Assert.That(json, Does.Contain("\"DdgiProbeVolumeBufferBytes\": 512"));
            Assert.That(json, Does.Contain("\"DdgiProbeStateBufferBytes\": 2048"));
            Assert.That(json, Does.Contain("\"DdgiProbeUpdateQueueBytes\": 4096"));
            Assert.That(json, Does.Contain("\"DdgiProbeRelocationClassificationBytes\": 8192"));
            Assert.That(json, Does.Contain("\"DdgiGatherTileBufferBytes\": 16384"));
            Assert.That(json, Does.Contain("\"DdgiLocalSlotReservedPoolBytes\": 32768"));
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
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerCandidateBufferOverflowCount\": 1"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPerBucketOverflowCount\": 2"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerDuplicateRequestCount\": 4"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerBudgetRejectedCount\": 5"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerRequestBudgetRejectedCount\": 2"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerPrimaryRayBudgetRejectedCount\": 3"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerInvalidProbeCount\": 6"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerCandidateOutputCapacity\": 64"));
            Assert.That(json, Does.Contain("\"DdgiGpuSchedulerFullScan\": 1"));
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
            Assert.That(json, Does.Contain("\"DdgiUpdateExecuted\": 1"));
            Assert.That(json, Does.Contain("\"DdgiUpdateSkipReason\": \"\""));
            Assert.That(json, Does.Contain("\"DdgiTraceDispatchGroupCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiTraceProbeCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiTraceRayCount\": 608"));
            Assert.That(json, Does.Contain("\"DdgiBlendProbeCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiRelocateClassifyProbeCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiPublishProbeCount\": 19"));
            Assert.That(json, Does.Contain("\"DdgiRayScratchBytes\": 20480"));
            Assert.That(json, Does.Contain("\"DdgiUpdatedAtlasBytes\": 12288"));
            Assert.That(json, Does.Contain("\"DdgiPublishExecuted\": 0"));
            Assert.That(json, Does.Contain("\"DdgiPublishSkipReason\": \"no-ddgi-updates\""));
            Assert.That(json, Does.Contain("\"DdgiPublishedCacheLatencyFrames\": 1"));
            Assert.That(json, Does.Contain("\"DdgiCacheGeneration\": 3"));
            Assert.That(json, Does.Contain("\"DdgiLastUpdatedFrameSerial\": 42"));
            Assert.That(json, Does.Contain("\"DdgiCacheWarmupState\": \"NearCascadeWarmup\""));
            Assert.That(json, Does.Contain("\"SceneSurfaceRenderTargetBytes\": 4096"));
            Assert.That(json, Does.Contain("\"AccelerationStructureTlasBuildCount\": 1"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleMicroseconds\": 4"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleP95Microseconds\": 240"));
            Assert.That(json, Does.Contain("\"GpuDdgiScheduleOverBudget\": 0"));
            Assert.That(json, Does.Contain("\"CpuRecordMicroseconds\": 164"));
            Assert.That(json, Does.Contain("\"CpuRecordP95Microseconds\": 200"));
            Assert.That(json, Does.Contain("\"CpuTimingSampleCount\": 3"));
            Assert.That(json, Does.Contain("\"GpuMicroseconds\": 615"));
            Assert.That(json, Does.Contain("\"ForwardGiInclusiveMicroseconds\": 40"));
            Assert.That(json, Does.Contain("\"ForwardGiInclusiveAttribution\": \"Inclusive\""));
            Assert.That(json, Does.Contain("\"GiTiming\""));
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

    [Test]
    public void PerformanceSnapshotWriter_WarnsWhenGiAccountingOrResidencyIsNotReleaseReady()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-gi-warning-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            SimpleDdgiSampledAtlasRequested = 1,
            SimpleDdgiSampledAtlasActive = 0,
            SimpleDdgiSampledAtlasFallbackReason = "sampled-atlas-memory-budget-exhausted",
            GpuTimingValid = 1,
            GpuForwardGiGatherTimingCoverage = 0,
            CpuGlobalIlluminationRecordP95Microseconds = 251,
            GlobalIlluminationCpuTimingSampleCount = 1,
            DdgiDetailedCountersEnabled = 1,
            DdgiInvestigationCountersReadbackValid = 0,
            SimpleDdgiDirtyFirstUpdateLatencySampleCount = 1,
            SimpleDdgiDirtyFirstUpdateLatencyP95Frames = 2,
            SimpleDdgiDirtyConvergenceLatencySampleCount = 1,
            SimpleDdgiDirtyConvergenceLatencyP95Frames = 9,
            StreamedGiAccelerationStructuresFeatureEnabled = 1,
            AccelerationStructureResidentBytes = 512,
            AccelerationStructureMemoryBudgetBytes = 256,
            AccelerationStructureBlasBudgetRejectedCount = 1,
            FarFieldPagedMode = 1,
            FarFieldCacheBytes = 512,
            FarFieldMemoryBudgetBytes = 256,
            DdgiBlackFrameSuspect = 1
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
            Assert.That(json, Does.Contain("Forward GI incremental timing is unavailable"));
            Assert.That(json, Does.Contain("GI CPU scheduling and upload P95 exceeds"));
            Assert.That(json, Does.Contain("Sampled Simple DDGI atlas fell back to the canonical SSBO path: sampled-atlas-memory-budget-exhausted"));
            Assert.That(json, Does.Contain("Detailed GI counter readback is unavailable"));
            Assert.That(json, Does.Contain("dirty-to-first-update P95 exceeds"));
            Assert.That(json, Does.Contain("dirty-to-convergence P95 exceeds"));
            Assert.That(json, Does.Contain("Resident GI acceleration structures exceed"));
            Assert.That(json, Does.Contain("rejected the complete resident set"));
            Assert.That(json, Does.Contain("Far-field page cache exceeds"));
            Assert.That(json, Does.Contain("DDGI black-frame suspect was reported"));
        });
    }

    [Test]
    public void PerformanceSnapshotWriter_WritesV3NamedEnumsAndCaptureContracts()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-v3-contract-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            GpuTimingValid = 1,
            GpuForwardGiGatherMicroseconds = 99,
            GpuForwardGiGatherTimingCoverage = 1,
            GpuForwardGiGatherTimingAttribution = GiTimingAttribution.Inclusive,
            CaptureRun = new PerformanceCaptureRunMetadata("Sponza", "Low", "Release", "1.2.3", "abc", "shader", 7),
            CaptureCamera = new PerformanceCaptureCameraMetadata(1, 2, 3, 0.1f, 0.2f, 1.0f, 0.1f, 1000, "view", "projection", 4),
            CaptureFrame = new PerformanceCaptureFrameMetadata(12, 8, DdgiRuntimeWarmupState.SteadyState, 2, 3),
            GiMeasurement = new GiMeasurementMetadata(GiMeasurementMode.NormalTelemetry, 1, "measured", false, false)
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        string path = new PerformanceSnapshotWriter().Write(directory, diagnostics, budget);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("Profile").GetProperty("Kind").ValueKind, Is.EqualTo(JsonValueKind.String));
            Assert.That(root.GetProperty("Diagnostics").GetProperty("GlobalIlluminationMode").ValueKind, Is.EqualTo(JsonValueKind.String));
            Assert.That(root.GetProperty("Budget").GetProperty("Metrics")[0].GetProperty("Status").ValueKind, Is.EqualTo(JsonValueKind.String));
            Assert.That(root.GetProperty("Capture").TryGetProperty("Run", out _), Is.True);
            Assert.That(root.GetProperty("Capture").TryGetProperty("Camera", out _), Is.True);
            Assert.That(root.GetProperty("Capture").TryGetProperty("Frame", out _), Is.True);
            Assert.That(root.GetProperty("Capture").TryGetProperty("ResolvedGiSettings", out _), Is.True);
            Assert.That(root.TryGetProperty("GiTiming", out _), Is.True);
            Assert.That(root.TryGetProperty("GiResidency", out _), Is.True);
        });
    }

    [Test]
    public void CaptureRunMetadata_ReplacesLegacyUnknownPlaceholdersWithExplicitUnavailableReasons()
    {
        PerformanceCaptureRunMetadata normalized = PerformanceSnapshotWriter.NormalizeCaptureRunMetadata(
            PerformanceCaptureRunMetadata.Unknown);

        Assert.Multiple(() =>
        {
            Assert.That(normalized.SceneKind, Is.EqualTo("unavailable:scene-kind-not-reported"));
            Assert.That(normalized.Scenario, Is.EqualTo("unavailable:scenario-not-reported"));
            Assert.That(normalized.BuildConfiguration, Is.EqualTo("unavailable:build-configuration-not-reported"));
            Assert.That(normalized.ApplicationVersion, Is.EqualTo("unavailable:application-version-not-reported"));
            Assert.That(normalized.Commit, Is.EqualTo("unavailable:source-revision-not-reported"));
            Assert.That(normalized.ShaderBundleHash, Is.EqualTo("unavailable:shader-bundle-hash-not-reported"));
            Assert.That(normalized.SettingsSchemaVersion, Is.EqualTo(0));
        });
    }

    [Test]
    public void PerformanceSnapshotWriter_ExportsExplicitUnavailableRunMetadataForLegacyDefaults()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "performance-snapshot-explicit-unavailable-capture-metadata-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            CaptureRun = PerformanceCaptureRunMetadata.Unknown
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        string path = new PerformanceSnapshotWriter().Write(directory, diagnostics, budget);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement run = document.RootElement.GetProperty("Capture").GetProperty("Run");

        Assert.Multiple(() =>
        {
            Assert.That(run.GetProperty("Scenario").GetString(), Is.EqualTo("unavailable:scenario-not-reported"));
            Assert.That(run.GetProperty("BuildConfiguration").GetString(), Is.EqualTo("unavailable:build-configuration-not-reported"));
            Assert.That(run.GetProperty("Commit").GetString(), Is.EqualTo("unavailable:source-revision-not-reported"));
            Assert.That(run.GetProperty("ShaderBundleHash").GetString(), Is.EqualTo("unavailable:shader-bundle-hash-not-reported"));
            Assert.That(File.ReadAllText(path), Does.Not.Contain("unknown-scenario"));
        });
    }

    [Test]
    public void VulkanRenderer_CaptureIdentityUsesAssemblyRevisionAndEffectiveShaderBundle()
    {
        string build = VulkanRenderer.CreatePerformanceCaptureBuildConfiguration("Standard");
        string applicationVersion = VulkanRenderer.ResolvePerformanceCaptureApplicationVersion();
        string commit = VulkanRenderer.ResolvePerformanceCaptureCommit();
        string shaderBundleHash = VulkanRenderer.ResolvePerformanceCaptureShaderBundleHash();

        Assert.Multiple(() =>
        {
            Assert.That(build, Does.Contain("validation=Standard"));
            Assert.That(build, Does.Contain("framework="));
            Assert.That(build, Does.Not.StartWith("unknown"));
            Assert.That(applicationVersion, Does.Not.StartWith("unknown"));
            Assert.That(commit, Does.Not.StartWith("unknown"));
            Assert.That(shaderBundleHash, Does.StartWith("sha256:"));
            Assert.That(shaderBundleHash.Length, Is.EqualTo("sha256:".Length + 64));
            Assert.That(shaderBundleHash, Is.EqualTo(VulkanRenderer.ResolvePerformanceCaptureShaderBundleHash()));
        });
    }

    [Test]
    public void VulkanRenderer_CaptureScenarioIsExplicitlyUnavailableUntilSuppliedByTheApplication()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                VulkanRenderer.ResolveCaptureScenario(null),
                Is.EqualTo("unavailable:active-scenario-not-supplied-by-renderer-client"));
            Assert.That(
                VulkanRenderer.ResolveCaptureScenario("  GiSponzaRightWallStationary  "),
                Is.EqualTo("GiSponzaRightWallStationary"));
        });
    }

    [TestCase("c0c70891390e9af5f9b7595dbb8bee243d3f3cdd", null, "c0c70891390e9af5f9b7595dbb8bee243d3f3cdd")]
    [TestCase(null, "1.2.3+c0c70891390e9af5f9b7595dbb8bee243d3f3cdd", "c0c70891390e9af5f9b7595dbb8bee243d3f3cdd")]
    [TestCase(null, "1.2.3+not-a-source-revision", "unavailable:source-revision-not-embedded")]
    public void VulkanRenderer_CommitMetadataAcceptsOnlyExplicitSourceRevisions(
        string? sourceRevision,
        string? informationalVersion,
        string expected)
    {
        Assert.That(
            VulkanRenderer.ResolvePerformanceCaptureCommit(sourceRevision, informationalVersion),
            Is.EqualTo(expected));
    }

    [Test]
    public void PerformanceSnapshotReader_MigratesV2NumericEnumBaseline()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-v2-reader-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
            GlobalIlluminationDdgiActive = 1,
            SimpleDdgiActive = 1,
            GpuTimingValid = 1,
            GpuForwardGiGatherMicroseconds = 37,
            GpuForwardGiGatherTimingCoverage = 1
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));
        string currentJson = File.ReadAllText(new PerformanceSnapshotWriter().Write(directory, diagnostics, budget));

        var legacy = System.Text.Json.Nodes.JsonNode.Parse(currentJson)!.AsObject();
        legacy["SchemaVersion"] = 2;
        legacy.Remove("OriginalSchemaVersion");
        legacy.Remove("StructuredWarnings");
        legacy.Remove("GiTiming");
        legacy.Remove("GiResidency");
        legacy["Profile"]!["Kind"] = 0;
        legacy["Diagnostics"]!["GlobalIlluminationMode"] = 2;
        legacy["GlobalIllumination"]!["ActiveQualityPreset"] = 4;
        legacy["GlobalIllumination"]!["Mode"] = 2;
        JsonObjectCapture(legacy).Remove("Run");
        JsonObjectCapture(legacy).Remove("Camera");
        JsonObjectCapture(legacy).Remove("Frame");
        JsonObjectCapture(legacy).Remove("ResolvedGiSettings");
        JsonObjectCapture(legacy).Remove("Measurement");
        JsonObjectCapture(legacy).Remove("FeatureStates");

        PerformanceSnapshot migrated = new PerformanceSnapshotReader().ReadJson(legacy.ToJsonString());

        Assert.Multiple(() =>
        {
            Assert.That(migrated.SchemaVersion, Is.EqualTo(3));
            Assert.That(migrated.OriginalSchemaVersion, Is.EqualTo(2));
            Assert.That(migrated.Profile.Kind, Is.EqualTo(RenderBudgetProfileKind.Development));
            Assert.That(migrated.Diagnostics.GlobalIlluminationMode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(migrated.GlobalIllumination.ActiveQualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(migrated.GiTiming.ForwardGiGatherInclusiveAttribution, Is.EqualTo(GiTimingAttribution.Inclusive));
            Assert.That(migrated.Capture.ResolvedGiSettings.StableHash, Is.Not.EqualTo("unknown"));
        });
    }

    [Test]
    public void PerformanceSnapshotReader_RejectsAmbiguousAndOversizedJson()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "performance-snapshot-reader-bounds-tests",
            Guid.NewGuid().ToString("N"));
        string duplicatePath = Path.Combine(directory, "duplicate.json");
        string oversizedPath = Path.Combine(directory, "oversized.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                duplicatePath,
                """
                {
                  "SchemaVersion": 3,
                  "SchemaVersion": 3
                }
                """);
            var reader = new PerformanceSnapshotReader();
            Assert.That(
                () => reader.Read(duplicatePath),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("duplicate JSON property"));

            File.WriteAllBytes(
                oversizedPath,
                new byte[DurableJsonFileWriter.MaximumPayloadBytes + 1]);
            Assert.That(
                () => reader.Read(oversizedPath),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("invalid bounded length"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static System.Text.Json.Nodes.JsonObject JsonObjectCapture(System.Text.Json.Nodes.JsonObject root) =>
        root["Capture"]!.AsObject();
}
