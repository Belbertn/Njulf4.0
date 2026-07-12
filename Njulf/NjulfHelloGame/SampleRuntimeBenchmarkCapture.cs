using System;
using System.IO;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal sealed class SampleRuntimeBenchmarkCapture
{
    private readonly SampleBenchmarkOptions _options;
    private readonly SamplePerformanceScenario _scenario;
    private readonly SampleBenchmarkAnalyzer _analyzer = new();
    private int _framesObserved;
    private int _samplesCaptured;
    private int _firstMeasurementFrame = -1;
    private int _lastMeasurementFrame = -1;

    public SampleRuntimeBenchmarkCapture(SamplePerformanceScenario scenario, int warmupFrameCount, int measureFrameCount)
    {
        if (warmupFrameCount < 0)
            throw new ArgumentOutOfRangeException(nameof(warmupFrameCount));
        if (measureFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(measureFrameCount));

        _scenario = scenario;
        _options = new SampleBenchmarkOptions(
            Enabled: true,
            WarmupFrameCount: warmupFrameCount,
            MeasureFrameCount: measureFrameCount,
            ReportPath: CreateDefaultReportPath(scenario),
            DisableVSync: false);
    }

    public SamplePerformanceScenario Scenario => _scenario;
    public int WarmupFrameCount => _options.WarmupFrameCount;
    public int MeasureFrameCount => _options.MeasureFrameCount;
    public int SamplesCaptured => _samplesCaptured;
    public bool IsComplete { get; private set; }
    public SampleBenchmarkReport? Report { get; private set; }
    public string? ReportPath { get; private set; }

    public bool OnFrameRendered(int frameIndex, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (IsComplete)
            return true;
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        int relativeFrame = _framesObserved++;
        if (relativeFrame < _options.WarmupFrameCount)
            return false;

        if (_samplesCaptured == 0)
            _firstMeasurementFrame = frameIndex;
        _lastMeasurementFrame = frameIndex;
        _analyzer.AddSample(diagnostics, budget);
        _samplesCaptured++;

        if (_samplesCaptured < _options.MeasureFrameCount)
            return false;

        Complete();
        return true;
    }

    private void Complete()
    {
        IsComplete = true;
        Report = _analyzer.CreateReport(
            _options,
            _scenario,
            _options.WarmupFrameCount,
            _samplesCaptured,
            _firstMeasurementFrame,
            _lastMeasurementFrame);
        if (SampleDdgiBenchmarkSuite.RequiredProductionGateScenes.Any(scene => scene.Scenario == _scenario))
        {
            SampleDdgiProductionGateReport gate = SampleDdgiProductionGate.Evaluate(Report);
            Report = Report with { DdgiProductionGate = gate };
        }

        ReportPath = SampleBenchmarkRunner.WriteReport(Report, _options.ReportPath);
    }

    private static string CreateDefaultReportPath(SamplePerformanceScenario scenario)
    {
        string scenarioName = ToKebabCase(scenario.ToString());
        return Path.Combine(
            AppContext.BaseDirectory,
            "BenchmarkReports",
            $"runtime-{scenarioName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    }

    private static string ToKebabCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = new System.Text.StringBuilder(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                chars.Append('-');
            chars.Append(char.ToLowerInvariant(c));
        }

        return chars.ToString();
    }
}
