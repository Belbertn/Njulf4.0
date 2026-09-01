using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Shaders;
using static Njulf.Rendering.RenderingConstants;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Buffer = Silk.NET.Vulkan.Buffer;
using ICamera = Njulf.Core.Interfaces.ICamera;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Njulf.Rendering
{
    /// <summary>
    /// Main Vulkan renderer implementing IRenderer.
    /// Coordinates all subsystems and manages the render loop.
    /// 
    /// OWNERSHIP RULES:
    /// - VulkanRenderer ORCHESTRATES: owns the render loop, frame lifecycle, and pass execution order
    /// - Managers OWN ALLOCATION: BufferManager, TextureManager, MeshManager, LightManager own their resources
    /// - Managers OWN LIFETIME: each manager is responsible for creating/destroying its Vulkan objects
    /// - VulkanRenderer RECORDS COMMANDS ONLY: does not own Vulkan objects, only records commands into managers' resources
    /// - Passes ONLY RECORD COMMANDS: VulkanRenderer calls methods on passes which record into command buffers
    /// </summary>
    public unsafe class VulkanRenderer : IRenderer, IRendererFrameState,
        IRendererFramePacingDiagnostics,
        IRendererFrameBoundaryTimingSource,
        IProgressiveScenePipelinePreparer,
        IStartupLatencyReporter, IStartupMilestoneLatencyReporter,
        IRendererDebugTools, IDisposable
    {
        public bool IsFrameInProgress => _lifetime.FrameInProgress;

        public bool IsProgressiveStartupEnabled =>
            RendererBuildConfiguration.ProgressivePipelineStartup;

        public RendererStartupSnapshot StartupSnapshot
        {
            get
            {
                RendererStartupSnapshot snapshot;
                lock (_startupGate)
                {
                    long now = Stopwatch.GetTimestamp();
                    GiPipelineCacheTelemetry telemetry =
                        _giPipelineCacheService?.Telemetry ??
                        GiPipelineCacheTelemetry.Empty;
                    snapshot = new RendererStartupSnapshot(
                        _startupPhase,
                        ElapsedMicroseconds(_startupStartedTimestamp, now),
                        ElapsedMicroseconds(
                            _startupPhaseStartedTimestamp,
                            now),
                        _bootstrapPresented,
                        _startupScenePresented,
                        _fullQualityPresented,
                        telemetry.PipelineCreationCount,
                        _startupDetail)
                    {
                        ActivePipelineCount =
                            telemetry.ActivePipelineCount,
                        OldestActivePipelineMicroseconds =
                            telemetry.OldestActivePipelineMicroseconds,
                        ActivePipelineSummary =
                            telemetry.ActivePipelineSummary
                    };
                }
                _startupLog?.WriteSnapshot(snapshot);
                return snapshot;
            }
        }

        internal static IReadOnlyList<string> ProductionRenderPassOrder =>
            ProductionRenderPipelineDeclaration.Instance.PassOrder;

        // The exact B1 transaction may span ordered graphics submissions, but
        // every pass that writes its private candidate buffer must remain on
        // that one graphics queue. Alpha/foliage feedback is emitted inside
        // ForwardPlus; reflection-capture and refinement completion are
        // appended to the terminal graphics command buffer outside the graph.
        private static readonly string[]
            ExactReceiverFeedbackGraphicsProducerPasses =
            [
                "ForwardPlusPass",
                "TransparentForwardPass",
                "WeightedTransparentPass",
                "ParticlePass"
            ];

        private static readonly string[]
            ExactReceiverFeedbackGraphicsProducerPassesWithFog =
            [
                "ForwardPlusPass",
                "TransparentForwardPass",
                "WeightedTransparentPass",
                "ParticlePass",
                "FogPass"
            ];

        private readonly IWindow _window;
        private readonly VulkanContext _context;
        private readonly RendererStartupLog? _startupLog;
        private readonly SwapchainManager _swapchain;
        private readonly SynchronizationManager _sync;
        private readonly FrameSubmissionOwnershipTracker
            _submissionOwnership;
        private readonly CommandBufferManager _cmd;
        private readonly BufferManager _bufferManager;
        private readonly TextureManager _textureManager;
        private readonly MeshManager _meshManager;
        private readonly MaterialManager _materialManager;
        private readonly LightManager _lightManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly RenderGraph _renderGraph;
        private readonly SceneDataBuilder _sceneDataBuilder;
        private readonly StagingRing _stagingRing;
        private readonly FenceBasedDeleter _deleter;
        private readonly IModelRenderUploadService _modelUploadService;
        private readonly VulkanMeshletPhysicalResidencyResources?
            _meshletPhysicalResidencyResources;
        private readonly OverlayDrawDataSource _overlayDrawData = new();
        private readonly RendererDiagnosticsBuffer _diagnosticsBuffer;
        private readonly GpuTimestampRecorder _gpuTimestamps;
        private readonly ParticleSystemManager _particleSystemManager = new();
        private readonly UploadBudgetTracker _uploadBudgetTracker = new();
        private readonly RuntimeStallTracker _stallTracker = new();
        private readonly RendererLifetimeCoordinator _lifetime;

        private readonly RendererDiagnosticsAssembler
            _diagnosticsAssembler = new();

        private readonly ShadowFramePlanner _shadowFramePlanner = new();

        private readonly PerformanceCaptureMetadataProvider
            _performanceCaptureMetadataProvider;

        private readonly PerformanceSampleWindow _globalIlluminationCpuTimingWindow = new(120);
        private readonly AsyncComputeCoordinator _asyncComputeCoordinator;
        private readonly List<DeferredAsyncSubmission> _deferredAsyncSubmissions = new();
        private readonly bool _ownsDependencies;

        private readonly AdvancedGiAdmissionCoordinator
            _advancedGiAdmission = new();

        private readonly DdgiSceneInvalidationCoordinator
            _ddgiInvalidation;

        private DdgiEmissiveTransportCoordinator? _ddgiEmissiveTransport;

        // Directional ray modes remain explicit Experimental features unless
        // this immutable, artifact-pinned manifest matches the exact build,
        // settings, resolution, AA mode, device, driver, and geometry policy.
        private DirectionalShadowQualificationManifest
            _directionalShadowQualificationManifest =
                DirectionalShadowQualificationManifest.Empty;

        private HiZDepthPyramid? _hizDepthPyramid;
        private RenderTargetManager? _renderTargets;
        private DirectionalShadowResources? _directionalShadowResources;
        private DirectionalShadowHistoryResources? _directionalShadowHistoryResources;

        private readonly DirectionalShadowStabilizationState
            _directionalShadowStabilizationState = new();

        private SpotShadowAtlas? _spotShadowAtlas;
        private PointShadowCubemapArray? _pointShadowCubemapArray;
        private EnvironmentManager? _environmentManager;
        private readonly IesPhotometricProfileManager _iesPhotometricProfileManager;
        private ReflectionProbeManager? _reflectionProbeManager;
        private AutomaticPlanarReflectionManager
            _automaticPlanarReflectionManager = null!;
        private ForwardPlusPass? _forwardPlusPass;
        private ReflectionProbeCapturePass? _reflectionProbeCapturePass;
        private ReflectionProbePrefilterPass? _reflectionProbePrefilterPass;
        private ReflectionProbePublishPass? _reflectionProbePublishPass;
        private readonly ReflectionProbeCompletionValueProvider _reflectionProbeCompletionValues = new();
        private SimpleDdgiVolumeManager? _simpleDdgiVolumeManager;

        private SimpleDdgiLightTreeGpuResources? _simpleDdgiLightTreeResources;

        // B1 owns a real 48-byte all-producer capture source. Construction is
        // still allocation-free; exact buffers and pipelines are created only
        // after the prerequisite/mode/memory/producer transaction is admitted.
        private SimpleDdgiReceiverFeedbackCoordinator?
            _simpleDdgiReceiverFeedback;

        // Buffer-only transient categories use one physical arena when graph
        // lifetimes prove they cannot overlap. Optional images remain owned by
        // their format-specific render-target allocators.
        private AdvancedGiTransientBufferArena? _advancedGiTransientBufferArena;

        // C3 uses a source-cache-owned direction/PDF sidecar and an exact
        // generation-time variable-PDF handshake. Construction remains
        // allocation-free until that handshake and the workload are admitted.
        private SimpleDdgiGuidingVulkanRuntime? _simpleDdgiGuidingRuntime;

        private SimpleDdgiGuidingSourceCacheSidecar?
            _simpleDdgiGuidingSourceCacheSidecar;

        private SimpleDdgiGuidingFrameCoordinator?
            _simpleDdgiGuidingFrameCoordinator;

        private SimpleDdgiFrameCoordinator? _simpleDdgiFrames;

        // C4 is an independently admitted authored-hero feature. Its complete
        // plan/runtime/publication state lives behind one frame coordinator.
        private readonly GiCausticFrameCoordinator _giCaustic;

        // C5 admission and all active/prepared/retired generation state live
        // behind one coordinator. Renderer effects remain explicit below.
        private readonly SimpleDdgiNearFieldResidualCoordinator
            _nearFieldResidual;

        private FarFieldClipmapManager? _farFieldClipmapManager;
        private AccelerationStructureManager? _accelerationStructureManager;
        private RaySceneDescriptorBank? _raySceneDescriptorBank;
        private DdgiFoliageProxyManager? _ddgiFoliageProxyManager;

        private DdgiFoliageProxyFrame _ddgiFoliageProxyFrame =
            DdgiFoliageProxyFrame.Empty(0);

        private AutoExposureManager? _autoExposureManager;
        private GiPipelineCacheService? _giPipelineCacheService;
        private HybridReflectionVulkanRuntime? _hybridReflectionRuntime;
        private int _hybridReflectionReceiverPipelinesPrepared;
        private int _hybridReflectionReceiverPerformancePipelinesPrepared;
        private SmaaResources? _smaaResources;
        private SkinningManager _skinningManager = null!;
        private GpuParticleRuntimeManager _gpuParticleRuntimeManager = null!;
        private readonly LocalShadowSelector _localShadowSelector = new();
        private readonly GPUSpotShadow[] _spotShadowScratch = new GPUSpotShadow[32];
        private readonly GPUPointShadow[] _pointShadowScratch = new GPUPointShadow[4];

        private readonly GPULocalLightShadowIndex[] _localShadowIndexScratch =
            new GPULocalLightShadowIndex[LightManager.MaxLights];

        private ulong _lastSpotShadowUploadSignature;
        private ulong _lastPointShadowUploadSignature;
        private ulong _lastLocalShadowIndexUploadSignature;
        private bool _hasUploadedSpotShadows;
        private bool _hasUploadedPointShadows;
        private bool _hasUploadedLocalShadowIndices;
        private ulong _lastDirectionalShadowRecordSignature;
        private ulong _lastSpotShadowRecordSignature;
        private ulong _lastPointShadowRecordSignature;
        private bool _hasDirectionalShadowRecordSignature;
        private bool _hasSpotShadowRecordSignature;

        private bool _hasPointShadowRecordSignature;

        // Pipelines
        private MeshPipeline _meshPipeline = null!;
        private ComputePipeline _computePipeline = null!;
        private CompositePipeline _compositePipeline = null!;
        private CompositePipeline _ldrCompositePipeline = null!;
        private WeightedOitCompositePipeline _weightedOitCompositePipeline = null!;
        private SkyboxPipeline _skyboxPipeline = null!;
        private ParticlePipeline _particlePipeline = null!;
        private FogPass _fogPass = null!;
        private SimpleDdgiTracePass? _simpleDdgiTracePass;
        private SimpleDdgiSchedulePass? _simpleDdgiSchedulePass;
        private SimpleDdgiPageDemandPass? _simpleDdgiPageDemandPass;
        private SimpleDdgiPageResidencyPass? _simpleDdgiPageResidencyPass;
        private SimpleDdgiPageFeedbackPass? _simpleDdgiPageFeedbackPass;
        private SimpleDdgiRelocateClassifyPass? _simpleDdgiRelocateClassifyPass;

        private SimpleDdgiDirectionalRadiancePass?
            _simpleDdgiDirectionalRadiancePass;

        private SimpleDdgiAcceleratedSolvePass? _simpleDdgiAcceleratedSolvePass;
        private SimpleDdgiTransportPass? _simpleDdgiTransportPass;
        private SimpleDdgiBlendPass? _simpleDdgiBlendPass;
        private SimpleDdgiPublishPass? _simpleDdgiPublishPass;
        private SimpleDdgiTransportAuditPass? _simpleDdgiTransportAuditPass;
        private SimpleDdgiSchedulerCommitPass? _simpleDdgiSchedulerCommitPass;
        private SkinningPass _skinningPass = null!;

        private DdgiFoliageProxyGenerationPass?
            _ddgiFoliageProxyGenerationPass;

        private GpuParticleResetPass _gpuParticleResetPass = null!;
        private GpuParticleSimulatePass _gpuParticleSimulatePass = null!;
        private GpuParticleSortPass _gpuParticleSortPass = null!;
        private GpuParticleResetGraphPass _gpuParticleResetGraphPass = null!;
        private GpuParticleSimulateGraphPass _gpuParticleSimulateGraphPass = null!;
        private GpuParticleSortGraphPass _gpuParticleSortGraphPass = null!;
        private FoliageManager _foliageManager = null!;
        private FoliagePipeline _foliagePipeline = null!;
        private FoliageCullPass _foliageCullPass = null!;
        private SceneOpaqueCompactionPass _sceneOpaqueCompactionPass = null!;
        private ForwardVisibilityCompactionPass _forwardVisibilityCompactionPass = null!;
        private readonly HashSet<ParticleBlendMode> _particleBlendModeScratch = new();
        private bool _initialScenePipelinesPrepared;
        private bool _pipelineCachePersistenceScheduled;
        private bool _fullQualityCachePersistenceScheduled;
        private Action? _postFirstPresentPipelinePreparation;
        private bool _postFirstPresentPipelinePreparationScheduled;
        private int _postFirstPresentPipelinePreparationGeneration;
        private int _postFirstPresentPipelineSpecializationsReady = 1;
        private readonly object _startupGate = new();
        private RendererStartupPhase _startupPhase =
            RendererStartupPhase.Bootstrap;
        private string _startupDetail =
            "Bootstrap device path is initializing.";
        private long _startupStartedTimestamp;
        private long _startupPhaseStartedTimestamp;
        private Task? _productionInitializationTask;
        private Task? _scenePreparationTask;
        private int _renderThreadManagedId;
        private int _pipelineCacheRenderCriticalFramesStarted;
        private bool _productionPreparationStarted;
        private bool _productionResourcesReady;
        private Exception? _startupFailure;
        private int _productionGraphReady;
        private bool _progressiveFrame;
        private RendererStartupPhase _progressiveFramePhase;
        private bool _bootstrapPresented;
        private bool _startupScenePresented;
        private bool _fullQualityPresented;
        private bool _productionFrameWasFullQuality;
        private DirectionalShadowPass? _directionalShadowPass;
        private DirectionalRayShadowPass? _directionalRayShadowPass;
        private AreaRayShadowPass? _areaRayShadowPass;

        // State
        private int _currentFrame = 0;
        private uint _allocatorFrameIndex;
        private uint _temporalSampleIndex;
        private ulong _ddgiFrameSerial;

        private readonly ulong[] _submittedGraphicsFrameFenceValues =
            new ulong[FramesInFlight];

        private ulong _completedGraphicsFrameFenceValue;
        private uint _imageIndex;
        private int _currentAcquireSemaphoreIndex;
        private CommandBuffer _currentCommandBuffer;

        private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
        private RenderBudgetSnapshot _lastBudgetSnapshot = RenderBudgetSnapshot.Empty;
        private SceneRenderingData? _lastSceneData;
        private Scene? _volumetricDensityCacheScene;
        private uint _volumetricDensityCacheRevision = uint.MaxValue;
        private VolumetricDensityVolume[] _sortedVolumetricDensityVolumes = [];
        private readonly DebugOverlayBuilder _debugOverlayBuilder;
        private readonly ScreenshotCaptureService _screenshotCaptureService = new();
        private readonly ScreenshotReadbackManager _screenshotReadbackManager;
        private readonly LinearHdrCaptureService _linearHdrCaptureService = new();
        private readonly LinearHdrReadbackManager _linearHdrReadbackManager;
        private readonly RenderDocCaptureService _renderDocCaptureService = new();
        private GpuMeshletCounters _completedGpuCounters;
        private DdgiForwardEstimateCounters _completedDdgiForwardEstimateCounters;
        private DdgiInvestigationCounters _completedDdgiInvestigationCounters;

        private DirectionalShadowReceiverCounters _completedDirectionalShadowReceiverCounters =
            DirectionalShadowReceiverCounters.Empty;

        private DirectionalShadowCasterDiagnostics _completedDirectionalShadowCasterDiagnostics =
            DirectionalShadowCasterDiagnostics.Empty;

        private DirectionalShadowRayCounters _completedDirectionalShadowRayCounters =
            DirectionalShadowRayCounters.Empty;

        private HybridReflectionCounterSnapshot _completedHybridReflectionCounters =
            HybridReflectionCounterSnapshot.Empty;
        private TransparentReflectionGpuCounters
            _completedTransparentReflectionCounters =
                TransparentReflectionGpuCounters.Empty;

        private readonly DirectionalShadowCasterFrameCapture[] _directionalShadowCasterFrameCaptures =
            new DirectionalShadowCasterFrameCapture[FramesInFlight];

        private FarFieldMaterialV2Counters _completedFarFieldMaterialV2Counters;
        private MaterialGiGpuCounters _completedMaterialGiCounters;
        private ThinSurfaceTransportCounters _completedThinSurfaceTransportCounters;

        private DdgiGeometryParticipationGpuCounters
            _completedDdgiGeometryParticipationCounters;

        private DdgiManyLightGpuCounters _completedDdgiManyLightCounters;
        private DdgiAreaLightGpuCounters _completedDdgiAreaLightCounters;
        private GpuParticleCounterSnapshot _completedGpuParticleCounters;
        private FoliageCounterSnapshot _completedFoliageCounters;
        private SceneSubmissionCounterSnapshot _completedSceneSubmissionCounters;
        private SceneSubmissionCounterSnapshot _completedForwardVisibilityCounters;
        private SceneSubmissionValidationSnapshot _completedSceneSubmissionValidation;

        private readonly SimpleDdgiFrameEvidenceCoordinator
            _simpleDdgiFrameEvidence = new(FramesInFlight);

        private readonly HiZVisibilityPolicyRuntimeState _hizVisibilityPolicyState = new();
        private Scene? _lastHiZScene;
        private bool _hasLastHiZCameraPose;
        private Vector3 _lastHiZCameraPosition;
        private Vector3 _lastHiZCameraForward;
        private int _previousHiZCameraMotionSuppressionFramesRemaining;
        private bool _previousHiZCameraMotionSuppressedThisFrame;
        private long _lastParticleTimestamp;
        private float _particleTimeSeconds;
        private long _lastAcquireImageMicroseconds;
        private long _lastSwapchainImageOwnerWaitMicroseconds;
        private long _lastFrameResourceRecycleWaitMicroseconds;
        private ulong _lastAcquiredImageOwnerSubmissionSerial;
        private int _lastAcquiredImageOwnerFrameContext = -1;
        private ulong _lastRecycledFrameResourceOwnerSubmissionSerial;
        private long _lastQueueSubmitMicroseconds;
        private long _lastPresentMicroseconds;
        private double _hostMaximumFramesPerSecond;
        private long _hostFramePacingWaitMicroseconds;
        private AsyncComputeResourcePlanGeneration? _asyncComputeResourcePlanGeneration;

        private RenderGraphResourcePlan? _asyncComputeResourcePlan;

        // Renderer-owned scratch keeps ExecuteSelected's pass predicate allocation-free.
        private readonly HashSet<string> _asyncComputePassNameScratch =
            new(StringComparer.Ordinal);

        private bool _swapchainImageTransitionedThisFrame;
        private bool _lastAmbientOcclusionTargetEnabled = true;
        private float _lastAmbientOcclusionResolutionScale = 0.5f;
        private AmbientOcclusionMode _lastAmbientOcclusionMode =
            AmbientOcclusionMode.Ssao;
        private bool _gtaoRuntimeSupported;
        private AntiAliasingMode _lastAntiAliasingTargetMode = AntiAliasingMode.SmaaMedium;
        private bool _lastMotionVectorTargetEnabled = true;
        private TransparencyMode _lastTransparencyTargetMode = TransparencyMode.SortedAlphaBlend;
        private int _lastBloomTargetMipCount = 6;
        private bool _lastFogTargetEnabled = true;
        private bool _lastMaterialTransportProvenanceTargetEnabled;
        private bool _lastHybridReflectionTargetEnabled;
        private bool _hybridReflectionTargetProvisioned;
        private float _lastHybridReflectionRayBudgetFraction;
        private Extent2D _lastSceneRenderExtent;
        private float _lastEffectiveResolutionScale = 1.0f;
        private readonly DynamicResolutionScaleController _dynamicResolutionScaleController = new();
        private string _lastRenderTargetRecreateReason = string.Empty;

        // Scene state
        private Color _clearColor = Color.Black;
        public RendererDiagnostics LastDiagnostics => _lastDiagnostics;
        RendererFrameBoundaryTiming
            IRendererFrameBoundaryTimingSource.LastFrameBoundaryTiming =>
            new(
                _lastSwapchainImageOwnerWaitMicroseconds +
                _lastFrameResourceRecycleWaitMicroseconds,
                _lastAcquireImageMicroseconds);
        public RenderBudgetSnapshot LastBudgetSnapshot => _lastBudgetSnapshot;
        public DeviceRequirementReport? SelectedDeviceRequirementReport => _context.SelectedDeviceRequirementReport;
        public MemoryHeapBudgetSnapshot CurrentMemoryHeapBudget => _context.GetMemoryHeapBudgetSnapshot();
        public RendererValidationMessageSnapshot ValidationMessageSnapshot =>
            _context.ValidationMessageSnapshot;
        public DebugDrawList DebugDraw => _debugOverlayBuilder.DrawList;
        public DebugOverlaySettings DebugOverlays => Settings.Debug;

        /// <summary>
        /// B1's explicit capability, allocation, and fence-publication state.
        /// Disabled or rejected modes report zero B1-owned buffers.
        /// </summary>
        public SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics
            SimpleDdgiReceiverFeedbackRuntimeDiagnostics =>
            _simpleDdgiReceiverFeedback?.Diagnostics ??
            SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;

        /// <summary>
        /// C3's explicit source-cache capability and allocation state. Slot
        /// 203 remains source-cache-owned; rejected handshakes own zero C3
        /// allocations and cannot publish a guided sample.
        /// </summary>
        public SimpleDdgiGuidingGpuRuntimeDiagnostics
            SimpleDdgiGuidingRuntimeDiagnostics =>
            _simpleDdgiGuidingRuntime?.Diagnostics ??
            SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;

        /// <summary>
        /// C4's authoritative resource/publication state. A renderer without
        /// bound qualification evidence or an eligible authored hero reports
        /// the disabled zero-allocation snapshot.
        /// </summary>
        public GiCausticVulkanRuntimeDiagnostics GiCausticRuntimeDiagnostics =>
            _giCaustic.Diagnostics;

        /// <summary>
        /// C5's concrete allocation/history state. A default renderer reports
        /// Disabled and owns no C5 resources because no post-B3 evidence has
        /// been supplied.
        /// </summary>
        public SimpleDdgiNearFieldResidualGpuRuntimeSnapshot
            SimpleDdgiNearFieldResidualRuntimeSnapshot =>
            _nearFieldResidual.RuntimeSnapshot;

        /// <summary>
        /// C5's active/pending/retired image-and-runtime ownership state. The
        /// snapshot includes the independent 96/192 MiB budget charges and
        /// the greatest active-generation fence reference.
        /// </summary>
        public SimpleDdgiNearFieldResidualGenerationSnapshot
            SimpleDdgiNearFieldResidualGenerationSnapshot =>
            _nearFieldResidual.GenerationSnapshot;

        /// <summary>
        /// Explicit development-only sparse residency command. Merely selecting
        /// a residency debug view never calls this API.
        /// </summary>
        public bool TrySetSimpleDdgiProbeResidencyDevelopmentPin(
            int virtualPageIndex,
            bool pinned)
        {
            _lifetime.ThrowIfDisposalStarted();
            return Settings.Debug.Enabled &&
                   _simpleDdgiVolumeManager?.TrySetProbeResidencyDevelopmentPin(
                       virtualPageIndex,
                       pinned) == true;
        }

        /// <summary>
        /// Explicit development-only in-place mutation freeze. Runtime failure
        /// latches remain independent and cannot be cleared through this API.
        /// </summary>
        public bool SetSimpleDdgiProbeResidencyDevelopmentFreeze(bool frozen)
        {
            _lifetime.ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || _simpleDdgiVolumeManager == null)
                return false;
            _simpleDdgiVolumeManager.SetProbeResidencyDevelopmentFreeze(frozen);
            return true;
        }

        public LightingVersionSnapshot LightingVersions
        {
            get
            {
                SimpleDdgiAtmosphereCohortFeedback? cohort =
                    _simpleDdgiVolumeManager is { TransportV2Active: true }
                        ? _simpleDdgiVolumeManager.CreateAtmosphereCohortFeedbackSnapshot()
                        : null;
                uint livePropagationSourceGeneration =
                    _simpleDdgiVolumeManager?.LivePropagationSourceGeneration ?? 0u;
                bool currentLivePropagation = cohort is { } live &&
                                              livePropagationSourceGeneration != 0u &&
                                              livePropagationSourceGeneration == live.SourceCohortGeneration;
                uint completedSourceGeneration = currentLivePropagation
                    ? livePropagationSourceGeneration
                    : cohort is { } source &&
                      !source.SourceCohortActive &&
                      source.StaleParticipatingProbeCount == 0 &&
                      source.SourceCohortGeneration ==
                      source.AdmittedSourceCohortGeneration &&
                      source.QuietPeriodComplete
                        ? source.SourceCohortGeneration
                        : 0u;
                uint convergedGeneration = currentLivePropagation
                    ? livePropagationSourceGeneration
                    : cohort is { } converged &&
                      completedSourceGeneration != 0u &&
                      converged.MinimumPropagationBoundaryComplete &&
                      converged.PropagationGeneration ==
                      converged.PublishedPropagationGeneration
                        ? converged.SourceCohortGeneration
                        : 0u;
                return new LightingVersionSnapshot(
                    _environmentManager?.AtmosphereFrame.Revision ?? 0u,
                    _environmentManager?.RequestedSpecularEnvironmentGeneration ?? 0u,
                    _environmentManager?.PublishedSpecularEnvironmentGeneration ?? 0u,
                    _environmentManager?.RequestedGiLightingGeneration ?? 0u,
                    _environmentManager?.GiLightingGeneration ?? 0u,
                    completedSourceGeneration,
                    convergedGeneration,
                    _performanceCaptureMetadataProvider.ObservedSceneRevision ==
                    ulong.MaxValue
                        ? 0UL
                        : _performanceCaptureMetadataProvider
                            .ObservedSceneRevision);
            }
        }

        private bool MeshletDiagnosticCountersActive => _meshPipeline?.GpuMeshletCountersEnabled == true;

        public SelectedObjectInspection? SelectedObject
        {
            get => TryInspectObject(Settings.Debug.SelectedObjectIndex, out SelectedObjectInspection inspection)
                ? inspection
                : null;
            set => Settings.Debug.SelectedObjectIndex = value?.ObjectIndex ?? -1;
        }

        public bool EnableHiZOcclusion { get; set; } = true;
        public bool EnableAdaptiveHiZOcclusion { get; set; } = true;

        /// <summary>
        /// Forward+ always requires a populated depth prepass. The setter remains only as a
        /// source-compatible migration aid for callers compiled against earlier releases.
        /// </summary>
        [Obsolete("The production Forward+ renderer always runs the depth prepass. Disabling it is unsupported.")]
        public bool EnableDepthPrePass
        {
            get => true;
            set
            {
                if (!value)
                {
                    throw new NotSupportedException(
                        "Disabling the depth prepass is unsupported by the production Forward+ renderer. " +
                        "Forward+, tiled light culling, and depth-dependent effects require current-frame prepass depth.");
                }
            }
        }

        public bool EnableTransparentPass { get; set; } = true;
        public bool EnableMeshletDebugView { get; set; }
        public RenderSettings Settings { get; }

        public AdvancedGiRuntimeContentState AdvancedGiRuntimeContentState =>
            _advancedGiAdmission.RuntimeContentState;

        public string AdvancedGiCandidateProfileStatus =>
            _advancedGiAdmission.CandidateProfileStatus;

        /// <summary>
        /// Installs the independently reviewed prerequisite evidence used when
        /// the renderer is next initialized. Advanced graph variants are
        /// constructed transactionally, therefore changing this evidence on a
        /// live renderer requires a normal renderer restart rather than a
        /// partial descriptor/pass transition.
        /// </summary>
        public void ConfigureAdvancedGiPrerequisiteManifest(
            AdvancedGiPrerequisiteManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI prerequisite evidence can only change before renderer initialization; restart to rebuild graph resources transactionally.");
            _advancedGiAdmission.ConfigurePrerequisiteManifest(manifest);
        }

        /// <summary>
        /// Installs the expected corpus/profile/scene identity before renderer
        /// initialization. An absent or invalid binding is accepted as the
        /// fail-closed state so explicit experiment modes remain usable while
        /// AutoQualified selections are rejected.
        /// </summary>
        public void ConfigureAdvancedGiRuntimeContentBinding(
            in AdvancedGiRuntimeContentBinding binding)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI runtime content binding can only change before renderer initialization; restart to select another qualified profile.");
            _advancedGiAdmission.ConfigureRuntimeContentBinding(binding);
        }

        /// <summary>
        /// Loads Phase 0 evidence before initialization.  A missing, malformed,
        /// or incomplete file deliberately installs an empty manifest and
        /// returns <see langword="false"/>; callers may surface the detail in
        /// their launch report, but the renderer continues on canonical GI.
        /// </summary>
        public bool TryConfigureAdvancedGiPrerequisiteManifestFile(
            string path,
            out string failureDetail)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI prerequisite evidence can only change before renderer initialization; restart to rebuild graph resources transactionally.");

            if (!AdvancedGiPrerequisiteManifestCodec.TryLoad(
                    path,
                    out AdvancedGiPrerequisiteManifest manifest,
                    out failureDetail))
            {
                _advancedGiAdmission.ResetPrerequisiteManifest();
                return false;
            }

            _advancedGiAdmission.ConfigurePrerequisiteManifest(manifest);
            return true;
        }

        /// <summary>
        /// Installs a previously load-authenticated feature qualification set. The immutable graph
        /// inventory and optional native backends are selected during initialization, so changing
        /// qualification evidence on a live renderer requires a normal restart.
        /// </summary>
        public void ConfigureAdvancedGiQualificationManifest(
            AdvancedGiQualificationManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI qualification evidence can only change before renderer initialization; restart to rebuild graph resources transactionally.");
            _advancedGiAdmission.ConfigureQualificationManifest(manifest);
        }

        public void ConfigureDirectionalShadowQualificationManifest(
            DirectionalShadowQualificationManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            _lifetime.ThrowIfInitializationSucceeded(
                "Directional-shadow qualification evidence can only change before renderer initialization.");
            _directionalShadowQualificationManifest = manifest;
        }

        /// <summary>
        /// Installs the independently measured C4 artifact and the exact
        /// bounded world-cache/screen profile for the next initialization.
        /// The profile is recompiled against the selected device and scene
        /// extent before the graph can expose any C4 resource.
        /// </summary>
        public void ConfigureGiCausticEvidence(
            in GiCausticQualificationEvidence evidence,
            in GiCausticAdmissionContext admissionContext,
            in GiTaggedCausticCacheConfiguration configuration)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "C4 evidence can only change before renderer initialization; restart to rebuild its immutable graph variant.");
            _giCaustic.ConfigureEvidence(
                evidence,
                admissionContext,
                configuration);
            _advancedGiAdmission.ConfigureGiCausticEvidence(
                evidence,
                admissionContext);
        }

        /// <summary>
        /// Installs the immutable C5 measure-before-build result for the next
        /// renderer initialization. The evidence is revalidated against the
        /// selected device, exact scene extent, source ABI/profile, B3
        /// identity, and compiled byte layout before any C5 graph resource is
        /// registered.
        /// </summary>
        public void ConfigureSimpleDdgiNearFieldResidualEvidence(
            in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
            in SimpleDdgiNearFieldResidualAdmissionContext admissionContext)
        {
            if (!TryResolveNearFieldEvidenceProfile(
                    evidence.Binding.ProfileFingerprint,
                    out SimpleDdgiNearFieldResidualProfile profile))
            {
                throw new ArgumentException(
                    "C5 evidence does not bind one of the production graph profiles.",
                    nameof(evidence));
            }

            ConfigureSimpleDdgiNearFieldResidualEvidence(
                evidence, admissionContext, profile);
        }

        /// <summary>
        /// Installs evidence together with the exact measured production
        /// profile. AutoQualified may use half, quarter, or eighth resolution;
        /// explicit HiZAdaptive starts at quarter resolution while the durable
        /// fixed mode executes at its admitted profile resolution.
        /// </summary>
        public void ConfigureSimpleDdgiNearFieldResidualEvidence(
            in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
            in SimpleDdgiNearFieldResidualAdmissionContext admissionContext,
            in SimpleDdgiNearFieldResidualProfile profile)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "C5 evidence can only change before renderer initialization; restart to rebuild its immutable graph variant.");
            if (!admissionContext.TryValidate(out string contextFailure))
                throw new ArgumentException(contextFailure, nameof(admissionContext));
            if (!evidence.HasEvidenceId || !evidence.Binding.IsValid)
            {
                throw new ArgumentException(
                    "C5 evidence requires a valid ID and complete immutable binding.",
                    nameof(evidence));
            }

            AdvancedGiNearFieldGraphProfile graphProfile =
                AdvancedGiNearFieldGraphProfile.From(profile);
            if (!graphProfile.IsSupported ||
                evidence.Binding.ProfileFingerprint !=
                SimpleDdgiNearFieldResidualEvidenceEvaluator
                    .ComputeProfileFingerprint(profile))
            {
                throw new ArgumentException(
                    "C5 evidence/profile fingerprint is unsupported or mismatched.",
                    nameof(profile));
            }

            _nearFieldResidual.ConfigureEvidenceProfile(
                profile,
                evidence.Binding.Tier);
            _advancedGiAdmission.ConfigureNearFieldResidualEvidence(
                evidence,
                admissionContext);
        }

        private static bool TryResolveNearFieldEvidenceProfile(
            ulong fingerprint,
            out SimpleDdgiNearFieldResidualProfile profile)
        {
            SimpleDdgiNearFieldResidualProfile[] candidates =
            [
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Performance, 0.125f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Performance, 0.25f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Balanced, 0.125f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Balanced, 0.25f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Balanced, 0.5f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Quality, 0.125f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Quality, 0.25f),
                SimpleDdgiNearFieldResidualProfile.ForPreset(
                    SimpleDdgiNearFieldResidualQualityPreset.Quality, 0.5f)
            ];
            foreach (SimpleDdgiNearFieldResidualProfile candidate in candidates)
            {
                if (fingerprint == SimpleDdgiNearFieldResidualEvidenceEvaluator
                        .ComputeProfileFingerprint(candidate))
                {
                    profile = candidate;
                    return true;
                }
            }

            profile = default;
            return false;
        }

        /// <summary>
        /// Loads and authenticates the complete AutoQualified evidence set before initialization.
        /// Any missing report, stale hash, unknown JSON member, device-matrix gap, or failed
        /// promotion floor installs the empty fail-closed set atomically.
        /// </summary>
        public bool TryConfigureAdvancedGiQualificationManifestFile(
            string path,
            out string failureDetail)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI qualification evidence can only change before renderer initialization; restart to rebuild graph resources transactionally.");

            if (!AdvancedGiQualificationManifestCodec.TryLoad(
                    path,
                    out AdvancedGiQualificationManifest manifest,
                    out failureDetail))
            {
                _advancedGiAdmission.ResetQualificationManifest();
                return false;
            }

            _advancedGiAdmission.ConfigureQualificationManifest(manifest);
            return true;
        }

        public bool TryConfigureDirectionalShadowQualificationManifestFile(
            string path,
            out string failureDetail)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Directional-shadow qualification evidence can only change before renderer initialization.");
            if (!DirectionalShadowQualificationManifestCodec.TryLoad(
                    path,
                    out DirectionalShadowQualificationManifest manifest,
                    out failureDetail))
            {
                _directionalShadowQualificationManifest =
                    DirectionalShadowQualificationManifest.Empty;
                return false;
            }

            _directionalShadowQualificationManifest = manifest;
            return true;
        }

        public bool TryConfigureAdvancedGiCandidateProfileFile(
            string path,
            out string failureDetail)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI candidate authorization can only change before renderer initialization; restart to rebuild graph resources transactionally.");
            if (!AdvancedGiCandidateProfileCodec.TryLoad(
                    path,
                    out AdvancedGiCandidateProfileDocument? profile,
                    out failureDetail) || profile is null)
            {
                _advancedGiAdmission.RejectCandidateProfile(failureDetail);
                return false;
            }

            _advancedGiAdmission.ConfigureCandidateProfile(profile);
            return true;
        }

        /// <summary>
        /// Atomically loads the exact scene/layout-bound C4 and C5 evidence
        /// used by the next initialization. A malformed or stale bundle clears
        /// both feature-specific records, so a partially accepted file cannot
        /// leave one experimental graph branch enabled.
        /// </summary>
        public bool TryConfigureAdvancedGiRuntimeEvidenceBundleFile(
            string path,
            out string failureDetail)
        {
            _lifetime.ThrowIfInitializationSucceeded(
                "Advanced GI runtime evidence can only change before renderer initialization; restart to rebuild graph resources transactionally.");

            if (!AdvancedGiRuntimeEvidenceBundleCodec.TryLoad(
                    path,
                    out AdvancedGiRuntimeEvidenceBundleDocument bundle,
                    out failureDetail))
            {
                ClearAdvancedGiRuntimeEvidence();
                return false;
            }

            try
            {
                // The codec has already compiled both plans. Reuse the public
                // setters here so renderer-owned budget/profile invariants stay
                // centralized and cannot drift from programmatic startup.
                ClearAdvancedGiRuntimeEvidence();
                if (bundle.Caustics is { } caustics)
                {
                    ConfigureGiCausticEvidence(
                        caustics.Evidence,
                        caustics.AdmissionContext,
                        caustics.Configuration);
                }

                if (TrySelectNearFieldRuntimeEvidence(
                        bundle.NearFieldResiduals,
                        out SimpleDdgiNearFieldResidualRuntimeEvidenceDocument?
                            nearField,
                        out SimpleDdgiNearFieldResidualExecutionScale
                            startupScale))
                {
                    ConfigureSimpleDdgiNearFieldResidualEvidence(
                        nearField!.Evidence,
                        nearField.AdmissionContext,
                        nearField.Configuration.Profile);
                    _nearFieldResidual.SetStartupScale(startupScale);
                }

                failureDetail = "valid";
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or OverflowException)
            {
                ClearAdvancedGiRuntimeEvidence();
                failureDetail =
                    "advanced-gi-runtime-evidence-bundle-renderer-rejected:" +
                    exception.Message;
                return false;
            }
        }

        private void ClearAdvancedGiRuntimeEvidence()
        {
            _giCaustic.ClearConfiguredEvidence();
            _advancedGiAdmission.ClearRuntimeEvidence();
            _nearFieldResidual.ClearConfiguredEvidence();
        }

        /// <summary>
        /// Optional application-supplied scene-kind identifier included in performance
        /// captures. When it is absent, the renderer falls back to the scene object's name.
        /// </summary>
        public string CaptureSceneKind
        {
            get => _performanceCaptureMetadataProvider.SceneKind;
            set => _performanceCaptureMetadataProvider.SceneKind = value;
        }

        /// <summary>
        /// Optional application-supplied active scenario identifier included in performance
        /// captures. Set this before drawing the scenario; when it is not supplied, captures
        /// deliberately report that the scenario is unavailable instead of inferring one from
        /// scene content or a camera pose.
        /// </summary>
        public string CaptureScenario
        {
            get => _performanceCaptureMetadataProvider.Scenario;
            set => _performanceCaptureMetadataProvider.Scenario = value;
        }

        public int DebugObjectSnapshotCount => _lastSceneData?.ObjectDebugSnapshots.Count ?? 0;

        public ScreenshotCaptureAnalysis LastScreenshotCaptureAnalysis =>
            _screenshotCaptureService.LastCaptureAnalysis;

        public void QueueOverlayDrawData(OverlayDrawData? drawData)
        {
            _lifetime.ThrowIfDisposalStarted();
            _overlayDrawData.Set(drawData);
        }

        public int CreateOverlayTexture(ReadOnlySpan<byte> pixels, uint width, uint height, string? name = null)
        {
            _lifetime.ThrowIfDisposalStarted();
            TextureHandle handle = _textureManager.CreateTexture(width, height, Format.R8G8B8A8Unorm,
                debugName: name ?? "Overlay Texture");
            try
            {
                _textureManager.UploadTextureData(handle, pixels, width, height, Format.R8G8B8A8Unorm);
                return _textureManager.GetBindlessTextureIndex(handle);
            }
            catch
            {
                _textureManager.DestroyTexture(handle);
                throw;
            }
        }

        public void RequestScreenshot(string? outputPath = null)
        {
            _lifetime.ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowScreenshots)
                return;

            _screenshotCaptureService.Request(outputPath);
        }

        /// <summary>
        /// Queues one lossless, pre-exposure SceneColor capture. The request is
        /// recorded after diagnostic rendering and completed only after the
        /// matching in-flight frame fence. This API is intentionally guarded by
        /// the same explicit debug screenshot permission as final-LDR capture.
        /// </summary>
        public bool RequestLinearHdrCapture(string outputPath)
        {
            return RequestLinearHdrCapture(outputPath, string.Empty);
        }

        /// <summary>
        /// Queues a lossless SceneColor capture carrying a caller-authored
        /// attestation token through the exact submitted DDGI frame serial.
        /// </summary>
        public bool RequestLinearHdrCapture(
            string outputPath,
            string captureToken)
        {
            _lifetime.ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowScreenshots)
                return false;

            _linearHdrCaptureService.Request(outputPath, captureToken);
            return true;
        }

        public LinearHdrCaptureResult GetLinearHdrCaptureResult(string outputPath)
        {
            _lifetime.ThrowIfDisposalStarted();
            return _linearHdrCaptureService.GetResult(outputPath);
        }

        /// <summary>
        /// Explicitly requests a local-probe recapture. The result is the
        /// immediate CPU scheduler admission; normal frame telemetry owns all
        /// later GPU-work and completion evidence.
        /// </summary>
        public ReflectionProbeRecaptureRequestSummary
            RequestReflectionProbeRecapture(string reason)
        {
            _lifetime.ThrowIfDisposalStarted();
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "A reflection recapture reason is required.",
                    nameof(reason));
            return _reflectionProbeManager?.RequestRecaptureAllWithSummary(
                       reason.Trim()) ??
                   ReflectionProbeRecaptureRequestSummary.Empty;
        }

        /// <summary>
        /// Predicts the next frame's near-ring motion from the live DDGI anchor.
        /// This is intentionally read-only and exists so deterministic tooling
        /// can queue a present-delimited capture before the recenter begins.
        /// </summary>
        public bool WouldSimpleDdgiNearRingRecenter(
            Vector3 cameraPosition,
            Vector3 cameraForward) =>
            _simpleDdgiVolumeManager?.WouldRecenterNearRing(
                cameraPosition,
                cameraForward) ?? false;

        public void RequestRenderDocCapture()
        {
            _lifetime.ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowRenderDocCapture)
                return;

            _renderDocCaptureService.RequestCapture();
            if (_renderDocCaptureService.CaptureRequested)
                Settings.Diagnostics.GpuMeshletCountersEnabled = true;
        }

        public string ExportPerformanceSnapshot(string? directory = null)
        {
            _lifetime.ThrowIfDisposalStarted();
            string targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(AppContext.BaseDirectory, "PerformanceSnapshots")
                : directory;

            return new PerformanceSnapshotWriter().Write(targetDirectory, _lastDiagnostics, _lastBudgetSnapshot);
        }

        public bool TryFindObjectByName(string name, out int objectIndex)
        {
            _lifetime.ThrowIfDisposalStarted();
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            SceneRenderingData? sceneData = _lastSceneData;
            if (sceneData == null || !sceneData.HasCpuSnapshots)
            {
                objectIndex = -1;
                return false;
            }

            for (int i = 0; i < sceneData.ObjectDebugSnapshots.Count; i++)
            {
                ObjectDebugSnapshot snapshot = sceneData.ObjectDebugSnapshots[i];
                if (string.Equals(snapshot.Name, name, StringComparison.Ordinal))
                {
                    objectIndex = snapshot.ObjectIndex;
                    return true;
                }
            }

            objectIndex = -1;
            return false;
        }

        public bool TryFindObjectById(Guid id, out int objectIndex)
        {
            _lifetime.ThrowIfDisposalStarted();
            SceneRenderingData? data = _lastSceneData;
            if (id != Guid.Empty && data?.HasCpuSnapshots == true)
                foreach (ObjectDebugSnapshot snapshot in data.ObjectDebugSnapshots)
                    if (snapshot.EntityId == id)
                    {
                        objectIndex = snapshot.ObjectIndex;
                        return true;
                    }

            objectIndex = -1;
            return false;
        }

        public bool TryInspectObject(int index, out SelectedObjectInspection inspection)
        {
            _lifetime.ThrowIfDisposalStarted();
            SceneRenderingData? sceneData = _lastSceneData;
            if (index < 0 || sceneData == null || !sceneData.HasCpuSnapshots ||
                index >= sceneData.ObjectDebugSnapshots.Count)
            {
                inspection = null!;
                return false;
            }

            ObjectDebugSnapshot snapshot = sceneData.ObjectDebugSnapshots[index];
            try
            {
                GPUMaterialData material = _materialManager.GetMaterialData(snapshot.Material);
                inspection = new SelectedObjectInspection(
                    snapshot.ObjectIndex,
                    snapshot.Name,
                    snapshot.Mesh,
                    snapshot.Material,
                    snapshot.WorldBounds,
                    snapshot.Visible,
                    snapshot.CpuCulled,
                    MaterialInspectionResult.FromGpuMaterial(snapshot.Material.Index, material));
                return true;
            }
            catch (InvalidOperationException)
            {
                inspection = null!;
                return false;
            }
        }

        public VulkanRenderer(
            IWindow window,
            VulkanContext context,
            SwapchainManager swapchainManager,
            SynchronizationManager syncManager,
            CommandBufferManager cmdManager,
            BufferManager bufferManager,
            TextureManager textureManager,
            MeshManager meshManager,
            MaterialManager materialManager,
            LightManager lightManager,
            BindlessHeap bindlessHeap,
            RenderGraph renderGraph,
            SceneDataBuilder sceneDataBuilder,
            StagingRing stagingRing,
            FenceBasedDeleter deleter,
            IModelRenderUploadService modelUploadService)
            : this(
                window,
                context,
                swapchainManager,
                syncManager,
                cmdManager,
                bufferManager,
                textureManager,
                meshManager,
                materialManager,
                lightManager,
                bindlessHeap,
                renderGraph,
                sceneDataBuilder,
                stagingRing,
                deleter,
                modelUploadService,
                ownsDependencies: true)
        {
        }

        internal VulkanRenderer(
            IWindow window,
            VulkanContext context,
            SwapchainManager swapchainManager,
            SynchronizationManager syncManager,
            CommandBufferManager cmdManager,
            BufferManager bufferManager,
            TextureManager textureManager,
            MeshManager meshManager,
            MaterialManager materialManager,
            LightManager lightManager,
            BindlessHeap bindlessHeap,
            RenderGraph renderGraph,
            SceneDataBuilder sceneDataBuilder,
            StagingRing stagingRing,
            FenceBasedDeleter deleter,
            IModelRenderUploadService modelUploadService,
            bool ownsDependencies,
            RenderSettings? initialSettings = null,
            RendererStartupLog? startupLog = null,
            VulkanMeshletPhysicalResidencyResources?
                meshletPhysicalResidencyResources = null)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _startupLog = startupLog;
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _swapchain = swapchainManager ?? throw new ArgumentNullException(nameof(swapchainManager));
            _sync = syncManager ?? throw new ArgumentNullException(nameof(syncManager));
            _submissionOwnership = new FrameSubmissionOwnershipTracker(
                FramesInFlight,
                SynchronizationManager.ImageAvailableSemaphoreCount,
                checked((int)_swapchain.ImageCount));
            _cmd = cmdManager ?? throw new ArgumentNullException(nameof(cmdManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _debugOverlayBuilder = new DebugOverlayBuilder(
                new RendererDebugOverlayResourceLookup(
                    _meshManager,
                    _materialManager));
            _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
            _asyncComputeCoordinator = new AsyncComputeCoordinator(
                _renderGraph,
                FramesInFlight);
            _sceneDataBuilder = sceneDataBuilder ?? throw new ArgumentNullException(nameof(sceneDataBuilder));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
            _modelUploadService = modelUploadService ?? throw new ArgumentNullException(nameof(modelUploadService));
            _meshletPhysicalResidencyResources =
                meshletPhysicalResidencyResources;
            _lifetime = new RendererLifetimeCoordinator(
                nameof(VulkanRenderer),
                startupLog);
            _performanceCaptureMetadataProvider = new(
                new PerformanceCaptureHostIdentityResolver(
                    typeof(VulkanRenderer).Assembly,
                    typeof(ShaderLibrary).Assembly));
            _iesPhotometricProfileManager = new IesPhotometricProfileManager(
                _textureManager,
                _bindlessHeap);
            _lightManager.PhotometricProfiles = _iesPhotometricProfileManager;
            Settings = initialSettings ?? new RenderSettings();
            _startupLog?.PerformanceConfiguration(Settings);
            Console.WriteLine(
                "Performance optimizations: " +
                $"enabled={Settings.PerformanceOptimizations.Enabled}, " +
                $"requested={PerformanceOptimizationFeatureMask.Format(Settings.PerformanceOptimizations.EnabledFeatures)}, " +
                $"effective={PerformanceOptimizationFeatureMask.Format(Settings.EffectivePerformanceOptimizationFeatures)}, " +
                $"async={Settings.AsyncCompute.Mode}.");
            _ddgiInvalidation = new DdgiSceneInvalidationCoordinator(
                _meshManager,
                _materialManager,
                _lightManager);
            _giCaustic = new GiCausticFrameCoordinator(
                _context,
                _bufferManager,
                _stagingRing);
            _nearFieldResidual =
                new SimpleDdgiNearFieldResidualCoordinator(_context);
            Settings.QualityPresetChanging += OnQualityPresetChanging;
            OnQualityPresetChanging(Settings.QualityPreset);
            _diagnosticsBuffer = new RendererDiagnosticsBuffer(_context, _bufferManager);
            _screenshotReadbackManager = new ScreenshotReadbackManager(_bufferManager, _screenshotCaptureService);
            _linearHdrReadbackManager = new LinearHdrReadbackManager(_bufferManager, _linearHdrCaptureService);
            _gpuTimestamps = new GpuTimestampRecorder(_context);
            _particleSystemManager = new ParticleSystemManager(_context, _bufferManager, _stagingRing);
            _gpuParticleRuntimeManager = new GpuParticleRuntimeManager(_context, _bufferManager, _stagingRing);
            _foliageManager =
                new FoliageManager(
                    _context,
                    _bufferManager,
                    _stagingRing,
                    _meshManager,
                    _materialManager,
                    _textureManager);
            _ownsDependencies = ownsDependencies;
        }

        private void OnQualityPresetChanging(RenderQualityPreset preset)
        {
            _materialManager.SetPrimitiveProfileGpuBudgetBytes(
                RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(preset));
        }

        public void Initialize()
        {
            if (_lifetime.Initialize(InitializeCore))
            {
                System.Diagnostics.Debug.WriteLine(
                    "VulkanRenderer initialized.");
            }
        }

        private void InitializeCore()
        {
            long started = Stopwatch.GetTimestamp();
            lock (_startupGate)
            {
                _startupStartedTimestamp = started;
                _startupPhaseStartedTimestamp = started;
            }
            InitializeBootstrapCore();
            if (!RendererBuildConfiguration.ProgressivePipelineStartup)
                InitializeProductionResourcesCore();
        }

        private void InitializeBootstrapCore()
        {
            // A pipeline-free clear needs only the device/swapchain services
            // constructed by DI plus one render-finished semaphore per image.
            // Everything else is deliberately deferred until after that clear
            // has been presented.
            _sync.EnsureRenderFinishedSemaphoreCapacity(
                _swapchain.ImageCount);
        }

        private void InitializeProductionResourcesCore()
        {
            System.Diagnostics.Debug.WriteLine("Initializing VulkanRenderer...");

            // Auto-qualified admission is shader-bundle keyed. Resolve the effective embedded
            // bytes before any optional backend or immutable graph inventory is selected; doing
            // this later would make startup admission compare against a placeholder identity.
            _performanceCaptureMetadataProvider.ResolveStartupIdentity();
            _advancedGiAdmission.PublishSettingsFingerprint(
                AdvancedGiSettingsFingerprint.Compute(
                    Settings.GlobalIllumination));

            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool materialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            float sceneResolutionScale = ResolveSceneResolutionScale();
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, sceneResolutionScale);
            GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
            AdvancedGiPrerequisiteGateResult opacityPrerequisite =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps);
            bool opacityPreflightSupported =
                IsOpacityMicromapRuntimePreflightSupported();
            AdvancedGiQualificationGateResult opacityQualification =
                EvaluateAdvancedGiQualification(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps,
                    opacityPrerequisite,
                    opacityPreflightSupported,
                    giSettings.DdgiOpacityMicromapQualificationId);
            bool enableOpacityMicromapRuntime = opacityPreflightSupported &&
                                                (giSettings.DdgiOpacityMicromapMode ==
                                                 DdgiOpacityMicromapMode.ExtFourStateExperiment ||
                                                 giSettings.DdgiOpacityMicromapMode ==
                                                 DdgiOpacityMicromapMode.AutoQualified &&
                                                 opacityPrerequisite.Passed &&
                                                 opacityQualification.Passed);
            _accelerationStructureManager = new AccelerationStructureManager(
                _context,
                _bufferManager,
                _meshManager,
                _materialManager,
                (_modelUploadService as ModelRenderUploadService)
                ?.OpacityMicromapRegistrations,
                enableOpacityMicromapRuntime: enableOpacityMicromapRuntime);
            _raySceneDescriptorBank = new RaySceneDescriptorBank(
                _context,
                _accelerationStructureManager);
            _raySceneDescriptorBank.TryInitialize();
            _advancedGiAdmission.PublishGraphModes(
                ResolveInitialAdvancedGiGraphModes(sceneRenderExtent));
            _gtaoRuntimeSupported = EvaluateGtaoRuntimeSupport();
            RegisterGraphResources();
            AmbientOcclusionMode effectiveAmbientOcclusionMode =
                ResolveEffectiveAmbientOcclusionMode();
            bool motionVectorTargetEnabled =
                ResolveSurfaceHistoryConsumers().RequiresMotionVectors();
            bool hybridReflectionTargetEnabled =
                ResolveHybridReflectionTargetProvisioning();
            _renderTargets = new RenderTargetManager(
                _context,
                sceneRenderExtent,
                _swapchain.Extent,
                _swapchain.DepthFormat,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.AmbientOcclusion.ResolutionScale,
                Settings.AntiAliasing.EffectiveMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                IsWeightedOitTargetEnabled(Settings),
                _renderGraph,
                materialTransportProvenanceTargetEnabled,
                nearFieldResidualEnabled:
                _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual,
                nearFieldResidualLayout:
                _nearFieldResidual.Plan.Layout,
                giCausticEnabled:
                _advancedGiAdmission.GraphModes.UsesCausticWorldCache,
                giCausticScreenLayout:
                _giCaustic.Plan.GpuLayout.ScreenResolve,
                hybridReflectionsEnabled:
                hybridReflectionTargetEnabled,
                ambientOcclusionMode: effectiveAmbientOcclusionMode);
            _lastAmbientOcclusionTargetEnabled = Settings.AmbientOcclusion.Enabled;
            _lastAmbientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;
            _lastAmbientOcclusionMode = effectiveAmbientOcclusionMode;
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastMaterialTransportProvenanceTargetEnabled =
                materialTransportProvenanceTargetEnabled;
            _lastHybridReflectionTargetEnabled =
                hybridReflectionTargetEnabled;
            _lastHybridReflectionRayBudgetFraction =
                Settings.Reflections.RayQueryPixelBudgetFraction;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Initial render targets";
            _hizDepthPyramid = new HiZDepthPyramid(_context, CreateHiZExtent(sceneRenderExtent));
            _renderGraph.RegisterImportedImageTarget(RenderGraphResourceId.HiZPyramid, _hizDepthPyramid);
            _directionalShadowResources = new DirectionalShadowResources(_context, _bufferManager, Settings.Shadows);
            _directionalShadowHistoryResources = new DirectionalShadowHistoryResources(
                _context,
                _bufferManager,
                _bindlessHeap);
            _spotShadowAtlas = new SpotShadowAtlas(_context, _bufferManager, Settings.Shadows);
            _pointShadowCubemapArray = new PointShadowCubemapArray(_context, _bufferManager, Settings.Shadows);
            _environmentManager = new EnvironmentManager(_context, _bufferManager, _textureManager, Settings);
            _reflectionProbeManager = new ReflectionProbeManager(
                _context,
                _bufferManager,
                Settings,
                _swapchain.DepthFormat);
            _automaticPlanarReflectionManager =
                new AutomaticPlanarReflectionManager(
                    _context,
                    _bufferManager,
                    _meshManager,
                    _materialManager,
                    _bindlessHeap,
                    Settings,
                    _swapchain.DepthFormat);
            // Persistent GI priors and AutoQualified admission share the exact same effective
            // shader identity resolved at the start of this initialization transaction.
            _simpleDdgiVolumeManager = new SimpleDdgiVolumeManager(
                _context,
                _bufferManager,
                Settings,
                RecordDeviceWaitIdle,
                WaitForSimpleDdgiBindlessDescriptorReaders,
                directionalGuidingTraceStagingEnabled:
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding);
            _simpleDdgiLightTreeResources = new SimpleDdgiLightTreeGpuResources(
                _context,
                _bufferManager,
                _lightManager,
                Settings,
                () => { WaitForSimpleDdgiBindlessDescriptorReaders(); });
            _advancedGiTransientBufferArena =
                new AdvancedGiTransientBufferArena(
                    _bufferManager,
                    () => { WaitForSimpleDdgiBindlessDescriptorReaders(); });
            _simpleDdgiReceiverFeedback =
                new SimpleDdgiReceiverFeedbackCoordinator(
                    _context,
                    _bufferManager,
                    () => { WaitForSimpleDdgiBindlessDescriptorReaders(); },
                    _advancedGiTransientBufferArena,
                    giSettings,
                    _advancedGiAdmission.EvaluatePrerequisite(
                        AdvancedGiPrerequisiteFeature.ReceiverFeedback));
            _simpleDdgiGuidingRuntime = new SimpleDdgiGuidingVulkanRuntime(
                _context,
                _bufferManager,
                () => { WaitForSimpleDdgiBindlessDescriptorReaders(); },
                _advancedGiTransientBufferArena);
            _simpleDdgiGuidingSourceCacheSidecar =
                new SimpleDdgiGuidingSourceCacheSidecar(
                    _context,
                    _bufferManager,
                    () => { WaitForSimpleDdgiBindlessDescriptorReaders(); },
                    buffer => _deleter.QueueBufferDeletion(
                        _sync.GetInFlightFence(_currentFrame),
                        buffer,
                        _bufferManager));
            _simpleDdgiGuidingFrameCoordinator =
                new SimpleDdgiGuidingFrameCoordinator(
                    _context,
                    _bufferManager,
                    _stagingRing,
                    _simpleDdgiVolumeManager,
                    _advancedGiTransientBufferArena,
                    _simpleDdgiGuidingSourceCacheSidecar,
                    _simpleDdgiGuidingRuntime);
            _ddgiEmissiveTransport = new DdgiEmissiveTransportCoordinator(
                _context,
                _bufferManager,
                _meshManager,
                _materialManager);
            _ddgiFoliageProxyManager = new DdgiFoliageProxyManager(
                _context,
                _bufferManager,
                _stagingRing);
            _farFieldClipmapManager = new FarFieldClipmapManager(
                _context,
                _bufferManager,
                _deleter,
                _sync,
                Settings,
                _accelerationStructureManager,
                _materialManager);
            _simpleDdgiFrames = new SimpleDdgiFrameCoordinator(
                _context,
                _stagingRing,
                _advancedGiAdmission,
                _ddgiInvalidation,
                _ddgiEmissiveTransport,
                _simpleDdgiReceiverFeedback,
                _simpleDdgiFrameEvidence,
                _simpleDdgiVolumeManager,
                _farFieldClipmapManager,
                _advancedGiTransientBufferArena,
                _simpleDdgiGuidingSourceCacheSidecar,
                _simpleDdgiGuidingFrameCoordinator);
            _autoExposureManager = new AutoExposureManager(_context, _bufferManager, Settings);
            _skinningManager = new SkinningManager(_context, _bufferManager, _stagingRing, _meshManager);

            // Resolve the exact shader identity before any GI pipeline is
            // created so compatible driver data can be admitted up front.
            _giPipelineCacheService = new GiPipelineCacheService(
                _context,
                _performanceCaptureMetadataProvider.BuildIdentity
                    .ShaderBundleHash,
                _performanceCaptureMetadataProvider.BuildIdentity
                    .CompileConfiguration,
                cacheDirectory: null);
            _simpleDdgiReceiverFeedback.SetPipelineCacheService(
                _giPipelineCacheService);
            _simpleDdgiGuidingRuntime.SetPipelineCacheService(
                _giPipelineCacheService);
            _hybridReflectionRuntime = new HybridReflectionVulkanRuntime(
                _context,
                _bindlessHeap,
                _bufferManager,
                _renderTargets,
                Settings,
                _accelerationStructureManager,
                _raySceneDescriptorBank,
                _giPipelineCacheService);

            if (_advancedGiAdmission.GraphModes.UsesCausticWorldCache)
            {
                _giCaustic.CreateRuntime(
                    _accelerationStructureManager,
                    () => { WaitForSimpleDdgiBindlessDescriptorReaders(); },
                    _renderTargets,
                    _giPipelineCacheService);
            }

            if (_advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual)
            {
                InitializeNearFieldResidualGenerationTransaction();
            }

            if (RendererBuildConfiguration.ProgressivePipelineStartup)
            {
                // These registrations contain no graphics-pipeline work and
                // make already uploaded scene geometry available to the tiny
                // startup path before production compilation begins.
                RegisterSceneBuffers();
            }
            else
            {
                InitializeProductionPipelinesAndGraph();
                RegisterSceneBuffers();
            }
        }

        private void InitializeProductionPipelinesAndGraph()
        {
            _lifetime.RunStartupStep(
                "VulkanRenderer.CreatePipelines",
                CreatePipelines);
            _lifetime.RunStartupStep(
                "VulkanRenderer.InitializeRenderGraph",
                InitializeRenderGraph);
            Volatile.Write(ref _productionGraphReady, 1);
        }

        public void BeginProductionPreparation()
        {
            _lifetime.ThrowIfDisposalStarted();
            if (!RendererBuildConfiguration.ProgressivePipelineStartup)
                return;

            ThrowIfProgressiveStartupFaulted();
            lock (_startupGate)
            {
                if (_productionResourcesReady)
                    return;
                if (_productionPreparationStarted)
                {
                    throw new InvalidOperationException(
                        "Production preparation is already in progress.");
                }

                _productionPreparationStarted = true;
                _startupDetail =
                    "Production resources are being created before native pipeline compilation starts.";
            }

            try
            {
                // Vulkan queues and command pools require external
                // synchronization. Resource creation stays on the host's
                // device-owning thread; native pipeline creation begins on the
                // bounded worker as soon as those resources are ready.
                _lifetime.RunStartupStep(
                    "VulkanRenderer.InitializeProductionResources",
                    InitializeProductionResourcesCore);
                lock (_startupGate)
                {
                    _productionResourcesReady = true;
                    SetStartupPhaseLocked(
                        RendererStartupPhase.ProductionPreparing,
                        "Production resources are ready; visual-critical pipelines are compiling.");
                }
                StartProgressiveProductionInitialization();
            }
            catch (Exception exception)
            {
                lock (_startupGate)
                {
                    _startupFailure = exception;
                    SetStartupPhaseLocked(
                        RendererStartupPhase.Faulted,
                        $"Production preparation failed: {exception.Message}");
                }
                throw;
            }
        }

        private void StartProgressiveProductionInitialization()
        {
            lock (_startupGate)
            {
                if (_productionInitializationTask != null)
                    return;
                SetStartupPhaseLocked(
                    RendererStartupPhase.ProductionPreparing,
                    "The neutral bootstrap remains visible while production variants compile.");
                _productionInitializationTask = Task.Run(() =>
                {
                    try
                    {
                        InitializeProductionPipelinesAndGraph();
                        lock (_startupGate)
                        {
                            if (_startupPhase ==
                                RendererStartupPhase.ProductionPreparing)
                            {
                                _startupDetail =
                                    "Production graph is ready; active-scene variants are being prepared.";
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        lock (_startupGate)
                        {
                            _startupFailure = exception;
                            SetStartupPhaseLocked(
                                RendererStartupPhase.Faulted,
                                $"Progressive renderer startup failed: {exception.Message}");
                        }
                        throw;
                    }
                });
            }
        }

        private void SetStartupPhaseLocked(
            RendererStartupPhase phase,
            string detail)
        {
            _startupPhase = phase;
            _startupPhaseStartedTimestamp = Stopwatch.GetTimestamp();
            _startupDetail = detail;
        }

        private SimpleDdgiNearFieldResidualVulkanRuntime
            CreateNearFieldResidualVulkanRuntime(
                SimpleDdgiNearFieldResidualLayout layout,
                SimpleDdgiNearFieldResidualRenderTargetGeneration
                    targetGeneration)
        {
            return _nearFieldResidual.CreateRuntime(
                new NearFieldResidualRuntimeAllocationRequest(
                    _bufferManager,
                    _bindlessHeap,
                    _renderTargets ?? throw new InvalidOperationException(
                        "C5 requires render targets before runtime allocation."),
                    _hizDepthPyramid ?? throw new InvalidOperationException(
                        "C5 requires Hi-Z before runtime allocation."),
                    _foliageManager ?? throw new InvalidOperationException(
                        "C5 requires foliage state before runtime allocation.")),
                layout,
                targetGeneration,
                _giPipelineCacheService);
        }

        private void InitializeNearFieldResidualGenerationTransaction()
        {
            RenderTargetManager targets = _renderTargets ??
                                          throw new InvalidOperationException(
                                              "C5 requires render targets before generation admission.");
            var backend =
                new SimpleDdgiNearFieldResidualVulkanGenerationBackend(
                    targets,
                    CreateNearFieldResidualVulkanRuntime);
            ApplyNearFieldResidualPublication(
                _nearFieldResidual.InitializeRuntime(backend));
        }

        private void CreatePipelines()
        {
            System.Diagnostics.Debug.WriteLine("Creating pipelines...");
            bool receiverFeedbackGraphicsPipelinesRequested =
                (_simpleDdgiReceiverFeedback ??
                 throw new InvalidOperationException(
                     "The receiver-feedback coordinator must exist before pipeline creation."))
                .GraphicsPipelinesRequested;

            // Create mesh pipeline for depth prepass and forward pass
            _lifetime.RunStartupStep("Pipeline.Create.Mesh", () =>
            {
                _meshPipeline = new MeshPipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.SceneColorFormat,
                    _swapchain.DepthFormat,
                    Settings,
                    _nearFieldResidual.PipelineConfiguration,
                    _giCaustic.ReceiverPipelineConfiguration,
                    ForwardHybridReflectionReceiverPipelineConfiguration
                        .Production,
                    receiverFeedbackGraphicsPipelinesRequested,
                    _raySceneDescriptorBank,
                    _giPipelineCacheService,
                    _lifetime.RunStartupStep);
            });
            _lifetime.RunStartupStep("Pipeline.Create.Foliage", () =>
            {
                _foliagePipeline = new FoliagePipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.SceneColorFormat,
                    RenderTargetManager.MotionVectorFormat,
                    _swapchain.DepthFormat,
                    Settings,
                    receiverFeedbackGraphicsPipelinesRequested,
                    _nearFieldResidual.PipelineConfiguration,
                    _giCaustic.ReceiverPipelineConfiguration,
                    ForwardHybridReflectionReceiverPipelineConfiguration
                        .Production,
                    _giPipelineCacheService,
                    createPipelines: false);
            });

            // Create compute pipeline for light culling
            _lifetime.RunStartupStep(
                "Pipeline.Create.LightCulling",
                () => _computePipeline = new ComputePipeline(
                    _context,
                    _bindlessHeap,
                    _giPipelineCacheService));

            _lifetime.RunStartupStep("Pipeline.Create.Composite", () =>
            {
                _compositePipeline = new CompositePipeline(
                    _context,
                    _bindlessHeap,
                    _swapchain.SurfaceFormat,
                    _giPipelineCacheService);
                _ldrCompositePipeline = new CompositePipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.LdrSceneColorFormat,
                    _giPipelineCacheService);
                _weightedOitCompositePipeline = new WeightedOitCompositePipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.SceneColorFormat,
                    _giPipelineCacheService,
                    createPipeline:
                        RendererBuildConfiguration.PipelineStartupMode ==
                            RendererPipelineStartupMode.Exhaustive ||
                        Settings.Transparency.Enabled &&
                        Settings.Transparency.Mode ==
                            TransparencyMode.WeightedBlendedOit);
            });
            _lifetime.RunStartupStep("Pipeline.Create.Skybox", () =>
            {
                _skyboxPipeline = new SkyboxPipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.SceneColorFormat,
                    _swapchain.DepthFormat,
                    _giPipelineCacheService,
                    createPipeline:
                        RendererBuildConfiguration.PipelineStartupMode ==
                            RendererPipelineStartupMode.Exhaustive ||
                        Settings.Environment.Enabled);
            });
            _lifetime.RunStartupStep("Pipeline.Create.Particle", () =>
            {
                _particlePipeline = new ParticlePipeline(
                    _context,
                    _bindlessHeap,
                    RenderTargetManager.SceneColorFormat,
                    _swapchain.DepthFormat,
                    receiverFeedbackGraphicsPipelinesRequested,
                    _giPipelineCacheService,
                    createPipelines: false);
            });
            _skinningPass = new SkinningPass(
                _context,
                _bindlessHeap,
                _bufferManager,
                _skinningManager,
                _giPipelineCacheService);
            _ddgiFoliageProxyGenerationPass =
                new DdgiFoliageProxyGenerationPass(
                    _context,
                    _bindlessHeap,
                    _bufferManager,
                    _giPipelineCacheService);
            _gpuParticleResetPass =
                new GpuParticleResetPass(
                    _context,
                    _bindlessHeap,
                    _bufferManager,
                    _gpuParticleRuntimeManager,
                    _giPipelineCacheService);
            _gpuParticleSimulatePass =
                new GpuParticleSimulatePass(
                    _context,
                    _bindlessHeap,
                    _bufferManager,
                    _gpuParticleRuntimeManager,
                    _giPipelineCacheService);
            _gpuParticleSortPass =
                new GpuParticleSortPass(
                    _context,
                    _bindlessHeap,
                    _bufferManager,
                    _gpuParticleRuntimeManager,
                    _giPipelineCacheService);
            _gpuParticleResetGraphPass =
                new GpuParticleResetGraphPass(_context, _swapchain, _bindlessHeap, _gpuParticleResetPass);
            _gpuParticleSimulateGraphPass =
                new GpuParticleSimulateGraphPass(_context, _swapchain, _bindlessHeap, _gpuParticleSimulatePass);
            _gpuParticleSortGraphPass = new GpuParticleSortGraphPass(_context, _swapchain, _bindlessHeap,
                _gpuParticleSortPass, _gpuParticleRuntimeManager);
            _foliageCullPass =
                new FoliageCullPass(_context, _bindlessHeap, _bufferManager, _foliageManager, _foliagePipeline);
            _sceneOpaqueCompactionPass =
                new SceneOpaqueCompactionPass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _meshPipeline,
                    _bufferManager,
                    _deleter,
                    _sync,
                    Settings.IsPerformanceOptimizationEnabled(
                        PerformanceOptimizationFeature
                            .AsymmetricSidedDrawStreams));
            _forwardVisibilityCompactionPass = new ForwardVisibilityCompactionPass(_context, _swapchain, _bindlessHeap,
                _meshPipeline, _bufferManager);

            System.Diagnostics.Debug.WriteLine("Pipelines created.");
        }

        private void InitializeRenderGraph()
        {
            System.Diagnostics.Debug.WriteLine("Initializing render graph...");

            // The selected state is computed only from effective modes and is
            // frozen for this renderer lifetime.  A mode transition therefore
            // cannot leave a graph declaration, resource descriptor, or pass
            // instance from a previous generation alive.
            ProductionRenderPipelineDeclaration.Instance.DeclarePassResources(
                _renderGraph,
                _advancedGiAdmission.GraphModes,
                Settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled);

            var passInstances = new Dictionary<string, RenderPassBase>(StringComparer.Ordinal);

            void AddPassInstance(RenderPassBase pass)
            {
                passInstances.Add(pass.Name, pass);
            }

            AddPassInstance(_sceneOpaqueCompactionPass);

            var directionalShadowPass = new DirectionalShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _directionalShadowResources!,
                Settings.Shadows,
                _foliagePipeline,
                _bufferManager,
                _foliageManager);
            _directionalShadowPass = directionalShadowPass;
            _sceneOpaqueCompactionPass.SetDirectionalStaticShadowRefreshQuery(directionalShadowPass
                .GetStaticCacheRefreshMask);
            AddPassInstance(directionalShadowPass);

            var spotShadowPass = new SpotShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _spotShadowAtlas!,
                Settings.Shadows,
                _foliagePipeline,
                _foliageManager);
            AddPassInstance(spotShadowPass);

            var pointShadowPass = new PointShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _pointShadowCubemapArray!,
                Settings.Shadows,
                _foliagePipeline,
                _foliageManager);
            AddPassInstance(pointShadowPass);

            // Create depth pre-pass
            var depthPrePass = new DepthPrePass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!, _foliagePipeline, _bufferManager,
                _foliageManager);
            AddPassInstance(depthPrePass);

            var motionVectorPass = new MotionVectorPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _renderTargets!,
                Settings,
                _foliagePipeline,
                _bufferManager,
                _foliageManager,
                ResolveSurfaceHistoryConsumers);
            AddPassInstance(motionVectorPass);

            var hizBuildPass = new HiZBuildPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _hizDepthPyramid!,
                _renderTargets!,
                _giPipelineCacheService);
            AddPassInstance(hizBuildPass);

            var directionalRayShadowPass = new DirectionalRayShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings.Shadows,
                _bufferManager,
                _accelerationStructureManager!,
                _raySceneDescriptorBank!,
                _directionalShadowHistoryResources!,
                _giPipelineCacheService);
            _directionalRayShadowPass = directionalRayShadowPass;
            AddPassInstance(directionalRayShadowPass);

            var areaRayShadowPass = new AreaRayShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings.Shadows,
                _bufferManager,
                _accelerationStructureManager!,
                _raySceneDescriptorBank!,
                _giPipelineCacheService);
            _areaRayShadowPass = areaRayShadowPass;
            AddPassInstance(areaRayShadowPass);

            AddPassInstance(new DirectionalShadowTemporalPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                _directionalShadowHistoryResources!,
                Settings.Shadows,
                _bufferManager,
                _giPipelineCacheService));
            AddPassInstance(new DirectionalShadowSpatialPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _directionalShadowHistoryResources!,
                directionalRayShadowPass,
                Settings.Shadows,
                _bufferManager,
                _giPipelineCacheService));

            AddPassInstance(_forwardVisibilityCompactionPass);

            var ambientOcclusionPass = new AmbientOcclusionPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                _gtaoRuntimeSupported,
                _giPipelineCacheService);
            AddPassInstance(ambientOcclusionPass);

            var ambientOcclusionBlurPass = new AmbientOcclusionBlurPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                _giPipelineCacheService);
            AddPassInstance(ambientOcclusionBlurPass);

            var gtaoHistoryState = new GtaoHistoryState();
            AddPassInstance(new GtaoPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                _hizDepthPyramid!,
                Settings,
                _giPipelineCacheService));
            AddPassInstance(new GtaoTemporalPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                gtaoHistoryState,
                _giPipelineCacheService));
            AddPassInstance(new GtaoSpatialPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                _giPipelineCacheService));

            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager, _renderTargets!);
            AddPassInstance(lightCullingPass);

            AddPassInstance(new VariableRateShadingPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                HasIncompatibleVariableRateShadingForwardOutput,
                _giPipelineCacheService));

            AddPassInstance(new EnvironmentPrefilterPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _environmentManager!,
                _giPipelineCacheService));

            AddPassInstance(new SimpleDdgiLightTreePass(
                _context,
                _swapchain,
                _bindlessHeap,
                _simpleDdgiLightTreeResources!,
                _giPipelineCacheService));

            // Create forward+ rendering pass
            ForwardNearFieldDirectSourceAttachmentBinding?
                nearFieldDirectSourceBinding =
                    _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual
                        ? new ForwardNearFieldDirectSourceAttachmentBinding(
                            _renderTargets!.NearFieldDirectSource!,
                            _renderTargets.NearFieldReceiverPayload!,
                            _renderTargets.NearFieldTraceRasterDepth,
                            _nearFieldResidual.PipelineConfiguration)
                        : null;
            ForwardGiCausticReceiverAttachmentBinding?
                giCausticReceiverBinding =
                    _advancedGiAdmission.GraphModes.UsesCausticWorldCache
                        ? new ForwardGiCausticReceiverAttachmentBinding(
                            _renderTargets!.GiCausticReceiverPayload!,
                            _giCaustic.ReceiverPipelineConfiguration)
                        : null;
            var hybridReflectionReceiverBinding =
                new ForwardHybridReflectionReceiverAttachmentBinding(
                    _renderTargets!.HybridReflectionReceiverPayload!,
                    _renderTargets.HybridReflectionRawMetadata!,
                    ForwardHybridReflectionReceiverPipelineConfiguration
                        .Production);
            var forwardPass = new ForwardPlusPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _renderTargets!,
                Settings,
                _foliagePipeline,
                _bufferManager,
                _foliageManager,
                _skyboxPipeline,
                _giPipelineCacheService,
                nearFieldDirectSourceBinding,
                nearFieldDirectSourceRuntimeAvailable: () =>
                    _nearFieldResidual.IsGenerationExecutable,
                giCausticReceiverBinding: giCausticReceiverBinding,
                giCausticRuntimeAvailable: () =>
                    _giCaustic.FrameAvailable,
                hybridReflectionReceiverBinding:
                hybridReflectionReceiverBinding,
                simpleDdgiReceiverFeedbackRuntime:
                _simpleDdgiReceiverFeedback,
                nearFieldDirectSourceExecutionExtent: () =>
                    _nearFieldResidual.CaptureGraphResources().Runtime?
                        .ExecutionExtent ?? default);
            _forwardPlusPass = forwardPass;
            AddPassInstance(forwardPass);

            if (_advancedGiAdmission.GraphModes.UsesDirectionalGuiding)
            {
                SimpleDdgiGuidingFrameCoordinator guidingCoordinator =
                    _simpleDdgiGuidingFrameCoordinator ??
                    throw new InvalidOperationException(
                        "The admitted C3 graph has no frame coordinator.");
                AddPassInstance(new SimpleDdgiGuidingSampleGraphPass(
                    _context, _swapchain, _bindlessHeap, guidingCoordinator));
                AddPassInstance(new SimpleDdgiGuidingTrainGraphPass(
                    _context, _swapchain, _bindlessHeap, guidingCoordinator));
                AddPassInstance(new SimpleDdgiGuidingBuildGraphPass(
                    _context, _swapchain, _bindlessHeap, guidingCoordinator));
                AddPassInstance(new SimpleDdgiGuidingValidateGraphPass(
                    _context, _swapchain, _bindlessHeap, guidingCoordinator));
            }

            if (_advancedGiAdmission.GraphModes.UsesCausticWorldCache)
            {
                GiCausticVulkanRuntime causticRuntime =
                    _giCaustic.CaptureGraphResources().Runtime ??
                    throw new InvalidOperationException(
                        "The admitted C4 graph has no concrete Vulkan runtime.");
                AddPassInstance(new GiCausticTaskGraphPass(
                    _context, _swapchain, _bindlessHeap, causticRuntime));
                AddPassInstance(new GiCausticTraceGraphPass(
                    _context, _swapchain, _bindlessHeap, causticRuntime));
                AddPassInstance(new GiCausticCacheBuildGraphPass(
                    _context, _swapchain, _bindlessHeap, causticRuntime));
                AddPassInstance(new GiCausticResolveGraphPass(
                    _context, _swapchain, _bindlessHeap, causticRuntime));
                AddPassInstance(new GiCausticCompositeGraphPass(
                    _context, _swapchain, _bindlessHeap, causticRuntime));
            }

            if (_advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual)
            {
                NearFieldResidualGraphResourceSnapshot nearFieldResources =
                    _nearFieldResidual.CaptureGraphResources();
                if (nearFieldResources.Runtime is null)
                    throw new InvalidOperationException(
                        "The admitted C5 graph has no concrete Vulkan runtime.");
                Func<SimpleDdgiNearFieldResidualVulkanRuntime?>
                    nearFieldRuntimeProvider = () =>
                        _nearFieldResidual.CaptureGraphResources().Runtime;
                AddPassInstance(new SimpleDdgiNearFieldResidualResetPass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualPreparePass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualClassifyPass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualTracePass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualTemporalPass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualFinalizePass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
                for (int iteration = 0;
                     iteration < _nearFieldResidual.Plan.Layout
                         .FilterIterationCount;
                     iteration++)
                {
                    AddPassInstance(new SimpleDdgiNearFieldResidualFilterPass(
                        _context,
                        _swapchain,
                        _bindlessHeap,
                        nearFieldRuntimeProvider,
                        iteration));
                }

                AddPassInstance(
                    new SimpleDdgiNearFieldResidualFrequencySeparationPass(
                        _context, _swapchain, _bindlessHeap,
                        nearFieldRuntimeProvider));
                AddPassInstance(new SimpleDdgiNearFieldResidualCompositePass(
                    _context, _swapchain, _bindlessHeap,
                    nearFieldRuntimeProvider));
            }

            var simpleDdgiPageDemandPass = new SimpleDdgiPageDemandPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _renderTargets!,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiPageDemandPass = simpleDdgiPageDemandPass;
            AddPassInstance(simpleDdgiPageDemandPass);

            var simpleDdgiPageResidencyPass = new SimpleDdgiPageResidencyPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiPageResidencyPass = simpleDdgiPageResidencyPass;
            AddPassInstance(simpleDdgiPageResidencyPass);

            // Reflection work is graphics-only and is recorded after the main graph has consumed
            // the old published layer. It has its own conditional pass trio because the graph's
            // fixed production order remains the latency-critical main-view contract.
            _reflectionProbeCapturePass = new ReflectionProbeCapturePass(
                _context,
                _swapchain,
                _bindlessHeap,
                _reflectionProbeManager!,
                Settings.Reflections,
                new ForwardPlusReflectionProbeCaptureSceneRenderer(forwardPass));
            _reflectionProbePrefilterPass = new ReflectionProbePrefilterPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _reflectionProbeManager!,
                Settings.Reflections,
                _giPipelineCacheService);
            _reflectionProbePublishPass = new ReflectionProbePublishPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _reflectionProbeManager!,
                Settings.Reflections,
                _reflectionProbeCompletionValues);
            _lifetime.RunStartupStep(
                "RenderPass.Initialize.ReflectionProbeCapturePass",
                _reflectionProbeCapturePass.Initialize);
            _lifetime.RunStartupStep(
                "RenderPass.Initialize.ReflectionProbePrefilterPass",
                _reflectionProbePrefilterPass.Initialize);
            _lifetime.RunStartupStep(
                "RenderPass.Initialize.ReflectionProbePublishPass",
                _reflectionProbePublishPass.Initialize);

            var farFieldClipmapBakePass = new FarFieldClipmapBakePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _farFieldClipmapManager!,
                _giPipelineCacheService);
            AddPassInstance(farFieldClipmapBakePass);

            var simpleDdgiSchedulePass = new SimpleDdgiSchedulePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiSchedulePass = simpleDdgiSchedulePass;
            AddPassInstance(simpleDdgiSchedulePass);

            var simpleDdgiTracePass = new SimpleDdgiTracePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!,
                _accelerationStructureManager!,
                _simpleDdgiLightTreeResources!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiTracePass = simpleDdgiTracePass;
            AddPassInstance(simpleDdgiTracePass);

            var simpleDdgiRelocateClassifyPass = new SimpleDdgiRelocateClassifyPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiRelocateClassifyPass = simpleDdgiRelocateClassifyPass;
            AddPassInstance(simpleDdgiRelocateClassifyPass);

            var simpleDdgiAcceleratedSolvePass = new SimpleDdgiAcceleratedSolvePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiAcceleratedSolvePass = simpleDdgiAcceleratedSolvePass;
            AddPassInstance(simpleDdgiAcceleratedSolvePass);

            var simpleDdgiTransportPass = new SimpleDdgiTransportPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiTransportPass = simpleDdgiTransportPass;
            AddPassInstance(simpleDdgiTransportPass);

            var simpleDdgiBlendPass = new SimpleDdgiBlendPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiBlendPass = simpleDdgiBlendPass;
            AddPassInstance(simpleDdgiBlendPass);

            var simpleDdgiDirectionalRadiancePass =
                new SimpleDdgiDirectionalRadiancePass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    Settings,
                    _simpleDdgiVolumeManager!,
                    _farFieldClipmapManager!,
                    _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                    _giPipelineCacheService);
            _simpleDdgiDirectionalRadiancePass =
                simpleDdgiDirectionalRadiancePass;
            AddPassInstance(simpleDdgiDirectionalRadiancePass);

            var simpleDdgiPublishPass = new SimpleDdgiPublishPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiPublishPass = simpleDdgiPublishPass;
            AddPassInstance(simpleDdgiPublishPass);

            var simpleDdgiTransportAuditPass = new SimpleDdgiTransportAuditPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding,
                _giPipelineCacheService);
            _simpleDdgiTransportAuditPass = simpleDdgiTransportAuditPass;
            AddPassInstance(simpleDdgiTransportAuditPass);

            var simpleDdgiSchedulerCommitPass = new SimpleDdgiSchedulerCommitPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiSchedulerCommitPass = simpleDdgiSchedulerCommitPass;
            AddPassInstance(simpleDdgiSchedulerCommitPass);

            AddPassInstance(new SimpleDdgiUrgentRelightPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                simpleDdgiSchedulePass,
                simpleDdgiTracePass,
                simpleDdgiRelocateClassifyPass,
                simpleDdgiDirectionalRadiancePass,
                simpleDdgiAcceleratedSolvePass,
                simpleDdgiTransportPass,
                simpleDdgiBlendPass,
                simpleDdgiPublishPass,
                simpleDdgiSchedulerCommitPass));

            var simpleDdgiPageFeedbackPass = new SimpleDdgiPageFeedbackPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _giPipelineCacheService);
            _simpleDdgiPageFeedbackPass = simpleDdgiPageFeedbackPass;
            AddPassInstance(simpleDdgiPageFeedbackPass);

            var skyboxPass = new SkyboxPass(
                _context, _swapchain, _bindlessHeap, _skyboxPipeline, _renderTargets!, Settings);
            AddPassInstance(skyboxPass);

            AddPassInstance(new AutomaticPlanarReflectionPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _automaticPlanarReflectionManager,
                forwardPass,
                _giPipelineCacheService));

            HybridReflectionVulkanRuntime hybridReflectionRuntime =
                _hybridReflectionRuntime ?? throw new InvalidOperationException(
                    "The hybrid reflection graph requires its shared runtime.");
            AddPassInstance(new HybridReflectionDdgiBasePass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionSsrPass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionRayQueryPass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionResolvePass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionTemporalPass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionSpatialPass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new HybridReflectionCompositePass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));
            AddPassInstance(new OpaqueSceneColorSnapshotPass(
                _context, _swapchain, _bindlessHeap,
                hybridReflectionRuntime));

            var transparentForwardPass = new TransparentForwardPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _renderTargets!,
                forwardPass,
                _raySceneDescriptorBank,
                _simpleDdgiReceiverFeedback);
            AddPassInstance(transparentForwardPass);

            var weightedTransparentPass = new WeightedTransparentPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _renderTargets!,
                forwardPass,
                _raySceneDescriptorBank,
                _simpleDdgiReceiverFeedback);
            AddPassInstance(weightedTransparentPass);

            var weightedOitCompositePass = new WeightedOitCompositePass(
                _context, _swapchain, _bindlessHeap, _weightedOitCompositePipeline, _renderTargets!);
            AddPassInstance(weightedOitCompositePass);

            var particlePass = new ParticlePass(
                _context,
                _swapchain,
                _bindlessHeap,
                _particlePipeline,
                _bufferManager,
                _renderTargets!,
                Settings.Particles,
                _simpleDdgiReceiverFeedback);
            AddPassInstance(_gpuParticleResetGraphPass);
            AddPassInstance(_gpuParticleSimulateGraphPass);
            AddPassInstance(_gpuParticleSortGraphPass);
            AddPassInstance(particlePass);

            var simpleDdgiProbeDebugPass = new SimpleDdgiProbeDebugPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _bufferManager,
                _stagingRing,
                _renderTargets!,
                _giPipelineCacheService);
            AddPassInstance(simpleDdgiProbeDebugPass);

            var debugDrawPass = new DebugDrawPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _bufferManager,
                _stagingRing,
                _renderTargets!,
                _giPipelineCacheService);
            AddPassInstance(debugDrawPass);

            var debugOverlayPass = new DebugOverlayPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                _giPipelineCacheService);
            AddPassInstance(debugOverlayPass);

            var fogPass = new FogPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _bufferManager,
                _renderTargets!,
                Settings,
                _simpleDdgiVolumeManager,
                _raySceneDescriptorBank!,
                _simpleDdgiReceiverFeedback,
                _giPipelineCacheService);
            _fogPass = fogPass;
            AddPassInstance(fogPass);

            var autoExposurePass = new AutoExposurePass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                _autoExposureManager!,
                _giPipelineCacheService);
            AddPassInstance(autoExposurePass);

            var bloomPass = new BloomPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                _giPipelineCacheService);
            AddPassInstance(bloomPass);

            var toneMapCompositePass = new ToneMapCompositePass(
                _context, _swapchain, _bindlessHeap, _compositePipeline, _ldrCompositePipeline, _renderTargets!,
                Settings);
            AddPassInstance(toneMapCompositePass);

            var antiAliasingPass = new AntiAliasingPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                () => _smaaResources?.IsReady == true,
                _giPipelineCacheService);
            AddPassInstance(antiAliasingPass);
            AddPassInstance(new ImGuiRenderPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _bufferManager,
                _stagingRing,
                _overlayDrawData,
                _giPipelineCacheService));
            ProductionRenderPipelineDeclaration.Instance.RegisterPasses(
                _renderGraph,
                passInstances,
                _advancedGiAdmission.GraphModes);
            foreach (RenderPassBase asyncCandidate in passInstances.Values.Where(pass => pass.SupportsAsyncCompute))
            {
                if (!AsyncComputePassCatalog.IsProductionCandidate(asyncCandidate.Name))
                {
                    throw new InvalidOperationException(
                        $"Async-capable pass '{asyncCandidate.Name}' has no production async-compute audit classification.");
                }
            }

            ProductionRenderPipelineDeclaration.Instance.ValidatePassOrder(
                _renderGraph.PassNames,
                _advancedGiAdmission.GraphModes);

            _renderGraph.Initialize(_lifetime.RunStartupStep);
            System.Diagnostics.Debug.WriteLine("Render graph initialized.");
        }

        private bool TrySelectNearFieldRuntimeEvidence(
            IReadOnlyList<SimpleDdgiNearFieldResidualRuntimeEvidenceDocument>
                entries,
            out SimpleDdgiNearFieldResidualRuntimeEvidenceDocument? selected,
            out SimpleDdgiNearFieldResidualExecutionScale startupScale)
        {
            selected = null;
            startupScale = SimpleDdgiNearFieldResidualExecutionScale.Eighth;
            if (entries.Count == 0)
                return false;

            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(
                _context.PhysicalDevice,
                &properties);
            string shaderSetHash = _performanceCaptureMetadataProvider
                .BuildIdentity.ShaderBundleHash;
            SimpleDdgiNearFieldResidualQualityPreset requestedPreset =
                Settings.GlobalIllumination
                    .SimpleDdgiNearFieldResidualQualityPreset;

            var matched = new
                SimpleDdgiNearFieldResidualRuntimeEvidenceDocument?[3];
            foreach (SimpleDdgiNearFieldResidualRuntimeEvidenceDocument entry in
                     entries)
            {
                SimpleDdgiNearFieldResidualAdmissionContext context =
                    entry.AdmissionContext;
                SimpleDdgiNearFieldResidualQualificationEvidence evidence =
                    entry.Evidence;
                if (context.VendorId != properties.VendorID ||
                    context.DeviceId != properties.DeviceID ||
                    context.DriverVersion != properties.DriverVersion ||
                    context.ApiVersion != properties.ApiVersion ||
                    !string.Equals(
                        context.ShaderSetHash,
                        shaderSetHash,
                        StringComparison.Ordinal) ||
                    evidence.Binding.QualityPreset != requestedPreset ||
                    !evidence.SourceCostAuthoritative ||
                    evidence.Binding.ProfileFingerprint !=
                    SimpleDdgiNearFieldResidualEvidenceEvaluator
                        .ComputeProfileFingerprint(
                            ApplyNearFieldAdvancedOverrides(
                                entry.Configuration.Profile,
                                Settings.GlobalIllumination)))
                {
                    continue;
                }

                matched[(int)evidence.Binding.Tier] = entry;
            }

            int startupIndex = -1;
            for (int index = matched.Length - 1; index >= 0; index--)
            {
                if (matched[index] is { } entry &&
                    entry.Evidence.C5P95Milliseconds <=
                    SimpleDdgiNearFieldResidualEvidenceAbi
                        .MaximumStartupP95Milliseconds)
                {
                    startupIndex = index;
                    break;
                }
            }

            if (startupIndex < 0)
                return false;

            int maximumContiguousIndex = startupIndex;
            while (maximumContiguousIndex + 1 < matched.Length &&
                   matched[maximumContiguousIndex + 1] is not null)
            {
                maximumContiguousIndex++;
            }

            selected = matched[maximumContiguousIndex];
            startupScale =
                (SimpleDdgiNearFieldResidualExecutionScale)startupIndex;
            return selected is not null;
        }

        private void RegisterGraphResources()
        {
            ProductionRenderPipelineDeclaration.Instance.RegisterResources(
                _renderGraph,
                _swapchain.DepthFormat,
                _swapchain.SurfaceFormat,
                _advancedGiAdmission.GraphModes);
        }

        private AdvancedGiRenderGraphModes ResolveInitialAdvancedGiGraphModes(
            Extent2D sceneRenderExtent)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            AdvancedGiPrerequisiteGateResult gate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps);
            OpacityMicromapGpuRuntimeSnapshot runtime =
                _accelerationStructureManager
                    ?.OpacityMicromapGpuRuntimeSnapshot ??
                OpacityMicromapGpuRuntimeSnapshot.Disabled;
            AdvancedGiQualificationGateResult qualification =
                EvaluateAdvancedGiQualification(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps,
                    gate,
                    runtime.Supported,
                    gi.DdgiOpacityMicromapQualificationId);
            GiExperimentModeState<DdgiOpacityMicromapMode> opacityMode =
                AdvancedGiAdmissionCoordinator.ResolveMode(
                    gi.DdgiOpacityMicromapMode,
                    DdgiOpacityMicromapMode.Off,
                    runtime.Supported,
                    gate,
                    qualification,
                    runtime.Enabled,
                    gi.DdgiOpacityMicromapQualificationId,
                    runtime.Detail);

            AdvancedGiPrerequisiteGateResult guidingGate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.DirectionalGuiding);
            // Both scheduler backends now have an authoritative C3 work
            // source.  GpuResident is compacted by ddgi_guiding_prepare.comp;
            // CpuReference retains the bounded upload/oracle path.
            bool guidingRuntimeSupported = true;
            AdvancedGiQualificationGateResult guidingQualification =
                EvaluateAdvancedGiQualification(
                    AdvancedGiPrerequisiteFeature.DirectionalGuiding,
                    guidingGate,
                    guidingRuntimeSupported,
                    gi.SimpleDdgiDirectionalGuidingQualificationId);
            GiExperimentModeState<SimpleDdgiDirectionalGuidingMode> guidingMode =
                AdvancedGiAdmissionCoordinator.ResolveMode(
                    gi.SimpleDdgiDirectionalGuidingMode,
                    SimpleDdgiDirectionalGuidingMode.Off,
                    supported: guidingRuntimeSupported,
                    prerequisiteGate: guidingGate,
                    qualificationGate: guidingQualification,
                    // This preflight controls immutable graph inventory. Exact
                    // per-scene resources are still admitted transactionally
                    // after the DDGI physical layout is known.
                    resourcesComplete: guidingRuntimeSupported,
                    gi.SimpleDdgiDirectionalGuidingQualificationId,
                    "guiding-frame-integration-available");

            InitializeGiCausticCoordinator(sceneRenderExtent);
            InitializeNearFieldResidualCoordinator(sceneRenderExtent);

            // Every optional graph branch is derived from an effective mode.
            // C3's live allocation still fails closed later if the exact
            // source-cache prefix, transient arena, or frame workload cannot
            // be admitted.
            return _advancedGiAdmission.ResolveStartup(
                new AdvancedGiStartupRequest(
                    SimpleDdgiReceiverFeedbackMode.Off,
                    opacityMode,
                    guidingMode,
                    _giCaustic.Mode,
                    _nearFieldResidual.Mode,
                    AdvancedGiNearFieldGraphProfile.From(
                        _nearFieldResidual.EffectiveProfile)));
        }

        private void InitializeGiCausticCoordinator(
            Extent2D sceneRenderExtent)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            AdvancedGiPrerequisiteGateResult gate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.TaggedCaustics);
            bool candidateAuthorized =
                gi.GiCausticMode == GiCausticMode.WorldCacheExperiment &&
                _advancedGiAdmission.CandidateProfile?.Caustics is not null &&
                _advancedGiAdmission.TryAuthorizeCandidate(
                    _performanceCaptureMetadataProvider.BuildIdentity,
                    out _);
            AdvancedGiCandidateProfileDocument? candidateProfile =
                _advancedGiAdmission.CandidateProfile;

            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(
                _context.PhysicalDevice,
                &properties);
            PerformanceCaptureBuildIdentity captureIdentity =
                _performanceCaptureMetadataProvider.BuildIdentity;
            var qualificationContext =
                new AdvancedGiRuntimeQualificationContext(
                    properties.VendorID,
                    properties.DeviceID,
                    properties.DriverVersion,
                    properties.ApiVersion,
                    FeatureSupported: false,
                    captureIdentity.ShaderBundleHash,
                    AdvancedGiQualificationContract.SettingsContractSha256,
                    captureIdentity.Commit,
                    _advancedGiAdmission.SettingsFingerprint,
                    _advancedGiAdmission.RuntimeContentBinding.CorpusSha256,
                    _advancedGiAdmission.RuntimeContentBinding
                        .ContentProfileId,
                    _advancedGiAdmission.RuntimeContentBinding
                        .SceneAssetSha256);
            GiCausticInitializationResult result = _giCaustic.Initialize(
                new GiCausticInitializationRequest(
                    gi.GiCausticMode,
                    gi.GiCausticQualificationId,
                    sceneRenderExtent,
                    _advancedGiAdmission.HasGiCausticEvidence,
                    _advancedGiAdmission.GiCausticEvidence,
                    _advancedGiAdmission.GiCausticAdmissionContext,
                    candidateAuthorized,
                    candidateAuthorized
                        ? candidateProfile!.Caustics
                        : null,
                    candidateProfile?.Authorization ?? default,
                    gate,
                    _advancedGiAdmission.QualificationManifest,
                    qualificationContext,
                    IsHybridReflectionTargetEnabled(Settings)));
            if (result.PublishAdmissionContext)
            {
                _advancedGiAdmission.UpdateGiCausticAdmissionContext(
                    result.AdmissionContext);
            }
        }

        private void InitializeNearFieldResidualCoordinator(
            Extent2D sceneRenderExtent)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            AdvancedGiPrerequisiteGateResult gate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.NearFieldResidual);
            bool candidateAuthorized =
                gi.SimpleDdgiNearFieldResidualMode is
                    SimpleDdgiNearFieldResidualMode
                        .HiZHalfResolutionExperiment or
                    SimpleDdgiNearFieldResidualMode.HiZAdaptive &&
                _advancedGiAdmission.CandidateProfile?.NearFieldResidual is
                    not null &&
                _advancedGiAdmission.TryAuthorizeCandidate(
                    _performanceCaptureMetadataProvider.BuildIdentity,
                    out _);
            AdvancedGiCandidateProfileDocument? candidateProfile =
                _advancedGiAdmission.CandidateProfile;

            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(
                _context.PhysicalDevice,
                &properties);
            PerformanceCaptureBuildIdentity captureIdentity =
                _performanceCaptureMetadataProvider.BuildIdentity;
            var qualificationContext =
                new AdvancedGiRuntimeQualificationContext(
                    properties.VendorID,
                    properties.DeviceID,
                    properties.DriverVersion,
                    properties.ApiVersion,
                    FeatureSupported: false,
                    captureIdentity.ShaderBundleHash,
                    AdvancedGiQualificationContract.SettingsContractSha256,
                    captureIdentity.Commit,
                    _advancedGiAdmission.SettingsFingerprint,
                    _advancedGiAdmission.RuntimeContentBinding.CorpusSha256,
                    _advancedGiAdmission.RuntimeContentBinding
                        .ContentProfileId,
                    _advancedGiAdmission.RuntimeContentBinding
                        .SceneAssetSha256);
            var nearFieldSettings = new NearFieldResidualSettings(
                gi.SimpleDdgiNearFieldResidualQualityPreset,
                gi.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled,
                gi.SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters,
                gi.SimpleDdgiNearFieldResidualRaysPerPixel,
                gi.SimpleDdgiNearFieldResidualFilterIterationCount,
                gi.SimpleDdgiNearFieldResidualIntensity,
                gi.SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled);
            NearFieldResidualInitializationResult result =
                _nearFieldResidual.Initialize(
                    new NearFieldResidualInitializationRequest(
                        gi.SimpleDdgiNearFieldResidualMode,
                        gi.SimpleDdgiNearFieldResidualQualificationId,
                        sceneRenderExtent,
                        nearFieldSettings,
                        _advancedGiAdmission.HasNearFieldResidualEvidence,
                        _advancedGiAdmission.NearFieldResidualEvidence,
                        _advancedGiAdmission
                            .NearFieldResidualAdmissionContext,
                        candidateAuthorized,
                        candidateAuthorized
                            ? candidateProfile!.NearFieldResidual
                            : null,
                        candidateProfile?.Authorization ?? default,
                        gate,
                        _advancedGiAdmission.QualificationManifest,
                        qualificationContext,
                        _giCaustic.Mode.EffectiveMode is
                            GiCausticMode.WorldCacheExperiment or
                            GiCausticMode.AutoQualified));
            if (result.PublishAdmissionContext)
            {
                _advancedGiAdmission
                    .UpdateNearFieldResidualAdmissionContext(
                        result.AdmissionContext);
            }
        }

        private static SimpleDdgiNearFieldResidualProfile
            ApplyNearFieldAdvancedOverrides(
                in SimpleDdgiNearFieldResidualProfile profile,
                GlobalIlluminationSettings settings)
        {
            if (!settings.SimpleDdgiNearFieldResidualAdvancedOverridesEnabled)
                return profile;

            float maximumDistance = settings
                .SimpleDdgiNearFieldResidualMaximumTraceDistanceMeters;
            return profile with
            {
                MaximumTraceDistanceMeters = maximumDistance,
                FullWeightTraceDistanceMeters = MathF.Min(
                    profile.FullWeightTraceDistanceMeters,
                    maximumDistance * 0.5f),
                MaximumRaysPerPixel = settings
                    .SimpleDdgiNearFieldResidualRaysPerPixel,
                FilterIterationCount = settings
                    .SimpleDdgiNearFieldResidualFilterIterationCount
            };
        }

        private bool HasFormatFeatures(
            Format format,
            FormatFeatureFlags required)
        {
            FormatProperties properties = default;
            _context.Api.GetPhysicalDeviceFormatProperties(
                _context.PhysicalDevice,
                format,
                &properties);
            return (properties.OptimalTilingFeatures & required) == required;
        }

        private bool IsOpacityMicromapRuntimePreflightSupported()
        {
            OpacityMicromapRuntimeCapabilities capabilities =
                _context.OpacityMicromapExtCapability.Capabilities;
            // Before the AS manager is constructed, the context deliberately has not yet marked
            // the matching static-BLAS attachment owner as integrated. Check every independent
            // logical-device/native fact here and let manager construction establish that final
            // ownership fact before it reports the authoritative runtime capability.
            return _context.RayQuerySupported &&
                   _context.KhrAccelerationStructure is not null &&
                   _context.OpacityMicromapExtCommandApi is not null &&
                   capabilities.ExtensionAvailable &&
                   capabilities.FeatureEnabled &&
                   capabilities.CommandBufferBuildAvailable &&
                   capabilities.FourStateFormatAvailable &&
                   capabilities.MaximumFourStateSubdivisionLevel != 0u;
        }

        private AdvancedGiQualificationGateResult EvaluateAdvancedGiQualification(
            AdvancedGiPrerequisiteFeature feature,
            in AdvancedGiPrerequisiteGateResult prerequisite,
            bool supported,
            string? configuredQualificationId)
        {
            if (!prerequisite.Passed)
            {
                return AdvancedGiQualificationGateResult.Reject(
                    prerequisite.FailureDetail);
            }

            AdvancedGiRuntimeQualificationContext context =
                CaptureAdvancedGiRuntimeQualificationContext(supported);
            return _advancedGiAdmission.EvaluateQualification(
                feature,
                context,
                prerequisite.QualificationId,
                configuredQualificationId);
        }

        private AdvancedGiRuntimeQualificationContext
            CaptureAdvancedGiRuntimeQualificationContext(bool supported)
        {
            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(
                _context.PhysicalDevice,
                &properties);
            PerformanceCaptureBuildIdentity captureIdentity =
                _performanceCaptureMetadataProvider.BuildIdentity;
            var context = new AdvancedGiRuntimeQualificationContext(
                properties.VendorID,
                properties.DeviceID,
                properties.DriverVersion,
                properties.ApiVersion,
                supported,
                captureIdentity.ShaderBundleHash,
                AdvancedGiQualificationContract.SettingsContractSha256,
                captureIdentity.Commit,
                _advancedGiAdmission.SettingsFingerprint,
                _advancedGiAdmission.RuntimeContentBinding.CorpusSha256,
                _advancedGiAdmission.RuntimeContentBinding.ContentProfileId,
                _advancedGiAdmission.RuntimeContentBinding.SceneAssetSha256);
            return context;
        }

        private void RegisterSceneBuffers()
        {
            System.Diagnostics.Debug.WriteLine("Registering scene buffers in bindless heap...");

            // Register mesh manager buffers
            _meshManager.RegisterBuffers(_bindlessHeap);
            _materialManager.RegisterBuffers(_bindlessHeap);

            // Register default material textures at fixed shader-visible indices.
            _textureManager.InitializeDefaultTextures(_bindlessHeap);
            _smaaResources ??= new SmaaResources(_textureManager, _bindlessHeap);

            // Register light manager buffer (index 12)
            _lightManager.RegisterBuffer(_bindlessHeap, BindlessIndex.LightBuffer);
            if (_simpleDdgiReceiverFeedback is not null &&
                !_simpleDdgiReceiverFeedback.TryRegisterDescriptors(
                    _bindlessHeap,
                    _lightManager.LightBuffer,
                    _lightManager.LightBufferAllocatedBytes,
                    out string receiverFeedbackDescriptorReason))
            {
                // The B1 runtime stays fail-closed. This diagnostic does not
                // alter the global prerequisite/mode gate and does not cause
                // a legacy receiver buffer to become a capture source.
                System.Diagnostics.Debug.WriteLine(
                    "B1 receiver-feedback descriptor fallback unavailable: " +
                    receiverFeedbackDescriptorReason);
            }

            if (_simpleDdgiGuidingRuntime is not null &&
                !_simpleDdgiGuidingRuntime.TryRegisterDescriptors(
                    _bindlessHeap,
                    _lightManager.LightBuffer,
                    _lightManager.LightBufferAllocatedBytes,
                    out string guidingDescriptorReason))
            {
                // This affects only C3's dormant descriptor fallback. It
                // neither admits the global guiding mode nor writes source
                // cache slot 203.
                System.Diagnostics.Debug.WriteLine(
                    "C3 guiding descriptor fallback unavailable: " +
                    guidingDescriptorReason);
            }

            if (_simpleDdgiGuidingSourceCacheSidecar is not null &&
                !_simpleDdgiGuidingSourceCacheSidecar.TryRegisterDescriptorContext(
                    _bindlessHeap,
                    _lightManager.LightBuffer,
                    _lightManager.LightBufferAllocatedBytes,
                    out string guidingSidecarDescriptorReason))
            {
                System.Diagnostics.Debug.WriteLine(
                    "C3 source-cache sidecar fallback unavailable: " +
                    guidingSidecarDescriptorReason);
            }

            if (_advancedGiAdmission.GraphModes.UsesCausticWorldCache &&
                !_giCaustic.TryRegisterDescriptors(
                    _bindlessHeap,
                    _lightManager.LightBuffer,
                    _lightManager.LightBufferAllocatedBytes,
                    out string causticDescriptorReason))
            {
                System.Diagnostics.Debug.WriteLine(
                    "C4 caustic descriptor fallback unavailable: " +
                    causticDescriptorReason);
            }

            _ddgiEmissiveTransport!.Register(_bindlessHeap);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.DepthTexture,
                _renderTargets!.SceneDepth.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            RegisterSceneRenderTextures();

            // Register scene data buffers
            _sceneDataBuilder.RegisterBuffers(_bindlessHeap);
            _skinningManager.RegisterBuffers(_bindlessHeap);
            _particleSystemManager.RegisterBuffers(_bindlessHeap);
            _gpuParticleRuntimeManager.RegisterBuffers(_bindlessHeap);
            _foliageManager.RegisterBuffers(_bindlessHeap);
            _diagnosticsBuffer.RegisterBuffers(_bindlessHeap);
            _autoExposureManager!.RegisterBuffers(_bindlessHeap);
            _directionalShadowResources!.Register(_bindlessHeap, _swapchain.DepthImageView);
            _spotShadowAtlas!.Register(_bindlessHeap, _swapchain.DepthImageView);
            _pointShadowCubemapArray!.Register(_bindlessHeap, _swapchain.DepthImageView);
            _environmentManager!.Register(_bindlessHeap);
            _environmentManager.RegisterReflectionProbeFallback(_bindlessHeap);
            _reflectionProbeManager!.Register(_bindlessHeap);
            _simpleDdgiVolumeManager!.Register(_bindlessHeap);
            _simpleDdgiLightTreeResources!.Register(_bindlessHeap);
            _ddgiFoliageProxyManager!.Register(_bindlessHeap);
            _farFieldClipmapManager!.Register(_bindlessHeap);
            _accelerationStructureManager!.Register(_bindlessHeap);
            _meshletPhysicalResidencyResources?.Initialize();

            System.Diagnostics.Debug.WriteLine("Scene buffers registered.");
        }

        private void ObserveFrameContextFenceCompletion(int frameContext)
        {
            ulong trackedSubmission =
                _submissionOwnership.ObserveContextCompleted(frameContext);
            ulong submittedSubmission =
                _submittedGraphicsFrameFenceValues[frameContext];
            if (trackedSubmission != submittedSubmission)
            {
                throw new InvalidOperationException(
                    "Frame submission ownership diverged from the fence publication ledger.");
            }

            _completedGraphicsFrameFenceValue = Math.Max(
                _completedGraphicsFrameFenceValue,
                submittedSubmission);
        }

        private void WaitForAcquiredSwapchainImageOwner()
        {
            SwapchainImageSubmissionOwner owner =
                _submissionOwnership.GetSwapchainImageOwner(_imageIndex);
            _lastAcquiredImageOwnerSubmissionSerial =
                owner.SubmissionSerial;
            _lastAcquiredImageOwnerFrameContext = owner.FrameContext;
            _lastSwapchainImageOwnerWaitMicroseconds = 0L;
            if (owner.Completed || owner.SubmissionSerial == 0UL)
                return;
            if ((uint)owner.FrameContext >= FramesInFlight ||
                _submittedGraphicsFrameFenceValues[owner.FrameContext] !=
                owner.SubmissionSerial)
            {
                throw new InvalidOperationException(
                    "The acquired swapchain image references an invalid frame submission owner.");
            }

            try
            {
                _sync.WaitForFence(owner.FrameContext);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault(
                    "Failed while waiting for the acquired swapchain image's prior submission owner.",
                    exception.Result);
                throw;
            }

            _lastSwapchainImageOwnerWaitMicroseconds =
                _sync.LastFenceWaitMicroseconds;
            _stallTracker.Record(
                RuntimeStallReason.SwapchainImageOwnerWait,
                _lastSwapchainImageOwnerWaitMicroseconds,
                "Acquired image submission owner");
            ObserveFrameContextFenceCompletion(owner.FrameContext);
        }

        private void SelectAndRecycleFrameResourceContext()
        {
            FrameResourceContextSelection selection;
            try
            {
                selection = _submissionOwnership
                    .SelectFrameResourceContext(_sync.IsFenceSignaled);
                if (selection.RequiresWait)
                    _sync.WaitForFence(selection.FrameContext);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault(
                    "Failed while selecting a reusable frame-resource context.",
                    exception.Result);
                throw;
            }

            _currentFrame = selection.FrameContext;
            _sync.SetCurrentFrame(_currentFrame);
            _lastRecycledFrameResourceOwnerSubmissionSerial =
                selection.PreviousSubmissionSerial;
            _lastFrameResourceRecycleWaitMicroseconds =
                selection.RequiresWait
                    ? _sync.LastFenceWaitMicroseconds
                    : 0L;
            if (selection.RequiresWait)
            {
                _stallTracker.Record(
                    RuntimeStallReason.FrameResourceRecycleWait,
                    _lastFrameResourceRecycleWaitMicroseconds,
                    "Frame-resource context recycle");
                ObserveFrameContextFenceCompletion(_currentFrame);
            }
            else if (selection.PreviousSubmissionSerial != 0UL)
            {
                // SelectFrameResourceContext has already established the
                // signaled state without blocking.
                ObserveFrameContextFenceCompletion(_currentFrame);
            }
        }

        public bool BeginFrame()
        {
            _lifetime.ThrowIfDisposalStarted();
            int currentThreadId = Environment.CurrentManagedThreadId;
            _ = Interlocked.CompareExchange(
                ref _renderThreadManagedId,
                currentThreadId,
                comparand: 0);
            if (!_lifetime.InitializationSucceeded)
                Initialize();

            _lifetime.ThrowIfSubmissionFaulted();
            _lifetime.EnsureCanBeginFrame();
            MarkPipelineCacheRenderCriticalFramesStarted();
            _lastAcquireImageMicroseconds = 0L;
            _lastSwapchainImageOwnerWaitMicroseconds = 0L;
            _lastFrameResourceRecycleWaitMicroseconds = 0L;

            if (RendererBuildConfiguration.ProgressivePipelineStartup &&
                StartupSnapshot.Phase != RendererStartupPhase.FullQuality)
            {
                ThrowIfProgressiveStartupFaulted();
                return BeginProgressiveFrame();
            }
            _productionFrameWasFullQuality = true;

            if (_lifetime.SwapchainRecreationRequested)
            {
                bool recreated = RecreateSwapchain();
                _lifetime.ObserveSwapchainRecreationAttempt(recreated);
                if (!recreated)
                    return false;
            }

            _stallTracker.BeginFrame();

            // Acquire first with a semaphore that is independent of frame
            // resources. Reacquiring an image identifies the only image owner
            // that can require a wait; frame-resource recycling is selected
            // separately afterwards.
            _currentAcquireSemaphoreIndex =
                _submissionOwnership.SelectAcquireSemaphore();
            long acquireStart = Stopwatch.GetTimestamp();
            Result acquireResult = _swapchain.TryAcquireNextImage(
                _sync.GetImageAvailableSemaphore(
                    _currentAcquireSemaphoreIndex),
                out _imageIndex);
            _lastAcquireImageMicroseconds = ElapsedMicroseconds(acquireStart);
            _stallTracker.Record(
                RuntimeStallReason.SwapchainAcquire,
                _lastAcquireImageMicroseconds,
                "Acquire next swapchain image");

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                _lifetime.RequestSwapchainRecreation();
                _lifetime.ObserveSwapchainRecreationAttempt(
                    RecreateSwapchain());
                return false;
            }

            if (acquireResult != Result.Success &&
                acquireResult != Result.SuboptimalKhr)
            {
                if (acquireResult == Result.ErrorDeviceLost)
                {
                    MarkFrameSubmissionFault(
                        "The Vulkan device was lost while acquiring a swapchain image.",
                        acquireResult);
                }
                throw new VulkanException(
                    "Failed to acquire swapchain image",
                    acquireResult);
            }

            if (acquireResult == Result.SuboptimalKhr)
                _lifetime.RequestSwapchainRecreation();

            WaitForAcquiredSwapchainImageOwner();
            SelectAndRecycleFrameResourceContext();
            _meshletPhysicalResidencyResources?.BeginFenceSafeFrame(
                _currentFrame,
                _ddgiFrameSerial,
                _completedGraphicsFrameFenceValue);

            _simpleDdgiVolumeManager?.ObserveFrameFenceCompletion(
                _ddgiFrameSerial,
                _completedGraphicsFrameFenceValue);
            _simpleDdgiVolumeManager?.TryConsumePersistentWarmStartReadback(
                _currentFrame);
            // This is deliberately the same ring slot whose fence was just
            // observed. A screenshot never reads a newer frame's buffer.
            _screenshotReadbackManager.CompleteFrameAfterFence(_currentFrame);
            _linearHdrReadbackManager.CompleteFrameAfterFence(_currentFrame);
            _diagnosticsBuffer.ReadCompletedFrame(_currentFrame);
            _directionalShadowHistoryResources?.ReadCompletedFrame(_currentFrame);
            _hybridReflectionRuntime?.ReadCompletedFrame(_currentFrame);
            _simpleDdgiLightTreeResources?.ReadCompletedFrame(_currentFrame);
            if (_ddgiFrameSerial < ulong.MaxValue)
            {
                // This is intentionally a no-op while B1 has no exact
                // all-producer capture contract. Once such a source exists,
                // the runtime routes the fence-complete 80-byte header/witness
                // prefix through
                // its strict resource manager before any scheduler binding can
                // be exposed.
                _simpleDdgiReceiverFeedback?.CompleteFrameAfterFence(
                    _currentFrame,
                    _ddgiFrameSerial + 1UL);
                // With no source-cache handshake no C3 build can be pending,
                // so this is normally a no-op. If a future exact integration
                // records one, publication still occurs only after this frame
                // slot's fence has completed.
                if (_simpleDdgiGuidingFrameCoordinator is { } guidingCoordinator)
                {
                    guidingCoordinator.CompleteFrameAfterFence(_currentFrame);
                }
                else if (_simpleDdgiGuidingRuntime is { } guidingRuntime)
                {
                    _ = guidingRuntime.TryReadCompletedFrame(
                        _currentFrame,
                        out _);
                }
            }

            _gpuParticleRuntimeManager.ReadCompletedFrame(_currentFrame);
            _foliageManager.ReadCompletedFrame(_currentFrame);
            _sceneOpaqueCompactionPass?.ReadCompletedFrame(_currentFrame);
            _forwardVisibilityCompactionPass?.ReadCompletedFrame(_currentFrame);
            _autoExposureManager?.ReadCompletedFrame(_currentFrame);
            _completedGpuCounters = _diagnosticsBuffer.GetLastCompletedCounters(_currentFrame);
            _completedDdgiForwardEstimateCounters =
                _diagnosticsBuffer.GetLastCompletedDdgiForwardEstimateCounters(_currentFrame);
            _completedDdgiInvestigationCounters =
                _diagnosticsBuffer.GetLastCompletedDdgiInvestigationCounters(_currentFrame);
            _completedDirectionalShadowReceiverCounters =
                _diagnosticsBuffer.GetLastCompletedDirectionalShadowReceiverCounters(_currentFrame);
            _completedDirectionalShadowCasterDiagnostics =
                DirectionalShadowCasterDiagnosticsEvaluator.AttachCpuReference(
                    _diagnosticsBuffer.GetLastCompletedDirectionalShadowCasterDiagnostics(_currentFrame),
                    _directionalShadowCasterFrameCaptures[_currentFrame]);
            _completedDirectionalShadowRayCounters =
                _directionalShadowHistoryResources?.GetLastCompletedCounters(
                    _currentFrame) ?? DirectionalShadowRayCounters.Empty;
            _completedHybridReflectionCounters =
                _hybridReflectionRuntime?.GetLastCompletedCounters(
                    _currentFrame) ?? HybridReflectionCounterSnapshot.Empty;
            _completedTransparentReflectionCounters =
                _diagnosticsBuffer
                    .GetLastCompletedTransparentReflectionCounters(
                        _currentFrame);
            _reflectionProbeManager?
                .ObserveCompletedTransparentReflectionCounters(
                    _completedTransparentReflectionCounters);
            _completedFarFieldMaterialV2Counters =
                _diagnosticsBuffer.GetLastCompletedFarFieldMaterialV2Counters(_currentFrame);
            _completedMaterialGiCounters = _diagnosticsBuffer.GetLastCompletedMaterialGiCounters(_currentFrame);
            _completedThinSurfaceTransportCounters =
                _diagnosticsBuffer.GetLastCompletedThinSurfaceTransportCounters(_currentFrame);
            _completedDdgiGeometryParticipationCounters =
                _diagnosticsBuffer.GetLastCompletedDdgiGeometryParticipationCounters(
                    _currentFrame);
            _completedDdgiManyLightCounters =
                _diagnosticsBuffer.GetLastCompletedDdgiManyLightCounters(
                    _currentFrame);
            _completedDdgiAreaLightCounters =
                _diagnosticsBuffer.GetLastCompletedDdgiAreaLightCounters(
                    _currentFrame);
            _forwardPlusPass?.ObserveCompletedSimpleDdgiReceiverCacheCounters(
                _diagnosticsBuffer
                    .GetLastCompletedSimpleDdgiReceiverCacheCounters(
                        _currentFrame));
            _debugOverlayBuilder.ObserveCompletedDdgiCounters(
                _diagnosticsBuffer.GetLastCompletedDebugDdgiOverlayCounters(
                    _currentFrame));
            _completedGpuParticleCounters = _gpuParticleRuntimeManager.GetLastCompletedCounters(_currentFrame);
            _completedFoliageCounters = _foliageManager.GetLastCompletedCounters(_currentFrame);
            _completedSceneSubmissionCounters = _sceneOpaqueCompactionPass?.GetLastCompletedCounters(_currentFrame) ??
                                                SceneSubmissionCounterSnapshot.Invalid;
            _completedForwardVisibilityCounters =
                _forwardVisibilityCompactionPass?.GetLastCompletedCounters(_currentFrame) ??
                SceneSubmissionCounterSnapshot.Invalid;
            _completedSceneSubmissionValidation =
                _sceneOpaqueCompactionPass?.GetLastCompletedValidation(_currentFrame) ??
                SceneSubmissionValidationSnapshot.Invalid;
            _gpuTimestamps.ReadCompletedFrame(_currentFrame);
            _accelerationStructureManager?
                .ObserveCompletedDynamicGeometryGpuTiming(
                    _gpuTimestamps.LastCompletedSnapshot,
                    _currentFrame);
            _giCaustic.CompleteFrameAfterFence(
                _currentFrame,
                _sync.GetInFlightFence(_currentFrame));

            // The frame-slot fence was waited above. C5 consumes the recorded
            // transaction before publishing or reclaiming any generation.
            ApplyNearFieldResidualPublication(
                _nearFieldResidual.CompleteFrameAfterFence(
                    _currentFrame,
                    _gpuTimestamps.LastCompletedSnapshot,
                    _completedGraphicsFrameFenceValue,
                    _ddgiFrameSerial));
            _asyncComputeCoordinator.ConsumeCompletedTiming(
                _currentFrame,
                _gpuTimestamps.LastCompletedSnapshot);
            bool completedSchedulerFeedbackAvailable = false;
            GPUSimpleDdgiSchedulerFeedback completedSchedulerFeedback = default;
            uint completedSchedulerFeedbackTransportTopologyGeneration = 0u;
            if (_simpleDdgiVolumeManager != null &&
                _ddgiFrameSerial < ulong.MaxValue)
            {
                // The scheduler summary is copied in the previous use of this
                // frame slot. The strict +1 serial is intentional: it keeps a
                // fence-complete result frame-late even when the CPU reaches
                // BeginFrame immediately after submission.
                completedSchedulerFeedbackAvailable =
                    _simpleDdgiVolumeManager.TryConsumeGpuSchedulerFeedback(
                        _currentFrame,
                        _ddgiFrameSerial + 1UL) &&
                    _simpleDdgiVolumeManager.GpuSchedulerFeedbackValid;
                if (completedSchedulerFeedbackAvailable)
                {
                    completedSchedulerFeedback =
                        _simpleDdgiVolumeManager.LastGpuSchedulerFeedback;
                    completedSchedulerFeedbackTransportTopologyGeneration =
                        _simpleDdgiVolumeManager.GpuScheduler
                            .LastFeedbackTransportTopologyGeneration;
                }

                _simpleDdgiVolumeManager.TryConsumeProbeResidencyFeedback(
                    _currentFrame,
                    _ddgiFrameSerial + 1UL);
                _simpleDdgiVolumeManager.TryConsumeGpuTransportAudit(
                    _currentFrame,
                    _ddgiFrameSerial + 1UL);
            }

            uint completedSchedulerActiveCanonicalMutationCount =
                completedSchedulerFeedbackAvailable
                    ? _simpleDdgiVolumeManager?.GpuScheduler
                        .LastActiveCanonicalMutationCount ?? 0u
                    : 0u;
            uint completedSchedulerActiveSourceMutationCount =
                completedSchedulerFeedbackAvailable
                    ? _simpleDdgiVolumeManager?.GpuScheduler
                        .LastActiveSourceMutationCount ?? 0u
                    : 0u;
            _simpleDdgiFrameEvidence.CompleteAfterFence(
                _currentFrame,
                new SimpleDdgiFenceCompletedEvidence(
                    _gpuTimestamps.LastCompletedSnapshot,
                    _completedDdgiForwardEstimateCounters,
                    _completedDdgiInvestigationCounters,
                    _completedMaterialGiCounters,
                    completedSchedulerFeedbackAvailable,
                    completedSchedulerFeedback,
                    completedSchedulerFeedbackTransportTopologyGeneration,
                    completedSchedulerActiveCanonicalMutationCount,
                    completedSchedulerActiveSourceMutationCount));
            SimpleDdgiSourceCacheWorkloadObservation sourceCacheObservation =
                _simpleDdgiFrameEvidence.CaptureSnapshot()
                    .SourceCacheObservation;
            if (sourceCacheObservation.Valid)
            {
                _simpleDdgiVolumeManager?.ObserveSourceCacheWorkload(
                    sourceCacheObservation.ShadeableHitCount,
                    sourceCacheObservation.MissCount,
                    sourceCacheObservation.RejectedBackFaceCount,
                    sourceCacheObservation.SourceCacheLayoutIdentity,
                    sourceCacheObservation.FrameSerial);
            }

            // Process completed frame deletions
            _deleter.ProcessCompletedFrame(_sync.GetInFlightFence(_currentFrame));

            // Scene transitions can change the render-target profile during Update. Apply those
            // changes only at this fence-complete frame boundary, before acquiring an image or
            // recording the next primary command buffer. Recreating targets from DrawScene used
            // to invalidate resources and command-buffer state belonging to the frame that was
            // already in progress.
            EnsureRenderTargetProfile();

            // The staging ring slot is safe to reuse after the frame fence has completed.
            _stagingRing.BeginFrame(_currentFrame);
            _uploadBudgetTracker.BeginFrame();
            _context.SetAllocatorCurrentFrameIndex(_allocatorFrameIndex++);

            EnsureMeshPipelineDiagnosticVariant();

            // Reset and begin recording the primary command buffer owned by this frame.
            _cmd.ResetGraphicsCommandBuffer(_currentFrame);
            _cmd.ResetAsyncSplitGraphicsCommandBuffers(_currentFrame);
            if (_context.HasIndependentComputeQueue)
                _cmd.ResetAsyncComputeCommandPool(_currentFrame);
            if (Settings.UseSecondaryCommandBuffers)
                _cmd.ResetSecondaryGraphicsCommandPool(_currentFrame);
            _environmentManager?.EnsureResourcesCurrent(
                _bindlessHeap,
                () => RecordDeviceWaitIdle(
                    RuntimeStallReason.DeviceWaitIdle,
                    "Environment resource recreate",
                    _context.WaitIdle));

            _currentCommandBuffer = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
            InsertInterFrameSharedResourceDependency(_currentCommandBuffer);
            _meshletPhysicalResidencyResources?.RecordFrameUploads(
                _currentCommandBuffer,
                _currentFrame,
                _ddgiFrameSerial,
                _sync.GetInFlightFence(1 - _currentFrame));
            RendererValidationMessageSnapshot validationAtBoundary = _context.ValidationMessageSnapshot;
            _asyncComputeCoordinator.BeginFrame(
                new AsyncComputeFrameBoundaryInput(
                    _ddgiFrameSerial,
                    validationAtBoundary.ErrorCount));
            _deferredAsyncSubmissions.Clear();
            _swapchainImageTransitionedThisFrame = false;
            _lastQueueSubmitMicroseconds = 0;
            bool gpuTimingRequested =
                Settings.Debug.AllowGpuTiming ||
                _nearFieldResidual.IsGenerationExecutable;
            BeginReflectionProbeCaptureFrame(
                _gpuTimestamps.Supported && gpuTimingRequested);
            _lifetime.MarkFrameStarted();
            _gpuTimestamps.BeginFrame(
                _currentCommandBuffer,
                _currentFrame,
                gpuTimingRequested);

            return true;
        }

        void IRendererFramePacingDiagnostics.ReportFramePacing(
            double maximumFramesPerSecond,
            long waitMicroseconds)
        {
            _hostMaximumFramesPerSecond = maximumFramesPerSecond;
            _hostFramePacingWaitMicroseconds = Math.Max(0L, waitMicroseconds);
        }

        public void EndFrame()
        {
            _lifetime.ThrowIfDisposalStarted();
            _lifetime.EnsureCanEndFrame();

            if (_progressiveFrame)
            {
                EndProgressiveFrame();
                return;
            }

            var vk = _context.Api;

            _meshletPhysicalResidencyResources?.RecordFeedbackReadback(
                _currentCommandBuffer,
                _currentFrame);

            // The acquired image is only transitioned once the terminal graphics submission owns
            // it. Earlier async setup/compute submissions never consume imageAvailable.
            if (_swapchainImageTransitionedThisFrame)
            {
                RecordTerminalScreenshotCapture();
                TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.PresentSrcKhr);
            }

            // End command buffer recording
            Result result = vk.EndCommandBuffer(_currentCommandBuffer);
            if (result != Result.Success)
            {
                MarkFrameSubmissionFault($"Failed to end terminal graphics command buffer: {result}.", result);
                throw new VulkanException("Failed to end command buffer", result);
            }

            // Every non-terminal command buffer was fully recorded before any submission. Submit
            // it now; a failed submit still leaves the previous frame fence signalled.
            SubmitRecordedAsyncSegments();

            // Reset the fence only after all preceding submissions succeeded and immediately
            // before the terminal graphics submit that waits all compute work.
            try
            {
                _sync.ResetFence(_currentFrame);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault("Failed to reset the terminal frame fence before graphics submission.",
                    exception.Result);
                throw;
            }

            _stallTracker.Record(RuntimeStallReason.Unknown, _sync.LastFenceResetMicroseconds, "Reset frame fence");

            Semaphore renderFinishedSemaphore = _sync.GetRenderFinishedSemaphoreForImage(_imageIndex);
            var signalSemaphores = stackalloc Semaphore[] { renderFinishedSemaphore };
            var commandBuffers = stackalloc CommandBuffer[] { _currentCommandBuffer };

            IReadOnlyList<AsyncComputeTimelineWait> asyncComputeWaits =
                _asyncComputeCoordinator.TerminalWaits;
            int timelineWaitCount = asyncComputeWaits.Count;
            int waitCount = checked(1 + timelineWaitCount);
            Semaphore* waitSemaphores = stackalloc Semaphore[waitCount];
            PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[waitCount];
            ulong* waitValues = stackalloc ulong[waitCount];
            // A TimelineSemaphoreSubmitInfo accompanies the terminal submit whenever it waits
            // on the async timeline. Its value arrays must cover *all* submit semaphores,
            // including the binary render-finished signal, whose required value is zero.
            ulong* signalValues = stackalloc ulong[1];
            signalValues[0] = 0;
            waitSemaphores[0] = _sync.GetImageAvailableSemaphore(
                _currentAcquireSemaphoreIndex);
            // This is the first submission allowed to access the acquired image. A no-op frame
            // still consumes the binary semaphore here so it can be safely reused on acquire;
            // use AllCommands in that case because no color-attachment stage is guaranteed to
            // occur in the terminal command buffer.
            waitStages[0] = _swapchainImageTransitionedThisFrame
                ? PipelineStageFlags.ColorAttachmentOutputBit
                : PipelineStageFlags.AllCommandsBit;
            waitValues[0] = 0;
            for (int i = 0; i < timelineWaitCount; i++)
            {
                AsyncComputeTimelineWait wait = asyncComputeWaits[i];
                waitSemaphores[i + 1] = _cmd.AsyncComputeTimelineSemaphore;
                waitStages[i + 1] = ToLegacyPipelineStage(wait.StageMask);
                waitValues[i + 1] = wait.Value;
            }

            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = (uint)waitCount,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = commandBuffers,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = signalSemaphores
            };

            TimelineSemaphoreSubmitInfo timelineWaitInfo = default;
            if (timelineWaitCount > 0)
            {
                timelineWaitInfo = new TimelineSemaphoreSubmitInfo
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    // vkQueueSubmit's timeline value arrays cover every wait semaphore. The
                    // binary imageAvailable and renderFinished entries are explicitly zero.
                    WaitSemaphoreValueCount = (uint)waitCount,
                    PWaitSemaphoreValues = waitValues,
                    SignalSemaphoreValueCount = 1,
                    PSignalSemaphoreValues = signalValues
                };
                submitInfo.PNext = &timelineWaitInfo;
            }

            long submitStart = Stopwatch.GetTimestamp();
            result = vk.QueueSubmit(
                _context.GraphicsQueue,
                1,
                &submitInfo,
                _sync.GetInFlightFence(_currentFrame));
            _lastQueueSubmitMicroseconds += ElapsedMicroseconds(submitStart);
            _stallTracker.Record(RuntimeStallReason.QueueSubmit, _lastQueueSubmitMicroseconds, "Graphics queue submit");

            if (result != Result.Success)
            {
                string failureReason = $"Failed to submit terminal graphics segment: {result}.";
                if (result == Result.ErrorDeviceLost)
                    _lifetime.RecordDeviceLoss();
                Result recoveryResult = TryRecoverFrameFenceAfterTerminalSubmitFailure(
                    waitCount,
                    waitSemaphores,
                    waitStages,
                    waitValues,
                    timelineWaitCount > 0);
                if (recoveryResult == Result.Success)
                    failureReason +=
                        " A fence-only recovery submission was queued; rendering is stopped to preserve the acquired image contract.";
                else if (result != Result.ErrorDeviceLost)
                    failureReason += $" Fence recovery submission also failed: {recoveryResult}.";

                _simpleDdgiFrameEvidence.AbortPendingSubmission();
                MarkFrameSubmissionFault(failureReason, result);
                throw new VulkanException("Failed to submit queue", result);
            }

            _submittedGraphicsFrameFenceValues[_currentFrame] =
                _ddgiFrameSerial == ulong.MaxValue
                    ? ulong.MaxValue
                    : _ddgiFrameSerial + 1UL;
            _submissionOwnership.MarkSubmitted(
                _currentFrame,
                _imageIndex,
                _currentAcquireSemaphoreIndex,
                _submittedGraphicsFrameFenceValues[_currentFrame]);
            _nearFieldResidual.ObserveSuccessfulSubmission(
                _submittedGraphicsFrameFenceValues[_currentFrame]);
            _simpleDdgiFrameEvidence.CommitSuccessfulSubmission(
                _currentFrame);
            _reflectionProbeManager?.CommitCaptureFrameSubmission(
                _currentFrame,
                _ddgiFrameSerial,
                _gpuTimestamps.EnabledThisFrame);

            // Static shadow layers recorded by this frame are not reused by
            // another command buffer until the owning graphics submission has
            // been accepted. All later graphics submissions are ordered after
            // this one, while resource replacement waits for device idle.
            _directionalShadowPass?.ConfirmCurrentFrameSubmission();

            // The terminal graphics submit owns both the acquired swapchain
            // image and its readback copy. Do not permit CPU mapping until this
            // exact frame fence has completed on a later reuse of this slot.
            _screenshotReadbackManager.MarkFrameSubmitted(_currentFrame);
            _linearHdrReadbackManager.MarkFrameSubmitted(_currentFrame);

            AsyncComputeSubmissionPatch asyncSubmissionPatch =
                _asyncComputeCoordinator.CompleteTerminalSubmission(
                    _currentFrame,
                    _lastQueueSubmitMicroseconds);
            _lastDiagnostics =
                _diagnosticsAssembler.ApplyAsyncSubmission(
                    _lastDiagnostics,
                    asyncSubmissionPatch);

            // Present
            var swapchains = stackalloc SwapchainKHR[] { _swapchain.Swapchain };
            var imageIndices = stackalloc uint[] { _imageIndex };

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = signalSemaphores,
                SwapchainCount = 1,
                PSwapchains = swapchains,
                PImageIndices = imageIndices,
                PResults = null
            };

            long presentStart = Stopwatch.GetTimestamp();
            Result presentResult = _swapchain.Present(&presentInfo);
            _lastPresentMicroseconds = ElapsedMicroseconds(presentStart);
            _stallTracker.Record(RuntimeStallReason.Present, _lastPresentMicroseconds, "Present swapchain image");

            if (presentResult != Result.Success &&
                presentResult != Result.ErrorOutOfDateKhr &&
                presentResult != Result.SuboptimalKhr)
            {
                MarkFrameSubmissionFault($"Failed to present swapchain image: {presentResult}.", presentResult);
                throw new VulkanException("Failed to present swapchain image", presentResult);
            }

            if (_productionFrameWasFullQuality)
            {
                lock (_startupGate)
                {
                    _bootstrapPresented = true;
                    _startupScenePresented = true;
                    _fullQualityPresented = true;
                }
            }
            SchedulePostFullQualityPersistence();

            // TriggerCapture queues a single present-delimited frame. Retire
            // the renderer-facing request only after that frame presented.
            _renderDocCaptureService.EndFrame(IntPtr.Zero, IntPtr.Zero);

            // Keep external frame-index consumers on the preferred context;
            // BeginFrame may choose a different already-completed context.
            _currentFrame = _submissionOwnership.PreferredFrameContext;
            _sync.SetCurrentFrame(_currentFrame);
            _temporalSampleIndex++;
            _ddgiFrameSerial++;
            _lifetime.CompleteFrame();

            if (presentResult == Result.ErrorOutOfDateKhr ||
                presentResult == Result.SuboptimalKhr ||
                _lifetime.SwapchainRecreationRequested)
            {
                _lifetime.RequestSwapchainRecreation();
                _lifetime.ObserveSwapchainRecreationAttempt(
                    RecreateSwapchain());
            }

            RefreshValidationDiagnostics();
            _context.ThrowIfValidationFailure();
        }

        private void EndProgressiveFrame()
        {
            var vk = _context.Api;
            if (_swapchainImageTransitionedThisFrame)
            {
                TransitionSwapchainImage(
                    _currentCommandBuffer,
                    ImageLayout.PresentSrcKhr);
            }

            Result result = vk.EndCommandBuffer(_currentCommandBuffer);
            if (result != Result.Success)
            {
                MarkFrameSubmissionFault(
                    "Failed to end progressive-startup command buffer.",
                    result);
                throw new VulkanException(
                    "Failed to end progressive-startup command buffer",
                    result);
            }

            try
            {
                _sync.ResetFence(_currentFrame);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault(
                    "Failed to reset the progressive-startup frame fence.",
                    exception.Result);
                throw;
            }

            Semaphore imageAvailable =
                _sync.GetImageAvailableSemaphore(
                    _currentAcquireSemaphoreIndex);
            Semaphore renderFinished =
                _sync.GetRenderFinishedSemaphoreForImage(_imageIndex);
            PipelineStageFlags waitStage =
                PipelineStageFlags.ColorAttachmentOutputBit;
            CommandBuffer commandBuffer = _currentCommandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished
            };
            result = vk.QueueSubmit(
                _context.GraphicsQueue,
                1,
                &submitInfo,
                _sync.GetInFlightFence(_currentFrame));
            if (result != Result.Success)
            {
                Result recovery = TryRecoverFrameFenceAfterTerminalSubmitFailure(
                    1,
                    &imageAvailable,
                    &waitStage,
                    null,
                    hasTimelineWaits: false);
                string reason =
                    $"Failed to submit progressive-startup frame: {result}; " +
                    $"fence recovery: {recovery}.";
                MarkFrameSubmissionFault(reason, result);
                throw new VulkanException(
                    "Failed to submit progressive-startup frame",
                    result);
            }

            var swapchain = _swapchain.Swapchain;
            uint imageIndex = _imageIndex;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex
            };
            Result presentResult = _swapchain.Present(&presentInfo);
            if (presentResult != Result.Success &&
                presentResult != Result.ErrorOutOfDateKhr &&
                presentResult != Result.SuboptimalKhr)
            {
                MarkFrameSubmissionFault(
                    $"Failed to present progressive-startup frame: {presentResult}.",
                    presentResult);
                throw new VulkanException(
                    "Failed to present progressive-startup frame",
                    presentResult);
            }

            lock (_startupGate)
            {
                _bootstrapPresented = true;
                if (_progressiveFramePhase ==
                        RendererStartupPhase.FullQuality)
                {
                    _startupScenePresented = true;
                    _fullQualityPresented = true;
                }
            }
            SchedulePostFullQualityPersistence();

            _submittedGraphicsFrameFenceValues[_currentFrame] =
                _ddgiFrameSerial == ulong.MaxValue
                    ? ulong.MaxValue
                    : _ddgiFrameSerial + 1UL;
            _submissionOwnership.MarkSubmitted(
                _currentFrame,
                _imageIndex,
                _currentAcquireSemaphoreIndex,
                _submittedGraphicsFrameFenceValues[_currentFrame]);
            _currentFrame = _submissionOwnership.PreferredFrameContext;
            _sync.SetCurrentFrame(_currentFrame);
            _temporalSampleIndex++;
            _ddgiFrameSerial++;
            _progressiveFrame = false;
            _lifetime.CompleteFrame();

            if (presentResult == Result.ErrorOutOfDateKhr ||
                presentResult == Result.SuboptimalKhr)
            {
                _lifetime.RequestSwapchainRecreation();
            }
            RefreshValidationDiagnostics();
            _context.ThrowIfValidationFailure();
        }

        public unsafe void Clear(Color color)
        {
            _lifetime.ThrowIfDisposalStarted();
            _lifetime.EnsureFrameInProgress(nameof(Clear));

            _clearColor = color;
            if (_progressiveFrame)
            {
                RecordProgressiveClear(color);
                return;
            }

            var vk = _context.Api;
            var khrDynamicRendering = _context.KhrDynamicRendering;

            _renderTargets!.SceneColor.TransitionToColorAttachment(_currentCommandBuffer);
            _renderTargets.SceneDepth.TransitionToDepthAttachment(_currentCommandBuffer);

            // After HDR pipeline setup, Clear initializes renderer-owned scene color.
            // The swapchain is written only by ToneMapCompositePass.
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneColor.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(color.R, color.G, color.B, color.A))
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)) // Reverse-Z: clear to 0
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), _renderTargets.SceneColor.Extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            vk.CmdBeginRendering(_currentCommandBuffer, &renderingInfo);
            vk.CmdEndRendering(_currentCommandBuffer);
        }

        private void RecordProgressiveClear(Color color)
        {
            EnsureSwapchainImageColorAttachment(_currentCommandBuffer);
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.ImageViews[_imageIndex],
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(
                    color.R,
                    color.G,
                    color.B,
                    color.A))
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(
                    new Offset2D(0, 0),
                    _swapchain.Extent),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment
            };
            _context.Api.CmdBeginRendering(
                _currentCommandBuffer,
                &renderingInfo);
            _context.Api.CmdEndRendering(_currentCommandBuffer);
        }

        /// <summary>
        /// Starts the optional reflection pipeline family without making the
        /// next scene commit wait on cold driver compilation. Until it is
        /// ready, frame preparation keeps the established probe/environment
        /// fallback active.
        /// </summary>
        public void BeginHybridReflectionPipelinePreparation()
        {
            _lifetime.ThrowIfDisposalStarted();
            HybridReflectionVulkanRuntime runtime =
                _hybridReflectionRuntime ?? throw new InvalidOperationException(
                    "Hybrid reflection runtime is not initialized.");
            _ = runtime.BeginInitializeAsync(
                PrepareHybridReflectionReceiverPipelines);
        }

        /// <summary>
        /// Reserves the hybrid-reflection render-target and forward-pipeline
        /// attachment profile before renderer initialization. Scene hosts that
        /// can activate reflections later use this to keep the attachment ABI
        /// stable across scene switches.
        /// </summary>
        public void ReserveHybridReflectionTargetProfile()
        {
            _lifetime.ThrowIfDisposalStarted();
            if (_renderTargets != null)
            {
                throw new InvalidOperationException(
                    "The hybrid reflection target profile must be reserved before renderer initialization.");
            }

            _hybridReflectionTargetProvisioned = true;
        }

        public Task PrepareEnvironmentResourcesAsync(
            EnvironmentSettings settings,
            CancellationToken cancellationToken = default)
        {
            _lifetime.ThrowIfDisposalStarted();
            ArgumentNullException.ThrowIfNull(settings);
            return _environmentManager?.PrepareResourcesAsync(
                       settings,
                       cancellationToken) ??
                   Task.CompletedTask;
        }

        /// <summary>
        /// Claims the optional reflection family without starting driver work.
        /// The scene-transition host releases the claim after the target's
        /// first present, preventing compiler contention on the critical path.
        /// </summary>
        public void DeferHybridReflectionPipelinePreparation()
        {
            _lifetime.ThrowIfDisposalStarted();
            HybridReflectionVulkanRuntime runtime =
                _hybridReflectionRuntime ?? throw new InvalidOperationException(
                    "Hybrid reflection runtime is not initialized.");
            if (Volatile.Read(
                    ref _hybridReflectionReceiverPipelinesPrepared) == 0)
            {
                runtime.DeferInitialize();
            }
        }

        private bool PrepareHybridReflectionReceiverPipelines()
        {
            // Screen-pipeline publication waits only for the exact opaque and
            // foliage receivers. Cache-specialized lanes preserve identical
            // output but are not quality-critical and are prepared after the
            // first full-quality present in progressive startup.
            if (!TryPrepareHybridReflectionExactReceiverCombination(
                    nearFieldDirectSource: false,
                    giCausticReceiver: false))
            {
                return false;
            }

            bool nearFieldDirectSource =
                _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual &&
                _meshPipeline.NearFieldDirectSourceConfiguration
                    .SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.ForwardMrt;
            bool giCausticReceiver =
                _advancedGiAdmission.GraphModes.UsesCausticWorldCache;
            if (nearFieldDirectSource && giCausticReceiver &&
                !_meshPipeline.CombinedAdvancedGiAttachmentEnabled)
            {
                giCausticReceiver = false;
            }

            if ((nearFieldDirectSource || giCausticReceiver) &&
                !TryPrepareHybridReflectionExactReceiverCombination(
                    nearFieldDirectSource,
                    giCausticReceiver))
            {
                return false;
            }

            Volatile.Write(
                ref _hybridReflectionReceiverPipelinesPrepared,
                1);
            return true;
        }

        private bool TryPrepareHybridReflectionExactReceiverCombination(
            bool nearFieldDirectSource,
            bool giCausticReceiver)
        {
            if (!_meshPipeline.TryPrepareHybridReflectionExactPipelines(
                    nearFieldDirectSource,
                    giCausticReceiver))
            {
                return false;
            }

            return !_foliagePipeline.IsPrepared ||
                _foliagePipeline.TryPrepareHybridReflectionExactPipelines(
                    nearFieldDirectSource,
                    giCausticReceiver);
        }

        private bool PrepareHybridReflectionReceiverPerformancePipelines()
        {
            if (!_meshPipeline.TryPrepareHybridReflectionPerformancePipelines(
                    nearFieldDirectSourceEnabled: false,
                    giCausticReceiverEnabled: false))
            {
                return false;
            }

            bool nearFieldDirectSource =
                _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual &&
                _meshPipeline.NearFieldDirectSourceConfiguration
                    .SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.ForwardMrt;
            bool giCausticReceiver =
                _advancedGiAdmission.GraphModes.UsesCausticWorldCache;
            if (nearFieldDirectSource && giCausticReceiver &&
                !_meshPipeline.CombinedAdvancedGiAttachmentEnabled)
            {
                giCausticReceiver = false;
            }

            if ((nearFieldDirectSource || giCausticReceiver) &&
                !_meshPipeline.TryPrepareHybridReflectionPerformancePipelines(
                    nearFieldDirectSource,
                    giCausticReceiver))
            {
                return false;
            }

            Volatile.Write(
                ref _hybridReflectionReceiverPerformancePipelinesPrepared,
                1);
            return true;
        }

        private bool AreReceiverCachePerformancePipelinesRequested()
        {
            SimpleDdgiReceiverCacheMode requestedMode =
                SimpleDdgiReceiverCachePolicy.ResolveRequestedMode(
                    Settings.GlobalIllumination.SimpleDdgiReceiverCacheMode,
                    Settings.Diagnostics.ForceForwardGiReceiverCacheForBenchmark,
                    Settings.Diagnostics.ForceExactForwardGiGatherForBenchmark);
            return requestedMode.UsesCache();
        }

        private void PrepareHybridReflectionsForFullQuality()
        {
            HybridReflectionVulkanRuntime runtime =
                _hybridReflectionRuntime ?? throw new InvalidOperationException(
                    "Hybrid reflection runtime is not initialized.");

            // BeginInitializeAsync also releases a claim made by
            // DeferInitialize. Exact receiver readiness is the quality gate;
            // the cache-specialized lanes are output-equivalent acceleration
            // paths and may safely retain the exact opaque fallback.
            runtime.BeginInitializeAsync(
                    PrepareHybridReflectionReceiverPipelines)
                .GetAwaiter()
                .GetResult();

            // An already-published runtime does not invoke the callback. Run
            // the idempotent preparation once more so newly prepared foliage
            // families on a scene transition are covered as well.
            if (!PrepareHybridReflectionReceiverPipelines() ||
                !runtime.ScreenPipelinesAvailable)
            {
                throw new InvalidOperationException(
                    "Hybrid reflections were requested, but their exact " +
                    "screen and receiver pipeline bank is unavailable. " +
                    $"Runtime: {runtime.FailureDetail}; mesh: " +
                    $"{_meshPipeline.HybridReflectionFailureReason}; foliage: " +
                    _foliagePipeline.HybridReflectionPipelineFailureReason);
            }

            if (!RendererBuildConfiguration.ProgressivePipelineStartup &&
                !PrepareHybridReflectionReceiverPerformancePipelines())
            {
                throw new InvalidOperationException(
                    "Hybrid reflections were requested in blocking startup, " +
                    "but their cache-specialized receiver pipeline bank is unavailable. " +
                    _meshPipeline.HybridReflectionFailureReason);
            }
        }

        private bool BeginProgressiveFrame()
        {
            if (_lifetime.SwapchainRecreationRequested)
            {
                bool recreated = RecreateProgressiveSwapchain();
                _lifetime.ObserveSwapchainRecreationAttempt(recreated);
                if (!recreated)
                    return false;
            }

            _stallTracker.BeginFrame();
            _currentAcquireSemaphoreIndex =
                _submissionOwnership.SelectAcquireSemaphore();
            long acquireStart = Stopwatch.GetTimestamp();
            Result acquireResult = _swapchain.TryAcquireNextImage(
                _sync.GetImageAvailableSemaphore(
                    _currentAcquireSemaphoreIndex),
                out _imageIndex);
            _lastAcquireImageMicroseconds =
                ElapsedMicroseconds(acquireStart);
            _stallTracker.Record(
                RuntimeStallReason.SwapchainAcquire,
                _lastAcquireImageMicroseconds,
                "Acquire progressive-startup swapchain image");
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                _lifetime.RequestSwapchainRecreation();
                _lifetime.ObserveSwapchainRecreationAttempt(
                    RecreateProgressiveSwapchain());
                return false;
            }
            if (acquireResult != Result.Success &&
                acquireResult != Result.SuboptimalKhr)
            {
                if (acquireResult == Result.ErrorDeviceLost)
                {
                    MarkFrameSubmissionFault(
                        "The Vulkan device was lost while acquiring a progressive-startup frame.",
                        acquireResult);
                }
                throw new VulkanException(
                    "Failed to acquire progressive-startup swapchain image",
                    acquireResult);
            }
            if (acquireResult == Result.SuboptimalKhr)
                _lifetime.RequestSwapchainRecreation();

            WaitForAcquiredSwapchainImageOwner();
            SelectAndRecycleFrameResourceContext();
            _deleter.ProcessCompletedFrame(
                _sync.GetInFlightFence(_currentFrame));
            _context.SetAllocatorCurrentFrameIndex(_allocatorFrameIndex++);

            _cmd.ResetGraphicsCommandBuffer(_currentFrame);
            _currentCommandBuffer =
                _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
            _swapchainImageTransitionedThisFrame = false;
            lock (_startupGate)
                _progressiveFramePhase = _startupPhase;
            _progressiveFrame = true;
            _lifetime.MarkFrameStarted();

            // Guarantee that even a host which has not issued Draw yet owns a
            // valid, visibly responsive present.
            RecordProgressiveClear(CreateAnimatedStartupClear());
            return true;
        }

        private Color CreateAnimatedStartupClear()
        {
            long started;
            lock (_startupGate)
                started = _startupStartedTimestamp;
            double seconds = started == 0
                ? 0.0
                : Stopwatch.GetElapsedTime(started).TotalSeconds;
            float pulse = 0.5f +
                0.5f * (float)Math.Sin(seconds * 1.35);
            float value = 0.008f + 0.012f * pulse;
            return new Color(
                value * 0.72f,
                value * 0.86f,
                value,
                1.0f);
        }

        private bool RecreateProgressiveSwapchain()
        {
            _lifetime.EnsureSwapchainRecreationAllowed();
            bool recreated = _swapchain.RecreateSwapchain(() =>
                RecordDeviceWaitIdle(
                    RuntimeStallReason.ResourceResize,
                    "Progressive startup swapchain recreate",
                    _context.WaitIdle));
            if (recreated)
            {
                _submissionOwnership.ResetAfterDeviceIdle(
                    checked((int)_swapchain.ImageCount));
                _sync.EnsureRenderFinishedSemaphoreCapacity(
                    _swapchain.ImageCount);
            }
            return recreated;
        }

        public void PrepareScene(Scene scene, ICamera camera)
        {
            _lifetime.ThrowIfDisposalStarted();
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(camera);

            if (RendererBuildConfiguration.ProgressivePipelineStartup)
            {
                BeginProductionPreparation();
                StartProgressiveProductionInitialization();
            }

            Task? initialization = _productionInitializationTask;
            if (initialization != null)
                initialization.GetAwaiter().GetResult();
            ThrowIfProgressiveStartupFaulted();
            PrepareSceneCore(scene, camera);
            PublishFullQualityStartup();
        }

        public Task PrepareSceneAsync(
            Scene scene,
            ICamera camera,
            CancellationToken cancellationToken = default)
        {
            _lifetime.ThrowIfDisposalStarted();
            ArgumentNullException.ThrowIfNull(scene);
            ArgumentNullException.ThrowIfNull(camera);

            if (RendererBuildConfiguration.ProgressivePipelineStartup)
            {
                BeginProductionPreparation();
                StartProgressiveProductionInitialization();
            }

            Task preparationTask = ProgressiveRendererStartupTask.RunAsync(
                _productionInitializationTask,
                () =>
                {
                    ThrowIfProgressiveStartupFaulted();
                    PrepareSceneCore(scene, camera);
                },
                PublishFullQualityStartup,
                cancellationToken);
            lock (_startupGate)
                _scenePreparationTask = preparationTask;
            return preparationTask;
        }

        private void PrepareSceneCore(Scene scene, ICamera camera)
        {
            bool exhaustive = RendererBuildConfiguration.PipelineStartupMode ==
                              RendererPipelineStartupMode.Exhaustive;
            ScenePipelineManifest pipelineManifest = exhaustive
                ? new ScenePipelineManifest(
                    SceneMaterialPipelineKinds.Masked |
                    SceneMaterialPipelineKinds.OrdinaryTransparent |
                    SceneMaterialPipelineKinds.ThinGlass |
                    SceneMaterialPipelineKinds.GeometryDecal |
                    SceneMaterialPipelineKinds.ThickTransmission,
                    HasRealTransparentShadowReceiver: true,
                    HasGeometryDecalShadowReceiver: true,
                    HasTransparentReflectionReceiver: true)
                : BuildScenePipelineManifest(scene);
            bool receiverFeedbackRequired =
                _simpleDdgiReceiverFeedback?.GraphicsPipelinesRequested == true;
            bool transparentRayVariantsRequired =
                pipelineManifest.HasRealTransparentSurface &&
                Settings.Transparency.Enabled &&
                (Settings.Transparency.ReceiveShadows &&
                 pipelineManifest.HasRealTransparentShadowReceiver ||
                 pipelineManifest.Requires(
                     SceneMaterialPipelineKinds.ThickTransmission) &&
                 Settings.Transparency.ThickTransmissionMode ==
                     ThickTransmissionMode.RayQuery ||
                 Settings.Transparency.SampleReflections &&
                 pipelineManifest.HasTransparentReflectionReceiver &&
                 Settings.Reflections.Enabled &&
                 Settings.Reflections.Mode == ReflectionMode.HybridRayQuery);
            bool decalRayVariantsRequired =
                pipelineManifest.HasGeometryDecalShadowReceiver &&
                Settings.Transparency.Enabled &&
                Settings.Decals.ReceiveShadows;
            TransparencyMode transparencyMode =
                Settings.Transparency.Mode;
            bool partitioningEnabled =
                Settings.Transparency.Enabled &&
                Settings.Transparency.PipelinePartitioningEnabled;
            bool rayVariantsRequired =
                _context.RayQuerySupported &&
                (transparentRayVariantsRequired ||
                 decalRayVariantsRequired);
            bool decalReceiverCacheRequired =
                receiverFeedbackRequired &&
                Settings.Decals.ReceiveGlobalIllumination;
            bool deferPostFirstPresentSpecializations =
                RendererBuildConfiguration.ProgressivePipelineStartup &&
                !exhaustive;
            bool initialScenePreparation =
                !_initialScenePipelinesPrepared;
            if (initialScenePreparation)
            {
                lock (_startupGate)
                {
                    _postFirstPresentPipelinePreparation = null;
                    _postFirstPresentPipelinePreparationScheduled = false;
                    _postFirstPresentPipelinePreparationGeneration = unchecked(
                        _postFirstPresentPipelinePreparationGeneration + 1);
                }
                Volatile.Write(
                    ref _postFirstPresentPipelineSpecializationsReady,
                    deferPostFirstPresentSpecializations ? 0 : 1);
            }
            bool firstPresentCriticalOnly =
                deferPostFirstPresentSpecializations &&
                Volatile.Read(
                    ref _postFirstPresentPipelineSpecializationsReady) == 0;

            if (receiverFeedbackRequired &&
                !deferPostFirstPresentSpecializations)
            {
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.DdgiReceiverFeedback",
                    _simpleDdgiReceiverFeedback!.PreparePipelines);
            }

            bool directionalGuidingRequired =
                Settings.GlobalIllumination
                    .SimpleDdgiDirectionalGuidingMode !=
                    SimpleDdgiDirectionalGuidingMode.Off &&
                _advancedGiAdmission.GraphModes.UsesDirectionalGuiding;
            if (directionalGuidingRequired)
            {
                SimpleDdgiStoragePackingMode storagePackingMode =
                    Settings.GlobalIllumination.SimpleDdgiStoragePackingMode
                        .Sanitize();
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.DdgiDirectionalGuiding",
                    () => _simpleDdgiGuidingRuntime!.PreparePipelines(
                        storagePackingMode));
            }

            _lifetime.RunStartupStep(
                "Pipeline.Prepare.FirstPresentForwardOpaque",
                _meshPipeline.PrepareFirstPresentForwardOpaquePipeline);

            ScenePipelinePreparationScope preparationScope =
                firstPresentCriticalOnly
                    ? ScenePipelinePreparationScope.FirstPresentCritical
                    : ScenePipelinePreparationScope.Complete;
            _lifetime.RunStartupStep(
                "Pipeline.Prepare.SceneManifest",
                () => _meshPipeline.PrepareScenePipelineManifest(
                    pipelineManifest,
                    transparencyMode,
                    partitioningEnabled,
                    receiverFeedbackRequired,
                    rayVariantsRequired,
                    decalReceiverCacheRequired,
                    preparationScope));

            bool foliageRequired = exhaustive ||
                                   Settings.Foliage.Enabled &&
                                   scene.FoliagePatches.Count > 0 &&
                                   scene.FoliagePrototypes.Count > 0;
            if (foliageRequired && !_foliagePipeline.IsPrepared)
            {
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.Foliage",
                    _foliagePipeline.Prepare);
            }

            CollectRequiredParticleBlendModes(
                scene,
                _particleBlendModeScratch);
            bool particlePreparationRequired = exhaustive
                ? !_particlePipeline.IsPrepared
                : Settings.Particles.Enabled &&
                  _particleBlendModeScratch.Count > 0 &&
                  _particlePipeline.RequiresPreparation(
                      _particleBlendModeScratch);
            if (particlePreparationRequired)
            {
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.Particle",
                    () =>
                    {
                        if (exhaustive)
                            _particlePipeline.PrepareAll();
                        else
                            _particlePipeline.Prepare(
                                _particleBlendModeScratch);
                    });
            }

            bool fogRequired = exhaustive ||
                               Settings.Fog.Enabled &&
                               Settings.Fog.Mode != FogMode.Disabled;
            if (fogRequired && !_fogPass.IsPrepared)
            {
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.Fog",
                    _fogPass.PreparePipelines);
            }

            bool hybridReflectionsRequested =
                Settings.Reflections.Enabled &&
                Settings.Reflections.Mode is
                    (ReflectionMode.StaticProbesAndSsr or
                     ReflectionMode.StaticProbesAndPlanar or
                     ReflectionMode.HybridRayQuery);
            bool hybridReflectionsRequired = exhaustive ||
                hybridReflectionsRequested;
            if (hybridReflectionsRequested &&
                !_meshPipeline.HybridReflectionAttachmentEnabled)
            {
                throw new InvalidOperationException(
                    "Hybrid reflections were requested without a valid " +
                    "forward receiver attachment: " +
                    _meshPipeline.HybridReflectionFailureReason);
            }
            if (hybridReflectionsRequired &&
                _meshPipeline.HybridReflectionAttachmentEnabled)
            {
                _lifetime.RunStartupStep(
                    "Pipeline.Prepare.HybridReflections.FullQuality",
                    PrepareHybridReflectionsForFullQuality);
            }

            if (initialScenePreparation && firstPresentCriticalOnly)
            {
                Action preparation = () =>
                    PreparePostFirstPresentPipelineBank(
                        pipelineManifest,
                        transparencyMode,
                        partitioningEnabled,
                        receiverFeedbackRequired,
                        rayVariantsRequired,
                        decalReceiverCacheRequired,
                        foliageRequired,
                        particlePreparationRequired,
                        fogRequired,
                        hybridReflectionsRequired);
                lock (_startupGate)
                    _postFirstPresentPipelinePreparation = preparation;
            }
            else if (receiverFeedbackRequired)
            {
                bool complete =
                    (_forwardPlusPass?.SimpleDdgiReceiverPipelineBankReady ??
                     false) &&
                    AreSceneReceiverFeedbackPipelinesReady(
                        pipelineManifest,
                        transparencyMode,
                        rayVariantsRequired,
                        foliageRequired,
                        particlePreparationRequired,
                        fogRequired);
                _simpleDdgiReceiverFeedback!.PublishPipelineBank(
                    complete,
                    complete
                        ? "receiver-feedback-pipeline-bank-ready"
                        : "receiver-feedback-pipeline-bank-incomplete");
            }

            if (!_initialScenePipelinesPrepared)
            {
                _initialScenePipelinesPrepared = true;
            }
        }

        private void PreparePostFirstPresentPipelineBank(
            ScenePipelineManifest pipelineManifest,
            TransparencyMode transparencyMode,
            bool partitioningEnabled,
            bool receiverFeedbackRequired,
            bool rayVariantsRequired,
            bool decalReceiverCacheRequired,
            bool foliageRequired,
            bool particlePreparationRequired,
            bool fogRequired,
            bool hybridReflectionsRequired)
        {
            bool hybridReceiverPerformanceRequired =
                hybridReflectionsRequired &&
                _meshPipeline.HybridReflectionAttachmentEnabled &&
                AreReceiverCachePerformancePipelinesRequested();
            bool hybridReady = !hybridReceiverPerformanceRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentHybridReflectionSpecializations",
                    PrepareHybridReflectionReceiverPerformancePipelines);

            bool meshReady = TryPreparePostFirstPresentFamily(
                "Pipeline.Prepare.PostFirstPresentSpecializations",
                () =>
                {
                    _meshPipeline.PrepareScenePipelineManifest(
                        pipelineManifest,
                        transparencyMode,
                        partitioningEnabled,
                        receiverFeedbackRequired,
                        rayVariantsRequired,
                        decalReceiverCacheRequired,
                        ScenePipelinePreparationScope.Complete);
                    return true;
                });
            if (meshReady)
            {
                // Scene-specialized variants are independent of receiver-cache
                // publication. Make them available immediately so a slow B1
                // native compile cannot hold transparent partitioning and the
                // other post-present paths behind it.
                Volatile.Write(
                    ref _postFirstPresentPipelineSpecializationsReady,
                    1);
            }
            bool foliageReady = !receiverFeedbackRequired ||
                !foliageRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentFoliageFeedback",
                    _foliagePipeline.PrepareReceiverFeedbackPipelines);
            bool particleReady = !receiverFeedbackRequired ||
                !particlePreparationRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentParticleFeedback",
                    _particlePipeline.PrepareReceiverFeedbackPipelines);
            bool fogReady = !receiverFeedbackRequired ||
                !fogRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentFogFeedback",
                    _fogPass.PrepareReceiverFeedbackPipeline);
            bool runtimeReady = !receiverFeedbackRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentReceiverRuntime",
                    () =>
                    {
                        _simpleDdgiReceiverFeedback!.PreparePipelines();
                        return true;
                    });

            // Build the historically pathological B1/adaptive compute family
            // last. Unrelated scene specializations are therefore usable even
            // when a native driver spends minutes compiling this bank.
            bool forwardReady = !receiverFeedbackRequired ||
                TryPreparePostFirstPresentFamily(
                    "Pipeline.Prepare.PostFirstPresentReceiverComputeBank",
                    () => _forwardPlusPass?
                        .PrepareSimpleDdgiReceiverPipelineBank() == true);

            bool complete = receiverFeedbackRequired &&
                hybridReady && meshReady && foliageReady && particleReady && fogReady &&
                runtimeReady && forwardReady &&
                AreSceneReceiverFeedbackPipelinesReady(
                    pipelineManifest,
                    transparencyMode,
                    rayVariantsRequired,
                    foliageRequired,
                    particlePreparationRequired,
                    fogRequired);
            if (receiverFeedbackRequired)
            {
                _simpleDdgiReceiverFeedback!.PublishPipelineBank(
                    complete,
                    complete
                        ? "receiver-feedback-pipeline-bank-ready"
                        : "receiver-feedback-pipeline-bank-incomplete");
            }
        }

        private bool TryPreparePostFirstPresentFamily(
            string stepName,
            Func<bool> prepare)
        {
            try
            {
                bool prepared = false;
                _lifetime.RunStartupStep(
                    stepName,
                    () => prepared = prepare());
                return prepared;
            }
            catch (Exception exception) when (
                exception is VulkanException or IOException or
                    InvalidOperationException or ArgumentException or
                    OverflowException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"{stepName} failed; exact canonical rendering retained: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private bool AreSceneReceiverFeedbackPipelinesReady(
            ScenePipelineManifest manifest,
            TransparencyMode transparencyMode,
            bool rayVariantsRequired,
            bool foliageRequired,
            bool particlePreparationRequired,
            bool fogRequired)
        {
            bool maskedReady =
                !manifest.Requires(SceneMaterialPipelineKinds.Masked) ||
                _meshPipeline.AlphaMaskReceiverFeedbackPipelinesAvailable;
            bool transparentReady = !manifest.HasTransparentSurface ||
                (transparencyMode == TransparencyMode.WeightedBlendedOit
                    ? _meshPipeline.WeightedOitReceiverFeedbackPipeline.Handle != 0
                    : _meshPipeline.TransparentReceiverFeedbackPipeline.Handle != 0 &&
                      (!manifest.Requires(SceneMaterialPipelineKinds.ThinGlass) ||
                       _meshPipeline.ThinGlassReceiverFeedbackPipeline.Handle != 0));
            bool rayFeedbackRequired = rayVariantsRequired &&
                !manifest.Requires(
                    SceneMaterialPipelineKinds.ThickTransmission);
            bool rayReady = !rayFeedbackRequired ||
                (transparencyMode == TransparencyMode.WeightedBlendedOit
                    ? _meshPipeline.RayWeightedOitReceiverFeedbackPipeline.Handle != 0
                    : _meshPipeline.RayTransparentReceiverFeedbackPipeline.Handle != 0);
            return maskedReady && transparentReady && rayReady &&
                (!foliageRequired ||
                 _foliagePipeline.ReceiverFeedbackPipelinesAvailable) &&
                (!particlePreparationRequired ||
                 _particlePipeline.ReceiverFeedbackPipelinesAvailable) &&
                (!fogRequired ||
                 _fogPass.ReceiverFeedbackPipelineAvailable);
        }

        private void MarkPipelineCacheRenderCriticalFramesStarted()
        {
            GiPipelineCacheService? pipelineCache = _giPipelineCacheService;
            int renderThreadId = Volatile.Read(ref _renderThreadManagedId);
            if (pipelineCache == null || renderThreadId <= 0 ||
                Volatile.Read(
                    ref _pipelineCacheRenderCriticalFramesStarted) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _pipelineCacheRenderCriticalFramesStarted,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                pipelineCache.MarkRenderCriticalFramesStarted(renderThreadId);
            }
            catch
            {
                Volatile.Write(
                    ref _pipelineCacheRenderCriticalFramesStarted,
                    0);
                throw;
            }
        }

        private void PublishFullQualityStartup()
        {
            Volatile.Write(ref _productionGraphReady, 1);
            lock (_startupGate)
            {
                if (_startupPhase != RendererStartupPhase.FullQuality)
                {
                    SetStartupPhaseLocked(
                        RendererStartupPhase.FullQuality,
                        "The active scene is running on the production render graph.");
                }
            }
        }

        private void SchedulePostFullQualityPersistence()
        {
            bool fullQualityPresented;
            lock (_startupGate)
                fullQualityPresented = _fullQualityPresented;
            if (!fullQualityPresented)
                return;

            GiPipelineCacheService? pipelineCache =
                _giPipelineCacheService;
            if (pipelineCache is not null)
            {
                SchedulePostFirstPresentPipelinePreparation(
                    pipelineCache);
            }
            if (pipelineCache is not null &&
                Volatile.Read(
                    ref _postFirstPresentPipelineSpecializationsReady) != 0 &&
                !_fullQualityCachePersistenceScheduled)
            {
                pipelineCache.SchedulePersist(immediate: true);
                _fullQualityCachePersistenceScheduled = true;
            }
            if (pipelineCache is not null &&
                !_pipelineCachePersistenceScheduled)
            {
                _performanceCaptureMetadataProvider
                    .SchedulePostStartupIdentityResolution();
                _pipelineCachePersistenceScheduled = true;
            }
        }

        private void SchedulePostFirstPresentPipelinePreparation(
            GiPipelineCacheService pipelineCache)
        {
            ArgumentNullException.ThrowIfNull(pipelineCache);
            Action? preparation;
            int generation;
            lock (_startupGate)
            {
                if (_postFirstPresentPipelinePreparationScheduled)
                    return;

                preparation = _postFirstPresentPipelinePreparation;
                if (preparation is null)
                {
                    Volatile.Write(
                        ref _postFirstPresentPipelineSpecializationsReady,
                        1);
                    return;
                }

                _postFirstPresentPipelinePreparationScheduled = true;
                generation =
                    _postFirstPresentPipelinePreparationGeneration;
            }

            pipelineCache.CompilationScheduler.Schedule(
                new PipelineArtifactId(
                    "MeshPipeline.PostFirstPresent.SceneSpecializations." +
                    generation),
                _ =>
                {
                    try
                    {
                        preparation();
                        Volatile.Write(
                            ref _postFirstPresentPipelineSpecializationsReady,
                            1);
                        pipelineCache.SchedulePersist();
                    }
                    catch (Exception exception) when (
                        exception is VulkanException or IOException or
                            InvalidOperationException or ArgumentException)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "Post-first-present pipeline specialization " +
                            $"failed: {exception.GetType().Name}: " +
                            exception.Message);
                    }
                });
        }

        private void ThrowIfProgressiveStartupFaulted()
        {
            Exception? failure;
            lock (_startupGate)
                failure = _startupFailure;
            if (failure != null)
            {
                throw new InvalidOperationException(
                    "Progressive production renderer initialization failed.",
                    failure);
            }
        }

        private ScenePipelineManifest BuildScenePipelineManifest(Scene scene)
        {
            ScenePipelineManifest manifest = ScenePipelineManifest.Empty;
            const SceneMaterialPipelineKinds complete =
                SceneMaterialPipelineKinds.Masked |
                SceneMaterialPipelineKinds.OrdinaryTransparent |
                SceneMaterialPipelineKinds.ThinGlass |
                SceneMaterialPipelineKinds.GeometryDecal |
                SceneMaterialPipelineKinds.ThickTransmission;

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible)
                    continue;
                manifest = IncludeSceneMaterial(
                    manifest,
                    renderObject.Material,
                    renderObject.Name);
                if (manifest.MaterialKinds == complete &&
                    manifest.HasRealTransparentShadowReceiver &&
                    manifest.HasGeometryDecalShadowReceiver &&
                    manifest.HasTransparentReflectionReceiver)
                    return manifest;
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                if (!batch.Visible)
                    continue;
                manifest = IncludeSceneMaterial(
                    manifest,
                    batch.Material,
                    batch.Name);
                if (manifest.MaterialKinds == complete &&
                    manifest.HasRealTransparentShadowReceiver &&
                    manifest.HasGeometryDecalShadowReceiver &&
                    manifest.HasTransparentReflectionReceiver)
                    break;
            }

            return manifest;
        }

        private ScenePipelineManifest IncludeSceneMaterial(
            ScenePipelineManifest manifest,
            object? material,
            string objectName)
        {
            MaterialHandle handle =
                SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    material,
                    _materialManager.DefaultMaterialHandle,
                    objectName);
            return manifest.Include(
                _materialManager.GetMaterialMetadata(handle));
        }

        public void ReportFirstPresent(long elapsedMicroseconds)
        {
            ReportStartupMilestone(
                RendererStartupMilestone.BootstrapPresent,
                elapsedMicroseconds);
        }

        public void ReportStartupMilestone(
            RendererStartupMilestone milestone,
            long elapsedMicroseconds)
        {
            RendererStartupLatencyGateMode gateMode =
                RendererBuildConfiguration.StartupLatencyGateMode;
            if (gateMode == RendererStartupLatencyGateMode.Disabled)
                return;

            GiPipelineCacheTelemetry cacheTelemetry =
                _giPipelineCacheService?.Telemetry ??
                GiPipelineCacheTelemetry.Empty;
            bool deploymentSeed =
                cacheTelemetry.QualifiedSeedEligible;
            RendererStartupMilestoneLatencyEvaluation evaluation =
                RendererStartupLatencyPolicy.EvaluateMilestone(
                    milestone,
                    elapsedMicroseconds,
                    cacheTelemetry.WarmEligible,
                    deploymentSeed);
            string milestoneName = milestone switch
            {
                RendererStartupMilestone.BootstrapPresent =>
                    "responsive bootstrap present",
                RendererStartupMilestone.ScenePresent =>
                    "simplified real-scene present",
                RendererStartupMilestone.FullQualityPresent =>
                    "production-graph scene present",
                RendererStartupMilestone.VisibleContentPresent =>
                    "visually qualified final frame",
                _ => "unknown startup milestone"
            };
            string cacheClass = cacheTelemetry.WarmEligible
                ? "warm application cache"
                : deploymentSeed
                    ? "compatible deployment seed"
                    : DescribeApplicationColdCache(cacheTelemetry);
            string outcome = !evaluation.GateApplies
                ? "control-plane timing recorded; the visible-content gate is authoritative"
                : evaluation.MeetsAspirationalTarget
                ? "target met"
                : evaluation.MeetsHardLimit
                    ? "above target"
                    : gateMode == RendererStartupLatencyGateMode.TimingOnly
                        ? "hard limit exceeded; reporting only"
                        : "hard limit exceeded";
            string budget = evaluation.GateApplies
                ? $"target <= {evaluation.AspirationalTargetMicroseconds / 1_000_000.0:F3}s, " +
                  $"hard <= {evaluation.HardLimitMicroseconds / 1_000_000.0:F3}s"
                : "no control-plane hard gate";
            string message =
                $"Startup latency ({milestoneName}): " +
                $"{evaluation.ElapsedMicroseconds / 1_000_000.0:F3}s " +
                $"({cacheClass}; {budget}; " +
                $"gate={DescribeStartupLatencyGateMode(gateMode)}; {outcome}).";
            Console.WriteLine(message);

            if (RendererStartupLatencyPolicy.ShouldFail(
                    evaluation,
                    gateMode))
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string DescribeStartupLatencyGateMode(
            RendererStartupLatencyGateMode gateMode) => gateMode switch
        {
            RendererStartupLatencyGateMode.Disabled => "off",
            RendererStartupLatencyGateMode.TimingOnly => "timing",
            RendererStartupLatencyGateMode.Enforce => "enforce",
            _ => "unknown"
        };

        private static string DescribeApplicationColdCache(
            in GiPipelineCacheTelemetry telemetry)
        {
            if (!telemetry.RuntimeCacheLoaded)
                return "application-cold cache";
            if (telemetry.LegacyEnvelopeLoaded)
            {
                return telemetry.ShaderBundleChanged
                    ? "application-cold legacy cache from another shader bundle"
                    : "application-cold legacy cache";
            }
            if (telemetry.ShaderBundleChanged &&
                telemetry.BuildConfigurationChanged)
            {
                return "application-cold cache from another shader bundle and " +
                       "build configuration";
            }
            if (telemetry.ShaderBundleChanged)
            {
                return "application-cold cache from another shader bundle";
            }
            if (telemetry.BuildConfigurationChanged)
            {
                return "application-cold cache from another build configuration";
            }
            return "application-cold cache with incomplete provenance";
        }

        private static void CollectRequiredParticleBlendModes(
            Scene scene,
            HashSet<ParticleBlendMode> destination)
        {
            destination.Clear();
            foreach (ParticleEffectInstance instance in scene.ParticleEffects)
            {
                if (!instance.Visible)
                    continue;

                foreach (ParticleEmitterDefinition emitter in
                         instance.Effect.Emitters)
                {
                    destination.Add(emitter.Material.BlendMode);
                }
                foreach (TrailDefinition trail in instance.Effect.Trails)
                    destination.Add(trail.Material.BlendMode);
                foreach (BeamDefinition beam in instance.Effect.Beams)
                    destination.Add(beam.Material.BlendMode);
            }
        }

        public void DrawScene(Scene scene, ICamera camera)
        {
            _lifetime.ThrowIfDisposalStarted();
            _lifetime.EnsureFrameInProgress(nameof(DrawScene));

            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            if (_progressiveFrame)
            {
                // BeginProgressiveFrame already recorded the neutral clear.
                // Do not draw a visibly incorrect approximation of the scene.
                return;
            }

            long drawSceneStart = Stopwatch.GetTimestamp();
            long drawStageStart = drawSceneStart;

            PrepareScene(scene, camera);
            long prepareSceneMicroseconds =
                ElapsedMicroseconds(drawStageStart);

            drawStageStart = Stopwatch.GetTimestamp();
            _materialManager.EnsureTextureFanoutReady();
            long textureFanoutMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            drawStageStart = Stopwatch.GetTimestamp();

            bool debugEnabled = Settings.Debug.Enabled;
            RenderFeatureIsolationMode isolationMode = Settings.FeatureIsolation;
            bool isolateSkinnedAnimationDebug = Settings.Animation.DebugView == AnimationDebugView.SkinnedObjects;
            bool shadowsAllowed = !isolateSkinnedAnimationDebug &&
                                  RenderFeatureIsolationPolicy.AllowsShadows(isolationMode);
            bool reflectionsAllowed = !isolateSkinnedAnimationDebug &&
                                      RenderFeatureIsolationPolicy.AllowsReflections(isolationMode);
            bool animationAllowed = RenderFeatureIsolationPolicy.AllowsAnimation(isolationMode);
            bool particlesAllowed = !isolateSkinnedAnimationDebug &&
                                    RenderFeatureIsolationPolicy.AllowsParticles(isolationMode);
            if (!reflectionsAllowed)
                _hybridReflectionRuntime?.InvalidateHistory();
            // Apply the independently persisted material-transport rollout switch before
            // SceneDataBuilder snapshots revisions or uploads the material buffer. The
            // manager makes an unchanged value a cheap no-op and publishes a transition
            // atomically when the switch changes at runtime.
            _materialManager.SetTransportV2Enabled(
                Settings.GlobalIllumination.EffectiveGiMaterialTransportV2);
            DebugOverlayMode requestedDebugOverlay = debugEnabled
                ? Settings.Debug.Mode
                : DebugOverlayMode.None;
            _sceneDataBuilder.CaptureCpuSnapshots = debugEnabled &&
                                                    (Settings.Debug.CpuSnapshotsEnabled ||
                                                     DebugOverlayCatalog.RequiresCpuSnapshots(
                                                         requestedDebugOverlay));
            _debugOverlayBuilder.ConfigureDrawList(
                debugEnabled,
                Settings.Debug.MaxDebugLineSegments);

            _environmentManager?.UpdateFrameLighting(_lightManager);
            if (_environmentManager != null)
            {
                bool simpleDdgiConsumesAtmosphere =
                    Settings.GlobalIllumination.EffectiveUseDdgi &&
                    _simpleDdgiVolumeManager is { TransportV2Active: true };
                GiAtmosphereCohortFeedback cohort = simpleDdgiConsumesAtmosphere
                    ? _simpleDdgiVolumeManager!.CreateAtmosphereCohortFeedback()
                    : new GiAtmosphereCohortFeedback(
                        ConsumesSteppedAtmosphere: false,
                        ParticipatingProbeCount: 0,
                        SourceCohortActive: false,
                        StaleParticipatingProbeCount: 0);
                _environmentManager.ApplyGiAtmosphereAdmission(
                    cohort,
                    hardInvalidation: false,
                    currentVolumeResourceGeneration: simpleDdgiConsumesAtmosphere
                        ? _simpleDdgiVolumeManager!.TransportTopologyGeneration
                        : 0U,
                    currentSourceCohortGeneration: simpleDdgiConsumesAtmosphere
                        ? _simpleDdgiVolumeManager!.SourceLightingGeneration
                        : 0U,
                    currentPropagationGeneration: simpleDdgiConsumesAtmosphere
                        ? _simpleDdgiVolumeManager!.TransportGeneration
                        : 0U);
            }

            _lightManager.UploadToGPU(_stagingRing, _currentCommandBuffer);
            ulong lightUploadBytes = _lightManager.LastUploadBytes;
            LightFrameSnapshot lightSnapshot = _lightManager.GetFrameSnapshot();
            _simpleDdgiLightTreeResources?.Prepare(lightSnapshot);
            int lightCount = lightSnapshot.Count;
            int directionalLightCount = lightSnapshot.DirectionalLightCount;
            int localLightCount = lightSnapshot.LocalLightCount;
            LocalShadowSelection localShadowSelection;
            bool hasLocalShadows;
            GPUShadowData shadowData = default;
            bool directionalShadowsEnabled = false;
            int shadowedDirectionalLightIndex = -1;
            if (shadowsAllowed)
            {
                localShadowSelection = _localShadowSelector.Select(
                    lightSnapshot.Lights.Span,
                    camera,
                    Settings.Shadows,
                    Settings.Shadows.SpotShadowAtlasCapacity,
                    Settings.Shadows.MaxShadowedPointLights);
                EnsureLocalShadowResources(
                    localShadowSelection.SpotLights.Length,
                    localShadowSelection.PointLights.Length);
                hasLocalShadows = localShadowSelection.SpotLights.Length > 0 ||
                                  localShadowSelection.PointLights.Length > 0;
                shadowData = CreateDirectionalShadowData(camera, lightSnapshot, out directionalShadowsEnabled,
                    out shadowedDirectionalLightIndex);
            }
            else
            {
                localShadowSelection = new LocalShadowSelection();
                hasLocalShadows = false;
                // Feature isolation must not retain maps that no pass can sample. Re-registering
                // after a release also prevents a descriptor from referencing a destroyed image.
                EnsureLocalShadowResources(0, 0);
                EnsureDirectionalShadowResources(requiresShadowMap: false);
            }

            GPUShadowData? enabledShadowData = directionalShadowsEnabled ? shadowData : null;
            int enabledShadowCascadeCount = directionalShadowsEnabled ? Settings.Shadows.DirectionalCascadeCount : 0;

            Vector2 jitter = AntiAliasingJitter.GetHaltonJitter(
                checked((int)_temporalSampleIndex),
                Settings.AntiAliasing.JitterSampleCount,
                _swapchain.Extent.Width,
                _swapchain.Extent.Height,
                Settings.AntiAliasing.JitterEnabled && Settings.AntiAliasing.Mode == AntiAliasingMode.Taa);

            bool gpuSkinningEnabled = animationAllowed &&
                                      Settings.Animation.Enabled &&
                                      Settings.Animation.SkinningMode == AnimationSkinningMode.GpuCompute;
            SkinningFrameStats skinningStats = _skinningManager.PrepareFrame(
                scene,
                _currentCommandBuffer,
                gpuSkinningEnabled,
                Settings.Animation.MaxAnimatedInstances);

            bool sceneGpuLodSelectionActive =
                Settings.SceneSubmission.GpuCompactionEnabled &&
                Settings.SceneSubmission.GpuLodSelectionEnabled;
            bool sceneGpuShadowCompactionActive =
                Settings.SceneSubmission.GpuCompactionEnabled &&
                Settings.SceneSubmission.GpuShadowCompactionEnabled;
            bool captureSceneSubmissionValidationLists =
                Settings.SceneSubmission.ValidationCompareCpuGpuLists ||
                Settings.HiZOcclusion.ValidateAgainstLegacyPath;
            bool buildGpuInstanceCandidates =
                Settings.SceneSubmission.GpuCompactionEnabled &&
                SceneSubmissionDiagnosticsPolicy.BuildPreviousFailureReason(
                    _completedSceneSubmissionCounters,
                    _completedSceneSubmissionValidation,
                    captureSceneSubmissionValidationLists).Length == 0;
            uint forwardDebugViewMode = ResolveForwardDebugViewMode();
            bool geometryDecalsEnabled =
                Settings.Decals.GeometryDecalsEnabled &&
                ShouldRenderGeometryDecals(forwardDebugViewMode);

            // Build and upload scene data using SceneDataBuilder
            long preSceneBuildMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            long sceneBuildCallStart = Stopwatch.GetTimestamp();
            var sceneData = _sceneDataBuilder.Build(
                scene,
                camera,
                _lastSceneRenderExtent.Width,
                _lastSceneRenderExtent.Height,
                _currentCommandBuffer,
                useTiledLightCulling: localLightCount > 0,
                directionalShadowData: enabledShadowData,
                directionalShadowCascadeCount: enabledShadowCascadeCount,
                buildLocalShadowMeshlets: hasLocalShadows,
                selectedPointShadows: localShadowSelection.PointLights,
                projectionJitter: jitter,
                transparencySettings: Settings.Transparency,
                decalSettings: Settings.Decals,
                geometryDecalsEnabled: geometryDecalsEnabled,
                useCameraDependentCpuPayload: Settings.UseCameraDependentCpuScenePayload &&
                                              !sceneGpuLodSelectionActive &&
                                              !sceneGpuShadowCompactionActive,
                useCpuMeshletFrustumCulling: Settings.UseCpuMeshletFrustumCulling && !sceneGpuLodSelectionActive,
                meshletNormalConeCullingEnabled: Settings.MeshletNormalConeCullingEnabled,
                buildGpuInstanceCandidates: buildGpuInstanceCandidates,
                captureSceneSubmissionValidationLists:
                    captureSceneSubmissionValidationLists,
                gpuLod1DistanceRatio: Settings.SceneSubmission.GpuLod1DistanceRatio,
                gpuLod2DistanceRatio: Settings.SceneSubmission.GpuLod2DistanceRatio);
            long sceneBuildCallMicroseconds =
                ElapsedMicroseconds(sceneBuildCallStart);
            drawStageStart = Stopwatch.GetTimestamp();
            sceneData.VolumetricDensityVolumes =
                GetSortedVolumetricDensityVolumes(scene);
            if (MaterialDebugViewPolicy.IsLinearDirectCapture(Settings.Materials.DebugView))
            {
                // A direct-light signal has no environment/background term.
                // Keep the diagnostic image mathematically zero off geometry.
                sceneData.ClearColor = new Vector4(0f, 0f, 0f, 1f);
            }

            int frameRingIndex = _currentFrame;
            ulong frameSerial = _ddgiFrameSerial;
            sceneData.FrameIndex = frameRingIndex;
            sceneData.TemporalSampleIndex = _temporalSampleIndex;
            sceneData.DdgiFrameSerial = frameSerial;
            sceneData.AreaShadowCandidateCount = shadowsAllowed
                ? localShadowSelection.AreaCandidateCount
                : 0;
            sceneData.AreaShadowSelectedCount = shadowsAllowed
                ? localShadowSelection.AreaLights.Length
                : 0;
            sceneData.AreaShadowRejectedByBudgetCount = shadowsAllowed
                ? localShadowSelection.AreaRejectedByBudgetCount
                : 0;
            sceneData.AreaShadowSampleCount = shadowsAllowed
                ? Settings.Shadows.AreaShadowSampleCount
                : 0;
            sceneData.AreaShadowMaximumRayDistance = shadowsAllowed
                ? ResolveMaximumAreaShadowRayDistance(
                    localShadowSelection.AreaLights)
                : 0f;
            sceneData.MaterialDetailedTransportHitCount =
                _completedMaterialGiCounters.EstimatedDetailedTransportHitCount;
            sceneData.MaterialCompactTransportHitCount =
                _completedMaterialGiCounters.EstimatedCompactTransportHitCount;
            sceneData.MaterialCorrectnessFallbackHitCount =
                _completedMaterialGiCounters.EstimatedCorrectnessFallbackHitCount;
            sceneData.MaterialFarFieldTransportHitCount =
                _completedMaterialGiCounters.EstimatedFarFieldTransportHitCount;
            _performanceCaptureMetadataProvider.ApplySceneLabels(
                sceneData,
                scene.Name);
            AdvancedGiRuntimeContentState advancedGiRuntimeContent =
                _advancedGiAdmission.ObserveRuntimeContent(
                    PerformanceCaptureHashing.ResolveScenario(
                        sceneData.CaptureScenario),
                    PerformanceCaptureHashing.ComputeSceneAssetHash(sceneData),
                    AdvancedGiSettingsFingerprint.Compute(
                        Settings.GlobalIllumination));
            _nearFieldResidual.SetFrameAdmission(advancedGiRuntimeContent);
            PerformanceCaptureFramePreparation captureFrame =
                _performanceCaptureMetadataProvider.ObserveSceneAndCamera(
                    sceneData,
                    camera,
                    frameSerial);
            if (captureFrame.SceneChanged)
                _diagnosticsAssembler.ResetSceneHistory();
            sceneData.ActiveFeatureIsolation = isolationMode;
            sceneData.DebugToolingEnabled = debugEnabled;
            sceneData.DebugOverlayMode = requestedDebugOverlay;
            sceneData.DebugOverlayStatus = debugEnabled
                ? DebugOverlayFrameStatus.Disabled(requestedDebugOverlay)
                : default;
            sceneData.CpuDebugSnapshotsEnabled = _sceneDataBuilder.CaptureCpuSnapshots;
            sceneData.DebugSelectedObjectIndex = Settings.Debug.SelectedObjectIndex;
            if (sceneData.DebugSelectedObjectIndex >= 0 &&
                sceneData.DebugSelectedObjectIndex < sceneData.ObjectDebugSnapshots.Count)
            {
                sceneData.DebugSelectedObjectName =
                    sceneData.ObjectDebugSnapshots[sceneData.DebugSelectedObjectIndex].Name;
            }

            sceneData.ImageIndex = _imageIndex;
            sceneData.LightCount = lightCount;
            sceneData.DirectionalLightCount = directionalLightCount;
            sceneData.DirectionalLightIndex0 =
                lightSnapshot.DirectionalLightIndex0;
            sceneData.DirectionalLightIndex1 =
                lightSnapshot.DirectionalLightIndex1;
            sceneData.LocalLightCount = localLightCount;
            sceneData.PointLightCount = lightSnapshot.PointLightCount;
            sceneData.SpotLightCount = lightSnapshot.SpotLightCount;
            sceneData.RectangleLightCount = lightSnapshot.RectangleLightCount;
            sceneData.DiskLightCount = lightSnapshot.DiskLightCount;
            sceneData.TubeLightCount = lightSnapshot.TubeLightCount;
            sceneData.AreaLightCount = lightSnapshot.AreaLightCount;
            sceneData.LightUploadBytes = lightUploadBytes;
            UpdateTiledLightDiagnostics(sceneData, lightSnapshot);
            sceneData.UploadedBytes += lightUploadBytes;
            sceneData.SceneSubmissionGpuCompactionEnabled = Settings.SceneSubmission.GpuCompactionEnabled;
            sceneData.SceneSubmissionIndirectMeshletDispatchEnabled =
                Settings.SceneSubmission.IndirectMeshletDispatchEnabled;
            sceneData.SceneSubmissionGpuLodSelectionEnabled = Settings.SceneSubmission.GpuLodSelectionEnabled;
            sceneData.SpecularAntialiasingMode =
                Settings.Materials.SpecularAntialiasingMode;
            sceneData.SceneSubmissionGpuLodSelectionMode =
                Settings.SceneSubmission.GpuLodSelectionMode;
            sceneData.SceneSubmissionGpuLodTargetPixelError =
                Settings.SceneSubmission.GpuLodTargetPixelError;
            sceneData.SceneSubmissionGpuLodDitherTransitionsEnabled =
                Settings.SceneSubmission.GpuLodDitherTransitionsEnabled;
            sceneData.SceneSubmissionGpuLodTransitionFrameCount =
                Settings.SceneSubmission.GpuLodTransitionFrameCount;
            sceneData.SceneSubmissionGpuHierarchicalLodEnabled =
                Settings.SceneSubmission.GpuHierarchicalLodEnabled;
            sceneData.SceneSubmissionGpuLod1DistanceRatio = Settings.SceneSubmission.GpuLod1DistanceRatio;
            sceneData.SceneSubmissionGpuLod2DistanceRatio = Settings.SceneSubmission.GpuLod2DistanceRatio;
            sceneData.SceneSubmissionGpuShadowCompactionEnabled = Settings.SceneSubmission.GpuShadowCompactionEnabled;
            sceneData.SceneSubmissionGpuShadowLodBias = Settings.SceneSubmission.GpuShadowLodBias;
            sceneData.HiZValidateAgainstLegacyPath = Settings.HiZOcclusion.ValidateAgainstLegacyPath;
            sceneData.SceneSubmissionValidationCompareCpuGpuLists =
                Settings.SceneSubmission.ValidationCompareCpuGpuLists ||
                sceneData.HiZValidateAgainstLegacyPath;
            sceneData.AnimationEnabled = gpuSkinningEnabled && skinningStats.SkinnedObjectCount > 0;
            sceneData.AnimationSkinningMode =
                gpuSkinningEnabled ? AnimationSkinningMode.GpuCompute : AnimationSkinningMode.Disabled;
            sceneData.AnimationDebugView = Settings.Animation.DebugView;
            sceneData.SkinnedObjectCount = skinningStats.SkinnedObjectCount;
            sceneData.SkinnedVertexCount = skinningStats.SkinnedVertexCount;
            sceneData.SkinningDispatchCount = skinningStats.SkinningDispatchCount;
            sceneData.JointMatrixCount = skinningStats.JointMatrixCount;
            sceneData.MaxJointsPerSkeleton = Settings.Animation.MaxJointsPerSkeleton;
            sceneData.CpuAnimationSampleMicroseconds = skinningStats.CpuAnimationSampleMicroseconds;
            sceneData.CpuSkinMatrixUploadMicroseconds = skinningStats.CpuSkinMatrixUploadMicroseconds;
            sceneData.SkinningUploadBytes = skinningStats.SkinningUploadBytes;
            sceneData.SkinMatrixBufferSize = skinningStats.SkinMatrixBufferSize;
            sceneData.SkinnedVertexBufferSize = skinningStats.SkinnedVertexBufferSize;
            sceneData.UploadedBytes += skinningStats.SkinningUploadBytes;
            sceneData.SkinningDispatches.AddRange(skinningStats.Dispatches);
            float particleDeltaSeconds = GetParticleDeltaSeconds();
            _particleTimeSeconds += particleDeltaSeconds;
            sceneData.GpuParticleDeltaSeconds = particleDeltaSeconds;
            sceneData.GpuParticleTimeSeconds = _particleTimeSeconds;
            bool gpuParticleMode = particlesAllowed &&
                                   Settings.Particles.Enabled &&
                                   Settings.Particles.SimulationMode == ParticleSimulationMode.Gpu;
            if (!particlesAllowed)
            {
                sceneData.ParticlesEnabled = false;
                sceneData.ParticleSimulationMode = Settings.Particles.SimulationMode;
                sceneData.ParticleDebugView = Settings.Particles.DebugView;
            }
            else if (gpuParticleMode)
            {
                ParticleSystemManager.PopulateSceneData(sceneData, Settings.Particles);
                _particleSystemManager.UploadFrameDataOnly(
                    Settings.Particles,
                    _currentCommandBuffer,
                    sceneData);
            }
            else
            {
                ParticleSimulationFrame particleFrame = _particleSystemManager.Update(
                    scene,
                    Settings.Particles,
                    camera.Position,
                    particleDeltaSeconds);
                ParticleSystemManager.PopulateSceneData(sceneData, Settings.Particles, particleFrame);
                _particleSystemManager.UploadFrame(
                    particleFrame,
                    Settings.Particles,
                    _textureManager,
                    _currentCommandBuffer,
                    sceneData);
            }

            if (particlesAllowed)
            {
                _gpuParticleRuntimeManager.PrepareFrame(
                    scene,
                    Settings.Particles,
                    _textureManager,
                    _currentCommandBuffer,
                    sceneData);
            }

            sceneData.FoliageDebugView = (uint)Settings.Foliage.DebugView;
            if (!isolateSkinnedAnimationDebug)
            {
                _foliageManager.PrepareFrame(
                    scene,
                    Settings.Foliage,
                    _currentCommandBuffer,
                    sceneData);
                sceneData.FoliageIndirectMeshletDispatchEnabled = Settings.Foliage.IndirectMeshletDispatchEnabled;
                sceneData.FoliageCastShadows =
                    shadowsAllowed && Settings.Foliage.Enabled && Settings.Foliage.CastShadows;
                sceneData.FoliageMotionVectorsEnabled = Settings.Foliage.MotionVectorsEnabled;
                sceneData.FoliageHiZCullingEnabled = Settings.Foliage.HiZCullingEnabled;
                sceneData.FoliageLocalShadowsEnabled = shadowsAllowed && Settings.Foliage.LocalShadowsEnabled;
                sceneData.FoliageGrassShadowDensityScale = Settings.Foliage.GrassShadowDensityScale;
                sceneData.FoliageMaxLocalShadowedSpotLights = Settings.Foliage.MaxLocalShadowedSpotLights;
                sceneData.FoliageMaxLocalShadowedPointLights = Settings.Foliage.MaxLocalShadowedPointLights;
                sceneData.FoliageLocalShadowClusterBudget = Settings.Foliage.MaxLocalShadowClusters;
                sceneData.FoliageLocalShadowMeshletDrawBudget = Settings.Foliage.MaxLocalShadowMeshletDraws;
            }
            else
            {
                sceneData.FoliageIndirectMeshletDispatchEnabled = false;
                sceneData.FoliageCastShadows = false;
                sceneData.FoliageMotionVectorsEnabled = false;
                sceneData.FoliageHiZCullingEnabled = false;
                sceneData.FoliageLocalShadowsEnabled = false;
                sceneData.FoliageGrassShadowDensityScale = 0f;
                sceneData.FoliageMaxLocalShadowedSpotLights = 0;
                sceneData.FoliageMaxLocalShadowedPointLights = 0;
                sceneData.FoliageLocalShadowClusterBudget = 0;
                sceneData.FoliageLocalShadowMeshletDrawBudget = 0;
            }

            sceneData.UploadedBytes += sceneData.ParticleInstanceUploadBytes;
            sceneData.ParticleDdgiSampleCount = Settings.GlobalIllumination.EffectiveUseDdgi &&
                                                Settings.GlobalIllumination.SimpleDdgiParticlesEnabled
                ? sceneData.GpuParticlesEnabled != 0
                    ? sceneData.GpuParticleEmitterCount
                    : sceneData.ParticleBatchCount
                : 0;
            sceneData.FoliageDdgiSampleCount = sceneData.FoliageClusterCount;
            // Forward+ consumes prepass depth for its depth test, tiled local-light culling,
            // and depth-based effects. Animation debug views may suppress individual effects,
            // but must never suppress the producer that establishes this frame's depth.
            sceneData.DepthPrePassEnabled = true;
            sceneData.DepthPrePassCompleted = false;
            sceneData.DepthPrePassFrameSerial = 0;
            sceneData.TiledLightCullingCompleted = false;
            sceneData.TiledLightCullingFrameSerial = 0;
            bool hybridReflectionHiZRequired =
                HybridReflectionHiZPolicy.RequiresPyramid(
                    Settings.Reflections,
                    reflectionsAllowed);
            HiZVisibilityPolicyDecision hiZDecision = PlanHiZVisibility(
                scene,
                camera,
                sceneData.DepthPrePassEnabled,
                isolateSkinnedAnimationDebug,
                hybridReflectionHiZRequired);
            _performanceCaptureMetadataProvider.ApplyCameraCut(
                sceneData,
                hiZDecision.CameraCut);
            HiZConsumerDecision hiZConsumers = ResolveHiZConsumers(
                sceneData,
                hybridReflection: hybridReflectionHiZRequired);
            bool hiZSkippedBecauseNoConsumer = hiZDecision.BuildHiZ && hiZConsumers.Count == 0;
            if (hiZSkippedBecauseNoConsumer)
            {
                hiZDecision = hiZDecision with
                {
                    BuildHiZ = false,
                    UseHiZForOcclusion = false,
                    Status = HiZVisibilityPolicyStatus.NoConsumer,
                    Reason = "No active Hi-Z consumers for this frame.",
                    WarmupFramesRemaining = 0
                };
            }
            else
            {
                hiZDecision = hiZDecision with
                {
                    Reason = BuildHiZPolicyReasonWithConsumers(hiZDecision, hiZConsumers)
                };
            }

            sceneData.HiZBuildEnabled = hiZDecision.BuildHiZ;
            sceneData.OcclusionCullingEnabled = sceneData.DepthPrePassEnabled && hiZDecision.UseHiZForOcclusion;
            sceneData.HiZTestMode = sceneData.OcclusionCullingEnabled ? Settings.HiZTestMode : HiZTestMode.Off;
            sceneData.OcclusionBias = Settings.HiZOcclusion.OcclusionBias;
            sceneData.PreviousHiZUvPaddingPixels = Settings.HiZOcclusion.PreviousFrameUvPaddingPixels;
            bool forwardVisibilityOverflowLastFrame =
                _completedForwardVisibilityCounters.IsValid &&
                _completedForwardVisibilityCounters.OverflowCount > 0;
            sceneData.ForwardVisibilityCompactionEnabled =
                Settings.HiZOcclusion.Enabled &&
                Settings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled &&
                Settings.SceneSubmission.GpuCompactionEnabled &&
                sceneData.OcclusionCullingEnabled &&
                sceneData.OpaqueMeshletCount > 0 &&
                !forwardVisibilityOverflowLastFrame;
            sceneData.ForwardVisibilityCompactionSkipReason = forwardVisibilityOverflowLastFrame
                ? "previous forward visibility compaction overflowed; using pre-Hi-Z compacted forward buffers this frame"
                : string.Empty;
            bool previousHiZHistoryValid = sceneData.OcclusionCullingEnabled &&
                                           Settings.HiZOcclusion.Enabled &&
                                           Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled &&
                                           !sceneData.ForwardVisibilityCompactionEnabled &&
                                           !hiZDecision.SceneChanged &&
                                           !hiZDecision.CameraCut &&
                                           !hiZDecision.PyramidInvalidated;
            sceneData.PreviousHiZFrameValid = previousHiZHistoryValid && !_previousHiZCameraMotionSuppressedThisFrame;
            sceneData.PreviousHiZSkippedInvalidHistory =
                hiZConsumers.SceneSubmissionPreviousHiZ &&
                sceneData.OcclusionCullingEnabled &&
                Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled &&
                !_previousHiZCameraMotionSuppressedThisFrame &&
                !sceneData.PreviousHiZFrameValid
                    ? 1
                    : 0;
            sceneData.PreviousHiZSkippedCameraMotion =
                hiZConsumers.SceneSubmissionPreviousHiZ &&
                sceneData.OcclusionCullingEnabled &&
                _previousHiZCameraMotionSuppressedThisFrame
                    ? 1
                    : 0;
            sceneData.HiZConsumerCount = hiZConsumers.Count;
            sceneData.HiZConsumerSummary = hiZConsumers.Summary;
            sceneData.HiZBuildSkippedBecauseNoConsumer = hiZSkippedBecauseNoConsumer;
            sceneData.HiZPolicyStatus = hiZDecision.Status;
            sceneData.HiZPolicyReason = hiZDecision.Reason;
            sceneData.HiZPolicyWarmupFramesRemaining = hiZDecision.WarmupFramesRemaining;
            sceneData.HiZPolicySceneChanged = hiZDecision.SceneChanged ? 1 : 0;
            sceneData.HiZPolicyCameraCut = hiZDecision.CameraCut ? 1 : 0;
            sceneData.HiZPolicyPyramidInvalidated = hiZDecision.PyramidInvalidated ? 1 : 0;
            sceneData.HiZPolicyAdaptiveSuppressed = hiZDecision.AdaptiveSuppressed ? 1 : 0;
            sceneData.HiZPolicyAdaptiveProbe = hiZDecision.AdaptiveProbe ? 1 : 0;
            sceneData.HiZPolicyAdaptiveProbeCountdown = hiZDecision.AdaptiveProbeCountdown;
            sceneData.HiZPolicyAdaptiveMeasuredOcclusionTests = hiZDecision.AdaptiveMeasuredOcclusionTests;
            sceneData.HiZPolicyAdaptiveMeasuredOcclusionCulled = hiZDecision.AdaptiveMeasuredOcclusionCulled;
            sceneData.HiZPolicyAdaptiveCullRate = hiZDecision.AdaptiveCullRate;
            sceneData.HiZPolicyCounterSource = hiZDecision.CounterSource;
            sceneData.HiZPolicyAdaptiveEstimatedSavedMicroseconds = hiZDecision.AdaptiveEstimatedSavedMicroseconds;
            sceneData.HiZPolicyAdaptiveEstimatedCostMicroseconds = hiZDecision.AdaptiveEstimatedCostMicroseconds;
            sceneData.HiZPolicyAdaptiveEstimatedNetMicroseconds = hiZDecision.AdaptiveEstimatedNetMicroseconds;
            sceneData.HiZPolicyAdaptiveSmoothedCullRate = hiZDecision.AdaptiveSmoothedCullRate;
            sceneData.HiZPolicyAdaptiveSmoothedSavedToCostRatio = hiZDecision.AdaptiveSmoothedSavedToCostRatio;
            sceneData.HiZPolicyAdaptiveSuppressedFrameCount = hiZDecision.AdaptiveSuppressedFrameCount;
            sceneData.HiZPolicyAdaptiveStatus = hiZDecision.AdaptiveStatus;
            sceneData.TransparentPassEnabled = EnableTransparentPass && Settings.Transparency.Enabled;
            sceneData.TransparencyMode = Settings.Transparency.Mode;
            sceneData.PostFirstPresentPipelineSpecializationsReady =
                Volatile.Read(
                    ref _postFirstPresentPipelineSpecializationsReady) != 0;
            sceneData.TransparentPipelinePartitioningEnabled =
                Settings.Transparency.PipelinePartitioningEnabled &&
                sceneData.PostFirstPresentPipelineSpecializationsReady;
            sceneData.TransparencyDebugView = Settings.Transparency.DebugView;
            sceneData.TransparentReceiveShadows = Settings.Transparency.ReceiveShadows;
            sceneData.TransparentReceiveGlobalIllumination =
                Settings.Transparency.ReceiveGlobalIllumination;
            sceneData.TransparentSampleReflections =
                Settings.Transparency.SampleReflections;
            sceneData.TransparentSceneReflectionRayTaskBudget =
                Settings.Transparency.SceneReflectionRayTaskBudget;
            sceneData.TransparentSceneReflectionSsrSampleBudget =
                Settings.Transparency.SceneReflectionSsrSampleBudget;
            sceneData.OpaqueSceneColorSnapshotAvailable = false;
            sceneData.TransparentDdgiReceiverCountersEnabled = false;
            sceneData.DecalDebugView = Settings.Decals.DebugView;
            sceneData.GeometryDecalsEnabled = geometryDecalsEnabled;
            sceneData.DecalReceiveShadows = Settings.Decals.ReceiveShadows;
            sceneData.DecalReceiveGlobalIllumination =
                Settings.Decals.ReceiveGlobalIllumination;
            sceneData.GeometryDecalDepthBias = Settings.Decals.GeometryDepthBias;
            sceneData.GeometryDecalSlopeScaledDepthBias = Settings.Decals.GeometrySlopeScaledDepthBias;
            sceneData.HiZMipCount = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.MipLevels ?? 0u : 0u;
            sceneData.HiZWidth = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Width ?? 0u : 0u;
            sceneData.HiZHeight = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Height ?? 0u : 0u;
            sceneData.ActiveSceneColorTextureIndex = BindlessIndex.HdrSceneColorTexture;
            sceneData.EffectiveExposure = Settings.Exposure;
            sceneData.FogDirectionalInscatteringDirection = ResolveFogDirectionalInscatteringDirection(lightSnapshot);
            sceneData.DebugViewMode = forwardDebugViewMode;
            GlobalIlluminationDebugView resolvedNearFieldDebugView =
                RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                    Settings.GlobalIllumination.DebugView);
            sceneData.NearFieldResidualDebugView =
                SimpleDdgiNearFieldResidualDebugViewContract.IsC5View(
                    resolvedNearFieldDebugView)
                    ? (uint)resolvedNearFieldDebugView
                    : (uint)GlobalIlluminationDebugView.None;
            sceneData.JitterEnabled = jitter.X != 0.0f || jitter.Y != 0.0f ? 1 : 0;
            sceneData.JitterX = jitter.X;
            sceneData.JitterY = jitter.Y;
            if (shadowsAllowed)
            {
                System.Numerics.Vector3 selectedShadowLightDirection =
                    lightSnapshot.FirstShadowCastingDirectionalLight.Direction;
                Vector3 shadowDiagnosticLightDirection = directionalShadowsEnabled
                    ? new Vector3(
                        selectedShadowLightDirection.X,
                        selectedShadowLightDirection.Y,
                        selectedShadowLightDirection.Z)
                    : Vector3.Zero;
                PrepareDirectionalShadows(
                    sceneData,
                    shadowData,
                    directionalShadowsEnabled,
                    shadowedDirectionalLightIndex,
                    shadowDiagnosticLightDirection);
            }

            long postSceneBuildMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            long sceneSetupMicroseconds = checked(
                preSceneBuildMicroseconds +
                sceneBuildCallMicroseconds +
                postSceneBuildMicroseconds);
            drawStageStart = Stopwatch.GetTimestamp();
            _environmentManager?.Upload(_stagingRing, _currentCommandBuffer);
            PrepareDdgiFoliageProxies(scene, sceneData);
            long environmentAndFoliageMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            long resourceSubstageStart = Stopwatch.GetTimestamp();
            PrepareAccelerationStructures(scene, sceneData);
            long accelerationPrepareMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            resourceSubstageStart = Stopwatch.GetTimestamp();
            ApplyCompletedSceneSubmissionCounters(sceneData, _completedSceneSubmissionCounters);
            ApplyCompletedForwardVisibilityCounters(sceneData, _completedForwardVisibilityCounters);
            ApplyCompletedSceneSubmissionValidation(sceneData, _completedSceneSubmissionValidation);
            sceneData.SceneSubmissionFallbackReason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
                sceneData,
                _completedSceneSubmissionCounters,
                _completedSceneSubmissionValidation);
            sceneData.SceneSubmissionCompactionSkipReason =
                SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(sceneData);
            UpdateHiZFallbackDiagnostics(sceneData);
            DirectionalShadowCasterFrameCapture directionalShadowCasterCapture =
                (uint)_currentFrame < (uint)_directionalShadowCasterFrameCaptures.Length
                    ? _directionalShadowCasterFrameCaptures[_currentFrame]
                    : DirectionalShadowCasterFrameCapture.Empty;
            _diagnosticsBuffer.ResetCounters(
                _currentCommandBuffer,
                _currentFrame,
                directionalShadowCasterCapture.FrameSerial,
                directionalShadowCasterCapture.ResourceGeneration,
                directionalShadowCasterCapture.Valid != 0,
                sceneData.DdgiFrameSerial);

            if (!isolateSkinnedAnimationDebug)
            {
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "FoliageCullPass");
                try
                {
                    _foliageCullPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }
            }

            if (_ddgiFoliageProxyGenerationPass != null &&
                _ddgiFoliageProxyFrame.RequiresGpuGeneration)
            {
                _gpuTimestamps.BeginPass(
                    _currentCommandBuffer,
                    _currentFrame,
                    "DdgiFoliageProxyGenerationPass");
                try
                {
                    _ddgiFoliageProxyGenerationPass.Execute(
                        _currentCommandBuffer,
                        _ddgiFoliageProxyFrame,
                        sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(
                        _currentCommandBuffer,
                        _currentFrame);
                }
            }

            if (animationAllowed && sceneData.AnimationEnabled)
            {
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "SkinningPass");
                try
                {
                    _skinningPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }
            }
            long preAccelerationRecordMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            resourceSubstageStart = Stopwatch.GetTimestamp();

            // Skinning output is now complete and visible to AS-build reads.
            // Publish the new TLAS/metadata transaction before DDGI chooses a
            // trace backend or records any ray-query consumer.
            RecordAccelerationStructures(sceneData);
            long accelerationRecordMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            resourceSubstageStart = Stopwatch.GetTimestamp();
            PrepareAreaRayShadows(
                sceneData,
                localShadowSelection,
                shadowsAllowed);
            PrepareLocalShadows(
                sceneData,
                localShadowSelection,
                lightCount);
            PrepareThickTransmissionFrame(sceneData);
            if (reflectionsAllowed)
                PrepareReflectionProbes(scene, sceneData);
            PrepareDirectionalShadowFrame(
                sceneData,
                lightSnapshot,
                directionalShadowsEnabled);
            long shadowAndReflectionPrepareMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            resourceSubstageStart = Stopwatch.GetTimestamp();
            PrepareSimpleDdgiFrame(
                scene,
                camera,
                sceneData,
                lightSnapshot);
            _automaticPlanarReflectionManager.PrepareFrame(
                scene,
                sceneData);
            long simpleDdgiPrepareMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            resourceSubstageStart = Stopwatch.GetTimestamp();
            PrepareGiCausticCoordinatorFrame(lightSnapshot);
            PopulateAdvancedGiFrameDiagnostics(sceneData);
            bool ddgiAvailableForLayeredReceivers =
                ForwardPlusPass.ShouldApplyDdgi(
                    sceneData,
                    Settings.GlobalIllumination);
            sceneData.TransparentReceiveGlobalIllumination &=
                ddgiAvailableForLayeredReceivers;
            sceneData.DecalReceiveGlobalIllumination &=
                ddgiAvailableForLayeredReceivers;
            sceneData.TransparentDdgiReceiverCountersEnabled =
                ddgiAvailableForLayeredReceivers &&
                RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
                Settings.Diagnostics.DdgiForwardEstimateCountersEnabled;
            var debugOverlayOptions = new DebugOverlayBuildOptions(
                Settings.GlobalIllumination.EffectiveUseDdgi,
                Settings.Debug.ShowXRayVolumes,
                Settings.Debug.ShowDepthTestedVolumes,
                Settings.Debug.SelectedReflectionProbeIndex);
            sceneData.DebugDrawSnapshot = _debugOverlayBuilder.Build(
                scene,
                sceneData,
                _simpleDdgiVolumeManager,
                debugOverlayOptions);
            long advancedGiAndDebugMicroseconds =
                ElapsedMicroseconds(resourceSubstageStart);
            long resourcePreparationMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            drawStageStart = Stopwatch.GetTimestamp();

            SetViewportAndScissor(_currentCommandBuffer);

            // Execute render graph
            sceneData.SecondaryCommandBufferEnabled = Settings.UseSecondaryCommandBuffers ? 1 : 0;
            bool asyncTimelineAvailable =
                _cmd.AsyncComputeTimelineSemaphore.Handle != 0;
            if (_asyncComputeCoordinator.RequiresConcreteResourceBindings(
                    Settings.AsyncCompute.Mode,
                    _context.HasIndependentComputeQueue,
                    asyncTimelineAvailable))
            {
                EnsureAsyncComputeResourceBindings();
            }

            DeviceRequirementReport? asyncDevice =
                _context.SelectedDeviceRequirementReport;
            bool exactReceiverFeedbackReady =
                _simpleDdgiReceiverFeedback?.IsOwnedCaptureReady == true;
            bool exactFogFeedbackRequired =
                Settings.Fog.Enabled &&
                Settings.Fog.Mode != FogMode.Disabled &&
                sceneData.AnimationDebugView == AnimationDebugView.None;
            var asyncPlanningInput = new AsyncComputePlanningInput(
                Settings,
                sceneData,
                _currentFrame,
                _temporalSampleIndex,
                _context.HasIndependentComputeQueue,
                asyncTimelineAvailable,
                _context.HasDedicatedComputeQueue,
                _context.GraphicsQueueFamilyIndex,
                _context.ComputeQueueFamilyIndex,
                _context.GraphicsQueueFlags,
                _context.ComputeQueueFlags,
                string.IsNullOrWhiteSpace(asyncDevice?.DeviceName)
                    ? "unknown-device"
                    : asyncDevice.DeviceName,
                string.IsNullOrWhiteSpace(asyncDevice?.DriverVersion)
                    ? "unknown-driver"
                    : asyncDevice.DriverVersion,
                _farFieldClipmapManager?.BakePending == true,
                _renderTargets?.BloomMipCount ?? 0,
                exactReceiverFeedbackReady
                    ? "exact receiver-feedback capture requires one graphics queue completion domain"
                    : string.Empty,
                exactReceiverFeedbackReady
                    ? exactFogFeedbackRequired
                        ? ExactReceiverFeedbackGraphicsProducerPassesWithFog
                        : ExactReceiverFeedbackGraphicsProducerPasses
                    : null);
            Njulf.Rendering.Pipeline.AsyncComputeFramePlan
                frameAsyncComputePlan =
                    _asyncComputeCoordinator.PlanFrame(asyncPlanningInput);
            AsyncComputeRecordingDecision asyncRecordingDecision =
                _asyncComputeCoordinator.ValidateForRecording(
                    frameAsyncComputePlan,
                    _renderGraph.ConcreteResourceBindings.Generation);
            frameAsyncComputePlan = asyncRecordingDecision.Plan;

            // This is a runtime execution flag, not a request flag.  DDGI setup runs before
            // the graph plan is compiled and may have observed an enabled setting, but a
            // graphics-only or validation-fallback execution must never let DDGI's local
            // barriers assume that a compute submission is going to follow.
            sceneData.DdgiAsyncComputeEnabled = 0;
            if (asyncRecordingDecision.RecordAsync)
            {
                RecordRenderGraphFromAsyncPlan(
                    frameAsyncComputePlan,
                    sceneData);
            }
            else
            {
                sceneData.DdgiAsyncComputeEnabled = 0;
                EnsureSwapchainImageColorAttachment(_currentCommandBuffer);
                _renderGraph.Execute(
                    _currentCommandBuffer,
                    _currentFrame,
                    sceneData,
                    _gpuTimestamps,
                    _cmd,
                    Settings.UseSecondaryCommandBuffers);
                sceneData.DdgiAsyncComputeEnabled =
                    frameAsyncComputePlan.IsPathActive(
                        AsyncComputePath.SimpleDdgiUpdate)
                        ? 1
                        : 0;
            }
            long graphRecordMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            drawStageStart = Stopwatch.GetTimestamp();

            RecordReflectionProbeWork(sceneData);
            if (reflectionsAllowed)
                UpdateReflectionProbeTelemetry(sceneData);
            _simpleDdgiReceiverFeedback?.FinalizeAfterAllReceiverProducers(
                _currentCommandBuffer,
                _currentFrame,
                _gpuTimestamps);

            // FogPass produces the B5 froxel-consumer evidence. Refresh this
            // one admission after graph execution so diagnostics describe the
            // commands that were actually recorded this frame.
            sceneData.GiRoadmapExperiments = sceneData.GiRoadmapExperiments with
            {
                DirectionalFog =
                CreateDirectionalFogExperimentAdmission(sceneData)
            };

            // SceneColor still contains the linear, pre-exposure result here.
            // Tone mapping only sampled it; capture before any subsequent
            // client draw can mutate renderer-owned targets.
            RecordLinearHdrSceneColorCapture();
            _hizVisibilityPolicyState.PyramidValid = sceneData.HiZBuildEnabled;
            if (MeshletDiagnosticCountersActive)
                ApplyCompletedGpuCounters(sceneData, _completedGpuCounters);
            ApplyCompletedCompactedMeshOnlyForwardCounters(
                sceneData,
                _completedSceneSubmissionCounters,
                _completedForwardVisibilityCounters);
            ApplyCompletedDdgiForwardEstimateCounters(sceneData, _completedDdgiForwardEstimateCounters);
            ApplyCompletedDdgiInvestigationCounters(sceneData, _completedDdgiInvestigationCounters);
            ApplyCompletedDirectionalShadowReceiverCounters(sceneData, _completedDirectionalShadowReceiverCounters);
            sceneData.DirectionalShadowCasterDiagnosticReadback = _completedDirectionalShadowCasterDiagnostics;
            sceneData.DirectionalShadowRayCountersReadback =
                _completedDirectionalShadowRayCounters;
            ApplyCompletedHybridReflectionCounters(
                sceneData,
                _completedHybridReflectionCounters);
            ApplyCompletedTransparentReflectionCounters(
                sceneData,
                _completedTransparentReflectionCounters);
            if (particlesAllowed)
                ApplyCompletedGpuParticleCounters(sceneData, _completedGpuParticleCounters);
            if (!isolateSkinnedAnimationDebug)
                ApplyCompletedFoliageCounters(sceneData, _completedFoliageCounters);
            ApplyHiZCounterDiagnostics(sceneData);
            UpdateHiZFallbackDiagnostics(sceneData);
            FrameTimingSnapshot completedGpuTimings = _gpuTimestamps.LastCompletedSnapshot;
            sceneData.SimpleDdgiCompletedFrameEvidence =
                _simpleDdgiFrameEvidence.CaptureSnapshot().Completed;
            ApplyCompletedGpuTimings(sceneData, completedGpuTimings);
            bool hybridReflectionTimingValid = HasCompletedGpuTiming(
                completedGpuTimings,
                "HybridReflectionSsrPass");
            long hybridReflectionOwnedMicroseconds =
                sceneData.GpuHybridReflectionSsrMicroseconds +
                sceneData.GpuHybridReflectionRayQueryMicroseconds +
                sceneData.GpuHybridReflectionDdgiBaseMicroseconds +
                sceneData.GpuHybridReflectionResolveMicroseconds +
                sceneData.GpuHybridReflectionTemporalMicroseconds +
                sceneData.GpuHybridReflectionSpatialMicroseconds +
                sceneData.GpuHybridReflectionCompositeMicroseconds +
                completedGpuTimings.GetGpuMicrosecondsOrZero(
                    "OpaqueSceneColorSnapshotPass") +
                (sceneData.TransparentSampleReflections
                    ? sceneData.GpuTransparentMicroseconds
                    : 0L);
            _hybridReflectionRuntime?.ObserveCompletedBudgetSample(
                new HybridReflectionBudgetSample(
                    hybridReflectionOwnedMicroseconds,
                    hybridReflectionTimingValid,
                    _completedHybridReflectionCounters.RayOverflows != 0u ||
                    _completedHybridReflectionCounters.TileOverflows != 0u));
            sceneData.AsyncComputeEstimatedOverlapMicroseconds =
                _asyncComputeCoordinator.EstimateOverlapMicroseconds(
                    frameAsyncComputePlan,
                    completedGpuTimings);
            GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
            bool detailedDdgiInstrumentationActive =
                RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled &&
                (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                 giSettings.DebugView != GlobalIlluminationDebugView.None);
            bool fixedSimpleDdgiBudget =
                !giSettings.DdgiAdaptiveBudgetingEnabled ||
                detailedDdgiInstrumentationActive;
            bool hasCompletedSimpleDdgiGpuTiming = HasCompletedSimpleDdgiGpuTiming(completedGpuTimings);
            bool hasCompletedSimpleDdgiScheduleTiming = HasCompletedGpuTiming(
                completedGpuTimings,
                "SimpleDdgiSchedulePass");
            if (_simpleDdgiVolumeManager != null &&
                sceneData.SimpleDdgiActive != 0 &&
                (fixedSimpleDdgiBudget || hasCompletedSimpleDdgiScheduleTiming))
            {
                ulong targetGpuMicroseconds = checked((ulong)Math.Round(
                    Math.Max(0.0f, giSettings.EffectiveDdgiAdaptiveBudgetTimeMilliseconds) * 1_000.0));
                _simpleDdgiVolumeManager.ReportSchedulingFeedback(new SimpleDdgiSchedulingFeedback(
                    CompletedGpuMicroseconds: hasCompletedSimpleDdgiGpuTiming
                        ? checked((ulong)Math.Max(0L, sceneData.GpuDdgiUpdateMicroseconds))
                        : 0UL,
                    TargetGpuMicroseconds: targetGpuMicroseconds,
                    // Diagnostic atomics intentionally perturb the timed trace and
                    // blend workload. Keep investigation captures reproducible and
                    // do not train the production feedback controller on that cost.
                    DeterministicFixedBudget: fixedSimpleDdgiBudget));
            }

            long postGraphMicroseconds =
                ElapsedMicroseconds(drawStageStart);
            sceneData.CpuTotalDrawSceneMicroseconds = ElapsedMicroseconds(drawSceneStart);
            if (sceneData.CpuTotalDrawSceneMicroseconds >= 100_000)
            {
                Console.WriteLine(
                    $"Renderer draw hitch: scene='{scene.Name}', " +
                    $"total={sceneData.CpuTotalDrawSceneMicroseconds / 1000.0:F3}ms, " +
                    $"prepareScene={prepareSceneMicroseconds / 1000.0:F3}ms, " +
                    $"textureFanout={textureFanoutMicroseconds / 1000.0:F3}ms, " +
                    $"sceneSetup={sceneSetupMicroseconds / 1000.0:F3}ms, " +
                    $"sceneSetupParts={preSceneBuildMicroseconds / 1000.0:F3}/" +
                    $"{sceneBuildCallMicroseconds / 1000.0:F3}/" +
                    $"{postSceneBuildMicroseconds / 1000.0:F3}ms, " +
                    $"resourcePreparation={resourcePreparationMicroseconds / 1000.0:F3}ms, " +
                    $"resourceParts={environmentAndFoliageMicroseconds / 1000.0:F3}/" +
                    $"{accelerationPrepareMicroseconds / 1000.0:F3}/" +
                    $"{preAccelerationRecordMicroseconds / 1000.0:F3}/" +
                    $"{accelerationRecordMicroseconds / 1000.0:F3}/" +
                    $"{shadowAndReflectionPrepareMicroseconds / 1000.0:F3}/" +
                    $"{simpleDdgiPrepareMicroseconds / 1000.0:F3}/" +
                    $"{advancedGiAndDebugMicroseconds / 1000.0:F3}ms, " +
                    $"graphRecord={graphRecordMicroseconds / 1000.0:F3}ms, " +
                    $"postGraph={postGraphMicroseconds / 1000.0:F3}ms.");
            }
            UpdateGlobalIlluminationCpuTiming(sceneData);
            _asyncComputeCoordinator.CaptureTiming(
                _currentFrame,
                frameAsyncComputePlan,
                new AsyncComputeTimingCaptureInput(
                    Settings,
                    sceneData,
                    asyncPlanningInput.DeviceName,
                    asyncPlanningInput.DriverVersion));
            SimpleDdgiSubmittedFrameEvidence pendingDdgiEvidence = default;
            if (_simpleDdgiVolumeManager is { } evidenceManager &&
                sceneData.SimpleDdgiActive != 0)
            {
                pendingDdgiEvidence = new SimpleDdgiSubmittedFrameEvidence
                {
                    Valid = true,
                    FrameSlot = _currentFrame,
                    FrameSerial = sceneData.DdgiFrameSerial,
                    SchedulerFrameSerial = evidenceManager.FrameSerial,
                    GpuTimingRecorded = _gpuTimestamps.EnabledThisFrame,
                    SchedulerMode = sceneData.SimpleDdgiSchedulerMode,
                    ActiveProbeCount = Math.Max(
                        0,
                        sceneData.DdgiActiveProbeCount),
                    AuditPhysicalProbeCount = Math.Max(
                        0,
                        evidenceManager.ProbeCount),
                    VolumeResourceGeneration =
                        sceneData.SimpleDdgiVolumeResourceGeneration,
                    TransportTopologyGeneration =
                        sceneData.SimpleDdgiTransportTopologyGeneration,
                    SourceLightingGeneration =
                        sceneData.SimpleDdgiSourceLightingGeneration,
                    AdmittedSourceCohortGeneration =
                        sceneData.SimpleDdgiAdmittedSourceCohortGeneration,
                    TransportGeneration =
                        sceneData.SimpleDdgiTransportGeneration,
                    PublishedPropagationGeneration =
                        sceneData.SimpleDdgiPublishedPropagationGeneration,
                    LivePropagationSourceGeneration =
                        sceneData.SimpleDdgiLivePropagationSourceGeneration,
                    SchedulerResourceGeneration =
                        sceneData.SimpleDdgiSchedulerResourceGeneration,
                    QueueTransactionGeneration = evidenceManager
                        .CurrentQueueTransactionGeneration,
                    CachedSweepCount = Math.Max(
                        0,
                        sceneData.SimpleDdgiTransportCachedSweepCount),
                    TailCertificationEnabled = sceneData
                        .SimpleDdgiTransportTailCertificationEnabled,
                    TailCertificate = evidenceManager
                        .CaptureTransportTailCertificateFrameEvidence(),
                    IntendedGpuPasses = _gpuTimestamps
                        .GetIntendedSimpleDdgiPasses(_currentFrame),
                    AdmittedGpuTimingPasses = _gpuTimestamps
                        .GetAdmittedSimpleDdgiTimingPasses(_currentFrame),
                    SourceCacheLayoutIdentity =
                        evidenceManager.SourceCacheAdmissionIdentity,
                    ScheduledPrimaryRayCount =
                        sceneData.DdgiScheduledPrimaryRayCount,
                    VisibilityRayCount = sceneData.DdgiVisibilityRayCount
                };
            }

            _simpleDdgiFrameEvidence.CapturePending(
                _currentFrame,
                new SimpleDdgiSubmittedWorkload(pendingDdgiEvidence));
            _lastSceneData = sceneData;
            AsyncComputeDiagnosticsSnapshot asyncComputeSnapshot =
                _asyncComputeCoordinator.CreateDiagnosticsSnapshot(
                    completedGpuTimings,
                    new AsyncComputeDiagnosticsContext(
                        _context.HasIndependentComputeQueue,
                        _context.HasDedicatedComputeQueue,
                        _context.GraphicsQueueFamilyIndex,
                        _context.ComputeQueueFamilyIndex));
            RendererDiagnosticsAssemblyInput diagnosticsInput =
                CaptureDiagnosticsInput(
                    sceneData,
                    asyncComputeSnapshot);
            RendererDiagnosticsAssemblyResult diagnosticsResult =
                _diagnosticsAssembler.Assemble(diagnosticsInput);
            _lastDiagnostics = diagnosticsResult.Diagnostics;
            _lastBudgetSnapshot = diagnosticsResult.Budget;
            _debugOverlayBuilder.ClearFrame();
        }

        private VolumetricDensityVolume[] GetSortedVolumetricDensityVolumes(
            Scene scene)
        {
            if (ReferenceEquals(_volumetricDensityCacheScene, scene) &&
                _volumetricDensityCacheRevision ==
                scene.VolumetricDensityRevision)
            {
                return _sortedVolumetricDensityVolumes;
            }

            VolumetricDensityVolume[] sorted =
                CreateSortedVolumetricDensityVolumeSnapshot(
                    scene.VolumetricDensityVolumes);

            _sortedVolumetricDensityVolumes = sorted;
            _volumetricDensityCacheScene = scene;
            _volumetricDensityCacheRevision =
                scene.VolumetricDensityRevision;
            return sorted;
        }

        internal static VolumetricDensityVolume[]
            CreateSortedVolumetricDensityVolumeSnapshot(
                IReadOnlyList<VolumetricDensityVolume> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            int enabledCount = 0;
            for (int index = 0; index < source.Count; index++)
            {
                if (source[index].Enabled)
                    enabledCount++;
            }

            VolumetricDensityVolume[] sorted = enabledCount == 0
                ? Array.Empty<VolumetricDensityVolume>()
                : new VolumetricDensityVolume[enabledCount];
            int destinationIndex = 0;
            for (int index = 0; index < source.Count; index++)
            {
                VolumetricDensityVolume volume = source[index];
                if (volume.Enabled)
                    sorted[destinationIndex++] = volume;
            }
            if (sorted.Length > 1)
                Array.Sort(sorted, CompareVolumetricDensityVolumes);
            return sorted;
        }

        private static int CompareVolumetricDensityVolumes(
            VolumetricDensityVolume left,
            VolumetricDensityVolume right)
        {
            int priorityOrder = right.Priority.CompareTo(left.Priority);
            return priorityOrder != 0
                ? priorityOrder
                : left.Id.CompareTo(right.Id);
        }

        // GI diagnostics inspect the same receiver set as the beauty frame.
        // Hiding decals here used to rebuild the transparent draw list on view
        // selection and made the diagnostic itself change the scene it sampled.
        internal static bool ShouldRenderGeometryDecals(uint forwardDebugViewMode) =>
            forwardDebugViewMode == 0 ||
            forwardDebugViewMode is >= 80 and <= 129;

        private uint ResolveForwardDebugViewMode()
        {
            if (EnableMeshletDebugView)
                return 1u;
            if (Settings.Animation.DebugView != AnimationDebugView.None)
                return (uint)Settings.Animation.DebugView;
            if (Settings.Materials.DebugView != MaterialDebugView.None)
                return (uint)Settings.Materials.DebugView;
            if (Settings.GlobalIllumination.Enabled &&
                Settings.GlobalIllumination.Mode != GlobalIlluminationMode.Disabled)
            {
                GlobalIlluminationDebugView effectiveDebugView =
                    RendererBuildFeatures.ResolveGlobalIlluminationDebugView(
                        Settings.GlobalIllumination.DebugView);
                if (SimpleDdgiNearFieldResidualDebugViewContract.IsC5View(
                        effectiveDebugView))
                {
                    // C5 owns these visualizations in its final compute pass.
                    // Forward must still execute its normal lighting path so
                    // the independent direct-diffuse/emissive MRT is valid.
                    return 0u;
                }

                return effectiveDebugView switch
                {
                    GlobalIlluminationDebugView.FinalIndirect => 80u,
                    GlobalIlluminationDebugView.DdgiIrradiance => 86u,
                    GlobalIlluminationDebugView.DdgiVisibility => 87u,
                    GlobalIlluminationDebugView.DdgiProbeIndex => 88u,
                    GlobalIlluminationDebugView.DdgiProbeState => 89u,
                    GlobalIlluminationDebugView.DdgiProbeRelocation => 90u,
                    GlobalIlluminationDebugView.DdgiLeakClamp => 91u,
                    GlobalIlluminationDebugView.DdgiCoverage => 92u,
                    GlobalIlluminationDebugView.DdgiCascadeSelection => 93u,
                    GlobalIlluminationDebugView.DdgiCascadeBlendWeight => 94u,
                    GlobalIlluminationDebugView.DdgiUpdateReasons => 95u,
                    GlobalIlluminationDebugView.DdgiRayBudget => 96u,
                    GlobalIlluminationDebugView.DdgiGatherLocalVolume => 97u,
                    GlobalIlluminationDebugView.DdgiGatherClipmap => 98u,
                    GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight => 99u,
                    GlobalIlluminationDebugView.DdgiGatherFallback => 100u,
                    GlobalIlluminationDebugView.DdgiRawDiffuse => 101u,
                    GlobalIlluminationDebugView.DdgiSuppressionMask => 102u,
                    GlobalIlluminationDebugView.DdgiEffectiveWeight => 103u,
                    GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight => 104u,
                    GlobalIlluminationDebugView.DdgiClassificationInvalidScore => 106u,
                    GlobalIlluminationDebugView.DdgiVisibilityMoments => 107u,
                    GlobalIlluminationDebugView.DdgiSpatialCoverage => 108u,
                    GlobalIlluminationDebugView.DdgiSupportCoverage => 109u,
                    GlobalIlluminationDebugView.DdgiDataConfidence => 110u,
                    GlobalIlluminationDebugView.DdgiVisibilityConfidence => 111u,
                    GlobalIlluminationDebugView.DdgiConfidenceChain => 112u,
                    GlobalIlluminationDebugView.DdgiProbeLogicalPosition => 113u,
                    GlobalIlluminationDebugView.DdgiProbeRelocatedPosition => 114u,
                    GlobalIlluminationDebugView.DdgiProbeRelocationDirection => 115u,
                    GlobalIlluminationDebugView.DdgiGatherBlendWeight => 116u,
                    GlobalIlluminationDebugView.DdgiSampledIrradiance => 117u,
                    GlobalIlluminationDebugView.DdgiFinalDiffuse => 118u,
                    GlobalIlluminationDebugView.DdgiConfidenceBypass => 119u,
                    GlobalIlluminationDebugView.FarFieldOccupancySlice => 120u,
                    GlobalIlluminationDebugView.FarFieldTraceResult => 121u,
                    GlobalIlluminationDebugView.FarFieldSkyVisibility => 122u,
                    GlobalIlluminationDebugView.FarFieldSunShadow => 123u,
                    GlobalIlluminationDebugView.DdgiDirectionalSupport => 124u,
                    GlobalIlluminationDebugView.DdgiSourceCacheRadiance => 125u,
                    GlobalIlluminationDebugView.DdgiProbeResidency => 126u,
                    GlobalIlluminationDebugView.DdgiResidencyFallback => 127u,
                    GlobalIlluminationDebugView.DdgiPageAge => 128u,
                    GlobalIlluminationDebugView.DdgiPhysicalPage => 129u,
                    GlobalIlluminationDebugView.DdgiReceiverCacheRejection =>
                        147u,
                    _ => (uint)Settings.Shadows.DebugView
                };
            }

            return (uint)Settings.Shadows.DebugView;
        }

        private float GetParticleDeltaSeconds()
        {
            float fixedDeltaSeconds = Settings.Particles.FixedSimulationDeltaSeconds;
            if (fixedDeltaSeconds > 0.0f)
                return fixedDeltaSeconds;

            long now = Stopwatch.GetTimestamp();
            if (_lastParticleTimestamp == 0)
            {
                _lastParticleTimestamp = now;
                return 1.0f / 60.0f;
            }

            float delta = (float)Stopwatch.GetElapsedTime(_lastParticleTimestamp, now).TotalSeconds;
            _lastParticleTimestamp = now;
            return delta;
        }


        private HiZVisibilityPolicyDecision PlanHiZVisibility(
            Scene scene,
            ICamera camera,
            bool depthPrePassEnabled,
            bool featureIsolationDisablesHiZ,
            bool hybridReflectionRequired)
        {
            bool sceneChanged = _lastHiZScene == null || !ReferenceEquals(_lastHiZScene, scene);
            bool cameraCut = DetectHiZCameraCut(camera);
            UpdatePreviousHiZCameraMotionSuppression(camera, cameraCut);
            _lastHiZScene = scene;
            _lastHiZCameraPosition = camera.Position;
            _lastHiZCameraForward = camera.Forward.Normalized();
            _hasLastHiZCameraPose = true;
            var completedTimings = _gpuTimestamps.LastCompletedSnapshot;
            CompletedHiZCounterResolution completedCounters = ResolveCompletedHiZCounters();

            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: depthPrePassEnabled,
                HiZOcclusionEnabled: EnableHiZOcclusion && Settings.HiZOcclusion.Enabled,
                FeatureIsolationDisablesHiZ: featureIsolationDisablesHiZ,
                RequestedTestMode: Settings.HiZTestMode,
                SceneChanged: sceneChanged,
                CameraCut: cameraCut,
                AdaptiveEnabled: EnableAdaptiveHiZOcclusion && Settings.HiZOcclusion.AdaptiveEnabled,
                ProductionCountersAvailable: completedCounters.Source != HiZCounterSource.Unavailable,
                CounterSource: completedCounters.Source,
                CompletedHiZTested: completedCounters.Tested,
                CompletedHiZCulled: completedCounters.Culled,
                DepthPrePassRequiredByOtherFeatures: IsDepthPrePassRequiredByNonHiZOcclusionFeatures(),
                CompletedDepthPrePassMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("DepthPrePass"),
                CompletedHiZBuildMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("HiZBuildPass"),
                CompletedSceneSubmissionCompactionMicroseconds: completedTimings.GetGpuMicrosecondsOrZero(
                    "SceneOpaqueCompactionPass"),
                CompletedForwardOpaqueMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("ForwardPlusPass"));

            bool previousForceOn = Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn;
            bool previousForceProbe = Settings.HiZVisibilityPolicy.ForceAdaptiveProbe;
            Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn = previousForceOn || Settings.HiZOcclusion.ForceOn;
            Settings.HiZVisibilityPolicy.ForceAdaptiveProbe = previousForceProbe || Settings.HiZOcclusion.ForceProbe;
            try
            {
                HiZVisibilityPolicyDecision decision = HiZVisibilityPolicy.Plan(
                    input,
                    Settings.HiZVisibilityPolicy,
                    _hizVisibilityPolicyState);
                // The occlusion policy intentionally drops scene/camera
                // invalidation when it is disabled. Reflection history still
                // needs those signals when it independently retains Hi-Z.
                return HybridReflectionHiZPolicy.RetainPyramid(
                    decision,
                    hybridReflectionRequired,
                    sceneChanged,
                    cameraCut);
            }
            finally
            {
                Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn = previousForceOn;
                Settings.HiZVisibilityPolicy.ForceAdaptiveProbe = previousForceProbe;
            }
        }

        private bool IsDepthPrePassRequiredByNonHiZOcclusionFeatures()
        {
            return Settings.AmbientOcclusion.Enabled;
        }

        private void UpdatePreviousHiZCameraMotionSuppression(ICamera camera, bool cameraCut)
        {
            HiZOcclusionSettings settings = Settings.HiZOcclusion;
            if (!settings.DisablePreviousFrameCullingDuringFastCameraMotion)
            {
                _previousHiZCameraMotionSuppressionFramesRemaining = 0;
                _previousHiZCameraMotionSuppressedThisFrame = false;
                return;
            }

            if (!cameraCut && IsPreviousHiZFastCameraMotion(camera, settings))
            {
                _previousHiZCameraMotionSuppressionFramesRemaining = Math.Max(
                    _previousHiZCameraMotionSuppressionFramesRemaining,
                    settings.CameraMotionSuppressionFrames);
            }

            _previousHiZCameraMotionSuppressedThisFrame = _previousHiZCameraMotionSuppressionFramesRemaining > 0;
            if (_previousHiZCameraMotionSuppressionFramesRemaining > 0)
                _previousHiZCameraMotionSuppressionFramesRemaining--;
        }

        private bool IsPreviousHiZFastCameraMotion(ICamera camera, HiZOcclusionSettings settings)
        {
            if (!_hasLastHiZCameraPose)
                return false;

            if (Vector3.DistanceSquared(camera.Position, _lastHiZCameraPosition) >
                settings.FastCameraMotionDistanceThreshold * settings.FastCameraMotionDistanceThreshold)
            {
                return true;
            }

            Vector3 currentForward = camera.Forward.Normalized();
            Vector3 previousForward = _lastHiZCameraForward.Normalized();
            if (currentForward == Vector3.Zero || previousForward == Vector3.Zero)
                return false;

            return Vector3.Dot(currentForward, previousForward) < settings.FastCameraMotionForwardDotThreshold;
        }

        private HiZConsumerDecision ResolveHiZConsumers(
            SceneRenderingData sceneData,
            bool hybridReflection)
        {
            bool sceneSubmissionPreviousHiZ = Settings.SceneSubmission.GpuCompactionEnabled &&
                                              Settings.HiZOcclusion.Enabled &&
                                              Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled &&
                                              sceneData.DepthPrePassEnabled &&
                                              sceneData.OpaqueMeshletCount > 0;
            bool forwardVisibilityCurrentHiZ = Settings.SceneSubmission.GpuCompactionEnabled &&
                                               Settings.HiZOcclusion.Enabled &&
                                               Settings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled &&
                                               sceneData.DepthPrePassEnabled &&
                                               sceneData.OpaqueMeshletCount > 0;
            bool legacyForwardTask = !Settings.SceneSubmission.GpuCompactionEnabled &&
                                     sceneData.DepthPrePassEnabled &&
                                     sceneData.OpaqueMeshletCount > 0;
            bool foliage = Settings.Foliage.HiZCullingEnabled && sceneData.FoliageClusterCount > 0;

            int count = 0;
            if (forwardVisibilityCurrentHiZ)
                count++;
            if (sceneSubmissionPreviousHiZ)
                count++;
            if (legacyForwardTask)
                count++;
            if (foliage)
                count++;
            if (hybridReflection)
                count++;
            if (count == 0)
                return HiZConsumerDecision.None;

            return new HiZConsumerDecision(
                count,
                BuildHiZConsumerSummary(
                    forwardVisibilityCurrentHiZ,
                    sceneSubmissionPreviousHiZ,
                    legacyForwardTask,
                    foliage,
                    hybridReflection),
                forwardVisibilityCurrentHiZ,
                sceneSubmissionPreviousHiZ,
                legacyForwardTask,
                foliage,
                hybridReflection);
        }

        private static string BuildHiZConsumerSummary(
            bool forwardVisibilityCurrentHiZ,
            bool sceneSubmissionPreviousHiZ,
            bool legacyForwardTask,
            bool foliage,
            bool hybridReflection)
        {
            string summary = string.Empty;
            AppendHiZConsumer(ref summary, forwardVisibilityCurrentHiZ, "ForwardVisibilityCurrentHiZ");
            AppendHiZConsumer(ref summary, sceneSubmissionPreviousHiZ, "SceneSubmissionPreviousHiZ");
            AppendHiZConsumer(ref summary, legacyForwardTask, "LegacyForwardTask");
            AppendHiZConsumer(ref summary, foliage, "Foliage");
            AppendHiZConsumer(ref summary, hybridReflection,
                "HybridReflectionSsr");
            return summary.Length == 0 ? "None" : summary;
        }

        private static void AppendHiZConsumer(ref string summary, bool enabled, string name)
        {
            if (!enabled)
                return;

            summary = summary.Length == 0 ? name : summary + "," + name;
        }

        private static string BuildHiZPolicyReasonWithConsumers(
            HiZVisibilityPolicyDecision decision,
            HiZConsumerDecision consumers)
        {
            if (consumers.Count == 0)
                return decision.Reason;

            if (decision.Status == HiZVisibilityPolicyStatus.WarmingUp)
            {
                string reason = decision.Reason + " Active Hi-Z consumers: " + consumers.Summary + ".";
                if (consumers.SceneSubmissionPreviousHiZ && decision.PyramidInvalidated)
                    reason += " Scene submission compaction is waiting for a valid previous Hi-Z pyramid.";
                return reason;
            }

            if (!decision.UseHiZForOcclusion &&
                consumers.SceneSubmissionPreviousHiZ &&
                decision.PyramidInvalidated)
            {
                return decision.Reason + " Scene submission compaction is waiting for a valid previous Hi-Z pyramid.";
            }

            return decision.Reason;
        }

        private void UpdateHiZFallbackDiagnostics(SceneRenderingData sceneData)
        {
            if (!Settings.HiZOcclusion.Enabled || !EnableHiZOcclusion)
            {
                sceneData.HiZFallbackPath = HiZFallbackPaths.Disabled;
                sceneData.HiZFallbackReason = "Hi-Z occlusion disabled.";
                return;
            }

            if (!sceneData.OcclusionCullingEnabled)
            {
                sceneData.HiZFallbackPath = HiZFallbackPaths.Disabled;
                sceneData.HiZFallbackReason = string.IsNullOrWhiteSpace(sceneData.HiZPolicyReason)
                    ? "Hi-Z occlusion inactive for this frame."
                    : sceneData.HiZPolicyReason;
                return;
            }

            if (!Settings.SceneSubmission.GpuCompactionEnabled ||
                sceneData.SceneSubmissionFallbackReason.Length > 0 ||
                sceneData.SceneSubmissionForwardPath == SceneSubmissionDiagnosticsPolicy.ForwardPathCpu ||
                sceneData.SceneSubmissionForwardPath == SceneSubmissionDiagnosticsPolicy.ForwardPathCpuFallback)
            {
                sceneData.HiZFallbackPath = HiZFallbackPaths.LegacyForward;
                sceneData.HiZFallbackReason = sceneData.SceneSubmissionFallbackReason.Length > 0
                    ? sceneData.SceneSubmissionFallbackReason
                    : "GPU scene submission unavailable; using legacy forward path.";
                return;
            }

            if (sceneData.ForwardVisibilityCompactionActive)
            {
                sceneData.HiZFallbackPath = HiZFallbackPaths.CurrentFrameForwardVisibility;
                sceneData.HiZFallbackReason = "Current-frame forward visibility compaction active.";
                return;
            }

            if (sceneData.PreviousHiZFrameValid)
            {
                sceneData.HiZFallbackPath = HiZFallbackPaths.PreviousFrameSceneSubmission;
                sceneData.HiZFallbackReason = sceneData.ForwardVisibilityCompactionSkipReason.Length > 0
                    ? sceneData.ForwardVisibilityCompactionSkipReason
                    : "Previous-frame scene-submission Hi-Z active.";
                return;
            }

            sceneData.HiZFallbackPath = HiZFallbackPaths.CompactedNoHiZ;
            if (sceneData.ForwardVisibilityCompactionSkipReason.Length > 0)
            {
                sceneData.HiZFallbackReason = sceneData.ForwardVisibilityCompactionSkipReason;
                return;
            }

            if (!Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled)
            {
                sceneData.HiZFallbackReason =
                    "Previous-frame scene-submission Hi-Z disabled; using compacted forward buffers without Hi-Z rejection.";
                return;
            }

            sceneData.HiZFallbackReason =
                "Previous Hi-Z history invalid; using compacted forward buffers without Hi-Z rejection.";
        }

        private readonly record struct HiZConsumerDecision(
            int Count,
            string Summary,
            bool ForwardVisibilityCurrentHiZ,
            bool SceneSubmissionPreviousHiZ,
            bool LegacyForwardTask,
            bool Foliage,
            bool HybridReflection)
        {
            public static HiZConsumerDecision None { get; } = new(
                0,
                "None",
                ForwardVisibilityCurrentHiZ: false,
                SceneSubmissionPreviousHiZ: false,
                LegacyForwardTask: false,
                Foliage: false,
                HybridReflection: false);
        }

        private CompletedHiZCounterResolution ResolveCompletedHiZCounters()
        {
            if (Settings.SceneSubmission.GpuCompactionEnabled && _completedForwardVisibilityCounters.IsValid)
            {
                return new CompletedHiZCounterResolution(
                    HiZCounterSource.ForwardVisibilityCompaction,
                    ClampUIntToInt(_completedForwardVisibilityCounters.HiZTestedCount),
                    ClampUIntToInt(_completedForwardVisibilityCounters.HiZRejectedCount));
            }

            if (Settings.SceneSubmission.GpuCompactionEnabled && _completedSceneSubmissionCounters.IsValid)
            {
                return new CompletedHiZCounterResolution(
                    HiZCounterSource.SceneSubmissionCompaction,
                    ClampUIntToInt(_completedSceneSubmissionCounters.HiZTestedCount),
                    ClampUIntToInt(_completedSceneSubmissionCounters.HiZRejectedCount));
            }

            if (MeshletDiagnosticCountersActive)
            {
                return new CompletedHiZCounterResolution(
                    HiZCounterSource.LegacyTaskShader,
                    _completedGpuCounters.ForwardOcclusionTested,
                    _completedGpuCounters.ForwardOcclusionCulled);
            }

            return new CompletedHiZCounterResolution(HiZCounterSource.Unavailable, 0, 0);
        }

        private readonly record struct CompletedHiZCounterResolution(
            HiZCounterSource Source,
            int Tested,
            int Culled);

        private bool DetectHiZCameraCut(ICamera camera)
        {
            if (!_hasLastHiZCameraPose)
                return true;

            HiZVisibilityPolicySettings policy = Settings.HiZVisibilityPolicy;
            if (Vector3.DistanceSquared(camera.Position, _lastHiZCameraPosition) >=
                policy.CameraCutDistance * policy.CameraCutDistance)
                return true;

            Vector3 currentForward = camera.Forward.Normalized();
            Vector3 previousForward = _lastHiZCameraForward.Normalized();
            if (currentForward == Vector3.Zero || previousForward == Vector3.Zero)
                return false;

            return Vector3.Dot(currentForward, previousForward) < policy.CameraCutForwardDotThreshold;
        }

        private void EnsureMeshPipelineDiagnosticVariant()
        {
            bool diagnosticCountersEnabled = Settings.Diagnostics.GpuMeshletCountersEnabled;
            bool meshNeedsRecreate = _meshPipeline != null &&
                                     _meshPipeline.GpuMeshletCountersEnabled != diagnosticCountersEnabled;
            // Foliage has a dedicated mesh-shader shadow path. Keep it in
            // lockstep with the opaque diagnostic variant so a runtime toggle
            // never produces a partial caster-attribution capture.
            bool foliageNeedsRecreate = _foliagePipeline != null &&
                                        _foliagePipeline.IsPrepared &&
                                        _foliagePipeline.GpuMeshletCountersEnabled != diagnosticCountersEnabled;
            if (!meshNeedsRecreate && !foliageNeedsRecreate)
            {
                return;
            }

            RecordDeviceWaitIdle(
                RuntimeStallReason.DeviceWaitIdle,
                "Mesh diagnostic pipeline recreate",
                () =>
                {
                    Result result = _context.Api.DeviceWaitIdle(_context.Device);
                    if (result != Result.Success)
                        throw new VulkanException(
                            "Failed to wait for device before recreating mesh diagnostic pipelines", result);
                });

            if (meshNeedsRecreate)
                _meshPipeline!.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            if (foliageNeedsRecreate)
            {
                _foliagePipeline!.Recreate(
                    RenderTargetManager.SceneColorFormat,
                    RenderTargetManager.MotionVectorFormat,
                    _swapchain.DepthFormat);
            }

            System.Diagnostics.Debug.WriteLine(
                diagnosticCountersEnabled
                    ? "GPU meshlet diagnostic counters enabled; using diagnostic opaque and foliage shadow variants."
                    : "GPU meshlet diagnostic counters disabled; using normal opaque and foliage shadow variants.");
        }

        private GPUShadowData CreateDirectionalShadowData(
            ICamera camera,
            LightFrameSnapshot lightSnapshot,
            out bool enabled,
            out int lightIndex)
        {
            lightIndex = -1;
            enabled = false;
            if (_directionalShadowResources == null)
                return default;

            ShadowSettings shadowSettings = Settings.Shadows;
            Light shadowLight = default;
            bool hasShadowLight = lightSnapshot.DirectionalLightCount > 0 &&
                                  lightSnapshot.HasShadowCastingDirectionalLight;
            if (hasShadowLight)
            {
                lightIndex = lightSnapshot.FirstShadowCastingDirectionalLightIndex;
                shadowLight = lightSnapshot.FirstShadowCastingDirectionalLight;
            }

            EnsureDirectionalShadowResources(hasShadowLight);
            enabled = shadowSettings.DirectionalShadowsEnabled && hasShadowLight &&
                      _directionalShadowResources.HasImage;

            GPUShadowData shadowData = enabled
                ? DirectionalShadowDataBuilder.Build(
                    camera,
                    shadowLight.Direction,
                    shadowSettings,
                    lightIndex,
                    shadowLight.ShadowStrength,
                    _directionalShadowStabilizationState,
                    lightIndex >= 0 && lightIndex < lightSnapshot.StableIdentities.Length
                        ? lightSnapshot.StableIdentities.Span[lightIndex]
                        : 0UL,
                    _directionalShadowResources.ResourceGeneration)
                : DirectionalShadowDataBuilder.Build(
                    camera,
                    new System.Numerics.Vector3(0f, -1f, 0f),
                    shadowSettings,
                    -1,
                    1f);

            if (!enabled)
            {
                shadowData.Settings.X = 0f;
                shadowData.Indices.X = 0f;
                shadowData.Indices.W = -1f;
            }

            return shadowData;
        }

        private Vector3 ResolveFogDirectionalInscatteringDirection(LightFrameSnapshot lightSnapshot)
        {
            Vector3 explicitDirection = Settings.Fog.DirectionalInscatteringDirection;
            if (explicitDirection.LengthSquared() > 0.000001f)
                return explicitDirection.Normalized();

            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light.Type != LightType.Directional)
                    continue;

                var direction = new Vector3(light.Direction.X, light.Direction.Y, light.Direction.Z);
                if (direction.LengthSquared() > 0.000001f)
                    return direction.Normalized();
            }

            return new Vector3(-0.35f, -0.75f, -0.55f).Normalized();
        }

        private void PrepareDirectionalShadows(
            SceneRenderingData sceneData,
            GPUShadowData shadowData,
            bool enabled,
            int lightIndex,
            Vector3 lightDirection)
        {
            if (_directionalShadowResources == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            GPUDirectionalShadowParameters shadowParameters =
                DirectionalShadowDataBuilder.BuildParameters(
                    shadowSettings,
                    _directionalShadowStabilizationState.Diagnostics,
                    DirectionalShadowMode.Cascaded);

            sceneData.DirectionalShadowPassEnabled = enabled;
            ulong recordSignature =
                CreateDirectionalShadowRecordSignature(sceneData, shadowData, enabled, shadowSettings);
            sceneData.DirectionalShadowRecordSkipped = enabled &&
                                                       _hasDirectionalShadowRecordSignature &&
                                                       _lastDirectionalShadowRecordSignature == recordSignature;
            if (enabled && !sceneData.DirectionalShadowRecordSkipped)
            {
                _lastDirectionalShadowRecordSignature = recordSignature;
                _hasDirectionalShadowRecordSignature = true;
            }

            if (!enabled)
                _hasDirectionalShadowRecordSignature = false;
            sceneData.DirectionalShadowMapSize = shadowSettings.DirectionalShadowMapSize;
            sceneData.DirectionalShadowCascadeCount = shadowSettings.DirectionalCascadeCount;
            sceneData.DirectionalShadowMaxDistance = shadowSettings.MaxShadowDistance;
            sceneData.DirectionalShadowCascadeBlendFraction = shadowSettings.DirectionalCascadeBlendFraction;
            sceneData.ShadowedDirectionalLightIndex = enabled ? lightIndex : -1;
            sceneData.ShadowDebugView = shadowSettings.DebugView;
            sceneData.DirectionalShadowPreviewCascade = Math.Clamp(
                shadowSettings.DirectionalShadowPreviewCascade,
                0,
                Math.Max(0, shadowSettings.DirectionalCascadeCount - 1));
            sceneData.ShadowNormalBias = shadowSettings.NormalBias;
            sceneData.ShadowSlopeScaledDepthBias = shadowSettings.SlopeScaledDepthBias;
            sceneData.DirectionalShadowPcfRadius = shadowSettings.PcfRadius;
            sceneData.ShadowData = shadowData;
            sceneData.DirectionalShadowParameters = shadowParameters;
            sceneData.DirectionalShadowLightDirection = enabled
                ? lightDirection
                : Vector3.Zero;
            if (enabled)
            {
                _directionalShadowStabilizationState.Diagnostics.CopyTo(
                    sceneData.DirectionalShadowCascadeFitDiagnostics);
            }

            int frameIndex = sceneData.FrameIndex;
            if ((uint)frameIndex < (uint)_directionalShadowCasterFrameCaptures.Length &&
                enabled &&
                MeshletDiagnosticCountersActive)
            {
                _directionalShadowCasterFrameCaptures[frameIndex] =
                    DirectionalShadowCasterFrameCapture.Create(
                        sceneData.DdgiFrameSerial,
                        _directionalShadowResources.ResourceGeneration,
                        sceneData.DirectionalShadowCascadeCount,
                        sceneData.CameraPosition,
                        lightDirection,
                        shadowData);
            }
            else if ((uint)frameIndex < (uint)_directionalShadowCasterFrameCaptures.Length)
            {
                _directionalShadowCasterFrameCaptures[frameIndex] =
                    DirectionalShadowCasterFrameCapture.Empty;
            }
        }

        private void EnsureDirectionalShadowResources(bool requiresShadowMap)
        {
            if (_directionalShadowResources == null)
                return;

            if (_directionalShadowResources.Ensure(Settings.Shadows, requiresShadowMap))
                _directionalShadowResources.Register(_bindlessHeap, _swapchain.DepthImageView);
        }

        private void EnsureLocalShadowResources(int selectedSpotShadowCount, int selectedPointShadowCount)
        {
            if (_spotShadowAtlas == null || _pointShadowCubemapArray == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            if (_spotShadowAtlas.Ensure(shadowSettings, selectedSpotShadowCount))
            {
                _spotShadowAtlas.Register(_bindlessHeap, _swapchain.DepthImageView);
                _hasUploadedSpotShadows = false;
                _hasUploadedLocalShadowIndices = false;
            }

            if (_pointShadowCubemapArray.Ensure(shadowSettings, selectedPointShadowCount))
            {
                _pointShadowCubemapArray.Register(_bindlessHeap, _swapchain.DepthImageView);
                _hasUploadedPointShadows = false;
            }
        }

        private DirectionalShadowQualificationGateResult
            EvaluateDirectionalShadowQualification(
                DirectionalShadowMode mode,
                bool csmTemporalRequested,
                uint width,
                uint height,
                in RaySceneReadinessSnapshot readiness)
        {
            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(
                _context.PhysicalDevice,
                &properties);
            PerformanceCaptureBuildIdentity captureIdentity =
                _performanceCaptureMetadataProvider.BuildIdentity;
            var runtimeContext = new DirectionalShadowQualificationRuntimeContext(
                mode,
                csmTemporalRequested,
                width,
                height,
                Settings.AntiAliasing.EffectiveMode,
                Settings.QualityPreset,
                properties.VendorID,
                properties.DeviceID,
                properties.DriverVersion,
                properties.ApiVersion,
                captureIdentity.ShaderBundleHash,
                DirectionalShadowSettingsFingerprint.Compute(Settings),
                captureIdentity.Commit,
                captureIdentity.DirtyWorktreeState,
                readiness.ExactCategories,
                readiness.ProxyCategories);
            return _directionalShadowQualificationManifest.Evaluate(
                runtimeContext);
        }

        private void PrepareDirectionalShadowFrame(
            SceneRenderingData sceneData,
            LightFrameSnapshot lightSnapshot,
            bool cascadedShadowsAvailable)
        {
            ShadowSettings settings = Settings.Shadows;
            RaySceneReadinessSnapshot readiness = _accelerationStructureManager?.ReadinessSnapshot ??
                                                  RaySceneReadinessSnapshot.Unavailable(
                                                      RaySceneRequirement.ForDirectionalShadows(settings).Consumers,
                                                      "the shared acceleration-structure manager is unavailable");
            sceneData.RaySceneReadiness = readiness;

            DirectionalShadowMode requestedMode =
                settings.RequestedDirectionalShadowMode;
            float softAngularRadiusRadians =
                Settings.Environment.SunAngularDiameterDegrees *
                settings.DirectionalSoftAngularDiameterScale *
                0.5f * (MathF.PI / 180f);
            bool softCollapsesToHard =
                requestedMode == DirectionalShadowMode.RayQuerySoft &&
                softAngularRadiusRadians <= 1.0e-6f;
            uint maskWidth = _renderTargets?.SceneDepth.Extent.Width ??
                             sceneData.ScreenWidth;
            uint maskHeight = _renderTargets?.SceneDepth.Extent.Height ??
                              sceneData.ScreenHeight;
            // Qualification manifests are retained as optional evidence only.
            // Enabled and the legacy Auto value both execute through the same
            // runtime allocation/reset gates without consulting a manifest.
            bool csmTemporalAutoRequested = false;
            DirectionalShadowQualificationGateResult csmTemporalQualification =
                csmTemporalAutoRequested
                    ? EvaluateDirectionalShadowQualification(
                        DirectionalShadowMode.Cascaded,
                        csmTemporalRequested: true,
                        maskWidth,
                        maskHeight,
                        readiness)
                    : DirectionalShadowQualificationGateResult.Reject(
                        "directional-shadow-csm-temporal-auto-not-requested");
            settings.DirectionalCsmTemporalQualificationApproved = false;
            bool csmTemporalActive = requestedMode == DirectionalShadowMode.Cascaded &&
                                     settings.EffectiveDirectionalCsmTemporalEnabled;
            bool detailedScreenDiagnostics = settings.DebugView is
                ShadowDebugView.DirectionalRayHitDistance or
                ShadowDebugView.DirectionalRayCandidateCount or
                ShadowDebugView.DirectionalHistoryRejection;
            sceneData.DirectionalShadowRayCountersEnabled =
                Settings.Diagnostics.DirectionalShadowReceiverCountersEnabled ||
                settings.DebugView != ShadowDebugView.None;
            if (csmTemporalActive)
            {
                try
                {
                    csmTemporalActive =
                        _directionalShadowHistoryResources?.Ensure(
                            maskWidth,
                            maskHeight,
                            detailedScreenDiagnostics) == true;
                }
                catch (Exception exception)
                {
                    csmTemporalActive = false;
                    sceneData.DirectionalShadowHistoryResetReason =
                        DirectionalShadowHistoryResetReason.ResourceRecreated;
                    System.Diagnostics.Debug.WriteLine(
                        $"Directional CSM temporal allocation was omitted: {exception.Message}");
                }
            }

            bool rayMaskAvailable = requestedMode == DirectionalShadowMode.Cascaded;
            if (cascadedShadowsAvailable &&
                requestedMode is (DirectionalShadowMode.HybridContact or
                    DirectionalShadowMode.RayQueryHard or
                    DirectionalShadowMode.RayQuerySoft))
            {
                rayMaskAvailable = _directionalRayShadowPass?.EnsureResources(
                    maskWidth,
                    maskHeight,
                    sceneData.DdgiFrameSerial,
                    requiresHistory: requestedMode ==
                                     DirectionalShadowMode.RayQuerySoft &&
                                     !softCollapsesToHard,
                    detailedDiagnostics: detailedScreenDiagnostics) == true;
            }

            bool transparentRayReceiverRequired =
                sceneData.TransparentPassEnabled &&
                sceneData.TransparentReceiveShadows &&
                sceneData.TransparentShadowReceiverMeshletCount > 0;
            bool transparentRayVariantsAdmitted =
                _raySceneDescriptorBank?.IsAvailable == true &&
                _meshPipeline.RayTransparentPipelinesAdmitted;

            ShadowFrameCandidate ResolveCandidate(
                bool transparentRayVariantsAvailable) =>
                _shadowFramePlanner.ResolveCandidate(
                    new ShadowFrameCandidateInput(
                        settings,
                        lightSnapshot.HasShadowCastingDirectionalLight,
                        _context.RayQuerySupported &&
                        _accelerationStructureManager?.Supported == true,
                        readiness,
                        rayMaskAvailable,
                        _directionalShadowHistoryResources?.IsAllocated == true,
                        transparentRayReceiverRequired,
                        transparentRayVariantsAvailable,
                        softCollapsesToHard,
                        cascadedShadowsAvailable,
                        _directionalRayShadowPass != null,
                        _directionalRayShadowPass?.FailureDetail ??
                        string.Empty));

            bool transparentRayVariantsAvailable =
                transparentRayVariantsAdmitted;
            ShadowFrameCandidate candidate = ResolveCandidate(
                transparentRayVariantsAvailable);
            if (candidate.EffectiveMode != DirectionalShadowMode.Cascaded &&
                lightSnapshot.HasShadowCastingDirectionalLight &&
                transparentRayReceiverRequired &&
                transparentRayVariantsAdmitted &&
                !_meshPipeline.TryEnsureRayTransparentPipelines())
            {
                transparentRayVariantsAvailable = false;
                candidate = ResolveCandidate(transparentRayVariantsAvailable);
            }

            DirectionalShadowQualificationGateResult rayQualification =
                DirectionalShadowQualificationGateResult.Reject(
                    "directional-shadow-ray-mode-not-effective");
            if (candidate.EffectiveMode != DirectionalShadowMode.Cascaded)
            {
                rayQualification = EvaluateDirectionalShadowQualification(
                    candidate.EffectiveMode,
                    csmTemporalRequested: false,
                    maskWidth,
                    maskHeight,
                    readiness);
            }

            ulong stableLightIdentity = 0UL;
            int lightIndex = sceneData.ShadowedDirectionalLightIndex;
            if ((uint)lightIndex < (uint)lightSnapshot.StableIdentities.Length)
                stableLightIdentity = lightSnapshot.StableIdentities.Span[lightIndex];
            bool csmDebugFallbackRequired = settings.DebugView is
                ShadowDebugView.CascadeOverlay or
                ShadowDebugView.ShadowMapPreview or
                ShadowDebugView.DirectionalCsmRayDifference;
            bool geometryDecalCsmFallbackRequired =
                sceneData.GeometryDecalsEnabled &&
                sceneData.DecalReceiveShadows &&
                sceneData.GeometryDecalMeshletCount > 0;
            sceneData.DirectionalShadowFramePlan =
                _shadowFramePlanner.CreatePlan(
                    new ShadowFramePlanInput(
                        Settings,
                        candidate,
                        csmTemporalActive,
                        csmTemporalQualification,
                        rayQualification,
                        sceneData.DdgiFrameSerial,
                        _lastDiagnostics.DirectionalShadowRuntime,
                        sceneData.DirectionalShadowCascadeCount,
                        stableLightIdentity,
                        _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual,
                        geometryDecalCsmFallbackRequired,
                        csmDebugFallbackRequired,
                        transparentRayVariantsAvailable,
                        _directionalShadowHistoryResources?.ResourceGeneration ??
                        0u,
                        softAngularRadiusRadians,
                        readiness));
            sceneData.DirectionalShadowHistoryBytes =
                sceneData.DirectionalShadowFramePlan.UsesScreenHistory
                    ? _directionalShadowHistoryResources?.EstimatedBytes ?? 0UL
                    : 0UL;
            if (!sceneData.DirectionalShadowFramePlan.UsesScreenHistory)
                _directionalShadowHistoryResources?.InvalidateHistoryState();

            GPUDirectionalShadowParameters parameters =
                DirectionalShadowDataBuilder.BuildParameters(
                    settings,
                    sceneData.DirectionalShadowCascadeFitDiagnostics,
                    sceneData.DirectionalShadowFramePlan.EffectiveMode,
                    sceneData.DirectionalShadowFramePlan.SunAngularRadiusRadians,
                    readiness,
                    sceneData.DirectionalShadowFramePlan.UsesCsmTemporal,
                    sceneData.DirectionalShadowFramePlan.QualificationLevel,
                    sceneData.DirectionalShadowFramePlan.ScreenResourceGeneration,
                    historyValid: sceneData.DirectionalShadowFramePlan.UsesScreenHistory);
            sceneData.DirectionalShadowParameters = parameters;
            _directionalShadowResources?.UploadShadowData(
                _stagingRing,
                _currentCommandBuffer,
                sceneData.ShadowData,
                parameters);
        }

        private void PrepareAreaRayShadows(
            SceneRenderingData sceneData,
            LocalShadowSelection selection,
            bool shadowsAllowed)
        {
            bool requested = shadowsAllowed &&
                             Settings.Shadows.AreaShadowsEnabled &&
                             selection.AreaLights.Length > 0;
            bool readiness = requested &&
                             sceneData.RaySceneReadiness.IsReady(
                                 RaySceneConsumer.AreaLightShadows,
                                 RaySceneGeometryCategory.DirectionalShadowDefault);
            bool resources = readiness &&
                             _areaRayShadowPass?.EnsureResources(
                                 _lastSceneRenderExtent.Width,
                                 _lastSceneRenderExtent.Height,
                                 sceneData.DdgiFrameSerial) == true;
            bool enabled = requested && readiness && resources;
            sceneData.AreaRayShadowPassEnabled = enabled;
            sceneData.AreaShadowSelectedCount = enabled
                ? selection.AreaLights.Length
                : 0;
            sceneData.AreaShadowLights = enabled
                ? selection.AreaLights
                : [];
            sceneData.AreaRayShadowMaskWidth = enabled
                ? _areaRayShadowPass?.Width ?? 0u
                : 0u;
            sceneData.AreaRayShadowMaskHeight = enabled
                ? _areaRayShadowPass?.Height ?? 0u
                : 0u;
            sceneData.AreaRayShadowMaskBytes = enabled
                ? checked((_areaRayShadowPass?.BufferBytes ?? 0UL) *
                          (ulong)FramesInFlight)
                : 0UL;
            sceneData.AreaRayShadowResourceGeneration = enabled
                ? _areaRayShadowPass?.ResourceGeneration ?? 0u
                : 0u;
            sceneData.AreaRayShadowFailureDetail = enabled || !requested
                ? string.Empty
                : !readiness
                    ? sceneData.RaySceneReadiness.FailureDetail
                    : _areaRayShadowPass?.FailureDetail ??
                      "area ray-shadow pass is unavailable";
        }

        private static float ResolveMaximumAreaShadowRayDistance(
            ReadOnlySpan<SelectedLocalShadow> selectedLights)
        {
            float maximum = 0f;
            for (int index = 0; index < selectedLights.Length; index++)
            {
                maximum = MathF.Max(
                    maximum,
                    AnalyticalLightGeometry.GetMaximumSurfaceSampleDistanceWithinRange(
                        selectedLights[index].Light));
            }

            return float.IsFinite(maximum) ? maximum : 0f;
        }

        private void PrepareLocalShadows(
            SceneRenderingData sceneData,
            LocalShadowSelection selection,
            int lightCount)
        {
            if (_spotShadowAtlas == null || _pointShadowCubemapArray == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            sceneData.SpotShadowPcfRadius = shadowSettings.SpotPcfRadius;
            sceneData.PointShadowPcfRadius = shadowSettings.PointPcfRadius;

            Span<GPUSpotShadow> spotShadows = _spotShadowScratch.AsSpan(0, selection.SpotLights.Length);
            Span<GPUPointShadow> pointShadows = _pointShadowScratch.AsSpan(0, selection.PointLights.Length);
            Span<GPULocalLightShadowIndex> shadowIndices = _localShadowIndexScratch.AsSpan(0, lightCount);
            ReadOnlySpan<SelectedLocalShadow> areaShadows =
                sceneData.AreaRayShadowPassEnabled
                    ? selection.AreaLights
                    : ReadOnlySpan<SelectedLocalShadow>.Empty;
            LocalShadowDataBuilder.FillSpotShadows(selection.SpotLights, shadowSettings, spotShadows);
            LocalShadowDataBuilder.FillPointShadows(selection.PointLights, shadowSettings, pointShadows);
            LocalShadowDataBuilder.FillShadowIndexMap(
                lightCount,
                selection.SpotLights,
                selection.PointLights,
                areaShadows,
                shadowIndices);

            ulong spotSignature = CreateSpotShadowSignature(selection.SpotLights, shadowSettings);
            if (!_hasUploadedSpotShadows || _lastSpotShadowUploadSignature != spotSignature)
            {
                _spotShadowAtlas.UploadSpotShadows(_stagingRing, _currentCommandBuffer, spotShadows);
                _lastSpotShadowUploadSignature = spotSignature;
                _hasUploadedSpotShadows = true;
            }

            ulong indexSignature = CreateLocalShadowIndexSignature(
                lightCount,
                selection.SpotLights,
                selection.PointLights,
                areaShadows);
            if (!_hasUploadedLocalShadowIndices || _lastLocalShadowIndexUploadSignature != indexSignature)
            {
                _spotShadowAtlas.UploadShadowIndices(_stagingRing, _currentCommandBuffer, shadowIndices);
                _lastLocalShadowIndexUploadSignature = indexSignature;
                _hasUploadedLocalShadowIndices = true;
            }

            ulong pointSignature = CreatePointShadowSignature(selection.PointLights, shadowSettings);
            if (!_hasUploadedPointShadows || _lastPointShadowUploadSignature != pointSignature)
            {
                _pointShadowCubemapArray.Upload(_stagingRing, _currentCommandBuffer, pointShadows);
                _lastPointShadowUploadSignature = pointSignature;
                _hasUploadedPointShadows = true;
            }

            sceneData.SpotShadowData = _spotShadowScratch;
            sceneData.PointShadowData = _pointShadowScratch;
            sceneData.LocalLightShadowIndices = _localShadowIndexScratch;
            sceneData.SpotShadowsEnabled = shadowSettings.SpotShadowsEnabled;
            ulong spotRecordSignature = HashAdd(HashAdd(spotSignature, sceneData.LocalStaticShadowMeshletDrawSignature),
                spotShadows.Length);
            spotRecordSignature = HashAdd(spotRecordSignature, sceneData.LocalStaticShadowMeshletCount);
            spotRecordSignature = HashAdd(spotRecordSignature, sceneData.LocalDynamicShadowMeshletDrawSignature);
            spotRecordSignature = HashAdd(spotRecordSignature, sceneData.LocalDynamicShadowMeshletCount);
            spotRecordSignature = AddAnimatedShadowFrameSignature(
                spotRecordSignature,
                sceneData,
                sceneData.LocalDynamicShadowMeshletCount > 0 ? sceneData.LocalShadowSkinnedObjectCount : 0);
            sceneData.SpotShadowRecordSkipped = spotShadows.Length > 0 &&
                                                _hasSpotShadowRecordSignature &&
                                                _lastSpotShadowRecordSignature == spotRecordSignature;
            if (spotShadows.Length > 0 && !sceneData.SpotShadowRecordSkipped)
            {
                _lastSpotShadowRecordSignature = spotRecordSignature;
                _hasSpotShadowRecordSignature = true;
            }

            if (spotShadows.Length == 0)
                _hasSpotShadowRecordSignature = false;
            sceneData.SpotShadowCandidateCount = selection.SpotCandidateCount;
            sceneData.SpotShadowSelectedCount = spotShadows.Length;
            sceneData.SpotShadowRejectedByBudgetCount = selection.SpotRejectedByBudgetCount;
            sceneData.SpotShadowAtlasSize = _spotShadowAtlas.AtlasSize;
            sceneData.SpotShadowTileSize = _spotShadowAtlas.TileSize;
            sceneData.SpotShadowAtlasCapacity = selection.SpotAtlasCapacity;
            sceneData.SpotShadowAtlasUsedTiles = spotShadows.Length;
            sceneData.PointShadowsEnabled = shadowSettings.PointShadowsEnabled;
            ulong pointRecordSignature =
                HashAdd(HashAdd(pointSignature, sceneData.LocalStaticShadowMeshletDrawSignature), pointShadows.Length);
            pointRecordSignature = HashAdd(pointRecordSignature, sceneData.LocalStaticShadowMeshletCount);
            pointRecordSignature = HashAdd(pointRecordSignature, sceneData.LocalDynamicShadowMeshletDrawSignature);
            pointRecordSignature = HashAdd(pointRecordSignature, sceneData.LocalDynamicShadowMeshletCount);
            pointRecordSignature = AddAnimatedShadowFrameSignature(
                pointRecordSignature,
                sceneData,
                sceneData.LocalDynamicShadowMeshletCount > 0 ? sceneData.LocalShadowSkinnedObjectCount : 0);
            sceneData.PointShadowRecordSkipped = pointShadows.Length > 0 &&
                                                 _hasPointShadowRecordSignature &&
                                                 _lastPointShadowRecordSignature == pointRecordSignature;
            if (pointShadows.Length > 0 && !sceneData.PointShadowRecordSkipped)
            {
                _lastPointShadowRecordSignature = pointRecordSignature;
                _hasPointShadowRecordSignature = true;
            }

            if (pointShadows.Length == 0)
                _hasPointShadowRecordSignature = false;
            sceneData.PointShadowCandidateCount = selection.PointCandidateCount;
            sceneData.PointShadowSelectedCount = pointShadows.Length;
            sceneData.PointShadowRejectedByBudgetCount = selection.PointRejectedByBudgetCount;
            sceneData.PointShadowMapSize = _pointShadowCubemapArray.MapSize;
            int pointShadowFaceCapacity = pointShadows.Length * 6;
            sceneData.PointShadowRenderedFaceCount =
                CountPointShadowFaces(sceneData.PointShadowFaceMasks, pointShadows.Length);
            sceneData.PointShadowSkippedFaceCount =
                Math.Max(0, pointShadowFaceCapacity - sceneData.PointShadowRenderedFaceCount);
        }

        private static ulong CreateSpotShadowSignature(ReadOnlySpan<SelectedLocalShadow> selectedLights,
            ShadowSettings settings)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, selectedLights.Length);
            hash = HashAdd(hash, settings.SpotShadowsEnabled);
            hash = HashAdd(hash, settings.SpotShadowAtlasSize);
            hash = HashAdd(hash, settings.SpotShadowTileSize);
            hash = HashAdd(hash, settings.SpotNormalBias);
            hash = HashAdd(hash, settings.SpotConstantDepthBias);
            hash = HashAdd(hash, settings.SpotPcfRadius);
            for (int i = 0; i < selectedLights.Length; i++)
                hash = HashAdd(hash, selectedLights[i]);
            return hash;
        }

        private static ulong CreateDirectionalShadowRecordSignature(
            SceneRenderingData sceneData,
            in GPUShadowData shadowData,
            bool enabled,
            ShadowSettings settings)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, enabled);
            hash = HashAdd(hash, sceneData.SceneContentRevision);
            hash = HashAdd(hash, settings.DirectionalShadowMapSize);
            hash = HashAdd(hash, settings.DirectionalCascadeCount);
            hash = HashAdd(hash, sceneData.OpaqueMeshletCount);
            hash = HashAdd(hash, sceneData.DirectionalShadowMeshletDrawSignature);
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletCount);
            hash = HashAdd(hash, sceneData.DirectionalStaticShadowMeshletDrawSignature);
            hash = HashAdd(hash, sceneData.DirectionalDynamicShadowMeshletCount);
            hash = HashAdd(hash, sceneData.DirectionalDynamicShadowMeshletDrawSignature);
            for (int i = 0; i < ShadowSettings.MaxDirectionalCascades; i++)
                hash = HashAdd(hash, sceneData.DirectionalShadowMeshletCounts[i]);
            hash = AddAnimatedShadowFrameSignature(
                hash,
                sceneData,
                sceneData.DirectionalDynamicShadowMeshletCount > 0 ? sceneData.DirectionalShadowSkinnedObjectCount : 0);

            fixed (GPUShadowData* shadowDataPtr = &shadowData)
            {
                byte* bytes = (byte*)shadowDataPtr;
                for (int i = 0; i < sizeof(GPUShadowData); i++)
                    hash = HashAdd(hash, bytes[i]);
            }

            return hash;
        }

        private static ulong AddAnimatedShadowFrameSignature(
            ulong hash,
            SceneRenderingData sceneData,
            int skinnedShadowCasterCount)
        {
            if (skinnedShadowCasterCount <= 0)
                return hash;

            hash = HashAdd(hash, sceneData.AnimationSkinningMode == AnimationSkinningMode.GpuCompute);
            hash = HashAdd(hash, sceneData.CurrentFrameIndex);
            hash = HashAdd(hash, sceneData.SkinningDispatchCount);
            hash = HashAdd(hash, sceneData.SkinnedVertexCount);
            return HashAdd(hash, skinnedShadowCasterCount);
        }

        private static ulong CreatePointShadowSignature(ReadOnlySpan<SelectedLocalShadow> selectedLights,
            ShadowSettings settings)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, selectedLights.Length);
            hash = HashAdd(hash, settings.PointShadowsEnabled);
            hash = HashAdd(hash, settings.PointShadowMapSize);
            hash = HashAdd(hash, settings.PointNormalBias);
            hash = HashAdd(hash, settings.PointConstantDepthBias);
            hash = HashAdd(hash, settings.PointPcfRadius);
            for (int i = 0; i < selectedLights.Length; i++)
                hash = HashAdd(hash, selectedLights[i]);
            return hash;
        }

        private static ulong CreateLocalShadowIndexSignature(
            int lightCount,
            ReadOnlySpan<SelectedLocalShadow> selectedSpots,
            ReadOnlySpan<SelectedLocalShadow> selectedPoints,
            ReadOnlySpan<SelectedLocalShadow> selectedAreas)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, lightCount);
            hash = HashAdd(hash, selectedSpots.Length);
            for (int i = 0; i < selectedSpots.Length; i++)
                hash = HashAdd(hash, selectedSpots[i].LightIndex);
            hash = HashAdd(hash, selectedPoints.Length);
            for (int i = 0; i < selectedPoints.Length; i++)
                hash = HashAdd(hash, selectedPoints[i].LightIndex);
            hash = HashAdd(hash, selectedAreas.Length);
            for (int i = 0; i < selectedAreas.Length; i++)
                hash = HashAdd(hash, selectedAreas[i].LightIndex);
            return hash;
        }

        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;

        public const uint SimpleDdgiDirtyReasonLight =
            DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonLight;

        public const uint SimpleDdgiDirtyReasonEmissive =
            DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonEmissive;

        public const uint SimpleDdgiDirtyReasonDynamicGeometry =
            DdgiSceneInvalidationCoordinator
                .SimpleDdgiDirtyReasonDynamicGeometry;

        private static ulong HashAdd(ulong hash, SelectedLocalShadow shadow)
        {
            hash = HashAdd(hash, shadow.LightIndex);
            return HashAdd(hash, shadow.Light);
        }

        private static ulong HashAdd(ulong hash, Light light)
        {
            hash = HashAdd(hash, light.Position);
            hash = HashAdd(hash, light.Intensity);
            hash = HashAdd(hash, light.Color);
            hash = HashAdd(hash, light.Range);
            hash = HashAdd(hash, light.Direction);
            hash = HashAdd(hash, light.SpotAngle);
            hash = HashAdd(hash, light.InnerSpotAngle);
            hash = HashAdd(hash, (int)light.AttenuationMode);
            hash = HashAdd(hash, light.AttenuationConstant);
            hash = HashAdd(hash, light.AttenuationLinear);
            hash = HashAdd(hash, light.AttenuationQuadratic);
            hash = HashAdd(hash, (int)light.Type);
            hash = HashAdd(hash, light.CastsShadows);
            hash = HashAdd(hash, light.ShadowStrength);
            hash = HashAdd(hash, light.ShadowMapSizeOverride);
            hash = HashAdd(hash, light.ShadowNearPlane);
            hash = HashAdd(hash, light.ShadowFarPlane);
            return HashAdd(hash, light.ShadowPriority);
        }

        private static ulong HashAdd(ulong hash, System.Numerics.Vector3 value)
        {
            hash = HashAdd(hash, value.X);
            hash = HashAdd(hash, value.Y);
            return HashAdd(hash, value.Z);
        }

        private static ulong HashAdd(ulong hash, Vector3 value)
        {
            hash = HashAdd(hash, value.X);
            hash = HashAdd(hash, value.Y);
            return HashAdd(hash, value.Z);
        }

        private static ulong HashAdd(ulong hash, Vector4 value)
        {
            hash = HashAdd(hash, value.X);
            hash = HashAdd(hash, value.Y);
            hash = HashAdd(hash, value.Z);
            return HashAdd(hash, value.W);
        }

        private static ulong HashAdd(ulong hash, Matrix4x4 value)
        {
            hash = HashAdd(hash, value.M11);
            hash = HashAdd(hash, value.M12);
            hash = HashAdd(hash, value.M13);
            hash = HashAdd(hash, value.M14);
            hash = HashAdd(hash, value.M21);
            hash = HashAdd(hash, value.M22);
            hash = HashAdd(hash, value.M23);
            hash = HashAdd(hash, value.M24);
            hash = HashAdd(hash, value.M31);
            hash = HashAdd(hash, value.M32);
            hash = HashAdd(hash, value.M33);
            hash = HashAdd(hash, value.M34);
            hash = HashAdd(hash, value.M41);
            hash = HashAdd(hash, value.M42);
            hash = HashAdd(hash, value.M43);
            return HashAdd(hash, value.M44);
        }

        private static ulong HashAdd(ulong hash, bool value) => HashAdd(hash, value ? 1u : 0u);

        private static ulong HashAdd(ulong hash, int value) => HashAdd(hash, unchecked((uint)value));

        private static ulong HashAdd(ulong hash, float value) => HashAdd(hash, BitConverter.SingleToUInt32Bits(value));

        private static ulong HashAdd(ulong hash, uint value)
        {
            unchecked
            {
                hash ^= value & 0xFFu;
                hash *= HashPrime;
                hash ^= (value >> 8) & 0xFFu;
                hash *= HashPrime;
                hash ^= (value >> 16) & 0xFFu;
                hash *= HashPrime;
                hash ^= (value >> 24) & 0xFFu;
                return hash * HashPrime;
            }
        }

        private static ulong HashAdd(ulong hash, ulong value)
        {
            hash = HashAdd(hash, unchecked((uint)value));
            return HashAdd(hash, unchecked((uint)(value >> 32)));
        }

        private static int CountPointShadowFaces(IReadOnlyList<int> faceMasks, int pointShadowCount)
        {
            int faceCount = 0;
            int count = Math.Min(pointShadowCount, faceMasks.Count);
            for (int i = 0; i < count; i++)
            {
                int mask = faceMasks[i] & 0x3F;
                for (int bit = 0; bit < 6; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                        faceCount++;
                }
            }

            return faceCount;
        }

        private static void UpdateTiledLightDiagnostics(SceneRenderingData sceneData, LightFrameSnapshot lightSnapshot)
        {
            sceneData.MaxLightsInAnyTile = 0;
            sceneData.AverageLightsPerNonEmptyTile = 0.0f;
            sceneData.LightTileSaturationCount = 0;
            sceneData.LightCullRejectedPointCount = 0;
            sceneData.LightCullRejectedSpotCount = 0;
            sceneData.LightCullRejectedAreaCount = 0;

            if (sceneData.LocalLightCount <= 0 ||
                sceneData.TileCountX == 0 ||
                sceneData.TileCountY == 0 ||
                sceneData.MaxLightsPerTile <= 0)
            {
                return;
            }

            int tileCount = checked((int)(sceneData.TileCountX * sceneData.TileCountY));
            int[] tileLightCounts = ArrayPool<int>.Shared.Rent(tileCount);
            Array.Clear(tileLightCounts, 0, tileCount);

            try
            {
                ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
                for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
                {
                    Light light = lights[lightIndex];
                    if (light.Type == LightType.Directional)
                        continue;

                    if (!TryProjectLocalLightTileBounds(
                            light,
                            sceneData,
                            out int minTileX,
                            out int minTileY,
                            out int maxTileX,
                            out int maxTileY))
                    {
                        IncrementRejectedLocalLight(sceneData, light.Type);
                        continue;
                    }

                    for (int y = minTileY; y <= maxTileY; y++)
                    {
                        int rowOffset = checked(y * (int)sceneData.TileCountX);
                        for (int x = minTileX; x <= maxTileX; x++)
                            tileLightCounts[rowOffset + x]++;
                    }
                }

                long totalLightsInNonEmptyTiles = 0;
                int nonEmptyTileCount = 0;
                int maxLightsInAnyTile = 0;
                int saturatedTileCount = 0;
                for (int i = 0; i < tileCount; i++)
                {
                    int count = tileLightCounts[i];
                    if (count <= 0)
                        continue;

                    nonEmptyTileCount++;
                    totalLightsInNonEmptyTiles += count;
                    maxLightsInAnyTile = Math.Max(maxLightsInAnyTile, count);
                    if (count >= sceneData.MaxLightsPerTile)
                        saturatedTileCount++;
                }

                sceneData.MaxLightsInAnyTile = maxLightsInAnyTile;
                sceneData.LightTileSaturationCount = saturatedTileCount;
                sceneData.AverageLightsPerNonEmptyTile = nonEmptyTileCount == 0
                    ? 0.0f
                    : (float)totalLightsInNonEmptyTiles / nonEmptyTileCount;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(tileLightCounts);
            }
        }

        private static bool TryProjectLocalLightTileBounds(
            Light light,
            SceneRenderingData sceneData,
            out int minTileX,
            out int minTileY,
            out int maxTileX,
            out int maxTileY)
        {
            minTileX = 0;
            minTileY = 0;
            maxTileX = checked((int)sceneData.TileCountX - 1);
            maxTileY = checked((int)sceneData.TileCountY - 1);

            if (light.Range <= 0.0f || light.Intensity <= 0.0f)
                return false;

            Vector4 clip = TransformHomogeneous(light.Position, sceneData.ViewProjectionMatrix);
            float radius = MathF.Max(
                AnalyticalLightGeometry.GetBoundingRadius(light),
                0.001f);
            if (!IsFinite(clip.X) || !IsFinite(clip.Y) || !IsFinite(clip.W))
                return false;

            if (clip.W <= radius || clip.W <= 0.0001f)
                return true;

            float invW = 1.0f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            float radiusNdcX = MathF.Abs(sceneData.ProjectionMatrix.M11) * radius * invW;
            float radiusNdcY = MathF.Abs(sceneData.ProjectionMatrix.M22) * radius * invW;

            if (ndcX + radiusNdcX < -1.0f ||
                ndcX - radiusNdcX > 1.0f ||
                ndcY + radiusNdcY < -1.0f ||
                ndcY - radiusNdcY > 1.0f)
            {
                return false;
            }

            float screenWidth = Math.Max(sceneData.ScreenWidth, 1u);
            float screenHeight = Math.Max(sceneData.ScreenHeight, 1u);
            float minPixelX = ((ndcX - radiusNdcX) * 0.5f + 0.5f) * screenWidth;
            float maxPixelX = ((ndcX + radiusNdcX) * 0.5f + 0.5f) * screenWidth;
            float minPixelY = ((ndcY - radiusNdcY) * 0.5f + 0.5f) * screenHeight;
            float maxPixelY = ((ndcY + radiusNdcY) * 0.5f + 0.5f) * screenHeight;

            minTileX = ClampTileIndex(MathF.Floor(minPixelX / 16.0f), sceneData.TileCountX);
            maxTileX = ClampTileIndex(MathF.Floor(maxPixelX / 16.0f), sceneData.TileCountX);
            minTileY = ClampTileIndex(MathF.Floor(minPixelY / 16.0f), sceneData.TileCountY);
            maxTileY = ClampTileIndex(MathF.Floor(maxPixelY / 16.0f), sceneData.TileCountY);

            return minTileX <= maxTileX && minTileY <= maxTileY;
        }

        private static int ClampTileIndex(float value, uint tileCount)
        {
            if (tileCount == 0)
                return 0;

            int index = (int)value;
            return Math.Clamp(index, 0, checked((int)tileCount - 1));
        }

        private static Vector4 TransformHomogeneous(System.Numerics.Vector3 position, Matrix4x4 matrix)
        {
            return new Vector4(
                position.X * matrix.M11 + position.Y * matrix.M21 + position.Z * matrix.M31 + matrix.M41,
                position.X * matrix.M12 + position.Y * matrix.M22 + position.Z * matrix.M32 + matrix.M42,
                position.X * matrix.M13 + position.Y * matrix.M23 + position.Z * matrix.M33 + matrix.M43,
                position.X * matrix.M14 + position.Y * matrix.M24 + position.Z * matrix.M34 + matrix.M44);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void IncrementRejectedLocalLight(SceneRenderingData sceneData, LightType lightType)
        {
            if (lightType == LightType.Point)
                sceneData.LightCullRejectedPointCount++;
            else if (lightType == LightType.Spot)
                sceneData.LightCullRejectedSpotCount++;
            else if (AnalyticalLightGeometry.IsArea(lightType))
                sceneData.LightCullRejectedAreaCount++;
        }

        private void UpdateGlobalIlluminationCpuTiming(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            bool active = gi.Enabled &&
                          gi.EffectiveUseDdgi;
            if (!active)
            {
                _globalIlluminationCpuTimingWindow.Clear();
                sceneData.CpuGlobalIlluminationRecordMicroseconds = 0;
                sceneData.CpuGlobalIlluminationRecordP95Microseconds = 0;
                sceneData.GlobalIlluminationCpuTimingSampleCount = 0;
                return;
            }

            // Every term is owned by a distinct recorder. The Simple-DDGI
            // manager includes its scheduler in its upload time, while the AS
            // manager reports one total preparation interval.
            long totalMicroseconds = Math.Max(0, sceneData.CpuSimpleDdgiRecordMicroseconds) +
                                     Math.Max(0, sceneData.CpuFarFieldRecordMicroseconds) +
                                     Math.Max(0, sceneData.CpuAccelerationStructureBuildMicroseconds);
            _globalIlluminationCpuTimingWindow.Add(totalMicroseconds);
            PerformanceSampleStats stats = _globalIlluminationCpuTimingWindow.GetStats();
            sceneData.CpuGlobalIlluminationRecordMicroseconds = totalMicroseconds;
            sceneData.CpuGlobalIlluminationRecordP95Microseconds = Math.Max(0, (long)Math.Round(stats.P95));
            sceneData.GlobalIlluminationCpuTimingSampleCount = stats.Count;
        }

        private SimpleDdgiNearFieldResidualDiagnostics
            ResolveNearFieldResidualDiagnostics()
        {
            return _nearFieldResidual.Diagnostics;
        }

        private RendererDiagnosticsAssemblyInput CaptureDiagnosticsInput(
            SceneRenderingData sceneData,
            AsyncComputeDiagnosticsSnapshot asyncComputeSnapshot)
        {
            GlobalIlluminationSettings giSettings =
                Settings.GlobalIllumination;
            bool giRayQuerySupported =
                _context.RayQuerySupported &&
                _accelerationStructureManager?.Supported == true;
            bool giAccelerationStructuresActive =
                _accelerationStructureManager?.Active == true;
            GlobalIlluminationMode effectiveGiMode =
                ResolveEffectiveGlobalIlluminationMode(
                    giSettings,
                    giRayQuerySupported);
            bool giEnabled =
                !giSettings.EmergencyGiFallbackEnabled &&
                giSettings.Enabled &&
                effectiveGiMode !=
                GlobalIlluminationMode.Disabled;
            bool giRayQueryActive =
                giEnabled &&
                giSettings.EffectiveUseRayQueryBackend &&
                giRayQuerySupported &&
                giAccelerationStructuresActive;
            bool giUsesSimpleDdgi = giSettings.EffectiveUseDdgi;

            DdgiContentRuntimeSnapshot contentRuntime =
                CreateDdgiContentRuntimeSnapshot(
                    sceneData,
                    giUsesSimpleDdgi,
                    giRayQueryActive);
            DdgiRuntimeSnapshot runtime = giUsesSimpleDdgi
                ? CreateDdgiRuntimeSnapshot(sceneData)
                : DdgiRuntimeSnapshot.Empty;

            RenderBudgetProfile budgetProfile =
                Settings.PerformanceBudgets.Profile;
            return new RendererDiagnosticsAssemblyInput(
                sceneData,
                Settings,
                new RendererDiagnosticsResourceInput(
                    _context,
                    _swapchain,
                    _sync,
                    _textureManager,
                    _meshManager,
                    _materialManager,
                    _lightManager,
                    _renderGraph,
                    _stagingRing,
                    _modelUploadService,
                    _renderTargets,
                    _directionalShadowResources,
                    _spotShadowAtlas,
                    _pointShadowCubemapArray,
                    _environmentManager,
                    _iesPhotometricProfileManager,
                    _reflectionProbeManager,
                    _forwardPlusPass,
                    _meshPipeline,
                    _meshletPhysicalResidencyResources,
                    _dynamicResolutionScaleController),
                new RendererDiagnosticsExecutionInput(
                    asyncComputeSnapshot,
                    BuildUploadBudgetSnapshot(
                        sceneData,
                        budgetProfile),
                    BuildMemoryBudgetSnapshot(budgetProfile),
                    _stallTracker.CreateSnapshot(),
                    BuildGpuTimingReason()),
                new RendererDiagnosticsGiInput(
                    _simpleDdgiVolumeManager,
                    _farFieldClipmapManager,
                    _accelerationStructureManager,
                    _giPipelineCacheService,
                    _completedFarFieldMaterialV2Counters,
                    _completedMaterialGiCounters,
                    _completedThinSurfaceTransportCounters,
                    _completedDdgiGeometryParticipationCounters,
                    _completedDdgiAreaLightCounters,
                    contentRuntime,
                    runtime,
                    ResolveNearFieldResidualDiagnostics(),
                    effectiveGiMode),
                new RendererDiagnosticsToolingInput(
                    _debugOverlayBuilder.DrawList.Enabled,
                    _screenshotCaptureService,
                    _renderDocCaptureService,
                    _gpuTimestamps),
                new RendererDiagnosticsCaptureInput(
                    _performanceCaptureMetadataProvider),
                new RendererDiagnosticsFrameInput(
                    _lastAcquireImageMicroseconds,
                    _lastSwapchainImageOwnerWaitMicroseconds,
                    _lastFrameResourceRecycleWaitMicroseconds,
                    _lastQueueSubmitMicroseconds,
                    _lastPresentMicroseconds,
                    _hostMaximumFramesPerSecond,
                    _hostFramePacingWaitMicroseconds,
                    _currentFrame,
                    _lastRecycledFrameResourceOwnerSubmissionSerial,
                    _imageIndex,
                    _lastAcquiredImageOwnerSubmissionSerial,
                    _lastAcquiredImageOwnerFrameContext,
                    _currentAcquireSemaphoreIndex,
                    _ddgiFrameSerial == ulong.MaxValue
                        ? ulong.MaxValue
                        : _ddgiFrameSerial + 1UL,
                    _lastRenderTargetRecreateReason));
        }

        private void RefreshValidationDiagnostics()
        {
            _lastDiagnostics =
                _diagnosticsAssembler.ApplyValidationMessages(
                    _lastDiagnostics,
                    _context.ValidationMessageSnapshot);
        }

        private void SetViewportAndScissor(CommandBuffer commandBuffer)
        {
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _swapchain.Extent.Width,
                Height = _swapchain.Extent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = _swapchain.Extent
            };

            _context.Api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
            _context.Api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        }

        private void RecordRenderGraphFromAsyncPlan(
            Njulf.Rendering.Pipeline.AsyncComputeFramePlan framePlan,
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(framePlan);
            AsyncComputeSubmissionPlan plan = framePlan.SubmissionPlan;
            if (!plan.Accepted || !plan.ContainsAsyncCompute)
            {
                throw new InvalidOperationException(
                    "Only an accepted plan with compute segments can be recorded asynchronously.");
            }

            int emittedAcquireBarrierCount = 0;
            int emittedReleaseBarrierCount = 0;
            int ownershipTransferCount = 0;
            long barrierRecordMicroseconds = 0;
            try
            {
                // This is an actual-execution flag. Publish it only after the
                // coordinator has authorized the complete immutable plan.
                sceneData.DdgiAsyncComputeEnabled =
                    framePlan.IsPathActive(AsyncComputePath.SimpleDdgiUpdate)
                        ? 1
                        : 0;
                _renderGraph.BeginSplitExecution(sceneData);

                foreach (AsyncComputeSubmissionSegment segment in plan.Segments)
                {
                    CommandBuffer commandBuffer;
                    if (segment.Id == 0)
                    {
                        commandBuffer = _currentCommandBuffer;
                    }
                    else if (segment.Queue == AsyncComputeQueue.Compute)
                    {
                        commandBuffer =
                            _cmd.BeginAsyncComputeCommand(_currentFrame);
                        _gpuTimestamps.BeginComputeQueueFrame(
                            commandBuffer,
                            _currentFrame);
                    }
                    else
                    {
                        commandBuffer =
                            _cmd.BeginScheduledGraphicsCommand(_currentFrame);
                        SetViewportAndScissor(commandBuffer);
                    }

                    if (segment.Queue == AsyncComputeQueue.Compute &&
                        segment.Passes.Count > 0 &&
                        AsyncComputePassCatalog.TryGetPath(
                            segment.Passes[0],
                            out AsyncComputePath path))
                    {
                        _asyncComputeCoordinator.RegisterComputeSegment(
                            segment.Id,
                            path,
                            (ulong)commandBuffer.Handle);
                    }

                    long barrierRecordStart = Stopwatch.GetTimestamp();
                    QueueOwnershipTransferBarrierCounts acquireCounts =
                        QueueOwnershipTransferRecorder.RecordAcquires(
                            _context,
                            commandBuffer,
                            segment.AcquireTransfers);
                    barrierRecordMicroseconds +=
                        ElapsedMicroseconds(barrierRecordStart);
                    emittedAcquireBarrierCount +=
                        acquireCounts.AcquireCount;

                    if (segment.AccessesSwapchain)
                        EnsureSwapchainImageColorAttachment(commandBuffer);

                    if (segment.Passes.Count > 0)
                    {
                        _asyncComputePassNameScratch.Clear();
                        for (int passIndex = 0;
                             passIndex < segment.Passes.Count;
                             passIndex++)
                        {
                            _asyncComputePassNameScratch.Add(
                                segment.Passes[passIndex]);
                        }

                        _renderGraph.ExecuteSelected(
                            commandBuffer,
                            _currentFrame,
                            sceneData,
                            IncludeAsyncComputePass,
                            _gpuTimestamps,
                            _cmd,
                            useSecondaryCommandBuffers:
                            segment.Queue == AsyncComputeQueue.Graphics &&
                            Settings.UseSecondaryCommandBuffers,
                            isComputeQueue:
                            segment.Queue == AsyncComputeQueue.Compute,
                            usesExplicitQueueTransfers: true);
                    }

                    barrierRecordStart = Stopwatch.GetTimestamp();
                    QueueOwnershipTransferBarrierCounts releaseCounts =
                        QueueOwnershipTransferRecorder.RecordReleases(
                            _context,
                            commandBuffer,
                            segment.ReleaseTransfers);
                    barrierRecordMicroseconds +=
                        ElapsedMicroseconds(barrierRecordStart);
                    emittedReleaseBarrierCount +=
                        releaseCounts.ReleaseCount;
                    ownershipTransferCount +=
                        releaseCounts.OwnershipTransferCount;

                    if (segment.IsTerminalGraphicsSegment)
                    {
                        _currentCommandBuffer = commandBuffer;
                        continue;
                    }

                    _cmd.EndCommandBuffer(commandBuffer);
                    _deferredAsyncSubmissions.Add(
                        new DeferredAsyncSubmission(commandBuffer, segment));
                }

                AsyncComputeRecordingPublication publication =
                    _asyncComputeCoordinator.CommitRecording(
                        framePlan,
                        new AsyncComputeRecordingSummary(
                            emittedReleaseBarrierCount,
                            emittedAcquireBarrierCount,
                            ownershipTransferCount,
                            barrierRecordMicroseconds));
                if (!publication.Succeeded)
                {
                    throw new InvalidOperationException(
                        publication.FailureReason);
                }

                sceneData.GraphQueueOwnershipTransitionCount =
                    plan.QueueFamilyOwnershipTransferCount;
                sceneData.AsyncComputeOwnershipTransferCount =
                    publication.OwnershipTransferCount;
                sceneData.GraphBarrierSummary =
                    publication.GraphBarrierSummary;
                _renderGraph.CompleteSplitExecution(sceneData);
            }
            catch (Exception exception)
            {
                // Some graph work is already appended after the renderer's
                // prelude. Never replay the graphics graph into this buffer.
                MarkFrameSubmissionFault(
                    $"Failed to record the validated async plan before submission: {exception.Message}",
                    Result.ErrorUnknown);
                throw;
            }
        }

        private unsafe void SubmitRecordedAsyncSegments()
        {
            int maxWaitCount = 0;
            foreach (DeferredAsyncSubmission deferred in _deferredAsyncSubmissions)
                maxWaitCount = Math.Max(maxWaitCount, deferred.Segment.TimelineWaits.Count);

            // Keep the temporary submission arrays outside the loop. Apart from avoiding managed
            // allocation in the hot path, this prevents unbounded stack growth when a frame has
            // many short segments.
            Semaphore* waitSemaphores = stackalloc Semaphore[Math.Max(1, maxWaitCount)];
            PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[Math.Max(1, maxWaitCount)];
            ulong* waitValues = stackalloc ulong[Math.Max(1, maxWaitCount)];
            Semaphore* signalSemaphores = stackalloc Semaphore[1];
            ulong* signalValues = stackalloc ulong[1];

            foreach (DeferredAsyncSubmission deferred in _deferredAsyncSubmissions)
            {
                AsyncComputeSubmissionSegment segment = deferred.Segment;
                int waitCount = segment.TimelineWaits.Count;
                int signalCount = segment.TimelineSignalValue.HasValue ? 1 : 0;
                for (int i = 0; i < waitCount; i++)
                {
                    AsyncComputeTimelineWait wait = segment.TimelineWaits[i];
                    waitSemaphores[i] = _cmd.AsyncComputeTimelineSemaphore;
                    waitStages[i] = ToLegacyPipelineStage(wait.StageMask);
                    waitValues[i] =
                        _asyncComputeCoordinator.ResolveTimelineValue(
                            wait.Value);
                }

                if (signalCount > 0)
                {
                    signalSemaphores[0] = _cmd.AsyncComputeTimelineSemaphore;
                    signalValues[0] =
                        _asyncComputeCoordinator.ResolveTimelineValue(
                            segment.TimelineSignalValue!.Value);
                }

                TimelineSemaphoreSubmitInfo timelineInfo = default;
                if (waitCount > 0 || signalCount > 0)
                {
                    timelineInfo = new TimelineSemaphoreSubmitInfo
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        WaitSemaphoreValueCount = (uint)waitCount,
                        PWaitSemaphoreValues = waitValues,
                        SignalSemaphoreValueCount = (uint)signalCount,
                        PSignalSemaphoreValues = signalValues
                    };
                }

                CommandBuffer commandBuffer = deferred.CommandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    PNext = waitCount > 0 || signalCount > 0 ? &timelineInfo : null,
                    WaitSemaphoreCount = (uint)waitCount,
                    PWaitSemaphores = waitSemaphores,
                    PWaitDstStageMask = waitStages,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                    SignalSemaphoreCount = (uint)signalCount,
                    PSignalSemaphores = signalSemaphores
                };

                long submitStart = Stopwatch.GetTimestamp();
                Result result = _context.Api.QueueSubmit(
                    segment.Queue == AsyncComputeQueue.Compute ? _context.ComputeQueue : _context.GraphicsQueue,
                    1,
                    &submitInfo,
                    default);
                long elapsed = ElapsedMicroseconds(submitStart);
                _lastQueueSubmitMicroseconds += elapsed;
                if (result != Result.Success)
                {
                    MarkFrameSubmissionFault(
                        $"Failed to submit scheduled {segment.Queue} segment {segment.Id}: {result}. " +
                        "The frame is abandoned before its terminal graphics submission so command-pool reuse cannot race submitted work.",
                        result);
                    throw new VulkanException($"Failed to submit scheduled {segment.Queue} command buffer", result);
                }

                _asyncComputeCoordinator
                    .RecordSubmittedNonTerminalSegment(
                        segment.Queue,
                        elapsed);
            }

            _deferredAsyncSubmissions.Clear();
        }

        private void EnsureSwapchainImageColorAttachment(CommandBuffer commandBuffer)
        {
            if (_swapchainImageTransitionedThisFrame)
                return;

            TransitionSwapchainImage(commandBuffer, ImageLayout.ColorAttachmentOptimal);
            _swapchainImageTransitionedThisFrame = true;
        }

        /// <summary>
        /// Records one optional final-LDR screenshot after every terminal render
        /// graph pass and before the image is returned to presentation. The
        /// request is dequeued only here, so a frame that never wrote a terminal
        /// swapchain image leaves queued capture requests intact.
        /// </summary>
        private void RecordTerminalScreenshotCapture()
        {
            if (_screenshotCaptureService.PendingCount == 0)
                return;

            if (!_screenshotReadbackManager.TryPrepareCapture(
                    _currentFrame,
                    _swapchain.Extent,
                    _swapchain.SurfaceFormat,
                    _swapchain.SupportsTransferSource,
                    _swapchain.ScreenshotCaptureSupportReason,
                    out ScreenshotReadbackCapturePlan plan))
            {
                return;
            }

            bool imageTransitionedToTransferSource = false;
            try
            {
                TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.TransferSrcOptimal);
                imageTransitionedToTransferSource = true;
                _screenshotReadbackManager.RecordCopy(
                    _currentCommandBuffer,
                    _swapchain.Images[_imageIndex],
                    plan,
                    _context.Api);
            }
            catch (Exception exception)
            {
                _screenshotReadbackManager.FailFrame(
                    _currentFrame,
                    $"Could not record renderer screenshot image-to-buffer copy: {exception.GetType().Name}: {exception.Message}");

                // Preserve the acquired-image presentation contract even if a
                // local readback recording error occurs after the layout change.
                if (imageTransitionedToTransferSource &&
                    _swapchain.GetImageLayout(_imageIndex) == ImageLayout.TransferSrcOptimal)
                {
                    TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.PresentSrcKhr);
                }
            }
        }

        /// <summary>
        /// Records at most one native RGBA16F SceneColor copy after the render
        /// graph has produced and diagnostically visualized the linear frame.
        /// The readback buffer is not inspected until this slot's terminal
        /// graphics fence completes.
        /// </summary>
        private void RecordLinearHdrSceneColorCapture()
        {
            if (_linearHdrCaptureService.PendingCount == 0 || _renderTargets == null)
                return;

            RenderTarget sceneColor = _renderTargets.SceneColor;
            if (!_linearHdrReadbackManager.TryPrepareCapture(
                    _currentFrame,
                    _ddgiFrameSerial,
                    sceneColor,
                    out LinearHdrReadbackCapturePlan plan))
            {
                return;
            }

            try
            {
                sceneColor.TransitionToTransferSource(_currentCommandBuffer);
                _linearHdrReadbackManager.RecordCopy(
                    _currentCommandBuffer,
                    sceneColor.Image,
                    plan,
                    _context.Api);
            }
            catch (Exception exception)
            {
                _linearHdrReadbackManager.FailFrame(
                    _currentFrame,
                    $"Could not record linear HDR SceneColor copy: {exception.GetType().Name}: {exception.Message}");
            }
        }

        /// <summary>
        /// A failed queue submit is not recoverable as a normal frame: it may have left an
        /// acquired image unpresented or a non-terminal command buffer in flight.  Stop future
        /// frame acquisition before a reset fence or command pool can be reused. Device loss is
        /// recorded separately because no Vulkan recovery submission is legal in that state.
        /// </summary>
        private void MarkFrameSubmissionFault(string reason, Result result)
        {
            RendererSubmissionFault fault =
                _lifetime.LatchSubmissionFault(
                    reason,
                    result == Result.ErrorDeviceLost);
            // A terminal fence will never be safely reused after this latch, so
            // any GPU readback or queued request must fail explicitly rather
            // than remain pending forever.
            _screenshotReadbackManager.FailAll(
                $"Renderer screenshot capture was cancelled because frame submission failed: {fault.Reason}",
                includeQueuedRequests: true);
            _linearHdrReadbackManager.FailAll(
                $"Linear HDR capture was cancelled because frame submission failed: {fault.Reason}",
                includeQueuedRequests: true);
            _simpleDdgiReceiverFeedback?.AbortCapture(
                "receiver-feedback-frame-submission-failed:" +
                fault.Reason);
            _asyncComputeCoordinator.LatchEmergencyFallback(fault.Reason);
            _asyncComputeCoordinator.AbortFrame(_currentFrame);
            _deferredAsyncSubmissions.Clear();
            _lifetime.AbandonFrame();
        }

        /// <summary>
        /// The terminal fence was reset immediately before the submit that failed. For failures
        /// other than device loss, submit an empty wait operation with that fence so all already
        /// submitted graphics/compute work is covered and the fence cannot remain permanently
        /// unsignalled. We still stop rendering afterwards: this only makes cleanup safe; it does
        /// not pretend the acquired swapchain image was rendered or presented.
        /// </summary>
        private unsafe Result TryRecoverFrameFenceAfterTerminalSubmitFailure(
            int waitCount,
            Semaphore* waitSemaphores,
            PipelineStageFlags* waitStages,
            ulong* waitValues,
            bool hasTimelineWaits)
        {
            if (_lifetime.DeviceLost || waitCount <= 0)
                return Result.ErrorDeviceLost;

            TimelineSemaphoreSubmitInfo timelineInfo = default;
            if (hasTimelineWaits)
            {
                timelineInfo = new TimelineSemaphoreSubmitInfo
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    WaitSemaphoreValueCount = (uint)waitCount,
                    PWaitSemaphoreValues = waitValues
                };
            }

            var recoverySubmit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = hasTimelineWaits ? &timelineInfo : null,
                WaitSemaphoreCount = (uint)waitCount,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 0,
                PCommandBuffers = null,
                SignalSemaphoreCount = 0,
                PSignalSemaphores = null
            };

            long submitStart = Stopwatch.GetTimestamp();
            Result recovery = _context.Api.QueueSubmit(
                _context.GraphicsQueue,
                1,
                &recoverySubmit,
                _sync.GetInFlightFence(_currentFrame));
            _lastQueueSubmitMicroseconds += ElapsedMicroseconds(submitStart);
            return recovery;
        }

        private static PipelineStageFlags ToLegacyPipelineStage(PipelineStageFlags2 stages)
        {
            PipelineStageFlags result = PipelineStageFlags.None;
            if ((stages & PipelineStageFlags2.ComputeShaderBit) != 0)
                result |= PipelineStageFlags.ComputeShaderBit;
            if ((stages & PipelineStageFlags2.FragmentShaderBit) != 0)
                result |= PipelineStageFlags.FragmentShaderBit;
            if ((stages & PipelineStageFlags2.VertexShaderBit) != 0)
                result |= PipelineStageFlags.VertexShaderBit;
            if ((stages & PipelineStageFlags2.ColorAttachmentOutputBit) != 0)
                result |= PipelineStageFlags.ColorAttachmentOutputBit;
            if ((stages & PipelineStageFlags2.EarlyFragmentTestsBit) != 0)
                result |= PipelineStageFlags.EarlyFragmentTestsBit;
            if ((stages & PipelineStageFlags2.LateFragmentTestsBit) != 0)
                result |= PipelineStageFlags.LateFragmentTestsBit;
            if ((stages & PipelineStageFlags2.TransferBit) != 0)
                result |= PipelineStageFlags.TransferBit;
            if ((stages & PipelineStageFlags2.DrawIndirectBit) != 0)
                result |= PipelineStageFlags.DrawIndirectBit;
            if ((stages & PipelineStageFlags2.AllCommandsBit) != 0)
                result |= PipelineStageFlags.AllCommandsBit;
            return result == PipelineStageFlags.None ? PipelineStageFlags.AllCommandsBit : result;
        }

        private sealed record DeferredAsyncSubmission(
            CommandBuffer CommandBuffer,
            AsyncComputeSubmissionSegment Segment);

        /// <summary>
        /// Captures allocation-bearing manager generations and typed handle sets without walking
        /// render targets, materials, textures, or acceleration-structure collections.
        /// </summary>
        private AsyncComputeResourcePlanGeneration CaptureAsyncComputeResourcePlanGeneration()
        {
            DdgiEmissiveBufferSnapshot emissiveBuffers =
                _ddgiEmissiveTransport?.Snapshot.Buffers ?? default;
            var coreBuffers = new AsyncCoreBufferIdentity(
                _meshManager.VertexPositionBuffer,
                _meshManager.VertexNormalTangentBuffer,
                _meshManager.VertexUvColorBuffer,
                _meshManager.IndexBuffer,
                _meshManager.MeshMetadataBuffer,
                _materialManager.MaterialBuffer,
                _materialManager.MaterialExtensionBuffer,
                _lightManager.LightBuffer,
                _environmentManager?.EnvironmentBuffer ?? BufferHandle.Invalid,
                _environmentManager?.PrefilterEnvironmentBuffer ?? BufferHandle.Invalid,
                _environmentManager?.GiEnvironmentBuffer ?? BufferHandle.Invalid,
                emissiveBuffers.SourceBuffer,
                emissiveBuffers.SurfaceBuffer);

            FarFieldClipmapManager? farField = _farFieldClipmapManager;
            var farFieldBuffers = farField == null
                ? default
                : new FarFieldAsyncBufferIdentity(
                    farField.ParamsBuffer,
                    farField.VoxelBuffer,
                    farField.BakeVoxelBuffer,
                    farField.InstanceBuffer,
                    farField.DistanceBuffer,
                    farField.JumpFloodScratch0Buffer,
                    farField.JumpFloodScratch1Buffer,
                    farField.PageTableBuffer);

            SimpleDdgiVolumeManager? simpleDdgi = _simpleDdgiVolumeManager;
            SimpleDdgiLightTreeGraphResourceSnapshot simpleDdgiLightTree =
                _simpleDdgiLightTreeResources?.CaptureGraphResources() ??
                default;
            var simpleDdgiBuffers = simpleDdgi == null
                ? default
                : new SimpleDdgiAsyncBufferIdentity(
                    simpleDdgi.ParamsBuffer,
                    simpleDdgi.IrradianceAtlasBuffer,
                    simpleDdgi.TransportIrradianceAtlasBuffer,
                    simpleDdgi.TransportSourceCacheBuffer,
                    simpleDdgi.VisibilityAtlasBuffer,
                    simpleDdgi.RayResultScratchBuffer,
                    simpleDdgi.ProbeStateBuffer,
                    simpleDdgi.ReceiverProbeBuffer,
                    simpleDdgi.DirectionalRadianceBuffer,
                    simpleDdgi.DirectionalRadianceParityBuffer,
                    simpleDdgi.ProbeUpdateQueueBuffer,
                    simpleDdgi.RelocationClassificationBuffer,
                    simpleDdgi.GpuSchedulerArenaBuffer,
                    simpleDdgi.ProbeResidencyGraphBuffer,
                    simpleDdgiLightTree.Node,
                    simpleDdgiLightTree.Leaf,
                    simpleDdgiLightTree.State,
                    simpleDdgiLightTree.Scratch,
                    simpleDdgi.SampledAtlasAllocationGeneration);
            NearFieldResidualGraphResourceSnapshot nearFieldResources =
                _nearFieldResidual.CaptureGraphResources();
            SimpleDdgiNearFieldResidualVulkanBuffers nearFieldBuffers =
                nearFieldResources.Buffers;
            GiCausticGraphResourceSnapshot causticResources =
                _giCaustic.CaptureGraphResources();
            GiCausticVulkanBuffers causticBuffers =
                causticResources.Buffers;
            SimpleDdgiGuidingGraphResourceSnapshot guidingResources =
                _simpleDdgiGuidingFrameCoordinator is { } guidingCoordinator &&
                guidingCoordinator.TryGetGraphResourceSnapshot(
                    out SimpleDdgiGuidingGraphResourceSnapshot capturedGuidingResources)
                    ? capturedGuidingResources
                    : default;

            return new AsyncComputeResourcePlanGeneration(
                _renderGraph.ResourceAllocationGeneration,
                _renderTargets?.ResizeCount ?? 0,
                _swapchain.ResourceGeneration,
                _materialManager.ReferencedTextureSetGeneration,
                _textureManager.ResourceGeneration,
                _environmentManager?.PrefilterResourceGeneration ?? 0,
                _particleSystemManager.ResourceGeneration,
                _gpuParticleRuntimeManager.ResourceGeneration,
                _accelerationStructureManager?.ResourceGeneration ?? 0,
                _context.GraphicsQueueFamilyIndex,
                _context.ComputeQueueFamilyIndex,
                _hizDepthPyramid?.Image.Handle ?? 0,
                coreBuffers,
                farFieldBuffers,
                simpleDdgiBuffers,
                new DirectionalRayShadowAsyncBufferIdentity(
                    _directionalRayShadowPass?.GetMaskBuffer(0) ??
                    BufferHandle.Invalid,
                    _directionalRayShadowPass?.GetMaskBuffer(1) ??
                    BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetRaw(0) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetRaw(1) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetHistory(0) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetHistory(1) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetScratch(0) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetScratch(1) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetDiagnostic(0) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetDiagnostic(1) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetCounters(0) ?? BufferHandle.Invalid,
                    _directionalShadowHistoryResources?.GetCounters(1) ?? BufferHandle.Invalid,
                    _areaRayShadowPass?.GetMaskBuffer(0) ?? BufferHandle.Invalid,
                    _areaRayShadowPass?.GetMaskBuffer(1) ?? BufferHandle.Invalid,
                    _directionalRayShadowPass?.ResourceGeneration ?? 0u,
                    _directionalShadowHistoryResources?.ResourceGeneration ?? 0u,
                    _areaRayShadowPass?.ResourceGeneration ?? 0u),
                new CausticAsyncBufferIdentity(
                    causticBuffers.Tasks,
                    causticBuffers.Photons,
                    causticBuffers.Cache,
                    causticBuffers.Scratch,
                    causticResources.FrameConstants0,
                    causticResources.FrameConstants1),
                new GuidingAsyncBufferIdentity(
                    guidingResources.Distributions.DistributionBank0,
                    guidingResources.Distributions.DistributionBank1,
                    guidingResources.Distributions.BankBytes,
                    guidingResources.Distributions.AllocationGeneration,
                    guidingResources.WorkspaceBuffer,
                    guidingResources.WorkspaceOffsetBytes,
                    guidingResources.WorkspaceBytes,
                    guidingResources.WorkspaceGeneration,
                    guidingResources.DirectionPayloadSidecar,
                    guidingResources.DirectionPayloadBytes,
                    guidingResources.DirectionPayloadGeneration),
                new NearFieldResidualAsyncBufferIdentity(
                    nearFieldBuffers.HistoryMetadata0,
                    nearFieldBuffers.HistoryMetadata1,
                    nearFieldBuffers.SurfaceTable,
                    nearFieldBuffers.ActiveTileAndIndirect,
                    nearFieldBuffers.TileRecords,
                    nearFieldBuffers.TraceFrameConstants0,
                    nearFieldBuffers.TraceFrameConstants1),
                new HybridReflectionAsyncBufferIdentity(
                    _hybridReflectionRuntime?.GetTaskBuffer(0) ??
                    BufferHandle.Invalid,
                    _hybridReflectionRuntime?.GetTaskBuffer(1) ??
                    BufferHandle.Invalid,
                    _hybridReflectionRuntime?.GetCounterBuffer(0) ??
                    BufferHandle.Invalid,
                    _hybridReflectionRuntime?.GetCounterBuffer(1) ??
                    BufferHandle.Invalid,
                    _hybridReflectionRuntime?.GetIndirectBuffer(0) ??
                    BufferHandle.Invalid,
                    _hybridReflectionRuntime?.GetIndirectBuffer(1) ??
                    BufferHandle.Invalid));
        }

        /// <summary>
        /// Resolves every frame/history slot to exact Vulkan allocations only when its generation
        /// key changes. A bindless index or broad BufferSet is never an ownership target.
        /// </summary>
        private void EnsureAsyncComputeResourceBindings()
        {
            AsyncComputeResourcePlanGeneration generation = CaptureAsyncComputeResourcePlanGeneration();
            if (_asyncComputeResourcePlanGeneration == generation && _asyncComputeResourcePlan != null)
            {
                if (!ReferenceEquals(_renderGraph.ConcreteResourceBindings.CurrentPlan, _asyncComputeResourcePlan))
                    _renderGraph.ActivateConcreteResourcePlan(_asyncComputeResourcePlan, resetState: true);
                return;
            }

            var bindings = new List<RenderGraphConcreteResourceBinding>();
            DdgiEmissiveBufferSnapshot emissiveBuffers =
                _ddgiEmissiveTransport?.Snapshot.Buffers ?? default;
            uint graphicsFamily = _context.GraphicsQueueFamilyIndex;
            uint computeFamily = _context.ComputeQueueFamilyIndex;
            IReadOnlyList<uint> queueFamilies = graphicsFamily == computeFamily
                ? new[] { graphicsFamily }
                : new[] { graphicsFamily, computeFamily };

            foreach (RenderGraphResourceId resource in Enum.GetValues<RenderGraphResourceId>())
            {
                IReadOnlyList<RenderTarget> targets =
                    _renderGraph.GetLayoutTrackedRenderTargets(resource);
                if (targets.Count == 0)
                    continue;
                RenderGraphResourceLifetime lifetime =
                    _renderGraph.GetResourceLifetime(resource);
                for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    RenderTarget target = targets[targetIndex];
                    AddRenderTargetBinding(
                        bindings,
                        resource,
                        target,
                        queueFamilies,
                        graphicsFamily,
                        lifetime,
                        targetIndex,
                        targets.Count > 1 ? targetIndex : -1);
                }
            }

            if (_hizDepthPyramid is { Image.Handle: not 0 } hiz)
            {
                bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                    RenderGraphResourceId.HiZPyramid,
                    "Hi-Z depth pyramid",
                    hiz.Image,
                    new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = hiz.MipLevels,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    hiz.Layout,
                    queueFamilies,
                    graphicsFamily,
                    SharingMode.Exclusive,
                    allocationGeneration: hiz.Image.Handle,
                    lifetime: RenderGraphResourceLifetime.Persistent,
                    layoutTracker: layout => hiz.Layout = layout,
                    layoutProvider: () => hiz.Layout));
            }

            for (int imageIndex = 0; imageIndex < _swapchain.Images.Length; imageIndex++)
            {
                Image swapchainImage = _swapchain.Images[imageIndex];
                if (swapchainImage.Handle == 0)
                    continue;

                int capturedImageIndex = imageIndex;
                bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                    RenderGraphResourceId.SwapchainColor,
                    $"Swapchain image {imageIndex}",
                    swapchainImage,
                    new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    _swapchain.GetImageLayout((uint)imageIndex),
                    queueFamilies,
                    graphicsFamily,
                    SharingMode.Exclusive,
                    allocationGeneration: swapchainImage.Handle,
                    lifetime: RenderGraphResourceLifetime.Imported,
                    layoutTracker: layout => _swapchain.SetImageLayout((uint)capturedImageIndex, layout),
                    layoutProvider: () => _swapchain.GetImageLayout((uint)capturedImageIndex)));
            }

            if (_accelerationStructureManager != null)
            {
                foreach (AccelerationStructureStorageBuffer storage in _accelerationStructureManager
                             .GetRayQueryStorageBuffers())
                {
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.TlasStorage,
                        storage.DebugName,
                        storage.Handle,
                        queueFamilies,
                        graphicsFamily,
                        PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                        AccessFlags2.AccelerationStructureWriteBitKhr);
                }
            }

            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.RayQueryInstanceMetadata,
                "Ray-query instance metadata",
                _accelerationStructureManager?.RayQueryInstanceMetadataBuffer ?? BufferHandle.Invalid,
                queueFamilies, graphicsFamily);
            for (int frameIndex = 0;
                 frameIndex < FramesInFlight;
                 frameIndex++)
            {
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalRayShadowMask,
                    $"Directional ray-shadow mask frame {frameIndex}",
                    _directionalRayShadowPass?.GetMaskBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalShadowRaw,
                    $"Directional shadow raw frame {frameIndex}",
                    _directionalShadowHistoryResources?.GetRaw(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalShadowHistory,
                    $"Directional shadow history frame {frameIndex}",
                    _directionalShadowHistoryResources?.GetHistory(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalShadowScratch,
                    $"Directional shadow scratch frame {frameIndex}",
                    _directionalShadowHistoryResources?.GetScratch(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalShadowDiagnostics,
                    $"Directional shadow diagnostics frame {frameIndex}",
                    _directionalShadowHistoryResources?.GetDiagnostic(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.DirectionalShadowCounters,
                    $"Directional shadow counters frame {frameIndex}",
                    _directionalShadowHistoryResources?.GetCounters(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.AreaRayShadowMask,
                    $"Area ray-shadow mask frame {frameIndex}",
                    _areaRayShadowPass?.GetMaskBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.HybridReflectionRayTasks,
                    $"Hybrid reflection ray tasks frame {frameIndex}",
                    _hybridReflectionRuntime?.GetTaskBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.HybridReflectionCounters,
                    $"Hybrid reflection counters frame {frameIndex}",
                    _hybridReflectionRuntime?.GetCounterBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.HybridReflectionIndirectArguments,
                    $"Hybrid reflection indirect arguments frame {frameIndex}",
                    _hybridReflectionRuntime?.GetIndirectBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.HybridReflectionTileScheduler,
                    $"Hybrid reflection tiles frame {frameIndex}",
                    _hybridReflectionRuntime?.GetTileBuffer(frameIndex) ??
                    BufferHandle.Invalid,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: frameIndex);
            }

            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh vertex positions",
                _meshManager.VertexPositionBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers,
                "Mesh vertex normal tangents",
                _meshManager.VertexNormalTangentBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh vertex UV colors",
                _meshManager.VertexUvColorBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh index data",
                _meshManager.IndexBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh metadata",
                _meshManager.MeshMetadataBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MaterialBuffers, "Material data",
                _materialManager.MaterialBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MaterialBuffers, "Material extension data",
                _materialManager.MaterialExtensionBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.LightBuffers, "Light data",
                _lightManager.LightBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.EnvironmentData, "Environment data",
                _environmentManager?.EnvironmentBuffer ?? BufferHandle.Invalid, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.EnvironmentData,
                "Environment prefilter snapshot",
                _environmentManager?.PrefilterEnvironmentBuffer ?? BufferHandle.Invalid, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.EnvironmentData, "Stepped GI environment data",
                _environmentManager?.GiEnvironmentBuffer ?? BufferHandle.Invalid, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.DdgiEmissiveSources, "DDGI emissive sources",
                emissiveBuffers.SourceBuffer,
                queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.DdgiEmissiveSources,
                "DDGI emissive surface sidecars",
                emissiveBuffers.SurfaceBuffer,
                queueFamilies, graphicsFamily);
            for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
            {
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.RendererDiagnosticsBuffer,
                    "Renderer diagnostics",
                    _diagnosticsBuffer.GetBufferHandle(frameIndex), queueFamilies, graphicsFamily,
                    frameIndex: frameIndex);
                ParticleSystemManager.ParticleAsyncResourceSet particleResources =
                    _particleSystemManager.GetAsyncResourceSet(frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle frame data",
                    particleResources.FrameDataBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle instances",
                    particleResources.InstanceBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle batches",
                    particleResources.BatchBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
            }

            if (_farFieldClipmapManager != null)
            {
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldParameters, "Far-field parameters",
                    _farFieldClipmapManager.ParamsBuffer, queueFamilies, graphicsFamily);

                // Paged far-field baking writes directly into the resident page pool, so the
                // active and bake descriptors intentionally reference one physical allocation.
                // Register that allocation once under the shared graph resource. The legacy
                // clipmap still uses two physical ping-pong buffers and therefore exposes both.
                BufferHandle activeVoxelBuffer = _farFieldClipmapManager.VoxelBuffer;
                BufferHandle bakeVoxelBuffer = _farFieldClipmapManager.BakeVoxelBuffer;
                bool sharesVoxelAllocation = FarFieldClipmapManager.SharesVoxelAllocation(
                    activeVoxelBuffer,
                    bakeVoxelBuffer);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.FarFieldVoxels,
                    sharesVoxelAllocation ? "Far-field active/bake voxels" : "Far-field active voxels",
                    activeVoxelBuffer,
                    queueFamilies,
                    graphicsFamily);
                if (!sharesVoxelAllocation)
                {
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.FarFieldVoxels,
                        "Far-field bake voxels",
                        bakeVoxelBuffer,
                        queueFamilies,
                        graphicsFamily);
                }

                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldInstances, "Far-field instances",
                    _farFieldClipmapManager.InstanceBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood,
                    "Far-field distance field",
                    _farFieldClipmapManager.DistanceBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood,
                    "Far-field jump-flood scratch 0",
                    _farFieldClipmapManager.JumpFloodScratch0Buffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood,
                    "Far-field jump-flood scratch 1",
                    _farFieldClipmapManager.JumpFloodScratch1Buffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldPageTable, "Far-field page table",
                    _farFieldClipmapManager.PageTableBuffer, queueFamilies, graphicsFamily);
            }

            if (_simpleDdgiVolumeManager != null)
            {
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiParameters,
                    "Simple DDGI parameters",
                    _simpleDdgiVolumeManager.ParamsBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiIrradianceAtlas,
                    "Simple DDGI irradiance atlas",
                    _simpleDdgiVolumeManager.IrradianceAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiTransportAtlas,
                    "Simple DDGI transport irradiance target",
                    _simpleDdgiVolumeManager.TransportIrradianceAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiTransportSourceCache,
                    "Simple DDGI transport source cache",
                    _simpleDdgiVolumeManager.TransportSourceCacheBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiVisibilityAtlas,
                    "Simple DDGI visibility atlas",
                    _simpleDdgiVolumeManager.VisibilityAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiRayScratch,
                    "Simple DDGI ray scratch",
                    _simpleDdgiVolumeManager.RayResultScratchBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiProbeState,
                    "Simple DDGI probe state",
                    _simpleDdgiVolumeManager.ProbeStateBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiReceiverProbes,
                    "Simple DDGI compact receiver probes",
                    _simpleDdgiVolumeManager.ReceiverProbeBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiDirectionalRadiance,
                    "Simple DDGI directional radiance",
                    _simpleDdgiVolumeManager.DirectionalRadianceBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiDirectionalRadiance,
                    "Simple DDGI directional radiance parity",
                    _simpleDdgiVolumeManager.DirectionalRadianceParityBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiUpdateQueue,
                    "Simple DDGI update queue",
                    _simpleDdgiVolumeManager.ProbeUpdateQueueBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiRelocationData,
                    "Simple DDGI relocation classification",
                    _simpleDdgiVolumeManager.RelocationClassificationBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiScheduler,
                    "Simple DDGI GPU scheduler arena",
                    _simpleDdgiVolumeManager.GpuSchedulerArenaBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiResidency,
                    "Simple DDGI residency arena",
                    _simpleDdgiVolumeManager.ProbeResidencyGraphBuffer, queueFamilies, graphicsFamily);

                SimpleDdgiLightTreeGraphResourceSnapshot lightTree =
                    _simpleDdgiLightTreeResources?.CaptureGraphResources() ??
                    default;
                var uniqueLightTreeBuffers = new HashSet<BufferHandle>();
                foreach ((string name, BufferHandle handle) in new[]
                         {
                             ("Simple DDGI light-tree nodes", lightTree.Node),
                             ("Simple DDGI light-tree leaves", lightTree.Leaf),
                             ("Simple DDGI light-tree state", lightTree.State),
                             ("Simple DDGI light-tree scratch", lightTree.Scratch)
                         })
                {
                    if (!handle.IsValid || !uniqueLightTreeBuffers.Add(handle))
                        continue;
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.SimpleDdgiLightTree,
                        name,
                        handle,
                        queueFamilies,
                        graphicsFamily);
                }

                if (_simpleDdgiVolumeManager
                    .TryGetSampledAtlasGraphResourceSnapshot(
                        out SimpleDdgiSampledAtlasGraphResourceSnapshot
                            sampledAtlas))
                {
                    AddSimpleDdgiSampledAtlasBindings(
                        bindings,
                        RenderGraphResourceId
                            .SimpleDdgiSampledIrradianceAtlas,
                        sampledAtlas.Irradiance!,
                        sampledAtlas,
                        queueFamilies,
                        graphicsFamily);
                    AddSimpleDdgiSampledAtlasBindings(
                        bindings,
                        RenderGraphResourceId
                            .SimpleDdgiSampledVisibilityAtlas,
                        sampledAtlas.Visibility!,
                        sampledAtlas,
                        queueFamilies,
                        graphicsFamily);
                }
            }

            if (_simpleDdgiGuidingFrameCoordinator is { } guidingCoordinator &&
                guidingCoordinator.TryGetGraphResourceSnapshot(
                    out SimpleDdgiGuidingGraphResourceSnapshot guiding))
            {
                AddAsyncComputeBufferRangeBinding(
                    bindings,
                    RenderGraphResourceId.SimpleDdgiGuidingDistributions,
                    "C3 directional-guiding distribution bank 0",
                    guiding.Distributions.DistributionBank0,
                    0UL,
                    guiding.Distributions.BankBytes,
                    guiding.Distributions.AllocationGeneration,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferRangeBinding(
                    bindings,
                    RenderGraphResourceId.SimpleDdgiGuidingDistributions,
                    "C3 directional-guiding distribution bank 1",
                    guiding.Distributions.DistributionBank1,
                    0UL,
                    guiding.Distributions.BankBytes,
                    guiding.Distributions.AllocationGeneration,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferRangeBinding(
                    bindings,
                    RenderGraphResourceId.SimpleDdgiGuidingScratch,
                    "C3 directional-guiding transient workspace",
                    guiding.WorkspaceBuffer,
                    guiding.WorkspaceOffsetBytes,
                    guiding.WorkspaceBytes,
                    guiding.WorkspaceGeneration,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferRangeBinding(
                    bindings,
                    RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar,
                    "C3 source-cache direction/PDF sidecar",
                    guiding.DirectionPayloadSidecar,
                    0UL,
                    guiding.DirectionPayloadBytes,
                    guiding.DirectionPayloadGeneration,
                    queueFamilies,
                    graphicsFamily);
            }

            GiCausticGraphResourceSnapshot causticResources =
                _giCaustic.CaptureGraphResources();
            if (causticResources.Runtime is not null)
            {
                GiCausticVulkanBuffers caustic = causticResources.Buffers;
                if (caustic.IsComplete)
                {
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.GiCausticTasks,
                        "C4 task and source metadata",
                        caustic.Tasks,
                        queueFamilies,
                        graphicsFamily);
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.GiCausticPhotons,
                        "C4 candidate and published photons",
                        caustic.Photons,
                        queueFamilies,
                        graphicsFamily);
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.GiCausticCache,
                        "C4 cache tables and publication headers",
                        caustic.Cache,
                        queueFamilies,
                        graphicsFamily);
                    AddAsyncComputeBufferBinding(
                        bindings,
                        RenderGraphResourceId.GiCausticScratch,
                        "C4 deterministic build and tile scratch",
                        caustic.Scratch,
                        queueFamilies,
                        graphicsFamily);
                    for (int frameIndex = 0;
                         frameIndex < FramesInFlight;
                         frameIndex++)
                    {
                        AddAsyncComputeBufferBinding(
                            bindings,
                            RenderGraphResourceId.GiCausticScreenFrameConstants,
                            $"C4 screen frame constants {frameIndex}",
                            causticResources.GetFrameConstants(frameIndex),
                            queueFamilies,
                            graphicsFamily,
                            frameIndex: frameIndex);
                    }
                }
            }

            NearFieldResidualGraphResourceSnapshot nearFieldResources =
                _nearFieldResidual.CaptureGraphResources();
            if (nearFieldResources.Runtime is { } nearFieldRuntime)
            {
                SimpleDdgiNearFieldResidualVulkanBuffers nearField =
                    nearFieldRuntime.Buffers;
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    "C5 history metadata 0",
                    nearField.HistoryMetadata0,
                    queueFamilies,
                    graphicsFamily,
                    historyIndex: 0);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                    "C5 history metadata 1",
                    nearField.HistoryMetadata1,
                    queueFamilies,
                    graphicsFamily,
                    historyIndex: 1);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldSurfaceTable,
                    "C5 frame-buffered surface table",
                    nearField.SurfaceTable,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments,
                    "C5 active tile list and indirect arguments",
                    nearField.ActiveTileAndIndirect,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldResidualTileBuffers,
                    "C5 tile records",
                    nearField.TileRecords,
                    queueFamilies,
                    graphicsFamily);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldResidualTraceFrameConstants,
                    "C5 trace frame constants 0",
                    nearField.TraceFrameConstants0,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: 0);
                AddAsyncComputeBufferBinding(
                    bindings,
                    RenderGraphResourceId.NearFieldResidualTraceFrameConstants,
                    "C5 trace frame constants 1",
                    nearField.TraceFrameConstants1,
                    queueFamilies,
                    graphicsFamily,
                    frameIndex: 1);
            }

            GpuParticleAsyncResourceSet firstParticleResources =
                _gpuParticleRuntimeManager.GetAsyncResourceSet(0);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndices,
                "GPU particle dead indices",
                firstParticleResources.DeadIndexBuffer, queueFamilies, graphicsFamily);
            for (int frameIndex = 0; frameIndex < FramesInFlight; frameIndex++)
            {
                GpuParticleAsyncResourceSet particleResources =
                    _gpuParticleRuntimeManager.GetAsyncResourceSet(frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleState, "GPU particle state",
                    particleResources.StateBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndices,
                    "GPU particle alive indices",
                    particleResources.AliveIndexBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleEmitterData,
                    "GPU particle emitters",
                    particleResources.EmitterBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleEmitterData,
                    "GPU particle curves",
                    particleResources.CurveSampleBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleCounters,
                    "GPU particle counters",
                    particleResources.CounterBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleUnsortedOutput,
                    "GPU particle unsorted output",
                    particleResources.UnsortedRenderInstanceBuffer, queueFamilies, graphicsFamily,
                    frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleRenderOutput,
                    "GPU particle render output",
                    particleResources.RenderInstanceBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndirectArguments,
                    "GPU particle indirect arguments",
                    particleResources.IndirectDrawBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleSortKeys,
                    "GPU particle sort keys",
                    particleResources.SortKeyBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleCounterReadback,
                    "GPU particle counter readback",
                    particleResources.CounterReadbackBuffer, queueFamilies, graphicsFamily, frameIndex: frameIndex);
            }

            // Deduplicate within a logical set only. The same texture can be reachable through
            // both environment and material descriptors; RenderGraphResourceBindings models
            // those exact aliases with one physical ownership key.
            AddTextureBindings(bindings, RenderGraphResourceId.EnvironmentMaps, "Environment texture",
                _environmentManager?.GetSampledTextureHandles(),
                queueFamilies, graphicsFamily, new HashSet<ulong>());
            IReadOnlyList<TextureHandle> materialTextures = _materialManager.GetReferencedTextureHandles();
            if (materialTextures.Count == 0)
                materialTextures = [_textureManager.DefaultWhiteTexture];
            int materialTextureBindingCount = AddTextureBindings(bindings, RenderGraphResourceId.MaterialTextures,
                "Material texture", materialTextures,
                queueFamilies, graphicsFamily, new HashSet<ulong>());
            if (materialTextureBindingCount == 0)
            {
                AddTextureBindings(bindings, RenderGraphResourceId.MaterialTextures, "Material fallback texture",
                    [_textureManager.DefaultWhiteTexture], queueFamilies, graphicsFamily, new HashSet<ulong>());
            }

            RenderGraphResourcePlan plan = _renderGraph.CreateConcreteResourcePlan(bindings);
            _renderGraph.ActivateConcreteResourcePlan(plan, resetState: true);
            _asyncComputeResourcePlan = plan;
            _asyncComputeResourcePlanGeneration = generation;
        }

        private static void AddRenderTargetBinding(
            ICollection<RenderGraphConcreteResourceBinding> bindings,
            RenderGraphResourceId resource,
            RenderTarget target,
            IReadOnlyList<uint> queueFamilies,
            uint graphicsFamily,
            RenderGraphResourceLifetime lifetime,
            int bindingIndex,
            int historyIndex)
        {
            if (target.Image.Handle == 0)
                return;

            ImageAspectFlags aspect = target.Descriptor.DepthAttachment
                ? ImageAspectFlags.DepthBit
                : ImageAspectFlags.ColorBit;
            bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                resource,
                $"{target.Name}#{bindingIndex}",
                target.Image,
                new ImageSubresourceRange
                {
                    AspectMask = aspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                target.Layout,
                queueFamilies,
                graphicsFamily,
                SharingMode.Exclusive,
                frameIndex: -1,
                historyIndex: historyIndex,
                allocationGeneration: target.Image.Handle,
                lifetime: lifetime,
                layoutTracker: target.SetTrackedLayout,
                layoutProvider: () => target.Layout));
        }

        private void AddAsyncComputeBufferBinding(
            ICollection<RenderGraphConcreteResourceBinding> bindings,
            RenderGraphResourceId resource,
            string name,
            BufferHandle handle,
            IReadOnlyList<uint> queueFamilies,
            uint graphicsFamily,
            PipelineStageFlags2 initialStageMask = PipelineStageFlags2.None,
            AccessFlags2 initialAccessMask = AccessFlags2.None,
            int frameIndex = -1,
            int historyIndex = -1)
        {
            if (!handle.IsValid)
                return;

            ulong byteSize = _bufferManager.GetBufferSize(handle);
            if (byteSize == 0)
                return;

            bindings.Add(RenderGraphConcreteResourceBinding.ForBuffer(
                resource,
                $"{name}#{handle.Index}.{handle.Generation}",
                _bufferManager.GetBuffer(handle),
                byteSize,
                queueFamilies,
                graphicsFamily,
                SharingMode.Exclusive,
                frameIndex: frameIndex,
                historyIndex: historyIndex,
                allocationGeneration: handle.Generation,
                lifetime: RenderGraphResourceLifetime.Imported,
                allocationSize: byteSize,
                initialStageMask: initialStageMask,
                initialAccessMask: initialAccessMask));
        }

        private void AddAsyncComputeBufferRangeBinding(
            ICollection<RenderGraphConcreteResourceBinding> bindings,
            RenderGraphResourceId resource,
            string name,
            BufferHandle handle,
            ulong byteOffset,
            ulong byteSize,
            ulong allocationGeneration,
            IReadOnlyList<uint> queueFamilies,
            uint graphicsFamily,
            PipelineStageFlags2 initialStageMask = PipelineStageFlags2.None,
            AccessFlags2 initialAccessMask = AccessFlags2.None)
        {
            if (!handle.IsValid || byteSize == 0UL || allocationGeneration == 0UL)
                return;

            ulong allocationSize = _bufferManager.GetBufferSize(handle);
            if (allocationSize == 0UL || byteOffset > allocationSize ||
                byteSize > allocationSize - byteOffset)
            {
                return;
            }

            bindings.Add(RenderGraphConcreteResourceBinding.ForBuffer(
                resource,
                $"{name}#{handle.Index}.{handle.Generation}",
                _bufferManager.GetBuffer(handle),
                byteSize,
                queueFamilies,
                graphicsFamily,
                SharingMode.Exclusive,
                byteOffset: byteOffset,
                allocationGeneration: allocationGeneration,
                lifetime: RenderGraphResourceLifetime.Imported,
                allocationSize: allocationSize,
                initialStageMask: initialStageMask,
                initialAccessMask: initialAccessMask));
        }

        private static void AddSimpleDdgiSampledAtlasBindings(
            ICollection<RenderGraphConcreteResourceBinding> bindings,
            RenderGraphResourceId resource,
            IReadOnlyList<SimpleDdgiSampledAtlasImageGraphBinding> images,
            in SimpleDdgiSampledAtlasGraphResourceSnapshot snapshot,
            IReadOnlyList<uint> queueFamilies,
            uint graphicsFamily)
        {
            uint? initialOwner = snapshot.SharingMode == SharingMode.Concurrent
                ? null
                : graphicsFamily;
            for (int imageIndex = 0;
                 imageIndex < images.Count;
                 imageIndex++)
            {
                SimpleDdgiSampledAtlasImageGraphBinding image =
                    images[imageIndex];
                if (image.Image.Handle == 0 || image.LayerCount == 0u)
                    continue;

                bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                    resource,
                    image.Name,
                    image.Image,
                    new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0u,
                        LevelCount = 1u,
                        BaseArrayLayer = 0u,
                        LayerCount = image.LayerCount
                    },
                    image.LayoutProvider(),
                    queueFamilies,
                    initialOwner,
                    snapshot.SharingMode,
                    allocationGeneration: snapshot.AllocationGeneration,
                    lifetime: RenderGraphResourceLifetime.Imported,
                    layoutTracker: image.LayoutTracker,
                    layoutProvider: image.LayoutProvider));
            }
        }

        private int AddTextureBindings(
            ICollection<RenderGraphConcreteResourceBinding> bindings,
            RenderGraphResourceId resource,
            string name,
            IReadOnlyList<TextureHandle>? textureHandles,
            IReadOnlyList<uint> queueFamilies,
            uint graphicsFamily,
            ISet<ulong> boundImages)
        {
            if (textureHandles == null || textureHandles.Count == 0)
                return 0;

            int added = 0;
            for (int index = 0; index < textureHandles.Count; index++)
            {
                TextureHandle handle = textureHandles[index];
                if (!handle.IsValid || !_textureManager.TryGetImageBinding(handle, out TextureImageBinding texture))
                    continue;
                if (!boundImages.Add(texture.Image.Handle))
                    continue;

                bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                    resource,
                    $"{name}#{handle.Index}.{handle.Generation}",
                    texture.Image,
                    new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = texture.MipLevels,
                        BaseArrayLayer = 0,
                        LayerCount = texture.ArrayLayers
                    },
                    ImageLayout.ShaderReadOnlyOptimal,
                    queueFamilies,
                    graphicsFamily,
                    SharingMode.Exclusive,
                    allocationGeneration: texture.Generation == 0 ? 1u : texture.Generation,
                    lifetime: RenderGraphResourceLifetime.Imported));
                added++;
            }

            return added;
        }

        private readonly record struct AsyncComputeResourcePlanGeneration(
            ulong GraphResourceGeneration,
            int RenderTargetGeneration,
            ulong SwapchainGeneration,
            ulong MaterialTextureSetGeneration,
            ulong TextureGeneration,
            uint EnvironmentGeneration,
            ulong ParticleGeneration,
            ulong GpuParticleGeneration,
            ulong AccelerationStructureGeneration,
            uint GraphicsQueueFamily,
            uint ComputeQueueFamily,
            ulong HiZImage,
            AsyncCoreBufferIdentity CoreBuffers,
            FarFieldAsyncBufferIdentity FarFieldBuffers,
            SimpleDdgiAsyncBufferIdentity SimpleDdgiBuffers,
            DirectionalRayShadowAsyncBufferIdentity DirectionalRayShadowBuffers,
            CausticAsyncBufferIdentity CausticBuffers,
            GuidingAsyncBufferIdentity GuidingBuffers,
            NearFieldResidualAsyncBufferIdentity NearFieldResidualBuffers,
            HybridReflectionAsyncBufferIdentity HybridReflectionBuffers);

        private readonly record struct HybridReflectionAsyncBufferIdentity(
            BufferHandle Tasks0,
            BufferHandle Tasks1,
            BufferHandle Counters0,
            BufferHandle Counters1,
            BufferHandle Indirect0,
            BufferHandle Indirect1);

        private readonly record struct DirectionalRayShadowAsyncBufferIdentity(
            BufferHandle Frame0,
            BufferHandle Frame1,
            BufferHandle Raw0,
            BufferHandle Raw1,
            BufferHandle History0,
            BufferHandle History1,
            BufferHandle Scratch0,
            BufferHandle Scratch1,
            BufferHandle Diagnostic0,
            BufferHandle Diagnostic1,
            BufferHandle Counters0,
            BufferHandle Counters1,
            BufferHandle AreaMask0,
            BufferHandle AreaMask1,
            uint ResourceGeneration,
            uint HistoryResourceGeneration,
            uint AreaMaskResourceGeneration);

        private readonly record struct CausticAsyncBufferIdentity(
            BufferHandle Tasks,
            BufferHandle Photons,
            BufferHandle Cache,
            BufferHandle Scratch,
            BufferHandle FrameConstants0,
            BufferHandle FrameConstants1);

        private readonly record struct GuidingAsyncBufferIdentity(
            BufferHandle DistributionBank0,
            BufferHandle DistributionBank1,
            ulong DistributionBankBytes,
            ulong DistributionAllocationGeneration,
            BufferHandle Workspace,
            ulong WorkspaceOffsetBytes,
            ulong WorkspaceBytes,
            ulong WorkspaceGeneration,
            BufferHandle DirectionPayloadSidecar,
            ulong DirectionPayloadBytes,
            ulong DirectionPayloadGeneration);

        private readonly record struct AsyncCoreBufferIdentity(
            BufferHandle MeshPositions,
            BufferHandle MeshNormalTangents,
            BufferHandle MeshUvColors,
            BufferHandle MeshIndices,
            BufferHandle MeshMetadata,
            BufferHandle Materials,
            BufferHandle MaterialExtensions,
            BufferHandle Lights,
            BufferHandle Environment,
            BufferHandle PrefilterEnvironment,
            BufferHandle GiEnvironment,
            BufferHandle DdgiEmissiveSources,
            BufferHandle DdgiEmissiveSurfaces);

        private readonly record struct FarFieldAsyncBufferIdentity(
            BufferHandle Parameters,
            BufferHandle Voxels,
            BufferHandle BakeVoxels,
            BufferHandle Instances,
            BufferHandle Distance,
            BufferHandle JumpFloodScratch0,
            BufferHandle JumpFloodScratch1,
            BufferHandle PageTable);

        private readonly record struct SimpleDdgiAsyncBufferIdentity(
            BufferHandle Parameters,
            BufferHandle IrradianceAtlas,
            BufferHandle TransportIrradianceAtlas,
            BufferHandle TransportSourceCache,
            BufferHandle VisibilityAtlas,
            BufferHandle RayScratch,
            BufferHandle ProbeState,
            BufferHandle ReceiverProbes,
            BufferHandle DirectionalRadiance,
            BufferHandle DirectionalRadianceParity,
            BufferHandle UpdateQueue,
            BufferHandle RelocationClassification,
            BufferHandle Scheduler,
            BufferHandle Residency,
            BufferHandle LightTreeNode,
            BufferHandle LightTreeLeaf,
            BufferHandle LightTreeState,
            BufferHandle LightTreeScratch,
            ulong SampledAtlasAllocationGeneration);

        private readonly record struct NearFieldResidualAsyncBufferIdentity(
            BufferHandle HistoryMetadata0,
            BufferHandle HistoryMetadata1,
            BufferHandle SurfaceTable,
            BufferHandle ActiveTileAndIndirect,
            BufferHandle TileRecords,
            BufferHandle TraceFrameConstants0,
            BufferHandle TraceFrameConstants1);

        private UploadBudgetSnapshot BuildUploadBudgetSnapshot(SceneRenderingData sceneData,
            RenderBudgetProfile profile)
        {
            _uploadBudgetTracker.BeginFrame();
            _uploadBudgetTracker.AddBytes(
                UploadBudgetCategory.Scene,
                sceneData.ObjectUploadBytes +
                sceneData.InstanceUploadBytes +
                sceneData.MeshletDrawUploadBytes +
                sceneData.SolidDepthMeshletDrawUploadBytes +
                sceneData.MaskedDepthMeshletDrawUploadBytes +
                sceneData.TransparentMeshletDrawUploadBytes);
            _uploadBudgetTracker.AddBytes(
                UploadBudgetCategory.Materials,
                sceneData.MaterialUploadBytes + sceneData.MaterialExtensionUploadBytes);
            _uploadBudgetTracker.AddBytes(UploadBudgetCategory.Lights, sceneData.LightUploadBytes);
            _uploadBudgetTracker.AddBytes(UploadBudgetCategory.Animation, sceneData.SkinningUploadBytes);
            _uploadBudgetTracker.AddBytes(
                UploadBudgetCategory.Particles,
                sceneData.ParticleInstanceUploadBytes + sceneData.TrailBeamUploadBytes);
            _uploadBudgetTracker.AddBytes(UploadBudgetCategory.Reflections,
                (ulong)Math.Max(0, sceneData.ReflectionProbeCount) * 0UL);
            ulong knownBytes =
                sceneData.ObjectUploadBytes +
                sceneData.InstanceUploadBytes +
                sceneData.MeshletDrawUploadBytes +
                sceneData.SolidDepthMeshletDrawUploadBytes +
                sceneData.MaskedDepthMeshletDrawUploadBytes +
                sceneData.TransparentMeshletDrawUploadBytes +
                sceneData.MaterialUploadBytes +
                sceneData.MaterialExtensionUploadBytes +
                sceneData.LightUploadBytes +
                sceneData.SkinningUploadBytes +
                sceneData.ParticleInstanceUploadBytes +
                sceneData.TrailBeamUploadBytes;
            if (sceneData.UploadedBytes > knownBytes)
                _uploadBudgetTracker.AddBytes(UploadBudgetCategory.Unknown, sceneData.UploadedBytes - knownBytes);

            return _uploadBudgetTracker.EndFrame(profile);
        }

        private MemoryBudgetSnapshot BuildMemoryBudgetSnapshot(RenderBudgetProfile profile)
        {
            MemoryBudgetSnapshot tracked = _bufferManager.AllocationTracker.CreateSnapshot(profile);
            var entries = new List<MemoryBudgetEntry>(tracked.Entries.Count + 8);
            ulong totalBytes = 0;
            foreach (MemoryBudgetEntry entry in tracked.Entries)
                AddMemoryEntry(entries, ref totalBytes, entry.Category, entry.Bytes, entry.AllocationCount,
                    entry.Description);

            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.TextureAssets,
                _textureManager.FileTextureBytes + _textureManager.DefaultTextureBytes,
                _textureManager.TextureCount,
                "Texture assets");
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.RenderTargets,
                _renderTargets?.TotalEstimatedBytes ?? 0,
                _renderTargets?.RenderTargetCount ?? 0,
                "Renderer-owned render targets");
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.ShadowMaps,
                (_directionalShadowResources?.EstimatedImageBytes ?? 0) +
                (_spotShadowAtlas?.EstimatedImageBytes ?? 0) +
                (_pointShadowCubemapArray?.EstimatedImageBytes ?? 0),
                3,
                "Shadow map images");
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.EnvironmentMaps,
                _environmentManager?.EstimatedBytes ?? 0,
                _environmentManager?.TextureResourceCount ?? 0,
                "Environment maps and BRDF LUT");
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.ReflectionProbes,
                _reflectionProbeManager?.CubemapArrayBytes ?? 0,
                _reflectionProbeManager == null ? 0 : 1,
                "Reflection probe cubemap array");
            // DDGI, Simple-DDGI, far-field, and GI acceleration-structure allocations register
            // themselves with AllocationTracker under GlobalIllumination. Adding the managers'
            // counters again here double-counted the same residency. GI render targets are
            // already included in the renderer-owned render-target entry above; detailed unique
            // GI residency is assembled from these disjoint sources by GiResidencyReporter.
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.Swapchain,
                _swapchain.EstimatedBytes,
                (int)_swapchain.ImageCount + 1,
                "Swapchain color images and depth target");

            entries.Sort((left, right) => left.Category.CompareTo(right.Category));
            return new MemoryBudgetSnapshot(
                totalBytes,
                profile.GpuMemoryBudgetBytes,
                entries,
                _context.GetMemoryHeapBudgetSnapshot());
        }

        private static void AddMemoryEntry(
            List<MemoryBudgetEntry> entries,
            ref ulong totalBytes,
            MemoryBudgetCategory category,
            ulong bytes,
            int allocationCount,
            string description)
        {
            if (bytes == 0 && allocationCount == 0)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                MemoryBudgetEntry existing = entries[i];
                if (existing.Category == category)
                {
                    entries[i] = existing with
                    {
                        Bytes = existing.Bytes + bytes,
                        AllocationCount = existing.AllocationCount + allocationCount
                    };
                    totalBytes += bytes;
                    return;
                }
            }

            entries.Add(new MemoryBudgetEntry(category, bytes, allocationCount, description));
            totalBytes += bytes;
        }

        internal static GlobalIlluminationMode ResolveEffectiveGlobalIlluminationMode(
            GlobalIlluminationSettings settings,
            bool rayQuerySupported)
        {
            if (!settings.Enabled)
                return GlobalIlluminationMode.Disabled;
            return settings.Mode == GlobalIlluminationMode.Ddgi
                ? GlobalIlluminationMode.Ddgi
                : GlobalIlluminationMode.Disabled;
        }

        private string BuildGpuTimingReason()
        {
            if (!_gpuTimestamps.Supported)
                return _gpuTimestamps.UnsupportedReason;

            if (!Settings.Debug.AllowGpuTiming)
                return
                    "GPU timing is disabled. Enable RenderSettings.Debug.AllowGpuTiming or press Ctrl+F4 in the sample.";

            if (_gpuTimestamps.PendingThisFrame && !HasCompletedGpuTiming(_gpuTimestamps.LastCompletedSnapshot))
                return "GPU timing is enabled; waiting for a completed frame of timestamp results.";

            return string.Empty;
        }

        private static bool HasCompletedGpuTiming(FrameTimingSnapshot timings)
        {
            foreach (PassTiming timing in timings.Passes)
            {
                if (timing.GpuAvailable)
                    return true;
            }

            return false;
        }

        private static bool HasCompletedDdgiGpuTiming(FrameTimingSnapshot timings)
        {
            return HasCompletedSimpleDdgiGpuTiming(timings);
        }

        private static bool HasCompletedSimpleDdgiGpuTiming(FrameTimingSnapshot timings)
        {
            return HasCompletedGpuTiming(timings, "SimpleDdgiPageDemandPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiPageResidencyPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiPageFeedbackPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiSchedulePass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiTracePass") ||
                   HasCompletedGpuTiming(
                       timings,
                       "SimpleDdgiDirectionalRadiancePass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiAcceleratedSolvePass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiTransportPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiBlendPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiPublishPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiTransportAuditPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiRelocateClassifyPass") ||
                   HasCompletedGpuTiming(timings, "SimpleDdgiSchedulerCommitPass");
        }

        private static bool HasCompletedGpuTiming(FrameTimingSnapshot timings, string passName)
        {
            return timings.TryGetPass(passName, out PassTiming timing) && timing.GpuAvailable;
        }

        internal static void ApplyCompletedGpuTimings(SceneRenderingData sceneData, FrameTimingSnapshot timings)
        {
            sceneData.GpuSkinningMicroseconds = timings.GetGpuMicrosecondsOrZero("SkinningPass");
            sceneData.GpuDirectionalShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("DirectionalShadowPass");
            sceneData.GpuDirectionalRayShadowMicroseconds =
                timings.GetGpuMicrosecondsOrZero("DirectionalRayShadowPass");
            sceneData.GpuAreaRayShadowMicroseconds =
                timings.GetGpuMicrosecondsOrZero("AreaRayShadowPass");
            sceneData.GpuDirectionalShadowTemporalMicroseconds =
                timings.GetGpuMicrosecondsOrZero("DirectionalShadowTemporalPass");
            sceneData.GpuDirectionalShadowSpatialMicroseconds =
                timings.GetGpuMicrosecondsOrZero("DirectionalShadowSpatialPass");
            sceneData.GpuSpotShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("SpotShadowPass");
            sceneData.GpuPointShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("PointShadowPass");
            bool reflectionTimingsMatchCompletedLifecycle =
                sceneData.ReflectionProbeCompletedLifecycle.Valid &&
                sceneData.ReflectionProbeCompletedLifecycle.GpuTimingRecorded;
            sceneData.GpuReflectionProbeCaptureMicroseconds =
                reflectionTimingsMatchCompletedLifecycle
                    ? timings.GetGpuMicrosecondsOrZero("ReflectionProbeCapturePass")
                    : 0;
            sceneData.GpuReflectionProbePrefilterMicroseconds =
                reflectionTimingsMatchCompletedLifecycle
                    ? timings.GetGpuMicrosecondsOrZero("ReflectionProbePrefilterPass")
                    : 0;
            sceneData.GpuReflectionProbePublishMicroseconds =
                reflectionTimingsMatchCompletedLifecycle
                    ? timings.GetGpuMicrosecondsOrZero("ReflectionProbePublishPass")
                    : 0;
            sceneData.GpuAutomaticPlanarCaptureMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "AutomaticPlanarReflectionPass");
            sceneData.GpuHybridReflectionSsrMicroseconds =
                timings.GetGpuMicrosecondsOrZero("HybridReflectionSsrPass");
            sceneData.GpuHybridReflectionRayQueryMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "HybridReflectionRayQueryPass");
            sceneData.GpuHybridReflectionDdgiBaseMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "HybridReflectionDdgiBasePass");
            sceneData.GpuHybridReflectionResolveMicroseconds =
                timings.GetGpuMicrosecondsOrZero("HybridReflectionResolvePass");
            sceneData.GpuHybridReflectionTemporalMicroseconds =
                timings.GetGpuMicrosecondsOrZero("HybridReflectionTemporalPass");
            sceneData.GpuHybridReflectionSpatialMicroseconds =
                timings.GetGpuMicrosecondsOrZero("HybridReflectionSpatialPass");
            sceneData.GpuHybridReflectionCompositeMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "HybridReflectionCompositePass");
            sceneData.GpuDepthPrePassMicroseconds = timings.GetGpuMicrosecondsOrZero("DepthPrePass");
            sceneData.GpuMotionVectorMicroseconds = timings.GetGpuMicrosecondsOrZero("MotionVectorPass");
            sceneData.GpuHiZBuildMicroseconds = timings.GetGpuMicrosecondsOrZero("HiZBuildPass");
            sceneData.GpuAmbientOcclusionMicroseconds =
                timings.GetGpuMicrosecondsOrZero("AmbientOcclusionPass") +
                timings.GetGpuMicrosecondsOrZero("GtaoPass");
            sceneData.GpuAmbientOcclusionBlurMicroseconds =
                timings.GetGpuMicrosecondsOrZero("AmbientOcclusionBlurPass") +
                timings.GetGpuMicrosecondsOrZero("GtaoTemporalPass") +
                timings.GetGpuMicrosecondsOrZero("GtaoSpatialPass");
            sceneData.GpuOpacityMicromapBuildMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "OpacityMicromapBuildPass");
            sceneData.GpuAccelerationStructureBlasMicroseconds = checked(
                timings.GetGpuMicrosecondsOrZero(
                    "AccelerationStructureBlasPass") +
                sceneData.GpuOpacityMicromapBuildMicroseconds);
            sceneData.GpuAccelerationStructureTlasMicroseconds =
                timings.GetGpuMicrosecondsOrZero("AccelerationStructureTlasPass");
            sceneData.GpuDdgiFoliageProxyGenerationMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "DdgiFoliageProxyGenerationPass");
            sceneData.GpuSimpleDdgiPageDemandMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageDemandPass");
            sceneData.GpuSimpleDdgiPageResidencyMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageResidencyPass");
            sceneData.GpuSimpleDdgiPageFeedbackMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiPageFeedbackPass");
            sceneData.GpuSimpleDdgiScheduleMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedulePass");
            sceneData.GpuSimpleDdgiScheduleResetMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Reset");
            sceneData.GpuSimpleDdgiScheduleClassifyMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Classify");
            sceneData.GpuSimpleDdgiSchedulePrefixMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Prefix");
            sceneData.GpuSimpleDdgiScheduleLaneBaseMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.LaneBase");
            sceneData.GpuSimpleDdgiScheduleCompactMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Compact");
            sceneData.GpuSimpleDdgiScheduleTailAdmitMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.TailAdmit");
            sceneData.GpuSimpleDdgiScheduleAdmitMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Admit");
            sceneData.GpuSimpleDdgiScheduleMaterializeMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Materialize");
            sceneData.GpuSimpleDdgiScheduleEmitMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedule.Emit");
            sceneData.GpuSimpleDdgiTraceMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiTracePass");
            sceneData.GpuSimpleDdgiDirectionalRadianceMicroseconds =
                timings.GetGpuMicrosecondsOrZero(
                    "SimpleDdgiDirectionalRadiancePass");
            sceneData.GpuSimpleDdgiAcceleratedSolveMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiAcceleratedSolvePass");
            sceneData.GpuSimpleDdgiTransportMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportPass");
            sceneData.GpuSimpleDdgiBlendMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiBlendPass");
            sceneData.GpuSimpleDdgiRelocateClassifyMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiRelocateClassifyPass");
            sceneData.GpuSimpleDdgiPublishMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiPublishPass");
            sceneData.GpuSimpleDdgiTransportAuditMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportAuditPass");
            sceneData.GpuSimpleDdgiCommitMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiSchedulerCommitPass");
            sceneData.GpuSimpleDdgiUrgentRelightMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiUrgentRelightPass");
            sceneData.GpuFarFieldUpdateMicroseconds = timings.GetGpuMicrosecondsOrZero("FarFieldClipmapBakePass");
            sceneData.GpuFarFieldUpdateTimingValid = HasCompletedGpuTiming(timings, "FarFieldClipmapBakePass") ? 1 : 0;
            sceneData.GpuDdgiUpdateMicroseconds =
                sceneData.GpuDdgiFoliageProxyGenerationMicroseconds +
                sceneData.GpuSimpleDdgiPageDemandMicroseconds +
                sceneData.GpuSimpleDdgiPageResidencyMicroseconds +
                sceneData.GpuSimpleDdgiPageFeedbackMicroseconds +
                sceneData.GpuSimpleDdgiScheduleMicroseconds +
                sceneData.GpuSimpleDdgiTraceMicroseconds +
                sceneData.GpuSimpleDdgiDirectionalRadianceMicroseconds +
                sceneData.GpuSimpleDdgiAcceleratedSolveMicroseconds +
                sceneData.GpuSimpleDdgiTransportMicroseconds +
                sceneData.GpuSimpleDdgiBlendMicroseconds +
                sceneData.GpuSimpleDdgiTransportAuditMicroseconds +
                sceneData.GpuSimpleDdgiRelocateClassifyMicroseconds +
                sceneData.GpuSimpleDdgiPublishMicroseconds +
                sceneData.GpuSimpleDdgiCommitMicroseconds;
            sceneData.GpuGiCompositeMicroseconds = 0;
            sceneData.GpuLightCullMicroseconds = timings.GetGpuMicrosecondsOrZero("TiledLightCullingPass");
            sceneData.GpuFoliageCullMicroseconds = timings.GetGpuMicrosecondsOrZero("FoliageCullPass");
            sceneData.GpuFoliageShadowMicroseconds = sceneData.FoliageCastShadows && sceneData.FoliageClusterCount > 0
                ? sceneData.GpuDirectionalShadowMicroseconds
                : 0;
            sceneData.GpuForwardOpaqueMicroseconds = timings.GetGpuMicrosecondsOrZero("ForwardPlusPass");
            sceneData.GpuForwardGiGatherMicroseconds = timings.GetGpuMicrosecondsOrZero("ForwardGiGatherPass");
            sceneData.GpuSimpleDdgiReceiverCacheMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiReceiverCachePass");
            sceneData.GpuForwardGiGatherTimingCoverage = HasCompletedGpuTiming(timings, "ForwardGiGatherPass") ? 1 : 0;
            sceneData.GpuTransparentMicroseconds =
                timings.GetGpuMicrosecondsOrZero("TransparentForwardPass") +
                timings.GetGpuMicrosecondsOrZero("WeightedTransparentPass") +
                timings.GetGpuMicrosecondsOrZero("WeightedOitCompositePass");
            sceneData.GpuParticleMicroseconds =
                timings.GetGpuMicrosecondsOrZero("GpuParticleResetPass") +
                timings.GetGpuMicrosecondsOrZero("GpuParticleSimulatePass") +
                timings.GetGpuMicrosecondsOrZero("GpuParticleSortPass") +
                timings.GetGpuMicrosecondsOrZero("ParticlePass");
            sceneData.GpuDebugDrawMicroseconds = timings.GetGpuMicrosecondsOrZero("DebugDrawPass");
            sceneData.GpuDebugDdgiProbeMicroseconds =
                timings.GetGpuMicrosecondsOrZero("SimpleDdgiProbeDebugPass");
            sceneData.GpuDebugLightTileMicroseconds =
                timings.GetGpuMicrosecondsOrZero("DebugOverlayPass");
            sceneData.GpuDebugOverlayMicroseconds =
                sceneData.GpuDebugDdgiProbeMicroseconds +
                sceneData.GpuDebugLightTileMicroseconds;
            sceneData.GpuFogMicroseconds = timings.GetGpuMicrosecondsOrZero("FogPass");
            sceneData.GpuVolumetricFogNoiseMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.FroxelNoise");
            sceneData.GpuVolumetricFogSourceCullMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.SourceCull");
            sceneData.GpuVolumetricFogMediumMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Medium");
            sceneData.GpuVolumetricFogTransmittanceMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Transmittance");
            sceneData.GpuVolumetricFogDdgiBounceMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.DdgiBounce");
            sceneData.GpuVolumetricFogLightingCacheMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.DirectLightingCache") +
                timings.GetGpuMicrosecondsOrZero("Fog.IndirectLightingCache");
            sceneData.GpuVolumetricFogMultipleScatteringMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.MultipleScattering.0") +
                timings.GetGpuMicrosecondsOrZero("Fog.MultipleScattering.1");
            sceneData.GpuVolumetricFogTemporalMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Temporal");
            sceneData.GpuVolumetricFogIntegrateMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Integrate");
            sceneData.GpuVolumetricFogResolveMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Resolve");
            sceneData.GpuVolumetricFogCompositeMicroseconds =
                timings.GetGpuMicrosecondsOrZero("Fog.Composite");
            sceneData.GpuAutoExposureMicroseconds = timings.GetGpuMicrosecondsOrZero("AutoExposurePass");
            sceneData.GpuCompositeMicroseconds = timings.GetGpuMicrosecondsOrZero("ToneMapCompositePass");
            sceneData.GpuAntiAliasingMicroseconds = timings.GetGpuMicrosecondsOrZero("AntiAliasingPass");

            long bloom = timings.GetGpuMicrosecondsOrZero("BloomPass");
            if (bloom > 0)
            {
                sceneData.GpuBloomExtractMicroseconds = bloom;
                sceneData.GpuBloomDownsampleMicroseconds = 0;
                sceneData.GpuBloomUpsampleMicroseconds = 0;
            }
        }

        private void PrepareThickTransmissionFrame(
            SceneRenderingData sceneData)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            RaySceneRequirement requirement =
                RaySceneRequirement.ForThickTransmission(
                    Settings.Transparency);
            bool raySceneReady = !requirement.Enabled ||
                                 sceneData.RaySceneReadiness.IsReady(
                                     RaySceneConsumer.ThickTransmission,
                                     requirement.RequiredCategories);
            bool rayPipelineAvailable =
                _meshPipeline?.RayTransparentPipelinesAdmitted == true;
            bool rayPipelineRequiredNow =
                Settings.Transparency.Enabled &&
                Settings.Transparency.ThickTransmissionMode ==
                    ThickTransmissionMode.RayQuery &&
                HasThickTransmissionDraws(sceneData);
            if (rayPipelineAvailable && rayPipelineRequiredNow)
            {
                rayPipelineAvailable =
                    _meshPipeline!.TryEnsureRayTransparentPipelines();
            }
            ThickTransmissionModeResolution resolution =
                ThickTransmissionModeResolver.Resolve(
                    Settings.Transparency,
                    new ThickTransmissionModeCapabilities(
                        _context.RayQuerySupported,
                        _accelerationStructureManager?.Supported == true,
                        raySceneReady,
                        rayPipelineAvailable));
            sceneData.RequestedThickTransmissionMode = resolution.Requested;
            sceneData.EffectiveThickTransmissionMode = resolution.Effective;
            sceneData.ThickTransmissionFallbackReason = resolution.Reason;
            sceneData.ThickTransmissionFallbackDetail = resolution.Detail;
            sceneData.ThickTransmissionDispersionEnabled =
                resolution.Effective != ThickTransmissionMode.Off &&
                Settings.Transparency.DispersionMode == DispersionMode.RgbTriplet;
        }

        internal static bool HasThickTransmissionDraws(
            SceneRenderingData sceneData)
        {
            foreach (TransparentMaterialRun run in
                     sceneData.TransparentMaterialRuns)
            {
                if (run.Classification.MaterialClass ==
                    TransparentMaterialClass.ThickTransmission)
                {
                    return true;
                }
            }

            return false;
        }

        private void PrepareReflectionProbes(Scene scene, SceneRenderingData sceneData)
        {
            if (_reflectionProbeManager == null)
                return;

            _reflectionProbeCompletionValues.SetFrameSerial(_ddgiFrameSerial);
            _reflectionProbeManager.BeginFrameResourceRetirement(
                _ddgiFrameSerial,
                _completedGraphicsFrameFenceValue);
            _reflectionProbeManager.PollCaptureCompletions(_ddgiFrameSerial);
            LightingVersionSnapshot lightingVersions = LightingVersions;
            _reflectionProbeManager.UpdateCaptureVersions(lightingVersions);
            _reflectionProbeManager.Upload(
                scene.ReflectionProbes,
                _stagingRing,
                _currentCommandBuffer,
                scene.ReflectionProbeRevision);
            // Upload may allocate or recreate the cubemap array. Register only when the
            // manager marks its descriptor dirty; this keeps the global environment bound as
            // the explicit fallback until a local array is available.
            _reflectionProbeManager.Register(_bindlessHeap);

            ReflectionSettings settings = Settings.Reflections;
            RaySceneRequirement reflectionRequirement =
                RaySceneRequirement.ForReflections(settings);
            bool sparseLobePayloadRequested =
                Settings.IsPerformanceOptimizationEnabled(
                    PerformanceOptimizationFeature.SparseHybridLobePayload);
            bool sparseLobePayloadAvailable =
                !sparseLobePayloadRequested ||
                _forwardPlusPass?.SparseHybridLobePayloadAvailable == true;
            bool receiverPayloadAvailable =
                _renderTargets?.HybridReflectionReceiverPayload is { } receiver &&
                receiver.Extent.Width == sceneData.ScreenWidth &&
                receiver.Extent.Height == sceneData.ScreenHeight &&
                _meshPipeline?.HybridReflectionAttachmentEnabled == true &&
                _hybridReflectionRuntime?.ScreenPipelinesAvailable == true &&
                sparseLobePayloadAvailable;
            bool raySceneReady = !reflectionRequirement.Enabled ||
                                 sceneData.RaySceneReadiness.IsReady(
                                     RaySceneConsumer.Reflection,
                                     reflectionRequirement.RequiredCategories);
            bool lobeExtensionAvailable = sparseLobePayloadRequested
                ? sparseLobePayloadAvailable
                : _renderTargets?.HybridReflectionRawMetadata is
                        { } lobeTarget &&
                    lobeTarget.Format == ForwardHybridReflectionReceiverContract
                        .LobeExtensionFormat &&
                    lobeTarget.Extent.Width == sceneData.ScreenWidth &&
                    lobeTarget.Extent.Height == sceneData.ScreenHeight;
            bool compactHistoryAvailable =
                _renderTargets?.HybridReflectionHistoryMetadata0?.Format ==
                    RenderTargetManager.HybridReflectionHistoryMetadataFormat &&
                _renderTargets.HybridReflectionHistoryMetadata1?.Format ==
                    RenderTargetManager.HybridReflectionHistoryMetadataFormat &&
                _renderTargets.HybridReflectionMoments0?.Format ==
                    RenderTargetManager.HybridReflectionMomentsFormat &&
                _renderTargets.HybridReflectionMoments1?.Format ==
                    RenderTargetManager.HybridReflectionMomentsFormat;
            ReflectionImplementationResolution implementationResolution =
                ReflectionImplementationResolver.Resolve(
                    settings,
                    new ReflectionImplementationCapabilities(
                        _hybridReflectionRuntime?.ScreenPipelinesAvailable ==
                            true,
                        lobeExtensionAvailable,
                        compactHistoryAvailable));
            ReflectionModeResolution reflectionResolution =
                ReflectionModeResolver.Resolve(
                    settings,
                    new ReflectionModeCapabilities(
                        receiverPayloadAvailable,
                        sceneData.HiZMipCount != 0u &&
                        _hizDepthPyramid != null,
                        _context.RayQuerySupported &&
                        _hybridReflectionRuntime?.RayPipelineAvailable == true,
                        _accelerationStructureManager?.Supported == true,
                        raySceneReady));
            if (!string.IsNullOrWhiteSpace(
                    _hybridReflectionRuntime?.FailureDetail) &&
                reflectionResolution.Reason is
                    ReflectionFallbackReason.ReceiverPayloadUnavailable or
                    ReflectionFallbackReason.RayQueryUnsupported)
            {
                reflectionResolution = reflectionResolution with
                {
                    Detail = _hybridReflectionRuntime.FailureDetail
                };
            }

            sceneData.ReflectionsEnabled =
                reflectionResolution.Effective != ReflectionMode.Disabled;
            sceneData.RequestedReflectionMode = reflectionResolution.Requested;
            sceneData.EffectiveReflectionMode = reflectionResolution.Effective;
            sceneData.RequestedReflectionImplementation =
                implementationResolution.Requested;
            sceneData.EffectiveReflectionImplementation =
                implementationResolution.Effective;
            sceneData.ReflectionImplementationFallbackReason =
                implementationResolution.Reason;
            sceneData.ReflectionImplementationFallbackDetail =
                implementationResolution.Detail;
            sceneData.ReflectionMode = reflectionResolution.Effective;
            sceneData.ReflectionFallbackReason = reflectionResolution.Reason;
            sceneData.ReflectionFallbackDetail = reflectionResolution.Detail;
            sceneData.ReflectionDebugView = settings.DebugView;
            sceneData.ReflectionProbeCount = _reflectionProbeManager.ActiveProbeCount;
            sceneData.ReflectionProbeCapacity = _reflectionProbeManager.ProbeCapacity;
            sceneData.MaxReflectionProbesPerPixel = settings.MaxProbesPerPixel;
            sceneData.ReflectionProbeResolution = _reflectionProbeManager.ProbeResolution;
            sceneData.ReflectionProbeMipCount = _reflectionProbeManager.ProbeMipCount;
            sceneData.ReflectionProbeEstimatedBytes = _reflectionProbeManager.EstimatedBytes;
            sceneData.ReflectionProbeContentRevision =
                (((ulong)_reflectionProbeManager.CubemapArrayResourceGeneration << 32) |
                 scene.ReflectionProbeRevision) ^
                (_reflectionProbeManager.CapturesCompletedTotal *
                 0x9e3779b97f4a7c15UL);
            sceneData.ReflectionEnvironmentGeneration =
                _environmentManager?.PublishedSpecularEnvironmentGeneration ?? 0u;
            sceneData.HybridReflectionEstimatedBytes =
                (_renderTargets?.HybridReflectionRenderTargetBytes ?? 0UL) +
                (_hybridReflectionRuntime?.BufferBytes ?? 0UL) +
                (_forwardPlusPass?.SparseHybridLobePayloadTotalBytes ?? 0UL);
            UpdateReflectionProbeTelemetry(sceneData);
            sceneData.CpuReflectionProbeUploadMicroseconds = _reflectionProbeManager.LastUploadMicroseconds;
        }

        private void UpdateReflectionProbeTelemetry(SceneRenderingData sceneData)
        {
            if (_reflectionProbeManager == null)
                return;

            ApplyReflectionProbeTelemetry(
                sceneData,
                _reflectionProbeManager.CurrentCaptureLifecycle,
                _reflectionProbeManager.CompletedCaptureLifecycle,
                _reflectionProbeManager.CaptureGpuBudget);
        }

        internal static void ApplyReflectionProbeTelemetry(
            SceneRenderingData sceneData,
            in ReflectionProbeLifecycleFrameSnapshot current,
            in ReflectionProbeLifecycleFrameSnapshot completed,
            in ReflectionProbeGpuBudgetSnapshot budget)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            sceneData.ReflectionProbeCurrentLifecycle = current;
            sceneData.ReflectionProbeCompletedLifecycle = completed;
            sceneData.ReflectionProbeCaptureBudget = budget;

            ReflectionProbeLifecycleSnapshot lifecycle = current.Lifecycle;
            sceneData.ReflectionProbeCapturesQueued = current.Valid
                ? checked(
                    lifecycle.QueuedCount +
                    lifecycle.ActiveCount +
                    lifecycle.AwaitingGpuCompletionCount)
                : 0;
            sceneData.ReflectionProbeCapturesCompleted = current.Valid
                ? lifecycle.CapturesCompletedThisFrame
                : 0;
            sceneData.ReflectionProbeCapturesCompletedTotal = current.Valid
                ? lifecycle.CapturesCompletedTotal
                : 0UL;
            sceneData.ReflectionProbePublishedCount = current.Valid
                ? lifecycle.PublishedCount
                : 0;
        }

        /// <summary>
        /// Opens reflection capture accounting only after swapchain acquisition succeeds. The
        /// completed timestamp snapshot and submitted workload are consumed from the exact same
        /// renderer frame slot before completion polling can emit this frame's lifecycle pulses.
        /// </summary>
        private void BeginReflectionProbeCaptureFrame(bool gpuTimingRecorded)
        {
            if (_reflectionProbeManager == null)
                return;

            _reflectionProbeManager.BeginCaptureFrame(
                _currentFrame,
                _ddgiFrameSerial,
                gpuTimingRecorded);
            FrameTimingSnapshot completedReflectionTimings =
                _gpuTimestamps.LastCompletedSnapshot;
            _reflectionProbeManager.UpdateCaptureGpuTimingHistory(
                _currentFrame,
                completedReflectionTimings.GetGpuMicrosecondsOrZero("ReflectionProbeCapturePass"),
                completedReflectionTimings.GetGpuMicrosecondsOrZero("ReflectionProbePrefilterPass"),
                completedReflectionTimings.GetGpuMicrosecondsOrZero("ReflectionProbePublishPass"));
        }

        private void RecordReflectionProbeWork(SceneRenderingData sceneData)
        {
            if (_reflectionProbeManager == null)
                return;

            if (_reflectionProbeCapturePass?.ShouldExecute(_currentFrame, sceneData) == true)
            {
                long start = Stopwatch.GetTimestamp();
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "ReflectionProbeCapturePass");
                try
                {
                    _reflectionProbeCapturePass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                    sceneData.CpuReflectionProbeCaptureRecordMicroseconds += ElapsedMicroseconds(start);
                }
            }

            if (_reflectionProbePrefilterPass?.ShouldExecute(_currentFrame, sceneData) == true)
            {
                long start = Stopwatch.GetTimestamp();
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "ReflectionProbePrefilterPass");
                try
                {
                    _reflectionProbePrefilterPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                    sceneData.CpuReflectionProbePrefilterRecordMicroseconds += ElapsedMicroseconds(start);
                }
            }

            if (_reflectionProbePublishPass?.ShouldExecute(_currentFrame, sceneData) == true)
            {
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "ReflectionProbePublishPass");
                try
                {
                    _reflectionProbePublishPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }
            }
        }

        private bool IncludeAsyncComputePass(string passName) =>
            _asyncComputePassNameScratch.Contains(passName);

        private void PrepareSimpleDdgiFrame(
            Scene scene,
            ICamera camera,
            SceneRenderingData sceneData,
            LightFrameSnapshot lightSnapshot)
        {
            SimpleDdgiFrameCoordinator coordinator = _simpleDdgiFrames ??
                                                     throw new InvalidOperationException(
                                                         "Simple DDGI frame coordinator is unavailable.");
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            int lightCount = Math.Min(lightSnapshot.Count, lights.Length);
            bool[] atmosphereOwnedBuffer =
                ArrayPool<bool>.Shared.Rent(Math.Max(1, lightCount));
            try
            {
                Span<bool> atmosphereOwnedLights =
                    atmosphereOwnedBuffer.AsSpan(0, lightCount);
                for (int lightIndex = 0;
                     lightIndex < lightCount;
                     lightIndex++)
                {
                    atmosphereOwnedLights[lightIndex] =
                        _environmentManager?.IsManagedAtmosphereLight(
                            lightIndex,
                            _lightManager) == true;
                }

                SimpleDdgiReceiverFeedbackViewport viewport =
                    _renderTargets is { } renderTargets
                        ? new SimpleDdgiReceiverFeedbackViewport(
                            Available: true,
                            renderTargets.SceneColor.Extent,
                            checked((uint)Math.Max(
                                0,
                                renderTargets.ResizeCount) + 1u))
                        : SimpleDdgiReceiverFeedbackViewport.Unavailable;
                bool reflectionConsumersAvailable =
                    _reflectionProbeManager is not null &&
                    Settings.Reflections.Enabled &&
                    Settings.Reflections.Mode != ReflectionMode.Disabled &&
                    sceneData.ReflectionProbeCount > 0;
                SimpleDdgiCoreFrameResult result =
                    coordinator.PrepareFrame(
                        new SimpleDdgiCoreFrameRequest(
                            new SimpleDdgiFrameSceneInput(
                                scene,
                                lightSnapshot,
                                _ddgiFoliageProxyFrame,
                                sceneData.GpuParticleDeltaSeconds,
                                new ReadOnlyMemory<bool>(
                                    atmosphereOwnedBuffer,
                                    0,
                                    lightCount)),
                            new SimpleDdgiFrameSettingsInput(Settings),
                            new SimpleDdgiFrameViewInput(
                                camera.Position,
                                camera.Forward,
                                sceneData.ViewProjectionMatrix,
                                viewport),
                            new SimpleDdgiFrameIdentity(
                                _currentFrame,
                                sceneData.DdgiFrameSerial,
                                sceneData.SceneContentRevision,
                                sceneData.GiTransportMaterialRevision,
                                sceneData.CaptureCameraCutSerial),
                            new SimpleDdgiFrameCapabilities(
                                _context.RayQuerySupported,
                                _accelerationStructureManager?.Active == true,
                                _environmentManager?.GiLightingSignature ??
                                0UL,
                                _environmentManager is
                                    { UsesAnalyticSky: true },
                                _performanceCaptureMetadataProvider
                                    .BuildIdentity.ShaderBundleHash,
                                reflectionConsumersAvailable,
                                _accelerationStructureManager?
                                    .RaySceneContentEpoch ?? 1UL),
                            new SimpleDdgiFrameAdmissionInput(
                                CaptureAdvancedGiRuntimeQualificationContext(
                                    supported: false)),
                            _currentCommandBuffer));
                DdgiFrameDataProjector.Project(
                    sceneData,
                    new DdgiFrameProjectionInput(
                        result,
                        _simpleDdgiVolumeManager,
                        Settings.GlobalIllumination,
                        result.Emissive.RefinementDiagnostics,
                        _completedDdgiInvestigationCounters.NearVisibility,
                        _forwardPlusPass?
                            .SimpleDdgiReceiverCacheBufferBytes ?? 0UL,
                        _forwardPlusPass?
                            .SimpleDdgiReceiverGatherBufferTotalBytes ?? 0UL,
                        _forwardPlusPass?
                            .SimpleDdgiReceiverSurfaceSidecarTotalBytes ?? 0UL,
                        _simpleDdgiFrameEvidence));
                ApplyReflectionRecaptureIntent(
                    sceneData,
                    result.ReflectionIntent);
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(atmosphereOwnedBuffer);
            }
        }

        private void ApplyReflectionRecaptureIntent(
            SceneRenderingData sceneData,
            in SimpleDdgiReflectionRecaptureIntent intent)
        {
            if (intent.RequestRecaptureAll)
                _reflectionProbeManager?.RequestRecaptureAll(intent.Reason);
            if (intent.UpdateTelemetry)
                UpdateReflectionProbeTelemetry(sceneData);
        }


        /// <summary>
        /// Captures advanced-GI admission and physical ownership only after
        /// AS/DDGI reconciliation for the frame has completed. This method is
        /// intentionally called even when Simple-DDGI is inactive: C1 may
        /// still have retirement work in flight, and suppressing it would
        /// under-report live Vulkan residency.
        /// </summary>
        private void PopulateAdvancedGiFrameDiagnostics(
            SceneRenderingData sceneData)
        {
            GiRoadmapExperimentDiagnostics roadmapExperiments =
                CreateGiRoadmapExperimentDiagnostics(sceneData);

            SimpleDdgiAdvancedExperimentMemoryPlan advanced =
                SimpleDdgiAdvancedExperimentMemoryPlan.Empty;

            SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics feedbackRuntime =
                _simpleDdgiReceiverFeedback?.Diagnostics ??
                SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;
            SimpleDdgiAdvancedExperimentMemoryPlan feedbackMemory =
                _simpleDdgiReceiverFeedback?.Plan.Memory ??
                SimpleDdgiAdvancedExperimentMemoryPlan.Empty;
            if (_simpleDdgiReceiverFeedback?.Plan.UsesExactCompacted == true &&
                (!feedbackRuntime.Resource.IsEffectivelyEnabled ||
                 feedbackRuntime.Resource.AllocatedBytes !=
                 feedbackMemory.AllocatedBytes))
            {
                feedbackMemory = SimpleDdgiAdvancedExperimentMemoryPlan
                    .CreateReceiverFeedbackRejected(
                        GiExperimentFallbackReason.ResourceIncomplete);
            }

            advanced = SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                advanced,
                feedbackMemory);

            OpacityMicromapGpuRuntimeSnapshot opacityRuntime =
                _accelerationStructureManager?.OpacityMicromapGpuRuntimeSnapshot ??
                OpacityMicromapGpuRuntimeSnapshot.Disabled;
            advanced = SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                advanced,
                opacityRuntime.Memory.NormalizeForPersistence());

            SimpleDdgiGuidingFrameConfiguration guidingConfiguration =
                _simpleDdgiFrames?.GuidingFrameConfiguration ??
                SimpleDdgiGuidingFrameConfiguration.Disabled;
            SimpleDdgiAdvancedExperimentMemoryPlan guidingMemory;
            if (guidingConfiguration.IsEnabled &&
                _simpleDdgiGuidingFrameCoordinator is { } guidingCoordinator &&
                guidingCoordinator.TryGetGraphResourceSnapshot(
                    out SimpleDdgiGuidingGraphResourceSnapshot guidingResources))
            {
                SimpleDdgiGuidingLayout guidingLayout =
                    guidingConfiguration.RuntimeRequest.Layout;
                ulong allocatedHistoryBytes = checked(
                    guidingResources.Distributions.BankBytes * 2UL +
                    guidingLayout.ValidationReferenceBankBytes +
                    guidingResources.DirectionPayloadBytes);
                guidingMemory = SimpleDdgiAdvancedExperimentMemoryPlan
                    .CreateDirectionalGuiding(
                        guidingLayout,
                        allocatedHistoryBytes,
                        guidingResources.WorkspaceBytes);
            }
            else
            {
                bool guidingRequested = Settings.GlobalIllumination
                                            .SimpleDdgiDirectionalGuidingMode !=
                                        SimpleDdgiDirectionalGuidingMode.Off;
                guidingMemory = guidingRequested
                    ? SimpleDdgiAdvancedExperimentMemoryPlan
                        .CreateDirectionalGuidingRejected(
                            GiExperimentFallbackReason.ResourceIncomplete)
                    : SimpleDdgiAdvancedExperimentMemoryPlan.Empty;
            }

            advanced = SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                advanced,
                guidingMemory);

            GiCausticCoordinatorSnapshot causticSnapshot =
                _giCaustic.CaptureSnapshot();
            SimpleDdgiAdvancedExperimentMemoryPlan causticMemory =
                causticSnapshot.Plan.Memory;
            GiCausticVulkanRuntimeDiagnostics causticRuntime =
                causticSnapshot.Runtime;
            if (causticSnapshot.Plan.Active &&
                (!causticSnapshot.RuntimeConfigured ||
                 !causticRuntime.Resource.IsEffectivelyEnabled ||
                 causticRuntime.Resource.AllocatedBytes !=
                 causticMemory.AllocatedBytes))
            {
                causticMemory = SimpleDdgiAdvancedExperimentMemoryPlan
                    .CreateCausticRejected(
                        GiExperimentFallbackReason.ResourceIncomplete);
            }

            advanced = SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                advanced,
                causticMemory);

            SimpleDdgiAdvancedExperimentMemoryPlan nearFieldMemory =
                _nearFieldResidual.Plan.Memory;
            NearFieldResidualGraphResourceSnapshot nearFieldResources =
                _nearFieldResidual.CaptureGraphResources();
            if (_nearFieldResidual.Plan.Active &&
                (!_nearFieldResidual.IsGenerationExecutable ||
                 nearFieldResources.Runtime is not { } nearFieldRuntime ||
                 nearFieldRuntime.ActualAllocationBytes !=
                 nearFieldMemory.AllocatedBytes))
            {
                nearFieldMemory = SimpleDdgiAdvancedExperimentMemoryPlan
                    .CreateNearFieldResidualRejected(
                        GiExperimentFallbackReason.ResourceIncomplete);
            }

            advanced = SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                advanced,
                nearFieldMemory);

            SimpleDdgiContentMemoryPlan contentMemory =
                SimpleDdgiContentMemoryPlan.Compile(
                    Settings.GlobalIllumination,
                    sceneData.LocalLightCount,
                    _simpleDdgiVolumeManager?.PhysicalProbeCapacity ?? 0,
                    dynamicBlasRequiredBytes:
                    _accelerationStructureManager?
                        .DynamicBottomLevelAccelerationStructureBytes ?? 0UL,
                    dynamicBlasScratchRequiredBytes:
                    sceneData.AccelerationStructureDynamicScratchBytes,
                    dynamicBlasRetiredRequiredBytes:
                    _accelerationStructureManager?
                        .RetiredDynamicBottomLevelAccelerationStructureBytes ??
                    0UL,
                    foliageProxyTriangleCount:
                    _ddgiFoliageProxyFrame.TriangleCount,
                    foliageProxyPatchCount:
                    _ddgiFoliageProxyFrame.PatchCount,
                    frameSlotCount: RenderingConstants.FramesInFlight,
                    advancedExperimentMemory: advanced);

            roadmapExperiments = roadmapExperiments with
            {
                ReceiverFeedbackRuntime =
                CreateReceiverFeedbackDiagnostics(
                    feedbackMemory,
                    roadmapExperiments.Modes
                        .ReceiverFeedback),
                DirectionalGuidingRuntime =
                CreateDirectionalGuidingDiagnostics(
                    advanced,
                    roadmapExperiments.Modes
                        .DirectionalGuiding),
                CausticRuntime = CreateGiCausticDiagnostics(
                    causticMemory,
                    roadmapExperiments.Modes.Caustic)
            };
            DdgiFrameDataProjector.ProjectAdvancedFrame(
                sceneData,
                new AdvancedGiFrameProjectionInput(
                    roadmapExperiments,
                    contentMemory));
        }

        private SimpleDdgiReceiverFeedbackDiagnostics
            CreateReceiverFeedbackDiagnostics(
                in SimpleDdgiAdvancedExperimentMemoryPlan memory,
                in GiExperimentModeState<SimpleDdgiReceiverFeedbackMode> mode)
        {
            SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics runtime =
                _simpleDdgiReceiverFeedback?.Diagnostics ??
                SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;
            var feedbackMemory = new SimpleDdgiReceiverFeedbackMemoryTelemetry(
                memory.ReceiverFeedbackRecordBanks,
                memory.ReceiverFeedbackSortScratch,
                memory.ReceiverFeedbackProbeSummaries);

            FrameTimingSnapshot completed = _gpuTimestamps.LastCompletedSnapshot;
            SimpleDdgiReceiverFeedbackTimedStage availableStages =
                SimpleDdgiReceiverFeedbackTimedStage.None;
            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiReceiverFeedbackGpuTimingNames.Reset))
            {
                availableStages |=
                    SimpleDdgiReceiverFeedbackTimedStage.Reset;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiReceiverFeedbackGpuTimingNames.Capture))
            {
                availableStages |=
                    SimpleDdgiReceiverFeedbackTimedStage.Capture;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiReceiverFeedbackGpuTimingNames.RawRadix))
            {
                availableStages |=
                    SimpleDdgiReceiverFeedbackTimedStage.RawRadix;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiReceiverFeedbackGpuTimingNames
                        .PartialBuildAndRadix))
            {
                availableStages |= SimpleDdgiReceiverFeedbackTimedStage
                    .PartialBuildAndRadix;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiReceiverFeedbackGpuTimingNames
                        .ReduceAndFinalize))
            {
                availableStages |= SimpleDdgiReceiverFeedbackTimedStage
                    .ReduceAndFinalize;
            }

            var timings = new SimpleDdgiReceiverFeedbackStageTimings(
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiReceiverFeedbackGpuTimingNames.Reset),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiReceiverFeedbackGpuTimingNames.Capture),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiReceiverFeedbackGpuTimingNames.RawRadix),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiReceiverFeedbackGpuTimingNames
                        .PartialBuildAndRadix),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiReceiverFeedbackGpuTimingNames
                        .ReduceAndFinalize),
                availableStages);

            bool exactRequested = mode.RequestedMode is
                SimpleDdgiReceiverFeedbackMode.ExactCompacted or
                SimpleDdgiReceiverFeedbackMode.AutoQualified;
            bool resourcesComplete = runtime.Resource.IsEffectivelyEnabled &&
                                     feedbackMemory.AllocatedBytes != 0UL &&
                                     runtime.Resource.AllocatedBytes == feedbackMemory.AllocatedBytes;
            bool publicationReadable = runtime.Publication.IsValid &&
                                       runtime.Resource.PublishedBankIndex is 0 or 1 &&
                                       runtime.Resource.PublishedGeneration ==
                                       runtime.Publication.FeedbackGeneration;

            SimpleDdgiReceiverFeedbackTelemetryState state;
            string reason;
            if (!exactRequested)
            {
                state = SimpleDdgiReceiverFeedbackTelemetryState.Disabled;
                reason = mode.RequestedMode ==
                         SimpleDdgiReceiverFeedbackMode.LegacyPackedReference
                    ? "receiver-feedback-legacy-reference-has-no-B1-GPU-publication"
                    : "receiver-feedback-disabled";
            }
            else if (!resourcesComplete)
            {
                state = runtime.CapabilityReason is
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason
                        .CaptureSubmissionRejected or
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason
                        .HeaderReadbackRejected or
                    SimpleDdgiReceiverFeedbackGpuCapabilityReason
                        .SchedulerBindingRejected
                    ? SimpleDdgiReceiverFeedbackTelemetryState.Faulted
                    : SimpleDdgiReceiverFeedbackTelemetryState
                        .ResourceIncomplete;
                reason = string.IsNullOrWhiteSpace(mode.FallbackDetail)
                    ? runtime.Detail
                    : mode.FallbackDetail;
            }
            else if (publicationReadable)
            {
                state = SimpleDdgiReceiverFeedbackTelemetryState.Readable;
                reason =
                    "receiver-feedback-fence-validated-publication-available";
            }
            else if (runtime.HeaderReadbackPending ||
                     runtime.Resource.State ==
                     SimpleDdgiReceiverFeedbackGpuResourceState.Capturing)
            {
                state = SimpleDdgiReceiverFeedbackTelemetryState
                    .PendingGpuPublication;
                reason = runtime.Detail;
            }
            else if (runtime.CapabilityReason !=
                     SimpleDdgiReceiverFeedbackGpuCapabilityReason.None)
            {
                state = SimpleDdgiReceiverFeedbackTelemetryState.Faulted;
                reason = runtime.Detail;
            }
            else
            {
                state = SimpleDdgiReceiverFeedbackTelemetryState
                    .PendingGpuPublication;
                reason = runtime.Detail;
            }

            return new SimpleDdgiReceiverFeedbackDiagnostics
            {
                State = state,
                Runtime = runtime,
                Publication = runtime.Publication,
                Timings = timings,
                Memory = feedbackMemory,
                Reason = reason
            }.NormalizeForPersistence();
        }

        private SimpleDdgiDirectionalGuidingDiagnostics
            CreateDirectionalGuidingDiagnostics(
                in SimpleDdgiAdvancedExperimentMemoryPlan memory,
                in GiExperimentModeState<SimpleDdgiDirectionalGuidingMode> mode)
        {
            bool requested = mode.RequestedMode !=
                             SimpleDdgiDirectionalGuidingMode.Off;
            SimpleDdgiGuidingGpuRuntimeDiagnostics runtime =
                _simpleDdgiGuidingRuntime?.Diagnostics ??
                SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;
            SimpleDdgiGuidingFrameCoordinatorDiagnostics frame =
                _simpleDdgiGuidingFrameCoordinator?.Diagnostics ??
                SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled;
            var guidingMemory = new SimpleDdgiGuidingMemoryTelemetry(
                memory.DirectionalGuidingHistoryBanks,
                memory.DirectionalGuidingBuildScratch);

            FrameTimingSnapshot completed = _gpuTimestamps.LastCompletedSnapshot;
            SimpleDdgiGuidingTimedStage availableStages =
                SimpleDdgiGuidingTimedStage.None;
            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiGuidingGpuPassNames.Sample))
            {
                availableStages |= SimpleDdgiGuidingTimedStage.Sample;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiGuidingGpuPassNames.Train))
            {
                availableStages |= SimpleDdgiGuidingTimedStage.Train;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiGuidingGpuPassNames.Build))
            {
                availableStages |= SimpleDdgiGuidingTimedStage.Build;
            }

            if (HasCompletedGpuTiming(
                    completed,
                    SimpleDdgiGuidingGpuPassNames.Validate))
            {
                availableStages |= SimpleDdgiGuidingTimedStage.Validate;
            }

            var timings = new SimpleDdgiGuidingStageTimings(
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiGuidingGpuPassNames.Sample),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiGuidingGpuPassNames.Train),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiGuidingGpuPassNames.Build),
                completed.GetGpuMicrosecondsOrZero(
                    SimpleDdgiGuidingGpuPassNames.Validate),
                availableStages);

            bool resourcesComplete = runtime.Resource.HasResources &&
                                     frame.Configured && guidingMemory.AllocatedBytes > 0UL;
            SimpleDdgiGuidingTelemetryState state;
            string reason;
            if (!requested)
            {
                state = SimpleDdgiGuidingTelemetryState.Disabled;
                reason = "directional-guiding-disabled";
            }
            else if (!resourcesComplete)
            {
                state = SimpleDdgiGuidingTelemetryState.ResourceIncomplete;
                reason = string.IsNullOrWhiteSpace(mode.FallbackDetail)
                    ? runtime.Detail
                    : mode.FallbackDetail;
            }
            else if (frame.SampleReadbackValid &&
                     !frame.SampleValidationCounters.AreZero)
            {
                state = SimpleDdgiGuidingTelemetryState.Faulted;
                reason = "directional-guiding-sample-validation-reported-errors";
            }
            else if (frame.SampleReadbackValid)
            {
                state = SimpleDdgiGuidingTelemetryState.Available;
                reason = "directional-guiding-fence-complete-sample-available";
            }
            else if (IsFatalSimpleDdgiGuidingRuntimeCapability(
                         runtime.CapabilityReason))
            {
                state = SimpleDdgiGuidingTelemetryState.Faulted;
                reason = string.IsNullOrWhiteSpace(runtime.LastCompletionDetail)
                    ? runtime.Detail
                    : runtime.LastCompletionDetail;
            }
            else
            {
                state = SimpleDdgiGuidingTelemetryState.PendingGpuReadback;
                reason = frame.State;
            }

            return new SimpleDdgiDirectionalGuidingDiagnostics
            {
                State = state,
                Runtime = runtime,
                Frame = frame,
                Timings = timings,
                Memory = guidingMemory,
                Reason = reason
            }.NormalizeForPersistence();
        }

        internal static bool IsFatalSimpleDdgiGuidingRuntimeCapability(
            SimpleDdgiGuidingGpuCapabilityReason reason) => reason is
            SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected or
            SimpleDdgiGuidingGpuCapabilityReason.HeaderReadbackRejected or
            SimpleDdgiGuidingGpuCapabilityReason.SampleRecordingRejected or
            SimpleDdgiGuidingGpuCapabilityReason.SampleReadbackRejected or
            SimpleDdgiGuidingGpuCapabilityReason.Disposed;

        private GiCausticDiagnostics CreateGiCausticDiagnostics(
            in SimpleDdgiAdvancedExperimentMemoryPlan memory,
            in GiExperimentModeState<GiCausticMode> mode)
        {
            bool requested = mode.RequestedMode != GiCausticMode.Off;
            GiCausticCoordinatorSnapshot snapshot =
                _giCaustic.CaptureSnapshot();
            GiCausticVulkanRuntimeDiagnostics runtime = snapshot.Runtime;
            GiCausticGpuMemoryRequirements causticMemory =
                new(
                    memory.CausticPhotonRecords,
                    memory.CausticCellTableAndSortScratch,
                    memory.CausticHistory,
                    snapshot.Plan.GpuLayout.TaskQueueBytes,
                    snapshot.Plan.GpuLayout.CandidateStagingBytes,
                    snapshot.Plan.GpuLayout.PublishedPhotonBytes,
                    snapshot.Plan.GpuLayout.PublicationHeaderBytes);

            FrameTimingSnapshot completed = _gpuTimestamps.LastCompletedSnapshot;
            GiCausticTimedStage availableStages = GiCausticTimedStage.None;
            if (HasCompletedGpuTiming(completed, GiCausticGpuPassNames.Task))
                availableStages |= GiCausticTimedStage.Task;
            if (HasCompletedGpuTiming(completed, GiCausticGpuPassNames.Trace))
                availableStages |= GiCausticTimedStage.Trace;
            if (HasCompletedGpuTiming(completed, GiCausticGpuPassNames.CacheBuild))
                availableStages |= GiCausticTimedStage.CacheBuild;
            if (HasCompletedGpuTiming(completed, GiCausticGpuPassNames.Resolve))
                availableStages |= GiCausticTimedStage.Resolve;
            if (HasCompletedGpuTiming(completed, GiCausticGpuPassNames.Composite))
                availableStages |= GiCausticTimedStage.Composite;

            var timings = new GiCausticStageTimings(
                completed.GetGpuMicrosecondsOrZero(GiCausticGpuPassNames.Task),
                completed.GetGpuMicrosecondsOrZero(GiCausticGpuPassNames.Trace),
                completed.GetGpuMicrosecondsOrZero(
                    GiCausticGpuPassNames.CacheBuild),
                completed.GetGpuMicrosecondsOrZero(GiCausticGpuPassNames.Resolve),
                completed.GetGpuMicrosecondsOrZero(
                    GiCausticGpuPassNames.Composite),
                availableStages);

            GiCausticTelemetryState state;
            string reason;
            if (!requested)
            {
                state = GiCausticTelemetryState.Disabled;
                reason = "caustic-disabled";
            }
            else if (mode.EffectiveMode == GiCausticMode.PhotonReference)
            {
                state = GiCausticTelemetryState.Disabled;
                reason = "caustic-CPU-reference-has-no-C4-GPU-publication";
            }
            else if (!snapshot.RuntimeConfigured ||
                     !runtime.Resource.IsEffectivelyEnabled ||
                     causticMemory.AllocatedBytes == 0UL)
            {
                state = GiCausticTelemetryState.ResourceIncomplete;
                reason = string.IsNullOrWhiteSpace(runtime.Detail)
                    ? mode.FallbackDetail
                    : runtime.Detail;
            }
            else if (runtime.Resource.HasReadableCache &&
                     runtime.Publication.Available)
            {
                state = GiCausticTelemetryState.Readable;
                reason = "caustic-fence-validated-cache-publication-available";
            }
            else if (runtime.HeaderReadbackPending ||
                     runtime.Resource.State == GiCausticGpuResourceState.Building)
            {
                state = GiCausticTelemetryState.PendingGpuPublication;
                reason = runtime.Detail;
            }
            else if (runtime.CapabilityReason !=
                     GiCausticVulkanRuntimeCapabilityReason.None)
            {
                state = GiCausticTelemetryState.Faulted;
                reason = runtime.Detail;
            }
            else
            {
                state = GiCausticTelemetryState.PendingGpuPublication;
                reason = runtime.Detail;
            }

            return new GiCausticDiagnostics
            {
                State = state,
                Runtime = runtime,
                Publication = runtime.Publication,
                Timings = timings,
                Memory = causticMemory,
                Reason = reason
            }.NormalizeForPersistence();
        }

        private GiExperimentAdmission CreateDirectionalFogExperimentAdmission(
            SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            return SimpleDdgiDirectionalFogExperiment.EvaluateAdmission(
                gi.SimpleDdgiDirectionalFogEnabled,
                new SimpleDdgiDirectionalFogCapabilities(
                    L2IncidentRadianceSidecarAvailable:
                    _simpleDdgiVolumeManager?.DirectionalRadianceMode ==
                    SimpleDdgiDirectionalRadianceMode.L2,
                    FroxelPhaseIntegrationAvailable:
                    sceneData.FogEffectiveTechnique == FogTechnique.Froxel &&
                    sceneData.VolumetricFogDirectionalL2Active,
                    DirectIndirectOwnershipSeparated:
                    sceneData.VolumetricFogEnergyOwnershipSeparated),
                productionQualified:
                Settings.Fog.Volumetric.SingleScatteringQualified &&
                sceneData.FogEffectiveTechnique == FogTechnique.Froxel &&
                sceneData.VolumetricFogOutputReadbackValid &&
                sceneData.VolumetricFogOutputProduced &&
                sceneData.VolumetricFogIndirectNonZeroFroxelCount > 0 &&
                sceneData.VolumetricFogDdgiSupportedFroxelCount > 0,
                allocatedBytes: sceneData.VolumetricFogAllocatedBytes);
        }

        /// <summary>
        /// Builds one fail-closed, generation-aligned Simple-DDGI liveness
        /// sample.  It is intentionally diagnostic-only: no scheduler budget,
        /// residency admission, or receiver fallback is changed from this
        /// method.  In particular, a delayed sparse-page summary is never
        /// combined with a current CPU queue, because that would manufacture a
        /// plausible-looking but invalid progress predicate.
        /// </summary>
        private GiRoadmapExperimentDiagnostics
            CreateGiRoadmapExperimentDiagnostics(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            GiExperimentAdmission directionalFog =
                CreateDirectionalFogExperimentAdmission(sceneData);

            Core.GiHardwareResearchCapabilities hardware =
                _context.GiHardwareResearchCapabilities;
            Core.OpacityMicromapCapabilitySnapshot opacityHardware =
                hardware.OpacityMicromap;
            OpacityMicromapGpuRuntimeSnapshot opacityRuntime =
                _accelerationStructureManager
                    ?.OpacityMicromapGpuRuntimeSnapshot ??
                OpacityMicromapGpuRuntimeSnapshot.Disabled;
            opacityRuntime = opacityRuntime with
            {
                LastGpuBuildMicroseconds = Math.Max(
                    0L,
                    sceneData.GpuOpacityMicromapBuildMicroseconds)
            };
            GiExperimentAdmission opacityMicromap =
                GiOpacityMicromapExperiment.EvaluateAdmission(
                    gi.DdgiOpacityMicromapExperimentEnabled,
                    new GiOpacityMicromapHardwareCapabilities(
                        opacityHardware.ExtensionAvailable,
                        opacityHardware.FeatureAvailable,
                        opacityHardware.HostCommandsAvailable,
                        opacityHardware.MaximumTwoStateSubdivisionLevel,
                        opacityHardware.MaximumFourStateSubdivisionLevel,
                        RuntimeBackendEnabled: opacityRuntime.Enabled),
                    new GiOpacityMicromapQualification(
                        VisibilityParityPassed: false,
                        ThinTransmissionParityPassed: false,
                        BuildCostAmortized: false,
                        TotalGiTimeImproved: false,
                        ResidentBytes: opacityRuntime.AllocatedBytes,
                        MemoryBudgetBytes: 128UL * 1024UL * 1024UL));

            Core.RayTracingInvocationReorderCapabilitySnapshot rayHardware =
                hardware.RayTracingInvocationReorder;
            GiRayTracingBackendSelection raySelection =
                GiRayTracingInvocationReorderExperiment.Select(
                    gi.DdgiRayTracingPipelineExperimentEnabled,
                    new GiRayTracingPipelineHardwareCapabilities(
                        rayHardware.RayTracingPipelineExtensionAvailable,
                        rayHardware.RayTracingPipelineFeatureAvailable,
                        rayHardware.InvocationReorderExtensionAvailable,
                        rayHardware.InvocationReorderFeatureAvailable,
                        rayHardware.EffectiveReorderingHint,
                        rayHardware.MaximumShaderBindingTableRecordIndex,
                        RuntimeBackendEnabled: false),
                    default,
                    default,
                    default);

            GiExperimentAdmission directionalGuiding =
                SimpleDdgiDirectionalGuidingExperiment.EvaluateAdmission(
                    gi.SimpleDdgiDirectionalRayGuidingExperimentEnabled,
                    new SimpleDdgiDirectionalGuidingPrerequisites(
                        SpatialEmissiveSamplingReady: true,
                        CachedRelightingReady: true,
                        VariablePdfDirectionIdentityAvailable: false,
                        MaintenanceSubsetPdfAudited: false,
                        CacheCardinalityAndTailAuditUpdated: false,
                        ReferenceParityPassed: false,
                        QualityPerMillisecondImproved: false));

            GiTaggedCausticCachePlan causticPlan =
                GiTaggedCausticCacheExperiment.CreatePlan(
                    new GiTaggedCausticCacheConfiguration(
                        gi.DdgiTaggedCausticCacheExperimentEnabled,
                        HeroMaterialCount:
                        gi.DdgiTaggedCausticCacheExperimentEnabled ? 1 : 0,
                        PhotonTaskCapacity: 4_096,
                        MaximumWorldCells: 4_096,
                        MaximumPhotonsPerCell: 8,
                        MemoryBudgetBytes: 8UL * 1024UL * 1024UL,
                        ScreenResolveProfile: new(1, 1)),
                    new GiTaggedCausticCacheQualification(
                        SeparateOwnershipImplemented: false,
                        DiffuseTransportFeedDisabled: true,
                        ReferenceParityPassed: false,
                        StabilityProofPassed: false,
                        QualityPerMillisecondImproved: false));

            SimpleDdgiNearFieldResidualPlan residualPlan =
                _nearFieldResidual.Plan;

            // The legacy admission records above remain snapshot-compatible,
            // but the mode state below is authoritative. In particular, it
            // evaluates frozen prerequisite evidence before considering a
            // capability setting or a not-yet-allocated resource complete.
            AdvancedGiPrerequisiteGateResult ommGate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps);
            AdvancedGiPrerequisiteGateResult guidingGate =
                _advancedGiAdmission.EvaluatePrerequisite(
                    AdvancedGiPrerequisiteFeature.DirectionalGuiding);
            AdvancedGiQualificationGateResult ommQualification =
                EvaluateAdvancedGiQualification(
                    AdvancedGiPrerequisiteFeature.OpacityMicromaps,
                    ommGate,
                    opacityRuntime.Supported,
                    gi.DdgiOpacityMicromapQualificationId);
            AdvancedGiQualificationGateResult guidingQualification =
                EvaluateAdvancedGiQualification(
                    AdvancedGiPrerequisiteFeature.DirectionalGuiding,
                    guidingGate,
                    supported: true,
                    gi.SimpleDdgiDirectionalGuidingQualificationId);
            bool residualRuntimeReady =
                _nearFieldResidual.IsGenerationExecutable;
            // Both scheduler backends have an authoritative work source. The
            // GPU-resident path is compacted by ddgi_guiding_prepare.comp and
            // therefore must use the same qualification/runtime diagnostics
            // as the CPU reference path.
            const bool guidingRuntimeSupported = true;
            SimpleDdgiGuidingGpuRuntimeDiagnostics guidingRuntime =
                _simpleDdgiGuidingRuntime?.Diagnostics ??
                SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;
            SimpleDdgiGuidingFrameCoordinatorDiagnostics guidingFrame =
                _simpleDdgiGuidingFrameCoordinator?.Diagnostics ??
                SimpleDdgiGuidingFrameCoordinatorDiagnostics.Disabled;
            bool guidingResourcesReady =
                _simpleDdgiGuidingFrameCoordinator is
                    {
                        IsConfigured: true
                    }
                    configuredGuidingCoordinator &&
                configuredGuidingCoordinator.TryGetGraphResourceSnapshot(out _) &&
                guidingRuntime.Resource.HasResources;
            string guidingDetail = guidingResourcesReady
                ? guidingFrame.State
                : !string.IsNullOrWhiteSpace(
                    _simpleDdgiFrames?.GuidingConfigurationReason)
                    ? _simpleDdgiFrames!.GuidingConfigurationReason
                    : guidingRuntime.Detail;

            GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>
                receiverFeedbackMode = _simpleDdgiReceiverFeedback?.Plan.Mode ??
                                       SimpleDdgiReceiverFeedbackPlan.Disabled().Mode;
            SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics feedbackRuntime =
                _simpleDdgiReceiverFeedback?.Diagnostics ??
                SimpleDdgiReceiverFeedbackGpuRuntimeDiagnostics.Disabled;
            if (receiverFeedbackMode.EffectiveMode is
                    (SimpleDdgiReceiverFeedbackMode.ExactCompacted or
                    SimpleDdgiReceiverFeedbackMode.AutoQualified) &&
                !feedbackRuntime.Resource.IsEffectivelyEnabled)
            {
                receiverFeedbackMode = receiverFeedbackMode with
                {
                    EffectiveMode = SimpleDdgiReceiverFeedbackMode.Off,
                    FallbackReason =
                    GiExperimentFallbackReason.ResourceIncomplete,
                    FallbackDetail = feedbackRuntime.Detail
                };
            }

            GiExperimentModeState<SimpleDdgiNearFieldResidualMode>
                nearFieldMode = _nearFieldResidual.Mode;
            if (nearFieldMode.IsEffective && !residualRuntimeReady)
            {
                nearFieldMode = nearFieldMode with
                {
                    EffectiveMode = SimpleDdgiNearFieldResidualMode.Off,
                    FallbackReason = GiExperimentFallbackReason.ResourceIncomplete,
                    FallbackDetail =
                    _nearFieldResidual.RuntimeSnapshot.Reason ??
                    residualPlan.Status
                };
            }

            var modes = new GiRoadmapExperimentModeDiagnostics(
                receiverFeedbackMode,
                AdvancedGiAdmissionCoordinator.ResolveMode(
                    gi.DdgiOpacityMicromapMode,
                    DdgiOpacityMicromapMode.Off,
                    supported: opacityRuntime.Supported,
                    prerequisiteGate: ommGate,
                    qualificationGate: ommQualification,
                    resourcesComplete: opacityRuntime.Enabled,
                    gi.DdgiOpacityMicromapQualificationId,
                    opacityRuntime.Detail),
                AdvancedGiAdmissionCoordinator.ResolveMode(
                    gi.SimpleDdgiDirectionalGuidingMode,
                    SimpleDdgiDirectionalGuidingMode.Off,
                    supported: guidingRuntimeSupported,
                    prerequisiteGate: guidingGate,
                    qualificationGate: guidingQualification,
                    resourcesComplete: guidingResourcesReady,
                    gi.SimpleDdgiDirectionalGuidingQualificationId,
                    guidingDetail),
                _giCaustic.Mode,
                nearFieldMode);
            return new GiRoadmapExperimentDiagnostics(
                directionalFog,
                opacityMicromap,
                raySelection.Admission,
                directionalGuiding,
                causticPlan.Admission,
                residualPlan.Admission)
            {
                Modes = modes,
                OpacityMicromapRuntime = opacityRuntime,
                CausticRuntime = GiCausticDiagnostics.Disabled
            };
        }

        private DdgiContentRuntimeSnapshot CreateDdgiContentRuntimeSnapshot(
            SceneRenderingData sceneData,
            bool simpleDdgiActive,
            bool rayQueryActive)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            SimpleDdgiVolumeManager? volumeManager = _simpleDdgiVolumeManager;
            SimpleDdgiLightTreeRuntimeDiagnostics lightTree = simpleDdgiActive
                ? _simpleDdgiLightTreeResources?.Diagnostics ??
                  SimpleDdgiLightTreeRuntimeDiagnostics.Disabled
                : SimpleDdgiLightTreeRuntimeDiagnostics.Disabled;
            SimpleDdgiDirectionalRadianceMode effectiveDirectional =
                simpleDdgiActive && volumeManager != null
                    ? volumeManager.DirectionalRadianceMode
                    : SimpleDdgiDirectionalRadianceMode.Off;
            SimpleDdgiGlossyTransportMode effectiveGlossy =
                simpleDdgiActive && volumeManager != null
                    ? volumeManager.GlossyTransportMode
                    : SimpleDdgiGlossyTransportMode.Off;
            ulong directionalAllocated = volumeManager == null
                ? 0UL
                : checked(
                    volumeManager.DirectionalRadianceBytes +
                    volumeManager.DirectionalRadianceParityBytes);

            return new DdgiContentRuntimeSnapshot
            {
                ConfiguredFeatures = gi.ConfiguredContentDependentFeatures,
                ActiveFeatures = simpleDdgiActive
                    ? gi.ActiveContentDependentFeatures
                    : DdgiContentFeature.None,
                RequestedLocalLightSamplingMode =
                    gi.SimpleDdgiLocalLightSamplingMode,
                EffectiveLocalLightSamplingMode = simpleDdgiActive
                    ? _simpleDdgiLightTreeResources?.EffectiveSamplingMode ??
                      SimpleDdgiLocalLightSamplingMode.Exact
                    : SimpleDdgiLocalLightSamplingMode.Exact,
                RequestedDirectionalRadianceMode =
                    gi.SimpleDdgiDirectionalRadianceMode,
                EffectiveDirectionalRadianceMode = effectiveDirectional,
                RequestedGlossyTransportMode =
                    gi.SimpleDdgiGlossyTransportMode,
                EffectiveGlossyTransportMode = effectiveGlossy,
                RequestedSkinnedGeometryMode = gi.DdgiSkinnedGeometryMode,
                EffectiveSkinnedGeometryMode = rayQueryActive
                    ? gi.EffectiveDdgiSkinnedGeometryMode
                    : DdgiSkinnedGeometryMode.Excluded,
                RequestedTransparentGeometryMode =
                    gi.DdgiTransparentGeometryMode,
                EffectiveTransparentGeometryMode = rayQueryActive
                    ? gi.EffectiveDdgiTransparentGeometryMode
                    : DdgiTransparentGeometryMode.MaskOnly,
                RequestedFoliageGeometryMode = gi.DdgiFoliageGeometryMode,
                EffectiveFoliageGeometryMode = rayQueryActive
                    ? sceneData.DdgiFoliageGeometryMode
                    : DdgiFoliageGeometryMode.Excluded,
                LightTree = lightTree,
                ManyLightCounters = simpleDdgiActive
                    ? _completedDdgiManyLightCounters
                    : default,
                LightBufferRevision = _lightManager.LightBufferRevision,
                LightTreeTopologyRevision =
                    _lightManager.LightTreeTopologyRevision,
                LightTreeContentRevision =
                    _lightManager.LightTreeContentRevision,
                RaySceneResourceGeneration =
                    _accelerationStructureManager?.ResourceGeneration ?? 1UL,
                RaySceneContentEpoch =
                    _accelerationStructureManager?.RaySceneContentEpoch ?? 1UL,
                SceneContentRevision = sceneData.SceneContentRevision,
                MaterialContentRevision = _materialManager.MaterialDataRevision,
                SourceLightingEpoch = volumeManager?.SourceLightingGeneration ?? 0u,
                SamplingSequenceEpoch =
                    DdgiStochasticIdentity.DefaultSamplingSequenceEpoch,
                StochasticHashAbiVersion = DdgiStochasticIdentity.HashAbiVersion,
                DirectionalRadianceAbiVersion =
                    DdgiDirectionalRadianceAbi.ForMode(effectiveDirectional),
                RayQueryInstanceAbiVersion = DdgiRayQueryInstanceAbi.Version2,
                RayQueryInstanceRecordBytes =
                    DdgiRayQueryInstanceAbi.SizeInBytes,
                DirectionalRadianceBudgetBytes =
                    gi.SimpleDdgiDirectionalRadianceMemoryBudgetBytes,
                DirectionalRadiancePlannedBytes =
                    volumeManager?.DirectionalRadianceRequiredBytes ?? 0UL,
                DirectionalRadianceAllocatedBytes = directionalAllocated,
                DirectionalRadianceParityBytes =
                    volumeManager?.DirectionalRadianceParityBytes ?? 0UL,
                DirectionalRadianceProjectionGpuMicroseconds = simpleDdgiActive
                    ? sceneData.GpuSimpleDdgiDirectionalRadianceMicroseconds
                    : 0,
                DirectionalRadianceFallbackReason = simpleDdgiActive
                    ? volumeManager?.DirectionalRadianceFallbackReason ??
                      "directional-radiance-manager-unavailable"
                    : "simple-ddgi-disabled",
                RaySceneFallbackReason = rayQueryActive
                    ? _accelerationStructureManager?.LastFallbackReason ??
                      string.Empty
                    : "ray-query-scene-disabled",
                FoliageFallbackReason =
                    sceneData.DdgiFoliageProxyFallbackReason,
                SettingsMigrationDiagnostic =
                    gi.ContentDependentSettingsMigrationDiagnostic ??
                    string.Empty
            };
        }

        private DdgiRuntimeSnapshot CreateDdgiRuntimeSnapshot(SceneRenderingData sceneData)
        {
            SimpleDdgiVolumeManager? residencyManager = _simpleDdgiVolumeManager;
            GPUSimpleDdgiResidencyFeedback residencyFeedback =
                residencyManager?.LastProbeResidencyFeedback ?? default;
            bool residencyFeedbackValid =
                residencyManager?.ProbeResidencyFeedbackValid == true;
            return new DdgiRuntimeSnapshot(
                VolumeCount: sceneData.DdgiProbeVolumeCount,
                ActiveProbeCount: sceneData.DdgiActiveProbeCount,
                ScheduledProbeUpdates: sceneData.DdgiProbesUpdated,
                WarmupState: sceneData.DdgiWarmupState,
                WarmedVisibleProbeFraction: sceneData.DdgiWarmedVisibleProbeFraction,
                WarmedLocalProbeFraction: sceneData.DdgiWarmedLocalProbeFraction,
                WarmedCascade0ProbeFraction: sceneData.DdgiWarmedCascade0ProbeFraction,
                SchedulerCandidateCount: 0,
                SchedulerRequestCount: 0,
                SchedulerBudgetRejectedCount: 0,
                SchedulerGpuMicroseconds: 0,
                SchedulerGpuP95Microseconds: 0,
                EstimateSpatialCoverage: sceneData.DdgiAverageSpatialCoverageEstimate,
                EstimateSupportCoverage: sceneData.DdgiAverageSupportCoverageEstimate,
                EstimateDataConfidence: sceneData.DdgiAverageDataConfidenceEstimate,
                EstimateVisibilityConfidence: sceneData.DdgiAverageVisibilityConfidenceEstimate,
                EstimateLeakAttenuation: sceneData.DdgiAverageLeakAttenuationEstimate,
                EstimateEffectiveWeight: sceneData.DdgiAverageEffectiveContributionEstimate,
                EstimateOwnershipConsumed: sceneData.DdgiAverageOwnershipConsumedEstimate,
                EstimateRelocationMagnitude: sceneData.DdgiAverageRelocationDisplacementFractionEstimate,
                EstimateInactiveProbeCount: sceneData.DdgiClassifiedInactiveProbeCountEstimate,
                GatherFallbackTileCount: 0,
                EmptyGatherTileCount: 0,
                SelectedLocalTileCount: 0,
                SelectedClipmapTileCount: 0,
                SimpleGatherPrimaryRejectionCounts: sceneData.SimpleDdgiGatherPrimaryRejectionCounts,
                SimpleGatherFallbackRejectionCounts: sceneData.SimpleDdgiGatherFallbackRejectionCounts,
                SimpleGatherRecoveryRejectionCounts: sceneData.SimpleDdgiGatherRecoveryRejectionCounts,
                SimpleGatherPrimaryAllFailedCount: sceneData.SimpleDdgiGatherPrimaryAllFailedCount,
                SimpleGatherFallbackAllFailedCount: sceneData.SimpleDdgiGatherFallbackAllFailedCount,
                SimpleGatherRecoveryAllFailedCount: sceneData.SimpleDdgiGatherRecoveryAllFailedCount,
                SimpleOldestVisibleUnsupportedProbeAge: sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge,
                SimpleVisibleUnsupportedProbeCountAboveLatencyTarget: sceneData
                    .SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget,
                SimpleVisibleZeroSupportRepairUpdateCount: sceneData.SimpleDdgiVisibleZeroSupportRepairUpdateCount,
                SimpleProbeLifecycleLatencyTargetFrames: sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames,
                SimpleMaximumFreshProbeAge: sceneData.SimpleDdgiMaximumFreshProbeAge,
                SimpleMaximumScrollExposedProbeAge: sceneData.SimpleDdgiMaximumScrollExposedProbeAge,
                SimpleMaximumRelocationPendingProbeAge: sceneData.SimpleDdgiMaximumRelocationPendingProbeAge,
                SimpleMaximumUnpublishedProbeAge: sceneData.SimpleDdgiMaximumUnpublishedProbeAge,
                SimpleProbeLifecycleBoundExceededCount: sceneData.SimpleDdgiProbeLifecycleBoundExceededCount,
                SimpleActive: sceneData.SimpleDdgiActive,
                SimpleProbeCount: sceneData.SimpleDdgiProbeCount,
                SimpleProbesUpdated: sceneData.SimpleDdgiProbesUpdated,
                SimpleRaysPerFrame: sceneData.SimpleDdgiRaysPerFrame,
                SimpleAtlasBytes: sceneData.SimpleDdgiAtlasBytes,
                SimpleGpuTraceMicroseconds: sceneData.GpuSimpleDdgiTraceMicroseconds,
                SimpleGpuTransportMicroseconds: sceneData.GpuSimpleDdgiTransportMicroseconds,
                SimpleGpuBlendMicroseconds: sceneData.GpuSimpleDdgiBlendMicroseconds,
                SimpleAverageBackfaceRatio: sceneData.SimpleDdgiAverageBackfaceRatioEstimate,
                SimpleAverageCloseRatio: sceneData.SimpleDdgiAverageCloseRatioEstimate,
                SimpleAverageHardInvalidProbeScore: sceneData.SimpleDdgiAverageHardInvalidProbeScoreEstimate,
                SimpleProbeResidencyMode: residencyManager?.ProbeResidencyMode ??
                                          SimpleDdgiProbeResidencyMode.Dense,
                SimpleProbeResidencyFeedbackValid: residencyFeedbackValid ? 1 : 0,
                SimpleVirtualPageCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.VirtualPageCount)
                    : 0,
                SimpleSparsePhysicalPageCapacity: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.SparsePhysicalPageCapacity)
                    : 0,
                SimpleResidentPageCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.ResidentPageCount)
                    : 0,
                SimpleDemandedPageCount: residencyFeedbackValid
                    ? ClampUIntToInt(SaturatingAdd(
                        residencyFeedback.VisibleDemandPageCount,
                        residencyFeedback.ReceiverDemandPageCount))
                    : 0,
                SimplePageAdmissionCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.AdmissionCount)
                    : 0,
                SimplePageEvictionCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.EvictionCount)
                    : 0,
                SimplePageFailedAdmissionCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.FailedAdmissionCount)
                    : 0,
                SimplePageMappingErrorCount: residencyFeedbackValid
                    ? ClampUIntToInt(SaturatingAdd(
                        residencyFeedback.PageTableReverseDisagreementCount,
                        SaturatingAdd(
                            residencyFeedback.DuplicateVirtualOwnerCount,
                            residencyFeedback.DuplicatePhysicalOwnerCount)))
                    : 0,
                SimpleNonResidentGatherRejectionCount: residencyFeedbackValid
                    ? ClampUIntToInt(
                        residencyFeedback.NonResidentGatherRejectionCount)
                    : 0,
                SimpleCoarserFallbackCount: residencyFeedbackValid
                    ? ClampUIntToInt(residencyFeedback.CoarserFallbackCount)
                    : 0,
                SimpleGpuPageDemandMicroseconds:
                sceneData.GpuSimpleDdgiPageDemandMicroseconds,
                SimpleGpuPageResidencyMicroseconds:
                sceneData.GpuSimpleDdgiPageResidencyMicroseconds,
                SimpleGpuPageFeedbackMicroseconds:
                sceneData.GpuSimpleDdgiPageFeedbackMicroseconds)
            {
                SimpleDdgiLivenessTelemetry = sceneData.SimpleDdgiLivenessTelemetry,
                SimpleDdgiLivenessWatchdog = sceneData.SimpleDdgiLivenessWatchdog
            };
        }

        internal static int SelectPrimaryDdgiLocalLight(
            LightFrameSnapshot lightSnapshot,
            out float selectedWeight,
            out float totalWeight)
        {
            int selectedIndex = -1;
            selectedWeight = 0.0f;
            totalWeight = 0.0f;
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light.Type == LightType.Directional)
                    continue;

                float weight = CalculateDdgiLocalLightSelectionWeight(light);
                totalWeight += weight;
                if (weight > selectedWeight)
                {
                    selectedIndex = i;
                    selectedWeight = weight;
                }
            }

            return selectedIndex;
        }

        private static float CalculateDdgiLocalLightSelectionWeight(Light light)
        {
            float range = Math.Max(light.Range, 0.0f);
            float spotFactor = light.Type == LightType.Spot
                ? Math.Clamp(1.0f - MathF.Cos(Math.Clamp(light.SpotAngle, 0.0f, MathF.PI)), 0.05f, 1.0f)
                : 1.0f;
            float weight = AnalyticalLightGeometry.ComputePowerWeight(light) *
                           range * range * spotFactor;
            return float.IsFinite(weight) ? weight : 0.0f;
        }

        private static float LightLuminance(Light light)
        {
            return Math.Max(0.0f, 0.2126f * light.Color.X + 0.7152f * light.Color.Y + 0.0722f * light.Color.Z);
        }

        private static bool ApproximatelyEqualProjection(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            return MathF.Abs(a.M11 - b.M11) <= epsilon &&
                   MathF.Abs(a.M12 - b.M12) <= epsilon &&
                   MathF.Abs(a.M13 - b.M13) <= epsilon &&
                   MathF.Abs(a.M14 - b.M14) <= epsilon &&
                   MathF.Abs(a.M21 - b.M21) <= epsilon &&
                   MathF.Abs(a.M22 - b.M22) <= epsilon &&
                   MathF.Abs(a.M23 - b.M23) <= epsilon &&
                   MathF.Abs(a.M24 - b.M24) <= epsilon &&
                   MathF.Abs(a.M31 - b.M31) <= epsilon &&
                   MathF.Abs(a.M32 - b.M32) <= epsilon &&
                   MathF.Abs(a.M33 - b.M33) <= epsilon &&
                   MathF.Abs(a.M34 - b.M34) <= epsilon &&
                   MathF.Abs(a.M41 - b.M41) <= epsilon &&
                   MathF.Abs(a.M42 - b.M42) <= epsilon &&
                   MathF.Abs(a.M43 - b.M43) <= epsilon &&
                   MathF.Abs(a.M44 - b.M44) <= epsilon;
        }

        private void PrepareGiCausticCoordinatorFrame(
            in LightFrameSnapshot lightSnapshot)
        {
            GiCausticHeroSourceSnapshot? heroSnapshot = null;
            string heroReason = "caustic-runtime-plan-or-graph-unavailable";
            if (_giCaustic.RequiresHeroSnapshot)
            {
                if (_accelerationStructureManager is null)
                {
                    heroReason =
                        "caustic-current-pose-acceleration-structures-unavailable";
                }
                else
                {
                    _ = _accelerationStructureManager
                        .TryCreateGiCausticHeroSourceSnapshot(
                            _giCaustic.HeroExtractionProfile,
                            out heroSnapshot,
                            out heroReason);
                }
            }

            _ = _giCaustic.PrepareFrame(
                new GiCausticFrameRequest(
                    _advancedGiAdmission.GraphModes.UsesCausticWorldCache,
                    Settings.GlobalIllumination.GiCausticMode,
                    _currentFrame,
                    _advancedGiAdmission.RuntimeContentState,
                    _advancedGiAdmission.GiCausticAdmissionContext,
                    _advancedGiAdmission.GiCausticEvidence,
                    lightSnapshot,
                    _ddgiEmissiveTransport?.Snapshot ?? default,
                    heroSnapshot,
                    heroReason));
        }

        private void PrepareAccelerationStructures(Scene scene, SceneRenderingData sceneData)
        {
            if (_accelerationStructureManager == null)
                return;

            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            bool opacityContentAdmitted =
                gi.DdgiOpacityMicromapMode !=
                DdgiOpacityMicromapMode.AutoQualified ||
                _advancedGiAdmission.RuntimeContentState.Matched;
            _accelerationStructureManager.SetOpacityMicromapRuntimeAdmission(
                opacityContentAdmitted,
                opacityContentAdmitted
                    ? null
                    : _advancedGiAdmission.RuntimeContentState.Reason);
            bool ddgiRaySceneEnabled = gi.Enabled &&
                                       gi.EffectiveUseRayQueryBackend;
            RaySceneRequirement requirement = ddgiRaySceneEnabled
                ? new RaySceneRequirement(
                    RaySceneConsumer.Ddgi,
                    ResolveDdgiRaySceneCategories(gi),
                    Settings.Shadows.MaxShadowDistance,
                    gi.EffectiveDdgiSkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose)
                : RaySceneRequirement.None;
            RaySceneRequirement directionalRequirement =
                sceneData.DirectionalShadowPassEnabled
                    ? RaySceneRequirement.ForDirectionalShadows(Settings.Shadows)
                    : RaySceneRequirement.None;
            requirement = requirement.Union(directionalRequirement);
            RaySceneRequirement areaShadowRequirement =
                RaySceneRequirement.ForAreaLightShadows(
                    Settings.Shadows,
                    sceneData.AreaShadowSelectedCount > 0,
                    sceneData.AreaShadowMaximumRayDistance);
            requirement = requirement.Union(areaShadowRequirement);
            RaySceneRequirement reflectionRequirement =
                RaySceneRequirement.ForReflections(Settings.Reflections);
            requirement = requirement.Union(reflectionRequirement);
            RaySceneRequirement thickTransmissionRequirement =
                RaySceneRequirement.ForThickTransmission(
                    Settings.Transparency);
            requirement = requirement.Union(thickTransmissionRequirement);
            bool enabled = requirement.Enabled;
            bool directionalRaySceneRequested = directionalRequirement.Enabled;
            bool areaShadowRaySceneRequested = areaShadowRequirement.Enabled;
            bool reflectionRaySceneRequested = reflectionRequirement.Enabled;
            bool thickTransmissionRaySceneRequested =
                thickTransmissionRequirement.Enabled;
            bool qualityAllowsStaticStreaming = gi.DdgiQualityTier is
                DdgiQualityTier.DdgiLow or DdgiQualityTier.DdgiMedium;
            bool farFieldCoverageReady = qualityAllowsStaticStreaming &&
                                         _farFieldClipmapManager?.CoverageReady == true;
            _accelerationStructureManager.PrepareFrameRayScene(
                scene,
                enabled,
                _currentFrame,
                new AccelerationStructureResidencyPolicy(
                    gi.StreamedGiAccelerationStructuresEnabled,
                    sceneData.CameraPosition,
                    gi.GiAccelerationStructureMemoryBudgetBytes,
                    farFieldCoverageReady
                        ? gi.GiAccelerationStructureStaticResidentDistance
                        : float.MaxValue,
                    farFieldCoverageReady
                        ? gi.GiAccelerationStructureMaximumStaticInstances
                        : int.MaxValue,
                    gi.GiAccelerationStructureEvictionGraceFrames,
                    AllowStaticMemoryCulling:
                    farFieldCoverageReady &&
                    !directionalRaySceneRequested &&
                    !areaShadowRaySceneRequested &&
                    !reflectionRaySceneRequested &&
                    !thickTransmissionRaySceneRequested,
                    // Build a complete ray scene transaction over bounded
                    // frames. Raster/IBL remains usable while the BLAS cache
                    // fills; a partial TLAS is never published.
                    MaximumStaticBlasBuildsPerFrame: 64),
                new DdgiDynamicRayScenePolicy(
                    directionalRaySceneRequested || areaShadowRaySceneRequested ||
                    reflectionRaySceneRequested ||
                    thickTransmissionRaySceneRequested
                        ? DdgiSkinnedGeometryMode.CurrentPose
                        : gi.EffectiveDdgiSkinnedGeometryMode,
                    // The shared scene must satisfy the union of its consumers.
                    // Reflection rays apply their binary alpha-hit policy while
                    // tracing, so they must not remove transparent geometry
                    // that DDGI requires from the shared TLAS.
                    ddgiRaySceneEnabled
                        ? gi.EffectiveDdgiTransparentGeometryMode
                        : DdgiTransparentGeometryMode.MaskOnly,
                    directionalRaySceneRequested || areaShadowRaySceneRequested ||
                    reflectionRaySceneRequested ||
                    thickTransmissionRaySceneRequested
                        ? DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                        : gi.EffectiveDdgiFoliageGeometryMode,
                    GeometryDecalsEnabled:
                    Settings.Decals.GeometryDecalsEnabled &&
                    (gi.ActiveContentDependentFeatures &
                     DdgiContentFeature.TransparentGeometry) != 0,
                    AlphaMaskedTransportEnabled:
                    directionalRaySceneRequested || areaShadowRaySceneRequested ||
                    reflectionRaySceneRequested ||
                    thickTransmissionRaySceneRequested ||
                    gi.DdgiAlphaMaskedTransportEnabled,
                    DynamicStorageBudgetBytes: gi.DdgiDynamicBlasMemoryBudgetBytes,
                    DynamicScratchBudgetBytes: gi.DdgiDynamicBlasScratchBudgetBytes,
                    MaximumBuildsPerFrame: gi.DdgiDynamicBlasBuildsPerFrame,
                    MaximumPrimitivesPerFrame: gi.DdgiDynamicBlasPrimitivesPerFrame,
                    DecalCandidateLimit: gi.DdgiDecalCandidateLimit)
                {
                    DynamicProviderGeometryEnabled =
                        ddgiRaySceneEnabled && gi.DdgiQualityTier is
                            DdgiQualityTier.DdgiHigh or
                            DdgiQualityTier.DdgiUltra,
                    DynamicGeometryBudgets =
                        DdgiDynamicGeometryBudgetPolicy.Production with
                        {
                            GpuTimeBudgetMicroseconds =
                                gi.DdgiQualityTier == DdgiQualityTier.DdgiUltra
                                    ? 1_000
                                    : 750
                        }
                },
                sceneContentRevision: sceneData.SceneContentRevision,
                foliageProxyFrame: _ddgiFoliageProxyFrame,
                requirement: requirement);
        }

        private static RaySceneGeometryCategory ResolveDdgiRaySceneCategories(
            GlobalIlluminationSettings settings)
        {
            RaySceneGeometryCategory categories =
                RaySceneGeometryCategory.StaticOpaque |
                RaySceneGeometryCategory.DynamicOpaque |
                RaySceneGeometryCategory.AlphaTested |
                RaySceneGeometryCategory.DoubleSided;
            if (settings.EffectiveDdgiSkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose)
                categories |= RaySceneGeometryCategory.SkinnedCurrentPose;
            if (settings.EffectiveDdgiFoliageGeometryMode != DdgiFoliageGeometryMode.Excluded)
                categories |= RaySceneGeometryCategory.FoliageOpaque |
                              RaySceneGeometryCategory.FoliageAlphaTested;
            if (settings.EffectiveDdgiTransparentGeometryMode != DdgiTransparentGeometryMode.MaskOnly)
                categories |= RaySceneGeometryCategory.ThinTransmission;
            if (settings.EffectiveDdgiTransparentGeometryMode == DdgiTransparentGeometryMode.StochasticBlend)
                categories |= RaySceneGeometryCategory.AlphaBlend;
            return categories;
        }

        private void PrepareDdgiFoliageProxies(
            Scene scene,
            SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            DdgiFoliageGeometryMode mode =
                Settings.Foliage.Enabled && _context.RayQuerySupported
                    ? Settings.Reflections.Enabled &&
                      Settings.Reflections.Mode == ReflectionMode.HybridRayQuery
                        ? DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                        : sceneData.AreaShadowSelectedCount > 0 &&
                          Settings.Shadows.AreaShadowsEnabled
                            ? DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                            : sceneData.DirectionalShadowPassEnabled &&
                              Settings.Shadows.RequestedDirectionalShadowMode is
                                  DirectionalShadowMode.HybridContact or
                                  DirectionalShadowMode.RayQueryHard or
                                  DirectionalShadowMode.RayQuerySoft
                                ? DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                                : gi.EffectiveUseDdgi && gi.EffectiveUseRayQueryBackend
                                    ? gi.EffectiveDdgiFoliageGeometryMode
                                    : DdgiFoliageGeometryMode.Excluded
                    : DdgiFoliageGeometryMode.Excluded;
            sceneData.DdgiFoliageGeometryMode = mode;
            if (_ddgiFoliageProxyManager == null ||
                mode == DdgiFoliageGeometryMode.Excluded)
            {
                _ddgiFoliageProxyFrame =
                    DdgiFoliageProxyFrame.Empty(_currentFrame);
                return;
            }

            _ddgiFoliageProxyFrame =
                _ddgiFoliageProxyManager.PrepareFrame(
                    scene,
                    mode,
                    gi.DdgiFoliageProxyTriangleBudget,
                    gi.DdgiFoliageProxyUpdateCadenceFrames,
                    _ddgiFrameSerial,
                    _particleTimeSeconds,
                    Settings.Foliage.DensityScale,
                    _ddgiFoliageProxyGenerationPass?.IsAvailable == true,
                    _ddgiFoliageProxyGenerationPass?
                        .InitializationFailureReason,
                    _currentCommandBuffer,
                    _currentFrame);
            DdgiFoliageProxyFrame frame = _ddgiFoliageProxyFrame;
            sceneData.DdgiFoliageProxyVertexCount = frame.VertexCount;
            sceneData.DdgiFoliageProxyCardCount =
                frame.VertexCount / DdgiFoliageProxyManager.VerticesPerCrossedCard;
            sceneData.DdgiFoliageProxyTriangleCount = frame.TriangleCount;
            sceneData.DdgiFoliageAuthoredInstanceCount =
                frame.AuthoredInstanceCount;
            sceneData.DdgiFoliageGeneratedInstanceCount =
                frame.GeneratedInstanceCount;
            sceneData.DdgiFoliageDroppedTriangleCount =
                frame.DroppedTriangleCount;
            sceneData.DdgiFoliageRepresentedBladeCount =
                frame.EstimatedRepresentedBladeCount;
            sceneData.DdgiFoliageProxyUpdatedThisFrame =
                frame.UpdatedThisFrame ? 1 : 0;
            sceneData.DdgiFoliageProxyUploadBytes = frame.UploadedBytes;
            sceneData.DdgiFoliageProxyVertexBufferBytes =
                frame.VertexBufferBytes;
            sceneData.DdgiFoliageProxyIndexBufferBytes =
                frame.IndexBufferBytes;
            sceneData.DdgiFoliageProxyPatchBufferBytes =
                frame.PatchBufferBytes;
            sceneData.DdgiFoliageProxyContentSignature =
                frame.ContentSignature;
            sceneData.DdgiFoliageProxyCadenceGeneration =
                frame.CadenceGeneration;
            sceneData.DdgiFoliageProxyRequestedRepresentedInstanceCount =
                frame.RequestedRepresentedInstanceCount;
            sceneData.DdgiFoliageProxyDensityError = frame.DensityError;
            sceneData.DdgiFoliageProxyWindAgeSeconds = frame.CardCount > 0
                ? MathF.Max(0f, _particleTimeSeconds - frame.WindTimeSeconds)
                : 0f;
            sceneData.DdgiFoliageProxyNearCardCount = frame.NearCardCount;
            sceneData.DdgiFoliageProxyMidCardCount = frame.MidCardCount;
            sceneData.DdgiFoliageProxyFarCardCount = frame.FarCardCount;
            sceneData.DdgiFoliageProxyExcludedPatchCount =
                frame.ExcludedPatchCount;
            sceneData.DdgiFoliageProxyLodPolicyVersion =
                frame.LodPolicyVersion;
            sceneData.CpuDdgiFoliageProxyBuildMicroseconds =
                frame.CpuBuildMicroseconds;
            sceneData.CpuDdgiFoliageProxyUploadMicroseconds =
                frame.CpuUploadMicroseconds;
            sceneData.DdgiFoliageProxyFallbackReason =
                frame.FallbackReason;
            sceneData.UploadedBytes = checked(
                sceneData.UploadedBytes + frame.UploadedBytes);
        }

        private void RecordAccelerationStructures(SceneRenderingData sceneData)
        {
            if (_accelerationStructureManager == null)
                return;

            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            BufferHandle skinnedVertexBuffer =
                _skinningManager.GetSkinnedVertexBuffer(_currentFrame);
            AccelerationStructureFrameStats stats =
                _accelerationStructureManager.RecordDynamicRaySceneBuilds(
                    _stagingRing,
                    _currentCommandBuffer,
                    skinnedVertexBuffer,
                    _gpuTimestamps,
                    _currentFrame);
            sceneData.AccelerationStructureBottomLevelCount = stats.BottomLevelCount;
            sceneData.AccelerationStructureTopLevelInstanceCount = stats.TopLevelInstanceCount;
            sceneData.AccelerationStructureBlasBuildCount = stats.BlasBuildCount;
            sceneData.AccelerationStructureBlasCompactionQueryCount = stats.BlasCompactionQueryCount;
            sceneData.AccelerationStructureBlasCompactionCount = stats.BlasCompactionCount;
            sceneData.AccelerationStructureBlasCompactionSourceBytes = stats.BlasCompactionSourceBytes;
            sceneData.AccelerationStructureBlasCompactionBytesSaved = stats.BlasCompactionBytesSaved;
            sceneData.AccelerationStructureBlasCompactedResidentBytesSaved =
                stats.BottomLevelAccelerationStructureCompactedBytesSaved;
            sceneData.AccelerationStructureBlasCompactionPendingCount = stats.PendingBlasCompactionCount;
            sceneData.AccelerationStructureBlasCompactionQueryOverflowCount = stats.BlasCompactionQueryOverflowCount;
            sceneData.AccelerationStructureBlasCompactionQueryReadbackFailureCount =
                stats.BlasCompactionQueryReadbackFailureCount;
            sceneData.AccelerationStructureTlasBuildCount = stats.TlasBuildCount;
            sceneData.AccelerationStructureTlasUpdateCount = stats.TlasUpdateCount;
            sceneData.AccelerationStructureTlasSkipCount = stats.TlasSkipCount;
            // This is the live state, not a copy of the request bit. Captures
            // keep authored intent separately in StreamedGiAccelerationStructuresFeatureEnabled.
            sceneData.AccelerationStructureStreamingEnabled =
                gi.StreamedGiAccelerationStructuresEnabled && stats.Active ? 1 : 0;
            sceneData.AccelerationStructureStaticInstanceCandidateCount = stats.StaticInstanceCandidateCount;
            sceneData.AccelerationStructureStaticInstanceResidentCount = stats.StaticInstanceResidentCount;
            sceneData.AccelerationStructureStaticInstanceCulledCount = stats.StaticInstanceCulledCount;
            sceneData.AccelerationStructureBlasEvictionCount = stats.BlasEvictionCount;
            sceneData.AccelerationStructureBlasEvictionBytes = stats.BlasEvictionBytes;
            sceneData.AccelerationStructureBlasBudgetRejectedCount = stats.BlasBudgetRejectedCount;
            sceneData.AccelerationStructureDynamicBottomLevelCount =
                stats.DynamicBottomLevelCount;
            sceneData.AccelerationStructureDynamicBlasBytes =
                stats.DynamicBottomLevelBytes;
            sceneData.AccelerationStructureDynamicBlasPeakBytes =
                stats.PeakDynamicBottomLevelBytes;
            sceneData.AccelerationStructureDynamicFullBuildCount =
                stats.DynamicFullBuildCount;
            sceneData.AccelerationStructureDynamicRefitCount =
                stats.DynamicRefitCount;
            sceneData.AccelerationStructureDynamicProxyFallbackCount =
                stats.DynamicProxyFallbackCount;
            sceneData.AccelerationStructureDynamicExcludedCount =
                stats.DynamicExcludedCount;
            sceneData.AccelerationStructureDynamicBudgetDeferredCount =
                stats.DynamicBudgetDeferredCount;
            sceneData.AccelerationStructureDynamicTopologyMismatchCount =
                stats.DynamicTopologyMismatchCount;
            sceneData.AccelerationStructureDynamicScratchBytes =
                stats.DynamicScratchBytes;
            sceneData.AccelerationStructureDynamicPrimitiveCount =
                stats.DynamicPrimitiveCount;
            sceneData.AccelerationStructureBlasBytes = stats.BottomLevelAccelerationStructureBytes;
            sceneData.AccelerationStructureTlasBytes = stats.TopLevelAccelerationStructureBytes;
            sceneData.AccelerationStructureRetiredBytes = stats.RetiredAccelerationStructureBytes;
            sceneData.AccelerationStructureResidentBytes = stats.AccelerationStructureBytes;
            sceneData.AccelerationStructureMemoryBudgetBytes = gi.StreamedGiAccelerationStructuresEnabled
                ? gi.GiAccelerationStructureMemoryBudgetBytes
                : 0;
            sceneData.AccelerationStructureBytes = checked(
                stats.AccelerationStructureBytes +
                stats.RetiredAccelerationStructureBytes +
                stats.ScratchBufferBytes +
                stats.InstanceBufferBytes +
                stats.RayQueryInstanceMetadataBufferBytes);
            sceneData.AccelerationStructureScratchBytes = stats.ScratchBufferBytes;
            sceneData.AccelerationStructureInstanceBufferBytes = stats.InstanceBufferBytes;
            sceneData.AccelerationStructureRayQueryMetadataBytes = stats.RayQueryInstanceMetadataBufferBytes;
            sceneData.AccelerationStructureInstanceUploadBytes = stats.InstanceUploadBytes;
            sceneData.AccelerationStructureRayQueryMetadataUploadBytes = stats.RayQueryInstanceMetadataUploadBytes;
            sceneData.CpuAccelerationStructureBuildMicroseconds = stats.BuildMicroseconds;
            sceneData.CpuAccelerationStructureBlasBuildMicroseconds = stats.BlasBuildMicroseconds;
            sceneData.CpuAccelerationStructureBlasCompactionMicroseconds = stats.BlasCompactionMicroseconds;
            sceneData.CpuAccelerationStructureTlasBuildMicroseconds = stats.TlasBuildMicroseconds;
            sceneData.CpuAccelerationStructureInstanceUploadMicroseconds = stats.InstanceUploadMicroseconds;
            sceneData.AccelerationStructureFallbackReason = stats.FallbackReason;
            sceneData.RaySceneReadiness =
                _accelerationStructureManager.ReadinessSnapshot;
        }

        private static BoundingBox Union(BoundingBox left, BoundingBox right) =>
            new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));

        private static Vector3 ToCoreVector(System.Numerics.Vector3 value) =>
            new(value.X, value.Y, value.Z);

        private static BoundingBox EstimateSceneProbeBounds(Scene scene)
        {
            return SimpleDdgiSceneBounds.Estimate(scene);
        }

        private static void ApplyCompletedHybridReflectionCounters(
            SceneRenderingData sceneData,
            in HybridReflectionCounterSnapshot counters)
        {
            if (counters.ReadbackValid == 0)
                return;

            sceneData.HybridReflectionCountersReadbackValid = 1;
            sceneData.HybridReflectionSsrHitCount = counters.SsrHits;
            sceneData.HybridReflectionRayQueryRequestCount =
                counters.RayRequests;
            sceneData.HybridReflectionRayQueryCount = counters.RayQueries;
            sceneData.HybridReflectionRayQueryOverflowCount =
                counters.RayOverflows;
            sceneData.HybridReflectionRayQueryHitCount = counters.RayHits;
            sceneData.HybridReflectionRayQueryMissCount = counters.RayMisses;
            sceneData.HybridReflectionDdgiFallbackCount =
                counters.DdgiFallbacks;
            sceneData.HybridReflectionProbeFallbackCount =
                counters.ProbeFallbacks;
            sceneData.HybridReflectionEnvironmentFallbackCount =
                counters.EnvironmentFallbacks;
            sceneData.HybridReflectionFullRateTileCount =
                counters.FullRateTiles;
            sceneData.HybridReflectionHalfRateTileCount =
                counters.HalfRateTiles;
            sceneData.HybridReflectionQuarterRateTileCount =
                counters.QuarterRateTiles;
            sceneData.HybridReflectionAnalyticTileCount =
                counters.AnalyticTiles;
            sceneData.HybridReflectionReuseTileCount =
                counters.ReuseTiles;
            sceneData.HybridReflectionActiveTileCount =
                counters.ActiveTiles;
            sceneData.HybridReflectionTileOverflowCount =
                counters.TileOverflows;
        }

        private static void ApplyCompletedTransparentReflectionCounters(
            SceneRenderingData sceneData,
            in TransparentReflectionGpuCounters counters)
        {
            sceneData.TransparentReflectionRayRequestCount =
                counters.RayRequests;
            sceneData.TransparentReflectionEstimatedSsrHitCount =
                counters.EstimatedSsrHits;
            sceneData.TransparentReflectionEstimatedRayHitCount =
                counters.EstimatedRayHits;
            sceneData.TransparentReflectionEstimatedRayMissCount =
                counters.EstimatedRayMisses;
            sceneData.TransparentReflectionEstimatedBudgetRejectedCount =
                counters.EstimatedBudgetRejected;
            sceneData.TransparentReflectionEstimatedDdgiFallbackCount =
                counters.EstimatedDdgiFallbacks;
            sceneData.TransparentReflectionEstimatedProbeFallbackCount =
                counters.EstimatedProbeFallbacks;
            sceneData.TransparentReflectionEstimatedEnvironmentFallbackCount =
                counters.EstimatedEnvironmentFallbacks;
            sceneData.TransparentReflectionExactSsrEligibleCount =
                counters.ExactSsrEligible;
            sceneData.TransparentReflectionExactSsrAdmittedCount =
                counters.ExactSsrAdmitted;
            sceneData.TransparentReflectionExactSsrReservedSampleCount =
                counters.ExactSsrReservedSamples;
            sceneData.TransparentReflectionExactSsrActualSampleCount =
                counters.ExactSsrActualSamples;
            sceneData.TransparentReflectionExactSsrHitCount =
                counters.ExactSsrHits;
            sceneData.TransparentReflectionExactSsrBudgetRejectedCount =
                counters.ExactSsrBudgetRejected;
            sceneData.TransparentReflectionExactRayAdmittedCount =
                counters.ExactRayAdmitted;
            sceneData.TransparentReflectionExactRayBudgetRejectedCount =
                counters.ExactRayBudgetRejected;
        }

        private static void ApplyCompletedGpuCounters(SceneRenderingData sceneData, GpuMeshletCounters counters)
        {
            sceneData.DepthTaskInvocations = counters.DepthCandidates;
            sceneData.DepthFrustumCulledMeshletsGpu = counters.DepthFrustumCulled;
            sceneData.DepthEmittedMeshletsGpu = counters.DepthEmitted;

            // The compacted indirect pipeline intentionally has no task stage.
            // Its authoritative counters come from the compaction pass below;
            // overwriting them with the (correctly zero) task diagnostic buffer
            // would make the capture claim that no mesh work was submitted.
            if (sceneData.SceneSubmissionForwardTaskShader ==
                SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly)
            {
                return;
            }

            int sceneSubmissionHiZTested = sceneData.ForwardOcclusionTestedMeshletsGpu;
            int sceneSubmissionHiZCulled = sceneData.ForwardOcclusionCulledMeshletsGpu;
            sceneData.ForwardTaskInvocations = counters.ForwardCandidates;
            sceneData.ForwardFrustumCulledMeshletsGpu = counters.ForwardFrustumCulled;
            sceneData.ForwardOcclusionTestedMeshletsGpu =
                Math.Max(counters.ForwardOcclusionTested, sceneSubmissionHiZTested);
            sceneData.ForwardOcclusionCulledMeshletsGpu =
                Math.Max(counters.ForwardOcclusionCulled, sceneSubmissionHiZCulled);
            sceneData.ForwardEmittedMeshletsGpu = counters.ForwardEmitted;
        }

        private static void ApplyCompletedCompactedMeshOnlyForwardCounters(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot sceneSubmissionCounters,
            SceneSubmissionCounterSnapshot forwardVisibilityCounters)
        {
            if (sceneData.SceneSubmissionForwardTaskShader !=
                SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedMeshOnly)
            {
                return;
            }

            int submittedMeshWorkgroups = Math.Max(0, sceneData.ForwardTaskInvocations);
            SceneSubmissionCounterSnapshot authoritativeCounters =
                sceneData.ForwardVisibilityCompactionActive
                    ? forwardVisibilityCounters
                    : sceneSubmissionCounters;
            if (authoritativeCounters.IsValid)
                submittedMeshWorkgroups = ClampUIntToInt(authoritativeCounters.EmittedCount);

            // Preserve the historical field contract for capture consumers: it
            // represents forward candidate workgroups. On the mesh-only path the
            // indirect group count is already dense, so candidates == emitted and
            // there is no second-stage frustum rejection.
            sceneData.ForwardTaskInvocations = submittedMeshWorkgroups;
            sceneData.ForwardFrustumCulledMeshletsGpu = 0;
            sceneData.ForwardEmittedMeshletsGpu = submittedMeshWorkgroups;
        }

        private static void ApplyCompletedDdgiForwardEstimateCounters(
            SceneRenderingData sceneData,
            DdgiForwardEstimateCounters counters)
        {
            bool ddgiActive = sceneData.DdgiProbeCount > 0 && sceneData.DdgiProbeVolumeCount > 0;
            if (!ddgiActive || counters.ReadbackValid == 0)
            {
                sceneData.DdgiForwardEstimateCountersReadbackValid = 0;
                sceneData.DdgiForwardEstimateSampleCount = 0;
                sceneData.DdgiForwardEstimateZeroVisibleButCoveredCount = 0;
                sceneData.DdgiForwardEstimateZeroEffectiveButCoveredCount = 0;
                sceneData.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount = 0;
                sceneData.DdgiForwardEstimateSampledIrradianceLuminance = 0.0f;
                sceneData.DdgiForwardEstimateRawDiffuseLuminance = 0.0f;
                sceneData.DdgiForwardEstimateFinalDiffuseLuminance = 0.0f;
                sceneData.DdgiForwardEstimateEnvironmentFallbackWeight = 0.0f;
                sceneData.DdgiReceiverDiffuseReflectanceLuminance = 0.0f;
                sceneData.DdgiReceiverDiffuseReflectanceSampleCount = 0;
                sceneData.DdgiTraceOneSidedBackFaceAlbedoLuminance = 0.0f;
                sceneData.DdgiTraceOneSidedBackFaceHitCount = 0;
                sceneData.DdgiTraceOpaqueAlbedoLuminance = 0.0f;
                sceneData.DdgiTraceOpaqueHitCount = 0;
                sceneData.DdgiTraceThinSurfaceAlbedoLuminance = 0.0f;
                sceneData.DdgiTraceThinSurfaceHitCount = 0;
                sceneData.DdgiTraceUnsupportedTransmissionAlbedoLuminance = 0.0f;
                sceneData.DdgiTraceUnsupportedTransmissionHitCount = 0;
                sceneData.DdgiTraceReflectDisabledAlbedoLuminance = 0.0f;
                sceneData.DdgiTraceReflectDisabledHitCount = 0;
                sceneData.DdgiAverageSpatialCoverageEstimate = 0.0f;
                sceneData.DdgiAverageSupportCoverageEstimate = 0.0f;
                sceneData.DdgiAverageDataConfidenceEstimate = 0.0f;
                sceneData.DdgiAverageVisibilityConfidenceEstimate = 0.0f;
                sceneData.DdgiAverageLeakAttenuationEstimate = 0.0f;
                sceneData.DdgiAverageOwnershipConsumedEstimate = 0.0f;
                sceneData.DdgiSupportRejectedInactiveCount = 0;
                sceneData.DdgiSupportRejectedZeroIrradianceAlphaCount = 0;
                sceneData.DdgiSupportRejectedLowQualityCount = 0;
                sceneData.DdgiProbeIrradianceAlphaAverage = 0.0f;
                sceneData.DdgiProbeQualityXAverage = 0.0f;
                sceneData.DdgiProbeQualityYAverage = 0.0f;
                sceneData.DdgiProbeQualityZAverage = 0.0f;
                sceneData.DdgiProbeQualitySampleCount = 0;
                sceneData.DdgiSampledProbeCurrentFrustumCount = 0;
                sceneData.DdgiSampledProbeSideRearCount = 0;
                sceneData.DdgiSampledProbeStaleAgeCount = 0;
                sceneData.DdgiClipmapInfoPrimaryAttemptCount = 0;
                sceneData.DdgiClipmapInfoPrimaryOkCount = 0;
                sceneData.DdgiClipmapInfoPrimaryFailedCount = 0;
                sceneData.DdgiClipmapInfoPrimaryEdgeFadeAverage = 0.0f;
                sceneData.DdgiClipmapInfoPrimaryBlendWeightAverage = 0.0f;
                sceneData.DdgiFastGatherAttemptCount = 0;
                sceneData.DdgiFastGatherAcceptedCount = 0;
                sceneData.DdgiFastGatherRejectedZeroSpatialCount = 0;
                sceneData.DdgiFastGatherRejectedZeroSupportCount = 0;
                sceneData.DdgiFastGatherRejectedZeroDataCount = 0;
                sceneData.DdgiFastGatherRejectedZeroOwnershipCount = 0;
                sceneData.DdgiShaderGatherFallbackAttemptCount = 0;
                sceneData.DdgiShaderGatherFallbackAcceptedCount = 0;
                sceneData.DdgiShaderGatherFallbackEmptyCount = 0;
                sceneData.DdgiTraceEnergySampleCount = 0;
                sceneData.DdgiTraceEnergyHitCount = 0;
                sceneData.DdgiTraceEnergyMissCount = 0;
                sceneData.DdgiTraceEnergyRayLuminanceAverage = 0.0f;
                sceneData.DdgiTraceEnergyDirectLuminanceAverage = 0.0f;
                sceneData.DdgiTraceEnergyEmissiveLuminanceAverage = 0.0f;
                sceneData.DdgiTraceEnergyStableLuminanceAverage = 0.0f;
                sceneData.DdgiTraceEnergySkyLuminanceAverage = 0.0f;
                sceneData.DdgiTraceEnergyHitZeroDirectCount = 0;
                sceneData.DdgiTraceEnergyHitWithDirectCount = 0;
                sceneData.DdgiTraceEnergyDirectNoShadowLuminanceAverage = 0.0f;
                sceneData.DdgiShadowVisibilityRayCount = 0;
                sceneData.DdgiShadowVisibilityOccludedCount = 0;
                sceneData.DdgiShadowVisibilityNearHitCount = 0;
                sceneData.DdgiShadowVisibilityCommittedHitDistanceAverage = 0.0f;
                sceneData.DdgiTraceEarlyOutDisabledCount = 0;
                sceneData.DdgiTraceEarlyOutBeyondRequestCount = 0;
                sceneData.DdgiTraceEarlyOutResolveBoundsCount = 0;
                sceneData.DdgiTraceEarlyOutResolveProbeRangeCount = 0;
                sceneData.DdgiTraceEarlyOutResolveClipmapCellCount = 0;
                sceneData.DdgiTraceEarlyOutResolveClipmapRingCount = 0;
                sceneData.DdgiTraceRingMismatchCorrectedCount = 0;
                sceneData.DdgiTraceRingMismatchSample = string.Empty;
                sceneData.DdgiBlendEnergySampleCount = 0;
                sceneData.DdgiBlendEnergyIrradianceLuminanceAverage = 0.0f;
                sceneData.DdgiBlendEnergyConfidenceAverage = 0.0f;
                sceneData.DdgiBlendEnergyLowConfidenceCount = 0;
                sceneData.DdgiBlendEnergyNonzeroIrradianceCount = 0;
                sceneData.DdgiBlendEnergyNonFiniteIrradianceCount = 0;
                sceneData.DdgiBlendEnergyFireflySuppressedCount = 0;
                sceneData.SimpleDdgiTransportEnergySampleCount = 0;
                sceneData.SimpleDdgiTransportSourceCacheHitCount = 0;
                sceneData.SimpleDdgiTransportSourceCacheMissCount = 0;
                sceneData.SimpleDdgiTransportBounceLuminanceAverage = 0.0f;
                sceneData.SimpleDdgiTransportSourceLuminanceAverage = 0.0f;
                sceneData.SimpleDdgiTransportTotalLuminanceAverage = 0.0f;
                sceneData.DdgiTransparentReceiverSampleCount = 0;
                sceneData.DdgiTransparentReceiverIrradianceLuminanceAverage = 0.0f;
                sceneData.DdgiTransparentReceiverFinalLuminanceAverage = 0.0f;
                sceneData.DdgiDecalReceiverSampleCount = 0;
                sceneData.DdgiDecalReceiverIrradianceLuminanceAverage = 0.0f;
                sceneData.DdgiDecalReceiverFinalLuminanceAverage = 0.0f;
                sceneData.DdgiVisibilityMomentMeanAverage = 0.0f;
                sceneData.DdgiVisibilityMomentVarianceAverage = 0.0f;
                sceneData.DdgiVisibilityProbeDistanceAverage = 0.0f;
                sceneData.DdgiVisibilityMomentSampleCount = 0;
                sceneData.DdgiVisibilityLargeDistanceMarginCount = 0;
                sceneData.DdgiVisibilityZeroTransportCount = 0;
                sceneData.DdgiVisibilityZeroTransportWithIrradianceCount = 0;
                return;
            }

            sceneData.DdgiForwardEstimateCountersReadbackValid = 1;
            sceneData.DdgiForwardEstimateSampleCount = counters.SampleCount;
            sceneData.DdgiForwardEstimateZeroVisibleButCoveredCount = counters.ZeroVisibleButCoveredCount;
            sceneData.DdgiForwardEstimateZeroEffectiveButCoveredCount = counters.ZeroEffectiveButCoveredCount;
            sceneData.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount =
                counters.HighOwnershipLowDeliveredIndirectCount;
            sceneData.DdgiForwardEstimateSampledIrradianceLuminance =
                Math.Max(counters.SampledIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiForwardEstimateRawDiffuseLuminance = counters.RawDiffuseLuminanceAverage;
            sceneData.DdgiForwardEstimateFinalDiffuseLuminance = counters.FinalDiffuseLuminanceAverage;
            sceneData.DdgiForwardEstimateEnvironmentFallbackWeight =
                Math.Clamp(counters.EnvironmentFallbackWeightAverage * 4.0f, 0.0f, 4.0f);
            sceneData.DdgiReceiverDiffuseReflectanceLuminance = counters.ReceiverDiffuseReflectanceLuminanceAverage;
            sceneData.DdgiReceiverDiffuseReflectanceSampleCount = counters.ReceiverDiffuseReflectanceSampleCount;
            sceneData.DdgiTraceOneSidedBackFaceAlbedoLuminance = counters.TraceOneSidedBackFaceAlbedoLuminanceAverage;
            sceneData.DdgiTraceOneSidedBackFaceHitCount = counters.TraceOneSidedBackFaceHitCount;
            sceneData.DdgiTraceOpaqueAlbedoLuminance = counters.TraceOpaqueAlbedoLuminanceAverage;
            sceneData.DdgiTraceOpaqueHitCount = counters.TraceOpaqueHitCount;
            sceneData.DdgiTraceThinSurfaceAlbedoLuminance = counters.TraceThinSurfaceAlbedoLuminanceAverage;
            sceneData.DdgiTraceThinSurfaceHitCount = counters.TraceThinSurfaceHitCount;
            sceneData.DdgiTraceUnsupportedTransmissionAlbedoLuminance =
                counters.TraceUnsupportedTransmissionAlbedoLuminanceAverage;
            sceneData.DdgiTraceUnsupportedTransmissionHitCount = counters.TraceUnsupportedTransmissionHitCount;
            sceneData.DdgiTraceReflectDisabledAlbedoLuminance = counters.TraceReflectDisabledAlbedoLuminanceAverage;
            sceneData.DdgiTraceReflectDisabledHitCount = counters.TraceReflectDisabledHitCount;
            sceneData.DdgiAverageSpatialCoverageEstimate = Math.Clamp(counters.SpatialCoverageAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageSupportCoverageEstimate = Math.Clamp(counters.SupportCoverageAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageDataConfidenceEstimate = Math.Clamp(counters.DataConfidenceAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageVisibilityConfidenceEstimate =
                Math.Clamp(counters.VisibilityConfidenceAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageLeakAttenuationEstimate = Math.Clamp(counters.LeakAttenuationAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageOwnershipConsumedEstimate = Math.Clamp(counters.OwnershipConsumedAverage, 0.0f, 1.0f);
            sceneData.DdgiSupportRejectedInactiveCount = counters.SupportRejectedInactiveCount;
            sceneData.DdgiSupportRejectedZeroIrradianceAlphaCount = counters.SupportRejectedZeroIrradianceAlphaCount;
            sceneData.DdgiSupportRejectedLowQualityCount = counters.SupportRejectedLowQualityCount;
            sceneData.DdgiProbeIrradianceAlphaAverage = Math.Clamp(counters.ProbeIrradianceAlphaAverage, 0.0f, 1.0f);
            sceneData.DdgiProbeQualityXAverage = Math.Clamp(counters.ProbeQualityXAverage, 0.0f, 1.0f);
            sceneData.DdgiProbeQualityYAverage = Math.Clamp(counters.ProbeQualityYAverage, 0.0f, 1.0f);
            sceneData.DdgiProbeQualityZAverage = Math.Clamp(counters.ProbeQualityZAverage, 0.0f, 1.0f);
            sceneData.DdgiProbeQualitySampleCount = counters.ProbeQualitySampleCount;
            sceneData.DdgiSampledProbeCurrentFrustumCount = counters.SampledProbeCurrentFrustumCount;
            sceneData.DdgiSampledProbeSideRearCount = counters.SampledProbeSideRearCount;
            sceneData.DdgiSampledProbeStaleAgeCount = counters.SampledProbeStaleAgeCount;
            sceneData.DdgiClipmapInfoPrimaryAttemptCount = counters.ClipmapInfoPrimaryAttemptCount;
            sceneData.DdgiClipmapInfoPrimaryOkCount = counters.ClipmapInfoPrimaryOkCount;
            sceneData.DdgiClipmapInfoPrimaryFailedCount = counters.ClipmapInfoPrimaryFailedCount;
            sceneData.DdgiClipmapInfoPrimaryEdgeFadeAverage =
                Math.Clamp(counters.ClipmapInfoPrimaryEdgeFadeAverage, 0.0f, 1.0f);
            sceneData.DdgiClipmapInfoPrimaryBlendWeightAverage =
                Math.Clamp(counters.ClipmapInfoPrimaryBlendWeightAverage, 0.0f, 1.0f);
            sceneData.DdgiFastGatherAttemptCount = counters.FastGatherAttemptCount;
            sceneData.DdgiFastGatherAcceptedCount = counters.FastGatherAcceptedCount;
            sceneData.DdgiFastGatherRejectedZeroSpatialCount = counters.FastGatherRejectedZeroSpatialCount;
            sceneData.DdgiFastGatherRejectedZeroSupportCount = counters.FastGatherRejectedZeroSupportCount;
            sceneData.DdgiFastGatherRejectedZeroDataCount = counters.FastGatherRejectedZeroDataCount;
            sceneData.DdgiFastGatherRejectedZeroOwnershipCount = counters.FastGatherRejectedZeroOwnershipCount;
            sceneData.DdgiShaderGatherFallbackAttemptCount = counters.ShaderGatherFallbackAttemptCount;
            sceneData.DdgiShaderGatherFallbackAcceptedCount = counters.ShaderGatherFallbackAcceptedCount;
            sceneData.DdgiShaderGatherFallbackEmptyCount = counters.ShaderGatherFallbackEmptyCount;
            sceneData.DdgiTraceEnergySampleCount = counters.TraceEnergySampleCount;
            sceneData.DdgiTraceEnergyHitCount = counters.TraceEnergyHitCount;
            sceneData.DdgiTraceEnergyMissCount = counters.TraceEnergyMissCount;
            sceneData.DdgiTraceEnergyRayLuminanceAverage = Math.Max(counters.TraceEnergyRayLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyDirectLuminanceAverage =
                Math.Max(counters.TraceEnergyDirectLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyEmissiveLuminanceAverage =
                Math.Max(counters.TraceEnergyEmissiveLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyStableLuminanceAverage =
                Math.Max(counters.TraceEnergyStableLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergySkyLuminanceAverage = Math.Max(counters.TraceEnergySkyLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyHitZeroDirectCount = counters.TraceEnergyHitZeroDirectCount;
            sceneData.DdgiTraceEnergyHitWithDirectCount = counters.TraceEnergyHitWithDirectCount;
            sceneData.DdgiTraceEnergyDirectNoShadowLuminanceAverage =
                Math.Max(counters.TraceEnergyDirectNoShadowLuminanceAverage, 0.0f);
            sceneData.DdgiShadowVisibilityRayCount = counters.ShadowVisibilityRayCount;
            sceneData.DdgiShadowVisibilityOccludedCount = counters.ShadowVisibilityOccludedCount;
            sceneData.DdgiShadowVisibilityNearHitCount = counters.ShadowVisibilityNearHitCount;
            sceneData.DdgiShadowVisibilityCommittedHitDistanceAverage =
                Math.Max(counters.ShadowVisibilityCommittedHitDistanceAverage, 0.0f);
            sceneData.DdgiTraceEarlyOutDisabledCount = counters.TraceEarlyOutDisabledCount;
            sceneData.DdgiTraceEarlyOutBeyondRequestCount = counters.TraceEarlyOutBeyondRequestCount;
            sceneData.DdgiTraceEarlyOutResolveBoundsCount = counters.TraceEarlyOutResolveBoundsCount;
            sceneData.DdgiTraceEarlyOutResolveProbeRangeCount = counters.TraceEarlyOutResolveProbeRangeCount;
            sceneData.DdgiTraceEarlyOutResolveClipmapCellCount = counters.TraceEarlyOutResolveClipmapCellCount;
            sceneData.DdgiTraceEarlyOutResolveClipmapRingCount = counters.TraceEarlyOutResolveClipmapRingCount;
            sceneData.DdgiTraceRingMismatchCorrectedCount = counters.TraceRingMismatchCorrectedCount;
            sceneData.DdgiTraceRingMismatchSample = FormatDdgiTraceRingMismatchSample(counters);
            sceneData.DdgiBlendEnergySampleCount = counters.BlendEnergySampleCount;
            sceneData.DdgiBlendEnergyIrradianceLuminanceAverage =
                Math.Max(counters.BlendEnergyIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiBlendEnergyConfidenceAverage = Math.Clamp(counters.BlendEnergyConfidenceAverage, 0.0f, 1.0f);
            sceneData.DdgiBlendEnergyLowConfidenceCount = counters.BlendEnergyLowConfidenceCount;
            sceneData.DdgiBlendEnergyNonzeroIrradianceCount = counters.BlendEnergyNonzeroIrradianceCount;
            sceneData.DdgiBlendEnergyNonFiniteIrradianceCount = counters.BlendEnergyNonFiniteIrradianceCount;
            sceneData.DdgiBlendEnergyFireflySuppressedCount = counters.BlendEnergyFireflySuppressedCount;
            sceneData.SimpleDdgiTransportEnergySampleCount = counters.SimpleDdgiTransportEnergySampleCount;
            sceneData.SimpleDdgiTransportSourceCacheHitCount = counters.SimpleDdgiTransportSourceCacheHitCount;
            sceneData.SimpleDdgiTransportSourceCacheMissCount = counters.SimpleDdgiTransportSourceCacheMissCount;
            sceneData.SimpleDdgiTransportBounceLuminanceAverage =
                Math.Max(counters.SimpleDdgiTransportBounceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiTransportSourceLuminanceAverage =
                Math.Max(counters.SimpleDdgiTransportSourceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiTransportTotalLuminanceAverage =
                Math.Max(counters.SimpleDdgiTransportTotalLuminanceAverage, 0.0f);
            sceneData.DdgiTransparentReceiverSampleCount = counters.TransparentReceiverSampleCount;
            sceneData.DdgiTransparentReceiverIrradianceLuminanceAverage =
                Math.Max(counters.TransparentReceiverIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiTransparentReceiverFinalLuminanceAverage =
                Math.Max(counters.TransparentReceiverFinalLuminanceAverage, 0.0f);
            sceneData.DdgiDecalReceiverSampleCount = counters.DecalReceiverSampleCount;
            sceneData.DdgiDecalReceiverIrradianceLuminanceAverage =
                Math.Max(counters.DecalReceiverIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiDecalReceiverFinalLuminanceAverage =
                Math.Max(counters.DecalReceiverFinalLuminanceAverage, 0.0f);
            sceneData.DdgiVisibilityMomentMeanAverage = Math.Max(counters.VisibilityMomentMeanAverage, 0.0f);
            sceneData.DdgiVisibilityMomentVarianceAverage = Math.Max(counters.VisibilityMomentVarianceAverage, 0.0f);
            sceneData.DdgiVisibilityProbeDistanceAverage = Math.Max(counters.VisibilityProbeDistanceAverage, 0.0f);
            sceneData.DdgiVisibilityMomentSampleCount = counters.VisibilityMomentSampleCount;
            sceneData.DdgiVisibilityLargeDistanceMarginCount = counters.VisibilityLargeDistanceMarginCount;
            sceneData.DdgiVisibilityZeroTransportCount = counters.VisibilityZeroTransportCount;
            sceneData.DdgiVisibilityZeroTransportWithIrradianceCount =
                counters.VisibilityZeroTransportWithIrradianceCount;
            sceneData.DdgiAverageEffectiveContributionEstimate =
                Math.Clamp(counters.EffectiveWeightAverage, 0.0f, 1.0f);
        }

        private static void ApplyCompletedDdgiInvestigationCounters(
            SceneRenderingData sceneData,
            DdgiInvestigationCounters counters)
        {
            sceneData.SimpleDdgiStorageValidation = counters.StorageValidation;
            if (sceneData.SimpleDdgiStorage.IsAvailable)
            {
                // Storage layout data is populated before the render graph,
                // while completed GPU counters become available afterward.
                // Refresh the nested capture contract here so reports never
                // retain the default counters copied during frame setup.
                sceneData.SimpleDdgiStorage = sceneData.SimpleDdgiStorage with
                {
                    ValidationCounters = counters.StorageValidation
                };
            }

            if (counters.ReadbackValid == 0)
            {
                sceneData.DdgiInvestigationCountersReadbackValid = 0;
                sceneData.SimpleDdgiFreshAtlasForwardSampleCount = 0;
                sceneData.SimpleDdgiZeroIrradianceSampleCount = 0;
                sceneData.SimpleDdgiNonzeroIrradianceSampleCount = 0;
                sceneData.SimpleDdgiAverageSampledIrradianceLuminance = 0.0f;
                sceneData.SimpleDdgiAverageVisibility = 0.0f;
                sceneData.SimpleDdgiLowVisibilitySampleCount = 0;
                sceneData.SimpleDdgiGatherSampleCount = 0;
                sceneData.SimpleDdgiSecondVolumeGatherCount = 0;
                sceneData.SimpleDdgiGatherMultiplicity =
                    SimpleDdgiGatherMultiplicityCounters.Empty;
                sceneData.DecalFragmentAttribution =
                    DecalFragmentAttributionCounters.Empty;
                sceneData.SimpleDdgiGatherPrimaryRejectionCounts = Array.Empty<uint>();
                sceneData.SimpleDdgiGatherFallbackRejectionCounts = Array.Empty<uint>();
                sceneData.SimpleDdgiGatherRecoveryRejectionCounts = Array.Empty<uint>();
                sceneData.SimpleDdgiGatherPrimaryAllFailedCount = 0;
                sceneData.SimpleDdgiGatherFallbackAllFailedCount = 0;
                sceneData.SimpleDdgiGatherRecoveryAllFailedCount = 0;
                sceneData.DdgiForwardSimplePathSampleCount = 0;
                sceneData.DdgiForwardLegacyPathSampleCount = 0;
                sceneData.DdgiForwardZeroFinalIndirectCount = 0;
                sceneData.DdgiForwardZeroDdgiButNonzeroIblCount = 0;
                sceneData.DdgiForwardZeroDdgiAndZeroIblCount = 0;
                sceneData.DdgiForwardOutOfGridSampleCount = 0;
                sceneData.DdgiForwardClampedProbeSampleCount = 0;
                sceneData.DdgiForwardNanOrInfSampleCount = 0;
                sceneData.DdgiIrradianceAtlasZeroTexelSampleCount = 0;
                sceneData.DdgiVisibilityAtlasZeroMomentSampleCount = 0;
                sceneData.DdgiAtlasWriteProbeCount = 0;
                sceneData.DdgiAtlasWriteTexelCount = 0;
                sceneData.DdgiBlendZeroRayWeightProbeCount = 0;
                sceneData.DdgiBlendNonzeroIrradianceProbeCount = 0;
                sceneData.DdgiBlendPreviousAtlasUsedCount = 0;
                sceneData.DdgiBlendHysteresisZeroFrameCount = 0;
                sceneData.DdgiSimpleTraceHitCount = 0;
                sceneData.DdgiSimpleTraceMissCount = 0;
                sceneData.DdgiSimpleTraceZeroRadianceHitCount = 0;
                sceneData.DdgiSimpleTraceDirectLightHitCount = 0;
                sceneData.DdgiSimpleTraceEmissiveHitCount = 0;
                sceneData.DdgiSimpleTraceFarFieldHitCount = 0;
                sceneData.DdgiSimpleTraceFarFieldMissCount = 0;
                sceneData.DdgiSimpleTraceTlasUnavailableFrameCount = 0;
                sceneData.SimpleDdgiSkyVisibilitySampleCount = 0;
                sceneData.SimpleDdgiAverageSkyVisibility = 0.0f;
                sceneData.FarFieldSunShadowSampleCount = 0;
                sceneData.FarFieldSunShadowOccludedCount = 0;
                sceneData.SimpleDdgiRoughSpecularSampleCount = 0;
                sceneData.SimpleDdgiRoughSpecularNonzeroCount = 0;
                sceneData.DdgiSimpleTraceFarFieldStepBucket0Count = 0;
                sceneData.DdgiSimpleTraceFarFieldStepBucket1Count = 0;
                sceneData.DdgiSimpleTraceFarFieldStepBucket2Count = 0;
                sceneData.DdgiSimpleTraceFarFieldStepBucket3Count = 0;
                sceneData.DdgiSimpleTraceFarFieldStepBucket4Count = 0;
                sceneData.DdgiBlackFrameSuspect = 0;
                sceneData.DdgiBlackFrameAfterRecenter = 0;
                sceneData.DdgiBlackFrameAfterAtlasClear = 0;
                sceneData.DdgiBlackFrameDuringFreshAtlas = 0;
                ApplySimpleDdgiVolumeGatherCounters(sceneData, counters);
                return;
            }

            sceneData.DdgiInvestigationCountersReadbackValid = 1;
            sceneData.SimpleDdgiFreshAtlasForwardSampleCount = counters.FreshAtlasForwardSampleCount;
            sceneData.SimpleDdgiZeroIrradianceSampleCount = counters.SimpleZeroIrradianceSampleCount;
            sceneData.SimpleDdgiNonzeroIrradianceSampleCount = counters.SimpleNonzeroIrradianceSampleCount;
            sceneData.SimpleDdgiAverageSampledIrradianceLuminance =
                Math.Max(counters.SimpleSampledIrradianceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiAverageVisibility = Math.Clamp(counters.SimpleVisibilityAverage, 0.0f, 1.0f);
            sceneData.SimpleDdgiLowVisibilitySampleCount = counters.SimpleLowVisibilitySampleCount;
            sceneData.SimpleDdgiGatherSampleCount = counters.SimpleGatherCount;
            sceneData.SimpleDdgiSecondVolumeGatherCount = counters.SimpleSecondVolumeGatherCount;
            sceneData.SimpleDdgiGatherMultiplicity = counters.GatherMultiplicity;
            sceneData.DecalFragmentAttribution = counters.DecalFragmentAttribution;
            sceneData.SimpleDdgiGatherPrimaryRejectionCounts =
                counters.SimpleGatherPrimaryRejectionCounts ?? Array.Empty<uint>();
            sceneData.SimpleDdgiGatherFallbackRejectionCounts =
                counters.SimpleGatherFallbackRejectionCounts ?? Array.Empty<uint>();
            sceneData.SimpleDdgiGatherRecoveryRejectionCounts =
                counters.SimpleGatherRecoveryRejectionCounts ?? Array.Empty<uint>();
            sceneData.SimpleDdgiGatherPrimaryAllFailedCount =
                counters.SimpleGatherPrimaryAllFailedCount;
            sceneData.SimpleDdgiGatherFallbackAllFailedCount =
                counters.SimpleGatherFallbackAllFailedCount;
            sceneData.SimpleDdgiGatherRecoveryAllFailedCount =
                counters.SimpleGatherRecoveryAllFailedCount;
            sceneData.DdgiForwardSimplePathSampleCount = counters.SimpleForwardSampleCount;
            sceneData.DdgiForwardLegacyPathSampleCount = counters.LegacyForwardSampleCount;
            sceneData.DdgiForwardZeroFinalIndirectCount = counters.ForwardZeroFinalIndirectCount;
            sceneData.DdgiForwardZeroDdgiButNonzeroIblCount = counters.ForwardZeroDdgiButNonzeroIblCount;
            sceneData.DdgiForwardZeroDdgiAndZeroIblCount = counters.ForwardZeroDdgiAndZeroIblCount;
            sceneData.DdgiForwardOutOfGridSampleCount = counters.ForwardOutOfGridSampleCount;
            sceneData.DdgiForwardClampedProbeSampleCount = counters.ForwardClampedProbeSampleCount;
            sceneData.DdgiForwardNanOrInfSampleCount = counters.ForwardNanOrInfSampleCount;
            sceneData.DdgiIrradianceAtlasZeroTexelSampleCount = counters.IrradianceAtlasZeroTexelSampleCount;
            sceneData.DdgiVisibilityAtlasZeroMomentSampleCount = counters.VisibilityAtlasZeroMomentSampleCount;
            sceneData.DdgiAtlasWriteProbeCount = counters.AtlasWriteProbeCount;
            sceneData.DdgiAtlasWriteTexelCount = counters.AtlasWriteTexelCount;
            sceneData.DdgiBlendZeroRayWeightProbeCount = counters.BlendZeroRayWeightProbeCount;
            sceneData.DdgiBlendNonzeroIrradianceProbeCount = counters.BlendNonzeroIrradianceProbeCount;
            sceneData.DdgiBlendPreviousAtlasUsedCount = counters.BlendPreviousAtlasUsedCount;
            sceneData.DdgiBlendHysteresisZeroFrameCount = counters.BlendHysteresisZeroFrameCount;
            sceneData.DdgiSimpleTraceHitCount = counters.SimpleTraceHitCount;
            sceneData.DdgiSimpleTraceMissCount = counters.SimpleTraceMissCount;
            sceneData.DdgiSimpleTraceZeroRadianceHitCount = counters.SimpleTraceZeroRadianceHitCount;
            sceneData.DdgiSimpleTraceDirectLightHitCount = counters.SimpleTraceDirectLightHitCount;
            sceneData.DdgiSimpleTraceEmissiveHitCount = counters.SimpleTraceEmissiveHitCount;
            sceneData.DdgiSimpleTraceFarFieldHitCount = counters.SimpleTraceFarFieldHitCount;
            sceneData.DdgiSimpleTraceFarFieldMissCount = counters.SimpleTraceFarFieldMissCount;
            sceneData.DdgiSimpleTraceTlasUnavailableFrameCount = Math.Max(
                sceneData.DdgiSimpleTraceTlasUnavailableFrameCount, counters.SimpleTraceTlasUnavailableFrameCount);
            sceneData.SimpleDdgiSkyVisibilitySampleCount = counters.SkyVisibilitySampleCount;
            sceneData.SimpleDdgiAverageSkyVisibility = Math.Clamp(counters.SkyVisibilityAverage, 0.0f, 1.0f);
            sceneData.FarFieldSunShadowSampleCount = counters.FarSunShadowSampleCount;
            sceneData.FarFieldSunShadowOccludedCount = counters.FarSunShadowOccludedCount;
            sceneData.SimpleDdgiRoughSpecularSampleCount = counters.RoughSpecularSampleCount;
            sceneData.SimpleDdgiRoughSpecularNonzeroCount = counters.RoughSpecularNonzeroCount;
            sceneData.DdgiSimpleTraceFarFieldStepBucket0Count = counters.FarFieldStepBucket0Count;
            sceneData.DdgiSimpleTraceFarFieldStepBucket1Count = counters.FarFieldStepBucket1Count;
            sceneData.DdgiSimpleTraceFarFieldStepBucket2Count = counters.FarFieldStepBucket2Count;
            sceneData.DdgiSimpleTraceFarFieldStepBucket3Count = counters.FarFieldStepBucket3Count;
            sceneData.DdgiSimpleTraceFarFieldStepBucket4Count = counters.FarFieldStepBucket4Count;
            ApplySimpleDdgiVolumeGatherCounters(sceneData, counters);

            // A one-pixel zero value is not a black frame. The stateful evaluator runs after
            // the full diagnostics snapshot is assembled, where it can combine sample
            // fractions, completed-readback freshness, recenter/clear/warmup state, and
            // consecutive-frame persistence. Keep these legacy scene fields neutral here so
            // they cannot reintroduce the old false-positive heuristic.
            sceneData.DdgiBlackFrameSuspect = 0;
            sceneData.DdgiBlackFrameAfterRecenter = 0;
            sceneData.DdgiBlackFrameAfterAtlasClear = 0;
            sceneData.DdgiBlackFrameDuringFreshAtlas = 0;
        }

        private static void ApplyCompletedDirectionalShadowReceiverCounters(
            SceneRenderingData sceneData,
            DirectionalShadowReceiverCounters counters)
        {
            sceneData.DirectionalShadowReceiverCountersReadbackValid = counters.ReadbackValid;
            sceneData.DirectionalShadowReceiverUnresolvedCount = ClampUIntToInt(counters.UnresolvedCount);
            if (counters.ReadbackValid == 0)
            {
                Array.Clear(
                    sceneData.DirectionalShadowReceiverPrimarySelectionCounts,
                    0,
                    sceneData.DirectionalShadowReceiverPrimarySelectionCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverProjectionRejectedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverProjectionRejectedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverUvDepthRejectedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverUvDepthRejectedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverFallbackCounts,
                    0,
                    sceneData.DirectionalShadowReceiverFallbackCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverTransitionBlendCounts,
                    0,
                    sceneData.DirectionalShadowReceiverTransitionBlendCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverPrimaryResolvedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverPrimaryResolvedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverClearDepthFootprintCounts,
                    0,
                    sceneData.DirectionalShadowReceiverClearDepthFootprintCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts,
                    0,
                    sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverFinalFullyLitCounts,
                    0,
                    sceneData.DirectionalShadowReceiverFinalFullyLitCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts,
                    0,
                    sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverAverageDepths,
                    0,
                    sceneData.DirectionalShadowReceiverAverageDepths.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths,
                    0,
                    sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths.Length);
                Array.Clear(
                    sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths,
                    0,
                    sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths.Length);
                return;
            }

            CopyCounterArray(counters.PrimarySelectionCounts,
                sceneData.DirectionalShadowReceiverPrimarySelectionCounts);
            CopyCounterArray(counters.ProjectionRejectedCounts,
                sceneData.DirectionalShadowReceiverProjectionRejectedCounts);
            CopyCounterArray(counters.UvDepthRejectedCounts, sceneData.DirectionalShadowReceiverUvDepthRejectedCounts);
            CopyCounterArray(counters.FallbackCounts, sceneData.DirectionalShadowReceiverFallbackCounts);
            CopyCounterArray(counters.TransitionBlendCounts, sceneData.DirectionalShadowReceiverTransitionBlendCounts);
            CopyCounterArray(counters.PrimaryResolvedCounts, sceneData.DirectionalShadowReceiverPrimaryResolvedCounts);
            CopyCounterArray(counters.ClearDepthFootprintCounts,
                sceneData.DirectionalShadowReceiverClearDepthFootprintCounts);
            CopyCounterArray(counters.PrimaryFullyLitCounts, sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts);
            CopyCounterArray(counters.PrimaryPartiallyShadowedCounts,
                sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts);
            CopyCounterArray(counters.PrimaryFullyShadowedCounts,
                sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts);
            CopyCounterArray(counters.FinalFullyLitCounts, sceneData.DirectionalShadowReceiverFinalFullyLitCounts);
            CopyCounterArray(counters.FinalPartiallyShadowedCounts,
                sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts);
            CopyCounterArray(counters.FinalFullyShadowedCounts,
                sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts);
            CopyFloatArray(counters.AverageReceiverDepths, sceneData.DirectionalShadowReceiverAverageDepths);
            CopyFloatArray(counters.AverageMinimumSampledDepths,
                sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths);
            CopyFloatArray(counters.AverageMaximumSampledDepths,
                sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths);
        }

        private static void ApplySimpleDdgiVolumeGatherCounters(
            SceneRenderingData sceneData,
            DdgiInvestigationCounters counters)
        {
            IReadOnlyList<uint>? primaryCounts = counters.SimpleVolumePrimaryGatherCounts;
            IReadOnlyList<uint>? sampledCounts = counters.SimpleVolumeSampledGatherCounts;
            IReadOnlyList<SimpleDdgiVolumeEnergyCounters>? energyCounts =
                counters.SimpleVolumeEnergyCounters;
            for (int i = 0; i < sceneData.DdgiVolumeDiagnostics.Count; i++)
            {
                DdgiVolumeDiagnosticsEntry entry = sceneData.DdgiVolumeDiagnostics[i];
                int volumeIndex = entry.VolumeIndex;
                bool countersValid = counters.ReadbackValid != 0 &&
                                     primaryCounts != null &&
                                     sampledCounts != null &&
                                     (uint)volumeIndex < (uint)primaryCounts.Count &&
                                     (uint)volumeIndex < (uint)sampledCounts.Count;
                bool energyCountersValid = counters.EnergyReadbackValid != 0 &&
                                           energyCounts != null &&
                                           (uint)volumeIndex < (uint)energyCounts.Count;
                sceneData.DdgiVolumeDiagnostics[i] = entry with
                {
                    GatherCountersReadbackValid = countersValid ? 1 : 0,
                    PrimaryGatherCount = countersValid ? primaryCounts![volumeIndex] : 0u,
                    SampledGatherCount = countersValid ? sampledCounts![volumeIndex] : 0u,
                    EnergyCountersReadbackValid = energyCountersValid ? 1 : 0,
                    EnergyCounters = energyCountersValid
                        ? energyCounts![volumeIndex]
                        : SimpleDdgiVolumeEnergyCounters.Empty
                };
            }
        }

        private static string FormatDdgiTraceRingMismatchSample(DdgiForwardEstimateCounters counters)
        {
            if (counters.TraceRingMismatchSampleValid == 0)
                return string.Empty;

            return
                $"updateIndex={counters.TraceRingMismatchSampleUpdateIndex}, requestProbe={counters.TraceRingMismatchSampleRequestProbeIndex}, " +
                $"computedProbe={counters.TraceRingMismatchSampleComputedProbeIndex}, volume={counters.TraceRingMismatchSampleVolumeIndex}, " +
                $"logical=({counters.TraceRingMismatchSampleLogicalCellX},{counters.TraceRingMismatchSampleLogicalCellY},{counters.TraceRingMismatchSampleLogicalCellZ}), " +
                $"firstProbe={counters.TraceRingMismatchSampleFirstProbe}, " +
                $"requestAge={counters.TraceRingMismatchSampleRequestAgeFrames}, " +
                $"gridMin=({counters.TraceRingMismatchSampleGridMinX},{counters.TraceRingMismatchSampleGridMinY},{counters.TraceRingMismatchSampleGridMinZ}), " +
                $"ringOffset=({counters.TraceRingMismatchSampleRingOffsetX},{counters.TraceRingMismatchSampleRingOffsetY},{counters.TraceRingMismatchSampleRingOffsetZ}), " +
                $"counts=({counters.TraceRingMismatchSampleProbeCountX},{counters.TraceRingMismatchSampleProbeCountY},{counters.TraceRingMismatchSampleProbeCountZ})";
        }

        private static void ApplyHiZCounterDiagnostics(SceneRenderingData sceneData)
        {
            sceneData.HiZCounterSource = sceneData.HiZPolicyCounterSource;
            sceneData.ForwardHiZTestedCount = sceneData.ForwardOcclusionTestedMeshletsGpu;
            sceneData.ForwardHiZCulledCount = sceneData.ForwardOcclusionCulledMeshletsGpu;
            sceneData.ForwardHiZCullRate = sceneData.ForwardHiZTestedCount > 0
                ? (float)sceneData.ForwardHiZCulledCount / sceneData.ForwardHiZTestedCount
                : 0.0f;
            if (sceneData.HiZCounterSource == HiZCounterSource.SceneSubmissionCompaction)
            {
                sceneData.PreviousHiZTested = sceneData.ForwardHiZTestedCount;
                sceneData.PreviousHiZCulled = sceneData.ForwardHiZCulledCount;
                sceneData.CurrentFrameHiZTested = 0;
                sceneData.CurrentFrameHiZCulled = 0;
            }
            else if (sceneData.HiZCounterSource == HiZCounterSource.ForwardVisibilityCompaction)
            {
                sceneData.CurrentFrameHiZTested = sceneData.ForwardHiZTestedCount;
                sceneData.CurrentFrameHiZCulled = sceneData.ForwardHiZCulledCount;
                sceneData.PreviousHiZTested = 0;
                sceneData.PreviousHiZCulled = 0;
            }
            else
            {
                sceneData.PreviousHiZTested = 0;
                sceneData.PreviousHiZCulled = 0;
            }
        }

        private static void ApplyCompletedGpuParticleCounters(SceneRenderingData sceneData,
            GpuParticleCounterSnapshot counters)
        {
            sceneData.GpuParticleCountersReadbackValid = counters.Valid;
            sceneData.GpuParticleAliveCount = counters.AliveCount;
            sceneData.GpuParticleDeadCount = counters.DeadCount;
            sceneData.GpuParticleSpawnedCount = counters.SpawnedCount;
            sceneData.GpuParticleKilledCount = counters.KilledCount;
            sceneData.GpuParticleCulledCount = counters.CulledCount;
            sceneData.GpuParticleRenderedCount = counters.RenderedCount;
            sceneData.GpuParticleDroppedSpawnCount = counters.DroppedSpawnCount;
            sceneData.GpuParticleBlendBucket0Count = counters.BlendBucket0Count;
            sceneData.GpuParticleBlendBucket1Count = counters.BlendBucket1Count;
            sceneData.GpuParticleBlendBucket2Count = counters.BlendBucket2Count;
            sceneData.GpuParticleBlendBucket3Count = counters.BlendBucket3Count;
            sceneData.GpuParticleBlendBucket4Count = counters.BlendBucket4Count;
        }

        private static void ApplyCompletedSceneSubmissionCounters(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot counters)
        {
            if (counters.IsValid)
            {
                sceneData.SceneSubmissionGpuOpaqueCandidateCount = ClampUIntToInt(counters.CandidateCount);
                sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount = ClampUIntToInt(counters.EmittedCount);
                sceneData.SceneSubmissionGpuOpaqueFrustumRejectedCount = ClampUIntToInt(counters.FrustumRejectedCount);
                sceneData.SceneSubmissionGpuOpaqueOverflowCount = ClampUIntToInt(counters.OverflowCount);
                sceneData.MeshletNormalConeCandidateCount = ClampUIntToInt(counters.NormalConeCandidateCount);
                sceneData.MeshletNormalConeTestedCount = ClampUIntToInt(counters.NormalConeTestedCount);
                sceneData.MeshletNormalConeRejectedCount = ClampUIntToInt(counters.NormalConeRejectedCount);
                sceneData.MeshletNormalConeInvalidCount = ClampUIntToInt(counters.NormalConeInvalidCount);
                sceneData.ForwardOcclusionTestedMeshletsGpu = ClampUIntToInt(counters.HiZTestedCount);
                sceneData.ForwardOcclusionCulledMeshletsGpu = ClampUIntToInt(counters.HiZRejectedCount);
                sceneData.SceneSubmissionGpuIndirectMeshletTaskCount =
                    sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                        ? ClampUIntToInt(counters.EmittedCount)
                        : 0;
                sceneData.SceneSubmissionGpuLod0EmittedCount = ClampUIntToInt(counters.Lod0EmittedCount);
                sceneData.SceneSubmissionGpuLod1EmittedCount = ClampUIntToInt(counters.Lod1EmittedCount);
                sceneData.SceneSubmissionGpuLod2EmittedCount = ClampUIntToInt(counters.Lod2EmittedCount);
                sceneData.SceneSubmissionGpuMissingLodFallbackCount = ClampUIntToInt(counters.MissingLodFallbackCount);
                sceneData.SceneSubmissionGpuOpaqueLodDecimatedCount =
                    ClampUIntToInt(counters.OpaqueLodDecimatedCount);
                sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount =
                    ClampUIntToInt(counters.DirectionalShadowLodFallbackCount);
                sceneData.SceneSubmissionGpuHierarchicalInstanceCount =
                    ClampUIntToInt(counters.HierarchicalInstanceCount);
                sceneData.SceneSubmissionGpuHierarchySelectedNodeCount =
                    ClampUIntToInt(counters.HierarchySelectedNodeCount);
                sceneData.SceneSubmissionGpuHierarchyTraversalFallbackCount =
                    ClampUIntToInt(
                        counters.HierarchyTraversalFallbackCount);
                sceneData.SceneSubmissionGpuDepthSolidCandidateCount =
                    ClampUIntToInt(counters.SolidDepthCandidateCount);
                sceneData.SceneSubmissionGpuDepthMaskedCandidateCount =
                    ClampUIntToInt(counters.MaskedDepthCandidateCount);
                sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount =
                    ClampUIntToInt(counters.SolidDepthEmittedCount);
                sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount =
                    ClampUIntToInt(counters.MaskedDepthEmittedCount);
                sceneData.SceneSubmissionGpuDepthOverflowCount = ClampUlongToInt(
                    (ulong)counters.SolidDepthOverflowCount + counters.MaskedDepthOverflowCount);
                ApplyDirectionalShadowCompactionCounters(sceneData, counters);
            }
        }

        private static void ApplyCompletedForwardVisibilityCounters(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot counters)
        {
            sceneData.ForwardVisibilityCounterReadbackValid =
                counters.IsValid ? 1 : 0;
            if (!counters.IsValid)
                return;

            sceneData.ForwardVisibilityCandidateCount =
                ClampUIntToInt(counters.CandidateCount);
            sceneData.ForwardVisibilityEmittedCount =
                ClampUIntToInt(counters.EmittedCount);
            sceneData.ForwardVisibilityOverflowCount =
                ClampUIntToInt(counters.OverflowCount);
            sceneData.CurrentFrameHiZTested = ClampUIntToInt(counters.HiZTestedCount);
            sceneData.CurrentFrameHiZCulled = ClampUIntToInt(counters.HiZRejectedCount);
            sceneData.ForwardOcclusionTestedMeshletsGpu = Math.Max(
                sceneData.ForwardOcclusionTestedMeshletsGpu,
                sceneData.CurrentFrameHiZTested);
            sceneData.ForwardOcclusionCulledMeshletsGpu = Math.Max(
                sceneData.ForwardOcclusionCulledMeshletsGpu,
                sceneData.CurrentFrameHiZCulled);
        }

        private static void ApplyDirectionalShadowCompactionCounters(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot counters)
        {
            CopyCounterArray(counters.DirectionalStaticShadowCandidateCounts,
                sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts);
            CopyCounterArray(counters.DirectionalStaticShadowEmittedCounts,
                sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts);
            CopyCounterArray(counters.DirectionalStaticShadowRejectedCounts,
                sceneData.SceneSubmissionGpuDirectionalStaticShadowRejectedCounts);
            CopyCounterArray(counters.DirectionalStaticShadowOverflowCounts,
                sceneData.SceneSubmissionGpuDirectionalStaticShadowOverflowCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowCandidateCounts,
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowEmittedCounts,
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowRejectedCounts,
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowOverflowCounts,
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts);

            ulong candidateCount =
                Sum(counters.DirectionalStaticShadowCandidateCounts) +
                Sum(counters.DirectionalDynamicShadowCandidateCounts);
            ulong emittedCount =
                Sum(counters.DirectionalStaticShadowEmittedCounts) +
                Sum(counters.DirectionalDynamicShadowEmittedCounts);
            ulong overflowCount =
                Sum(counters.DirectionalStaticShadowOverflowCounts) +
                Sum(counters.DirectionalDynamicShadowOverflowCounts);
            sceneData.SceneSubmissionGpuDirectionalShadowCandidateCount = ClampUlongToInt(candidateCount);
            sceneData.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount = ClampUlongToInt(emittedCount);
            sceneData.SceneSubmissionGpuDirectionalShadowOverflowCount = ClampUlongToInt(overflowCount);
            sceneData.SceneSubmissionGpuCompactedShadowMeshletCount =
                sceneData.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount;
        }

        private static void CopyCounterArray(uint[] source, int[] destination)
        {
            int count = Math.Min(source.Length, destination.Length);
            for (int i = 0; i < count; i++)
                destination[i] = ClampUIntToInt(source[i]);
            for (int i = count; i < destination.Length; i++)
                destination[i] = 0;
        }

        private static void CopyFloatArray(float[] source, float[] destination)
        {
            int count = Math.Min(source.Length, destination.Length);
            for (int i = 0; i < count; i++)
                destination[i] = source[i];
            for (int i = count; i < destination.Length; i++)
                destination[i] = 0.0f;
        }

        private static ulong Sum(uint[] values)
        {
            ulong sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return sum;
        }

        private static void ApplyCompletedSceneSubmissionValidation(
            SceneRenderingData sceneData,
            SceneSubmissionValidationSnapshot validation)
        {
            if (!sceneData.SceneSubmissionValidationCompareCpuGpuLists)
            {
                sceneData.SceneSubmissionValidationValid = 0;
                sceneData.SceneSubmissionValidationStatus = string.Empty;
                sceneData.SceneSubmissionValidationCpuOpaqueCount = 0;
                sceneData.SceneSubmissionValidationGpuOpaqueCount = 0;
                sceneData.SceneSubmissionValidationComparedSampleCount = 0;
                sceneData.SceneSubmissionValidationMismatchCount = 0;
                sceneData.SceneSubmissionValidationSampleLimit = 0;
                sceneData.SceneSubmissionValidationFirstMismatch = string.Empty;
                return;
            }

            sceneData.SceneSubmissionValidationValid = validation.Valid;
            sceneData.SceneSubmissionValidationStatus = validation.Status;
            sceneData.SceneSubmissionValidationCpuOpaqueCount = validation.CpuOpaqueCount;
            sceneData.SceneSubmissionValidationGpuOpaqueCount = validation.GpuOpaqueCount;
            sceneData.SceneSubmissionValidationComparedSampleCount = validation.ComparedSampleCount;
            sceneData.SceneSubmissionValidationMismatchCount = validation.MismatchCount;
            sceneData.SceneSubmissionValidationSampleLimit = validation.SampleLimit;
            sceneData.SceneSubmissionValidationFirstMismatch = validation.FirstMismatch;
        }

        private static int ClampUIntToInt(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static uint SaturatingAdd(uint left, uint right)
        {
            ulong sum = (ulong)left + right;
            return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
        }

        private static int ClampUlongToInt(ulong value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void ApplyCompletedFoliageCounters(SceneRenderingData sceneData, FoliageCounterSnapshot counters)
        {
            if (counters.Valid == 0)
                return;

            sceneData.FoliageVisibleClusterCount = checked((int)counters.VisibleClusterCount);
            sceneData.FoliageCulledClusterCount = checked((int)counters.CulledClusterCount);
            sceneData.FoliageLod0VisibleCount = checked((int)counters.Lod0VisibleCount);
            sceneData.FoliageLod1VisibleCount = checked((int)counters.Lod1VisibleCount);
            sceneData.FoliageLod2VisibleCount = checked((int)counters.Lod2VisibleCount);
            sceneData.FoliageHiZTestedCount = checked((int)counters.HiZTestedCount);
            sceneData.FoliageHiZRejectedCount = checked((int)counters.HiZRejectedCount);
            sceneData.FoliageVisibleMeshletDrawCount = checked((int)counters.VisibleMeshletDrawCount);
            sceneData.FoliageDdgiSampleCount =
                checked((int)(counters.VisibleClusterCount + counters.VisibleMeshletDrawCount));
            sceneData.FoliageMeshletDrawOverflowCount = checked((int)counters.MeshletDrawOverflowCount);
            sceneData.FoliageFarImpostorVisibleCount = checked((int)counters.FarImpostorVisibleCount);
            sceneData.FoliageDensityRejectedCount = checked(
                (int)counters.DensityRejectedCount);
            sceneData.FoliageOverflowCount =
                checked(sceneData.FoliageOverflowCount + sceneData.FoliageMeshletDrawOverflowCount);
        }

        public void Resize(int width, int height)
        {
            _lifetime.ThrowIfDisposalStarted();
            _lifetime.RequestSwapchainRecreation();
            if (width <= 0 || height <= 0 || _lifetime.FrameInProgress)
                return;

            _lifetime.ObserveSwapchainRecreationAttempt(
                RecreateSwapchain());

            // Update camera aspect ratio if camera is provided
            // (Camera aspect ratio should be updated by the caller)
        }

        private void ApplyGiCausticExtentTransitionAfterDeviceIdle(
            Extent2D nextSceneExtent)
        {
            GiCausticExtentTransition transition =
                _giCaustic.DisableForIncompatibleExtentAfterDeviceIdle(
                    nextSceneExtent);
            if (!transition.Changed)
                return;

            _renderTargets?.ReleaseGiCausticTargetsAfterDeviceIdle();
            _meshPipeline?.DisableGiCausticReceiverAfterDeviceIdle(
                transition.Reason);
            _advancedGiAdmission.PublishGraphModes(
                _advancedGiAdmission.GraphModes with
                {
                    Caustics = GiCausticMode.Off
                });
            ProductionRenderPipelineDeclaration.Instance.DeclarePassResources(
                _renderGraph,
                _advancedGiAdmission.GraphModes,
                Settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled);
            _renderGraph.RemovePassesAfterDeviceIdle(
            [
                GiCausticGpuPassNames.Task,
                GiCausticGpuPassNames.Trace,
                GiCausticGpuPassNames.CacheBuild,
                GiCausticGpuPassNames.Resolve,
                GiCausticGpuPassNames.Composite
            ]);
            _renderGraph.UnregisterResourcesAfterDeviceIdle(
            [
                RenderGraphResourceId.GiCausticTasks,
                RenderGraphResourceId.GiCausticPhotons,
                RenderGraphResourceId.GiCausticCache,
                RenderGraphResourceId.GiCausticScratch,
                RenderGraphResourceId.GiCausticReceiverPayload,
                RenderGraphResourceId.GiCausticRadiance,
                RenderGraphResourceId.GiCausticMoments,
                RenderGraphResourceId.GiCausticScreenFrameConstants
            ]);
        }

        private bool PrepareNearFieldResidualGenerationAfterDeviceIdle(
            Extent2D nextSceneExtent)
        {
            NearFieldResidualRecreationPreparation preparation =
                _nearFieldResidual
                    .PrepareTargetRecreationAfterDeviceIdle(nextSceneExtent);
            ApplyNearFieldResidualPublication(preparation.Publication);
            return preparation.ReplacementPrepared;
        }

        private void CompleteNearFieldResidualGenerationAfterTargetRecreate()
        {
            ulong completedFenceValue =
                ObserveAllGraphicsSubmissionsCompletedAfterDeviceIdle();
            ApplyNearFieldResidualPublication(
                _nearFieldResidual.CompleteTargetRecreation(
                    completedFenceValue,
                    _ddgiFrameSerial));
        }

        private ulong ObserveAllGraphicsSubmissionsCompletedAfterDeviceIdle()
        {
            _submissionOwnership.ObserveAllSubmittedCompleted();
            ulong completedFenceValue = _completedGraphicsFrameFenceValue;
            for (int frameIndex = 0;
                 frameIndex < RenderingConstants.FramesInFlight;
                 frameIndex++)
            {
                completedFenceValue = Math.Max(
                    completedFenceValue,
                    _submittedGraphicsFrameFenceValues[frameIndex]);
            }

            _completedGraphicsFrameFenceValue = completedFenceValue;
            return completedFenceValue;
        }

        private void ApplyNearFieldResidualPublication(
            in NearFieldResidualPublication publication)
        {
            if (!publication.Changed)
                return;

            if (publication.Executable)
            {
                SimpleDdgiNearFieldResidualRenderTargetGeneration targets =
                    publication.Targets ?? throw new InvalidOperationException(
                        "An executable C5 publication requires a target generation.");
                (_renderTargets ?? throw new InvalidOperationException(
                        "C5 publication requires render targets."))
                    .PublishNearFieldResidualGeneration(targets);
                _meshPipeline?.PublishNearFieldDirectSourceGeneration(
                    publication.PipelineConfiguration);
                _forwardPlusPass?.PublishNearFieldDirectSourceGeneration(
                    new ForwardNearFieldDirectSourceAttachmentBinding(
                        targets.DirectSource,
                        targets.ReceiverPayload,
                        targets.TraceRasterDepth,
                        publication.PipelineConfiguration));
                return;
            }

            _forwardPlusPass?.PublishNearFieldDirectSourceGeneration(null);
            // Suppression must also remove the generation from the render
            // graph before another frame records. Otherwise graph-planned
            // barriers can acquire a newer GPU reference after the generation
            // transaction has already captured its retirement fence.
            _renderTargets?
                .SuspendNearFieldResidualGenerationPublication();
            if (!publication.DisableFeature)
                return;

            if (publication.ReleaseUnmanagedTargets)
            {
                _renderTargets?
                    .ReleaseNearFieldResidualTargetsAfterDeviceIdle();
            }

            _meshPipeline?.DisableNearFieldDirectSourceAfterDeviceIdle(
                publication.Reason);
            _advancedGiAdmission.PublishGraphModes(
                _advancedGiAdmission.GraphModes with
                {
                    NearFieldResidual =
                    SimpleDdgiNearFieldResidualMode.Off,
                    NearFieldProfile = default
                });

            // At startup no pass inventory exists yet; the graph-mode update
            // above is sufficient for fail-closed construction.
            if (_forwardPlusPass is null)
                return;

            // Rewrite shared pass usage before removing the optional C5
            // inventory so ForwardPlus no longer names the released MRTs.
            ProductionRenderPipelineDeclaration.Instance.DeclarePassResources(
                _renderGraph,
                _advancedGiAdmission.GraphModes,
                Settings.GlobalIllumination.SimpleDdgiSampledAtlasEnabled);
            var c5PassNames = new List<string>(
                publication.FilterIterationCount + 7)
            {
                SimpleDdgiNearFieldResidualGpuPassNames.Reset,
                SimpleDdgiNearFieldResidualGpuPassNames.Prepare,
                SimpleDdgiNearFieldResidualGpuPassNames.Classify,
                SimpleDdgiNearFieldResidualGpuPassNames.Trace,
                SimpleDdgiNearFieldResidualGpuPassNames.Temporal,
                SimpleDdgiNearFieldResidualGpuPassNames.Finalize,
                SimpleDdgiNearFieldResidualGpuPassNames.FrequencySeparation,
                SimpleDdgiNearFieldResidualGpuPassNames.Composite
            };
            for (int iteration = 0;
                 iteration < publication.FilterIterationCount;
                 iteration++)
            {
                c5PassNames.Add(
                    SimpleDdgiNearFieldResidualGpuPassNames.FilterIteration(iteration));
            }

            _renderGraph.RemovePassesAfterDeviceIdle(c5PassNames);
            _renderGraph.UnregisterResourcesAfterDeviceIdle(
            [
                RenderGraphResourceId.NearFieldDirectSource,
                RenderGraphResourceId.NearFieldReceiverPayload,
                RenderGraphResourceId.NearFieldPreparedDepthFootprint,
                RenderGraphResourceId.NearFieldPreparedReceiverPayload,
                RenderGraphResourceId.NearFieldPreparedMotion,
                RenderGraphResourceId.NearFieldSourceLuminance,
                RenderGraphResourceId.NearFieldResidualRaw,
                RenderGraphResourceId.NearFieldResidualHistory,
                RenderGraphResourceId.NearFieldResidualMoments,
                RenderGraphResourceId.NearFieldResidualValidity,
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                RenderGraphResourceId.NearFieldResidualHistoryNormals,
                RenderGraphResourceId.NearFieldResidualFilterScratch,
                RenderGraphResourceId.NearFieldResidualTileBuffers,
                RenderGraphResourceId.NearFieldSurfaceTable,
                RenderGraphResourceId.NearFieldActiveTilesAndIndirectArguments,
                RenderGraphResourceId.NearFieldResidualTraceFrameConstants
            ]);
        }

        private void EnsureRenderTargetProfile()
        {
            if (_renderTargets == null)
                return;

            bool aoEnabled = Settings.AmbientOcclusion.Enabled;
            float ambientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;
            AmbientOcclusionMode effectiveAmbientOcclusionMode =
                ResolveEffectiveAmbientOcclusionMode();
            AntiAliasingMode aaMode = Settings.AntiAliasing.EffectiveMode;
            bool motionVectorTargetEnabled =
                ResolveSurfaceHistoryConsumers().RequiresMotionVectors();
            int bloomMipCount = Settings.Bloom.MipCount;
            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool weightedOitTargetEnabled = IsWeightedOitTargetEnabled(Settings);
            bool materialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            bool hybridReflectionTargetEnabled =
                ResolveHybridReflectionTargetProvisioning();
            bool forwardAttachmentProfileChanged =
                _lastMaterialTransportProvenanceTargetEnabled !=
                materialTransportProvenanceTargetEnabled ||
                _lastHybridReflectionTargetEnabled !=
                hybridReflectionTargetEnabled;
            DynamicResolutionScaleDecision scaleDecision = ResolveSceneResolutionScaleDecision();
            float effectiveResolutionScale = scaleDecision.CommittedScale;
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, effectiveResolutionScale);
            sceneRenderExtent = PreserveExtentBoundCausticSceneExtent(
                sceneRenderExtent);
            bool featureTargetsChanged =
                _lastAmbientOcclusionTargetEnabled != aoEnabled ||
                MathF.Abs(_lastAmbientOcclusionResolutionScale - ambientOcclusionResolutionScale) > 0.0001f ||
                _lastAmbientOcclusionMode != effectiveAmbientOcclusionMode ||
                _lastAntiAliasingTargetMode != aaMode ||
                _lastMotionVectorTargetEnabled != motionVectorTargetEnabled ||
                _lastTransparencyTargetMode != Settings.Transparency.Mode ||
                _lastBloomTargetMipCount != bloomMipCount ||
                _lastFogTargetEnabled != fogTargetEnabled ||
                _lastMaterialTransportProvenanceTargetEnabled !=
                materialTransportProvenanceTargetEnabled ||
                _lastHybridReflectionTargetEnabled !=
                hybridReflectionTargetEnabled ||
                hybridReflectionTargetEnabled && MathF.Abs(
                    _lastHybridReflectionRayBudgetFraction -
                    Settings.Reflections.RayQueryPixelBudgetFraction) > 0.000001f;
            bool sceneExtentChanged =
                _lastSceneRenderExtent.Width != sceneRenderExtent.Width ||
                _lastSceneRenderExtent.Height != sceneRenderExtent.Height ||
                MathF.Abs(_lastEffectiveResolutionScale - effectiveResolutionScale) > 0.0001f;

            if (!featureTargetsChanged && !sceneExtentChanged)
            {
                return;
            }

            string recreateReason = featureTargetsChanged
                ? "Render feature target change"
                : string.IsNullOrWhiteSpace(scaleDecision.CommitReason)
                    ? "Resolution scale setting"
                    : scaleDecision.CommitReason;

            var changedProfileFields = new List<string>(12);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "aoEnabled",
                _lastAmbientOcclusionTargetEnabled,
                aoEnabled);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "aoScale",
                _lastAmbientOcclusionResolutionScale,
                ambientOcclusionResolutionScale);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "aoMode",
                _lastAmbientOcclusionMode,
                effectiveAmbientOcclusionMode);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "aaMode",
                _lastAntiAliasingTargetMode,
                aaMode);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "motionVectors",
                _lastMotionVectorTargetEnabled,
                motionVectorTargetEnabled);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "transparency",
                _lastTransparencyTargetMode,
                Settings.Transparency.Mode);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "bloomMips",
                _lastBloomTargetMipCount,
                bloomMipCount);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "fog",
                _lastFogTargetEnabled,
                fogTargetEnabled);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "materialProvenance",
                _lastMaterialTransportProvenanceTargetEnabled,
                materialTransportProvenanceTargetEnabled);
            AddRenderTargetProfileChange(
                changedProfileFields,
                "hybridReflections",
                _lastHybridReflectionTargetEnabled,
                hybridReflectionTargetEnabled);
            if (hybridReflectionTargetEnabled)
            {
                AddRenderTargetProfileChange(
                    changedProfileFields,
                    "reflectionRayFraction",
                    _lastHybridReflectionRayBudgetFraction,
                    Settings.Reflections.RayQueryPixelBudgetFraction);
            }
            AddRenderTargetProfileChange(
                changedProfileFields,
                "sceneExtent",
                $"{_lastSceneRenderExtent.Width}x{_lastSceneRenderExtent.Height}",
                $"{sceneRenderExtent.Width}x{sceneRenderExtent.Height}");
            AddRenderTargetProfileChange(
                changedProfileFields,
                "resolutionScale",
                _lastEffectiveResolutionScale,
                effectiveResolutionScale);

            string changedProfileDescription = changedProfileFields.Count == 0
                ? "none"
                : string.Join(", ", changedProfileFields);
            Console.WriteLine(
                $"Render target profile rebuild started: reason='{recreateReason}', " +
                $"changes=[{changedProfileDescription}].");
            long rebuildStart = Stopwatch.GetTimestamp();

            long stageStart = Stopwatch.GetTimestamp();
            RecordDeviceWaitIdle(
                RuntimeStallReason.ResourceResize,
                $"Render target profile rebuild: {recreateReason}",
                _context.WaitIdle);
            _automaticPlanarReflectionManager
                .ReleaseForSwapchainRecreation();
            long waitIdleMicroseconds = ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            ApplyGiCausticExtentTransitionAfterDeviceIdle(sceneRenderExtent);
            long causticMicroseconds = ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            PrepareNearFieldResidualGenerationAfterDeviceIdle(
                sceneRenderExtent);
            long nearFieldPrepareMicroseconds = ElapsedMicroseconds(stageStart);
            motionVectorTargetEnabled =
                ResolveSurfaceHistoryConsumers().RequiresMotionVectors();
            stageStart = Stopwatch.GetTimestamp();
            _renderTargets.Recreate(
                sceneRenderExtent,
                _swapchain.Extent,
                ambientOcclusionResolutionScale,
                bloomMipCount,
                aoEnabled,
                aaMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                weightedOitTargetEnabled,
                materialTransportProvenanceTargetEnabled,
                hybridReflectionTargetEnabled,
                effectiveAmbientOcclusionMode);
            long renderTargetsMicroseconds = ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            if (forwardAttachmentProfileChanged)
            {
                _meshPipeline?.Recreate(
                    RenderTargetManager.SceneColorFormat,
                    _swapchain.DepthFormat);
                _foliagePipeline?.Recreate(
                    RenderTargetManager.SceneColorFormat,
                    RenderTargetManager.MotionVectorFormat,
                    _swapchain.DepthFormat);
            }
            long pipelinesMicroseconds = ElapsedMicroseconds(stageStart);

            stageStart = Stopwatch.GetTimestamp();
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            _hizVisibilityPolicyState.PyramidValid = false;
            long hizMicroseconds = ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            CompleteNearFieldResidualGenerationAfterTargetRecreate();
            long nearFieldCompleteMicroseconds =
                ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            RegisterSceneRenderTextures();
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            long registrationMicroseconds = ElapsedMicroseconds(stageStart);
            stageStart = Stopwatch.GetTimestamp();
            _renderGraph.OnSwapchainRecreated();
            _asyncComputeCoordinator.ResetTimingHistory(
                AsyncComputeTimingResetKind.RenderTargetsOrSwapchain);
            long graphMicroseconds = ElapsedMicroseconds(stageStart);
            _lastAmbientOcclusionTargetEnabled = aoEnabled;
            _lastAmbientOcclusionResolutionScale = ambientOcclusionResolutionScale;
            _lastAmbientOcclusionMode = effectiveAmbientOcclusionMode;
            _lastAntiAliasingTargetMode = aaMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = bloomMipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastMaterialTransportProvenanceTargetEnabled =
                materialTransportProvenanceTargetEnabled;
            _lastHybridReflectionTargetEnabled =
                hybridReflectionTargetEnabled;
            _lastHybridReflectionRayBudgetFraction =
                Settings.Reflections.RayQueryPixelBudgetFraction;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = effectiveResolutionScale;
            _lastRenderTargetRecreateReason = recreateReason;
            Console.WriteLine(
                $"Render target profile rebuild completed: " +
                $"total={ElapsedMicroseconds(rebuildStart) / 1000.0:F3}ms, " +
                $"waitIdle={waitIdleMicroseconds / 1000.0:F3}ms, " +
                $"caustic={causticMicroseconds / 1000.0:F3}ms, " +
                $"nearFieldPrepare={nearFieldPrepareMicroseconds / 1000.0:F3}ms, " +
                $"targets={renderTargetsMicroseconds / 1000.0:F3}ms, " +
                $"pipelines={pipelinesMicroseconds / 1000.0:F3}ms, " +
                $"hiz={hizMicroseconds / 1000.0:F3}ms, " +
                $"nearFieldComplete={nearFieldCompleteMicroseconds / 1000.0:F3}ms, " +
                $"registration={registrationMicroseconds / 1000.0:F3}ms, " +
                $"graph={graphMicroseconds / 1000.0:F3}ms.");
        }

        private static void AddRenderTargetProfileChange<T>(
            List<string> changes,
            string name,
            T previous,
            T current)
        {
            if (!EqualityComparer<T>.Default.Equals(previous, current))
                changes.Add($"{name}:{previous}->{current}");
        }

        private void InsertInterFrameSharedResourceDependency(CommandBuffer commandBuffer)
        {
            // Several long-lived renderer buffers and images are intentionally shared between
            // frames-in-flight. Queue submission order supplies execution order, but those shared
            // writes still require an explicit memory dependency before the next frame consumes or
            // overwrites them. Keep this at the start of every primary graphics command buffer.
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                DstStageMask = PipelineStageFlags2.AllCommandsBit,
                DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void TransitionSwapchainImage(CommandBuffer cmd, ImageLayout newLayout)
        {
            var vk = _context.Api;
            ImageLayout oldLayout = _swapchain.GetImageLayout(_imageIndex);

            if (oldLayout == newLayout)
                return;

            GetTransitionMasks(
                oldLayout,
                newLayout,
                out PipelineStageFlags2 srcStage,
                out AccessFlags2 srcAccess,
                out PipelineStageFlags2 dstStage,
                out AccessFlags2 dstAccess);

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStage,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStage,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchain.Images[_imageIndex],
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
            _swapchain.SetImageLayout(_imageIndex, newLayout);
        }

        private static void GetTransitionMasks(
            ImageLayout oldLayout,
            ImageLayout newLayout,
            out PipelineStageFlags2 srcStage,
            out AccessFlags2 srcAccess,
            out PipelineStageFlags2 dstStage,
            out AccessFlags2 dstAccess)
        {
            switch (oldLayout)
            {
                case ImageLayout.Undefined:
                case ImageLayout.PresentSrcKhr:
                    srcStage = PipelineStageFlags2.None;
                    srcAccess = AccessFlags2.None;
                    break;
                case ImageLayout.ColorAttachmentOptimal:
                    srcStage = PipelineStageFlags2.ColorAttachmentOutputBit;
                    srcAccess = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    srcStage = PipelineStageFlags2.TransferBit;
                    srcAccess = AccessFlags2.TransferReadBit;
                    break;
                default:
                    srcStage = PipelineStageFlags2.AllCommandsBit;
                    srcAccess = AccessFlags2.MemoryReadBit;
                    break;
            }

            switch (newLayout)
            {
                case ImageLayout.ColorAttachmentOptimal:
                    dstStage = PipelineStageFlags2.ColorAttachmentOutputBit;
                    dstAccess = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit;
                    break;
                case ImageLayout.PresentSrcKhr:
                    dstStage = PipelineStageFlags2.None;
                    dstAccess = AccessFlags2.None;
                    break;
                case ImageLayout.TransferSrcOptimal:
                    dstStage = PipelineStageFlags2.TransferBit;
                    dstAccess = AccessFlags2.TransferReadBit;
                    break;
                default:
                    dstStage = PipelineStageFlags2.AllCommandsBit;
                    dstAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
                    break;
            }
        }

        private bool RecreateSwapchain()
        {
            _lifetime.EnsureSwapchainRecreationAllowed();

            if (!_swapchain.RecreateSwapchain(() =>
                {
                    RecordDeviceWaitIdle(
                        RuntimeStallReason.ResourceResize,
                        "Swapchain recreate",
                        _context.WaitIdle);

                    // The old swapchain image is no longer needed after the
                    // recorded copy, but the readback buffer must be finalized
                    // before resize destroys/replaces presentation resources.
                    if (_lifetime.DeviceLost)
                    {
                        _screenshotReadbackManager.FailAll(
                            "Renderer screenshot capture was cancelled because the device was lost during swapchain recreation.",
                            includeQueuedRequests: false);
                        _linearHdrReadbackManager.FailAll(
                            "Linear HDR capture was cancelled because the device was lost during swapchain recreation.",
                            includeQueuedRequests: false);
                    }
                    else
                    {
                        _screenshotReadbackManager.CompleteAllAfterDeviceIdle();
                        _linearHdrReadbackManager.CompleteAllAfterDeviceIdle();
                    }

                    _automaticPlanarReflectionManager
                        .ReleaseForSwapchainRecreation();
                }))
            {
                return false;
            }

            _submissionOwnership.ResetAfterDeviceIdle(
                checked((int)_swapchain.ImageCount));
            _sync.EnsureRenderFinishedSemaphoreCapacity(_swapchain.ImageCount);
            float sceneResolutionScale = ResolveSceneResolutionScale();
            bool hybridReflectionTargetEnabled =
                ResolveHybridReflectionTargetProvisioning();
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, sceneResolutionScale);
            sceneRenderExtent = PreserveExtentBoundCausticSceneExtent(
                sceneRenderExtent);
            ApplyGiCausticExtentTransitionAfterDeviceIdle(sceneRenderExtent);
            PrepareNearFieldResidualGenerationAfterDeviceIdle(
                sceneRenderExtent);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            _hizVisibilityPolicyState.PyramidValid = false;
            _renderTargets?.Recreate(
                sceneRenderExtent,
                _swapchain.Extent,
                Settings.AmbientOcclusion.ResolutionScale,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.AntiAliasing.EffectiveMode,
                ResolveSurfaceHistoryConsumers().RequiresMotionVectors(),
                IsFogTargetEnabled(Settings),
                IsWeightedOitTargetEnabled(Settings),
                IsMaterialTransportProvenanceTargetEnabled(Settings),
                hybridReflectionTargetEnabled,
                ResolveEffectiveAmbientOcclusionMode());
            CompleteNearFieldResidualGenerationAfterTargetRecreate();
            _bindlessHeap.RegisterTexture(
                BindlessIndex.DepthTexture,
                _renderTargets!.SceneDepth.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            RegisterSceneRenderTextures();
            _lastAmbientOcclusionTargetEnabled = Settings.AmbientOcclusion.Enabled;
            _lastAmbientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;
            _lastAmbientOcclusionMode = ResolveEffectiveAmbientOcclusionMode();
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled =
                ResolveSurfaceHistoryConsumers().RequiresMotionVectors();
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = IsFogTargetEnabled(Settings);
            _lastMaterialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            _lastHybridReflectionTargetEnabled =
                hybridReflectionTargetEnabled;
            _lastHybridReflectionRayBudgetFraction =
                Settings.Reflections.RayQueryPixelBudgetFraction;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Swapchain resize";
            _meshPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _foliagePipeline?.Recreate(RenderTargetManager.SceneColorFormat, RenderTargetManager.MotionVectorFormat,
                _swapchain.DepthFormat);
            _compositePipeline?.Recreate(_swapchain.SurfaceFormat);
            _ldrCompositePipeline?.Recreate(RenderTargetManager.LdrSceneColorFormat);
            _weightedOitCompositePipeline?.Recreate(RenderTargetManager.SceneColorFormat);
            _skyboxPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _directionalShadowResources?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _spotShadowAtlas?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _pointShadowCubemapArray?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _environmentManager?.Register(_bindlessHeap);
            _environmentManager?.RegisterReflectionProbeFallback(_bindlessHeap);
            _reflectionProbeManager?.Register(_bindlessHeap);
            _simpleDdgiVolumeManager?.Register(_bindlessHeap);
            _renderGraph.OnSwapchainRecreated();
            _asyncComputeCoordinator.ResetTimingHistory(
                AsyncComputeTimingResetKind.RenderTargetsOrSwapchain);
            return true;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private static long ElapsedMicroseconds(
            long startTimestamp,
            long endTimestamp)
        {
            if (startTimestamp <= 0 || endTimestamp <= startTimestamp)
                return 0;
            return checked((long)Math.Round(
                (endTimestamp - startTimestamp) * 1_000_000.0 /
                Stopwatch.Frequency));
        }

        private void RecordDeviceWaitIdle(RuntimeStallReason reason, string description, Action wait)
        {
            if (wait == null)
                throw new ArgumentNullException(nameof(wait));

            long waitStart = Stopwatch.GetTimestamp();
            wait();
            _stallTracker.Record(reason, ElapsedMicroseconds(waitStart), description);
        }

        /// <summary>
        /// Completes every submitted frame that can still observe the shared
        /// update-after-bind descriptor set. This is a targeted fence wait,
        /// not a device-wide idle: unrelated queues and future work remain
        /// untouched. The current frame fence is still signalled here because
        /// terminal submission resets it only after command recording ends.
        /// </summary>
        private ulong WaitForSimpleDdgiBindlessDescriptorReaders()
        {
            ulong completedFenceValue = _completedGraphicsFrameFenceValue;
            try
            {
                for (int frameIndex = 0;
                     frameIndex < RenderingConstants.FramesInFlight;
                     frameIndex++)
                {
                    _sync.WaitForFence(frameIndex);
                    _submissionOwnership.ObserveContextCompleted(frameIndex);
                    completedFenceValue = Math.Max(
                        completedFenceValue,
                        _submittedGraphicsFrameFenceValues[frameIndex]);
                }
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault(
                    "Failed while completing Simple DDGI bindless descriptor readers.",
                    exception.Result);
                throw;
            }

            _completedGraphicsFrameFenceValue = Math.Max(
                _completedGraphicsFrameFenceValue,
                completedFenceValue);
            return _completedGraphicsFrameFenceValue;
        }

        private static Extent2D CreateHiZExtent(Extent2D swapchainExtent)
        {
            return new Extent2D
            {
                Width = Math.Max(1u, swapchainExtent.Width / 2u),
                Height = Math.Max(1u, swapchainExtent.Height / 2u)
            };
        }

        private float ResolveSceneResolutionScale()
        {
            return ResolveSceneResolutionScaleDecision().CommittedScale;
        }

        private DynamicResolutionScaleDecision ResolveSceneResolutionScaleDecision()
        {
            long frameMicroseconds = _lastDiagnostics.GpuTimingValid != 0
                ? _lastDiagnostics.GpuFrameMicroseconds
                : _lastDiagnostics.CpuTotalDrawSceneMicroseconds;
            DynamicResolutionScaleDecision decision =
                _dynamicResolutionScaleController.Resolve(
                    Settings,
                    frameMicroseconds);
            if (!_lifetime.InitializationSucceeded ||
                !_advancedGiAdmission.GraphModes.UsesCausticWorldCache)
            {
                return decision;
            }

            // C4 still owns an immutable extent-bound cache. C5 uses a
            // replacement generation and therefore follows committed dynamic
            // resolution changes without freezing the scene extent.
            return new DynamicResolutionScaleDecision(
                decision.RequestedScale,
                _lastEffectiveResolutionScale,
                CommittedScaleChanged: false,
                CommitReason: string.Empty);
        }

        private Extent2D PreserveExtentBoundCausticSceneExtent(
            Extent2D proposed)
        {
            if (!_lifetime.InitializationSucceeded ||
                !_advancedGiAdmission.GraphModes.UsesCausticWorldCache ||
                _lastSceneRenderExtent.Width == 0u ||
                _lastSceneRenderExtent.Height == 0u)
            {
                return proposed;
            }

            return _lastSceneRenderExtent;
        }

        private static Extent2D CreateSceneRenderExtent(Extent2D swapchainExtent, float resolutionScale)
        {
            if (swapchainExtent.Width == 0 || swapchainExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainExtent), "Swapchain extent must be non-zero.");

            float scale = float.IsFinite(resolutionScale) ? Math.Clamp(resolutionScale, 0.5f, 1.0f) : 1.0f;
            return new Extent2D
            {
                Width = Math.Max(1u, (uint)MathF.Ceiling(swapchainExtent.Width * scale)),
                Height = Math.Max(1u, (uint)MathF.Ceiling(swapchainExtent.Height * scale))
            };
        }

        private static bool IsFogTargetEnabled(RenderSettings settings)
        {
            return settings.Fog.Enabled && settings.Fog.Mode != FogMode.Disabled;
        }

        private static bool IsWeightedOitTargetEnabled(RenderSettings settings)
        {
            return settings.Transparency.Enabled &&
                   settings.Transparency.Mode == TransparencyMode.WeightedBlendedOit;
        }

        internal static bool IsMaterialTransportProvenanceTargetEnabled(
            RenderSettings settings)
        {
            return settings.GlobalIllumination.DebugView ==
                   GlobalIlluminationDebugView.MaterialTransportHitProvenance;
        }

        internal static bool IsHybridReflectionTargetEnabled(
            RenderSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return settings.Reflections.Enabled && settings.Reflections.Mode is
                ReflectionMode.StaticProbesAndSsr or
                ReflectionMode.StaticProbesAndPlanar or
                ReflectionMode.HybridRayQuery;
        }

        private bool ResolveHybridReflectionTargetProvisioning()
        {
            // The forward attachment ABI is intentionally monotonic. Tearing
            // it down on a scene switch would destroy and synchronously
            // rebuild the complete mesh-pipeline bank, and can contend with
            // optional background pipeline compilation. Keeping the superset
            // profile resident makes later scene switches allocation-only.
            _hybridReflectionTargetProvisioned |=
                IsHybridReflectionTargetEnabled(Settings);
            return _hybridReflectionTargetProvisioned;
        }

        private SurfaceHistoryConsumer ResolveSurfaceHistoryConsumers() =>
            SurfaceHistoryPolicy.Resolve(
                Settings,
                _advancedGiAdmission.GraphModes.UsesNearFieldHiZResidual,
                directionalCsmTemporalActive:
                Settings.Shadows.EffectiveDirectionalCsmTemporalEnabled,
                directionalRaySoftActive:
                Settings.Shadows.RequestedDirectionalShadowMode ==
                DirectionalShadowMode.RayQuerySoft,
                reflectionActive: IsHybridReflectionTargetEnabled(Settings),
                simpleDdgiReceiverCacheActive:
                Settings.GlobalIllumination.SimpleDdgiReceiverCacheMode ==
                    SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                ambientOcclusionGtaoActive:
                Settings.AmbientOcclusion.Enabled &&
                ResolveEffectiveAmbientOcclusionMode() ==
                    AmbientOcclusionMode.Gtao,
                variableRateShadingActive:
                _context.FragmentShadingRateSupported &&
                Settings.Raster.VariableRateShadingMode ==
                    VariableRateShadingMode.Auto);

        private bool HasIncompatibleVariableRateShadingForwardOutput()
        {
            AdvancedGiRenderGraphModes modes =
                _advancedGiAdmission.GraphModes;
            bool forwardMrtNearField =
                modes.UsesNearFieldHiZResidual &&
                modes.NearFieldProfile.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.ForwardMrt;
            return IsMaterialTransportProvenanceTargetEnabled(Settings) ||
                   modes.UsesCausticWorldCache ||
                   forwardMrtNearField ||
                   IsHybridReflectionTargetEnabled(Settings);
        }

        private AmbientOcclusionMode ResolveEffectiveAmbientOcclusionMode() =>
            AmbientOcclusionPass.ResolveEffectiveMode(
                Settings.AmbientOcclusion.Mode,
                _gtaoRuntimeSupported);

        private bool EvaluateGtaoRuntimeSupport()
        {
            const FormatFeatureFlags storageSampled =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.StorageImageBit;
            return HasFormatFeatures(
                       RenderTargetManager.GtaoRadianceFormat,
                       storageSampled |
                       FormatFeatureFlags.SampledImageFilterLinearBit) &&
                   HasFormatFeatures(
                       RenderTargetManager.GtaoGeometryHistoryFormat,
                       storageSampled);
        }

        private void RegisterBloomTextures()
        {
            if (_renderTargets == null)
                return;

            for (int i = 0; i < _renderTargets.BloomMipCount; i++)
            {
                _bindlessHeap.RegisterTexture(
                    BindlessIndex.BloomMipTextureBase + i,
                    _renderTargets.BloomMipChain[i].View,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            }
        }

        private void RegisterSceneRenderTextures()
        {
            if (_renderTargets == null)
                return;

            _bindlessHeap.RegisterTexture(
                BindlessIndex.DepthTexture,
                _renderTargets.SceneDepth.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HdrSceneColorTexture,
                _renderTargets.SceneColor.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.FoggedSceneColorTexture,
                _renderTargets.FoggedSceneColor.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            ImageView opaqueSceneColorSnapshotView =
                _textureManager.GetTextureView(
                    _textureManager.DefaultBlackTexture);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.OpaqueSceneColorSnapshotTexture,
                opaqueSceneColorSnapshotView,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            RegisterAmbientOcclusionTextures();
            RegisterGlobalIlluminationTextures();
            RegisterWeightedOitTextures();
            RegisterAntiAliasingTextures();
            RegisterBloomTextures();
        }

        private void RegisterWeightedOitTextures()
        {
            if (_renderTargets == null)
                return;

            _bindlessHeap.RegisterTexture(
                BindlessIndex.WeightedOitAccumulationTexture,
                _renderTargets.WeightedOitAccumulation.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.WeightedOitRevealageTexture,
                _renderTargets.WeightedOitRevealage.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterAmbientOcclusionTextures()
        {
            if (_renderTargets == null)
                return;

            if (!Settings.AmbientOcclusion.Enabled && _textureManager.DefaultWhiteTexture.IsValid)
            {
                ImageView whiteView = _textureManager.GetTextureView(_textureManager.DefaultWhiteTexture);
                _bindlessHeap.RegisterTexture(
                    BindlessIndex.AmbientOcclusionRawTexture,
                    whiteView,
                    _bindlessHeap.ScreenSampler,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);

                _bindlessHeap.RegisterTexture(
                    BindlessIndex.AmbientOcclusionBlurredTexture,
                    whiteView,
                    _bindlessHeap.ScreenSampler,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);
                _bindlessHeap.RegisterTexture(
                    BindlessIndex.GtaoFilteredTexture,
                    whiteView,
                    _bindlessHeap.ScreenSampler,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);
                _bindlessHeap.RegisterTexture(
                    BindlessIndex.GtaoDebugTexture,
                    whiteView,
                    _bindlessHeap.ScreenSampler,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);
                return;
            }

            _bindlessHeap.RegisterTexture(
                BindlessIndex.AmbientOcclusionRawTexture,
                _renderTargets.AmbientOcclusionRaw.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.AmbientOcclusionBlurredTexture,
                _renderTargets.AmbientOcclusionBlurred.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.GtaoFilteredTexture,
                _renderTargets.GtaoFiltered.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.GtaoDebugTexture,
                _renderTargets.GtaoSpatialScratch.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterGlobalIlluminationTextures()
        {
            if (_renderTargets == null)
                return;

            _bindlessHeap.RegisterTexture(
                BindlessIndex.MaterialTransportProvenanceTexture,
                _renderTargets.MaterialTransportProvenance.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterAntiAliasingTextures()
        {
            if (_renderTargets == null)
                return;

            _bindlessHeap.RegisterTexture(
                BindlessIndex.LdrSceneColorTexture,
                _renderTargets.LdrSceneColor.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SmaaEdgesTexture,
                _renderTargets.SmaaEdges.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SmaaBlendWeightsTexture,
                _renderTargets.SmaaBlendWeights.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.MotionVectorTexture,
                _renderTargets.MotionVectors.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.TaaHistoryTexture,
                _renderTargets.TaaHistoryA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private sealed class ReflectionProbeCompletionValueProvider :
            IReflectionProbeCompletionValueProvider
        {
            private ulong _completionValue;

            public void SetFrameSerial(ulong frameSerial)
            {
                _completionValue = frameSerial > ulong.MaxValue -
                    (ulong)FramesInFlight - 1UL
                        ? ulong.MaxValue
                        : frameSerial + (ulong)FramesInFlight + 1UL;
            }

            public ulong GetCompletionValue(int frameIndex) => _completionValue;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            bool completed = _lifetime.DrainDisposal(
                CreateResourceDisposalPlan,
                () => Settings.QualityPresetChanging -=
                    OnQualityPresetChanging);
            if (completed)
            {
                System.Diagnostics.Debug.WriteLine(
                    "VulkanRenderer disposed.");
            }
        }

        private StagedDisposalPlan CreateResourceDisposalPlan()
        {
            const string DeviceIdle = "device-idle";
            var steps =
                new List<StagedDisposalStep>(64);
            var terminalDependencies =
                new List<string>(64);

            void AddResourceStage(
                string name,
                Action dispose,
                params string[] additionalDependencies)
            {
                var dependencies =
                    new string[
                        additionalDependencies.Length +
                        1];
                dependencies[0] = DeviceIdle;
                additionalDependencies.CopyTo(
                    dependencies,
                    1);
                steps.Add(
                    new StagedDisposalStep(
                        name,
                        dispose,
                        dependencies));
                terminalDependencies.Add(name);
            }

            steps.Add(
                new StagedDisposalStep(
                    "progressive-startup-drain",
                    () =>
                    {
                        try
                        {
                            _productionInitializationTask?.GetAwaiter()
                                .GetResult();
                        }
                        catch
                        {
                            // Startup failure is surfaced through the host;
                            // disposal still owns every partially created
                            // Vulkan resource after the task has stopped.
                        }
                        try
                        {
                            _scenePreparationTask?.GetAwaiter()
                                .GetResult();
                        }
                        catch
                        {
                            // Cancellation or preparation failure is observed
                            // by the host. The important shutdown invariant is
                            // that no preparation callback is still touching
                            // resources when the device-idle stage begins.
                        }
                        try
                        {
                            // Post-first-present pipeline jobs are intentionally
                            // not part of scene preparation. They still own pass
                            // and VkDevice state, so drain them before either
                            // DeviceWaitIdle or render-graph teardown can race
                            // publication/destruction.
                            _giPipelineCacheService?.CompilationScheduler
                                .WaitForAll();
                        }
                        catch (OperationCanceledException)
                        {
                            // Pending jobs may be cancelled by an already-started
                            // cache shutdown; native calls that entered the driver
                            // have completed before WaitForAll returns.
                        }
                    }));
            steps.Add(
                new StagedDisposalStep(
                    DeviceIdle,
                    () =>
                    {
                        _lifetime.RecordDisposalDeviceIdleResult(
                            _context.Api.DeviceWaitIdle(
                            _context.Device));
                    },
                    "progressive-startup-drain"));
            AddResourceStage(
                "screenshot-capture-resolution",
                ResolveScreenshotCapturesForDisposal);
            AddResourceStage(
                "linear-hdr-capture-resolution",
                ResolveLinearHdrCapturesForDisposal);
            AddResourceStage(
                "screenshot-readback-manager",
                _screenshotReadbackManager.Dispose,
                "screenshot-capture-resolution");
            AddResourceStage(
                "linear-hdr-readback-manager",
                _linearHdrReadbackManager.Dispose,
                "linear-hdr-capture-resolution");

            AddResourceStage(
                "reflection-probe-passes",
                () =>
                {
                    _reflectionProbePublishPass?.Dispose();
                    _reflectionProbePrefilterPass?.Dispose();
                    _reflectionProbeCapturePass?.Dispose();
                    _reflectionProbePublishPass = null;
                    _reflectionProbePrefilterPass = null;
                    _reflectionProbeCapturePass = null;
                });

            // The generation owner releases C5 descriptor/buffer state and
            // every graph-owned image bank while the graph still owns those
            // targets. Terminal device idle is the sole shutdown guarantee;
            // ordinary resize uses fence polling instead.
            AddResourceStage(
                "simple-ddgi-near-field-residual-coordinator",
                _nearFieldResidual.Dispose);

            AddResourceStage(
                "hybrid-reflection-runtime",
                () =>
                {
                    _hybridReflectionRuntime?.Dispose();
                    _hybridReflectionRuntime = null;
                });
            AddResourceStage(
                "render-graph",
                _renderGraph.Cleanup,
                "simple-ddgi-near-field-residual-coordinator",
                "hybrid-reflection-runtime");
            AddResourceStage(
                "gi-pipeline-cache",
                () =>
                {
                    _giPipelineCacheService?.Dispose();
                    _giPipelineCacheService = null;
                },
                "render-graph");
            AddResourceStage(
                "gpu-timestamps",
                _gpuTimestamps.Dispose);
            AddResourceStage(
                "diagnostics-buffer",
                _diagnosticsBuffer.Dispose);
            AddResourceStage(
                "meshlet-physical-residency",
                () => _meshletPhysicalResidencyResources?.Dispose());
            AddResourceStage(
                "directional-shadow-resources",
                () =>
                    _directionalShadowResources
                        ?.Dispose());
            AddResourceStage(
                "directional-shadow-history-resources",
                () => _directionalShadowHistoryResources?.Dispose(),
                "render-graph");
            AddResourceStage(
                "spot-shadow-atlas",
                () => _spotShadowAtlas?.Dispose());
            AddResourceStage(
                "point-shadow-cubemap-array",
                () =>
                    _pointShadowCubemapArray
                        ?.Dispose());
            AddResourceStage(
                "environment-manager",
                () => _environmentManager?.Dispose());
            AddResourceStage(
                "ies-photometric-profile-manager",
                () =>
                {
                    if (ReferenceEquals(
                            _lightManager.PhotometricProfiles,
                            _iesPhotometricProfileManager))
                    {
                        _lightManager.PhotometricProfiles = null;
                    }

                    _iesPhotometricProfileManager.Dispose();
                });
            AddResourceStage(
                "reflection-probe-manager",
                () =>
                    _reflectionProbeManager
                        ?.Dispose());
            AddResourceStage(
                "automatic-planar-reflection-manager",
                () => _automaticPlanarReflectionManager?.Dispose(),
                "render-graph");
            AddResourceStage(
                "ddgi-mutation-journal",
                _ddgiInvalidation.Dispose);
            AddResourceStage(
                "simple-ddgi-frame-coordinator",
                () => _simpleDdgiFrames = null);
            AddResourceStage(
                "ddgi-emissive-table-cache",
                () => _ddgiEmissiveTransport?.ResetSceneTracking());
            AddResourceStage(
                "ddgi-emissive-source-buffer",
                () =>
                {
                    _ddgiEmissiveTransport?.Dispose();
                    _ddgiEmissiveTransport = null;
                });
            AddResourceStage(
                "simple-ddgi-receiver-feedback-coordinator",
                () =>
                {
                    _simpleDdgiReceiverFeedback?.Dispose();
                    _simpleDdgiReceiverFeedback = null;
                });
            AddResourceStage(
                "simple-ddgi-guiding-runtime",
                () =>
                {
                    _simpleDdgiGuidingFrameCoordinator?.Dispose();
                    _simpleDdgiGuidingFrameCoordinator = null;
                    _simpleDdgiGuidingSourceCacheSidecar?.Dispose();
                    _simpleDdgiGuidingSourceCacheSidecar = null;
                    _simpleDdgiGuidingRuntime?.Dispose();
                    _simpleDdgiGuidingRuntime = null;
                });
            AddResourceStage(
                "gi-caustic-frame-coordinator",
                _giCaustic.Dispose,
                "render-graph");
            AddResourceStage(
                "advanced-gi-transient-buffer-arena",
                () =>
                {
                    _advancedGiTransientBufferArena?.Dispose();
                    _advancedGiTransientBufferArena = null;
                },
                "simple-ddgi-receiver-feedback-coordinator",
                "simple-ddgi-guiding-runtime",
                "simple-ddgi-near-field-residual-coordinator");
            AddResourceStage(
                "simple-ddgi-light-tree-resources",
                () =>
                    _simpleDdgiLightTreeResources
                        ?.Dispose());
            AddResourceStage(
                "simple-ddgi-volume-manager",
                () =>
                    _simpleDdgiVolumeManager
                        ?.Dispose());
            AddResourceStage(
                "far-field-clipmap-manager",
                () =>
                    _farFieldClipmapManager
                        ?.Dispose());
            AddResourceStage(
                "auto-exposure-manager",
                () =>
                    _autoExposureManager?.Dispose());
            AddResourceStage(
                "smaa-resources",
                () => _smaaResources?.Dispose());
            AddResourceStage(
                "hiz-depth-pyramid",
                () => _hizDepthPyramid?.Dispose());
            AddResourceStage(
                "render-targets",
                () => _renderTargets?.Dispose(),
                "render-graph");

            AddResourceStage(
                "mesh-pipeline",
                () => _meshPipeline?.Dispose(),
                "gi-pipeline-cache");
            AddResourceStage(
                "ray-scene-descriptor-bank",
                () =>
                {
                    _raySceneDescriptorBank?.Dispose();
                    _raySceneDescriptorBank = null;
                },
                "render-graph",
                "mesh-pipeline");
            AddResourceStage(
                "acceleration-structure-manager",
                () =>
                    _accelerationStructureManager
                        ?.Dispose(),
                "ray-scene-descriptor-bank");
            AddResourceStage(
                "ddgi-foliage-proxy-manager",
                () =>
                    _ddgiFoliageProxyManager
                        ?.Dispose(),
                "acceleration-structure-manager");
            AddResourceStage(
                "compute-pipeline",
                () => _computePipeline?.Dispose());
            AddResourceStage(
                "skinning-pass",
                () => _skinningPass?.Dispose());
            AddResourceStage(
                "ddgi-foliage-proxy-generation-pass",
                () =>
                    _ddgiFoliageProxyGenerationPass
                        ?.Dispose());
            AddResourceStage(
                "gpu-particle-reset-pass",
                () =>
                    _gpuParticleResetPass?.Dispose());
            AddResourceStage(
                "gpu-particle-simulate-pass",
                () =>
                    _gpuParticleSimulatePass
                        ?.Dispose());
            AddResourceStage(
                "gpu-particle-sort-pass",
                () =>
                    _gpuParticleSortPass?.Dispose());
            AddResourceStage(
                "foliage-cull-pass",
                () => _foliageCullPass?.Dispose());
            AddResourceStage(
                "skinning-manager",
                () => _skinningManager?.Dispose());
            AddResourceStage(
                "particle-system-manager",
                () =>
                    _particleSystemManager?.Dispose());
            AddResourceStage(
                "gpu-particle-runtime-manager",
                () =>
                    _gpuParticleRuntimeManager
                        ?.Dispose());
            AddResourceStage(
                "foliage-manager",
                () => _foliageManager?.Dispose());
            AddResourceStage(
                "foliage-pipeline",
                () => _foliagePipeline?.Dispose());
            AddResourceStage(
                "composite-pipeline",
                () => _compositePipeline?.Dispose());
            AddResourceStage(
                "ldr-composite-pipeline",
                () =>
                    _ldrCompositePipeline?.Dispose());
            AddResourceStage(
                "weighted-oit-composite-pipeline",
                () =>
                    _weightedOitCompositePipeline
                        ?.Dispose());
            AddResourceStage(
                "skybox-pipeline",
                () => _skyboxPipeline?.Dispose());
            AddResourceStage(
                "particle-pipeline",
                () => _particlePipeline?.Dispose());

            if (_ownsDependencies)
            {
                AddResourceStage(
                    "staging-ring",
                    _stagingRing.Dispose);
                AddResourceStage(
                    "swapchain",
                    _swapchain.Dispose);
                AddResourceStage(
                    "command-buffer-manager",
                    _cmd.Dispose);
                AddResourceStage(
                    "synchronization-manager",
                    _sync.Dispose);
                AddResourceStage(
                    "light-manager",
                    _lightManager.Dispose);
                // Resource owners retire descriptor registrations while the
                // heap and backing allocator remain alive. The dependency
                // chain also closes the deferred-deletion queue only after
                // every pending fenced texture retirement has been enqueued.
                AddResourceStage(
                    "model-upload-service",
                    () =>
                        (_modelUploadService as IDisposable)
                        ?.Dispose());
                AddResourceStage(
                    "material-manager",
                    _materialManager.Dispose,
                    "model-upload-service");
                AddResourceStage(
                    "mesh-manager",
                    _meshManager.Dispose,
                    "material-manager");
                AddResourceStage(
                    "texture-pending-retirements",
                    _textureManager.FlushPendingTextureRetirements,
                    "mesh-manager");
                AddResourceStage(
                    "deferred-deleter",
                    _deleter.Dispose,
                    "texture-pending-retirements");
                AddResourceStage(
                    "texture-manager",
                    _textureManager.Dispose,
                    "deferred-deleter");

                const string ResourceOwners =
                    "resource-owner-barrier";
                steps.Add(
                    new StagedDisposalStep(
                        ResourceOwners,
                        static () => { },
                        terminalDependencies.ToArray()));
                steps.Add(
                    new StagedDisposalStep(
                        "bindless-heap",
                        _bindlessHeap.Dispose,
                        ResourceOwners));
                steps.Add(
                    new StagedDisposalStep(
                        "buffer-manager",
                        _bufferManager.Dispose,
                        "bindless-heap"));
                steps.Add(
                    new StagedDisposalStep(
                        "vulkan-context",
                        _context.Dispose,
                        "buffer-manager"));
            }

            return new StagedDisposalPlan(steps);
        }

        private void ResolveScreenshotCapturesForDisposal()
        {
            if (_lifetime.DisposalDeviceIdleResult ==
                Result.Success &&
                !_lifetime.DeviceLost)
            {
                _screenshotReadbackManager
                    .CompleteAllAfterDeviceIdle();
                return;
            }

            _screenshotReadbackManager.FailAll(
                $"Renderer screenshot capture was cancelled during renderer disposal because DeviceWaitIdle returned {_lifetime.DisposalDeviceIdleResult}.",
                includeQueuedRequests: true);
        }

        private void ResolveLinearHdrCapturesForDisposal()
        {
            if (_lifetime.DisposalDeviceIdleResult ==
                Result.Success &&
                !_lifetime.DeviceLost)
            {
                _linearHdrReadbackManager
                    .CompleteAllAfterDeviceIdle();
                return;
            }

            _linearHdrReadbackManager.FailAll(
                $"Linear HDR capture was cancelled during renderer disposal because DeviceWaitIdle returned {_lifetime.DisposalDeviceIdleResult}.",
                includeQueuedRequests: true);
        }
    }

    /// <summary>
    /// Exception for Vulkan API errors.
    /// </summary>
    public class VulkanException : Exception
    {
        public Result Result { get; }

        public VulkanException(string message, Result result) : base($"{message}: {result}")
        {
            Result = result;
        }

        public VulkanException(string message) : base(message)
        {
            Result = Result.ErrorUnknown;
        }
    }
}
