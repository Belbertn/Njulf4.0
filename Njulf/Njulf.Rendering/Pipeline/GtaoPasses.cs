using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

internal sealed class GtaoHistoryState
{
    private bool _valid;
    private Extent2D _extent;
    private GtaoQualityPreset _qualityPreset;
    private Matrix4x4 _projection;
    private ulong _sceneContentRevision = ulong.MaxValue;
    private ulong _cameraCutSerial = ulong.MaxValue;

    public bool CanReuse(
        SceneRenderingData sceneData,
        AmbientOcclusionSettings settings,
        Extent2D extent)
    {
        return _valid &&
            sceneData.MotionVectorsEnabled != 0 &&
            sceneData.HiZPolicyCameraCut == 0 &&
            sceneData.HiZPolicySceneChanged == 0 &&
            _extent.Width == extent.Width &&
            _extent.Height == extent.Height &&
            _qualityPreset == settings.GtaoQualityPreset &&
            _projection.Equals(sceneData.ProjectionMatrix) &&
            _sceneContentRevision == sceneData.SceneContentRevision &&
            _cameraCutSerial == sceneData.CaptureCameraCutSerial;
    }

    public void Commit(
        SceneRenderingData sceneData,
        AmbientOcclusionSettings settings,
        Extent2D extent)
    {
        _valid = true;
        _extent = extent;
        _qualityPreset = settings.GtaoQualityPreset;
        _projection = sceneData.ProjectionMatrix;
        _sceneContentRevision = sceneData.SceneContentRevision;
        _cameraCutSerial = sceneData.CaptureCameraCutSerial;
    }

    public void Reset()
    {
        _valid = false;
        _extent = default;
        _sceneContentRevision = ulong.MaxValue;
        _cameraCutSerial = ulong.MaxValue;
    }
}

internal readonly record struct GtaoImageDescriptor(
    uint Binding,
    DescriptorType Type,
    ImageView View,
    Sampler Sampler,
    ImageLayout Layout);

internal abstract unsafe class GtaoComputePassBase : RenderPassBase
{
    private const string EntryPoint = "main";

    private readonly string _shaderName;
    private readonly DescriptorSetLayoutBinding[] _bindings;
    private readonly int _setCount;
    private readonly uint _pushConstantBytes;
    private readonly nint _entryPointName;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet[] _descriptorSets = Array.Empty<DescriptorSet>();
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;

    protected GtaoComputePassBase(
        string passName,
        string shaderName,
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        DescriptorSetLayoutBinding[] bindings,
        int setCount,
        uint pushConstantBytes,
        GiPipelineCacheService? pipelineCacheService)
        : base(passName, context, swapchain, bindlessHeap)
    {
        _shaderName = shaderName;
        _bindings = bindings;
        _setCount = setCount;
        _pushConstantBytes = pushConstantBytes;
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
    }

    public sealed override bool SupportsSecondaryCommandBuffer => false;

    public override void Initialize()
    {
        CreateDescriptorSetLayout();
        CreatePipelineCache();
        CreatePipelineLayout();
        CreatePipeline();
        RecreateDescriptorSets();
    }

    public override void OnSwapchainRecreated() => RecreateDescriptorSets();

    protected DescriptorSet GetDescriptorSet(int index) =>
        _descriptorSets[index];

    protected void BindAndPush<T>(
        CommandBuffer commandBuffer,
        int descriptorSetIndex,
        in T pushConstants)
        where T : unmanaged
    {
        _context.Api.CmdBindPipeline(commandBuffer,
            PipelineBindPoint.Compute, _pipeline);
        DescriptorSet descriptorSet = GetDescriptorSet(descriptorSetIndex);
        _context.Api.CmdBindDescriptorSets(commandBuffer,
            PipelineBindPoint.Compute, _pipelineLayout, 0, 1,
            &descriptorSet, 0, null);
        T localPushConstants = pushConstants;
        _context.Api.CmdPushConstants(commandBuffer, _pipelineLayout,
            ShaderStageFlags.ComputeBit, 0, _pushConstantBytes,
            &localPushConstants);
    }

