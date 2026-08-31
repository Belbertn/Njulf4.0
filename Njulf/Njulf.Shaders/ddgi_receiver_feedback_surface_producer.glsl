#ifndef NJULF_DDGI_RECEIVER_FEEDBACK_SURFACE_PRODUCER_GLSL
#define NJULF_DDGI_RECEIVER_FEEDBACK_SURFACE_PRODUCER_GLSL

// Shared low-contention fragment-producer implementation for transparent,
// alpha-mask, and foliage receiver feedback. Callers supply the producer
// namespace and stable geometry identity, while this helper owns sampling,
// subgroup chunk reservation, overflow invalidation, and the frozen record ABI.

uint SimpleDdgiSurfaceFeedbackHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

float SimpleDdgiCubemapAreaElement(float x, float y)
{
    return atan(x * y, sqrt(x * x + y * y + 1.0));
}

float SimpleDdgiCubemapTexelSolidAngle(
    vec2 fragmentCoordinate,
    vec2 faceDimensions)
{
    vec2 extent = max(faceDimensions, vec2(1.0));
    vec2 texelMin = clamp(
        floor(fragmentCoordinate),
        vec2(0.0),
        extent - vec2(1.0));
    vec2 uv0 = texelMin / extent * 2.0 - vec2(1.0);
    vec2 uv1 = (texelMin + vec2(1.0)) / extent * 2.0 - vec2(1.0);
    float solidAngle =
        SimpleDdgiCubemapAreaElement(uv1.x, uv1.y) -
        SimpleDdgiCubemapAreaElement(uv0.x, uv1.y) -
        SimpleDdgiCubemapAreaElement(uv1.x, uv0.y) +
        SimpleDdgiCubemapAreaElement(uv0.x, uv0.y);
    return max(solidAngle, 0.0);
}

// Avoid UINT_MAX / dynamicStride overflow guards. Some NVIDIA native shader
// compilers reject that otherwise valid SPIR-V pattern in the larger B1
// graphics programs. The extended multiply proves the same condition without
// introducing a speculative integer division.
bool SimpleDdgiSurfaceFeedbackTryMultiplyU32(
    uint left,
    uint right,
    out uint product)
{
    uint highWord;
    umulExtended(left, right, highWord, product);
    return highWord == 0u;
}

bool SimpleDdgiTryComputeCubemapTileNamespace(
    uint cubemapArrayLayer,
    vec2 faceDimensions,
    out uint tileNamespaceBase)
{
    tileNamespaceBase = 0u;
    uvec2 extent = uvec2(max(floor(faceDimensions), vec2(1.0)));
    uvec2 tileExtent = (extent +
        uvec2(SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE - 1u)) /
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    uint faceTileCount;
    if (!SimpleDdgiSurfaceFeedbackTryMultiplyU32(
            tileExtent.x,
            tileExtent.y,
            faceTileCount) ||
        faceTileCount == 0u ||
        !SimpleDdgiSurfaceFeedbackTryMultiplyU32(
            cubemapArrayLayer,
            faceTileCount,
            tileNamespaceBase))
    {
        return false;
    }

    return true;
}

bool SimpleDdgiSurfaceFeedbackTryResolveTile(
    vec2 fragmentCoordinate,
    vec2 screenDimensions,
    bool exactTileNamespaceInputValid,
    uint exactTileNamespaceBase,
    out uvec2 pixel,
    out uint exactTileId,
    out uvec2 tileBase,
    out uvec2 coveredTileExtent,
    out uint coveredTilePixelCount)
{
    uvec2 extent = uvec2(max(floor(screenDimensions), vec2(1.0)));
    pixel = min(
        uvec2(max(floor(fragmentCoordinate), vec2(0.0))),
        extent - uvec2(1u));
    uvec2 tileGrid = (extent +
        uvec2(SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE - 1u)) /
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    uvec2 tile = pixel /
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    uint tileCount;
    bool tileOrdinalValid = SimpleDdgiSurfaceFeedbackTryMultiplyU32(
        tileGrid.x,
        tileGrid.y,
        tileCount);
    uint localTileId = tileOrdinalValid
        ? tile.y * tileGrid.x + tile.x
        : 0u;
    bool tileNamespaceValid = exactTileNamespaceInputValid &&
        tileOrdinalValid &&
        exactTileNamespaceBase <= 0xffffffffu - localTileId;
    exactTileId = tileNamespaceValid
        ? exactTileNamespaceBase + localTileId
        : 0u;
    tileBase = tile *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    coveredTileExtent = min(
        uvec2(SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE),
        extent - tileBase);
    coveredTilePixelCount =
        coveredTileExtent.x * coveredTileExtent.y;
    return tileNamespaceValid;
}

