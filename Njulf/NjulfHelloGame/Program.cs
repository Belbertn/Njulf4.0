using System;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal static class Program
{
    public static void Main(string[] args)
    {
        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(args);
        using var game = new HelloGame(options, args);
        game.Run();
    }
}

internal sealed class HelloGame : Game
{
    private static readonly SampleAssetManifest AssetManifest = SampleAssetManifest.NewSponza;
    private const SampleLightingMode LightingMode = SampleLightingMode.PointShadowDemo;
    private const SampleEnvironmentMode EnvironmentMode = SampleEnvironmentMode.ProceduralOutdoor;
    private const int BaselineCaptureFrameCount = 1;

    private SampleInputController? _inputController;
    private SampleSceneLoader? _sceneLoader;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private IReadOnlyList<ParticleEffectInstance>? _sampleVfxEffects;
    private readonly SampleSmokeOptions _smokeOptions;
    private readonly RendererStartupLog _startupLog;
    private readonly SampleHealthReportWriter _healthReportWriter = new();
    private SampleLifecycleSmokeRunner? _smokeRunner;
    private SampleSceneReloadRunner? _sceneReloadRunner;
    private SampleLongRunMonitor? _longRunMonitor;
    private SampleBenchmarkRunner? _benchmarkRunner;
    private string? _lastSuccessfulStartupStep;
    private string? _startupFailure;
    private int _drawnFrames;
    private int _baselineScenarioRenderedFrames;
    private bool _baselineSnapshotExported;
    private float _modelRotation;
    private (int Width, int Height)? _pendingSmokeResize;

    public HelloGame(SampleSmokeOptions smokeOptions, string[] commandLineArgs)
    {
        _smokeOptions = smokeOptions ?? throw new ArgumentNullException(nameof(smokeOptions));
        _startupLog = new RendererStartupLog(_smokeOptions.StartupLogPath, commandLineArgs);

        Name = "Njulf Hello Game";
        WindowTitle = "Njulf Hello Game - Mesh Shader glTF Sample";
        WindowWidth = 1600;
        WindowHeight = 900;
        VSync = !_smokeOptions.Benchmark.Enabled || !_smokeOptions.Benchmark.DisableVSync;
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        if (Window == null)
            throw new InvalidOperationException("Window must exist before configuring the rendering sample.");

        services.AddNjulfCore();
        services.AddCamera(CreateSampleCamera());
        services.AddSingleton(_startupLog);
        services.AddRendering(Window, options =>
        {
            options.ValidationSettings = RendererValidationSettings.Default with
            {
                Mode = _smokeOptions.ValidationMode,
                FailOnErrorMessage = _smokeOptions.FailOnValidationMessage,
                StartupLogPath = _smokeOptions.StartupLogPath,
                HealthReportPath = _smokeOptions.HealthReportPath
            };
        });
        services.AddAssets(AppContext.BaseDirectory);
        services.AddInput();
    }

    protected override void Load()
    {
        var camera = Camera as FirstPersonCamera
            ?? throw new InvalidOperationException("NjulfHelloGame requires a FirstPersonCamera.");

        ValidateRuntimeServices();

        if (Input is not InputManager input)
            throw new InvalidOperationException("NjulfHelloGame requires the default InputManager.");

        IServiceProvider services = Services
            ?? throw new InvalidOperationException("Service provider was not created.");
        MeshManager meshManager = services.GetRequiredService<MeshManager>();
        MaterialManager materialManager = services.GetRequiredService<MaterialManager>();
        LightManager lightManager = services.GetRequiredService<LightManager>();
        VulkanRenderer renderer = Renderer as VulkanRenderer
            ?? throw new InvalidOperationException("NjulfHelloGame requires the Vulkan renderer.");

        SampleAssetValidationGate.Validate(AppContext.BaseDirectory, AssetManifest);
        SampleInputController.Configure(input);
        Model model = LoadSampleScene(meshManager, materialManager);
        _performanceScenarioRunner = new SamplePerformanceScenarioRunner(new SampleStressSceneBuilder(
            Scene,
            meshManager,
            materialManager,
            lightManager,
            LightingMode));
        if (_smokeOptions.PerformanceScenario != SamplePerformanceScenario.Normal)
        {
            SamplePerformanceScenarioSummary summary = _performanceScenarioRunner.Apply(_smokeOptions.PerformanceScenario);
            Console.WriteLine(
                $"Applied performance scenario for smoke run: {summary.Scenario} " +
                $"objects={summary.ObjectCount}, lights={summary.LightCount}, materials={summary.MaterialCount}, notes={summary.Notes}");
        }

        _inputController = new SampleInputController(
            camera,
            input,
            Exit,
            renderer,
            lightManager,
            LightingMode,
            _sampleVfxEffects,
            _performanceScenarioRunner);
        if (!string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory))
        {
            SamplePerformanceScenario baselineScenario = _smokeOptions.PerformanceScenario == SamplePerformanceScenario.ForestFoliage
                ? SamplePerformanceScenario.ForestFoliage
                : SamplePerformanceScenario.Normal;
            _inputController.ApplyBaselineScenario(baselineScenario);
        }