    protected void WriteImageDescriptors(
        int setIndex,
        params GtaoImageDescriptor[] descriptors)
    {
        var imageInfos = new DescriptorImageInfo[descriptors.Length];
        var writes = new WriteDescriptorSet[descriptors.Length];
        fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
        fixed (WriteDescriptorSet* writePointer = writes)
        {
            for (int i = 0; i < descriptors.Length; i++)
            {
                GtaoImageDescriptor descriptor = descriptors[i];
                imageInfos[i] = new DescriptorImageInfo
                {
                    Sampler = descriptor.Sampler,
                    ImageView = descriptor.View,
                    ImageLayout = descriptor.Layout
                };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _descriptorSets[setIndex],
                    DstBinding = descriptor.Binding,
                    DescriptorCount = 1,
                    DescriptorType = descriptor.Type,
                    PImageInfo = imageInfoPointer + i
                };
            }
            _context.Api.UpdateDescriptorSets(_context.Device,
                (uint)writes.Length, writePointer, 0, null);
        }
    }

    protected abstract void RewriteDescriptors();

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        if (_pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device,
                _descriptorPool, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device,
                _pipelineLayout, null);
        if (_descriptorSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(_context.Device,
                _descriptorSetLayout, null);
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device,
                _pipelineCache, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
        _pipeline = default;
        _descriptorPool = default;
        _pipelineLayout = default;
        _descriptorSetLayout = default;
        _pipelineCache = default;
        _descriptorSets = Array.Empty<DescriptorSet>();
    }

    private void CreateDescriptorSetLayout()
    {
        fixed (DescriptorSetLayoutBinding* bindings = _bindings)
        {
            var createInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)_bindings.Length,
                PBindings = bindings
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device, &createInfo, null,
                out _descriptorSetLayout);
            if (result != Result.Success)
                throw new VulkanException(
                    $"Failed to create {Name} descriptor layout", result);
        }
        _context.SetDebugName(_descriptorSetLayout.Handle,
            ObjectType.DescriptorSetLayout, $"{Name} Descriptor Layout");
    }

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService != null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
            return;
        }

        var createInfo = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo
        };
        Result result = _context.Api.CreatePipelineCache(_context.Device,
            &createInfo, null, out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException(
                $"Failed to create {Name} pipeline cache", result);
    }

    private void CreatePipelineLayout()
    {
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = _pushConstantBytes
        };
        DescriptorSetLayout setLayout = _descriptorSetLayout;
        var createInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(_context.Device,
            &createInfo, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException(
                $"Failed to create {Name} pipeline layout", result);
    }

    private void CreatePipeline()
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = PipelineObjects.ShaderModuleLoader.Load(
                _context, _shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)_entryPointName
            };
            var createInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId(
                        $"AmbientOcclusion.{Name}"),
                    &createInfo,
                    out _pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &createInfo,
                    null,
                    out _pipeline);
            if (result != Result.Success)
                throw new VulkanException(
                    $"Failed to create {Name} compute pipeline", result);
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device,
                    shaderModule, null);
        }
        _context.SetDebugName(_pipeline.Handle, ObjectType.Pipeline,
            $"{Name} Compute Pipeline");
    }

    private void RecreateDescriptorSets()
    {
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device,
                _descriptorPool, null);
        uint sampledPerSet = 0;
        uint storagePerSet = 0;
        foreach (DescriptorSetLayoutBinding binding in _bindings)
        {
            if (binding.DescriptorType ==
                DescriptorType.CombinedImageSampler)
                sampledPerSet += binding.DescriptorCount;
            else if (binding.DescriptorType == DescriptorType.StorageImage)
                storagePerSet += binding.DescriptorCount;
        }
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = sampledPerSet * (uint)_setCount
        };
        poolSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = storagePerSet * (uint)_setCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = (uint)_setCount
        };
        Result result = _context.Api.CreateDescriptorPool(_context.Device,
            &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException(
                $"Failed to create {Name} descriptor pool", result);

        _descriptorSets = new DescriptorSet[_setCount];
        var layouts = new DescriptorSetLayout[_setCount];
        Array.Fill(layouts, _descriptorSetLayout);
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (DescriptorSet* setPointer = _descriptorSets)
        {
            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = (uint)_setCount,
                PSetLayouts = layoutPointer
            };
            result = _context.Api.AllocateDescriptorSets(_context.Device,
                &allocateInfo, setPointer);
        }
        if (result != Result.Success)
            throw new VulkanException(
                $"Failed to allocate {Name} descriptor sets", result);
        RewriteDescriptors();
    }

    protected static DescriptorSetLayoutBinding Binding(
        uint binding,
        DescriptorType type) => new()
    {
        Binding = binding,
        DescriptorType = type,
        DescriptorCount = 1,
        StageFlags = ShaderStageFlags.ComputeBit
    };
}

