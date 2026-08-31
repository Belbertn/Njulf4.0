using System;
using Njulf.Rendering.Memory;

namespace Njulf.Rendering.Data;

/// <summary>
/// Per-tile work rate for the temporal Simple-DDGI receiver cache. Fractions
/// are relative to the existing 12x12 exact gather lattice.
/// </summary>
public enum SimpleDdgiReceiverCacheRate : uint
{
    Full = 0,
    Half = 1,
    Quarter = 2,
    Reuse = 3
}

/// <summary>
/// Frozen GPU layout and bounded-list contract shared by the classify,
/// compact, gather, and dirty-resolve stages.
/// </summary>
public static class SimpleDdgiReceiverCacheAdaptiveAbi
{
    public const uint Version = 3u;
    public const uint SchedulingTileScale = 8u;
    public const uint WorkgroupInvocationCount = 64u;
    public const ulong MetadataEntryBytes = 16u;
    public const ulong TileScheduleEntryBytes = 16u;
    public const ulong GatherWorkEntryBytes = 8u;
    public const ulong ResolveTileEntryBytes = 8u;
    public const ulong GatherStampEntryBytes = 4u;
    public const ulong ControlBytes = 96u;

    public const uint GatherCountWord = 0u;
    public const uint ResolveCountWord = 1u;
    public const uint OverflowFlagsWord = 2u;
    public const uint FrameStampWord = 3u;
    public const uint GatherIndirectWord = 4u;
    public const uint ResolveIndirectWord = 7u;
    public const uint AcceptedEntryCountWord = 11u;
    public const uint RejectedEntryCountWord = 12u;
    public const uint FullTileCountWord = 13u;
    public const uint HalfTileCountWord = 14u;
    public const uint QuarterTileCountWord = 15u;
    public const uint ReuseTileCountWord = 16u;
    public const uint MissingFeedbackCountWord = 17u;
    public const uint MissingFeedbackIndirectWord = 18u;
    // Words 21..23 are deliberately inside the frozen 96-byte control block.
    // They expose publication-reuse effectiveness without growing any GPU
    // allocation or changing the indirect argument offsets above.
    public const uint PublicationGenerationHitCountWord = 21u;
    public const uint PublicationDirtyInvalidationCountWord = 22u;
    public const uint PublicationSkippedTileCountWord = 23u;

    public const ulong GatherIndirectByteOffset =
        GatherIndirectWord * sizeof(uint);
    public const ulong ResolveIndirectByteOffset =
        ResolveIndirectWord * sizeof(uint);
    public const ulong MissingFeedbackIndirectByteOffset =
        MissingFeedbackIndirectWord * sizeof(uint);

    public static uint DivideRoundUp(uint value, uint divisor)
    {
        if (divisor == 0u)
            throw new ArgumentOutOfRangeException(nameof(divisor));
        return value == 0u ? 0u : checked((value + divisor - 1u) / divisor);
    }

    public static uint TileWidth(uint cacheWidth) =>
        DivideRoundUp(cacheWidth, SchedulingTileScale);

    public static uint TileHeight(uint cacheHeight) =>
        DivideRoundUp(cacheHeight, SchedulingTileScale);

    public static ulong RequiredMetadataBytes(uint cacheWidth, uint cacheHeight) =>
        checked((ulong)cacheWidth * cacheHeight * MetadataEntryBytes);

    public static ulong RequiredTileScheduleBytes(
        uint cacheWidth,
        uint cacheHeight) =>
        checked((ulong)TileWidth(cacheWidth) * TileHeight(cacheHeight) *
                TileScheduleEntryBytes);

    public static ulong RequiredGatherWorkBytes(
        uint gatherWidth,
        uint gatherHeight) =>
        checked((ulong)gatherWidth * gatherHeight * GatherWorkEntryBytes);

    public static ulong RequiredResolveTileBytes(
        uint cacheWidth,
        uint cacheHeight) =>
        checked((ulong)TileWidth(cacheWidth) * TileHeight(cacheHeight) *
                ResolveTileEntryBytes);

    public static ulong RequiredGatherStampBytes(
        uint gatherWidth,
        uint gatherHeight) =>
        checked((ulong)gatherWidth * gatherHeight * GatherStampEntryBytes);

