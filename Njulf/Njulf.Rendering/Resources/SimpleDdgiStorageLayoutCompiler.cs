using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>Why a volume did or did not receive FP16 source distance.</summary>
public enum SimpleDdgiDistancePackingDecision : uint
{
    LegacyMode = 0,
    Eligible = 1,
    NonFiniteRange = 2,
    HalfRangeExceeded = 3,
    HitPointOffsetError = 4,
    ArchitecturalThicknessError = 5,
    SyntheticBoundaryError = 6
}

/// <summary>One physical source-cache region request.</summary>
public readonly record struct SimpleDdgiTransportCacheRegionRequest(
    int VolumeIndex,
    string Identity,
    int SourceOrdinal,
    int PhysicalFirstProbe,
    int PhysicalProbeCount,
    int RaysPerProbe,
    int GridCountX,
    int GridCountY,
    int GridCountZ,
    float Spacing,
    float ArchitecturalThickness,
    SimpleDdgiStoragePackingMode PackingMode)
{
    /// <summary>
    /// Exact shader trace limit. A null value requests the compatibility
    /// derivation from spacing and grid dimensions and remains strict-JSON-safe
    /// when the request is embedded in a capture report.
    /// </summary>
    public float? MaximumTraceDistance { get; init; }
    /// <summary>
    /// Uses a page-local dense hot-header array followed by a conditional hit
    /// sidecar. Total capacity stays fixed, while miss/backface solve paths do
    /// not fetch surface response words.
    /// </summary>
    public bool UseHotColdLayout { get; init; }
}

/// <summary>Authoritative byte and address contract for one volume cache region.</summary>
public readonly record struct SimpleDdgiTransportCacheRegion(
    int VolumeIndex,
    string Identity,
    int SourceOrdinal,
    int PhysicalFirstProbe,
    int PhysicalProbeCount,
    int RaysPerProbe,
    ulong BaseWord,
    int StrideWords,
    ulong ByteCount,
    ulong AlignmentPaddingBytes,
    SimpleDdgiTransportCacheFormat Format,
    float MaximumTraceDistance,
    float WorstCaseHalfUlp,
    float MaximumDecodedDistanceError,
    SimpleDdgiDistancePackingDecision DistancePackingDecision)
{
    public ulong EndWord => checked(BaseWord + ByteCount / sizeof(uint));
    public bool UsesFp16Distance => Format == SimpleDdgiTransportCacheFormat.Compact24;
    public bool UsesHotColdLayout { get; init; }
}

/// <summary>
/// Complete mixed-stride source-cache plan. The Vulkan allocation, volume
/// upload, diagnostics, and CPU tests consume this same instance.
/// </summary>
public sealed record SimpleDdgiStorageLayout(
    SimpleDdgiStoragePackingMode PackingMode,
    SimpleDdgiStorageAbiVersion AbiVersion,
    uint DirectionCodebookVersion,
    IReadOnlyList<SimpleDdgiTransportCacheRegion> Regions,
    ulong SourceCacheBytes,
    ulong LegacyBytes,
    ulong Compact28Bytes,
    ulong Compact24Bytes,
    ulong AlignmentPaddingBytes,
    int LegacyRayCount,
    int Compact28RayCount,
    int Compact24RayCount,
    ulong Fingerprint)
{
    public static SimpleDdgiStorageLayout Empty(
        SimpleDdgiStoragePackingMode mode = SimpleDdgiStoragePackingMode.Packed) =>
        new(
            mode.Sanitize(),
            mode.UsesPackedCache()
                ? SimpleDdgiStorageAbiVersion.Packed
                : SimpleDdgiStorageAbiVersion.Legacy,
            SimpleDdgiStorageLayoutCompiler.DirectionCodebookVersion,
            Array.Empty<SimpleDdgiTransportCacheRegion>(),
            SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes,
            0UL,
            0UL,
            0UL,
            SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes,
            0,
            0,
            0,
            SimpleDdgiStorageLayoutCompiler.InitialFingerprint);

    public SimpleDdgiTransportCacheRegion? FindVolume(int volumeIndex)
    {
        foreach (SimpleDdgiTransportCacheRegion region in Regions)
        {
            if (region.VolumeIndex == volumeIndex)
                return region;
        }

        return null;
    }
}

