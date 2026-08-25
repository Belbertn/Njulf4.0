using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkReport(
    string Kind,
    DateTimeOffset CapturedAtUtc,
    [property: JsonRequired] SampleBenchmarkOptions Options,
    [property: JsonRequired] SamplePerformanceScenario Scenario,
    int WarmupFrameCount,
    int MeasurementFrameCount,
    int FirstMeasurementFrameIndex,
    int LastMeasurementFrameIndex,
    SampleBenchmarkTimingStats CpuFrameMilliseconds,
    SampleBenchmarkTimingStats GpuFrameMilliseconds,
    [property: JsonRequired] int GpuTimingSupported,
    [property: JsonRequired] int GpuTimingValidSampleCount,
    string GpuTimingUnavailableReason,
    IReadOnlyList<SampleBenchmarkTimingStats> GpuPasses,
    IReadOnlyList<SampleBenchmarkTimingStats> CpuStages,
    IReadOnlyList<SampleBenchmarkFinding> Findings,
    IReadOnlyList<BudgetMetric> BudgetMetrics,
    [property: JsonRequired] RendererDiagnostics LastDiagnostics)
{
    [JsonRequired]
    public string Schema { get; init; } =
        MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema;

    [JsonPropertyName("producerIdentity")]
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }

    public SampleDdgiProductionGateReport? DdgiProductionGate { get; init; }
    public IReadOnlyList<SampleGiAccuracyOracleResult> AccuracyOracleResults { get; init; } =
        Array.Empty<SampleGiAccuracyOracleResult>();
    [JsonRequired]
    public SampleBenchmarkCaptureContract CaptureContract { get; init; } =
        SampleBenchmarkCaptureContract.Unavailable;
    public SampleBenchmarkTimingStats GpuIndependentPassSumMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("GPU independent pass sum");
    public SampleBenchmarkTimingStats GpuUnexplainedMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("GPU unexplained");
    /// <summary>
    /// Per-frame sum of the independent Simple-DDGI cached-transport and blend
    /// timestamp scopes. This is calculated before percentile aggregation so
    /// its P95 is a real combined-frame percentile, not a sum of unrelated P95s.
    /// </summary>
    public SampleBenchmarkTimingStats SimpleDdgiTransportBlendMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("Simple DDGI transport + blend");
    public SampleDdgiSchedulerRefreshEvidence SimpleDdgiSchedulerRefresh { get; init; } =
        SampleDdgiSchedulerRefreshEvidence.Empty;
    public int AdditionalSettlingFrameCount { get; init; }
    public bool SettlingWaitTimedOut { get; init; }
    public SampleBenchmarkHdrDifference HdrDifference { get; init; } =
        SampleBenchmarkHdrDifference.Unavailable("HDR comparison was not requested.");
    public SampleShaderProfileEvidence ShaderProfile { get; init; } =
        SampleShaderProfileEvidence.Unavailable(
            "Nsight shader-profile evidence was not supplied.");
    public SampleTailDdgiRuntimeEvidence TailDdgiEvidence { get; init; } =
        SampleTailDdgiRuntimeEvidence.Unavailable(
            "Tail-certified DDGI was not observed in this capture.");
    public SampleBenchmarkMaterialTimingEvidence MaterialTimingEvidence { get; init; } =
        SampleBenchmarkMaterialTimingEvidence.Unavailable;
    public SampleBenchmarkCpuSpikeEvidence CpuSpikeEvidence { get; init; } =
        SampleBenchmarkCpuSpikeEvidence.Empty;
    public SampleRealtimePerformanceTargetReport RealtimePerformanceTarget
        { get; init; } = SampleRealtimePerformanceTargetReport.NotRequested;
    [JsonRequired]
    public SampleBenchmarkDdgiTransientRawEvidence
        DdgiTransientRawEvidence { get; init; } =
            SampleBenchmarkDdgiTransientRawEvidence.NotApplicable;
    [JsonRequired]
    public SampleBenchmarkDdgiTransientEvidence DdgiTransientEvidence { get; init; } =
        SampleBenchmarkDdgiTransientEvidence.NotApplicable;
    public SampleBenchmarkActivationEvidence ActivationEvidence { get; init; } =
        SampleBenchmarkActivationEvidence.Unavailable;
    public SampleBenchmarkSponzaSceneAnimationEvidence
        SponzaSceneAnimationEvidence { get; init; } =
            SampleBenchmarkSponzaSceneAnimationEvidence.Unavailable;
    [JsonRequired]
    public SampleBenchmarkReflectionProbeRawEvidence
        ReflectionProbeCaptureRawEvidence { get; init; } =
            SampleBenchmarkReflectionProbeRawEvidence.NotApplicable;
    [JsonRequired]
    public SampleReflectionProbeCaptureEvidence ReflectionProbeCaptureEvidence { get; init; } =
        SampleReflectionProbeCaptureEvidence.NotApplicable;
}

