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
/// Builds one or more roughness mips of the procedural environment per frame.
/// The manager keeps the target immutable for the entire build, then crossfades
/// complete cubemaps so readers never observe partially updated mip chains.
/// </summary>
internal sealed unsafe class EnvironmentPrefilterPass : RenderPassBase
{
    private const int MaximumMipsPerFrame = 5;
    private const int DescriptorSetCount =
        RenderingConstants.FramesInFlight * MaximumMipsPerFrame;

    private readonly EnvironmentManager _environment;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly nint _entryPointName;
    private readonly DescriptorSet[] _outputSets = new DescriptorSet[DescriptorSetCount];
    private DescriptorSetLayout _outputSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _float16Pipeline;
    private VkPipeline _float32Pipeline;
    private bool _pipelinesPrepared;

    public EnvironmentPrefilterPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        EnvironmentManager environment)
        : this(
            context,
            swapchain,
            bindlessHeap,
            environment,
            pipelineCacheService: null)
    {
    }

    internal EnvironmentPrefilterPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        EnvironmentManager environment,
        GiPipelineCacheService? pipelineCacheService)
        : base("EnvironmentPrefilterPass", context, swapchain, bindlessHeap)
    {
        _environment = environment ??
            throw new ArgumentNullException(nameof(environment));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override RenderGraphQueueIntent QueueIntent =>
        RenderGraphQueueIntent.Compute;

    // Keep this work on the graphics queue. The cubemap is consumed by both
    // fragment and DDGI compute passes in the same frame and one tiny mip does
    // not justify cross-queue ownership transfers.
    public override bool SupportsAsyncCompute => false;

    public override void Initialize()
    {
        CreateDescriptorSetLayout();
        CreateDescriptorPoolAndSets();
        CreatePipelineLayout();
        if (RendererBuildConfiguration.PipelineStartupMode ==
                RendererPipelineStartupMode.Exhaustive ||
            _environment.PrefilterPipelinesRequired)
        {
            PreparePipelines();
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        if (!_environment.HasPendingPrefilterWork)
            return false;
        PreparePipelines();
        return true;
    }

    internal bool IsPrepared => _pipelinesPrepared;

    internal void PreparePipelines()
    {
        if (_pipelinesPrepared)
            return;
        CreatePipelineCache();
        _float16Pipeline = CreatePipeline("environment_prefilter.comp.spv");
        _float32Pipeline = CreatePipeline("environment_prefilter_float.comp.spv");
        _pipelinesPrepared = true;
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        int workLimit = Math.Clamp(
            _environment.PrefilterMipsPerFrame,
            1,
            MaximumMipsPerFrame);
        for (int workIndex = 0; workIndex < workLimit; workIndex++)
        {
            if (!_environment.TryGetNextPrefilterWork(out EnvironmentPrefilterWork work))
                break;

            VkPipeline pipeline = work.Format switch
            {
                Format.R16G16B16A16Sfloat => _float16Pipeline,
                Format.R32G32B32A32Sfloat => _float32Pipeline,
                _ => throw new NotSupportedException(
                    $"Environment prefilter format {work.Format} is not supported.")
            };
            DescriptorSet outputSet = _outputSets[
                frameIndex * MaximumMipsPerFrame + workIndex];
            UpdateOutputSet(outputSet, work.StorageView);
            TransitionForWrite(commandBuffer, work);

            _context.Api.CmdBindPipeline(
                commandBuffer,
                PipelineBindPoint.Compute,
                pipeline);
            DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
            DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &storageSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                1,
                1,
                &textureSet,
                0,
                null);
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                2,
                1,
                &outputSet,
                0,
                null);

            var push = new EnvironmentPrefilterPushConstants
            {
                OutputSize = work.Size,
                MipLevel = work.MipLevel,
                MipCount = _environment.PrefilteredMipCount,
                SampleCount = work.Roughness <= 0.0001f ? 1u : 64u,
                Roughness = work.Roughness
            };
            _context.Api.CmdPushConstants(
                commandBuffer,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<EnvironmentPrefilterPushConstants>(),
                &push);
            _context.Api.CmdDispatch(
                commandBuffer,
                (work.Size + 7u) / 8u,
                (work.Size + 7u) / 8u,
                6u);

            TransitionForSampling(commandBuffer, work);
            _environment.CompletePrefilterWork(work);
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        if (_float16Pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _float16Pipeline, null);
        if (_float32Pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _float32Pipeline, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_outputSetLayout.Handle != 0)
        {
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _outputSetLayout,
                null);
        }
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);

        _float16Pipeline = default;
        _float32Pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
        _descriptorPool = default;
        _outputSetLayout = default;
        _pipelinesPrepared = false;
        Array.Clear(_outputSets);
    }

    private void CreateDescriptorSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &info,
            null,
            out _outputSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create environment prefilter descriptor layout.",
                result);
        }
    }

    private void CreateDescriptorPoolAndSets()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = DescriptorSetCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = DescriptorSetCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create environment prefilter descriptor pool.", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[DescriptorSetCount];
        for (int index = 0; index < DescriptorSetCount; index++)
            layouts[index] = _outputSetLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = DescriptorSetCount,
            PSetLayouts = layouts
        };
        fixed (DescriptorSet* sets = _outputSets)
        {
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocate,
                sets);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate environment prefilter descriptor sets.", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            _outputSetLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<EnvironmentPrefilterPushConstants>()
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
            throw new VulkanException("Failed to create environment prefilter pipeline layout.", result);
    }

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService != null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
            return;
        }

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
            throw new VulkanException("Failed to create environment prefilter pipeline cache.", result);
    }

    private VkPipeline CreatePipeline(string shaderName)
    {
        long pipelineStart =
            _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
        ShaderModule shader = default;
        try
        {
            shader = ShaderModuleLoader.Load(_context, shaderName);
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
                throw new VulkanException($"Failed to create {shaderName} pipeline.", result);
            return pipeline;
        }
        finally
        {
            if (shader.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shader, null);
            _pipelineCacheService?.EndPipelineCreation(
                "EnvironmentPrefilter." + shaderName,
                pipelineStart);
        }
    }

    private void UpdateOutputSet(DescriptorSet set, ImageView view)
    {
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = view,
            ImageLayout = ImageLayout.General
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    private void TransitionForWrite(
        CommandBuffer commandBuffer,
        in EnvironmentPrefilterWork work)
    {
        PipelineStageFlags2 sourceStage = work.OldLayout == ImageLayout.Undefined
            ? PipelineStageFlags2.TopOfPipeBit
            : PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
        AccessFlags2 sourceAccess = work.OldLayout == ImageLayout.Undefined
            ? AccessFlags2.None
            : AccessFlags2.ShaderSampledReadBit;
        ExecuteBarrier(
            commandBuffer,
            work,
            sourceStage,
            sourceAccess,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            work.OldLayout,
            ImageLayout.General);
    }

    private void TransitionForSampling(
        CommandBuffer commandBuffer,
        in EnvironmentPrefilterWork work)
    {
        ExecuteBarrier(
            commandBuffer,
            work,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.General,
            ImageLayout.ShaderReadOnlyOptimal);
    }

    private void ExecuteBarrier(
        CommandBuffer commandBuffer,
        in EnvironmentPrefilterWork work,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = work.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = work.MipLevel,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 6
            }
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct EnvironmentPrefilterPushConstants
    {
        public uint OutputSize;
        public uint MipLevel;
        public uint MipCount;
        public uint SampleCount;
        public float Roughness;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
    }
}
