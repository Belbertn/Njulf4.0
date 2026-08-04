namespace Njulf.Rendering.Data;

/// <summary>
/// Selects the authority for the Simple-DDGI update scheduler.
/// </summary>
/// <remarks>
/// The value is deliberately Simple-DDGI specific.  Legacy DDGI settings and
/// the Simple transport state machine must not share a mode bit: doing so makes
/// a serialized setting ambiguous during a CPU/GPU authority transition.
/// </remarks>
public enum SimpleDdgiSchedulerMode : uint
{
    /// <summary>CPU persistent-queue scheduler and direct dispatch path.</summary>
    CpuReference = 0,

    /// <summary>
    /// GPU classification/admission is compared through delayed validation,
    /// while the CPU reference queue remains the rendering authority.
    /// </summary>
    GpuMirror = 1,

    /// <summary>
    /// GPU state, queue emission, lifecycle commit, and indirect dispatch are
    /// the rendering authority.
    /// </summary>
    GpuResident = 2
}

public static class SimpleDdgiSchedulerModeExtensions
{
    public static bool IsGpuMode(this SimpleDdgiSchedulerMode mode) =>
        mode is SimpleDdgiSchedulerMode.GpuMirror or SimpleDdgiSchedulerMode.GpuResident;

    public static bool IsDefined(this SimpleDdgiSchedulerMode mode) =>
        mode is SimpleDdgiSchedulerMode.CpuReference or
            SimpleDdgiSchedulerMode.GpuMirror or
            SimpleDdgiSchedulerMode.GpuResident;

    public static SimpleDdgiSchedulerMode Sanitize(this SimpleDdgiSchedulerMode mode) =>
        mode.IsDefined() ? mode : SimpleDdgiSchedulerMode.CpuReference;
}
