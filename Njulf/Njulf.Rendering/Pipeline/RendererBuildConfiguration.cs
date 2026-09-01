using System;

namespace Njulf.Rendering.Pipeline;

internal enum RendererPipelineStartupMode
{
    ActiveScene,
    BlockingActiveScene,
    Exhaustive
}

internal enum RendererStartupWaitTarget
{
    Bootstrap,
    Scene,
    FullQuality
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
    Capture,
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
    internal const string PipelineBinaryAutoCaptureEnvironmentVariable =
        "NJULF_PIPELINE_BINARY_AUTO_CAPTURE";
    internal const string StartupWaitEnvironmentVariable =
        "NJULF_STARTUP_WAIT";
    internal const string PipelineCacheVerifyEnvironmentVariable =
        "NJULF_PIPELINE_CACHE_VERIFY";

    internal static RendererPipelineStartupMode PipelineStartupMode { get; } =
        ResolvePipelineStartupMode();

    internal static RendererStartupWaitTarget StartupWaitTarget { get; } =
        ResolveStartupWaitTarget();

    // Retained as the compatibility name used by the mesh pipeline. Active
    // scene preparation is now the default in every build tier.
    internal static bool FastPipelineStartup =>
        PipelineStartupMode != RendererPipelineStartupMode.Exhaustive;

    internal static bool ProgressivePipelineStartup =>
        PipelineStartupMode == RendererPipelineStartupMode.ActiveScene;

    internal static RendererStartupLatencyGateMode StartupLatencyGateMode { get; } =
        ResolveStartupLatencyGateMode(
            Environment.GetEnvironmentVariable(
                StartupLatencyGateEnvironmentVariable),
            EnforceStartupLatencyByDefault);

    internal static RendererPipelineBinaryCacheMode PipelineBinaryCacheMode
        { get; } = ResolvePipelineBinaryCacheMode(
            ResolveCommandLineValue("--pipeline-binary-cache") ??
            Environment.GetEnvironmentVariable(
                PipelineBinaryCacheEnvironmentVariable));

    internal static bool PipelineBinaryAutoCaptureEnabled { get; } =
        ResolvePipelineBinaryAutoCaptureEnabled(
            Environment.GetEnvironmentVariable(
                PipelineBinaryAutoCaptureEnvironmentVariable));

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

        if (requested.Equals(
                "blocking-active-scene",
                StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("blocking", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineStartupMode.BlockingActiveScene;
        }

        throw new InvalidOperationException(
            $"Unsupported pipeline startup mode '{requested}'. Use " +
            "'active-scene', 'blocking-active-scene', or 'exhaustive'.");
    }

    internal static RendererStartupWaitTarget ResolveStartupWaitTarget(
        string? requested = null)
    {
        requested ??= ResolveCommandLineValue("--startup-wait");
        requested ??= Environment.GetEnvironmentVariable(
            StartupWaitEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupWaitTarget.Bootstrap;
        }
        if (requested.Equals("scene", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("fallback-scene", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupWaitTarget.FullQuality;
        }
        if (requested.Equals("full-quality", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            return RendererStartupWaitTarget.FullQuality;
        }

        throw new InvalidOperationException(
            $"Unsupported startup wait target '{requested}'. Use " +
            "'bootstrap' or 'full-quality' ('scene' is a compatibility alias).");
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
        if (requested.Equals("capture", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("populate", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineBinaryCacheMode.Capture;
        }
        if (requested.Equals("require", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("verify", StringComparison.OrdinalIgnoreCase))
        {
            return RendererPipelineBinaryCacheMode.Require;
        }

        throw new InvalidOperationException(
            $"Unsupported pipeline binary cache mode '{requested}'. Use " +
            "'auto', 'off', 'capture', or 'require'.");
    }

    internal static bool ResolvePipelineBinaryAutoCaptureEnabled(
        string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (requested.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            requested.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Unsupported pipeline binary auto-capture setting " +
            $"'{requested}'. Use 'on' or 'off'.");
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

    private static string? ResolveCommandLineValue(string option)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        string prefix = option + "=";
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument[prefix.Length..];
            if (argument.Equals(option, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length)
            {
                return arguments[index + 1];
            }
        }
        return null;
    }
}
