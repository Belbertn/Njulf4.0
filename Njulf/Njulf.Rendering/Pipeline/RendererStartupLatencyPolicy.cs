using System;

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

internal static class RendererStartupLatencyPolicy
{
    internal const long WarmAspirationalTargetMicroseconds = 5_000_000;
    internal const long WarmHardLimitMicroseconds = 10_000_000;
    internal const long ApplicationColdAspirationalTargetMicroseconds =
        15_000_000;
    internal const long ApplicationColdHardLimitMicroseconds = 30_000_000;

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
}