        SampleLighting.ConfigureRenderSettings(renderer.Settings, LightingMode);
        ApplySmokeRenderSettings(renderer);
        SampleLighting.Configure(lightManager, LightingMode);
        SampleEnvironment.Configure(renderer, EnvironmentMode);
        _sceneReloadRunner = new SampleSceneReloadRunner(() =>
        {
            Scene.ClearAndDispose();
            LoadSampleScene(meshManager, materialManager);
            SampleLighting.ConfigureRenderSettings(renderer.Settings, LightingMode);
            ApplySmokeRenderSettings(renderer);
            SampleLighting.Configure(lightManager, LightingMode);
            SampleEnvironment.Configure(renderer, EnvironmentMode);
        });
        _smokeRunner = new SampleLifecycleSmokeRunner(
            _smokeOptions,
            ResizeForSmoke,
            _sceneReloadRunner.Reload,
            Exit);
        _longRunMonitor = new SampleLongRunMonitor();
        if (_smokeOptions.Benchmark.Enabled)
        {
            _benchmarkRunner = new SampleBenchmarkRunner(
                _smokeOptions.Benchmark,
                _smokeOptions.PerformanceScenario,
                Exit);
            Console.WriteLine(
                $"Benchmark armed: warmup={_smokeOptions.Benchmark.WarmupFrameCount}, " +
                $"measure={_smokeOptions.Benchmark.MeasureFrameCount}, vsync={(VSync ? "on" : "off")}");
        }

