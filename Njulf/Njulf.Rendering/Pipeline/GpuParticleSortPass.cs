using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class GpuParticleSortPass : IDisposable
    {
        private const string EntryPoint = "main";
        private const uint WorkgroupSize = 256;
        private const uint BlendBucketCount = 5;
        private const uint ModeBuildKeys = 0;
        private const uint ModeBitonic = 1;
        private const uint ModeReorder = 2;

        private static readonly uint[] SortedBuckets = { 0u, 1u, 4u };

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly BufferManager _bufferManager;
        private readonly GpuParticleRuntimeManager _runtimeManager;
        private readonly nint _entryPointName;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _disposed;

        public GpuParticleSortPass(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            BufferManager bufferManager,
            GpuParticleRuntimeManager runtimeManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUParticleSortPushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        public void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData.GpuParticlesEnabled == 0 || sceneData.GpuParticleEmitterCount <= 0)
                return;
            if (sceneData.GpuParticleCapacity <= 0 || !sceneData.GpuParticleIndirectDrawBuffer.IsValid)
                return;

            GpuParticleRuntimeBuffers buffers = _runtimeManager.GetBuffers(frameIndex);
            _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline);

            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _layout, 0, 1, &storageSet, 0, null);
            _context.Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _layout, 1, 1, &textureSet, 0, null);

            uint particleCapacity = checked((uint)sceneData.GpuParticleCapacity);
            Dispatch(commandBuffer, CreatePushConstants(frameIndex, particleCapacity, ModeBuildKeys), checked(particleCapacity * BlendBucketCount));
            RecordSortKeyBarrier(commandBuffer, buffers);

            foreach (uint bucket in SortedBuckets)
            {
                for (uint level = 2u; level <= particleCapacity; level <<= 1)
                {
                    for (uint stage = level >> 1; stage > 0u; stage >>= 1)
                    {
                        Dispatch(commandBuffer, CreatePushConstants(frameIndex, particleCapacity, ModeBitonic, bucket, level, stage), particleCapacity);
                        RecordSortKeyBarrier(commandBuffer, buffers);
                    }

                    if (level == particleCapacity)
                        break;
                }
            }

            Dispatch(commandBuffer, CreatePushConstants(frameIndex, particleCapacity, ModeReorder), checked(particleCapacity * BlendBucketCount));
            RecordFinalBarriers(commandBuffer, buffers);
        }

        private static GPUParticleSortPushConstants CreatePushConstants(
            int frameIndex,
            uint particleCapacity,
            uint mode,
            uint bucket = 0,
            uint sortLevel = 0,
            uint sortStage = 0)
        {
            return new GPUParticleSortPushConstants
            {
                CurrentFrameIndex = (uint)frameIndex,
                ParticleCapacity = particleCapacity,
                Mode = mode,
                Bucket = bucket,
                SortLevel = sortLevel,
                SortStage = sortStage,
                Padding0 = 0,
                Padding1 = 0
            };
        }

        private void Dispatch(CommandBuffer commandBuffer, GPUParticleSortPushConstants pushConstants, uint dispatchCount)
        {
            if (dispatchCount == 0)
                return;

            _context.Api.CmdPushConstants(
                commandBuffer,
                _layout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUParticleSortPushConstants>(),
                &pushConstants);

            uint groupCountX = Math.Max(1u, (dispatchCount + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(commandBuffer, groupCountX, 1, 1);
        }

        private void RecordSortKeyBarrier(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
            barriers[0] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffers.SortKeyBuffer),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            ExecuteBufferBarriers(commandBuffer, barriers);
        }

        private void RecordFinalBarriers(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[4];
            int count = 0;
            AddBarrier(barriers, ref count, buffers.RenderInstanceBuffer, PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderStorageReadBit);
            AddBarrier(barriers, ref count, buffers.IndirectDrawBuffer, PipelineStageFlags2.DrawIndirectBit, AccessFlags2.IndirectCommandReadBit);
            AddBarrier(barriers, ref count, buffers.CounterBuffer, PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit);
            AddBarrier(barriers, ref count, buffers.SortKeyBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);

            if (count > 0)
                ExecuteBufferBarriers(commandBuffer, barriers[..count]);
        }

        private void AddBarrier(
            Span<BufferMemoryBarrier2> barriers,
            ref int count,
            BufferHandle handle,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            if (!handle.IsValid)
                return;

            barriers[count++] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                destinationStage,
                destinationAccess);
        }

        private void ExecuteBufferBarriers(CommandBuffer commandBuffer, ReadOnlySpan<BufferMemoryBarrier2> barriers)
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

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
                throw new VulkanException($"GPU particle sorting requires {requiredSize} bytes of push constants but GPU supports {properties.Limits.MaxPushConstantsSize}.");
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create GPU particle sort pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "GPU Particle Sort Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUParticleSortPushConstants>()
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create GPU particle sort pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "GPU Particle Sort Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "particle_sort.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "particle_sort.comp.spv");

                var shaderStageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = shaderStageInfo,
                    Layout = _layout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                Result result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out _pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create GPU particle sort compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "GpuParticleSortPass");
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_pipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }
    }
}
