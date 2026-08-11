namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiNearVisibilityGpuCounters(
    int ReadbackValid,
    ulong FrameSerial,
    uint CoherentClusterTexelCount,
    uint RejectedClusterTexelCount,
    uint InsufficientConfidenceTapCount,
    uint InvalidDepthTapCount,
    uint NoMomentDiscrepancyTapCount,
    uint ReceiverInFrontTapCount,
    uint AppliedEvaluationCount,
    uint EvaluationCount,
    float AverageClamp,
    float MaximumClamp)
{
    public static SimpleDdgiNearVisibilityGpuCounters Unavailable { get; } =
        default;
}

/// <summary>
/// Allocation/admission telemetry for the optional B4 near-occluder sidecar.
/// Canonical visibility bytes are intentionally excluded: admission failure
/// leaves the baseline atlas and accepted volume set untouched.
/// </summary>
public readonly record struct SimpleDdgiNearVisibilityDiagnostics(
    bool Requested,
    bool Active,
    ulong BudgetBytes,
    ulong RequiredBytes,
    ulong PublicBytes,
    ulong PrivateBytes,
    int EligibleVolumeCount,
    string Status)
{
    public SimpleDdgiNearVisibilityGpuCounters Evidence { get; init; } =
        SimpleDdgiNearVisibilityGpuCounters.Unavailable;

    public ulong AllocatedBytes => checked(PublicBytes + PrivateBytes);

    public static SimpleDdgiNearVisibilityDiagnostics Disabled(
        string status = "disabled") =>
        new(false, false, 0UL, 0UL, 0UL, 0UL, 0, status);
}
