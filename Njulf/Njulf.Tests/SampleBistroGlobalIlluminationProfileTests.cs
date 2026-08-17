using Njulf.Core.Math;
using Njulf.Rendering.Data;
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
            Assert.That(settings.Environment.AtmosphereIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SolarIrradianceScale, Is.EqualTo(14.0f));
            Assert.That(settings.AutoExposure.MinExposure, Is.EqualTo(0.03125f));
            Assert.That(settings.AutoExposure.MaxExposure, Is.EqualTo(4.0f));
            Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(120.0f));
            Assert.That(settings.Reflections.Intensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.GlobalFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.CaptureOnLoad, Is.False);
            Assert.That(settings.Reflections.MaxProbeCapturesPerFrame, Is.Zero);
        });
    }
}
