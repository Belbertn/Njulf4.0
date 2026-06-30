using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public static class SampleGlobalIlluminationValidation
{
    public static IReadOnlyList<SampleGiValidationScene> Phase10DeterministicScenes { get; } =
    [
        new(
            "ddgi-open-sky-ground",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Open sky box with diffuse ground",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "ddgi-thin-wall-corridor",
            SamplePerformanceScenario.GiLongCorridorOcclusion,
            "Thin-wall corridor with sunlight at one end",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "ddgi-sponza-courtyard",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Sponza-like courtyard with sunlit upper wall and shadowed lower arcade",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "ddgi-local-volume-room",
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            "Local dense volume inside a small room",
            RequiresLocalDenseVolume: true,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "ddgi-camera-relative-scroll",
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            "Camera-relative scrolling test",
            RequiresLocalDenseVolume: true,
            RequiresCameraRelativeScroll: true,
            RequiresCameraCut: false),
        new(
            "ddgi-teleport-cut",
            SamplePerformanceScenario.GiFastTraversalTeleport,
            "Teleport/camera-cut test",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: true,
            RequiresCameraCut: true)
    ];

    public static IReadOnlyList<SampleGiValidationMetric> Phase10Metrics { get; } =
    [
        new("mean-shadowed-indirect-luminance", "luminance", "Mean indirect luminance sampled from stable shadowed regions."),
        new("mean-sunlit-indirect-luminance", "luminance", "Mean indirect luminance sampled from stable sunlit regions."),
        new("coverage-mean", "ratio", "Mean DDGI coverage over the measured image mask."),
        new("visible-support-mean", "ratio", "Mean visible-support confidence over covered pixels."),
        new("effective-weight-mean", "ratio", "Mean final DDGI contribution weight after coverage, visibility, and suppression."),
        new("zero-visible-covered-fraction", "ratio", "Covered pixels whose visibility support collapsed to zero."),
        new("scheduler-p95", "microseconds", "CPU or GPU scheduler P95 selected by the active scheduler mode."),
        new("ddgi-gpu-p95", "milliseconds", "P95 of the split DDGI GPU update passes."),
        new("ddgi-memory", "bytes", "DDGI texture, atlas, scheduler, and staging memory."),
        new("warmup-frame-count", "frames", "Frames required to reach steady-state cache warmup.")
    ];

    public static IReadOnlyList<SampleGiGoldenDebugBuffer> Phase10GoldenDebugBuffers { get; } =
    [
        new("final-color", GlobalIlluminationDebugView.None, RelativeLuminanceTolerance: 0.04f, AbsoluteTolerance: 0.005f),
        new("ddgi-raw-diffuse", GlobalIlluminationDebugView.DdgiRawDiffuse, RelativeLuminanceTolerance: 0.05f, AbsoluteTolerance: 0.006f),
        new("ddgi-effective-weight", GlobalIlluminationDebugView.DdgiEffectiveWeight, RelativeLuminanceTolerance: 0.03f, AbsoluteTolerance: 0.004f),
        new("ddgi-coverage", GlobalIlluminationDebugView.DdgiCoverage, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f),
        new("ddgi-visibility", GlobalIlluminationDebugView.DdgiVisibilityMoments, RelativeLuminanceTolerance: 0.04f, AbsoluteTolerance: 0.005f),
        new("ddgi-suppression-mask", GlobalIlluminationDebugView.DdgiSuppressionMask, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f)
    ];

    public static SampleGiSchedulerEquivalenceContract Phase10SchedulerEquivalence { get; } = new(
        MaxRequestCountDelta: 0,
        MaxInvalidProbeCount: 0,
        MaxDuplicateRequestCount: 0,
        MaxPriorityBucketDelta: 1,
        MaxPerVolumeDistributionDelta: 1,
        MaxCoverageMeanDelta: 0.01f);

    public static IReadOnlyList<SampleGiValidationPath> DeterministicPaths { get; } =
    [
        new("sponza-right-wall-stationary", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("stationary-convergence", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("slow-pan", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("fast-pan", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("translation", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("camera-cut", IncludesCameraCut: true, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("fov-change", IncludesCameraCut: false, IncludesFovChange: true, IncludesMovingObjects: false, IncludesMovingLights: false),
        new("moving-rigid-and-skinned", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: true, IncludesMovingLights: false),
        new("moving-light", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: true),
        new("thin-wall-silhouette", IncludesCameraCut: false, IncludesFovChange: false, IncludesMovingObjects: false, IncludesMovingLights: false)
    ];

    public static IReadOnlyList<SampleGiValidationGate> Gates { get; } =
    [
        new("history-rejection-ratio", Maximum: 0.35f, Unit: "ratio"),
        new("stable-temporal-luma-error", Maximum: 0.05f, Unit: "relative-luma"),
        new("right-wall-relative-luma-stddev", Maximum: 0.02f, Unit: "relative-luma"),
        new("disocclusion-recovery-frames", Maximum: 6.0f, Unit: "frames"),
        new("thin-wall-leakage", Maximum: 0.03f, Unit: "relative-luma"),
        new("cornell-room-leakage", Maximum: 0.03f, Unit: "relative-luma"),
        new("bright-exterior-room-leakage", Maximum: 0.04f, Unit: "relative-luma"),
        new("room-clipmap-transition-seam", Maximum: 0.04f, Unit: "relative-luma"),
        new("nan-inf-hdr-outliers", Maximum: 0.0f, Unit: "pixels"),
        new("ddgi-coverage-debug-contamination", Maximum: 0.0f, Unit: "pixels"),
        new("ddgi-gpu-scheduler-invalid-requests", Maximum: 0.0f, Unit: "requests"),
        new("ddgi-gpu-scheduler-duplicate-requests", Maximum: 0.0f, Unit: "requests"),
        new("ddgi-gpu-scheduler-fallback-active", Maximum: 0.0f, Unit: "boolean"),
        new("ddgi-gpu-mode-cpu-scheduler-us", Maximum: 300.0f, Unit: "microseconds"),
        new("ssgi-trace-gpu-us", Maximum: 2200.0f, Unit: "microseconds"),
        new("ssgi-temporal-gpu-us", Maximum: 900.0f, Unit: "microseconds"),
        new("ssgi-spatial-gpu-us", Maximum: 1800.0f, Unit: "microseconds")
    ];

    public static bool IsValidationScenario(SamplePerformanceScenario scenario)
    {
        return scenario is SamplePerformanceScenario.GiSponzaRightWallStationary
            or SamplePerformanceScenario.GiCornellRoom
            or SamplePerformanceScenario.GiThinWallLeakTest
            or SamplePerformanceScenario.GiMovingPointLight
            or SamplePerformanceScenario.GiMovingRigidObject
            or SamplePerformanceScenario.GiBrightExteriorRoom
            or SamplePerformanceScenario.GiLongCorridorOcclusion
            or SamplePerformanceScenario.GiEmissiveMaterialRoom
            or SamplePerformanceScenario.GiLocalVolumeStreaming
            or SamplePerformanceScenario.GiFastTraversalTeleport
            or SamplePerformanceScenario.ForestFoliage;
    }

    public static void ConfigureRenderSettings(RenderSettings settings, SamplePerformanceScenario scenario)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!IsValidationScenario(scenario))
            return;

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.DynamicResolution.MinimumScale = 1.0f;
        settings.DynamicResolution.MaximumScale = 1.0f;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 1.0f;
        settings.Bloom.Enabled = false;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = false;
        settings.AmbientOcclusion.Enabled = true;
        settings.Shadows.PointShadowMapSize = 1024;
        settings.Shadows.PointNormalBias = 0.008f;
        settings.Shadows.PointConstantDepthBias = 0.0003f;
        settings.Shadows.PointPcfRadius = 1;

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = false;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 1.5f;
        gi.EnvironmentFallbackIntensity = 0.65f;
        gi.MaxBounceDistance = 10.0f;
        gi.DdgiThinWallPolicyEnabled = true;
        gi.DdgiRoomSpacingScaledBiasEnabled = true;
        gi.DdgiThinWallLeakClampStrength = 0.9f;
        gi.DdgiThinWallProxyThickness = 0.12f;
        gi.TemporalEnabled = false;
        gi.DenoiserEnabled = false;
    }

    public static void ConfigureSchedulerMode(RenderSettings settings, DdgiSchedulerMode? schedulerMode)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!schedulerMode.HasValue)
            return;

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.DdgiSchedulerMode = schedulerMode.Value;
        if (schedulerMode.Value == DdgiSchedulerMode.CpuGpuCompare)
            gi.DdgiGpuSchedulerReadbackValidationEnabled = true;
    }
}

public sealed record SampleGiValidationPath(
    string Name,
    bool IncludesCameraCut,
    bool IncludesFovChange,
    bool IncludesMovingObjects,
    bool IncludesMovingLights);

public sealed record SampleGiValidationGate(
    string Metric,
    float Maximum,
    string Unit);

public sealed record SampleGiValidationScene(
    string Name,
    SamplePerformanceScenario Scenario,
    string Coverage,
    bool RequiresLocalDenseVolume,
    bool RequiresCameraRelativeScroll,
    bool RequiresCameraCut);

public sealed record SampleGiValidationMetric(
    string Name,
    string Unit,
    string Description);

public sealed record SampleGiGoldenDebugBuffer(
    string Name,
    GlobalIlluminationDebugView DebugView,
    float RelativeLuminanceTolerance,
    float AbsoluteTolerance);

public sealed record SampleGiSchedulerEquivalenceContract(
    int MaxRequestCountDelta,
    uint MaxInvalidProbeCount,
    uint MaxDuplicateRequestCount,
    uint MaxPriorityBucketDelta,
    uint MaxPerVolumeDistributionDelta,
    float MaxCoverageMeanDelta);
