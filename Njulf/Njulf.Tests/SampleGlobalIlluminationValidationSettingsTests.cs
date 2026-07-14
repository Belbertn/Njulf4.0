using Njulf.Rendering.Data;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleGlobalIlluminationValidationSettingsTests
{
    private const ulong DdgiHighAccelerationStructureBudgetBytes = 256UL * 1024UL * 1024UL;
    private const ulong SponzaAccelerationStructureBudgetBytes = 512UL * 1024UL * 1024UL;

    [Test]
    public void ConfigureRenderSettings_SponzaAddsUpperDenseCoverageWithinProbeBudget()
    {
        var settings = new RenderSettings();

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);
        // Configuration is applied again during scene initialization/reload.
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(
            settings,
            SamplePerformanceScenario.GiSponzaRightWallStationary);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        SimpleDdgiAuthoredVolume volume = gi.SimpleDdgiAuthoredVolumes.Single();
        int ringProbeCount =
            gi.SimpleDdgiRingCount *
            gi.SimpleDdgiRingGridSizeX *
            gi.SimpleDdgiRingGridSizeY *
            gi.SimpleDdgiRingGridSizeZ;
        int authoredProbeCount =
            ((int)MathF.Ceiling((volume.Max.X - volume.Min.X) / volume.Spacing) + 1) *
            ((int)MathF.Ceiling((volume.Max.Y - volume.Min.Y) / volume.Spacing) + 1) *
            ((int)MathF.Ceiling((volume.Max.Z - volume.Min.Z) / volume.Spacing) + 1);

        Assert.Multiple(() =>
        {
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(SponzaAccelerationStructureBudgetBytes));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Count.EqualTo(1));
            Assert.That(volume.Spacing, Is.EqualTo(1.0f));

            // Measured main-mesh bounds are
            // (-16.245575, -1.131135, -9.402087)..(20.001305, 18.757229, 14.363933).
            Assert.That(volume.Min.X, Is.LessThanOrEqualTo(-16.245575f));
            Assert.That(volume.Max.X, Is.GreaterThanOrEqualTo(20.001305f));
            Assert.That(volume.Min.Z, Is.LessThanOrEqualTo(-9.402087f));
            Assert.That(volume.Max.Z, Is.GreaterThanOrEqualTo(14.363933f));
            Assert.That(volume.Min.Y, Is.LessThanOrEqualTo(10.5f), "Upper coverage must overlap the dense near ring.");
            Assert.That(volume.Max.Y, Is.GreaterThanOrEqualTo(18.757229f));

            Assert.That(ringProbeCount, Is.EqualTo(20_736));
            Assert.That(authoredProbeCount, Is.EqualTo(11_154));
            Assert.That(ringProbeCount + authoredProbeCount, Is.EqualTo(31_890));
            Assert.That(ringProbeCount + authoredProbeCount, Is.LessThanOrEqualTo(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount));
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
