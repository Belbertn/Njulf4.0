using System;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

/// <summary>Producer namespaces carried into exact receiver feedback records.</summary>
public enum SimpleDdgiReceiverFeedbackProducer : uint
{
    OpaqueForward = 0,
    AlphaMaskOrFoliage = 1,
    TransparentWeightedOit = 2,
    Particles = 3,
    Fog = 4,
    ReflectionCapture = 5,
    RefinementOrBaseFallback = 6
}

/// <summary>
/// Describes which owner a feedback record represents.  Requested and resolved
/// IDs are always carried independently; this field is only scheduler bias and
/// diagnostics, never an ownership gate.
/// </summary>
public enum SimpleDdgiReceiverFeedbackFallbackRole : uint
{
    ResolvedOwner = 0,
    RequestedFineOwnerFallback = 1,
    RefinementToBaseFallback = 2,
    UnavailablePageFallback = 3
}

[Flags]
public enum SimpleDdgiReceiverFeedbackSummaryStatus : uint
{
    None = 0,
    Validated = 1u << 0,
    FallbackCountOverflow = 1u << 1,
    NonFiniteInputRejected = 1u << 2,
    GenerationMismatch = 1u << 3,
    ProducerRangeOverflow = 1u << 4,
    AppendOverflow = 1u << 5
}

/// <summary>
/// Frozen initial V2 append ABI.  Its 32-byte size and every member offset are
/// verified by CPU tests before a shader mirror may consume the layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverContributionRecordV2
{
    public uint RequestedVirtualProbeId;
    public uint ResolvedVirtualProbeId;
    public uint ResolvedVirtualPageId;
    public uint ExactTileId;
    public float InterpolationWeight;
    public float InverseInclusionProbability;
    public uint PackedConsumerFallbackAndPageGeneration;
    public uint FeedbackGeneration;
}

/// <summary>Frozen initial V2 resolved-probe feedback summary ABI.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverContributionSummaryV2
{
    public float EstimatedContributionMass;
    public float MaximumSingleReceiverWeight;
    public uint ExactUniqueTileCount;
    public uint SampledReceiverCount;
    public uint ConsumerMask;
    public uint PackedFallbackCounts;
    public uint FeedbackGeneration;
    public uint StatusFlags;
}

/// <summary>
/// Managed half of the V2 ABI.  Packing returns false instead of truncating a
/// page generation: callers must reject V2 or move to a wider ABI.
/// </summary>
public static class SimpleDdgiReceiverFeedbackV2Abi
{
    public const uint LayoutRevision = 0xB101_0002u;
    public const uint EndianSentinel = 0x0102_0304u;
    public const int RecordStrideBytes = 32;
    public const int SummaryStrideBytes = 32;
    public const int ConsumerBits = 4;
    public const int FallbackRoleBits = 4;
    public const int PageGenerationShift = ConsumerBits + FallbackRoleBits;
    public const uint ConsumerMask = (1u << ConsumerBits) - 1u;
    public const uint FallbackRoleMask =
        ((1u << FallbackRoleBits) - 1u) << ConsumerBits;
    public const uint PageGenerationMask = 0x00ff_ffffu;
    public const int RequestedFallbackCountShift = 0;
    public const int ResolvedFallbackCountShift = 16;
    public const uint PackedFallbackCountMask = 0xffffu;

    /// <summary>
    /// Zero is reserved for an unowned/unpublished page.  A feedback record
    /// must carry a real publication generation so sparse-slot reuse cannot
    /// be mistaken for a compatible owner.
    /// </summary>
    public static bool CanRepresentPageGeneration(uint pageGeneration) =>
        pageGeneration != 0u && pageGeneration <= PageGenerationMask;

    public static bool TryPackConsumerFallbackAndPageGeneration(
        SimpleDdgiReceiverFeedbackProducer producer,
        SimpleDdgiReceiverFeedbackFallbackRole fallbackRole,
        uint pageGeneration,
        out uint packed)
    {
        packed = 0u;
        if (!Enum.IsDefined(producer) || !Enum.IsDefined(fallbackRole) ||
            !CanRepresentPageGeneration(pageGeneration))
        {
            return false;
        }

        uint producerBits = (uint)producer;
        uint fallbackBits = (uint)fallbackRole;
        if (producerBits > ConsumerMask ||
            fallbackBits > (FallbackRoleMask >> ConsumerBits))
        {
            return false;
        }

        packed = producerBits |
            (fallbackBits << ConsumerBits) |
            (pageGeneration << PageGenerationShift);
        return true;
    }

