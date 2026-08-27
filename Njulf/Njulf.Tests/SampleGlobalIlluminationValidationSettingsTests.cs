using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
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
        float nearSpacing = gi.SimpleDdgiRingBaseSpacing *
            gi.SimpleDdgiAutomaticProbeDensityScale;
        float nearExtentX = (gi.SimpleDdgiNearRingGridSizeX - 1) * nearSpacing;
        float nearExtentZ = (gi.SimpleDdgiNearRingGridSizeZ - 1) * nearSpacing;

        Assert.Multiple(() =>
        {
            Assert.That(gi.GiAccelerationStructureMemoryBudgetBytes, Is.EqualTo(DdgiHighAccelerationStructureBudgetBytes));
            Assert.That(
                gi.IndirectIntensity,
                Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultIndirectIntensity));
            Assert.That(gi.EnvironmentFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(gi.SimpleDdgiAuthoredVolumes, Is.Empty);
            Assert.That(gi.SimpleDdgiRingCount, Is.EqualTo(3));
            Assert.That(gi.SimpleDdgiRingBaseSpacing, Is.EqualTo(1.25f));
            Assert.That(gi.SimpleDdgiRingSpacingMultiplier, Is.EqualTo(3.0f));
            Assert.That(gi.SimpleDdgiAutomaticProbeDensityScale, Is.EqualTo(0.95f));
            Assert.That(gi.SimpleDdgiVerticalRingPolicy, Is.EqualTo(SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis));
            Assert.That(gi.SimpleDdgiNearRingGridSizeX, Is.EqualTo(34));
            Assert.That(gi.SimpleDdgiNearRingGridSizeY, Is.EqualTo(15));
            Assert.That(gi.SimpleDdgiNearRingGridSizeZ, Is.EqualTo(23));
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
            Assert.That(ringProbeCount, Is.EqualTo(16_122));
            Assert.That(ringProbeCount, Is.LessThanOrEqualTo(tierBudget.ProbeBudget));
            Assert.That(persistentBytes, Is.LessThanOrEqualTo(tierBudget.PersistentMemoryBudgetBytes));
            Assert.That(nearSpacing, Is.EqualTo(1.1875f));
            Assert.That(nearExtentX, Is.GreaterThanOrEqualTo(38.0f));
            Assert.That(nearExtentZ, Is.GreaterThanOrEqualTo(25.0f));
            Assert.That(settings.AutoExposure.Enabled, Is.True);
            Assert.That(settings.Reflections.Enabled, Is.True);
            Assert.That(settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(
                gi.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
        });
    }

    [TestCase(RenderQualityPreset.Low,
        SimpleDdgiNearFieldResidualMode.Off)]
    [TestCase(RenderQualityPreset.Medium,
        SimpleDdgiNearFieldResidualMode.Off)]
    [TestCase(RenderQualityPreset.High,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    [TestCase(RenderQualityPreset.DdgiHigh,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    [TestCase(RenderQualityPreset.Ultra,
        SimpleDdgiNearFieldResidualMode.HiZAdaptive)]
    public void SponzaPostRolloutPolicy_FollowsQualityTier(
        RenderQualityPreset preset,
        SimpleDdgiNearFieldResidualMode expectedMode)
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(preset);
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            expectedMode == SimpleDdgiNearFieldResidualMode.Off
                ? SimpleDdgiNearFieldResidualMode.HiZAdaptive
                : SimpleDdgiNearFieldResidualMode.Off;

        SampleSponzaGlobalIlluminationProfile
            .ConfigurePostAdvancedGiRollout(settings);

        Assert.That(
            settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode,
            Is.EqualTo(expectedMode));
    }

    [Test]
    public void SponzaPostRolloutPolicy_ExplicitC5FixtureEnablesResidual()
    {
        var settings = new RenderSettings();
        settings.ApplyQualityPreset(RenderQualityPreset.Medium);
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.Off;

        SampleSponzaGlobalIlluminationProfile
            .ConfigurePostAdvancedGiRollout(
                settings,
                residualValidationEnabled: true);

        Assert.That(
            settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode,
            Is.EqualTo(SimpleDdgiNearFieldResidualMode.HiZAdaptive));
    }

    [Test]
    public void SponzaMediumMemoryProfile_UsesSsrWithoutC5()
    {
        var settings = new RenderSettings();

        SamplePlazaGlobalIllumination.ConfigureRenderSettingsForMemoryProfile(
            settings,
            SamplePlazaGpuMemoryProfile.Medium);

        Assert.Multiple(() =>
        {
            Assert.That(settings.QualityPreset,
                Is.EqualTo(RenderQualityPreset.Medium));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode.Off));
            Assert.That(settings.Reflections.Enabled, Is.True);
            Assert.That(settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.StaticProbesAndSsr));
            Assert.That(settings.Reflections.RayQueryPixelBudgetFraction,
                Is.Zero);
        });
    }

    [Test]
    public void SponzaCaptureSettings_ExplicitlyOwnDeterministicPresentationOverrides()
    {
        var settings = new RenderSettings();

        SampleGlobalIlluminationValidation.ConfigureSponzaCaptureSettings(
            settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.Enabled, Is.True);
            Assert.That(settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(settings.Bloom.Enabled, Is.False);
            Assert.That(
                settings.GlobalIllumination
                    .SimpleDdgiThinSurfaceTransmissionEnabled,
                Is.True);
        });
    }

    [Test]
    public void AuthoredVolume_IsAnExplicitLocalOptInAfterCameraRelativeSetup()
    {
        var settings = new RenderSettings();

        SampleSponzaGlobalIlluminationProfile.Configure(settings);

        Assert.That(settings.GlobalIllumination.SimpleDdgiThinSurfaceTransmissionEnabled, Is.False);
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
            Assert.That(settings.Environment.Enabled, Is.True);
            Assert.That(settings.Environment.SourceKind, Is.EqualTo(EnvironmentSourceKind.ProceduralSky));
            Assert.That(settings.Environment.SourcePath, Is.Null);
            Assert.That(settings.Environment.SunDriver, Is.EqualTo(ProceduralSkySunDriver.AstronomicalTime));
            Assert.That(settings.Environment.AnimateTimeOfDay, Is.False);
            Assert.That(settings.Environment.TimeOfDayHours, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultSolarTimeHours));
            Assert.That(settings.Environment.LatitudeDegrees, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultLatitudeDegrees));
            Assert.That(settings.Environment.DayOfYear, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultDayOfYear));
            Assert.That(settings.Environment.NorthOffsetDegrees, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultNorthOffsetDegrees));
            Assert.That(settings.Environment.TimeScale, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultTimeScale));
            Assert.That(settings.Environment.Turbidity, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultTurbidity));
            Assert.That(settings.Environment.GroundAlbedo, Is.EqualTo(new Vector3(SampleSponzaGlobalIlluminationProfile.DefaultGroundAlbedo)));
            Assert.That(settings.Environment.AtmosphereIntensity, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultAtmosphereIntensity));
            Assert.That(settings.Environment.SolarIrradianceScale, Is.EqualTo(SampleSponzaGlobalIlluminationProfile.DefaultSolarIrradianceScale));
            Assert.That(settings.Environment.MoonIrradianceScale, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.StarIntensity, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.AirglowIntensity, Is.GreaterThan(0.0f));
            Assert.That(settings.Environment.GiSunStepDegrees, Is.EqualTo(0.25f));
            Assert.That(settings.Environment.GiTargetSourceSweepSeconds, Is.EqualTo(8.0f));
            Assert.That(settings.Environment.PrefilteredSize, Is.EqualTo(128));
            Assert.That(settings.Environment.SpecularPrefilterMipsPerFrame, Is.EqualTo(1));
            Assert.That(settings.Environment.SpecularPrefilterTransitionFrames, Is.EqualTo(8));
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.DiffuseIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SpecularIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.AutoExposure.Enabled, Is.True);
            Assert.That(settings.AutoExposure.TargetLuminance, Is.EqualTo(0.125f));
            Assert.That(settings.AutoExposure.MinExposure, Is.EqualTo(0.25f));
            Assert.That(settings.AutoExposure.MaxExposure, Is.EqualTo(2.0f));
            Assert.That(settings.AutoExposure.LowPercentile, Is.EqualTo(70.0f));
            Assert.That(settings.AutoExposure.HighPercentile, Is.EqualTo(95.0f));
            Assert.That(settings.AutoExposure.DarkToLightAdaptationSpeed, Is.EqualTo(3.0f));
            Assert.That(settings.AutoExposure.LightToDarkAdaptationSpeed, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.Enabled, Is.True);
            Assert.That(settings.Reflections.Mode, Is.EqualTo(ReflectionMode.HybridRayQuery));
            Assert.That(settings.Reflections.Intensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.GlobalFallbackIntensity, Is.EqualTo(1.0f));
            Assert.That(settings.Reflections.CaptureOnLoad, Is.False);
            Assert.That(settings.Reflections.CaptureIncludesDdgi, Is.False);
            Assert.That(settings.Reflections.MaxProbeCapturesPerFrame, Is.Zero);
            Assert.That(settings.GlobalIllumination.DdgiAlphaMaskedTransportEnabled, Is.True);
            Assert.That(settings.Shadows.DirectionalCascadeCount, Is.EqualTo(3));
            Assert.That(settings.Shadows.MaxShadowDistance, Is.EqualTo(48.0f));
        });

        SampleSponzaGlobalIlluminationProfile.ApplyValidationOverlay(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.AutoExposure.Enabled, Is.False);
            Assert.That(settings.Exposure, Is.EqualTo(1.0f));
            Assert.That(settings.Environment.SourceKind, Is.EqualTo(EnvironmentSourceKind.ProceduralSky));
            Assert.That(settings.Environment.SunDriver, Is.EqualTo(ProceduralSkySunDriver.AstronomicalTime));
            Assert.That(settings.Environment.AnimateTimeOfDay, Is.False);
            Assert.That(settings.Environment.SkyIntensity, Is.EqualTo(1.0f));
            Assert.That(
                settings.Diagnostics.DirectionalShadowReceiverCountersEnabled,
                Is.EqualTo(RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled));
            Assert.That(settings.GlobalIllumination.SimpleDdgiThinSurfaceTransmissionEnabled, Is.False);
        });
    }

    [Test]
    public void SponzaProfile_AstronomicalSunStartsHighAndShinesAcrossCurtainRows()
    {
        System.Numerics.Vector3 toSun = SolarPositionCalculator.CalculateToSunDirection(
            SampleSponzaGlobalIlluminationProfile.DefaultSolarTimeHours,
            SampleSponzaGlobalIlluminationProfile.DefaultLatitudeDegrees,
            SampleSponzaGlobalIlluminationProfile.DefaultDayOfYear,
            SampleSponzaGlobalIlluminationProfile.DefaultNorthOffsetDegrees);
        System.Numerics.Vector2 actualAzimuth = System.Numerics.Vector2.Normalize(
            new System.Numerics.Vector2(toSun.X, toSun.Z));
        System.Numerics.Vector2 curtainFacingAzimuth = System.Numerics.Vector2.Normalize(
            new System.Numerics.Vector2(1.0f, -1.0f));
        float elevationDegrees = MathF.Asin(toSun.Y) * 180.0f / MathF.PI;

        Assert.Multiple(() =>
        {
            Assert.That(
                System.Numerics.Vector2.Dot(actualAzimuth, curtainFacingAzimuth),
                Is.GreaterThan(0.9999f));
            Assert.That(elevationDegrees, Is.InRange(50.0f, 56.0f));
        });
    }

    [Test]
    public void SponzaProfile_DefaultAtmosphereProvidesBoundedCoastalDaylightFill()
    {
        var settings = new RenderSettings();
        SampleSponzaGlobalIlluminationProfile.Configure(settings);

        System.Numerics.Vector3 toSun = SolarPositionCalculator.CalculateToSunDirection(
            settings.Environment.TimeOfDayHours,
            settings.Environment.LatitudeDegrees,
            settings.Environment.DayOfYear,
            settings.Environment.NorthOffsetDegrees);
        var model = new HosekWilkieSkyModel();
        var frame = new ProceduralAtmosphereFrame();
        model.UpdateFrame(settings.Environment, toSun, authoredSunRadiance: null, frame);

        System.Numerics.Vector3 zenith = model.EvaluateSkyRadiance(
            System.Numerics.Vector3.UnitY,
            frame,
            ProceduralSkyEvaluationMode.Visual,
            includeCelestialDiscs: false,
            includeStars: false);
        System.Numerics.Vector3 skyIrradiance =
            HosekWilkieSkyModel.EvaluateDiffuseIrradianceSh(
                System.Numerics.Vector3.UnitY,
                frame.DiffuseIrradianceSh);
        System.Numerics.Vector3 directHorizontalIrradiance =
            frame.SunRadiance * MathF.Max(toSun.Y, 0.0f);
        float dynamicDiffuseToDirect = Luminance(skyIrradiance) /
            MathF.Max(Luminance(directHorizontalIrradiance), 0.000001f);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Environment.DayOfYear, Is.InRange(152, 243));
            Assert.That(settings.Environment.Turbidity, Is.InRange(8.0f, 10.0f));
            Assert.That(zenith.Z, Is.InRange(zenith.X * 1.15f, zenith.X * 1.35f));
            Assert.That(frame.SunRadiance.Z, Is.GreaterThan(frame.SunRadiance.X * 0.55f));
            Assert.That(dynamicDiffuseToDirect, Is.InRange(0.10f, 0.12f));
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

    private static float Luminance(System.Numerics.Vector3 color) =>
        color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
}
