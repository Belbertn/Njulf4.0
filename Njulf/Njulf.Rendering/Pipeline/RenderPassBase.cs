using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Njulf.Rendering.Data;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;

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
        internal VulkanContext Context => _context;
        public virtual bool SupportsSecondaryCommandBuffer => false;
        
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
        public abstract void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData);
        
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
            System.Diagnostics.Debug.WriteLine($"Render pass '{Name}' disposed.");
        }
    }
}
