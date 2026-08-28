using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Core;

/// <summary>
/// Maps coherent DDGI frame facts into the public per-frame data schema. The
/// projector is stateless and consumes its synchronous source view without
/// retaining the manager, settings, command buffer, or scene data.
/// </summary>
internal static class DdgiFrameDataProjector
{
    public static void Project(
        SceneRenderingData sceneData,
        in DdgiFrameProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        ProjectCoreFrame(sceneData, input.Core);
        if (input.Core.Active && input.VolumeManager is { } manager)
            ProjectSimpleDdgiFrame(sceneData, manager, input);
    }

    public static void ProjectAdvancedFrame(
        SceneRenderingData sceneData,
        in AdvancedGiFrameProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        sceneData.GiRoadmapExperiments = input.RoadmapExperiments;
        sceneData.SimpleDdgiContentMemory = input.ContentMemory;
    }

    private static void ProjectCoreFrame(
        SceneRenderingData sceneData,
        in SimpleDdgiCoreFrameResult result)
    {
        if (result.Active)
        {
            DdgiInvalidationTelemetry invalidation =
                result.InvalidationTelemetry;
            sceneData.VfxDdgiDirtyProbeEventCount =
                invalidation.VfxDirtyProbeEventCount;
            sceneData.SimpleDdgiMutationJournalLastConsumedSerial =
                invalidation.LastConsumedSerial;
            sceneData.SimpleDdgiMutationJournalEnqueuedEventCount =
                invalidation.EnqueuedEventCount;
            sceneData.SimpleDdgiMutationJournalCoalescedEventCount =
                invalidation.CoalescedEventCount;
            sceneData.SimpleDdgiMutationJournalOverflowCount =
                invalidation.OverflowCount;
            sceneData.SimpleDdgiMutationJournalConservativeFallbackCount =
                invalidation.ConservativeFallbackCount;
            sceneData.SimpleDdgiMutationJournalAttachScanCount =
                invalidation.SceneAttachScanCount;
            sceneData.SimpleDdgiMutationJournalAttachObjectCount =
                invalidation.SceneAttachObjectCount;
            sceneData.SimpleDdgiMutationJournalOracleComparisonCount =
                invalidation.OracleComparisonCount;
            sceneData.SimpleDdgiMutationJournalOracleMismatchCount =
                invalidation.OracleMismatchCount;
            sceneData.SimpleDdgiMutationJournalPendingEventCount =
                invalidation.PendingEventCount;
            sceneData.SimpleDdgiMutationJournalOutputRegionCount =
                invalidation.OutputRegionCount;
            sceneData.SimpleDdgiMutationJournalOverflowedThisFrame =
                invalidation.OverflowedThisFrame;
        }

        ProjectEmissiveFrame(sceneData, result.Emissive);
        if (!result.Active)
            return;

        SimpleDdgiFarFieldFrameSnapshot farField = result.FarField;
        sceneData.FarFieldPagedMode = farField.PagedMode ? 1 : 0;
        sceneData.FarFieldPagePoolCapacity = farField.PagePoolCapacity;
        sceneData.FarFieldResidentPageCount = farField.ResidentPageCount;
        sceneData.FarFieldPendingPageCount = farField.PendingPageCount;
        sceneData.FarFieldPageRequestCount = farField.PageRequestCount;
        sceneData.FarFieldPageMissCount = farField.PageMissCount;
        sceneData.FarFieldPageRebuildCount = farField.PageRebuildCount;
        sceneData.FarFieldPageEvictionCount = farField.PageEvictionsThisFrame;
        sceneData.FarFieldScheduledPageBakeCount =
            farField.ScheduledPageBakeCount;
        sceneData.FarFieldCacheBytes = farField.PageCacheBytes;
        sceneData.FarFieldMemoryBudgetBytes = farField.MemoryBudgetBytes;
        sceneData.FarFieldInstanceBufferBytes = farField.InstanceBufferBytes;
        sceneData.FarFieldPageTableBytes = farField.PageTableBufferBytes;
        sceneData.SimpleDdgiPageFullManagementRequired =
            result.FullPageManagementRequired ? 1 : 0;
        sceneData.CpuDdgiRecordMicroseconds = 0;
        sceneData.CpuSimpleDdgiRecordMicroseconds =
            result.SimpleDdgiUploadMicroseconds;
        sceneData.SimpleDdgiUploadTiming = result.SimpleDdgiUploadTiming;
        sceneData.CpuFarFieldRecordMicroseconds = farField.UploadMicroseconds;
    }

