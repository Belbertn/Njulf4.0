using Microsoft.Extensions.DependencyInjection;
using Njulf.Core;
using Njulf.Graphics;
using Njulf.Graphics.Vulkan;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class CustomRenderingLifecycleTests
{
    private sealed class RecreationGame : Game
    {
        internal VulkanRenderer NativeRenderer = null!;
        internal bool Resized, EnabledObserved, DisabledObserved;
        internal string ResizeReason = "";
        private int _phase;

        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void ConfigureRendererBeforeInitialize(Njulf.Core.Interfaces.IRenderer renderer) =>
            ((VulkanRenderer)renderer).Settings.Diagnostics.GpuMeshletCountersEnabled = false;
        protected override void Initialize()
        {
            Window.IsVisible = false;
            NativeRenderer = Services.GetRequiredService<VulkanRenderer>();
        }
        protected override void Load()
        {
            using var mesh = GraphicsDevice.CreateMesh(
                [new Njulf.Core.Math.Vector3(-1, -1, 0), new Njulf.Core.Math.Vector3(1, -1, 0), new Njulf.Core.Math.Vector3(0, 1, 0)],
                [0u, 1u, 2u]);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default);
            Scene.Add(GraphicsDevice.CreateRenderObject(mesh, material));
        }
        protected override void OnResize(int width, int height)
        {
            if (width == 320 && height == 240) Resized = true;
        }
        protected override void Update(GameTime time)
        {
            if (time.TotalGameTime > TimeSpan.FromMinutes(2)) throw new TimeoutException($"Pipeline recreation did not complete: phase={_phase}, resized={Resized}, window={Window.Size}, framebuffer={Window.FramebufferSize}, counters={NativeRenderer.LastDiagnostics.GpuMeshletCountersEnabled}, quality={NativeRenderer.StartupSnapshot.IsFullQuality}, reason={NativeRenderer.LastDiagnostics.LastRenderTargetRecreateReason}.");
            base.Update(time);
        }
        protected override void OnFramePresented()
        {
            if (!NativeRenderer.StartupSnapshot.IsFullQuality) return;
            if (_phase == 0)
            {
                Window.Size = new Silk.NET.Maths.Vector2D<int>(320, 240);
                NativeRenderer.Settings.Diagnostics.GpuMeshletCountersEnabled = true;
                _phase = 1;
            }
            else if (_phase == 1 && Resized && NativeRenderer.LastDiagnostics.GpuMeshletCountersEnabled == 1)
            {
                EnabledObserved = true;
                ResizeReason = NativeRenderer.LastDiagnostics.LastRenderTargetRecreateReason;
                NativeRenderer.Settings.Diagnostics.GpuMeshletCountersEnabled = false;
                _phase = 2;
            }
            else if (_phase == 2 && NativeRenderer.LastDiagnostics.GpuMeshletCountersEnabled == 0)
            {
                DisabledObserved = true;
                Assert.That(Camera.AspectRatio, Is.EqualTo(320f / 240f).Within(.001f));
                Exit();
            }
        }
    }

    [Test]
    public void WindowResizeAndDiagnosticVariants_RecreateAndPresentWithoutValidationErrors()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        using var game = new RecreationGame { WindowWidth = 256, WindowHeight = 192 };
        game.Run();
        Assert.That(game.Resized && game.EnabledObserved && game.DisabledObserved, Is.True);
        Assert.That(game.ResizeReason, Is.EqualTo("Swapchain resize"));
        Assert.That(game.NativeRenderer.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    private sealed class CallbackFailure : Exception;
    private sealed class Pass(List<string> order, string name, bool fail) : IVulkanRenderPass
    {
        internal int Disposals;
        public void Initialize(VulkanDeviceInfo device) { }
        public void ResourcesChanged(VulkanPassContext context) { }
        public void Record(VulkanPassContext context)
        {
            order.Add(name);
            Assert.That(context.GetBuffer("data").Length, Is.EqualTo(16));
            if (fail) throw new CallbackFailure();
        }
        public void Dispose() => Disposals++;
    }
    private sealed class TestGame(bool fail) : Game
    {
        internal readonly List<string> Order = [];
        internal readonly List<Pass> Passes = [];
        internal VulkanRenderer NativeRenderer = null!;
        internal GraphicsSettingsResult? SettingsResult;
        internal bool Removed;
        private readonly List<VulkanPassRegistration> _registrations = [];
        private GraphicsBuffer _buffer = null!;
        private int _frames;
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void Initialize()
        {
            Window.IsVisible = false;
            NativeRenderer = Services.GetRequiredService<VulkanRenderer>();
        }
        protected override void Load()
        {
            _buffer = GraphicsDevice.CreateBuffer(32);
            foreach (var stage in new[] { VulkanPassStage.BeforeScene, VulkanPassStage.BeforeScene, VulkanPassStage.AfterScene, VulkanPassStage.AfterPostProcessing })
            {
                string name = "Custom.Test" + Passes.Count;
                var pass = new Pass(Order, name, fail);
                var uses = new VulkanResourceUse[] { new VulkanBufferUse("data", RenderGraphResourceAccess.Read,
                    PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit, _buffer, Offset: (ulong)(Passes.Count * 4), Size: 16) };
                _registrations.Add(GraphicsDevice.AddVulkanPass(new(name, VulkanPassKind.Graphics, stage, uses), pass));
                Passes.Add(pass);
            }
            _buffer.Dispose(); // Registrations retain it, including overlapping byte ranges.
        }
        protected override async Task LoadAsync(CancellationToken cancellationToken)
        {
            // Must finish while bootstrap is running, before scene preparation can start.
            SettingsResult = await GraphicsDevice.Settings.ApplyAsync(new() { Exposure = .125f }, cancellationToken);
        }
        protected override void Update(GameTime time)
        {
            if (time.TotalGameTime > TimeSpan.FromMinutes(2)) throw new TimeoutException("Custom pass lifecycle did not advance.");
            if (!Removed && Order.Count >= 8)
            {
                foreach (var registration in _registrations) registration.Dispose();
                Removed = true;
            }
        }
        protected override void OnFramePresented()
        {
            if (!NativeRenderer.StartupSnapshot.IsFullQuality) return;
            if (Removed && ++_frames > 4) Exit();
        }
        protected override void Unload()
        {
            foreach (var registration in _registrations) registration.Dispose();
            _buffer?.Dispose();
        }
    }
    [TestCase(false), TestCase(true)]
    public void SettingsCanBeAwaitedDuringLoadAndPassesRetireOnRemovalOrFailure(bool fail)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        using var game = new TestGame(fail) { WindowWidth = 64, WindowHeight = 64 };
        if (fail) Assert.Throws<CallbackFailure>(game.Run);
        else game.Run();
        Assert.That(game.SettingsResult?.Outcome, Is.EqualTo(GraphicsSettingsOutcome.Applied));
        Assert.That(game.Passes, Has.Count.EqualTo(4));
        Assert.That(game.Passes.All(p => p.Disposals == 1), Is.True);
        Assert.That(game.NativeRenderer.ValidationMessageSnapshot.ErrorCount, Is.Zero);
        if (!fail)
        {
            Assert.That(game.Removed, Is.True);
            Assert.That(game.Order, Is.EqualTo(new[] { "Custom.Test0", "Custom.Test1", "Custom.Test2", "Custom.Test3",
                "Custom.Test0", "Custom.Test1", "Custom.Test2", "Custom.Test3" }));
        }
    }
}
