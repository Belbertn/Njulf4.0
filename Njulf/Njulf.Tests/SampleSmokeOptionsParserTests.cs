using System;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
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
        Environment.SetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_INDIRECT_DISPATCH", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_LOD", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_SHADOW_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_ASYNC_COMPUTE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_TRANSPARENCY_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REPORT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_VALIDATION", null);
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
    [TestCase("gi-sponza-right-wall-stationary", SamplePerformanceScenario.GiSponzaRightWallStationary)]
    [TestCase("gi-thin-wall-leak-test", SamplePerformanceScenario.GiThinWallLeakTest)]
    [TestCase("gi-moving-point-light", SamplePerformanceScenario.GiMovingPointLight)]
    [TestCase("gi-moving-rigid-object", SamplePerformanceScenario.GiMovingRigidObject)]
    [TestCase("gi-bright-exterior-room", SamplePerformanceScenario.GiBrightExteriorRoom)]
    [TestCase("gi-long-corridor-occlusion", SamplePerformanceScenario.GiLongCorridorOcclusion)]
    [TestCase("gi-emissive-material-room", SamplePerformanceScenario.GiEmissiveMaterialRoom)]
    [TestCase("gi-local-volume-streaming", SamplePerformanceScenario.GiLocalVolumeStreaming)]
    [TestCase("gi-fast-traversal-teleport", SamplePerformanceScenario.GiFastTraversalTeleport)]
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
    public void GlobalIlluminationValidationSettings_EnableVisibleRayQueryHybridPath()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.High);

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, SamplePerformanceScenario.GiCornellRoom);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseSsgi, Is.False);
            Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.EffectiveUseSsgi, Is.False);
            Assert.That(settings.GlobalIllumination.EffectiveUseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.EffectiveUseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
            Assert.That(settings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.5f));
            Assert.That(settings.GlobalIllumination.EnvironmentFallbackIntensity, Is.EqualTo(0.65f));
            Assert.That(settings.GlobalIllumination.MaxBounceDistance, Is.EqualTo(10.0f));
            Assert.That(settings.GlobalIllumination.DdgiThinWallPolicyEnabled, Is.True);
            Assert.That(settings.GlobalIllumination.DdgiRoomSpacingScaledBiasEnabled, Is.True);
            Assert.That(settings.GlobalIllumination.DdgiThinWallLeakClampStrength, Is.EqualTo(0.9f));
            Assert.That(settings.GlobalIllumination.DdgiThinWallProxyThickness, Is.EqualTo(0.12f));
            Assert.That(settings.GlobalIllumination.TemporalEnabled, Is.False);
            Assert.That(settings.GlobalIllumination.DenoiserEnabled, Is.False);
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
                    "ssgi-trace-gpu-us",
                    "ssgi-temporal-gpu-us",
                    "ssgi-spatial-gpu-us"
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
            Assert.That(settings.GlobalIllumination.Mode, Is.EqualTo(GlobalIlluminationMode.Hybrid));
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
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
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
            "--benchmark-measure-frames", "8"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.Benchmark.ReportPath, Is.EqualTo(System.IO.Path.GetFullPath(reportPath)));
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(0));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(8));
            Assert.That(options.EnableGpuTiming, Is.True);
            Assert.That(options.Enabled, Is.True);
        });
    }

    [Test]
    public void ParsesBenchmarkEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", "true");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_WARMUP_FRAMES", "2");
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_MEASURE_FRAMES", "5");

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(options.Benchmark.Enabled, Is.True);
            Assert.That(options.Benchmark.WarmupFrameCount, Is.EqualTo(2));
            Assert.That(options.Benchmark.MeasureFrameCount, Is.EqualTo(5));
            Assert.That(options.EnableGpuTiming, Is.True);
        });
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
}
