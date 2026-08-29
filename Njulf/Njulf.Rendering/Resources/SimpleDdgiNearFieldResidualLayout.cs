using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiNearFieldResidualFormat
{
    R16G16B16A16Sfloat,
    B10G11R11UfloatPack32
}

/// <summary>
/// Selects the sole producer of C5's pre-indirect direct-light source.  The
/// trace-resolution raster path is the production default; ForwardMrt keeps
/// the canonical full-resolution producer available as an explicit fallback.
/// </summary>
public enum SimpleDdgiNearFieldSourceProducerMode : byte
{
    ForwardMrt = 0,
    TraceResolutionRaster = 1
}

[Flags]
public enum SimpleDdgiNearFieldResidualResolutionScales : uint
{
    None = 0,
    Eighth = 1u << 0,
    Quarter = 1u << 1,
    Half = 1u << 2
}

/// <summary>
/// A complete quality profile. The layout compiler never drops the two-bank
/// hit metadata, moments, or history simply to make an incomplete profile fit
/// a budget.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualProfile(
    float ResolutionScale,
    SimpleDdgiNearFieldResidualFormat SourceFormat,
    int MaximumTraceSteps,
    int MaximumMipVisits,
    int BinaryRefinementSteps,
    int FilterIterationCount,
    uint ImageRowAlignment,
    uint ImageAllocationGranularity)
{
    public SimpleDdgiNearFieldSourceProducerMode SourceProducerMode
        { get; init; } =
            SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;

    public SimpleDdgiNearFieldResidualQualityPreset Preset { get; init; } =
        SimpleDdgiNearFieldResidualQualityPreset.Balanced;

    public float FullWeightTraceDistanceMeters { get; init; } = 4.0f;

    public float MaximumTraceDistanceMeters { get; init; } = 8.0f;

    public int MaximumRaysPerPixel { get; init; } = 2;

    public SimpleDdgiNearFieldResidualResolutionScales AllowedResolutionScales
        { get; init; } =
            SimpleDdgiNearFieldResidualResolutionScales.Eighth |
            SimpleDdgiNearFieldResidualResolutionScales.Quarter |
            SimpleDdgiNearFieldResidualResolutionScales.Half;

    /// <summary>Fixed production Performance preset at its highest tier.</summary>
    public static SimpleDdgiNearFieldResidualProfile Performance { get; } = new(
        ResolutionScale: 0.25f,
        SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
        MaximumTraceSteps: 48,
        // Retained in the managed signature for source compatibility only.
        // V13 charges every depth test to MaximumTraceSteps and never rejects
        // a trace through a separate mip-visit budget.
        MaximumMipVisits: 32,
        BinaryRefinementSteps: 4,
        FilterIterationCount: 1,
        ImageRowAlignment: 256,
        ImageAllocationGranularity: 65_536)
    {
        Preset = SimpleDdgiNearFieldResidualQualityPreset.Performance,
        FullWeightTraceDistanceMeters = 3.0f,
        MaximumTraceDistanceMeters = 6.0f,
        MaximumRaysPerPixel = 1,
        AllowedResolutionScales =
            SimpleDdgiNearFieldResidualResolutionScales.Eighth |
            SimpleDdgiNearFieldResidualResolutionScales.Quarter |
            SimpleDdgiNearFieldResidualResolutionScales.Half
    };

    /// <summary>Fixed production Balanced preset at its highest tier.</summary>
    public static SimpleDdgiNearFieldResidualProfile Balanced { get; } = new(
        ResolutionScale: 0.5f,
        SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
        MaximumTraceSteps: 64,
        MaximumMipVisits: 32,
        BinaryRefinementSteps: 4,
        FilterIterationCount: 2,
        ImageRowAlignment: 256,
        ImageAllocationGranularity: 65_536)
    {
        Preset = SimpleDdgiNearFieldResidualQualityPreset.Balanced,
        FullWeightTraceDistanceMeters = 4.0f,
        MaximumTraceDistanceMeters = 8.0f,
        MaximumRaysPerPixel = 2,
        AllowedResolutionScales =
            SimpleDdgiNearFieldResidualResolutionScales.Eighth |
            SimpleDdgiNearFieldResidualResolutionScales.Quarter |
            SimpleDdgiNearFieldResidualResolutionScales.Half
    };

    /// <summary>Fixed production Quality preset at its highest tier.</summary>
    public static SimpleDdgiNearFieldResidualProfile Quality { get; } = new(
        ResolutionScale: 0.5f,
        SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
        MaximumTraceSteps: 96,
        MaximumMipVisits: 32,
        BinaryRefinementSteps: 4,
        FilterIterationCount: 3,
        ImageRowAlignment: 256,
        ImageAllocationGranularity: 65_536)
    {
        Preset = SimpleDdgiNearFieldResidualQualityPreset.Quality,
        FullWeightTraceDistanceMeters = 6.0f,
        MaximumTraceDistanceMeters = 12.0f,
        MaximumRaysPerPixel = 4,
        AllowedResolutionScales =
            SimpleDdgiNearFieldResidualResolutionScales.Eighth |
            SimpleDdgiNearFieldResidualResolutionScales.Quarter |
            SimpleDdgiNearFieldResidualResolutionScales.Half
    };

    public static SimpleDdgiNearFieldResidualProfile ForPreset(
        SimpleDdgiNearFieldResidualQualityPreset preset,
        float resolutionScale)
    {
        SimpleDdgiNearFieldResidualProfile profile = preset switch
        {
            SimpleDdgiNearFieldResidualQualityPreset.Performance => Performance,
            SimpleDdgiNearFieldResidualQualityPreset.Quality => Quality,
            _ => Balanced
        };
        return profile with { ResolutionScale = resolutionScale };
    }

    public bool AllowsResolutionScale(float scale)
    {
        SimpleDdgiNearFieldResidualResolutionScales requested = scale switch
        {
            0.125f => SimpleDdgiNearFieldResidualResolutionScales.Eighth,
            0.25f => SimpleDdgiNearFieldResidualResolutionScales.Quarter,
            0.5f => SimpleDdgiNearFieldResidualResolutionScales.Half,
            _ => SimpleDdgiNearFieldResidualResolutionScales.None
        };
        return requested != SimpleDdgiNearFieldResidualResolutionScales.None &&
            (AllowedResolutionScales & requested) != 0;
    }

    public static SimpleDdgiNearFieldResidualProfile HalfResolutionReference { get; } =
        Balanced;

    /// <summary>
    /// Complete lower-resolution profile used when the half-resolution set
    /// cannot fit its independent envelope. No validation/history resource is
    /// removed; only the measured trace/reconstruction resolution changes.
    /// </summary>
    public static SimpleDdgiNearFieldResidualProfile QuarterResolutionPerformance { get; } =
        Performance;

    /// <summary>
    /// Memory-bound complete profile. This is the lowest supported production
    /// scale; admission still accounts for the full-resolution 128-bit receiver
    /// payload, so a device/extent can fail the independent 96 MiB envelope.
    /// </summary>
    public static SimpleDdgiNearFieldResidualProfile EighthResolutionMemoryBound { get; } =
        Performance with { ResolutionScale = 0.125f };
}

