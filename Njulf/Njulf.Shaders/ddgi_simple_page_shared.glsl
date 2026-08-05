#ifndef NJULF_DDGI_SIMPLE_PAGE_SHARED_GLSL
#define NJULF_DDGI_SIMPLE_PAGE_SHARED_GLSL

// Fixed production page geometry. These values are ABI, not runtime tuning.
const uvec3 SIMPLE_DDGI_PAGE_DIMENSIONS = uvec3(2u);
const uint SIMPLE_DDGI_PROBES_PER_PAGE = 8u;
const uint SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS = 4u;
const uint SIMPLE_DDGI_PAGE_HISTORY_WORDS = 4u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_METADATA_WORDS = 12u;
const uint SIMPLE_DDGI_RESIDENCY_HEADER_WORDS = 16u;
const uint SIMPLE_DDGI_RESIDENCY_VOLUME_TABLE_WORDS =
    SIMPLE_DDGI_MAX_VOLUME_COUNT * SIMPLE_DDGI_VOLUME_PAGING_STRIDE_WORDS;
const uint SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS =
    SIMPLE_DDGI_RESIDENCY_HEADER_WORDS +
    SIMPLE_DDGI_RESIDENCY_VOLUME_TABLE_WORDS;

const uint SIMPLE_DDGI_PAGE_TABLE_VALID = 1u << 0u;
const uint SIMPLE_DDGI_PAGE_TABLE_INITIALIZING = 1u << 1u;
const uint SIMPLE_DDGI_PAGE_TABLE_PUBLISHED = 1u << 2u;
const uint SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY = 1u << 3u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_IN_FLIGHT = 1u << 0u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_PINNED = 1u << 1u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_PUBLISHED_MASK_SHIFT = 8u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_PUBLISHED_MASK = 0xffu <<
    SIMPLE_DDGI_PHYSICAL_PAGE_PUBLISHED_MASK_SHIFT;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_IN_FLIGHT_COUNT_SHIFT = 16u;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_IN_FLIGHT_COUNT_MASK = 0xffu <<
    SIMPLE_DDGI_PHYSICAL_PAGE_IN_FLIGHT_COUNT_SHIFT;
const uint SIMPLE_DDGI_PHYSICAL_PAGE_ALLOCATION_CAMERA_CUT = 1u << 0u;
const uint SIMPLE_DDGI_PAGE_HISTORY_SUPPRESSED = 1u << 0u;
const uint SIMPLE_DDGI_PAGE_HISTORY_DEVELOPMENT_PIN = 1u << 1u;
const uint SIMPLE_DDGI_PAGE_HISTORY_EMPTY_CONFIRMATION_SHIFT = 8u;
const uint SIMPLE_DDGI_PAGE_HISTORY_EMPTY_CONFIRMATION_MASK = 0xffu <<
    SIMPLE_DDGI_PAGE_HISTORY_EMPTY_CONFIRMATION_SHIFT;
const uint SIMPLE_DDGI_INVALID_PHYSICAL_PROBE = 0xffffffffu;
const uint SIMPLE_DDGI_DENSE_MAPPING_GENERATION = 0xffffffffu;
const uint SIMPLE_DDGI_RESIDENCY_FLAG_READY = 1u << 0u;
const uint SIMPLE_DDGI_RESIDENCY_FLAG_FROZEN = 1u << 1u;
const uint SIMPLE_DDGI_RESIDENCY_FLAG_VALID = 1u << 2u;

const uint SIMPLE_DDGI_DEMAND_VISIBLE = 0u;
const uint SIMPLE_DDGI_DEMAND_RECEIVER = 1u;
const uint SIMPLE_DDGI_DEMAND_DEVELOPMENT_PIN = 2u;
const uint SIMPLE_DDGI_DEMAND_EPOCH_MASK = 0x00ffffffu;

