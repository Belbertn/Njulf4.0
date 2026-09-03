using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

public sealed record SampleRealtimePerformanceTargetReport(
    bool Applicable,
    bool Passed,
    uint Width,
    uint Height,
    double CpuP95Milliseconds,
    double GpuP95Milliseconds,
    double CpuP99Milliseconds,
    double GpuP99Milliseconds,
    ulong TrackedGpuMemoryBytes,
    ulong TargetGpuMemoryBytes,
    double MemoryHeadroomFraction,
    IReadOnlyList<string> Failures)
{
    public static SampleRealtimePerformanceTargetReport NotRequested { get; } =
        new(
            Applicable: false,
            Passed: true,
            0,
            0,
            0.0,
            0.0,
            0.0,
            0.0,
            0,
            0,
            0.0,
            Array.Empty<string>());
}

/// <summary>
/// Absolute release target shared by Sponza and Bistro. Relative A/B gates
/// remain useful for optimization, but cannot prove that either candidate is
/// actually fast enough for the product target.
/// </summary>
internal static class SampleRealtimePerformanceTarget
{
    public const uint Width = 1920;
    public const uint Height = 1080;
    public const double FramesPerSecond = 60.0;
    public const double CpuP95BudgetMilliseconds = 6.0;
    public const double GpuP95BudgetMilliseconds = 10.0;
    public const double FrameP99BudgetMilliseconds = 1000.0 / FramesPerSecond;
    public const ulong TargetGpuMemoryBytes = 6UL * 1024UL * 1024UL * 1024UL;
    public const double MinimumMemoryHeadroomFraction = 0.10;
    public const ulong MaximumTrackedGpuMemoryBytes =
        TargetGpuMemoryBytes * 9UL / 10UL;

    public static SampleRealtimePerformanceTargetReport Evaluate(
        SampleBenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.Options.RequireRealtime1080p60Target)
            return SampleRealtimePerformanceTargetReport.NotRequested;

        RendererDiagnostics diagnostics = report.LastDiagnostics;
        var failures = new List<string>();
        if (diagnostics.CaptureRenderWidth != Width ||
            diagnostics.CaptureRenderHeight != Height)
        {
            failures.Add(
                $"Realtime target requires {Width}x{Height}; captured " +
                $"{diagnostics.CaptureRenderWidth}x{diagnostics.CaptureRenderHeight}.");
        }

        RequireFiniteAtMost(
            report.CpuFrameMilliseconds.P95Milliseconds,
            CpuP95BudgetMilliseconds,
            "CPU frame P95",
            failures);
        RequireFiniteAtMost(
            report.GpuFrameMilliseconds.P95Milliseconds,
            GpuP95BudgetMilliseconds,
            "GPU frame P95",
            failures);
        RequireFiniteAtMost(
            report.CpuFrameMilliseconds.P99Milliseconds,
            FrameP99BudgetMilliseconds,
            "CPU frame P99",
            failures);
        RequireFiniteAtMost(
            report.GpuFrameMilliseconds.P99Milliseconds,
            FrameP99BudgetMilliseconds,
            "GPU frame P99",
            failures);

        ulong trackedBytes = diagnostics.TrackedGpuMemoryBytes;
        if (trackedBytes > MaximumTrackedGpuMemoryBytes)
        {
            failures.Add(
                $"Tracked GPU memory {trackedBytes} bytes exceeds the " +
                $"{MaximumTrackedGpuMemoryBytes}-byte limit required for " +
                $"{MinimumMemoryHeadroomFraction:P0} headroom on a six-GiB target.");
        }

        if (diagnostics.GpuMemoryBudgetQueryAvailable != 0 &&
            diagnostics.ActualGpuMemoryBudgetBytes > 0)
        {
            decimal maximumActualUsage =
                (decimal)diagnostics.ActualGpuMemoryBudgetBytes * 0.90m;
            if (diagnostics.ActualGpuMemoryUsageBytes > maximumActualUsage)
            {
                failures.Add(
                    $"Actual device-local memory usage " +
                    $"{diagnostics.ActualGpuMemoryUsageBytes} bytes leaves less " +
                    $"than {MinimumMemoryHeadroomFraction:P0} driver-reported headroom.");
            }
        }

        double headroom = trackedBytes >= TargetGpuMemoryBytes
            ? 0.0
            : (TargetGpuMemoryBytes - trackedBytes) /
              (double)TargetGpuMemoryBytes;
        return new SampleRealtimePerformanceTargetReport(
            Applicable: true,
            Passed: failures.Count == 0,
            diagnostics.CaptureRenderWidth,
            diagnostics.CaptureRenderHeight,
            report.CpuFrameMilliseconds.P95Milliseconds,
            report.GpuFrameMilliseconds.P95Milliseconds,
            report.CpuFrameMilliseconds.P99Milliseconds,
            report.GpuFrameMilliseconds.P99Milliseconds,
            trackedBytes,
            TargetGpuMemoryBytes,
            headroom,
            failures.AsReadOnly());
    }

    private static void RequireFiniteAtMost(
        double value,
        double maximum,
        string label,
        ICollection<string> failures)
    {
        if (!double.IsFinite(value) || value > maximum)
        {
            failures.Add(
                $"{label} {value:R} ms exceeds the {maximum:R} ms " +
                "1080p60 target.");
        }
    }
}
