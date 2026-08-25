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
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;
#if NJULF_EDITOR
using Njulf.Editor;
using Silk.NET.Input;
#endif

namespace NjulfHelloGame;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (SampleBenchmarkDdgiTransientVerificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int ddgiTransientVerificationExitCode))
        {
            return ddgiTransientVerificationExitCode;
        }
        if (SampleTailDdgiQualificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int tailDdgiQualificationExitCode))
        {
            return tailDdgiQualificationExitCode;
        }
        if (SampleBenchmarkActivationVerificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int activationVerificationExitCode))
        {
            return activationVerificationExitCode;
        }
        if (SampleBenchmarkQualityActivationVerificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int qualityActivationVerificationExitCode))
        {
            return qualityActivationVerificationExitCode;
        }
        if (SampleBenchmarkControlledIsolationVerificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int controlledIsolationVerificationExitCode))
        {
            return controlledIsolationVerificationExitCode;
        }
        if (SampleBenchmarkPairComparisonCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int benchmarkComparisonExitCode))
        {
            return benchmarkComparisonExitCode;
        }
        if (SampleMaterialGiComparisonCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int comparisonExitCode))
        {
            return comparisonExitCode;
        }
        if (SampleMaterialGiApprovedHdrCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int visualRegressionExitCode))
        {
            return visualRegressionExitCode;
        }

        SampleSmokeOptions options = SampleSmokeOptionsParser.Parse(args);
        if (!string.IsNullOrWhiteSpace(
                options.SponzaTemporalAnalyzeDirectory))
        {
            return SampleSponzaTemporalCaptureAnalyzer.RunOffline(
                options.SponzaTemporalAnalyzeDirectory,
                Console.Out,
                Console.Error);
        }
        if (!string.IsNullOrWhiteSpace(
                options.VolumetricTemporalAnalyzeDirectory))
        {
            return SampleVolumetricTemporalCaptureAnalyzer.RunOffline(
                options.VolumetricTemporalAnalyzeDirectory,
                Console.Out,
                Console.Error);
        }
        using var gateFailureGuard =
            options.KhronosMaterialGiRenderedGate is { } gateOptions
                ? new SampleKhronosMaterialGiRenderedGateHostFailureGuard(gateOptions)
                : null;
        try
        {
            while (true)
            {
                string? requestedProfilePath;
                AdvancedGiFeatureSelection? requestedFeatures;
                SampleSceneKind currentSceneKind;
                using (var game = new HelloGame(options, args))
                {
                    game.Run();
                    requestedProfilePath =
                        game.RequestedAdvancedGiStartupProfilePath;
                    requestedFeatures =
                        game.RequestedAdvancedGiFeatureSelection;
                    currentSceneKind = game.CurrentSceneKind;
                }
                if (requestedFeatures is { } features)
                {
                    options = PrepareAdvancedGiFeatureRestartOptions(
                        options,
                        features,
                        currentSceneKind);
                    Environment.ExitCode = 0;
                    continue;
                }
                if (requestedProfilePath is not { } profilePath)
                {
                    break;
                }

                // The old renderer, service provider, Vulkan device, input,
                // and window are now fully disposed. The reconstructed host
                // consumes this profile before optional device features and
                // immutable graph branches are selected.
                options = PrepareAdvancedGiRestartOptions(
                    options,
                    profilePath,
                    currentSceneKind);
                Environment.ExitCode = 0;
            }
        }
        catch (Exception exception) when (options.KhronosMaterialGiRenderedGate is not null)
        {
            string failure =
                $"Khronos rendered-gate host failed: {exception.GetType().Name}: {exception.Message}";
            gateFailureGuard?.RecordHostFailure(failure);
            Environment.ExitCode = 1;
            Console.Error.WriteLine(failure);
        }
        if (gateFailureGuard is not null &&
            !gateFailureGuard.CompleteHostRun(Environment.ExitCode))
        {
            Environment.ExitCode = 1;
        }
        return Environment.ExitCode;
    }

    internal static SampleSmokeOptions PrepareAdvancedGiRestartOptions(
        SampleSmokeOptions source,
        string profilePath,
        SampleSceneKind currentSceneKind) => source with
    {
        SceneKind = currentSceneKind,
        AdvancedGiStartupProfilePath = Path.GetFullPath(profilePath),
        AdvancedGiPrerequisiteManifestPath = null,
        AdvancedGiQualificationManifestPath = null,
        AdvancedGiRuntimeEvidenceBundlePath = null,
        SimpleDdgiReceiverFeedbackModeOverride = null,
        DdgiOpacityMicromapModeOverride = null,
        SimpleDdgiDirectionalGuidingModeOverride = null,
        GiCausticModeOverride = null,
        SimpleDdgiNearFieldResidualModeOverride = null,
        SimpleDdgiReceiverFeedbackQualificationId = null,
        DdgiOpacityMicromapQualificationId = null,
        SimpleDdgiDirectionalGuidingQualificationId = null,
        GiCausticQualificationId = null,
        SimpleDdgiNearFieldResidualQualificationId = null,
        OpenEditorOnStartup = true
    };

    internal static SampleSmokeOptions PrepareAdvancedGiFeatureRestartOptions(
        SampleSmokeOptions source,
        in AdvancedGiFeatureSelection selection) =>
        PrepareAdvancedGiFeatureRestartOptions(
            source,
            selection,
            source.SceneKind);

    internal static SampleSmokeOptions PrepareAdvancedGiFeatureRestartOptions(
        SampleSmokeOptions source,
        in AdvancedGiFeatureSelection selection,
        SampleSceneKind currentSceneKind) => source with
    {
        SceneKind = currentSceneKind,
        AdvancedGiStartupProfilePath = null,
        AdvancedGiPrerequisiteManifestPath = null,
        AdvancedGiQualificationManifestPath = null,
        AdvancedGiRuntimeEvidenceBundlePath = null,
        SimpleDdgiReceiverFeedbackModeOverride =
            selection.ReceiverFeedbackEnabled
                ? SimpleDdgiReceiverFeedbackMode.ExactCompacted
                : SimpleDdgiReceiverFeedbackMode.Off,
        DdgiOpacityMicromapModeOverride = selection.OpacityMicromapsEnabled
            ? DdgiOpacityMicromapMode.ExtFourStateExperiment
            : DdgiOpacityMicromapMode.Off,
        SimpleDdgiDirectionalGuidingModeOverride =
            selection.DirectionalGuidingEnabled
                ? SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment
                : SimpleDdgiDirectionalGuidingMode.Off,
        GiCausticModeOverride = selection.TaggedCausticsEnabled
            ? GiCausticMode.WorldCacheExperiment
            : GiCausticMode.Off,
        SimpleDdgiNearFieldResidualModeOverride =
            selection.NearFieldResidualEnabled
                ? SimpleDdgiNearFieldResidualMode
                    .HiZAdaptive
                : SimpleDdgiNearFieldResidualMode.Off,
        SimpleDdgiReceiverFeedbackQualificationId = null,
        DdgiOpacityMicromapQualificationId = null,
        SimpleDdgiDirectionalGuidingQualificationId = null,
        GiCausticQualificationId = null,
        SimpleDdgiNearFieldResidualQualificationId = null,
        OpenEditorOnStartup = true
    };
}

internal sealed class HelloGame : Game
{
    private static readonly SampleAssetManifest SponzaAssetManifest = SampleAssetManifest.NewSponza;
    private const SampleLightingMode LightingMode = SampleLightingMode.DirectionalKey;
    private const SampleEnvironmentMode EnvironmentMode = SampleEnvironmentMode.ProceduralOutdoor;
    private const SamplePerformanceScenario DefaultInteractiveScenario = SamplePerformanceScenario.Normal;
    private const int BaselineCaptureFrameCount = 900;
    private const int VolumetricBaselineCaptureFrameCount = 12;
    internal const int BenchmarkDynamicScenarioDisturbanceFrameCount = 30;
    internal const float BenchmarkSimulationDeltaSeconds = 1.0f / 60.0f;

    private SampleInputController? _inputController;
    private SampleSceneLoader? _sceneLoader;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private IReadOnlyList<ParticleEffectInstance>? _sampleVfxEffects;
    private readonly SampleSmokeOptions _smokeOptions;
    private readonly SampleMaterialGiRolloutBootstrap _materialGiRolloutBootstrap;
    private readonly SampleVfxVolumetricDemoOverride
        _vfxVolumetricDemoOverride = new();
    private readonly RendererStartupLog _startupLog;
    private readonly SampleHealthReportWriter _healthReportWriter = new();
    private SampleSceneKind _sceneKind;
    private SampleLifecycleSmokeRunner? _smokeRunner;
    private SampleSceneReloadRunner? _sceneReloadRunner;
    private SampleLongRunMonitor? _longRunMonitor;
    private SampleQualitySwitchSmokeRunner? _qualitySwitchSmokeRunner;
    private SampleDdgiResidencySwitchSmokeRunner?
        _ddgiResidencySwitchSmokeRunner;
    private SampleTextureHotReloadSmokeRunner? _textureHotReloadSmokeRunner;
    private SampleBenchmarkRunner? _benchmarkRunner;
    private SampleBenchmarkQualitySequenceRunner?
        _benchmarkQualitySequenceRunner;
    private SampleBistroQualityRuntimeController?
        _bistroQualityRuntimeController;
    private SampleBistroQualityCaptureRunner? _bistroQualityCaptureRunner;
    private SampleMaterialGiCaptureRunner? _materialGiCaptureRunner;
    private SampleSponzaTemporalCaptureRunner?
        _sponzaTemporalCaptureRunner;
    private SampleVolumetricTemporalCaptureRunner?
        _volumetricTemporalCaptureRunner;
    private SampleKhronosMaterialGiRenderedSceneBuild? _khronosMaterialGiRenderedScene;
    private SampleKhronosMaterialGiRenderedGateRunner? _khronosMaterialGiRenderedGateRunner;
    private string? _lastSuccessfulStartupStep;
    private string? _startupFailure;
    private string? _runtimeSmokeFailure;
    private int _drawnFrames;
    private bool _sponzaAtmosphereFrozen;
    private bool _benchmarkDynamicScenarioFrozen;
    private int _baselineScenarioRenderedFrames;
    private bool _baselineSnapshotExported;
    private float _modelRotation;
    private (int Width, int Height)? _pendingSmokeResize;
    private PendingSmokeWindowMutation? _observingSmokeWindowMutation;
    private long _framebufferResizeRevision;
    private int _benchmarkActivationPreparedDrawFrame = -1;
    // Explicit initialization keeps non-editor builds warning-clean; the
    // editor-only restart callback is the sole writer when that feature is
    // compiled in.
    private string? _requestedAdvancedGiStartupProfilePath = null;
    private AdvancedGiFeatureSelection?
        _requestedAdvancedGiFeatureSelection = null;
#if NJULF_EDITOR
    private ImGuiEditorOverlayHost? _editorHost;
    private EditorInputBridge? _editorInput;
    private EditorController? _editorController;
    private EditorImGuiPanels? _editorPanels;
    private bool _editorTogglePressed;
    private bool _editorSavePressed;
    private bool _editorPickPressed;
#endif

    public HelloGame(SampleSmokeOptions smokeOptions, string[] commandLineArgs)
    {
        _smokeOptions = smokeOptions ?? throw new ArgumentNullException(nameof(smokeOptions));
        _materialGiRolloutBootstrap = SampleMaterialGiRolloutBootstrap.Load(
            _smokeOptions.MaterialGiQualificationManifestPath,
            qualificationCandidate:
                _smokeOptions.Benchmark.MaterialGiQualificationCandidate);
        _sceneKind = _smokeOptions.SceneKind;
        _startupLog = new RendererStartupLog(_smokeOptions.StartupLogPath, commandLineArgs);

        Name = "Njulf Hello Game";
        WindowTitle = "Njulf Hello Game - Mesh Shader glTF Sample";
        bool sponzaTemporalCapture = !string.IsNullOrWhiteSpace(
            _smokeOptions.SponzaTemporalCaptureDirectory);
        bool volumetricTemporalCapture = !string.IsNullOrWhiteSpace(
            _smokeOptions.VolumetricTemporalCaptureDirectory);
        bool controlledProductionRun = RequiresControlledProductionWindow(
            _smokeOptions);
        if (volumetricTemporalCapture)
        {
            (WindowWidth, WindowHeight) =
                SampleVolumetricTemporalCaptureContract.GetDimensions(
                    _smokeOptions.QualityPresetOverride ??
                    RenderQualityPreset.High);
        }
        else
        {
            WindowWidth = controlledProductionRun ? 1920 : 1600;
            WindowHeight = controlledProductionRun ? 1080 : 900;
        }
        WindowBorderStyle = controlledProductionRun ||
                            sponzaTemporalCapture ||
                            volumetricTemporalCapture
            ? Silk.NET.Windowing.WindowBorder.Hidden
            : Silk.NET.Windowing.WindowBorder.Resizable;
        VSync = _smokeOptions.KhronosMaterialGiRenderedGate is null &&
                !(_smokeOptions.TailDdgiLongSoak ||
                  (_smokeOptions.Benchmark.Enabled &&
                   _smokeOptions.Benchmark.DisableVSync) ||
                  _smokeOptions.BenchmarkQualitySequence.Enabled ||
                  sponzaTemporalCapture ||
                  volumetricTemporalCapture ||
                  !string.IsNullOrWhiteSpace(
                      _smokeOptions.BistroQualityCaptureDirectory));
    }