bool SimpleDdgiSurfaceFeedbackRepresentativePixel(
    uvec2 pixel,
    uvec2 tileBase,
    uvec2 coveredTileExtent,
    uint coveredTilePixelCount,
    uint exactTileId,
    uint frameSerialLow,
    uint frameSerialHigh,
    uint producer)
{
    uint representativeHash = SimpleDdgiSurfaceFeedbackHash(
        exactTileId ^ frameSerialLow ^
        SimpleDdgiSurfaceFeedbackHash(frameSerialHigh + 0x9e3779b9u) ^
        SimpleDdgiSurfaceFeedbackHash(producer + 0x85ebca6bu));
    uint representativeLinear = coveredTilePixelCount != 0u
        ? representativeHash % coveredTilePixelCount
        : 0u;
    uvec2 representativePixel = tileBase + uvec2(
        representativeLinear % max(coveredTileExtent.x, 1u),
        representativeLinear / max(coveredTileExtent.x, 1u));
    return all(equal(pixel, representativePixel));
}

// Used before an exact gather to decide whether a producer could emit from
// this pixel. A producer absent from the immutable required mask is a valid
// no-op. A required but malformed policy is not safe to skip: the caller must
// retain the dense path so its normal failure accounting remains authoritative.
bool SimpleDdgiSurfaceFeedbackCouldSelectProducer(
    uint controlOffsetWords,
    uint producer,
    uvec2 pixel,
    uvec2 tileBase,
    uvec2 coveredTileExtent,
    uint coveredTilePixelCount,
    uint exactTileId,
    out bool policyUsable)
{
    uint requiredMask = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_REQUIRED_MASK);
    if ((requiredMask & (1u << producer)) == 0u)
    {
        policyUsable = true;
        return false;
    }
    if (!SimpleDdgiReceiverFeedbackProducerControlIsValid(
            controlOffsetWords,
            producer))
    {
        policyUsable = false;
        return false;
    }

    uint samplingPeriod = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PERIOD);
    uint samplingPhase = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PHASE);
    uint maximumOwners = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_MAXIMUM_OWNERS);
    policyUsable = samplingPeriod != 0u &&
        samplingPhase < samplingPeriod && maximumOwners != 0u &&
        maximumOwners <= SIMPLE_DDGI_EXACT_FEEDBACK_MAX_OWNERS;
    if (!policyUsable)
        return false;

    uint frameSerialLow =
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_FRAME_LOW);
    uint frameSerialHigh =
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_FRAME_HIGH);
    return exactTileId % samplingPeriod == samplingPhase &&
        SimpleDdgiSurfaceFeedbackRepresentativePixel(
            pixel,
            tileBase,
            coveredTileExtent,
            coveredTilePixelCount,
            exactTileId,
            frameSerialLow,
            frameSerialHigh,
            producer);
}

