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
    public sealed unsafe class ForwardPlusPass : RenderPassBase
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
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _meshPipeline.ForwardPipeline);
            
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
            var imageIndex = sceneData.ImageIndex < _swapchain.ImageCount ? sceneData.ImageIndex : (uint)frameIndex;
            var swapchainImageView = _swapchain.ImageViews[imageIndex];
            
            // Begin rendering with color and depth attachments
            var colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = swapchainImageView,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(
                    sceneData.ClearColor.X,
                    sceneData.ClearColor.Y,
                    sceneData.ClearColor.Z,
                    sceneData.ClearColor.W))
            };
            
            var depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = _swapchain.DepthImageView,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                LoadOp = AttachmentLoadOp.Load, // Load from depth prepass
                StoreOp = AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue(null, new ClearDepthStencilValue(0.0f, 0))
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
            var pushConstants = new Data.GPUForwardPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewMatrix = sceneData.ViewMatrix.Invert(),
                InverseProjectionMatrix = sceneData.ProjectionMatrix.Invert(),
                CameraPosition = sceneData.CameraPosition,
                Time = sceneData.Time,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                MeshletDrawCount = (uint)sceneData.OpaqueMeshletCount,
                MeshletDrawBufferBaseIndex = BindlessIndex.MeshletDrawBufferBase,
                LightCount = (uint)sceneData.LightCount,
                LocalLightCount = (uint)sceneData.LocalLightCount,
                HiZTextureIndex = BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? 1u : 0u,
                OcclusionBias = sceneData.OcclusionBias,
                DebugViewMode = sceneData.DebugViewMode
            };
            
            uint size = (uint)Marshal.SizeOf<Data.GPUForwardPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.Layout,
                ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.TaskBitExt,
                0,
                size,
                &pushConstants);

            if (sceneData.OpaqueMeshletCount > 0)
            {
                sceneData.ForwardTaskInvocations = sceneData.OpaqueMeshletCount;
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
        
        public override void OnSwapchainRecreated()
        {
        }
        
        public override void Cleanup()
        {
        }
    }
    
}
