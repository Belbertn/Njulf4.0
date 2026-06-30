using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data
{
    public enum DdgiRuntimeWarmupState
    {
        Disabled,
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
        float EstimateCoverage,
        float EstimateVisibleSupport,
        float EstimateEffectiveWeight,
        float EstimateRelocationMagnitude,
        int EstimateInactiveProbeCount,
        int GatherFallbackTileCount,
        int EmptyGatherTileCount,
        int SelectedLocalTileCount,
        int SelectedClipmapTileCount)
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
            EstimateCoverage: 0.0f,
            EstimateVisibleSupport: 0.0f,
            EstimateEffectiveWeight: 0.0f,
            EstimateRelocationMagnitude: 0.0f,
            EstimateInactiveProbeCount: 0,
            GatherFallbackTileCount: 0,
            EmptyGatherTileCount: 0,
            SelectedLocalTileCount: 0,
            SelectedClipmapTileCount: 0);
    }

    public readonly record struct DdgiForwardEstimateCounters(
        int ReadbackValid,
        float CoverageAverage,
        float VisibleSupportAverage,
        float EffectiveWeightAverage,
        float RawDiffuseLuminanceAverage,
        float FinalDiffuseLuminanceAverage,
        uint SampleCount,
        uint ZeroVisibleButCoveredCount,
        uint ZeroEffectiveButCoveredCount,
        float VisibilityMomentMeanAverage,
        float VisibilityMomentVarianceAverage,
        float VisibilityProbeDistanceAverage,
        uint VisibilityMomentSampleCount,
        uint VisibilityLargeDistanceMarginCount,
        uint VisibilityZeroTransportCount,
        uint VisibilityZeroTransportWithIrradianceCount)
    {
        public static DdgiForwardEstimateCounters Empty { get; } = default;
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
        private int _localWarmupIncompleteFrames;
        private int _cascade0WarmupIncompleteFrames;

        public IReadOnlyList<string> Update(
            DdgiRuntimeSnapshot snapshot,
            bool schedulerOverBudget,
            int persistenceFrames = DefaultPersistenceFrames,
            int targetWarmupFrames = DefaultTargetWarmupFrames)
        {
            persistenceFrames = Math.Max(1, persistenceFrames);
            targetWarmupFrames = Math.Max(1, targetWarmupFrames);

            UpdateCounter(ref _coverageVisibleCollapseFrames,
                snapshot.EstimateCoverage > 0.75f && snapshot.EstimateVisibleSupport < 0.05f);
            UpdateCounter(ref _coverageEffectiveCollapseFrames,
                snapshot.EstimateCoverage > 0.75f && snapshot.EstimateEffectiveWeight < 0.05f);
            UpdateCounter(ref _schedulerOverBudgetFrames, schedulerOverBudget);
            UpdateCounter(ref _budgetRejectedDominatesFrames,
                snapshot.SchedulerRequestCount > 0 &&
                snapshot.SchedulerBudgetRejectedCount > snapshot.SchedulerRequestCount * 8);
            UpdateCounter(ref _warmupStarvedFrames,
                snapshot.ScheduledProbeUpdates > 0 &&
                snapshot.ActiveProbeCount / Math.Max(1.0f, snapshot.ScheduledProbeUpdates) > targetWarmupFrames);
            UpdateCounter(ref _localWarmupIncompleteFrames,
                snapshot.WarmupState is DdgiRuntimeWarmupState.LocalVolumeWarmup or DdgiRuntimeWarmupState.Recovery &&
                snapshot.WarmedLocalProbeFraction < 0.80f);
            UpdateCounter(ref _cascade0WarmupIncompleteFrames,
                snapshot.WarmupState is DdgiRuntimeWarmupState.NearCascadeWarmup or DdgiRuntimeWarmupState.Recovery &&
                snapshot.WarmedCascade0ProbeFraction < 0.80f);

            List<string>? warnings = null;
            AddIfPersistent(ref warnings, _coverageVisibleCollapseFrames, persistenceFrames,
                "DDGI coverage is high but visible support has remained below 0.05.");
            AddIfPersistent(ref warnings, _coverageEffectiveCollapseFrames, persistenceFrames,
                "DDGI coverage is high but effective contribution has remained below 0.05.");
            AddIfPersistent(ref warnings, _schedulerOverBudgetFrames, persistenceFrames,
                "DDGI scheduler has remained over budget.");
            AddIfPersistent(ref warnings, _budgetRejectedDominatesFrames, persistenceFrames,
                "DDGI scheduler budget rejections have remained more than 8x accepted requests.");
            AddIfPersistent(ref warnings, _warmupStarvedFrames, persistenceFrames,
                "DDGI active probe count is too large for the current scheduled update rate.");
            AddIfPersistent(ref warnings, _localWarmupIncompleteFrames, Math.Min(persistenceFrames, 30),
                "DDGI local visible probe warmup has remained below 80%.");
            AddIfPersistent(ref warnings, _cascade0WarmupIncompleteFrames, Math.Min(persistenceFrames, 60),
                "DDGI cascade 0 visible probe warmup has remained below 80%.");

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
