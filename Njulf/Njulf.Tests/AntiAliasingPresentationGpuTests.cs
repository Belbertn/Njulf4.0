using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Framework;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;
using StbImageSharp;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class AntiAliasingPresentationGpuTests
{
    [Test, Category("GPU")]
    public void AaAndIdentityPostEffectsPreserveFlatSurfaceBrightness()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "aa-presentation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = new Dictionary<string, string?>();
        try
        {
            foreach (string key in new[] { "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY",
                         "NJULF_PIPELINE_BINARY_CACHE_DIRECTORY", "NJULF_DDGI_WARM_CACHE_DIR" })
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, Path.Combine(directory, key));
            }
            using var game = new PresentationGame(directory) { WindowWidth = 128, WindowHeight = 96 };
            game.Run();
            Assert.That(game.Completed, Is.True);
            Assert.That(game.Renderer.ValidationMessageSnapshot.ErrorCount, Is.Zero);
        }
        finally
        {
            foreach (var entry in previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            Directory.Delete(directory, true);
        }
    }

    private sealed class PresentationGame(string directory) : Game
    {
        private static readonly AntiAliasingMode[] Modes =
            [AntiAliasingMode.None, AntiAliasingMode.Fxaa, AntiAliasingMode.SmaaLow,
             AntiAliasingMode.SmaaMedium, AntiAliasingMode.SmaaHigh,
             AntiAliasingMode.SmaaUltra, AntiAliasingMode.Taa];
        internal new VulkanRenderer Renderer = null!;
        internal bool Completed;
        private EffectRegistration _identity = null!;
        private int _qualityFrames, _lastChange, _step;
        private string? _pending;
        private byte[]? _reference;

        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void ConfigureRendererBeforeInitialize(Njulf.Core.Interfaces.IRenderer renderer)
        {
            Renderer = (VulkanRenderer)renderer;
            RenderSettings settings = Renderer.Settings;
            settings.ApplyQualityPreset(RenderQualityPreset.Low);
            settings.AntiAliasing.Mode = AntiAliasingMode.None;
            settings.AutoExposure.Enabled = settings.Bloom.Enabled = settings.Fog.Enabled = false;
            settings.GlobalIllumination.Enabled = settings.Reflections.Enabled = false;
            settings.AmbientOcclusion.Enabled = false;
            settings.Exposure = 1;
            settings.Debug.Enabled = settings.Debug.AllowScreenshots = true;
        }
        protected override void Initialize() => Window.IsVisible = false;
        protected override void Load()
        {
            using var mesh = GraphicsDevice.CreateMesh(
                [new Vector3(-20, -20, 0), new Vector3(20, -20, 0), new Vector3(0, 20, 0)], [0u, 1u, 2u]);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
            {
                ShadingModel = MaterialShadingModel.Unlit,
                BaseColorFactor = new Vector4(.08f, .25f, .6f, 1), DoubleSided = true
            });
            Scene.Add(GraphicsDevice.CreateRenderObject(mesh, material));
            var asset = Content.Load<ShaderEffectAsset>(Path.Combine(ShaderEffectAssetTests.FixtureRoot, "effect_math.njeffect.json"));
            using var black = GraphicsDevice.CreateTexture2D(1, 1, [0, 0, 0, 255], TextureColorSpace.Linear);
            _identity = GraphicsDevice.AddPostProcessEffect("AA.Identity", asset, [new("ExtraColor", Texture: black)]);
            _identity.Enabled = false;
        }
        protected override void OnFramePresented()
        {
            if (Renderer.StartupSnapshot.IsFullQuality) _qualityFrames++;
        }
        protected override void Update(GameTime time)
        {
            base.Update(time);
            if (time.TotalGameTime > TimeSpan.FromMinutes(3)) throw new TimeoutException($"AA presentation stalled at {_step}.");
            if (_pending is not null)
            {
                if (!File.Exists(_pending)) return;
                using var stream = File.OpenRead(_pending);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                byte[] center = image.Data.AsSpan((image.Height / 2 * image.Width + image.Width / 2) * 4, 3).ToArray();
                if (_reference is null)
                {
                    _reference = center;
                    Assert.That(center.Min(), Is.GreaterThan(8), "Reference must contain the lit color fixture.");
                    Assert.That(center.Max() - center.Min(), Is.GreaterThan(30));
                }
                else
                    for (int channel = 0; channel < 3; channel++)
                        Assert.That(center[channel], Is.EqualTo(_reference[channel]).Within(2),
                            $"{Modes[_step % Modes.Length]}, identity={_step >= Modes.Length}, channel={channel}");
                _pending = null;
                _step++;
                if (_step == Modes.Length * 2) { Completed = true; Exit(); return; }
                Renderer.Settings.AntiAliasing.Mode = Modes[_step % Modes.Length];
                _identity.Enabled = _step >= Modes.Length;
                _lastChange = _qualityFrames;
            }
            if (_qualityFrames < _lastChange + 8) return;
            _pending = Path.Combine(directory, $"{_step}.png");
            Renderer.RequestScreenshot(_pending);
        }
        protected override void Unload() { _identity?.Dispose(); base.Unload(); }
    }
}
