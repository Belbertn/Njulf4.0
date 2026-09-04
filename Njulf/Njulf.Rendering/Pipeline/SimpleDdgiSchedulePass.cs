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
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Records the bounded GPU scheduler transaction. Reset is the only direct
/// dispatch; it clears every command before the GPU-sized schedule chain is
/// consumed, so an empty transaction cannot replay work from a prior frame.
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
        "ddgi_simple_schedule_emit.comp.spv",
        "ddgi_simple_schedule_emit_scatter.comp.spv"
    ];

    private static readonly SimpleDdgiSchedulerDispatchSlot[] DispatchSlots =
    [
        SimpleDdgiSchedulerDispatchSlot.Reset,
        SimpleDdgiSchedulerDispatchSlot.Classify,
        SimpleDdgiSchedulerDispatchSlot.Prefix,
        SimpleDdgiSchedulerDispatchSlot.LaneBase,
        SimpleDdgiSchedulerDispatchSlot.Compact,
        SimpleDdgiSchedulerDispatchSlot.TailAdmit,
        SimpleDdgiSchedulerDispatchSlot.Admit,
        SimpleDdgiSchedulerDispatchSlot.MaterializeClassify,
        SimpleDdgiSchedulerDispatchSlot.EmitPrefix,
        SimpleDdgiSchedulerDispatchSlot.EmitScatter
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
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiSchedulePass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiSchedulePass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute =>
        AsyncComputePassCatalog.IsProductionActivationAuthorized(
            AsyncComputePath.SimpleDdgiUpdate);
    public override string AsyncComputeReason =>
        "Simple DDGI scheduling is bounded compute and owns the resident admission arena.";

    public override void Initialize()
    {
        try
        {
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
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

        const int EmitFirstStage = 8;
        for (int stage = 0; stage < EmitFirstStage; stage++)
        {
            timestamps?.BeginPass(cmd, frameIndex, TimingNames[stage]);
            try
            {
                DispatchStage(cmd, pushConstants, stage);
            }
            finally
            {
                timestamps?.EndPass(cmd, frameIndex);
            }
        }

        timestamps?.BeginPass(cmd, frameIndex, TimingNames[EmitFirstStage]);
        try
        {
            for (int stage = EmitFirstStage;
                 stage < DispatchSlots.Length;
                 stage++)
            {
                DispatchStage(cmd, pushConstants, stage);
            }
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

    private void DispatchStage(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants,
        int stage)
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
        if (stage == 0)
        {
            _context.Api.CmdDispatch(cmd, 1u, 1u, 1u);
        }
        else
        {
            ulong offset = _volumeManager.GpuScheduler
                .GetIndirectCommandOffset(DispatchSlots[stage]);
            _context.Api.CmdDispatchIndirect(
                cmd,
                _volumeManager.GpuScheduler.GetArenaVkBuffer(),
                offset);
        }
        InsertStageBarrier(cmd, stage);
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
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId($"{Name}:{shaderName}"),
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out pipeline);
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

    private void InsertStageBarrier(CommandBuffer cmd, int stage)
    {
        SimpleDdgiGpuSchedulerLayout layout =
            _volumeManager.GpuScheduler.Layout ??
            throw new InvalidOperationException(
                "Simple DDGI scheduler layout is not resident.");
        VkBuffer arena = _volumeManager.GpuScheduler.GetArenaVkBuffer();
        Span<BufferMemoryBarrier2> barriers =
            stackalloc BufferMemoryBarrier2[12];
        int count = 0;

        switch (stage)
        {
            case 0: // reset -> classify and all later schedule stages
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateGroupLaneCounts);
                AppendRange(barriers, ref count, arena,
                    layout.LaneCandidateCounts.Offset,
                    checked(layout.LaneAdmission.End -
                        layout.LaneCandidateCounts.Offset));
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendRegion(barriers, ref count, arena,
                    layout.FeedbackSummary);
                AppendIndirectCommands(barriers, ref count, arena, layout);
                break;
            case 1: // classify -> prefix/compact/admit
                AppendRegion(barriers, ref count, arena, layout.CandidateInput);
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateGroupLaneCounts);
                AppendRegion(barriers, ref count, arena,
                    layout.LaneCandidateCounts);
                AppendRegion(barriers, ref count, arena, layout.Counters);
                break;
            case 2: // packed lane prefix -> lane-base/compact
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateGroupLaneCounts);
                AppendRange(barriers, ref count, arena,
                    layout.LaneCandidateCounts.Offset,
                    checked(layout.LanePrefixes.End -
                        layout.LaneCandidateCounts.Offset));
                break;
            case 3: // lane-base -> compact and exactly one admission variant
                AppendRegion(barriers, ref count, arena, layout.LanePrefixes);
                AppendRegion(barriers, ref count, arena, layout.LaneTotals);
                AppendRegion(barriers, ref count, arena, layout.LaneAdmission);
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendIndirectCommands(barriers, ref count, arena, layout);
                break;
            case 4: // compact -> both admission variants
                AppendRegion(barriers, ref count, arena, layout.CandidateOutput);
                AppendRegion(barriers, ref count, arena, layout.Counters);
                break;
            case 5: // tail-specialized admission -> materialize/emit
            case 6: // generic admission -> materialize/emit
                AppendRegion(barriers, ref count, arena, layout.CandidateInput);
                AppendRegion(barriers, ref count, arena, layout.UpdateRecords);
                AppendRegion(barriers, ref count, arena, layout.LaneCursors);
                AppendRegion(barriers, ref count, arena, layout.LaneAdmission);
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendIndirectCommands(barriers, ref count, arena, layout);
                break;
            case 7: // fused selection materialization/bucket classify -> emit prefix
                AppendRegion(barriers, ref count, arena, layout.UpdateRecords);
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateGroupLaneCounts);
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateOutput);
                // Classification atomically counts unmatched accepted records
                // and initializes outcomes. Emit must observe both before it
                // can publish any indirect consumer command.
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendRegion(barriers, ref count, arena, layout.Outcomes);
                break;
            case 8: // stable group prefix -> stable queue scatter
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateGroupLaneCounts);
                AppendRegion(barriers, ref count, arena,
                    layout.CandidateOutput);
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendRegion(barriers, ref count, arena,
                    layout.RayBucketMetadata);
                AppendIndirectCommands(barriers, ref count, arena, layout);
                break;
            case 9: // stable queue scatter -> resident consumers
                AppendRegion(barriers, ref count, arena, layout.Counters);
                AppendRegion(barriers, ref count, arena, layout.Outcomes);
                AppendRegion(barriers, ref count, arena,
                    layout.RayBucketMetadata);
                AppendRange(
                    barriers,
                    ref count,
                    arena,
                    layout.RayBucketCommands.Offset,
                    layout.RayBucketCommands.ByteSize,
                    PipelineStageFlags2.DrawIndirectBit,
                    AccessFlags2.IndirectCommandReadBit);
                AppendRange(
                    barriers,
                    ref count,
                    arena,
                    layout.IndirectCommands.Offset,
                    layout.IndirectCommands.ByteSize,
                    PipelineStageFlags2.DrawIndirectBit,
                    AccessFlags2.IndirectCommandReadBit);
                barriers[count++] = new BufferMemoryBarrier2
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
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stage));
        }

        fixed (BufferMemoryBarrier2* barrierPointer = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = checked((uint)count),
                PBufferMemoryBarriers = barrierPointer
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    private static void AppendIndirectCommands(
        Span<BufferMemoryBarrier2> barriers,
        ref int count,
        VkBuffer arena,
        SimpleDdgiGpuSchedulerLayout layout)
    {
        AppendRange(
            barriers,
            ref count,
            arena,
            layout.IndirectCommands.Offset,
            layout.IndirectCommands.ByteSize,
            PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.IndirectCommandReadBit);
    }

    private static void AppendRegion(
        Span<BufferMemoryBarrier2> barriers,
        ref int count,
        VkBuffer arena,
        SimpleDdgiSchedulerArenaRegion region)
    {
        AppendRange(
            barriers,
            ref count,
            arena,
            region.Offset,
            region.ByteSize,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit);
    }

    private static void AppendRange(
        Span<BufferMemoryBarrier2> barriers,
        ref int count,
        VkBuffer arena,
        ulong offset,
        ulong size,
        PipelineStageFlags2 destinationStages =
            PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2 destinationAccess =
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit)
    {
        barriers[count++] = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = destinationStages,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = arena,
            Offset = offset,
            Size = size
        };
    }
}
