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
        private const uint JumpFloodModeSeed = 0;
        private const uint JumpFloodModePropagate = 1;
        private const uint JumpFloodModePublish = 2;
        private const uint DetailedDiagnosticsFlag = 1u << 0;

        private readonly RenderSettings _settings;
        private readonly FarFieldClipmapManager _manager;
        private readonly nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private VkPipeline _jumpFloodPipeline;

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
            _jumpFloodPipeline = CreateJumpFloodPipeline();
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return _pipeline.Handle != 0 &&
                   _jumpFloodPipeline.Handle != 0 &&
                   _settings.GlobalIllumination.FarFieldClipmapEnabled &&
                   _settings.GlobalIllumination.EffectiveUseSimpleDdgi &&
                   _manager.BakePending;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!_manager.ConsumeBakePending())
                return;

            if (_manager.PagedMode)
            {
                try
                {
                    for (int pageIndex = 0; pageIndex < _manager.PageBakeCount; pageIndex++)
                    {
                        FarFieldPageBakeWork work = _manager.GetPageBakeWork(pageIndex);
                        ExecuteBake(cmd, sceneData, work.Request, work.InstanceIndices, work.InstanceCount);
                        _manager.MarkPageBakePublished(work.Request);
                    }
                }
                catch
                {
                    for (int pageIndex = 0; pageIndex < _manager.PageBakeCount; pageIndex++)
                        _manager.MarkPageBakeFailed(_manager.GetPageBakeWork(pageIndex).Request);
                    throw;
                }
                finally
                {
                    _manager.CompletePagedBakeBatch();
                }

                return;
            }

            ExecuteBake(cmd, sceneData, request: null, instanceIndices: null, instanceIndexCount: 0);
            _manager.MarkBakePublished();
        }

        private void ExecuteBake(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            FarFieldPageBakeRequest? request,
            int[]? instanceIndices,
            int instanceIndexCount)
        {
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            int resolution = Math.Max(1, _manager.Resolution);
            uint bakeVoxelBufferIndex = checked((uint)_manager.BakeVoxelBufferIndex);
            uint voxelCount = checked((uint)resolution * (uint)resolution * (uint)resolution);
            uint voxelGroups = Math.Max(1u, (voxelCount + 63u) / 64u);
            uint pageVoxelOffset = request.HasValue ? _manager.GetPageVoxelOffset(request.Value) : 0u;
            uint pageDistanceWordOffset = request.HasValue ? _manager.GetPageDistanceWordOffset(request.Value) : 0u;
            uint pageTableEntryIndex = request.HasValue ? checked((uint)request.Value.GpuTableEntryIndex) : 0u;
            uint pageGeneration = request?.Generation ?? 0u;
            uint pageTableBufferIndex = checked((uint)_manager.PageTableBufferIndex);

            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = bakeVoxelBufferIndex,
                InstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
                Mode = VoxelizeModeClear,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PageVoxelOffset = pageVoxelOffset,
                PageDistanceWordOffset = pageDistanceWordOffset,
                PageTableBufferIndex = pageTableBufferIndex,
                PageTableEntryIndex = pageTableEntryIndex,
                PageGeneration = pageGeneration
            });
            _context.Api.CmdDispatch(cmd, voxelGroups, 1, 1);
            InsertComputeBarrier(cmd);

            int instanceCount = instanceIndices == null
                ? _manager.InstanceCount
                : Math.Min(Math.Max(instanceIndexCount, 0), instanceIndices.Length);
            for (int bakeInstanceIndex = 0; bakeInstanceIndex < instanceCount; bakeInstanceIndex++)
            {
                int instanceIndex = instanceIndices?[bakeInstanceIndex] ?? bakeInstanceIndex;
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
                    CurrentFrameIndex = sceneData.CurrentFrameIndex,
                    PageVoxelOffset = pageVoxelOffset,
                    PageDistanceWordOffset = pageDistanceWordOffset,
                    PageTableBufferIndex = pageTableBufferIndex,
                    PageTableEntryIndex = pageTableEntryIndex,
                    PageGeneration = pageGeneration
                });
                _context.Api.CmdDispatch(cmd, Math.Max(1u, (triangleCount + 63u) / 64u), 1, 1);
            }

            InsertComputeBarrier(cmd);
            BuildJumpFloodDistanceField(
                cmd,
                sceneData,
                resolution,
                bakeVoxelBufferIndex,
                voxelGroups,
                pageVoxelOffset,
                pageDistanceWordOffset,
                pageTableBufferIndex,
                pageTableEntryIndex,
                pageGeneration);

            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = bakeVoxelBufferIndex,
                Mode = VoxelizeModePublish,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PageVoxelOffset = pageVoxelOffset,
                PageDistanceWordOffset = pageDistanceWordOffset,
                PageTableBufferIndex = pageTableBufferIndex,
                PageTableEntryIndex = pageTableEntryIndex,
                PageGeneration = pageGeneration
            });
            _context.Api.CmdDispatch(cmd, 1, 1, 1);
            InsertComputeBarrier(cmd);
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

            if (_jumpFloodPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _jumpFloodPipeline, null);
                _jumpFloodPipeline = default;
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
            pushConstants.DiagnosticFlags = ShouldCollectDetailedDiagnostics()
                ? DetailedDiagnosticsFlag
                : 0u;
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUFarFieldVoxelizePushConstants>(),
                &pushConstants);
        }

        private bool ShouldCollectDetailedDiagnostics()
        {
            return _settings.Diagnostics.DdgiForwardEstimateCountersEnabled ||
                   _settings.GlobalIllumination.DebugView != GlobalIlluminationDebugView.None;
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

        private VkPipeline CreateJumpFloodPipeline()
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, "farfield_jumpflood.comp.spv");
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
                    throw new VulkanException("Failed to create far-field jump-flood compute pipeline", result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void BuildJumpFloodDistanceField(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int resolution,
            uint voxelBufferIndex,
            uint voxelGroups,
            uint pageVoxelOffset,
            uint pageDistanceWordOffset,
            uint pageTableBufferIndex,
            uint pageTableEntryIndex,
            uint pageGeneration)
        {
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _jumpFloodPipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);

            uint scratch0 = checked((uint)_manager.JumpFloodScratch0BufferIndex);
            uint scratch1 = checked((uint)_manager.JumpFloodScratch1BufferIndex);
            uint distanceBuffer = checked((uint)_manager.DistanceBufferIndex);

            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = voxelBufferIndex,
                InstanceBufferIndex = scratch0,
                InstanceIndex = scratch1,
                Mode = JumpFloodModeSeed,
                MaterialTextureMaxCascade = distanceBuffer,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PageVoxelOffset = pageVoxelOffset,
                PageDistanceWordOffset = pageDistanceWordOffset,
                PageTableBufferIndex = pageTableBufferIndex,
                PageTableEntryIndex = pageTableEntryIndex,
                PageGeneration = pageGeneration
            });
            _context.Api.CmdDispatch(cmd, voxelGroups, 1, 1);
            InsertComputeBarrier(cmd);

            uint source = scratch0;
            uint dest = scratch1;
            for (uint stride = HighestPowerOfTwoLessThanOrEqualTo((uint)Math.Max(1, resolution)) >> 1; stride >= 1; stride >>= 1)
            {
                Push(cmd, new GPUFarFieldVoxelizePushConstants
                {
                    ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                    VoxelBufferIndex = voxelBufferIndex,
                    InstanceBufferIndex = source,
                    InstanceIndex = dest,
                    Mode = JumpFloodModePropagate,
                    TriangleCount = stride,
                    MaterialTextureMaxCascade = distanceBuffer,
                    CurrentFrameIndex = sceneData.CurrentFrameIndex,
                    PageVoxelOffset = pageVoxelOffset,
                    PageDistanceWordOffset = pageDistanceWordOffset,
                    PageTableBufferIndex = pageTableBufferIndex,
                    PageTableEntryIndex = pageTableEntryIndex,
                    PageGeneration = pageGeneration
                });
                _context.Api.CmdDispatch(cmd, voxelGroups, 1, 1);
                InsertComputeBarrier(cmd);
                (source, dest) = (dest, source);
            }

            uint packedDistanceGroups = checked((uint)Math.Max(1, (((resolution * resolution * resolution) + 1) / 2 + 63) / 64));
            Push(cmd, new GPUFarFieldVoxelizePushConstants
            {
                ParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                VoxelBufferIndex = voxelBufferIndex,
                InstanceBufferIndex = source,
                InstanceIndex = dest,
                Mode = JumpFloodModePublish,
                MaterialTextureMaxCascade = distanceBuffer,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                PageVoxelOffset = pageVoxelOffset,
                PageDistanceWordOffset = pageDistanceWordOffset,
                PageTableBufferIndex = pageTableBufferIndex,
                PageTableEntryIndex = pageTableEntryIndex,
                PageGeneration = pageGeneration
            });
            _context.Api.CmdDispatch(cmd, packedDistanceGroups, 1, 1);
            InsertComputeBarrier(cmd);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        }

        private static uint HighestPowerOfTwoLessThanOrEqualTo(uint value)
        {
            uint result = 1;
            while ((result << 1) <= value)
                result <<= 1;
            return result;
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
