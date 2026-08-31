using Njulf.Core.Math;

namespace Njulf.Core.Interfaces
{
    public interface IRenderer
    {
        void Initialize();
        bool BeginFrame();
        void EndFrame();
        void Clear(Color color);
        void DrawScene(Scene.Scene scene, ICamera camera);
        void Resize(int width, int height);
        void Dispose();
    }

    /// <summary>
    /// Optional renderer lifecycle state used by the host to avoid ending a
    /// frame that the renderer already abandoned after a submission fault.
    /// </summary>
    public interface IRendererFrameState
    {
        bool IsFrameInProgress { get; }
    }

    public readonly record struct RendererFrameBoundaryTiming(
        long FrameFenceWaitMicroseconds,
        long SwapchainAcquireMicroseconds);

    /// <summary>
    /// Optional host/renderer diagnostics bridge. It keeps host pacing outside
    /// renderer timing while still publishing the configured limit and wait.
    /// </summary>
    public interface IRendererFramePacingDiagnostics
    {
        void ReportFramePacing(
            double maximumFramesPerSecond,
            long waitMicroseconds);
    }

    /// <summary>
    /// Optional renderer timing source used to break BeginFrame into its two
    /// potentially blocking Vulkan operations and remaining CPU work.
    /// </summary>
    public interface IRendererFrameBoundaryTimingSource
    {
        RendererFrameBoundaryTiming LastFrameBoundaryTiming { get; }
    }

    /// <summary>
    /// Optional renderer capability for compiling the pipelines required by a
    /// fully loaded scene before its first frame is recorded.
    /// </summary>
    public interface IScenePipelinePreparer
    {
        void PrepareScene(Scene.Scene scene, ICamera camera);
    }

    /// <summary>
    /// User-visible renderer startup stages. Bootstrap can present without a
    /// graphics pipeline. FallbackScene is retained for source compatibility,
    /// but the default startup path advances directly to ProductionPreparing.
    /// </summary>
    public enum RendererStartupPhase
    {
        Bootstrap,
        FallbackScene,
        ProductionPreparing,
        FullQuality,
        Faulted
    }

    /// <summary>
    /// Immutable progress reported by a renderer whose production pipelines
    /// are prepared after the window has become responsive.
    /// </summary>
    public readonly record struct RendererStartupSnapshot(
        RendererStartupPhase Phase,
        long ElapsedMicroseconds,
        long PhaseElapsedMicroseconds,
        bool BootstrapPresented,
        bool ScenePresented,
        bool FullQualityPresented,
        ulong PipelinesCompleted,
        string Detail)
    {
        /// <summary>
        /// Native Vulkan pipeline creations that have entered the driver but
        /// have not returned yet. This remains zero when the renderer is doing
        /// non-pipeline startup work.
        /// </summary>
        public int ActivePipelineCount { get; init; }

        /// <summary>
        /// Elapsed time for the oldest active native pipeline creation.
        /// </summary>
        public long OldestActivePipelineMicroseconds { get; init; }

        /// <summary>
        /// Bounded, human-readable identities for the active pipelines.
        /// </summary>
        public string ActivePipelineSummary { get; init; } = string.Empty;

        public bool IsFullQuality =>
            Phase == RendererStartupPhase.FullQuality;
        public bool IsFaulted => Phase == RendererStartupPhase.Faulted;
    }

    /// <summary>
    /// Optional progressive counterpart to <see cref="IScenePipelinePreparer"/>.
    /// The host starts production work as soon as the renderer has initialized
    /// so native pipeline compilation can overlap content loading.
    /// </summary>
    public interface IProgressiveScenePipelinePreparer :
        IScenePipelinePreparer
    {
        bool IsProgressiveStartupEnabled { get; }
        RendererStartupSnapshot StartupSnapshot { get; }

        /// <summary>
        /// Creates production resources on the device-owning thread and starts
        /// production pipeline compilation immediately.
        /// </summary>
        void BeginProductionPreparation();

        [Obsolete(
            "Use BeginProductionPreparation. The synthetic fallback-scene path is no longer part of startup.")]
        void PrepareFallbackScene() => BeginProductionPreparation();

        Task PrepareSceneAsync(
            Scene.Scene scene,
            ICamera camera,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional renderer capability for classifying and enforcing the
    /// application time from <see cref="Game.Run"/> to the first completed
    /// presentation.
    /// </summary>
    public interface IStartupLatencyReporter
    {
        void ReportFirstPresent(long elapsedMicroseconds);
    }

    public enum RendererStartupMilestone
    {
        BootstrapPresent,
        ScenePresent,
        FullQualityPresent,
        VisibleContentPresent
    }

    /// <summary>
    /// Optional reporter for the independent responsive, real-scene, and
    /// production-quality startup gates.
    /// </summary>
    public interface IStartupMilestoneLatencyReporter
    {
        void ReportStartupMilestone(
            RendererStartupMilestone milestone,
            long elapsedMicroseconds);
    }
}
