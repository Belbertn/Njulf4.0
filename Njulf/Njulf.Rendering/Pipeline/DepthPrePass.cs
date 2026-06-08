using System;
using System.Collections.Generic;
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
    public sealed class DepthPrePass : RenderPassBase
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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.Pipeline);
            
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
                ClearValue = new ClearValue(new ClearDepthStencilValue(0.0f, 0))
            };
            
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearDepthStencilValue(0.0f, 0))
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
            var pushConstants = new Data.GPUSceneData
            {
                ViewMatrix = sceneData.ViewMatrix,
                ProjectionMatrix = sceneData.ProjectionMatrix,
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPosition = sceneData.ViewMatrix.Translation,
                Time = sceneData.Time,
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 0, 0),
                NearFarPlanes = new Vector4(0.1f, 1000.0f, 0, 0)
            };
            
            ulong size = (ulong)Marshal.SizeOf(typeof(Data.GPUSceneData));
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt,
                0,
                size,
                &pushConstants);
            
            // TODO: Dispatch mesh shader for all visible meshlets
            // This is a placeholder - actual implementation would:
            // 1. Bind the meshlet draw buffer
            // 2. Dispatch mesh shader with draw count from buffer
            
            // For now, just clear depth
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            // Transition depth image to DepthStencilAttachmentOptimal
            yield return BarrierBuilder.UndefinedToDepthStencil(_swapchain.DepthImage);
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
