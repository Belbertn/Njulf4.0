using System;
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
/// Assembly-private C3 compute recorder.  The public runtime owns source-cache
/// admission, bindless publication, allocation lifetime, compact header
/// readback, and the rule that only a validated read bank may be sampled.
/// </summary>
internal sealed unsafe class SimpleDdgiGuidingGpuPass : IDisposable
{
    private const uint TrainAndSampleWorkgroupSize = 64u;
    private const ulong ValidationCounterByteCount =
        SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount;
    private const int PassKindCount = 5;
    private const int DescriptorSetCount =
        RenderingConstants.FramesInFlight * PassKindCount;

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly SimpleDdgiStoragePackingMode _storagePackingMode;
    private readonly nint _entryPointName;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    // Private descriptor sets are frame-ringed.  Unlike the renderer's
    // update-after-bind global heap these ordinary sets cannot be updated
    // while an older command buffer still references them.
    private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[DescriptorSetCount];
    private PipelineLayout _pipelineLayout;
    private PipelineLayout _extractPipelineLayout;
    private PipelineCache _pipelineCache;
    private VkPipeline _trainPipeline;
    private VkPipeline _buildPipeline;
    private VkPipeline _samplePipeline;
    private VkPipeline _validatePipeline;
    private VkPipeline _extractPipeline;
    private bool _disposed;

    public SimpleDdgiGuidingGpuPass(
        VulkanContext context,
        BufferManager bufferManager,
        BindlessHeap bindlessHeap,
        SimpleDdgiStoragePackingMode storagePackingMode)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
        if (!Enum.IsDefined(storagePackingMode))
            throw new ArgumentOutOfRangeException(nameof(storagePackingMode));
        _storagePackingMode = storagePackingMode;
        _entryPointName = SilkMarshal.StringToPtr("main");

        try
        {
            SimpleDdgiGuidingGpuAbi.VerifyManagedLayout();
            ValidatePushConstantRange(
                (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingPushConstants>());
            ValidatePushConstantRange(
                (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingExtractPushConstants>());
            CreateDescriptorSetLayout();
            CreateDescriptorPoolAndSets();
            CreatePipelineCache();
            CreatePipelineLayout();
            CreateExtractPipelineLayout();
            _trainPipeline = CreatePipeline(
                SimpleDdgiGuidingGpuPassNames.TrainShader,
                "Simple DDGI Guiding Train",
                _pipelineLayout);
            _buildPipeline = CreatePipeline(
                SimpleDdgiGuidingGpuPassNames.BuildShader,
                "Simple DDGI Guiding Build",
                _pipelineLayout);
            _samplePipeline = CreatePipeline(
                SimpleDdgiGuidingGpuPassNames.SampleShader,
                "Simple DDGI Guiding Sample",
                _pipelineLayout);
            _validatePipeline = CreatePipeline(
                SimpleDdgiGuidingGpuPassNames.ValidateShader,
                "Simple DDGI Guiding Validate",
                _pipelineLayout);
            _extractPipeline = CreatePipeline(
                ResolveExtractShader(storagePackingMode),
                "Simple DDGI Guiding Trace Training Extract",
                _extractPipelineLayout);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Emits the full candidate-bank transaction.  The candidate is cleared,
    /// then trained, built, and validated before the caller copies headers for
    /// CPU publication.  The currently readable bank is never bound here.
    /// </summary>
    internal void RecordBuild(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        RecordTrain(commandBuffer, frameIndex, layout, token, buffers, workload);
        RecordHierarchyBuild(commandBuffer, frameIndex, layout, token, buffers,
            workload);
        RecordValidate(commandBuffer, frameIndex, layout, token, buffers,
            workload);
    }

    /// <summary>
    /// Extracts radiometric observations from the completed DDGI trace and
    /// reduces them into deterministic per-probe FP32 leaf partials.
    /// </summary>
    internal void RecordTrain(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ValidateBuildArguments(commandBuffer, layout, token, buffers, workload);

        if (workload.TraceTrainingSource.StoragePackingMode !=
            _storagePackingMode)
        {
            throw new ArgumentException(
                "C3 trace extraction mode differs from the configured pipeline.",
                nameof(workload));
        }

        // Materialize the training signal from the exact completed DDGI queue
        // and ray scratch. This removes the former trust boundary where a
        // caller could upload records unrelated to the trace transaction.
        // Clear the complete bounded output first: a malformed work item can
        // return before its per-record loop, and stale records must never be
        // interpreted as fresh radiometric evidence.
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(workload.TrainingRecords.Buffer),
            workload.TrainingRecords.OffsetBytes,
            workload.TrainingRecords.RangeBytes,
            0u);
        RecordExtractInputBarriers(commandBuffer, workload);
        ResetValidationCounters(commandBuffer, workload.ValidationCounters);
        WriteStorageSet(
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Extract),
            new StorageRange(workload.TrainingRecords),
            new StorageRange(workload.TrainingWorkItems),
            new StorageRange(workload.ValidationCounters),
            new StorageRange(workload.ValidationCounters));
        DispatchExtract(
            commandBuffer,
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Extract),
            CreateExtractPushConstants(layout, workload),
            workload.TrainingWorkItems.ElementCount);
        RecordComputeStorageBarrier(
            commandBuffer,
            workload.TrainingRecords,
            workload.ValidationCounters);

        // Train writes every declared partial; zeroing the complete bounded
        // scratch prevents an invalid/missing source record from inheriting a
        // prior transaction.
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(buffers.TrainingScratch),
            buffers.TrainingScratchOffsetBytes,
            buffers.TrainingScratchRangeBytes,
            0u);
        RecordTransferToComputeBarrier(
            commandBuffer,
            TrainingScratchRange(buffers));

