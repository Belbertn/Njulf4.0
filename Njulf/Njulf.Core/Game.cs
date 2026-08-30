using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Njulf.Core
{
    public abstract class Game : IDisposable
    {
        private IServiceProvider? _services;
        private IWindow? _window;
        private IInputContext? _inputContext;
        private IRenderer? _renderer;
        private IContentManager? _content;
        private IInputManager? _input;
        private ICamera? _camera;
        private Scene.Scene _scene = null!;
        private bool _isRunning = false;
        private bool _isShuttingDown = false;
        private bool _isUpdatingFrame = false;
        private bool _isRenderingFrame = false;
        private bool _exitRequestedAfterFrame = false;
        private bool _firstFrameLogged = false;
        private bool _scenePresentLatencyReported;
        private bool _fullQualityLatencyReported;
        private CancellationTokenSource? _pipelinePreparationCancellation;
        private Task? _pipelinePreparationTask;
        private bool _progressiveContentLoadPending;
        private bool _progressiveScenePreparationPending;
        private bool _contentLoaded;
        private long _runStartedTimestamp;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDE0052:Remove unread private members", Justification = "Used for initialization tracking")]
        private bool _isInitialized = false;

        public string Name { get; set; } = "Njulf Game";
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public string WindowTitle { get; set; } = "Njulf Game";
        public WindowBorder WindowBorderStyle { get; set; } = WindowBorder.Resizable;
        public bool VSync { get; set; } = true;
        public bool IsRunning => _isRunning;

        public IServiceProvider? Services => _services;
        public IWindow? Window => _window;
        public IRenderer? Renderer => _renderer;
        public IContentManager? Content => _content;
        public IInputManager? Input => _input;
        public ICamera? Camera => _camera;
        public Scene.Scene Scene => _scene;

        protected Game()
        {
            _scene = new Scene.Scene();
        }

        protected long RunElapsedMicroseconds => _runStartedTimestamp == 0
            ? 0
            : GetElapsedMicroseconds(_runStartedTimestamp);

        public void Run()
        {
            if (_isRunning) return;
            _isRunning = true;
            _isShuttingDown = false;
            _runStartedTimestamp = Stopwatch.GetTimestamp();

            try
            {
                _window = RunStartupStep("Game.CreateWindow", CreateWindow);
                HookWindowEvents(_window);
                _window.Run();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error: " + ex.Message);
                throw;
            }
            finally
            {
                Shutdown(disposeWindow: true);
                _isRunning = false;
            }
        }

        protected virtual void Initialize()
        {
            if (_window == null)
                throw new InvalidOperationException("The Silk.NET window must be created before services are initialized.");

            _inputContext = _window.CreateInput();

            var services = new ServiceCollection();
            services.AddSingleton(_window);
            services.AddSingleton(_inputContext);

            RunStartupStep("Game.ConfigureServices", () => ConfigureServices(services));

            _services = services.BuildServiceProvider();

            _renderer = _services.GetService<IRenderer>()!;
            _content = _services.GetService<IContentManager>()!;
            _input = _services.GetService<IInputManager>()!;
            _camera = _services.GetService<ICamera>() ?? CreateDefaultCamera();

            if (_renderer != null)
            {
                RunStartupStep(
                    "Game.ConfigureRendererBeforeInitialize",
                    () => ConfigureRendererBeforeInitialize(_renderer));
                RunStartupStep("VulkanRenderer.Initialize", _renderer.Initialize);
            }
        }

        protected virtual void ConfigureServices(IServiceCollection services)
        {
        }

        protected virtual ICamera CreateDefaultCamera()
        {
            return new FirstPersonCamera(new Vector3(0, 0, 5));
        }

        /// <summary>
        /// Gives the application a final opportunity to establish renderer
        /// settings after device-backed services exist but before immutable
        /// render targets, graph resources, and pipelines are created.
        /// </summary>
        protected virtual void ConfigureRendererBeforeInitialize(
            IRenderer renderer)
        {
        }

        protected virtual void Load()
        {
        }

        protected virtual void Update(float deltaTime)
        {
            if (!_isRunning)
                return;

            _scene.Update(deltaTime);
        }

        protected virtual void Draw()
        {
        }

        /// <summary>Called after a frame has been submitted and presented.</summary>
        protected virtual void OnFramePresented()
        {
        }

        /// <summary>
        /// Called once after the first successful present, including the
        /// pipeline-free progressive bootstrap frame. Automation can use this
        /// milestone without forcing initial content or fallback resources to
        /// load first.
        /// </summary>
        protected virtual void OnBootstrapFramePresented()
        {
        }

        /// <summary>
        /// Atomically replaces the scene observed by subsequent update and
        /// render callbacks. The caller retains ownership of the previous
        /// scene and decides when it is safe to dispose it.
        /// </summary>
        protected Scene.Scene ExchangeScene(Scene.Scene nextScene)
        {
            ArgumentNullException.ThrowIfNull(nextScene);
            Scene.Scene previous = _scene;
            _scene = nextScene;
            return previous;
        }

        protected virtual void Unload()
        {
            _renderer?.Dispose();
            _scene?.Dispose();
        }

        protected virtual void OnStartupStepStarted(string name)
        {
        }

        protected virtual void OnStartupStepSucceeded(string name, long elapsedMicroseconds)
        {
        }

        protected virtual void OnStartupStepFailed(string name, Exception exception, long elapsedMicroseconds)
        {
        }

        protected virtual void OnResize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            WindowWidth = width;
            WindowHeight = height;
            _renderer?.Resize(width, height);
            if (_camera != null)
            {
                _camera.AspectRatio = (float)width / height;
            }
        }

        public void Exit()
        {
            _isRunning = false;
            if (_isUpdatingFrame || _isRenderingFrame)
            {
                _exitRequestedAfterFrame = true;
                return;
            }

            _window?.Close();
        }

        public void Dispose()
        {
            Exit();

            // Exit() defers the window close while an update or render callback is
            // active. Keep disposal deferred as well so the callback cannot resume
            // against a scene that has already been torn down.
            if (_isUpdatingFrame || _isRenderingFrame)
            {
                GC.SuppressFinalize(this);
                return;
            }

            Shutdown(disposeWindow: true);
            GC.SuppressFinalize(this);
        }

        private IWindow CreateWindow()
        {
            if (WindowWidth <= 0)
                throw new InvalidOperationException("WindowWidth must be greater than zero.");
            if (WindowHeight <= 0)
                throw new InvalidOperationException("WindowHeight must be greater than zero.");

            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>(WindowWidth, WindowHeight);
            options.Title = WindowTitle;
            options.WindowBorder = WindowBorderStyle;
            options.VSync = VSync;

            return Silk.NET.Windowing.Window.Create(options);
        }

        private void HookWindowEvents(IWindow window)
        {
            window.Load += OnWindowLoad;
            window.Update += OnWindowUpdate;
            window.Render += OnWindowRender;
            window.FramebufferResize += OnWindowFramebufferResize;
            window.Closing += OnWindowClosing;
        }

        private void OnWindowLoad()
        {
            Initialize();
            _isInitialized = true;
            if (_renderer is IProgressiveScenePipelinePreparer
                { IsProgressiveStartupEnabled: true } progressivePreparer)
            {
                RunStartupStep(
                    "Renderer.BeginProductionPreparation",
                    progressivePreparer.BeginProductionPreparation);
                _progressiveContentLoadPending = true;
                return;
            }

            LoadInitialContentAndPreparePipelines();
        }

        private void LoadInitialContentAndPreparePipelines()
        {
            RunStartupStep("Content.LoadInitialScene", Load);
            _contentLoaded = true;
            if (_renderer is IProgressiveScenePipelinePreparer
                    { IsProgressiveStartupEnabled: true }
                    &&
                _camera != null)
            {
                _progressiveScenePreparationPending = true;
                BeginProgressiveScenePreparation(_renderer);
            }
            else if (_renderer is IScenePipelinePreparer pipelinePreparer &&
                _camera != null)
            {
                RunStartupStep(
                    "Renderer.PrepareInitialScene",
                    () => pipelinePreparer.PrepareScene(_scene, _camera));
            }
        }

        private void OnWindowUpdate(double deltaSeconds)
        {
            if (!_isRunning)
                return;

            ObservePipelinePreparation();
            if (_progressiveContentLoadPending && _firstFrameLogged)
            {
                _progressiveContentLoadPending = false;
                LoadInitialContentAndPreparePipelines();
            }

            _isUpdatingFrame = true;
            try
            {
                _input?.Update();
                if (_isRunning && _contentLoaded)
                    Update((float)deltaSeconds);
            }
            finally
            {
                _isUpdatingFrame = false;

                if (_exitRequestedAfterFrame)
                {
                    _exitRequestedAfterFrame = false;
                    _window?.Close();
                }
            }
        }

        private void OnWindowRender(double deltaSeconds)
        {
            if (!_isRunning || _renderer == null)
                return;

            ObservePipelinePreparation();

            IRenderer renderer = _renderer;
            long frameStarted = Stopwatch.GetTimestamp();
            if (renderer.BeginFrame() != true)
                return;
            long beginFrameMicroseconds = GetElapsedMicroseconds(frameStarted);

            _isRenderingFrame = true;
            try
            {
                long drawStarted = Stopwatch.GetTimestamp();
                try
                {
                    if (!_firstFrameLogged)
                        RunStartupStep("FirstFrame.Begin", () => { });
                    if (_contentLoaded)
                        Draw();
                    else
                        renderer.Clear(Color.Black);
                }
                catch (Exception drawFailure)
                {
                    // Vulkan submission/recording faults abandon their frame
                    // before rethrowing. Do not replace that useful exception
                    // with the secondary "EndFrame without BeginFrame" error.
                    // Renderers that still own a frame retain the historical
                    // EndFrame cleanup attempt, but its failure is attached to
                    // the original exception instead of masking it.
                    if (renderer is not IRendererFrameState
                        {
                            IsFrameInProgress: false
                        })
                    {
                        try
                        {
                            renderer.EndFrame();
                        }
                        catch (Exception cleanupFailure)
                        {
                            drawFailure.Data[
                                "Njulf.RenderFrameCleanupFailure"] =
                                cleanupFailure;
                        }
                    }

                    ExceptionDispatchInfo.Capture(drawFailure).Throw();
                }
                long drawMicroseconds = GetElapsedMicroseconds(drawStarted);

                long endFrameStarted = Stopwatch.GetTimestamp();
                renderer.EndFrame();
                long endFrameMicroseconds =
                    GetElapsedMicroseconds(endFrameStarted);
                if (_contentLoaded)
                    OnFramePresented();
                long frameMicroseconds =
                    GetElapsedMicroseconds(frameStarted);
                if (_firstFrameLogged && frameMicroseconds > 100_000)
                {
                    Console.WriteLine(
                        $"Render frame hitch: total={frameMicroseconds / 1000.0:F3}ms, " +
                        $"begin={beginFrameMicroseconds / 1000.0:F3}ms, " +
                        $"draw={drawMicroseconds / 1000.0:F3}ms, " +
                        $"end={endFrameMicroseconds / 1000.0:F3}ms, " +
                        $"presentedCallback={(frameMicroseconds - beginFrameMicroseconds - drawMicroseconds - endFrameMicroseconds) / 1000.0:F3}ms.");
                }
                if (!_firstFrameLogged)
                {
                    long firstPresentElapsedMicroseconds = checked((long)System.Math.Round(
                        (Stopwatch.GetTimestamp() - _runStartedTimestamp) *
                        1_000_000.0 / Stopwatch.Frequency));
                    RunStartupStep("FirstFrame.End", () => { });
                    _firstFrameLogged = true;
                    if (renderer is IStartupLatencyReporter latencyReporter)
                    {
                        RunStartupStep(
                            "StartupLatency.Evaluate",
                            () => latencyReporter.ReportFirstPresent(
                                firstPresentElapsedMicroseconds));
                    }
                    OnBootstrapFramePresented();
                }
                ReportProgressiveStartupMilestones(renderer);
                if (_isRunning)
                    BeginProgressiveScenePreparation(renderer);
            }
            finally
            {
                // Draw, EndFrame, present, and deferred validation can all
                // fail. Always restore the lifecycle guard before the
                // exception unwinds into the window backend.
                _isRenderingFrame = false;

                if (_exitRequestedAfterFrame)
                {
                    _exitRequestedAfterFrame = false;
                    _window?.Close();
                }
            }
        }

        private static long GetElapsedMicroseconds(long startedTimestamp) =>
            checked((long)System.Math.Round(
                Stopwatch.GetElapsedTime(startedTimestamp)
                    .TotalMicroseconds));

        private void ReportProgressiveStartupMilestones(IRenderer renderer)
        {
            if (renderer is not IProgressiveScenePipelinePreparer progressive ||
                renderer is not IStartupMilestoneLatencyReporter reporter)
            {
                return;
            }

            RendererStartupSnapshot snapshot = progressive.StartupSnapshot;
            long elapsed = GetElapsedMicroseconds(_runStartedTimestamp);
            if (snapshot.ScenePresented &&
                !_scenePresentLatencyReported)
            {
                _scenePresentLatencyReported = true;
                reporter.ReportStartupMilestone(
                    RendererStartupMilestone.ScenePresent,
                    elapsed);
            }
            if (snapshot.FullQualityPresented &&
                !_fullQualityLatencyReported)
            {
                _fullQualityLatencyReported = true;
                reporter.ReportStartupMilestone(
                    RendererStartupMilestone.FullQualityPresent,
                    elapsed);
            }
        }

        private void OnWindowFramebufferResize(Vector2D<int> size)
        {
            OnResize(size.X, size.Y);
        }

        private void OnWindowClosing()
        {
            _isRunning = false;
            Shutdown(disposeWindow: false);
        }

        private void Shutdown(bool disposeWindow)
        {
            // Stop callbacks before tearing down services owned by Update/Draw. Some window
            // backends can dispatch one last callback while Run() or window disposal unwinds.
            _isRunning = false;

            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            try
            {
                _pipelinePreparationCancellation?.Cancel();
                if (_isInitialized)
                    Unload();
            }
            finally
            {
                _pipelinePreparationTask = null;
                _pipelinePreparationCancellation?.Dispose();
                _pipelinePreparationCancellation = null;
                _progressiveContentLoadPending = false;
                _progressiveScenePreparationPending = false;
                _contentLoaded = false;
                _isInitialized = false;
                if (_services is IDisposable disposableServices)
                    disposableServices.Dispose();
                _services = null;

                _inputContext?.Dispose();
                _inputContext = null;

                _renderer = null;
                _content = null;
                _input = null;
                _camera = null;

                if (disposeWindow)
                {
                    _window?.Dispose();
                    _window = null;
                }

                _isShuttingDown = false;
            }
        }

        private void ObservePipelinePreparation()
        {
            Task? task = _pipelinePreparationTask;
            if (task == null || !task.IsCompleted)
                return;

            _pipelinePreparationTask = null;
            if (task.IsCanceled &&
                _pipelinePreparationCancellation?.IsCancellationRequested == true)
            {
                return;
            }
            if (task.Exception == null)
                return;

            Exception failure = task.Exception.InnerExceptions.Count == 1
                ? task.Exception.InnerException!
                : task.Exception;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private void BeginProgressiveScenePreparation(IRenderer renderer)
        {
            if (!_progressiveScenePreparationPending ||
                renderer is not IProgressiveScenePipelinePreparer
                {
                    IsProgressiveStartupEnabled: true
                } progressivePreparer ||
                _camera == null)
            {
                return;
            }

            _progressiveScenePreparationPending = false;
            _pipelinePreparationCancellation =
                new CancellationTokenSource();
            _pipelinePreparationTask =
                progressivePreparer.PrepareSceneAsync(
                    _scene,
                    _camera,
                    _pipelinePreparationCancellation.Token);
        }

        private void RunStartupStep(string name, Action action)
        {
            RunStartupStep<object?>(
                name,
                () =>
                {
                    action();
                    return null;
                });
        }

        private T RunStartupStep<T>(string name, Func<T> action)
        {
            var stopwatch = Stopwatch.StartNew();
            OnStartupStepStarted(name);
            try
            {
                T result = action();
                stopwatch.Stop();
                OnStartupStepSucceeded(name, stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                OnStartupStepFailed(name, ex, stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
                throw;
            }
        }
    }
}
