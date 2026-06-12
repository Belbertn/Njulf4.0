using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class DirectionalShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly DirectionalShadowResources _shadowResources;
        private readonly ShadowSettings _settings;

        public DirectionalShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            DirectionalShadowResources shadowResources,
            ShadowSettings settings)
            : base("DirectionalShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _shadowResources = shadowResources ?? throw new ArgumentNullException(nameof(shadowResources));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.DirectionalShadowPassEnabled || sceneData.OpaqueMeshletCount <= 0)
                return;

            TransitionShadowMap(cmd, ImageLayout.DepthStencilAttachmentOptimal);

            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = _shadowResources.MapSize,
                Height = _shadowResources.MapSize,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = new Extent2D { Width = _shadowResources.MapSize, Height = _shadowResources.MapSize }
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            _context.Api.CmdSetDepthBias(cmd, _settings.ConstantDepthBias, 0.0f, _settings.SlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ShadowAlphaDepthPipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 1, 1, &textureSet, 0, null);

            int cascadeCount = Math.Min(sceneData.DirectionalShadowCascadeCount, _shadowResources.CascadeCount);
            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                _context.BeginDebugLabel(cmd, $"DirectionalShadowPass Cascade {cascade}");
                try
                {
                    RenderCascade(cmd, sceneData, cascade);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }

            TransitionShadowMap(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void RenderCascade(CommandBuffer cmd, SceneRenderingData sceneData, int cascade)
        {
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _shadowResources.GetCascadeView(cascade),
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = _shadowResources.MapSize, Height = _shadowResources.MapSize }
                },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = GetCascadeMatrix(sceneData.ShadowData, cascade),
                ScreenDimensions = new Vector2(_shadowResources.MapSize, _shadowResources.MapSize),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.DirectionalShadowMeshletCounts[cascade],
                MeshletDrawBufferBaseIndex = BindlessIndex.DirectionalShadowMeshletDrawBufferBase
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (sceneData.DirectionalShadowMeshletCounts[cascade] > 0)
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)sceneData.DirectionalShadowMeshletCounts[cascade], 1, 1);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void TransitionShadowMap(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_shadowResources.Layout == newLayout)
                return;

            ImageLayout oldLayout = _shadowResources.Layout;
            _shadowResources.Layout = newLayout;

            PipelineStageFlags2 srcStage = oldLayout == ImageLayout.DepthStencilAttachmentOptimal
                ? PipelineStageFlags2.LateFragmentTestsBit
                : PipelineStageFlags2.None;
            AccessFlags2 srcAccess = oldLayout == ImageLayout.DepthStencilAttachmentOptimal
                ? AccessFlags2.DepthStencilAttachmentWriteBit
                : AccessFlags2.None;
            PipelineStageFlags2 dstStage = newLayout == ImageLayout.DepthStencilAttachmentOptimal
                ? PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit
                : PipelineStageFlags2.FragmentShaderBit;
            AccessFlags2 dstAccess = newLayout == ImageLayout.DepthStencilAttachmentOptimal
                ? AccessFlags2.DepthStencilAttachmentWriteBit
                : AccessFlags2.ShaderSampledReadBit;

            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = (uint)_shadowResources.CascadeCount
            };

            var barrier = BarrierBuilder.CreateImageBarrier(
                _shadowResources.Image,
                srcStage,
                srcAccess,
                dstStage,
                dstAccess,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);
            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }

        private static Matrix4x4 GetCascadeMatrix(GPUShadowData data, int cascade)
        {
            return cascade switch
            {
                0 => data.LightViewProjection0,
                1 => data.LightViewProjection1,
                2 => data.LightViewProjection2,
                _ => data.LightViewProjection3
            };
        }
    }
}
