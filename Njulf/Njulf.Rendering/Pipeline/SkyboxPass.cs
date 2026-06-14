using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SkyboxPass : RenderPassBase
    {
        private readonly SkyboxPipeline _skyboxPipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;

        public SkyboxPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            SkyboxPipeline skyboxPipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("SkyboxPass", context, swapchain, bindlessHeap)
        {
            _skyboxPipeline = skyboxPipeline ?? throw new ArgumentNullException(nameof(skyboxPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneColor = ProductionRenderGraphResources.HdrSceneColor(resources);
            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle environment = ProductionRenderGraphResources.EnvironmentCubemap(resources, _settings);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .After("ForwardPlusPass")
                .ReadWrite(
                    sceneColor,
                    RenderGraphResourceAccess.ColorAttachmentWrite,
                    PipelineStageFlags2.ColorAttachmentOutputBit,
                    AttachmentLoadOp.Load,
                    AttachmentStoreOp.Store)
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentRead,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)
                .Read(
                    environment,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.FragmentShaderBit));
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!_settings.Environment.Enabled || _settings.AmbientOcclusion.DebugView != AmbientOcclusionDebugView.None)
                return;

            Extent2D renderExtent = _renderTargets.SceneColor.Extent;
            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = renderExtent.Width,
                Height = renderExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = renderExtent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            _renderTargets.SceneDepth.TransitionToDepthReadOnly(cmd);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _skyboxPipeline.Pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _skyboxPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _skyboxPipeline.Layout, 1, 1, &textureSet, 0, null);

            var pushConstants = new GPUSkyboxPushConstants
            {
                InverseViewMatrix = sceneData.ViewMatrix.Invert(),
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
                EnvironmentTextureIndex = BindlessIndex.EnvironmentCubemapTexture,
                SkyIntensity = _settings.Environment.SkyIntensity,
                RotationRadians = _settings.Environment.RotationRadians,
                DebugView = (uint)_settings.Environment.DebugView
            };

            uint size = (uint)Marshal.SizeOf<GPUSkyboxPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _skyboxPipeline.Layout,
                ShaderStageFlags.FragmentBit,
                0,
                size,
                &pushConstants);

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
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.DontCare
            };
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
            _context.Api.CmdDraw(cmd, 3, 1, 0, 0);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
    }
}
