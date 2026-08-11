using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Fixed C4 visible-receiver resolve recorder. Descriptor sets and pipelines
/// are fully built before the mode becomes effective; frame recording performs
/// no allocation or descriptor mutation. Sets 0/1 remain the global bindless
/// heaps and set 2 is the versioned private screen ABI.
/// </summary>
internal sealed unsafe class GiCausticScreenGpuPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly RenderTargetManager _targets;
    private readonly GiCausticScreenResolveLayout _screenLayout;
    private readonly BufferHandle[] _frameConstantBuffers;
    private readonly DescriptorSet[] _descriptorSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly nint _entryPointName;

    private DescriptorSetLayout _screenSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _resetPipeline;
    private VkPipeline _classifyPipeline;
    private VkPipeline _resolvePipeline;
    private VkPipeline _compositePipeline;
    private bool _disposed;

    internal GiCausticScreenGpuPass(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        RenderTargetManager targets,
        in GiCausticScreenResolveLayout screenLayout,
        BufferHandle[] frameConstantBuffers)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _screenLayout = screenLayout;
        _frameConstantBuffers = frameConstantBuffers ??
            throw new ArgumentNullException(nameof(frameConstantBuffers));
        _entryPointName = SilkMarshal.StringToPtr("main");

        try
        {
            GiCausticScreenGpuAbi.VerifyManagedLayout();
            ValidateResources();
            ValidatePushConstantRange();
            CreateDescriptorSetLayout();
            CreateDescriptorPoolAndSets();
            CreatePipelineCache();
            CreatePipelineLayout();
            _resetPipeline = CreatePipeline(
                GiCausticGpuPassNames.ScreenResetShader,
                "C4 Screen Reset Pipeline");
            _classifyPipeline = CreatePipeline(
                GiCausticGpuPassNames.ScreenClassifyShader,
                "C4 Screen Tile Classify Pipeline");
            _resolvePipeline = CreatePipeline(
                GiCausticGpuPassNames.ScreenResolveShader,
                "C4 Screen Receiver Resolve Pipeline");
            _compositePipeline = CreatePipeline(
                GiCausticGpuPassNames.ScreenCompositeShader,
                "C4 Screen Composite Pipeline");
            WriteDescriptorSets();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void RecordResolve(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        in GPUCausticScreenPushConstantsV1 pushConstants,
        BufferHandle scratchBuffer)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(sceneData);
        ValidateRecordInputs(commandBuffer, sceneData, pushConstants,
            scratchBuffer);

        WriteFrameConstants(frameIndex, sceneData, pushConstants);
        _targets.SceneDepth.TransitionToDepthReadOnly(commandBuffer);
        _targets.GiCausticReceiverPayload!.TransitionToShaderRead(commandBuffer);
        RepublishStorageWrite(_targets.GiCausticRadiance!, commandBuffer);
        RepublishStorageWrite(_targets.GiCausticMoments!, commandBuffer);

        DescriptorSet descriptorSet = _descriptorSets[frameIndex];
        BindAndPush(commandBuffer, _resetPipeline, descriptorSet,
            pushConstants);
        _context.Api.CmdDispatch(commandBuffer, 1u, 1u, 1u);
        RecordScratchComputeBarrier(commandBuffer, scratchBuffer,
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit);

        BindAndPush(commandBuffer, _classifyPipeline, descriptorSet,
            pushConstants);
        _context.Api.CmdDispatch(
            commandBuffer,
            checked((uint)_screenLayout.TileCountX),
            checked((uint)_screenLayout.TileCountY),
            1u);
        RecordClassifyToIndirectResolveBarrier(commandBuffer, scratchBuffer);

        BindAndPush(commandBuffer, _resolvePipeline, descriptorSet,
            pushConstants);
        VkBuffer scratch = _bufferManager.GetBuffer(scratchBuffer);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            scratch,
            checked((ulong)GiCausticScreenGpuAbi.IndirectDispatchWordOffset *
                sizeof(uint)));
        RecordResolveToCompositeBarrier(commandBuffer);
    }

    internal void RecordComposite(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData,
        in GPUCausticScreenPushConstantsV1 pushConstants,
        BufferHandle scratchBuffer)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ArgumentNullException.ThrowIfNull(sceneData);
        ValidateRecordInputs(commandBuffer, sceneData, pushConstants,
            scratchBuffer);

        RepublishStorageReadWrite(_targets.SceneColor, commandBuffer);
        DescriptorSet descriptorSet = _descriptorSets[frameIndex];
        BindAndPush(commandBuffer, _compositePipeline, descriptorSet,
            pushConstants);
        VkBuffer scratch = _bufferManager.GetBuffer(scratchBuffer);
        _context.Api.CmdDispatchIndirect(
            commandBuffer,
            scratch,
            checked((ulong)GiCausticScreenGpuAbi.IndirectDispatchWordOffset *
                sizeof(uint)));
        RecordCompositeConsumerBarrier(commandBuffer);
    }

    private void WriteFrameConstants(
        int frameIndex,
        SceneRenderingData sceneData,
        in GPUCausticScreenPushConstantsV1 pushConstants)
    {
        Matrix4x4 viewProjection = ToNumerics(sceneData.ViewProjectionMatrix);
        Matrix4x4 inverseViewProjection =
            ToNumerics(sceneData.InverseViewProjectionMatrix);
        Vector3 cameraPosition = ToNumerics(sceneData.CameraPosition);
        if (!Finite(viewProjection) || !Finite(inverseViewProjection) ||
            !Finite(cameraPosition))
        {
            throw new ArgumentException(
                "C4 screen frame matrices and camera position must be finite.",
                nameof(sceneData));
        }

        BufferHandle handle = _frameConstantBuffers[frameIndex];
        void* mapped = _bufferManager.GetMappedPointer(handle);
        if (mapped is null)
        {
            throw new InvalidOperationException(
                "C4 screen frame constants are not host mapped.");
        }

        *(GPUCausticScreenFrameConstantsV1*)mapped = new()
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverseViewProjection,
            FullExtentAndInverse = new Vector4(
                _screenLayout.Width,
                _screenLayout.Height,
                1.0f / _screenLayout.Width,
                1.0f / _screenLayout.Height),
            CameraPositionAndFlags = new Vector4(cameraPosition, 0.0f),
            ScreenParameters = new GPUCausticUInt4
            {
                X = checked((uint)_screenLayout.TileCountX),
                Y = checked((uint)_screenLayout.TileCountY),
                Z = checked((uint)_screenLayout.TileCapacity),
                W = (uint)(GiCausticScreenGpuFlags.ReversedZ |
                    GiCausticScreenGpuFlags.ReceiverPayloadValidated |
                    GiCausticScreenGpuFlags.SceneColorCompositeEnabled)
            },
            ResolveParameters = new Vector4(
                _screenLayout.MinimumReceiverNormalCosine,
                pushConstants.CellOriginAndSize.W,
                0.0f,
                0.0f)
        };
        _bufferManager.FlushBuffer(
            handle,
            0UL,
            GiCausticScreenGpuAbi.FrameConstantsBytes);
    }

    private void ValidateRecordInputs(
        CommandBuffer commandBuffer,
        SceneRenderingData sceneData,
        in GPUCausticScreenPushConstantsV1 pushConstants,
        BufferHandle scratchBuffer)
    {
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
        if (!scratchBuffer.IsValid ||
            _bufferManager.GetBufferSize(scratchBuffer) <
                _screenLayout.TileScratchBytes)
        {
            throw new ArgumentException(
                "C4 screen scratch allocation is unavailable or undersized.",
                nameof(scratchBuffer));
        }
        BufferUsageFlags usage = _bufferManager.GetBufferUsage(scratchBuffer);
        BufferUsageFlags required = BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.IndirectBufferBit;
        if ((usage & required) != required)
        {
            throw new ArgumentException(
                "C4 screen scratch must support storage and indirect dispatch.",
                nameof(scratchBuffer));
        }
        if (sceneData.ScreenWidth != (uint)_screenLayout.Width ||
            sceneData.ScreenHeight != (uint)_screenLayout.Height)
        {
            throw new ArgumentException(
                "C4 screen resolve extent does not match the current frame.",
                nameof(sceneData));
        }
        if (pushConstants.AbiVersion != GiCausticScreenGpuAbi.Version ||
            pushConstants.CacheGeneration == 0u ||
            pushConstants.CellOriginAndSize.W <= 0.0f ||
            !float.IsFinite(pushConstants.CellOriginAndSize.W))
        {
            throw new ArgumentException(
                "C4 screen push constants are not a published cache contract.",
                nameof(pushConstants));
        }
    }

    private void ValidateResources()
    {
        if (!_screenLayout.IsValid ||
            _frameConstantBuffers.Length != RenderingConstants.FramesInFlight)
        {
            throw new ArgumentException(
                "C4 screen resolve requires its exact layout and frame ring.");
        }
        for (int i = 0; i < _frameConstantBuffers.Length; i++)
        {
            BufferHandle handle = _frameConstantBuffers[i];
            if (!handle.IsValid || _bufferManager.GetBufferSize(handle) !=
                    GiCausticScreenGpuAbi.FrameConstantsBytes ||
                (_bufferManager.GetBufferUsage(handle) &
                    BufferUsageFlags.StorageBufferBit) == 0)
            {
                throw new ArgumentException(
                    "Every C4 frame-constant buffer must exactly match the ABI.",
                    nameof(_frameConstantBuffers));
            }
        }

        ValidateTarget(_targets.GiCausticReceiverPayload,
            GiCausticScreenGpuAbi.ReceiverPayloadFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            "receiver payload");
        ValidateTarget(_targets.GiCausticRadiance,
            GiCausticScreenGpuAbi.RadianceFormat,
            ImageUsageFlags.StorageBit,
            "radiance");
        ValidateTarget(_targets.GiCausticMoments,
            GiCausticScreenGpuAbi.MomentsFormat,
            ImageUsageFlags.StorageBit,
            "moments");
        ValidateTarget(_targets.SceneColor,
            RenderTargetManager.SceneColorFormat,
            ImageUsageFlags.StorageBit,
            "scene color");
        if (_targets.SceneDepth.Extent.Width != (uint)_screenLayout.Width ||
            _targets.SceneDepth.Extent.Height != (uint)_screenLayout.Height ||
            (_targets.SceneDepth.Usage & ImageUsageFlags.SampledBit) == 0)
        {
            throw new ArgumentException(
                "C4 requires the current full-resolution sampled scene depth.");
        }
    }

    private void ValidateTarget(
        RenderTarget? target,
        Format format,
        ImageUsageFlags requiredUsage,
        string label)
    {
        if (target is null || target.Format != format ||
            target.Extent.Width != (uint)_screenLayout.Width ||
            target.Extent.Height != (uint)_screenLayout.Height ||
            (target.Usage & requiredUsage) != requiredUsage)
        {
            throw new ArgumentException(
                $"C4 {label} target does not match the admitted screen ABI.");
        }
    }

    private void ValidatePushConstantRange()
    {
        var properties = new PhysicalDeviceProperties();
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice,
            &properties);
        if (GiCausticScreenGpuAbi.PushConstantsBytes >
            properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"C4 screen resolve requires {GiCausticScreenGpuAbi.PushConstantsBytes} " +
                $"push-constant bytes but the device exposes " +
                $"{properties.Limits.MaxPushConstantsSize}.");
        }
    }

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding* bindings =
            stackalloc DescriptorSetLayoutBinding[6];
        bindings[0] = Binding(GiCausticScreenGpuBindings.SceneDepth,
            DescriptorType.CombinedImageSampler);
        bindings[1] = Binding(GiCausticScreenGpuBindings.ReceiverPayload,
            DescriptorType.CombinedImageSampler);
        bindings[2] = Binding(GiCausticScreenGpuBindings.CausticRadiance,
            DescriptorType.StorageImage);
        bindings[3] = Binding(GiCausticScreenGpuBindings.CausticMoments,
            DescriptorType.StorageImage);
        bindings[4] = Binding(GiCausticScreenGpuBindings.SceneColor,
            DescriptorType.StorageImage);
        bindings[5] = Binding(GiCausticScreenGpuBindings.FrameConstants,
            DescriptorType.StorageBuffer);
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = GiCausticScreenGpuAbi.DescriptorCount,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device, &info, null, out _screenSetLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 screen descriptor layout.", result);
        _context.SetDebugName(_screenSetLayout.Handle,
            ObjectType.DescriptorSetLayout, "C4 Screen Descriptor Set Layout");
    }

    private static DescriptorSetLayoutBinding Binding(
        uint binding,
        DescriptorType type) => new()
    {
        Binding = binding,
        DescriptorType = type,
        DescriptorCount = 1u,
        StageFlags = ShaderStageFlags.ComputeBit
    };

    private void CreateDescriptorPoolAndSets()
    {
        uint frameCount = RenderingConstants.FramesInFlight;
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = checked(frameCount * 2u)
        };
        sizes[1] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageImage,
            DescriptorCount = checked(frameCount * 3u)
        };
        sizes[2] = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = frameCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3u,
            PPoolSizes = sizes,
            MaxSets = frameCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device, &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 screen descriptor pool.", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[
            RenderingConstants.FramesInFlight];
        for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            layouts[i] = _screenSetLayout;
        fixed (DescriptorSet* sets = _descriptorSets)
        {
            var allocationInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = frameCount,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device, &allocationInfo, sets);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate C4 screen descriptor sets.", result);
    }

    private void WriteDescriptorSets()
    {
        DescriptorImageInfo* images = stackalloc DescriptorImageInfo[5];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[6];
        for (int frameIndex = 0;
             frameIndex < RenderingConstants.FramesInFlight;
             frameIndex++)
        {
            DescriptorSet set = _descriptorSets[frameIndex];
            images[0] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.HiZSampler,
                ImageView = _targets.SceneDepth.View,
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
            };
            images[1] = new DescriptorImageInfo
            {
                Sampler = _bindlessHeap.HiZSampler,
                ImageView = _targets.GiCausticReceiverPayload!.View,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            images[2] = Storage(_targets.GiCausticRadiance!);
            images[3] = Storage(_targets.GiCausticMoments!);
            images[4] = Storage(_targets.SceneColor);
            DescriptorBufferInfo frameConstants = new()
            {
                Buffer = _bufferManager.GetBuffer(
                    _frameConstantBuffers[frameIndex]),
                Offset = 0UL,
                Range = GiCausticScreenGpuAbi.FrameConstantsBytes
            };
            writes[0] = ImageWrite(set,
                GiCausticScreenGpuBindings.SceneDepth,
                DescriptorType.CombinedImageSampler, &images[0]);
            writes[1] = ImageWrite(set,
                GiCausticScreenGpuBindings.ReceiverPayload,
                DescriptorType.CombinedImageSampler, &images[1]);
            writes[2] = ImageWrite(set,
                GiCausticScreenGpuBindings.CausticRadiance,
                DescriptorType.StorageImage, &images[2]);
            writes[3] = ImageWrite(set,
                GiCausticScreenGpuBindings.CausticMoments,
                DescriptorType.StorageImage, &images[3]);
            writes[4] = ImageWrite(set,
                GiCausticScreenGpuBindings.SceneColor,
                DescriptorType.StorageImage, &images[4]);
            writes[5] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = GiCausticScreenGpuBindings.FrameConstants,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &frameConstants
            };
            _context.Api.UpdateDescriptorSets(
                _context.Device, 6u, writes, 0u, null);
        }
    }

    private static DescriptorImageInfo Storage(RenderTarget target) => new()
    {
        ImageView = target.View,
        ImageLayout = ImageLayout.General
    };

    private static WriteDescriptorSet ImageWrite(
        DescriptorSet set,
        uint binding,
        DescriptorType type,
        DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1u,
        DescriptorType = type,
        PImageInfo = info
    };

    private void CreatePipelineCache()
    {
        var info = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo
        };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device, &info, null, out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 screen pipeline cache.", result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3];
        layouts[GiCausticScreenGpuDescriptorSets.BindlessStorageBuffers] =
            _bindlessHeap.StorageBufferSetLayout;
        layouts[GiCausticScreenGpuDescriptorSets.BindlessTextures] =
            _bindlessHeap.TextureSamplerSetLayout;
        layouts[GiCausticScreenGpuDescriptorSets.ScreenResources] =
            _screenSetLayout;
        var range = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = GiCausticScreenGpuAbi.PushConstantsBytes
        };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3u,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &range
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device, &info, null, out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 screen pipeline layout.", result);
        _context.SetDebugName(_pipelineLayout.Handle,
            ObjectType.PipelineLayout, "C4 Screen Pipeline Layout");
    }

    private VkPipeline CreatePipeline(string shaderName, string debugName)
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
            Result result = _context.Api.CreateComputePipelines(
                _context.Device, _pipelineCache, 1u, &info, null,
                out VkPipeline pipeline);
            if (result != Result.Success)
                throw new VulkanException("Failed to create " + debugName + ".", result);
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
            return pipeline;
        }
        finally
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }
    }

    private void BindAndPush(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        DescriptorSet screenSet,
        in GPUCausticScreenPushConstantsV1 pushConstants)
    {
        if (pipeline.Handle == 0)
            throw new InvalidOperationException("C4 screen pipeline is unavailable.");
        DescriptorSet* sets = stackalloc DescriptorSet[3];
        sets[0] = _bindlessHeap.StorageBufferSet;
        sets[1] = _bindlessHeap.TextureSamplerSet;
        sets[2] = screenSet;
        _context.Api.CmdBindPipeline(
            commandBuffer, PipelineBindPoint.Compute, pipeline);
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            0u,
            3u,
            sets,
            0u,
            null);
        GPUCausticScreenPushConstantsV1 local = pushConstants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            GiCausticScreenGpuAbi.PushConstantsBytes,
            &local);
    }

    private void RecordScratchComputeBarrier(
        CommandBuffer commandBuffer,
        BufferHandle scratchBuffer,
        AccessFlags2 destinationAccess)
    {
        var barrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = destinationAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _bufferManager.GetBuffer(scratchBuffer),
            Offset = 0UL,
            Size = _bufferManager.GetBufferSize(scratchBuffer)
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordClassifyToIndirectResolveBarrier(
        CommandBuffer commandBuffer,
        BufferHandle scratchBuffer)
    {
        var bufferBarrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.DrawIndirectBit |
                PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.IndirectCommandReadBit |
                AccessFlags2.ShaderStorageReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _bufferManager.GetBuffer(scratchBuffer),
            Offset = 0UL,
            Size = _bufferManager.GetBufferSize(scratchBuffer)
        };
        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &memoryBarrier,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &bufferBarrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordResolveToCompositeBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void RecordCompositeConsumerBarrier(CommandBuffer commandBuffer)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.AllGraphicsBit |
                PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.MemoryReadBit |
                AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ColorAttachmentReadBit |
                AccessFlags2.ColorAttachmentWriteBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private static void RepublishStorageWrite(
        RenderTarget target,
        CommandBuffer commandBuffer) => target.TransitionToLayout(
        commandBuffer,
        ImageLayout.General,
        PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderStorageWriteBit,
        force: true);

    private static void RepublishStorageReadWrite(
        RenderTarget target,
        CommandBuffer commandBuffer) => target.TransitionToLayout(
        commandBuffer,
        ImageLayout.General,
        PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit,
        force: true);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static Vector3 ToNumerics(Njulf.Core.Math.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    private static Matrix4x4 ToNumerics(
        Njulf.Core.Math.Matrix4x4 value) => new(
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DestroyPipeline(_resetPipeline);
        DestroyPipeline(_classifyPipeline);
        DestroyPipeline(_resolvePipeline);
        DestroyPipeline(_compositePipeline);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(
                _context.Device, _pipelineLayout, null);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(
                _context.Device, _descriptorPool, null);
        if (_screenSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device, _screenSetLayout, null);
        if (_pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(
                _context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
    }

    private void DestroyPipeline(VkPipeline pipeline)
    {
        if (pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GiCausticScreenGpuPass));
    }
}
