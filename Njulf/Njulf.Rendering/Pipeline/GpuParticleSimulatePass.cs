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
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class GpuParticleSimulatePass : IDisposable
    {
        private const string EntryPoint = "main";
        private const uint WorkgroupSize = 256;
        private const uint BlendBucketCount = 5;
        private const uint ModeUpdate = 0;
        private const uint ModeSpawn = 1;
        private const uint ModePrefix = 2;
        private const uint ModeEmit = 3;

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly BufferManager _bufferManager;
        private readonly GpuParticleRuntimeManager _runtimeManager;
        private readonly nint _entryPointName;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _disposed;

        public GpuParticleSimulatePass(
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

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUParticleSimulatePushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        public void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData.GpuParticlesEnabled == 0 || sceneData.GpuParticleEmitterCount <= 0)
                return;
            if (sceneData.GpuParticleCapacity <= 0 || sceneData.GpuParticleMaxSpawnPerEmitter <= 0)
                return;

            long start = Stopwatch.GetTimestamp();
            GpuParticleRuntimeBuffers buffers = _runtimeManager.GetBuffers(frameIndex);
            ResetFrameRenderCounters(commandBuffer, buffers);

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

            var pushConstants = CreatePushConstants(frameIndex, sceneData);

            DispatchMode(commandBuffer, pushConstants, ModeUpdate, pushConstants.ParticleCapacity);
            RecordComputeToComputeBarriers(commandBuffer, buffers);

            DispatchMode(
                commandBuffer,
                pushConstants,
                ModeSpawn,
                checked(pushConstants.EmitterCount * pushConstants.MaxSpawnPerEmitter));
            RecordComputeToComputeBarriers(commandBuffer, buffers);

            DispatchMode(commandBuffer, pushConstants, ModePrefix, 1u);
            RecordPrefixBarriers(commandBuffer, buffers);

            DispatchMode(commandBuffer, pushConstants, ModeEmit, pushConstants.ParticleCapacity);
            RecordSimulationBarriers(commandBuffer, buffers);
            sceneData.CpuGpuParticleSimulateRecordMicroseconds = Stopwatch.GetElapsedTime(start).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private GPUParticleSimulatePushConstants CreatePushConstants(int frameIndex, SceneRenderingData sceneData)
        {
            return new GPUParticleSimulatePushConstants
            {
                CurrentFrameIndex = (uint)frameIndex,
                ParticleCapacity = checked((uint)sceneData.GpuParticleCapacity),
                EmitterCount = checked((uint)sceneData.GpuParticleEmitterCount),
                MaxSpawnPerEmitter = checked((uint)sceneData.GpuParticleMaxSpawnPerEmitter),
                DeltaSeconds = sceneData.GpuParticleDeltaSeconds,
                TimeSeconds = sceneData.GpuParticleTimeSeconds,
                SoftParticleDistance = _runtimeManager.SoftParticleDistanceForFrame,
                Flags = 0,
                Padding0 = 0,
                Padding1 = 0,
                Padding2 = 0,
                Padding3 = 0
            };
        }

        private void DispatchMode(
            CommandBuffer commandBuffer,
            GPUParticleSimulatePushConstants pushConstants,
            uint mode,
            uint dispatchCount)
        {
            if (dispatchCount == 0)
                return;

            pushConstants.Flags = mode;
            _context.Api.CmdPushConstants(
                commandBuffer,
                _layout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUParticleSimulatePushConstants>(),
                &pushConstants);

            uint groupCountX = Math.Max(1u, (dispatchCount + WorkgroupSize - 1u) / WorkgroupSize);
            _context.Api.CmdDispatch(commandBuffer, groupCountX, 1, 1);
        }

        private void ResetFrameRenderCounters(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            VkBuffer counterBuffer = _bufferManager.GetBuffer(buffers.CounterBuffer);
            VkBuffer indirectBuffer = _bufferManager.GetBuffer(buffers.IndirectDrawBuffer);

            _context.Api.CmdFillBuffer(commandBuffer, counterBuffer, 5u * sizeof(uint), sizeof(uint), 0);
            _context.Api.CmdFillBuffer(commandBuffer, counterBuffer, 7u * sizeof(uint), BlendBucketCount * 3u * sizeof(uint), 0);
            for (uint bucket = 0; bucket < BlendBucketCount; bucket++)
            {
                ulong instanceCountOffset = bucket * (ulong)Marshal.SizeOf<GPUParticleDrawCommand>() + sizeof(uint);
                _context.Api.CmdFillBuffer(commandBuffer, indirectBuffer, instanceCountOffset, sizeof(uint), 0);
            }

            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
            barriers[0] = BarrierBuilder.BufferBarrier(
                counterBuffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            barriers[1] = BarrierBuilder.BufferBarrier(
                indirectBuffer,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);

            ExecuteBufferBarriers(commandBuffer, barriers);
        }

        private void RecordSimulationBarriers(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[6];
            int count = 0;
            AddBarrier(barriers, ref count, buffers.StateBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.AliveIndexBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.DeadIndexBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.CounterBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.UnsortedRenderInstanceBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);
            AddBarrier(barriers, ref count, buffers.IndirectDrawBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);

            if (count > 0)
                ExecuteBufferBarriers(commandBuffer, barriers[..count]);
        }

        private void RecordComputeToComputeBarriers(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[4];
            int count = 0;
            AddBarrier(barriers, ref count, buffers.StateBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.AliveIndexBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.DeadIndexBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.CounterBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

            if (count > 0)
                ExecuteBufferBarriers(commandBuffer, barriers[..count]);
        }

        private void RecordPrefixBarriers(CommandBuffer commandBuffer, GpuParticleRuntimeBuffers buffers)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
            int count = 0;
            AddBarrier(barriers, ref count, buffers.CounterBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
            AddBarrier(barriers, ref count, buffers.IndirectDrawBuffer, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

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
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but GPU particle simulation requires {requiredSize} bytes.");
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
                throw new VulkanException("Failed to create GPU particle simulation pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "GPU Particle Simulation Pipeline Cache");
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
                Size = (uint)Marshal.SizeOf<GPUParticleSimulatePushConstants>()
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
                throw new VulkanException("Failed to create GPU particle simulation pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "GPU Particle Simulation Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "particle_simulate.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "particle_simulate.comp.spv");

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
                    throw new VulkanException("Failed to create GPU particle simulation compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "GpuParticleSimulatePass");
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
