using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        internal static IReadOnlyList<string> PhaseOneRenderPassOrder => ProductionRenderPipelineDeclaration.Instance.PassOrder;

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
        private readonly RendererDiagnosticsBuffer _diagnosticsBuffer;
        private readonly GpuTimestampRecorder _gpuTimestamps;
        private readonly ParticleSystemManager _particleSystemManager = new();
        private readonly UploadBudgetTracker _uploadBudgetTracker = new();
        private readonly RuntimeStallTracker _stallTracker = new();
        private readonly RenderBudgetEvaluator _budgetEvaluator = new();
        private readonly bool _ownsDependencies;
        private HiZDepthPyramid? _hizDepthPyramid;
        private RenderTargetManager? _renderTargets;
        private DirectionalShadowResources? _directionalShadowResources;
        private SpotShadowAtlas? _spotShadowAtlas;
        private PointShadowCubemapArray? _pointShadowCubemapArray;
        private EnvironmentManager? _environmentManager;
        private ReflectionProbeManager? _reflectionProbeManager;
        private DdgiProbeVolumeManager? _ddgiProbeVolumeManager;
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
        private static readonly ulong DdgiEmissiveSourceStride = (ulong)Marshal.SizeOf<GPUDdgiEmissiveSource>();
        private readonly GPUDdgiEmissiveSource[] _ddgiEmissiveSourceScratch = new GPUDdgiEmissiveSource[MaxDdgiEmissiveSourceCount];
        private readonly float[] _ddgiEmissiveSourceImportanceScratch = new float[MaxDdgiEmissiveSourceCount];
        private BufferHandle _ddgiEmissiveSourceBuffer = BufferHandle.Invalid;
        private ulong _ddgiEmissiveSourceBufferSize;
        private int _ddgiEmissiveSourceCount;
        private uint _ddgiEmissiveSourceRevision;
        private ulong _lastDdgiEmissiveSourceSignature;
        private int _ddgiTrackingFrame;
        private bool _hasLastDdgiCameraPosition;
        private Vector3 _lastDdgiCameraPosition;
        private bool _hasLastDdgiProjectionMatrix;
        private Matrix4x4 _lastDdgiProjectionMatrix;
        private ulong _lastDdgiLightSignature;
        private ulong _lastDdgiProbeVolumeSignature;
        private Light[] _lastDdgiLights = Array.Empty<Light>();
        private bool _hasDdgiDynamicSignature;
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
        private SkinningPass _skinningPass = null!;
        private GpuParticleResetPass _gpuParticleResetPass = null!;
        private GpuParticleSimulatePass _gpuParticleSimulatePass = null!;
        private GpuParticleSortPass _gpuParticleSortPass = null!;
        private FoliageManager _foliageManager = null!;
        private FoliagePipeline _foliagePipeline = null!;
        private FoliageCullPass _foliageCullPass = null!;
        private SceneOpaqueCompactionPass _sceneOpaqueCompactionPass = null!;
        
        // State
        private int _currentFrame = 0;
        private uint _allocatorFrameIndex;
        private uint _temporalSampleIndex;
        private uint _imageIndex;
        private CommandBuffer _currentCommandBuffer;
        private bool _isInitialized = false;
        private bool _disposed = false;
        private bool _frameInProgress;
        private bool _swapchainNeedsRecreate;
        private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
        private RenderBudgetSnapshot _lastBudgetSnapshot = RenderBudgetSnapshot.Empty;
        private SceneRenderingData? _lastSceneData;
        private readonly DebugDrawList _debugDraw = new();
        private readonly ScreenshotCaptureService _screenshotCaptureService = new();
        private readonly RenderDocCaptureService _renderDocCaptureService = new();
        private GpuMeshletCounters _completedGpuCounters;
        private GpuParticleCounterSnapshot _completedGpuParticleCounters;
        private FoliageCounterSnapshot _completedFoliageCounters;
        private SceneSubmissionCounterSnapshot _completedSceneSubmissionCounters;
        private SceneSubmissionValidationSnapshot _completedSceneSubmissionValidation;
        private bool _ddgiGpuSchedulerFallbackLatched;
        private string _ddgiGpuSchedulerFallbackReason = string.Empty;
        private string _ddgiGpuSchedulerLoggedFallbackReason = string.Empty;
        private int _ddgiGpuSchedulerValidationFailureCount;
        private int _ddgiGpuSchedulerFallbackStableFrameCount;
        private readonly HiZVisibilityPolicyRuntimeState _hizVisibilityPolicyState = new();
        private Scene? _lastHiZScene;
        private bool _hasLastHiZCameraPose;
        private Vector3 _lastHiZCameraPosition;
        private Vector3 _lastHiZCameraForward;
        private long _lastParticleTimestamp;
        private float _particleTimeSeconds;
        private long _lastAcquireImageMicroseconds;
        private long _lastQueueSubmitMicroseconds;
        private long _lastPresentMicroseconds;
        private bool _lastAmbientOcclusionTargetEnabled = true;
        private AntiAliasingMode _lastAntiAliasingTargetMode = AntiAliasingMode.SmaaMedium;
        private bool _lastMotionVectorTargetEnabled = true;
        private TransparencyMode _lastTransparencyTargetMode = TransparencyMode.SortedAlphaBlend;
        private int _lastBloomTargetMipCount = 6;
        private bool _lastFogTargetEnabled = true;
        private bool _lastGlobalIlluminationTargetEnabled = true;
        private float _lastGlobalIlluminationResolutionScale = 0.5f;
        private Extent2D _lastSceneRenderExtent;
        private float _lastEffectiveResolutionScale = 1.0f;
        private readonly DynamicResolutionScaleController _dynamicResolutionScaleController = new();
        private string _lastRenderTargetRecreateReason = string.Empty;
        
        // Scene state
        private Color _clearColor = Color.CornflowerBlue;
        public RendererDiagnostics LastDiagnostics => _lastDiagnostics;
        public RenderBudgetSnapshot LastBudgetSnapshot => _lastBudgetSnapshot;
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
        public bool EnableDepthPrePass { get; set; } = true;
        public bool EnableTransparentPass { get; set; } = true;
        public bool EnableMeshletDebugView { get; set; }
        public RenderSettings Settings { get; } = new();
        public int DebugObjectSnapshotCount => _lastSceneData?.ObjectDebugSnapshots.Count ?? 0;

        public void RequestScreenshot(string? outputPath = null)
        {
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowScreenshots)
                return;

            _screenshotCaptureService.Request(outputPath);
        }

        public void RequestRenderDocCapture()
        {
            if (!Settings.Debug.Enabled || !Settings.Debug.AllowRenderDocCapture)
                return;

            _renderDocCaptureService.RequestCapture();
            if (_renderDocCaptureService.CaptureRequested)
                Settings.Diagnostics.GpuMeshletCountersEnabled = true;
        }

        public string ExportPerformanceSnapshot(string? directory = null)
        {
            string targetDirectory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(AppContext.BaseDirectory, "PerformanceSnapshots")
                : directory;

            return new PerformanceSnapshotWriter().Write(targetDirectory, _lastDiagnostics, _lastBudgetSnapshot);
        }

        public bool TryFindObjectByName(string name, out int objectIndex)
        {
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

        public bool TryInspectObject(int index, out SelectedObjectInspection inspection)
        {
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
            _diagnosticsBuffer = new RendererDiagnosticsBuffer(_context, _bufferManager);
            _gpuTimestamps = new GpuTimestampRecorder(_context);
            _particleSystemManager = new ParticleSystemManager(_context, _bufferManager, _stagingRing);
            _gpuParticleRuntimeManager = new GpuParticleRuntimeManager(_context, _bufferManager, _stagingRing);
            _foliageManager = new FoliageManager(_context, _bufferManager, _stagingRing, _meshManager, _materialManager);
            _ownsDependencies = ownsDependencies;
        }
        
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            System.Diagnostics.Debug.WriteLine("Initializing VulkanRenderer...");
            
            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool ssgiTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
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
                ssgiTargetEnabled,
                Settings.GlobalIllumination.ResolutionScale,
                Settings.AntiAliasing.EffectiveMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                IsWeightedOitTargetEnabled(Settings),
                _renderGraph);
            _lastAmbientOcclusionTargetEnabled = Settings.AmbientOcclusion.Enabled;
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastGlobalIlluminationTargetEnabled = ssgiTargetEnabled;
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
            _ddgiGatherTileManager = new DdgiGatherTileManager(_context, _bufferManager);
            _ddgiEmissiveSourceBuffer = CreateDdgiEmissiveSourceBuffer();
            _accelerationStructureManager = new AccelerationStructureManager(_context, _bufferManager, _meshManager, _materialManager);
            _autoExposureManager = new AutoExposureManager(_context, _bufferManager, Settings);
            _skinningManager = new SkinningManager(_context, _bufferManager, _stagingRing, _meshManager);

            // Create pipelines
            CreatePipelines();
            
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
            if (Settings.GlobalIllumination.EffectiveUseSsgi)
                _ssgiCompositePipeline = new SsgiCompositePipeline(_context, _bindlessHeap, RenderTargetManager.SceneColorFormat);
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
            _foliageCullPass = new FoliageCullPass(_context, _bindlessHeap, _bufferManager, _foliageManager, _foliagePipeline);
            _sceneOpaqueCompactionPass = new SceneOpaqueCompactionPass(_context, _swapchain, _bindlessHeap, _meshPipeline, _bufferManager);
            
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

                var ssgiCompositePass = new SsgiCompositePass(
                    _context,
                    _swapchain,
                    _bindlessHeap,
                    _ssgiCompositePipeline!,
                    _renderTargets!,
                    Settings);
                AddPassInstance(ssgiCompositePass);
            }

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
            ProductionRenderPipelineDeclaration.Instance.RegisterPasses(_renderGraph, passInstances, includeSsgi);
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
            _ddgiGatherTileManager!.Register(_bindlessHeap);
            _accelerationStructureManager!.Register(_bindlessHeap);
            
            System.Diagnostics.Debug.WriteLine("Scene buffers registered.");
        }

        private BufferHandle CreateDdgiEmissiveSourceBuffer()
        {
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
            if (!_isInitialized)
                Initialize();

            if (_frameInProgress)
                throw new InvalidOperationException("BeginFrame was called while a frame is already in progress.");

            _stallTracker.BeginFrame();
            
            // Wait for previous frame to complete
            _sync.WaitForFence(_currentFrame);
            _stallTracker.Record(RuntimeStallReason.FrameFenceWait, _sync.LastFenceWaitMicroseconds, "Frame fence");
            _diagnosticsBuffer.ReadCompletedFrame(_currentFrame);
            _gpuParticleRuntimeManager.ReadCompletedFrame(_currentFrame);
            _foliageManager.ReadCompletedFrame(_currentFrame);
            _ddgiProbeVolumeManager?.ReadCompletedGpuSchedulerCounters(_currentFrame);
            UpdateDdgiGpuSchedulerFallbackStateFromCompletedFrame();
            _sceneOpaqueCompactionPass?.ReadCompletedFrame(_currentFrame);
            _autoExposureManager?.ReadCompletedFrame(_currentFrame);
            _completedGpuCounters = _diagnosticsBuffer.GetLastCompletedCounters(_currentFrame);
            _completedGpuParticleCounters = _gpuParticleRuntimeManager.GetLastCompletedCounters(_currentFrame);
            _completedFoliageCounters = _foliageManager.GetLastCompletedCounters(_currentFrame);
            _completedSceneSubmissionCounters = _sceneOpaqueCompactionPass?.GetLastCompletedCounters(_currentFrame) ?? SceneSubmissionCounterSnapshot.Invalid;
            _completedSceneSubmissionValidation = _sceneOpaqueCompactionPass?.GetLastCompletedValidation(_currentFrame) ?? SceneSubmissionValidationSnapshot.Invalid;
            _gpuTimestamps.ReadCompletedFrame(_currentFrame);
            
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
                RecreateSwapchain();
                return false;
            }

            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
                throw new VulkanException("Failed to acquire swapchain image", acquireResult);

            _swapchainNeedsRecreate = acquireResult == Result.SuboptimalKhr;

            EnsureMeshPipelineDiagnosticVariant();
            
            // Reset fence for current frame
            _sync.ResetFence(_currentFrame);
            _stallTracker.Record(RuntimeStallReason.Unknown, _sync.LastFenceResetMicroseconds, "Reset frame fence");

            // Reset and begin recording the primary command buffer owned by this frame.
            _cmd.ResetGraphicsCommandBuffer(_currentFrame);
            if (Settings.UseSecondaryCommandBuffers)
                _cmd.ResetSecondaryGraphicsCommandPool(_currentFrame);
            _environmentManager?.EnsureResourcesCurrent(
                _bindlessHeap,
                () => RecordDeviceWaitIdle(
                    RuntimeStallReason.DeviceWaitIdle,
                    "Environment resource recreate",
                    _context.WaitIdle));
            
            _currentCommandBuffer = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
            _frameInProgress = true;
            _gpuTimestamps.BeginFrame(_currentCommandBuffer, _currentFrame, Settings.Debug.AllowGpuTiming);

            // Acquired swapchain images are transitioned from their tracked layout.
            TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.ColorAttachmentOptimal);

            return true;
        }
        
        public void EndFrame()
        {
            if (!_frameInProgress)
                throw new InvalidOperationException("EndFrame was called without a successful BeginFrame.");

            var vk = _context.Api;
            
            // Transition swapchain image to present for the presentation engine.
            TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.PresentSrcKhr);
            
            // End command buffer recording
            Result result = vk.EndCommandBuffer(_currentCommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end command buffer", result);
            
            // Submit command buffer
            var waitSemaphores = stackalloc Semaphore[] { _sync.GetImageAvailableSemaphore(_currentFrame) };
            var waitStages = stackalloc PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
            Semaphore renderFinishedSemaphore = _sync.GetRenderFinishedSemaphoreForImage(_imageIndex);
            var signalSemaphores = stackalloc Semaphore[] { renderFinishedSemaphore };
            var commandBuffers = stackalloc CommandBuffer[] { _currentCommandBuffer };
            
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = commandBuffers,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = signalSemaphores
            };
            
            long submitStart = Stopwatch.GetTimestamp();
            result = vk.QueueSubmit(
                _context.GraphicsQueue,
                1,
                &submitInfo,
                _sync.GetInFlightFence(_currentFrame));
            _lastQueueSubmitMicroseconds = ElapsedMicroseconds(submitStart);
            _stallTracker.Record(RuntimeStallReason.QueueSubmit, _lastQueueSubmitMicroseconds, "Graphics queue submit");
            
            if (result != Result.Success)
                throw new VulkanException("Failed to submit queue", result);
            
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
            
            // Advance to next frame
            _currentFrame = (_currentFrame + 1) % FramesInFlight;
            _temporalSampleIndex++;
            _sync.AdvanceFrame();
            _frameInProgress = false;

            if (presentResult == Result.ErrorOutOfDateKhr ||
                presentResult == Result.SuboptimalKhr ||
                _swapchainNeedsRecreate)
            {
                _swapchainNeedsRecreate = false;
                RecreateSwapchain();
            }
        }
        
        public unsafe void Clear(Color color)
        {
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
            EnsureFrameInProgress(nameof(DrawScene));
            long drawSceneStart = Stopwatch.GetTimestamp();

            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            bool debugEnabled = Settings.Debug.Enabled;
            RenderFeatureIsolationMode isolationMode = Settings.FeatureIsolation;
            bool isolateSkinnedAnimationDebug = Settings.Animation.DebugView == AnimationDebugView.SkinnedObjects;
            bool shadowsAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsShadows(isolationMode);
            bool reflectionsAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsReflections(isolationMode);
            bool animationAllowed = RenderFeatureIsolationPolicy.AllowsAnimation(isolationMode);
            bool particlesAllowed = !isolateSkinnedAnimationDebug && RenderFeatureIsolationPolicy.AllowsParticles(isolationMode);
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
                EnsureLocalShadowResources();
                localShadowSelection = _localShadowSelector.Select(
                    lightSnapshot.Lights.Span,
                    camera,
                    Settings.Shadows,
                    _spotShadowAtlas?.Capacity ?? Settings.Shadows.SpotShadowAtlasCapacity,
                    _pointShadowCubemapArray?.PointCapacity ?? Settings.Shadows.MaxShadowedPointLights);
                hasLocalShadows = localShadowSelection.SpotLights.Length > 0 || localShadowSelection.PointLights.Length > 0;
                shadowData = CreateDirectionalShadowData(camera, lightSnapshot, out directionalShadowsEnabled, out shadowedDirectionalLightIndex);
            }
            else
            {
                localShadowSelection = new LocalShadowSelection();
                hasLocalShadows = false;
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
                useCameraDependentCpuPayload: Settings.UseCameraDependentCpuScenePayload &&
                    !sceneGpuLodSelectionActive &&
                    !sceneGpuShadowCompactionActive,
                useCpuMeshletFrustumCulling: Settings.UseCpuMeshletFrustumCulling && !sceneGpuLodSelectionActive,
                captureSceneSubmissionValidationLists: Settings.SceneSubmission.ValidationCompareCpuGpuLists);
            sceneData.FrameIndex = _currentFrame;
            sceneData.TemporalSampleIndex = _temporalSampleIndex;
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
            sceneData.SceneSubmissionGpuShadowCompactionEnabled = Settings.SceneSubmission.GpuShadowCompactionEnabled;
            sceneData.SceneSubmissionValidationCompareCpuGpuLists = Settings.SceneSubmission.ValidationCompareCpuGpuLists;
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
            sceneData.ParticleDdgiSampleCount = sceneData.GpuParticlesEnabled != 0
                ? sceneData.GpuParticleEmitterCount
                : sceneData.ParticleBatchCount;
            sceneData.FoliageDdgiSampleCount = sceneData.FoliageClusterCount;
            sceneData.DepthPrePassEnabled = EnableDepthPrePass && !isolateSkinnedAnimationDebug;
            HiZVisibilityPolicyDecision hiZDecision = PlanHiZVisibility(scene, camera, sceneData.DepthPrePassEnabled, isolateSkinnedAnimationDebug);
            sceneData.HiZBuildEnabled = hiZDecision.BuildHiZ;
            sceneData.OcclusionCullingEnabled = sceneData.DepthPrePassEnabled && hiZDecision.UseHiZForOcclusion;
            sceneData.HiZTestMode = sceneData.OcclusionCullingEnabled ? Settings.HiZTestMode : HiZTestMode.Off;
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
            sceneData.HiZPolicyAdaptiveEstimatedSavedMicroseconds = hiZDecision.AdaptiveEstimatedSavedMicroseconds;
            sceneData.HiZPolicyAdaptiveEstimatedCostMicroseconds = hiZDecision.AdaptiveEstimatedCostMicroseconds;
            sceneData.HiZPolicyAdaptiveEstimatedNetMicroseconds = hiZDecision.AdaptiveEstimatedNetMicroseconds;
            sceneData.HiZPolicyAdaptiveSuppressedFrameCount = hiZDecision.AdaptiveSuppressedFrameCount;
            sceneData.HiZPolicyAdaptiveStatus = hiZDecision.AdaptiveStatus;
            sceneData.TransparentPassEnabled = EnableTransparentPass && Settings.Transparency.Enabled;
            sceneData.TransparencyMode = Settings.Transparency.Mode;
            sceneData.TransparencyDebugView = Settings.Transparency.DebugView;
            sceneData.TransparentReceiveShadows = Settings.Transparency.ReceiveShadows;
            sceneData.DecalDebugView = Settings.Decals.DebugView;
            sceneData.GeometryDecalsEnabled = Settings.Decals.GeometryDecalsEnabled;
            sceneData.GeometryDecalDepthBias = Settings.Decals.GeometryDepthBias;
            sceneData.GeometryDecalSlopeScaledDepthBias = Settings.Decals.GeometrySlopeScaledDepthBias;
            sceneData.HiZMipCount = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.MipLevels ?? 0u : 0u;
            sceneData.HiZWidth = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Width ?? 0u : 0u;
            sceneData.HiZHeight = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Height ?? 0u : 0u;
            sceneData.ActiveSceneColorTextureIndex = BindlessIndex.HdrSceneColorTexture;
            sceneData.EffectiveExposure = Settings.Exposure;
            sceneData.FogDirectionalInscatteringDirection = ResolveFogDirectionalInscatteringDirection(lightSnapshot);
            sceneData.DebugViewMode = ResolveForwardDebugViewMode();
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
            ApplyCompletedSceneSubmissionValidation(sceneData, _completedSceneSubmissionValidation);
            sceneData.SceneSubmissionFallbackReason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
                sceneData,
                _completedSceneSubmissionCounters,
                _completedSceneSubmissionValidation);
            sceneData.SceneSubmissionCompactionSkipReason =
                SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(sceneData);
            _diagnosticsBuffer.ResetCounters(_currentCommandBuffer, _currentFrame);
            if (particlesAllowed)
            {
                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "GpuParticleResetPass");
                try
                {
                    _gpuParticleResetPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }

                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "GpuParticleSimulatePass");
                try
                {
                    _gpuParticleSimulatePass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }

                _gpuTimestamps.BeginPass(_currentCommandBuffer, _currentFrame, "GpuParticleSortPass");
                try
                {
                    _gpuParticleSortPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
                }
                finally
                {
                    _gpuTimestamps.EndPass(_currentCommandBuffer, _currentFrame);
                }

                _gpuParticleRuntimeManager.RecordCounterReadback(_currentCommandBuffer, _currentFrame, sceneData);
            }

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
            
            var vk = _context.Api;
            
            // Set viewport and scissor
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
            
            vk.CmdSetViewport(_currentCommandBuffer, 0, 1, &viewport);
            vk.CmdSetScissor(_currentCommandBuffer, 0, 1, &scissor);
            
            // Execute render graph
            sceneData.SecondaryCommandBufferEnabled = Settings.UseSecondaryCommandBuffers ? 1 : 0;
            _renderGraph.Execute(
                _currentCommandBuffer,
                _currentFrame,
                sceneData,
                _gpuTimestamps,
                _cmd,
                Settings.UseSecondaryCommandBuffers);
            _hizVisibilityPolicyState.PyramidValid = sceneData.HiZBuildEnabled;
            if (MeshletDiagnosticCountersActive)
                ApplyCompletedGpuCounters(sceneData, _completedGpuCounters);
            ApplyCompletedSsgiCounters(sceneData, _completedGpuCounters);
            if (particlesAllowed)
                ApplyCompletedGpuParticleCounters(sceneData, _completedGpuParticleCounters);
            if (!isolateSkinnedAnimationDebug)
                ApplyCompletedFoliageCounters(sceneData, _completedFoliageCounters);
            FrameTimingSnapshot completedGpuTimings = _gpuTimestamps.LastCompletedSnapshot;
            ApplyCompletedGpuTimings(sceneData, completedGpuTimings);
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
            _lastSceneData = sceneData;
            _lastDiagnostics = BuildDiagnostics(sceneData);
            _debugDraw.ClearFrame();
        }

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
            _ = scene;
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes = _lastDdgiFrameLayout.Volumes;
            int activeProbeStart = 0;
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
                DrawDdgiProbeSamples(volume, metadata, firstProbeIndex, sceneData.DebugOverlayMode, depthMode);
                sceneData.DebugDdgiProbeVolumesDrawn++;
                if (active)
                    activeProbeStart += volume.ProbeCount;
            }
        }

        private void DrawDdgiProbeSamples(
            GlobalIlluminationProbeVolume volume,
            DdgiProbeVolumeRuntimeMetadata metadata,
            int firstProbeIndex,
            DebugOverlayMode overlayMode,
            DebugDrawDepthMode depthMode)
        {
            const int MaxProbeMarkersPerVolume = 512;
            int probeIndex = 0;
            Vector3 spacing = volume.ProbeSpacing;
            float markerRadius = MathF.Min(MathF.Min(spacing.X, spacing.Y), spacing.Z) * 0.08f;
            markerRadius = Math.Clamp(markerRadius, 0.04f, 0.2f);
            DdgiProbeMarkerSampling markerSampling = CalculateDdgiProbeMarkerSampling(
                volume.ProbeCountX,
                volume.ProbeCountY,
                volume.ProbeCountZ,
                MaxProbeMarkersPerVolume);
            DdgiClipmapCascadeState? cascadeState = metadata.Kind == DdgiProbeVolumeKind.CameraClipmap
                ? FindDdgiClipmapCascade(metadata.CascadeIndex)
                : null;
            for (int z = 0; z < volume.ProbeCountZ; z++)
            {
                for (int y = 0; y < volume.ProbeCountY; y++)
                {
                    for (int x = 0; x < volume.ProbeCountX; x++, probeIndex++)
                    {
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
                    }
                }
            }
        }

        internal readonly record struct DdgiProbeMarkerSampling(int StepX, int StepY, int StepZ);

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
            _lastHiZScene = scene;
            _lastHiZCameraPosition = camera.Position;
            _lastHiZCameraForward = camera.Forward.Normalized();
            _hasLastHiZCameraPose = true;
            var completedTimings = _gpuTimestamps.LastCompletedSnapshot;

            var input = new HiZVisibilityPolicyInput(
                DepthPrePassEnabled: depthPrePassEnabled,
                HiZOcclusionEnabled: EnableHiZOcclusion,
                FeatureIsolationDisablesHiZ: featureIsolationDisablesHiZ,
                RequestedTestMode: Settings.HiZTestMode,
                SceneChanged: sceneChanged,
                CameraCut: cameraCut,
                AdaptiveEnabled: EnableAdaptiveHiZOcclusion,
                MeshletCountersActive: MeshletDiagnosticCountersActive,
                CompletedForwardOcclusionTested: _completedGpuCounters.ForwardOcclusionTested,
                CompletedForwardOcclusionCulled: _completedGpuCounters.ForwardOcclusionCulled,
                CompletedDepthPrePassMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("DepthPrePass"),
                CompletedHiZBuildMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("HiZBuildPass"),
                CompletedForwardOpaqueMicroseconds: completedTimings.GetGpuMicrosecondsOrZero("ForwardPlusPass"));

            return HiZVisibilityPolicy.Plan(input, Settings.HiZVisibilityPolicy, _hizVisibilityPolicyState);
        }

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
            if (_directionalShadowResources.Ensure(shadowSettings))
                _directionalShadowResources.Register(_bindlessHeap, _swapchain.DepthImageView);

            Light shadowLight = default;
            bool hasShadowLight = lightSnapshot.DirectionalLightCount > 0 && lightSnapshot.HasShadowCastingDirectionalLight;
            if (hasShadowLight)
            {
                lightIndex = lightSnapshot.FirstShadowCastingDirectionalLightIndex;
                shadowLight = lightSnapshot.FirstShadowCastingDirectionalLight;
            }
            enabled = shadowSettings.DirectionalShadowsEnabled && hasShadowLight && _directionalShadowResources.HasImage;

            GPUShadowData shadowData = enabled
                ? DirectionalShadowDataBuilder.Build(camera, shadowLight.Direction, shadowSettings, lightIndex)
                : DirectionalShadowDataBuilder.Build(camera, new System.Numerics.Vector3(0f, -1f, 0f), shadowSettings, -1);

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
            sceneData.ShadowedDirectionalLightIndex = enabled ? lightIndex : -1;
            sceneData.ShadowDebugView = shadowSettings.DebugView;
            sceneData.ShadowNormalBias = shadowSettings.NormalBias;
            sceneData.ShadowSlopeScaledDepthBias = shadowSettings.SlopeScaledDepthBias;
            sceneData.DirectionalShadowPcfRadius = shadowSettings.PcfRadius;
            sceneData.ShadowData = shadowData;
        }

        private void EnsureLocalShadowResources()
        {
            if (_spotShadowAtlas == null || _pointShadowCubemapArray == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            if (_spotShadowAtlas.Ensure(shadowSettings))
            {
                _spotShadowAtlas.Register(_bindlessHeap, _swapchain.DepthImageView);
                _hasUploadedSpotShadows = false;
                _hasUploadedLocalShadowIndices = false;
            }

            if (_pointShadowCubemapArray.Ensure(shadowSettings))
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
            AsyncComputePlan asyncComputePlan = BuildAsyncComputePlan(sceneData);
            GlobalIlluminationSettings giSettings = Settings.GlobalIllumination;
            bool giRayQuerySupported = _context.RayQuerySupported && _accelerationStructureManager?.Supported == true;
            bool giAccelerationStructuresActive = _accelerationStructureManager?.Active == true;
            GlobalIlluminationMode effectiveGiMode = ResolveEffectiveGlobalIlluminationMode(giSettings, giRayQuerySupported);
            if (effectiveGiMode == GlobalIlluminationMode.RayQueryHybrid && !giAccelerationStructuresActive)
                effectiveGiMode = GlobalIlluminationMode.Hybrid;
            bool giEnabled = giSettings.Enabled && effectiveGiMode != GlobalIlluminationMode.Disabled;
            bool giUsesSsgi = giSettings.EffectiveUseSsgi;
            bool giUsesDdgi = giSettings.EffectiveUseDdgi;
            IReadOnlyList<string> activeProductionPipelinePasses = productionPipeline.GetActivePasses(
                sceneData.ActiveFeatureIsolation,
                sceneData.TransparencyMode,
                giUsesSsgi);
            bool giRayQueryActive = giEnabled &&
                                    giSettings.EffectiveUseRayQueryBackend &&
                                    giRayQuerySupported &&
                                    giAccelerationStructuresActive;
            (uint ssgiWidth, uint ssgiHeight) = CalculateSsgiExtent(sceneData.ScreenWidth, sceneData.ScreenHeight, giSettings.ResolutionScale, giUsesSsgi);
            int ssgiRayCount = ResolveSsgiRayCount(Settings.QualityPreset, giUsesSsgi);
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
                GlobalIlluminationEnabled = giEnabled ? 1 : 0,
                GlobalIlluminationMode = giEnabled ? effectiveGiMode : GlobalIlluminationMode.Disabled,
                GlobalIlluminationDebugView = giEnabled ? giSettings.DebugView : GlobalIlluminationDebugView.None,
                GlobalIlluminationRayQuerySupported = giRayQuerySupported ? 1 : 0,
                GlobalIlluminationRayQueryActive = giRayQueryActive ? 1 : 0,
                GlobalIlluminationSsgiActive = giUsesSsgi ? 1 : 0,
                GlobalIlluminationDdgiActive = giUsesDdgi ? 1 : 0,
                SsgiWidth = ssgiWidth,
                SsgiHeight = ssgiHeight,
                SsgiResolutionScale = giUsesSsgi ? giSettings.ResolutionScale : 0f,
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
                DdgiGatherTileCount = giUsesDdgi ? sceneData.DdgiGatherTileCount : 0,
                DdgiGatherTileCountX = giUsesDdgi ? sceneData.DdgiGatherTileCountX : 0,
                DdgiGatherTileCountY = giUsesDdgi ? sceneData.DdgiGatherTileCountY : 0,
                DdgiGatherSelectedLocalTileCount = giUsesDdgi ? sceneData.DdgiGatherSelectedLocalTileCount : 0,
                DdgiGatherSelectedClipmapTileCount = giUsesDdgi ? sceneData.DdgiGatherSelectedClipmapTileCount : 0,
                DdgiGatherFallbackTileCount = giUsesDdgi ? sceneData.DdgiGatherFallbackTileCount : 0,
                DdgiSchedulerMode = giUsesDdgi ? giSettings.DdgiSchedulerMode : DdgiSchedulerMode.CpuReference,
                DdgiQualityTier = giUsesDdgi ? sceneData.DdgiQualityTier : DdgiQualityTier.DdgiHigh,
                DdgiAdaptiveBudgetScale = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetScale : 1.0f,
                DdgiAdaptiveBudgetReduced = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReduced : 0,
                DdgiEmergencyDegradeActive = giUsesDdgi ? sceneData.DdgiEmergencyDegradeActive : 0,
                DdgiEffectiveMaxShadedLights = giUsesDdgi ? sceneData.DdgiEffectiveMaxShadedLights : 0,
                DdgiAdaptiveBudgetReason = giUsesDdgi ? sceneData.DdgiAdaptiveBudgetReason : string.Empty,
                DdgiAsyncComputeEnabled = giUsesDdgi ? sceneData.DdgiAsyncComputeEnabled : 0,
                DdgiAtlasMemoryBudgetBytes = giUsesDdgi ? sceneData.DdgiAtlasMemoryBudgetBytes : 0,
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
                DdgiCurrentIrradianceAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentIrradianceAtlasBytes : 0UL,
                DdgiCurrentVisibilityAtlasBytes = giUsesDdgi ? sceneData.DdgiCurrentVisibilityAtlasBytes : 0UL,
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
                DdgiGpuSchedulerDuplicateRequestCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerDuplicateRequestCount : 0u,
                DdgiGpuSchedulerBudgetRejectedCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerBudgetRejectedCount : 0u,
                DdgiGpuSchedulerInvalidProbeCount = giUsesDdgi ? sceneData.DdgiGpuSchedulerInvalidProbeCount : 0u,
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
                DdgiRayScratchBytes = giUsesDdgi ? sceneData.DdgiRayScratchBytes : 0UL,
                DdgiUpdatedAtlasBytes = giUsesDdgi ? sceneData.DdgiUpdatedAtlasBytes : 0UL,
                DdgiPublishedCacheLatencyFrames = giUsesDdgi ? sceneData.DdgiPublishedCacheLatencyFrames : 0,
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
                AccelerationStructureInstanceUploadBytes = sceneData.AccelerationStructureInstanceUploadBytes,
                AccelerationStructureRayQueryMetadataUploadBytes = sceneData.AccelerationStructureRayQueryMetadataUploadBytes,
                CpuAccelerationStructureBuildMicroseconds = sceneData.CpuAccelerationStructureBuildMicroseconds,
                CpuAccelerationStructureBlasBuildMicroseconds = sceneData.CpuAccelerationStructureBlasBuildMicroseconds,
                CpuAccelerationStructureTlasBuildMicroseconds = sceneData.CpuAccelerationStructureTlasBuildMicroseconds,
                CpuAccelerationStructureInstanceUploadMicroseconds = sceneData.CpuAccelerationStructureInstanceUploadMicroseconds,
                GpuAccelerationStructureBlasMicroseconds = sceneData.GpuAccelerationStructureBlasMicroseconds,
                GpuAccelerationStructureTlasMicroseconds = sceneData.GpuAccelerationStructureTlasMicroseconds,
                AccelerationStructureFallbackReason = sceneData.AccelerationStructureFallbackReason,
                GeometryDecalsEnabled = sceneData.GeometryDecalsEnabled ? 1 : 0,
                GeometryDecalDepthBias = sceneData.GeometryDecalDepthBias,
                GeometryDecalSlopeScaledDepthBias = sceneData.GeometryDecalSlopeScaledDepthBias,
                SolidDepthMeshletDrawUploadBytes = sceneData.SolidDepthMeshletDrawUploadBytes,
                MaskedDepthMeshletDrawUploadBytes = sceneData.MaskedDepthMeshletDrawUploadBytes,
                MaterialExtensionUploadBytes = sceneData.MaterialExtensionUploadBytes,
                MaterialExtensionDataCount = sceneData.MaterialExtensionData.Count,
                MaterialDebugView = Settings.Materials.DebugView,
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
                ForwardMeshletsSubmittedCpu = sceneData.MeshletCountSubmittedCpu,
                ForwardGpuOcclusionRejectedMeshlets = forwardOcclusionRejected,
                ForwardGpuOcclusionCountersReconciled = forwardOcclusionCountersReconciled ? 1 : 0,
                ForwardGpuOcclusionSanity = forwardOcclusionSanity,
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
                HiZPolicyAdaptiveEstimatedSavedMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedSavedMicroseconds,
                HiZPolicyAdaptiveEstimatedCostMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedCostMicroseconds,
                HiZPolicyAdaptiveEstimatedNetMicroseconds = sceneData.HiZPolicyAdaptiveEstimatedNetMicroseconds,
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
                SceneSubmissionGpuDirectionalShadowCascadeSummary = BuildDirectionalShadowCompactionSummary(sceneData),
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
                LargestTextureAssets = _textureManager.GetLargestFileTextures(10),
                MeshletQualityEntries = _meshManager.GetMeshletQualityEntries(10)
            };

            long gpuFrameMicroseconds = CalculateGpuFrameMicroseconds(sceneData);
            diagnostics = diagnostics with
            {
                GpuFrameMicroseconds = gpuFrameMicroseconds,
                GpuTimingValid = gpuFrameMicroseconds > 0 ? 1 : 0
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

            return diagnostics with
            {
                ActiveBudgetProfile = profile.Kind,
                ActiveBudgetProfileName = profile.Name,
                ActiveQualityPreset = Settings.QualityPreset,
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
                AsyncComputeCandidatePassCount = asyncComputePlan.CandidatePasses.Count,
                AsyncComputeEnabledPassCount = asyncComputePlan.EnabledPasses.Count,
                AsyncComputeQueueOwnershipTransitionCount = asyncComputePlan.QueueOwnershipTransitionCount,
                AsyncComputeStatus = asyncComputePlan.Status,
                AsyncComputeCandidatePasses = asyncComputePlan.CandidatePasses,
                AsyncComputeEnabledPasses = asyncComputePlan.EnabledPasses,
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
                SceneObjectBufferHighWaterBytes = sceneObjectHighWaterBytes,
                SceneOpaqueMeshletBufferHighWaterBytes = sceneOpaqueHighWaterBytes,
                SceneDepthMeshletBufferHighWaterBytes = sceneDepthHighWaterBytes,
                SceneTransparentMeshletBufferHighWaterBytes = sceneTransparentHighWaterBytes,
                SceneShadowMeshletBufferHighWaterBytes = sceneShadowHighWaterBytes
            };
        }

        private static ulong SumDirectionalShadowMeshlets(SceneRenderingData sceneData)
        {
            ulong sum = 0;
            for (int i = 0; i < sceneData.DirectionalShadowMeshletCounts.Length; i++)
                sum += (ulong)Math.Max(0, sceneData.DirectionalShadowMeshletCounts[i]);
            return sum;
        }

        private AsyncComputePlan BuildAsyncComputePlan(SceneRenderingData sceneData)
        {
            bool requested = Settings.AsyncCompute.Enabled;
            bool supported = false;
            bool enabled = false;
            RenderGraphDiagnostics graphDiagnostics = _renderGraph.CreateDiagnostics(
                sceneData.ActiveFeatureIsolation,
                asyncComputeEnabled: enabled);

            var candidates = new List<string>();
            foreach (RenderGraphPassDiagnostics pass in graphDiagnostics.Passes)
            {
                if (pass.EnabledByFeatureIsolation &&
                    pass.AsyncComputeCandidate &&
                    IsAsyncComputePassAllowedBySettings(pass.Name))
                {
                    candidates.Add(pass.Name);
                }
            }

            if (Settings.AsyncCompute.GpuParticlesEnabled && sceneData.GpuParticlesEnabled != 0)
            {
                candidates.Add("GpuParticleResetPass");
                candidates.Add("GpuParticleSimulatePass");
                candidates.Add("GpuParticleSortPass");
            }

            string status = requested
                ? "requested but inactive: renderer does not yet create a dedicated async compute queue; graph queue ownership transitions are diagnostic-only."
                : "disabled by settings; async compute candidates are reported for timing validation.";

            return new AsyncComputePlan(
                requested,
                supported,
                enabled,
                candidates,
                Array.Empty<string>(),
                graphDiagnostics.QueueOwnershipTransitionCount,
                status,
                graphDiagnostics);
        }

        private bool IsAsyncComputePassAllowedBySettings(string passName)
        {
            return passName switch
            {
                "HiZBuildPass" => Settings.AsyncCompute.HiZBuildEnabled,
                "AmbientOcclusionBlurPass" => Settings.AsyncCompute.AmbientOcclusionBlurEnabled,
                "FogPass" => Settings.AsyncCompute.FogEnabled,
                "BloomPass" => Settings.AsyncCompute.BloomEnabled,
                "DdgiSchedulePass" or "DdgiTracePass" or "DdgiBlendPass" or "DdgiRelocateClassifyPass" or "DdgiPublishPass" => Settings.AsyncCompute.DdgiUpdateEnabled && Settings.GlobalIllumination.DdgiAsyncComputeEnabled,
                _ => true
            };
        }

        private sealed record AsyncComputePlan(
            bool Requested,
            bool Supported,
            bool Enabled,
            IReadOnlyList<string> CandidatePasses,
            IReadOnlyList<string> EnabledPasses,
            int QueueOwnershipTransitionCount,
            string Status,
            RenderGraphDiagnostics GraphDiagnostics);

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
            bool giUsesSsgi = Settings.GlobalIllumination.EffectiveUseSsgi;
            ulong globalIlluminationBytes = _renderTargets?.GlobalIlluminationRenderTargetBytes ?? EstimateGlobalIlluminationRenderTargetBytes(
                _swapchain.Extent.Width,
                _swapchain.Extent.Height,
                Settings.GlobalIllumination.ResolutionScale,
                giUsesSsgi);
            globalIlluminationBytes += (_ddgiProbeVolumeManager?.TextureBytes ?? 0) + (_ddgiProbeVolumeManager?.BufferBytes ?? 0);
            AddMemoryEntry(
                entries,
                ref totalBytes,
                MemoryBudgetCategory.GlobalIllumination,
                globalIlluminationBytes,
                globalIlluminationBytes == 0 ? 0 : _ddgiProbeVolumeManager == null ? 5 : 7,
                "Projected global illumination targets and probe resources");
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

            if (_gpuTimestamps.PendingThisFrame)
                return "GPU timing is enabled; waiting for a completed frame of timestamp results.";

            return string.Empty;
        }

        private static bool HasCompletedDdgiGpuTiming(FrameTimingSnapshot timings)
        {
            return HasCompletedGpuTiming(timings, "DdgiSchedulePass") ||
                HasCompletedGpuTiming(timings, "DdgiTracePass") ||
                HasCompletedGpuTiming(timings, "DdgiBlendPass") ||
                HasCompletedGpuTiming(timings, "DdgiRelocateClassifyPass") ||
                HasCompletedGpuTiming(timings, "DdgiPublishPass");
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
            sceneData.GpuDdgiTraceMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiTracePass");
            sceneData.GpuDdgiBlendMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiBlendPass");
            sceneData.GpuDdgiRelocateClassifyMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiRelocateClassifyPass");
            sceneData.GpuDdgiPublishMicroseconds = timings.GetGpuMicrosecondsOrZero("DdgiPublishPass");
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

        private static bool ForwardOcclusionCountersReconcile(SceneRenderingData sceneData)
        {
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
                return;
            }

            DdgiFrameLayout layout = BuildDdgiFrameLayout(scene, camera, lightSnapshot, sceneData, cameraCut);
            _lastDdgiFrameLayout = layout;
            _ddgiProbeVolumeManager.Upload(layout, _stagingRing, _currentCommandBuffer);
            _ddgiGatherTileManager?.Upload(
                layout,
                sceneData.ViewProjectionMatrix,
                sceneData.ScreenWidth,
                sceneData.ScreenHeight,
                _stagingRing,
                _currentCommandBuffer);

            bool ddgiActive = Settings.GlobalIllumination.EffectiveUseDdgi;
            bool ddgiRayUpdateActive = ddgiActive &&
                                       Settings.GlobalIllumination.EffectiveUseRayQueryBackend &&
                                       _accelerationStructureManager?.Active == true;
            bool ddgiCompareMode = Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare;
            bool gpuSchedulerRequested = Settings.GlobalIllumination.DdgiSchedulerMode != DdgiSchedulerMode.CpuReference;
            bool gpuSchedulerQueueRequested = Settings.GlobalIllumination.DdgiSchedulerMode == DdgiSchedulerMode.Gpu ||
                                              (ddgiCompareMode && Settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering);
            AdvanceDdgiGpuSchedulerFallbackRetry(ddgiRayUpdateActive);
            string gpuSchedulerFallbackReason = ResolveDdgiGpuSchedulerFallbackReason(
                ddgiRayUpdateActive,
                ddgiCompareMode,
                Settings.GlobalIllumination.DdgiCompareModeUseGpuQueueForRendering);
            bool gpuSchedulerActive = ddgiRayUpdateActive &&
                                      gpuSchedulerQueueRequested &&
                                      string.IsNullOrEmpty(gpuSchedulerFallbackReason);
            int scheduledProbeUpdates;
            if (ddgiCompareMode && ddgiRayUpdateActive)
            {
                int cpuReferenceScheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(
                    enabled: true,
                    layout,
                    sceneData.CurrentFrameIndex);
                _ddgiProbeVolumeManager.CaptureGpuSchedulerValidationExpectedFrame(_currentFrame, cpuReferenceScheduledProbeUpdates);
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
                    _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame(_currentFrame);
                    scheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(ddgiRayUpdateActive, layout, sceneData.CurrentFrameIndex);
                    _ddgiProbeVolumeManager.UploadScheduledProbeUpdateQueue(_stagingRing, _currentCommandBuffer);
                }
            }
            else
            {
                _ddgiProbeVolumeManager.ClearGpuSchedulerValidationExpectedFrame(_currentFrame);
                scheduledProbeUpdates = _ddgiProbeVolumeManager.ScheduleProbeUpdates(ddgiRayUpdateActive, layout, sceneData.CurrentFrameIndex);
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
            sceneData.DdgiGatherTileCount = ddgiActive ? _ddgiGatherTileManager?.LastTileCount ?? 0 : 0;
            sceneData.DdgiGatherTileCountX = ddgiActive ? _ddgiGatherTileManager?.LastTileCountX ?? 0 : 0;
            sceneData.DdgiGatherTileCountY = ddgiActive ? _ddgiGatherTileManager?.LastTileCountY ?? 0 : 0;
            sceneData.DdgiGatherSelectedLocalTileCount = ddgiActive ? _ddgiGatherTileManager?.LastSelectedLocalTileCount ?? 0 : 0;
            sceneData.DdgiGatherSelectedClipmapTileCount = ddgiActive ? _ddgiGatherTileManager?.LastSelectedClipmapTileCount ?? 0 : 0;
            sceneData.DdgiGatherFallbackTileCount = ddgiActive ? _ddgiGatherTileManager?.LastFallbackTileCount ?? 0 : 0;
            sceneData.DdgiQualityTier = ddgiActive ? Settings.GlobalIllumination.DdgiQualityTier : DdgiQualityTier.DdgiHigh;
            sceneData.DdgiAdaptiveBudgetScale = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetScale : 1.0f;
            sceneData.DdgiAdaptiveBudgetReduced = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetReduced : 0;
            sceneData.DdgiEmergencyDegradeActive = ddgiActive ? _ddgiProbeVolumeManager.LastEmergencyDegradeActive : 0;
            sceneData.DdgiEffectiveMaxShadedLights = ddgiActive ? _ddgiProbeVolumeManager.LastEffectiveMaxShadedLights : 0;
            sceneData.DdgiAdaptiveBudgetReason = ddgiActive ? _ddgiProbeVolumeManager.LastAdaptiveBudgetReason : string.Empty;
            sceneData.DdgiAsyncComputeEnabled = ddgiActive && Settings.AsyncCompute.DdgiUpdateEnabled && Settings.GlobalIllumination.DdgiAsyncComputeEnabled ? 1 : 0;
            sceneData.DdgiAtlasMemoryBudgetBytes = ddgiActive ? Settings.GlobalIllumination.DdgiAtlasMemoryBudgetBytes : 0UL;
            sceneData.DdgiProbeRelocationCount = ddgiRayUpdateActive && Settings.GlobalIllumination.DdgiProbeRelocationEnabled ? scheduledProbeUpdates : 0;
            sceneData.DdgiProbeClassificationCount = ddgiRayUpdateActive && Settings.GlobalIllumination.DdgiProbeClassificationEnabled ? scheduledProbeUpdates : 0;
            PopulateDdgiLightSelectionMetadata(sceneData, lightSnapshot, ddgiRayUpdateActive);
            UploadDdgiEmissiveSources(scene, sceneData, ddgiRayUpdateActive);
            PopulateDdgiDiagnostics(sceneData, layout, ddgiActive);
            sceneData.DdgiTextureBytes = ddgiActive ? _ddgiProbeVolumeManager.TextureBytes : 0;
            sceneData.DdgiBufferBytes = ddgiActive
                ? _ddgiProbeVolumeManager.BufferBytes + (_ddgiGatherTileManager?.CurrentBufferBytes ?? 0UL)
                : 0;
            sceneData.DdgiCurrentIrradianceAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.CurrentIrradianceAtlasBytes : 0UL;
            sceneData.DdgiCurrentVisibilityAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.CurrentVisibilityAtlasBytes : 0UL;
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
            sceneData.DdgiGpuSchedulerConsideredProbeCount = gpuSchedulerActive ? sceneData.DdgiActiveProbeCount : 0;
            sceneData.DdgiGpuSchedulerRequestCount = gpuSchedulerActive ? completedSchedulerCounters.RequestCount : 0u;
            sceneData.DdgiGpuSchedulerPrimaryRayCount = gpuSchedulerActive ? completedSchedulerCounters.PrimaryRayCount : 0u;
            sceneData.DdgiGpuSchedulerCandidateCount = gpuSchedulerActive ? completedSchedulerCounters.CandidateCount : 0u;
            sceneData.DdgiGpuSchedulerOverflowCount = gpuSchedulerActive ? completedSchedulerCounters.OverflowCount : 0u;
            sceneData.DdgiGpuSchedulerDuplicateRequestCount = gpuSchedulerActive ? completedSchedulerCounters.DuplicateRequestCount : 0u;
            sceneData.DdgiGpuSchedulerBudgetRejectedCount = gpuSchedulerActive ? completedSchedulerCounters.BudgetRejectedCount : 0u;
            sceneData.DdgiGpuSchedulerInvalidProbeCount = gpuSchedulerActive ? completedSchedulerCounters.InvalidProbeCount : 0u;
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
            sceneData.DdgiRayScratchBytes = ddgiActive ? _ddgiProbeVolumeManager.LastRayScratchBytes : 0UL;
            sceneData.DdgiUpdatedAtlasBytes = ddgiActive ? _ddgiProbeVolumeManager.LastUpdatedAtlasBytes : 0UL;
            sceneData.DdgiPublishedCacheLatencyFrames = ddgiActive ? _ddgiProbeVolumeManager.LastPublishedCacheLatencyFrames : 0;
            sceneData.CpuDdgiRecordMicroseconds = ddgiActive ? _ddgiProbeVolumeManager.LastUploadMicroseconds : 0;
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
            DdgiFrameLayout layout = DdgiFrameLayoutBuilder.Build(
                scene,
                camera,
                Settings.GlobalIllumination,
                _cameraRelativeDdgiClipmaps,
                unchecked((ulong)_currentFrame),
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
                sceneData.DdgiEmissiveSourceCount = 0;
                sceneData.DdgiEmissiveSourceRevision = _ddgiEmissiveSourceRevision;
                return;
            }

            int count = BuildDdgiEmissiveSources(scene, out ulong signature);
            if (signature != _lastDdgiEmissiveSourceSignature)
            {
                _lastDdgiEmissiveSourceSignature = signature;
                _ddgiEmissiveSourceRevision++;
                if (_ddgiEmissiveSourceRevision == 0)
                    _ddgiEmissiveSourceRevision = 1;
            }

            _ddgiEmissiveSourceCount = count;
            sceneData.DdgiEmissiveSourceCount = count;
            sceneData.DdgiEmissiveSourceRevision = _ddgiEmissiveSourceRevision;
            if (count == 0)
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
        }

        private int BuildDdgiEmissiveSources(Scene scene, out ulong signature)
        {
            int count = 0;
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
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].CenterRadius);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].RadianceImportance);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].BoundsMinRevision);
                signature = HashAdd(signature, _ddgiEmissiveSourceScratch[i].BoundsMaxFlags);
            }

            return count;
        }

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
                Vector3 radiance = new(
                    MathF.Max(material.DdgiAverageEmissive.X, material.Emissive.X),
                    MathF.Max(material.DdgiAverageEmissive.Y, material.Emissive.Y),
                    MathF.Max(material.DdgiAverageEmissive.Z, material.Emissive.Z));
                importance = MathF.Max(
                    material.DdgiAverageEmissive.W,
                    0.2126f * radiance.X + 0.7152f * radiance.Y + 0.0722f * radiance.Z);
                if (importance <= 0.0001f)
                    return false;

                BoundingBox bounds = tracked.Bounds;
                Vector3 center = bounds.Center;
                Vector3 size = bounds.Size;
                float objectRadius = MathF.Max(0.05f, size.Length() * 0.5f);
                float affectedRadius = MathF.Max(objectRadius, MathF.Sqrt(importance) * 4.0f);
                source = new GPUDdgiEmissiveSource
                {
                    CenterRadius = new Vector4(center.X, center.Y, center.Z, affectedRadius),
                    RadianceImportance = new Vector4(radiance.X, radiance.Y, radiance.Z, importance),
                    BoundsMinRevision = new Vector4(bounds.Min.X, bounds.Min.Y, bounds.Min.Z, _ddgiEmissiveSourceRevision),
                    BoundsMaxFlags = new Vector4(bounds.Max.X, bounds.Max.Y, bounds.Max.Z, 0.0f)
                };

                sourceSignature = HashAdd(HashAdd(tracked.EmissiveSignature, materialHandle.Index), materialHandle.Generation);
                sourceSignature = HashAdd(sourceSignature, source.CenterRadius);
                sourceSignature = HashAdd(sourceSignature, source.RadianceImportance);
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

            if (!Settings.GlobalIllumination.EffectiveUseDdgi)
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
                if (metadata.RenderMode != MaterialRenderMode.Opaque || metadata.IsGeometryDecal)
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
                ulong materialSignature = CreateDdgiMaterialSignature(materialHandle, materialData, metadata);
                ulong emissiveSignature = CreateDdgiEmissiveMaterialSignature(materialData);
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
            MaterialRenderMetadata metadata)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, materialHandle.Index);
            hash = HashAdd(hash, materialHandle.Generation);
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

        private static ulong CreateDdgiEmissiveMaterialSignature(GPUMaterialData materialData)
        {
            ulong hash = HashStart;
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
            _ddgiDirtyRegionScratch.Add(new DdgiDirtyRegion(expanded, reason));
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
            AccelerationStructureFrameStats stats = _accelerationStructureManager.PrepareFrame(
                scene,
                _stagingRing,
                _currentCommandBuffer,
                enabled,
                _gpuTimestamps,
                _currentFrame);
            sceneData.AccelerationStructureBottomLevelCount = stats.BottomLevelCount;
            sceneData.AccelerationStructureTopLevelInstanceCount = stats.TopLevelInstanceCount;
            sceneData.AccelerationStructureBlasBuildCount = stats.BlasBuildCount;
            sceneData.AccelerationStructureTlasBuildCount = stats.TlasBuildCount;
            sceneData.AccelerationStructureTlasUpdateCount = stats.TlasUpdateCount;
            sceneData.AccelerationStructureTlasSkipCount = stats.TlasSkipCount;
            sceneData.AccelerationStructureBytes = checked(
                stats.AccelerationStructureBytes +
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
            sceneData.DepthTaskInvocations = counters.DepthCandidates;
            sceneData.DepthFrustumCulledMeshletsGpu = counters.DepthFrustumCulled;
            sceneData.DepthEmittedMeshletsGpu = counters.DepthEmitted;
            sceneData.ForwardTaskInvocations = counters.ForwardCandidates;
            sceneData.ForwardFrustumCulledMeshletsGpu = counters.ForwardFrustumCulled;
            sceneData.ForwardOcclusionTestedMeshletsGpu = counters.ForwardOcclusionTested;
            sceneData.ForwardOcclusionCulledMeshletsGpu = counters.ForwardOcclusionCulled;
            sceneData.ForwardEmittedMeshletsGpu = counters.ForwardEmitted;
        }

        private static void ApplyCompletedSsgiCounters(SceneRenderingData sceneData, GpuMeshletCounters counters)
        {
            sceneData.SsgiRejectedHistoryPixelCount = counters.SsgiRejectedHistoryPixels;
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
                sceneData.SceneSubmissionGpuIndirectMeshletTaskCount = sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                    ? ClampUIntToInt(counters.EmittedCount)
                    : 0;
                sceneData.SceneSubmissionGpuLod0EmittedCount = ClampUIntToInt(counters.Lod0EmittedCount);
                sceneData.SceneSubmissionGpuLod1EmittedCount = ClampUIntToInt(counters.Lod1EmittedCount);
                sceneData.SceneSubmissionGpuLod2EmittedCount = ClampUIntToInt(counters.Lod2EmittedCount);
                sceneData.SceneSubmissionGpuMissingLodFallbackCount = ClampUIntToInt(counters.MissingLodFallbackCount);
                sceneData.SceneSubmissionGpuDepthSolidCandidateCount = ClampUIntToInt(counters.SolidDepthCandidateCount);
                sceneData.SceneSubmissionGpuDepthMaskedCandidateCount = ClampUIntToInt(counters.MaskedDepthCandidateCount);
                sceneData.SceneSubmissionGpuCompactedSolidDepthMeshletCount = ClampUIntToInt(counters.SolidDepthEmittedCount);
                sceneData.SceneSubmissionGpuCompactedMaskedDepthMeshletCount = ClampUIntToInt(counters.MaskedDepthEmittedCount);
                sceneData.SceneSubmissionGpuDepthOverflowCount = ClampUlongToInt(
                    (ulong)counters.SolidDepthOverflowCount + counters.MaskedDepthOverflowCount);
                ApplyDirectionalShadowCompactionCounters(sceneData, counters);
            }

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
            RecreateSwapchain();
            
            // Update camera aspect ratio if camera is provided
            // (Camera aspect ratio should be updated by the caller)
        }

        private void EnsureRenderTargetProfile()
        {
            if (_renderTargets == null)
                return;

            bool aoEnabled = Settings.AmbientOcclusion.Enabled;
            AntiAliasingMode aaMode = Settings.AntiAliasing.EffectiveMode;
            bool motionVectorTargetEnabled = NeedsMotionVectors(Settings);
            int bloomMipCount = Settings.Bloom.MipCount;
            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            bool weightedOitTargetEnabled = IsWeightedOitTargetEnabled(Settings);
            bool globalIlluminationTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
            float globalIlluminationResolutionScale = Settings.GlobalIllumination.ResolutionScale;
            DynamicResolutionScaleDecision scaleDecision = ResolveSceneResolutionScaleDecision();
            float effectiveResolutionScale = scaleDecision.CommittedScale;
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, effectiveResolutionScale);
            bool featureTargetsChanged =
                _lastAmbientOcclusionTargetEnabled != aoEnabled ||
                _lastAntiAliasingTargetMode != aaMode ||
                _lastMotionVectorTargetEnabled != motionVectorTargetEnabled ||
                _lastTransparencyTargetMode != Settings.Transparency.Mode ||
                _lastBloomTargetMipCount != bloomMipCount ||
                _lastFogTargetEnabled != fogTargetEnabled ||
                _lastGlobalIlluminationTargetEnabled != globalIlluminationTargetEnabled ||
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
                Settings.AmbientOcclusion.ResolutionScale,
                globalIlluminationResolutionScale,
                bloomMipCount,
                aoEnabled,
                globalIlluminationTargetEnabled,
                aaMode,
                motionVectorTargetEnabled,
                fogTargetEnabled,
                weightedOitTargetEnabled);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            _hizVisibilityPolicyState.PyramidValid = false;
            RegisterSceneRenderTextures();
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _renderGraph.OnSwapchainRecreated();
            _lastAmbientOcclusionTargetEnabled = aoEnabled;
            _lastAntiAliasingTargetMode = aaMode;
            _lastMotionVectorTargetEnabled = motionVectorTargetEnabled;
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = bloomMipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastGlobalIlluminationTargetEnabled = globalIlluminationTargetEnabled;
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

        private void RecreateSwapchain()
        {
            if (_frameInProgress)
                throw new InvalidOperationException("Swapchain cannot be recreated while command recording is in progress.");

            _swapchain.RecreateSwapchain(
                () => RecordDeviceWaitIdle(
                    RuntimeStallReason.ResourceResize,
                    "Swapchain recreate",
                    _context.WaitIdle));
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
                IsWeightedOitTargetEnabled(Settings));
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
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastMotionVectorTargetEnabled = NeedsMotionVectors(Settings);
            _lastTransparencyTargetMode = Settings.Transparency.Mode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = IsFogTargetEnabled(Settings);
            _lastGlobalIlluminationTargetEnabled = Settings.GlobalIllumination.EffectiveUseSsgi;
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
            _spotShadowAtlas?.Register(_bindlessHeap);
            _pointShadowCubemapArray?.Register(_bindlessHeap);
            _environmentManager?.Register(_bindlessHeap);
            _environmentManager?.RegisterReflectionProbeFallback(_bindlessHeap);
            _reflectionProbeManager?.Register(_bindlessHeap);
            _ddgiProbeVolumeManager?.Register(_bindlessHeap);
            _ddgiGatherTileManager?.Register(_bindlessHeap);
            _renderGraph.OnSwapchainRecreated();
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

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            if (disposing)
            {
                // Wait for all frames to complete
                var vk = _context.Api;
                vk.DeviceWaitIdle(_context.Device);
                
                // Cleanup in reverse order
                _renderGraph.Cleanup();
                _gpuTimestamps.Dispose();
                _diagnosticsBuffer.Dispose();
                _directionalShadowResources?.Dispose();
                _spotShadowAtlas?.Dispose();
                _pointShadowCubemapArray?.Dispose();
                _environmentManager?.Dispose();
                _reflectionProbeManager?.Dispose();
                if (_ddgiEmissiveSourceBuffer.IsValid)
                {
                    _bufferManager.DestroyBuffer(_ddgiEmissiveSourceBuffer);
                    _ddgiEmissiveSourceBuffer = BufferHandle.Invalid;
                    _ddgiEmissiveSourceBufferSize = 0;
                }
                _ddgiProbeVolumeManager?.Dispose();
                _ddgiGatherTileManager?.Dispose();
                _accelerationStructureManager?.Dispose();
                _autoExposureManager?.Dispose();
                _smaaResources?.Dispose();
                _hizDepthPyramid?.Dispose();
                _renderTargets?.Dispose();
                
                _meshPipeline?.Dispose();
                _computePipeline?.Dispose();
                _skinningPass?.Dispose();
                _gpuParticleResetPass?.Dispose();
                _gpuParticleSimulatePass?.Dispose();
                _gpuParticleSortPass?.Dispose();
                _foliageCullPass?.Dispose();
                _skinningManager?.Dispose();
                _particleSystemManager?.Dispose();
                _gpuParticleRuntimeManager?.Dispose();
                _foliageManager?.Dispose();
                _foliagePipeline?.Dispose();
                _compositePipeline?.Dispose();
                _ldrCompositePipeline?.Dispose();
                _weightedOitCompositePipeline?.Dispose();
                _ssgiCompositePipeline?.Dispose();
                _skyboxPipeline?.Dispose();
                _particlePipeline?.Dispose();

                if (_ownsDependencies)
                {
                    _deleter.Cleanup();
                    _stagingRing.Dispose();
                    _swapchain.Dispose();
                    _cmd.Dispose();
                    _sync.Dispose();
                    _bindlessHeap.Dispose();
                    _lightManager.Dispose();
                    _materialManager.Dispose();
                    _meshManager.Dispose();
                    _textureManager.Dispose();
                    _bufferManager.Dispose();
                    _context.Dispose();
                }
            }
            
            System.Diagnostics.Debug.WriteLine("VulkanRenderer disposed.");
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
