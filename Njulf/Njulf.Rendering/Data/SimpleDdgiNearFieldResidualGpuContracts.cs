using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Byte-level contract shared by the optional C5 near-field residual compute
/// stages and the native upload/readback boundary.  This contract intentionally
/// says nothing about renderer activation: the C5 shaders remain unavailable
/// until the renderer has registered the required source attachment, images,
/// barriers, and passes.
/// </summary>
public static class SimpleDdgiNearFieldResidualGpuAbi
{
    /// <summary>
    /// Increment when any C5 GPU field, binding meaning, source ownership rule,
    /// or history-reuse rule changes.
    /// </summary>
    // V14 gives trace and resolve coverage independent compact lists and adds
    // a separate double-buffered 16-byte per-tile scheduler history.
    public const uint Version = 0x4335_000Eu;

    public const uint DirectDiffuseTraceSourceTerm = 1u << 0;
    public const uint EmissiveTraceSourceTerm = 1u << 1;
    public const uint AllowedTraceSourceTerms =
        DirectDiffuseTraceSourceTerm | EmissiveTraceSourceTerm;

    public const uint HitMetadataByteCount = 48u;
    public const uint TraceFrameConstantsByteCount = 288u;
    public const uint TelemetryMagic = 0x4335_544Du;
    public const uint TelemetryHeaderByteCount = 128u;
    public const uint TelemetryHeaderWordCount = TelemetryHeaderByteCount / 4u;
    // The first twenty words retain the diagnostics-v3 payload. The appended
    // words carry bounded per-tile proposal/traversal evidence consumed by the
    // final reduction pass; two words remain reserved and must stay zero.
    public const uint TileRecordByteCount = 96u;
    public const uint TileRecordWordCount = TileRecordByteCount / 4u;
    public const uint TelemetryTraceCompleteBit = 1u << 0;
    public const uint TelemetryTemporalCompleteBit = 1u << 1;
    public const uint TelemetryRequiredCompletionMask =
        TelemetryTraceCompleteBit | TelemetryTemporalCompleteBit;
    public const uint TelemetryTraceRecordOverflowBit = 1u << 0;
    public const uint TelemetryTemporalRecordOverflowBit = 1u << 1;
    public const uint TelemetryTemporalRecordIdentityMismatchBit = 1u << 2;
    public const uint TelemetryFinalizeRecordInvalidBit = 1u << 16;
    public const uint TelemetryKnownOverflowFlags =
        TelemetryTraceRecordOverflowBit |
        TelemetryTemporalRecordOverflowBit |
        TelemetryTemporalRecordIdentityMismatchBit |
        TelemetryFinalizeRecordInvalidBit;
    public const uint ResetPushConstantByteCount = 32u;
    public const uint FinalizePushConstantByteCount = 16u;
    public const uint TracePushConstantByteCount = 84u;
    public const uint TemporalPushConstantByteCount = 96u;
    public const uint FilterPushConstantByteCount = 48u;
    public const uint CompositePushConstantByteCount = 48u;

    public const uint MaximumTraceSteps = 256u;
    // Retained only for managed source compatibility. V13 has no independent
    // mip-visit termination budget; each hierarchy sample consumes one trace
    // step. The telemetry peak is a saturating six-bit diagnostic.
    public const uint MaximumMipVisits = 32u;
    public const uint MaximumTelemetryMipVisits = 63u;
    public const uint MaximumBinaryRefinementSteps = 16u;
    public const uint MaximumFilterIterations = 8u;
    public const uint MaximumFilterRadius = 8u;
    public const uint MaximumTemporalHistoryLength = 64u;
    public const uint MaximumB3FootprintRadius = 8u;
    public const float MaximumEncodableTraceDistance = 65_504.0f;
    public const uint MaximumSurfaceTableEntryCount = 65_534u;
    public const uint InvalidSurfaceToken = 0xffffu;
    public const uint PreparePushConstantByteCount = 48u;
    public const uint ClassifyPushConstantByteCount = 96u;
    public const uint FrequencySeparationPushConstantByteCount = 48u;
    public const uint IndirectDispatchArgumentByteCount = 12u;
    public const uint IndirectStageCount = 8u;
    public const uint ActiveTileHeaderWordCount = 64u;
    public const uint IndirectArgumentFirstWord = 8u;
    public const uint TraceIndirectStage = 0u;
    public const uint TemporalIndirectStage = 1u;
    public const uint FirstFilterIndirectStage = 2u;
    public const uint FrequencySeparationIndirectStage = 6u;
    public const uint CompositeIndirectStage = 7u;