/// <summary>
/// One completed timing/counter record joined backward to the measured frame
/// serial that submitted it. Completion-observed indices make the frame-slot
/// delay explicit instead of phase-shifting the values onto a later frame.
/// </summary>
public sealed record SampleBenchmarkDdgiTransientFrame(
    [property: JsonRequired] int MeasurementSampleIndex,
    [property: JsonRequired] int RouteFrameIndex,
    [property: JsonRequired] int CompletionObservedMeasurementSampleIndex,
    [property: JsonRequired] int CompletionObservedRouteFrameIndex,
    [property: JsonRequired] SimpleDdgiCompletedFrameEvidence Completed);

/// <summary>
/// Deterministic relight interval from an observed source-generation edge
/// through an authenticated response closure. A stationary interval may close
/// with a complete tail certificate; a continuously moving route closes when
/// the new source generation first becomes live in published propagation.
/// </summary>
public sealed record SampleBenchmarkDdgiTransientWindow(
    [property: JsonRequired] int WindowIndex,
    [property: JsonRequired] int AuthoredEventRouteFrameIndex,
    [property: JsonRequired] int ObservedGenerationEdgeRouteFrameIndex,
    [property: JsonRequired] int GenerationResponseLatencyFrames,
    [property: JsonRequired] uint PreviousSourceLightingGeneration,
    [property: JsonRequired] uint SourceLightingGeneration,
    [property: JsonRequired] string ClosureKind,
    [property: JsonRequired] int ResponseClosureRouteFrameIndex,
    [property: JsonRequired] int ResponseLatencyFrames,
    [property: JsonRequired] ulong FirstSubmittedFrameSerial,
    [property: JsonRequired] ulong LastSubmittedFrameSerial,
    [property: JsonRequired] ulong FirstSubmittedSchedulerFrameSerial,
    [property: JsonRequired] ulong LastSubmittedSchedulerFrameSerial,
    [property: JsonRequired] IReadOnlyList<SampleBenchmarkDdgiTransientFrame> Frames);

public static class SampleBenchmarkDdgiTransientClosureKind
{
    public const string CertifiedTail = "certified-tail";
    public const string DynamicLivePropagation = "dynamic-live-propagation";

    public static bool IsCanonical(string? value) =>
        string.Equals(value, CertifiedTail, StringComparison.Ordinal) ||
        string.Equals(value, DynamicLivePropagation, StringComparison.Ordinal);
}

public sealed record SampleBenchmarkDdgiTransientEvidence(
    [property: JsonRequired] bool Applicable,
    [property: JsonRequired] bool Available,
    [property: JsonRequired] IReadOnlyList<string> Failures,
    [property: JsonRequired] IReadOnlyList<SampleBenchmarkDdgiTransientWindow> Windows)
{
    public const string CurrentSchema =
        MaterialGiReleaseEvidenceContract
            .BenchmarkDdgiTransientEvidenceSchema;

    [JsonRequired]
    public string Schema { get; init; } = CurrentSchema;

    public static SampleBenchmarkDdgiTransientEvidence NotApplicable { get; } =
        new(
            Applicable: false,
            Available: false,
            Array.Empty<string>(),
            Array.Empty<SampleBenchmarkDdgiTransientWindow>());

    public static SampleBenchmarkDdgiTransientEvidence Unavailable(
        params string[] failures) =>
        new(
            Applicable: true,
            Available: false,
            Array.AsReadOnly(failures ?? Array.Empty<string>()),
            Array.Empty<SampleBenchmarkDdgiTransientWindow>());

    public static SampleBenchmarkDdgiTransientEvidence Failed(
        bool applicable,
        IEnumerable<string> failures)
    {
        string[] distinct = failures
            .Where(static failure => !string.IsNullOrWhiteSpace(failure))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SampleBenchmarkDdgiTransientEvidence(
            applicable,
            Available: false,
            Array.AsReadOnly(distinct),
            Array.Empty<SampleBenchmarkDdgiTransientWindow>());
    }
}

