using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Produces a deterministic full-resolution directional visibility mask from
/// the current shared TLAS. Four R8-unorm visibility values are packed into
/// each storage word, matching the planned one-byte-per-pixel footprint.
/// </summary>
public sealed unsafe class DirectionalRayShadowPass : RenderPassBase
{
    private const string ShaderName = "directional_ray_shadow.comp.spv";
    private const uint WorkgroupSize = 8u;
    private const ulong PixelsPerWord = 4UL;
    private const ulong BytesPerWord = sizeof(uint);
    private const ulong AllocationRetryFrames = 60UL;

    private readonly RenderTargetManager _renderTargets;
    private readonly ShadowSettings _settings;
    private readonly BufferManager _bufferManager;
    private readonly AccelerationStructureManager _accelerationStructureManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly BufferHandle[] _maskBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private readonly DescriptorSet[] _accelerationStructureSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly AccelerationStructureKHR[] _boundTlases =
        new AccelerationStructureKHR[RenderingConstants.FramesInFlight];

    private nint _entryPointName;
    private DescriptorSetLayout _accelerationStructureSetLayout;
    private DescriptorPool _descriptorPool;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;
    private uint _allocatedWidth;
    private uint _allocatedHeight;
    private ulong _bufferBytes;
    private ulong _nextAllocationRetryFrame;

    public DirectionalRayShadowPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        ShadowSettings settings,
        BufferManager bufferManager,
        AccelerationStructureManager accelerationStructureManager,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("DirectionalRayShadowPass", context, swapchain, bindlessHeap)
    {
        _renderTargets = renderTargets ??
            throw new ArgumentNullException(nameof(renderTargets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _accelerationStructureManager = accelerationStructureManager ??
            throw new ArgumentNullException(nameof(accelerationStructureManager));
        _pipelineCacheService = pipelineCacheService;
    }

    /// <summary>True when the ray-query pipeline and current-sized mask banks exist.</summary>
    public bool IsAvailable =>
        PipelineAvailable &&
        _allocatedWidth != 0u &&
        _allocatedHeight != 0u &&
        AllMaskBuffersValid();

    public bool PipelineAvailable { get; private set; }
    public string FailureDetail { get; private set; } = string.Empty;
    public uint ResourceGeneration { get; private set; }
    public ulong BufferBytes => _bufferBytes;
    public uint Width => _allocatedWidth;
    public uint Height => _allocatedHeight;

    public BufferHandle GetMaskBuffer(int frameIndex) =>
        (uint)frameIndex < (uint)_maskBuffers.Length
            ? _maskBuffers[frameIndex]
            : BufferHandle.Invalid;

    public override bool SupportsSecondaryCommandBuffer => true;

    public override void Initialize()
    {
        if (!_context.RayQuerySupported ||
            _context.KhrAccelerationStructure == null ||
            !_accelerationStructureManager.Supported)
        {
            FailureDetail =
                "directional ray shadows require ray-query and acceleration-structure support";
            return;
        }

        try
        {
            _entryPointName = SilkMarshal.StringToPtr("main");
            ValidatePushConstantRange();
            CreateAccelerationStructureSetLayout();
            CreateDescriptorPoolAndSets();
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
            PipelineAvailable = true;
            FailureDetail = string.Empty;
        }
        catch (Exception exception)
        {
            PipelineAvailable = false;
            FailureDetail =
                $"directional ray-shadow pipeline initialization failed: {exception.Message}";
            CleanupPipelineResources();
        }
    }

    /// <summary>
    /// Allocates both frame banks before effective-mode selection. Allocation
    /// failure is contained and rate-limited so the same frame can select CSM.
    /// The caller changes the requested extent only from the renderer's
    /// device-idle render-target rebuild, so replaced banks can be released
    /// once their bindless descriptors have been repointed.
    /// </summary>
    public bool EnsureResources(
        uint width,
        uint height,
        ulong frameSerial)
    {
        if (!PipelineAvailable)
            return false;
        if (width == 0u || height == 0u)
        {
            FailureDetail = "directional ray-shadow mask has an invalid extent";
            return false;
        }
        if (_allocatedWidth == width &&
            _allocatedHeight == height &&
            AllMaskBuffersValid())
        {
            return true;
        }
        if (frameSerial < _nextAllocationRetryFrame)
            return false;

        BufferHandle[] replacements =
            new BufferHandle[RenderingConstants.FramesInFlight];
        try
        {
            ulong pixelCount = checked((ulong)width * height);
            ulong wordCount = checked(
                (pixelCount + PixelsPerWord - 1UL) / PixelsPerWord);
            ulong requiredBytes = checked(wordCount * BytesPerWord);
            if (requiredBytes == 0UL ||
                (_context.MaximumStorageBufferRange != 0UL &&
                 requiredBytes > _context.MaximumStorageBufferRange))
            {
                throw new InvalidOperationException(
                    $"directional ray-shadow mask requires {requiredBytes} bytes, " +
                    $"but the storage-buffer limit is {_context.MaximumStorageBufferRange} bytes");
            }

            for (int frameIndex = 0;
                 frameIndex < replacements.Length;
                 frameIndex++)
            {
                replacements[frameIndex] = _bufferManager.CreateBuffer(
                    requiredBytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferDevice,
                    debugName: $"Directional ray shadow mask frame {frameIndex}",
                    category: MemoryBudgetCategory.ShadowMaps);
            }

            for (int frameIndex = 0;
                 frameIndex < replacements.Length;
                 frameIndex++)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.DirectionalRayShadowMaskBufferBase + frameIndex,
                    _bufferManager.GetBuffer(replacements[frameIndex]),
                    0,
                    requiredBytes);
            }

            for (int frameIndex = 0;
                 frameIndex < _maskBuffers.Length;
                 frameIndex++)
            {
                BufferHandle replaced = _maskBuffers[frameIndex];
                _maskBuffers[frameIndex] = replacements[frameIndex];
                replacements[frameIndex] = BufferHandle.Invalid;
                if (replaced.IsValid)
                    _bufferManager.DestroyBuffer(replaced);
            }

            _allocatedWidth = width;
            _allocatedHeight = height;
            _bufferBytes = requiredBytes;
            ResourceGeneration = ResourceGeneration == uint.MaxValue
                ? 1u
                : ResourceGeneration + 1u;
            if (ResourceGeneration == 0u)
                ResourceGeneration = 1u;
            _nextAllocationRetryFrame = 0UL;
            FailureDetail = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            for (int index = 0; index < replacements.Length; index++)
            {
                if (replacements[index].IsValid)
                    _bufferManager.DestroyBuffer(replacements[index]);
            }
            _nextAllocationRetryFrame = checked(
                frameSerial + AllocationRetryFrames);
            FailureDetail =
                $"directional ray-shadow mask allocation failed: {exception.Message}";
            return false;
        }
    }

