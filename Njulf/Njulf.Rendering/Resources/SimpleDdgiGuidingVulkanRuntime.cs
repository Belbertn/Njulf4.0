using System;
using System.Collections.Generic;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using Vma;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Capability outcomes for the C3 Vulkan boundary.  C3 is intentionally
/// fail-closed: a learned proposal may not become active until the source
/// cache owns a frozen direction/PDF sidecar and its consumer preserves the
/// exact generation-time, potentially variable PDF.
/// </summary>
public enum SimpleDdgiGuidingGpuCapabilityReason : byte
{
    None = 0,
    SourceCacheSidecarUnavailable = 1,
    SourceCacheSidecarOwnershipInvalid = 2,
    SourceCacheHandshakeInvalid = 3,
    SourceCacheDirectionPdfConsumerUnavailable = 4,
    VariablePdfProjectionUnavailable = 5,
    GlobalPrerequisiteGateRejected = 6,
    ExactModeNotAdmitted = 7,
    BindlessDescriptorContextUnavailable = 8,
    PipelineUnavailable = 9,
    ResourceAllocationFailed = 10,
    WorkloadRejected = 11,
    BuildRecordingRejected = 12,
    HeaderReadbackRejected = 13,
    SampleRecordingRejected = 14,
    Disposed = 15,
    SampleReadbackRejected = 16
}