const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_NONE = 0u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_SUPPRESSED_RETRY = 1u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_NON_DEPTH_TOUCH = 2u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_RECENT_RETENTION = 3u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_PUBLICATION_LATENCY = 4u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_RECEIVER_MISS = 5u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_VISIBLE_SURFACE = 6u;
const uint SIMPLE_DDGI_PAGE_DEMAND_CLASS_DEVELOPMENT_PIN = 7u;

const uint SIMPLE_DDGI_PAGE_CLASS_RESIDENT = 1u << 0u;
const uint SIMPLE_DDGI_PAGE_CLASS_MISSING = 1u << 1u;
const uint SIMPLE_DDGI_PAGE_CLASS_RETAINED = 1u << 2u;
const uint SIMPLE_DDGI_PAGE_CLASS_VICTIM = 1u << 3u;
const uint SIMPLE_DDGI_PAGE_CLASS_SUPPRESSED = 1u << 4u;
const uint SIMPLE_DDGI_PAGE_CLASS_INVALID_MAPPING = 1u << 5u;
const uint SIMPLE_DDGI_PAGE_CLASS_SELECTED = 1u << 6u;
// Naturally expired and confirmed-empty pages may leave the working set even
// without an admission. Camera-cut-only victims deliberately omit this bit:
// cuts waive retention under pressure but never flush the pool eagerly.
const uint SIMPLE_DDGI_PAGE_CLASS_EVICT_WHEN_IDLE = 1u << 7u;

const uint SIMPLE_DDGI_RESIDENCY_COUNTER_EPOCH = 0u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_REQUESTS = 1u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_OVERFLOW = 2u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_VISIBLE_STAMPS = 3u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_ADMISSION_CANDIDATES = 4u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_VICTIMS = 5u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_ADMISSIONS = 6u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_EVICTIONS = 7u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_FAILED_ADMISSIONS = 8u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RESIDENT = 9u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_PUBLISHED = 10u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_INITIALIZING = 11u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_SUPPRESSED = 12u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_VISIBLE_DEMAND = 13u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_DEMAND = 14u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RETAINED = 15u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_MAPPING_ERRORS = 16u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_DUPLICATE_VIRTUAL = 17u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_DUPLICATE_PHYSICAL = 18u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_PRESSURE_FRAMES = 19u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_CONSECUTIVE_PRESSURE = 20u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_MAX_CONSECUTIVE_PRESSURE = 21u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_COARSER_FALLBACK = 22u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_VIRTUAL = 23u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_MAPPING = 24u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_STALE_RESOURCE = 25u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_OUT_OF_RANGE = 26u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_RETRY = 27u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_ADMISSION_PROBES = 28u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_EVICTION_PROBES = 29u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_OTHER_GENERATION_EVICTION_PROBES = 30u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_NONRESIDENT_GATHER_REJECTIONS = 31u;
const uint SIMPLE_DDGI_RESIDENCY_DEVELOPMENT_CONTROL_WORD = 32u;
const uint SIMPLE_DDGI_RESIDENCY_DEVELOPMENT_CONTROL_VALID = 1u << 0u;
const uint SIMPLE_DDGI_RESIDENCY_DEVELOPMENT_CONTROL_PIN = 1u << 1u;
const uint SIMPLE_DDGI_RESIDENCY_COUNTER_WORDS = 64u;

struct SimpleDdgiProbeAddress
{
    uint virtualProbeIndex;
    uint physicalProbeIndex;
    uint virtualPageIndex;
    uint physicalPageIndex;
    uint pageMappingGeneration;
    bool resident;
    bool published;
};

SimpleDdgiProbeAddress EmptySimpleDdgiProbeAddress(uint virtualProbeIndex)
{
    SimpleDdgiProbeAddress address;
    address.virtualProbeIndex = virtualProbeIndex;
    address.physicalProbeIndex = SIMPLE_DDGI_INVALID_PHYSICAL_PROBE;
    address.virtualPageIndex = 0xffffffffu;
    address.physicalPageIndex = 0xffffffffu;
    address.pageMappingGeneration = 0u;
    address.resident = false;
    address.published = false;
    return address;
}

uint SimpleDdgiPageHistoryOffsetWords(SimpleDdgiParams p)
{
    return SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS +
        p.virtualPageCount * SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS;
}

uint SimpleDdgiPhysicalMetadataOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiPageHistoryOffsetWords(p) +
        p.virtualPageCount * SIMPLE_DDGI_PAGE_HISTORY_WORDS;
}

uint SimpleDdgiDemandCountersOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiPhysicalMetadataOffsetWords(p) +
        p.sparsePhysicalPageCapacity *
            SIMPLE_DDGI_PHYSICAL_PAGE_METADATA_WORDS;
}

uint SimpleDdgiAlignArenaWords(uint value)
{
    return (value + 3u) & ~3u;
}

uint SimpleDdgiClassificationScratchOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiDemandCountersOffsetWords(p) +
        SIMPLE_DDGI_RESIDENCY_COUNTER_WORDS;
}

uint SimpleDdgiPrefixScratchOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiClassificationScratchOffsetWords(p) +
        p.virtualPageCount * 4u);
}

uint SimpleDdgiCandidateScratchOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiPrefixScratchOffsetWords(p) + p.virtualPageCount);
}

uint SimpleDdgiVictimScratchOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiCandidateScratchOffsetWords(p) +
        p.virtualPageCount * 2u);
}

uint SimpleDdgiInitWorkOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiVictimScratchOffsetWords(p) +
        p.sparsePhysicalPageCapacity * 2u);
}

uint SimpleDdgiIndirectCommandsOffsetWords(SimpleDdgiParams p)
{
    uint maximumAdmissions = ReadStorageWordUniform(
        p.residencyArenaBufferIndex,
        12u);
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiInitWorkOffsetWords(p) + maximumAdmissions * 4u);
}

uint SimpleDdgiFeedbackOffsetWords(SimpleDdgiParams p)
{
    return SimpleDdgiAlignArenaWords(
        SimpleDdgiIndirectCommandsOffsetWords(p) + 16u);
}

uint SimpleDdgiDemandEpochForFrame(uint frameIndex)
{
    return (frameIndex % (SIMPLE_DDGI_DEMAND_EPOCH_MASK - 1u)) + 1u;
}

uint SimpleDdgiPackDemandStamp(
    uint demandEpoch,
    uint distanceBucket)
{
    uint epoch = demandEpoch & SIMPLE_DDGI_DEMAND_EPOCH_MASK;
    return (epoch << 8u) | (255u - min(distanceBucket, 255u));
}

uint SimpleDdgiDemandStampEpoch(uint stamp)
{
    return stamp >> 8u;
}

uint SimpleDdgiDemandStampDistanceBucket(uint stamp)
{
    return 255u - (stamp & 0xffu);
}

bool SimpleDdgiResidencyHeaderMatches(SimpleDdgiParams p)
{
    if (p.residencyResourceGeneration == 0u ||
        p.residencyArenaBufferIndex !=
            uint(SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX) ||
        (p.residencyFlags & SIMPLE_DDGI_RESIDENCY_FLAG_READY) == 0u ||
        (p.residencyFlags & SIMPLE_DDGI_RESIDENCY_FLAG_VALID) == 0u)
    {
        return false;
    }

    uint arena = p.residencyArenaBufferIndex;
    return ReadStorageWordUniform(arena, 2u) ==
            p.residencyResourceGeneration &&
        ReadStorageWordUniform(arena, 4u) == p.residencyMode &&
        ReadStorageWordUniform(arena, 6u) == p.virtualPageCount &&
        ReadStorageWordUniform(arena, 8u) ==
            p.sparsePhysicalPageCapacity &&
        ReadStorageWordUniform(arena, 9u) == p.physicalProbeCapacity;
}

