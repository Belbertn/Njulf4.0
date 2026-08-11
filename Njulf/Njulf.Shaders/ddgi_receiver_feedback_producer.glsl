#ifndef NJULF_DDGI_RECEIVER_FEEDBACK_PRODUCER_GLSL
#define NJULF_DDGI_RECEIVER_FEEDBACK_PRODUCER_GLSL

// Include common.glsl and ddgi_receiver_feedback_abi.glsl first.

struct SimpleDdgiReceiverFeedbackProducerReservation
{
    uint requestedCount;
    uint reservedBase;
    uint reservedCount;
    uint sharedBase;
    uint sharedCount;
};

uint SimpleDdgiReceiverFeedbackGlobalWord(uint word)
{
    return ReadStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        word);
}

bool SimpleDdgiReceiverFeedbackTryResolveFrameControlOffset(
    uint frameIndex,
    out uint controlOffsetWords)
{
    controlOffsetWords = 0u;
    uint sourceLength = uint(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words.length());
    if (sourceLength <
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_GLOBAL_HEADER_WORDS ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_ABI) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_ABI_VERSION ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_LAYOUT) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_LAYOUT_REVISION ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_HEADER_WORDS) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_GLOBAL_HEADER_WORDS ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_CONTROL_WORDS) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_CANDIDATE_WORDS) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CANDIDATE_WORDS ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_FLAGS) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_GLOBAL_READY ||
        SimpleDdgiReceiverFeedbackGlobalWord(
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_ENDIAN) !=
                SIMPLE_DDGI_RECEIVER_FEEDBACK_ENDIAN_SENTINEL)
    {
        return false;
    }

    uint frameCount = SimpleDdgiReceiverFeedbackGlobalWord(
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_FRAME_COUNT);
    uint frameStride = SimpleDdgiReceiverFeedbackGlobalWord(
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_GLOBAL_FRAME_STRIDE);
    if (frameIndex >= frameCount || frameCount == 0u ||
        frameStride < SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS)
    {
        return false;
    }
    controlOffsetWords =
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_GLOBAL_HEADER_WORDS +
        frameIndex * frameStride;
    return controlOffsetWords <= sourceLength &&
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS <=
            sourceLength - controlOffsetWords;
}

uint SimpleDdgiReceiverFeedbackProducerControlWord(
    uint controlOffsetWords,
    uint word)
{
    return ReadStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        controlOffsetWords + word);
}

uint SimpleDdgiReceiverFeedbackProducerRangeWord(
    uint controlOffsetWords,
    uint producer,
    uint word)
{
    return SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_HEADER_WORDS +
            producer *
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_RANGE_WORDS +
            word);
}

bool SimpleDdgiReceiverFeedbackProducerControlIsValid(
    uint controlOffsetWords,
    uint producer)
{
    uint sourceLength = uint(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words.length());
    uint requiredMask = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_REQUIRED_MASK);
    return producer <
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_PRODUCER_COUNT &&
        requiredMask != 0u &&
        (requiredMask &
            ~SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_KNOWN_PRODUCER_MASK) == 0u &&
        (requiredMask & (1u << producer)) != 0u &&
        (controlOffsetWords &
            (SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS - 1u)) == 0u &&
        controlOffsetWords <= sourceLength &&
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS <=
            sourceLength - controlOffsetWords &&
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_ABI) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_ABI_VERSION &&
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_LAYOUT) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_LAYOUT_REVISION &&
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_PRODUCER_COUNT) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_PRODUCER_COUNT &&
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_ENDIAN) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_ENDIAN_SENTINEL &&
        SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_FLAGS) ==
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_READY;
}

// Call once from a guaranteed invocation of the producer dispatch/draw. The
// later producer-to-capture barrier proves all writes from that dispatch are
// complete; this bit proves the dispatch itself was not omitted.
void SimpleDdgiReceiverFeedbackMarkProducerCompleted(
    uint controlOffsetWords,
    uint producer)
{
    if (!SimpleDdgiReceiverFeedbackProducerControlIsValid(
            controlOffsetWords, producer))
    {
        return;
    }
    atomicOr(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
            controlOffsetWords +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_COMPLETED_MASK],
        1u << producer);
}

