using Njulf.Assets;
using Njulf.Assets.Scenes;

namespace Njulf.Core;

/// <summary>Owns one scene, its content scope, and optional local modules. Created by Game.LoadLevelAsync.</summary>
/// <remarks>Populate on the game thread and await all loading work before returning from the loader.
/// Register physics after loading the scene. Borrowed references are valid until replacement or unloading.</remarks>
public sealed class GameLevel : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _sealed, _disposed, _contentOwnsScene;
    internal bool IsActive { get; set; }
    internal GameModules Modules { get; }

    /// <summary>The level-owned scene; initially empty. Populate directly or use LoadSceneAsync.</summary>
    public Scene.Scene Scene { get; private set; } = new();
    /// <summary>Borrowed independent content lifetime. Root content remains separate.</summary>
    public IContentScope Content { get; }

    internal GameLevel(IContentScope content, HashSet<IGameModule> registrations)
    {
        Content = content;
        Modules = new(registrations);
    }

    /// <summary>Transfers ownership of a local module. It becomes active only when this level is committed.</summary>
    public T RegisterModule<T>(T module) where T : IGameModule
    {
        CheckPreparing();
        return Modules.Register(module, active: false, paused: true, fixedStep: true);
    }

    /// <summary>Replaces the initial scene with a scope-owned scene document, before registering modules.</summary>
    public async Task LoadSceneAsync(string path, SceneLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckPreparing();
        if (!Modules.IsEmpty) throw new InvalidOperationException("Load the scene before registering local modules.");
        var next = await Content.LoadSceneAsync(path, options, cancellationToken);
        CheckPreparing();
        if (!Modules.IsEmpty) throw new InvalidOperationException("Modules were registered while the scene was loading.");
        ReleaseScene();
        Scene = next;
        _contentOwnsScene = true;
    }

    internal void Seal() { CheckPreparing(); _sealed = true; }
    private void CheckPreparing()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) throw new InvalidOperationException("The level loader has already completed.");
    }
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Level operations require the game thread.");
    }
    private void ReleaseScene()
    {
        if (_contentOwnsScene) Content.Unload(Scene);
        else Scene.Dispose();
    }

    /// <summary>Disposes a detached level, modules first and content last. Active levels must be unloaded through Game.</summary>
    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        if (IsActive) throw new InvalidOperationException("Unload the active level through Game.UnloadLevelAsync.");
        _disposed = true;
        List<Exception> failures = [];
        try { Modules.Dispose(); } catch (Exception e) { failures.Add(e); }
        try { ReleaseScene(); } catch (Exception e) { failures.Add(e); }
        try { Content.Dispose(); } catch (Exception e) { failures.Add(e); }
        if (failures.Count != 0) throw new AggregateException("Level cleanup failed.", failures);
    }
}
