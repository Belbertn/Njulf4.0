using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Data;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Input;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class GameLifecycleIntegrationTests
{
    private sealed class AsyncLoadingGame(bool cancel) : Game
    {
        internal bool Resumed, Cancelled;
        internal int Updates;
        internal VulkanRenderer? NativeRenderer;
        private int _ownerThread;
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void Initialize()
        {
            Window.IsVisible = false;
            NativeRenderer = Services.GetRequiredService<VulkanRenderer>();
            _ownerThread = Environment.CurrentManagedThreadId;
        }
        protected override async Task LoadAsync(CancellationToken cancellationToken)
        {
            if (cancel)
            {
                Exit();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; }
                return;
            }
            await Task.Delay(10, cancellationToken);
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(_ownerThread));
            int result = await Services.GetRequiredService<IContentUploadDispatcher>()
                .DispatchAsync(() => Environment.CurrentManagedThreadId, cancellationToken);
            Assert.That(result, Is.EqualTo(_ownerThread));
            Assert.That(Environment.CurrentManagedThreadId, Is.EqualTo(_ownerThread));
            Resumed = true;
        }
        protected override void Update(GameTime time)
        {
            Assert.That(Resumed, Is.True);
            Assert.That(time.ElapsedGameTime, Is.EqualTo(TimeSpan.Zero));
            Updates++;
            Exit();
        }
    }

    [TestCase(false), TestCase(true)]
    public void AsyncStartupPumpsUploadsAndShutdownContinuationsOnDeviceThread(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Vulkan window fixture requires Windows.");
        SynchronizationContext? previous = SynchronizationContext.Current;
        using var game = new AsyncLoadingGame(cancel) { WindowWidth = 64, WindowHeight = 64 };
        game.Run();
        Assert.That(SynchronizationContext.Current, Is.SameAs(previous));
        Assert.That(game.Cancelled, Is.EqualTo(cancel));
        Assert.That(game.Resumed, Is.EqualTo(!cancel));
        Assert.That(game.Updates, Is.EqualTo(cancel ? 0 : 1));
        Assert.That(game.NativeRenderer!.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    private sealed class RetirementGame : Game
    {
        private Njulf.Core.Scene.RenderObject? _object;
        private MeshHandle _mesh;
        private MaterialHandle _material;
        private TextureHandle _texture;
        private bool _removed;
        private int _presents;
        internal bool Reclaimed;
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void Load()
        {
            using var mesh = GraphicsDevice.CreateMesh([new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(0, 1, 0)], [0u, 1u, 2u]);
            using var texture = GraphicsDevice.CreateTexture2D(1, 1, [255, 128, 64, 255], Njulf.Graphics.TextureColorSpace.Srgb);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default,
                [new(Njulf.Graphics.MaterialTextureSlot.BaseColor, texture)]);
            _mesh = ((Njulf.Graphics.VulkanMesh)mesh).Handle; _material = ((Njulf.Graphics.VulkanMaterial)material).Handle; _texture = ((Njulf.Graphics.VulkanTexture)texture).Handle;
            _object = GraphicsDevice.CreateRenderObject(mesh, material);
            Scene.Add(_object);
        }
        protected override void Draw(GameTime time)
        {
            base.Draw(time);
            if (_removed || !Services.GetRequiredService<VulkanRenderer>().StartupSnapshot.IsFullQuality) return;
            Scene.Remove(_object!);
            _removed = true;
            Assert.DoesNotThrow(() => Services.GetRequiredService<MeshManager>().GetMeshInfo(_mesh));
            Assert.DoesNotThrow(() => Services.GetRequiredService<MaterialManager>().GetMaterialDefinition(_material));
            Assert.DoesNotThrow(() => Services.GetRequiredService<TextureManager>().GetTextureInfo(_texture));
        }
        protected override void OnFramePresented()
        {
            if (!_removed || ++_presents < 10) return;
            Assert.Throws<InvalidOperationException>(() => Services.GetRequiredService<MeshManager>().GetMeshInfo(_mesh));
            Assert.Throws<InvalidOperationException>(() => Services.GetRequiredService<MaterialManager>().GetMaterialDefinition(_material));
            Assert.Throws<InvalidOperationException>(() => Services.GetRequiredService<TextureManager>().GetTextureInfo(_texture));
            Reclaimed = true;
            Exit();
        }
        protected override void Unload() => Assert.That(Services.GetRequiredService<VulkanRenderer>().ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    [Test]
    public void RemovingRecordedGeometryDefersSlotsAndDescriptorsUntilGpuCompletion()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Vulkan window fixture requires Windows.");
        using var game = new RetirementGame { WindowWidth = 64, WindowHeight = 64 };
        game.Run();
        Assert.That(game.Reclaimed, Is.True);
    }

    private sealed class EarlyExitGame(bool fail) : Game
    {
        internal int InitializeCalls, UnloadCalls;
        internal VulkanRenderer? NativeRenderer;
        internal readonly Exception Failure = new InvalidOperationException("User initialization failure");
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void Initialize()
        {
            InitializeCalls++;
            Window.IsVisible = false;
            NativeRenderer = Services.GetRequiredService<VulkanRenderer>();
            Assert.That(Renderer, Is.SameAs(NativeRenderer));
            Assert.That(GraphicsDevice, Is.SameAs(NativeRenderer.NativeGraphicsDevice));
            Assert.That(Content, Is.SameAs(Services.GetRequiredService<IContentManager>()));
            Assert.That(Input, Is.SameAs(Services.GetRequiredService<IInputManager>()));
            Assert.That(Camera, Is.SameAs(Services.GetRequiredService<ICamera>()));
            if (fail) throw Failure;
            Dispose();
            Assert.That(Scene, Is.Not.Null, "Callback disposal must wait for callback completion.");
        }
        protected override void Load() => Assert.Fail("Exit during Initialize must skip Load.");
        protected override void Unload()
        {
            UnloadCalls++;
            Assert.That(GraphicsDevice, Is.Not.Null);
            Assert.That(Content, Is.Not.Null);
        }
    }

    [TestCase(false), TestCase(true)]
    public void DefaultServicesExistAndEarlyExitAlwaysUnloadsOnce(bool fail)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Vulkan window fixture requires Windows.");
        using var game = new EarlyExitGame(fail) { WindowWidth = 64, WindowHeight = 64 };
        if (fail) Assert.That(Assert.Throws<InvalidOperationException>(game.Run), Is.SameAs(game.Failure));
        else Assert.DoesNotThrow(game.Run);
        Assert.That(game.InitializeCalls, Is.EqualTo(1));
        Assert.That(game.UnloadCalls, Is.EqualTo(1));
        Assert.That(game.NativeRenderer!.ValidationMessageSnapshot.ErrorCount, Is.Zero);
        Assert.That(() => game.Run(), Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => game.Content, Throws.TypeOf<ObjectDisposedException>());
    }
}