internal sealed unsafe class GtaoPass : GtaoComputePassBase
{
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;

    public GtaoPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        HiZDepthPyramid hiZ,
        RenderSettings settings)
        : this(context, swapchain, bindlessHeap, renderTargets, hiZ,
            settings, pipelineCacheService: null)
    {
    }

    internal GtaoPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        HiZDepthPyramid hiZ,
        RenderSettings settings,
        GiPipelineCacheService? pipelineCacheService)
        : base("GtaoPass", "gtao.comp.spv", context, swapchain,
            bindlessHeap,
            [
                Binding(0, DescriptorType.CombinedImageSampler),
                Binding(1, DescriptorType.StorageImage),
                Binding(2, DescriptorType.StorageImage)
            ],
            1,
            (uint)Marshal.SizeOf<GPUGtaoPushConstants>(),
            pipelineCacheService)
    {
        _renderTargets = renderTargets;
        _settings = settings;
    }

    public override bool ShouldExecute(int frameIndex,
        SceneRenderingData sceneData) =>
        sceneData.AmbientOcclusionEnabled &&
        sceneData.AmbientOcclusionMode == AmbientOcclusionMode.Gtao;

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData)
    {
        AmbientOcclusionSettings settings = _settings.AmbientOcclusion;
        var push = new GPUGtaoPushConstants
        {
            InverseProjectionMatrix = sceneData.InverseProjectionMatrix,
            ProjectionMatrix = sceneData.ProjectionMatrix,
            SourceDimensions = new Vector2(sceneData.ScreenWidth,
                sceneData.ScreenHeight),
            DestinationDimensions = new Vector2(
                _renderTargets.GtaoRaw.Extent.Width,
                _renderTargets.GtaoRaw.Extent.Height),
            Radius = settings.Radius,
            Thickness = settings.GtaoThickness,
            Falloff = settings.GtaoFalloff,
            PlaneBias = settings.Bias,
            Intensity = settings.Intensity,
            Power = settings.Power,
            DirectionCount = (uint)settings.EffectiveGtaoDirectionCount,
            StepCount = (uint)settings.EffectiveGtaoStepCount,
            FrameIndex = sceneData.TemporalSampleIndex
        };
        BindAndPush(cmd, 0, push);
        Extent2D extent = _renderTargets.GtaoRaw.Extent;
        _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u,
            (extent.Height + 7u) / 8u, 1u);
        _renderTargets.GtaoRaw.TransitionToComputeShaderRead(cmd);
        _renderTargets.GtaoCurrentGeometry.TransitionToComputeShaderRead(cmd);
    }

    protected override void RewriteDescriptors()
    {
        WriteImageDescriptors(0,
            new GtaoImageDescriptor(0,
                DescriptorType.CombinedImageSampler,
                _renderTargets.SceneDepth.View,
                _bindlessHeap.ScreenSampler,
                ImageLayout.DepthStencilReadOnlyOptimal),
            new GtaoImageDescriptor(1, DescriptorType.StorageImage,
                _renderTargets.GtaoRaw.View, default,
                ImageLayout.General),
            new GtaoImageDescriptor(2, DescriptorType.StorageImage,
                _renderTargets.GtaoCurrentGeometry.View, default,
                ImageLayout.General));
    }
}

internal sealed unsafe class GtaoTemporalPass : GtaoComputePassBase
{
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;
    private readonly GtaoHistoryState _historyState;

