using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Assets;

namespace NjulfHelloGame;

public sealed record SampleSmokeOperationResult(
    string Name,
    string Status,
    int FrameIndex,
    string? Detail);

public sealed class SampleLifecycleSmokeRunner
{
    private readonly SampleSmokeOptions _options;
    private readonly Action<int, int> _resize;
    private readonly Action _reloadScene;
    private readonly Action _exit;
    private readonly Func<IReadOnlyList<SampleMissingAssetScenario>, string?> _runMissingAssetScenario;
    private readonly Func<TimeSpan> _elapsed;
    private readonly List<SampleSmokeOperationResult> _results = new();
    private int _resizeStep;
    private int _sceneReloadsCompleted;
    private int _sceneReloadIssuedFrame = -1;
    private int _restoreNotBeforeFrame = int.MaxValue;
    private readonly int _initialWindowWidth;
    private readonly int _initialWindowHeight;
    private int _lastPositiveWindowWidth;
    private int _lastPositiveWindowHeight;
    private bool _awaitingSceneReloadFrame;
    private bool _minimizeIssued;
    private bool _restoreIssued;
    private bool _fullscreenSkipped;
    private bool _missingAssetScenarioRecorded;
    private bool _exitRequested;
    private PendingFramebufferMutation? _pendingFramebufferMutation;

    public SampleLifecycleSmokeRunner(
        SampleSmokeOptions options,
        Action<int, int> resize,
        Action reloadScene,
        Action exit,
        Func<IReadOnlyList<SampleMissingAssetScenario>, string?>? runMissingAssetScenario = null,
        Func<TimeSpan>? elapsed = null,
        Func<(int Width, int Height)>? initialWindowSize = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resize = resize ?? throw new ArgumentNullException(nameof(resize));
        _reloadScene = reloadScene ?? throw new ArgumentNullException(nameof(reloadScene));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _runMissingAssetScenario = runMissingAssetScenario ?? RunDefaultMissingAssetScenario;
        if (elapsed != null)
        {
            _elapsed = elapsed;
        }
        else
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _elapsed = () => stopwatch.Elapsed;
        }

