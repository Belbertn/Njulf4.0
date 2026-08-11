using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiNearFieldResidualFormat
{
    R16G16B16A16Sfloat,
    B10G11R11UfloatPack32
}

/// <summary>
/// A complete quality profile. The layout compiler never drops hit metadata,
/// moments, or history simply to make an incomplete profile fit a budget.
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
    public static SimpleDdgiNearFieldResidualProfile HalfResolutionReference { get; } = new(
        ResolutionScale: 0.5f,
        SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
        MaximumTraceSteps: 32,
        MaximumMipVisits: 8,
        BinaryRefinementSteps: 4,
        FilterIterationCount: 2,
        ImageRowAlignment: 256,
        ImageAllocationGranularity: 65_536);

    /// <summary>
    /// Complete lower-resolution profile used when the half-resolution set
    /// cannot fit its independent envelope. No validation/history resource is
    /// removed; only the measured trace/reconstruction resolution changes.
    /// </summary>
    public static SimpleDdgiNearFieldResidualProfile QuarterResolutionPerformance { get; } =
        HalfResolutionReference with { ResolutionScale = 0.25f };

    /// <summary>
    /// Memory-bound complete profile. This is the lowest supported production
    /// scale and exists primarily to keep the 1440p reference-source set inside
    /// the initial 96 MiB experiment envelope.
    /// </summary>
    public static SimpleDdgiNearFieldResidualProfile EighthResolutionMemoryBound { get; } =
        HalfResolutionReference with { ResolutionScale = 0.125f };
}

public readonly record struct SimpleDdgiNearFieldResidualLayout(
    int SourceWidth,
    int SourceHeight,
    SimpleDdgiNearFieldResidualFormat SourceFormat,
    float TraceResolutionScale,
    int TraceWidth,
    int TraceHeight,
    int FilterIterationCount,
    ulong TraceSourceBytes,
    ulong ReceiverPayloadBytes,
    ulong TraceFrameConstantsBytes,
    ulong RawCandidateBytes,
    ulong HitMetadataBytes,
    ulong HistoryRadianceBytes,
    ulong MomentBytes,
    ulong HistoryValidityBytes,
    ulong HistoryMetadataBytes,
    ulong HistoryNormalBytes,
    ulong FilterScratchBytes,
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
        TraceSourceBytes: 0UL,
        ReceiverPayloadBytes: 0UL,
        TraceFrameConstantsBytes: 0UL,
        RawCandidateBytes: 0UL,
        HitMetadataBytes: 0UL,
        HistoryRadianceBytes: 0UL,
        MomentBytes: 0UL,
        HistoryValidityBytes: 0UL,
        HistoryMetadataBytes: 0UL,
        HistoryNormalBytes: 0UL,
        FilterScratchBytes: 0UL,
        TileBuffersBytes: 0UL,
        TelemetryReadbackBytes: 0UL,
        TotalBytes: 0UL,
        IsValid: false,
        FailureReason: reason);

    public ulong PersistentBytes => checked(
        HistoryRadianceBytes + MomentBytes + HistoryValidityBytes +
        HistoryMetadataBytes + HistoryNormalBytes);

    public ulong TransientBytes => checked(
        TraceSourceBytes + ReceiverPayloadBytes + TraceFrameConstantsBytes +
        RawCandidateBytes + HitMetadataBytes + FilterScratchBytes +
        TileBuffersBytes + TelemetryReadbackBytes);
}

public static class SimpleDdgiNearFieldResidualLayoutCompiler
{
    private const int Rgba16FloatBytesPerPixel = 8;
    private const int Rgba32UintBytesPerPixel = 16;
    private const int R11G11B10BytesPerPixel = 4;
    private const int HitMetadataBytesPerPixel =
        (int)SimpleDdgiNearFieldResidualGpuAbi.HitMetadataByteCount;
    // R16G16_SFLOAT stores two 16-bit channels, not two 32-bit floats.
    private const int MomentBytesPerPixel = 4;
    private const int HistoryValidityBytesPerPixel = 4;
    private const int HistoryNormalBytesPerPixel = Rgba16FloatBytesPerPixel;
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
            (profile.SourceFormat !=
                SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat &&
             profile.SourceFormat !=
                SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32) ||
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
            int sourceBpp = profile.SourceFormat switch
            {
                SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat => Rgba16FloatBytesPerPixel,
                SimpleDdgiNearFieldResidualFormat.B10G11R11UfloatPack32 => R11G11B10BytesPerPixel,
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };

            ulong traceSource = CalculateImageBytes(
                sourceWidth, sourceHeight, sourceBpp, profile.ImageRowAlignment,
                profile.ImageAllocationGranularity);
            // One compact payload replaces four full-resolution MRTs. It owns
            // packed geometric/shading normals, object/material identity, and
            // RGB9E5 receiver diffuse throughput. Stable rays are reconstructed
            // from depth and the per-frame matrix block in the trace pass.
            ulong receiverPayload = CalculateImageBytes(
                sourceWidth, sourceHeight, Rgba32UintBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            ulong traceFrameConstants = checked(2UL * AlignUp(
                SimpleDdgiNearFieldResidualGpuAbi.TraceFrameConstantsByteCount,
                256u));
            // Raw candidate is radiance + confidence/validity. It is separate
            // from history because invalid samples must not become history.
            ulong rawCandidate = CalculateImageBytes(
                traceWidth, traceHeight, Rgba16FloatBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
            ulong hitMetadata = CalculateImageBytes(
                traceWidth, traceHeight, HitMetadataBytesPerPixel,
                profile.ImageRowAlignment, profile.ImageAllocationGranularity);
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
            ulong filterScratch = profile.FilterIterationCount == 0
                ? 0UL
                : checked(2UL * CalculateImageBytes(
                    traceWidth, traceHeight, Rgba16FloatBytesPerPixel,
                    profile.ImageRowAlignment, profile.ImageAllocationGranularity));
            int tileCountX = checked((traceWidth + TileWidth - 1) / TileWidth);
            int tileCountY = checked((traceHeight + TileHeight - 1) / TileHeight);
            ulong tileBuffers = AlignUp(checked(
                (ulong)tileCountX * (ulong)tileCountY * TileRecordBytes +
                SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderByteCount),
                profile.ImageAllocationGranularity);
            // One host-visible copy per frame slot is the asynchronous
            // completion/telemetry boundary. It is part of the admitted C5
            // envelope so diagnostics never become hidden allocations.
            ulong telemetryReadback = checked(
                tileBuffers * (ulong)RenderingConstants.FramesInFlight);
            ulong total = checked(traceSource + receiverPayload + traceFrameConstants +
                rawCandidate + hitMetadata + historyRadiance + moments +
                historyValidity + historyMetadata + historyNormals + filterScratch +
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
                traceSource,
                receiverPayload,
                traceFrameConstants,
                rawCandidate,
                hitMetadata,
                historyRadiance,
                moments,
                historyValidity,
                historyMetadata,
                historyNormals,
                filterScratch,
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
