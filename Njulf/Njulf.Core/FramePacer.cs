using System.Diagnostics;
using System.Threading;

namespace Njulf.Core;

/// <summary>
/// Applies host-side frame pacing without attributing intentional idle time to
/// renderer work. Deadlines remain phase-locked during ordinary frames and are
/// rebased after a long stall so the host never emits catch-up bursts.
/// </summary>
internal sealed class FramePacer
{
    internal const double DefaultMaximumFramesPerSecond = 60.0;
    internal const double MaximumSupportedFramesPerSecond = 1000.0;

    private const double CoarseSleepGuardMilliseconds = 0.75;

    private readonly Func<long> _getTimestamp;
    private readonly Action<int> _sleep;
    private readonly Action _yieldThread;
    private readonly long _frequency;

    private bool _scheduled;
    private double _scheduledMaximumFramesPerSecond;
    private long _nextFrameTimestamp;

    public FramePacer()
        : this(
            Stopwatch.GetTimestamp,
            Stopwatch.Frequency,
            Thread.Sleep,
            static () => Thread.Yield())
    {
    }

    internal FramePacer(
        Func<long> getTimestamp,
        long frequency,
        Action<int> sleep,
        Action yieldThread)
    {
        _getTimestamp = getTimestamp ??
            throw new ArgumentNullException(nameof(getTimestamp));
        _sleep = sleep ?? throw new ArgumentNullException(nameof(sleep));
        _yieldThread = yieldThread ??
            throw new ArgumentNullException(nameof(yieldThread));
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        _frequency = frequency;
    }

    /// <summary>
    /// Waits until the next configured frame boundary and returns the actual
    /// host-side wait duration in microseconds. Zero disables pacing.
    /// </summary>
    public long Wait(double maximumFramesPerSecond)
    {
        ValidateMaximumFramesPerSecond(maximumFramesPerSecond);
        if (maximumFramesPerSecond == 0.0)
        {
            Reset();
            return 0L;
        }

        long now = _getTimestamp();
        long period = System.Math.Max(
            1L,
            checked((long)System.Math.Round(
                _frequency / maximumFramesPerSecond,
                MidpointRounding.AwayFromZero)));
        if (!_scheduled ||
            maximumFramesPerSecond != _scheduledMaximumFramesPerSecond)
        {
            _scheduled = true;
            _scheduledMaximumFramesPerSecond = maximumFramesPerSecond;
            _nextFrameTimestamp = checked(now + period);
            return 0L;
        }

        long deadline = _nextFrameTimestamp;
        long waitStarted = now;
        WaitUntil(deadline, ref now);

        long lateness = now - deadline;
        _nextFrameTimestamp = lateness >= period
            ? checked(now + period)
            : checked(deadline + period);

        return now <= waitStarted
            ? 0L
            : checked((long)System.Math.Round(
                (now - waitStarted) * 1_000_000.0 / _frequency,
                MidpointRounding.AwayFromZero));
    }

    public void Reset()
    {
        _scheduled = false;
        _scheduledMaximumFramesPerSecond = 0.0;
        _nextFrameTimestamp = 0L;
    }

    internal static void ValidateMaximumFramesPerSecond(
        double maximumFramesPerSecond)
    {
        if (!double.IsFinite(maximumFramesPerSecond) ||
            maximumFramesPerSecond < 0.0 ||
            maximumFramesPerSecond > MaximumSupportedFramesPerSecond ||
            maximumFramesPerSecond is > 0.0 and < 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFramesPerSecond),
                maximumFramesPerSecond,
                $"The maximum frame rate must be zero (unlimited) or between 1 and {MaximumSupportedFramesPerSecond} FPS.");
        }
    }

    private void WaitUntil(long deadline, ref long now)
    {
        while (now < deadline)
        {
            long remaining = deadline - now;
            double remainingMilliseconds =
                remaining * 1000.0 / _frequency;
            int sleepMilliseconds = checked((int)System.Math.Floor(
                remainingMilliseconds - CoarseSleepGuardMilliseconds));
            if (sleepMilliseconds > 0)
                _sleep(sleepMilliseconds);
            else
                _yieldThread();

            now = _getTimestamp();
        }
    }
}
