using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline;

internal sealed unsafe class VariableRateShadingPass : GtaoComputePassBase
{
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;
    private readonly Func<bool>? _incompatiblePerPixelOutput;

    public VariableRateShadingPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        Func<bool>? incompatiblePerPixelOutput = null,
        GiPipelineCacheService? pipelineCacheService = null)
        : base(
            "VariableRateShadingPass",
            "variable_rate_shading.comp.spv",
            context,
            swapchain,
            bindlessHeap,
            [
                Binding(0, DescriptorType.CombinedImageSampler),
                Binding(1, DescriptorType.CombinedImageSampler),
                Binding(2, DescriptorType.StorageImage)
            ],
            setCount: 1,
            (uint)Marshal.SizeOf<GPUVariableRateShadingPushConstants>(),
            pipelineCacheService)
    {
        _renderTargets = renderTargets ??
            throw new ArgumentNullException(nameof(renderTargets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _incompatiblePerPixelOutput = incompatiblePerPixelOutput;
    }

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        bool requested = _settings.Raster.VariableRateShadingMode ==
            VariableRateShadingMode.Auto;
        bool targetMatchesScene =
            _renderTargets.MotionVectors.Extent.Width == sceneData.ScreenWidth &&
            _renderTargets.MotionVectors.Extent.Height == sceneData.ScreenHeight;
        VariableRateShadingDecision decision =
            VariableRateShadingPolicy.Evaluate(
                _settings.Raster.VariableRateShadingMode,
                _context.FragmentShadingRateSupported,
                sceneData.ActiveFeatureIsolation ==
                    RenderFeatureIsolationMode.FullFrame,
                sceneData.DebugToolingEnabled ||
                sceneData.DebugViewMode != 0 ||
                sceneData.NearFieldResidualDebugView != 0 ||
                _settings.ShowRawHdrSceneColor,
                _incompatiblePerPixelOutput?.Invoke() == true,
                sceneData.MotionVectorsEnabled != 0 && targetMatchesScene,
                sceneData.MaskedMeshletCount,
                sceneData.FoliageClusterCount,
                sceneData.LocalLightCount);

        sceneData.VariableRateShadingSupported =
            _context.FragmentShadingRateSupported ? 1 : 0;
        sceneData.VariableRateShadingRequested = requested ? 1 : 0;
        sceneData.VariableRateShadingActive = decision.IsEnabled ? 1 : 0;
        Extent2D attachmentTexel =
            _context.FragmentShadingRateAttachmentTexelSize;
        sceneData.VariableRateShadingAttachmentTexelWidth =
            decision.IsEnabled ? attachmentTexel.Width : 0;
        sceneData.VariableRateShadingAttachmentTexelHeight =
            decision.IsEnabled ? attachmentTexel.Height : 0;
        sceneData.VariableRateShadingFallbackReason =
            decision.FallbackReason;
        return decision.IsEnabled;
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        long start = Stopwatch.GetTimestamp();
        Extent2D attachmentTexel =
            _context.FragmentShadingRateAttachmentTexelSize;
        Extent2D expectedExtent =
            RenderTargetManager.CalculateVariableRateShadingExtent(
                _renderTargets.SceneDepth.Extent,
                attachmentTexel);
        if (_renderTargets.VariableRateShading.Extent.Width !=
                expectedExtent.Width ||
            _renderTargets.VariableRateShading.Extent.Height !=
                expectedExtent.Height)
        {
            throw new InvalidOperationException(
                "The fragment shading-rate target does not match the current scene and device texel extent.");
        }

        _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
        _renderTargets.MotionVectors.TransitionToShaderRead(cmd);
        _renderTargets.VariableRateShading.TransitionToStorageWrite(cmd);

        var push = new GPUVariableRateShadingPushConstants
        {
            InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
            SourceDimensions = new Vector2(
                sceneData.ScreenWidth,
                sceneData.ScreenHeight),
            RateImageDimensions = new Vector2(
                expectedExtent.Width,
                expectedExtent.Height),
            AttachmentTexelWidth = attachmentTexel.Width,
            AttachmentTexelHeight = attachmentTexel.Height,
            ForegroundDistanceMeters =
                VariableRateShadingPolicy.ForegroundDistanceMeters,
            MotionThresholdPixels =
                VariableRateShadingPolicy.MotionThresholdPixels,
            AbsoluteDepthThresholdMeters =
                VariableRateShadingPolicy.AbsoluteDepthThresholdMeters,
            RelativeDepthThreshold =
                VariableRateShadingPolicy.RelativeDepthThreshold,
            NormalDotThreshold =
                VariableRateShadingPolicy.NormalDotThreshold,
            FineRateEncoding =
                VariableRateShadingPolicy.FineRateEncoding,
            CoarseRateEncoding =
                VariableRateShadingPolicy.Coarse2X2RateEncoding
        };
        BindAndPush(cmd, 0, push);
        _context.Api.CmdDispatch(
            cmd,
            (expectedExtent.Width + 7u) / 8u,
            (expectedExtent.Height + 7u) / 8u,
            1);
        _renderTargets.VariableRateShading
            .TransitionToFragmentShadingRateAttachment(cmd);

        sceneData.CpuVariableRateShadingRecordMicroseconds =
            Stopwatch.GetElapsedTime(start).Ticks /
            (TimeSpan.TicksPerMillisecond / 1000);
    }

    protected override void RewriteDescriptors()
    {
        WriteImageDescriptors(
            0,
            new GtaoImageDescriptor(
                0,
                DescriptorType.CombinedImageSampler,
                _renderTargets.SceneDepth.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.DepthStencilReadOnlyOptimal),
            new GtaoImageDescriptor(
                1,
                DescriptorType.CombinedImageSampler,
                _renderTargets.MotionVectors.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.ShaderReadOnlyOptimal),
            new GtaoImageDescriptor(
                2,
                DescriptorType.StorageImage,
                _renderTargets.VariableRateShading.View,
                default,
                ImageLayout.General));
    }
}
