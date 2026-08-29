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

public readonly record struct TransparentReflectionAdmissionThresholds(
    uint Ssr,
    uint Ray);

/// <summary>
/// Converts the prior completed request population into a stable whole-screen
/// hash threshold. A missing population deliberately admits everything for one
/// discovery frame; the exact subgroup allocator remains the hard budget.
/// </summary>
public static class TransparentReflectionAdmissionPolicy
{
    public const double TargetUtilization = 0.95;

    public static TransparentReflectionAdmissionThresholds Resolve(
        int ssrSampleBudget,
        int rayTaskBudget,
        int ssrMaximumSteps,
        in TransparentReflectionGpuCounters previous)
    {
        uint samplesPerTrace = checked((uint)Math.Max(
            8,
            ssrMaximumSteps) * 2u);
        uint ssrCapacity = checked((uint)Math.Max(0, ssrSampleBudget)) /
            samplesPerTrace;
        uint rayCapacity = checked((uint)Math.Max(0, rayTaskBudget));
        return new TransparentReflectionAdmissionThresholds(
            ResolveThreshold(ssrCapacity, previous.ExactSsrEligible),
            ResolveThreshold(rayCapacity, previous.RayRequests));
    }

    public static uint ResolveThreshold(uint capacity, uint requests)
    {
        if (capacity == 0u)
            return 0u;
        if (requests == 0u || capacity >= requests)
            return uint.MaxValue;
        double probability = Math.Min(
            1.0,
            capacity * TargetUtilization / requests);
        return Math.Max(
            1u,
            checked((uint)Math.Floor(probability * uint.MaxValue)));
    }
}