    private static void ProjectEmissiveFrame(
        SceneRenderingData sceneData,
        in DdgiEmissiveTransportSnapshot snapshot)
    {
        DdgiEmissiveContentSnapshot content = snapshot.Content;
        DdgiEmissiveDiagnosticSnapshot diagnostics = snapshot.Diagnostics;
        DdgiVfxMacroReductionResult vfx = diagnostics.Vfx;
        bool active = content.Active;
        bool triangleSampling = content.TriangleSampling;

        sceneData.DdgiEmissiveSourceCount = content.SourceCount;
        sceneData.DdgiEmissiveSourceRevision = content.SourceRevision;
        sceneData.DdgiEmissiveSamplingMode = !active
            ? "Inactive"
            : triangleSampling
                ? vfx.SourceCount > 0
                    ? "TriangleVfxSpatialAliasMixture"
                    : "TriangleSpatialAliasMixture"
                : "ProxyRollback";
        sceneData.DdgiEmissiveTriangleCandidateCount = triangleSampling
            ? diagnostics.TriangleStats.CandidateCount
            : 0;
        sceneData.DdgiEmissiveTriangleBudget = triangleSampling
            ? content.TriangleBudget
            : 0;
        sceneData.DdgiEmissiveSkippedEnergyFraction = triangleSampling
            ? diagnostics.TriangleStats.SkippedEnergyFraction
            : 0.0f;
        sceneData.DdgiEmissiveSkippedSkinnedObjectCount = triangleSampling
            ? diagnostics.SkippedSkinnedObjectCount
            : 0;
        sceneData.DdgiEmissiveSkippedSkinnedImportance = triangleSampling
            ? diagnostics.SkippedSkinnedImportance
            : 0.0;
        DdgiEmissiveEnergyDiagnostics energy = diagnostics.Energy;
        sceneData.DdgiEmissiveAverageRadiance =
            energy.AreaWeightedAverageRadiance;
        sceneData.DdgiEmissivePeakLuminanceNits =
            energy.PeakSelectedLuminanceNits;
        sceneData.DdgiEmissiveCoveredAreaSquareMeters =
            energy.SelectedCoveredAreaSquareMeters;
        sceneData.DdgiEmissiveIntegratedPowerRed = energy.IntegratedPowerRed;
        sceneData.DdgiEmissiveIntegratedPowerGreen =
            energy.IntegratedPowerGreen;
        sceneData.DdgiEmissiveIntegratedPowerBlue = energy.IntegratedPowerBlue;
        sceneData.DdgiEmissiveIntegratedPowerLuminance =
            energy.IntegratedPowerLuminance;
        sceneData.DdgiEmissiveSelectedProbability = energy.SelectedProbability;
        sceneData.DdgiEmissiveEnergyWarningCount =
            diagnostics.EnergyWarningCount;
        sceneData.DdgiEmissiveLastEnergyWarning = diagnostics.LastEnergyWarning;
        DdgiEmissiveTableCacheDiagnostics cache = diagnostics.Cache;
        sceneData.DdgiEmissiveTableCacheHit =
            active && cache.LastLookupWasHit ? 1 : 0;
        sceneData.DdgiEmissiveTableCacheHitCount = cache.HitCount;
        sceneData.DdgiEmissiveTableCacheMissCount = cache.MissCount;
        sceneData.DdgiEmissiveTableRebuildCount = cache.RebuildCount;
        sceneData.DdgiEmissiveTableInvalidationCount = cache.InvalidationCount;
        sceneData.DdgiEmissiveTableUploadCount = content.UploadCount;
        DdgiEmissiveHierarchyDiagnostics hierarchy = diagnostics.Hierarchy;
        sceneData.DdgiEmissiveHierarchyNodeCount = active
            ? hierarchy.NodeCount
            : 0;
        sceneData.DdgiEmissiveHierarchyBuildCount = hierarchy.BuildCount;
        sceneData.DdgiEmissiveHierarchyRefitCount = hierarchy.RefitCount;
        sceneData.DdgiEmissiveHierarchyUpdatedNodeCount = active
            ? hierarchy.LastUpdatedNodeCount
            : 0;
        sceneData.DdgiVfxMacroSourceCount = active ? vfx.SourceCount : 0;
        sceneData.DdgiVfxMacroEligibleEmitterCount = active
            ? vfx.EligibleEmitterCount
            : 0;
        sceneData.DdgiVfxMacroRejectedTransientCount = active
            ? vfx.RejectedTransientCount
            : 0;
        sceneData.DdgiVfxMacroOverflowCount = active ? vfx.OverflowCount : 0;
        sceneData.DdgiVfxMacroAuthoredPowerCount = active
            ? vfx.AuthoredPowerCount
            : 0;
        sceneData.DdgiVfxMacroAutoPowerCount = active
            ? vfx.AutoPowerCount
            : 0;
        sceneData.DdgiVfxMacroRevision = vfx.Revision;
        sceneData.DdgiVfxMacroRefitCount = vfx.RefitCount;
    }

    internal static int ResolveConfiguredSimpleDdgiPrimaryRayBudget(
        int configuredBudget) => Math.Max(0, configuredBudget);

    internal static SimpleDdgiFrameWork ResolveSimpleDdgiFrameWork(
        bool rayUpdateActive,
        SimpleDdgiSchedulerMode schedulerMode,
        bool gpuFeedbackValid,
        in GPUSimpleDdgiSchedulerFeedback gpuFeedback,
        int cpuScheduledProbeCount,
        int cpuSourceRefreshProbeCount,
        ulong cpuPrimaryRayCount,
        ulong cpuSourceRayCount,
        ulong cpuTransportRayCount,
        int cpuPublishedProbeCount)
    {
        if (!rayUpdateActive)
            return default;

        if (schedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
            gpuFeedbackValid)
        {
            return new SimpleDdgiFrameWork(
                checked((int)Math.Min(
                    gpuFeedback.AcceptedCount,
                    (uint)int.MaxValue)),
                checked((int)Math.Min(
                    gpuFeedback.SourceProbeUsed,
                    (uint)int.MaxValue)),
                gpuFeedback.PrimaryRayUsed,
                gpuFeedback.SourceAchievedRays,
                gpuFeedback.TransportRayUsed,
                checked((int)Math.Min(
                    gpuFeedback.PublishedCount,
                    (uint)int.MaxValue)));
        }

        return new SimpleDdgiFrameWork(
            Math.Max(0, cpuScheduledProbeCount),
            Math.Max(0, cpuSourceRefreshProbeCount),
            cpuPrimaryRayCount,
            cpuSourceRayCount,
            cpuTransportRayCount,
            Math.Max(0, cpuPublishedProbeCount));
    }

    internal static ulong EstimateSimpleDdgiShadowRayUpperBound(
        ulong primaryRayCount,
        int directionalLightCount,
        int localLightCount,
        int maxShadedLights)
    {
        int capacity = Math.Min(Math.Max(maxShadedLights, 0), 8);
        int lightCount = Math.Min(
            capacity,
            Math.Max(directionalLightCount, 0) +
            Math.Max(localLightCount, 0));
        return primaryRayCount * (ulong)lightCount;
    }

    internal static DdgiRuntimeWarmupState ResolveSimpleDdgiWarmupState(
        int activeProbeCount,
        bool transportConvergencePending,
        bool tailCertificationEnabled,
        bool transportCertificateCurrent,
        SimpleDdgiTransportPhase transportPhase,
        in SimpleDdgiRefinementBrickDiagnostics refinement)
    {
        if (activeProbeCount <= 0)
            return DdgiRuntimeWarmupState.ColdStart;

        if (transportPhase is
            SimpleDdgiTransportPhase.SourceRepair or
            SimpleDdgiTransportPhase.ParticipantReconciliation or
            SimpleDdgiTransportPhase.FailClosedRecovery)
        {
            return DdgiRuntimeWarmupState.Recovery;
        }

        bool refinementPending =
            refinement.ReceiverReadyBrickCount !=
            refinement.AdmittedBrickCount ||
            refinement.BaseFallbackBrickCount > 0;
        if (refinementPending)
            return DdgiRuntimeWarmupState.LocalVolumeWarmup;

        return transportConvergencePending ||
               !tailCertificationEnabled ||
               !transportCertificateCurrent
            ? DdgiRuntimeWarmupState.NearCascadeWarmup
            : DdgiRuntimeWarmupState.SteadyState;
    }

    private static void PopulateSimpleDdgiLightSelectionDiagnostics(
        SceneRenderingData sceneData,
        ulong primaryRayCount,
        int directionalLightCount,
        int localLightCount,
        int maxShadedLights)
    {
        int capacity = Math.Min(Math.Max(maxShadedLights, 0), 8);
        int selectedDirectionalLights = Math.Min(
            Math.Max(directionalLightCount, 0),
            capacity);
        int selectedLocalLights = Math.Min(
            Math.Max(localLightCount, 0),
            capacity - selectedDirectionalLights);
        ulong selectedDirectionalHits =
            primaryRayCount * (ulong)selectedDirectionalLights;
        ulong selectedLocalHits =
            primaryRayCount * (ulong)selectedLocalLights;

        sceneData.DdgiSelectedDirectionalHitCount = selectedDirectionalHits;
        sceneData.DdgiSelectedLocalHitCount = selectedLocalHits;
        sceneData.DdgiVisibilityRayCount =
            selectedDirectionalHits + selectedLocalHits;
        sceneData.DdgiSkippedLocalLightCount = primaryRayCount *
                                               (ulong)Math.Max(0, localLightCount - selectedLocalLights);
        sceneData.DdgiLightSelectionMode = primaryRayCount > 0 && capacity > 0
            ? "simple-per-hit-top-n"
            : "disabled";
    }

