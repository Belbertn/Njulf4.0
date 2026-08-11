using System;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Data;

/// <summary>
/// Fixed per-producer reservation capacities for one B1 capture generation.
/// The values are part of the admitted layout and cannot be changed while an
/// allocation is live.  A producer may borrow only from the separate shared
/// overflow range; it can never consume another producer's reserved minimum.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackProducerCapacities(
    uint OpaqueForward,
    uint AlphaMaskOrFoliage,
    uint TransparentWeightedOit,
    uint Particles,
    uint Fog,
    uint ReflectionCapture,
    uint RefinementOrBaseFallback)
{
    public uint Get(SimpleDdgiReceiverFeedbackProducer producer) => producer switch
    {
        SimpleDdgiReceiverFeedbackProducer.OpaqueForward => OpaqueForward,
        SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage => AlphaMaskOrFoliage,
        SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit => TransparentWeightedOit,
        SimpleDdgiReceiverFeedbackProducer.Particles => Particles,
        SimpleDdgiReceiverFeedbackProducer.Fog => Fog,
        SimpleDdgiReceiverFeedbackProducer.ReflectionCapture => ReflectionCapture,
        SimpleDdgiReceiverFeedbackProducer.RefinementOrBaseFallback =>
            RefinementOrBaseFallback,
        _ => throw new ArgumentOutOfRangeException(nameof(producer))
    };

    public ulong Total => checked(
        (ulong)OpaqueForward +
        AlphaMaskOrFoliage +
        TransparentWeightedOit +
        Particles +
        Fog +
        ReflectionCapture +
        RefinementOrBaseFallback);

    public SimpleDdgiReceiverFeedbackProducerCapacities Add(
        SimpleDdgiReceiverFeedbackProducer producer,
        uint count)
    {
        if (!Enum.IsDefined(producer))
            throw new ArgumentOutOfRangeException(nameof(producer));

        return producer switch
        {
            SimpleDdgiReceiverFeedbackProducer.OpaqueForward =>
                this with { OpaqueForward = checked(OpaqueForward + count) },
            SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage =>
                this with { AlphaMaskOrFoliage = checked(AlphaMaskOrFoliage + count) },
            SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit =>
                this with { TransparentWeightedOit = checked(TransparentWeightedOit + count) },
            SimpleDdgiReceiverFeedbackProducer.Particles =>
                this with { Particles = checked(Particles + count) },
            SimpleDdgiReceiverFeedbackProducer.Fog =>
                this with { Fog = checked(Fog + count) },
            SimpleDdgiReceiverFeedbackProducer.ReflectionCapture =>
                this with { ReflectionCapture = checked(ReflectionCapture + count) },
            SimpleDdgiReceiverFeedbackProducer.RefinementOrBaseFallback =>
                this with
                {
                    RefinementOrBaseFallback = checked(
                        RefinementOrBaseFallback + count)
                },
            _ => throw new ArgumentOutOfRangeException(nameof(producer))
        };
    }
}

/// <summary>One immutable producer range inside a frame's candidate records.</summary>
public readonly record struct SimpleDdgiReceiverFeedbackCaptureProducerRange(
    SimpleDdgiReceiverFeedbackProducer Producer,
    uint BaseRecord,
    uint Capacity)
{
    public uint EndRecord => checked(BaseRecord + Capacity);
}

