using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Fence-complete adaptive-cardinality evidence for one ring or volume-kind
/// bucket. A zero error with <see cref="QualityErrorValid"/> false means that
/// no current directional witness was available; it is never a quality pass.
/// </summary>
public readonly record struct SimpleDdgiAdaptiveRayBucketEvidence(
    uint SavedRayCount,
    float MaximumQuadratureError,
    bool QualityErrorValid)
{
    public static SimpleDdgiAdaptiveRayBucketEvidence FromPacked(
        uint savedRayCount,
        uint packedMaximumQuadratureError)
    {
        bool valid = SimpleDdgiAdaptiveRayCardinality.TryUnpackQuadratureWitness(
            packedMaximumQuadratureError,
            out float error);
        return new(
            savedRayCount,
            valid ? error : 0.0f,
            valid);
    }
}

/// <summary>
/// Bounded GPU-resident adaptive-ray summary. Ring buckets cover near, mid,
/// and far clipmap volumes. Content buckets are an exact partition by stable
/// volume kind: legacy/procedural, authored, ring, and refinement.
/// </summary>
public readonly record struct SimpleDdgiAdaptiveRayEvidence(
    SimpleDdgiAdaptiveRayBucketEvidence NearRing,
    SimpleDdgiAdaptiveRayBucketEvidence MidRing,
    SimpleDdgiAdaptiveRayBucketEvidence FarRing,
    SimpleDdgiAdaptiveRayBucketEvidence LegacyOrProcedural,
    SimpleDdgiAdaptiveRayBucketEvidence Authored,
    SimpleDdgiAdaptiveRayBucketEvidence Ring,
    SimpleDdgiAdaptiveRayBucketEvidence Refinement)
{
    public ulong TotalSavedRayCount =>
        (ulong)LegacyOrProcedural.SavedRayCount +
        Authored.SavedRayCount +
        Ring.SavedRayCount +
        Refinement.SavedRayCount;
}

/// <summary>
/// Canonical four-level source-ray policy mirrored by the resident scheduler.
/// Levels are derived from the authored maintenance/full bounds, prefer nested
/// power-of-two Fibonacci subsets, and remain valid when authored bounds are
/// not powers of two themselves.
/// </summary>
public static class SimpleDdgiAdaptiveRayCardinality
{
    public const int TierCount = 4;
    public const float ProductionQuadratureErrorThreshold = 0.05f;
    private const uint WitnessValidBit = 1u << 31;

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

    /// <summary>
    /// Packs a finite normalized nested-prefix quadrature error. Zero is kept
    /// as the invalid/unmeasured sentinel so scheduler activation from an older
    /// CPU snapshot can never silently certify a shorter ray sequence.
    /// </summary>
    public static uint PackQuadratureWitness(float normalizedError)
    {
        float error = float.IsFinite(normalizedError)
            ? Math.Clamp(normalizedError, 0.0f, 1.0f)
            : 1.0f;
        return WitnessValidBit |
            unchecked((uint)BitConverter.SingleToInt32Bits(error));
    }

    public static bool TryUnpackQuadratureWitness(
        uint packed,
        out float normalizedError)
    {
        normalizedError = 1.0f;
        if ((packed & WitnessValidBit) == 0u)
            return false;

        float decoded = BitConverter.Int32BitsToSingle(
            unchecked((int)(packed & ~WitnessValidBit)));
        if (!float.IsFinite(decoded) || decoded < 0.0f || decoded > 1.0f)
            return false;

        normalizedError = decoded;
        return true;
    }

    public static bool CertifiesDemotion(uint packedWitness) =>
        TryUnpackQuadratureWitness(packedWitness, out float error) &&
        error <= ProductionQuadratureErrorThreshold;

    public static bool RequiresPromotion(uint packedWitness) =>
        !TryUnpackQuadratureWitness(packedWitness, out float error) ||
        error > ProductionQuadratureErrorThreshold;

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
