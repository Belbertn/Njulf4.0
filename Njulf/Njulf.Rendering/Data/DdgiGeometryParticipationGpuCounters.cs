namespace Njulf.Rendering.Data;

/// <summary>Completed GPU telemetry for DDGI ray-scene material semantics.</summary>
public readonly record struct DdgiGeometryParticipationGpuCounters(
    uint TransparentVisibilityLayerCount,
    uint TransparentVisibilityLimitCount,
    uint DecalCandidateCount,
    uint DecalRetainedCount,
    uint DecalAssociatedCount,
    uint DecalDepthRejectCount,
    uint DecalFacingRejectCount,
    uint DecalCandidateLimitCount,
    uint FoliageProxyHitCount,
    uint InvalidRayMetadataCount,
    uint StochasticAlphaAcceptCount,
    uint StochasticAlphaRejectCount);