    internal static bool RequiresControlledProductionWindow(
        SampleSmokeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Benchmark.Enabled ||
               options.BenchmarkQualitySequence.Enabled ||
               options.TailDdgiLongSoak ||
               !string.IsNullOrWhiteSpace(options.SponzaGiCaptureDirectory) ||
               !string.IsNullOrWhiteSpace(options.BistroQualityCaptureDirectory);
    }

    internal string? RequestedAdvancedGiStartupProfilePath =>
        _requestedAdvancedGiStartupProfilePath;
    internal AdvancedGiFeatureSelection?
        RequestedAdvancedGiFeatureSelection =>
            _requestedAdvancedGiFeatureSelection;
    internal SampleSceneKind CurrentSceneKind => _sceneKind;

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
            options.AdvancedGiPrerequisiteManifestPath =
                _smokeOptions.AdvancedGiPrerequisiteManifestPath;
            options.AdvancedGiQualificationManifestPath =
                _smokeOptions.AdvancedGiQualificationManifestPath;
            options.AdvancedGiRuntimeEvidenceBundlePath =
                _smokeOptions.AdvancedGiRuntimeEvidenceBundlePath;
            options.AdvancedGiStartupProfilePath =
                _smokeOptions.AdvancedGiStartupProfilePath;
            if (_sceneKind == SampleSceneKind.Bistro &&
                SampleBistroGlobalIlluminationProfile
                    .ShouldApplyDefaultImportedTextureBudget(
                        Environment.GetEnvironmentVariable(
                            "NJULF_MAX_IMPORTED_TEXTURE_SIZE"),
                        Environment.GetEnvironmentVariable(
                            "NJULF_TEXTURE_BUDGET_PROFILE")))
            {
                options.SetCustomMaxImportedTextureDimension(
                    SampleBistroGlobalIlluminationProfile
                        .DefaultImportedTextureDimension);
            }
            ApplyPreInitializationRenderSettings(options.InitialSettings);
            if (_smokeOptions.DdgiOpacityMicromapModeOverride is
                DdgiOpacityMicromapMode.ExtFourStateExperiment or
                DdgiOpacityMicromapMode.AutoQualified)
            {
                // An explicit C1 override also pins the optional-device request.
                // Capability, prerequisite, evidence, and static-BLAS gates
                // still independently fail closed during initialization.
                options.EnableExtOpacityMicromap = true;
            }
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
        renderer.CaptureSceneKind = GetPerformanceCaptureSceneKind(_sceneKind);
        if (ShouldAutoEnableGpuTiming())
            renderer.Settings.Debug.AllowGpuTiming = true;
        ConfigureSceneRenderSettings(renderer);

        if (_sceneKind == SampleSceneKind.SponzaPlaza)
            SampleAssetValidationGate.Validate(AppContext.BaseDirectory, SponzaAssetManifest);
        SampleInputController.Configure(input);
        Console.WriteLine(
            "Debug overlays: Ctrl+Keypad9/Ctrl+Num9 forward, add Shift for reverse; " +
            "cycle=" + string.Join(" -> ", DebugOverlayCatalog.ActiveCycle.Select(
                static descriptor => descriptor.DisplayName)) + ".");
        PrintRendererDeviceInfo(renderer);
        Model model = LoadSampleScene(meshManager, materialManager, lightManager);
        _performanceScenarioRunner = CreatePerformanceScenarioRunner(meshManager, materialManager, lightManager);
        var diagnosticsReporter = new SampleDiagnosticsReporter(
            materialManager,
            services.GetService<IModelRenderUploadService>());
        _diagnosticsReporter = diagnosticsReporter;
        SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
        ApplySmokeRenderSettings(renderer);
        ConfigureSceneLighting(lightManager);
        ConfigureSceneEnvironment(renderer);
        ConfigureSceneRenderSettings(renderer);
        ApplySmokeRenderSettings(renderer);
        SamplePerformanceScenario startupScenario = ResolveStartupScenario();
        if (startupScenario != SamplePerformanceScenario.Normal)
        {
            SamplePerformanceScenarioSummary summary = _performanceScenarioRunner.Apply(startupScenario);
            _sponzaAtmosphereFrozen = false;
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(renderer.Settings, startupScenario);
            ApplySmokeRenderSettings(renderer);
            ApplyPerformanceScenarioCamera(camera, startupScenario);
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
            () => CycleScene(meshManager, materialManager, lightManager, renderer, camera),
            () => CycleSponzaAndBistro(meshManager, materialManager, lightManager, renderer, camera),
            sceneKind => LoadSceneKind(sceneKind, meshManager, materialManager, lightManager, renderer, camera),
            () => diagnosticsReporter.ToggleDdgiFilter(),
            () => diagnosticsReporter.Filter,
            () => RestoreSceneRenderSettings(renderer),
            ApplyScenePostOverrides,
            CaptureDiagnosticScreenshot
#if NJULF_EDITOR
            , () => _editorController?.SuppressGameInput == true
#endif
            );

