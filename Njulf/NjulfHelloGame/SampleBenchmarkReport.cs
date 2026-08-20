using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkReport(
    string Kind,
    DateTimeOffset CapturedAtUtc,
    SampleBenchmarkOptions Options,
    SamplePerformanceScenario Scenario,
    int WarmupFrameCount,
    int MeasurementFrameCount,
    int FirstMeasurementFrameIndex,
    int LastMeasurementFrameIndex,
    SampleBenchmarkTimingStats CpuFrameMilliseconds,
    SampleBenchmarkTimingStats GpuFrameMilliseconds,
    int GpuTimingSupported,
    int GpuTimingValidSampleCount,
    string GpuTimingUnavailableReason,
    IReadOnlyList<SampleBenchmarkTimingStats> GpuPasses,
    IReadOnlyList<SampleBenchmarkTimingStats> CpuStages,
    IReadOnlyList<SampleBenchmarkFinding> Findings,
    IReadOnlyList<BudgetMetric> BudgetMetrics,
    RendererDiagnostics LastDiagnostics)
{
    public string Schema { get; init; } =
        MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema;

    [JsonPropertyName("producerIdentity")]
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }

    public SampleDdgiProductionGateReport? DdgiProductionGate { get; init; }
    public IReadOnlyList<SampleGiAccuracyOracleResult> AccuracyOracleResults { get; init; } =
        Array.Empty<SampleGiAccuracyOracleResult>();
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
