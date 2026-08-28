using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Restores scene-neutral outdoor lighting and camera-relative DDGI for Bistro.
/// Bistro is intentionally not tuned with Sponza's plaza lattice, exposure,
/// or Dubrovnik atmosphere. Sharp specular uses the probe-free hybrid path.
/// </summary>
internal static class SampleBistroGlobalIlluminationProfile
{
    // Bistro ships hundreds of cooked BC textures. Selecting the authored
    // 512px mip as their runtime base keeps the complete material set inside
    // the 2 GiB/20%-headroom contract at 1080p without runtime resampling.
    internal const uint DefaultImportedTextureDimension = 512u;

    // Cornell and Bistro are the interactive transition pair. Keeping their
    // AO allocation extent identical prevents a render-target transaction
    // every time the user crosses between the two scenes.
    internal const float TransitionAmbientOcclusionResolutionScale = 0.5f;

    internal static bool ShouldApplyDefaultImportedTextureBudget(
        string? explicitMaximum,
        string? explicitProfile) =>
        string.IsNullOrWhiteSpace(explicitMaximum) &&
        string.IsNullOrWhiteSpace(explicitProfile);

    public static void Configure(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.EmergencyGiFallbackEnabled = false;
        gi.SimpleDdgiLayoutAdmissionMode = SimpleDdgiLayoutAdmissionMode.Degrade;
        gi.SimpleDdgiVerticalRingPolicy =
            SimpleDdgiVerticalRingPolicy.CameraRelativeWithHysteresis;
        gi.DdgiCameraRelativeEnabled = true;
        // Preserve physical contrast between open sunlight and covered
        // storefronts. Scene-local DDGI is already calibrated at unity.
        gi.IndirectIntensity = 1.0f;
        gi.EnvironmentFallbackIntensity = 1.0f;
        // Bistro is an open outdoor courtyard. Its DDGI moments already own
        // broad transport visibility, while the far-field three-cone mask is
        // a four-level voxel estimate intended for enclosed fallback. Feeding
        // that coarse mask through the 12-pixel receiver lattice creates broad
        // dark blotches that the exact per-fragment Flax-style gather does not
        // exhibit. Keep the conservative engine default for enclosed scenes,
        // but omit the redundant mask here; this also removes trace work.
        gi.FarFieldSkyVisibilityEnabled = false;
        // Directional DDGI is Bistro's probe-free reflection base. Preserve a
        // wide overlap band for stable broad lobes; SSR and bounded ray queries
        // still own sharp detail. The hybrid resolve uses DDGI visibility and
        // confidence before it admits the global environment fallback.
        gi.SimpleDdgiRoughSpecularMinimumRoughness = 0.55f;
        gi.SimpleDdgiRoughSpecularFullWeightRoughness = 0.70f;
        // High-class profiles pair DDGI's stable low-frequency and off-screen
        // ownership with the bounded C5 screen-space residual for nearby
        // indirect-light detail.
        ConfigurePostAdvancedGiRollout(settings);
        // Keep the steady-state tier unchanged. During an actual lighting
        // transition, spend the already-bounded urgent lane on the full set of
        // visible near probes so the first useful result lands immediately.
        gi.SimpleDdgiUrgentRelightEnabled = true;
        gi.SimpleDdgiUrgentRelightProbeBudget =
            SimpleDdgiUrgentRelightPolicy.MaximumProbeBudget;
        // Cached solve sweeps reuse a frozen deterministic source sequence.
        // A 0.90 relaxation reaches the same positive, albedo-clamped fixed
        // point faster without adding ray queries or steady-state dispatches.
        gi.SimpleDdgiTransportSolverRelaxation = 0.90f;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        // Bistro uses the three production clipmap rings directly. A transient
        // receiver-driven refinement brick was previously inherited from the
        // Sponza high profile and never reached publication in this scene.
        gi.SimpleDdgiRefinementBricksEnabled = false;
        gi.SimpleDdgiRefinementMaximumBricks = 0;

        ConfigureEnvironment(settings.Environment);
        ConfigurePresentation(settings);
    }