/// <summary>
/// Exact frame-ringed staging layout consumed by the B1 capture pass.  Every
/// frame slice starts with a fixed control/range table and is followed by the
/// frozen 48-byte candidate records.  The control block is 256-byte aligned so
/// transfer updates and storage reads use one naturally aligned transaction.
/// </summary>
public readonly record struct SimpleDdgiReceiverFeedbackCaptureSourceLayout(
    uint RecordCapacity,
    uint SharedOverflowBaseRecord,
    uint SharedOverflowCapacity,
    uint RecordsOffsetWords,
    uint FrameStrideWords,
    ulong RequiredBytes,
    SimpleDdgiReceiverFeedbackProducerCapacities ProducerCapacities)
{
    public static SimpleDdgiReceiverFeedbackCaptureSourceLayout Empty { get; } =
        new();

    public bool IsValid =>
        RecordCapacity != 0u &&
        RecordsOffsetWords == SimpleDdgiReceiverFeedbackCaptureSourceAbi.ControlWords &&
        FrameStrideWords >= RecordsOffsetWords &&
        RequiredBytes != 0UL;

    public uint GetFrameControlOffsetWords(int frameIndex)
    {
        RenderingConstants.ValidateFrameIndex(frameIndex);
        return checked(
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords +
            (uint)frameIndex * FrameStrideWords);
    }

    public uint GetFrameRecordOffsetWords(int frameIndex) => checked(
        GetFrameControlOffsetWords(frameIndex) + RecordsOffsetWords);

    public SimpleDdgiReceiverFeedbackCaptureProducerRange GetProducerRange(
        SimpleDdgiReceiverFeedbackProducer producer)
    {
        if (!Enum.IsDefined(producer))
            throw new ArgumentOutOfRangeException(nameof(producer));

        uint baseRecord = 0u;
        for (uint ordinal = 0u; ordinal < (uint)producer; ordinal++)
        {
            baseRecord = checked(baseRecord + ProducerCapacities.Get(
                (SimpleDdgiReceiverFeedbackProducer)ordinal));
        }

        return new SimpleDdgiReceiverFeedbackCaptureProducerRange(
            producer,
            baseRecord,
            ProducerCapacities.Get(producer));
    }
}

/// <summary>
/// Managed mirror of the source-control block read by all exact B1 producers.
/// Counters are GPU-owned after the reset/update transaction; managed code
/// writes only the immutable identity and range words.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverFeedbackCaptureControl
{
    public uint AbiVersion;
    public uint LayoutRevision;
    public uint FeedbackGeneration;
    public uint ViewportGeneration;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint RecordCapacity;
    public uint ProducerCount;
    public uint SharedOverflowBaseRecord;
    public uint SharedOverflowCapacity;
    public uint SharedOverflowCount;
    public uint ProducerOverflowMask;
    public uint TotalReservedRecordCount;
    public uint Flags;
    public uint EndianSentinel;
    public uint RequiredProducerMask;
}

/// <summary>
/// Immutable allocation header at word zero of the candidate descriptor. It
/// lets graphics/compute producers derive their own frame-ringed control
/// offset from an existing frame index without expanding unrelated push-
/// constant ABIs or rebinding an in-flight global descriptor.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverFeedbackCaptureGlobalHeader
{
    public uint AbiVersion;
    public uint LayoutRevision;
    public uint FrameCount;
    public uint FrameStrideWords;
    public uint GlobalHeaderWords;
    public uint ControlWords;
    public uint CandidateWords;
    public uint RecordCapacity;
    public uint RequiredBytesLow;
    public uint RequiredBytesHigh;
    public uint Flags;
    public uint EndianSentinel;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
}

/// <summary>GPU-owned range counters following the capture control header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUSimpleDdgiReceiverFeedbackCaptureProducerRange
{
    public uint BaseRecord;
    public uint Capacity;
    public uint ReservedCount;
    public uint DroppedCount;
}

