using System;
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
/// Captures automatically discovered planar receivers with the ordinary
/// forward material path. Between captures, the prior color/depth pair is
/// depth-reprojected into the current reflected camera. Every published bank
/// receives a GGX roughness chain before hybrid and transparent consumers run.
/// </summary>
public sealed unsafe class AutomaticPlanarReflectionPass : RenderPassBase
{
    private const int MaximumMipsPerCapture = 16;
    private const int ReprojectDescriptorSetCount =
        RenderingConstants.FramesInFlight *
        AutomaticPlanarReflectionManager.MaximumCaptureCount;
    private const int PrefilterDescriptorSetCount =
        ReprojectDescriptorSetCount * MaximumMipsPerCapture;

    private readonly AutomaticPlanarReflectionManager _manager;
    private readonly ForwardPlusPass _forwardPass;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly nint _entryPointName;
    private readonly DescriptorSet[] _reprojectDescriptorSets =
        new DescriptorSet[ReprojectDescriptorSetCount];
    private readonly DescriptorSet[] _prefilterDescriptorSets =
        new DescriptorSet[PrefilterDescriptorSetCount];
    private DescriptorSetLayout _reprojectSetLayout;
    private DescriptorSetLayout _prefilterSetLayout;
    private DescriptorPool _reprojectDescriptorPool;
    private DescriptorPool _prefilterDescriptorPool;
    private PipelineLayout _reprojectPipelineLayout;
    private PipelineLayout _prefilterPipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _reprojectPipeline;
    private VkPipeline _prefilterPipeline;

    public AutomaticPlanarReflectionPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        AutomaticPlanarReflectionManager manager,
        ForwardPlusPass forwardPass,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("AutomaticPlanarReflectionPass", context, swapchain,
            bindlessHeap)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _forwardPass = forwardPass ??
            throw new ArgumentNullException(nameof(forwardPass));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");
    }

    public override RenderGraphQueueIntent QueueIntent =>
        RenderGraphQueueIntent.Graphics;

    public override bool SupportsAsyncCompute => false;

    public override string AsyncComputeReason =>
        "Automatic planar updates combine dynamic rendering and compute reprojection on the graphics queue.";

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData) =>
        _reprojectPipeline.Handle != 0 &&
        _prefilterPipeline.Handle != 0 &&
        sceneData.AutomaticPlanarReflectionActive &&
        _manager.HasCaptureWork;

    public override void Initialize()
    {
        CreateDescriptorSetLayouts();
        CreateDescriptorPoolsAndSets();
        _reprojectPipelineLayout = CreatePipelineLayout(
            _reprojectSetLayout,
            (uint)Marshal.SizeOf<AutomaticPlanarReprojectPushConstants>());
        _prefilterPipelineLayout = CreatePipelineLayout(
            _prefilterSetLayout,
            (uint)Marshal.SizeOf<AutomaticPlanarPrefilterPushConstants>());
        if (_pipelineCacheService != null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
        }
        else
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };
            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create automatic planar pipeline cache.",
                    result);
            }
        }
        _reprojectPipeline = CreateComputePipeline(
            "automatic_planar_reproject.comp.spv",
            _reprojectPipelineLayout);
        _prefilterPipeline = CreateComputePipeline(
            "automatic_planar_prefilter.comp.spv",
            _prefilterPipelineLayout);
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (_reprojectPipeline.Handle == 0 ||
            _prefilterPipeline.Handle == 0)
        {
            return;
        }

        foreach (AutomaticPlanarPreparedCapture capture in
                 _manager.PreparedCaptures)
        {
            if (capture.Action == AutomaticPlanarCaptureAction.Capture)
            {
                RecordCapture(
                    commandBuffer,
                    frameIndex,
                    sceneData,
                    capture);
            }
            else if (capture.Action ==
                     AutomaticPlanarCaptureAction.Reproject)
            {
                RecordReprojection(commandBuffer, frameIndex, capture);
            }
            else
            {
                continue;
            }

            GenerateRoughnessMipChain(
                commandBuffer,
                frameIndex,
                capture);
        }
    }

    private void RecordCapture(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        AutomaticPlanarPreparedCapture capture)
    {
        AutomaticPlanarCaptureResource resource = capture.Resource;
        RenderTarget[] destination = resource.GetColorMips(
            capture.DestinationBank);
        destination[0].TransitionToColorAttachment(commandBuffer);
        resource.Depth.TransitionToDepthAttachment(commandBuffer);
        _forwardPass.RecordAutomaticPlanarCapture(
            commandBuffer,
            frameIndex,
            sceneData,
            capture.View,
            destination[0].View,
            resource.Depth.View);
        resource.Depth.TransitionToDepthReadOnly(commandBuffer);

        PrepareReprojectionResources(
            commandBuffer,
            capture,
            clearDestinationDepth: false);
        DescriptorSet descriptorSet = ResolveReprojectDescriptorSet(
            frameIndex,
            capture.Slot);
        UpdateReprojectDescriptorSet(descriptorSet, capture);
        DispatchReprojection(
            commandBuffer,
            frameIndex,
            descriptorSet,
            capture,
            AutomaticPlanarReprojectionMode.CaptureDepth);
        PublishStorageWrite(commandBuffer,
            resource.GetDepthHistory(capture.DestinationBank));
        PublishStorageWrite(commandBuffer, destination[0]);
        destination[0].TransitionToShaderRead(commandBuffer);
    }

    private void RecordReprojection(
        CommandBuffer commandBuffer,
        int frameIndex,
        AutomaticPlanarPreparedCapture capture)
    {
        AutomaticPlanarCaptureResource resource = capture.Resource;
        PrepareReprojectionResources(
            commandBuffer,
            capture,
            clearDestinationDepth: true);
        DescriptorSet descriptorSet = ResolveReprojectDescriptorSet(
            frameIndex,
            capture.Slot);
        UpdateReprojectDescriptorSet(descriptorSet, capture);

        DispatchReprojection(
            commandBuffer,
            frameIndex,
            descriptorSet,
            capture,
            AutomaticPlanarReprojectionMode.ScatterDepth);
        PublishStorageWrite(
            commandBuffer,
            resource.GetDepthHistory(capture.DestinationBank));
        DispatchReprojection(
            commandBuffer,
            frameIndex,
            descriptorSet,
            capture,
            AutomaticPlanarReprojectionMode.ResolveColor);
        RenderTarget destination = resource.GetColorMips(
            capture.DestinationBank)[0];
        PublishStorageWrite(commandBuffer, destination);
        destination.TransitionToShaderRead(commandBuffer);
    }

    private void PrepareReprojectionResources(
        CommandBuffer commandBuffer,
        AutomaticPlanarPreparedCapture capture,
        bool clearDestinationDepth)
    {
        AutomaticPlanarCaptureResource resource = capture.Resource;
        resource.GetColorMips(capture.SourceBank)[0]
            .TransitionToShaderRead(commandBuffer);
        resource.Depth.TransitionToDepthReadOnly(commandBuffer);
        resource.GetDepthHistory(capture.SourceBank)
            .TransitionToStorageReadWrite(commandBuffer);
        RenderTarget destinationDepth = resource.GetDepthHistory(
            capture.DestinationBank);
        if (clearDestinationDepth)
            ClearDepthHistory(commandBuffer, destinationDepth);
        else
            destinationDepth.TransitionToStorageReadWrite(commandBuffer);
        resource.GetColorMips(capture.DestinationBank)[0]
            .TransitionToStorageReadWrite(commandBuffer);
    }

    private void ClearDepthHistory(
        CommandBuffer commandBuffer,
        RenderTarget target)
    {
        target.TransitionToLayout(
            commandBuffer,
            ImageLayout.General,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            force: true);
        ClearColorValue zero = default;
        var range = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            LevelCount = 1u,
            LayerCount = 1u
        };
        _context.Api.CmdClearColorImage(
            commandBuffer,
            target.Image,
            ImageLayout.General,
            &zero,
            1u,
            &range);
        target.TransitionToLayout(
            commandBuffer,
            ImageLayout.General,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            force: true);
    }

    private void DispatchReprojection(
        CommandBuffer commandBuffer,
        int frameIndex,
        DescriptorSet descriptorSet,
        AutomaticPlanarPreparedCapture capture,
        AutomaticPlanarReprojectionMode mode)
    {
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _reprojectPipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _reprojectPipelineLayout,
            PipelineBindPoint.Compute);
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _reprojectPipelineLayout,
            2u,
            1u,
            &descriptorSet,
            0u,
            null);
        var push = new AutomaticPlanarReprojectPushConstants(
            checked((uint)frameIndex),
            checked((uint)capture.Slot),
            mode,
            capture.View.Width,
            capture.View.Height);
        _context.Api.CmdPushConstants(
            commandBuffer,
            _reprojectPipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<AutomaticPlanarReprojectPushConstants>(),
            &push);
        _context.Api.CmdDispatch(
            commandBuffer,
            (capture.View.Width + 7u) / 8u,
            (capture.View.Height + 7u) / 8u,
            1u);
    }

    private void GenerateRoughnessMipChain(
        CommandBuffer commandBuffer,
        int frameIndex,
        AutomaticPlanarPreparedCapture capture)
    {
        RenderTarget[] colorMips = capture.Resource.GetColorMips(
            capture.DestinationBank);
        colorMips[0].TransitionToShaderRead(commandBuffer);
        int boundedMipCount = Math.Min(
            colorMips.Length,
            MaximumMipsPerCapture);
        for (int mip = 1; mip < boundedMipCount; mip++)
        {
            RenderTarget destination = colorMips[mip];
            destination.TransitionToStorageWrite(commandBuffer);
            DescriptorSet descriptorSet = ResolvePrefilterDescriptorSet(
                frameIndex,
                capture.Slot,
                mip);
            UpdatePrefilterDescriptorSet(
                descriptorSet,
                colorMips[0].View,
                destination.View);

            _context.Api.CmdBindPipeline(
                commandBuffer,
                PipelineBindPoint.Compute,
                _prefilterPipeline);
            BindBindlessStorageAndTextures(
                commandBuffer,
                _prefilterPipelineLayout,
                PipelineBindPoint.Compute);
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _prefilterPipelineLayout,
                2u,
                1u,
                &descriptorSet,
                0u,
                null);
            float roughness = colorMips.Length <= 1
                ? 0.0f
                : mip / (float)(colorMips.Length - 1);
            uint sampleCount = roughness < 0.2f
                ? 16u
                : roughness < 0.6f
                    ? 32u
                    : 64u;
            var push = new AutomaticPlanarPrefilterPushConstants(
                destination.Extent.Width,
                destination.Extent.Height,
                sampleCount,
                checked((uint)mip),
                roughness,
                1.0f / Math.Max(capture.Resource.Width, 1u),
                1.0f / Math.Max(capture.Resource.Height, 1u),
                Math.Min(
                    0.25f,
                    0.01f + roughness * roughness * 0.20f));
            _context.Api.CmdPushConstants(
                commandBuffer,
                _prefilterPipelineLayout,
                ShaderStageFlags.ComputeBit,
                0u,
                (uint)Marshal.SizeOf<AutomaticPlanarPrefilterPushConstants>(),
                &push);
            _context.Api.CmdDispatch(
                commandBuffer,
                (destination.Extent.Width + 7u) / 8u,
                (destination.Extent.Height + 7u) / 8u,
                1u);
            PublishStorageWrite(commandBuffer, destination);
            destination.TransitionToShaderRead(commandBuffer);
        }
    }

    private void PublishStorageWrite(
        CommandBuffer commandBuffer,
        RenderTarget target) => target.TransitionToLayout(
        commandBuffer,
        ImageLayout.General,
        PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit,
        PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderStorageWriteBit,
        force: true);

    private DescriptorSet ResolveReprojectDescriptorSet(
        int frameIndex,
        int slot)
    {
        int bank = frameIndex % RenderingConstants.FramesInFlight;
        return _reprojectDescriptorSets[
            bank * AutomaticPlanarReflectionManager.MaximumCaptureCount +
            slot];
    }

    private DescriptorSet ResolvePrefilterDescriptorSet(
        int frameIndex,
        int slot,
        int mip)
    {
        int bank = frameIndex % RenderingConstants.FramesInFlight;
        return _prefilterDescriptorSets[
            (bank * AutomaticPlanarReflectionManager.MaximumCaptureCount +
                slot) * MaximumMipsPerCapture + mip];
    }

    private void UpdateReprojectDescriptorSet(
        DescriptorSet set,
        AutomaticPlanarPreparedCapture capture)
    {
        AutomaticPlanarCaptureResource resource = capture.Resource;
        DescriptorImageInfo* infos = stackalloc DescriptorImageInfo[5];
        infos[0] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.ScreenSampler,
            ImageView = resource.GetColorMips(capture.SourceBank)[0].View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        infos[1] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.ScreenSampler,
            ImageView = resource.Depth.View,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
        };
        infos[2] = new DescriptorImageInfo
        {
            ImageView = resource.GetDepthHistory(capture.SourceBank).View,
            ImageLayout = ImageLayout.General
        };
        infos[3] = new DescriptorImageInfo
        {
            ImageView = resource.GetDepthHistory(capture.DestinationBank).View,
            ImageLayout = ImageLayout.General
        };
        infos[4] = new DescriptorImageInfo
        {
            ImageView = resource.GetColorMips(capture.DestinationBank)[0].View,
            ImageLayout = ImageLayout.General
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[5];
        for (int binding = 0; binding < 5; binding++)
        {
            writes[binding] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = checked((uint)binding),
                DescriptorCount = 1u,
                DescriptorType = binding < 2
                    ? DescriptorType.CombinedImageSampler
                    : DescriptorType.StorageImage,
                PImageInfo = infos + binding
            };
        }
        _context.Api.UpdateDescriptorSets(
            _context.Device,
            5u,
            writes,
            0u,
            null);
    }

    private void UpdatePrefilterDescriptorSet(
        DescriptorSet set,
        ImageView source,
        ImageView destination)
    {
        DescriptorImageInfo* infos = stackalloc DescriptorImageInfo[2];
        infos[0] = new DescriptorImageInfo
        {
            Sampler = _bindlessHeap.ScreenSampler,
            ImageView = source,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        infos[1] = new DescriptorImageInfo
        {
            ImageView = destination,
            ImageLayout = ImageLayout.General
        };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0u,
            DescriptorCount = 1u,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = infos
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1u,
            DescriptorCount = 1u,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = infos + 1
        };
        _context.Api.UpdateDescriptorSets(
            _context.Device,
            2u,
            writes,
            0u,
            null);
    }

    private void CreateDescriptorSetLayouts()
    {
        DescriptorSetLayoutBinding* reprojectBindings =
            stackalloc DescriptorSetLayoutBinding[5];
        for (int binding = 0; binding < 5; binding++)
        {
            reprojectBindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = checked((uint)binding),
                DescriptorType = binding < 2
                    ? DescriptorType.CombinedImageSampler
                    : DescriptorType.StorageImage,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }
        var reprojectInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 5u,
            PBindings = reprojectBindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &reprojectInfo,
            null,
            out _reprojectSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create automatic planar reprojection descriptor layout.",
                result);
        }

        DescriptorSetLayoutBinding* prefilterBindings =
            stackalloc DescriptorSetLayoutBinding[2];
        prefilterBindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0u,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        prefilterBindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1u,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var prefilterInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2u,
            PBindings = prefilterBindings
        };
        result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &prefilterInfo,
            null,
            out _prefilterSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create automatic planar prefilter descriptor layout.",
                result);
        }
    }

    private void CreateDescriptorPoolsAndSets()
    {
        DescriptorPoolSize* reprojectSizes = stackalloc DescriptorPoolSize[2];
        reprojectSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = checked(
                (uint)(2 * ReprojectDescriptorSetCount))
        };
        reprojectSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = checked(
                (uint)(3 * ReprojectDescriptorSetCount))
        };
        _reprojectDescriptorPool = CreateDescriptorPool(
            reprojectSizes,
            2u,
            ReprojectDescriptorSetCount,
            "reprojection");
        AllocateDescriptorSets(
            _reprojectDescriptorPool,
            _reprojectSetLayout,
            _reprojectDescriptorSets,
            "reprojection");

        DescriptorPoolSize* prefilterSizes = stackalloc DescriptorPoolSize[2];
        prefilterSizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = PrefilterDescriptorSetCount
        };
        prefilterSizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = PrefilterDescriptorSetCount
        };
        _prefilterDescriptorPool = CreateDescriptorPool(
            prefilterSizes,
            2u,
            PrefilterDescriptorSetCount,
            "prefilter");
        AllocateDescriptorSets(
            _prefilterDescriptorPool,
            _prefilterSetLayout,
            _prefilterDescriptorSets,
            "prefilter");
    }

    private DescriptorPool CreateDescriptorPool(
        DescriptorPoolSize* sizes,
        uint sizeCount,
        int setCount,
        string label)
    {
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = sizeCount,
            PPoolSizes = sizes,
            MaxSets = checked((uint)setCount)
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &info,
            null,
            out DescriptorPool pool);
        if (result != Result.Success)
        {
            throw new VulkanException(
                $"Failed to create automatic planar {label} descriptor pool.",
                result);
        }
        return pool;
    }

    private void AllocateDescriptorSets(
        DescriptorPool pool,
        DescriptorSetLayout layout,
        DescriptorSet[] sets,
        string label)
    {
        var layouts = new DescriptorSetLayout[sets.Length];
        Array.Fill(layouts, layout);
        fixed (DescriptorSetLayout* layoutPointer = layouts)
        fixed (DescriptorSet* setPointer = sets)
        {
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = checked((uint)sets.Length),
                PSetLayouts = layoutPointer
            };
            Result result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &info,
                setPointer);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    $"Failed to allocate automatic planar {label} descriptor sets.",
                    result);
            }
        }
    }

    private PipelineLayout CreatePipelineLayout(
        DescriptorSetLayout localLayout,
        uint pushConstantSize)
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            localLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = pushConstantSize
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3u,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &info,
            null,
            out PipelineLayout pipelineLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create automatic planar compute pipeline layout.",
                result);
        }
        return pipelineLayout;
    }

    private VkPipeline CreateComputePipeline(
        string shaderName,
        PipelineLayout layout)
    {
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
                Layout = layout,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId($"AutomaticPlanar.{shaderName}"),
                    &info,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1u,
                    &info,
                    null,
                    out pipeline);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    $"Failed to create automatic planar pipeline '{shaderName}'.",
                    result);
            }
            return pipeline;
        }
        finally
        {
            if (shader.Handle != 0)
            {
                _context.Api.DestroyShaderModule(
                    _context.Device,
                    shader,
                    null);
            }
        }
    }

    public override void Cleanup()
    {
        if (_reprojectPipeline.Handle != 0)
            _context.Api.DestroyPipeline(
                _context.Device, _reprojectPipeline, null);
        if (_prefilterPipeline.Handle != 0)
            _context.Api.DestroyPipeline(
                _context.Device, _prefilterPipeline, null);
        if (_reprojectPipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(
                _context.Device, _reprojectPipelineLayout, null);
        if (_prefilterPipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(
                _context.Device, _prefilterPipelineLayout, null);
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(
                _context.Device, _pipelineCache, null);
        if (_reprojectDescriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(
                _context.Device, _reprojectDescriptorPool, null);
        if (_prefilterDescriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(
                _context.Device, _prefilterDescriptorPool, null);
        if (_reprojectSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _reprojectSetLayout, null);
        if (_prefilterSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _prefilterSetLayout, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);

        _reprojectPipeline = default;
        _prefilterPipeline = default;
        _reprojectPipelineLayout = default;
        _prefilterPipelineLayout = default;
        _pipelineCache = default;
        _reprojectDescriptorPool = default;
        _prefilterDescriptorPool = default;
        _reprojectSetLayout = default;
        _prefilterSetLayout = default;
        Array.Clear(_reprojectDescriptorSets);
        Array.Clear(_prefilterDescriptorSets);
    }

    private enum AutomaticPlanarReprojectionMode : uint
    {
        CaptureDepth = 0u,
        ScatterDepth = 1u,
        ResolveColor = 2u
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AutomaticPlanarReprojectPushConstants
    {
        public readonly uint FrameIndex;
        public readonly uint Slot;
        public readonly uint Mode;
        public readonly uint Width;
        public readonly uint Height;
        public readonly uint Reserved0;
        public readonly uint Reserved1;
        public readonly uint Reserved2;

        public AutomaticPlanarReprojectPushConstants(
            uint frameIndex,
            uint slot,
            AutomaticPlanarReprojectionMode mode,
            uint width,
            uint height)
        {
            FrameIndex = frameIndex;
            Slot = slot;
            Mode = (uint)mode;
            Width = width;
            Height = height;
            Reserved0 = 0u;
            Reserved1 = 0u;
            Reserved2 = 0u;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct AutomaticPlanarPrefilterPushConstants
    {
        public readonly uint OutputWidth;
        public readonly uint OutputHeight;
        public readonly uint SampleCount;
        public readonly uint MipLevel;
        public readonly float Roughness;
        public readonly float SourceTexelX;
        public readonly float SourceTexelY;
        public readonly float MaximumUvFootprint;

        public AutomaticPlanarPrefilterPushConstants(
            uint outputWidth,
            uint outputHeight,
            uint sampleCount,
            uint mipLevel,
            float roughness,
            float sourceTexelX,
            float sourceTexelY,
            float maximumUvFootprint)
        {
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
            SampleCount = sampleCount;
            MipLevel = mipLevel;
            Roughness = roughness;
            SourceTexelX = sourceTexelX;
            SourceTexelY = sourceTexelY;
            MaximumUvFootprint = maximumUvFootprint;
        }
    }
}
