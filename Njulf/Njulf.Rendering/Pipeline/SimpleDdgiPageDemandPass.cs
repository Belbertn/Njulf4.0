using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
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
/// Predicts the sparse near-ring working set from current opaque depth. One
/// invocation covers a 64x64 receiver tile and stamps four stratified samples;
/// no page list is copied to or selected by the CPU.
/// </summary>
public sealed unsafe class SimpleDdgiPageDemandPass : RenderPassBase
{
    internal const uint ReceiverTileSize = 64u;
    internal const uint WorkgroupWidth = 4u;
    private readonly RenderSettings _settings;
    private readonly RenderTargetManager _renderTargets;
    private readonly SimpleDdgiVolumeManager _volumeManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;

    public SimpleDdgiPageDemandPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        RenderTargetManager renderTargets,
        SimpleDdgiVolumeManager volumeManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("SimpleDdgiPageDemandPass", context, swapchain, bindlessHeap)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override bool SupportsSecondaryCommandBuffer => true;
    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
    public override bool SupportsAsyncCompute => false;
    public override string AsyncComputeReason =>
        "Depth demand and residency mutation remain in the serial Simple-DDGI ownership segment.";

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
                $"page demand pipeline unavailable: {ex.GetType().Name}: {ex.Message}");
            Cleanup();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        GlobalIlluminationSettings gi = _settings.GlobalIllumination;
        return _pipeline.Handle != 0 &&
            sceneData.SimpleDdgiPageFullManagementRequired != 0 &&
            gi.EffectiveUseDdgi &&
            gi.SimpleDdgiStructuredGatherEnabled &&
            _volumeManager.ProbeResidencyMode.CollectsDemand() &&
            _volumeManager.ProbePageCache.IsReady &&
            _volumeManager.ProbePageCache.ResidencyValid &&
            !_volumeManager.ProbePageCache.Frozen &&
            _volumeManager.ProbePageCache.Layout?.VirtualPageCount > 0 &&
            _renderTargets.SceneDepth.Extent.Width > 0 &&
            _renderTargets.SceneDepth.Extent.Height > 0;
    }

    public override void Execute(
        CommandBuffer cmd,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        // This pass is deliberately graphics-queue serialized so the general
        // depth transition can safely include both compute and graphics stages.
        // ForwardPlusPass leaves SceneDepth in read-only layout and performs
        // no depth writes. This edge is read-after-read, so do not issue the
        // conservative same-layout attachment republish used by callers that
        // cannot prove the preceding access scope.
        _renderTargets.SceneDepth.TransitionToDepthReadOnly(
            cmd,
            synchronizeMatchingLayout: false);
        _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        BindBindlessStorageAndTextures(
            cmd,
            _pipelineLayout,
            PipelineBindPoint.Compute);

        uint demandEpoch = SimpleDdgiProbePageLayout.DemandEpochForFrame(
            _volumeManager.FrameIndex);
        var pushConstants = new GPUSimpleDdgiPageDemandPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndPadding = new Vector4(sceneData.CameraPosition, 0.0f),
            ScreenWidth = _renderTargets.SceneDepth.Extent.Width,
            ScreenHeight = _renderTargets.SceneDepth.Extent.Height,
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            DepthTextureIndex = BindlessIndex.DepthTexture,
            DemandEpoch = demandEpoch,
            SampleCount = 4u,
            Flags = ReceiverTileSize
        };
        _context.Api.CmdPushConstants(
            cmd,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiPageDemandPushConstants>(),
            &pushConstants);
        const uint workgroupCoverage = ReceiverTileSize * WorkgroupWidth;
        uint groupsX = (pushConstants.ScreenWidth + workgroupCoverage - 1u) /
            workgroupCoverage;
        uint groupsY = (pushConstants.ScreenHeight + workgroupCoverage - 1u) /
            workgroupCoverage;
        _context.Api.CmdDispatch(cmd, Math.Max(groupsX, 1u), Math.Max(groupsY, 1u), 1u);
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
            throw new VulkanException("Failed to create Simple-DDGI page-demand pipeline cache", result);
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
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPageDemandPushConstants>()
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
            throw new VulkanException("Failed to create Simple-DDGI page-demand pipeline layout", result);
    }

    private VkPipeline CreatePipeline()
    {
        ShaderModule module = default;
        try
        {
            module = ShaderModuleLoader.Load(
                _context,
                "ddgi_simple_page_demand.comp.spv");
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
                    new PipelineArtifactId(
                        $"{Name}:ddgi_simple_page_demand.comp.spv"),
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
                throw new VulkanException("Failed to create Simple-DDGI page-demand pipeline", result);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

}
