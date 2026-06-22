using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public static class SampleGlobalIlluminationValidation
{
    public static bool IsValidationScenario(SamplePerformanceScenario scenario)
    {
        return scenario is SamplePerformanceScenario.GiCornellRoom
            or SamplePerformanceScenario.GiThinWallLeakTest
            or SamplePerformanceScenario.GiMovingPointLight
            or SamplePerformanceScenario.GiMovingRigidObject
            or SamplePerformanceScenario.GiBrightExteriorRoom;
    }

    public static void ConfigureRenderSettings(RenderSettings settings, SamplePerformanceScenario scenario)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!IsValidationScenario(scenario))
            return;

        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.DynamicResolution.MinimumScale = 1.0f;
        settings.DynamicResolution.MaximumScale = 1.0f;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 1.0f;
        settings.Bloom.Enabled = false;
        settings.Fog.Enabled = false;
        settings.Reflections.Enabled = false;
        settings.AmbientOcclusion.Enabled = false;
        settings.Shadows.PointShadowMapSize = 1024;
        settings.Shadows.PointNormalBias = 0.008f;
        settings.Shadows.PointConstantDepthBias = 0.0003f;
        settings.Shadows.PointPcfRadius = 1;

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.RayQueryHybrid;
        gi.DebugView = GlobalIlluminationDebugView.None;
        gi.UseSsgi = true;
        gi.UseDdgi = true;
        gi.UseRayQueryBackend = true;
        gi.IndirectIntensity = 1.5f;
        gi.EnvironmentFallbackIntensity = 0.65f;
        gi.ResolutionScale = 0.5f;
        gi.MaxBounceDistance = 10.0f;
        gi.SsgiMaxDistance = 3.0f;
        gi.SsgiThickness = 0.04f;
        gi.SsgiHitNormalThreshold = 0.15f;
        gi.TemporalEnabled = true;
        gi.DenoiserEnabled = true;
        gi.HistoryResponsiveness = 0.12f;
    }
}
