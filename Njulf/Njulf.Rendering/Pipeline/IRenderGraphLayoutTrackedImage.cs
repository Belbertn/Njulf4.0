using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// An image whose layout is owned outside the render graph but can be transitioned by it.
    /// This lets imported resources participate in graph barriers without transferring their
    /// allocation lifetime to the graph.
    /// </summary>
    public interface IRenderGraphLayoutTrackedImage
    {
        ImageLayout Layout { get; }

        void TransitionToLayout(
            CommandBuffer cmd,
            ImageLayout newLayout,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            PipelineStageFlags2? srcStage = null,
            AccessFlags2? srcAccess = null,
            bool force = false);
    }
}
