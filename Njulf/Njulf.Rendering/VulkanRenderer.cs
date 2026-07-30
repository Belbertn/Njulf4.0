using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Njulf.Assets;
using Njulf.Assets.Cooked;
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
    public unsafe class VulkanRenderer : IRenderer, IRendererDebugTools, IDisposable
    {
        private const long LocalShadowGpuCompactionRecordThresholdMicroseconds = 750;
        private const int LocalShadowGpuCompactionWorkThreshold = 8192;

        internal static IReadOnlyList<string> ProductionRenderPassOrder => ProductionRenderPipelineDeclaration.Instance.PassOrder;

        private readonly IWindow _window;
        private readonly VulkanContext _context;
        private readonly SwapchainManager _swapchain;
        private readonly SynchronizationManager _sync;
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
        private readonly OverlayDrawDataSource _overlayDrawData = new();
        private readonly RendererDiagnosticsBuffer _diagnosticsBuffer;
        private readonly GpuTimestampRecorder _gpuTimestamps;
        private readonly ParticleSystemManager _particleSystemManager = new();
        private readonly UploadBudgetTracker _uploadBudgetTracker = new();
        private readonly RuntimeStallTracker _stallTracker = new();
        private readonly RenderBudgetEvaluator _budgetEvaluator = new();
        private readonly GiWarningEvaluator _giWarningEvaluator = new();
        private readonly PerformanceSampleWindow _globalIlluminationCpuTimingWindow = new(120);
        private readonly AsyncComputeScheduler _asyncComputeScheduler = new();
        private readonly AsyncComputeTimingPolicy _asyncComputeTimingPolicy = new();
        private readonly List<DeferredAsyncSubmission> _deferredAsyncSubmissions = new();
        private readonly AsyncComputeTimingFrame?[] _asyncComputeTimingFrames = new AsyncComputeTimingFrame?[FramesInFlight];
        private readonly bool _ownsDependencies;
        // Captured after pipeline creation, when the effective shader assets have been loaded.
        // Keeping this renderer-local prevents a later on-disk asset change from being reported
        // as though it had affected the already-created Vulkan pipeline.
        private string _captureShaderBundleHash = "unavailable:shader-bundle-not-initialized";
        // Capture metadata is renderer-owned rather than application-owned so snapshots always
        // retain a coherent frame/camera serial even when callers use a minimal ICamera.
        private ulong _captureSceneRevision = ulong.MaxValue;
        private ulong _captureSceneLoadFrameSerial;
        private ulong _captureCameraCutSerial;
        private HiZDepthPyramid? _hizDepthPyramid;
        private RenderTargetManager? _renderTargets;
        private DirectionalShadowResources? _directionalShadowResources;
        private SpotShadowAtlas? _spotShadowAtlas;
        private PointShadowCubemapArray? _pointShadowCubemapArray;
        private EnvironmentManager? _environmentManager;
        private ReflectionProbeManager? _reflectionProbeManager;
        private DdgiProbeVolumeManager? _ddgiProbeVolumeManager;
        private SimpleDdgiVolumeManager? _simpleDdgiVolumeManager;
        private FarFieldClipmapManager? _farFieldClipmapManager;
        private DdgiGatherTileManager? _ddgiGatherTileManager;
        private readonly CameraRelativeDdgiClipmapController _cameraRelativeDdgiClipmaps = new();
        private readonly DdgiLocalVolumeSlotAllocator _ddgiLocalVolumeSlots = new();
        private AccelerationStructureManager? _accelerationStructureManager;
        private AutoExposureManager? _autoExposureManager;
        private SmaaResources? _smaaResources;
        private SkinningManager _skinningManager = null!;
        private GpuParticleRuntimeManager _gpuParticleRuntimeManager = null!;
        private readonly LocalShadowSelector _localShadowSelector = new();
        private readonly GPUSpotShadow[] _spotShadowScratch = new GPUSpotShadow[32];
        private readonly GPUPointShadow[] _pointShadowScratch = new GPUPointShadow[4];
        private readonly GPULocalLightShadowIndex[] _localShadowIndexScratch = new GPULocalLightShadowIndex[LightManager.MaxLights];
        private DdgiFrameLayout _lastDdgiFrameLayout = DdgiFrameLayout.Empty;
        private readonly List<BoundingBox> _ddgiDirtyBoundsScratch = new();
        private readonly List<DdgiDirtyRegion> _ddgiDirtyRegionScratch = new();
        private readonly Dictionary<RenderObject, DdgiTrackedRenderObject> _ddgiTrackedRenderObjects = new();
        private readonly List<RenderObject> _ddgiTrackedRenderObjectRemovalScratch = new();
        private readonly Dictionary<ParticleEffectInstance, DdgiTrackedVfxProxy> _ddgiTrackedVfxProxies = new();
        private readonly List<ParticleEffectInstance> _ddgiTrackedVfxProxyRemovalScratch = new();
        private const int MaxDdgiEmissiveSourceCount = 256;
        private const int MaximumDdgiEmissiveRuntimeRecordScans = 262144;
        private static readonly ulong DdgiEmissiveSourceStride = (ulong)Marshal.SizeOf<GPUDdgiEmissiveSource>();
        private readonly GPUDdgiEmissiveSource[] _ddgiEmissiveSourceScratch = new GPUDdgiEmissiveSource[MaxDdgiEmissiveSourceCount];
        private readonly float[] _ddgiEmissiveSourceImportanceScratch = new float[MaxDdgiEmissiveSourceCount];
        private readonly DdgiEmissiveTableCache _ddgiEmissiveTableCache = new(MaxDdgiEmissiveSourceCount);
        private BufferHandle _ddgiEmissiveSourceBuffer = BufferHandle.Invalid;
        private ulong _ddgiEmissiveSourceBufferSize;
        private bool _ddgiEmissiveSourceBufferContentValid;
        private ulong _ddgiEmissiveSourceUploadCount;
        private int _ddgiEmissiveSourceCount;
        private uint _ddgiEmissiveSourceRevision;
        private ulong _lastDdgiEmissiveSourceSignature;
        private DdgiEmissiveTriangleTableStats _ddgiEmissiveTriangleTableStats;
        private int _ddgiEmissiveSkippedSkinnedObjectCount;
        private double _ddgiEmissiveSkippedSkinnedImportance;
        private int _ddgiEmissiveExcludedCandidateCount;
        private double _ddgiEmissiveExcludedImportance;
        private int _ddgiEmissiveRuntimeRecordScanCount;
        private int _ddgiTrackingFrame;
        private bool _hasLastDdgiCameraPosition;
        private Vector3 _lastDdgiCameraPosition;
        private bool _hasLastDdgiProjectionMatrix;
        private Matrix4x4 _lastDdgiProjectionMatrix;
        private ulong _lastDdgiLightSignature;
        private ulong _lastDdgiProbeVolumeSignature;
        private Light[] _lastDdgiLights = Array.Empty<Light>();
        private bool _hasDdgiDynamicSignature;
        private bool _hasSimpleDdgiDirtySignature;
        private ulong _lastSimpleDdgiLightSignature;
        private ulong _lastSimpleDdgiEmissiveSignature;
        private ulong _lastSimpleDdgiDynamicGeometrySignature;
        private bool _lastReflectionProbeGiReady;
        private uint _lastReflectionProbeSimpleDirtyReasonFlags;
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
        private SsgiCompositePipeline? _ssgiCompositePipeline;
        private SkyboxPipeline _skyboxPipeline = null!;
        private ParticlePipeline _particlePipeline = null!;
        private DdgiSchedulePass? _ddgiSchedulePass;
        private SimpleDdgiTracePass? _simpleDdgiTracePass;
        private SimpleDdgiRelocateClassifyPass? _simpleDdgiRelocateClassifyPass;
        private SimpleDdgiTransportPass? _simpleDdgiTransportPass;
        private SimpleDdgiBlendPass? _simpleDdgiBlendPass;
        private SkinningPass _skinningPass = null!;
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

        // State
        private int _currentFrame = 0;
        private uint _allocatorFrameIndex;
        private uint _temporalSampleIndex;
        private ulong _ddgiFrameSerial;
        private uint _imageIndex;
        private CommandBuffer _currentCommandBuffer;
        private bool _isInitialized = false;
        private readonly object _disposeLock = new();
        private bool _disposeStarted;
        private bool _disposeCompleted;
        private StagedDisposalPlan? _disposalPlan;
        private Result _disposalDeviceIdleResult =
            Result.ErrorUnknown;
        private bool _frameInProgress;
        private bool _swapchainNeedsRecreate;
        private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
        private RenderBudgetSnapshot _lastBudgetSnapshot = RenderBudgetSnapshot.Empty;
        private SceneRenderingData? _lastSceneData;
        private readonly DebugDrawList _debugDraw = new();
        private readonly ScreenshotCaptureService _screenshotCaptureService = new();
        private readonly ScreenshotReadbackManager _screenshotReadbackManager;
        private readonly LinearHdrCaptureService _linearHdrCaptureService = new();
        private readonly LinearHdrReadbackManager _linearHdrReadbackManager;
        private readonly RenderDocCaptureService _renderDocCaptureService = new();
        private GpuMeshletCounters _completedGpuCounters;
        private DdgiForwardEstimateCounters _completedDdgiForwardEstimateCounters;
        private DdgiInvestigationCounters _completedDdgiInvestigationCounters;
        private DirectionalShadowReceiverCounters _completedDirectionalShadowReceiverCounters = DirectionalShadowReceiverCounters.Empty;
        private FarFieldMaterialV2Counters _completedFarFieldMaterialV2Counters;
        private MaterialGiGpuCounters _completedMaterialGiCounters;
        private GpuParticleCounterSnapshot _completedGpuParticleCounters;
        private FoliageCounterSnapshot _completedFoliageCounters;
        private SceneSubmissionCounterSnapshot _completedSceneSubmissionCounters;
        private SceneSubmissionCounterSnapshot _completedForwardVisibilityCounters;
        private SceneSubmissionValidationSnapshot _completedSceneSubmissionValidation;
        private bool _ddgiGpuSchedulerFallbackLatched;
        private string _ddgiGpuSchedulerFallbackReason = string.Empty;
        private string _ddgiGpuSchedulerLoggedFallbackReason = string.Empty;
        private int _ddgiGpuSchedulerValidationFailureCount;
        private int _ddgiGpuSchedulerFallbackStableFrameCount;
        private readonly DdgiDiagnosticWarningTracker _ddgiDiagnosticWarningTracker = new();
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
        private long _lastQueueSubmitMicroseconds;
        private long _lastAsyncComputeSubmitMicroseconds;
        private long _lastPresentMicroseconds;
        private bool _asyncComputePlanRecordedThisFrame;
        private int _asyncComputeSubmittedGraphicsSegmentsThisFrame;
        private int _asyncComputeSubmittedComputeSegmentsThisFrame;
        private readonly List<AsyncComputeTimelineWait> _asyncComputeWaitsThisFrame = new();
        private int _asyncComputeOwnershipTransferCountThisFrame;
        private int _asyncComputePlannedReleaseBarrierCountThisFrame;
        private int _asyncComputePlannedAcquireBarrierCountThisFrame;
        private int _asyncComputeEmittedReleaseBarrierCountThisFrame;
        private int _asyncComputeEmittedAcquireBarrierCountThisFrame;
        private long _asyncComputeBarrierRecordMicrosecondsThisFrame;
        private ulong _asyncComputeTransferredBytesThisFrame;
        private int _asyncComputeTransferredImageSubresourcesThisFrame;
        private ulong _nextAsyncComputeTimelineValue = 1;
        private int _nextAutoTimingProbePath;
        private AsyncComputeMode? _lastAsyncComputeTimingMode;
        private AsyncComputePlan? _frameAsyncComputePlan;
        private AsyncComputeSubmissionPlan? _frameAsyncComputeSubmissionPlan;
        private readonly AsyncComputeRecoverablePlanRetryGate _asyncComputeRecoverablePlanRetryGate = new();
        private bool _asyncComputeEmergencyFallbackLatched;
        private string _asyncComputeLastFallbackReason = string.Empty;
        private int _asyncComputeValidationFallbackCount;
        private bool _deviceLost;
        private bool _frameSubmissionFaulted;
        private string _frameSubmissionFaultReason = string.Empty;
        private bool _swapchainImageTransitionedThisFrame;
        private bool _lastAmbientOcclusionTargetEnabled = true;
        private float _lastAmbientOcclusionResolutionScale = 0.5f;
        private AntiAliasingMode _lastAntiAliasingTargetMode = AntiAliasingMode.SmaaMedium;
        private bool _lastMotionVectorTargetEnabled = true;
        private TransparencyMode _lastTransparencyTargetMode = TransparencyMode.SortedAlphaBlend;
        private int _lastBloomTargetMipCount = 6;
        private bool _lastFogTargetEnabled = true;
        private bool _lastGlobalIlluminationTargetEnabled = true;
        private bool _lastMaterialTransportProvenanceTargetEnabled;
        private float _lastGlobalIlluminationResolutionScale = 0.5f;
        private Extent2D _lastSceneRenderExtent;
        private float _lastEffectiveResolutionScale = 1.0f;
        private readonly DynamicResolutionScaleController _dynamicResolutionScaleController = new();
        private string _lastRenderTargetRecreateReason = string.Empty;

        // Scene state
        private Color _clearColor = Color.CornflowerBlue;
        public RendererDiagnostics LastDiagnostics => _lastDiagnostics;
        public RenderBudgetSnapshot LastBudgetSnapshot => _lastBudgetSnapshot;
        public DeviceRequirementReport? SelectedDeviceRequirementReport => _context.SelectedDeviceRequirementReport;
        public MemoryHeapBudgetSnapshot CurrentMemoryHeapBudget => _context.GetMemoryHeapBudgetSnapshot();
        public DebugDrawList DebugDraw => _debugDraw;
        public DebugOverlaySettings DebugOverlays => Settings.Debug;
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
        public RenderSettings Settings { get; } = new();
        /// <summary>
        /// Optional application-supplied active scenario identifier included in performance
        /// captures. Set this before drawing the scenario; when it is not supplied, captures
        /// deliberately report that the scenario is unavailable instead of inferring one from
        /// scene content or a camera pose.
        /// </summary>
        public string CaptureScenario { get; set; } = string.Empty;
        public int DebugObjectSnapshotCount => _lastSceneData?.ObjectDebugSnapshots.Count ?? 0;
        public void QueueOverlayDrawData(OverlayDrawData? drawData)
        {
            ThrowIfDisposalStarted();
            _overlayDrawData.Set(drawData);
        }

        public int CreateOverlayTexture(ReadOnlySpan<byte> pixels, uint width, uint height, string? name = null)
        {
            ThrowIfDisposalStarted();
            TextureHandle handle = _textureManager.CreateTexture(width, height, Format.R8G8B8A8Unorm, debugName: name ?? "Overlay Texture");
            try { _textureManager.UploadTextureData(handle, pixels, width, height, Format.R8G8B8A8Unorm); return _textureManager.GetBindlessTextureIndex(handle); }
            catch { _textureManager.DestroyTexture(handle); throw; }
        }

        public void RequestScreenshot(string? outputPath = null)
        {
            ThrowIfDisposalStarted();
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
            ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowScreenshots)
                return false;

            _linearHdrCaptureService.Request(outputPath);
            return true;
        }

        public LinearHdrCaptureResult GetLinearHdrCaptureResult(string outputPath)
        {
            ThrowIfDisposalStarted();
            return _linearHdrCaptureService.GetResult(outputPath);
        }

        public void RequestRenderDocCapture()
        {
            ThrowIfDisposalStarted();
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowRenderDocCapture)
                return;

            _renderDocCaptureService.RequestCapture();
            if (_renderDocCaptureService.CaptureRequested)
                Settings.Diagnostics.GpuMeshletCountersEnabled = true;
        }

        public string ExportPerformanceSnapshot(string? directory = null)
        {
            ThrowIfDisposalStarted();
            string targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(AppContext.BaseDirectory, "PerformanceSnapshots")
                : directory;

            return new PerformanceSnapshotWriter().Write(targetDirectory, _lastDiagnostics, _lastBudgetSnapshot);
        }

        public bool TryFindObjectByName(string name, out int objectIndex)
        {
            ThrowIfDisposalStarted();
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
            ThrowIfDisposalStarted();
            SceneRenderingData? data = _lastSceneData;
            if (id != Guid.Empty && data?.HasCpuSnapshots == true)
                foreach (ObjectDebugSnapshot snapshot in data.ObjectDebugSnapshots)
                    if (snapshot.EntityId == id) { objectIndex = snapshot.ObjectIndex; return true; }
            objectIndex = -1;
            return false;
        }

        public bool TryInspectObject(int index, out SelectedObjectInspection inspection)
        {
            ThrowIfDisposalStarted();
            SceneRenderingData? sceneData = _lastSceneData;
            if (index < 0 || sceneData == null || !sceneData.HasCpuSnapshots || index >= sceneData.ObjectDebugSnapshots.Count)
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
            bool ownsDependencies)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _swapchain = swapchainManager ?? throw new ArgumentNullException(nameof(swapchainManager));
            _sync = syncManager ?? throw new ArgumentNullException(nameof(syncManager));
            _cmd = cmdManager ?? throw new ArgumentNullException(nameof(cmdManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
            _sceneDataBuilder = sceneDataBuilder ?? throw new ArgumentNullException(nameof(sceneDataBuilder));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
            _modelUploadService = modelUploadService ?? throw new ArgumentNullException(nameof(modelUploadService));
            Settings.QualityPresetChanging += OnQualityPresetChanging;
            OnQualityPresetChanging(Settings.QualityPreset);
            _diagnosticsBuffer = new RendererDiagnosticsBuffer(_context, _bufferManager);
            _screenshotReadbackManager = new ScreenshotReadbackManager(_bufferManager, _screenshotCaptureService);
            _linearHdrReadbackManager = new LinearHdrReadbackManager(_bufferManager, _linearHdrCaptureService);
            _gpuTimestamps = new GpuTimestampRecorder(_context);
            _particleSystemManager = new ParticleSystemManager(_context, _bufferManager, _stagingRing);
            _gpuParticleRuntimeManager = new GpuParticleRuntimeManager(_context, _bufferManager, _stagingRing);
            _foliageManager = new FoliageManager(_context, _bufferManager, _stagingRing, _meshManager, _materialManager);
            _ownsDependencies = ownsDependencies;
        }

        private void OnQualityPresetChanging(RenderQualityPreset preset)
        {
            _materialManager.SetPrimitiveProfileGpuBudgetBytes(
                RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(preset));
        }

        public void Initialize()
        {
            ThrowIfDisposalStarted();
            if (_isInitialized)
                return;

            System.Diagnostics.Debug.WriteLine("Initializing VulkanRenderer...");

            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool ssgiTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
            bool materialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            float sceneResolutionScale = ResolveSceneResolutionScale();
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, sceneResolutionScale);
            RegisterGraphResources();
            bool motionVectorTargetEnabled = NeedsMotionVectors(Settings);
            _renderTargets = new RenderTargetManager(
                _context,
                sceneRenderExtent,
                _swapchain.Extent,
                _swapchain.DepthFormat,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.AmbientOcclusion.ResolutionScale,
                ssgiTargetEnabled,
                Settings.GlobalIllumination.ResolutionScale,
                Settings.AntiAliasing.EffectiveMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                IsWeightedOitTargetEnabled(Settings),
                _renderGraph,
                materialTransportProvenanceTargetEnabled);
            _lastAmbientOcclusionTargetEnabled = Settings.AmbientOcclusion.Enabled;
            _lastAmbientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastGlobalIlluminationTargetEnabled = ssgiTargetEnabled;
            _lastMaterialTransportProvenanceTargetEnabled =
                materialTransportProvenanceTargetEnabled;
            _lastGlobalIlluminationResolutionScale = Settings.GlobalIllumination.ResolutionScale;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Initial render targets";
            _hizDepthPyramid = new HiZDepthPyramid(_context, CreateHiZExtent(sceneRenderExtent));
            _directionalShadowResources = new DirectionalShadowResources(_context, _bufferManager, Settings.Shadows);
            _spotShadowAtlas = new SpotShadowAtlas(_context, _bufferManager, Settings.Shadows);
            _pointShadowCubemapArray = new PointShadowCubemapArray(_context, _bufferManager, Settings.Shadows);
            _environmentManager = new EnvironmentManager(_context, _bufferManager, _textureManager, Settings);
            _reflectionProbeManager = new ReflectionProbeManager(_context, _bufferManager, Settings);
            _ddgiProbeVolumeManager = new DdgiProbeVolumeManager(_context, _bufferManager, Settings);
            _simpleDdgiVolumeManager = new SimpleDdgiVolumeManager(_context, _bufferManager, Settings);
            _ddgiGatherTileManager = new DdgiGatherTileManager(_context, _bufferManager);
            _ddgiEmissiveSourceBuffer = CreateDdgiEmissiveSourceBuffer();
            _accelerationStructureManager = new AccelerationStructureManager(_context, _bufferManager, _meshManager, _materialManager);
            _farFieldClipmapManager = new FarFieldClipmapManager(
                _context,
                _bufferManager,
                Settings,
                _accelerationStructureManager,
                _materialManager);
            _autoExposureManager = new AutoExposureManager(_context, _bufferManager, Settings);
            _skinningManager = new SkinningManager(_context, _bufferManager, _stagingRing, _meshManager);

            // Create pipelines
            CreatePipelines();
            _captureShaderBundleHash = ResolvePerformanceCaptureShaderBundleHash();

            // Initialize render graph with passes
            InitializeRenderGraph();

            // Register static buffers in bindless heap
            RegisterSceneBuffers();
            _sync.EnsureRenderFinishedSemaphoreCapacity(_swapchain.ImageCount);

            _isInitialized = true;
            System.Diagnostics.Debug.WriteLine("VulkanRenderer initialized.");
        }

        private void CreatePipelines()
        {
            System.Diagnostics.Debug.WriteLine("Creating pipelines...");

            // Create mesh pipeline for depth prepass and forward pass
            _meshPipeline = new MeshPipeline(
                _context,
                _bindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _swapchain.DepthFormat,
                Settings);
            _foliagePipeline = new FoliagePipeline(
                _context,
                _bindlessHeap,
                RenderTargetManager.SceneColorFormat,
                RenderTargetManager.MotionVectorFormat,
                _swapchain.DepthFormat,
                Settings);

            // Create compute pipeline for light culling
            _computePipeline = new ComputePipeline(_context, _bindlessHeap);

            _compositePipeline = new CompositePipeline(_context, _bindlessHeap, _swapchain.SurfaceFormat);
            _ldrCompositePipeline = new CompositePipeline(_context, _bindlessHeap, RenderTargetManager.LdrSceneColorFormat);
            _weightedOitCompositePipeline = new WeightedOitCompositePipeline(_context, _bindlessHeap, RenderTargetManager.SceneColorFormat);
            _ssgiCompositePipeline = new SsgiCompositePipeline(
                _context,
                _bindlessHeap,
                RenderTargetManager.SceneColorFormat);
            _skyboxPipeline = new SkyboxPipeline(
                _context,
                _bindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _swapchain.DepthFormat);
            _particlePipeline = new ParticlePipeline(
                _context,
                _bindlessHeap,
                RenderTargetManager.SceneColorFormat,
                _swapchain.DepthFormat);
            _skinningPass = new SkinningPass(_context, _bindlessHeap, _bufferManager, _skinningManager);
            _gpuParticleResetPass = new GpuParticleResetPass(_context, _bindlessHeap, _bufferManager, _gpuParticleRuntimeManager);
            _gpuParticleSimulatePass = new GpuParticleSimulatePass(_context, _bindlessHeap, _bufferManager, _gpuParticleRuntimeManager);
            _gpuParticleSortPass = new GpuParticleSortPass(_context, _bindlessHeap, _bufferManager, _gpuParticleRuntimeManager);
            _gpuParticleResetGraphPass = new GpuParticleResetGraphPass(_context, _swapchain, _bindlessHeap, _gpuParticleResetPass);
            _gpuParticleSimulateGraphPass = new GpuParticleSimulateGraphPass(_context, _swapchain, _bindlessHeap, _gpuParticleSimulatePass);
            _gpuParticleSortGraphPass = new GpuParticleSortGraphPass(_context, _swapchain, _bindlessHeap, _gpuParticleSortPass, _gpuParticleRuntimeManager);
            _foliageCullPass = new FoliageCullPass(_context, _bindlessHeap, _bufferManager, _foliageManager, _foliagePipeline);
            _sceneOpaqueCompactionPass = new SceneOpaqueCompactionPass(_context, _swapchain, _bindlessHeap, _meshPipeline, _bufferManager);
            _forwardVisibilityCompactionPass = new ForwardVisibilityCompactionPass(_context, _swapchain, _bindlessHeap, _meshPipeline, _bufferManager);

            System.Diagnostics.Debug.WriteLine("Pipelines created.");
        }

        private void InitializeRenderGraph()
        {
            System.Diagnostics.Debug.WriteLine("Initializing render graph...");

            bool includeSsgi = Settings.GlobalIllumination.EffectiveUseSsgi;
            ProductionRenderPipelineDeclaration.Instance.DeclarePassResources(_renderGraph, includeSsgi);

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
            _sceneOpaqueCompactionPass.SetDirectionalStaticShadowRefreshQuery(directionalShadowPass.NeedsStaticCacheRefresh);
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
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!, _foliagePipeline, _bufferManager, _foliageManager);
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
                _foliageManager);
            AddPassInstance(motionVectorPass);

            var hizBuildPass = new HiZBuildPass(
                _context, _swapchain, _bindlessHeap, _hizDepthPyramid!, _renderTargets!);
            AddPassInstance(hizBuildPass);

            AddPassInstance(_forwardVisibilityCompactionPass);

            if (includeSsgi)
            {
                var sceneSurfacePass = new SceneSurfacePass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _meshPipeline,
                    _renderTargets!,
                    Settings,
                    _bufferManager);
                AddPassInstance(sceneSurfacePass);
            }

            var ambientOcclusionPass = new AmbientOcclusionPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            AddPassInstance(ambientOcclusionPass);

            var ambientOcclusionBlurPass = new AmbientOcclusionBlurPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            AddPassInstance(ambientOcclusionBlurPass);

            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager, _renderTargets!);
            AddPassInstance(lightCullingPass);

            // Create forward+ rendering pass
            var forwardPass = new ForwardPlusPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!, Settings, _foliagePipeline, _bufferManager, _foliageManager);
            AddPassInstance(forwardPass);

            if (includeSsgi)
            {
                var ssgiTracePass = new SsgiTracePass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _renderTargets!,
                    Settings);
                AddPassInstance(ssgiTracePass);

                var ssgiTemporalPass = new SsgiTemporalPass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _renderTargets!,
                    Settings);
                AddPassInstance(ssgiTemporalPass);

                var ssgiDenoisePass = new SsgiDenoisePass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _renderTargets!,
                    Settings);
                AddPassInstance(ssgiDenoisePass);

            }

            // The composite also presents the forward-written provenance
            // diagnostic in DDGI-only configurations.
            var ssgiCompositePass = new SsgiCompositePass(
                _context,
                _swapchain,
                _bindlessHeap,
                _ssgiCompositePipeline!,
                _renderTargets!,
                Settings);
            AddPassInstance(ssgiCompositePass);

            var farFieldClipmapBakePass = new FarFieldClipmapBakePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _farFieldClipmapManager!);
            AddPassInstance(farFieldClipmapBakePass);

            var simpleDdgiTracePass = new SimpleDdgiTracePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!,
                _accelerationStructureManager!);
            _simpleDdgiTracePass = simpleDdgiTracePass;
            AddPassInstance(simpleDdgiTracePass);

            var simpleDdgiRelocateClassifyPass = new SimpleDdgiRelocateClassifyPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!);
            _simpleDdgiRelocateClassifyPass = simpleDdgiRelocateClassifyPass;
            AddPassInstance(simpleDdgiRelocateClassifyPass);

            var simpleDdgiTransportPass = new SimpleDdgiTransportPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!);
            _simpleDdgiTransportPass = simpleDdgiTransportPass;
            AddPassInstance(simpleDdgiTransportPass);

            var simpleDdgiBlendPass = new SimpleDdgiBlendPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _simpleDdgiVolumeManager!,
                _farFieldClipmapManager!);
            _simpleDdgiBlendPass = simpleDdgiBlendPass;
            AddPassInstance(simpleDdgiBlendPass);

            var ddgiSchedulePass = new DdgiSchedulePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _ddgiProbeVolumeManager!,
                _accelerationStructureManager!);
            _ddgiSchedulePass = ddgiSchedulePass;
            AddPassInstance(ddgiSchedulePass);

            var ddgiTracePass = new DdgiTracePass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _ddgiProbeVolumeManager!,
                _accelerationStructureManager!);
            AddPassInstance(ddgiTracePass);

            var ddgiBlendPass = new DdgiBlendPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _ddgiProbeVolumeManager!,
                _accelerationStructureManager!);
            AddPassInstance(ddgiBlendPass);

            var ddgiRelocateClassifyPass = new DdgiRelocateClassifyPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _ddgiProbeVolumeManager!,
                _accelerationStructureManager!);
            AddPassInstance(ddgiRelocateClassifyPass);

            var ddgiPublishPass = new DdgiPublishPass(
                _context,
                _swapchain,
                _bindlessHeap,
                Settings,
                _ddgiProbeVolumeManager!,
                _accelerationStructureManager!);
            AddPassInstance(ddgiPublishPass);

            var skyboxPass = new SkyboxPass(
                _context, _swapchain, _bindlessHeap, _skyboxPipeline, _renderTargets!, Settings);
            AddPassInstance(skyboxPass);

            var transparentForwardPass = new TransparentForwardPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!);
            AddPassInstance(transparentForwardPass);

            var weightedTransparentPass = new WeightedTransparentPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!);
            AddPassInstance(weightedTransparentPass);

            var weightedOitCompositePass = new WeightedOitCompositePass(
                _context, _swapchain, _bindlessHeap, _weightedOitCompositePipeline, _renderTargets!);
            AddPassInstance(weightedOitCompositePass);

            var particlePass = new ParticlePass(
                _context, _swapchain, _bindlessHeap, _particlePipeline, _bufferManager, _renderTargets!, Settings.Particles);
            AddPassInstance(_gpuParticleResetGraphPass);
            AddPassInstance(_gpuParticleSimulateGraphPass);
            AddPassInstance(_gpuParticleSortGraphPass);
            AddPassInstance(particlePass);

            var debugDrawPass = new DebugDrawPass(
                _context, _swapchain, _bindlessHeap, _bufferManager, _stagingRing, _renderTargets!);
            AddPassInstance(debugDrawPass);

            var fogPass = new FogPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            AddPassInstance(fogPass);

            var autoExposurePass = new AutoExposurePass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings, _autoExposureManager!);
            AddPassInstance(autoExposurePass);

            var bloomPass = new BloomPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            AddPassInstance(bloomPass);

            var toneMapCompositePass = new ToneMapCompositePass(
                _context, _swapchain, _bindlessHeap, _compositePipeline, _ldrCompositePipeline, _renderTargets!, Settings);
            AddPassInstance(toneMapCompositePass);

            var antiAliasingPass = new AntiAliasingPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                () => _smaaResources?.IsReady == true);
            AddPassInstance(antiAliasingPass);
            AddPassInstance(new ImGuiRenderPass(_context, _swapchain, _bindlessHeap, _bufferManager, _stagingRing, _overlayDrawData));
            ProductionRenderPipelineDeclaration.Instance.RegisterPasses(_renderGraph, passInstances, includeSsgi);
            foreach (RenderPassBase asyncCandidate in passInstances.Values.Where(pass => pass.SupportsAsyncCompute))
            {
                if (!AsyncComputePassCatalog.IsProductionCandidate(asyncCandidate.Name))
                {
                    throw new InvalidOperationException(
                        $"Async-capable pass '{asyncCandidate.Name}' has no production async-compute audit classification.");
                }
            }
            ProductionRenderPipelineDeclaration.Instance.ValidatePassOrder(_renderGraph.PassNames, includeSsgi);

            _renderGraph.Initialize();
            System.Diagnostics.Debug.WriteLine("Render graph initialized.");
        }

        private void RegisterGraphResources()
        {
            ProductionRenderPipelineDeclaration.Instance.RegisterResources(
                _renderGraph,
                _swapchain.DepthFormat,
                _swapchain.SurfaceFormat,
                Settings.GlobalIllumination.EffectiveUseSsgi);
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
            RegisterDdgiEmissiveSourceBuffer();

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
            _ddgiProbeVolumeManager!.Register(_bindlessHeap);
            _simpleDdgiVolumeManager!.Register(_bindlessHeap);
            _farFieldClipmapManager!.Register(_bindlessHeap);
            _ddgiGatherTileManager!.Register(_bindlessHeap);
            _accelerationStructureManager!.Register(_bindlessHeap);

            System.Diagnostics.Debug.WriteLine("Scene buffers registered.");
        }

        private BufferHandle CreateDdgiEmissiveSourceBuffer()
        {
            _ddgiEmissiveTableCache.Clear();
            _ddgiEmissiveSourceBufferContentValid = false;
            _ddgiEmissiveSourceBufferSize = checked((ulong)MaxDdgiEmissiveSourceCount * DdgiEmissiveSourceStride);
            return _bufferManager.CreateDeviceBuffer(
                _ddgiEmissiveSourceBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Emissive Source Buffer");
        }

        private void RegisterDdgiEmissiveSourceBuffer()
        {
            if (!_ddgiEmissiveSourceBuffer.IsValid)
                return;

            _bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.DdgiEmissiveSourceBuffer,
                _bufferManager.GetBuffer(_ddgiEmissiveSourceBuffer),
                0,
                _ddgiEmissiveSourceBufferSize);
        }

        public bool BeginFrame()
        {
            ThrowIfDisposalStarted();
            if (!_isInitialized)
                Initialize();

            ThrowIfFrameSubmissionFaulted();

            if (_frameInProgress)
                throw new InvalidOperationException("BeginFrame was called while a frame is already in progress.");

            if (_swapchainNeedsRecreate)
            {
                if (!RecreateSwapchain())
                    return false;

                _swapchainNeedsRecreate = false;
            }

            _stallTracker.BeginFrame();

            // Wait for previous frame to complete
            try
            {
                _sync.WaitForFence(_currentFrame);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault("Failed while waiting for the in-flight frame fence.", exception.Result);
                throw;
            }
            _stallTracker.Record(RuntimeStallReason.FrameFenceWait, _sync.LastFenceWaitMicroseconds, "Frame fence");
            // This is deliberately the same ring slot whose fence was just
            // observed. A screenshot never reads a newer frame's buffer.
            _screenshotReadbackManager.CompleteFrameAfterFence(_currentFrame);
            _linearHdrReadbackManager.CompleteFrameAfterFence(_currentFrame);
            _diagnosticsBuffer.ReadCompletedFrame(_currentFrame);
            _gpuParticleRuntimeManager.ReadCompletedFrame(_currentFrame);
            _foliageManager.ReadCompletedFrame(_currentFrame);
            _ddgiProbeVolumeManager?.ReadCompletedGpuSchedulerCounters(_currentFrame);
            UpdateDdgiGpuSchedulerFallbackStateFromCompletedFrame();
            _sceneOpaqueCompactionPass?.ReadCompletedFrame(_currentFrame);
            _forwardVisibilityCompactionPass?.ReadCompletedFrame(_currentFrame);
            _autoExposureManager?.ReadCompletedFrame(_currentFrame);
            _completedGpuCounters = _diagnosticsBuffer.GetLastCompletedCounters(_currentFrame);
            _completedDdgiForwardEstimateCounters = _diagnosticsBuffer.GetLastCompletedDdgiForwardEstimateCounters(_currentFrame);
            _completedDdgiInvestigationCounters = _diagnosticsBuffer.GetLastCompletedDdgiInvestigationCounters(_currentFrame);
            _completedDirectionalShadowReceiverCounters = _diagnosticsBuffer.GetLastCompletedDirectionalShadowReceiverCounters(_currentFrame);
            _completedFarFieldMaterialV2Counters = _diagnosticsBuffer.GetLastCompletedFarFieldMaterialV2Counters(_currentFrame);
            _completedMaterialGiCounters = _diagnosticsBuffer.GetLastCompletedMaterialGiCounters(_currentFrame);
            _completedGpuParticleCounters = _gpuParticleRuntimeManager.GetLastCompletedCounters(_currentFrame);
            _completedFoliageCounters = _foliageManager.GetLastCompletedCounters(_currentFrame);
            _completedSceneSubmissionCounters = _sceneOpaqueCompactionPass?.GetLastCompletedCounters(_currentFrame) ?? SceneSubmissionCounterSnapshot.Invalid;
            _completedForwardVisibilityCounters = _forwardVisibilityCompactionPass?.GetLastCompletedCounters(_currentFrame) ?? SceneSubmissionCounterSnapshot.Invalid;
            _completedSceneSubmissionValidation = _sceneOpaqueCompactionPass?.GetLastCompletedValidation(_currentFrame) ?? SceneSubmissionValidationSnapshot.Invalid;
            _gpuTimestamps.ReadCompletedFrame(_currentFrame);
            RecordCompletedAsyncComputeTimingFrame(_currentFrame, _gpuTimestamps.LastCompletedSnapshot);

            // Process completed frame deletions
            _deleter.ProcessCompletedFrame(_sync.GetInFlightFence(_currentFrame));

            // The staging ring slot is safe to reuse after the frame fence has completed.
            _stagingRing.BeginFrame(_currentFrame);
            _uploadBudgetTracker.BeginFrame();
            _context.SetAllocatorCurrentFrameIndex(_allocatorFrameIndex++);

            // Acquire next swapchain image
            long acquireStart = Stopwatch.GetTimestamp();
            Result acquireResult = _swapchain.TryAcquireNextImage(
                _sync.GetImageAvailableSemaphore(_currentFrame),
                out _imageIndex);
            _lastAcquireImageMicroseconds = ElapsedMicroseconds(acquireStart);
            _stallTracker.Record(RuntimeStallReason.SwapchainAcquire, _lastAcquireImageMicroseconds, "Acquire next swapchain image");

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                _swapchainNeedsRecreate = true;
                if (RecreateSwapchain())
                    _swapchainNeedsRecreate = false;
                return false;
            }

            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            {
                if (acquireResult == Result.ErrorDeviceLost)
                    MarkFrameSubmissionFault("The Vulkan device was lost while acquiring a swapchain image.", acquireResult);
                throw new VulkanException("Failed to acquire swapchain image", acquireResult);
            }

            _swapchainNeedsRecreate = acquireResult == Result.SuboptimalKhr;

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
            _asyncComputePlanRecordedThisFrame = false;
            _asyncComputeSubmittedGraphicsSegmentsThisFrame = 0;
            _asyncComputeSubmittedComputeSegmentsThisFrame = 0;
            _asyncComputeWaitsThisFrame.Clear();
            _asyncComputeOwnershipTransferCountThisFrame = 0;
            _asyncComputePlannedReleaseBarrierCountThisFrame = 0;
            _asyncComputePlannedAcquireBarrierCountThisFrame = 0;
            _asyncComputeEmittedReleaseBarrierCountThisFrame = 0;
            _asyncComputeEmittedAcquireBarrierCountThisFrame = 0;
            _asyncComputeBarrierRecordMicrosecondsThisFrame = 0;
            _asyncComputeTransferredBytesThisFrame = 0;
            _asyncComputeTransferredImageSubresourcesThisFrame = 0;
            _frameAsyncComputePlan = null;
            _frameAsyncComputeSubmissionPlan = null;
            _deferredAsyncSubmissions.Clear();
            _swapchainImageTransitionedThisFrame = false;
            _lastQueueSubmitMicroseconds = 0;
            _lastAsyncComputeSubmitMicroseconds = 0;
            _frameInProgress = true;
            _gpuTimestamps.BeginFrame(_currentCommandBuffer, _currentFrame, Settings.Debug.AllowGpuTiming);

            return true;
        }

        public void EndFrame()
        {
            ThrowIfDisposalStarted();
            if (!_frameInProgress)
                throw new InvalidOperationException("EndFrame was called without a successful BeginFrame.");

            var vk = _context.Api;

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
            SubmitDeferredAsyncSubmissions();

            // Reset the fence only after all preceding submissions succeeded and immediately
            // before the terminal graphics submit that waits all compute work.
            try
            {
                _sync.ResetFence(_currentFrame);
            }
            catch (VulkanException exception)
            {
                MarkFrameSubmissionFault("Failed to reset the terminal frame fence before graphics submission.", exception.Result);
                throw;
            }
            _stallTracker.Record(RuntimeStallReason.Unknown, _sync.LastFenceResetMicroseconds, "Reset frame fence");

            Semaphore renderFinishedSemaphore = _sync.GetRenderFinishedSemaphoreForImage(_imageIndex);
            var signalSemaphores = stackalloc Semaphore[] { renderFinishedSemaphore };
            var commandBuffers = stackalloc CommandBuffer[] { _currentCommandBuffer };

            int timelineWaitCount = _asyncComputeWaitsThisFrame.Count;
            int waitCount = checked(1 + timelineWaitCount);
            Semaphore* waitSemaphores = stackalloc Semaphore[waitCount];
            PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[waitCount];
            ulong* waitValues = stackalloc ulong[waitCount];
            // A TimelineSemaphoreSubmitInfo accompanies the terminal submit whenever it waits
            // on the async timeline. Its value arrays must cover *all* submit semaphores,
            // including the binary render-finished signal, whose required value is zero.
            ulong* signalValues = stackalloc ulong[1];
            signalValues[0] = 0;
            waitSemaphores[0] = _sync.GetImageAvailableSemaphore(_currentFrame);
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
                AsyncComputeTimelineWait wait = _asyncComputeWaitsThisFrame[i];
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
                    _deviceLost = true;
                Result recoveryResult = TryRecoverFrameFenceAfterTerminalSubmitFailure(
                    waitCount,
                    waitSemaphores,
                    waitStages,
                    waitValues,
                    timelineWaitCount > 0);
                if (recoveryResult == Result.Success)
                    failureReason += " A fence-only recovery submission was queued; rendering is stopped to preserve the acquired image contract.";
                else if (result != Result.ErrorDeviceLost)
                    failureReason += $" Fence recovery submission also failed: {recoveryResult}.";

                MarkFrameSubmissionFault(failureReason, result);
                throw new VulkanException("Failed to submit queue", result);
            }

            // The terminal graphics submit owns both the acquired swapchain
            // image and its readback copy. Do not permit CPU mapping until this
            // exact frame fence has completed on a later reuse of this slot.
            _screenshotReadbackManager.MarkFrameSubmitted(_currentFrame);
            _linearHdrReadbackManager.MarkFrameSubmitted(_currentFrame);

            if (_asyncComputePlanRecordedThisFrame)
            {
                RecordSubmittedAsyncComputeSegment(AsyncComputeQueue.Graphics);
                UpdateAsyncComputeSubmissionDiagnostics();
            }

            FinalizeAsyncComputeTimingFrame(_currentFrame);

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

            // Advance to next frame
            _currentFrame = (_currentFrame + 1) % FramesInFlight;
            _temporalSampleIndex++;
            _ddgiFrameSerial++;
            _sync.AdvanceFrame();
            _frameInProgress = false;

            if (presentResult == Result.ErrorOutOfDateKhr ||
                presentResult == Result.SuboptimalKhr ||
                _swapchainNeedsRecreate)
            {
                _swapchainNeedsRecreate = true;
                if (RecreateSwapchain())
                    _swapchainNeedsRecreate = false;
            }

            RefreshValidationDiagnostics();
            _context.ThrowIfValidationFailure();
        }

        public unsafe void Clear(Color color)
        {
            ThrowIfDisposalStarted();
            EnsureFrameInProgress(nameof(Clear));

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

        public void DrawScene(Scene scene, ICamera camera)
        {
            ThrowIfDisposalStarted();
            EnsureFrameInProgress(nameof(DrawScene));
            long drawSceneStart = Stopwatch.GetTimestamp();

            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            _materialManager.EnsureTextureFanoutReady();

            bool debugEnabled = Settings.Debug.Enabled;
            RenderFeatureIsolationMode isolationMode = Settings.FeatureIsolation;
            bool isolateSkinnedAnimationDebug = Settings.Animation.DebugView == AnimationDebugView.SkinnedObjects;
            bool shadowsAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsShadows(isolationMode);
            bool reflectionsAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsReflections(isolationMode);
            bool animationAllowed = RenderFeatureIsolationPolicy.AllowsAnimation(isolationMode);
            bool particlesAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsParticles(isolationMode);
            // Apply the independently persisted material-transport rollout switch before
            // SceneDataBuilder snapshots revisions or uploads the material buffer. The
            // manager makes an unchanged value a cheap no-op and publishes a transition
            // atomically when the switch changes at runtime.
            _materialManager.SetTransportV2Enabled(
                Settings.GlobalIllumination.EffectiveGiMaterialTransportV2);
            EnsureRenderTargetProfile();
            DebugOverlayMode activeDebugOverlay = debugEnabled ? Settings.Debug.Mode : DebugOverlayMode.None;
            _sceneDataBuilder.CaptureCpuSnapshots = debugEnabled &&
                                                    (Settings.Debug.CpuSnapshotsEnabled ||
                                                    activeDebugOverlay is DebugOverlayMode.ObjectBounds or
                                                        DebugOverlayMode.MeshletBounds or
                                                        DebugOverlayMode.SelectedObject or
                                                        DebugOverlayMode.MaterialInspection or
                                                        DebugOverlayMode.DecalVolumes);
            _debugDraw.Enabled = debugEnabled;
            _debugDraw.MaxLineSegments = Settings.Debug.MaxDebugLineSegments;

            _lightManager.UploadToGPU(_stagingRing, _currentCommandBuffer);
            ulong lightUploadBytes = _lightManager.LastUploadBytes;
            LightFrameSnapshot lightSnapshot = _lightManager.GetFrameSnapshot();
            // The next BeginFrame safely recreates the procedural environment if
            // this authoritative sun snapshot materially changed. Keeping the
            // update here ensures sky, DDGI misses, and directional shadows all
            // derive from the same scene light rather than independent constants.
            _environmentManager?.UpdateProceduralSkyLighting(lightSnapshot);
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
                hasLocalShadows = localShadowSelection.SpotLights.Length > 0 || localShadowSelection.PointLights.Length > 0;
                shadowData = CreateDirectionalShadowData(camera, lightSnapshot, out directionalShadowsEnabled, out shadowedDirectionalLightIndex);
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
            uint forwardDebugViewMode = ResolveForwardDebugViewMode();
            bool geometryDecalsEnabled =
                Settings.Decals.GeometryDecalsEnabled &&
                ShouldRenderGeometryDecals(forwardDebugViewMode);

            // Build and upload scene data using SceneDataBuilder
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
                captureSceneSubmissionValidationLists: Settings.SceneSubmission.ValidationCompareCpuGpuLists,
                gpuLod1DistanceRatio: Settings.SceneSubmission.GpuLod1DistanceRatio,
                gpuLod2DistanceRatio: Settings.SceneSubmission.GpuLod2DistanceRatio);
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
            sceneData.MaterialDetailedTransportHitCount =
                _completedMaterialGiCounters.EstimatedDetailedTransportHitCount;
            sceneData.MaterialCompactTransportHitCount =
                _completedMaterialGiCounters.EstimatedCompactTransportHitCount;
            sceneData.MaterialCorrectnessFallbackHitCount =
                _completedMaterialGiCounters.EstimatedCorrectnessFallbackHitCount;
            sceneData.MaterialFarFieldTransportHitCount =
                _completedMaterialGiCounters.EstimatedFarFieldTransportHitCount;
            sceneData.CaptureSceneName = string.IsNullOrWhiteSpace(scene.Name) ? "unknown-scene" : scene.Name;
            sceneData.CaptureScenario = CaptureScenario;
            if (_captureSceneRevision != sceneData.SceneContentRevision)
            {
                _captureSceneRevision = sceneData.SceneContentRevision;
                _captureSceneLoadFrameSerial = frameSerial;
                _captureCameraCutSerial = 0;
                _giWarningEvaluator.Reset();
            }
            sceneData.CaptureCameraYawRadians = MathF.Atan2(camera.Forward.X, -camera.Forward.Z);
            sceneData.CaptureCameraPitchRadians = MathF.Asin(Math.Clamp(camera.Forward.Y, -1.0f, 1.0f));
            sceneData.CaptureCameraFieldOfViewRadians = camera.FieldOfView;
            sceneData.CaptureCameraNearPlane = camera.NearPlane;
            sceneData.CaptureCameraFarPlane = camera.FarPlane;
            sceneData.CaptureFramesSinceSceneLoad = frameSerial >= _captureSceneLoadFrameSerial
                ? frameSerial - _captureSceneLoadFrameSerial
                : 0;
            sceneData.ActiveFeatureIsolation = isolationMode;
            sceneData.DebugToolingEnabled = debugEnabled;
            sceneData.DebugOverlayMode = activeDebugOverlay;
            sceneData.CpuDebugSnapshotsEnabled = _sceneDataBuilder.CaptureCpuSnapshots;
            sceneData.DebugSelectedObjectIndex = Settings.Debug.SelectedObjectIndex;
            if (sceneData.DebugSelectedObjectIndex >= 0 &&
                sceneData.DebugSelectedObjectIndex < sceneData.ObjectDebugSnapshots.Count)
            {
                sceneData.DebugSelectedObjectName = sceneData.ObjectDebugSnapshots[sceneData.DebugSelectedObjectIndex].Name;
            }
            sceneData.ImageIndex = _imageIndex;
            sceneData.LightCount = lightCount;
            sceneData.DirectionalLightCount = directionalLightCount;
            sceneData.LocalLightCount = localLightCount;
            sceneData.LightUploadBytes = lightUploadBytes;
            UpdateTiledLightDiagnostics(sceneData, lightSnapshot);
            sceneData.UploadedBytes += lightUploadBytes;
            sceneData.SceneSubmissionGpuCompactionEnabled = Settings.SceneSubmission.GpuCompactionEnabled;
            sceneData.SceneSubmissionIndirectMeshletDispatchEnabled = Settings.SceneSubmission.IndirectMeshletDispatchEnabled;
            sceneData.SceneSubmissionGpuLodSelectionEnabled = Settings.SceneSubmission.GpuLodSelectionEnabled;
            sceneData.SceneSubmissionGpuLod1DistanceRatio = Settings.SceneSubmission.GpuLod1DistanceRatio;
            sceneData.SceneSubmissionGpuLod2DistanceRatio = Settings.SceneSubmission.GpuLod2DistanceRatio;
            sceneData.SceneSubmissionGpuShadowCompactionEnabled = Settings.SceneSubmission.GpuShadowCompactionEnabled;
            sceneData.SceneSubmissionGpuShadowLodBias = Settings.SceneSubmission.GpuShadowLodBias;
            sceneData.HiZValidateAgainstLegacyPath = Settings.HiZOcclusion.ValidateAgainstLegacyPath;
            sceneData.SceneSubmissionValidationCompareCpuGpuLists =
                Settings.SceneSubmission.ValidationCompareCpuGpuLists ||
                sceneData.HiZValidateAgainstLegacyPath;
            sceneData.AnimationEnabled = gpuSkinningEnabled && skinningStats.SkinnedObjectCount > 0;
            sceneData.AnimationSkinningMode = gpuSkinningEnabled ? AnimationSkinningMode.GpuCompute : AnimationSkinningMode.Disabled;
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
                sceneData.FoliageCastShadows = shadowsAllowed && Settings.Foliage.Enabled && Settings.Foliage.CastShadows;
                sceneData.FoliageMotionVectorsEnabled = Settings.Foliage.MotionVectorsEnabled;
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
                sceneData.FoliageLocalShadowsEnabled = false;
                sceneData.FoliageGrassShadowDensityScale = 0f;
                sceneData.FoliageMaxLocalShadowedSpotLights = 0;
                sceneData.FoliageMaxLocalShadowedPointLights = 0;
                sceneData.FoliageLocalShadowClusterBudget = 0;
                sceneData.FoliageLocalShadowMeshletDrawBudget = 0;
            }
            sceneData.UploadedBytes += sceneData.ParticleInstanceUploadBytes;
            sceneData.ParticleDdgiSampleCount = Settings.GlobalIllumination.EffectiveUseSimpleDdgi && Settings.GlobalIllumination.SimpleDdgiParticlesEnabled
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
            HiZVisibilityPolicyDecision hiZDecision = PlanHiZVisibility(scene, camera, sceneData.DepthPrePassEnabled, isolateSkinnedAnimationDebug);
            if (hiZDecision.CameraCut)
                _captureCameraCutSerial++;
            sceneData.CaptureCameraCutSerial = _captureCameraCutSerial;
            HiZConsumerDecision hiZConsumers = ResolveHiZConsumers(sceneData, hiZDecision);
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
            sceneData.TransparencyDebugView = Settings.Transparency.DebugView;
            sceneData.TransparentReceiveShadows = Settings.Transparency.ReceiveShadows;
            sceneData.DecalDebugView = Settings.Decals.DebugView;
            sceneData.GeometryDecalsEnabled = geometryDecalsEnabled;
            sceneData.GeometryDecalDepthBias = Settings.Decals.GeometryDepthBias;
            sceneData.GeometryDecalSlopeScaledDepthBias = Settings.Decals.GeometrySlopeScaledDepthBias;
            sceneData.HiZMipCount = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.MipLevels ?? 0u : 0u;
            sceneData.HiZWidth = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Width ?? 0u : 0u;
            sceneData.HiZHeight = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Height ?? 0u : 0u;
            sceneData.ActiveSceneColorTextureIndex = BindlessIndex.HdrSceneColorTexture;
            sceneData.EffectiveExposure = Settings.Exposure;
            sceneData.FogDirectionalInscatteringDirection = ResolveFogDirectionalInscatteringDirection(lightSnapshot);
            sceneData.DebugViewMode = forwardDebugViewMode;
            sceneData.JitterEnabled = jitter.X != 0.0f || jitter.Y != 0.0f ? 1 : 0;
            sceneData.JitterX = jitter.X;
            sceneData.JitterY = jitter.Y;
            if (shadowsAllowed)
            {
                PrepareDirectionalShadows(sceneData, shadowData, directionalShadowsEnabled, shadowedDirectionalLightIndex);
                PrepareLocalShadows(sceneData, localShadowSelection, lightCount);
            }
            _environmentManager?.Upload(_stagingRing, _currentCommandBuffer);
            if (reflectionsAllowed)
                PrepareReflectionProbes(scene, sceneData);
            PrepareAccelerationStructures(scene, sceneData);
            PrepareDdgiProbeVolumes(scene, camera, sceneData, lightSnapshot, hiZDecision.CameraCut);
            BuildDebugOverlayDrawCommands(scene, sceneData);
            sceneData.DebugDrawSnapshot = _debugDraw.Snapshot();
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
            _diagnosticsBuffer.ResetCounters(_currentCommandBuffer, _currentFrame);

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

            SetViewportAndScissor(_currentCommandBuffer);

            // Execute render graph
            sceneData.SecondaryCommandBufferEnabled = Settings.UseSecondaryCommandBuffers ? 1 : 0;
            AsyncComputePlan frameAsyncComputePlan = BuildAsyncComputePlan(sceneData);
            _frameAsyncComputePlan = frameAsyncComputePlan;
            _frameAsyncComputeSubmissionPlan = frameAsyncComputePlan.SubmissionPlan;

            // This is a runtime execution flag, not a request flag.  DDGI setup runs before
            // the graph plan is compiled and may have observed an enabled setting, but a
            // graphics-only or validation-fallback execution must never let DDGI's local
            // barriers assume that a compute submission is going to follow.
            sceneData.DdgiAsyncComputeEnabled = 0;
            if (frameAsyncComputePlan.SubmissionPlan.Accepted && frameAsyncComputePlan.SubmissionPlan.ContainsAsyncCompute)
            {
                if (!ExecuteRenderGraphWithAsyncPlan(
                        frameAsyncComputePlan.SubmissionPlan,
                        sceneData,
                        out string fallbackReason))
                {
                    frameAsyncComputePlan = CreateGraphicsFallbackAsyncPlan(frameAsyncComputePlan, fallbackReason);
                    _frameAsyncComputePlan = frameAsyncComputePlan;
                    _frameAsyncComputeSubmissionPlan = frameAsyncComputePlan.SubmissionPlan;
                }
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
                sceneData.DdgiAsyncComputeEnabled = IsDdgiAsyncComputeActuallyEnabled(frameAsyncComputePlan) ? 1 : 0;
            }

            // SceneColor still contains the linear, pre-exposure result here.
            // Tone mapping only sampled it; capture before any subsequent
            // client draw can mutate renderer-owned targets.
            RecordLinearHdrSceneColorCapture();
            _hizVisibilityPolicyState.PyramidValid = sceneData.HiZBuildEnabled;
            if (MeshletDiagnosticCountersActive)
                ApplyCompletedGpuCounters(sceneData, _completedGpuCounters);
            ApplyCompletedSsgiCounters(sceneData, _completedGpuCounters);
            ApplyCompletedDdgiForwardEstimateCounters(sceneData, _completedDdgiForwardEstimateCounters);
            ApplyCompletedDdgiInvestigationCounters(sceneData, _completedDdgiInvestigationCounters);
            ApplyCompletedDirectionalShadowReceiverCounters(sceneData, _completedDirectionalShadowReceiverCounters);
            if (particlesAllowed)
                ApplyCompletedGpuParticleCounters(sceneData, _completedGpuParticleCounters);
            if (!isolateSkinnedAnimationDebug)
                ApplyCompletedFoliageCounters(sceneData, _completedFoliageCounters);
            ApplyHiZCounterDiagnostics(sceneData);
            UpdateHiZFallbackDiagnostics(sceneData);
            FrameTimingSnapshot completedGpuTimings = _gpuTimestamps.LastCompletedSnapshot;
            ApplyCompletedGpuTimings(sceneData, completedGpuTimings);
            sceneData.AsyncComputeEstimatedOverlapMicroseconds = EstimateAsyncComputeOverlapMicroseconds(
                frameAsyncComputePlan.SubmissionPlan,
                completedGpuTimings);
            GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
            bool detailedDdgiInstrumentationActive =
                Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                giSettings.DebugView != GlobalIlluminationDebugView.None;
            bool fixedSimpleDdgiBudget =
                !giSettings.DdgiAdaptiveBudgetingEnabled ||
                detailedDdgiInstrumentationActive;
            bool hasCompletedSimpleDdgiGpuTiming = HasCompletedSimpleDdgiGpuTiming(completedGpuTimings);
            if (_simpleDdgiVolumeManager != null &&
                sceneData.SimpleDdgiActive != 0 &&
                (fixedSimpleDdgiBudget || hasCompletedSimpleDdgiGpuTiming))
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
            _ddgiProbeVolumeManager?.ReportCompletedGpuUpdateMicroseconds(
                sceneData.GpuDdgiUpdateMicroseconds,
                HasCompletedDdgiGpuTiming(completedGpuTimings),
                sceneData.GpuDdgiScheduleMicroseconds,
                HasCompletedGpuTiming(completedGpuTimings, "DdgiSchedulePass"));
            if (_ddgiProbeVolumeManager != null)
            {
                bool gpuDdgiSchedulerActive = Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.Gpu ||
                    (Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare &&
                     Settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering);
                sceneData.GpuDdgiScheduleP95Microseconds = _ddgiProbeVolumeManager.GpuScheduleP95Microseconds;
                sceneData.GpuDdgiScheduleOverBudget = _ddgiProbeVolumeManager.GpuScheduleOverBudget;
                if (gpuDdgiSchedulerActive && Settings.GlobalIllumination.EffectiveUseDdgi)
                    sceneData.DdgiSchedulerP95OverBudget = sceneData.GpuDdgiScheduleOverBudget;
            }
            sceneData.CpuTotalDrawSceneMicroseconds = ElapsedMicroseconds(drawSceneStart);
            UpdateGlobalIlluminationCpuTiming(sceneData);
            CaptureAsyncComputeTimingFrame(frameAsyncComputePlan, sceneData);
            _lastSceneData = sceneData;
            _lastDiagnostics = BuildDiagnostics(sceneData);
            _debugDraw.ClearFrame();
        }

        internal static bool ShouldRenderGeometryDecals(uint forwardDebugViewMode) => forwardDebugViewMode == 0;

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
                return Settings.GlobalIllumination.DebugView switch
                {
                    GlobalIlluminationDebugView.FinalIndirect => 80u,
                    GlobalIlluminationDebugView.SsgiRaw => 81u,
                    GlobalIlluminationDebugView.SsgiFiltered => 82u,
                    GlobalIlluminationDebugView.SsgiHistory => 83u,
                    GlobalIlluminationDebugView.SsgiRayHitMask => 84u,
                    GlobalIlluminationDebugView.SsgiHistoryRejection => 85u,
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
                    GlobalIlluminationDebugView.DdgiRelocationNormalized => 105u,
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
                    _ => (uint)Settings.Shadows.DebugView
                };
            }
            return (uint)Settings.Shadows.DebugView;
        }

        private float GetParticleDeltaSeconds()
        {
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

        private void BuildDebugOverlayDrawCommands(Scene scene, SceneRenderingData sceneData)
        {
            if (!sceneData.DebugToolingEnabled || sceneData.DebugOverlayMode == DebugOverlayMode.None)
                return;

            long start = Stopwatch.GetTimestamp();
            DebugDrawDepthMode depthMode = ResolveOverlayDepthMode();

            switch (sceneData.DebugOverlayMode)
            {
                case DebugOverlayMode.ObjectBounds:
                    DrawObjectBoundsOverlay(sceneData, depthMode);
                    break;
                case DebugOverlayMode.MeshletBounds:
                    DrawMeshletBoundsOverlay(sceneData, depthMode);
                    break;
                case DebugOverlayMode.SelectedObject:
                case DebugOverlayMode.MaterialInspection:
                    DrawSelectedObjectOverlay(sceneData, depthMode);
                    break;
                case DebugOverlayMode.ReflectionProbeVolumes:
                    DrawReflectionProbeOverlay(scene, sceneData, depthMode);
                    break;
                case DebugOverlayMode.DdgiProbeVolumes:
                case DebugOverlayMode.DdgiProbeActivity:
                case DebugOverlayMode.DdgiUpdatedProbes:
                case DebugOverlayMode.DdgiProbeRelocation:
                case DebugOverlayMode.DdgiProbeAge:
                case DebugOverlayMode.DdgiPhysicalSlots:
                case DebugOverlayMode.DdgiCascadeBounds:
                case DebugOverlayMode.DdgiNewlyExposedCells:
                case DebugOverlayMode.DdgiFrustumPriority:
                case DebugOverlayMode.DdgiSafetyRefresh:
                case DebugOverlayMode.DdgiCascadeBlend:
                case DebugOverlayMode.DdgiUpdateReasons:
                    DrawDdgiProbeVolumeOverlay(scene, sceneData, depthMode);
                    break;
                case DebugOverlayMode.DecalVolumes:
                    DrawGeometryDecalOverlay(sceneData, depthMode);
                    break;
            }

            sceneData.CpuDebugOverlayRecordMicroseconds = ElapsedMicroseconds(start);
        }

        private DebugDrawDepthMode ResolveOverlayDepthMode()
        {
            if (Settings.Debug.ShowXRayVolumes)
                return DebugDrawDepthMode.XRay;
            return Settings.Debug.ShowDepthTestedVolumes
                ? DebugDrawDepthMode.DepthTested
                : DebugDrawDepthMode.AlwaysVisible;
        }

        private void DrawObjectBoundsOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
        {
            foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
            {
                Vector4 color = snapshot.Visible
                    ? new Vector4(0.15f, 0.9f, 0.35f, 1.0f)
                    : new Vector4(1.0f, 0.35f, 0.1f, 1.0f);
                _debugDraw.Box(snapshot.WorldBounds, color, depthMode);
                sceneData.DebugObjectBoundsDrawn++;
            }
        }

        private void DrawSelectedObjectOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
        {
            int index = sceneData.DebugSelectedObjectIndex;
            if (index < 0 || index >= sceneData.ObjectDebugSnapshots.Count)
                return;

            ObjectDebugSnapshot snapshot = sceneData.ObjectDebugSnapshots[index];
            _debugDraw.Box(snapshot.WorldBounds, new Vector4(1.0f, 0.85f, 0.1f, 1.0f), depthMode);
            sceneData.DebugObjectBoundsDrawn = 1;
        }

        private void DrawReflectionProbeOverlay(
            Scene scene,
            SceneRenderingData sceneData,
            DebugDrawDepthMode depthMode)
        {
            int selectedProbe = Settings.Debug.SelectedReflectionProbeIndex;
            IReadOnlyList<ReflectionProbe> probes = scene.ReflectionProbes;
            for (int i = 0; i < probes.Count; i++)
            {
                if (selectedProbe >= 0 && i != selectedProbe)
                    continue;

                ReflectionProbe probe = probes[i];
                Vector4 color = i == selectedProbe
                    ? new Vector4(0.1f, 0.85f, 1.0f, 1.0f)
                    : new Vector4(0.2f, 0.55f, 1.0f, 0.85f);
                if (probe.Shape == ReflectionProbeShape.Sphere)
                {
                    _debugDraw.Sphere(probe.Position, probe.Radius, color, segments: 32, depthMode);
                }
                else
                {
                    Matrix4x4 transform = probe.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(probe.Position);
                    _debugDraw.OrientedBox(transform, probe.BoxExtents, color, depthMode);
                }

                sceneData.DebugReflectionProbeVolumesDrawn++;
            }
        }

        private void DrawDdgiProbeVolumeOverlay(
            Scene scene,
            SceneRenderingData sceneData,
            DebugDrawDepthMode depthMode)
        {
            const int MaxDetailedProbeMarkersPerFrame = 768;
            _ = scene;
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes = _lastDdgiFrameLayout.Volumes;
            int activeProbeStart = 0;
            int remainingDetailedProbeMarkers = sceneData.DebugOverlayMode == DebugOverlayMode.DdgiProbeVolumes
                ? 0
                : MaxDetailedProbeMarkersPerFrame;
            int simpleVolumeCount = Settings.GlobalIllumination.EffectiveUseSimpleDdgi &&
                _simpleDdgiVolumeManager != null &&
                _simpleDdgiVolumeManager.ProbeCount > 0
                    ? _simpleDdgiVolumeManager.LastVolumes.Length
                    : 0;
            int remainingMarkerVolumes = volumes.Count + simpleVolumeCount;
            for (int i = 0; i < volumes.Count; i++)
            {
                GlobalIlluminationProbeVolume volume = volumes[i];
                bool active = volume.Enabled && Settings.GlobalIllumination.EffectiveUseDdgi;
                int firstProbeIndex = active ? activeProbeStart : -1;
                Vector4 color = active
                    ? new Vector4(0.1f, 0.9f, 0.55f, 0.9f)
                    : new Vector4(0.45f, 0.45f, 0.45f, 0.55f);
                _debugDraw.Box(volume.Bounds, color, depthMode);
                DdgiProbeVolumeRuntimeMetadata metadata = i < _lastDdgiFrameLayout.VolumeMetadata.Count
                    ? _lastDdgiFrameLayout.VolumeMetadata[i]
                    : DdgiProbeVolumeRuntimeMetadata.Authored;
                if (remainingDetailedProbeMarkers > 0)
                {
                    int volumeMarkerBudget = CalculateDdgiProbeMarkerBudget(
                        remainingDetailedProbeMarkers,
                        remainingMarkerVolumes);
                    remainingDetailedProbeMarkers -= DrawDdgiProbeSamples(
                        volume,
                        metadata,
                        firstProbeIndex,
                        sceneData.DebugOverlayMode,
                        depthMode,
                        volumeMarkerBudget);
                }

                sceneData.DebugDdgiProbeVolumesDrawn++;
                if (active)
                    activeProbeStart += volume.ProbeCount;
                remainingMarkerVolumes = Math.Max(0, remainingMarkerVolumes - 1);
            }

            DrawSimpleDdgiProbeVolumeOverlay(sceneData, depthMode, remainingDetailedProbeMarkers);
        }

        private void DrawSimpleDdgiProbeVolumeOverlay(
            SceneRenderingData sceneData,
            DebugDrawDepthMode depthMode,
            int maxProbeMarkers)
        {
            if (!Settings.GlobalIllumination.EffectiveUseSimpleDdgi ||
                _simpleDdgiVolumeManager == null ||
                _simpleDdgiVolumeManager.ProbeCount <= 0)
            {
                return;
            }

            ReadOnlySpan<GPUSimpleDdgiVolume> volumes = _simpleDdgiVolumeManager.LastVolumes;
            int remainingProbeMarkers = maxProbeMarkers;
            int remainingMarkerVolumes = volumes.Length;
            for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
            {
                GPUSimpleDdgiVolume volume = volumes[volumeIndex];
                float spacing = Math.Max(volume.OriginAndSpacing.W, 0.001f);
                Vector3 origin = new(
                    volume.OriginAndSpacing.X,
                    volume.OriginAndSpacing.Y,
                    volume.OriginAndSpacing.Z);
                int probeCountX = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.X));
                int probeCountY = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Y));
                int probeCountZ = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Z));
                int firstProbeIndex = Math.Max(0, (int)MathF.Round(volume.GridCountsAndFirstProbe.W));
                Vector3 worldMin = new(
                    volume.WorldMinAndEdgeFade.X,
                    volume.WorldMinAndEdgeFade.Y,
                    volume.WorldMinAndEdgeFade.Z);
                Vector3 worldMax = new(
                    volume.WorldMaxAndKind.X,
                    volume.WorldMaxAndKind.Y,
                    volume.WorldMaxAndKind.Z);

                _debugDraw.Box(
                    new BoundingBox(worldMin, worldMax),
                    ResolveSimpleDdgiVolumeDebugColor(volumeIndex, volume),
                    depthMode);
                sceneData.DebugDdgiProbeVolumesDrawn++;

                if (remainingProbeMarkers <= 0)
                {
                    remainingMarkerVolumes = Math.Max(0, remainingMarkerVolumes - 1);
                    continue;
                }

                int volumeMarkerBudget = CalculateDdgiProbeMarkerBudget(
                    remainingProbeMarkers,
                    remainingMarkerVolumes);
                remainingProbeMarkers -= DrawSimpleDdgiProbeSamples(
                    origin,
                    spacing,
                    probeCountX,
                    probeCountY,
                    probeCountZ,
                    firstProbeIndex,
                    sceneData.DebugOverlayMode,
                    depthMode,
                    volumeMarkerBudget);
                remainingMarkerVolumes = Math.Max(0, remainingMarkerVolumes - 1);
            }
        }

        private static Vector4 ResolveSimpleDdgiVolumeDebugColor(int volumeIndex, GPUSimpleDdgiVolume volume)
        {
            // Authored volumes sort before same-spacing rings. Give them a distinct
            // colour so Ctrl+9 makes the overlap and camera-relative coverage clear.
            int kind = (int)MathF.Round(volume.WorldMaxAndKind.W);
            if (kind == 1)
                return new Vector4(0.95f, 0.9f, 0.25f, 0.95f);

            return (volumeIndex % 3) switch
            {
                0 => new Vector4(0.2f, 0.75f, 1.0f, 0.9f),
                1 => new Vector4(0.3f, 0.95f, 0.55f, 0.9f),
                _ => new Vector4(0.95f, 0.3f, 0.85f, 0.9f)
            };
        }

        private int DrawSimpleDdgiProbeSamples(
            Vector3 origin,
            float spacing,
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            int firstProbeIndex,
            DebugOverlayMode overlayMode,
            DebugDrawDepthMode depthMode,
            int maxProbeMarkers)
        {
            int markersDrawn = 0;
            float markerRadius = Math.Clamp(spacing * 0.08f, 0.04f, 0.2f);
            DdgiProbeMarkerSampling markerSampling = CalculateDdgiProbeMarkerSampling(
                probeCountX,
                probeCountY,
                probeCountZ,
                maxProbeMarkers);
            for (int z = 0; z < probeCountZ; z++)
            {
                for (int y = 0; y < probeCountY; y++)
                {
                    for (int x = 0; x < probeCountX; x++)
                    {
                        if (markersDrawn >= maxProbeMarkers)
                            return markersDrawn;
                        if (!ShouldDrawDdgiProbeMarker(x, y, z, markerSampling))
                            continue;

                        int localProbeIndex = x + y * probeCountX + z * probeCountX * probeCountY;
                        int probeIndex = firstProbeIndex + localProbeIndex;
                        bool updated = _simpleDdgiVolumeManager?.IsProbeScheduledForUpdate(probeIndex) == true;
                        if (!TryResolveSimpleDdgiProbeMarkerColor(overlayMode, probeIndex, updated, out Vector4 markerColor))
                            continue;

                        Vector3 p = origin + new Vector3(spacing * x, spacing * y, spacing * z);
                        _debugDraw.Line(p - Vector3.UnitX * markerRadius, p + Vector3.UnitX * markerRadius, markerColor, depthMode);
                        _debugDraw.Line(p - Vector3.UnitY * markerRadius, p + Vector3.UnitY * markerRadius, markerColor, depthMode);
                        _debugDraw.Line(p - Vector3.UnitZ * markerRadius, p + Vector3.UnitZ * markerRadius, markerColor, depthMode);
                        markersDrawn++;
                    }
                }
            }

            return markersDrawn;
        }

        private static bool TryResolveSimpleDdgiProbeMarkerColor(
            DebugOverlayMode overlayMode,
            int probeIndex,
            bool updated,
            out Vector4 color)
        {
            switch (overlayMode)
            {
                case DebugOverlayMode.DdgiUpdatedProbes:
                    color = updated
                        ? new Vector4(0.15f, 0.65f, 1.0f, 1.0f)
                        : new Vector4(0.25f, 0.28f, 0.35f, 0.35f);
                    return true;
                case DebugOverlayMode.DdgiPhysicalSlots:
                    color = ResolveDdgiPhysicalSlotColor(true, probeIndex, 0);
                    return true;
                case DebugOverlayMode.DdgiCascadeBounds:
                case DebugOverlayMode.DdgiCascadeBlend:
                    color = new Vector4(0.2f, 0.75f, 1.0f, 0.95f);
                    return true;
                case DebugOverlayMode.DdgiUpdateReasons:
                    color = updated
                        ? new Vector4(0.15f, 0.65f, 1.0f, 1.0f)
                        : new Vector4(0.24f, 0.28f, 0.34f, 0.35f);
                    return true;
                case DebugOverlayMode.DdgiProbeVolumes:
                    color = new Vector4(0.95f, 0.9f, 0.25f, 0.95f);
                    return true;
                default:
                    color = new Vector4(0.2f, 1.0f, 0.35f, 0.95f);
                    return true;
            }
        }

        private int DrawDdgiProbeSamples(
            GlobalIlluminationProbeVolume volume,
            DdgiProbeVolumeRuntimeMetadata metadata,
            int firstProbeIndex,
            DebugOverlayMode overlayMode,
            DebugDrawDepthMode depthMode,
            int maxProbeMarkers)
        {
            if (maxProbeMarkers <= 0)
                return 0;

            int probeIndex = 0;
            int markersDrawn = 0;
            Vector3 spacing = volume.ProbeSpacing;
            float markerRadius = MathF.Min(MathF.Min(spacing.X, spacing.Y), spacing.Z) * 0.08f;
            markerRadius = Math.Clamp(markerRadius, 0.04f, 0.2f);
            DdgiProbeMarkerSampling markerSampling = CalculateDdgiProbeMarkerSampling(
                volume.ProbeCountX,
                volume.ProbeCountY,
                volume.ProbeCountZ,
                maxProbeMarkers);
            DdgiClipmapCascadeState? cascadeState = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                ? FindDdgiClipmapCascade(metadata.CascadeIndex)
                : null;
            for (int z = 0; z < volume.ProbeCountZ; z++)
            {
                for (int y = 0; y < volume.ProbeCountY; y++)
                {
                    for (int x = 0; x < volume.ProbeCountX; x++, probeIndex++)
                    {
                        if (markersDrawn >= maxProbeMarkers)
                            return markersDrawn;
                        if (!ShouldDrawDdgiProbeMarker(x, y, z, markerSampling))
                            continue;

                        DdgiClipmapCell logicalCell = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                            ? new DdgiClipmapCell(
                                metadata.LogicalGridMinX + x,
                                metadata.LogicalGridMinY + y,
                                metadata.LogicalGridMinZ + z)
                            : new DdgiClipmapCell(x, y, z);
                        int globalProbeIndex = ResolveDdgiDebugProbeIndex(
                            metadata,
                            logicalCell,
                            firstProbeIndex,
                            probeIndex,
                            volume.ProbeCountX,
                            volume.ProbeCountY,
                            volume.ProbeCountZ);
                        DdgiClipmapCellState cellState = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                            ? ResolveDdgiDebugCellState(cascadeState, logicalCell, globalProbeIndex)
                            : new DdgiClipmapCellState(globalProbeIndex >= 0, 0UL, 0UL, globalProbeIndex);
                        uint updateFlags = ResolveScheduledDdgiProbeUpdateFlags(globalProbeIndex, out uint updatePriority);
                        if (!TryResolveDdgiProbeMarkerColor(
                            overlayMode,
                            volume.Enabled,
                            globalProbeIndex,
                            _ddgiProbeVolumeManager?.IsProbeScheduledForUpdate(globalProbeIndex) == true,
                            updateFlags,
                            updatePriority,
                            metadata,
                            cellState,
                            firstProbeIndex,
                            out Vector4 markerColor))
                        {
                            continue;
                        }

                        Vector3 p = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                            ? new Vector3(logicalCell.X * spacing.X, logicalCell.Y * spacing.Y, logicalCell.Z * spacing.Z)
                            : volume.Origin + new Vector3(spacing.X * x, spacing.Y * y, spacing.Z * z);
                        _debugDraw.Line(p - Vector3.UnitX * markerRadius, p + Vector3.UnitX * markerRadius, markerColor, depthMode);
                        _debugDraw.Line(p - Vector3.UnitY * markerRadius, p + Vector3.UnitY * markerRadius, markerColor, depthMode);
                        _debugDraw.Line(p - Vector3.UnitZ * markerRadius, p + Vector3.UnitZ * markerRadius, markerColor, depthMode);
                        markersDrawn++;
                    }
                }
            }

            return markersDrawn;
        }

        internal readonly record struct DdgiProbeMarkerSampling(int StepX, int StepY, int StepZ);

        internal static int CalculateDdgiProbeMarkerBudget(int remainingMarkers, int remainingVolumes)
        {
            if (remainingMarkers <= 0 || remainingVolumes <= 0)
                return 0;

            // Divide the still-available budget among every volume that has not
            // been visited yet. Any markers a sparse/filtering volume does not use
            // remain available and are redistributed by the next iteration.
            return Math.Max(1, (remainingMarkers + remainingVolumes - 1) / remainingVolumes);
        }

        internal static DdgiProbeMarkerSampling CalculateDdgiProbeMarkerSampling(
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            int maxMarkers)
        {
            int safeCountX = Math.Max(1, probeCountX);
            int safeCountY = Math.Max(1, probeCountY);
            int safeCountZ = Math.Max(1, probeCountZ);
            int safeMaxMarkers = Math.Max(1, maxMarkers);
            int stepX = 1;
            int stepY = 1;
            int stepZ = 1;

            while (SampledAxisCount(safeCountX, stepX) *
                SampledAxisCount(safeCountY, stepY) *
                SampledAxisCount(safeCountZ, stepZ) > safeMaxMarkers)
            {
                int sampledX = SampledAxisCount(safeCountX, stepX);
                int sampledY = SampledAxisCount(safeCountY, stepY);
                int sampledZ = SampledAxisCount(safeCountZ, stepZ);
                if (sampledX >= sampledY && sampledX >= sampledZ)
                    stepX++;
                else if (sampledZ >= sampledX && sampledZ >= sampledY)
                    stepZ++;
                else
                    stepY++;
            }

            return new DdgiProbeMarkerSampling(stepX, stepY, stepZ);
        }

        internal static bool ShouldDrawDdgiProbeMarker(int x, int y, int z, DdgiProbeMarkerSampling sampling)
        {
            return x >= 0 &&
                y >= 0 &&
                z >= 0 &&
                x % Math.Max(1, sampling.StepX) == 0 &&
                y % Math.Max(1, sampling.StepY) == 0 &&
                z % Math.Max(1, sampling.StepZ) == 0;
        }

        private static int SampledAxisCount(int count, int step)
        {
            return (Math.Max(1, count) + Math.Max(1, step) - 1) / Math.Max(1, step);
        }

        private DdgiClipmapCascadeState? FindDdgiClipmapCascade(int cascadeIndex)
        {
            IReadOnlyList<DdgiClipmapCascadeState> cascades = _cameraRelativeDdgiClipmaps.Cascades;
            for (int i = 0; i < cascades.Count; i++)
            {
                if (cascades[i].CascadeIndex == cascadeIndex)
                    return cascades[i];
            }

            return null;
        }

        private static int ResolveDdgiDebugProbeIndex(
            DdgiProbeVolumeRuntimeMetadata metadata,
            DdgiClipmapCell logicalCell,
            int firstProbeIndex,
            int linearProbeIndex,
            int probeCountX,
            int probeCountY,
            int probeCountZ)
        {
            if (firstProbeIndex < 0)
                return -1;
            if (metadata.Kind != DdgiProbeVolumeKind.CameraClipmap)
                return firstProbeIndex + linearProbeIndex;

            return DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                logicalCell,
                new DdgiClipmapCell(metadata.LogicalGridMinX, metadata.LogicalGridMinY, metadata.LogicalGridMinZ),
                new DdgiClipmapCell(metadata.RingOffsetX, metadata.RingOffsetY, metadata.RingOffsetZ),
                probeCountX,
                probeCountY,
                probeCountZ,
                firstProbeIndex);
        }

        private static DdgiClipmapCellState ResolveDdgiDebugCellState(
            DdgiClipmapCascadeState? cascade,
            DdgiClipmapCell logicalCell,
            int physicalProbeIndex)
        {
            if (cascade == null || !cascade.ContainsLogicalCell(logicalCell))
                return new DdgiClipmapCellState(false, ulong.MaxValue, 0UL, physicalProbeIndex);

            return cascade.GetCellState(logicalCell);
        }

        private uint ResolveScheduledDdgiProbeUpdateFlags(int globalProbeIndex, out uint updatePriority)
        {
            if (_ddgiProbeVolumeManager != null &&
                _ddgiProbeVolumeManager.TryGetScheduledProbeUpdateFlags(globalProbeIndex, out uint flags, out updatePriority))
            {
                return flags;
            }

            updatePriority = 0u;
            return 0u;
        }

        private static bool TryResolveDdgiProbeMarkerColor(
            DebugOverlayMode overlayMode,
            bool volumeEnabled,
            int globalProbeIndex,
            bool updated,
            uint updateFlags,
            uint updatePriority,
            DdgiProbeVolumeRuntimeMetadata metadata,
            DdgiClipmapCellState cellState,
            int firstProbeIndex,
            out Vector4 color)
        {
            switch (overlayMode)
            {
                case DebugOverlayMode.DdgiProbeActivity:
                    color = volumeEnabled
                        ? new Vector4(0.2f, 1.0f, 0.35f, 0.95f)
                        : new Vector4(0.6f, 0.6f, 0.6f, 0.65f);
                    return true;
                case DebugOverlayMode.DdgiUpdatedProbes:
                    color = updated
                        ? new Vector4(0.15f, 0.65f, 1.0f, 1.0f)
                        : new Vector4(0.25f, 0.28f, 0.35f, 0.35f);
                    return updated || volumeEnabled;
                case DebugOverlayMode.DdgiProbeRelocation:
                    color = updated
                        ? new Vector4(1.0f, 0.2f, 0.85f, 1.0f)
                        : new Vector4(0.3f, 0.2f, 0.35f, 0.35f);
                    return updated || volumeEnabled;
                case DebugOverlayMode.DdgiProbeAge:
                    color = ResolveDdgiProbeAgeColor(volumeEnabled, cellState);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiPhysicalSlots:
                    color = ResolveDdgiPhysicalSlotColor(volumeEnabled, globalProbeIndex, firstProbeIndex);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiCascadeBounds:
                    color = ResolveDdgiCascadeColor(volumeEnabled, metadata.CascadeIndex);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiNewlyExposedCells:
                    color = !cellState.Initialized ||
                            (updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0
                        ? new Vector4(1.0f, 0.36f, 0.12f, 1.0f)
                        : new Vector4(0.24f, 0.28f, 0.34f, 0.25f);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiFrustumPriority:
                    color = (updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag) != 0
                        ? new Vector4(0.1f, 0.72f, 1.0f, 1.0f)
                        : new Vector4(0.2f, 0.24f, 0.32f, 0.28f);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiSafetyRefresh:
                    color = (updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag) != 0
                        ? new Vector4(0.95f, 0.78f, 0.2f, 1.0f)
                        : new Vector4(0.22f, 0.22f, 0.28f, 0.28f);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiCascadeBlend:
                    color = ResolveDdgiCascadeBlendColor(volumeEnabled, metadata);
                    return volumeEnabled;
                case DebugOverlayMode.DdgiUpdateReasons:
                    color = ResolveDdgiUpdateReasonColor(volumeEnabled, updateFlags, updatePriority);
                    return volumeEnabled;
                default:
                    color = volumeEnabled
                        ? new Vector4(0.95f, 0.9f, 0.25f, 0.95f)
                        : new Vector4(0.55f, 0.55f, 0.55f, 0.55f);
                    return true;
            }
        }

        private static Vector4 ResolveDdgiCascadeColor(bool volumeEnabled, int cascadeIndex)
        {
            if (!volumeEnabled)
                return new Vector4(0.45f, 0.45f, 0.45f, 0.45f);

            uint hash = HashProbeColor(unchecked((uint)(cascadeIndex + 17)));
            return new Vector4(
                0.25f + ((hash & 0xffu) / 255.0f) * 0.7f,
                0.25f + (((hash >> 8) & 0xffu) / 255.0f) * 0.7f,
                0.25f + (((hash >> 16) & 0xffu) / 255.0f) * 0.7f,
                0.95f);
        }

        private static Vector4 ResolveDdgiCascadeBlendColor(bool volumeEnabled, DdgiProbeVolumeRuntimeMetadata metadata)
        {
            if (!volumeEnabled)
                return new Vector4(0.45f, 0.45f, 0.45f, 0.45f);

            float blend = Math.Clamp(metadata.EdgeBlendFraction, 0.0f, 1.0f);
            return new Vector4(0.18f + blend * 0.82f, 0.85f - blend * 0.45f, 0.95f - blend * 0.7f, 0.95f);
        }

        private static Vector4 ResolveDdgiUpdateReasonColor(bool volumeEnabled, uint updateFlags, uint updatePriority)
        {
            if (!volumeEnabled)
                return new Vector4(0.45f, 0.45f, 0.45f, 0.45f);
            if ((updateFlags & (GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryAddedFlag |
                                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonGeometryRemovedFlag |
                                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonTransformChangedFlag |
                                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamInFlag |
                                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonStreamOutFlag)) != 0)
                return new Vector4(1.0f, 0.18f, 0.06f, 1.0f);
            if ((updateFlags & (GlobalIlluminationProbeVolumeData.ProbeUpdateReasonMaterialChangedFlag |
                                GlobalIlluminationProbeVolumeData.ProbeUpdateReasonEmissiveChangedFlag)) != 0)
                return new Vector4(0.92f, 0.16f, 0.86f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonLocalLightChangedFlag) != 0)
                return new Vector4(1.0f, 0.62f, 0.12f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirectionalLightChangedFlag) != 0)
                return new Vector4(1.0f, 0.94f, 0.2f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonNewCellFlag) != 0)
                return new Vector4(1.0f, 0.3f, 0.08f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonDirtyBoundsFlag) != 0)
                return new Vector4(0.9f, 0.1f, 0.95f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonVisibleFrustumFlag) != 0)
                return new Vector4(0.1f, 0.72f, 1.0f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonOutsideFrustumSafetyFlag) != 0)
                return new Vector4(0.95f, 0.78f, 0.2f, 1.0f);
            if ((updateFlags & GlobalIlluminationProbeVolumeData.ProbeUpdateReasonAgeRefreshFlag) != 0)
                return new Vector4(0.45f, 0.95f, 0.35f, 1.0f);
            if (updatePriority > 0u)
                return ResolveDdgiPhysicalSlotColor(true, unchecked((int)updatePriority), 0);

            return new Vector4(0.2f, 0.24f, 0.32f, 0.25f);
        }

        private static Vector4 ResolveDdgiProbeAgeColor(bool volumeEnabled, DdgiClipmapCellState cellState)
        {
            if (!volumeEnabled)
                return new Vector4(0.45f, 0.45f, 0.45f, 0.45f);
            if (!cellState.Initialized)
                return new Vector4(1.0f, 0.18f, 0.1f, 1.0f);

            float normalizedAge = cellState.AgeFrames == ulong.MaxValue
                ? 1.0f
                : Math.Clamp(cellState.AgeFrames / 180.0f, 0.0f, 1.0f);
            return new Vector4(
                0.1f + normalizedAge * 0.9f,
                0.95f - normalizedAge * 0.55f,
                0.25f + (1.0f - normalizedAge) * 0.55f,
                0.95f);
        }

        private static Vector4 ResolveDdgiPhysicalSlotColor(bool volumeEnabled, int globalProbeIndex, int firstProbeIndex)
        {
            if (!volumeEnabled || globalProbeIndex < 0 || firstProbeIndex < 0)
                return new Vector4(0.45f, 0.45f, 0.45f, 0.45f);

            uint hash = HashProbeColor(unchecked((uint)(globalProbeIndex - firstProbeIndex)));
            float r = 0.2f + ((hash & 0xffu) / 255.0f) * 0.75f;
            float g = 0.2f + (((hash >> 8) & 0xffu) / 255.0f) * 0.75f;
            float b = 0.2f + (((hash >> 16) & 0xffu) / 255.0f) * 0.75f;
            return new Vector4(r, g, b, 0.95f);
        }

        private static uint HashProbeColor(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;
            return value;
        }

        private void DrawGeometryDecalOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
        {
            foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
            {
                MaterialRenderMetadata metadata;
                try
                {
                    metadata = _materialManager.GetMaterialMetadata(snapshot.Material);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    continue;
                }

                if (!metadata.IsGeometryDecal)
                    continue;

                _debugDraw.Box(snapshot.WorldBounds, new Vector4(1.0f, 0.25f, 0.9f, 1.0f), depthMode);
                sceneData.DebugDecalVolumesDrawn++;
            }
        }

        private void DrawMeshletBoundsOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
        {
            const int SphereSegments = 8;
            const int LinesPerSphere = SphereSegments * 3;
            Vector4 color = new(0.1f, 0.75f, 1.0f, 0.9f);
            int lineBudget = Math.Max(0, Settings.Debug.MaxDebugLineSegments);
            int usedLines = _debugDraw.Snapshot().LineCount;

            foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
            {
                if (!snapshot.Visible)
                    continue;

                MeshInfo meshInfo;
                try
                {
                    meshInfo = _meshManager.GetMeshInfo(snapshot.Mesh);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    continue;
                }

                uint meshletOffset = meshInfo.MeshletCount > 0
                    ? meshInfo.MeshletOffset
                    : meshInfo.MeshletLodGeneratedCount > 0
                        ? meshInfo.MeshletOffset
                        : 0u;
                uint meshletCount = meshInfo.MeshletCount > 0
                    ? meshInfo.MeshletCount
                    : meshInfo.MeshletLodGeneratedCount;
                if (meshletCount == 0)
                    continue;

                float radiusScale = GetMaxAbsScale(snapshot.WorldMatrix);
                ulong end = (ulong)meshletOffset + meshletCount;
                for (ulong meshletIndex = meshletOffset; meshletIndex < end; meshletIndex++)
                {
                    if (usedLines + LinesPerSphere > lineBudget)
                    {
                        ulong remaining = end - meshletIndex;
                        sceneData.DebugMeshletBoundsDropped += remaining > int.MaxValue ? int.MaxValue : (int)remaining;
                        break;
                    }

                    Njulf.Core.Geometry.Meshlet meshlet;
                    try
                    {
                        meshlet = _meshManager.GetMeshlet((uint)meshletIndex);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        sceneData.DebugMeshletBoundsDropped++;
                        continue;
                    }

                    Vector3 center = SceneDataBuilder.TransformPoint(meshlet.BoundingSphereCenter, snapshot.WorldMatrix);
                    float radius = meshlet.BoundingSphereRadius * radiusScale;
                    if (radius <= 0.0f || float.IsNaN(radius) || float.IsInfinity(radius))
                    {
                        sceneData.DebugMeshletBoundsDropped++;
                        continue;
                    }

                    _debugDraw.Sphere(center, radius, color, SphereSegments, depthMode);
                    usedLines += LinesPerSphere;
                    sceneData.DebugMeshletBoundsDrawn++;
                }
            }
        }

        private static float GetMaxAbsScale(Matrix4x4 matrix)
        {
            Vector3 scale = matrix.Scale;
            return MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
        }

        private HiZVisibilityPolicyDecision PlanHiZVisibility(
            Scene scene,
            ICamera camera,
            bool depthPrePassEnabled,
            bool featureIsolationDisablesHiZ)
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
                CompletedSceneSubmissionCompactionMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("SceneOpaqueCompactionPass"),
                CompletedForwardOpaqueMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("ForwardPlusPass"));

            bool previousForceOn = Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn;
            bool previousForceProbe = Settings.HiZVisibilityPolicy.ForceAdaptiveProbe;
            Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn = previousForceOn || Settings.HiZOcclusion.ForceOn;
            Settings.HiZVisibilityPolicy.ForceAdaptiveProbe = previousForceProbe || Settings.HiZOcclusion.ForceProbe;
            try
            {
                return HiZVisibilityPolicy.Plan(input, Settings.HiZVisibilityPolicy, _hizVisibilityPolicyState);
            }
            finally
            {
                Settings.HiZVisibilityPolicy.ForceHiZOcclusionOn = previousForceOn;
                Settings.HiZVisibilityPolicy.ForceAdaptiveProbe = previousForceProbe;
            }
        }

        private bool IsDepthPrePassRequiredByNonHiZOcclusionFeatures()
        {
            return Settings.AmbientOcclusion.Enabled ||
                Settings.GlobalIllumination.EffectiveUseSsgi;
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
            HiZVisibilityPolicyDecision hiZDecision)
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
            bool ssgi = Settings.GlobalIllumination.EffectiveUseSsgi;

            int count = 0;
            if (forwardVisibilityCurrentHiZ)
                count++;
            if (sceneSubmissionPreviousHiZ)
                count++;
            if (legacyForwardTask)
                count++;
            if (foliage)
                count++;
            if (ssgi)
                count++;

            if (count == 0)
                return HiZConsumerDecision.None;

            return new HiZConsumerDecision(
                count,
                BuildHiZConsumerSummary(forwardVisibilityCurrentHiZ, sceneSubmissionPreviousHiZ, legacyForwardTask, foliage, ssgi),
                forwardVisibilityCurrentHiZ,
                sceneSubmissionPreviousHiZ,
                legacyForwardTask,
                foliage,
                ssgi);
        }

        private static string BuildHiZConsumerSummary(
            bool forwardVisibilityCurrentHiZ,
            bool sceneSubmissionPreviousHiZ,
            bool legacyForwardTask,
            bool foliage,
            bool ssgi)
        {
            string summary = string.Empty;
            AppendHiZConsumer(ref summary, forwardVisibilityCurrentHiZ, "ForwardVisibilityCurrentHiZ");
            AppendHiZConsumer(ref summary, sceneSubmissionPreviousHiZ, "SceneSubmissionPreviousHiZ");
            AppendHiZConsumer(ref summary, legacyForwardTask, "LegacyForwardTask");
            AppendHiZConsumer(ref summary, foliage, "Foliage");
            AppendHiZConsumer(ref summary, ssgi, "Ssgi");
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
                sceneData.HiZFallbackReason = "Previous-frame scene-submission Hi-Z disabled; using compacted forward buffers without Hi-Z rejection.";
                return;
            }

            sceneData.HiZFallbackReason = "Previous Hi-Z history invalid; using compacted forward buffers without Hi-Z rejection.";
        }

        private readonly record struct HiZConsumerDecision(
            int Count,
            string Summary,
            bool ForwardVisibilityCurrentHiZ,
            bool SceneSubmissionPreviousHiZ,
            bool LegacyForwardTask,
            bool Foliage,
            bool Ssgi)
        {
            public static HiZConsumerDecision None { get; } = new(
                0,
                "None",
                ForwardVisibilityCurrentHiZ: false,
                SceneSubmissionPreviousHiZ: false,
                LegacyForwardTask: false,
                Foliage: false,
                Ssgi: false);
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
            if (Vector3.DistanceSquared(camera.Position, _lastHiZCameraPosition) >= policy.CameraCutDistance * policy.CameraCutDistance)
                return true;

            Vector3 currentForward = camera.Forward.Normalized();
            Vector3 previousForward = _lastHiZCameraForward.Normalized();
            if (currentForward == Vector3.Zero || previousForward == Vector3.Zero)
                return false;

            return Vector3.Dot(currentForward, previousForward) < policy.CameraCutForwardDotThreshold;
        }

        private void EnsureMeshPipelineDiagnosticVariant()
        {
            if (_meshPipeline == null ||
                _meshPipeline.GpuMeshletCountersEnabled == Settings.Diagnostics.GpuMeshletCountersEnabled)
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
                        throw new VulkanException("Failed to wait for device before recreating mesh diagnostic pipelines", result);
                });

            _meshPipeline.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            System.Diagnostics.Debug.WriteLine(
                Settings.Diagnostics.GpuMeshletCountersEnabled
                    ? "GPU meshlet diagnostic counters enabled; using diagnostic task shader variants."
                    : "GPU meshlet diagnostic counters disabled; using normal task shader variants.");
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
            bool hasShadowLight = lightSnapshot.DirectionalLightCount > 0 && lightSnapshot.HasShadowCastingDirectionalLight;
            if (hasShadowLight)
            {
                lightIndex = lightSnapshot.FirstShadowCastingDirectionalLightIndex;
                shadowLight = lightSnapshot.FirstShadowCastingDirectionalLight;
            }

            EnsureDirectionalShadowResources(hasShadowLight);
            enabled = shadowSettings.DirectionalShadowsEnabled && hasShadowLight && _directionalShadowResources.HasImage;

            GPUShadowData shadowData = enabled
                ? DirectionalShadowDataBuilder.Build(
                    camera,
                    shadowLight.Direction,
                    shadowSettings,
                    lightIndex,
                    shadowLight.ShadowStrength)
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
            int lightIndex)
        {
            if (_directionalShadowResources == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            _directionalShadowResources.UploadShadowData(_stagingRing, _currentCommandBuffer, shadowData);

            sceneData.DirectionalShadowPassEnabled = enabled;
            ulong recordSignature = CreateDirectionalShadowRecordSignature(sceneData, shadowData, enabled, shadowSettings);
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
            LocalShadowDataBuilder.FillSpotShadows(selection.SpotLights, shadowSettings, spotShadows);
            LocalShadowDataBuilder.FillPointShadows(selection.PointLights, shadowSettings, pointShadows);
            LocalShadowDataBuilder.FillShadowIndexMap(lightCount, selection.SpotLights, selection.PointLights, shadowIndices);

            ulong spotSignature = CreateSpotShadowSignature(selection.SpotLights, shadowSettings);
            if (!_hasUploadedSpotShadows || _lastSpotShadowUploadSignature != spotSignature)
            {
                _spotShadowAtlas.UploadSpotShadows(_stagingRing, _currentCommandBuffer, spotShadows);
                _lastSpotShadowUploadSignature = spotSignature;
                _hasUploadedSpotShadows = true;
            }

            ulong indexSignature = CreateLocalShadowIndexSignature(lightCount, selection.SpotLights, selection.PointLights);
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
            ulong spotRecordSignature = HashAdd(HashAdd(spotSignature, sceneData.LocalStaticShadowMeshletDrawSignature), spotShadows.Length);
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
            ulong pointRecordSignature = HashAdd(HashAdd(pointSignature, sceneData.LocalStaticShadowMeshletDrawSignature), pointShadows.Length);
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
            sceneData.PointShadowRenderedFaceCount = CountPointShadowFaces(sceneData.PointShadowFaceMasks, pointShadows.Length);
            sceneData.PointShadowSkippedFaceCount = Math.Max(0, pointShadowFaceCapacity - sceneData.PointShadowRenderedFaceCount);
        }

        private static ulong CreateSpotShadowSignature(ReadOnlySpan<SelectedLocalShadow> selectedLights, ShadowSettings settings)
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

        private static ulong CreatePointShadowSignature(ReadOnlySpan<SelectedLocalShadow> selectedLights, ShadowSettings settings)
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
            ReadOnlySpan<SelectedLocalShadow> selectedPoints)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, lightCount);
            hash = HashAdd(hash, selectedSpots.Length);
            for (int i = 0; i < selectedSpots.Length; i++)
                hash = HashAdd(hash, selectedSpots[i].LightIndex);
            hash = HashAdd(hash, selectedPoints.Length);
            for (int i = 0; i < selectedPoints.Length; i++)
                hash = HashAdd(hash, selectedPoints[i].LightIndex);
            return hash;
        }

        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        public const uint SimpleDdgiDirtyReasonLight = 1u << 0;
        public const uint SimpleDdgiDirtyReasonEmissive = 1u << 1;
        public const uint SimpleDdgiDirtyReasonDynamicGeometry = 1u << 2;

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
            float radius = MathF.Max(light.Range, 0.001f);
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
        }

        private void UpdateGlobalIlluminationCpuTiming(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            bool active = gi.Enabled &&
                (gi.EffectiveUseSsgi || gi.EffectiveUseDdgi || gi.EffectiveUseSimpleDdgi);
            if (!active)
            {
                _globalIlluminationCpuTimingWindow.Clear();
                sceneData.CpuGlobalIlluminationRecordMicroseconds = 0;
                sceneData.CpuGlobalIlluminationRecordP95Microseconds = 0;
                sceneData.GlobalIlluminationCpuTimingSampleCount = 0;
                return;
            }

            // Every term is owned by a distinct recorder.  The legacy DDGI
            // scheduler runs outside its manager upload scope, while the simple
            // path includes its scheduler in the manager upload time.  The AS
            // manager reports a total preparation interval, so its detailed
            // BLAS/TLAS/upload counters are deliberately not added again.
            long totalMicroseconds = Math.Max(0, sceneData.CpuSsgiRecordMicroseconds) +
                Math.Max(0, sceneData.CpuDdgiRecordMicroseconds) +
                Math.Max(0, sceneData.CpuDdgiSchedulerMicroseconds) +
                Math.Max(0, sceneData.CpuSimpleDdgiRecordMicroseconds) +
                Math.Max(0, sceneData.CpuFarFieldRecordMicroseconds) +
                Math.Max(0, sceneData.CpuAccelerationStructureBuildMicroseconds);
            _globalIlluminationCpuTimingWindow.Add(totalMicroseconds);
            PerformanceSampleStats stats = _globalIlluminationCpuTimingWindow.GetStats();
            sceneData.CpuGlobalIlluminationRecordMicroseconds = totalMicroseconds;
            sceneData.CpuGlobalIlluminationRecordP95Microseconds = Math.Max(0, (long)Math.Round(stats.P95));
            sceneData.GlobalIlluminationCpuTimingSampleCount = stats.Count;
        }

        private RendererDiagnostics BuildDiagnostics(SceneRenderingData sceneData)
        {
            ModelRenderUploadDiagnostics uploadDiagnostics = _modelUploadService.LastUploadDiagnostics;
            bool gpuMeshletCountersEnabled = MeshletDiagnosticCountersActive;
            int submittedOpaqueMeshlets = sceneData.ForwardTaskInvocations > 0
                ? sceneData.ForwardTaskInvocations
                : sceneData.OpaqueMeshletCount;
            int forwardCandidates = sceneData.ForwardTaskInvocations > 0
                ? sceneData.ForwardTaskInvocations
                : sceneData.OpaqueMeshletCount;
            int forwardVisibleAfterOcclusion = sceneData.ForwardTaskInvocations > 0
                ? sceneData.ForwardEmittedMeshletsGpu
                : Math.Max(0, forwardCandidates - sceneData.ForwardFrustumCulledMeshletsGpu - sceneData.ForwardOcclusionCulledMeshletsGpu);
            int forwardOcclusionRejected = sceneData.ForwardOcclusionCulledMeshletsGpu;
            bool forwardOcclusionCountersReconciled = !gpuMeshletCountersEnabled || ForwardOcclusionCountersReconcile(sceneData);
            string forwardOcclusionSanity = BuildForwardOcclusionSanity(sceneData, gpuMeshletCountersEnabled, forwardOcclusionCountersReconciled);
            string gpuMeshletCountersStatus = gpuMeshletCountersEnabled
                ? "GPU meshlet counters enabled."
                : "GPU meshlet counters disabled.";
            SceneSubmissionMode sceneSubmissionActiveMode = SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData);
            int spotShadowMeshletLightTests = CalculateSpotShadowMeshletLightTests(sceneData);
            int pointShadowMeshletFaceTests = CalculatePointShadowMeshletFaceTests(sceneData);
            bool spotShadowGpuCompactionJustified = IsSpotShadowGpuCompactionJustified(sceneData, spotShadowMeshletLightTests);
            bool pointShadowGpuCompactionJustified = IsPointShadowGpuCompactionJustified(sceneData, pointShadowMeshletFaceTests);
            ProductionRenderPipelineDeclaration productionPipeline = ProductionRenderPipelineDeclaration.Instance;
            AsyncComputePlan asyncComputePlan = _frameAsyncComputePlan ?? BuildAsyncComputePlan(sceneData);
            DeviceRequirementReport? captureDevice = _context.SelectedDeviceRequirementReport;
            GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
            bool giRayQuerySupported = _context.RayQuerySupported && _accelerationStructureManager?.Supported == true;
            bool giAccelerationStructuresActive = _accelerationStructureManager?.Active == true;
            GlobalIlluminationMode effectiveGiMode = ResolveEffectiveGlobalIlluminationMode(giSettings, giRayQuerySupported);
            if (effectiveGiMode == GlobalIlluminationMode.RayQueryHybrid && !giAccelerationStructuresActive)
                effectiveGiMode = GlobalIlluminationMode.Hybrid;
            // Preserve authored intent separately from the live path.  The emergency switch and
            // capability fallbacks must never make a capture look as though a feature was simply
            // not requested.
            bool giRequested = giSettings.Enabled && giSettings.Mode != GlobalIlluminationMode.Disabled;
            bool modeRequestsSsgi = giSettings.Mode is GlobalIlluminationMode.Ssgi or
                GlobalIlluminationMode.Hybrid or GlobalIlluminationMode.RayQueryHybrid;
            bool modeRequestsDdgi = giSettings.Mode is GlobalIlluminationMode.Ddgi or
                GlobalIlluminationMode.Hybrid or GlobalIlluminationMode.RayQueryHybrid;
            bool ssgiRequested = giRequested && giSettings.UseSsgi && modeRequestsSsgi;
            bool ddgiRequested = giRequested && giSettings.UseDdgi && modeRequestsDdgi;
            bool simpleDdgiRequested = ddgiRequested && giSettings.DdgiSimpleEnabled;
            bool rayQueryGiRequested = ddgiRequested && giSettings.UseRayQueryBackend;
            string globalIlluminationFallbackReason = giSettings.EmergencyGiFallbackEnabled && giRequested
                ? "Emergency GI fallback is enabled; dynamic GI paths are intentionally suppressed."
                : string.Empty;
            // The emergency switch is a live rollback control: it must make the
            // renderer report GI as inactive immediately while leaving stable
            // environment/reflection lighting intact. The authored settings stay
            // untouched so clearing the switch restores the prior configuration.
            bool giEnabled = !giSettings.EmergencyGiFallbackEnabled &&
                giSettings.Enabled && effectiveGiMode != GlobalIlluminationMode.Disabled;
            bool giUsesSsgi = giSettings.EffectiveUseSsgi;
            bool giUsesSimpleDdgi = giSettings.EffectiveUseSimpleDdgi;
            bool giUsesDdgi = giSettings.EffectiveUseDdgi || giUsesSimpleDdgi;
            bool ddgiAsyncComputeActuallyEnabled = giUsesDdgi && IsDdgiAsyncComputeActuallyEnabled(asyncComputePlan);
            IReadOnlyList<string> activeProductionPipelinePasses = productionPipeline.GetActivePasses(
                sceneData.ActiveFeatureIsolation,
                sceneData.TransparencyMode,
                giUsesSsgi);
            bool giRayQueryActive = giEnabled &&
                                    giSettings.EffectiveUseRayQueryBackend &&
                                    giRayQuerySupported &&
                                    giAccelerationStructuresActive;
            (uint ssgiWidth, uint ssgiHeight) = CalculateSsgiExtent(sceneData.ScreenWidth, sceneData.ScreenHeight, giSettings.ResolutionScale, giUsesSsgi);
            int ssgiRayCount = ResolveSsgiRayCount(Settings.QualityPreset, ssgiRequested);
            ulong globalIlluminationRenderTargetBytes = _renderTargets?.GlobalIlluminationRenderTargetBytes ?? EstimateGlobalIlluminationRenderTargetBytes(
                sceneData.ScreenWidth,
                sceneData.ScreenHeight,
                giSettings.ResolutionScale,
                giUsesSsgi);
            ulong ssgiRenderTargetBytes = giUsesSsgi ? globalIlluminationRenderTargetBytes : 0UL;
            ulong sceneSurfaceRenderTargetBytes = _renderTargets?.SceneSurfaceRenderTargetBytes ?? 0UL;
            if (_renderTargets != null && giUsesSsgi)
            {
                ssgiWidth = _renderTargets.SsgiRaw.Extent.Width;
                ssgiHeight = _renderTargets.SsgiRaw.Extent.Height;
            }
            string localShadowGpuCompactionStatus = BuildLocalShadowGpuCompactionStatus(
                sceneData,
                spotShadowMeshletLightTests,
                pointShadowMeshletFaceTests,
                spotShadowGpuCompactionJustified,
                pointShadowGpuCompactionJustified);
            string localShadowOverflowSummary = BuildLocalShadowOverflowSummary(
                spotShadowGpuCompactionJustified,
                pointShadowGpuCompactionJustified);
            DdgiRuntimeSnapshot ddgiRuntimeSnapshot = giUsesDdgi
                ? CreateDdgiRuntimeSnapshot(sceneData)
                : DdgiRuntimeSnapshot.Empty;
            IReadOnlyList<string> ddgiDiagnosticWarnings = _ddgiDiagnosticWarningTracker.Update(
                ddgiRuntimeSnapshot,
                giUsesDdgi && sceneData.DdgiSchedulerP95OverBudget != 0);
            MaterialManagerDiagnostics materialDiagnostics = _materialManager.Diagnostics;
            MaterialGiRolloutEvaluation materialGiRollout =
                giSettings.EvaluateMaterialGiRollout();
            RendererValidationMessageSnapshot validationMessages = _context.ValidationMessageSnapshot;
            RendererDiagnostics diagnostics = new RendererDiagnostics(
                sceneData.ObjectCount,
                sceneData.MeshletCount,
                sceneData.OpaqueObjectCount,
                sceneData.MaskedObjectCount,
                sceneData.TransparentObjectCount,
                sceneData.OpaqueMeshletCount,
                sceneData.TransparentMeshletCount,
                submittedOpaqueMeshlets,
                sceneData.ForwardFrustumCulledMeshletsGpu,
                sceneData.ForwardOcclusionCulledMeshletsGpu,
                forwardCandidates,
                forwardVisibleAfterOcclusion,
                sceneData.BlendMaterialCount,
                sceneData.UploadedBytes,
                sceneData.LightCount,
                sceneData.TileCountX,
                sceneData.TileCountY,
                sceneData.MaterialCount,
                _textureManager.TextureCount,
                _textureManager.LoadedFileTextureCount,
                _textureManager.MipmapFallbackCount,
                _textureManager.DownscaledTextureCount,
                _textureManager.MaxLoadedTextureDimension,
                _textureManager.EstimatedTextureBytes,
                uploadDiagnostics.ModelName,
                uploadDiagnostics.RenderObjectCount,
                uploadDiagnostics.RegisteredMeshCount,
                uploadDiagnostics.LoadedMaterialCount,
                uploadDiagnostics.LoadedTextureCount,
                uploadDiagnostics.DefaultWhiteSubstitutions,
                uploadDiagnostics.DefaultNormalSubstitutions,
                uploadDiagnostics.DefaultBlackSubstitutions,
                sceneData.CpuSceneBuildMicroseconds,
                sceneData.GpuDepthPrePassMicroseconds,
                sceneData.GpuHiZBuildMicroseconds,
                sceneData.GpuForwardOpaqueMicroseconds,
                sceneData.GpuTransparentMicroseconds,
                sceneData.SceneUploadCount,
                sceneData.SceneUploadSkipped,
                sceneData.ObjectCandidatesCpu,
                sceneData.ObjectFrustumCulledCpu,
                sceneData.MeshletCandidatesCpu,
                sceneData.MeshletFrustumCulledCpu,
                sceneData.MeshletLodSkippedCpu,
                sceneData.MeshletLod0SubmittedCpu,
                sceneData.MeshletLod1SubmittedCpu,
                sceneData.MeshletLod2SubmittedCpu,
                sceneData.CpuPayloadSignatureMicroseconds,
                sceneData.CpuObjectCullMicroseconds,
                sceneData.CpuMeshletCullMicroseconds,
                sceneData.CpuUploadMicroseconds,
                sceneData.CpuMaterialUploadMicroseconds,
                sceneData.CpuTotalDrawSceneMicroseconds,
                sceneData.CpuDirectionalShadowRecordMicroseconds,
                sceneData.CpuSpotShadowRecordMicroseconds,
                sceneData.CpuPointShadowRecordMicroseconds,
                sceneData.CpuDepthPrePassRecordMicroseconds,
                sceneData.CpuHiZBuildRecordMicroseconds,
                sceneData.CpuLightCullRecordMicroseconds,
                sceneData.CpuForwardOpaqueRecordMicroseconds,
                sceneData.CpuTransparentRecordMicroseconds,
                sceneData.CpuBloomExtractRecordMicroseconds,
                sceneData.CpuBloomDownsampleRecordMicroseconds,
                sceneData.CpuBloomUpsampleRecordMicroseconds,
                sceneData.CpuFogRecordMicroseconds,
                sceneData.CpuCompositeRecordMicroseconds,
                sceneData.GpuLightCullMicroseconds,
                sceneData.DepthTaskInvocations,
                sceneData.DepthFrustumCulledMeshletsGpu,
                sceneData.DepthEmittedMeshletsGpu,
                sceneData.ForwardTaskInvocations,
                sceneData.ForwardFrustumCulledMeshletsGpu,
                sceneData.ForwardOcclusionTestedMeshletsGpu,
                sceneData.ForwardEmittedMeshletsGpu,
                sceneData.MeshletCountTotal,
                sceneData.MeshletCountSubmittedCpu,
                sceneData.AvgTrianglesPerSubmittedMeshlet,
                sceneData.AvgVerticesPerSubmittedMeshlet,
                sceneData.SmallMeshletsUnder16Triangles,
                sceneData.SmallMeshletsUnder32Triangles,
                sceneData.ScenePayloadRebuilt,
                sceneData.ObjectUploadBytes,
                sceneData.InstanceUploadBytes,
                sceneData.MeshletDrawUploadBytes,
                sceneData.TransparentMeshletDrawUploadBytes,
                sceneData.MaterialUploadBytes,
                sceneData.LightUploadBytes,
                sceneData.DepthPrePassEnabled ? 1 : 0,
                sceneData.HiZBuildEnabled ? 1 : 0,
                sceneData.OcclusionCullingEnabled ? 1 : 0,
                sceneData.HiZMipCount,
                sceneData.HiZWidth,
                sceneData.HiZHeight,
                sceneData.DirectionalShadowPassEnabled ? 1 : 0,
                sceneData.DirectionalShadowMapSize,
                sceneData.DirectionalShadowCascadeCount,
                sceneData.ShadowedDirectionalLightIndex,
                sceneData.ShadowDebugView,
                sceneData.ShadowNormalBias,
                sceneData.ShadowSlopeScaledDepthBias,
                sceneData.DirectionalShadowPcfRadius,
                sceneData.SpotShadowPcfRadius,
                sceneData.PointShadowPcfRadius,
                sceneData.ForwardShadowReceiverMeshletCount,
                sceneData.SpotShadowsEnabled ? 1 : 0,
                sceneData.SpotShadowCandidateCount,
                sceneData.SpotShadowSelectedCount,
                sceneData.SpotShadowRejectedByBudgetCount,
                sceneData.SpotShadowAtlasSize,
                sceneData.SpotShadowTileSize,
                sceneData.SpotShadowAtlasCapacity,
                sceneData.SpotShadowAtlasUsedTiles,
                sceneData.PointShadowsEnabled ? 1 : 0,
                sceneData.PointShadowCandidateCount,
                sceneData.PointShadowSelectedCount,
                sceneData.PointShadowRejectedByBudgetCount,
                sceneData.PointShadowMapSize,
                sceneData.PointShadowRenderedFaceCount,
                HdrEnabled: 1,
                SceneColorFormat: RenderTargetManager.SceneColorFormat.ToString(),
                Exposure: sceneData.EffectiveExposure,
                ToneMapper: Settings.ToneMapper,
                BloomEnabled: sceneData.BloomEnabled ? 1 : 0,
                BloomMipCount: sceneData.BloomMipCount,
                BloomBaseWidth: sceneData.BloomBaseWidth,
                BloomBaseHeight: sceneData.BloomBaseHeight,
                BloomFormat: RenderTargetManager.SceneColorFormat.ToString(),
                BloomIntensity: Settings.Bloom.Intensity,
                BloomThreshold: Settings.Bloom.Threshold,
                BloomKnee: Settings.Bloom.Knee,
                BloomRadius: Settings.Bloom.Radius,
                BloomDebugView: Settings.Bloom.DebugView,
                BloomDebugMipLevel: Settings.Bloom.DebugMipLevel,
                FogEnabled: sceneData.FogEnabled ? 1 : 0,
                FogMode: sceneData.FogMode,
                FogColorMode: sceneData.FogColorMode,
                FogDebugView: sceneData.FogDebugView,
                FogDensity: sceneData.FogDensity,
                FogStartDistance: sceneData.FogStartDistance,
                FogEndDistance: sceneData.FogEndDistance,
                FogHeight: sceneData.FogHeight,
                FogHeightFalloff: sceneData.FogHeightFalloff,
                FogHeightDensity: sceneData.FogHeightDensity,
                FogMaxOpacity: sceneData.FogMaxOpacity,
                FogDirectionalInscatteringEnabled: sceneData.FogDirectionalInscatteringEnabled,
                FogWidth: sceneData.FogWidth,
                FogHeightPixels: sceneData.FogHeightPixels,
                FogFormat: sceneData.FogFormat,
                GpuFogMicroseconds: sceneData.GpuFogMicroseconds,
                AmbientOcclusionEnabled: sceneData.AmbientOcclusionEnabled ? 1 : 0,
                AmbientOcclusionMode: sceneData.AmbientOcclusionMode,
                AmbientOcclusionDebugView: sceneData.AmbientOcclusionDebugView,
                AmbientOcclusionForwardSamplingMode: sceneData.AmbientOcclusionForwardSamplingMode,
                AmbientOcclusionForwardDepthAwareSamples: sceneData.AmbientOcclusionForwardDepthAwareSamples,
                AmbientOcclusionWidth: sceneData.AmbientOcclusionWidth,
                AmbientOcclusionHeight: sceneData.AmbientOcclusionHeight,
                AmbientOcclusionFormat: sceneData.AmbientOcclusionFormat,
                AmbientOcclusionResolutionScale: sceneData.AmbientOcclusionResolutionScale,
                AmbientOcclusionRadius: sceneData.AmbientOcclusionRadius,
                AmbientOcclusionIntensity: sceneData.AmbientOcclusionIntensity,
                AmbientOcclusionBias: sceneData.AmbientOcclusionBias,
                AmbientOcclusionSampleCount: sceneData.AmbientOcclusionSampleCount,
                AmbientOcclusionBlurRadius: sceneData.AmbientOcclusionBlurRadius,
                CpuAmbientOcclusionRecordMicroseconds: sceneData.CpuAmbientOcclusionRecordMicroseconds,
                CpuAmbientOcclusionBlurRecordMicroseconds: sceneData.CpuAmbientOcclusionBlurRecordMicroseconds,
                GpuAmbientOcclusionMicroseconds: sceneData.GpuAmbientOcclusionMicroseconds,
                GpuAmbientOcclusionBlurMicroseconds: sceneData.GpuAmbientOcclusionBlurMicroseconds,
                AntiAliasingMode: sceneData.AntiAliasingMode,
                AntiAliasingDebugView: sceneData.AntiAliasingDebugView,
                AntiAliasingWidth: sceneData.AntiAliasingWidth,
                AntiAliasingHeight: sceneData.AntiAliasingHeight,
                AntiAliasingInputFormat: sceneData.AntiAliasingInputFormat,
                AntiAliasingOutputFormat: sceneData.AntiAliasingOutputFormat,
                CpuFxaaRecordMicroseconds: sceneData.CpuFxaaRecordMicroseconds,
                CpuSmaaEdgeRecordMicroseconds: sceneData.CpuSmaaEdgeRecordMicroseconds,
                CpuSmaaBlendRecordMicroseconds: sceneData.CpuSmaaBlendRecordMicroseconds,
                CpuSmaaNeighborhoodRecordMicroseconds: sceneData.CpuSmaaNeighborhoodRecordMicroseconds,
                GpuAntiAliasingMicroseconds: sceneData.GpuAntiAliasingMicroseconds,
                SmaaLookupTexturesReady: sceneData.SmaaLookupTexturesReady,
                MotionVectorsEnabled: sceneData.MotionVectorsEnabled,
                JitterEnabled: sceneData.JitterEnabled,
                JitterX: sceneData.JitterX,
                JitterY: sceneData.JitterY,
                EnvironmentEnabled: Settings.Environment.Enabled ? 1 : 0,
                EnvironmentSourceKind: Settings.Environment.SourceKind,
                EnvironmentSourcePath: Settings.Environment.SourcePath ?? string.Empty,
                EnvironmentUsesFallback: _environmentManager?.UsesFallback == true ? 1 : 0,
                EnvironmentCubemapSize: _environmentManager?.EnvironmentSize ?? 0,
                IrradianceCubemapSize: _environmentManager?.IrradianceSize ?? 0,
                PrefilteredEnvironmentSize: _environmentManager?.PrefilteredSize ?? 0,
                PrefilteredEnvironmentMipCount: _environmentManager?.PrefilteredMipCount ?? 0,
                BrdfLutSize: _environmentManager?.BrdfLutSize ?? 0,
                SkyIntensity: Settings.Environment.SkyIntensity,
                DiffuseIblIntensity: Settings.Environment.DiffuseIntensity,
                SpecularIblIntensity: Settings.Environment.SpecularIntensity,
                EnvironmentDebugView: Settings.Environment.DebugView,
                EnvironmentDebugMipLevel: Settings.Environment.DebugMipLevel,
                EnvironmentTextureBytes: _environmentManager?.EstimatedBytes ?? 0,
                ReflectionsEnabled: sceneData.ReflectionsEnabled ? 1 : 0,
                ReflectionMode: sceneData.ReflectionMode,
                ReflectionDebugView: sceneData.ReflectionDebugView,
                ReflectionProbeCount: sceneData.ReflectionProbeCount,
                ReflectionProbeCapacity: sceneData.ReflectionProbeCapacity,
                MaxReflectionProbesPerPixel: sceneData.MaxReflectionProbesPerPixel,
                ReflectionProbeResolution: sceneData.ReflectionProbeResolution,
                ReflectionProbeMipCount: sceneData.ReflectionProbeMipCount,
                ReflectionProbeEstimatedBytes: sceneData.ReflectionProbeEstimatedBytes,
                ReflectionProbeCapturesQueued: sceneData.ReflectionProbeCapturesQueued,
                ReflectionProbeCapturesCompleted: sceneData.ReflectionProbeCapturesCompleted,
                CpuReflectionProbeUploadMicroseconds: sceneData.CpuReflectionProbeUploadMicroseconds,
                CpuReflectionProbeCaptureRecordMicroseconds: sceneData.CpuReflectionProbeCaptureRecordMicroseconds,
                CpuReflectionProbePrefilterRecordMicroseconds: sceneData.CpuReflectionProbePrefilterRecordMicroseconds,
                GpuReflectionProbeCaptureMicroseconds: sceneData.GpuReflectionProbeCaptureMicroseconds,
                GpuReflectionProbePrefilterMicroseconds: sceneData.GpuReflectionProbePrefilterMicroseconds)
            {
                StableSceneInputUploadBytes = sceneData.StableSceneInputUploadBytes,
                CpuCandidateListUploadBytes = sceneData.CpuCandidateListUploadBytes,
                CameraDrivenCpuDrawListRebuilt = sceneData.CameraDrivenCpuDrawListRebuilt,
                SolidObjectCount = sceneData.SolidObjectCount,
                GeometryDecalObjectCount = sceneData.GeometryDecalObjectCount,
                SolidMeshletCount = sceneData.SolidMeshletCount,
                MaskedMeshletCount = sceneData.MaskedMeshletCount,
                GeometryDecalMeshletCount = sceneData.GeometryDecalMeshletCount,
                ForwardSimpleMeshletCount = sceneData.ForwardSimpleMeshletCount,
                ForwardFullMaterialMeshletCount = sceneData.ForwardFullMaterialMeshletCount,
                ForwardLocalProbeMeshletCount = sceneData.ForwardLocalProbeMeshletCount,
                MaskMaterialCount = sceneData.MaskMaterialCount,
                GeometryDecalMaterialCount = sceneData.GeometryDecalMaterialCount,
                TransparentSortCandidateCount = sceneData.TransparentSortCandidateCount,
                TransparentSortMicroseconds = sceneData.TransparentSortMicroseconds,
                TransparentOverflowCount = sceneData.TransparentOverflowCount,
                StaticInstanceBatchCount = sceneData.StaticInstanceBatchCount,
                StaticInstanceCount = sceneData.StaticInstanceCount,
                VisibleStaticInstanceCount = sceneData.VisibleStaticInstanceCount,
                CulledStaticInstanceCount = sceneData.CulledStaticInstanceCount,
                StaticBatchMeshletDrawCommandCount = sceneData.StaticBatchMeshletDrawCommandCount,
                CpuStaticBatchBuildMicroseconds = sceneData.CpuStaticBatchBuildMicroseconds,
                TransparencyMode = sceneData.TransparencyMode,
                TransparencyDebugView = sceneData.TransparencyDebugView,
                DecalDebugView = sceneData.DecalDebugView,
                TransparentReceiveShadows = sceneData.TransparentReceiveShadows ? 1 : 0,
                WeightedOitEnabled = sceneData.TransparentPassEnabled && sceneData.TransparencyMode == TransparencyMode.WeightedBlendedOit ? 1 : 0,
                WeightedOitRenderTargetBytes = _renderTargets?.WeightedOitRenderTargetBytes ?? 0,
                WeightedOitRenderTargetCount = _renderTargets == null ? 0 : 2,
                GlobalIlluminationRequested = giRequested ? 1 : 0,
                GlobalIlluminationRequestedMode = giSettings.Mode,
                GlobalIlluminationRequestedDebugView = giSettings.DebugView,
                GlobalIlluminationEmergencyFallbackEnabled = giSettings.EmergencyGiFallbackEnabled ? 1 : 0,
                GlobalIlluminationFallbackReason = globalIlluminationFallbackReason,
                GlobalIlluminationSsgiRequested = ssgiRequested ? 1 : 0,
                GlobalIlluminationDdgiRequested = ddgiRequested ? 1 : 0,
                SimpleDdgiRequested = simpleDdgiRequested ? 1 : 0,
                GlobalIlluminationRayQueryRequested = rayQueryGiRequested ? 1 : 0,
                GlobalIlluminationIndirectIntensity = giSettings.IndirectIntensity,
                GlobalIlluminationEnvironmentFallbackIntensity = giSettings.EnvironmentFallbackIntensity,
                GlobalIlluminationEnabled = giEnabled ? 1 : 0,
                GlobalIlluminationMode = giEnabled ? effectiveGiMode : GlobalIlluminationMode.Disabled,
                GlobalIlluminationDebugView = giEnabled ? giSettings.DebugView : GlobalIlluminationDebugView.None,
                GlobalIlluminationRayQuerySupported = giRayQuerySupported ? 1 : 0,
                GlobalIlluminationRayQueryActive = giRayQueryActive ? 1 : 0,
                GlobalIlluminationSsgiActive = giUsesSsgi ? 1 : 0,
                GlobalIlluminationDdgiActive = giUsesDdgi ? 1 : 0,
                SimpleDdgiActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiActive : 0,
                SimpleDdgiStructuredGatherEnabled = giUsesSimpleDdgi &&
                    sceneData.SimpleDdgiActive != 0 &&
                    giRayQueryActive &&
                    giSettings.SimpleDdgiStructuredGatherEnabled ? 1 : 0,
                SimpleDdgiReducedBlendEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiReducedBlendEnabled ? 1 : 0,
                SimpleDdgiToroidalScrollingEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiToroidalScrollingEnabled ? 1 : 0,
                SimpleDdgiRegionalInvalidationEnabled = giUsesSimpleDdgi && giSettings.SimpleDdgiRegionalInvalidationEnabled ? 1 : 0,
                FarFieldPagedFeatureEnabled = simpleDdgiRequested &&
                    giSettings.FarFieldClipmapEnabled &&
                    giSettings.FarFieldPagedEnabled ? 1 : 0,
                StreamedGiAccelerationStructuresFeatureEnabled = ddgiRequested && giSettings.StreamedGiAccelerationStructuresEnabled ? 1 : 0,
                DdgiDetailedCountersRequested = ddgiRequested &&
                    (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                     giSettings.DebugView != GlobalIlluminationDebugView.None) ? 1 : 0,
                DdgiDetailedCountersEnabled = giUsesDdgi &&
                    (Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                     giSettings.DebugView != GlobalIlluminationDebugView.None) ? 1 : 0,
                SimpleDdgiProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeCount : 0,
                SimpleDdgiProbesUpdated = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbesUpdated : 0,
                SimpleDdgiRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiRaysPerFrame : 0UL,
                SimpleDdgiTransportV2Active = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportV2Active : 0,
                SimpleDdgiAutomaticProbeDensityActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiAutomaticProbeDensityActive : 0,
                SimpleDdgiTransportSourceRefreshProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshProbeCount : 0,
                SimpleDdgiTransportSourceCacheReuseProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheReuseProbeCount : 0,
                SimpleDdgiTransportSourceRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRayCount : 0UL,
                SimpleDdgiTransportSolveRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolveRayCount : 0UL,
                SimpleDdgiTransportPublishedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishedProbeCount : 0,
                SimpleDdgiTransportPublishRegionCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishRegionCount : 0,
                SimpleDdgiTransportPublishedProbeTotal = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishedProbeTotal : 0UL,
                SimpleDdgiTransportPublishRegionTotal = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPublishRegionTotal : 0UL,
                SimpleDdgiUpdateTransactionAbortCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiUpdateTransactionAbortCount : 0UL,
                SimpleDdgiTransportSourceCacheInvalidationCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheInvalidationCount : 0UL,
                SimpleDdgiSourceLightingGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiSourceLightingGeneration : 0u,
                SimpleDdgiTransportGeneration = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGeneration : 0u,
                SimpleDdgiTransportSourceReadyProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceReadyProbeCount : 0,
                SimpleDdgiTransportSourceStaleProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceStaleProbeCount : 0,
                SimpleDdgiTransportConvergedProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportConvergedProbeCount : 0,
                SimpleDdgiTransportPendingSolverProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportPendingSolverProbeCount : 0,
                SimpleDdgiTransportGlobalConvergencePending = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGlobalConvergencePending : 0,
                SimpleDdgiTransportGlobalConvergenceElapsedFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportGlobalConvergenceElapsedFrames : 0,
                SimpleDdgiTransportCalibrationChangeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportCalibrationChangeCount : 0UL,
                SimpleDdgiTransportIrradianceAtlasBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportIrradianceAtlasBytes : 0UL,
                SimpleDdgiTransportSourceCacheBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheBytes : 0UL,
                SimpleDdgiTransportSolverRelaxation = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSolverRelaxation : 0.0f,
                SimpleDdgiTransportAlbedoClamp = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportAlbedoClamp : 0.0f,
                SimpleDdgiTransportResidualThreshold = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportResidualThreshold : 0.0f,
                SimpleDdgiTransportMaximumSolverGenerations = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportMaximumSolverGenerations : 0,
                SimpleDdgiTransportSourceRefreshFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceRefreshFrames : 0,
                SimpleDdgiInactiveProbeCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiInactiveProbeCount : 0,
                SimpleDdgiInactiveProbeSkipCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiInactiveProbeSkipCount : 0,
                SimpleDdgiSavedRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiSavedRaysPerFrame : 0UL,
                SimpleDdgiLightingDirtyFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiLightingDirtyFrames : 0,
                SimpleDdgiLightingDirtyBoostedCapacity = giUsesSimpleDdgi ? sceneData.SimpleDdgiLightingDirtyBoostedCapacity : 0,
                SimpleDdgiDirtyReasonFlags = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyReasonFlags : 0,
                SimpleDdgiFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFullRayProbeUpdateCount : 0,
                SimpleDdgiMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaintenanceRayProbeUpdateCount : 0,
                SimpleDdgiAdaptiveRaySavedRaysPerFrame = giUsesSimpleDdgi ? sceneData.SimpleDdgiAdaptiveRaySavedRaysPerFrame : 0UL,
                SimpleDdgiNearFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearFullRayProbeUpdateCount : 0,
                SimpleDdgiMidFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidFullRayProbeUpdateCount : 0,
                SimpleDdgiFarFullRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarFullRayProbeUpdateCount : 0,
                SimpleDdgiNearMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearMaintenanceRayProbeUpdateCount : 0,
                SimpleDdgiMidMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidMaintenanceRayProbeUpdateCount : 0,
                SimpleDdgiFarMaintenanceRayProbeUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarMaintenanceRayProbeUpdateCount : 0,
                SimpleDdgiNearScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNearScheduledPrimaryRayCount : 0UL,
                SimpleDdgiMidScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiMidScheduledPrimaryRayCount : 0UL,
                SimpleDdgiFarScheduledPrimaryRayCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFarScheduledPrimaryRayCount : 0UL,
                SimpleDdgiDirtyFirstUpdateLatencySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencySampleCount : 0,
                SimpleDdgiDirtyFirstUpdateLatencyP50Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyP50Frames : 0,
                SimpleDdgiDirtyFirstUpdateLatencyP95Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyP95Frames : 0,
                SimpleDdgiDirtyFirstUpdateLatencyMaxFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyFirstUpdateLatencyMaxFrames : 0,
                SimpleDdgiOldestVisibleUnsupportedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge : 0,
                SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget : 0,
                SimpleDdgiVisibleZeroSupportRepairUpdateCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiVisibleZeroSupportRepairUpdateCount : 0,
                SimpleDdgiProbeLifecycleLatencyTargetFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames : 0,
                SimpleDdgiMaximumFreshProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumFreshProbeAge : 0,
                SimpleDdgiMaximumScrollExposedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumScrollExposedProbeAge : 0,
                SimpleDdgiMaximumRelocationPendingProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumRelocationPendingProbeAge : 0,
                SimpleDdgiMaximumUnpublishedProbeAge = giUsesSimpleDdgi ? sceneData.SimpleDdgiMaximumUnpublishedProbeAge : 0,
                SimpleDdgiProbeLifecycleBoundExceededCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiProbeLifecycleBoundExceededCount : 0,
                SimpleDdgiDirtyConvergenceLatencySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencySampleCount : 0,
                SimpleDdgiDirtyConvergenceLatencyP50Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyP50Frames : 0,
                SimpleDdgiDirtyConvergenceLatencyP95Frames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyP95Frames : 0,
                SimpleDdgiDirtyConvergenceLatencyMaxFrames = giUsesSimpleDdgi ? sceneData.SimpleDdgiDirtyConvergenceLatencyMaxFrames : 0,
                SimpleDdgiAtlasBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasBytes : 0UL,
                SimpleDdgiSampledAtlasRequested = simpleDdgiRequested && giSettings.SimpleDdgiSampledAtlasEnabled ? 1 : 0,
                SimpleDdgiSampledAtlasActive = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasActive : 0,
                SimpleDdgiSampledAtlasGroupCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasGroupCount : 0,
                SimpleDdgiSampledAtlasLayersPerTexture = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasLayersPerTexture : 0,
                SimpleDdgiSampledAtlasImageBytes = giUsesSimpleDdgi ? sceneData.SimpleDdgiSampledAtlasImageBytes : 0UL,
                SimpleDdgiSampledAtlasFallbackReason = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiSampledAtlasFallbackReason
                    : simpleDdgiRequested && giSettings.EmergencyGiFallbackEnabled
                        ? "Emergency GI fallback is active."
                        : string.Empty,
                FarFieldPagedMode = giUsesSimpleDdgi ? sceneData.FarFieldPagedMode : 0,
                FarFieldPagePoolCapacity = giUsesSimpleDdgi ? sceneData.FarFieldPagePoolCapacity : 0,
                FarFieldResidentPageCount = giUsesSimpleDdgi ? sceneData.FarFieldResidentPageCount : 0,
                FarFieldPendingPageCount = giUsesSimpleDdgi ? sceneData.FarFieldPendingPageCount : 0,
                FarFieldPageRequestCount = giUsesSimpleDdgi ? sceneData.FarFieldPageRequestCount : 0,
                FarFieldPageMissCount = giUsesSimpleDdgi ? sceneData.FarFieldPageMissCount : 0,
                FarFieldPageRebuildCount = giUsesSimpleDdgi ? sceneData.FarFieldPageRebuildCount : 0,
                FarFieldPageEvictionCount = giUsesSimpleDdgi ? sceneData.FarFieldPageEvictionCount : 0,
                FarFieldScheduledPageBakeCount = giUsesSimpleDdgi ? sceneData.FarFieldScheduledPageBakeCount : 0,
                FarFieldCacheBytes = giUsesSimpleDdgi ? sceneData.FarFieldCacheBytes : 0UL,
                FarFieldMemoryBudgetBytes = simpleDdgiRequested &&
                    giSettings.FarFieldClipmapEnabled
                    ? giSettings.FarFieldMemoryBudgetBytes
                    : 0UL,
                FarFieldInstanceBufferBytes = giUsesSimpleDdgi ? sceneData.FarFieldInstanceBufferBytes : 0UL,
                FarFieldPageTableBytes = giUsesSimpleDdgi ? sceneData.FarFieldPageTableBytes : 0UL,
                SimpleDdgiRecentered = giUsesSimpleDdgi ? sceneData.SimpleDdgiRecentered : 0,
                SimpleDdgiAtlasPreservedOnRecenter = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasPreservedOnRecenter : 0,
                SimpleDdgiAtlasCleared = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasCleared : 0,
                SimpleDdgiAtlasFresh = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasFresh : 0,
                SimpleDdgiRecenterCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRecenterCount : 0,
                SimpleDdgiAtlasClearCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasClearCount : 0,
                SimpleDdgiAtlasPreserveOnRecenterCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiAtlasPreserveOnRecenterCount : 0,
                SimpleDdgiFramesSinceLastClear = giUsesSimpleDdgi ? sceneData.SimpleDdgiFramesSinceLastClear : 0,
                SimpleDdgiFramesSinceLastRecenter = giUsesSimpleDdgi ? sceneData.SimpleDdgiFramesSinceLastRecenter : 0,
                DdgiInvestigationCountersReadbackValid = giUsesDdgi ? sceneData.DdgiInvestigationCountersReadbackValid : 0,
                SimpleDdgiFreshAtlasForwardSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiFreshAtlasForwardSampleCount : 0,
                SimpleDdgiZeroIrradianceSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiZeroIrradianceSampleCount : 0,
                SimpleDdgiNonzeroIrradianceSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiNonzeroIrradianceSampleCount : 0,
                SimpleDdgiAverageSampledIrradianceLuminance = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageSampledIrradianceLuminance : 0.0f,
                SimpleDdgiAverageVisibility = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageVisibility : 0.0f,
                SimpleDdgiLowVisibilitySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiLowVisibilitySampleCount : 0,
                SimpleDdgiGatherSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiGatherSampleCount : 0,
                SimpleDdgiSecondVolumeGatherCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSecondVolumeGatherCount : 0,
                SimpleDdgiGatherPrimaryRejectionCounts = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherPrimaryRejectionCounts
                    : Array.Empty<uint>(),
                SimpleDdgiGatherFallbackRejectionCounts = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherFallbackRejectionCounts
                    : Array.Empty<uint>(),
                SimpleDdgiGatherRecoveryRejectionCounts = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherRecoveryRejectionCounts
                    : Array.Empty<uint>(),
                SimpleDdgiGatherPrimaryAllFailedCount = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherPrimaryAllFailedCount
                    : 0,
                SimpleDdgiGatherFallbackAllFailedCount = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherFallbackAllFailedCount
                    : 0,
                SimpleDdgiGatherRecoveryAllFailedCount = giUsesSimpleDdgi
                    ? sceneData.SimpleDdgiGatherRecoveryAllFailedCount
                    : 0,
                DdgiFullRefreshFrameCount = giUsesDdgi ? sceneData.DdgiFullRefreshFrameCount : 0,
                DdgiPartialRefreshFrameCount = giUsesDdgi ? sceneData.DdgiPartialRefreshFrameCount : 0,
                DdgiUpdatedProbeFraction = giUsesDdgi ? sceneData.DdgiUpdatedProbeFraction : 0.0f,
                DdgiProbeUpdateStartIndex = giUsesDdgi ? sceneData.DdgiProbeUpdateStartIndex : 0,
                DdgiProbeUpdateEndIndex = giUsesDdgi ? sceneData.DdgiProbeUpdateEndIndex : 0,
                DdgiSkippedProbeCount = giUsesDdgi ? sceneData.DdgiSkippedProbeCount : 0,
                DdgiFramesSinceProbeUpdatedP50 = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedP50 : 0.0f,
                DdgiFramesSinceProbeUpdatedP95 = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedP95 : 0.0f,
                DdgiFramesSinceProbeUpdatedMax = giUsesDdgi ? sceneData.DdgiFramesSinceProbeUpdatedMax : 0.0f,
                DdgiNewlyInvalidatedProbeCount = giUsesDdgi ? sceneData.DdgiNewlyInvalidatedProbeCount : 0,
                DdgiRefreshReasonRecenterProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonRecenterProbeCount : 0,
                DdgiRefreshReasonDirtyProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonDirtyProbeCount : 0,
                DdgiRefreshReasonAgeProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonAgeProbeCount : 0,
                DdgiRefreshReasonVisibilityProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonVisibilityProbeCount : 0,
                DdgiRefreshReasonFullRefreshProbeCount = giUsesDdgi ? sceneData.DdgiRefreshReasonFullRefreshProbeCount : 0,
                DdgiForwardSimplePathSampleCount = giUsesDdgi ? sceneData.DdgiForwardSimplePathSampleCount : 0,
                DdgiForwardLegacyPathSampleCount = giUsesDdgi ? sceneData.DdgiForwardLegacyPathSampleCount : 0,
                DdgiForwardZeroFinalIndirectCount = giUsesDdgi ? sceneData.DdgiForwardZeroFinalIndirectCount : 0,
                DdgiForwardZeroDdgiButNonzeroIblCount = giUsesDdgi ? sceneData.DdgiForwardZeroDdgiButNonzeroIblCount : 0,
                DdgiForwardZeroDdgiAndZeroIblCount = giUsesDdgi ? sceneData.DdgiForwardZeroDdgiAndZeroIblCount : 0,
                DdgiForwardOutOfGridSampleCount = giUsesDdgi ? sceneData.DdgiForwardOutOfGridSampleCount : 0,
                DdgiForwardClampedProbeSampleCount = giUsesDdgi ? sceneData.DdgiForwardClampedProbeSampleCount : 0,
                DdgiForwardNanOrInfSampleCount = giUsesDdgi ? sceneData.DdgiForwardNanOrInfSampleCount : 0,
                DdgiIrradianceAtlasZeroTexelSampleCount = giUsesDdgi ? sceneData.DdgiIrradianceAtlasZeroTexelSampleCount : 0,
                DdgiVisibilityAtlasZeroMomentSampleCount = giUsesDdgi ? sceneData.DdgiVisibilityAtlasZeroMomentSampleCount : 0,
                DdgiAtlasWriteProbeCount = giUsesDdgi ? sceneData.DdgiAtlasWriteProbeCount : 0,
                DdgiAtlasWriteTexelCount = giUsesDdgi ? sceneData.DdgiAtlasWriteTexelCount : 0,
                DdgiBlendZeroRayWeightProbeCount = giUsesDdgi ? sceneData.DdgiBlendZeroRayWeightProbeCount : 0,
                DdgiBlendNonzeroIrradianceProbeCount = giUsesDdgi ? sceneData.DdgiBlendNonzeroIrradianceProbeCount : 0,
                DdgiBlendPreviousAtlasUsedCount = giUsesDdgi ? sceneData.DdgiBlendPreviousAtlasUsedCount : 0,
                DdgiBlendHysteresisZeroFrameCount = giUsesDdgi ? sceneData.DdgiBlendHysteresisZeroFrameCount : 0,
                DdgiSimpleTraceHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceHitCount : 0,
                DdgiSimpleTraceMissCount = giUsesDdgi ? sceneData.DdgiSimpleTraceMissCount : 0,
                DdgiSimpleTraceZeroRadianceHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceZeroRadianceHitCount : 0,
                DdgiSimpleTraceDirectLightHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceDirectLightHitCount : 0,
                DdgiSimpleTraceEmissiveHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceEmissiveHitCount : 0,
                DdgiSimpleTraceFarFieldHitCount = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldHitCount : 0,
                DdgiSimpleTraceFarFieldMissCount = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldMissCount : 0,
                DdgiSimpleTraceTlasUnavailableFrameCount = giUsesDdgi ? sceneData.DdgiSimpleTraceTlasUnavailableFrameCount : 0,
                SimpleDdgiSkyVisibilitySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiSkyVisibilitySampleCount : 0,
                SimpleDdgiAverageSkyVisibility = giUsesSimpleDdgi ? sceneData.SimpleDdgiAverageSkyVisibility : 0.0f,
                FarFieldSunShadowSampleCount = giUsesSimpleDdgi ? sceneData.FarFieldSunShadowSampleCount : 0,
                FarFieldSunShadowOccludedCount = giUsesSimpleDdgi ? sceneData.FarFieldSunShadowOccludedCount : 0,
                SimpleDdgiRoughSpecularSampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRoughSpecularSampleCount : 0,
                SimpleDdgiRoughSpecularNonzeroCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiRoughSpecularNonzeroCount : 0,
                DdgiSimpleTraceFarFieldStepBucket0Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket0Count : 0,
                DdgiSimpleTraceFarFieldStepBucket1Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket1Count : 0,
                DdgiSimpleTraceFarFieldStepBucket2Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket2Count : 0,
                DdgiSimpleTraceFarFieldStepBucket3Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket3Count : 0,
                DdgiSimpleTraceFarFieldStepBucket4Count = giUsesDdgi ? sceneData.DdgiSimpleTraceFarFieldStepBucket4Count : 0,
                DdgiBlackFrameSuspect = giUsesDdgi ? sceneData.DdgiBlackFrameSuspect : 0,
                DdgiBlackFrameAfterRecenter = giUsesDdgi ? sceneData.DdgiBlackFrameAfterRecenter : 0,
                DdgiBlackFrameAfterAtlasClear = giUsesDdgi ? sceneData.DdgiBlackFrameAfterAtlasClear : 0,
                DdgiBlackFrameDuringFreshAtlas = giUsesDdgi ? sceneData.DdgiBlackFrameDuringFreshAtlas : 0,
                DdgiBlackFrameMovementClass = giUsesDdgi ? sceneData.DdgiBlackFrameMovementClass : DdgiCameraMovementClass.None,
                GpuForwardGiGatherMicroseconds = giEnabled ? sceneData.GpuForwardGiGatherMicroseconds : 0,
                GpuForwardGiGatherTimingCoverage = giEnabled ? sceneData.GpuForwardGiGatherTimingCoverage : 0,
                GpuForwardGiGatherTimingAttribution = giEnabled && sceneData.GpuForwardGiGatherTimingCoverage != 0
                    ? GiTimingAttribution.Inclusive
                    : GiTimingAttribution.Unavailable,
                // ForwardPlusPass currently records a whole forward draw. Preserve that raw
                // inclusive scope above, but never pretend it is an isolated GI timer.
                GpuForwardGiIncrementalMicroseconds = 0,
                GpuForwardGiIncrementalAttribution = GiTimingAttribution.Unavailable,
                GpuForwardGiIncrementalTimingReason = giUsesDdgi
                    ? "Forward GI gather is inside the inclusive forward draw; use a deterministic paired capture until an isolated scope exists."
                    : "Forward GI gather is inactive.",
                GpuFarFieldUpdateMicroseconds = giUsesSimpleDdgi ? sceneData.GpuFarFieldUpdateMicroseconds : 0,
                GpuFarFieldUpdateTimingValid = giUsesSimpleDdgi ? sceneData.GpuFarFieldUpdateTimingValid : 0,
                GpuSimpleDdgiTraceMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiTraceMicroseconds : 0,
                GpuSimpleDdgiTransportMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiTransportMicroseconds : 0,
                GpuSimpleDdgiBlendMicroseconds = giUsesSimpleDdgi ? sceneData.GpuSimpleDdgiBlendMicroseconds : 0,
                SsgiWidth = ssgiWidth,
                SsgiHeight = ssgiHeight,
                SsgiResolutionScale = ssgiRequested ? giSettings.ResolutionScale : 0f,
                SsgiRayCount = ssgiRayCount,
                SsgiHistoryValid = giUsesSsgi ? sceneData.SsgiHistoryValid : 0,
                SsgiRejectedHistoryPixelCount = giUsesSsgi ? sceneData.SsgiRejectedHistoryPixelCount : 0,
                DdgiProbeVolumeCount = giUsesDdgi ? sceneData.DdgiProbeVolumeCount : 0,
                DdgiProbeCount = giUsesDdgi ? sceneData.DdgiProbeCount : 0,
                DdgiActiveProbeCount = giUsesDdgi ? sceneData.DdgiActiveProbeCount : 0,
                DdgiProbesUpdated = giUsesDdgi ? sceneData.DdgiProbesUpdated : 0,
                DdgiRaysPerProbe = giUsesDdgi ? sceneData.DdgiRaysPerProbe : 0,
                DdgiMaxActiveProbeBudget = giUsesDdgi ? sceneData.DdgiMaxActiveProbeBudget : 0,
                DdgiMaxProbeUpdatesPerFrame = giUsesDdgi ? sceneData.DdgiMaxProbeUpdatesPerFrame : 0,
                DdgiProbeUpdateRequestBudget = giUsesDdgi ? sceneData.DdgiProbeUpdateRequestBudget : 0,
                DdgiProbeUpdatePrimaryRayBudget = giUsesDdgi ? sceneData.DdgiProbeUpdatePrimaryRayBudget : 0,
                DdgiScheduledRequestBudget = giUsesDdgi ? sceneData.DdgiScheduledRequestBudget : 0,
                DdgiScheduledPrimaryRayBudget = giUsesDdgi ? sceneData.DdgiScheduledPrimaryRayBudget : 0,
                DdgiGpuSchedulerPredictedRequestUpperBound = giUsesDdgi ? sceneData.DdgiGpuSchedulerPredictedRequestUpperBound : 0,
                DdgiGpuSchedulerActualRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerActualRequestCount : 0u,
                DdgiGpuSchedulerActualPrimaryRayCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerActualPrimaryRayCount : 0u,
                DdgiGatherTileCount = giUsesDdgi ? sceneData.DdgiGatherTileCount : 0,
                DdgiGatherTileCountX = giUsesDdgi ? sceneData.DdgiGatherTileCountX : 0,
                DdgiGatherTileCountY = giUsesDdgi ? sceneData.DdgiGatherTileCountY : 0,
                DdgiGatherSelectedLocalTileCount = giUsesDdgi ? sceneData.DdgiGatherSelectedLocalTileCount : 0,
                DdgiGatherSelectedClipmapTileCount = giUsesDdgi ? sceneData.DdgiGatherSelectedClipmapTileCount : 0,
                DdgiGatherFallbackTileCount = giUsesDdgi ? sceneData.DdgiGatherFallbackTileCount : 0,
                DdgiGatherSelectedLocalTileFraction = giUsesDdgi ? sceneData.DdgiGatherSelectedLocalTileFraction : 0.0f,
                DdgiGatherSelectedClipmapTileFraction = giUsesDdgi ? sceneData.DdgiGatherSelectedClipmapTileFraction : 0.0f,
                DdgiGatherFallbackTileFraction = giUsesDdgi ? sceneData.DdgiGatherFallbackTileFraction : 0.0f,
                DdgiForwardGatherFallbackUsed = giUsesDdgi ? sceneData.DdgiForwardGatherFallbackUsed : 0,
                DdgiForwardGatherFallbackDisabled = giUsesDdgi ? sceneData.DdgiForwardGatherFallbackDisabled : 0,
                DdgiForwardGatherTileEmpty = giUsesDdgi ? sceneData.DdgiForwardGatherTileEmpty : 0,
                DdgiAverageSpatialCoverageEstimate = giUsesDdgi ? sceneData.DdgiAverageSpatialCoverageEstimate : 0.0f,
                DdgiAverageSupportCoverageEstimate = giUsesDdgi ? sceneData.DdgiAverageSupportCoverageEstimate : 0.0f,
                DdgiAverageDataConfidenceEstimate = giUsesDdgi ? sceneData.DdgiAverageDataConfidenceEstimate : 0.0f,
                DdgiAverageVisibilityConfidenceEstimate = giUsesDdgi ? sceneData.DdgiAverageVisibilityConfidenceEstimate : 0.0f,
                DdgiAverageLeakAttenuationEstimate = giUsesDdgi ? sceneData.DdgiAverageLeakAttenuationEstimate : 0.0f,
                DdgiAverageEffectiveContributionEstimate = giUsesDdgi ? sceneData.DdgiAverageEffectiveContributionEstimate : 0.0f,
                DdgiAverageOwnershipConsumedEstimate = giUsesDdgi ? sceneData.DdgiAverageOwnershipConsumedEstimate : 0.0f,
                DdgiWarmupState = giUsesDdgi ? sceneData.DdgiWarmupState : DdgiRuntimeWarmupState.Disabled,
                DdgiWarmedVisibleProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedVisibleProbeFraction : 0.0f,
                DdgiWarmedLocalProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedLocalProbeFraction : 0.0f,
                DdgiWarmedCascade0ProbeFraction = giUsesDdgi ? sceneData.DdgiWarmedCascade0ProbeFraction : 0.0f,
                DdgiForwardEstimateCountersReadbackValid = giUsesDdgi ? sceneData.DdgiForwardEstimateCountersReadbackValid : 0,
                DdgiForwardEstimateSampleCount = giUsesDdgi ? sceneData.DdgiForwardEstimateSampleCount : 0u,
                DdgiForwardEstimateZeroVisibleButCoveredCount = giUsesDdgi ? sceneData.DdgiForwardEstimateZeroVisibleButCoveredCount : 0u,
                DdgiForwardEstimateZeroEffectiveButCoveredCount = giUsesDdgi ? sceneData.DdgiForwardEstimateZeroEffectiveButCoveredCount : 0u,
                DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount = giUsesDdgi ? sceneData.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount : 0u,
                DdgiForwardEstimateSampledIrradianceLuminance = giUsesDdgi ? sceneData.DdgiForwardEstimateSampledIrradianceLuminance : 0.0f,
                DdgiForwardEstimateRawDiffuseLuminance = giUsesDdgi ? sceneData.DdgiForwardEstimateRawDiffuseLuminance : 0.0f,
                DdgiForwardEstimateFinalDiffuseLuminance = giUsesDdgi ? sceneData.DdgiForwardEstimateFinalDiffuseLuminance : 0.0f,
                DdgiForwardEstimateEnvironmentFallbackWeight = giUsesDdgi ? sceneData.DdgiForwardEstimateEnvironmentFallbackWeight : 0.0f,
                DdgiSupportRejectedInactiveCount = giUsesDdgi ? sceneData.DdgiSupportRejectedInactiveCount : 0u,
                DdgiSupportRejectedZeroIrradianceAlphaCount = giUsesDdgi ? sceneData.DdgiSupportRejectedZeroIrradianceAlphaCount : 0u,
                DdgiSupportRejectedLowQualityCount = giUsesDdgi ? sceneData.DdgiSupportRejectedLowQualityCount : 0u,
                DdgiProbeIrradianceAlphaAverage = giUsesDdgi ? sceneData.DdgiProbeIrradianceAlphaAverage : 0.0f,
                DdgiProbeQualityXAverage = giUsesDdgi ? sceneData.DdgiProbeQualityXAverage : 0.0f,
                DdgiProbeQualityYAverage = giUsesDdgi ? sceneData.DdgiProbeQualityYAverage : 0.0f,
                DdgiProbeQualityZAverage = giUsesDdgi ? sceneData.DdgiProbeQualityZAverage : 0.0f,
                DdgiProbeQualitySampleCount = giUsesDdgi ? sceneData.DdgiProbeQualitySampleCount : 0u,
                DdgiSampledProbeCurrentFrustumCount = giUsesDdgi ? sceneData.DdgiSampledProbeCurrentFrustumCount : 0u,
                DdgiSampledProbeSideRearCount = giUsesDdgi ? sceneData.DdgiSampledProbeSideRearCount : 0u,
                DdgiSampledProbeStaleAgeCount = giUsesDdgi ? sceneData.DdgiSampledProbeStaleAgeCount : 0u,
                DdgiClipmapInfoPrimaryAttemptCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryAttemptCount : 0u,
                DdgiClipmapInfoPrimaryOkCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryOkCount : 0u,
                DdgiClipmapInfoPrimaryFailedCount = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryFailedCount : 0u,
                DdgiClipmapInfoPrimaryEdgeFadeAverage = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryEdgeFadeAverage : 0.0f,
                DdgiClipmapInfoPrimaryBlendWeightAverage = giUsesDdgi ? sceneData.DdgiClipmapInfoPrimaryBlendWeightAverage : 0.0f,
                DdgiFastGatherAttemptCount = giUsesDdgi ? sceneData.DdgiFastGatherAttemptCount : 0u,
                DdgiFastGatherAcceptedCount = giUsesDdgi ? sceneData.DdgiFastGatherAcceptedCount : 0u,
                DdgiFastGatherRejectedZeroSpatialCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroSpatialCount : 0u,
                DdgiFastGatherRejectedZeroSupportCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroSupportCount : 0u,
                DdgiFastGatherRejectedZeroDataCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroDataCount : 0u,
                DdgiFastGatherRejectedZeroOwnershipCount = giUsesDdgi ? sceneData.DdgiFastGatherRejectedZeroOwnershipCount : 0u,
                DdgiShaderGatherFallbackAttemptCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackAttemptCount : 0u,
                DdgiShaderGatherFallbackAcceptedCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackAcceptedCount : 0u,
                DdgiShaderGatherFallbackEmptyCount = giUsesDdgi ? sceneData.DdgiShaderGatherFallbackEmptyCount : 0u,
                DdgiTraceEnergySampleCount = giUsesDdgi ? sceneData.DdgiTraceEnergySampleCount : 0u,
                DdgiTraceEnergyHitCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitCount : 0u,
                DdgiTraceEnergyMissCount = giUsesDdgi ? sceneData.DdgiTraceEnergyMissCount : 0u,
                DdgiTraceEnergyRayLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyRayLuminanceAverage : 0.0f,
                DdgiTraceEnergyDirectLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyDirectLuminanceAverage : 0.0f,
                DdgiTraceEnergyEmissiveLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyEmissiveLuminanceAverage : 0.0f,
                DdgiTraceEnergyStableLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyStableLuminanceAverage : 0.0f,
                DdgiTraceEnergySkyLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergySkyLuminanceAverage : 0.0f,
                DdgiTraceEnergyHitZeroDirectCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitZeroDirectCount : 0u,
                DdgiTraceEnergyHitWithDirectCount = giUsesDdgi ? sceneData.DdgiTraceEnergyHitWithDirectCount : 0u,
                DdgiTraceEnergyDirectNoShadowLuminanceAverage = giUsesDdgi ? sceneData.DdgiTraceEnergyDirectNoShadowLuminanceAverage : 0.0f,
                DdgiShadowVisibilityRayCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityRayCount : 0u,
                DdgiShadowVisibilityOccludedCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityOccludedCount : 0u,
                DdgiShadowVisibilityNearHitCount = giUsesDdgi ? sceneData.DdgiShadowVisibilityNearHitCount : 0u,
                DdgiShadowVisibilityCommittedHitDistanceAverage = giUsesDdgi ? sceneData.DdgiShadowVisibilityCommittedHitDistanceAverage : 0.0f,
                DdgiTraceEarlyOutDisabledCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutDisabledCount : 0u,
                DdgiTraceEarlyOutBeyondRequestCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutBeyondRequestCount : 0u,
                DdgiTraceEarlyOutResolveBoundsCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveBoundsCount : 0u,
                DdgiTraceEarlyOutResolveProbeRangeCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveProbeRangeCount : 0u,
                DdgiTraceEarlyOutResolveClipmapCellCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveClipmapCellCount : 0u,
                DdgiTraceEarlyOutResolveClipmapRingCount = giUsesDdgi ? sceneData.DdgiTraceEarlyOutResolveClipmapRingCount : 0u,
                DdgiTraceRingMismatchCorrectedCount = giUsesDdgi ? sceneData.DdgiTraceRingMismatchCorrectedCount : 0u,
                DdgiTraceRingMismatchSample = giUsesDdgi ? sceneData.DdgiTraceRingMismatchSample : string.Empty,
                DdgiBlendEnergySampleCount = giUsesDdgi ? sceneData.DdgiBlendEnergySampleCount : 0u,
                DdgiBlendEnergyIrradianceLuminanceAverage = giUsesDdgi ? sceneData.DdgiBlendEnergyIrradianceLuminanceAverage : 0.0f,
                DdgiBlendEnergyConfidenceAverage = giUsesDdgi ? sceneData.DdgiBlendEnergyConfidenceAverage : 0.0f,
                DdgiBlendEnergyLowConfidenceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyLowConfidenceCount : 0u,
                DdgiBlendEnergyNonzeroIrradianceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyNonzeroIrradianceCount : 0u,
                DdgiBlendEnergyNonFiniteIrradianceCount = giUsesDdgi ? sceneData.DdgiBlendEnergyNonFiniteIrradianceCount : 0u,
                DdgiBlendEnergyFireflySuppressedCount = giUsesDdgi ? sceneData.DdgiBlendEnergyFireflySuppressedCount : 0u,
                SimpleDdgiTransportEnergySampleCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportEnergySampleCount : 0u,
                SimpleDdgiTransportSourceCacheHitCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheHitCount : 0u,
                SimpleDdgiTransportSourceCacheMissCount = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceCacheMissCount : 0u,
                SimpleDdgiTransportBounceLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportBounceLuminanceAverage : 0.0f,
                SimpleDdgiTransportSourceLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportSourceLuminanceAverage : 0.0f,
                SimpleDdgiTransportTotalLuminanceAverage = giUsesSimpleDdgi ? sceneData.SimpleDdgiTransportTotalLuminanceAverage : 0.0f,
                DdgiVisibilityMomentMeanAverage = giUsesDdgi ? sceneData.DdgiVisibilityMomentMeanAverage : 0.0f,
                DdgiVisibilityMomentVarianceAverage = giUsesDdgi ? sceneData.DdgiVisibilityMomentVarianceAverage : 0.0f,
                DdgiVisibilityProbeDistanceAverage = giUsesDdgi ? sceneData.DdgiVisibilityProbeDistanceAverage : 0.0f,
                DdgiVisibilityMomentSampleCount = giUsesDdgi ? sceneData.DdgiVisibilityMomentSampleCount : 0u,
                DdgiVisibilityLargeDistanceMarginCount = giUsesDdgi ? sceneData.DdgiVisibilityLargeDistanceMarginCount : 0u,
                DdgiVisibilityZeroTransportCount = giUsesDdgi ? sceneData.DdgiVisibilityZeroTransportCount : 0u,
                DdgiVisibilityZeroTransportWithIrradianceCount = giUsesDdgi ? sceneData.DdgiVisibilityZeroTransportWithIrradianceCount : 0u,
                DdgiAverageRelocationFractionEstimate = giUsesDdgi ? sceneData.DdgiAverageRelocationFractionEstimate : 0.0f,
                DdgiClassifiedInactiveProbeCountEstimate = giUsesDdgi ? sceneData.DdgiClassifiedInactiveProbeCountEstimate : 0,
                DdgiSchedulerMode = ddgiRequested ? giSettings.DdgiSchedulerMode : DdgiSchedulerMode.CpuReference,
                DdgiQualityTier = ddgiRequested ? giSettings.DdgiQualityTier : DdgiQualityTier.DdgiHigh,
                DdgiAdaptiveBudgetScale = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetScale : 1.0f,
                DdgiAdaptiveBudgetReduced = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReduced : 0,
                DdgiEmergencyDegradeActive = giUsesDdgi ? sceneData.DdgiEmergencyDegradeActive : 0,
                DdgiEffectiveMaxShadedLights = giUsesDdgi ? sceneData.DdgiEffectiveMaxShadedLights : 0,
                DdgiAdaptiveBudgetReason = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReason : string.Empty,
                DdgiAsyncComputeEnabled = ddgiAsyncComputeActuallyEnabled ? 1 : 0,
                DdgiAtlasMemoryBudgetBytes = ddgiRequested ? giSettings.DdgiAtlasMemoryBudgetBytes : 0,
                DdgiProbeRelocationCount = giUsesDdgi ? sceneData.DdgiProbeRelocationCount : 0,
                DdgiProbeClassificationCount = giUsesDdgi ? sceneData.DdgiProbeClassificationCount : 0,
                DdgiCascadeCount = giUsesDdgi ? sceneData.DdgiCascadeCount : 0,
                DdgiScrollCount = giUsesDdgi ? sceneData.DdgiScrollCount : 0,
                DdgiNewProbeCount = giUsesDdgi ? sceneData.DdgiNewProbeCount : 0,
                DdgiDirtyBoundsProbeUpdateCount = giUsesDdgi ? sceneData.DdgiDirtyBoundsProbeUpdateCount : 0,
                DdgiVisibleFrustumProbeUpdateCount = giUsesDdgi ? sceneData.DdgiVisibleFrustumProbeUpdateCount : 0,
                DdgiOutsideFrustumSafetyProbeUpdateCount = giUsesDdgi ? sceneData.DdgiOutsideFrustumSafetyProbeUpdateCount : 0,
                DdgiAgeRefreshProbeUpdateCount = giUsesDdgi ? sceneData.DdgiAgeRefreshProbeUpdateCount : 0,
                DdgiHighVarianceProbeUpdateCount = giUsesDdgi ? sceneData.DdgiHighVarianceProbeUpdateCount : 0,
                DdgiLowConfidenceProbeUpdateCount = giUsesDdgi ? sceneData.DdgiLowConfidenceProbeUpdateCount : 0,
                DdgiStableProbeUpdateCount = giUsesDdgi ? sceneData.DdgiStableProbeUpdateCount : 0,
                DdgiAverageProbeVariability = giUsesDdgi ? sceneData.DdgiAverageProbeVariability : 0.0f,
                DdgiAverageProbeConfidence = giUsesDdgi ? sceneData.DdgiAverageProbeConfidence : 0.0f,
                DdgiScheduledPrimaryRayCount = giUsesDdgi ? sceneData.DdgiScheduledPrimaryRayCount : 0UL,
                DdgiEstimatedShadowRayUpperBound = giUsesDdgi ? sceneData.DdgiEstimatedShadowRayUpperBound : 0UL,
                DdgiSelectedDirectionalHitCount = giUsesDdgi ? sceneData.DdgiSelectedDirectionalHitCount : 0UL,
                DdgiSelectedLocalHitCount = giUsesDdgi ? sceneData.DdgiSelectedLocalHitCount : 0UL,
                DdgiVisibilityRayCount = giUsesDdgi ? sceneData.DdgiVisibilityRayCount : 0UL,
                DdgiSkippedLocalLightCount = giUsesDdgi ? sceneData.DdgiSkippedLocalLightCount : 0UL,
                DdgiLightSelectionMode = giUsesDdgi ? sceneData.DdgiLightSelectionMode : string.Empty,
                DdgiEmissiveSourceCount = giUsesDdgi ? sceneData.DdgiEmissiveSourceCount : 0,
                DdgiEmissiveSourceRevision = giUsesDdgi ? sceneData.DdgiEmissiveSourceRevision : 0,
                DdgiEmissiveSamplingMode = giUsesDdgi ? sceneData.DdgiEmissiveSamplingMode : string.Empty,
                DdgiEmissiveTriangleCandidateCount = giUsesDdgi ? sceneData.DdgiEmissiveTriangleCandidateCount : 0,
                DdgiEmissiveTriangleBudget = giUsesDdgi ? sceneData.DdgiEmissiveTriangleBudget : 0,
                DdgiEmissiveSkippedEnergyFraction = giUsesDdgi ? sceneData.DdgiEmissiveSkippedEnergyFraction : 0.0f,
                DdgiEmissiveSkippedSkinnedObjectCount = giUsesDdgi ? sceneData.DdgiEmissiveSkippedSkinnedObjectCount : 0,
                DdgiEmissiveSkippedSkinnedImportance = giUsesDdgi ? sceneData.DdgiEmissiveSkippedSkinnedImportance : 0.0,
                DdgiEmissiveSamplingInvocationCount = giUsesDdgi
                    ? _completedMaterialGiCounters.EstimatedEmissiveSamplingInvocationCount
                    : 0u,
                DdgiEmissiveTableCacheHit = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheHit : 0,
                DdgiEmissiveTableCacheHitCount = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheHitCount : 0UL,
                DdgiEmissiveTableCacheMissCount = giUsesDdgi ? sceneData.DdgiEmissiveTableCacheMissCount : 0UL,
                DdgiEmissiveTableRebuildCount = giUsesDdgi ? sceneData.DdgiEmissiveTableRebuildCount : 0UL,
                DdgiEmissiveTableInvalidationCount = giUsesDdgi ? sceneData.DdgiEmissiveTableInvalidationCount : 0UL,
                DdgiEmissiveTableUploadCount = giUsesDdgi ? sceneData.DdgiEmissiveTableUploadCount : 0UL,
                DdgiProbeVolumeBufferBytes = giUsesDdgi ? sceneData.DdgiProbeVolumeBufferBytes : 0UL,
                DdgiProbeStateBufferBytes = giUsesDdgi ? sceneData.DdgiProbeStateBufferBytes : 0UL,
                DdgiProbeUpdateQueueBytes = giUsesDdgi ? sceneData.DdgiProbeUpdateQueueBytes : 0UL,
                DdgiProbeRelocationClassificationBytes = giUsesDdgi ? sceneData.DdgiProbeRelocationClassificationBytes : 0UL,
                DdgiCurrentIrradianceAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentIrradianceAtlasBytes : 0UL,
                DdgiCurrentVisibilityAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentVisibilityAtlasBytes : 0UL,
                DdgiGatherTileBufferBytes = giUsesDdgi ? sceneData.DdgiGatherTileBufferBytes : 0UL,
                DdgiLocalSlotReservedPoolBytes = giUsesDdgi ? sceneData.DdgiLocalSlotReservedPoolBytes : 0UL,
                DdgiGpuSchedulerBufferBytes = giUsesDdgi ? sceneData.DdgiGpuSchedulerBufferBytes : 0UL,
                DdgiGpuSchedulerDirtyRegionCapacity = giUsesDdgi ? sceneData.DdgiGpuSchedulerDirtyRegionCapacity : 0,
                DdgiGpuSchedulerCandidateCapacity = giUsesDdgi ? sceneData.DdgiGpuSchedulerCandidateCapacity : 0,
                DdgiGpuSchedulerGroupCountCapacity = giUsesDdgi ? sceneData.DdgiGpuSchedulerGroupCountCapacity : 0,
                DdgiGpuSchedulerPrefixCapacity = giUsesDdgi ? sceneData.DdgiGpuSchedulerPrefixCapacity : 0,
                DdgiGpuSchedulerDirtyRegionCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerDirtyRegionCount : 0,
                DdgiGpuSchedulerDirtyRegionOverflowCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerDirtyRegionOverflowCount : 0,
                DdgiGpuSchedulerResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerResourceReinitializationCount : 0,
                DdgiGpuSchedulerTotalResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerTotalResourceReinitializationCount : 0,
                DdgiGpuSchedulerUploadBytes = giUsesDdgi ? sceneData.DdgiGpuSchedulerUploadBytes : 0UL,
                DdgiGpuSchedulerReadbackValid = giUsesDdgi ? sceneData.DdgiGpuSchedulerReadbackValid : 0,
                DdgiGpuSchedulerReadbackLatencyFrames = giUsesDdgi ? sceneData.DdgiGpuSchedulerReadbackLatencyFrames : 0,
                DdgiGpuSchedulerFallbackActive = giUsesDdgi ? sceneData.DdgiGpuSchedulerFallbackActive : 0,
                DdgiGpuSchedulerFallbackReason = giUsesDdgi ? sceneData.DdgiGpuSchedulerFallbackReason : string.Empty,
                DdgiGpuSchedulerConsideredProbeCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerConsideredProbeCount : 0,
                DdgiGpuSchedulerRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerRequestCount : 0u,
                DdgiGpuSchedulerPrimaryRayCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPrimaryRayCount : 0u,
                DdgiGpuSchedulerCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerCandidateCount : 0u,
                DdgiGpuSchedulerOverflowCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerOverflowCount : 0u,
                DdgiGpuSchedulerCandidateBufferOverflowCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerCandidateBufferOverflowCount : 0u,
                DdgiGpuSchedulerPerBucketOverflowCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPerBucketOverflowCount : 0u,
                DdgiGpuSchedulerDuplicateRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerDuplicateRequestCount : 0u,
                DdgiGpuSchedulerBudgetRejectedCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerBudgetRejectedCount : 0u,
                DdgiGpuSchedulerRequestBudgetRejectedCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerRequestBudgetRejectedCount : 0u,
                DdgiGpuSchedulerPrimaryRayBudgetRejectedCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPrimaryRayBudgetRejectedCount : 0u,
                DdgiGpuSchedulerInvalidProbeCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerInvalidProbeCount : 0u,
                DdgiGpuSchedulerCandidateOutputCapacity = giUsesDdgi ? sceneData.DdgiGpuSchedulerCandidateOutputCapacity : 0,
                DdgiGpuSchedulerFullScan = giUsesDdgi ? sceneData.DdgiGpuSchedulerFullScan : 0,
                DdgiGpuSchedulerVisibleFrustumCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerVisibleFrustumCandidateCount : 0u,
                DdgiGpuSchedulerSafetyShellCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerSafetyShellCandidateCount : 0u,
                DdgiGpuSchedulerAgeRefreshCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerAgeRefreshCandidateCount : 0u,
                DdgiGpuSchedulerHighVarianceCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerHighVarianceCandidateCount : 0u,
                DdgiGpuSchedulerLowConfidenceCandidateCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerLowConfidenceCandidateCount : 0u,
                DdgiGpuSchedulerStableSkippedCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerStableSkippedCount : 0u,
                DdgiGpuSchedulerPriority0RequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPriority0RequestCount : 0u,
                DdgiGpuSchedulerPriority1RequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPriority1RequestCount : 0u,
                DdgiGpuSchedulerPriority2RequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPriority2RequestCount : 0u,
                DdgiGpuSchedulerPriority3RequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPriority3RequestCount : 0u,
                DdgiGpuSchedulerPriorityBucketMismatchSkipCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerPriorityBucketMismatchSkipCount : 0u,
                DdgiGpuSchedulerRequestBudgetSaturated = giUsesDdgi ? sceneData.DdgiGpuSchedulerRequestBudgetSaturated : 0,
                DdgiGpuSchedulerPrimaryRayBudgetSaturated = giUsesDdgi ? sceneData.DdgiGpuSchedulerPrimaryRayBudgetSaturated : 0,
                DdgiGpuSchedulerValidationValid = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationValid : 0,
                DdgiGpuSchedulerValidationStatus = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationStatus : string.Empty,
                DdgiGpuSchedulerValidationCpuRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationCpuRequestCount : 0,
                DdgiGpuSchedulerValidationGpuRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationGpuRequestCount : 0u,
                DdgiGpuSchedulerValidationComparedRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationComparedRequestCount : 0,
                DdgiGpuSchedulerValidationMismatchCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationMismatchCount : 0,
                DdgiGpuSchedulerValidationSampleLimit = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationSampleLimit : 0,
                DdgiGpuSchedulerValidationFirstMismatch = giUsesDdgi ? sceneData.DdgiGpuSchedulerValidationFirstMismatch : string.Empty,
                DdgiTraceDispatchGroupCount = giUsesDdgi ? sceneData.DdgiTraceDispatchGroupCount : 0u,
                DdgiTraceProbeCount = giUsesDdgi ? sceneData.DdgiTraceProbeCount : 0u,
                DdgiTraceRayCount = giUsesDdgi ? sceneData.DdgiTraceRayCount : 0u,
                DdgiBlendProbeCount = giUsesDdgi ? sceneData.DdgiBlendProbeCount : 0u,
                DdgiRelocateClassifyProbeCount = giUsesDdgi ? sceneData.DdgiRelocateClassifyProbeCount : 0u,
                DdgiPublishProbeCount = giUsesDdgi ? sceneData.DdgiPublishProbeCount : 0u,
                DdgiUpdateExecuted = sceneData.DdgiUpdateExecuted,
                DdgiUpdateSkipReason = sceneData.DdgiUpdateSkipReason,
                DdgiRayScratchBytes = giUsesDdgi ? sceneData.DdgiRayScratchBytes : 0UL,
                DdgiUpdatedAtlasBytes = giUsesDdgi ? sceneData.DdgiUpdatedAtlasBytes : 0UL,
                DdgiPublishExecuted = sceneData.DdgiPublishExecuted,
                DdgiPublishSkipReason = sceneData.DdgiPublishSkipReason,
                DdgiPublishedCacheLatencyFrames = giUsesDdgi ? sceneData.DdgiPublishedCacheLatencyFrames : 0,
                DdgiCacheGeneration = giUsesDdgi ? sceneData.DdgiCacheGeneration : 0u,
                DdgiLastUpdatedFrameSerial = giUsesDdgi ? sceneData.DdgiLastUpdatedFrameSerial : 0UL,
                DdgiCacheWarmupState = giUsesDdgi ? sceneData.DdgiCacheWarmupState : DdgiRuntimeWarmupState.Disabled,
                DdgiStaleProbeCount = giUsesDdgi ? sceneData.DdgiStaleProbeCount : 0,
                DdgiAverageProbeAge = giUsesDdgi ? sceneData.DdgiAverageProbeAge : 0.0f,
                DdgiMaxProbeAge = giUsesDdgi ? sceneData.DdgiMaxProbeAge : 0UL,
                DdgiFrustumUpdatePercentage = giUsesDdgi ? sceneData.DdgiFrustumUpdatePercentage : 0.0f,
                DdgiOutsideFrustumUpdatePercentage = giUsesDdgi ? sceneData.DdgiOutsideFrustumUpdatePercentage : 0.0f,
                DdgiResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiResourceReinitializationCount : 0,
                DdgiTotalResourceReinitializationCount = giUsesDdgi ? sceneData.DdgiTotalResourceReinitializationCount : 0,
                DdgiActiveLocalSlotCount = giUsesDdgi ? sceneData.DdgiActiveLocalSlotCount : 0,
                DdgiLocalSlotGeneration = giUsesDdgi ? sceneData.DdgiLocalSlotGeneration : 0,
                DdgiLocalSlotInitBytes = giUsesDdgi ? sceneData.DdgiLocalSlotInitBytes : 0UL,
                DdgiLocalVolumeEvictionReason = giUsesDdgi ? sceneData.DdgiLocalVolumeEvictionReason : string.Empty,
                DdgiCacheClearReason = giUsesDdgi ? sceneData.DdgiCacheClearReason : string.Empty,
                DdgiCameraMovementClass = giUsesDdgi ? sceneData.DdgiCameraMovementClass : DdgiCameraMovementClass.None,
                CpuSsgiRecordMicroseconds = giUsesSsgi ? sceneData.CpuSsgiRecordMicroseconds : 0,
                CpuDdgiRecordMicroseconds = giUsesDdgi ? sceneData.CpuDdgiRecordMicroseconds : 0,
                CpuSimpleDdgiRecordMicroseconds = giUsesSimpleDdgi ? sceneData.CpuSimpleDdgiRecordMicroseconds : 0,
                CpuFarFieldRecordMicroseconds = giUsesSimpleDdgi ? sceneData.CpuFarFieldRecordMicroseconds : 0,
                CpuGlobalIlluminationRecordMicroseconds = giEnabled ? sceneData.CpuGlobalIlluminationRecordMicroseconds : 0,
                CpuGlobalIlluminationRecordP95Microseconds = giEnabled ? sceneData.CpuGlobalIlluminationRecordP95Microseconds : 0,
                GlobalIlluminationCpuTimingSampleCount = giEnabled ? sceneData.GlobalIlluminationCpuTimingSampleCount : 0,
                CpuDdgiSchedulerMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerMicroseconds : 0,
                CpuDdgiSchedulerP95Microseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerP95Microseconds : 0,
                CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds : 0,
                CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds : 0,
                CpuDdgiSchedulerPhaseUninitializedMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseUninitializedMicroseconds : 0,
                CpuDdgiSchedulerPhaseFrustumMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseFrustumMicroseconds : 0,
                CpuDdgiSchedulerPhaseSafetyMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseSafetyMicroseconds : 0,
                CpuDdgiSchedulerPhaseRoundRobinMicroseconds = giUsesDdgi ? sceneData.CpuDdgiSchedulerPhaseRoundRobinMicroseconds : 0,
                CpuDdgiSchedulerCandidateInsertCount = giUsesDdgi ? sceneData.CpuDdgiSchedulerCandidateInsertCount : 0,
                CpuDdgiSchedulerCandidateMaxShiftCount = giUsesDdgi ? sceneData.CpuDdgiSchedulerCandidateMaxShiftCount : 0,
                DdgiSchedulerTimingSampleCount = giUsesDdgi ? sceneData.DdgiSchedulerTimingSampleCount : 0,
                DdgiSchedulerP95OverBudget = giUsesDdgi ? sceneData.DdgiSchedulerP95OverBudget : 0,
                GpuSsgiTraceMicroseconds = giUsesSsgi ? sceneData.GpuSsgiTraceMicroseconds : 0,
                GpuSsgiTemporalMicroseconds = giUsesSsgi ? sceneData.GpuSsgiTemporalMicroseconds : 0,
                GpuSsgiDenoiseMicroseconds = giUsesSsgi ? sceneData.GpuSsgiDenoiseMicroseconds : 0,
                GpuDdgiScheduleMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleMicroseconds : 0,
                GpuDdgiScheduleP95Microseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleP95Microseconds : 0,
                GpuDdgiScheduleOverBudget = giUsesDdgi ? sceneData.GpuDdgiScheduleOverBudget : 0,
                GpuDdgiScheduleResetMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleResetMicroseconds : 0,
                GpuDdgiScheduleScoreMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleScoreMicroseconds : 0,
                GpuDdgiSchedulePrefixMicroseconds = giUsesDdgi ? sceneData.GpuDdgiSchedulePrefixMicroseconds : 0,
                GpuDdgiScheduleCompactMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleCompactMicroseconds : 0,
                GpuDdgiScheduleFinalizeMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleFinalizeMicroseconds : 0,
                GpuDdgiScheduleReadbackMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleReadbackMicroseconds : 0,
                GpuDdgiScheduleBarrierMicroseconds = giUsesDdgi ? sceneData.GpuDdgiScheduleBarrierMicroseconds : 0,
                GpuDdgiTraceMicroseconds = giUsesDdgi ? sceneData.GpuDdgiTraceMicroseconds : 0,
                GpuDdgiBlendMicroseconds = giUsesDdgi ? sceneData.GpuDdgiBlendMicroseconds : 0,
                GpuDdgiRelocateClassifyMicroseconds = giUsesDdgi ? sceneData.GpuDdgiRelocateClassifyMicroseconds : 0,
                GpuDdgiPublishMicroseconds = giUsesDdgi ? sceneData.GpuDdgiPublishMicroseconds : 0,
                GpuDdgiUpdateMicroseconds = giUsesDdgi ? sceneData.GpuDdgiUpdateMicroseconds : 0,
                GpuGiCompositeMicroseconds = giEnabled ? sceneData.GpuGiCompositeMicroseconds : 0,
                GlobalIlluminationRenderTargetBytes = globalIlluminationRenderTargetBytes,
                SsgiRenderTargetBytes = ssgiRenderTargetBytes,
                SceneSurfaceRenderTargetBytes = sceneSurfaceRenderTargetBytes,
                DdgiTextureBytes = giUsesDdgi ? sceneData.DdgiTextureBytes : 0,
                DdgiBufferBytes = giUsesDdgi ? sceneData.DdgiBufferBytes : 0,
                AccelerationStructureBytes = sceneData.AccelerationStructureBytes,
                AccelerationStructureScratchBytes = sceneData.AccelerationStructureScratchBytes,
                AccelerationStructureInstanceBufferBytes = sceneData.AccelerationStructureInstanceBufferBytes,
                AccelerationStructureRayQueryMetadataBytes = sceneData.AccelerationStructureRayQueryMetadataBytes,
                AccelerationStructureBottomLevelCount = sceneData.AccelerationStructureBottomLevelCount,
                AccelerationStructureTopLevelInstanceCount = sceneData.AccelerationStructureTopLevelInstanceCount,
                AccelerationStructureBlasBuildCount = sceneData.AccelerationStructureBlasBuildCount,
                AccelerationStructureTlasBuildCount = sceneData.AccelerationStructureTlasBuildCount,
                AccelerationStructureTlasUpdateCount = sceneData.AccelerationStructureTlasUpdateCount,
                AccelerationStructureTlasSkipCount = sceneData.AccelerationStructureTlasSkipCount,
                AccelerationStructureStreamingEnabled = giUsesDdgi
                    ? sceneData.AccelerationStructureStreamingEnabled
                    : 0,
                AccelerationStructureStaticInstanceCandidateCount = sceneData.AccelerationStructureStaticInstanceCandidateCount,
                AccelerationStructureStaticInstanceResidentCount = sceneData.AccelerationStructureStaticInstanceResidentCount,
                AccelerationStructureStaticInstanceCulledCount = sceneData.AccelerationStructureStaticInstanceCulledCount,
                AccelerationStructureBlasEvictionCount = sceneData.AccelerationStructureBlasEvictionCount,
                AccelerationStructureBlasEvictionBytes = sceneData.AccelerationStructureBlasEvictionBytes,
                AccelerationStructureBlasBudgetRejectedCount = sceneData.AccelerationStructureBlasBudgetRejectedCount,
                AccelerationStructureBlasBytes = sceneData.AccelerationStructureBlasBytes,
                AccelerationStructureTlasBytes = sceneData.AccelerationStructureTlasBytes,
                AccelerationStructureRetiredBytes = sceneData.AccelerationStructureRetiredBytes,
                AccelerationStructureResidentBytes = sceneData.AccelerationStructureResidentBytes,
                AccelerationStructureMemoryBudgetBytes = ddgiRequested && giSettings.StreamedGiAccelerationStructuresEnabled
                    ? giSettings.GiAccelerationStructureMemoryBudgetBytes
                    : 0UL,
                AccelerationStructureInstanceUploadBytes = sceneData.AccelerationStructureInstanceUploadBytes,
                AccelerationStructureRayQueryMetadataUploadBytes = sceneData.AccelerationStructureRayQueryMetadataUploadBytes,
                CpuAccelerationStructureBuildMicroseconds = sceneData.CpuAccelerationStructureBuildMicroseconds,
                CpuAccelerationStructureBlasBuildMicroseconds = sceneData.CpuAccelerationStructureBlasBuildMicroseconds,
                CpuAccelerationStructureTlasBuildMicroseconds = sceneData.CpuAccelerationStructureTlasBuildMicroseconds,
                CpuAccelerationStructureInstanceUploadMicroseconds = sceneData.CpuAccelerationStructureInstanceUploadMicroseconds,
                GpuAccelerationStructureBlasMicroseconds = sceneData.GpuAccelerationStructureBlasMicroseconds,
                GpuAccelerationStructureTlasMicroseconds = sceneData.GpuAccelerationStructureTlasMicroseconds,
                AccelerationStructureFallbackReason = giUsesDdgi
                    ? sceneData.AccelerationStructureFallbackReason
                    : ddgiRequested && giSettings.EmergencyGiFallbackEnabled
                        ? "Emergency GI fallback is active."
                        : sceneData.AccelerationStructureFallbackReason,
                GeometryDecalsEnabled = sceneData.GeometryDecalsEnabled ? 1 : 0,
                GeometryDecalDepthBias = sceneData.GeometryDecalDepthBias,
                GeometryDecalSlopeScaledDepthBias = sceneData.GeometryDecalSlopeScaledDepthBias,
                SolidDepthMeshletDrawUploadBytes = sceneData.SolidDepthMeshletDrawUploadBytes,
                MaskedDepthMeshletDrawUploadBytes = sceneData.MaskedDepthMeshletDrawUploadBytes,
                MaterialExtensionUploadBytes = sceneData.MaterialExtensionUploadBytes,
                MaterialExtensionDataCount = sceneData.MaterialExtensionData.Count,
                MaterialDebugView = Settings.Materials.DebugView,
                MaterialCompileCount = materialDiagnostics.MaterialCompileCount,
                MaterialLastCompileMicroseconds = materialDiagnostics.LastCompileMicroseconds,
                MaterialTotalCompileMicroseconds = materialDiagnostics.TotalCompileMicroseconds,
                MaterialCompileP95Microseconds = materialDiagnostics.CompileP95Microseconds,
                MaterialCompileTimingSampleCount = materialDiagnostics.CompileTimingSampleCount,
                MaterialUploadP95Microseconds = materialDiagnostics.UploadP95Microseconds,
                MaterialUploadTimingSampleCount = materialDiagnostics.UploadTimingSampleCount,
                MaterialLegacyV1FallbackCount = materialDiagnostics.LegacyV1FallbackCount,
                MaterialInvalidStatisticsCompileCount = materialDiagnostics.InvalidStatisticsCompileCount,
                MaterialActiveLegacyV1FallbackCount =
                    materialDiagnostics.ActiveLegacyV1FallbackCount,
                MaterialActiveInvalidProfileCount =
                    materialDiagnostics.ActiveInvalidProfileCount,
                MaterialActivePrimitiveProfileCount =
                    materialDiagnostics.ActivePrimitiveProfileCount,
                MaterialPrimitiveProfileGpuBytes =
                    materialDiagnostics.PrimitiveProfileGpuBytes,
                MaterialPrimitiveProfileAbsoluteBudgetBytes =
                    materialDiagnostics.PrimitiveProfileGpuBudgetBytes,
                MaterialRevision = materialDiagnostics.MaterialRevision,
                MaterialTextureContentRevision =
                    materialDiagnostics.TextureContentRevision,
                MaterialMaximumTransportProfileRevision =
                    materialDiagnostics.MaximumTransportProfileRevision,
                MaterialGiV2ActiveFeatures = materialGiRollout.ActiveFeatures,
                MaterialGiRolloutMode = materialGiRollout.Mode,
                MaterialGiReleaseQualificationRequired =
                    materialGiRollout.ReleaseQualificationRequired ? 1 : 0,
                MaterialGiReleaseQualified = materialGiRollout.ReleaseQualified ? 1 : 0,
                MaterialGiReleaseQualificationFailureCount =
                    materialGiRollout.QualificationFailureCount,
                MaterialGiReleaseQualificationSummary =
                    materialGiRollout.QualificationSummary,
                MaterialGiReleaseApprovalId = materialGiRollout.ApprovalId,
                MaterialGiReleaseEvidenceSha256 = materialGiRollout.EvidenceSha256,
                MaterialGiQualifiedDeviceCount = materialGiRollout.QualifiedDeviceCount,
                MaterialGiV1RemovalOwner = materialGiRollout.V1RemovalOwner,
                MaterialGiV1RemovalTargetDate =
                    materialGiRollout.V1RemovalTargetDate.ToString("yyyy-MM-dd"),
                MaterialTrackedTextureDependencyCount = materialDiagnostics.TrackedTextureDependencyCount,
                MaterialEstimatedAlphaCandidateTestCount = _completedMaterialGiCounters.EstimatedAlphaCandidateTestCount,
                MaterialEstimatedAlphaCandidateRejectCount = _completedMaterialGiCounters.EstimatedAlphaCandidateRejectCount,
                MaterialNonFiniteValueCount = _completedMaterialGiCounters.NonFiniteMaterialOrRadianceCount,
                MaterialClampedValueCount = _completedMaterialGiCounters.ClampedMaterialOrRadianceCount,
                MaterialAlphaCandidateLimitReachedCount = _completedMaterialGiCounters.AlphaCandidateLimitReachedCount,
                MaterialEstimatedDetailedTransportHitCount =
                    _completedMaterialGiCounters.EstimatedDetailedTransportHitCount,
                MaterialEstimatedCompactTransportHitCount =
                    _completedMaterialGiCounters.EstimatedCompactTransportHitCount,
                MaterialEstimatedCorrectnessFallbackHitCount =
                    _completedMaterialGiCounters.EstimatedCorrectnessFallbackHitCount,
                MaterialEstimatedFarFieldTransportHitCount =
                    _completedMaterialGiCounters.EstimatedFarFieldTransportHitCount,
                FarFieldMaterialConflictCount = _completedFarFieldMaterialV2Counters.ConflictCount,
                FarFieldStalePublicationRejectCount = (uint)Math.Min(
                    (ulong)_completedFarFieldMaterialV2Counters.StalePublicationRejectCount +
                    (ulong)Math.Max(_farFieldClipmapManager?.StalePublicationRejectCount ?? 0, 0),
                    uint.MaxValue),
                AutoExposureEnabled = sceneData.AutoExposureEnabled ? 1 : 0,
                AutoExposureAverageLuminance = sceneData.AutoExposureAverageLuminance,
                AutoExposureTargetExposure = sceneData.AutoExposureTargetExposure,
                AutoExposureSampleCount = sceneData.AutoExposureSampleCount,
                CpuAutoExposureRecordMicroseconds = sceneData.CpuAutoExposureRecordMicroseconds,
                GpuAutoExposureMicroseconds = sceneData.GpuAutoExposureMicroseconds,
                AnimationEnabled = Settings.Animation.Enabled ? 1 : 0,
                AnimationSkinningMode = Settings.Animation.Enabled ? Settings.Animation.SkinningMode : AnimationSkinningMode.Disabled,
                AnimationDebugView = Settings.Animation.DebugView,
                AnimatedModelCount = sceneData.AnimatedModelCount,
                SkinnedObjectCount = sceneData.SkinnedObjectCount,
                SkeletonCount = sceneData.SkeletonCount,
                SkinCount = sceneData.SkinCount,
                AnimationClipCount = sceneData.AnimationClipCount,
                ActiveAnimatorCount = sceneData.ActiveAnimatorCount,
                PlayingAnimatorCount = sceneData.PlayingAnimatorCount,
                PausedAnimatorCount = sceneData.PausedAnimatorCount,
                SkinnedVertexCount = sceneData.SkinnedVertexCount,
                SkinningDispatchCount = sceneData.SkinningDispatchCount,
                JointMatrixCount = sceneData.JointMatrixCount,
                MaxJointsPerSkeleton = Settings.Animation.MaxJointsPerSkeleton,
                CpuAnimationSampleMicroseconds = sceneData.CpuAnimationSampleMicroseconds,
                CpuSkinMatrixUploadMicroseconds = sceneData.CpuSkinMatrixUploadMicroseconds,
                CpuSkinningRecordMicroseconds = sceneData.CpuSkinningRecordMicroseconds,
                GpuSkinningMicroseconds = sceneData.GpuSkinningMicroseconds,
                SkinningUploadBytes = sceneData.SkinningUploadBytes,
                SkinMatrixBufferSize = sceneData.SkinMatrixBufferSize,
                SkinnedVertexBufferSize = sceneData.SkinnedVertexBufferSize,
                AnimatedBoundsMode = sceneData.AnimatedBoundsMode,
                ParticlesEnabled = sceneData.ParticlesEnabled ? 1 : 0,
                ParticleSimulationMode = sceneData.ParticleSimulationMode,
                ParticleDebugView = sceneData.ParticleDebugView,
                ParticleEffectCount = sceneData.ParticleEffectCount,
                ParticleEmitterCount = sceneData.ParticleEmitterCount,
                LiveParticleCount = sceneData.LiveParticleCount,
                SimulatedParticleCount = sceneData.SimulatedParticleCount,
                CulledParticleCount = sceneData.CulledParticleCount,
                RenderedParticleCount = sceneData.RenderedParticleCount,
                ParticleBatchCount = sceneData.ParticleBatchCount,
                AlphaParticleCount = sceneData.AlphaParticleCount,
                AdditiveParticleCount = sceneData.AdditiveParticleCount,
                SoftParticleCount = sceneData.SoftParticleCount,
                FlipbookParticleCount = sceneData.FlipbookParticleCount,
                TrailCount = sceneData.TrailCount,
                TrailSegmentCount = sceneData.TrailSegmentCount,
                BeamCount = sceneData.BeamCount,
                ParticleBudgetExceeded = sceneData.ParticleBudgetExceeded,
                ParticleUploadBudgetExceeded = sceneData.ParticleUploadBudgetExceeded,
                ParticleInstanceUploadBytes = sceneData.ParticleInstanceUploadBytes,
                TrailBeamUploadBytes = sceneData.TrailBeamUploadBytes,
                CpuParticleSimulationMicroseconds = sceneData.CpuParticleSimulationMicroseconds,
                CpuParticleBuildMicroseconds = sceneData.CpuParticleBuildMicroseconds,
                CpuParticleRecordMicroseconds = sceneData.CpuParticleRecordMicroseconds,
                CpuGpuParticleResetRecordMicroseconds = sceneData.CpuGpuParticleResetRecordMicroseconds,
                CpuGpuParticleEmitterUploadMicroseconds = sceneData.CpuGpuParticleEmitterUploadMicroseconds,
                CpuGpuParticleSimulateRecordMicroseconds = sceneData.CpuGpuParticleSimulateRecordMicroseconds,
                CpuTrailBeamRecordMicroseconds = sceneData.CpuTrailBeamRecordMicroseconds,
                GpuParticleMicroseconds = sceneData.GpuParticleMicroseconds,
                GpuTrailBeamMicroseconds = sceneData.GpuTrailBeamMicroseconds,
                ParticleDrawCallCount = sceneData.ParticleDrawCallCount,
                ParticleInstanceBufferSize = sceneData.ParticleInstanceBufferSize,
                ParticleBatchBufferSize = sceneData.ParticleBatchBufferSize,
                ParticleFrameDataBufferSize = sceneData.ParticleFrameDataBufferSize,
                GpuParticlesEnabled = sceneData.GpuParticlesEnabled,
                GpuParticleCapacity = sceneData.GpuParticleCapacity,
                GpuParticleEmitterCapacity = sceneData.GpuParticleEmitterCapacity,
                GpuParticleDrawCapacity = sceneData.GpuParticleDrawCapacity,
                GpuParticleResetRequired = sceneData.GpuParticleResetRequired,
                GpuParticleEmitterCount = sceneData.GpuParticleEmitterCount,
                GpuParticleMaxSpawnPerEmitter = sceneData.GpuParticleMaxSpawnPerEmitter,
                GpuParticleDeltaSeconds = sceneData.GpuParticleDeltaSeconds,
                GpuParticleEmitterUploadBytes = sceneData.GpuParticleEmitterUploadBytes,
                GpuParticleCountersReadbackValid = sceneData.GpuParticleCountersReadbackValid,
                GpuParticleAliveCount = sceneData.GpuParticleAliveCount,
                GpuParticleDeadCount = sceneData.GpuParticleDeadCount,
                GpuParticleSpawnedCount = sceneData.GpuParticleSpawnedCount,
                GpuParticleKilledCount = sceneData.GpuParticleKilledCount,
                GpuParticleCulledCount = sceneData.GpuParticleCulledCount,
                GpuParticleRenderedCount = sceneData.GpuParticleRenderedCount,
                GpuParticleDroppedSpawnCount = sceneData.GpuParticleDroppedSpawnCount,
                GpuParticleBlendBucket0Count = sceneData.GpuParticleBlendBucket0Count,
                GpuParticleBlendBucket1Count = sceneData.GpuParticleBlendBucket1Count,
                GpuParticleBlendBucket2Count = sceneData.GpuParticleBlendBucket2Count,
                GpuParticleBlendBucket3Count = sceneData.GpuParticleBlendBucket3Count,
                GpuParticleBlendBucket4Count = sceneData.GpuParticleBlendBucket4Count,
                ParticleDdgiSampleCount = sceneData.ParticleDdgiSampleCount,
                VfxDdgiDirtyProbeEventCount = sceneData.VfxDdgiDirtyProbeEventCount,
                FoliagePatchCount = sceneData.FoliagePatchCount,
                FoliagePrototypeCount = sceneData.FoliagePrototypeCount,
                FoliageClusterCount = sceneData.FoliageClusterCount,
                FoliageVisibleClusterCount = sceneData.FoliageVisibleClusterCount,
                FoliageCulledClusterCount = sceneData.FoliageCulledClusterCount,
                FoliageVisibleMeshletDrawCount = sceneData.FoliageVisibleMeshletDrawCount,
                FoliageDdgiSampleCount = sceneData.FoliageDdgiSampleCount,
                FoliageDdgiTransportExcludedClusterCount = giUsesDdgi
                    ? sceneData.FoliageClusterCount
                    : 0,
                FoliageDdgiTransportExclusionReason = giUsesDdgi &&
                    sceneData.FoliageClusterCount > 0
                        ? AccelerationStructureManager.FoliageDdgiExclusionReason
                        : string.Empty,
                FoliageGrassBladeEstimate = sceneData.FoliageGrassBladeEstimate,
                FoliageLod0VisibleCount = sceneData.FoliageLod0VisibleCount,
                FoliageLod1VisibleCount = sceneData.FoliageLod1VisibleCount,
                FoliageLod2VisibleCount = sceneData.FoliageLod2VisibleCount,
                FoliageHiZTestedCount = sceneData.FoliageHiZTestedCount,
                FoliageHiZRejectedCount = sceneData.FoliageHiZRejectedCount,
                FoliageOverflowCount = sceneData.FoliageOverflowCount,
                FoliageMeshletDrawOverflowCount = sceneData.FoliageMeshletDrawOverflowCount,
                FoliageFarImpostorVisibleCount = sceneData.FoliageFarImpostorVisibleCount,
                FoliageIndirectMeshletDispatchEnabled = sceneData.FoliageIndirectMeshletDispatchEnabled,
                FoliageInstanceBufferBytes = sceneData.FoliageInstanceBufferBytes,
                FoliageClusterBufferBytes = sceneData.FoliageClusterBufferBytes,
                FoliageDrawBufferBytes = sceneData.FoliageDrawBufferBytes,
                FoliageImpostorAtlasBytes = sceneData.FoliageImpostorAtlasBytes,
                CpuFoliageBuildMicroseconds = sceneData.CpuFoliageBuildMicroseconds,
                CpuFoliageUploadMicroseconds = sceneData.CpuFoliageUploadMicroseconds,
                GpuFoliageCullMicroseconds = sceneData.GpuFoliageCullMicroseconds,
                GpuFoliageDepthMicroseconds = sceneData.GpuFoliageDepthMicroseconds,
                GpuFoliageForwardMicroseconds = sceneData.GpuFoliageForwardMicroseconds,
                GpuFoliageShadowMicroseconds = sceneData.GpuFoliageShadowMicroseconds,
                GpuParticleStateBufferSize = sceneData.GpuParticleStateBufferSize,
                GpuParticleAliveIndexBufferSize = sceneData.GpuParticleAliveIndexBufferSize,
                GpuParticleDeadIndexBufferSize = sceneData.GpuParticleDeadIndexBufferSize,
                GpuParticleEmitterBufferSize = sceneData.GpuParticleEmitterBufferSize,
                GpuParticleCurveSampleBufferSize = sceneData.GpuParticleCurveSampleBufferSize,
                GpuParticleCounterBufferSize = sceneData.GpuParticleCounterBufferSize,
                GpuParticleUnsortedRenderInstanceBufferSize = sceneData.GpuParticleUnsortedRenderInstanceBufferSize,
                GpuParticleRenderInstanceBufferSize = sceneData.GpuParticleRenderInstanceBufferSize,
                GpuParticleIndirectDrawBufferSize = sceneData.GpuParticleIndirectDrawBufferSize,
                GpuParticleSortKeyBufferSize = sceneData.GpuParticleSortKeyBufferSize,
                DebugToolingEnabled = sceneData.DebugToolingEnabled ? 1 : 0,
                DebugOverlayEnabled = sceneData.DebugToolingEnabled && sceneData.DebugOverlayMode != DebugOverlayMode.None ? 1 : 0,
                DebugOverlayMode = sceneData.DebugOverlayMode,
                CpuDebugSnapshotsEnabled = sceneData.CpuDebugSnapshotsEnabled ? 1 : 0,
                DebugSelectedObjectIndex = sceneData.DebugSelectedObjectIndex,
                DebugSelectedObjectName = sceneData.DebugSelectedObjectName,
                DebugDrawEnabled = _debugDraw.Enabled ? 1 : 0,
                DebugDrawLineCount = sceneData.DebugDrawSnapshot.LineCount,
                DebugDrawPersistentLineCount = sceneData.DebugDrawSnapshot.PersistentLineCount,
                DebugDrawDroppedLineCount = sceneData.DebugDrawSnapshot.DroppedLineCount,
                CpuDebugDrawBuildMicroseconds = sceneData.CpuDebugDrawBuildMicroseconds,
                CpuDebugDrawRecordMicroseconds = sceneData.CpuDebugDrawRecordMicroseconds,
                GpuDebugDrawMicroseconds = sceneData.GpuDebugDrawMicroseconds,
                CpuDebugOverlayRecordMicroseconds = sceneData.CpuDebugOverlayRecordMicroseconds,
                GpuDebugOverlayMicroseconds = sceneData.GpuDebugOverlayMicroseconds,
                DebugObjectBoundsDrawn = sceneData.DebugObjectBoundsDrawn,
                DebugMeshletBoundsDrawn = sceneData.DebugMeshletBoundsDrawn,
                DebugMeshletBoundsDropped = sceneData.DebugMeshletBoundsDropped,
                DebugReflectionProbeVolumesDrawn = sceneData.DebugReflectionProbeVolumesDrawn,
                DebugDdgiProbeVolumesDrawn = sceneData.DebugDdgiProbeVolumesDrawn,
                DebugDecalVolumesDrawn = sceneData.DebugDecalVolumesDrawn,
                GpuTimingSupported = _gpuTimestamps.Supported ? 1 : 0,
                GpuTimingEnabled = Settings.Debug.AllowGpuTiming ? 1 : 0,
                GpuTimingPending = _gpuTimestamps.PendingThisFrame ? 1 : 0,
                GpuTimingFrameLatency = FramesInFlight,
                GpuTimingUnavailableReason = BuildGpuTimingReason(),
                CpuHiZDepthTransitionMicroseconds = sceneData.CpuHiZDepthTransitionMicroseconds,
                CpuHiZPyramidTransitionMicroseconds = sceneData.CpuHiZPyramidTransitionMicroseconds,
                CpuHiZDescriptorBindMicroseconds = sceneData.CpuHiZDescriptorBindMicroseconds,
                CpuHiZPushDispatchMicroseconds = sceneData.CpuHiZPushDispatchMicroseconds,
                CpuHiZFinalBarrierMicroseconds = sceneData.CpuHiZFinalBarrierMicroseconds,
                ForwardMeshletsSubmittedCpu = sceneData.MeshletCountSubmittedCpu,
                ForwardGpuOcclusionRejectedMeshlets = forwardOcclusionRejected,
                ForwardGpuOcclusionCountersReconciled = forwardOcclusionCountersReconciled ? 1 : 0,
                ForwardGpuOcclusionSanity = forwardOcclusionSanity,
                HiZConsumerCount = sceneData.HiZConsumerCount,
                HiZConsumerSummary = sceneData.HiZConsumerSummary,
                HiZBuildSkippedBecauseNoConsumer = sceneData.HiZBuildSkippedBecauseNoConsumer ? 1 : 0,
                HiZCounterSource = sceneData.HiZCounterSource,
                ForwardHiZTestedCount = sceneData.ForwardHiZTestedCount,
                ForwardHiZCulledCount = sceneData.ForwardHiZCulledCount,
                ForwardHiZCullRate = sceneData.ForwardHiZCullRate,
                HiZFallbackPath = sceneData.HiZFallbackPath,
                HiZFallbackReason = sceneData.HiZFallbackReason,
                HiZValidateAgainstLegacyPath = sceneData.HiZValidateAgainstLegacyPath ? 1 : 0,
                PreviousHiZFrameValid = sceneData.PreviousHiZFrameValid ? 1 : 0,
                PreviousHiZSkippedInvalidHistory = sceneData.PreviousHiZSkippedInvalidHistory,
                PreviousHiZSkippedCameraMotion = sceneData.PreviousHiZSkippedCameraMotion,
                PreviousHiZTested = sceneData.PreviousHiZTested,
                PreviousHiZCulled = sceneData.PreviousHiZCulled,
                ForwardVisibilityCompactionEnabled = sceneData.ForwardVisibilityCompactionEnabled ? 1 : 0,
                ForwardVisibilityCompactionActive = sceneData.ForwardVisibilityCompactionActive ? 1 : 0,
                ForwardVisibilityCompactionSkipReason = sceneData.ForwardVisibilityCompactionSkipReason,
                CurrentFrameHiZTested = sceneData.CurrentFrameHiZTested,
                CurrentFrameHiZCulled = sceneData.CurrentFrameHiZCulled,
                HiZPolicyStatus = sceneData.HiZPolicyStatus,
                HiZPolicyReason = sceneData.HiZPolicyReason,
                HiZPolicyWarmupFramesRemaining = sceneData.HiZPolicyWarmupFramesRemaining,
                HiZPolicySceneChanged = sceneData.HiZPolicySceneChanged,
                HiZPolicyCameraCut = sceneData.HiZPolicyCameraCut,
                HiZPolicyPyramidInvalidated = sceneData.HiZPolicyPyramidInvalidated,
                HiZPolicyAdaptiveSuppressed = sceneData.HiZPolicyAdaptiveSuppressed,
                HiZPolicyAdaptiveProbe = sceneData.HiZPolicyAdaptiveProbe,
                HiZPolicyAdaptiveProbeCountdown = sceneData.HiZPolicyAdaptiveProbeCountdown,
                HiZPolicyAdaptiveMeasuredOcclusionTests = sceneData.HiZPolicyAdaptiveMeasuredOcclusionTests,
                HiZPolicyAdaptiveMeasuredOcclusionCulled = sceneData.HiZPolicyAdaptiveMeasuredOcclusionCulled,
                HiZPolicyAdaptiveCullRate = sceneData.HiZPolicyAdaptiveCullRate,
                HiZPolicyCounterSource = sceneData.HiZPolicyCounterSource,
                HiZPolicyAdaptiveEstimatedSavedMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedSavedMicroseconds,
                HiZPolicyAdaptiveEstimatedCostMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedCostMicroseconds,
                HiZPolicyAdaptiveEstimatedNetMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedNetMicroseconds,
                HiZPolicyAdaptiveSmoothedCullRate = sceneData.HiZPolicyAdaptiveSmoothedCullRate,
                HiZPolicyAdaptiveSmoothedSavedToCostRatio = sceneData.HiZPolicyAdaptiveSmoothedSavedToCostRatio,
                HiZPolicyAdaptiveSuppressedFrameCount = sceneData.HiZPolicyAdaptiveSuppressedFrameCount,
                HiZPolicyAdaptiveStatus = sceneData.HiZPolicyAdaptiveStatus,
                GpuMeshletCountersEnabled = gpuMeshletCountersEnabled ? 1 : 0,
                GpuMeshletCountersStatus = gpuMeshletCountersStatus,
                SceneSubmissionActiveMode = sceneSubmissionActiveMode,
                SceneSubmissionForwardPath = sceneData.SceneSubmissionForwardPath,
                SceneSubmissionForwardTaskShader = sceneData.SceneSubmissionForwardTaskShader,
                SceneSubmissionCpuCandidateCount = sceneData.MeshletCandidatesCpu,
                SceneSubmissionGpuEmittedCount = sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount,
                SceneSubmissionIndirectTaskCount = sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
                SceneSubmissionGpuCompactionEnabled = sceneData.SceneSubmissionGpuCompactionEnabled ? 1 : 0,
                SceneSubmissionIndirectMeshletDispatchEnabled = sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ? 1 : 0,
                SceneSubmissionGpuLodSelectionEnabled = sceneData.SceneSubmissionGpuLodSelectionEnabled ? 1 : 0,
                SceneSubmissionGpuShadowCompactionEnabled = sceneData.SceneSubmissionGpuShadowCompactionEnabled ? 1 : 0,
                SceneSubmissionValidationCompareCpuGpuLists = sceneData.SceneSubmissionValidationCompareCpuGpuLists ? 1 : 0,
                SceneSubmissionGpuCompactionActive = sceneData.SceneSubmissionGpuCompactionActive ? 1 : 0,
                SceneSubmissionCompactionSkipReason = sceneData.SceneSubmissionCompactionSkipReason,
                SceneSubmissionIndirectDispatchSkipReason = sceneData.SceneSubmissionIndirectDispatchSkipReason,
                SceneSubmissionFallbackReason = sceneData.SceneSubmissionFallbackReason,
                SceneSubmissionGpuOpaqueCandidateCount = sceneData.SceneSubmissionGpuOpaqueCandidateCount,
                SceneSubmissionGpuOpaqueFrustumRejectedCount = sceneData.SceneSubmissionGpuOpaqueFrustumRejectedCount,
                SceneSubmissionGpuOpaqueOverflowCount = sceneData.SceneSubmissionGpuOpaqueOverflowCount,
                SceneSubmissionGpuCompactedOpaqueCapacity = sceneData.SceneSubmissionGpuCompactedOpaqueCapacity,
                SceneSubmissionGpuCompactedOpaqueMeshletCount = sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount,
                SceneSubmissionGpuIndirectMeshletTaskCount = sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
                SceneSubmissionGpuCompactedShadowMeshletCount = sceneData.SceneSubmissionGpuCompactedShadowMeshletCount,
                SceneSubmissionGpuDepthSolidCandidateCount = sceneData.SceneSubmissionGpuDepthSolidCandidateCount,
                SceneSubmissionGpuDepthMaskedCandidateCount = sceneData.SceneSubmissionGpuDepthMaskedCandidateCount,
                SceneSubmissionGpuCompactedSolidDepthMeshletCount = sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount,
                SceneSubmissionGpuCompactedMaskedDepthMeshletCount = sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount,
                SceneSubmissionGpuCompactedSolidDepthCapacity = sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity,
                SceneSubmissionGpuCompactedMaskedDepthCapacity = sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity,
                SceneSubmissionGpuDepthOverflowCount = sceneData.SceneSubmissionGpuDepthOverflowCount,
                SceneSubmissionGpuDirectionalShadowCandidateCount = sceneData.SceneSubmissionGpuDirectionalShadowCandidateCount,
                SceneSubmissionGpuCompactedDirectionalShadowMeshletCount = sceneData.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount,
                SceneSubmissionGpuDirectionalShadowOverflowCount = sceneData.SceneSubmissionGpuDirectionalShadowOverflowCount,
                SceneSubmissionGpuDirectionalShadowLodFallbackCount = sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount,
                SceneSubmissionGpuDirectionalShadowCascadeSummary = BuildDirectionalShadowCompactionSummary(sceneData),
                DirectionalShadowRuntime = CreateDirectionalShadowRuntimeDiagnostics(sceneData),
                SceneSubmissionLocalShadowGpuCompactionJustified =
                    spotShadowGpuCompactionJustified || pointShadowGpuCompactionJustified ? 1 : 0,
                SceneSubmissionSpotShadowGpuCompactionJustified = spotShadowGpuCompactionJustified ? 1 : 0,
                SceneSubmissionPointShadowGpuCompactionJustified = pointShadowGpuCompactionJustified ? 1 : 0,
                SceneSubmissionLocalShadowCpuRecordMicroseconds =
                    sceneData.CpuSpotShadowRecordMicroseconds + sceneData.CpuPointShadowRecordMicroseconds,
                SceneSubmissionSpotShadowMeshletLightTests = spotShadowMeshletLightTests,
                SceneSubmissionPointShadowMeshletFaceTests = pointShadowMeshletFaceTests,
                SceneSubmissionLocalShadowGpuCompactionStatus = localShadowGpuCompactionStatus,
                SceneSubmissionLocalShadowOverflowSummary = localShadowOverflowSummary,
                SceneSubmissionGpuLod0EmittedCount = sceneData.SceneSubmissionGpuLod0EmittedCount,
                SceneSubmissionGpuLod1EmittedCount = sceneData.SceneSubmissionGpuLod1EmittedCount,
                SceneSubmissionGpuLod2EmittedCount = sceneData.SceneSubmissionGpuLod2EmittedCount,
                SceneSubmissionGpuMissingLodFallbackCount = sceneData.SceneSubmissionGpuMissingLodFallbackCount,
                SceneSubmissionValidationValid = sceneData.SceneSubmissionValidationValid,
                SceneSubmissionValidationStatus = sceneData.SceneSubmissionValidationStatus,
                SceneSubmissionValidationCpuOpaqueCount = sceneData.SceneSubmissionValidationCpuOpaqueCount,
                SceneSubmissionValidationGpuOpaqueCount = sceneData.SceneSubmissionValidationGpuOpaqueCount,
                SceneSubmissionValidationComparedSampleCount = sceneData.SceneSubmissionValidationComparedSampleCount,
                SceneSubmissionValidationMismatchCount = sceneData.SceneSubmissionValidationMismatchCount,
                SceneSubmissionValidationSampleLimit = sceneData.SceneSubmissionValidationSampleLimit,
                SceneSubmissionValidationFirstMismatch = sceneData.SceneSubmissionValidationFirstMismatch,
                SceneSubmissionOpaqueCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize,
                SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize,
                SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize,
                SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize = sceneData.SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize,
                SceneSubmissionCounterBufferSize = sceneData.SceneSubmissionCounterBufferSize,
                SceneSubmissionOpaqueIndirectDispatchBufferSize = sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize,
                GpuCompositeMicroseconds = sceneData.GpuCompositeMicroseconds,
                GpuBloomExtractMicroseconds = sceneData.GpuBloomExtractMicroseconds,
                GpuBloomDownsampleMicroseconds = sceneData.GpuBloomDownsampleMicroseconds,
                GpuBloomUpsampleMicroseconds = sceneData.GpuBloomUpsampleMicroseconds,
                GpuDirectionalShadowMicroseconds = sceneData.GpuDirectionalShadowMicroseconds,
                GpuSpotShadowMicroseconds = sceneData.GpuSpotShadowMicroseconds,
                GpuPointShadowMicroseconds = sceneData.GpuPointShadowMicroseconds,
                DirectionalShadowRecordSkipped = sceneData.DirectionalShadowRecordSkipped ? 1 : 0,
                SpotShadowRecordSkipped = sceneData.SpotShadowRecordSkipped ? 1 : 0,
                PointShadowRecordSkipped = sceneData.PointShadowRecordSkipped ? 1 : 0,
                ScreenshotRequested = _screenshotCaptureService.PendingCount > 0 ? 1 : 0,
                ScreenshotPendingCount = _screenshotCaptureService.PendingCount,
                ScreenshotCompletedCount = _screenshotCaptureService.CompletedCount,
                LastScreenshotPath = _screenshotCaptureService.LastScreenshotPath,
                LastScreenshotError = _screenshotCaptureService.LastScreenshotError,
                RenderDocAvailable = _renderDocCaptureService.IsAvailable ? 1 : 0,
                RenderDocCaptureRequested = _renderDocCaptureService.CaptureRequested ? 1 : 0,
                RenderDocCaptureCompletedCount = _renderDocCaptureService.CompletedCount,
                LastRenderDocCaptureMessage = _renderDocCaptureService.LastMessage,
                DdgiVolumes = giUsesDdgi
                    ? sceneData.DdgiVolumeDiagnostics.ToArray()
                    : Array.Empty<DdgiVolumeDiagnosticsEntry>(),
                DdgiRuntimeSnapshot = ddgiRuntimeSnapshot,
                DdgiDiagnosticWarnings = ddgiDiagnosticWarnings,
                LargestTextureAssets = _textureManager.GetLargestFileTextures(10),
                MeshletQualityEntries = _meshManager.GetMeshletQualityEntries(10)
            };

            long gpuFrameMicroseconds = CalculateGpuFrameMicroseconds(sceneData);
            SimpleDdgiLayoutTelemetry simpleDdgiLayout = simpleDdgiRequested
                ? SimpleDdgiLayoutTelemetryFactory.Create(
                    _simpleDdgiVolumeManager?.LastLayoutReport,
                    giSettings.SimpleDdgiSampledAtlasEnabled,
                    // The graph reserves V2 resources even under the V1
                    // compatibility switch, so admission telemetry must account
                    // for the same fixed allocation as the live manager.
                    transportV2Enabled: true,
                    transportRayCapacity: Math.Max(
                        giSettings.SimpleDdgiNearFullRaysPerProbe,
                        Math.Max(
                            giSettings.SimpleDdgiMidFullRaysPerProbe,
                            giSettings.SimpleDdgiFarFullRaysPerProbe)))
                : SimpleDdgiLayoutTelemetry.Unavailable("Simple DDGI was not requested by the resolved GI settings.");
            SimpleDdgiSchedulingTelemetry simpleDdgiScheduling = giUsesSimpleDdgi && _simpleDdgiVolumeManager != null
                ? _simpleDdgiVolumeManager.GetSchedulingTelemetry()
                : SimpleDdgiSchedulingTelemetry.Unavailable("Simple DDGI is inactive for this capture.");
            SimpleDdgiSchedulerPolicyTelemetry simpleDdgiSchedulerPolicy = giUsesSimpleDdgi && _simpleDdgiVolumeManager != null
                ? SimpleDdgiSchedulerPolicyTelemetryFactory.Create(_simpleDdgiVolumeManager.SchedulerTelemetry)
                : SimpleDdgiSchedulerPolicyTelemetry.Unavailable("Simple DDGI scheduler is inactive for this capture.");
            diagnostics = diagnostics with
            {
                GpuFrameMicroseconds = gpuFrameMicroseconds,
                GpuTimingValid = gpuFrameMicroseconds > 0 ? 1 : 0,
                SimpleDdgiLayout = simpleDdgiLayout,
                SimpleDdgiScheduling = simpleDdgiScheduling,
                SimpleDdgiSchedulerPolicy = simpleDdgiSchedulerPolicy
            };

            RenderBudgetProfile profile = Settings.PerformanceBudgets.Profile;
            UploadBudgetSnapshot uploadSnapshot = BuildUploadBudgetSnapshot(sceneData, profile);
            MemoryBudgetSnapshot memorySnapshot = BuildMemoryBudgetSnapshot(profile);
            RuntimeStallSnapshot stallSnapshot = _stallTracker.CreateSnapshot();
            _lastBudgetSnapshot = _budgetEvaluator.Evaluate(profile, diagnostics, memorySnapshot, uploadSnapshot, stallSnapshot);
            MemoryHeapBudgetSnapshot heapBudget = memorySnapshot.HeapBudget;
            ulong actualGpuMemoryBudgetBytes = heapBudget.PrimaryBudgetBytes;
            ulong actualGpuMemoryUsageBytes = heapBudget.PrimaryUsageBytes;
            ulong sceneObjectHighWaterBytes = checked((ulong)Math.Max(sceneData.ObjectCount, sceneData.ObjectData.Count) * (ulong)Marshal.SizeOf<GPUObjectData>());
            ulong sceneOpaqueHighWaterBytes = checked((ulong)sceneData.MeshletDrawCommands.Count * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
            ulong sceneDepthHighWaterBytes = checked(
                ((ulong)sceneData.SolidDepthMeshletDrawCommands.Count + (ulong)sceneData.MaskedDepthMeshletDrawCommands.Count) *
                (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
            ulong sceneTransparentHighWaterBytes = checked((ulong)sceneData.TransparentMeshletDrawCommands.Count * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());
            ulong sceneShadowHighWaterBytes = checked(
                ((ulong)sceneData.LocalShadowMeshletCount + SumDirectionalShadowMeshlets(sceneData)) *
                (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());

            RendererDiagnostics finalDiagnostics = diagnostics with
            {
                ActiveBudgetProfile = profile.Kind,
                ActiveBudgetProfileName = profile.Name,
                ActiveQualityPreset = Settings.QualityPreset,
                CaptureGpuDeviceName = string.IsNullOrWhiteSpace(captureDevice?.DeviceName)
                    ? "unknown-device"
                    : captureDevice.DeviceName,
                CaptureGpuDriverVersion = string.IsNullOrWhiteSpace(captureDevice?.DriverVersion)
                    ? "unknown-driver"
                    : captureDevice.DriverVersion,
                CaptureRenderWidth = sceneData.ScreenWidth,
                CaptureRenderHeight = sceneData.ScreenHeight,
                CaptureSceneContentRevision = sceneData.SceneContentRevision,
                CaptureRun = new PerformanceCaptureRunMetadata(
                    ResolveCaptureSceneKind(sceneData.CaptureSceneName),
                    ResolveCaptureScenario(sceneData.CaptureScenario),
                    CreatePerformanceCaptureBuildConfiguration(_context.ValidationSettings.Mode.ToString()),
                    ResolvePerformanceCaptureApplicationVersion(),
                    ResolvePerformanceCaptureCommit(),
                    ResolveCaptureShaderBundleHash(_captureShaderBundleHash),
                    RenderSettings.SerializationVersion),
                CaptureCamera = CreatePerformanceCaptureCameraMetadata(sceneData),
                CaptureFrame = new PerformanceCaptureFrameMetadata(
                    sceneData.DdgiFrameSerial,
                    sceneData.CaptureFramesSinceSceneLoad,
                    sceneData.DdgiWarmupState,
                    sceneData.SimpleDdgiFramesSinceLastRecenter,
                    sceneData.SimpleDdgiFramesSinceLastClear),
                ResolvedGiSettings = ResolvedGiSettingsMetadata.Unknown,
                GiMeasurement = new GiMeasurementMetadata(
                    Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                    giSettings.DebugView != GlobalIlluminationDebugView.None
                        ? GiMeasurementMode.DetailedInvestigation
                        : GiMeasurementMode.NormalTelemetry,
                    Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ? 1 : 0,
                    Settings.Diagnostics.DdgiForwardEstimateCountersEnabled
                        ? "Detailed GPU investigation counters enabled; overhead is capture-specific."
                        : "Normal telemetry; detailed GI investigation counters disabled.",
                    Settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                    giSettings.DebugView != GlobalIlluminationDebugView.None,
                    sceneData.DdgiInvestigationCountersReadbackValid != 0),
                ActiveFeatureIsolation = sceneData.ActiveFeatureIsolation,
                SkippedRenderPassCount = sceneData.SkippedRenderPassCount,
                GraphPlannedBarrierCount = sceneData.GraphPlannedBarrierCount,
                GraphExecutedBarrierCount = sceneData.GraphExecutedBarrierCount,
                GraphQueueOwnershipTransitionCount = asyncComputePlan.QueueOwnershipTransitionCount,
                GraphBarrierSummary = sceneData.GraphBarrierSummary,
                Graph = asyncComputePlan.GraphDiagnostics,
                ProductionPipelineName = productionPipeline.Name,
                ProductionPipelineDeclaredPasses = productionPipeline.PassOrder,
                ProductionPipelineDeclaredPassCount = productionPipeline.PassOrder.Count,
                ProductionPipelineActivePasses = activeProductionPipelinePasses,
                ProductionPipelineActivePassCount = activeProductionPipelinePasses.Count,
                SecondaryCommandBufferEnabled = sceneData.SecondaryCommandBufferEnabled,
                SecondaryCommandBufferPassCount = sceneData.SecondaryCommandBufferPassCount,
                AsyncComputeRequested = asyncComputePlan.Requested ? 1 : 0,
                AsyncComputeEnabled = asyncComputePlan.Enabled ? 1 : 0,
                AsyncComputeSupported = asyncComputePlan.Supported ? 1 : 0,
                AsyncComputeIndependentQueueAvailable = _context.HasIndependentComputeQueue ? 1 : 0,
                AsyncComputeDedicatedQueueFamilyAvailable = _context.HasDedicatedComputeQueue ? 1 : 0,
                AsyncComputeGraphicsQueueFamily = _context.GraphicsQueueFamilyIndex,
                AsyncComputeComputeQueueFamily = _context.ComputeQueueFamilyIndex,
                AsyncComputeCandidatePassCount = asyncComputePlan.CandidatePasses.Count,
                AsyncComputeEnabledPassCount = asyncComputePlan.EnabledPasses.Count,
                AsyncComputeQueueOwnershipTransitionCount = asyncComputePlan.QueueOwnershipTransitionCount,
                AsyncComputeOwnershipTransferCount = sceneData.AsyncComputeOwnershipTransferCount,
                AsyncComputeEstimatedOverlapMicroseconds = sceneData.AsyncComputeEstimatedOverlapMicroseconds,
                AsyncComputeQueueBusyMicroseconds = EstimateAsyncComputeQueueBusyMicroseconds(
                    asyncComputePlan.SubmissionPlan,
                    _gpuTimestamps.LastCompletedSnapshot),
                AsyncComputeFirstConsumerWaitEstimateMicroseconds = EstimateAsyncComputeFirstConsumerWaitMicroseconds(
                    asyncComputePlan.SubmissionPlan,
                    _gpuTimestamps.LastCompletedSnapshot),
                AsyncComputeBarrierRecordMicroseconds = _asyncComputeBarrierRecordMicrosecondsThisFrame,
                AsyncComputeStatus = asyncComputePlan.Status,
                AsyncComputeCandidatePasses = asyncComputePlan.CandidatePasses,
                AsyncComputeEnabledPasses = asyncComputePlan.EnabledPasses,
                AsyncComputeRequestedMode = asyncComputePlan.RequestedMode,
                AsyncComputeEffectiveMode = asyncComputePlan.EffectiveMode,
                AsyncComputeGraphicsSegmentCount = asyncComputePlan.SubmissionPlan.GraphicsSegmentCount,
                AsyncComputeComputeSegmentCount = asyncComputePlan.SubmissionPlan.ComputeSegmentCount,
                AsyncComputePlannedGraphicsSegmentCount = asyncComputePlan.SubmissionPlan.GraphicsSegmentCount,
                AsyncComputePlannedComputeSegmentCount = asyncComputePlan.SubmissionPlan.ComputeSegmentCount,
                AsyncComputeSubmittedGraphicsSegmentCount = _asyncComputeSubmittedGraphicsSegmentsThisFrame,
                AsyncComputeSubmittedComputeSegmentCount = _asyncComputeSubmittedComputeSegmentsThisFrame,
                AsyncComputePlannedReleaseBarrierCount = _asyncComputePlannedReleaseBarrierCountThisFrame,
                AsyncComputePlannedAcquireBarrierCount = _asyncComputePlannedAcquireBarrierCountThisFrame,
                AsyncComputeEmittedReleaseBarrierCount = _asyncComputeEmittedReleaseBarrierCountThisFrame,
                AsyncComputeEmittedAcquireBarrierCount = _asyncComputeEmittedAcquireBarrierCountThisFrame,
                AsyncComputeTransferredBytes = _asyncComputeTransferredBytesThisFrame,
                AsyncComputeTransferredImageSubresources = _asyncComputeTransferredImageSubresourcesThisFrame,
                AsyncComputeValidationFallbackCount = _asyncComputeValidationFallbackCount,
                AsyncComputeLastFallbackReason = _asyncComputeLastFallbackReason,
                AsyncComputeResourcePlanGeneration = asyncComputePlan.SubmissionPlan.ResourcePlanGeneration,
                AsyncComputeStalePlanRejectionCount = _renderGraph.ConcreteResourceBindings.StalePlanRejectionCount,
                AsyncComputePaths = asyncComputePlan.SubmissionPlan.Paths
                    .Select(path => new AsyncComputePathDiagnostic(
                        path.Path,
                        path.Requested,
                        path.Supported,
                        path.Eligible,
                        path.Active,
                        path.Status,
                        path.Reason,
                        path.Passes))
                    .ToArray(),
                AsyncComputeSegments = asyncComputePlan.SubmissionPlan.Segments
                    .Select(segment => new AsyncComputeSegmentDiagnostic(
                        segment.Id,
                        segment.Queue.ToString(),
                        segment.Passes,
                        segment.TimelineWaits.Select(wait => wait.Value).ToArray(),
                        segment.TimelineSignalValue,
                        segment.AcquireTransfers.Count,
                        segment.ReleaseTransfers.Count,
                        segment.AccessesSwapchain,
                        segment.IsTerminalGraphicsSegment))
                    .ToArray(),
                CpuPrimaryCommandRecordMicroseconds = sceneData.CpuPrimaryCommandRecordMicroseconds,
                CpuSecondaryCommandRecordMicroseconds = sceneData.CpuSecondaryCommandRecordMicroseconds,
                BudgetOverallStatus = _lastBudgetSnapshot.OverallStatus,
                CpuFrameBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "CPU renderer"),
                GpuFrameBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "GPU frame"),
                GpuMemoryBudgetStatus = FindMetricStatus(_lastBudgetSnapshot, "GPU memory"),
                UploadBudgetStatus = uploadSnapshot.Status,
                GpuMemoryBudgetBytes = profile.GpuMemoryBudgetBytes,
                TrackedGpuMemoryBytes = memorySnapshot.TotalTrackedBytes,
                GpuMemoryBudgetQueryAvailable = heapBudget.IsAvailable ? 1 : 0,
                ActualGpuMemoryUsageBytes = actualGpuMemoryUsageBytes,
                ActualGpuMemoryBudgetBytes = actualGpuMemoryBudgetBytes,
                ActualGpuMemoryAllocationBytes = heapBudget.PrimaryAllocationBytes,
                ActualGpuMemoryBlockBytes = heapBudget.PrimaryBlockBytes,
                ActualGpuMemoryUtilization = actualGpuMemoryBudgetBytes == 0
                    ? 0f
                    : (float)((double)actualGpuMemoryUsageBytes / actualGpuMemoryBudgetBytes),
                GpuMemoryHeapCount = heapBudget.Entries.Count,
                GpuMemoryHeapBudgets = heapBudget.Entries,
                UnknownGpuMemoryBytes = GetMemoryCategoryBytes(memorySnapshot, MemoryBudgetCategory.Unknown),
                MeshBufferAllocatedBytes = _meshManager.MeshBufferAllocatedBytes,
                MeshBufferUsedBytes = _meshManager.MeshBufferUsedBytes,
                MeshBufferUtilization = _meshManager.MeshBufferUtilization,
                MeshBufferCompactionCount = _meshManager.MeshBufferCompactionCount,
                MeshBufferCompactedBytesSaved = _meshManager.MeshBufferCompactedBytesSaved,
                MeshRetainedDeadBytes =
                    _meshManager.RetainedDeadMeshBytes,
                MeshRetainedDeadByteBudget =
                    MeshManager.MaximumRetainedDeadMeshBytes,
                MeshRetainedDeadByteBudgetRejectionCount =
                    _meshManager
                        .RetainedDeadMeshBudgetRejectionCount,
                MeshPostCommitCleanupFailureCount =
                    _meshManager.PostCommitCleanupFailureCount,
                PendingMaterialTextureFanoutCount =
                    materialDiagnostics.PendingTextureFanoutCount,
                MaterialTextureFanoutFailureCount =
                    materialDiagnostics.TextureFanoutFailureCount,
                PendingRetiredMaterialBufferCount =
                    materialDiagnostics.PendingRetiredBufferCount,
                QuarantinedMaterialBufferCount =
                    materialDiagnostics.QuarantinedBufferCount,
                MaterialRetiredBufferCleanupFailureCount =
                    materialDiagnostics
                        .RetiredBufferCleanupFailureCount,
                MaterialBindingRepairPending =
                    materialDiagnostics
                        .MaterialBindingRepairPending
                        ? 1
                        : 0,
                SceneBufferAllocatedBytes = sceneData.ObjectBufferSize +
                    sceneData.InstanceBufferSize +
                    sceneData.MeshletDrawBufferSize +
                    sceneData.FullOpaqueMeshletDrawBufferSize +
                    sceneData.SolidDepthMeshletDrawBufferSize +
                    sceneData.MaskedDepthMeshletDrawBufferSize +
                    sceneData.TransparentMeshletDrawBufferSize +
                    sceneData.DirectionalShadowMeshletDrawBufferSize +
                    sceneData.LocalShadowMeshletDrawBufferSize +
                    sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize +
                    sceneData.SceneSubmissionCounterBufferSize +
                    sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize +
                    sceneData.GpuParticleStateBufferSize +
                    sceneData.GpuParticleAliveIndexBufferSize +
                    sceneData.GpuParticleDeadIndexBufferSize +
                    sceneData.GpuParticleEmitterBufferSize +
                    sceneData.GpuParticleCurveSampleBufferSize +
                    sceneData.GpuParticleCounterBufferSize +
                    sceneData.GpuParticleUnsortedRenderInstanceBufferSize +
                    sceneData.GpuParticleRenderInstanceBufferSize +
                    sceneData.GpuParticleIndirectDrawBufferSize +
                    sceneData.GpuParticleSortKeyBufferSize,
                SceneBufferPeakBytes = sceneObjectHighWaterBytes +
                    sceneOpaqueHighWaterBytes +
                    sceneDepthHighWaterBytes +
                    sceneTransparentHighWaterBytes +
                    sceneShadowHighWaterBytes,
                MaterialBufferAllocatedBytes = _materialManager.MaterialBufferSize + _materialManager.MaterialExtensionBufferSize,
                MaterialBufferUtilization = _materialManager.MaterialBufferUtilization,
                LightBufferAllocatedBytes = _lightManager.LightBufferAllocatedBytes,
                TiledLightBufferAllocatedBytes = sceneData.TiledLightHeaderBufferSize + sceneData.TiledLightIndexBufferSize,
                TiledLightHeaderBufferClearBytes = sceneData.TiledLightHeaderBufferClearBytes,
                TiledLightIndexBufferClearBytes = sceneData.TiledLightIndexBufferClearBytes,
                LightTileSaturationCount = sceneData.LightTileSaturationCount,
                MaxLightsInAnyTile = sceneData.MaxLightsInAnyTile,
                AverageLightsPerNonEmptyTile = sceneData.AverageLightsPerNonEmptyTile,
                LightCullRejectedPointCount = sceneData.LightCullRejectedPointCount,
                LightCullRejectedSpotCount = sceneData.LightCullRejectedSpotCount,
                TextureAssetBytes = _textureManager.FileTextureBytes + _textureManager.DefaultTextureBytes,
                DefaultTextureBytes = _textureManager.DefaultTextureBytes,
                FileTextureBytes = _textureManager.FileTextureBytes,
                TextureCacheEntryCount = _textureManager.TextureCacheEntryCount,
                TextureBindlessUsedCount = _textureManager.TextureBindlessUsedCount,
                TextureBindlessFreeCount = _textureManager.TextureBindlessFreeCount,
                ActiveTextureBudgetProfile = _textureManager.ActiveTextureBudgetProfile,
                RenderTargetBytes = _renderTargets?.TotalEstimatedBytes ?? 0,
                RenderTargetCount = _renderTargets?.RenderTargetCount ?? 0,
                RenderTargetResizeCount = _renderTargets?.ResizeCount ?? 0,
                RequestedDynamicResolutionScale = _dynamicResolutionScaleController.RequestedScale,
                CommittedRenderTargetScale = _dynamicResolutionScaleController.CommittedScale,
                LastRenderTargetRecreateReason = _lastRenderTargetRecreateReason,
                BloomRenderTargetBytes = _renderTargets?.BloomRenderTargetBytes ?? 0,
                AmbientOcclusionRenderTargetBytes = _renderTargets?.AmbientOcclusionRenderTargetBytes ?? 0,
                AntiAliasingRenderTargetBytes = _renderTargets?.AntiAliasingRenderTargetBytes ?? 0,
                WeightedOitRenderTargetBytes = _renderTargets?.WeightedOitRenderTargetBytes ?? 0,
                WeightedOitRenderTargetCount = _renderTargets == null ? 0 : 2,
                DirectionalShadowBytes = _directionalShadowResources?.EstimatedImageBytes ?? 0,
                SpotShadowAtlasBytes = _spotShadowAtlas?.EstimatedImageBytes ?? 0,
                PointShadowBytes = _pointShadowCubemapArray?.EstimatedImageBytes ?? 0,
                PointShadowSkippedFaceCount = sceneData.PointShadowSkippedFaceCount,
                ShadowMapBytes = (_directionalShadowResources?.EstimatedImageBytes ?? 0) +
                    (_spotShadowAtlas?.EstimatedImageBytes ?? 0) +
                    (_pointShadowCubemapArray?.EstimatedImageBytes ?? 0),
                SpotShadowAtlasUtilization = sceneData.SpotShadowAtlasCapacity <= 0
                    ? 0f
                    : (float)sceneData.SpotShadowAtlasUsedTiles / sceneData.SpotShadowAtlasCapacity,
                PointShadowFaceUtilization = Settings.Shadows.MaxShadowedPointLights <= 0
                    ? 0f
                    : (float)sceneData.PointShadowRenderedFaceCount / (Settings.Shadows.MaxShadowedPointLights * 6),
                EnvironmentMapBytes = _environmentManager?.EnvironmentMapBytes ?? 0,
                IrradianceMapBytes = _environmentManager?.IrradianceMapBytes ?? 0,
                PrefilteredEnvironmentBytes = _environmentManager?.PrefilteredEnvironmentBytes ?? 0,
                BrdfLutBytes = _environmentManager?.BrdfLutBytes ?? 0,
                ReflectionProbeBytes = _reflectionProbeManager?.EstimatedBytes ?? 0,
                ReflectionProbeCubemapArrayBytes = _reflectionProbeManager?.CubemapArrayBytes ?? 0,
                ReflectionProbeCaptureBudgetUsed = sceneData.ReflectionProbeCapturesCompleted,
                StagingBufferAllocatedBytes = _stagingRing.TotalAllocatedBytes,
                StagingBytesUsedThisFrame = _stagingRing.CurrentFrameBytesUsed,
                StagingBytesPeakThisSession = _stagingRing.PeakBytesThisSession,
                StagingOverflowCount = _stagingRing.OverflowCount,
                StagingOverflowCountThisFrame = _stagingRing.CurrentFrameOverflowCount,
                StagingRetainedOverflowBufferCount = _stagingRing.RetainedOverflowBufferCount,
                StagingRetainedOverflowBytes = _stagingRing.RetainedOverflowBytes,
                StagingPeakOverflowBytes = _stagingRing.PeakOverflowBytesThisSession,
                StagingLargestOverflowAllocationBytes = _stagingRing.LargestOverflowAllocationBytes,
                UploadBudgetExceeded = uploadSnapshot.BudgetExceededFrameCount,
                UploadBudgetUtilization = profile.UploadBudgetBytesPerFrame == 0 || profile.UploadBudgetBytesPerFrame == ulong.MaxValue
                    ? 0f
                    : (float)((double)uploadSnapshot.TotalBytes / profile.UploadBudgetBytesPerFrame),
                UploadBudgetBytesPerFrame = profile.UploadBudgetBytesPerFrame,
                SwapchainEstimatedBytes = _swapchain.EstimatedBytes,
                SwapchainImageCount = (int)_swapchain.ImageCount,
                SwapchainFormat = _swapchain.SurfaceFormat.ToString(),
                CpuAcquireImageMicroseconds = _lastAcquireImageMicroseconds,
                CpuWaitForFrameFenceMicroseconds = _sync.LastFenceWaitMicroseconds,
                CpuQueueSubmitMicroseconds = _lastQueueSubmitMicroseconds,
                CpuPresentMicroseconds = _lastPresentMicroseconds,
                CpuFenceResetMicroseconds = _sync.LastFenceResetMicroseconds,
                RuntimeStallMicrosecondsThisFrame = stallSnapshot.TotalMicrosecondsThisFrame,
                RuntimeWorstStallMicroseconds = stallSnapshot.WorstMicrosecondsThisFrame,
                RuntimeWorstStallReason = stallSnapshot.WorstReasonThisFrame,
                RuntimeDeviceWaitIdleCount = stallSnapshot.DeviceWaitIdleCount,
                GpuFrameMicroseconds = gpuFrameMicroseconds,
                ValidationMode = _context.ValidationSettings.Mode,
                ValidationVerboseMessageCount = validationMessages.VerboseCount,
                ValidationInfoMessageCount = validationMessages.InformationCount,
                ValidationWarningMessageCount = validationMessages.WarningCount,
                ValidationErrorMessageCount = validationMessages.ErrorCount,
                ValidationFirstWarningMessage = validationMessages.FirstWarningMessage,
                ValidationLastWarningMessage = validationMessages.LastWarningMessage,
                ValidationFirstErrorMessage = validationMessages.FirstErrorMessage,
                ValidationLastErrorMessage = validationMessages.LastErrorMessage,
                SceneObjectBufferHighWaterBytes = sceneObjectHighWaterBytes,
                SceneOpaqueMeshletBufferHighWaterBytes = sceneOpaqueHighWaterBytes,
                SceneDepthMeshletBufferHighWaterBytes = sceneDepthHighWaterBytes,
                SceneTransparentMeshletBufferHighWaterBytes = sceneTransparentHighWaterBytes,
                SceneShadowMeshletBufferHighWaterBytes = sceneShadowHighWaterBytes
            };

            IReadOnlyList<GiFeatureState> giFeatureStates = GiFeatureStateFactory.Create(finalDiagnostics);
            finalDiagnostics = finalDiagnostics with
            {
                GiFeatureStates = giFeatureStates
            };
            finalDiagnostics = finalDiagnostics with
            {
                ResolvedGiSettings = ResolvedGiSettingsMetadataFactory.Create(finalDiagnostics)
            };
            GiWarningEvaluationResult giWarningEvaluation = _giWarningEvaluator.Evaluate(finalDiagnostics);
            IReadOnlyList<GiDiagnosticWarning> giWarnings = GiDiagnosticWarningFactory.Create(
                finalDiagnostics,
                giWarningEvaluation,
                giFeatureStates);
            GiBlackFrameMetrics blackFrameMetrics = giWarningEvaluation.BlackFrame;
            return finalDiagnostics with
            {
                GiWarnings = giWarnings,
                GiBlackFrameMetrics = blackFrameMetrics,
                // Retain legacy fields for existing overlays/capture consumers, but source them
                // from the calibrated stateful evaluator rather than the old one-pixel rule.
                DdgiBlackFrameSuspect = blackFrameMetrics.LargeAreaBlackout ? 1 : 0,
                DdgiBlackFrameAfterRecenter = blackFrameMetrics.LargeAreaBlackout &&
                    finalDiagnostics.SimpleDdgiRecentered != 0 ? 1 : 0,
                DdgiBlackFrameAfterAtlasClear = blackFrameMetrics.LargeAreaBlackout &&
                    finalDiagnostics.SimpleDdgiAtlasCleared != 0 ? 1 : 0,
                DdgiBlackFrameDuringFreshAtlas = blackFrameMetrics.LargeAreaBlackout &&
                    finalDiagnostics.SimpleDdgiAtlasFresh != 0 ? 1 : 0,
                DdgiBlackFrameMovementClass = blackFrameMetrics.LargeAreaBlackout
                    ? finalDiagnostics.DdgiCameraMovementClass
                    : DdgiCameraMovementClass.None
            };
        }

        private void RefreshValidationDiagnostics()
        {
            RendererValidationMessageSnapshot validationMessages = _context.ValidationMessageSnapshot;
            _lastDiagnostics = _lastDiagnostics with
            {
                ValidationVerboseMessageCount = validationMessages.VerboseCount,
                ValidationInfoMessageCount = validationMessages.InformationCount,
                ValidationWarningMessageCount = validationMessages.WarningCount,
                ValidationErrorMessageCount = validationMessages.ErrorCount,
                ValidationFirstWarningMessage = validationMessages.FirstWarningMessage,
                ValidationLastWarningMessage = validationMessages.LastWarningMessage,
                ValidationFirstErrorMessage = validationMessages.FirstErrorMessage,
                ValidationLastErrorMessage = validationMessages.LastErrorMessage
            };
        }

        private static PerformanceCaptureCameraMetadata CreatePerformanceCaptureCameraMetadata(SceneRenderingData sceneData)
        {
            return new PerformanceCaptureCameraMetadata(
                sceneData.CameraPosition.X,
                sceneData.CameraPosition.Y,
                sceneData.CameraPosition.Z,
                sceneData.CaptureCameraYawRadians,
                sceneData.CaptureCameraPitchRadians,
                sceneData.CaptureCameraFieldOfViewRadians,
                sceneData.CaptureCameraNearPlane,
                sceneData.CaptureCameraFarPlane,
                ComputePerformanceCaptureMatrixHash(sceneData.ViewMatrix),
                ComputePerformanceCaptureMatrixHash(sceneData.ProjectionMatrix),
                sceneData.CaptureCameraCutSerial);
        }

        private static string ComputePerformanceCaptureMatrixHash(Matrix4x4 matrix)
        {
            string canonical = string.Join("|", new[]
            {
                matrix.M11.ToString("R", CultureInfo.InvariantCulture), matrix.M12.ToString("R", CultureInfo.InvariantCulture), matrix.M13.ToString("R", CultureInfo.InvariantCulture), matrix.M14.ToString("R", CultureInfo.InvariantCulture),
                matrix.M21.ToString("R", CultureInfo.InvariantCulture), matrix.M22.ToString("R", CultureInfo.InvariantCulture), matrix.M23.ToString("R", CultureInfo.InvariantCulture), matrix.M24.ToString("R", CultureInfo.InvariantCulture),
                matrix.M31.ToString("R", CultureInfo.InvariantCulture), matrix.M32.ToString("R", CultureInfo.InvariantCulture), matrix.M33.ToString("R", CultureInfo.InvariantCulture), matrix.M34.ToString("R", CultureInfo.InvariantCulture),
                matrix.M41.ToString("R", CultureInfo.InvariantCulture), matrix.M42.ToString("R", CultureInfo.InvariantCulture), matrix.M43.ToString("R", CultureInfo.InvariantCulture), matrix.M44.ToString("R", CultureInfo.InvariantCulture)
            });
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal static string CreatePerformanceCaptureBuildConfiguration(string? validationMode)
        {
            string validation = NormalizeCaptureMetadataValue(
                validationMode,
                "unavailable:validation-mode-not-reported");
            string? framework = typeof(VulkanRenderer).Assembly
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName;
            framework = NormalizeCaptureMetadataValue(
                framework,
                "unavailable:target-framework-not-embedded");
            return ResolveBuildConfiguration() + "; validation=" + validation + "; framework=" + framework;
        }

        internal static string ResolvePerformanceCaptureApplicationVersion()
        {
            Assembly assembly = typeof(VulkanRenderer).Assembly;
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return NormalizeCaptureMetadataValue(
                informationalVersion ?? assembly.GetName().Version?.ToString(),
                "unavailable:application-version-not-embedded");
        }

        internal static string ResolvePerformanceCaptureCommit()
        {
            Assembly assembly = typeof(VulkanRenderer).Assembly;
            string? sourceRevision = null;
            foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, "SourceRevisionId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attribute.Key, "GitCommitId", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attribute.Key, "Commit", StringComparison.OrdinalIgnoreCase))
                {
                    sourceRevision = attribute.Value;
                    break;
                }
            }

            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return ResolvePerformanceCaptureCommit(sourceRevision, informationalVersion);
        }

        internal static string ResolvePerformanceCaptureCommit(string? sourceRevision, string? informationalVersion)
        {
            string? revision = NormalizeSourceRevision(sourceRevision);
            if (revision != null)
                return revision;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataIndex = informationalVersion.IndexOf('+');
                if (metadataIndex >= 0 && metadataIndex < informationalVersion.Length - 1)
                    revision = NormalizeSourceRevision(informationalVersion[(metadataIndex + 1)..]);
            }

            return revision ?? "unavailable:source-revision-not-embedded";
        }

        internal static string ResolvePerformanceCaptureShaderBundleHash()
        {
            try
            {
                Assembly assembly = typeof(ShaderLibrary).Assembly;
                string[] resourceNames = assembly.GetManifestResourceNames();
                Array.Sort(resourceNames, StringComparer.Ordinal);

                const string resourcePrefix = "Njulf.Shaders.";
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendCaptureHashText(hash, "njulf-effective-shader-bundle-v1");

                int shaderResourceCount = 0;
                foreach (string resourceName in resourceNames)
                {
                    if (!resourceName.StartsWith(resourcePrefix, StringComparison.Ordinal))
                        continue;

                    string shaderFileName = resourceName[resourcePrefix.Length..];
                    using Stream? stream = OpenEffectiveCaptureShaderStream(assembly, resourceName, shaderFileName);
                    if (stream == null)
                        return "unavailable:shader-resource-missing";

                    AppendCaptureHashText(hash, shaderFileName);
                    AppendCaptureHashStream(hash, stream);
                    shaderResourceCount++;
                }

                if (shaderResourceCount == 0)
                    return "unavailable:shader-resources-not-embedded";

                return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            catch (IOException)
            {
                return "unavailable:shader-bundle-hash-failed";
            }
            catch (UnauthorizedAccessException)
            {
                return "unavailable:shader-bundle-hash-failed";
            }
            catch (NotSupportedException)
            {
                // Capture export must remain available even in a stripped or damaged deployment.
                // The reason makes the missing identity explicit instead of silently emitting a
                // generic placeholder.
                return "unavailable:shader-bundle-hash-failed";
            }
            catch (CryptographicException)
            {
                return "unavailable:shader-bundle-hash-failed";
            }
        }

        internal static string ResolveCaptureScenario(string? scenario)
        {
            return NormalizeCaptureMetadataValue(
                scenario,
                "unavailable:active-scenario-not-supplied-by-renderer-client");
        }

        private static string ResolveCaptureSceneKind(string? sceneName)
        {
            return NormalizeCaptureMetadataValue(sceneName, "unavailable:scene-name-not-reported");
        }

        private static string ResolveCaptureShaderBundleHash(string? shaderBundleHash)
        {
            return NormalizeCaptureMetadataValue(
                shaderBundleHash,
                "unavailable:shader-bundle-hash-not-reported");
        }

        private static string NormalizeCaptureMetadataValue(string? value, string unavailableValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return unavailableValue;

            string normalized = value.Trim();
            return normalized.StartsWith("unknown", StringComparison.OrdinalIgnoreCase)
                ? unavailableValue
                : normalized;
        }

        private static string? NormalizeSourceRevision(string? sourceRevision)
        {
            if (string.IsNullOrWhiteSpace(sourceRevision))
                return null;

            string revision = sourceRevision.Trim();
            if (revision.StartsWith("sha", StringComparison.OrdinalIgnoreCase))
            {
                int separatorIndex = revision.IndexOfAny([':', '-', '=']);
                if (separatorIndex >= 0 && separatorIndex < revision.Length - 1)
                    revision = revision[(separatorIndex + 1)..].Trim();
            }

            if (revision.Length is < 7 or > 128)
                return null;

            for (int i = 0; i < revision.Length; i++)
            {
                if (!Uri.IsHexDigit(revision[i]))
                    return null;
            }

            return revision.ToLowerInvariant();
        }

        private static Stream? OpenEffectiveCaptureShaderStream(
            Assembly shaderAssembly,
            string resourceName,
            string shaderFileName)
        {
            foreach (string candidate in GetCaptureShaderFileCandidates(shaderFileName))
            {
                if (File.Exists(candidate))
                    return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            return shaderAssembly.GetManifestResourceStream(resourceName);
        }

        private static IEnumerable<string> GetCaptureShaderFileCandidates(string shaderFileName)
        {
            string baseDirectory = AppContext.BaseDirectory;
            yield return Path.Combine(baseDirectory, "Shaders", shaderFileName);
            yield return Path.Combine(baseDirectory, shaderFileName);

            DirectoryInfo? directory = new(baseDirectory);
            while (directory != null)
            {
                yield return Path.Combine(directory.FullName, "Njulf.Shaders", "bin", "Debug", "net10.0", "Shaders", shaderFileName);
                yield return Path.Combine(directory.FullName, "Njulf.Shaders", "bin", "Release", "net10.0", "Shaders", shaderFileName);
                directory = directory.Parent;
            }
        }

        private static void AppendCaptureHashText(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        private static void AppendCaptureHashStream(IncrementalHash hash, Stream stream)
        {
            // Fold a fixed-size digest for each shader into the bundle hash. Apart from keeping
            // memory bounded, this makes the concatenation unambiguous even when an embedded
            // shader stream has no seekable length.
            using IncrementalHash shaderHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    shaderHash.AppendData(buffer.AsSpan(0, bytesRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            hash.AppendData(shaderHash.GetHashAndReset());
        }

        private static string ResolveBuildConfiguration()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        private static ulong SumDirectionalShadowMeshlets(SceneRenderingData sceneData)
        {
            ulong sum = 0;
            for (int i = 0; i < sceneData.DirectionalShadowMeshletCounts.Length; i++)
                sum += (ulong)Math.Max(0, sceneData.DirectionalShadowMeshletCounts[i]);
            return sum;
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

        private bool ExecuteRenderGraphWithAsyncPlan(
            AsyncComputeSubmissionPlan plan,
            SceneRenderingData sceneData,
            out string fallbackReason)
        {
            fallbackReason = string.Empty;
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (!plan.Accepted || !plan.ContainsAsyncCompute)
                throw new InvalidOperationException("Only an accepted plan with compute segments can be recorded asynchronously.");
            if (plan.ResourcePlanGeneration != _renderGraph.ConcreteResourceBindings.Generation)
            {
                _renderGraph.ConcreteResourceBindings.RecordStalePlanRejection();
                fallbackReason = "The concrete resource-binding generation changed after plan compilation.";
                RecordRecoverableAsyncComputePlanFallback(
                    new AsyncComputePlanRetryScope(
                        plan.ResourcePlanGeneration,
                        ComputeAsyncComputeSettingsSignature(Settings.AsyncCompute)),
                    fallbackReason);
                sceneData.DdgiAsyncComputeEnabled = 0;
                EnsureSwapchainImageColorAttachment(_currentCommandBuffer);
                _renderGraph.Execute(
                    _currentCommandBuffer,
                    _currentFrame,
                    sceneData,
                    _gpuTimestamps,
                    _cmd,
                    Settings.UseSecondaryCommandBuffers);
                return false;
            }

            AsyncComputeSubmissionSegment? earlySwapchainSegment = plan.Segments.FirstOrDefault(segment =>
                segment.AccessesSwapchain && !segment.IsTerminalGraphicsSegment);
            if (earlySwapchainSegment != null)
            {
                fallbackReason =
                    $"Async compute plan segment {earlySwapchainSegment.Id} ({earlySwapchainSegment.Queue}: " +
                    $"{string.Join(", ", earlySwapchainSegment.Passes)}) would access the acquired swapchain image " +
                    "before the terminal graphics submission.";
                RecordRecoverableAsyncComputePlanFallback(
                    new AsyncComputePlanRetryScope(
                        plan.ResourcePlanGeneration,
                        ComputeAsyncComputeSettingsSignature(Settings.AsyncCompute)),
                    fallbackReason);
                sceneData.DdgiAsyncComputeEnabled = 0;
                EnsureSwapchainImageColorAttachment(_currentCommandBuffer);
                _renderGraph.Execute(
                    _currentCommandBuffer,
                    _currentFrame,
                    sceneData,
                    _gpuTimestamps,
                    _cmd,
                    Settings.UseSecondaryCommandBuffers);
                return false;
            }

            try
            {
                // This must be set before executing selected passes. Simple DDGI uses it to choose
                // the local post-dispatch dependency; it is true only after the entire scheduler
                // plan has passed validation and is about to be recorded on its compute segment.
                sceneData.DdgiAsyncComputeEnabled = IsDdgiAsyncComputeActuallyEnabled(plan) ? 1 : 0;
                _renderGraph.BeginSplitExecution(sceneData);
                _asyncComputePlannedReleaseBarrierCountThisFrame = plan.PlannedReleaseBarrierCount;
                _asyncComputePlannedAcquireBarrierCountThisFrame = plan.PlannedAcquireBarrierCount;
                _asyncComputeTransferredBytesThisFrame = plan.TransferBytes;
                _asyncComputeTransferredImageSubresourcesThisFrame = plan.TransferImageSubresources;

                foreach (AsyncComputeSubmissionSegment segment in plan.Segments)
                {
                    CommandBuffer commandBuffer;
                    if (segment.Id == 0)
                    {
                        commandBuffer = _currentCommandBuffer;
                    }
                    else if (segment.Queue == AsyncComputeQueue.Compute)
                    {
                        commandBuffer = _cmd.BeginAsyncComputeCommand(_currentFrame);
                        _gpuTimestamps.BeginComputeQueueFrame(commandBuffer, _currentFrame);
                    }
                    else
                    {
                        commandBuffer = _cmd.BeginScheduledGraphicsCommand(_currentFrame);
                        SetViewportAndScissor(commandBuffer);
                    }

                    long barrierRecordStart = Stopwatch.GetTimestamp();
                    QueueOwnershipTransferBarrierCounts acquireCounts = QueueOwnershipTransferRecorder.RecordAcquires(
                        _context,
                        commandBuffer,
                        segment.AcquireTransfers);
                    _asyncComputeBarrierRecordMicrosecondsThisFrame += ElapsedMicroseconds(barrierRecordStart);
                    _asyncComputeEmittedAcquireBarrierCountThisFrame += acquireCounts.AcquireCount;

                    if (segment.AccessesSwapchain)
                        EnsureSwapchainImageColorAttachment(commandBuffer);

                    if (segment.Passes.Count > 0)
                    {
                        var passNames = new HashSet<string>(segment.Passes, StringComparer.Ordinal);
                        _renderGraph.ExecuteSelected(
                            commandBuffer,
                            _currentFrame,
                            sceneData,
                            passNames.Contains,
                            _gpuTimestamps,
                            _cmd,
                            useSecondaryCommandBuffers: segment.Queue == AsyncComputeQueue.Graphics && Settings.UseSecondaryCommandBuffers,
                            isComputeQueue: segment.Queue == AsyncComputeQueue.Compute,
                            usesExplicitQueueTransfers: true);
                    }

                    barrierRecordStart = Stopwatch.GetTimestamp();
                    QueueOwnershipTransferBarrierCounts releaseCounts = QueueOwnershipTransferRecorder.RecordReleases(
                        _context,
                        commandBuffer,
                        segment.ReleaseTransfers);
                    _asyncComputeBarrierRecordMicrosecondsThisFrame += ElapsedMicroseconds(barrierRecordStart);
                    _asyncComputeEmittedReleaseBarrierCountThisFrame += releaseCounts.ReleaseCount;
                    _asyncComputeOwnershipTransferCountThisFrame += releaseCounts.OwnershipTransferCount;

                    if (segment.IsTerminalGraphicsSegment)
                    {
                        _currentCommandBuffer = commandBuffer;
                        _asyncComputeWaitsThisFrame.Clear();
                        _asyncComputeWaitsThisFrame.AddRange(segment.TimelineWaits);
                        continue;
                    }

                    _cmd.EndCommandBuffer(commandBuffer);
                    _deferredAsyncSubmissions.Add(new DeferredAsyncSubmission(commandBuffer, segment));
                }

                foreach (QueueOwnershipTransfer transfer in plan.Transfers.OrderBy(transfer => transfer.Id))
                {
                    if (!transfer.IsConcurrentResource)
                    {
                        foreach (RenderGraphConcreteResourceBinding binding in transfer.AllBindings)
                            _renderGraph.ConcreteResourceBindings.CommitOwner(binding, transfer.DestinationQueueFamily);
                    }
                }

                ulong maxTimelineValue = plan.Segments
                    .Select(segment => segment.TimelineSignalValue.GetValueOrDefault())
                    .DefaultIfEmpty(0UL)
                    .Max();
                if (maxTimelineValue >= _nextAsyncComputeTimelineValue)
                    _nextAsyncComputeTimelineValue = checked(maxTimelineValue + 1UL);

                _asyncComputePlanRecordedThisFrame = true;
                sceneData.GraphQueueOwnershipTransitionCount = plan.QueueFamilyOwnershipTransferCount;
                sceneData.AsyncComputeOwnershipTransferCount = _asyncComputeOwnershipTransferCountThisFrame;
                sceneData.GraphBarrierSummary = $"async plan: {plan.GraphicsSegmentCount} graphics segments, {plan.ComputeSegmentCount} compute segments, {plan.QueueFamilyOwnershipTransferCount} queue-family handoffs";
                _renderGraph.CompleteSplitExecution(sceneData);
                return true;
            }
            catch (Exception exception)
            {
                // Some graph work is already appended after the renderer's upload/prelude
                // commands, so replaying the graphics-only graph here would duplicate work in
                // the same command buffer. Nothing has been submitted yet; abandon this frame
                // rather than risk a partially recorded submission, and permanently fail closed.
                MarkFrameSubmissionFault(
                    $"Failed to record the validated async plan before submission: {exception.Message}",
                    Result.ErrorUnknown);
                throw;
            }
        }

        private static AsyncComputePlan CreateGraphicsFallbackAsyncPlan(AsyncComputePlan source, string reason)
        {
            string fallbackReason = string.IsNullOrWhiteSpace(reason)
                ? "Async command recording was rejected before submission."
                : reason;
            IReadOnlyList<AsyncComputePathRuntimeStatus> paths = source.SubmissionPlan.Paths
                .Select(path => path.Active || path.Eligible
                    ? path with
                    {
                        Active = false,
                        Eligible = false,
                        Status = AsyncComputePathStatus.ValidationFallback,
                        Reason = fallbackReason
                    }
                    : path)
                .ToArray();
            var submissionPlan = new AsyncComputeSubmissionPlan(
                Accepted: false,
                FailureReason: fallbackReason,
                source.SubmissionPlan.ResourcePlanGeneration,
                Array.Empty<AsyncComputeSubmissionSegment>(),
                Array.Empty<QueueOwnershipTransfer>(),
                paths);
            return source with
            {
                EffectiveMode = AsyncComputeMode.Disabled,
                SubmissionPlan = submissionPlan,
                Status = $"validation fallback: {fallbackReason}"
            };
        }

        private unsafe void SubmitDeferredAsyncSubmissions()
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
                    waitValues[i] = wait.Value;
                }

                if (signalCount > 0)
                {
                    signalSemaphores[0] = _cmd.AsyncComputeTimelineSemaphore;
                    signalValues[0] = segment.TimelineSignalValue!.Value;
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
                if (segment.Queue == AsyncComputeQueue.Compute)
                    _lastAsyncComputeSubmitMicroseconds += elapsed;
                if (result != Result.Success)
                {
                    MarkFrameSubmissionFault(
                        $"Failed to submit scheduled {segment.Queue} segment {segment.Id}: {result}. " +
                        "The frame is abandoned before its terminal graphics submission so command-pool reuse cannot race submitted work.",
                        result);
                    throw new VulkanException($"Failed to submit scheduled {segment.Queue} command buffer", result);
                }

                RecordSubmittedAsyncComputeSegment(segment.Queue);
            }

            _deferredAsyncSubmissions.Clear();
        }

        private void RecordSubmittedAsyncComputeSegment(AsyncComputeQueue queue)
        {
            if (queue == AsyncComputeQueue.Compute)
                _asyncComputeSubmittedComputeSegmentsThisFrame++;
            else
                _asyncComputeSubmittedGraphicsSegmentsThisFrame++;
        }

        private void UpdateAsyncComputeSubmissionDiagnostics()
        {
            _lastDiagnostics = _lastDiagnostics with
            {
                AsyncComputeSubmittedGraphicsSegmentCount = _asyncComputeSubmittedGraphicsSegmentsThisFrame,
                AsyncComputeSubmittedComputeSegmentCount = _asyncComputeSubmittedComputeSegmentsThisFrame
            };
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
        /// Records an invalid immutable scheduler plan without disabling async compute for the
        /// rest of the renderer session. The retry gate keeps this exact graph/settings scope on
        /// the graphics-only reference path; any regenerated scope is allowed to validate again.
        /// </summary>
        private void RecordRecoverableAsyncComputePlanFallback(
            AsyncComputePlanRetryScope scope,
            string reason)
        {
            if (_asyncComputeRecoverablePlanRetryGate.RecordRejected(scope, reason))
                _asyncComputeValidationFallbackCount++;
            _asyncComputeLastFallbackReason = _asyncComputeRecoverablePlanRetryGate.Reason;
        }

        private void ObserveAsyncComputePlanRetryScope(AsyncComputePlanRetryScope scope)
        {
            if (_asyncComputeRecoverablePlanRetryGate.ObserveScope(scope) &&
                !_asyncComputeEmergencyFallbackLatched)
            {
                // Diagnostics describe the active scope, not a repaired historic failure.
                _asyncComputeLastFallbackReason = string.Empty;
            }
        }

        private void RecordValidatedAsyncComputePlan(AsyncComputePlanRetryScope scope)
        {
            _asyncComputeRecoverablePlanRetryGate.RecordValidatedPlan(scope);
            if (!_asyncComputeEmergencyFallbackLatched &&
                !_asyncComputeRecoverablePlanRetryGate.RejectedScope.HasValue)
            {
                _asyncComputeLastFallbackReason = string.Empty;
            }
        }

        /// <summary>
        /// This latch is intentionally reserved for faults after command recording/submission has
        /// started. Unlike a plan declaration error, those faults can leave Vulkan work in flight
        /// or the acquired image in an indeterminate presentation state and must fail closed.
        /// </summary>
        private void LatchAsyncComputeEmergencyFallback(string reason)
        {
            _asyncComputeEmergencyFallbackLatched = true;
            _asyncComputeLastFallbackReason = string.IsNullOrWhiteSpace(reason)
                ? "Async compute synchronization-plan failure."
                : reason;
            _asyncComputeValidationFallbackCount++;
        }

        /// <summary>
        /// A failed queue submit is not recoverable as a normal frame: it may have left an
        /// acquired image unpresented or a non-terminal command buffer in flight.  Stop future
        /// frame acquisition before a reset fence or command pool can be reused. Device loss is
        /// recorded separately because no Vulkan recovery submission is legal in that state.
        /// </summary>
        private void MarkFrameSubmissionFault(string reason, Result result)
        {
            _deviceLost |= result == Result.ErrorDeviceLost;
            _frameSubmissionFaulted = true;
            _frameSubmissionFaultReason = string.IsNullOrWhiteSpace(reason)
                ? "A Vulkan frame submission failed."
                : reason;
            // A terminal fence will never be safely reused after this latch, so
            // any GPU readback or queued request must fail explicitly rather
            // than remain pending forever.
            _screenshotReadbackManager.FailAll(
                $"Renderer screenshot capture was cancelled because frame submission failed: {_frameSubmissionFaultReason}",
                includeQueuedRequests: true);
            _linearHdrReadbackManager.FailAll(
                $"Linear HDR capture was cancelled because frame submission failed: {_frameSubmissionFaultReason}",
                includeQueuedRequests: true);
            LatchAsyncComputeEmergencyFallback(_frameSubmissionFaultReason);
            _asyncComputeTimingFrames[_currentFrame] = null;
            _deferredAsyncSubmissions.Clear();
            _frameInProgress = false;
        }

        private void ThrowIfFrameSubmissionFaulted()
        {
            if (!_frameSubmissionFaulted)
                return;

            string prefix = _deviceLost
                ? "The Vulkan device was lost during a frame submission."
                : "A previous frame submission failed and the renderer was stopped before unsafe resource reuse.";
            throw new InvalidOperationException($"{prefix} {_frameSubmissionFaultReason}");
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
            if (_deviceLost || waitCount <= 0)
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

        private sealed class AsyncComputeTimingFrame
        {
            public AsyncComputeTimingFrame(
                AsyncComputePlan plan,
                IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingKey> keys)
            {
                Plan = plan ?? throw new ArgumentNullException(nameof(plan));
                Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            }

            public AsyncComputePlan Plan { get; }
            public IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingKey> Keys { get; }
            public long CpuSubmitMicroseconds { get; set; }
            public long CpuBarrierRecordMicroseconds { get; set; }
        }

        private AsyncComputePlan BuildAsyncComputePlan(SceneRenderingData sceneData)
        {
            if (sceneData.ScenePayloadRebuilt != 0 ||
                sceneData.HiZPolicySceneChanged != 0 ||
                sceneData.HiZPolicyCameraCut != 0)
            {
                // A scene reload, material/object payload rebuild, or camera cut changes the
                // producer/consumer workload enough that prior Auto samples are not comparable.
                // Drop in-flight samples as well: their query results belong to the old workload.
                _asyncComputeTimingPolicy.Clear();
                Array.Clear(_asyncComputeTimingFrames);
                _nextAutoTimingProbePath = 0;
            }

            AsyncComputeMode requestedMode = Settings.AsyncCompute.Mode;
            if (_lastAsyncComputeTimingMode != requestedMode)
            {
                // Force validation deliberately permits several paths at once, so its timings
                // are not attributable to an individual Auto candidate. Likewise, a policy
                // change is a user-visible workload change. Start a fresh baseline instead of
                // letting a previous mode promote a path with mixed samples.
                _asyncComputeTimingPolicy.Clear();
                Array.Clear(_asyncComputeTimingFrames);
                _nextAutoTimingProbePath = 0;
                _lastAsyncComputeTimingMode = requestedMode;
            }

            // Disabled is the exact graphics-only fallback and must retain the original hot
            // path. Do not rebuild concrete bindings, allocate scheduler inputs, or touch a
            // compute command resource merely to report that policy is off.
            if (requestedMode == AsyncComputeMode.Disabled)
                return CreateDisabledAsyncComputePlan(sceneData);

            AsyncComputeMode effectiveMode = _asyncComputeEmergencyFallbackLatched
                ? AsyncComputeMode.Disabled
                : requestedMode;
            bool supported = _context.HasIndependentComputeQueue && _cmd.AsyncComputeTimelineSemaphore.Handle != 0;
            ulong settingsSignature = ComputeAsyncComputeSettingsSignature(Settings.AsyncCompute);
            // A resource-plan refresh resolves many imported allocations. Avoid that CPU work on
            // unsupported devices and after the emergency kill switch has already selected the
            // graphics-only path; neither case can legally submit a compute segment.
            if (supported && effectiveMode != AsyncComputeMode.Disabled)
                RefreshAsyncComputeResourceBindings(sceneData);
            var retryScope = new AsyncComputePlanRetryScope(
                _renderGraph.ConcreteResourceBindings.Generation,
                settingsSignature);
            ObserveAsyncComputePlanRetryScope(retryScope);
            var capabilities = new AsyncComputeQueueCapabilities(
                supported,
                _context.GraphicsQueueFamilyIndex,
                _context.ComputeQueueFamilyIndex);

            AsyncComputePath[] allPaths = Enum.GetValues<AsyncComputePath>();
            var requestedByFeature = new Dictionary<AsyncComputePath, bool>(allPaths.Length);
            var timingDecisions = new Dictionary<AsyncComputePath, AsyncComputeTimingDecision>(allPaths.Length);
            foreach (AsyncComputePath path in allPaths)
            {
                requestedByFeature.Add(
                    path,
                    Settings.AsyncCompute.IsEnabledBy(path) &&
                    RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, GetRepresentativeAsyncPass(path)) &&
                    IsAsyncComputePathFeatureActive(path, sceneData));
                timingDecisions.Add(path, GetAsyncComputeTimingDecision(path, sceneData));
            }

            // Auto intentionally runs at most one path at a time. A path's timing samples are
            // only meaningful when they are not contaminated by a different async path sharing
            // the same frame, and a validated active path must continue receiving samples so it
            // can demote on a workload regression.
            AsyncComputePath? autoEnabledPath = effectiveMode == AsyncComputeMode.Auto
                ? SelectAutoEnabledPath(sceneData, requestedByFeature, timingDecisions)
                : null;
            AsyncComputePath? autoTimingProbe = effectiveMode == AsyncComputeMode.Auto && !autoEnabledPath.HasValue
                ? SelectAutoTimingProbe(sceneData, requestedByFeature)
                : null;
            AsyncComputePath? autoIsolatedPath = autoEnabledPath ?? autoTimingProbe;
            var paths = new List<AsyncComputePathEligibility>(allPaths.Length);
            foreach (AsyncComputePath path in allPaths)
            {
                bool requested = requestedByFeature[path];
                AsyncComputeTimingDecision timing = timingDecisions[path];
                bool isSelectedAutoPath = autoIsolatedPath == path;
                bool isProbe = autoTimingProbe == path;
                bool pauseForAutoIsolation = effectiveMode == AsyncComputeMode.Auto &&
                    requested &&
                    autoIsolatedPath.HasValue &&
                    !isSelectedAutoPath;
                bool timingEligible = effectiveMode == AsyncComputeMode.Auto
                    ? isSelectedAutoPath && timing.Eligible
                    : timing.Eligible;
                AsyncComputePathStatus timingStatus = pauseForAutoIsolation
                    ? timing.Status == AsyncComputePathStatus.Enabled
                        ? AsyncComputePathStatus.PendingWarmup
                        : timing.Status
                    : timing.Status;
                string reason = !requested
                    ? DescribeInactiveAsyncComputePath(path, sceneData)
                    : isProbe
                        ? $"Collecting isolated Auto timing samples for {path}; no other async path is scheduled this frame."
                        : pauseForAutoIsolation
                            ? $"Waiting while {autoIsolatedPath!.Value} is the sole isolated Auto path for this workload."
                            : effectiveMode == AsyncComputeMode.Auto && !autoIsolatedPath.HasValue
                                ? "No complete, independently validated Auto path is available for this workload."
                            : timing.Reason;
                paths.Add(new AsyncComputePathEligibility(
                    path,
                    requested,
                    timingEligible,
                    timingStatus,
                    reason,
                    IsAutoTimingProbe: isProbe));
            }

            var passes = new List<AsyncComputePassRequest>(_renderGraph.PassNames.Count);
            foreach (string passName in _renderGraph.PassNames)
            {
                AsyncComputePath? path = GetAsyncComputePath(passName);
                bool enabledByFeatureIsolation = RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, passName);
                passes.Add(new AsyncComputePassRequest(
                    passName,
                    path,
                    _renderGraph.GetPassResourceUsages(passName),
                    enabledByFeatureIsolation,
                    path?.ToString() ?? string.Empty,
                    WillExecute: enabledByFeatureIsolation && _renderGraph.WillExecutePass(passName, _currentFrame, sceneData)));
            }

            if (!_asyncComputeRecoverablePlanRetryGate.CanAttempt(retryScope))
            {
                return CreateGenerationScopedGraphicsFallbackAsyncPlan(
                    requestedMode,
                    supported,
                    retryScope,
                    paths,
                    passes,
                    sceneData);
            }

            AsyncComputeSubmissionPlan submissionPlan = _asyncComputeScheduler.Compile(new AsyncComputeSchedulerInput(
                effectiveMode,
                capabilities,
                _renderGraph.ConcreteResourceBindings,
                paths,
                passes,
                _currentFrame,
                _nextAsyncComputeTimelineValue));
            if (!submissionPlan.Accepted)
            {
                // The scheduler has not recorded any Vulkan command buffer or committed resource
                // ownership. Keep only this graph/settings scope on the graphics reference path;
                // resize, reload, and settings regeneration receive a fresh validation attempt.
                RecordRecoverableAsyncComputePlanFallback(retryScope, submissionPlan.FailureReason);
                effectiveMode = AsyncComputeMode.Disabled;
            }
            else
            {
                RecordValidatedAsyncComputePlan(retryScope);
            }
            RenderGraphDiagnostics graphDiagnostics = _renderGraph.CreateDiagnostics(
                sceneData.ActiveFeatureIsolation,
                asyncComputeEnabled: false,
                sceneData: sceneData);

            string status = _asyncComputeEmergencyFallbackLatched
                ? $"emergency fallback latched: {_asyncComputeLastFallbackReason}"
                : requestedMode == AsyncComputeMode.Disabled
                    ? "disabled by policy; graphics-only execution is active."
                    : !supported
                        ? "requested but inactive: no independent compute queue is available; using graphics-only execution."
                        : !submissionPlan.Accepted
                            ? $"validation fallback: {submissionPlan.FailureReason}"
                            : submissionPlan.ContainsAsyncCompute
                                ? "enabled: generic async segment plan compiled with validated concrete handoffs."
                                : "no async path was eligible for this frame.";

            return new AsyncComputePlan(
                requestedMode,
                effectiveMode,
                supported,
                submissionPlan,
                graphDiagnostics,
                status);
        }

        private AsyncComputePlan CreateDisabledAsyncComputePlan(SceneRenderingData sceneData)
        {
            bool supported = _context.HasIndependentComputeQueue && _cmd.AsyncComputeTimelineSemaphore.Handle != 0;
            AsyncComputePathRuntimeStatus[] paths = Enum.GetValues<AsyncComputePath>()
                .Select(path => new AsyncComputePathRuntimeStatus(
                    path,
                    Requested: false,
                    Supported: supported,
                    Eligible: false,
                    Active: false,
                    AsyncComputePathStatus.DisabledByPolicy,
                    "Async compute mode is Disabled.",
                    _renderGraph.PassNames.Where(passName => GetAsyncComputePath(passName) == path).ToArray()))
                .ToArray();
            var submissionPlan = new AsyncComputeSubmissionPlan(
                Accepted: true,
                FailureReason: string.Empty,
                _renderGraph.ConcreteResourceBindings.Generation,
                Array.Empty<AsyncComputeSubmissionSegment>(),
                Array.Empty<QueueOwnershipTransfer>(),
                paths);
            return new AsyncComputePlan(
                AsyncComputeMode.Disabled,
                AsyncComputeMode.Disabled,
                supported,
                submissionPlan,
                _renderGraph.CreateDiagnostics(sceneData.ActiveFeatureIsolation, asyncComputeEnabled: false),
                _asyncComputeEmergencyFallbackLatched
                    ? $"emergency fallback latched: {_asyncComputeLastFallbackReason}"
                    : "disabled by policy; graphics-only execution is active.");
        }

        private AsyncComputePlan CreateGenerationScopedGraphicsFallbackAsyncPlan(
            AsyncComputeMode requestedMode,
            bool supported,
            AsyncComputePlanRetryScope scope,
            IReadOnlyList<AsyncComputePathEligibility> eligibility,
            IReadOnlyList<AsyncComputePassRequest> passes,
            SceneRenderingData sceneData)
        {
            string reason = string.IsNullOrWhiteSpace(_asyncComputeRecoverablePlanRetryGate.Reason)
                ? "Async compute plan validation failed for this graph/settings scope."
                : _asyncComputeRecoverablePlanRetryGate.Reason;
            AsyncComputePathRuntimeStatus[] paths = eligibility
                .Select(path =>
                {
                    bool requested = requestedMode != AsyncComputeMode.Disabled && path.RequestedByFeature;
                    IReadOnlyList<string> pathPasses = passes
                        .Where(pass => pass.Path == path.Path)
                        .Select(pass => pass.Name)
                        .ToArray();
                    return new AsyncComputePathRuntimeStatus(
                        path.Path,
                        requested,
                        supported,
                        Eligible: false,
                        Active: false,
                        requested ? AsyncComputePathStatus.ValidationFallback : AsyncComputePathStatus.DisabledByFeature,
                        requested ? reason : path.Reason,
                        pathPasses);
                })
                .ToArray();
            var submissionPlan = new AsyncComputeSubmissionPlan(
                Accepted: false,
                FailureReason: reason,
                scope.ResourcePlanGeneration,
                Array.Empty<AsyncComputeSubmissionSegment>(),
                Array.Empty<QueueOwnershipTransfer>(),
                paths);
            return new AsyncComputePlan(
                requestedMode,
                AsyncComputeMode.Disabled,
                supported,
                submissionPlan,
                _renderGraph.CreateDiagnostics(sceneData.ActiveFeatureIsolation, asyncComputeEnabled: false, sceneData: sceneData),
                $"validation fallback retained for plan generation {scope.ResourcePlanGeneration}: {reason}");
        }

        private static ulong ComputeAsyncComputeSettingsSignature(AsyncComputeSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            const ulong offsetBasis = 14695981039346656037UL;
            ulong signature = offsetBasis;
            signature = MixAsyncComputeSettingsSignature(signature, (ulong)settings.Mode);
            signature = MixAsyncComputeSettingsSignature(signature, settings.HiZBuildEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.AmbientOcclusionBlurEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.SsgiChainEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.FogEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.BloomEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.SimpleDdgiUpdateEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.FullDdgiUpdateEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.FarFieldClipmapBakeEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, settings.GpuParticlesEnabled ? 1UL : 0UL);
            signature = MixAsyncComputeSettingsSignature(signature, unchecked((uint)settings.AutoMinimumSampleCount));
            signature = MixAsyncComputeSettingsSignature(signature, unchecked((uint)settings.AutoWarmupFrameCount));
            signature = MixAsyncComputeSettingsSignature(signature, BitConverter.SingleToUInt32Bits(settings.AutoMinimumAbsoluteBenefitMilliseconds));
            signature = MixAsyncComputeSettingsSignature(signature, BitConverter.SingleToUInt32Bits(settings.AutoMinimumRelativeBenefit));
            return MixAsyncComputeSettingsSignature(signature, unchecked((uint)settings.AutoDecisionCooldownFrames));
        }

        private static ulong MixAsyncComputeSettingsSignature(ulong signature, ulong value)
        {
            const ulong prime = 1099511628211UL;
            return (signature ^ value) * prime;
        }

        /// <summary>
        /// Resolves graph resources to the exact Vulkan allocations used by this frame. Imported
        /// buffer families are deliberately bound at allocation granularity; a bindless index or
        /// a broad BufferSet is never treated as a queue-ownership target.
        /// </summary>
        private void RefreshAsyncComputeResourceBindings(SceneRenderingData sceneData)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));

            var bindings = new List<RenderGraphConcreteResourceBinding>();
            uint graphicsFamily = _context.GraphicsQueueFamilyIndex;
            uint computeFamily = _context.ComputeQueueFamilyIndex;
            IReadOnlyList<uint> queueFamilies = graphicsFamily == computeFamily
                ? new[] { graphicsFamily }
                : new[] { graphicsFamily, computeFamily };

            foreach (RenderGraphResourceId resource in Enum.GetValues<RenderGraphResourceId>())
            {
                IReadOnlyList<RenderTarget> targets = _renderGraph.GetOwnedRenderTargets(resource);
                for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    RenderTarget target = targets[targetIndex];
                    AddRenderTargetBinding(
                        bindings,
                        resource,
                        target,
                        queueFamilies,
                        graphicsFamily,
                        RenderGraphResourceLifetime.Persistent,
                        targetIndex,
                        targets.Count > 1 ? targetIndex : -1);
                }
            }

            // These two renderer-owned targets are graph imports rather than graph-owned
            // allocations. They are nevertheless concrete Vulkan images and must participate in
            // a plan before Hi-Z, fog, bloom, or SSGI can be placed on compute.
            if (_renderTargets != null)
            {
                AddRenderTargetBinding(
                    bindings,
                    RenderGraphResourceId.SceneColor,
                    _renderTargets.SceneColor,
                    queueFamilies,
                    graphicsFamily,
                    RenderGraphResourceLifetime.Imported,
                    bindingIndex: 0,
                    historyIndex: -1);
                AddRenderTargetBinding(
                    bindings,
                    RenderGraphResourceId.SceneDepth,
                    _renderTargets.SceneDepth,
                    queueFamilies,
                    graphicsFamily,
                    RenderGraphResourceLifetime.Imported,
                    bindingIndex: 0,
                    historyIndex: -1);
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
                    layoutTracker: layout => hiz.Layout = layout));
            }

            if (_imageIndex < _swapchain.Images.Length && _swapchain.Images[_imageIndex].Handle != 0)
            {
                Image swapchainImage = _swapchain.Images[_imageIndex];
                bindings.Add(RenderGraphConcreteResourceBinding.ForImage(
                    RenderGraphResourceId.SwapchainColor,
                    $"Swapchain image {_imageIndex}",
                    swapchainImage,
                    new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    _swapchain.GetImageLayout(_imageIndex),
                    queueFamilies,
                    graphicsFamily,
                    SharingMode.Exclusive,
                    frameIndex: _currentFrame,
                    allocationGeneration: swapchainImage.Handle,
                    lifetime: RenderGraphResourceLifetime.Imported,
                    layoutTracker: layout => _swapchain.SetImageLayout(_imageIndex, layout)));
            }

            if (_accelerationStructureManager != null)
            {
                foreach (AccelerationStructureStorageBuffer storage in _accelerationStructureManager.GetRayQueryStorageBuffers())
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
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.RayQueryInstanceMetadata, "Ray-query instance metadata",
                _accelerationStructureManager?.RayQueryInstanceMetadataBuffer ?? BufferHandle.Invalid,
                queueFamilies, graphicsFamily);

            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh vertex positions",
                _meshManager.VertexPositionBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.MeshGeometryBuffers, "Mesh vertex normal tangents",
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
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.DdgiEmissiveSources, "DDGI emissive sources",
                _ddgiEmissiveSourceBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.RendererDiagnosticsBuffer, "Renderer diagnostics",
                _diagnosticsBuffer.GetBufferHandle(_currentFrame), queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle frame data",
                sceneData.ParticleFrameDataBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle instances",
                sceneData.ParticleInstanceBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.ParticleBuffers, "Particle batches",
                sceneData.ParticleBatchBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);

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
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood, "Far-field distance field",
                    _farFieldClipmapManager.DistanceBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood, "Far-field jump-flood scratch 0",
                    _farFieldClipmapManager.JumpFloodScratch0Buffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldJumpFlood, "Far-field jump-flood scratch 1",
                    _farFieldClipmapManager.JumpFloodScratch1Buffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FarFieldPageTable, "Far-field page table",
                    _farFieldClipmapManager.PageTableBuffer, queueFamilies, graphicsFamily);
            }

            if (_simpleDdgiVolumeManager != null)
            {
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiParameters, "Simple DDGI parameters",
                    _simpleDdgiVolumeManager.ParamsBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiIrradianceAtlas, "Simple DDGI irradiance atlas",
                    _simpleDdgiVolumeManager.IrradianceAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiTransportAtlas, "Simple DDGI transport irradiance target",
                    _simpleDdgiVolumeManager.TransportIrradianceAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiTransportSourceCache, "Simple DDGI transport source cache",
                    _simpleDdgiVolumeManager.TransportSourceCacheBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiVisibilityAtlas, "Simple DDGI visibility atlas",
                    _simpleDdgiVolumeManager.VisibilityAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiRayScratch, "Simple DDGI ray scratch",
                    _simpleDdgiVolumeManager.RayResultScratchBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiProbeState, "Simple DDGI probe state",
                    _simpleDdgiVolumeManager.ProbeStateBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiUpdateQueue, "Simple DDGI update queue",
                    _simpleDdgiVolumeManager.ProbeUpdateQueueBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.SimpleDdgiRelocationData, "Simple DDGI relocation classification",
                    _simpleDdgiVolumeManager.RelocationClassificationBuffer, queueFamilies, graphicsFamily);
            }

            if (_ddgiProbeVolumeManager != null)
            {
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiState, "DDGI volume metadata",
                    _ddgiProbeVolumeManager.VolumeMetadataBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiState, "DDGI probe state",
                    _ddgiProbeVolumeManager.ProbeStateBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiState, "DDGI probe update queue",
                    _ddgiProbeVolumeManager.ProbeUpdateQueueBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiState, "DDGI relocation classification",
                    _ddgiProbeVolumeManager.ProbeRelocationClassificationBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiAtlases, "DDGI irradiance atlas",
                    _ddgiProbeVolumeManager.IrradianceAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiAtlases, "DDGI visibility atlas",
                    _ddgiProbeVolumeManager.VisibilityAtlasBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiRayResources, "DDGI ray scratch",
                    _ddgiProbeVolumeManager.RayResultScratchBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI scheduler constants",
                    _ddgiProbeVolumeManager.SchedulerConstantsBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI dirty regions",
                    _ddgiProbeVolumeManager.DirtyRegionBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI probe candidates",
                    _ddgiProbeVolumeManager.ProbeCandidateBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI scheduler group counts",
                    _ddgiProbeVolumeManager.SchedulerGroupCountBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI scheduler prefix",
                    _ddgiProbeVolumeManager.SchedulerPrefixBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI scheduler counters",
                    _ddgiProbeVolumeManager.SchedulerCounterBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI trace indirect dispatch",
                    _ddgiProbeVolumeManager.TraceIndirectDispatchBuffer, queueFamilies, graphicsFamily);
                AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.FullDdgiScheduler, "DDGI scheduler counter readback",
                    _ddgiProbeVolumeManager.GetSchedulerCounterReadbackBuffer(_currentFrame), queueFamilies, graphicsFamily,
                    frameIndex: _currentFrame);
            }

            GpuParticleAsyncResourceSet particleResources = _gpuParticleRuntimeManager.GetAsyncResourceSet(_currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleState, "GPU particle state",
                particleResources.StateBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndices, "GPU particle alive indices",
                particleResources.AliveIndexBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndices, "GPU particle dead indices",
                particleResources.DeadIndexBuffer, queueFamilies, graphicsFamily);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleEmitterData, "GPU particle emitters",
                particleResources.EmitterBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleEmitterData, "GPU particle curves",
                particleResources.CurveSampleBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleCounters, "GPU particle counters",
                particleResources.CounterBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleUnsortedOutput, "GPU particle unsorted output",
                particleResources.UnsortedRenderInstanceBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleRenderOutput, "GPU particle render output",
                particleResources.RenderInstanceBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleIndirectArguments, "GPU particle indirect arguments",
                particleResources.IndirectDrawBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleSortKeys, "GPU particle sort keys",
                particleResources.SortKeyBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);
            AddAsyncComputeBufferBinding(bindings, RenderGraphResourceId.GpuParticleCounterReadback, "GPU particle counter readback",
                particleResources.CounterReadbackBuffer, queueFamilies, graphicsFamily, frameIndex: _currentFrame);

            // Deduplicate within a logical set only. The same texture can be reachable through
            // both environment and material descriptors; RenderGraphResourceBindings models
            // those exact aliases with one physical ownership key.
            AddTextureBindings(bindings, RenderGraphResourceId.EnvironmentMaps, "Environment texture", _environmentManager?.GetSampledTextureHandles(),
                queueFamilies, graphicsFamily, new HashSet<ulong>());
            IReadOnlyList<TextureHandle> materialTextures = _materialManager.GetReferencedTextureHandles();
            if (materialTextures.Count == 0)
                materialTextures = [_textureManager.DefaultWhiteTexture];
            int materialTextureBindingCount = AddTextureBindings(bindings, RenderGraphResourceId.MaterialTextures, "Material texture", materialTextures,
                queueFamilies, graphicsFamily, new HashSet<ulong>());
            if (materialTextureBindingCount == 0)
            {
                AddTextureBindings(bindings, RenderGraphResourceId.MaterialTextures, "Material fallback texture",
                    [_textureManager.DefaultWhiteTexture], queueFamilies, graphicsFamily, new HashSet<ulong>());
            }

            _renderGraph.ReplaceConcreteResourceBindings(bindings);
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
                layoutTracker: target.SetTrackedLayout));
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
            int frameIndex = -1)
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
                allocationGeneration: handle.Generation,
                lifetime: RenderGraphResourceLifetime.Imported,
                allocationSize: byteSize,
                initialStageMask: initialStageMask,
                initialAccessMask: initialAccessMask));
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

        private bool IsAsyncComputePathFeatureActive(AsyncComputePath path, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            bool animationDebugActive = sceneData.AnimationDebugView != AnimationDebugView.None;
            bool fogWillExecute =
                Settings.Fog.Enabled &&
                Settings.Fog.Mode != FogMode.Disabled &&
                !animationDebugActive;
            bool ssgiChainWillExecute =
                GlobalIlluminationPassExecutionPolicy.ShouldRunSsgiProducer(gi, sceneData.DebugViewMode) &&
                gi.TemporalEnabled &&
                sceneData.DepthPrePassEnabled &&
                sceneData.FoliageDebugView == 0 &&
                !animationDebugActive;
            return path switch
            {
                AsyncComputePath.SimpleDdgiUpdate =>
                    gi.EffectiveUseSimpleDdgi &&
                    gi.DdgiAsyncComputeEnabled &&
                    sceneData.SimpleDdgiActive != 0 &&
                    sceneData.SimpleDdgiProbesUpdated > 0 &&
                    // Sampled-atlas images are currently submitted as direct
                    // graphics-queue resources. Keep the whole producer chain
                    // on graphics until the render graph owns their queue
                    // transitions and lifetime declarations.
                    sceneData.SimpleDdgiSampledAtlasActive == 0,
                AsyncComputePath.FullDdgiUpdate =>
                    gi.EffectiveUseDdgi &&
                    !gi.EffectiveUseSimpleDdgi &&
                    gi.DdgiAsyncComputeEnabled &&
                    sceneData.GlobalIlluminationDdgiActive != 0 &&
                    sceneData.DdgiProbesUpdated > 0,
                AsyncComputePath.FarFieldClipmapBake =>
                    gi.EffectiveUseSimpleDdgi &&
                    gi.FarFieldClipmapEnabled &&
                    _farFieldClipmapManager?.BakePending == true,
                AsyncComputePath.AmbientOcclusionBlur =>
                    Settings.AmbientOcclusion.Enabled &&
                    sceneData.DepthPrePassEnabled &&
                    Settings.AmbientOcclusion.BlurRadius > 0,
                AsyncComputePath.HiZBuild => sceneData.HiZBuildEnabled,
                AsyncComputePath.SsgiChain => ssgiChainWillExecute,
                // Fog samples Simple DDGI. Keep it on graphics whenever the
                // optional image mirror is live for the same ownership reason.
                AsyncComputePath.Fog => fogWillExecute && sceneData.SimpleDdgiSampledAtlasActive == 0,
                AsyncComputePath.Bloom =>
                    Settings.Bloom.Enabled &&
                    _renderTargets?.BloomMipCount > 0 &&
                    !(fogWillExecute && Settings.Fog.DebugView != FogDebugView.None),
                AsyncComputePath.GpuParticles =>
                    sceneData.GpuParticlesEnabled != 0 &&
                    sceneData.GpuParticleEmitterCount > 0 &&
                    sceneData.GpuParticleCapacity > 0,
                _ => false
            };
        }

        private AsyncComputeTimingDecision GetAsyncComputeTimingDecision(
            AsyncComputePath path,
            SceneRenderingData sceneData)
        {
            if (Settings.AsyncCompute.Mode != AsyncComputeMode.Auto)
            {
                return new AsyncComputeTimingDecision(
                    AsyncComputePathStatus.Enabled,
                    Eligible: true,
                    Active: true,
                    "Timing gate is bypassed only by ForceEnabledForValidation; resource and capability validation still apply.",
                    new AsyncComputeTimingStats(0, 0, 0, 0),
                    new AsyncComputeTimingStats(0, 0, 0, 0));
            }

            return _asyncComputeTimingPolicy.Evaluate(
                CreateAsyncComputeTimingKey(path, sceneData),
                Settings.AsyncCompute,
                _temporalSampleIndex > int.MaxValue ? int.MaxValue : (int)_temporalSampleIndex);
        }

        /// <summary>
        /// Auto keeps one proven path active at a time. Combining independently measured paths
        /// would make their samples non-attributable and can turn two local wins into a frame-time
        /// loss through extra queue waits or submissions.
        /// </summary>
        private AsyncComputePath? SelectAutoEnabledPath(
            SceneRenderingData sceneData,
            IReadOnlyDictionary<AsyncComputePath, bool> requestedByFeature,
            IReadOnlyDictionary<AsyncComputePath, AsyncComputeTimingDecision> timingDecisions)
        {
            AsyncComputePath[] candidates = Enum.GetValues<AsyncComputePath>();
            return candidates
                .Where(path => requestedByFeature.TryGetValue(path, out bool requested) && requested)
                .Where(path => timingDecisions.TryGetValue(path, out AsyncComputeTimingDecision? timing) && timing.Eligible)
                .OrderByDescending(path =>
                {
                    AsyncComputeTimingDecision timing = timingDecisions[path];
                    return timing.GraphicsOnly.MeanMilliseconds - timing.Async.MeanMilliseconds;
                })
                .ThenBy(path => (int)path)
                .Select(path => (AsyncComputePath?)path)
                .FirstOrDefault(path => path.HasValue && HasCompleteAsyncResourcePlan(path.Value, sceneData));
        }

        private bool HasCompleteAsyncResourcePlan(AsyncComputePath path, SceneRenderingData sceneData)
        {
            string[] passNames = _renderGraph.PassNames
                .Where(passName => GetAsyncComputePath(passName) == path)
                .Where(passName => _renderGraph.WillExecutePass(passName, _currentFrame, sceneData))
                .ToArray();
            return passNames.Length > 0 &&
                   _renderGraph.ValidateConcreteResourcePlan(passNames, _currentFrame).Count == 0;
        }

        /// <summary>
        /// Auto mode first profiles one concrete, fully bound path at a time. This avoids the
        /// circular policy failure where Auto has no async samples to promote from, and avoids
        /// attributing a combined multi-path frame-time change to every individual path.
        /// </summary>
        private AsyncComputePath? SelectAutoTimingProbe(
            SceneRenderingData sceneData,
            IReadOnlyDictionary<AsyncComputePath, bool> requestedByFeature)
        {
            int frameNumber = _temporalSampleIndex > int.MaxValue ? int.MaxValue : (int)_temporalSampleIndex;
            AsyncComputePath[] allPaths = Enum.GetValues<AsyncComputePath>();
            for (int offset = 0; offset < allPaths.Length; offset++)
            {
                int index = (_nextAutoTimingProbePath + offset) % allPaths.Length;
                AsyncComputePath path = allPaths[index];
                if (!requestedByFeature.TryGetValue(path, out bool requested) || !requested)
                    continue;
                if (!_asyncComputeTimingPolicy.CanCollectAsyncProbe(
                        CreateAsyncComputeTimingKey(path, sceneData),
                        Settings.AsyncCompute,
                        frameNumber))
                {
                    continue;
                }

                if (!HasCompleteAsyncResourcePlan(path, sceneData))
                {
                    // An incomplete path must not monopolize Auto probing. It remains visible as
                    // MissingResourcePlan when explicitly forced, while complete paths continue
                    // collecting their own samples.
                    continue;
                }

                _nextAutoTimingProbePath = (index + 1) % allPaths.Length;
                return path;
            }

            return null;
        }

        private AsyncComputeTimingKey CreateAsyncComputeTimingKey(
            AsyncComputePath path,
            SceneRenderingData sceneData)
        {
            DeviceRequirementReport? device = _context.SelectedDeviceRequirementReport;
            return new AsyncComputeTimingKey(
                string.IsNullOrWhiteSpace(device?.DeviceName) ? "unknown-device" : device.DeviceName,
                string.IsNullOrWhiteSpace(device?.DriverVersion) ? "unknown-driver" : device.DriverVersion,
                BuildAsyncComputeWorkloadIdentity(sceneData),
                path);
        }

        private string DescribeInactiveAsyncComputePath(AsyncComputePath path, SceneRenderingData sceneData)
        {
            if (!Settings.AsyncCompute.IsEnabledBy(path))
                return "Disabled by the per-path async-compute setting.";
            if (!RenderFeatureIsolationPolicy.ShouldExecutePass(sceneData.ActiveFeatureIsolation, GetRepresentativeAsyncPass(path)))
                return "Disabled by active feature-isolation mode.";

            return path switch
            {
                AsyncComputePath.SimpleDdgiUpdate => "Simple DDGI is inactive for this frame.",
                AsyncComputePath.FullDdgiUpdate => "Full DDGI is inactive for this frame.",
                AsyncComputePath.FarFieldClipmapBake => "No far-field clipmap bake is pending.",
                AsyncComputePath.AmbientOcclusionBlur => "Ambient occlusion blur is inactive for this frame.",
                AsyncComputePath.HiZBuild => "Hi-Z build is inactive for this frame.",
                AsyncComputePath.SsgiChain => "The complete SSGI chain is inactive for this frame.",
                AsyncComputePath.Fog => "Fog is disabled for this frame.",
                AsyncComputePath.Bloom => "Bloom is disabled or has no mip chain.",
                AsyncComputePath.GpuParticles => "GPU particle emitters are inactive for this frame.",
                _ => "The feature is inactive for this frame."
            };
        }

        private string BuildAsyncComputeWorkloadIdentity(SceneRenderingData sceneData)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{sceneData.ScreenWidth}x{sceneData.ScreenHeight}|{sceneData.ActiveFeatureIsolation}|{sceneData.ObjectCount}|{sceneData.MeshletCount}|{sceneData.LightCount}|{sceneData.DepthPrePassEnabled}|{sceneData.HiZBuildEnabled}|{sceneData.GpuParticlesEnabled}|{sceneData.GpuParticleEmitterCount}|{sceneData.FoliageClusterCount}|{Settings.GlobalIllumination.Mode}|{Settings.GlobalIllumination.EffectiveUseSsgi}|{Settings.GlobalIllumination.EffectiveUseDdgi}|{Settings.GlobalIllumination.EffectiveUseSimpleDdgi}|{Settings.AmbientOcclusion.Enabled}|{Settings.AmbientOcclusion.BlurRadius}|{Settings.Fog.Enabled}|{Settings.Fog.Mode}|{Settings.Bloom.Enabled}|{Settings.Bloom.MipCount}|{Settings.AntiAliasing.EffectiveMode}");
        }

        private static string GetRepresentativeAsyncPass(AsyncComputePath path) => path switch
        {
            AsyncComputePath.SimpleDdgiUpdate => "SimpleDdgiTracePass",
            AsyncComputePath.FullDdgiUpdate => "DdgiTracePass",
            AsyncComputePath.FarFieldClipmapBake => "FarFieldClipmapBakePass",
            AsyncComputePath.AmbientOcclusionBlur => "AmbientOcclusionBlurPass",
            AsyncComputePath.HiZBuild => "HiZBuildPass",
            AsyncComputePath.SsgiChain => "SsgiTracePass",
            AsyncComputePath.Fog => "FogPass",
            AsyncComputePath.Bloom => "BloomPass",
            AsyncComputePath.GpuParticles => "GpuParticleSimulatePass",
            _ => string.Empty
        };

        private static AsyncComputePath? GetAsyncComputePath(string passName) => passName switch
        {
            "SimpleDdgiTracePass" or "SimpleDdgiRelocateClassifyPass" or "SimpleDdgiTransportPass" or "SimpleDdgiBlendPass" => AsyncComputePath.SimpleDdgiUpdate,
            "DdgiSchedulePass" or "DdgiTracePass" or "DdgiBlendPass" or "DdgiRelocateClassifyPass" or "DdgiPublishPass" => AsyncComputePath.FullDdgiUpdate,
            "FarFieldClipmapBakePass" => AsyncComputePath.FarFieldClipmapBake,
            "AmbientOcclusionBlurPass" => AsyncComputePath.AmbientOcclusionBlur,
            "HiZBuildPass" => AsyncComputePath.HiZBuild,
            "SsgiTracePass" or "SsgiTemporalPass" or "SsgiDenoisePass" => AsyncComputePath.SsgiChain,
            "FogPass" => AsyncComputePath.Fog,
            "BloomPass" => AsyncComputePath.Bloom,
            "GpuParticleResetPass" or "GpuParticleSimulatePass" or "GpuParticleSortPass" => AsyncComputePath.GpuParticles,
            _ => null
        };

        private void CaptureAsyncComputeTimingFrame(AsyncComputePlan plan, SceneRenderingData sceneData)
        {
            if (plan.RequestedMode == AsyncComputeMode.Disabled)
            {
                _asyncComputeTimingFrames[_currentFrame] = null;
                return;
            }

            if (plan.RequestedMode == AsyncComputeMode.ForceEnabledForValidation)
            {
                // A forced frame can contain multiple independent paths. It remains useful for
                // validation and diagnostics, but cannot provide an attributable Auto sample.
                _asyncComputeTimingFrames[_currentFrame] = null;
                return;
            }

            var keys = new Dictionary<AsyncComputePath, AsyncComputeTimingKey>();
            foreach (AsyncComputePathRuntimeStatus path in plan.SubmissionPlan.Paths)
            {
                // Only collect a graphics baseline while the feature actually executes. A frame
                // with fog, GI, or a history path disabled is not a valid baseline for enabling
                // that path later in Auto mode.
                if (path.Requested)
                    keys.Add(path.Path, CreateAsyncComputeTimingKey(path.Path, sceneData));
            }

            _asyncComputeTimingFrames[_currentFrame] = new AsyncComputeTimingFrame(plan, keys);
        }

        private void FinalizeAsyncComputeTimingFrame(int frameIndex)
        {
            if (_asyncComputeTimingFrames[frameIndex] is { } timingFrame)
            {
                // This includes every queue submit needed by the frame, which is the cost Auto
                // must amortize before it can promote a path.
                timingFrame.CpuSubmitMicroseconds = _lastQueueSubmitMicroseconds;
                timingFrame.CpuBarrierRecordMicroseconds = _asyncComputeBarrierRecordMicrosecondsThisFrame;
            }
        }

        private void RecordCompletedAsyncComputeTimingFrame(int frameIndex, FrameTimingSnapshot timings)
        {
            AsyncComputeTimingFrame? timingFrame = _asyncComputeTimingFrames[frameIndex];
            _asyncComputeTimingFrames[frameIndex] = null;
            if (timingFrame == null)
                return;

            AsyncComputeSubmissionPlan plan = timingFrame.Plan.SubmissionPlan;
            long totalPassGpuMicroseconds = 0;
            foreach (PassTiming timing in timings.Passes)
            {
                if (timing.GpuAvailable && timing.GpuMicroseconds > 0)
                    totalPassGpuMicroseconds = checked(totalPassGpuMicroseconds + timing.GpuMicroseconds);
            }
            // Graphics and compute pass query pools measure durations independently. When work is
            // actually concurrent, subtract the deliberately labelled overlap estimate rather
            // than treating the sum of both queues as elapsed frame time.
            long frameGpuMicroseconds = plan.ContainsAsyncCompute
                ? Math.Max(0, totalPassGpuMicroseconds - EstimateAsyncComputeOverlapMicroseconds(plan, timings))
                : totalPassGpuMicroseconds;
            if (frameGpuMicroseconds <= 0)
                return;

            double frameMilliseconds = frameGpuMicroseconds / 1000.0;
            if (!plan.ContainsAsyncCompute)
            {
                foreach ((AsyncComputePath _, AsyncComputeTimingKey key) in timingFrame.Keys)
                    _asyncComputeTimingPolicy.RecordGraphicsOnly(
                        key,
                        frameMilliseconds,
                        timingFrame.CpuSubmitMicroseconds / 1000.0);
                return;
            }

            foreach (AsyncComputePathRuntimeStatus path in plan.Paths)
            {
                if (!path.Active || !timingFrame.Keys.TryGetValue(path.Path, out AsyncComputeTimingKey key))
                    continue;

                long dispatchMicroseconds = 0;
                foreach (string passName in path.Passes)
                    dispatchMicroseconds = checked(dispatchMicroseconds + timings.GetGpuMicrosecondsOrZero(passName));

                _asyncComputeTimingPolicy.RecordAsync(
                    key,
                    frameMilliseconds,
                    dispatchMicroseconds / 1000.0,
                    transferBarrierMilliseconds: timingFrame.CpuBarrierRecordMicroseconds / 1000.0,
                    graphicsWaitMilliseconds: EstimateAsyncComputeFirstConsumerWaitMicroseconds(plan, timings) / 1000.0,
                    cpuSubmitMilliseconds: timingFrame.CpuSubmitMicroseconds / 1000.0);
            }
        }

        private static bool IsDdgiAsyncComputeActuallyEnabled(AsyncComputePlan plan)
        {
            return IsDdgiAsyncComputeActuallyEnabled(plan.SubmissionPlan);
        }

        private static bool IsDdgiAsyncComputeActuallyEnabled(AsyncComputeSubmissionPlan plan) =>
            plan.Accepted &&
            plan.ContainsAsyncCompute &&
            plan.Paths.Any(path => path.Active && path.Path is AsyncComputePath.SimpleDdgiUpdate or AsyncComputePath.FullDdgiUpdate);

        private sealed record AsyncComputePlan(
            AsyncComputeMode RequestedMode,
            AsyncComputeMode EffectiveMode,
            bool Supported,
            AsyncComputeSubmissionPlan SubmissionPlan,
            RenderGraphDiagnostics GraphDiagnostics,
            string Status)
        {
            public bool Requested => RequestedMode != AsyncComputeMode.Disabled;
            public bool Enabled => SubmissionPlan.ContainsAsyncCompute;
            public IReadOnlyList<string> CandidatePasses => SubmissionPlan.Paths
                .Where(path => path.Requested &&
                               path.Supported &&
                               path.Status is not AsyncComputePathStatus.MissingResourcePlan and not AsyncComputePathStatus.ValidationFallback)
                .SelectMany(path => path.Passes)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            public IReadOnlyList<string> EnabledPasses => SubmissionPlan.ActivePasses;
            public int QueueOwnershipTransitionCount => SubmissionPlan.QueueFamilyOwnershipTransferCount;
        }

        private UploadBudgetSnapshot BuildUploadBudgetSnapshot(SceneRenderingData sceneData, RenderBudgetProfile profile)
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
            _uploadBudgetTracker.AddBytes(UploadBudgetCategory.Reflections, (ulong)Math.Max(0, sceneData.ReflectionProbeCount) * 0UL);
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
                AddMemoryEntry(entries, ref totalBytes, entry.Category, entry.Bytes, entry.AllocationCount, entry.Description);

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
                _environmentManager == null ? 0 : 4,
                "Environment cubemaps and BRDF LUT");
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

        private static RenderBudgetStatus FindMetricStatus(RenderBudgetSnapshot snapshot, string metricName)
        {
            foreach (BudgetMetric metric in snapshot.Metrics)
            {
                if (string.Equals(metric.Name, metricName, StringComparison.Ordinal))
                    return metric.Status;
            }

            return RenderBudgetStatus.Unknown;
        }

        private static ulong GetMemoryCategoryBytes(MemoryBudgetSnapshot snapshot, MemoryBudgetCategory category)
        {
            foreach (MemoryBudgetEntry entry in snapshot.Entries)
            {
                if (entry.Category == category)
                    return entry.Bytes;
            }

            return 0;
        }

        private static long CalculateGpuFrameMicroseconds(SceneRenderingData sceneData)
        {
            return sceneData.GpuDepthPrePassMicroseconds +
                sceneData.GpuDirectionalShadowMicroseconds +
                sceneData.GpuSpotShadowMicroseconds +
                sceneData.GpuPointShadowMicroseconds +
                sceneData.GpuHiZBuildMicroseconds +
                sceneData.GpuMotionVectorMicroseconds +
                sceneData.GpuAmbientOcclusionMicroseconds +
                sceneData.GpuAmbientOcclusionBlurMicroseconds +
                sceneData.GpuAccelerationStructureBlasMicroseconds +
                sceneData.GpuAccelerationStructureTlasMicroseconds +
                sceneData.GpuSsgiTraceMicroseconds +
                sceneData.GpuSsgiTemporalMicroseconds +
                sceneData.GpuSsgiDenoiseMicroseconds +
                sceneData.GpuDdgiUpdateMicroseconds +
                sceneData.GpuGiCompositeMicroseconds +
                sceneData.GpuLightCullMicroseconds +
                sceneData.GpuForwardOpaqueMicroseconds +
                sceneData.GpuTransparentMicroseconds +
                sceneData.GpuParticleMicroseconds +
                sceneData.GpuTrailBeamMicroseconds +
                sceneData.GpuFogMicroseconds +
                sceneData.GpuAutoExposureMicroseconds +
                sceneData.GpuAntiAliasingMicroseconds +
                sceneData.GpuBloomExtractMicroseconds +
                sceneData.GpuBloomDownsampleMicroseconds +
                sceneData.GpuBloomUpsampleMicroseconds +
                sceneData.GpuCompositeMicroseconds +
                sceneData.GpuSkinningMicroseconds +
                sceneData.GpuReflectionProbeCaptureMicroseconds +
                sceneData.GpuReflectionProbePrefilterMicroseconds;
        }

        private static (uint Width, uint Height) CalculateSsgiExtent(uint width, uint height, float resolutionScale, bool enabled)
        {
            if (!enabled || width == 0 || height == 0)
                return (0, 0);

            float scale = resolutionScale <= 0.375f ? 0.25f : resolutionScale <= 0.75f ? 0.5f : 1.0f;
            uint scaledWidth = Math.Max(1u, (uint)Math.Ceiling(width * scale));
            uint scaledHeight = Math.Max(1u, (uint)Math.Ceiling(height * scale));
            return (scaledWidth, scaledHeight);
        }

        internal static GlobalIlluminationMode ResolveEffectiveGlobalIlluminationMode(
            GlobalIlluminationSettings settings,
            bool rayQuerySupported)
        {
            if (!settings.Enabled)
                return GlobalIlluminationMode.Disabled;
            if (settings.Mode == GlobalIlluminationMode.RayQueryHybrid && !rayQuerySupported)
                return GlobalIlluminationMode.Hybrid;
            return settings.Mode;
        }

        private static int ResolveSsgiRayCount(RenderQualityPreset qualityPreset, bool enabled)
        {
            if (!enabled)
                return 0;

            return qualityPreset switch
            {
                RenderQualityPreset.Medium => 4,
                RenderQualityPreset.Ultra => 8,
                RenderQualityPreset.Low => 0,
                _ => 6
            };
        }

        private static ulong EstimateGlobalIlluminationRenderTargetBytes(uint width, uint height, float resolutionScale, bool ssgiEnabled)
        {
            if (!ssgiEnabled || width == 0 || height == 0)
                return 0;

            (uint ssgiWidth, uint ssgiHeight) = CalculateSsgiExtent(width, height, resolutionScale, enabled: true);
            ulong ssgiPixels = (ulong)ssgiWidth * ssgiHeight;
            ulong fullResolutionPixels = (ulong)width * height;
            const ulong rgba16FloatBytesPerPixel = 8;
            const ulong r32FloatBytesPerPixel = 4;
            const ulong rg16FloatBytesPerPixel = 4;
            const ulong r16FloatBytesPerPixel = 2;
            const ulong ssgiColorTargetCount = 4;
            const ulong ssgiHitDistanceTargetCount = 1;
            const ulong ssgiDepthHistoryTargetCount = 2;
            const ulong ssgiNormalHistoryTargetCount = 2;
            const ulong ssgiMomentTargetCount = 2;
            const ulong ssgiHistoryLengthTargetCount = 2;
            const ulong finalDiffuseTargetCount = 1;
            return checked(
                (ssgiPixels * rgba16FloatBytesPerPixel * ssgiColorTargetCount) +
                (ssgiPixels * r16FloatBytesPerPixel * ssgiHitDistanceTargetCount) +
                (ssgiPixels * r32FloatBytesPerPixel * ssgiDepthHistoryTargetCount) +
                (ssgiPixels * rgba16FloatBytesPerPixel * ssgiNormalHistoryTargetCount) +
                (ssgiPixels * rg16FloatBytesPerPixel * ssgiMomentTargetCount) +
                (ssgiPixels * r16FloatBytesPerPixel * ssgiHistoryLengthTargetCount) +
                (fullResolutionPixels * rgba16FloatBytesPerPixel * finalDiffuseTargetCount));
        }

        private string BuildGpuTimingReason()
        {
            if (!_gpuTimestamps.Supported)
                return _gpuTimestamps.UnsupportedReason;

            if (!Settings.Debug.AllowGpuTiming)
                return "GPU timing is disabled. Enable RenderSettings.Debug.AllowGpuTiming or press Ctrl+F4 in the sample.";

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
            return HasCompletedGpuTiming(timings, "DdgiSchedulePass") ||
                HasCompletedGpuTiming(timings, "DdgiTracePass") ||
                HasCompletedGpuTiming(timings, "DdgiBlendPass") ||
                HasCompletedGpuTiming(timings, "DdgiRelocateClassifyPass") ||
                HasCompletedGpuTiming(timings, "DdgiPublishPass") ||
                HasCompletedSimpleDdgiGpuTiming(timings);
        }

        private static bool HasCompletedSimpleDdgiGpuTiming(FrameTimingSnapshot timings)
        {
            return HasCompletedGpuTiming(timings, "SimpleDdgiTracePass") ||
                HasCompletedGpuTiming(timings, "SimpleDdgiTransportPass") ||
                HasCompletedGpuTiming(timings, "SimpleDdgiBlendPass") ||
                HasCompletedGpuTiming(timings, "SimpleDdgiRelocateClassifyPass");
        }

        private static bool HasCompletedGpuTiming(FrameTimingSnapshot timings, string passName)
        {
            return timings.TryGetPass(passName, out PassTiming timing) && timing.GpuAvailable;
        }

        private static void ApplyCompletedGpuTimings(SceneRenderingData sceneData, FrameTimingSnapshot timings)
        {
            sceneData.GpuSkinningMicroseconds = timings.GetGpuMicrosecondsOrZero("SkinningPass");
            sceneData.GpuDirectionalShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("DirectionalShadowPass");
            sceneData.GpuSpotShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("SpotShadowPass");
            sceneData.GpuPointShadowMicroseconds = timings.GetGpuMicrosecondsOrZero("PointShadowPass");
            sceneData.GpuDepthPrePassMicroseconds = timings.GetGpuMicrosecondsOrZero("DepthPrePass");
            sceneData.GpuMotionVectorMicroseconds = timings.GetGpuMicrosecondsOrZero("MotionVectorPass");
            sceneData.GpuHiZBuildMicroseconds = timings.GetGpuMicrosecondsOrZero("HiZBuildPass");
            sceneData.GpuAmbientOcclusionMicroseconds = timings.GetGpuMicrosecondsOrZero("AmbientOcclusionPass");
            sceneData.GpuAmbientOcclusionBlurMicroseconds = timings.GetGpuMicrosecondsOrZero("AmbientOcclusionBlurPass");
            sceneData.GpuAccelerationStructureBlasMicroseconds = timings.GetGpuMicrosecondsOrZero("AccelerationStructureBlasPass");
            sceneData.GpuAccelerationStructureTlasMicroseconds = timings.GetGpuMicrosecondsOrZero("AccelerationStructureTlasPass");
            sceneData.GpuSsgiTraceMicroseconds = timings.GetGpuMicrosecondsOrZero("SsgiTracePass");
            sceneData.GpuSsgiTemporalMicroseconds = timings.GetGpuMicrosecondsOrZero("SsgiTemporalPass");
            sceneData.GpuSsgiDenoiseMicroseconds = timings.GetGpuMicrosecondsOrZero("SsgiDenoisePass");
            sceneData.GpuDdgiScheduleMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiSchedulePass");
            sceneData.GpuDdgiScheduleResetMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiScheduleReset");
            sceneData.GpuDdgiScheduleScoreMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiScheduleScore");
            sceneData.GpuDdgiSchedulePrefixMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiSchedulePrefix");
            sceneData.GpuDdgiScheduleCompactMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiScheduleCompact");
            sceneData.GpuDdgiScheduleFinalizeMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiScheduleFinalize");
            sceneData.GpuDdgiScheduleReadbackMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiScheduleReadback");
            sceneData.GpuDdgiScheduleBarrierMicroseconds =
                timings.GetGpuMicrosecondsOrZero("DdgiScheduleBarrierReset") +
                timings.GetGpuMicrosecondsOrZero("DdgiScheduleBarrierScore") +
                timings.GetGpuMicrosecondsOrZero("DdgiScheduleBarrierPrefix") +
                timings.GetGpuMicrosecondsOrZero("DdgiScheduleBarrierCompact") +
                timings.GetGpuMicrosecondsOrZero("DdgiScheduleTraceBarrier");
            sceneData.GpuDdgiTraceMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiTracePass");
            sceneData.GpuDdgiBlendMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiBlendPass");
            sceneData.GpuDdgiRelocateClassifyMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiRelocateClassifyPass");
            sceneData.GpuDdgiPublishMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiPublishPass");
            sceneData.GpuSimpleDdgiTraceMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiTracePass");
            sceneData.GpuSimpleDdgiTransportMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiTransportPass");
            sceneData.GpuSimpleDdgiBlendMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiBlendPass");
            sceneData.GpuFarFieldUpdateMicroseconds = timings.GetGpuMicrosecondsOrZero("FarFieldClipmapBakePass");
            sceneData.GpuFarFieldUpdateTimingValid = HasCompletedGpuTiming(timings, "FarFieldClipmapBakePass") ? 1 : 0;
            long gpuSimpleDdgiRelocateClassifyMicroseconds = timings.GetGpuMicrosecondsOrZero("SimpleDdgiRelocateClassifyPass");
            if (sceneData.GpuSimpleDdgiTraceMicroseconds > 0 ||
                gpuSimpleDdgiRelocateClassifyMicroseconds > 0 ||
                sceneData.GpuSimpleDdgiTransportMicroseconds > 0 ||
                sceneData.GpuSimpleDdgiBlendMicroseconds > 0)
            {
                sceneData.GpuDdgiScheduleMicroseconds = 0;
                sceneData.GpuDdgiScheduleResetMicroseconds = 0;
                sceneData.GpuDdgiScheduleScoreMicroseconds = 0;
                sceneData.GpuDdgiSchedulePrefixMicroseconds = 0;
                sceneData.GpuDdgiScheduleCompactMicroseconds = 0;
                sceneData.GpuDdgiScheduleFinalizeMicroseconds = 0;
                sceneData.GpuDdgiScheduleReadbackMicroseconds = 0;
                sceneData.GpuDdgiScheduleBarrierMicroseconds = 0;
                sceneData.GpuDdgiTraceMicroseconds = sceneData.GpuSimpleDdgiTraceMicroseconds;
                sceneData.GpuDdgiBlendMicroseconds = sceneData.GpuSimpleDdgiTransportMicroseconds +
                    sceneData.GpuSimpleDdgiBlendMicroseconds;
                sceneData.GpuDdgiRelocateClassifyMicroseconds = gpuSimpleDdgiRelocateClassifyMicroseconds;
                sceneData.GpuDdgiPublishMicroseconds = 0;
            }
            sceneData.GpuDdgiUpdateMicroseconds =
                sceneData.GpuDdgiScheduleMicroseconds +
                sceneData.GpuDdgiTraceMicroseconds +
                sceneData.GpuDdgiBlendMicroseconds +
                sceneData.GpuDdgiRelocateClassifyMicroseconds +
                sceneData.GpuDdgiPublishMicroseconds;
            sceneData.GpuGiCompositeMicroseconds = timings.GetGpuMicrosecondsOrZero("SsgiCompositePass");
            sceneData.GpuLightCullMicroseconds = timings.GetGpuMicrosecondsOrZero("TiledLightCullingPass");
            sceneData.GpuFoliageCullMicroseconds = timings.GetGpuMicrosecondsOrZero("FoliageCullPass");
            sceneData.GpuFoliageShadowMicroseconds = sceneData.FoliageCastShadows && sceneData.FoliageClusterCount > 0
                ? sceneData.GpuDirectionalShadowMicroseconds
                : 0;
            sceneData.GpuForwardOpaqueMicroseconds = timings.GetGpuMicrosecondsOrZero("ForwardPlusPass");
            sceneData.GpuForwardGiGatherMicroseconds = timings.GetGpuMicrosecondsOrZero("ForwardGiGatherPass");
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
            sceneData.GpuFogMicroseconds = timings.GetGpuMicrosecondsOrZero("FogPass");
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

        private static long EstimateAsyncComputeQueueBusyMicroseconds(
            AsyncComputeSubmissionPlan plan,
            FrameTimingSnapshot timings)
        {
            if (plan == null || !plan.ContainsAsyncCompute)
                return 0;

            long total = 0;
            foreach (AsyncComputeSubmissionSegment segment in plan.Segments)
            {
                if (segment.Queue != AsyncComputeQueue.Compute)
                    continue;
                foreach (string pass in segment.Passes)
                    total = checked(total + timings.GetGpuMicrosecondsOrZero(pass));
            }

            return total;
        }

        /// <summary>
        /// Timestamp query pools provide pass durations rather than queue-global wall-clock
        /// intervals, so overlap is intentionally an estimate. For each compute segment we use
        /// the immediately following graphics segment as its useful overlap window. This remains
        /// meaningful for diagnostics and Auto policy without claiming an unmeasured queue span.
        /// </summary>
        private static long EstimateAsyncComputeOverlapMicroseconds(
            AsyncComputeSubmissionPlan plan,
            FrameTimingSnapshot timings)
        {
            if (plan == null || !plan.ContainsAsyncCompute)
                return 0;

            long totalOverlap = 0;
            for (int segmentIndex = 0; segmentIndex < plan.Segments.Count; segmentIndex++)
            {
                AsyncComputeSubmissionSegment compute = plan.Segments[segmentIndex];
                if (compute.Queue != AsyncComputeQueue.Compute)
                    continue;

                long computeBusy = SumGpuPassMicroseconds(compute.Passes, timings);
                long graphicsWindow = 0;
                for (int next = segmentIndex + 1; next < plan.Segments.Count; next++)
                {
                    AsyncComputeSubmissionSegment candidate = plan.Segments[next];
                    if (candidate.Queue != AsyncComputeQueue.Graphics)
                        continue;
                    graphicsWindow = SumGpuPassMicroseconds(candidate.Passes, timings);
                    break;
                }

                totalOverlap = checked(totalOverlap + Math.Min(computeBusy, graphicsWindow));
            }

            return Math.Max(0, totalOverlap);
        }

        private static long EstimateAsyncComputeFirstConsumerWaitMicroseconds(
            AsyncComputeSubmissionPlan plan,
            FrameTimingSnapshot timings)
        {
            if (plan == null || !plan.ContainsAsyncCompute)
                return 0;

            long totalWait = 0;
            for (int segmentIndex = 0; segmentIndex < plan.Segments.Count; segmentIndex++)
            {
                AsyncComputeSubmissionSegment compute = plan.Segments[segmentIndex];
                if (compute.Queue != AsyncComputeQueue.Compute)
                    continue;

                long computeBusy = SumGpuPassMicroseconds(compute.Passes, timings);
                long graphicsWindow = 0;
                for (int next = segmentIndex + 1; next < plan.Segments.Count; next++)
                {
                    AsyncComputeSubmissionSegment candidate = plan.Segments[next];
                    if (candidate.Queue != AsyncComputeQueue.Graphics)
                        continue;
                    graphicsWindow = SumGpuPassMicroseconds(candidate.Passes, timings);
                    break;
                }

                totalWait = checked(totalWait + Math.Max(0, computeBusy - graphicsWindow));
            }

            return totalWait;
        }

        private static long SumGpuPassMicroseconds(
            IReadOnlyList<string> passNames,
            FrameTimingSnapshot timings)
        {
            long total = 0;
            foreach (string passName in passNames)
                total = checked(total + timings.GetGpuMicrosecondsOrZero(passName));
            return total;
        }

        private static bool ForwardOcclusionCountersReconcile(SceneRenderingData sceneData)
        {
            if (sceneData.SceneSubmissionGpuCompactionActive &&
                sceneData.SceneSubmissionFallbackReason.Length == 0 &&
                sceneData.ForwardOcclusionTestedMeshletsGpu > 0)
            {
                return sceneData.ForwardOcclusionCulledMeshletsGpu <= sceneData.ForwardOcclusionTestedMeshletsGpu;
            }

            if (sceneData.ForwardTaskInvocations <= 0)
                return true;

            if (!sceneData.OcclusionCullingEnabled || sceneData.HiZMipCount == 0)
                return sceneData.ForwardOcclusionTestedMeshletsGpu == 0 &&
                    sceneData.ForwardOcclusionCulledMeshletsGpu == 0;

            int visibleAfterFrustum = Math.Max(0, sceneData.ForwardTaskInvocations - sceneData.ForwardFrustumCulledMeshletsGpu);
            return sceneData.ForwardOcclusionTestedMeshletsGpu == visibleAfterFrustum &&
                sceneData.ForwardOcclusionCulledMeshletsGpu + sceneData.ForwardEmittedMeshletsGpu == sceneData.ForwardOcclusionTestedMeshletsGpu;
        }

        private static string BuildForwardOcclusionSanity(
            SceneRenderingData sceneData,
            bool gpuMeshletCountersEnabled,
            bool reconciled)
        {
            if (!gpuMeshletCountersEnabled)
                return "GPU meshlet counters disabled.";

            if (sceneData.SceneSubmissionGpuCompactionActive &&
                sceneData.SceneSubmissionFallbackReason.Length == 0 &&
                sceneData.ForwardOcclusionTestedMeshletsGpu > 0)
            {
                return reconciled
                    ? "Scene submission Hi-Z occlusion counters reconcile: rejected is within tested."
                    : "Scene submission Hi-Z occlusion counters do not reconcile.";
            }

            if (sceneData.ForwardTaskInvocations <= 0)
                return "No completed forward GPU counters are available yet.";

            if (!sceneData.OcclusionCullingEnabled || sceneData.HiZMipCount == 0)
            {
                return reconciled
                    ? "Hi-Z occlusion disabled; tested and rejected counters are zero."
                    : "Hi-Z occlusion disabled, but tested or rejected counters are non-zero.";
            }

            if (reconciled)
                return "Forward occlusion counters reconcile: emitted plus rejected equals tested.";

            return "Forward occlusion counters do not reconcile; inspect shader diagnostics and frame latency.";
        }

        private void PrepareReflectionProbes(Scene scene, SceneRenderingData sceneData)
        {
            if (_reflectionProbeManager == null)
                return;

            _reflectionProbeManager.Upload(scene.ReflectionProbes, _stagingRing, _currentCommandBuffer);
            // Upload may allocate or recreate the cubemap array. Register only when the
            // manager marks its descriptor dirty; this keeps the global environment bound as
            // the explicit fallback until a local array is available.
            _reflectionProbeManager.Register(_bindlessHeap);

            ReflectionSettings settings = Settings.Reflections;
            sceneData.ReflectionsEnabled = settings.Enabled && settings.Mode != ReflectionMode.Disabled;
            sceneData.ReflectionMode = settings.Mode;
            sceneData.ReflectionDebugView = settings.DebugView;
            sceneData.ReflectionProbeCount = _reflectionProbeManager.ActiveProbeCount;
            sceneData.ReflectionProbeCapacity = _reflectionProbeManager.ProbeCapacity;
            sceneData.MaxReflectionProbesPerPixel = settings.MaxProbesPerPixel;
            sceneData.ReflectionProbeResolution = _reflectionProbeManager.ProbeResolution;
            sceneData.ReflectionProbeMipCount = _reflectionProbeManager.ProbeMipCount;
            sceneData.ReflectionProbeEstimatedBytes = _reflectionProbeManager.EstimatedBytes;
            sceneData.ReflectionProbeCapturesQueued = _reflectionProbeManager.CapturesQueued;
            sceneData.ReflectionProbeCapturesCompleted = _reflectionProbeManager.CapturesCompleted;
            sceneData.CpuReflectionProbeUploadMicroseconds = _reflectionProbeManager.LastUploadMicroseconds;
        }

        private DdgiGatherTileManager.DdgiGatherSupportReadiness ResolveDdgiGatherSupportReadiness()
        {
            if (_ddgiProbeVolumeManager == null)
                return default;

            const float readyFraction = 0.80f;
            float localReadiness = ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastWarmedLocalProbeFraction);
            float cascade0Readiness = ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastWarmedCascade0ProbeFraction);
            float visibleReadiness = ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastWarmedVisibleProbeFraction);
            if (_ddgiProbeVolumeManager.WarmupState == DdgiRuntimeWarmupState.SteadyState &&
                localReadiness >= readyFraction &&
                cascade0Readiness >= readyFraction &&
                visibleReadiness >= readyFraction)
            {
                return DdgiGatherTileManager.DdgiGatherSupportReadiness.Steady;
            }

            float publishedCacheReadiness = _ddgiProbeVolumeManager.PublishedCacheGeneration > 0u ? 0.05f : 0.0f;
            float publishedProbeConfidence = _ddgiProbeVolumeManager.PublishedCacheGeneration > 0u
                ? ResolveDdgiGatherReadinessHint(_ddgiProbeVolumeManager.LastAverageProbeConfidence)
                : 0.0f;
            float clipmapReadiness = Math.Max(
                Math.Min(cascade0Readiness, visibleReadiness),
                Math.Max(publishedProbeConfidence, publishedCacheReadiness));
            return new DdgiGatherTileManager.DdgiGatherSupportReadiness(
                localReadiness,
                clipmapReadiness,
                clipmapReadiness);
        }

        private static float ResolveDdgiGatherReadinessHint(float warmedProbeFraction)
        {
            if (!float.IsFinite(warmedProbeFraction))
                return 0.0f;

            return Math.Clamp(warmedProbeFraction, 0.0f, 1.0f);
        }

        private void PrepareDdgiProbeVolumes(
            Scene scene,
            ICamera camera,
            SceneRenderingData sceneData,
            LightFrameSnapshot lightSnapshot,
            bool cameraCut)
        {
            if (_ddgiProbeVolumeManager == null)
            {
                _lastDdgiFrameLayout = DdgiFrameLayout.Empty;
                _hasLastDdgiCameraPosition = false;
                _hasLastDdgiProjectionMatrix = false;
                PopulateDdgiPassExecutionDiagnostics(sceneData);
                return;
            }

            bool ddgiActive = Settings.GlobalIllumination.EffectiveUseDdgi;
            bool simpleDdgiActive = Settings.GlobalIllumination.EffectiveUseSimpleDdgi &&
                                    _simpleDdgiVolumeManager != null &&
                                    _farFieldClipmapManager != null;
            bool simpleDdgiStructuredGatherAvailable = simpleDdgiActive &&
                                                         Settings.GlobalIllumination.EffectiveUseRayQueryBackend &&
                                                         _context.RayQuerySupported &&
                                                         _accelerationStructureManager?.Active == true;
            bool ddgiRayUpdateActive = ddgiActive &&
                                       Settings.GlobalIllumination.EffectiveUseRayQueryBackend &&
                                       _accelerationStructureManager?.Active == true;
            bool simpleDdgiRayUpdateActive = simpleDdgiActive &&
                                             Settings.GlobalIllumination.EffectiveUseRayQueryBackend &&
                                             _accelerationStructureManager?.Active == true;

            DdgiFrameLayout layout = BuildDdgiFrameLayout(scene, camera, lightSnapshot, sceneData, cameraCut);
            _lastDdgiFrameLayout = layout;
            // Full and simple DDGI have separate state/atlas owners. Do not run
            // the inactive owner's upload path and then overwrite a shared
            // gather table later in the frame; only the selected backend may
            // mutate its resources.
            if (ddgiActive)
            {
                _ddgiProbeVolumeManager.Upload(layout, _stagingRing, _currentCommandBuffer);
                _ddgiGatherTileManager?.Upload(
                    layout,
                    sceneData.ViewProjectionMatrix,
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight,
                    _stagingRing,
                    _currentCommandBuffer,
                    ResolveDdgiGatherSupportReadiness());
            }
            else
            {
                // Control headers are the only inactive-owner writes. They are
                // required once at startup and on a backend transition so forward
                // shading can never observe a stale legacy enabled bit.
                _ddgiProbeVolumeManager.EnsureDisabled(_stagingRing, _currentCommandBuffer);
            }

            // Build and upload the current emissive payload before calculating
            // the simple-DDGI dirty signature. This makes the revision consumed
            // by scheduling describe the data traced in this same frame.
            UploadDdgiEmissiveSources(scene, sceneData, ddgiRayUpdateActive || simpleDdgiRayUpdateActive);
            if (simpleDdgiActive)
            {
                _farFieldClipmapManager!.Upload(
                    scene,
                    camera.Position,
                    _stagingRing,
                    _currentCommandBuffer,
                    sceneData.SceneContentRevision);
                sceneData.FarFieldPagedMode = _farFieldClipmapManager.PagedMode ? 1 : 0;
                sceneData.FarFieldPagePoolCapacity = _farFieldClipmapManager.PagePoolCapacity;
                sceneData.FarFieldResidentPageCount = _farFieldClipmapManager.ResidentPageCount;
                sceneData.FarFieldPendingPageCount = _farFieldClipmapManager.PendingPageCount;
                sceneData.FarFieldPageRequestCount = _farFieldClipmapManager.PageRequestCount;
                sceneData.FarFieldPageMissCount = _farFieldClipmapManager.PageMissCount;
                sceneData.FarFieldPageRebuildCount = _farFieldClipmapManager.PageRebuildCount;
                sceneData.FarFieldPageEvictionCount = _farFieldClipmapManager.PageEvictionsThisFrame;
                sceneData.FarFieldScheduledPageBakeCount = _farFieldClipmapManager.ScheduledPageBakeCount;
                sceneData.FarFieldCacheBytes = _farFieldClipmapManager.PageCacheBytes;
                sceneData.FarFieldMemoryBudgetBytes = Settings.GlobalIllumination.FarFieldMemoryBudgetBytes;
                sceneData.FarFieldInstanceBufferBytes = _farFieldClipmapManager.InstanceBufferBytes;
                sceneData.FarFieldPageTableBytes = _farFieldClipmapManager.PageTableBufferBytes;
            }
            bool farFieldCoverageAvailable = simpleDdgiActive &&
                                             _farFieldClipmapManager!.CoverageReady;
            if (simpleDdgiActive)
            {
                SimpleDdgiDirtySignature simpleDdgiDirtySignature = CreateSimpleDdgiDirtySignature(
                    scene,
                    lightSnapshot,
                    _ddgiEmissiveSourceRevision,
                    farFieldCoverageAvailable);
                _simpleDdgiVolumeManager!.Upload(
                    scene,
                    camera.Position,
                    _stagingRing,
                    _currentCommandBuffer,
                    _currentFrame,
                    simpleDdgiDirtySignature.Signature,
                    simpleDdgiDirtySignature.ReasonFlags,
                    simpleDdgiStructuredGatherAvailable,
                    farFieldCoverageAvailable,
                    layout.DirtyRegions);
                // The gather table has one active producer. In simple mode it is
                // populated directly from the simple volume table; no legacy
                // payload is uploaded first.
                _ddgiGatherTileManager?.UploadSimple(
                    _simpleDdgiVolumeManager!.LastVolumes,
                    sceneData.ViewProjectionMatrix,
                    sceneData.ScreenWidth,
                    sceneData.ScreenHeight,
                    _stagingRing,
                    _currentCommandBuffer);
            }
            else
            {
                // As above, disable only the control header. The inactive simple
                // scheduler, atlases, passes, and gather producer remain dormant.
                _simpleDdgiVolumeManager?.EnsureDisabled(_stagingRing, _currentCommandBuffer);
            }
            bool ddgiCompareMode = Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare;
            bool gpuSchedulerRequested = Settings.GlobalIllumination.DdgiSchedulerMode != DdgiSchedulerMode.CpuReference;
            bool gpuSchedulerQueueRequested = Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.Gpu ||
                                              (ddgiCompareMode && Settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering);
            int frameRingIndex = _currentFrame;
            ulong frameSerial = sceneData.DdgiFrameSerial;
            if (ddgiActive)
                AdvanceDdgiGpuSchedulerFallbackRetry(ddgiRayUpdateActive);
            string gpuSchedulerFallbackReason = ddgiActive
                ? ResolveDdgiGpuSchedulerFallbackReason(
                    ddgiRayUpdateActive,
                    ddgiCompareMode,
                    Settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering)
                : string.Empty;
            bool gpuSchedulerActive = ddgiRayUpdateActive &&
                                       gpuSchedulerQueueRequested &&
                                       string.IsNullOrEmpty(gpuSchedulerFallbackReason);
            int scheduledProbeUpdates = 0;
            if (!ddgiActive)
            {
                _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame(frameRingIndex);
            }
            else if (ddgiCompareMode && ddgiRayUpdateActive)
            {
                int cpuReferenceScheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(
                    enabled: true,
                    layout,
                    frameSerial);
                _ddgiProbeVolumeManager.CaptureGpuSchedulerValidationExpectedFrame(frameRingIndex, cpuReferenceScheduledProbeUpdates);
                if (gpuSchedulerActive)
                {
                    try
                    {
                        scheduledProbeUpdates = _ddgiProbeVolumeManager.PrepareGpuScheduleInputs(
                            layout,
                            sceneData,
                            _stagingRing,
                            _currentCommandBuffer,
                            preserveCpuSchedulerDiagnostics: true);
                    }
                    catch (Exception ex)
                    {
                        gpuSchedulerFallbackReason = LatchDdgiGpuSchedulerFallback($"gpu-scheduler-input-prep-failed:{ex.GetType().Name}");
                        gpuSchedulerActive = false;
                        scheduledProbeUpdates = cpuReferenceScheduledProbeUpdates;
                        _ddgiProbeVolumeManager.UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);
                    }
                }
                else
                {
                    scheduledProbeUpdates = cpuReferenceScheduledProbeUpdates;
                    _ddgiProbeVolumeManager.UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);
                }
            }
            else if (gpuSchedulerActive)
            {
                try
                {
                    scheduledProbeUpdates = _ddgiProbeVolumeManager.PrepareGpuScheduleInputs(
                        layout,
                        sceneData,
                        _stagingRing,
                        _currentCommandBuffer);
                }
                catch (Exception ex)
                {
                    gpuSchedulerFallbackReason = LatchDdgiGpuSchedulerFallback($"gpu-scheduler-input-prep-failed:{ex.GetType().Name}");
                    gpuSchedulerActive = false;
                    _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame(frameRingIndex);
                    scheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(ddgiRayUpdateActive, layout, frameSerial);
                    _ddgiProbeVolumeManager.UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);
                }
            }
            else
            {
                _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame(frameRingIndex);
                scheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(ddgiRayUpdateActive, layout, frameSerial);
                _ddgiProbeVolumeManager.UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);
            }

            sceneData.DdgiProbeVolumeCount = ddgiActive ? _ddgiProbeVolumeManager.VolumeCount : 0;
            sceneData.DdgiProbeCount = ddgiActive ? _ddgiProbeVolumeManager.ProbeCount : 0;
            sceneData.DdgiActiveProbeCount = ddgiActive ? _ddgiProbeVolumeManager.ActiveProbeCount : 0;
            sceneData.DdgiRaysPerProbe = ddgiActive ? _ddgiProbeVolumeManager.RaysPerProbe : 0;
            sceneData.DdgiProbesUpdated = ddgiActive ? scheduledProbeUpdates : 0;
            sceneData.DdgiMaxActiveProbeBudget = ddgiActive ? GlobalIlluminationProbeVolumeData.CalculateActiveProbeBudget(Settings.GlobalIllumination) : 0;
            sceneData.DdgiMaxProbeUpdatesPerFrame = ddgiActive ? _ddgiProbeVolumeManager.MaxProbeUpdatesPerFrame : 0;
            sceneData.DdgiProbeUpdateRequestBudget = ddgiActive ? _ddgiProbeVolumeManager.LastProbeUpdateRequestBudget : 0;
            sceneData.DdgiProbeUpdatePrimaryRayBudget = ddgiActive ? _ddgiProbeVolumeManager.LastProbeUpdatePrimaryRayBudget : 0;
            sceneData.DdgiScheduledRequestBudget = sceneData.DdgiProbeUpdateRequestBudget;
            sceneData.DdgiScheduledPrimaryRayBudget = sceneData.DdgiProbeUpdatePrimaryRayBudget;
            sceneData.DdgiGpuSchedulerPredictedRequestUpperBound = ddgiActive && gpuSchedulerActive ? scheduledProbeUpdates : 0;
            sceneData.DdgiGatherTileCount = ddgiActive ? _ddgiGatherTileManager?.LastTileCount ?? 0 : 0;
            sceneData.DdgiGatherTileCountX = ddgiActive ? _ddgiGatherTileManager?.LastTileCountX ?? 0 : 0;
            sceneData.DdgiGatherTileCountY = ddgiActive ? _ddgiGatherTileManager?.LastTileCountY ?? 0 : 0;
            sceneData.DdgiGatherSelectedLocalTileCount = ddgiActive ? _ddgiGatherTileManager?.LastSelectedLocalTileCount ?? 0 : 0;
            sceneData.DdgiGatherSelectedClipmapTileCount = ddgiActive ? _ddgiGatherTileManager?.LastSelectedClipmapTileCount ?? 0 : 0;
            int ddgiGatherFallbackTileCount = ddgiActive ? _ddgiGatherTileManager?.LastFallbackTileCount ?? 0 : 0;
            bool ddgiExhaustiveGatherFallbackEnabled = ddgiActive && Settings.GlobalIllumination.DdgiExhaustiveGatherFallbackEnabled;
            sceneData.DdgiGatherFallbackTileCount = ddgiGatherFallbackTileCount;
            sceneData.DdgiForwardGatherTileEmpty = ddgiGatherFallbackTileCount;
            sceneData.DdgiForwardGatherFallbackUsed = ddgiExhaustiveGatherFallbackEnabled ? ddgiGatherFallbackTileCount : 0;
            sceneData.DdgiForwardGatherFallbackDisabled = ddgiExhaustiveGatherFallbackEnabled ? 0 : ddgiGatherFallbackTileCount;
            sceneData.DdgiQualityTier = ddgiActive ? Settings.GlobalIllumination.DdgiQualityTier : DdgiQualityTier.DdgiHigh;
            sceneData.DdgiAdaptiveBudgetScale = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetScale : 1.0f;
            sceneData.DdgiAdaptiveBudgetReduced = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetReduced : 0;
            sceneData.DdgiEmergencyDegradeActive = ddgiActive ? _ddgiProbeVolumeManager.LastEmergencyDegradeActive : 0;
            sceneData.DdgiEffectiveMaxShadedLights = ddgiActive ? _ddgiProbeVolumeManager.LastEffectiveMaxShadedLights : 0;
            sceneData.DdgiAdaptiveBudgetReason = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetReason : string.Empty;
            // The graph scheduler owns the runtime async state.  Configuration only expresses
            // a request; complete concrete bindings and an accepted submission plan are also
            // required before this becomes true.
            sceneData.DdgiAsyncComputeEnabled = 0;
            sceneData.DdgiAtlasMemoryBudgetBytes = ddgiActive ? Settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes : 0UL;
            sceneData.DdgiProbeRelocationCount = ddgiRayUpdateActive && Settings.GlobalIllumination.DdgiProbeRelocationEnabled ? scheduledProbeUpdates : 0;
            sceneData.DdgiProbeClassificationCount = ddgiRayUpdateActive && Settings.GlobalIllumination.DdgiProbeClassificationEnabled ? scheduledProbeUpdates : 0;
            PopulateDdgiLightSelectionMetadata(
                sceneData,
                lightSnapshot,
                ddgiRayUpdateActive || simpleDdgiRayUpdateActive);
            PopulateDdgiDiagnostics(sceneData, layout, ddgiActive);
            PopulateDdgiCoverageDiagnostics(sceneData);
            sceneData.DdgiTextureBytes = ddgiActive ? _ddgiProbeVolumeManager.TextureBytes : 0;
            sceneData.DdgiBufferBytes = ddgiActive
                ? _ddgiProbeVolumeManager.BufferBytes + (_ddgiGatherTileManager?.CurrentBufferBytes ?? 0UL)
                : 0;
            if (simpleDdgiActive)
            {
                sceneData.DdgiBufferBytes =
                    (_simpleDdgiVolumeManager?.BufferBytes ?? 0UL) +
                    (_ddgiGatherTileManager?.CurrentBufferBytes ?? 0UL);
            }
            sceneData.DdgiProbeVolumeBufferBytes = ddgiActive ? _ddgiProbeVolumeManager.ProbeVolumeBufferBytes : 0UL;
            sceneData.DdgiProbeStateBufferBytes = ddgiActive ? _ddgiProbeVolumeManager.ProbeStateBufferBytes : 0UL;
            sceneData.DdgiProbeUpdateQueueBytes = ddgiActive ? _ddgiProbeVolumeManager.ProbeUpdateQueueBytes : 0UL;
            sceneData.DdgiProbeRelocationClassificationBytes = ddgiActive ? _ddgiProbeVolumeManager.ProbeRelocationClassificationBytes : 0UL;
            sceneData.DdgiCurrentIrradianceAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.CurrentIrradianceAtlasBytes : 0UL;
            sceneData.DdgiCurrentVisibilityAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.CurrentVisibilityAtlasBytes : 0UL;
            sceneData.DdgiGatherTileBufferBytes = ddgiActive ? (_ddgiGatherTileManager?.CurrentBufferBytes ?? 0UL) : 0UL;
            if (simpleDdgiActive)
                sceneData.DdgiGatherTileBufferBytes = _ddgiGatherTileManager?.CurrentBufferBytes ?? 0UL;
            sceneData.DdgiLocalSlotReservedPoolBytes = ddgiActive ? _ddgiProbeVolumeManager.LocalSlotReservedPoolBytes : 0UL;
            sceneData.DdgiGpuSchedulerBufferBytes = ddgiActive ? _ddgiProbeVolumeManager.GpuSchedulerBufferBytes : 0UL;
            sceneData.DdgiGpuSchedulerDirtyRegionCapacity = ddgiActive ? _ddgiProbeVolumeManager.GpuSchedulerDirtyRegionCapacity : 0;
            sceneData.DdgiGpuSchedulerCandidateCapacity = ddgiActive ? _ddgiProbeVolumeManager.GpuSchedulerCandidateCapacity : 0;
            sceneData.DdgiGpuSchedulerGroupCountCapacity = ddgiActive ? _ddgiProbeVolumeManager.GpuSchedulerGroupCountCapacity : 0;
            sceneData.DdgiGpuSchedulerPrefixCapacity = ddgiActive ? _ddgiProbeVolumeManager.GpuSchedulerPrefixCapacity : 0;
            sceneData.DdgiGpuSchedulerDirtyRegionCount = ddgiActive ? _ddgiProbeVolumeManager.LastGpuSchedulerDirtyRegionCount : 0;
            sceneData.DdgiGpuSchedulerDirtyRegionOverflowCount = ddgiActive ? _ddgiProbeVolumeManager.LastGpuSchedulerDirtyRegionOverflowCount : 0;
            sceneData.DdgiGpuSchedulerResourceReinitializationCount = ddgiActive ? _ddgiProbeVolumeManager.LastGpuSchedulerResourceReinitializationCount : 0;
            sceneData.DdgiGpuSchedulerTotalResourceReinitializationCount = ddgiActive ? _ddgiProbeVolumeManager.TotalGpuSchedulerResourceReinitializationCount : 0;
            sceneData.DdgiGpuSchedulerUploadBytes = ddgiActive ? _ddgiProbeVolumeManager.LastGpuSchedulerUploadBytes : 0UL;
            GPUDdgiSchedulerCounters completedSchedulerCounters = _ddgiProbeVolumeManager.LastCompletedGpuSchedulerCounters;
            sceneData.DdgiGpuSchedulerReadbackValid = gpuSchedulerActive ? _ddgiProbeVolumeManager.LastCompletedGpuSchedulerCountersValid : 0;
            sceneData.DdgiGpuSchedulerReadbackLatencyFrames = sceneData.DdgiGpuSchedulerReadbackValid != 0
                ? _ddgiProbeVolumeManager.LastCompletedGpuSchedulerReadbackLatencyFrames
                : 0;
            sceneData.DdgiGpuSchedulerFallbackActive = ddgiActive && gpuSchedulerRequested && !gpuSchedulerActive ? 1 : 0;
            sceneData.DdgiGpuSchedulerFallbackReason = sceneData.DdgiGpuSchedulerFallbackActive != 0
                ? gpuSchedulerFallbackReason
                : string.Empty;
            sceneData.DdgiGpuSchedulerConsideredProbeCount = gpuSchedulerActive ? _ddgiProbeVolumeManager.LastGpuSchedulerScanProbeCount : 0;
            sceneData.DdgiGpuSchedulerRequestCount = gpuSchedulerActive ? completedSchedulerCounters.RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPrimaryRayCount = gpuSchedulerActive ? completedSchedulerCounters.PrimaryRayCount : 0u;
            sceneData.DdgiGpuSchedulerActualRequestCount =
                gpuSchedulerActive && sceneData.DdgiGpuSchedulerReadbackValid != 0 ? completedSchedulerCounters.RequestCount : 0u;
            sceneData.DdgiGpuSchedulerActualPrimaryRayCount =
                gpuSchedulerActive && sceneData.DdgiGpuSchedulerReadbackValid != 0 ? completedSchedulerCounters.PrimaryRayCount : 0u;
            sceneData.DdgiGpuSchedulerCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.CandidateCount : 0u;
            sceneData.DdgiGpuSchedulerOverflowCount = gpuSchedulerActive ? completedSchedulerCounters.OverflowCount : 0u;
            sceneData.DdgiGpuSchedulerCandidateBufferOverflowCount = gpuSchedulerActive ? completedSchedulerCounters.CandidateBufferOverflowCount : 0u;
            sceneData.DdgiGpuSchedulerPerBucketOverflowCount = gpuSchedulerActive ? completedSchedulerCounters.PerBucketOverflowCount : 0u;
            sceneData.DdgiGpuSchedulerDuplicateRequestCount = gpuSchedulerActive ? completedSchedulerCounters.DuplicateRequestCount : 0u;
            sceneData.DdgiGpuSchedulerBudgetRejectedCount = gpuSchedulerActive ? completedSchedulerCounters.BudgetRejectedCount : 0u;
            sceneData.DdgiGpuSchedulerRequestBudgetRejectedCount = gpuSchedulerActive ? completedSchedulerCounters.RequestBudgetRejectedCount : 0u;
            sceneData.DdgiGpuSchedulerPrimaryRayBudgetRejectedCount = gpuSchedulerActive ? completedSchedulerCounters.PrimaryRayBudgetRejectedCount : 0u;
            sceneData.DdgiGpuSchedulerInvalidProbeCount = gpuSchedulerActive ? completedSchedulerCounters.InvalidProbeCount : 0u;
            sceneData.DdgiGpuSchedulerCandidateOutputCapacity = gpuSchedulerActive ? _ddgiProbeVolumeManager.LastGpuSchedulerCandidateOutputCapacity : 0;
            sceneData.DdgiGpuSchedulerFullScan = gpuSchedulerActive ? _ddgiProbeVolumeManager.LastGpuSchedulerFullScan : 0;
            sceneData.DdgiGpuSchedulerVisibleFrustumCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.VisibleFrustumCount : 0u;
            sceneData.DdgiGpuSchedulerSafetyShellCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.SafetyShellCount : 0u;
            sceneData.DdgiGpuSchedulerAgeRefreshCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.AgeRefreshCount : 0u;
            sceneData.DdgiGpuSchedulerHighVarianceCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.HighVarianceCount : 0u;
            sceneData.DdgiGpuSchedulerLowConfidenceCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.LowConfidenceCount : 0u;
            sceneData.DdgiGpuSchedulerStableSkippedCount = gpuSchedulerActive ? completedSchedulerCounters.StableSkippedCount : 0u;
            sceneData.DdgiGpuSchedulerPriority0RequestCount = gpuSchedulerActive ? completedSchedulerCounters.Priority0RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPriority1RequestCount = gpuSchedulerActive ? completedSchedulerCounters.Priority1RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPriority2RequestCount = gpuSchedulerActive ? completedSchedulerCounters.Priority2RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPriority3RequestCount = gpuSchedulerActive ? completedSchedulerCounters.Priority3RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPriorityBucketMismatchSkipCount = gpuSchedulerActive ? completedSchedulerCounters.PriorityBucketMismatchSkipCount : 0u;
            sceneData.DdgiGpuSchedulerRequestBudgetSaturated =
                sceneData.DdgiGpuSchedulerReadbackValid != 0 &&
                _ddgiProbeVolumeManager.LastCompletedGpuSchedulerRequestBudget > 0 &&
                completedSchedulerCounters.RequestCount >= (uint)_ddgiProbeVolumeManager.LastCompletedGpuSchedulerRequestBudget ? 1 : 0;
            sceneData.DdgiGpuSchedulerPrimaryRayBudgetSaturated =
                sceneData.DdgiGpuSchedulerReadbackValid != 0 &&
                _ddgiProbeVolumeManager.LastCompletedGpuSchedulerPrimaryRayBudget > 0 &&
                completedSchedulerCounters.PrimaryRayCount >= (uint)_ddgiProbeVolumeManager.LastCompletedGpuSchedulerPrimaryRayBudget ? 1 : 0;
            DdgiGpuSchedulerValidationSnapshot schedulerValidation = _ddgiProbeVolumeManager.LastCompletedGpuSchedulerValidation;
            sceneData.DdgiGpuSchedulerValidationValid = gpuSchedulerActive ? schedulerValidation.Valid : 0;
            sceneData.DdgiGpuSchedulerValidationStatus = gpuSchedulerActive ? schedulerValidation.Status : string.Empty;
            sceneData.DdgiGpuSchedulerValidationCpuRequestCount = gpuSchedulerActive ? schedulerValidation.CpuRequestCount : 0;
            sceneData.DdgiGpuSchedulerValidationGpuRequestCount = gpuSchedulerActive ? schedulerValidation.GpuRequestCount : 0u;
            sceneData.DdgiGpuSchedulerValidationComparedRequestCount = gpuSchedulerActive ? schedulerValidation.ComparedRequestCount : 0;
            sceneData.DdgiGpuSchedulerValidationMismatchCount = gpuSchedulerActive ? schedulerValidation.MismatchCount : 0;
            sceneData.DdgiGpuSchedulerValidationSampleLimit = gpuSchedulerActive ? schedulerValidation.SampleLimit : 0;
            sceneData.DdgiGpuSchedulerValidationFirstMismatch = gpuSchedulerActive ? schedulerValidation.FirstMismatch : string.Empty;
            uint knownUpdateProbeCount = ddgiActive
                ? gpuSchedulerActive
                    ? sceneData.DdgiGpuSchedulerActualRequestCount
                    : (uint)Math.Max(0, sceneData.DdgiProbesUpdated)
                : 0u;
            uint knownUpdateRayCount = ddgiActive
                ? gpuSchedulerActive
                    ? sceneData.DdgiGpuSchedulerActualPrimaryRayCount
                    : (uint)Math.Min((ulong)uint.MaxValue, _ddgiProbeVolumeManager.LastScheduledPrimaryRayCount)
                : 0u;
            sceneData.DdgiTraceDispatchGroupCount = knownUpdateProbeCount;
            sceneData.DdgiTraceProbeCount = knownUpdateProbeCount;
            sceneData.DdgiTraceRayCount = knownUpdateRayCount;
            sceneData.DdgiBlendProbeCount = knownUpdateProbeCount;
            sceneData.DdgiRelocateClassifyProbeCount = knownUpdateProbeCount;
            sceneData.DdgiPublishProbeCount = knownUpdateProbeCount;
            sceneData.DdgiRayScratchBytes = ddgiActive ? _ddgiProbeVolumeManager.LastRayScratchBytes : 0UL;
            sceneData.DdgiUpdatedAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.LastUpdatedAtlasBytes : 0UL;
            sceneData.DdgiPublishedCacheLatencyFrames = ddgiActive ? _ddgiProbeVolumeManager.LastPublishedCacheLatencyFrames : 0;
            sceneData.DdgiCacheGeneration = ddgiActive ? _ddgiProbeVolumeManager.PublishedCacheGeneration : 0u;
            sceneData.DdgiLastUpdatedFrameSerial = ddgiActive ? _ddgiProbeVolumeManager.PublishedCacheLastUpdatedFrameSerial : 0UL;
            sceneData.DdgiCacheWarmupState = ddgiActive ? _ddgiProbeVolumeManager.WarmupState : DdgiRuntimeWarmupState.Disabled;
            sceneData.DdgiWarmupState = ddgiActive ? _ddgiProbeVolumeManager.WarmupState : DdgiRuntimeWarmupState.Disabled;
            sceneData.DdgiWarmedVisibleProbeFraction = ddgiActive ? _ddgiProbeVolumeManager.LastWarmedVisibleProbeFraction : 0.0f;
            sceneData.DdgiWarmedLocalProbeFraction = ddgiActive ? _ddgiProbeVolumeManager.LastWarmedLocalProbeFraction : 0.0f;
            sceneData.DdgiWarmedCascade0ProbeFraction = ddgiActive ? _ddgiProbeVolumeManager.LastWarmedCascade0ProbeFraction : 0.0f;
            PopulateDdgiPassExecutionDiagnostics(sceneData);
            sceneData.CpuDdgiRecordMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.LastUploadMicroseconds : 0;
            sceneData.CpuSimpleDdgiRecordMicroseconds = simpleDdgiActive
                ? _simpleDdgiVolumeManager?.LastUploadMicroseconds ?? 0
                : 0;
            sceneData.CpuFarFieldRecordMicroseconds = simpleDdgiActive
                ? _farFieldClipmapManager?.LastUploadMicroseconds ?? 0
                : 0;
            sceneData.CpuDdgiSchedulerMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.LastSchedulerMicroseconds : 0;
            sceneData.CpuDdgiSchedulerP95Microseconds = ddgiActive ? _ddgiProbeVolumeManager.SchedulerP95Microseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseClipmapDirtyMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseClipmapDirtyMicroseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseDirtyRegionsMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseDirtyRegionsMicroseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseUninitializedMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseUninitializedMicroseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseFrustumMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseFrustumMicroseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseSafetyMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseSafetyMicroseconds : 0;
            sceneData.CpuDdgiSchedulerPhaseRoundRobinMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerPhaseRoundRobinMicroseconds : 0;
            sceneData.CpuDdgiSchedulerCandidateInsertCount = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerCandidateInsertCount : 0;
            sceneData.CpuDdgiSchedulerCandidateMaxShiftCount = ddgiActive ? _ddgiProbeVolumeManager.CpuSchedulerCandidateMaxShiftCount : 0;
            sceneData.DdgiSchedulerTimingSampleCount = ddgiActive ? _ddgiProbeVolumeManager.SchedulerTimingSampleCount : 0;
            sceneData.GpuDdgiScheduleP95Microseconds = ddgiActive ? _ddgiProbeVolumeManager.GpuScheduleP95Microseconds : 0;
            sceneData.GpuDdgiScheduleOverBudget = ddgiActive ? _ddgiProbeVolumeManager.GpuScheduleOverBudget : 0;
            sceneData.DdgiSchedulerP95OverBudget = gpuSchedulerActive
                ? sceneData.GpuDdgiScheduleOverBudget
                : sceneData.CpuDdgiSchedulerP95Microseconds > 250 ? 1 : 0;
            if (simpleDdgiActive)
            {
                PopulateSimpleDdgiFrameData(sceneData, simpleDdgiRayUpdateActive);
                // The canonical simple-DDGI atlases are SSBOs. Only the optional
                // sampled mirror belongs in texture accounting; keeping the two
                // categories separate makes the hard memory budget meaningful.
                sceneData.DdgiTextureBytes = _simpleDdgiVolumeManager?.SampledAtlasImageBytes ?? 0UL;
                // Paged far-field has its own independently enforced memory
                // budget. Charging it to DDGI as well creates a false atlas
                // budget overrun.
                sceneData.DdgiBufferBytes = _simpleDdgiVolumeManager?.BufferBytes ?? 0UL;
                sceneData.DdgiGpuSchedulerFallbackActive = 0;
                sceneData.DdgiGpuSchedulerFallbackReason = string.Empty;
                sceneData.DdgiSchedulerP95OverBudget = 0;
            }
            ScheduleReflectionProbeRecapturesFromGi(sceneData, ddgiActive, simpleDdgiActive);
        }

        private void ScheduleReflectionProbeRecapturesFromGi(SceneRenderingData sceneData, bool ddgiActive, bool simpleDdgiActive)
        {
            if (_reflectionProbeManager == null ||
                !Settings.Reflections.Enabled ||
                Settings.Reflections.Mode == ReflectionMode.Disabled ||
                sceneData.ReflectionProbeCount <= 0)
            {
                _lastReflectionProbeGiReady = false;
                _lastReflectionProbeSimpleDirtyReasonFlags = 0u;
                return;
            }

            bool giReady = simpleDdgiActive
                ? sceneData.SimpleDdgiActive != 0 &&
                  sceneData.SimpleDdgiProbeCount > 0 &&
                  sceneData.SimpleDdgiAtlasFresh == 0 &&
                  sceneData.SimpleDdgiFramesSinceLastClear > 0
                : ddgiActive && sceneData.DdgiWarmupState == DdgiRuntimeWarmupState.SteadyState;
            if (giReady && !_lastReflectionProbeGiReady)
                _reflectionProbeManager.RequestRecaptureAll("ddgi-ready");

            uint dirtyReasonFlags = simpleDdgiActive ? sceneData.SimpleDdgiDirtyReasonFlags : 0u;
            if (dirtyReasonFlags != 0u && dirtyReasonFlags != _lastReflectionProbeSimpleDirtyReasonFlags)
                _reflectionProbeManager.RequestRecaptureAll("simple-ddgi-dirty");
            if (ddgiActive && sceneData.DdgiNewlyInvalidatedProbeCount > 0)
                _reflectionProbeManager.RequestRecaptureAll("ddgi-dirty");

            sceneData.ReflectionProbeCapturesQueued = _reflectionProbeManager.CapturesQueued;
            sceneData.ReflectionProbeCapturesCompleted = _reflectionProbeManager.CapturesCompleted;
            _lastReflectionProbeGiReady = giReady;
            _lastReflectionProbeSimpleDirtyReasonFlags = dirtyReasonFlags;
        }

        internal static int ResolveConfiguredSimpleDdgiPrimaryRayBudget(int configuredBudget) =>
            Math.Max(0, configuredBudget);

        private void PopulateSimpleDdgiFrameData(SceneRenderingData sceneData, bool simpleDdgiRayUpdateActive)
        {
            if (_simpleDdgiVolumeManager == null)
                return;

            int probesToUpdate = simpleDdgiRayUpdateActive ? _simpleDdgiVolumeManager.ProbesToUpdate : 0;
            ulong primaryRayCount = simpleDdgiRayUpdateActive ? _simpleDdgiVolumeManager.ScheduledPrimaryRayCount : 0UL;
            int configuredRequestBudget =
                _simpleDdgiVolumeManager.SchedulerTelemetry.ConfiguredRequestBudget;
            int configuredPrimaryRayBudget = ResolveConfiguredSimpleDdgiPrimaryRayBudget(
                Settings.GlobalIllumination.DdgiProbeUpdatePrimaryRayBudget);
            sceneData.DdgiProbeVolumeCount = _simpleDdgiVolumeManager.VolumeCount;
            sceneData.DdgiProbeCount = _simpleDdgiVolumeManager.ProbeCount;
            sceneData.DdgiActiveProbeCount = _simpleDdgiVolumeManager.ActiveProbeCount;
            sceneData.DdgiRaysPerProbe = _simpleDdgiVolumeManager.RaysPerProbe;
            sceneData.DdgiProbesUpdated = probesToUpdate;
            sceneData.SimpleDdgiActive = _simpleDdgiVolumeManager.ProbeCount > 0 ? 1 : 0;
            sceneData.SimpleDdgiProbeCount = _simpleDdgiVolumeManager.ProbeCount;
            sceneData.SimpleDdgiProbesUpdated = probesToUpdate;
            sceneData.SimpleDdgiRaysPerFrame = primaryRayCount;
            sceneData.SimpleDdgiTransportV2Active = _simpleDdgiVolumeManager.TransportV2Active ? 1 : 0;
            sceneData.SimpleDdgiAutomaticProbeDensityActive = _simpleDdgiVolumeManager.TransportV2Active &&
                Settings.GlobalIllumination.SimpleDdgiAutomaticProbeDensityEnabled ? 1 : 0;
            sceneData.SimpleDdgiTransportSourceRefreshProbeCount = _simpleDdgiVolumeManager.SourceRefreshProbeCount;
            sceneData.SimpleDdgiTransportSourceCacheReuseProbeCount = _simpleDdgiVolumeManager.SourceCacheReuseProbeCount;
            sceneData.SimpleDdgiTransportSourceRayCount = _simpleDdgiVolumeManager.ScheduledSourceRayCount;
            sceneData.SimpleDdgiTransportSolveRayCount = _simpleDdgiVolumeManager.ScheduledTransportRayCount;
            sceneData.SimpleDdgiTransportPublishedProbeCount = _simpleDdgiVolumeManager.TransportPublishedProbeCount;
            sceneData.SimpleDdgiTransportPublishRegionCount = _simpleDdgiVolumeManager.TransportPublishRegionCount;
            sceneData.SimpleDdgiTransportPublishedProbeTotal = _simpleDdgiVolumeManager.TransportPublishedProbeTotal;
            sceneData.SimpleDdgiTransportPublishRegionTotal = _simpleDdgiVolumeManager.TransportPublishRegionTotal;
            sceneData.SimpleDdgiUpdateTransactionAbortCount = _simpleDdgiVolumeManager.UpdateTransactionAbortCount;
            sceneData.SimpleDdgiTransportSourceCacheInvalidationCount = _simpleDdgiVolumeManager.SourceCacheInvalidationCount;
            sceneData.SimpleDdgiSourceLightingGeneration = _simpleDdgiVolumeManager.SourceLightingGeneration;
            sceneData.SimpleDdgiTransportGeneration = _simpleDdgiVolumeManager.TransportGeneration;
            _simpleDdgiVolumeManager.GetTransportProgress(
                out int sourceReadyProbeCount,
                out int sourceStaleProbeCount,
                out int convergedProbeCount,
                out int pendingSolverProbeCount);
            sceneData.SimpleDdgiTransportSourceReadyProbeCount = sourceReadyProbeCount;
            sceneData.SimpleDdgiTransportSourceStaleProbeCount = sourceStaleProbeCount;
            sceneData.SimpleDdgiTransportConvergedProbeCount = convergedProbeCount;
            sceneData.SimpleDdgiTransportPendingSolverProbeCount = pendingSolverProbeCount;
            sceneData.SimpleDdgiTransportGlobalConvergencePending = _simpleDdgiVolumeManager.TransportGlobalConvergencePending ? 1 : 0;
            sceneData.SimpleDdgiTransportGlobalConvergenceElapsedFrames = _simpleDdgiVolumeManager.TransportGlobalConvergenceElapsedFrames;
            sceneData.SimpleDdgiTransportCalibrationChangeCount = _simpleDdgiVolumeManager.TransportCalibrationChangeCount;
            sceneData.SimpleDdgiTransportIrradianceAtlasBytes = _simpleDdgiVolumeManager.TransportIrradianceAtlasBytes;
            sceneData.SimpleDdgiTransportSourceCacheBytes = _simpleDdgiVolumeManager.TransportSourceCacheBytes;
            sceneData.SimpleDdgiTransportSolverRelaxation = Settings.GlobalIllumination.SimpleDdgiTransportSolverRelaxation;
            sceneData.SimpleDdgiTransportAlbedoClamp = Settings.GlobalIllumination.SimpleDdgiTransportAlbedoClamp;
            sceneData.SimpleDdgiTransportResidualThreshold = Settings.GlobalIllumination.SimpleDdgiTransportResidualThreshold;
            sceneData.SimpleDdgiTransportMaximumSolverGenerations = Settings.GlobalIllumination.SimpleDdgiTransportMaximumSolverGenerations;
            sceneData.SimpleDdgiTransportSourceRefreshFrames = Settings.GlobalIllumination.SimpleDdgiTransportSourceRefreshFrames;
            sceneData.SimpleDdgiInactiveProbeCount = _simpleDdgiVolumeManager.InactiveProbeCount;
            sceneData.SimpleDdgiInactiveProbeSkipCount = _simpleDdgiVolumeManager.InactiveProbeSkipCount;
            sceneData.SimpleDdgiSavedRaysPerFrame = _simpleDdgiVolumeManager.InactiveProbeSavedPrimaryRayCount;
            sceneData.SimpleDdgiLightingDirtyFrames = _simpleDdgiVolumeManager.LightingDirtyFrames;
            sceneData.SimpleDdgiLightingDirtyBoostedCapacity = _simpleDdgiVolumeManager.LightingDirtyBoostedCapacity;
            sceneData.SimpleDdgiDirtyReasonFlags = _simpleDdgiVolumeManager.DirtyReasonFlags;
            sceneData.SimpleDdgiFullRayProbeUpdateCount = _simpleDdgiVolumeManager.FullRayProbeUpdateCount;
            sceneData.SimpleDdgiMaintenanceRayProbeUpdateCount = _simpleDdgiVolumeManager.MaintenanceRayProbeUpdateCount;
            sceneData.SimpleDdgiAdaptiveRaySavedRaysPerFrame = _simpleDdgiVolumeManager.AdaptiveRaySavedPrimaryRayCount;
            sceneData.SimpleDdgiNearFullRayProbeUpdateCount = _simpleDdgiVolumeManager.NearFullRayProbeUpdateCount;
            sceneData.SimpleDdgiMidFullRayProbeUpdateCount = _simpleDdgiVolumeManager.MidFullRayProbeUpdateCount;
            sceneData.SimpleDdgiFarFullRayProbeUpdateCount = _simpleDdgiVolumeManager.FarFullRayProbeUpdateCount;
            sceneData.SimpleDdgiNearMaintenanceRayProbeUpdateCount = _simpleDdgiVolumeManager.NearMaintenanceRayProbeUpdateCount;
            sceneData.SimpleDdgiMidMaintenanceRayProbeUpdateCount = _simpleDdgiVolumeManager.MidMaintenanceRayProbeUpdateCount;
            sceneData.SimpleDdgiFarMaintenanceRayProbeUpdateCount = _simpleDdgiVolumeManager.FarMaintenanceRayProbeUpdateCount;
            sceneData.SimpleDdgiNearScheduledPrimaryRayCount = _simpleDdgiVolumeManager.NearScheduledPrimaryRayCount;
            sceneData.SimpleDdgiMidScheduledPrimaryRayCount = _simpleDdgiVolumeManager.MidScheduledPrimaryRayCount;
            sceneData.SimpleDdgiFarScheduledPrimaryRayCount = _simpleDdgiVolumeManager.FarScheduledPrimaryRayCount;
            sceneData.SimpleDdgiDirtyFirstUpdateLatencySampleCount = _simpleDdgiVolumeManager.DirtyFirstUpdateLatencySampleCount;
            sceneData.SimpleDdgiDirtyFirstUpdateLatencyP50Frames = _simpleDdgiVolumeManager.DirtyFirstUpdateLatencyP50Frames;
            sceneData.SimpleDdgiDirtyFirstUpdateLatencyP95Frames = _simpleDdgiVolumeManager.DirtyFirstUpdateLatencyP95Frames;
            sceneData.SimpleDdgiDirtyFirstUpdateLatencyMaxFrames = _simpleDdgiVolumeManager.DirtyFirstUpdateLatencyMaxFrames;
            sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge =
                _simpleDdgiVolumeManager.OldestVisibleUnsupportedProbeAge;
            sceneData.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget =
                _simpleDdgiVolumeManager.VisibleUnsupportedProbeCountAboveLatencyTarget;
            sceneData.SimpleDdgiVisibleZeroSupportRepairUpdateCount =
                _simpleDdgiVolumeManager.VisibleZeroSupportRepairUpdateCount;
            sceneData.SimpleDdgiProbeLifecycleLatencyTargetFrames =
                _simpleDdgiVolumeManager.ProbeLifecycleLatencyTargetFrames;
            sceneData.SimpleDdgiMaximumFreshProbeAge =
                _simpleDdgiVolumeManager.MaximumFreshProbeAge;
            sceneData.SimpleDdgiMaximumScrollExposedProbeAge =
                _simpleDdgiVolumeManager.MaximumScrollExposedProbeAge;
            sceneData.SimpleDdgiMaximumRelocationPendingProbeAge =
                _simpleDdgiVolumeManager.MaximumRelocationPendingProbeAge;
            sceneData.SimpleDdgiMaximumUnpublishedProbeAge =
                _simpleDdgiVolumeManager.MaximumUnpublishedProbeAge;
            sceneData.SimpleDdgiProbeLifecycleBoundExceededCount =
                _simpleDdgiVolumeManager.ProbeLifecycleBoundExceededCount;
            sceneData.SimpleDdgiDirtyConvergenceLatencySampleCount = _simpleDdgiVolumeManager.DirtyConvergenceLatencySampleCount;
            sceneData.SimpleDdgiDirtyConvergenceLatencyP50Frames = _simpleDdgiVolumeManager.DirtyConvergenceLatencyP50Frames;
            sceneData.SimpleDdgiDirtyConvergenceLatencyP95Frames = _simpleDdgiVolumeManager.DirtyConvergenceLatencyP95Frames;
            sceneData.SimpleDdgiDirtyConvergenceLatencyMaxFrames = _simpleDdgiVolumeManager.DirtyConvergenceLatencyMaxFrames;
            sceneData.SimpleDdgiAtlasBytes = _simpleDdgiVolumeManager.AtlasBytes;
            sceneData.SimpleDdgiSampledAtlasRequested = _simpleDdgiVolumeManager.SampledAtlasRequested ? 1 : 0;
            sceneData.SimpleDdgiSampledAtlasActive = _simpleDdgiVolumeManager.SampledAtlasActive ? 1 : 0;
            sceneData.SimpleDdgiSampledAtlasGroupCount = _simpleDdgiVolumeManager.SampledAtlasGroupCount;
            sceneData.SimpleDdgiSampledAtlasLayersPerTexture = _simpleDdgiVolumeManager.SampledAtlasLayersPerTexture;
            sceneData.SimpleDdgiSampledAtlasImageBytes = _simpleDdgiVolumeManager.SampledAtlasImageBytes;
            sceneData.SimpleDdgiSampledAtlasFallbackReason = _simpleDdgiVolumeManager.SampledAtlasFallbackReason;
            sceneData.SimpleDdgiRecentered = _simpleDdgiVolumeManager.RecenteredThisFrame ? 1 : 0;
            sceneData.SimpleDdgiAtlasPreservedOnRecenter = _simpleDdgiVolumeManager.AtlasPreservedOnRecenterThisFrame ? 1 : 0;
            sceneData.SimpleDdgiAtlasCleared = _simpleDdgiVolumeManager.AtlasClearedThisFrame ? 1 : 0;
            sceneData.SimpleDdgiAtlasFresh = _simpleDdgiVolumeManager.AtlasFresh ? 1 : 0;
            sceneData.SimpleDdgiRecenterCount = _simpleDdgiVolumeManager.TotalRecenterCount;
            sceneData.SimpleDdgiAtlasClearCount = _simpleDdgiVolumeManager.TotalAtlasClearCount;
            sceneData.SimpleDdgiAtlasPreserveOnRecenterCount = _simpleDdgiVolumeManager.TotalAtlasPreserveOnRecenterCount;
            sceneData.SimpleDdgiFramesSinceLastClear = _simpleDdgiVolumeManager.FramesSinceLastClear;
            sceneData.SimpleDdgiFramesSinceLastRecenter = _simpleDdgiVolumeManager.FramesSinceLastRecenter;
            sceneData.DdgiFullRefreshFrameCount = _simpleDdgiVolumeManager.FullRefreshFrameCount;
            sceneData.DdgiPartialRefreshFrameCount = _simpleDdgiVolumeManager.PartialRefreshFrameCount;
            sceneData.DdgiUpdatedProbeFraction = _simpleDdgiVolumeManager.ProbeCount > 0
                ? Math.Clamp(probesToUpdate / (float)_simpleDdgiVolumeManager.ProbeCount, 0.0f, 1.0f)
                : 0.0f;
            sceneData.DdgiProbeUpdateStartIndex = _simpleDdgiVolumeManager.UpdateStartProbe;
            sceneData.DdgiProbeUpdateEndIndex = _simpleDdgiVolumeManager.ProbeCount > 0 && probesToUpdate > 0
                ? (_simpleDdgiVolumeManager.UpdateStartProbe + probesToUpdate - 1) % _simpleDdgiVolumeManager.ProbeCount
                : 0;
            sceneData.DdgiSkippedProbeCount = Math.Max(0, _simpleDdgiVolumeManager.ProbeCount - probesToUpdate);
            _simpleDdgiVolumeManager.GetEstimatedProbeAgeFrames(
                out float estimatedAgeP50,
                out float estimatedAgeP95,
                out float estimatedAgeMaximum);
            sceneData.DdgiFramesSinceProbeUpdatedP50 = estimatedAgeP50;
            sceneData.DdgiFramesSinceProbeUpdatedP95 = estimatedAgeP95;
            sceneData.DdgiFramesSinceProbeUpdatedMax = estimatedAgeMaximum;
            sceneData.DdgiNewlyInvalidatedProbeCount = _simpleDdgiVolumeManager.NewlyInvalidatedProbeCount;
            sceneData.DdgiRefreshReasonRecenterProbeCount = _simpleDdgiVolumeManager.RecenterRefreshProbeCount;
            sceneData.DdgiRefreshReasonDirtyProbeCount = _simpleDdgiVolumeManager.DirtyRefreshProbeCount;
            sceneData.DdgiRefreshReasonAgeProbeCount = _simpleDdgiVolumeManager.AgeRefreshProbeCount;
            sceneData.DdgiRefreshReasonVisibilityProbeCount = 0;
            sceneData.DdgiRefreshReasonFullRefreshProbeCount = _simpleDdgiVolumeManager.FullRefreshProbeCount;
            if (!simpleDdgiRayUpdateActive && _simpleDdgiVolumeManager.ProbeCount > 0)
                sceneData.DdgiSimpleTraceTlasUnavailableFrameCount = Math.Max(sceneData.DdgiSimpleTraceTlasUnavailableFrameCount, 1u);
            sceneData.DdgiMaxActiveProbeBudget = _simpleDdgiVolumeManager.LastLayoutReport?.Budget.ProbeBudget
                ?? _simpleDdgiVolumeManager.ProbeCount;
            sceneData.DdgiMaxProbeUpdatesPerFrame = configuredRequestBudget;
            // These are hard configured caps, not the current queue output.  Keeping
            // them independent makes budget evaluation capable of detecting an
            // accidental scheduler overrun instead of comparing a value to itself.
            sceneData.DdgiProbeUpdateRequestBudget = configuredRequestBudget;
            sceneData.DdgiProbeUpdatePrimaryRayBudget = configuredPrimaryRayBudget;
            sceneData.DdgiScheduledRequestBudget = configuredRequestBudget;
            sceneData.DdgiScheduledPrimaryRayBudget = configuredPrimaryRayBudget;
            sceneData.DdgiTraceDispatchGroupCount = (uint)Math.Max(0, probesToUpdate);
            sceneData.DdgiTraceProbeCount = (uint)Math.Max(0, probesToUpdate);
            sceneData.DdgiTraceRayCount = (uint)Math.Min(uint.MaxValue, primaryRayCount);
            sceneData.DdgiBlendProbeCount = (uint)Math.Max(0, probesToUpdate);
            sceneData.DdgiRelocateClassifyProbeCount = (uint)Math.Max(0, probesToUpdate);
            sceneData.DdgiPublishProbeCount = (uint)Math.Max(0, probesToUpdate);
            sceneData.DdgiRayScratchBytes = _simpleDdgiVolumeManager.RayScratchBytes;
            sceneData.DdgiUpdatedAtlasBytes = _simpleDdgiVolumeManager.AtlasBytes;
            sceneData.DdgiProbeStateBufferBytes = _simpleDdgiVolumeManager.ProbeStateBytes;
            sceneData.DdgiProbeUpdateQueueBytes = _simpleDdgiVolumeManager.ProbeUpdateQueueBytes;
            sceneData.DdgiProbeRelocationClassificationBytes = _simpleDdgiVolumeManager.RelocationClassificationBytes;
            sceneData.DdgiCurrentIrradianceAtlasBytes = _simpleDdgiVolumeManager.IrradianceAtlasBytes;
            sceneData.DdgiCurrentVisibilityAtlasBytes = _simpleDdgiVolumeManager.VisibilityAtlasBytes;
            sceneData.DdgiAtlasMemoryBudgetBytes = Settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes;
            sceneData.DdgiTextureBytes = _simpleDdgiVolumeManager.SampledAtlasImageBytes;
            // Far-field residency is accounted against
            // FarFieldMemoryBudgetBytes and must not also consume the
            // independent DDGI atlas budget.
            sceneData.DdgiBufferBytes = _simpleDdgiVolumeManager.BufferBytes;
            sceneData.DdgiProbeRelocationCount = _simpleDdgiVolumeManager.ProbeRelocationCount;
            sceneData.DdgiProbeClassificationCount = probesToUpdate;
            sceneData.DdgiClassifiedInactiveProbeCountEstimate = _simpleDdgiVolumeManager.ClassifiedInactiveProbeCountEstimate;
            sceneData.DdgiAverageRelocationFractionEstimate = _simpleDdgiVolumeManager.AverageRelocationFractionEstimate;
            sceneData.DdgiScrollCount = _simpleDdgiVolumeManager.ScrollCopyCount;
            sceneData.DdgiVolumeDiagnostics.Clear();
            sceneData.DdgiVolumeDiagnostics.AddRange(_simpleDdgiVolumeManager.GetVolumeDiagnostics());
            sceneData.DdgiScheduledPrimaryRayCount = primaryRayCount;
            sceneData.DdgiEffectiveMaxShadedLights = _simpleDdgiVolumeManager.EffectiveMaxShadedLights;
            sceneData.DdgiEstimatedShadowRayUpperBound = EstimateSimpleDdgiShadowRayUpperBound(
                primaryRayCount,
                sceneData.DirectionalLightCount,
                sceneData.LocalLightCount,
                sceneData.DdgiEffectiveMaxShadedLights);
            PopulateSimpleDdgiLightSelectionDiagnostics(
                sceneData,
                primaryRayCount,
                sceneData.DirectionalLightCount,
                sceneData.LocalLightCount,
                sceneData.DdgiEffectiveMaxShadedLights);
            sceneData.DdgiQualityTier = Settings.GlobalIllumination.DdgiQualityTier;
            // See the full-DDGI setup above: this is a runtime execution result, not the
            // user's request. It is set immediately before a validated split graph executes.
            sceneData.DdgiAsyncComputeEnabled = 0;
            sceneData.DdgiCacheGeneration = 1u;
            sceneData.DdgiWarmupState = DdgiRuntimeWarmupState.SteadyState;
            sceneData.DdgiCacheWarmupState = DdgiRuntimeWarmupState.SteadyState;
            sceneData.DdgiWarmedVisibleProbeFraction = 1.0f;
            sceneData.DdgiWarmedLocalProbeFraction = 1.0f;
            sceneData.DdgiWarmedCascade0ProbeFraction = 1.0f;
            sceneData.DdgiUpdateSkipReason = string.Empty;
            sceneData.DdgiPublishSkipReason = string.Empty;
        }

        private void PopulateDdgiPassExecutionDiagnostics(SceneRenderingData sceneData)
        {
            bool accelerationStructureActive = _accelerationStructureManager?.Active == true;
            sceneData.DdgiUpdateExecuted = 0;
            sceneData.DdgiPublishExecuted = 0;
            sceneData.DdgiUpdateSkipReason = DdgiPassExecutionDiagnostics.ResolveUpdateSkipReason(
                true,
                Settings.GlobalIllumination,
                accelerationStructureActive,
                sceneData);
            sceneData.DdgiPublishSkipReason = DdgiPassExecutionDiagnostics.ResolvePublishSkipReason(
                Settings.GlobalIllumination,
                accelerationStructureActive,
                sceneData);
        }

        private static void PopulateDdgiCoverageDiagnostics(SceneRenderingData sceneData)
        {
            int tileCount = Math.Max(sceneData.DdgiGatherTileCount, 0);
            if (tileCount <= 0)
            {
                sceneData.DdgiGatherSelectedLocalTileFraction = 0.0f;
                sceneData.DdgiGatherSelectedClipmapTileFraction = 0.0f;
                sceneData.DdgiGatherFallbackTileFraction = 0.0f;
                sceneData.DdgiAverageSpatialCoverageEstimate = 0.0f;
                sceneData.DdgiAverageSupportCoverageEstimate = 0.0f;
                sceneData.DdgiAverageDataConfidenceEstimate = 0.0f;
                sceneData.DdgiAverageVisibilityConfidenceEstimate = 0.0f;
                sceneData.DdgiAverageLeakAttenuationEstimate = 0.0f;
                sceneData.DdgiAverageEffectiveContributionEstimate = 0.0f;
                sceneData.DdgiAverageOwnershipConsumedEstimate = 0.0f;
                sceneData.DdgiAverageRelocationFractionEstimate = 0.0f;
                sceneData.DdgiClassifiedInactiveProbeCountEstimate = 0;
                return;
            }

            float invTileCount = 1.0f / tileCount;
            int fallbackTiles = Math.Clamp(sceneData.DdgiGatherFallbackTileCount, 0, tileCount);
            int coveredTiles = Math.Max(0, tileCount - fallbackTiles);
            float coverage = Math.Clamp(coveredTiles * invTileCount, 0.0f, 1.0f);
            float visibleSupport = Math.Clamp(sceneData.DdgiAverageProbeConfidence, 0.0f, 1.0f);

            sceneData.DdgiGatherSelectedLocalTileFraction = Math.Clamp(sceneData.DdgiGatherSelectedLocalTileCount * invTileCount, 0.0f, 1.0f);
            sceneData.DdgiGatherSelectedClipmapTileFraction = Math.Clamp(sceneData.DdgiGatherSelectedClipmapTileCount * invTileCount, 0.0f, 1.0f);
            sceneData.DdgiGatherFallbackTileFraction = Math.Clamp(fallbackTiles * invTileCount, 0.0f, 1.0f);
            sceneData.DdgiAverageSpatialCoverageEstimate = coverage;
            sceneData.DdgiAverageSupportCoverageEstimate = visibleSupport;
            sceneData.DdgiAverageDataConfidenceEstimate = visibleSupport;
            sceneData.DdgiAverageVisibilityConfidenceEstimate = visibleSupport;
            sceneData.DdgiAverageLeakAttenuationEstimate = visibleSupport > 0.0f ? 1.0f : 0.0f;
            sceneData.DdgiAverageEffectiveContributionEstimate = Math.Clamp(coverage * visibleSupport, 0.0f, 1.0f);
            sceneData.DdgiAverageOwnershipConsumedEstimate = sceneData.DdgiAverageEffectiveContributionEstimate;

            int activeProbes = Math.Max(sceneData.DdgiActiveProbeCount, 0);
            sceneData.DdgiAverageRelocationFractionEstimate = activeProbes > 0
                ? Math.Clamp(sceneData.DdgiProbeRelocationCount / (float)activeProbes, 0.0f, 1.0f)
                : 0.0f;
            sceneData.DdgiClassifiedInactiveProbeCountEstimate = Math.Clamp(
                sceneData.DdgiLowConfidenceProbeUpdateCount,
                0,
                Math.Max(sceneData.DdgiProbeClassificationCount, 0));
        }

        private static DdgiRuntimeSnapshot CreateDdgiRuntimeSnapshot(SceneRenderingData sceneData)
        {
            return new DdgiRuntimeSnapshot(
                VolumeCount: sceneData.DdgiProbeVolumeCount,
                ActiveProbeCount: sceneData.DdgiActiveProbeCount,
                ScheduledProbeUpdates: sceneData.DdgiProbesUpdated,
                WarmupState: sceneData.DdgiWarmupState,
                WarmedVisibleProbeFraction: sceneData.DdgiWarmedVisibleProbeFraction,
                WarmedLocalProbeFraction: sceneData.DdgiWarmedLocalProbeFraction,
                WarmedCascade0ProbeFraction: sceneData.DdgiWarmedCascade0ProbeFraction,
                SchedulerCandidateCount: ClampUIntToInt(sceneData.DdgiGpuSchedulerCandidateCount),
                SchedulerRequestCount: ClampUIntToInt(sceneData.DdgiGpuSchedulerRequestCount),
                SchedulerBudgetRejectedCount: ClampUIntToInt(sceneData.DdgiGpuSchedulerBudgetRejectedCount),
                SchedulerGpuMicroseconds: sceneData.GpuDdgiScheduleMicroseconds,
                SchedulerGpuP95Microseconds: sceneData.GpuDdgiScheduleP95Microseconds,
                EstimateSpatialCoverage: sceneData.DdgiAverageSpatialCoverageEstimate,
                EstimateSupportCoverage: sceneData.DdgiAverageSupportCoverageEstimate,
                EstimateDataConfidence: sceneData.DdgiAverageDataConfidenceEstimate,
                EstimateVisibilityConfidence: sceneData.DdgiAverageVisibilityConfidenceEstimate,
                EstimateLeakAttenuation: sceneData.DdgiAverageLeakAttenuationEstimate,
                EstimateEffectiveWeight: sceneData.DdgiAverageEffectiveContributionEstimate,
                EstimateOwnershipConsumed: sceneData.DdgiAverageOwnershipConsumedEstimate,
                EstimateRelocationMagnitude: sceneData.DdgiAverageRelocationFractionEstimate,
                EstimateInactiveProbeCount: sceneData.DdgiClassifiedInactiveProbeCountEstimate,
                GatherFallbackTileCount: sceneData.DdgiGatherFallbackTileCount,
                EmptyGatherTileCount: sceneData.DdgiForwardGatherTileEmpty,
                SelectedLocalTileCount: sceneData.DdgiGatherSelectedLocalTileCount,
                SelectedClipmapTileCount: sceneData.DdgiGatherSelectedClipmapTileCount,
                SimpleGatherPrimaryRejectionCounts: sceneData.SimpleDdgiGatherPrimaryRejectionCounts,
                SimpleGatherFallbackRejectionCounts: sceneData.SimpleDdgiGatherFallbackRejectionCounts,
                SimpleGatherRecoveryRejectionCounts: sceneData.SimpleDdgiGatherRecoveryRejectionCounts,
                SimpleGatherPrimaryAllFailedCount: sceneData.SimpleDdgiGatherPrimaryAllFailedCount,
                SimpleGatherFallbackAllFailedCount: sceneData.SimpleDdgiGatherFallbackAllFailedCount,
                SimpleGatherRecoveryAllFailedCount: sceneData.SimpleDdgiGatherRecoveryAllFailedCount,
                SimpleOldestVisibleUnsupportedProbeAge: sceneData.SimpleDdgiOldestVisibleUnsupportedProbeAge,
                SimpleVisibleUnsupportedProbeCountAboveLatencyTarget: sceneData.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget,
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
                SimpleGpuBlendMicroseconds: sceneData.GpuSimpleDdgiBlendMicroseconds);
        }

        private void PopulateDdgiDiagnostics(SceneRenderingData sceneData, DdgiFrameLayout layout, bool ddgiActive)
        {
            if (!ddgiActive || _ddgiProbeVolumeManager == null)
            {
                sceneData.DdgiCascadeCount = 0;
                sceneData.DdgiScrollCount = 0;
                sceneData.DdgiNewProbeCount = 0;
                sceneData.DdgiDirtyBoundsProbeUpdateCount = 0;
                sceneData.DdgiVisibleFrustumProbeUpdateCount = 0;
                sceneData.DdgiOutsideFrustumSafetyProbeUpdateCount = 0;
                sceneData.DdgiAgeRefreshProbeUpdateCount = 0;
                sceneData.DdgiHighVarianceProbeUpdateCount = 0;
                sceneData.DdgiLowConfidenceProbeUpdateCount = 0;
                sceneData.DdgiStableProbeUpdateCount = 0;
                sceneData.DdgiAverageProbeVariability = 0.0f;
                sceneData.DdgiAverageProbeConfidence = 0.0f;
                sceneData.DdgiScheduledPrimaryRayCount = 0;
                sceneData.DdgiEstimatedShadowRayUpperBound = 0;
                sceneData.DdgiSelectedDirectionalHitCount = 0;
                sceneData.DdgiSelectedLocalHitCount = 0;
                sceneData.DdgiVisibilityRayCount = 0;
                sceneData.DdgiSkippedLocalLightCount = 0;
                sceneData.DdgiLightSelectionMode = string.Empty;
                sceneData.DdgiPrimaryDirectionalLightIndex = -1;
                sceneData.DdgiSelectedLocalLightIndex = -1;
                sceneData.DdgiSelectedLocalLightEnergyScale = 1.0f;
                sceneData.DdgiQualityTier = DdgiQualityTier.DdgiHigh;
                sceneData.DdgiAdaptiveBudgetScale = 1.0f;
                sceneData.DdgiAdaptiveBudgetReduced = 0;
                sceneData.DdgiEmergencyDegradeActive = 0;
                sceneData.DdgiEffectiveMaxShadedLights = 0;
                sceneData.DdgiAdaptiveBudgetReason = string.Empty;
                sceneData.DdgiVolumeDiagnostics.Clear();
                sceneData.DdgiCacheGeneration = 0u;
                sceneData.DdgiLastUpdatedFrameSerial = 0UL;
                sceneData.DdgiCacheWarmupState = DdgiRuntimeWarmupState.Disabled;
                sceneData.DdgiStaleProbeCount = 0;
                sceneData.DdgiAverageProbeAge = 0.0f;
                sceneData.DdgiMaxProbeAge = 0UL;
                sceneData.DdgiFrustumUpdatePercentage = 0.0f;
                sceneData.DdgiOutsideFrustumUpdatePercentage = 0.0f;
                sceneData.DdgiResourceReinitializationCount = 0;
                sceneData.DdgiTotalResourceReinitializationCount = 0;
                sceneData.DdgiActiveLocalSlotCount = 0;
                sceneData.DdgiLocalSlotGeneration = 0;
                sceneData.DdgiLocalSlotInitBytes = 0UL;
                sceneData.DdgiLocalVolumeEvictionReason = string.Empty;
                sceneData.DdgiCacheClearReason = string.Empty;
                sceneData.DdgiCameraMovementClass = DdgiCameraMovementClass.None;
                return;
            }

            sceneData.DdgiCascadeCount = layout.CameraRelativeCascadeCount;
            sceneData.DdgiScrollCount = CountDdgiScrolledCascades(layout.CameraRelativeCascades);
            sceneData.DdgiNewProbeCount = _ddgiProbeVolumeManager.LastNewProbeUpdateCount;
            sceneData.DdgiDirtyBoundsProbeUpdateCount = _ddgiProbeVolumeManager.LastDirtyBoundsProbeUpdateCount;
            sceneData.DdgiVisibleFrustumProbeUpdateCount = _ddgiProbeVolumeManager.LastFrustumProbeUpdateCount;
            sceneData.DdgiOutsideFrustumSafetyProbeUpdateCount = _ddgiProbeVolumeManager.LastOutsideFrustumProbeUpdateCount;
            sceneData.DdgiAgeRefreshProbeUpdateCount = _ddgiProbeVolumeManager.LastAgeRefreshProbeUpdateCount;
            sceneData.DdgiHighVarianceProbeUpdateCount = _ddgiProbeVolumeManager.LastHighVarianceProbeUpdateCount;
            sceneData.DdgiLowConfidenceProbeUpdateCount = _ddgiProbeVolumeManager.LastLowConfidenceProbeUpdateCount;
            sceneData.DdgiStableProbeUpdateCount = _ddgiProbeVolumeManager.LastStableProbeUpdateCount;
            sceneData.DdgiAverageProbeVariability = _ddgiProbeVolumeManager.LastAverageProbeVariability;
            sceneData.DdgiAverageProbeConfidence = _ddgiProbeVolumeManager.LastAverageProbeConfidence;
            sceneData.DdgiScheduledPrimaryRayCount = _ddgiProbeVolumeManager.LastScheduledPrimaryRayCount;
            sceneData.DdgiEstimatedShadowRayUpperBound = EstimateDdgiShadowRayUpperBound(
                sceneData.DdgiScheduledPrimaryRayCount,
                sceneData.DirectionalLightCount,
                sceneData.LocalLightCount,
                sceneData.DdgiEffectiveMaxShadedLights > 0
                    ? sceneData.DdgiEffectiveMaxShadedLights
                    : Settings.GlobalIllumination.DdgiMaxShadedLights);
            PopulateDdgiLightSelectionDiagnostics(
                sceneData,
                sceneData.DdgiScheduledPrimaryRayCount,
                sceneData.DdgiPrimaryDirectionalLightIndex,
                sceneData.DdgiSelectedLocalLightIndex,
                sceneData.LocalLightCount,
                sceneData.DdgiEffectiveMaxShadedLights > 0
                    ? sceneData.DdgiEffectiveMaxShadedLights
                    : Settings.GlobalIllumination.DdgiMaxShadedLights);
            sceneData.DdgiVolumeDiagnostics.Clear();
            sceneData.DdgiVolumeDiagnostics.AddRange(_ddgiProbeVolumeManager.GetVolumeDiagnostics());
            sceneData.DdgiFrustumUpdatePercentage = Percentage(_ddgiProbeVolumeManager.LastFrustumProbeUpdateCount, sceneData.DdgiProbesUpdated);
            sceneData.DdgiOutsideFrustumUpdatePercentage = Percentage(_ddgiProbeVolumeManager.LastOutsideFrustumProbeUpdateCount, sceneData.DdgiProbesUpdated);
            sceneData.DdgiResourceReinitializationCount = _ddgiProbeVolumeManager.LastResourceReinitializationCount;
            sceneData.DdgiTotalResourceReinitializationCount = _ddgiProbeVolumeManager.TotalResourceReinitializationCount;
            sceneData.DdgiActiveLocalSlotCount = _ddgiProbeVolumeManager.LastActiveLocalSlotCount;
            sceneData.DdgiLocalSlotGeneration = _ddgiProbeVolumeManager.LastLocalSlotGeneration;
            sceneData.DdgiLocalSlotInitBytes = _ddgiProbeVolumeManager.LastLocalSlotInitBytes;
            sceneData.DdgiLocalVolumeEvictionReason = _ddgiProbeVolumeManager.LastLocalVolumeEvictionReason;
            sceneData.DdgiCacheClearReason = _ddgiProbeVolumeManager.LastCacheClearReason;
            sceneData.DdgiCameraMovementClass = layout.MovementClass;
            PopulateDdgiAgeDiagnostics(sceneData, layout.CameraRelativeCascades);
        }

        private void UpdateDdgiGpuSchedulerFallbackStateFromCompletedFrame()
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            if (gi.DdgiSchedulerMode == DdgiSchedulerMode.CpuReference)
            {
                ClearDdgiGpuSchedulerFallback();
                return;
            }

            if (_ddgiProbeVolumeManager == null || gi.DdgiGpuSchedulerForceCpuFallback)
                return;

            if (_ddgiProbeVolumeManager.LastCompletedGpuSchedulerCountersValid != 0)
            {
                string counterFailureReason = ResolveDdgiGpuSchedulerCounterFailureReason(
                    _ddgiProbeVolumeManager.LastCompletedGpuSchedulerCounters,
                    _ddgiProbeVolumeManager.LastCompletedGpuSchedulerRequestBudget,
                    _ddgiProbeVolumeManager.LastCompletedGpuSchedulerPrimaryRayBudget,
                    _ddgiProbeVolumeManager.LastCompletedGpuSchedulerQueueCapacity);
                if (!string.IsNullOrEmpty(counterFailureReason))
                {
                    LatchDdgiGpuSchedulerFallback(counterFailureReason);
                    return;
                }
            }

            DdgiGpuSchedulerValidationSnapshot validation = _ddgiProbeVolumeManager.LastCompletedGpuSchedulerValidation;
            if (gi.DdgiGpuSchedulerFallbackOnValidationFailure && validation.Valid != 0)
            {
                if (string.Equals(validation.Status, "ok", StringComparison.Ordinal))
                {
                    _ddgiGpuSchedulerValidationFailureCount = 0;
                }
                else if (validation.MismatchCount > 0)
                {
                    _ddgiGpuSchedulerValidationFailureCount++;
                    if (_ddgiGpuSchedulerValidationFailureCount >= gi.DdgiGpuSchedulerValidationFailureThreshold)
                        LatchDdgiGpuSchedulerFallback($"validation-{validation.Status}");
                }
            }
        }

        private void AdvanceDdgiGpuSchedulerFallbackRetry(bool ddgiRayUpdateActive)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            if (!_ddgiGpuSchedulerFallbackLatched)
            {
                _ddgiGpuSchedulerFallbackStableFrameCount = 0;
                return;
            }

            if (!gi.DdgiGpuSchedulerAutoRetryAfterFallback ||
                gi.DdgiGpuSchedulerForceCpuFallback ||
                !ddgiRayUpdateActive)
            {
                return;
            }

            _ddgiGpuSchedulerFallbackStableFrameCount++;
            if (_ddgiGpuSchedulerFallbackStableFrameCount >= gi.DdgiGpuSchedulerFallbackRetryStableFrames)
                ClearDdgiGpuSchedulerFallback();
        }

        private string ResolveDdgiGpuSchedulerFallbackReason(
            bool ddgiRayUpdateActive,
            bool ddgiCompareMode,
            bool compareModeUseGpuQueueForRendering)
        {
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            if (gi.DdgiSchedulerMode == DdgiSchedulerMode.CpuReference)
                return string.Empty;
            if (gi.DdgiGpuSchedulerForceCpuFallback)
                return LogDdgiGpuSchedulerFallbackOnce("forced-cpu-fallback");
            if (!ddgiRayUpdateActive)
                return LogDdgiGpuSchedulerFallbackOnce("ddgi-ray-update-inactive");
            if (ddgiCompareMode && !compareModeUseGpuQueueForRendering)
                return "compare-mode-cpu-queue";
            if (_ddgiSchedulePass?.IsAvailable != true)
            {
                string reason = string.IsNullOrEmpty(_ddgiSchedulePass?.InitializationFailureReason)
                    ? "schedule-pipeline-unavailable"
                    : _ddgiSchedulePass.InitializationFailureReason;
                return LatchDdgiGpuSchedulerFallback(reason);
            }
            return _ddgiGpuSchedulerFallbackLatched ? _ddgiGpuSchedulerFallbackReason : string.Empty;
        }

        internal static string ResolveDdgiGpuSchedulerCounterFailureReason(
            GPUDdgiSchedulerCounters counters,
            int requestBudget,
            int primaryRayBudget,
            int queueCapacity)
        {
            if (queueCapacity > 0 && counters.RequestCount > (uint)queueCapacity)
                return "counter-request-count-exceeds-queue-capacity";
            if (requestBudget > 0 && counters.RequestCount > (uint)requestBudget)
                return "counter-request-count-exceeds-budget";
            if (primaryRayBudget > 0 && counters.PrimaryRayCount > (uint)primaryRayBudget)
                return "counter-primary-ray-count-exceeds-budget";
            if (counters.DuplicateRequestCount > 0u)
                return "counter-duplicate-request";
            if (counters.InvalidProbeCount > 0u)
                return "counter-invalid-probe";
            return string.Empty;
        }

        private string LatchDdgiGpuSchedulerFallback(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "unknown";

            _ddgiGpuSchedulerFallbackLatched = true;
            _ddgiGpuSchedulerFallbackReason = LogDdgiGpuSchedulerFallbackOnce(reason);
            _ddgiGpuSchedulerFallbackStableFrameCount = 0;
            return _ddgiGpuSchedulerFallbackReason;
        }

        private string LogDdgiGpuSchedulerFallbackOnce(string reason)
        {
            if (!string.Equals(_ddgiGpuSchedulerLoggedFallbackReason, reason, StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine($"DDGI GPU scheduler fallback: {reason}");
                _ddgiGpuSchedulerLoggedFallbackReason = reason;
            }

            return reason;
        }

        private void ClearDdgiGpuSchedulerFallback()
        {
            _ddgiGpuSchedulerFallbackLatched = false;
            _ddgiGpuSchedulerFallbackReason = string.Empty;
            _ddgiGpuSchedulerValidationFailureCount = 0;
            _ddgiGpuSchedulerFallbackStableFrameCount = 0;
        }

        private static void PopulateDdgiLightSelectionMetadata(
            SceneRenderingData sceneData,
            LightFrameSnapshot lightSnapshot,
            bool ddgiRayUpdateActive)
        {
            if (!ddgiRayUpdateActive || lightSnapshot.Count == 0)
            {
                sceneData.DdgiPrimaryDirectionalLightIndex = -1;
                sceneData.DdgiSelectedLocalLightIndex = -1;
                sceneData.DdgiSelectedLocalLightEnergyScale = 1.0f;
                return;
            }

            sceneData.DdgiPrimaryDirectionalLightIndex = SelectPrimaryDdgiDirectionalLight(lightSnapshot);
            sceneData.DdgiSelectedLocalLightIndex = SelectPrimaryDdgiLocalLight(lightSnapshot, out float selectedLocalWeight, out float totalLocalWeight);
            sceneData.DdgiSelectedLocalLightEnergyScale = selectedLocalWeight > 0.0f
                ? Math.Clamp(totalLocalWeight / selectedLocalWeight, 1.0f, 64.0f)
                : 1.0f;
        }

        internal static int SelectPrimaryDdgiDirectionalLight(LightFrameSnapshot lightSnapshot)
        {
            int selectedIndex = -1;
            float selectedScore = -1.0f;
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light.Type != LightType.Directional)
                    continue;

                float score = LightLuminance(light) * Math.Max(light.Intensity, 0.0f);
                if (score > selectedScore)
                {
                    selectedIndex = i;
                    selectedScore = score;
                }
            }

            return selectedIndex;
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
            return LightLuminance(light) * Math.Max(light.Intensity, 0.0f) * range * range * spotFactor;
        }

        private static float LightLuminance(Light light)
        {
            return Math.Max(0.0f, 0.2126f * light.Color.X + 0.7152f * light.Color.Y + 0.0722f * light.Color.Z);
        }

        private static int CountDdgiScrolledCascades(IReadOnlyList<DdgiClipmapCascadeState> cascades)
        {
            int count = 0;
            for (int i = 0; i < cascades.Count; i++)
            {
                if (cascades[i].ScrollDelta != DdgiClipmapCell.Zero)
                    count++;
            }

            return count;
        }

        private static void PopulateDdgiAgeDiagnostics(SceneRenderingData sceneData, IReadOnlyList<DdgiClipmapCascadeState> cascades)
        {
            if (cascades.Count == 0)
            {
                sceneData.DdgiStaleProbeCount = 0;
                sceneData.DdgiAverageProbeAge = 0.0f;
                sceneData.DdgiMaxProbeAge = 0UL;
                return;
            }

            ulong ageSum = 0UL;
            ulong maxAge = 0UL;
            int sampled = 0;
            int stale = 0;
            int staleThreshold = Math.Max(120, sceneData.DdgiActiveProbeCount / Math.Max(1, sceneData.DdgiProbesUpdated));
            for (int i = 0; i < cascades.Count; i++)
            {
                DdgiClipmapCascadeState cascade = cascades[i];
                for (int z = 0; z < cascade.ProbeCountZ; z++)
                {
                    for (int y = 0; y < cascade.ProbeCountY; y++)
                    {
                        for (int x = 0; x < cascade.ProbeCountX; x++)
                        {
                            DdgiClipmapCell cell = new(
                                cascade.LogicalGridMinCell.X + x,
                                cascade.LogicalGridMinCell.Y + y,
                                cascade.LogicalGridMinCell.Z + z);
                            DdgiClipmapCellState state = cascade.GetCellState(cell);
                            ulong age = state.Initialized ? state.AgeFrames : (ulong)staleThreshold + 1UL;
                            if (age == ulong.MaxValue)
                                age = (ulong)staleThreshold + 1UL;

                            ageSum += age;
                            maxAge = Math.Max(maxAge, age);
                            sampled++;
                            if (!state.Initialized || age > (ulong)staleThreshold)
                                stale++;
                        }
                    }
                }
            }

            sceneData.DdgiStaleProbeCount = stale;
            sceneData.DdgiAverageProbeAge = sampled > 0 ? ageSum / (float)sampled : 0.0f;
            sceneData.DdgiMaxProbeAge = maxAge;
        }

        private static float Percentage(int numerator, int denominator)
        {
            if (denominator <= 0)
                return 0.0f;
            return Math.Clamp(numerator / (float)denominator, 0.0f, 1.0f) * 100.0f;
        }

        internal static ulong EstimateDdgiShadowRayUpperBound(
            ulong primaryRayCount,
            int directionalLightCount,
            int localLightCount,
            int maxShadedLights)
        {
            if (primaryRayCount == 0 || maxShadedLights <= 0)
                return 0;

            return primaryRayCount * (ulong)CountSelectedDdgiHitLights(directionalLightCount, localLightCount, maxShadedLights);
        }

        internal static ulong EstimateSimpleDdgiShadowRayUpperBound(
            ulong primaryRayCount,
            int directionalLightCount,
            int localLightCount,
            int maxShadedLights)
        {
            int capacity = Math.Min(Math.Max(maxShadedLights, 0), 8);
            int lightCount = Math.Min(
                capacity,
                Math.Max(directionalLightCount, 0) + Math.Max(localLightCount, 0));
            return primaryRayCount * (ulong)lightCount;
        }

        private static void PopulateSimpleDdgiLightSelectionDiagnostics(
            SceneRenderingData sceneData,
            ulong primaryRayCount,
            int directionalLightCount,
            int localLightCount,
            int maxShadedLights)
        {
            int capacity = Math.Min(Math.Max(maxShadedLights, 0), 8);
            int selectedDirectionalLights = Math.Min(Math.Max(directionalLightCount, 0), capacity);
            int selectedLocalLights = Math.Min(
                Math.Max(localLightCount, 0),
                capacity - selectedDirectionalLights);
            ulong selectedDirectionalHits = primaryRayCount * (ulong)selectedDirectionalLights;
            ulong selectedLocalHits = primaryRayCount * (ulong)selectedLocalLights;

            sceneData.DdgiSelectedDirectionalHitCount = selectedDirectionalHits;
            sceneData.DdgiSelectedLocalHitCount = selectedLocalHits;
            sceneData.DdgiVisibilityRayCount = selectedDirectionalHits + selectedLocalHits;
            sceneData.DdgiSkippedLocalLightCount = primaryRayCount *
                (ulong)Math.Max(0, localLightCount - selectedLocalLights);
            sceneData.DdgiLightSelectionMode = primaryRayCount > 0 && capacity > 0
                ? "simple-per-hit-top-n"
                : "disabled";
        }

        private static void PopulateDdgiLightSelectionDiagnostics(
            SceneRenderingData sceneData,
            ulong primaryRayCount,
            int selectedDirectionalLightIndex,
            int selectedLocalLightIndex,
            int localLightCount,
            int maxShadedLights)
        {
            int capacity = Math.Min(maxShadedLights, 2);
            int selectedDirectionalLights = selectedDirectionalLightIndex >= 0 && capacity > 0 ? 1 : 0;
            int selectedLocalLights = selectedLocalLightIndex >= 0 && selectedDirectionalLights < capacity ? 1 : 0;
            ulong selectedDirectionalHits = primaryRayCount * (ulong)selectedDirectionalLights;
            ulong selectedLocalHits = primaryRayCount * (ulong)selectedLocalLights;

            sceneData.DdgiSelectedDirectionalHitCount = selectedDirectionalHits;
            sceneData.DdgiSelectedLocalHitCount = selectedLocalHits;
            sceneData.DdgiVisibilityRayCount = selectedDirectionalHits + selectedLocalHits;
            sceneData.DdgiSkippedLocalLightCount = primaryRayCount * (ulong)Math.Max(0, localLightCount - selectedLocalLights);
            sceneData.DdgiLightSelectionMode = primaryRayCount > 0 && maxShadedLights > 0
                ? "bounded-directional-local"
                : "disabled";
        }

        private static int CountSelectedDdgiHitLights(int directionalLightCount, int localLightCount, int maxShadedLights)
        {
            int capacity = Math.Min(maxShadedLights, 2);
            int selected = 0;
            if (directionalLightCount > 0 && selected < capacity)
                selected++;
            if (localLightCount > 0 && selected < capacity)
                selected++;
            return selected;
        }

        private DdgiFrameLayout BuildDdgiFrameLayout(
            Scene scene,
            ICamera camera,
            LightFrameSnapshot lightSnapshot,
            SceneRenderingData sceneData,
            bool cameraCut)
        {
            bool viewPriorityHistoryReset = DetectDdgiViewPriorityHistoryReset(camera, cameraCut);
            ulong frameSerial = sceneData.DdgiFrameSerial;
            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                Settings.GlobalIllumination,
                _cameraRelativeDdgiClipmaps,
                frameSerial,
                viewPriorityHistoryReset,
                ResolveDdgiCameraVelocity(camera, viewPriorityHistoryReset),
                _ddgiLocalVolumeSlots);
            IReadOnlyList<DdgiDirtyRegion> dirtyRegions = CollectDdgiDirtyRegions(scene, lightSnapshot, layout.Volumes, sceneData);
            return layout.WithDirtyRegions(dirtyRegions);
        }

        private bool DetectDdgiViewPriorityHistoryReset(ICamera camera, bool cameraCut)
        {
            Matrix4x4 projection = camera.ProjectionMatrix;
            bool projectionChanged = _hasLastDdgiProjectionMatrix &&
                !ApproximatelyEqualProjection(projection, _lastDdgiProjectionMatrix, 0.0005f);

            _lastDdgiProjectionMatrix = projection;
            _hasLastDdgiProjectionMatrix = true;
            return cameraCut || projectionChanged;
        }

        private Vector3 ResolveDdgiCameraVelocity(ICamera camera, bool viewPriorityHistoryReset)
        {
            Vector3 velocity = Vector3.Zero;
            if (_hasLastDdgiCameraPosition && !viewPriorityHistoryReset)
                velocity = camera.Position - _lastDdgiCameraPosition;

            _lastDdgiCameraPosition = camera.Position;
            _hasLastDdgiCameraPosition = true;
            return velocity;
        }

        private void UploadDdgiEmissiveSources(Scene scene, SceneRenderingData sceneData, bool ddgiRayUpdateActive)
        {
            if (!ddgiRayUpdateActive || !_ddgiEmissiveSourceBuffer.IsValid)
            {
                _ddgiEmissiveSourceCount = 0;
                _ddgiEmissiveTriangleTableStats = default;
                _ddgiEmissiveSkippedSkinnedObjectCount = 0;
                _ddgiEmissiveSkippedSkinnedImportance = 0.0;
                sceneData.DdgiEmissiveSourceCount = 0;
                sceneData.DdgiEmissiveSourceRevision = _ddgiEmissiveSourceRevision;
                PopulateDdgiEmissiveDiagnostics(sceneData, active: false);
                return;
            }

            bool triangleSampling =
                Settings.GlobalIllumination.EffectiveGiEmissiveMeshSampling;
            int triangleBudget = triangleSampling
                ? Math.Clamp(
                    Settings.GlobalIllumination.DdgiEmissiveTriangleBudget,
                    1,
                    MaxDdgiEmissiveSourceCount)
                : MaxDdgiEmissiveSourceCount;
            var cacheKey = new DdgiEmissiveTableCacheKey(
                scene.Id,
                sceneData.SceneContentRevision,
                _materialManager.MaterialDataRevision,
                triangleSampling,
                triangleBudget);
            bool cacheHit = _ddgiEmissiveTableCache.TryGet(
                cacheKey,
                out DdgiEmissiveTableBuildResult buildResult);
            if (!cacheHit)
            {
                int rebuiltCount = BuildDdgiEmissiveSources(scene, out ulong rebuiltSignature);
                buildResult = new DdgiEmissiveTableBuildResult(
                    rebuiltCount,
                    rebuiltSignature,
                    _ddgiEmissiveTriangleTableStats,
                    _ddgiEmissiveSkippedSkinnedObjectCount,
                    _ddgiEmissiveSkippedSkinnedImportance);
                _ddgiEmissiveTableCache.Store(
                    cacheKey,
                    _ddgiEmissiveSourceScratch.AsSpan(0, rebuiltCount),
                    buildResult);
            }
            else if (!_ddgiEmissiveSourceBufferContentValid)
            {
                _ddgiEmissiveTableCache.CopyPayloadTo(_ddgiEmissiveSourceScratch);
            }

            _ddgiEmissiveTriangleTableStats = buildResult.TriangleStats;
            _ddgiEmissiveSkippedSkinnedObjectCount = buildResult.SkippedSkinnedObjectCount;
            _ddgiEmissiveSkippedSkinnedImportance = buildResult.SkippedSkinnedImportance;
            int count = buildResult.Count;
            ulong signature = buildResult.PayloadSignature;
            bool signatureChanged = signature != _lastDdgiEmissiveSourceSignature;
            if (signatureChanged)
            {
                _ddgiEmissiveSourceRevision++;
                if (_ddgiEmissiveSourceRevision == 0)
                    _ddgiEmissiveSourceRevision = 1;
            }
            _lastDdgiEmissiveSourceSignature = signature;

            _ddgiEmissiveSourceCount = count;
            sceneData.DdgiEmissiveSourceCount = count;
            sceneData.DdgiEmissiveSourceRevision = _ddgiEmissiveSourceRevision;
            PopulateDdgiEmissiveDiagnostics(sceneData, active: true);
            if (count == 0)
                return;
            if (cacheHit && _ddgiEmissiveSourceBufferContentValid)
                return;
            if (!signatureChanged && _ddgiEmissiveSourceBufferContentValid)
                return;

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                _currentCommandBuffer,
                _ddgiEmissiveSourceBuffer,
                _ddgiEmissiveSourceScratch.AsSpan(0, count),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _ddgiEmissiveSourceBufferContentValid = true;
            _ddgiEmissiveSourceUploadCount++;
        }

        private void PopulateDdgiEmissiveDiagnostics(SceneRenderingData sceneData, bool active)
        {
            bool triangleSampling =
                Settings.GlobalIllumination.EffectiveGiEmissiveMeshSampling;
            sceneData.DdgiEmissiveSamplingMode = !active
                ? "Inactive"
                : triangleSampling
                    ? "TriangleAlias"
                    : "ProxyRollback";
            sceneData.DdgiEmissiveTriangleCandidateCount =
                triangleSampling ? _ddgiEmissiveTriangleTableStats.CandidateCount : 0;
            sceneData.DdgiEmissiveTriangleBudget = triangleSampling
                ? Math.Clamp(
                    Settings.GlobalIllumination.DdgiEmissiveTriangleBudget,
                    1,
                    MaxDdgiEmissiveSourceCount)
                : 0;
            sceneData.DdgiEmissiveSkippedEnergyFraction =
                triangleSampling ? _ddgiEmissiveTriangleTableStats.SkippedEnergyFraction : 0.0f;
            sceneData.DdgiEmissiveSkippedSkinnedObjectCount =
                triangleSampling ? _ddgiEmissiveSkippedSkinnedObjectCount : 0;
            sceneData.DdgiEmissiveSkippedSkinnedImportance =
                triangleSampling ? _ddgiEmissiveSkippedSkinnedImportance : 0.0;
            DdgiEmissiveTableCacheDiagnostics cache = _ddgiEmissiveTableCache.Diagnostics;
            sceneData.DdgiEmissiveTableCacheHit = active && cache.LastLookupWasHit ? 1 : 0;
            sceneData.DdgiEmissiveTableCacheHitCount = cache.HitCount;
            sceneData.DdgiEmissiveTableCacheMissCount = cache.MissCount;
            sceneData.DdgiEmissiveTableRebuildCount = cache.RebuildCount;
            sceneData.DdgiEmissiveTableInvalidationCount = cache.InvalidationCount;
            sceneData.DdgiEmissiveTableUploadCount = _ddgiEmissiveSourceUploadCount;
        }

        private int BuildDdgiEmissiveSources(Scene scene, out ulong signature)
        {
            if (Settings.GlobalIllumination.EffectiveGiEmissiveMeshSampling)
                return BuildDdgiEmissiveTriangleSources(scene, out signature);

            // Explicit rollback: legacy bounds proxies and triangle sampling are
            // mutually exclusive, so disabling the feature cannot double energy.
            int count = 0;
            _ddgiEmissiveTriangleTableStats = default;
            _ddgiEmissiveSkippedSkinnedObjectCount = 0;
            _ddgiEmissiveSkippedSkinnedImportance = 0.0;
            signature = HashStart;
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!TryCreateDdgiEmissiveSource(renderObject, out GPUDdgiEmissiveSource source, out float importance, out ulong sourceSignature))
                    continue;

                InsertDdgiEmissiveSource(source, importance, ref count);
                signature = HashAdd(signature, sourceSignature);
            }

            SortDdgiEmissiveSourcesByImportance(count);
            signature = HashAdd(signature, count);
            for (int i = 0; i < count; i++)
            {
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Vertex0Area);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge1AliasProbability);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge2AliasFlags);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].RadianceSelectionProbability);
            }

            return count;
        }

        private int BuildDdgiEmissiveTriangleSources(Scene scene, out ulong signature)
        {
            _ddgiEmissiveSkippedSkinnedObjectCount = 0;
            _ddgiEmissiveSkippedSkinnedImportance = 0.0;
            _ddgiEmissiveExcludedCandidateCount = 0;
            _ddgiEmissiveExcludedImportance = 0.0;
            _ddgiEmissiveRuntimeRecordScanCount = 0;
            int budget = Math.Clamp(
                Settings.GlobalIllumination.DdgiEmissiveTriangleBudget,
                1,
                MaxDdgiEmissiveSourceCount);
            DdgiEmissiveTriangleTableStats retainedStats = DdgiEmissiveTriangleTable.Build(
                EnumerateDdgiEmissiveTriangles(scene),
                _ddgiEmissiveSourceScratch.AsSpan(0, budget));
            _ddgiEmissiveTriangleTableStats = DdgiEmissiveTriangleTable.IncludeExcluded(
                retainedStats,
                _ddgiEmissiveExcludedCandidateCount,
                _ddgiEmissiveExcludedImportance);

            int count = _ddgiEmissiveTriangleTableStats.SelectedCount;
            signature = HashAdd(HashStart, 1u);
            signature = HashAdd(signature, budget);
            signature = HashAdd(signature, _ddgiEmissiveTriangleTableStats.CandidateCount);
            signature = HashAdd(signature, _ddgiEmissiveTriangleTableStats.SkippedEnergyFraction);
            signature = HashAdd(signature, _ddgiEmissiveSkippedSkinnedObjectCount);
            signature = HashAdd(signature, (float)_ddgiEmissiveSkippedSkinnedImportance);
            for (int i = 0; i < count; i++)
            {
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Vertex0Area);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge1AliasProbability);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].Edge2AliasFlags);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].RadianceSelectionProbability);
            }

            return count;
        }

        private IEnumerable<DdgiEmissiveTriangleCandidate> EnumerateDdgiEmissiveTriangles(Scene scene)
        {
            for (int objectIndex = 0; objectIndex < scene.RenderObjects.Count; objectIndex++)
            {
                RenderObject renderObject = scene.RenderObjects[objectIndex];
                if (!renderObject.Enabled ||
                    !renderObject.Visible ||
                    renderObject.Mesh is not MeshHandle meshHandle ||
                    !meshHandle.IsValid ||
                    !TryResolveDdgiEmissiveMaterial(
                        renderObject.Material,
                        renderObject.Name,
                        out DdgiResolvedEmissiveMaterial material,
                        out DdgiEmissiveSourceFlags sourceFlags))
                {
                    continue;
                }

                if (!TryGetDdgiEmissiveGeometry(meshHandle, out MeshTransportGeometry geometry))
                {
                    AddDdgiEmissiveExclusion(1, 1e-12);
                    continue;
                }
                bool skinned = geometry.IsSkinned || renderObject is SkinnedRenderObject;
                if (skinned)
                {
                    _ddgiEmissiveSkippedSkinnedObjectCount++;
                    double skippedImportance = EstimateExcludedDdgiEmissiveImportance(
                        geometry,
                        renderObject.WorldMatrix,
                        material,
                        sourceFlags);
                    _ddgiEmissiveSkippedSkinnedImportance = SaturatingImportanceAdd(
                        _ddgiEmissiveSkippedSkinnedImportance,
                        skippedImportance);
                    AddDdgiEmissiveExclusion(
                        EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                        skippedImportance);
                    continue;
                }

                if (!renderObject.IsStatic)
                    sourceFlags |= DdgiEmissiveSourceFlags.DynamicTransform;
                ulong stableKey = HashAdd(HashAdd(HashStart, objectIndex), renderObject.WorldMatrix);
                foreach (DdgiEmissiveTriangleCandidate candidate in EnumerateDdgiEmissiveInstanceTriangles(
                    geometry,
                    renderObject.WorldMatrix,
                    material,
                    sourceFlags,
                    stableKey))
                {
                    yield return candidate;
                }
            }

            for (int batchIndex = 0; batchIndex < scene.StaticInstanceBatches.Count; batchIndex++)
            {
                StaticInstanceBatch batch = scene.StaticInstanceBatches[batchIndex];
                if (!batch.Visible ||
                    batch.Mesh is not MeshHandle meshHandle ||
                    !meshHandle.IsValid ||
                    !TryResolveDdgiEmissiveMaterial(
                        batch.Material,
                        batch.Name,
                        out DdgiResolvedEmissiveMaterial material,
                        out DdgiEmissiveSourceFlags sourceFlags))
                {
                    continue;
                }
                if (!TryGetDdgiEmissiveGeometry(meshHandle, out MeshTransportGeometry geometry))
                {
                    int invalidInstanceCount = Math.Max(batch.WorldMatrices.Count, 1);
                    AddDdgiEmissiveExclusion(invalidInstanceCount, invalidInstanceCount * 1e-12);
                    continue;
                }

                for (int instanceIndex = 0; instanceIndex < batch.WorldMatrices.Count; instanceIndex++)
                {
                    Matrix4x4 worldMatrix = batch.WorldMatrices[instanceIndex];
                    if (geometry.IsSkinned)
                    {
                        _ddgiEmissiveSkippedSkinnedObjectCount++;
                        double skippedImportance = EstimateExcludedDdgiEmissiveImportance(
                            geometry,
                            worldMatrix,
                            material,
                            sourceFlags);
                        _ddgiEmissiveSkippedSkinnedImportance = SaturatingImportanceAdd(
                            _ddgiEmissiveSkippedSkinnedImportance,
                            skippedImportance);
                        AddDdgiEmissiveExclusion(
                            EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                            skippedImportance);
                        continue;
                    }
                    ulong stableKey = HashAdd(
                        HashAdd(HashAdd(HashStart, batchIndex), instanceIndex),
                        worldMatrix);
                    foreach (DdgiEmissiveTriangleCandidate candidate in EnumerateDdgiEmissiveInstanceTriangles(
                        geometry,
                        worldMatrix,
                        material,
                        sourceFlags,
                        stableKey))
                    {
                        yield return candidate;
                    }
                }
            }
        }

        private IEnumerable<DdgiEmissiveTriangleCandidate> EnumerateDdgiEmissiveInstanceTriangles(
            MeshTransportGeometry geometry,
            Matrix4x4 worldMatrix,
            DdgiResolvedEmissiveMaterial material,
            DdgiEmissiveSourceFlags sourceFlags,
            ulong stableKey)
        {
            ReadOnlyMemory<GPUVertexPositionStream> vertices = geometry.VertexPositions;
            ReadOnlyMemory<uint> indices = geometry.Indices;
            GiPrimitiveTransportProfile? profile = geometry.PrimitiveTransportProfile;
            bool compatible = DdgiCookedEmissiveTransport.TryValidateCompatibility(
                profile,
                material.Definition,
                material.TransportProfile,
                out _);
            if (!compatible && !CanUseUniformAnalyticEmission(material.Definition))
            {
                AddDdgiEmissiveExclusion(
                    EstimateExcludedDdgiEmissiveCandidateCount(geometry),
                    EstimateExcludedDdgiEmissiveImportance(
                        geometry,
                        worldMatrix,
                        material,
                        sourceFlags));
                yield break;
            }

            if (compatible &&
                profile is not null &&
                (profile.EmissiveCandidateTriangleCount > 0 ||
                 !CanUseUniformAnalyticEmission(material.Definition)))
            {
                int cookOmittedCount = Math.Max(
                    profile.EmissiveCandidateTriangleCount - profile.EmissiveTriangles.Length,
                    0);
                AddDdgiEmissiveExclusion(
                    cookOmittedCount,
                    DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                        profile.EmissiveOmittedCookedImportance,
                        material.Definition,
                        worldMatrix,
                        material.DoubleSided));

                int remainingScanCapacity = Math.Max(
                    MaximumDdgiEmissiveRuntimeRecordScans -
                    _ddgiEmissiveRuntimeRecordScanCount,
                    0);
                int scanCount = Math.Min(profile.EmissiveTriangles.Length, remainingScanCapacity);
                double scannedNeutralImportance = 0.0;
                for (int recordIndex = 0; recordIndex < scanCount; recordIndex++)
                {
                    GiPrimitiveEmissiveTriangleRecord record = profile.EmissiveTriangles[recordIndex];
                    scannedNeutralImportance += record.CookedImportance;
                    _ddgiEmissiveRuntimeRecordScanCount++;
                    if (!TryBuildDdgiEmissiveTriangleCandidate(
                            vertices,
                            indices,
                            record.TriangleIndex,
                            worldMatrix,
                            DdgiCookedEmissiveTransport.EvaluateCoveredRadiance(
                                record,
                                material.Definition),
                            sourceFlags,
                            HashAdd(stableKey, record.TriangleIndex),
                            out DdgiEmissiveTriangleCandidate candidate))
                    {
                        AddDdgiEmissiveExclusion(
                            1,
                            DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                                record.CookedImportance,
                                material.Definition,
                                worldMatrix,
                                material.DoubleSided));
                        continue;
                    }
                    yield return candidate;
                }

                int runtimeOmittedCount = profile.EmissiveTriangles.Length - scanCount;
                if (runtimeOmittedCount > 0)
                {
                    double runtimeOmittedNeutralImportance = Math.Max(
                        profile.EmissiveRetainedCookedImportance - scannedNeutralImportance,
                        0.0);
                    AddDdgiEmissiveExclusion(
                        runtimeOmittedCount,
                        DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                            runtimeOmittedNeutralImportance,
                            material.Definition,
                            worldMatrix,
                            material.DoubleSided));
                }
                yield break;
            }

            Vector3 radiance = EvaluateUniformCoveredRadiance(material.Definition);
            int analyticScanCount = Math.Min(
                geometry.TriangleCount,
                Math.Max(
                    MaximumDdgiEmissiveRuntimeRecordScans -
                    _ddgiEmissiveRuntimeRecordScanCount,
                    0));
            for (int triangleIndex = 0; triangleIndex < analyticScanCount; triangleIndex++)
            {
                _ddgiEmissiveRuntimeRecordScanCount++;
                if (TryBuildDdgiEmissiveTriangleCandidate(
                        vertices,
                        indices,
                        triangleIndex,
                        worldMatrix,
                        radiance,
                        sourceFlags,
                        HashAdd(stableKey, triangleIndex),
                        out DdgiEmissiveTriangleCandidate candidate))
                {
                    yield return candidate;
                }
            }
            int analyticOmittedCount = geometry.TriangleCount - analyticScanCount;
            if (analyticOmittedCount > 0)
            {
                double omittedArea = geometry.TriangleCount > 0
                    ? geometry.LocalSurfaceArea * analyticOmittedCount / geometry.TriangleCount
                    : 0.0;
                AddDdgiEmissiveExclusion(
                    analyticOmittedCount,
                    BoundUniformWorldImportance(
                        omittedArea,
                        material.Definition,
                        worldMatrix,
                        material.DoubleSided));
            }
        }

        private static bool TryBuildDdgiEmissiveTriangleCandidate(
            ReadOnlyMemory<GPUVertexPositionStream> vertices,
            ReadOnlyMemory<uint> indices,
            int triangleIndex,
            Matrix4x4 worldMatrix,
            Vector3 radiance,
            DdgiEmissiveSourceFlags sourceFlags,
            ulong stableKey,
            out DdgiEmissiveTriangleCandidate candidate)
        {
            candidate = default;
            int indexBase = triangleIndex * 3;
            if (triangleIndex < 0 || indexBase > indices.Length - 3)
                return false;
            uint i0 = indices.Span[indexBase];
            uint i1 = indices.Span[indexBase + 1];
            uint i2 = indices.Span[indexBase + 2];
            if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
                return false;

            Vector4 p0 = vertices.Span[(int)i0].Position;
            Vector4 p1 = vertices.Span[(int)i1].Position;
            Vector4 p2 = vertices.Span[(int)i2].Position;
            Vector3 v0 = new Vector3(p0.X, p0.Y, p0.Z) * worldMatrix;
            Vector3 v1 = new Vector3(p1.X, p1.Y, p1.Z) * worldMatrix;
            Vector3 v2 = new Vector3(p2.X, p2.Y, p2.Z) * worldMatrix;
            candidate = new DdgiEmissiveTriangleCandidate(
                v0,
                v1,
                v2,
                radiance,
                sourceFlags | DdgiEmissiveSourceFlags.Triangle,
                stableKey);
            return true;
        }

        private bool TryResolveDdgiEmissiveMaterial(
            object? materialReference,
            string ownerName,
            out DdgiResolvedEmissiveMaterial resolved,
            out DdgiEmissiveSourceFlags flags)
        {
            resolved = default;
            flags = DdgiEmissiveSourceFlags.None;
            try
            {
                MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    materialReference,
                    _materialManager.DefaultMaterialHandle,
                    ownerName);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                if (metadata.RenderMode == MaterialRenderMode.Blend ||
                    metadata.IsGeometryDecal ||
                    !metadata.EmitsIntoGi)
                {
                    return false;
                }

                GPUMaterialData gpuMaterial = _materialManager.GetMaterialData(materialHandle);
                GiMaterialTransportFlags transportFlags =
                    (GiMaterialTransportFlags)gpuMaterial.TransportFlags;
                if ((transportFlags & GiMaterialTransportFlags.EmitsIntoGi) == 0)
                {
                    return false;
                }

                MaterialDefinition definition = _materialManager.GetMaterialDefinition(materialHandle);
                GiMaterialTransportProfile transportProfile =
                    _materialManager.GetMaterialTransportProfile(materialHandle);
                float alphaCoverage = metadata.RenderMode == MaterialRenderMode.Mask
                    ? Math.Clamp(gpuMaterial.DdgiMaterialPolicy.Z, 0.0f, 1.0f)
                    : 1.0f;
                Vector3 averageCoveredRadiance = new(
                    Math.Max(gpuMaterial.DdgiAverageEmissive.X, 0.0f),
                    Math.Max(gpuMaterial.DdgiAverageEmissive.Y, 0.0f),
                    Math.Max(gpuMaterial.DdgiAverageEmissive.Z, 0.0f));
                averageCoveredRadiance *= alphaCoverage;
                Vector3 factorRadiance =
                    definition.EmissiveFactor *
                    Math.Clamp(definition.EmissiveStrength, 0.0f, 65504.0f);
                float luminance =
                    0.2126f * factorRadiance.X +
                    0.7152f * factorRadiance.Y +
                    0.0722f * factorRadiance.Z;
                if (!float.IsFinite(luminance) || luminance <= 0.000001f)
                    return false;

                if (metadata.DoubleSided)
                    flags |= DdgiEmissiveSourceFlags.DoubleSided;
                if (metadata.RenderMode == MaterialRenderMode.Mask)
                    flags |= DdgiEmissiveSourceFlags.AlphaCoverageApproximation;
                resolved = new DdgiResolvedEmissiveMaterial(
                    definition,
                    transportProfile,
                    averageCoveredRadiance,
                    metadata.DoubleSided);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        private bool TryGetDdgiEmissiveGeometry(
            MeshHandle meshHandle,
            out MeshTransportGeometry geometry)
        {
            try
            {
                geometry = _meshManager.GetTransportGeometry(meshHandle);
                return geometry.IsValid;
            }
            catch (InvalidOperationException)
            {
                geometry = default;
                return false;
            }
        }

        private static int EstimateExcludedDdgiEmissiveCandidateCount(
            MeshTransportGeometry geometry) =>
            geometry.PrimitiveTransportProfile?.EmissiveCandidateTriangleCount ??
            geometry.TriangleCount;

        private static double EstimateExcludedDdgiEmissiveImportance(
            MeshTransportGeometry geometry,
            Matrix4x4 worldMatrix,
            DdgiResolvedEmissiveMaterial material,
            DdgiEmissiveSourceFlags flags)
        {
            GiPrimitiveTransportProfile? profile = geometry.PrimitiveTransportProfile;
            if (DdgiCookedEmissiveTransport.TryValidateCompatibility(
                    profile,
                    material.Definition,
                    material.TransportProfile,
                    out _) &&
                profile is not null)
            {
                return DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                    profile.EmissiveTotalCookedImportance,
                    material.Definition,
                    worldMatrix,
                    material.DoubleSided);
            }

            // An incompatible or absent texture profile cannot safely reuse a
            // texture-wide mean. Unit texture luminance over the full local
            // area is a conservative, observable skipped-energy bound.
            return BoundUniformWorldImportance(
                geometry.LocalSurfaceArea,
                material.Definition,
                worldMatrix,
                (flags & DdgiEmissiveSourceFlags.DoubleSided) != 0);
        }

        private static bool CanUseUniformAnalyticEmission(MaterialDefinition material) =>
            !material.Emissive.IsBound &&
            (!material.BaseColor.IsBound || material.AlphaMode == MaterialAlphaMode.Opaque);

        private static Vector3 EvaluateUniformCoveredRadiance(MaterialDefinition material)
        {
            float coverage = material.AlphaMode switch
            {
                MaterialAlphaMode.Mask =>
                    material.BaseColorFactor.W >= material.AlphaCutoff ? 1.0f : 0.0f,
                MaterialAlphaMode.Opaque => 1.0f,
                _ => 0.0f
            };
            return material.EmissiveFactor *
                   Math.Clamp(material.EmissiveStrength, 0.0f, 65504.0f) *
                   coverage;
        }

        private static double BoundUniformWorldImportance(
            double localArea,
            MaterialDefinition material,
            Matrix4x4 worldMatrix,
            bool doubleSided) =>
            DdgiCookedEmissiveTransport.BoundOmittedWorldImportance(
                localArea,
                material,
                worldMatrix,
                doubleSided);

        private void AddDdgiEmissiveExclusion(int candidateCount, double importance)
        {
            if (candidateCount > 0)
            {
                _ddgiEmissiveExcludedCandidateCount = (int)Math.Min(
                    (long)_ddgiEmissiveExcludedCandidateCount + candidateCount,
                    int.MaxValue);
            }
            _ddgiEmissiveExcludedImportance = SaturatingImportanceAdd(
                _ddgiEmissiveExcludedImportance,
                importance);
        }

        private static double SaturatingImportanceAdd(double left, double right)
        {
            if (left == double.MaxValue || right == double.MaxValue)
                return double.MaxValue;
            double result = left + right;
            return double.IsFinite(result) ? Math.Max(result, 0.0) : double.MaxValue;
        }

        private readonly record struct DdgiResolvedEmissiveMaterial(
            MaterialDefinition Definition,
            GiMaterialTransportProfile TransportProfile,
            Vector3 AverageCoveredRadiance,
            bool DoubleSided);

        private bool TryCreateDdgiEmissiveSource(
            RenderObject renderObject,
            out GPUDdgiEmissiveSource source,
            out float importance,
            out ulong sourceSignature)
        {
            source = default;
            importance = 0.0f;
            sourceSignature = 0UL;

            if (!TryCreateDdgiTrackedRenderObject(renderObject, out DdgiTrackedRenderObject tracked))
                return false;

            MaterialHandle materialHandle;
            try
            {
                materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name);
                GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                if (!metadata.EmitsIntoGi)
                    return false;
                GiMaterialTransportFlags transportFlags =
                    (GiMaterialTransportFlags)material.TransportFlags;
                if ((transportFlags & GiMaterialTransportFlags.EmissionProfileValid) == 0 ||
                    (transportFlags & GiMaterialTransportFlags.EmitsIntoGi) == 0)
                {
                    return false;
                }
                Vector3 radiance = new(
                    MathF.Max(material.DdgiAverageEmissive.X, 0.0f),
                    MathF.Max(material.DdgiAverageEmissive.Y, 0.0f),
                    MathF.Max(material.DdgiAverageEmissive.Z, 0.0f));
                importance = MathF.Max(material.DdgiAverageEmissive.W, 0.0f);
                if (importance <= 0.0001f)
                    return false;

                BoundingBox bounds = tracked.Bounds;
                Vector3 center = bounds.Center;
                Vector3 size = bounds.Size;
                float objectRadius = MathF.Max(0.05f, size.Length() * 0.5f);
                float affectedRadius = MathF.Max(objectRadius, MathF.Sqrt(importance) * 4.0f);
                source = new GPUDdgiEmissiveSource
                {
                    Vertex0Area = new Vector4(center.X, center.Y, center.Z, affectedRadius),
                    Edge1AliasProbability = new Vector4(radiance.X, radiance.Y, radiance.Z, importance),
                    Edge2AliasFlags = new Vector4(
                        bounds.Min.X,
                        bounds.Min.Y,
                        bounds.Min.Z,
                        BitConverter.UInt32BitsToSingle(
                            (uint)DdgiEmissiveSourceFlags.ProxyRollback <<
                            DdgiEmissiveTriangleTable.FlagsShift)),
                    RadianceSelectionProbability = new Vector4(
                        bounds.Max.X,
                        bounds.Max.Y,
                        bounds.Max.Z,
                        0.0f)
                };

                sourceSignature = HashAdd(HashAdd(tracked.EmissiveSignature, materialHandle.Index), materialHandle.Generation);
                sourceSignature = HashAdd(sourceSignature, source.Vertex0Area);
                sourceSignature = HashAdd(sourceSignature, source.Edge1AliasProbability);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        private void InsertDdgiEmissiveSource(GPUDdgiEmissiveSource source, float importance, ref int count)
        {
            if (count < MaxDdgiEmissiveSourceCount)
            {
                _ddgiEmissiveSourceScratch[count] = source;
                _ddgiEmissiveSourceImportanceScratch[count] = importance;
                count++;
                return;
            }

            int weakestIndex = 0;
            float weakestImportance = _ddgiEmissiveSourceImportanceScratch[0];
            for (int i = 1; i < MaxDdgiEmissiveSourceCount; i++)
            {
                if (_ddgiEmissiveSourceImportanceScratch[i] >= weakestImportance)
                    continue;

                weakestImportance = _ddgiEmissiveSourceImportanceScratch[i];
                weakestIndex = i;
            }

            if (importance <= weakestImportance)
                return;

            _ddgiEmissiveSourceScratch[weakestIndex] = source;
            _ddgiEmissiveSourceImportanceScratch[weakestIndex] = importance;
        }

        private void SortDdgiEmissiveSourcesByImportance(int count)
        {
            for (int i = 1; i < count; i++)
            {
                GPUDdgiEmissiveSource source = _ddgiEmissiveSourceScratch[i];
                float importance = _ddgiEmissiveSourceImportanceScratch[i];
                int j = i - 1;
                while (j >= 0 && _ddgiEmissiveSourceImportanceScratch[j] < importance)
                {
                    _ddgiEmissiveSourceScratch[j + 1] = _ddgiEmissiveSourceScratch[j];
                    _ddgiEmissiveSourceImportanceScratch[j + 1] = _ddgiEmissiveSourceImportanceScratch[j];
                    j--;
                }

                _ddgiEmissiveSourceScratch[j + 1] = source;
                _ddgiEmissiveSourceImportanceScratch[j + 1] = importance;
            }
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

        private IReadOnlyList<DdgiDirtyRegion> CollectDdgiDirtyRegions(
            Scene scene,
            LightFrameSnapshot lightSnapshot,
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes,
            SceneRenderingData sceneData)
        {
            _ddgiDirtyBoundsScratch.Clear();
            _ddgiDirtyRegionScratch.Clear();
            sceneData.VfxDdgiDirtyProbeEventCount = 0;

            if (!Settings.GlobalIllumination.EffectiveUseDdgi &&
                !Settings.GlobalIllumination.EffectiveUseSimpleDdgi)
            {
                ResetDdgiDynamicTracking();
                return _ddgiDirtyRegionScratch;
            }

            _ddgiTrackingFrame++;
            ulong lightSignature = CreateDdgiLightSignature(lightSnapshot);
            ulong volumeSignature = CreateDdgiProbeVolumeSignature(volumes);
            bool hasPreviousSignature = _hasDdgiDynamicSignature;

            if (hasPreviousSignature)
            {
                if (volumeSignature != _lastDdgiProbeVolumeSignature)
                    AddDdgiDirtyRegion(EstimateSceneProbeBounds(scene), 4.0f, DdgiDirtyReason.StreamIn);

                if (lightSignature != _lastDdgiLightSignature)
                    AddDdgiDirtyRegionsForLightChanges(scene, lightSnapshot);
            }

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (renderObject == null ||
                    !TryCreateDdgiTrackedRenderObject(renderObject, out DdgiTrackedRenderObject current))
                    continue;

                if (_ddgiTrackedRenderObjects.TryGetValue(renderObject, out DdgiTrackedRenderObject previous))
                {
                    if (hasPreviousSignature)
                        AddDdgiDirtyRegionsForObjectChange(previous, current);
                }
                else if (hasPreviousSignature)
                {
                    AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.GeometryAdded);
                }

                _ddgiTrackedRenderObjects[renderObject] = current with { LastSeenFrame = _ddgiTrackingFrame };
            }

            foreach (KeyValuePair<RenderObject, DdgiTrackedRenderObject> entry in _ddgiTrackedRenderObjects)
            {
                if (entry.Value.LastSeenFrame == _ddgiTrackingFrame)
                    continue;

                if (hasPreviousSignature)
                    AddDdgiDirtyRegion(entry.Value.Bounds, 1.0f, DdgiDirtyReason.GeometryRemoved);
                _ddgiTrackedRenderObjectRemovalScratch.Add(entry.Key);
            }

            for (int i = 0; i < _ddgiTrackedRenderObjectRemovalScratch.Count; i++)
                _ddgiTrackedRenderObjects.Remove(_ddgiTrackedRenderObjectRemovalScratch[i]);
            _ddgiTrackedRenderObjectRemovalScratch.Clear();
            AddDdgiDirtyRegionsForSustainedVfx(scene, hasPreviousSignature, sceneData);

            _lastDdgiLightSignature = lightSignature;
            _lastDdgiProbeVolumeSignature = volumeSignature;
            StoreLastDdgiLights(lightSnapshot);
            _hasDdgiDynamicSignature = true;
            return _ddgiDirtyRegionScratch;
        }

        private void AddDdgiDirtyRegionsForSustainedVfx(
            Scene scene,
            bool hasPreviousSignature,
            SceneRenderingData sceneData)
        {
            foreach (ParticleEffectInstance instance in scene.ParticleEffects)
            {
                if (!TryCreateDdgiTrackedVfxProxy(instance, out DdgiTrackedVfxProxy current))
                    continue;

                if (_ddgiTrackedVfxProxies.TryGetValue(instance, out DdgiTrackedVfxProxy previous))
                {
                    if (hasPreviousSignature && previous.Signature != current.Signature)
                    {
                        AddDdgiDirtyRegion(Union(previous.Bounds, current.Bounds), 1.0f, DdgiDirtyReason.EmissiveChanged);
                        sceneData.VfxDdgiDirtyProbeEventCount++;
                    }
                }
                else if (hasPreviousSignature)
                {
                    AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.EmissiveChanged);
                    sceneData.VfxDdgiDirtyProbeEventCount++;
                }

                _ddgiTrackedVfxProxies[instance] = current with { LastSeenFrame = _ddgiTrackingFrame };
            }

            foreach (KeyValuePair<ParticleEffectInstance, DdgiTrackedVfxProxy> entry in _ddgiTrackedVfxProxies)
            {
                if (entry.Value.LastSeenFrame == _ddgiTrackingFrame)
                    continue;

                if (hasPreviousSignature)
                {
                    AddDdgiDirtyRegion(entry.Value.Bounds, 1.0f, DdgiDirtyReason.EmissiveChanged);
                    sceneData.VfxDdgiDirtyProbeEventCount++;
                }
                _ddgiTrackedVfxProxyRemovalScratch.Add(entry.Key);
            }

            for (int i = 0; i < _ddgiTrackedVfxProxyRemovalScratch.Count; i++)
                _ddgiTrackedVfxProxies.Remove(_ddgiTrackedVfxProxyRemovalScratch[i]);
            _ddgiTrackedVfxProxyRemovalScratch.Clear();
        }

        private bool TryCreateDdgiTrackedVfxProxy(
            ParticleEffectInstance instance,
            out DdgiTrackedVfxProxy tracked)
        {
            tracked = default;
            if (instance == null || !instance.Visible || !instance.Playing || instance.Stopped)
                return false;

            BoundingBox bounds = default;
            bool hasBounds = false;
            int sustainedEmitterCount = 0;
            ulong signature = HashAdd(HashStart, instance.WorldMatrix);

            IReadOnlyList<ParticleEmitterDefinition> emitters = instance.Effect.Emitters;
            for (int i = 0; i < emitters.Count; i++)
            {
                ParticleEmitterDefinition emitter = emitters[i];
                if (!IsDdgiSustainedEmissiveVfx(emitter))
                    continue;

                BoundingBox emitterBounds = EstimateDdgiVfxEmitterBounds(instance, emitter);
                bounds = hasBounds ? Union(bounds, emitterBounds) : emitterBounds;
                hasBounds = true;
                sustainedEmitterCount++;
                signature = HashAdd(signature, i);
                signature = HashAdd(signature, emitter.SpawnShape.Radius);
                signature = HashAdd(signature, emitter.SpawnShape.Extents);
                signature = HashAdd(signature, emitter.SpawnShape.Length);
                signature = HashAdd(signature, emitter.SpawnRatePerSecond);
                signature = HashAdd(signature, emitter.DurationSeconds);
                signature = HashAdd(signature, emitter.Looping);
                signature = HashAdd(signature, SampleMaxEmissive(emitter));
            }

            if (!hasBounds || sustainedEmitterCount == 0)
                return false;

            tracked = new DdgiTrackedVfxProxy(
                HashAdd(signature, sustainedEmitterCount),
                bounds,
                _ddgiTrackingFrame);
            return true;
        }

        private static bool IsDdgiSustainedEmissiveVfx(ParticleEmitterDefinition emitter)
        {
            if (emitter == null)
                return false;

            float maxEmissive = SampleMaxEmissive(emitter);
            bool transientBurstOnly = !emitter.Looping &&
                emitter.DurationSeconds < 1.0f &&
                emitter.SpawnRatePerSecond <= 0.01f &&
                emitter.BurstCount > 0;
            bool sustained = emitter.Looping ||
                emitter.DurationSeconds >= 1.0f ||
                emitter.SpawnRatePerSecond >= 2.0f;
            return sustained && !transientBurstOnly && maxEmissive >= 1.25f;
        }

        private static float SampleMaxEmissive(ParticleEmitterDefinition emitter)
        {
            return MathF.Max(
                emitter.EmissiveOverLife.Sample(0.0f),
                MathF.Max(
                    emitter.EmissiveOverLife.Sample(0.5f),
                    emitter.EmissiveOverLife.Sample(1.0f)));
        }

        private static BoundingBox EstimateDdgiVfxEmitterBounds(
            ParticleEffectInstance instance,
            ParticleEmitterDefinition emitter)
        {
            Vector3 center = new(
                instance.WorldMatrix.M41,
                instance.WorldMatrix.M42,
                instance.WorldMatrix.M43);
            ParticleSpawnShape shape = emitter.SpawnShape;
            float spawnRadius = MathF.Max(
                shape.Radius,
                MathF.Max(shape.Length * 0.5f, MathF.Max(shape.Extents.X, MathF.Max(shape.Extents.Y, shape.Extents.Z))));
            float velocityRadius = MathF.Max(emitter.InitialVelocityMin.Length(), emitter.InitialVelocityMax.Length()) *
                MathF.Max(0.0f, emitter.LifetimeSeconds.Sample(1.0f));
            float sizeRadius = MathF.Max(emitter.Size.Sample(0.0f), emitter.Size.Sample(1.0f));
            float radius = MathF.Max(0.25f, spawnRadius + velocityRadius + sizeRadius);
            Vector3 r = new(radius);
            return new BoundingBox(center - r, center + r);
        }

        private bool TryCreateDdgiTrackedRenderObject(
            RenderObject renderObject,
            out DdgiTrackedRenderObject tracked)
        {
            tracked = default;

            if (!renderObject.Enabled ||
                !renderObject.Visible ||
                renderObject.Mesh is not MeshHandle meshHandle ||
                !meshHandle.IsValid)
            {
                return false;
            }

            try
            {
                MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
                if (meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                    return false;

                MaterialHandle materialHandle = SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                if (metadata.RenderMode == MaterialRenderMode.Blend || metadata.IsGeometryDecal)
                    return false;

                BoundingBox localBounds = new(ToCoreVector(meshInfo.BoundingBoxMin), ToCoreVector(meshInfo.BoundingBoxMax));
                BoundingBox bounds = SceneDataBuilder.TransformBoundingBox(localBounds, renderObject.WorldMatrix);

                ulong geometrySignature = HashStart;
                geometrySignature = HashAdd(geometrySignature, meshHandle.Index);
                geometrySignature = HashAdd(geometrySignature, meshHandle.Generation);
                geometrySignature = HashAdd(geometrySignature, meshInfo.VertexCount);
                geometrySignature = HashAdd(geometrySignature, meshInfo.IndexCount);
                geometrySignature = HashAdd(geometrySignature, ToCoreVector(meshInfo.BoundingBoxMin));
                geometrySignature = HashAdd(geometrySignature, ToCoreVector(meshInfo.BoundingBoxMax));

                GPUMaterialData materialData = _materialManager.GetMaterialData(materialHandle);
                MaterialAspectRevisions aspectRevisions = _materialManager.GetMaterialAspectRevisions(materialHandle);
                uint profileRevision = _materialManager.GetMaterialTransportProfileRevision(materialHandle.Index);
                ulong materialSignature = CreateDdgiMaterialSignature(
                    materialHandle,
                    materialData,
                    metadata,
                    aspectRevisions,
                    profileRevision);
                ulong emissiveSignature = CreateDdgiEmissiveMaterialSignature(
                    materialData,
                    aspectRevisions.Emission,
                    profileRevision);
                ulong transformSignature = HashAdd(HashStart, renderObject.WorldMatrix);
                tracked = new DdgiTrackedRenderObject(
                    geometrySignature,
                    materialSignature,
                    emissiveSignature,
                    transformSignature,
                    bounds,
                    _ddgiTrackingFrame);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }

        private void AddDdgiDirtyRegionsForObjectChange(
            DdgiTrackedRenderObject previous,
            DdgiTrackedRenderObject current)
        {
            if (previous.GeometrySignature != current.GeometrySignature)
            {
                AddDdgiDirtyRegion(previous.Bounds, 1.0f, DdgiDirtyReason.GeometryRemoved);
                AddDdgiDirtyRegion(current.Bounds, 1.0f, DdgiDirtyReason.GeometryAdded);
                return;
            }

            if (previous.TransformSignature != current.TransformSignature)
                AddDdgiDirtyRegion(Union(previous.Bounds, current.Bounds), 1.0f, DdgiDirtyReason.TransformChanged);

            if (previous.MaterialSignature != current.MaterialSignature)
            {
                DdgiDirtyReason reason = previous.EmissiveSignature != current.EmissiveSignature
                    ? DdgiDirtyReason.EmissiveChanged
                    : DdgiDirtyReason.MaterialChanged;
                AddDdgiDirtyRegion(current.Bounds, 1.0f, reason);
            }
        }

        private static ulong CreateDdgiMaterialSignature(
            MaterialHandle materialHandle,
            GPUMaterialData materialData,
            MaterialRenderMetadata metadata,
            MaterialAspectRevisions aspectRevisions,
            uint profileRevision)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, materialHandle.Index);
            hash = HashAdd(hash, materialHandle.Generation);
            // DDGI consumes every transport aspect below. Hash their monotonic
            // revisions instead of trying to maintain a second, inevitably partial
            // list of texture indices, transforms, extension words, and compiled
            // profile fields here. Keep the compact payload values in the signature
            // as a defensive ABI/backward-compatibility check.
            hash = HashAdd(hash, aspectRevisions.DiffuseTransport);
            hash = HashAdd(hash, aspectRevisions.Emission);
            hash = HashAdd(hash, aspectRevisions.AlphaCoverage);
            hash = HashAdd(hash, aspectRevisions.Sidedness);
            hash = HashAdd(hash, aspectRevisions.ShadingModel);
            hash = HashAdd(hash, profileRevision);
            hash = HashAdd(hash, materialData.PackedMeanGiDirectionalDiffuseBaseRg);
            hash = HashAdd(hash, materialData.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
            hash = HashAdd(hash, materialData.PackedMeanGiDielectricF0Gb);
            hash = HashAdd(hash, materialData.DdgiAverageAlbedo);
            hash = HashAdd(hash, materialData.DdgiAverageEmissive);
            hash = HashAdd(hash, materialData.DdgiMaterialPolicy);
            hash = HashAdd(hash, materialData.Emissive);
            hash = HashAdd(hash, materialData.AlbedoTextureIndex);
            hash = HashAdd(hash, materialData.EmissiveTextureIndex);
            hash = HashAdd(hash, materialData.FeatureFlags);
            hash = HashAdd(hash, (int)metadata.RenderMode);
            hash = HashAdd(hash, metadata.IsGeometryDecal);
            return hash;
        }

        private static ulong CreateDdgiEmissiveMaterialSignature(
            GPUMaterialData materialData,
            uint emissionRevision,
            uint profileRevision)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, emissionRevision);
            hash = HashAdd(hash, profileRevision);
            hash = HashAdd(hash, materialData.Emissive);
            hash = HashAdd(hash, materialData.DdgiAverageEmissive);
            hash = HashAdd(hash, materialData.EmissiveTextureIndex);
            return hash;
        }

        private void AddDdgiDirtyRegionsForLightChanges(Scene scene, LightFrameSnapshot lightSnapshot)
        {
            bool dirtiedWholeScene = false;
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            int count = Math.Min(lightSnapshot.Count, lights.Length);
            int previousCount = _lastDdgiLights.Length;
            int compareCount = Math.Max(count, previousCount);
            for (int i = 0; i < compareCount; i++)
            {
                bool hasCurrent = i < count;
                bool hasPrevious = i < previousCount;
                Light current = hasCurrent ? lights[i] : default;
                Light previous = hasPrevious ? _lastDdgiLights[i] : default;
                if (hasCurrent && hasPrevious && HashAddDdgiLight(HashStart, current) == HashAddDdgiLight(HashStart, previous))
                    continue;

                bool directional = (hasCurrent && current.Type == LightType.Directional) ||
                    (hasPrevious && previous.Type == LightType.Directional);
                if (directional)
                {
                    if (!dirtiedWholeScene)
                    {
                        AddDdgiDirtyRegion(EstimateSceneProbeBounds(scene), 4.0f, DdgiDirtyReason.DirectionalLightChanged);
                        dirtiedWholeScene = true;
                    }
                    continue;
                }

                if (hasPrevious)
                    AddDdgiDirtyRegion(CreateLocalLightBounds(previous), 1.0f, DdgiDirtyReason.LocalLightChanged);
                if (hasCurrent)
                    AddDdgiDirtyRegion(CreateLocalLightBounds(current), 1.0f, DdgiDirtyReason.LocalLightChanged);
            }
        }

        private static BoundingBox CreateLocalLightBounds(Light light)
        {
            float range = MathF.Max(light.Range, 0.0f);
            Vector3 center = ToCoreVector(light.Position);
            Vector3 radius = new(range);
            return new BoundingBox(center - radius, center + radius);
        }

        private void AddDdgiDirtyRegion(BoundingBox bounds, float padding, DdgiDirtyReason reason)
        {
            if (bounds.Max.X < bounds.Min.X || bounds.Max.Y < bounds.Min.Y || bounds.Max.Z < bounds.Min.Z)
                return;

            Vector3 p = new(MathF.Max(0.0f, padding));
            BoundingBox expanded = new(bounds.Min - p, bounds.Max + p);
            _ddgiDirtyBoundsScratch.Add(expanded);
            _ddgiDirtyRegionScratch.Add(new DdgiDirtyRegion(expanded, reason)
            {
                OldWorldBounds = bounds,
                NewWorldBounds = bounds,
                InfluenceBounds = expanded,
                ReasonFlags = 1u << (int)reason,
                Priority = DdgiDirtyReasonPolicy.ResolvePriority(reason)
            });
        }

        private void StoreLastDdgiLights(LightFrameSnapshot lightSnapshot)
        {
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            int count = Math.Min(lightSnapshot.Count, lights.Length);
            if (_lastDdgiLights.Length != count)
                _lastDdgiLights = new Light[count];
            for (int i = 0; i < count; i++)
                _lastDdgiLights[i] = lights[i];
        }

        private void ResetDdgiDynamicTracking()
        {
            _ddgiEmissiveTableCache.Clear();
            _ddgiEmissiveSourceBufferContentValid = false;
            _ddgiDirtyBoundsScratch.Clear();
            _ddgiDirtyRegionScratch.Clear();
            _ddgiTrackedRenderObjects.Clear();
            _ddgiTrackedRenderObjectRemovalScratch.Clear();
            _ddgiTrackedVfxProxies.Clear();
            _ddgiTrackedVfxProxyRemovalScratch.Clear();
            _lastDdgiLights = Array.Empty<Light>();
            _hasDdgiDynamicSignature = false;
        }

        private void PrepareAccelerationStructures(Scene scene, SceneRenderingData sceneData)
        {
            if (_accelerationStructureManager == null)
                return;

            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            bool enabled = gi.Enabled &&
                           gi.EffectiveUseRayQueryBackend;
            bool qualityAllowsStaticStreaming = gi.DdgiQualityTier is
                DdgiQualityTier.DdgiLow or DdgiQualityTier.DdgiMedium;
            bool farFieldCoverageReady = qualityAllowsStaticStreaming &&
                _farFieldClipmapManager?.CoverageReady == true;
            AccelerationStructureFrameStats stats = _accelerationStructureManager.PrepareFrame(
                scene,
                _stagingRing,
                _currentCommandBuffer,
                enabled,
                _gpuTimestamps,
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
                    AllowStaticMemoryCulling: farFieldCoverageReady),
                alphaMaskedTransportEnabled: gi.DdgiAlphaMaskedTransportEnabled);
            sceneData.AccelerationStructureBottomLevelCount = stats.BottomLevelCount;
            sceneData.AccelerationStructureTopLevelInstanceCount = stats.TopLevelInstanceCount;
            sceneData.AccelerationStructureBlasBuildCount = stats.BlasBuildCount;
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
            sceneData.CpuAccelerationStructureTlasBuildMicroseconds = stats.TlasBuildMicroseconds;
            sceneData.CpuAccelerationStructureInstanceUploadMicroseconds = stats.InstanceUploadMicroseconds;
            sceneData.AccelerationStructureFallbackReason = stats.FallbackReason;
        }

        private static ulong CreateDdgiLightSignature(LightFrameSnapshot lightSnapshot)
        {
            ulong hash = HashStart;
            ReadOnlySpan<Light> lights = lightSnapshot.Lights.Span;
            int count = Math.Min(lightSnapshot.Count, lights.Length);
            hash = HashAdd(hash, count);
            for (int i = 0; i < count; i++)
                hash = HashAddDdgiLight(hash, lights[i]);
            return hash;
        }

        internal static ulong CreateSimpleDdgiLightingSignature(LightFrameSnapshot lightSnapshot, uint emissiveSourceRevision)
        {
            ulong hash = CreateDdgiLightSignature(lightSnapshot);
            return HashAdd(hash, emissiveSourceRevision);
        }

        internal static ulong CreateSimpleDdgiEnvironmentSignature(EnvironmentSettings environment)
        {
            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            ulong hash = HashStart;
            hash = HashAdd(hash, environment.Enabled);
            hash = HashAdd(hash, (uint)environment.SourceKind);
            hash = HashAdd(hash, environment.SkyIntensity);
            hash = HashAdd(hash, environment.RotationRadians);
            hash = HashAdd(hash, environment.EnvironmentSize);
            hash = HashAdd(hash, environment.IrradianceSize);
            hash = HashAdd(hash, (uint)environment.TexturePrecision);
            string sourcePath = environment.SourcePath ?? string.Empty;
            hash = HashAdd(hash, sourcePath.Length);
            for (int i = 0; i < sourcePath.Length; i++)
                hash = HashAdd(hash, sourcePath[i]);
            return hash;
        }

        private SimpleDdgiDirtySignature CreateSimpleDdgiDirtySignature(
            Scene scene,
            LightFrameSnapshot lightSnapshot,
            uint emissiveSourceRevision,
            bool farFieldCoverageAvailable)
        {
            ulong lightSignature = CreateDdgiLightSignature(lightSnapshot);
            lightSignature = HashAdd(
                lightSignature,
                CreateSimpleDdgiEnvironmentSignature(Settings.Environment));
            GlobalIlluminationSettings gi = Settings.GlobalIllumination;
            lightSignature = HashAdd(lightSignature, gi.EnvironmentFallbackIntensity);
            lightSignature = HashAdd(lightSignature, gi.DdgiSelfShadowBiasScale);
            lightSignature = HashAdd(lightSignature, gi.DdgiThinWallPolicyEnabled);
            lightSignature = HashAdd(lightSignature, gi.DdgiThinWallLeakClampStrength);
            lightSignature = HashAdd(lightSignature, gi.SimpleDdgiNormalBias);
            lightSignature = HashAdd(lightSignature, gi.SimpleDdgiViewBias);
            lightSignature = HashAdd(lightSignature, gi.SimpleDdgiMaximumWorldBiasMeters);
            lightSignature = HashAdd(lightSignature, gi.SimpleDdgiArchitecturalThicknessMeters);
            lightSignature = HashAdd(lightSignature, gi.FarFieldClipmapEnabled);
            lightSignature = HashAdd(lightSignature, gi.FarFieldPagedEnabled);
            lightSignature = HashAdd(lightSignature, gi.FarFieldForceAll);
            lightSignature = HashAdd(lightSignature, gi.FarFieldSkyVisibilityEnabled);
            lightSignature = HashAdd(lightSignature, gi.FarFieldStartDistance);
            lightSignature = HashAdd(lightSignature, gi.FarFieldMaxTraceSteps);
            lightSignature = HashAdd(lightSignature, farFieldCoverageAvailable);
            ulong emissiveSignature = HashAdd(HashStart, emissiveSourceRevision);
            // Region events are the normal Simple DDGI dynamic path.  Retain the
            // whole-scene signature only as the explicit legacy/validation mode;
            // otherwise hashing every render object merely recreates global dirty
            // boosts that the region scheduler is designed to avoid.
            ulong geometrySignature = !Settings.GlobalIllumination.SimpleDdgiRegionalInvalidationEnabled &&
                                     Settings.GlobalIllumination.SimpleDdgiDynamicGeometryDirtyBoostEnabled
                ? CreateSimpleDdgiDynamicGeometrySignature(scene)
                : HashStart;

            uint reasonFlags = 0u;
            if (_hasSimpleDdgiDirtySignature)
            {
                if (lightSignature != _lastSimpleDdgiLightSignature)
                    reasonFlags |= SimpleDdgiDirtyReasonLight;
                if (emissiveSignature != _lastSimpleDdgiEmissiveSignature)
                    reasonFlags |= SimpleDdgiDirtyReasonEmissive;
                if (geometrySignature != _lastSimpleDdgiDynamicGeometrySignature)
                    reasonFlags |= SimpleDdgiDirtyReasonDynamicGeometry;
            }

            _lastSimpleDdgiLightSignature = lightSignature;
            _lastSimpleDdgiEmissiveSignature = emissiveSignature;
            _lastSimpleDdgiDynamicGeometrySignature = geometrySignature;
            _hasSimpleDdgiDirtySignature = true;

            ulong combined = HashStart;
            combined = HashAdd(combined, lightSignature);
            combined = HashAdd(combined, emissiveSignature);
            combined = HashAdd(combined, geometrySignature);
            return new SimpleDdgiDirtySignature(combined, reasonFlags);
        }

        private ulong CreateSimpleDdgiDynamicGeometrySignature(Scene scene)
        {
            ulong hash = HashStart;
            int count = 0;
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!TryCreateDdgiTrackedRenderObject(renderObject, out DdgiTrackedRenderObject tracked))
                    continue;

                hash = HashAdd(hash, tracked.GeometrySignature);
                hash = HashAdd(hash, tracked.MaterialSignature);
                hash = HashAdd(hash, tracked.EmissiveSignature);
                hash = HashAdd(hash, tracked.TransformSignature);
                hash = HashAdd(hash, tracked.Bounds.Min);
                hash = HashAdd(hash, tracked.Bounds.Max);
                count++;
            }

            return HashAdd(hash, count);
        }

        private static ulong HashAddDdgiLight(ulong hash, Light light)
        {
            hash = HashAdd(hash, (int)light.Type);
            hash = HashAdd(hash, QuantizeForHash(light.Intensity, 0.01f));
            hash = HashAdd(hash, QuantizeForHash(light.Color.X, 0.01f));
            hash = HashAdd(hash, QuantizeForHash(light.Color.Y, 0.01f));
            hash = HashAdd(hash, QuantizeForHash(light.Color.Z, 0.01f));
            hash = HashAdd(hash, light.CastsShadows);
            hash = HashAdd(hash, QuantizeForHash(light.ShadowStrength, 0.01f));

            if (light.Type == LightType.Directional)
            {
                hash = HashAdd(hash, QuantizeForHash(light.Direction.X, 0.0025f));
                hash = HashAdd(hash, QuantizeForHash(light.Direction.Y, 0.0025f));
                return HashAdd(hash, QuantizeForHash(light.Direction.Z, 0.0025f));
            }

            hash = HashAdd(hash, QuantizeForHash(light.Position.X, 0.05f));
            hash = HashAdd(hash, QuantizeForHash(light.Position.Y, 0.05f));
            hash = HashAdd(hash, QuantizeForHash(light.Position.Z, 0.05f));
            hash = HashAdd(hash, QuantizeForHash(light.Range, 0.05f));
            if (light.Type == LightType.Spot)
            {
                hash = HashAdd(hash, QuantizeForHash(light.Direction.X, 0.005f));
                hash = HashAdd(hash, QuantizeForHash(light.Direction.Y, 0.005f));
                hash = HashAdd(hash, QuantizeForHash(light.Direction.Z, 0.005f));
                hash = HashAdd(hash, QuantizeForHash(light.SpotAngle, 0.0025f));
            }

            return hash;
        }

        private static int QuantizeForHash(float value, float step)
        {
            if (!float.IsFinite(value))
                return 0;
            if (!float.IsFinite(step) || step <= 0.0f)
                return (int)MathF.Round(value);

            float quantized = MathF.Round(value / step);
            if (quantized <= int.MinValue)
                return int.MinValue;
            if (quantized >= int.MaxValue)
                return int.MaxValue;
            return (int)quantized;
        }

        private static ulong CreateDdgiProbeVolumeSignature(IReadOnlyList<GlobalIlluminationProbeVolume> volumes)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, volumes.Count);
            for (int i = 0; i < volumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? volume = volumes[i];
                if (volume == null)
                {
                    hash = HashAdd(hash, 0);
                    continue;
                }

                hash = HashAdd(hash, volume.Enabled);
                hash = HashAdd(hash, volume.Origin);
                hash = HashAdd(hash, volume.Size);
                hash = HashAdd(hash, volume.ProbeCountX);
                hash = HashAdd(hash, volume.ProbeCountY);
                hash = HashAdd(hash, volume.ProbeCountZ);
                hash = HashAdd(hash, volume.NormalBias);
                hash = HashAdd(hash, volume.ViewBias);
                hash = HashAdd(hash, volume.MaxRayDistance);
                hash = HashAdd(hash, volume.Intensity);
                hash = HashAdd(hash, volume.Hysteresis);
                hash = HashAdd(hash, volume.RaysPerProbe);
                hash = HashAdd(hash, volume.DirtyRaysPerProbe);
                hash = HashAdd(hash, volume.MaxProbeUpdatesPerFrame);
            }

            return hash;
        }

        private static BoundingBox Union(BoundingBox left, BoundingBox right) =>
            new(Vector3.Min(left.Min, right.Min), Vector3.Max(left.Max, right.Max));

        private static Vector3 ToCoreVector(System.Numerics.Vector3 value) =>
            new(value.X, value.Y, value.Z);

        private static BoundingBox EstimateSceneProbeBounds(Scene scene)
        {
            return DdgiFrameLayoutBuilder.EstimateSceneProbeBounds(scene);
        }

        private static void ApplyCompletedGpuCounters(SceneRenderingData sceneData, GpuMeshletCounters counters)
        {
            int sceneSubmissionHiZTested = sceneData.ForwardOcclusionTestedMeshletsGpu;
            int sceneSubmissionHiZCulled = sceneData.ForwardOcclusionCulledMeshletsGpu;
            sceneData.DepthTaskInvocations = counters.DepthCandidates;
            sceneData.DepthFrustumCulledMeshletsGpu = counters.DepthFrustumCulled;
            sceneData.DepthEmittedMeshletsGpu = counters.DepthEmitted;
            sceneData.ForwardTaskInvocations = counters.ForwardCandidates;
            sceneData.ForwardFrustumCulledMeshletsGpu = counters.ForwardFrustumCulled;
            sceneData.ForwardOcclusionTestedMeshletsGpu = Math.Max(counters.ForwardOcclusionTested, sceneSubmissionHiZTested);
            sceneData.ForwardOcclusionCulledMeshletsGpu = Math.Max(counters.ForwardOcclusionCulled, sceneSubmissionHiZCulled);
            sceneData.ForwardEmittedMeshletsGpu = counters.ForwardEmitted;
        }

        private static void ApplyCompletedSsgiCounters(SceneRenderingData sceneData, GpuMeshletCounters counters)
        {
            sceneData.SsgiRejectedHistoryPixelCount = counters.SsgiRejectedHistoryPixels;
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
            sceneData.DdgiForwardEstimateSampledIrradianceLuminance = Math.Max(counters.SampledIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiForwardEstimateRawDiffuseLuminance = counters.RawDiffuseLuminanceAverage;
            sceneData.DdgiForwardEstimateFinalDiffuseLuminance = counters.FinalDiffuseLuminanceAverage;
            sceneData.DdgiForwardEstimateEnvironmentFallbackWeight = Math.Clamp(counters.EnvironmentFallbackWeightAverage * 4.0f, 0.0f, 4.0f);
            sceneData.DdgiAverageSpatialCoverageEstimate = Math.Clamp(counters.SpatialCoverageAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageSupportCoverageEstimate = Math.Clamp(counters.SupportCoverageAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageDataConfidenceEstimate = Math.Clamp(counters.DataConfidenceAverage, 0.0f, 1.0f);
            sceneData.DdgiAverageVisibilityConfidenceEstimate = Math.Clamp(counters.VisibilityConfidenceAverage, 0.0f, 1.0f);
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
            sceneData.DdgiClipmapInfoPrimaryEdgeFadeAverage = Math.Clamp(counters.ClipmapInfoPrimaryEdgeFadeAverage, 0.0f, 1.0f);
            sceneData.DdgiClipmapInfoPrimaryBlendWeightAverage = Math.Clamp(counters.ClipmapInfoPrimaryBlendWeightAverage, 0.0f, 1.0f);
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
            sceneData.DdgiTraceEnergyDirectLuminanceAverage = Math.Max(counters.TraceEnergyDirectLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyEmissiveLuminanceAverage = Math.Max(counters.TraceEnergyEmissiveLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyStableLuminanceAverage = Math.Max(counters.TraceEnergyStableLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergySkyLuminanceAverage = Math.Max(counters.TraceEnergySkyLuminanceAverage, 0.0f);
            sceneData.DdgiTraceEnergyHitZeroDirectCount = counters.TraceEnergyHitZeroDirectCount;
            sceneData.DdgiTraceEnergyHitWithDirectCount = counters.TraceEnergyHitWithDirectCount;
            sceneData.DdgiTraceEnergyDirectNoShadowLuminanceAverage = Math.Max(counters.TraceEnergyDirectNoShadowLuminanceAverage, 0.0f);
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
            sceneData.DdgiBlendEnergyIrradianceLuminanceAverage = Math.Max(counters.BlendEnergyIrradianceLuminanceAverage, 0.0f);
            sceneData.DdgiBlendEnergyConfidenceAverage = Math.Clamp(counters.BlendEnergyConfidenceAverage, 0.0f, 1.0f);
            sceneData.DdgiBlendEnergyLowConfidenceCount = counters.BlendEnergyLowConfidenceCount;
            sceneData.DdgiBlendEnergyNonzeroIrradianceCount = counters.BlendEnergyNonzeroIrradianceCount;
            sceneData.DdgiBlendEnergyNonFiniteIrradianceCount = counters.BlendEnergyNonFiniteIrradianceCount;
            sceneData.DdgiBlendEnergyFireflySuppressedCount = counters.BlendEnergyFireflySuppressedCount;
            sceneData.SimpleDdgiTransportEnergySampleCount = counters.SimpleDdgiTransportEnergySampleCount;
            sceneData.SimpleDdgiTransportSourceCacheHitCount = counters.SimpleDdgiTransportSourceCacheHitCount;
            sceneData.SimpleDdgiTransportSourceCacheMissCount = counters.SimpleDdgiTransportSourceCacheMissCount;
            sceneData.SimpleDdgiTransportBounceLuminanceAverage = Math.Max(counters.SimpleDdgiTransportBounceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiTransportSourceLuminanceAverage = Math.Max(counters.SimpleDdgiTransportSourceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiTransportTotalLuminanceAverage = Math.Max(counters.SimpleDdgiTransportTotalLuminanceAverage, 0.0f);
            sceneData.DdgiForwardGatherFallbackUsed = Math.Max(sceneData.DdgiForwardGatherFallbackUsed, checked((int)Math.Min(int.MaxValue, counters.ShaderGatherFallbackAttemptCount)));
            if (counters.FastGatherAttemptCount > counters.FastGatherAcceptedCount &&
                counters.ShaderGatherFallbackAttemptCount == 0)
            {
                uint disabledCount = counters.FastGatherAttemptCount - counters.FastGatherAcceptedCount;
                sceneData.DdgiForwardGatherFallbackDisabled = Math.Max(sceneData.DdgiForwardGatherFallbackDisabled, checked((int)Math.Min(int.MaxValue, disabledCount)));
            }
            sceneData.DdgiVisibilityMomentMeanAverage = Math.Max(counters.VisibilityMomentMeanAverage, 0.0f);
            sceneData.DdgiVisibilityMomentVarianceAverage = Math.Max(counters.VisibilityMomentVarianceAverage, 0.0f);
            sceneData.DdgiVisibilityProbeDistanceAverage = Math.Max(counters.VisibilityProbeDistanceAverage, 0.0f);
            sceneData.DdgiVisibilityMomentSampleCount = counters.VisibilityMomentSampleCount;
            sceneData.DdgiVisibilityLargeDistanceMarginCount = counters.VisibilityLargeDistanceMarginCount;
            sceneData.DdgiVisibilityZeroTransportCount = counters.VisibilityZeroTransportCount;
            sceneData.DdgiVisibilityZeroTransportWithIrradianceCount = counters.VisibilityZeroTransportWithIrradianceCount;
            sceneData.DdgiAverageEffectiveContributionEstimate = Math.Clamp(counters.EffectiveWeightAverage, 0.0f, 1.0f);
        }

        private static void ApplyCompletedDdgiInvestigationCounters(
            SceneRenderingData sceneData,
            DdgiInvestigationCounters counters)
        {
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
                sceneData.DdgiBlackFrameMovementClass = DdgiCameraMovementClass.None;
                ApplySimpleDdgiVolumeGatherCounters(sceneData, counters);
                return;
            }

            sceneData.DdgiInvestigationCountersReadbackValid = 1;
            sceneData.SimpleDdgiFreshAtlasForwardSampleCount = counters.FreshAtlasForwardSampleCount;
            sceneData.SimpleDdgiZeroIrradianceSampleCount = counters.SimpleZeroIrradianceSampleCount;
            sceneData.SimpleDdgiNonzeroIrradianceSampleCount = counters.SimpleNonzeroIrradianceSampleCount;
            sceneData.SimpleDdgiAverageSampledIrradianceLuminance = Math.Max(counters.SimpleSampledIrradianceLuminanceAverage, 0.0f);
            sceneData.SimpleDdgiAverageVisibility = Math.Clamp(counters.SimpleVisibilityAverage, 0.0f, 1.0f);
            sceneData.SimpleDdgiLowVisibilitySampleCount = counters.SimpleLowVisibilitySampleCount;
            sceneData.SimpleDdgiGatherSampleCount = counters.SimpleGatherCount;
            sceneData.SimpleDdgiSecondVolumeGatherCount = counters.SimpleSecondVolumeGatherCount;
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
            sceneData.DdgiSimpleTraceTlasUnavailableFrameCount = Math.Max(sceneData.DdgiSimpleTraceTlasUnavailableFrameCount, counters.SimpleTraceTlasUnavailableFrameCount);
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
            sceneData.DdgiBlackFrameMovementClass = DdgiCameraMovementClass.None;
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

            CopyCounterArray(counters.PrimarySelectionCounts, sceneData.DirectionalShadowReceiverPrimarySelectionCounts);
            CopyCounterArray(counters.ProjectionRejectedCounts, sceneData.DirectionalShadowReceiverProjectionRejectedCounts);
            CopyCounterArray(counters.UvDepthRejectedCounts, sceneData.DirectionalShadowReceiverUvDepthRejectedCounts);
            CopyCounterArray(counters.FallbackCounts, sceneData.DirectionalShadowReceiverFallbackCounts);
            CopyCounterArray(counters.TransitionBlendCounts, sceneData.DirectionalShadowReceiverTransitionBlendCounts);
            CopyCounterArray(counters.PrimaryResolvedCounts, sceneData.DirectionalShadowReceiverPrimaryResolvedCounts);
            CopyCounterArray(counters.ClearDepthFootprintCounts, sceneData.DirectionalShadowReceiverClearDepthFootprintCounts);
            CopyCounterArray(counters.PrimaryFullyLitCounts, sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts);
            CopyCounterArray(counters.PrimaryPartiallyShadowedCounts, sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts);
            CopyCounterArray(counters.PrimaryFullyShadowedCounts, sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts);
            CopyCounterArray(counters.FinalFullyLitCounts, sceneData.DirectionalShadowReceiverFinalFullyLitCounts);
            CopyCounterArray(counters.FinalPartiallyShadowedCounts, sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts);
            CopyCounterArray(counters.FinalFullyShadowedCounts, sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts);
            CopyFloatArray(counters.AverageReceiverDepths, sceneData.DirectionalShadowReceiverAverageDepths);
            CopyFloatArray(counters.AverageMinimumSampledDepths, sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths);
            CopyFloatArray(counters.AverageMaximumSampledDepths, sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths);
        }

        private static void ApplySimpleDdgiVolumeGatherCounters(
            SceneRenderingData sceneData,
            DdgiInvestigationCounters counters)
        {
            IReadOnlyList<uint>? primaryCounts = counters.SimpleVolumePrimaryGatherCounts;
            IReadOnlyList<uint>? sampledCounts = counters.SimpleVolumeSampledGatherCounts;
            for (int i = 0; i < sceneData.DdgiVolumeDiagnostics.Count; i++)
            {
                DdgiVolumeDiagnosticsEntry entry = sceneData.DdgiVolumeDiagnostics[i];
                int volumeIndex = entry.VolumeIndex;
                bool countersValid = counters.ReadbackValid != 0 &&
                    primaryCounts != null &&
                    sampledCounts != null &&
                    (uint)volumeIndex < (uint)primaryCounts.Count &&
                    (uint)volumeIndex < (uint)sampledCounts.Count;
                sceneData.DdgiVolumeDiagnostics[i] = entry with
                {
                    GatherCountersReadbackValid = countersValid ? 1 : 0,
                    PrimaryGatherCount = countersValid ? primaryCounts![volumeIndex] : 0u,
                    SampledGatherCount = countersValid ? sampledCounts![volumeIndex] : 0u
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

        private static void ApplyCompletedGpuParticleCounters(SceneRenderingData sceneData, GpuParticleCounterSnapshot counters)
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
                sceneData.ForwardOcclusionTestedMeshletsGpu = ClampUIntToInt(counters.HiZTestedCount);
                sceneData.ForwardOcclusionCulledMeshletsGpu = ClampUIntToInt(counters.HiZRejectedCount);
                sceneData.SceneSubmissionGpuIndirectMeshletTaskCount = sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                    ? ClampUIntToInt(counters.EmittedCount)
                    : 0;
                sceneData.SceneSubmissionGpuLod0EmittedCount = ClampUIntToInt(counters.Lod0EmittedCount);
                sceneData.SceneSubmissionGpuLod1EmittedCount = ClampUIntToInt(counters.Lod1EmittedCount);
                sceneData.SceneSubmissionGpuLod2EmittedCount = ClampUIntToInt(counters.Lod2EmittedCount);
                sceneData.SceneSubmissionGpuMissingLodFallbackCount = ClampUIntToInt(counters.MissingLodFallbackCount);
                sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount =
                    ClampUIntToInt(counters.DirectionalShadowLodFallbackCount);
                sceneData.SceneSubmissionGpuDepthSolidCandidateCount = ClampUIntToInt(counters.SolidDepthCandidateCount);
                sceneData.SceneSubmissionGpuDepthMaskedCandidateCount = ClampUIntToInt(counters.MaskedDepthCandidateCount);
                sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount = ClampUIntToInt(counters.SolidDepthEmittedCount);
                sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount = ClampUIntToInt(counters.MaskedDepthEmittedCount);
                sceneData.SceneSubmissionGpuDepthOverflowCount = ClampUlongToInt(
                    (ulong)counters.SolidDepthOverflowCount + counters.MaskedDepthOverflowCount);
                ApplyDirectionalShadowCompactionCounters(sceneData, counters);
            }

        }

        private static void ApplyCompletedForwardVisibilityCounters(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot counters)
        {
            if (!counters.IsValid)
                return;

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
            CopyCounterArray(counters.DirectionalStaticShadowCandidateCounts, sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts);
            CopyCounterArray(counters.DirectionalStaticShadowEmittedCounts, sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts);
            CopyCounterArray(counters.DirectionalStaticShadowRejectedCounts, sceneData.SceneSubmissionGpuDirectionalStaticShadowRejectedCounts);
            CopyCounterArray(counters.DirectionalStaticShadowOverflowCounts, sceneData.SceneSubmissionGpuDirectionalStaticShadowOverflowCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowCandidateCounts, sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowEmittedCounts, sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowRejectedCounts, sceneData.SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts);
            CopyCounterArray(counters.DirectionalDynamicShadowOverflowCounts, sceneData.SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts);

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

        private static int ClampUlongToInt(ulong value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int CalculateSpotShadowMeshletLightTests(SceneRenderingData sceneData)
        {
            int selectedSpotLights = Math.Max(0, sceneData.SpotShadowSelectedCount);
            int meshlets = Math.Max(0, sceneData.LocalStaticShadowMeshletCount) +
                Math.Max(0, sceneData.LocalDynamicShadowMeshletCount);
            return SaturatingMultiply(selectedSpotLights, meshlets);
        }

        private static int CalculatePointShadowMeshletFaceTests(SceneRenderingData sceneData)
        {
            int renderedFaces = Math.Max(0, sceneData.PointShadowRenderedFaceCount);
            int meshlets = Math.Max(0, sceneData.LocalStaticShadowMeshletCount) +
                Math.Max(0, sceneData.LocalDynamicShadowMeshletCount);
            return SaturatingMultiply(renderedFaces, meshlets);
        }

        private static bool IsSpotShadowGpuCompactionJustified(
            SceneRenderingData sceneData,
            int meshletLightTests)
        {
            return sceneData.SpotShadowsEnabled &&
                   sceneData.SpotShadowSelectedCount > 0 &&
                   !sceneData.SpotShadowRecordSkipped &&
                   sceneData.CpuSpotShadowRecordMicroseconds >= LocalShadowGpuCompactionRecordThresholdMicroseconds &&
                   meshletLightTests >= LocalShadowGpuCompactionWorkThreshold;
        }

        private static bool IsPointShadowGpuCompactionJustified(
            SceneRenderingData sceneData,
            int meshletFaceTests)
        {
            return sceneData.PointShadowsEnabled &&
                   sceneData.PointShadowSelectedCount > 0 &&
                   !sceneData.PointShadowRecordSkipped &&
                   sceneData.CpuPointShadowRecordMicroseconds >= LocalShadowGpuCompactionRecordThresholdMicroseconds &&
                   meshletFaceTests >= LocalShadowGpuCompactionWorkThreshold;
        }

        private static string BuildLocalShadowGpuCompactionStatus(
            SceneRenderingData sceneData,
            int spotShadowMeshletLightTests,
            int pointShadowMeshletFaceTests,
            bool spotShadowGpuCompactionJustified,
            bool pointShadowGpuCompactionJustified)
        {
            if (spotShadowGpuCompactionJustified)
            {
                return
                    $"spot candidate: cpu={sceneData.CpuSpotShadowRecordMicroseconds}us tests={spotShadowMeshletLightTests}; CPU fallback active until GPU spot-list path is validated.";
            }

            if (pointShadowGpuCompactionJustified)
            {
                return
                    $"point candidate: cpu={sceneData.CpuPointShadowRecordMicroseconds}us tests={pointShadowMeshletFaceTests}; deferred until spot-list GPU path validates.";
            }

            if (sceneData.SpotShadowRecordSkipped && sceneData.PointShadowRecordSkipped)
                return "not justified: local shadow command recording was skipped by stable signatures.";

            long localShadowCpuRecordMicroseconds =
                sceneData.CpuSpotShadowRecordMicroseconds + sceneData.CpuPointShadowRecordMicroseconds;
            int localShadowWork = Math.Max(spotShadowMeshletLightTests, pointShadowMeshletFaceTests);
            return
                $"not justified: cpu={localShadowCpuRecordMicroseconds}us tests={localShadowWork}, thresholds={LocalShadowGpuCompactionRecordThresholdMicroseconds}us/{LocalShadowGpuCompactionWorkThreshold}; CPU fallback active.";
        }

        private static string BuildLocalShadowOverflowSummary(
            bool spotShadowGpuCompactionJustified,
            bool pointShadowGpuCompactionJustified)
        {
            return spotShadowGpuCompactionJustified || pointShadowGpuCompactionJustified
                ? "none: local shadow GPU compaction is not enabled, so CPU fallback has no GPU output overflow."
                : string.Empty;
        }

        private static int SaturatingMultiply(int left, int right)
        {
            long product = (long)Math.Max(0, left) * Math.Max(0, right);
            return product > int.MaxValue ? int.MaxValue : (int)product;
        }

        private static DirectionalShadowRuntimeDiagnostics CreateDirectionalShadowRuntimeDiagnostics(
            SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled)
                return DirectionalShadowRuntimeDiagnostics.Empty;

            int cascadeCount = Math.Min(
                Math.Max(0, sceneData.DirectionalShadowCascadeCount),
                ShadowSettings.MaxDirectionalCascades);
            float[] splits = new float[cascadeCount];
            for (int cascade = 0; cascade < cascadeCount; cascade++)
                splits[cascade] = GetDirectionalShadowSplit(sceneData.ShadowData, cascade);

            DirectionalShadowReceiverCounters receiverCounters =
                sceneData.DirectionalShadowReceiverCountersReadbackValid != 0
                    ? new DirectionalShadowReceiverCounters(
                        ReadbackValid: 1,
                        PrimarySelectionCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimarySelectionCounts),
                        ProjectionRejectedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverProjectionRejectedCounts),
                        UvDepthRejectedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverUvDepthRejectedCounts),
                        FallbackCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFallbackCounts),
                        TransitionBlendCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverTransitionBlendCounts),
                        PrimaryResolvedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryResolvedCounts),
                        ClearDepthFootprintCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverClearDepthFootprintCounts),
                        PrimaryFullyLitCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryFullyLitCounts),
                        PrimaryPartiallyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryPartiallyShadowedCounts),
                        PrimaryFullyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverPrimaryFullyShadowedCounts),
                        FinalFullyLitCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalFullyLitCounts),
                        FinalPartiallyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalPartiallyShadowedCounts),
                        FinalFullyShadowedCounts: CopyDiagnosticCountersAsUInt(sceneData.DirectionalShadowReceiverFinalFullyShadowedCounts),
                        AverageReceiverDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageDepths),
                        AverageMinimumSampledDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageMinimumSampledDepths),
                        AverageMaximumSampledDepths: CopyDiagnosticValues(sceneData.DirectionalShadowReceiverAverageMaximumSampledDepths),
                        UnresolvedCount: unchecked((uint)Math.Max(0, sceneData.DirectionalShadowReceiverUnresolvedCount)))
                    : DirectionalShadowReceiverCounters.Empty;

            return new DirectionalShadowRuntimeDiagnostics(
                Enabled: 1,
                ConfiguredMaxDistance: sceneData.DirectionalShadowMaxDistance,
                EffectiveNearDistance: sceneData.ShadowData.CascadeTransitionData.Y,
                EffectiveFarDistance: sceneData.ShadowData.CascadeTransitionData.Z,
                CascadeBlendFraction: sceneData.ShadowData.CascadeTransitionData.X,
                CascadeSplits: splits,
                StaticCacheActiveMask: sceneData.DirectionalShadowStaticCacheActiveMask,
                StaticCacheValidMask: sceneData.DirectionalShadowStaticCacheValidMask,
                StaticCacheRefreshMask: sceneData.DirectionalShadowStaticCacheRefreshMask,
                StaticCacheReuseMask: sceneData.DirectionalShadowStaticCacheReuseMask,
                StaticCandidateCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts),
                StaticEmittedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts),
                StaticRejectedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowRejectedCounts),
                StaticOverflowCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalStaticShadowOverflowCounts),
                DynamicCandidateCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts),
                DynamicEmittedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts),
                DynamicRejectedCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowRejectedCounts),
                DynamicOverflowCounts: CopyDiagnosticCounters(sceneData.SceneSubmissionGpuDirectionalDynamicShadowOverflowCounts),
                ConservativeLodFallbackCount: sceneData.SceneSubmissionGpuDirectionalShadowLodFallbackCount,
                ReceiverCounters: receiverCounters);
        }

        private static float GetDirectionalShadowSplit(in GPUShadowData shadowData, int cascade)
        {
            return cascade switch
            {
                0 => shadowData.CascadeSplits.X,
                1 => shadowData.CascadeSplits.Y,
                2 => shadowData.CascadeSplits.Z,
                _ => shadowData.CascadeSplits.W
            };
        }

        private static int[] CopyDiagnosticCounters(int[] source)
        {
            var copy = new int[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static uint[] CopyDiagnosticCountersAsUInt(int[] source)
        {
            var copy = new uint[source.Length];
            for (int i = 0; i < source.Length; i++)
                copy[i] = unchecked((uint)Math.Max(0, source[i]));
            return copy;
        }

        private static float[] CopyDiagnosticValues(float[] source)
        {
            var copy = new float[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static string BuildDirectionalShadowCompactionSummary(SceneRenderingData sceneData)
        {
            int cascadeCount = Math.Min(
                Math.Max(0, sceneData.DirectionalShadowCascadeCount),
                ShadowSettings.MaxDirectionalCascades);
            if (cascadeCount == 0)
                return string.Empty;

            string summary = string.Empty;
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                if (summary.Length > 0)
                    summary += ", ";
                summary +=
                    $"c{cascade}:s={sceneData.SceneSubmissionGpuDirectionalStaticShadowEmittedCounts[cascade]}/{sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade]} " +
                    $"d={sceneData.SceneSubmissionGpuDirectionalDynamicShadowEmittedCounts[cascade]}/{sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade]}";
            }

            return summary;
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
            sceneData.FoliageDdgiSampleCount = checked((int)(counters.VisibleClusterCount + counters.VisibleMeshletDrawCount));
            sceneData.FoliageMeshletDrawOverflowCount = checked((int)counters.MeshletDrawOverflowCount);
            sceneData.FoliageFarImpostorVisibleCount = checked((int)counters.FarImpostorVisibleCount);
            sceneData.FoliageOverflowCount = checked(sceneData.FoliageOverflowCount + sceneData.FoliageMeshletDrawOverflowCount);
        }

        public void Resize(int width, int height)
        {
            ThrowIfDisposalStarted();
            _swapchainNeedsRecreate = true;
            if (width <= 0 || height <= 0 || _frameInProgress)
                return;

            if (RecreateSwapchain())
                _swapchainNeedsRecreate = false;

            // Update camera aspect ratio if camera is provided
            // (Camera aspect ratio should be updated by the caller)
        }

        private void EnsureRenderTargetProfile()
        {
            if (_renderTargets == null)
                return;

            bool aoEnabled = Settings.AmbientOcclusion.Enabled;
            float ambientOcclusionResolutionScale = Settings.AmbientOcclusion.ResolutionScale;
            AntiAliasingMode aaMode = Settings.AntiAliasing.EffectiveMode;
            bool motionVectorTargetEnabled = NeedsMotionVectors(Settings);
            int bloomMipCount = Settings.Bloom.MipCount;
            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool weightedOitTargetEnabled = IsWeightedOitTargetEnabled(Settings);
            bool globalIlluminationTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
            bool materialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            bool forwardAttachmentProfileChanged =
                _lastGlobalIlluminationTargetEnabled != globalIlluminationTargetEnabled ||
                _lastMaterialTransportProvenanceTargetEnabled !=
                materialTransportProvenanceTargetEnabled;
            float globalIlluminationResolutionScale = Settings.GlobalIllumination.ResolutionScale;
            DynamicResolutionScaleDecision scaleDecision = ResolveSceneResolutionScaleDecision();
            float effectiveResolutionScale = scaleDecision.CommittedScale;
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, effectiveResolutionScale);
            bool featureTargetsChanged =
                _lastAmbientOcclusionTargetEnabled != aoEnabled ||
                MathF.Abs(_lastAmbientOcclusionResolutionScale - ambientOcclusionResolutionScale) > 0.0001f ||
                _lastAntiAliasingTargetMode != aaMode ||
                _lastMotionVectorTargetEnabled != motionVectorTargetEnabled ||
                _lastTransparencyTargetMode != Settings.Transparency.Mode ||
                _lastBloomTargetMipCount != bloomMipCount ||
                _lastFogTargetEnabled != fogTargetEnabled ||
                _lastGlobalIlluminationTargetEnabled != globalIlluminationTargetEnabled ||
                _lastMaterialTransportProvenanceTargetEnabled !=
                    materialTransportProvenanceTargetEnabled ||
                MathF.Abs(_lastGlobalIlluminationResolutionScale - globalIlluminationResolutionScale) > 0.0001f;
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

            RecordDeviceWaitIdle(
                RuntimeStallReason.ResourceResize,
                $"Render target profile rebuild: {recreateReason}",
                _context.WaitIdle);
            _renderTargets.Recreate(
                sceneRenderExtent,
                _swapchain.Extent,
                ambientOcclusionResolutionScale,
                globalIlluminationResolutionScale,
                bloomMipCount,
                aoEnabled,
                globalIlluminationTargetEnabled,
                aaMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                weightedOitTargetEnabled,
                materialTransportProvenanceTargetEnabled);
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
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            _hizVisibilityPolicyState.PyramidValid = false;
            RegisterSceneRenderTextures();
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _renderGraph.OnSwapchainRecreated();
            _asyncComputeTimingPolicy.Clear();
            Array.Clear(_asyncComputeTimingFrames);
            _lastAmbientOcclusionTargetEnabled = aoEnabled;
            _lastAmbientOcclusionResolutionScale = ambientOcclusionResolutionScale;
            _lastAntiAliasingTargetMode = aaMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = bloomMipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastGlobalIlluminationTargetEnabled = globalIlluminationTargetEnabled;
            _lastMaterialTransportProvenanceTargetEnabled =
                materialTransportProvenanceTargetEnabled;
            _lastGlobalIlluminationResolutionScale = globalIlluminationResolutionScale;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = effectiveResolutionScale;
            _lastRenderTargetRecreateReason = recreateReason;
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
            if (_frameInProgress)
                throw new InvalidOperationException("Swapchain cannot be recreated while command recording is in progress.");

            if (!_swapchain.RecreateSwapchain(
                () =>
                {
                    RecordDeviceWaitIdle(
                        RuntimeStallReason.ResourceResize,
                        "Swapchain recreate",
                        _context.WaitIdle);

                    // The old swapchain image is no longer needed after the
                    // recorded copy, but the readback buffer must be finalized
                    // before resize destroys/replaces presentation resources.
                    if (_deviceLost)
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
                }))
            {
                return false;
            }
            _sync.EnsureRenderFinishedSemaphoreCapacity(_swapchain.ImageCount);
            float sceneResolutionScale = ResolveSceneResolutionScale();
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, sceneResolutionScale);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            _hizVisibilityPolicyState.PyramidValid = false;
            _renderTargets?.Recreate(
                sceneRenderExtent,
                _swapchain.Extent,
                Settings.AmbientOcclusion.ResolutionScale,
                Settings.GlobalIllumination.ResolutionScale,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.GlobalIllumination.EffectiveUseSsgi,
                Settings.AntiAliasing.EffectiveMode,
                NeedsMotionVectors(Settings),
                IsFogTargetEnabled(Settings),
                IsWeightedOitTargetEnabled(Settings),
                IsMaterialTransportProvenanceTargetEnabled(Settings));
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
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled = NeedsMotionVectors(Settings);
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = IsFogTargetEnabled(Settings);
            _lastGlobalIlluminationTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
            _lastMaterialTransportProvenanceTargetEnabled =
                IsMaterialTransportProvenanceTargetEnabled(Settings);
            _lastGlobalIlluminationResolutionScale = Settings.GlobalIllumination.ResolutionScale;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Swapchain resize";
            _meshPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _foliagePipeline?.Recreate(RenderTargetManager.SceneColorFormat, RenderTargetManager.MotionVectorFormat, _swapchain.DepthFormat);
            _compositePipeline?.Recreate(_swapchain.SurfaceFormat);
            _ldrCompositePipeline?.Recreate(RenderTargetManager.LdrSceneColorFormat);
            _weightedOitCompositePipeline?.Recreate(RenderTargetManager.SceneColorFormat);
            _ssgiCompositePipeline?.Recreate(RenderTargetManager.SceneColorFormat);
            _skyboxPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _directionalShadowResources?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _spotShadowAtlas?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _pointShadowCubemapArray?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _environmentManager?.Register(_bindlessHeap);
            _environmentManager?.RegisterReflectionProbeFallback(_bindlessHeap);
            _reflectionProbeManager?.Register(_bindlessHeap);
            _ddgiProbeVolumeManager?.Register(_bindlessHeap);
            _simpleDdgiVolumeManager?.Register(_bindlessHeap);
            _ddgiGatherTileManager?.Register(_bindlessHeap);
            _renderGraph.OnSwapchainRecreated();
            _asyncComputeTimingPolicy.Clear();
            Array.Clear(_asyncComputeTimingFrames);
            return true;
        }

        private void EnsureFrameInProgress(string operation)
        {
            if (!_frameInProgress)
                throw new InvalidOperationException($"{operation} requires a successful BeginFrame call.");
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private void RecordDeviceWaitIdle(RuntimeStallReason reason, string description, Action wait)
        {
            if (wait == null)
                throw new ArgumentNullException(nameof(wait));

            long waitStart = Stopwatch.GetTimestamp();
            wait();
            _stallTracker.Record(reason, ElapsedMicroseconds(waitStart), description);
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
            return _dynamicResolutionScaleController.Resolve(Settings, frameMicroseconds);
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

        private static bool NeedsMotionVectors(RenderSettings settings)
        {
            return settings.AntiAliasing.EffectiveMode == AntiAliasingMode.Taa ||
                   (settings.GlobalIllumination.EffectiveUseSsgi &&
                    settings.GlobalIllumination.TemporalEnabled);
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

            if (!Settings.GlobalIllumination.EffectiveUseSsgi && _textureManager.DefaultBlackTexture.IsValid)
            {
                ImageView blackView = _textureManager.GetTextureView(_textureManager.DefaultBlackTexture);
                RegisterGlobalIlluminationFallbackTextures(blackView);
                return;
            }

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SceneNormalTexture,
                _renderTargets.SceneNormal.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SceneMaterialTexture,
                _renderTargets.SceneMaterial.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiTraceSourceTexture,
                _renderTargets.SsgiTraceSource.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiRawTexture,
                _renderTargets.SsgiRaw.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHitDistanceTexture,
                _renderTargets.SsgiHitDistance.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiFilteredTexture,
                _renderTargets.SsgiFiltered.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryTexture,
                _renderTargets.SsgiHistoryA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousDepthTexture,
                _renderTargets.SsgiDepthHistoryA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiPreviousNormalTexture,
                _renderTargets.SsgiNormalHistoryA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiMomentsTexture,
                _renderTargets.SsgiMomentsA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.SsgiHistoryLengthTexture,
                _renderTargets.SsgiHistoryLengthA.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.GiFinalDiffuseTexture,
                _renderTargets.GiFinalDiffuse.View,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
        }

        private void RegisterGlobalIlluminationFallbackTextures(ImageView fallbackView)
        {
            int[] indices =
            [
                BindlessIndex.SceneNormalTexture,
                BindlessIndex.SceneMaterialTexture,
                BindlessIndex.SsgiTraceSourceTexture,
                BindlessIndex.SsgiRawTexture,
                BindlessIndex.SsgiHitDistanceTexture,
                BindlessIndex.SsgiFilteredTexture,
                BindlessIndex.SsgiHistoryTexture,
                BindlessIndex.SsgiPreviousDepthTexture,
                BindlessIndex.SsgiPreviousNormalTexture,
                BindlessIndex.SsgiMomentsTexture,
                BindlessIndex.SsgiHistoryLengthTexture,
                BindlessIndex.GiFinalDiffuseTexture
            ];

            for (int i = 0; i < indices.Length; i++)
            {
                _bindlessHeap.RegisterTexture(
                    indices[i],
                    fallbackView,
                    _bindlessHeap.ScreenSampler,
                    imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            }
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

        private readonly record struct DdgiTrackedRenderObject(
            ulong GeometrySignature,
            ulong MaterialSignature,
            ulong EmissiveSignature,
            ulong TransformSignature,
            BoundingBox Bounds,
            int LastSeenFrame);

        private readonly record struct DdgiTrackedVfxProxy(
            ulong Signature,
            BoundingBox Bounds,
            int LastSeenFrame);

        private readonly record struct SimpleDdgiDirtySignature(
            ulong Signature,
            uint ReasonFlags);

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            lock (_disposeLock)
            {
                if (_disposeCompleted)
                    return;

                if (_disposalPlan == null)
                {
                    // Build the complete dependency graph before publishing
                    // the terminal lifecycle state. An allocation failure here
                    // leaves the renderer fully operational and retryable.
                    StagedDisposalPlan preparedPlan =
                        CreateDisposalPlan();
                    _disposeStarted = true;
                    Settings.QualityPresetChanging -=
                        OnQualityPresetChanging;
                    _disposalPlan = preparedPlan;
                }

                Exception? failure =
                    _disposalPlan.TryDrain();
                if (failure != null)
                {
                    throw failure;
                }

                _disposeCompleted =
                    _disposalPlan.IsComplete;
                if (!_disposeCompleted)
                {
                    throw new InvalidOperationException(
                        "Renderer disposal returned without a failure but still has pending stages.");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "VulkanRenderer disposed.");
        }

        private StagedDisposalPlan CreateDisposalPlan()
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
                    DeviceIdle,
                    () =>
                    {
                        _disposalDeviceIdleResult =
                            _context.Api.DeviceWaitIdle(
                                _context.Device);
                    }));
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
                "render-graph",
                _renderGraph.Cleanup);
            AddResourceStage(
                "gpu-timestamps",
                _gpuTimestamps.Dispose);
            AddResourceStage(
                "diagnostics-buffer",
                _diagnosticsBuffer.Dispose);
            AddResourceStage(
                "directional-shadow-resources",
                () =>
                    _directionalShadowResources
                        ?.Dispose());
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
                "reflection-probe-manager",
                () =>
                    _reflectionProbeManager
                        ?.Dispose());
            AddResourceStage(
                "ddgi-emissive-table-cache",
                () =>
                {
                    _ddgiEmissiveTableCache.Clear();
                    _ddgiEmissiveSourceBufferContentValid =
                        false;
                });
            AddResourceStage(
                "ddgi-emissive-source-buffer",
                DestroyDdgiEmissiveSourceBuffer);
            AddResourceStage(
                "ddgi-probe-volume-manager",
                () =>
                    _ddgiProbeVolumeManager
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
                "ddgi-gather-tile-manager",
                () =>
                    _ddgiGatherTileManager
                        ?.Dispose());
            AddResourceStage(
                "acceleration-structure-manager",
                () =>
                    _accelerationStructureManager
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
                () => _renderTargets?.Dispose());

            AddResourceStage(
                "mesh-pipeline",
                () => _meshPipeline?.Dispose());
            AddResourceStage(
                "compute-pipeline",
                () => _computePipeline?.Dispose());
            AddResourceStage(
                "skinning-pass",
                () => _skinningPass?.Dispose());
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
                "ssgi-composite-pipeline",
                () =>
                    _ssgiCompositePipeline?.Dispose());
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

        private void ThrowIfDisposalStarted()
        {
            lock (_disposeLock)
            {
                if (_disposeStarted)
                {
                    throw new ObjectDisposedException(
                        nameof(VulkanRenderer));
                }
            }
        }

        private void ResolveScreenshotCapturesForDisposal()
        {
            if (_disposalDeviceIdleResult ==
                    Result.Success &&
                !_deviceLost)
            {
                _screenshotReadbackManager
                    .CompleteAllAfterDeviceIdle();
                return;
            }

            _screenshotReadbackManager.FailAll(
                $"Renderer screenshot capture was cancelled during renderer disposal because DeviceWaitIdle returned {_disposalDeviceIdleResult}.",
                includeQueuedRequests: true);
        }

        private void ResolveLinearHdrCapturesForDisposal()
        {
            if (_disposalDeviceIdleResult ==
                    Result.Success &&
                !_deviceLost)
            {
                _linearHdrReadbackManager
                    .CompleteAllAfterDeviceIdle();
                return;
            }

            _linearHdrReadbackManager.FailAll(
                $"Linear HDR capture was cancelled during renderer disposal because DeviceWaitIdle returned {_disposalDeviceIdleResult}.",
                includeQueuedRequests: true);
        }

        private void DestroyDdgiEmissiveSourceBuffer()
        {
            if (!_ddgiEmissiveSourceBuffer.IsValid)
                return;

            _bufferManager.DestroyBuffer(
                _ddgiEmissiveSourceBuffer);
            _ddgiEmissiveSourceBuffer =
                BufferHandle.Invalid;
            _ddgiEmissiveSourceBufferSize = 0;
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
