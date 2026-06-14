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

        public TransparentForwardPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets)
            : base("TransparentForwardPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.TransparentPassEnabled)
                return;

            if (sceneData.TransparentMeshletCount <= 0)
                return;

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, renderExtent);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.TransparentForwardPipeline);
            BindBindlessStorageAndTextures(cmd, _meshPipeline.Layout);

            var colorAttachment = ColorAttachment(
                _renderTargets.SceneColor.View,
                ImageLayout.ColorAttachmentOptimal,
                AttachmentLoadOp.Load,
                AttachmentStoreOp.Store);

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
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.ViewMatrix.Invert(),
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
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

        public override void OnSwapchainRecreated()
        {
        }

        public override void Cleanup()
        {
        }
    }
}
