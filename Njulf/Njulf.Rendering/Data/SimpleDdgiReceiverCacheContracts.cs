namespace Njulf.Rendering.Data;

/// <summary>
/// Requested Simple-DDGI receiver path. Presets may select the surface-aware
/// temporal path; exact remains the correctness oracle and fail-closed
/// fallback. The legacy value is restricted to controlled benchmark runs.
/// </summary>
public enum SimpleDdgiReceiverCacheMode : uint
{
    Exact = 0,
    LegacyDepthOnlyBenchmark = 1,
    SurfaceAwareSpatial = 2,
    TemporalAdaptive = 3
}

/// <summary>Stable reason taxonomy for an exact receiver-cache fallback.</summary>
public enum SimpleDdgiReceiverCacheFallbackReason : uint
{
    None = 0,
    ExactRequested = 1,
    DetailedBuildUsesExact = 2,
    PipelineUnavailable = 3,
    ResourceUnavailable = 4,
    DescriptorUnavailable = 5,
    DispatchUnavailable = 6,
    DdgiInactive = 7,
    NoOpaqueReceivers = 8,
    ReflectionCapture = 9,
    MaterialTransportProvenance = 10,
    DebugViewActive = 11,
    InvalidEnvironmentFallback = 12,
    DirectionalReceiverUnavailable = 13,
    AdvancedOutputRequiresExact = 14,
    FeedbackVariantRequiresExact = 15,
    FrameGenerationMismatch = 16,
    ExtentMismatch = 17,
    TemporalAdaptiveUnavailable = 18,
    LegacyBenchmarkUnavailable = 19,
    InvalidConfiguration = 20,
    BentNormalLightingRequiresExact = 21
}

