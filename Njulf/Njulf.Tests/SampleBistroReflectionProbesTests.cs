using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleReflectionPolicyTests
{
    [Test]
    public void Apply_DisablesManualProbeCapacityAndCaptureWork()
    {
        var settings = new RenderSettings();
        settings.Reflections.MaxProbes = 8;
        settings.Reflections.CaptureOnLoad = true;
        settings.Reflections.CaptureIncludesDdgi = true;
        settings.Reflections.MaxProbeCapturesPerFrame = 2;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = 6;
        settings.Reflections.MaxProbePrefilterMipsPerFrame = 8;
        settings.Reflections.ReflectionCaptureGpuBudgetMicroseconds = 500;

        SampleReflectionPolicy.Apply(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Reflections.MaxProbes, Is.Zero);
            Assert.That(settings.Reflections.CaptureOnLoad, Is.False);
            Assert.That(settings.Reflections.CaptureIncludesDdgi, Is.False);
            Assert.That(settings.Reflections.MaxProbeCapturesPerFrame, Is.Zero);
            Assert.That(settings.Reflections.MaxProbeCaptureFacesPerFrame, Is.Zero);
            Assert.That(settings.Reflections.MaxProbePrefilterMipsPerFrame, Is.Zero);
            Assert.That(settings.Reflections.ReflectionCaptureGpuBudgetMicroseconds, Is.Zero);
        });
    }

    [Test]
    public void EnsureProbeFree_RejectsAuthoredProbe()
    {
        using var scene = new Scene { Name = "Manual probe fixture" };
        scene.Add(new ReflectionProbe());

        Assert.That(
            () => SampleReflectionPolicy.EnsureProbeFree(scene),
            Throws.InvalidOperationException.With.Message.Contains(
                "must use SSR, ray-query recovery, and the global environment"));
    }
}
