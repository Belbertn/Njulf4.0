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

            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _swapchain.Extent.Width,
                Height = _swapchain.Extent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = _swapchain.Extent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.TransparentForwardPipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                _meshPipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneColor.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.DontCare
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = _swapchain.Extent },
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
