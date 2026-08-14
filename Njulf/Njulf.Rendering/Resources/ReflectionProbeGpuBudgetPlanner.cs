using System;

namespace Njulf.Rendering.Resources;

/// <summary>Per-frame reflection work cost estimates and reservations.</summary>
public readonly record struct ReflectionProbeGpuBudgetSnapshot(
    int BudgetMicroseconds,
    int ReservedMicroseconds,
    int FaceEstimateMicroseconds,
    int PrefilterEstimateMicroseconds,
    int CopyEstimateMicroseconds,
    bool HasTimingHistory,
    bool BudgetExhausted);

/// <summary>
/// Predicts the GPU cost of a single reflection work unit and admits work against the configured
/// per-frame budget. A nonzero budget always admits one unit per frame, even when its learned cost
/// exceeds the target. The budget is therefore a soft cap with a liveness floor: an expensive
/// capture can make slow progress, but it cannot leave an unpublished probe queued forever.
/// </summary>
public sealed class ReflectionProbeGpuBudgetPlanner
{
    private const int DefaultFaceEstimateMicroseconds = 100;
    private const int DefaultPrefilterEstimateMicroseconds = 125;
    private const int DefaultCopyEstimateMicroseconds = 25;
    private const int MaximumEstimateMicroseconds = 1_000_000;

    private int _budgetMicroseconds;
    private int _reservedMicroseconds;
    private int _faceEstimateMicroseconds = DefaultFaceEstimateMicroseconds;
    private int _prefilterEstimateMicroseconds = DefaultPrefilterEstimateMicroseconds;
    private int _copyEstimateMicroseconds = DefaultCopyEstimateMicroseconds;
    private bool _hasTimingHistory;

    public void BeginFrame(int budgetMicroseconds)
    {
        _budgetMicroseconds = Math.Clamp(budgetMicroseconds, 0, MaximumEstimateMicroseconds);
        _reservedMicroseconds = 0;
    }

    public bool CanReserve(ReflectionProbeWorkKind kind)
    {
        if (kind == ReflectionProbeWorkKind.None)
            return _budgetMicroseconds > 0;
        if (_budgetMicroseconds <= 0)
            return false;

        // Always admit the first unit. Without timing history this provides a bounded cold start;
        // with timing history it prevents an estimate above the whole frame budget from starving
        // an in-progress capture transaction forever.
        if (_reservedMicroseconds == 0)
            return true;

        // A cold start admits one unit, then waits for a completed timestamp. This remains a
        // deterministic cap even when a device reports no timestamp results for several frames.
        if (!_hasTimingHistory)
            return _reservedMicroseconds == 0;

        int estimate = GetEstimate(kind);
        return estimate > 0 &&
            _reservedMicroseconds <= _budgetMicroseconds - estimate;
    }

    public bool TryReserve(ReflectionProbeWorkKind kind)
    {
        if (!CanReserve(kind))
            return false;

        _reservedMicroseconds = checked(_reservedMicroseconds + GetEstimate(kind));
        return true;
    }

    public void Release(ReflectionProbeWorkKind kind)
    {
        if (kind == ReflectionProbeWorkKind.None)
            return;

        _reservedMicroseconds = Math.Max(0, _reservedMicroseconds - GetEstimate(kind));
    }

    public void RecordTiming(ReflectionProbeWorkKind kind, int unitCount, long measuredMicroseconds)
    {
        if (kind == ReflectionProbeWorkKind.None || unitCount <= 0 || measuredMicroseconds <= 0)
            return;

        long perUnit = (measuredMicroseconds + unitCount - 1L) / unitCount;
        int sample = (int)Math.Clamp(perUnit, 1L, MaximumEstimateMicroseconds);
        int previous = GetEstimate(kind);
        // EWMA alpha=1/4. The +2 term makes integer rounding nearest rather than always down.
        int updated = (int)Math.Clamp((previous * 3L + sample + 2L) / 4L, 1L, MaximumEstimateMicroseconds);
        SetEstimate(kind, updated);
        _hasTimingHistory = true;
    }

    public ReflectionProbeGpuBudgetSnapshot GetSnapshot() => new(
        _budgetMicroseconds,
        _reservedMicroseconds,
        _faceEstimateMicroseconds,
        _prefilterEstimateMicroseconds,
        _copyEstimateMicroseconds,
        _hasTimingHistory,
        _budgetMicroseconds > 0 && _reservedMicroseconds >= _budgetMicroseconds);

    private int GetEstimate(ReflectionProbeWorkKind kind) => kind switch
    {
        ReflectionProbeWorkKind.CaptureFace => _faceEstimateMicroseconds,
        ReflectionProbeWorkKind.PrefilterMip => _prefilterEstimateMicroseconds,
        ReflectionProbeWorkKind.PublishCopy => _copyEstimateMicroseconds,
        _ => 0
    };

    private void SetEstimate(ReflectionProbeWorkKind kind, int value)
    {
        switch (kind)
        {
            case ReflectionProbeWorkKind.CaptureFace:
                _faceEstimateMicroseconds = value;
                break;
            case ReflectionProbeWorkKind.PrefilterMip:
                _prefilterEstimateMicroseconds = value;
                break;
            case ReflectionProbeWorkKind.PublishCopy:
                _copyEstimateMicroseconds = value;
                break;
        }
    }
}
