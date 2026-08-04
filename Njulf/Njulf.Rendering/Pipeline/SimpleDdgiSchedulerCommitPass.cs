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
        "ddgi_simple_schedule_commit_local.comp.spv",
        "ddgi_simple_schedule_commit_propagation.comp.spv",
        "ddgi_simple_schedule_feedback.comp.spv"
    ];

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiSchedulerCommitPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager)
        : base("SimpleDdgiSchedulerCommitPass", context, swapchain, bindlessHeap)
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
        "Simple DDGI commit validates publication generations and exports fixed feedback.";

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
                $"GPU scheduler commit pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        return _volumeManager.SchedulerMode.IsGpuMode() &&
            gi.EffectiveUseDdgi &&
            gi.SimpleDdgiStructuredGatherEnabled &&
            gi.EffectiveUseRayQueryBackend &&
            _volumeManager.GpuSchedulerFrameExecutionAvailable &&
            _volumeManager.ProbeCount > 0 &&
            _volumeManager.GpuScheduler.IsReady &&
            _pipelines[2].Handle != 0;
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

        // Mirror mode deliberately skips lifecycle mutation: the CPU queue and
        // CPU state remain authoritative. It still receives the same fixed
        // feedback reduction for delayed validation tooling.
        if (resident)
        {
            DispatchIndirect(cmd, pushConstants, 0,
                scheduler.GetIndirectCommandOffset(SimpleDdgiSchedulerDispatchSlot.CommitLocal));
            DispatchIndirect(cmd, pushConstants, 1,
                scheduler.GetIndirectCommandOffset(SimpleDdgiSchedulerDispatchSlot.CommitPropagation));
        }

        pushConstants.Stage = 2u;
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[2]);
        BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        // Feedback always consists of one bounded reduction workgroup.  It has
        // no data-dependent extent, so using an arena-written indirect command
        // provides no scheduling benefit and unnecessarily couples the final
        // frame fence to an indirect-buffer read after all scheduler writes.
        // Keep the genuinely variable commit stages indirect, but make this
        // fixed dispatch explicit and robust across drivers.
        _context.Api.CmdDispatch(cmd, 1, 1, 1);
        InsertStorageBarrier(cmd);
        _ = scheduler.RecordFeedbackReadback(cmd, frameIndex, _volumeManager.FrameSerial);
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

    private void DispatchIndirect(
        CommandBuffer cmd,
        GPUSimpleDdgiSchedulePushConstants pushConstants,
        uint stage,
        ulong offset)
    {
        pushConstants.Stage = stage;
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipelines[checked((int)stage)]);
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
            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1,
                &pipelineInfo,
                null,
                out VkPipeline pipeline);
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
