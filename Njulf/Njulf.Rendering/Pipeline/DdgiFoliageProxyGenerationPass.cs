using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Vk = Silk.NET.Vulkan.Vk;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline;

/// <summary>
/// Expands compact, stable foliage-patch work records into frame-slot geometry
/// immediately before the corresponding updateable BLAS is built.
/// </summary>
public sealed unsafe class DdgiFoliageProxyGenerationPass : IDisposable
{
    private const uint WorkgroupSize = 64;
    private const string ShaderName = "ddgi_foliage_proxy_generate.comp.spv";

    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private nint _entryPointName;
    private PipelineLayout _pipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _pipeline;
    private bool _disposed;

    public DdgiFoliageProxyGenerationPass(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr("main");

        try
        {
            ValidatePushConstantRange();
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipeline();
            IsAvailable = true;
            InitializationFailureReason = string.Empty;
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            InitializationFailureReason =
                $"foliage proxy compute pipeline initialization failed: " +
                exception.Message;
            CleanupPipelineResources();
        }
    }

    public bool IsAvailable { get; private set; }
    public string InitializationFailureReason { get; private set; }

    public void Execute(
        CommandBuffer commandBuffer,
        DdgiFoliageProxyFrame frame,
        SceneRenderingData sceneData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sceneData);
        if (!frame.RequiresGpuGeneration)
            return;
        if (!IsAvailable || _pipeline.Handle == 0)
        {
            throw new InvalidOperationException(
                "A procedural foliage frame was admitted without an available " +
                "DDGI foliage proxy compute pipeline.");
        }
        if (commandBuffer.Handle == 0 ||
            !frame.PatchBuffer.IsValid ||
            !frame.VertexBuffer.IsValid ||
            !frame.IndexBuffer.IsValid)
        {
            throw new InvalidOperationException(
                "DDGI foliage proxy generation requires a valid command buffer " +
                "and complete frame-slot buffers.");
        }

        long start = Stopwatch.GetTimestamp();
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipeline);
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

        var push = new GPUDdgiFoliageProxyGenerationPushConstants
        {
            PatchBufferIndex = frame.PatchBufferIndex,
            VertexBufferIndex = frame.VertexBufferIndex,
            IndexBufferIndex = frame.IndexBufferIndex,
            PatchCount = checked((uint)frame.PatchCount),
            CardCount = checked((uint)frame.CardCount),
            CurrentFrameIndex = checked((uint)frame.FrameSlot),
            WindTimeSeconds = frame.WindTimeSeconds,
            CadenceGenerationLow = unchecked((uint)frame.CadenceGeneration)
        };
        _context.Api.CmdPushConstants(
            commandBuffer,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUDdgiFoliageProxyGenerationPushConstants>(),
            &push);
        uint groupCount = checked(
            (push.CardCount + WorkgroupSize - 1u) / WorkgroupSize);
        _context.Api.CmdDispatch(commandBuffer, groupCount, 1, 1);
        RecordAccelerationStructureInputBarriers(commandBuffer, frame);
        sceneData.CpuDdgiFoliageProxyGenerationRecordMicroseconds =
            ElapsedMicroseconds(start);
    }

    private void RecordAccelerationStructureInputBarriers(
        CommandBuffer commandBuffer,
        DdgiFoliageProxyFrame frame)
    {
        ulong vertexBytes = checked(
            (ulong)frame.VertexCount * (ulong)Marshal.SizeOf<GPUVertex>());
        ulong indexBytes = checked(
            (ulong)frame.TriangleCount * 3UL * sizeof(uint));
        Span<BufferMemoryBarrier2> barriers =
            stackalloc BufferMemoryBarrier2[2];
        barriers[0] = CreateOutputBarrier(
            _bufferManager.GetBuffer(frame.VertexBuffer),
            vertexBytes);
        barriers[1] = CreateOutputBarrier(
            _bufferManager.GetBuffer(frame.IndexBuffer),
            indexBytes);
        fixed (BufferMemoryBarrier2* barrierPointer = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)barriers.Length,
                PBufferMemoryBarriers = barrierPointer
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private static BufferMemoryBarrier2 CreateOutputBarrier(
        Silk.NET.Vulkan.Buffer buffer,
        ulong byteSize) =>
        new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = buffer,
            Offset = 0,
            Size = byteSize
        };

    private void ValidatePushConstantRange()
    {
        PhysicalDeviceProperties properties = default;
        _context.Api.GetPhysicalDeviceProperties(
            _context.PhysicalDevice,
            &properties);
        uint required =
            (uint)Marshal.SizeOf<GPUDdgiFoliageProxyGenerationPushConstants>();
        if (required > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes " +
                $"of push constants, but DDGI foliage generation requires {required} bytes.");
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
                "Failed to create DDGI foliage proxy pipeline cache",
                result);
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2]
        {
            _bindlessHeap.StorageBufferSetLayout,
            _bindlessHeap.TextureSamplerSetLayout
        };
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Size = (uint)Marshal.SizeOf<GPUDdgiFoliageProxyGenerationPushConstants>()
        };
        var createInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
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
                "Failed to create DDGI foliage proxy pipeline layout",
                result);
    }

    private void CreatePipeline()
    {
        ShaderModule shader = default;
        try
        {
            shader = ShaderModuleLoader.Load(_context, ShaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shader,
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
                        $"DdgiFoliageProxyGenerationPass:{ShaderName}"),
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
                    "Failed to create DDGI foliage proxy compute pipeline",
                    result);
            _context.SetDebugName(
                _pipeline.Handle,
                ObjectType.Pipeline,
                "DDGI Foliage Proxy Generation");
        }
        finally
        {
            if (shader.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shader, null);
        }
    }

    private static long ElapsedMicroseconds(long startTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - startTimestamp) *
            1_000_000.0 / Stopwatch.Frequency);

    private void CleanupPipelineResources()
    {
        if (_pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(
                _context.Device,
                _pipelineLayout,
                null);
        if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(
                _context.Device,
                _pipelineCache,
                null);
        if (_entryPointName != 0)
        {
            SilkMarshal.Free(_entryPointName);
            _entryPointName = 0;
        }
        _pipeline = default;
        _pipelineLayout = default;
        _pipelineCache = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IsAvailable = false;
        CleanupPipelineResources();
    }
}