        (int Width, int Height) initialSize =
            initialWindowSize?.Invoke() ?? (1280, 720);
        if (initialSize.Width <= 0 || initialSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialWindowSize),
                "The initial smoke window size must be positive.");
        }
        _initialWindowWidth = initialSize.Width;
        _initialWindowHeight = initialSize.Height;
        _lastPositiveWindowWidth = initialSize.Width;
        _lastPositiveWindowHeight = initialSize.Height;
    }

    public IReadOnlyList<SampleSmokeOperationResult> Results => _results;

    public void OnFrameRendered(int frameIndex)
    {
        if (!_options.Enabled || _exitRequested)
            return;

        switch (_options.Mode)
        {
            case SampleSmokeMode.Startup:
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.Resize:
                RunResize(frameIndex);
                ExitWhenFrameBudgetReached(
                    frameIndex,
                    _resizeStep >= 3 && _pendingFramebufferMutation is null);
                break;
            case SampleSmokeMode.Minimize:
                RunMinimize(frameIndex);
                ExitWhenFrameBudgetReached(
                    frameIndex,
                    _minimizeIssued &&
                    _restoreIssued &&
                    _pendingFramebufferMutation is null);
                break;
            case SampleSmokeMode.Fullscreen:
                RunFullscreen(frameIndex);
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.SceneReload:
                RunSceneReload(frameIndex);
                break;
            case SampleSmokeMode.MissingAssets:
                RunMissingAssets(frameIndex);
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.LongRun:
                RunLongRun(frameIndex);
                break;
            case SampleSmokeMode.QualitySwitch:
            case SampleSmokeMode.DdgiResidencySwitch:
            case SampleSmokeMode.TextureHotReload:
                // These bounded state machines own completion, timeout, and
                // exit. Their frame requirements include asynchronous GPU
                // readback latency and cannot be represented by the generic
                // lifecycle frame budget.
                break;
            case SampleSmokeMode.All:
                RunAll(frameIndex);
                break;
        }
    }

    /// <summary>
    /// Advances lifecycle mutations from the window update callback. Unlike a
    /// rendered-frame callback, this continues to run while a minimized
    /// zero-sized framebuffer correctly suppresses BeginFrame.
    /// </summary>
    public void OnUpdate(int nextRenderedFrameIndex)
    {
        if (!_options.Enabled || _exitRequested || nextRenderedFrameIndex < 0)
            return;
        if (_options.Mode is not (SampleSmokeMode.Minimize or SampleSmokeMode.All))
            return;
        if (!_minimizeIssued ||
            _restoreIssued ||
            _pendingFramebufferMutation is not null ||
            nextRenderedFrameIndex < _restoreNotBeforeFrame)
        {
            return;
        }

        RequestFramebufferMutation(
            FramebufferMutationKind.Restore,
            _lastPositiveWindowWidth,
            _lastPositiveWindowHeight,
            nextRenderedFrameIndex);
    }

    public void OnFramebufferMutationObserved(
        bool succeeded,
        string detail)
    {
        if (_pendingFramebufferMutation is not { } mutation)
        {
            Record(
                "framebuffer-mutation-observation",
                "failed",
                Math.Max(0, _sceneReloadIssuedFrame),
                "The host reported a framebuffer mutation with no pending request.");
            return;
        }

        _pendingFramebufferMutation = null;
        switch (mutation.Kind)
        {
            case FramebufferMutationKind.Resize:
                _resizeStep++;
                if (succeeded)
                {
                    _lastPositiveWindowWidth = mutation.Width;
                    _lastPositiveWindowHeight = mutation.Height;
                }
                break;
            case FramebufferMutationKind.Minimize:
                _minimizeIssued = true;
                break;
            case FramebufferMutationKind.Restore:
                _restoreIssued = true;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        Record(
            mutation.OperationName,
            succeeded ? "passed" : "failed",
            mutation.RequestFrameIndex,
            string.IsNullOrWhiteSpace(detail)
                ? $"requested={mutation.Width}x{mutation.Height}"
                : detail);
    }

    public void RecordOperation(
        string name,
        string status,
        int frameIndex,
        string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Smoke operation name cannot be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Smoke operation status cannot be empty.", nameof(status));
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        Record(name, status, frameIndex, detail);
    }

    private void RunLongRun(int frameIndex)
    {
        if (_options.LongRunMinutes > 0.0 &&
            _elapsed().TotalMinutes >= _options.LongRunMinutes)
        {
            Record(
                "long-run-duration",
                "passed",
                frameIndex,
                $"elapsedMinutes={_elapsed().TotalMinutes:F3}, requestedMinutes={_options.LongRunMinutes:F3}");
            RequestExit();
            return;
        }

        ExitWhenFrameBudgetReached(frameIndex);
    }

    private void RunResize(int frameIndex)
    {
        (int Width, int Height)[] sequence =
        {
            (Math.Min(1280, _initialWindowWidth),
                Math.Min(720, _initialWindowHeight)),
            // Requesting a client area larger than the desktop work area is
            // backend-defined and Windows clamps it below the taskbar. The
            // known-good initial client size still exercises a second
            // swapchain rebuild without turning host window policy into a
            // renderer failure.
            (_initialWindowWidth, _initialWindowHeight),
            (Math.Min(800, _initialWindowWidth),
                Math.Min(600, _initialWindowHeight))
        };

        if (_resizeStep >= sequence.Length ||
            _pendingFramebufferMutation is not null ||
            frameIndex <= _resizeStep)
            return;

        (int width, int height) = sequence[_resizeStep];
        RequestFramebufferMutation(
            FramebufferMutationKind.Resize,
            width,
            height,
            frameIndex);
    }

    private void RunMinimize(
        int frameIndex,
        int minimizeFrame = 1,
        int restoreFrame = 2)
    {
        if (!_minimizeIssued &&
            _pendingFramebufferMutation is null &&
            frameIndex >= minimizeFrame)
        {
            _restoreNotBeforeFrame = restoreFrame;
            RequestFramebufferMutation(
                FramebufferMutationKind.Minimize,
                0,
                0,
                frameIndex);
        }
    }

    private void RunFullscreen(int frameIndex)
    {
        if (_fullscreenSkipped)
            return;

        _fullscreenSkipped = true;
        Record("fullscreen", "skipped", frameIndex, "Silk.NET fullscreen switching is backend-dependent and is not forced in smoke mode.");
    }

    private void RunAll(int frameIndex)
    {
        RunFullscreen(frameIndex);

        // Program.ResizeForSmoke owns one pending resize applied by the next
        // update. Issue at most one window mutation per rendered frame so no
        // requested size can be overwritten before it reaches the renderer.
        if (_resizeStep < 3)
            RunResize(frameIndex);
        else
            RunMinimize(frameIndex, minimizeFrame: 4, restoreFrame: 5);

        bool framebufferMutationsComplete =
            _resizeStep >= 3 &&
            _minimizeIssued &&
            _restoreIssued &&
            _pendingFramebufferMutation is null;

        // Frame six is the first completed frame after the restore request from
        // frame five. Start reloads only after that lifecycle sequence has been
        // observed; every reload still requires its own post-reload frame.
        if (frameIndex >= 6 && framebufferMutationsComplete)
            RunSceneReload(frameIndex, exitWhenComplete: false);

        bool reloadComplete =
            _options.SceneReloadCount <= 0 ||
            (_sceneReloadsCompleted >= _options.SceneReloadCount &&
             !_awaitingSceneReloadFrame);
        bool mutationsComplete =
            framebufferMutationsComplete &&
            _fullscreenSkipped &&
            reloadComplete;

        int minimumCompletionFrame = Math.Max(6, _options.FrameCount - 1);
        if (mutationsComplete && frameIndex >= minimumCompletionFrame)
            RequestExit();
    }

    private void RunSceneReload(int frameIndex, bool exitWhenComplete = true)
    {
        if (_options.SceneReloadCount <= 0)
        {
            if (exitWhenComplete)
                ExitWhenFrameBudgetReached(frameIndex);
            return;
        }

        if (_awaitingSceneReloadFrame)
        {
            if (frameIndex <= _sceneReloadIssuedFrame)
                return;

            _awaitingSceneReloadFrame = false;
            _sceneReloadsCompleted++;
            Record(
                "scene-reload",
                "passed",
                frameIndex,
                $"reload={_sceneReloadsCompleted}/{_options.SceneReloadCount}, postReloadFrameObserved=true");
            if (exitWhenComplete &&
                _sceneReloadsCompleted >= _options.SceneReloadCount)
                RequestExit();
            return;
        }

        if (frameIndex == 0 || _sceneReloadsCompleted >= _options.SceneReloadCount)
            return;

        try
        {
            _reloadScene();
        }
        catch (Exception ex)
        {
            Record(
                "scene-reload",
                "failed",
                frameIndex,
                $"Reloading the scene failed with {ex.GetType().Name}: {ex.Message}");
            RequestExit();
            return;
        }
        _sceneReloadIssuedFrame = frameIndex;
        _awaitingSceneReloadFrame = true;
    }

    private void RunMissingAssets(int frameIndex)
    {
        if (_missingAssetScenarioRecorded)
            return;

        _missingAssetScenarioRecorded = true;
        if (!_options.ForceMissingAssets)
        {
            Record("missing-assets", "skipped", frameIndex, "Pass --force-missing-assets to enable controlled missing-asset validation.");
            return;
        }

        var scenarios = new[]
        {
            new SampleMissingAssetScenario("required-model", "model", "missing-required-model.gltf", Required: true)
        };

        string? failure;
        try
        {
            failure = _runMissingAssetScenario(scenarios);
        }
        catch (Exception ex)
        {
            failure =
                $"The controlled missing-asset scenario threw {ex.GetType().Name}: {ex.Message}";
        }
        Record(
            "missing-assets",
            failure == null ? "passed" : "failed",
            frameIndex,
            failure ?? "Required missing model path produced a controlled FileNotFoundException.");
    }

    private void RequestFramebufferMutation(
        FramebufferMutationKind kind,
        int width,
        int height,
        int frameIndex)
    {
        if (_pendingFramebufferMutation is not null)
            return;

        var mutation = new PendingFramebufferMutation(
            kind,
            width,
            height,
            frameIndex);
        _pendingFramebufferMutation = mutation;
        try
        {
            _resize(width, height);
        }
        catch (Exception ex)
        {
            OnFramebufferMutationObserved(
                succeeded: false,
                $"Window mutation request {width}x{height} failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ExitWhenFrameBudgetReached(
        int frameIndex,
        bool operationComplete = true)
    {
        if (operationComplete &&
            _options.FrameCount > 0 &&
            frameIndex + 1 >= _options.FrameCount)
        {
            RequestExit();
        }
    }

    private void RequestExit()
    {
        if (_exitRequested)
            return;

        _exitRequested = true;
        _exit();
    }

    private void Record(string name, string status, int frameIndex, string? detail)
    {
        _results.Add(new SampleSmokeOperationResult(name, status, frameIndex, detail));
        Console.WriteLine($"Smoke {name}: {status}" + (detail == null ? string.Empty : $" ({detail})"));
    }

    private static string? RunDefaultMissingAssetScenario(IReadOnlyList<SampleMissingAssetScenario> scenarios)
    {
        string root = Path.Combine(Path.GetTempPath(), "NjulfMissingAssetSmoke");
        Directory.CreateDirectory(root);

        using var content = new ContentManager(root);
        foreach (SampleMissingAssetScenario scenario in scenarios)
        {
            if (!scenario.Required)
                continue;

            try
            {
                _ = content.Load<ModelMesh>(scenario.AssetPath);
                return $"Required missing {scenario.AssetKind} '{scenario.AssetPath}' loaded unexpectedly.";
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception ex)
            {
                return $"Required missing {scenario.AssetKind} '{scenario.AssetPath}' failed with {ex.GetType().Name} instead of FileNotFoundException.";
            }
        }

        return null;
    }

    private enum FramebufferMutationKind
    {
        Resize,
        Minimize,
        Restore
    }

    private sealed record PendingFramebufferMutation(
        FramebufferMutationKind Kind,
        int Width,
        int Height,
        int RequestFrameIndex)
    {
        public string OperationName => Kind switch
        {
            FramebufferMutationKind.Resize => "resize",
            FramebufferMutationKind.Minimize => "minimize-zero-framebuffer",
            FramebufferMutationKind.Restore => "restore-framebuffer",
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
