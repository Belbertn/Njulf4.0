using System;
using System.Diagnostics;
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
    public sealed unsafe class GpuParticleResetPass : IDisposable
    {
        private const string EntryPoint = "main";
        private const uint WorkgroupSize = 256;

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly BufferManager _bufferManager;
        private readonly GpuParticleRuntimeManager _runtimeManager;
        private readonly nint _entryPointName;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _disposed;

        public GpuParticleResetPass(
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

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUParticleResetPushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        public void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData.GpuParticlesEnabled == 0 || sceneData.GpuParticleResetRequired == 0)
                return;
            if (sceneData.GpuParticleCapacity <= 0 || sceneData.GpuParticleDrawCapacity <= 0)
                return;

            long start = Stopwatch.GetTimestamp();
            _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline);

            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;

            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _layout,
                0,
                1,
                &storageSet,
                0,
                null);

            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _layout,
                1,
                1,
                &textureSet,
                0,
                null);

            var pushConstants = new GPUParticleResetPushConstants
            {
                CurrentFrameIndex = (uint)frameIndex,
                ParticleCapacity = checked((uint)sceneData.GpuParticleCapacity),
                DrawCapacity = checked((uint)sceneData.GpuParticleDrawCapacity),
                Flags = 0,
                Padding0 = 0,
                Padding1 = 0,
                Padding2 = 0,
                Padding3 = 0
            };

            _context.Api.CmdPushConstants(
                commandBuffer,
                _layout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUParticleResetPushConstants>(),
                &pushConstants);

            uint dispatchCount = Math.Max(checked(pushConstants.ParticleCapacity * 5u), pushConstants.DrawCapacity);
            uint groupCountX = Math.Max(1u, (dispatchCount + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(commandBuffer, groupCountX, 1, 1);

            RecordResetBarriers(commandBuffer, frameIndex);
            _runtimeManager.MarkResetRecorded();
            sceneData.GpuParticleResetRequired = 0;
            sceneData.CpuGpuParticleResetRecordMicroseconds = Stopwatch.GetElapsedTime(start).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private void RecordResetBarriers(CommandBuffer commandBuffer, int frameIndex)
        {
            GpuParticleRuntimeBuffers buffers = _runtimeManager.GetBuffers(frameIndex);
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[8];
            int count = 0;

            AddBarrier(barriers, ref count, buffers.StateBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.AliveIndexBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.DeadIndexBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.CounterBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.UnsortedRenderInstanceBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.RenderInstanceBuffer, AccessFlags2.ShaderStorageReadBit);
            AddBarrier(barriers, ref count, buffers.IndirectDrawBuffer, AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderStorageReadBit);
            AddBarrier(barriers, ref count, buffers.SortKeyBuffer, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

            if (count == 0)
                return;

            fixed (BufferMemoryBarrier2* pBarriers = barriers)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)count,
                    PBufferMemoryBarriers = pBarriers
                };

                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
            }
        }

        private void AddBarrier(
            Span<BufferMemoryBarrier2> barriers,
            ref int count,
            BufferHandle handle,
            AccessFlags2 destinationAccess)
        {
            if (!handle.IsValid)
                return;

            barriers[count++] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.DrawIndirectBit,
                destinationAccess);
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);

            if (requiredSize > properties.Limits.MaxPushConstantsSize)
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but GPU particle reset requires {requiredSize} bytes.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create GPU particle reset pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "GPU Particle Reset Pipeline Cache");
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
                Size = (uint)Marshal.SizeOf<GPUParticleResetPushConstants>()
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
                throw new VulkanException("Failed to create GPU particle reset pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "GPU Particle Reset Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "particle_reset.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "particle_reset.comp.spv");

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

                Result result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out _pipeline);

                if (result != Result.Success)
                    throw new VulkanException("Failed to create GPU particle reset compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "GpuParticleResetPass");
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
