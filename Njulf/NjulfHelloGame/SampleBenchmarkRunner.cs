using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public sealed class SampleBenchmarkRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly SampleBenchmarkOptions _options;
    private readonly SamplePerformanceScenario _scenario;
    private readonly Action _exit;
    private readonly SampleBenchmarkAnalyzer _analyzer = new();
    private int _samplesCaptured;
    private int _firstMeasurementFrame = -1;
    private int _lastMeasurementFrame = -1;
    private bool _completed;

    public SampleBenchmarkRunner(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        Action exit)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scenario = scenario;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public SampleBenchmarkReport? Report { get; private set; }
    public string? ReportPath { get; private set; }

    public void OnFrameRendered(int frameIndex, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (!_options.Enabled || _completed)
            return;
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        if (frameIndex < _options.WarmupFrameCount)
            return;

        if (_samplesCaptured == 0)
            _firstMeasurementFrame = frameIndex;
        _lastMeasurementFrame = frameIndex;
        _analyzer.AddSample(diagnostics, budget);
        _samplesCaptured++;

        if (_samplesCaptured < _options.MeasureFrameCount)
            return;

        Complete();
    }

    private void Complete()
    {
        _completed = true;
        Report = _analyzer.CreateReport(
            _options,
            _scenario,
            _options.WarmupFrameCount,
            _samplesCaptured,
            _firstMeasurementFrame,
            _lastMeasurementFrame);
        ReportPath = WriteReport(Report, _options.ReportPath);
        Console.WriteLine(
            $"Benchmark report exported: {ReportPath} " +
            $"cpuP95={Report.CpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"gpuP95={Report.GpuFrameMilliseconds.P95Milliseconds:F3}ms " +
            $"top='{Report.Findings.FirstOrDefault()?.Subject ?? "none"}'");
        _exit();
    }

    private static string WriteReport(SampleBenchmarkReport report, string? path)
    {
        string targetPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "BenchmarkReports", $"benchmark-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json")
            : Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(report, SerializerOptions);
        File.WriteAllText(targetPath, json);
        return targetPath;
    }
}

public sealed class SampleBenchmarkAnalyzer
{
    private static readonly IReadOnlyList<TimingSelector> GpuTimings =
    [
        new("DepthPrePass", d => d.GpuDepthPrePassMicroseconds),
        new("DirectionalShadowPass", d => d.GpuDirectionalShadowMicroseconds),
        new("SpotShadowPass", d => d.GpuSpotShadowMicroseconds),
        new("PointShadowPass", d => d.GpuPointShadowMicroseconds),
        new("HiZBuildPass", d => d.GpuHiZBuildMicroseconds),
        new("AmbientOcclusionPass", d => d.GpuAmbientOcclusionMicroseconds),
        new("AmbientOcclusionBlurPass", d => d.GpuAmbientOcclusionBlurMicroseconds),
        new("TiledLightCullingPass", d => d.GpuLightCullMicroseconds),
        new("ForwardPlusPass", d => d.GpuForwardOpaqueMicroseconds),
        new("TransparentPasses", d => d.GpuTransparentMicroseconds),
        new("ParticlePasses", d => d.GpuParticleMicroseconds),
        new("TrailBeamPass", d => d.GpuTrailBeamMicroseconds),
        new("FogPass", d => d.GpuFogMicroseconds),
        new("AutoExposurePass", d => d.GpuAutoExposureMicroseconds),
        new("AntiAliasingPass", d => d.GpuAntiAliasingMicroseconds),
        new("BloomExtractPass", d => d.GpuBloomExtractMicroseconds),
        new("BloomDownsamplePass", d => d.GpuBloomDownsampleMicroseconds),
        new("BloomUpsamplePass", d => d.GpuBloomUpsampleMicroseconds),
        new("ToneMapCompositePass", d => d.GpuCompositeMicroseconds),
        new("SkinningPass", d => d.GpuSkinningMicroseconds),
        new("ReflectionProbeCapture", d => d.GpuReflectionProbeCaptureMicroseconds),
        new("ReflectionProbePrefilter", d => d.GpuReflectionProbePrefilterMicroseconds),
        new("FoliageCullPass", d => d.GpuFoliageCullMicroseconds),
        new("FoliageDepth", d => d.GpuFoliageDepthMicroseconds),
        new("FoliageForward", d => d.GpuFoliageForwardMicroseconds),
        new("FoliageShadow", d => d.GpuFoliageShadowMicroseconds),
        new("DebugDrawPass", d => d.GpuDebugDrawMicroseconds),
        new("DebugOverlay", d => d.GpuDebugOverlayMicroseconds)
    ];

