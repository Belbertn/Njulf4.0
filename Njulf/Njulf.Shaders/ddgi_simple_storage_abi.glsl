#ifndef NJULF_DDGI_SIMPLE_STORAGE_ABI_GLSL
#define NJULF_DDGI_SIMPLE_STORAGE_ABI_GLSL

// Central address/format contract shared by ordinary DDGI consumers and the
// resident commit shader. All mixed-stride cache access must pass this gate.
const uint SIMPLE_DDGI_STORAGE_HEADER_WORDS = 64u;
const uint SIMPLE_DDGI_STORAGE_VOLUME_WORDS = 28u;
const uint SIMPLE_DDGI_STORAGE_MAX_VOLUMES = 16u;
const uint SIMPLE_DDGI_STORAGE_PAGING_WORDS = 8u;
const uint SIMPLE_DDGI_STORAGE_PAGING_BASE =
    SIMPLE_DDGI_STORAGE_HEADER_WORDS +
    SIMPLE_DDGI_STORAGE_MAX_VOLUMES * SIMPLE_DDGI_STORAGE_VOLUME_WORDS;
const uint SIMPLE_DDGI_STORAGE_SPARSE_MODE = 2u;
const uint SIMPLE_DDGI_STORAGE_PROBES_PER_PAGE = 8u;
const uint SIMPLE_DDGI_STORAGE_FORMAT_MASK = 0x3u;
const uint SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36 = 0u;
const uint SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 = 1u;
const uint SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24 = 2u;
const uint SIMPLE_DDGI_STORAGE_FORMAT_INVALID = 3u;
const uint SIMPLE_DDGI_STORAGE_ABI_SHIFT = 4u;
const uint SIMPLE_DDGI_STORAGE_ABI_MASK = 0xfu << SIMPLE_DDGI_STORAGE_ABI_SHIFT;
const uint SIMPLE_DDGI_STORAGE_ABI_LEGACY = 4u;
const uint SIMPLE_DDGI_STORAGE_ABI_PACKED = 7u;
const uint SIMPLE_DDGI_STORAGE_CODEBOOK_SHIFT = 8u;
const uint SIMPLE_DDGI_STORAGE_CODEBOOK_MASK = 0xffu <<
    SIMPLE_DDGI_STORAGE_CODEBOOK_SHIFT;
const uint SIMPLE_DDGI_STORAGE_CODEBOOK_VERSION = 3u;
// Version 7 optionally transposes each packed probe page into a dense hot
// header array followed by a three-word surface-response sidecar. Capacity is
// unchanged, but misses and authored one-sided backfaces never fetch sidecar
// words during solve/audit.
const uint SIMPLE_DDGI_STORAGE_HOT_COLD_LAYOUT_BIT = 1u << 16u;
// Certified recursive glossy adds one packed {F0.rgb, roughness} word after
// each probe-local ordinary ray block. The ordinary record ABI and stride are
// unchanged, and non-recursive variants never address this sidecar.
const uint SIMPLE_DDGI_STORAGE_RECURSIVE_GLOSSY_SIDECAR_BIT = 1u << 17u;
const uint SIMPLE_DDGI_STORAGE_RESERVED_MASK = 0xfffc0000u;

uint SimpleDdgiStorageExpectedStride(uint format)
{
    if (format == SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36)
        return 9u;
    if (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28)
        return 7u;
    if (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24)
        return 6u;
    return 0u;
}

bool SimpleDdgiStorageUsesHotColdLayout(uint layoutFlags)
{
    return (layoutFlags & SIMPLE_DDGI_STORAGE_HOT_COLD_LAYOUT_BIT) != 0u;
}

bool SimpleDdgiStorageUsesRecursiveGlossySidecar(uint layoutFlags)
{
    return (layoutFlags &
        SIMPLE_DDGI_STORAGE_RECURSIVE_GLOSSY_SIDECAR_BIT) != 0u;
}

uint SimpleDdgiStorageWordsPerProbe(
    uint raysPerProbe,
    uint cacheStrideWords,
    uint layoutFlags)
{
    uint wordsPerRay = cacheStrideWords +
        (SimpleDdgiStorageUsesRecursiveGlossySidecar(layoutFlags) ? 1u : 0u);
    return raysPerProbe * wordsPerRay;
}

