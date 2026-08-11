using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Thin graph adapters for the transactional C4 runtime. Resource hazards are
/// declared by <see cref="ProductionRenderPipelineDeclaration"/>; the runtime
/// remains the sole owner of stage tokens, cache generations, and publication.
/// </summary>
internal abstract class GiCausticGraphPass : RenderPassBase
{
    protected GiCausticGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(name, context, swapchain, bindlessHeap) =>
        Runtime = runtime;

    protected GiCausticVulkanRuntime Runtime { get; }

    public override RenderGraphQueueIntent QueueIntent =>
        RenderGraphQueueIntent.Compute;

    // C4 currently stays on the graphics queue. Its task/trace chain consumes
    // the current TLAS and its resolve consumes current forward attachments;
    // moving either half to async requires an explicit ownership-transfer and
    // timeline publication policy rather than an optimistic capability bit.
    public override bool SupportsAsyncCompute => false;

    public override void Initialize()
    {
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }
}

internal abstract class GiCausticBuildGraphPass : GiCausticGraphPass
{
    protected GiCausticBuildGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(name, context, swapchain, bindlessHeap, runtime)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Runtime.CanExecuteBuildFrame(frameIndex);
}

internal sealed class GiCausticTaskGraphPass : GiCausticBuildGraphPass
{
    public GiCausticTaskGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(GiCausticGpuPassNames.Task, context, swapchain, bindlessHeap,
            runtime)
    {
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.TryRecordTaskStage(cmd, frameIndex, out _);
}

internal sealed class GiCausticTraceGraphPass : GiCausticBuildGraphPass
{
    public GiCausticTraceGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(GiCausticGpuPassNames.Trace, context, swapchain, bindlessHeap,
            runtime)
    {
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.TryRecordTraceStage(cmd, frameIndex, out _);
}

internal sealed class GiCausticCacheBuildGraphPass : GiCausticBuildGraphPass
{
    public GiCausticCacheBuildGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(GiCausticGpuPassNames.CacheBuild, context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.TryRecordCacheBuildStage(cmd, frameIndex, out _);
}

internal sealed class GiCausticResolveGraphPass : GiCausticGraphPass
{
    public GiCausticResolveGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(GiCausticGpuPassNames.Resolve, context, swapchain, bindlessHeap,
            runtime)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Runtime.CanExecuteScreenFrame(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.TryRecordPreparedScreenResolve(
            cmd, frameIndex, sceneData, out _);
}

internal sealed class GiCausticCompositeGraphPass : GiCausticGraphPass
{
    public GiCausticCompositeGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GiCausticVulkanRuntime runtime)
        : base(GiCausticGpuPassNames.Composite, context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Runtime.CanExecuteScreenFrame(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.TryRecordPreparedScreenComposite(
            cmd, frameIndex, sceneData, out _);
}
