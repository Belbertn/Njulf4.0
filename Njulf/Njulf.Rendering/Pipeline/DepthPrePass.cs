using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Depth prepass: renders all visible meshlets to create a hi-Z depth buffer.
    /// Uses mesh shaders with reverse-Z (depth cleared to 0.0, greater comparison).
    /// </summary>
    public sealed unsafe class DepthPrePass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        private readonly RenderTargetManager _renderTargets;
        
        public DepthPrePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline,
            RenderTargetManager renderTargets)
            : base("DepthPrePass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }
        
        public override void Initialize()
        {
            // Pipeline is already created
        }

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle solidDepthDraws = ProductionRenderGraphResources.SolidDepthMeshletDrawBuffer(resources);
            RenderGraphResourceHandle maskedDepthDraws = ProductionRenderGraphResources.MaskedDepthMeshletDrawBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Graphics)
            {
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true
            }
                .Write(
                    sceneDepth,
                    RenderGraphResourceAccess.DepthStencilAttachmentWrite,
                    PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    AttachmentLoadOp.Clear,
                    AttachmentStoreOp.Store,
                    new ClearValue(null, new ClearDepthStencilValue(0.0f, 0)))
                .Read(
                    solidDepthDraws,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt)
                .Read(
                    maskedDepthDraws,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.MeshShaderBitExt));
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            if (!sceneData.DepthPrePassEnabled)
                return;

            _renderTargets.SceneDepth.TransitionToDepthAttachment(cmd);
            var renderExtent = new Extent2D { Width = sceneData.ScreenWidth, Height = sceneData.ScreenHeight };

            // Set viewport and scissor
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
            
            // Bind descriptor sets
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
            
            // Begin rendering
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = default, // No color attachment for depth prepass
                ImageLayout = ImageLayout.Undefined,
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _renderTargets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = renderExtent },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };
            
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            
            DrawDepthList(
                cmd,
                sceneData,
                _meshPipeline.DepthPipeline,
                sceneData.SolidMeshletCount,
                BindlessIndex.SolidDepthMeshletDrawBufferBase);

            DrawDepthList(
                cmd,
                sceneData,
                _meshPipeline.MaskedDepthPipeline,
                sceneData.MaskedMeshletCount,
                BindlessIndex.MaskedDepthMeshletDrawBufferBase);
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }

        private void DrawDepthList(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            Silk.NET.Vulkan.Pipeline pipeline,
            int meshletCount,
            int meshletDrawBufferBaseIndex)
        {
            if (meshletCount <= 0)
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);

            var pushConstants = new GPUDepthPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)meshletCount,
                MeshletDrawBufferBaseIndex = (uint)meshletDrawBufferBaseIndex
            };

            uint size = (uint)Marshal.SizeOf<GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            sceneData.DepthTaskInvocations += meshletCount;
            _context.ExtMeshShader.CmdDrawMeshTask(cmd, (uint)meshletCount, 1, 1);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        private void TransitionDepthForWrite(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilAttachmentOptimal)
                return;

            var depthRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageLayout oldLayout = _swapchain.DepthImageLayout;
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilAttachmentOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                oldLayout,
                ImageLayout.DepthStencilAttachmentOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                depthRange);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }
        
        public override void OnSwapchainRecreated()
        {
            // Depth pass doesn't have swapchain-dependent resources
        }
        
        public override void Cleanup()
        {
        }
    }
}