    /// <summary>
    /// Reasserts the quality tier's C5 policy after the global rollout
    /// bootstrap. Explicit smoke/CLI overrides are applied afterwards.
    /// </summary>
    public static void ConfigurePostAdvancedGiRollout(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            settings.QualityPreset is
                RenderQualityPreset.High or
                RenderQualityPreset.DdgiHigh or
                RenderQualityPreset.Ultra
                    ? GlobalIlluminationSettings
                        .DefaultSimpleDdgiNearFieldResidualMode
                    : SimpleDdgiNearFieldResidualMode.Off;
    }

    private static void ConfigureEnvironment(EnvironmentSettings environment)
    {
        environment.Enabled = true;
        // Retain Bistro's warm, location-specific image lighting. Falcor's
        // single intensity cannot be copied into this split-lighting pipeline:
        // the sky, diffuse IBL, DDGI fallback, and specular path would each
        // multiply the same source energy.
        environment.SourceKind = EnvironmentSourceKind.HdrEquirectangular;
        environment.SourcePath =
            "Assets/Bistro_v5_2/san_giuseppe_bridge_4k.hdr";
        environment.TexturePrecision = EnvironmentTexturePrecision.Float16;
        environment.SunDriver = ProceduralSkySunDriver.SceneDirectionalLight;
        environment.AnimateTimeOfDay = false;
        environment.TimeOfDayHours = 14.0f;
        environment.LatitudeDegrees = 59.9139f;
        environment.DayOfYear = 172;
        environment.NorthOffsetDegrees = 0.0f;
        environment.TimeScale = 60.0f;
        environment.Turbidity = 3.0f;
        environment.GroundAlbedo = new Vector3(0.20f);
        environment.SunAngularDiameterDegrees = 0.53f;
        environment.MoonAngularDiameterDegrees = 0.52f;
        environment.AtmosphereIntensity = 1.0f;
        environment.SolarIrradianceScale = 14.0f;
        environment.MoonIrradianceScale = 0.12f;
        environment.StarIntensity = 0.025f;
        environment.AirglowIntensity = 0.025f;
        environment.GiSunStepDegrees = 0.25f;
        // Flax-style source attention: drain a stepped-light cohort promptly
        // while leaving ordinary steady-state maintenance unchanged.
        environment.GiTargetSourceSweepSeconds = 0.5f;
        environment.SpecularPrefilterMipsPerFrame = 1;
        environment.SpecularPrefilterTransitionFrames = 8;
        environment.RotationRadians = 0.0f;
        environment.SkyIntensity = 1.0f;
        environment.DiffuseIntensity = 1.0f;
        environment.SpecularIntensity = 1.0f;
        environment.DebugView = EnvironmentDebugView.None;
    }

    private static void ConfigurePresentation(RenderSettings settings)
    {
        // Bistro contains large sunlit exterior areas. Allow the meter to move
        // below Sponza's 0.25 exposure floor instead of clipping those surfaces.
        settings.AutoExposure.Enabled = true;
        settings.AutoExposure.TargetLuminance = 0.19f;
        settings.AutoExposure.MinExposure = 0.03125f;
        settings.AutoExposure.MaxExposure = 4.0f;
        settings.AutoExposure.LowPercentile = 70.0f;
        settings.AutoExposure.HighPercentile = 95.0f;
        settings.AutoExposure.DarkToLightAdaptationSpeed = 3.0f;
        settings.AutoExposure.LightToDarkAdaptationSpeed = 1.0f;

        settings.Shadows.DirectionalShadowsEnabled = true;
        settings.Shadows.DirectionalShadowMapSize = 2048;
        settings.Shadows.DirectionalCascadeCount = 3;
        settings.Shadows.MaxShadowDistance = 120.0f;
        settings.Shadows.PcfRadius = 1;

        settings.AmbientOcclusion.Enabled = true;
        // DDGI visibility owns broad outdoor transport. Half-resolution GTAO
        // retains the small contact band without spending a second full-screen
        // 32-sample solve over Bistro's dense facade geometry.
        settings.AmbientOcclusion.ResolutionScale =
            TransitionAmbientOcclusionResolutionScale;
        settings.AmbientOcclusion.SampleCount = 16;
        settings.AmbientOcclusion.Radius = 0.65f;
        settings.AmbientOcclusion.Intensity = 0.70f;
        settings.AmbientOcclusion.Power = 1.0f;

        settings.Bloom.Enabled = true;
        settings.Bloom.MipCount = 6;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = true;
        settings.Reflections.Intensity = 1.0f;
        settings.Reflections.GlobalFallbackIntensity = 1.0f;
    }
}
