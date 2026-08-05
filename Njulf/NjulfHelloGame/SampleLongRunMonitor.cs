using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Camera;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal sealed record SampleRecoveryCapability(
    bool Supported,
    bool Attempted,
    string Status,
    string Reason);

internal sealed record SampleLongRunWorkloadSummary(
    string Name,
    int DeterministicSeed,
    int PreparedFrameCount,
    int MaterialMutationCount,
    int MaterialMutationIntervalFrames,
    bool MaterialRollbackSucceeded,
    bool CameraRollbackSucceeded,
    string CameraPath);

internal sealed record SampleLongRunDescriptorPressureSummary(
    long PostWarmupSampleCount,
    long TextureExhaustionSampleCount,
    long SamplerExhaustionSampleCount,
    int MaximumTextureUsed,
    int MaximumTextureCapacity,
    int MaximumSamplerUsed,
    int MaximumSamplerCapacity);

internal sealed record SampleTailDdgiLongSoakTimingGate(
    string Name,
    SampleBenchmarkTimingStats Statistics,
    double FailureThresholdMilliseconds,
    bool Passed);

internal sealed record SampleLongRunReport(
    int SchemaVersion,
    string Kind,
    string Status,
    string? Failure,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    double ElapsedSeconds,
    int RequestedFrameCount,
    double RequestedMinutes,
    int WarmupFrames,
    int SampleIntervalFrames,
    int RetainedSampleCapacity,
    int LastPreparedFrameIndex,
    long ExpectedSampleCount,
    long TotalSamples,
    IReadOnlyList<LongRunStabilitySample> RetainedSamples,
    LongRunMemoryTrend ManagedMemoryTrend,
    LongRunMemoryTrend GpuMemoryTrend,
    string GpuMemorySignal,
    int PostWarmupBudgetViolationFrameCount,
    IReadOnlyList<string> BudgetViolations,
    int PostWarmupTelemetryCoverageFailureFrameCount,
    IReadOnlyList<string> TelemetryCoverageFailures,
    SampleLongRunDescriptorPressureSummary DescriptorPressure,
    SampleLongRunWorkloadSummary Workload,
    SampleRecoveryCapability DeviceLossRecovery,
    [property: JsonPropertyName("producerIdentity")]
    MaterialGiProducerIdentity ProducerIdentity)
{
    public string BuildConfiguration { get; init; } = string.Empty;
    public string QualificationProfile { get; init; } = string.Empty;
    public string GiGpuMetricSource { get; init; } = string.Empty;
    public IReadOnlyList<string> NonApplicableBudgetMetrics { get; init; } =
        Array.Empty<string>();
    public int InformationalBudgetObservationFrameCount { get; init; }
    public IReadOnlyList<string> InformationalBudgetObservations { get; init; } =
        Array.Empty<string>();
    public uint CaptureRenderWidth { get; init; }
    public uint CaptureRenderHeight { get; init; }
    public IReadOnlyList<SampleTailDdgiLongSoakTimingGate> TailTimingGates { get; init; } =
        Array.Empty<SampleTailDdgiLongSoakTimingGate>();
}

internal sealed record SampleLongRunCompletion(
    bool Passed,
    string? Failure,
    string ReportPath,
    SampleLongRunReport Report);

/// <summary>
/// Deterministic material-and-camera workload used by the production soak.
/// It keeps one editable material handle for the entire run and recompiles it
/// in place, so descriptor/material growth is a defect rather than test input.
/// </summary>
internal sealed class SampleDeterministicLongRunWorkload
{
    public const int DefaultMutationIntervalFrames = 30;
    public const int DeterministicSeed = 0x4D474932; // "MGI2"

    private readonly FirstPersonCamera _camera;
    private readonly MaterialManager _materialManager;
    private readonly MaterialHandle _material;
    private readonly MaterialDefinition _initialDefinition;
    private readonly CoreVector3 _initialCameraPosition;
    private readonly float _initialYaw;
    private readonly float _initialPitch;
    private readonly int _mutationIntervalFrames;
    private bool _restored;

