using System;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSmokeOptionsParserTests
{
    [SetUp]
    public void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_RELOAD_COUNT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_FORCE_MISSING_ASSETS", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_INDIRECT_DISPATCH", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_LOD", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_SHADOW_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_FAR_FIELD_CLIPMAP", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_FAR_FIELD_FORCE_ALL", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_SCHEDULER_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_STORAGE_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SIMPLE_DDGI_MIRROR_COVERAGE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_TRANSPARENCY_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_SPONZA_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_MATERIAL_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_MATERIAL_GI_QUALIFICATION_MANIFEST", null);
        Environment.SetEnvironmentVariable(
            "NJULF_ADVANCED_GI_PREREQUISITE_MANIFEST",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_ADVANCED_GI_QUALIFICATION_MANIFEST",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_ADVANCED_GI_RUNTIME_EVIDENCE_BUNDLE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_ADVANCED_GI_STARTUP_PROFILE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_RECEIVER_FEEDBACK_MODE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_DDGI_OPACITY_MICROMAP_MODE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_DIRECTIONAL_GUIDING_MODE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_GI_CAUSTIC_MODE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_MODE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_SIMPLE_DDGI_RECEIVER_FEEDBACK_QUALIFICATION_ID",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_DDGI_OPACITY_MICROMAP_QUALIFICATION_ID",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_SIMPLE_DDGI_DIRECTIONAL_GUIDING_QUALIFICATION_ID",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_GI_CAUSTIC_QUALIFICATION_ID",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_QUALIFICATION_ID",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_MATERIAL_GI_QUALIFICATION_CANDIDATE",
            null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REPORT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES", null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_MAX_SETTLE_FRAMES",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_BUDGET_PROFILE",
            null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_PAIR_ID", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_VARIANT", null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_REQUIRE_PRODUCTION",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_HDR_REFERENCE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_HDR_CANDIDATE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_SHADER_PROFILE",
            null);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_REQUIRE_SHADER_PROFILE",
            null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_VALIDATION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_QUALITY_PRESET", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_REPORT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_WARMUP_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_SAMPLE_INTERVAL", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MAX_SAMPLES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MEMORY_GROWTH_TOLERANCE_BYTES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_LONG_RUN_MINUTES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_TAIL_DDGI_LONG_SOAK", null);
    }

    [Test]
    public void DefaultsToCornellGlobalIlluminationScene()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());
        SampleSceneKind firstScene = Enum.GetValues<SampleSceneKind>()[0];

        Assert.Multiple(() =>
        {
            Assert.That(firstScene, Is.EqualTo(SampleSceneKind.GlobalIlluminationTest));
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.GlobalIlluminationTest));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.None));
            Assert.That(options.Enabled, Is.False);
        });
    }

    [Test]
    public void CommandLineOverridesEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE", "startup");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES", "3");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_VALIDATION", "off");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--smoke-mode", "resize",
            "--smoke-frames", "6",
            "--validation", "standard"
        });

        Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Resize));
        Assert.That(options.FrameCount, Is.EqualTo(6));
        Assert.That(options.ValidationMode, Is.EqualTo(RendererValidationMode.Standard));
    }

    [Test]
    public void ParsesPerformanceScenarioAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", "foliage-like-static-instances"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.FoliageLikeStaticInstances));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [TestCase("material-showcase", SampleSceneKind.MaterialShowcase)]
    [TestCase("global-illumination-test", SampleSceneKind.GlobalIlluminationTest)]
    [TestCase("foliage-showcase", SampleSceneKind.FoliageShowcase)]
    [TestCase("vfx-showcase", SampleSceneKind.VfxShowcase)]
    public void ParsesSceneAndDefaultsToStartupSmoke(string value, SampleSceneKind expected)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--scene", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.SceneKind, Is.EqualTo(expected));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ParsesMaterialShowcaseSceneEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE", "material_showcase");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.MaterialShowcase));
    }

    [Test]
    public void ParsesFoliageDebugFallbackScenario()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", "foliage-debug-fallback"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.FoliageDebugFallback));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [TestCase("dense-grass-field", SamplePerformanceScenario.DenseGrassField)]
    [TestCase("shrub-foliage", SamplePerformanceScenario.ShrubFoliage)]
    [TestCase("mixed-tree-line-foliage", SamplePerformanceScenario.MixedTreeLineFoliage)]
    [TestCase("mixed-tree-line-foliage-no-shadows", SamplePerformanceScenario.MixedTreeLineFoliageNoShadows)]
    public void ParsesPhase9FoliageStressScenarios(string value, SamplePerformanceScenario expected)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(expected));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [TestCase("gi-cornell-room", SamplePerformanceScenario.GiCornellRoom)]
    [TestCase("gi-simple-ddgi-furnace", SamplePerformanceScenario.GiSimpleDdgiFurnace)]
    [TestCase("gi-sponza-right-wall-stationary", SamplePerformanceScenario.GiSponzaRightWallStationary)]
    [TestCase("gi-thin-wall-leak-test", SamplePerformanceScenario.GiThinWallLeakTest)]
    [TestCase("gi-moving-point-light", SamplePerformanceScenario.GiMovingPointLight)]
    [TestCase("gi-moving-rigid-object", SamplePerformanceScenario.GiMovingRigidObject)]
    [TestCase("gi-bright-exterior-room", SamplePerformanceScenario.GiBrightExteriorRoom)]
    [TestCase("gi-long-corridor-occlusion", SamplePerformanceScenario.GiLongCorridorOcclusion)]
    [TestCase("gi-emissive-material-room", SamplePerformanceScenario.GiEmissiveMaterialRoom)]
    [TestCase("gi-local-volume-streaming", SamplePerformanceScenario.GiLocalVolumeStreaming)]
    [TestCase("gi-fast-traversal-teleport", SamplePerformanceScenario.GiFastTraversalTeleport)]
    [TestCase("gi-instanced-city-stress", SamplePerformanceScenario.GiInstancedCityStress)]
    public void ParsesGlobalIlluminationValidationScenarios(string value, SamplePerformanceScenario expected)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(expected));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void SponzaRightWallScenario_SelectsSponzaSceneWhenSceneIsUnspecified()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", "gi-sponza-right-wall-stationary"
        });

        Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.SponzaPlaza));
    }

    [Test]
    public void SponzaRightWallScenario_RejectsExplicitConflictingScene()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--scene", "material-showcase",
                "--performance-scenario", "gi-sponza-right-wall-stationary"
            }),
            Throws.ArgumentException.With.Message.Contains(
                "requires the Sponza plaza scene"));
    }

    [Test]
    public void GlobalIlluminationValidation_CoversPhase9Scenarios()
    {
        SamplePerformanceScenario[] scenarios =
        [
            SamplePerformanceScenario.GiSponzaRightWallStationary,
            SamplePerformanceScenario.GiCornellRoom,
            SamplePerformanceScenario.GiThinWallLeakTest,
            SamplePerformanceScenario.GiMovingPointLight,
            SamplePerformanceScenario.GiMovingRigidObject,
            SamplePerformanceScenario.GiBrightExteriorRoom,
            SamplePerformanceScenario.GiLongCorridorOcclusion,
            SamplePerformanceScenario.GiEmissiveMaterialRoom,
            SamplePerformanceScenario.GiLocalVolumeStreaming,
            SamplePerformanceScenario.GiFastTraversalTeleport,
            SamplePerformanceScenario.GiInstancedCityStress
        ];
        SamplePerformanceScenario[] benchmarkScenarios = SampleDdgiBenchmarkSuite.Scenes
            .Select(scene => scene.Scenario)
            .ToArray();

        Assert.Multiple(() =>
        {
            foreach (SamplePerformanceScenario scenario in scenarios)
            {
                Assert.That(SampleGlobalIlluminationValidation.IsValidationScenario(scenario), Is.True, scenario.ToString());
                Assert.That(benchmarkScenarios, Does.Contain(scenario), scenario.ToString());

                var settings = new RenderSettings();
                SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, scenario);

                Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True, scenario.ToString());
                Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True, scenario.ToString());
            }
        });
    }

    [Test]
    public void GlobalIlluminationValidation_CoversPhase10SceneMetricAndGoldenContracts()
    {
        string[] sceneNames = SampleGlobalIlluminationValidation.Phase10DeterministicScenes
            .Select(scene => scene.Name)
            .ToArray();
        string[] metricNames = SampleGlobalIlluminationValidation.Phase10Metrics
            .Select(metric => metric.Name)
            .ToArray();
        string[] phase9MetricNames = SampleGlobalIlluminationValidation.Phase9RegressionMetrics
            .Select(metric => metric.Name)
            .ToArray();
        string[] phase9ComparisonNames = SampleGlobalIlluminationValidation.Phase9RequiredComparisons
            .Select(comparison => comparison.Name)
            .ToArray();
        string[] goldenBufferNames = SampleGlobalIlluminationValidation.Phase10GoldenDebugBuffers
            .Select(buffer => buffer.Name)
            .ToArray();
        SampleGiSchedulerEquivalenceContract schedulerContract = SampleGlobalIlluminationValidation.Phase10SchedulerEquivalence;

        Assert.Multiple(() =>
        {
            Assert.That(sceneNames, Is.EquivalentTo(new[]
            {
                "ddgi-open-sky-ground",
                "ddgi-thin-wall-corridor",
                "ddgi-sponza-courtyard",
                "ddgi-local-volume-room",
                "ddgi-camera-relative-scroll",
                "ddgi-verticality-rings",
                "ddgi-teleport-cut"
            }));
            Assert.That(metricNames, Is.EquivalentTo(new[]
            {
                "mean-shadowed-indirect-luminance",
                "mean-sunlit-indirect-luminance",
                "spatial-coverage-mean",
                "support-coverage-mean",
                "effective-ddgi-weight-mean",
                "zero-support-spatial-fraction",
                "scheduler-p95",
                "ddgi-gpu-p95",
                "ddgi-memory",
                "warmup-frame-count"
            }));
            Assert.That(phase9MetricNames, Is.SupersetOf(new[]
            {
                "colored-bounce-chroma-ratio",
                "emissive-bounce-luminance",
                "raw-atlas-luminance",
                "sampled-irradiance-before-albedo",
                "final-ddgi-diffuse-after-albedo",
                "effective-ddgi-weight",
                "environment-fallback-weight",
                "thin-wall-leak-ratio",
                "ddgi-gpu-p95",
                "ddgi-memory"
            }));
            Assert.That(phase9ComparisonNames, Is.EquivalentTo(new[]
            {
                "direct-only-vs-ddgi",
                "confidence-bypass-vs-normal-ddgi",
                "raw-atlas-vs-final-indirect",
                "ddgi-high-vs-ultra-reference"
            }));
            Assert.That(goldenBufferNames, Is.EquivalentTo(new[]
            {
                "final-color",
                "ddgi-sampled-irradiance",
                "ddgi-final-diffuse",
                "ddgi-raw-diffuse",
                "ddgi-confidence-bypass",
                "ddgi-effective-weight",
                "ddgi-coverage",
                "ddgi-support-coverage",
                "ddgi-data-confidence",
                "ddgi-confidence-chain",
                "ddgi-visibility",
                "ddgi-suppression-mask"
            }));
            Assert.That(SampleGlobalIlluminationValidation.Phase10GoldenDebugBuffers.Select(buffer => buffer.RelativeLuminanceTolerance), Has.All.GreaterThan(0.0f));
            Assert.That(SampleGlobalIlluminationValidation.Phase10GoldenDebugBuffers.Select(buffer => buffer.AbsoluteTolerance), Has.All.GreaterThan(0.0f));
            Assert.That(SampleGlobalIlluminationValidation.Phase10DeterministicScenes.Count(scene => scene.RequiresLocalDenseVolume), Is.EqualTo(2));
            Assert.That(SampleGlobalIlluminationValidation.Phase10DeterministicScenes.Count(scene => scene.RequiresCameraRelativeScroll), Is.EqualTo(2));
            Assert.That(SampleGlobalIlluminationValidation.Phase10DeterministicScenes.Count(scene => scene.RequiresCameraCut), Is.EqualTo(1));
            Assert.That(schedulerContract.MaxRequestCountDelta, Is.EqualTo(0));
            Assert.That(schedulerContract.MaxInvalidProbeCount, Is.EqualTo(0));
            Assert.That(schedulerContract.MaxDuplicateRequestCount, Is.EqualTo(0));
            Assert.That(schedulerContract.MaxPriorityBucketDelta, Is.LessThanOrEqualTo(1));
            Assert.That(schedulerContract.MaxPerVolumeDistributionDelta, Is.LessThanOrEqualTo(1));
            Assert.That(schedulerContract.MaxCoverageMeanDelta, Is.LessThanOrEqualTo(0.01f));
        });
    }

    [Test]
    public void GlobalIlluminationValidationSettings_EnableVisibleRayQuerySimpleDdgiPath()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.High);

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, SamplePerformanceScenario.GiCornellRoom);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
            Assert.That(settings.GlobalIllumination.DdgiCameraRelativeEnabled, Is.True);
            Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(0.85f));
            Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(10.0f));
            Assert.That(settings.GlobalIllumination.DdgiThinWallPolicyEnabled, Is.True);
            Assert.That(settings.GlobalIllumination.DdgiThinWallLeakClampStrength, Is.EqualTo(0.9f));
            Assert.That(settings.GlobalIllumination.DdgiSelfShadowBiasScale, Is.EqualTo(1.0f));
            Assert.That(settings.GlobalIllumination.TemporalEnabled, Is.False);
            Assert.That(settings.GlobalIllumination.DenoiserEnabled, Is.False);
            Assert.That(
                settings.Diagnostics.DdgiForwardEstimateCountersEnabled,
                Is.EqualTo(RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled));
            Assert.That(settings.Environment.Enabled, Is.False);
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(0.0f));
            Assert.That(settings.ResolutionScale, Is.EqualTo(1.0f));
            Assert.That(settings.EffectiveResolutionScale, Is.EqualTo(1.0f));
            Assert.That(settings.DynamicResolution.Enabled, Is.False);
            Assert.That(settings.DynamicResolution.MinimumScale, Is.EqualTo(1.0f));
            Assert.That(settings.DynamicResolution.MaximumScale, Is.EqualTo(1.0f));
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Bloom.Enabled, Is.False);
            Assert.That(settings.Fog.Enabled, Is.False);
            Assert.That(settings.Reflections.Enabled, Is.False);
            Assert.That(settings.AmbientOcclusion.Enabled, Is.True);
            Assert.That(settings.Shadows.PointShadowMapSize, Is.EqualTo(1024));
            Assert.That(settings.Shadows.PointNormalBias, Is.EqualTo(0.008f));
            Assert.That(settings.Shadows.PointConstantDepthBias, Is.EqualTo(0.0003f));
        });
    }

    [Test]
    public void GlobalIlluminationValidation_DefinesTemporalStabilityAndTimingGates()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SampleGlobalIlluminationValidation.Phase7ProductionScenes.Select(scene => scene.Name),
                Is.EquivalentTo(new[]
                {
                    "sponza-interior",
                    "simple-ddgi-furnace",
                    "sunlit-courtyard",
                    "colored-bounce-room",
                    "thin-wall-corridor",
                    "emissive-room",
                    "moving-rigid-object",
                    "moving-local-light",
                    "camera-teleport-recenter",
                    "verticality-rings",
                    "outdoor-foliage-plaza"
                }));
            Assert.That(
                SampleGlobalIlluminationValidation.Phase7ProductionScenes.Select(scene => scene.Scenario),
                Is.SupersetOf(new[]
                {
                    SamplePerformanceScenario.GiSponzaRightWallStationary,
                    SamplePerformanceScenario.GiSimpleDdgiFurnace,
                    SamplePerformanceScenario.GiCornellRoom,
                    SamplePerformanceScenario.GiLongCorridorOcclusion,
                    SamplePerformanceScenario.GiEmissiveMaterialRoom,
                    SamplePerformanceScenario.GiMovingRigidObject,
                    SamplePerformanceScenario.GiMovingPointLight,
                    SamplePerformanceScenario.GiFastTraversalTeleport,
                    SamplePerformanceScenario.GiVerticalityRings,
                    SamplePerformanceScenario.ForestFoliage
                }));
            Assert.That(SampleGlobalIlluminationValidation.Phase7ProductionScenes.Any(scene => scene.RequiresDynamicActor), Is.True);
            Assert.That(SampleGlobalIlluminationValidation.Phase7ProductionScenes.Any(scene => scene.RequiresDynamicLight), Is.True);
            Assert.That(SampleGlobalIlluminationValidation.Phase7ProductionScenes.Any(scene => scene.RequiresCameraTeleport), Is.True);
            Assert.That(
                SampleGlobalIlluminationValidation.DeterministicPaths.Select(path => path.Name),
                Is.SupersetOf(new[]
                {
                    "sponza-right-wall-stationary",
                    "stationary-convergence",
                    "slow-pan",
                    "fast-pan",
                    "translation",
                    "camera-cut",
                    "fov-change",
                    "moving-rigid-and-skinned",
                    "moving-light",
                    "thin-wall-silhouette"
                }));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Select(gate => gate.Metric),
                Is.SupersetOf(new[]
                {
                    "history-rejection-ratio",
                    "stable-temporal-luma-error",
                    "right-wall-relative-luma-stddev",
                    "disocclusion-recovery-frames",
                    "thin-wall-leakage",
                    "cornell-room-leakage",
                    "bright-exterior-room-leakage",
                    "room-clipmap-transition-seam",
                    "nan-inf-hdr-outliers",
                    "ddgi-coverage-debug-contamination",
                    "ddgi-gpu-scheduler-invalid-requests",
                    "ddgi-gpu-scheduler-duplicate-requests",
                    "ddgi-gpu-scheduler-fallback-active",
                    "ddgi-gpu-mode-cpu-scheduler-us",
                }));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "history-rejection-ratio").Maximum,
                Is.LessThanOrEqualTo(0.35f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "right-wall-relative-luma-stddev").Maximum,
                Is.LessThanOrEqualTo(0.02f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "ddgi-coverage-debug-contamination").Maximum,
                Is.EqualTo(0.0f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "ddgi-gpu-scheduler-invalid-requests").Maximum,
                Is.EqualTo(0.0f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "ddgi-gpu-scheduler-fallback-active").Maximum,
                Is.EqualTo(0.0f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "disocclusion-recovery-frames").Maximum,
                Is.LessThanOrEqualTo(6.0f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "thin-wall-leakage").Maximum,
                Is.LessThanOrEqualTo(0.03f));
            Assert.That(
                SampleGlobalIlluminationValidation.Gates.Single(gate => gate.Metric == "room-clipmap-transition-seam").Maximum,
                Is.LessThanOrEqualTo(0.04f));
        });
    }

    [Test]
    public void GlobalIlluminationValidationSettings_LeaveNonGiScenarioUnchanged()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.High);

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, SamplePerformanceScenario.ManyLights);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.False);
            Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void ParsesPerformanceScenarioWithFrameCountAsStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", "foliage-like-static-instances",
            "--smoke-frames", "3"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.FoliageLikeStaticInstances));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ExplicitSmokeModeOverridesPerformanceScenarioDefault()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--performance-scenario", "foliage-like-static-instances",
            "--smoke-mode", "resize",
            "--smoke-frames", "3"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.FoliageLikeStaticInstances));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Resize));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ParsesGpuTimingFlagForSmokeRuns()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--smoke-mode", "startup",
            "--gpu-timing"
        });

        Assert.That(options.EnableGpuTiming, Is.True);
    }

    [Test]
    public void ParsesGpuTimingEnvironmentForSmokeRuns()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING", "true");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.That(options.EnableGpuTiming, Is.True);
    }

    [Test]
    public void ParsesGpuMeshletCounterFlagAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--gpu-meshlet-counters"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableGpuMeshletCounters, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void DdgiContentConformanceFlag_IsRuntimeOnlyAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--ddgi-content-conformance"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableDdgiContentConformance, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ParsesSceneSubmissionSmokeFlags()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--smoke-mode", "startup",
            "--scene-gpu-compaction",
            "--scene-indirect-dispatch",
            "--scene-gpu-lod",
            "--scene-gpu-shadow-compaction",
            "--scene-submission-validation"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableSceneGpuCompaction, Is.True);
            Assert.That(options.EnableSceneIndirectDispatch, Is.True);
            Assert.That(options.EnableSceneGpuLodSelection, Is.True);
            Assert.That(options.EnableSceneGpuShadowCompaction, Is.True);
            Assert.That(options.EnableSceneSubmissionValidation, Is.True);
        });
    }

    [Test]
    public void ParsesSceneSubmissionSmokeEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_COMPACTION", "true");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_INDIRECT_DISPATCH", "true");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION", "true");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableSceneGpuCompaction, Is.True);
            Assert.That(options.EnableSceneIndirectDispatch, Is.True);
            Assert.That(options.EnableSceneSubmissionValidation, Is.True);
        });
    }

    [Test]
    public void ParsesAsyncComputeFlagAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--async-compute"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableAsyncCompute, Is.True);
            Assert.That(
                options.AsyncComputeModeOverride,
                Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ParsesAsyncComputeEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE", "true");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableAsyncCompute, Is.True);
            Assert.That(
                options.AsyncComputeModeOverride,
                Is.EqualTo(AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
        });
    }

    [TestCase("auto", AsyncComputeMode.Auto)]
    [TestCase("disabled", AsyncComputeMode.Disabled)]
    [TestCase("off", AsyncComputeMode.Disabled)]
    [TestCase("forced", AsyncComputeMode.ForceEnabledForValidation)]
    [TestCase("force-enabled-for-validation", AsyncComputeMode.ForceEnabledForValidation)]
    public void ParsesExplicitAsyncComputeMode(string value, AsyncComputeMode expected)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--async-compute-mode", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.AsyncComputeModeOverride, Is.EqualTo(expected));
            Assert.That(
                options.EnableAsyncCompute,
                Is.EqualTo(expected == AsyncComputeMode.ForceEnabledForValidation));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ExplicitAsyncComputeEnvironmentOverridesLegacyBoolean()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE", "true");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE_MODE", "disabled");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.AsyncComputeModeOverride, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(options.EnableAsyncCompute, Is.False);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
        });
    }

    [Test]
    public void LaterAsyncComputeArgumentWins()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--async-compute",
            "--async-compute-mode", "disabled"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.AsyncComputeModeOverride, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(options.EnableAsyncCompute, Is.False);
        });
    }

    [Test]
    public void InvalidAsyncComputeModeFailsBeforeRendererConstruction()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--async-compute-mode", "maybe"
            }))!;

        Assert.That(exception.Message, Does.Contain("Valid values: auto, disabled, forced"));
    }

    [Test]
    public void ParsesFarFieldForceAllFlagAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--far-field-force-all"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableFarFieldClipmap, Is.True);
            Assert.That(options.EnableFarFieldForceAll, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ParsesTransparencyModeAndDefaultsToStartupSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--transparency-mode", "weighted-blended-oit"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.TransparencyMode, Is.EqualTo(TransparencyMode.WeightedBlendedOit));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ParsesTransparencyModeEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_TRANSPARENCY_MODE", "weighted");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.That(options.TransparencyMode, Is.EqualTo(TransparencyMode.WeightedBlendedOit));
    }

    [Test]
    public void ParsesBaselineSnapshotDirectoryAndDefaultsToStartupSmoke()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NjulfBaselineSnapshots");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--baseline-snapshot-dir", directory
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.BaselineSnapshotDirectory, Is.EqualTo(System.IO.Path.GetFullPath(directory)));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void BaselineSnapshotDirectoryAllowsSingleFrameSmoke()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--smoke-mode", "startup",
            "--smoke-frames", "1",
            "--baseline-snapshot-dir", System.IO.Path.GetTempPath()
        });

        Assert.That(options.FrameCount, Is.EqualTo(1));
    }

    [Test]
    public void ParsesBenchmarkOptionsAndEnablesGpuTiming()
    {
        string reportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "njulf-benchmark.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--benchmark",
            "--benchmark-report", reportPath,
            "--benchmark-warmup-frames", "0",
            "--benchmark-measure-frames", "8",
            "--benchmark-max-settle-frames", "750",
            "--benchmark-budget-profile", "high"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.AsyncComputeModeOverride, Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(options.Benchmark.ReportPath, Is.EqualTo(System.IO.Path.GetFullPath(reportPath)));
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(0));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(8));
            Assert.That(
                options.Benchmark.MaximumAdditionalSettlingFrameCount,
                Is.EqualTo(750));
            Assert.That(
                options.Benchmark.BudgetProfileOverride,
                Is.EqualTo(RenderBudgetProfileKind.HighSpec1440p60));
            Assert.That(options.EnableGpuTiming, Is.True);
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void BenchmarkWithQualityAndSceneOverrides_OwnsTheFullMeasurementSequence()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--benchmark",
            "--quality-preset", "low",
            "--scene", "global-illumination-test",
            "--benchmark-warmup-frames", "30",
            "--benchmark-measure-frames", "120"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.QualityPresetOverride, Is.EqualTo(RenderQualityPreset.Low));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.None));
            Assert.That(options.FrameCount, Is.Zero);
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(30));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(120));
        });
    }

    [Test]
    public void ProductionBenchmarkRejectsTruncatedTailSettlingWindow()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark",
                "--benchmark-require-production",
                "--benchmark-max-settle-frames", "750"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "--benchmark-max-settle-frames >= 4096"));
    }

    [Test]
    public void ProductionBenchmarkRejectsDdgiContentConformanceAuthorization()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark",
                "--benchmark-require-production",
                "--ddgi-content-conformance"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "non-shipping --ddgi-content-conformance rollout authorization"));
    }

    [Test]
    public void Benchmark_PreservesExplicitAsyncModeForControlledComparison()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--benchmark",
            "--async-compute-mode", "auto"
        });

        Assert.That(options.AsyncComputeModeOverride, Is.EqualTo(AsyncComputeMode.Auto));
    }

    [Test]
    public void BenchmarkRejectsCompetingSmokeFrameSequence()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--benchmark",
                "--smoke-mode", "startup"
            }),
            Throws.ArgumentException.With.Message.Contains("owns its warmup and measurement"));
    }

    [Test]
    public void ParsesBenchmarkEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", "true");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES", "2");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES", "5");
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_BENCHMARK_BUDGET_PROFILE",
            "ultra");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(2));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(5));
            Assert.That(
                options.Benchmark.BudgetProfileOverride,
                Is.EqualTo(RenderBudgetProfileKind.Ultra4k60));
            Assert.That(options.EnableGpuTiming, Is.True);
        });
    }

    [Test]
    public void ParsesLockedBenchmarkEvidenceAndVariantOptions()
    {
        string reference = Path.Combine(Path.GetTempPath(), "reference.pfm");
        string candidate = Path.Combine(Path.GetTempPath(), "candidate.pfm");
        string shaderProfile = Path.Combine(Path.GetTempPath(), "shader-profile.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--benchmark-pair-id", "sponza-right-wall-20260802",
            "--benchmark-variant", "decal-shadows-disabled",
            "--benchmark-hdr-reference", reference,
            "--benchmark-hdr-candidate", candidate,
            "--benchmark-shader-profile", shaderProfile,
            "--benchmark-require-shader-profile"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(
                options.Benchmark.CapturePairId,
                Is.EqualTo("sponza-right-wall-20260802"));
            Assert.That(
                options.Benchmark.CaptureVariant,
                Is.EqualTo("decal-shadows-disabled"));
            Assert.That(
                options.Benchmark.HdrReferencePath,
                Is.EqualTo(Path.GetFullPath(reference)));
            Assert.That(
                options.Benchmark.HdrCandidatePath,
                Is.EqualTo(Path.GetFullPath(candidate)));
            Assert.That(
                options.Benchmark.ShaderProfileArtifactPath,
                Is.EqualTo(Path.GetFullPath(shaderProfile)));
            Assert.That(options.Benchmark.RequireShaderProfileEvidence, Is.True);
        });
    }

    [Test]
    public void BenchmarkHdrCandidate_RequiresReferenceGate()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark-hdr-candidate",
                Path.Combine(Path.GetTempPath(), "candidate.pfm")
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "requires --benchmark-hdr-reference"));
    }

    [Test]
    public void BenchmarkBudgetProfile_RejectsNonReleaseProfile()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--benchmark-budget-profile",
                "development"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "low, medium, high, ultra"));
    }

    [Test]
    public void RejectsInvalidMode()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[] { "--smoke-mode", "chaos" }),
            Throws.ArgumentException.With.Message.Contains("Invalid smoke mode"));
    }

    [Test]
    public void RejectsInvalidPerformanceScenario()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[] { "--performance-scenario", "too-many-ferns" }),
            Throws.ArgumentException.With.Message.Contains("Invalid performance scenario"));
    }

    [Test]
    public void ParsesSponzaGiCaptureDirectoryAsTheLockedStandaloneSequence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NjulfSponzaGiCapture");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--sponza-gi-capture-dir", directory
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.SponzaGiCaptureDirectory, Is.EqualTo(Path.GetFullPath(directory)));
            Assert.That(
                options.SponzaGiCaptureMode,
                Is.EqualTo(SampleSponzaGiCaptureMode.DetailedDiagnostics));
            Assert.That(options.SceneKind, Is.EqualTo(SampleSceneKind.SponzaPlaza));
            Assert.That(options.PerformanceScenario, Is.EqualTo(SamplePerformanceScenario.GiSponzaRightWallStationary));
            Assert.That(options.EnableGpuTiming, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.None));
            Assert.That(options.FrameCount, Is.EqualTo(0));
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void SponzaGiCaptureDirectory_RejectsCompetingSmokeFrameSequence()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--sponza-gi-capture-dir", Path.GetTempPath(),
                "--smoke-frames", "4"
            }),
            Throws.ArgumentException.With.Message.Contains("owns its deterministic frame sequence"));
    }

    [TestCase("low", RenderQualityPreset.Low)]
    [TestCase("medium", RenderQualityPreset.Medium)]
    [TestCase("high", RenderQualityPreset.High)]
    [TestCase("ultra", RenderQualityPreset.Ultra)]
    [TestCase("ddgi-high", RenderQualityPreset.DdgiHigh)]
    public void ParsesQualityPresetAndDefaultsToStartup(
        string value,
        RenderQualityPreset expected)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--quality-preset", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.QualityPresetOverride, Is.EqualTo(expected));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [TestCase("quality-switch", SampleSmokeMode.QualitySwitch, 7)]
    [TestCase("ddgi-residency-switch", SampleSmokeMode.DdgiResidencySwitch, 12)]
    [TestCase("texture-hot-reload", SampleSmokeMode.TextureHotReload, 4)]
    [TestCase("resize", SampleSmokeMode.Resize, 5)]
    [TestCase("minimize", SampleSmokeMode.Minimize, 4)]
    [TestCase("all", SampleSmokeMode.All, 8)]
    public void ParsesProductionRuntimeSmokeModes(
        string value,
        SampleSmokeMode expected,
        int expectedFrames)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--smoke-mode", value
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(expected));
            Assert.That(options.FrameCount, Is.EqualTo(expectedFrames));
        });
    }

    [Test]
    public void ParsesLongRunGateAndDurationWithoutImplicitFrameCap()
    {
        string report = Path.Combine(Path.GetTempPath(), "material-gi-long-run.json");
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--long-run-report", report,
            "--long-run-minutes", "30.5",
            "--long-run-warmup-frames", "360",
            "--long-run-sample-interval", "30",
            "--long-run-max-samples", "512",
            "--long-run-memory-growth-tolerance-bytes", "2097152"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.LongRun));
            Assert.That(options.FrameCount, Is.EqualTo(0));
            Assert.That(options.LongRunMinutes, Is.EqualTo(30.5));
            Assert.That(options.LongRunWarmupFrames, Is.EqualTo(360));
            Assert.That(options.LongRunSampleInterval, Is.EqualTo(30));
            Assert.That(options.LongRunMaxRetainedSamples, Is.EqualTo(512));
            Assert.That(options.LongRunMemoryGrowthToleranceBytes, Is.EqualTo(2_097_152));
            Assert.That(options.LongRunReportPath, Is.EqualTo(Path.GetFullPath(report)));
        });
    }

    [TestCase("production", SampleSponzaGiCaptureMode.ProductionTiming)]
    [TestCase("presentation", SampleSponzaGiCaptureMode.PresentationReview)]
    public void ParsesExplicitSponzaGiCaptureMode(
        string value,
        SampleSponzaGiCaptureMode expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), "NjulfSponzaGiCaptureExplicitMode");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--sponza-gi-capture-dir", directory,
            "--sponza-gi-capture-mode", value
        ]);

        Assert.That(
            options.SponzaGiCaptureMode,
            Is.EqualTo(expected));
    }

    [Test]
    public void SponzaGiCaptureMode_RequiresCaptureDirectoryAndKnownValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                    ["--sponza-gi-capture-mode", "production"]),
                Throws.ArgumentException.With.Message.Contains(
                    "requires --sponza-gi-capture-dir"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--sponza-gi-capture-dir", Path.GetTempPath(),
                    "--sponza-gi-capture-mode", "benchmark-ish"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "Invalid Sponza GI capture mode"));
        });
    }

    [Test]
    public void ParsesSimpleDdgiStorageQualificationOverrides()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--simple-ddgi-storage-mode", "packed",
            "--simple-ddgi-mirror-coverage", "receiver-relevant"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(
                options.SimpleDdgiStoragePackingModeOverride,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Packed));
            Assert.That(
                options.SimpleDdgiSampledAtlasCoverageModeOverride,
                Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant));
        });
    }

    [Test]
    public void ParsesSimpleDdgiStorageQualificationOverridesFromEnvironment()
    {
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_STORAGE_MODE",
            "validate");
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_MIRROR_COVERAGE",
            "full-canonical");

        SampleSmokeOptions options =
            SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(
                options.SimpleDdgiStoragePackingModeOverride,
                Is.EqualTo(SimpleDdgiStoragePackingMode.Validate));
            Assert.That(
                options.SimpleDdgiSampledAtlasCoverageModeOverride,
                Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.FullCanonical));
        });
    }

    [Test]
    public void TailDdgiLongSoak_LocksAcceleratedProductionIdentity()
    {
        string report = Path.Combine(
            Path.GetTempPath(),
            "tail-ddgi-long-soak.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--tail-ddgi-long-soak",
            "--long-run-report", report
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.TailDdgiLongSoak, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.LongRun));
            Assert.That(
                options.FrameCount,
                Is.EqualTo(SampleTailDdgiLongSoakProfile.RequiredFrameCount));
            Assert.That(
                options.LongRunWarmupFrames,
                Is.EqualTo(
                    SampleTailDdgiLongSoakProfile.MinimumWarmupFrameCount));
            Assert.That(
                options.PerformanceScenario,
                Is.EqualTo(
                    SamplePerformanceScenario.GiSimpleDdgiFurnace));
            Assert.That(
                options.QualityPresetOverride,
                Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(options.EnableGpuTiming, Is.True);
            Assert.That(options.ValidationMode, Is.EqualTo(RendererValidationMode.Off));
            Assert.That(
                options.SimpleDdgiSchedulerModeOverride,
                Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(
                options.AsyncComputeModeOverride,
                Is.EqualTo(AsyncComputeMode.Disabled));
            Assert.That(options.Benchmark.Enabled, Is.False);
            Assert.That(options.UsesDeterministicSimulationClock, Is.True);
            Assert.That(
                options.LongRunReportPath,
                Is.EqualTo(Path.GetFullPath(report)));
        });
    }

    [TestCase("--smoke-frames", "3599", "at least 3600")]
    [TestCase("--long-run-warmup-frames", "1199", "at least 1200")]
    [TestCase("--simple-ddgi-scheduler-mode", "cpu-reference", "gpu-resident")]
    [TestCase("--validation", "standard", "validation off")]
    public void TailDdgiLongSoak_RejectsNonCanonicalOverrides(
        string option,
        string value,
        string expectedFailure)
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--tail-ddgi-long-soak",
                "--long-run-report", Path.Combine(
                    Path.GetTempPath(),
                    "tail-ddgi-long-soak.json"),
                option, value
            ]),
            Throws.ArgumentException.With.Message.Contains(expectedFailure));
    }

    [Test]
    public void LongRunOptionsRejectOtherSmokeModes()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--smoke-mode", "startup",
                "--long-run-report", Path.GetTempPath()
            }),
            Throws.ArgumentException.With.Message.Contains("require --smoke-mode long-run"));
    }

    [Test]
    public void LongRunRetainedWindowRequiresAtLeastTwoSamples()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--smoke-mode", "long-run",
                "--long-run-max-samples", "1"
            }),
            Throws.ArgumentException.With.Message.Contains("at least two"));
    }

    [Test]
    public void ParsesMaterialGiQualificationManifestFromCommandLine()
    {
        string manifest = Path.Combine(
            Path.GetTempPath(),
            "material-gi-release-qualification.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(new[]
        {
            "--material-gi-qualification-manifest", manifest
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                options.MaterialGiQualificationManifestPath,
                Is.EqualTo(Path.GetFullPath(manifest)));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.None));
        });
    }

    [Test]
    public void ParsesMaterialGiQualificationManifestFromEnvironment()
    {
        string manifest = Path.Combine(
            Path.GetTempPath(),
            "material-gi-environment-qualification.json");
        Environment.SetEnvironmentVariable(
            "NJULF_MATERIAL_GI_QUALIFICATION_MANIFEST",
            manifest);

        SampleSmokeOptions options =
            SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.That(
            options.MaterialGiQualificationManifestPath,
            Is.EqualTo(Path.GetFullPath(manifest)));
    }

    [Test]
    public void ParsesAdvancedGiStartupManifestsModesAndQualificationIds()
    {
        string prerequisite = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-prerequisites.json");
        string qualification = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-qualification.json");
        string runtimeEvidence = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-runtime-evidence.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--advanced-gi-prerequisite-manifest", prerequisite,
            "--advanced-gi-qualification-manifest", qualification,
            "--advanced-gi-runtime-evidence-bundle", runtimeEvidence,
            "--simple-ddgi-receiver-feedback-mode", "exact-compacted",
            "--ddgi-opacity-micromap-mode", "auto-qualified",
            "--simple-ddgi-directional-guiding-mode",
                "per-probe-histogram-experiment",
            "--gi-caustic-mode", "world-cache-experiment",
            "--simple-ddgi-near-field-residual-mode",
                "hi-z-half-resolution-experiment",
            "--simple-ddgi-receiver-feedback-qualification-id", "b1-qid",
            "--ddgi-opacity-micromap-qualification-id", "c1-qid",
            "--simple-ddgi-directional-guiding-qualification-id", "c3-qid",
            "--gi-caustic-qualification-id", "c4-qid",
            "--simple-ddgi-near-field-residual-qualification-id", "c5-qid"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.AdvancedGiPrerequisiteManifestPath,
                Is.EqualTo(Path.GetFullPath(prerequisite)));
            Assert.That(options.AdvancedGiQualificationManifestPath,
                Is.EqualTo(Path.GetFullPath(qualification)));
            Assert.That(options.AdvancedGiRuntimeEvidenceBundlePath,
                Is.EqualTo(Path.GetFullPath(runtimeEvidence)));
            Assert.That(options.SimpleDdgiReceiverFeedbackModeOverride,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(options.DdgiOpacityMicromapModeOverride,
                Is.EqualTo(DdgiOpacityMicromapMode.AutoQualified));
            Assert.That(options.SimpleDdgiDirectionalGuidingModeOverride,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment));
            Assert.That(options.GiCausticModeOverride,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(options.SimpleDdgiNearFieldResidualModeOverride,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode
                    .HiZHalfResolutionExperiment));
            Assert.That(options.SimpleDdgiReceiverFeedbackQualificationId,
                Is.EqualTo("b1-qid"));
            Assert.That(options.DdgiOpacityMicromapQualificationId,
                Is.EqualTo("c1-qid"));
            Assert.That(options.SimpleDdgiDirectionalGuidingQualificationId,
                Is.EqualTo("c3-qid"));
            Assert.That(options.GiCausticQualificationId,
                Is.EqualTo("c4-qid"));
            Assert.That(options.SimpleDdgiNearFieldResidualQualificationId,
                Is.EqualTo("c5-qid"));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ParsesAdvancedGiAtomicStartupProfile()
    {
        string profile = Path.Combine(
            Path.GetTempPath(), "advanced-gi-startup-profile.json");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--advanced-gi-startup-profile", profile
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.AdvancedGiStartupProfilePath,
                Is.EqualTo(Path.GetFullPath(profile)));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
            Assert.That(options.FrameCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void ParsesAdvancedGiStartupConfigurationFromEnvironment()
    {
        string prerequisite = Path.Combine(
            Path.GetTempPath(),
            "advanced-gi-prerequisites-environment.json");
        Environment.SetEnvironmentVariable(
            "NJULF_ADVANCED_GI_PREREQUISITE_MANIFEST",
            prerequisite);
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_SIMPLE_DDGI_DIRECTIONAL_GUIDING_MODE",
            "auto-qualified");
        Environment.SetEnvironmentVariable(
            "NJULF_SIMPLE_DDGI_DIRECTIONAL_GUIDING_QUALIFICATION_ID",
            "c3-environment-qid");

        SampleSmokeOptions options =
            SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.AdvancedGiPrerequisiteManifestPath,
                Is.EqualTo(Path.GetFullPath(prerequisite)));
            Assert.That(options.SimpleDdgiDirectionalGuidingModeOverride,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode.AutoQualified));
            Assert.That(options.SimpleDdgiDirectionalGuidingQualificationId,
                Is.EqualTo("c3-environment-qid"));
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
        });
    }

    [Test]
    public void AdvancedGiStartupOptionsRejectUnknownModesAndUnsafeIds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--gi-caustic-mode", "pretend-production"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "--gi-caustic-mode"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--gi-caustic-qualification-id", new string('x', 257)
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "at most 256"));
        });
    }

    [Test]
    public void QualificationCandidate_LocksDurableFurnaceTierBenchmark()
    {
        string report = Path.Combine(
            Path.GetTempPath(),
            "material-gi-candidate-benchmark.json");
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--material-gi-qualification-candidate",
            "--benchmark",
            "--benchmark-report", report,
            "--benchmark-budget-profile", "low"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(
                options.Benchmark.MaterialGiQualificationCandidate,
                Is.True);
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(30));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(120));
            Assert.That(
                options.Benchmark.ReportPath,
                Is.EqualTo(Path.GetFullPath(report)));
            Assert.That(
                options.Benchmark.BudgetProfileOverride,
                Is.EqualTo(RenderBudgetProfileKind.LowSpec1080p30));
            Assert.That(
                options.PerformanceScenario,
                Is.EqualTo(SamplePerformanceScenario.GiSimpleDdgiFurnace));
            Assert.That(
                options.QualityPresetOverride,
                Is.EqualTo(RenderQualityPreset.DdgiHigh));
        });
    }

    [Test]
    public void QualificationCandidate_RequiresReportTierAndLockedFrames()
    {
        string report = Path.Combine(
            Path.GetTempPath(),
            "material-gi-candidate-benchmark.json");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-qualification-candidate",
                    "--benchmark",
                    "--benchmark-report", report
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "--benchmark-budget-profile"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-qualification-candidate",
                    "--benchmark-budget-profile", "high"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "--benchmark-report"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-qualification-candidate",
                    "--benchmark-report", report,
                    "--benchmark-budget-profile", "ultra",
                    "--benchmark-warmup-frames", "29"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "30-frame warmup"));
        });
    }

    [Test]
    public void QualificationCandidate_RejectsApprovedManifestAndForeignWorkload()
    {
        string report = Path.Combine(
            Path.GetTempPath(),
            "material-gi-candidate-benchmark.json");
        string manifest = Path.Combine(
            Path.GetTempPath(),
            "material-gi-release-qualification.json");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-qualification-candidate",
                    "--material-gi-qualification-manifest", manifest,
                    "--benchmark-report", report,
                    "--benchmark-budget-profile", "medium"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "cannot be combined"));
            Assert.That(
                () => SampleSmokeOptionsParser.Parse(
                [
                    "--material-gi-qualification-candidate",
                    "--benchmark-report", report,
                    "--benchmark-budget-profile", "medium",
                    "--performance-scenario", "gi-cornell-room"
                ]),
                Throws.ArgumentException.With.Message.Contains(
                    "GiSimpleDdgiFurnace"));
        });
    }

    [Test]
    public void QualificationManifestRejectsNonShippingMaterialCapture()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[]
            {
                "--material-gi-qualification-manifest", Path.Combine(Path.GetTempPath(), "qualification.json"),
                "--material-gi-capture-dir", Path.Combine(Path.GetTempPath(), "capture")
            }),
            Throws.ArgumentException.With.Message.Contains("qualified shipping rollout"));
    }

    [Test]
    public void UnknownOption_IsRejectedBeforeAFollowingKnownOptionCanBeConsumed()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--material-gi-qualificaton-manifest",
                "--smoke-mode",
                "startup"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "Unknown renderer option '--material-gi-qualificaton-manifest'"));
    }

    [Test]
    public void MissingValue_DoesNotConsumeTheFollowingOption()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(
            [
                "--material-gi-qualification-manifest",
                "--smoke-mode",
                "startup"
            ]),
            Throws.ArgumentException.With.Message.Contains(
                "found option '--smoke-mode' instead"));
    }

    [Test]
    public void BooleanPresenceOptions_HonorExplicitFalse()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
        [
            "--force-missing-assets=false",
            "--fail-on-validation-message=false"
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(options.ForceMissingAssets, Is.False);
            Assert.That(options.FailOnValidationMessage, Is.False);
        });
    }

    [TestCase("--benchmark=treu", "--benchmark")]
    [TestCase("--gpu-timing=enabled", "--gpu-timing")]
    [TestCase("--force-missing-assets=2", "--force-missing-assets")]
    public void BooleanOptions_RejectInvalidValuesInsteadOfSilentlyDisablingAGate(
        string argument,
        string optionName)
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse([argument]),
            Throws.ArgumentException.With.Message.Contains(
                $"{optionName} requires a boolean value"));
    }

    [Test]
    public void BooleanEnvironmentOptions_RejectInvalidValues()
    {
        Environment.SetEnvironmentVariable(
            "NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE",
            "enabled");

        Assert.That(
            () => SampleSmokeOptionsParser.Parse(Array.Empty<string>()),
            Throws.ArgumentException.With.Message.Contains(
                "NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE requires a boolean value"));
    }
}
