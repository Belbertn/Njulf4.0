using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

internal static class SamplePlazaGlobalIllumination
{
    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.RayQueryHybrid;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = true;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.DdgiCameraRelativeEnabled = true;
        gi.DdgiProbeClassificationEnabled = true;
        gi.DdgiProbeRelocationEnabled = false;
        gi.IndirectIntensity = 1.2f;
        gi.EnvironmentFallbackIntensity = 0.0f;
        gi.ResolutionScale = 0.5f;
        gi.MaxBounceDistance = 14.0f;
        gi.SsgiMaxDistance = 4.0f;
        gi.SsgiThickness = 0.05f;
        gi.SsgiHitNormalThreshold = 0.15f;
        gi.TemporalEnabled = true;
        gi.DenoiserEnabled = true;
        gi.HistoryResponsiveness = 0.14f;

        settings.Environment.Enabled = false;
        settings.Environment.SkyIntensity = 0.0f;
        settings.Environment.DiffuseIntensity = 0.0f;
        settings.Environment.SpecularIntensity = 0.0f;
        settings.Reflections.Enabled = false;
    }

    public static void ConfigureSceneLighting(Scene scene)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        scene.AmbientLight = new Color(0.0f, 0.0f, 0.0f, 1.0f);
    }
}