        _diagnosticsReporter = new SampleDiagnosticsReporter(
            materialManager,
            services.GetService<IModelRenderUploadService>());
        _diagnosticsReporter.PrintModelSummary(model, AssetManifest);
    }

    private void ApplySmokeRenderSettings(VulkanRenderer renderer)
    {
        if (_smokeOptions.EnableGpuTiming || _smokeOptions.Benchmark.Enabled)
            renderer.Settings.Debug.AllowGpuTiming = true;

        if (_smokeOptions.EnableSceneGpuCompaction)
            renderer.Settings.SceneSubmission.GpuCompactionEnabled = true;
        if (_smokeOptions.EnableSceneIndirectDispatch)
            renderer.Settings.SceneSubmission.IndirectMeshletDispatchEnabled = true;
        if (_smokeOptions.EnableSceneGpuLodSelection)
            renderer.Settings.SceneSubmission.GpuLodSelectionEnabled = true;
        if (_smokeOptions.EnableSceneGpuShadowCompaction)
            renderer.Settings.SceneSubmission.GpuShadowCompactionEnabled = true;
        if (_smokeOptions.EnableSceneSubmissionValidation)
            renderer.Settings.SceneSubmission.ValidationCompareCpuGpuLists = true;
        if (_smokeOptions.EnableAsyncCompute)
            renderer.Settings.AsyncCompute.Enabled = true;

        renderer.Settings.Transparency.Mode = _smokeOptions.TransparencyMode;
    }

    protected override void Update(float deltaTime)
    {
        ApplyPendingSmokeResize();
        _inputController?.Update(deltaTime, WindowWidth, WindowHeight);

        if (AssetManifest.RotationSpeed != 0f)
        {
            _modelRotation += deltaTime * AssetManifest.RotationSpeed;
            _sceneLoader?.ApplyModelRotation(_modelRotation);
        }

        base.Update(deltaTime);
    }

    protected override void Draw()
    {
        if (Renderer == null)
            throw new InvalidOperationException("Renderer is not available during Draw().");
        if (Camera == null)
            throw new InvalidOperationException("Camera is not available during Draw().");

        Renderer.DrawScene(Scene, Camera);
        _diagnosticsReporter?.PrintFirstFrameDiagnostics(Renderer);
        if (Camera is FirstPersonCamera firstPersonCamera)
            _diagnosticsReporter?.PrintMovementFrameDiagnostics(Renderer, firstPersonCamera);

        if (_smokeOptions.Mode == SampleSmokeMode.LongRun || _smokeOptions.Mode == SampleSmokeMode.All)
            _longRunMonitor?.Sample(_drawnFrames);

        CaptureBaselineSnapshotIfRequested();
        if (Renderer is VulkanRenderer benchmarkRenderer)
        {
            _benchmarkRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics,
                benchmarkRenderer.LastBudgetSnapshot);
        }

        _smokeRunner?.OnFrameRendered(_drawnFrames);
        _drawnFrames++;
    }

    protected override void Unload()
    {
        RendererDiagnostics diagnostics = (Renderer as VulkanRenderer)?.LastDiagnostics ?? RendererDiagnostics.Empty;
        _healthReportWriter.TryWrite(
            _smokeOptions,
            _startupLog.Path,
            _smokeRunner?.Results ?? Array.Empty<SampleSmokeOperationResult>(),
            diagnostics,
            _startupFailure == null ? "passed" : "failed",
            _startupFailure);

        _startupLog.Dispose();
        base.Unload();
    }

    protected override void OnStartupStepStarted(string name)
    {
        _startupLog.StepStarted(name);
    }

    protected override void OnStartupStepSucceeded(string name, long elapsedMicroseconds)
    {
        _lastSuccessfulStartupStep = name;
        _startupLog.StepSucceeded(name);
    }

    protected override void OnStartupStepFailed(string name, Exception exception, long elapsedMicroseconds)
    {
        _startupFailure = exception.Message;
        _startupLog.StepFailed(name, exception);
        _startupLog.WriteFailure(RendererFailureReport.FromException(
            name,
            _lastSuccessfulStartupStep,
            exception,
            _startupLog.Path));
    }

    private static FirstPersonCamera CreateSampleCamera()
    {
        var camera = new FirstPersonCamera(new CoreVector3(0f, 1.25f, 5.5f), yaw: 0f, pitch: -0.12f)
        {
            FieldOfView = MathF.PI / 3.2f,
            NearPlane = 0.05f,
            FarPlane = 250f
        };

        return camera;
    }

    private Model LoadSampleScene(MeshManager meshManager, MaterialManager materialManager)
    {
        _sceneLoader = new SampleSceneLoader(Content!, materialManager, meshManager, AssetManifest);
        Model model = _sceneLoader.Load(Scene);
        SampleReflectionProbes.Configure(Scene);
        SampleReflectionTestSpheres.Configure(Scene, meshManager, materialManager);
        SampleAnimatedCharacter.Configure(Scene, Content!);
        _sampleVfxEffects = SampleVfxEffects.Configure(Scene);
        meshManager.CompactStaticBuffers();
        return model;
    }

    private void ResizeForSmoke(int width, int height)
    {
        _pendingSmokeResize = (width, height);
    }

    private void ApplyPendingSmokeResize()
    {
        if (_pendingSmokeResize is not { } resize)
            return;

        _pendingSmokeResize = null;
        int width = resize.Width;
        int height = resize.Height;
        if (width <= 0 || height <= 0)
        {
            Renderer?.Resize(width, height);
            return;
        }

        WindowWidth = width;
        WindowHeight = height;
        Renderer?.Resize(width, height);
        if (Camera != null)
            Camera.AspectRatio = (float)width / height;
    }

    private void CaptureBaselineSnapshotIfRequested()
    {
        if (_baselineSnapshotExported ||
            string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory) ||
            _inputController == null)
            return;

        _baselineScenarioRenderedFrames++;
        if (_baselineScenarioRenderedFrames < BaselineCaptureFrameCount)
            return;

        if (_smokeOptions.PerformanceScenario == SamplePerformanceScenario.ForestFoliage)
            ExportBaselineSnapshot("forest-foliage", "Baseline forest foliage snapshot");
        else
            ExportBaselineSnapshot("normal-sponza-interior", "Baseline normal Sponza/interior snapshot");

        _baselineSnapshotExported = true;
        Exit();
    }

    private void ExportBaselineSnapshot(string scenarioDirectoryName, string label)
    {
        if (_inputController == null || string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory))
            return;

        string directory = System.IO.Path.Combine(_smokeOptions.BaselineSnapshotDirectory, scenarioDirectoryName);
        _inputController.ExportPerformanceSnapshotFile(directory, label);
    }

    private void ValidateRuntimeServices()
    {
        if (Renderer == null)
            throw new InvalidOperationException("Renderer was not registered. Call AddRendering before loading content.");
        if (Content == null)
            throw new InvalidOperationException("Content manager was not registered. Call AddAssets after rendering services are registered.");
        if (Input == null)
            throw new InvalidOperationException("Input manager was not registered. Call AddInput before the sample loads.");
        if (Services?.GetService<IModelRenderUploadService>() == null)
        {
            throw new InvalidOperationException(
                "IModelRenderUploadService was not registered. Content.Load<Model>() requires renderer-backed model upload.");
        }
        if (Services.GetService<LightManager>() == null)
            throw new InvalidOperationException("LightManager was not registered by AddRendering.");
        if (Services.GetService<MeshManager>() == null)
            throw new InvalidOperationException("MeshManager was not registered by AddRendering.");
        if (Services.GetService<MaterialManager>() == null)
            throw new InvalidOperationException("MaterialManager was not registered by AddRendering.");
    }
}
