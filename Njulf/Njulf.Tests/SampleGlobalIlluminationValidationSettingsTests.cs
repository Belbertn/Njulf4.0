using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleGlobalIlluminationValidationSettingsTests
{
    private const ulong DdgiHighAccelerationStructureBudgetBytes = 1024UL * 1024UL * 1024UL;

    [Test]
    public void ConfigureRenderSettings_SponzaUsesGenericCameraRelativeCoverage()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new Vector3(-1.0f, -1.0f, -1.0f),
            new Vector3(1.0f, 1.0f, 1.0f),
            0.5f));

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);
        // Configuration is applied again during scene initialization/reload.
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        int ringProbeCount =
            gi.SimpleDdgiNearRingGridSizeX * gi.SimpleDdgiNearRingGridSizeY * gi.SimpleDdgiNearRingGridSizeZ +
            gi.SimpleDdgiMidRingGridSizeX * gi.SimpleDdgiMidRingGridSizeY * gi.SimpleDdgiMidRingGridSizeZ +
            gi.SimpleDdgiFarRingGridSizeX * gi.SimpleDdgiFarRingGridSizeY * gi.SimpleDdgiFarRingGridSizeZ;
        SimpleDdgiLayoutBudget tierBudget = SimpleDdgiLayoutBudget.Resolve(gi);
        ulong persistentBytes = SimpleDdgiLayoutCompiler.EstimatePersistentBytes(
            ringProbeCount,
            gi.SimpleDdgiSampledAtlasEnabled);

        Assert.Multiple(() =>
        {
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(DdgiHighAccelerationStructureBudgetBytes));
            Assert.That(gi.IndirectIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.Empty);
            Assert.That(gi.SimpleDdgiRingCount, Is.EqualTo(3));
            Assert.That(gi.SimpleDdgiRingBaseSpacing, Is.EqualTo(1.25f));
            Assert.That(gi.SimpleDdgiRingSpacingMultiplier, Is.EqualTo(3.0f));
            Assert.That(gi.SimpleDdgiVerticalRingPolicy, Is.EqualTo(SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(28));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(14));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(28));
            Assert.That(gi.SimpleDdgiMidRingGridSizeX, Is.EqualTo(18));
            Assert.That(gi.SimpleDdgiMidRingGridSizeY, Is.EqualTo(10));
            Assert.That(gi.SimpleDdgiMidRingGridSizeZ, Is.EqualTo(18));
            Assert.That(gi.SimpleDdgiFarRingGridSizeX, Is.EqualTo(12));
            Assert.That(gi.SimpleDdgiFarRingGridSizeY, Is.EqualTo(8));
            Assert.That(gi.SimpleDdgiFarRingGridSizeZ, Is.EqualTo(12));
            Assert.That(gi.SimpleDdgiNearFullRaysPerProbe, Is.EqualTo(128));
            Assert.That(gi.SimpleDdgiSampledAtlasEnabled, Is.True);
            Assert.That(gi.SimpleDdgiReducedBlendEnabled, Is.False);
            Assert.That(gi.FarFieldClipmapEnabled, Is.True);
            Assert.That(gi.FarFieldPagedEnabled, Is.True);
            Assert.That(ringProbeCount, Is.EqualTo(15_368));
            Assert.That(ringProbeCount, Is.LessThanOrEqualTo(tierBudget.ProbeBudget));
            Assert.That(persistentBytes, Is.LessThanOrEqualTo(tierBudget.PersistentMemoryBudgetBytes));
        });
    }

    [Test]
    public void AuthoredVolume_IsAnExplicitLocalOptInAfterCameraRelativeSetup()
    {
        var settings = new RenderSettings();

        SampleSponzaGlobalIlluminationProfile.Configure(settings);
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            new Vector3(-2.0f, 0.0f, -2.0f),
            new Vector3(2.0f, 3.0f, 2.0f),
            0.5f));

        Assert.Multiple(() =>
        {
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Count.EqualTo(1));
            Assert.That(gi.SimpleDdgiAuthoredVolumes[0].Spacing, Is.EqualTo(0.5f));
            Assert.That(gi.SimpleDdgiRingCount, Is.EqualTo(3));
            Assert.That(gi.SimpleDdgiVerticalRingPolicy, Is.EqualTo(SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis));
        });
    }

    [Test]
    public void SponzaProfile_UsesPhysicalEnvironmentAndInteractiveExposureWhileCaptureRemainsLocked()
    {
        var settings = new RenderSettings();

        SampleSponzaGlobalIlluminationProfile.Configure(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.AutoExposure.Enabled, Is.True);
            Assert.That(settings.AutoExposure.MinExposure, Is.EqualTo(0.5f));
            Assert.That(settings.AutoExposure.MaxExposure, Is.EqualTo(8.0f));
            Assert.That(settings.GlobalIllumination.DdgiAlphaMaskedTransportEnabled, Is.True);
            Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(3));
            Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(120.0f));
        });

        SampleSponzaGlobalIlluminationProfile.ApplyValidationOverlay(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
        });
    }

    [TestCase(SamplePerformanceScenario.GiCornellRoom)]
    [TestCase(SamplePerformanceScenario.GiSimpleDdgiFurnace)]
    public void ConfigureRenderSettings_EnclosedScenesRetainZeroEnvironmentFallback(
        SamplePerformanceScenario scenario)
    {
        var settings = new RenderSettings();

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, scenario);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(0.0f));
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(DdgiHighAccelerationStructureBudgetBytes));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Count.EqualTo(1));
            Assert.That(gi.SimpleDdgiAuthoredVolumes[0].Max.Y, Is.EqualTo(4.25f));
        });
    }

    [Test]
    public void ConfigureRenderSettings_OtherValidationSceneDoesNotInheritSponzaOverrides()
    {
        var settings = new RenderSettings();

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiThinWallLeakTest);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        Assert.Multiple(() =>
        {
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(0.2f));
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(DdgiHighAccelerationStructureBudgetBytes));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.Empty);
        });
    }
}