/// <summary>
/// Capture-ready storage and mirror contract. All byte fields come from the
/// same layouts used for allocation and shader metadata; no consumer derives
/// one resource by subtracting another.
/// </summary>
public sealed record SimpleDdgiStorageDiagnostics(
    bool IsAvailable,
    SimpleDdgiStoragePackingMode PackingMode,
    SimpleDdgiStorageAbiVersion AbiVersion,
    uint DirectionCodebookVersion,
    string CanonicalIrradianceFormat,
    string CanonicalVisibilityFormat,
    ulong CanonicalIrradianceBytes,
    ulong CanonicalVisibilityBytes,
    ulong SourceCacheBytes,
    ulong SourceCacheLegacyBytes,
    ulong SourceCacheCompact28Bytes,
    ulong SourceCacheCompact24Bytes,
    ulong SourceCacheAlignmentBytes,
    int SourceCacheLegacyRayCount,
    int SourceCacheCompact28RayCount,
    int SourceCacheCompact24RayCount,
    int Fp16DistanceEligibleVolumeCount,
    int Fp16DistanceEligibleProbeCount,
    int Fp32DistanceVolumeCount,
    int Fp32DistanceProbeCount,
    ulong RayScratchStrideBytes,
    ulong RayScratchBytes,
    SimpleDdgiSampledAtlasCoverageMode MirrorCoverageMode,
    int MirrorRequestedProbeCount,
    int MirrorEligibleProbeCount,
    int MirrorAdmittedProbeCount,
    int MirrorProvisionedProbeCount,
    ulong MirrorIrradianceBytes,
    ulong MirrorVisibilityBytes,
    ulong MirrorTotalBytes,
    ulong MirrorAllocatedBytes,
    IReadOnlyList<string> MirrorExcludedIdentities,
    IReadOnlyList<SimpleDdgiTransportCacheRegion> CacheRegions,
    ulong StorageLayoutFingerprint,
    ulong MirrorLayoutFingerprint,
    ulong MirrorAllocationGeneration,
    string MirrorFallbackReason)
{
    public SimpleDdgiStorageValidationCounters ValidationCounters { get; init; } =
        SimpleDdgiStorageValidationCounters.Empty;
    public int SourceCacheHotColdVolumeCount { get; init; }
    public ulong SourceCacheHotHeaderCapacityBytes { get; init; }
    public ulong SourceCacheConditionalPayloadCapacityBytes { get; init; }
    public ulong SourceCacheEstimatedSolveReadBytes { get; init; }
    public float SourceCacheMeasuredColdExitFraction { get; init; }
    public string SourceCacheLayoutAdmissionReason { get; init; } = string.Empty;
    public SimpleDdgiSourceCacheLayoutMode SourceCacheRequestedLayoutMode { get; init; } =
        SimpleDdgiSourceCacheLayoutMode.FixedRecord;
    public SimpleDdgiSourceCacheLayoutMode SourceCacheEffectiveLayoutMode { get; init; } =
        SimpleDdgiSourceCacheLayoutMode.FixedRecord;
    public ulong SourceCacheAdmissionLayoutIdentity { get; init; }
    public bool SourceCacheAdmissionHasCompletedSample { get; init; }
    public ulong SourceCacheAdmissionSampleFrameSerial { get; init; }

    public static SimpleDdgiStorageDiagnostics Unavailable { get; } = new(
        false,
        SimpleDdgiStoragePackingMode.Legacy,
        SimpleDdgiStorageAbiVersion.Legacy,
        0u,
        "unavailable",
        "unavailable",
        0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL,
        0, 0, 0, 0, 0, 0, 0, 0UL, 0UL,
        SimpleDdgiSampledAtlasCoverageMode.Disabled,
        0, 0, 0, 0,
        0UL, 0UL, 0UL, 0UL,
        Array.Empty<string>(),
        Array.Empty<SimpleDdgiTransportCacheRegion>(),
        0UL, 0UL, 0UL,
        "unavailable");
}

/// <summary>Pure compiler for variable-stride cache regions and FP16-distance admission.</summary>
public static class SimpleDdgiStorageLayoutCompiler
{
    public const uint DirectionCodebookVersion = SimpleDdgiDirectionCodebook.Version;
    public const int RegionAlignmentWords = 4;
    public const ulong RegionAlignmentBytes = RegionAlignmentWords * sizeof(uint);
    public const int LegacyStrideWords = 9;
    public const int Compact28StrideWords = 7;
    public const int Compact24StrideWords = 6;
    public const int LegacyScratchStrideWords = 8;
    public const int PackedScratchStrideWords = 5;
    public const float MaximumFiniteHalf = 65_504.0f;
    internal const ulong InitialFingerprint = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;

