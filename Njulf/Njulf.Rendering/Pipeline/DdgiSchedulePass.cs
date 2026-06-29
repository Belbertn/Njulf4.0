using System;
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
    public sealed unsafe class DdgiSchedulePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint WorkgroupSize = 64;

        private readonly RenderSettings _settings;
        private readonly DdgiProbeVolumeManager _probeVolumeManager;
        private readonly AccelerationStructureManager _accelerationStructureManager;
        private readonly SchedulePipeline[] _pipelines =
        [
            new("ddgi_schedule_reset.comp.spv", "DDGI Schedule Reset Compute Pipeline"),
            new("ddgi_schedule_score.comp.spv", "DDGI Schedule Score Compute Pipeline"),
            new("ddgi_schedule_prefix.comp.spv", "DDGI Schedule Prefix Compute Pipeline"),
            new("ddgi_schedule_compact.comp.spv", "DDGI Schedule Compact Compute Pipeline"),
            new("ddgi_schedule_finalize.comp.spv", "DDGI Schedule Finalize Compute Pipeline")
        ];
        private nint _entryPointName;
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private string _initializationFailureReason = string.Empty;

        public DdgiSchedulePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            DdgiProbeVolumeManager probeVolumeManager,
            AccelerationStructureManager accelerationStructureManager)
            : base("DdgiSchedulePass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeVolumeManager = probeVolumeManager ?? throw new ArgumentNullException(nameof(probeVolumeManager));
            _accelerationStructureManager = accelerationStructureManager ?? throw new ArgumentNullException(nameof(accelerationStructureManager));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute => true;
        public override string AsyncComputeReason => "DDGI GPU scheduling is compute-only and writes scheduler buffers before DDGI trace.";
        public bool IsAvailable => string.IsNullOrEmpty(_initializationFailureReason) && AllPipelinesReady();
        public string InitializationFailureReason => _initializationFailureReason;

        public override void Initialize()
        {
            try
            {
                CreatePipelineCache();
                CreatePipelineLayout();
                for (int i = 0; i < _pipelines.Length; i++)
                    _pipelines[i].Pipeline = CreatePipeline(_pipelines[i]);
            }
            catch (Exception ex)
            {
                _initializationFailureReason = $"schedule-pipeline-unavailable:{ex.GetType().Name}";
                System.Diagnostics.Debug.WriteLine($"DDGI GPU scheduler fallback: {_initializationFailureReason}");
                Cleanup();
            }
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            return IsAvailable &&
                   sceneData.DdgiGpuSchedulerFallbackActive == 0 &&
                   sceneData.DdgiGpuSchedulerConsideredProbeCount > 0 &&
                   (gi.DdgiSchedulerMode == DdgiSchedulerMode.Gpu ||
                    (gi.DdgiSchedulerMode == DdgiSchedulerMode.CpuGpuCompare && gi.DdgiCompareModeUseGpuQueueForRendering)) &&
                   gi.Enabled &&
                   gi.EffectiveUseDdgi &&
                   gi.EffectiveUseRayQueryBackend &&
                   _accelerationStructureManager.Active &&
                   sceneData.DdgiProbeVolumeCount > 0 &&
                   sceneData.DdgiProbesUpdated > 0 &&
                   _probeVolumeManager.GpuSchedulerBufferBytes > 0UL;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            DispatchPipeline(cmd, _pipelines[0], CalculateResetGroupCount());
            InsertScheduleStageBarrier(cmd);
            DispatchPipeline(cmd, _pipelines[1], CalculateProbeGroupCount(sceneData));
            InsertScheduleStageBarrier(cmd);
            DispatchPipeline(cmd, _pipelines[2], 1);
            InsertScheduleStageBarrier(cmd);
            DispatchPipeline(cmd, _pipelines[3], CalculateProbeGroupCount(sceneData));
            InsertScheduleStageBarrier(cmd);
            DispatchPipeline(cmd, _pipelines[4], 1);
            _probeVolumeManager.RecordGpuSchedulerCounterReadback(cmd, frameIndex);
            InsertScheduleToTraceBarrier(cmd);
        }

        public override void Cleanup()
        {
            for (int i = 0; i < _pipelines.Length; i++)
            {
                if (_pipelines[i].Pipeline.Handle != 0)
                {
                    _context.Api.DestroyPipeline(_context.Device, _pipelines[i].Pipeline, null);
                    _pipelines[i].Pipeline = default;
                }
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
            {
                SilkMarshal.Free(_entryPointName);
                _entryPointName = 0;
            }
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create DDGI schedule pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "DDGI Schedule Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];
            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = setLayouts
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create DDGI schedule pipeline layout", result);
                _context.SetDebugName(_pipelineLayout.Handle, ObjectType.PipelineLayout, "DDGI Schedule Pipeline Layout");
            }
        }

        private VkPipeline CreatePipeline(SchedulePipeline schedulePipeline)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, schedulePipeline.ShaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, schedulePipeline.ShaderName);

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
                    throw new VulkanException($"Failed to create {schedulePipeline.ShaderName} compute pipeline", result);
                _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, schedulePipeline.DebugName);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void DispatchPipeline(CommandBuffer cmd, SchedulePipeline schedulePipeline, uint groupCountX)
        {
            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, schedulePipeline.Pipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            _context.Api.CmdDispatch(cmd, Math.Max(1u, groupCountX), 1, 1);
        }

        private uint CalculateResetGroupCount()
        {
            int resetWordCount = Math.Max(
                Math.Max(16, 3),
                Math.Max(
                    _probeVolumeManager.GpuSchedulerGroupCountCapacity,
                    _probeVolumeManager.GpuSchedulerPrefixCapacity));
            return DivRoundUp(checked((uint)Math.Max(1, resetWordCount)), WorkgroupSize);
        }

        private static uint CalculateProbeGroupCount(SceneRenderingData sceneData)
        {
            uint activeProbeCount = checked((uint)Math.Max(0, sceneData.DdgiActiveProbeCount));
            return DivRoundUp(Math.Max(1u, activeProbeCount), WorkgroupSize);
        }

        private static uint DivRoundUp(uint value, uint divisor)
        {
            return (value + divisor - 1u) / divisor;
        }

        private bool AllPipelinesReady()
        {
            for (int i = 0; i < _pipelines.Length; i++)
            {
                if (_pipelines[i].Pipeline.Handle == 0)
                    return false;
            }

            return true;
        }

        private void InsertScheduleStageBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void InsertScheduleToTraceBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.IndirectCommandReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private sealed class SchedulePipeline
        {
            public SchedulePipeline(string shaderName, string debugName)
            {
                ShaderName = shaderName;
                DebugName = debugName;
            }

            public string ShaderName { get; }
            public string DebugName { get; }
            public VkPipeline Pipeline { get; set; }
        }
    }
}
