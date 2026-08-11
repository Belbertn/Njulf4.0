namespace Njulf.Rendering.Resources;

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
    public ulong AllocatedBytes => checked(PublicBytes + PrivateBytes);

    public static SimpleDdgiNearVisibilityDiagnostics Disabled(
        string status = "disabled") =>
        new(false, false, 0UL, 0UL, 0UL, 0UL, 0, status);
}
