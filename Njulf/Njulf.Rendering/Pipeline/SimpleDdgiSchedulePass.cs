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
/// Records the bounded GPU scheduler transaction.  Every stage is a direct,
/// fixed-capacity dispatch; only the update consumers use the indirect commands
/// produced by the final emit stage.  This keeps the scheduler itself easy to
/// validate and prevents a stale indirect command from replaying prior work.
/// </summary>
public sealed unsafe class SimpleDdgiSchedulePass : RenderPassBase
{
    private static readonly string[] ShaderNames =
    [
        "ddgi_simple_schedule_reset.comp.spv",
        "ddgi_simple_schedule_classify.comp.spv",
        "ddgi_simple_schedule_prefix.comp.spv",
        "ddgi_simple_schedule_lane_base.comp.spv",
        "ddgi_simple_schedule_compact.comp.spv",
        "ddgi_simple_schedule_admit_tail.comp.spv",
        "ddgi_simple_schedule_admit.comp.spv",
        "ddgi_simple_schedule_materialize.comp.spv",
        "ddgi_simple_schedule_emit.comp.spv"
    ];

    private static readonly string[] TimingNames =
    [
        "SimpleDdgiSchedule.Reset",
        "SimpleDdgiSchedule.Classify",
        "SimpleDdgiSchedule.Prefix",
        "SimpleDdgiSchedule.LaneBase",
        "SimpleDdgiSchedule.Compact",
        "SimpleDdgiSchedule.TailAdmit",
        "SimpleDdgiSchedule.Admit",
        "SimpleDdgiSchedule.Materialize",
        "SimpleDdgiSchedule.Emit"
    ];

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiSchedulePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager)
        : base("SimpleDdgiSchedulePass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute =>
        AsyncComputePassCatalog.IsCorrectnessCertified(AsyncComputePath.SimpleDdgiUpdate);
    public override string AsyncComputeReason =>
        "Simple DDGI scheduling is bounded compute and owns the resident admission arena.";

    public override void Initialize()
    {
        try
        {
            CreatePipelineCache();
            CreatePipelineLayout();
            for (int i = 0; i < _pipelines.Length; i++)
                _pipelines[i] = CreatePipeline(ShaderNames[i]);
        }
        catch (Exception ex)
        {
            _volumeManager.ReportGpuSchedulerUnavailable(
                $"GPU scheduler schedule pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        if (_volumeManager.TransportTailAuditPending)
            return false;
        return _volumeManager.SchedulerMode.IsGpuMode() &&
            gi.EffectiveUseDdgi &&
            gi.SimpleDdgiStructuredGatherEnabled &&
            gi.EffectiveUseRayQueryBackend &&
            _volumeManager.GpuSchedulerFrameExecutionAvailable &&
            _volumeManager.ProbeCount > 0 &&
            _volumeManager.GpuScheduler.IsReady &&
            _pipelines[0].Handle != 0;
    }

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        ExecuteStages(cmd, frameIndex, timestamps: null);
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData,
        GpuTimestampRecorder? timestamps)
    {
        ExecuteStages(cmd, frameIndex, timestamps);
    }

    private void ExecuteStages(
        CommandBuffer cmd,
        int frameIndex,
        GpuTimestampRecorder? timestamps)
    {
        SimpleDdgiGpuSchedulerLayout layout = _volumeManager.GpuScheduler.Layout ??
            throw new InvalidOperationException("Simple DDGI scheduler layout is not resident.");
        GPUSimpleDdgiSchedulePushConstants pushConstants =
            _volumeManager.GpuScheduler.BuildPushConstants();

        Span<uint> groupCounts =
        [
            1,
            SimpleDdgiGpuSchedulerLayout.GroupsFor(layout.ActiveProbeCount),
            SimpleDdgiGpuSchedulerLayout.GroupsFor((layout.LaneCapacity + 1) / 2),
            1,
            SimpleDdgiGpuSchedulerLayout.GroupsFor(layout.ActiveProbeCount),
            1,
            1,
            SimpleDdgiGpuSchedulerLayout.GroupsFor(layout.RequestCapacity),
            1
        ];
        for (int stage = 0; stage < groupCounts.Length; stage++)
        {
            timestamps?.BeginPass(cmd, frameIndex, TimingNames[stage]);
            try
            {
                DispatchStage(cmd, pushConstants, stage, groupCounts[stage]);
            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
            }
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        for (int i = 0; i < _pipelines.Length; i++)
        {
            if (_pipelines[i].Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _pipelines[i], null);
            _pipelines[i] = default;
        }
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipelineLayout = default;
        _pipelineCache = default;
    }

    private void DispatchStage(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants,
        int stage,
        uint groupCount)
    {
        pushConstants.Stage = checked((uint)stage);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[stage]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiSchedulePushConstants>(),
            &pushConstants);
        _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
        // Emit publishes the indirect command records consumed by the rest of
        // the resident transaction. Those reads occur at DRAW_INDIRECT, not
        // COMPUTE_SHADER, including when the commands dispatch compute work.
        if (stage == _pipelines.Length - 1)
            InsertStorageAndIndirectBarrier(cmd);
        else
            InsertStorageBarrier(cmd);
    }

    private void CreatePipelineCache()
    {
        var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device,
            &cacheInfo,
            null,
            out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create Simple DDGI scheduler pipeline cache", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout
        };
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiSchedulePushConstants>()
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &layoutInfo,
            null,
            out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create Simple DDGI scheduler pipeline layout", result);
    }

    private VkPipeline CreatePipeline(string shaderName)
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, shaderName);
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
            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create Simple DDGI scheduler pipeline '{shaderName}'", result);
            return pipeline;
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
        }
    }

    private void InsertStorageBarrier(CommandBuffer cmd)
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

    private void InsertStorageAndIndirectBarrier(CommandBuffer cmd)
    {
        BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                           PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                            AccessFlags2.ShaderStorageWriteBit |
                            AccessFlags2.IndirectCommandReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _volumeManager.GpuScheduler.GetArenaVkBuffer(),
            Offset = 0,
            Size = _volumeManager.GpuScheduler.Layout?.TotalBytes ?? 0UL
        };
        barriers[1] = new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                            AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _volumeManager.GetProbeUpdateQueueVkBuffer(),
            Offset = 0,
            Size = _volumeManager.ProbeUpdateQueueBytes
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 2,
            PBufferMemoryBarriers = barriers
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
}
