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
    /// Forward+ pass: renders all visible meshlets with per-tile lighting.
    /// Input: meshlet data, material data, textures, light index buffers
    /// Uses mesh shaders and bindless resource access.
    /// </summary>
    public sealed class ForwardPlusPass : RenderPassBase
    {
        private readonly PipelineObjects.MeshPipeline _meshPipeline;
        
        public ForwardPlusPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.MeshPipeline meshPipeline)
            : base("ForwardPlusPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
        }
        
        public override void Initialize()
        {
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
            
            // Get swapchain image for this frame
            var swapchainImageView = _swapchain.ImageViews[frameIndex];
            
            // Begin rendering with color and depth attachments
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = swapchainImageView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load, // Load existing content
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(0.1f, 0.1f, 0.1f, 1.0f))
            };
            
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load, // Load from depth prepass
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearDepthStencilValue(0.0f, 0))
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
            
            // Push constants
            var pushConstants = new Data.GPUSceneData
            {
                ViewMatrix = sceneData.ViewMatrix,
                ProjectionMatrix = sceneData.ProjectionMatrix,
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                CameraPosition = sceneData.ViewMatrix.Translation,
                Time = sceneData.Time,
                ScreenDimensions = new Vector4(sceneData.ScreenWidth, sceneData.ScreenHeight, 0, 0),
                NearFarPlanes = new Vector4(0.1f, 1000.0f, 0, 0),
                LightCount = sceneData.LightCount,
                Padding0 = 0,
                Padding1 = 0,
                Padding2 = 0
            };
            
            ulong size = (ulong)Marshal.SizeOf(typeof(Data.GPUSceneData));
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                0,
                size,
                &pushConstants);
            
            // TODO: Dispatch mesh shader for all visible meshlets
            // This would:
            // 1. Bind the meshlet draw buffer
            // 2. Use vk.CmdDrawMeshTasksEXT for GPU-driven dispatch
            // 3. Or use vk.CmdDispatchMesh for explicit dispatch
            
            _context.KhrDynamicRendering.CmdEndRendering(cmd);
            
            // Transition swapchain image to TransferSrc for present
            var barrier = BarrierBuilder.TransferSrcToPresent(swapchainImageView);
            BarrierBuilder.ExecuteBarrier(cmd, barrier);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            // Ensure depth is ready for forward pass
            yield return BarrierBuilder.DepthStencilToReadOnly(_swapchain.DepthImage);
        }
        
        public override void OnSwapchainRecreated()
        {
        }
        
        public override void Cleanup()
        {
        }
    }
    
}