    private static readonly IReadOnlyList<TimingSelector> CpuTimings =
    [
        new("DrawSceneTotal", d => d.CpuTotalDrawSceneMicroseconds),
        new("SceneBuild", d => d.CpuSceneBuildMicroseconds),
        new("ObjectCull", d => d.CpuObjectCullMicroseconds),
        new("MeshletCull", d => d.CpuMeshletCullMicroseconds),
        new("Upload", d => d.CpuUploadMicroseconds),
        new("MaterialUpload", d => d.CpuMaterialUploadMicroseconds),
        new("DepthPrePassRecord", d => d.CpuDepthPrePassRecordMicroseconds),
        new("HiZBuildRecord", d => d.CpuHiZBuildRecordMicroseconds),
        new("LightCullRecord", d => d.CpuLightCullRecordMicroseconds),
        new("ForwardOpaqueRecord", d => d.CpuForwardOpaqueRecordMicroseconds),
        new("TransparentRecord", d => d.CpuTransparentRecordMicroseconds),
        new("DirectionalShadowRecord", d => d.CpuDirectionalShadowRecordMicroseconds),
        new("SpotShadowRecord", d => d.CpuSpotShadowRecordMicroseconds),
        new("PointShadowRecord", d => d.CpuPointShadowRecordMicroseconds),
        new("AmbientOcclusionRecord", d => d.CpuAmbientOcclusionRecordMicroseconds),
        new("AmbientOcclusionBlurRecord", d => d.CpuAmbientOcclusionBlurRecordMicroseconds),
        new("BloomExtractRecord", d => d.CpuBloomExtractRecordMicroseconds),
        new("BloomDownsampleRecord", d => d.CpuBloomDownsampleRecordMicroseconds),
        new("BloomUpsampleRecord", d => d.CpuBloomUpsampleRecordMicroseconds),
        new("FogRecord", d => d.CpuFogRecordMicroseconds),
        new("CompositeRecord", d => d.CpuCompositeRecordMicroseconds),
        new("AutoExposureRecord", d => d.CpuAutoExposureRecordMicroseconds),
        new("FxaaRecord", d => d.CpuFxaaRecordMicroseconds),
        new("SmaaEdgeRecord", d => d.CpuSmaaEdgeRecordMicroseconds),
        new("SmaaBlendRecord", d => d.CpuSmaaBlendRecordMicroseconds),
        new("SmaaNeighborhoodRecord", d => d.CpuSmaaNeighborhoodRecordMicroseconds),
        new("ReflectionProbeCaptureRecord", d => d.CpuReflectionProbeCaptureRecordMicroseconds),
        new("ReflectionProbePrefilterRecord", d => d.CpuReflectionProbePrefilterRecordMicroseconds),
        new("SkinningRecord", d => d.CpuSkinningRecordMicroseconds),
        new("ParticleRecord", d => d.CpuParticleRecordMicroseconds),
        new("ParticleSimulation", d => d.CpuParticleSimulationMicroseconds),
        new("ParticleBuild", d => d.CpuParticleBuildMicroseconds),
        new("FoliageBuild", d => d.CpuFoliageBuildMicroseconds),
        new("FoliageUpload", d => d.CpuFoliageUploadMicroseconds),
        new("PrimaryCommandRecord", d => d.CpuPrimaryCommandRecordMicroseconds),
        new("SecondaryCommandRecord", d => d.CpuSecondaryCommandRecordMicroseconds),
        new("AcquireImage", d => d.CpuAcquireImageMicroseconds),
        new("QueueSubmit", d => d.CpuQueueSubmitMicroseconds),
        new("Present", d => d.CpuPresentMicroseconds),
        new("WaitForFrameFence", d => d.CpuWaitForFrameFenceMicroseconds),
        new("RuntimeStall", d => d.RuntimeStallMicrosecondsThisFrame)
    ];

    private readonly List<RendererDiagnostics> _samples = new();
    private RenderBudgetSnapshot _lastBudget = RenderBudgetSnapshot.Empty;

    public void AddSample(RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        if (diagnostics == null)
            throw new ArgumentNullException(nameof(diagnostics));
        if (budget == null)
            throw new ArgumentNullException(nameof(budget));

        _samples.Add(diagnostics);
        _lastBudget = budget;
    }

    public SampleBenchmarkReport CreateReport(
        SampleBenchmarkOptions options,
        SamplePerformanceScenario scenario,
        int warmupFrameCount,
        int measurementFrameCount,
        int firstMeasurementFrameIndex,
        int lastMeasurementFrameIndex)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        RendererDiagnostics last = _samples.Count == 0 ? RendererDiagnostics.Empty : _samples[^1];
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses = BuildTimingStats(GpuTimings, requireGpuTiming: true);
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages = BuildTimingStats(CpuTimings, requireGpuTiming: false);
        SampleBenchmarkTimingStats cpuFrame = BuildStats("CPU frame", _samples.Select(d => MicrosecondsToMilliseconds(d.CpuTotalDrawSceneMicroseconds)));
        SampleBenchmarkTimingStats gpuFrame = BuildStats(
            "GPU frame",
            _samples.Where(d => d.GpuTimingValid != 0).Select(d => MicrosecondsToMilliseconds(d.GpuFrameMicroseconds)));
        int gpuValidSamples = _samples.Count(d => d.GpuTimingValid != 0);

