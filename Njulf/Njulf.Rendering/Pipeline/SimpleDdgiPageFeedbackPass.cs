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
/// Reduces the completed page/scheduler transaction into the fixed 1 KiB
/// delayed residency summary. It is deliberately ordered after lifecycle
/// commit so publication and rejection counters describe the same frame.
/// </summary>
public sealed unsafe class SimpleDdgiPageFeedbackPass : RenderPassBase
{
    private readonly RenderSettings _settings;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;

    public SimpleDdgiPageFeedbackPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        SimpleDdgiVolumeManager volumeManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiPageFeedbackPass", context, swapchain, bindlessHeap)
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
        "Residency feedback closes the serial page/publication transaction.";

    public override void Initialize()
    {
        try
        {
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            _pipeline = CreatePipeline();
        }
        catch (Exception ex)
        {
            _volumeManager.ReportProbeResidencyUnavailable(
                $"page feedback pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        return _pipeline.Handle != 0 &&
            gi.EffectiveUseDdgi &&
            _volumeManager.ProbeResidencyMode.CollectsDemand() &&
            _volumeManager.ProbePageCache.IsReady &&
            _volumeManager.ProbePageCache.ResidencyValid &&
            !_volumeManager.ProbePageCache.BootstrapRequired &&
            _volumeManager.ProbePageCache.Layout?.VirtualPageCount > 0;
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        SimpleDdgiGpuSchedulerLayout? schedulerLayout =
            _volumeManager.GpuScheduler.Layout;
        ulong frameSerial = _volumeManager.FrameSerial;
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
            Stage = sceneData.SimpleDdgiPageFullManagementRequired != 0
                ? 5u
                : 6u,
            FrameSerialLow = unchecked((uint)frameSerial),
            FrameSerialHigh = unchecked((uint)(frameSerial >> 32))
        };

        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        BindBindlessStorageAndTextures(
            cmd,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiPageResidencyPushConstants>(),
            &pushConstants);
        _context.Api.CmdDispatch(cmd, 1u, 1u, 1u);
        _ = _volumeManager.ProbePageCache.RecordFeedbackReadback(
            cmd,
            frameIndex,
            frameSerial);
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        if (_pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
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
            throw new VulkanException(
                "Failed to create Simple-DDGI page-feedback pipeline cache",
                result);
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
            throw new VulkanException(
                "Failed to create Simple-DDGI page-feedback pipeline layout",
                result);
    }

    private VkPipeline CreatePipeline()
    {
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(
                _context,
                "ddgi_simple_page_feedback.comp.spv");
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
            long pipelineStart = _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
            Result result;
            VkPipeline pipeline;
            try
            {
                result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &info,
                    null,
                    out pipeline);
            }
            finally
            {
                _pipelineCacheService?.EndPipelineCreation(
                    $"{Name}:ddgi_simple_page_feedback.comp.spv",
                    pipelineStart);
            }
            if (result != Result.Success)
                throw new VulkanException(
                    "Failed to create Simple-DDGI page-feedback pipeline",
                    result);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }
}
