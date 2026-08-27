using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Applies the production DDGI preset and Sponza's presentation settings.
/// Sponza deliberately has no authored probe volume: it exercises the same
/// camera-relative rings used by arbitrary interiors and large worlds. The
/// near-ring aspect ratio is tuned to the plaza's architectural footprint so
/// a clipmap ownership boundary cannot bisect the default view.
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
    public const float DefaultTurbidity = 9.0f;
    public const float DefaultGroundAlbedo = 0.16f;
    public const float DefaultAtmosphereIntensity = 0.03f;
    public const float DefaultSolarIrradianceScale = 34.0f;
    public const float DefaultIndirectIntensity = 1.25f;

    /// <summary>
    /// Restores the bounded DdgiHigh camera-relative plaza layout. Reapplying
    /// the profile is idempotent and cannot retain authored coverage from
    /// another scene or a previous validation run.
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
        // The former 28x14x28 near field ended at X ~= -7.4 in the default
        // right-wall view. Its handoff to the much coarser mid field landed in
        // the middle of the plaza floor and produced a coherent light stripe.
        // Reallocate, rather than increase, the DdgiHigh probe budget: at the
        // 0.95 density scale this lattice has 1.1875 m spacing and a
        // 39.19x16.63x26.13 m core, fully containing Sponza's 38x25 m plan.
        gi.SimpleDdgiAutomaticProbeDensityScale = 0.95f;
        gi.SimpleDdgiNearRingGridSizeX = 34;
        gi.SimpleDdgiNearRingGridSizeY = 15;
        gi.SimpleDdgiNearRingGridSizeZ = 23;
        ConfigurePostAdvancedGiRollout(settings);
        // Sponza's diffuse materials and deep galleries need a modest display-
        // referred lift after tone mapping. This retains probe visibility
        // contrast while making the first DDGI bounce clearly readable.
        gi.IndirectIntensity = DefaultIndirectIntensity;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        ConfigureReferenceOutput(settings);
    }

    /// <summary>
    /// Reasserts the quality tier's C5 policy after global rollout changes.
    /// The validation fixture may explicitly force the residual on, while
    /// later CLI overrides remain authoritative.
    /// </summary>
    public static void ConfigurePostAdvancedGiRollout(
        RenderSettings settings,
        bool residualValidationEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool highClassPreset = settings.QualityPreset is
            RenderQualityPreset.High or
            RenderQualityPreset.DdgiHigh or
            RenderQualityPreset.Ultra;
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            residualValidationEnabled || highClassPreset
                ? GlobalIlluminationSettings
                    .DefaultSimpleDdgiNearFieldResidualMode
                : SimpleDdgiNearFieldResidualMode.Off;
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
        // Freeze the same astronomical source used by normal Sponza. The
        // previous capture-only authored-light driver reduced the 34-unit
        // presentation sun to the scene's 14-unit loading key, so the judged
        // image had a much larger sky/direct ratio and looked uniformly blue.
        // A paused astronomical clock is deterministic without changing the
        // physical lighting profile being reviewed.
        settings.Environment.SunDriver = ProceduralSkySunDriver.AstronomicalTime;
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
        environment.AnimateTimeOfDay = false;

        environment.TimeOfDayHours = DefaultSolarTimeHours;
        environment.LatitudeDegrees = DefaultLatitudeDegrees;
        environment.DayOfYear = DefaultDayOfYear;
        environment.NorthOffsetDegrees = DefaultNorthOffsetDegrees;
        environment.TimeScale = DefaultTimeScale;

        // Coastal haze neutralizes the open-sky fill instead of washing covered
        // galleries in saturated blue. Hosek-Wilkie's RGB sky radiance and
        // directional solar irradiance remain independent physical inputs. The
        // lower atmosphere scale keeps diffuse daylight near one tenth of the
        // horizontal sun without flattening contact shadows.
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
        ApplyPresentationOverlay(settings);
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
        settings.AmbientOcclusion.ResolutionScale = 0.5f;
        settings.AmbientOcclusion.SampleCount = 16;
        settings.AmbientOcclusion.Radius = 0.45f;
        settings.AmbientOcclusion.Intensity = 0.55f;
        settings.AmbientOcclusion.Power = 1.0f;
    }

    /// <summary>
    /// Restores only Sponza's normal display-referred presentation controls.
    /// The current sky, sun, DDGI layout, and transport remain untouched so a
    /// fixed physical capture can be reviewed through the interactive output.
    /// </summary>
    public static void ApplyPresentationOverlay(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        // Meter the upper-middle of the histogram so deep shade remains shade
        // instead of being lifted toward gray. The 12.5% key follows reflected
        // light-meter calibration. The lower bookmark reaches the former 2x
        // daylight ceiling at only 2.9% average luminance. Preserve that 2x
        // ceiling so covered galleries remain shaded instead of metering gray.
        settings.AutoExposure.Enabled = true;
        settings.AutoExposure.TargetLuminance = 0.125f;
        settings.AutoExposure.MinExposure = 0.25f;
        settings.AutoExposure.MaxExposure = 2.0f;
        settings.AutoExposure.LowPercentile = 70.0f;
        settings.AutoExposure.HighPercentile = 95.0f;
        settings.AutoExposure.DarkToLightAdaptationSpeed = 3.0f;
        settings.AutoExposure.LightToDarkAdaptationSpeed = 1.0f;
        settings.Bloom.Enabled = true;
        settings.Bloom.MipCount = 6;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = true;
        settings.Reflections.Intensity = 1.0f;
        settings.Reflections.GlobalFallbackIntensity = 1.0f;
    }
}
