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
/// Deterministically classifies, compacts, reconciles, initializes, and reports
/// the bounded Simple-DDGI page pool. All page-table mutation precedes scheduler
/// classification in the same serial graph segment.
/// </summary>
public sealed unsafe class SimpleDdgiPageResidencyPass : RenderPassBase
{
    private static readonly string[] ShaderNames =
    [
        "ddgi_simple_page_reset.comp.spv",
        "ddgi_simple_page_classify.comp.spv",
        "ddgi_simple_page_reconcile.comp.spv",
        "ddgi_simple_page_initialize.comp.spv"
    ];

    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private readonly VkPipeline[] _pipelines = new VkPipeline[ShaderNames.Length];
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;

    public SimpleDdgiPageResidencyPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiPageResidencyPass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => false;
    public override string AsyncComputeReason =>
        "Residency mutation, scheduler classification, and all payload consumers share one serial ownership segment.";

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
        catch (Exception ex)
        {
            _volumeManager.ReportProbeResidencyUnavailable(
                $"page residency pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        return _pipelines[0].Handle != 0 &&
            sceneData.SimpleDdgiPageFullManagementRequired != 0 &&
            !_volumeManager.TransportTailAuditPending &&
            gi.EffectiveUseDdgi &&
            gi.SimpleDdgiStructuredGatherEnabled &&
            _volumeManager.ProbeResidencyMode.CollectsDemand() &&
            _volumeManager.ProbePageCache.IsReady &&
            _volumeManager.ProbePageCache.ResidencyValid &&
            !_volumeManager.ProbePageCache.BootstrapRequired &&
            !_volumeManager.ProbePageCache.Frozen &&
            _volumeManager.ProbePageCache.Layout?.VirtualPageCount > 0;
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        SimpleDdgiProbePageLayout layout =
            _volumeManager.ProbePageCache.Layout ??
            throw new InvalidOperationException("Simple-DDGI page layout is not resident.");
        SimpleDdgiGpuSchedulerLayout? schedulerLayout =
            _volumeManager.GpuScheduler.Layout;
        bool cameraCut = sceneData.HiZPolicyCameraCut != 0;
        bool bootstrap =
            _volumeManager.ProbeResidencyBootstrapClassificationActive;
        var pushConstants = new GPUSimpleDdgiPageResidencyPushConstants
        {
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
            RelocationClassificationBufferIndex =
                BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
            ReceiverProbeBufferIndex = BindlessIndex.SimpleDdgiReceiverProbeBuffer,
            TransportSourceCacheBufferIndex =
                BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
            SchedulerArenaBufferIndex = schedulerLayout != null
                ? (uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer
                : 0u,
            SchedulerProbeStateOffsetWords = schedulerLayout?.ProbeState.OffsetWords ?? 0u,
            SchedulerActiveProbeCount = checked((uint)Math.Max(
                schedulerLayout?.ActiveProbeCount ?? 0,
                0)),
            CurrentFrame = _volumeManager.FrameIndex,
            DemandEpoch = SimpleDdgiProbePageLayout.DemandEpochForFrame(
                _volumeManager.FrameIndex),
            ResourceGeneration = _volumeManager.ProbeResidencyResourceGeneration,
            GeometryGeneration = _volumeManager.ProbeResidencyGeometryGeneration,
            SourceGeneration = _volumeManager.SourceLightingGeneration,
            CohortGeneration = _volumeManager.SourceLightingGeneration,
            AllocationFlags =
                (cameraCut
                    ? SimpleDdgiProbePageLayout.PhysicalPageAllocationCameraCut
                    : 0u) |
                (bootstrap
                    ? SimpleDdgiProbePageLayout.PhysicalPageAllocationBootstrap
                    : 0u),
            VisiblePublicationProbeBudget = checked((uint)
                _volumeManager.ProbeResidencyVisiblePublicationProbeBudget),
            PublicationLatencyTargetFrames = checked((uint)
                (cameraCut || bootstrap
                    ? SimpleDdgiProbePageLayout.CameraCutPublicationLatencyTargetFrames
                    : SimpleDdgiProbePageLayout.OrdinaryPublicationLatencyTargetFrames))
        };

        InsertDemandVisibilityBarrier(cmd);
        Dispatch(cmd, pushConstants, 0, 1u, indirectConsumers: false);
        Dispatch(
            cmd,
            pushConstants,
            1,
            checked((uint)Math.Max(1, (layout.VirtualPageCount + 63) / 64)),
            indirectConsumers: false);
        Dispatch(cmd, pushConstants, 2, 1u, indirectConsumers: true);

        pushConstants.Stage = 3u;
        _context.Api.CmdBindPipeline(
            cmd,
            PipelineBindPoint.Compute,
            _pipelines[3]);
        BindBindlessStorageAndTextures(
            cmd,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatchIndirect(
            cmd,
            _volumeManager.ProbePageCache.GetArenaVkBuffer(),
            _volumeManager.ProbePageCache.GetInitializationIndirectOffset());
        InsertStorageBarrier(cmd, includeIndirectRead: false);

    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
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

    private void Dispatch(
        CommandBuffer cmd,
        GPUSimpleDdgiPageResidencyPushConstants pushConstants,
        int stage,
        uint groupCount,
        bool indirectConsumers)
    {
        pushConstants.Stage = checked((uint)stage);
        _context.Api.CmdBindPipeline(
            cmd,
            PipelineBindPoint.Compute,
            _pipelines[stage]);
        BindBindlessStorageAndTextures(
            cmd,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        PushConstants(cmd, pushConstants);
        _context.Api.CmdDispatch(cmd, groupCount, 1u, 1u);
        InsertStorageBarrier(cmd, indirectConsumers);
    }

    private void PushConstants(
        CommandBuffer cmd,
        GPUSimpleDdgiPageResidencyPushConstants pushConstants)
    {
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiPageResidencyPushConstants>(),
            &pushConstants);
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
            throw new VulkanException("Failed to create Simple-DDGI page-residency pipeline cache", result);
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
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPageResidencyPushConstants>()
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
            throw new VulkanException("Failed to create Simple-DDGI page-residency pipeline layout", result);
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
                throw new VulkanException(
                    $"Failed to create Simple-DDGI page pipeline '{shaderName}'",
                    result);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    private void InsertStorageBarrier(
        CommandBuffer cmd,
        bool includeIndirectRead)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                (includeIndirectRead ? PipelineStageFlags2.DrawIndirectBit : 0),
            DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit |
                (includeIndirectRead ? AccessFlags2.IndirectCommandReadBit : 0)
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void InsertDemandVisibilityBarrier(CommandBuffer cmd)
    {
        // Opaque/foliage receiver feedback is written by graphics shaders and
        // proactive depth demand is written by compute. The render graph tracks
        // buffer ownership but image-only planning does not publish shader
        // storage writes, so acquire both producers explicitly before reset and
        // classification consume the target epoch.
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.MeshShaderBitExt |
                PipelineStageFlags2.ComputeShaderBit,
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
}
