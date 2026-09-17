using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Framework;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using StbImageSharp;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class SmaaReferenceGpuTests
{
    [Test, Category("GPU")]
    public void CornersAndDiagonalsMatchIndependentUpstreamSmaa()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Requires Vulkan window support.");
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "smaa-reference", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string?>();
        bool succeeded = false;
        try
        {
            foreach (string key in new[] { "NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY", "NJULF_PIPELINE_BINARY_CACHE_DIRECTORY", "NJULF_DDGI_WARM_CACHE_DIR" })
            {
                environment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, Path.Combine(directory, key));
            }
            using var game = new ReferenceGame(directory) { WindowWidth = 128, WindowHeight = 96 };
            game.Run();
            Assert.That(game.Completed, Is.True);
            succeeded = true;
        }
        finally
        {
            foreach (var entry in environment) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            if (succeeded) Directory.Delete(directory, true);
            else TestContext.Progress.WriteLine($"SMAA failure evidence: {directory}");
        }
    }

    private sealed class ReferenceGame(string directory) : Game
    {
        private readonly List<EffectRegistration> _effects = [];
        private VulkanRenderer _renderer = null!;
        private RenderTarget2D _expected = null!;
        private Task<TextureReadback>? _readback;
        private int _frames;
        internal bool Completed;
        private string Capture => Path.Combine(directory, "actual.png");
        protected override void ConfigureRendererBeforeInitialize(Njulf.Core.Interfaces.IRenderer renderer)
        {
            _renderer = (VulkanRenderer)renderer;
            var settings = _renderer.Settings;
            settings.ApplyQualityPreset(RenderQualityPreset.Low);
            settings.ResolutionScale = 1;
            settings.DynamicResolution.Enabled = false;
            settings.AntiAliasing.Mode = AntiAliasingMode.SmaaHigh;
            settings.AntiAliasing.SmaaPredicationEnabled = false;
            settings.GlobalIllumination.Enabled = settings.Reflections.Enabled = settings.AmbientOcclusion.Enabled = false;
            settings.AutoExposure.Enabled = settings.Bloom.Enabled = settings.Fog.Enabled = false;
            settings.Debug.Enabled = settings.Debug.AllowScreenshots = true;
        }
        protected override void Initialize() => Window.IsVisible = false;
        protected override void Load()
        {
            using var mesh = GraphicsDevice.CreateMesh([new Vector3(0, 1, 0), new Vector3(-1, -1, 0), new Vector3(1, -1, 0)], [0u, 1u, 2u]);
            using var material = GraphicsDevice.CreateMaterial(MaterialDefinition.Default);
            Scene.Add(GraphicsDevice.CreateRenderObject(mesh, material));
            byte[] pixels = new byte[128 * 96 * 4];
            for (int y = 0; y < 96; y++)
                for (int x = 0; x < 128; x++)
                {
                    bool on = x < 64 ? ((x > y / 2 + 5 && x < y / 2 + 13) || (x > 12 && x < 36 && y > 60 && y < 84))
                        : ((x + y) % 31 < 8 || (x > 80 && x < 104 && y > 20 && y < 44));
                    int index = (y * 128 + x) * 4;
                    pixels[index] = pixels[index + 1] = pixels[index + 2] = on ? (byte)255 : (byte)0;
                    pixels[index + 3] = 255;
                }
            using var input = GraphicsDevice.CreateTexture2D(128, 96, pixels, TextureColorSpace.Linear);
            using var area = GraphicsDevice.CreateTexture2D(160, 560, Expand(SmaaLookupData.DecodeArea(), 2), TextureColorSpace.Linear);
            using var search = GraphicsDevice.CreateTexture2D(64, 16, Expand(SmaaLookupData.DecodeSearch(), 1), TextureColorSpace.Linear);
            using var edges = GraphicsDevice.CreateRenderTarget2D(128, 96);
            using var blend = GraphicsDevice.CreateRenderTarget2D(128, 96);
            _expected = GraphicsDevice.CreateRenderTarget2D(128, 96);
            ShaderEffectAsset Asset(string name) => Content.Load<ShaderEffectAsset>(Path.Combine(ShaderEffectAssetTests.FixtureRoot, name + ".njeffect.json"));
            _effects.Add(GraphicsDevice.AddFullscreenEffect("Reference.Edges", Asset("smaa_reference_edge"), EffectStage.BeforeScene,
                [new("SourceColor", Texture: input)], new(Texture: edges)));
            _effects.Add(GraphicsDevice.AddFullscreenEffect("Reference.Blend", Asset("smaa_reference_blend"), EffectStage.BeforeScene,
                [new("Edges", Texture: edges), new("Area", Texture: area), new("Search", Texture: search)], new(Texture: blend)));
            _effects.Add(GraphicsDevice.AddFullscreenEffect("Reference.Neighborhood", Asset("smaa_reference_neighborhood"), EffectStage.BeforeScene,
                [new("SourceColor", Texture: input), new("Blend", Texture: blend)], new(Texture: _expected)));
            var pattern = GraphicsDevice.AddPostProcessEffect("Reference.Input", Asset("smaa_reference_input"), [new("Pattern", Texture: input)]);
            _effects.Add(pattern);
        }
        private static byte[] Expand(byte[] source, int channels)
        {
            byte[] rgba = new byte[source.Length / channels * 4];
            for (int i = 0; i < source.Length / channels; i++)
            {
                Array.Copy(source, i * channels, rgba, i * 4, channels);
                rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }
        protected override void OnFramePresented() { if (_renderer.StartupSnapshot.IsFullQuality) _frames++; }
        protected override void Update(GameTime time)
        {
            base.Update(time);
            if (time.TotalGameTime > TimeSpan.FromMinutes(3)) throw new TimeoutException("SMAA reference capture stalled.");
            if (_frames < 8) return;
            if (_readback is null)
            {
                _readback = GraphicsDevice.ReadTexture2DAsync(_expected);
                _renderer.RequestScreenshot(Capture);
                return;
            }
            if (!_readback.IsCompleted || !File.Exists(Capture)) return;
            byte[] expected = _readback.GetAwaiter().GetResult().Data;
            using var stream = File.OpenRead(Capture);
            var actual = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            Assert.That((actual.Width, actual.Height), Is.EqualTo((128, 96)));
            int blended = 0, mismatches = 0, maximumError = 0;
            var failures = new List<string>();
            for (int y = 4; y < 92; y++)
                for (int x = 4; x < 124; x++)
                {
                    int pixel = y * 128 + x;
                    float linear = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(expected, pixel * 8));
                    if (linear > .01f && linear < .99f) blended++;
                    float srgb = linear <= .0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - .055f;
                    int error = Math.Abs(actual.Data[pixel * 4] - (int)MathF.Round(srgb * 255));
                    maximumError = Math.Max(maximumError, error);
                    // Compare in linear light: one UNORM8 weight step near black
                    // spans many sRGB codes. Include the screenshot's half-code
                    // quantization interval, then allow one weight-attachment LSB.
                    float Decode(float value) => value <= .04045f
                        ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
                    float minimum = Decode(Math.Max(0, actual.Data[pixel * 4] - .5f) / 255f);
                    float maximum = Decode(Math.Min(255, actual.Data[pixel * 4] + .5f) / 255f);
                    float linearError = Math.Max(minimum - linear, linear - maximum);
                    if (linearError > 1f / 255f + .00001f)
                    {
                        mismatches++;
                        failures.Add($"{x},{y},{linear:R},{actual.Data[pixel * 4]},{error}");
                    }
                }
            if (mismatches != 0)
            {
                File.WriteAllBytes(Path.Combine(directory, "expected.rgba16f"), expected);
                File.WriteAllLines(Path.Combine(directory, "mismatches.csv"),
                    new[] { "x,y,expected_linear,actual_srgb,error" }.Concat(failures));
            }
            Assert.That(blended, Is.GreaterThan(50), "The oracle must exercise nonzero AA weights.");
            Assert.That(mismatches, Is.Zero, $"SMAA differs from upstream; maximum channel error {maximumError}.");
            Completed = true;
            Exit();
        }
        protected override void Unload()
        {
            foreach (var effect in _effects) effect.Dispose();
            _expected?.Dispose();
            base.Unload();
        }
    }
}
