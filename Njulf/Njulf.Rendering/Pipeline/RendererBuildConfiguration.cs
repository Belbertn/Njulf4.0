using System;

namespace Njulf.Rendering.Pipeline;

internal enum RendererPipelineStartupMode
{
    ActiveScene,
    Exhaustive
}

internal enum RendererStartupLatencyGateMode
{
    Disabled,
    TimingOnly,
    Enforce
}

internal static class RendererBuildConfiguration
{
    internal const string PipelineStartupModeEnvironmentVariable =
        "NJULF_PIPELINE_STARTUP_MODE";
    internal const string StartupLatencyGateEnvironmentVariable =
        "NJULF_STARTUP_LATENCY_GATE";

    internal static RendererPipelineStartupMode PipelineStartupMode { get; } =
        ResolvePipelineStartupMode();

    // Retained as the compatibility name used by the mesh pipeline. Active
    // scene preparation is now the default in every build tier.
    internal static bool FastPipelineStartup =>
        PipelineStartupMode == RendererPipelineStartupMode.ActiveScene;

    internal static RendererStartupLatencyGateMode StartupLatencyGateMode { get; } =
        ResolveStartupLatencyGateMode(
            Environment.GetEnvironmentVariable(
                StartupLatencyGateEnvironmentVariable),
            EnforceStartupLatencyByDefault);

    // Interactive launches must remain usable under transient system load.
    // Hardware qualification opts into fatal enforcement explicitly.
    internal static bool EnforceStartupLatencyByDefault => false;

    private static RendererPipelineStartupMode ResolvePipelineStartupMode()
    {
        string? requested = null;
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            const string prefix = "--pipeline-startup=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                requested = argument[prefix.Length..];
            else if (argument.Equals(
                         "--pipeline-startup",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < arguments.Length)
                requested = arguments[++index];
        }

        requested ??= Environment.GetEnvironmentVariable(
            PipelineStartupModeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("active-scene", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("active", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("demand", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineStartupMode.ActiveScene;
        }

        if (requested.Equals("exhaustive", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineStartupMode.Exhaustive;
        }

        throw new InvalidOperationException(
            $"Unsupported pipeline startup mode '{requested}'. Use " +
            "'active-scene' or 'exhaustive'.");
    }

    internal static RendererStartupLatencyGateMode ResolveStartupLatencyGateMode(
        string? requested,
        bool enforceByDefault)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return enforceByDefault
                ? RendererStartupLatencyGateMode.Enforce
                : RendererStartupLatencyGateMode.TimingOnly;
        }

        if (requested.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupLatencyGateMode.Disabled;
        }

        if (requested.Equals("timing", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("timing-only", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupLatencyGateMode.TimingOnly;
        }

        if (requested.Equals("enforce", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupLatencyGateMode.Enforce;
        }

        throw new InvalidOperationException(
            $"Unsupported startup latency gate mode '{requested}'. Use " +
            "'off', 'timing', or 'enforce'.");
    }
}
