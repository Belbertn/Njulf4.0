namespace Njulf.Core;

internal sealed class GameClock
{
    private TimeSpan _origin, _now, _simulationTotal;
    private TimeSpan? _lastUpdate, _lastDraw, _lastFixed;
    private double _scaledTicks, _updateScaled, _drawScaled, _fixedBoundary, _accumulator;
    private bool _started, _hasFixedStep, _zeroUpdate, _zeroDraw;
    private int _stepsRemaining;
    public bool IsFixedTimeStep { get; private set; }
    public TimeSpan TargetElapsedTime { get; private set; } = TimeSpan.FromSeconds(1.0 / 60);
    public int MaxCatchUpSteps { get; private set; } = 5;
    public double TimeScale { get; private set; } = 1;
    public bool IsPaused { get; private set; }
    public float InterpolationAlpha => !IsFixedTimeStep || IsPaused || !_hasFixedStep
        ? 1 : MathF.Min((float)(_accumulator / TargetElapsedTime.Ticks), MathF.BitDecrement(1f));

    public void Configure(TimeSpan now, bool fixedStep, TimeSpan interval, int maxSteps, double scale, bool paused)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSteps);
        if (!double.IsFinite(scale) || scale < 0) throw new ArgumentOutOfRangeException(nameof(scale));
        Advance(now);
        paused |= scale == 0;
        bool reset = fixedStep != IsFixedTimeStep ||
            ((fixedStep || IsFixedTimeStep) && interval != TargetElapsedTime) || paused != IsPaused;
        if (IsPaused && !paused) _zeroUpdate = _zeroDraw = true;
        IsFixedTimeStep = fixedStep;
        TargetElapsedTime = interval;
        MaxCatchUpSteps = maxSteps;
        TimeScale = scale;
        IsPaused = paused;
        if (reset)
        {
            _accumulator = 0;
            _stepsRemaining = 0;
            _hasFixedStep = false;
            _fixedBoundary = _scaledTicks;
            _updateScaled = _drawScaled = _scaledTicks;
        }
    }

    public void Start(TimeSpan now)
    {
        _started = true;
        _origin = _now = now;
        _lastUpdate = _lastDraw = _lastFixed = null;
        _simulationTotal = TimeSpan.Zero;
        _scaledTicks = _updateScaled = _drawScaled = _fixedBoundary = _accumulator = 0;
        _stepsRemaining = 0;
        _hasFixedStep = false;
        _zeroUpdate = _zeroDraw = false;
    }

    public GameTime Update(TimeSpan now)
    {
        Advance(now);
        var time = Read(now, ref _lastUpdate, ref _updateScaled);
        if (_zeroUpdate) { time = time with { ElapsedGameTime = TimeSpan.Zero }; _zeroUpdate = false; }
        if (IsFixedTimeStep && !IsPaused)
        {
            _accumulator += _scaledTicks - _fixedBoundary;
            double due = System.Math.Floor(_accumulator / TargetElapsedTime.Ticks);
            _stepsRemaining = (int)System.Math.Min(due, MaxCatchUpSteps);
            // Discard excess whole steps; they never advance simulation totals.
            _accumulator %= TargetElapsedTime.Ticks;
        }
        else _stepsRemaining = 0;
        _fixedBoundary = _scaledTicks;
        return time;
    }

    public bool TryFixedUpdate(TimeSpan now, out GameTime time)
    {
        time = default;
        if (!IsFixedTimeStep || IsPaused || _stepsRemaining <= 0) return false;
        --_stepsRemaining;
        _simulationTotal += TargetElapsedTime;
        time = new GameTime(_simulationTotal, TargetElapsedTime)
        {
            UnscaledTotalGameTime = now - _origin,
            UnscaledElapsedGameTime = _lastFixed.HasValue ? now - _lastFixed.Value : TimeSpan.Zero
        };
        _lastFixed = now;
        _hasFixedStep = true;
        return true;
    }

    public GameTime Draw(TimeSpan now)
    {
        Advance(now);
        var time = Read(now, ref _lastDraw, ref _drawScaled);
        if (_zeroDraw) { time = time with { ElapsedGameTime = TimeSpan.Zero }; _zeroDraw = false; }
        return time;
    }

    private void Advance(TimeSpan now)
    {
        if (!_started) return;
        if (now < _now) throw new ArgumentOutOfRangeException(nameof(now), "Clock timestamps must be monotonic.");
        if (!IsPaused) _scaledTicks += (now - _now).Ticks * TimeScale;
        _now = now;
    }

    private GameTime Read(TimeSpan now, ref TimeSpan? previous, ref double previousScaled)
    {
        var result = new GameTime(TimeSpan.FromTicks(checked((long)_scaledTicks)),
            previous.HasValue ? TimeSpan.FromTicks(checked((long)(_scaledTicks - previousScaled))) : TimeSpan.Zero)
        {
            UnscaledTotalGameTime = now - _origin,
            UnscaledElapsedGameTime = previous.HasValue ? now - previous.Value : TimeSpan.Zero
        };
        previous = now;
        previousScaled = _scaledTicks;
        return result;
    }
}
