using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Canonical four-level source-ray policy mirrored by the resident scheduler.
/// Levels are derived from the authored maintenance/full bounds, prefer nested
/// power-of-two Fibonacci subsets, and remain valid when authored bounds are
/// not powers of two themselves.
/// </summary>
public static class SimpleDdgiAdaptiveRayCardinality
{
    public const int TierCount = 4;

    public static void BuildTiers(uint maintenanceRays, uint fullRays, Span<uint> tiers)
    {
        if (tiers.Length < TierCount)
            throw new ArgumentException("Four DDGI cardinality tiers are required.", nameof(tiers));

        uint maximum = Math.Clamp(
            fullRays,
            1u,
            (uint)GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
        uint minimum = Math.Clamp(maintenanceRays, 1u, maximum);
        uint quarter = Math.Max(minimum, DivideRoundUp(maximum, 4u));
        uint half = Math.Max(minimum, DivideRoundUp(maximum, 2u));

        tiers[0] = minimum;
        tiers[1] = Math.Clamp(CeilPowerOfTwo(quarter), minimum, maximum);
        tiers[2] = Math.Clamp(CeilPowerOfTwo(half), tiers[1], maximum);
        tiers[3] = maximum;
    }

    public static uint ResolveBaseline(uint maintenanceRays, uint fullRays, int ringIndex)
    {
        Span<uint> tiers = stackalloc uint[TierCount];
        BuildTiers(maintenanceRays, fullRays, tiers);
        // The current convergence residual measures iterative transport change,
        // not directional quadrature error. It cannot certify that a short
        // nested subset is spatially stable, so production baselines must
        // retain the authored full cardinality until a directional variance
        // witness is available. Keep ringIndex in the API because the GPU mirror is
        // volume/ring based and a future certified policy may use it again.
        _ = ringIndex;
        return tiers[^1];
    }

    public static bool IsValid(uint rayCount, uint maintenanceRays, uint fullRays)
    {
        Span<uint> tiers = stackalloc uint[TierCount];
        BuildTiers(maintenanceRays, fullRays, tiers);
        for (int i = 0; i < tiers.Length; i++)
        {
            if (tiers[i] == rayCount)
                return true;
        }
        return false;
    }

    public static uint Promote(uint rayCount, uint maintenanceRays, uint fullRays)
    {
        Span<uint> tiers = stackalloc uint[TierCount];
        BuildTiers(maintenanceRays, fullRays, tiers);
        for (int i = 0; i < tiers.Length; i++)
        {
            if (tiers[i] > rayCount)
                return tiers[i];
        }
        return tiers[^1];
    }

    public static uint Demote(uint rayCount, uint maintenanceRays, uint fullRays)
    {
        Span<uint> tiers = stackalloc uint[TierCount];
        BuildTiers(maintenanceRays, fullRays, tiers);
        uint result = tiers[0];
        for (int i = 0; i < tiers.Length; i++)
        {
            if (tiers[i] >= rayCount)
                break;
            result = tiers[i];
        }
        return result;
    }

    private static uint DivideRoundUp(uint value, uint divisor) =>
        (value + divisor - 1u) / divisor;

    private static uint CeilPowerOfTwo(uint value)
    {
        value = Math.Clamp(
            value,
            1u,
            (uint)GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        return Math.Min(
            value + 1u,
            (uint)GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
    }
}
