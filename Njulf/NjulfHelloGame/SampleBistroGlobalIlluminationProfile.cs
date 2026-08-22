using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Restores scene-neutral outdoor lighting and camera-relative DDGI for Bistro.
/// Bistro is intentionally not tuned with Sponza's plaza lattice, exposure,
/// or Dubrovnik atmosphere. It owns a small pair of local reflection probes.
/// </summary>
internal static class SampleBistroGlobalIlluminationProfile
{
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
        // Lift shaded storefronts with bounced light instead of increasing
        // exposure and clipping the sunlit plaster and scooter highlights.
        gi.IndirectIntensity = 1.65f;
        gi.EnvironmentFallbackIntensity = 1.0f;
        // Bistro is an open outdoor courtyard. Its DDGI moments already own
        // broad transport visibility, while the far-field three-cone mask is
        // a four-level voxel estimate intended for enclosed fallback. Feeding
        // that coarse mask through the 12-pixel receiver lattice creates broad
        // dark blotches that the exact per-fragment Flax-style gather does not
        // exhibit. Keep the conservative engine default for enclosed scenes,
        // but omit the redundant mask here; this also removes trace work.
        gi.FarFieldSkyVisibilityEnabled = false;
        // The low-frequency receiver cache cannot reproduce the high-frequency
        // visibility changes of Bistro's carved stone at every fragment. Let
        // material/screen AO own rough-specular contact occlusion in this open
        // exterior scene while the exact DDGI visibility remains available to
        // diffuse transport. This removes cache-shaped reflection blotches
        // without another gather, texture read, or dispatch.
        gi.SimpleDdgiRoughSpecularMinimumRoughness = 1.0f;
        gi.SimpleDdgiRoughSpecularFullWeightRoughness = 1.0f;
        // Establish DDGI as the sole diffuse-indirect owner while its quality
        // baseline is being qualified. C5 currently changes the Bistro/Sponza
        // image by less than one display code value while paying for motion
        // vectors and a full residual pass. Keep the explicit experiment/CLI
        // override available, but do not spend that cost in production.
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
    /// Reasserts Bistro's scene-local advanced-GI policy after
    /// the global rollout bootstrap. Explicit smoke/CLI overrides are applied
    /// by the caller after this hook and therefore still take precedence.
    /// </summary>
    public static void ConfigurePostAdvancedGiRollout(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.GlobalIllumination.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.Off;
    }

    private static void ConfigureEnvironment(EnvironmentSettings environment)
    {
        environment.Enabled = true;
        // Match Bistro's authored Falcor scene instead of replacing its warm,
        // location-specific image lighting with the generic procedural sky.
        // The source scene specifies this bundled map at intensity 10.
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
        environment.SkyIntensity = 10.0f;
        environment.DiffuseIntensity = 10.0f;
        // Falcor's single environment multiplier cannot be copied directly to
        // this split-lighting pipeline: multiplying the already-prefiltered
        // specular lobe by ten clips painted metal and exposes coarse mip
        // footprints. Keep authored sky/diffuse energy but normalize specular.
        environment.SpecularIntensity = 1.5f;
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
        settings.AmbientOcclusion.Radius = 0.65f;
        settings.AmbientOcclusion.Intensity = 0.55f;
        settings.AmbientOcclusion.Power = 1.0f;

        settings.Bloom.Enabled = true;
        settings.Bloom.MipCount = 6;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = true;
        settings.Reflections.Mode = ReflectionMode.StaticProbes;
        // Bistro's higher-priority cafe probe covers the presentation view.
        // Selecting a single local source avoids paying for two cubemap reads
        // per shaded pixel while the broad courtyard probe remains available
        // as the spatial fallback outside that volume.
        settings.Reflections.MaxProbesPerPixel = 1;
        settings.Reflections.Intensity = 1.0f;
        settings.Reflections.GlobalFallbackIntensity = 1.0f;
        settings.Reflections.CaptureOnLoad = true;
        // Publish a useful direct-lit local source first. HelloGame promotes
        // these probes to DDGI-fed recaptures only after the field reports a
        // current admitted source and propagation generation; a missing GI
        // certificate must never leave every local probe permanently queued.
        settings.Reflections.CaptureIncludesDdgi = false;
        settings.Reflections.MaxProbeCapturesPerFrame = 1;
        settings.Reflections.MaxConcurrentProbeCaptures = 1;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = 1;
        settings.Reflections.MaxProbePrefilterMipsPerFrame = 1;
        settings.Reflections.ReflectionCaptureGpuBudgetMicroseconds = 500;
        settings.Reflections.MinimumEnvironmentRecaptureIntervalSeconds = 0.5f;

    }
}
