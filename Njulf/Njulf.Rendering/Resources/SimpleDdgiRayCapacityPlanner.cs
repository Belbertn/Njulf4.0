using System;

namespace Njulf.Rendering.Resources;

/// <summary>One source-refresh tier used by the exact ray-equivalent capacity calculation.</summary>
public readonly record struct SimpleDdgiRayTier(
    int ParticipatingProbeCount,
    int SourceRaysPerProbe);

public readonly record struct SimpleDdgiRayCapacityResult(
    ulong TotalRequiredRays,
    ulong TargetRaysPerFrame,
    ulong AdmittedRaysPerFrame,
    ulong CapacityShortfall,
    float MinimumAchievableSweepSeconds,
    bool TargetIsFeasible);

/// <summary>
/// Converts mixed-quality source work into one reservation unit.  Probe counts are never used as
/// a proxy for ray counts: a low-tier probe and a high-tier probe consume their authored ray
/// counts, and the visible/repair reservation is compared against the same primary-ray budget.
/// </summary>
public static class SimpleDdgiRayCapacityPlanner
{
    /// <summary>
    /// Returns this tier's deterministic probe allotment for one frame. Across
    /// any complete target-frame cycle the allotments sum to the exact probe
    /// count, including tiers whose per-frame average is fractional.
    /// </summary>
    public static int ResolveTierProbeTarget(
        int participatingProbeCount,
        int targetFrames,
        uint frameIndex)
    {
        int probes = Math.Max(0, participatingProbeCount);
        int frames = Math.Max(1, targetFrames);
        int baseTarget = probes / frames;
        int remainder = probes % frames;
        int phase = (int)(frameIndex % (uint)frames);
        long before = (long)phase * remainder / frames;
        long after = (long)(phase + 1) * remainder / frames;
        return checked(baseTarget + (int)(after - before));
    }

    public static SimpleDdgiRayCapacityResult Evaluate(
        ReadOnlySpan<SimpleDdgiRayTier> tiers,
        int targetFrames,
        ulong admittedRaysPerFrame,
        float framesPerSecond)
    {
        ulong totalRequired = 0UL;
        for (int index = 0; index < tiers.Length; index++)
        {
            int probes = Math.Max(0, tiers[index].ParticipatingProbeCount);
            int rays = Math.Max(1, tiers[index].SourceRaysPerProbe);
            ulong tierRays = checked((ulong)probes * (ulong)rays);
            totalRequired = checked(totalRequired + tierRays);
        }

        int safeFrames = Math.Max(1, targetFrames);
        ulong targetPerFrame = totalRequired == 0UL
            ? 0UL
            : checked((totalRequired + (ulong)safeFrames - 1UL) / (ulong)safeFrames);
        ulong shortfall = targetPerFrame > admittedRaysPerFrame
            ? targetPerFrame - admittedRaysPerFrame
            : 0UL;
        float safeFps = float.IsFinite(framesPerSecond) && framesPerSecond > 0.0f
            ? framesPerSecond
            : 1.0f;
        float minimumSweepSeconds = admittedRaysPerFrame == 0UL
            ? totalRequired == 0UL ? 0.0f : float.PositiveInfinity
            : totalRequired / (float)admittedRaysPerFrame / safeFps;

        return new SimpleDdgiRayCapacityResult(
            totalRequired,
            targetPerFrame,
            admittedRaysPerFrame,
            shortfall,
            minimumSweepSeconds,
            shortfall == 0UL);
    }
}
