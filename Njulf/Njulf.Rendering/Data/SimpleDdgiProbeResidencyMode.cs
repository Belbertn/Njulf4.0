namespace Njulf.Rendering.Data;

/// <summary>
/// Selects the payload-addressing authority used by Simple-DDGI.  Virtual probe
/// topology is identical in every mode; only the ownership of large probe
/// payloads changes.
/// </summary>
public enum SimpleDdgiProbeResidencyMode : uint
{
    /// <summary>Every virtual probe owns an identity-mapped payload slot.</summary>
    Dense = 0,

    /// <summary>
    /// Dense rendering remains authoritative while demand and page allocation
    /// are evaluated for qualification.
    /// </summary>
    Shadow = 1,

    /// <summary>
    /// The finest camera-relative ring uses the bounded physical page pool;
    /// authored and coarser rings remain dense.
    /// </summary>
    SparseNearRing = 2
}

public static class SimpleDdgiProbeResidencyModeExtensions
{
    public static bool IsDefined(this SimpleDdgiProbeResidencyMode mode) =>
        mode is SimpleDdgiProbeResidencyMode.Dense or
            SimpleDdgiProbeResidencyMode.Shadow or
            SimpleDdgiProbeResidencyMode.SparseNearRing;

    public static SimpleDdgiProbeResidencyMode Sanitize(
        this SimpleDdgiProbeResidencyMode mode) =>
        mode.IsDefined() ? mode : SimpleDdgiProbeResidencyMode.Dense;

    public static bool CollectsDemand(this SimpleDdgiProbeResidencyMode mode) =>
        mode is SimpleDdgiProbeResidencyMode.Shadow or
            SimpleDdgiProbeResidencyMode.SparseNearRing;

    public static bool UsesSparsePayloads(this SimpleDdgiProbeResidencyMode mode) =>
        mode == SimpleDdgiProbeResidencyMode.SparseNearRing;
}
