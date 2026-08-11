using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Explicit C3 memory request. The two persistent FP16 banks are always
/// planned together; the FP32 reference mirror is validation-only and cannot
/// become an accidental shipping allocation.
/// </summary>
public readonly record struct SimpleDdgiGuidingLayoutRequest(
    SimpleDdgiGuidingDistributionConfiguration Distribution,
    int PhysicalProbeCapacity,
    int ScheduledGuidedProbeCapacity,
    ulong StorageAlignmentBytes,
    bool AllocateValidationReferenceBank)
{
    public const int PersistentBankCount = 2;

    /// <summary>
    /// Persistent source-cache direction/PDF identities per admitted physical
    /// probe. Zero preserves the standalone train/build-only layout used by
    /// oracle and ABI tests. A production transport layout supplies the exact
    /// source-ray cardinality and must also supply a nonzero sidecar budget.
    /// </summary>
    public int DirectionSlotsPerProbe { get; init; }

    /// <summary>
    /// Hard budget for the source-cache-owned slot-203 sidecar. The compiler
    /// never silently truncates this allocation because doing so would make
    /// the distribution and direction-identity physical domains disagree.
    /// </summary>
    public ulong DirectionPdfSidecarBudgetBytes { get; init; }
}

/// <summary>
/// Checked byte accounting for the C3 persistent hierarchy, validation mirror,
/// and per-scheduled-probe FP32 training scratch.
/// </summary>
public readonly record struct SimpleDdgiGuidingLayout(
    uint AbiVersion,
    int LeafResolution,
    int LeafCount,
    int HierarchyWeightCount,
    int PhysicalProbeCapacity,
    int ScheduledGuidedProbeCapacity,
    ulong StorageAlignmentBytes,
    ulong HeaderBytes,
    ulong PersistentWeightBytesPerBank,
    ulong PersistentBankUnalignedBytes,
    ulong PersistentBankStrideBytes,
    int PersistentBankCount,
    ulong PersistentDoubleBufferedBytes,
    bool ValidationReferenceAllocated,
    ulong ValidationReferenceWeightBytesPerBank,
    ulong ValidationReferenceBankUnalignedBytes,
    ulong ValidationReferenceBankStrideBytes,
    ulong ValidationReferenceBankBytes,
    ulong TrainingScratchBytes,
    int DirectionSlotsPerProbe,
    uint DirectionPayloadCapacity,
    ulong DirectionPdfSidecarBytes,
    ulong ManagerOwnedBytes,
    ulong TotalBytes)
{
    /// <summary>
    /// Exact device-local workspace for one serialized C3 transaction.  The
    /// ranges are deliberately packed into the central advanced-GI scratch
    /// arena; none is a persistent bindless allocation.
    /// </summary>
    public SimpleDdgiGuidingTransientWorkspace TransientWorkspace { get; init; }

    public bool HasAllocation => TotalBytes != 0UL;

    public bool HasTransportSidecar => DirectionSlotsPerProbe > 0 &&
        DirectionPayloadCapacity > 0u && DirectionPdfSidecarBytes > 0UL;
}

/// <summary>
/// Aligned suballocation map for the complete C3 build/sample workspace.
/// Transactions are serialized by the runtime, so one copy is sufficient and
/// no frames-in-flight multiplier is hidden in the memory plan.
/// </summary>
public readonly record struct SimpleDdgiGuidingTransientWorkspace(
    ulong TrainingScratchOffsetBytes,
    ulong TrainingScratchBytes,
    ulong TrainingRecordsOffsetBytes,
    ulong TrainingRecordsBytes,
    ulong TrainingWorkItemsOffsetBytes,
    ulong TrainingWorkItemsBytes,
    ulong BuildWorkItemsOffsetBytes,
    ulong BuildWorkItemsBytes,
    ulong SampleRequestsOffsetBytes,
    ulong SampleRequestsBytes,
    ulong ValidationCountersOffsetBytes,
    ulong ValidationCountersBytes,
    ulong TotalBytes)
{
    public bool IsComplete => TrainingScratchBytes > 0UL &&
        TrainingRecordsBytes > 0UL && TrainingWorkItemsBytes > 0UL &&
        BuildWorkItemsBytes > 0UL && SampleRequestsBytes > 0UL &&
        ValidationCountersBytes ==
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount &&
        TotalBytes > 0UL;
}

