using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Run-wide observations which cannot be reconstructed from the fixed
/// measurement window. In particular, convergence starts during warmup.
/// </summary>
public sealed record SampleTailDdgiRunObservation(
    int ObservedFrameCount,
    int ActiveFrameCount,
    int SolveEpochCount,
    int ConvergenceFrameCount,
    int CurrentCertificateFrameCount,
    int StaticConvergedWithoutCurrentCertificateCount,
    ulong CachedTransportRayEvaluationCount,
    ulong CachedSolverIterationCount,
    ulong AuditChunkCount)
{
    public int FirstSourceReadyFrameCount { get; init; }
    public int FirstSolveEpochFrameCount { get; init; }
    public uint MaximumSolveParticipantCount { get; init; }
    public uint MaximumSolveVisitedCount { get; init; }
    public ulong HardSourceProbeCount { get; init; }
    public ulong RoutineSourceProbeCount { get; init; }
    public ulong CachedSolverProbeCount { get; init; }
    public ulong PrimaryProbeCount { get; init; }
    public ulong PrimaryRayCount { get; init; }
    public ulong RayQueryCount { get; init; }
    public ulong ShadowRayCount { get; init; }
    public ulong EstimatedShadowRayUpperBound { get; init; }

    public static SampleTailDdgiRunObservation Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0UL, 0UL, 0UL);
}

/// <summary>
/// Immutable qualification evidence emitted with every benchmark report.
/// Counts cover the measurement window unless explicitly named run-wide.
/// </summary>
public sealed record SampleTailDdgiRuntimeEvidence
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;
    public string Variant { get; init; } = SampleBenchmarkCaptureVariant.Baseline;
    public SampleBenchmarkTimingStats GiGpuMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("GI GPU");
    public SampleBenchmarkTimingStats AcceleratedSolveGpuMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("Simple DDGI accelerated solve GPU");
    public SampleBenchmarkTimingStats AuditGpuMilliseconds { get; init; } =
        SampleBenchmarkTimingStats.Empty("Simple DDGI audit GPU");
    public ulong PrimaryProbeCount { get; init; }
    public ulong PrimaryRayCount { get; init; }
    public ulong RayQueryCount { get; init; }
    public ulong ShadowRayCount { get; init; }
    public ulong EstimatedShadowRayUpperBound { get; init; }
    public ulong CachedTransportRayEvaluationCount { get; init; }
    public ulong CachedSolverIterationCount { get; init; }
    public ulong AuditChunkCount { get; init; }
    public ulong RunCachedTransportRayEvaluationCount { get; init; }
    public ulong RunCachedSolverIterationCount { get; init; }
    public ulong RunAuditChunkCount { get; init; }
    public int RunObservedFrameCount { get; init; }
    public int RunActiveFrameCount { get; init; }
    public int SolveEpochCount { get; init; }
    public int ConvergenceFrameCount { get; init; }
    public int CurrentCertificateFrameCount { get; init; }
    public int StaticConvergedWithoutCurrentCertificateCount { get; init; }
    public int FirstSourceReadyFrameCount { get; init; }
    public int FirstSolveEpochFrameCount { get; init; }
    public uint MaximumSolveParticipantCount { get; init; }
    public uint MaximumSolveVisitedCount { get; init; }
    public ulong RunHardSourceProbeCount { get; init; }
    public ulong RunRoutineSourceProbeCount { get; init; }
    public ulong RunCachedSolverProbeCount { get; init; }
    public ulong RunPrimaryProbeCount { get; init; }
    public ulong RunPrimaryRayCount { get; init; }
    public ulong RunRayQueryCount { get; init; }
    public ulong RunShadowRayCount { get; init; }
    public ulong RunEstimatedShadowRayUpperBound { get; init; }
    public SimpleDdgiTrackingState FinalTrackingState { get; init; } =
        SimpleDdgiTrackingState.Bootstrapping;
    public SimpleDdgiSchedulerMode SchedulerMode { get; init; } =
        SimpleDdgiSchedulerMode.CpuReference;
    public bool TailCertificationEnabled { get; init; }
    public bool AccelerationEnabled { get; init; }
    public string TailCertificationFallbackReason { get; init; } = string.Empty;
    public uint ExpectedParticipantCount { get; init; }
    public uint AuditedParticipantCount { get; init; }
    public uint ExcludedInactiveCount { get; init; }
    public uint ExcludedNotVisibleCount { get; init; }
    public uint InvalidCacheCount { get; init; }
    public uint CacheIdentityFailureCount { get; init; }
    public uint CacheCardinalityFailureCount { get; init; }
    public uint CacheSourceGenerationFailureCount { get; init; }
    public uint CacheSourceEpochFailureCount { get; init; }
    public uint CachePhysicalGenerationFailureCount { get; init; }
    public uint NonFiniteCount { get; init; }
    public uint CounterOverflowCount { get; init; }
    public uint ExpectedTexelCount { get; init; }
    public uint AuditedTexelCount { get; init; }
    public float FinalTailBound { get; init; }
    public float FinalTailTolerance { get; init; }
    public bool FinalAuditComplete { get; init; }
    public bool FinalCertificateCurrent { get; init; }
    public ulong TrackedGpuMemoryBytes { get; init; }
    public ulong GpuMemoryBudgetBytes { get; init; }
    public ulong DdgiTextureBytes { get; init; }
    public ulong DdgiBufferBytes { get; init; }
    public ulong ReceiverProbeBytes { get; init; }
    public ulong SchedulerArenaBytes { get; init; }
    public ulong SchedulerFeedbackReadbackBytes { get; init; }
    public ulong SchedulerAuditReadbackBytes { get; init; }

    public static SampleTailDdgiRuntimeEvidence Unavailable(string reason) => new()
    {
        UnavailableReason = string.IsNullOrWhiteSpace(reason)
            ? "Tail-certified DDGI evidence is unavailable."
            : reason.Trim()
    };
}

