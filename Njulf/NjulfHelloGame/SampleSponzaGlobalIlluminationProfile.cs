using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Applies the production DDGI preset and Sponza's presentation settings.
/// Sponza deliberately has no scene-specific probe layout: it exercises the
/// same camera-relative rings used by arbitrary interiors and large worlds.
/// Authored volumes remain an explicit, opt-in local quality control.
/// </summary>
public static class SampleSponzaGlobalIlluminationProfile
{
    // Sponza Palace is in Dubrovnik. Start in the early afternoon on the summer
    // solstice. Aim the high sun across the courtyard so it strikes the long
    // rows of hanging curtains instead of travelling nearly parallel to them.
    // The elevation remains spectrally close to neutral daylight and retains
    // useful, readable shadows inside the galleries.
    public const float DefaultSolarTimeHours = 14.5f;
    public const float DefaultLatitudeDegrees = 42.6507f;
    public const int DefaultDayOfYear = 172;
    public const float DefaultNorthOffsetDegrees = -115.0f;
    public const float DefaultTimeScale = 60.0f;
    public const float DefaultTurbidity = 2.0f;
    public const float DefaultGroundAlbedo = 0.16f;
    public const float DefaultAtmosphereIntensity = 0.30f;
    public const float DefaultSolarIrradianceScale = 34.0f;

    /// <summary>
    /// Restores the generic DdgiHigh camera-relative layout. Reapplying the
    /// profile is idempotent and cannot retain authored coverage from another
    /// scene or a previous validation run.
    /// </summary>
    public static void Configure(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.EmergencyGiFallbackEnabled = false;
        gi.SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode.Degrade;
        gi.SimpleDdgiVerticalRingPolicy = SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        ConfigureReferenceOutput(settings);
    }

    /// <summary>
    /// Applies only deterministic validation controls. This method intentionally
    /// does not call a quality preset or mutate the DDGI layout, transport,
    /// intensity, rays, or budgets.
    /// </summary>
    public static void ApplyValidationOverlay(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.DynamicResolution.MinimumScale = 1.0f;
        settings.DynamicResolution.MaximumScale = 1.0f;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 1.0f;
        settings.Bloom.Enabled = false;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = false;
        settings.AmbientOcclusion.Enabled = true;
        settings.Shadows.PointShadowMapSize = 1024;
        settings.Shadows.PointNormalBias = 0.008f;
        settings.Shadows.PointConstantDepthBias = 0.0003f;
        settings.Shadows.PointPcfRadius = 1;
        settings.GlobalIllumination.SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode.Reject;
        // The same locked scene drives both clean timing and investigation.
        // Production binaries must not be disqualified by a validation overlay
        // that requests shader counters they deliberately do not contain.
        settings.Diagnostics.DdgiForwardEstimateCountersEnabled =
            RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled;
        settings.Diagnostics.DirectionalShadowReceiverCountersEnabled =
            RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled;
        // Locked captures compare a single, immutable authored key. Normal
        // Sponza uses the astronomical driver configured below; the capture
        // overlay is the deliberate deterministic exception.
        settings.Environment.SunDriver = ProceduralSkySunDriver.SceneDirectionalLight;
        settings.Environment.AnimateTimeOfDay = false;
    }

    internal static void ConfigureDynamicEnvironment(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        EnvironmentSettings environment = settings.Environment;
        environment.Enabled = true;
        environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
        environment.SourcePath = null;
        environment.TexturePrecision = EnvironmentTexturePrecision.Float16;
        environment.SunDriver = ProceduralSkySunDriver.AstronomicalTime;
        environment.AnimateTimeOfDay = true;

        environment.TimeOfDayHours = DefaultSolarTimeHours;
        environment.LatitudeDegrees = DefaultLatitudeDegrees;
        environment.DayOfYear = DefaultDayOfYear;
        environment.NorthOffsetDegrees = DefaultNorthOffsetDegrees;
        environment.TimeScale = DefaultTimeScale;

        // Clear maritime summer air. Hosek-Wilkie's RGB sky radiance and the
        // directional solar irradiance use independent scales; this calibrated
        // pair keeps initial horizontal diffuse skylight near one quarter of
        // direct sunlight. That produces a blue dome and crisp solar modeling
        // without materially increasing the scene's total daylight energy.
        environment.Turbidity = DefaultTurbidity;
        environment.GroundAlbedo = new Vector3(DefaultGroundAlbedo);
        environment.SunAngularDiameterDegrees = 0.53f;
        environment.MoonAngularDiameterDegrees = 0.52f;
        environment.AtmosphereIntensity = DefaultAtmosphereIntensity;
        environment.SolarIrradianceScale = DefaultSolarIrradianceScale;
        environment.MoonIrradianceScale = 0.12f;
        environment.StarIntensity = 0.025f;
        environment.AirglowIntensity = 0.025f;

        environment.GiSunStepDegrees = 0.25f;
        environment.GiTargetSourceSweepSeconds = 8.0f;
        environment.PrefilteredSize = 128;
        environment.SpecularPrefilterMipsPerFrame = 1;
        environment.SpecularPrefilterTransitionFrames = 8;
        environment.DebugView = EnvironmentDebugView.None;
        environment.RotationRadians = 0.0f;

        // DDGI owns probe-covered diffuse transport. Keep every environment
        // channel physically authored at unity; ownership composition prevents
        // diffuse IBL from being counted twice at valid DDGI receivers.
        environment.SkyIntensity = 1.0f;
        environment.DiffuseIntensity = 1.0f;
        environment.SpecularIntensity = 1.0f;
    }

    private static void ConfigureReferenceOutput(RenderSettings settings)
    {
        ConfigureDynamicEnvironment(settings);
        // Interactive presentation follows the camera into shaded galleries.
        // ApplyValidationOverlay intentionally overrides this for deterministic
        // linear-light validation captures.
        settings.AutoExposure.Enabled = true;
        settings.AutoExposure.TargetLuminance = 0.18f;
        settings.AutoExposure.MinExposure = 0.5f;
        settings.AutoExposure.MaxExposure = 8.0f;
        settings.AutoExposure.AdaptationSpeed = 2.0f;
        settings.Reflections.Enabled = true;
        settings.Shadows.DirectionalShadowMapSize = 2048;
        settings.Shadows.DirectionalCascadeCount = 3;
        // Sponza fits inside this range; concentrating the three cascades here
        // keeps the existing receiver and raster biases proportional to texels.
        settings.Shadows.MaxShadowDistance = 48.0f;
        settings.Shadows.PcfRadius = 1;
        settings.Shadows.SpotShadowsEnabled = false;
        settings.Shadows.MaxShadowedSpotLights = 0;
        settings.Shadows.PointShadowsEnabled = false;
        settings.Shadows.MaxShadowedPointLights = 0;
        settings.AmbientOcclusion.Enabled = true;
        settings.AmbientOcclusion.Radius = 0.45f;
        settings.AmbientOcclusion.Intensity = 0.55f;
        settings.AmbientOcclusion.Power = 1.0f;
    }
}
