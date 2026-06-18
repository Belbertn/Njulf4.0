using System;
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