    public static SimpleDdgiReceiverFeedbackProducer UnpackProducer(uint packed) =>
        (SimpleDdgiReceiverFeedbackProducer)(packed & ConsumerMask);

    public static SimpleDdgiReceiverFeedbackFallbackRole UnpackFallbackRole(
        uint packed) => (SimpleDdgiReceiverFeedbackFallbackRole)(
            (packed & FallbackRoleMask) >> ConsumerBits);

    public static uint UnpackPageGeneration(uint packed) =>
        packed >> PageGenerationShift;

    public static bool TryPackFallbackCounts(
        uint requestedFallbackCount,
        uint resolvedFallbackCount,
        out uint packed)
    {
        packed = 0u;
        if (requestedFallbackCount > PackedFallbackCountMask ||
            resolvedFallbackCount > PackedFallbackCountMask)
        {
            return false;
        }

        packed = (requestedFallbackCount << RequestedFallbackCountShift) |
            (resolvedFallbackCount << ResolvedFallbackCountShift);
        return true;
    }

    public static uint UnpackRequestedFallbackCount(uint packed) =>
        packed & PackedFallbackCountMask;

    public static uint UnpackResolvedFallbackCount(uint packed) =>
        packed >> ResolvedFallbackCountShift;

    public static void AssertManagedLayout()
    {
        AssertLittleEndianSentinel();
        AssertSize<GPUSimpleDdgiReceiverContributionRecordV2>(RecordStrideBytes);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.RequestedVirtualProbeId), 0);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.ResolvedVirtualProbeId), 4);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.ResolvedVirtualPageId), 8);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.ExactTileId), 12);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.InterpolationWeight), 16);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.InverseInclusionProbability), 20);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.PackedConsumerFallbackAndPageGeneration), 24);
        AssertOffset<GPUSimpleDdgiReceiverContributionRecordV2>(
            nameof(GPUSimpleDdgiReceiverContributionRecordV2.FeedbackGeneration), 28);

        AssertSize<GPUSimpleDdgiReceiverContributionSummaryV2>(SummaryStrideBytes);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.EstimatedContributionMass), 0);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.MaximumSingleReceiverWeight), 4);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.ExactUniqueTileCount), 8);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.SampledReceiverCount), 12);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.ConsumerMask), 16);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.PackedFallbackCounts), 20);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.FeedbackGeneration), 24);
        AssertOffset<GPUSimpleDdgiReceiverContributionSummaryV2>(
            nameof(GPUSimpleDdgiReceiverContributionSummaryV2.StatusFlags), 28);
    }

    private static void AssertSize<T>(int expected)
        where T : struct
    {
        if (Marshal.SizeOf<T>() != expected)
            throw new InvalidOperationException(
                $"{typeof(T).Name} no longer matches the frozen V2 ABI.");
    }

    private static void AssertOffset<T>(string member, int expected)
        where T : struct
    {
        if (Marshal.OffsetOf<T>(member).ToInt32() != expected)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name}.{member} no longer matches the frozen V2 ABI.");
        }
    }

    private static void AssertLittleEndianSentinel()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        uint sentinel = EndianSentinel;
        MemoryMarshal.Write(bytes, in sentinel);
        if (!BitConverter.IsLittleEndian || bytes[0] != 0x04 ||
            bytes[1] != 0x03 || bytes[2] != 0x02 || bytes[3] != 0x01)
        {
            throw new PlatformNotSupportedException(
                "The V2 receiver-feedback ABI requires little-endian scalar layout.");
        }
    }
}

