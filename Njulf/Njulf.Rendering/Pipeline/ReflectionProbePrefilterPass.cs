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
/// Filters one complete local-probe scratch mip at a time with the same GGX sample sequence as
/// the procedural environment prefilter. The scheduler is deliberately queried by work kind, so
/// this pass cannot consume a face unit or begin before the six raw faces are complete.
/// </summary>
public sealed unsafe class ReflectionProbePrefilterPass : RenderPassBase
{
    private const int MaximumMipsPerFrame = 16;
    private const int DescriptorSetCount = RenderingConstants.FramesInFlight * MaximumMipsPerFrame;

    private readonly ReflectionProbeManager _manager;
    private readonly ReflectionSettings _settings;
    private readonly nint _entryPointName;
    private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[DescriptorSetCount];
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;

    public ReflectionProbePrefilterPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        ReflectionProbeManager manager,
        ReflectionSettings settings)
        : base("ReflectionProbePrefilterPass", context, swapchain, bindlessHeap)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Graphics;
    public override bool SupportsAsyncCompute => false;

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData) =>
        _pipeline.Handle != 0 &&
        _settings.Enabled &&
        _settings.MaxProbePrefilterMipsPerFrame > 0 &&
        _manager.HasCaptureWork(ReflectionProbeWorkKind.PrefilterMip);

    public override void Initialize()
    {
        CreateDescriptorSetLayout();
        CreateDescriptorPoolAndSets();
        CreatePipelineLayout();
        CreatePipelineCache();
        _pipeline = CreatePipeline();
    }

    public override void Execute(CommandBuffer commandBuffer, int frameIndex, SceneRenderingData sceneData)
    {
        if (_pipeline.Handle == 0)
            return;

        int unitLimit = Math.Min(
            Math.Clamp(_settings.MaxProbePrefilterMipsPerFrame, 0, MaximumMipsPerFrame),
            MaximumMipsPerFrame);
        for (int unit = 0; unit < unitLimit; unit++)
        {
            if (!_manager.TryAcquirePrefilterMip(out ReflectionProbeWork work))
                break;

            try
            {
                ReflectionPrefilterMipWork mip = ReflectionPrefilterContract.GetMipWork(
                    work.Mip,
                    _manager.ProbeResolution,
                    _manager.ProbeMipCount);
                _manager.PreparePrefilterMip(commandBuffer, work);
                DescriptorSet descriptorSet = _descriptorSets[
                    frameIndex * MaximumMipsPerFrame + unit];
                UpdateDescriptorSet(descriptorSet, _manager.ScratchCaptureView, _manager.GetScratchMipView(work.Mip));

                _context.Api.CmdBindPipeline(
                    commandBuffer,
                    PipelineBindPoint.Compute,
                    _pipeline);
                BindBindlessStorageAndTextures(
                    commandBuffer,
                    _pipelineLayout,
                    PipelineBindPoint.Compute);
                _context.Api.CmdBindDescriptorSets(
                    commandBuffer,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    2,
                    1,
                    &descriptorSet,
                    0,
                    null);

                ReflectionPrefilterPushConstants push = new(
                    mip.Resolution,
                    checked((uint)mip.SampleCount),
                    mip.Roughness,
                    2.0f / Math.Max(_manager.ProbeResolution, 1U));
                _context.Api.CmdPushConstants(
                    commandBuffer,
                    _pipelineLayout,
                    ShaderStageFlags.ComputeBit,
                    0,
                    (uint)Marshal.SizeOf<ReflectionPrefilterPushConstants>(),
                    &push);
                _context.Api.CmdDispatch(
                    commandBuffer,
                    (mip.Resolution + 7U) / 8U,
                    (mip.Resolution + 7U) / 8U,
                    6U);
                _manager.CompletePrefilterMipRecording(commandBuffer, work);
                _manager.CompleteCaptureWork(work);
            }
            catch
            {
                _manager.FailCaptureWork(work, retry: true);
                throw;
            }
        }
    }

    public override void Cleanup()
    {
        if (_pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_descriptorSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(_context.Device, _descriptorSetLayout, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);

        _pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
        _descriptorPool = default;
        _descriptorSetLayout = default;
        Array.Clear(_descriptorSets);
    }

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &info,
            null,
            out _descriptorSetLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create reflection prefilter descriptor layout.", result);
    }

    private void CreateDescriptorPoolAndSets()
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = DescriptorSetCount
        };
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = DescriptorSetCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
            MaxSets = DescriptorSetCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create reflection prefilter descriptor pool.", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[DescriptorSetCount];
        for (int index = 0; index < DescriptorSetCount; index++)
            layouts[index] = _descriptorSetLayout;
        var allocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = DescriptorSetCount,
            PSetLayouts = layouts
        };
        fixed (DescriptorSet* sets = _descriptorSets)
        {
            result = _context.Api.AllocateDescriptorSets(_context.Device, &allocation, sets);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate reflection prefilter descriptor sets.", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            _descriptorSetLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<ReflectionPrefilterPushConstants>()
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &info,
            null,
            out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create reflection prefilter pipeline layout.", result);
    }

    private void CreatePipelineCache()
    {
        var info = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device,
            &info,
            null,
            out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create reflection prefilter pipeline cache.", result);
    }

    private VkPipeline CreatePipeline()
    {
        ShaderModule shader = default;
        try
        {
            shader = ShaderModuleLoader.Load(_context, "reflection_probe_prefilter.comp.spv");
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shader,
                PName = (byte*)_entryPointName
            };
            var info = new ComputePipelineCreateInfo
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
                &info,
                null,
                out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException("Failed to create reflection prefilter pipeline.", result);
            return pipeline;
        }
        finally
        {
            if (shader.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shader, null);
        }
    }

    private void UpdateDescriptorSet(DescriptorSet set, ImageView sourceView, ImageView outputView)
    {
        DescriptorImageInfo* imageInfos = stackalloc DescriptorImageInfo[2];
        imageInfos[0] = new DescriptorImageInfo
        {
            Sampler = _manager.ScratchSampler,
            ImageView = sourceView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        imageInfos[1] = new DescriptorImageInfo
        {
            ImageView = outputView,
            ImageLayout = ImageLayout.General
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = imageInfos
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = imageInfos + 1
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct ReflectionPrefilterPushConstants
    {
        public readonly uint OutputSize;
        public readonly uint SampleCount;
        public readonly float Roughness;
        public readonly float SourceTexelSize;

        public ReflectionPrefilterPushConstants(
            uint outputSize,
            uint sampleCount,
            float roughness,
            float sourceTexelSize)
        {
            OutputSize = outputSize;
            SampleCount = sampleCount;
            Roughness = roughness;
            SourceTexelSize = sourceTexelSize;
        }
    }
}
