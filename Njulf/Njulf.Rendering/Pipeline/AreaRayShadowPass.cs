using System;
using System.Collections.Generic;
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
/// Traces full-resolution opaque-receiver visibility to up to four scheduled
/// rectangle, disk, or tube emitters. One R8 visibility value per emitter is
/// packed into each per-pixel storage word.
/// </summary>
public sealed unsafe class AreaRayShadowPass : RenderPassBase
{
    private const string ShaderName = "area_ray_shadow.comp.spv";
    private const uint WorkgroupSize = 8u;
    private const ulong BytesPerPixel = sizeof(uint);
    private const ulong AllocationRetryFrames = 60UL;

    private readonly RenderTargetManager _renderTargets;
    private readonly ShadowSettings _settings;
    private readonly BufferManager _bufferManager;
    private readonly AccelerationStructureManager _accelerationStructureManager;
    private readonly RaySceneDescriptorBank _raySceneDescriptors;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly BufferHandle[] _maskBuffers =
        new BufferHandle[RenderingConstants.FramesInFlight];
    private nint _entryPointName;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;
    private uint _allocatedWidth;
    private uint _allocatedHeight;
    private ulong _bufferBytes;
    private ulong _nextAllocationRetryFrame;

    public AreaRayShadowPass(
        VulkanContext context,
        SwapchainManager swapchain,
        BindlessHeap bindlessHeap,
        RenderTargetManager renderTargets,
        ShadowSettings settings,
        BufferManager bufferManager,
        AccelerationStructureManager accelerationStructureManager,
        RaySceneDescriptorBank raySceneDescriptors,
        GiPipelineCacheService? pipelineCacheService = null)
        : base("AreaRayShadowPass", context, swapchain, bindlessHeap)
    {
        _renderTargets = renderTargets ??
            throw new ArgumentNullException(nameof(renderTargets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _accelerationStructureManager = accelerationStructureManager ??
            throw new ArgumentNullException(nameof(accelerationStructureManager));
        _raySceneDescriptors = raySceneDescriptors ??
            throw new ArgumentNullException(nameof(raySceneDescriptors));
        _pipelineCacheService = pipelineCacheService;
    }

    public bool PipelineAvailable { get; private set; }
    public string FailureDetail { get; private set; } = string.Empty;
    public uint ResourceGeneration { get; private set; }
    public ulong BufferBytes => _bufferBytes;
    public uint Width => _allocatedWidth;
    public uint Height => _allocatedHeight;
    public bool IsAvailable => PipelineAvailable &&
        _allocatedWidth != 0u && _allocatedHeight != 0u &&
        AllMaskBuffersValid();

    public BufferHandle GetMaskBuffer(int frameIndex) =>
        (uint)frameIndex < (uint)_maskBuffers.Length
            ? _maskBuffers[frameIndex]
            : BufferHandle.Invalid;

    public override bool SupportsSecondaryCommandBuffer => true;

    public override void Initialize()
    {
        if (!_context.RayQuerySupported ||
            _context.KhrAccelerationStructure == null ||
            !_accelerationStructureManager.Supported ||
            !_raySceneDescriptors.IsAvailable)
        {
            FailureDetail =
                "area-light shadows require ray-query and acceleration-structure support";
            return;
        }

        try
        {
            _entryPointName = SilkMarshal.StringToPtr("main");
            ValidatePushConstantRange();
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
                $"area ray-shadow pipeline initialization failed: {exception.Message}";
            CleanupPipelineResources();
        }
    }

    public bool EnsureResources(uint width, uint height, ulong frameSerial)
    {
        if (!PipelineAvailable)
            return false;
        if (width == 0u || height == 0u)
        {
            FailureDetail = "area ray-shadow mask has an invalid extent";
            return false;
        }
        if (_allocatedWidth == width && _allocatedHeight == height &&
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
            ulong requiredBytes = checked((ulong)width * height * BytesPerPixel);
            if (requiredBytes == 0UL ||
                (_context.MaximumStorageBufferRange != 0UL &&
                 requiredBytes > _context.MaximumStorageBufferRange))
            {
                throw new InvalidOperationException(
                    $"area ray-shadow mask requires {requiredBytes} bytes, " +
                    $"but the storage-buffer limit is {_context.MaximumStorageBufferRange} bytes");
            }

            for (int frameIndex = 0; frameIndex < replacements.Length; frameIndex++)
            {
                replacements[frameIndex] = _bufferManager.CreateBuffer(
                    requiredBytes,
                    BufferUsageFlags.StorageBufferBit |
                    BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferDevice,
                    debugName: $"Area ray shadow mask frame {frameIndex}",
                    category: MemoryBudgetCategory.ShadowMaps);
            }
            for (int frameIndex = 0; frameIndex < replacements.Length; frameIndex++)
            {
                _bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.AreaRayShadowMaskBufferBase + frameIndex,
                    _bufferManager.GetBuffer(replacements[frameIndex]),
                    0,
                    requiredBytes);
            }
            for (int frameIndex = 0; frameIndex < _maskBuffers.Length; frameIndex++)
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
            _nextAllocationRetryFrame = checked(frameSerial + AllocationRetryFrames);
            FailureDetail =
                $"area ray-shadow mask allocation failed: {exception.Message}";
            return false;
        }
    }

    public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
    {
        bool execute = sceneData.AreaRayShadowPassEnabled &&
            sceneData.AreaShadowSelectedCount > 0 &&
            sceneData.AreaShadowSelectedCount <= 4 &&
            sceneData.AreaShadowLights.Length >= sceneData.AreaShadowSelectedCount &&
            IsAvailable &&
            _accelerationStructureManager.Active &&
            sceneData.RaySceneReadiness.IsReady(
                RaySceneConsumer.AreaLightShadows,
                RaySceneGeometryCategory.DirectionalShadowDefault);
        sceneData.AreaRayShadowPassEnabled = execute;
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
        if (!_raySceneDescriptors.TryUpdate(frameIndex, out string descriptorFailure))
            throw new InvalidOperationException(descriptorFailure);

        _renderTargets.SceneDepth.TransitionToDepthReadOnly(commandBuffer);
        ResetMask(commandBuffer, frameIndex);
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipeline);
        BindBindlessStorageAndTextures(
            commandBuffer,
            _pipelineLayout,
            PipelineBindPoint.Compute);
        _raySceneDescriptors.Bind(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            frameIndex);

        SelectedLocalShadow[] selected = sceneData.AreaShadowLights;
        uint Index(int slot) => slot < selected.Length
            ? checked((uint)selected[slot].LightIndex)
            : 0u;
        var push = new GPUAreaRayShadowPushConstants
        {
            InverseViewProjectionMatrix = sceneData.InverseViewProjectionMatrix,
            CameraPosition = new Vector4(sceneData.CameraPosition, 1f),
            LightIndex0 = Index(0),
            LightIndex1 = Index(1),
            LightIndex2 = Index(2),
            LightIndex3 = Index(3),
            ScreenWidth = _allocatedWidth,
            ScreenHeight = _allocatedHeight,
            OutputBufferIndex = checked((uint)(
                BindlessIndex.AreaRayShadowMaskBufferBase + frameIndex)),
            InstanceMask = AccelerationStructureManager
                .DirectionalShadowInstanceMask,
            TemporalSampleIndex = sceneData.TemporalSampleIndex,
            TraceSampleCount = checked((uint)Math.Clamp(
                sceneData.AreaShadowSampleCount,
                1,
                4)),
            SelectedLightCount = checked((uint)sceneData.AreaShadowSelectedCount),
            CurrentFrameIndex = checked((uint)frameIndex)
        };
        _context.Api.CmdPushConstants(
            commandBuffer,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUAreaRayShadowPushConstants>(),
            &push);
        _context.Api.CmdDispatch(
            commandBuffer,
            (_allocatedWidth + WorkgroupSize - 1u) / WorkgroupSize,
            (_allocatedHeight + WorkgroupSize - 1u) / WorkgroupSize,
            1u);
    }