/// <summary>
/// Exact receiver-side values before their compact GPU representation.  The
/// requested owner remains present when a finer virtual probe falls back to a
/// resolved base probe.
/// </summary>
public readonly record struct SimpleDdgiReceiverContribution(
    uint RequestedVirtualProbeId,
    uint ResolvedVirtualProbeId,
    uint ResolvedVirtualPageId,
    uint ExactTileId,
    float InterpolationWeight,
    float InverseInclusionProbability,
    SimpleDdgiReceiverFeedbackProducer Producer,
    SimpleDdgiReceiverFeedbackFallbackRole FallbackRole,
    uint PagePublicationGeneration,
    uint FeedbackGeneration)
{
    public bool IsFallback => RequestedVirtualProbeId != ResolvedVirtualProbeId ||
        FallbackRole != SimpleDdgiReceiverFeedbackFallbackRole.ResolvedOwner;

    public bool TryCreateGpuRecord(
        out GPUSimpleDdgiReceiverContributionRecordV2 record)
    {
        record = default;
        if (!float.IsFinite(InterpolationWeight) ||
            InterpolationWeight < 0.0f || InterpolationWeight > 1.0f ||
            !float.IsFinite(InverseInclusionProbability) ||
            InverseInclusionProbability < 1.0f || FeedbackGeneration == 0u ||
            !SimpleDdgiReceiverFeedbackV2Abi
                .TryPackConsumerFallbackAndPageGeneration(
                    Producer,
                    FallbackRole,
                    PagePublicationGeneration,
                    out uint packed))
        {
            return false;
        }

        record = new GPUSimpleDdgiReceiverContributionRecordV2
        {
            RequestedVirtualProbeId = RequestedVirtualProbeId,
            ResolvedVirtualProbeId = ResolvedVirtualProbeId,
            ResolvedVirtualPageId = ResolvedVirtualPageId,
            ExactTileId = ExactTileId,
            InterpolationWeight = InterpolationWeight,
            InverseInclusionProbability = InverseInclusionProbability,
            PackedConsumerFallbackAndPageGeneration = packed,
            FeedbackGeneration = FeedbackGeneration
        };
        return true;
    }

    /// <summary>
    /// Horvitz-Thompson corrected contribution mass.  No priority clamp is
    /// applied here: only the later scheduler transform may clamp its score.
    /// </summary>
    public float EstimateContributionMass(float physicalReceiverContribution)
    {
        if (!float.IsFinite(physicalReceiverContribution) ||
            physicalReceiverContribution < 0.0f ||
            !float.IsFinite(InterpolationWeight) ||
            InterpolationWeight < 0.0f || InterpolationWeight > 1.0f ||
            !float.IsFinite(InverseInclusionProbability) ||
            InverseInclusionProbability < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalReceiverContribution));
        }

        double corrected = (double)physicalReceiverContribution *
            InterpolationWeight * InverseInclusionProbability;
        if (!double.IsFinite(corrected) || corrected > float.MaxValue)
            throw new OverflowException("Receiver contribution mass is not representable as FP32.");
        return (float)corrected;
    }
}

/// <summary>
/// Stable B1 sampling identity.  It deliberately contains no transient draw,
/// fragment, or compacted-array index.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackStochasticIdentity(
    SimpleDdgiReceiverFeedbackProducer Producer,
    ulong StableReceiverOrTileId,
    uint FrameSampleEpoch)
{
    public DdgiStochasticIdentity ToPrerequisiteIdentity() => new(
        WorldProbeStableKey: StableReceiverOrTileId,
        DirectionRayOrdinal: (uint)Producer,
        SourceLightingEpoch: 0u,
        SamplingSequenceEpoch: FrameSampleEpoch,
        DecisionDomain: DdgiStochasticDecisionDomain.ReceiverContributionFeedback);

    public uint Hash32() => ToPrerequisiteIdentity().Hash32();

    /// <summary>Returns a deterministic Bernoulli decision with known p.</summary>
    public bool IsIncluded(float inclusionProbability)
    {
        if (!float.IsFinite(inclusionProbability) ||
            inclusionProbability < 0.0f || inclusionProbability > 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(inclusionProbability));
        }
        if (inclusionProbability <= 0.0f)
            return false;
        if (inclusionProbability >= 1.0f)
            return true;

        return ToPrerequisiteIdentity().UnitFloat() < inclusionProbability;
    }
}
