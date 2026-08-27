using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class WeightedTransparentPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RaySceneDescriptorBank? _raySceneDescriptors;

        private readonly ISimpleDdgiReceiverFeedbackCapture?
            _receiverFeedbackRuntime;

        public WeightedTransparentPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            RaySceneDescriptorBank? raySceneDescriptors = null,
            ISimpleDdgiReceiverFeedbackCapture? receiverFeedbackRuntime = null)
            : base("WeightedTransparentPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _raySceneDescriptors = raySceneDescriptors;
            _receiverFeedbackRuntime = receiverFeedbackRuntime;
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.TransparentPassEnabled &&
                   sceneData.TransparencyMode == TransparencyMode.WeightedBlendedOit &&
                   sceneData.TransparentMeshletCount > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            bool rayVariantRequired =
                sceneData.DirectionalShadowFramePlan.TransparentReceiverPolicy ==
                 DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery ||
                 sceneData.EffectiveThickTransmissionMode ==
                 ThickTransmissionMode.RayQuery ||
                TransparentForwardPass.RequiresSceneReflectionRayVariant(
                    sceneData);
            bool rayVariant = rayVariantRequired &&
                _meshPipeline.RayTransparentPipelinesAvailable &&
                _raySceneDescriptors?.IsAvailable == true;
            if (sceneData.TransparentReceiveGlobalIllumination ||
                rayVariant ||
                sceneData.DecalReceiveGlobalIllumination)
            {
                PublishComputeStorageToFragment(cmd);
            }

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.WeightedOitAccumulation.TransitionToColorAttachment(cmd);
            _renderTargets.WeightedOitRevealage.TransitionToColorAttachment(cmd);
            bool exactFeedback = TrySelectExactFeedbackPipeline(
                frameIndex,
                sceneData,
                rayVariant,
                out Silk.NET.Vulkan.Pipeline pipeline,
                out PipelineLayout pipelineLayout);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            BindBindlessStorageAndTextures(cmd, pipelineLayout);
            if (rayVariant)
            {
                _raySceneDescriptors!.Bind(
                    cmd,
                    PipelineBindPoint.Graphics,
                    pipelineLayout,
                    frameIndex);
            }

            var colorAttachments = stackalloc RenderingAttachmentInfo[2];
            colorAttachments[0] = ColorAttachment(
                _renderTargets.WeightedOitAccumulation.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));
            colorAttachments[1] = ColorAttachment(
                _renderTargets.WeightedOitRevealage.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Clear,
                AttachmentStoreOp.Store,
                new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 0.0f)));

            var depthAttachment = DepthAttachment(
                _renderTargets.SceneDepth.View,
                ImageLayout.DepthStencilReadOnlyOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 2,
                PColorAttachments = colorAttachments,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.InverseViewMatrix,
                InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.TransparentMeshletCount,
                MeshletDrawBufferBaseIndex = BindlessIndex.TransparentMeshletDrawBufferBase,
                PackedLightDispatch = GPUForwardPushConstants.PackLightDispatch(
                    sceneData.LightCount,
                    sceneData.LocalLightCount,
                    sceneData.DirectionalLightIndex0,
                    sceneData.DirectionalLightIndex1),
                LocalLightCount = (uint)sceneData.LocalLightCount,
                // Hi-Z is disabled for transparent task culling, so this word
                // carries the exact bounded optical traversal limits to the
                // fragment stage without changing the push-constant ABI.
                HiZMipCount = GPUForwardPushConstants.PackThickTransmissionLimits(
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumInterfaces,
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumMediaDepth,
                    _meshPipeline.Settings.Transparency
                        .ThickTransmissionMaximumCandidatesPerInterface),
                // Low bits remain Hi-Z off. Higher bits carry the exact
                // frame-local thick-transmission task budget.
                OcclusionCullingEnabled =
                    GPUForwardPushConstants.PackThickTransmissionTaskBudget(
                        _meshPipeline.Settings.Transparency
                            .ThickTransmissionRayTaskBudget),
                // Transparent task culling is disabled; this otherwise-unused
                // word carries the bounded optical path distance without
                // growing the frozen 256-byte forward push ABI.
                OcclusionBias = _meshPipeline.Settings.Transparency
                    .ThickTransmissionMaximumDistance,
                DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    ambientOcclusionEnabled: false,
                    ambientOcclusionDebugView: (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: sceneData.TransparentReceiveShadows,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView,
                    ambientOcclusionForwardSamplingMode: (uint)AmbientOcclusionForwardSamplingMode.Disabled,
                    globalIlluminationEnabled:
                    sceneData.TransparentReceiveGlobalIllumination),
                DiagnosticFlags = GPUForwardPushConstants.PackDiagnosticFlags(
                    ddgiForwardEstimateCountersEnabled: false,
                    directionalShadowPreviewCascade: (uint)sceneData.DirectionalShadowPreviewCascade,
                    decalGlobalIlluminationEnabled:
                    sceneData.DecalReceiveGlobalIllumination,
                    ddgiLayeredReceiverCountersEnabled:
                    sceneData.TransparentDdgiReceiverCountersEnabled,
                    decalReceiveShadows: sceneData.DecalReceiveShadows,
                    thickTransmissionRayQueryEnabled:
                    sceneData.EffectiveThickTransmissionMode ==
                    ThickTransmissionMode.RayQuery,
                    thickTransmissionDispersionEnabled:
                    sceneData.ThickTransmissionDispersionEnabled,
                    effectiveReflectionMode:
                    sceneData.EffectiveReflectionMode,
                    transparentSampleReflections:
                    sceneData.TransparentSampleReflections,
                    opaqueSceneColorSnapshotAvailable:
                    sceneData.OpaqueSceneColorSnapshotAvailable)
            };

            uint size = (uint)Marshal.SizeOf<GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                pipelineLayout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            _context.ExtMeshShader.CmdDrawMeshTask(
                cmd,
                (uint)sceneData.TransparentMeshletCount,
                1,
                1);

            _context.KhrDynamicRendering.CmdEndRendering(cmd);

            if (exactFeedback &&
                !_receiverFeedbackRuntime!.TryRecordOwnedProducerCompletion(
                    cmd,
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit,
                    out string completionReason))
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-weighted-oit-completion-failed:" +
                    completionReason);
            }
        }

        private bool TrySelectExactFeedbackPipeline(
            int frameIndex,
            SceneRenderingData sceneData,
            bool rayVariant,
            out Silk.NET.Vulkan.Pipeline pipeline,
            out PipelineLayout pipelineLayout)
        {
            pipeline = rayVariant
                ? _meshPipeline.RayWeightedOitTransparentPipeline
                : _meshPipeline.WeightedOitTransparentPipeline;
            pipelineLayout = rayVariant
                ? _meshPipeline.RayTransparentLayout
                : _meshPipeline.Layout;
            if (_receiverFeedbackRuntime is null ||
                !_receiverFeedbackRuntime.IsPendingOwnedProducerRequired(
                    frameIndex,
                    SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit))
            {
                return false;
            }

            if (sceneData.CurrentFrameIndex != checked((uint)frameIndex) ||
                (rayVariant
                    ? _meshPipeline.RayWeightedOitReceiverFeedbackPipeline.Handle
                    : _meshPipeline.WeightedOitReceiverFeedbackPipeline.Handle) == 0)
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-weighted-oit-pipeline-or-frame-slot-unavailable");
                return false;
            }

            pipeline = rayVariant
                ? _meshPipeline.RayWeightedOitReceiverFeedbackPipeline
                : _meshPipeline.WeightedOitReceiverFeedbackPipeline;
            return true;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
    }
}