public sealed record SampleBenchmarkTimingStats(
    string Name,
    int Count,
    double AverageMilliseconds,
    double MinMilliseconds,
    double MaxMilliseconds,
    double P95Milliseconds)
{
    public double MedianMilliseconds { get; init; }
    public double P50Milliseconds { get; init; }
    public double P99Milliseconds { get; init; }

    public static SampleBenchmarkTimingStats Empty(string name) =>
        new(name, 0, 0, 0, 0, 0)
        {
            MedianMilliseconds = 0,
            P50Milliseconds = 0,
            P99Milliseconds = 0
        };
}

public sealed record SampleBenchmarkMaterialTimingEvidence(
    SampleBenchmarkTimingStats Compile,
    SampleBenchmarkTimingStats Upload,
    SampleBenchmarkTimingStats Pipeline,
    bool CompileSequenceExact,
    bool UploadSequenceExact)
{
    public static SampleBenchmarkMaterialTimingEvidence Unavailable { get; } =
        new(
            SampleBenchmarkTimingStats.Empty("Material GI compile P95"),
            SampleBenchmarkTimingStats.Empty("Material GI upload P95"),
            SampleBenchmarkTimingStats.Empty("Material GI compile/upload P95"),
            CompileSequenceExact: false,
            UploadSequenceExact: false);
}

public sealed record SampleBenchmarkCaptureContract(
    bool Comparable,
    bool ProductionTiming,
    string PairId,
    string Variant,
    string IdentityHash,
    IReadOnlyList<string> Mismatches)
{
    /// <summary>Exact rendered-state identity for this individual run.</summary>
    public string FullIdentityHash { get; init; } = "unavailable";
    /// <summary>Named stationary or deterministic moving camera program.</summary>
    public string Trajectory { get; init; } =
        SampleBenchmarkTrajectory.StationaryName;
    /// <summary>Hash of the authored trajectory contract and lighting script.</summary>
    public string TrajectoryFingerprint { get; init; } = "unavailable";
    /// <summary>Number of camera states in one complete trajectory cycle.</summary>
    public int TrajectoryFrameCount { get; init; } = 1;
    /// <summary>
    /// Camera-only identity for the authored route, excluding renderer state
    /// and absolute camera-cut serials so controlled A/B variants can pair.
    /// </summary>
    public string TrajectoryRouteHash { get; init; } = "unavailable";
    /// <summary>
    /// Hash of every measured camera and scene-state identity in order. This
    /// lets paired captures compare moving workloads without pretending that
    /// every frame has the same camera or dynamic scene state.
    /// </summary>
    public string TrajectorySequenceHash { get; init; } = "unavailable";
    /// <summary>
    /// Quantization allowance used to reconcile independently rounded pass
    /// durations with the rounded frame duration.
    /// </summary>
    public long PassTimestampReconciliationToleranceMicroseconds { get; init; }
    public string Activation { get; init; } = SampleBenchmarkActivation.None;
    public string ActivationFingerprint { get; init; } =
        SampleBenchmarkActivation.CreateFingerprint(
            SampleBenchmarkActivation.None);
    public SampleSponzaFixtureMode SponzaFixtureMode { get; init; } =
        SampleSponzaFixtureMode.Architecture;
    public string SponzaSceneAnimationFingerprint { get; init; } =
        "unavailable";
    public SampleBenchmarkSponzaSceneAnimationMode
        SponzaSceneAnimationMode { get; init; } =
            SampleBenchmarkSponzaSceneAnimationMode.Unavailable;
    public string SponzaSceneAnimationConfigurationFingerprint { get; init; } =
        "unavailable";
    public string SponzaSceneAnimationSequenceHash { get; init; } =
        "unavailable";
    public string SponzaSceneAnimationSidecarSha256 { get; init; } =
        "unavailable";
    /// <summary>
    /// Shared build/scene/camera identity for explicitly authored controlled
    /// isolations. It replaces the role-specific activation fingerprint with
    /// the isolation-family fingerprint while retaining exact target state.
    /// </summary>
    public string ControlledIsolationIdentityHash { get; init; } =
        "unavailable";
    /// <summary>
    /// Full render-settings identity shared by the directional isolation after
    /// normalizing only the authored forced static-cascade refresh switch.
    /// </summary>
    public string ControlledIsolationSettingsFingerprint { get; init; } =
        "unavailable";
    /// <summary>
    /// Recomputable role-neutral identity over every measured directional
    /// route/state/cache-provenance row.
    /// </summary>
    public string ControlledIsolationSequenceHash { get; init; } =
        "unavailable";
    public IReadOnlyList<SampleBenchmarkControlledIsolationFrameEvidence>
        ControlledIsolationFrames { get; init; } =
            Array.Empty<SampleBenchmarkControlledIsolationFrameEvidence>();

    public static SampleBenchmarkCaptureContract Unavailable { get; } = new(
        false,
        false,
        string.Empty,
        "baseline",
        "unavailable",
        Array.Empty<string>())
    {
        FullIdentityHash = "unavailable"
    };
}

