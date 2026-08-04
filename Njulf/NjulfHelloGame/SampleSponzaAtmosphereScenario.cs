using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public enum SampleSponzaAtmosphereStage : byte
{
    Animated,
    FreezeAfterOneQuantizedStep,
    ReflectionLifecycle
}

public readonly record struct SampleSponzaAtmosphereScenarioSpec(
    SamplePerformanceScenario Scenario,
    SampleSponzaAtmosphereStage Stage,
    bool AnimateTimeOfDay,
    bool FreezeAfterFirstStep,
    bool EnableReflections,
    bool CaptureIncludesDdgi,
    int MaximumCapturesPerFrame,
    int MaximumCaptureFacesPerFrame,
    int MaximumPrefilterMipsPerFrame,
    float GiSunStepDegrees,
    float GiTargetSourceSweepSeconds);

/// <summary>
/// The deterministic Sponza scenarios share one authored scene identity. This contract keeps
/// report labels, environment quantization, and reflection budgets aligned across the CLI and
/// interactive scenario selector.
/// </summary>
public static class SampleSponzaAtmosphereScenario
{
    public static SampleSponzaAtmosphereScenarioSpec Resolve(SamplePerformanceScenario scenario) =>
        scenario switch
        {
            SamplePerformanceScenario.GiSponzaAnimatedAtmosphere => new(
                scenario,
                SampleSponzaAtmosphereStage.Animated,
                AnimateTimeOfDay: true,
                FreezeAfterFirstStep: false,
                EnableReflections: true,
                CaptureIncludesDdgi: true,
                MaximumCapturesPerFrame: 1,
                MaximumCaptureFacesPerFrame: 2,
                MaximumPrefilterMipsPerFrame: 1,
                GiSunStepDegrees: 0.25f,
                GiTargetSourceSweepSeconds: 8.0f),
            SamplePerformanceScenario.GiSponzaFreezeAfterAtmosphereStep => new(
                scenario,
                SampleSponzaAtmosphereStage.FreezeAfterOneQuantizedStep,
                AnimateTimeOfDay: true,
                FreezeAfterFirstStep: true,
                EnableReflections: true,
                CaptureIncludesDdgi: true,
                MaximumCapturesPerFrame: 1,
                MaximumCaptureFacesPerFrame: 2,
                MaximumPrefilterMipsPerFrame: 1,
                GiSunStepDegrees: 0.25f,
                GiTargetSourceSweepSeconds: 8.0f),
            SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle => new(
                scenario,
                SampleSponzaAtmosphereStage.ReflectionLifecycle,
                AnimateTimeOfDay: false,
                FreezeAfterFirstStep: false,
                EnableReflections: true,
                CaptureIncludesDdgi: true,
                MaximumCapturesPerFrame: 1,
                MaximumCaptureFacesPerFrame: 2,
                MaximumPrefilterMipsPerFrame: 1,
                GiSunStepDegrees: 0.25f,
                GiTargetSourceSweepSeconds: 8.0f),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Not a Sponza atmosphere scenario.")
        };

    public static bool IsScenario(SamplePerformanceScenario scenario) =>
        scenario is SamplePerformanceScenario.GiSponzaAnimatedAtmosphere
            or SamplePerformanceScenario.GiSponzaFreezeAfterAtmosphereStep
            or SamplePerformanceScenario.GiSponzaReflectionProbeLifecycle;

    public static void Configure(RenderSettings settings, SamplePerformanceScenario scenario)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        SampleSponzaAtmosphereScenarioSpec spec = Resolve(scenario);
        SampleSponzaGlobalIlluminationProfile.Configure(settings);
        settings.ResolutionScale = 1.0f;
        settings.DynamicResolution.Enabled = false;
        settings.AutoExposure.Enabled = false;
        settings.Exposure = 1.0f;
        settings.Bloom.Enabled = false;
        settings.Fog.Enabled = false;
        settings.Environment.GiSunStepDegrees = spec.GiSunStepDegrees;
        settings.Environment.GiTargetSourceSweepSeconds = spec.GiTargetSourceSweepSeconds;
        settings.Environment.TimeScale = 60.0f;
        settings.Environment.AnimateTimeOfDay = spec.AnimateTimeOfDay;
        settings.Environment.SunDriver = ProceduralSkySunDriver.AstronomicalTime;
        settings.Reflections.Enabled = spec.EnableReflections;
        settings.Reflections.Mode = ReflectionMode.StaticProbes;
        settings.Reflections.CaptureOnLoad = true;
        settings.Reflections.CaptureIncludesDdgi = spec.CaptureIncludesDdgi;
        settings.Reflections.MaxProbeCapturesPerFrame = spec.MaximumCapturesPerFrame;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = spec.MaximumCaptureFacesPerFrame;
        settings.Reflections.MaxProbePrefilterMipsPerFrame = spec.MaximumPrefilterMipsPerFrame;
        settings.Reflections.MinimumEnvironmentRecaptureIntervalSeconds = 0.25f;
        settings.Reflections.MaximumEnvironmentCaptureAgeSeconds = 30.0f;
    }
}