void EmitSimpleDdgiSurfaceReceiverFeedbackCore(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float physicalSurfaceWeight,
    bool eligible,
    uint producer,
    uint currentFrameIndex,
    vec2 screenDimensions,
    vec2 fragmentCoordinate,
    bool exactTileNamespaceInputValid,
    uint exactTileNamespaceBase,
    uvec3 stableGeometryIdentity)
{
    uint controlOffsetWords;
    if (!SimpleDdgiReceiverFeedbackTryResolveFrameControlOffset(
            currentFrameIndex,
            controlOffsetWords))
    {
        return;
    }

    uvec2 pixel;
    uint exactTileId;
    uvec2 tileBase;
    uvec2 coveredTileExtent;
    uint coveredTilePixelCount;
    bool tileNamespaceValid = SimpleDdgiSurfaceFeedbackTryResolveTile(
        fragmentCoordinate,
        screenDimensions,
        exactTileNamespaceInputValid,
        exactTileNamespaceBase,
        pixel,
        exactTileId,
        tileBase,
        coveredTileExtent,
        coveredTilePixelCount);
    // One rotating pixel represents the whole 12x12 (or clipped edge) tile.
    // Multiplying its physical measure by exact tile area makes that spatial
    // stratum unbiased; the independent tile-period correction is stored in
    // every emitted record below.
    float physicalContribution =
        float(coveredTilePixelCount) *
        max(physicalSurfaceWeight, 0.0) *
        clamp(radiometricOwnership, 0.0, 1.0) *
        clamp(leakAttenuation, 0.0, 1.0);
    bool finitePositiveContribution = physicalContribution > 0.0 &&
        !isnan(physicalContribution) && !isinf(physicalContribution);
    bool refinementOrBaseFallback =
        gather.exactFeedbackRefinementOrBaseFallback != 0u;

    // A subgroup may shade ordinary and refinement receivers together. Run
    // two uniform reservation phases so no lane can reserve producer 6 records
    // from another producer's range. Exactly one phase is active per lane.
    for (uint producerPhase = 0u; producerPhase < 2u; ++producerPhase)
    {
        uint effectiveProducer = producerPhase == 0u ? producer : 6u;
        bool laneBelongsToProducer = producerPhase == 0u
            ? !refinementOrBaseFallback
            : refinementOrBaseFallback;
        uint activeLaneCount = subgroupAdd(
            laneBelongsToProducer ? 1u : 0u);
        if (activeLaneCount == 0u)
            continue;
        bool controlValid =
            SimpleDdgiReceiverFeedbackProducerControlIsValid(
                controlOffsetWords,
                effectiveProducer);
        uint samplingPeriod = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PERIOD)
            : 0u;
        uint samplingPhase = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SAMPLE_PHASE)
            : 0u;
        uint maximumOwners = controlValid
            ? SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_MAXIMUM_OWNERS)
            : 0u;
        bool policyValid = controlValid && samplingPeriod != 0u &&
            samplingPhase < samplingPeriod && maximumOwners != 0u &&
            maximumOwners <= SIMPLE_DDGI_EXACT_FEEDBACK_MAX_OWNERS;
        if (!policyValid)
        {
            if (subgroupElect() && activeLaneCount != 0u)
            {
                SimpleDdgiReceiverFeedbackMarkProducerFailure(
                    controlOffsetWords,
                    effectiveProducer,
                    activeLaneCount);
            }
            continue;
        }

        uint frameSerialLow =
            SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_FRAME_LOW);
        uint frameSerialHigh =
            SimpleDdgiReceiverFeedbackProducerControlWord(
                controlOffsetWords,
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_FRAME_HIGH);
        bool representative =
            SimpleDdgiSurfaceFeedbackRepresentativePixel(
                pixel,
                tileBase,
                coveredTileExtent,
                coveredTilePixelCount,
                exactTileId,
                frameSerialLow,
                frameSerialHigh,
                effectiveProducer);

        uint receiverHash = SimpleDdgiSurfaceFeedbackHash(
            exactTileId ^
            SimpleDdgiSurfaceFeedbackHash(stableGeometryIdentity.x) ^
            SimpleDdgiSurfaceFeedbackHash(
                stableGeometryIdentity.y + 0x9e3779b9u) ^
            SimpleDdgiSurfaceFeedbackHash(
                stableGeometryIdentity.z + 0x85ebca6bu) ^
            SimpleDdgiSurfaceFeedbackHash(
                effectiveProducer + 0xc2b2ae35u));
        bool selected = laneBelongsToProducer && eligible &&
            tileNamespaceValid &&
            representative &&
            exactTileId % samplingPeriod == samplingPhase;
        bool ownerSetValid = gather.exactFeedbackOverflow == 0u &&
            gather.exactFeedbackOwnerCount <= maximumOwners;
        uint malformedOwnerCount = selected && gatherContributed &&
                finitePositiveContribution && !ownerSetValid
            ? max(gather.exactFeedbackOwnerCount, 1u)
            : 0u;
        uint subgroupMalformedCount = subgroupAdd(malformedOwnerCount);
        if (subgroupElect() && subgroupMalformedCount != 0u)
        {
            SimpleDdgiReceiverFeedbackMarkProducerFailure(
                controlOffsetWords,
                effectiveProducer,
                subgroupMalformedCount);
        }
        uint invalidTileCount = laneBelongsToProducer && eligible &&
                !tileNamespaceValid
            ? 1u
            : 0u;
        uint subgroupInvalidTileCount = subgroupAdd(invalidTileCount);
        if (subgroupElect() && subgroupInvalidTileCount != 0u)
        {
            SimpleDdgiReceiverFeedbackMarkProducerFailure(
                controlOffsetWords,
                effectiveProducer,
                subgroupInvalidTileCount);
        }

        uint localOwnerCount = selected && gatherContributed &&
                finitePositiveContribution && ownerSetValid
            ? gather.exactFeedbackOwnerCount
            : 0u;
        uint subgroupOwnerCount = subgroupAdd(localOwnerCount);
        uint subgroupOwnerPrefix = subgroupExclusiveAdd(localOwnerCount);

        SimpleDdgiReceiverFeedbackProducerReservation reservation;
        reservation.requestedCount = 0u;
        reservation.reservedBase = 0u;
        reservation.reservedCount = 0u;
        reservation.sharedBase = 0u;
        reservation.sharedCount = 0u;
        if (subgroupElect())
        {
            reservation = SimpleDdgiReceiverFeedbackReserveProducerRecords(
                controlOffsetWords,
                effectiveProducer,
                subgroupOwnerCount);
        }
        reservation.requestedCount = subgroupBroadcastFirst(
            reservation.requestedCount);
        reservation.reservedBase = subgroupBroadcastFirst(
            reservation.reservedBase);
        reservation.reservedCount = subgroupBroadcastFirst(
            reservation.reservedCount);
        reservation.sharedBase = subgroupBroadcastFirst(
            reservation.sharedBase);
        reservation.sharedCount = subgroupBroadcastFirst(
            reservation.sharedCount);

        float inverseInclusionProbability = float(samplingPeriod);
        uvec2 stableReceiverIdentity = uvec2(
            exactTileId,
            receiverHash ^ SimpleDdgiSurfaceFeedbackHash(
                stableGeometryIdentity.x * 0x9e3779b9u +
                stableGeometryIdentity.y));
        for (uint ownerIndex = 0u;
             ownerIndex < localOwnerCount;
             ++ownerIndex)
        {
            uint recordIndex;
            if (!SimpleDdgiReceiverFeedbackTryGetReservationRecord(
                    reservation,
                    subgroupOwnerPrefix + ownerIndex,
                    recordIndex))
            {
                continue;
            }
            SimpleDdgiExactFeedbackOwner owner =
                gather.exactFeedbackOwners[ownerIndex];
            SimpleDdgiReceiverFeedbackWriteCandidate(
                controlOffsetWords,
                recordIndex,
                effectiveProducer,
                owner.fallbackRole,
                owner.requestedProbe,
                owner.resolvedProbe,
                owner.resolvedPage,
                owner.requestedPage,
                exactTileId,
                owner.normalizedWeight,
                inverseInclusionProbability,
                physicalContribution,
                owner.pageGeneration,
                stableReceiverIdentity);
        }
    }
}

#endif