    private void ResetMask(CommandBuffer commandBuffer, int frameIndex)
    {
        VkBuffer mask = _bufferManager.GetBuffer(_maskBuffers[frameIndex]);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
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
            uint.MaxValue);
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
        for (int index = 0; index < _maskBuffers.Length; index++)
        {
            if (_maskBuffers[index].IsValid)
                _bufferManager.DestroyBuffer(_maskBuffers[index]);
            _maskBuffers[index] = BufferHandle.Invalid;
        }
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

    private void ValidatePushConstantRange()
    {
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        uint required = (uint)Marshal.SizeOf<GPUAreaRayShadowPushConstants>();
        if (required > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"Area ray shadows require {required} push-constant bytes, " +
                $"but the device exposes {properties.Limits.MaxPushConstantsSize}.");
        }
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
                "Failed to create area ray-shadow pipeline cache",
                result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout,
            _raySceneDescriptors.Layout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = (uint)Marshal.SizeOf<GPUAreaRayShadowPushConstants>()
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
                "Failed to create area ray-shadow pipeline layout",
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
                    $"AreaRayShadowPass:{ShaderName}",
                    start);
            }
            if (result != Result.Success)
                throw new VulkanException(
                    "Failed to create area ray-shadow compute pipeline",
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
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
    }
}
