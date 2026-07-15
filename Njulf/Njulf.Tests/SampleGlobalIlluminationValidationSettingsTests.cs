using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleGlobalIlluminationValidationSettingsTests
{
    private const ulong DdgiHighAccelerationStructureBudgetBytes = 256UL * 1024UL * 1024UL;
    private const ulong SponzaAccelerationStructureBudgetBytes = 512UL * 1024UL * 1024UL;

    [Test]
    public void ConfigureRenderSettings_SponzaReallocatesProbeBudgetToNavigableCoverage()
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
        int ringProbeCount =
            gi.SimpleDdgiNearRingGridSizeX * gi.SimpleDdgiNearRingGridSizeY * gi.SimpleDdgiNearRingGridSizeZ +
            gi.SimpleDdgiMidRingGridSizeX * gi.SimpleDdgiMidRingGridSizeY * gi.SimpleDdgiMidRingGridSizeZ +
            gi.SimpleDdgiFarRingGridSizeX * gi.SimpleDdgiFarRingGridSizeY * gi.SimpleDdgiFarRingGridSizeZ;
        int authoredProbeCount = gi.SimpleDdgiAuthoredVolumes.Sum(AuthoredProbeCount);

        Assert.Multiple(() =>
        {
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(SponzaAccelerationStructureBudgetBytes));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Count.EqualTo(6));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.All.Matches<SimpleDdgiAuthoredVolume>(volume => volume.Spacing == 1.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Some.Matches<SimpleDdgiAuthoredVolume>(volume => volume.Min.Y < 0.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.All.Matches<SimpleDdgiAuthoredVolume>(volume => volume.Max.Y <= 8.5f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Has.Some.Matches<SimpleDdgiAuthoredVolume>(volume => volume.LatticePhase != default));

            Assert.That(gi.SimpleDdgiRingBaseSpacing, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiRingSpacingMultiplier, Is.EqualTo(3.0f));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(32));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(12));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(32));
            Assert.That(gi.SimpleDdgiMidRingGridSizeX, Is.EqualTo(18));
            Assert.That(gi.SimpleDdgiMidRingGridSizeY, Is.EqualTo(10));
            Assert.That(gi.SimpleDdgiMidRingGridSizeZ, Is.EqualTo(18));
            Assert.That(gi.SimpleDdgiFarRingGridSizeX, Is.EqualTo(12));
            Assert.That(gi.SimpleDdgiFarRingGridSizeY, Is.EqualTo(8));
            Assert.That(gi.SimpleDdgiFarRingGridSizeZ, Is.EqualTo(12));
            Assert.That(gi.SimpleDdgiNearFullRaysPerProbe, Is.EqualTo(64));
            Assert.That(gi.SimpleDdgiSampledAtlasEnabled, Is.True);
            Assert.That(ringProbeCount, Is.EqualTo(16_680));
            Assert.That(authoredProbeCount, Is.EqualTo(8_569));
            Assert.That(ringProbeCount + authoredProbeCount, Is.EqualTo(25_249));
            Assert.That(ringProbeCount + authoredProbeCount, Is.LessThanOrEqualTo(GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount));
        });
    }

    private static int AuthoredProbeCount(SimpleDdgiAuthoredVolume volume)
    {
        Vector3 min = Vector3.Min(volume.Min, volume.Max);
        Vector3 max = Vector3.Max(volume.Min, volume.Max);
        Vector3 origin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(
            min,
            volume.Spacing,
            volume.LatticePhase);
        int countX = (int)MathF.Ceiling((max.X - origin.X) / volume.Spacing) + 1;
        int countY = (int)MathF.Ceiling((max.Y - origin.Y) / volume.Spacing) + 1;
        int countZ = (int)MathF.Ceiling((max.Z - origin.Z) / volume.Spacing) + 1;
        return countX * countY * countZ;
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