    private static void ProjectSimpleDdgiFrame(
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager manager,
        in DdgiFrameProjectionInput input)
    {
        GPUSimpleDdgiSchedulerFeedback schedulerFeedback =
            manager.LastGpuSchedulerFeedback;
        bool schedulerFeedbackValid = manager.GpuSchedulerFeedbackValid;
        SimpleDdgiFrameWork frameWork = ResolveSimpleDdgiFrameWork(
            input.Core.RayUpdateActive,
            manager.SchedulerMode,
            schedulerFeedbackValid,
            schedulerFeedback,
            manager.ProbesToUpdate,
            manager.SourceRefreshProbeCount,
            manager.ScheduledPrimaryRayCount,
            manager.ScheduledSourceRayCount,
            manager.ScheduledTransportRayCount,
            manager.TransportPublishedProbeCount);
        int probesToUpdate = frameWork.ScheduledProbeCount;
        ulong primaryRayCount = frameWork.PrimaryRayCount;
        int configuredRequestBudget =
            manager.SchedulerTelemetry.ConfiguredRequestBudget;
        int configuredPrimaryRayBudget =
            ResolveConfiguredSimpleDdgiPrimaryRayBudget(
                input.Settings.DdgiProbeUpdatePrimaryRayBudget);
        sceneData.DdgiProbeVolumeCount = manager.VolumeCount;
        sceneData.DdgiProbeCount = manager.ProbeCount;
        sceneData.DdgiActiveProbeCount = manager.ActiveProbeCount;
        sceneData.DdgiRaysPerProbe = manager.RaysPerProbe;
        sceneData.DdgiProbesUpdated = probesToUpdate;
        sceneData.SimpleDdgiActive = manager.ProbeCount > 0 ? 1 : 0;
        sceneData.SimpleDdgiSchedulerMode = manager.SchedulerMode;
        sceneData.SimpleDdgiSchedulerReady =
            manager.GpuScheduler.IsReady ? 1 : 0;
        sceneData.SimpleDdgiSchedulerFeedbackValid =
            schedulerFeedbackValid ? 1 : 0;
        sceneData.SimpleDdgiSchedulerFeedbackFrameSerial =
            manager.GpuSchedulerFeedbackFrameSerial;
        sceneData.SimpleDdgiSchedulerFeedbackConsideredCount =
            schedulerFeedback.ConsideredCount;
        sceneData.SimpleDdgiSchedulerFeedbackEligibleCount =
            schedulerFeedback.EligibleCount;
        sceneData.SimpleDdgiSchedulerFeedbackAcceptedCount =
            schedulerFeedback.AcceptedCount;
        sceneData.SimpleDdgiSchedulerFeedbackCommittedCount =
            schedulerFeedback.CommittedCount;
        sceneData.SimpleDdgiSchedulerFeedbackFailedCommitCount =
            schedulerFeedback.FailedCommitCount;
        sceneData.SimpleDdgiSchedulerCommitFailureBreakdown =
            manager.GpuScheduler.LastCommitFailureBreakdown.ToString();
        sceneData.SimpleDdgiSchedulerFeedbackPendingFreshCount =
            schedulerFeedback.PendingFreshCount;
        sceneData.SimpleDdgiSchedulerFeedbackPendingExposedCount =
            schedulerFeedback.PendingExposedCount;
        sceneData.SimpleDdgiSchedulerFeedbackPendingRelocationCount =
            schedulerFeedback.PendingRelocationCount;
        sceneData.SimpleDdgiSchedulerFeedbackPendingSourceCount =
            schedulerFeedback.PendingSourceCount;
        sceneData.SimpleDdgiSchedulerFeedbackPendingSourceInvalidFlagCount =
            schedulerFeedback
                .PackedPendingSourceInvalidAndCardinalityCounts & 0xffffu;
        sceneData
                .SimpleDdgiSchedulerFeedbackPendingSourcePrivateRepairCount =
            schedulerFeedback
                .PackedPendingSourceRepairAndGenerationCounts & 0xffffu;
        sceneData.SimpleDdgiSchedulerFeedbackPendingSourceCardinalityCount =
            schedulerFeedback
                .PackedPendingSourceInvalidAndCardinalityCounts >> 16;
        sceneData.SimpleDdgiSchedulerFeedbackPendingSourceGenerationCount =
            schedulerFeedback
                .PackedPendingSourceRepairAndGenerationCounts >> 16;
        sceneData.SimpleDdgiSchedulerFeedbackSolveParticipantCount =
            schedulerFeedback.SolveEpochParticipantCount;
        sceneData.SimpleDdgiSchedulerFeedbackSolveVisitedCount =
            schedulerFeedback.SolveEpochVisitedCount;
        sceneData.SimpleDdgiSchedulerFeedbackSolveEpoch =
            schedulerFeedback.SolveEpoch;
        sceneData.SimpleDdgiSchedulerFeedbackPrimaryRayCount =
            schedulerFeedback.PrimaryRayUsed;
        sceneData.SimpleDdgiSchedulerFeedbackSourceRayCount =
            schedulerFeedback.SourceAchievedRays;
        sceneData.SimpleDdgiSchedulerFeedbackTransportRayCount =
            schedulerFeedback.TransportRayUsed;
        sceneData.SimpleDdgiSchedulerFeedbackSourceProbeCount =
            schedulerFeedback.SourceProbeUsed;
        sceneData.SimpleDdgiSchedulerFeedbackHardSourceProbeCount =
            schedulerFeedback.HardSourceProbeUsed;
        sceneData.SimpleDdgiSchedulerFeedbackRoutineSourceProbeCount =
            schedulerFeedback.RoutineSourceProbeUsed;
        sceneData.SimpleDdgiSchedulerFeedbackCachedSolverProbeCount =
            schedulerFeedback.CachedSolverProbeUsed;
        sceneData.SimpleDdgiSchedulerFeedbackPublishedCount =
            schedulerFeedback.PublishedCount;
        sceneData.SimpleDdgiSchedulerResourceGeneration =
            manager.GpuScheduler.ResourceGeneration;
        sceneData.SimpleDdgiSchedulerArenaBytes =
            manager.GpuSchedulerArenaBytes;
        SimpleDdgiSchedulerCostEstimate schedulerCost =
            manager.SchedulerCostEstimate;
        sceneData.SimpleDdgiCostAwareSchedulingActive =
            input.Settings.SimpleDdgiCostAwareSchedulingEnabled ? 1 : 0;
        sceneData.SimpleDdgiSchedulerCostSampleCount =
            schedulerCost.AcceptedSampleCount;
        sceneData.SimpleDdgiSchedulerVisibilityPerPrimary =
            SimpleDdgiSchedulerCostModel.DecodeQ8(
                schedulerCost.VisibilityPerPrimaryQ8);
        sceneData.SimpleDdgiSchedulerAlphaCandidatesPerPrimary =
            SimpleDdgiSchedulerCostModel.DecodeQ8(
                schedulerCost.AlphaCandidatesPerPrimaryQ8);
        sceneData.SimpleDdgiSchedulerMaterialEvaluationsPerPrimary =
            SimpleDdgiSchedulerCostModel.DecodeQ8(
                schedulerCost.MaterialEvaluationsPerPrimaryQ8);
        sceneData.SimpleDdgiSchedulerFarFieldStepsPerPrimary =
            SimpleDdgiSchedulerCostModel.DecodeQ8(
                schedulerCost.FarFieldStepsPerPrimaryQ8);
        sceneData.SimpleDdgiSparseResidualPropagationActive =
            input.Settings.SimpleDdgiSparseResidualPropagationEnabled ? 1 : 0;
        SimpleDdgiResidualPropagationEvidence residualEvidence =
            manager.GpuScheduler.LastResidualPropagationEvidence;
        sceneData.SimpleDdgiResidualSeededCount = residualEvidence.SeededCount;
        sceneData.SimpleDdgiResidualDependentWakeCount =
            residualEvidence.DependentWakeCount;
        sceneData.SimpleDdgiResidualThresholdRejectedCount =
            residualEvidence.ThresholdRejectedCount;
        sceneData.SimpleDdgiResidualCompleteSweepFallbackCount =
            residualEvidence.CompleteSweepFallbackCount;
        sceneData.SimpleDdgiReceiverContributionFeedbackActive =
            input.Settings.SimpleDdgiReceiverContributionFeedbackEnabled &&
            manager.SchedulerMode.IsGpuMode()
                ? 1
                : 0;
        SimpleDdgiReceiverContributionEvidence receiverEvidence =
            manager.GpuScheduler.LastReceiverContributionEvidence;
        sceneData.SimpleDdgiReceiverContributingProbeCount =
            receiverEvidence.ContributingProbeCount;
        sceneData.SimpleDdgiReceiverCoverageBucketCount =
            receiverEvidence.CoverageBucketCount;
        sceneData.SimpleDdgiReceiverFallbackProbeCount =
            receiverEvidence.FallbackProbeCount;
        sceneData.SimpleDdgiReceiverConsumerMask = receiverEvidence.ConsumerMask;
        sceneData.SimpleDdgiUrgentRelightActive =
            input.Settings.SimpleDdgiUrgentRelightEnabled &&
            manager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
            !manager.RadiometricRelightPublicationPending
                ? 1
                : 0;
        SimpleDdgiUrgentRelightEvidence urgentEvidence =
            manager.GpuScheduler.LastUrgentRelightEvidence;
        sceneData.SimpleDdgiUrgentRelightAcceptedCount =
            urgentEvidence.AcceptedProbeCount;
        sceneData.SimpleDdgiUrgentRelightCommittedCount =
            urgentEvidence.CommittedProbeCount;
        sceneData.SimpleDdgiUrgentRelightRejectedCount =
            urgentEvidence.RejectedProbeCount;
        sceneData.SimpleDdgiSchedulerFeedbackReadbackBytes =
            manager.GpuSchedulerFeedbackReadbackBytes;
        sceneData.SimpleDdgiSchedulerRetiredBytes =
            manager.GpuSchedulerRetiredBytes;
        sceneData.SimpleDdgiSchedulerStaleFeedbackCount =
            manager.GpuScheduler.StaleFeedbackCount;
        sceneData.SimpleDdgiSchedulerFeedbackGenerationRejectionCount =
            manager.GpuSchedulerFeedbackGenerationRejectionCount;
        sceneData.SimpleDdgiSchedulerFallbackLatched =
            manager.GpuSchedulerFallbackLatched ? 1 : 0;
        sceneData.SimpleDdgiSchedulerFallbackFreshResetPending =
            manager.GpuSchedulerFallbackFreshResetPending ? 1 : 0;
        sceneData.SimpleDdgiSchedulerFallbackCount =
            manager.GpuSchedulerFallbackCount;
        sceneData.SimpleDdgiSchedulerFallbackReason =
            manager.GpuSchedulerFallbackReason;
        sceneData.SimpleDdgiSchedulerFallbackExportPending =
            manager.GpuSchedulerFallbackExportPending ? 1 : 0;
        sceneData.SimpleDdgiSchedulerFallbackExportBytes =
            manager.GpuScheduler.FallbackStateExportBytes;
        sceneData.SimpleDdgiSchedulerStateExportSuccessCount =
            manager.GpuSchedulerStateExportSuccessCount;
        sceneData.SimpleDdgiSchedulerStateExportFailureCount =
            manager.GpuSchedulerStateExportFailureCount;
        sceneData.SimpleDdgiSchedulerReentryStableFrameCount =
            manager.GpuSchedulerReentryStableFrameCount;
        sceneData.SimpleDdgiSchedulerReentryCount =
            manager.GpuSchedulerReentryCount;
        sceneData.SimpleDdgiProbeCount = manager.ProbeCount;
        sceneData.SimpleDdgiProbesUpdated = probesToUpdate;
        sceneData.SimpleDdgiRaysPerFrame = primaryRayCount;
        sceneData.SimpleDdgiTransportV2Active =
            manager.TransportV2Active ? 1 : 0;
        sceneData.SimpleDdgiAutomaticProbeDensityActive =
            manager.TransportV2Active &&
            input.Settings.SimpleDdgiAutomaticProbeDensityEnabled
                ? 1
                : 0;
        sceneData.SimpleDdgiTransportSourceRefreshProbeCount =
            frameWork.SourceRefreshProbeCount;
        sceneData.SimpleDdgiTransportSourceRefreshTargetProbeCount =
            manager.SourceRefreshTargetProbeCount;
        sceneData.SimpleDdgiTransportSourceRefreshCapacityShortfall =
            manager.SourceRefreshCapacityShortfall;
        sceneData.SimpleDdgiTransportSourceCohortTransitionActive =
            manager.SourceCohortTransitionActive ? 1 : 0;
        sceneData.SimpleDdgiTransportSourceCohortTransitionCount =
            manager.SourceCohortTransitionCount;
        sceneData.SimpleDdgiTransportSourceCohortElapsedFrames =
            manager.SourceCohortTransitionElapsedFrames;
        sceneData.SimpleDdgiTransportSourceStepStaleProbeCount =
            manager.SourceStepStaleProbeCount;
        sceneData.SimpleDdgiTransportSourceStepAgeP95Frames =
            manager.SourceStepAgeP95Frames;
        sceneData.SimpleDdgiTransportSourceStepAgeMaximumFrames =
            manager.SourceStepAgeMaximumFrames;
        sceneData.SimpleDdgiTransportSourceStepAgeP95Seconds =
            manager.SourceStepAgeP95Seconds;
        sceneData.SimpleDdgiTransportSourceStepAgeMaximumSeconds =
            manager.SourceStepAgeMaximumSeconds;
        sceneData.SimpleDdgiTransportSourceCacheReuseProbeCount = Math.Max(
            0,
            frameWork.ScheduledProbeCount - frameWork.SourceRefreshProbeCount);
        sceneData.SimpleDdgiTransportSourceRayCount = frameWork.SourceRayCount;
        sceneData.SimpleDdgiTransportSolveRayCount =
            frameWork.TransportRayCount;
        sceneData.SimpleDdgiTransportPublishedProbeCount =
            frameWork.PublishedProbeCount;
        sceneData.SimpleDdgiTransportPublishRegionCount =
            manager.TransportPublishRegionCount;
        sceneData.SimpleDdgiTransportPublishedProbeTotal =
            manager.TransportPublishedProbeTotal;
        sceneData.SimpleDdgiTransportPublishRegionTotal =
            manager.TransportPublishRegionTotal;
        sceneData.SimpleDdgiUpdateTransactionAbortCount =
            manager.UpdateTransactionAbortCount;
        sceneData.SimpleDdgiTransportSourceCacheInvalidationCount =
            manager.SourceCacheInvalidationCount;
        sceneData.SimpleDdgiTransportSolverInvalidationCount =
            manager.SourceRefreshTransportInvalidationCount;
        sceneData.SimpleDdgiTransportSolverInvalidationsPerSourceRefresh =
            manager.SourceRefreshTransportInvalidationsPerRefresh;
        SimpleDdgiAtmosphereCohortFeedback cohort =
            manager.CreateAtmosphereCohortFeedbackSnapshot();
        sceneData.SimpleDdgiVolumeResourceGeneration =
            manager.VolumeTableGeneration;
        sceneData.SimpleDdgiTransportTopologyGeneration =
            manager.TransportTopologyGeneration;
        sceneData.SimpleDdgiVolumeRemapKind =
            manager.VolumeRemapKindThisFrame;
        sceneData.SimpleDdgiCompatibleToroidalScrollCount =
            manager.CompatibleToroidalScrollCount;
        sceneData.SimpleDdgiIncompatibleTopologyChangeCount =
            manager.IncompatibleTopologyChangeCount;
        sceneData.SimpleDdgiGlobalConvergenceRestartCount =
            manager.GlobalConvergenceRestartCount;
        sceneData.SimpleDdgiWholeReadbackDropCount =
            manager.WholeReadbackDropCount;
        sceneData.SimpleDdgiSourceLightingGeneration =
            cohort.SourceCohortGeneration;
        sceneData.SimpleDdgiAdmittedSourceCohortGeneration =
            cohort.AdmittedSourceCohortGeneration;
        sceneData.SimpleDdgiTransportGeneration =
            cohort.PropagationGeneration;
        sceneData.SimpleDdgiPublishedPropagationGeneration =
            cohort.PublishedPropagationGeneration;
        sceneData.SimpleDdgiLivePropagationSourceGeneration =
            manager.LivePropagationSourceGeneration;
        sceneData.SimpleDdgiVisiblePriorityParticipatingProbeCount =
            cohort.VisiblePriorityParticipatingProbeCount;
        sceneData.SimpleDdgiVisiblePrioritySourceReadyProbeCount =
            cohort.VisiblePrioritySourceReadyProbeCount;
        sceneData.SimpleDdgiVisiblePriorityPublishedProbeCount =
            cohort.VisiblePriorityPublishedProbeCount;
        sceneData.SimpleDdgiQuietPeriodComplete =
            cohort.QuietPeriodComplete ? 1 : 0;
        manager.GetTransportProgress(
            out int sourceReadyProbeCount,
            out int sourceStaleProbeCount,
            out int convergedProbeCount,
            out int pendingSolverProbeCount);
        sceneData.SimpleDdgiTransportSourceReadyProbeCount =
            sourceReadyProbeCount;
        sceneData.SimpleDdgiTransportSourceStaleProbeCount =
            sourceStaleProbeCount;
        sceneData.SimpleDdgiTransportConvergedProbeCount =
            convergedProbeCount;
        sceneData.SimpleDdgiTransportPendingSolverProbeCount =
            pendingSolverProbeCount;
        sceneData.SimpleDdgiTransportGlobalConvergencePending =
            manager.TransportGlobalConvergencePending ? 1 : 0;
        sceneData.SimpleDdgiTransportGlobalConvergenceElapsedFrames =
            manager.TransportGlobalConvergenceElapsedFrames;
        sceneData.SimpleDdgiTransportConvergence =
            manager.CreateTransportConvergenceTelemetry();
        sceneData.SimpleDdgiTrackingState = manager.TrackingState;
        sceneData.SimpleDdgiTransportCalibrationChangeCount =
            manager.TransportCalibrationChangeCount;
        sceneData.SimpleDdgiTransportIrradianceAtlasBytes =
            manager.TransportIrradianceAtlasBytes;
        sceneData.SimpleDdgiTransportSourceCacheBytes =
            manager.TransportSourceCacheBytes;
        sceneData.SimpleDdgiTransportSolverRelaxation =
            input.Settings.SimpleDdgiTransportSolverRelaxation;
        sceneData.SimpleDdgiTransportAlbedoClamp =
            input.Settings.SimpleDdgiTransportAlbedoClamp;
        sceneData.SimpleDdgiTransportTailRelativeTolerance =
            input.Settings.SimpleDdgiTransportTailRelativeTolerance;
        sceneData.SimpleDdgiTransportAcceleratedSweepCount =
            input.Settings.SimpleDdgiTransportAcceleratedSweepCount;
        sceneData.SimpleDdgiTransportAccelerationEnabled =
            input.Settings.SimpleDdgiTransportAccelerationEnabled;
        sceneData.SimpleDdgiTransportTailCertificationEnabled =
            manager.TailCertificationEnabled;
        sceneData.SimpleDdgiTransportTailCertificationFallbackReason =
            manager.TailCertificationFallbackReason;
        sceneData.SimpleDdgiSchedulerAuditReadbackBytes =
            manager.GpuSchedulerAuditReadbackBytes;
        sceneData.SimpleDdgiTransportResidualThreshold =
            sceneData.SimpleDdgiTransportTailRelativeTolerance;
        sceneData.SimpleDdgiTransportMaximumSolverGenerations =
            input.Settings.SimpleDdgiTransportMaximumSolverGenerations;
        sceneData.SimpleDdgiTransportSourceRefreshFrames =
            manager.EffectiveTransportSourceRefreshFrames;
        sceneData.SimpleDdgiTransportConfiguredSourceRefreshFrames =
            input.Settings.SimpleDdgiTransportSourceRefreshFrames;
        sceneData.SimpleDdgiInactiveProbeCount = manager.InactiveProbeCount;
        sceneData.SimpleDdgiInactiveProbeSkipCount =
            manager.InactiveProbeSkipCount;
        sceneData.SimpleDdgiSavedRaysPerFrame =
            manager.InactiveProbeSavedPrimaryRayCount;
        sceneData.SimpleDdgiLightingDirtyFrames = manager.LightingDirtyFrames;
        sceneData.SimpleDdgiLightingDirtyBoostedCapacity =
            manager.LightingDirtyBoostedCapacity;
        sceneData.SimpleDdgiDirtyReasonFlags = manager.DirtyReasonFlags;
        sceneData.SimpleDdgiFullRayProbeUpdateCount =
            manager.FullRayProbeUpdateCount;
        sceneData.SimpleDdgiMaintenanceRayProbeUpdateCount =
            manager.MaintenanceRayProbeUpdateCount;
        SimpleDdgiAdaptiveRayEvidence adaptiveRayEvidence =
            manager.SchedulerMode.IsGpuMode() && schedulerFeedbackValid
                ? manager.GpuScheduler.LastAdaptiveRayEvidence
                : default;
        sceneData.SimpleDdgiAdaptiveRayEvidence = adaptiveRayEvidence;
        sceneData.SimpleDdgiAdaptiveRaySavedRaysPerFrame =
            manager.SchedulerMode.IsGpuMode()
                ? schedulerFeedbackValid
                    ? adaptiveRayEvidence.TotalSavedRayCount
                    : 0UL
                : manager.AdaptiveRaySavedPrimaryRayCount;
        sceneData.SimpleDdgiNearFullRayProbeUpdateCount =
            manager.NearFullRayProbeUpdateCount;
        sceneData.SimpleDdgiMidFullRayProbeUpdateCount =
            manager.MidFullRayProbeUpdateCount;
        sceneData.SimpleDdgiFarFullRayProbeUpdateCount =
            manager.FarFullRayProbeUpdateCount;
        sceneData.SimpleDdgiNearMaintenanceRayProbeUpdateCount =
            manager.NearMaintenanceRayProbeUpdateCount;
        sceneData.SimpleDdgiMidMaintenanceRayProbeUpdateCount =
            manager.MidMaintenanceRayProbeUpdateCount;
        sceneData.SimpleDdgiFarMaintenanceRayProbeUpdateCount =
            manager.FarMaintenanceRayProbeUpdateCount;
        sceneData.SimpleDdgiNearScheduledPrimaryRayCount =
            manager.NearScheduledPrimaryRayCount;
        sceneData.SimpleDdgiMidScheduledPrimaryRayCount =
            manager.MidScheduledPrimaryRayCount;
        sceneData.SimpleDdgiFarScheduledPrimaryRayCount =
            manager.FarScheduledPrimaryRayCount;
        sceneData.SimpleDdgiDirtyFirstUpdateLatencySampleCount =
            manager.DirtyFirstUpdateLatencySampleCount;
        sceneData.SimpleDdgiDirtyFirstUpdateLatencyP50Frames =
            manager.DirtyFirstUpdateLatencyP50Frames;
        sceneData.SimpleDdgiDirtyFirstUpdateLatencyP95Frames =
            manager.DirtyFirstUpdateLatencyP95Frames;
        sceneData.SimpleDdgiDirtyFirstUpdateLatencyMaxFrames =
            manager.DirtyFirstUpdateLatencyMaxFrames;
        sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge =
            manager.OldestVisibleUnsupportedProbeAge;
        sceneData.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget =
            manager.VisibleUnsupportedProbeCountAboveLatencyTarget;
        sceneData.SimpleDdgiVisibleZeroSupportRepairUpdateCount =
            manager.VisibleZeroSupportRepairUpdateCount;
        sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames =
            manager.ProbeLifecycleLatencyTargetFrames;
        sceneData.SimpleDdgiMaximumFreshProbeAge =
            manager.MaximumFreshProbeAge;
        sceneData.SimpleDdgiMaximumScrollExposedProbeAge =
            manager.MaximumScrollExposedProbeAge;
        sceneData.SimpleDdgiMaximumRelocationPendingProbeAge =
            manager.MaximumRelocationPendingProbeAge;
        sceneData.SimpleDdgiMaximumUnpublishedProbeAge =
            manager.MaximumUnpublishedProbeAge;
        sceneData.SimpleDdgiProbeLifecycleBoundExceededCount =
            manager.ProbeLifecycleBoundExceededCount;
        sceneData.SimpleDdgiDirtyConvergenceLatencySampleCount =
            manager.DirtyConvergenceLatencySampleCount;
        sceneData.SimpleDdgiDirtyConvergenceLatencyP50Frames =
            manager.DirtyConvergenceLatencyP50Frames;
        sceneData.SimpleDdgiDirtyConvergenceLatencyP95Frames =
            manager.DirtyConvergenceLatencyP95Frames;
        sceneData.SimpleDdgiDirtyConvergenceLatencyMaxFrames =
            manager.DirtyConvergenceLatencyMaxFrames;
        sceneData.SimpleDdgiMutationLatency = manager.MutationLatencyTelemetry;
        sceneData.SimpleDdgiAtlasBytes = manager.AtlasBytes;
        sceneData.SimpleDdgiSampledAtlasRequested =
            manager.SampledAtlasRequested ? 1 : 0;
        sceneData.SimpleDdgiSampledAtlasActive =
            manager.SampledAtlasActive ? 1 : 0;
        sceneData.SimpleDdgiSampledAtlasGroupCount =
            manager.SampledAtlasGroupCount;
        sceneData.SimpleDdgiSampledAtlasLayersPerTexture =
            manager.SampledAtlasLayersPerTexture;
        sceneData.SimpleDdgiSampledAtlasImageBytes =
            manager.SampledAtlasImageBytes;
        sceneData.SimpleDdgiSampledAtlasFallbackReason =
            manager.SampledAtlasFallbackReason;
        sceneData.SimpleDdgiStorage = manager.CreateStorageDiagnostics() with
        {
            ValidationCounters = sceneData.SimpleDdgiStorageValidation
        };
        sceneData.SimpleDdgiWarmStart = manager.WarmStartTelemetry;
        sceneData.SimpleDdgiRefinement = manager.RefinementBrickDiagnostics;
        sceneData.SimpleDdgiRefinementEmissiveDemand =
            input.EmissiveRefinementDiagnostics;
        sceneData.SimpleDdgiNearVisibility = manager.NearVisibilityDiagnostics
            with
            {
                Evidence = input.NearVisibilityEvidence
            };
        sceneData.SimpleDdgiRecentered =
            manager.RecenteredThisFrame ? 1 : 0;
        sceneData.SimpleDdgiAtlasPreservedOnRecenter =
            manager.AtlasPreservedOnRecenterThisFrame ? 1 : 0;
        sceneData.SimpleDdgiAtlasCleared =
            manager.AtlasClearedThisFrame ? 1 : 0;
        sceneData.SimpleDdgiAtlasFresh = manager.AtlasFresh ? 1 : 0;
        sceneData.SimpleDdgiRecenterCount = manager.TotalRecenterCount;
        sceneData.SimpleDdgiAtlasClearCount = manager.TotalAtlasClearCount;
        sceneData.SimpleDdgiAtlasPreserveOnRecenterCount =
            manager.TotalAtlasPreserveOnRecenterCount;
        sceneData.SimpleDdgiFramesSinceLastClear = manager.FramesSinceLastClear;
        sceneData.SimpleDdgiFramesSinceLastRecenter =
            manager.FramesSinceLastRecenter;
        sceneData.DdgiFullRefreshFrameCount = manager.FullRefreshFrameCount;
        sceneData.DdgiPartialRefreshFrameCount =
            manager.PartialRefreshFrameCount;
        sceneData.DdgiUpdatedProbeFraction = manager.ProbeCount > 0
            ? Math.Clamp(
                probesToUpdate / (float)manager.ProbeCount,
                0.0f,
                1.0f)
            : 0.0f;
        sceneData.DdgiProbeUpdateStartIndex = manager.UpdateStartProbe;
        sceneData.DdgiProbeUpdateEndIndex =
            manager.ProbeCount > 0 && probesToUpdate > 0
                ? (manager.UpdateStartProbe + probesToUpdate - 1) %
                  manager.ProbeCount
                : 0;
        sceneData.DdgiSkippedProbeCount =
            Math.Max(0, manager.ProbeCount - probesToUpdate);
        manager.GetEstimatedProbeAgeFrames(
            out float estimatedAgeP50,
            out float estimatedAgeP95,
            out float estimatedAgeMaximum);
        sceneData.DdgiFramesSinceProbeUpdatedP50 = estimatedAgeP50;
        sceneData.DdgiFramesSinceProbeUpdatedP95 = estimatedAgeP95;
        sceneData.DdgiFramesSinceProbeUpdatedMax = estimatedAgeMaximum;
        sceneData.DdgiNewlyInvalidatedProbeCount =
            manager.NewlyInvalidatedProbeCount;
        sceneData.DdgiRefreshReasonRecenterProbeCount =
            manager.RecenterRefreshProbeCount;
        sceneData.DdgiRefreshReasonDirtyProbeCount =
            manager.DirtyRefreshProbeCount;
        sceneData.DdgiRefreshReasonAgeProbeCount = manager.AgeRefreshProbeCount;
        sceneData.DdgiRefreshReasonVisibilityProbeCount = 0;
        sceneData.DdgiRefreshReasonFullRefreshProbeCount =
            manager.FullRefreshProbeCount;
        if (!input.Core.RayUpdateActive && manager.ProbeCount > 0)
        {
            sceneData.DdgiSimpleTraceTlasUnavailableFrameCount = Math.Max(
                sceneData.DdgiSimpleTraceTlasUnavailableFrameCount,
                1u);
        }

        sceneData.DdgiMaxActiveProbeBudget =
            manager.LastLayoutReport?.Budget.ProbeBudget ?? manager.ProbeCount;
        sceneData.DdgiMaxProbeUpdatesPerFrame = configuredRequestBudget;
        sceneData.DdgiProbeUpdateRequestBudget = configuredRequestBudget;
        sceneData.DdgiProbeUpdatePrimaryRayBudget =
            configuredPrimaryRayBudget;
        sceneData.DdgiScheduledRequestBudget = configuredRequestBudget;
        sceneData.DdgiScheduledPrimaryRayBudget =
            configuredPrimaryRayBudget;
        sceneData.DdgiTraceDispatchGroupCount =
            (uint)Math.Max(0, probesToUpdate);
        sceneData.DdgiTraceProbeCount = (uint)Math.Max(0, probesToUpdate);
        sceneData.DdgiTraceRayCount =
            (uint)Math.Min(uint.MaxValue, primaryRayCount);
        sceneData.DdgiBlendProbeCount = (uint)Math.Max(0, probesToUpdate);
        sceneData.DdgiRelocateClassifyProbeCount =
            (uint)Math.Max(0, probesToUpdate);
        sceneData.DdgiPublishProbeCount = (uint)Math.Max(0, probesToUpdate);
        sceneData.DdgiRayScratchBytes = manager.RayScratchBytes;
        sceneData.DdgiUpdatedAtlasBytes = manager.AtlasBytes;
        sceneData.DdgiProbeStateBufferBytes = manager.ProbeStateBytes;
        sceneData.DdgiProbeUpdateQueueBytes = manager.ProbeUpdateQueueBytes;
        sceneData.DdgiProbeRelocationClassificationBytes =
            manager.RelocationClassificationBytes;
        sceneData.DdgiCurrentIrradianceAtlasBytes =
            manager.IrradianceAtlasBytes;
        sceneData.DdgiCurrentVisibilityAtlasBytes =
            manager.VisibilityAtlasBytes;
        sceneData.DdgiAtlasMemoryBudgetBytes =
            input.Settings.DdgiAtlasMemoryBudgetBytes;
        sceneData.DdgiTextureBytes = checked(manager.SampledAtlasImageBytes);
        sceneData.DdgiBufferBytes = checked(
            manager.BufferBytes +
            input.ReceiverCacheBufferBytes +
            input.ReceiverGatherBufferBytes +
            input.ReceiverSurfaceSidecarBytes);
        sceneData.DdgiProbeRelocationCount = manager.ProbeRelocationCount;
        sceneData.DdgiProbeClassificationCount = probesToUpdate;
        sceneData.DdgiClassifiedInactiveProbeCountEstimate =
            manager.ClassifiedInactiveProbeCountEstimate;
        sceneData.DdgiAverageRelocationFractionEstimate =
            manager.AverageRelocationFractionEstimate;
        sceneData.DdgiAverageRelocationDisplacementFractionEstimate =
            manager.AverageRelocationFractionEstimate;
        sceneData.DdgiRelocatedProbeFractionEstimate = manager.ProbeCount > 0
            ? Math.Clamp(
                manager.ProbeRelocationCount / (float)manager.ProbeCount,
                0.0f,
                1.0f)
            : 0.0f;
        sceneData.SimpleDdgiAverageBackfaceRatioEstimate =
            manager.AverageBackfaceRatioEstimate;
        sceneData.SimpleDdgiAverageCloseRatioEstimate =
            manager.AverageCloseRatioEstimate;
        sceneData.SimpleDdgiAverageHardInvalidProbeScoreEstimate =
            manager.AverageHardInvalidProbeScoreEstimate;
        sceneData.DdgiScrollCount = manager.ScrollCopyCount;
        sceneData.DdgiVolumeDiagnostics.Clear();
        sceneData.DdgiVolumeDiagnostics.AddRange(
            manager.GetVolumeDiagnostics());
        sceneData.DdgiScheduledPrimaryRayCount = primaryRayCount;
        sceneData.DdgiEffectiveMaxShadedLights =
            manager.EffectiveMaxShadedLights;
        sceneData.DdgiEstimatedShadowRayUpperBound =
            EstimateSimpleDdgiShadowRayUpperBound(
                primaryRayCount,
                sceneData.DirectionalLightCount,
                sceneData.LocalLightCount,
                sceneData.DdgiEffectiveMaxShadedLights);
        PopulateSimpleDdgiLightSelectionDiagnostics(
            sceneData,
            primaryRayCount,
            sceneData.DirectionalLightCount,
            sceneData.LocalLightCount,
            sceneData.DdgiEffectiveMaxShadedLights);
        sceneData.DdgiQualityTier = input.Settings.DdgiQualityTier;
        sceneData.DdgiAsyncComputeEnabled = 0;
        sceneData.DdgiCacheGeneration = 1u;
        DdgiRuntimeWarmupState warmupState = ResolveSimpleDdgiWarmupState(
            manager.ProbeCount,
            manager.TransportGlobalConvergencePending,
            manager.TailCertificationEnabled,
            manager.TransportTailCertificateCurrent,
            manager.TransportTailPhase,
            sceneData.SimpleDdgiRefinement);
        sceneData.DdgiWarmupState = warmupState;
        sceneData.DdgiCacheWarmupState = warmupState;
        sceneData.DdgiWarmedVisibleProbeFraction = 1.0f;
        sceneData.DdgiWarmedLocalProbeFraction = 1.0f;
        sceneData.DdgiWarmedCascade0ProbeFraction = 1.0f;
        sceneData.DdgiUpdateSkipReason = string.Empty;
        sceneData.DdgiPublishSkipReason = string.Empty;
        ProjectSimpleDdgiLiveness(sceneData, manager, input);
    }