public readonly record struct SimpleDdgiNearFieldResidualLayout(
    int SourceWidth,
    int SourceHeight,
    SimpleDdgiNearFieldResidualFormat SourceFormat,
    float TraceResolutionScale,
    int TraceWidth,
    int TraceHeight,
    int FilterIterationCount,
    SimpleDdgiNearFieldSourceProducerMode SourceProducerMode,
    ulong TraceSourceBytes,
    ulong ReceiverPayloadBytes,
    ulong TraceRasterDepthBytes,
    ulong TraceFrameConstantsBytes,
    ulong PreparedDepthFootprintBytes,
    ulong PreparedReceiverPayloadBytes,
    ulong PreparedMotionBytes,
    ulong SourceLuminanceBytes,
    ulong RawCandidateBytes,
    ulong HitMetadataBytes,
    ulong HistoryRadianceBytes,
    ulong MomentBytes,
    ulong HistoryValidityBytes,
    ulong HistoryMetadataBytes,
    ulong HistoryNormalBytes,
    ulong FilterScratchBytes,
    ulong SurfaceTableBytes,
    ulong ActiveTileAndIndirectBytes,
    ulong SchedulerHistoryBytes,
    ulong TileBuffersBytes,
    ulong TelemetryReadbackBytes,
    ulong TotalBytes,
    bool IsValid,
    string FailureReason)
{
    public static SimpleDdgiNearFieldResidualLayout Empty(string reason = "disabled") => new(
        SourceWidth: 0,
        SourceHeight: 0,
        SourceFormat: default,
        TraceResolutionScale: 0.0f,
        TraceWidth: 0,
        TraceHeight: 0,
        FilterIterationCount: 0,
        SourceProducerMode:
            SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster,
        TraceSourceBytes: 0UL,
        ReceiverPayloadBytes: 0UL,
        TraceRasterDepthBytes: 0UL,
        TraceFrameConstantsBytes: 0UL,
        PreparedDepthFootprintBytes: 0UL,
        PreparedReceiverPayloadBytes: 0UL,
        PreparedMotionBytes: 0UL,
        SourceLuminanceBytes: 0UL,
        RawCandidateBytes: 0UL,
        HitMetadataBytes: 0UL,
        HistoryRadianceBytes: 0UL,
        MomentBytes: 0UL,
        HistoryValidityBytes: 0UL,
        HistoryMetadataBytes: 0UL,
        HistoryNormalBytes: 0UL,
        FilterScratchBytes: 0UL,
        SurfaceTableBytes: 0UL,
        ActiveTileAndIndirectBytes: 0UL,
        SchedulerHistoryBytes: 0UL,
        TileBuffersBytes: 0UL,
        TelemetryReadbackBytes: 0UL,
        TotalBytes: 0UL,
        IsValid: false,
        FailureReason: reason);

    public ulong PersistentBytes => checked(
        HistoryRadianceBytes + MomentBytes + HistoryValidityBytes +
        HistoryMetadataBytes + HistoryNormalBytes + SurfaceTableBytes +
        SchedulerHistoryBytes);

    public ulong TransientBytes => checked(
        TraceSourceBytes + ReceiverPayloadBytes + TraceRasterDepthBytes +
        TraceFrameConstantsBytes +
        PreparedDepthFootprintBytes + PreparedReceiverPayloadBytes +
        PreparedMotionBytes + SourceLuminanceBytes + RawCandidateBytes +
        HitMetadataBytes + FilterScratchBytes + ActiveTileAndIndirectBytes +
        TileBuffersBytes + TelemetryReadbackBytes);

    /// <summary>
    /// Physical bytes avoided by reusing RawCandidate as the alternating
    /// filter target after temporal resolve has consumed the candidate.
    /// </summary>
    public ulong AliasedFilterScratchBytes => FilterIterationCount > 0
        ? RawCandidateBytes
        : 0UL;

    public int PhysicalFilterScratchImageCount =>
        FilterIterationCount > 0 ? 1 : 0;
}

