using System;
using System.Collections.Generic;
using System.Diagnostics;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
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
        internal static IReadOnlyList<string> ProductionRenderPassOrder { get; } = new[]
        {
            "DirectionalShadowPass",
            "SpotShadowPass",
            "PointShadowPass",
            "DepthPrePass",
            "HiZBuildPass",
            "AmbientOcclusionPass",
            "AmbientOcclusionBlurPass",
            "TiledLightCullingPass",
            "ForwardPlusPass",
            "SkyboxPass",
            "TransparentForwardPass",
            "ParticlePass",
            "FogPass",
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
        private readonly ParticleSystemManager _particleSystemManager = new();
        private readonly bool _ownsDependencies;
        private HiZDepthPyramid? _hizDepthPyramid;
        private RenderTargetManager? _renderTargets;
        private DirectionalShadowResources? _directionalShadowResources;
        private SpotShadowAtlas? _spotShadowAtlas;
        private PointShadowCubemapArray? _pointShadowCubemapArray;
        private EnvironmentManager? _environmentManager;
        private ReflectionProbeManager? _reflectionProbeManager;
        private SmaaResources? _smaaResources;
        private SkinningManager _skinningManager = null!;
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
        
        // Pipelines
        private MeshPipeline _meshPipeline = null!;
        private ComputePipeline _computePipeline = null!;
        private CompositePipeline _compositePipeline = null!;
        private CompositePipeline _ldrCompositePipeline = null!;
        private SkyboxPipeline _skyboxPipeline = null!;
        private ParticlePipeline _particlePipeline = null!;
        private SkinningPass _skinningPass = null!;
        
        // State
        private int _currentFrame = 0;
        private uint _imageIndex;
        private CommandBuffer _currentCommandBuffer;
        private bool _isInitialized = false;
        private bool _disposed = false;
        private bool _frameInProgress;
        private bool _swapchainNeedsRecreate;
        private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
        private SceneRenderingData? _lastSceneData;
        private readonly DebugDrawList _debugDraw = new();
        private readonly ScreenshotCaptureService _screenshotCaptureService = new();
        private readonly RenderDocCaptureService _renderDocCaptureService = new();
        private GpuMeshletCounters _completedGpuCounters;
        private bool _adaptiveHiZSuppressed;
        private int _adaptiveHiZProbeCountdown;
        private long _lastParticleTimestamp;
        
        // Scene state
        private Color _clearColor = Color.CornflowerBlue;
        public RendererDiagnostics LastDiagnostics => _lastDiagnostics;
        public DebugDrawList DebugDraw => _debugDraw;
        public DebugOverlaySettings DebugOverlays => Settings.Debug;
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
            _particleSystemManager = new ParticleSystemManager(_context, _bufferManager, _stagingRing);
            _ownsDependencies = ownsDependencies;
        }
        
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            System.Diagnostics.Debug.WriteLine("Initializing VulkanRenderer...");
            
            _renderTargets = new RenderTargetManager(_context, _swapchain.Extent);
            _hizDepthPyramid = new HiZDepthPyramid(_context, CreateHiZExtent(_swapchain.Extent));
            _directionalShadowResources = new DirectionalShadowResources(_context, _bufferManager, Settings.Shadows);
            _spotShadowAtlas = new SpotShadowAtlas(_context, _bufferManager, Settings.Shadows);
            _pointShadowCubemapArray = new PointShadowCubemapArray(_context, _bufferManager, Settings.Shadows);
            _environmentManager = new EnvironmentManager(_context, _bufferManager, _textureManager, Settings);
            _reflectionProbeManager = new ReflectionProbeManager(_context, _bufferManager, Settings);
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
            
            System.Diagnostics.Debug.WriteLine("Pipelines created.");
        }
        
        private void InitializeRenderGraph()
        {
            System.Diagnostics.Debug.WriteLine("Initializing render graph...");
            
            var directionalShadowPass = new DirectionalShadowPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _directionalShadowResources!, Settings.Shadows);
            _renderGraph.AddPass(directionalShadowPass);

            var spotShadowPass = new SpotShadowPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _spotShadowAtlas!, Settings.Shadows);
            _renderGraph.AddPass(spotShadowPass);

            var pointShadowPass = new PointShadowPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _pointShadowCubemapArray!, Settings.Shadows);
            _renderGraph.AddPass(pointShadowPass);

            // Create depth pre-pass
            var depthPrePass = new DepthPrePass(
                _context, _swapchain, _bindlessHeap, _meshPipeline);
            _renderGraph.AddPass(depthPrePass);

            var hizBuildPass = new HiZBuildPass(
                _context, _swapchain, _bindlessHeap, _hizDepthPyramid!);
            _renderGraph.AddPass(hizBuildPass);

            var ambientOcclusionPass = new AmbientOcclusionPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(ambientOcclusionPass);

            var ambientOcclusionBlurPass = new AmbientOcclusionBlurPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(ambientOcclusionBlurPass);
            
            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager);
            _renderGraph.AddPass(lightCullingPass);
            
            // Create forward+ rendering pass
            var forwardPass = new ForwardPlusPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!);
            _renderGraph.AddPass(forwardPass);

            var skyboxPass = new SkyboxPass(
                _context, _swapchain, _bindlessHeap, _skyboxPipeline, _renderTargets!, Settings);
            _renderGraph.AddPass(skyboxPass);

            var transparentForwardPass = new TransparentForwardPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline, _renderTargets!);
            _renderGraph.AddPass(transparentForwardPass);

            var particlePass = new ParticlePass(
                _context, _swapchain, _bindlessHeap, _particlePipeline, _renderTargets!, Settings.Particles);
            _renderGraph.AddPass(particlePass);

            var fogPass = new FogPass(
                _context, _swapchain, _bindlessHeap, _renderTargets!, Settings);
            _renderGraph.AddPass(fogPass);

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
                _swapchain.DepthImageView,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.HdrSceneColorTexture,
                _renderTargets!.SceneColor.View,
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
            
            // Register scene data buffers
            _sceneDataBuilder.RegisterBuffers(_bindlessHeap);
            _skinningManager.RegisterBuffers(_bindlessHeap);
            _particleSystemManager.RegisterBuffers(_bindlessHeap);
            _diagnosticsBuffer.RegisterBuffers(_bindlessHeap);
            _directionalShadowResources!.Register(_bindlessHeap);
            _spotShadowAtlas!.Register(_bindlessHeap);
            _pointShadowCubemapArray!.Register(_bindlessHeap);
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
            
            // Wait for previous frame to complete
            _sync.WaitForFence(_currentFrame);
            _diagnosticsBuffer.ReadCompletedFrame(_currentFrame);
            _completedGpuCounters = _diagnosticsBuffer.GetLastCompletedCounters(_currentFrame);
            
            // Process completed frame deletions
            _deleter.ProcessCompletedFrame(_sync.GetInFlightFence(_currentFrame));

            // The staging ring slot is safe to reuse after the frame fence has completed.
            _stagingRing.BeginFrame(_currentFrame);
            
            // Acquire next swapchain image
            Result acquireResult = _swapchain.TryAcquireNextImage(
                _sync.GetImageAvailableSemaphore(_currentFrame),
                out _imageIndex);

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchain();
                return false;
            }

            if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
                throw new VulkanException("Failed to acquire swapchain image", acquireResult);

            _swapchainNeedsRecreate = acquireResult == Result.SuboptimalKhr;
            
            // Reset fence for current frame
            _sync.ResetFence(_currentFrame);

            // Reset and begin recording the primary command buffer owned by this frame.
            _cmd.ResetGraphicsCommandBuffer(_currentFrame);
            
            _currentCommandBuffer = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
            _frameInProgress = true;

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
            
            result = vk.QueueSubmit(
                _context.GraphicsQueue,
                1,
                &submitInfo,
                _sync.GetInFlightFence(_currentFrame));
            
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
            
            Result presentResult = _swapchain.Present(&presentInfo);
            
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
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)) // Reverse-Z: clear to 0
            };
            
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchain.Extent),
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
            DebugOverlayMode activeDebugOverlay = debugEnabled ? Settings.Debug.Mode : DebugOverlayMode.None;
            _sceneDataBuilder.CaptureCpuSnapshots = debugEnabled &&
                                                    (Settings.Debug.CpuSnapshotsEnabled ||
                                                    activeDebugOverlay is DebugOverlayMode.ObjectBounds or
                                                        DebugOverlayMode.MeshletBounds or
                                                        DebugOverlayMode.SelectedObject or
                                                        DebugOverlayMode.MaterialInspection);
            _debugDraw.Enabled = debugEnabled;
            _debugDraw.MaxLineSegments = Settings.Debug.MaxDebugLineSegments;
            
            _lightManager.UploadToGPU(_stagingRing, _currentCommandBuffer);
            ulong lightUploadBytes = _lightManager.LastUploadBytes;
            LightFrameSnapshot lightSnapshot = _lightManager.GetFrameSnapshot();
            int lightCount = lightSnapshot.Count;
            int directionalLightCount = lightSnapshot.DirectionalLightCount;
            int localLightCount = lightSnapshot.LocalLightCount;
            LocalShadowSelection localShadowSelection = _localShadowSelector.Select(lightSnapshot.Lights.Span, camera, Settings.Shadows);
            bool hasLocalShadows = localShadowSelection.SpotLights.Length > 0 || localShadowSelection.PointLights.Length > 0;
            GPUShadowData shadowData = CreateDirectionalShadowData(camera, lightSnapshot, out bool directionalShadowsEnabled, out int shadowedDirectionalLightIndex);
            GPUShadowData? enabledShadowData = directionalShadowsEnabled ? shadowData : null;
            int enabledShadowCascadeCount = directionalShadowsEnabled ? Settings.Shadows.DirectionalCascadeCount : 0;

            Vector2 jitter = AntiAliasingJitter.GetHaltonJitter(
                _currentFrame,
                Settings.AntiAliasing.JitterSampleCount,
                _swapchain.Extent.Width,
                _swapchain.Extent.Height,
                Settings.AntiAliasing.JitterEnabled && Settings.AntiAliasing.Mode == AntiAliasingMode.Taa);

            bool gpuSkinningEnabled = Settings.Animation.Enabled &&
                                      Settings.Animation.SkinningMode == AnimationSkinningMode.GpuCompute;
            SkinningFrameStats skinningStats = _skinningManager.PrepareFrame(
                scene,
                _currentCommandBuffer,
                gpuSkinningEnabled,
                Settings.Animation.MaxAnimatedInstances);

            // Build and upload scene data using SceneDataBuilder
            var sceneData = _sceneDataBuilder.Build(
                scene,
                camera,
                _swapchain.Extent.Width,
                _swapchain.Extent.Height,
                _currentCommandBuffer,
                useTiledLightCulling: localLightCount > 0,
                directionalShadowData: enabledShadowData,
                directionalShadowCascadeCount: enabledShadowCascadeCount,
                buildLocalShadowMeshlets: hasLocalShadows,
                projectionJitter: jitter,
                transparencySettings: Settings.Transparency,
                decalSettings: Settings.Decals);
            sceneData.FrameIndex = _currentFrame;
            sceneData.DebugToolingEnabled = debugEnabled;
            sceneData.DebugOverlayMode = activeDebugOverlay;
            sceneData.CpuDebugSnapshotsEnabled = _sceneDataBuilder.CaptureCpuSnapshots;
            sceneData.DebugSelectedObjectIndex = Settings.Debug.SelectedObjectIndex;
            sceneData.DebugDrawSnapshot = _debugDraw.Snapshot();
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
            ParticleSimulationFrame particleFrame = _particleSystemManager.Update(
                scene,
                Settings.Particles,
                camera.Position,
                GetParticleDeltaSeconds());
            ParticleSystemManager.PopulateSceneData(sceneData, Settings.Particles, particleFrame);
            _particleSystemManager.UploadFrame(
                particleFrame,
                Settings.Particles,
                _textureManager,
                _currentCommandBuffer,
                sceneData);
            sceneData.UploadedBytes += sceneData.ParticleInstanceUploadBytes;
            bool hiZEnabledThisFrame = ShouldEnableHiZThisFrame(_completedGpuCounters);
            sceneData.DepthPrePassEnabled = EnableDepthPrePass;
            sceneData.HiZBuildEnabled = EnableDepthPrePass && hiZEnabledThisFrame;
            sceneData.OcclusionCullingEnabled = EnableDepthPrePass && hiZEnabledThisFrame;
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
            sceneData.FogDirectionalInscatteringDirection = ResolveFogDirectionalInscatteringDirection(lightSnapshot);
            sceneData.DebugViewMode = ResolveForwardDebugViewMode();
            sceneData.JitterEnabled = jitter.X != 0.0f || jitter.Y != 0.0f ? 1 : 0;
            sceneData.JitterX = jitter.X;
            sceneData.JitterY = jitter.Y;
            PrepareDirectionalShadows(sceneData, shadowData, directionalShadowsEnabled, shadowedDirectionalLightIndex);
            PrepareLocalShadows(sceneData, localShadowSelection, lightCount);
            _environmentManager?.Upload(_stagingRing, _currentCommandBuffer);
            PrepareReflectionProbes(scene, sceneData);
            _diagnosticsBuffer.ResetCounters(_currentCommandBuffer, _currentFrame);
            _skinningPass.Execute(_currentCommandBuffer, _currentFrame, sceneData);
            
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
            _renderGraph.Execute(_currentCommandBuffer, _currentFrame, sceneData);
            ApplyCompletedGpuCounters(sceneData, _completedGpuCounters);
            sceneData.CpuTotalDrawSceneMicroseconds = ElapsedMicroseconds(drawSceneStart);
            _lastSceneData = sceneData;
            _lastDiagnostics = BuildDiagnostics(sceneData);
            _debugDraw.ClearFrame();
        }

        private uint ResolveForwardDebugViewMode()
        {
            if (EnableMeshletDebugView)
                return 1u;
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
                _directionalShadowResources.Register(_bindlessHeap);

            Light shadowLight = default;
            bool hasShadowLight = lightSnapshot.DirectionalLightCount > 0 && lightSnapshot.HasShadowCastingDirectionalLight;
            if (hasShadowLight)
            {
                lightIndex = lightSnapshot.FirstShadowCastingDirectionalLightIndex;
                shadowLight = lightSnapshot.FirstShadowCastingDirectionalLight;
            }
            enabled = shadowSettings.DirectionalShadowsEnabled && hasShadowLight;

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
            sceneData.DirectionalShadowMapSize = shadowSettings.DirectionalShadowMapSize;
            sceneData.DirectionalShadowCascadeCount = shadowSettings.DirectionalCascadeCount;
            sceneData.ShadowedDirectionalLightIndex = enabled ? lightIndex : -1;
            sceneData.ShadowDebugView = shadowSettings.DebugView;
            sceneData.ShadowNormalBias = shadowSettings.NormalBias;
            sceneData.ShadowSlopeScaledDepthBias = shadowSettings.SlopeScaledDepthBias;
            sceneData.ShadowData = shadowData;
        }

        private void PrepareLocalShadows(
            SceneRenderingData sceneData,
            LocalShadowSelection selection,
            int lightCount)
        {
            if (_spotShadowAtlas == null || _pointShadowCubemapArray == null)
                return;

            ShadowSettings shadowSettings = Settings.Shadows;
            if (_spotShadowAtlas.Ensure(shadowSettings))
            {
                _spotShadowAtlas.Register(_bindlessHeap);
                _hasUploadedSpotShadows = false;
                _hasUploadedLocalShadowIndices = false;
            }

            if (_pointShadowCubemapArray.Ensure(shadowSettings))
            {
                _pointShadowCubemapArray.Register(_bindlessHeap);
                _hasUploadedPointShadows = false;
            }

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
            sceneData.SpotShadowCandidateCount = selection.SpotCandidateCount;
            sceneData.SpotShadowSelectedCount = spotShadows.Length;
            sceneData.SpotShadowRejectedByBudgetCount = selection.SpotRejectedByBudgetCount;
            sceneData.SpotShadowAtlasSize = shadowSettings.SpotShadowAtlasSize;
            sceneData.SpotShadowTileSize = shadowSettings.SpotShadowTileSize;
            sceneData.SpotShadowAtlasCapacity = selection.SpotAtlasCapacity;
            sceneData.SpotShadowAtlasUsedTiles = spotShadows.Length;
            sceneData.PointShadowsEnabled = shadowSettings.PointShadowsEnabled;
            sceneData.PointShadowCandidateCount = selection.PointCandidateCount;
            sceneData.PointShadowSelectedCount = pointShadows.Length;
            sceneData.PointShadowRejectedByBudgetCount = selection.PointRejectedByBudgetCount;
            sceneData.PointShadowMapSize = shadowSettings.PointShadowMapSize;
            sceneData.PointShadowRenderedFaceCount = pointShadows.Length * 6;
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

        private RendererDiagnostics BuildDiagnostics(SceneRenderingData sceneData)
        {
            ModelRenderUploadDiagnostics uploadDiagnostics = _modelUploadService.LastUploadDiagnostics;
            int submittedOpaqueMeshlets = sceneData.ForwardTaskInvocations > 0
                ? sceneData.ForwardTaskInvocations
                : sceneData.OpaqueMeshletCount;
            int forwardCandidates = sceneData.ForwardTaskInvocations > 0
                ? sceneData.ForwardTaskInvocations
                : sceneData.OpaqueMeshletCount;
            int forwardVisibleAfterOcclusion = sceneData.ForwardEmittedMeshletsGpu > 0 || forwardCandidates == 0
                ? sceneData.ForwardEmittedMeshletsGpu
                : Math.Max(0, forwardCandidates - sceneData.ForwardFrustumCulledMeshletsGpu - sceneData.ForwardOcclusionCulledMeshletsGpu);
            return new RendererDiagnostics(
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
                Exposure: Settings.Exposure,
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
                CpuTrailBeamRecordMicroseconds = sceneData.CpuTrailBeamRecordMicroseconds,
                GpuParticleMicroseconds = sceneData.GpuParticleMicroseconds,
                GpuTrailBeamMicroseconds = sceneData.GpuTrailBeamMicroseconds,
                ParticleDrawCallCount = sceneData.ParticleDrawCallCount,
                ParticleInstanceBufferSize = sceneData.ParticleInstanceBufferSize,
                ParticleBatchBufferSize = sceneData.ParticleBatchBufferSize,
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
                DebugDecalVolumesDrawn = sceneData.DebugOverlayMode == DebugOverlayMode.DecalVolumes ? 0 : 0,
                GpuTimingEnabled = Settings.Debug.AllowGpuTiming ? 1 : 0,
                ScreenshotRequested = _screenshotCaptureService.PendingCount > 0 ? 1 : 0,
                ScreenshotPendingCount = _screenshotCaptureService.PendingCount,
                ScreenshotCompletedCount = _screenshotCaptureService.CompletedCount,
                LastScreenshotPath = _screenshotCaptureService.LastScreenshotPath,
                LastScreenshotError = _screenshotCaptureService.LastScreenshotError,
                RenderDocAvailable = _renderDocCaptureService.IsAvailable ? 1 : 0,
                RenderDocCaptureRequested = _renderDocCaptureService.CaptureRequested ? 1 : 0,
                RenderDocCaptureCompletedCount = _renderDocCaptureService.CompletedCount,
                LastRenderDocCaptureMessage = _renderDocCaptureService.LastMessage
            };
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
        
        public void Resize(int width, int height)
        {
            RecreateSwapchain();
            
            // Update camera aspect ratio if camera is provided
            // (Camera aspect ratio should be updated by the caller)
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

            _swapchain.RecreateSwapchain();
            _sync.EnsureRenderFinishedSemaphoreCapacity(_swapchain.ImageCount);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.DepthTexture,
                _swapchain.DepthImageView,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(_swapchain.Extent));
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                _bindlessHeap.ScreenSampler,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _renderTargets?.Recreate(_swapchain.Extent, Settings.AmbientOcclusion.ResolutionScale);
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HdrSceneColorTexture,
                _renderTargets!.SceneColor.View,
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
            _meshPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _compositePipeline?.Recreate(_swapchain.SurfaceFormat);
            _ldrCompositePipeline?.Recreate(RenderTargetManager.LdrSceneColorFormat);
            _skyboxPipeline?.Recreate(RenderTargetManager.SceneColorFormat, _swapchain.DepthFormat);
            _directionalShadowResources?.Register(_bindlessHeap);
            _spotShadowAtlas?.Register(_bindlessHeap);
            _pointShadowCubemapArray?.Register(_bindlessHeap);
            _environmentManager?.Register(_bindlessHeap);
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

        private static Extent2D CreateHiZExtent(Extent2D swapchainExtent)
        {
            return new Extent2D
            {
                Width = Math.Max(1u, swapchainExtent.Width / 2u),
                Height = Math.Max(1u, swapchainExtent.Height / 2u)
            };
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

        private void RegisterAmbientOcclusionTextures()
        {
            if (_renderTargets == null)
                return;

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
                _diagnosticsBuffer.Dispose();
                _directionalShadowResources?.Dispose();
                _spotShadowAtlas?.Dispose();
                _pointShadowCubemapArray?.Dispose();
                _environmentManager?.Dispose();
                _reflectionProbeManager?.Dispose();
                _smaaResources?.Dispose();
                _hizDepthPyramid?.Dispose();
                _renderTargets?.Dispose();
                
                _meshPipeline?.Dispose();
                _computePipeline?.Dispose();
                _skinningPass?.Dispose();
                _skinningManager?.Dispose();
                _particleSystemManager?.Dispose();
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
