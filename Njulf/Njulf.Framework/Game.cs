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
using Njulf.Graphics;
using Njulf.Rendering;
using Njulf.Assets;
using Njulf.Input;

namespace Njulf.Core
{
    /// <summary>Default application host. Owns the window, services and current scene; callbacks run on the game/device thread.</summary>
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
        private long _nextStartupHeartbeatMicroseconds = 2_000_000L;
        private bool _startupTitleActive;
        private readonly FramePacer _framePacer = new();
        private double _maximumFramesPerSecond =
            FramePacer.DefaultMaximumFramesPerSecond;
        private bool _runStarted, _disposed, _userInitializationStarted;
        private ExceptionDispatchInfo? _callbackFailure;
        private readonly GameClock _gameClock = new();
        private GameSynchronizationContext? _hostContext;
        private readonly CancellationTokenSource _contentCancellation = new();
        private Task? _initialContentTask;
        private IContentUploadPump? _contentUploadPump;

        /// <summary>Maximum CPU time spent pumping uploads per host iteration.</summary>
        private TimeSpan _contentUploadCpuBudget = TimeSpan.FromMilliseconds(2);
        private int _contentUploadMaximumCallbacks = 1;
        private long _contentUploadMaximumSubmissionBytes = 8L * 1024 * 1024;
        /// <summary>CPU upload budget per host iteration; defaults to 2 milliseconds. Zero is allowed.</summary>
        public TimeSpan ContentUploadCpuBudget
        {
            get => _contentUploadCpuBudget;
            set { ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero); _contentUploadCpuBudget = value; }
        }
        /// <summary>Maximum upload callbacks per iteration; defaults to one and must be positive.</summary>
        public int ContentUploadMaximumCallbacks
        {
            get => _contentUploadMaximumCallbacks;
            set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _contentUploadMaximumCallbacks = value; }
        }
        /// <summary>Maximum upload submission size in bytes; defaults to 8 MiB and must be positive.</summary>
        public long ContentUploadMaximumSubmissionBytes
        {
            get => _contentUploadMaximumSubmissionBytes;
            set { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); _contentUploadMaximumSubmissionBytes = value; }
        }

        /// <summary>Observes the host upload pump on the device thread, including during startup.</summary>
        protected virtual void OnContentUploadsProcessed(ContentUploadPumpResult result) { }

        /// <summary>Application name; defaults to Njulf Game.</summary>
        public string Name { get; set; } = "Njulf Game";
        /// <summary>Initial client width in pixels; defaults to 1280. Updated after resize.</summary>
        public int WindowWidth { get; set; } = 1280;
        /// <summary>Initial client height in pixels; defaults to 720. Updated after resize.</summary>
        public int WindowHeight { get; set; } = 720;
        /// <summary>Initial window title; defaults to Njulf Game. Configure before Run.</summary>
        public string WindowTitle { get; set; } = "Njulf Game";
        /// <summary>Initial native window border; defaults to resizable. Configure before Run.</summary>
        public WindowBorder WindowBorderStyle { get; set; } = WindowBorder.Resizable;
        /// <summary>Startup presentation synchronization; enabled by default. Configure before Run.</summary>
        public bool VSync { get; set; } = true;
        /// <summary>
        /// Maximum host render rate in frames per second; defaults to 60. Zero disables CPU pacing; Vulkan VSync may
        /// still constrain presentation to the display refresh rate.
        /// </summary>
        public double MaximumFramesPerSecond
        {
            get => _maximumFramesPerSecond;
            set
            {
                FramePacer.ValidateMaximumFramesPerSecond(value);
                _maximumFramesPerSecond = value;
            }
        }
        /// <summary>Time spent in the latest host pacing wait, in microseconds.</summary>
        public long LastFramePacingWaitMicroseconds { get; private set; }
        /// <summary>Whether the host is running and exit has not been requested.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>Root for relative content paths; defaults to AppContext.BaseDirectory. Set before Run.</summary>
        public string ContentRoot { get; set; } = AppContext.BaseDirectory;
        /// <summary>Borrowed service provider, available after host initialization and through Unload.</summary>
        public IServiceProvider Services => Require(_services, nameof(Services));
        /// <summary>Borrowed native window for platform integration; available during service configuration.</summary>
        public IWindow Window => Require(_window, nameof(Window));
        /// <summary>Borrowed renderer, available from Initialize through Unload.</summary>
        public IRenderer Renderer => Require(_renderer, nameof(Renderer));
        /// <summary>Host-owned content cache, available from Initialize through Unload.</summary>
        public IContentManager Content => Require(_content, nameof(Content));
        /// <summary>Host-owned input service, updated before gameplay callbacks.</summary>
        public IInputManager Input => Require(_input, nameof(Input));
        /// <summary>Active camera, available from Initialize through Unload; defaults to a first-person camera.</summary>
        public ICamera Camera => Require(_camera, nameof(Camera));
        /// <summary>Host-owned current scene, available immediately after construction and until shutdown.</summary>
        public Scene.Scene Scene => Require(_scene, nameof(Scene));
        /// <summary>Borrowed graphics device on the existing renderer. Resource operations require the device thread.</summary>
        public GraphicsDevice GraphicsDevice => Renderer.GetGraphicsDevice();

        private T Require<T>(T? value, string name) where T : class
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return value ?? throw new InvalidOperationException($"{name} is unavailable before host initialization.");
        }

        /// <summary>Creates the owned scene; other services are initialized by Run.</summary>
        protected Game()
        {
            _scene = new Scene.Scene();
        }

        /// <summary>Wall time since Run began, in microseconds; includes startup and is zero before Run.</summary>
        protected long RunElapsedMicroseconds => _runStartedTimestamp == 0
            ? 0
            : GetElapsedMicroseconds(_runStartedTimestamp);

        /// <summary>Runs once, blocking on the calling game thread until shutdown. Callback failures are rethrown after cleanup.</summary>
        public void Run()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runStarted) throw new InvalidOperationException("A Game instance can only run once.");
            _runStarted = true;
            _isRunning = true;
            _isShuttingDown = false;
            _runStartedTimestamp = Stopwatch.GetTimestamp();
            _nextStartupHeartbeatMicroseconds = 2_000_000L;
            _startupTitleActive = false;
            _framePacer.Reset();
            LastFramePacingWaitMicroseconds = 0L;
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            _hostContext = new GameSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_hostContext);

            try
            {
                _window = RunStartupStep("Game.CreateWindow", CreateWindow);
                HookWindowEvents(_window);
                // Silk's parameterless Run resets the native window before returning.
                // Own that boundary so input, renderer and surface cleanup happen first.
                _window.Initialize();
                _window.Run(PumpWindow);
            }
            catch (Exception ex) { _callbackFailure ??= ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                try { Shutdown(disposeWindow: true); }
                catch (Exception cleanupFailure)
                {
                    if (_callbackFailure == null) _callbackFailure = ExceptionDispatchInfo.Capture(cleanupFailure);
                    else _callbackFailure.SourceException.Data["Njulf.GameCleanupFailure"] = cleanupFailure;
                }
                _isRunning = false;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            _callbackFailure?.Throw();
        }

        private void PumpWindow()
        {
            _window!.DoEvents();
            try
            {
                _hostContext!.Pump();
                PumpContentUploads();
                ObserveInitialContent();
            }
            catch (Exception error) { CallbackFailed(error); }
            if (!_window.IsClosing) _window.DoUpdate();
            if (!_window.IsClosing) _window.DoRender();
        }

        private void InitializeHost()
        {
            if (_window == null)
                throw new InvalidOperationException("The Silk.NET window must be created before services are initialized.");

            _inputContext = _window.CreateInput();

            var services = new ServiceCollection();
            services.AddSingleton(_window);
            services.AddSingleton(_inputContext);
            services.AddSingleton<ICamera>(_ => CreateDefaultCamera());
            services.AddRendering(_window, ConfigureRendering);
            services.AddAssets(ContentRoot);
            services.AddInput();

            RunStartupStep("Game.ConfigureServices", () => ConfigureServices(services));

            _services = services.BuildServiceProvider();

            _renderer = _services.GetRequiredService<IRenderer>();
            _content = _services.GetRequiredService<IContentManager>();
            _contentUploadPump = _services.GetService<IContentUploadPump>();
            _input = _services.GetRequiredService<IInputManager>();
            _camera = _services.GetRequiredService<ICamera>();

            if (_renderer != null)
            {
                RunStartupStep(
                    "Game.ConfigureRendererBeforeInitialize",
                    () => ConfigureRendererBeforeInitialize(_renderer));
                RunStartupStep("VulkanRenderer.Initialize", _renderer.Initialize);
            }
        }

        /// <summary>Initializes game state once with graphics and services available, before content hooks.</summary>
        protected virtual void Initialize() { }
        /// <summary>Configures renderer startup defaults before construction. The supplied options are host-owned.</summary>
        protected virtual void ConfigureRendering(RenderingOptions options) { }

        /// <summary>Replaces or extends default registrations before the host builds its provider.</summary>
        protected virtual void ConfigureServices(IServiceCollection services)
        {
        }

        /// <summary>Creates the default first-person camera at (0, 0, 5), with the window aspect ratio.</summary>
        protected virtual ICamera CreateDefaultCamera()
        {
            return new FirstPersonCamera(new Vector3(0, 0, 5)) { AspectRatio = (float)WindowWidth / WindowHeight };
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

        /// <summary>Loads initial game content on the device thread after Initialize, before LoadAsync.</summary>
        protected virtual void Load()
        {
        }

        /// <summary>Runs after Load. Ordinary awaits resume on the game/device thread.</summary>
        protected virtual Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Updates the scene by default. Input is already published; call base to retain scene updates.</summary>
        protected virtual void Update(GameTime gameTime)
        {
            if (!_isRunning)
                return;

            _scene.Update((float)gameTime.ElapsedGameTime.TotalSeconds);
        }

        /// <summary>Renders the current scene and camera by default. Elapsed time belongs to the draw stream.</summary>
        protected virtual void Draw(GameTime gameTime)
        {
            Renderer.DrawScene(Scene, Camera);
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

        /// <summary>Releases game-owned resources once during shutdown while services remain available.</summary>
        protected virtual void Unload()
        {
        }

        /// <summary>Observes a named startup step before its work begins.</summary>
        protected virtual void OnStartupStepStarted(string name)
        {
        }

        /// <summary>Observes successful startup work and elapsed wall time in microseconds.</summary>
        protected virtual void OnStartupStepSucceeded(string name, long elapsedMicroseconds)
        {
        }

        /// <summary>Observes failed startup work, its exception and elapsed wall time in microseconds.</summary>
        protected virtual void OnStartupStepFailed(string name, Exception exception, long elapsedMicroseconds)
        {
        }

        /// <summary>Observes a positive client size in pixels after renderer and camera resize propagation.</summary>
        protected virtual void OnResize(int width, int height)
        {
        }

        private void ResizeHost(int width, int height)
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
            OnResize(width, height);
        }

        /// <summary>Requests shutdown on the game thread. Active update or draw callbacks finish before cleanup.</summary>
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

        /// <summary>Requests exit and releases owned services after active callbacks. Repeated calls are harmless.</summary>
        public void Dispose()
        {
            if (_disposed || _isShuttingDown) return;
            Exit();

            // Exit() defers the window close while an update or render callback is
            // active. Keep disposal deferred as well so the callback cannot resume
            // against a scene that has already been torn down.
            if (_runStarted)
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
            window.Load += SafeWindowLoad;
            window.Update += SafeWindowUpdate;
            window.Render += SafeWindowRender;
            window.FramebufferResize += SafeWindowResize;
            window.Closing += OnWindowClosing;
        }

        private void CallbackFailed(Exception error)
        {
            _callbackFailure ??= ExceptionDispatchInfo.Capture(error);
            Exit();
        }
        private void SafeWindowLoad() { try { OnWindowLoad(); } catch (Exception e) { CallbackFailed(e); } }
        private void SafeWindowUpdate(double delta) { try { OnWindowUpdate(delta); } catch (Exception e) { CallbackFailed(e); } }
        private void SafeWindowRender(double delta) { try { OnWindowRender(delta); } catch (Exception e) { CallbackFailed(e); } }
        private void SafeWindowResize(Vector2D<int> size) { try { OnWindowFramebufferResize(size); } catch (Exception e) { CallbackFailed(e); } }

        private void OnWindowLoad()
        {
            InitializeHost();
            if (!_isRunning) return;
            _userInitializationStarted = true;
            Initialize();
            if (!_isRunning) return;
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
            if (!_isRunning) return;
            _initialContentTask = LoadAsync(_contentCancellation.Token)
                ?? throw new InvalidOperationException("LoadAsync must return a task.");
            ObserveInitialContent();
        }

        private void PumpContentUploads()
        {
            if (_contentUploadPump?.PendingCount > 0)
            {
                ContentUploadPumpResult result = _contentUploadPump.ProcessFrame(
                    ContentUploadCpuBudget, ContentUploadMaximumCallbacks, ContentUploadMaximumSubmissionBytes);
                if (!_isShuttingDown) OnContentUploadsProcessed(result);
            }
        }

        private void ObserveInitialContent()
        {
            if (_initialContentTask is not { IsCompleted: true } completed) return;
            _initialContentTask = null;
            completed.GetAwaiter().GetResult();
            if (!_isRunning) return;
            _gameClock.Start(TimeSpan.FromMicroseconds(RunElapsedMicroseconds));
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
                    Update(_gameClock.Update(TimeSpan.FromMicroseconds(RunElapsedMicroseconds)));
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

            IRenderer renderer = _renderer;
            LastFramePacingWaitMicroseconds =
                _framePacer.Wait(MaximumFramesPerSecond);
            if (renderer is IRendererFramePacingDiagnostics pacingDiagnostics)
            {
                pacingDiagnostics.ReportFramePacing(
                    MaximumFramesPerSecond,
                    LastFramePacingWaitMicroseconds);
            }

            ObservePipelinePreparation();

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
                        Draw(_gameClock.Draw(TimeSpan.FromMicroseconds(RunElapsedMicroseconds)));
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
                    RendererFrameBoundaryTiming boundaryTiming =
                        renderer is IRendererFrameBoundaryTimingSource timingSource
                            ? timingSource.LastFrameBoundaryTiming
                            : default;
                    long beginOtherMicroseconds = System.Math.Max(
                        0L,
                        beginFrameMicroseconds -
                        boundaryTiming.FrameFenceWaitMicroseconds -
                        boundaryTiming.SwapchainAcquireMicroseconds);
                    Console.WriteLine(
                        $"Render frame hitch: total={frameMicroseconds / 1000.0:F3}ms, " +
                        $"pacing={LastFramePacingWaitMicroseconds / 1000.0:F3}ms, " +
                        $"begin={beginFrameMicroseconds / 1000.0:F3}ms, " +
                        $"beginFence={boundaryTiming.FrameFenceWaitMicroseconds / 1000.0:F3}ms, " +
                        $"beginAcquire={boundaryTiming.SwapchainAcquireMicroseconds / 1000.0:F3}ms, " +
                        $"beginOther={beginOtherMicroseconds / 1000.0:F3}ms, " +
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
            UpdateProgressiveStartupObservability(snapshot, elapsed);
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

        private void UpdateProgressiveStartupObservability(
            in RendererStartupSnapshot snapshot,
            long elapsedMicroseconds)
        {
            if (_window == null)
                return;

            if (snapshot.FullQualityPresented)
            {
                if (_startupTitleActive)
                {
                    _window.Title = WindowTitle;
                    _startupTitleActive = false;
                }
                return;
            }

            string active = snapshot.ActivePipelineCount == 0
                ? "no native pipeline active"
                : snapshot.ActivePipelineCount == 1
                    ? "1 native pipeline active"
                    : $"{snapshot.ActivePipelineCount} native pipelines active";
            string oldest = ResolveOldestActivePipelineBasename(
                snapshot.ActivePipelineSummary);
            _window.Title =
                $"{WindowTitle} - loading {elapsedMicroseconds / 1_000_000.0:F1}s; " +
                $"{snapshot.PipelinesCompleted} pipelines complete; {active}" +
                (string.IsNullOrEmpty(oldest) ? string.Empty : $"; {oldest}");
            _startupTitleActive = true;

            if (elapsedMicroseconds < _nextStartupHeartbeatMicroseconds)
                return;

            Console.WriteLine(
                $"Renderer startup heartbeat: elapsed=" +
                $"{elapsedMicroseconds / 1_000_000.0:F3}s, " +
                $"phase={snapshot.Phase}, completed=" +
                $"{snapshot.PipelinesCompleted}, active=" +
                $"{snapshot.ActivePipelineCount}, oldest=" +
                $"{snapshot.OldestActivePipelineMicroseconds / 1_000_000.0:F3}s" +
                (string.IsNullOrEmpty(oldest)
                    ? string.Empty
                    : $", pipeline={oldest}"));
            _nextStartupHeartbeatMicroseconds = checked(
                elapsedMicroseconds + 10_000_000L);
        }

        private static string ResolveOldestActivePipelineBasename(
            string activePipelineSummary)
        {
            if (string.IsNullOrWhiteSpace(activePipelineSummary))
                return string.Empty;

            string identity = activePipelineSummary.Split(',', 2)[0].Trim();
            int separator = System.Math.Max(
                identity.LastIndexOf('/'),
                identity.LastIndexOf('\\'));
            return separator >= 0 && separator + 1 < identity.Length
                ? identity[(separator + 1)..]
                : identity;
        }

        private void OnWindowFramebufferResize(Vector2D<int> size)
        {
            ResizeHost(size.X, size.Y);
        }

        private void OnWindowClosing()
        {
            _isRunning = false;
            // Run() owns teardown in its finally block. Keep the window callback
            // responsive while renderer-owned native work drains there.
        }

        private void Shutdown(bool disposeWindow)
        {
            // Stop callbacks before tearing down services owned by Update/Draw. Some window
            // backends can dispatch one last callback while Run() or window disposal unwinds.
            _isRunning = false;

            if (_isShuttingDown || _disposed)
                return;

            _isShuttingDown = true;
            List<Exception>? failures = null;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception e) { (failures ??= new()).Add(e); }
            }
            try
            {
                Cleanup(() => (_content as IContentLifetime)?.BeginShutdown());
                Cleanup(_contentCancellation.Cancel);
                Cleanup(() => (_contentUploadPump as IContentUploadLifetime)?.BeginShutdown());
                // Startup continuations and cooperative cancellation can require the device thread.
                while (_initialContentTask is { IsCompleted: false } ||
                       (_content as IContentLifetime)?.ActiveOperationCount > 0 ||
                       _contentUploadPump?.PendingCount > 0)
                {
                    Cleanup(() => _hostContext?.Pump());
                    Cleanup(PumpContentUploads);
                    Thread.Yield();
                }
                Cleanup(() =>
                {
                    try { _initialContentTask?.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (_contentCancellation.IsCancellationRequested) { }
                });
                Cleanup(() => _pipelinePreparationCancellation?.Cancel());
                // Renderer drains its own production, scene and post-present jobs.
                if (_renderer is VulkanRenderer vulkan) Cleanup(vulkan.DrainStartupPreparation);
                Cleanup(() =>
                {
                    try { _pipelinePreparationTask?.GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (_pipelinePreparationCancellation?.IsCancellationRequested == true) { }
                });
                if (_userInitializationStarted)
                {
                    _userInitializationStarted = false;
                    Cleanup(Unload);
                }
                Cleanup(_scene.Dispose);
                Cleanup(() => _renderer?.Dispose());
                if (_services is IDisposable disposableServices) Cleanup(disposableServices.Dispose);
                Cleanup(() => _inputContext?.Dispose());
                if (disposeWindow) Cleanup(() => _window?.Dispose());
            }
            finally
            {
                _pipelinePreparationTask = null;
                _initialContentTask = null;
                _contentCancellation.Dispose();
                _contentUploadPump = null;
                _pipelinePreparationCancellation?.Dispose();
                _pipelinePreparationCancellation = null;
                _progressiveContentLoadPending = false;
                _progressiveScenePreparationPending = false;
                _contentLoaded = false;
                _services = null;

                _inputContext = null;

                _renderer = null;
                _content = null;
                _input = null;
                _camera = null;

                if (disposeWindow)
                {
                    _window = null;
                }

                _isShuttingDown = false;
                _disposed = true;
            }
            if (failures != null) throw new AggregateException("Game shutdown failed.", failures);
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

        /// <summary>Runs named startup work synchronously and reports its duration and outcome.</summary>
        protected void RunStartupStep(string name, Action action)
        {
            RunStartupStep<object?>(
                name,
                () =>
                {
                    action();
                    return null;
                });
        }

        /// <summary>Runs named startup work synchronously, returning its result and reporting timing.</summary>
        protected T RunStartupStep<T>(string name, Func<T> action)
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