        WriteStorageSet(
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Train),
            new StorageRange(workload.TrainingRecords),
            new StorageRange(workload.TrainingWorkItems),
            TrainingScratchRange(buffers),
            new StorageRange(workload.ValidationCounters));
        Dispatch(
            commandBuffer,
            _trainPipeline,
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Train),
            CreatePushConstants(
                layout,
                token.ReadBankIndex,
                token.WriteBankIndex,
                token.CandidateBankGeneration,
                token.TargetProposalEpoch,
                workload.TrainingWorkItems.ElementCount),
            workload.TrainingWorkItems.ElementCount);
        RecordComputeStorageBarrier(
            commandBuffer,
            TrainingScratchRange(buffers),
            workload.ValidationCounters);
    }

    /// <summary>
    /// Builds one candidate hierarchy from the completed FP32 partials. The
    /// candidate bank is cleared in this stage and remains unpublished.
    /// </summary>
    internal void RecordHierarchyBuild(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ValidateBuildArguments(commandBuffer, layout, token, buffers, workload);

        BufferHandle writeBank = token.WriteBankIndex == 0
            ? buffers.DistributionBank0
            : buffers.DistributionBank1;
        RecordExternalInputBarrier(commandBuffer, workload.BuildWorkItems);
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(writeBank),
            0UL,
            _bufferManager.GetBufferSize(writeBank),
            0u);
        RecordTransferToComputeBarrier(
            commandBuffer,
            StorageRange.ForWholeBuffer(writeBank, _bufferManager));

        WriteStorageSet(
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Build),
            new StorageRange(workload.BuildWorkItems),
            TrainingScratchRange(buffers),
            StorageRange.ForWholeBuffer(writeBank, _bufferManager),
            new StorageRange(workload.ValidationCounters));
        Dispatch(
            commandBuffer,
            _buildPipeline,
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Build),
            CreatePushConstants(
                layout,
                token.ReadBankIndex,
                token.WriteBankIndex,
                token.CandidateBankGeneration,
                token.TargetProposalEpoch,
                workload.BuildWorkItems.ElementCount),
            workload.BuildWorkItems.ElementCount);
        RecordComputeStorageBarrier(
            commandBuffer,
            StorageRange.ForWholeBuffer(writeBank, _bufferManager),
            workload.ValidationCounters);
    }

    /// <summary>
    /// Performs the mandatory full candidate-header validation. A failed item
    /// clears BuildComplete in-place; CPU publication still requires the later
    /// fence-complete compact header readback.
    /// </summary>
    internal void RecordValidate(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        ValidateBuildArguments(commandBuffer, layout, token, buffers, workload);

        BufferHandle writeBank = token.WriteBankIndex == 0
            ? buffers.DistributionBank0
            : buffers.DistributionBank1;

        // Validation is mandatory for this runtime boundary.  A validation
        // failure clears BuildComplete/set Invalid in the same candidate bank;
        // header readback then rejects the whole publication transaction.
        WriteStorageSet(
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Validate),
            StorageRange.ForWholeBuffer(writeBank, _bufferManager),
            new StorageRange(workload.BuildWorkItems),
            new StorageRange(workload.ValidationCounters),
            new StorageRange(workload.ValidationCounters));
        Dispatch(
            commandBuffer,
            _validatePipeline,
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Validate),
            CreatePushConstants(
                layout,
                token.ReadBankIndex,
                token.WriteBankIndex,
                token.CandidateBankGeneration,
                token.TargetProposalEpoch,
                workload.BuildWorkItems.ElementCount),
            workload.BuildWorkItems.ElementCount);
        RecordComputeStorageBarrier(
            commandBuffer,
            StorageRange.ForWholeBuffer(writeBank, _bufferManager),
            workload.ValidationCounters);
    }

    /// <summary>
    /// Samples only an already published bank and exposes the exact 64-byte
    /// payload (including the generation-time mixture PDF) to the source-cache
    /// consumer specified by the handshake.
    /// </summary>
    internal void RecordSample(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingLayout layout,
        int readBankIndex,
        uint readBankGeneration,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        in SimpleDdgiGuidingSampleWorkload workload)
    {
        ThrowIfDisposed();
        RenderingConstants.ValidateFrameIndex(frameIndex);
        if (commandBuffer.Handle == 0 || readBankIndex is < 0 or > 1 ||
            !buffers.IsComplete || readBankGeneration == 0u)
        {
            throw new ArgumentException(
                "C3 published-bank sample arguments are invalid.",
                nameof(workload));
        }
        if (!workload.TryValidate(handshake, out string workloadReason))
        {
            throw new ArgumentException(
                "C3 published-bank sample workload is invalid: " + workloadReason,
                nameof(workload));
        }

        BufferHandle readBank = readBankIndex == 0
            ? buffers.DistributionBank0
            : buffers.DistributionBank1;
        RecordSampleInputBarriers(
            commandBuffer,
            workload.SampleRequests,
            workload.ValidationCounters);
        ResetValidationCounters(commandBuffer, workload.ValidationCounters);
        RecordComputeStorageBarrier(commandBuffer, readBank);

        var sidecar = new StorageRange(
            handshake.DirectionPdfSidecar,
            handshake.DirectionPdfSidecarOffsetBytes,
            handshake.DirectionPdfSidecarBytes);
        // Slot 203 stays source-cache-owned.  The owner supplies the exact
        // preceding access for this range, whether that was a prior payload
        // consumer or a source-cache write.  C3 neither infers a stage from
        // the global bindless slot nor publishes a replacement descriptor.
        RecordSidecarReuseBarrier(commandBuffer, sidecar, handshake);
        WriteStorageSet(
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Sample),
            StorageRange.ForWholeBuffer(readBank, _bufferManager),
            new StorageRange(workload.SampleRequests),
            sidecar,
            new StorageRange(workload.ValidationCounters));
        Dispatch(
            commandBuffer,
            _samplePipeline,
            DescriptorSetFor(frameIndex, SimpleDdgiGuidingPassKind.Sample),
            CreatePushConstants(
                layout,
                readBankIndex,
                writeBankIndex: -1,
                targetDistributionGeneration: readBankGeneration,
                targetProposalEpoch: 0u,
                dispatchCount: workload.SampleRequests.ElementCount),
            DivideRoundUp(workload.SampleRequests.ElementCount,
                TrainAndSampleWorkgroupSize));

        RecordSidecarConsumerBarrier(commandBuffer, sidecar, handshake);
    }

    private static void ValidateBuildArguments(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingLayout layout,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingVulkanBuffers buffers,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        if (commandBuffer.Handle == 0 || !buffers.IsComplete ||
            token.AllocationEpoch == 0UL || token.WriteBankIndex is < 0 or > 1 ||
            token.CandidateBankGeneration == 0u || token.TargetProposalEpoch == 0u ||
            token.GuidingAbiVersion != SimpleDdgiGuidingGpuAbi.Version)
        {
            throw new ArgumentException(
                "C3 candidate-bank build arguments are invalid.",
                nameof(workload));
        }
        if (!workload.TryValidate(layout, out string reason))
        {
            throw new ArgumentException(
                "C3 candidate-bank build workload is invalid: " + reason,
                nameof(workload));
        }
    }

    private static GPUSimpleDdgiGuidingPushConstants CreatePushConstants(
        in SimpleDdgiGuidingLayout layout,
        int readBankIndex,
        int writeBankIndex,
        uint targetDistributionGeneration,
        uint targetProposalEpoch,
        uint dispatchCount)
    {
        if (layout.PersistentBankStrideBytes % sizeof(uint) != 0UL ||
            layout.PhysicalProbeCapacity <= 0 || layout.LeafResolution <= 0 ||
            layout.HierarchyWeightCount <= 0 || dispatchCount == 0u)
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
        return new GPUSimpleDdgiGuidingPushConstants
        {
            AbiVersion = SimpleDdgiGuidingGpuAbi.Version,
            PhysicalProbeCapacity = checked((uint)layout.PhysicalProbeCapacity),
            LeafResolution = checked((uint)layout.LeafResolution),
            HierarchyWeightCount = checked((uint)layout.HierarchyWeightCount),
            BankStrideWords = checked((uint)(layout.PersistentBankStrideBytes / sizeof(uint))),
            DispatchCount = dispatchCount,
            ReadBankIndex = readBankIndex < 0 ? uint.MaxValue : checked((uint)readBankIndex),
            WriteBankIndex = writeBankIndex < 0 ? uint.MaxValue : checked((uint)writeBankIndex),
            TargetDistributionGeneration = targetDistributionGeneration,
            TargetProposalEpoch = targetProposalEpoch,
            Flags = 0u,
            // Sample uses this as the exact persistent sidecar stride. Other
            // passes keep the field inert while sharing the frozen push ABI.
            Reserved = checked((uint)Math.Max(0, layout.DirectionSlotsPerProbe))
        };
    }

    private static GPUSimpleDdgiGuidingExtractPushConstants
        CreateExtractPushConstants(
            in SimpleDdgiGuidingLayout layout,
            in SimpleDdgiGuidingBuildWorkload workload)
    {
        SimpleDdgiGuidingTraceTrainingSource source =
            workload.TraceTrainingSource;
        if (layout.PhysicalProbeCapacity <= 0 || layout.LeafResolution <= 0 ||
            layout.DirectionSlotsPerProbe <= 0 ||
            workload.TrainingWorkItems.ElementCount == 0u ||
            workload.TrainingRecords.ElementCount == 0u ||
            source.ProbeUpdateQueue.ElementCount == 0u ||
            source.RayResultScratch.ElementCount == 0u)
        {
            throw new ArgumentOutOfRangeException(nameof(workload));
        }

        return new GPUSimpleDdgiGuidingExtractPushConstants
        {
            AbiVersion = SimpleDdgiGuidingGpuAbi.Version,
            ParamsBufferIndex = source.ParamsBufferIndex,
            RayResultScratchBufferIndex = source.RayResultScratchBufferIndex,
            ProbeUpdateQueueBufferIndex = source.ProbeUpdateQueueBufferIndex,
            TrainingWorkItemCount = workload.TrainingWorkItems.ElementCount,
            TrainingRecordCapacity = workload.TrainingRecords.ElementCount,
            ProbeUpdateCapacity = source.ProbeUpdateQueue.ElementCount,
            RayResultCapacity = source.RayResultScratch.ElementCount,
            PhysicalProbeCapacity = checked((uint)layout.PhysicalProbeCapacity),
            LeafResolution = checked((uint)layout.LeafResolution),
            DirectionSlotsPerProbe =
                checked((uint)layout.DirectionSlotsPerProbe),
            Flags = 0u
        };
    }

    private void Dispatch(
        CommandBuffer commandBuffer,
        VkPipeline pipeline,
        DescriptorSet descriptorSet,
        in GPUSimpleDdgiGuidingPushConstants pushConstants,
        uint groupCountX)
    {
        if (pipeline.Handle == 0 || groupCountX == 0u)
            throw new InvalidOperationException("C3 compute pipeline or dispatch size is unavailable.");

        _context.Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
        DescriptorSet localSet = descriptorSet;
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            0u,
            1u,
            &localSet,
            0u,
            null);
        GPUSimpleDdgiGuidingPushConstants localPushConstants = pushConstants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _pipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingPushConstants>(),
            &localPushConstants);
        _context.Api.CmdDispatch(commandBuffer, groupCountX, 1u, 1u);
    }

    private void DispatchExtract(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        in GPUSimpleDdgiGuidingExtractPushConstants pushConstants,
        uint groupCountX)
    {
        if (_extractPipeline.Handle == 0 || groupCountX == 0u)
        {
            throw new InvalidOperationException(
                "C3 trace extractor pipeline or dispatch size is unavailable.");
        }

        _context.Api.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _extractPipeline);
        DescriptorSet* sets = stackalloc DescriptorSet[3];
        sets[0] = _bindlessHeap.StorageBufferSet;
        sets[1] = _bindlessHeap.TextureSamplerSet;
        sets[2] = descriptorSet;
        _context.Api.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _extractPipelineLayout,
            0u,
            3u,
            sets,
            0u,
            null);
        GPUSimpleDdgiGuidingExtractPushConstants localPushConstants =
            pushConstants;
        _context.Api.CmdPushConstants(
            commandBuffer,
            _extractPipelineLayout,
            ShaderStageFlags.ComputeBit,
            0u,
            (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingExtractPushConstants>(),
            &localPushConstants);
        _context.Api.CmdDispatch(commandBuffer, groupCountX, 1u, 1u);
    }

    private DescriptorSet DescriptorSetFor(
        int frameIndex,
        SimpleDdgiGuidingPassKind kind)
    {
        int kindIndex = (int)kind;
        if ((uint)kindIndex >= PassKindCount)
            throw new ArgumentOutOfRangeException(nameof(kind));
        RenderingConstants.ValidateFrameIndex(frameIndex);
        return _descriptorSets[checked(frameIndex * PassKindCount + kindIndex)];
    }

    private void WriteStorageSet(
        DescriptorSet descriptorSet,
        in StorageRange first,
        in StorageRange second,
        in StorageRange third,
        in StorageRange fourth)
    {
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[4];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
        WriteStorageDescriptor(0u, first, &infos[0], &writes[0]);
        WriteStorageDescriptor(1u, second, &infos[1], &writes[1]);
        WriteStorageDescriptor(2u, third, &infos[2], &writes[2]);
        WriteStorageDescriptor(3u, fourth, &infos[3], &writes[3]);
        for (int index = 0; index < 4; index++)
            writes[index].DstSet = descriptorSet;
        _context.Api.UpdateDescriptorSets(_context.Device, 4u, writes, 0u, null);
    }

    private void WriteStorageDescriptor(
        uint binding,
        in StorageRange range,
        DescriptorBufferInfo* info,
        WriteDescriptorSet* write)
    {
        if (!range.Buffer.IsValid || range.RangeBytes == 0UL)
            throw new InvalidOperationException("C3 descriptor range is invalid.");
        *info = new DescriptorBufferInfo
        {
            Buffer = _bufferManager.GetBuffer(range.Buffer),
            Offset = range.OffsetBytes,
            Range = range.RangeBytes
        };
        *write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = binding,
            DescriptorCount = 1u,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = info
        };
    }

    private void RecordExternalInputBarrier(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingExternalBuffer input)
    {
        BufferMemoryBarrier2 barrier = CreateExternalInputBarrier(input);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordExtractInputBarriers(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        SimpleDdgiGuidingTraceTrainingSource source =
            workload.TraceTrainingSource;
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[6];
        barriers[0] = CreateExternalInputBarrier(source.Params);
        barriers[1] = CreateExternalInputBarrier(source.RayResultScratch);
        barriers[2] = CreateExternalInputBarrier(source.ProbeUpdateQueue);
        barriers[3] = CreateExternalInputBarrier(workload.TrainingRecords);
        barriers[4] = CreateExternalInputBarrier(workload.TrainingWorkItems);
        barriers[5] = CreateExternalCounterResetBarrier(
            workload.ValidationCounters);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordSampleInputBarriers(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingExternalBuffer sampleRequests,
        in SimpleDdgiGuidingExternalBuffer validationCounters)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = CreateExternalInputBarrier(sampleRequests);
        barriers[1] = CreateExternalCounterResetBarrier(validationCounters);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private BufferMemoryBarrier2 CreateExternalInputBarrier(
        in SimpleDdgiGuidingExternalBuffer range) =>
        BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(range.Buffer),
            range.LastWriterStageMask,
            range.LastWriterAccessMask,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            range.OffsetBytes,
            range.RangeBytes);

    private BufferMemoryBarrier2 CreateExternalCounterResetBarrier(
        in SimpleDdgiGuidingExternalBuffer range) =>
        BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(range.Buffer),
            range.LastWriterStageMask,
            range.LastWriterAccessMask,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            range.OffsetBytes,
            ValidationCounterByteCount);

    private void ResetValidationCounters(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingExternalBuffer counters)
    {
        // Counter reset is recorder-owned. A caller-provided "cleared" flag
        // is not evidence of device memory state and cannot be part of the
        // publication safety boundary.
        _context.Api.CmdFillBuffer(
            commandBuffer,
            _bufferManager.GetBuffer(counters.Buffer),
            counters.OffsetBytes,
            ValidationCounterByteCount,
            0u);
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(counters.Buffer),
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit |
                AccessFlags2.ShaderStorageWriteBit,
            counters.OffsetBytes,
            ValidationCounterByteCount);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordTransferToComputeBarrier(
        CommandBuffer commandBuffer,
        in StorageRange first,
        in StorageRange second)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = CreateTransferToComputeBarrier(first);
        barriers[1] = CreateTransferToComputeBarrier(second);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordTransferToComputeBarrier(
        CommandBuffer commandBuffer,
        in StorageRange range)
    {
        BufferMemoryBarrier2 barrier = CreateTransferToComputeBarrier(range);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private BufferMemoryBarrier2 CreateTransferToComputeBarrier(
        in StorageRange range) =>
        BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(range.Buffer),
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            range.OffsetBytes,
            range.RangeBytes);

    private void RecordComputeStorageBarrier(
        CommandBuffer commandBuffer,
        in SimpleDdgiGuidingExternalBuffer first,
        in SimpleDdgiGuidingExternalBuffer second)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(first.Buffer),
            first.OffsetBytes,
            first.RangeBytes);
        barriers[1] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(second.Buffer),
            second.OffsetBytes,
            second.RangeBytes);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordComputeStorageBarrier(
        CommandBuffer commandBuffer,
        BufferHandle buffer)
    {
        BufferMemoryBarrier2 barrier = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(buffer),
            0UL,
            _bufferManager.GetBufferSize(buffer));
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordComputeStorageBarrier(
        CommandBuffer commandBuffer,
        in StorageRange first,
        in SimpleDdgiGuidingExternalBuffer second)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(first.Buffer),
            first.OffsetBytes,
            first.RangeBytes);
        barriers[1] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(second.Buffer),
            second.OffsetBytes,
            second.RangeBytes);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordComputeStorageBarrier(
        CommandBuffer commandBuffer,
        BufferHandle first,
        in SimpleDdgiGuidingExternalBuffer second,
        in SimpleDdgiGuidingExternalBuffer third)
    {
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[3];
        barriers[0] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(first),
            0UL,
            _bufferManager.GetBufferSize(first));
        barriers[1] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(second.Buffer),
            second.OffsetBytes,
            second.RangeBytes);
        barriers[2] = CreateComputeStorageBarrier(
            _bufferManager.GetBuffer(third.Buffer),
            third.OffsetBytes,
            third.RangeBytes);
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private static BufferMemoryBarrier2 CreateComputeStorageBarrier(
        VkBuffer buffer,
        ulong offset,
        ulong size) =>
        BarrierBuilder.BufferBarrier(
            buffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
            offset,
            size);

    private void RecordSidecarConsumerBarrier(
        CommandBuffer commandBuffer,
        in StorageRange sidecar,
        in SimpleDdgiGuidingSourceCacheHandshake handshake)
    {
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(sidecar.Buffer),
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            handshake.ConsumerReadStageMask,
            handshake.ConsumerReadAccessMask,
            sidecar.OffsetBytes,
            sidecar.RangeBytes);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void RecordSidecarReuseBarrier(
        CommandBuffer commandBuffer,
        in StorageRange sidecar,
        in SimpleDdgiGuidingSourceCacheHandshake handshake)
    {
        BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
            _bufferManager.GetBuffer(sidecar.Buffer),
            handshake.SourceCachePriorAccessStageMask,
            handshake.SourceCachePriorAccessMask,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            sidecar.OffsetBytes,
            sidecar.RangeBytes);
        Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[1];
        barriers[0] = barrier;
        ExecuteBufferBarriers(commandBuffer, barriers);
    }

    private void ExecuteBufferBarriers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<BufferMemoryBarrier2> barriers)
    {
        if (barriers.IsEmpty)
            return;
        fixed (BufferMemoryBarrier2* pointer = barriers)
        {
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)barriers.Length,
                PBufferMemoryBarriers = pointer
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private static uint DivideRoundUp(uint value, uint divisor)
    {
        if (value == 0u || divisor == 0u)
            throw new ArgumentOutOfRangeException(nameof(value));
        return checked((uint)(((ulong)value + divisor - 1UL) / divisor));
    }

    private void ValidatePushConstantRange(uint requiredSize)
    {
        var properties = new PhysicalDeviceProperties();
        _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
        if (requiredSize > properties.Limits.MaxPushConstantsSize)
        {
            throw new VulkanException(
                $"C3 guiding requires {requiredSize} bytes of push constants but the device " +
                $"exposes {properties.Limits.MaxPushConstantsSize}.");
        }
    }

    private void CreateDescriptorSetLayout()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[4];
        for (int binding = 0; binding < 4; binding++)
        {
            bindings[binding] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)binding,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1u,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4u,
            PBindings = bindings
        };
        Result result = _context.Api.CreateDescriptorSetLayout(
            _context.Device,
            &info,
            null,
            out _descriptorSetLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C3 guiding descriptor-set layout.", result);
        _context.SetDebugName(
            _descriptorSetLayout.Handle,
            ObjectType.DescriptorSetLayout,
            "Simple DDGI Guiding Descriptor Set Layout");
    }

    private void CreateDescriptorPoolAndSets()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = checked((uint)(DescriptorSetCount * 4))
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1u,
            PPoolSizes = &poolSize,
            MaxSets = (uint)DescriptorSetCount
        };
        Result result = _context.Api.CreateDescriptorPool(
            _context.Device,
            &poolInfo,
            null,
            out _descriptorPool);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C3 guiding descriptor pool.", result);

        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[DescriptorSetCount];
        DescriptorSet* sets = stackalloc DescriptorSet[DescriptorSetCount];
        for (int index = 0; index < DescriptorSetCount; index++)
            layouts[index] = _descriptorSetLayout;
        var allocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = (uint)DescriptorSetCount,
            PSetLayouts = layouts
        };
        result = _context.Api.AllocateDescriptorSets(_context.Device, &allocation, sets);
        if (result != Result.Success)
            throw new VulkanException("Failed to allocate C3 guiding descriptor sets.", result);
        for (int index = 0; index < DescriptorSetCount; index++)
            _descriptorSets[index] = sets[index];
    }

    private void CreatePipelineCache()
    {
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
            throw new VulkanException("Failed to create C3 guiding pipeline cache.", result);
    }

    private void CreatePipelineLayout()
    {
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingPushConstants>()
        };
        DescriptorSetLayout layout = _descriptorSetLayout;
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1u,
            PSetLayouts = &layout,
            PushConstantRangeCount = 1u,
            PPushConstantRanges = &pushRange
        };
        Result result = _context.Api.CreatePipelineLayout(
            _context.Device,
            &info,
            null,
            out _pipelineLayout);
        if (result != Result.Success)
            throw new VulkanException("Failed to create C3 guiding pipeline layout.", result);
        _context.SetDebugName(
            _pipelineLayout.Handle,
            ObjectType.PipelineLayout,
            "Simple DDGI Guiding Pipeline Layout");
    }

    private void CreateExtractPipelineLayout()
    {
        DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3];
        layouts[0] = _bindlessHeap.StorageBufferSetLayout;
        layouts[1] = _bindlessHeap.TextureSamplerSetLayout;
        layouts[2] = _descriptorSetLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0u,
            Size = (uint)Marshal.SizeOf<GPUSimpleDdgiGuidingExtractPushConstants>()
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
            out _extractPipelineLayout);
        if (result != Result.Success)
        {
            throw new VulkanException(
                "Failed to create C3 trace-extractor pipeline layout.",
                result);
        }
        _context.SetDebugName(
            _extractPipelineLayout.Handle,
            ObjectType.PipelineLayout,
            "Simple DDGI Guiding Trace Extract Pipeline Layout");
    }

    private VkPipeline CreatePipeline(
        string shaderName,
        string debugName,
        PipelineLayout pipelineLayout)
    {
        ShaderModule shaderModule = default;
        try
        {
            shaderModule = ShaderModuleLoader.Load(_context, shaderName);
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)_entryPointName
            };
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = pipelineLayout,
                BasePipelineIndex = -1
            };
            Result result = _context.Api.CreateComputePipelines(
                _context.Device,
                _pipelineCache,
                1u,
                &info,
                null,
                out VkPipeline pipeline);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to create C3 guiding pipeline " + shaderName + ".",
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
        DestroyPipeline(_trainPipeline);
        DestroyPipeline(_buildPipeline);
        DestroyPipeline(_samplePipeline);
        DestroyPipeline(_validatePipeline);
        DestroyPipeline(_extractPipeline);
        if (_extractPipelineLayout.Handle != 0)
        {
            _context.Api.DestroyPipelineLayout(
                _context.Device,
                _extractPipelineLayout,
                null);
        }
        if (_pipelineLayout.Handle != 0)
            _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_descriptorPool.Handle != 0)
            _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
        if (_descriptorSetLayout.Handle != 0)
            _context.Api.DestroyDescriptorSetLayout(_context.Device, _descriptorSetLayout, null);
        if (_pipelineCache.Handle != 0)
            _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
        if (_entryPointName != 0)
            SilkMarshal.Free(_entryPointName);
        _trainPipeline = default;
        _buildPipeline = default;
        _samplePipeline = default;
        _validatePipeline = default;
        _extractPipeline = default;
        _pipelineLayout = default;
        _extractPipelineLayout = default;
        _descriptorPool = default;
        _descriptorSetLayout = default;
        _pipelineCache = default;
    }

    private void DestroyPipeline(VkPipeline pipeline)
    {
        if (pipeline.Handle != 0)
            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
    }

    private static string ResolveExtractShader(
        SimpleDdgiStoragePackingMode storagePackingMode) => storagePackingMode switch
    {
        SimpleDdgiStoragePackingMode.Legacy =>
            SimpleDdgiGuidingGpuPassNames.ExtractLegacyShader,
        SimpleDdgiStoragePackingMode.Validate =>
            SimpleDdgiGuidingGpuPassNames.ExtractValidateShader,
        SimpleDdgiStoragePackingMode.Packed =>
            SimpleDdgiGuidingGpuPassNames.ExtractPackedShader,
        _ => throw new ArgumentOutOfRangeException(nameof(storagePackingMode))
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiGuidingGpuPass));
    }

    private readonly record struct StorageRange(
        BufferHandle Buffer,
        ulong OffsetBytes,
        ulong RangeBytes)
    {
        public StorageRange(in SimpleDdgiGuidingExternalBuffer buffer)
            : this(buffer.Buffer, buffer.OffsetBytes, buffer.RangeBytes)
        {
        }

        public static StorageRange ForWholeBuffer(
            BufferHandle buffer,
            BufferManager bufferManager) =>
            new(buffer, 0UL, bufferManager.GetBufferSize(buffer));
    }

    private static StorageRange TrainingScratchRange(
        in SimpleDdgiGuidingVulkanBuffers buffers) =>
        new(
            buffers.TrainingScratch,
            buffers.TrainingScratchOffsetBytes,
            buffers.TrainingScratchRangeBytes);
}