        if (_sceneKind == SampleSceneKind.Bistro &&
            (startupScenario ==
                 SamplePerformanceScenario.BistroQualityMotionRelight ||
             SampleBenchmarkTrajectory.RequiresBistro(
                 _smokeOptions.Benchmark.Trajectory) ||
             SampleBenchmarkTrajectory.RequiresBistro(
                 _smokeOptions.BenchmarkQualitySequence.Trajectory) ||
             !string.IsNullOrWhiteSpace(
                 _smokeOptions.BistroQualityCaptureDirectory)))
        {
            var contract = new SampleBistroQualityCaptureContract(
                _smokeOptions.BistroQualityCaptureVariant);
            _bistroQualityRuntimeController =
                new SampleBistroQualityRuntimeController(
                    renderer,
                    camera,
                    lightManager,
                    contract);
            if (!string.IsNullOrWhiteSpace(
                    _smokeOptions.BistroQualityCaptureDirectory))
            {
                _bistroQualityCaptureRunner =
                    new SampleBistroQualityCaptureRunner(
                        renderer,
                        _bistroQualityRuntimeController,
                        _smokeOptions.BistroQualityCaptureDirectory,
                        Exit);
                Console.WriteLine(
                    $"Bistro quality capture armed: " +
                    $"variant={contract.Variant}, " +
                    $"frames={SampleBistroQualityCaptureContract.TotalCaptureFrameCount}, " +
                    $"contract={contract.Fingerprint}.");
            }
        }
#if NJULF_EDITOR
        _editorHost = new ImGuiEditorOverlayHost();
        _editorInput = new EditorInputBridge(input, _editorHost);
        RenderingOptions renderingOptions =
            services.GetRequiredService<RenderingOptions>();
        var advancedGiStartup = new AdvancedGiEditorStartupContext(
            renderingOptions.AdvancedGiStartupProfilePath,
            renderingOptions.AdvancedGiStartupProfileStatus,
            renderingOptions.AdvancedGiContentBinding,
            renderingOptions.AdvancedGiPrerequisiteManifestPath,
            renderingOptions.AdvancedGiQualificationManifestPath,
            renderingOptions.AdvancedGiRuntimeEvidenceBundlePath,
            renderingOptions.AdvancedGiCandidateProfilePath);
        _editorController = new EditorController(
            Scene,
            Content!,
            lightManager,
            materialManager,
            _editorHost,
            renderer,
            camera,
            advancedGiStartup,
            requestAdvancedGiRestart: RequestAdvancedGiRestart,
            requestAdvancedGiFeatureRestart:
                RequestAdvancedGiFeatureRestart);
        _editorPanels = new EditorImGuiPanels();
        if (_sceneKind == SampleSceneKind.SponzaPlaza)
            _editorController.SetScenePath(Path.Combine(AppContext.BaseDirectory, "Scenes", "SampleScene.njscene.json"));
        if (_smokeOptions.OpenEditorOnStartup)
        {
            _editorController.SetEnabled(true);
            input.SetCursorMode(CursorMode.Normal);
        }
#endif
        if (_smokeOptions.KhronosMaterialGiRenderedGate is not null)
        {
            SampleKhronosMaterialGiRenderedSceneBuild scene =
                _khronosMaterialGiRenderedScene ??
                throw new InvalidOperationException(
                    "Khronos rendered-gate scene evidence was not created.");
            _khronosMaterialGiRenderedGateRunner =
                new SampleKhronosMaterialGiRenderedGateRunner(
                    scene,
                    renderer,
                    camera,
                    lightManager,
                    () => (WindowWidth, WindowHeight),
                    Exit);
        }
        else if (!string.IsNullOrWhiteSpace(_smokeOptions.MaterialGiCaptureDirectory))
        {
            _materialGiCaptureRunner = new SampleMaterialGiCaptureRunner(
                renderer,
                camera,
                lightManager,
                _smokeOptions.MaterialGiCaptureDirectory,
                () => (WindowWidth, WindowHeight),
                Exit,
                _smokeOptions.AsyncComputeModeOverride ?? AsyncComputeMode.Disabled);
        }
        else if (!string.IsNullOrWhiteSpace(
                     _smokeOptions.VolumetricTemporalCaptureDirectory))
        {
            _performanceScenarioRunner?.SetScenarioUpdateablesEnabled(false);
            _volumetricTemporalCaptureRunner =
                new SampleVolumetricTemporalCaptureRunner(
                    renderer,
                    camera,
                    Scene,
                    _smokeOptions.VolumetricTemporalCaptureDirectory,
                    () => (WindowWidth, WindowHeight),
                    Exit);
        }
        else if (!string.IsNullOrWhiteSpace(
                     _smokeOptions.SponzaTemporalCaptureDirectory))
        {
            _sponzaTemporalCaptureRunner =
                new SampleSponzaTemporalCaptureRunner(
                    renderer,
                    camera,
                    lightManager,
                    _smokeOptions.SponzaTemporalCaptureDirectory,
                    () => (WindowWidth, WindowHeight),
                    Exit);
        }
        else if (!string.IsNullOrWhiteSpace(_smokeOptions.SponzaGiCaptureDirectory))
        {
            _inputController.StartSponzaGiCapture(
                _smokeOptions.SponzaGiCaptureDirectory,
                exitWhenComplete: true,
                captureMode: _smokeOptions.SponzaGiCaptureMode,
                storagePackingModeOverride:
                    _smokeOptions.SimpleDdgiStoragePackingModeOverride,
                sampledAtlasCoverageModeOverride:
                    _smokeOptions.SimpleDdgiSampledAtlasCoverageModeOverride);
        }
        else if (!string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory))
        {
            if (_sceneKind == SampleSceneKind.Bistro)
            {
                // Keep automated screenshots and RenderDoc captures on the same
                // representative Bistro view used when the scene starts or is cycled.
                ApplyBistroCameraPreset(camera);
            }
            else
            {
                SamplePerformanceScenario baselineScenario = ResolveBaselineSnapshotScenario();
                _inputController.ApplyBaselineScenario(baselineScenario);
            }

            // Baseline scenarios restore their authored render settings while
            // positioning the camera. A benchmark capture variant is the final,
            // intentional delta and must therefore be re-applied afterward;
            // otherwise settings such as AO/reflection isolation are silently
            // lost even though the report still names the requested variant.
            if (_smokeOptions.Benchmark.Enabled)
            {
                SampleBenchmarkCaptureVariant.Apply(
                    renderer.Settings,
                    _smokeOptions.Benchmark.CaptureVariant);
            }
            if (ShouldAutoEnableGpuTiming())
                renderer.Settings.Debug.AllowGpuTiming = true;
        }

        _sceneReloadRunner = new SampleSceneReloadRunner(() =>
        {
            Scene.ClearAndDispose();
            LoadSampleScene(meshManager, materialManager, lightManager);
            SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
            ApplySmokeRenderSettings(renderer);
            ConfigureSceneLighting(lightManager);
            ConfigureSceneEnvironment(renderer);
            ConfigureSceneRenderSettings(renderer);
            _inputController?.SetParticleEffects(_sampleVfxEffects);
            SamplePerformanceScenario reloadScenario = ResolveStartupScenario();
            if (reloadScenario != SamplePerformanceScenario.Normal)
            {
                _performanceScenarioRunner!.Apply(reloadScenario);
                _sponzaAtmosphereFrozen = false;
                SampleGlobalIlluminationValidation.ConfigureRenderSettings(renderer.Settings, reloadScenario);
                ApplySmokeRenderSettings(renderer);
                ApplyPerformanceScenarioCamera(camera, reloadScenario);
            }
            ApplySmokeRenderSettings(renderer);
        });
        _smokeRunner = new SampleLifecycleSmokeRunner(
            _smokeOptions,
            ResizeForSmoke,
            _sceneReloadRunner.Reload,
            Exit,
            initialWindowSize: () =>
            {
                Silk.NET.Maths.Vector2D<int> size =
                    Window?.Size ??
                    new Silk.NET.Maths.Vector2D<int>(WindowWidth, WindowHeight);
                return (size.X, size.Y);
            });
        if (_smokeOptions.Mode == SampleSmokeMode.LongRun)
        {
            var workload = new SampleDeterministicLongRunWorkload(
                camera,
                Scene,
                materialManager);
            BindlessHeap bindlessHeap = services.GetRequiredService<BindlessHeap>();
            _longRunMonitor = new SampleLongRunMonitor(
                _smokeOptions,
                workload,
                bindlessHeap.GetDescriptorPressureSnapshot,
                () => SampleRenderSettingsFingerprint.Capture(
                    renderer.Settings));
            Console.WriteLine(
                $"Long-run stability gate armed: warmup={_smokeOptions.LongRunWarmupFrames}, " +
                $"sampleInterval={_smokeOptions.LongRunSampleInterval}, " +
                $"retainedSamples={_smokeOptions.LongRunMaxRetainedSamples}, " +
                $"minutes={_smokeOptions.LongRunMinutes:R}, frames={_smokeOptions.FrameCount}.");
        }
        else if (_smokeOptions.Mode == SampleSmokeMode.QualitySwitch)
        {
            SampleRenderSettingsSnapshot initialSettings =
                SampleRenderSettingsSnapshot.Capture(renderer.Settings);
            _qualitySwitchSmokeRunner = new SampleQualitySwitchSmokeRunner(
                preset =>
                {
                    renderer.Settings.ApplyQualityPreset(preset);
                    _materialGiRolloutBootstrap.Apply(renderer.Settings, Console.Out);
                    SampleLighting.ConfigureRenderSettings(
                        renderer.Settings,
                        ResolveSceneLightingMode());
                    ApplyScenePostOverrides(renderer.Settings);
                },
                () => initialSettings.Restore(renderer.Settings),
                () => renderer.Settings.QualityPreset,
                () => SampleRenderSettingsFingerprint.Capture(renderer.Settings),
                () => GetRendererDeviceIdentity(renderer),
                RecordSmokeOperation,
                Exit);
        }
        else if (_smokeOptions.Mode == SampleSmokeMode.DdgiResidencySwitch)
        {
            SampleRenderSettingsSnapshot initialSettings =
                SampleRenderSettingsSnapshot.Capture(renderer.Settings);
            _ddgiResidencySwitchSmokeRunner =
                new SampleDdgiResidencySwitchSmokeRunner(
                    mode => renderer.Settings.GlobalIllumination
                        .SimpleDdgiProbeResidencyMode = mode,
                    () => initialSettings.Restore(renderer.Settings),
                    () => renderer.Settings.GlobalIllumination
                        .SimpleDdgiProbeResidencyMode,
                    () => SampleRenderSettingsFingerprint.Capture(
                        renderer.Settings),
                    () => GetRendererDeviceIdentity(renderer),
                    RecordSmokeOperation,
                    Exit);
        }
        else if (_smokeOptions.Mode == SampleSmokeMode.TextureHotReload)
        {
            var session = new SampleTextureHotReloadSession(
                services.GetRequiredService<TextureManager>(),
                materialManager,
                Scene,
                renderer);
            _textureHotReloadSmokeRunner = new SampleTextureHotReloadSmokeRunner(
                session,
                () => GetRendererDeviceIdentity(renderer),
                RecordSmokeOperation,
                Exit);
        }
        if (_smokeOptions.Benchmark.Enabled)
        {
            _benchmarkRunner = new SampleBenchmarkRunner(
                _smokeOptions.Benchmark,
                _smokeOptions.PerformanceScenario,
                Exit,
                () => SampleRenderSettingsFingerprint.Capture(
                    renderer.Settings),
                outputPath =>
                {
                    // This is armed only after the final timing sample, so the
                    // readback and debug permission cannot contaminate the
                    // ProductionTiming distribution.
                    renderer.Settings.Debug.Enabled = true;
                    renderer.Settings.Debug.AllowScreenshots = true;
                    return renderer.RequestLinearHdrCapture(outputPath);
                },
                renderer.GetLinearHdrCaptureResult,
                getControlledIsolationSettingsFingerprint: () =>
                    SampleRenderSettingsFingerprint
                        .CaptureDirectionalIsolationFamily(
                            renderer.Settings));
            Console.WriteLine(
                $"Benchmark armed: warmup={_smokeOptions.Benchmark.WarmupFrameCount}, " +
                $"measure={_smokeOptions.Benchmark.MeasureFrameCount}, vsync={(VSync ? "on" : "off")}");
        }
        if (_smokeOptions.BenchmarkQualitySequence.Enabled)
        {
            renderer.Settings.Debug.Enabled = true;
            renderer.Settings.Debug.AllowScreenshots = true;
            _benchmarkQualitySequenceRunner =
                new SampleBenchmarkQualitySequenceRunner(
                    _smokeOptions.BenchmarkQualitySequence,
                    _smokeOptions.PerformanceScenario,
                    Exit,
                    () => SampleRenderSettingsFingerprint.Capture(
                        renderer.Settings),
                    (outputPath, captureToken) =>
                        renderer.RequestLinearHdrCapture(
                            outputPath,
                            captureToken),
                    renderer.GetLinearHdrCaptureResult);
            Console.WriteLine(
                "Benchmark quality sequence armed: " +
                $"trajectory={SampleBenchmarkTrajectory.GetName(_smokeOptions.BenchmarkQualitySequence.Trajectory)}, " +
                $"checkpoints={SampleBenchmarkQualityCheckpointCatalog.GetCheckpointIndices(_smokeOptions.BenchmarkQualitySequence.Trajectory).Count}, " +
                $"extent={SampleBenchmarkQualityCheckpointCatalog.RequiredWidth}x" +
                $"{SampleBenchmarkQualityCheckpointCatalog.RequiredHeight}, " +
                "timingEligible=false.");
        }

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

    private static string GetRendererDeviceIdentity(VulkanRenderer renderer)
    {
        DeviceRequirementReport? device = renderer.SelectedDeviceRequirementReport;
        return device == null
            ? "unknown"
            : $"{device.DeviceName}|{device.VendorId:X8}|{device.DeviceId:X8}|" +
              $"{device.ApiVersion}|{device.DriverVersion}";
    }

    private void RecordSmokeOperation(SampleSmokeOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _smokeRunner?.RecordOperation(
            result.Name,
            result.Status,
            result.FrameIndex,
            result.Detail);
        if (string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase))
            _runtimeSmokeFailure ??= result.Detail ?? $"{result.Name} failed.";
    }

    private SamplePerformanceScenario ResolveStartupScenario()
    {
        if (_smokeOptions.PerformanceScenario != SamplePerformanceScenario.Normal)
            return _smokeOptions.PerformanceScenario;

        return _smokeOptions.Enabled ? SamplePerformanceScenario.Normal : DefaultInteractiveScenario;
    }

    private void ApplySmokeRenderSettings(VulkanRenderer renderer)
    {
        if (_smokeOptions.QualityPresetOverride.HasValue)
        {
            renderer.Settings.ApplyQualityPreset(
                _smokeOptions.QualityPresetOverride.Value);
        }
        RenderBudgetProfileKind? benchmarkBudgetProfile =
            _smokeOptions.Benchmark.Enabled
                ? _smokeOptions.Benchmark.BudgetProfileOverride
                : _smokeOptions.BenchmarkQualitySequence.Enabled
                    ? _smokeOptions.BenchmarkQualitySequence.BudgetProfileOverride
                    : null;
        if (benchmarkBudgetProfile.HasValue)
        {
            renderer.Settings.PerformanceBudgets.ActiveProfile =
                benchmarkBudgetProfile.Value;
        }
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
        if (_smokeOptions.EnableGpuMeshletCounters)
            renderer.Settings.Diagnostics.GpuMeshletCountersEnabled = true;
        if (_smokeOptions.AsyncComputeModeOverride.HasValue)
            renderer.Settings.AsyncCompute.Mode = _smokeOptions.AsyncComputeModeOverride.Value;
        else if (_smokeOptions.EnableAsyncCompute)
            renderer.Settings.AsyncCompute.Mode = AsyncComputeMode.ForceEnabledForValidation;
        if (_smokeOptions.SimpleDdgiSchedulerModeOverride.HasValue)
        {
            renderer.Settings.GlobalIllumination.SimpleDdgiSchedulerMode =
                _smokeOptions.SimpleDdgiSchedulerModeOverride.Value;
        }
        GlobalIlluminationSettings gi = renderer.Settings.GlobalIllumination;
        ApplyAdvancedGiSettings(gi);
        if (_smokeOptions.EnableDdgiContentConformance)
        {
            // Runtime-only authorization: this makes the requested profile
            // modes effective for a controlled capture without persisting or
            // claiming a release-qualified device profile.
            gi.EnableContentDependentFeaturesForConformance();
        }
        if (_smokeOptions.SimpleDdgiStoragePackingModeOverride.HasValue)
        {
            gi.SimpleDdgiStoragePackingMode =
                _smokeOptions.SimpleDdgiStoragePackingModeOverride.Value;
        }
        if (_smokeOptions.SimpleDdgiSampledAtlasCoverageModeOverride.HasValue)
        {
            gi.SimpleDdgiSampledAtlasCoverageMode =
                _smokeOptions.SimpleDdgiSampledAtlasCoverageModeOverride.Value;
            gi.SimpleDdgiSampledAtlasEnabled =
                gi.SimpleDdgiSampledAtlasCoverageMode !=
                    SimpleDdgiSampledAtlasCoverageMode.Disabled;
        }
        if (_smokeOptions.SimpleDdgiProbeResidencyModeOverride.HasValue)
            gi.SimpleDdgiProbeResidencyMode =
                _smokeOptions.SimpleDdgiProbeResidencyModeOverride.Value;
        if (_smokeOptions.SimpleDdgiSparsePhysicalPageBudgetOverride.HasValue)
            gi.SimpleDdgiSparsePhysicalPageBudget =
                _smokeOptions.SimpleDdgiSparsePhysicalPageBudgetOverride.Value;
        if (_smokeOptions.SimpleDdgiSparseMinimumPhysicalPageBudgetOverride.HasValue)
            gi.SimpleDdgiSparseMinimumPhysicalPageBudget =
                _smokeOptions.SimpleDdgiSparseMinimumPhysicalPageBudgetOverride.Value;
        if (_smokeOptions.SimpleDdgiSparseRetentionFramesOverride.HasValue)
            gi.SimpleDdgiSparseRetentionFrames =
                _smokeOptions.SimpleDdgiSparseRetentionFramesOverride.Value;
        if (_smokeOptions.SimpleDdgiSparseMaximumAdmissionsOverride.HasValue)
            gi.SimpleDdgiSparseMaximumAdmissionsPerFrame =
                _smokeOptions.SimpleDdgiSparseMaximumAdmissionsOverride.Value;
        if (_smokeOptions.SimpleDdgiSparseMaximumReceiverFeedbackOverride.HasValue)
            gi.SimpleDdgiSparseMaximumReceiverFeedbackRequests =
                _smokeOptions.SimpleDdgiSparseMaximumReceiverFeedbackOverride.Value;
        if (_smokeOptions.SimpleDdgiSparseInactiveRetryFramesOverride.HasValue)
            gi.SimpleDdgiSparseInactiveRetryFrames =
                _smokeOptions.SimpleDdgiSparseInactiveRetryFramesOverride.Value;
        renderer.Settings.AsyncCompute.ForceValidationPath = _smokeOptions.AsyncComputeValidationPath;
        if (_smokeOptions.AsyncComputeValidationPath is { } validationPath)
        {
            // An explicit validation selector is an opt-in to exercise that atomic path. This
            // is important for paths such as GPU particles whose production toggles are
            // deliberately conservative by default; it does not bypass the renderer's feature,
            // capability, concrete-resource, or validation-failure gates.
            switch (validationPath)
            {
                case AsyncComputePath.SimpleDdgiUpdate:
                    renderer.Settings.AsyncCompute.SimpleDdgiUpdateEnabled = true;
                    break;
                case AsyncComputePath.FarFieldClipmapBake:
                    renderer.Settings.AsyncCompute.FarFieldClipmapBakeEnabled = true;
                    break;
                case AsyncComputePath.AmbientOcclusionBlur:
                    renderer.Settings.AsyncCompute.AmbientOcclusionBlurEnabled = true;
                    break;
                case AsyncComputePath.HiZBuild:
                    renderer.Settings.AsyncCompute.HiZBuildEnabled = true;
                    break;
                case AsyncComputePath.Fog:
                    renderer.Settings.AsyncCompute.FogEnabled = true;
                    break;
                case AsyncComputePath.Bloom:
                    renderer.Settings.AsyncCompute.BloomEnabled = true;
                    break;
                case AsyncComputePath.GpuParticles:
                    renderer.Settings.AsyncCompute.GpuParticlesEnabled = true;
                    renderer.Settings.Particles.SimulationMode = ParticleSimulationMode.Gpu;
                    break;
            }
        }
        if (_smokeOptions.EnableFarFieldClipmap)
            renderer.Settings.GlobalIllumination.FarFieldClipmapEnabled = true;
        if (_smokeOptions.EnableFarFieldForceAll)
            renderer.Settings.GlobalIllumination.FarFieldForceAll = true;

        renderer.Settings.Transparency.Mode = _smokeOptions.TransparencyMode;
        if (_smokeOptions.FogDebugViewOverride.HasValue)
        {
            renderer.Settings.Fog.DebugView =
                _smokeOptions.FogDebugViewOverride.Value;
        }
        if (_smokeOptions.FogDebugProjectionOverride.HasValue)
        {
            renderer.Settings.Fog.Volumetric.DebugProjection =
                _smokeOptions.FogDebugProjectionOverride.Value;
        }
        if (_smokeOptions.FogDebugSliceOverride.HasValue)
        {
            renderer.Settings.Fog.Volumetric.DebugSlice =
                _smokeOptions.FogDebugSliceOverride.Value;
            if (!_smokeOptions.FogDebugProjectionOverride.HasValue)
            {
                renderer.Settings.Fog.Volumetric.DebugProjection =
                    FogDebugProjection.Slice;
            }
        }
        _materialGiRolloutBootstrap.Apply(renderer.Settings, Console.Out);
        if (_smokeOptions.TailDdgiLongSoak)
        {
            SampleTailDdgiLongSoakProfile.Apply(renderer.Settings);
        }
        else if (_smokeOptions.Benchmark.Enabled ||
                 _smokeOptions.BenchmarkQualitySequence.Enabled)
        {
            // A controlled timing window must not let completed GPU timings
            // change the following frame's update population. Adaptive DDGI
            // budgeting remains available in normal rendering and explicit
            // experiments, but the canonical benchmark uses the authored cap.
            renderer.Settings.GlobalIllumination.DdgiAdaptiveBudgetingEnabled = false;
            // Particle simulation is normally wall-clock driven. Lock it to the
            // benchmark timestep so graphics/async image captures compare the
            // same scene state rather than two different particle trajectories.
            renderer.Settings.Particles.FixedSimulationDeltaSeconds =
                BenchmarkSimulationDeltaSeconds;
            SampleBenchmarkCaptureVariant.Apply(
                renderer.Settings,
                _smokeOptions.Benchmark.Enabled
                    ? _smokeOptions.Benchmark.CaptureVariant
                    : _smokeOptions.BenchmarkQualitySequence.CaptureVariant);
        }

        ApplyScenePostOverrides(renderer.Settings);
    }

    private void ApplyPreInitializationRenderSettings(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_smokeOptions.QualityPresetOverride.HasValue)
        {
            settings.ApplyQualityPreset(
                _smokeOptions.QualityPresetOverride.Value);
        }
        if (_smokeOptions.SceneKind == SampleSceneKind.VfxShowcase)
        {
            _vfxVolumetricDemoOverride.Enter(settings);
            SampleVfxShowcaseScene.ConfigurePreInitializationSettings(
                settings,
                _smokeOptions.QualityPresetOverride);
        }
        else if (_smokeOptions.SceneKind == SampleSceneKind.Bistro)
        {
            // C5 admission allocates immutable startup resources. Establish
            // the scene's enabled policy before renderer construction;
            // explicit smoke/CLI overrides below retain final authority.
            SampleBistroGlobalIlluminationProfile
                .ConfigurePostAdvancedGiRollout(settings);
        }
        else if (_smokeOptions.SceneKind == SampleSceneKind.SponzaPlaza)
        {
            SampleSponzaGlobalIlluminationProfile
                .ConfigurePostAdvancedGiRollout(
                    settings,
                    _smokeOptions.SponzaFixtureMode ==
                        SampleSponzaFixtureMode.C5ResidualValidation);
        }
        if (_smokeOptions.SimpleDdgiSchedulerModeOverride.HasValue)
        {
            settings.GlobalIllumination.SimpleDdgiSchedulerMode =
                _smokeOptions.SimpleDdgiSchedulerModeOverride.Value;
        }
        if (_smokeOptions.SimpleDdgiStoragePackingModeOverride.HasValue)
        {
            settings.GlobalIllumination.SimpleDdgiStoragePackingMode =
                _smokeOptions.SimpleDdgiStoragePackingModeOverride.Value;
        }
        ApplyAdvancedGiSettings(settings.GlobalIllumination);
        ApplyScenePostOverrides(settings);
    }

    private void ApplyAdvancedGiSettings(GlobalIlluminationSettings gi)
    {
        ArgumentNullException.ThrowIfNull(gi);
        if (_smokeOptions.SimpleDdgiReceiverFeedbackModeOverride.HasValue)
        {
            gi.SimpleDdgiReceiverFeedbackMode =
                _smokeOptions.SimpleDdgiReceiverFeedbackModeOverride.Value;
        }
        if (_smokeOptions.DdgiOpacityMicromapModeOverride.HasValue)
        {
            gi.DdgiOpacityMicromapMode =
                _smokeOptions.DdgiOpacityMicromapModeOverride.Value;
        }
        if (_smokeOptions.SimpleDdgiDirectionalGuidingModeOverride.HasValue)
        {
            gi.SimpleDdgiDirectionalGuidingMode =
                _smokeOptions.SimpleDdgiDirectionalGuidingModeOverride.Value;
        }
        if (_smokeOptions.GiCausticModeOverride.HasValue)
            gi.GiCausticMode = _smokeOptions.GiCausticModeOverride.Value;
        if (_smokeOptions.SimpleDdgiNearFieldResidualModeOverride.HasValue)
        {
            gi.SimpleDdgiNearFieldResidualMode =
                _smokeOptions.SimpleDdgiNearFieldResidualModeOverride.Value;
        }

        if (_smokeOptions.SimpleDdgiReceiverFeedbackQualificationId is { } b1Id)
            gi.SimpleDdgiReceiverFeedbackQualificationId = b1Id;
        if (_smokeOptions.DdgiOpacityMicromapQualificationId is { } c1Id)
            gi.DdgiOpacityMicromapQualificationId = c1Id;
        if (_smokeOptions.SimpleDdgiDirectionalGuidingQualificationId is { } c3Id)
            gi.SimpleDdgiDirectionalGuidingQualificationId = c3Id;
        if (_smokeOptions.GiCausticQualificationId is { } c4Id)
            gi.GiCausticQualificationId = c4Id;
        if (_smokeOptions.SimpleDdgiNearFieldResidualQualificationId is { } c5Id)
            gi.SimpleDdgiNearFieldResidualQualificationId = c5Id;
    }

    protected override void OnResize(int width, int height)
    {
        _framebufferResizeRevision++;
        base.OnResize(width, height);
    }

    protected override void Update(float deltaTime)
    {
        float simulationDeltaTime = ResolveSimulationDeltaTime(
            deltaTime,
            _smokeOptions.UsesDeterministicSimulationClock);
        ApplyPendingSmokeResize();
        ObserveSmokeWindowMutation();
        _smokeRunner?.OnUpdate(_drawnFrames);
#if NJULF_EDITOR
        UpdateEditor(simulationDeltaTime);
#endif
        if (_materialGiCaptureRunner == null &&
            _khronosMaterialGiRenderedGateRunner == null &&
            _bistroQualityCaptureRunner == null &&
            _sponzaTemporalCaptureRunner == null &&
            _volumetricTemporalCaptureRunner == null &&
            !_smokeOptions.Benchmark.Enabled &&
            !_smokeOptions.BenchmarkQualitySequence.Enabled)
            _inputController?.Update(
                simulationDeltaTime,
                WindowWidth,
                WindowHeight);
        _materialGiCaptureRunner?.PrepareFrame();
        _khronosMaterialGiRenderedGateRunner?.PrepareFrame();
        if (_smokeOptions.Mode == SampleSmokeMode.LongRun)
            _longRunMonitor?.PrepareFrame(_drawnFrames);

        if (GetModelSceneManifest(_sceneKind) is { RotationSpeed: not 0f } assetManifest)
        {
            _modelRotation +=
                simulationDeltaTime * assetManifest.RotationSpeed;
            _sceneLoader?.ApplyModelRotation(_modelRotation);
        }

        ApplySponzaScenarioFrameControls();
        ApplyBenchmarkDynamicScenarioFrameControls();
        ApplyBenchmarkCameraScenarioFrameControls();
        ApplyBenchmarkNamedTrajectoryFrameControls();
        if (_bistroQualityCaptureRunner != null)
            _bistroQualityCaptureRunner.PrepareFrame(_drawnFrames);
        else if (_benchmarkRunner != null &&
                 SampleBenchmarkTrajectory.RequiresBistro(
                     _smokeOptions.Benchmark.Trajectory) &&
                 !_benchmarkRunner.HoldTrajectoryForPostMeasurementEvidence)
        {
            _bistroQualityRuntimeController?.PrepareFrame(
                _benchmarkRunner.ResolveBistroControllerFrameIndexForNextRender(
                    _drawnFrames));
        }
        else if (_benchmarkQualitySequenceRunner != null &&
                 SampleBenchmarkTrajectory.RequiresBistro(
                     _smokeOptions.BenchmarkQualitySequence.Trajectory))
        {
            _bistroQualityRuntimeController?.PrepareFrame(
                _benchmarkQualitySequenceRunner
                    .ResolveBistroControllerFrameIndexForNextRender(
                        _drawnFrames));
        }
        else if (_benchmarkRunner?.HoldTrajectoryForPostMeasurementEvidence != true)
            _bistroQualityRuntimeController?.PrepareFrame(_drawnFrames);

        base.Update(simulationDeltaTime);
        ApplyBenchmarkActivationPreDrawControls();
        if (_benchmarkQualitySequenceRunner != null)
        {
            var camera = Camera as FirstPersonCamera ??
                throw new InvalidOperationException(
                    "Benchmark quality sequence requires a FirstPersonCamera.");
            _benchmarkQualitySequenceRunner.PrepareFrame(
                _drawnFrames,
                CaptureBenchmarkCameraPose(camera),
                SampleBenchmarkTrajectory.RequiresBistro(
                    _smokeOptions.BenchmarkQualitySequence.Trajectory)
                    ? _bistroQualityRuntimeController?.LastAppliedState
                    : null);
        }
        _sponzaTemporalCaptureRunner?.PrepareFrame(
            WindowWidth,
            WindowHeight);
        _volumetricTemporalCaptureRunner?.PrepareFrame();
    }

    private void ApplySponzaScenarioFrameControls()
    {
        SamplePerformanceScenario scenario = _performanceScenarioRunner?.CurrentScenario ??
            _smokeOptions.PerformanceScenario;
        if (scenario != SamplePerformanceScenario.GiSponzaFreezeAfterAtmosphereStep ||
            Renderer is not VulkanRenderer renderer || _sponzaAtmosphereFrozen)
            return;

        // Let the first rendered frame expose one quantized atmosphere update, then hold the
        // exact owner generation still so the convergence and reflection publication gates can be
        // measured without an additional CPU-side scene scan.
        if (_drawnFrames < 1)
            return;
        renderer.Settings.Environment.AnimateTimeOfDay = false;
        _sponzaAtmosphereFrozen = true;
    }

    private void ApplyBenchmarkDynamicScenarioFrameControls()
    {
        SamplePerformanceScenario scenario =
            _performanceScenarioRunner?.CurrentScenario ??
            _smokeOptions.PerformanceScenario;
        if (_benchmarkDynamicScenarioFrozen ||
            !ShouldFreezeBenchmarkDynamicScenario(
                scenario,
                _smokeOptions.Benchmark.Enabled ||
                _smokeOptions.BenchmarkQualitySequence.Enabled,
                _drawnFrames))
        {
            return;
        }

        _performanceScenarioRunner?.SetScenarioUpdateablesEnabled(false);
        _benchmarkDynamicScenarioFrozen = true;
        Console.WriteLine(
            $"Benchmark disturbance complete: scenario={scenario}, " +
            $"motionFrames={BenchmarkDynamicScenarioDisturbanceFrameCount}; " +
            "holding scene state for DDGI recovery and certification.");
    }

    internal static bool ShouldFreezeBenchmarkDynamicScenario(
        SamplePerformanceScenario scenario,
        bool benchmarkEnabled,
        int drawnFrames) =>
        benchmarkEnabled &&
        drawnFrames >= BenchmarkDynamicScenarioDisturbanceFrameCount &&
        scenario is SamplePerformanceScenario.GiMovingPointLight or
            SamplePerformanceScenario.GiMovingRigidObject;

    internal static float ResolveSimulationDeltaTime(
        float hostDeltaTime,
        bool benchmarkEnabled) =>
        benchmarkEnabled
            ? BenchmarkSimulationDeltaSeconds
            : hostDeltaTime;

    private void ApplyBenchmarkCameraScenarioFrameControls()
    {
        SamplePerformanceScenario scenario =
            _performanceScenarioRunner?.CurrentScenario ??
            _smokeOptions.PerformanceScenario;
        if (!(_smokeOptions.Benchmark.Enabled ||
              _smokeOptions.BenchmarkQualitySequence.Enabled) ||
            scenario != SamplePerformanceScenario.GiFastTraversalTeleport ||
            Camera is not FirstPersonCamera camera)
        {
            return;
        }

        (CoreVector3 position, float yaw, float pitch) =
            ResolveFastTraversalBenchmarkCameraPose(_drawnFrames);
        camera.Position = position;
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.Update();

        if (_drawnFrames is 6 or 18 or 23)
        {
            Console.WriteLine(
                $"Benchmark camera disturbance: scenario={scenario}, " +
                $"frame={_drawnFrames}, position={position}, yaw={yaw:F3}.");
        }
    }

    private void ApplyBenchmarkActivationPreDrawControls()
    {
        if (_benchmarkActivationPreparedDrawFrame == _drawnFrames ||
            Renderer is not VulkanRenderer renderer)
        {
            return;
        }

        bool timing = _benchmarkRunner != null;
        bool quality = !timing && _benchmarkQualitySequenceRunner != null;
        string activation = timing
            ? _smokeOptions.Benchmark.Activation
            : _smokeOptions.BenchmarkQualitySequence.Activation;
        SampleBenchmarkTrajectoryKind trajectory = timing
            ? _smokeOptions.Benchmark.Trajectory
            : _smokeOptions.BenchmarkQualitySequence.Trajectory;
        bool sponza = SampleBenchmarkTrajectory.RequiresSponza(trajectory);
        bool active = SampleBenchmarkActivation.Normalize(activation) !=
            SampleBenchmarkActivation.None;
        if (!(timing || quality) || (!sponza && !active))
        {
            return;
        }

        bool evidenceFrame;
        bool holdFrame = false;
        int routeFrameIndex;
        if (timing)
        {
            evidenceFrame = _benchmarkRunner!
                .TryGetMeasurementFrameIndexForNextRender(
                    out routeFrameIndex);
            if (!evidenceFrame)
            {
                holdFrame = _benchmarkRunner
                    .HoldTrajectoryForPostMeasurementEvidence;
                routeFrameIndex = holdFrame
                    ? _smokeOptions.Benchmark.MeasureFrameCount - 1
                    : _benchmarkRunner
                        .ResolveTrajectoryFrameIndexForNextRender(_drawnFrames);
            }
        }
        else
        {
            evidenceFrame = _benchmarkQualitySequenceRunner!
                .TryGetActivationFrameIndexForNextRender(
                    out routeFrameIndex);
            if (!evidenceFrame)
            {
                routeFrameIndex = _benchmarkQualitySequenceRunner
                    .ResolveTrajectoryFrameIndexForNextRender(_drawnFrames);
                holdFrame = _benchmarkQualitySequenceRunner
                    .HoldTrajectoryForReadbackDrain;
            }
        }

        try
        {
            ApplyBenchmarkActivationPreDrawControlsCore(
                renderer,
                timing,
                evidenceFrame,
                holdFrame,
                routeFrameIndex,
                activation);
        }
        catch (Exception exception)
        {
            if (quality)
            {
                _benchmarkQualitySequenceRunner!
                    .RecordPreDrawActivationFailure(exception);
            }
            else
            {
                _benchmarkRunner!.RecordPreDrawActivationFailure(exception);
            }
        }
        _benchmarkActivationPreparedDrawFrame = _drawnFrames;
    }

    private void ApplyBenchmarkActivationPreDrawControlsCore(
        VulkanRenderer renderer,
        bool timing,
        bool evidenceFrame,
        bool holdFrame,
        int routeFrameIndex,
        string activation)
    {
        SampleBenchmarkTrajectoryKind trajectory = timing
            ? _smokeOptions.Benchmark.Trajectory
            : _smokeOptions.BenchmarkQualitySequence.Trajectory;
        if (SampleBenchmarkTrajectory.RequiresSponza(trajectory))
        {
            if (timing)
            {
                _benchmarkRunner!.PrepareSponzaSceneAnimationFrame(
                    Scene,
                    routeFrameIndex,
                    evidenceFrame,
                    holdFrame);
            }
            else
            {
                bool routeEvidence =
                    _benchmarkQualitySequenceRunner!.RouteStarted &&
                    !holdFrame;
                _benchmarkQualitySequenceRunner
                    .PrepareSponzaSceneAnimationFrame(
                        Scene,
                        routeFrameIndex,
                        routeEvidence ? routeFrameIndex : null,
                        holdFrame);
            }
        }

        bool activationRequestAllowed = timing ||
            _benchmarkQualitySequenceRunner?.CanIssueActivationRequest == true;
        if (evidenceFrame && activationRequestAllowed &&
            SampleBenchmarkActivation.ShouldRequestReflectionRecapture(
                activation,
                routeFrameIndex))
        {
            ReflectionProbeRecaptureRequestSummary admission =
                renderer.RequestReflectionProbeRecapture(
                    "benchmark-authored-manual-recapture");
            if (timing)
            {
                _benchmarkRunner!.RecordReflectionActivationRequest(
                    routeFrameIndex,
                    admission);
            }
            else
            {
                _benchmarkQualitySequenceRunner!
                    .RecordReflectionActivationRequest(
                        routeFrameIndex,
                        admission);
            }
        }
    }

    private void ApplyBenchmarkNamedTrajectoryFrameControls()
    {
        bool timingEnabled = _smokeOptions.Benchmark.Enabled;
        bool qualityEnabled = _smokeOptions.BenchmarkQualitySequence.Enabled;
        SampleBenchmarkTrajectoryKind trajectory = timingEnabled
            ? _smokeOptions.Benchmark.Trajectory
            : _smokeOptions.BenchmarkQualitySequence.Trajectory;
        SampleBistroQualityCaptureVariant bistroVariant = timingEnabled
            ? _smokeOptions.Benchmark.TrajectoryBistroVariant
            : _smokeOptions.BenchmarkQualitySequence.TrajectoryBistroVariant;
        if (!(timingEnabled || qualityEnabled) ||
            !SampleBenchmarkTrajectory.RequiresSponza(trajectory) ||
            _benchmarkRunner?.HoldTrajectoryForPostMeasurementEvidence == true ||
            Camera is not FirstPersonCamera camera)
        {
            return;
        }

        int trajectoryFrame =
            timingEnabled
                ? _benchmarkRunner?.ResolveTrajectoryFrameIndexForNextRender(
                    _drawnFrames) ??
                  SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                      trajectory,
                      _drawnFrames)
                : _benchmarkQualitySequenceRunner
                    ?.ResolveTrajectoryFrameIndexForNextRender(_drawnFrames) ??
                  SampleBenchmarkTrajectory.GetWarmupFrameIndex(
                      trajectory,
                      _drawnFrames);
        SampleBenchmarkCameraPose pose =
            SampleBenchmarkTrajectory.ResolveCamera(
                trajectory,
                trajectoryFrame,
                bistroVariant) ??
            throw new InvalidOperationException(
                "A named Sponza benchmark trajectory did not resolve a camera pose.");
        camera.Position = pose.Position;
        camera.Yaw = pose.Yaw;
        camera.Pitch = pose.Pitch;
        camera.FieldOfView = pose.FieldOfView;
        camera.NearPlane = pose.NearPlane;
        camera.FarPlane = pose.FarPlane;
        camera.Update();
    }

    internal static (CoreVector3 Position, float Yaw, float Pitch)
        ResolveFastTraversalBenchmarkCameraPose(int drawnFrames)
    {
        // The 35 m separation is one complete 28x1.25 m near-ring width.
        // Frames 6-17 stream the path incrementally, then frames 18 and 23
        // exercise zero-overlap ring remaps in both directions. The final pose
        // remains fixed so readiness and timing describe recovered GI.
        var start = new CoreVector3(0.0f, 1.65f, 6.5f);
        var arrival = new CoreVector3(0.0f, 1.65f, -28.5f);
        const float pitch = -0.04f;
        if (drawnFrames < 6)
            return (start, 0.0f, pitch);
        if (drawnFrames < 18)
        {
            float t = Math.Clamp((drawnFrames - 5) / 12.0f, 0.0f, 1.0f);
            return (CoreVector3.Lerp(start, arrival, t), 0.0f, pitch);
        }
        if (drawnFrames < 23)
            return (start, 0.0f, pitch);
        return (arrival, MathF.PI, pitch);
    }

    internal static SampleBenchmarkCameraPose CaptureBenchmarkCameraPose(
        FirstPersonCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new SampleBenchmarkCameraPose(
            "pre-draw-live",
            camera.Position,
            camera.Yaw,
            camera.Pitch,
            camera.FieldOfView,
            camera.NearPlane,
            camera.FarPlane);
    }

    protected override void Draw()
    {
        if (Renderer == null)
            throw new InvalidOperationException("Renderer is not available during Draw().");
        if (Camera == null)
            throw new InvalidOperationException("Camera is not available during Draw().");

#if NJULF_EDITOR
        if (_editorController?.Enabled == true)
            _editorHost!.SubmitFrame((VulkanRenderer)Renderer);
        else
            _editorHost?.ClearRenderer((VulkanRenderer)Renderer);
#endif
        Renderer.DrawScene(Scene, Camera);
        if (Renderer is VulkanRenderer temporalCaptureRenderer)
        {
            _sponzaTemporalCaptureRunner?.OnFrameRendered(
                temporalCaptureRenderer.LastDiagnostics);
            _volumetricTemporalCaptureRunner?.OnFrameRendered(
                temporalCaptureRenderer.LastDiagnostics);
        }
        _materialGiCaptureRunner?.OnFrameRendered();
        _khronosMaterialGiRenderedGateRunner?.OnFrameRendered();
        _diagnosticsReporter?.PrintFirstFrameDiagnostics(Renderer);
        if (Camera is FirstPersonCamera firstPersonCamera)
            _diagnosticsReporter?.PrintMovementFrameDiagnostics(Renderer, firstPersonCamera);

        CaptureBaselineSnapshotIfRequested();
        if (Renderer is VulkanRenderer benchmarkRenderer)
        {
            _benchmarkQualitySequenceRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics);
            if (_smokeOptions.Mode == SampleSmokeMode.LongRun)
            {
                _longRunMonitor?.Sample(
                    _drawnFrames,
                    benchmarkRenderer.LastDiagnostics,
                    benchmarkRenderer.LastBudgetSnapshot);
            }
            _qualitySwitchSmokeRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics,
                benchmarkRenderer.LastBudgetSnapshot);
            _ddgiResidencySwitchSmokeRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics);
            _textureHotReloadSmokeRunner?.OnFrameRendered(_drawnFrames);
            _bistroQualityCaptureRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics);
            _benchmarkRunner?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics,
                benchmarkRenderer.LastBudgetSnapshot);
            _inputController?.OnFrameRendered(
                _drawnFrames,
                benchmarkRenderer.LastDiagnostics,
                benchmarkRenderer.LastBudgetSnapshot);
        }

        _smokeRunner?.OnFrameRendered(_drawnFrames);
        _drawnFrames++;
    }

    private bool CaptureDiagnosticScreenshot(string path)
    {
        if (Window == null)
        {
            return false;
        }

        if (SampleWindowCapture.TryCaptureClientArea(Window, path, out string error))
        {
            Console.WriteLine($"Diagnostic screenshot stored: {path}");
            return true;
        }

        Console.WriteLine($"Diagnostic screenshot failed: {error}");
        return false;
    }

    private bool ShouldAutoEnableGpuTiming()
    {
        return _smokeOptions.EnableGpuTiming ||
            _smokeOptions.Benchmark.Enabled ||
            _smokeOptions.BenchmarkQualitySequence.Enabled ||
            _smokeOptions.Mode is SampleSmokeMode.QualitySwitch or SampleSmokeMode.LongRun;
    }

    protected override void Unload()
    {
        _bistroQualityRuntimeController?.Restore();
        _materialGiCaptureRunner?.CancelIfIncomplete(
            _startupFailure ?? "The application closed before the material/GI capture completed.");
        _khronosMaterialGiRenderedGateRunner?.CancelIfIncomplete(
            _startupFailure ?? "The application closed before the Khronos rendered gate completed.");
        VulkanRenderer? renderer = Renderer as VulkanRenderer;
        RendererDiagnostics diagnostics =
            renderer?.LastDiagnostics ?? RendererDiagnostics.Empty;
        if (_longRunMonitor != null)
        {
            try
            {
                SampleLongRunCompletion completion = _longRunMonitor.Complete();
                _smokeRunner?.RecordOperation(
                    "long-run-stability",
                    completion.Passed ? "passed" : "failed",
                    Math.Max(0, _drawnFrames - 1),
                    $"report='{completion.ReportPath}'" +
                    (completion.Failure == null ? string.Empty : $", {completion.Failure}"));
                _smokeRunner?.RecordOperation(
                    "device-loss-recovery",
                    completion.Report.DeviceLossRecovery.Status,
                    Math.Max(0, _drawnFrames - 1),
                    completion.Report.DeviceLossRecovery.Reason);
                if (!completion.Passed)
                    _runtimeSmokeFailure ??= completion.Failure;
            }
            catch (Exception ex)
            {
                _runtimeSmokeFailure ??= $"Long-run report finalization failed: {ex.Message}";
            }
        }

        IReadOnlyList<SampleSmokeOperationResult> smokeOperations =
            _smokeRunner?.Results ?? Array.Empty<SampleSmokeOperationResult>();
        if (SampleHealthReportEvaluation.FindFirstFailedOperation(smokeOperations) is { } failedOperation)
        {
            _runtimeSmokeFailure ??=
                failedOperation.Detail ?? $"{failedOperation.Name} failed.";
        }
        else if (SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
                     _smokeOptions,
                     smokeOperations,
                     _drawnFrames) is { } incompleteOperation)
        {
            _runtimeSmokeFailure ??=
                incompleteOperation.Detail ?? "Smoke execution was incomplete.";
        }
        if (_smokeOptions.Benchmark.Enabled)
        {
            if (_benchmarkRunner?.Report is not { } benchmarkReport)
            {
                _runtimeSmokeFailure ??=
                    "Benchmark closed before its complete measurement report was published.";
            }
            else
            {
                SampleBenchmarkGateEvaluation benchmarkGate =
                    SampleBenchmarkGateEvaluation.Evaluate(benchmarkReport);
                if (!benchmarkGate.Passed)
                    _runtimeSmokeFailure ??= benchmarkGate.Failure;
            }
        }
        if (_smokeOptions.BenchmarkQualitySequence.Enabled)
        {
            if (_benchmarkQualitySequenceRunner?.Report is not { } qualityReport)
            {
                _runtimeSmokeFailure ??=
                    "Benchmark quality sequence closed before its authenticated report was published.";
            }
            else if (!qualityReport.Passed ||
                     qualityReport.TimingEligible ||
                     qualityReport.ProductionTiming)
            {
                _runtimeSmokeFailure ??= qualityReport.Failures.Count == 0
                    ? "Benchmark quality-sequence evidence was invalid or timing-eligible."
                    : string.Join(" ", qualityReport.Failures);
            }
        }
        if (!string.IsNullOrWhiteSpace(
                _smokeOptions.VolumetricTemporalCaptureDirectory))
        {
            if (_volumetricTemporalCaptureRunner?.Report is not { } fogReport)
            {
                _runtimeSmokeFailure ??=
                    "Volumetric temporal capture closed before its quality report was published.";
            }
            else if (!fogReport.Passed)
            {
                _runtimeSmokeFailure ??=
                    "Volumetric temporal capture failed its quality gates.";
            }
        }

        string? failure = _startupFailure ?? _runtimeSmokeFailure;
        string status = failure == null ? "passed" : "failed";
        SampleHealthReportEvaluation healthEvaluation =
            SampleHealthReportEvaluation.Evaluate(diagnostics);
        bool volumetricCapturePassed =
            _volumetricTemporalCaptureRunner?.Report?.Passed == true;
        if (_smokeOptions.Enabled && !volumetricCapturePassed &&
            healthEvaluation.FirstGiDiagnosticError is { } giError)
        {
            status = "failed";
            failure ??=
                $"GI diagnostic {giError.Code} reported an error for " +
                $"'{giError.Feature}': {giError.Message}";
        }
        if (_smokeOptions.FailOnValidationMessage &&
            (diagnostics.ValidationWarningMessageCount > 0 ||
             diagnostics.ValidationErrorMessageCount > 0))
        {
            status = "failed";
            failure ??=
                $"Vulkan validation emitted " +
                $"{diagnostics.ValidationWarningMessageCount} warning message(s) and " +
                $"{diagnostics.ValidationErrorMessageCount} error message(s).";
        }
        if (failure != null)
            Environment.ExitCode = 1;
        try
        {
            _healthReportWriter.Write(
                _smokeOptions,
                _startupLog.Path,
                smokeOperations,
                diagnostics,
                status,
                failure,
                renderer?.Settings);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine(
                $"Required health report publication failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

#if NJULF_EDITOR
        _editorInput?.Dispose();
        _editorInput = null;

        ImGuiEditorOverlayHost? editorHost = _editorHost;
        _editorHost = null;
        _editorController = null;
        _editorPanels = null;
        editorHost?.Dispose();
#endif
        _startupLog.Dispose();
        base.Unload();
    }

#if NJULF_EDITOR
    private void RequestAdvancedGiRestart(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (_requestedAdvancedGiStartupProfilePath is not null ||
            _requestedAdvancedGiFeatureSelection.HasValue)
            return;
        _requestedAdvancedGiStartupProfilePath = Path.GetFullPath(profilePath);
        Console.WriteLine(
            $"Advanced GI cold restart requested: " +
            _requestedAdvancedGiStartupProfilePath);
        Exit();
    }

    private void RequestAdvancedGiFeatureRestart(
        AdvancedGiFeatureSelection selection)
    {
        if (_requestedAdvancedGiFeatureSelection.HasValue ||
            _requestedAdvancedGiStartupProfilePath is not null)
        {
            return;
        }
        _requestedAdvancedGiFeatureSelection = selection;
        Console.WriteLine(
            "Advanced GI feature change requested; rebuilding the renderer.");
        Exit();
    }

    private void UpdateEditor(float deltaTime)
    {
        if (Input is not InputManager input || _editorController == null || _editorHost == null || _editorPanels == null)
            return;

        bool controlDown = input.IsPhysicalKeyDown(Key.ControlLeft) || input.IsPhysicalKeyDown(Key.ControlRight);
        bool toggleDown = controlDown && input.IsPhysicalKeyDown(Key.Keypad1);
        if (toggleDown && !_editorTogglePressed)
        {
            _editorController.Toggle();
            input.SetCursorMode(_editorController.Enabled ? CursorMode.Normal : CursorMode.Raw);
        }
        _editorTogglePressed = toggleDown;
        if (!_editorController.Enabled)
            return;

        _editorHost.BeginFrame(
            new System.Numerics.Vector2(Math.Max(1, WindowWidth), Math.Max(1, WindowHeight)),
            System.Numerics.Vector2.One,
            Math.Max(deltaTime, 1f / 1000f));
        _editorPanels.Render(_editorController);

        bool saveDown = controlDown && input.IsPhysicalKeyDown(Key.S);
        if (saveDown && !_editorSavePressed && _editorController.ScenePath != null)
            _editorController.Save();
        _editorSavePressed = saveDown;

        bool pickDown = input.IsMouseButtonDown((int)MouseButton.Left);
        if (pickDown && !_editorPickPressed)
            _editorController.TryPick((FirstPersonCamera)Camera!, input.MousePosition, new Njulf.Core.Math.Vector2(WindowWidth, WindowHeight));
        _editorPickPressed = pickDown;
        _editorController.UpdateSelectionHighlight();
    }
#endif

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

        Model Finish(Model model)
        {
            SampleReflectionPolicy.EnsureProbeFree(Scene);
            return model;
        }

        if (_sceneKind == SampleSceneKind.MaterialShowcase)
        {
            _sceneLoader = null;
            if (_smokeOptions.KhronosMaterialGiRenderedGate is { } gateOptions)
            {
                try
                {
                    ContentManager contentManager =
                        Services?.GetRequiredService<ContentManager>() ??
                        throw new InvalidOperationException(
                            "Khronos rendered gate requires the shipping ContentManager.");
                    _khronosMaterialGiRenderedScene =
                        SampleKhronosMaterialGiRenderedSceneBuilder.Build(
                            gateOptions,
                            Scene,
                            contentManager,
                            materialManager);
                    meshManager.CompactStaticBuffers();
                    Console.WriteLine(
                        $"Official Khronos Material/GI scene: " +
                        $"assets={_khronosMaterialGiRenderedScene.Assets.Count}, " +
                        $"objects={_khronosMaterialGiRenderedScene.RenderObjectCount}, " +
                        $"unlitObjects={_khronosMaterialGiRenderedScene.RuntimeUnlitRenderObjectCount}, " +
                        $"packageSha256={_khronosMaterialGiRenderedScene.PackageSha256}.");
                    return Finish(new Model { Name = "Official Khronos Material/GI Conformance" });
                }
                catch (Exception exception)
                {
                    string failure =
                        $"Khronos rendered-gate preflight failed: " +
                        $"{exception.GetType().Name}: {exception.Message}";
                    SampleKhronosMaterialGiRenderedGateReportPublisher.TryWriteFailed(
                        gateOptions,
                        failure,
                        _khronosMaterialGiRenderedScene);
                    Environment.ExitCode = 1;
                    throw;
                }
            }
            if (SampleMaterialGiConformanceScene.IsCaptureSceneRequested(_smokeOptions))
            {
                TextureManager textureManager = Services?.GetRequiredService<TextureManager>()
                    ?? throw new InvalidOperationException(
                        "Material/GI conformance capture requires the renderer TextureManager.");
                SampleMaterialGiConformanceSceneBuildSummary summary =
                    SampleMaterialGiConformanceScene.Configure(
                        Scene,
                        meshManager,
                        materialManager,
                        textureManager);
                meshManager.CompactStaticBuffers();
                Console.WriteLine(
                    $"Material/GI conformance scene: fixtures={summary.FixtureCount}, " +
                    $"oracleCases={summary.CatalogCaseFixtureCount}, skinned={summary.SkinnedFixtureCount}, " +
                    $"liveEdits={summary.LiveEditTargetCount}, sceneSha256={summary.SceneFingerprint}.");
                return Finish(new Model { Name = "Material-GI Conformance" });
            }

            TextureManager showcaseTextureManager = Services?.GetRequiredService<TextureManager>()
                ?? throw new InvalidOperationException(
                    "The material showcase requires the renderer TextureManager.");
            SampleMaterialShowcaseScene.Configure(
                Scene,
                meshManager,
                materialManager,
                showcaseTextureManager);
            meshManager.CompactStaticBuffers();
            return Finish(new Model { Name = "Material Showcase" });
        }

        if (_sceneKind == SampleSceneKind.AnalyticalAreaLights)
        {
            _sceneLoader = null;
            SampleAnalyticalAreaLightRoomScene.Configure(
                Scene,
                meshManager,
                materialManager);
            meshManager.CompactStaticBuffers();
            return Finish(new Model { Name = "Analytical Area Light Room" });
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
            return Finish(new Model { Name = "Foliage Showcase" });
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
            return Finish(new Model { Name = "GI Test Scene" });
        }

        if (_sceneKind == SampleSceneKind.VfxShowcase)
        {
            _sceneLoader = null;
            _sampleVfxEffects = SampleVfxShowcaseScene.Configure(Scene, meshManager, materialManager);
            meshManager.CompactStaticBuffers();
            return Finish(new Model { Name = "VFX Showcase" });
        }

        SampleAssetManifest assetManifest = GetModelSceneManifest(_sceneKind)
            ?? throw new InvalidOperationException(
                $"Scene '{_sceneKind}' does not have a model asset manifest.");
        _sceneLoader = new SampleSceneLoader(
            Content!,
            materialManager,
            meshManager,
            lightManager,
            assetManifest,
            loadSceneDocument: _sceneKind == SampleSceneKind.SponzaPlaza,
            sponzaFixtureMode: _smokeOptions.SponzaFixtureMode);
        Model model = _sceneLoader.Load(Scene);
        if (_sceneKind == SampleSceneKind.SponzaPlaza &&
            !_sceneLoader.LoadedFromDocument)
        {
            SamplePlazaGlobalIllumination.ConfigureSceneLighting(Scene);
        }
        if (_sceneKind == SampleSceneKind.SponzaPlaza &&
            !_sceneLoader.LoadedFromDocument &&
            _smokeOptions.SponzaFixtureMode ==
                SampleSponzaFixtureMode.AnimationDemo)
        {
            SampleAnimatedCharacter.Configure(Scene, Content!);
        }
        if (_sceneKind == SampleSceneKind.SponzaPlaza &&
            _smokeOptions.SponzaFixtureMode ==
                SampleSponzaFixtureMode.C5ResidualValidation)
        {
            TextureManager sponzaTextureManager = Services?.GetRequiredService<TextureManager>()
                ?? throw new InvalidOperationException(
                    "The Sponza C5 emissive test sphere requires the renderer TextureManager.");
            SampleSponzaNearFieldResidualTestSphere.Configure(
                Scene,
                meshManager,
                materialManager,
                sponzaTextureManager);
        }
        meshManager.CompactStaticBuffers();
        return Finish(model);
    }

    private void ConfigureSceneRenderSettings(VulkanRenderer renderer)
    {
        RenderSettings settings = renderer.Settings;
        UpdateVfxVolumetricDemoOverrideOwnership(settings);
        if (_smokeOptions.KhronosMaterialGiRenderedGate is not null)
        {
            SampleKhronosMaterialGiRenderedGateRunner.ApplyLockedSettings(settings);
        }
        else if (_sceneKind == SampleSceneKind.MaterialShowcase)
        {
            SampleMaterialShowcaseScene.ConfigureRenderSettings(settings);
            settings.Particles.Enabled = false;
        }
        else if (_sceneKind == SampleSceneKind.AnalyticalAreaLights)
        {
            SampleAnalyticalAreaLightRoomScene.ConfigureRenderSettings(settings);
        }
        else if (_sceneKind == SampleSceneKind.FoliageShowcase)
        {
            ConfigureFoliageShowcaseRenderSettings(settings);
        }
        else if (_sceneKind == SampleSceneKind.GlobalIlluminationTest)
        {
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, SamplePerformanceScenario.GiCornellRoom);
            settings.Particles.Enabled = false;
        }
        else if (_sceneKind == SampleSceneKind.VfxShowcase)
        {
            SampleVfxShowcaseScene.ConfigureRenderSettings(settings);
        }
        else if (_sceneKind == SampleSceneKind.Bistro)
        {
            SampleBistroGlobalIlluminationProfile.Configure(settings);
            // Reuse the exact DDGI diagnostic override in Bistro quality runs.
            // Debug permutations intentionally bypass the opaque receiver
            // cache, which lets a deterministic capture distinguish the
            // canonical per-fragment gather from cache reconstruction defects.
            if (Enum.TryParse(
                    Environment.GetEnvironmentVariable(
                        "NJULF_EXACT_DDGI_DEBUG_VIEW"),
                    ignoreCase: true,
                    out GlobalIlluminationDebugView exactDebugView))
            {
                settings.GlobalIllumination.DebugView = exactDebugView;
            }
            if (bool.TryParse(
                    Environment.GetEnvironmentVariable(
                        "NJULF_BISTRO_FORCE_EXACT_DDGI_GATHER"),
                    out bool forceExactDdgiGather) &&
                forceExactDdgiGather)
            {
                settings.Diagnostics.ForceExactForwardGiGatherForBenchmark =
                    true;
            }
            settings.Particles.Enabled = false;
        }
        else if (_sceneKind == SampleSceneKind.SponzaPlaza)
        {
            SamplePlazaGlobalIllumination.ConfigureRenderSettingsForMemoryProfile(
                settings,
                ResolveSponzaGpuMemoryProfile(renderer));
            settings.Animation.Enabled =
                _smokeOptions.SponzaFixtureMode ==
                SampleSponzaFixtureMode.AnimationDemo;
            settings.Particles.Enabled = false;
        }
        else
        {
            throw new InvalidOperationException(
                $"Scene '{_sceneKind}' does not have a render-settings profile.");
        }

        _materialGiRolloutBootstrap.Apply(settings, Console.Out);
        if (_sceneKind == SampleSceneKind.Bistro)
        {
            // Keep the scene aligned with the engine-wide C5-on default after
            // rollout mutation. ApplySmokeRenderSettings runs after this, so
            // an explicit command-line Off remains authoritative.
            SampleBistroGlobalIlluminationProfile
                .ConfigurePostAdvancedGiRollout(settings);
        }
        else if (_sceneKind == SampleSceneKind.SponzaPlaza)
        {
            SampleSponzaGlobalIlluminationProfile
                .ConfigurePostAdvancedGiRollout(
                    settings,
                    _smokeOptions.SponzaFixtureMode ==
                        SampleSponzaFixtureMode.C5ResidualValidation);
        }

        SampleReflectionPolicy.Apply(settings);
    }

    private void RestoreSceneRenderSettings(VulkanRenderer renderer)
    {
        ConfigureSceneRenderSettings(renderer);

        if (_performanceScenarioRunner != null)
        {
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(
                renderer.Settings,
                _performanceScenarioRunner.CurrentScenario);
        }

        ApplySmokeRenderSettings(renderer);
    }

    private void UpdateVfxVolumetricDemoOverrideOwnership(
        RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_sceneKind == SampleSceneKind.VfxShowcase)
        {
            if (_vfxVolumetricDemoOverride.Active)
                _vfxVolumetricDemoOverride.Apply(settings);
            else
                _vfxVolumetricDemoOverride.Enter(settings);
            return;
        }

        _vfxVolumetricDemoOverride.Exit(settings);
    }

    private void ApplyScenePostOverrides(RenderSettings settings)
    {
        UpdateVfxVolumetricDemoOverrideOwnership(settings);
    }

    private static SamplePlazaGpuMemoryProfile ResolveSponzaGpuMemoryProfile(VulkanRenderer renderer)
    {
        MemoryHeapBudgetSnapshot heapBudget = renderer.CurrentMemoryHeapBudget;
        if (!heapBudget.IsAvailable || heapBudget.PrimaryBudgetBytes == 0)
            return SamplePlazaGpuMemoryProfile.Medium;

        const ulong oneGiB = 1024UL * 1024UL * 1024UL;
        ulong budget = heapBudget.PrimaryBudgetBytes;
        if (budget < 2UL * oneGiB)
            return SamplePlazaGpuMemoryProfile.Low;
        if (budget < 4UL * oneGiB)
            return SamplePlazaGpuMemoryProfile.Medium;

        return SamplePlazaGpuMemoryProfile.High;
    }

    private SamplePerformanceScenarioRunner CreatePerformanceScenarioRunner(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager)
    {
        return new SamplePerformanceScenarioRunner(new SampleStressSceneBuilder(
            Scene,
            meshManager,
            materialManager,
            lightManager,
            LightingMode));
    }

    private void CycleScene(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        LoadSceneKind(
            GetNextKey3Scene(_sceneKind),
            meshManager,
            materialManager,
            lightManager,
            renderer,
            camera);
    }

    internal static SampleSceneKind GetNextKey3Scene(SampleSceneKind current)
    {
        SampleSceneKind[] sceneKinds = Enum.GetValues<SampleSceneKind>();
        int index = Array.IndexOf(sceneKinds, current);
        return sceneKinds[(index + 1) % sceneKinds.Length];
    }

    private void CycleSponzaAndBistro(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        SampleSceneKind nextScene = _sceneKind == SampleSceneKind.SponzaPlaza
            ? SampleSceneKind.Bistro
            : SampleSceneKind.SponzaPlaza;
        LoadSceneKind(
            nextScene,
            meshManager,
            materialManager,
            lightManager,
            renderer,
            camera);
    }

    private bool LoadSceneKind(
        SampleSceneKind sceneKind,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        ClearSceneAndContent();
        bool requestedSceneLoaded =
            SampleSceneTransitionRecovery.Execute(
                loadRequestedScene: () =>
                    LoadSceneKindCore(
                        sceneKind,
                        meshManager,
                        materialManager,
                        lightManager,
                        renderer,
                        camera),
                cleanupRequestedScene: ClearSceneAndContent,
                loadSafeScene: () =>
                    LoadSceneKindCore(
                        SampleSceneKind.GlobalIlluminationTest,
                        meshManager,
                        materialManager,
                        lightManager,
                        renderer,
                        camera),
                cleanupFailedSafeScene: ClearSceneAndContent,
                reportRequestedFailure: failure =>
                {
                    Console.Error.WriteLine(
                        $"Scene '{GetSceneDisplayName(sceneKind)}' exhausted Vulkan device memory. Attempting the safe GI scene.");
                    Console.Error.WriteLine(failure);
                });

        if (!requestedSceneLoaded)
        {
            Console.Error.WriteLine(
                $"Recovered from the failed '{GetSceneDisplayName(sceneKind)}' load with '{GetSceneDisplayName(SampleSceneKind.GlobalIlluminationTest)}'.");
        }

        return requestedSceneLoaded;
    }

    private void LoadSceneKindCore(
        SampleSceneKind sceneKind,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        _sceneKind = sceneKind;
        UpdateVfxVolumetricDemoOverrideOwnership(renderer.Settings);
        renderer.CaptureSceneKind = GetPerformanceCaptureSceneKind(_sceneKind);

        Model model = LoadSampleScene(meshManager, materialManager, lightManager);
        _performanceScenarioRunner = CreatePerformanceScenarioRunner(meshManager, materialManager, lightManager);
        SampleLighting.ConfigureRenderSettings(renderer.Settings, ResolveSceneLightingMode());
        ApplySmokeRenderSettings(renderer);
        ConfigureSceneLighting(lightManager);
        ConfigureSceneEnvironment(renderer);
        ConfigureSceneRenderSettings(renderer);
        ApplySmokeRenderSettings(renderer);
        _inputController?.SetParticleEffects(_sampleVfxEffects);
        _inputController?.SetLightingMode(ResolveSceneLightingMode());
        _inputController?.SetPerformanceScenarioRunner(_performanceScenarioRunner);
        ApplyCameraPreset(camera, _sceneKind);
        PrintLoadedSceneSummary(model);

        Console.WriteLine($"Scene: {GetSceneDisplayName(_sceneKind)}");
    }

    private void ClearSceneAndContent()
    {
        Scene.ClearAndDispose();
        (Content ?? throw new InvalidOperationException(
            "Interactive scene transitions require an initialized content manager."))
            .Clear();
    }

    private void ConfigureSceneLighting(LightManager lightManager)
    {
        if (_smokeOptions.KhronosMaterialGiRenderedGate is not null)
        {
            SampleKhronosMaterialGiRenderedGateRunner.ConfigureLockedLighting(lightManager);
            return;
        }
        if (_sceneKind == SampleSceneKind.GlobalIlluminationTest)
            return;

        if (_sceneKind == SampleSceneKind.Bistro)
        {
            // Bistro's FBX sun carries the scene's intended radiance and
            // orientation, but imported model lights do not opt into shadows.
            // Use one canonical shadow-casting copy instead of combining the
            // unshadowed source with Sponza's unrelated directional key.
            lightManager.ClearLights();
            lightManager.AddLight(SampleBistroLightingProfile.CreateDirectionalKey());
            return;
        }

        if (_sceneKind == SampleSceneKind.SponzaPlaza && _sceneLoader?.LoadedFromDocument == true)
            return;

        SampleLighting.Configure(lightManager, ResolveSceneLightingMode());
    }

    private void ConfigureSceneEnvironment(VulkanRenderer renderer)
    {
        if (_smokeOptions.KhronosMaterialGiRenderedGate is not null)
        {
            SampleEnvironment.Configure(renderer, SampleEnvironmentMode.StudioNeutral);
            return;
        }
        SampleEnvironment.Configure(renderer, _sceneKind switch
        {
            SampleSceneKind.MaterialShowcase => SampleEnvironmentMode.StudioNeutral,
            SampleSceneKind.GlobalIlluminationTest => SampleEnvironmentMode.Disabled,
            SampleSceneKind.AnalyticalAreaLights => SampleEnvironmentMode.Disabled,
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
            SampleSceneKind.AnalyticalAreaLights =>
                SampleLightingMode.AnalyticalAreaLightShowcase,
            SampleSceneKind.VfxShowcase => SampleLightingMode.VolumetricShowcase,
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

        if (GetModelSceneManifest(_sceneKind) is { } assetManifest)
        {
            _diagnosticsReporter.PrintModelSummary(model, assetManifest);
            return;
        }

        _diagnosticsReporter.PrintProceduralSceneSummary(Scene, GetSceneDisplayName(_sceneKind));
    }

    private static void ApplyCameraPreset(FirstPersonCamera camera, SampleSceneKind sceneKind)
    {
        if (sceneKind == SampleSceneKind.Bistro)
        {
            ApplyBistroCameraPreset(camera);
            return;
        }

        (CoreVector3 position, float yaw, float pitch, float farPlane) = GetCameraPreset(sceneKind);
        camera.Position = position;
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.FarPlane = farPlane;
        camera.Update();
    }

    private static void ApplyBistroCameraPreset(FirstPersonCamera camera)
    {
        (CoreVector3 position, float yaw, float pitch, float farPlane) =
            GetCameraPreset(SampleSceneKind.Bistro);
        camera.Position = position;
        camera.Yaw = yaw;
        // Performance metadata round-trips FirstPersonCamera's pitch convention.
        camera.Pitch = pitch;
        camera.FieldOfView = MathF.PI / 3.2f;
        camera.NearPlane = 0.05f;
        camera.FarPlane = farPlane;
        camera.Update();
    }

    private static void ApplyPerformanceScenarioCamera(
        FirstPersonCamera camera,
        SamplePerformanceScenario scenario)
    {
        if (scenario != SamplePerformanceScenario.GiSponzaRightWallStationary &&
            !SampleSponzaAtmosphereScenario.IsScenario(scenario))
            return;

        SampleSponzaGiCameraBookmark bookmark = SampleSponzaGiCaptureContract.Default.LowBookmark;
        camera.Position = bookmark.Position;
        camera.Yaw = bookmark.Yaw;
        camera.Pitch = bookmark.Pitch;
        camera.FieldOfView = bookmark.FieldOfView;
        camera.NearPlane = bookmark.NearPlane;
        camera.FarPlane = bookmark.FarPlane;
        camera.Update();
    }

    internal static (CoreVector3 Position, float Yaw, float Pitch, float FarPlane) GetCameraPreset(
        SampleSceneKind sceneKind)
    {
        return sceneKind switch
        {
            SampleSceneKind.GlobalIlluminationTest => (new CoreVector3(0f, 1.7f, 1.15f), 0f, -0.08f, 80f),
            SampleSceneKind.Bistro =>
                (new CoreVector3(-16.003326f, 2.5132222f, 1.2387409f), 1.6121571f, 0.0660575f, 500f),
            SampleSceneKind.MaterialShowcase => (new CoreVector3(0f, 2.15f, 9.0f), 0f, -0.17f, 120f),
            SampleSceneKind.AnalyticalAreaLights =>
                (new CoreVector3(0f, 2.15f, 8.25f), 0f, -0.08f, 60f),
            SampleSceneKind.FoliageShowcase => (new CoreVector3(0f, 1.6f, 5.5f), 0f, -0.14f, 180f),
            SampleSceneKind.VfxShowcase => (new CoreVector3(0f, 1.7f, 7.2f), 0f, -0.12f, 100f),
            // Face across the courtyard on Sponza startup instead of directly
            // into the nearby wall.
            _ => (new CoreVector3(6f, 1.25f, 5.5f), -MathF.PI * 0.5f, -0.12f, 250f)
        };
    }

    private static string GetSceneDisplayName(SampleSceneKind sceneKind)
    {
        return sceneKind switch
        {
            SampleSceneKind.GlobalIlluminationTest => "GI Test Scene",
            SampleSceneKind.Bistro => "Bistro",
            SampleSceneKind.MaterialShowcase => "Material Showcase",
            SampleSceneKind.AnalyticalAreaLights => "Analytical Area Light Room",
            SampleSceneKind.FoliageShowcase => "Foliage Showcase",
            SampleSceneKind.VfxShowcase => "Volumetric VFX Showcase",
            _ => "Sponza Plaza"
        };
    }

    internal static string GetPerformanceCaptureSceneKind(
        SampleSceneKind sceneKind) => sceneKind == SampleSceneKind.SponzaPlaza
            ? "Sponza"
            : sceneKind.ToString();

    private static SampleAssetManifest? GetModelSceneManifest(SampleSceneKind sceneKind) =>
        sceneKind switch
        {
            SampleSceneKind.SponzaPlaza => SponzaAssetManifest,
            SampleSceneKind.Bistro => SampleAssetManifest.Bistro,
            _ => null
        };

    private void ResizeForSmoke(int width, int height)
    {
        if (_pendingSmokeResize is not null ||
            _observingSmokeWindowMutation is not null)
        {
            throw new InvalidOperationException(
                "A smoke framebuffer mutation is already pending observation.");
        }

        _pendingSmokeResize = (width, height);
    }

    private void ApplyPendingSmokeResize()
    {
        if (_pendingSmokeResize is not { } resize)
            return;

        _pendingSmokeResize = null;
        int width = resize.Width;
        int height = resize.Height;
        if (Window == null)
        {
            _smokeRunner?.OnFramebufferMutationObserved(
                succeeded: false,
                "The Silk.NET window was unavailable while applying the framebuffer mutation.");
            return;
        }

        _observingSmokeWindowMutation = new PendingSmokeWindowMutation(
            width,
            height,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            _framebufferResizeRevision);
        try
        {
            if (width <= 0 || height <= 0)
            {
                Window.WindowState = Silk.NET.Windowing.WindowState.Minimized;
            }
            else
            {
                if (Window.WindowState == Silk.NET.Windowing.WindowState.Minimized)
                    Window.WindowState = Silk.NET.Windowing.WindowState.Normal;
                Window.Size = new Silk.NET.Maths.Vector2D<int>(width, height);
            }
        }
        catch (Exception ex)
        {
            _observingSmokeWindowMutation = null;
            _smokeRunner?.OnFramebufferMutationObserved(
                succeeded: false,
                $"Silk.NET window mutation {width}x{height} threw " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ObserveSmokeWindowMutation()
    {
        if (_observingSmokeWindowMutation is not { } mutation ||
            Window == null)
        {
            return;
        }

        try
        {
            Silk.NET.Maths.Vector2D<int> logicalSize = Window.Size;
            Silk.NET.Maths.Vector2D<int> framebufferSize = Window.FramebufferSize;
            Silk.NET.Windowing.WindowState state = Window.WindowState;
            bool minimizeRequested = mutation.Width <= 0 || mutation.Height <= 0;
            bool observed = minimizeRequested
                ? state == Silk.NET.Windowing.WindowState.Minimized &&
                  (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
                : state != Silk.NET.Windowing.WindowState.Minimized &&
                  logicalSize.X == mutation.Width &&
                  logicalSize.Y == mutation.Height &&
                  framebufferSize.X > 0 &&
                  framebufferSize.Y > 0;
            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(
                mutation.StartedTimestamp);
            bool timedOut = elapsed >= TimeSpan.FromSeconds(5);
            if (!observed && !timedOut)
                return;

            _observingSmokeWindowMutation = null;
            if (observed &&
                !minimizeRequested &&
                _framebufferResizeRevision ==
                mutation.FramebufferResizeRevisionAtRequest)
            {
                // Silk normally publishes FramebufferResize first. Keep this
                // fallback for backends that expose the new framebuffer before
                // raising the event, without rebuilding the swapchain twice.
                WindowWidth = framebufferSize.X;
                WindowHeight = framebufferSize.Y;
                Renderer?.Resize(framebufferSize.X, framebufferSize.Y);
                if (Camera != null)
                {
                    Camera.AspectRatio =
                        (float)framebufferSize.X / framebufferSize.Y;
                }
            }

            string detail =
                $"requested={mutation.Width}x{mutation.Height}, " +
                $"logical={logicalSize.X}x{logicalSize.Y}, " +
                $"framebuffer={framebufferSize.X}x{framebufferSize.Y}, " +
                $"state={state}, observed={observed}, elapsedMs={elapsed.TotalMilliseconds:F1}";
            _smokeRunner?.OnFramebufferMutationObserved(
                succeeded: observed,
                detail);
        }
        catch (Exception ex)
        {
            _observingSmokeWindowMutation = null;
            _smokeRunner?.OnFramebufferMutationObserved(
                succeeded: false,
                $"Observing Silk.NET window mutation {mutation.Width}x{mutation.Height} " +
                $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CaptureBaselineSnapshotIfRequested()
    {
        if (_baselineSnapshotExported ||
            string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory) ||
            _inputController == null)
            return;

        _baselineScenarioRenderedFrames++;
        int requiredFrames = _sceneKind == SampleSceneKind.VfxShowcase
            ? VolumetricBaselineCaptureFrameCount
            : BaselineCaptureFrameCount;
        if (_baselineScenarioRenderedFrames < requiredFrames)
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
            SamplePerformanceScenario.GiQualityInterior => SamplePerformanceScenario.GiQualityInterior,
            _ => SamplePerformanceScenario.Normal
        };
    }

    private (string DirectoryName, string Label) ResolveBaselineSnapshotMetadata()
    {
        if (_sceneKind == SampleSceneKind.Bistro)
            return ("bistro", "Baseline Bistro snapshot");
        if (_sceneKind == SampleSceneKind.VfxShowcase)
            return ("volumetric-vfx-showcase",
                "Baseline volumetric VFX showcase snapshot");

        return ResolveBaselineSnapshotScenario() switch
        {
            SamplePerformanceScenario.ForestFoliage => ("forest-foliage", "Baseline forest foliage snapshot"),
            SamplePerformanceScenario.GiSponzaRightWallStationary => ("gi-sponza-right-wall-stationary", "Baseline Sponza right-wall GI snapshot"),
            SamplePerformanceScenario.GiQualityInterior => ("gi-quality-interior", "Baseline GI quality interior snapshot"),
            _ => ("normal-sponza-interior", "Baseline normal Sponza/interior snapshot")
        };
    }

    private void ExportBaselineSnapshot(string scenarioDirectoryName, string label)
    {
        if (_inputController == null || string.IsNullOrWhiteSpace(_smokeOptions.BaselineSnapshotDirectory))
            return;

        string directory = System.IO.Path.Combine(_smokeOptions.BaselineSnapshotDirectory, scenarioDirectoryName);
        _inputController.ExportPerformanceSnapshotFile(directory, label);
        CaptureDiagnosticScreenshot(System.IO.Path.Combine(directory, "exact-camera.png"));
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

    private sealed record PendingSmokeWindowMutation(
        int Width,
        int Height,
        long StartedTimestamp,
        long FramebufferResizeRevisionAtRequest);
}
