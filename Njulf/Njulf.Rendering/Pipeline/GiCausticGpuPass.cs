using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
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
/// Records the isolated C4 compute transaction.  Native resource lifetime,
/// descriptor publication, fence-owned header readback, and the decision to
/// expose a forward handoff remain owned by <see cref="GiCausticVulkanRuntime"/>.
///
/// <para>This recorder deliberately has no path that invents a light sample,
/// hero material, or photon endpoint.  Its task source must be supplied by a
/// typed tagged-light/hero-caster producer, and it is never constructed by the
/// checked-in fail-closed runtime qualification.</para>
/// </summary>
internal sealed unsafe class GiCausticGpuPass : IDisposable
{
    private const string EntryPoint = "main";
    private const uint GeneralWorkgroupSize = 64u;
    private const uint CacheBuildWorkgroupSize =
        GiCausticDeterministicBuildScratchLayout.WorkgroupSize;

    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly AccelerationStructureManager _accelerationStructureManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly nint _entryPointName;
    private DescriptorSetLayout _accelerationStructureSetLayout;
    private DescriptorPool _descriptorPool;
    private readonly DescriptorSet[] _accelerationStructureSets =
        new DescriptorSet[RenderingConstants.FramesInFlight];
    private readonly AccelerationStructureKHR[] _boundTlases =
        new AccelerationStructureKHR[RenderingConstants.FramesInFlight];
    private PipelineLayout _layout;
    private PipelineCache _pipelineCache;
    private VkPipeline _taskPipeline;
    private VkPipeline _tracePipeline;
    private VkPipeline _cacheBuildPipeline;
    private VkPipeline _resolvePipeline;
    private bool _disposed;