/// <summary>
/// An exact externally-owned storage range used by C3 train/build/sample
/// programs.  The range carries the producer visibility that the Vulkan
/// recorder needs to construct a real synchronization2 dependency instead of
/// guessing which source-cache pass last wrote it.
/// </summary>
public readonly record struct SimpleDdgiGuidingExternalBuffer(
    BufferHandle Buffer,
    ulong OffsetBytes,
    ulong RangeBytes,
    uint ElementCount,
    uint ElementStrideBytes,
    PipelineStageFlags2 LastWriterStageMask,
    AccessFlags2 LastWriterAccessMask)
{
    public bool TryValidate(
        uint expectedElementStrideBytes,
        uint minimumElementCount,
        string name,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An external C3 buffer name is required.", nameof(name));
        if (!Buffer.IsValid || expectedElementStrideBytes == 0u ||
            ElementStrideBytes != expectedElementStrideBytes ||
            ElementCount < minimumElementCount ||
            OffsetBytes % sizeof(uint) != 0UL || RangeBytes == 0UL ||
            LastWriterStageMask == 0 || LastWriterAccessMask == 0)
        {
            reason = "guiding-" + name + "-contract-invalid";
            return false;
        }

        const AccessFlags2 WriteAccesses =
            AccessFlags2.ShaderStorageWriteBit |
            AccessFlags2.TransferWriteBit |
            AccessFlags2.HostWriteBit |
            AccessFlags2.MemoryWriteBit;
        if ((LastWriterAccessMask & WriteAccesses) == 0)
        {
            reason = "guiding-" + name + "-last-writer-is-not-a-write";
            return false;
        }

        try
        {
            ulong requiredBytes = checked(
                (ulong)ElementCount * expectedElementStrideBytes);
            if (requiredBytes > RangeBytes)
            {
                reason = "guiding-" + name + "-range-smaller-than-element-count";
                return false;
            }
        }
        catch (OverflowException)
        {
            reason = "guiding-" + name + "-range-overflow";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// CPU-side identity for one compact header copied from a candidate bank.  It
/// is supplied by the source-cache/scheduler producer, not inferred from a
/// GPU-written header, so a malformed candidate can never define its own
/// expected ownership.
/// </summary>
public readonly record struct SimpleDdgiGuidingExpectedProbeHeader(
    uint PhysicalProbeIndex,
    uint VirtualProbeId,
    uint PageGeneration);

/// <summary>
/// Authoritative DDGI trace inputs used to materialize C3 training records.
/// The queue and scratch remain owned by the ordinary DDGI transaction; C3
/// receives only exact ranges, fixed bindless indices, and producer visibility.
/// </summary>
public readonly record struct SimpleDdgiGuidingTraceTrainingSource(
    bool IsAvailable,
    bool TraceDispatchCompleted,
    SimpleDdgiStoragePackingMode StoragePackingMode,
    uint ParamsBufferIndex,
    uint RayResultScratchBufferIndex,
    uint ProbeUpdateQueueBufferIndex,
    SimpleDdgiGuidingExternalBuffer Params,
    SimpleDdgiGuidingExternalBuffer RayResultScratch,
    SimpleDdgiGuidingExternalBuffer ProbeUpdateQueue)
{
    /// <summary>
    /// Compact, authenticated C3 trace payloads stored in the reserved tail of
    /// the ordinary DDGI ray scratch allocation. The sample pass publishes one
    /// fixed-size record per scheduled ray for the later DDGI consumers.
    /// </summary>
    public SimpleDdgiGuidingExternalBuffer GuidingTracePayloadScratch { get; init; }

    public uint RayResultStrideBytes => StoragePackingMode ==
            SimpleDdgiStoragePackingMode.Packed
        ? 20u
        : 32u;

    public bool TryValidate(out string reason)
    {
        if (!IsAvailable || !TraceDispatchCompleted)
        {
            reason = "guiding-trace-training-source-not-complete";
            return false;
        }
        if (!Enum.IsDefined(StoragePackingMode))
        {
            reason = "guiding-trace-training-storage-mode-invalid";
            return false;
        }
        if (ParamsBufferIndex != (uint)BindlessIndex.SimpleDdgiParamsBuffer ||
            RayResultScratchBufferIndex !=
                (uint)BindlessIndex.SimpleDdgiRayResultScratchBuffer ||
            ProbeUpdateQueueBufferIndex !=
                (uint)BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer)
        {
            reason = "guiding-trace-training-bindless-index-mismatch";
            return false;
        }
        if (!Params.TryValidate(
                sizeof(uint),
                64u,
                "trace-params",
                out reason) ||
            !RayResultScratch.TryValidate(
                RayResultStrideBytes,
                1u,
                "trace-ray-results",
                out reason) ||
            !ProbeUpdateQueue.TryValidate(
                checked((uint)SimpleDdgiMemoryPlan.ProbeUpdateBytes),
                1u,
                "trace-probe-updates",
                out reason) ||
            !GuidingTracePayloadScratch.TryValidate(
                checked((uint)SimpleDdgiMemoryPlan.GuidingTraceDirectionRecordBytes),
                1u,
                "guiding-trace-payload-scratch",
                out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// Immutable bridge from the GPU-resident DDGI scheduler to C3. The accepted
/// count and public update queue stay device-owned; managed code supplies only
/// frozen offsets, capacities, and the scene revision needed to reproduce the
/// CPU reference identities in the preparation shader.
/// </summary>
public readonly record struct SimpleDdgiGuidingGpuResidentWorkSource(
    bool IsAvailable,
    uint SchedulerArenaBufferIndex,
    uint SchedulerCountersOffsetWords,
    uint SchedulerAcceptedCounterWord,
    uint SchedulerRequestCapacity,
    ulong SceneContentRevision,
    BufferHandle SchedulerArenaBuffer,
    ulong SchedulerArenaBytes)
{
    public bool TryValidate(out string reason)
    {
        if (!IsAvailable ||
            SchedulerArenaBufferIndex !=
                (uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer ||
            SchedulerAcceptedCounterWord != 2u ||
            SchedulerRequestCapacity == 0u ||
            SceneContentRevision == 0UL || !SchedulerArenaBuffer.IsValid ||
            SchedulerArenaBytes == 0UL)
        {
            reason = "guiding-gpu-resident-work-source-invalid";
            return false;
        }

        try
        {
            _ = checked(SchedulerCountersOffsetWords +
                SchedulerAcceptedCounterWord);
        }
        catch (OverflowException)
        {
            reason = "guiding-gpu-resident-counter-offset-overflow";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryValidate(
        in SimpleDdgiGuidingLayout layout,
        out string reason)
    {
        if (!TryValidate(out reason) ||
            SchedulerRequestCapacity <
                checked((uint)layout.ScheduledGuidedProbeCapacity))
        {
            if (string.IsNullOrEmpty(reason))
                reason = "guiding-gpu-resident-work-source-capacity-insufficient";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Frozen ownership handshake for the C3 direction/PDF sidecar.  Slot 203 is
/// source-cache-owned: this runtime validates that ownership and may reference
/// the supplied buffer through its private compute descriptor set, but it
/// never allocates, registers, replaces, or otherwise publishes bindless slot
/// 203 itself.
/// </summary>
public readonly record struct SimpleDdgiGuidingSourceCacheHandshake(
    bool IsAvailable,
    uint GuidingAbiVersion,
    bool SourceCacheOwnsDirectionPdfSidecar,
    uint DirectionPdfSidecarBindlessSlot,
    BufferHandle DirectionPdfSidecar,
    ulong DirectionPdfSidecarOffsetBytes,
    ulong DirectionPdfSidecarBytes,
    uint DirectionPdfSidecarCapacity,
    uint DirectionPdfSidecarStrideBytes,
    PipelineStageFlags2 SourceCachePriorAccessStageMask,
    AccessFlags2 SourceCachePriorAccessMask,
    bool ConsumerAcceptsGenerationTimePdf,
    bool ConsumerSupportsVariablePdfProjection,
    PipelineStageFlags2 ConsumerReadStageMask,
    AccessFlags2 ConsumerReadAccessMask)
{
    public static SimpleDdgiGuidingSourceCacheHandshake Unavailable { get; } =
        new(
            IsAvailable: false,
            GuidingAbiVersion: 0u,
            SourceCacheOwnsDirectionPdfSidecar: false,
            DirectionPdfSidecarBindlessSlot: 0u,
            DirectionPdfSidecar: BufferHandle.Invalid,
            DirectionPdfSidecarOffsetBytes: 0UL,
            DirectionPdfSidecarBytes: 0UL,
            DirectionPdfSidecarCapacity: 0u,
            DirectionPdfSidecarStrideBytes: 0u,
            SourceCachePriorAccessStageMask: 0,
            SourceCachePriorAccessMask: 0,
            ConsumerAcceptsGenerationTimePdf: false,
            ConsumerSupportsVariablePdfProjection: false,
            ConsumerReadStageMask: 0,
            ConsumerReadAccessMask: 0);

    public bool TryValidate(
        out SimpleDdgiGuidingGpuCapabilityReason capabilityReason,
        out string reason)
    {
        if (!IsAvailable)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarUnavailable;
            reason = "guiding-source-cache-direction-pdf-sidecar-unavailable";
            return false;
        }
        if (GuidingAbiVersion != SimpleDdgiGuidingGpuAbi.Version)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid;
            reason = "guiding-source-cache-abi-version-mismatch";
            return false;
        }
        if (!SourceCacheOwnsDirectionPdfSidecar ||
            DirectionPdfSidecarBindlessSlot !=
                (uint)SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarOwnershipInvalid;
            reason = "guiding-source-cache-does-not-own-fixed-sidecar-slot-203";
            return false;
        }
        if (!DirectionPdfSidecar.IsValid || DirectionPdfSidecarBytes == 0UL ||
            DirectionPdfSidecarCapacity == 0u ||
            DirectionPdfSidecarStrideBytes !=
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount ||
            DirectionPdfSidecarOffsetBytes % sizeof(uint) != 0UL)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid;
            reason = "guiding-source-cache-sidecar-buffer-or-stride-invalid";
            return false;
        }
        // The owner must describe the exact last access to the supplied range
        // for this dispatch.  C3 does not infer it from the sidecar's fixed
        // global slot and cannot safely assume that the payload consumer was
        // the last source-cache operation.
        if (SourceCachePriorAccessStageMask == 0 ||
            SourceCachePriorAccessMask == 0)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid;
            reason = "guiding-source-cache-sidecar-prior-access-unavailable";
            return false;
        }
        try
        {
            ulong requiredBytes = checked(
                (ulong)DirectionPdfSidecarCapacity *
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
            if (requiredBytes > DirectionPdfSidecarBytes)
            {
                capabilityReason =
                    SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid;
                reason = "guiding-source-cache-sidecar-range-smaller-than-payload-capacity";
                return false;
            }
        }
        catch (OverflowException)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid;
            reason = "guiding-source-cache-sidecar-range-overflow";
            return false;
        }
        if (!ConsumerAcceptsGenerationTimePdf)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheDirectionPdfConsumerUnavailable;
            reason = "guiding-source-cache-consumer-does-not-preserve-generation-time-pdf";
            return false;
        }
        if (!ConsumerSupportsVariablePdfProjection)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.VariablePdfProjectionUnavailable;
            reason = "guiding-source-cache-consumer-does-not-support-variable-pdf-projection";
            return false;
        }
        if (ConsumerReadStageMask == 0 || ConsumerReadAccessMask == 0 ||
            (ConsumerReadAccessMask & AccessFlags2.ShaderStorageReadBit) == 0)
        {
            capabilityReason =
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheDirectionPdfConsumerUnavailable;
            reason = "guiding-source-cache-consumer-read-visibility-invalid";
            return false;
        }

        capabilityReason = SimpleDdgiGuidingGpuCapabilityReason.None;
        reason = string.Empty;
        return true;
    }
}

/// <summary>
/// One exact source-cache workload used to create a candidate C3 bank.  The
/// work-item data stays producer-owned; only its frozen byte stride, range,
/// last-writer visibility, and CPU-owned header identities cross this boundary.
/// </summary>
public readonly record struct SimpleDdgiGuidingBuildWorkload(
    uint TargetProposalEpoch,
    SimpleDdgiGuidingExternalBuffer TrainingRecords,
    SimpleDdgiGuidingExternalBuffer TrainingWorkItems,
    SimpleDdgiGuidingExternalBuffer BuildWorkItems,
    SimpleDdgiGuidingExternalBuffer ValidationCounters,
    ReadOnlyMemory<SimpleDdgiGuidingExpectedProbeHeader> ExpectedHeaders)
{
    /// <summary>
    /// Mandatory production source. Caller-authored training records are not
    /// accepted: the extractor derives them from the completed trace scratch
    /// and verifies the immutable queue provenance in each work item.
    /// </summary>
    public SimpleDdgiGuidingTraceTrainingSource TraceTrainingSource { get; init; }

    /// <summary>
    /// Present only when work items are compacted on device from the resident
    /// scheduler queue. An unavailable/default value selects the audited CPU
    /// reference compiler.
    /// </summary>
    public SimpleDdgiGuidingGpuResidentWorkSource GpuResidentSource { get; init; }

    public bool UsesGpuResidentWork => GpuResidentSource.IsAvailable;

    public bool TryValidate(
        in SimpleDdgiGuidingLayout layout,
        out string reason)
    {
        uint scheduledCapacity;
        uint physicalCapacity;
        try
        {
            scheduledCapacity = checked((uint)layout.ScheduledGuidedProbeCapacity);
            physicalCapacity = checked((uint)layout.PhysicalProbeCapacity);
        }
        catch (OverflowException)
        {
            reason = "guiding-workload-layout-capacity-overflow";
            return false;
        }
        bool gpuGenerated = UsesGpuResidentWork;
        uint requiredTrainingRecordCapacity;
        try
        {
            requiredTrainingRecordCapacity = checked(scheduledCapacity *
                (uint)layout.DirectionSlotsPerProbe);
        }
        catch (OverflowException)
        {
            reason = "guiding-build-workload-training-capacity-overflow";
            return false;
        }
        if (TargetProposalEpoch == 0u || scheduledCapacity == 0u ||
            (gpuGenerated
                ? (!ExpectedHeaders.IsEmpty ||
                   BuildWorkItems.ElementCount != scheduledCapacity ||
                   TrainingWorkItems.ElementCount != scheduledCapacity ||
                   TrainingRecords.ElementCount < requiredTrainingRecordCapacity)
                : (ExpectedHeaders.IsEmpty ||
                   ExpectedHeaders.Length > scheduledCapacity ||
                   BuildWorkItems.ElementCount != (uint)ExpectedHeaders.Length ||
                   TrainingWorkItems.ElementCount == 0u ||
                   TrainingWorkItems.ElementCount > scheduledCapacity)))
        {
            reason = "guiding-build-workload-count-invalid";
            return false;
        }
        if ((gpuGenerated && !GpuResidentSource.TryValidate(layout, out reason)) ||
            !TraceTrainingSource.TryValidate(out reason) ||
            !TrainingRecords.TryValidate(
                SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount,
                0u,
                "training-records",
                out reason) ||
            !TrainingWorkItems.TryValidate(
                SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount,
                0u,
                "training-work-items",
                out reason) ||
            !BuildWorkItems.TryValidate(
                SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount,
                1u,
                "build-work-items",
                out reason) ||
            !ValidationCounters.TryValidate(
                sizeof(uint),
                SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
                "validation-counters",
                out reason))
        {
            return false;
        }

        uint previousPhysicalProbe = 0u;
        bool hasPrevious = false;
        foreach (SimpleDdgiGuidingExpectedProbeHeader expected in ExpectedHeaders.Span)
        {
            if (expected.PhysicalProbeIndex >= physicalCapacity ||
                (hasPrevious && expected.PhysicalProbeIndex <= previousPhysicalProbe))
            {
                reason = "guiding-build-expected-probe-identities-not-strictly-ascending";
                return false;
            }
            previousPhysicalProbe = expected.PhysicalProbeIndex;
            hasPrevious = true;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>Exact source-cache request/counter ranges for a published-bank
/// sampling dispatch.</summary>
public readonly record struct SimpleDdgiGuidingSampleWorkload(
    SimpleDdgiGuidingExternalBuffer SampleRequests,
    SimpleDdgiGuidingExternalBuffer ValidationCounters)
{
    /// <summary>
    /// Producer witness that (physical probe, slot) destinations are unique
    /// for this dispatch. Duplicate requests would create an unordered SSBO
    /// race and are therefore rejected before command recording.
    /// </summary>
    public bool DestinationsAreUnique { get; init; }

    /// <summary>
    /// CPU-owned identities for the payload-owner transaction represented by
    /// this dispatch.  They are copied into the frame-fence readback record;
    /// GPU output can never define the ownership that will be committed.
    /// </summary>
    public ReadOnlyMemory<SimpleDdgiGuidingSampleCommit> ExpectedCommits { get; init; }

    /// <summary>Device-owned scheduler bridge for GPU-compacted requests.</summary>
    public SimpleDdgiGuidingGpuResidentWorkSource GpuResidentSource { get; init; }

    public float UniformMixtureFraction { get; init; } =
        SimpleDdgiGuidingProposalPolicy.ProductionBaseline.UniformMixtureFraction;

    public SimpleDdgiGuidingTraceTrainingSource TraceTrainingSource { get; init; }

    public SimpleDdgiGuidingExternalBuffer TrainingWorkItems { get; init; }

    public SimpleDdgiGuidingExternalBuffer BuildWorkItems { get; init; }

    public bool UsesGpuResidentWork => GpuResidentSource.IsAvailable;

    public bool TryValidate(
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        out string reason)
    {
        bool gpuGenerated = UsesGpuResidentWork;
        if (!DestinationsAreUnique ||
            (gpuGenerated ? !ExpectedCommits.IsEmpty : ExpectedCommits.IsEmpty) ||
            !float.IsFinite(UniformMixtureFraction) ||
            UniformMixtureFraction <
                SimpleDdgiDirectionalGuidingExperiment.MinimumUniformFraction ||
            UniformMixtureFraction > 1.0f ||
            SampleRequests.ElementCount == 0u ||
            SampleRequests.ElementCount > handshake.DirectionPdfSidecarCapacity ||
            ExpectedCommits.Length > SampleRequests.ElementCount ||
            ValidationCounters.ElementCount !=
                SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount)
        {
            reason = "guiding-sample-workload-count-invalid";
            return false;
        }
        if (gpuGenerated && !GpuResidentSource.TryValidate(out reason))
        {
            return false;
        }
        if (!TraceTrainingSource.TryValidate(out reason))
        {
            return false;
        }
        if (gpuGenerated &&
            (!TrainingWorkItems.TryValidate(
                 SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount,
                 1u,
                 "sample-prepare-training-work-items",
                 out reason) ||
             !BuildWorkItems.TryValidate(
                 SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount,
                 1u,
                 "sample-prepare-build-work-items",
                 out reason)))
        {
            return false;
        }
        if (!SampleRequests.TryValidate(
                SimpleDdgiGuidingGpuAbi.SampleRequestByteCount,
                1u,
                "sample-requests",
                out reason) ||
            !ValidationCounters.TryValidate(
                sizeof(uint),
                SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
                "validation-counters",
                out reason))
        {
            return false;
        }


        uint previousPhysicalProbe = 0u;
        bool hasPrevious = false;
        foreach (SimpleDdgiGuidingSampleCommit commit in ExpectedCommits.Span)
        {
            if (commit.StableProbeId == 0UL || commit.PageGeneration == 0u ||
                commit.PageGeneration > SimpleDdgiSchedulerAbi.PhysicalGenerationMask ||
                commit.ProposalEpoch == 0u ||
                (hasPrevious && commit.PhysicalProbeIndex <= previousPhysicalProbe))
            {
                reason = "guiding-sample-commit-identities-invalid-or-unsorted";
                return false;
            }
            previousPhysicalProbe = commit.PhysicalProbeIndex;
            hasPrevious = true;
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>The four frozen C3 GPU validation counters.</summary>
public readonly record struct SimpleDdgiGuidingValidationCounters(
    uint InvalidRecords,
    uint InvalidHeaders,
    uint InvalidPdfs,
    uint PublicationRejections)
{
    public bool AreZero => InvalidRecords == 0u && InvalidHeaders == 0u &&
        InvalidPdfs == 0u && PublicationRejections == 0u;

    public ulong Total => (ulong)InvalidRecords + InvalidHeaders + InvalidPdfs +
        PublicationRejections;
}

/// <summary>
/// Fixed 16-bin log2(1/PDF) histogram. Bin zero contains values below 2^-7;
/// bin 15 contains values at or above 2^7. Intermediate bins use unit-width
/// log2 intervals and therefore remain comparable across resolutions.
/// </summary>
public readonly record struct SimpleDdgiGuidingInversePdfHistogram(
    uint Bin0,
    uint Bin1,
    uint Bin2,
    uint Bin3,
    uint Bin4,
    uint Bin5,
    uint Bin6,
    uint Bin7,
    uint Bin8,
    uint Bin9,
    uint Bin10,
    uint Bin11,
    uint Bin12,
    uint Bin13,
    uint Bin14,
    uint Bin15)
{
    public ulong Total => checked(
        (ulong)Bin0 + Bin1 + Bin2 + Bin3 + Bin4 + Bin5 + Bin6 +
        Bin7 + Bin8 + Bin9 + Bin10 + Bin11 + Bin12 + Bin13 +
        Bin14 + Bin15);

    public uint GetBin(int index) => index switch
    {
        0 => Bin0,
        1 => Bin1,
        2 => Bin2,
        3 => Bin3,
        4 => Bin4,
        5 => Bin5,
        6 => Bin6,
        7 => Bin7,
        8 => Bin8,
        9 => Bin9,
        10 => Bin10,
        11 => Bin11,
        12 => Bin12,
        13 => Bin13,
        14 => Bin14,
        15 => Bin15,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>Conservative upper edge of the bucket containing a percentile.</summary>
    public float PercentileUpperBound(float percentile)
    {
        if (!float.IsFinite(percentile) || percentile <= 0.0f ||
            percentile > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }
        ulong total = Total;
        if (total == 0UL)
            return 0.0f;

        ulong target = Math.Max(
            1UL,
            checked((ulong)Math.Ceiling(total * (double)percentile)));
        ulong cumulative = 0UL;
        for (int bin = 0; bin < 16; bin++)
        {
            cumulative = checked(cumulative + GetBin(bin));
            if (cumulative >= target)
                return MathF.Pow(2.0f, bin - 7);
        }
        return MathF.Pow(2.0f, 8.0f);
    }
}

/// <summary>
/// Fence-complete sampling statistics parsed from the frozen 32-word GPU
/// counter ABI. The exact extrema complement conservative histogram quantiles.
/// </summary>
public readonly record struct SimpleDdgiGuidingSampleTelemetry(
    uint RequestCount,
    uint ValidSampleCount,
    uint BootstrapInvalidationCount,
    uint MaintenanceSampleCount,
    uint MixtureUniformSampleCount,
    uint MixtureGuidedSampleCount,
    uint UniformFallbackSampleCount,
    float MinimumPdf,
    float MaximumPdf,
    float MinimumInversePdf,
    float P50InversePdfUpperBound,
    float P95InversePdfUpperBound,
    float P99InversePdfUpperBound,
    float MaximumInversePdf,
    SimpleDdgiGuidingInversePdfHistogram InversePdfHistogram)
{
    public static SimpleDdgiGuidingSampleTelemetry Empty { get; } = default;

    public bool IsConsistent(
        in SimpleDdgiGuidingValidationCounters validation)
    {
        if (RequestCount == 0U)
            return Equals(Empty);

        ulong invalid = (ulong)validation.InvalidRecords +
            validation.InvalidHeaders + validation.InvalidPdfs;
        ulong branchTotal = (ulong)MaintenanceSampleCount +
            MixtureUniformSampleCount + MixtureGuidedSampleCount;
        bool countsValid = (ulong)ValidSampleCount + invalid +
                BootstrapInvalidationCount == RequestCount &&
            branchTotal == ValidSampleCount &&
            UniformFallbackSampleCount <= ValidSampleCount &&
            InversePdfHistogram.Total == ValidSampleCount;
        if (!countsValid)
            return false;
        if (ValidSampleCount == 0U)
        {
            return MinimumPdf == 0.0f && MaximumPdf == 0.0f &&
                MinimumInversePdf == 0.0f && MaximumInversePdf == 0.0f &&
                P50InversePdfUpperBound == 0.0f &&
                P95InversePdfUpperBound == 0.0f &&
                P99InversePdfUpperBound == 0.0f;
        }

        return
            float.IsFinite(MinimumPdf) && MinimumPdf > 0.0f &&
            float.IsFinite(MaximumPdf) && MaximumPdf >= MinimumPdf &&
            float.IsFinite(MinimumInversePdf) &&
            MinimumInversePdf > 0.0f &&
            float.IsFinite(MaximumInversePdf) &&
            MaximumInversePdf >= MinimumInversePdf &&
            float.IsFinite(P50InversePdfUpperBound) &&
            float.IsFinite(P95InversePdfUpperBound) &&
            float.IsFinite(P99InversePdfUpperBound) &&
            P50InversePdfUpperBound > 0.0f &&
            P50InversePdfUpperBound <= P95InversePdfUpperBound &&
            P95InversePdfUpperBound <= P99InversePdfUpperBound;
    }

    public static bool TryCreate(
        ReadOnlySpan<uint> words,
        uint requestCount,
        in SimpleDdgiGuidingValidationCounters validation,
        out SimpleDdgiGuidingSampleTelemetry telemetry,
        out string reason,
        bool gpuGenerated = false)
    {
        telemetry = default;
        if (words.Length < SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount ||
            (requestCount == 0U && !gpuGenerated))
        {
            reason = "guiding-sample-telemetry-range-invalid";
            return false;
        }

        uint valid = words[(int)SimpleDdgiGuidingGpuAbi.CounterValidSamples];
        uint maintenance =
            words[(int)SimpleDdgiGuidingGpuAbi.CounterMaintenanceSamples];
        uint mixtureUniform =
            words[(int)SimpleDdgiGuidingGpuAbi.CounterMixtureUniformSamples];
        uint mixtureGuided =
            words[(int)SimpleDdgiGuidingGpuAbi.CounterMixtureGuidedSamples];
        uint uniformFallback =
            words[(int)SimpleDdgiGuidingGpuAbi.CounterUniformFallbackSamples];
        uint bootstrapInvalidations = words[(int)SimpleDdgiGuidingGpuAbi
            .CounterBootstrapInvalidations];
        var histogram = new SimpleDdgiGuidingInversePdfHistogram(
            words[12], words[13], words[14], words[15],
            words[16], words[17], words[18], words[19],
            words[20], words[21], words[22], words[23],
            words[24], words[25], words[26], words[27]);

        ulong invalid = (ulong)validation.InvalidRecords +
            validation.InvalidHeaders + validation.InvalidPdfs;
        ulong branchTotal = (ulong)maintenance + mixtureUniform + mixtureGuided;
        bool reservedValid = gpuGenerated
            ? words[28] == 0U && words[29] == 0U &&
                words[30] == requestCount &&
                words[31] == SimpleDdgiGuidingGpuAbi.Version
            : words[28] == 0U && words[29] == 0U &&
                words[30] == 0U && words[31] == 0U;
        if ((ulong)valid + invalid + bootstrapInvalidations != requestCount ||
            branchTotal != valid ||
            histogram.Total != valid || uniformFallback > valid ||
            validation.PublicationRejections != 0U || !reservedValid)
        {
            reason = "guiding-sample-telemetry-count-mismatch";
            return false;
        }

        float maximumInversePdf = BitConverter.UInt32BitsToSingle(
            words[(int)SimpleDdgiGuidingGpuAbi.CounterMaximumInversePdfBits]);
        float maximumPdf = BitConverter.UInt32BitsToSingle(
            words[(int)SimpleDdgiGuidingGpuAbi.CounterMaximumPdfBits]);
        if (valid == 0U)
        {
            if (maximumInversePdf != 0.0f || maximumPdf != 0.0f)
            {
                reason = "guiding-sample-telemetry-empty-extrema-nonzero";
                return false;
            }
            telemetry = new SimpleDdgiGuidingSampleTelemetry(
                requestCount, 0U, bootstrapInvalidations, 0U, 0U, 0U, 0U,
                0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
                histogram);
            reason = "valid";
            return true;
        }
        if (!float.IsFinite(maximumInversePdf) || maximumInversePdf <= 0.0f ||
            !float.IsFinite(maximumPdf) || maximumPdf <= 0.0f)
        {
            reason = "guiding-sample-telemetry-extrema-invalid";
            return false;
        }

        telemetry = new SimpleDdgiGuidingSampleTelemetry(
            requestCount,
            valid,
            bootstrapInvalidations,
            maintenance,
            mixtureUniform,
            mixtureGuided,
            uniformFallback,
            1.0f / maximumInversePdf,
            maximumPdf,
            1.0f / maximumPdf,
            histogram.PercentileUpperBound(0.50f),
            histogram.PercentileUpperBound(0.95f),
            histogram.PercentileUpperBound(0.99f),
            maximumInversePdf,
            histogram);
        if (!telemetry.IsConsistent(validation))
        {
            telemetry = default;
            reason = "guiding-sample-telemetry-derived-statistic-invalid";
            return false;
        }
        reason = "valid";
        return true;
    }
}

/// <summary>
/// Fence-complete sample result.  <see cref="Commits"/> remains CPU-owned and
/// may be passed to <see cref="SimpleDdgiGuidingWorkloadPlanner.TryCommitSamples"/>
/// only when <see cref="Succeeded"/> is true.
/// </summary>
public readonly record struct SimpleDdgiGuidingSampleCompletion(
    bool FenceCompleted,
    bool ReadbackValid,
    SimpleDdgiGuidingValidationCounters ValidationCounters,
    ReadOnlyMemory<SimpleDdgiGuidingSampleCommit> Commits,
    string Reason)
{
    public SimpleDdgiGuidingSampleTelemetry Telemetry { get; init; } =
        SimpleDdgiGuidingSampleTelemetry.Empty;

    public bool Succeeded => FenceCompleted && ReadbackValid &&
        ValidationCounters.AreZero;

    public static SimpleDdgiGuidingSampleCompletion None { get; } = new(
        false,
        false,
        default,
        ReadOnlyMemory<SimpleDdgiGuidingSampleCommit>.Empty,
        "guiding-no-fence-complete-sample-readback");
}

/// <summary>Inspectable C3 Vulkan state.  A failed capability leaves no
/// C3-owned device buffers or C3 compute pipelines resident.</summary>
public readonly record struct SimpleDdgiGuidingGpuRuntimeDiagnostics(
    SimpleDdgiGuidingGpuCapabilityReason CapabilityReason,
    bool SourceCacheHandshakeAvailable,
    bool DescriptorContextRegistered,
    bool HeaderReadbackPending,
    SimpleDdgiGuidingRuntimeSnapshot Resource,
    string Detail)
{
    /// <summary>
    /// Result of the most recently consumed fence-complete transaction.  This
    /// remains available while a newer frame is pending so asynchronous
    /// failures cannot be hidden by the next submission.
    /// </summary>
    public string LastCompletionDetail { get; init; } = "none";

    public static SimpleDdgiGuidingGpuRuntimeDiagnostics Disabled { get; } =
        new(
            SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarUnavailable,
            false,
            false,
            false,
            new SimpleDdgiGuidingRuntimeSnapshot(
                SimpleDdgiGuidingResourceState.Disabled,
                false,
                0UL,
                0UL,
                0u,
                -1,
                0,
                0u,
                0u,
                0,
                "guiding-source-cache-direction-pdf-sidecar-unavailable"),
            "guiding-source-cache-direction-pdf-sidecar-unavailable");
}

/// <summary>
/// Exact persistent C3 distribution allocations exposed to render-graph
/// ownership planning. The transient workspace and source-cache sidecar have
/// separate owners and are deliberately not represented here.
/// </summary>
internal readonly record struct SimpleDdgiGuidingDistributionResourceSnapshot(
    BufferHandle DistributionBank0,
    BufferHandle DistributionBank1,
    ulong BankBytes,
    ulong AllocationGeneration)
{
    public bool IsComplete => DistributionBank0.IsValid &&
        DistributionBank1.IsValid && BankBytes > 0UL &&
        AllocationGeneration > 0UL;
}

/// <summary>
/// Vulkan allocation, private-descriptor, compute-recording, and header
/// publication boundary for C3.  It deliberately does not create a competing
/// owner for bindless slot 203; an exact source-cache handshake is mandatory
/// before any C3 buffer or pipeline can be created.
/// </summary>
public sealed unsafe class SimpleDdgiGuidingVulkanRuntime : IDisposable
{
    private const ulong HeaderBytes = SimpleDdgiGuidingGpuAbi.HeaderByteCount;

    private readonly object _sync = new();
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly Action? _waitForDescriptorReaders;
    private readonly SimpleDdgiGuidingManager _manager = new();
    private readonly VulkanAllocator _allocator;
    private readonly PendingReadback?[] _pendingReadbacks =
        new PendingReadback?[RenderingConstants.FramesInFlight];
    private readonly PendingSampleReadback?[] _pendingSampleReadbacks =
        new PendingSampleReadback?[RenderingConstants.FramesInFlight];
    private string _lastCompletionDetail = "none";
    private readonly PendingStagedBuild?[] _stagedBuilds =
        new PendingStagedBuild?[RenderingConstants.FramesInFlight];
    // One descriptor instance exists per (fence-reclaimed frame slot, pass
    // kind).  A claimed instance must not be updated again until the caller
    // reports that frame slot fence-complete through TryReadCompletedFrame.
    private readonly bool[,] _privateDescriptorSetsClaimed =
        new bool[RenderingConstants.FramesInFlight, 7];

    private SimpleDdgiGuidingGpuPass? _pass;
    private SimpleDdgiGuidingSourceCacheHandshake? _configuredHandshake;
    private SimpleDdgiStoragePackingMode? _configuredStoragePackingMode;
    private SimpleDdgiStoragePackingMode? _pipelineStoragePackingMode;
    private SimpleDdgiGuidingBuildToken? _reservedBuild;
    private GiPipelineCacheService? _pipelineCacheService;
    private bool _disposed;

    public SimpleDdgiGuidingVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        Action? waitForDescriptorReaders = null)
        : this(context, bufferManager, waitForDescriptorReaders, null)
    {
    }

    internal SimpleDdgiGuidingVulkanRuntime(
        VulkanContext context,
        BufferManager bufferManager,
        Action? waitForDescriptorReaders,
        AdvancedGiTransientBufferArena? transientBufferArena)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _waitForDescriptorReaders = waitForDescriptorReaders;
        _allocator = new VulkanAllocator(bufferManager, transientBufferArena);
        Diagnostics = SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;
    }

    internal void SetPipelineCacheService(
        GiPipelineCacheService pipelineCacheService)
    {
        ArgumentNullException.ThrowIfNull(pipelineCacheService);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pass != null)
            {
                throw new InvalidOperationException(
                    "Guiding pipelines were already created.");
            }
            _pipelineCacheService = pipelineCacheService;
        }
    }

    internal void PreparePipelines(
        SimpleDdgiStoragePackingMode storagePackingMode)
    {
        storagePackingMode = storagePackingMode.Sanitize();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_allocator.HasDescriptorContext)
            {
                throw new InvalidOperationException(
                    "C3 bindless descriptor context is unavailable during pipeline preparation.");
            }

            EnsurePipelinesNoLock(storagePackingMode);
        }
    }

    private void EnsurePipelinesNoLock(
        SimpleDdgiStoragePackingMode storagePackingMode)
    {
        if (_pass is not null &&
            _pipelineStoragePackingMode != storagePackingMode)
        {
            _pass.Dispose();
            _pass = null;
            _pipelineStoragePackingMode = null;
        }

        if (_pass is null)
        {
            _pass = new SimpleDdgiGuidingGpuPass(
                _context,
                _bufferManager,
                _allocator.DescriptorHeap,
                storagePackingMode,
                _pipelineCacheService);
            _pipelineStoragePackingMode = storagePackingMode;
        }
    }

    public SimpleDdgiGuidingGpuRuntimeDiagnostics Diagnostics { get; private set; }

    /// <summary>
    /// Captures the immutable persistent bank ranges for the current
    /// allocation epoch. Callers use this only to build a concrete graph
    /// resource plan; publication/read-bank selection still remains guarded
    /// by validated GPU headers.
    /// </summary>
    internal bool TryGetDistributionResourceSnapshot(
        out SimpleDdgiGuidingDistributionResourceSnapshot snapshot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out SimpleDdgiGuidingLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out SimpleDdgiGuidingNativeAllocation nativeAllocation) ||
                layout.PersistentDoubleBufferedBytes == 0UL ||
                layout.PersistentDoubleBufferedBytes % 2UL != 0UL)
            {
                snapshot = default;
                return false;
            }

            ulong bankBytes = layout.PersistentDoubleBufferedBytes / 2UL;
            snapshot = new SimpleDdgiGuidingDistributionResourceSnapshot(
                nativeAllocation.Buffers.DistributionBank0,
                nativeAllocation.Buffers.DistributionBank1,
                bankBytes,
                allocation.AllocationId);
            return snapshot.IsComplete;
        }
    }

    public bool TryGetReadableProbeIdentity(
        uint physicalProbeIndex,
        uint virtualProbeId,
        uint pageGeneration,
        out SimpleDdgiGuidingReadableProbeIdentity identity)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _manager.TryGetReadableProbeIdentity(
                physicalProbeIndex,
                virtualProbeId,
                pageGeneration,
                out identity);
        }
    }

    /// <summary>
    /// Reserves the candidate generation before CPU work-item upload.  The
    /// build shader requires that exact generation in every work item; asking
    /// callers to predict the manager's private counter would create an ABA
    /// race after rejection or reconfiguration.
    /// </summary>
    public bool TryReserveBuild(
        uint targetProposalEpoch,
        out SimpleDdgiGuidingBuildToken token,
        out SimpleDdgiGuidingLayout layout,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            token = default;
            layout = default;
            if (!_configuredHandshake.HasValue || _pass is null ||
                !_manager.TryGetActiveAllocation(out _, out layout))
            {
                reason = "guiding-runtime-not-configured-for-build-reservation";
                return false;
            }
            if (_reservedBuild.HasValue)
            {
                reason = "guiding-build-reservation-already-pending";
                return false;
            }

            SimpleDdgiGuidingBuildBeginResult begin =
                _manager.BeginBuild(targetProposalEpoch);
            if (!begin.Started)
            {
                reason = begin.Reason;
                return false;
            }
            _reservedBuild = begin.Token;
            token = begin.Token;
            reason = "guiding-build-generation-reserved";
            return true;
        }
    }

    public bool AbortReservedBuild(
        in SimpleDdgiGuidingBuildToken token,
        string reason = "guiding-reserved-build-aborted")
    {
        lock (_sync)
        {
            if (_disposed || !_reservedBuild.HasValue ||
                !_reservedBuild.Value.Equals(token))
            {
                return false;
            }
            bool aborted = _manager.AbortBuild(token, reason);
            _reservedBuild = null;
            return aborted;
        }
    }

    /// <summary>
    /// Binds a safe existing storage buffer to slots 200-202 while C3 is
    /// inactive.  Slot 203 is intentionally absent from this method.
    /// </summary>
    public bool TryRegisterDescriptors(
        BindlessHeap bindlessHeap,
        BufferHandle safeFallbackBuffer,
        ulong safeFallbackBufferBytes,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(bindlessHeap);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_allocator.TrySetDescriptorContext(
                    bindlessHeap,
                    safeFallbackBuffer,
                    safeFallbackBufferBytes,
                    out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: false);
                return false;
            }

            if (_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out _))
            {
                SynchronizeDescriptorReadersNoLock();
                if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
                {
                    DisableAtSafeTransitionNoLock(
                        SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                        reason,
                        sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
                    return false;
                }
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.None,
                    "guiding-registered-active-descriptors",
                    sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
                reason = "guiding-registered-active-descriptors";
                return true;
            }

            if (!_allocator.TryBindFallback(out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: false);
                return false;
            }
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarUnavailable,
                "guiding-source-cache-direction-pdf-sidecar-unavailable",
                sourceCacheHandshakeAvailable: false);
            reason = "guiding-registered-safe-descriptor-fallbacks";
            return true;
        }
    }

    /// <summary>
    /// Applies an already-admitted C3 request only after a complete source
    /// cache ownership/consumer handshake has been verified.  A false result
    /// is a hard zero-resource C3 transition.
    /// </summary>
    public bool TryConfigure(
        in SimpleDdgiGuidingRuntimeRequest request,
        bool globalPrerequisiteGateAdmitted,
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        out string reason)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            SynchronizeDescriptorReadersNoLock();
            // A reconfiguration is a safe transaction boundary.  Do not
            // merely forget a submitted candidate: that would strand the
            // manager in Building and could make a stale header publish after
            // its source-cache handshake has changed.
            AbortPendingReadbacksNoLock("guiding-configuration-changed");
            ClearDescriptorClaimsNoLock();

            if (!globalPrerequisiteGateAdmitted)
            {
                reason = "guiding-global-prerequisite-gate-rejected";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.GlobalPrerequisiteGateRejected,
                    reason,
                    handshake.IsAvailable);
                return false;
            }
            if (!request.IsEffectivelyEnabled)
            {
                reason = "guiding-exact-mode-not-admitted";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.ExactModeNotAdmitted,
                    reason,
                    handshake.IsAvailable);
                return false;
            }
            if (!Enum.IsDefined(request.SourceStoragePackingMode))
            {
                reason = "guiding-source-storage-packing-mode-invalid";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.WorkloadRejected,
                    reason,
                    handshake.IsAvailable);
                return false;
            }
            if (!handshake.TryValidate(out SimpleDdgiGuidingGpuCapabilityReason handshakeCapability,
                    out string handshakeReason))
            {
                reason = handshakeReason;
                DisableAtSafeTransitionNoLock(
                    handshakeCapability,
                    reason,
                    sourceCacheHandshakeAvailable: handshake.IsAvailable);
                return false;
            }
            SimpleDdgiGuidingLayout requestedLayout = request.Layout;
            if (!requestedLayout.HasTransportSidecar ||
                requestedLayout.DirectionPayloadCapacity !=
                    handshake.DirectionPdfSidecarCapacity ||
                requestedLayout.DirectionPdfSidecarBytes !=
                    handshake.DirectionPdfSidecarBytes ||
                requestedLayout.DirectionSlotsPerProbe <= 0)
            {
                reason = "guiding-source-cache-sidecar-layout-does-not-match-admitted-c3-layout";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!TryValidatePhysicalRange(
                    handshake.DirectionPdfSidecar,
                    handshake.DirectionPdfSidecarOffsetBytes,
                    handshake.DirectionPdfSidecarBytes,
                    "source-cache-direction-pdf-sidecar",
                    out reason))
            {
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!_allocator.HasDescriptorContext)
            {
                reason = "guiding-bindless-descriptor-context-unavailable";
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            try
            {
                EnsurePipelinesNoLock(request.SourceStoragePackingMode);
            }
            catch (Exception exception)
            {
                // Preserve the failing shader name carried by VulkanException.
                // A type-only reason made a driver compilation rejection
                // impossible to diagnose and encouraged repeated blind retries.
                string pipelineFailure = exception.Message
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();
                reason = "guiding-pipeline-unavailable:" +
                    exception.GetType().Name + ":" + pipelineFailure;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.PipelineUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            if (!_allocator.TryBindFallback(out string fallbackReason))
            {
                reason = fallbackReason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            SimpleDdgiGuidingRuntimeSnapshot snapshot;
            try
            {
                snapshot = _manager.Reconcile(request, _allocator);
            }
            catch (Exception exception)
            {
                reason = "guiding-resource-configuration-failed:" + exception.GetType().Name;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.ResourceAllocationFailed,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!snapshot.HasResources ||
                !_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out _))
            {
                reason = snapshot.Reason;
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.ResourceAllocationFailed,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!_allocator.TryBindAllocation(allocation.AllocationId, out reason))
            {
                DisableAtSafeTransitionNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BindlessDescriptorContextUnavailable,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            _configuredHandshake = handshake;
            _configuredStoragePackingMode = request.SourceStoragePackingMode;
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                "guiding-allocated-awaiting-build",
                sourceCacheHandshakeAvailable: true);
            reason = "guiding-allocated-awaiting-build";
            return true;
        }
    }

    /// <summary>
    /// Freezes one already-uploaded build transaction for execution by the
    /// three distinct render-graph nodes. No command is recorded here; the
    /// immutable buffer ranges and expected headers are retained until the
    /// validate node emits the compact readback.
    /// </summary>
    public bool TryPrepareBuildFrame(
        int frameIndex,
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        in SimpleDdgiGuidingBuildWorkload workload,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_stagedBuilds[frameIndex].HasValue ||
                _pendingReadbacks[frameIndex].HasValue)
            {
                reason = "guiding-frame-slot-build-transaction-still-pending";
                return false;
            }
            if (!TryValidateConfiguredHandshakeNoLock(handshake, out reason))
                return false;
            if (!_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out SimpleDdgiGuidingLayout layout) ||
                !_allocator.TryGetNativeAllocation(allocation.AllocationId, out _) ||
                _pass is null)
            {
                reason = "guiding-runtime-not-configured-for-staged-build";
                return false;
            }
            if (!workload.TryValidate(layout, out reason) ||
                !TryValidateBuildWorkloadPhysicalRanges(workload, out reason))
            {
                if (_reservedBuild.HasValue)
                {
                    _manager.AbortBuild(_reservedBuild.Value, reason);
                    _reservedBuild = null;
                }
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.WorkloadRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            SimpleDdgiGuidingBuildBeginResult begin;
            if (_reservedBuild.HasValue)
            {
                SimpleDdgiGuidingBuildToken reserved = _reservedBuild.Value;
                if (reserved.TargetProposalEpoch != workload.TargetProposalEpoch)
                {
                    reason = "guiding-reserved-build-proposal-epoch-mismatch";
                    _manager.AbortBuild(reserved, reason);
                    _reservedBuild = null;
                    return false;
                }
                begin = new(true, reserved, "reserved");
            }
            else
            {
                begin = _manager.BeginBuild(workload.TargetProposalEpoch);
            }
            if (!begin.Started)
            {
                reason = begin.Reason;
                return false;
            }
            if (!TryClaimBuildDescriptorSetsNoLock(frameIndex, out reason))
            {
                _manager.AbortBuild(begin.Token, reason);
                _reservedBuild = null;
                return false;
            }

            // Expected ownership must not remain backed by caller-mutable CPU
            // storage while graph execution is deferred.
            SimpleDdgiGuidingBuildWorkload frozenWorkload = workload with
            {
                ExpectedHeaders = workload.ExpectedHeaders.Span.ToArray()
            };
            _stagedBuilds[frameIndex] = new PendingStagedBuild(
                allocation.AllocationId,
                begin.Token,
                layout,
                frozenWorkload,
                StagedBuildState.Prepared);
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                "guiding-build-prepared-for-render-graph",
                sourceCacheHandshakeAvailable: true);
            reason = "guiding-build-prepared-for-render-graph";
            return true;
        }
    }

    public bool CanRecordTrainStage(int frameIndex) =>
        CanRecordStagedBuildStage(frameIndex, StagedBuildState.Prepared);

    public bool CanRecordHierarchyBuildStage(int frameIndex) =>
        CanRecordStagedBuildStage(frameIndex, StagedBuildState.Trained);

    public bool CanRecordValidateStage(int frameIndex) =>
        CanRecordStagedBuildStage(frameIndex, StagedBuildState.Built);

    public bool TryRecordTrainStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason) =>
        TryRecordStagedBuildStage(
            commandBuffer,
            frameIndex,
            StagedBuildState.Prepared,
            StagedBuildState.Trained,
            out reason);

    public bool TryRecordHierarchyBuildStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason) =>
        TryRecordStagedBuildStage(
            commandBuffer,
            frameIndex,
            StagedBuildState.Trained,
            StagedBuildState.Built,
            out reason);

    public bool TryRecordValidateStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        out string reason) =>
        TryRecordStagedBuildStage(
            commandBuffer,
            frameIndex,
            StagedBuildState.Built,
            StagedBuildState.Validated,
            out reason);

    /// <summary>
    /// Records the write-bank clear, train, deterministic build, mandatory GPU
    /// validation, and compact header copies.  It does not sample the candidate
    /// bank; sampling is separately gated on CPU header publication.
    /// </summary>
    public bool TryRecordBuild(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        in SimpleDdgiGuidingBuildWorkload workload,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "guiding-command-buffer-invalid";
                return false;
            }
            if (_pendingReadbacks[frameIndex].HasValue ||
                _stagedBuilds[frameIndex].HasValue)
            {
                reason = "guiding-frame-slot-header-readback-still-pending";
                return false;
            }
            if (!TryValidateConfiguredHandshakeNoLock(handshake, out reason))
                return false;
            if (!_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out SimpleDdgiGuidingLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out SimpleDdgiGuidingNativeAllocation nativeAllocation) ||
                _pass is null)
            {
                reason = "guiding-runtime-not-configured-for-exact-build";
                return false;
            }
            if (!workload.TryValidate(layout, out reason) ||
                !TryValidateBuildWorkloadPhysicalRanges(workload, out reason))
            {
                if (_reservedBuild.HasValue)
                {
                    _manager.AbortBuild(_reservedBuild.Value, reason);
                    _reservedBuild = null;
                }
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.WorkloadRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            SimpleDdgiGuidingBuildBeginResult begin;
            if (_reservedBuild.HasValue)
            {
                SimpleDdgiGuidingBuildToken reserved = _reservedBuild.Value;
                if (reserved.TargetProposalEpoch != workload.TargetProposalEpoch)
                {
                    reason = "guiding-reserved-build-proposal-epoch-mismatch";
                    _manager.AbortBuild(reserved, reason);
                    _reservedBuild = null;
                    return false;
                }
                begin = new(true, reserved, "reserved");
            }
            else
            {
                begin = _manager.BeginBuild(workload.TargetProposalEpoch);
            }
            if (!begin.Started)
            {
                reason = begin.Reason;
                return false;
            }
            if (!TryClaimBuildDescriptorSetsNoLock(frameIndex, out reason))
            {
                _manager.AbortBuild(begin.Token, reason);
                _reservedBuild = null;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            try
            {
                _pass.RecordBuild(
                    commandBuffer,
                    frameIndex,
                    layout,
                    begin.Token,
                    nativeAllocation.Buffers,
                    workload);
                RecordHeaderReadback(
                    commandBuffer,
                    frameIndex,
                    allocation.AllocationId,
                    begin.Token,
                    layout,
                    nativeAllocation,
                    workload);
            }
            catch (Exception exception)
            {
                _manager.AbortBuild(
                    begin.Token,
                    "guiding-gpu-recording-failed:" + exception.GetType().Name);
                _reservedBuild = null;
                _pendingReadbacks[frameIndex] = null;
                reason = "guiding-gpu-recording-failed:" + exception.GetType().Name;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                "guiding-build-recorded-awaiting-header-readback",
                sourceCacheHandshakeAvailable: true);
            reason = "guiding-build-recorded-awaiting-header-readback";
            _reservedBuild = null;
            return true;
        }
    }

    /// <summary>
    /// Compatibility overload for callers interested only in candidate-bank
    /// publication.  New integrations should consume the sample completion
    /// overload as well so payload ownership is committed exactly once.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        out SimpleDdgiGuidingPublicationResult publication)
    {
        _ = TryReadCompletedFrame(
            frameIndex,
            out publication,
            out _);
        return publication.Published;
    }

    /// <summary>
    /// Consumes every readback associated with a fence-complete frame slot.
    /// Candidate headers are validated before publication, and sample payload
    /// ownership is exposed only with the exact four zero-error GPU counters.
    /// The return value reports whether at least one pending transaction was
    /// consumed; callers must inspect the two result records for success.
    /// </summary>
    public bool TryReadCompletedFrame(
        int frameIndex,
        out SimpleDdgiGuidingPublicationResult publication,
        out SimpleDdgiGuidingSampleCompletion sampleCompletion)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            PendingStagedBuild? incompleteBuild = _stagedBuilds[frameIndex];
            if (incompleteBuild.HasValue)
            {
                AbortStagedBuildNoLock(
                    frameIndex,
                    incompleteBuild.Value,
                    "guiding-render-graph-build-transaction-incomplete");
            }
            // This public method is intentionally named around the required
            // fence condition.  It is the only point at which the matching
            // private descriptor-ring slot becomes reusable.
            ClearFrameDescriptorClaimsNoLock(frameIndex);
            publication = new(
                false,
                SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                "guiding-no-fence-complete-header-readback");
            sampleCompletion = SimpleDdgiGuidingSampleCompletion.None;
            PendingReadback? pendingBuild = _pendingReadbacks[frameIndex];
            PendingSampleReadback? pendingSample = _pendingSampleReadbacks[frameIndex];
            if (!pendingBuild.HasValue && !pendingSample.HasValue)
            {
                if (!incompleteBuild.HasValue)
                    return false;
                publication = new(
                    false,
                    SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                    "guiding-render-graph-build-transaction-incomplete");
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected,
                    publication.Reason,
                    sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
                return true;
            }

            _pendingReadbacks[frameIndex] = null;
            _pendingSampleReadbacks[frameIndex] = null;
            if (pendingBuild.HasValue)
            {
                CompleteBuildReadbackNoLock(
                    frameIndex,
                    pendingBuild.Value,
                    out publication);
            }
            if (pendingSample.HasValue)
            {
                CompleteSampleReadbackNoLock(
                    frameIndex,
                    pendingSample.Value,
                    out sampleCompletion);
            }

            _lastCompletionDetail = pendingBuild.HasValue
                ? publication.Reason
                : sampleCompletion.Reason;
            if (pendingBuild.HasValue && pendingSample.HasValue)
            {
                _lastCompletionDetail += ";" + sampleCompletion.Reason;
            }

            if (pendingBuild.HasValue &&
                publication.Failure ==
                    SimpleDdgiGuidingPublicationFailure.EmptyPublication)
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.None,
                    publication.Reason,
                    sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
            }
            else if (pendingBuild.HasValue && !publication.Published)
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.HeaderReadbackRejected,
                    publication.Reason,
                    sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
            }
            else if (pendingSample.HasValue && !sampleCompletion.Succeeded)
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SampleReadbackRejected,
                    sampleCompletion.Reason,
                    sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
            }
            else
            {
                string detail = pendingBuild.HasValue
                    ? "guiding-read-bank-published-after-header-validation"
                    : "guiding-sample-validated-after-fence";
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.None,
                    detail,
                    sourceCacheHandshakeAvailable: true);
            }
            return true;
        }
    }

    /// <summary>
    /// Records sampling only from a manager-published read bank.  The final
    /// barrier exposes the output to the exact source-cache consumer described
    /// by the handshake; no current guide is allowed to recompute its PDF.
    /// </summary>
    public bool TryRecordSample(
        CommandBuffer commandBuffer,
        int frameIndex,
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        in SimpleDdgiGuidingSampleWorkload workload,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "guiding-command-buffer-invalid";
                return false;
            }
            if (_pendingSampleReadbacks[frameIndex].HasValue)
            {
                reason = "guiding-frame-slot-sample-readback-still-pending";
                return false;
            }
            if (!TryValidateConfiguredHandshakeNoLock(handshake, out reason))
                return false;
            SimpleDdgiGuidingRuntimeSnapshot snapshot = _manager.Snapshot;
            if (!snapshot.HasReadableDistribution || snapshot.ReadBankIndex is < 0 or > 1 ||
                !_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out SimpleDdgiGuidingLayout layout) ||
                !_allocator.TryGetNativeAllocation(
                    allocation.AllocationId,
                    out SimpleDdgiGuidingNativeAllocation nativeAllocation) ||
                _pass is null)
            {
                reason = "guiding-no-header-validated-read-bank";
                return false;
            }
            if (!workload.TryValidate(handshake, out reason) ||
                !TryValidatePhysicalRange(
                    workload.SampleRequests.Buffer,
                    workload.SampleRequests.OffsetBytes,
                    workload.SampleRequests.RangeBytes,
                    "sample-requests",
                    out reason) ||
                !TryValidatePhysicalRange(
                    workload.ValidationCounters.Buffer,
                    workload.ValidationCounters.OffsetBytes,
                    workload.ValidationCounters.RangeBytes,
                    "validation-counters",
                    out reason) ||
                (workload.UsesGpuResidentWork &&
                    !TryValidateGpuSamplePreparationPhysicalRanges(
                        workload,
                        out reason)))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.WorkloadRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!TryValidateTransferSourceRange(
                    workload.ValidationCounters.Buffer,
                    "sample-validation-counters",
                    out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.WorkloadRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (!TryClaimDescriptorSetNoLock(
                    frameIndex,
                    SimpleDdgiGuidingPassKind.Sample,
                    out reason))
            {
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SampleRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }
            if (workload.UsesGpuResidentWork &&
                !TryClaimDescriptorSetNoLock(
                    frameIndex,
                    SimpleDdgiGuidingPassKind.PrepareSample,
                    out reason))
            {
                _privateDescriptorSetsClaimed[
                    frameIndex,
                    (int)SimpleDdgiGuidingPassKind.Sample] = false;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SampleRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            try
            {
                _pass.RecordSample(
                    commandBuffer,
                    frameIndex,
                    layout,
                    snapshot.ReadBankIndex,
                    snapshot.ReadBankGeneration,
                    nativeAllocation.Buffers,
                    handshake,
                    workload);
                RecordSampleValidationReadback(
                    commandBuffer,
                    frameIndex,
                    allocation.AllocationId,
                    nativeAllocation,
                    workload);
            }
            catch (Exception exception)
            {
                reason = "guiding-sample-recording-failed:" + exception.GetType().Name;
                _pendingSampleReadbacks[frameIndex] = null;
                _privateDescriptorSetsClaimed[
                    frameIndex,
                    (int)SimpleDdgiGuidingPassKind.Sample] = false;
                _privateDescriptorSetsClaimed[
                    frameIndex,
                    (int)SimpleDdgiGuidingPassKind.PrepareSample] = false;
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.SampleRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                "guiding-published-read-bank-sampled",
                sourceCacheHandshakeAvailable: true);
            reason = "guiding-published-read-bank-sampled";
            return true;
        }
    }

    /// <summary>Aborts an unsubmitted candidate transaction without touching a
    /// previously published read bank.</summary>
    public void AbortBuild(string reason = "guiding-build-aborted")
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            foreach (PendingReadback? pending in _pendingReadbacks)
            {
                if (pending.HasValue)
                    _manager.AbortBuild(pending.Value.Token, reason);
            }
            foreach (PendingStagedBuild? staged in _stagedBuilds)
            {
                if (staged.HasValue)
                    _manager.AbortBuild(staged.Value.Token, reason);
            }
            if (_reservedBuild.HasValue)
                _manager.AbortBuild(_reservedBuild.Value, reason);
            _reservedBuild = null;
            Array.Clear(_pendingReadbacks);
            Array.Clear(_stagedBuilds);
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected,
                string.IsNullOrWhiteSpace(reason) ? "guiding-build-aborted" : reason.Trim(),
                sourceCacheHandshakeAvailable: _configuredHandshake.HasValue);
        }
    }

    private bool CanRecordStagedBuildStage(
        int frameIndex,
        StagedBuildState requiredState)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            return !_disposed && _stagedBuilds[frameIndex] is { } pending &&
                pending.State == requiredState;
        }
    }

    private bool TryRecordStagedBuildStage(
        CommandBuffer commandBuffer,
        int frameIndex,
        StagedBuildState requiredState,
        StagedBuildState completedState,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
            {
                reason = "guiding-command-buffer-invalid";
                return false;
            }
            if (_stagedBuilds[frameIndex] is not { } pending ||
                pending.State != requiredState)
            {
                reason = "guiding-staged-build-order-invalid";
                return false;
            }
            if (!_manager.TryGetActiveAllocation(
                    out SimpleDdgiGuidingGpuAllocation allocation,
                    out SimpleDdgiGuidingLayout layout) ||
                allocation.AllocationId != pending.AllocationId ||
                !layout.Equals(pending.Layout) ||
                !_allocator.TryGetNativeAllocation(
                    pending.AllocationId,
                    out SimpleDdgiGuidingNativeAllocation nativeAllocation) ||
                _pass is null)
            {
                reason = "guiding-staged-build-allocation-changed";
                AbortStagedBuildNoLock(frameIndex, pending, reason);
                return false;
            }

            try
            {
                switch (requiredState)
                {
                    case StagedBuildState.Prepared:
                        _pass.RecordTrain(
                            commandBuffer,
                            frameIndex,
                            layout,
                            pending.Token,
                            nativeAllocation.Buffers,
                            pending.Workload);
                        break;
                    case StagedBuildState.Trained:
                        _pass.RecordHierarchyBuild(
                            commandBuffer,
                            frameIndex,
                            layout,
                            pending.Token,
                            nativeAllocation.Buffers,
                            pending.Workload);
                        break;
                    case StagedBuildState.Built:
                        _pass.RecordValidate(
                            commandBuffer,
                            frameIndex,
                            layout,
                            pending.Token,
                            nativeAllocation.Buffers,
                            pending.Workload);
                        RecordHeaderReadback(
                            commandBuffer,
                            frameIndex,
                            pending.AllocationId,
                            pending.Token,
                            layout,
                            nativeAllocation,
                            pending.Workload);
                        break;
                    default:
                        throw new InvalidOperationException(
                            "C3 staged build state is not recordable.");
                }
            }
            catch (Exception exception)
            {
                reason = "guiding-staged-build-recording-failed:" +
                    requiredState + ":" + exception.GetType().Name + ":" +
                    exception.Message;
                AbortStagedBuildNoLock(frameIndex, pending, reason);
                UpdateDiagnosticsNoLock(
                    SimpleDdgiGuidingGpuCapabilityReason.BuildRecordingRejected,
                    reason,
                    sourceCacheHandshakeAvailable: true);
                return false;
            }

            if (completedState == StagedBuildState.Validated)
            {
                _stagedBuilds[frameIndex] = null;
                _reservedBuild = null;
                reason = "guiding-build-recorded-awaiting-header-readback";
            }
            else
            {
                _stagedBuilds[frameIndex] = pending with
                {
                    State = completedState
                };
                reason = completedState == StagedBuildState.Trained
                    ? "guiding-train-stage-recorded"
                    : "guiding-hierarchy-build-stage-recorded";
            }
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                reason,
                sourceCacheHandshakeAvailable: true);
            return true;
        }
    }

    private void AbortStagedBuildNoLock(
        int frameIndex,
        in PendingStagedBuild pending,
        string reason)
    {
        _manager.AbortBuild(pending.Token, reason);
        if (_reservedBuild.HasValue &&
            _reservedBuild.Value.Equals(pending.Token))
        {
            _reservedBuild = null;
        }
        _stagedBuilds[frameIndex] = null;
        // Descriptor claims remain held until this frame slot's fence is
        // observed. A partially recorded command buffer may still reference
        // them even though its candidate can no longer publish.
    }

    private void RecordHeaderReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong allocationId,
        in SimpleDdgiGuidingBuildToken token,
        in SimpleDdgiGuidingLayout layout,
        SimpleDdgiGuidingNativeAllocation nativeAllocation,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        ReadOnlyMemory<SimpleDdgiGuidingExpectedProbeHeader> expectedHeaders =
            workload.ExpectedHeaders;
        if (workload.UsesGpuResidentWork)
        {
            RecordGpuResidentPublicationReadback(
                commandBuffer,
                frameIndex,
                allocationId,
                token,
                nativeAllocation,
                workload);
            return;
        }
        if (expectedHeaders.IsEmpty)
            throw new ArgumentException("C3 header readback requires expected probe identities.",
                nameof(expectedHeaders));

        BufferHandle readback = nativeAllocation.HeaderReadbacks[frameIndex];
        if (!readback.IsValid || nativeAllocation.HeaderCopies.Length < expectedHeaders.Length)
            throw new InvalidOperationException("C3 compact header readback storage is unavailable.");

        VkBuffer source = _bufferManager.GetBuffer(
            token.WriteBankIndex == 0
                ? nativeAllocation.Buffers.DistributionBank0
                : nativeAllocation.Buffers.DistributionBank1);
        VkBuffer destination = _bufferManager.GetBuffer(readback);
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            0UL,
            _bufferManager.GetBufferSize(
                token.WriteBankIndex == 0
                    ? nativeAllocation.Buffers.DistributionBank0
                    : nativeAllocation.Buffers.DistributionBank1));
        ExecuteBufferBarrier(commandBuffer, beforeCopy);

        ReadOnlySpan<SimpleDdgiGuidingExpectedProbeHeader> expected =
            expectedHeaders.Span;
        for (int index = 0; index < expected.Length; index++)
        {
            nativeAllocation.HeaderCopies[index] = new BufferCopy
            {
                SrcOffset = checked(
                    (ulong)expected[index].PhysicalProbeIndex *
                    layout.PersistentBankStrideBytes),
                DstOffset = checked((ulong)index * HeaderBytes),
                Size = HeaderBytes
            };
        }
        fixed (BufferCopy* copies = nativeAllocation.HeaderCopies)
        {
            _context.Api.CmdCopyBuffer(
                commandBuffer,
                source,
                destination,
                (uint)expected.Length,
                copies);
        }

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            checked((ulong)expected.Length * HeaderBytes));
        ExecuteBufferBarrier(commandBuffer, afterCopy);

        var identities = new SimpleDdgiGuidingExpectedProbeHeader[expected.Length];
        expected.CopyTo(identities);
        _pendingReadbacks[frameIndex] = new PendingReadback(
            allocationId,
            token,
            identities,
            GpuGenerated: false,
            GpuCapacity: 0u);
    }

    private void RecordGpuResidentPublicationReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong allocationId,
        in SimpleDdgiGuidingBuildToken token,
        SimpleDdgiGuidingNativeAllocation nativeAllocation,
        in SimpleDdgiGuidingBuildWorkload workload)
    {
        uint capacity = workload.BuildWorkItems.ElementCount;
        ulong publicationBytes = checked(
            (ulong)capacity *
            SimpleDdgiGuidingGpuAbi.PublicationRecordByteCount);
        ulong totalBytes = checked(publicationBytes +
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount);
        if (capacity == 0u || publicationBytes >
                workload.TrainingRecords.RangeBytes)
        {
            throw new InvalidOperationException(
                "C3 GPU publication readback range is invalid.");
        }

        BufferHandle readback = nativeAllocation.HeaderReadbacks[frameIndex];
        if (!readback.IsValid ||
            _bufferManager.GetBufferSize(readback) < totalBytes)
        {
            throw new InvalidOperationException(
                "C3 GPU publication readback storage is unavailable.");
        }
        if (!TryValidateTransferSourceRange(
                workload.TrainingRecords.Buffer,
                "gpu-publication-records",
                out string publicationReason))
        {
            throw new InvalidOperationException(publicationReason);
        }
        if (!TryValidateTransferSourceRange(
                workload.ValidationCounters.Buffer,
                "gpu-publication-counters",
                out string counterReason))
        {
            throw new InvalidOperationException(counterReason);
        }

        VkBuffer publicationSource = _bufferManager.GetBuffer(
            workload.TrainingRecords.Buffer);
        VkBuffer counterSource = _bufferManager.GetBuffer(
            workload.ValidationCounters.Buffer);
        VkBuffer destination = _bufferManager.GetBuffer(readback);
        Span<BufferMemoryBarrier2> sourceBarriers =
            stackalloc BufferMemoryBarrier2[2];
        sourceBarriers[0] = BarrierBuilder.BufferBarrier(
            publicationSource,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            workload.TrainingRecords.OffsetBytes,
            publicationBytes);
        sourceBarriers[1] = BarrierBuilder.BufferBarrier(
            counterSource,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            workload.ValidationCounters.OffsetBytes,
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount);
        ExecuteBufferBarriers(commandBuffer, sourceBarriers);

        var publicationCopy = new BufferCopy
        {
            SrcOffset = workload.TrainingRecords.OffsetBytes,
            DstOffset = 0UL,
            Size = publicationBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            publicationSource,
            destination,
            1u,
            &publicationCopy);
        var counterCopy = new BufferCopy
        {
            SrcOffset = workload.ValidationCounters.OffsetBytes,
            DstOffset = publicationBytes,
            Size = SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            counterSource,
            destination,
            1u,
            &counterCopy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            totalBytes);
        ExecuteBufferBarrier(commandBuffer, afterCopy);
        _pendingReadbacks[frameIndex] = new PendingReadback(
            allocationId,
            token,
            [],
            GpuGenerated: true,
            GpuCapacity: capacity);
    }

    private void RecordSampleValidationReadback(
        CommandBuffer commandBuffer,
        int frameIndex,
        ulong allocationId,
        SimpleDdgiGuidingNativeAllocation nativeAllocation,
        in SimpleDdgiGuidingSampleWorkload workload)
    {
        const ulong CounterBytes =
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount;
        BufferHandle readback = nativeAllocation.SampleValidationReadbacks[frameIndex];
        if (!readback.IsValid)
            throw new InvalidOperationException("C3 sample validation readback is unavailable.");

        VkBuffer source = _bufferManager.GetBuffer(workload.ValidationCounters.Buffer);
        VkBuffer destination = _bufferManager.GetBuffer(readback);
        BufferMemoryBarrier2 beforeCopy = BarrierBuilder.BufferBarrier(
            source,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit,
            workload.ValidationCounters.OffsetBytes,
            CounterBytes);
        ExecuteBufferBarrier(commandBuffer, beforeCopy);

        var copy = new BufferCopy
        {
            SrcOffset = workload.ValidationCounters.OffsetBytes,
            DstOffset = 0UL,
            Size = CounterBytes
        };
        _context.Api.CmdCopyBuffer(
            commandBuffer,
            source,
            destination,
            1u,
            &copy);

        BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
            destination,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.HostBit,
            AccessFlags2.HostReadBit,
            0UL,
            CounterBytes);
        ExecuteBufferBarrier(commandBuffer, afterCopy);

        var commits = new SimpleDdgiGuidingSampleCommit[
            workload.ExpectedCommits.Length];
        workload.ExpectedCommits.Span.CopyTo(commits);
        _pendingSampleReadbacks[frameIndex] = new PendingSampleReadback(
            allocationId,
            commits,
            workload.SampleRequests.ElementCount,
            workload.UsesGpuResidentWork);
    }

    private void CompleteBuildReadbackNoLock(
        int frameIndex,
        in PendingReadback expected,
        out SimpleDdgiGuidingPublicationResult publication)
    {
        if (!_manager.TryGetActiveAllocation(
                out SimpleDdgiGuidingGpuAllocation allocation,
                out SimpleDdgiGuidingLayout layout) ||
            allocation.AllocationId != expected.AllocationId ||
            !_allocator.TryGetNativeAllocation(
                expected.AllocationId,
                out SimpleDdgiGuidingNativeAllocation nativeAllocation))
        {
            _manager.AbortBuild(
                expected.Token,
                "guiding-header-readback-allocation-no-longer-current");
            publication = new(
                false,
                SimpleDdgiGuidingPublicationFailure.NotEnabled,
                "guiding-header-readback-allocation-no-longer-current");
            return;
        }

        if (expected.GpuGenerated)
        {
            CompleteGpuResidentBuildReadbackNoLock(
                frameIndex,
                expected,
                layout,
                nativeAllocation,
                out publication);
            return;
        }

        try
        {
            BufferHandle readback = nativeAllocation.HeaderReadbacks[frameIndex];
            _bufferManager.InvalidateBuffer(
                readback,
                0UL,
                checked((ulong)expected.ExpectedHeaders.Length * HeaderBytes));
            byte* baseAddress = (byte*)_bufferManager.GetMappedPointer(readback);
            if (baseAddress == null)
                throw new InvalidOperationException("Guiding header readback mapping is null.");

            var headers = new SimpleDdgiGuidingPublishedProbeHeader[
                expected.ExpectedHeaders.Length];
            for (int index = 0; index < headers.Length; index++)
            {
                GPUSimpleDdgiGuidingDistributionHeader header =
                    *(GPUSimpleDdgiGuidingDistributionHeader*)(
                        baseAddress + checked(index * (int)HeaderBytes));
                SimpleDdgiGuidingExpectedProbeHeader expectedHeader =
                    expected.ExpectedHeaders[index];
                headers[index] = new SimpleDdgiGuidingPublishedProbeHeader(
                    expectedHeader.PhysicalProbeIndex,
                    expectedHeader.VirtualProbeId,
                    expectedHeader.PageGeneration,
                    header);
            }

            publication = _manager.CompleteBuild(
                expected.Token,
                gpuWorkCompleted: true,
                headers);
        }
        catch (Exception exception)
        {
            _manager.AbortBuild(
                expected.Token,
                "guiding-header-readback-failed:" + exception.GetType().Name);
            publication = new(
                false,
                SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                "guiding-header-readback-failed:" + exception.GetType().Name);
        }
    }

    private void CompleteGpuResidentBuildReadbackNoLock(
        int frameIndex,
        in PendingReadback expected,
        in SimpleDdgiGuidingLayout layout,
        SimpleDdgiGuidingNativeAllocation nativeAllocation,
        out SimpleDdgiGuidingPublicationResult publication)
    {
        try
        {
            ulong publicationBytes = checked(
                (ulong)expected.GpuCapacity *
                SimpleDdgiGuidingGpuAbi.PublicationRecordByteCount);
            ulong totalBytes = checked(publicationBytes +
                SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount);
            BufferHandle readback = nativeAllocation.HeaderReadbacks[frameIndex];
            _bufferManager.InvalidateBuffer(readback, 0UL, totalBytes);
            byte* baseAddress = (byte*)_bufferManager.GetMappedPointer(readback);
            if (baseAddress == null)
            {
                throw new InvalidOperationException(
                    "Guiding GPU publication readback mapping is null.");
            }

            uint* counters = (uint*)(baseAddress + checked((int)publicationBytes));
            uint actualCount = counters[
                SimpleDdgiGuidingGpuAbi.CounterGpuWorkItemCount];
            uint trainingRecordCount = counters[
                SimpleDdgiGuidingGpuAbi.CounterGpuTrainingRecordCount];
            bool preparationValid =
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidRecords] == 0u &&
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidHeaders] == 0u &&
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidPdfs] == 0u &&
                counters[SimpleDdgiGuidingGpuAbi.CounterPublicationRejections] == 0u &&
                counters[SimpleDdgiGuidingGpuAbi.CounterGpuSampleRequestCount] == 0u &&
                counters[SimpleDdgiGuidingGpuAbi.CounterGpuPreparationStatus] ==
                    SimpleDdgiGuidingGpuAbi.Version;
            if (preparationValid && actualCount == 0u && trainingRecordCount == 0u)
            {
                const string NoWorkReason =
                    "guiding-gpu-preparation-produced-no-eligible-work";
                _manager.AbortBuild(expected.Token, NoWorkReason);
                publication = new(
                    false,
                    SimpleDdgiGuidingPublicationFailure.EmptyPublication,
                    NoWorkReason);
                return;
            }

            bool countersValid = preparationValid &&
                actualCount is > 0u && actualCount <= expected.GpuCapacity &&
                trainingRecordCount is > 0u &&
                trainingRecordCount <= checked(actualCount *
                    (uint)layout.DirectionSlotsPerProbe);
            if (!countersValid)
            {
                string counterReason =
                    "guiding-gpu-publication-counters-invalid:" +
                    "invalidRecords=" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterInvalidRecords] +
                    ",invalidHeaders=" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterInvalidHeaders] +
                    ",invalidPdfs=" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterInvalidPdfs] +
                    ",publicationRejections=" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterPublicationRejections] +
                    ",sampleRequests=" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterGpuSampleRequestCount] +
                    ",preparationStatus=0x" + counters[
                        SimpleDdgiGuidingGpuAbi.CounterGpuPreparationStatus]
                        .ToString("x8") +
                    ",workItems=" + actualCount +
                    ",trainingRecords=" + trainingRecordCount +
                    ",capacity=" + expected.GpuCapacity;
                _manager.AbortBuild(
                    expected.Token,
                    counterReason);
                publication = new(
                    false,
                    SimpleDdgiGuidingPublicationFailure.HeaderInvalid,
                    counterReason);
                return;
            }

            var headers = new SimpleDdgiGuidingPublishedProbeHeader[
                checked((int)actualCount)];
            for (int index = 0; index < headers.Length; index++)
            {
                GPUSimpleDdgiGuidingPublicationRecord record =
                    *(GPUSimpleDdgiGuidingPublicationRecord*)(
                        baseAddress + checked(index *
                            (int)SimpleDdgiGuidingGpuAbi
                                .PublicationRecordByteCount));
                if (record.Status != SimpleDdgiGuidingGpuAbi.Version ||
                    record.PhysicalProbeIndex >=
                        (uint)layout.PhysicalProbeCapacity ||
                    record.VirtualProbeId != record.Header.VirtualProbeId ||
                    record.PageGeneration != record.Header.PageGeneration)
                {
                    _manager.AbortBuild(
                        expected.Token,
                        "guiding-gpu-publication-record-invalid");
                    publication = new(
                        false,
                        SimpleDdgiGuidingPublicationFailure.HeaderInvalid,
                        "guiding-gpu-publication-record-invalid");
                    return;
                }
                headers[index] = new SimpleDdgiGuidingPublishedProbeHeader(
                    record.PhysicalProbeIndex,
                    record.VirtualProbeId,
                    record.PageGeneration,
                    record.Header);
            }
            Array.Sort(
                headers,
                static (left, right) => left.PhysicalProbeIndex.CompareTo(
                    right.PhysicalProbeIndex));
            publication = _manager.CompleteBuild(
                expected.Token,
                gpuWorkCompleted: true,
                headers);
        }
        catch (Exception exception)
        {
            _manager.AbortBuild(
                expected.Token,
                "guiding-gpu-publication-readback-failed:" +
                    exception.GetType().Name);
            publication = new(
                false,
                SimpleDdgiGuidingPublicationFailure.GpuWorkIncomplete,
                "guiding-gpu-publication-readback-failed:" +
                    exception.GetType().Name);
        }
    }

    private void CompleteSampleReadbackNoLock(
        int frameIndex,
        in PendingSampleReadback expected,
        out SimpleDdgiGuidingSampleCompletion completion)
    {
        const ulong CounterBytes =
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount;
        if (!_manager.TryGetActiveAllocation(
                out SimpleDdgiGuidingGpuAllocation allocation,
                out _) ||
            allocation.AllocationId != expected.AllocationId ||
            !_allocator.TryGetNativeAllocation(
                expected.AllocationId,
                out SimpleDdgiGuidingNativeAllocation nativeAllocation))
        {
            completion = new(
                true,
                false,
                default,
                expected.Commits,
                "guiding-sample-readback-allocation-no-longer-current");
            return;
        }

        try
        {
            BufferHandle readback = nativeAllocation.SampleValidationReadbacks[frameIndex];
            _bufferManager.InvalidateBuffer(readback, 0UL, CounterBytes);
            uint* counters = (uint*)_bufferManager.GetMappedPointer(readback);
            if (counters == null)
            {
                throw new InvalidOperationException(
                    "Guiding sample validation readback mapping is null.");
            }

            var values = new SimpleDdgiGuidingValidationCounters(
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidRecords],
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidHeaders],
                counters[SimpleDdgiGuidingGpuAbi.CounterInvalidPdfs],
                counters[SimpleDdgiGuidingGpuAbi.CounterPublicationRejections]);
            var words = new ReadOnlySpan<uint>(
                counters,
                checked((int)SimpleDdgiGuidingGpuAbi
                    .ValidationCounterWordCount));
            uint requestCount = expected.GpuGenerated
                ? counters[SimpleDdgiGuidingGpuAbi
                    .CounterGpuSampleRequestCount]
                : expected.RequestCount;
            if (requestCount > expected.RequestCount)
            {
                completion = new SimpleDdgiGuidingSampleCompletion(
                    true,
                    false,
                    values,
                    expected.Commits,
                    "guiding-sample-gpu-request-count-exceeds-capacity");
                return;
            }
            if (!SimpleDdgiGuidingSampleTelemetry.TryCreate(
                    words,
                    requestCount,
                    values,
                    out SimpleDdgiGuidingSampleTelemetry telemetry,
                    out string telemetryReason,
                    expected.GpuGenerated))
            {
                completion = new SimpleDdgiGuidingSampleCompletion(
                    true,
                    false,
                    values,
                    expected.Commits,
                    telemetryReason);
                return;
            }
            completion = new(
                true,
                true,
                values,
                expected.Commits,
                values.AreZero
                    ? "guiding-sample-validated-after-fence"
                    : "guiding-sample-validation-counters-nonzero")
            {
                Telemetry = telemetry
            };
        }
        catch (Exception exception)
        {
            completion = new(
                true,
                false,
                default,
                expected.Commits,
                "guiding-sample-readback-failed:" + exception.GetType().Name);
        }
    }

    private bool TryValidateConfiguredHandshakeNoLock(
        in SimpleDdgiGuidingSourceCacheHandshake handshake,
        out string reason)
    {
        if (!_configuredHandshake.HasValue)
        {
            reason = "guiding-source-cache-handshake-not-configured";
            return false;
        }
        if (!_configuredHandshake.Value.Equals(handshake))
        {
            reason = "guiding-source-cache-handshake-changed-reconfigure-required";
            return false;
        }
        if (!handshake.TryValidate(out SimpleDdgiGuidingGpuCapabilityReason capabilityReason,
                out reason))
        {
            UpdateDiagnosticsNoLock(
                capabilityReason,
                reason,
                sourceCacheHandshakeAvailable: handshake.IsAvailable);
            return false;
        }
        if (!TryValidatePhysicalRange(
                handshake.DirectionPdfSidecar,
                handshake.DirectionPdfSidecarOffsetBytes,
                handshake.DirectionPdfSidecarBytes,
                "source-cache-direction-pdf-sidecar",
                out reason))
        {
            UpdateDiagnosticsNoLock(
                SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid,
                reason,
                sourceCacheHandshakeAvailable: true);
            return false;
        }
        return true;
    }

    private bool TryValidateBuildWorkloadPhysicalRanges(
        in SimpleDdgiGuidingBuildWorkload workload,
        out string reason) =>
        TryValidatePhysicalRange(
            workload.TrainingRecords.Buffer,
            workload.TrainingRecords.OffsetBytes,
            workload.TrainingRecords.RangeBytes,
            "training-records",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TrainingWorkItems.Buffer,
            workload.TrainingWorkItems.OffsetBytes,
            workload.TrainingWorkItems.RangeBytes,
            "training-work-items",
            out reason) &&
        TryValidatePhysicalRange(
            workload.BuildWorkItems.Buffer,
            workload.BuildWorkItems.OffsetBytes,
            workload.BuildWorkItems.RangeBytes,
            "build-work-items",
            out reason) &&
        TryValidatePhysicalRange(
            workload.ValidationCounters.Buffer,
            workload.ValidationCounters.OffsetBytes,
            workload.ValidationCounters.RangeBytes,
            "validation-counters",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TraceTrainingSource.Params.Buffer,
            workload.TraceTrainingSource.Params.OffsetBytes,
            workload.TraceTrainingSource.Params.RangeBytes,
            "trace-params",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TraceTrainingSource.RayResultScratch.Buffer,
            workload.TraceTrainingSource.RayResultScratch.OffsetBytes,
            workload.TraceTrainingSource.RayResultScratch.RangeBytes,
            "trace-ray-results",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TraceTrainingSource.ProbeUpdateQueue.Buffer,
            workload.TraceTrainingSource.ProbeUpdateQueue.OffsetBytes,
            workload.TraceTrainingSource.ProbeUpdateQueue.RangeBytes,
            "trace-probe-updates",
            out reason) &&
        (!workload.UsesGpuResidentWork ||
            TryValidatePhysicalRange(
                workload.GpuResidentSource.SchedulerArenaBuffer,
                0UL,
                workload.GpuResidentSource.SchedulerArenaBytes,
                "gpu-resident-scheduler-arena",
                out reason));

    private bool TryValidateGpuSamplePreparationPhysicalRanges(
        in SimpleDdgiGuidingSampleWorkload workload,
        out string reason) =>
        TryValidatePhysicalRange(
            workload.TrainingWorkItems.Buffer,
            workload.TrainingWorkItems.OffsetBytes,
            workload.TrainingWorkItems.RangeBytes,
            "sample-prepare-training-work-items",
            out reason) &&
        TryValidatePhysicalRange(
            workload.BuildWorkItems.Buffer,
            workload.BuildWorkItems.OffsetBytes,
            workload.BuildWorkItems.RangeBytes,
            "sample-prepare-build-work-items",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TraceTrainingSource.Params.Buffer,
            workload.TraceTrainingSource.Params.OffsetBytes,
            workload.TraceTrainingSource.Params.RangeBytes,
            "sample-prepare-params",
            out reason) &&
        TryValidatePhysicalRange(
            workload.TraceTrainingSource.ProbeUpdateQueue.Buffer,
            workload.TraceTrainingSource.ProbeUpdateQueue.OffsetBytes,
            workload.TraceTrainingSource.ProbeUpdateQueue.RangeBytes,
            "sample-prepare-update-queue",
            out reason) &&
        TryValidatePhysicalRange(
            workload.GpuResidentSource.SchedulerArenaBuffer,
            0UL,
            workload.GpuResidentSource.SchedulerArenaBytes,
            "sample-prepare-scheduler-arena",
            out reason);

    private bool TryValidatePhysicalRange(
        BufferHandle buffer,
        ulong offsetBytes,
        ulong rangeBytes,
        string name,
        out string reason)
    {
        try
        {
            ulong end = checked(offsetBytes + rangeBytes);
            if (!buffer.IsValid || rangeBytes == 0UL ||
                end > _bufferManager.GetBufferSize(buffer))
            {
                reason = "guiding-" + name + "-range-exceeds-live-buffer";
                return false;
            }
            if ((_bufferManager.GetBufferUsage(buffer) &
                    BufferUsageFlags.StorageBufferBit) == 0)
            {
                reason = "guiding-" + name + "-buffer-lacks-storage-usage";
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "guiding-" + name + "-buffer-is-not-live:" +
                exception.GetType().Name;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateTransferSourceRange(
        BufferHandle buffer,
        string name,
        out string reason)
    {
        try
        {
            if ((_bufferManager.GetBufferUsage(buffer) &
                    BufferUsageFlags.TransferSrcBit) == 0)
            {
                reason = "guiding-" + name + "-buffer-lacks-transfer-source-usage";
                return false;
            }
        }
        catch (Exception exception)
        {
            reason = "guiding-" + name + "-buffer-is-not-live:" +
                exception.GetType().Name;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void DisableAtSafeTransitionNoLock(
        SimpleDdgiGuidingGpuCapabilityReason capabilityReason,
        string detail,
        bool sourceCacheHandshakeAvailable)
    {
        if (_reservedBuild.HasValue)
            _manager.AbortBuild(_reservedBuild.Value, detail);
        foreach (PendingStagedBuild? staged in _stagedBuilds)
        {
            if (staged.HasValue)
                _manager.AbortBuild(staged.Value.Token, detail);
        }
        _reservedBuild = null;
        ClearPendingReadbacksNoLock();
        _allocator.TryBindFallback(out _);
        _manager.Reconcile(
            new SimpleDdgiGuidingRuntimeRequest(false, default),
            _allocator);
        _configuredHandshake = null;
        _configuredStoragePackingMode = null;
        // A disabled C3 allocation does not invalidate immutable pipelines or
        // their descriptor layouts. First-frame admission intentionally uses
        // this safe fallback state before the source-cache handshake exists;
        // retaining the prewarmed pass prevents all six guiding pipelines
        // from being recreated on the render thread when admission publishes.
        UpdateDiagnosticsNoLock(
            capabilityReason,
            detail,
            sourceCacheHandshakeAvailable);
    }

    private void ClearPendingReadbacksNoLock()
    {
        Array.Clear(_pendingReadbacks);
        Array.Clear(_pendingSampleReadbacks);
        Array.Clear(_stagedBuilds);
    }

    private void AbortPendingReadbacksNoLock(string reason)
    {
        if (_reservedBuild.HasValue)
            _manager.AbortBuild(_reservedBuild.Value, reason);
        _reservedBuild = null;
        foreach (PendingReadback? pending in _pendingReadbacks)
        {
            if (pending.HasValue)
                _manager.AbortBuild(pending.Value.Token, reason);
        }
        foreach (PendingStagedBuild? pending in _stagedBuilds)
        {
            if (pending.HasValue)
                _manager.AbortBuild(pending.Value.Token, reason);
        }
        ClearPendingReadbacksNoLock();
    }

    private bool TryClaimBuildDescriptorSetsNoLock(int frameIndex, out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        const int Train = (int)SimpleDdgiGuidingPassKind.Train;
        const int Build = (int)SimpleDdgiGuidingPassKind.Build;
        const int Validate = (int)SimpleDdgiGuidingPassKind.Validate;
        const int Extract = (int)SimpleDdgiGuidingPassKind.Extract;
        const int PrepareBuild =
            (int)SimpleDdgiGuidingPassKind.PrepareBuild;
        if (_privateDescriptorSetsClaimed[frameIndex, Train] ||
            _privateDescriptorSetsClaimed[frameIndex, Build] ||
            _privateDescriptorSetsClaimed[frameIndex, Validate] ||
            _privateDescriptorSetsClaimed[frameIndex, Extract] ||
            _privateDescriptorSetsClaimed[frameIndex, PrepareBuild])
        {
            reason = "guiding-private-descriptor-build-slot-not-fence-reclaimed";
            return false;
        }

        _privateDescriptorSetsClaimed[frameIndex, Train] = true;
        _privateDescriptorSetsClaimed[frameIndex, Build] = true;
        _privateDescriptorSetsClaimed[frameIndex, Validate] = true;
        _privateDescriptorSetsClaimed[frameIndex, Extract] = true;
        _privateDescriptorSetsClaimed[frameIndex, PrepareBuild] = true;
        reason = string.Empty;
        return true;
    }

    private bool TryClaimDescriptorSetNoLock(
        int frameIndex,
        SimpleDdgiGuidingPassKind kind,
        out string reason)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        int kindIndex = (int)kind;
        if ((uint)kindIndex >= 7u)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (_privateDescriptorSetsClaimed[frameIndex, kindIndex])
        {
            reason = "guiding-private-descriptor-slot-not-fence-reclaimed";
            return false;
        }

        _privateDescriptorSetsClaimed[frameIndex, kindIndex] = true;
        reason = string.Empty;
        return true;
    }

    private void ReleaseBuildDescriptorClaimsNoLock(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        _privateDescriptorSetsClaimed[
            frameIndex,
            (int)SimpleDdgiGuidingPassKind.Extract] = false;
        _privateDescriptorSetsClaimed[
            frameIndex,
            (int)SimpleDdgiGuidingPassKind.Train] = false;
        _privateDescriptorSetsClaimed[
            frameIndex,
            (int)SimpleDdgiGuidingPassKind.Build] = false;
        _privateDescriptorSetsClaimed[
            frameIndex,
            (int)SimpleDdgiGuidingPassKind.Validate] = false;
        _privateDescriptorSetsClaimed[
            frameIndex,
            (int)SimpleDdgiGuidingPassKind.PrepareBuild] = false;
    }

    private void ClearFrameDescriptorClaimsNoLock(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        for (int kindIndex = 0; kindIndex < 7; kindIndex++)
            _privateDescriptorSetsClaimed[frameIndex, kindIndex] = false;
    }

    private void ClearDescriptorClaimsNoLock() =>
        Array.Clear(_privateDescriptorSetsClaimed);

    private void SynchronizeDescriptorReadersNoLock() =>
        _waitForDescriptorReaders?.Invoke();

    private void UpdateDiagnosticsNoLock(
        SimpleDdgiGuidingGpuCapabilityReason capabilityReason,
        string detail,
        bool sourceCacheHandshakeAvailable)
    {
        Diagnostics = new SimpleDdgiGuidingGpuRuntimeDiagnostics(
            capabilityReason,
            sourceCacheHandshakeAvailable,
            _allocator.HasDescriptorContext,
            HasPendingReadbackNoLock(),
            _manager.Snapshot,
            string.IsNullOrWhiteSpace(detail) ? "unknown" : detail.Trim())
        {
            LastCompletionDetail = _lastCompletionDetail
        };
    }

    private bool HasPendingReadbackNoLock()
    {
        foreach (PendingReadback? pending in _pendingReadbacks)
        {
            if (pending.HasValue)
                return true;
        }
        foreach (PendingSampleReadback? pending in _pendingSampleReadbacks)
        {
            if (pending.HasValue)
                return true;
        }
        foreach (PendingStagedBuild? pending in _stagedBuilds)
        {
            if (pending.HasValue)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _allocator.TryBindFallback(out _);
            _manager.Dispose();
            _pass?.Dispose();
            _pass = null;
            _allocator.Dispose();
            _configuredHandshake = null;
            _configuredStoragePackingMode = null;
            _pipelineStoragePackingMode = null;
            _reservedBuild = null;
            ClearPendingReadbacksNoLock();
            Diagnostics = new SimpleDdgiGuidingGpuRuntimeDiagnostics(
                SimpleDdgiGuidingGpuCapabilityReason.Disposed,
                false,
                false,
                false,
                _manager.Snapshot,
                "disposed");
        }
    }

    private void ExecuteBufferBarrier(
        CommandBuffer commandBuffer,
        BufferMemoryBarrier2 barrier)
    {
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1u,
            PBufferMemoryBarriers = &barrier
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
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
                BufferMemoryBarrierCount = checked((uint)barriers.Length),
                PBufferMemoryBarriers = pointer
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SimpleDdgiGuidingVulkanRuntime));
    }

    private readonly record struct PendingReadback(
        ulong AllocationId,
        SimpleDdgiGuidingBuildToken Token,
        SimpleDdgiGuidingExpectedProbeHeader[] ExpectedHeaders,
        bool GpuGenerated,
        uint GpuCapacity);

    private readonly record struct PendingSampleReadback(
        ulong AllocationId,
        SimpleDdgiGuidingSampleCommit[] Commits,
        uint RequestCount,
        bool GpuGenerated);

    private enum StagedBuildState : byte
    {
        Prepared = 0,
        Trained = 1,
        Built = 2,
        Validated = 3
    }

    private readonly record struct PendingStagedBuild(
        ulong AllocationId,
        SimpleDdgiGuidingBuildToken Token,
        SimpleDdgiGuidingLayout Layout,
        SimpleDdgiGuidingBuildWorkload Workload,
        StagedBuildState State);

    private sealed class VulkanAllocator : ISimpleDdgiGuidingGpuResourceAllocator,
        IDisposable
    {
        private readonly BufferManager _bufferManager;
        private readonly AdvancedGiTransientBufferArena? _transientBufferArena;
        private readonly Dictionary<ulong, SimpleDdgiGuidingNativeAllocation> _allocations = new();
        private BindlessHeap? _bindlessHeap;
        private BufferHandle _fallbackBuffer = BufferHandle.Invalid;
        private ulong _fallbackBytes;
        private ulong _nextAllocationId;
        private bool _disposed;

        public VulkanAllocator(
            BufferManager bufferManager,
            AdvancedGiTransientBufferArena? transientBufferArena)
        {
            _bufferManager = bufferManager;
            _transientBufferArena = transientBufferArena;
            ValidateFixedBindlessSlots();
        }

        public bool HasDescriptorContext =>
            !_disposed && _bindlessHeap is not null && _fallbackBuffer.IsValid &&
            _fallbackBytes >= sizeof(uint) * 4UL;

        public BindlessHeap DescriptorHeap => _bindlessHeap ??
            throw new InvalidOperationException(
                "C3 bindless descriptor context is unavailable.");

        public bool TrySetDescriptorContext(
            BindlessHeap bindlessHeap,
            BufferHandle fallbackBuffer,
            ulong fallbackBytes,
            out string reason)
        {
            if (_disposed)
            {
                reason = "guiding-vulkan-allocator-disposed";
                return false;
            }
            if (!fallbackBuffer.IsValid || fallbackBytes < sizeof(uint) * 4UL)
            {
                reason = "guiding-safe-descriptor-fallback-invalid";
                return false;
            }
            try
            {
                if (_bufferManager.GetBufferSize(fallbackBuffer) < fallbackBytes ||
                    (_bufferManager.GetBufferUsage(fallbackBuffer) &
                        BufferUsageFlags.StorageBufferBit) == 0)
                {
                    reason = "guiding-safe-descriptor-fallback-range-or-usage-invalid";
                    return false;
                }
            }
            catch (Exception exception)
            {
                reason = "guiding-safe-descriptor-fallback-not-live:" +
                    exception.GetType().Name;
                return false;
            }

            _bindlessHeap = bindlessHeap;
            _fallbackBuffer = fallbackBuffer;
            _fallbackBytes = fallbackBytes;
            return TryBindFallback(out reason);
        }

        public SimpleDdgiGuidingGpuAllocation Allocate(in SimpleDdgiGuidingLayout layout)
        {
            ThrowIfDisposed();
            ulong bankBytes = checked(layout.PersistentDoubleBufferedBytes / 2UL);
            if (bankBytes == 0UL || layout.PersistentDoubleBufferedBytes % 2UL != 0UL ||
                layout.TrainingScratchBytes == 0UL ||
                layout.ScheduledGuidedProbeCapacity <= 0 ||
                (ulong)layout.ScheduledGuidedProbeCapacity >
                    ((ulong)int.MaxValue -
                        SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount) /
                    SimpleDdgiGuidingGpuAbi.PublicationRecordByteCount)
            {
                throw new ArgumentException("C3 Vulkan allocation requires a complete exact layout.",
                    nameof(layout));
            }

            BufferHandle bank0 = BufferHandle.Invalid;
            BufferHandle bank1 = BufferHandle.Invalid;
            BufferHandle scratch = BufferHandle.Invalid;
            ulong scratchOffsetBytes = 0UL;
            ulong scratchRangeBytes = layout.TrainingScratchBytes;
            bool ownsScratch = false;
            BufferHandle validationReference = BufferHandle.Invalid;
            var headerReadbacks = new BufferHandle[RenderingConstants.FramesInFlight];
            var sampleValidationReadbacks =
                new BufferHandle[RenderingConstants.FramesInFlight];
            try
            {
                bank0 = _bufferManager.CreateDeviceBuffer(
                    bankBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI Guiding Distribution Bank 0");
                bank1 = _bufferManager.CreateDeviceBuffer(
                    bankBytes,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                    requireDeviceAddress: false,
                    MemoryBudgetCategory.GlobalIllumination,
                    "Simple DDGI Guiding Distribution Bank 1");
                if (_transientBufferArena is not null)
                {
                    if (!layout.TransientWorkspace.IsComplete)
                    {
                        throw new InvalidOperationException(
                            "C3 central transient workspace layout is incomplete.");
                    }
                    if (!_transientBufferArena.TryGetSlice(
                            SimpleDdgiAdvancedMemoryCategory.DirectionalGuidingBuildScratch,
                            layout.TransientWorkspace.TotalBytes,
                            layout.StorageAlignmentBytes,
                            out AdvancedGiTransientBufferSlice workspace,
                            out string workspaceReason))
                    {
                        throw new InvalidOperationException(
                            "C3 central transient workspace is unavailable: " +
                            workspaceReason);
                    }

                    scratch = workspace.Buffer;
                    scratchOffsetBytes = checked(
                        workspace.Offset +
                        layout.TransientWorkspace.TrainingScratchOffsetBytes);
                    scratchRangeBytes = layout.TransientWorkspace.TrainingScratchBytes;
                    ulong scratchEnd = checked(scratchOffsetBytes + scratchRangeBytes);
                    ulong workspaceEnd = checked(workspace.Offset + workspace.Bytes);
                    if (scratchRangeBytes == 0UL || scratchEnd > workspaceEnd)
                    {
                        throw new InvalidOperationException(
                            "C3 training scratch exceeds its central transient workspace.");
                    }
                }
                else
                {
                    scratch = _bufferManager.CreateDeviceBuffer(
                        layout.TrainingScratchBytes,
                        BufferUsageFlags.StorageBufferBit,
                        requireDeviceAddress: false,
                        MemoryBudgetCategory.GlobalIllumination,
                        "Simple DDGI Guiding Training Scratch");
                    ownsScratch = true;
                }
                if (layout.ValidationReferenceAllocated)
                {
                    validationReference = _bufferManager.CreateDeviceBuffer(
                        layout.ValidationReferenceBankBytes,
                        BufferUsageFlags.StorageBufferBit,
                        requireDeviceAddress: false,
                        MemoryBudgetCategory.GlobalIllumination,
                        "Simple DDGI Guiding Validation Reference");
                }

                ulong cpuHeaderReadbackBytes = checked(
                    (ulong)layout.ScheduledGuidedProbeCapacity * HeaderBytes);
                ulong gpuPublicationReadbackBytes = checked(
                    (ulong)layout.ScheduledGuidedProbeCapacity *
                        SimpleDdgiGuidingGpuAbi.PublicationRecordByteCount +
                    SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount);
                ulong headerReadbackBytes = Math.Max(
                    cpuHeaderReadbackBytes,
                    gpuPublicationReadbackBytes);
                for (int frameIndex = 0; frameIndex < headerReadbacks.Length; frameIndex++)
                {
                    headerReadbacks[frameIndex] = _bufferManager.CreateBuffer(
                        headerReadbackBytes,
                        BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessRandomBit,
                        $"Simple DDGI Guiding Header Readback Frame {frameIndex}",
                        MemoryBudgetCategory.GlobalIllumination);
                    sampleValidationReadbacks[frameIndex] = _bufferManager.CreateBuffer(
                        SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount,
                        BufferUsageFlags.TransferDstBit,
                        MemoryUsage.AutoPreferHost,
                        AllocationCreateFlags.MappedBit |
                            AllocationCreateFlags.HostAccessRandomBit,
                        $"Simple DDGI Guiding Sample Validation Readback Frame {frameIndex}",
                        MemoryBudgetCategory.GlobalIllumination);
                }

                ulong allocationId = NextAllocationId();
                var buffers = new SimpleDdgiGuidingVulkanBuffers(
                    bank0,
                    bank1,
                    scratch,
                    scratchOffsetBytes,
                    scratchRangeBytes,
                    validationReference);
                _allocations.Add(
                    allocationId,
                    new SimpleDdgiGuidingNativeAllocation(
                        buffers,
                        headerReadbacks,
                        sampleValidationReadbacks,
                        new BufferCopy[layout.ScheduledGuidedProbeCapacity],
                        ownsScratch));
                return new SimpleDdgiGuidingGpuAllocation(
                    allocationId,
                    new SimpleDdgiGuidingGpuBuffer(
                        _bufferManager.GetBuffer(bank0).Handle,
                        bankBytes),
                    new SimpleDdgiGuidingGpuBuffer(
                        _bufferManager.GetBuffer(bank1).Handle,
                        bankBytes),
                    new SimpleDdgiGuidingGpuBuffer(
                        _bufferManager.GetBuffer(scratch).Handle,
                        layout.TrainingScratchBytes),
                    layout.ValidationReferenceAllocated
                        ? new SimpleDdgiGuidingGpuBuffer(
                            _bufferManager.GetBuffer(validationReference).Handle,
                            layout.ValidationReferenceBankBytes)
                        : default,
                    DescriptorCount: 3u);
            }
            catch
            {
                Destroy(bank0);
                Destroy(bank1);
                if (ownsScratch)
                    Destroy(scratch);
                Destroy(validationReference);
                foreach (BufferHandle readback in headerReadbacks)
                    Destroy(readback);
                foreach (BufferHandle readback in sampleValidationReadbacks)
                    Destroy(readback);
                throw;
            }
        }

        public void Retire(SimpleDdgiGuidingGpuAllocation allocation)
        {
            if (!_allocations.Remove(allocation.AllocationId,
                    out SimpleDdgiGuidingNativeAllocation? native))
            {
                return;
            }
            Destroy(native.Buffers.DistributionBank0);
            Destroy(native.Buffers.DistributionBank1);
            if (native.OwnsTrainingScratch)
                Destroy(native.Buffers.TrainingScratch);
            Destroy(native.Buffers.ValidationReference);
            foreach (BufferHandle readback in native.HeaderReadbacks)
                Destroy(readback);
            foreach (BufferHandle readback in native.SampleValidationReadbacks)
                Destroy(readback);
        }

        public bool TryGetNativeAllocation(
            ulong allocationId,
            out SimpleDdgiGuidingNativeAllocation allocation)
        {
            if (_allocations.TryGetValue(
                    allocationId,
                    out SimpleDdgiGuidingNativeAllocation? native))
            {
                allocation = native;
                return true;
            }
            allocation = null!;
            return false;
        }

        public bool TryBindAllocation(ulong allocationId, out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "guiding-bindless-descriptor-context-unavailable";
                return false;
            }
            if (!_allocations.TryGetValue(
                    allocationId,
                    out SimpleDdgiGuidingNativeAllocation? native))
            {
                reason = "guiding-native-allocation-not-found";
                return false;
            }

            try
            {
                Register(
                    SimpleDdgiGuidingBindlessSlots.DistributionBank0,
                    native.Buffers.DistributionBank0,
                    _bufferManager.GetBufferSize(native.Buffers.DistributionBank0));
                Register(
                    SimpleDdgiGuidingBindlessSlots.DistributionBank1,
                    native.Buffers.DistributionBank1,
                    _bufferManager.GetBufferSize(native.Buffers.DistributionBank1));
                Register(
                    SimpleDdgiGuidingBindlessSlots.TrainingScratch,
                    native.Buffers.TrainingScratch,
                    native.Buffers.TrainingScratchOffsetBytes,
                    native.Buffers.TrainingScratchRangeBytes);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "guiding-descriptor-publication-failed:" + exception.GetType().Name;
                return false;
            }
        }

        public bool TryBindFallback(out string reason)
        {
            if (!HasDescriptorContext)
            {
                reason = "guiding-safe-descriptor-fallback-unavailable";
                return false;
            }
            try
            {
                Register(SimpleDdgiGuidingBindlessSlots.DistributionBank0,
                    _fallbackBuffer, _fallbackBytes);
                Register(SimpleDdgiGuidingBindlessSlots.DistributionBank1,
                    _fallbackBuffer, _fallbackBytes);
                Register(SimpleDdgiGuidingBindlessSlots.TrainingScratch,
                    _fallbackBuffer, _fallbackBytes);
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = "guiding-safe-descriptor-publication-failed:" +
                    exception.GetType().Name;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (SimpleDdgiGuidingNativeAllocation allocation in _allocations.Values)
            {
                Destroy(allocation.Buffers.DistributionBank0);
                Destroy(allocation.Buffers.DistributionBank1);
                if (allocation.OwnsTrainingScratch)
                    Destroy(allocation.Buffers.TrainingScratch);
                Destroy(allocation.Buffers.ValidationReference);
                foreach (BufferHandle readback in allocation.HeaderReadbacks)
                    Destroy(readback);
                foreach (BufferHandle readback in allocation.SampleValidationReadbacks)
                    Destroy(readback);
            }
            _allocations.Clear();
            _bindlessHeap = null;
            _fallbackBuffer = BufferHandle.Invalid;
            _fallbackBytes = 0UL;
        }

        private void Register(
            int slot,
            BufferHandle buffer,
            ulong bytes) => Register(slot, buffer, 0UL, bytes);

        private void Register(
            int slot,
            BufferHandle buffer,
            ulong offsetBytes,
            ulong bytes)
        {
            if (!buffer.IsValid || bytes == 0UL ||
                (slot != SimpleDdgiGuidingBindlessSlots.DistributionBank0 &&
                 slot != SimpleDdgiGuidingBindlessSlots.DistributionBank1 &&
                 slot != SimpleDdgiGuidingBindlessSlots.TrainingScratch) ||
                !BindlessIndex.IsStaticBufferIndex(slot))
            {
                throw new InvalidOperationException(
                    "C3 may publish only valid fixed slots 200, 201, and 202.");
            }
            ulong bufferBytes = _bufferManager.GetBufferSize(buffer);
            if (offsetBytes > bufferBytes || bytes > bufferBytes - offsetBytes)
            {
                throw new InvalidOperationException(
                    "C3 bindless descriptor range exceeds its live buffer.");
            }
            _bindlessHeap!.RegisterStorageBuffer(
                slot,
                _bufferManager.GetBuffer(buffer),
                offsetBytes,
                bytes);
        }

        private static void ValidateFixedBindlessSlots()
        {
            if (SimpleDdgiGuidingBindlessSlots.DistributionBank0 != 200 ||
                SimpleDdgiGuidingBindlessSlots.DistributionBank1 != 201 ||
                SimpleDdgiGuidingBindlessSlots.TrainingScratch != 202 ||
                SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar != 203 ||
                BindlessIndex.SimpleDdgiGuidingDistributionBank0Buffer != 200 ||
                BindlessIndex.SimpleDdgiGuidingDistributionBank1Buffer != 201 ||
                BindlessIndex.SimpleDdgiGuidingTrainingScratchBuffer != 202 ||
                BindlessIndex.SimpleDdgiGuidingDirectionPdfSidecarBuffer != 203)
            {
                throw new InvalidOperationException(
                    "C3 requires immutable bindless slots 200, 201, 202, and source-cache-owned 203.");
            }
        }

        private ulong NextAllocationId()
        {
            do
            {
                _nextAllocationId = _nextAllocationId == ulong.MaxValue
                    ? 1UL
                    : _nextAllocationId + 1UL;
            }
            while (_allocations.ContainsKey(_nextAllocationId));
            return _nextAllocationId;
        }

        private void Destroy(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VulkanAllocator));
        }
    }
}

