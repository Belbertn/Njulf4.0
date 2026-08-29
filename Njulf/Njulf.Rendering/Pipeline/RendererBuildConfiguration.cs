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

internal enum RendererPipelineBinaryCacheMode
{
    Auto,
    Off,
    Require
}

internal static class RendererBuildConfiguration
{
    internal const string PipelineStartupModeEnvironmentVariable =
        "NJULF_PIPELINE_STARTUP_MODE";
    internal const string StartupLatencyGateEnvironmentVariable =
        "NJULF_STARTUP_LATENCY_GATE";
    internal const string PipelineBinaryCacheEnvironmentVariable =
        "NJULF_PIPELINE_BINARY_CACHE";
    internal const string PipelineCacheVerifyEnvironmentVariable =
        "NJULF_PIPELINE_CACHE_VERIFY";

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

    internal static RendererPipelineBinaryCacheMode PipelineBinaryCacheMode
        { get; } = ResolvePipelineBinaryCacheMode(
            Environment.GetEnvironmentVariable(
                PipelineBinaryCacheEnvironmentVariable));

    internal static bool VerifyPipelineCacheCompleteness { get; } =
        ResolveBooleanSwitch(
            PipelineCacheVerifyEnvironmentVariable,
            "--pipeline-cache-verify") ||
        PipelineBinaryCacheMode == RendererPipelineBinaryCacheMode.Require;

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

    internal static RendererPipelineBinaryCacheMode
        ResolvePipelineBinaryCacheMode(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineBinaryCacheMode.Auto;
        }
        if (requested.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineBinaryCacheMode.Off;
        }
        if (requested.Equals("require", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("verify", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineBinaryCacheMode.Require;
        }

        throw new InvalidOperationException(
            $"Unsupported pipeline binary cache mode '{requested}'. Use " +
            "'auto', 'off', or 'require'.");
    }

    private static bool ResolveBooleanSwitch(
        string environmentVariable,
        string commandLineSwitch)
    {
        if (Environment.GetCommandLineArgs().Any(argument =>
                argument.Equals(
                    commandLineSwitch,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string? configured = Environment.GetEnvironmentVariable(
            environmentVariable);
        return configured != null &&
               (configured.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                configured.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                configured.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                configured.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