bool TryResolveSimpleDdgiRecursiveGlossySidecarAddressFromProbeBase(
    uint cacheProbeBaseWordPlusOne,
    uint directionRayIndex,
    uint raysPerProbe,
    uint cacheStrideWords,
    uint layoutFlags,
    out uint sidecarWord)
{
    sidecarWord = 0u;
    if (!SimpleDdgiStorageUsesRecursiveGlossySidecar(layoutFlags) ||
        cacheProbeBaseWordPlusOne == 0u || raysPerProbe == 0u ||
        directionRayIndex >= raysPerProbe || cacheStrideWords == 0u)
    {
        return false;
    }

    uint probeBase = cacheProbeBaseWordPlusOne - 1u;
    uint ordinaryWordsHigh;
    uint ordinaryWords;
    umulExtended(
        raysPerProbe,
        cacheStrideWords,
        ordinaryWordsHigh,
        ordinaryWords);
    if (ordinaryWordsHigh != 0u ||
        ordinaryWords > 0xffffffffu - probeBase ||
        directionRayIndex > 0xffffffffu - probeBase - ordinaryWords)
    {
        return false;
    }
    sidecarWord = probeBase + ordinaryWords + directionRayIndex;
    return true;
}

uint SimpleDdgiStorageHotHeaderStride(uint format)
{
    return format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ? 4u :
        format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24 ? 3u :
        SimpleDdgiStorageExpectedStride(format);
}

uint SimpleDdgiStorageGenerationWord(
    uint cacheWord,
    uint format,
    uint layoutFlags)
{
    uint stride = SimpleDdgiStorageUsesHotColdLayout(layoutFlags)
        ? SimpleDdgiStorageHotHeaderStride(format)
        : SimpleDdgiStorageExpectedStride(format);
    return cacheWord + stride - 1u;
}

uint SimpleDdgiStorageSurfacePayloadWord(
    uint cacheWord,
    uint directionRayIndex,
    uint raysPerProbe,
    uint format,
    uint layoutFlags)
{
    if (!SimpleDdgiStorageUsesHotColdLayout(layoutFlags))
    {
        return cacheWord + (format == SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36
            ? 5u
            : format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ? 3u : 2u);
    }

    uint headerStride = SimpleDdgiStorageHotHeaderStride(format);
    uint probeBase = cacheWord - directionRayIndex * headerStride;
    return probeBase + raysPerProbe * headerStride + directionRayIndex * 3u;
}

// Scheduler volume policy is compiled from the same authoritative cache region
// table as GPUSimpleDdgiVolume. Resolve the one-based per-probe address while
// work is still parallel in the admission kernel, then carry it through the
// private update record. This keeps the single-invocation emit kernel free of
// sparse paging walks and preserves the high bit for the split-trace fallback
// handshake.
bool TryResolveSimpleDdgiTransportCacheProbeBase(
    uint cacheRegionBaseWord,
    uint cacheWordsPerProbe,
    uint cachePhysicalFirstProbe,
    uint cachePhysicalProbeCount,
    uint physicalProbeIndex,
    out uint cacheProbeBaseWordPlusOne)
{
    cacheProbeBaseWordPlusOne = 0u;
    if (physicalProbeIndex < cachePhysicalFirstProbe ||
        cacheWordsPerProbe == 0u || cachePhysicalProbeCount == 0u)
    {
        return false;
    }

    uint localPhysicalProbe = physicalProbeIndex - cachePhysicalFirstProbe;
    if (localPhysicalProbe >= cachePhysicalProbeCount)
        return false;

    // One-based zero is invalid and bit 31 is reserved by the public queue.
    const uint maximumCacheProbeBaseWord = 0x7ffffffeu;
    uint offsetHigh;
    uint offsetLow;
    umulExtended(
        localPhysicalProbe,
        cacheWordsPerProbe,
        offsetHigh,
        offsetLow);
    if (cacheRegionBaseWord > maximumCacheProbeBaseWord || offsetHigh != 0u ||
        offsetLow > maximumCacheProbeBaseWord - cacheRegionBaseWord)
    {
        return false;
    }

    cacheProbeBaseWordPlusOne = cacheRegionBaseWord +
        offsetLow + 1u;
    return true;
}