    /// <summary>
    /// Resolves the first cache word owned by one physical probe. Zero is
    /// reserved as the invalid queue sentinel, so the returned address is
    /// encoded as the real word offset plus one. Ray-level addressing remains
    /// the responsibility of the shared CPU/GLSL cache-address contract.
    /// </summary>
    public static bool TryResolveProbeCacheBaseWordPlusOne(
        in SimpleDdgiTransportCacheRegion region,
        uint physicalProbeIndex,
        out uint cacheProbeBaseWordPlusOne)
    {
        cacheProbeBaseWordPlusOne = 0u;
        if (region.PhysicalFirstProbe < 0 ||
            region.PhysicalProbeCount <= 0 ||
            region.RaysPerProbe <= 0 ||
            region.StrideWords != region.Format.WordCount() ||
            region.StrideWords <= 0)
        {
            return false;
        }

        ulong physicalFirst = checked((uint)region.PhysicalFirstProbe);
        ulong physical = physicalProbeIndex;
        ulong physicalCount = checked((uint)region.PhysicalProbeCount);
        if (physical < physicalFirst || physical - physicalFirst >= physicalCount)
            return false;

        try
        {
            ulong localProbeIndex = physical - physicalFirst;
            ulong wordsPerProbe = checked(
                (ulong)(uint)region.RaysPerProbe *
                (ulong)(uint)region.StrideWords);
            ulong probeBaseWord = checked(
                region.BaseWord + localProbeIndex * wordsPerProbe);
            ulong probeEndWord = checked(probeBaseWord + wordsPerProbe);
            // Bit 31 of the queued one-based address is a transaction-private
            // cache-miss handshake used by the split trace kernels.
            if (probeEndWord > region.EndWord ||
                probeBaseWord + 1UL > 0x7fff_ffffUL)
                return false;

            cacheProbeBaseWordPlusOne = checked((uint)(probeBaseWord + 1UL));
            return cacheProbeBaseWordPlusOne != 0u;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static SimpleDdgiStorageLayout Compile(
        IReadOnlyList<SimpleDdgiTransportCacheRegionRequest> requests)
    {
        if (requests == null)
            throw new ArgumentNullException(nameof(requests));
        if (requests.Count == 0)
            return SimpleDdgiStorageLayout.Empty();

        SimpleDdgiStoragePackingMode mode = requests[0].PackingMode.Sanitize();
        var regions = new List<SimpleDdgiTransportCacheRegion>(requests.Count);
        ulong cursorWords = 0UL;
        ulong legacyBytes = 0UL;
        ulong compact28Bytes = 0UL;
        ulong compact24Bytes = 0UL;
        ulong paddingBytes = 0UL;
        int legacyRays = 0;
        int compact28Rays = 0;
        int compact24Rays = 0;
        ulong fingerprint = Add(InitialFingerprint, (uint)mode);
        fingerprint = Add(fingerprint, DirectionCodebookVersion);

        var seenVolumes = new HashSet<int>();
        var seenSourceOrdinals = new HashSet<int>();
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var physicalRanges = new List<(ulong First, ulong End, int VolumeIndex)>(
            requests.Count);
        foreach (SimpleDdgiTransportCacheRegionRequest request in requests)
        {
            if (!seenVolumes.Add(request.VolumeIndex))
                throw new ArgumentException($"Duplicate Simple-DDGI cache region for volume {request.VolumeIndex}.", nameof(requests));
            if (request.VolumeIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(requests), "Volume indices must be non-negative.");
            if (request.SourceOrdinal < 0 ||
                !seenSourceOrdinals.Add(request.SourceOrdinal))
            {
                throw new ArgumentException(
                    $"Simple-DDGI source ordinal {request.SourceOrdinal} is invalid or duplicated.",
                    nameof(requests));
            }
            string identity = request.Identity ?? string.Empty;
            if (string.IsNullOrWhiteSpace(identity) ||
                !seenIdentities.Add(identity))
            {
                throw new ArgumentException(
                    $"Simple-DDGI cache identity '{identity}' is empty or duplicated.",
                    nameof(requests));
            }
            if (request.PhysicalFirstProbe < 0 || request.PhysicalProbeCount < 0)
                throw new ArgumentOutOfRangeException(nameof(requests), "Physical probe ranges must be non-negative.");
            if (request.GridCountX <= 0 || request.GridCountY <= 0 ||
                request.GridCountZ <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    "Simple-DDGI cache grid dimensions must be positive.");
            }
            if (!float.IsFinite(request.Spacing) || request.Spacing <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    "Simple-DDGI cache spacing must be finite and positive.");
            }
            if (request.RaysPerProbe < 1 ||
                request.RaysPerProbe > GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe)
            {
                throw new ArgumentOutOfRangeException(nameof(requests), "Ray capacity is outside the supported Simple-DDGI range.");
            }
            if (request.PackingMode.Sanitize() != mode)
                throw new ArgumentException("One storage layout cannot mix global packing modes.", nameof(requests));
            if (request.UseHotColdLayout && !mode.UsesPackedCache())
            {
                throw new ArgumentException(
                    "The hot-header source-cache layout requires packed storage.",
                    nameof(requests));
            }

            ulong physicalFirst = checked((ulong)request.PhysicalFirstProbe);
            ulong physicalEnd = checked(
                physicalFirst + (ulong)request.PhysicalProbeCount);
            if (physicalEnd > (ulong)uint.MaxValue + 1UL)
            {
                throw new OverflowException(
                    $"Simple-DDGI physical range for volume {request.VolumeIndex} exceeds the shader uint address space.");
            }
            if (request.PhysicalProbeCount > 0)
            {
                foreach ((ulong first, ulong end, int volumeIndex) in physicalRanges)
                {
                    if (physicalFirst < end && first < physicalEnd)
                    {
                        throw new ArgumentException(
                            $"Simple-DDGI physical ranges for volumes {volumeIndex} and {request.VolumeIndex} overlap.",
                            nameof(requests));
                    }
                }
                physicalRanges.Add((physicalFirst, physicalEnd, request.VolumeIndex));
            }

            float maximumTraceDistance = ResolveMaximumTraceDistance(request);
            float halfUlp = ResolveWorstCaseHalfUlp(maximumTraceDistance);
            float maximumDecodedError = halfUlp * 0.5f;
            SimpleDdgiDistancePackingDecision distanceDecision =
                ResolveDistancePackingDecision(
                    request,
                    maximumTraceDistance,
                    halfUlp,
                    maximumDecodedError);
            SimpleDdgiTransportCacheFormat format = mode.UsesPackedCache()
                ? distanceDecision == SimpleDdgiDistancePackingDecision.Eligible
                    ? SimpleDdgiTransportCacheFormat.Compact24
                    : SimpleDdgiTransportCacheFormat.Compact28
                : SimpleDdgiTransportCacheFormat.Legacy36;
            int strideWords = format.WordCount();

            ulong alignedCursor = AlignWords(cursorWords, RegionAlignmentWords);
            ulong regionPadding = checked((alignedCursor - cursorWords) * sizeof(uint));
            ulong rayCount = checked(
                (ulong)request.PhysicalProbeCount * (ulong)request.RaysPerProbe);
            ulong bytes = checked(rayCount * (ulong)strideWords * sizeof(uint));
            ulong endWord = checked(alignedCursor + bytes / sizeof(uint));
            if (alignedCursor > uint.MaxValue ||
                endWord > (ulong)uint.MaxValue + 1UL)
            {
                throw new OverflowException(
                    "Simple-DDGI source-cache layout exceeds the shader uint word address space.");
            }
            int boundedRayCount = checked((int)Math.Min(rayCount, int.MaxValue));
            var region = new SimpleDdgiTransportCacheRegion(
                request.VolumeIndex,
                identity,
                request.SourceOrdinal,
                request.PhysicalFirstProbe,
                request.PhysicalProbeCount,
                request.RaysPerProbe,
                alignedCursor,
                strideWords,
                bytes,
                regionPadding,
                format,
                maximumTraceDistance,
                halfUlp,
                maximumDecodedError,
                distanceDecision)
            {
                UsesHotColdLayout = request.UseHotColdLayout
            };
            regions.Add(region);
            cursorWords = endWord;
            paddingBytes = checked(paddingBytes + regionPadding);

            switch (format)
            {
                case SimpleDdgiTransportCacheFormat.Legacy36:
                    legacyBytes = checked(legacyBytes + bytes);
                    legacyRays = checked(legacyRays + boundedRayCount);
                    break;
                case SimpleDdgiTransportCacheFormat.Compact28:
                    compact28Bytes = checked(compact28Bytes + bytes);
                    compact28Rays = checked(compact28Rays + boundedRayCount);
                    break;
                case SimpleDdgiTransportCacheFormat.Compact24:
                    compact24Bytes = checked(compact24Bytes + bytes);
                    compact24Rays = checked(compact24Rays + boundedRayCount);
                    break;
            }

            fingerprint = Add(fingerprint, checked((uint)request.VolumeIndex));
            fingerprint = Add(fingerprint, checked((uint)request.SourceOrdinal));
            fingerprint = AddString(fingerprint, identity);
            fingerprint = Add(fingerprint, checked((uint)request.PhysicalFirstProbe));
            fingerprint = Add(fingerprint, checked((uint)request.PhysicalProbeCount));
            fingerprint = Add(fingerprint, checked((uint)request.RaysPerProbe));
            fingerprint = Add(fingerprint, checked((uint)request.GridCountX));
            fingerprint = Add(fingerprint, checked((uint)request.GridCountY));
            fingerprint = Add(fingerprint, checked((uint)request.GridCountZ));
            fingerprint = Add(
                fingerprint,
                BitConverter.SingleToUInt32Bits(request.Spacing));
            fingerprint = Add(
                fingerprint,
                BitConverter.SingleToUInt32Bits(request.ArchitecturalThickness));
            fingerprint = Add(fingerprint, (uint)format);
            fingerprint = Add(fingerprint, (uint)distanceDecision);
            fingerprint = Add(
                fingerprint,
                BitConverter.SingleToUInt32Bits(maximumTraceDistance));
            fingerprint = Add(fingerprint, alignedCursor);
            fingerprint = Add(fingerprint, checked((uint)strideWords));
            fingerprint = Add(fingerprint, request.UseHotColdLayout ? 1u : 0u);
        }

        ulong sourceBytes = Math.Max(
            SimpleDdgiMemoryPlan.GraphSafePlaceholderBytes,
            checked(cursorWords * sizeof(uint)));
        if (cursorWords == 0UL)
            paddingBytes = sourceBytes;
        return new SimpleDdgiStorageLayout(
            mode,
            mode.UsesPackedCache()
                ? SimpleDdgiStorageAbiVersion.Packed
                : SimpleDdgiStorageAbiVersion.Legacy,
            DirectionCodebookVersion,
            regions.AsReadOnly(),
            sourceBytes,
            legacyBytes,
            compact28Bytes,
            compact24Bytes,
            paddingBytes,
            legacyRays,
            compact28Rays,
            compact24Rays,
            fingerprint);
    }