void SimpleDdgiReceiverFeedbackMarkProducerFailure(
    uint controlOffsetWords,
    uint producer,
    uint droppedCount)
{
    if (!SimpleDdgiReceiverFeedbackProducerControlIsValid(
            controlOffsetWords, producer))
    {
        return;
    }
    uint rangeWord = controlOffsetWords +
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_HEADER_WORDS +
        producer * SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_RANGE_WORDS;
    atomicAdd(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
            rangeWord + SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_RANGE_DROPPED],
        max(droppedCount, 1u));
    atomicOr(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
            controlOffsetWords +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_OVERFLOW_MASK],
        1u << producer);
}

uint SimpleDdgiReceiverFeedbackBoundedReserve(
    uint counterWord,
    uint capacity,
    uint requested,
    out uint reservationOffset)
{
    reservationOffset = 0u;
    if (requested == 0u || capacity == 0u)
        return 0u;

    uint observed = atomicAdd(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
            counterWord], 0u);
    for (uint attempt = 0u; attempt < 64u; ++attempt)
    {
        if (observed >= capacity)
            return 0u;
        uint granted = min(requested, capacity - observed);
        uint prior = atomicCompSwap(BindlessStorageBuffers[
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
                counterWord], observed, observed + granted);
        if (prior == observed)
        {
            reservationOffset = observed;
            return granted;
        }
        observed = prior;
    }

    // Excessive contention is handled like capacity exhaustion. The complete
    // generation becomes unusable; no lane spins indefinitely on a global
    // counter.
    return 0u;
}

SimpleDdgiReceiverFeedbackProducerReservation
SimpleDdgiReceiverFeedbackReserveProducerRecords(
    uint controlOffsetWords,
    uint producer,
    uint requestedCount)
{
    SimpleDdgiReceiverFeedbackProducerReservation reservation;
    reservation.requestedCount = requestedCount;
    reservation.reservedBase = 0u;
    reservation.reservedCount = 0u;
    reservation.sharedBase = 0u;
    reservation.sharedCount = 0u;
    if (requestedCount == 0u ||
        !SimpleDdgiReceiverFeedbackProducerControlIsValid(
            controlOffsetWords, producer))
    {
        return reservation;
    }

    uint rangeWord = controlOffsetWords +
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_HEADER_WORDS +
        producer * SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_RANGE_WORDS;
    uint rangeBase = SimpleDdgiReceiverFeedbackProducerRangeWord(
        controlOffsetWords,
        producer,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_RANGE_BASE);
    uint rangeCapacity = SimpleDdgiReceiverFeedbackProducerRangeWord(
        controlOffsetWords,
        producer,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_RANGE_CAPACITY);
    uint reservedOffset;
    reservation.reservedCount = SimpleDdgiReceiverFeedbackBoundedReserve(
        rangeWord + SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_RANGE_COUNT,
        rangeCapacity,
        requestedCount,
        reservedOffset);
    reservation.reservedBase = rangeBase + reservedOffset;

    uint remaining = requestedCount - reservation.reservedCount;
    if (remaining != 0u)
    {
        uint sharedBase = SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SHARED_BASE);
        uint sharedCapacity = SimpleDdgiReceiverFeedbackProducerControlWord(
            controlOffsetWords,
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SHARED_CAPACITY);
        uint sharedOffset;
        reservation.sharedCount = SimpleDdgiReceiverFeedbackBoundedReserve(
            controlOffsetWords +
                SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_SHARED_COUNT,
            sharedCapacity,
            remaining,
            sharedOffset);
        reservation.sharedBase = sharedBase + sharedOffset;
        remaining -= reservation.sharedCount;
    }

    uint granted = reservation.reservedCount + reservation.sharedCount;
    if (granted != 0u)
    {
        atomicAdd(BindlessStorageBuffers[
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
                controlOffsetWords +
                    SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_TOTAL_COUNT],
            granted);
    }
    if (remaining != 0u)
    {
        atomicAdd(BindlessStorageBuffers[
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
                rangeWord +
                    SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_RANGE_DROPPED],
            remaining);
        atomicOr(BindlessStorageBuffers[
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
                controlOffsetWords +
                    SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_OVERFLOW_MASK],
            1u << producer);
    }
    return reservation;
}

