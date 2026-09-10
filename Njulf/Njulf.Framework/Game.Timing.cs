namespace Njulf.Core;

public abstract partial class Game
{
    private int _timingThreadId;
    private bool _isPaused, _pauseWhenInactive;

    /// <summary>Enables FixedUpdate simulation; false by default. Update continues once per host tick.</summary>
    public bool IsFixedTimeStep
    {
        get => _gameClock.IsFixedTimeStep;
        set { EnsureTimingThread(); ConfigureClock(fixedStep: value); }
    }
    /// <summary>Positive fixed simulation interval; defaults to 1/60 second. Changing it clears catch-up backlog.</summary>
    public TimeSpan TargetElapsedTime
    {
        get => _gameClock.TargetElapsedTime;
        set { EnsureTimingThread(); ConfigureClock(interval: value); }
    }
    /// <summary>Maximum fixed callbacks per host tick, default five. Excess whole steps are discarded.</summary>
    public int MaxCatchUpSteps
    {
        get => _gameClock.MaxCatchUpSteps;
        set { EnsureTimingThread(); ConfigureClock(maxSteps: value); }
    }
    /// <summary>Finite nonnegative multiplier, default one. Zero pauses simulation; fixed step size is unchanged.</summary>
    public double TimeScale
    {
        get => _gameClock.TimeScale;
        set { EnsureTimingThread(); ConfigureClock(scale: value); }
    }
    /// <summary>Explicit application pause. Input, Update, Draw and content pumping continue.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set { EnsureTimingThread(); _isPaused = value; ConfigureClock(); }
    }
    /// <summary>Automatically pauses while unfocused; false by default. Does not change IsPaused.</summary>
    public bool PauseWhenInactive
    {
        get => _pauseWhenInactive;
        set { EnsureTimingThread(); _pauseWhenInactive = value; ConfigureClock(); }
    }
    /// <summary>Whether the native window is focused.</summary>
    public bool IsActive => _windowFocused;
    /// <summary>Effective pause from explicit pause, zero scale or the focus policy.</summary>
    public bool IsSimulationPaused => _gameClock.IsPaused;
    /// <summary>Previous/current fixed-state interpolation fraction. One when paused, variable-step or awaiting a first step after reset.</summary>
    public float InterpolationAlpha => _gameClock.InterpolationAlpha;

    private void EnsureTimingThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runStarted && Environment.CurrentManagedThreadId != _timingThreadId)
            throw new InvalidOperationException("Timing controls require the game thread.");
    }

    private void ConfigureClock(bool? fixedStep = null, TimeSpan? interval = null, int? maxSteps = null, double? scale = null)
        => _gameClock.Configure(TimeSpan.FromMicroseconds(RunElapsedMicroseconds),
            fixedStep ?? IsFixedTimeStep, interval ?? TargetElapsedTime, maxSteps ?? MaxCatchUpSteps,
            scale ?? TimeScale, _isPaused || (_pauseWhenInactive && !_windowFocused));
}
