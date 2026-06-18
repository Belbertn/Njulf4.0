using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleSmokeOptions(
    SampleSmokeMode Mode,
    int FrameCount,
    int SceneReloadCount,
    string? StartupLogPath,
    string? HealthReportPath,
    RendererValidationMode ValidationMode,
    bool FailOnValidationMessage,
    bool ForceMissingAssets,
    SamplePerformanceScenario PerformanceScenario,
    bool EnableGpuTiming)
{
    public bool Enabled => Mode != SampleSmokeMode.None || FrameCount > 0 || PerformanceScenario != SamplePerformanceScenario.Normal;
}
