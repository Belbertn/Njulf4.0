using System;
using System.Collections.Generic;
using System.Diagnostics;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
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
    public unsafe class VulkanRenderer : IRenderer, IDisposable
    {

        
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
        private readonly bool _ownsDependencies;
        private HiZDepthPyramid? _hizDepthPyramid;
        
        // Pipelines
        private MeshPipeline _meshPipeline = null!;
        private ComputePipeline _computePipeline = null!;
        
        // State
        private int _currentFrame = 0;
        private uint _imageIndex;
        private CommandBuffer _currentCommandBuffer;
        private bool _isInitialized = false;
        private bool _disposed = false;
        private bool _frameInProgress;
        private bool _swapchainNeedsRecreate;
        private RendererDiagnostics _lastDiagnostics = RendererDiagnostics.Empty;
        
        // Scene state
        private Color _clearColor = Color.CornflowerBlue;
        public RendererDiagnostics LastDiagnostics => _lastDiagnostics;
        public bool EnableHiZOcclusion { get; set; } = true;
        public bool EnableDepthPrePass { get; set; } = true;
        public bool EnableTransparentPass { get; set; } = true;
        public bool EnableMeshletDebugView { get; set; }
        
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
            _ownsDependencies = ownsDependencies;
        }
        
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            Console.WriteLine("Initializing VulkanRenderer...");
            
            // Create pipelines
            CreatePipelines();

            _hizDepthPyramid = new HiZDepthPyramid(_context, CreateHiZExtent(_swapchain.Extent));
            
            // Initialize render graph with passes
            InitializeRenderGraph();
            
            // Register static buffers in bindless heap
            RegisterSceneBuffers();
            _sync.EnsureRenderFinishedSemaphoreCapacity(_swapchain.ImageCount);
            
            _isInitialized = true;
            Console.WriteLine("VulkanRenderer initialized.");
        }
        
        private void CreatePipelines()
        {
            Console.WriteLine("Creating pipelines...");
            
            // Create mesh pipeline for depth prepass and forward pass
            _meshPipeline = new MeshPipeline(
                _context,
                _bindlessHeap,
                _swapchain.SurfaceFormat,
                _swapchain.DepthFormat);
            
            // Create compute pipeline for light culling
            _computePipeline = new ComputePipeline(_context, _bindlessHeap);
            
            Console.WriteLine("Pipelines created.");
        }
        
        private void InitializeRenderGraph()
        {
            Console.WriteLine("Initializing render graph...");
            
            // Create depth pre-pass
            var depthPrePass = new DepthPrePass(
                _context, _swapchain, _bindlessHeap, _meshPipeline);
            _renderGraph.AddPass(depthPrePass);

            var hizBuildPass = new HiZBuildPass(
                _context, _swapchain, _bindlessHeap, _hizDepthPyramid!);
            _renderGraph.AddPass(hizBuildPass);
            
            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager);
            _renderGraph.AddPass(lightCullingPass);
            
            // Create forward+ rendering pass
            var forwardPass = new ForwardPlusPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline);
            _renderGraph.AddPass(forwardPass);

            var transparentForwardPass = new TransparentForwardPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline);
            _renderGraph.AddPass(transparentForwardPass);
            
            _renderGraph.Initialize();
            Console.WriteLine("Render graph initialized.");
        }
        
        private void RegisterSceneBuffers()
        {
            Console.WriteLine("Registering scene buffers in bindless heap...");
            
            // Register mesh manager buffers
            _meshManager.RegisterBuffers(_bindlessHeap);
            _materialManager.RegisterBuffers(_bindlessHeap);

            // Register default material textures at fixed shader-visible indices.
            _textureManager.InitializeDefaultTextures(_bindlessHeap);
            
            // Register light manager buffer (index 12)
            _lightManager.RegisterBuffer(_bindlessHeap, BindlessIndex.LightBuffer);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.DepthTexture,
                _swapchain.DepthImageView,
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);

            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            
            // Register scene data buffers
            _sceneDataBuilder.RegisterBuffers(_bindlessHeap);
            
            Console.WriteLine("Scene buffers registered.");
        }
        
        public bool BeginFrame()
        {
            if (!_isInitialized)
                Initialize();

            if (_frameInProgress)
                throw new InvalidOperationException("BeginFrame was called while a frame is already in progress.");
            
            // Wait for previous frame to complete
            _sync.WaitForFence(_currentFrame);
            
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
            
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.ImageViews[_imageIndex],
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
            
            _lightManager.UploadToGPU(_stagingRing, _currentCommandBuffer);
            ulong lightUploadBytes = _lightManager.LastUploadBytes;
            int lightCount = _lightManager.LightCount;
            int directionalLightCount = _lightManager.DirectionalLightCount;
            int localLightCount = _lightManager.LocalLightCount;

            // Build and upload scene data using SceneDataBuilder
            var sceneData = _sceneDataBuilder.Build(
                scene,
                camera,
                _swapchain.Extent.Width,
                _swapchain.Extent.Height,
                _currentCommandBuffer,
                useTiledLightCulling: localLightCount > 0);
            sceneData.FrameIndex = _currentFrame;
            sceneData.ImageIndex = _imageIndex;
            sceneData.LightCount = lightCount;
            sceneData.DirectionalLightCount = directionalLightCount;
            sceneData.LocalLightCount = localLightCount;
            sceneData.LightUploadBytes = lightUploadBytes;
            sceneData.UploadedBytes += lightUploadBytes;
            sceneData.DepthPrePassEnabled = EnableDepthPrePass;
            sceneData.HiZBuildEnabled = EnableDepthPrePass && EnableHiZOcclusion;
            sceneData.OcclusionCullingEnabled = EnableDepthPrePass && EnableHiZOcclusion;
            sceneData.TransparentPassEnabled = EnableTransparentPass;
            sceneData.HiZMipCount = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.MipLevels ?? 0u : 0u;
            sceneData.HiZWidth = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Width ?? 0u : 0u;
            sceneData.HiZHeight = sceneData.HiZBuildEnabled ? _hizDepthPyramid?.Extent.Height ?? 0u : 0u;
            sceneData.DebugViewMode = EnableMeshletDebugView ? 1u : 0u;
            
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
            sceneData.CpuTotalDrawSceneMicroseconds = ElapsedMicroseconds(drawSceneStart);
            _lastDiagnostics = BuildDiagnostics(sceneData);
        }

        private RendererDiagnostics BuildDiagnostics(SceneRenderingData sceneData)
        {
            ModelRenderUploadDiagnostics uploadDiagnostics = _modelUploadService.LastUploadDiagnostics;
            return new RendererDiagnostics(
                sceneData.ObjectCount,
                sceneData.MeshletCount,
                sceneData.OpaqueObjectCount,
                sceneData.MaskedObjectCount,
                sceneData.TransparentObjectCount,
                sceneData.OpaqueMeshletCount,
                sceneData.TransparentMeshletCount,
                sceneData.OpaqueMeshletCount,
                sceneData.ForwardFrustumCulledMeshletsGpu,
                sceneData.ForwardOcclusionCulledMeshletsGpu,
                sceneData.OpaqueMeshletCount,
                sceneData.OpaqueMeshletCount,
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
                sceneData.CpuDepthPrePassRecordMicroseconds,
                sceneData.CpuHiZBuildRecordMicroseconds,
                sceneData.CpuLightCullRecordMicroseconds,
                sceneData.CpuForwardOpaqueRecordMicroseconds,
                sceneData.CpuTransparentRecordMicroseconds,
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
                sceneData.HiZHeight);
        }
        
        public void Resize(int width, int height)
        {
            // Wait for device to be idle
            var vk = _context.Api;
            vk.DeviceWaitIdle(_context.Device);
            
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
                imageLayout: ImageLayout.DepthStencilReadOnlyOptimal);
            _hizDepthPyramid?.Recreate(CreateHiZExtent(_swapchain.Extent));
            _bindlessHeap.RegisterTexture(
                BindlessIndex.HiZDepthTexture,
                _hizDepthPyramid!.FullView,
                imageLayout: ImageLayout.ShaderReadOnlyOptimal);
            _meshPipeline?.Recreate(_swapchain.SurfaceFormat, _swapchain.DepthFormat);
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
                _hizDepthPyramid?.Dispose();
                
                _meshPipeline?.Dispose();
                _computePipeline?.Dispose();

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
            
            Console.WriteLine("VulkanRenderer disposed.");
        }
        
        ~VulkanRenderer()
        {
            Dispose(false);
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
