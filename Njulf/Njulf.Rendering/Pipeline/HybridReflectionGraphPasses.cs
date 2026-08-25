using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal abstract class HybridReflectionGraphPass : RenderPassBase
{
    protected readonly HybridReflectionVulkanRuntime Runtime;
    private readonly bool _ownsTargetNotification;

    protected HybridReflectionGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime,
        bool ownsTargetNotification = false)
        : base(name, context, swapchain, bindlessHeap)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _ownsTargetNotification = ownsTargetNotification;
    }

    public override RenderGraphQueueIntent QueueIntent =>
        RenderGraphQueueIntent.Compute;

    // The runtime uses explicit image transitions and a shared descriptor bank.
    // Keep the chain on the graphics command stream until it receives a
    // dedicated queue-family ownership audit.
    public override bool SupportsAsyncCompute => false;

    public override string AsyncComputeReason =>
        "Hybrid reflection shares SceneColor and history descriptors with adjacent graphics passes.";

    public override void Initialize() => Runtime.Initialize();

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData) => Runtime.PrepareFrame(sceneData);

    public override void OnSwapchainRecreated()
    {
        if (_ownsTargetNotification)
            Runtime.OnTargetsRecreated();
    }
}

internal sealed class HybridReflectionSsrPass : HybridReflectionGraphPass
{
    public HybridReflectionSsrPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionSsrPass", context, swapchain, bindlessHeap,
            runtime, ownsTargetNotification: true)
    {
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordSsr(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionRayQueryPass : HybridReflectionGraphPass
{
    public HybridReflectionRayQueryPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionRayQueryPass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData) => Runtime.ShouldTraceRays(sceneData);

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordRayQuery(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionResolvePass : HybridReflectionGraphPass
{
    public HybridReflectionResolvePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionResolvePass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordResolve(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionDdgiBasePass : HybridReflectionGraphPass
{
    public HybridReflectionDdgiBasePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionDdgiBasePass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData) => Runtime.ShouldEvaluateDdgiBase(sceneData);

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordDdgiBase(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionTemporalPass : HybridReflectionGraphPass
{
    public HybridReflectionTemporalPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionTemporalPass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordTemporal(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionSpatialPass : HybridReflectionGraphPass
{
    public HybridReflectionSpatialPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionSpatialPass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordSpatial(commandBuffer, frameIndex, sceneData);
}

internal sealed class HybridReflectionCompositePass : HybridReflectionGraphPass
{
    public HybridReflectionCompositePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        HybridReflectionVulkanRuntime runtime)
        : base("HybridReflectionCompositePass", context, swapchain,
            bindlessHeap, runtime)
    {
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Runtime.RecordComposite(commandBuffer, frameIndex, sceneData);
}
