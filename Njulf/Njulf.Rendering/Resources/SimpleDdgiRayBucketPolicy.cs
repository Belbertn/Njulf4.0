using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Builds the fixed six-entry source-ray cardinality table shared by CPU
/// scroll planning and the GPU scheduler frame header. A cardinality is valid
/// for a frame only when it occurs in this table.
/// </summary>
internal static class SimpleDdgiRayBucketPolicy
{
    public static int Build(
        GlobalIlluminationSettings settings,
        Span<uint> buckets)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (buckets.Length < SimpleDdgiSchedulerAbi.MaxRayBucketCount)
        {
            throw new ArgumentException(
                $"At least {SimpleDdgiSchedulerAbi.MaxRayBucketCount} bucket entries are required.",
                nameof(buckets));
        }

        buckets[..SimpleDdgiSchedulerAbi.MaxRayBucketCount].Clear();
        Span<int> fullRays =
        [
            settings.SimpleDdgiNearFullRaysPerProbe,
            settings.SimpleDdgiMidFullRaysPerProbe,
            settings.SimpleDdgiFarFullRaysPerProbe
        ];
        Span<int> maintenanceRays =
        [
            settings.SimpleDdgiNearMaintenanceRaysPerProbe,
            settings.SimpleDdgiMidMaintenanceRaysPerProbe,
            settings.SimpleDdgiFarMaintenanceRaysPerProbe
        ];
        // Authored and legacy volumes use the near policy. Keep it present even
        // when camera-relative rings have been disabled by a custom profile.
        int qualityCount = Math.Clamp(settings.SimpleDdgiRingCount, 1, 3);
        int count = 0;
        for (int quality = 0; quality < qualityCount; quality++)
        {
            count = Add(buckets, count, fullRays[quality]);
            count = Add(buckets, count, maintenanceRays[quality]);
        }

        // Preserve authored endpoints first. Any remaining ABI entries carry
        // nested adaptive prefixes in near-to-far order.
        Span<uint> adaptiveTiers =
            stackalloc uint[SimpleDdgiAdaptiveRayCardinality.TierCount];
        for (int quality = 0;
             quality < qualityCount && count < SimpleDdgiSchedulerAbi.MaxRayBucketCount;
             quality++)
        {
            SimpleDdgiAdaptiveRayCardinality.BuildTiers(
                checked((uint)Math.Max(1, maintenanceRays[quality])),
                checked((uint)Math.Max(1, fullRays[quality])),
                adaptiveTiers);
            for (int tier = 1;
                 tier < adaptiveTiers.Length - 1 &&
                 count < SimpleDdgiSchedulerAbi.MaxRayBucketCount;
                 tier++)
            {
                count = Add(
                    buckets,
                    count,
                    checked((int)adaptiveTiers[tier]));
            }
        }

        if (count == 0)
            count = Add(buckets, count, fullRays[0]);
        return count;
    }

    public static bool TrySelectHighest(
        ReadOnlySpan<uint> buckets,
        int minimumRays,
        int maximumRays,
        ulong affordableRaysPerProbe,
        out int selectedRays)
    {
        selectedRays = 0;
        int safeMaximum = Math.Clamp(
            maximumRays,
            1,
            GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
        int safeMinimum = Math.Clamp(minimumRays, 1, safeMaximum);
        ulong affordable = Math.Min(
            affordableRaysPerProbe,
            (ulong)safeMaximum);
        foreach (uint bucket in buckets)
        {
            if (bucket == 0u ||
                bucket < (uint)safeMinimum ||
                bucket > (uint)safeMaximum ||
                bucket > affordable ||
                bucket <= (uint)selectedRays)
            {
                continue;
            }

            selectedRays = checked((int)bucket);
        }

        return selectedRays != 0;
    }

    public static bool Contains(ReadOnlySpan<uint> buckets, int rayCount)
    {
        if (rayCount <= 0)
            return false;
        foreach (uint bucket in buckets)
        {
            if (bucket == (uint)rayCount)
                return true;
        }

        return false;
    }

    private static int Add(Span<uint> buckets, int count, int rayCount)
    {
        uint value = checked((uint)Math.Clamp(
            rayCount,
            1,
            GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe));
        for (int i = 0; i < count; i++)
        {
            if (buckets[i] == value)
                return count;
        }

        if (count >= SimpleDdgiSchedulerAbi.MaxRayBucketCount)
            return count;
        buckets[count] = value;
        return count + 1;
    }
}
