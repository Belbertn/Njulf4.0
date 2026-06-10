using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Depth prepass: renders all visible meshlets to create a hi-Z depth buffer.
    /// Uses mesh shaders with reverse-Z (depth cleared to 0.0, greater comparison).
    /// </summary>
    public sealed unsafe class DepthPrePass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        
        public DepthPrePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline)
            : base("DepthPrePass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
        }
        
        public override void Initialize()
        {
            // Pipeline is already created
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            if (!sceneData.DepthPrePassEnabled)
                return;

            TransitionDepthForWrite(cmd);

            // Set viewport and scissor
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
            
            // Bind pipeline
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.DepthPipeline);
            
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
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
            };
            
            var renderingInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = _swapchain.Extent },
                LayerCount = 1,
                ColorAttachmentCount = 0,
                PColorAttachments = null,
                PDepthAttachment = &depthAttachment,
                PStencilAttachment = null
            };
            
            _context.KhrDynamicRendering.CmdBeginRendering(cmd, &renderingInfo);
            
            // Push constants
            var pushConstants = new Data.GPUDepthPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.OpaqueMeshletCount
            };
            
            uint size = (uint)Marshal.SizeOf<Data.GPUDepthPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (sceneData.OpaqueMeshletCount > 0)
            {
                sceneData.DepthTaskInvocations = sceneData.OpaqueMeshletCount;
                _context.ExtMeshShader.CmdDrawMeshTask(
                    cmd,
                    (uint)sceneData.OpaqueMeshletCount,
                    1,
                    1);
            }
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
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
