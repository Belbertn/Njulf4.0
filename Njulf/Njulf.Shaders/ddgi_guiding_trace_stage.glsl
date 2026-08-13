#ifndef NJULF_DDGI_GUIDING_TRACE_STAGE_GLSL
#define NJULF_DDGI_GUIDING_TRACE_STAGE_GLSL

// The C3 sample kernel writes the compact payload first and publishes this
// marker last. Trace and post-trace consumers read the record through the
// existing ray-scratch descriptor, keeping the persistent 64-byte sidecar out
// of NVIDIA-sensitive DDGI modules.
const uint SIMPLE_DDGI_GUIDING_TRACE_STAGE_VALID = 0x43335453u; // "C3TS"
const uint SIMPLE_DDGI_GUIDING_TRACE_STAGE_WORDS = 8u;
const uint SIMPLE_DDGI_GUIDING_TRACE_DIRECTION_WORD = 0u;
const uint SIMPLE_DDGI_GUIDING_TRACE_PDF_WORD = 1u;
const uint SIMPLE_DDGI_GUIDING_TRACE_TECHNIQUE_WORD = 2u;
const uint SIMPLE_DDGI_GUIDING_TRACE_FLAGS_WORD = 3u;
const uint SIMPLE_DDGI_GUIDING_TRACE_STABLE_LOW_WORD = 4u;
const uint SIMPLE_DDGI_GUIDING_TRACE_STABLE_HIGH_WORD = 5u;
const uint SIMPLE_DDGI_GUIDING_TRACE_OWNERSHIP_TAG_WORD = 6u;
const uint SIMPLE_DDGI_GUIDING_TRACE_MARKER_WORD = 7u;

bool TryResolveSimpleDdgiGuidingTraceStageWords(
    uint firstStageWord,
    uint globalRayIndex,
    out uint firstPayloadWord,
    out uint markerWord)
{
    firstPayloadWord = 0u;
    markerWord = 0u;
    uint payloadOffset;
    if (firstStageWord == 0u ||
        !SimpleDdgiGuidingTryMultiplyU32(
            globalRayIndex,
            SIMPLE_DDGI_GUIDING_TRACE_STAGE_WORDS,
            payloadOffset) ||
        payloadOffset > 0xffffffffu - firstStageWord)
    {
        return false;
    }

    firstPayloadWord = firstStageWord + payloadOffset;
    markerWord = firstPayloadWord +
        SIMPLE_DDGI_GUIDING_TRACE_MARKER_WORD;
    return true;
}

#endif