    public SampleDeterministicLongRunWorkload(
        FirstPersonCamera camera,
        Scene scene,
        MaterialManager materialManager,
        int mutationIntervalFrames = DefaultMutationIntervalFrames)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        ArgumentNullException.ThrowIfNull(scene);
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        if (mutationIntervalFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(mutationIntervalFrames));

        RenderObject target = scene.RenderObjects.FirstOrDefault(
            renderObject => renderObject.Material is MaterialHandle handle && handle.IsValid)
            ?? throw new InvalidOperationException(
                "The long-run material workload requires at least one render object with a live material.");
        MaterialHandle source = (MaterialHandle)target.Material!;
        _material = _materialManager.CreateEditableMaterialCopy(source);
        if (_material != source)
        {
            if (target.HasResourceLifetime)
                target.AdoptTransferredMaterial(_material);
            else
                target.Material = _material;
        }
        _initialDefinition = _materialManager.GetMaterialDefinition(_material);
        _initialCameraPosition = _camera.Position;
        _initialYaw = _camera.Yaw;
        _initialPitch = _camera.Pitch;
        _mutationIntervalFrames = mutationIntervalFrames;
    }

    public int PreparedFrameCount { get; private set; }
    public int MaterialMutationCount { get; private set; }
    public bool MaterialRollbackSucceeded { get; private set; }
    public bool CameraRollbackSucceeded { get; private set; }

    public void PrepareFrame(int frameIndex)
    {
        if (_restored)
            throw new InvalidOperationException("The long-run workload cannot continue after rollback.");
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        // A 2,400-frame irrational-looking loop exercises both incremental
        // clipmap movement and revisits without relying on wall-clock delta.
        const float pathFrames = 2_400f;
        float phase = (frameIndex % (int)pathFrames) * (MathF.Tau / pathFrames);
        _camera.Position = _initialCameraPosition + new CoreVector3(
            MathF.Cos(phase) * 2.75f,
            MathF.Sin(phase * 2f) * 0.35f,
            MathF.Sin(phase) * 2.0f);
        _camera.Yaw = _initialYaw + MathF.Sin(phase) * 0.42f;
        _camera.Pitch = _initialPitch + MathF.Sin(phase * 2f) * 0.08f;
        _camera.Update();
        PreparedFrameCount++;

        if (frameIndex % _mutationIntervalFrames != 0)
            return;

        float mutationPhase =
            ((frameIndex / _mutationIntervalFrames) % 97) * (MathF.Tau / 97f);
        float redScale = 0.82f + 0.16f * (0.5f + 0.5f * MathF.Sin(mutationPhase));
        float greenScale = 0.82f + 0.16f * (0.5f + 0.5f * MathF.Sin(mutationPhase + 2.0943952f));
        float blueScale = 0.82f + 0.16f * (0.5f + 0.5f * MathF.Sin(mutationPhase + 4.1887903f));
        CoreVector4 baseColor = _initialDefinition.BaseColorFactor;
        var mutated = _initialDefinition with
        {
            BaseColorFactor = new CoreVector4(
                Math.Clamp(baseColor.X * redScale, 0f, 1f),
                Math.Clamp(baseColor.Y * greenScale, 0f, 1f),
                Math.Clamp(baseColor.Z * blueScale, 0f, 1f),
                baseColor.W),
            RoughnessFactor = Math.Clamp(
                _initialDefinition.RoughnessFactor *
                (0.78f + 0.18f * (0.5f + 0.5f * MathF.Cos(mutationPhase))),
                0f,
                1f)
        };
        _materialManager.UpdateMaterialDefinition(_material, mutated);
        MaterialMutationCount++;
    }

    public SampleLongRunWorkloadSummary Restore()
    {
        if (!_restored)
        {
            // Attempt both independent rollback domains even if one manager is
            // already faulted. Complete() can then publish a failed report
            // instead of losing all soak evidence to the first exception.
            try
            {
                _materialManager.UpdateMaterialDefinition(_material, _initialDefinition);
                MaterialRollbackSucceeded =
                    _materialManager.GetMaterialDefinition(_material) == _initialDefinition;
            }
            catch
            {
                MaterialRollbackSucceeded = false;
            }

            try
            {
                _camera.Position = _initialCameraPosition;
                _camera.Yaw = _initialYaw;
                _camera.Pitch = _initialPitch;
                _camera.Update();
                CameraRollbackSucceeded =
                    _camera.Position == _initialCameraPosition &&
                    _camera.Yaw == _initialYaw &&
                    _camera.Pitch == _initialPitch;
            }
            catch
            {
                CameraRollbackSucceeded = false;
            }
            _restored = true;
        }

        return new SampleLongRunWorkloadSummary(
            "deterministic-dynamic-material-and-camera-path",
            DeterministicSeed,
            PreparedFrameCount,
            MaterialMutationCount,
            _mutationIntervalFrames,
            MaterialRollbackSucceeded,
            CameraRollbackSucceeded,
            "2400-frame elliptical path with bounded vertical/yaw/pitch modulation");
    }
}

