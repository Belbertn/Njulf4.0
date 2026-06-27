using System;
using System.Collections.Generic;
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
    public SampleDdgiProductionGateReport? DdgiProductionGate { get; init; }
}

public sealed record SampleBenchmarkTimingStats(
    string Name,
    int Count,
    double AverageMilliseconds,
    double MinMilliseconds,
    double MaxMilliseconds,
    double P95Milliseconds);

public sealed record SampleBenchmarkFinding(
    string Category,
    string Subject,
    string Detail);