    public override bool ShouldExecute(
        int frameIndex,
        SceneRenderingData sceneData)
    {
        bool execute = IsAvailable &&
            sceneData.DirectionalShadowFramePlan.UsesRayQuery &&
            sceneData.DirectionalShadowFramePlan.EffectiveMode !=
                DirectionalShadowMode.RayQuerySoft &&
            sceneData.DirectionalShadowPassEnabled &&
            _accelerationStructureManager.Active;
        sceneData.DirectionalRayShadowPassEnabled = execute;
        sceneData.DirectionalRayShadowMaskWidth = execute ? _allocatedWidth : 0u;
        sceneData.DirectionalRayShadowMaskHeight = execute ? _allocatedHeight : 0u;
        sceneData.DirectionalRayShadowMaskBytes = execute
            ? checked(_bufferBytes * (ulong)_maskBuffers.Length)
            : 0UL;
        sceneData.DirectionalRayShadowResourceGeneration = execute
            ? ResourceGeneration
            : 0u;
        return execute;
    }

    public override void Execute(
        CommandBuffer commandBuffer,
        int frameIndex,
        SceneRenderingData sceneData)
    {
        if (!ShouldExecute(frameIndex, sceneData))
            return;
        if ((uint)frameIndex >= (uint)_maskBuffers.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        UpdateAccelerationStructureDescriptor(sceneData);
        _renderTargets.SceneDepth.TransitionToDepthReadOnly(commandBuffer);
        ResetPackedMask(commandBuffer, frameIndex);
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        DescriptorSet accelerationStructureSet =
            _accelerationStructureSets[frameIndex];
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            2,
            1,
            &accelerationStructureSet,
            0,
            null);

        Vector3 lightDirection = -sceneData.DirectionalShadowLightDirection;
        float lightDirectionLengthSquared = lightDirection.LengthSquared();
        if (!float.IsFinite(lightDirectionLengthSquared) ||
            lightDirectionLengthSquared <= 1.0e-8f)
        {
            throw new InvalidOperationException(
                "The admitted directional ray-shadow pass has no finite sun direction.");
        }
        lightDirection /= MathF.Sqrt(lightDirectionLengthSquared);
        float maximumRayDistance =
            sceneData.DirectionalShadowFramePlan.EffectiveMode ==
                DirectionalShadowMode.HybridContact
                ? _settings.DirectionalContactShadowDistance
                : _settings.MaxShadowDistance;
        var push = new GPUDirectionalRayShadowPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPositionAndReceiverDistance = new Vector4(
                sceneData.CameraPosition,
                _settings.MaxShadowDistance),
            RayDirectionAndMaximumDistance = new Vector4(
                lightDirection,
                maximumRayDistance),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            OutputBufferIndex = checked((uint)
                (BindlessIndex.DirectionalRayShadowMaskBufferBase + frameIndex)),
            InstanceMask = AccelerationStructureManager
                .DirectionalShadowInstanceMask,
            OutputMode = (uint)sceneData.DirectionalShadowFramePlan.EffectiveMode
        };
        _context.Api.CmdPushConstants(
            commandBuffer,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>(),
            &push);
        _context.Api.CmdDispatch(
            commandBuffer,
            (_allocatedWidth + WorkgroupSize - 1u) / WorkgroupSize,
            (_allocatedHeight + WorkgroupSize - 1u) / WorkgroupSize,
            1u);
    }

    private void ResetPackedMask(CommandBuffer commandBuffer, int frameIndex)
    {
        VkBuffer mask = _bufferManager.GetBuffer(_maskBuffers[frameIndex]);
        Span<BufferMemoryBarrier2> barriers =
            stackalloc BufferMemoryBarrier2[1];
        // The frame-slot fence makes the previous consumer complete; this
        // explicit memory dependency makes its shader access available before
        // the transfer clear overwrites the reused bank.
        barriers[0] = BarrierBuilder.BufferBarrier(
            mask,
            PipelineStageFlags2.ComputeShaderBit |
                PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            0UL,
            _bufferBytes);
        ExecuteBarriers(commandBuffer, barriers);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            mask,
            0UL,
            _bufferBytes,
            0u);
        barriers[0] = BarrierBuilder.BufferBarrier(
            mask,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            0UL,
            _bufferBytes);
        ExecuteBarriers(commandBuffer, barriers);
    }

    private void ExecuteBarriers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferMemoryBarrier2> barriers)
    {
        fixed (BufferMemoryBarrier2* pBarriers = barriers)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)barriers.Length,
                PBufferMemoryBarriers = pBarriers
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }
    }

    public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
    {
        yield break;
    }

    public override void Cleanup()
    {
        PipelineAvailable = false;
        DestroyMaskBuffers(_maskBuffers);
        _allocatedWidth = 0u;
        _allocatedHeight = 0u;
        _bufferBytes = 0UL;
        CleanupPipelineResources();
    }

    private bool AllMaskBuffersValid()
    {
        for (int index = 0; index < _maskBuffers.Length; index++)
        {
            if (!_maskBuffers[index].IsValid)
                return false;
        }
        return true;
    }

    private void DestroyMaskBuffers(Span<BufferHandle> buffers)
    {
        for (int index = 0; index < buffers.Length; index++)
        {
            if (buffers[index].IsValid)
                _bufferManager.DestroyBuffer(buffers[index]);
            buffers[index] = BufferHandle.Invalid;
        }
    }

    private void ValidatePushConstantRange()
    {
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        uint required =
            (uint)Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>();
        if (required > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"Directional ray shadows require {required} push-constant bytes, " +
                $"but the device exposes {properties.Limits.MaxPushConstantsSize}.");
        }
    }

    private void CreateAccelerationStructureSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var createInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &createInfo,
            null,
            out _accelerationStructureSetLayout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create directional ray-shadow AS descriptor layout",
                result);
    }

    private void CreateDescriptorPoolAndSets()
    {
        const uint descriptorSetCount = RenderingConstants.FramesInFlight;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = descriptorSetCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = descriptorSetCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create directional ray-shadow descriptor pool",
                result);

        DescriptorSetLayout* layouts =
            stackalloc DescriptorSetLayout[RenderingConstants.FramesInFlight];
        for (int index = 0;
             index < RenderingConstants.FramesInFlight;
             index++)
        {
            layouts[index] = _accelerationStructureSetLayout;
        }
        fixed (DescriptorSet* descriptorSets = _accelerationStructureSets)
        {
            var allocationInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = descriptorSetCount,
                PSetLayouts = layouts
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocationInfo,
                descriptorSets);
        }
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to allocate directional ray-shadow descriptor sets",
                result);
    }

    private void CreatePipelineCache()
    {
        var createInfo = new PipelineCacheCreateInfo
        {
            SType = StructureType.PipelineCacheCreateInfo
        };
        Result result = _context.Api.CreatePipelineCache(
            _context.Device,
            &createInfo,
            null,
            out _pipelineCache);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create directional ray-shadow pipeline cache",
                result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            _accelerationStructureSetLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = (uint)Marshal.SizeOf<GPUDirectionalRayShadowPushConstants>()
        };
        var createInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3,
            PSetLayouts = layouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &createInfo,
            null,
            out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException(
                "Failed to create directional ray-shadow pipeline layout",
                result);
    }

    private void CreatePipeline()
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, ShaderName);
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
            long start = _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
            Result result;
            try
            {
                result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &createInfo,
                    null,
                    out _pipeline);
            }
            finally
            {
                _pipelineCacheService?.EndPipelineCreation(
                    $"DirectionalRayShadowPass:{ShaderName}",
                    start);
            }
            if (result != Result.Success)
                throw new VulkanException(
                    "Failed to create directional ray-shadow compute pipeline",
                    result);
        }
        finally
        {
            if (shaderModule.Handle != 0)
            {
                _context.Api.DestroyShaderModule(
                    _context.Device,
                    shaderModule,
                    null);
            }
        }
    }

    private void UpdateAccelerationStructureDescriptor(
        SceneRenderingData sceneData)
    {
        uint frameSlot = sceneData.CurrentFrameIndex;
        if (frameSlot >= RenderingConstants.FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(sceneData));
        AccelerationStructureKHR tlas =
            _accelerationStructureManager.TopLevelAccelerationStructureHandle;
        if (tlas.Handle == 0)
            throw new InvalidOperationException(
                "Directional ray shadows were admitted without a live TLAS.");
        if (_boundTlases[frameSlot].Handle == tlas.Handle)
            return;

        var accelerationStructureInfo =
            new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType
                    .WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &tlas
            };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &accelerationStructureInfo,
            DstSet = _accelerationStructureSets[frameSlot],
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr
        };
        _context.Api.UpdateDescriptorSets(
            _context.Device,
            1,
            &write,
            0,
            null);
        _boundTlases[frameSlot] = tlas;
    }

    private void CleanupPipelineResources()
    {
        if (_pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(
                _context.Device,
                _pipelineLayout,
                null);
        }
        if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
        {
            _context.Api.DestroyPipelineCache(
                _context.Device,
                _pipelineCache,
                null);
        }
        if (_descriptorPool.Handle != 0)
        {
            _context.Api.DestroyDescriptorPool(
                _context.Device,
                _descriptorPool,
                null);
        }
        if (_accelerationStructureSetLayout.Handle != 0)
        {
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _accelerationStructureSetLayout,
                null);
        }
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
        _descriptorPool = default;
        _accelerationStructureSetLayout = default;
    }
}
