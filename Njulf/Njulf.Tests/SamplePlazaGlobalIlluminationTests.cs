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
    public void ConfigureRenderSettings_UsesTierBoundedClipmapBudget()
    {
        var settings = new RenderSettings();

        ConfigurePlazaRenderSettings(settings);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        int totalClipmapProbes =
            gi.DdgiClipmapProbeCountX *
            gi.DdgiClipmapProbeCountY *
            gi.DdgiClipmapProbeCountZ *
            gi.DdgiClipmapCascadeCount;

        Assert.Multiple(() =>
        {
            Assert.That(gi.DdgiCameraRelativeEnabled, Is.True);
            Assert.That(gi.DdgiQualityTier, Is.EqualTo(DdgiQualityTier.DdgiHigh));
            Assert.That(gi.DdgiAtlasMemoryBudgetBytes, Is.EqualTo(192UL * 1024UL * 1024UL));
            Assert.That(gi.DdgiClipmapCascadeCount, Is.EqualTo(3));
            Assert.That(gi.DdgiClipmapProbeCountX, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapProbeCountY, Is.EqualTo(14));
            Assert.That(gi.DdgiClipmapProbeCountZ, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapBaseSpacing, Is.EqualTo(1.0f));
            Assert.That(gi.DdgiClipmapVerticalCenterOffset, Is.EqualTo(6.25f));
            Assert.That(totalClipmapProbes, Is.EqualTo(24_192));
            Assert.That(totalClipmapProbes, Is.LessThanOrEqualTo(gi.DdgiMaxActiveProbes));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(0.12f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(0.10f));
        });
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
    public void FrameLayout_EmitsOnlyCameraClipmaps()
    {
        var settings = new RenderSettings();
        var scene = new Scene();
        ConfigurePlazaRenderSettings(settings);
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
            Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(3));
            Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(24_192));
            Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(0));
            Assert.That(layout.AuthoredProbeCount, Is.EqualTo(0));
            Assert.That(layout.LocalSlotCount, Is.EqualTo(0));
            Assert.That(layout.LocalSlotProbeCapacity, Is.EqualTo(0));
            Assert.That(layout.ActiveLocalSlotCount, Is.EqualTo(0));
            Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(24_192));
            Assert.That(layout.TotalPhysicalProbeCount, Is.LessThanOrEqualTo(settings.GlobalIllumination.DdgiMaxActiveProbes));
            Assert.That(layout.Volumes, Has.Count.EqualTo(3));
            Assert.That(layout.VolumeMetadata, Has.All.Matches<DdgiProbeVolumeRuntimeMetadata>(metadata =>
                metadata.Kind == DdgiProbeVolumeKind.CameraClipmap));
            Assert.That(layout.Volumes[0].Bounds.Min.Y, Is.LessThanOrEqualTo(camera.Position.Y - 1.0f));
            Assert.That(layout.Volumes[0].Bounds.Max.Y, Is.GreaterThanOrEqualTo(13.0f));
            Assert.That(layout.Volumes[1].Bounds.Max.Y, Is.GreaterThanOrEqualTo(18.0f));
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
}