        return new SampleBenchmarkReport(
            Kind: "njulf-renderer-benchmark",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Options: options,
            Scenario: scenario,
            WarmupFrameCount: warmupFrameCount,
            MeasurementFrameCount: measurementFrameCount,
            FirstMeasurementFrameIndex: firstMeasurementFrameIndex,
            LastMeasurementFrameIndex: lastMeasurementFrameIndex,
            CpuFrameMilliseconds: cpuFrame,
            GpuFrameMilliseconds: gpuFrame,
            GpuTimingSupported: last.GpuTimingSupported,
            GpuTimingValidSampleCount: gpuValidSamples,
            GpuTimingUnavailableReason: last.GpuTimingValid == 0 ? last.GpuTimingUnavailableReason : string.Empty,
            GpuPasses: gpuPasses,
            CpuStages: cpuStages,
            Findings: BuildFindings(cpuFrame, gpuFrame, gpuPasses, cpuStages, _lastBudget),
            BudgetMetrics: _lastBudget.Metrics,
            LastDiagnostics: last);
    }

    private IReadOnlyList<SampleBenchmarkTimingStats> BuildTimingStats(
        IReadOnlyList<TimingSelector> selectors,
        bool requireGpuTiming)
    {
        return selectors
            .Select(selector => BuildStats(
                selector.Name,
                _samples
                    .Where(d => !requireGpuTiming || d.GpuTimingValid != 0)
                    .Select(d => MicrosecondsToMilliseconds(selector.GetMicroseconds(d)))
                    .Where(milliseconds => milliseconds > 0)))
            .Where(stats => stats.Count > 0)
            .OrderByDescending(stats => stats.P95Milliseconds)
            .ThenByDescending(stats => stats.AverageMilliseconds)
            .ToArray();
    }

    private static IReadOnlyList<SampleBenchmarkFinding> BuildFindings(
        SampleBenchmarkTimingStats cpuFrame,
        SampleBenchmarkTimingStats gpuFrame,
        IReadOnlyList<SampleBenchmarkTimingStats> gpuPasses,
        IReadOnlyList<SampleBenchmarkTimingStats> cpuStages,
        RenderBudgetSnapshot budget)
    {
        var findings = new List<SampleBenchmarkFinding>();
        SampleBenchmarkTimingStats? topGpu = gpuPasses.FirstOrDefault();
        SampleBenchmarkTimingStats? topCpu = cpuStages.FirstOrDefault(stage => stage.Name != "DrawSceneTotal");

        if (gpuFrame.Count > 0 && gpuFrame.P95Milliseconds >= cpuFrame.P95Milliseconds && topGpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topGpu.Name,
                $"GPU dominated this sample set; pass p95={topGpu.P95Milliseconds:F3}ms avg={topGpu.AverageMilliseconds:F3}ms."));
        }
        else if (topCpu != null)
        {
            findings.Add(new SampleBenchmarkFinding(
                "likely-bound",
                topCpu.Name,
                $"CPU dominated this sample set; stage p95={topCpu.P95Milliseconds:F3}ms avg={topCpu.AverageMilliseconds:F3}ms."));
        }

        foreach (BudgetMetric metric in budget.Metrics.Where(metric => metric.Status is RenderBudgetStatus.OverBudget or RenderBudgetStatus.Warning))
        {
            findings.Add(new SampleBenchmarkFinding(
                "budget",
                metric.Name,
                $"{metric.Status}: {metric.Value:F3} {metric.Unit}, budget={metric.FailureThreshold:F3} {metric.Unit}."));
        }

        if (gpuFrame.Count == 0)
        {
            findings.Add(new SampleBenchmarkFinding(
                "gpu-timing",
                "GPU frame",
                "No valid GPU timestamp samples were captured; CPU timings and counters are still reported."));
        }

        return findings;
    }

    private static SampleBenchmarkTimingStats BuildStats(string name, IEnumerable<double> values)
    {
        double[] samples = values.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (samples.Length == 0)
            return new SampleBenchmarkTimingStats(name, 0, 0, 0, 0, 0);

        Array.Sort(samples);
        double sum = samples.Sum();
        int p95Index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.95) - 1);
        return new SampleBenchmarkTimingStats(
            name,
            samples.Length,
            sum / samples.Length,
            samples[0],
            samples[^1],
            samples[p95Index]);
    }

    private static double MicrosecondsToMilliseconds(long microseconds)
    {
        return microseconds / 1000.0;
    }

    private sealed record TimingSelector(string Name, Func<RendererDiagnostics, long> GetMicroseconds);
}
