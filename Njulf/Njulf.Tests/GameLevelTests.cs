using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Framework;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GameLevelTests
{
    private sealed class EmptyGame : Game;
    private sealed class Module(List<string> events, string name, GameModulePhase phase, bool fail = false) : IGameModule
    {
        public GameModulePhase Phase => phase;
        public void Dispose() { events.Add(name); if (fail) throw new IOException(name); }
    }
    private sealed class Entity(List<string> events) : IUpdateable, IDisposable
    {
        public bool Enabled { get; set; } = true;
        public int UpdateOrder { get; set; }
        public void Update(float dt) { }
        public void Dispose() => events.Add("scene");
    }
    private sealed class Scope(List<string> events) : IContentScope
    {
        public void Dispose() => events.Add("content");
        public T Load<T>(string path) => throw new NotSupportedException();
        public T Load<T>(string path, ContentLoadOptions options) => throw new NotSupportedException();
        public Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContentPreloadResult<T>> PreloadAsync<T>(IEnumerable<ContentPreloadRequest> requests, ContentPreloadOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Unload<T>(T asset) => throw new NotSupportedException();
        public void UnloadAll() => throw new NotSupportedException();
    }

    [Test]
    public void RegistrationEnforcesFixedPhysicsAndDisposesRemainingModulesAfterFailure()
    {
        var events = new List<string>();
        using var game = new EmptyGame();
        using var simulation = new PhysicsHostModule(new PhysicsScene(PhysicsMode.Simulation));
        Assert.Throws<InvalidOperationException>(() => game.RegisterModule(simulation));
        game.IsFixedTimeStep = true;
        game.RegisterModule(simulation);
        Assert.Throws<InvalidOperationException>(() => game.RegisterModule(simulation));
        Assert.Throws<InvalidOperationException>(() => game.IsFixedTimeStep = false);
        game.RegisterModule(new Module(events, "audio", GameModulePhase.AudioSpatial, fail: true));
        game.RegisterModule(new Module(events, "device", GameModulePhase.AudioMaintenance));
        Assert.Throws<AggregateException>(game.Dispose);
        game.Dispose();
        Assert.That(events, Is.EqualTo(new[] { "audio", "device" }));

        using var queryGame = new EmptyGame();
        queryGame.RegisterModule(new PhysicsHostModule(new PhysicsScene(PhysicsMode.QueryOnly)));
        queryGame.IsFixedTimeStep = false;
    }

    [Test]
    public void ReplacementWaitsForBoundaryAndDetachesBeforeOrderedDisposal()
    {
        WithContext(context =>
        {
            List<string> events = [];
            var scene = new Scene();
            Task boundary = Task.CompletedTask;
            var levels = new GameLevels(() => new Scope(events), next => { var old = scene; scene = next; return old; },
                () => true, () => false, _ => boundary, new(ReferenceEqualityComparer.Instance), default);
            levels.LoadAsync((level, _) =>
            {
                level.Scene.Add(new Entity(events));
                level.RegisterModule(new Module(events, "physics", GameModulePhase.Physics));
                level.RegisterModule(new Module(events, "audio", GameModulePhase.AudioSpatial));
                return Task.CompletedTask;
            }, default).GetAwaiter().GetResult();
            var old = levels.Active!;
            Assert.Throws<InvalidOperationException>(old.Dispose);
            var ready = new TaskCompletionSource();
            boundary = ready.Task;
            Task load = levels.LoadAsync((_, _) => Task.CompletedTask, default);
            Assert.That(levels.Active, Is.SameAs(old));
            Assert.That(events, Is.Empty);
            Assert.Throws<InvalidOperationException>(() => levels.LoadAsync((_, _) => Task.CompletedTask, default));
            ready.SetResult();
            Pump(load, context);
            Assert.That(scene, Is.SameAs(levels.Active!.Scene).And.Not.SameAs(old.Scene));
            Assert.That(events, Is.EqualTo(new[] { "audio", "physics", "scene", "content" }));
            old.Dispose();
            events.Clear();
            levels.UnloadAsync().GetAwaiter().GetResult();
            Assert.That(levels.Active, Is.Null);
            Assert.That(events, Is.EqualTo(new[] { "content" }));
            scene.Dispose();
        });
    }

    [TestCase("failure"), TestCase("cancel"), TestCase("unload"), TestCase("shutdown"), TestCase("fixed-mode")]
    public void PartialCandidatesAreReleasedAndCannotCommitAfterCancellation(string outcome)
    {
        WithContext(context =>
        {
            List<string> events = [];
            var scene = new Scene();
            using var shutdown = new CancellationTokenSource();
            using var cancellation = new CancellationTokenSource();
            var levels = new GameLevels(() => new Scope(events), next => { var old = scene; scene = next; return old; },
                () => false, () => false, _ => Task.CompletedTask, new(ReferenceEqualityComparer.Instance), shutdown.Token);
            levels.LoadAsync((_, _) => Task.CompletedTask, default).GetAwaiter().GetResult();
            var old = levels.Active;
            Task load = levels.LoadAsync(async (level, token) =>
            {
                level.Scene.Add(new Entity(events));
                level.RegisterModule(new Module(events, "candidate", GameModulePhase.AudioSpatial));
                if (outcome == "failure") throw new IOException("Loading failed");
                if (outcome == "fixed-mode")
                {
                    level.RegisterModule(new PhysicsHostModule(new PhysicsScene(PhysicsMode.Simulation, level.Scene)));
                    return;
                }
                await Task.Delay(Timeout.Infinite, token);
            }, cancellation.Token);
            Task? unload = null;
            if (outcome == "cancel") cancellation.Cancel();
            if (outcome == "shutdown") shutdown.Cancel();
            if (outcome == "unload") unload = levels.UnloadAsync();
            Assert.That(() => Pump(load, context), Throws.Exception);
            if (unload != null) Pump(unload, context);
            Assert.That(levels.Active, outcome == "unload" ? Is.Null : Is.SameAs(old));
            Assert.That(events.Take(3), Is.EqualTo(new[] { "candidate", "scene", "content" }));
            levels.DisposeActive();
            scene.Dispose();
        });
    }

    private static void WithContext(Action<GameSynchronizationContext> test)
    {
        var previous = SynchronizationContext.Current;
        var context = new GameSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try { test(context); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Pump(Task task, GameSynchronizationContext context)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < limit) { context.Pump(); Thread.Yield(); }
        Assert.That(task.IsCompleted, Is.True, "Level operation did not settle.");
        task.GetAwaiter().GetResult();
    }
}
