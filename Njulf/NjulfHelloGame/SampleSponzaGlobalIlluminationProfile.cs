using System;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Applies the production DDGI preset and Sponza's presentation settings.
/// Sponza deliberately has no scene-specific probe layout: it exercises the
/// same camera-relative rings used by arbitrary interiors and large worlds.
/// Authored volumes remain an explicit, opt-in local quality control.
/// </summary>
public static class SampleSponzaGlobalIlluminationProfile
{
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
        settings.Diagnostics.DdgiForwardEstimateCountersEnabled = true;
    }

    private static void ConfigureReferenceOutput(RenderSettings settings)
    {
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.45f;
        settings.Environment.DiffuseIntensity = 0.10f;
        settings.Environment.SpecularIntensity = 0.25f;
        settings.Reflections.Enabled = true;
        settings.Shadows.DirectionalShadowMapSize = 2048;
        settings.Shadows.DirectionalCascadeCount = 3;
        settings.Shadows.MaxShadowDistance = 120.0f;
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
