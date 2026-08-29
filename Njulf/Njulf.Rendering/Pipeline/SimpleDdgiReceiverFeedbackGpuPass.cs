using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
/// Records B1's exact receiver-feedback reset/capture/sort/reduce transaction.
/// This object owns only Vulkan pipeline state.  The resource runtime owns
/// allocation, descriptor lifetime, readback, and the strict one-frame-late
/// publication gate.
/// </summary>
// This is deliberately an assembly-private recording implementation.  The
// public B1 boundary is SimpleDdgiReceiverFeedbackVulkanRuntime; exposing a
// command recorder would let callers bypass its allocation, header-readback,
// and previous-frame publication gates.
internal sealed unsafe class SimpleDdgiReceiverFeedbackGpuPass : IDisposable
{
    private const string EntryPoint = "main";

    private readonly VulkanContext _context;
    private readonly BindlessHeap _bindlessHeap;
    private readonly BufferManager _bufferManager;
    private readonly GiPipelineCacheService? _pipelineCacheService;
    private readonly nint _entryPointName;
    private PipelineLayout _layout;
    private PipelineCache _pipelineCache;
    private VkPipeline _resetPipeline;
    private VkPipeline _capturePipeline;
    private VkPipeline _histogramPipeline;
    private VkPipeline _prefixPipeline;
    private VkPipeline _scatterPipeline;
    private VkPipeline _reducePipeline;
    private bool _disposed;

    internal SimpleDdgiReceiverFeedbackGpuPass(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        BufferManager bufferManager,
        GiPipelineCacheService? pipelineCacheService = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _pipelineCacheService = pipelineCacheService;
        _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

        try
        {
            ValidateFixedBindlessSlots();
            SimpleDdgiReceiverFeedbackGpuSortAbi.VerifyManagedLayout();
            ValidatePushConstantRange(
                (uint)Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            _resetPipeline = CreatePipeline(
                "ddgi_receiver_feedback_reset.comp.spv",
                "Simple DDGI Receiver Feedback Reset");
            _capturePipeline = CreatePipeline(
                "ddgi_receiver_feedback_capture.comp.spv",
                "Simple DDGI Receiver Feedback Capture");
            _histogramPipeline = CreatePipeline(
                "ddgi_receiver_feedback_radix_histogram.comp.spv",
                "Simple DDGI Receiver Feedback Radix Histogram");
            _prefixPipeline = CreatePipeline(
                "ddgi_receiver_feedback_radix_prefix.comp.spv",
                "Simple DDGI Receiver Feedback Radix Prefix");
            _scatterPipeline = CreatePipeline(
                "ddgi_receiver_feedback_radix_scatter.comp.spv",
                "Simple DDGI Receiver Feedback Radix Scatter");
            _reducePipeline = CreatePipeline(
                "ddgi_receiver_feedback_reduce.comp.spv",
                "Simple DDGI Receiver Feedback Reduce");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Emits the complete bounded transaction.  Every phase is separated by a
    /// Vulkan synchronization2 storage barrier; callers must append the
    /// summary-header readback before submitting this command buffer.
    /// </summary>
    internal void Record(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackVulkanBuffers buffers,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer) => Record(
            commandBuffer,
            gpuLayout,
            token,
            buffers,
            producer,
            timestamps: null,
            frameIndex: 0);

    internal void Record(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackVulkanBuffers buffers,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer,
        GpuTimestampRecorder? timestamps,
        int frameIndex)
    {
        ThrowIfDisposed();
        if (commandBuffer.Handle == 0)
            throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));
        if (token.WriteBankIndex is < 0 or > 1 ||
            token.FeedbackGeneration == 0u ||
            token.ViewportGeneration == 0u ||
            token.FrameSerial == ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }
        if (!buffers.IsComplete)
            throw new ArgumentException("B1 Vulkan buffers are not fully allocated.", nameof(buffers));
        if (!producer.TryValidate(gpuLayout.RecordCapacity, out string producerReason))
        {
            throw new ArgumentException(
                "B1 capture producer is not the frozen 48-byte contract: " + producerReason,
                nameof(producer));
        }

        Bind(commandBuffer);

        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            SimpleDdgiReceiverFeedbackGpuTimingNames.Reset);
        try
        {
            Dispatch(
                commandBuffer,
                _resetPipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.Reset,
                    SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank),
                1u);
            RecordB1StorageBarrier(commandBuffer, buffers);
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
        }