internal sealed class SampleLongRunMonitor
{
    private const int MaximumDistinctBudgetViolations = 128;
    internal const int MaximumRetainedSampleCapacity = 4_096;

    private readonly SampleSmokeOptions _options;
    private readonly SampleDeterministicLongRunWorkload _workload;
    private readonly Func<DescriptorPressureSnapshot> _getDescriptorPressure;
    private readonly Func<string> _getSettingsFingerprint;
    private readonly LongRunStabilityTracker _tracker;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly HashSet<string> _budgetViolations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _telemetryCoverageFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _informationalBudgetObservations = new(StringComparer.Ordinal);
    private readonly LongRunMemoryTrendAccumulator _managedMemoryTrend = new();
    private readonly LongRunMemoryTrendAccumulator _trackedGpuMemoryTrend = new();
    private readonly LongRunMemoryTrendAccumulator _actualGpuMemoryTrend = new();
    private readonly Dictionary<string, TailTimingAccumulator> _tailTiming =
        new(StringComparer.Ordinal);
    private int _budgetViolationFrameCount;
    private int _telemetryCoverageFailureFrameCount;
    private int _informationalBudgetObservationFrameCount;
    private int _lastPreparedFrameIndex = -1;
    private int _lastSampleInvocationFrameIndex = -1;
    private bool _warmupCollectionCompleted;
    private bool _completed;
    private RendererDiagnostics? _lastDiagnostics;
    private RenderBudgetProfileKind? _lastBudgetProfileKind;
    private long _postWarmupDescriptorSampleCount;
    private long _textureExhaustionSampleCount;
    private long _samplerExhaustionSampleCount;
    private int _maximumTextureUsed;
    private int _maximumTextureCapacity;
    private int _maximumSamplerUsed;
    private int _maximumSamplerCapacity;

