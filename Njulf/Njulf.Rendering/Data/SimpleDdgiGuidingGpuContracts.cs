using System;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

/// <summary>
/// ABI constants shared by the C3 CPU upload path and the standalone guiding
/// compute programs.  This is deliberately separate from the managed oracle:
/// a capture/readback must carry a byte-level revision before it can be
/// interpreted as a learned distribution.
/// </summary>
public static class SimpleDdgiGuidingGpuAbi
{
    /// <summary>
    /// Increment when any field order, bit assignment, quantization rule, or
    /// stable sampling payload meaning changes.  The value is written into
    /// every persistent distribution header and every sampled-ray payload.
    /// </summary>
    public const uint Version = 0x4333_0006u;

    public const uint HeaderWordCount = 8u;
    public const uint HeaderByteCount = HeaderWordCount * sizeof(uint);
    public const uint TrainingRecordByteCount = 40u;
    public const uint TrainingWorkItemByteCount = 56u;
    public const uint BuildWorkItemByteCount = 48u;
    public const uint SampleRequestByteCount = 56u;
    public const uint SamplePayloadByteCount = 64u;
    public const uint PushConstantByteCount = 48u;
    public const uint ExtractPushConstantByteCount = 48u;
    public const uint ValidationCounterWordCount = 32u;
    public const uint ValidationCounterByteCount =
        ValidationCounterWordCount * sizeof(uint);

    public const uint CounterInvalidRecords = 0u;
    public const uint CounterInvalidHeaders = 1u;
    public const uint CounterInvalidPdfs = 2u;
    public const uint CounterPublicationRejections = 3u;
    public const uint CounterValidSamples = 4u;
    public const uint CounterMaintenanceSamples = 5u;
    public const uint CounterMixtureUniformSamples = 6u;
    public const uint CounterMixtureGuidedSamples = 7u;
    public const uint CounterUniformFallbackSamples = 8u;
    public const uint CounterMaximumInversePdfBits = 9u;
    public const uint CounterMaximumPdfBits = 10u;
    public const uint CounterInversePdfHistogramBase = 12u;
    public const uint InversePdfHistogramBinCount = 16u;

    public const uint MinimumLeafResolution = 4u;
    public const uint MaximumLeafResolution = 16u;
    public const uint MinimumUniformFractionBits = 0x3dcccccdU; // 0.1f
    public const uint KnownDistributionFlagMask =
        (uint)(SimpleDdgiGuidingGpuDistributionFlags.UniformFallback |
            SimpleDdgiGuidingGpuDistributionFlags.BuildComplete |
            SimpleDdgiGuidingGpuDistributionFlags.ValidationReference |
            SimpleDdgiGuidingGpuDistributionFlags.Invalid);