bool SimpleDdgiFindSparsePageVolume(
    SimpleDdgiParams p,
    uint virtualPageIndex,
    out uint volumeIndex,
    out SimpleDdgiVolume volume,
    out SimpleDdgiVolumePaging paging,
    out uint volumeLocalPageIndex)
{
    volumeIndex = 0xffffffffu;
    volumeLocalPageIndex = 0u;
    volume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        0u);
    paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        0u);
    for (uint candidate = 0u;
         candidate < SIMPLE_DDGI_MAX_VOLUME_COUNT;
         candidate++)
    {
        if (candidate >= p.volumeCount)
            break;
        SimpleDdgiVolume candidateVolume = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            candidate);
        SimpleDdgiVolumePaging candidatePaging =
            ReadSimpleDdgiVolumePaging(
                uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
                candidate);
        if (candidatePaging.residencyMode ==
                SIMPLE_DDGI_RESIDENCY_MODE_DENSE)
        {
            continue;
        }
        uint pageCount = candidatePaging.pageGrid.x *
            candidatePaging.pageGrid.y *
            candidatePaging.pageGrid.z;
        if (virtualPageIndex < candidatePaging.pageTableFirst ||
            virtualPageIndex - candidatePaging.pageTableFirst >= pageCount)
        {
            continue;
        }
        volumeIndex = candidate;
        volumeLocalPageIndex = virtualPageIndex -
            candidatePaging.pageTableFirst;
        volume = candidateVolume;
        paging = candidatePaging;
        return true;
    }
    return false;
}

bool SimpleDdgiVirtualProbeForPageSlot(
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uint volumeLocalPageIndex,
    uint pageLocalProbeIndex,
    out uint virtualProbeIndex)
{
    virtualProbeIndex = 0xffffffffu;
    if (pageLocalProbeIndex >= SIMPLE_DDGI_PROBES_PER_PAGE ||
        any(equal(paging.pageGrid, uvec3(0u))))
    {
        return false;
    }
    uint pageXY = paging.pageGrid.x * paging.pageGrid.y;
    uint pageZ = volumeLocalPageIndex / pageXY;
    uint pageRemainder = volumeLocalPageIndex - pageZ * pageXY;
    uint pageY = pageRemainder / paging.pageGrid.x;
    uint pageX = pageRemainder - pageY * paging.pageGrid.x;
    uvec3 localCoord = uvec3(
        pageLocalProbeIndex & 1u,
        (pageLocalProbeIndex >> 1u) & 1u,
        (pageLocalProbeIndex >> 2u) & 1u);
    uvec3 physicalCoord = uvec3(pageX, pageY, pageZ) * 2u +
        localCoord;
    if (any(greaterThanEqual(physicalCoord, volume.gridCount)))
        return false;
    virtualProbeIndex = volume.firstProbeIndex + physicalCoord.x +
        physicalCoord.y * volume.gridCount.x +
        physicalCoord.z * volume.gridCount.x * volume.gridCount.y;
    return true;
}

uint SimpleDdgiValidPageProbeMask(
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uint volumeLocalPageIndex)
{
    uint validMask = 0u;
    for (uint slot = 0u; slot < SIMPLE_DDGI_PROBES_PER_PAGE; slot++)
    {
        uint virtualProbeIndex;
        if (SimpleDdgiVirtualProbeForPageSlot(
                volume,
                paging,
                volumeLocalPageIndex,
                slot,
                virtualProbeIndex))
        {
            validMask |= 1u << slot;
        }
    }
    return validMask;
}

uint SimpleDdgiFlattenPageLocal(uvec3 localCoord)
{
    return localCoord.x + localCoord.y * 2u + localCoord.z * 4u;
}

uint SimpleDdgiFlattenPage(uvec3 pageCoord, uvec3 pageGrid)
{
    return pageCoord.x +
        pageCoord.y * pageGrid.x +
        pageCoord.z * pageGrid.x * pageGrid.y;
}

bool ResolveSimpleDdgiVirtualPageIndex(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uvec3 logicalCoord,
    out uint virtualPageIndex)
{
    virtualPageIndex = 0xffffffffu;
    if (paging.virtualFirstProbe != volume.firstProbeIndex ||
        any(equal(paging.pageGrid, uvec3(0u))))
    {
        return false;
    }

    uvec3 physicalCoord =
        (logicalCoord + volume.physicalOffset) %
            max(volume.gridCount, uvec3(1u));
    uvec3 pageCoord = physicalCoord / SIMPLE_DDGI_PAGE_DIMENSIONS;
    if (any(greaterThanEqual(pageCoord, paging.pageGrid)))
        return false;
    virtualPageIndex = paging.pageTableFirst +
        SimpleDdgiFlattenPage(pageCoord, paging.pageGrid);
    return virtualPageIndex < p.virtualPageCount;
}

