using System;

namespace Njulf.Rendering.Diagnostics;

public sealed record RendererValidationSettings(
    RendererValidationMode Mode,
    bool FailOnErrorMessage,
    bool EnableBestPractices,
    bool EnableVerboseMessages,
    string? StartupLogPath,
    string? HealthReportPath)
{
    public bool EnableValidation => Mode != RendererValidationMode.Off;
    public bool EnableGpuAssisted => Mode is RendererValidationMode.GpuAssisted or RendererValidationMode.All;
    public bool EnableSynchronization => Mode is RendererValidationMode.Synchronization or RendererValidationMode.All;
    public RendererValidationFeatureSelection FeatureSelection => EnableValidation
        ? new RendererValidationFeatureSelection(
            GpuAssisted: EnableGpuAssisted,
            ReserveGpuAssistedBindingSlot: EnableGpuAssisted,
            SynchronizationValidation: EnableSynchronization,
            BestPractices: EnableBestPractices)
        : RendererValidationFeatureSelection.None;

    public static RendererValidationSettings Default { get; } = new(
#if DEBUG
        RendererValidationMode.Standard,
#else
        RendererValidationMode.Off,
#endif
        FailOnErrorMessage: false,
        EnableBestPractices: false,
        EnableVerboseMessages: false,
        StartupLogPath: null,
        HealthReportPath: null);

    public static RendererValidationSettings FromEnvironment()
    {
        RendererValidationMode mode = TryParseMode(
            Environment.GetEnvironmentVariable("NJULF_RENDERER_VALIDATION"),
            out RendererValidationMode parsedMode,
            out string? error)
            ? parsedMode
            : throw new ArgumentException(error);

        return Default with
        {
            Mode = mode,
            FailOnErrorMessage = ParseBool(Environment.GetEnvironmentVariable("NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE")),
            StartupLogPath = NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_STARTUP_LOG")),
            HealthReportPath = NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_HEALTH_REPORT"))
        };
    }

    public static bool TryParseMode(string? value, out RendererValidationMode mode, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = Default.Mode;
            error = null;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = RendererValidationMode.Off;
                error = null;
                return true;
            case "standard":
                mode = RendererValidationMode.Standard;
                error = null;
                return true;
            case "gpu":
            case "gpu-assisted":
            case "gpuassisted":
                mode = RendererValidationMode.GpuAssisted;
                error = null;
                return true;
            case "sync":
            case "synchronization":
                mode = RendererValidationMode.Synchronization;
                error = null;
                return true;
            case "all":
                mode = RendererValidationMode.All;
                error = null;
                return true;
            default:
                mode = RendererValidationMode.Off;
                error = $"Invalid renderer validation mode '{value}'. Valid values: off, standard, gpu, sync, all.";
                return false;
        }
    }

    public static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Vulkan validation-layer features that must be attached to
/// <c>VkInstanceCreateInfo::pNext</c>. Keeping this selection independent of
/// Silk.NET makes the command-line modes straightforward to test.
/// </summary>
public readonly record struct RendererValidationFeatureSelection(
    bool GpuAssisted,
    bool ReserveGpuAssistedBindingSlot,
    bool SynchronizationValidation,
    bool BestPractices)
{
    public static RendererValidationFeatureSelection None { get; } = new(
        GpuAssisted: false,
        ReserveGpuAssistedBindingSlot: false,
        SynchronizationValidation: false,
        BestPractices: false);

    public int EnabledFeatureCount =>
        (GpuAssisted ? 1 : 0) +
        (ReserveGpuAssistedBindingSlot ? 1 : 0) +
        (SynchronizationValidation ? 1 : 0) +
        (BestPractices ? 1 : 0);
}