    public static void VerifyManagedLayout()
    {
        Verify<GPUSimpleDdgiGuidingDistributionHeader>(
            HeaderByteCount,
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.AbiVersion), 0),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.VirtualProbeId), 4),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.PageGeneration), 8),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.DistributionGeneration), 12),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.DirectionProposalEpoch), 16),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.SampleCountAndAge), 20),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.TotalIncidentEnergy), 24),
            (nameof(GPUSimpleDdgiGuidingDistributionHeader.PackedLeafResolutionAndFlags), 28));
        Verify<GPUSimpleDdgiGuidingTrainingRecord>(
            TrainingRecordByteCount,
            (nameof(GPUSimpleDdgiGuidingTrainingRecord.PhysicalProbeIndex), 0),
            (nameof(GPUSimpleDdgiGuidingTrainingRecord.SamplePdf), 24),
            (nameof(GPUSimpleDdgiGuidingTrainingRecord.IncidentLuminance), 28),
            (nameof(GPUSimpleDdgiGuidingTrainingRecord.ContentRevision), 32));
        Verify<GPUSimpleDdgiGuidingTrainingWorkItem>(
            TrainingWorkItemByteCount,
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.PhysicalProbeIndex), 0),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.RecordOffset), 20),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.PartialOffset), 28),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.QueueOffset), 36),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.RayResultBaseIndex), 40),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.DirectionSlotsPerProbe), 44),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.SourceEpoch), 48),
            (nameof(GPUSimpleDdgiGuidingTrainingWorkItem.SourceLightingGeneration), 52));
        Verify<GPUSimpleDdgiGuidingBuildWorkItem>(
            BuildWorkItemByteCount,
            (nameof(GPUSimpleDdgiGuidingBuildWorkItem.PhysicalProbeIndex), 0),
            (nameof(GPUSimpleDdgiGuidingBuildWorkItem.TargetDistributionGeneration), 16),
            (nameof(GPUSimpleDdgiGuidingBuildWorkItem.PartialOffset), 24));
        Verify<GPUSimpleDdgiGuidingSampleRequest>(
            SampleRequestByteCount,
            (nameof(GPUSimpleDdgiGuidingSampleRequest.PhysicalProbeIndex), 0),
            (nameof(GPUSimpleDdgiGuidingSampleRequest.StableProbeIdLow), 20),
            (nameof(GPUSimpleDdgiGuidingSampleRequest.RequestedUniformFraction), 40));
        Verify<GPUSimpleDdgiGuidingSamplePayload>(
            SamplePayloadByteCount,
            (nameof(GPUSimpleDdgiGuidingSamplePayload.AbiVersion), 0),
            (nameof(GPUSimpleDdgiGuidingSamplePayload.StableProbeIdLow), 4),
            (nameof(GPUSimpleDdgiGuidingSamplePayload.DistributionGeneration), 24),
            (nameof(GPUSimpleDdgiGuidingSamplePayload.GenerationTimePdfBits), 52));
        Verify<GPUSimpleDdgiGuidingPushConstants>(
            PushConstantByteCount,
            (nameof(GPUSimpleDdgiGuidingPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiGuidingPushConstants.PhysicalProbeCapacity), 4),
            (nameof(GPUSimpleDdgiGuidingPushConstants.BankStrideWords), 16),
            (nameof(GPUSimpleDdgiGuidingPushConstants.DispatchCount), 20),
            (nameof(GPUSimpleDdgiGuidingPushConstants.TargetProposalEpoch), 36));
        Verify<GPUSimpleDdgiGuidingExtractPushConstants>(
            ExtractPushConstantByteCount,
            (nameof(GPUSimpleDdgiGuidingExtractPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiGuidingExtractPushConstants.RayResultScratchBufferIndex), 8),
            (nameof(GPUSimpleDdgiGuidingExtractPushConstants.TrainingWorkItemCount), 16),
            (nameof(GPUSimpleDdgiGuidingExtractPushConstants.RayResultCapacity), 28),
            (nameof(GPUSimpleDdgiGuidingExtractPushConstants.DirectionSlotsPerProbe), 40));
    }

    public static bool IsSupportedLeafResolution(uint leafResolution) =>
        leafResolution is 4u or 8u or 16u;

    public static uint GetLeafCount(uint leafResolution)
    {
        if (!IsSupportedLeafResolution(leafResolution))
            throw new ArgumentOutOfRangeException(nameof(leafResolution));
        return checked(leafResolution * leafResolution);
    }

    public static uint GetHierarchyWeightCount(uint leafResolution)
    {
        if (!IsSupportedLeafResolution(leafResolution))
            throw new ArgumentOutOfRangeException(nameof(leafResolution));
        uint count = 0u;
        for (uint side = leafResolution;; side >>= 1)
        {
            count = checked(count + checked(side * side));
            if (side == 1u)
                return count;
        }
    }

    public static uint GetPackedHierarchyWordCount(uint leafResolution) =>
        checked((GetHierarchyWeightCount(leafResolution) + 1u) / 2u);

    public static uint PackLeafResolutionAndFlags(
        uint leafResolution,
        SimpleDdgiGuidingGpuDistributionFlags flags)
    {
        if (!IsSupportedLeafResolution(leafResolution))
            throw new ArgumentOutOfRangeException(nameof(leafResolution));
        if (((uint)flags & 0xffu) != 0u ||
            ((uint)flags & ~KnownDistributionFlagMask) != 0u)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        return leafResolution | (uint)flags;
    }

    public static uint GetLeafResolution(uint packedLeafResolutionAndFlags) =>
        packedLeafResolutionAndFlags & 0xffu;

    public static SimpleDdgiGuidingGpuDistributionFlags GetDistributionFlags(
        uint packedLeafResolutionAndFlags) =>
        (SimpleDdgiGuidingGpuDistributionFlags)(packedLeafResolutionAndFlags &
            ~0xffu);

    private static void Verify<T>(
        uint expectedSize,
        params (string Field, int Offset)[] expectedOffsets)
        where T : struct
    {
        if (Marshal.SizeOf<T>() != expectedSize)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is {Marshal.SizeOf<T>()} bytes; expected {expectedSize}.");
        }

        foreach ((string field, int expectedOffset) in expectedOffsets)
        {
            int actualOffset = checked((int)Marshal.OffsetOf<T>(field));
            if (actualOffset != expectedOffset)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{field} is at byte {actualOffset}; " +
                    $"expected {expectedOffset}.");
            }
        }
    }
}

