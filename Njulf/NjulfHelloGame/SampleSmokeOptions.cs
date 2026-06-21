using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Data;

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
    bool EnableAsyncCompute,
    string? BaselineSnapshotDirectory,
    TransparencyMode TransparencyMode = Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend,
    SampleBenchmarkOptions? Benchmark = null)
{
    public SampleBenchmarkOptions Benchmark { get; init; } = Benchmark ?? SampleBenchmarkOptions.Disabled;

    public bool Enabled =>
        Mode != SampleSmokeMode.None ||
        FrameCount > 0 ||
        PerformanceScenario != SamplePerformanceScenario.Normal ||
        TransparencyMode != Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend ||
        EnableAsyncCompute ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory) ||
        Benchmark.Enabled;
}