    internal GiCausticGpuPass(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        AccelerationStructureManager accelerationStructureManager,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _accelerationStructureManager = accelerationStructureManager ??
            throw new ArgumentNullException(nameof(accelerationStructureManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

        try
        {
            ValidateFixedBindlessSlots();
            GiCausticGpuAbi.VerifyManagedLayout();
            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUCausticPushConstantsV1>());
            if (!_context.RayQuerySupported || _context.KhrAccelerationStructure is null)
                throw new InvalidOperationException("C4 tagged transport requires Vulkan ray query.");
            CreateAccelerationStructureSetLayout();
            CreateAccelerationStructureDescriptorSets();
            CreatePipelineCache();
            CreatePipelineLayout();
            _taskPipeline = CreatePipeline(
                GiCausticGpuPassNames.TaskShader,
                "GI Caustic Task Pipeline");
            _tracePipeline = CreatePipeline(
                GiCausticGpuPassNames.TraceShader,
                "GI Caustic Tagged First-Diffuse Trace Pipeline");
            _cacheBuildPipeline = CreatePipeline(
                GiCausticGpuPassNames.CacheBuildShader,
                "GI Caustic Deterministic Cache Build Pipeline");
            _resolvePipeline = CreatePipeline(
                GiCausticGpuPassNames.ResolveShader,
                "GI Caustic Isolated Resolve Pipeline");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records task validation, tagged first-diffuse tracing, clear, and the
    /// deterministic compaction phase in that exact order.  The runtime must
    /// append a cache-header copy and wait for its submission fence before a
    /// resolve can observe the write bank.
    /// </summary>
    internal void RecordBuild(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers,
        in GiCausticTaggedTransportProducerContract producer,
        int frameIndex)
    {
        RecordTaskStage(commandBuffer, resourceManager, layout, token, buffers,
            producer, frameIndex);
        RecordTraceStage(commandBuffer, resourceManager, layout, token, buffers,
            frameIndex);
        RecordCacheBuildStage(commandBuffer, resourceManager, layout, token,
            buffers);
    }

    /// <summary>
    /// Records only immutable metadata validation and bounded photon-task
    /// generation. Splitting the transaction at this boundary lets the render
    /// graph describe the real task-to-trace dependency instead of registering
    /// placeholder C4 passes.
    /// </summary>
    internal void RecordTaskStage(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers,
        in GiCausticTaggedTransportProducerContract producer,
        int frameIndex)
    {
        ValidateBuildStageInputs(commandBuffer, resourceManager, layout, token,
            buffers, frameIndex);
        if (!producer.TryValidateForBuild(layout, token.Revision,
                out string producerReason))
        {
            throw new ArgumentException(
                "C4 tagged transport producer is not qualified: " + producerReason,
                nameof(producer));
        }

        uint scratchWords =
            GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(layout);
        Bind(commandBuffer, bindAccelerationStructure: false, frameIndex);
        RecordTaskUploadToTaskBarrier(commandBuffer, buffers.Tasks, producer);
        Dispatch(commandBuffer, _taskPipeline,
            CreateTaskPushConstants(resourceManager, layout, token, producer,
                scratchWords, GiCausticGpuBuildPhases.TaskReset), 1u);
        RecordC4StorageBarrier(commandBuffer, buffers);
        Dispatch(commandBuffer, _taskPipeline,
            CreateTaskPushConstants(resourceManager, layout, token, producer,
                scratchWords, GiCausticGpuBuildPhases.TaskValidateMetadata),
            DivideRoundUpAtLeastOne((uint)Math.Max(producer.ProposalPairCount,
                Math.Max(producer.EmitterCount, producer.HeroCount)),
                GeneralWorkgroupSize));
        RecordC4StorageBarrier(commandBuffer, buffers);
        Dispatch(commandBuffer, _taskPipeline,
            CreateTaskPushConstants(resourceManager, layout, token, producer,
                scratchWords, GiCausticGpuBuildPhases.TaskGenerate),
            DivideRoundUpAtLeastOne((uint)token.TaskCount, GeneralWorkgroupSize));
        RecordC4StorageBarrier(commandBuffer, buffers);
        Dispatch(commandBuffer, _taskPipeline,
            CreateTaskPushConstants(resourceManager, layout, token, producer,
                scratchWords, GiCausticGpuBuildPhases.TaskValidate),
            DivideRoundUpAtLeastOne((uint)token.TaskCount, GeneralWorkgroupSize));
        RecordC4StorageBarrier(commandBuffer, buffers);
    }

    /// <summary>Records only current-pose tagged transport to first diffuse hit.</summary>
    internal void RecordTraceStage(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers,
        int frameIndex)
    {
        ValidateBuildStageInputs(commandBuffer, resourceManager, layout, token,
            buffers, frameIndex);
        if (!_accelerationStructureManager.Active ||
            _accelerationStructureManager.TopLevelAccelerationStructureHandle.Handle == 0)
        {
            throw new InvalidOperationException(
                "C4 tagged transport requires a complete current-pose TLAS.");
        }

        uint scratchWords =
            GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(layout);
        UpdateAccelerationStructureDescriptor(frameIndex);
        Bind(commandBuffer, bindAccelerationStructure: true, frameIndex);
        RecordAccelerationStructureReadBarrier(commandBuffer);
        Dispatch(commandBuffer, _tracePipeline,
            resourceManager.CreatePushConstants(token, scratchWords,
                GiCausticGpuBuildPhases.CacheClear),
            DivideRoundUpAtLeastOne((uint)token.TaskCount, GeneralWorkgroupSize));
        RecordC4StorageBarrier(commandBuffer, buffers);
    }

    /// <summary>
    /// Records deterministic radix ordering, bottom-K retention, exact cell
    /// hashing, and the final write-bank publication header.
    /// </summary>
    internal void RecordCacheBuildStage(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers)
    {
        ValidateBuildStageInputs(commandBuffer, resourceManager, layout, token,
            buffers, frameIndex: 0);
        uint scratchWords =
            GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(layout);
        if (!GiCausticDeterministicBuildScratchLayout.TryCreate(
                layout.PhotonCapacity,
                out GiCausticDeterministicBuildScratchLayout buildLayout) ||
            buildLayout.RequiredBytes > layout.ScratchBytes)
        {
            throw new ArgumentException(
                "C4 deterministic build scratch ABI does not match the allocation.",
                nameof(layout));
        }

        Bind(commandBuffer, bindAccelerationStructure: false, frameIndex: 0);
        Dispatch(commandBuffer, _cacheBuildPipeline,
            resourceManager.CreatePushConstants(token, scratchWords,
                GiCausticGpuBuildPhases.CacheClear),
            DivideRoundUpAtLeastOne((uint)layout.CellTableCapacity,
                CacheBuildWorkgroupSize));
        RecordC4StorageBarrier(commandBuffer, buffers);
        Dispatch(commandBuffer, _cacheBuildPipeline,
            resourceManager.CreatePushConstants(token, scratchWords,
                GiCausticGpuBuildPhases.InitializeIndices),
            buildLayout.WorkgroupCount);
        RecordC4StorageBarrier(commandBuffer, buffers);

        for (uint key = 0u;
             key < GiCausticDeterministicBuildScratchLayout.RadixKeyCount;
             ++key)
        {
            for (uint byteIndex = 0u;
                 byteIndex < GiCausticDeterministicBuildScratchLayout.RadixBytesPerKey;
                 ++byteIndex)
            {
                DispatchCacheBuildPhase(commandBuffer, resourceManager, token,
                    buffers, scratchWords,
                    GiCausticGpuBuildPhases.EncodeRadix(
                        GiCausticGpuBuildPhases.RadixHistogram, key, byteIndex),
                    buildLayout.WorkgroupCount);
                DispatchCacheBuildPhase(commandBuffer, resourceManager, token,
                    buffers, scratchWords,
                    GiCausticGpuBuildPhases.EncodeRadix(
                        GiCausticGpuBuildPhases.RadixPrefix, key, byteIndex), 1u);
                DispatchCacheBuildPhase(commandBuffer, resourceManager, token,
                    buffers, scratchWords,
                    GiCausticGpuBuildPhases.EncodeRadix(
                        GiCausticGpuBuildPhases.RadixScatter, key, byteIndex),
                    buildLayout.WorkgroupCount);
            }
        }

        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.CompactLocalScan,
            buildLayout.WorkgroupCount);
        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.CompactGroupPrefix, 1u);
        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.CompactScatter,
            buildLayout.WorkgroupCount);
        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.StageSortedCells,
            DivideRoundUpAtLeastOne(
                (uint)layout.SourceLayout.MaximumOccupiedCells,
                CacheBuildWorkgroupSize));
        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.ClearCellTableForHash,
            DivideRoundUpAtLeastOne((uint)layout.CellTableCapacity,
                CacheBuildWorkgroupSize));
        DispatchCacheBuildPhase(commandBuffer, resourceManager, token, buffers,
            scratchWords, GiCausticGpuBuildPhases.HashAndFinalize, 1u);
    }

    private void ValidateBuildStageInputs(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers,
        int frameIndex)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resourceManager);
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
        if (!layout.IsValid || !buffers.IsComplete || token.IsDefault)
            throw new ArgumentException("C4 build resources or token are invalid.");
        RenderingConstants.ValidateFrameIndex(frameIndex);
    }

    private static GPUCausticPushConstantsV1 CreateTaskPushConstants(
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuResourceLayout layout,
        in GiCausticGpuBuildToken token,
        in GiCausticTaggedTransportProducerContract producer,
        uint scratchWords,
        uint phase)
    {
        GPUCausticPushConstantsV1 constants = resourceManager.CreatePushConstants(
            token, scratchWords, phase);
        constants.CandidateStagingWordOffset = checked((uint)(
            layout.EmitterRecordOffsetBytes / sizeof(uint)));
        constants.CachePhotonBankBaseWord = checked((uint)(
            layout.HeroRecordOffsetBytes / sizeof(uint)));
        constants.CacheBankTableWordOffset = checked((uint)(
            layout.ProposalPairRecordOffsetBytes / sizeof(uint)));
        constants.Flags = GiCausticGpuTaskGenerationFlags.Encode(
            producer.EmitterCount,
            producer.HeroCount,
            producer.ProposalPairCount);
        return constants;
    }

    private void DispatchCacheBuildPhase(
        CommandBuffer commandBuffer,
        GiCausticGpuResourceManager resourceManager,
        in GiCausticGpuBuildToken token,
        in GiCausticVulkanBuffers buffers,
        uint scratchWords,
        uint buildPhase,
        uint groupCount)
    {
        Dispatch(
            commandBuffer,
            _cacheBuildPipeline,
            resourceManager.CreatePushConstants(token, scratchWords, buildPhase),
            groupCount);
        RecordC4StorageBarrier(commandBuffer, buffers);
    }

    /// <summary>
    /// Resolves only a header-published immutable cache into C4 scratch and
    /// establishes visibility for the typed forward-composite consumer.  It
    /// neither writes scene color nor aliases DDGI storage.
    /// </summary>
    internal void RecordResolve(
        CommandBuffer commandBuffer,
        in GPUCausticPushConstantsV1 constants,
        in GiCausticVulkanBuffers buffers,
        in GiCausticForwardCompositeConsumerContract consumer)
    {
        ThrowIfDisposed();
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
        if (!buffers.IsComplete || constants.ResolveRequestCount == 0u)
            throw new ArgumentException("C4 resolve resources or request count are invalid.");
        if (!consumer.TryValidate(constants.ScratchWordCapacity, out string consumerReason))
        {
            throw new ArgumentException(
                "C4 forward-composite consumer is not qualified: " + consumerReason,
                nameof(consumer));
        }

        Bind(commandBuffer, bindAccelerationStructure: false, frameIndex: 0);
        RecordResolveRequestToResolveBarrier(commandBuffer, buffers.Scratch, consumer);
        RecordPublishedCacheToResolveBarrier(commandBuffer, buffers.Cache);
        Dispatch(
            commandBuffer,
            _resolvePipeline,
            constants,
            DivideRoundUpAtLeastOne(constants.ResolveRequestCount, GeneralWorkgroupSize));
        RecordResolveToForwardCompositeBarrier(commandBuffer, buffers.Scratch, consumer);
    }

    private void Bind(
        CommandBuffer commandBuffer,
        bool bindAccelerationStructure,
        int frameIndex)
    {
        DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
        DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _layout,
            0u,
            1u,
            &storageSet,
            0u,
            null);
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _layout,
            1u,
            1u,
            &textureSet,
            0u,
            null);
        if (bindAccelerationStructure)
        {
            DescriptorSet accelerationStructureSet =
                _accelerationStructureSets[frameIndex];
            _context.Api.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _layout,
                2u,
                1u,
                &accelerationStructureSet,
                0u,
                null);
        }
    }

    private void Dispatch(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        in GPUCausticPushConstantsV1 constants,
        uint groupCountX)
    {
        if (pipeline.Handle == 0 || groupCountX == 0u)
            throw new InvalidOperationException("C4 compute pipeline is not available.");

        _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        GPUCausticPushConstantsV1 localConstants = constants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<GPUCausticPushConstantsV1>(),
            &localConstants);
        _context.Api.CmdDispatch(commandBuffer, groupCountX, 1u, 1u);
    }

    private void RecordTaskUploadToTaskBarrier(
        CommandBuffer commandBuffer,
        BufferHandle taskBuffer,
        in GiCausticTaggedTransportProducerContract producer)
    {
        VkBuffer buffer = _bufferManager.GetBuffer(taskBuffer);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            producer.ProducerWriteStageMask,
            producer.ProducerWriteAccessMask,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        ExecuteBufferBarrier(commandBuffer, barrier);
    }

    private void RecordC4StorageBarrier(
        CommandBuffer commandBuffer,
        in GiCausticVulkanBuffers buffers)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[4];
        barriers[0] = CreateStorageBarrier(_bufferManager.GetBuffer(buffers.Tasks));
        barriers[1] = CreateStorageBarrier(_bufferManager.GetBuffer(buffers.Photons));
        barriers[2] = CreateStorageBarrier(_bufferManager.GetBuffer(buffers.Cache));
        barriers[3] = CreateStorageBarrier(_bufferManager.GetBuffer(buffers.Scratch));
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordResolveRequestToResolveBarrier(
        CommandBuffer commandBuffer,
        BufferHandle scratchBuffer,
        in GiCausticForwardCompositeConsumerContract consumer)
    {
        VkBuffer buffer = _bufferManager.GetBuffer(scratchBuffer);
        ulong offset = checked((ulong)consumer.ResolveRequestWordOffset * sizeof(uint));
        ulong bytes = checked(
            (ulong)consumer.ResolveRequestCount * GiCausticGpuAbi.ResolveRequestBytes);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            consumer.RequestWriteStageMask,
            consumer.RequestWriteAccessMask,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            offset,
            bytes);
        ExecuteBufferBarrier(commandBuffer, barrier);
    }

    private void RecordPublishedCacheToResolveBarrier(
        CommandBuffer commandBuffer,
        BufferHandle cacheBuffer)
    {
        VkBuffer buffer = _bufferManager.GetBuffer(cacheBuffer);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.AllCommandsBit,
            AccessFlags2.MemoryWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit);
        ExecuteBufferBarrier(commandBuffer, barrier);
    }

    private void RecordAccelerationStructureReadBarrier(CommandBuffer commandBuffer)
    {
        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr |
                PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr |
                AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit,
            DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr |
                AccessFlags2.ShaderStorageReadBit
        };
        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1u,
            PMemoryBarriers = &memoryBarrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
    }

    private void RecordResolveToForwardCompositeBarrier(
        CommandBuffer commandBuffer,
        BufferHandle scratchBuffer,
        in GiCausticForwardCompositeConsumerContract consumer)
    {
        VkBuffer buffer = _bufferManager.GetBuffer(scratchBuffer);
        ulong resultWordOffset = checked(
            (ulong)consumer.ResolveRequestWordOffset +
            (ulong)consumer.ResolveRequestCount *
                (GiCausticGpuAbi.ResolveRequestBytes / sizeof(uint)));
        ulong offset = checked(resultWordOffset * sizeof(uint));
        ulong bytes = checked(
            (ulong)consumer.ResolveRequestCount * GiCausticGpuAbi.ResolveResultBytes);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            consumer.CompositeReadStageMask,
            consumer.CompositeReadAccessMask,
            offset,
            bytes);
        ExecuteBufferBarrier(commandBuffer, barrier);
    }

    private static BufferMemoryBarrier2 CreateStorageBarrier(VkBuffer buffer) =>
        BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);

    private void ExecuteBufferBarriers(
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

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
    }

    private static uint DivideRoundUpAtLeastOne(uint value, uint divisor)
    {
        ulong groups = ((ulong)value + divisor - 1UL) / divisor;
        return checked((uint)Math.Max(1UL, groups));
    }

    private static void ValidateFixedBindlessSlots()
    {
        GiCausticGpuBindlessSlots slots = GiCausticGpuAbi.BindlessSlots;
        slots.Validate();
        if (slots.TaskBufferIndex != 204 || slots.PhotonBufferIndex != 205 ||
            slots.CacheBufferIndex != 206 || slots.ScratchBufferIndex != 207)
        {
            throw new InvalidOperationException(
                "C4 requires immutable bindless storage slots 204 through 207.");
        }
    }

    private void ValidatePushConstantRange(uint requiredSize)
    {
        var properties = new PhysicalDeviceProperties();
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (requiredSize > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"C4 caustics require {requiredSize} bytes of push constants but " +
                $"the device exposes {properties.Limits.MaxPushConstantsSize}.");
        }
    }

    private void CreatePipelineCache()
    {
        if (_pipelineCacheService != null)
        {
            _pipelineCache = _pipelineCacheService.Cache;
            return;
        }

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
            throw new VulkanException("Failed to create C4 caustic pipeline cache.", result);
        _context.SetDebugName(
            _pipelineCache.Handle,
            ObjectType.PipelineCache,
            "GI Caustic Pipeline Cache");
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[3];
        setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
        setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;
        setLayouts[2] = _accelerationStructureSetLayout;
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = (uint)Marshal.SizeOf<GPUCausticPushConstantsV1>()
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 3u,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &pushConstantRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &layoutInfo,
            null,
            out _layout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 caustic pipeline layout.", result);
        _context.SetDebugName(
            _layout.Handle,
            ObjectType.PipelineLayout,
            "GI Caustic Pipeline Layout");
    }

    private void CreateAccelerationStructureSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0u,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1u,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1u,
            PBindings = &binding
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &info,
            null,
            out _accelerationStructureSetLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create C4 acceleration-structure descriptor layout.",
                result);
        }
        _context.SetDebugName(
            _accelerationStructureSetLayout.Handle,
            ObjectType.DescriptorSetLayout,
            "GI Caustic Acceleration Structure Set Layout");
    }

    private void CreateAccelerationStructureDescriptorSets()
    {
        uint descriptorSetCount = RenderingConstants.FramesInFlight;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = descriptorSetCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1u,
            PPoolSizes = &poolSize,
            MaxSets = descriptorSetCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C4 descriptor pool.", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[
            RenderingConstants.FramesInFlight];
        for (int frame = 0; frame < RenderingConstants.FramesInFlight; ++frame)
            layouts[frame] = _accelerationStructureSetLayout;
        fixed (DescriptorSet* sets = _accelerationStructureSets)
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
                sets);
        }
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate C4 TLAS descriptor sets.", result);
    }

    private void UpdateAccelerationStructureDescriptor(int frameIndex)
    {
        AccelerationStructureKHR tlas =
            _accelerationStructureManager.TopLevelAccelerationStructureHandle;
        if (tlas.Handle == 0)
            throw new InvalidOperationException("C4 cannot bind an empty TLAS.");
        if (_boundTlases[frameIndex].Handle == tlas.Handle)
            return;

        var accelerationStructureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1u,
            PAccelerationStructures = &tlas
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &accelerationStructureInfo,
            DstSet = _accelerationStructureSets[frameIndex],
            DstBinding = 0u,
            DescriptorCount = 1u,
            DescriptorType = DescriptorType.AccelerationStructureKhr
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1u, &write, 0u, null);
        _boundTlases[frameIndex] = tlas;
    }

    private VkPipeline CreatePipeline(string shaderName, string debugName)
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, shaderName);
            _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)_entryPointName
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _layout,
                BasePipelineHandle = default,
                BasePipelineIndex = -1
            };
            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateComputePipeline(
                    new PipelineArtifactId($"GiCaustic.{shaderName}"),
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1u,
                    &pipelineInfo,
                    null,
                    out pipeline);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create C4 caustic compute pipeline " + shaderName + ".",
                    result);
            }
            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
            return pipeline;
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DestroyPipeline(_taskPipeline);
        DestroyPipeline(_tracePipeline);
        DestroyPipeline(_cacheBuildPipeline);
        DestroyPipeline(_resolvePipeline);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_layout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        if (_accelerationStructureSetLayout.Handle != 0)
        {
            _context.Api.DestroyDescriptorSetLayout(
                _context.Device,
                _accelerationStructureSetLayout,
                null);
        }
        if (_pipelineCacheService is null && _pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
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
            throw new ObjectDisposedException(nameof(GiCausticGpuPass));
    }
}