    private static void ProjectSimpleDdgiLiveness(
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager manager,
        in DdgiFrameProjectionInput input)
    {
        SimpleDdgiLivenessSnapshot snapshot =
            sceneData.SimpleDdgiActive == 0
                ? input.FrameEvidence.EvaluateLiveness(default)
                : input.FrameEvidence.EvaluateLiveness(
                    new SimpleDdgiLivenessRequest(
                        Active: true,
                        GpuSchedulerAuthoritative:
                        manager.SchedulerMode ==
                        SimpleDdgiSchedulerMode.GpuResident,
                        SparseResidencyAuthoritative:
                        manager.ProbeResidencyMode.UsesSparsePayloads(),
                        FrameSerial: sceneData.DdgiFrameSerial,
                        ProbesUpdated: sceneData.SimpleDdgiProbesUpdated,
                        Recentered: sceneData.SimpleDdgiRecentered != 0,
                        AtlasCleared: sceneData.SimpleDdgiAtlasCleared != 0,
                        SourceCohortTransitionActive:
                        manager.SourceCohortTransitionActive ||
                        sceneData
                            .SimpleDdgiTransportSourceCohortTransitionActive !=
                        0,
                        ConfiguredPrimaryRayBudget:
                        ResolveConfiguredSimpleDdgiPrimaryRayBudget(
                            input.Settings.DdgiProbeUpdatePrimaryRayBudget),
                        SchedulerFeedback: manager.LastGpuSchedulerFeedback,
                        SchedulerFeedbackValid:
                        manager.GpuSchedulerFeedbackValid,
                        SchedulerFeedbackFrameSerial:
                        manager.GpuSchedulerFeedbackFrameSerial,
                        SchedulerFeedbackGenerationRejectionCount:
                        manager
                            .GpuSchedulerFeedbackGenerationRejectionCount,
                        SchedulerFeedbackTransportTopologyGeneration:
                        manager.GpuScheduler
                            .LastFeedbackTransportTopologyGeneration,
                        SchedulerResourceGeneration:
                        manager.GpuScheduler.ResourceGeneration,
                        SchedulerFeedbackCoversCurrentVolumeTable:
                        manager.GpuSchedulerFeedbackCoversCurrentVolumeTable,
                        SchedulerEligibility:
                        manager.GpuSchedulerEligibilityEvidence,
                        ResidencyFeedback:
                        manager.LastProbeResidencyFeedback,
                        ResidencyFeedbackValid:
                        manager.ProbeResidencyFeedbackValid,
                        ResidencyFeedbackFrameSerial:
                        manager.ProbeResidencyFeedbackFrameSerial,
                        ResidencyFeedbackGenerationRejectionCount:
                        manager
                            .ProbeResidencyFeedbackGenerationRejectionCount,
                        ResidencyResourceGeneration:
                        manager.ProbeResidencyResourceGeneration,
                        VolumeTableGeneration: manager.VolumeTableGeneration,
                        TransportTopologyGeneration:
                        manager.TransportTopologyGeneration,
                        SourceLightingGeneration:
                        manager.SourceLightingGeneration,
                        TransportGeneration: manager.TransportGeneration,
                        TransportGlobalConvergencePending:
                        manager.TransportGlobalConvergencePending,
                        HasPendingUpdateTransaction:
                        manager.HasPendingUpdateTransaction,
                        ReceiverRecordsPublishedCount:
                        manager.ReceiverRecordsPublishedCount,
                        SchedulerTelemetry: manager.SchedulerTelemetry,
                        ProbeResidencyBootstrapClassificationActive:
                        manager
                            .ProbeResidencyBootstrapClassificationActive,
                        TransportTailAuditPending:
                        manager.TransportTailAuditPending,
                        TransactionAbortDeltas:
                        manager.LivenessTransactionAbortReasonDeltas,
                        SourceCacheInvalidationDeltas:
                        manager
                            .LivenessSourceCacheInvalidationReasonDeltas));

        sceneData.SimpleDdgiLivenessTelemetry = snapshot.Telemetry;
        sceneData.SimpleDdgiLivenessWatchdog = snapshot.Watchdog;
    }
}

internal readonly record struct DdgiFrameProjectionInput(
    SimpleDdgiCoreFrameResult Core,
    SimpleDdgiVolumeManager? VolumeManager,
    GlobalIlluminationSettings Settings,
    SimpleDdgiRefinementEmissiveDemandDiagnostics
        EmissiveRefinementDiagnostics,
    SimpleDdgiNearVisibilityGpuCounters NearVisibilityEvidence,
    ulong ReceiverCacheBufferBytes,
    ulong ReceiverGatherBufferBytes,
    ulong ReceiverSurfaceSidecarBytes,
    SimpleDdgiFrameEvidenceCoordinator FrameEvidence);

internal readonly record struct AdvancedGiFrameProjectionInput(
    GiRoadmapExperimentDiagnostics RoadmapExperiments,
    SimpleDdgiContentMemoryPlan ContentMemory);

internal readonly record struct SimpleDdgiFrameWork(
    int ScheduledProbeCount,
    int SourceRefreshProbeCount,
    ulong PrimaryRayCount,
    ulong SourceRayCount,
    ulong TransportRayCount,
    int PublishedProbeCount);
