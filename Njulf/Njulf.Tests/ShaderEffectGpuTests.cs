using Microsoft.Extensions.DependencyInjection;
using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;
using StbImageSharp;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class ShaderEffectGpuTests
{
    private readonly Dictionary<string, string?> _cacheEnvironment = new();
    private string? _cacheDirectory;
    [OneTimeSetUp]
    public void UseWorkspaceCaches()
    {
        _cacheDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "effect-runtime-cache", Guid.NewGuid().ToString("N"));
        foreach (string key in new[] { "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY", "NJULF_PIPELINE_BINARY_CACHE_DIRECTORY", "NJULF_DDGI_WARM_CACHE_DIR" })
        {
            string? previous = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(previous)) continue;
            _cacheEnvironment.Add(key, previous);
            Environment.SetEnvironmentVariable(key, Path.Combine(_cacheDirectory, key));
        }
    }
    [OneTimeTearDown]
    public void RestoreCaches()
    {
        foreach (var pair in _cacheEnvironment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        if (_cacheDirectory is not null && Directory.Exists(_cacheDirectory)) Directory.Delete(_cacheDirectory, true);
    }
    [TestCase(AntiAliasingMode.None)]
    [TestCase(AntiAliasingMode.SmaaMedium)]
    [TestCase(AntiAliasingMode.Taa)]
    public void EffectsProducePixelsAndSurviveRebindingResizeAndRemoval(AntiAliasingMode mode)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "effect-gpu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var game = new EffectGame(mode, directory) { WindowWidth = 128, WindowHeight = 96 };
            game.Run();
            Assert.That(game.Completed, Is.True);
            Assert.That(game.Renderer.ValidationMessageSnapshot.ErrorCount, Is.Zero);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class EffectGame(AntiAliasingMode mode, string directory) : Game
    {
        internal new VulkanRenderer Renderer = null!;
        internal bool Completed;
        private RenderTarget2D _intermediate = null!, _output = null!;
        private GraphicsBuffer _buffer = null!;
        private EffectRegistration _fullscreen = null!, _compute = null!, _first = null!, _second = null!;
        private Task<TextureReadback>? _pixels;
        private Task<byte[]>? _marker;
        private Task<GraphicsSettingsResult>? _resize;
        private int _qualityFrames, _phase, _phaseFrame;
        private string Capture(int number) => Path.Combine(directory, number + ".png");
        protected override void ConfigureRendering(RenderingOptions options) => options.ValidationSettings =
            RendererValidationSettings.Default with { Mode = RendererValidationMode.Standard, FailOnErrorMessage = true };
        protected override void ConfigureRendererBeforeInitialize(Njulf.Core.Interfaces.IRenderer renderer)
        {
            Renderer = (VulkanRenderer)renderer;
            Renderer.Settings.AntiAliasing.Mode = mode;
            Renderer.Settings.AutoExposure.Enabled = false;
            Renderer.Settings.Debug.Enabled = Renderer.Settings.Debug.AllowScreenshots = true;
        }
        protected override void Initialize() => Window.IsVisible = false;
        protected override void Load()
        {
            Assert.That(Renderer.Settings.AntiAliasing.EffectiveMode, Is.EqualTo(mode));
            var fragment = Content.Load<ShaderEffectAsset>(Path.Combine(ShaderEffectAssetTests.FixtureRoot, "effect_math.njeffect.json"));
            var compute = Content.Load<ShaderEffectAsset>(Path.Combine(ShaderEffectAssetTests.FixtureRoot, "effect_compute.njeffect.json"));
            using var source = GraphicsDevice.CreateTexture2D(1, 1, new byte[] { 204, 102, 51, 128 }, TextureColorSpace.Linear);
            using var black = GraphicsDevice.CreateTexture2D(1, 1, new byte[] { 0, 0, 0, 255 }, TextureColorSpace.Linear);
            _intermediate = GraphicsDevice.CreateRenderTarget2D(17, 13);
            _output = GraphicsDevice.CreateRenderTarget2D(17, 13);
            _buffer = GraphicsDevice.CreateBuffer(4);
            _fullscreen = GraphicsDevice.AddFullscreenEffect("Gpu.Fullscreen", fragment, EffectStage.BeforeScene,
                [new("SourceColor", Texture: source), new("ExtraColor", Texture: black)], new(Texture: _intermediate));
            _compute = GraphicsDevice.AddComputeEffect("Gpu.Compute", compute, EffectStage.BeforeScene,
                [new("SourceColor", Texture: _intermediate), new("Destination", Texture: _output), new("Data", Buffer: _buffer)], new(17, 13));
            _first = GraphicsDevice.AddPostProcessEffect("Gpu.Post.A", fragment, [new("ExtraColor", Texture: black)]);
            _first.SetParameter("Gain", 0f);
            _first.SetParameter("Bias", new Vector3(.25f, .5f, .75f));
            using var postData = GraphicsDevice.CreateBuffer(4);
            _second = GraphicsDevice.AddPostProcessEffect("Gpu.Post.B", compute, [new("Data", Buffer: postData)]);
            _second.SetParameter("Tint", new Vector3(.5f));
            Assert.Throws<ArgumentException>(() => _fullscreen.SetParameter("Gain", 1));
            Assert.Throws<ArgumentException>(() => _fullscreen.SetParameter("Unknown", 1f));
            Assert.Throws<ArgumentException>(() => _fullscreen.Rebind([]));
            Assert.Throws<ArgumentException>(() => _fullscreen.Rebind(
                [new("SourceColor", Texture: _intermediate), new("ExtraColor", Texture: black)]));
            using var released = GraphicsDevice.CreateTexture2D(1, 1, new byte[4], TextureColorSpace.Linear);
            released.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _fullscreen.Rebind(
                [new("SourceColor", Texture: released), new("ExtraColor", Texture: black)]));
            Assert.Throws<ArgumentException>(() => _compute.Rebind(
                [new("SourceColor", Texture: _intermediate), new("Destination", Texture: _output), new("Data", Buffer: _buffer, Offset: 5)]));
            // A tiny real scene admits production rendering. Post effects replace its color deterministically.
            using var mesh = GraphicsDevice.CreateMesh([new Vector3(0, 1, 0), new Vector3(-1, -1, 0), new Vector3(1, -1, 0)], [0u, 1u, 2u]);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default);
            Scene.Add(GraphicsDevice.CreateRenderObject(mesh, material));
            // Both source wrappers are disposed here; every registration keeps independent references.
        }
        protected override void OnFramePresented()
        {
            if (Renderer.StartupSnapshot.IsFullQuality) _qualityFrames++;
        }
        protected override void Update(GameTime time)
        {
            if (time.TotalGameTime > TimeSpan.FromMinutes(3)) throw new TimeoutException($"Effect test stalled at phase {_phase}.");
            base.Update(time);
            if (_qualityFrames < 4) return;
            switch (_phase)
            {
                case 0:
                    ReadOutput();
                    Renderer.RequestScreenshot(Capture(0));
                    _phase = 1;
                    break;
                case 1 when OutputReady() && File.Exists(Capture(0)):
                    AssertPixels(_pixels!.Result, new(.8f, .4f, .2f, 128f / 255f));
                    Assert.That(BitConverter.ToUInt32(_marker!.Result), Is.EqualTo(17u));
                    AssertCapture(Capture(0), new(.125f, .25f, .375f));
                    _first.Enabled = false;
                    _first.Enabled = true; // Re-enabling the earlier effect must preserve A -> B order.
                    _second.SetParameter("Tint", new Vector3(.25f));
                    Renderer.RequestScreenshot(Capture(1)); // First submitted frame after the parameter change.
                    _phase = 2;
                    break;
                case 2 when File.Exists(Capture(1)):
                    AssertCapture(Capture(1), new(.0625f, .125f, .1875f));
                    var nextIntermediate = GraphicsDevice.CreateRenderTarget2D(19, 11);
                    var nextOutput = GraphicsDevice.CreateRenderTarget2D(19, 11);
                    using (var green = GraphicsDevice.CreateTexture2D(1, 1, new byte[] { 0, 255, 0, 255 }, TextureColorSpace.Linear))
                    using (var black = GraphicsDevice.CreateTexture2D(1, 1, new byte[] { 0, 0, 0, 255 }, TextureColorSpace.Linear))
                        _fullscreen.Rebind([new("SourceColor", Texture: green), new("ExtraColor", Texture: black)], new EffectImage(Texture: nextIntermediate));
                    _compute.Rebind([new("SourceColor", Texture: nextIntermediate), new("Destination", Texture: nextOutput), new("Data", Buffer: _buffer)], new EffectDispatchSize(19, 11));
                    _intermediate.Dispose(); _output.Dispose();
                    _intermediate = nextIntermediate; _output = nextOutput;
                    _fullscreen.SetParameter("Gain", .5f);
                    _fullscreen.SetParameter("Bias", new Vector3(.1f, .2f, .3f));
                    _compute.SetParameter("Tint", new Vector3(.5f, 1, 1));
                    _compute.SetParameter("Marker", 29u);
                    Window.Size = new(144, 104);
                    _resize = GraphicsDevice.Settings.ApplyAsync(new() { ResolutionScale = .75f });
                    _phaseFrame = _qualityFrames;
                    _phase = 3;
                    break;
                case 3 when _resize!.IsCompleted && Window.FramebufferSize.X == 144 && _qualityFrames >= _phaseFrame + 4:
                    Assert.That(_resize.Result.Outcome, Is.EqualTo(GraphicsSettingsOutcome.Rebuilt));
                    ReadOutput();
                    Renderer.RequestScreenshot(Capture(2));
                    _phase = 4;
                    break;
                case 4 when OutputReady() && File.Exists(Capture(2)):
                    AssertPixels(_pixels!.Result, new(.05f, .7f, .3f, 1), 19, 11);
                    Assert.That(BitConverter.ToUInt32(_marker!.Result), Is.EqualTo(29u));
                    AssertCapture(Capture(2), new(.0625f, .125f, .1875f));
                    _first.Enabled = _second.Enabled = false;
                    _phaseFrame = _qualityFrames;
                    _phase = 5;
                    break;
                case 5 when _qualityFrames >= _phaseFrame + 4:
                    var graphics = (VulkanGraphicsDevice)GraphicsDevice;
                    Assert.That(graphics.CustomPasses.RequiresPostProcessColor, Is.False);
                    Assert.That(graphics.CustomPasses.Graph.GetPassResourceUsages("Gpu.Post.A"), Is.Empty);
                    ulong disabledRevision = graphics.PostEffectRevision;
                    _second.SetParameter("Tint", Vector3.One);
                    Assert.That(graphics.PostEffectRevision, Is.EqualTo(disabledRevision), "Editing a disabled effect must preserve TAA history.");
                    _fullscreen.Dispose(); _compute.Dispose(); _first.Dispose(); _second.Dispose();
                    Assert.Throws<ObjectDisposedException>(() => _second.SetParameter("Tint", Vector3.One));
                    _phaseFrame = _qualityFrames;
                    _phase = 6;
                    break;
                case 6 when _qualityFrames >= _phaseFrame + 4:
                    var identity = Services.GetRequiredService<Njulf.Rendering.Core.VulkanContext>().ShaderModuleIdentities.Snapshot();
                    Assert.That(LoadedShaderIdentity.Validate(identity), Is.Null);
                    Assert.That(identity.Modules.Count(m => m.SourceKind == "effect"), Is.EqualTo(3), "Fragment, compute and copy; parameter updates must not create shader variants.");
                    Completed = true;
                    Exit();
                    break;
            }
        }
        private void ReadOutput()
        {
            _pixels = GraphicsDevice.ReadTexture2DAsync(_output);
            _marker = GraphicsDevice.ReadBufferAsync(_buffer, 0, 4);
        }
        private bool OutputReady() => _pixels?.IsCompleted == true && _marker?.IsCompleted == true;
        protected override void Unload()
        {
            _fullscreen?.Dispose(); _compute?.Dispose(); _first?.Dispose(); _second?.Dispose();
            _intermediate?.Dispose(); _output?.Dispose(); _buffer?.Dispose();
            base.Unload();
        }
    }
    private static void AssertPixels(TextureReadback readback, Vector4 expected, int width = 17, int height = 13)
    {
        Assert.That((readback.Width, readback.Height, readback.Format), Is.EqualTo((width, height, TextureFormat.Rgba16Float)));
        float[] channels = [expected.X, expected.Y, expected.Z, expected.W];
        for (int pixel = 0; pixel < width * height; pixel++)
            for (int channel = 0; channel < 4; channel++)
                Assert.That((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(readback.Data, pixel * 8 + channel * 2)),
                    Is.EqualTo(channels[channel]).Within(.001f), $"Pixel {pixel}, channel {channel}");
    }
    private static void AssertCapture(string path, Vector3 linear)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        float[] values = [linear.X, linear.Y, linear.Z];
        // Uniform output checks both encoding and composition independently of scene lighting and AA.
        foreach (var point in new[] { (image.Width / 2, image.Height / 2), (image.Width / 4, image.Height / 4) })
            for (int channel = 0; channel < 3; channel++)
            {
                float encoded = values[channel] <= .0031308f ? values[channel] * 12.92f : 1.055f * MathF.Pow(values[channel], 1f / 2.4f) - .055f;
                Assert.That(image.Data[(point.Item2 * image.Width + point.Item1) * 4 + channel],
                    Is.EqualTo((int)MathF.Round(encoded * 255)).Within(2), $"Capture channel {channel}: {path}");
            }
    }
}
