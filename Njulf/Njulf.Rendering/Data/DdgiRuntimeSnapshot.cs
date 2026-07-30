using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data
{
    public enum DdgiRuntimeWarmupState
    {
        Disabled = 0,
        NoCache = Disabled,
        ColdStart,
        LocalVolumeWarmup,
        NearCascadeWarmup,
        SteadyState,
        Recovery
    }

    public readonly record struct DdgiRuntimeSnapshot(
        int VolumeCount,
        int ActiveProbeCount,
        int ScheduledProbeUpdates,
        DdgiRuntimeWarmupState WarmupState,
        float WarmedVisibleProbeFraction,
        float WarmedLocalProbeFraction,
        float WarmedCascade0ProbeFraction,
        int SchedulerCandidateCount,
        int SchedulerRequestCount,
        int SchedulerBudgetRejectedCount,
        long SchedulerGpuMicroseconds,
        long SchedulerGpuP95Microseconds,
        float EstimateSpatialCoverage,
        float EstimateSupportCoverage,
        float EstimateDataConfidence,
        float EstimateVisibilityConfidence,
        float EstimateLeakAttenuation,
        float EstimateEffectiveWeight,
        float EstimateOwnershipConsumed,
        float EstimateRelocationMagnitude,
        int EstimateInactiveProbeCount,
        int GatherFallbackTileCount,
        int EmptyGatherTileCount,
        int SelectedLocalTileCount,
        int SelectedClipmapTileCount,
        IReadOnlyList<uint> SimpleGatherPrimaryRejectionCounts,
        IReadOnlyList<uint> SimpleGatherFallbackRejectionCounts,
        IReadOnlyList<uint> SimpleGatherRecoveryRejectionCounts,
        uint SimpleGatherPrimaryAllFailedCount,
        uint SimpleGatherFallbackAllFailedCount,
        uint SimpleGatherRecoveryAllFailedCount,
        uint SimpleOldestVisibleUnsupportedProbeAge,
        int SimpleVisibleUnsupportedProbeCountAboveLatencyTarget,
        int SimpleVisibleZeroSupportRepairUpdateCount,
        int SimpleProbeLifecycleLatencyTargetFrames,
        uint SimpleMaximumFreshProbeAge,
        uint SimpleMaximumScrollExposedProbeAge,
        uint SimpleMaximumRelocationPendingProbeAge,
        uint SimpleMaximumUnpublishedProbeAge,
        int SimpleProbeLifecycleBoundExceededCount,
        int SimpleActive,
        int SimpleProbeCount,
        int SimpleProbesUpdated,
        ulong SimpleRaysPerFrame,
        ulong SimpleAtlasBytes,
        long SimpleGpuTraceMicroseconds,
        long SimpleGpuTransportMicroseconds,
        long SimpleGpuBlendMicroseconds)
    {
        public static DdgiRuntimeSnapshot Empty { get; } = new(
            VolumeCount: 0,
            ActiveProbeCount: 0,
            ScheduledProbeUpdates: 0,
            WarmupState: DdgiRuntimeWarmupState.Disabled,
            WarmedVisibleProbeFraction: 0.0f,
            WarmedLocalProbeFraction: 0.0f,
            WarmedCascade0ProbeFraction: 0.0f,
            SchedulerCandidateCount: 0,
            SchedulerRequestCount: 0,
            SchedulerBudgetRejectedCount: 0,
            SchedulerGpuMicroseconds: 0,
            SchedulerGpuP95Microseconds: 0,
            EstimateSpatialCoverage: 0.0f,
            EstimateSupportCoverage: 0.0f,
            EstimateDataConfidence: 0.0f,
            EstimateVisibilityConfidence: 0.0f,
            EstimateLeakAttenuation: 0.0f,
            EstimateEffectiveWeight: 0.0f,
            EstimateOwnershipConsumed: 0.0f,
            EstimateRelocationMagnitude: 0.0f,
            EstimateInactiveProbeCount: 0,
            GatherFallbackTileCount: 0,
            EmptyGatherTileCount: 0,
            SelectedLocalTileCount: 0,
            SelectedClipmapTileCount: 0,
            SimpleGatherPrimaryRejectionCounts: Array.Empty<uint>(),
            SimpleGatherFallbackRejectionCounts: Array.Empty<uint>(),
            SimpleGatherRecoveryRejectionCounts: Array.Empty<uint>(),
            SimpleGatherPrimaryAllFailedCount: 0,
            SimpleGatherFallbackAllFailedCount: 0,
            SimpleGatherRecoveryAllFailedCount: 0,
            SimpleOldestVisibleUnsupportedProbeAge: 0,
            SimpleVisibleUnsupportedProbeCountAboveLatencyTarget: 0,
            SimpleVisibleZeroSupportRepairUpdateCount: 0,
            SimpleProbeLifecycleLatencyTargetFrames: 0,
            SimpleMaximumFreshProbeAge: 0,
            SimpleMaximumScrollExposedProbeAge: 0,
            SimpleMaximumRelocationPendingProbeAge: 0,
            SimpleMaximumUnpublishedProbeAge: 0,
            SimpleProbeLifecycleBoundExceededCount: 0,
            SimpleActive: 0,
            SimpleProbeCount: 0,
            SimpleProbesUpdated: 0,
            SimpleRaysPerFrame: 0UL,
            SimpleAtlasBytes: 0UL,
            SimpleGpuTraceMicroseconds: 0,
            SimpleGpuTransportMicroseconds: 0,
            SimpleGpuBlendMicroseconds: 0);

    }

    public readonly record struct DdgiForwardEstimateCounters(
        int ReadbackValid,
        float SpatialCoverageAverage,
        float SupportCoverageAverage,
        float DataConfidenceAverage,
        float VisibilityConfidenceAverage,
        float LeakAttenuationAverage,
        float EffectiveWeightAverage,
        float RawDiffuseLuminanceAverage,
        float FinalDiffuseLuminanceAverage,
        float EnvironmentFallbackWeightAverage,
        float OwnershipConsumedAverage,
        float SampledIrradianceLuminanceAverage,
        uint SampleCount,
        uint ZeroSupportButSpatiallyCoveredCount,
        uint ZeroEffectiveButSpatiallyCoveredCount,
        uint HighOwnershipLowDeliveredIndirectCount,
        float VisibilityMomentMeanAverage,
        float VisibilityMomentVarianceAverage,
        float VisibilityProbeDistanceAverage,
        uint VisibilityMomentSampleCount,
        uint VisibilityLargeDistanceMarginCount,
        uint VisibilityZeroTransportCount,
        uint VisibilityZeroTransportWithIrradianceCount,
        uint SupportRejectedInactiveCount,
        uint SupportRejectedZeroIrradianceAlphaCount,
        uint SupportRejectedLowQualityCount,
        float ProbeIrradianceAlphaAverage,
        float ProbeQualityXAverage,
        float ProbeQualityYAverage,
        float ProbeQualityZAverage,
        uint ProbeQualitySampleCount,
        uint SampledProbeCurrentFrustumCount,
        uint SampledProbeSideRearCount,
        uint SampledProbeStaleAgeCount,
        uint ClipmapInfoPrimaryAttemptCount,
        uint ClipmapInfoPrimaryOkCount,
        uint ClipmapInfoPrimaryFailedCount,
        float ClipmapInfoPrimaryEdgeFadeAverage,
        float ClipmapInfoPrimaryBlendWeightAverage,
        uint FastGatherAttemptCount,
        uint FastGatherAcceptedCount,
        uint FastGatherRejectedZeroSpatialCount,
        uint FastGatherRejectedZeroSupportCount,
        uint FastGatherRejectedZeroDataCount,
        uint FastGatherRejectedZeroOwnershipCount,
        uint ShaderGatherFallbackAttemptCount,
        uint ShaderGatherFallbackAcceptedCount,
        uint ShaderGatherFallbackEmptyCount,
        uint TraceEnergySampleCount,
        uint TraceEnergyHitCount,
        uint TraceEnergyMissCount,
        float TraceEnergyRayLuminanceAverage,
        float TraceEnergyDirectLuminanceAverage,
        float TraceEnergyEmissiveLuminanceAverage,
        float TraceEnergyStableLuminanceAverage,
        float TraceEnergySkyLuminanceAverage,
        uint TraceEnergyHitZeroDirectCount,
        uint TraceEnergyHitWithDirectCount,
        float TraceEnergyDirectNoShadowLuminanceAverage,
        uint ShadowVisibilityRayCount,
        uint ShadowVisibilityOccludedCount,
        uint ShadowVisibilityNearHitCount,
        float ShadowVisibilityCommittedHitDistanceAverage,
        uint TraceEarlyOutDisabledCount,
        uint TraceEarlyOutBeyondRequestCount,
        uint TraceEarlyOutResolveBoundsCount,
        uint TraceEarlyOutResolveProbeRangeCount,
        uint TraceEarlyOutResolveClipmapCellCount,
        uint TraceEarlyOutResolveClipmapRingCount,
        uint TraceRingMismatchSampleValid,
        uint TraceRingMismatchSampleUpdateIndex,
        uint TraceRingMismatchSampleRequestProbeIndex,
        uint TraceRingMismatchSampleVolumeIndex,
        int TraceRingMismatchSampleLogicalCellX,
        int TraceRingMismatchSampleLogicalCellY,
        int TraceRingMismatchSampleLogicalCellZ,
        uint TraceRingMismatchSampleFirstProbe,
        uint TraceRingMismatchSampleComputedProbeIndex,
        int TraceRingMismatchSampleGridMinX,
        int TraceRingMismatchSampleGridMinY,
        int TraceRingMismatchSampleGridMinZ,
        int TraceRingMismatchSampleRingOffsetX,
        int TraceRingMismatchSampleRingOffsetY,
        int TraceRingMismatchSampleRingOffsetZ,
        uint TraceRingMismatchSampleProbeCountX,
        uint TraceRingMismatchSampleProbeCountY,
        uint TraceRingMismatchSampleProbeCountZ,
        uint TraceRingMismatchSampleRequestAgeFrames,
        uint TraceRingMismatchCorrectedCount,
        uint BlendEnergySampleCount,
        float BlendEnergyIrradianceLuminanceAverage,
        float BlendEnergyConfidenceAverage,
        uint BlendEnergyLowConfidenceCount,
        uint BlendEnergyNonzeroIrradianceCount,
        uint BlendEnergyNonFiniteIrradianceCount,
        uint BlendEnergyFireflySuppressedCount,
        uint SimpleDdgiTransportEnergySampleCount,
        uint SimpleDdgiTransportSourceCacheHitCount,
        uint SimpleDdgiTransportSourceCacheMissCount,
        float SimpleDdgiTransportBounceLuminanceAverage,
        float SimpleDdgiTransportSourceLuminanceAverage,
        float SimpleDdgiTransportTotalLuminanceAverage)
    {
        public static DdgiForwardEstimateCounters Empty { get; } = default;

        public float CoverageAverage => SpatialCoverageAverage;
        public float VisibleSupportAverage => SupportCoverageAverage;
        public uint ZeroVisibleButCoveredCount => ZeroSupportButSpatiallyCoveredCount;
        public uint ZeroEffectiveButCoveredCount => ZeroEffectiveButSpatiallyCoveredCount;
    }

    public readonly record struct DdgiInvestigationCounters(
        int ReadbackValid,
        uint SimpleForwardSampleCount,
        uint LegacyForwardSampleCount,
        uint FreshAtlasForwardSampleCount,
        uint SimpleZeroIrradianceSampleCount,
        uint SimpleNonzeroIrradianceSampleCount,
        float SimpleSampledIrradianceLuminanceAverage,
        float SimpleVisibilityAverage,
        uint SimpleLowVisibilitySampleCount,
        uint ForwardZeroFinalIndirectCount,
        uint ForwardZeroDdgiButNonzeroIblCount,
        uint ForwardZeroDdgiAndZeroIblCount,
        uint ForwardOutOfGridSampleCount,
        uint ForwardClampedProbeSampleCount,
        uint ForwardNanOrInfSampleCount,
        uint IrradianceAtlasZeroTexelSampleCount,
        uint VisibilityAtlasZeroMomentSampleCount,
        uint AtlasWriteProbeCount,
        uint AtlasWriteTexelCount,
        uint BlendZeroRayWeightProbeCount,
        uint BlendNonzeroIrradianceProbeCount,
        uint BlendPreviousAtlasUsedCount,
        uint BlendHysteresisZeroFrameCount,
        uint SimpleTraceHitCount,
        uint SimpleTraceMissCount,
        uint SimpleTraceZeroRadianceHitCount,
        uint SimpleTraceDirectLightHitCount,
        uint SimpleTraceEmissiveHitCount,
        uint SimpleTraceFarFieldHitCount,
        uint SimpleTraceFarFieldMissCount,
        uint SimpleTraceTlasUnavailableFrameCount,
        uint SkyVisibilitySampleCount,
        float SkyVisibilityAverage,
        uint FarSunShadowSampleCount,
        uint FarSunShadowOccludedCount,
        uint RoughSpecularSampleCount,
        uint RoughSpecularNonzeroCount,
        uint SimpleGatherCount,
        uint SimpleSecondVolumeGatherCount,
        IReadOnlyList<uint>? SimpleVolumePrimaryGatherCounts,
        IReadOnlyList<uint>? SimpleVolumeSampledGatherCounts,
        IReadOnlyList<uint>? SimpleGatherPrimaryRejectionCounts,
        IReadOnlyList<uint>? SimpleGatherFallbackRejectionCounts,
        IReadOnlyList<uint>? SimpleGatherRecoveryRejectionCounts,
        uint SimpleGatherPrimaryAllFailedCount,
        uint SimpleGatherFallbackAllFailedCount,
        uint SimpleGatherRecoveryAllFailedCount,
        uint FarFieldStepBucket0Count,
        uint FarFieldStepBucket1Count,
        uint FarFieldStepBucket2Count,
        uint FarFieldStepBucket3Count,
        uint FarFieldStepBucket4Count)
    {
        public static DdgiInvestigationCounters Empty { get; } = default;
    }

    public sealed class DdgiDiagnosticWarningTracker
    {
        public const int DefaultPersistenceFrames = 30;
        public const int DefaultTargetWarmupFrames = 60;

        private int _coverageVisibleCollapseFrames;
        private int _coverageEffectiveCollapseFrames;
        private int _schedulerOverBudgetFrames;
        private int _budgetRejectedDominatesFrames;
        private int _warmupStarvedFrames;
        private int _visibleWarmupIncompleteFrames;
        private int _localWarmupIncompleteFrames;
        private int _cascade0WarmupIncompleteFrames;
        private int _simpleLifecycleBoundExceededFrames;

        public IReadOnlyList<string> Update(
            DdgiRuntimeSnapshot snapshot,
            bool schedulerOverBudget,
            int persistenceFrames = DefaultPersistenceFrames,
            int targetWarmupFrames = DefaultTargetWarmupFrames)
        {
            persistenceFrames = Math.Max(1, persistenceFrames);
            targetWarmupFrames = Math.Max(1, targetWarmupFrames);

            UpdateCounter(ref _coverageVisibleCollapseFrames,
                snapshot.EstimateSpatialCoverage > 0.75f && snapshot.EstimateSupportCoverage < 0.05f);
            UpdateCounter(ref _coverageEffectiveCollapseFrames,
                snapshot.EstimateSpatialCoverage > 0.75f && snapshot.EstimateEffectiveWeight < 0.05f);
            UpdateCounter(ref _schedulerOverBudgetFrames, schedulerOverBudget);
            UpdateCounter(ref _budgetRejectedDominatesFrames,
                snapshot.SchedulerRequestCount > 0 &&
                snapshot.SchedulerBudgetRejectedCount > snapshot.SchedulerRequestCount * 8);
            UpdateCounter(ref _warmupStarvedFrames,
                snapshot.ScheduledProbeUpdates > 0 &&
                snapshot.ActiveProbeCount / Math.Max(1.0f, snapshot.ScheduledProbeUpdates) > targetWarmupFrames);
            UpdateCounter(ref _visibleWarmupIncompleteFrames,
                (snapshot.WarmupState is DdgiRuntimeWarmupState.LocalVolumeWarmup
                    or DdgiRuntimeWarmupState.NearCascadeWarmup
                    or DdgiRuntimeWarmupState.Recovery) &&
                snapshot.WarmedVisibleProbeFraction < 0.80f);
            UpdateCounter(ref _localWarmupIncompleteFrames,
                snapshot.WarmupState is DdgiRuntimeWarmupState.LocalVolumeWarmup or DdgiRuntimeWarmupState.Recovery &&
                snapshot.WarmedLocalProbeFraction < 0.80f);
            UpdateCounter(ref _cascade0WarmupIncompleteFrames,
                snapshot.WarmupState is DdgiRuntimeWarmupState.NearCascadeWarmup or DdgiRuntimeWarmupState.Recovery &&
                snapshot.WarmedCascade0ProbeFraction < 0.80f);
            UpdateCounter(
                ref _simpleLifecycleBoundExceededFrames,
                snapshot.SimpleActive != 0 &&
                snapshot.SimpleProbeLifecycleBoundExceededCount > 0);

            List<string>? warnings = null;
            AddIfPersistent(ref warnings, _coverageVisibleCollapseFrames, persistenceFrames,
                "DDGI spatial coverage is high but support coverage has remained below 0.05.");
            AddIfPersistent(ref warnings, _coverageEffectiveCollapseFrames, persistenceFrames,
                "DDGI spatial coverage is high but effective contribution has remained below 0.05.");
            AddIfPersistent(ref warnings, _schedulerOverBudgetFrames, persistenceFrames,
                "DDGI scheduler has remained over budget.");
            AddIfPersistent(ref warnings, _budgetRejectedDominatesFrames, persistenceFrames,
                "DDGI scheduler budget rejections have remained more than 8x accepted requests.");
            AddIfPersistent(ref warnings, _warmupStarvedFrames, persistenceFrames,
                "DDGI active probe count is too large for the current scheduled update rate.");
            AddIfPersistent(ref warnings, _visibleWarmupIncompleteFrames, Math.Min(persistenceFrames, 45),
                "DDGI visible probe warmup has remained below 80%.");
            AddIfPersistent(ref warnings, _localWarmupIncompleteFrames, Math.Min(persistenceFrames, 30),
                "DDGI local visible probe warmup has remained below 80%.");
            AddIfPersistent(ref warnings, _cascade0WarmupIncompleteFrames, Math.Min(persistenceFrames, 60),
                "DDGI cascade 0 visible probe warmup has remained below 80%.");
            AddIfPersistent(
                ref warnings,
                _simpleLifecycleBoundExceededFrames,
                1,
                "Simple DDGI has visible probes exceeding the bounded fresh/scroll/relocation/publication latency.");

            return warnings == null ? Array.Empty<string>() : warnings;
        }

        private static void UpdateCounter(ref int counter, bool active)
        {
            counter = active ? Math.Min(counter + 1, int.MaxValue - 1) : 0;
        }

        private static void AddIfPersistent(
            ref List<string>? warnings,
            int count,
            int threshold,
            string warning)
        {
            if (count <= threshold)
                return;

            warnings ??= new List<string>();
            warnings.Add(warning);
        }
    }
}
