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
    if (tileExtent.y != 0u &&
        tileExtent.x > 0xffffffffu / tileExtent.y)
        return false;

    uint faceTileCount = tileExtent.x * tileExtent.y;
    if (faceTileCount == 0u ||
        cubemapArrayLayer > 0xffffffffu / faceTileCount)
    {
        return false;
    }

    tileNamespaceBase = cubemapArrayLayer * faceTileCount;
    return true;
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

    uvec2 extent = uvec2(max(floor(screenDimensions), vec2(1.0)));
    uvec2 pixel = min(
        uvec2(max(floor(gl_FragCoord.xy), vec2(0.0))),
        extent - uvec2(1u));
    uvec2 tileGrid = (extent +
        uvec2(SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE - 1u)) /
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    uvec2 tile = pixel /
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    bool tileOrdinalValid = tileGrid.y != 0u &&
        tileGrid.x <= 0xffffffffu / tileGrid.y;
    uint localTileId = tileOrdinalValid
        ? tile.y * tileGrid.x + tile.x
        : 0u;
    bool tileNamespaceValid = exactTileNamespaceInputValid &&
        tileOrdinalValid &&
        exactTileNamespaceBase <= 0xffffffffu - localTileId;
    uint exactTileId = tileNamespaceValid
        ? exactTileNamespaceBase + localTileId
        : 0u;
    uvec2 tileBase = tile *
        SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE;
    uvec2 coveredTileExtent = min(
        uvec2(SIMPLE_DDGI_RECEIVER_FEEDBACK_SURFACE_TILE_SCALE),
        extent - tileBase);
    uint coveredTilePixelCount =
        coveredTileExtent.x * coveredTileExtent.y;
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
        uint representativeHash = SimpleDdgiSurfaceFeedbackHash(
            exactTileId ^ frameSerialLow ^
            SimpleDdgiSurfaceFeedbackHash(frameSerialHigh + 0x9e3779b9u) ^
            SimpleDdgiSurfaceFeedbackHash(
                effectiveProducer + 0x85ebca6bu));
        uint representativeLinear = coveredTilePixelCount != 0u
            ? representativeHash % coveredTilePixelCount
            : 0u;
        uvec2 representativePixel = tileBase + uvec2(
            representativeLinear % max(coveredTileExtent.x, 1u),
            representativeLinear / max(coveredTileExtent.x, 1u));
        bool representative = all(equal(pixel, representativePixel));

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
