using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// Frozen GPU ABI for the cache-accepted alpha-mask receiver list. The list
/// is deliberately separate from B1's 48-byte candidate ABI: raster writes a
/// surface sample here, then the post-forward compute pass performs the exact
/// gather and publishes the unchanged B1 owner records.
/// </summary>
public static class SimpleDdgiMaskedFeedbackCompactionAbi
{
    public const uint Version = 1u;
    public const uint HeaderWords = 4u;
    public const uint HeaderBytes = HeaderWords * sizeof(uint);
    public const uint RecordWords = 12u;
    public const uint RecordBytes = RecordWords * sizeof(uint);
    public const uint ActiveBit = 1u << 31;
    public const uint InitializedBit = 1u << 30;
    public const uint CapacityMask = InitializedBit - 1u;
    public const uint WorkgroupSize = 64u;

    public const uint PublishedCountWord = 0u;
    public const uint OverflowFallbackCountWord = 1u;
    public const uint CandidateHighWaterWord = 2u;
    public const uint StateWord = 3u;

    // A cold renderer has no fence-complete observation yet. This bounded
    // bootstrap covers a modest masked scene; any excess runs the exact
    // fragment fallback and raises the measured high-water for later frames.
    public const uint BootstrapCapacity = 1024u;
    public const uint FixedSafetyEntries = 256u;
    public const uint SafetyMarginNumerator = 3u;
    public const uint SafetyMarginDenominator = 2u;

    public static uint ResolvePhysicalCapacity(uint tileCount)
    {
        if (tileCount == 0u)
            return 0u;

        // Producer 1 and producer 6 use independently rotating representatives.
        // Two records per screen tile therefore cover the non-overdraw upper
        // bound while pathological overdraw remains quality-safe via inline
        // fallback.
        return tileCount > CapacityMask / 2u
            ? CapacityMask
            : tileCount * 2u;
    }

    public static uint ResolveLogicalCapacity(
        uint observedHighWater,
        uint previousCapacity,
        uint physicalCapacity)
    {
        if (physicalCapacity == 0u)
            return 0u;

        ulong measuredWithMargin = observedHighWater == 0u
            ? BootstrapCapacity
            : checked(
                ((ulong)observedHighWater * SafetyMarginNumerator +
                 SafetyMarginDenominator - 1u) /
                SafetyMarginDenominator + FixedSafetyEntries);
        ulong requested = Math.Max((ulong)previousCapacity, measuredWithMargin);
        return checked((uint)Math.Min(requested, physicalCapacity));
    }

    public static ulong ResolveBufferBytes(uint physicalCapacity) => checked(
        HeaderBytes + (ulong)physicalCapacity * RecordBytes);

    public static uint PackState(uint logicalCapacity, bool active)
    {
        if ((logicalCapacity & ~CapacityMask) != 0u)
            throw new ArgumentOutOfRangeException(nameof(logicalCapacity));
        return logicalCapacity | InitializedBit | (active ? ActiveBit : 0u);
    }
}

public readonly record struct SimpleDdgiMaskedFeedbackCompactionCounters(
    int ReadbackValid,
    uint PublishedCount,
    uint OverflowFallbackCount,
    uint CandidateHighWater,
    uint LogicalCapacity,
    uint ObservedHighWater,
    ulong BufferBytes)
{
    public static SimpleDdgiMaskedFeedbackCompactionCounters Unavailable =>
        default;
}
