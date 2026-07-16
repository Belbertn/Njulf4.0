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
    bool EnableFarFieldClipmap,
    bool EnableFarFieldForceAll,
    string? BaselineSnapshotDirectory,
    DdgiSchedulerMode? DdgiSchedulerModeOverride = null,
    SampleSceneKind SceneKind = SampleSceneKind.GlobalIlluminationTest,
    TransparencyMode TransparencyMode = Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend,
    SampleBenchmarkOptions? Benchmark = null,
    string? SponzaGiCaptureDirectory = null)
{
    public SampleBenchmarkOptions Benchmark { get; init; } = Benchmark ?? SampleBenchmarkOptions.Disabled;

    public bool Enabled =>
        Mode != SampleSmokeMode.None ||
        FrameCount > 0 ||
        SceneKind != SampleSceneKind.GlobalIlluminationTest ||
        PerformanceScenario != SamplePerformanceScenario.Normal ||
        TransparencyMode != Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend ||
        EnableAsyncCompute ||
        EnableFarFieldClipmap ||
        EnableFarFieldForceAll ||
        DdgiSchedulerModeOverride.HasValue ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaGiCaptureDirectory) ||
        Benchmark.Enabled;
}