public static class SimpleDdgiNearFieldResidualLayoutCompiler
{
    private const int Rgba16FloatBytesPerPixel = 8;
    private const int Rgba32UintBytesPerPixel = 16;
    private const int Depth32BytesPerPixel = 4;
    private const int Rg16FloatBytesPerPixel = 4;
    private const int Rg32FloatBytesPerPixel = 8;
    private const int R16FloatBytesPerPixel = 2;
    private const int HitMetadataBytesPerPixel =
        (int)SimpleDdgiNearFieldResidualGpuAbi.HitMetadataByteCount;
    // R16G16_SFLOAT stores two 16-bit channels, not two 32-bit floats.
    private const int MomentBytesPerPixel = 4;
    // The reset pass fully initializes the current bank, so an eight-bit epoch
    // tag plus seven-bit history length is sufficient in R16_UINT. Receiver
    // geometric/shading octahedra are four signed normalized bytes in R32_UINT.
    private const int HistoryValidityBytesPerPixel = 2;
    private const int HistoryNormalBytesPerPixel = 4;
    private const int TileWidth = 8;
    private const int TileHeight = 8;
    private const int TileRecordBytes =
        (int)SimpleDdgiNearFieldResidualGpuAbi.TileRecordByteCount;

    public static SimpleDdgiNearFieldResidualLayout Compile(
        int sourceWidth,
        int sourceHeight,
        in SimpleDdgiNearFieldResidualProfile profile,
        ulong budgetBytes)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || budgetBytes == 0UL ||
            !float.IsFinite(profile.ResolutionScale) ||
            profile.ResolutionScale < 0.125f || profile.ResolutionScale > 1.0f ||
            profile.SourceFormat !=
                SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat ||
            !Enum.IsDefined(profile.SourceProducerMode) ||
            !Enum.IsDefined(profile.Preset) ||
            !profile.AllowsResolutionScale(profile.ResolutionScale) ||
            !float.IsFinite(profile.FullWeightTraceDistanceMeters) ||
            !float.IsFinite(profile.MaximumTraceDistanceMeters) ||
            profile.FullWeightTraceDistanceMeters <= 0.0f ||
            profile.MaximumTraceDistanceMeters > 16.0f ||
            profile.FullWeightTraceDistanceMeters >
                profile.MaximumTraceDistanceMeters ||
            profile.MaximumRaysPerPixel is < 1 or > 4 ||
            profile.MaximumTraceSteps is < 1 or > 256 ||
            profile.MaximumMipVisits is < 1 or > 32 ||
            profile.BinaryRefinementSteps is < 0 or > 16 ||
            profile.FilterIterationCount is < 0 or > 8 ||
            !IsPowerOfTwo(profile.ImageRowAlignment) ||
            !IsPowerOfTwo(profile.ImageAllocationGranularity))
        {
            return SimpleDdgiNearFieldResidualLayout.Empty("invalid-near-field-profile");
        }

        try
        {
            int traceWidth = Math.Max(1, checked((int)Math.Ceiling(sourceWidth * profile.ResolutionScale)));
            int traceHeight = Math.Max(1, checked((int)Math.Ceiling(sourceHeight * profile.ResolutionScale)));
            const int sourceBpp = Rgba16FloatBytesPerPixel;

            bool traceResolutionSource = profile.SourceProducerMode ==
                SimpleDdgiNearFieldSourceProducerMode.TraceResolutionRaster;
            int sourceAttachmentWidth = traceResolutionSource
                ? traceWidth
                : sourceWidth;
            int sourceAttachmentHeight = traceResolutionSource
                ? traceHeight
                : sourceHeight;
            ulong traceSource = CalculateImageBytes(
                sourceAttachmentWidth, sourceAttachmentHeight, sourceBpp,
                profile.ImageRowAlignment,
                profile.ImageAllocationGranularity);
            // One compact payload replaces four full-resolution MRTs. It owns
            // packed geometric/shading normals, object/material identity, and
            // RGB9E5 receiver diffuse throughput. Stable rays are reconstructed
            // from depth and the per-frame matrix block in the trace pass.
            ulong receiverPayload = CalculateImageBytes(
                sourceAttachmentWidth, sourceAttachmentHeight,
                Rgba32UintBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            ulong traceRasterDepth = traceResolutionSource
                ? CalculateImageBytes(
                    traceWidth, traceHeight, Depth32BytesPerPixel,
                    profile.ImageRowAlignment,
                    profile.ImageAllocationGranularity)
                : 0UL;
            ulong traceFrameConstants = checked(2UL * AlignUp(
                SimpleDdgiNearFieldResidualGpuAbi.TraceFrameConstantsByteCount,
                256u));
            // Prepare chooses one nearest valid full-resolution receiver for
            // each trace footprint and keeps depth/footprint, payload and
            // motion from that exact same source pixel.
            ulong preparedDepthFootprint = CalculateImageBytes(
                traceWidth, traceHeight, Rg32FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            ulong preparedReceiverPayload = CalculateImageBytes(
                traceWidth, traceHeight, Rgba32UintBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            ulong preparedMotion = CalculateImageBytes(
                traceWidth, traceHeight, Rg16FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            // The base level is always present so prepare has one immutable
            // descriptor shape. Profiles with one ray never build or sample
            // additional guiding hierarchy levels.
            ulong sourceLuminance = CalculateImageBytes(
                traceWidth, traceHeight, R16FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            // Raw candidate is radiance + confidence/validity. It is separate
            // from history because invalid samples must not become history.
            ulong rawCandidate = CalculateImageBytes(
                traceWidth, traceHeight, Rgba16FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            // Trace writes the current history metadata bank directly. V13
            // deliberately has no third per-pixel metadata allocation.
            const ulong hitMetadata = 0UL;
            ulong historyRadiance = checked(2UL * CalculateImageBytes(
                traceWidth, traceHeight, Rgba16FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            ulong moments = checked(2UL * CalculateImageBytes(
                traceWidth, traceHeight, MomentBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            // Validity/history length and hit identity must be double-buffered
            // with their radiance/moment peers. A single image would let a
            // temporal write overwrite the prior receiver/hit object and
            // material identity before it is validated.
            ulong historyValidity = checked(2UL * CalculateImageBytes(
                traceWidth, traceHeight, HistoryValidityBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            ulong historyMetadata = checked(2UL * CalculateImageBytes(
                traceWidth, traceHeight, HitMetadataBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            // Temporal normal rejection needs the same previous-frame normal
            // that produced the metadata. Sampling an unspecified current
            // normal after camera/object motion would accept false history.
            ulong historyNormals = checked(2UL * CalculateImageBytes(
                traceWidth, traceHeight, HistoryNormalBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            // Temporal consumes RawCandidate before filtering begins. Reuse it
            // as one ping-pong target and allocate only the other target. The
            // iteration parity is selected so the final estimate always lives
            // in the separate image before frequency separation rewrites Raw.
            ulong filterScratch = profile.FilterIterationCount == 0
                ? 0UL
                : CalculateImageBytes(
                    traceWidth, traceHeight, Rgba16FloatBytesPerPixel,
                    profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            int tileCountX = checked((traceWidth + TileWidth - 1) / TileWidth);
            int tileCountY = checked((traceHeight + TileHeight - 1) / TileHeight);
            uint tileCapacity = checked((uint)(tileCountX * tileCountY));
            ulong activeTileAndIndirect = AlignUp(
                SimpleDdgiNearFieldResidualAdaptiveAbi.ArenaByteCount(
                    tileCapacity),
                profile.ImageAllocationGranularity);
            ulong schedulerBank = AlignUp(checked(
                (ulong)tileCapacity *
                SimpleDdgiNearFieldResidualAdaptiveAbi
                    .SchedulerRecordByteCount),
                profile.ImageAllocationGranularity);
            ulong schedulerHistory = checked(2UL * schedulerBank);
            ulong tileBuffers = AlignUp(checked(
                (ulong)tileCountX * (ulong)tileCountY * TileRecordBytes +
                SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderByteCount),
                profile.ImageAllocationGranularity);
            ulong surfaceTable = AlignUp(checked(
                (ulong)SimpleDdgiNearFieldResidualGpuAbi
                    .MaximumSurfaceTableEntryCount * 16UL *
                (ulong)RenderingConstants.FramesInFlight),
                profile.ImageAllocationGranularity);
            // One host-visible copy per frame slot is the asynchronous
            // completion/telemetry boundary. It is part of the admitted C5
            // envelope so diagnostics never become hidden allocations.
            ulong telemetryReadback = checked(
                tileBuffers * (ulong)RenderingConstants.FramesInFlight);
            ulong total = checked(traceSource + receiverPayload +
                traceRasterDepth + traceFrameConstants +
                preparedDepthFootprint + preparedReceiverPayload + preparedMotion +
                sourceLuminance + rawCandidate + hitMetadata + historyRadiance + moments +
                historyValidity + historyMetadata + historyNormals + filterScratch +
                surfaceTable + activeTileAndIndirect + schedulerHistory +
                tileBuffers + telemetryReadback);
            if (total > budgetBytes)
            {
                return SimpleDdgiNearFieldResidualLayout.Empty(
                    "independent-near-field-memory-budget");
            }

            return new SimpleDdgiNearFieldResidualLayout(
                sourceWidth,
                sourceHeight,
                profile.SourceFormat,
                profile.ResolutionScale,
                traceWidth,
                traceHeight,
                profile.FilterIterationCount,
                profile.SourceProducerMode,
                traceSource,
                receiverPayload,
                traceRasterDepth,
                traceFrameConstants,
                preparedDepthFootprint,
                preparedReceiverPayload,
                preparedMotion,
                sourceLuminance,
                rawCandidate,
                hitMetadata,
                historyRadiance,
                moments,
                historyValidity,
                historyMetadata,
                historyNormals,
                filterScratch,
                surfaceTable,
                activeTileAndIndirect,
                schedulerHistory,
                tileBuffers,
                telemetryReadback,
                total,
                true,
                "valid");
        }
        catch (OverflowException)
        {
            return SimpleDdgiNearFieldResidualLayout.Empty("near-field-layout-overflow");
        }
    }

    private static ulong CalculateImageBytes(
        int width,
        int height,
        int bytesPerPixel,
        uint rowAlignment,
        uint allocationGranularity)
    {
        ulong rowBytes = AlignUp(checked((ulong)width * (ulong)bytesPerPixel), rowAlignment);
        return AlignUp(checked(rowBytes * (ulong)height), allocationGranularity);
    }

    private static ulong AlignUp(ulong value, uint alignment)
    {
        ulong alignmentValue = alignment;
        return checked((value + alignmentValue - 1UL) & ~(alignmentValue - 1UL));
    }

    private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1U)) == 0;
}
