using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

internal enum SamplePlazaGpuMemoryProfile
{
    High,
    Medium,
    Low
}

internal static class SamplePlazaGlobalIllumination
{
    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        ConfigureRenderSettingsForMemoryProfile(settings, SamplePlazaGpuMemoryProfile.High);
    }

    public static void ConfigureRenderSettingsForMemoryProfile(
        RenderSettings settings,
        SamplePlazaGpuMemoryProfile memoryProfile)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        if (memoryProfile == SamplePlazaGpuMemoryProfile.Low)
        {
            ConfigureLowMemoryRenderSettings(settings);
            return;
        }

        if (memoryProfile == SamplePlazaGpuMemoryProfile.Medium)
        {
            ConfigureMediumMemoryRenderSettings(settings);
            return;
        }

        SampleSponzaGlobalIlluminationProfile.Configure(settings);
    }

    private static void ConfigureMediumMemoryRenderSettings(RenderSettings settings)
    {
        settings.ApplyQualityPreset(RenderQualityPreset.Medium);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 0.85f;
        gi.EnvironmentFallbackIntensity = 0.45f;
        gi.ResolutionScale = 0.5f;
        gi.MaxBounceDistance = 8.0f;
        gi.TemporalEnabled = true;
        gi.DenoiserEnabled = true;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        ConfigureSharedLighting(settings);
        settings.Reflections.Enabled = true;
        settings.Reflections.MaxProbesPerPixel = 1;
        settings.Shadows.DirectionalShadowMapSize = 2048;
        settings.Shadows.DirectionalCascadeCount = 2;
        settings.Shadows.MaxShadowDistance = 80.0f;
        settings.Shadows.PcfRadius = 1;
        settings.AmbientOcclusion.Enabled = true;
        settings.AmbientOcclusion.ResolutionScale = 0.5f;
        settings.AmbientOcclusion.SampleCount = 16;
        settings.AmbientOcclusion.Radius = 0.45f;
        settings.AmbientOcclusion.Intensity = 0.5f;
        settings.AmbientOcclusion.Power = 1.0f;
    }

    private static void ConfigureLowMemoryRenderSettings(RenderSettings settings)
    {
        settings.ApplyQualityPreset(RenderQualityPreset.Low);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = false;
        gi.Mode = GlobalIlluminationMode.Disabled;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseDdgi = false;
        gi.UseRayQueryBackend = false;
        gi.IndirectIntensity = 0.0f;
        gi.EnvironmentFallbackIntensity = 1.0f;
        gi.SimpleDdgiAuthoredVolumes.Clear();

        ConfigureSharedLighting(settings);
        settings.Reflections.Enabled = false;
        settings.Shadows.DirectionalShadowMapSize = 1024;
        settings.Shadows.DirectionalCascadeCount = 1;
        settings.Shadows.MaxShadowDistance = 60.0f;
        settings.Shadows.PcfRadius = 1;
        settings.AmbientOcclusion.Enabled = false;
    }

    private static void ConfigureSharedLighting(RenderSettings settings)
    {
        // Reduced-memory rendering tiers retain the complete dynamic
        // atmosphere even when their GI backend has to degrade.
        SampleSponzaGlobalIlluminationProfile.ConfigureDynamicEnvironment(settings);
        settings.Shadows.SpotShadowsEnabled = false;
        settings.Shadows.MaxShadowedSpotLights = 0;
        settings.Shadows.PointShadowsEnabled = false;
        settings.Shadows.MaxShadowedPointLights = 0;
    }

    public static void ConfigureSceneLighting(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        scene.AmbientLight = new Color(0.0f, 0.0f, 0.0f, 1.0f);
    }
}
