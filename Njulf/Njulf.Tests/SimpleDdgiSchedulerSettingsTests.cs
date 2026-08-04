using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSchedulerSettingsTests
{
    [Test]
    public void SchedulerModeSanitizesUnknownValuesToCpuReference()
    {
        Assert.Multiple(() =>
        {
            Assert.That(((SimpleDdgiSchedulerMode)99u).Sanitize(), Is.EqualTo(SimpleDdgiSchedulerMode.CpuReference));
            Assert.That(SimpleDdgiSchedulerMode.CpuReference.IsGpuMode(), Is.False);
            Assert.That(SimpleDdgiSchedulerMode.GpuMirror.IsGpuMode(), Is.True);
            Assert.That(SimpleDdgiSchedulerMode.GpuResident.IsGpuMode(), Is.True);
        });
    }

    [Test]
    public void SchedulerModeRoundTripsThroughRenderSettingsFile()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"simple-ddgi-scheduler-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiSchedulerMode = SimpleDdgiSchedulerMode.GpuResident;
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.That(
                loaded.GlobalIllumination.SimpleDdgiSchedulerMode,
                Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SchedulerModeHasExplicitSmokeOverride()
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(
            new[] { "--simple-ddgi-scheduler-mode=gpu-resident" });

        Assert.Multiple(() =>
        {
            Assert.That(options.SimpleDdgiSchedulerModeOverride, Is.EqualTo(SimpleDdgiSchedulerMode.GpuResident));
            Assert.That(options.Enabled, Is.True);
            Assert.That(options.Mode, Is.EqualTo(SampleSmokeMode.Startup));
        });
    }
}