/// <summary>
/// Source-side ABI for exact candidates.  It is intentionally versioned
/// independently from the sort ABI: producer/range changes must not silently
/// reinterpret a staging buffer accepted by an older capture shader.
/// </summary>
public static class SimpleDdgiReceiverFeedbackCaptureSourceAbi
{
    public const uint Version = 0xB101_2005u;
    public const uint LayoutRevision = 0xB101_2005u;
    public const uint CandidateBindlessSlot = 209u;
    public const uint EndianSentinel = SimpleDdgiReceiverFeedbackV2Abi.EndianSentinel;
    public const uint ProducerCount = 7u;
    public const uint KnownProducerMask = (1u << (int)ProducerCount) - 1u;
    public const uint HeaderWords = 16u;
    public const uint RawGlobalHeaderWords = 16u;
    public const uint GlobalHeaderWords = 64u;
    public const uint ProducerRangeWords = 4u;
    public const uint RawControlWords = HeaderWords + ProducerCount * ProducerRangeWords;
    public const uint ControlAlignmentWords = 64u;
    public const uint ControlWords = 64u;
    public const uint CandidateWords = 12u;
    public const uint CandidateBytes = CandidateWords * sizeof(uint);
    public const uint SurfaceTileScale = 12u;
    public const uint MaximumUniqueGatherOwnersPerTile = 32u;
    public const uint FrameCount = RenderingConstants.FramesInFlight;
    public const uint ReadyForCaptureFlag = 1u << 0;
    public const uint KnownFlags = ReadyForCaptureFlag;
    public const uint GlobalHeaderReadyFlag = 1u << 0;
    public const uint KnownGlobalHeaderFlags = GlobalHeaderReadyFlag;
    // Header word 15 freezes which producer classes are semantically required
    // for this frame. Word 44 is the first padded word after the seven range
    // records and is GPU-owned. Capture/reduce is legal only when the two
    // masks match exactly. This prevents an omitted late graphics/compute
    // producer from being mistaken for a valid zero-contribution producer.
    public const uint RequiredProducerMaskWord = 15u;
    public const uint CompletedProducerMaskWord = RawControlWords;
    // Padded control words are immutable for the lifetime of one capture
    // transaction. Graphics producers cannot grow unrelated push-constant
    // blocks, so they consume the exact planner-selected sampling policy here.
    public const uint ScreenSamplingPeriodWord = RawControlWords + 1u;
    public const uint ScreenSamplingPhaseWord = RawControlWords + 2u;
    public const uint MaximumUniqueGatherOwnersWord = RawControlWords + 3u;

    public static uint GetProducerBit(SimpleDdgiReceiverFeedbackProducer producer)
    {
        if (!Enum.IsDefined(producer))
            throw new ArgumentOutOfRangeException(nameof(producer));
        return 1u << checked((int)producer);
    }

    public static bool IsValidProducerMask(uint mask, bool allowEmpty = false) =>
        (allowEmpty || mask != 0u) && (mask & ~KnownProducerMask) == 0u;

    public static bool TryCreateLayout(
        uint recordCapacity,
        in SimpleDdgiReceiverFeedbackProducerCapacities producerCapacities,
        out SimpleDdgiReceiverFeedbackCaptureSourceLayout layout,
        out string reason)
    {
        layout = SimpleDdgiReceiverFeedbackCaptureSourceLayout.Empty;
        reason = string.Empty;
        if (recordCapacity == 0u ||
            recordCapacity > SimpleDdgiReceiverFeedbackGpuSortAbi.MaximumRecordCapacity)
        {
            reason = "receiver-feedback-capture-record-capacity-invalid";
            return false;
        }

        try
        {
            ulong producerTotal = producerCapacities.Total;
            if (producerTotal > recordCapacity)
            {
                reason = "receiver-feedback-producer-reservations-exceed-record-capacity";
                return false;
            }

            uint sharedBase = checked((uint)producerTotal);
            uint sharedCapacity = checked(recordCapacity - sharedBase);
            ulong unalignedFrameWords = checked(
                (ulong)ControlWords + (ulong)recordCapacity * CandidateWords);
            ulong frameStrideWords64 = AlignUp(
                unalignedFrameWords,
                ControlAlignmentWords);
            ulong totalWords = checked(
                GlobalHeaderWords + frameStrideWords64 * FrameCount);
            if (frameStrideWords64 > uint.MaxValue || totalWords > uint.MaxValue)
            {
                reason = "receiver-feedback-capture-source-u32-word-address-limit-exceeded";
                return false;
            }

            layout = new SimpleDdgiReceiverFeedbackCaptureSourceLayout(
                recordCapacity,
                sharedBase,
                sharedCapacity,
                ControlWords,
                checked((uint)frameStrideWords64),
                checked(totalWords * sizeof(uint)),
                producerCapacities);
            return true;
        }
        catch (OverflowException)
        {
            reason = "receiver-feedback-capture-source-layout-overflow";
            return false;
        }
    }

