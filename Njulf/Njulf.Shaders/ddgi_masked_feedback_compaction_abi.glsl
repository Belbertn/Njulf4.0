#ifndef NJULF_DDGI_MASKED_FEEDBACK_COMPACTION_ABI_GLSL
#define NJULF_DDGI_MASKED_FEEDBACK_COMPACTION_ABI_GLSL

// Must mirror SimpleDdgiMaskedFeedbackCompactionAbi. The first four words are
// reset by the host before raster. Successful candidates occupy a dense
// 48-byte surface list; overflow candidates stay on the inline exact path.
const uint SIMPLE_DDGI_MASKED_FEEDBACK_HEADER_WORDS = 4u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_RECORD_WORDS = 12u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_ACTIVE_BIT = 1u << 31u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_INITIALIZED_BIT = 1u << 30u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_CAPACITY_MASK =
    SIMPLE_DDGI_MASKED_FEEDBACK_INITIALIZED_BIT - 1u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_PUBLISHED_COUNT_WORD = 0u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_OVERFLOW_FALLBACK_WORD = 1u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_HIGH_WATER_WORD = 2u;
const uint SIMPLE_DDGI_MASKED_FEEDBACK_STATE_WORD = 3u;

uint SimpleDdgiMaskedFeedbackBufferIndex(uint frameIndex)
{
    return uint(SIMPLE_DDGI_MASKED_FEEDBACK_COMPACT_BUFFER_BASE_INDEX) +
        (frameIndex & 1u);
}

uint SimpleDdgiMaskedFeedbackWord(uint bufferIndex, uint word)
{
    return ReadStorageWordUniform(bufferIndex, word);
}

bool SimpleDdgiMaskedFeedbackCompactionActive(
    uint bufferIndex,
    out uint logicalCapacity)
{
    logicalCapacity = 0u;
    uint wordCount = uint(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words.length());
    if (wordCount < SIMPLE_DDGI_MASKED_FEEDBACK_HEADER_WORDS)
        return false;
    uint state = SimpleDdgiMaskedFeedbackWord(
        bufferIndex,
        SIMPLE_DDGI_MASKED_FEEDBACK_STATE_WORD);
    logicalCapacity = state & SIMPLE_DDGI_MASKED_FEEDBACK_CAPACITY_MASK;
    uint physicalCapacity =
        (wordCount - SIMPLE_DDGI_MASKED_FEEDBACK_HEADER_WORDS) /
        SIMPLE_DDGI_MASKED_FEEDBACK_RECORD_WORDS;
    logicalCapacity = min(logicalCapacity, physicalCapacity);
    return (state & SIMPLE_DDGI_MASKED_FEEDBACK_INITIALIZED_BIT) != 0u &&
        (state & SIMPLE_DDGI_MASKED_FEEDBACK_ACTIVE_BIT) != 0u &&
        logicalCapacity != 0u;
}

bool SimpleDdgiMaskedFeedbackTryAppend(
    uint bufferIndex,
    uint logicalCapacity,
    vec3 worldPosition,
    vec3 geometricNormal,
    float survivingCoverage,
    uvec2 pixel,
    uvec3 stableGeometryIdentity)
{
    uint ordinal = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            SIMPLE_DDGI_MASKED_FEEDBACK_HIGH_WATER_WORD],
        1u);
    if (ordinal >= logicalCapacity)
    {
        atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
                SIMPLE_DDGI_MASKED_FEEDBACK_OVERFLOW_FALLBACK_WORD],
            1u);
        return false;
    }

    uint recordBase = SIMPLE_DDGI_MASKED_FEEDBACK_HEADER_WORDS +
        ordinal * SIMPLE_DDGI_MASKED_FEEDBACK_RECORD_WORDS;
    WriteStorageWordUniform(bufferIndex, recordBase + 0u,
        floatBitsToUint(worldPosition.x));
    WriteStorageWordUniform(bufferIndex, recordBase + 1u,
        floatBitsToUint(worldPosition.y));
    WriteStorageWordUniform(bufferIndex, recordBase + 2u,
        floatBitsToUint(worldPosition.z));
    WriteStorageWordUniform(bufferIndex, recordBase + 3u,
        floatBitsToUint(survivingCoverage));
    WriteStorageWordUniform(bufferIndex, recordBase + 4u,
        floatBitsToUint(geometricNormal.x));
    WriteStorageWordUniform(bufferIndex, recordBase + 5u,
        floatBitsToUint(geometricNormal.y));
    WriteStorageWordUniform(bufferIndex, recordBase + 6u,
        floatBitsToUint(geometricNormal.z));
    WriteStorageWordUniform(bufferIndex, recordBase + 7u, pixel.x);
    WriteStorageWordUniform(bufferIndex, recordBase + 8u, pixel.y);
    WriteStorageWordUniform(bufferIndex, recordBase + 9u,
        stableGeometryIdentity.x);
    WriteStorageWordUniform(bufferIndex, recordBase + 10u,
        stableGeometryIdentity.y);
    WriteStorageWordUniform(bufferIndex, recordBase + 11u,
        stableGeometryIdentity.z);
    atomicMax(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            SIMPLE_DDGI_MASKED_FEEDBACK_PUBLISHED_COUNT_WORD],
        ordinal + 1u);
    return true;
}

#endif
