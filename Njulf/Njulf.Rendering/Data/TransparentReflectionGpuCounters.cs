namespace Njulf.Rendering.Data;

/// <summary>
/// Sparse transparent-reflection source telemetry. RayRequests is also the
/// exact frame-local admission counter; other values are 8x8 weighted
/// estimates to avoid one diagnostic atomic per transparent fragment.
/// </summary>
public readonly record struct TransparentReflectionGpuCounters(
    uint RayRequests,
    uint EstimatedSsrHits,
    uint EstimatedRayHits,
    uint EstimatedRayMisses,
    uint EstimatedBudgetRejected,
    uint EstimatedDdgiFallbacks,
    uint EstimatedProbeFallbacks,
    uint EstimatedEnvironmentFallbacks)
{
    public static TransparentReflectionGpuCounters Empty { get; } = default;
}