    public static ulong IndirectArgumentOffset(uint stage)
    {
        if (stage >= IndirectStageCount)
            throw new ArgumentOutOfRangeException(nameof(stage));
        return checked((IndirectArgumentFirstWord + stage * 3u) * sizeof(uint));
    }

    /// <summary>
    /// C5's managed allocation only owns these descriptors.  The Hi-Z,
    /// receiver metadata, and final canonical scene-color descriptors are
    /// externally owned by the renderer integration.
    /// </summary>
    // V14 appends two persistent scheduler-history buffers. Disabled local
    // adaptivity never dispatches their classifier, but generation accounting
    // remains complete and deterministic.
    public const uint BaseOwnedDescriptorCount = 24u;
    public const uint FilterScratchDescriptorCount = 2u;

    public static bool HasOnlyAllowedTraceSources(uint sourceTerms) =>
        (sourceTerms & ~AllowedTraceSourceTerms) == 0u &&
        (sourceTerms & AllowedTraceSourceTerms) != 0u;

    public static void VerifyManagedLayout()
    {
        Verify<GPUSimpleDdgiNearFieldResidualHitMetadata>(
            HitMetadataByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.ReceiverLinearDepth), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.HitLinearDepth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedFlagsAndReceiverFootprint), 8),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedHitNormal), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.ReceiverObjectId), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.HitMaterialId), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedReceiverRevisions), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedHitUv), 40),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedHitSourceRadiance), 44));
        Verify<GPUSimpleDdgiNearFieldResidualTraceFrameConstants>(
            TraceFrameConstantsByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.ViewProjection), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.InverseViewProjection), 64),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.PreviousViewProjection), 128),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.PreviousInverseViewProjection), 192),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.FullExtentAndInverse), 256),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.ClipAndSequence), 272));
        Verify<GPUSimpleDdgiNearFieldResidualResetPushConstants>(
            ResetPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.MetadataCount), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.TileWordCount), 8),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.HistoryEpoch), 12));
        Verify<GPUSimpleDdgiNearFieldResidualPreparePushConstants>(
            PreparePushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualPreparePushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualPreparePushConstants.TraceWidth), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualPreparePushConstants.TileCapacity), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualPreparePushConstants.NearPlane), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualPreparePushConstants.IndirectStageCount), 44));
        Verify<GPUSimpleDdgiNearFieldResidualClassifyPushConstants>(
            ClassifyPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.TileCapacity), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.HistoryEpoch), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.SchedulerEpoch), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.MaximumHistoryOnlyAge), 44),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.HighMotion), 52),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.LowConfidence), 68),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.InterleavedConfidenceDecay), 76),
            (nameof(GPUSimpleDdgiNearFieldResidualClassifyPushConstants.ReceiverCacheMetadataAvailable), 80));
        SimpleDdgiNearFieldResidualAdaptiveAbi.VerifyManagedLayout();
        Verify<GPUSimpleDdgiNearFieldResidualTelemetryHeader>(
            TelemetryHeaderByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.Magic), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.TraceWidth), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.CompletionMask), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.RayHitCount), 40),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.OverflowFlags), 60),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.CandidateTileCount), 64),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.OverflowTileCount), 80),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.ProposalSampleCount), 88),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.ValidSampleCount), 108));
        Verify<GPUSimpleDdgiNearFieldResidualTileRecord>(
            TileRecordByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TileIndex), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TraceCounts0), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.HistoryCounts0), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TraceVisitTotals), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.VarianceSumBits), 36),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.SignedResidualEnergyBits), 44),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.FlagsAndMaximumDistance), 60),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.DetailedHistoryCounts0), 64),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.ProposalCounts), 76),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.HitAndValidSampleCounts), 84),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.Reserved23), 92));
        Verify<GPUSimpleDdgiNearFieldResidualFinalizePushConstants>(
            FinalizePushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualFinalizePushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualFinalizePushConstants.TileCount), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualFinalizePushConstants.TraceWidth), 8),
            (nameof(GPUSimpleDdgiNearFieldResidualFinalizePushConstants.TraceHeight), 12));
        Verify<GPUSimpleDdgiNearFieldResidualTracePushConstants>(
            TracePushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.TraceSourceTerms), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.TraceWidth), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.HistoryEpoch), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.MaximumTraceSteps), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.Flags), 44),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.Thickness), 48),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.MinimumNormalDot), 60),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.MaximumTraceDistance), 64),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.FullWeightTraceDistance), 68),
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.TraceSourceRevision), 80));
        Verify<GPUSimpleDdgiNearFieldResidualTemporalPushConstants>(
            TemporalPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TraceWidth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.HistoryEpoch), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TraceSourceAbiRevision), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.StructuralProjectionRevision), 44),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TraceSourceLayoutRevision), 68),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.MaximumHistoryLength), 72),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.Flags), 76),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TemporalBlend), 80),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.HitUvTolerance), 92));
        Verify<GPUSimpleDdgiNearFieldResidualFilterPushConstants>(
            FilterPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.TraceWidth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.IterationIndex), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.FilterRadius), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.DepthTolerance), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualFilterPushConstants.MinimumNormalDot), 32));
        Verify<GPUSimpleDdgiNearFieldResidualFrequencyPushConstants>(
            FrequencySeparationPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.HistoryEpoch), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.ActiveTileHeaderWords), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.DepthTolerance), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.MaximumOuterStride), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualFrequencyPushConstants.DebugView), 36));
        Verify<GPUSimpleDdgiNearFieldResidualCompositePushConstants>(
            CompositePushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.FullWidth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.HistoryEpoch), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.Flags), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.ResidualIntensity), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.ConfidenceFloor), 32),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.DebugView), 44));
    }

    private static void Verify<T>(
        uint expectedSize,
        params (string Field, int Offset)[] expectedOffsets)
        where T : struct
    {
        int actualSize = Marshal.SizeOf<T>();
        if (actualSize != expectedSize)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is {actualSize} bytes; expected {expectedSize}.");
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
/// Common C5 flags.  An invalid/miss pixel must have neither
/// <see cref="ValidCandidate"/> nor <see cref="HistoryAccepted"/> set and its
/// residual payload must be exactly zero.
/// </summary>
[Flags]
public enum SimpleDdgiNearFieldResidualGpuFlags : uint
{
    None = 0u,
    ValidCandidate = 1u << 0,
    ScreenSpaceHit = 1u << 1,
    HistoryInputValid = 1u << 2,
    HistoryAccepted = 1u << 3,
    CameraCut = 1u << 4,
    ReversedZ = 1u << 5,
    SourceAttachmentVerified = 1u << 6,
    InvalidAndMissOutputsZeroed = 1u << 7,
    CompositeUsesValidResidualOnly = 1u << 8,
    FoliageMotionVectorsValid = 1u << 9,
    SourceLightingEpochChanged = 1u << 10,
    LocalAdaptiveScheduling = 1u << 11
}

/// <summary>
/// Fixed 48-byte per-trace-pixel metadata. Source/layout/history revisions are
/// immutable bank identity validated before the previous bank is exposed;
/// keeping them once per bank avoids repeating eight bytes for every pixel.
/// Exact hit UV and receiver/hit identities remain per pixel so independently
/// moving receivers and emitters cannot reuse an unrelated sample.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiNearFieldResidualHitMetadata
{
    public float ReceiverLinearDepth;
    public float HitLinearDepth;
    // Lower 16 bits are flags; upper 16 are the IEEE-754 half receiver B3
    // footprint. This keeps the complete per-pixel contract at 48 bytes.
    public uint PackedFlagsAndReceiverFootprint;
    public uint PackedHitNormal;
    public uint ReceiverObjectId;
    public uint ReceiverMaterialId;
    public uint HitObjectId;
    public uint HitMaterialId;
    public uint PackedReceiverRevisions;
    public uint PackedHitRevisions;
    public uint PackedHitUv;
    public uint PackedHitSourceRadiance;
}

/// <summary>
/// Immutable matrices consumed by one trace ring slot. The renderer updates a
/// host-visible slot only after that frame fence has completed; descriptors
/// themselves are never rewritten while in flight.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 288)]
public struct GPUSimpleDdgiNearFieldResidualTraceFrameConstants
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Matrix4x4 PreviousViewProjection;
    public Matrix4x4 PreviousInverseViewProjection;
    public Vector4 FullExtentAndInverse;
    // near plane, far plane, stable sequence index, source semantic version.
    public Vector4 ClipAndSequence;
}

