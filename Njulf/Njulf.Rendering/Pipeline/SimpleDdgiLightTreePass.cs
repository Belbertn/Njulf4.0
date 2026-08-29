using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// GPU-resident local-light hierarchy transaction. Build stages target inactive
/// storage and finalize publishes validity last, so DDGI trace never observes a
/// partially built hierarchy.
/// </summary>
public sealed unsafe class SimpleDdgiLightTreePass : RenderPassBase
{
    private static readonly string[] ShaderNames =
    [
        "ddgi_light_bounds.comp.spv",
        "ddgi_light_sort.comp.spv",
        "ddgi_light_tree_build.comp.spv",
        "ddgi_light_tree_refit.comp.spv",
        "ddgi_light_tree_finalize.comp.spv"
    ];

    private readonly SimpleDdgiLightTreeGpuResources _resources;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private nint _entryPointName;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiLightTreePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        SimpleDdgiLightTreeGpuResources resources,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiLightTreePass", context, swapchain, bindlessHeap)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => false;
    public override string AsyncComputeReason =>
        "Light-tree publication shares the graphics-queue DDGI trace descriptor transaction.";

    public override void Initialize()
    {
        try
        {
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            for (int index = 0; index < _pipelines.Length; index++)
                _pipelines[index] = CreatePipeline(ShaderNames[index]);
        }
        catch
        {
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        (_resources.StateNeedsInitialization || _resources.HasPendingGpuWork) &&
        (_resources.PendingAction == DdgiLightTreeBuildAction.PublishEmpty ||
         _resources.StateNeedsInitialization ||
         _pipelines[0].Handle != 0);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData) =>
        ExecuteStages(cmd, frameIndex, timestamps: null);

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData,
        GpuTimestampRecorder? timestamps) =>
        ExecuteStages(cmd, frameIndex, timestamps);

    private void ExecuteStages(
        CommandBuffer cmd,
        int frameIndex,
        GpuTimestampRecorder? timestamps)
    {
        DdgiLightTreeBuildAction action = _resources.PendingAction;
        if (_resources.StateNeedsInitialization)
        {
            _context.Api.CmdFillBuffer(
                cmd,
                _resources.GetStateVkBuffer(),
                0,
                128,
                0);
            InsertTransferToTraceBarrier(cmd);
            _resources.MarkStateInitialized();
            // A missing pipeline is a supported correctness fallback. Leaving
            // validity clear makes trace evaluate the exact local-light set.
            if (_pipelines[0].Handle == 0 &&
                action != DdgiLightTreeBuildAction.PublishEmpty)
            {
                return;
            }
        }
        if (action == DdgiLightTreeBuildAction.PublishEmpty)
        {
            timestamps?.BeginPass(cmd, frameIndex, "SimpleDdgiLightTree.PublishEmpty");
            try
            {
                _context.Api.CmdFillBuffer(
                    cmd,
                    _resources.GetStateVkBuffer(),
                    0,
                    128,
                    0);
                InsertTransferToTraceBarrier(cmd);
                _resources.MarkRecorded();
            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
            }
            return;
        }

        GPUDdgiLightTreePushConstants push = _resources.BuildPushConstants();
        DispatchStage(cmd, frameIndex, timestamps, 0, "SimpleDdgiLightTree.Bounds", push);
        InsertComputeStorageBarrier(cmd);
        if (action == DdgiLightTreeBuildAction.BuildInactive)
        {
            DispatchStage(cmd, frameIndex, timestamps, 1, "SimpleDdgiLightTree.Sort", push);
            InsertComputeStorageBarrier(cmd);
            DispatchStage(cmd, frameIndex, timestamps, 2, "SimpleDdgiLightTree.Build", push);
        }
        else
        {
            DispatchStage(cmd, frameIndex, timestamps, 3, "SimpleDdgiLightTree.Refit", push);
        }
        InsertComputeStorageBarrier(cmd);
        DispatchStage(cmd, frameIndex, timestamps, 4, "SimpleDdgiLightTree.Finalize", push);
        InsertComputeStorageBarrier(cmd);
        _resources.MarkRecorded();
        _resources.RecordPublicationReadback(cmd, frameIndex);
    }

    private void DispatchStage(
        CommandBuffer cmd,
        int frameIndex,
        GpuTimestampRecorder? timestamps,
        int stage,
        string timingName,
        GPUDdgiLightTreePushConstants push)
    {
        timestamps?.BeginPass(cmd, frameIndex, timingName);
        try
        {
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _pipelines[stage]);
            BindBindlessStorageAndTextures(
                cmd,
                _pipelineLayout,
                PipelineBindPoint.Compute);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUDdgiLightTreePushConstants>(),
                &push);
            _context.Api.CmdDispatch(cmd, 1, 1, 1);
        }
        finally
        {
            timestamps?.EndPass(cmd, frameIndex);
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    private void InsertComputeStorageBarrier(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void InsertTransferToTraceBarrier(CommandBuffer cmd)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void CreatePipelineCache()
    {
        var info = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo
        };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device,
            &info,
            null,
            out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create DDGI light-tree pipeline cache", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout
        };
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = (uint)Marshal.SizeOf<GPUDdgiLightTreePushConstants>()
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &info,
            null,
            out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create DDGI light-tree pipeline layout", result);
    }

    private VkPipeline CreatePipeline(string shaderName)
    {
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)_entryPointName
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId($"{Name}:{shaderName}"),
                    &info,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &info,
                    null,
                    out pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create DDGI light-tree pipeline '{shaderName}'", result);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    public override void Cleanup()
    {
        for (int index = 0; index < _pipelines.Length; index++)
        {
            if (_pipelines[index].Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipelines[index], null);
            _pipelines[index] = default;
        }
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipelineLayout = default;
        _pipelineCache = default;
    }
}
