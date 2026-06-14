using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SkinningPass : IDisposable
    {
        private const string EntryPoint = "main";
        private const uint WorkgroupSize = 64;

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly BufferManager _bufferManager;
        private readonly SkinningManager _skinningManager;
        private readonly nint _entryPointName;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private bool _disposed;

        public SkinningPass(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            BufferManager bufferManager,
            SkinningManager skinningManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _skinningManager = skinningManager ?? throw new ArgumentNullException(nameof(skinningManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUSkinningPushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
        }

        public void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
        {
            if (sceneData.SkinningDispatchCount <= 0)
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

            for (uint dispatchIndex = 0; dispatchIndex < sceneData.SkinningDispatchCount; dispatchIndex++)
            {
                var pushConstants = new GPUSkinningPushConstants
                {
                    DispatchIndex = dispatchIndex,
                    CurrentFrameIndex = (uint)frameIndex,
                    Padding0 = 0,
                    Padding1 = 0
                };

                _context.Api.CmdPushConstants(
                    commandBuffer,
                    _layout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<GPUSkinningPushConstants>(),
                    &pushConstants);

                GPUSkinningDispatch dispatch = sceneData.SkinningDispatches[(int)dispatchIndex];
                uint groupCountX = Math.Max(1u, (dispatch.VertexCount + WorkgroupSize - 1u) / WorkgroupSize);
                _context.Api.CmdDispatch(commandBuffer, groupCountX, 1, 1);
            }

            sceneData.CpuSkinningRecordMicroseconds = Stopwatch.GetElapsedTime(start).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);

            if (requiredSize > properties.Limits.MaxPushConstantsSize)
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but skinning requires {requiredSize} bytes.");
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
                throw new VulkanException("Failed to create skinning pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Skinning Compute Pipeline Cache");
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
                Size = (uint)Marshal.SizeOf<GPUSkinningPushConstants>()
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
                throw new VulkanException("Failed to create skinning pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "Skinning Compute Pipeline Layout");
        }

        private void CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "skinning.comp.spv");
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, "skinning.comp.spv");

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
                    throw new VulkanException("Failed to create skinning compute pipeline", result);
                _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline, "SkinningPass");
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