/// <summary>
/// Clears every per-frame C5 metadata/tile word before trace.  Keeping this a
/// separate bounded dispatch prevents stale atomic tile counters or hit
/// metadata from becoming a plausible candidate after a viewport or mode
/// transition.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct GPUSimpleDdgiNearFieldResidualResetPushConstants
{
    public uint AbiVersion;
    public uint MetadataCount;
    public uint TileWordCount;
    public uint HistoryEpoch;
    public uint Flags;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint TileCount;
}

/// <summary>
/// Fixed prepare/compaction parameters. The prepare shader selects the nearest
/// valid full-resolution receiver for each trace pixel and emits all indirect
/// dispatch arguments consumed later in the frame.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiNearFieldResidualPreparePushConstants
{
    public uint AbiVersion;
    public uint FullWidth;
    public uint FullHeight;
    public uint TraceWidth;
    public uint TraceHeight;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public uint TileCapacity;
    public uint RaysPerPixel;
    public float NearPlane;
    public float FarPlane;
    public uint ActiveTileHeaderWords;
    public uint IndirectStageCount;
}

/// <summary>96-byte local scheduler/classifier push block.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct GPUSimpleDdgiNearFieldResidualClassifyPushConstants
{
    public uint AbiVersion;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint TileCapacity;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public uint HistoryEpoch;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint SchedulerEpoch;
    public uint MaximumRaysPerPixel;
    public uint NormalRaysPerPixel;
    public uint MaximumHistoryOnlyAge;
    public uint ForcedRefreshPeriod;
    public float HighMotion;
    public float HighVariance;
    public float ActiveEnergy;
    public float PerceptualEnergyFloor;
    public float LowConfidence;
    public float HistoryOnlyConfidenceDecay;
    public float InterleavedConfidenceDecay;
    public uint ReceiverCacheMetadataAvailable;
    public uint FullWidth;
    public uint FullHeight;
    public uint Reserved23;
}

