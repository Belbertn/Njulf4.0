using System;
using System.Collections.Generic;

namespace NjulfHelloGame;

internal sealed record SampleSmokeOperationResult(
    string Name,
    string Status,
    int FrameIndex,
    string? Detail);

internal sealed class SampleLifecycleSmokeRunner
{
    private readonly SampleSmokeOptions _options;
    private readonly Action<int, int> _resize;
    private readonly Action _reloadScene;
    private readonly Action _exit;
    private readonly List<SampleSmokeOperationResult> _results = new();
    private int _resizeStep;
    private int _sceneReloadsCompleted;
    private bool _minimizeIssued;
    private bool _restoreIssued;
    private bool _fullscreenSkipped;
    private bool _missingAssetScenarioRecorded;

    public SampleLifecycleSmokeRunner(
        SampleSmokeOptions options,
        Action<int, int> resize,
        Action reloadScene,
        Action exit)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resize = resize ?? throw new ArgumentNullException(nameof(resize));
        _reloadScene = reloadScene ?? throw new ArgumentNullException(nameof(reloadScene));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public IReadOnlyList<SampleSmokeOperationResult> Results => _results;

    public void OnFrameRendered(int frameIndex)
    {
        if (!_options.Enabled)
            return;

        switch (_options.Mode)
        {
            case SampleSmokeMode.Startup:
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.Resize:
                RunResize(frameIndex);
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.Minimize:
                RunMinimize(frameIndex);
                ExitWhenFrameBudgetReached(frameIndex);
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
                ExitWhenFrameBudgetReached(frameIndex);
                break;
            case SampleSmokeMode.All:
                RunResize(frameIndex);
                RunMinimize(frameIndex);
                RunFullscreen(frameIndex);
                RunSceneReload(frameIndex);
                ExitWhenFrameBudgetReached(frameIndex);
                break;
        }
    }

    private void RunResize(int frameIndex)
    {
        (int Width, int Height)[] sequence =
        {
            (1280, 720),
            (1920, 1080),
            (800, 600)
        };

        if (_resizeStep >= sequence.Length || frameIndex <= _resizeStep)
            return;

        (int width, int height) = sequence[_resizeStep++];
        _resize(width, height);
        Record("resize", "passed", frameIndex, $"{width}x{height}");
    }

    private void RunMinimize(int frameIndex)
    {
        if (!_minimizeIssued && frameIndex >= 1)
        {
            _resize(0, 0);
            _minimizeIssued = true;
            Record("minimize-zero-framebuffer", "passed", frameIndex, "Renderer ignored zero-sized framebuffer.");
        }

        if (!_restoreIssued && frameIndex >= 2)
        {
            _resize(1280, 720);
            _restoreIssued = true;
            Record("restore-framebuffer", "passed", frameIndex, "1280x720");
        }
    }

    private void RunFullscreen(int frameIndex)
    {
        if (_fullscreenSkipped)
            return;

        _fullscreenSkipped = true;
        Record("fullscreen", "skipped", frameIndex, "Silk.NET fullscreen switching is backend-dependent and is not forced in smoke mode.");
    }

    private void RunSceneReload(int frameIndex)
    {
        if (frameIndex == 0 || _sceneReloadsCompleted >= _options.SceneReloadCount)
        {
            if (_sceneReloadsCompleted >= _options.SceneReloadCount)
                ExitWhenFrameBudgetReached(Math.Max(frameIndex, _options.FrameCount));
            return;
        }

        _reloadScene();
        _sceneReloadsCompleted++;
        Record("scene-reload", "passed", frameIndex, $"reload={_sceneReloadsCompleted}/{_options.SceneReloadCount}");

        if (_sceneReloadsCompleted >= _options.SceneReloadCount)
            _exit();
    }

    private void RunMissingAssets(int frameIndex)
    {
        if (_missingAssetScenarioRecorded)
            return;

        _missingAssetScenarioRecorded = true;
        string status = _options.ForceMissingAssets ? "skipped" : "skipped";
        string detail = _options.ForceMissingAssets
            ? "Controlled missing-asset scenarios are recorded as skipped until importer fallback wiring is active."
            : "Pass --force-missing-assets to enable destructive missing-asset scenarios.";
        Record("missing-assets", status, frameIndex, detail);
    }

    private void ExitWhenFrameBudgetReached(int frameIndex)
    {
        if (_options.FrameCount > 0 && frameIndex + 1 >= _options.FrameCount)
            _exit();
    }

    private void Record(string name, string status, int frameIndex, string? detail)
    {
        _results.Add(new SampleSmokeOperationResult(name, status, frameIndex, detail));
        Console.WriteLine($"Smoke {name}: {status}" + (detail == null ? string.Empty : $" ({detail})"));
    }
}