    public static ulong RequiredMissingPrefixBytes(
        uint gatherWidth,
        uint gatherHeight) =>
        checked((ulong)DivideRoundUp(
            checked(gatherWidth * gatherHeight),
            WorkgroupInvocationCount) * sizeof(uint));

    /// <summary>
    /// The compactor emits each gather coordinate and each resolve tile at
    /// most once. Full canonical capacities therefore make overflow
    /// impossible for any well-formed dispatch dimensions.
    /// </summary>
    public static bool CapacitiesCoverCanonicalWork(
        uint cacheWidth,
        uint cacheHeight,
        uint gatherWidth,
        uint gatherHeight,
        ulong gatherWorkBytes,
        ulong resolveTileBytes) =>
        gatherWorkBytes >= RequiredGatherWorkBytes(gatherWidth, gatherHeight) &&
        resolveTileBytes >= RequiredResolveTileBytes(cacheWidth, cacheHeight);
}

/// <summary>
/// Fragment-visible publication contract. Region zero is intentionally the
/// whole view until DDGI exposes an authenticated atlas-to-screen dirty map.
/// This conservative granularity may invalidate too much work, but can never
/// retain data from a changed region.
/// </summary>
public static class SimpleDdgiReceiverPublicationAbi
{
    public const ulong ByteCount = 8u;
    public const ulong GenerationByteOffset = 0u;
    public const ulong ChangedRegionsByteOffset = 4u;
    public const uint GlobalRegionBit = 1u;
}

public readonly record struct SimpleDdgiReceiverCacheRateInput(
    bool HistoryValid,
    bool EpochChanged,
    uint RejectedEntryCount,
    float MaximumRelativeDepthGradient,
    float MinimumNormalDot,
    float MaximumMotion,
    float MinimumConfidence,
    uint MaximumAge);

public readonly record struct SimpleDdgiReceiverCacheRateThresholds(
    float FullDepthGradient,
    float FullNormalDot,
    float FullMotion,
    float HalfDepthGradient,
    float HalfNormalDot,
    float HalfMotion,
    float QuarterDepthGradient,
    float QuarterNormalDot,
    float QuarterMotion,
    float MinimumReuseConfidence,
    uint MaximumHistoryAge)
{
    public static SimpleDdgiReceiverCacheRateThresholds ForPreset(
        RenderQualityPreset preset) => preset switch
    {
        RenderQualityPreset.Low => new(
            0.10f, 0.82f, 0.050f,
            0.060f, 0.90f, 0.025f,
            0.025f, 0.96f, 0.010f,
            0.55f, 24u),
        RenderQualityPreset.Medium => new(
            0.085f, 0.85f, 0.040f,
            0.050f, 0.92f, 0.020f,
            0.020f, 0.97f, 0.008f,
            0.62f, 16u),
        RenderQualityPreset.Ultra => new(
            0.055f, 0.90f, 0.025f,
            0.030f, 0.95f, 0.012f,
            0.012f, 0.985f, 0.004f,
            0.75f, 8u),
        RenderQualityPreset.High or RenderQualityPreset.DdgiHigh => new(
            0.070f, 0.88f, 0.030f,
            0.040f, 0.94f, 0.015f,
            0.016f, 0.98f, 0.006f,
            0.70f, 10u),
        _ => ForPreset(RenderQualityPreset.DdgiHigh)
    };
}

public static class SimpleDdgiReceiverCacheRateSelector
{
    public static SimpleDdgiReceiverCacheRate Select(
        in SimpleDdgiReceiverCacheRateInput input,
        in SimpleDdgiReceiverCacheRateThresholds thresholds)
    {
        if (!input.HistoryValid || input.EpochChanged ||
            input.RejectedEntryCount != 0u ||
            input.MaximumAge >= thresholds.MaximumHistoryAge ||
            !Finite(input.MaximumRelativeDepthGradient) ||
            !Finite(input.MinimumNormalDot) ||
            !Finite(input.MaximumMotion) ||
            !Finite(input.MinimumConfidence))
        {
            return SimpleDdgiReceiverCacheRate.Full;
        }

        if (input.MaximumRelativeDepthGradient >= thresholds.FullDepthGradient ||
            input.MinimumNormalDot <= thresholds.FullNormalDot ||
            input.MaximumMotion >= thresholds.FullMotion)
        {
            return SimpleDdgiReceiverCacheRate.Full;
        }

        if (input.MaximumRelativeDepthGradient >= thresholds.HalfDepthGradient ||
            input.MinimumNormalDot <= thresholds.HalfNormalDot ||
            input.MaximumMotion >= thresholds.HalfMotion)
        {
            return SimpleDdgiReceiverCacheRate.Half;
        }

        if (input.MaximumRelativeDepthGradient >=
                thresholds.QuarterDepthGradient ||
            input.MinimumNormalDot <= thresholds.QuarterNormalDot ||
            input.MaximumMotion >= thresholds.QuarterMotion ||
            input.MinimumConfidence < thresholds.MinimumReuseConfidence)
        {
            return SimpleDdgiReceiverCacheRate.Quarter;
        }

        return SimpleDdgiReceiverCacheRate.Reuse;
    }

