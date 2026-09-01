using System;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Assets.Cooked;
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
        if (SampleBistroReflectionQualificationCli.TryRun(
                args,
                Console.Out,
                Console.Error,
                out int reflectionQualificationExitCode))
        {
            return reflectionQualificationExitCode;
        }
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
        using var allOnFailureGuard =
            options.GiAllOnQualificationReportPath is { } allOnReportPath
                ? new SampleGiAllOnQualificationHostFailureGuard(
                    allOnReportPath,
                    options.SceneKind)
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
        catch (Exception exception) when (
            options.KhronosMaterialGiRenderedGate is not null ||
            options.GiAllOnQualificationReportPath is not null)
        {
            string description =
                $"{exception.GetType().Name}: {exception.Message}";
            gateFailureGuard?.RecordHostFailure(
                "Khronos rendered-gate host failed: " + description);
            allOnFailureGuard?.RecordHostFailure(
                "All-on GI qualification host failed: " + description);
            Environment.ExitCode = 1;
            Console.Error.WriteLine(description);
        }
        if (gateFailureGuard is not null &&
            !gateFailureGuard.CompleteHostRun(Environment.ExitCode))
        {
            Environment.ExitCode = 1;
        }
        if (allOnFailureGuard is not null &&
            !allOnFailureGuard.CompleteHostRun(Environment.ExitCode))
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
        SimpleDdgiReceiverCacheModeOverride = null,
        SimpleDdgiTransportAccelerationEnabledOverride = null,
        SimpleDdgiTransportAcceleratedSweepCountOverride = null,
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
        SimpleDdgiReceiverCacheModeOverride = selection.ReceiverCacheEnabled
            ? SimpleDdgiReceiverCacheMode.TemporalAdaptive
            : SimpleDdgiReceiverCacheMode.Exact,
        SimpleDdgiTransportAccelerationEnabledOverride =
            selection.AcceleratedTransportSolverEnabled,
        SimpleDdgiTransportAcceleratedSweepCountOverride = 2,
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
    private const string LoadingSceneName =
        "Njulf Lightweight Loading Scene";
    private static readonly TimeSpan TransitionUploadCpuBudget =
        TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan DeferredSceneAttachmentCpuBudget =
        TimeSpan.FromMilliseconds(16);
    private const int TransitionUploadCallbacksPerFrame = 32;
    private const int TransitionReleaseRenderObjectsPerFrame = 384;
    // Mesh submissions are fence-polled rather than waited synchronously. A
    // larger window avoids dozens of whole-buffer growth/copy submissions for
    // flattened scenes while the cooperative CPU budget still bounds
    // recording.
    private const long TransitionUploadSubmissionBytes =
        24L * 1024L * 1024L;
    private const long TransitionPreloadInflightBytes =
        512L * 1024L * 1024L;
    private const int SceneRetirementPresentDelay = 3;
    private const int HybridReflectionPreparationStablePresentDelay = 60;
    private static readonly SampleAssetManifest SponzaAssetManifest = SampleAssetManifest.NewSponza;
    private const SampleLightingMode LightingMode = SampleLightingMode.DirectionalKey;
    private const SampleEnvironmentMode EnvironmentMode = SampleEnvironmentMode.ProceduralOutdoor;
    private const SamplePerformanceScenario DefaultInteractiveScenario = SamplePerformanceScenario.Normal;
    private const int BaselineCaptureFrameCount = 900;
    private const int VolumetricBaselineCaptureFrameCount = 12;
    internal const int BenchmarkDynamicScenarioDisturbanceFrameCount = 30;
    internal const float BenchmarkSimulationDeltaSeconds = 1.0f / 60.0f;

    private SampleInputController? _inputController;
    private readonly SampleStressSceneResourceCache
        _sampleStressSceneResources = new();
    private SampleSceneLoader? _sceneLoader;
    private SampleSceneTransitionCoordinator? _sceneTransition;
    private SampleSceneResidencyCache? _sceneResidency;
    private IContentUploadPump? _contentUploadPump;
    private Scene? _transitionLoadingScene;
    private Scene? _transitionPreviousScene;
    private long _loadingTransitionGeneration;
    private bool _loadingFramePresented;
    private bool _loadingSceneInstancesReleased;
    private bool _loadingResidencyAssetsReleased;
    private long _handledTransitionGeneration;
    private bool _transitionWasResident;
    private bool _transitionKeepsPreviousResidency;
    private readonly object _preparedSceneTransitionGate = new();
    private PreparedSceneTransition? _preparedSceneTransition;
    private long _scenePreparationGeneration;
    private PendingPostPresentSceneCommit?
        _pendingPostPresentSceneCommit;
    private long _hybridReflectionPreparationEligiblePresentSerial =
        long.MaxValue;
    private bool _hybridReflectionPreparationStarted;
    private long _transitionRequestTimestamp;
    private SampleSceneTransitionPhase _lastReportedTransitionPhase =
        SampleSceneTransitionPhase.Idle;
    private DeferredSceneStreaming? _deferredSceneStreaming;
    private PendingDeferredSceneStreaming? _pendingDeferredSceneStreaming;
    private ContentLoadProgressEvent? _deferredSceneProgress;
    private long _deferredLastProgressTimestamp;
    private long _deferredLastCompletedBytes;
    private int _deferredWatchdogWarned;
    private BistroTransitionSmokeState? _bistroTransitionSmoke;
    private readonly Queue<DeferredSceneRetirement>
        _deferredSceneRetirements = new();
    private long _presentedFrameSerial;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private IReadOnlyList<ParticleEffectInstance>? _sampleVfxEffects;
    private readonly SampleSmokeOptions _smokeOptions;
    private readonly string _startupWaitTarget;
    private string? _startupVisualCapturePath;
    private int _startupVisualCaptureAttempt;
    private bool _startupVisualCaptureAwaitingPresent;
    private bool _startupVisualQualified;
    private long _startupVisualCandidatePresentMicroseconds;
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
    private int _bistroQualityCaptureFrameOrigin = -1;
    private SampleMaterialGiCaptureRunner? _materialGiCaptureRunner;
    private SampleSponzaTemporalCaptureRunner?
        _sponzaTemporalCaptureRunner;
    private SampleVolumetricTemporalCaptureRunner?
        _volumetricTemporalCaptureRunner;
    private SampleKhronosMaterialGiRenderedSceneBuild? _khronosMaterialGiRenderedScene;
    private SampleKhronosMaterialGiRenderedGateRunner? _khronosMaterialGiRenderedGateRunner;
    private SampleGiAllOnQualificationRunner? _giAllOnQualificationRunner;
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
    private Scene? _pendingEditorScene;
    private EditorImGuiPanels? _editorPanels;
    private bool _editorTogglePressed;
    private bool _editorSavePressed;
    private bool _editorPickPressed;