    public GtaoTemporalPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        GtaoHistoryState historyState)
        : this(context, swapchain, bindlessHeap, renderTargets, settings,
            historyState, pipelineCacheService: null)
    {
    }

    internal GtaoTemporalPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        GtaoHistoryState historyState,
        GiPipelineCacheService? pipelineCacheService)
        : base("GtaoTemporalPass", "gtao_temporal.comp.spv", context,
            swapchain, bindlessHeap,
            [
                Binding(0, DescriptorType.CombinedImageSampler),
                Binding(1, DescriptorType.CombinedImageSampler),
                Binding(2, DescriptorType.CombinedImageSampler),
                Binding(3, DescriptorType.CombinedImageSampler),
                Binding(4, DescriptorType.CombinedImageSampler),
                Binding(5, DescriptorType.StorageImage),
                Binding(6, DescriptorType.StorageImage)
            ],
            2,
            (uint)Marshal.SizeOf<GPUGtaoTemporalPushConstants>(),
            pipelineCacheService)
    {
        _renderTargets = renderTargets;
        _settings = settings;
        _historyState = historyState;
    }

    public override bool ShouldExecute(int frameIndex,
        SceneRenderingData sceneData) =>
        sceneData.AmbientOcclusionEnabled &&
        sceneData.AmbientOcclusionMode == AmbientOcclusionMode.Gtao;

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData)
    {
        int writeIndex = frameIndex & 1;
        Extent2D extent = _renderTargets.GtaoRaw.Extent;
        bool historyValid = _historyState.CanReuse(sceneData,
            _settings.AmbientOcclusion, extent);
        sceneData.GtaoHistoryValid = historyValid ? 1 : 0;
        var push = new GPUGtaoTemporalPushConstants
        {
            Dimensions = new Vector2(extent.Width, extent.Height),
            SceneDimensions = new Vector2(sceneData.ScreenWidth,
                sceneData.ScreenHeight),
            HistoryValid = historyValid ? 1u : 0u,
            MaximumHistoryAge = 32u,
            FrameIndex = sceneData.TemporalSampleIndex,
            DepthThresholdScale = 0.03f,
            NormalThreshold = 0.85f,
            StableHistoryWeight = 0.92f,
            MotionRejectionScale = 0.15f
        };
        BindAndPush(cmd, writeIndex, push);
        _context.Api.CmdDispatch(cmd, (extent.Width + 7u) / 8u,
            (extent.Height + 7u) / 8u, 1u);
        History(writeIndex).TransitionToComputeShaderRead(cmd);
        Geometry(writeIndex).TransitionToComputeShaderRead(cmd);
        _historyState.Commit(sceneData, _settings.AmbientOcclusion,
            extent);
    }

    public override void OnSwapchainRecreated()
    {
        _historyState.Reset();
        base.OnSwapchainRecreated();
    }

    protected override void RewriteDescriptors()
    {
        for (int writeIndex = 0; writeIndex < 2; writeIndex++)
        {
            int readIndex = 1 - writeIndex;
            WriteImageDescriptors(writeIndex,
                new GtaoImageDescriptor(0,
                    DescriptorType.CombinedImageSampler,
                    _renderTargets.GtaoRaw.View,
                    _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(1,
                    DescriptorType.CombinedImageSampler,
                    _renderTargets.GtaoCurrentGeometry.View,
                    _bindlessHeap.HiZSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(2,
                    DescriptorType.CombinedImageSampler,
                    _renderTargets.MotionVectors.View,
                    _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(3,
                    DescriptorType.CombinedImageSampler,
                    History(readIndex).View,
                    _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(4,
                    DescriptorType.CombinedImageSampler,
                    Geometry(readIndex).View,
                    _bindlessHeap.HiZSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(5, DescriptorType.StorageImage,
                    History(writeIndex).View, default,
                    ImageLayout.General),
                new GtaoImageDescriptor(6, DescriptorType.StorageImage,
                    Geometry(writeIndex).View, default,
                    ImageLayout.General));
        }
    }

    private RenderTarget History(int index) => index == 0
        ? _renderTargets.GtaoHistory0
        : _renderTargets.GtaoHistory1;

    private RenderTarget Geometry(int index) => index == 0
        ? _renderTargets.GtaoGeometryHistory0
        : _renderTargets.GtaoGeometryHistory1;
}

internal sealed unsafe class GtaoSpatialPass : GtaoComputePassBase
{
    private readonly RenderTargetManager _renderTargets;
    private readonly RenderSettings _settings;

    public GtaoSpatialPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        RenderSettings settings)
        : this(context, swapchain, bindlessHeap, renderTargets, settings,
            pipelineCacheService: null)
    {
    }

    internal GtaoSpatialPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        RenderSettings settings,
        GiPipelineCacheService? pipelineCacheService)
        : base("GtaoSpatialPass", "gtao_spatial.comp.spv", context,
            swapchain, bindlessHeap,
            [
                Binding(0, DescriptorType.CombinedImageSampler),
                Binding(1, DescriptorType.CombinedImageSampler),
                Binding(2, DescriptorType.CombinedImageSampler),
                Binding(3, DescriptorType.StorageImage),
                Binding(4, DescriptorType.StorageImage),
                Binding(5, DescriptorType.StorageImage)
            ],
            2,
            (uint)Marshal.SizeOf<GPUGtaoSpatialPushConstants>(),
            pipelineCacheService)
    {
        _renderTargets = renderTargets;
        _settings = settings;
    }

    public override bool ShouldExecute(int frameIndex,
        SceneRenderingData sceneData)
    {
        bool execute = sceneData.AmbientOcclusionEnabled &&
            sceneData.AmbientOcclusionMode == AmbientOcclusionMode.Gtao;
        if (!execute)
            return false;
        _bindlessHeap.RegisterTexture(BindlessIndex.AmbientOcclusionBlurredTexture,
            _renderTargets.AmbientOcclusionBlurred.View,
            _bindlessHeap.ScreenSampler, ImageLayout.ShaderReadOnlyOptimal);
        _bindlessHeap.RegisterTexture(BindlessIndex.GtaoFilteredTexture,
            _renderTargets.GtaoFiltered.View,
            _bindlessHeap.ScreenSampler, ImageLayout.ShaderReadOnlyOptimal);
        _bindlessHeap.RegisterTexture(BindlessIndex.GtaoDebugTexture,
            _renderTargets.GtaoSpatialScratch.View,
            _bindlessHeap.ScreenSampler, ImageLayout.ShaderReadOnlyOptimal);
        return true;
    }

    public override void Execute(CommandBuffer cmd, int frameIndex,
        SceneRenderingData sceneData)
    {
        int historyIndex = frameIndex & 1;
        Extent2D sourceExtent = _renderTargets.GtaoRaw.Extent;
        Extent2D outputExtent = _renderTargets.GtaoFiltered.Extent;
        var push = new GPUGtaoSpatialPushConstants
        {
            SourceDimensions = new Vector2(sourceExtent.Width,
                sourceExtent.Height),
            OutputDimensions = new Vector2(outputExtent.Width,
                outputExtent.Height),
            DepthSigma = _settings.AmbientOcclusion.DepthSigma,
            NormalSigma = _settings.AmbientOcclusion.NormalSigma,
            Radius = 2u,
            DebugView = (uint)_settings.AmbientOcclusion.DebugView
        };
        BindAndPush(cmd, historyIndex, push);
        _context.Api.CmdDispatch(cmd, (outputExtent.Width + 7u) / 8u,
            (outputExtent.Height + 7u) / 8u, 1u);
        _renderTargets.GtaoFiltered.TransitionToShaderRead(cmd);
        _renderTargets.GtaoSpatialScratch.TransitionToShaderRead(cmd);
        _renderTargets.AmbientOcclusionBlurred.TransitionToShaderRead(cmd);
    }

    protected override void RewriteDescriptors()
    {
        for (int historyIndex = 0; historyIndex < 2; historyIndex++)
        {
            RenderTarget history = historyIndex == 0
                ? _renderTargets.GtaoHistory0
                : _renderTargets.GtaoHistory1;
            RenderTarget geometry = historyIndex == 0
                ? _renderTargets.GtaoGeometryHistory0
                : _renderTargets.GtaoGeometryHistory1;
            WriteImageDescriptors(historyIndex,
                new GtaoImageDescriptor(0,
                    DescriptorType.CombinedImageSampler,
                    history.View, _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(1,
                    DescriptorType.CombinedImageSampler,
                    geometry.View, _bindlessHeap.HiZSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(2,
                    DescriptorType.CombinedImageSampler,
                    _renderTargets.GtaoRaw.View,
                    _bindlessHeap.ScreenSampler,
                    ImageLayout.ShaderReadOnlyOptimal),
                new GtaoImageDescriptor(3, DescriptorType.StorageImage,
                    _renderTargets.GtaoFiltered.View, default,
                    ImageLayout.General),
                new GtaoImageDescriptor(4, DescriptorType.StorageImage,
                    _renderTargets.AmbientOcclusionBlurred.View, default,
                    ImageLayout.General),
                new GtaoImageDescriptor(5, DescriptorType.StorageImage,
                    _renderTargets.GtaoSpatialScratch.View, default,
                    ImageLayout.General));
        }
    }
}
