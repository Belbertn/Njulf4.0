using System;
using System.Linq;
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
    public void ConfigureRenderSettings_UsesDenseCenteredClipmapWithinActiveBudget()
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
            Assert.That(gi.DdgiClipmapCascadeCount, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount));
            Assert.That(gi.DdgiClipmapProbeCountX, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapProbeCountY, Is.EqualTo(14));
            Assert.That(gi.DdgiClipmapProbeCountZ, Is.EqualTo(24));
            Assert.That(gi.DdgiClipmapBaseSpacing, Is.EqualTo(1.0f));
            Assert.That(gi.DdgiClipmapVerticalCenterOffset, Is.EqualTo(0.0f));
            Assert.That(totalClipmapProbes, Is.EqualTo(32_256));
            Assert.That(totalClipmapProbes, Is.LessThanOrEqualTo(gi.DdgiMaxActiveProbes));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(0.12f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(0.10f));
        });
    }

    [Test]
    public void ConfigureSceneLighting_AddsDenseAlleyVolumeOnce()
    {
        var scene = new Scene();

        ConfigurePlazaSceneLighting(scene);
        ConfigurePlazaSceneLighting(scene);

        GlobalIlluminationProbeVolume volume = scene.GlobalIlluminationProbeVolumes.Single();
        Vector3 spacing = volume.ProbeSpacing;

        Assert.Multiple(() =>
        {
            Assert.That(volume.Name, Is.EqualTo(DenseAlleyVolumeName));
            Assert.That(volume.Enabled, Is.True);
            Assert.That(volume.Interior, Is.True);
            Assert.That(volume.QualityClass, Is.EqualTo(GlobalIlluminationProbeVolumeQualityClass.High));
            Assert.That(volume.Bounds.Contains(new Vector3(0.0f, 1.35f, 3.1f)), Is.True);
            Assert.That(volume.ProbeCount, Is.EqualTo(392));
            Assert.That(spacing.X, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(spacing.Y, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(spacing.Z, Is.LessThanOrEqualTo(0.6f));
            Assert.That(volume.Intensity, Is.EqualTo(1.5f));
            Assert.That(volume.MaxProbeUpdatesPerFrame, Is.EqualTo(128));
            Assert.That(volume.Priority, Is.EqualTo(256));
            Assert.That(volume.UpdatePriority, Is.EqualTo(256));
            Assert.That(volume.StreamingCellId, Is.EqualTo(1));
        });
    }

    [Test]
    public void FrameLayout_AdmitsDenseAlleyVolumeWithCameraClipmaps()
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
            frameIndex: 1,
            cameraCut: false,
            localVolumeSlots: localSlots);

        int denseVolumeIndex = Enumerable.Range(0, layout.Volumes.Count)
            .Single(index => layout.Volumes[index].Name == DenseAlleyVolumeName);

        Assert.Multiple(() =>
        {
            Assert.That(layout.CameraRelativeCascadeCount, Is.EqualTo(GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount));
            Assert.That(layout.CameraRelativeProbeCount, Is.EqualTo(32_256));
            Assert.That(layout.AuthoredVolumeCount, Is.EqualTo(1));
            Assert.That(layout.AuthoredProbeCount, Is.EqualTo(392));
            Assert.That(layout.LocalSlotCount, Is.EqualTo(1));
            Assert.That(layout.LocalSlotProbeCapacity, Is.EqualTo(392));
            Assert.That(layout.ActiveLocalSlotCount, Is.EqualTo(1));
            Assert.That(layout.TotalPhysicalProbeCount, Is.EqualTo(32_648));
            Assert.That(layout.TotalPhysicalProbeCount, Is.LessThanOrEqualTo(settings.GlobalIllumination.DdgiMaxActiveProbes));
            Assert.That(layout.VolumeMetadata[denseVolumeIndex].Kind, Is.EqualTo(DdgiProbeVolumeKind.Authored));
            Assert.That(layout.VolumeMetadata[denseVolumeIndex].Flags & GlobalIlluminationProbeVolumeData.VolumeLocalSlotFlag, Is.Not.Zero);
            Assert.That(layout.VolumeMetadata[denseVolumeIndex].Flags & GlobalIlluminationProbeVolumeData.VolumeInteriorFlag, Is.Not.Zero);
            Assert.That(layout.VolumeMetadata[denseVolumeIndex].PhysicalFirstProbeIndex, Is.EqualTo(32_256));
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