SimpleDdgiProbeAddress ResolveSimpleDdgiReceiverProbeAddress(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uvec3 logicalCoord)
{
    uvec3 physicalCoord =
        (logicalCoord + volume.physicalOffset) %
            max(volume.gridCount, uvec3(1u));
    uint localVirtualProbe = physicalCoord.x +
        physicalCoord.y * volume.gridCount.x +
        physicalCoord.z * volume.gridCount.x * volume.gridCount.y;
    uint virtualProbeIndex = volume.firstProbeIndex + localVirtualProbe;
    SimpleDdgiProbeAddress address =
        EmptySimpleDdgiProbeAddress(virtualProbeIndex);
    if (paging.virtualFirstProbe != volume.firstProbeIndex ||
        virtualProbeIndex >= p.probeCount)
    {
        return address;
    }

    uint virtualPageIndex;
    if (ResolveSimpleDdgiVirtualPageIndex(
            p,
            volume,
            paging,
            logicalCoord,
            virtualPageIndex))
    {
        address.virtualPageIndex = virtualPageIndex;
    }

    // Receiver publication is carried by the compact per-virtual-probe record.
    // Residency invalidates that record before owner reuse and commits its
    // physical atlas address last after generation validation. These booleans
    // therefore mean that the virtual address is valid; the compact record is
    // the later physical publication gate.
    address.resident = true;
    address.published = true;
    return address;
}

