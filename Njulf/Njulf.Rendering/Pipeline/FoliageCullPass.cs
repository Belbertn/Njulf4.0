using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class FoliageCullPass : IDisposable
    {
        private const uint WorkgroupSize = 64;
        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly BufferManager _bufferManager;
        private readonly FoliageManager _foliageManager;
        private readonly FoliagePipeline _pipeline;

        public FoliageCullPass(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            BufferManager bufferManager,
            FoliageManager foliageManager,
            FoliagePipeline pipeline)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _foliageManager = foliageManager ?? throw new ArgumentNullException(nameof(foliageManager));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        }

        public void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (!sceneData.DepthPrePassEnabled || sceneData.FoliageClusterCount <= 0)
                return;

            FoliageRuntimeBuffers buffers = _foliageManager.GetBuffers(frameIndex);
            if (!buffers.ClusterBuffer.IsValid ||
                !buffers.VisibleClusterBuffer.IsValid ||
                !buffers.MeshletDrawBuffer.IsValid ||
                !buffers.IndirectDispatchBuffer.IsValid ||
                !buffers.CounterBuffer.IsValid)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            ResetOutputs(commandBuffer, buffers);

            _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline.CullPipeline);

            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipeline.ComputeLayout,
                0,
                1,
                &storageSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipeline.ComputeLayout,
                1,
                1,
                &textureSet,
                0,
                null);

            var pushConstants = new GPUFoliageCullPushConstants
            {
                CameraPositionMaxDistance = new Vector4(
                    sceneData.CameraPosition.X,
                    sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z,
                    1.0e20f),
                CurrentFrameIndex = (uint)frameIndex,
                ClusterCount = checked((uint)sceneData.FoliageClusterCount),
                VisibleClusterCapacity = checked((uint)Math.Max(0, buffers.VisibleClusterCapacity)),
                MeshletDrawCapacity = checked((uint)Math.Max(0, buffers.MeshletDrawCapacity)),
                IndirectDispatchBufferBaseIndex = (uint)BindlessIndex.FoliageIndirectDispatchBufferBase,
                Flags = sceneData.OcclusionCullingEnabled ? 1u : 0u,
                AuthoredMeshletWorkItemCount = checked((uint)Math.Max(0, buffers.AuthoredMeshletWorkItemCount)),
                FirstAuthoredClusterIndex = buffers.FirstAuthoredClusterIndex == uint.MaxValue ? 0u : buffers.FirstAuthoredClusterIndex,
                AuthoredClusterCount = checked((uint)Math.Max(0, buffers.AuthoredClusterCount))
            };

            _context.Api.CmdPushConstants(
                commandBuffer,
                _pipeline.ComputeLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUFoliageCullPushConstants>(),
                &pushConstants);

            uint invocationCount = Math.Max(pushConstants.ClusterCount, pushConstants.AuthoredMeshletWorkItemCount);
            uint groupCountX = Math.Max(1u, (invocationCount + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(commandBuffer, groupCountX, 1, 1);
            RecordCullOutputBarrier(commandBuffer, buffers);
            _foliageManager.RecordCounterReadback(commandBuffer, frameIndex);
        }

        private void ResetOutputs(CommandBuffer commandBuffer, FoliageRuntimeBuffers buffers)
        {
            VkBuffer counter = _bufferManager.GetBuffer(buffers.CounterBuffer);
            VkBuffer visible = _bufferManager.GetBuffer(buffers.VisibleClusterBuffer);
            VkBuffer meshletDraw = _bufferManager.GetBuffer(buffers.MeshletDrawBuffer);
            VkBuffer indirect = _bufferManager.GetBuffer(buffers.IndirectDispatchBuffer);

            _context.Api.CmdFillBuffer(commandBuffer, counter, 0, FoliageManager.CounterStride, 0u);
            _context.Api.CmdFillBuffer(commandBuffer, visible, 0, buffers.VisibleClusterBufferSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(commandBuffer, meshletDraw, 0, buffers.MeshletDrawBufferSize, 0xffffffffu);
            _context.Api.CmdFillBuffer(commandBuffer, indirect, 0, buffers.IndirectDispatchBufferSize, 0u);
            _context.Api.CmdFillBuffer(commandBuffer, indirect, 4, 4, 1u);
            _context.Api.CmdFillBuffer(commandBuffer, indirect, 8, 4, 1u);

            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[4];
            barriers[0] = BarrierBuilder.BufferBarrier(
                counter,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                FoliageManager.CounterStride);
            barriers[1] = BarrierBuilder.BufferBarrier(
                visible,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                buffers.VisibleClusterBufferSize);
            barriers[2] = BarrierBuilder.BufferBarrier(
                meshletDraw,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                buffers.MeshletDrawBufferSize);
            barriers[3] = BarrierBuilder.BufferBarrier(
                indirect,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                buffers.IndirectDispatchBufferSize);
            ExecuteBarriers(commandBuffer, barriers);
        }

        private void RecordCullOutputBarrier(CommandBuffer commandBuffer, FoliageRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[4];
            barriers[0] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffers.CounterBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.AllCommandsBit | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit,
                0,
                FoliageManager.CounterStride);
            barriers[1] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffers.VisibleClusterBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.ShaderStorageReadBit,
                0,
                buffers.VisibleClusterBufferSize);
            barriers[2] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffers.MeshletDrawBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.ShaderStorageReadBit,
                0,
                buffers.MeshletDrawBufferSize);
            barriers[3] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffers.IndirectDispatchBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.DrawIndirectBit,
                AccessFlags2.IndirectCommandReadBit,
                0,
                buffers.IndirectDispatchBufferSize);
            ExecuteBarriers(commandBuffer, barriers);
        }

        private void ExecuteBarriers(CommandBuffer commandBuffer, ReadOnlySpan<BufferMemoryBarrier2> barriers)
        {
            fixed (BufferMemoryBarrier2* pBarriers = barriers)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)barriers.Length,
                    PBufferMemoryBarriers = pBarriers
                };
                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
            }
        }

        public void Dispose()
        {
        }
    }
}
