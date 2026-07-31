using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public static class SampleGlobalIlluminationValidation
{
    public const float SimpleDdgiFurnaceAlbedo = 0.5f;
    public const float SimpleDdgiFurnaceEmittedRadiance = 0.25f;
    public const float SimpleDdgiFurnaceExpectedIrradianceLuminance =
        MathF.PI * SimpleDdgiFurnaceEmittedRadiance / (1.0f - SimpleDdgiFurnaceAlbedo);

    public static IReadOnlyList<SampleGiProductionScene> Phase7ProductionScenes { get; } =
    [
        new("simple-ddgi-furnace", SamplePerformanceScenario.GiSimpleDdgiFurnace, "Closed furnace energy conservation", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("sponza-interior", SamplePerformanceScenario.GiSponzaRightWallStationary, "Sponza interior bounce and support coverage", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("sunlit-courtyard", SamplePerformanceScenario.GiSponzaRightWallStationary, "Sunlit courtyard direct-plus-DDGI energy balance", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("colored-bounce-room", SamplePerformanceScenario.GiCornellRoom, "Enclosed colored-bounce room without hidden intensity compensation", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("thin-wall-corridor", SamplePerformanceScenario.GiLongCorridorOcclusion, "Thin-wall corridor leak and visibility validation", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("emissive-room", SamplePerformanceScenario.GiEmissiveMaterialRoom, "Emissive material bounce convergence", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("moving-rigid-object", SamplePerformanceScenario.GiMovingRigidObject, "Moving rigid object invalidation and recovery", RequiresDynamicActor: true, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("moving-local-light", SamplePerformanceScenario.GiMovingPointLight, "Moving local light convergence", RequiresDynamicActor: false, RequiresDynamicLight: true, RequiresCameraTeleport: false),
        new("camera-teleport-scroll", SamplePerformanceScenario.GiFastTraversalTeleport, "Camera-relative teleport and clipmap scroll recovery", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: true),
        new("verticality-rings", SamplePerformanceScenario.GiVerticalityRings, "Rings-only tall-world vertical coverage and recenter stability", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false),
        new("outdoor-foliage-plaza", SamplePerformanceScenario.ForestFoliage, "Outdoor foliage/plaza DDGI fallback and receiving path", RequiresDynamicActor: false, RequiresDynamicLight: false, RequiresCameraTeleport: false)
    ];

    public static IReadOnlyList<SampleGiPerformanceTarget> Phase8PerformanceTargets { get; } =
    [
        new(DdgiQualityTier.DdgiLow, SampleDdgiProductionGate.DdgiLowUpdateP95BudgetMilliseconds, 64UL * 1024UL * 1024UL, ReferenceTier: false),
        new(DdgiQualityTier.DdgiMedium, SampleDdgiProductionGate.DdgiMediumUpdateP95BudgetMilliseconds, 128UL * 1024UL * 1024UL, ReferenceTier: false),
        new(DdgiQualityTier.DdgiHigh, SampleDdgiProductionGate.DdgiHighUpdateP95BudgetMilliseconds, 192UL * 1024UL * 1024UL, ReferenceTier: false),
        new(DdgiQualityTier.DdgiUltra, SampleDdgiProductionGate.DdgiUltraUpdateP95BudgetMilliseconds, 384UL * 1024UL * 1024UL, ReferenceTier: true)
    ];

    public static IReadOnlyList<SampleGiAccuracyOracle> AccuracyOracles { get; } =
    [
        new(
            "simple-ddgi-furnace",
            SamplePerformanceScenario.GiSimpleDdgiFurnace,
            "Closed diffuse room with uniform steady-state irradiance; sampled irradiance luminance must remain within 5 percent of the recorded analytic/reference constant.",
            Metric: "SimpleDdgiAverageSampledIrradianceLuminance",
            ReferenceValue: SimpleDdgiFurnaceExpectedIrradianceLuminance,
            MaximumRelativeError: 0.05f,
            MaximumLatencyFrames: null),
        new(
            "simple-ddgi-light-toggle",
            SamplePerformanceScenario.GiMovingPointLight,
            "Point-light toggle/move response; sampled luminance must reach 90 percent of the new steady state within 0.25 seconds at 60 fps.",
            Metric: "SimpleDdgiAverageSampledIrradianceLuminance",
            ReferenceValue: null,
            MaximumRelativeError: 0.05f,
            MaximumLatencyFrames: 15),
        new(
            "simple-ddgi-cornell-reference",
            SamplePerformanceScenario.GiCornellRoom,
            "Static Cornell reference; sampled luminance must remain within 2 percent of the phase-0 table after later feature phases.",
            Metric: "SimpleDdgiAverageSampledIrradianceLuminance",
            ReferenceValue: null,
            MaximumRelativeError: 0.02f,
            MaximumLatencyFrames: null),
        new(
            "simple-ddgi-emissive-panel",
            SamplePerformanceScenario.GiEmissiveMaterialRoom,
            "Warm/cool emissive panels; both uploaded emissive sources must affect sampled irradiance and panel toggles must meet the light-toggle latency gate.",
            Metric: "DdgiSimpleTraceEmissiveHitCount",
            ReferenceValue: null,
            MaximumRelativeError: 0.10f,
            MaximumLatencyFrames: 15),
        new(
            "simple-ddgi-moving-occluder",
            SamplePerformanceScenario.GiMovingRigidObject,
            "Moving rigid box; indirect shadowing must follow TLAS transform updates without residual old-position ghosting after convergence.",
            Metric: "SimpleDdgiAverageVisibility",
            ReferenceValue: null,
            MaximumRelativeError: 0.05f,
            MaximumLatencyFrames: 18)
    ];

    public static IReadOnlyList<SampleGiValidationMetric> Phase9RegressionMetrics { get; } =
    [
        new("mean-shadowed-indirect-luminance", "luminance", "Mean indirect luminance sampled from stable shadowed regions."),
        new("mean-sunlit-indirect-luminance", "luminance", "Mean indirect luminance sampled from stable sunlit regions."),
        new("colored-bounce-chroma-ratio", "ratio", "Colored bounce chroma relative to neutral white reference."),
        new("emissive-bounce-luminance", "luminance", "Emissive contribution measured in the emissive material room."),
        new("raw-atlas-luminance", "luminance", "Probe blend irradiance luminance before final gather suppression."),
        new("sampled-irradiance-before-albedo", "luminance", "Forward-sampled DDGI irradiance before receiver albedo."),
        new("final-ddgi-diffuse-after-albedo", "luminance", "Final DDGI diffuse after receiver BRDF and confidence gates."),
        new("effective-ddgi-weight", "ratio", "Final DDGI contribution weight after support, visibility, and suppression."),
        new("environment-fallback-weight", "weight", "Average environment fallback blend weight in DDGI-covered pixels."),
        new("thin-wall-leak-ratio", "relative-luma", "Leakage ratio across thin-wall validation geometry."),
        new("probe-cache-warmup-frames", "frames", "Frames required for the published DDGI cache to reach steady state."),
        new("ddgi-gpu-p95", "milliseconds", "Total DDGI update P95 across schedule, trace, blend, relocate/classify, and publish."),
        new("ddgi-memory", "bytes", "DDGI atlas, buffers, scheduler, gather, and ray scratch memory.")
    ];

    public static IReadOnlyList<SampleGiRegressionComparison> Phase9RequiredComparisons { get; } =
    [
        new("direct-only-vs-ddgi", "DDGI-enabled capture must show higher shadowed indirect luminance than direct-only without increased fallback."),
        new("confidence-bypass-vs-normal-ddgi", "Confidence bypass must not be the only path where raw atlas energy reaches the final image."),
        new("raw-atlas-vs-final-indirect", "Healthy raw atlas luminance must not collapse before final indirect output."),
        new("ddgi-high-vs-ultra-reference", "DdgiHigh should remain within tolerance of DdgiUltra reference for steady production scenes.")
    ];

    public static IReadOnlyList<SampleGiValidationScene> Phase11RegressionScenes { get; } =
    [
        new(
            "CornellBox_Static",
            SamplePerformanceScenario.GiCornellRoom,
            "Static enclosed room with colored bounce, point-light shadowing, and dense local DDGI support.",
            RequiresLocalDenseVolume: true,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "Sponza_Alley_Shadowed",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Shadowed arcade/alley pixels for support coverage, fallback weight, and visibility-moment stability.",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "Sponza_Courtyard_Sunlit",
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            "Sunlit courtyard pixels for direct-only, raw DDGI diffuse, and final direct-plus-DDGI comparisons.",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "ThinWallRoom",
            SamplePerformanceScenario.GiThinWallLeakTest,
            "Thin-wall adjacent rooms for relocation, leak clamp, and invalid-support ownership regressions.",
            RequiresLocalDenseVolume: true,
            RequiresCameraRelativeScroll: false,
            RequiresCameraCut: false),
        new(
            "CameraScroll_Clipmap",
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            "Camera-relative clipmap scrolling path for warmup starvation and scheduler overflow regressions.",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: true,
            RequiresCameraCut: false),
        new(
            "LocalVolume_StreamInOut",
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            "Authored local-volume stream-in/out path with clipmap backup and gather tile support readiness.",
            RequiresLocalDenseVolume: true,
            RequiresCameraRelativeScroll: true,
            RequiresCameraCut: false)
    ];

    public static IReadOnlyList<SampleGiExpectedMetricThreshold> Phase11ExpectedMetricThresholds { get; } =
    [
        new("average-support-coverage", Minimum: 0.05f, Maximum: 1.0f, Unit: "ratio"),
        new("average-effective-ddgi-weight", Minimum: 0.02f, Maximum: 1.0f, Unit: "ratio"),
        new("fallback-weight", Minimum: 0.0f, Maximum: 1.0f, Unit: "ratio"),
        new("visible-warmed-fraction", Minimum: 0.80f, Maximum: 1.0f, Unit: "ratio"),
        new("scheduler-time", Minimum: 0.0f, Maximum: 350.0f, Unit: "microseconds"),
        new("update-time", Minimum: 0.0f, Maximum: 1.5f, Unit: "milliseconds"),
        new("candidate-overflow", Minimum: 0.0f, Maximum: 0.0f, Unit: "count")
    ];

    public static IReadOnlyList<string> Phase11RenderDocChecklist { get; } =
    [
        "selected gather tile",
        "selected volume index",
        "probe indices sampled",
        "probe states",
        "irradiance atlas texels",
        "visibility atlas texels",
        "ddgi.weight",
        "ddgi.supportCoverage",
        "effectiveDdgiWeight",
        "final color contribution"
    ];

    public static IReadOnlyList<SampleGiCiGuard> Phase11CiGuards { get; } =
    [
        new(
            "no-zero-output-for-covered-pixels",
            "Fail when spatial coverage is high but support, effective contribution, and fallback are all zero."),
        new(
            "steady-state-scheduler-overflow-free",
            "Fail when scheduler overflow persists after cache warmup reaches steady state."),
        new(
            "cache-warmup-bounded",
            "Fail when the DDGI cache remains cold beyond the configured warmup window."),
        new(
            "visible-local-probes-not-starved",
            "Fail when visible local probes remain below the warmup completion target in steady state.")
    ];

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
            "ddgi-verticality-rings",
            SamplePerformanceScenario.GiVerticalityRings,
            "Tall tower and distant large occluders with rings only",
            RequiresLocalDenseVolume: false,
            RequiresCameraRelativeScroll: false,
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
        new("spatial-coverage-mean", "ratio", "Mean DDGI spatial coverage over the measured image mask."),
        new("support-coverage-mean", "ratio", "Mean usable probe support over spatially covered pixels."),
        new("effective-ddgi-weight-mean", "ratio", "Mean final DDGI contribution weight after support, visibility, and suppression."),
        new("zero-support-spatial-fraction", "ratio", "Spatially covered pixels whose usable support collapsed to zero."),
        new("scheduler-p95", "microseconds", "CPU or GPU scheduler P95 selected by the active scheduler mode."),
        new("ddgi-gpu-p95", "milliseconds", "P95 of the split DDGI GPU update passes."),
        new("ddgi-memory", "bytes", "DDGI texture, atlas, scheduler, and staging memory."),
        new("warmup-frame-count", "frames", "Frames required to reach steady-state cache warmup.")
    ];

    public static IReadOnlyList<SampleGiGoldenDebugBuffer> Phase10GoldenDebugBuffers { get; } =
    [
        new("final-color", GlobalIlluminationDebugView.None, RelativeLuminanceTolerance: 0.04f, AbsoluteTolerance: 0.005f),
        new("ddgi-sampled-irradiance", GlobalIlluminationDebugView.DdgiSampledIrradiance, RelativeLuminanceTolerance: 0.05f, AbsoluteTolerance: 0.006f),
        new("ddgi-final-diffuse", GlobalIlluminationDebugView.DdgiFinalDiffuse, RelativeLuminanceTolerance: 0.05f, AbsoluteTolerance: 0.006f),
        new("ddgi-raw-diffuse", GlobalIlluminationDebugView.DdgiRawDiffuse, RelativeLuminanceTolerance: 0.05f, AbsoluteTolerance: 0.006f),
        new("ddgi-confidence-bypass", GlobalIlluminationDebugView.DdgiConfidenceBypass, RelativeLuminanceTolerance: 0.05f, AbsoluteTolerance: 0.006f),
        new("ddgi-effective-weight", GlobalIlluminationDebugView.DdgiEffectiveWeight, RelativeLuminanceTolerance: 0.03f, AbsoluteTolerance: 0.004f),
        new("ddgi-coverage", GlobalIlluminationDebugView.DdgiCoverage, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f),
        new("ddgi-support-coverage", GlobalIlluminationDebugView.DdgiSupportCoverage, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f),
        new("ddgi-data-confidence", GlobalIlluminationDebugView.DdgiDataConfidence, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f),
        new("ddgi-confidence-chain", GlobalIlluminationDebugView.DdgiConfidenceChain, RelativeLuminanceTolerance: 0.02f, AbsoluteTolerance: 0.003f),
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
            or SamplePerformanceScenario.GiSimpleDdgiFurnace
            or SamplePerformanceScenario.GiCornellRoom
            or SamplePerformanceScenario.GiQualityInterior
            or SamplePerformanceScenario.GiThinWallLeakTest
            or SamplePerformanceScenario.GiMovingPointLight
            or SamplePerformanceScenario.GiMovingRigidObject
            or SamplePerformanceScenario.GiBrightExteriorRoom
            or SamplePerformanceScenario.GiLongCorridorOcclusion
            or SamplePerformanceScenario.GiEmissiveMaterialRoom
            or SamplePerformanceScenario.GiLocalVolumeStreaming
            or SamplePerformanceScenario.GiFastTraversalTeleport
            or SamplePerformanceScenario.GiVerticalityRings
            or SamplePerformanceScenario.GiInstancedCityStress
            or SamplePerformanceScenario.ForestFoliage;
    }

    public static void ConfigureRenderSettings(RenderSettings settings, SamplePerformanceScenario scenario)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!IsValidationScenario(scenario))
            return;

        if (scenario == SamplePerformanceScenario.GiSponzaRightWallStationary)
        {
            // Keep the validation path physically identical to normal Sponza.
            // The narrow overlay below is limited to deterministic capture controls.
            SampleSponzaGlobalIlluminationProfile.Configure(settings);
            SampleSponzaGlobalIlluminationProfile.ApplyValidationOverlay(settings);
            // Exercise the authored curtain path only in the locked A/B
            // qualification scenario until the hardware acceptance gates pass.
            settings.GlobalIllumination.SimpleDdgiThinSurfaceTransmissionEnabled = true;
            return;
        }

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        SampleSponzaGlobalIlluminationProfile.ApplyValidationOverlay(settings);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = false;
        gi.UseDdgi = true;
        gi.DdgiSimpleEnabled = true;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 1.5f;
        gi.EnvironmentFallbackIntensity = 0.2f;
        gi.MaxBounceDistance = 10.0f;
        gi.DdgiClipmapBaseSpacing = 0.75f;
        gi.DdgiThinWallPolicyEnabled = true;
        gi.DdgiRoomSpacingScaledBiasEnabled = true;
        gi.DdgiThinWallLeakClampStrength = 0.9f;
        gi.DdgiThinWallProxyThickness = 0.12f;
        gi.DdgiSelfShadowBiasScale = 1.0f;
        gi.DdgiHysteresisResponse = 1.0f;
        gi.SimpleDdgiAuthoredVolumes.Clear();
        gi.SimpleDdgiRingCount = 3;
        gi.SimpleDdgiRingBaseSpacing = 1.25f;
        gi.SimpleDdgiRingSpacingMultiplier = 3.0f;
        gi.SimpleDdgiNearRingGridSizeX = 28;
        gi.SimpleDdgiNearRingGridSizeY = 14;
        gi.SimpleDdgiNearRingGridSizeZ = 28;
        gi.SimpleDdgiMidRingGridSizeX = 18;
        gi.SimpleDdgiMidRingGridSizeY = 10;
        gi.SimpleDdgiMidRingGridSizeZ = 18;
        gi.SimpleDdgiFarRingGridSizeX = 12;
        gi.SimpleDdgiFarRingGridSizeY = 8;
        gi.SimpleDdgiFarRingGridSizeZ = 12;
        gi.SimpleDdgiProbeUpdatesPerFrame = 2_048;
        gi.DdgiProbeUpdatePrimaryRayBudget = 262_144;
        gi.DdgiColdStartPrimaryRayBudget = 524_288;
        gi.SimpleDdgiNearFullRaysPerProbe = 128;
        gi.SimpleDdgiMidFullRaysPerProbe = 64;
        gi.SimpleDdgiFarFullRaysPerProbe = 32;
        gi.SimpleDdgiNearMaintenanceRaysPerProbe = 32;
        gi.SimpleDdgiMidMaintenanceRaysPerProbe = 16;
        gi.SimpleDdgiFarMaintenanceRaysPerProbe = 8;
        gi.SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold = 0.95f;
        gi.SimpleDdgiSampledAtlasEnabled = true;
        gi.SimpleDdgiReducedBlendEnabled = false;
        gi.TemporalEnabled = false;
        gi.DenoiserEnabled = false;

        if (scenario is SamplePerformanceScenario.GiCornellRoom or SamplePerformanceScenario.GiSimpleDdgiFurnace)
        {
            settings.Exposure = 0.85f;
            gi.IndirectIntensity = 0.85f;
            gi.EnvironmentFallbackIntensity = 0.0f;
            settings.Environment.Enabled = false;
            settings.Environment.SkyIntensity = 0.0f;
            settings.Environment.DiffuseIntensity = 0.0f;
            settings.Environment.SpecularIntensity = 0.0f;
            gi.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
                new Vector3(-3.25f, -0.15f, -8.75f),
                new Vector3(3.25f, 4.25f, -2.25f),
                0.75f));
        }
        else if (scenario == SamplePerformanceScenario.GiQualityInterior)
        {
            settings.Exposure = 0.82f;
            settings.Fog.Enabled = true;
            settings.Fog.Density = 0.018f;
            settings.Fog.StartDistance = 1.0f;
            settings.Fog.EndDistance = 34.0f;
            settings.Fog.MaxOpacity = 0.38f;
            settings.Particles.Enabled = true;
            settings.Particles.SimulationMode = ParticleSimulationMode.Cpu;
            settings.Reflections.Enabled = true;
            settings.Reflections.Mode = ReflectionMode.StaticProbes;
            settings.Reflections.CaptureOnLoad = true;
            settings.Reflections.MaxProbeCapturesPerFrame = 2;
            gi.FarFieldClipmapEnabled = true;
            gi.FarFieldSkyVisibilityEnabled = true;
            gi.FarFieldSunShadowEnabled = true;
            gi.SimpleDdgiFogEnabled = true;
            gi.SimpleDdgiParticlesEnabled = true;
            gi.SimpleDdgiRoughSpecularEnabled = true;
            gi.EnvironmentFallbackIntensity = 0.18f;
            gi.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
                new Vector3(-3.25f, -0.15f, -8.75f),
                new Vector3(3.25f, 4.25f, -2.25f),
                0.75f));
        }
        else if (scenario == SamplePerformanceScenario.GiVerticalityRings)
        {
            gi.SimpleDdgiAuthoredVolumes.Clear();
            gi.IndirectIntensity = 1.1f;
            gi.EnvironmentFallbackIntensity = 0.35f;
            gi.SimpleDdgiNearRingGridSizeY = 16;
            gi.SimpleDdgiMidRingGridSizeY = 16;
            gi.SimpleDdgiFarRingGridSizeY = 16;
            settings.Environment.Enabled = true;
            settings.Environment.DiffuseIntensity = 0.25f;
        }
        else if (scenario == SamplePerformanceScenario.GiInstancedCityStress)
        {
            gi.SimpleDdgiAuthoredVolumes.Clear();
            gi.IndirectIntensity = 1.15f;
            gi.EnvironmentFallbackIntensity = 0.30f;
            gi.SimpleDdgiNearRingGridSizeX = 32;
            gi.SimpleDdgiNearRingGridSizeY = 14;
            gi.SimpleDdgiNearRingGridSizeZ = 32;
            gi.SimpleDdgiMidRingGridSizeX = 21;
            gi.SimpleDdgiMidRingGridSizeY = 10;
            gi.SimpleDdgiMidRingGridSizeZ = 21;
            gi.SimpleDdgiFarRingGridSizeX = 14;
            gi.SimpleDdgiFarRingGridSizeY = 8;
            gi.SimpleDdgiFarRingGridSizeZ = 14;
            gi.SimpleDdgiProbeUpdatesPerFrame = 3_072;
            gi.FarFieldStartDistance = 12.0f;
            settings.Environment.Enabled = true;
            settings.Environment.DiffuseIntensity = 0.22f;
        }
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

public sealed record SampleGiProductionScene(
    string Name,
    SamplePerformanceScenario Scenario,
    string Coverage,
    bool RequiresDynamicActor,
    bool RequiresDynamicLight,
    bool RequiresCameraTeleport);

public sealed record SampleGiPerformanceTarget(
    DdgiQualityTier Tier,
    double UpdateP95BudgetMilliseconds,
    ulong AtlasMemoryBudgetBytes,
    bool ReferenceTier);

public sealed record SampleGiAccuracyOracle(
    string Name,
    SamplePerformanceScenario Scenario,
    string Description,
    string Metric,
    float? ReferenceValue,
    float MaximumRelativeError,
    int? MaximumLatencyFrames);

public sealed record SampleGiRegressionComparison(
    string Name,
    string Description);

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

public sealed record SampleGiExpectedMetricThreshold(
    string Metric,
    float Minimum,
    float Maximum,
    string Unit);

public sealed record SampleGiCiGuard(
    string Name,
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
