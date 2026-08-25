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
    public sealed unsafe class TransparentForwardPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly ForwardPlusPass _forwardPlusPass;
        private readonly RaySceneDescriptorBank? _raySceneDescriptors;
        private readonly SimpleDdgiReceiverFeedbackVulkanRuntime?
            _receiverFeedbackRuntime;

        public TransparentForwardPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets,
            ForwardPlusPass forwardPlusPass,
            RaySceneDescriptorBank? raySceneDescriptors = null,
            SimpleDdgiReceiverFeedbackVulkanRuntime? receiverFeedbackRuntime = null)
            : base("TransparentForwardPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _forwardPlusPass = forwardPlusPass ??
                throw new ArgumentNullException(nameof(forwardPlusPass));
            _raySceneDescriptors = raySceneDescriptors;
            _receiverFeedbackRuntime = receiverFeedbackRuntime;
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return sceneData.TransparentPassEnabled &&
                   sceneData.TransparencyMode == TransparencyMode.SortedAlphaBlend &&
                   sceneData.TransparentMeshletCount > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            bool rayVariant =
                (sceneData.DirectionalShadowFramePlan.TransparentReceiverPolicy ==
                    DirectionalShadowReceiverPolicy.LayeredFragmentRayQuery ||
                 sceneData.EffectiveThickTransmissionMode ==
                    ThickTransmissionMode.RayQuery) &&
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
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            bool exactFeedback = TrySelectExactFeedbackPipeline(
                frameIndex,
                sceneData,
                rayVariant,
                out Silk.NET.Vulkan.Pipeline pipeline,
                out PipelineLayout pipelineLayout);
            bool decalReceiverCache = ShouldUseDecalReceiverCache(
                sceneData,
                exactFeedback,
                rayVariant,
                _forwardPlusPass.CanConsumeSimpleDdgiReceiverCacheForCurrentView,
                _meshPipeline.TransparentReceiverCachePipeline.Handle != 0);
            if (decalReceiverCache)
            {
                pipeline = _meshPipeline.TransparentReceiverCachePipeline;
                pipelineLayout = _meshPipeline.Layout;
            }
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            BindBindlessStorageAndTextures(cmd, pipelineLayout);
            if (decalReceiverCache)
            {
                _forwardPlusPass.BindSimpleDdgiReceiverCacheBuffer(
                    cmd,
                    frameIndex);
            }
            if (rayVariant)
            {
                _raySceneDescriptors!.Bind(
                    cmd,
                    PipelineBindPoint.Graphics,
                    pipelineLayout,
                    frameIndex);
            }

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

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
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
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
                LightCount = (uint)sceneData.LightCount,
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
                        sceneData.ThickTransmissionDispersionEnabled)
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
                    "receiver-feedback-transparent-completion-failed:" +
                    completionReason);
            }
        }

        internal static bool ShouldUseDecalReceiverCache(
            SceneRenderingData sceneData,
            bool exactFeedback,
            bool rayVariant,
            bool receiverCacheAvailable,
            bool receiverCachePipelineAvailable)
        {
            ArgumentNullException.ThrowIfNull(sceneData);

            // Geometry decals are depth-backed overlays, so the current-frame
            // low-frequency irradiance cache produced from the opaque depth
            // owner is the correct bounded approximation. Real transparent
            // surfaces may not have an opaque depth owner and retain the exact
            // gather path. Exact B1 feedback also always takes precedence.
            return sceneData.TransparentObjectCount == 0 &&
                   sceneData.TransparentMeshletCount > 0 &&
                   sceneData.GeometryDecalMeshletCount >=
                       sceneData.TransparentMeshletCount &&
                   sceneData.DecalReceiveGlobalIllumination &&
                   !exactFeedback &&
                   !rayVariant &&
                   receiverCacheAvailable &&
                   receiverCachePipelineAvailable;
        }

        private bool TrySelectExactFeedbackPipeline(
            int frameIndex,
            SceneRenderingData sceneData,
            bool rayVariant,
            out Silk.NET.Vulkan.Pipeline pipeline,
            out PipelineLayout pipelineLayout)
        {
            pipeline = rayVariant
                ? _meshPipeline.RayTransparentForwardPipeline
                : _meshPipeline.TransparentForwardPipeline;
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
                    ? _meshPipeline.RayTransparentReceiverFeedbackPipeline.Handle
                    : _meshPipeline.TransparentReceiverFeedbackPipeline.Handle) == 0)
            {
                _receiverFeedbackRuntime.AbortCapture(
                    "receiver-feedback-transparent-pipeline-or-frame-slot-unavailable");
                return false;
            }
            pipeline = rayVariant
                ? _meshPipeline.RayTransparentReceiverFeedbackPipeline
                : _meshPipeline.TransparentReceiverFeedbackPipeline;
            return true;
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void OnSwapchainRecreated()
        {
        }

        public override void Cleanup()
        {
        }
    }
}
