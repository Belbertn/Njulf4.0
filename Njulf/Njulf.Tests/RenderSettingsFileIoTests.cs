using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
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
            settings.Transparency.ThickTransmissionMode =
                ThickTransmissionMode.Approximation;
            settings.Transparency.DispersionMode =
                DispersionMode.RgbTriplet;
            settings.Transparency.ThickTransmissionMaximumInterfaces = 6;
            settings.Transparency.ThickTransmissionMaximumMediaDepth = 3;
            settings.Transparency.ThickTransmissionMaximumCandidatesPerInterface = 24;
            settings.Transparency.ThickTransmissionMaximumDistance = 175f;
            settings.Transparency.SceneReflectionRayTaskBudget = 77_777;
            settings.Transparency.SceneReflectionSsrSampleBudget = 5_242_880;
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
                    loaded.Transparency.ThickTransmissionMode,
                    Is.EqualTo(ThickTransmissionMode.Approximation));
                Assert.That(
                    loaded.Transparency.DispersionMode,
                    Is.EqualTo(DispersionMode.RgbTriplet));
                Assert.That(
                    loaded.Transparency.ThickTransmissionMaximumInterfaces,
                    Is.EqualTo(6));
                Assert.That(
                    loaded.Transparency.ThickTransmissionMaximumMediaDepth,
                    Is.EqualTo(3));
                Assert.That(
                    loaded.Transparency.ThickTransmissionMaximumCandidatesPerInterface,
                    Is.EqualTo(24));
                Assert.That(
                    loaded.Transparency.ThickTransmissionMaximumDistance,
                    Is.EqualTo(175f));
                Assert.That(
                    loaded.Transparency.SceneReflectionRayTaskBudget,
                    Is.EqualTo(77_777));
                Assert.That(
                    loaded.Transparency.SceneReflectionSsrSampleBudget,
                    Is.EqualTo(5_242_880));
                Assert.That(
                    loaded.Decals.ReceiveGlobalIllumination,
                    Is.True);
                Assert.That(loaded.Decals.ReceiveShadows, Is.False);
                Assert.That(RenderSettings.SerializationVersion, Is.EqualTo(20));
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
            shadows.DirectionalPcfRadiusMode =
                DirectionalPcfRadiusMode.WorldSpaceAdaptive;
            shadows.DirectionalShadowMapSize = 4096;
            shadows.DirectionalCascadeCount = 4;
            shadows.MaxShadowDistance = 220f;
            shadows.DirectionalCascadeBlendFraction = 0.18f;
            shadows.DirectionalCascadeSplitLambda = 0.72f;
            shadows.DirectionalCasterExtrusionDistance = 350f;
            shadows.DirectionalContactShadowDistance = 4.5f;
            shadows.DirectionalSoftAngularDiameterScale = 2.5f;
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
                Assert.That(loaded.DirectionalPcfRadiusMode,
                    Is.EqualTo(DirectionalPcfRadiusMode.WorldSpaceAdaptive));
                Assert.That(loaded.DirectionalShadowMapSize, Is.EqualTo(4096u));
                Assert.That(loaded.DirectionalCascadeCount, Is.EqualTo(4));
                Assert.That(loaded.MaxShadowDistance, Is.EqualTo(220f));
                Assert.That(loaded.DirectionalCascadeBlendFraction, Is.EqualTo(0.18f));
                Assert.That(loaded.DirectionalCascadeSplitLambda, Is.EqualTo(0.72f));
                Assert.That(loaded.DirectionalCasterExtrusionDistance, Is.EqualTo(350f));
                Assert.That(loaded.DirectionalContactShadowDistance, Is.EqualTo(4.5f));
                Assert.That(loaded.DirectionalSoftAngularDiameterScale,
                    Is.EqualTo(2.5f));
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
                Assert.That(loaded.DirectionalPcfRadiusMode,
                    Is.EqualTo(DirectionalPcfRadiusMode.Constant));
                Assert.That(loaded.DirectionalSoftAngularDiameterScale,
                    Is.EqualTo(1f));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version12ShadowObjectMigratesNewControlsToLegacyParity()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-12-shadows.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 12,
                  "QualityPreset": 3,
                  "Shadows": {
                    "DirectionalShadowsEnabled": true,
                    "RequestedDirectionalShadowMode": 3,
                    "DirectionalFilterMode": 1,
                    "DirectionalBiasMode": 1
                  }
                }
                """);

            ShadowSettings loaded = RenderSettings.Load(path).Shadows;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.DirectionalPcfRadiusMode,
                    Is.EqualTo(DirectionalPcfRadiusMode.Constant));
                Assert.That(loaded.DirectionalSoftAngularDiameterScale,
                    Is.EqualTo(1f));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version18WithoutSceneReflectionBudgetUsesPresetDefault()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-18-transparency.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 18,
                  "QualityPreset": 2,
                  "Transparency": {
                    "Enabled": true,
                    "SampleReflections": true
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.QualityPreset,
                    Is.EqualTo(RenderQualityPreset.High));
                Assert.That(loaded.Transparency.SampleReflections, Is.True);
                Assert.That(loaded.Transparency.SceneReflectionRayTaskBudget,
                    Is.EqualTo(65_536));
                Assert.That(loaded.Transparency.SceneReflectionSsrSampleBudget,
                    Is.EqualTo(4_194_304));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version19WithoutSsrBudgetUsesPresetDefault()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-19-transparency.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 19,
                  "QualityPreset": 3,
                  "Transparency": {
                    "Enabled": true,
                    "SampleReflections": true,
                    "SceneReflectionRayTaskBudget": 131072
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.QualityPreset,
                    Is.EqualTo(RenderQualityPreset.Ultra));
                Assert.That(loaded.Transparency.SceneReflectionRayTaskBudget,
                    Is.EqualTo(131_072));
                Assert.That(loaded.Transparency.SceneReflectionSsrSampleBudget,
                    Is.EqualTo(8_388_608));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveLoad_PreservesFroxelFogContract()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "froxel-fog.json");
        try
        {
            var settings = new RenderSettings();
            settings.ApplyQualityPreset(RenderQualityPreset.Medium);
            settings.Fog.Enabled = true;
            settings.Fog.Technique = FogTechnique.Froxel;
            settings.Fog.DebugView = FogDebugView.HistoryConfidence;
            // A scene/settings file must not be able to self-qualify a
            // production renderer profile.
            settings.Fog.Volumetric.SingleScatteringQualified = true;
            settings.Fog.Volumetric.MaxDistance = 480f;
            settings.Fog.Volumetric.BaseExtinctionPerMeter = 0.027f;
            settings.Fog.Volumetric.Anisotropy = 0.61f;
            settings.Fog.Volumetric.GlobalWind = new(1.5f, -0.25f, 3f);
            settings.Fog.Volumetric.MultipleScatteringIterations = 2;
            settings.Fog.Volumetric.MultipleScatteringEnergyLimit = 0.35f;
            settings.Fog.Volumetric.DebugProjection =
                FogDebugProjection.Slice;
            settings.Fog.Volumetric.DebugSlice = 17;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(loaded.Fog.Enabled, Is.True);
                Assert.That(loaded.Fog.Technique, Is.EqualTo(FogTechnique.Froxel));
                Assert.That(loaded.Fog.DebugView,
                    Is.EqualTo(FogDebugView.HistoryConfidence));
                Assert.That(loaded.Fog.Volumetric.SingleScatteringQualified,
                    Is.False);
                Assert.That(loaded.Fog.Volumetric.MultipleScatteringQualified,
                    Is.False);
                Assert.That(loaded.Fog.Volumetric.MaxDistance, Is.EqualTo(480f));
                Assert.That(loaded.Fog.Volumetric.BaseExtinctionPerMeter,
                    Is.EqualTo(0.027f));
                Assert.That(loaded.Fog.Volumetric.Anisotropy, Is.EqualTo(0.61f));
                Assert.That(loaded.Fog.Volumetric.GlobalWind,
                    Is.EqualTo(new Njulf.Core.Math.Vector3(1.5f, -0.25f, 3f)));
                Assert.That(loaded.Fog.Volumetric.MultipleScatteringIterations,
                    Is.EqualTo(2));
                Assert.That(loaded.Fog.Volumetric.MultipleScatteringEnergyLimit,
                    Is.EqualTo(0.35f));
                Assert.That(loaded.Fog.Volumetric.DebugProjection,
                    Is.EqualTo(FogDebugProjection.Slice));
                Assert.That(loaded.Fog.Volumetric.DebugSlice, Is.EqualTo(17));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version13PreservesC5ModeButInvalidatesEvidenceAndDefaultsBalanced()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-13-c5.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 13,
                  "GlobalIllumination": {
                    "SimpleDdgiNearFieldResidualMode": 4,
                    "SimpleDdgiNearFieldResidualQualificationId": "stale-v5-evidence"
                  }
                }
                """);

            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualityPreset,
                    Is.EqualTo(SimpleDdgiNearFieldResidualQualityPreset.Balanced));
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualificationId,
                    Is.Empty);
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                    Is.False);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version14UpgradesAutoQualifiedC5AndPreservesV2Settings()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-14-auto-c5.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 14,
                  "GlobalIllumination": {
                    "SimpleDdgiNearFieldResidualMode": 3,
                    "SimpleDdgiNearFieldResidualQualityPreset": 2,
                    "SimpleDdgiNearFieldResidualAdvancedOverridesEnabled": true,
                    "SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters": 12.0,
                    "SimpleDdgiNearFieldResidualRaysPerPixel": 3,
                    "SimpleDdgiNearFieldResidualFilterIterationCount": 3,
                    "SimpleDdgiNearFieldResidualIntensity": 1.25,
                    "SimpleDdgiNearFieldResidualQualificationId": "c5-v6-current"
                  }
                }
                """);

            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualityPreset,
                    Is.EqualTo(SimpleDdgiNearFieldResidualQualityPreset.Quality));
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                    Is.True);
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters,
                    Is.EqualTo(12.0f));
                Assert.That(loaded.SimpleDdgiNearFieldResidualRaysPerPixel,
                    Is.EqualTo(3));
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualFilterIterationCount,
                    Is.EqualTo(3));
                Assert.That(loaded.SimpleDdgiNearFieldResidualIntensity,
                    Is.EqualTo(1.25f));
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualificationId,
                    Is.EqualTo("c5-v6-current"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version14PreservesExplicitC5Off()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "version-14-off-c5.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 14,
                  "GlobalIllumination": {
                    "SimpleDdgiNearFieldResidualMode": 0
                  }
                }
                """);

            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;

            Assert.That(loaded.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode.Off));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveLoad_C5CurrentControlsRoundTripAfterBoundedClamping()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "current-c5.json");
        try
        {
            var settings = new RenderSettings();
            GlobalIlluminationSettings c5 = settings.GlobalIllumination;
            c5.SimpleDdgiNearFieldResidualQualityPreset =
                SimpleDdgiNearFieldResidualQualityPreset.Quality;
            c5.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled = true;
            c5.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters = 100.0f;
            c5.SimpleDdgiNearFieldResidualRaysPerPixel = 10;
            c5.SimpleDdgiNearFieldResidualFilterIterationCount = -1;
            c5.SimpleDdgiNearFieldResidualIntensity = float.NaN;
            c5.SimpleDdgiNearFieldResidualQualificationId = "c5-v6-current";

            settings.Save(path);
            GlobalIlluminationSettings loaded = RenderSettings.Load(path)
                .GlobalIllumination;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualityPreset,
                    Is.EqualTo(SimpleDdgiNearFieldResidualQualityPreset.Quality));
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                    Is.True);
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters,
                    Is.EqualTo(16.0f));
                Assert.That(loaded.SimpleDdgiNearFieldResidualRaysPerPixel,
                    Is.EqualTo(4));
                Assert.That(
                    loaded.SimpleDdgiNearFieldResidualFilterIterationCount,
                    Is.Zero);
                Assert.That(loaded.SimpleDdgiNearFieldResidualIntensity,
                    Is.Zero);
                Assert.That(loaded.SimpleDdgiNearFieldResidualQualificationId,
                    Is.EqualTo("c5-v6-current"));
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
    public void SaveLoad_PreservesRecursiveCertifiedGlossyTransportIntent()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "recursive-certified-transport.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.SimpleDdgiDirectionalRadianceMode =
                SimpleDdgiDirectionalRadianceMode.L2;
            settings.GlobalIllumination.SimpleDdgiGlossyTransportMode =
                SimpleDdgiGlossyTransportMode.RecursiveCertified;

            settings.Save(path);
            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiDirectionalRadianceMode,
                    Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiGlossyTransportMode,
                    Is.EqualTo(SimpleDdgiGlossyTransportMode.RecursiveCertified));
                Assert.That(
                    loaded.GlobalIllumination.ContentDependentSettingsMigrationDiagnostic,
                    Is.Null);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version11RecursiveExperimentalMigratesToOneBounce()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "legacy-recursive-transport.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 11,
                  "GlobalIllumination": {
                    "SimpleDdgiDirectionalRadianceMode": 2,
                    "SimpleDdgiGlossyTransportMode": 3
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiDirectionalRadianceMode,
                    Is.EqualTo(SimpleDdgiDirectionalRadianceMode.L2));
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiGlossyTransportMode,
                    Is.EqualTo(SimpleDdgiGlossyTransportMode.OneBounce));
                Assert.That(
                    loaded.GlobalIllumination.ContentDependentSettingsMigrationDiagnostic,
                    Does.Contain("Legacy RecursiveExperimental"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Load_Version12RetainsRecursiveCertifiedTransport()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "certified-recursive-transport.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "Version": 12,
                  "GlobalIllumination": {
                    "SimpleDdgiDirectionalRadianceMode": 2,
                    "SimpleDdgiGlossyTransportMode": 3
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination.SimpleDdgiGlossyTransportMode,
                    Is.EqualTo(SimpleDdgiGlossyTransportMode.RecursiveCertified));
                Assert.That(
                    loaded.GlobalIllumination.ContentDependentSettingsMigrationDiagnostic,
                    Is.Null);
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

    [Test]
    public void QualityPresets_UseAdaptiveTentDirectionalFilteringAboveLow()
    {
        var settings = new RenderSettings();

        settings.ApplyQualityPreset(RenderQualityPreset.Low);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Shadows.DirectionalFilterMode,
                Is.EqualTo(DirectionalShadowFilterMode.LegacyBoxPcf));
            Assert.That(settings.Shadows.DirectionalBiasMode,
                Is.EqualTo(DirectionalShadowBiasMode.Legacy));
            Assert.That(settings.Shadows.DirectionalPcfRadiusMode,
                Is.EqualTo(DirectionalPcfRadiusMode.Constant));
        });

        foreach (RenderQualityPreset preset in new[]
                 {
                     RenderQualityPreset.Medium,
                     RenderQualityPreset.High,
                     RenderQualityPreset.DdgiHigh,
                     RenderQualityPreset.Ultra
                 })
        {
            settings.ApplyQualityPreset(preset);
            Assert.Multiple(() =>
            {
                Assert.That(settings.Shadows.DirectionalFilterMode,
                    Is.EqualTo(DirectionalShadowFilterMode.TentPcf),
                    preset.ToString());
                Assert.That(settings.Shadows.DirectionalBiasMode,
                    Is.EqualTo(DirectionalShadowBiasMode.WorldTexelScaled),
                    preset.ToString());
                Assert.That(settings.Shadows.DirectionalPcfRadiusMode,
                    Is.EqualTo(DirectionalPcfRadiusMode.WorldSpaceAdaptive),
                    preset.ToString());
            });
        }
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
