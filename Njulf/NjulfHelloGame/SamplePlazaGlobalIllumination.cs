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
    private const string LegacyDenseAlleyVolumeName = "Dense Alley DDGI";
    private const int CameraRelativeClipmapCascadeCount = 3;
    private const int CameraRelativeClipmapProbeCountX = 24;
    private const int CameraRelativeClipmapProbeCountY = 14;
    private const int CameraRelativeClipmapProbeCountZ = 24;
    private const float CameraRelativeClipmapBaseSpacing = 1.0f;
    private const float CameraRelativeClipmapVerticalCenterOffset = 6.25f;
    private const int CameraRelativeClipmapProbeBudget =
        CameraRelativeClipmapProbeCountX *
        CameraRelativeClipmapProbeCountY *
        CameraRelativeClipmapProbeCountZ *
        CameraRelativeClipmapCascadeCount;

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

        settings.ApplyQualityPreset(RenderQualityPreset.DdgiHigh);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = false;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.DdgiCameraRelativeEnabled = true;
        gi.DdgiProbeClassificationEnabled = true;
        gi.DdgiProbeRelocationEnabled = true;
        gi.DdgiClipmapCascadeCount = CameraRelativeClipmapCascadeCount;
        gi.DdgiClipmapProbeCountX = CameraRelativeClipmapProbeCountX;
        gi.DdgiClipmapProbeCountY = CameraRelativeClipmapProbeCountY;
        gi.DdgiClipmapProbeCountZ = CameraRelativeClipmapProbeCountZ;
        gi.DdgiClipmapBaseSpacing = CameraRelativeClipmapBaseSpacing;
        gi.DdgiClipmapVerticalCenterOffset = CameraRelativeClipmapVerticalCenterOffset;
        gi.DdgiMaxActiveProbes = Math.Min(
            GlobalIlluminationSettings.AbsoluteDdgiMaxActiveProbeBudget,
            Math.Max(gi.DdgiMaxActiveProbes, CameraRelativeClipmapProbeBudget));
        gi.DdgiCascade0RaysPerProbe = 96;
        gi.DdgiCascade1RaysPerProbe = 64;
        gi.DdgiCascade2RaysPerProbe = 48;
        gi.DdgiCascade3RaysPerProbe = 32;
        gi.IndirectIntensity = 1.85f;
        gi.EnvironmentFallbackIntensity = 0.12f;
        gi.ResolutionScale = 0.5f;
        gi.MaxBounceDistance = 14.0f;
        gi.TemporalEnabled = false;
        gi.DenoiserEnabled = false;

        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.45f;
        settings.Environment.DiffuseIntensity = 0.10f;
        settings.Environment.SpecularIntensity = 0.25f;
        settings.Reflections.Enabled = true;
        settings.Shadows.DirectionalShadowMapSize = 2048;
        settings.Shadows.DirectionalCascadeCount = 3;
        settings.Shadows.MaxShadowDistance = 120.0f;
        settings.Shadows.PcfRadius = 2;
        settings.Shadows.SpotShadowsEnabled = false;
        settings.Shadows.MaxShadowedSpotLights = 0;
        settings.Shadows.PointShadowsEnabled = false;
        settings.Shadows.MaxShadowedPointLights = 0;
        settings.AmbientOcclusion.Enabled = true;
        settings.AmbientOcclusion.Radius = 0.45f;
        settings.AmbientOcclusion.Intensity = 0.55f;
        settings.AmbientOcclusion.Power = 1.0f;
    }

    private static void ConfigureMediumMemoryRenderSettings(RenderSettings settings)
    {
        settings.ApplyQualityPreset(RenderQualityPreset.Medium);

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ssgi;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = true;
        gi.UseDdgi = false;
        gi.UseRayQueryBackend = false;
        gi.IndirectIntensity = 0.85f;
        gi.EnvironmentFallbackIntensity = 0.45f;
        gi.ResolutionScale = 0.5f;
        gi.MaxBounceDistance = 8.0f;
        gi.TemporalEnabled = true;
        gi.DenoiserEnabled = true;

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
        gi.UseSsgi = false;
        gi.UseDdgi = false;
        gi.UseRayQueryBackend = false;
        gi.IndirectIntensity = 0.0f;
        gi.EnvironmentFallbackIntensity = 1.0f;

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
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.45f;
        settings.Environment.DiffuseIntensity = 0.10f;
        settings.Environment.SpecularIntensity = 0.25f;
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
        RemoveLegacyDenseAlleyVolume(scene);
    }

    private static void RemoveLegacyDenseAlleyVolume(Scene scene)
    {
        for (int i = scene.GlobalIlluminationProbeVolumes.Count - 1; i >= 0; i--)
        {
            GlobalIlluminationProbeVolume volume = scene.GlobalIlluminationProbeVolumes[i];
            if (string.Equals(volume.Name, LegacyDenseAlleyVolumeName, StringComparison.Ordinal))
                scene.Remove(volume);
        }
    }
}
