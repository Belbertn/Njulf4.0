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

        public WeightedTransparentPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets)
            : base("WeightedTransparentPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
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

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _renderTargets.WeightedOitAccumulation.TransitionToColorAttachment(cmd);
            _renderTargets.WeightedOitRevealage.TransitionToColorAttachment(cmd);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.WeightedOitTransparentPipeline);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

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
                AttachmentStoreOp.DontCare);

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
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = 0u,
                OcclusionBias = sceneData.OcclusionBias,
                DebugAndAoFlags = GPUForwardPushConstants.PackDebugAndAoFlags(
                    sceneData.DebugViewMode,
                    ambientOcclusionEnabled: false,
                    ambientOcclusionDebugView: (uint)sceneData.AmbientOcclusionDebugView,
                    transparentReceiveShadows: sceneData.TransparentReceiveShadows,
                    transparencyDebugView: (uint)sceneData.TransparencyDebugView)
            };

            uint size = (uint)Marshal.SizeOf<GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
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
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
    }
}
