using System;
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
        Environment.SetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_GPU_TIMING", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_INDIRECT_DISPATCH", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_LOD", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_GPU_SHADOW_COMPACTION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE_SUBMISSION_VALIDATION", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_TRANSPARENCY_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR", null);
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