    public SampleLongRunMonitor(
        SampleSmokeOptions options,
        SampleDeterministicLongRunWorkload workload,
        Func<DescriptorPressureSnapshot> getDescriptorPressure,
        Func<string> getSettingsFingerprint)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _workload = workload ?? throw new ArgumentNullException(nameof(workload));
        _getDescriptorPressure =
            getDescriptorPressure ?? throw new ArgumentNullException(nameof(getDescriptorPressure));
        _getSettingsFingerprint = getSettingsFingerprint ??
            throw new ArgumentNullException(nameof(getSettingsFingerprint));
        if (_options.LongRunMaxRetainedSamples is < 2 or
            > MaximumRetainedSampleCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Long-run retained sample count must be between two and " +
                $"{MaximumRetainedSampleCapacity}.");
        }
        if (_options.LongRunWarmupFrames < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Long-run warmup frame count cannot be negative.");
        }
        if (_options.LongRunSampleInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Long-run sample interval must be positive.");
        }

        _tracker = new LongRunStabilityTracker(_options.LongRunMaxRetainedSamples);
    }

    public LongRunStabilityTracker Tracker => _tracker;

    public void PrepareFrame(int frameIndex)
    {
        if (_completed)
            return;
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (frameIndex < _lastPreparedFrameIndex)
        {
            throw new InvalidOperationException(
                "Long-run frame indices cannot move backwards.");
        }
        if (frameIndex == _lastPreparedFrameIndex)
            return;

        _workload.PrepareFrame(frameIndex);
        _lastPreparedFrameIndex = frameIndex;
    }

    public void Sample(
        int frameIndex,
        RendererDiagnostics diagnostics,
        RenderBudgetSnapshot budget)
    {
        if (_completed)
            return;
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(budget);
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (frameIndex < _lastSampleInvocationFrameIndex)
        {
            throw new InvalidOperationException(
                "Long-run sample frame indices cannot move backwards.");
        }
        if (frameIndex == _lastSampleInvocationFrameIndex)
            return;
        _lastDiagnostics = diagnostics;

        bool isWarmupBoundary = frameIndex == _options.LongRunWarmupFrames;
        if (!isWarmupBoundary && frameIndex % _options.LongRunSampleInterval != 0)
        {
            _lastSampleInvocationFrameIndex = frameIndex;
            return;
        }

        if (!_warmupCollectionCompleted &&
            frameIndex >= _options.LongRunWarmupFrames)
        {
            // Sample() runs after this frame's renderer budgets have been
            // finalized. Collect here so the managed baseline measures retained
            // state, not the allocations made between PrepareFrame() and Draw().
            CollectRetainedManagedMemory();
            _warmupCollectionCompleted = true;
        }

        RenderBudgetSnapshot evaluatedBudget = budget;
        RendererDiagnostics coverageDiagnostics = diagnostics;
        if (_options.TailDdgiLongSoak)
        {
            BudgetMetric[] informationalOverBudget =
                (budget.Metrics ?? Array.Empty<BudgetMetric>())
                    .Where(metric =>
                        metric.Status == RenderBudgetStatus.OverBudget &&
                        SampleTailDdgiLongSoakProfile.IsNonApplicableBudgetMetric(
                            metric.Name))
                    .ToArray();
            if (frameIndex >= _options.LongRunWarmupFrames &&
                informationalOverBudget.Length > 0)
            {
                _informationalBudgetObservationFrameCount++;
                foreach (BudgetMetric metric in informationalOverBudget)
                {
                    _informationalBudgetObservations.Add(
                        $"{metric.Name} (not part of the tail-DDGI runtime budget)");
                }
            }

            SampleTailDdgiLongSoakBudgetProjection projection =
                SampleTailDdgiLongSoakProfile.ProjectBudget(
                    budget,
                    diagnostics);
            evaluatedBudget = projection.Budget;
            coverageDiagnostics = projection.CoverageDiagnostics;
        }

        IReadOnlyList<BudgetMetric> metrics =
            evaluatedBudget.Metrics ?? Array.Empty<BudgetMetric>();
        _lastBudgetProfileKind = evaluatedBudget.Profile.Kind;
        SampleBudgetMetricCoverage metricCoverage =
            SampleBudgetMetricCoverage.Evaluate(
                metrics,
                coverageDiagnostics,
                $"Long-run frame {frameIndex}",
                evaluatedBudget.OverallStatus);
        if (frameIndex >= _options.LongRunWarmupFrames && !metricCoverage.Passed)
        {
            _telemetryCoverageFailureFrameCount++;
            if (_telemetryCoverageFailures.Count < MaximumDistinctBudgetViolations)
            {
                _telemetryCoverageFailures.Add(
                    metricCoverage.Failure ??
                    $"Long-run frame {frameIndex} budget telemetry coverage failed.");
            }
        }

        string[] overBudget = metrics
            .OfType<BudgetMetric>()
            .Where(metric =>
                metric.Status == RenderBudgetStatus.OverBudget &&
                !(_options.TailDdgiLongSoak &&
                  SampleTailDdgiLongSoakProfile.IsPercentileTimingMetric(
                      metric.Name)))
            .Select(metric =>
                $"{metric.Name}={metric.Value:R}{metric.Unit}>{metric.FailureThreshold:R}{metric.Unit}")
            .ToArray();
        if (_options.TailDdgiLongSoak &&
            frameIndex >= _options.LongRunWarmupFrames)
        {
            foreach (BudgetMetric metric in metrics)
            {
                if (!SampleTailDdgiLongSoakProfile.IsPercentileTimingMetric(
                        metric.Name) ||
                    metric.Status is RenderBudgetStatus.Unavailable or
                        RenderBudgetStatus.Unknown)
                {
                    continue;
                }

                if (!_tailTiming.TryGetValue(
                        metric.Name,
                        out TailTimingAccumulator? accumulator))
                {
                    accumulator = new TailTimingAccumulator(
                        metric.Name,
                        metric.FailureThreshold);
                    _tailTiming.Add(metric.Name, accumulator);
                }
                accumulator.Add(metric.Value, metric.FailureThreshold);
            }
        }
        if (diagnostics.GpuMemoryBudgetQueryAvailable != 0 &&
            diagnostics.ActualGpuMemoryBudgetBytes > 0 &&
            diagnostics.ActualGpuMemoryUsageBytes > diagnostics.ActualGpuMemoryBudgetBytes)
        {
            overBudget =
            [
                .. overBudget,
                $"actual-gpu-memory={diagnostics.ActualGpuMemoryUsageBytes}bytes>" +
                $"{diagnostics.ActualGpuMemoryBudgetBytes}bytes"
            ];
        }
        if (frameIndex >= _options.LongRunWarmupFrames && overBudget.Length > 0)
        {
            _budgetViolationFrameCount++;
            foreach (string violation in overBudget)
            {
                if (_budgetViolations.Count >= MaximumDistinctBudgetViolations)
                    break;
                _budgetViolations.Add(violation);
            }
        }

        DescriptorPressureSnapshot descriptorPressure = _getDescriptorPressure();
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        ulong actualGpuBytes = diagnostics.GpuMemoryBudgetQueryAvailable != 0
            ? diagnostics.ActualGpuMemoryUsageBytes
            : 0;
        _tracker.Add(new LongRunStabilitySample(
            frameIndex,
            managedBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            descriptorPressure)
        {
            TrackedGpuMemoryBytes = diagnostics.TrackedGpuMemoryBytes,
            ActualGpuMemoryUsageBytes = actualGpuBytes,
            EffectiveGpuMemoryBudgetBytes = diagnostics.GpuMemoryBudgetQueryAvailable != 0
                ? diagnostics.ActualGpuMemoryBudgetBytes
                : diagnostics.GpuMemoryBudgetBytes,
            BudgetStatus = evaluatedBudget.OverallStatus,
            OverBudgetMetrics = overBudget
        });
        if (frameIndex >= _options.LongRunWarmupFrames)
        {
            _postWarmupDescriptorSampleCount++;
            if (descriptorPressure.IsTextureExhausted)
                _textureExhaustionSampleCount++;
            if (descriptorPressure.IsSamplerExhausted)
                _samplerExhaustionSampleCount++;
            _maximumTextureUsed = Math.Max(
                _maximumTextureUsed,
                descriptorPressure.TextureUsed);
            _maximumTextureCapacity = Math.Max(
                _maximumTextureCapacity,
                descriptorPressure.TextureCapacity);
            _maximumSamplerUsed = Math.Max(
                _maximumSamplerUsed,
                descriptorPressure.SamplerUsed);
            _maximumSamplerCapacity = Math.Max(
                _maximumSamplerCapacity,
                descriptorPressure.SamplerCapacity);
            _managedMemoryTrend.Add(
                frameIndex,
                checked((ulong)Math.Max(0L, managedBytes)));
            _trackedGpuMemoryTrend.Add(frameIndex, diagnostics.TrackedGpuMemoryBytes);
            if (diagnostics.GpuMemoryBudgetQueryAvailable != 0)
                _actualGpuMemoryTrend.Add(frameIndex, actualGpuBytes);
        }
        _lastSampleInvocationFrameIndex = frameIndex;
    }

    public SampleLongRunCompletion Complete()
    {
        if (_completed)
            throw new InvalidOperationException("The long-run report has already been completed.");
        _completed = true;
        _elapsed.Stop();

        SampleLongRunWorkloadSummary workload = _workload.Restore();
        if (_warmupCollectionCompleted &&
            _managedMemoryTrend.SampleCount > 0 &&
            _lastPreparedFrameIndex < int.MaxValue)
        {
            // Pair the collected warmup baseline with a collected terminal
            // endpoint. Intermediate observations remain useful for slope and
            // sawtooth visibility, while dead generation-zero garbage cannot
            // masquerade as retained growth merely because the run ended just
            // before a natural collection.
            CollectRetainedManagedMemory();
            _managedMemoryTrend.Add(
                _lastPreparedFrameIndex + 1,
                checked((ulong)Math.Max(
                    0L,
                    GC.GetTotalMemory(forceFullCollection: false))));
        }
        LongRunMemoryTrend managedTrend = _managedMemoryTrend.Evaluate(
            "managed-memory",
            _options.LongRunMemoryGrowthToleranceBytes);
        bool actualGpuMemoryAvailable = _actualGpuMemoryTrend.SampleCount >= 2;
        LongRunMemoryTrend gpuTrend = actualGpuMemoryAvailable
            ? _actualGpuMemoryTrend.Evaluate(
                "actual-gpu-memory",
                _options.LongRunMemoryGrowthToleranceBytes)
            : _trackedGpuMemoryTrend.Evaluate(
                "tracked-gpu-memory",
                _options.LongRunMemoryGrowthToleranceBytes);

        var failures = new List<string>();
        SampleTailDdgiLongSoakTimingGate[] tailTimingGates =
            BuildTailTimingGates();
        if (managedTrend.SampleCount < 2 || gpuTrend.SampleCount < 2)
            failures.Add("Fewer than two post-warmup telemetry samples were captured.");
        if (managedTrend.HasPositiveTrend)
        {
            failures.Add(
                $"Managed memory has a positive post-warmup trend " +
                $"({managedTrend.SlopeBytesPerFrame:R} bytes/frame, net={managedTrend.NetGrowthBytes} bytes).");
        }
        if (gpuTrend.HasPositiveTrend)
        {
            failures.Add(
                $"{gpuTrend.Signal} has a positive post-warmup trend " +
                $"({gpuTrend.SlopeBytesPerFrame:R} bytes/frame, net={gpuTrend.NetGrowthBytes} bytes).");
        }
        if (_budgetViolationFrameCount > 0)
        {
            failures.Add(
                $"{_budgetViolationFrameCount} sampled post-warmup frame(s) exceeded a renderer budget.");
        }
        if (_telemetryCoverageFailureFrameCount > 0)
        {
            failures.Add(
                $"{_telemetryCoverageFailureFrameCount} sampled post-warmup frame(s) " +
                "had incomplete required renderer budget telemetry.");
        }
        if (_textureExhaustionSampleCount != 0 ||
            _samplerExhaustionSampleCount != 0)
        {
            failures.Add("The bindless image/sampler descriptor table reached capacity.");
        }
        long expectedSampleCount = CountExpectedSamples(
            _lastPreparedFrameIndex,
            _options.LongRunWarmupFrames,
            _options.LongRunSampleInterval);
        if (_tracker.TotalSampleCount != expectedSampleCount)
        {
            failures.Add(
                $"Long-run sampling cadence was incomplete: expected {expectedSampleCount} " +
                $"sample(s), observed {_tracker.TotalSampleCount}.");
        }
        if (workload.PreparedFrameCount == 0 || workload.MaterialMutationCount == 0)
            failures.Add("The deterministic camera/material workload did not execute.");
        if (!workload.MaterialRollbackSucceeded || !workload.CameraRollbackSucceeded)
            failures.Add("The long-run workload did not roll back cleanly without a restart.");

        if (_options.TailDdgiLongSoak)
        {
            if (_lastBudgetProfileKind !=
                RenderBudgetProfileKind.HighSpec1440p60)
            {
                failures.Add(
                    "The tail-DDGI soak did not run under the HighSpec1440p60 budget profile.");
            }
            if (tailTimingGates.Length !=
                SampleTailDdgiLongSoakProfile.RequiredPercentileTimingMetrics.Count)
            {
                failures.Add(
                    "The tail-DDGI soak did not capture every required percentile timing metric.");
            }
            foreach (SampleTailDdgiLongSoakTimingGate gate in tailTimingGates)
            {
                if (!gate.Passed)
                {
                    failures.Add(
                        $"{gate.Name} P95 exceeded its production budget " +
                        $"({gate.Statistics.P95Milliseconds:R}ms > " +
                        $"{gate.FailureThresholdMilliseconds:R}ms).");
                }
            }
            RendererDiagnostics? tailDiagnostics = _lastDiagnostics;
            if (tailDiagnostics == null ||
                tailDiagnostics.ActiveQualityPreset != RenderQualityPreset.DdgiHigh ||
                tailDiagnostics.SimpleDdgiSchedulerMode !=
                    SimpleDdgiSchedulerMode.GpuResident ||
                tailDiagnostics.SimpleDdgiActive == 0 ||
                tailDiagnostics.SimpleDdgiTransportV2Active == 0 ||
                !tailDiagnostics.SimpleDdgiTransportTailCertificationEnabled ||
                !tailDiagnostics.SimpleDdgiTransportAccelerationEnabled)
            {
                failures.Add(
                    "The tail-DDGI soak did not retain the accelerated, certified, gpu-resident DDGI identity.");
            }
            else if (tailDiagnostics.SimpleDdgiTrackingState ==
                         SimpleDdgiTrackingState.StaticConverged &&
                     !tailDiagnostics.SimpleDdgiTransportConvergence
                         .TailCertificateCurrent)
            {
                failures.Add(
                    "The tail-DDGI soak reported StaticConverged without a current accepted certificate.");
            }
        }

        string? failure = failures.Count == 0 ? null : string.Join(" ", failures);
        string reportPath = ResolveReportPath(_options.LongRunReportPath);
        var recovery = new SampleRecoveryCapability(
            Supported: false,
            Attempted: false,
            Status: "rejected-unsupported",
            Reason:
                "No safe deterministic device-loss injection is exposed by the renderer; " +
                "unsafe driver/device fault injection was intentionally not attempted.");
        RendererDiagnostics producerDiagnostics = _lastDiagnostics ??
            throw new InvalidOperationException(
                "Long-run completion requires at least one renderer diagnostics observation.");
        var descriptorPressure = new SampleLongRunDescriptorPressureSummary(
            _postWarmupDescriptorSampleCount,
            _textureExhaustionSampleCount,
            _samplerExhaustionSampleCount,
            _maximumTextureUsed,
            _maximumTextureCapacity,
            _maximumSamplerUsed,
            _maximumSamplerCapacity);
        var report = new SampleLongRunReport(
            SchemaVersion: 4,
            Kind: MaterialGiReleaseEvidenceContract.LongRunProducerKind,
            Status: failure == null ? "passed" : "failed",
            Failure: failure,
            StartedUtc: _startedUtc,
            CompletedUtc: DateTimeOffset.UtcNow,
            ElapsedSeconds: _elapsed.Elapsed.TotalSeconds,
            RequestedFrameCount: _options.FrameCount,
            RequestedMinutes: _options.LongRunMinutes,
            WarmupFrames: _options.LongRunWarmupFrames,
            SampleIntervalFrames: _options.LongRunSampleInterval,
            RetainedSampleCapacity: _tracker.Capacity,
            LastPreparedFrameIndex: _lastPreparedFrameIndex,
            ExpectedSampleCount: expectedSampleCount,
            TotalSamples: _tracker.TotalSampleCount,
            RetainedSamples: _tracker.Samples,
            ManagedMemoryTrend: managedTrend,
            GpuMemoryTrend: gpuTrend,
            GpuMemorySignal: actualGpuMemoryAvailable ? "VK_EXT_memory_budget" : "renderer-tracked",
            PostWarmupBudgetViolationFrameCount: _budgetViolationFrameCount,
            BudgetViolations: _budgetViolations.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            PostWarmupTelemetryCoverageFailureFrameCount: _telemetryCoverageFailureFrameCount,
            TelemetryCoverageFailures: _telemetryCoverageFailures
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            DescriptorPressure: descriptorPressure,
            Workload: workload,
            DeviceLossRecovery: recovery,
            ProducerIdentity:
                SampleMaterialGiProducerIdentityFactory.Create(
                    producerDiagnostics,
                    _getSettingsFingerprint(),
                    _options.TailDdgiLongSoak ? "High" : string.Empty))
        {
            BuildConfiguration = producerDiagnostics.CaptureRun.BuildConfiguration,
            QualificationProfile = _options.TailDdgiLongSoak
                ? SampleTailDdgiLongSoakProfile.Name
                : string.Empty,
            GiGpuMetricSource = _options.TailDdgiLongSoak
                ? SampleTailDdgiLongSoakProfile.GiGpuMetricSource
                : string.Empty,
            NonApplicableBudgetMetrics = _options.TailDdgiLongSoak
                ? SampleTailDdgiLongSoakProfile.NonApplicableBudgetMetrics
                : Array.Empty<string>(),
            InformationalBudgetObservationFrameCount =
                _informationalBudgetObservationFrameCount,
            InformationalBudgetObservations =
                _informationalBudgetObservations
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
            CaptureRenderWidth = producerDiagnostics.CaptureRenderWidth,
            CaptureRenderHeight = producerDiagnostics.CaptureRenderHeight,
            TailTimingGates = tailTimingGates
        };

        WriteReportAtomically(reportPath, report);
        return new SampleLongRunCompletion(
            failure == null,
            failure,
            reportPath,
            report);
    }

    internal static long CountExpectedSamples(
        int lastPreparedFrameIndex,
        int warmupFrameIndex,
        int sampleIntervalFrames)
    {
        if (lastPreparedFrameIndex < 0)
            return 0;
        if (warmupFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(warmupFrameIndex));
        if (sampleIntervalFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleIntervalFrames));

        long intervalSamples =
            (long)lastPreparedFrameIndex / sampleIntervalFrames + 1L;
        bool addWarmupBoundary =
            warmupFrameIndex <= lastPreparedFrameIndex &&
            warmupFrameIndex % sampleIntervalFrames != 0;
        return checked(intervalSamples + (addWarmupBoundary ? 1L : 0L));
    }

    private static string ResolveReportPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(
            Path.Combine(Environment.CurrentDirectory, "material-gi-long-run-report.json"));
    }

    private SampleTailDdgiLongSoakTimingGate[] BuildTailTimingGates()
    {
        if (!_options.TailDdgiLongSoak)
            return Array.Empty<SampleTailDdgiLongSoakTimingGate>();

        return _tailTiming.Values
            .OrderBy(static accumulator => accumulator.Name, StringComparer.Ordinal)
            .Select(static accumulator => accumulator.Build())
            .ToArray();
    }

    private static void CollectRetainedManagedMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WriteReportAtomically(string path, SampleLongRunReport report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(report, options);
        SampleEvidenceFileIo.WriteAtomic(
            path,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Material/GI long-run report");
    }

    private sealed class TailTimingAccumulator
    {
        private readonly List<double> _values = new();
        private bool _thresholdChanged;

        public TailTimingAccumulator(
            string name,
            double failureThresholdMilliseconds)
        {
            Name = name;
            FailureThresholdMilliseconds = failureThresholdMilliseconds;
        }

        public string Name { get; }
        public double FailureThresholdMilliseconds { get; }

        public void Add(double value, double failureThresholdMilliseconds)
        {
            if (!double.IsFinite(value))
                return;
            _thresholdChanged |=
                failureThresholdMilliseconds !=
                FailureThresholdMilliseconds;
            _values.Add(value);
        }

        public SampleTailDdgiLongSoakTimingGate Build()
        {
            SampleBenchmarkTimingStats statistics =
                SampleBenchmarkAnalyzer.BuildStats(Name, _values);
            bool passed =
                !_thresholdChanged &&
                statistics.Count > 0 &&
                double.IsFinite(FailureThresholdMilliseconds) &&
                FailureThresholdMilliseconds > 0.0 &&
                statistics.P95Milliseconds <=
                    FailureThresholdMilliseconds;
            return new SampleTailDdgiLongSoakTimingGate(
                Name,
                statistics,
                FailureThresholdMilliseconds,
                passed);
        }
    }
}