#endif

    public HelloGame(SampleSmokeOptions smokeOptions, string[] commandLineArgs)
    {
        _smokeOptions = smokeOptions ?? throw new ArgumentNullException(nameof(smokeOptions));
        _startupWaitTarget = ResolveStartupWaitTarget(commandLineArgs);
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
        (VSync, MaximumFramesPerSecond) = ResolveFramePacing(_smokeOptions);
    }

    private bool IsStartupWaitSatisfied(VulkanRenderer renderer)
    {
        RendererStartupSnapshot snapshot = renderer.StartupSnapshot;
        bool fullQualityRequested = _startupWaitTarget is
            "scene" or "fallback-scene" or "full-quality";
        if (!fullQualityRequested)
        {
            return !renderer.IsProgressiveStartupEnabled ||
                   snapshot.BootstrapPresented || snapshot.IsFullQuality;
        }
        if (renderer.IsProgressiveStartupEnabled &&
            !snapshot.FullQualityPresented)
        {
            return false;
        }
        if (_startupVisualQualified)
            return true;

        RendererDiagnostics diagnostics = renderer.LastDiagnostics;
        ScreenshotCaptureAnalysis capture =
            renderer.LastScreenshotCaptureAnalysis;
        if (_startupVisualCapturePath != null &&
            !string.IsNullOrWhiteSpace(capture.OutputPath) &&
            string.Equals(
                Path.GetFullPath(capture.OutputPath),
                Path.GetFullPath(_startupVisualCapturePath),
                StringComparison.OrdinalIgnoreCase))
        {
            if (capture.Content.HasVisibleContent)
            {
                if (diagnostics.GiRenderCriticalPipelineCreationCount != 0UL)
                {
                    return FailStartupVisualQualification(
                        $"renderer capture contains visible pixels, but " +
                        $"render-thread pipeline creation escaped startup " +
                        $"preparation (count=" +
                        $"{diagnostics.GiRenderCriticalPipelineCreationCount}, " +
                        $"last={diagnostics.GiLastCreatedPipeline}, " +
                        $"capture={capture.OutputPath})");
                }

                _startupVisualQualified = true;
                long elapsedMicroseconds = Math.Max(
                    0L,
                    _startupVisualCandidatePresentMicroseconds);
                renderer.ReportStartupMilestone(
                    RendererStartupMilestone.VisibleContentPresent,
                    elapsedMicroseconds);
                Console.WriteLine(
                    $"Startup visible final frame qualified: " +
                    $"elapsed={elapsedMicroseconds / 1_000_000.0:F3}s, " +
                    $"capture={capture.OutputPath}, {capture.Content.Detail}.");
                return true;
            }

            Console.WriteLine(
                $"Startup frame rejected as non-visible: " +
                $"capture={capture.OutputPath}, {capture.Content.Detail}.");
            _startupVisualCapturePath = null;
        }
        else if (_startupVisualCapturePath != null &&
                 string.Equals(
                     diagnostics.LastScreenshotPath,
                     _startupVisualCapturePath,
                     StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(
                     diagnostics.LastScreenshotError))
        {
            return FailStartupVisualQualification(
                "renderer final-LDR capture failed: " +
                diagnostics.LastScreenshotError);
        }

        if (_startupVisualCapturePath != null)
            return false;
        if (_startupVisualCaptureAttempt >= 8)
        {
            return FailStartupVisualQualification(
                "eight production swapchain captures remained black or uniform");
        }

        renderer.Settings.Debug.Enabled = true;
        renderer.Settings.Debug.AllowScreenshots = true;
        int attempt = ++_startupVisualCaptureAttempt;
        _startupVisualCapturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Screenshots",
            $"startup-final-{Environment.ProcessId}-{attempt}.png");
        _startupVisualCaptureAwaitingPresent = true;
        renderer.RequestScreenshot(_startupVisualCapturePath);
        return false;
    }

    private bool FailStartupVisualQualification(string detail)
    {
        _runtimeSmokeFailure ??=
            "Startup visible-final-frame qualification failed: " + detail;
        Console.Error.WriteLine(_runtimeSmokeFailure);
        Environment.ExitCode = 1;
        Exit();
        return false;
    }

    protected override void OnBootstrapFramePresented()
    {
        if (_smokeOptions.Mode == SampleSmokeMode.Startup &&
            _startupWaitTarget == "bootstrap")
        {
            // The pipeline-free clear is a real submitted/presented smoke
            // frame even though the content Draw override intentionally did
            // not run. Count it in the generic health contract.
            _drawnFrames = Math.Max(_drawnFrames, 1);
            if (_smokeOptions.FrameCount <= 1)
                Exit();
        }
    }

    private static string ResolveStartupWaitTarget(
        IReadOnlyList<string> arguments)
    {
        const string option = "--startup-wait";
        const string prefix = "--startup-wait=";
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return argument[prefix.Length..].ToLowerInvariant();
            }
            if (argument.Equals(option, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count)
            {
                return arguments[index + 1].ToLowerInvariant();
            }
        }
        return Environment.GetEnvironmentVariable("NJULF_STARTUP_WAIT")
                   ?.Trim().ToLowerInvariant() ??
               "bootstrap";
    }

    internal static bool RequiresControlledProductionWindow(
        SampleSmokeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Benchmark.Enabled ||
               options.BenchmarkQualitySequence.Enabled ||
               options.TailDdgiLongSoak ||
               !string.IsNullOrWhiteSpace(
                   options.GiAllOnQualificationReportPath) ||
               !string.IsNullOrWhiteSpace(options.SponzaGiCaptureDirectory) ||
               !string.IsNullOrWhiteSpace(options.BistroQualityCaptureDirectory);
    }

    internal static (bool VSync, double MaximumFramesPerSecond)
        ResolveFramePacing(SampleSmokeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool sponzaTemporalCapture = !string.IsNullOrWhiteSpace(
            options.SponzaTemporalCaptureDirectory);
        bool volumetricTemporalCapture = !string.IsNullOrWhiteSpace(
            options.VolumetricTemporalCaptureDirectory);
        bool defaultVSync =
            options.KhronosMaterialGiRenderedGate is null &&
            !(options.TailDdgiLongSoak ||
              (options.Benchmark.Enabled &&
               options.Benchmark.DisableVSync) ||
              options.BenchmarkQualitySequence.Enabled ||
              !string.IsNullOrWhiteSpace(
                  options.GiAllOnQualificationReportPath) ||
              sponzaTemporalCapture ||
              volumetricTemporalCapture ||
              !string.IsNullOrWhiteSpace(
                  options.BistroQualityCaptureDirectory));
        bool vSync = options.VSyncOverride ?? defaultVSync;
        double maximumFramesPerSecond =
            options.MaximumFramesPerSecondOverride ??
            (vSync ? 60.0 : 0.0);
        return (vSync, maximumFramesPerSecond);
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
            // Bistro is reachable through the runtime scene switcher even
            // when Cornell/GI was the startup scene. Texture admission is a
            // renderer-creation policy, so waiting until Bistro becomes the
            // active scene is too late: the transition would otherwise upload
            // the uncapped cooked mip chains.
            if (SampleBistroGlobalIlluminationProfile
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

    protected override void ConfigureRendererBeforeInitialize(
        IRenderer renderer)
    {
        if (renderer is not VulkanRenderer vulkanRenderer)
        {
            throw new InvalidOperationException(
                "NjulfHelloGame requires the Vulkan renderer.");
        }

        vulkanRenderer.CaptureSceneKind =
            GetPerformanceCaptureSceneKind(_sceneKind);
        if (_smokeOptions.Mode is SampleSmokeMode.None or
            SampleSmokeMode.SceneTransition or
            SampleSmokeMode.All)
        {
            // The interactive Cornell/Bistro pair activates hybrid
            // reflections in Bistro. Provision its forward-attachment ABI
            // before targets and pipelines are created so a scene switch does
            // not rebuild the complete mesh-pipeline bank.
            vulkanRenderer.ReserveHybridReflectionTargetProfile();
        }
        if (ShouldAutoEnableGpuTiming())
            vulkanRenderer.Settings.Debug.AllowGpuTiming = true;

        // Apply the complete settings transaction before VulkanRenderer creates
        // settings-dependent render targets, graph resources, or pipelines.
        // Content loading below may populate scene/managers, but must not need to
        // repair this startup profile during the first frame.
        SampleLighting.ConfigureRenderSettings(
            vulkanRenderer.Settings,
            ResolveSceneLightingMode());
        ConfigureSceneEnvironment(vulkanRenderer);
        ConfigureSceneRenderSettings(vulkanRenderer);
        ApplySmokeRenderSettings(vulkanRenderer);

        SamplePerformanceScenario startupScenario = ResolveStartupScenario();
        if (startupScenario != SamplePerformanceScenario.Normal)
        {
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(
                vulkanRenderer.Settings,
                startupScenario);
            ApplySmokeRenderSettings(vulkanRenderer);
        }

        if (!string.IsNullOrWhiteSpace(
                _smokeOptions.BaselineSnapshotDirectory) &&
            _sceneKind != SampleSceneKind.Bistro)
        {
            SamplePerformanceScenario baselineScenario =
                ResolveBaselineSnapshotScenario();
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(
                vulkanRenderer.Settings,
                baselineScenario);
            if (baselineScenario ==
                SamplePerformanceScenario.GiSponzaRightWallStationary)
            {
                vulkanRenderer.Settings.Diagnostics
                    .DdgiForwardEstimateCountersEnabled = true;
                vulkanRenderer.Settings.Debug.AllowGpuTiming = false;
            }
            ApplySmokeRenderSettings(vulkanRenderer);
        }
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
        ContentManager contentManager =
            services.GetRequiredService<ContentManager>();
        VulkanRenderer renderer = Renderer as VulkanRenderer
            ?? throw new InvalidOperationException("NjulfHelloGame requires the Vulkan renderer.");
        if (_smokeOptions.Mode == SampleSmokeMode.SceneTransition)
        {
            // This gate measures content handoff and residency. Claim optional
            // reflection initialization before the initial scene is prepared so
            // cold driver compilation cannot overlap either transition leg.
            renderer.DeferHybridReflectionPipelinePreparation();
        }
        if (_sceneKind == SampleSceneKind.SponzaPlaza)
        {
            RunStartupStep(
                "Content.ValidateSampleAssets",
                () => SampleAssetValidationGate.Validate(
                    AppContext.BaseDirectory,
                    SponzaAssetManifest));
        }
        SampleInputController.Configure(input);
        Console.WriteLine(
            "Debug overlays: Ctrl+Keypad9/Ctrl+Num9 forward, add Shift for reverse; " +
            "cycle=" + string.Join(" -> ", DebugOverlayCatalog.ActiveCycle.Select(
                static descriptor => descriptor.DisplayName)) + ".");
        PrintRendererDeviceInfo(renderer);
        CookedContentDiagnostics contentDiagnosticsBefore =
            contentManager.CookedDiagnostics;
        Model model = RunStartupStep(
            "Content.LoadSampleScene",
            () => LoadSampleScene(
                meshManager,
                materialManager,
                lightManager));
        (string contentSummary, bool contentWarning) =
            FormatInitialContentSummary(
                contentDiagnosticsBefore,
                contentManager.CookedDiagnostics);
        (contentWarning ? Console.Error : Console.Out)
            .WriteLine(contentSummary);
        _contentUploadPump =
            services.GetRequiredService<IContentUploadPump>();
        _sceneResidency = new SampleSceneResidencyCache(contentManager);
        _sceneResidency.Capture(
            _sceneKind,
            GetModelSceneManifest(_sceneKind),
            EstimateSceneResidencyBytes(_sceneKind));
        _sceneResidency.MarkActive(_sceneKind);
        _sceneTransition = new SampleSceneTransitionCoordinator(
            PrepareSceneTransitionAsync,
            target => CommitPreparedScene(
                target,
                meshManager,
                materialManager,
                lightManager,
                renderer,
                camera));
        _performanceScenarioRunner = CreatePerformanceScenarioRunner(meshManager, materialManager, lightManager);
        var diagnosticsReporter = new SampleDiagnosticsReporter(
            materialManager,
            services.GetService<IModelRenderUploadService>());
        _diagnosticsReporter = diagnosticsReporter;
        if (_smokeOptions.GiAllOnQualificationReportPath is { } allOnReportPath)
        {
            _giAllOnQualificationRunner =
                new SampleGiAllOnQualificationRunner(
                    allOnReportPath,
                    _sceneKind,
                    GetRendererDeviceIdentity(renderer),
                    SampleRenderSettingsFingerprint.Capture(renderer.Settings),
                    Exit);
            Console.WriteLine(
                "All-on GI runtime qualification armed: " +
                $"scene={_sceneKind}, maxFrames={_smokeOptions.FrameCount}, " +
                $"report='{Path.GetFullPath(allOnReportPath)}'.");
        }
        ConfigureSceneLighting(lightManager);
        SamplePerformanceScenario startupScenario = ResolveStartupScenario();
        if (startupScenario != SamplePerformanceScenario.Normal)
        {
            SamplePerformanceScenarioSummary summary = _performanceScenarioRunner.Apply(startupScenario);
            _sponzaAtmosphereFrozen = false;
            ApplyPerformanceScenarioCamera(camera, startupScenario);
            Console.WriteLine(
                $"Applied startup scenario: {summary.Scenario} " +
                $"objects={summary.ObjectCount}, lights={summary.LightCount}, materials={summary.MaterialCount}, notes={summary.Notes}");
        }

        _inputController = new SampleInputController(
            camera,
            input,
            HandleExitRequest,
            renderer,
            lightManager,
            ResolveSceneLightingMode(),
            _sampleVfxEffects,
            _performanceScenarioRunner,
            () => CycleScene(renderer),
            () => CycleSponzaAndBistro(renderer),
            sceneKind => LoadSceneKind(sceneKind, renderer),
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
                RequestAdvancedGiFeatureRestart,
            // Resolve through the loader that owns the *current* scene. A
            // delegate captured during procedural-scene startup would fall
            // back to default importer options after a later Bistro handoff,
            // missing the prepared Amazon-Bistro cache entry and attempting a
            // second synchronous model load during commit.
            loadModel: LoadModelForEditorRuntimeMetadata);
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
            CommitPreparedScene(
                _sceneKind,
                meshManager,
                materialManager,
                lightManager,
                renderer,
                camera);
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
            },
            startupWaitSatisfied: () =>
                IsStartupWaitSatisfied(renderer));
        if (_smokeOptions.Mode == SampleSmokeMode.SceneTransition)
        {
            _bistroTransitionSmoke = new BistroTransitionSmokeState
            {
                InitialScene = _sceneKind
            };
            Console.WriteLine(
                _sceneKind == SampleSceneKind.SponzaPlaza
                    ? "Bistro transition smoke armed: exact Sponza -> cold Bistro."
                    : "Bistro transition smoke armed: Cornell/GI -> cold Bistro " +
                      "-> Cornell/GI -> resident Bistro.");
        }
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
                $"measure={_smokeOptions.Benchmark.MeasureFrameCount}, " +
                $"vsync={(VSync ? "on" : "off")}, " +
                $"maxFps={(MaximumFramesPerSecond == 0.0 ? "unlimited" : MaximumFramesPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))}");
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

        PrintLoadedSceneSummary(Scene, model);
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
        ApplyPerformanceOptimizationOverrides(renderer.Settings);
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
        ApplyPerformanceOptimizationOverrides(settings);
        if (_smokeOptions.AsyncComputeModeOverride.HasValue)
        {
            settings.AsyncCompute.Mode =
                _smokeOptions.AsyncComputeModeOverride.Value;
        }
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
        if (_smokeOptions.Benchmark.Enabled ||
            _smokeOptions.BenchmarkQualitySequence.Enabled)
        {
            // Pipeline families are immutable for a renderer lifetime. Apply
            // capture-only pipeline requirements before construction, then
            // ApplySmokeRenderSettings repeats the same deterministic delta
            // after scene profiles have established ordinary runtime state.
            SampleBenchmarkCaptureVariant.Apply(
                settings,
                _smokeOptions.Benchmark.Enabled
                    ? _smokeOptions.Benchmark.CaptureVariant
                    : _smokeOptions.BenchmarkQualitySequence.CaptureVariant);
        }
        ApplyAdvancedGiSettings(settings.GlobalIllumination);
        ApplyScenePostOverrides(settings);
    }

    private void ApplyPerformanceOptimizationOverrides(RenderSettings settings)
    {
        if (_smokeOptions.PerformanceOptimizationsEnabledOverride.HasValue)
        {
            settings.PerformanceOptimizations.Enabled = _smokeOptions
                .PerformanceOptimizationsEnabledOverride.Value;
        }
        if (_smokeOptions.PerformanceOptimizationMaskOverride.HasValue)
        {
            settings.PerformanceOptimizations.EnabledFeatures = _smokeOptions
                .PerformanceOptimizationMaskOverride.Value &
                PerformanceOptimizationFeature.All;
        }
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
        if (_smokeOptions.SimpleDdgiReceiverCacheModeOverride.HasValue)
        {
            gi.SimpleDdgiReceiverCacheMode =
                _smokeOptions.SimpleDdgiReceiverCacheModeOverride.Value;
        }
        if (_smokeOptions.SimpleDdgiTransportAccelerationEnabledOverride
                .HasValue)
        {
            gi.SimpleDdgiTransportAccelerationEnabled = _smokeOptions
                .SimpleDdgiTransportAccelerationEnabledOverride.Value;
        }
        if (_smokeOptions.SimpleDdgiTransportAcceleratedSweepCountOverride
                .HasValue)
        {
            gi.SimpleDdgiTransportAcceleratedSweepCount = _smokeOptions
                .SimpleDdgiTransportAcceleratedSweepCountOverride.Value;
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
        AdvanceSceneTransitionHost();
        AdvanceBistroTransitionSmoke();
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
        if (_bistroQualityCaptureRunner != null &&
            Renderer is VulkanRenderer bistroCaptureRenderer &&
            SampleBistroQualityCaptureRunner.IsReadyForCapture(
                bistroCaptureRenderer.StartupSnapshot.FullQualityPresented,
                bistroCaptureRenderer.Settings.GlobalIllumination
                    .SimpleDdgiReceiverCacheMode,
                bistroCaptureRenderer.LastDiagnostics
                    .SimpleDdgiReceiverCache))
        {
            if (_bistroQualityCaptureFrameOrigin < 0)
                _bistroQualityCaptureFrameOrigin = _drawnFrames;
            _bistroQualityCaptureRunner.PrepareFrame(
                _drawnFrames - _bistroQualityCaptureFrameOrigin);
        }
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

        // Bistro runtime preparation also authors the presentation camera.
        // Apply the selected benchmark route last so incident bookmarks remain
        // authoritative while retaining the controller's lighting and settings.
        ApplyBenchmarkNamedTrajectoryFrameControls();

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
        bool hasAuthoredCamera =
            SampleBenchmarkTrajectory.RequiresSponza(trajectory) ||
            SampleBenchmarkTrajectory.RequiresBistro(trajectory);
        if (!(timingEnabled || qualityEnabled) ||
            !hasAuthoredCamera ||
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
                "A named benchmark trajectory did not resolve a camera pose.");
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
        // Console formatting and output are diagnostic work, not a
        // prerequisite for presenting usable pixels. Begin reporting on the
        // frame after the first present so time-to-first-frame measurements do
        // not include a multi-page diagnostics dump.
        if (_drawnFrames > 0)
        {
            _diagnosticsReporter?.PrintFirstFrameDiagnostics(Renderer);
            if (Camera is FirstPersonCamera firstPersonCamera)
            {
                _diagnosticsReporter?.PrintMovementFrameDiagnostics(
                    Renderer,
                    firstPersonCamera);
            }
        }

        CaptureBaselineSnapshotIfRequested();
        if (Renderer is VulkanRenderer benchmarkRenderer)
        {
            _giAllOnQualificationRunner?.OnFrameRendered(
                benchmarkRenderer.LastDiagnostics);
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
            if (_bistroQualityCaptureRunner != null &&
                _bistroQualityCaptureFrameOrigin >= 0)
            {
                _bistroQualityCaptureRunner.OnFrameRendered(
                    _drawnFrames - _bistroQualityCaptureFrameOrigin,
                    benchmarkRenderer.LastDiagnostics);
            }
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

    protected override void OnFramePresented()
    {
        if (_startupVisualCaptureAwaitingPresent)
        {
            _startupVisualCandidatePresentMicroseconds =
                RunElapsedMicroseconds;
            _startupVisualCaptureAwaitingPresent = false;
        }
        _presentedFrameSerial = _presentedFrameSerial == long.MaxValue
            ? 1
            : _presentedFrameSerial + 1;
        RetirePresentedScenes();

        if (_transitionLoadingScene != null &&
            ReferenceEquals(Scene, _transitionLoadingScene))
        {
            _loadingFramePresented = true;
        }

        SampleSceneTransitionSnapshot snapshot =
            _sceneTransition?.Snapshot ??
            SampleSceneTransitionSnapshot.Idle;
        if (snapshot.Phase != SampleSceneTransitionPhase.Completed ||
            snapshot.Generation == _handledTransitionGeneration ||
            ReferenceEquals(Scene, _transitionLoadingScene))
        {
            CompletePostPresentSceneCommit();
            return;
        }

        _handledTransitionGeneration = snapshot.Generation;
        long firstPresentMicroseconds = _transitionRequestTimestamp == 0
            ? snapshot.ElapsedMicroseconds
            : checked((long)Math.Round(
                System.Diagnostics.Stopwatch.GetElapsedTime(
                    _transitionRequestTimestamp).TotalMicroseconds));
        SampleSceneTransitionLatencyEvaluation latency =
            SampleSceneTransitionLatencyPolicy.Evaluate(
                snapshot.Target,
                _transitionWasResident,
                firstPresentMicroseconds);
        ObserveBistroTransitionFirstPresent(
            snapshot.Target,
            _transitionWasResident,
            firstPresentMicroseconds);
        if (snapshot.Target == SampleSceneKind.Bistro)
        {
            ScheduleHybridReflectionPipelinePreparation();
        }
        Console.WriteLine(
            $"Scene transition ready: target={snapshot.Target}, " +
            $"elapsed={firstPresentMicroseconds / 1_000_000.0:F3}s, " +
            $"target<={latency.TargetMicroseconds / 1_000_000.0:F3}s, " +
            $"cache={latency.CacheClass}, " +
            $"outcome={(latency.MeetsTarget ? "target-met" : "above-target")}, " +
            $"phase=first-present.");
        RestoreWindowTitle();
        _sceneTransition!.ResetToIdle();
        CompletePostPresentSceneCommit();
        StartPendingDeferredSceneStreaming();
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
        CancelDeferredSceneStreaming();
        _sceneTransition?.Dispose();
        _sceneTransition = null;
        DiscardPreparedSceneTransition();
        _transitionPreviousScene?.Dispose();
        _transitionPreviousScene = null;
        _transitionLoadingScene = null;
        _loadingSceneInstancesReleased = false;
        _loadingResidencyAssetsReleased = false;
        RetirePresentedScenes(forceAll: true);
        _bistroQualityRuntimeController?.Restore();
        _materialGiCaptureRunner?.CancelIfIncomplete(
            _startupFailure ?? "The application closed before the material/GI capture completed.");
        _khronosMaterialGiRenderedGateRunner?.CancelIfIncomplete(
            _startupFailure ?? "The application closed before the Khronos rendered gate completed.");
        VulkanRenderer? renderer = Renderer as VulkanRenderer;
        RendererDiagnostics diagnostics =
            renderer?.LastDiagnostics ?? RendererDiagnostics.Empty;
        bool allOnQualificationPassed = false;
        if (_giAllOnQualificationRunner != null)
        {
            try
            {
                SampleGiAllOnQualificationReport allOnReport =
                    _giAllOnQualificationRunner.Complete();
                allOnQualificationPassed = allOnReport.Passed;
                if (!allOnReport.Passed)
                {
                    _runtimeSmokeFailure ??=
                        "All-on GI runtime qualification failed: " +
                        string.Join(
                            " ",
                            allOnReport.Failures.Select(static failure =>
                                $"{failure.Name}: {failure.Detail}"));
                }
            }
            catch (Exception ex)
            {
                _runtimeSmokeFailure ??=
                    "All-on GI qualification report finalization failed: " +
                    ex.Message;
            }
        }
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
        else if (SampleGiAllOnQualificationContract
                     .RequiresGeneralSmokeCompletion(
                         allOnQualificationPassed) &&
                 SampleHealthReportEvaluation.FindIncompleteSmokeOperation(
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

    private sealed record SampleSceneBuild(
        Model Model,
        SampleSceneLoader? Loader,
        IReadOnlyList<ParticleEffectInstance> VfxEffects);

    private sealed record PreparedSceneTransition(
        long Generation,
        SampleSceneKind Kind,
        Scene Scene,
        SampleSceneBuild Build,
        bool FirstViewOnly);

    private sealed record PendingPostPresentSceneCommit(
        SampleSceneKind Target,
        SampleAssetManifest? Manifest,
        bool FirstViewOnly,
        SampleSceneKind? ProtectedKind);

    private sealed record DeferredSceneRetirement(
        Scene Scene,
        long RetireAfterPresent);

    private sealed record PendingDeferredSceneStreaming(
        long Generation,
        SampleSceneKind Kind,
        Scene Scene,
        SampleSceneLoader Loader,
        SampleAssetManifest Manifest);

    private sealed class DeferredSceneStreaming
    {
        public required long Generation { get; init; }
        public required SampleSceneKind Kind { get; init; }
        public required Scene Scene { get; init; }
        public required SampleSceneLoader Loader { get; init; }
        public required IReadOnlyList<SampleAssetReference> Assets { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required Task Preparation { get; init; }
        public required long StartedTimestamp { get; init; }
        public bool PreparationObserved { get; set; }
        public int AssetIndex { get; set; }
        public SampleSceneLoader.PreparedAssetAttachment? Attachment
        {
            get;
            set;
        }
    }

    private sealed class DelegateContentLoadProgressSink :
        IContentLoadProgressSink
    {
        private readonly Action<ContentLoadProgressEvent> _report;

        public DelegateContentLoadProgressSink(
            Action<ContentLoadProgressEvent> report)
        {
            _report = report ??
                throw new ArgumentNullException(nameof(report));
        }

        public void Report(ContentLoadProgressEvent progress) =>
            _report(progress);
    }

    private Model LoadSampleScene(
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager)
    {
        SampleSceneBuild build = BuildSampleScene(
            Scene,
            _sceneKind,
            meshManager,
            materialManager,
            lightManager,
            runModelLoadStep: (name, load) =>
                RunStartupStep(name, load));
        PublishSceneBuild(build);
        return build.Model;
    }

    private SampleSceneBuild BuildSampleScene(
        Scene targetScene,
        SampleSceneKind sceneKind,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        bool firstViewOnly = false,
        Func<string, Func<Model>, Model>? runModelLoadStep = null)
    {
        ArgumentNullException.ThrowIfNull(targetScene);
        IReadOnlyList<ParticleEffectInstance> sampleVfxEffects =
            Array.Empty<ParticleEffectInstance>();
        SampleSceneLoader? sceneLoader = null;

        SampleSceneBuild Finish(Model model)
        {
            if (!string.IsNullOrWhiteSpace(
                    _smokeOptions.GiAllOnQualificationReportPath) &&
                SampleGiAllOnQualificationContract.IsSupportedScene(sceneKind))
            {
                ContentManager qualificationContent =
                    Services?.GetRequiredService<ContentManager>() ??
                    throw new InvalidOperationException(
                        "All-on GI scene qualification requires ContentManager.");
                SampleGiAllOnSceneRigSummary rig =
                    SampleGiAllOnSceneRig.Configure(
                        targetScene,
                        sceneKind,
                        qualificationContent,
                        meshManager,
                        materialManager);
                Console.WriteLine(
                    "All-on GI scene rig attached: " +
                    $"c1Asset='{rig.C1AssetPath}', " +
                    $"c1Objects={rig.C1RenderObjectCount}, " +
                    $"injectedC4HeroObjects={rig.C4HeroRenderObjectCount}, " +
                    $"scale={rig.FixtureScale:R}.");
            }
            SampleReflectionPolicy.EnsureProbeFree(targetScene);
            return new SampleSceneBuild(
                model,
                sceneLoader,
                sampleVfxEffects);
        }

        if (sceneKind == SampleSceneKind.MaterialShowcase)
        {
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
                            targetScene,
                            contentManager,
                            materialManager);
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
                        targetScene,
                        meshManager,
                        materialManager,
                        textureManager);
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
                targetScene,
                meshManager,
                materialManager,
                showcaseTextureManager);
            return Finish(new Model { Name = "Material Showcase" });
        }

        if (sceneKind == SampleSceneKind.AnalyticalAreaLights)
        {
            SampleAnalyticalAreaLightRoomScene.Configure(
                targetScene,
                meshManager,
                materialManager);
            return Finish(new Model { Name = "Analytical Area Light Room" });
        }

        if (sceneKind == SampleSceneKind.FoliageShowcase)
        {
            targetScene.Name = "Njulf Foliage Showcase";
            var builder = new SampleStressSceneBuilder(
                targetScene,
                meshManager,
                materialManager,
                lightManager,
                SampleLightingMode.DirectionalKey,
                _sampleStressSceneResources);
            builder.Apply(SamplePerformanceScenario.ForestFoliage);
            return Finish(new Model { Name = "Foliage Showcase" });
        }

        if (sceneKind == SampleSceneKind.GlobalIlluminationTest)
        {
            targetScene.Name = "Njulf GI Test Scene";
            var builder = new SampleStressSceneBuilder(
                targetScene,
                meshManager,
                materialManager,
                lightManager,
                LightingMode,
                _sampleStressSceneResources);
            builder.Apply(SamplePerformanceScenario.GiCornellRoom);
            return Finish(new Model { Name = "GI Test Scene" });
        }

        if (sceneKind == SampleSceneKind.VfxShowcase)
        {
            sampleVfxEffects = SampleVfxShowcaseScene.Configure(
                targetScene,
                meshManager,
                materialManager);
            return Finish(new Model { Name = "VFX Showcase" });
        }

        SampleAssetManifest assetManifest = GetModelSceneManifest(sceneKind)
            ?? throw new InvalidOperationException(
                $"Scene '{sceneKind}' does not have a model asset manifest.");
        sceneLoader = new SampleSceneLoader(
            Content!,
            materialManager,
            meshManager,
            lightManager,
            assetManifest,
            loadSceneDocument: sceneKind == SampleSceneKind.SponzaPlaza,
            sponzaFixtureMode: _smokeOptions.SponzaFixtureMode,
            runModelLoadStep: runModelLoadStep);
        Model model = firstViewOnly
            ? sceneLoader.LoadFirstView(targetScene)
            : sceneLoader.Load(targetScene);
        if (sceneKind == SampleSceneKind.SponzaPlaza &&
            !sceneLoader.LoadedFromDocument)
        {
            SamplePlazaGlobalIllumination.ConfigureSceneLighting(targetScene);
        }
        if (sceneKind == SampleSceneKind.SponzaPlaza &&
            !sceneLoader.LoadedFromDocument &&
            _smokeOptions.SponzaFixtureMode ==
                SampleSponzaFixtureMode.AnimationDemo)
        {
            SampleAnimatedCharacter.Configure(targetScene, Content!);
        }
        if (sceneKind == SampleSceneKind.SponzaPlaza &&
            _smokeOptions.SponzaFixtureMode ==
                SampleSponzaFixtureMode.C5ResidualValidation)
        {
            TextureManager sponzaTextureManager = Services?.GetRequiredService<TextureManager>()
                ?? throw new InvalidOperationException(
                    "The Sponza C5 emissive test sphere requires the renderer TextureManager.");
            SampleSponzaNearFieldResidualTestSphere.Configure(
                targetScene,
                meshManager,
                materialManager,
                sponzaTextureManager);
        }
        return Finish(model);
    }

    internal static (string Message, bool IsWarning)
        FormatInitialContentSummary(
            CookedContentDiagnostics before,
            CookedContentDiagnostics after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        int sourceFallbackCount = Math.Max(
            0,
            after.SourceFallbackCount - before.SourceFallbackCount);
        int cookedModelCount = Math.Max(
            0,
            after.CookedAssetCount - before.CookedAssetCount);
        if (sourceFallbackCount != 0)
        {
            return (
                "WARNING [Njulf.Content]: initial scene used " +
                $"{sourceFallbackCount} source import fallback(s); " +
                "startup timing is degraded.",
                true);
        }

        return (
            "Cooked content: initial scene used no source import fallback " +
            $"(cooked models={cookedModelCount}).",
            false);
    }

    private void PublishSceneBuild(SampleSceneBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _sceneLoader = build.Loader;
        _sampleVfxEffects = build.VfxEffects;
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
            settings.AmbientOcclusion.ResolutionScale =
                SampleBistroGlobalIlluminationProfile
                    .TransitionAmbientOcclusionResolutionScale;
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
            // Keep the scene aligned with the engine-wide tiered C5 default
            // after rollout mutation. ApplySmokeRenderSettings runs after
            // this, so an explicit command-line Off remains authoritative.
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
        if (!string.IsNullOrWhiteSpace(
                _smokeOptions.GiAllOnQualificationReportPath))
        {
            SampleGiAllOnQualificationContract.ApplyIsolationSettings(
                settings,
                _sceneKind);
        }
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
            LightingMode,
            _sampleStressSceneResources));
    }

    private void CycleScene(VulkanRenderer renderer)
    {
        LoadSceneKind(
            GetNextKey3Scene(_sceneKind),
            renderer);
    }

    internal static SampleSceneKind GetNextKey3Scene(SampleSceneKind current)
    {
        SampleSceneKind[] sceneKinds = Enum.GetValues<SampleSceneKind>();
        int index = Array.IndexOf(sceneKinds, current);
        return sceneKinds[(index + 1) % sceneKinds.Length];
    }

    private void CycleSponzaAndBistro(VulkanRenderer renderer)
    {
        SampleSceneKind nextScene = _sceneKind == SampleSceneKind.SponzaPlaza
            ? SampleSceneKind.Bistro
            : SampleSceneKind.SponzaPlaza;
        LoadSceneKind(
            nextScene,
            renderer);
    }

    private bool LoadSceneKind(
        SampleSceneKind sceneKind,
        VulkanRenderer renderer)
    {
        return RequestSceneTransition(sceneKind, renderer);
    }

    private bool RequestSceneTransition(
        SampleSceneKind target,
        VulkanRenderer renderer)
    {
        long feedbackStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        SampleSceneTransitionCoordinator coordinator =
            _sceneTransition ?? throw new InvalidOperationException(
                "Scene transition services are not initialized.");
        SampleSceneResidencyCache residency =
            _sceneResidency ?? throw new InvalidOperationException(
                "Scene residency services are not initialized.");

        if (target == _sceneKind &&
            !coordinator.IsActive &&
            _transitionLoadingScene == null)
        {
            return true;
        }

        _transitionRequestTimestamp = feedbackStarted;
        if (target != SampleSceneKind.Bistro &&
            !_hybridReflectionPreparationStarted)
        {
            _hybridReflectionPreparationEligiblePresentSerial =
                long.MaxValue;
        }

        if (coordinator.IsActive)
            coordinator.Cancel();
        DiscardPreparedSceneTransition();
        CancelDeferredSceneStreaming();
        RestoreDeferredPreviousScene();

        bool alreadyLoading = _transitionLoadingScene != null &&
            ReferenceEquals(Scene, _transitionLoadingScene);
        bool residentCacheHit = residency.Contains(target);
        SampleAssetManifest? targetManifest =
            GetModelSceneManifest(target);
        bool requiresImportedContent = targetManifest != null;
        ulong targetBytes = residentCacheHit || !requiresImportedContent
            ? 0
            : EstimateTransitionAdmissionBytes(
                target,
                targetManifest!);
        SampleSceneTransitionMemoryDecision memory =
            EvaluateSceneTransitionMemory(
                renderer,
                targetBytes);
        bool useLoadingScene = alreadyLoading ||
            !memory.KeepCurrentScene;
        _transitionKeepsPreviousResidency = !useLoadingScene;
        residency.MarkPending(target);

        long generation = coordinator.Request(
            target,
            waitForLoadingFrame: useLoadingScene && !alreadyLoading);
        _transitionWasResident = residentCacheHit;
        if (useLoadingScene && !alreadyLoading)
        {
            try
            {
                ActivateLoadingScene(generation, target, memory);
            }
            catch (Exception activationFailure)
            {
                coordinator.Fail(
                    generation,
                    activationFailure,
                    "the lightweight loading scene could not be activated");
                residency.MarkPending(null);
                Console.Error.WriteLine(
                    $"Scene transition to '{target}' was rejected: " +
                    activationFailure.Message);
                coordinator.ResetToIdle();
                RestoreWindowTitle();
                return false;
            }
        }
        else
            _loadingTransitionGeneration = generation;

        Console.WriteLine(
            $"Scene transition requested: generation={generation}, " +
            $"from={_sceneKind}, to={target}, " +
            $"mode={(useLoadingScene ? "loading-scene" : "overlap")}, " +
            $"reason={memory.Reason}.");
        UpdateSceneTransitionTitle(coordinator.Snapshot);
        long feedbackMicroseconds = checked((long)Math.Round(
            System.Diagnostics.Stopwatch.GetElapsedTime(feedbackStarted)
                .TotalMicroseconds));
        if (feedbackMicroseconds >
            SampleSceneTransitionLatencyPolicy
                .FeedbackTargetMicroseconds)
        {
            Console.WriteLine(
                $"Scene transition feedback hitch: " +
                $"elapsed={feedbackMicroseconds / 1000.0:F3}ms, " +
                $"target<={SampleSceneTransitionLatencyPolicy.FeedbackTargetMicroseconds / 1000.0:F3}ms.");
        }
        ObserveBistroTransitionHitch(feedbackMicroseconds);
        return true;
    }

    private async Task PrepareSceneTransitionAsync(
        SampleSceneKind target,
        IContentLoadProgressSink progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        // The coordinator invokes this callback from the input/update thread.
        // Yield before starting cache lookup or content-manager work so the
        // transition request can publish feedback within the host-step budget.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        long generation = _sceneTransition?.Snapshot.Generation ?? 0L;
        Volatile.Write(
            ref _scenePreparationGeneration,
            generation);
        Task environmentPreparation =
            PrepareSceneEnvironmentResourcesAsync(
                target,
                cancellationToken);
        SampleAssetManifest? manifest = GetModelSceneManifest(target);
        if (manifest == null)
        {
            await environmentPreparation.ConfigureAwait(false);
            progress.Report(new ContentLoadProgressEvent(
                target.ToString(),
                ContentLoadPriority.Critical,
                ContentLoadStage.Ready,
                Message: "procedural scene requires no content preload"));
            return;
        }

        if (_sceneResidency?.Contains(target) == true)
        {
            Task scenePreparation =
                PrepareImportedSceneGraphAsync(
                    generation,
                    target,
                    cancellationToken);
            await Task.WhenAll(
                    environmentPreparation,
                    scenePreparation)
                .ConfigureAwait(false);
            progress.Report(new ContentLoadProgressEvent(
                target.ToString(),
                ContentLoadPriority.Critical,
                ContentLoadStage.Ready,
                checked((long)Math.Min(
                    long.MaxValue,
                    _sceneResidency.GetEstimatedBytes(target))),
                "scene residency cache hit"));
            return;
        }

        IAsyncContentManager content = Services?
            .GetRequiredService<IAsyncContentManager>() ??
            throw new InvalidOperationException(
                "Asynchronous content services are unavailable.");
        SampleAssetReference[] assets = manifest
            .EnumerateAssets(SampleAssetLoadTier.Critical)
            .GroupBy(
                static asset => asset.CreateContentIdentity(),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (assets.Length == 0)
        {
            throw new InvalidOperationException(
                $"Scene '{target}' has no critical first-view assets.");
        }
        foreach (IGrouping<
                     (ModelImportBackend Backend,
                      AssimpMaterialTextureConvention Convention),
                     SampleAssetReference> group in assets.GroupBy(asset =>
                     (asset.ExpectedBackend,
                      asset.AssimpMaterialTextureConvention)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SampleAssetReference[] groupedAssets = group.ToArray();
            ContentPreloadRequest[] requests = groupedAssets
                .Select(asset => new ContentPreloadRequest(
                    asset.Path,
                    ContentLoadPriority.Critical,
                    EstimateAssetPreloadBytes(asset.Path)))
                .ToArray();
            ContentPreloadResult<Model> result = await content
                .PreloadAsync<Model>(
                    requests,
                    new ContentPreloadOptions
                    {
                        MaxConcurrency = 2,
                        MaxInflightBytes =
                            TransitionPreloadInflightBytes,
                        LoadOptions = groupedAssets[0]
                            .CreateLoadOptions(),
                        Progress = progress
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Exception[] failures = result.Items
                .Where(static item => item.Failure != null)
                .Select(static item => item.Failure!)
                .ToArray();
            if (failures.Length == 1)
                throw failures[0];
            if (failures.Length > 1)
            {
                throw new AggregateException(
                    $"Scene '{target}' content preload failed.",
                    failures);
            }
            if (result.CancelledCount != 0)
                throw new OperationCanceledException(cancellationToken);
        }

        Task firstViewPreparation = PrepareImportedSceneGraphAsync(
            generation,
            target,
            cancellationToken);
        await Task.WhenAll(
                environmentPreparation,
                firstViewPreparation)
            .ConfigureAwait(false);
    }

    private async Task PrepareImportedSceneGraphAsync(
        long generation,
        SampleSceneKind target,
        CancellationToken cancellationToken)
    {
        // Bistro's cached model contains thousands of renderer-owned object
        // templates. Cloning them at publication used to monopolize the host
        // thread for hundreds of milliseconds even though GPU upload was
        // already complete. Build an isolated scene on a worker after preload;
        // only the final pointer exchange remains on the deterministic host.
        if (target != SampleSceneKind.Bistro)
            return;

        PreparedSceneTransition prepared = await Task.Run(
                () => CreatePreparedImportedScene(
                    generation,
                    target,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        PreparedSceneTransition? replaced = null;
        bool accepted = false;
        lock (_preparedSceneTransitionGate)
        {
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _scenePreparationGeneration) ==
                    generation)
            {
                replaced = _preparedSceneTransition;
                _preparedSceneTransition = prepared;
                accepted = true;
            }
        }

        replaced?.Scene.Dispose();
        if (accepted)
            return;

        prepared.Scene.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(
            "Scene preparation was superseded by a newer transition.",
            cancellationToken);
    }

    private PreparedSceneTransition CreatePreparedImportedScene(
        long generation,
        SampleSceneKind target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IServiceProvider services = Services ??
            throw new InvalidOperationException(
                "Scene preparation services are unavailable.");
        MeshManager meshManager =
            services.GetRequiredService<MeshManager>();
        MaterialManager materialManager =
            services.GetRequiredService<MaterialManager>();
        LightManager lightManager =
            services.GetRequiredService<LightManager>();
        SampleAssetManifest manifest = GetModelSceneManifest(target) ??
            throw new InvalidOperationException(
                $"Scene '{target}' has no imported asset manifest.");
        bool firstViewOnly = manifest.HasDeferredAssets &&
            _sceneResidency?.GetState(target) !=
                SampleSceneResidencyState.FullyResident;
        var scene = new Scene
        {
            Name = GetSceneDisplayName(target)
        };
        try
        {
            SampleSceneBuild build = BuildSampleScene(
                scene,
                target,
                meshManager,
                materialManager,
                lightManager,
                firstViewOnly);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedSceneTransition(
                generation,
                target,
                scene,
                build,
                firstViewOnly);
        }
        catch
        {
            scene.Dispose();
            throw;
        }
    }

    private PreparedSceneTransition? TakePreparedSceneTransition(
        SampleSceneKind target)
    {
        long generation = _sceneTransition?.Snapshot.Generation ?? 0L;
        lock (_preparedSceneTransitionGate)
        {
            PreparedSceneTransition? prepared =
                _preparedSceneTransition;
            if (prepared == null ||
                prepared.Generation != generation ||
                prepared.Kind != target)
            {
                return null;
            }

            _preparedSceneTransition = null;
            return prepared;
        }
    }

    private void DiscardPreparedSceneTransition()
    {
        PreparedSceneTransition? prepared;
        lock (_preparedSceneTransitionGate)
        {
            prepared = _preparedSceneTransition;
            _preparedSceneTransition = null;
        }

        prepared?.Scene.Dispose();
    }

    private Task PrepareSceneEnvironmentResourcesAsync(
        SampleSceneKind target,
        CancellationToken cancellationToken)
    {
        if (target != SampleSceneKind.Bistro ||
            Renderer is not VulkanRenderer renderer)
        {
            return Task.CompletedTask;
        }

        var targetSettings = new RenderSettings();
        SampleBistroGlobalIlluminationProfile.Configure(targetSettings);
        return renderer.PrepareEnvironmentResourcesAsync(
            targetSettings.Environment,
            cancellationToken);
    }

    private void AdvanceSceneTransitionHost()
    {
        TryBeginHybridReflectionPipelinePreparation();
        SampleSceneTransitionCoordinator? coordinator =
            _sceneTransition;
        if (coordinator == null)
            return;

        if (_loadingFramePresented &&
            coordinator.Snapshot.Phase ==
                SampleSceneTransitionPhase.WaitingForLoadingFrame &&
            coordinator.Snapshot.Generation ==
                _loadingTransitionGeneration)
        {
            long releaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            string releaseStage = _loadingSceneInstancesReleased
                ? _loadingResidencyAssetsReleased
                    ? "loading-content-cache-release"
                    : "loading-residency-asset-release"
                : "loading-scene-instance-release";
            try
            {
                ReleasePreviousSceneForLoadingTransition(coordinator);
            }
            catch (Exception releaseFailure)
            {
                coordinator.Fail(
                    _loadingTransitionGeneration,
                    releaseFailure,
                    "the previous scene could not be released after the loading frame");
            }
            ReportTransitionHitch(
                releaseStage,
                releaseStarted);
        }

        if (_contentUploadPump?.PendingCount > 0)
        {
            ContentUploadPumpResult upload =
                _contentUploadPump.ProcessFrame(
                    TransitionUploadCpuBudget,
                    maximumCallbacks:
                        TransitionUploadCallbacksPerFrame,
                    maximumSubmissionBytes:
                        TransitionUploadSubmissionBytes);
            if (upload.ProcessedCount > 0)
            {
                coordinator.ObserveHostActivity(
                    coordinator.Snapshot.Generation);
            }
            ObserveBistroTransitionHitch(
                upload.ElapsedMicroseconds);
            if (upload.ElapsedMicroseconds >
                SampleSceneTransitionLatencyPolicy
                    .HitchTargetMicroseconds)
            {
                Console.WriteLine(
                    $"Scene transition upload hitch: " +
                    $"elapsed={upload.ElapsedMicroseconds / 1000.0:F3}ms, " +
                    $"remaining={upload.RemainingCount}.");
            }
        }

        long advanceStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        SampleSceneTransitionPhase phaseBeforeAdvance =
            coordinator.Snapshot.Phase;
        coordinator.Advance();
        AdvanceDeferredSceneStreaming();
        SampleSceneTransitionPhase phaseAfterAdvance =
            coordinator.Snapshot.Phase;
        bool committedDuringAdvance =
            phaseAfterAdvance is (
                SampleSceneTransitionPhase.Completed or
                SampleSceneTransitionPhase.Failed) &&
            phaseBeforeAdvance is (
                SampleSceneTransitionPhase.Decoding or
                SampleSceneTransitionPhase.WaitingForUpload);
        ReportTransitionHitch(
            committedDuringAdvance
                ? "scene-commit"
                : "transition-advance",
            advanceStarted);
        SampleSceneTransitionSnapshot snapshot = coordinator.Snapshot;
        if (snapshot.Phase != _lastReportedTransitionPhase)
        {
            _lastReportedTransitionPhase = snapshot.Phase;
            if (snapshot.Phase != SampleSceneTransitionPhase.Idle)
            {
                Console.WriteLine(
                    $"Scene transition: generation={snapshot.Generation}, " +
                    $"target={snapshot.Target}, phase={snapshot.Phase}, " +
                    $"progress={snapshot.Progress:P0}, " +
                    $"detail={snapshot.Detail}.");
            }
        }
        UpdateSceneTransitionTitle(snapshot);

        if (snapshot.Generation == 0 ||
            snapshot.Generation == _handledTransitionGeneration ||
            snapshot.Phase is not (
                SampleSceneTransitionPhase.Cancelled or
                SampleSceneTransitionPhase.Failed))
        {
            return;
        }

        _handledTransitionGeneration = snapshot.Generation;
        _sceneResidency?.MarkPending(null);
        DiscardPreparedSceneTransition();
        if (snapshot.Phase == SampleSceneTransitionPhase.Failed)
        {
            Console.Error.WriteLine(
                $"Scene transition to '{snapshot.Target}' failed: " +
                $"{snapshot.Failure?.GetType().Name}: " +
                $"{snapshot.Detail}");
            FailBistroTransitionSmoke(
                $"Transition to '{snapshot.Target}' failed: " +
                snapshot.Detail);
        }

        if (_transitionPreviousScene != null)
        {
            RestoreDeferredPreviousScene();
        }
        else if (_transitionLoadingScene != null &&
                 ReferenceEquals(Scene, _transitionLoadingScene))
        {
            RecoverSafeSceneAfterTransitionFailure();
        }

        RestoreWindowTitle();
        coordinator.ResetToIdle();
    }

    private void CommitPreparedScene(
        SampleSceneKind target,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera)
    {
        long commitProfileStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        long commitPhaseStarted = commitProfileStarted;
        long snapshotMicroseconds;
        long buildMicroseconds = 0;
        long publicationMicroseconds = 0;
        long applyStateMicroseconds = 0;
        long rendererPreparationMicroseconds = 0;
        long exchangeMicroseconds = 0;
        long retirementMicroseconds;
        long residencyMicroseconds;
        PreparedSceneTransition? prepared =
            TakePreparedSceneTransition(target);
        Scene targetScene = prepared?.Scene ?? new Scene
        {
            Name = GetSceneDisplayName(target)
        };
        Scene activeScene = Scene;
        SampleSceneKind previousKind = _sceneKind;
        SampleSceneLoader? previousLoader = _sceneLoader;
        IReadOnlyList<ParticleEffectInstance>? previousVfx =
            _sampleVfxEffects;
        SamplePerformanceScenarioRunner? previousScenario =
            _performanceScenarioRunner;
        LightRecord[] previousLights = lightManager.GetLightRecords()
            .ToArray();
        CoreVector3 previousCameraPosition = camera.Position;
        float previousCameraYaw = camera.Yaw;
        float previousCameraPitch = camera.Pitch;
        float previousCameraFov = camera.FieldOfView;
        float previousCameraNear = camera.NearPlane;
        float previousCameraFar = camera.FarPlane;
        bool exchanged = false;
        SampleAssetManifest? targetManifest =
            GetModelSceneManifest(target);
        bool firstViewOnly = prepared?.FirstViewOnly ??
            (targetManifest?.HasDeferredAssets == true &&
             _sceneResidency?.GetState(target) !=
                 SampleSceneResidencyState.FullyResident);
        SampleSceneBuild? committedBuild = null;
        snapshotMicroseconds = ElapsedMicroseconds(
            commitPhaseStarted);

        try
        {
            commitPhaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            lightManager.ClearLights();
            SampleSceneBuild build = prepared?.Build ??
                BuildSampleScene(
                    targetScene,
                    target,
                    meshManager,
                    materialManager,
                    lightManager,
                    firstViewOnly);
            committedBuild = build;
            buildMicroseconds = ElapsedMicroseconds(
                commitPhaseStarted);

            commitPhaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            _sceneKind = target;
            PublishSceneBuild(build);
            renderer.CaptureSceneKind =
                GetPerformanceCaptureSceneKind(target);
            if (target == SampleSceneKind.Bistro)
            {
                // Reserve the optional reflection family before Bistro's
                // settings become active. Driver work remains dormant until
                // this exterior has presented once.
                renderer.DeferHybridReflectionPipelinePreparation();
            }
            publicationMicroseconds = ElapsedMicroseconds(
                commitPhaseStarted);
            commitPhaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyLoadedSceneState(
                targetScene,
                meshManager,
                materialManager,
                lightManager,
                renderer,
                camera,
                build.Model);
            applyStateMicroseconds = ElapsedMicroseconds(
                commitPhaseStarted);
            commitPhaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            renderer.PrepareScene(targetScene, camera);
            rendererPreparationMicroseconds = ElapsedMicroseconds(
                commitPhaseStarted);
            commitPhaseStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            Scene previous = ExchangeScene(targetScene);
            if (!ReferenceEquals(previous, activeScene))
            {
                throw new InvalidOperationException(
                    "The active scene changed while a prepared scene was being committed.");
            }
            exchanged = true;
            exchangeMicroseconds = ElapsedMicroseconds(
                commitPhaseStarted);
#if NJULF_EDITOR
            _pendingEditorScene = targetScene;
#endif
        }
        catch
        {
            if (exchanged)
            {
                Scene failedScene = ExchangeScene(activeScene);
                if (!ReferenceEquals(failedScene, targetScene))
                {
                    throw new InvalidOperationException(
                        "Scene rollback observed an unexpected active scene.");
                }
            }

            _sceneKind = previousKind;
            _sceneLoader = previousLoader;
            _sampleVfxEffects = previousVfx;
            _performanceScenarioRunner = previousScenario;
            renderer.CaptureSceneKind =
                GetPerformanceCaptureSceneKind(previousKind);
            UpdateVfxVolumetricDemoOverrideOwnership(
                renderer.Settings);
            SampleLighting.ConfigureRenderSettings(
                renderer.Settings,
                ResolveSceneLightingMode());
            ConfigureSceneEnvironment(renderer);
            ConfigureSceneRenderSettings(renderer);
            ApplySmokeRenderSettings(renderer);
            RestoreLights(lightManager, previousLights);
            camera.Position = previousCameraPosition;
            camera.Yaw = previousCameraYaw;
            camera.Pitch = previousCameraPitch;
            camera.FieldOfView = previousCameraFov;
            camera.NearPlane = previousCameraNear;
            camera.FarPlane = previousCameraFar;
            camera.Update();
            _inputController?.SetParticleEffects(previousVfx);
            _inputController?.SetLightingMode(
                ResolveSceneLightingMode());
            _inputController?.SetPerformanceScenarioRunner(
                previousScenario);
#if NJULF_EDITOR
            _pendingEditorScene = null;
            _editorController?.SetScene(activeScene);
#endif
            targetScene.Dispose();
            throw;
        }

        commitPhaseStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        if (ReferenceEquals(activeScene, _transitionLoadingScene))
        {
            _transitionLoadingScene = null;
            _loadingFramePresented = false;
            _loadingSceneInstancesReleased = false;
            _loadingResidencyAssetsReleased = false;
        }
        _transitionPreviousScene = null;
        QueueSceneRetirement(activeScene);
        retirementMicroseconds = ElapsedMicroseconds(
            commitPhaseStarted);

        commitPhaseStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();
        _pendingPostPresentSceneCommit =
            new PendingPostPresentSceneCommit(
                target,
                targetManifest,
                firstViewOnly,
                _transitionKeepsPreviousResidency
                    ? previousKind
                    : null);
        residencyMicroseconds = ElapsedMicroseconds(
            commitPhaseStarted);

        if (firstViewOnly &&
            targetManifest != null &&
            committedBuild?.Loader != null)
        {
            QueueDeferredSceneStreaming(
                _sceneTransition?.Snapshot.Generation ?? 0,
                target,
                targetScene,
                committedBuild.Loader,
                targetManifest);
        }

        long totalMicroseconds = ElapsedMicroseconds(
            commitProfileStarted);
        if (totalMicroseconds >
            SampleSceneTransitionLatencyPolicy.HitchTargetMicroseconds)
        {
            Console.WriteLine(
                $"Scene commit profile: target={target}, " +
                $"prepared={(prepared != null ? 1 : 0)}, " +
                $"total={totalMicroseconds / 1000.0:F3}ms, " +
                $"snapshot={snapshotMicroseconds / 1000.0:F3}ms, " +
                $"build={buildMicroseconds / 1000.0:F3}ms, " +
                $"publish={publicationMicroseconds / 1000.0:F3}ms, " +
                $"state={applyStateMicroseconds / 1000.0:F3}ms, " +
                $"renderer={rendererPreparationMicroseconds / 1000.0:F3}ms, " +
                $"exchange={exchangeMicroseconds / 1000.0:F3}ms, " +
                $"retire={retirementMicroseconds / 1000.0:F3}ms, " +
                $"residency={residencyMicroseconds / 1000.0:F3}ms.");
        }
    }

    private void CompletePostPresentSceneCommit()
    {
        PendingPostPresentSceneCommit? pending =
            _pendingPostPresentSceneCommit;
        _pendingPostPresentSceneCommit = null;
        if (pending != null && Renderer is VulkanRenderer renderer)
        {
            try
            {
                if (pending.Manifest != null && pending.FirstViewOnly)
                {
                    SampleAssetReference[] criticalAssets = pending.Manifest
                        .EnumerateAssets(SampleAssetLoadTier.Critical)
                        .ToArray();
                    _sceneResidency?.Capture(
                        pending.Target,
                        criticalAssets,
                        EstimateAssetTierResidencyBytes(
                            pending.Target,
                            pending.Manifest,
                            SampleAssetLoadTier.Critical),
                        SampleSceneResidencyState.FirstViewReady);
                }
                else
                {
                    _sceneResidency?.Capture(
                        pending.Target,
                        pending.Manifest,
                        EstimateSceneResidencyBytes(pending.Target));
                }

                _sceneResidency?.MarkActive(pending.Target);
                SampleSceneTransitionMemoryDecision memory =
                    EvaluateSceneTransitionMemory(renderer, 0);
                IReadOnlyList<SampleSceneKind> evicted =
                    _sceneResidency?.Trim(
                        memory.EffectiveBudgetBytes,
                        ResolveCurrentGpuUsage(renderer),
                        pending.ProtectedKind) ??
                    Array.Empty<SampleSceneKind>();
                if (evicted.Count > 0)
                {
                    Console.WriteLine(
                        "Scene residency evicted: " +
                        string.Join(", ", evicted));
                }
            }
            catch (Exception residencyFailure)
            {
                _runtimeSmokeFailure ??=
                    "Scene residency maintenance failed after publication: " +
                    residencyFailure.Message;
                Console.Error.WriteLine(_runtimeSmokeFailure);
            }
        }

#if NJULF_EDITOR
        Scene? editorScene = _pendingEditorScene;
        _pendingEditorScene = null;
        if (editorScene != null && ReferenceEquals(editorScene, Scene))
            _editorController?.SetScene(editorScene);
#endif
    }

    private void QueueDeferredSceneStreaming(
        long generation,
        SampleSceneKind kind,
        Scene scene,
        SampleSceneLoader loader,
        SampleAssetManifest manifest)
    {
        CancelDeferredSceneStreaming();
        _pendingDeferredSceneStreaming = new PendingDeferredSceneStreaming(
            generation,
            kind,
            scene,
            loader,
            manifest);
        Console.WriteLine(
            $"Scene first view queued: target={kind}; deferred content " +
            "will start after the first present.");
    }

    private void StartPendingDeferredSceneStreaming()
    {
        PendingDeferredSceneStreaming? pending =
            _pendingDeferredSceneStreaming;
        if (pending == null)
            return;
        _pendingDeferredSceneStreaming = null;
        if (!ReferenceEquals(pending.Scene, Scene) ||
            pending.Kind != _sceneKind)
        {
            return;
        }

        StartDeferredSceneStreaming(
            pending.Generation,
            pending.Kind,
            pending.Scene,
            pending.Loader,
            pending.Manifest);
    }

    private void StartDeferredSceneStreaming(
        long generation,
        SampleSceneKind kind,
        Scene scene,
        SampleSceneLoader loader,
        SampleAssetManifest manifest)
    {
        SampleAssetReference[] assets = manifest
            .EnumerateAssets(SampleAssetLoadTier.Deferred)
            .GroupBy(
                static asset => asset.CreateContentIdentity(),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (assets.Length == 0)
            return;

        CancelDeferredSceneStreaming();
        var cancellation = new CancellationTokenSource();
        var progress = new DelegateContentLoadProgressSink(
            ReportDeferredSceneProgress);
        Task preparation = PreloadDeferredSceneAssetsAsync(
            assets,
            progress,
            cancellation.Token);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _deferredLastProgressTimestamp = started;
        _deferredLastCompletedBytes = 0;
        _deferredWatchdogWarned = 0;
        _deferredSceneProgress = null;
        _deferredSceneStreaming = new DeferredSceneStreaming
        {
            Generation = generation,
            Kind = kind,
            Scene = scene,
            Loader = loader,
            Assets = assets,
            Cancellation = cancellation,
            Preparation = preparation,
            StartedTimestamp = started
        };
        Console.WriteLine(
            $"Scene first view ready: target={kind}, " +
            $"deferredAssets={assets.Length}; streaming full residency.");
    }

    private async Task PreloadDeferredSceneAssetsAsync(
        IReadOnlyList<SampleAssetReference> assets,
        IContentLoadProgressSink progress,
        CancellationToken cancellationToken)
    {
        IAsyncContentManager content = Services?
            .GetRequiredService<IAsyncContentManager>() ??
            throw new InvalidOperationException(
                "Asynchronous content services are unavailable.");
        foreach (IGrouping<
                     (ModelImportBackend Backend,
                      AssimpMaterialTextureConvention Convention),
                     SampleAssetReference> group in assets.GroupBy(asset =>
                     (asset.ExpectedBackend,
                      asset.AssimpMaterialTextureConvention)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SampleAssetReference[] groupedAssets = group.ToArray();
            ContentPreloadResult<Model> result = await content
                .PreloadAsync<Model>(
                    groupedAssets.Select(asset =>
                        new ContentPreloadRequest(
                            asset.Path,
                            ContentLoadPriority.Normal,
                            EstimateAssetPreloadBytes(asset.Path))),
                    new ContentPreloadOptions
                    {
                        MaxConcurrency = 1,
                        MaxInflightBytes =
                            TransitionPreloadInflightBytes,
                        LoadOptions = groupedAssets[0]
                            .CreateLoadOptions(),
                        Progress = progress
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            Exception[] failures = result.Items
                .Where(static item => item.Failure != null)
                .Select(static item => item.Failure!)
                .ToArray();
            if (failures.Length == 1)
                throw failures[0];
            if (failures.Length > 1)
            {
                throw new AggregateException(
                    "Deferred scene content preload failed.",
                    failures);
            }
            if (result.CancelledCount != 0)
                throw new OperationCanceledException(cancellationToken);
        }
    }

    private void ReportDeferredSceneProgress(
        ContentLoadProgressEvent progress)
    {
        ContentLoadProgressEvent? previous =
            Volatile.Read(ref _deferredSceneProgress);
        Volatile.Write(ref _deferredSceneProgress, progress);
        long completed = Math.Max(0, progress.CompletedBytes);
        if (completed > Interlocked.Read(
                ref _deferredLastCompletedBytes) ||
            previous?.Stage != progress.Stage)
        {
            Interlocked.Exchange(
                ref _deferredLastCompletedBytes,
                completed);
            Interlocked.Exchange(
                ref _deferredLastProgressTimestamp,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    private void AdvanceDeferredSceneStreaming()
    {
        DeferredSceneStreaming? streaming =
            _deferredSceneStreaming;
        if (streaming == null)
            return;
        if (!ReferenceEquals(streaming.Scene, Scene) ||
            streaming.Kind != _sceneKind)
        {
            CancelDeferredSceneStreaming();
            return;
        }

        if (!streaming.Preparation.IsCompleted)
        {
            TimeSpan stalled =
                System.Diagnostics.Stopwatch.GetElapsedTime(
                    Interlocked.Read(
                        ref _deferredLastProgressTimestamp));
            if (stalled >= TimeSpan.FromSeconds(2) &&
                Interlocked.CompareExchange(
                    ref _deferredWatchdogWarned,
                    1,
                    0) == 0)
            {
                Console.WriteLine(
                    $"Deferred scene upload watchdog: target={streaming.Kind}, " +
                    $"no progress for {stalled.TotalSeconds:F1}s.");
            }
            if (stalled >= TimeSpan.FromSeconds(30) &&
                !streaming.Cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"Deferred scene upload degraded: target={streaming.Kind}, " +
                    "no progress for 30s; keeping the usable first view.");
                streaming.Cancellation.Cancel();
            }

            UpdateDeferredSceneTitle(streaming);
            return;
        }

        if (!streaming.PreparationObserved)
        {
            try
            {
                streaming.Preparation.GetAwaiter().GetResult();
                streaming.PreparationObserved = true;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(
                    $"Deferred scene streaming cancelled: " +
                    $"target={streaming.Kind}; first view remains usable.");
                FinishDeferredSceneStreaming(streaming);
                return;
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine(
                    $"Deferred scene streaming degraded: target={streaming.Kind}, " +
                    $"{failure.GetType().Name}: {failure.Message}. " +
                    "The first view remains active.");
                FinishDeferredSceneStreaming(streaming);
                return;
            }
        }

        if (streaming.AssetIndex < streaming.Assets.Count)
        {
            long attachmentStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            bool beganAttachment = streaming.Attachment == null;
            streaming.Attachment ??=
                streaming.Loader.BeginPreparedAssetAttachment(
                    streaming.Assets[streaming.AssetIndex]);
            SampleSceneLoader.PreparedAssetAttachment attachment =
                streaming.Attachment;
            long attachmentBeginMicroseconds = checked((long)Math.Round(
                System.Diagnostics.Stopwatch.GetElapsedTime(
                        attachmentStarted)
                    .TotalMicroseconds));
            int attached = streaming.Loader.AdvancePreparedAssetAttachment(
                streaming.Scene,
                attachment,
                maximumRenderObjects: 512,
                maximumCpuTime: DeferredSceneAttachmentCpuBudget);
            if (attachment.Completed)
            {
                streaming.AssetIndex++;
                streaming.Attachment = null;
            }
            long titleStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateDeferredSceneTitle(streaming);
            long titleMicroseconds = checked((long)Math.Round(
                System.Diagnostics.Stopwatch.GetElapsedTime(titleStarted)
                    .TotalMicroseconds));
            long totalMicroseconds = checked((long)Math.Round(
                System.Diagnostics.Stopwatch.GetElapsedTime(
                        attachmentStarted)
                    .TotalMicroseconds));
            if (totalMicroseconds >
                SampleSceneTransitionLatencyPolicy.HitchTargetMicroseconds)
            {
                Console.WriteLine(
                    "Deferred scene attachment hitch: " +
                    $"total={totalMicroseconds / 1000.0:F3}ms, " +
                    $"begin={attachmentBeginMicroseconds / 1000.0:F3}ms, " +
                    $"clone={attachment.LastCloneMicroseconds / 1000.0:F3}ms, " +
                    $"scene={attachment.LastSceneAttachmentMicroseconds / 1000.0:F3}ms, " +
                    $"title={titleMicroseconds / 1000.0:F3}ms, " +
                    $"objects={attached}, first={beganAttachment}.");
            }
            return;
        }

        SampleAssetManifest? manifest =
            GetModelSceneManifest(streaming.Kind);
        if (manifest != null)
        {
            _sceneResidency?.Capture(
                streaming.Kind,
                streaming.Assets,
                EstimateSceneResidencyBytes(streaming.Kind),
                SampleSceneResidencyState.FullyResident);
            _sceneResidency?.MarkActive(streaming.Kind);
        }
        double elapsedSeconds =
            System.Diagnostics.Stopwatch.GetElapsedTime(
                streaming.StartedTimestamp).TotalSeconds;
        Console.WriteLine(
            $"Scene fully resident: target={streaming.Kind}, " +
            $"elapsed={elapsedSeconds:F3}s, " +
            $"deferredAssets={streaming.Assets.Count}.");
        FinishDeferredSceneStreaming(streaming);
    }

    private void UpdateDeferredSceneTitle(
        DeferredSceneStreaming streaming)
    {
        if (Window == null)
            return;
        ContentLoadProgressEvent? progress =
            Volatile.Read(ref _deferredSceneProgress);
        double fraction = progress is
            { TotalBytes: > 0 }
                ? Math.Clamp(
                    progress.CompletedBytes /
                    (double)progress.TotalBytes,
                    0.0,
                    1.0)
                : streaming.PreparationObserved
                    ? streaming.AssetIndex /
                      (double)Math.Max(1, streaming.Assets.Count)
                    : 0.0;
        string phase = streaming.PreparationObserved
            ? "Attaching interior"
            : progress?.Stage.ToString() ?? "Preparing interior";
        string title = $"{WindowTitle} - {phase} {fraction:P0}";
        if (!string.Equals(
                Window.Title,
                title,
                StringComparison.Ordinal))
        {
            Window.Title = title;
        }
    }

    private void FinishDeferredSceneStreaming(
        DeferredSceneStreaming streaming)
    {
        if (!ReferenceEquals(_deferredSceneStreaming, streaming))
            return;
        _deferredSceneStreaming = null;
        _deferredSceneProgress = null;
        streaming.Cancellation.Dispose();
        RestoreWindowTitle();
    }

    private void CancelDeferredSceneStreaming()
    {
        _pendingDeferredSceneStreaming = null;
        DeferredSceneStreaming? streaming =
            _deferredSceneStreaming;
        if (streaming == null)
            return;
        _deferredSceneStreaming = null;
        _deferredSceneProgress = null;
        streaming.Cancellation.Cancel();
        _ = streaming.Preparation.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            streaming.Cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void AdvanceBistroTransitionSmoke()
    {
        BistroTransitionSmokeState? smoke =
            _bistroTransitionSmoke;
        if (smoke == null ||
            smoke.Phase == BistroTransitionSmokePhase.Completed)
        {
            return;
        }
        if (smoke.Phase !=
                BistroTransitionSmokePhase.WaitingForInitialFrame &&
            smoke.InitialScene == SampleSceneKind.SponzaPlaza &&
            _sceneKind == SampleSceneKind.GlobalIlluminationTest)
        {
            smoke.CornellRecoveryObserved = true;
        }

        VulkanRenderer renderer = Renderer as VulkanRenderer ??
            throw new InvalidOperationException(
                "The Bistro transition smoke requires VulkanRenderer.");
        switch (smoke.Phase)
        {
            case BistroTransitionSmokePhase.WaitingForInitialFrame:
                if (_drawnFrames <= 0)
                    return;
                if (_sceneKind is not (
                    SampleSceneKind.GlobalIlluminationTest or
                    SampleSceneKind.SponzaPlaza))
                {
                    FailBistroTransitionSmoke(
                        "The smoke did not start in the Cornell/GI or Sponza scene.");
                    return;
                }

                smoke.InitialScene = _sceneKind;
                smoke.ValidationErrorBaseline =
                    renderer.ValidationMessageSnapshot.ErrorCount;
                smoke.SourceFallbackBaseline =
                    (Content as ContentManager)?.CookedDiagnostics
                        .SourceFallbackCount ?? 0;
                smoke.ColdStartedTimestamp =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                BeginBistroTransitionSmokeTransition(
                    smoke,
                    BistroTransitionSmokePhase.ColdBistro,
                    SampleSceneKind.Bistro);
                return;

            case BistroTransitionSmokePhase.ColdBistro:
                if (HasBistroTransitionSmokeTimedOut(
                        smoke,
                        TimeSpan.FromSeconds(45)))
                {
                    FailBistroTransitionSmoke(
                        "Cold Bistro did not reach full residency within 45 seconds.");
                    return;
                }

                if (smoke.ColdFirstPresentMicroseconds < 0 ||
                    _sceneKind != SampleSceneKind.Bistro ||
                    _deferredSceneStreaming != null ||
                    _sceneResidency?.GetState(
                        SampleSceneKind.Bistro) !=
                    SampleSceneResidencyState.FullyResident)
                {
                    return;
                }

                smoke.ColdFullResidencyMicroseconds =
                    ElapsedMicroseconds(
                        smoke.ColdStartedTimestamp);
                if (smoke.InitialScene ==
                    SampleSceneKind.SponzaPlaza)
                {
                    smoke.Phase = BistroTransitionSmokePhase.Settling;
                    smoke.PhaseStartedTimestamp =
                        System.Diagnostics.Stopwatch.GetTimestamp();
                    smoke.SettleAfterPresentSerial =
                        _presentedFrameSerial > long.MaxValue - 4
                            ? long.MaxValue
                            : _presentedFrameSerial + 4;
                    return;
                }
                BeginBistroTransitionSmokeTransition(
                    smoke,
                    BistroTransitionSmokePhase.ReturningToGi,
                    SampleSceneKind.GlobalIlluminationTest);
                return;

            case BistroTransitionSmokePhase.ReturningToGi:
                if (HasBistroTransitionSmokeTimedOut(
                        smoke,
                        TimeSpan.FromSeconds(10)))
                {
                    FailBistroTransitionSmoke(
                        "The return to Cornell/GI did not present within 10 seconds.");
                    return;
                }
                if (smoke.ReturnFirstPresentMicroseconds < 0)
                    return;

                smoke.WarmStartedTimestamp =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                BeginBistroTransitionSmokeTransition(
                    smoke,
                    BistroTransitionSmokePhase.WarmBistro,
                    SampleSceneKind.Bistro);
                return;

            case BistroTransitionSmokePhase.WarmBistro:
                if (HasBistroTransitionSmokeTimedOut(
                        smoke,
                        TimeSpan.FromSeconds(10)))
                {
                    FailBistroTransitionSmoke(
                        "Resident Bistro did not present within 10 seconds.");
                    return;
                }
                if (smoke.WarmFirstPresentMicroseconds < 0)
                    return;

                smoke.Phase = BistroTransitionSmokePhase.Settling;
                smoke.PhaseStartedTimestamp =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                smoke.SettleAfterPresentSerial =
                    _presentedFrameSerial > long.MaxValue - 4
                        ? long.MaxValue
                        : _presentedFrameSerial + 4;
                return;

            case BistroTransitionSmokePhase.Settling:
                if (_presentedFrameSerial <
                    smoke.SettleAfterPresentSerial)
                {
                    return;
                }

                CompleteBistroTransitionSmoke(smoke, renderer);
                return;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void BeginBistroTransitionSmokeTransition(
        BistroTransitionSmokeState smoke,
        BistroTransitionSmokePhase phase,
        SampleSceneKind target)
    {
        smoke.Phase = phase;
        smoke.PhaseStartedTimestamp =
            System.Diagnostics.Stopwatch.GetTimestamp();
        if (RequestSceneTransition(
                target,
                Renderer as VulkanRenderer ??
                throw new InvalidOperationException(
                    "The Bistro transition smoke requires VulkanRenderer.")))
        {
            return;
        }

        FailBistroTransitionSmoke(
            $"The transition request to '{target}' was rejected.");
    }

    private void ObserveBistroTransitionFirstPresent(
        SampleSceneKind target,
        bool residentCacheHit,
        long elapsedMicroseconds)
    {
        BistroTransitionSmokeState? smoke =
            _bistroTransitionSmoke;
        if (smoke == null)
            return;

        switch (smoke.Phase)
        {
            case BistroTransitionSmokePhase.ColdBistro
                when target == SampleSceneKind.Bistro:
                smoke.ColdFirstPresentMicroseconds =
                    elapsedMicroseconds;
                smoke.ColdWasResident = residentCacheHit;
                break;
            case BistroTransitionSmokePhase.ReturningToGi
                when target ==
                    SampleSceneKind.GlobalIlluminationTest:
                smoke.ReturnFirstPresentMicroseconds =
                    elapsedMicroseconds;
                break;
            case BistroTransitionSmokePhase.WarmBistro
                when target == SampleSceneKind.Bistro:
                smoke.WarmFirstPresentMicroseconds =
                    elapsedMicroseconds;
                smoke.WarmWasResident = residentCacheHit;
                break;
        }
    }

    private void ObserveBistroTransitionHitch(
        long elapsedMicroseconds)
    {
        BistroTransitionSmokeState? smoke =
            _bistroTransitionSmoke;
        if (smoke == null || elapsedMicroseconds < 0)
            return;
        smoke.MaximumHostStepMicroseconds = Math.Max(
            smoke.MaximumHostStepMicroseconds,
            elapsedMicroseconds);
    }

    private void CompleteBistroTransitionSmoke(
        BistroTransitionSmokeState smoke,
        VulkanRenderer renderer)
    {
        int validationErrors = Math.Max(
            0,
            renderer.ValidationMessageSnapshot.ErrorCount -
            smoke.ValidationErrorBaseline);
        int sourceFallbacks = Math.Max(
            0,
            ((Content as ContentManager)?.CookedDiagnostics
                .SourceFallbackCount ?? smoke.SourceFallbackBaseline) -
            smoke.SourceFallbackBaseline);
        bool exactSponzaPath = smoke.InitialScene ==
            SampleSceneKind.SponzaPlaza;
        bool returnAndWarmPassed = exactSponzaPath ||
            smoke.WarmWasResident &&
            smoke.ReturnFirstPresentMicroseconds <=
                SampleSceneTransitionLatencyPolicy
                    .WarmOrProceduralTargetMicroseconds &&
            smoke.WarmFirstPresentMicroseconds <=
                SampleSceneTransitionLatencyPolicy
                    .WarmOrProceduralTargetMicroseconds;
        bool passed =
            !smoke.ColdWasResident &&
            smoke.ColdFirstPresentMicroseconds <=
                SampleSceneTransitionLatencyPolicy
                    .ColdBistroTargetMicroseconds &&
            smoke.ColdFullResidencyMicroseconds <=
                SampleSceneTransitionLatencyPolicy
                    .ColdBistroFullResidencyTargetMicroseconds &&
            returnAndWarmPassed &&
            smoke.MaximumHostStepMicroseconds <=
                SampleSceneTransitionLatencyPolicy
                    .HitchTargetMicroseconds &&
            validationErrors == 0 &&
            sourceFallbacks == 0 &&
            !smoke.CornellRecoveryObserved;
        string detail =
            $"coldFirst={smoke.ColdFirstPresentMicroseconds / 1000.0:F3}ms/" +
            $"{SampleSceneTransitionLatencyPolicy.ColdBistroTargetMicroseconds / 1000.0:F3}ms, " +
            $"coldFull={smoke.ColdFullResidencyMicroseconds / 1000.0:F3}ms/" +
            $"{SampleSceneTransitionLatencyPolicy.ColdBistroFullResidencyTargetMicroseconds / 1000.0:F3}ms, " +
            $"return={smoke.ReturnFirstPresentMicroseconds / 1000.0:F3}ms, " +
            $"warm={smoke.WarmFirstPresentMicroseconds / 1000.0:F3}ms/" +
            $"{SampleSceneTransitionLatencyPolicy.WarmOrProceduralTargetMicroseconds / 1000.0:F3}ms, " +
            $"maxHostStep={smoke.MaximumHostStepMicroseconds / 1000.0:F3}ms/" +
            $"{SampleSceneTransitionLatencyPolicy.HitchTargetMicroseconds / 1000.0:F3}ms, " +
            $"coldCache={smoke.ColdWasResident}, " +
            $"warmCache={smoke.WarmWasResident}, " +
            $"workflow={(exactSponzaPath ? "Sponza->Bistro" : "Cornell->Bistro->Cornell->Bistro")}, " +
            $"sourceFallbacks={sourceFallbacks}, " +
            $"cornellRecovery={smoke.CornellRecoveryObserved}, " +
            $"validationErrors={validationErrors}.";

        smoke.Phase = BistroTransitionSmokePhase.Completed;
        _smokeRunner?.RecordOperation(
            "scene-transition",
            passed ? "passed" : "failed",
            Math.Max(0, _drawnFrames - 1),
            detail);
        if (!passed)
        {
            _runtimeSmokeFailure ??=
                "Bistro scene-transition smoke failed: " +
                detail;
            Console.Error.WriteLine(_runtimeSmokeFailure);
        }
        else
        {
            Console.WriteLine(
                "Bistro scene-transition smoke passed: " +
                detail);
        }

        Exit();
    }

    private void FailBistroTransitionSmoke(string detail)
    {
        BistroTransitionSmokeState? smoke =
            _bistroTransitionSmoke;
        if (smoke == null ||
            smoke.Phase == BistroTransitionSmokePhase.Completed)
        {
            return;
        }

        smoke.Phase = BistroTransitionSmokePhase.Completed;
        _runtimeSmokeFailure ??=
            "Bistro scene-transition smoke failed: " + detail;
        _smokeRunner?.RecordOperation(
            "scene-transition",
            "failed",
            Math.Max(0, _drawnFrames - 1),
            detail);
        Console.Error.WriteLine(_runtimeSmokeFailure);
        Exit();
    }

    private static bool HasBistroTransitionSmokeTimedOut(
        BistroTransitionSmokeState smoke,
        TimeSpan timeout) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(
            smoke.PhaseStartedTimestamp) >= timeout;

    private static long ElapsedMicroseconds(
        long startedTimestamp) => checked((long)Math.Round(
        System.Diagnostics.Stopwatch.GetElapsedTime(
            startedTimestamp).TotalMicroseconds));

    private void ScheduleHybridReflectionPipelinePreparation()
    {
        if (_hybridReflectionPreparationStarted)
            return;
        _hybridReflectionPreparationEligiblePresentSerial =
            _presentedFrameSerial >
            long.MaxValue - HybridReflectionPreparationStablePresentDelay
                ? long.MaxValue
                : _presentedFrameSerial +
                  HybridReflectionPreparationStablePresentDelay;
    }

    private void TryBeginHybridReflectionPipelinePreparation()
    {
        if (_hybridReflectionPreparationStarted ||
            _hybridReflectionPreparationEligiblePresentSerial ==
            long.MaxValue ||
            _presentedFrameSerial <
            _hybridReflectionPreparationEligiblePresentSerial ||
            _sceneKind != SampleSceneKind.Bistro ||
            _bistroTransitionSmoke is
                { Phase: not BistroTransitionSmokePhase.Completed } ||
            _sceneTransition?.IsActive == true ||
            _deferredSceneStreaming != null ||
            Renderer is not VulkanRenderer renderer)
        {
            return;
        }

        renderer.BeginHybridReflectionPipelinePreparation();
        _hybridReflectionPreparationStarted = true;
        _hybridReflectionPreparationEligiblePresentSerial = long.MaxValue;
        Console.WriteLine(
            "Bistro optional reflection pipeline preparation started " +
            "after the scene became stable.");
    }

    private void QueueSceneRetirement(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        long retireAfter = _presentedFrameSerial >
                           long.MaxValue - SceneRetirementPresentDelay
            ? long.MaxValue
            : _presentedFrameSerial + SceneRetirementPresentDelay;
        _deferredSceneRetirements.Enqueue(
            new DeferredSceneRetirement(scene, retireAfter));
    }

    private void ReportTransitionHitch(
        string stage,
        long startedTimestamp)
    {
        long elapsedMicroseconds = checked((long)Math.Round(
            System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp)
                .TotalMicroseconds));
        ObserveBistroTransitionHitch(elapsedMicroseconds);
        if (elapsedMicroseconds <=
            SampleSceneTransitionLatencyPolicy.HitchTargetMicroseconds)
        {
            return;
        }

        Console.WriteLine(
            $"Scene transition hitch: stage={stage}, " +
            $"elapsed={elapsedMicroseconds / 1000.0:F3}ms, " +
            $"target<={SampleSceneTransitionLatencyPolicy.HitchTargetMicroseconds / 1000.0:F3}ms.");
    }

    private void RetirePresentedScenes(bool forceAll = false)
    {
        while (_deferredSceneRetirements.TryPeek(
                   out DeferredSceneRetirement? retirement) &&
               (forceAll ||
                retirement.RetireAfterPresent <= _presentedFrameSerial))
        {
            _deferredSceneRetirements.Dequeue();
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                retirement.Scene.Dispose();
            }
            catch (Exception retirementFailure)
            {
                _runtimeSmokeFailure ??=
                    $"Deferred scene retirement failed for " +
                    $"'{retirement.Scene.Name}': " +
                    retirementFailure.Message;
                Console.Error.WriteLine(_runtimeSmokeFailure);
            }

            long elapsedMicroseconds = checked((long)Math.Round(
                System.Diagnostics.Stopwatch.GetElapsedTime(started)
                    .TotalMicroseconds));
            if (elapsedMicroseconds > 33_000)
            {
                Console.WriteLine(
                    $"Scene retirement hitch: " +
                    $"scene='{retirement.Scene.Name}', " +
                    $"elapsed={elapsedMicroseconds / 1000.0:F3}ms.");
            }
            ObserveBistroTransitionHitch(elapsedMicroseconds);
        }
    }

    private void ActivateLoadingScene(
        long generation,
        SampleSceneKind target,
        in SampleSceneTransitionMemoryDecision memory)
    {
        var loading = new Scene
        {
            Name = LoadingSceneName,
            AmbientLight = new Njulf.Core.Math.Color(
                0.01f,
                0.015f,
                0.025f,
                1f)
        };
        Scene previous = ExchangeScene(loading);
        try
        {
            _transitionPreviousScene = previous;
            _transitionLoadingScene = loading;
            _loadingTransitionGeneration = generation;
            _loadingFramePresented = false;
            _loadingSceneInstancesReleased = false;
            _loadingResidencyAssetsReleased = false;
            _inputController?.SetParticleEffects(
                Array.Empty<ParticleEffectInstance>());
            _inputController?.SetPerformanceScenarioRunner(null);
#if NJULF_EDITOR
            _editorController?.SetScene(loading);
#endif
        }
        catch
        {
            Scene rejectedLoading = ExchangeScene(previous);
            _transitionPreviousScene = null;
            _transitionLoadingScene = null;
            _loadingFramePresented = false;
            _loadingSceneInstancesReleased = false;
            _loadingResidencyAssetsReleased = false;
            _inputController?.SetParticleEffects(_sampleVfxEffects);
            _inputController?.SetPerformanceScenarioRunner(
                _performanceScenarioRunner);
            if (ReferenceEquals(rejectedLoading, loading))
                loading.Dispose();
            throw;
        }
        Console.WriteLine(
            $"Loading scene activated for '{target}': " +
            $"required={memory.RequiredBytes / (1024.0 * 1024.0):F1}MiB, " +
            $"ceiling={memory.AdmissionCeilingBytes / (1024.0 * 1024.0):F1}MiB.");
    }

    private void ReleasePreviousSceneForLoadingTransition(
        SampleSceneTransitionCoordinator coordinator)
    {
        Scene? previous = _transitionPreviousScene;
        if (previous == null)
        {
            _loadingSceneInstancesReleased = false;
            _loadingResidencyAssetsReleased = false;
            coordinator.ReleaseLoadingFrame(
                _loadingTransitionGeneration);
            return;
        }

        // Releasing the scene instances and their cached assets back-to-back can
        // double the handle-release work in one host frame. The loading scene
        // is already visible, so retire those ownership layers on consecutive
        // frames. Scene references must go first so no frame can observe live
        // instances whose cached GPU assets have already been released.
        if (!_loadingSceneInstancesReleased)
        {
            _sceneLoader = null;
            _sampleVfxEffects = Array.Empty<ParticleEffectInstance>();
            _performanceScenarioRunner = null;
            _inputController?.SetPerformanceScenarioRunner(null);
            previous.Dispose();
            Services?.GetRequiredService<LightManager>().ClearLights();
            _loadingSceneInstancesReleased = true;
            coordinator.ObserveHostActivity(
                _loadingTransitionGeneration);
            return;
        }

        if (!_loadingResidencyAssetsReleased)
        {
            bool released = _sceneResidency?.ReleaseActiveAssetsStep(
                TransitionReleaseRenderObjectsPerFrame) ?? true;
            coordinator.ObserveHostActivity(
                _loadingTransitionGeneration);
            if (!released)
                return;

            _loadingResidencyAssetsReleased = true;
            return;
        }

        (Content ?? throw new InvalidOperationException(
            "Content manager is unavailable during a loading-scene handoff."))
            .Clear();
        _sceneResidency?.ResetAfterContentClear();
        _transitionPreviousScene = null;
        _loadingFramePresented = false;
        _loadingSceneInstancesReleased = false;
        _loadingResidencyAssetsReleased = false;
        coordinator.ReleaseLoadingFrame(
            _loadingTransitionGeneration);
    }

    private void RestoreDeferredPreviousScene()
    {
        Scene? previous = _transitionPreviousScene;
        if (previous == null)
            return;

        Scene loading = ExchangeScene(previous);
        _transitionPreviousScene = null;
        _transitionLoadingScene = null;
        _loadingFramePresented = false;
        _loadingSceneInstancesReleased = false;
        _loadingResidencyAssetsReleased = false;
        loading.Dispose();
        _inputController?.SetParticleEffects(_sampleVfxEffects);
        _inputController?.SetPerformanceScenarioRunner(
            _performanceScenarioRunner);
#if NJULF_EDITOR
        _editorController?.SetScene(previous);
#endif
    }

    private void RecoverSafeSceneAfterTransitionFailure()
    {
        IServiceProvider services = Services ??
            throw new InvalidOperationException(
                "Services are unavailable during scene recovery.");
        try
        {
            CommitPreparedScene(
                SampleSceneKind.GlobalIlluminationTest,
                services.GetRequiredService<MeshManager>(),
                services.GetRequiredService<MaterialManager>(),
                services.GetRequiredService<LightManager>(),
                services.GetRequiredService<VulkanRenderer>(),
                Camera as FirstPersonCamera ??
                    throw new InvalidOperationException(
                        "Safe scene recovery requires a FirstPersonCamera."));
            Console.Error.WriteLine(
                "Recovered with the safe GI scene after a failed low-memory transition.");
        }
        catch (Exception recoveryFailure)
        {
            _runtimeSmokeFailure ??=
                "Safe scene recovery failed: " +
                recoveryFailure.Message;
            Console.Error.WriteLine(_runtimeSmokeFailure);
            Exit();
        }
    }

    private void HandleExitRequest()
    {
        if (_sceneTransition?.IsActive == true)
        {
            _sceneTransition.Cancel();
            return;
        }

        Exit();
    }

    private SampleSceneTransitionMemoryDecision
        EvaluateSceneTransitionMemory(
            VulkanRenderer renderer,
            ulong targetIncrementalBytes)
    {
        MemoryHeapBudgetSnapshot heap =
            renderer.CurrentMemoryHeapBudget;
        ulong budget = heap.IsAvailable &&
                       heap.PrimaryBudgetBytes > 0
            ? heap.PrimaryBudgetBytes
            : renderer.Settings.PerformanceBudgets.Profile
                .GpuMemoryBudgetBytes;
        return SampleSceneTransitionMemoryPolicy.Evaluate(
            ResolveCurrentGpuUsage(renderer),
            budget,
            targetIncrementalBytes);
    }

    private static ulong ResolveCurrentGpuUsage(
        VulkanRenderer renderer)
    {
        MemoryHeapBudgetSnapshot heap =
            renderer.CurrentMemoryHeapBudget;
        if (heap.IsAvailable && heap.PrimaryBudgetBytes > 0)
            return heap.PrimaryUsageBytes;
        return renderer.LastBudgetSnapshot.Memory
            .EffectiveMemoryBytes;
    }

    internal static ulong EstimateSceneResidencyBytes(
        SampleSceneKind kind) => kind switch
    {
        SampleSceneKind.Bistro =>
            3UL * 1024UL * 1024UL * 1024UL,
        SampleSceneKind.SponzaPlaza =>
            1536UL * 1024UL * 1024UL,
        SampleSceneKind.FoliageShowcase =>
            768UL * 1024UL * 1024UL,
        SampleSceneKind.MaterialShowcase =>
            512UL * 1024UL * 1024UL,
        SampleSceneKind.VfxShowcase =>
            256UL * 1024UL * 1024UL,
        _ => 128UL * 1024UL * 1024UL
    };

    internal static ulong EstimateTransitionAdmissionBytes(
        SampleSceneKind kind,
        SampleAssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.HasDeferredAssets
            ? EstimateAssetTierResidencyBytes(
                kind,
                manifest,
                SampleAssetLoadTier.Critical)
            : EstimateSceneResidencyBytes(kind);
    }

    private static ulong EstimateAssetTierResidencyBytes(
        SampleSceneKind kind,
        SampleAssetManifest manifest,
        SampleAssetLoadTier tier)
    {
        SampleAssetReference[] all = manifest.EnumerateAssets().ToArray();
        if (all.Length == 0)
            return 0;
        ulong totalWeight = 0;
        ulong tierWeight = 0;
        foreach (SampleAssetReference asset in all)
        {
            ulong weight = checked((ulong)Math.Max(
                1,
                EstimateAssetPreloadBytes(asset.Path)));
            totalWeight = totalWeight > ulong.MaxValue - weight
                ? ulong.MaxValue
                : totalWeight + weight;
            if (asset.LoadTier == tier)
            {
                tierWeight = tierWeight > ulong.MaxValue - weight
                    ? ulong.MaxValue
                    : tierWeight + weight;
            }
        }

        if (tierWeight == 0 || totalWeight == 0)
            return 0;
        decimal fraction = tierWeight / (decimal)totalWeight;
        return Math.Max(
            1,
            (ulong)Math.Ceiling(
                EstimateSceneResidencyBytes(kind) * fraction));
    }

    private static long EstimateAssetPreloadBytes(string relativePath)
    {
        const long unknownBytes = 64L * 1024L * 1024L;
        try
        {
            string path = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(
                    AppContext.BaseDirectory,
                    relativePath);
            if (!File.Exists(path))
                return unknownBytes;
            long sourceBytes = new FileInfo(path).Length;
            return Math.Clamp(
                checked(sourceBytes * 4),
                1L * 1024L * 1024L,
                TransitionPreloadInflightBytes);
        }
        catch (OverflowException)
        {
            return TransitionPreloadInflightBytes;
        }
    }

    private static void RestoreLights(
        LightManager lightManager,
        IReadOnlyList<LightRecord> lights)
    {
        lightManager.ClearLights();
        foreach (LightRecord record in lights)
        {
            lightManager.AddLightHandle(
                record.Light,
                record.Name,
                record.Id);
        }
    }

    private void UpdateSceneTransitionTitle(
        SampleSceneTransitionSnapshot snapshot)
    {
        if (Window == null || !snapshot.Active)
            return;
        Window.Title =
            $"{WindowTitle} - Loading " +
            $"{GetSceneDisplayName(snapshot.Target)} " +
            $"{snapshot.Progress:P0} ({snapshot.Phase})";
    }

    private void RestoreWindowTitle()
    {
        if (Window != null)
            Window.Title = WindowTitle;
    }

    private void ApplyLoadedSceneState(
        Scene targetScene,
        MeshManager meshManager,
        MaterialManager materialManager,
        LightManager lightManager,
        VulkanRenderer renderer,
        FirstPersonCamera camera,
        Model model)
    {
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
        PrintLoadedSceneSummary(targetScene, model);

        Console.WriteLine($"Scene: {GetSceneDisplayName(_sceneKind)}");
    }

    private Model LoadModelForEditorRuntimeMetadata(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        SampleSceneLoader? loader = _sceneLoader;
        if (loader != null)
            return loader.LoadModelForRuntimeMetadata(modelPath);

        return Content?.Load<Model>(modelPath) ??
            throw new InvalidOperationException(
                $"Content manager returned null for scene model '{modelPath}'.");
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
    }

    private void PrintLoadedSceneSummary(
        Scene scene,
        Model model)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_diagnosticsReporter == null)
            return;

        if (GetModelSceneManifest(_sceneKind) is { } assetManifest)
        {
            _diagnosticsReporter.PrintModelSummary(model, assetManifest);
            return;
        }

        _diagnosticsReporter.PrintProceduralSceneSummary(
            scene,
            GetSceneDisplayName(_sceneKind));
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

    private enum BistroTransitionSmokePhase
    {
        WaitingForInitialFrame,
        ColdBistro,
        ReturningToGi,
        WarmBistro,
        Settling,
        Completed
    }

    private sealed class BistroTransitionSmokeState
    {
        public BistroTransitionSmokePhase Phase { get; set; } =
            BistroTransitionSmokePhase.WaitingForInitialFrame;
        public long PhaseStartedTimestamp { get; set; }
        public long ColdStartedTimestamp { get; set; }
        public long WarmStartedTimestamp { get; set; }
        public long ColdFirstPresentMicroseconds { get; set; } = -1;
        public long ColdFullResidencyMicroseconds { get; set; } = -1;
        public long ReturnFirstPresentMicroseconds { get; set; } = -1;
        public long WarmFirstPresentMicroseconds { get; set; } = -1;
        public long MaximumHostStepMicroseconds { get; set; }
        public long SettleAfterPresentSerial { get; set; }
        public int ValidationErrorBaseline { get; set; }
        public int SourceFallbackBaseline { get; set; }
        public SampleSceneKind InitialScene { get; set; }
        public bool CornellRecoveryObserved { get; set; }
        public bool ColdWasResident { get; set; }
        public bool WarmWasResident { get; set; }
    }
}
