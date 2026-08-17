using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Restores scene-neutral outdoor lighting and camera-relative DDGI for Bistro.
/// Bistro is intentionally not tuned with Sponza's plaza lattice, exposure,
/// reflection probes, or Dubrovnik atmosphere.
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
        gi.IndirectIntensity = 1.0f;
        gi.EnvironmentFallbackIntensity = 1.0f;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        // Bistro uses the three production clipmap rings directly. A transient
        // receiver-driven refinement brick was previously inherited from the
        // Sponza high profile and never reached publication in this scene.
        gi.SimpleDdgiRefinementBricksEnabled = false;
        gi.SimpleDdgiRefinementMaximumBricks = 0;

        ConfigureEnvironment(settings.Environment);
        ConfigurePresentation(settings);
    }

    private static void ConfigureEnvironment(EnvironmentSettings environment)
    {
        environment.Enabled = true;
        environment.SourceKind = EnvironmentSourceKind.ProceduralSky;
        environment.SourcePath = null;
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
        environment.GiTargetSourceSweepSeconds = 8.0f;
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
        settings.AutoExposure.TargetLuminance = 0.18f;
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
        settings.AmbientOcclusion.Intensity = 0.70f;
        settings.AmbientOcclusion.Power = 1.0f;

        settings.Bloom.Enabled = true;
        settings.Bloom.MipCount = 6;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = true;
        settings.Reflections.Mode = ReflectionMode.StaticProbes;
        settings.Reflections.MaxProbesPerPixel = 2;
        settings.Reflections.Intensity = 1.0f;
        settings.Reflections.GlobalFallbackIntensity = 1.0f;
        settings.Reflections.CaptureOnLoad = false;
        settings.Reflections.CaptureIncludesDdgi = false;
        settings.Reflections.MaxProbeCapturesPerFrame = 0;
        settings.Reflections.MaxConcurrentProbeCaptures = 1;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = 1;
        settings.Reflections.MaxProbePrefilterMipsPerFrame = 1;
    }
}
