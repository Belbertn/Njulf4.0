namespace Njulf.Rendering.Data;

/// <summary>
/// Bounded GPU-side material/GI telemetry. Alpha, transport-provenance, and
/// emissive-invocation counts are deterministic sparse estimates (one
/// 64x-weighted sample per hash bucket); error counters are exact because they
/// are expected to remain zero in production content.
/// </summary>
public readonly record struct MaterialGiGpuCounters(
    uint EstimatedAlphaCandidateTestCount,
    uint EstimatedAlphaCandidateRejectCount,
    uint NonFiniteMaterialOrRadianceCount,
    uint ClampedMaterialOrRadianceCount,
    uint AlphaCandidateLimitReachedCount,
    uint EstimatedDetailedTransportHitCount,
    uint EstimatedCompactTransportHitCount,
    uint EstimatedCorrectnessFallbackHitCount,
    uint EstimatedFarFieldTransportHitCount,
    uint EstimatedEmissiveSamplingInvocationCount)
{
    public static MaterialGiGpuCounters Empty { get; } = default;
}
