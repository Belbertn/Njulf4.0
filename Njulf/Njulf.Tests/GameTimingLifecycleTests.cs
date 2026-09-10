using Microsoft.Extensions.DependencyInjection;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Input;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class GameTimingLifecycleTests
{
    [Test]
    public void HostKeepsInputResponsiveAndGpuParticlesFrozenUntilResume()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires a Vulkan window.");
        using var game = new TimingGame { WindowWidth = 96, WindowHeight = 64 };
        game.Run();
        Assert.That(game.Completed, Is.True);
    }

    private sealed class Counter : IUpdateable
    {
        public bool Enabled { get; set; } = true;
        public int UpdateOrder { get; set; }
        public int Calls;
        public void Update(float deltaTime) => Calls++;
    }

    private sealed class TimingGame : Game
    {
        private VulkanRenderer _native = null!;
        private VulkanContext _context = null!;
        private BufferManager _buffers = null!;
        private readonly Counter _counter = new();
        private GPUParticleState[][] _frozen = [];
        private uint[] _frozenSpawnCounts = [];
        private bool _seenVariableUpdate;
        private int _qualityFrames, _inputUpdates, _lastInputUpdate, _fixedThisTick, _frozenSceneCalls;
        internal bool Completed;

        public TimingGame()
        {
            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1);
            MaxCatchUpSteps = 3;
            IsPaused = true;
        }

        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };

        protected override void ConfigureServices(IServiceCollection services)
        {
            var (input, proxy) = InputDeviceProxy.Create<IInputManager>();
            proxy.Methods[nameof(IInputManager.Update)] = _ => { _inputUpdates++; return null; };
            services.AddSingleton(input);
        }

        protected override void ConfigureRendererBeforeInitialize(IRenderer renderer)
        {
            _native = (VulkanRenderer)renderer;
            _native.Settings.Particles.SimulationMode = ParticleSimulationMode.Gpu;
            _native.Settings.Particles.MaxParticles = 64;
            _native.Settings.Particles.MaxEmitters = 1;
            _native.Settings.Particles.FixedSimulationDeltaSeconds = 1f / 60;
            _native.Settings.AntiAliasing.Mode = AntiAliasingMode.None;
            _native.Settings.AutoExposure.Enabled = false;
        }

        protected override void Initialize()
        {
            Window.IsVisible = false;
            _context = Services.GetRequiredService<VulkanContext>();
            _buffers = Services.GetRequiredService<BufferManager>();
        }

        protected override void Load()
        {
            Scene.Add(_counter);
            Scene.Add(new ParticleEffectInstance(new ParticleEffect
            {
                Name = "Pause regression",
                Emitters = [new ParticleEmitterDefinition
                {
                    Name = "Emitter", SpawnRatePerSecond = 600, MaxParticles = 64,
                    LifetimeSeconds = ParticleCurve.Constant(100), Size = ParticleCurve.Constant(.1f),
                    InitialVelocityMin = new(.1f, 0, 0), InitialVelocityMax = new(.1f, 0, 0),
                    ColorOverLife = ParticleGradient.Constant(Color.White),
                    Material = new ParticleMaterialDefinition { Name = "White" }
                }]
            }) { RandomSeed = 42 });
        }

        protected override void Update(GameTime time)
        {
            if (time.UnscaledTotalGameTime > TimeSpan.FromMinutes(2)) throw new TimeoutException("Timing fixture did not complete.");
            if (_lastInputUpdate != 0) Assert.That(_inputUpdates, Is.EqualTo(_lastInputUpdate + 1));
            _lastInputUpdate = _inputUpdates;
            _fixedThisTick = 0;
            int before = _counter.Calls;
            base.Update(time);
            bool variable = !IsFixedTimeStep && !IsSimulationPaused;
            Assert.That(_counter.Calls, Is.EqualTo(before + (variable ? 1 : 0)), "The scene must advance in exactly one simulation mode.");
            _seenVariableUpdate |= variable;
            if (IsSimulationPaused) Assert.That(time.ElapsedGameTime, Is.EqualTo(TimeSpan.Zero));
        }

        protected override void FixedUpdate(GameTime time)
        {
            Assert.That(IsSimulationPaused, Is.False);
            Assert.That(_inputUpdates, Is.EqualTo(_lastInputUpdate));
            Assert.That(++_fixedThisTick, Is.LessThanOrEqualTo(MaxCatchUpSteps));
            Assert.That(time.ElapsedGameTime, Is.EqualTo(TargetElapsedTime));
            int before = _counter.Calls;
            base.FixedUpdate(time);
            Assert.That(_counter.Calls, Is.EqualTo(before + 1));
        }

        protected override void OnFramePresented()
        {
            if (!_native.StartupSnapshot.IsFullQuality) return;
            _qualityFrames++;
            if (_qualityFrames == 1)
            {
                Assert.That(ReadStates().SelectMany(s => s), Is.Empty, "Starting paused must not spawn particles.");
                Assert.That(_counter.Calls, Is.Zero);
                IsPaused = false;
            }
            if (_qualityFrames == 8) { IsPaused = true; _frozenSceneCalls = _counter.Calls; }
            if (_qualityFrames == 12)
            {
                _frozen = ReadStates();
                _frozenSpawnCounts = Enumerable.Range(0, RenderingConstants.FramesInFlight)
                    .Select(i => Read<GPUParticleCounters>(_native.GetGpuParticleBuffers(i).CounterBuffer).Single().SpawnedCount).ToArray();
                Assert.That(_frozen.SelectMany(s => s), Is.Not.Empty);
                Assert.That(_counter.Calls, Is.EqualTo(_frozenSceneCalls));
            }
            if (_qualityFrames == 16)
            {
                var still = ReadStates();
                for (int i = 0; i < still.Length; ++i)
                {
                    Assert.That(still[i], Is.EqualTo(_frozen[i]), "Paused state must remain identical across presented frames.");
                    var counters = Read<GPUParticleCounters>(_native.GetGpuParticleBuffers(i).CounterBuffer).Single();
                    Assert.That(counters.SpawnedCount, Is.EqualTo(_frozenSpawnCounts[i]), "Spawn counts are cumulative and must not advance while paused.");
                    Assert.That(counters.RenderedCount, Is.GreaterThan(0), "Frozen particles must still produce render instances.");
                }
                Assert.That(_counter.Calls, Is.EqualTo(_frozenSceneCalls));
                TimeScale = .5;
                IsPaused = false;
            }
            if (_qualityFrames == 20)
            {
                var resumed = ReadStates();
                Assert.That(resumed.SelectMany(s => s).Max(s => s.PositionAge.W),
                    Is.GreaterThan(_frozen.SelectMany(s => s).Max(s => s.PositionAge.W)));
                Assert.That(_counter.Calls, Is.GreaterThan(_frozenSceneCalls));
                IsFixedTimeStep = false;
            }
            if (_qualityFrames == 22)
            {
                Assert.That(_seenVariableUpdate, Is.True);
                Completed = true;
                Exit();
            }
        }

        private GPUParticleState[][] ReadStates()
        {
            _context.WaitIdle();
            return Enumerable.Range(0, RenderingConstants.FramesInFlight)
                .Select(i => Read<GPUParticleState>(_native.GetGpuParticleBuffers(i).StateBuffer)
                    .Where(s => (s.Flags & 1u) != 0).ToArray()).ToArray();
        }

        private unsafe T[] Read<T>(BufferHandle source) where T : unmanaged
        {
            ulong size = _buffers.GetBufferSize(source);
            var destination = _buffers.CreateBuffer(size, BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost, Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                "Timing regression readback");
            try
            {
                var command = _context.BeginSingleTimeCommands();
                var before = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit, DstAccessMask = AccessFlags.TransferReadBit };
                _context.Api.CmdPipelineBarrier(command.CommandBuffer, PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.TransferBit, 0, 1, &before, 0, null, 0, null);
                var copy = new BufferCopy(0, 0, size);
                _context.Api.CmdCopyBuffer(command.CommandBuffer, _buffers.GetBuffer(source), _buffers.GetBuffer(destination), 1, &copy);
                var after = new MemoryBarrier { SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
                _context.Api.CmdPipelineBarrier(command.CommandBuffer, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit, 0, 1, &after, 0, null, 0, null);
                _context.EndSingleTimeCommands(command);
                _buffers.InvalidateBuffer(destination, 0, size);
                return new ReadOnlySpan<T>(_buffers.GetMappedPointer(destination), checked((int)(size / (ulong)sizeof(T)))).ToArray();
            }
            finally { _buffers.DestroyBuffer(destination); }
        }

        protected override void Unload() => Assert.That(_native.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExitOrFailureDuringFixedCallbackStopsTheBatchAndUnloads(bool fail)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires a Vulkan window.");
        using var game = new ExitGame(fail) { WindowWidth = 64, WindowHeight = 64 };
        if (fail) Assert.That(Assert.Throws<InvalidOperationException>(game.Run), Is.SameAs(game.Failure));
        else game.Run();
        Assert.That(game.FixedCalls, Is.EqualTo(1));
        Assert.That(game.Unloads, Is.EqualTo(1));
    }

    private sealed class ExitGame(bool fail) : Game
    {
        internal int FixedCalls, Unloads;
        internal readonly Exception Failure = new InvalidOperationException("Fixed callback failure");
        protected override void Initialize()
        {
            Window.IsVisible = false;
            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromTicks(1);
        }
        protected override void FixedUpdate(GameTime time)
        {
            FixedCalls++;
            if (fail) throw Failure;
            Dispose();
        }
        protected override void Unload() => Unloads++;
    }
}
