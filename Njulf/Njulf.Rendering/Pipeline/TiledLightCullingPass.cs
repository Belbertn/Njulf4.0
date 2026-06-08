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
        private BufferHandle _tiledLightHeaderBuffer;
        private BufferHandle _tiledLightIndicesBuffer;
        
        private const int TileSize = 16;
        private const int MaxLightsPerTile = 128;
        
        public TiledLightCullingPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            PipelineObjects.ComputePipeline computePipeline,
            BufferManager bufferManager)
            : base("TiledLightCullingPass", context, swapchain, bindlessHeap)
        {
            _computePipeline = computePipeline ?? throw new ArgumentNullException(nameof(computePipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        }
        
        public override void Initialize()
        {
            // Create tiled light buffers
            uint tileCountX = (uint)Math.Ceiling(_swapchain.Extent.Width / (float)TileSize);
            uint tileCountY = (uint)Math.Ceiling(_swapchain.Extent.Height / (float)TileSize);
            uint totalTiles = tileCountX * tileCountY;
            
            // Header buffer: one uint per tile for light count
            _tiledLightHeaderBuffer = _bufferManager.CreateDeviceBuffer(
                totalTiles * 4,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            // Indices buffer: MaxLightsPerTile * totalTiles * 4 bytes
            _tiledLightIndicesBuffer = _bufferManager.CreateDeviceBuffer(
                totalTiles * MaxLightsPerTile * 4,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            // Register buffers in bindless heap
            _bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.TiledLightHeaderBuffer,
                _bufferManager.GetBuffer(_tiledLightHeaderBuffer),
                0,
                Vk.WholeSize);
            
            _bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.TiledLightIndicesBuffer,
                _bufferManager.GetBuffer(_tiledLightIndicesBuffer),
                0,
                Vk.WholeSize);
            
            Console.WriteLine("Tiled light culling pass initialized.");
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
            
            // Calculate dispatch size
            uint tileCountX = (uint)Math.Ceiling(_swapchain.Extent.Width / (float)TileSize);
            uint tileCountY = (uint)Math.Ceiling(_swapchain.Extent.Height / (float)TileSize);
            
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
                MaxLightsPerTile = (uint)MaxLightsPerTile,
                TileCountX = tileCountX,
                TileCountY = tileCountY
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
            
            // Memory barrier for compute shader writes
            var bufferBarriers = new[]
            {
                BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(_tiledLightHeaderBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderReadBit),
                BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(_tiledLightIndicesBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderWriteBit,
                PipelineStageFlags2.FragmentShaderBit,
                AccessFlags2.ShaderReadBit)
            };

            BarrierBuilder.ExecuteBarrier(cmd, bufferBarriers: bufferBarriers);
        }
        
        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            // Ensure depth buffer is ready for reading
            yield return BarrierBuilder.DepthStencilToReadOnly(_swapchain.DepthImage);
        }
        
        public override void OnSwapchainRecreated()
        {
            // Reinitialize buffers with new dimensions
            Cleanup();
            Initialize();
        }
        
        public override void Cleanup()
        {
            if (_tiledLightHeaderBuffer.IsValid)
            {
                _bufferManager.DestroyBuffer(_tiledLightHeaderBuffer);
                _tiledLightHeaderBuffer = BufferHandle.Invalid;
            }
            if (_tiledLightIndicesBuffer.IsValid)
            {
                _bufferManager.DestroyBuffer(_tiledLightIndicesBuffer);
                _tiledLightIndicesBuffer = BufferHandle.Invalid;
            }
        }
    }
    
}