SimpleDdgiProbeAddress ResolveSimpleDdgiProbeAddress(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uvec3 logicalCoord)
{
    uvec3 physicalCoord =
        (logicalCoord + volume.physicalOffset) %
            max(volume.gridCount, uvec3(1u));
    uint localVirtualProbe = physicalCoord.x +
        physicalCoord.y * volume.gridCount.x +
        physicalCoord.z * volume.gridCount.x * volume.gridCount.y;
    uint virtualProbeIndex = volume.firstProbeIndex + localVirtualProbe;
    SimpleDdgiProbeAddress address =
        EmptySimpleDdgiProbeAddress(virtualProbeIndex);

    if (paging.virtualFirstProbe != volume.firstProbeIndex ||
        virtualProbeIndex >= p.probeCount)
    {
        return address;
    }

    if (paging.residencyMode !=
        SIMPLE_DDGI_RESIDENCY_MODE_SPARSE_NEAR_RING)
    {
        if (paging.residencyMode == SIMPLE_DDGI_RESIDENCY_MODE_SHADOW &&
            SimpleDdgiResidencyHeaderMatches(p) &&
            !any(equal(paging.pageGrid, uvec3(0u))))
        {
            uvec3 pageCoord =
                physicalCoord / SIMPLE_DDGI_PAGE_DIMENSIONS;
            if (!any(greaterThanEqual(pageCoord, paging.pageGrid)))
            {
                uint virtualPageIndex = paging.pageTableFirst +
                    SimpleDdgiFlattenPage(
                        pageCoord,
                        paging.pageGrid);
                if (virtualPageIndex < p.virtualPageCount)
                    address.virtualPageIndex = virtualPageIndex;
            }
        }
        uint physicalProbeIndex = paging.densePhysicalFirstProbe +
            localVirtualProbe;
        if (physicalProbeIndex >= p.physicalProbeCapacity)
            return address;
        address.physicalProbeIndex = physicalProbeIndex;
        address.pageMappingGeneration =
            SIMPLE_DDGI_DENSE_MAPPING_GENERATION;
        address.resident = true;
        address.published = true;
        return address;
    }

    if (!SimpleDdgiResidencyHeaderMatches(p) ||
        any(equal(paging.pageGrid, uvec3(0u))))
    {
        return address;
    }

    uvec3 pageCoord = physicalCoord / SIMPLE_DDGI_PAGE_DIMENSIONS;
    uvec3 pageLocalCoord = physicalCoord % SIMPLE_DDGI_PAGE_DIMENSIONS;
    if (any(greaterThanEqual(pageCoord, paging.pageGrid)))
        return address;
    uint virtualPageIndex = paging.pageTableFirst +
        SimpleDdgiFlattenPage(pageCoord, paging.pageGrid);
    address.virtualPageIndex = virtualPageIndex;
    if (virtualPageIndex >= p.virtualPageCount)
        return address;

    uint tableBase = SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS +
        virtualPageIndex * SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS;
    uvec4 table = ReadStorageAlignedUVec4Uniform(
        p.residencyArenaBufferIndex,
        tableBase);
    uint tableFlags = table.z;
    if (table.x == 0u || table.y == 0u ||
        (tableFlags & SIMPLE_DDGI_PAGE_TABLE_VALID) == 0u ||
        (tableFlags & SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY) != 0u)
    {
        return address;
    }

    uint physicalPageIndex = table.x - 1u;
    address.physicalPageIndex = physicalPageIndex;
    if (physicalPageIndex >= p.sparsePhysicalPageCapacity)
        return address;
    uint reverseBase = SimpleDdgiPhysicalMetadataOffsetWords(p) +
        physicalPageIndex * SIMPLE_DDGI_PHYSICAL_PAGE_METADATA_WORDS;
    uvec4 reverseIdentity = ReadStorageAlignedUVec4Uniform(
        p.residencyArenaBufferIndex,
        reverseBase);
    if (reverseIdentity.x != virtualPageIndex + 1u ||
        reverseIdentity.y != table.y ||
        reverseIdentity.z != p.residencyResourceGeneration)
    {
        return address;
    }

    uint physicalProbeIndex = paging.sparsePoolFirstProbe +
        physicalPageIndex * SIMPLE_DDGI_PROBES_PER_PAGE +
        SimpleDdgiFlattenPageLocal(pageLocalCoord);
    if (physicalProbeIndex >= p.physicalProbeCapacity)
        return address;

    address.physicalProbeIndex = physicalProbeIndex;
    address.pageMappingGeneration = table.y;
    address.resident = true;
    address.published =
        (tableFlags & SIMPLE_DDGI_PAGE_TABLE_PUBLISHED) != 0u &&
        (tableFlags & SIMPLE_DDGI_PAGE_TABLE_INITIALIZING) == 0u;
    return address;
}

bool SimpleDdgiProbeAddressMatchesLiveMapping(
    SimpleDdgiParams p,
    SimpleDdgiVolume volume,
    SimpleDdgiVolumePaging paging,
    uvec3 logicalCoord,
    uint expectedPhysicalProbeIndex,
    uint expectedMappingGeneration)
{
    SimpleDdgiProbeAddress live = ResolveSimpleDdgiProbeAddress(
        p,
        volume,
        paging,
        logicalCoord);
    return live.resident &&
        live.physicalProbeIndex == expectedPhysicalProbeIndex &&
        live.pageMappingGeneration == expectedMappingGeneration;
}

void SimpleDdgiStampPageDemand(
    SimpleDdgiParams p,
    uint virtualPageIndex,
    uint demandClass,
    uint demandEpoch,
    uint distanceBucket)
{
    if (!SimpleDdgiResidencyHeaderMatches(p) ||
        virtualPageIndex >= p.virtualPageCount ||
        demandEpoch == 0u)
    {
        return;
    }
    uint historyBase = SimpleDdgiPageHistoryOffsetWords(p) +
        virtualPageIndex * SIMPLE_DDGI_PAGE_HISTORY_WORDS;
    uint lane = demandClass == SIMPLE_DDGI_DEMAND_RECEIVER ? 1u : 0u;
    atomicMax(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[historyBase + lane],
        SimpleDdgiPackDemandStamp(demandEpoch, distanceBucket));
}