/// <summary>
/// Fence-independent identity and optional fence-complete counter evidence for
/// the receiver cache. Zero counters with <see cref="CounterReadbackValid"/>
/// false mean unavailable, never a measured zero.
/// </summary>
public readonly record struct SimpleDdgiReceiverCacheDiagnostics(
    SimpleDdgiReceiverCacheMode RequestedMode,
    SimpleDdgiReceiverCacheMode EffectiveMode,
    SimpleDdgiReceiverCacheFallbackReason FallbackReason,
    string FallbackDetail,
    uint SurfaceAbiVersion,
    float MaximumRelativeDepthDifference,
    float MinimumNormalDot,
    float MinimumWorldTolerance,
    float MinimumPlaneTolerance,
    ulong RadianceBytes,
    ulong SurfaceSidecarBytes,
    string PipelineArtifact,
    int CounterReadbackValid,
    ulong ResolveCandidateCount,
    ulong ResolveValidCount,
    ulong ResolveInvalidOrNonFiniteRejectCount,
    ulong ResolveDepthOrPositionRejectCount,
    ulong ResolvePlaneRejectCount,
    ulong ResolveNormalRejectCount,
    ulong ResolveInsufficientSupportRejectCount,
    ulong ForwardCandidateCount,
    ulong ForwardAcceptedCount,
    ulong ForwardInvalidOrNonFiniteRejectCount,
    ulong ForwardDepthOrPositionRejectCount,
    ulong ForwardPlaneRejectCount,
    ulong ForwardNormalRejectCount,
    ulong ForwardInsufficientSupportRejectCount,
    ulong ExactFallbackFragmentCount,
    ulong LegacyFragmentCount,
    int ExactDualEvaluationEnabled,
    uint AdaptiveAbiVersion = 0u,
    int AdaptiveHistoryValid = 0,
    uint AdaptiveResourceGeneration = 0u,
    ulong AdaptiveResourceBytes = 0UL,
    int AdaptiveCounterReadbackValid = 0,
    uint AdaptiveGatherWorkCount = 0u,
    uint AdaptiveMissingFeedbackWorkCount = 0u,
    uint AdaptiveResolveTileCount = 0u,
    uint AdaptiveOverflowFlags = 0u,
    uint AdaptiveAcceptedEntryCount = 0u,
    uint AdaptiveRejectedEntryCount = 0u,
    uint AdaptiveFullTileCount = 0u,
    uint AdaptiveHalfTileCount = 0u,
    uint AdaptiveQuarterTileCount = 0u,
    uint AdaptiveReuseTileCount = 0u,
    uint PublicationGeneration = 0u,
    ulong PublicationStableIdentityHitCount = 0UL,
    ulong PublicationDirtyIdentityCount = 0UL,
    ulong PublicationWrapResetCount = 0UL,
    uint AdaptivePublicationGenerationHitCount = 0u,
    uint AdaptivePublicationDirtyInvalidationCount = 0u,
    uint AdaptivePublicationSkippedTileCount = 0u,
    ulong DirectionalCacheEvaluationCount = 0UL,
    ulong LifetimeObservedFrameCount = 0UL,
    ulong LifetimeResolveCandidateCount = 0UL,
    ulong LifetimeResolveValidCount = 0UL,
    ulong LifetimeForwardCandidateCount = 0UL,
    ulong LifetimeForwardAcceptedCount = 0UL,
    ulong LifetimeExactFallbackFragmentCount = 0UL,
    ulong LifetimeDirectionalCacheEvaluationCount = 0UL,
    ulong LifetimeLegacyFragmentCount = 0UL)
{
    public static SimpleDdgiReceiverCacheDiagnostics Exact(
        SimpleDdgiReceiverCacheMode requestedMode,
        SimpleDdgiReceiverCacheFallbackReason reason,
        string detail,
        ulong radianceBytes = 0,
        ulong surfaceSidecarBytes = 0) => new(
            requestedMode,
            SimpleDdgiReceiverCacheMode.Exact,
            reason,
            detail ?? string.Empty,
            SimpleDdgiReceiverSurfaceAbi.Version,
            SimpleDdgiReceiverSurfaceAbi.MaximumRelativeDepthDifference,
            SimpleDdgiReceiverSurfaceAbi.MinimumNormalDot,
            SimpleDdgiReceiverSurfaceAbi.MinimumWorldTolerance,
            SimpleDdgiReceiverSurfaceAbi.MinimumPlaneTolerance,
            radianceBytes,
            surfaceSidecarBytes,
            "forward-exact-ddgi",
            0,
            0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0,
            0, 0, 0);

    public static SimpleDdgiReceiverCacheDiagnostics Active(
        SimpleDdgiReceiverCacheMode requestedMode,
        SimpleDdgiReceiverCacheMode effectiveMode,
        SimpleDdgiReceiverCacheFallbackReason reason,
        string detail,
        ulong radianceBytes,
        ulong surfaceSidecarBytes,
        string pipelineArtifact) => new(
            requestedMode,
            effectiveMode,
            reason,
            detail ?? string.Empty,
            SimpleDdgiReceiverSurfaceAbi.Version,
            SimpleDdgiReceiverSurfaceAbi.MaximumRelativeDepthDifference,
            SimpleDdgiReceiverSurfaceAbi.MinimumNormalDot,
            SimpleDdgiReceiverSurfaceAbi.MinimumWorldTolerance,
            SimpleDdgiReceiverSurfaceAbi.MinimumPlaneTolerance,
            radianceBytes,
            surfaceSidecarBytes,
            pipelineArtifact ?? string.Empty,
            0,
            0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0,
            0, 0, 0);

    public double AcceptedPercentage =>
        CounterReadbackValid != 0 && ForwardCandidateCount != 0
            ? ForwardAcceptedCount * 100.0 / ForwardCandidateCount
            : 0.0;

    public double ExactFallbackPercentage =>
        CounterReadbackValid != 0 && ForwardCandidateCount != 0
            ? ExactFallbackFragmentCount * 100.0 / ForwardCandidateCount
            : 0.0;

    public bool TimingEligible =>
        ExactDualEvaluationEnabled == 0 && CounterReadbackValid == 0;
}

public static class SimpleDdgiReceiverCachePolicy
{
    public static SimpleDdgiReceiverCacheMode Sanitize(
        this SimpleDdgiReceiverCacheMode mode) => mode is
            SimpleDdgiReceiverCacheMode.Exact or
            SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark or
            SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial or
            SimpleDdgiReceiverCacheMode.TemporalAdaptive
                ? mode
                : SimpleDdgiReceiverCacheMode.Exact;

