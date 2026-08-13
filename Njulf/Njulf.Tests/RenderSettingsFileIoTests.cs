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
            settings.Decals.ReceiveShadows = false;

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
                Assert.That(loaded.Decals.ReceiveShadows, Is.False);
                Assert.That(RenderSettings.SerializationVersion, Is.EqualTo(11));
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
    public void SaveLoad_PreservesCompleteDirectionalShadowContract()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "directional-shadows.json");
        try
        {
            var settings = new RenderSettings();
            ShadowSettings shadows = settings.Shadows;
            shadows.DirectionalShadowsEnabled = true;
            shadows.RequestedDirectionalShadowMode = DirectionalShadowMode.RayQuerySoft;
            shadows.DirectionalFilterMode = DirectionalShadowFilterMode.TentPcf;
            shadows.DirectionalBiasMode = DirectionalShadowBiasMode.WorldTexelScaled;
            shadows.DirectionalShadowMapSize = 4096;
            shadows.DirectionalCascadeCount = 4;
            shadows.MaxShadowDistance = 220f;
            shadows.DirectionalCascadeBlendFraction = 0.18f;
            shadows.DirectionalCascadeSplitLambda = 0.72f;
            shadows.DirectionalCasterExtrusionDistance = 350f;
            shadows.DirectionalContactShadowDistance = 4.5f;
            shadows.NormalBias = 0.045f;
            shadows.SlopeScaledDepthBias = 2.25f;
            shadows.ConstantDepthBias = 0.00075f;
            shadows.PcfRadius = 2;

            settings.Save(path);
            string json = File.ReadAllText(path);
            ShadowSettings loaded = RenderSettings.Load(path).Shadows;

            Assert.Multiple(() =>
            {
                Assert.That(loaded.RequestedDirectionalShadowMode,
                    Is.EqualTo(DirectionalShadowMode.RayQuerySoft));
                Assert.That(loaded.DirectionalFilterMode,
                    Is.EqualTo(DirectionalShadowFilterMode.TentPcf));
                Assert.That(loaded.DirectionalBiasMode,
                    Is.EqualTo(DirectionalShadowBiasMode.WorldTexelScaled));
                Assert.That(loaded.DirectionalShadowMapSize, Is.EqualTo(4096u));
                Assert.That(loaded.DirectionalCascadeCount, Is.EqualTo(4));
                Assert.That(loaded.MaxShadowDistance, Is.EqualTo(220f));
                Assert.That(loaded.DirectionalCascadeBlendFraction, Is.EqualTo(0.18f));
                Assert.That(loaded.DirectionalCascadeSplitLambda, Is.EqualTo(0.72f));
                Assert.That(loaded.DirectionalCasterExtrusionDistance, Is.EqualTo(350f));
                Assert.That(loaded.DirectionalContactShadowDistance, Is.EqualTo(4.5f));
                Assert.That(loaded.NormalBias, Is.EqualTo(0.045f));
                Assert.That(loaded.SlopeScaledDepthBias, Is.EqualTo(2.25f));
                Assert.That(loaded.ConstantDepthBias, Is.EqualTo(0.00075f));
                Assert.That(loaded.PcfRadius, Is.EqualTo(2));
                Assert.That(json, Does.Contain("\"Shadows\""));
                Assert.That(json, Does.Not.Contain("\"ShadowsEnabled\""));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version10ShadowSwitchRetainsLegacyDirectionalModes()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "legacy-shadows.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 10,
                  "QualityPreset": 3,
                  "ShadowsEnabled": false
                }
                """);

            ShadowSettings loaded = RenderSettings.Load(path).Shadows;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.DirectionalShadowsEnabled, Is.False);
                Assert.That(loaded.RequestedDirectionalShadowMode,
                    Is.EqualTo(DirectionalShadowMode.Cascaded));
                Assert.That(loaded.DirectionalFilterMode,
                    Is.EqualTo(DirectionalShadowFilterMode.LegacyBoxPcf));
                Assert.That(loaded.DirectionalBiasMode,
                    Is.EqualTo(DirectionalShadowBiasMode.Legacy));
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
    public void SaveLoad_PreservesCertifiedTransportControlsAndOmitsLegacyGateNames()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "transport-controls.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance = 0.0625f;
            settings.GlobalIllumination.SimpleDdgiTransportAcceleratedSweepCount = 4;
            settings.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled = false;
            settings.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled = false;

            settings.Save(path);
            string json = File.ReadAllText(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance,
                    Is.EqualTo(0.0625f));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportAcceleratedSweepCount,
                    Is.EqualTo(4));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportAccelerationEnabled,
                    Is.False);
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportTailCertificationEnabled,
                    Is.False);
                Assert.That(json, Does.Contain("SimpleDdgiTransportTailRelativeTolerance"));
                Assert.That(json, Does.Contain("SimpleDdgiTransportAcceleratedSweepCount"));
                Assert.That(json, Does.Not.Contain("SimpleDdgiTransportResidualThreshold"));
                Assert.That(json, Does.Not.Contain("SimpleDdgiTransportMaximumSolverGenerations"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveLoad_PreservesExplicitSimpleDdgiRepresentationRollback()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "ddgi-representation-rollback.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiStoragePackingMode =
                SimpleDdgiStoragePackingMode.Legacy;
            settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled = true;
            settings.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode =
                SimpleDdgiSampledAtlasCoverageMode.FullCanonical;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIllumination.SimpleDdgiStoragePackingMode,
                    Is.EqualTo(SimpleDdgiStoragePackingMode.Legacy));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSampledAtlasEnabled,
                    Is.True);
                Assert.That(loaded.GlobalIllumination.SimpleDdgiSampledAtlasCoverageMode,
                    Is.EqualTo(SimpleDdgiSampledAtlasCoverageMode.FullCanonical));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_MigratesLegacyTransportNamesAndSaveWritesOnlyCurrentNames()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "legacy-transport-controls.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 8,
                  "GlobalIllumination": {
                    "SimpleDdgiTransportResidualThreshold": 0.07,
                    "SimpleDdgiTransportMaximumSolverGenerations": 3
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportTailRelativeTolerance,
                    Is.EqualTo(0.07f));
                Assert.That(loaded.GlobalIllumination.SimpleDdgiTransportMaximumSolverGenerations,
                    Is.EqualTo(3));
            });

            loaded.Save(path);
            string migratedJson = File.ReadAllText(path);
            Assert.Multiple(() =>
            {
                Assert.That(migratedJson, Does.Contain("SimpleDdgiTransportTailRelativeTolerance"));
                Assert.That(migratedJson, Does.Not.Contain("SimpleDdgiTransportResidualThreshold"));
                Assert.That(migratedJson, Does.Not.Contain("SimpleDdgiTransportMaximumSolverGenerations"));
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
