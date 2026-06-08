using System;
using System.Collections.Generic;
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
    /// </summary>
    public unsafe class VulkanRenderer : IRenderer, IDisposable
    {
        private const int FramesInFlight = 2;
        
        private readonly IWindow _window;
        private readonly VulkanContext _context;
        private readonly SwapchainManager _swapchain;
        private readonly SynchronizationManager _sync;
        private readonly CommandBufferManager _cmd;
        private readonly BufferManager _bufferManager;
        private readonly TextureManager _textureManager;
        private readonly MeshManager _meshManager;
        private readonly LightManager _lightManager;
        private readonly BindlessHeap _bindlessHeap;
        private readonly RenderGraph _renderGraph;
        private readonly SceneDataBuilder _sceneDataBuilder;
        private readonly StagingRing _stagingRing;
        private readonly FenceBasedDeleter _deleter;
        
        // Pipelines
        private MeshPipeline _meshPipeline;
        private ComputePipeline _computePipeline;
        
        // State
        private int _currentFrame = 0;
        private uint _imageIndex;
        private CommandBuffer _currentCommandBuffer;
        private bool _isInitialized = false;
        private bool _disposed = false;
        
        // Scene state
        private Color _clearColor = Color.CornflowerBlue;
        
        public VulkanRenderer(
            IWindow window,
            VulkanContext context,
            SwapchainManager swapchainManager,
            SynchronizationManager syncManager,
            CommandBufferManager cmdManager,
            BufferManager bufferManager,
            TextureManager textureManager,
            MeshManager meshManager,
            LightManager lightManager,
            BindlessHeap bindlessHeap,
            RenderGraph renderGraph,
            SceneDataBuilder sceneDataBuilder,
            StagingRing stagingRing,
            FenceBasedDeleter deleter)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _swapchain = swapchainManager ?? throw new ArgumentNullException(nameof(swapchainManager));
            _sync = syncManager ?? throw new ArgumentNullException(nameof(syncManager));
            _cmd = cmdManager ?? throw new ArgumentNullException(nameof(cmdManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _renderGraph = renderGraph ?? throw new ArgumentNullException(nameof(renderGraph));
            _sceneDataBuilder = sceneDataBuilder ?? throw new ArgumentNullException(nameof(sceneDataBuilder));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
        }
        
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            Console.WriteLine("Initializing VulkanRenderer...");
            
            // Create pipelines
            CreatePipelines();
            
            // Initialize render graph with passes
            InitializeRenderGraph();
            
            // Register static buffers in bindless heap
            RegisterSceneBuffers();
            
            _isInitialized = true;
            Console.WriteLine("VulkanRenderer initialized.");
        }
        
        private void CreatePipelines()
        {
            Console.WriteLine("Creating pipelines...");
            
            // Create mesh pipeline for depth prepass and forward pass
            _meshPipeline = new MeshPipeline(_context, _bindlessHeap);
            
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
            
            // Create tiled light culling pass
            var lightCullingPass = new TiledLightCullingPass(
                _context, _swapchain, _bindlessHeap, _computePipeline, _bufferManager);
            _renderGraph.AddPass(lightCullingPass);
            
            // Create forward+ rendering pass
            var forwardPass = new ForwardPlusPass(
                _context, _swapchain, _bindlessHeap, _meshPipeline);
            _renderGraph.AddPass(forwardPass);
            
            _renderGraph.Initialize();
            Console.WriteLine("Render graph initialized.");
        }
        
        private void RegisterSceneBuffers()
        {
            Console.WriteLine("Registering scene buffers in bindless heap...");
            
            // Register mesh manager buffers
            _meshManager.RegisterBuffers(_bindlessHeap);
            
            // Register light manager buffer (index 12)
            _lightManager.RegisterBuffer(_bindlessHeap, BindlessIndex.LightBuffer);
            
            // Register scene data buffers
            // The SceneDataBuilder has per-frame buffers that need to be registered
            // For now, we'll register them when we upload data
            
            Console.WriteLine("Scene buffers registered.");
        }
        
        public void BeginFrame()
        {
            if (!_isInitialized)
                Initialize();
            
            // Wait for previous frame to complete
            _sync.WaitForFence(_currentFrame);
            
            // Process completed frame deletions
            _deleter.ProcessCompletedFrame(_sync.GetInFlightFence(_currentFrame));
            
            // Acquire next swapchain image
            _imageIndex = _swapchain.AcquireNextImage(
                _sync.GetImageAvailableSemaphore(_currentFrame));
            
            // Reset fence for current frame
            _sync.ResetFence(_currentFrame);
            
            // Advance staging ring
            _stagingRing.AdvanceFrame();
            
            // Begin recording primary command buffer
            _currentCommandBuffer = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);
        }
        
        public void EndFrame()
        {
            var vk = _context.Api;
            
            // Transition swapchain image to transfer for present
            TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.TransferSrcOptimal);
            
            // End command buffer recording
            Result result = vk.EndCommandBuffer(_currentCommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end command buffer", result);
            
            // Submit command buffer
            var waitSemaphores = stackalloc Semaphore[] { _sync.GetImageAvailableSemaphore(_currentFrame) };
            var waitStages = stackalloc PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
            var signalSemaphores = stackalloc Semaphore[] { _sync.GetRenderFinishedSemaphore(_currentFrame) };
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
            
            _swapchain.Present(&presentInfo);
            
            // Advance to next frame
            _currentFrame = (_currentFrame + 1) % FramesInFlight;
        }
        
        public unsafe void Clear(Color color)
        {
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
                ClearValue = new ClearValue(new ClearColorValue(0.0f, 0)) // Reverse-Z: clear to 0
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
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            
            // Build and upload scene data using SceneDataBuilder
            var sceneData = _sceneDataBuilder.Build(
                scene,
                camera,
                _swapchain.Extent.Width,
                _swapchain.Extent.Height);
            
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
            
            // Transition swapchain image to color attachment for rendering
            TransitionSwapchainImage(_currentCommandBuffer, ImageLayout.ColorAttachmentOptimal);
            
            // Execute render graph
            _renderGraph.Execute(_currentCommandBuffer, _currentFrame, sceneData);
        }
        
        public void Resize(int width, int height)
        {
            // Wait for device to be idle
            var vk = _context.Api;
            vk.DeviceWaitIdle(_context.Device);
            
            // Recreate swapchain
            _swapchain.RecreateSwapchain();
            
            // Notify render graph
            _renderGraph.OnSwapchainRecreated();
            
            // Update camera aspect ratio if camera is provided
            // (Camera aspect ratio should be updated by the caller)
        }
        
        private void TransitionSwapchainImage(CommandBuffer cmd, ImageLayout newLayout)
        {
            var vk = _context.Api;
            
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags2.ColorAttachmentWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchain.Images[_imageIndex],
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = Vk.RemainingMipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = Vk.RemainingArrayLayers
                }
            };
            
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };
            
            vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
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
                
                _meshPipeline?.Dispose();
                _computePipeline?.Dispose();
                
                _deleter.Cleanup();
                _stagingRing.Dispose();
                _swapchain.Dispose();
                _cmd.Dispose();
                _sync.Dispose();
                _bindlessHeap.Dispose();
                _lightManager.Dispose();
                _meshManager.Dispose();
                _textureManager.Dispose();
                _bufferManager.Dispose();
                _context.Dispose();
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
