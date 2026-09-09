using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.ApiExamples;

// Example-specific lighting, capture and finite-run controls; Game supplies the host services.
internal abstract class ExampleGame : Game
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private VulkanRenderer? _renderer;
    private int _qualityFrames;
    private bool _captureRequested;
    private bool _timedOut;
    private readonly List<double> _frameTimes = new();
    private long _lastFrameTimestamp;
    private long _allocationStart;
    protected ExampleOptions Options { get; }
    protected GraphicsDevice Graphics => GraphicsDevice;
    protected int QualityFrames => _qualityFrames;
    private Task<GraphicsSettingsResult>? _initialSettings;

    protected ExampleGame(ExampleOptions options)
    {
        Options = options;
        WindowTitle = $"Njulf API: {options.Example}";
        WindowWidth = 960;
        WindowHeight = 640;
    }

    protected override void ConfigureRendering(RenderingOptions options)
    {
        options.ValidationSettings =
            RendererValidationSettings.Default with
            {
                Mode = Options.Validation ? RendererValidationMode.Standard : RendererValidationMode.Off,
                FailOnErrorMessage = Options.Validation
            };
    }

    protected override void Load()
    {
        _renderer = Services!.GetRequiredService<VulkanRenderer>();
        if (Options.ForceAsyncValidation)
            _renderer.Settings.AsyncCompute.ForceValidationPath = Njulf.Rendering.Data.AsyncComputePath.Bloom;
        // Frame the small outdoor fixture with a fixed, reproducible exposure.
        _initialSettings = Graphics.Settings.ApplyAsync(new() { AutoExposureEnabled = false, Exposure = 0.01f,
            AntiAliasingMode = Options.DisableAntialiasing ? AntiAliasingMode.None : null,
            AsyncComputeMode = Options.ForceAsyncValidation ? AsyncComputeMode.ForceEnabledForValidation : null });
        if (Options.Capture != null)
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.Settings.Debug.AllowScreenshots = true;
        }
        Scene.Add(new SceneLight
        {
            Type = SceneLightType.Directional,
            Direction = new Vector3(-0.5f, -1, -1).Normalized(),
            Color = Vector3.One,
            Intensity = 3,
            CastsShadows = true,
            ShadowStrength = 1
        });
    }

    protected override void Update(GameTime gameTime)
    {
        if (Options.Frames > 0 && _elapsed.Elapsed > TimeSpan.FromMinutes(10))
        {
            _timedOut = true;
            Exit();
            return;
        }
        base.Update(gameTime);
    }

    protected override void OnFramePresented()
    {
        if (_renderer?.StartupSnapshot.IsFullQuality != true) return;
        _qualityFrames++;
        long timestamp = Stopwatch.GetTimestamp();
        if (_qualityFrames == 120) _allocationStart = GC.GetTotalAllocatedBytes();
        if (_qualityFrames > 120)
            _frameTimes.Add(Stopwatch.GetElapsedTime(_lastFrameTimestamp, timestamp).TotalMilliseconds);
        _lastFrameTimestamp = timestamp;
        // Let temporal lighting settle before saving the example image.
        if (!_captureRequested && Options.Capture != null && _qualityFrames >= (Options.Example == "custom" ? 300 : 120))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Options.Capture)!);
            _renderer.RequestScreenshot(Options.Capture);
            _captureRequested = true;
        }
        if (Options.Frames > 0 && _qualityFrames >= Options.Frames &&
            (Options.Capture == null || File.Exists(Options.Capture)))
            Exit();
    }

    public void ValidateCompletion()
    {
        if (_initialSettings?.IsCompletedSuccessfully != true || _initialSettings.Result.Outcome is GraphicsSettingsOutcome.Failed or GraphicsSettingsOutcome.Rejected)
            throw new InvalidOperationException("Example settings were not applied.");
        if (_timedOut)
            throw new TimeoutException("The example did not reach its full-quality frame target within ten minutes.");
        if (_renderer?.ValidationMessageSnapshot.ErrorCount > 0)
            throw new InvalidOperationException("Vulkan validation reported errors.");
        if (Options.Frames > 0 && _qualityFrames < Options.Frames)
            throw new InvalidOperationException("The example ended before the requested full-quality frames were presented.");
        if (Options.Capture != null && (!_captureRequested || !File.Exists(Options.Capture)))
            throw new InvalidOperationException("The requested screenshot was not produced.");
        Console.WriteLine($"API example {Options.Example}: {_qualityFrames} full-quality frames; validation errors=0.");
        if (Options.ForceAsyncValidation)
        {
            Console.WriteLine($"Async validation: {_renderer!.LastDiagnostics.AsyncComputeEffectiveMode}; enabled passes={_renderer.LastDiagnostics.AsyncComputeEnabledPassCount}; {_renderer.LastDiagnostics.AsyncComputeLastFallbackReason}");
            foreach (var path in _renderer.LastDiagnostics.AsyncComputePaths) Console.WriteLine($"Async path: {path}");
            if (_renderer.LastDiagnostics.AsyncComputeEnabledPassCount == 0)
                throw new InvalidOperationException("The requested async validation path did not execute; inspect its diagnostic reason.");
        }
        if (_frameTimes.Count > 0)
        {
            _frameTimes.Sort();
            Console.WriteLine($"Frame interval ms: median={_frameTimes[_frameTimes.Count / 2]:F3}, p95={_frameTimes[(int)((_frameTimes.Count - 1) * .95)]:F3}, p99={_frameTimes[(int)((_frameTimes.Count - 1) * .99)]:F3}; allocated bytes={GC.GetTotalAllocatedBytes() - _allocationStart}; samples={_frameTimes.Count}.");
        }
    }
}