// Version 3 maps every source cardinality into one maximum-cardinality
// Fibonacci lattice. Lower tiers occupy deterministic strided slots; promotion
// fills previously unused slots and maintenance selects an exact nested subset.
// Cached records are therefore never reinterpreted, while each supported tier
// retains the low quadrature error required to avoid a visible probe lattice.
uint SimpleDdgiStorageDirectionRayIndex(
    uint localRayOrdinal,
    uint activeRayCount,
    uint sourceRayCount,
    uint maximumRayCount)
{
    uint safeMaximum = max(maximumRayCount, 1u);
    uint safeActive = clamp(activeRayCount, 1u, safeMaximum);
    uint safeSource = clamp(sourceRayCount, 1u, safeMaximum);
    uint sourceOrdinal = min(
        localRayOrdinal * safeSource / safeActive,
        safeSource - 1u);
    return sourceOrdinal * safeMaximum / safeSource;
}

// Queue producers resolve sparse/dense physical ownership once per probe and
// publish this one-based address. Per-ray consumers still pass through this
// gate so format, ABI, codebook, stride, cardinality, and uint overflow remain
// validated by the same storage contract as full address resolution.
bool TryResolveSimpleDdgiTransportCacheAddressFromProbeBase(
    uint cacheProbeBaseWordPlusOne,
    uint directionRayIndex,
    uint raysPerProbe,
    uint cacheStrideWords,
    uint layoutFlags,
    out uint cacheWord,
    out uint format)
{
    cacheWord = 0u;
    format = layoutFlags & SIMPLE_DDGI_STORAGE_FORMAT_MASK;
    uint abi = (layoutFlags & SIMPLE_DDGI_STORAGE_ABI_MASK) >>
        SIMPLE_DDGI_STORAGE_ABI_SHIFT;
    uint codebook = (layoutFlags & SIMPLE_DDGI_STORAGE_CODEBOOK_MASK) >>
        SIMPLE_DDGI_STORAGE_CODEBOOK_SHIFT;
    bool formatMatchesAbi =
        (abi == SIMPLE_DDGI_STORAGE_ABI_LEGACY &&
            format == SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36) ||
        (abi == SIMPLE_DDGI_STORAGE_ABI_PACKED &&
            (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ||
             format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24));
    bool hotColdLayoutValid =
        !SimpleDdgiStorageUsesHotColdLayout(layoutFlags) ||
        (abi == SIMPLE_DDGI_STORAGE_ABI_PACKED &&
            (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ||
             format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24));
    uint expectedStride = SimpleDdgiStorageExpectedStride(format);
    if (cacheProbeBaseWordPlusOne == 0u || raysPerProbe == 0u ||
        directionRayIndex >= raysPerProbe ||
        (layoutFlags & SIMPLE_DDGI_STORAGE_RESERVED_MASK) != 0u ||
        codebook != SIMPLE_DDGI_STORAGE_CODEBOOK_VERSION ||
        !formatMatchesAbi || !hotColdLayoutValid || expectedStride == 0u ||
        cacheStrideWords != expectedStride)
    {
        return false;
    }

    uint cacheProbeBaseWord = cacheProbeBaseWordPlusOne - 1u;
    uint addressStride = SimpleDdgiStorageUsesHotColdLayout(layoutFlags)
        ? SimpleDdgiStorageHotHeaderStride(format)
        : cacheStrideWords;
    if (directionRayIndex >
            (0xffffffffu - cacheProbeBaseWord) / addressStride)
    {
        return false;
    }
    cacheWord = cacheProbeBaseWord + directionRayIndex * addressStride;
    return true;
}