/// <summary>Native C3 allocation owned by one lifecycle allocation epoch.</summary>
internal readonly record struct SimpleDdgiGuidingVulkanBuffers(
    BufferHandle DistributionBank0,
    BufferHandle DistributionBank1,
    BufferHandle TrainingScratch,
    ulong TrainingScratchOffsetBytes,
    ulong TrainingScratchRangeBytes,
    BufferHandle ValidationReference)
{
    public bool IsComplete => DistributionBank0.IsValid && DistributionBank1.IsValid &&
        TrainingScratch.IsValid && TrainingScratchRangeBytes > 0UL;
}

internal sealed class SimpleDdgiGuidingNativeAllocation
{
    public SimpleDdgiGuidingNativeAllocation(
        SimpleDdgiGuidingVulkanBuffers buffers,
        BufferHandle[] headerReadbacks,
        BufferHandle[] sampleValidationReadbacks,
        BufferCopy[] headerCopies,
        bool ownsTrainingScratch)
    {
        Buffers = buffers;
        HeaderReadbacks = headerReadbacks;
        SampleValidationReadbacks = sampleValidationReadbacks;
        HeaderCopies = headerCopies;
        OwnsTrainingScratch = ownsTrainingScratch;
    }

    public SimpleDdgiGuidingVulkanBuffers Buffers { get; }
    public BufferHandle[] HeaderReadbacks { get; }
    public BufferHandle[] SampleValidationReadbacks { get; }
    public BufferCopy[] HeaderCopies { get; }
    public bool OwnsTrainingScratch { get; }
}