/// <summary>Allocation-free observer used while benchmark warmup is running.</summary>
internal sealed class SampleTailDdgiRunObserver
{
    private int _observedFrameCount;
    private int _activeFrameCount;
    private int _solveEpochCount;
    private int _convergenceFrameCount;
    private int _currentCertificateFrameCount;
    private int _staticWithoutCertificateCount;
    private ulong _cachedTransportRayEvaluationCount;
    private ulong _cachedSolverIterationCount;
    private ulong _auditChunkCount;
    private uint _lastSolveEpoch;
    private bool _hasSolveEpoch;
    private int _firstSourceReadyFrameCount;
    private int _firstSolveEpochFrameCount;
    private uint _maximumSolveParticipantCount;
    private uint _maximumSolveVisitedCount;
    private ulong _hardSourceProbeCount;
    private ulong _routineSourceProbeCount;
    private ulong _cachedSolverProbeCount;
    private ulong _primaryProbeCount;
    private ulong _primaryRayCount;
    private ulong _rayQueryCount;
    private ulong _shadowRayCount;
    private ulong _estimatedShadowRayUpperBound;

    public void Observe(RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _observedFrameCount++;
        if (diagnostics.SimpleDdgiActive == 0 ||
            diagnostics.SimpleDdgiTransportV2Active == 0)
        {
            return;
        }

        _activeFrameCount++;
        _primaryProbeCount = checked(
            _primaryProbeCount +
            (ulong)Math.Max(0, diagnostics.SimpleDdgiTransportSourceRefreshProbeCount));
        _primaryRayCount = checked(
            _primaryRayCount + diagnostics.SimpleDdgiTransportSourceRayCount);
        _rayQueryCount = checked(
            _rayQueryCount + diagnostics.DdgiTraceRayCount);
        _shadowRayCount = checked(
            _shadowRayCount + diagnostics.DdgiVisibilityRayCount);
        _estimatedShadowRayUpperBound = checked(
            _estimatedShadowRayUpperBound +
            diagnostics.DdgiEstimatedShadowRayUpperBound);
        if (diagnostics.SimpleDdgiSchedulerFeedbackValid != 0)
        {
            if (_firstSourceReadyFrameCount == 0 &&
                diagnostics.SimpleDdgiSchedulerFeedbackPendingSourceCount == 0u)
            {
                _firstSourceReadyFrameCount = _activeFrameCount;
            }
            if (_firstSolveEpochFrameCount == 0 &&
                diagnostics.SimpleDdgiSchedulerFeedbackSolveEpoch != 0u)
            {
                _firstSolveEpochFrameCount = _activeFrameCount;
            }
            _maximumSolveParticipantCount = Math.Max(
                _maximumSolveParticipantCount,
                diagnostics.SimpleDdgiSchedulerFeedbackSolveParticipantCount);
            _maximumSolveVisitedCount = Math.Max(
                _maximumSolveVisitedCount,
                diagnostics.SimpleDdgiSchedulerFeedbackSolveVisitedCount);
            _hardSourceProbeCount = checked(
                _hardSourceProbeCount +
                diagnostics.SimpleDdgiSchedulerFeedbackHardSourceProbeCount);
            _routineSourceProbeCount = checked(
                _routineSourceProbeCount +
                diagnostics.SimpleDdgiSchedulerFeedbackRoutineSourceProbeCount);
            _cachedSolverProbeCount = checked(
                _cachedSolverProbeCount +
                diagnostics.SimpleDdgiSchedulerFeedbackCachedSolverProbeCount);
        }
        ulong cachedSweeps = checked((ulong)Math.Max(
            0,
            diagnostics.SimpleDdgiTransportCachedSweepCount));
        ulong cachedTransportRayCount = diagnostics.SimpleDdgiTransportSolveRayCount >
            diagnostics.SimpleDdgiTransportSourceRayCount
            ? diagnostics.SimpleDdgiTransportSolveRayCount -
                diagnostics.SimpleDdgiTransportSourceRayCount
            : 0UL;
        _cachedTransportRayEvaluationCount = checked(
            _cachedTransportRayEvaluationCount +
            cachedTransportRayCount * cachedSweeps);
        _cachedSolverIterationCount = checked(
            _cachedSolverIterationCount +
            cachedSweeps);
        _auditChunkCount = checked(
            _auditChunkCount +
            (ulong)Math.Max(0, diagnostics.SimpleDdgiTransportAuditChunkCount));
        SimpleDdgiTransportConvergenceTelemetry tail =
            diagnostics.SimpleDdgiTransportConvergence;
        if (tail.TailSolveEpoch != 0u &&
            (!_hasSolveEpoch ||
             tail.TailSolveEpoch != _lastSolveEpoch))
        {
            _solveEpochCount++;
            _lastSolveEpoch = tail.TailSolveEpoch;
            _hasSolveEpoch = true;
        }

        if (tail.TailCertificateCurrent)
        {
            _currentCertificateFrameCount++;
            if (_convergenceFrameCount == 0)
            {
                // Acceleration is evaluated over rendered solve work. Source
                // cohort construction is identical by contract and can dwarf
                // the actual solver interval, hiding a real 30% improvement.
                int firstSolveFrame = _firstSolveEpochFrameCount > 0
                    ? _firstSolveEpochFrameCount
                    : 1;
                _convergenceFrameCount = checked(
                    _activeFrameCount - firstSolveFrame + 1);
            }
        }

        if (diagnostics.SimpleDdgiTrackingState ==
                SimpleDdgiTrackingState.StaticConverged &&
            !tail.TailCertificateCurrent)
        {
            _staticWithoutCertificateCount++;
        }
    }

