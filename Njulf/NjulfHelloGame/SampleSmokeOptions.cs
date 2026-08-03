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
    string? SponzaGiCaptureDirectory = null,
    AsyncComputeMode? AsyncComputeModeOverride = null,
    string? MaterialGiCaptureDirectory = null,
    RenderQualityPreset? QualityPresetOverride = null,
    string? LongRunReportPath = null,
    int LongRunWarmupFrames = 120,
    int LongRunSampleInterval = 15,
    int LongRunMaxRetainedSamples = 256,
    ulong LongRunMemoryGrowthToleranceBytes = 1_048_576,
    double LongRunMinutes = 0.0,
    SampleKhronosMaterialGiRenderedGateOptions? KhronosMaterialGiRenderedGate = null,
    string? MaterialGiQualificationManifestPath = null,
    AsyncComputePath? AsyncComputeValidationPath = null)
{
    public SampleBenchmarkOptions Benchmark { get; init; } = Benchmark ?? SampleBenchmarkOptions.Disabled;

    public bool Enabled =>
        Mode != SampleSmokeMode.None ||
        FrameCount > 0 ||
        SceneKind != SampleSceneKind.GlobalIlluminationTest ||
        PerformanceScenario != SamplePerformanceScenario.Normal ||
        TransparencyMode != Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend ||
        EnableAsyncCompute ||
        AsyncComputeModeOverride.HasValue ||
        QualityPresetOverride.HasValue ||
        EnableFarFieldClipmap ||
        EnableFarFieldForceAll ||
        DdgiSchedulerModeOverride.HasValue ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(MaterialGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(LongRunReportPath) ||
        LongRunMinutes > 0.0 ||
        KhronosMaterialGiRenderedGate is not null ||
        Benchmark.Enabled;
}