/// <summary>Fixed 128-byte identity, trace, compaction, and proposal header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 128)]
public struct GPUSimpleDdgiNearFieldResidualTelemetryHeader
{
    public uint Magic;
    public uint AbiVersion;
    public uint FrameSerialLow;
    public uint FrameSerialHigh;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint TileCount;
    public uint CompletionMask;
    public uint CandidateReceiverCount;
    public uint RaysLaunched;
    public uint RayHitCount;
    public uint RayMissCount;
    public uint InvalidReceiverCount;
    public uint InvalidRayCount;
    public uint TraceRejectedCount;
    public uint OverflowFlags;
    public uint CandidateTileCount;
    public uint ActiveTileCount;
    public uint CompactedTileCount;
    public uint EmptyTileCount;
    public uint OverflowTileCount;
    public uint InvalidSurfacePixelCount;
    public uint ProposalSampleCount;
    public uint GuidedProposalSampleCount;
    public uint GuidedValidSampleCount;
    public uint GuidedZeroContributionSampleCount;
    public uint CosineProposalSampleCount;
    public uint ValidSampleCount;
    public uint Reserved28;
    public uint Reserved29;
    public uint Reserved30;
    public uint Reserved31;
}

/// <summary>
/// Bounded per-tile trace/history/energy payload (24 uint words). Counts0..2
/// each pack four unsigned byte counters from least- to most-significant byte.
/// Visit totals and peaks use their documented bounded bit lanes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct GPUSimpleDdgiNearFieldResidualTileRecord
{
    public uint TileIndex;
    public uint TraceCounts0;
    public uint TraceCounts1;
    public uint TraceCounts2;
    public uint HistoryCounts0;
    public uint HistoryCounts1;
    public uint HistoryCounts2;
    public uint TraceVisitTotals;
    public uint TracePeakAndRefinement;
    public uint VarianceSumBits;
    public uint MaximumVarianceBits;
    public uint SignedResidualEnergyBits;
    public uint AbsoluteResidualEnergyBits;
    public uint SquaredResidualEnergyBits;
    public uint MaximumAbsoluteResidualEnergyBits;
    public uint FlagsAndMaximumDistance;
    public uint DetailedHistoryCounts0;
    public uint DetailedHistoryCounts1;
    public uint DetailedHistoryCounts2;
    public uint ProposalCounts;
    public uint GuidedAndTraversalCounts;
    public uint HitAndValidSampleCounts;
    public uint Reserved22;
    public uint Reserved23;
}

/// <summary>16-byte post-temporal telemetry reduction push block.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct GPUSimpleDdgiNearFieldResidualFinalizePushConstants
{
    public uint AbiVersion;
    public uint TileCount;
    public uint TraceWidth;
    public uint TraceHeight;
}