    public SampleTailDdgiRunObservation Snapshot() => new(
        _observedFrameCount,
        _activeFrameCount,
        _solveEpochCount,
        _convergenceFrameCount,
        _currentCertificateFrameCount,
        _staticWithoutCertificateCount,
        _cachedTransportRayEvaluationCount,
        _cachedSolverIterationCount,
        _auditChunkCount)
    {
        FirstSourceReadyFrameCount = _firstSourceReadyFrameCount,
        FirstSolveEpochFrameCount = _firstSolveEpochFrameCount,
        MaximumSolveParticipantCount = _maximumSolveParticipantCount,
        MaximumSolveVisitedCount = _maximumSolveVisitedCount,
        HardSourceProbeCount = _hardSourceProbeCount,
        RoutineSourceProbeCount = _routineSourceProbeCount,
        CachedSolverProbeCount = _cachedSolverProbeCount,
        PrimaryProbeCount = _primaryProbeCount,
        PrimaryRayCount = _primaryRayCount,
        RayQueryCount = _rayQueryCount,
        ShadowRayCount = _shadowRayCount,
        EstimatedShadowRayUpperBound = _estimatedShadowRayUpperBound
    };
}

public static class SampleTailDdgiRuntimeEvidenceBuilder
{
    public static SampleTailDdgiRuntimeEvidence Create(
        IReadOnlyList<RendererDiagnostics> samples,
        SampleTailDdgiRunObservation observation,
        string? variant)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(observation);
        RendererDiagnostics[] active = samples
            .Where(static sample =>
                sample.SimpleDdgiActive != 0 &&
                sample.SimpleDdgiTransportV2Active != 0)
            .ToArray();
        if (active.Length == 0)
        {
            return SampleTailDdgiRuntimeEvidence.Unavailable(
                "No active V2 Simple-DDGI measurement samples were captured.");
        }

