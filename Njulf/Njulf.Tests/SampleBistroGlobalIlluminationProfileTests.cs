using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleBistroGlobalIlluminationProfileTests
{
    [Test]
    public void Configure_RemovesSponzaLayoutAndDisablesRefinementBricks()
    {
        var settings = new RenderSettings();
        SampleSponzaGlobalIlluminationProfile.Configure(settings);
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(
            new SimpleDdgiAuthoredVolume(
                new Vector3(-1.0f),
                new Vector3(1.0f),
                0.5f));

        SampleBistroGlobalIlluminationProfile.Configure(settings);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset, Is.EqualTo(RenderQualityPreset.DdgiHigh));
            Assert.That(gi.DdgiCameraRelativeEnabled, Is.True);
            Assert.That(
                gi.SimpleDdgiVerticalRingPolicy,
                Is.EqualTo(SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(28));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(14));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(28));
            Assert.That(gi.SimpleDdgiAutomaticProbeDensityScale, Is.EqualTo(0.70f));
            Assert.That(gi.IndirectIntensity, Is.EqualTo(1.65f));
            Assert.That(gi.FarFieldSkyVisibilityEnabled, Is.False);
            Assert.That(gi.SimpleDdgiRoughSpecularMinimumRoughness, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiRoughSpecularFullWeightRoughness, Is.EqualTo(1.0f));
            Assert.That(
                gi.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
            Assert.That(gi.SimpleDdgiTransportSolverRelaxation, Is.EqualTo(0.90f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.Empty);
            Assert.That(gi.SimpleDdgiRefinementBricksEnabled, Is.False);
            Assert.That(gi.SimpleDdgiRefinementMaximumBricks, Is.Zero);
        });
    }

    [Test]
    public void Configure_RestoresBistroPresentationAfterSponza()
    {
        var settings = new RenderSettings();
        SampleSponzaGlobalIlluminationProfile.Configure(settings);

        SampleBistroGlobalIlluminationProfile.Configure(settings);

        Assert.Multiple(() =>
        {
            Assert.That(
                settings.Environment.SunDriver,
                Is.EqualTo(ProceduralSkySunDriver.SceneDirectionalLight));
            Assert.That(
                settings.Environment.SourceKind,
                Is.EqualTo(EnvironmentSourceKind.HdrEquirectangular));
            Assert.That(
                settings.Environment.SourcePath,
                Is.EqualTo("Assets/Bistro_v5_2/san_giuseppe_bridge_4k.hdr"));
            Assert.That(settings.Environment.AtmosphereIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SolarIrradianceScale, Is.EqualTo(14.0f));
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(10.0f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(10.0f));
            Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(1.5f));
            Assert.That(settings.AutoExposure.MinExposure, Is.EqualTo(0.03125f));
            Assert.That(settings.AutoExposure.MaxExposure, Is.EqualTo(4.0f));
            Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(120.0f));
            Assert.That(
                settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(settings.Reflections.Intensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.GlobalFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.CaptureOnLoad, Is.False);
            Assert.That(settings.Reflections.CaptureIncludesDdgi, Is.False);
            Assert.That(settings.Reflections.MaxProbeCapturesPerFrame, Is.Zero);
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiUrgentRelightProbeBudget,
                Is.EqualTo(SimpleDdgiUrgentRelightPolicy.MaximumProbeBudget));
            Assert.That(
                settings.Environment.GiTargetSourceSweepSeconds,
                Is.EqualTo(0.5f));
        });
    }

    [Test]
    public void PostRolloutPolicy_KeepsC5Enabled()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.Off;

        SampleBistroGlobalIlluminationProfile
            .ConfigurePostAdvancedGiRollout(settings);

        Assert.That(
            settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode,
            Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
    }
}