/// <summary>
/// Header flags use the upper 24 bits; the low eight bits of the packed field
/// are the persisted equal-area leaf resolution.  A header is readable only
/// when <see cref="BuildComplete"/> is set after all hierarchy words have been
/// written and validated.
/// </summary>
[Flags]
public enum SimpleDdgiGuidingGpuDistributionFlags : uint
{
    None = 0u,
    UniformFallback = 1u << 8,
    BuildComplete = 1u << 9,
    ValidationReference = 1u << 10,
    Invalid = 1u << 11
}

[Flags]
public enum SimpleDdgiGuidingTrainingRecordFlags : uint
{
    None = 0u,
    FiniteIncidentRadiance = 1u << 0,
    ContentRevisionMatched = 1u << 1,
    FromRadiometricTransport = 1u << 2
}

[Flags]
public enum SimpleDdgiGuidingSamplePayloadFlags : uint
{
    None = 0u,
    UniformMaintenance = 1u << 0,
    MixtureUniformBranch = 1u << 1,
    MixtureGuidedBranch = 1u << 2,
    UniformFallback = 1u << 3,
    InvalidDistribution = 1u << 4
}

/// <summary>
/// Fixed 32-byte persistent distribution header.  It is intentionally a
/// blittable upload/readback payload rather than a C# record.  A physical slot
/// can be reused only when all virtual/page/generation fields match.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct GPUSimpleDdgiGuidingDistributionHeader
{
    public uint AbiVersion;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint DistributionGeneration;
    public uint DirectionProposalEpoch;
    public uint SampleCountAndAge;
    public float TotalIncidentEnergy;
    public uint PackedLeafResolutionAndFlags;

    public readonly uint LeafResolution =>
        SimpleDdgiGuidingGpuAbi.GetLeafResolution(PackedLeafResolutionAndFlags);

    public readonly SimpleDdgiGuidingGpuDistributionFlags Flags =>
        SimpleDdgiGuidingGpuAbi.GetDistributionFlags(PackedLeafResolutionAndFlags);

    public readonly bool IsBuildComplete =>
        (Flags & SimpleDdgiGuidingGpuDistributionFlags.BuildComplete) !=
        SimpleDdgiGuidingGpuDistributionFlags.None;

    public readonly bool IsUniformFallback =>
        (Flags & SimpleDdgiGuidingGpuDistributionFlags.UniformFallback) !=
        SimpleDdgiGuidingGpuDistributionFlags.None;
}

