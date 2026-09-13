using Microsoft.Extensions.DependencyInjection;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Framework;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class GameModuleLifecycleTests
{
    [TestCase(false), TestCase(true)]
    public void NativeHostOrdersModulesAndReplacesRecordedLevelBetweenCallbacks(bool shutdownDuringLoad)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires a Vulkan window.");
        using var game = new LevelGame(shutdownDuringLoad) { WindowWidth = 64, WindowHeight = 64 };
        game.Run();
        Assert.That(game.Completed, Is.True);
        Assert.That(game.ModuleDisposals, Is.EqualTo(3));
    }

    private sealed class Probe(GameModulePhase phase, LevelGame game) : IGameModule
    {
        public GameModulePhase Phase => phase;
        public void FixedUpdate(GameTime time) => game.Events.Add("physics");
        public void Update(GameModuleFrame frame)
        {
            if (Phase == GameModulePhase.Physics) return;
            if (Phase == GameModulePhase.AudioSpatial)
            {
                Assert.That(game.Events[0], Is.EqualTo("gameplay"));
                for (int i = 1; i < game.Events.Count; i += 2)
                    Assert.That(game.Events.Skip(i).Take(2), Is.EqualTo(new[] { "fixed", "physics" }));
                if (frame.IsSimulationPaused)
                {
                    Assert.That(game.Events, Has.Count.EqualTo(1));
                    game.SawPause = true;
                }
                game.Events.Add("spatial");
            }
            else Assert.That(game.Events[^1], Is.EqualTo("spatial"));
        }
        public void Dispose()
        {
            Assert.That(game.UserUnloaded, Is.True);
            game.ModuleDisposals++;
        }
    }

    private sealed class OwnedEntity : IUpdateable, IDisposable
    {
        public bool Enabled { get; set; } = true;
        public int UpdateOrder { get; set; }
        public int Disposals;
        public void Update(float deltaTime) { }
        public void Dispose() => Disposals++;
    }

    private sealed class LevelGame : Game
    {
        internal readonly List<string> Events = [];
        internal bool Completed, SawPause, UserUnloaded;
        internal int ModuleDisposals;
        private readonly OwnedEntity _first = new(), _second = new();
        private Task? _replacement, _unloading;
        private int _fixed;
        private readonly bool _shutdownDuringLoad;
        public LevelGame(bool shutdownDuringLoad)
        {
            _shutdownDuringLoad = shutdownDuringLoad;
            IsFixedTimeStep = true; IsPaused = true; TargetElapsedTime = TimeSpan.FromMilliseconds(1);
        }
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void Initialize()
        {
            Window.IsVisible = false;
            // Deliberately reverse registration order: phases still govern execution.
            RegisterModule(new Probe(GameModulePhase.AudioMaintenance, this));
            RegisterModule(new Probe(GameModulePhase.AudioSpatial, this));
            RegisterModule(new Probe(GameModulePhase.Physics, this));
        }
        private Task Populate(GameLevel level, OwnedEntity entity)
        {
            level.Scene.Add(entity);
            using var mesh = GraphicsDevice.CreateMesh([new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0)], [0u, 1u, 2u]);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default);
            level.Scene.Add(GraphicsDevice.CreateRenderObject(mesh, material));
            return Task.CompletedTask;
        }
        protected override Task LoadAsync(CancellationToken token) => LoadLevelAsync((level, _) => Populate(level, _first), token);
        protected override void Update(GameTime time)
        {
            if (time.UnscaledTotalGameTime > TimeSpan.FromSeconds(30)) throw new TimeoutException("Level lifecycle did not complete.");
            Events.Clear(); Events.Add("gameplay"); // Deliberately omit base: modules must still run.
            if (SawPause) IsPaused = false;
            if (_replacement?.IsCompleted == true && _unloading == null)
            {
                _replacement.GetAwaiter().GetResult();
                Assert.That(_first.Disposals, Is.EqualTo(1));
                Assert.Throws<InvalidOperationException>(() => ExchangeScene(Scene));
                _unloading ??= UnloadLevelAsync();
                if (!_unloading.IsCompleted) Assert.That(CurrentLevel, Is.Not.Null);
            }
            if (_unloading?.IsCompleted == true)
            {
                _unloading.GetAwaiter().GetResult();
                Assert.That(CurrentLevel, Is.Null);
                Assert.That(_second.Disposals, Is.EqualTo(1));
                Completed = true;
                Exit();
            }
        }
        protected override void FixedUpdate(GameTime time) { Events.Add("fixed"); _fixed++; }
        protected override void Draw(GameTime time)
        {
            base.Draw(time);
            if (_replacement != null || _fixed == 0) return;
            var previous = CurrentLevel;
            _replacement = LoadLevelAsync(async (level, token) =>
            {
                await Populate(level, _second);
                if (_shutdownDuringLoad) await Task.Delay(Timeout.Infinite, token);
            });
            Assert.That(CurrentLevel, Is.SameAs(previous));
            Assert.That(_first.Disposals, Is.Zero, "Do not release geometry still being recorded.");
            if (_shutdownDuringLoad) Exit();
        }
        protected override void Unload()
        {
            UserUnloaded = true;
            if (_shutdownDuringLoad)
            {
                Assert.That(_replacement!.IsCanceled, Is.True);
                Assert.That(_second.Disposals, Is.EqualTo(1));
                Assert.That(_first.Disposals, Is.Zero, "Active level remains available through user Unload.");
                Completed = true;
            }
            Assert.That(Services.GetRequiredService<VulkanRenderer>().ValidationMessageSnapshot.ErrorCount, Is.Zero);
        }
    }
}
