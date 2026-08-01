using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderSettingsFileIoTests
{
    [Test]
    public void Load_RejectsOversizedFileBeforeParsing()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "oversized.json");
        try
        {
            using (FileStream stream = File.Create(path))
                stream.SetLength(RenderSettings.MaximumSettingsFileBytes + 1L);

            Assert.That(
                () => RenderSettings.Load(path),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("valid range"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Save_AtomicallyReplacesSettingsAndLeavesNoTemporaryArtifact()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new RenderSettings
            {
                Exposure = 1.25f
            };
            settings.Save(path);
            settings.Exposure = 2.5f;
            settings.Save(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    RenderSettings.Load(path).Exposure,
                    Is.EqualTo(2.5f));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp"),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveLoad_PreservesIndependentLayeredReceiverGiPolicies()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "layered-gi.json");
        try
        {
            var settings = new RenderSettings();
            settings.Transparency.ReceiveGlobalIllumination = false;
            settings.Decals.ReceiveGlobalIllumination = true;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.Transparency.ReceiveGlobalIllumination,
                    Is.False);
                Assert.That(
                    loaded.Decals.ReceiveGlobalIllumination,
                    Is.True);
                Assert.That(
                    File.ReadAllText(path),
                    Does.Contain($"\"Version\": {RenderSettings.SerializationVersion}"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveLoad_PreservesProceduralAtmosphereAuthoringContract()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "atmosphere.json");
        try
        {
            var settings = new RenderSettings();
            EnvironmentSettings environment = settings.Environment;
            environment.Enabled = true;
            environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
            environment.TexturePrecision = EnvironmentTexturePrecision.Float32;
            environment.SunDriver = ProceduralSkySunDriver.AstronomicalTime;
            environment.AnimateTimeOfDay = true;
            environment.Turbidity = 6.25f;
            environment.GroundAlbedo = new Njulf.Core.Math.Vector3(0.1f, 0.3f, 0.5f);
            environment.TimeOfDayHours = 19.75f;
            environment.LatitudeDegrees = 67.28f;
            environment.DayOfYear = 305;
            environment.NorthOffsetDegrees = 37.5f;
            environment.TimeScale = 1800.0f;
            environment.DirectSunDirection = new Njulf.Core.Math.Vector3(0.4f, 0.5f, -0.6f);
            environment.GiSunStepDegrees = 0.4f;
            environment.GiTargetSourceSweepSeconds = 4.5f;
            environment.SpecularPrefilterMipsPerFrame = 3;
            environment.SpecularPrefilterTransitionFrames = 12;
            environment.PrefilteredSize = 256;

            settings.Save(path);
            EnvironmentSettings loaded = RenderSettings.Load(path).Environment;

            Assert.Multiple(() =>
            {
                Assert.That(loaded.TexturePrecision, Is.EqualTo(EnvironmentTexturePrecision.Float32));
                Assert.That(loaded.SunDriver, Is.EqualTo(ProceduralSkySunDriver.AstronomicalTime));
                Assert.That(loaded.AnimateTimeOfDay, Is.True);
                Assert.That(loaded.Turbidity, Is.EqualTo(6.25f));
                Assert.That(loaded.GroundAlbedo, Is.EqualTo(environment.GroundAlbedo));
                Assert.That(loaded.TimeOfDayHours, Is.EqualTo(19.75f));
                Assert.That(loaded.LatitudeDegrees, Is.EqualTo(67.28f));
                Assert.That(loaded.DayOfYear, Is.EqualTo(305));
                Assert.That(loaded.NorthOffsetDegrees, Is.EqualTo(37.5f));
                Assert.That(loaded.TimeScale, Is.EqualTo(1800.0f));
                Assert.That(loaded.DirectSunDirection, Is.EqualTo(environment.DirectSunDirection));
                Assert.That(loaded.GiSunStepDegrees, Is.EqualTo(0.4f));
                Assert.That(loaded.GiTargetSourceSweepSeconds, Is.EqualTo(4.5f));
                Assert.That(loaded.SpecularPrefilterMipsPerFrame, Is.EqualTo(3));
                Assert.That(loaded.SpecularPrefilterTransitionFrames, Is.EqualTo(12));
                Assert.That(loaded.PrefilteredSize, Is.EqualTo(256u));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void QualityPresets_EnableLayeredDdgiOnlyWhenDdgiIsAvailable()
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(RenderQualityPreset.Low);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.False);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.False);

        settings.ApplyQualityPreset(RenderQualityPreset.Medium);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.False);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.False);

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.True);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.True);

        settings.ApplyQualityPreset(RenderQualityPreset.Ultra);
        Assert.That(settings.Transparency.ReceiveGlobalIllumination, Is.True);
        Assert.That(settings.Decals.ReceiveGlobalIllumination, Is.True);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-render-settings-io-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