/// <summary>
/// One source-radiance observation.  The train pass deposits it exactly once
/// as max(luminance, 0) / SamplePdf; it is never fed a temporally blended atlas
/// result.  The source distribution identity prevents a stale slot from
/// training a newly assigned physical probe.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 40)]
public struct GPUSimpleDdgiGuidingTrainingRecord
{
    public uint PhysicalProbeIndex;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint SourceDistributionGeneration;
    public uint DirectionProposalEpoch;
    public uint LeafIndex;
    public float SamplePdf;
    public float IncidentLuminance;
    public uint ContentRevision;
    public SimpleDdgiGuidingTrainingRecordFlags Flags;
}

/// <summary>
/// Workgroup-owned range of training observations.  One workgroup writes one
/// non-overlapping partial histogram, avoiding global floating-point atomics
/// on every ray.  The build pass reduces partials in ascending order.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 56)]
public struct GPUSimpleDdgiGuidingTrainingWorkItem
{
    public uint PhysicalProbeIndex;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint SourceDistributionGeneration;
    public uint DirectionProposalEpoch;
    public uint RecordOffset;
    public uint RecordCount;
    public uint PartialOffset;
    public uint ExpectedContentRevision;
    // Queue/scratch provenance used by the production trace-result extractor.
    // The standalone training shader ignores these fields after the extractor
    // has materialized its exact 40-byte records.
    public uint QueueOffset;
    public uint RayResultBaseIndex;
    public uint DirectionSlotsPerProbe;
    // Immutable trace-result provenance. The extractor rejects a recycled
    // queue slot rather than relabeling stale radiance as current evidence.
    public uint SourceEpoch;
    public uint SourceLightingGeneration;
}

/// <summary>
/// Build command for a physical probe.  The output header is written only
/// after all packed FP16 hierarchy weights are visible.  The renderer must
/// still wait for command completion and ask the lifecycle manager to publish
/// the bank transactionally.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiGuidingBuildWorkItem
{
    public uint PhysicalProbeIndex;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint PreviousDistributionGeneration;
    public uint TargetDistributionGeneration;
    public uint TargetProposalEpoch;
    public uint PartialOffset;
    public uint PartialCount;
    public uint SampleCountAndAge;
    public uint ExpectedContentRevision;
    public uint Flags;
    public uint Reserved;
}

/// <summary>
/// Stable sampling request.  Random values are raw deterministic bits instead
/// of transient dispatch indices, so a sort/compaction change cannot change a
/// ray direction.  The shader rejects a mismatched bank and marks the output
/// invalid for the caller's ordinary uniform fallback path.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 56)]
public struct GPUSimpleDdgiGuidingSampleRequest
{
    public uint PhysicalProbeIndex;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint ExpectedDistributionGeneration;
    public uint ExpectedProposalEpoch;
    public uint StableProbeIdLow;
    public uint StableProbeIdHigh;
    public uint SlotIndex;
    public uint Technique;
    public uint RandomBranchBits;
    public float RequestedUniformFraction;
    public uint RandomIntraLeafUBits;
    public uint RandomIntraLeafVBits;
    public uint Reserved;
}

/// <summary>
/// Versioned direction/PDF sidecar consumed by trace and source-cache code.
/// <see cref="GenerationTimePdfBits"/> is the authoritative generation-time
/// mixture density at this direction. It must never be recomputed from a
/// current guide during cached re-lighting. For maintenance samples the own
/// proposal remains the analytic uniform density; storing the mixture density
/// is what makes the two-technique balance denominator reconstructable.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
public struct GPUSimpleDdgiGuidingSamplePayload
{
    public uint AbiVersion;
    public uint StableProbeIdLow;
    public uint StableProbeIdHigh;
    public uint PhysicalProbeIndex;
    public uint VirtualProbeId;
    public uint PageGeneration;
    public uint DistributionGeneration;
    public uint DirectionProposalEpoch;
    public uint SlotIndex;
    public uint TechniqueAndBranch;
    public uint LeafIndex;
    public uint IntraLeafSampleBits;
    public uint PackedDirectionOct32;
    public uint GenerationTimePdfBits;
    public SimpleDdgiGuidingSamplePayloadFlags Flags;
    public uint Reserved;
}

