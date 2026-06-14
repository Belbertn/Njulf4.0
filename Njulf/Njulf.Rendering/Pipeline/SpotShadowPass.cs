using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SpotShadowPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly SpotShadowAtlas _atlas;
        private readonly ShadowSettings _settings;
        private readonly BufferManager _bufferManager;
        private readonly GpuVisibilityBufferSet _visibilityBuffers;

        public SpotShadowPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            SpotShadowAtlas atlas,
            ShadowSettings settings,
            BufferManager bufferManager,
            GpuVisibilityBufferSet visibilityBuffers)
            : base("SpotShadowPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _visibilityBuffers = visibilityBuffers ?? throw new ArgumentNullException(nameof(visibilityBuffers));
        }

        public override void Initialize()
        {
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle shadowAtlas = ProductionRenderGraphResources.SpotShadowAtlas(resources, _atlas);
            RenderGraphResourceHandle localShadowDraws = ProductionRenderGraphResources.LocalShadowMeshletDrawBuffer(resources);
            RenderGraphResourceHandle skinnedVertices = ProductionRenderGraphResources.SkinnedVertexBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .After("DirectionalShadowPass")
                .Write(
                    shadowAtlas,
                    RenderGraphResourceAccess.DepthStencilAttachmentWrite,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)))
                .Read(
                    localShadowDraws,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt)
                .Read(
                    skinnedVertices,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt));
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            Transition(cmd, ImageLayout.DepthStencilAttachmentOptimal);
            ClearAtlas(cmd);
            _context.Api.CmdSetDepthBias(cmd, _settings.SpotConstantDepthBias, 0.0f, _settings.SpotSlopeScaledDepthBias);
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ShadowAlphaDepthPipeline);
            BindDescriptors(cmd);

            for (int i = 0; i < sceneData.SpotShadowSelectedCount; i++)
            {
                _context.BeginDebugLabel(cmd, $"SpotShadowPass Light {i}");
                try
                {
                    RenderSpot(cmd, sceneData, i);
                }
                finally
                {
                    _context.EndDebugLabel(cmd);
                }
            }

            Transition(cmd, ImageLayout.DepthStencilReadOnlyOptimal);
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return FramePassRuntimePolicy.ShouldExecute(Name, sceneData);
        }

        private void RenderSpot(CommandBuffer cmd, SceneRenderingData sceneData, int shadowIndex)
        {
            SpotShadowAtlasRect rect = LocalShadowAllocator.GetSpotTileRect(_atlas.AtlasSize, _atlas.TileSize, shadowIndex);
            var viewport = new Viewport
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                MinDepth = 0.0f,
                MaxDepth = 1.0f
            };
            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = (int)rect.X, Y = (int)rect.Y },
                Extent = new Extent2D { Width = rect.Width, Height = rect.Height }
            };
            _context.Api.CmdSetViewport(cmd, 0, 1, &viewport);
            _context.Api.CmdSetScissor(cmd, 0, 1, &scissor);

            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _atlas.View,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = scissor,
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            GPUDepthPushConstants pushConstants = new()
            {
                ViewProjectionMatrix = sceneData.SpotShadowData[shadowIndex].LightViewProjection,
                ScreenDimensions = new Vector2(rect.Width, rect.Height),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)(sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.LocalShadowListCapacity : sceneData.LocalShadowMeshletCount),
                MeshletDrawBufferBaseIndex = BindlessIndex.LocalShadowMeshletDrawBufferBase,
                FirstMeshletDrawIndex = (uint)(sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.GetSpotShadowFirstDrawIndex(shadowIndex) : 0u)
            };
            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(cmd, _meshPipeline.Layout, ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt, 0, size, &pushConstants);
            int drawCount = sceneData.GpuDrivenVisibilityEnabled ? _visibilityBuffers.LocalShadowListCapacity : sceneData.LocalShadowMeshletCount;
            if (sceneData.GpuDrivenVisibilityEnabled)
            {
                _context.ExtMeshShader.CmdDrawMeshTasksIndirect(
                    cmd,
                    _bufferManager.GetBuffer(_visibilityBuffers.GetCounterBuffer((int)sceneData.CurrentFrameIndex)),
                    _visibilityBuffers.GetIndirectCommandOffset(_visibilityBuffers.GetSpotShadowIndirectList(shadowIndex)),
                    1,
                    (uint)Marshal.SizeOf<GPUMeshTaskIndirectCommand>());
            }
            else
            {
                _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)drawCount, 1, 1);
            }
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void ClearAtlas(CommandBuffer cmd)
        {
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _atlas.View,
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
                    Extent = new Extent2D { Width = _atlas.AtlasSize, Height = _atlas.AtlasSize }
                },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment
            };

            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void BindDescriptors(CommandBuffer cmd)
        {
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _meshPipeline.Layout, 1, 1, &textureSet, 0, null);
        }

        private void Transition(CommandBuffer cmd, ImageLayout newLayout)
        {
            if (_atlas.Layout == newLayout)
                return;
            ImageLayout oldLayout = _atlas.Layout;
            _atlas.Layout = newLayout;
            var range = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
            var barrier = BarrierBuilder.CreateImageBarrier(
                _atlas.Image,
                oldLayout == ImageLayout.DepthStencilAttachmentOptimal ? PipelineStageFlags2.LateFragmentTestsBit : PipelineStageFlags2.None,
                oldLayout == ImageLayout.DepthStencilAttachmentOptimal ? AccessFlags2.DepthStencilAttachmentWriteBit : AccessFlags2.None,
                newLayout == ImageLayout.DepthStencilAttachmentOptimal ? PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit : PipelineStageFlags2.FragmentShaderBit,
                newLayout == ImageLayout.DepthStencilAttachmentOptimal ? AccessFlags2.DepthStencilAttachmentWriteBit : AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                newLayout,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                range);
            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }
    }
}