/// <summary>84-byte trace push block mirrored by ddgi_near_field_residual_trace.comp.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 84)]
public struct GPUSimpleDdgiNearFieldResidualTracePushConstants
{
    public uint AbiVersion;
    public uint TraceSourceTerms;
    public uint FullWidth;
    public uint FullHeight;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint FrameIndex;
    public uint HistoryEpoch;
    public uint MaximumTraceSteps;
    public uint RaysPerPixel;
    public uint BinaryRefinementSteps;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public float Thickness;
    public float StartBias;
    public float DepthTolerance;
    public float MinimumNormalDot;
    public float MaximumTraceDistance;
    public float FullWeightTraceDistance;
    public uint MinimumB3FootprintRadius;
    public uint MaximumB3FootprintRadius;
    public uint TraceSourceRevision;
}

/// <summary>96-byte temporal push block mirrored by the temporal stage.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public struct GPUSimpleDdgiNearFieldResidualTemporalPushConstants
{
    public uint AbiVersion;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint HistoryReadIndex;
    public uint HistoryWriteIndex;
    public uint HistoryEpoch;
    public uint TraceSourceAbiRevision;
    public uint ViewportRevision;
    public uint HiZRevision;
    public uint EffectiveModeRevision;
    public uint ExposureDomainRevision;
    public uint StructuralProjectionRevision;
    public uint OriginRebaseRevision;
    public uint SceneGeneration;
    public uint TraceSourceContentRevision;
    public uint NearFieldLayoutRevision;
    public uint B3OwnershipRevision;
    public uint TraceSourceLayoutRevision;
    public uint MaximumHistoryLength;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public float TemporalBlend;
    public float DepthTolerance;
    public float MinimumNormalDot;
    public float HitUvTolerance;
}

/// <summary>48-byte edge-aware filter push block.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiNearFieldResidualFilterPushConstants
{
    public uint AbiVersion;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint IterationIndex;
    public uint IterationCount;
    public uint FilterRadius;
    public float DepthTolerance;
    public float NormalPower;
    public float MinimumNormalDot;
    public float Reserved0;
    public uint HistoryEpoch;
    public uint Flags;
}

/// <summary>48-byte fixed B3 frequency-separation push block.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiNearFieldResidualFrequencyPushConstants
{
    public uint AbiVersion;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint HistoryEpoch;
    public uint ActiveTileHeaderWords;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public float DepthTolerance;
    public float MinimumNormalDot;
    public uint MaximumOuterStride;
    public uint DebugView;
    public uint Reserved1;
    public uint Reserved2;
}

/// <summary>48-byte final composite push block.  The canonical scene colour is bound only here.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
public struct GPUSimpleDdgiNearFieldResidualCompositePushConstants
{
    public uint AbiVersion;
    public uint FullWidth;
    public uint FullHeight;
    public uint TraceWidth;
    public uint TraceHeight;
    public uint HistoryEpoch;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public float ResidualIntensity;
    public float ConfidenceFloor;
    public float Reserved0;
    public float Reserved1;
    public uint DebugView;
}

/// <summary>
/// Binding map for the production C5 shader modules. It remains fixed and
/// separate from the global bindless table so every generation can build
/// immutable frame/bank descriptor sets. Any incompatible change must advance
/// <see cref="SimpleDdgiNearFieldResidualGpuAbi.Version"/>.
/// </summary>
public static class SimpleDdgiNearFieldResidualGpuBindings
{
    public const uint ResetHitMetadata = 0u;
    public const uint ResetTileRecords = 1u;

    public const uint PrepareFullDepth = 0u;
    public const uint PrepareFullSource = 1u;
    public const uint PrepareFullPayload = 2u;
    public const uint PrepareFullMotion = 3u;
    public const uint PrepareDepthFootprintOutput = 4u;
    public const uint PreparePayloadOutput = 5u;
    public const uint PrepareMotionOutput = 6u;
    public const uint PrepareSourceLuminanceOutput = 7u;
    public const uint PrepareActiveTilesAndIndirect = 8u;
    public const uint PrepareSurfaceTable = 9u;
    public const uint PrepareSceneObjects = 10u;
    public const uint PrepareSceneMaterials = 11u;
    public const uint PrepareTelemetry = 12u;
    public const uint PrepareFoliagePrototypes = 13u;
    public const uint PrepareFoliagePatches = 14u;
    public const uint PrepareFoliageClusters = 15u;