        // The producer contract names the real Vulkan buffer and the exact
        // stage/access that completed it.  This deliberately does not infer a
        // source from the legacy 16-byte receiver gather arena.
        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            SimpleDdgiReceiverFeedbackGpuTimingNames.Capture);
        try
        {
            RecordProducerToCaptureBarrier(commandBuffer, producer);
            Dispatch(
                commandBuffer,
                _capturePipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.Capture,
                    SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
                    captureProducer: producer),
                DivideRoundUpAtLeastOne(
                    producer.CandidateRecordCount,
                    SimpleDdgiReceiverFeedbackGpuSortAbi.WorkgroupSize));
            RecordB1StorageBarrier(commandBuffer, buffers);
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
        }

        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            SimpleDdgiReceiverFeedbackGpuTimingNames.RawRadix);
        try
        {
            RecordRadixSort(
                commandBuffer,
                gpuLayout,
                token,
                buffers,
                SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords);
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
        }

        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            SimpleDdgiReceiverFeedbackGpuTimingNames.PartialBuildAndRadix);
        try
        {
            Dispatch(
                commandBuffer,
                _reducePipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.BuildPartials,
                    SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank),
                gpuLayout.RadixWorkgroupCount);
            RecordB1StorageBarrier(commandBuffer, buffers);

            RecordRadixSort(
                commandBuffer,
                gpuLayout,
                token,
                buffers,
                SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials);
            RecordRadixSort(
                commandBuffer,
                gpuLayout,
                token,
                buffers,
                SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials);
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
        }

        timestamps?.BeginPass(
            commandBuffer,
            frameIndex,
            SimpleDdgiReceiverFeedbackGpuTimingNames.ReduceAndFinalize);
        try
        {
            Dispatch(
                commandBuffer,
                _reducePipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.ReduceProbeSummaries,
                    SimpleDdgiReceiverFeedbackGpuInputKind.ProbePartials,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchTemporary),
                gpuLayout.RadixWorkgroupCount);
            RecordB1StorageBarrier(commandBuffer, buffers);

            Dispatch(
                commandBuffer,
                _reducePipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.ReduceFallbackPressure,
                    SimpleDdgiReceiverFeedbackGpuInputKind.FallbackPartials,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.ScratchFallback),
                gpuLayout.RadixWorkgroupCount);
            RecordB1StorageBarrier(commandBuffer, buffers);

            // Validated is the shader's final store. A later
            // compute-to-transfer barrier around the copied header makes this
            // publication observable to the fence-owned CPU readback without
            // allowing same-frame scheduling.
            Dispatch(
                commandBuffer,
                _reducePipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.Finalize,
                    SimpleDdgiReceiverFeedbackGpuInputKind.RawRecords,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank,
                    SimpleDdgiReceiverFeedbackGpuItemLocation.RecordBank),
                1u);
        }
        finally
        {
            timestamps?.EndPass(commandBuffer, frameIndex);
        }
    }

    private void RecordRadixSort(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        in SimpleDdgiReceiverFeedbackVulkanBuffers buffers,
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind)
    {
        uint passCount = SimpleDdgiReceiverFeedbackGpuSortAbi.GetRadixPassCount(inputKind);
        for (uint passIndex = 0u; passIndex < passCount; ++passIndex)
        {
            if (!SimpleDdgiReceiverFeedbackGpuSortAbi.TryGetRadixDispatch(
                    inputKind,
                    passIndex,
                    out SimpleDdgiReceiverFeedbackGpuRadixDispatch dispatch))
            {
                throw new InvalidOperationException("B1 radix dispatch sequence is incomplete.");
            }

            Dispatch(
                commandBuffer,
                _histogramPipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.RadixHistogram,
                    inputKind,
                    dispatch.InputLocation,
                    dispatch.OutputLocation,
                    passIndex,
                    dispatch.Flags),
                gpuLayout.RadixWorkgroupCount);
            RecordB1StorageBarrier(commandBuffer, buffers);

            Dispatch(
                commandBuffer,
                _prefixPipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.RadixPrefix,
                    inputKind,
                    dispatch.InputLocation,
                    dispatch.OutputLocation,
                    passIndex,
                    dispatch.Flags),
                1u);
            RecordB1StorageBarrier(commandBuffer, buffers);

            Dispatch(
                commandBuffer,
                _scatterPipeline,
                CreatePushConstants(
                    gpuLayout,
                    token,
                    SimpleDdgiReceiverFeedbackGpuOperation.RadixScatter,
                    inputKind,
                    dispatch.InputLocation,
                    dispatch.OutputLocation,
                    passIndex,
                    dispatch.Flags),
                gpuLayout.RadixWorkgroupCount);
            RecordB1StorageBarrier(commandBuffer, buffers);
        }
    }

    private static GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants CreatePushConstants(
        in SimpleDdgiReceiverFeedbackGpuSortLayout gpuLayout,
        in SimpleDdgiReceiverFeedbackFrameToken token,
        SimpleDdgiReceiverFeedbackGpuOperation operation,
        SimpleDdgiReceiverFeedbackGpuInputKind inputKind,
        SimpleDdgiReceiverFeedbackGpuItemLocation inputLocation,
        SimpleDdgiReceiverFeedbackGpuItemLocation outputLocation,
        uint radixPassIndex = 0u,
        SimpleDdgiReceiverFeedbackGpuSortFlags flags =
            SimpleDdgiReceiverFeedbackGpuSortFlags.None,
        SimpleDdgiReceiverFeedbackCaptureProducerContract captureProducer = default)
    {
        uint captureSourceIndex = operation ==
            SimpleDdgiReceiverFeedbackGpuOperation.Capture
            ? captureProducer.CandidateBufferBindlessIndex
            : 0u;
        uint captureSourceOffsetWords = operation ==
            SimpleDdgiReceiverFeedbackGpuOperation.Capture
            ? captureProducer.CandidateRecordOffsetWords
            : 0u;
        uint captureSourceCount = operation ==
            SimpleDdgiReceiverFeedbackGpuOperation.Capture
            ? captureProducer.CandidateRecordCount
            : 0u;
        uint captureSourceControlOffsetWords = operation ==
            SimpleDdgiReceiverFeedbackGpuOperation.Capture
            ? captureProducer.CandidateControlOffsetWords
            : 0u;
        if (!SimpleDdgiReceiverFeedbackGpuSortAbi.TryCreatePushConstants(
                gpuLayout,
                operation,
                token.FeedbackGeneration,
                token.ViewportGeneration,
                token.FrameSerial,
                checked((uint)token.WriteBankIndex),
                checked((uint)token.WriteBankIndex),
                inputKind,
                inputLocation,
                outputLocation,
                radixPassIndex,
                inputCount: 0u,
                captureSourceBufferIndex: captureSourceIndex,
                captureSourceRecordOffsetWords: captureSourceOffsetWords,
                captureSourceRecordCount: captureSourceCount,
                captureSourceControlOffsetWords: captureSourceControlOffsetWords,
                flags: flags,
                constants: out GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants constants,
                reason: out string reason))
        {
            throw new InvalidOperationException(
                "B1 GPU sort push-constant construction failed: " + reason);
        }

        return constants;
    }

    private void Bind(CommandBuffer commandBuffer)
    {
        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _resetPipeline);
        DescriptorSet storageSet = _bindlessHeap.StorageBufferSet;
        DescriptorSet textureSet = _bindlessHeap.TextureSamplerSet;
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _layout,
            0,
            1,
            &storageSet,
            0,
            null);
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _layout,
            1,
            1,
            &textureSet,
            0,
            null);
    }

    private void Dispatch(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        in GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants constants,
        uint groupCountX)
    {
        if (pipeline.Handle == 0 || groupCountX == 0u)
            throw new InvalidOperationException("B1 compute pipeline is not available.");

        _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants localConstants = constants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.ComputeBit,
            0,
            (uint)Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants>(),
            &localConstants);
        _context.Api.CmdDispatch(commandBuffer, groupCountX, 1u, 1u);
    }

    private void RecordProducerToCaptureBarrier(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackCaptureProducerContract producer)
    {
        VkBuffer source = _bufferManager.GetBuffer(producer.CandidateBuffer);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            source,
            producer.ProducerWriteStageMask,
            producer.ProducerWriteAccessMask,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit,
            0UL,
            producer.CandidateBufferDescriptorBytes);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordB1StorageBarrier(
        CommandBuffer commandBuffer,
        in SimpleDdgiReceiverFeedbackVulkanBuffers buffers)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[3];
        barriers[0] = CreateB1StorageBarrier(_bufferManager.GetBuffer(buffers.RecordBanks));
        barriers[1] = CreateB1StorageBarrier(
            _bufferManager.GetBuffer(buffers.SortScratch),
            buffers.SortScratchOffset,
            buffers.SortScratchBytes);
        barriers[2] = CreateB1StorageBarrier(_bufferManager.GetBuffer(buffers.SummaryBanks));
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private static BufferMemoryBarrier2 CreateB1StorageBarrier(
        VkBuffer buffer,
        ulong offset = 0UL,
        ulong size = Vk.WholeSize) =>
        BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            offset,
            size);

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

    private static uint DivideRoundUpAtLeastOne(uint value, uint divisor)
    {
        ulong groups = ((ulong)value + divisor - 1UL) / divisor;
        return checked((uint)Math.Max(1UL, groups));
    }

    private static void ValidateFixedBindlessSlots()
    {
        if (SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot != 194u ||
            SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot != 195u ||
            SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot != 196u ||
            BindlessIndex.SimpleDdgiReceiverFeedbackRecordsBuffer !=
                (int)SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot ||
            BindlessIndex.SimpleDdgiReceiverFeedbackSortScratchBuffer !=
                (int)SimpleDdgiReceiverFeedbackGpuSortAbi.SortScratchBindlessSlot ||
            BindlessIndex.SimpleDdgiReceiverFeedbackSummaryBuffer !=
                (int)SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot ||
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot != 209u ||
            BindlessIndex.SimpleDdgiReceiverFeedbackCandidateBuffer !=
                (int)SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot)
        {
            throw new InvalidOperationException(
                "B1 requires immutable bindless storage slots 194-196 and 209.");
        }
    }

    private void ValidatePushConstantRange(uint requiredSize)
    {
        var properties = new PhysicalDeviceProperties();
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (requiredSize > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"B1 receiver feedback requires {requiredSize} bytes of push constants but " +
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
            throw new VulkanException("Failed to create B1 receiver-feedback pipeline cache.", result);
        _context.SetDebugName(
            _pipelineCache.Handle,
            ObjectType.PipelineCache,
            "Simple DDGI Receiver Feedback Pipeline Cache");
    }

    private void CreatePipelineLayout()
    {
        DescriptorSetLayout* setLayouts = stackalloc DescriptorSetLayout[2];
        setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
        setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackGpuSortPushConstants>()
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 2,
            PSetLayouts = setLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &layoutInfo,
            null,
            out _layout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create B1 receiver-feedback pipeline layout.", result);
        _context.SetDebugName(
            _layout.Handle,
            ObjectType.PipelineLayout,
            "Simple DDGI Receiver Feedback Pipeline Layout");
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
                    new PipelineArtifactId(
                        $"SimpleDdgi.ReceiverFeedback.{shaderName}"),
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out pipeline);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create B1 receiver-feedback compute pipeline " + shaderName + ".",
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

        DestroyPipeline(_resetPipeline);
        DestroyPipeline(_capturePipeline);
        DestroyPipeline(_histogramPipeline);
        DestroyPipeline(_prefixPipeline);
        DestroyPipeline(_scatterPipeline);
        DestroyPipeline(_reducePipeline);
        if (_layout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
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
            throw new ObjectDisposedException(nameof(SimpleDdgiReceiverFeedbackGpuPass));
    }
}
