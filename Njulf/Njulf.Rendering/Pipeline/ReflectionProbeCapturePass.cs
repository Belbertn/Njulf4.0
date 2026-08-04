using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// The scene-rendering seam used by local reflection capture. Implementations must record actual
/// linear-HDR opaque/alpha-masked scene draws into the supplied face view; returning false is a
/// recoverable work failure and never advances scheduler progress.
/// </summary>
public interface IReflectionProbeCaptureSceneRenderer
{
    bool RecordCaptureFace(
        CommandBuffer commandBuffer,
        SceneRenderingData sceneData,
        in ReflectionCaptureViewContext view,
        ImageView colorView,
        ImageView depthView);
}

/// <summary>Adapter over the prepared main forward scene path.</summary>
public sealed class ForwardPlusReflectionProbeCaptureSceneRenderer : IReflectionProbeCaptureSceneRenderer
{
    private readonly ForwardPlusPass _forwardPass;

    public ForwardPlusReflectionProbeCaptureSceneRenderer(ForwardPlusPass forwardPass)
    {
        _forwardPass = forwardPass ?? throw new ArgumentNullException(nameof(forwardPass));
    }

    public bool RecordCaptureFace(
        CommandBuffer commandBuffer,
        SceneRenderingData sceneData,
        in ReflectionCaptureViewContext view,
        ImageView colorView,
        ImageView depthView)
    {
        _forwardPass.RecordReflectionCapture(
            commandBuffer,
            sceneData,
            view,
            colorView,
            depthView);
        return true;
    }
}

/// <summary>
/// Records a bounded number of cube faces. Face completion is separate from copy completion:
/// this pass can only make mip-0 scratch data shader-readable, never publish a layer.
/// </summary>
public sealed class ReflectionProbeCapturePass : RenderPassBase
{
    private readonly ReflectionProbeManager _manager;
    private readonly ReflectionSettings _settings;
    private readonly IReflectionProbeCaptureSceneRenderer _sceneRenderer;

    public ReflectionProbeCapturePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        ReflectionProbeManager manager,
        ReflectionSettings settings,
        IReflectionProbeCaptureSceneRenderer sceneRenderer)
        : base("ReflectionProbeCapturePass", context, swapchain, bindlessHeap)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sceneRenderer = sceneRenderer ?? throw new ArgumentNullException(nameof(sceneRenderer));
    }

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Graphics;
    public override bool SupportsAsyncCompute => false;

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        _settings.Enabled &&
        _settings.MaxProbeCapturesPerFrame > 0 &&
        _settings.MaxProbeCaptureFacesPerFrame > 0 &&
        _manager.HasCaptureWork(ReflectionProbeWorkKind.CaptureFace);

    public override void Initialize()
    {
    }

    public override void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
    {
        int faceLimit = Math.Clamp(_settings.MaxProbeCaptureFacesPerFrame, 0, 6);
        for (int unit = 0; unit < faceLimit; unit++)
        {
            if (!_manager.TryAcquireCaptureFace(out ReflectionProbeWork work))
                break;

            try
            {
                ReflectionCaptureViewContext view = _manager.CreateCaptureViewContext(
                    work,
                    _settings.CaptureIncludesDdgi);
                _manager.PrepareCaptureFace(commandBuffer, work);
                bool recorded = _sceneRenderer.RecordCaptureFace(
                    commandBuffer,
                    sceneData,
                    view,
                    _manager.GetScratchFaceView(work.Face),
                    _manager.CaptureDepthView);
                if (!recorded)
                {
                    _manager.FailCaptureWork(work, retry: true);
                    break;
                }

                _manager.CompleteCaptureFaceRecording(commandBuffer, work);
                _manager.CompleteCaptureWork(work);
            }
            catch
            {
                _manager.FailCaptureWork(work, retry: true);
                throw;
            }
        }
    }
}
