using System;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public static class SampleSmokeOptionsParser
{
    public static SampleSmokeOptions Parse(string[] args)
    {
        if (args == null)
            throw new ArgumentNullException(nameof(args));

        string? smokeModeEnvironment = Environment.GetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE");
        bool smokeModeSpecified = !string.IsNullOrWhiteSpace(smokeModeEnvironment);
        SampleSmokeMode mode = ParseMode(smokeModeEnvironment, SampleSmokeMode.None);
        int frameCount = ParsePositiveInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES"), 0, "NJULF_RENDERER_SMOKE_FRAMES");
        int sceneReloadCount = ParsePositiveInt(Environment.GetEnvironmentVariable("NJULF_RENDERER_SCENE_RELOAD_COUNT"), 1, "NJULF_RENDERER_SCENE_RELOAD_COUNT");
        SamplePerformanceScenario performanceScenario = ParsePerformanceScenario(Environment.GetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO"));
        string? startupLogPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_STARTUP_LOG"));
        string? healthReportPath = RendererValidationSettings.NormalizeOptionalPath(Environment.GetEnvironmentVariable("NJULF_RENDERER_HEALTH_REPORT"));
        bool forceMissingAssets = ParseBool(Environment.GetEnvironmentVariable("NJULF_RENDERER_FORCE_MISSING_ASSETS"));
        bool failOnValidationMessage = ParseBool(Environment.GetEnvironmentVariable("NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE"));
        bool enableGpuTiming = ParseBool(Environment.GetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING"));

        if (!RendererValidationSettings.TryParseMode(
                Environment.GetEnvironmentVariable("NJULF_RENDERER_VALIDATION"),
                out RendererValidationMode validationMode,
                out string? validationError))
        {
            throw new ArgumentException(validationError);
        }

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string value = ReadValue(args, ref i);
            switch (arg.Split('=', 2)[0])
            {
                case "--smoke-frames":
                    frameCount = ParsePositiveInt(value, 0, "--smoke-frames");
                    break;
                case "--smoke-mode":
                    mode = ParseMode(value, SampleSmokeMode.None);
                    smokeModeSpecified = true;
                    break;
                case "--scene-reloads":
                    sceneReloadCount = ParsePositiveInt(value, 1, "--scene-reloads");
                    break;
                case "--performance-scenario":
                    performanceScenario = ParsePerformanceScenario(value);
                    break;
                case "--health-report":
                    healthReportPath = RequirePath(value, "--health-report");
                    break;
                case "--startup-log":
                    startupLogPath = RequirePath(value, "--startup-log");
                    break;
                case "--validation":
                    if (!RendererValidationSettings.TryParseMode(value, out validationMode, out validationError))
                        throw new ArgumentException(validationError);
                    break;
                case "--force-missing-assets":
                    forceMissingAssets = true;
                    break;
                case "--fail-on-validation-message":
                    failOnValidationMessage = true;
                    break;
                case "--gpu-timing":
                    enableGpuTiming = ParseBool(value);
                    break;
            }
        }

        if (mode == SampleSmokeMode.None && performanceScenario != SamplePerformanceScenario.Normal && !smokeModeSpecified)
            mode = SampleSmokeMode.Startup;
        if (mode == SampleSmokeMode.None && frameCount > 0)
            mode = SampleSmokeMode.Resize;
        if (mode != SampleSmokeMode.None && frameCount <= 0)
            frameCount = mode == SampleSmokeMode.LongRun ? 1000 : 3;

        return new SampleSmokeOptions(
            mode,
            frameCount,
            sceneReloadCount,
            startupLogPath,
            healthReportPath,
            validationMode,
            failOnValidationMessage,
            forceMissingAssets,
            performanceScenario,
            enableGpuTiming);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        string arg = args[index];
        int equals = arg.IndexOf('=');
        if (equals >= 0)
            return arg[(equals + 1)..];

        if (arg is "--force-missing-assets" or "--fail-on-validation-message" or "--gpu-timing")
            return "true";

        if (index + 1 >= args.Length)
            throw new ArgumentException($"{arg} requires a value.");

        index++;
        return args[index];
    }

    private static SampleSmokeMode ParseMode(string? value, SampleSmokeMode defaultMode)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultMode;

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => SampleSmokeMode.None,
            "startup" => SampleSmokeMode.Startup,
            "resize" => SampleSmokeMode.Resize,
            "fullscreen" => SampleSmokeMode.Fullscreen,
            "minimize" => SampleSmokeMode.Minimize,
            "scene-reload" or "scene_reload" or "scenereload" => SampleSmokeMode.SceneReload,
            "missing-assets" or "missing_assets" or "missingassets" => SampleSmokeMode.MissingAssets,
            "long-run" or "long_run" or "longrun" => SampleSmokeMode.LongRun,
            "all" => SampleSmokeMode.All,
            _ => throw new ArgumentException($"Invalid smoke mode '{value}'. Valid values: none, startup, resize, fullscreen, minimize, scene-reload, missing-assets, long-run, all.")
        };
    }

    private static SamplePerformanceScenario ParsePerformanceScenario(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SamplePerformanceScenario.Normal;

        string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty);
        foreach (SamplePerformanceScenario scenario in Enum.GetValues<SamplePerformanceScenario>())
        {
            string scenarioName = scenario.ToString().Replace("-", string.Empty).Replace("_", string.Empty);
            if (scenarioName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return scenario;
        }

        throw new ArgumentException($"Invalid performance scenario '{value}'. Valid values: {string.Join(", ", Enum.GetNames<SamplePerformanceScenario>())}.");
    }

    private static int ParsePositiveInt(string? value, int defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(value, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{name} requires a positive integer value.");
        return parsed;
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

    private static string RequirePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} requires a non-empty path.");

        return System.IO.Path.GetFullPath(value);
    }
}
