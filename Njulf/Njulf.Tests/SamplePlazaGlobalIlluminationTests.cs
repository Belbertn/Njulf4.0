using System;
using System.Reflection;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SamplePlazaGlobalIlluminationTests
{
    private const string DenseAlleyVolumeName = "Dense Alley DDGI";

    [Test]
    public void ConfigureRenderSettings_UsesGenericDdgiHighCameraRelativeProfile()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new Vector3(-1.0f),
            new Vector3(1.0f),
            0.5f));

        ConfigurePlazaRenderSettings(settings);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        int totalClipmapProbes =
            gi.DdgiClipmapProbeCountX *
            gi.DdgiClipmapProbeCountY *
            gi.DdgiClipmapProbeCountZ *
            gi.DdgiClipmapCascadeCount;

        Assert.Multiple(() =>
        {
            Assert.That(gi.DdgiSimpleEnabled, Is.True);
            Assert.That(gi.EffectiveUseSimpleDdgi, Is.True);
            Assert.That(gi.EffectiveUseDdgi, Is.False);
            Assert.That(gi.DdgiCameraRelativeEnabled, Is.True);
            Assert.That(gi.DdgiSchedulerMode, Is.EqualTo(DdgiSchedulerMode.Gpu));
            Assert.That(gi.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
            Assert.That(gi.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(192UL * 1024UL * 1024UL));
            Assert.That(gi.DdgiClipmapCascadeCount, Is.EqualTo(4));
            Assert.That(gi.DdgiClipmapProbeCountX, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapProbeCountY, Is.EqualTo(14));
            Assert.That(gi.DdgiClipmapProbeCountZ, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapBaseSpacing, Is.EqualTo(0.75f));
            Assert.That(gi.DdgiClipmapVerticalCenterOffset, Is.EqualTo(-0.25f));
            Assert.That(gi.DdgiCascade0VerticalCenterOffset, Is.EqualTo(-0.25f));
            Assert.That(gi.DdgiCascade1VerticalCenterOffset, Is.EqualTo(2.5f));
            Assert.That(gi.DdgiCascade2VerticalCenterOffset, Is.EqualTo(8.0f));
            Assert.That(gi.DdgiCascade3VerticalCenterOffset, Is.EqualTo(16.0f));
            Assert.That(totalClipmapProbes, Is.EqualTo(32_256));
            Assert.That(totalClipmapProbes, Is.LessThanOrEqualTo(gi.DdgiMaxActiveProbes));
            Assert.That(gi.IndirectIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.DdgiMaxRaysPerProbe, Is.EqualTo(128));
            Assert.That(gi.DdgiCascade0RaysPerProbe, Is.EqualTo(128));
            Assert.That(gi.DdgiCascade1RaysPerProbe, Is.EqualTo(96));
            Assert.That(gi.DdgiCascade2RaysPerProbe, Is.EqualTo(64));
            Assert.That(gi.DdgiCascade3RaysPerProbe, Is.EqualTo(48));
            Assert.That(gi.DdgiCascade0MaxRayDistance, Is.EqualTo(12.0f));
            Assert.That(gi.DdgiCascade1MaxRayDistance, Is.EqualTo(36.0f));
            Assert.That(gi.DdgiCascade2MaxRayDistance, Is.EqualTo(96.0f));
            Assert.That(gi.DdgiCascade3MaxRayDistance, Is.EqualTo(192.0f));
            Assert.That(gi.DdgiMaxProbeUpdatesPerFrame, Is.EqualTo(2_048));
            Assert.That(gi.DdgiProbeUpdatePrimaryRayBudget, Is.EqualTo(262_144));
            Assert.That(gi.DdgiColdStartMaxProbeUpdatesPerFrame, Is.EqualTo(2_048));
            Assert.That(gi.DdgiColdStartPrimaryRayBudget, Is.EqualTo(524_288));
            Assert.That(gi.DdgiMinimumProbeRefreshFrames, Is.EqualTo(120));
            Assert.That(gi.DdgiProbeUpdateTimeBudgetMilliseconds, Is.EqualTo(3.0f));
            Assert.That(gi.DdgiGpuTotalUpdateTimeBudgetMilliseconds, Is.EqualTo(3.0f));
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(1024UL * 1024UL * 1024UL));
            Assert.That(gi.SimpleDdgiNearFullRaysPerProbe, Is.EqualTo(128));
            Assert.That(gi.SimpleDdgiReducedBlendEnabled, Is.False);
            Assert.That(gi.FarFieldClipmapEnabled, Is.True);
            Assert.That(gi.FarFieldPagedEnabled, Is.True);
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.Empty);
            Assert.That(gi.SimpleDdgiRingBaseSpacing, Is.EqualTo(1.25f));
            Assert.That(gi.SimpleDdgiRingSpacingMultiplier, Is.EqualTo(3.0f));
            Assert.That(gi.SimpleDdgiVerticalRingPolicy, Is.EqualTo(SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(28));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(14));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(28));
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Shadows.DirectionalShadowMapSize, Is.EqualTo(2048));
            Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(3));
            Assert.That(settings.Shadows.PcfRadius, Is.EqualTo(1));
        });
    }

    [TestCase("Medium")]
    [TestCase("Low")]
    public void ReducedMemoryProfiles_DoNotRetainAuthoredVolumes(string profileName)
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new Vector3(-1.0f),
            new Vector3(1.0f),
            0.5f));

        ConfigurePlazaRenderSettings(settings, profileName);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.SimpleDdgiAuthoredVolumes, Is.Empty);
            Assert.That(settings.Environment.Enabled, Is.True);
            Assert.That(settings.Environment.SourceKind, Is.EqualTo(EnvironmentSourceKind.ProceduralSky));
            Assert.That(settings.Environment.SunDriver, Is.EqualTo(ProceduralSkySunDriver.AstronomicalTime));
            Assert.That(settings.Environment.AnimateTimeOfDay, Is.True);
            Assert.That(settings.Environment.MoonIrradianceScale, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.StarIntensity, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.AirglowIntensity, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void NormalAndValidationSponza_ResolveIdenticalCoreGiSettingsAndCoverage()
    {
        var normalSettings = new RenderSettings();
        var validationSettings = new RenderSettings();

        ConfigurePlazaRenderSettings(normalSettings);
        // Reapplying either path must restore the same profile rather than append
        // duplicate volumes or retain an earlier validation override.
        ConfigurePlazaRenderSettings(normalSettings);
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            validationSettings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            validationSettings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);

        AssertSponzaCoreGiEquivalent(
            normalSettings.GlobalIllumination,
            validationSettings.GlobalIllumination);
        Assert.Multiple(() =>
        {
            Assert.That(normalSettings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
            Assert.That(validationSettings.GlobalIllumination.IndirectIntensity, Is.EqualTo(1.0f));
            Assert.That(normalSettings.GlobalIllumination.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(1024UL * 1024UL * 1024UL));
            Assert.That(validationSettings.GlobalIllumination.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(1024UL * 1024UL * 1024UL));
            Assert.That(validationSettings.AutoExposure.Enabled, Is.False);
            Assert.That(validationSettings.Reflections.Enabled, Is.False);
        });
    }

    [Test]
    public void CanonicalSponzaProfile_SettingsSerializationRoundTripsWithoutInventingAuthoredCoverage()
    {
        string path = System.IO.Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"sponza-gi-profile-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            ConfigurePlazaRenderSettings(settings);
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            AssertSponzaCoreGiEquivalent(settings.GlobalIllumination, loaded.GlobalIllumination);
        }
        finally
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
    }

    [Test]
    public void ConfigureSceneLighting_RemovesLegacyDenseAlleyVolumeWithoutAddingLocalVolume()
    {
        var scene = new Scene();
        scene.Add(new GlobalIlluminationProbeVolume { Name = DenseAlleyVolumeName });

        ConfigurePlazaSceneLighting(scene);
        ConfigurePlazaSceneLighting(scene);

        Assert.Multiple(() =>
        {
            Assert.That(scene.AmbientLight, Is.EqualTo(new Color(0.0f, 0.0f, 0.0f, 1.0f)));
            Assert.That(scene.GlobalIlluminationProbeVolumes, Is.Empty);
        });
    }

    [Test]
    public void LegacyFrameLayout_WhenExplicitlySelected_EmitsOnlyCameraClipmaps()
    {
        var settings = new RenderSettings();
        var scene = new Scene();
        ConfigurePlazaRenderSettings(settings);
        settings.GlobalIllumination.DdgiSimpleEnabled = false;
        ConfigurePlazaSceneLighting(scene);
        var camera = new FirstPersonCamera(new Vector3(0.0f, 1.35f, 3.1f), -1.5707964f, -0.08f);
        var clipmaps = new CameraRelativeDdgiClipmapController();
        var localSlots = new DdgiLocalVolumeSlotAllocator();

        DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
            scene,
            camera,
            settings.GlobalIllumination,
            clipmaps,
            frameSerial: 1,
            cameraCut: false,
            localVolumeSlots: localSlots);

        Assert.Multiple(() =>
        {
            Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(4));
            Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(32_256));
            Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(0));
            Assert.That(layout.AuthoredProbeCount, Is.EqualTo(0));
            Assert.That(layout.LocalSlotCount, Is.EqualTo(0));
            Assert.That(layout.LocalSlotProbeCapacity, Is.EqualTo(0));
            Assert.That(layout.ActiveLocalSlotCount, Is.EqualTo(0));
            Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(32_256));
            Assert.That(layout.TotalPhysicalProbeCount, Is.LessThanOrEqualTo(settings.GlobalIllumination.DdgiMaxActiveProbes));
            Assert.That(layout.Volumes, Has.Count.EqualTo(4));
            Assert.That(layout.VolumeMetadata, Has.All.Matches<DdgiProbeVolumeRuntimeMetadata>(metadata =>
                metadata.Kind == DdgiProbeVolumeKind.CameraClipmap));
            Assert.That(layout.Volumes[0].Bounds.Min.Y, Is.LessThanOrEqualTo(camera.Position.Y - 1.0f));
            Assert.That(layout.Volumes[0].Bounds.Max.Y, Is.GreaterThanOrEqualTo(camera.Position.Y + 3.0f));
            Assert.That(layout.Volumes[1].Bounds.Max.Y, Is.GreaterThanOrEqualTo(camera.Position.Y + 10.0f));
            Assert.That(layout.Volumes[2].Bounds.Max.Y, Is.GreaterThanOrEqualTo(camera.Position.Y + 23.0f));
            Assert.That(layout.Volumes[3].Bounds.Max.Y, Is.GreaterThanOrEqualTo(camera.Position.Y + 45.0f));
        });
    }

    private static void ConfigurePlazaRenderSettings(RenderSettings settings)
    {
        Type type = typeof(SampleBenchmarkOptions).Assembly.GetType(
            "NjulfHelloGame.SamplePlazaGlobalIllumination",
            throwOnError: true)!;
        MethodInfo method = type.GetMethod(
            "ConfigureRenderSettings",
            BindingFlags.Public | BindingFlags.Static)!;

        method.Invoke(null, [settings]);
    }

    private static void ConfigurePlazaRenderSettings(RenderSettings settings, string profileName)
    {
        Type assemblyMarker = typeof(SampleBenchmarkOptions);
        Type type = assemblyMarker.Assembly.GetType(
            "NjulfHelloGame.SamplePlazaGlobalIllumination",
            throwOnError: true)!;
        Type profileType = assemblyMarker.Assembly.GetType(
            "NjulfHelloGame.SamplePlazaGpuMemoryProfile",
            throwOnError: true)!;
        MethodInfo method = type.GetMethod(
            "ConfigureRenderSettingsForMemoryProfile",
            BindingFlags.Public | BindingFlags.Static)!;

        method.Invoke(null, [settings, Enum.Parse(profileType, profileName)]);
    }

    private static void ConfigurePlazaSceneLighting(Scene scene)
    {
        Type type = typeof(SampleBenchmarkOptions).Assembly.GetType(
            "NjulfHelloGame.SamplePlazaGlobalIllumination",
            throwOnError: true)!;
        MethodInfo method = type.GetMethod(
            "ConfigureSceneLighting",
            BindingFlags.Public | BindingFlags.Static)!;

        method.Invoke(null, [scene]);
    }

    private static void AssertSponzaCoreGiEquivalent(
        GlobalIlluminationSettings expected,
        GlobalIlluminationSettings actual)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Enabled, Is.EqualTo(expected.Enabled));
            Assert.That(actual.Mode, Is.EqualTo(expected.Mode));
            Assert.That(actual.UseSsgi, Is.EqualTo(expected.UseSsgi));
            Assert.That(actual.UseDdgi, Is.EqualTo(expected.UseDdgi));
            Assert.That(actual.DdgiSimpleEnabled, Is.EqualTo(expected.DdgiSimpleEnabled));
            Assert.That(actual.UseRayQueryBackend, Is.EqualTo(expected.UseRayQueryBackend));
            Assert.That(actual.IndirectIntensity, Is.EqualTo(expected.IndirectIntensity));
            Assert.That(actual.EnvironmentFallbackIntensity, Is.EqualTo(expected.EnvironmentFallbackIntensity));
            Assert.That(actual.MaxBounceDistance, Is.EqualTo(expected.MaxBounceDistance));
            Assert.That(actual.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(expected.GiAccelerationStructureMemoryBudgetBytes));
            Assert.That(actual.SimpleDdgiRingCount, Is.EqualTo(expected.SimpleDdgiRingCount));
            Assert.That(actual.SimpleDdgiRingBaseSpacing, Is.EqualTo(expected.SimpleDdgiRingBaseSpacing));
            Assert.That(actual.SimpleDdgiRingSpacingMultiplier, Is.EqualTo(expected.SimpleDdgiRingSpacingMultiplier));
            Assert.That(actual.SimpleDdgiVerticalRingPolicy, Is.EqualTo(expected.SimpleDdgiVerticalRingPolicy));
            Assert.That(actual.SimpleDdgiReceiverVerticalAnchor, Is.EqualTo(expected.SimpleDdgiReceiverVerticalAnchor));
            Assert.That(actual.SimpleDdgiNearRingGridSizeX, Is.EqualTo(expected.SimpleDdgiNearRingGridSizeX));
            Assert.That(actual.SimpleDdgiNearRingGridSizeY, Is.EqualTo(expected.SimpleDdgiNearRingGridSizeY));
            Assert.That(actual.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(expected.SimpleDdgiNearRingGridSizeZ));
            Assert.That(actual.SimpleDdgiMidRingGridSizeX, Is.EqualTo(expected.SimpleDdgiMidRingGridSizeX));
            Assert.That(actual.SimpleDdgiMidRingGridSizeY, Is.EqualTo(expected.SimpleDdgiMidRingGridSizeY));
            Assert.That(actual.SimpleDdgiMidRingGridSizeZ, Is.EqualTo(expected.SimpleDdgiMidRingGridSizeZ));
            Assert.That(actual.SimpleDdgiFarRingGridSizeX, Is.EqualTo(expected.SimpleDdgiFarRingGridSizeX));
            Assert.That(actual.SimpleDdgiFarRingGridSizeY, Is.EqualTo(expected.SimpleDdgiFarRingGridSizeY));
            Assert.That(actual.SimpleDdgiFarRingGridSizeZ, Is.EqualTo(expected.SimpleDdgiFarRingGridSizeZ));
            Assert.That(actual.SimpleDdgiNormalBias, Is.EqualTo(expected.SimpleDdgiNormalBias));
            Assert.That(actual.SimpleDdgiViewBias, Is.EqualTo(expected.SimpleDdgiViewBias));
            Assert.That(actual.SimpleDdgiMaximumWorldBiasMeters, Is.EqualTo(expected.SimpleDdgiMaximumWorldBiasMeters));
            Assert.That(actual.SimpleDdgiArchitecturalThicknessMeters, Is.EqualTo(expected.SimpleDdgiArchitecturalThicknessMeters));
            Assert.That(actual.SimpleDdgiProbeUpdatesPerFrame, Is.EqualTo(expected.SimpleDdgiProbeUpdatesPerFrame));
            Assert.That(actual.SimpleDdgiNearFullRaysPerProbe, Is.EqualTo(expected.SimpleDdgiNearFullRaysPerProbe));
            Assert.That(actual.SimpleDdgiAuthoredVolumes, Has.Count.EqualTo(expected.SimpleDdgiAuthoredVolumes.Count));
        });

        for (int i = 0; i < expected.SimpleDdgiAuthoredVolumes.Count; i++)
        {
            SimpleDdgiAuthoredVolume expectedVolume = expected.SimpleDdgiAuthoredVolumes[i];
            SimpleDdgiAuthoredVolume actualVolume = actual.SimpleDdgiAuthoredVolumes[i];
            Assert.Multiple(() =>
            {
                Assert.That(actualVolume.Min, Is.EqualTo(expectedVolume.Min));
                Assert.That(actualVolume.Max, Is.EqualTo(expectedVolume.Max));
                Assert.That(actualVolume.Spacing, Is.EqualTo(expectedVolume.Spacing));
                Assert.That(actualVolume.LatticePhase, Is.EqualTo(expectedVolume.LatticePhase));
                Assert.That(actualVolume.Purpose, Is.EqualTo(expectedVolume.Purpose));
                Assert.That(actualVolume.Priority, Is.EqualTo(expectedVolume.Priority));
            });
        }
    }
}
