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
    public void RejectsInvalidMode()
    {
        Assert.That(
            () => SampleSmokeOptionsParser.Parse(new[] { "--smoke-mode", "chaos" }),
            Throws.ArgumentException.With.Message.Contains("Invalid smoke mode"));
    }
}