bool TryResolveSimpleDdgiTransportCacheAddress(
    uint paramsBufferIndex,
    uint volumeIndex,
    uint physicalProbeIndex,
    uint directionRayIndex,
    uint raysPerProbe,
    uint sparsePhysicalPageCapacity,
    out uint cacheWord,
    out uint format,
    out uint layoutFlags)
{
    cacheWord = 0u;
    format = SIMPLE_DDGI_STORAGE_FORMAT_INVALID;
    layoutFlags = 0u;
    if (volumeIndex >= SIMPLE_DDGI_STORAGE_MAX_VOLUMES ||
        raysPerProbe == 0u || directionRayIndex >= raysPerProbe)
    {
        return false;
    }

    uint volumeBase = SIMPLE_DDGI_STORAGE_HEADER_WORDS +
        volumeIndex * SIMPLE_DDGI_STORAGE_VOLUME_WORDS;
    vec4 gridAndFirst = ReadStorageAlignedVec4Uniform(
        paramsBufferIndex,
        volumeBase + 4u);
    uvec4 cacheLayout = floatBitsToUint(ReadStorageAlignedVec4Uniform(
        paramsBufferIndex,
        volumeBase + 24u));
    uint pagingBase = SIMPLE_DDGI_STORAGE_PAGING_BASE +
        volumeIndex * SIMPLE_DDGI_STORAGE_PAGING_WORDS;
    uvec4 pagingAddress = ReadStorageAlignedUVec4Uniform(
        paramsBufferIndex,
        pagingBase);
    uvec4 pagingLayout = ReadStorageAlignedUVec4Uniform(
        paramsBufferIndex,
        pagingBase + 4u);

    uint cacheBaseWord = cacheLayout.x;
    uint cacheStrideWords = cacheLayout.y;
    layoutFlags = cacheLayout.w;
    format = layoutFlags & SIMPLE_DDGI_STORAGE_FORMAT_MASK;
    uint abi = (layoutFlags & SIMPLE_DDGI_STORAGE_ABI_MASK) >>
        SIMPLE_DDGI_STORAGE_ABI_SHIFT;
    uint codebook = (layoutFlags & SIMPLE_DDGI_STORAGE_CODEBOOK_MASK) >>
        SIMPLE_DDGI_STORAGE_CODEBOOK_SHIFT;
    bool formatMatchesAbi =
        (abi == SIMPLE_DDGI_STORAGE_ABI_LEGACY &&
            format == SIMPLE_DDGI_STORAGE_FORMAT_LEGACY_36) ||
        (abi == SIMPLE_DDGI_STORAGE_ABI_PACKED &&
            (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ||
             format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24));
    bool hotColdLayoutValid =
        !SimpleDdgiStorageUsesHotColdLayout(layoutFlags) ||
        (abi == SIMPLE_DDGI_STORAGE_ABI_PACKED &&
            (format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_28 ||
             format == SIMPLE_DDGI_STORAGE_FORMAT_COMPACT_24));
    if ((layoutFlags & SIMPLE_DDGI_STORAGE_RESERVED_MASK) != 0u ||
        codebook != SIMPLE_DDGI_STORAGE_CODEBOOK_VERSION ||
        !formatMatchesAbi || !hotColdLayoutValid ||
        cacheStrideWords != SimpleDdgiStorageExpectedStride(format))
    {
        return false;
    }

    bool sparse = pagingAddress.w == SIMPLE_DDGI_STORAGE_SPARSE_MODE;
    uint physicalFirst = sparse ? pagingLayout.w : pagingAddress.z;
    uint physicalCount;
    if (sparse)
    {
        if (sparsePhysicalPageCapacity > 0xffffffffu /
                SIMPLE_DDGI_STORAGE_PROBES_PER_PAGE)
            return false;
        physicalCount = sparsePhysicalPageCapacity *
            SIMPLE_DDGI_STORAGE_PROBES_PER_PAGE;
    }
    else
    {
        uvec3 grid = uvec3(max(gridAndFirst.xyz, vec3(1.0)));
        if (grid.x > 0xffffffffu / grid.y ||
            grid.x * grid.y > 0xffffffffu / grid.z)
            return false;
        physicalCount = grid.x * grid.y * grid.z;
    }
    if (physicalProbeIndex < physicalFirst ||
        physicalProbeIndex - physicalFirst >= physicalCount)
        return false;

    uint localProbeIndex = physicalProbeIndex - physicalFirst;
    uint wordsPerProbeHigh;
    uint wordsPerProbe;
    uint wordsPerRay = cacheStrideWords +
        (SimpleDdgiStorageUsesRecursiveGlossySidecar(layoutFlags) ? 1u : 0u);
    umulExtended(raysPerProbe, wordsPerRay, wordsPerProbeHigh, wordsPerProbe);
    if (wordsPerProbeHigh != 0u || wordsPerProbe == 0u)
        return false;
    uint probeOffsetHigh;
    uint probeOffset;
    umulExtended(localProbeIndex, wordsPerProbe, probeOffsetHigh, probeOffset);
    if (probeOffsetHigh != 0u || probeOffset > 0xffffffffu - cacheBaseWord)
        return false;
    uint probeBase = cacheBaseWord + probeOffset;
    uint addressStride = SimpleDdgiStorageUsesHotColdLayout(layoutFlags)
        ? SimpleDdgiStorageHotHeaderStride(format)
        : cacheStrideWords;
    if (directionRayIndex > (0xffffffffu - probeBase) / addressStride)
        return false;
    cacheWord = probeBase + directionRayIndex * addressStride;
    return true;
}

#endif
