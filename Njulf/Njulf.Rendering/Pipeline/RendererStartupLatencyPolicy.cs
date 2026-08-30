using System;
using Njulf.Core.Interfaces;

namespace Njulf.Rendering.Pipeline;

internal enum RendererStartupCacheClass
{
    Warm,
    ApplicationCold
}

internal readonly record struct RendererStartupLatencyEvaluation(
    long ElapsedMicroseconds,
    RendererStartupCacheClass CacheClass,
    long AspirationalTargetMicroseconds,
    long HardLimitMicroseconds)
{
    public bool MeetsAspirationalTarget =>
        ElapsedMicroseconds <= AspirationalTargetMicroseconds;

    public bool MeetsHardLimit =>
        ElapsedMicroseconds <= HardLimitMicroseconds;
}

internal readonly record struct RendererStartupMilestoneLatencyEvaluation(
    RendererStartupMilestone Milestone,
    long ElapsedMicroseconds,
    long AspirationalTargetMicroseconds,
    long HardLimitMicroseconds,
    bool GateApplies)
{
    public bool MeetsAspirationalTarget =>
        !GateApplies || ElapsedMicroseconds <= AspirationalTargetMicroseconds;
    public bool MeetsHardLimit =>
        !GateApplies || ElapsedMicroseconds <= HardLimitMicroseconds;
}

internal static class RendererStartupLatencyPolicy
{
    internal const long WarmAspirationalTargetMicroseconds = 5_000_000;
    internal const long WarmHardLimitMicroseconds = 10_000_000;
    internal const long ApplicationColdAspirationalTargetMicroseconds =
        15_000_000;
    internal const long ApplicationColdHardLimitMicroseconds = 30_000_000;
    internal const long BootstrapAspirationalTargetMicroseconds = 3_000_000;
    internal const long BootstrapHardLimitMicroseconds = 5_000_000;

    internal static RendererStartupLatencyEvaluation Evaluate(
        long elapsedMicroseconds,
        bool applicationPipelineCacheLoaded)
    {
        if (elapsedMicroseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMicroseconds));

        RendererStartupCacheClass cacheClass = applicationPipelineCacheLoaded
            ? RendererStartupCacheClass.Warm
            : RendererStartupCacheClass.ApplicationCold;
        return cacheClass == RendererStartupCacheClass.Warm
            ? new RendererStartupLatencyEvaluation(
                elapsedMicroseconds,
                cacheClass,
                WarmAspirationalTargetMicroseconds,
                WarmHardLimitMicroseconds)
            : new RendererStartupLatencyEvaluation(
                elapsedMicroseconds,
                cacheClass,
                ApplicationColdAspirationalTargetMicroseconds,
                ApplicationColdHardLimitMicroseconds);
    }

    internal static bool ShouldFail(
        in RendererStartupLatencyEvaluation evaluation,
        RendererStartupLatencyGateMode gateMode) =>
        gateMode == RendererStartupLatencyGateMode.Enforce &&
        !evaluation.MeetsHardLimit;

    internal static RendererStartupMilestoneLatencyEvaluation
        EvaluateMilestone(
            RendererStartupMilestone milestone,
            long elapsedMicroseconds,
            bool warmApplicationCache,
            bool compatibleDeploymentSeed)
    {
        if (elapsedMicroseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMicroseconds));

        return milestone switch
        {
            RendererStartupMilestone.BootstrapPresent => new(
                milestone,
                elapsedMicroseconds,
                BootstrapAspirationalTargetMicroseconds,
                BootstrapHardLimitMicroseconds,
                GateApplies: true),
            RendererStartupMilestone.ScenePresent => new(
                milestone,
                elapsedMicroseconds,
                0,
                0,
                GateApplies: false),
            RendererStartupMilestone.FullQualityPresent => new(
                milestone,
                elapsedMicroseconds,
                0,
                0,
                GateApplies: false),
            RendererStartupMilestone.VisibleContentPresent
                when warmApplicationCache => new(
                    milestone,
                    elapsedMicroseconds,
                    WarmAspirationalTargetMicroseconds,
                    WarmHardLimitMicroseconds,
                    GateApplies: true),
            RendererStartupMilestone.VisibleContentPresent => new(
                milestone,
                elapsedMicroseconds,
                ApplicationColdAspirationalTargetMicroseconds,
                ApplicationColdHardLimitMicroseconds,
                GateApplies: true),
            _ => throw new ArgumentOutOfRangeException(nameof(milestone))
        };
    }

    internal static bool ShouldFail(
        in RendererStartupMilestoneLatencyEvaluation evaluation,
        RendererStartupLatencyGateMode gateMode) =>
        gateMode == RendererStartupLatencyGateMode.Enforce &&
        evaluation.GateApplies &&
        !evaluation.MeetsHardLimit;
}
