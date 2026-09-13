using Njulf.Assets;

namespace Njulf.Core;

// The same coordinator is used by the native host and focused ownership tests.
internal sealed class GameLevels(Func<IContentScope> createScope, Func<Scene.Scene, Scene.Scene> exchange,
    Func<bool> fixedStep, Func<bool> paused, Func<CancellationToken, Task> boundary,
    HashSet<IGameModule> registrations, CancellationToken shutdown)
{
    private CancellationTokenSource? _loadingCancellation;
    private Task? _loadTask, _unloadTask;
    private bool _loading, _unloading;
    internal GameLevel? Active { get; private set; }
    internal Task? Pending => _unloading ? _unloadTask : _loading ? _loadTask : null;

    internal Task LoadAsync(Func<GameLevel, CancellationToken, Task> loader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loader);
        shutdown.ThrowIfCancellationRequested();
        if (_loading || _unloading) throw new InvalidOperationException("A level transition is already pending.");
        _loadingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        _loading = true;
        return _loadTask = LoadCoreAsync(loader, _loadingCancellation);
    }

    private async Task LoadCoreAsync(Func<GameLevel, CancellationToken, Task> loader, CancellationTokenSource cancellation)
    {
        GameLevel? candidate = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            candidate = new(createScope(), registrations);
            await loader(candidate, cancellation.Token);
            candidate.Seal();
            await boundary(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            candidate.Modules.Validate(fixedStep());
            candidate.Modules.Activate(paused());
            var previous = Active;
            var previousScene = exchange(candidate.Scene);
            Active = candidate;
            candidate.IsActive = true;
            candidate = null; // Commit: later cleanup failure must not roll back the new level.
            if (previous != null)
            {
                previous.IsActive = false;
                previous.Dispose();
            }
            else previousScene.Dispose();
        }
        catch (Exception failure)
        {
            try { candidate?.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Level loading and cleanup failed.", failure, cleanup); }
            throw;
        }
        finally
        {
            _loading = false;
            _loadingCancellation = null;
            cancellation.Dispose();
        }
    }

    internal Task UnloadAsync()
    {
        if (_unloading) return _unloadTask!;
        _unloading = true;
        return _unloadTask = UnloadCoreAsync();
    }

    private async Task UnloadCoreAsync()
    {
        try
        {
            _loadingCancellation?.Cancel();
            if (_loadTask is { IsCompleted: false })
            {
                // The load's caller owns its failure; unloading must still release the current level.
                try { await _loadTask; } catch { }
            }
            await boundary(CancellationToken.None);
            DisposeActive();
        }
        finally { _unloading = false; }
    }

    internal void DisposeActive()
    {
        var previous = Active;
        Active = null;
        var previousScene = exchange(new Scene.Scene());
        if (previous != null)
        {
            previous.IsActive = false;
            previous.Dispose();
        }
        else previousScene.Dispose();
    }
}
