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
    bool EnableGpuTiming,
    bool EnableSceneGpuCompaction,
    bool EnableSceneIndirectDispatch,
    bool EnableSceneGpuLodSelection,
    bool EnableSceneGpuShadowCompaction,
    bool EnableSceneSubmissionValidation,
    string? BaselineSnapshotDirectory)
{
    public bool Enabled =>
        Mode != SampleSmokeMode.None ||
        FrameCount > 0 ||
        PerformanceScenario != SamplePerformanceScenario.Normal ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory);
}
