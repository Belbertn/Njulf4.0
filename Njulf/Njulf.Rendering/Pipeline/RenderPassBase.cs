using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Abstract base class for render passes.
    /// Each render pass can execute commands and has dependencies.
    /// </summary>
    public abstract class RenderPassBase : IDisposable
    {
        protected readonly VulkanContext _context;
        protected readonly SwapchainManager _swapchain;
        protected readonly BindlessHeap _bindlessHeap;
        
        private bool _disposed;
        
        public string Name { get; }
        
        public RenderPassBase(
            string name,
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _swapchain = swapchain ?? throw new ArgumentNullException(nameof(swapchain));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        }
        
        /// <summary>
        /// Initializes the render pass (pipeline creation, etc.).
        /// </summary>
        public abstract void Initialize();
        
        /// <summary>
        /// Executes the render pass commands.
        /// </summary>
        /// <param name="cmd">The command buffer to record into</param>
        /// <param name="frameIndex">Current frame index (0 or 1)</param>
        /// <param name="sceneData">Scene data for this frame</param>
        public abstract void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData);
        
        /// <summary>
        /// Gets the barriers needed before this pass executes.
        /// </summary>
        public virtual IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
        
        /// <summary>
        /// Called when the swapchain is recreated.
        /// </summary>
        public virtual void OnSwapchainRecreated()
        {
            // Recreate any swapchain-dependent resources
        }
        
        /// <summary>
        /// Cleans up the render pass.
        /// </summary>
        public virtual void Cleanup()
        {
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
            
            Cleanup();
            Console.WriteLine($"Render pass '{Name}' disposed.");
        }
        
        ~RenderPassBase()
        {
            Dispose(false);
        }
    }
    
    /// <summary>
    /// Scene rendering data passed to render passes.
    /// Contains all the data needed to render a frame.
    /// </summary>
    public class SceneRenderingData
    {
        public int ObjectCount { get; set; }
        public int MeshletCount { get; set; }
        public int LightCount { get; set; }
        public uint CurrentFrameIndex { get; set; }
        public float Time { get; set; }
        
        // Buffer offsets and sizes
        public ulong ObjectDataOffset { get; set; }
        public ulong ObjectDataSize { get; set; }
        public ulong MeshletDrawOffset { get; set; }
        public ulong MeshletDrawSize { get; set; }
        
        // Scene matrices
        public Matrix4x4 ViewMatrix { get; set; }
        public Matrix4x4 ProjectionMatrix { get; set; }
        public Matrix4x4 ViewProjectionMatrix { get; set; }
        
        // Screen info
        public uint ScreenWidth { get; set; }
        public uint ScreenHeight { get; set; }
    }
}
