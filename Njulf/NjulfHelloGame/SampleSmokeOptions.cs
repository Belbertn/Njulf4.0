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
    AsyncComputePath? AsyncComputeValidationPath = null,
    SimpleDdgiSchedulerMode? SimpleDdgiSchedulerModeOverride = null,
    bool TailDdgiLongSoak = false,
    SimpleDdgiProbeResidencyMode? SimpleDdgiProbeResidencyModeOverride = null,
    int? SimpleDdgiSparsePhysicalPageBudgetOverride = null,
    int? SimpleDdgiSparseMinimumPhysicalPageBudgetOverride = null,
    int? SimpleDdgiSparseRetentionFramesOverride = null,
    int? SimpleDdgiSparseMaximumAdmissionsOverride = null,
    int? SimpleDdgiSparseMaximumReceiverFeedbackOverride = null,
    int? SimpleDdgiSparseInactiveRetryFramesOverride = null,
    SimpleDdgiStoragePackingMode? SimpleDdgiStoragePackingModeOverride = null,
    SimpleDdgiSampledAtlasCoverageMode? SimpleDdgiSampledAtlasCoverageModeOverride = null)
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
        SimpleDdgiSchedulerModeOverride.HasValue ||
        SimpleDdgiProbeResidencyModeOverride.HasValue ||
        SimpleDdgiSparsePhysicalPageBudgetOverride.HasValue ||
        SimpleDdgiSparseMinimumPhysicalPageBudgetOverride.HasValue ||
        SimpleDdgiSparseRetentionFramesOverride.HasValue ||
        SimpleDdgiSparseMaximumAdmissionsOverride.HasValue ||
        SimpleDdgiSparseMaximumReceiverFeedbackOverride.HasValue ||
        SimpleDdgiSparseInactiveRetryFramesOverride.HasValue ||
        SimpleDdgiStoragePackingModeOverride.HasValue ||
        SimpleDdgiSampledAtlasCoverageModeOverride.HasValue ||
        QualityPresetOverride.HasValue ||
        EnableFarFieldClipmap ||
        EnableFarFieldForceAll ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(MaterialGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(LongRunReportPath) ||
        LongRunMinutes > 0.0 ||
        KhronosMaterialGiRenderedGate is not null ||
        TailDdgiLongSoak ||
        Benchmark.Enabled;

    public bool UsesDeterministicSimulationClock =>
        Benchmark.Enabled || TailDdgiLongSoak;
}