/// <summary>
/// Shared 48-byte push block for the C3 train/build/sample/validate programs.
/// The selected source/destination bank is bound explicitly by the pass; bank
/// indices remain in the payload for diagnostics and stale-command detection.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiGuidingPushConstants
{
    public uint AbiVersion;
    public uint PhysicalProbeCapacity;
    public uint LeafResolution;
    public uint HierarchyWeightCount;
    public uint BankStrideWords;
    public uint DispatchCount;
    public uint ReadBankIndex;
    public uint WriteBankIndex;
    public uint TargetDistributionGeneration;
    public uint TargetProposalEpoch;
    public uint Flags;
    public uint Reserved;
}

/// <summary>
/// Frozen push block for the production trace-result extractor. Buffer
/// indices address the global bindless heap; independent capacities make
/// malformed work items rejectable before any source load.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiGuidingExtractPushConstants
{
    public uint AbiVersion;
    public uint ParamsBufferIndex;
    public uint RayResultScratchBufferIndex;
    public uint ProbeUpdateQueueBufferIndex;
    public uint TrainingWorkItemCount;
    public uint TrainingRecordCapacity;
    public uint ProbeUpdateCapacity;
    public uint RayResultCapacity;
    public uint PhysicalProbeCapacity;
    public uint LeafResolution;
    public uint DirectionSlotsPerProbe;
    public uint Flags;
}

/// <summary>Validation result used before a GPU header becomes readable.</summary>
public readonly record struct SimpleDdgiGuidingGpuHeaderValidation(
    bool IsValid,
    string Reason)
{
    public static SimpleDdgiGuidingGpuHeaderValidation Valid { get; } =
        new(true, "valid");
}

public static class SimpleDdgiGuidingGpuHeaderValidator
{
    public static SimpleDdgiGuidingGpuHeaderValidation Validate(
        in GPUSimpleDdgiGuidingDistributionHeader header,
        uint expectedVirtualProbeId,
        uint expectedPageGeneration,
        uint expectedProposalEpoch)
    {
        if (header.AbiVersion != SimpleDdgiGuidingGpuAbi.Version)
            return Invalid("guiding-distribution-abi-mismatch");
        if (header.VirtualProbeId != expectedVirtualProbeId)
            return Invalid("guiding-virtual-probe-id-mismatch");
        if (header.PageGeneration != expectedPageGeneration)
            return Invalid("guiding-page-generation-mismatch");
        if (header.DistributionGeneration == 0u)
            return Invalid("guiding-distribution-generation-missing");
        if (header.DirectionProposalEpoch != expectedProposalEpoch ||
            header.DirectionProposalEpoch == 0u)
        {
            return Invalid("guiding-proposal-epoch-mismatch");
        }
        if (!SimpleDdgiGuidingGpuAbi.IsSupportedLeafResolution(
                header.LeafResolution))
        {
            return Invalid("guiding-leaf-resolution-unsupported");
        }
        if (((uint)header.Flags & ~SimpleDdgiGuidingGpuAbi.KnownDistributionFlagMask) != 0u)
            return Invalid("guiding-distribution-header-has-unknown-flags");
        if (!float.IsFinite(header.TotalIncidentEnergy) ||
            header.TotalIncidentEnergy < 0.0f)
        {
            return Invalid("guiding-total-incident-energy-invalid");
        }
        if (!header.IsBuildComplete ||
            (header.Flags & SimpleDdgiGuidingGpuDistributionFlags.Invalid) !=
            SimpleDdgiGuidingGpuDistributionFlags.None)
        {
            return Invalid("guiding-bank-not-complete-or-invalid");
        }
        return SimpleDdgiGuidingGpuHeaderValidation.Valid;
    }

    private static SimpleDdgiGuidingGpuHeaderValidation Invalid(string reason) =>
        new(false, reason);
}