    public static SimpleDdgiReceiverCacheMode ResolveRequestedMode(
        SimpleDdgiReceiverCacheMode configuredMode,
        bool forceLegacyBenchmark,
        bool forceExact)
    {
        // The oracle always wins if capture controls are accidentally combined.
        if (forceExact)
            return SimpleDdgiReceiverCacheMode.Exact;
        if (forceLegacyBenchmark)
            return SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark;
        return configuredMode.Sanitize();
    }

    public static bool UsesCache(this SimpleDdgiReceiverCacheMode mode) =>
        mode is SimpleDdgiReceiverCacheMode.LegacyDepthOnlyBenchmark or
            SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial or
            SimpleDdgiReceiverCacheMode.TemporalAdaptive;

    /// <summary>
    /// True when the cache ABI preserves the compact directional-L2 receiver
    /// payload. The legacy depth-only benchmark intentionally carries only
    /// scalar irradiance and therefore cannot replace a directional gather.
    /// </summary>
    public static bool CarriesDirectionalRadiancePayload(
        this SimpleDdgiReceiverCacheMode mode) =>
        mode is SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial or
            SimpleDdgiReceiverCacheMode.TemporalAdaptive;
}

public readonly record struct SimpleDdgiReceiverCacheGpuCounters(
    int ReadbackValid,
    ulong ResolveCandidateCount,
    ulong ResolveValidCount,
    ulong ResolveInvalidOrNonFiniteRejectCount,
    ulong ResolveDepthOrPositionRejectCount,
    ulong ResolvePlaneRejectCount,
    ulong ResolveNormalRejectCount,
    ulong ResolveInsufficientSupportRejectCount,
    ulong ForwardCandidateCount,
    ulong ForwardAcceptedCount,
    ulong ForwardInvalidOrNonFiniteRejectCount,
    ulong ForwardDepthOrPositionRejectCount,
    ulong ForwardPlaneRejectCount,
    ulong ForwardNormalRejectCount,
    ulong ForwardInsufficientSupportRejectCount,
    ulong ExactFallbackFragmentCount,
    ulong LegacyFragmentCount,
    ulong DirectionalCacheEvaluationCount = 0UL)
{
    public static SimpleDdgiReceiverCacheGpuCounters Unavailable => default;
}

internal readonly record struct SimpleDdgiReceiverCacheLifetimeCounters(
    ulong ObservedFrameCount,
    ulong ResolveCandidateCount,
    ulong ResolveValidCount,
    ulong ForwardCandidateCount,
    ulong ForwardAcceptedCount,
    ulong ExactFallbackFragmentCount,
    ulong DirectionalCacheEvaluationCount,
    ulong LegacyFragmentCount);

internal sealed class SimpleDdgiReceiverCacheLifetimeAccumulator
{
    internal SimpleDdgiReceiverCacheLifetimeCounters Snapshot { get; private set; }

    internal void Observe(in SimpleDdgiReceiverCacheGpuCounters counters)
    {
        if (counters.ReadbackValid == 0)
            return;

        SimpleDdgiReceiverCacheLifetimeCounters current = Snapshot;
        Snapshot = new SimpleDdgiReceiverCacheLifetimeCounters(
            SaturatingAdd(current.ObservedFrameCount, 1UL),
            SaturatingAdd(
                current.ResolveCandidateCount,
                counters.ResolveCandidateCount),
            SaturatingAdd(
                current.ResolveValidCount,
                counters.ResolveValidCount),
            SaturatingAdd(
                current.ForwardCandidateCount,
                counters.ForwardCandidateCount),
            SaturatingAdd(
                current.ForwardAcceptedCount,
                counters.ForwardAcceptedCount),
            SaturatingAdd(
                current.ExactFallbackFragmentCount,
                counters.ExactFallbackFragmentCount),
            SaturatingAdd(
                current.DirectionalCacheEvaluationCount,
                counters.DirectionalCacheEvaluationCount),
            SaturatingAdd(
                current.LegacyFragmentCount,
                counters.LegacyFragmentCount));
    }

    private static ulong SaturatingAdd(ulong current, ulong value) =>
        ulong.MaxValue - current < value
            ? ulong.MaxValue
            : current + value;
}
