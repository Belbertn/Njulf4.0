using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Thin graph adapters for C3. The coordinator owns work preparation and
/// publication transactions; graph declarations own resource hazards and pass
/// placement relative to schedule/trace.
/// </summary>
internal abstract class SimpleDdgiGuidingGraphPass : RenderPassBase
{
    protected SimpleDdgiGuidingGraphPass(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiGuidingFrameCoordinator coordinator)
        : base(name, context, swapchain, bindlessHeap) =>
        Coordinator = coordinator;

    protected SimpleDdgiGuidingFrameCoordinator Coordinator { get; }

    public override RenderGraphQueueIntent QueueIntent =>
        RenderGraphQueueIntent.Compute;

    // Sample, trace, training, hierarchy publication, and validation are one
    // indivisible Simple-DDGI transaction. The render graph declares every
    // persistent distribution and transient workspace range, so the scheduler
    // may migrate the complete group but can never split an individual phase.
    public override bool SupportsAsyncCompute =>
        AsyncComputePassCatalog.IsProductionActivationAuthorized(
            AsyncComputePath.SimpleDdgiUpdate);

    public override string AsyncComputeReason =>
        "Directional guiding is part of the atomic Simple-DDGI update segment with concrete distribution, workspace, and direction-sidecar ranges.";

    public override void Initialize()
    {
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }
}

internal sealed class SimpleDdgiGuidingSampleGraphPass :
    SimpleDdgiGuidingGraphPass
{
    public SimpleDdgiGuidingSampleGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiGuidingFrameCoordinator coordinator)
        : base(SimpleDdgiGuidingGpuPassNames.Sample, context, swapchain,
            bindlessHeap, coordinator)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Coordinator.CanExecuteSample(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Coordinator.TryRecordSample(cmd, frameIndex, out _);
}

internal sealed class SimpleDdgiGuidingTrainGraphPass :
    SimpleDdgiGuidingGraphPass
{
    public SimpleDdgiGuidingTrainGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiGuidingFrameCoordinator coordinator)
        : base(SimpleDdgiGuidingGpuPassNames.Train, context, swapchain,
            bindlessHeap, coordinator)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Coordinator.CanExecuteTrain(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Coordinator.TryRecordTrain(cmd, frameIndex, out _);
}

internal sealed class SimpleDdgiGuidingBuildGraphPass :
    SimpleDdgiGuidingGraphPass
{
    public SimpleDdgiGuidingBuildGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiGuidingFrameCoordinator coordinator)
        : base(SimpleDdgiGuidingGpuPassNames.Build, context, swapchain,
            bindlessHeap, coordinator)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Coordinator.CanExecuteHierarchyBuild(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Coordinator.TryRecordHierarchyBuild(cmd, frameIndex, out _);
}

internal sealed class SimpleDdgiGuidingValidateGraphPass :
    SimpleDdgiGuidingGraphPass
{
    public SimpleDdgiGuidingValidateGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiGuidingFrameCoordinator coordinator)
        : base(SimpleDdgiGuidingGpuPassNames.Validate, context, swapchain,
            bindlessHeap, coordinator)
    {
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        Coordinator.CanExecuteValidate(frameIndex);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        Coordinator.TryRecordValidate(cmd, frameIndex, out _);
}