    public const uint ClassifyPreparedDepthFootprint = 0u;
    public const uint ClassifyPreparedReceiverPayload = 1u;
    public const uint ClassifyPreparedMotion = 2u;
    public const uint ClassifySchedulerHistoryRead = 3u;
    public const uint ClassifySchedulerHistoryWrite = 4u;
    public const uint ClassifyActiveTilesAndIndirect = 5u;
    public const uint ClassifyTileRecords = 6u;

    public const uint TraceDirectDiffuseEmissiveSource = 0u;
    public const uint TraceHiZ = 1u;
    public const uint TracePreparedDepthFootprint = 2u;
    public const uint TracePreparedReceiverPayload = 3u;
    public const uint TraceRawResidualOutput = 4u;
    public const uint TraceHitMetadataOutput = 5u;
    public const uint TraceTileRecords = 6u;
    public const uint TraceFrameConstants = 7u;
    public const uint TraceSurfaceTable = 8u;
    public const uint TraceActiveTiles = 9u;
    public const uint TraceFullReceiverPayload = 10u;
    public const uint TraceSourceLuminance = 11u;
    public const uint TraceSchedulerHistory = 12u;

    public const uint TemporalRawResidual = 0u;
    public const uint TemporalCurrentMetadata = 1u;
    public const uint TemporalHistoryRadiance = 2u;
    public const uint TemporalHistoryMoments = 3u;
    public const uint TemporalHistoryValidity = 4u;
    public const uint TemporalHistoryMetadata = 5u;
    public const uint TemporalRadianceOutput = 6u;
    public const uint TemporalMomentsOutput = 7u;
    public const uint TemporalValidityOutput = 8u;
    public const uint TemporalMetadataOutput = 9u;
    public const uint TemporalMotionVectors = 10u;
    public const uint TemporalCurrentReceiverPayload = 11u;
    public const uint TemporalHistoryReceiverNormal = 12u;
    public const uint TemporalHistoryNormalOutput = 13u;
    public const uint TemporalTileRecords = 14u;
    public const uint TemporalActiveTiles = 15u;
    public const uint TemporalCurrentSourceRadiance = 16u;
    public const uint TemporalCurrentFullDepth = 17u;
    public const uint TemporalCurrentFullPayload = 18u;
    public const uint TemporalFrameConstants = 19u;
    public const uint TemporalSurfaceTable = 20u;
    public const uint TemporalPreparedDepthFootprint = 21u;
    public const uint TemporalSchedulerHistory = 22u;

    public const uint FinalizeTileRecords = 0u;

    public const uint FilterInput = 0u;
    public const uint FilterMetadata = 1u;
    public const uint FilterOutput = 2u;
    public const uint FilterReceiverPayload = 3u;
    public const uint FilterActiveTiles = 4u;
    public const uint FilterMoments = 5u;

    public const uint FrequencyNearEstimate = 0u;
    public const uint FrequencyPreparedDepthFootprint = 1u;
    public const uint FrequencyPreparedPayload = 2u;
    public const uint FrequencyMetadata = 3u;
    public const uint FrequencyBandResidualOutput = 4u;
    public const uint FrequencyActiveTiles = 5u;
    public const uint FrequencyFrameConstants = 6u;

    public const uint CompositeCanonicalSceneColor = 0u;
    public const uint CompositeFilteredResidual = 1u;
    public const uint CompositeMetadata = 2u;
    public const uint CompositeSceneColorOutput = 3u;
    public const uint CompositeActiveTiles = 4u;
    public const uint CompositeFullReceiverPayload = 5u;
    public const uint CompositeFullReceiverDepth = 6u;
    public const uint CompositeSurfaceTable = 7u;
    public const uint CompositePreparedReceiverPayload = 8u;
    public const uint CompositeFrameConstants = 9u;
    public const uint CompositeFullDirectSource = 10u;
    public const uint CompositeHistoryValidity = 11u;
}

/// <summary>
/// Keeps the appended C5 diagnostic values in their own channel. Their authored
/// enum values intentionally are not forwarded through the legacy material /
/// animation debug-number namespace used by the opaque fragment shaders.
/// </summary>
public static class SimpleDdgiNearFieldResidualDebugViewContract
{
    public static bool IsC5View(GlobalIlluminationDebugView view) =>
        view is >= GlobalIlluminationDebugView.C5SourceRadiance and
            <= GlobalIlluminationDebugView.C5B3Footprint;

    public static bool IsC5View(uint view) =>
        view is >= (uint)GlobalIlluminationDebugView.C5SourceRadiance and
            <= (uint)GlobalIlluminationDebugView.C5B3Footprint;
}
