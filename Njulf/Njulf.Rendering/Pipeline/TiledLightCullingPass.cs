using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Silk.NET.Vulkan;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Utilities;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Tiled light culling pass: compute shader that assigns lights to screen tiles.
    /// Input: light buffer, depth buffer
    /// Output: per-tile light lists (headers + indices)
    /// </summary>
    public sealed unsafe class TiledLightCullingPass : RenderPassBase
    {
        private readonly PipelineObjects.ComputePipeline _computePipeline;
        private readonly BufferManager _bufferManager;
        private readonly RenderTargetManager _renderTargets;
        
        public TiledLightCullingPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.ComputePipeline computePipeline,
            BufferManager bufferManager,
            RenderTargetManager renderTargets)
            : base("TiledLightCullingPass", context, swapchain, bindlessHeap)
        {
            _computePipeline = computePipeline ?? throw new ArgumentNullException(nameof(computePipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        }
        
        public override void Initialize()
        {
            System.Diagnostics.Debug.WriteLine("Tiled light culling pass initialized.");
        }

        public override bool SupportsSecondaryCommandBuffer => true;

        public override void DeclareResources(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            RenderGraphResourceHandle sceneDepth = ProductionRenderGraphResources.SceneDepth(resources, _swapchain.DepthFormat);
            RenderGraphResourceHandle lightBuffer = ProductionRenderGraphResources.LightBuffer(resources);
            RenderGraphResourceHandle tileHeaders = ProductionRenderGraphResources.TiledLightHeaderBuffer(resources);
            RenderGraphResourceHandle tileIndices = ProductionRenderGraphResources.TiledLightIndexBuffer(resources);

            resources.AddPass(new RenderGraphPassDesc(Name, RenderGraphQueueClass.Compute)
            {
                AsyncEligible = true,
                PreferredQueue = RenderGraphQueueClass.Compute,
                ExpectedWorkloadScore = 100,
                DependencyUrgency = RenderGraphDependencyUrgency.ImmediateGraphicsConsumer,
                TimingLabel = Name,
                HasExternalSideEffect = true,
                NeverCull = true,
                SupportsSecondaryCommandBuffer = SupportsSecondaryCommandBuffer
            }.SupportsQueue(RenderGraphQueueClass.Graphics)
                .After("AmbientOcclusionBlurPass")
                .Read(
                    sceneDepth,
                    RenderGraphResourceAccess.SampledRead,
                    PipelineStageFlags2.ComputeShaderBit)
                .Read(
                    lightBuffer,
                    RenderGraphResourceAccess.StorageRead,
                    PipelineStageFlags2.ComputeShaderBit)
                .Write(
                    tileHeaders,
                    RenderGraphResourceAccess.StorageWrite,
                    PipelineStageFlags2.ComputeShaderBit)
                .Write(
                    tileIndices,
                    RenderGraphResourceAccess.StorageWrite,
                    PipelineStageFlags2.ComputeShaderBit));
        }
        
        public override void Execute(CommandBuffer cmd, int frameIndex, Data.SceneRenderingData sceneData)
        {
            // Bind pipeline
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _computePipeline.Pipeline);
            
            // Bind descriptor sets
            var storageSet = _bindlessHeap.StorageBufferSet;
            var textureSet = _bindlessHeap.TextureSamplerSet;
            
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _computePipeline.Layout,
                0,
                1,
                &storageSet,
                0,
                null);
            
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _computePipeline.Layout,
                1,
                1,
                &textureSet,
                0,
                null);
            
            if (!sceneData.TiledLightHeaderBuffer.IsValid || !sceneData.TiledLightIndexBuffer.IsValid)
                throw new InvalidOperationException("Scene tiled light buffers are not initialized.");

            uint tileCountX = sceneData.TileCountX;
            uint tileCountY = sceneData.TileCountY;
            
            // Push constants
            var pushConstants = new Data.GPULightCullPushConstants
            {
                ViewProjectionMatrix = sceneData.ViewProjectionMatrix,
                InverseViewProjectionMatrix = sceneData.ViewProjectionMatrix.Invert(),
                CameraPosition = sceneData.CameraPosition,
                ScreenDimensions = new Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                NearPlane = 0.1f,
                FarPlane = 1000.0f,
                LightCount = (uint)sceneData.LightCount,
                MaxLightsPerTile = (uint)sceneData.MaxLightsPerTile,
                TileCountX = tileCountX,
                TileCountY = tileCountY,
                DepthTextureIndex = (uint)BindlessIndex.DepthTexture,
                Padding1 = 0,
                Padding2 = 0,
                Padding3 = 0
            };
            
            uint size = (uint)Marshal.SizeOf<Data.GPULightCullPushConstants>();
            _context.Api.CmdPushConstants(
                cmd,
                _computePipeline.Layout,
                ShaderStageFlags.ComputeBit,
                0,
                size,
                &pushConstants);
            
            // Dispatch compute shader
            _context.Api.CmdDispatch(
                cmd,
                tileCountX,
                tileCountY,
                1);
            
            var bufferBarriers = new[]
            {
                BarrierBuilder.BufferBarrier(
                    _bufferManager.GetBuffer(sceneData.TiledLightHeaderBuffer),
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit),
                BarrierBuilder.BufferBarrier(
                    _bufferManager.GetBuffer(sceneData.TiledLightIndexBuffer),
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit)
            };

            BarrierBuilder.ExecuteBarrier(cmd, bufferBarriers: bufferBarriers);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return FramePassRuntimePolicy.ShouldExecute(Name, sceneData);
        }

        private void TransitionDepthForRead(CommandBuffer cmd)
        {
            if (_swapchain.DepthImageLayout == ImageLayout.DepthStencilReadOnlyOptimal)
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
            _swapchain.SetDepthImageLayout(ImageLayout.DepthStencilReadOnlyOptimal);

            var barrier = BarrierBuilder.CreateImageBarrier(
                _swapchain.DepthImage,
                PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                oldLayout,
                ImageLayout.DepthStencilReadOnlyOptimal,
                Vk.QueueFamilyIgnored,
                Vk.QueueFamilyIgnored,
                depthRange);

            BarrierBuilder.ExecuteImageBarrier(cmd, barrier);
        }
        
        public override void OnSwapchainRecreated()
        {
        }
        
        public override void Cleanup()
        {
        }
    }
    
}
