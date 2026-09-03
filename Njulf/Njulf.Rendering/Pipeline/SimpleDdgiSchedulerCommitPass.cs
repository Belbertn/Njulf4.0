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

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Completes the GPU-owned Simple-DDGI transaction after publication and copies
/// only the fixed feedback summary to a delayed host-visible slot.
/// </summary>
public sealed unsafe class SimpleDdgiSchedulerCommitPass : RenderPassBase
{
    private static readonly string[] ShaderNames =
    [
        "ddgi_simple_schedule_validate_scroll_cohorts.comp.spv",
        "ddgi_simple_schedule_commit_local.comp.spv",
        "ddgi_simple_schedule_commit_propagation.comp.spv",
        "ddgi_simple_schedule_feedback_partial.comp.spv",
        "ddgi_simple_schedule_feedback.comp.spv"
    ];

    internal const uint FeedbackProbesPerPartialGroup = 64u * 64u;
    internal const uint MaximumFeedbackPartialGroupCount = 8u;

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiSchedulerCommitPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiSchedulerCommitPass", context, swapchain, bindlessHeap)
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
        "Simple DDGI commit validates publication generations and exports fixed feedback.";

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
                $"GPU scheduler commit pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
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
            PipelinesAreReady();
    }

    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        SimpleDdgiGpuScheduler scheduler = _volumeManager.GpuScheduler;
        _ = scheduler.Layout ??
            throw new InvalidOperationException("Simple DDGI scheduler layout is not resident.");
        GPUSimpleDdgiSchedulePushConstants pushConstants = scheduler.BuildPushConstants();
        pushConstants.PrivateVisibilityAtlasOffsetWords =
            _volumeManager.GpuSchedulerPrivateVisibilityOffsetWords;
        bool resident = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident;

        DispatchScrollCohortValidation(cmd, pushConstants);

        // Mirror mode deliberately skips lifecycle mutation: the CPU queue and
        // CPU state remain authoritative. It still receives the same fixed
        // feedback reduction for delayed validation tooling.
        if (resident)
        {
            DispatchResidentLocal(cmd, pushConstants);
            DispatchIndirect(cmd, pushConstants, 2, 1,
                scheduler.GetIndirectCommandOffset(SimpleDdgiSchedulerDispatchSlot.CommitPropagation));
        }

        uint partialGroupCount = CalculateFeedbackPartialGroupCount(
            _volumeManager.ProbeCount);
        pushConstants.Stage = partialGroupCount;
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[3]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatch(cmd, partialGroupCount, 1, 1);
        InsertStorageBarrier(cmd);

        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[4]);
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatch(cmd, 1, 1, 1);
        InsertStorageBarrier(cmd);
        _ = scheduler.RecordFeedbackReadback(
            cmd,
            frameIndex,
            _volumeManager.FrameSerial,
            _volumeManager.MutationGeneration,
            _volumeManager.LastPreparedFrameIndex);
    }

    internal static uint CalculateFeedbackPartialGroupCount(int probeCount)
    {
        uint activeProbeCount = checked((uint)Math.Min(
            Math.Max(0, probeCount),
            32768));
        return Math.Min(
            MaximumFeedbackPartialGroupCount,
            Math.Max(
                1u,
                (activeProbeCount + FeedbackProbesPerPartialGroup - 1u) /
                    FeedbackProbesPerPartialGroup));
    }

    /// <summary>
    /// Commits the producer-complete urgent cohort and conservatively spills
    /// its 3x3x3 dependency proxy into the persistent sparse-residual queue.
    /// Feedback export remains owned by the ordinary post-forward commit, so
    /// the pre-forward transaction cannot publish a partial policy sample.
    /// </summary>
    public void ExecuteResidentLocalAndPropagation(CommandBuffer cmd)
    {
        if (_volumeManager.SchedulerMode != SimpleDdgiSchedulerMode.GpuResident)
            return;

        SimpleDdgiGpuScheduler scheduler = _volumeManager.GpuScheduler;
        _ = scheduler.Layout ??
            throw new InvalidOperationException(
                "Simple DDGI scheduler layout is not resident.");
        GPUSimpleDdgiSchedulePushConstants pushConstants =
            scheduler.BuildPushConstants();
        pushConstants.PrivateVisibilityAtlasOffsetWords =
            _volumeManager.GpuSchedulerPrivateVisibilityOffsetWords;
        DispatchScrollCohortValidation(cmd, pushConstants);
        DispatchResidentLocal(cmd, pushConstants);
        DispatchIndirect(
            cmd,
            pushConstants,
            2,
            1,
            scheduler.GetIndirectCommandOffset(
                SimpleDdgiSchedulerDispatchSlot.CommitPropagation));
    }

    private void DispatchResidentLocal(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants)
    {
        DispatchIndirect(
            cmd,
            pushConstants,
            1,
            0,
            _volumeManager.GpuScheduler.GetIndirectCommandOffset(
                SimpleDdgiSchedulerDispatchSlot.CommitLocal));
    }

    private void DispatchScrollCohortValidation(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants)
    {
        pushConstants.Stage = 0u;
        _context.Api.CmdBindPipeline(
            cmd,
            PipelineBindPoint.Compute,
            _pipelines[0]);
        BindBindlessStorageAndTextures(
            cmd,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatch(cmd, 1u, 1u, 1u);
        InsertStorageBarrier(cmd);
    }

    private bool PipelinesAreReady()
    {
        for (int i = 0; i < _pipelines.Length; i++)
        {
            if (_pipelines[i].Handle == 0)
                return false;
        }
        return true;
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

    private void DispatchIndirect(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants,
        int pipelineIndex,
        uint pushStage,
        ulong offset)
    {
        pushConstants.Stage = pushStage;
        _context.Api.CmdBindPipeline(
            cmd,
            PipelineBindPoint.Compute,
            _pipelines[pipelineIndex]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        InsertIndirectCommandReadBarrier(cmd);
        _context.Api.CmdDispatchIndirect(cmd, _volumeManager.GpuScheduler.GetArenaVkBuffer(), offset);
        InsertStorageBarrier(cmd);
    }

    private void PushConstants(CommandBuffer cmd, GPUSimpleDdgiSchedulePushConstants pushConstants)
    {
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiSchedulePushConstants>(),
            &pushConstants);
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
            throw new VulkanException("Failed to create Simple DDGI commit pipeline cache", result);
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
            throw new VulkanException("Failed to create Simple DDGI commit pipeline layout", result);
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
                throw new VulkanException($"Failed to create Simple DDGI commit pipeline '{shaderName}'", result);
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

    private void InsertIndirectCommandReadBarrier(CommandBuffer cmd)
    {
        SimpleDdgiGpuScheduler scheduler = _volumeManager.GpuScheduler;
        SimpleDdgiGpuSchedulerLayout? layout = scheduler.Layout;
        BufferMemoryBarrier2 barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                           PipelineStageFlags2.DrawIndirectBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit |
                            AccessFlags2.ShaderStorageReadBit |
                            AccessFlags2.ShaderStorageWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = scheduler.GetArenaVkBuffer(),
            Offset = 0,
            Size = layout?.TotalBytes ?? 0UL
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

}