    private static bool Finite(float value) => float.IsFinite(value);
}

/// <summary>
/// CPU-owned invalidation identity. Camera translation/rotation are omitted:
/// corrected motion vectors own ordinary reprojection. Projection, content,
/// material, lighting, DDGI-source, mode, and physical-ownership changes fail
/// closed. A compatible toroidal scroll advances the logical volume-table
/// generation while retaining physical history, so that diagnostic epoch is
/// deliberately excluded from compatibility.
/// </summary>
public readonly record struct SimpleDdgiReceiverCacheHistoryIdentity(
    uint CacheWidth,
    uint CacheHeight,
    uint GatherWidth,
    uint GatherHeight,
    int ProjectionM11Bits,
    int ProjectionM22Bits,
    int ProjectionM33Bits,
    int ProjectionM43Bits,
    ulong CameraCutSerial,
    ulong SceneContentRevision,
    uint MaterialRevision,
    uint VolumeResourceGeneration,
    uint TransportTopologyGeneration,
    uint SourceLightingGeneration,
    uint SourceCohortGeneration,
    uint PublishedRadiometricGeneration,
    uint ReceiverPublicationGeneration,
    uint TransportGeneration,
    uint PublishedPropagationGeneration,
    uint LivePropagationSourceGeneration,
    uint EmissiveSourceRevision,
    ulong VfxMacroRevision,
    SimpleDdgiReceiverCacheMode Mode,
    uint ResourceGeneration)
{
    public static bool IsImmediatelyPrevious(ulong current, ulong previous) =>
        current != 0UL && previous != 0UL &&
        previous != ulong.MaxValue && previous + 1UL == current;

    public bool IsHistoryCompatibleWith(
        in SimpleDdgiReceiverCacheHistoryIdentity other) =>
        this with
        {
            VolumeResourceGeneration = other.VolumeResourceGeneration
        } == other;
}

/// <summary>
/// Complete receiver-lattice publication identity. Ordinary camera motion is
/// excluded because motion/depth/normal/disocclusion validation owns it;
/// projection changes and explicit cuts remain hard discontinuities.
/// </summary>
public readonly record struct SimpleDdgiReceiverPublicationIdentity(
    uint CacheWidth,
    uint CacheHeight,
    uint GatherWidth,
    uint GatherHeight,
    int ProjectionM11Bits,
    int ProjectionM22Bits,
    int ProjectionM33Bits,
    int ProjectionM43Bits,
    ulong CameraCutSerial,
    ulong SceneContentRevision,
    uint MaterialRevision,
    uint VolumeResourceGeneration,
    uint TransportTopologyGeneration,
    uint SourceLightingGeneration,
    uint SourceCohortGeneration,
    uint PublishedRadiometricGeneration,
    uint ReceiverPublicationGeneration,
    uint TransportGeneration,
    uint PublishedPropagationGeneration,
    uint LivePropagationSourceGeneration,
    uint EmissiveSourceRevision,
    ulong VfxMacroRevision,
    SimpleDdgiReceiverCacheMode Mode,
    uint ResourceGeneration);

public readonly record struct SimpleDdgiReceiverPublicationUpdate(
    uint Stamp,
    uint ChangedRegionMask,
    bool Enabled,
    bool IdentityChanged,
    bool ResetDependentCache,
    bool Wrapped);

