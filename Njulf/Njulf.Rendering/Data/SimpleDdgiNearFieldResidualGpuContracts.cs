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
    // V10 fixes the GLSL std430 metadata-array stride and admits C5 output only
    // after temporal/spatial evidence plus a bounded composite correction.
    public const uint Version = 0x4335_000Au;

    public const uint DirectDiffuseTraceSourceTerm = 1u << 0;
    public const uint EmissiveTraceSourceTerm = 1u << 1;
    public const uint AllowedTraceSourceTerms =
        DirectDiffuseTraceSourceTerm | EmissiveTraceSourceTerm;

    public const uint HitMetadataByteCount = 40u;
    public const uint TraceFrameConstantsByteCount = 160u;
    public const uint TelemetryMagic = 0x4335_544Du;
    public const uint TelemetryHeaderByteCount = 64u;
    public const uint TelemetryHeaderWordCount = TelemetryHeaderByteCount / 4u;
    // One cache line per 8x8 tile. Tile-local population/rejection counters
    // are packed as bytes (their mathematical maximum is 64); bounded visit
    // totals use 16-bit lanes. Float aggregates retain full FP32 precision.
    public const uint TileRecordByteCount = 64u;
    public const uint TileRecordWordCount = TileRecordByteCount / 4u;
    public const uint TelemetryTraceCompleteBit = 1u << 0;
    public const uint TelemetryTemporalCompleteBit = 1u << 1;
    public const uint TelemetryRequiredCompletionMask =
        TelemetryTraceCompleteBit | TelemetryTemporalCompleteBit;
    public const uint ResetPushConstantByteCount = 32u;
    public const uint TracePushConstantByteCount = 80u;
    public const uint TemporalPushConstantByteCount = 96u;
    public const uint FilterPushConstantByteCount = 48u;
    public const uint CompositePushConstantByteCount = 48u;

    public const uint MaximumTraceSteps = 256u;
    public const uint MaximumMipVisits = 32u;
    public const uint MaximumBinaryRefinementSteps = 16u;
    public const uint MaximumFilterIterations = 8u;
    public const uint MaximumFilterRadius = 8u;
    public const uint MaximumTemporalHistoryLength = 64u;
    public const uint MaximumB3FootprintRadius = 8u;
    public const float MaximumEncodableTraceDistance = 65_504.0f;

    /// <summary>
    /// C5's managed allocation only owns these descriptors.  The Hi-Z,
    /// receiver metadata, and final canonical scene-color descriptors are
    /// externally owned by the renderer integration.
    /// </summary>
    public const uint BaseOwnedDescriptorCount = 17u;
    public const uint FilterScratchDescriptorCount = 2u;

    public static bool HasOnlyAllowedTraceSources(uint sourceTerms) =>
        (sourceTerms & ~AllowedTraceSourceTerms) == 0u &&
        (sourceTerms & AllowedTraceSourceTerms) != 0u;

    public static void VerifyManagedLayout()
    {
        Verify<GPUSimpleDdgiNearFieldResidualHitMetadata>(
            HitMetadataByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.ReceiverDepth), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.HitDepth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.Confidence), 8),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.PackedFlags), 12),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.ReceiverObjectId), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.HitMaterialId), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualHitMetadata.HitUvX), 32));
        Verify<GPUSimpleDdgiNearFieldResidualTraceFrameConstants>(
            TraceFrameConstantsByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.ViewProjection), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.InverseViewProjection), 64),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.FullExtentAndInverse), 128),
            (nameof(GPUSimpleDdgiNearFieldResidualTraceFrameConstants.Reserved), 144));
        Verify<GPUSimpleDdgiNearFieldResidualResetPushConstants>(
            ResetPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.MetadataCount), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.TileWordCount), 8),
            (nameof(GPUSimpleDdgiNearFieldResidualResetPushConstants.HistoryEpoch), 12));
        Verify<GPUSimpleDdgiNearFieldResidualTelemetryHeader>(
            TelemetryHeaderByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.Magic), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.TraceWidth), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.CompletionMask), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.RayHitCount), 40),
            (nameof(GPUSimpleDdgiNearFieldResidualTelemetryHeader.OverflowFlags), 60));
        Verify<GPUSimpleDdgiNearFieldResidualTileRecord>(
            TileRecordByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TileIndex), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TraceCounts0), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.HistoryCounts0), 16),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.TraceVisitTotals), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.VarianceSumBits), 36),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.SignedResidualEnergyBits), 44),
            (nameof(GPUSimpleDdgiNearFieldResidualTileRecord.FlagsAndMaximumDistance), 60));
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
            (nameof(GPUSimpleDdgiNearFieldResidualTracePushConstants.TraceSourceRevision), 76));
        Verify<GPUSimpleDdgiNearFieldResidualTemporalPushConstants>(
            TemporalPushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TraceWidth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.HistoryEpoch), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.TraceSourceAbiRevision), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualTemporalPushConstants.ProjectionJitterRevision), 44),
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
        Verify<GPUSimpleDdgiNearFieldResidualCompositePushConstants>(
            CompositePushConstantByteCount,
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.AbiVersion), 0),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.FullWidth), 4),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.HistoryEpoch), 20),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.Flags), 24),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.ResidualIntensity), 28),
            (nameof(GPUSimpleDdgiNearFieldResidualCompositePushConstants.ConfidenceFloor), 32));
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
    CompositeUsesValidResidualOnly = 1u << 8
}

