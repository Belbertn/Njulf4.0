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
    public uint ExactSsrEligible { get; init; }
    public uint ExactSsrAdmitted { get; init; }
    public uint ExactSsrReservedSamples { get; init; }
    public uint ExactSsrActualSamples { get; init; }
    public uint ExactSsrHits { get; init; }
    public uint ExactSsrBudgetRejected { get; init; }
    public uint ExactRayAdmitted { get; init; }
    public uint ExactRayBudgetRejected { get; init; }

    public static TransparentReflectionGpuCounters Empty { get; } = default;
}