/// <summary>
/// Pure layout compiler for C3. It is deliberately independent of renderer
/// allocations so requests can be budgeted, recorded, and fuzzed before a
/// manager attempts a transactional GPU transition.
/// </summary>
public static class SimpleDdgiGuidingLayoutCompiler
{
    public const uint AbiVersion = SimpleDdgiGuidingGpuAbi.Version;
    public const ulong PersistentWeightBytes = sizeof(ushort);
    public const ulong ValidationReferenceWeightBytes = sizeof(float);
    public const ulong TrainingScratchValueBytes = sizeof(float);

    public static SimpleDdgiGuidingLayout Compile(
        in SimpleDdgiGuidingLayoutRequest request)
    {
        request.Distribution.Validate();
        if (request.PhysicalProbeCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(request.PhysicalProbeCapacity));
        if (request.ScheduledGuidedProbeCapacity < 0 ||
            request.ScheduledGuidedProbeCapacity > request.PhysicalProbeCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ScheduledGuidedProbeCapacity));
        }
        if (!IsPowerOfTwo(request.StorageAlignmentBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(request.StorageAlignmentBytes),
                "Storage alignment must be a nonzero power of two.");
        }
        // The persistent shader ABI addresses every header/hierarchy bank as
        // uint words.  Permitting a byte-only alignment here could create a
        // mathematically valid CPU plan whose stride cannot be represented by
        // GPUSimpleDdgiGuidingPushConstants.BankStrideWords.
        if (request.StorageAlignmentBytes < sizeof(uint) ||
            request.StorageAlignmentBytes % sizeof(uint) != 0UL)
        {
            throw new ArgumentOutOfRangeException(nameof(request.StorageAlignmentBytes),
                "C3 persistent storage alignment must preserve uint-word addressing.");
        }
        if (request.DirectionSlotsPerProbe < 0)
            throw new ArgumentOutOfRangeException(nameof(request.DirectionSlotsPerProbe));
        if (request.DirectionSlotsPerProbe > 0 &&
            request.DirectionPdfSidecarBudgetBytes == 0UL)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.DirectionPdfSidecarBudgetBytes),
                "A production C3 transport layout requires an explicit nonzero sidecar budget.");
        }

        int weightCount = request.Distribution.HierarchyWeightCount;
        ulong headerBytes = SimpleDdgiGuidingDistributionHeader.ByteSize;
        ulong persistentWeights = checked((ulong)weightCount * PersistentWeightBytes);
        ulong persistentUnaligned = checked(headerBytes + persistentWeights);
        ulong persistentStride = AlignUp(persistentUnaligned,
            request.StorageAlignmentBytes);
        ulong persistentBytes = checked(
            checked(persistentStride * (ulong)request.PhysicalProbeCapacity) *
            (ulong)SimpleDdgiGuidingLayoutRequest.PersistentBankCount);

        ulong referenceWeights = checked((ulong)weightCount *
            ValidationReferenceWeightBytes);
        ulong referenceUnaligned = checked(headerBytes + referenceWeights);
        ulong referenceStride = AlignUp(referenceUnaligned,
            request.StorageAlignmentBytes);
        ulong referenceBytes = request.AllocateValidationReferenceBank
            ? checked(referenceStride * (ulong)request.PhysicalProbeCapacity)
            : 0UL;

        // Training deposits one FP32 accumulator per leaf. Parent nodes are
        // built deterministically from those leaves and are not separately
        // charged to the update scratch allocation.
        ulong trainingScratch = checked(
            checked((ulong)request.ScheduledGuidedProbeCapacity *
                (ulong)request.Distribution.LeafCount) *
            TrainingScratchValueBytes);
        SimpleDdgiGuidingTransientWorkspace transientWorkspace =
            CompileTransientWorkspace(
                request.ScheduledGuidedProbeCapacity,
                request.DirectionSlotsPerProbe,
                trainingScratch,
                request.StorageAlignmentBytes);
        ulong directionPayloadCapacity64 = checked(
            (ulong)request.PhysicalProbeCapacity *
            (ulong)request.DirectionSlotsPerProbe);
        if (directionPayloadCapacity64 > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.DirectionSlotsPerProbe),
                "The C3 direction/PDF payload capacity exceeds the frozen uint addressing ABI.");
        }
        ulong sidecarBytes = checked(
            directionPayloadCapacity64 *
            SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
        if (sidecarBytes > request.DirectionPdfSidecarBudgetBytes &&
            request.DirectionSlotsPerProbe > 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.DirectionPdfSidecarBudgetBytes),
                $"The exact C3 direction/PDF sidecar requires {sidecarBytes} bytes, " +
                $"exceeding its {request.DirectionPdfSidecarBudgetBytes}-byte budget.");
        }
        // Only the persistent distribution/validation banks are allocated and
        // retired by the guiding manager. Training scratch is a subrange of
        // the central advanced-GI arena and must not be charged a second time
        // as manager-owned residency.
        ulong managerOwnedBytes = checked(
            persistentBytes + referenceBytes);
        ulong total = checked(
            persistentBytes + referenceBytes + sidecarBytes +
            transientWorkspace.TotalBytes);

        return new SimpleDdgiGuidingLayout(
            AbiVersion,
            request.Distribution.LeafResolution,
            request.Distribution.LeafCount,
            weightCount,
            request.PhysicalProbeCapacity,
            request.ScheduledGuidedProbeCapacity,
            request.StorageAlignmentBytes,
            headerBytes,
            persistentWeights,
            persistentUnaligned,
            persistentStride,
            SimpleDdgiGuidingLayoutRequest.PersistentBankCount,
            persistentBytes,
            request.AllocateValidationReferenceBank,
            referenceWeights,
            referenceUnaligned,
            referenceStride,
            referenceBytes,
            trainingScratch,
            request.DirectionSlotsPerProbe,
            checked((uint)directionPayloadCapacity64),
            sidecarBytes,
            managerOwnedBytes,
            total)
        {
            TransientWorkspace = transientWorkspace
        };
    }

    private static SimpleDdgiGuidingTransientWorkspace CompileTransientWorkspace(
        int scheduledProbeCapacity,
        int directionSlotsPerProbe,
        ulong trainingScratchBytes,
        ulong alignment)
    {
        if (scheduledProbeCapacity == 0 || directionSlotsPerProbe == 0)
        {
            return new SimpleDdgiGuidingTransientWorkspace(
                0UL,
                trainingScratchBytes,
                trainingScratchBytes,
                0UL,
                trainingScratchBytes,
                0UL,
                trainingScratchBytes,
                0UL,
                trainingScratchBytes,
                0UL,
                trainingScratchBytes,
                0UL,
                trainingScratchBytes);
        }

        ulong scheduled = checked((ulong)scheduledProbeCapacity);
        ulong slots = checked((ulong)directionSlotsPerProbe);
        ulong maximumRecordCount = checked(scheduled * slots);
        ulong trainingRecordsBytes = checked(
            maximumRecordCount * SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount);
        ulong trainingWorkItemsBytes = checked(
            scheduled * SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount);
        ulong buildWorkItemsBytes = checked(
            scheduled * SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount);
        ulong sampleRequestsBytes = checked(
            maximumRecordCount * SimpleDdgiGuidingGpuAbi.SampleRequestByteCount);
        const ulong validationCountersBytes =
            SimpleDdgiGuidingGpuAbi.ValidationCounterByteCount;

        ulong trainingScratchOffset = 0UL;
        ulong trainingRecordsOffset = AlignUp(
            checked(trainingScratchOffset + trainingScratchBytes), alignment);
        ulong trainingWorkItemsOffset = AlignUp(
            checked(trainingRecordsOffset + trainingRecordsBytes), alignment);
        ulong buildWorkItemsOffset = AlignUp(
            checked(trainingWorkItemsOffset + trainingWorkItemsBytes), alignment);
        ulong sampleRequestsOffset = AlignUp(
            checked(buildWorkItemsOffset + buildWorkItemsBytes), alignment);
        ulong validationCountersOffset = AlignUp(
            checked(sampleRequestsOffset + sampleRequestsBytes), alignment);
        ulong totalBytes = AlignUp(
            checked(validationCountersOffset + validationCountersBytes), alignment);

        return new SimpleDdgiGuidingTransientWorkspace(
            trainingScratchOffset,
            trainingScratchBytes,
            trainingRecordsOffset,
            trainingRecordsBytes,
            trainingWorkItemsOffset,
            trainingWorkItemsBytes,
            buildWorkItemsOffset,
            buildWorkItemsBytes,
            sampleRequestsOffset,
            sampleRequestsBytes,
            validationCountersOffset,
            validationCountersBytes,
            totalBytes);
    }

    public static ulong AlignUp(ulong value, ulong alignment)
    {
        if (!IsPowerOfTwo(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment));
        ulong remainder = value & (alignment - 1UL);
        return remainder == 0UL
            ? value
            : checked(value + alignment - remainder);
    }

    private static bool IsPowerOfTwo(ulong value) =>
        value != 0UL && (value & (value - 1UL)) == 0UL;
}