        RendererDiagnostics last = active[^1];
        SimpleDdgiTransportConvergenceTelemetry tail =
            last.SimpleDdgiTransportConvergence;
        return new SampleTailDdgiRuntimeEvidence
        {
            Available = true,
            Variant = string.IsNullOrWhiteSpace(variant)
                ? SampleBenchmarkCaptureVariant.Baseline
                : variant.Trim().ToLowerInvariant(),
            GiGpuMilliseconds = SampleBenchmarkAnalyzer.BuildStats(
                "GI GPU",
                active
                    .Where(static sample => sample.GpuTimingValid != 0)
                    .Select(static sample =>
                        sample.GpuDdgiUpdateMicroseconds / 1000.0)),
            AcceleratedSolveGpuMilliseconds = SampleBenchmarkAnalyzer.BuildStats(
                "Simple DDGI accelerated solve GPU",
                active
                    .Where(static sample => sample.GpuTimingValid != 0)
                    .Select(static sample =>
                        sample.GpuSimpleDdgiAcceleratedSolveMicroseconds / 1000.0)),
            AuditGpuMilliseconds = SampleBenchmarkAnalyzer.BuildStats(
                "Simple DDGI audit GPU",
                active
                    .Where(static sample => sample.GpuTimingValid != 0)
                    .Select(static sample =>
                        sample.GpuSimpleDdgiTransportAuditMicroseconds / 1000.0)),
            PrimaryProbeCount = CheckedSum(
                active,
                static sample => checked((ulong)Math.Max(
                    0,
                    sample.SimpleDdgiTransportSourceRefreshProbeCount))),
            PrimaryRayCount = CheckedSum(
                active,
                static sample => sample.SimpleDdgiTransportSourceRayCount),
            RayQueryCount = CheckedSum(
                active,
                static sample => sample.DdgiTraceRayCount),
            ShadowRayCount = CheckedSum(
                active,
                static sample => sample.DdgiVisibilityRayCount),
            EstimatedShadowRayUpperBound = CheckedSum(
                active,
                static sample => sample.DdgiEstimatedShadowRayUpperBound),
            CachedTransportRayEvaluationCount = CheckedSum(
                active,
                static sample => checked(
                    (sample.SimpleDdgiTransportSolveRayCount >
                        sample.SimpleDdgiTransportSourceRayCount
                        ? sample.SimpleDdgiTransportSolveRayCount -
                            sample.SimpleDdgiTransportSourceRayCount
                        : 0UL) *
                    (ulong)Math.Max(
                        0,
                        sample.SimpleDdgiTransportCachedSweepCount))),
            CachedSolverIterationCount = CheckedSum(
                active,
                static sample => checked((ulong)Math.Max(
                    0,
                    sample.SimpleDdgiTransportCachedSweepCount))),
            AuditChunkCount = CheckedSum(
                active,
                static sample => checked((ulong)Math.Max(
                    0,
                    sample.SimpleDdgiTransportAuditChunkCount))),
            RunCachedTransportRayEvaluationCount =
                observation.CachedTransportRayEvaluationCount,
            RunCachedSolverIterationCount =
                observation.CachedSolverIterationCount,
            RunAuditChunkCount = observation.AuditChunkCount,
            RunObservedFrameCount = observation.ObservedFrameCount,
            RunActiveFrameCount = observation.ActiveFrameCount,
            SolveEpochCount = observation.SolveEpochCount,
            ConvergenceFrameCount = observation.ConvergenceFrameCount,
            CurrentCertificateFrameCount = observation.CurrentCertificateFrameCount,
            StaticConvergedWithoutCurrentCertificateCount =
                observation.StaticConvergedWithoutCurrentCertificateCount,
            FirstSourceReadyFrameCount = observation.FirstSourceReadyFrameCount,
            FirstSolveEpochFrameCount = observation.FirstSolveEpochFrameCount,
            MaximumSolveParticipantCount = observation.MaximumSolveParticipantCount,
            MaximumSolveVisitedCount = observation.MaximumSolveVisitedCount,
            RunHardSourceProbeCount = observation.HardSourceProbeCount,
            RunRoutineSourceProbeCount = observation.RoutineSourceProbeCount,
            RunCachedSolverProbeCount = observation.CachedSolverProbeCount,
            RunPrimaryProbeCount = observation.PrimaryProbeCount,
            RunPrimaryRayCount = observation.PrimaryRayCount,
            RunRayQueryCount = observation.RayQueryCount,
            RunShadowRayCount = observation.ShadowRayCount,
            RunEstimatedShadowRayUpperBound =
                observation.EstimatedShadowRayUpperBound,
            FinalTrackingState = last.SimpleDdgiTrackingState,
            SchedulerMode = last.SimpleDdgiSchedulerMode,
            TailCertificationEnabled =
                last.SimpleDdgiTransportTailCertificationEnabled,
            AccelerationEnabled =
                last.SimpleDdgiTransportAccelerationEnabled,
            TailCertificationFallbackReason =
                last.SimpleDdgiTransportTailCertificationFallbackReason,
            ExpectedParticipantCount = tail.TailExpectedParticipantCount,
            AuditedParticipantCount = tail.TailAuditedParticipantCount,
            ExcludedInactiveCount = tail.TailExcludedInactiveCount,
            ExcludedNotVisibleCount = tail.TailExcludedNotVisibleCount,
            InvalidCacheCount = tail.TailExcludedInvalidCacheCount,
            CacheIdentityFailureCount = tail.TailCacheIdentityFailureCount,
            CacheCardinalityFailureCount = tail.TailCacheCardinalityFailureCount,
            CacheSourceGenerationFailureCount =
                tail.TailCacheSourceGenerationFailureCount,
            CacheSourceEpochFailureCount = tail.TailCacheSourceEpochFailureCount,
            CachePhysicalGenerationFailureCount =
                tail.TailCachePhysicalGenerationFailureCount,
            NonFiniteCount = tail.TailNonFiniteCount,
            CounterOverflowCount = tail.TailCounterOverflowCount,
            ExpectedTexelCount = tail.TailExpectedTexelCount,
            AuditedTexelCount = tail.TailAuditedTexelCount,
            FinalTailBound = tail.TailAbsoluteBound,
            FinalTailTolerance = tail.TailTolerance,
            FinalAuditComplete = tail.TailAuditComplete,
            FinalCertificateCurrent = tail.TailCertificateCurrent,
            TrackedGpuMemoryBytes = last.TrackedGpuMemoryBytes,
            GpuMemoryBudgetBytes = last.GpuMemoryBudgetBytes,
            DdgiTextureBytes = last.DdgiTextureBytes,
            DdgiBufferBytes = last.DdgiBufferBytes,
            // CapacityDetails describes only this frame's transition work and
            // is zero on the stable-key path used by production captures. The
            // current allocation is the authoritative memory evidence.
            ReceiverProbeBytes = last.SimpleDdgiReceiverProbeBytes,
            SchedulerArenaBytes = last.SimpleDdgiSchedulerArenaBytes,
            SchedulerFeedbackReadbackBytes =
                last.SimpleDdgiSchedulerFeedbackReadbackBytes,
            SchedulerAuditReadbackBytes =
                last.SimpleDdgiSchedulerAuditReadbackBytes
        };
    }

    private static ulong CheckedSum(
        IEnumerable<RendererDiagnostics> samples,
        Func<RendererDiagnostics, ulong> selector)
    {
        ulong total = 0UL;
        foreach (RendererDiagnostics sample in samples)
            total = checked(total + selector(sample));
        return total;
    }
}