    public static float ResolveMaximumTraceDistance(
        in SimpleDdgiTransportCacheRegionRequest request)
    {
        if (request.MaximumTraceDistance is float configuredDistance &&
            !float.IsNaN(configuredDistance))
        {
            return configuredDistance;
        }
        int maximumGridCount = Math.Max(
            1,
            Math.Max(request.GridCountX, Math.Max(request.GridCountY, request.GridCountZ)));
        return request.Spacing * maximumGridCount;
    }

    /// <summary>Returns the largest binary16 step touching [0, maximum].</summary>
    public static float ResolveWorstCaseHalfUlp(float maximum)
    {
        if (!float.IsFinite(maximum) || maximum < 0.0f || maximum > MaximumFiniteHalf)
            return float.PositiveInfinity;
        if (maximum == 0.0f)
            return MathF.Pow(2.0f, -24.0f);

        Half rounded = (Half)maximum;
        ushort bits = BitConverter.HalfToUInt16Bits(rounded);
        if ((bits & 0x7c00) == 0)
            return MathF.Pow(2.0f, -24.0f);
        int exponent = ((bits >> 10) & 0x1f) - 15;
        return MathF.Pow(2.0f, exponent - 10);
    }

    public static SimpleDdgiDistancePackingDecision ResolveDistancePackingDecision(
        in SimpleDdgiTransportCacheRegionRequest request,
        float maximumTraceDistance,
        float worstCaseHalfUlp,
        float maximumDecodedError)
    {
        if (!request.PackingMode.UsesPackedCache())
            return SimpleDdgiDistancePackingDecision.LegacyMode;
        if (!float.IsFinite(maximumTraceDistance) || maximumTraceDistance < 0.0f)
            return SimpleDdgiDistancePackingDecision.NonFiniteRange;
        if (maximumTraceDistance > MaximumFiniteHalf || !float.IsFinite(worstCaseHalfUlp))
            return SimpleDdgiDistancePackingDecision.HalfRangeExceeded;

        float surfaceOffset = MathF.Max(0.03f, request.Spacing * 0.02f);
        if (worstCaseHalfUlp > surfaceOffset * 0.25f)
            return SimpleDdgiDistancePackingDecision.HitPointOffsetError;
        if (!float.IsFinite(request.ArchitecturalThickness) ||
            request.ArchitecturalThickness <= 0.0f ||
            worstCaseHalfUlp > request.ArchitecturalThickness * 0.10f)
        {
            return SimpleDdgiDistancePackingDecision.ArchitecturalThicknessError;
        }
        if (!ValidateHalfExponentBoundaries(
                maximumTraceDistance,
                MathF.Min(surfaceOffset * 0.25f, request.ArchitecturalThickness * 0.10f),
                out float observedMaximumError) ||
            observedMaximumError > maximumDecodedError + float.Epsilon)
        {
            return SimpleDdgiDistancePackingDecision.SyntheticBoundaryError;
        }

        return SimpleDdgiDistancePackingDecision.Eligible;
    }