/// <summary>
/// Fixed 40-byte per-trace-pixel metadata. Source/layout/history revisions are
/// immutable bank identity validated before the previous bank is exposed;
/// keeping them once per bank avoids repeating eight bytes for every pixel.
/// Exact hit UV and receiver/hit identities remain per pixel so independently
/// moving receivers and emitters cannot reuse an unrelated sample.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 40)]
public struct GPUSimpleDdgiNearFieldResidualHitMetadata
{
    public float ReceiverDepth;
    public float HitDepth;
    public float Confidence;
    public SimpleDdgiNearFieldResidualGpuFlags PackedFlags;
    public uint ReceiverObjectId;
    public uint ReceiverMaterialId;
    public uint HitObjectId;
    public uint HitMaterialId;
    public float HitUvX;
    public float HitUvY;
}

/// <summary>
/// Immutable matrices consumed by one trace ring slot. The renderer updates a
/// host-visible slot only after that frame fence has completed; descriptors
/// themselves are never rewritten while in flight.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 160)]
public struct GPUSimpleDdgiNearFieldResidualTraceFrameConstants
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Vector4 FullExtentAndInverse;
    public Vector4 Reserved;
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

/// <summary>Fixed 64-byte identity and aggregate header.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
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
}

/// <summary>
/// Bounded per-tile trace/history/energy payload (16 uint words). Counts0..2
/// each pack four unsigned byte counters from least- to most-significant byte.
/// Visit totals and peaks use their documented bounded bit lanes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 64)]
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
}

/// <summary>80-byte trace push block mirrored by ddgi_near_field_residual_trace.comp.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
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
    public uint MaximumMipVisits;
    public uint BinaryRefinementSteps;
    public SimpleDdgiNearFieldResidualGpuFlags Flags;
    public float Thickness;
    public float StartBias;
    public float DepthTolerance;
    public float MinimumNormalDot;
    public float MaximumTraceDistance;
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
    public uint ProjectionJitterRevision;
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
    public uint Reserved2;
}

/// <summary>
/// Binding map for the standalone C5 shader modules.  It is intentionally
/// fixed and separate from the global bindless table because actual renderer
/// pass integration has not yet been admitted.  A future integration must
/// use this map verbatim or advance <see cref="SimpleDdgiNearFieldResidualGpuAbi.Version"/>.
/// </summary>
public static class SimpleDdgiNearFieldResidualGpuBindings
{
    public const uint ResetHitMetadata = 0u;
    public const uint ResetTileRecords = 1u;

    public const uint TraceDirectDiffuseEmissiveSource = 0u;
    public const uint TraceHiZ = 1u;
    public const uint TraceReceiverDepth = 2u;
    public const uint TraceReceiverPayload = 3u;
    public const uint TraceRawResidualOutput = 4u;
    public const uint TraceHitMetadataOutput = 5u;
    public const uint TraceTileRecords = 6u;
    public const uint TraceFrameConstants = 7u;

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

    public const uint FilterInput = 0u;
    public const uint FilterMetadata = 1u;
    public const uint FilterOutput = 2u;
    public const uint FilterReceiverPayload = 3u;

    public const uint CompositeCanonicalSceneColor = 0u;
    public const uint CompositeFilteredResidual = 1u;
    public const uint CompositeMetadata = 2u;
    public const uint CompositeSceneColorOutput = 3u;
}
