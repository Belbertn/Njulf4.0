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
    public sealed unsafe class ToneMapCompositePass : RenderPassBase
    {
        private readonly CompositePipeline _compositePipeline;
        private readonly CompositePipeline _ldrCompositePipeline;
        private readonly RenderTargetManager _renderTargets;
        private readonly RenderSettings _settings;

        public ToneMapCompositePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            CompositePipeline compositePipeline,
            CompositePipeline ldrCompositePipeline,
            RenderTargetManager renderTargets,
            RenderSettings settings)
            : base("ToneMapCompositePass", context, swapchain, bindlessHeap)
        {
            _compositePipeline = compositePipeline ?? throw new ArgumentNullException(nameof(compositePipeline));
            _ldrCompositePipeline = ldrCompositePipeline ?? throw new ArgumentNullException(nameof(ldrCompositePipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            bool antiAliasingEnabled = _settings.AntiAliasing.EffectiveMode != AntiAliasingMode.None;
            CompositePipeline pipeline = antiAliasingEnabled ? _ldrCompositePipeline : _compositePipeline;
            Extent2D outputExtent = antiAliasingEnabled ? _renderTargets.LdrSceneColor.Extent : _swapchain.Extent;
            int activeSceneColorTextureIndex = sceneData.ActiveSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture
                ? BindlessIndex.FoggedSceneColorTexture
                : BindlessIndex.HdrSceneColorTexture;
            RenderTarget activeSceneColor = activeSceneColorTextureIndex == BindlessIndex.FoggedSceneColorTexture
                ? _renderTargets.FoggedSceneColor
                : _renderTargets.SceneColor;
            activeSceneColor.TransitionToShaderRead(cmd);

            var viewport = new Viewport
            {
                X = 0,
                Y = 0,
                Width = outputExtent.Width,
                Height = outputExtent.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = outputExtent
            };

            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline.Pipeline);

            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                pipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                pipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);

            var pushConstants = new GPUCompositePushConstants
            {
                SceneColorTextureIndex = (uint)activeSceneColorTextureIndex,
                BloomTextureIndex = (uint)BindlessIndex.BloomMipTextureBase,
                BloomDebugTextureIndex = (uint)GetBloomDebugTextureIndex(),
                BloomEnabled = sceneData.BloomEnabled && _renderTargets.BloomMipCount > 0 ? 1u : 0u,
                Exposure = _settings.Exposure,
                BloomIntensity = _settings.Bloom.Intensity,
                ToneMapper = (uint)_settings.ToneMapper,
                DebugViewMode = GetDebugViewMode(),
                OutputToSrgb = antiAliasingEnabled ? 0u : IsSrgbFormat(_swapchain.SurfaceFormat) ? 0u : 1u,
                EnvironmentDebugView = (uint)_settings.Environment.DebugView,
                EnvironmentDebugMipLevel = (uint)_settings.Environment.DebugMipLevel,
                AmbientOcclusionDebugTextureIndex = (uint)GetAmbientOcclusionDebugTextureIndex(),
                AutoExposureEnabled = _settings.AutoExposure.Enabled ? 1u : 0u,
                AutoExposureStateBufferIndex = (uint)(BindlessIndex.AutoExposureStateBufferBase + frameIndex)
            };

            uint size = (uint)Marshal.SizeOf<GPUCompositePushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                pipeline.Layout,
                ShaderStageFlags.FragmentBit,
                0,
                size,
                &pushConstants);

            uint imageIndex = sceneData.ImageIndex < _swapchain.ImageCount ? sceneData.ImageIndex : (uint)frameIndex;
            ImageView outputView = antiAliasingEnabled
                ? _renderTargets.LdrSceneColor.View
                : _swapchain.ImageViews[imageIndex];
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = outputView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0.0f, 0.0f, 0.0f, 1.0f))
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

        private static bool IsSrgbFormat(Format format)
        {
            return format == Format.R8G8B8A8Srgb ||
                   format == Format.B8G8R8A8Srgb ||
                   format == Format.A8B8G8R8SrgbPack32;
        }

        private uint GetDebugViewMode()
        {
            if (_settings.ShowRawHdrSceneColor)
                return 1u;

            return _settings.Bloom.DebugView == BloomDebugView.None
                ? 0u
                : (uint)_settings.Bloom.DebugView + 1u;
        }

        private int GetBloomDebugTextureIndex()
        {
            if (_renderTargets.BloomMipCount == 0)
                return BindlessIndex.DefaultBlackTexture;

            int mip = _settings.Bloom.DebugView switch
            {
                BloomDebugView.DownsampleMip => Math.Min(_settings.Bloom.DebugMipLevel, _renderTargets.BloomMipCount - 1),
                BloomDebugView.ExtractMask => 0,
                BloomDebugView.UpsampleResult => 0,
                BloomDebugView.BloomOnly => 0,
                _ => 0
            };

            return BindlessIndex.BloomMipTextureBase + mip;
        }

        private int GetAmbientOcclusionDebugTextureIndex()
        {
            return _settings.AmbientOcclusion.DebugView switch
            {
                AmbientOcclusionDebugView.RawAo => BindlessIndex.AmbientOcclusionRawTexture,
                AmbientOcclusionDebugView.BlurredAo => BindlessIndex.AmbientOcclusionBlurredTexture,
                _ => 0
            };
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }
    }
}