bool SimpleDdgiReceiverFeedbackTryGetReservationRecord(
    SimpleDdgiReceiverFeedbackProducerReservation reservation,
    uint localRecord,
    out uint recordIndex)
{
    if (localRecord < reservation.reservedCount)
    {
        recordIndex = reservation.reservedBase + localRecord;
        return true;
    }
    uint sharedOrdinal = localRecord - reservation.reservedCount;
    if (sharedOrdinal < reservation.sharedCount)
    {
        recordIndex = reservation.sharedBase + sharedOrdinal;
        return true;
    }
    recordIndex = 0u;
    return false;
}

bool SimpleDdgiReceiverFeedbackWriteCandidate(
    uint controlOffsetWords,
    uint recordIndex,
    uint producer,
    uint fallbackRole,
    uint requestedVirtualProbeId,
    uint resolvedVirtualProbeId,
    uint resolvedVirtualPageId,
    uint requestedVirtualPageId,
    uint exactTileId,
    float interpolationWeight,
    float inverseInclusionProbability,
    float physicalReceiverContribution,
    uint pageGeneration,
    uvec2 stableReceiverIdentity)
{
    uint capacity = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_CAPACITY);
    uint recordBase = controlOffsetWords +
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_CONTROL_WORDS +
        recordIndex * SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CANDIDATE_WORDS;
    uint sourceLength = uint(BindlessStorageBuffers[
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words.length());
    bool valid = producer <
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_SOURCE_PRODUCER_COUNT &&
        fallbackRole <= 3u && pageGeneration != 0u &&
        pageGeneration <= 0x00ffffffu && recordIndex < capacity &&
        recordBase <= sourceLength &&
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CANDIDATE_WORDS <=
            sourceLength - recordBase &&
        !isnan(interpolationWeight) && !isinf(interpolationWeight) &&
        interpolationWeight > 0.0 &&
        !isnan(inverseInclusionProbability) &&
        !isinf(inverseInclusionProbability) &&
        inverseInclusionProbability >= 1.0 &&
        !isnan(physicalReceiverContribution) &&
        !isinf(physicalReceiverContribution) &&
        physicalReceiverContribution >= 0.0;
    if (!valid)
    {
        atomicOr(BindlessStorageBuffers[
            SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX].Words[
                controlOffsetWords +
                    SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_OVERFLOW_MASK],
            producer < 7u ? (1u << producer) : 0x80000000u);
        return false;
    }

    uint feedbackGeneration = SimpleDdgiReceiverFeedbackProducerControlWord(
        controlOffsetWords,
        SIMPLE_DDGI_RECEIVER_FEEDBACK_CAPTURE_CONTROL_GENERATION);
    uint packed = producer | (fallbackRole << 4u) |
        (pageGeneration << 8u);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 0u, requestedVirtualProbeId);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 1u, resolvedVirtualProbeId);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 2u, resolvedVirtualPageId);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 3u, requestedVirtualPageId);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 4u, exactTileId);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 5u, floatBitsToUint(interpolationWeight));
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 6u, floatBitsToUint(inverseInclusionProbability));
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 7u, floatBitsToUint(physicalReceiverContribution));
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 8u, packed);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 9u, feedbackGeneration);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 10u, stableReceiverIdentity.x);
    WriteStorageWordUniform(
        uint(SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX),
        recordBase + 11u, stableReceiverIdentity.y);
    return true;
}

#endif
