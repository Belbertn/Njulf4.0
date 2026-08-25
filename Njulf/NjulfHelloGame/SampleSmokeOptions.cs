using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

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
    SampleSponzaGiCaptureMode SponzaGiCaptureMode =
        SampleSponzaGiCaptureMode.DetailedDiagnostics,
    string? SponzaTemporalCaptureDirectory = null,
    string? SponzaTemporalAnalyzeDirectory = null,
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
    SimpleDdgiSampledAtlasCoverageMode? SimpleDdgiSampledAtlasCoverageModeOverride = null,
    bool EnableGpuMeshletCounters = false,
    bool EnableDdgiContentConformance = false,
    string? AdvancedGiPrerequisiteManifestPath = null,
    string? AdvancedGiQualificationManifestPath = null,
    SimpleDdgiReceiverFeedbackMode? SimpleDdgiReceiverFeedbackModeOverride = null,
    DdgiOpacityMicromapMode? DdgiOpacityMicromapModeOverride = null,
    SimpleDdgiDirectionalGuidingMode? SimpleDdgiDirectionalGuidingModeOverride = null,
    GiCausticMode? GiCausticModeOverride = null,
    SimpleDdgiNearFieldResidualMode? SimpleDdgiNearFieldResidualModeOverride = null,
    string? SimpleDdgiReceiverFeedbackQualificationId = null,
    string? DdgiOpacityMicromapQualificationId = null,
    string? SimpleDdgiDirectionalGuidingQualificationId = null,
    string? GiCausticQualificationId = null,
    string? SimpleDdgiNearFieldResidualQualificationId = null,
    string? AdvancedGiRuntimeEvidenceBundlePath = null,
    string? AdvancedGiStartupProfilePath = null,
    string? BistroQualityCaptureDirectory = null,
    SampleBistroQualityCaptureVariant BistroQualityCaptureVariant =
        SampleBistroQualityCaptureVariant.SunScaleStep,
    SampleBenchmarkQualitySequenceOptions? BenchmarkQualitySequence = null,
    FogDebugView? FogDebugViewOverride = null,
    FogDebugProjection? FogDebugProjectionOverride = null,
    int? FogDebugSliceOverride = null,
    string? VolumetricTemporalCaptureDirectory = null,
    string? VolumetricTemporalAnalyzeDirectory = null,
    SampleSponzaFixtureMode SponzaFixtureMode =
        SampleSponzaFixtureMode.Architecture)
{
    public SampleBenchmarkOptions Benchmark { get; init; } = Benchmark ?? SampleBenchmarkOptions.Disabled;
    public SampleBenchmarkQualitySequenceOptions BenchmarkQualitySequence { get; init; } =
        BenchmarkQualitySequence ?? SampleBenchmarkQualitySequenceOptions.Disabled;

    /// <summary>
    /// Reopens the editor after an editor-initiated renderer reconstruction.
    /// It is host state, not a command-line smoke-test input.
    /// </summary>
    public bool OpenEditorOnStartup { get; init; }

    public bool Enabled =>
        Mode != SampleSmokeMode.None ||
        FrameCount > 0 ||
        SceneKind != SampleSceneKind.GlobalIlluminationTest ||
        PerformanceScenario != SamplePerformanceScenario.Normal ||
        TransparencyMode != Njulf.Rendering.Data.TransparencyMode.SortedAlphaBlend ||
        EnableGpuMeshletCounters ||
        EnableDdgiContentConformance ||
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
        FogDebugViewOverride.HasValue ||
        FogDebugProjectionOverride.HasValue ||
        FogDebugSliceOverride.HasValue ||
        SponzaFixtureMode != SampleSponzaFixtureMode.Architecture ||
        EnableFarFieldClipmap ||
        EnableFarFieldForceAll ||
        !string.IsNullOrWhiteSpace(BaselineSnapshotDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaTemporalCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(SponzaTemporalAnalyzeDirectory) ||
        !string.IsNullOrWhiteSpace(VolumetricTemporalCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(VolumetricTemporalAnalyzeDirectory) ||
        !string.IsNullOrWhiteSpace(BistroQualityCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(MaterialGiCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(AdvancedGiPrerequisiteManifestPath) ||
        !string.IsNullOrWhiteSpace(AdvancedGiQualificationManifestPath) ||
        !string.IsNullOrWhiteSpace(AdvancedGiRuntimeEvidenceBundlePath) ||
        !string.IsNullOrWhiteSpace(AdvancedGiStartupProfilePath) ||
        SimpleDdgiReceiverFeedbackModeOverride.HasValue ||
        DdgiOpacityMicromapModeOverride.HasValue ||
        SimpleDdgiDirectionalGuidingModeOverride.HasValue ||
        GiCausticModeOverride.HasValue ||
        SimpleDdgiNearFieldResidualModeOverride.HasValue ||
        !string.IsNullOrWhiteSpace(LongRunReportPath) ||
        LongRunMinutes > 0.0 ||
        KhronosMaterialGiRenderedGate is not null ||
        TailDdgiLongSoak ||
        Benchmark.Enabled ||
        BenchmarkQualitySequence.Enabled;

    public bool UsesDeterministicSimulationClock =>
        Benchmark.Enabled ||
        BenchmarkQualitySequence.Enabled ||
        TailDdgiLongSoak ||
        !string.IsNullOrWhiteSpace(SponzaTemporalCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(VolumetricTemporalCaptureDirectory) ||
        !string.IsNullOrWhiteSpace(BistroQualityCaptureDirectory) ||
        PerformanceScenario ==
            SamplePerformanceScenario.BistroQualityMotionRelight;
}
