using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

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
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

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
        settings.Shadows.DirectionalShadowMapSize = 4096;
        settings.Shadows.DirectionalCascadeCount = ShadowSettings.MaxDirectionalCascades;
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