    public static GPUSimpleDdgiReceiverFeedbackCaptureControl CreateControl(
        in SimpleDdgiReceiverFeedbackCaptureSourceLayout layout,
        uint feedbackGeneration,
        uint viewportGeneration,
        ulong frameSerial,
        uint requiredProducerMask)
    {
        if (!layout.IsValid || feedbackGeneration == 0u ||
            viewportGeneration == 0u || frameSerial == ulong.MaxValue ||
            !IsValidProducerMask(requiredProducerMask))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        return new GPUSimpleDdgiReceiverFeedbackCaptureControl
        {
            AbiVersion = Version,
            LayoutRevision = LayoutRevision,
            FeedbackGeneration = feedbackGeneration,
            ViewportGeneration = viewportGeneration,
            FrameSerialLow = unchecked((uint)frameSerial),
            FrameSerialHigh = checked((uint)(frameSerial >> 32)),
            RecordCapacity = layout.RecordCapacity,
            ProducerCount = ProducerCount,
            SharedOverflowBaseRecord = layout.SharedOverflowBaseRecord,
            SharedOverflowCapacity = layout.SharedOverflowCapacity,
            SharedOverflowCount = 0u,
            ProducerOverflowMask = 0u,
            TotalReservedRecordCount = 0u,
            Flags = 0u,
            EndianSentinel = EndianSentinel,
            RequiredProducerMask = requiredProducerMask
        };
    }

    public static GPUSimpleDdgiReceiverFeedbackCaptureGlobalHeader
        CreateGlobalHeader(
            in SimpleDdgiReceiverFeedbackCaptureSourceLayout layout)
    {
        if (!layout.IsValid)
            throw new ArgumentOutOfRangeException(nameof(layout));
        return new GPUSimpleDdgiReceiverFeedbackCaptureGlobalHeader
        {
            AbiVersion = Version,
            LayoutRevision = LayoutRevision,
            FrameCount = FrameCount,
            FrameStrideWords = layout.FrameStrideWords,
            GlobalHeaderWords = GlobalHeaderWords,
            ControlWords = ControlWords,
            CandidateWords = CandidateWords,
            RecordCapacity = layout.RecordCapacity,
            RequiredBytesLow = unchecked((uint)layout.RequiredBytes),
            RequiredBytesHigh = checked((uint)(layout.RequiredBytes >> 32)),
            Flags = GlobalHeaderReadyFlag,
            EndianSentinel = EndianSentinel
        };
    }

    public static void AssertManagedLayout()
    {
        SimpleDdgiReceiverFeedbackV2Abi.AssertManagedLayout();
        if (RawControlWords > ControlWords ||
            RawGlobalHeaderWords > GlobalHeaderWords ||
            Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackCaptureGlobalHeader>() !=
                RawGlobalHeaderWords * sizeof(uint) ||
            Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackCaptureControl>() !=
                HeaderWords * sizeof(uint) ||
            Marshal.SizeOf<GPUSimpleDdgiReceiverFeedbackCaptureProducerRange>() !=
                ProducerRangeWords * sizeof(uint))
        {
            throw new InvalidOperationException(
                "The B1 capture-source control layout no longer matches its frozen ABI.");
        }
    }

    private static ulong AlignUp(ulong value, uint alignment) => checked(
        ((value + alignment - 1UL) / alignment) * alignment);
}
