using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Data;

/// <summary>
/// Selects the persistent Simple-DDGI transport representation. The value is
/// serialized, fingerprinted, and treated as a resource-generation boundary.
/// </summary>
public enum SimpleDdgiStoragePackingMode : uint
{
    /// <summary>The release-window rollback ABI: 36-byte cache rays and 32-byte scratch rays.</summary>
    Legacy = 0,

    /// <summary>
    /// Keeps the rollback byte layout while exercising deterministic direction
    /// reconstruction and validation telemetry.
    /// </summary>
    Validate = 1,

    /// <summary>Mixed Compact-28/Compact-24 cache regions and 20-byte scratch rays.</summary>
    Packed = 2
}

/// <summary>
/// Selects the page-local organization of packed transport-source records.
/// Auto admits the conditional sidecar only after a bounded completed-work
/// sample demonstrates enough miss/backface traffic to cover its address cost.
/// </summary>
public enum SimpleDdgiSourceCacheLayoutMode : uint
{
    FixedRecord = 0,
    Auto = 1,
    HotHeaderConditionalPayload = 2
}

/// <summary>Controls which complete physical probe ranges receive an image mirror.</summary>
public enum SimpleDdgiSampledAtlasCoverageMode : uint
{
    Disabled = 0,
    FullCanonical = 1,
    ReceiverRelevant = 2
}

/// <summary>Format selector encoded in bits 0..1 of GPUSimpleDdgiVolume.CacheLayout.W.</summary>
public enum SimpleDdgiTransportCacheFormat : uint
{
    Legacy36 = 0,
    Compact28 = 1,
    Compact24 = 2,
    Invalid = 3
}

/// <summary>Version encoded in bits 4..7 of GPUSimpleDdgiVolume.CacheLayout.W.</summary>
public enum SimpleDdgiStorageAbiVersion : uint
{
    Legacy = 4,
    Packed = 7
}

/// <summary>Opt-in GPU evidence for packed storage and compact mirror qualification.</summary>
public readonly record struct SimpleDdgiStorageValidationCounters(
    int ReadbackValid,
    uint MirrorInteriorOpportunityCount,
    uint MirrorImageHitCount,
    uint MirrorSeamFallbackCount,
    uint MirrorUnmirroredFallbackCount,
    uint MirrorInvalidMapFallbackCount,
    uint CachePackAttemptCount,
    uint CachePackNonFiniteCount,
    uint CachePackRadianceSaturationCount,
    float CachePackMaximumRadianceError,
    float CachePackMaximumDistanceError,
    uint DirectionComparisonSampleCount,
    uint DirectionEpochMismatchCount,
    float DirectionMaximumAngularErrorRadians,
    IReadOnlyList<uint> DirectionAngularErrorHistogram,
    uint InvalidSourceEpochCount,
    uint InvalidHitKindCount)
{
    public static SimpleDdgiStorageValidationCounters Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0f, 0.0f, 0, 0, 0.0f,
        Array.Empty<uint>(), 0, 0);

    /// <summary>
    /// Conservative upper bound for the histogram bucket containing P99.
    /// The final overflow bucket uses the observed maximum plus half of its
    /// reporting quantization step; pi remains the fail-closed physical bound.
    /// </summary>
    public float DirectionAngularErrorP99UpperBoundRadians
    {
        get
        {
            if (DirectionComparisonSampleCount == 0 ||
                DirectionAngularErrorHistogram.Count == 0)
            {
                return 0.0f;
            }

            ulong target = ((ulong)DirectionComparisonSampleCount * 99UL + 99UL) / 100UL;
            ulong cumulative = 0UL;
            ReadOnlySpan<float> upperBounds =
            [
                1.0e-6f, 5.0e-6f, 1.0e-5f, 2.5e-5f,
                5.0e-5f, 1.0e-4f, 2.5e-4f
            ];
            float overflowUpperBound =
                float.IsFinite(DirectionMaximumAngularErrorRadians) &&
                DirectionMaximumAngularErrorRadians > 0.0f
                    ? MathF.Min(
                        MathF.PI,
                        DirectionMaximumAngularErrorRadians + 0.5e-6f)
                    : MathF.PI;
            for (int index = 0; index < DirectionAngularErrorHistogram.Count; index++)
            {
                cumulative += DirectionAngularErrorHistogram[index];
                if (cumulative < target)
                    continue;
                return index < upperBounds.Length
                    ? upperBounds[index]
                    : overflowUpperBound;
            }

            return MathF.PI;
        }
    }
}

public static class SimpleDdgiStorageModeExtensions
{
    public static bool IsDefined(this SimpleDdgiSourceCacheLayoutMode mode) =>
        mode is SimpleDdgiSourceCacheLayoutMode.FixedRecord or
            SimpleDdgiSourceCacheLayoutMode.Auto or
            SimpleDdgiSourceCacheLayoutMode.HotHeaderConditionalPayload;

    public static SimpleDdgiSourceCacheLayoutMode Sanitize(
        this SimpleDdgiSourceCacheLayoutMode mode) =>
        mode.IsDefined() ? mode : SimpleDdgiSourceCacheLayoutMode.FixedRecord;

    public static bool IsDefined(this SimpleDdgiStoragePackingMode mode) =>
        mode is SimpleDdgiStoragePackingMode.Legacy or
            SimpleDdgiStoragePackingMode.Validate or
            SimpleDdgiStoragePackingMode.Packed;

    public static SimpleDdgiStoragePackingMode Sanitize(
        this SimpleDdgiStoragePackingMode mode) =>
        mode.IsDefined() ? mode : SimpleDdgiStoragePackingMode.Legacy;

    public static bool UsesPackedCache(this SimpleDdgiStoragePackingMode mode) =>
        mode.Sanitize() == SimpleDdgiStoragePackingMode.Packed;

    public static bool UsesDirectionFreeScratch(this SimpleDdgiStoragePackingMode mode) =>
        mode.Sanitize() == SimpleDdgiStoragePackingMode.Packed;

    public static bool IsDefined(this SimpleDdgiSampledAtlasCoverageMode mode) =>
        mode is SimpleDdgiSampledAtlasCoverageMode.Disabled or
            SimpleDdgiSampledAtlasCoverageMode.FullCanonical or
            SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant;

    public static SimpleDdgiSampledAtlasCoverageMode Sanitize(
        this SimpleDdgiSampledAtlasCoverageMode mode) =>
        mode.IsDefined() ? mode : SimpleDdgiSampledAtlasCoverageMode.Disabled;

    public static int WordCount(this SimpleDdgiTransportCacheFormat format) =>
        format switch
        {
            SimpleDdgiTransportCacheFormat.Legacy36 => 9,
            SimpleDdgiTransportCacheFormat.Compact28 => 7,
            SimpleDdgiTransportCacheFormat.Compact24 => 6,
            _ => 0
        };
}
