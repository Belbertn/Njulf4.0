using System;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiNearFieldResidualExecutionScale : byte
{
    Eighth = 0,
    Quarter = 1,
    Half = 2
}

public readonly record struct SimpleDdgiNearFieldResidualExecutionExtent(
    int Width,
    int Height,
    SimpleDdgiNearFieldResidualExecutionScale Scale,
    uint Revision)
{
    public bool IsValid => Width > 0 && Height > 0 && Revision != 0U;
}

/// <summary>
/// Allocation-free C5 resolution governor. It consumes only fence-complete,
/// exclusive GPU timestamps and applies hysteresis so transient spikes cannot
/// churn history. Half resolution is reachable only when the immutable
/// admitted layout was itself measured and allocated at half resolution.
/// </summary>
public sealed class SimpleDdgiNearFieldResidualAdaptiveResolution
{
    public const ulong ProductionP95BudgetMicroseconds = 750UL;
    public const ulong PromotionP95HeadroomMicroseconds = 450UL;
    public const int SampleWindowSize = 120;
    public const int EvaluationCadence = 30;
    public const int PromotionWindowCount = 4;
    public const int LowestTierOverBudgetEvaluationCount = 2;
    public const int SuspensionFrameCount = 300;

    private readonly ulong[] _samples = new ulong[SampleWindowSize];
    private readonly ulong[] _sortedSamples = new ulong[SampleWindowSize];
    private readonly bool _enabled;
    private readonly bool _promotionEnabled;
    private readonly SimpleDdgiNearFieldResidualExecutionScale _maximumScale;
    private int _sampleCount;
    private int _nextSample;
    private int _samplesSinceEvaluation;
    private int _promotionWindows;
    private int _lowestTierOverBudgetEvaluations;
    private int _suspendedFramesRemaining;
    private ulong _authoritativeTimingSampleCount;
    private uint _promotionCount;
    private uint _demotionCount;
    private uint _suspensionCount;
    private uint _revision = 1U;

