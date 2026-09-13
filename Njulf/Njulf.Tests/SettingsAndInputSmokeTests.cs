using Hexa.NET.ImGui;
using Njulf.Core;
using Njulf.Editor;
using Njulf.Framework;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Input.Advanced;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;
using Silk.NET.Input;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class SettingsAndInputSmokeTests
{
    private sealed class SmokeGame : Game
    {
        internal VulkanRenderer Native = null!;
        internal bool Completed, Unloaded;
        private ImGuiEditorOverlayHost _overlay = null!;
        private EditorInputBridge _bridge = null!, _deviceBridge = null!;
        private InputManager _devices = null!;
        private Task<GraphicsSettingsResult>? _change;
        private int _frames;

        protected override void ConfigureRendering(RenderingOptions options)
        {
            options.ValidationSettings = RendererValidationSettings.Default with
            { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
            options.InitialSettings.Shadows.ApplyPreset(ShadowQualityPreset.Low);
            options.InitialSettings.Shadows.DirectionalShadowMapSize = 256;
        }

        protected override void Initialize()
        {
            Window.IsVisible = false;
            Native = (VulkanRenderer)Renderer;
        }

        protected override void Load()
        {
            _overlay = new ImGuiEditorOverlayHost();
            _overlay.SetEnabled(true);
            _bridge = new EditorInputBridge((INativeInputIntegration)Input, _overlay);
            // Deterministic native callbacks exercise the same Silk-backed manager and editor adapter.
            var (keyboard, keys) = InputDeviceProxy.Create<IKeyboard>();
            var (mouse, pointer) = InputDeviceProxy.Create<IMouse>();
            _devices = new InputManager(new TestInputContext(keyboards: [keyboard], mice: [mouse]));
            _devices.Initialize();
            _deviceBridge = new EditorInputBridge((INativeInputIntegration)_devices, _overlay);
            ImGui.GetIO().ConfigInputTrickleEventQueue = false;
            keys.Raise("KeyDown", keyboard, Key.A, 0);
            keys.Raise("KeyChar", keyboard, 'a');
            pointer.Raise("MouseMove", mouse, new System.Numerics.Vector2(30, 40));
            pointer.Raise("MouseDown", mouse, Silk.NET.Input.MouseButton.Left);
        }

        protected override void Update(GameTime time)
        {
            if (time.TotalGameTime > TimeSpan.FromMinutes(2)) throw new TimeoutException("Settings/editor smoke did not finish.");
            _overlay.BeginFrame(new System.Numerics.Vector2(128, 128), System.Numerics.Vector2.One, 1f / 60f);
            if (_frames++ == 0)
            {
                Assert.That(ImGui.IsKeyDown(ImGuiKey.A), Is.True);
                Assert.That(ImGui.IsMouseDown(ImGuiMouseButton.Left), Is.True);
                Assert.That(ImGui.GetIO().MousePos.X, Is.EqualTo(30));
                Assert.That(ImGui.GetIO().InputQueueCharacters.Size, Is.EqualTo(1));
                Assert.That(ImGui.GetIO().InputQueueCharacters[0], Is.EqualTo((uint)'a'));
            }
            ImGui.Text("Settings and input smoke");
            _overlay.SubmitFrame(Native);
            if (!Native.StartupSnapshot.IsFullQuality) return;
            _change ??= GraphicsDevice.Settings.ApplyAsync(new() { ShadowPreset = ShadowQualityPreset.Ultra });
            if (!_change.IsCompleted) return;
            Assert.That(_change.GetAwaiter().GetResult().Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rebuilt));
            Assert.That(Native.Settings.Shadows.DirectionalCascadeCount, Is.EqualTo(4));
            Completed = true;
            Exit();
        }

        protected override void Unload()
        {
            _deviceBridge?.Dispose();
            _bridge?.Dispose();
            _devices?.Dispose();
            _overlay?.ClearRenderer(Native);
            _overlay?.Dispose();
            Unloaded = true;
        }
    }

    [Test]
    public void EditorInputAndRuntimeShadowPresetSurviveHostLifecycle()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        using var game = new SmokeGame { WindowWidth = 128, WindowHeight = 128 };
        game.Run();
        Assert.That(game.Completed && game.Unloaded, Is.True);
        Assert.That(game.Native.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }
}