public sealed record SampleBenchmarkControlledIsolationCascadeEvidence(
    int CascadeIndex,
    ulong CacheSignature,
    int DynamicWorkAppended,
    int FoliageWorkAppended);

public sealed record SampleBenchmarkControlledIsolationFrameEvidence(
    int MeasurementFrameIndex,
    PerformanceCaptureCameraMetadata Camera,
    string SceneAssetHash,
    string SceneStateHash,
    ulong SceneContentRevision,
    string ResolvedGiSettingsHash,
    RenderFeatureIsolationMode FeatureIsolation,
    GlobalIlluminationDebugView DebugView,
    string ControlledSettingsFingerprint,
    int DirectionalStaticCacheActiveMask,
    int PlayingAnimatorCount,
    int SkinningDispatchCount,
    int SkinnedObjectCount,
    int DirectionalDynamicShadowMeshletCount,
    int DirectionalShadowSkinnedObjectCount,
    IReadOnlyList<SampleBenchmarkControlledIsolationCascadeEvidence>
        Cascades);

public sealed record SampleBenchmarkFinding(
    string Category,
    string Subject,
    string Detail);

public sealed record SampleBenchmarkIntegerStats(
    string Name,
    int Count,
    double Average,
    int Min,
    int Max,
    int P95,
    double Median)
{
    public static SampleBenchmarkIntegerStats Empty(string name) =>
        new(name, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// CPU timing distributions split by whether the renderer rebuilt its scene
/// payload. Camera-driven draw-list rebuilds are a subset of rebuilt frames
/// and are counted separately so moving captures remain attributable.
/// </summary>
public sealed record SampleBenchmarkCpuCohortEvidence(
    string Name,
    int FrameCount,
    int ScenePayloadRebuiltFrameCount,
    int CameraDrivenCpuDrawListRebuiltFrameCount,
    SampleBenchmarkTimingStats TotalDrawSceneMilliseconds,
    SampleBenchmarkTimingStats SceneBuildMilliseconds,
    SampleBenchmarkTimingStats PayloadSignatureMilliseconds,
    SampleBenchmarkTimingStats ObjectCullMilliseconds,
    SampleBenchmarkTimingStats MeshletCullMilliseconds,
    SampleBenchmarkTimingStats StaticBatchBuildMilliseconds,
    SampleBenchmarkTimingStats UploadMilliseconds,
    SampleBenchmarkTimingStats MaterialUploadMilliseconds,
    SampleBenchmarkTimingStats AccelerationStructureBuildMilliseconds,
    SampleBenchmarkTimingStats PrimaryCommandRecordMilliseconds,
    SampleBenchmarkTimingStats SecondaryCommandRecordMilliseconds,
    SampleBenchmarkTimingStats FrameFenceWaitMilliseconds)
{
    public static SampleBenchmarkCpuCohortEvidence Empty(string name) => new(
        name,
        0,
        0,
        0,
        SampleBenchmarkTimingStats.Empty($"{name} total draw scene"),
        SampleBenchmarkTimingStats.Empty($"{name} scene build"),
        SampleBenchmarkTimingStats.Empty($"{name} payload signature"),
        SampleBenchmarkTimingStats.Empty($"{name} object cull"),
        SampleBenchmarkTimingStats.Empty($"{name} meshlet cull"),
        SampleBenchmarkTimingStats.Empty($"{name} static batch build"),
        SampleBenchmarkTimingStats.Empty($"{name} upload"),
        SampleBenchmarkTimingStats.Empty($"{name} material upload"),
        SampleBenchmarkTimingStats.Empty($"{name} acceleration structure build"),
        SampleBenchmarkTimingStats.Empty($"{name} primary command record"),
        SampleBenchmarkTimingStats.Empty($"{name} secondary command record"),
        SampleBenchmarkTimingStats.Empty($"{name} frame-fence wait"));
}

/// <summary>
/// Correlated diagnostics for one of the slowest CPU renderer frames. Queue
/// submit and present timings are deliberately omitted because those values
/// describe phase-lagged work and must not be reported as causes of this
/// measurement sample's scene-build spike. RuntimeWorstStallReason is also
/// omitted because it describes the session maximum rather than this frame.
/// </summary>
public sealed record SampleBenchmarkCpuSlowFrame
{
    public int MeasurementSampleIndex { get; init; }

    public long CpuTotalDrawSceneMicroseconds { get; init; }
    public long CpuSceneBuildMicroseconds { get; init; }
    public long CpuPayloadSignatureMicroseconds { get; init; }
    public long CpuObjectCullMicroseconds { get; init; }
    public long CpuMeshletCullMicroseconds { get; init; }
    public long CpuStaticBatchBuildMicroseconds { get; init; }
    public long CpuUploadMicroseconds { get; init; }
    public long CpuMaterialUploadMicroseconds { get; init; }
    public long CpuAccelerationStructureBuildMicroseconds { get; init; }
    public long CpuAccelerationStructureBlasBuildMicroseconds { get; init; }
    public long CpuAccelerationStructureBlasCompactionMicroseconds { get; init; }
    public long CpuAccelerationStructureTlasBuildMicroseconds { get; init; }
    public long CpuAccelerationStructureInstanceUploadMicroseconds { get; init; }
    public long CpuPrimaryCommandRecordMicroseconds { get; init; }
    public long CpuSecondaryCommandRecordMicroseconds { get; init; }
    public long CpuWaitForFrameFenceMicroseconds { get; init; }
    public long RuntimeStallMicrosecondsThisFrame { get; init; }
    public long CpuReflectionProbeCaptureRecordMicroseconds { get; init; }
    public long CpuReflectionProbePrefilterRecordMicroseconds { get; init; }

    public int ScenePayloadRebuilt { get; init; }
    public int CameraDrivenCpuDrawListRebuilt { get; init; }
    public int HiZPolicyCameraCut { get; init; }
    public int SceneUploadCount { get; init; }
    public int SceneUploadSkipped { get; init; }
    public int VisibleObjectCount { get; init; }
    public int VisibleMeshletCount { get; init; }
    public int StaticInstanceBatchCount { get; init; }
    public int StaticInstanceCount { get; init; }
    public int VisibleStaticInstanceCount { get; init; }
    public int CulledStaticInstanceCount { get; init; }
    public int StaticBatchMeshletDrawCommandCount { get; init; }
    public int MaterialCount { get; init; }
    public uint MaterialRevision { get; init; }
    public int TransparentSortCandidateCount { get; init; }
    public long TransparentSortMicroseconds { get; init; }
    public int ReflectionProbeCapturesQueued { get; init; }
    public int ReflectionProbeCapturesCompleted { get; init; }
    public ulong ReflectionProbeCapturesCompletedTotal { get; init; }
    public int ObjectCandidatesCpu { get; init; }
    public int ObjectFrustumCulledCpu { get; init; }
    public int MeshletCandidatesCpu { get; init; }
    public int MeshletFrustumCulledCpu { get; init; }
    public int MeshletLodSkippedCpu { get; init; }
    public int MeshletLod0SubmittedCpu { get; init; }
    public int MeshletLod1SubmittedCpu { get; init; }
    public int MeshletLod2SubmittedCpu { get; init; }
    public int MeshletCountSubmittedCpu { get; init; }
    public SceneSubmissionMode SceneSubmissionActiveMode { get; init; }
    public int SceneSubmissionCpuCandidateCount { get; init; }
    public int SceneSubmissionGpuOpaqueCandidateCount { get; init; }
    public int SceneSubmissionGpuOpaqueFrustumRejectedCount { get; init; }
    public int SceneSubmissionGpuLod0EmittedCount { get; init; }
    public int SceneSubmissionGpuLod1EmittedCount { get; init; }
    public int SceneSubmissionGpuLod2EmittedCount { get; init; }
    public int SceneSubmissionGpuMissingLodFallbackCount { get; init; }
    public int SceneSubmissionGpuOpaqueLodDecimatedCount { get; init; }
    public int AccelerationStructureBlasBuildCount { get; init; }
    public int AccelerationStructureBlasCompactionQueryCount { get; init; }
    public int AccelerationStructureBlasCompactionCount { get; init; }
    public int AccelerationStructureBlasCompactionPendingCount { get; init; }
    public int AccelerationStructureBlasCompactionQueryOverflowCount { get; init; }
    public int AccelerationStructureBlasCompactionQueryReadbackFailureCount { get; init; }
    public int AccelerationStructureTlasBuildCount { get; init; }
    public int AccelerationStructureTlasUpdateCount { get; init; }
    public int AccelerationStructureTlasSkipCount { get; init; }

    public ulong UploadedBytes { get; init; }
    public ulong StableSceneInputUploadBytes { get; init; }
    public ulong CpuCandidateListUploadBytes { get; init; }
    public ulong ObjectUploadBytes { get; init; }
    public ulong InstanceUploadBytes { get; init; }
    public ulong MeshletDrawUploadBytes { get; init; }
    public ulong TransparentMeshletDrawUploadBytes { get; init; }
    public ulong SolidDepthMeshletDrawUploadBytes { get; init; }
    public ulong MaskedDepthMeshletDrawUploadBytes { get; init; }
    public ulong MaterialUploadBytes { get; init; }
    public ulong MaterialExtensionUploadBytes { get; init; }
    public ulong LightUploadBytes { get; init; }
    public ulong AccelerationStructureInstanceUploadBytes { get; init; }
    public ulong AccelerationStructureRayQueryMetadataUploadBytes { get; init; }

    public ulong CaptureSceneContentRevision { get; init; }
    public ulong CaptureFrameSerial { get; init; }
    public ulong CaptureFramesSinceSceneLoad { get; init; }
    public string CaptureSceneAssetHash { get; init; } = string.Empty;
    public string CaptureSceneStateHash { get; init; } = string.Empty;
}

public sealed record SampleBenchmarkCpuSpikeEvidence(
    SampleBenchmarkCpuCohortEvidence Rebuilt,
    SampleBenchmarkCpuCohortEvidence Stable,
    IReadOnlyList<SampleBenchmarkCpuSlowFrame> SlowestFrames)
{
    public const int SlowFrameLimit = 8;

    public static SampleBenchmarkCpuSpikeEvidence Empty { get; } = new(
        SampleBenchmarkCpuCohortEvidence.Empty("Rebuilt"),
        SampleBenchmarkCpuCohortEvidence.Empty("Stable"),
        Array.Empty<SampleBenchmarkCpuSlowFrame>());
}

/// <summary>
/// One completed reflection workload ranked by its aligned capture, prefilter,
/// and publish GPU timestamps. CompletedLifecycle owns the unit counts and
/// frame-slot identity used by those timings. SubmittedBudget is recovered
/// only by joining that identity to an earlier measured current frame.
/// </summary>
public sealed record SampleReflectionProbeSlowFrame(
    [property: JsonRequired] int MeasurementSampleIndex,
    [property: JsonRequired] long CompletedGpuMicroseconds,
    [property: JsonRequired] long GpuCaptureMicroseconds,
    [property: JsonRequired] long GpuPrefilterMicroseconds,
    [property: JsonRequired] long GpuPublishMicroseconds,
    [property: JsonRequired]
        ReflectionProbeLifecycleFrameSnapshot CompletedLifecycle,
    [property: JsonRequired] bool SubmittedBudgetAvailable,
    [property: JsonRequired] int SubmittedBudgetMeasurementSampleIndex,
    [property: JsonRequired] int SubmittedBudgetFrameSlot,
    [property: JsonRequired] ulong SubmittedBudgetFrameSerial,
    [property: JsonRequired]
        ReflectionProbeGpuBudgetSnapshot SubmittedBudget);

public sealed record SampleReflectionProbeCaptureEvidence(
    [property: JsonRequired]
        IReadOnlyList<SampleReflectionProbeSlowFrame> SlowestFrames)
{
    public const int SlowFrameLimit = 8;
    public const string CurrentSchema =
        "njulf-benchmark-reflection-probe-capture-evidence/v1";

    [JsonRequired]
    public string Schema { get; init; } = CurrentSchema;
    [JsonRequired]
    public bool Applicable { get; init; }

    public static SampleReflectionProbeCaptureEvidence NotApplicable { get; } =
        new(Array.Empty<SampleReflectionProbeSlowFrame>());

    public static SampleReflectionProbeCaptureEvidence Empty { get; } =
        NotApplicable;

    public static bool IsCanonicalNotApplicable(
        SampleReflectionProbeCaptureEvidence? evidence) =>
        evidence != null &&
        string.Equals(evidence.Schema, CurrentSchema, StringComparison.Ordinal) &&
        !evidence.Applicable &&
        evidence.SlowestFrames is { Count: 0 };
}

public sealed record SampleDdgiSchedulerSlowFrame(
    int MeasurementSampleIndex,
    long SchedulerRefreshMicroseconds,
    int SchedulerEntryRefreshCount,
    int SchedulerWakeEntryRefreshCount,
    int SchedulerWakeRefreshBudget,
    int SchedulerWakeBudgetSaturated,
    int SchedulerFullRebuildCount,
    int VisibilityEntryRefreshCount,
    int ReadbackProbeCount,
    int ProbesUpdated,
    int TransportSourceReadyProbeCount,
    int TransportConvergedProbeCount,
    int TransportGlobalConvergencePending)
{
    public int RoutineSourceRepairProbeCount { get; init; }
    public int RoutineMaintenancePendingProbeCount { get; init; }
}

public sealed record SampleDdgiSchedulerRefreshEvidence(
    SampleBenchmarkIntegerStats SchedulerEntryRefreshCount,
    SampleBenchmarkIntegerStats SchedulerWakeEntryRefreshCount,
    SampleBenchmarkIntegerStats VisibilityEntryRefreshCount,
    SampleBenchmarkIntegerStats ReadbackProbeCount,
    int WakeBudgetSaturatedFrameCount,
    int FullRebuildFrameCount,
    IReadOnlyList<SampleDdgiSchedulerSlowFrame> SlowestFrames)
{
    public static SampleDdgiSchedulerRefreshEvidence Empty { get; } = new(
        SampleBenchmarkIntegerStats.Empty("Scheduler entries refreshed"),
        SampleBenchmarkIntegerStats.Empty("Scheduler wake entries refreshed"),
        SampleBenchmarkIntegerStats.Empty("Visibility entries refreshed"),
        SampleBenchmarkIntegerStats.Empty("Probe readback entries"),
        0,
        0,
        Array.Empty<SampleDdgiSchedulerSlowFrame>());
}