    public SimpleDdgiNearFieldResidualAdaptiveResolution(
        float maximumScale,
        bool enabled = true,
        SimpleDdgiNearFieldResidualExecutionScale? startingScale = null,
        bool promotionEnabled = true)
    {
        if (!float.IsFinite(maximumScale) || maximumScale < 0.125F)
            throw new ArgumentOutOfRangeException(nameof(maximumScale));

        _enabled = enabled;
        _promotionEnabled = enabled && promotionEnabled;
        _maximumScale = maximumScale >= 0.5F
            ? SimpleDdgiNearFieldResidualExecutionScale.Half
            : maximumScale >= 0.25F
                ? SimpleDdgiNearFieldResidualExecutionScale.Quarter
                : SimpleDdgiNearFieldResidualExecutionScale.Eighth;
        // Quarter is the production starting point. Half resolution must be
        // both admitted by immutable evidence and promoted by sustained live
        // GPU headroom; an eighth-only allocation necessarily starts eighth.
        SimpleDdgiNearFieldResidualExecutionScale defaultStartingScale = !_enabled
            ? _maximumScale
            : _maximumScale ==
                SimpleDdgiNearFieldResidualExecutionScale.Eighth
                ? SimpleDdgiNearFieldResidualExecutionScale.Eighth
                : SimpleDdgiNearFieldResidualExecutionScale.Quarter;
        ActiveScale = startingScale ?? defaultStartingScale;
        if (!Enum.IsDefined(ActiveScale) || ActiveScale > _maximumScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startingScale),
                "The initial C5 tier must be defined and no larger than the admitted tier.");
        }
    }

    public SimpleDdgiNearFieldResidualExecutionScale ActiveScale { get; private set; }

    public SimpleDdgiNearFieldResidualExecutionScale MaximumScale => _maximumScale;

    public uint Revision => _revision;

    public ulong LastP95Microseconds { get; private set; }

    public ulong AuthoritativeTimingSampleCount =>
        _authoritativeTimingSampleCount;

    public int WindowSampleCount => _sampleCount;

    public int PromotionWindowStreak => _promotionWindows;

    public uint PromotionCount => _promotionCount;

    public uint DemotionCount => _demotionCount;

    public int LowestTierOverBudgetEvaluationStreak =>
        _lowestTierOverBudgetEvaluations;

    public int SuspendedFramesRemaining => _suspendedFramesRemaining;

    public uint SuspensionCount => _suspensionCount;

    public bool PromotionEnabled => _promotionEnabled;

    public bool IsSuspended => _suspendedFramesRemaining > 0;

    /// <summary>
    /// Advances the fixed retry interval once per renderer frame. Returning
    /// false asks the renderer to run canonical DDGI+B3 without recording any
    /// C5 work or retaining a stale timing sample.
    /// </summary>
    public bool AdvanceFrame()
    {
        if (_suspendedFramesRemaining <= 0)
            return true;

        _suspendedFramesRemaining--;
        if (_suspendedFramesRemaining == 0)
            ResetTimingWindow();
        return false;
    }

    public bool ObserveAuthoritativeGpuTime(ulong totalMicroseconds)
    {
        if (!_enabled || IsSuspended)
            return false;

        if (_authoritativeTimingSampleCount != ulong.MaxValue)
            _authoritativeTimingSampleCount++;
        _samples[_nextSample] = totalMicroseconds;
        _nextSample = (_nextSample + 1) % SampleWindowSize;
        _sampleCount = Math.Min(_sampleCount + 1, SampleWindowSize);
        _samplesSinceEvaluation++;
        if (_sampleCount < SampleWindowSize ||
            _samplesSinceEvaluation < EvaluationCadence)
        {
            return false;
        }

        _samplesSinceEvaluation = 0;
        Array.Copy(_samples, _sortedSamples, SampleWindowSize);
        Array.Sort(_sortedSamples);
        int percentileIndex = checked(
            (int)Math.Ceiling(SampleWindowSize * 0.95) - 1);
        LastP95Microseconds = _sortedSamples[percentileIndex];

        if (LastP95Microseconds > ProductionP95BudgetMicroseconds)
        {
            _promotionWindows = 0;
            if (TryDemote())
            {
                _lowestTierOverBudgetEvaluations = 0;
                return true;
            }

            _lowestTierOverBudgetEvaluations++;
            if (_lowestTierOverBudgetEvaluations >=
                LowestTierOverBudgetEvaluationCount)
            {
                Suspend();
                return true;
            }
            return false;
        }

        _lowestTierOverBudgetEvaluations = 0;

        if (LastP95Microseconds <= PromotionP95HeadroomMicroseconds &&
            _promotionEnabled && ActiveScale < _maximumScale)
        {
            _promotionWindows++;
            if (_promotionWindows >= PromotionWindowCount)
            {
                _promotionWindows = 0;
                return TryPromote();
            }
        }
        else
        {
            _promotionWindows = 0;
        }

        return false;
    }

    public SimpleDdgiNearFieldResidualExecutionExtent CreateExtent(
        int sourceWidth,
        int sourceHeight)
    {
        if (sourceWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceHeight));

        float scale = ActiveScale switch
        {
            SimpleDdgiNearFieldResidualExecutionScale.Half => 0.5F,
            SimpleDdgiNearFieldResidualExecutionScale.Quarter => 0.25F,
            _ => 0.125F
        };
        return new SimpleDdgiNearFieldResidualExecutionExtent(
            Math.Max(1, checked((int)Math.Ceiling(sourceWidth * scale))),
            Math.Max(1, checked((int)Math.Ceiling(sourceHeight * scale))),
            ActiveScale,
            _revision);
    }

    private bool TryDemote()
    {
        if (ActiveScale == SimpleDdgiNearFieldResidualExecutionScale.Eighth)
            return false;
        ActiveScale--;
        if (_demotionCount != uint.MaxValue)
            _demotionCount++;
        AdvanceRevision();
        return true;
    }

    private bool TryPromote()
    {
        if (ActiveScale >= _maximumScale)
            return false;
        ActiveScale++;
        if (_promotionCount != uint.MaxValue)
            _promotionCount++;
        AdvanceRevision();
        return true;
    }

    private void AdvanceRevision()
    {
        _revision = unchecked(_revision + 1U);
        if (_revision == 0U)
            _revision = 1U;
        // Measurements from a different dispatch extent are not evidence for
        // the new extent. Require a complete fresh P95 window before another
        // promotion or demotion decision.
        ResetTimingWindow();
    }

    private void Suspend()
    {
        _suspendedFramesRemaining = SuspensionFrameCount;
        _lowestTierOverBudgetEvaluations = 0;
        if (_suspensionCount != uint.MaxValue)
            _suspensionCount++;
        AdvanceRevision();
    }

    private void ResetTimingWindow()
    {
        _sampleCount = 0;
        _nextSample = 0;
        _samplesSinceEvaluation = 0;
        _promotionWindows = 0;
        Array.Clear(_samples);
        Array.Clear(_sortedSamples);
    }
}
