using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Supplies the renderer-owned completion value associated with the terminal graphics submission.
/// A reflection feature never waits or invents a signaled value; the provider must map this frame
/// to an already-owned fence/timeline sequence.
/// </summary>
public interface IReflectionProbeCompletionValueProvider
{
    ulong GetCompletionValue(int frameIndex);
}

/// <summary>
/// Copies one complete private cube chain into its stable layer. The scheduler enters
/// AwaitingGpuCompletion only after the copy has been recorded and the renderer-owned completion
/// value has been attached; logical publication happens on a later poll.
/// </summary>
public sealed class ReflectionProbePublishPass : RenderPassBase
{
    private readonly ReflectionProbeManager _manager;
    private readonly ReflectionSettings _settings;
    private readonly IReflectionProbeCompletionValueProvider _completionValues;

    public ReflectionProbePublishPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        ReflectionProbeManager manager,
        ReflectionSettings settings,
        IReflectionProbeCompletionValueProvider completionValues)
        : base("ReflectionProbePublishPass", context, swapchain, bindlessHeap)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completionValues = completionValues ?? throw new ArgumentNullException(nameof(completionValues));
    }

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Graphics;
    public override bool SupportsAsyncCompute => false;

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        _settings.Enabled &&
        _settings.MaxProbeCapturesPerFrame > 0 &&
        _manager.HasCaptureWork(ReflectionProbeWorkKind.PublishCopy);

    public override void Initialize()
    {
    }

    public override void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
    {
        if (!_manager.TryAcquirePublishCopy(out ReflectionProbeWork work))
            return;

        try
        {
            _manager.RecordPublishCopy(commandBuffer, work);
            ulong completionValue = _completionValues.GetCompletionValue(frameIndex);
            if (completionValue == 0UL)
                throw new InvalidOperationException(
                    "The reflection publish pass did not receive a renderer-owned completion value.");
            _manager.SubmitCaptureCopy(work, completionValue);
        }
        catch
        {
            _manager.FailCaptureWork(work, retry: true);
            throw;
        }
    }
}
