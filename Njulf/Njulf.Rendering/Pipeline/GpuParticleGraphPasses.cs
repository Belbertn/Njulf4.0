using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Render-graph adapters for the established GPU particle executors. Keeping pipeline creation
/// in the executor classes avoids a second set of compute pipelines while giving the scheduler
/// one authoritative pass order, timestamp scope, and queue-ownership contract.
/// </summary>
internal abstract class GpuParticleGraphPassBase : RenderPassBase
{
    protected GpuParticleGraphPassBase(
        string name,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap)
        : base(name, context, swapchain, bindlessHeap)
    {
    }

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => true;
    public override string AsyncComputeReason =>
        "GPU particle compute work is graph-recorded with concrete per-frame buffer handoffs.";

    // The executor owns its eagerly-created compute pipeline. There is no graph-local pipeline
    // object to initialize or dispose.
    public override void Initialize()
    {
    }
}

internal sealed class GpuParticleResetGraphPass : GpuParticleGraphPassBase
{
    private readonly GpuParticleResetPass _executor;

    public GpuParticleResetGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GpuParticleResetPass executor)
        : base("GpuParticleResetPass", context, swapchain, bindlessHeap)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        sceneData.GpuParticlesEnabled != 0 &&
        sceneData.GpuParticleResetRequired != 0 &&
        sceneData.GpuParticleCapacity > 0 &&
        sceneData.GpuParticleDrawCapacity > 0;

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData) =>
        _executor.Execute(cmd, frameIndex, sceneData);
}

internal sealed class GpuParticleSimulateGraphPass : GpuParticleGraphPassBase
{
    private readonly GpuParticleSimulatePass _executor;

    public GpuParticleSimulateGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GpuParticleSimulatePass executor)
        : base("GpuParticleSimulatePass", context, swapchain, bindlessHeap)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        sceneData.GpuParticlesEnabled != 0 &&
        sceneData.GpuParticleEmitterCount > 0 &&
        sceneData.GpuParticleCapacity > 0 &&
        sceneData.GpuParticleMaxSpawnPerEmitter > 0;

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData) =>
        _executor.Execute(cmd, frameIndex, sceneData);
}

internal sealed class GpuParticleSortGraphPass : GpuParticleGraphPassBase
{
    private readonly GpuParticleSortPass _executor;
    private readonly GpuParticleRuntimeManager _runtimeManager;

    public GpuParticleSortGraphPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        GpuParticleSortPass executor,
        GpuParticleRuntimeManager runtimeManager)
        : base("GpuParticleSortPass", context, swapchain, bindlessHeap)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        sceneData.GpuParticlesEnabled != 0 &&
        sceneData.GpuParticleEmitterCount > 0 &&
        sceneData.GpuParticleCapacity > 0 &&
        sceneData.GpuParticleIndirectDrawBuffer.IsValid;

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        _executor.Execute(cmd, frameIndex, sceneData);
        _runtimeManager.RecordCounterReadback(cmd, frameIndex, sceneData);
    }
}
