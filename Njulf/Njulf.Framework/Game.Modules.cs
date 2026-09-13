using Njulf.Core;

namespace Njulf.Framework;

public abstract partial class Game
{
    private readonly HashSet<IGameModule> _moduleRegistrations = new(ReferenceEqualityComparer.Instance);
    private GameModules? _modules;
    private GameLevels? _levels;
    private Func<bool>? _canRunModules, _canStepModules;

    /// <summary>Borrowed managed level, or null when using the initial/manual scene or after unloading.</summary>
    public GameLevel? CurrentLevel => _levels?.Active;

    /// <summary>Registers an optional module once and transfers ownership. Call on the game thread during initialization/loading.</summary>
    /// <remarks>Module callbacks run after gameplay even when overrides omit base. Shutdown disposes modules after Unload.</remarks>
    public T RegisterModule<T>(T module) where T : IGameModule
    {
        EnsureTimingThread();
        if (_isShuttingDown || _contentLoaded || _isUpdatingFrame || _isRenderingFrame)
            throw new InvalidOperationException("Register host modules during initialization or initial loading.");
        return (_modules ??= new(_moduleRegistrations)).Register(module, true, IsSimulationPaused, IsFixedTimeStep);
    }

    /// <summary>Loads a candidate while the current level runs, then replaces scene/modules together between callbacks.</summary>
    /// <remarks>Call on the game thread after services exist; ordinary awaits stay on that thread.
    /// Await all work in the loader. Failed/cancelled loading preserves the current level; overlapping transitions are rejected.
    /// The returned task completes after old-level cleanup. Cleanup failure after commit leaves the new level active.</remarks>
    public Task LoadLevelAsync(Func<GameLevel, CancellationToken, Task> loader, CancellationToken cancellationToken = default)
    {
        EnsureTimingThread();
        if (_isShuttingDown) throw new InvalidOperationException("The game is shutting down.");
        _ = Content;
        return Levels.LoadAsync(loader, cancellationToken);
    }

    /// <summary>Cancels/drains pending level loading, detaches the current scene, and releases local resources on the game thread.</summary>
    /// <remarks>Installs an empty scene. Root content and host-owned audio/music survive. Await this task; do not block the game thread.</remarks>
    public Task UnloadLevelAsync()
    {
        EnsureTimingThread();
        return Levels.UnloadAsync();
    }

    private GameLevels Levels => _levels ??= new(() => Content.CreateScope(), next =>
    {
        var previous = _scene;
        _scene = next;
        return previous;
    }, () => IsFixedTimeStep, () => IsSimulationPaused, LevelBoundaryAsync, _moduleRegistrations, _contentCancellation.Token);

    private async Task LevelBoundaryAsync(CancellationToken cancellationToken)
    {
        if (_isUpdatingFrame || _isRenderingFrame) await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool CanRunModules() => _isRunning;
    private bool CanStepModules() => _isRunning && IsFixedTimeStep && !IsSimulationPaused;

    private void StepModules(GameTime time)
    {
        _modules?.FixedUpdate(time, _canStepModules ??= CanStepModules);
        _levels?.Active?.Modules.FixedUpdate(time, _canStepModules ??= CanStepModules);
    }

    private void UpdateModules(GameTime time)
    {
        for (var phase = GameModulePhase.Physics; phase <= GameModulePhase.AudioMaintenance; phase++)
        {
            if (phase == GameModulePhase.AudioSpatial && _isRunning)
                _cameraController?.Update(Camera, time);
            var frame = new GameModuleFrame(time, IsSimulationPaused, Camera.Position, Camera.Forward, Camera.Up)
            { InterpolationAlpha = InterpolationAlpha };
            _modules?.Update(phase, frame, _canRunModules ??= CanRunModules);
            _levels?.Active?.Modules.Update(phase, frame, _canRunModules ??= CanRunModules);
        }
    }
}