/// <summary>
/// CPU-owned monotonic epoch. The identity is observed even while the feature
/// is disabled, and re-enabling always requests a dependent-cache clear, so a
/// coincidental stamp can never resurrect data written by the baseline path.
/// </summary>
public sealed class SimpleDdgiReceiverPublicationTracker
{
    private SimpleDdgiReceiverPublicationIdentity _identity;
    private bool _hasIdentity;
    private bool _enabledLastUpdate;
    private uint _generation;

    public ulong StableIdentityHitCount { get; private set; }
    public ulong DirtyIdentityCount { get; private set; }
    public ulong WrapResetCount { get; private set; }
    public uint Generation => _generation;

    public SimpleDdgiReceiverPublicationUpdate Update(
        in SimpleDdgiReceiverPublicationIdentity identity,
        bool enabled,
        uint fallbackStamp,
        bool forceDirty = false)
    {
        if (fallbackStamp == 0u)
            fallbackStamp = 1u;

        bool firstIdentity = !_hasIdentity;
        bool identityChanged = firstIdentity || forceDirty ||
            _identity != identity;
        bool enabling = enabled && !_enabledLastUpdate;
        bool wrapped = false;
        if (identityChanged || enabling)
        {
            _generation = NextGeneration(_generation, out wrapped);
            _identity = identity;
            _hasIdentity = true;
            DirtyIdentityCount++;
            if (wrapped)
                WrapResetCount++;
        }
        else if (enabled)
        {
            StableIdentityHitCount++;
        }

        _enabledLastUpdate = enabled;
        return new SimpleDdgiReceiverPublicationUpdate(
            enabled ? _generation : fallbackStamp,
            enabled && (identityChanged || enabling)
                ? SimpleDdgiReceiverPublicationAbi.GlobalRegionBit
                : 0u,
            enabled,
            identityChanged,
            enabled && (firstIdentity || enabling || wrapped),
            wrapped);
    }

    public void Reset()
    {
        _identity = default;
        _hasIdentity = false;
        _enabledLastUpdate = false;
        _generation = 0u;
    }

    public static uint NextGeneration(uint current, out bool wrapped)
    {
        wrapped = current == uint.MaxValue;
        uint next = wrapped ? 1u : current + 1u;
        return next == 0u ? 1u : next;
    }
}

/// <summary>
/// Read-only publication for consumers such as C5. Handles are valid only for
/// the stated frame serial and resource generation; consumers must reject a
/// mismatched token instead of retaining it.
/// </summary>
public readonly record struct SimpleDdgiReceiverCacheFrameToken(
    ulong FrameSerial,
    uint ResourceGeneration,
    int FrameIndex,
    uint CacheWidth,
    uint CacheHeight,
    uint TileWidth,
    uint TileHeight,
    BufferHandle MetadataBuffer,
    BufferHandle TileScheduleBuffer,
    BufferHandle DirtyResolveTileBuffer,
    BufferHandle ControlBuffer)
{
    public static SimpleDdgiReceiverCacheFrameToken Unavailable => default;

    public bool IsAvailable => FrameSerial != 0UL &&
        ResourceGeneration != 0u &&
        FrameIndex is 0 or 1 &&
        CacheWidth != 0u && CacheHeight != 0u &&
        TileWidth != 0u && TileHeight != 0u &&
        MetadataBuffer.IsValid && TileScheduleBuffer.IsValid &&
        DirtyResolveTileBuffer.IsValid && ControlBuffer.IsValid;

    public bool Matches(ulong frameSerial, uint resourceGeneration) =>
        IsAvailable && FrameSerial == frameSerial &&
        ResourceGeneration == resourceGeneration;
}

public readonly record struct SimpleDdgiReceiverCacheAdaptiveCounters(
    int ReadbackValid,
    uint GatherWorkCount,
    uint MissingFeedbackWorkCount,
    uint ResolveTileCount,
    uint OverflowFlags,
    uint AcceptedEntryCount,
    uint RejectedEntryCount,
    uint FullTileCount,
    uint HalfTileCount,
    uint QuarterTileCount,
    uint ReuseTileCount,
    uint PublicationGenerationHitCount,
    uint PublicationDirtyInvalidationCount,
    uint PublicationSkippedTileCount)
{
    public static SimpleDdgiReceiverCacheAdaptiveCounters Unavailable => default;
}