    public static bool ValidateHalfExponentBoundaries(
        float maximumDistance,
        float errorLimit,
        out float maximumError)
    {
        maximumError = 0.0f;
        if (!float.IsFinite(maximumDistance) || maximumDistance < 0.0f ||
            !float.IsFinite(errorLimit) || errorLimit < 0.0f)
        {
            return false;
        }

        Span<float> offsets = stackalloc float[] { -0.5f, -0.25f, 0.0f, 0.25f, 0.5f };
        for (int exponent = -14; exponent <= 15; exponent++)
        {
            float boundary = MathF.Pow(2.0f, exponent);
            if (boundary > maximumDistance && exponent > -14)
                break;
            float ulp = MathF.Pow(2.0f, exponent - 10);
            foreach (float offset in offsets)
            {
                float value = Math.Clamp(boundary + offset * ulp, 0.0f, maximumDistance);
                float decoded = (float)(Half)value;
                float error = MathF.Abs(decoded - value);
                maximumError = MathF.Max(maximumError, error);
                if (!float.IsFinite(decoded) || error > errorLimit)
                    return false;
            }
        }

        float maximumDecoded = (float)(Half)maximumDistance;
        maximumError = MathF.Max(maximumError, MathF.Abs(maximumDecoded - maximumDistance));
        return float.IsFinite(maximumDecoded) && maximumError <= errorLimit;
    }