// Opaque forward shading already resolves the exact eight-corner gather set.
// Shadow records that set in the otherwise-reserved fourth table word for
// predictor qualification. Rendering remains dense in Shadow, and this word
// never participates in mapping identity or authoritative sparse demand.
void SimpleDdgiStampOpaqueGatherDemand(
    SimpleDdgiParams p,
    uint virtualPageIndex,
    uint demandEpoch,
    uint distanceBucket)
{
#if SIMPLE_DDGI_OPAQUE_GATHER_ORACLE != 0
    if (!SimpleDdgiResidencyHeaderMatches(p) ||
        virtualPageIndex >= p.virtualPageCount ||
        demandEpoch == 0u)
    {
        return;
    }
    if (p.residencyMode != SIMPLE_DDGI_RESIDENCY_MODE_SHADOW)
        return;
    uint tableBase = SIMPLE_DDGI_RESIDENCY_PAGE_TABLE_OFFSET_WORDS +
        virtualPageIndex * SIMPLE_DDGI_PAGE_TABLE_ENTRY_WORDS;
    atomicMax(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[tableBase + 3u],
        SimpleDdgiPackDemandStamp(demandEpoch, distanceBucket));
#endif
}

void SimpleDdgiRecordReceiverPageDemand(
    SimpleDdgiParams p,
    uint virtualPageIndex,
    uint demandEpoch)
{
    if (!SimpleDdgiResidencyHeaderMatches(p) ||
        virtualPageIndex >= p.virtualPageCount ||
        demandEpoch == 0u)
        return;
    uint countersBase = SimpleDdgiDemandCountersOffsetWords(p);
    uint epoch = demandEpoch & SIMPLE_DDGI_DEMAND_EPOCH_MASK;
    // The feedback stage exclusively snapshots and clears interval counters;
    // changing this marker therefore requires no critical section. Never spin
    // on a GPU lock here: sibling lanes can otherwise prevent the owning lane
    // from reaching its release under SIMT execution. Concurrent exchanges of
    // the same target epoch are idempotent, while the per-page atomicMax below
    // remains the authoritative no-lost-request claim.
    atomicExchange(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[countersBase +
            SIMPLE_DDGI_RESIDENCY_COUNTER_EPOCH],
        epoch);

    // Claim the virtual page before consuming the bounded request budget.
    // Every receiver uses the same worst-distance bucket, so an equal-or-newer
    // stamp proves this page has already been represented for the epoch.
    uint historyLane = SimpleDdgiPageHistoryOffsetWords(p) +
        virtualPageIndex * SIMPLE_DDGI_PAGE_HISTORY_WORDS + 1u;
    uint desiredStamp = SimpleDdgiPackDemandStamp(
        demandEpoch,
        255u);
    uint previousStamp = atomicMax(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[historyLane],
        desiredStamp);
    if (previousStamp >= desiredStamp)
        return;

    uint requestIndex = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[countersBase +
            SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_REQUESTS],
        1u);
    uint limit = ReadStorageWordUniform(
        p.residencyArenaBufferIndex,
        13u);
    if (requestIndex < limit)
    {
        return;
    }

    // The request did not fit. Restore the previous stamp only if this exact
    // claim is still current, and restore the accepted-request count so the
    // configured bound remains an actual bound rather than a soft threshold.
    atomicCompSwap(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[historyLane],
        desiredStamp,
        previousStamp);
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[countersBase +
            SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_REQUESTS],
        0xffffffffu);
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[countersBase +
            SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_OVERFLOW],
        1u);
}

void SimpleDdgiRecordResidencyCounter(
    SimpleDdgiParams p,
    uint counter,
    uint value)
{
    if (value == 0u || !SimpleDdgiResidencyHeaderMatches(p) ||
        counter >= SIMPLE_DDGI_RESIDENCY_COUNTER_WORDS)
    {
        return;
    }
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(
            p.residencyArenaBufferIndex)].Words[
                SimpleDdgiDemandCountersOffsetWords(p) + counter],
        value);
}

#endif
