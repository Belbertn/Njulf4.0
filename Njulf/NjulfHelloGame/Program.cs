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
    private const SampleLightingMode LightingMode = SampleLightingMode.DirectionalKey;
    private const SampleEnvironmentMode EnvironmentMode = SampleEnvironmentMode.ProceduralOutdoor;
    private const SamplePerformanceScenario DefaultInteractiveScenario = SamplePerformanceScenario.Normal;
    private const int BaselineCaptureFrameCount = 1;

    private SampleInputController? _inputController;
    private SampleSceneLoader? _sceneLoader;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private IReadOnlyList<ParticleEffectInstance>? _sampleVfxEffects;
    private readonly SampleSmokeOptions _smokeOptions;
    private readonly RendererStartupLog _startupLog;
    private readonly SampleHealthReportWriter _healthReportWriter = new();
    private SampleSceneKind _sceneKind;
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
        _sceneKind = _smokeOptions.SceneKind;
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
        renderer.Settings.Debug.AllowGpuTiming = true;

        if (_sceneKind == SampleSceneKind.SponzaPlaza)
            SampleAssetValidationGate.Validate(AppContext.BaseDirectory, AssetManifest);
        SampleInputController.Configure(input);
        PrintRendererDeviceInfo(renderer);
        Model model = LoadSampleScene(meshManager, materialManager, lightManager);
        _performanceScenarioRunner = new SamplePerformanceScenarioRunner(new SampleStressSceneBuilder(
            Scene,
            meshManager,
            materialManager,
            lightManager,
            LightingMode));
        SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
        ApplySmokeRenderSettings(renderer);
        ConfigureSceneLighting(lightManager);
        ConfigureSceneEnvironment(renderer);
        ConfigureSceneRenderSettings(renderer.Settings);
        SamplePerformanceScenario startupScenario = ResolveStartupScenario();
        if (startupScenario != SamplePerformanceScenario.Normal)
        {
            SamplePerformanceScenarioSummary summary = _performanceScenarioRunner.Apply(startupScenario);
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(renderer.Settings, startupScenario);
            SampleGlobalIlluminationValidation.ConfigureSchedulerMode(renderer.Settings, _smokeOptions.DdgiSchedulerModeOverride);
            Console.WriteLine(
                $"Applied startup scenario: {summary.Scenario} " +
                $"objects={summary.ObjectCount}, lights={summary.LightCount}, materials={summary.MaterialCount}, notes={summary.Notes}");
        }

        _inputController = new SampleInputController(
            camera,
            input,
            Exit,
            renderer,
            lightManager,
            ResolveSceneLightingMode(),
            _sampleVfxEffects,
            _performanceScenarioRunner,
            () => CycleScene(meshManager, materialManager, lightManager, renderer, camera));
        if (!string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory))
        {
            SamplePerformanceScenario baselineScenario = ResolveBaselineSnapshotScenario();
            _inputController.ApplyBaselineScenario(baselineScenario);
        }

        _sceneReloadRunner = new SampleSceneReloadRunner(() =>
        {
            Scene.ClearAndDispose();
            LoadSampleScene(meshManager, materialManager, lightManager);
            SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
            ApplySmokeRenderSettings(renderer);
            ConfigureSceneLighting(lightManager);
            ConfigureSceneEnvironment(renderer);
            ConfigureSceneRenderSettings(renderer.Settings);
            _inputController?.SetParticleEffects(_sampleVfxEffects);
            SamplePerformanceScenario reloadScenario = ResolveStartupScenario();
            if (reloadScenario != SamplePerformanceScenario.Normal)
            {
                _performanceScenarioRunner.Apply(reloadScenario);
                SampleGlobalIlluminationValidation.ConfigureRenderSettings(renderer.Settings, reloadScenario);
                SampleGlobalIlluminationValidation.ConfigureSchedulerMode(renderer.Settings, _smokeOptions.DdgiSchedulerModeOverride);
            }
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
        PrintLoadedSceneSummary(model);
    }

    private static void PrintRendererDeviceInfo(VulkanRenderer renderer)
    {
        DeviceRequirementReport? device = renderer.SelectedDeviceRequirementReport;
        if (device == null || string.IsNullOrWhiteSpace(device.DeviceName))
        {
            Console.WriteLine("Vulkan GPU: unknown");
            return;
        }

        Console.WriteLine(
            $"Vulkan GPU: {device.DeviceName} " +
            $"vendor=0x{device.VendorId:X4}, device=0x{device.DeviceId:X4}, " +
            $"api={device.ApiVersion}, driver={device.DriverVersion}");
    }

    private SamplePerformanceScenario ResolveStartupScenario()
    {
        if (_smokeOptions.PerformanceScenario != SamplePerformanceScenario.Normal)
            return _smokeOptions.PerformanceScenario;

        return _smokeOptions.Enabled ? SamplePerformanceScenario.Normal : DefaultInteractiveScenario;
    }

    private void ApplySmokeRenderSettings(VulkanRenderer renderer)
    {
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

        SampleGlobalIlluminationValidation.ConfigureSchedulerMode(renderer.Settings, _smokeOptions.DdgiSchedulerModeOverride);
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
        if (_drawnFrames == 0 &&
            ShouldAutoEnableGpuTiming() &&
            Renderer is VulkanRenderer renderer)
        {
            renderer.Settings.Debug.AllowGpuTiming = true;
        }

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

    private bool ShouldAutoEnableGpuTiming()
    {
        return _smokeOptions.EnableGpuTiming ||
            _smokeOptions.Benchmark.Enabled;
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

    private FirstPersonCamera CreateSampleCamera()
    {
        (CoreVector3 position, float yaw, float pitch, float farPlane) = GetCameraPreset(_sceneKind);
        var camera = new FirstPersonCamera(position, yaw, pitch)
        {
            FieldOfView = MathF.PI / 3.2f,
            NearPlane = 0.05f,
            FarPlane = farPlane
        };

        return camera;
    }

    private Model LoadSampleScene(MeshManager meshManager, MaterialManager materialManager, LightManager lightManager)
    {
        _sampleVfxEffects = Array.Empty<ParticleEffectInstance>();

        if (_sceneKind == SampleSceneKind.MaterialShowcase)
        {
            _sceneLoader = null;
            SampleMaterialShowcaseScene.Configure(Scene, meshManager, materialManager);
            meshManager.CompactStaticBuffers();
            return new Model { Name = "Material Showcase" };
        }

        if (_sceneKind == SampleSceneKind.FoliageShowcase)
        {
            _sceneLoader = null;
            Scene.Name = "Njulf Foliage Showcase";
            var builder = new SampleStressSceneBuilder(
                Scene,
                meshManager,
                materialManager,
                lightManager,
                SampleLightingMode.DirectionalKey);
            builder.Apply(SamplePerformanceScenario.ForestFoliage);
            meshManager.CompactStaticBuffers();
            return new Model { Name = "Foliage Showcase" };
        }

        if (_sceneKind == SampleSceneKind.GlobalIlluminationTest)
        {
            _sceneLoader = null;
            Scene.Name = "Njulf GI Test Scene";
            var builder = new SampleStressSceneBuilder(
                Scene,
                meshManager,
                materialManager,
                lightManager,
                LightingMode);
            builder.Apply(SamplePerformanceScenario.GiCornellRoom);
            meshManager.CompactStaticBuffers();
            return new Model { Name = "GI Test Scene" };
        }

        if (_sceneKind == SampleSceneKind.VfxShowcase)
        {
            _sceneLoader = null;
            _sampleVfxEffects = SampleVfxShowcaseScene.Configure(Scene, meshManager, materialManager);
            meshManager.CompactStaticBuffers();
            return new Model { Name = "VFX Showcase" };
        }

        _sceneLoader = new SampleSceneLoader(Content!, materialManager, meshManager, AssetManifest);
        Model model = _sceneLoader.Load(Scene);
        SamplePlazaGlobalIllumination.ConfigureSceneLighting(Scene);
        SampleReflectionProbes.Configure(Scene);
        SampleAnimatedCharacter.Configure(Scene, Content!);
        meshManager.CompactStaticBuffers();
        return model;
    }

    private void ConfigureSceneRenderSettings(RenderSettings settings)
    {
        if (_sceneKind == SampleSceneKind.MaterialShowcase)
        {
            SampleMaterialShowcaseScene.ConfigureRenderSettings(settings);
            settings.Particles.Enabled = false;
            return;
        }

        if (_sceneKind == SampleSceneKind.FoliageShowcase)
        {
            ConfigureFoliageShowcaseRenderSettings(settings);
            return;
        }

        if (_sceneKind == SampleSceneKind.GlobalIlluminationTest)
        {
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, SamplePerformanceScenario.GiCornellRoom);
            settings.Particles.Enabled = false;
            return;
        }

        if (_sceneKind == SampleSceneKind.VfxShowcase)
        {
            SampleVfxShowcaseScene.ConfigureRenderSettings(settings);
            return;
        }

        SamplePlazaGlobalIllumination.ConfigureRenderSettings(settings);
        settings.Particles.Enabled = false;
    }

    private void CycleScene(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        SampleSceneKind[] sceneKinds = Enum.GetValues<SampleSceneKind>();
        int index = Array.IndexOf(sceneKinds, _sceneKind);
        _sceneKind = sceneKinds[(index + 1) % sceneKinds.Length];

        Scene.ClearAndDispose();
        Model model = LoadSampleScene(meshManager, materialManager, lightManager);
        SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
        ApplySmokeRenderSettings(renderer);
        ConfigureSceneLighting(lightManager);
        ConfigureSceneEnvironment(renderer);
        ConfigureSceneRenderSettings(renderer.Settings);
        _inputController?.SetParticleEffects(_sampleVfxEffects);
        _inputController?.SetLightingMode(ResolveSceneLightingMode());
        ApplyCameraPreset(camera, _sceneKind);
        PrintLoadedSceneSummary(model);

        Console.WriteLine($"Scene: {GetSceneDisplayName(_sceneKind)}");
    }

    private void ConfigureSceneLighting(LightManager lightManager)
    {
        if (_sceneKind == SampleSceneKind.GlobalIlluminationTest)
            return;

        SampleLighting.Configure(lightManager, ResolveSceneLightingMode());
    }

    private void ConfigureSceneEnvironment(VulkanRenderer renderer)
    {
        SampleEnvironment.Configure(renderer, _sceneKind switch
        {
            SampleSceneKind.MaterialShowcase => SampleEnvironmentMode.StudioNeutral,
            SampleSceneKind.GlobalIlluminationTest => SampleEnvironmentMode.StudioNeutral,
            SampleSceneKind.VfxShowcase => SampleEnvironmentMode.StudioNeutral,
            _ => EnvironmentMode
        });
    }

    private SampleLightingMode ResolveSceneLightingMode()
    {
        return _sceneKind switch
        {
            SampleSceneKind.GlobalIlluminationTest => SampleLightingMode.PointShadowDemo,
            SampleSceneKind.FoliageShowcase => SampleLightingMode.DirectionalKey,
            SampleSceneKind.MaterialShowcase => SampleLightingMode.ThreePointDemo,
            SampleSceneKind.VfxShowcase => SampleLightingMode.ThreePointDemo,
            _ => LightingMode
        };
    }

    private static void ConfigureFoliageShowcaseRenderSettings(RenderSettings settings)
    {
        settings.GlobalIllumination.Enabled = false;
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 1.0f;
        settings.Environment.DiffuseIntensity = 1.0f;
        settings.Environment.SpecularIntensity = 0.45f;
        settings.Reflections.Enabled = false;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.06f;
        settings.Particles.Enabled = false;
        settings.AmbientOcclusion.Enabled = true;
        settings.Foliage.Enabled = true;
        settings.Foliage.GpuDrivenEnabled = true;
    }

    private void PrintLoadedSceneSummary(Model model)
    {
        if (_diagnosticsReporter == null)
            return;

        if (_sceneKind == SampleSceneKind.SponzaPlaza)
        {
            _diagnosticsReporter.PrintModelSummary(model, AssetManifest);
            return;
        }

        _diagnosticsReporter.PrintProceduralSceneSummary(Scene, GetSceneDisplayName(_sceneKind));
    }

    private static void ApplyCameraPreset(FirstPersonCamera camera, SampleSceneKind sceneKind)
    {
        (CoreVector3 position, float yaw, float pitch, float farPlane) = GetCameraPreset(sceneKind);
        camera.Position = position;
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.FarPlane = farPlane;
        camera.Update();
    }

    private static (CoreVector3 Position, float Yaw, float Pitch, float FarPlane) GetCameraPreset(SampleSceneKind sceneKind)
    {
        return sceneKind switch
        {
            SampleSceneKind.GlobalIlluminationTest => (new CoreVector3(0f, 1.7f, 1.15f), 0f, -0.08f, 80f),
            SampleSceneKind.MaterialShowcase => (new CoreVector3(0f, 1.65f, 7.8f), 0f, -0.11f, 120f),
            SampleSceneKind.FoliageShowcase => (new CoreVector3(0f, 1.6f, 5.5f), 0f, -0.14f, 180f),
            SampleSceneKind.VfxShowcase => (new CoreVector3(0f, 1.45f, 6.2f), 0f, -0.16f, 120f),
            _ => (new CoreVector3(0f, 1.25f, 5.5f), 0f, -0.12f, 250f)
        };
    }

    private static string GetSceneDisplayName(SampleSceneKind sceneKind)
    {
        return sceneKind switch
        {
            SampleSceneKind.GlobalIlluminationTest => "GI Test Scene",
            SampleSceneKind.MaterialShowcase => "Material Showcase",
            SampleSceneKind.FoliageShowcase => "Foliage Showcase",
            SampleSceneKind.VfxShowcase => "VFX Showcase",
            _ => "Sponza Plaza"
        };
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

        (string directoryName, string label) = ResolveBaselineSnapshotMetadata();
        ExportBaselineSnapshot(directoryName, label);

        _baselineSnapshotExported = true;
        Exit();
    }

    private SamplePerformanceScenario ResolveBaselineSnapshotScenario()
    {
        return _smokeOptions.PerformanceScenario switch
        {
            SamplePerformanceScenario.ForestFoliage => SamplePerformanceScenario.ForestFoliage,
            SamplePerformanceScenario.GiSponzaRightWallStationary => SamplePerformanceScenario.GiSponzaRightWallStationary,
            _ => SamplePerformanceScenario.Normal
        };
    }

    private (string DirectoryName, string Label) ResolveBaselineSnapshotMetadata()
    {
        return ResolveBaselineSnapshotScenario() switch
        {
            SamplePerformanceScenario.ForestFoliage => ("forest-foliage", "Baseline forest foliage snapshot"),
            SamplePerformanceScenario.GiSponzaRightWallStationary => ("gi-sponza-right-wall-stationary", "Baseline Sponza right-wall GI snapshot"),
            _ => ("normal-sponza-interior", "Baseline normal Sponza/interior snapshot")
        };
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
