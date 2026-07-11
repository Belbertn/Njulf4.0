using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class FarFieldClipmapBakePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint VoxelizeModeClear = 0;
        private const uint VoxelizeModeTriangles = 1;
        private const uint VoxelizeModePublish = 2;

        private readonly RenderSettings _settings;
        private readonly FarFieldClipmapManager _manager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;

        public FarFieldClipmapBakePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            FarFieldClipmapManager manager)
            : base("FarFieldClipmapBakePass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "Far-field voxel baking is compute-only load-time work.";

        public override void Initialize()
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return _pipeline.Handle != 0 &&
                   _settings.GlobalIllumination.FarFieldClipmapEnabled &&
                   _settings.GlobalIllumination.EffectiveUseSimpleDdgi &&
                   _manager.BakePending;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!_manager.ConsumeBakePending())
                return;

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            int resolution = Math.Max(1, _manager.Resolution);
            uint bakeVoxelBufferIndex = checked((uint)_manager.BakeVoxelBufferIndex);
            uint voxelGroups = checked((uint)Math.Max(1, ((resolution * resolution * resolution) + 63) / 64));
            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = bakeVoxelBufferIndex,
                InstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
                Mode = VoxelizeModeClear,
                CurrentFrameIndex = sceneData.CurrentFrameIndex
            });
            _context.Api.CmdDispatch(cmd, voxelGroups, 1, 1);
            InsertComputeBarrier(cmd);

            for (int instanceIndex = 0; instanceIndex < _manager.InstanceCount; instanceIndex++)
            {
                uint triangleCount = _manager.GetTriangleCount(instanceIndex);
                if (triangleCount == 0)
                    continue;
                Push(cmd, new GPUFarFieldVoxelizePushConstants
                {
                    ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                    VoxelBufferIndex = bakeVoxelBufferIndex,
                    InstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
                    InstanceIndex = checked((uint)instanceIndex),
                    Mode = VoxelizeModeTriangles,
                    TriangleCount = triangleCount,
                    MaterialTextureMaxCascade = _settings.GlobalIllumination.DdgiMaterialTextureMaxCascade < 0
                        ? GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount
                        : checked((uint)Math.Clamp(_settings.GlobalIllumination.DdgiMaterialTextureMaxCascade, 0, GlobalIlluminationSettings.MaxDdgiClipmapCascadeCount - 1)),
                    CurrentFrameIndex = sceneData.CurrentFrameIndex
                });
                _context.Api.CmdDispatch(cmd, Math.Max(1u, (triangleCount + 63u) / 64u), 1, 1);
            }

            InsertComputeBarrier(cmd);
            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = bakeVoxelBufferIndex,
                Mode = VoxelizeModePublish,
                CurrentFrameIndex = sceneData.CurrentFrameIndex
            });
            _context.Api.CmdDispatch(cmd, 1, 1, 1);
            InsertComputeBarrier(cmd);
            _manager.MarkBakePublished();
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            if (_pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
                _pipeline = default;
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        private void Push(CommandBuffer cmd, GPUFarFieldVoxelizePushConstants pushConstants)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUFarFieldVoxelizePushConstants>(),
                &pushConstants);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create far-field bake pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];

            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUFarFieldVoxelizePushConstants>()
                };

                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create far-field bake pipeline layout", result);
            }
        }

        private VkPipeline CreatePipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "farfield_voxelize.comp.spv");
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                    BasePipelineIndex = -1
                };

                Result result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create far-field bake compute pipeline", result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void InsertComputeBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }
    }
}
