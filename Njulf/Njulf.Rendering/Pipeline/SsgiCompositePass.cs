using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SsgiCompositePass : RenderPassBase
    {
        private readonly SsgiCompositePipeline _pipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;

        public SsgiCompositePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            SsgiCompositePipeline pipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("SsgiCompositePass", context, swapchain, bindlessHeap)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return _settings.GlobalIllumination.EffectiveUseSsgi &&
                   sceneData.DepthPrePassEnabled &&
                   sceneData.AnimationDebugView == AnimationDebugView.None &&
                   RenderFeatureIsolationPolicy.AllowsPostProcessing(sceneData.ActiveFeatureIsolation);
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!ShouldExecute(frameIndex, sceneData))
                return;

            Extent2D outputExtent = _renderTargets.SceneColor.Extent;
            SetFullViewportAndScissor(cmd, outputExtent);
            _renderTargets.GiFinalDiffuse.TransitionToShaderRead(cmd);
            _renderTargets.SceneMaterial.TransitionToShaderRead(cmd);
            _renderTargets.SceneColor.TransitionToColorAttachment(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline.Pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeline.Layout, 1, 1, &textureSet, 0, null);

            var pushConstants = new GPUSsgiCompositePushConstants
            {
                GiFinalDiffuseTextureIndex = BindlessIndex.GiFinalDiffuseTexture,
                SceneMaterialTextureIndex = BindlessIndex.SceneMaterialTexture,
                DebugView = (uint)_settings.GlobalIllumination.DebugView
            };

            _context.Api.CmdPushConstants(
                cmd,
                _pipeline.Layout,
                ShaderStageFlags.FragmentBit,
                0,
                (uint)Marshal.SizeOf<GPUSsgiCompositePushConstants>(),
                &pushConstants);

            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneColor.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store
            };

            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = outputExtent },
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachment,
                PDepthAttachment = null,
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
