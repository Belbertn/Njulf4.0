using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
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

        internal static IReadOnlyList<string> ProductionRenderPassOrder { get; } = new[]
        {
            "SceneOpaqueCompactionPass",
            "DirectionalShadowPass",
            "SpotShadowPass",
            "PointShadowPass",
            "DepthPrePass",
            "MotionVectorPass",
            "HiZBuildPass",
            "AmbientOcclusionPass",
            "AmbientOcclusionBlurPass",
            "TiledLightCullingPass",
            "ForwardPlusPass",
            "SkyboxPass",
            "TransparentForwardPass",
            "ParticlePass",
            "DebugDrawPass",
            "FogPass",
            "AutoExposurePass",
            "BloomPass",
            "ToneMapCompositePass",
            "AntiAliasingPass"
        };

        internal static IReadOnlyList<string> PhaseOneRenderPassOrder => ProductionRenderPassOrder;

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
        private AutoExposureManager? _autoExposureManager;
        private SmaaResources? _smaaResources;
        private SkinningManager _skinningManager = null!;
        private GpuParticleRuntimeManager _gpuParticleRuntimeManager = null!;
        private readonly LocalShadowSelector _localShadowSelector = new();
        private readonly GPUSpotShadow[] _spotShadowScratch = new GPUSpotShadow[32];
        private readonly GPUPointShadow[] _pointShadowScratch = new GPUPointShadow[4];
        private readonly GPULocalLightShadowIndex[] _localShadowIndexScratch = new GPULocalLightShadowIndex[LightManager.MaxLights];
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
        private SkyboxPipeline _skyboxPipeline = null!;
        private ParticlePipeline _particlePipeline = null!;
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
        private bool _adaptiveHiZSuppressed;
        private int _adaptiveHiZProbeCountdown;
        private long _lastParticleTimestamp;
        private float _particleTimeSeconds;
        private long _lastAcquireImageMicroseconds;
        private long _lastQueueSubmitMicroseconds;
        private long _lastPresentMicroseconds;
        private bool _lastAmbientOcclusionTargetEnabled = true;
        private AntiAliasingMode _lastAntiAliasingTargetMode = AntiAliasingMode.SmaaMedium;
        private int _lastBloomTargetMipCount = 6;
        private bool _lastFogTargetEnabled = true;
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
            float sceneResolutionScale = ResolveSceneResolutionScale();
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, sceneResolutionScale);
            _renderTargets = new RenderTargetManager(
                _context,
                sceneRenderExtent,
                _swapchain.DepthFormat,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.AntiAliasing.EffectiveMode,
                fogTargetEnabled);
            _lastAmbientOcclusionTargetEnabled = Settings.AmbientOcclusion.Enabled;
            _lastAntiAliasingTargetMode = Settings.AntiAliasing.EffectiveMode;
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Initial render targets";
            _hizDepthPyramid = new HiZDepthPyramid(_context, CreateHiZExtent(sceneRenderExtent));
            _directionalShadowResources = new DirectionalShadowResources(_context, _bufferManager, Settings.Shadows);
            _spotShadowAtlas = new SpotShadowAtlas(_context, _bufferManager, Settings.Shadows);
            _pointShadowCubemapArray = new PointShadowCubemapArray(_context, _bufferManager, Settings.Shadows);
            _environmentManager = new EnvironmentManager(_context, _bufferManager, _textureManager, Settings);
            _reflectionProbeManager = new ReflectionProbeManager(_context, _bufferManager, Settings);
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
                _swapchain.DepthFormat);
            
            // Create compute pipeline for light culling
            _computePipeline = new ComputePipeline(_context, _bindlessHeap);

            _compositePipeline = new CompositePipeline(_context, _bindlessHeap, _swapchain.SurfaceFormat);
            _ldrCompositePipeline = new CompositePipeline(_context, _bindlessHeap, RenderTargetManager.LdrSceneColorFormat);
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

            _renderGraph.AddPass(_sceneOpaqueCompactionPass);
            
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
            _renderGraph.AddPass(directionalShadowPass);

            var spotShadowPass = new SpotShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _spotShadowAtlas!,
                Settings.Shadows,
                _foliagePipeline,
                _foliageManager);
            _renderGraph.AddPass(spotShadowPass);

            var pointShadowPass = new PointShadowPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _meshPipeline,
                _pointShadowCubemapArray!,
                Settings.Shadows,
                _foliagePipeline,
                _foliageManager);
            _renderGraph.AddPass(pointShadowPass);

            // Create depth pre-pass
            var depthPrePass = new DepthPrePass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!, _foliagePipeline, _bufferManager, _foliageManager);
            _renderGraph.AddPass(depthPrePass);

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
            _renderGraph.AddPass(motionVectorPass);

            var hizBuildPass = new HiZBuildPass(
                _context, _swapchain, _bindlessHeap, _hizDepthPyramid!, _renderTargets!);
            _renderGraph.AddPass(hizBuildPass);

            var ambientOcclusionPass = new AmbientOcclusionPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(ambientOcclusionPass);

            var ambientOcclusionBlurPass = new AmbientOcclusionBlurPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(ambientOcclusionBlurPass);
            
            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager, _renderTargets!);
            _renderGraph.AddPass(lightCullingPass);
            
            // Create forward+ rendering pass
            var forwardPass = new ForwardPlusPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!, _foliagePipeline, _bufferManager, _foliageManager);
            _renderGraph.AddPass(forwardPass);

            var skyboxPass = new SkyboxPass(
                _context, _swapchain, _bindlessHeap, _skyboxPipeline, _renderTargets!, Settings);
            _renderGraph.AddPass(skyboxPass);

            var transparentForwardPass = new TransparentForwardPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!);
            _renderGraph.AddPass(transparentForwardPass);

            var particlePass = new ParticlePass(
                _context, _swapchain, _bindlessHeap, _particlePipeline, _bufferManager, _renderTargets!, Settings.Particles);
            _renderGraph.AddPass(particlePass);

            var debugDrawPass = new DebugDrawPass(
                _context, _swapchain, _bindlessHeap, _bufferManager, _stagingRing, _renderTargets!);
            _renderGraph.AddPass(debugDrawPass);

            var fogPass = new FogPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(fogPass);

            var autoExposurePass = new AutoExposurePass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings, _autoExposureManager!);
            _renderGraph.AddPass(autoExposurePass);

            var bloomPass = new BloomPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(bloomPass);

            var toneMapCompositePass = new ToneMapCompositePass(
                _context, _swapchain, _bindlessHeap, _compositePipeline, _ldrCompositePipeline, _renderTargets!, Settings);
            _renderGraph.AddPass(toneMapCompositePass);

            var antiAliasingPass = new AntiAliasingPass(
                _context,
                _swapchain,
                _bindlessHeap,
                _renderTargets!,
                Settings,
                () => _smaaResources?.IsReady == true);
            _renderGraph.AddPass(antiAliasingPass);
            ValidatePhaseOneRenderPassOrder(_renderGraph.PassNames);
            
            _renderGraph.Initialize();
            System.Diagnostics.Debug.WriteLine("Render graph initialized.");
        }

        private static void ValidatePhaseOneRenderPassOrder(IReadOnlyList<string> actualPassOrder)
        {
            if (actualPassOrder.Count != ProductionRenderPassOrder.Count)
                throw new InvalidOperationException(
                    $"Render graph pass count changed. Expected {string.Join(", ", ProductionRenderPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");

            for (int i = 0; i < ProductionRenderPassOrder.Count; i++)
            {
                if (!string.Equals(actualPassOrder[i], ProductionRenderPassOrder[i], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Render graph pass order changed. Expected {string.Join(", ", ProductionRenderPassOrder)}; actual {string.Join(", ", actualPassOrder)}.");
                }
            }
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
            
            System.Diagnostics.Debug.WriteLine("Scene buffers registered.");
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
                _currentFrame,
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
            bool hiZEnabledThisFrame = ShouldEnableHiZThisFrame(_completedGpuCounters);
            sceneData.DepthPrePassEnabled = EnableDepthPrePass && !isolateSkinnedAnimationDebug;
            sceneData.HiZBuildEnabled = EnableDepthPrePass && hiZEnabledThisFrame;
            sceneData.HiZBuildEnabled &= !isolateSkinnedAnimationDebug;
            sceneData.OcclusionCullingEnabled = sceneData.DepthPrePassEnabled && hiZEnabledThisFrame && Settings.HiZTestMode != HiZTestMode.Off;
            sceneData.HiZTestMode = sceneData.OcclusionCullingEnabled ? Settings.HiZTestMode : HiZTestMode.Off;
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
            BuildDebugOverlayDrawCommands(scene, sceneData);
            sceneData.DebugDrawSnapshot = _debugDraw.Snapshot();
            ApplyCompletedSceneSubmissionCounters(sceneData, _completedSceneSubmissionCounters);
            ApplyCompletedSceneSubmissionValidation(sceneData, _completedSceneSubmissionValidation);
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
            if (MeshletDiagnosticCountersActive)
                ApplyCompletedGpuCounters(sceneData, _completedGpuCounters);
            if (particlesAllowed)
                ApplyCompletedGpuParticleCounters(sceneData, _completedGpuParticleCounters);
            if (!isolateSkinnedAnimationDebug)
                ApplyCompletedFoliageCounters(sceneData, _completedFoliageCounters);
            ApplyCompletedGpuTimings(sceneData, _gpuTimestamps.LastCompletedSnapshot);
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

        private bool ShouldEnableHiZThisFrame(GpuMeshletCounters counters)
        {
            if (!EnableDepthPrePass || !EnableHiZOcclusion)
            {
                _adaptiveHiZSuppressed = false;
                _adaptiveHiZProbeCountdown = 0;
                return false;
            }

            if (!EnableAdaptiveHiZOcclusion)
                return true;

            if (!MeshletDiagnosticCountersActive)
            {
                _adaptiveHiZSuppressed = false;
                _adaptiveHiZProbeCountdown = 0;
                return true;
            }

            const int MinMeasuredOcclusionTests = 512;
            const float MinUsefulOcclusionCullRate = 0.03f;
            const int ProbeIntervalFrames = 60;

            if (counters.ForwardOcclusionTested >= MinMeasuredOcclusionTests)
            {
                float cullRate = (float)counters.ForwardOcclusionCulled / counters.ForwardOcclusionTested;
                _adaptiveHiZSuppressed = cullRate < MinUsefulOcclusionCullRate;
                _adaptiveHiZProbeCountdown = _adaptiveHiZSuppressed ? ProbeIntervalFrames : 0;
            }
            else if (_adaptiveHiZSuppressed && _adaptiveHiZProbeCountdown > 0)
            {
                _adaptiveHiZProbeCountdown--;
            }

            if (_adaptiveHiZSuppressed && _adaptiveHiZProbeCountdown == 0)
            {
                _adaptiveHiZProbeCountdown = ProbeIntervalFrames;
                return true;
            }

            return !_adaptiveHiZSuppressed;
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
            SceneSubmissionMode sceneSubmissionActiveMode = ResolveSceneSubmissionMode(sceneData);
            int spotShadowMeshletLightTests = CalculateSpotShadowMeshletLightTests(sceneData);
            int pointShadowMeshletFaceTests = CalculatePointShadowMeshletFaceTests(sceneData);
            bool spotShadowGpuCompactionJustified = IsSpotShadowGpuCompactionJustified(sceneData, spotShadowMeshletLightTests);
            bool pointShadowGpuCompactionJustified = IsPointShadowGpuCompactionJustified(sceneData, pointShadowMeshletFaceTests);
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
                FoliagePatchCount = sceneData.FoliagePatchCount,
                FoliagePrototypeCount = sceneData.FoliagePrototypeCount,
                FoliageClusterCount = sceneData.FoliageClusterCount,
                FoliageVisibleClusterCount = sceneData.FoliageVisibleClusterCount,
                FoliageCulledClusterCount = sceneData.FoliageCulledClusterCount,
                FoliageVisibleMeshletDrawCount = sceneData.FoliageVisibleMeshletDrawCount,
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
                GpuMeshletCountersEnabled = gpuMeshletCountersEnabled ? 1 : 0,
                GpuMeshletCountersStatus = gpuMeshletCountersStatus,
                SceneSubmissionActiveMode = sceneSubmissionActiveMode,
                SceneSubmissionCpuCandidateCount = sceneData.MeshletCandidatesCpu,
                SceneSubmissionGpuEmittedCount = sceneData.SceneSubmissionGpuCompactedOpaqueMeshletCount,
                SceneSubmissionIndirectTaskCount = sceneData.SceneSubmissionGpuIndirectMeshletTaskCount,
                SceneSubmissionGpuCompactionEnabled = sceneData.SceneSubmissionGpuCompactionEnabled ? 1 : 0,
                SceneSubmissionIndirectMeshletDispatchEnabled = sceneData.SceneSubmissionIndirectMeshletDispatchEnabled ? 1 : 0,
                SceneSubmissionGpuLodSelectionEnabled = sceneData.SceneSubmissionGpuLodSelectionEnabled ? 1 : 0,
                SceneSubmissionGpuShadowCompactionEnabled = sceneData.SceneSubmissionGpuShadowCompactionEnabled ? 1 : 0,
                SceneSubmissionValidationCompareCpuGpuLists = sceneData.SceneSubmissionValidationCompareCpuGpuLists ? 1 : 0,
                SceneSubmissionGpuCompactionActive = sceneData.SceneSubmissionGpuCompactionActive ? 1 : 0,
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
                SecondaryCommandBufferEnabled = sceneData.SecondaryCommandBufferEnabled,
                SecondaryCommandBufferPassCount = sceneData.SecondaryCommandBufferPassCount,
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
                MaxLightsInAnyTile = sceneData.MaxLightsPerTile,
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
            sceneData.GpuLightCullMicroseconds = timings.GetGpuMicrosecondsOrZero("TiledLightCullingPass");
            sceneData.GpuFoliageCullMicroseconds = timings.GetGpuMicrosecondsOrZero("FoliageCullPass");
            sceneData.GpuFoliageShadowMicroseconds = sceneData.FoliageCastShadows && sceneData.FoliageClusterCount > 0
                ? sceneData.GpuDirectionalShadowMicroseconds
                : 0;
            sceneData.GpuForwardOpaqueMicroseconds = timings.GetGpuMicrosecondsOrZero("ForwardPlusPass");
            sceneData.GpuTransparentMicroseconds = timings.GetGpuMicrosecondsOrZero("TransparentForwardPass");
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

            if (!sceneData.SceneSubmissionGpuCompactionEnabled)
            {
                sceneData.SceneSubmissionFallbackReason = string.Empty;
                return;
            }

            if (counters.OverflowCount > 0)
                sceneData.SceneSubmissionFallbackReason = "previous GPU opaque compaction overflow";
            else if (counters.SolidDepthOverflowCount > 0 || counters.MaskedDepthOverflowCount > 0)
                sceneData.SceneSubmissionFallbackReason = "previous GPU depth compaction overflow";
            else if (Sum(counters.DirectionalStaticShadowOverflowCounts) > 0 ||
                     Sum(counters.DirectionalDynamicShadowOverflowCounts) > 0)
                sceneData.SceneSubmissionFallbackReason = "previous GPU directional shadow compaction overflow";
            else
                sceneData.SceneSubmissionFallbackReason = string.Empty;
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

        private static SceneSubmissionMode ResolveSceneSubmissionMode(SceneRenderingData sceneData)
        {
            if (sceneData.SceneSubmissionGpuCompactionActive && sceneData.SceneSubmissionFallbackReason.Length == 0)
            {
                return sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                    ? SceneSubmissionMode.GpuCompactedIndirect
                    : SceneSubmissionMode.GpuCompactedDirect;
            }

            if (sceneData.SceneSubmissionGpuCompactionEnabled && sceneData.SceneSubmissionFallbackReason.Length > 0)
                return SceneSubmissionMode.CpuFallback;

            return SceneSubmissionMode.Cpu;
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
            int bloomMipCount = Settings.Bloom.MipCount;
            bool fogTargetEnabled = IsFogTargetEnabled(Settings);
            DynamicResolutionScaleDecision scaleDecision = ResolveSceneResolutionScaleDecision();
            float effectiveResolutionScale = scaleDecision.CommittedScale;
            Extent2D sceneRenderExtent = CreateSceneRenderExtent(_swapchain.Extent, effectiveResolutionScale);
            bool featureTargetsChanged =
                _lastAmbientOcclusionTargetEnabled != aoEnabled ||
                _lastAntiAliasingTargetMode != aaMode ||
                _lastBloomTargetMipCount != bloomMipCount ||
                _lastFogTargetEnabled != fogTargetEnabled;
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
                Settings.AmbientOcclusion.ResolutionScale,
                bloomMipCount,
                aoEnabled,
                aaMode,
                fogTargetEnabled);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(sceneRenderExtent));
            RegisterSceneRenderTextures();
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.HiZSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _renderGraph.OnSwapchainRecreated();
            _lastAmbientOcclusionTargetEnabled = aoEnabled;
            _lastAntiAliasingTargetMode = aaMode;
            _lastBloomTargetMipCount = bloomMipCount;
            _lastFogTargetEnabled = fogTargetEnabled;
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
                    srcAccess = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
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
            _renderTargets?.Recreate(
                sceneRenderExtent,
                Settings.AmbientOcclusion.ResolutionScale,
                Settings.Bloom.MipCount,
                Settings.AmbientOcclusion.Enabled,
                Settings.AntiAliasing.EffectiveMode,
                IsFogTargetEnabled(Settings));
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
            _lastBloomTargetMipCount = Settings.Bloom.MipCount;
            _lastFogTargetEnabled = IsFogTargetEnabled(Settings);
            _lastSceneRenderExtent = sceneRenderExtent;
            _lastEffectiveResolutionScale = sceneResolutionScale;
            _lastRenderTargetRecreateReason = "Swapchain resize";
            _meshPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _foliagePipeline?.Recreate(RenderTargetManager.SceneColorFormat, RenderTargetManager.MotionVectorFormat, _swapchain.DepthFormat);
            _compositePipeline?.Recreate(_swapchain.SurfaceFormat);
            _ldrCompositePipeline?.Recreate(RenderTargetManager.LdrSceneColorFormat);
            _skyboxPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _directionalShadowResources?.Register(_bindlessHeap, _swapchain.DepthImageView);
            _spotShadowAtlas?.Register(_bindlessHeap);
            _pointShadowCubemapArray?.Register(_bindlessHeap);
            _environmentManager?.Register(_bindlessHeap);
            _environmentManager?.RegisterReflectionProbeFallback(_bindlessHeap);
            _reflectionProbeManager?.Register(_bindlessHeap);
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
            RegisterAntiAliasingTextures();
            RegisterBloomTextures();
        }

        private void RegisterAmbientOcclusionTextures()
        {
            if (_renderTargets == null)
                return;

            if (!Settings.AmbientOcclusion.Enabled && _textureManager.DefaultWhiteTexture.IsValid)
            {
                ImageView whiteView = _textureManager.GetTextureView(_textureManager.DefaultWhiteTexture);
                ImageView normalView = _textureManager.DefaultNormalTexture.IsValid
                    ? _textureManager.GetTextureView(_textureManager.DefaultNormalTexture)
                    : whiteView;

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
                    BindlessIndex.SceneNormalTexture,
                    normalView,
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
                BindlessIndex.SceneNormalTexture,
                _renderTargets.AmbientOcclusionBlurred.View,
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