    public static uint PackVolumeFlags(
        SimpleDdgiTransportCacheFormat format,
        bool irradianceMirrorPresent,
        bool visibilityMirrorPresent,
        SimpleDdgiStorageAbiVersion abiVersion,
        uint directionCodebookVersion = DirectionCodebookVersion,
        bool hotColdLayout = false)
    {
        if (format == SimpleDdgiTransportCacheFormat.Invalid)
            throw new ArgumentOutOfRangeException(nameof(format));
        if ((uint)abiVersion > 0x0fu)
            throw new ArgumentOutOfRangeException(nameof(abiVersion));
        if (directionCodebookVersion > 0xffu)
            throw new ArgumentOutOfRangeException(nameof(directionCodebookVersion));
        if (hotColdLayout &&
            (abiVersion != SimpleDdgiStorageAbiVersion.Packed ||
             format is not (SimpleDdgiTransportCacheFormat.Compact24 or
                 SimpleDdgiTransportCacheFormat.Compact28)))
        {
            throw new ArgumentException(
                "The hot-header source-cache flag requires a packed compact format.",
                nameof(hotColdLayout));
        }

        return ((uint)format & 0x3u) |
            (irradianceMirrorPresent ? 1u << 2 : 0u) |
            (visibilityMirrorPresent ? 1u << 3 : 0u) |
            (((uint)abiVersion & 0x0fu) << 4) |
            ((directionCodebookVersion & 0xffu) << 8) |
            (hotColdLayout ? SimpleDdgiHotColdCacheLayout.LayoutFlag : 0u);
    }

    public static ulong AlignWords(ulong value, int alignmentWords)
    {
        if (alignmentWords <= 0 || (alignmentWords & (alignmentWords - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignmentWords));
        ulong alignment = checked((ulong)alignmentWords);
        return checked((value + alignment - 1UL) & ~(alignment - 1UL));
    }

    private static ulong Add(ulong hash, ulong value) =>
        unchecked((hash ^ value) * FingerprintPrime);

    private static ulong AddString(ulong hash, string value)
    {
        hash = Add(hash, checked((uint)value.Length));
        foreach (char character in value)
            hash = Add(hash, character);
        return hash;
    }
}
