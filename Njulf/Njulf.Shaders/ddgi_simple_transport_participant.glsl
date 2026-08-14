#ifndef NJULF_DDGI_SIMPLE_TRANSPORT_PARTICIPANT_GLSL
#define NJULF_DDGI_SIMPLE_TRANSPORT_PARTICIPANT_GLSL

#include "ddgi_simple_scheduler_metadata_abi.glsl"

// Exact, generation-frozen eligibility contract shared by the resident
// scheduler summary and the transport audit. Solve-epoch visitation is kept
// separate: it proves that every eligible participant was updated, while this
// predicate defines the denominator that must remain stable through audit.
bool SimpleDdgiTransportSourceReady(
    uint flags,
    uint sourceRayCount,
    uint requiredSourceRayCount,
    uint sourceLightingGeneration,
    uint expectedSourceLightingGeneration,
    uint sourceEpoch,
    uint volumeGeneration,
    uint expectedVolumeGeneration,
    uint schedulerMetadata,
    uint cacheProbeBaseWordPlusOne)
{
    return requiredSourceRayCount != 0u &&
        sourceRayCount != 0u &&
        sourceRayCount <= requiredSourceRayCount &&
        sourceLightingGeneration == expectedSourceLightingGeneration &&
        sourceEpoch != 0u &&
        volumeGeneration == expectedVolumeGeneration &&
        (schedulerMetadata & SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR) == 0u &&
        (flags & SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID) == 0u &&
        cacheProbeBaseWordPlusOne != 0u;
}

bool SimpleDdgiTransportParticipantEligible(
    bool transportEnabled,
    bool resident,
    bool published,
    uint classification,
    uint flags,
    float activeWeight,
    uint sourceRayCount,
    uint requiredSourceRayCount,
    uint sourceLightingGeneration,
    uint expectedSourceLightingGeneration,
    uint sourceEpoch,
    uint volumeGeneration,
    uint expectedVolumeGeneration,
    uint schedulerMetadata,
    uint cacheProbeBaseWordPlusOne)
{
    const uint transientFlags =
        SIMPLE_DDGI_PROBE_FLAG_FRESH |
        SIMPLE_DDGI_PROBE_FLAG_SCROLL_EXPOSED |
        SIMPLE_DDGI_PROBE_FLAG_RELOCATION_PENDING |
        SIMPLE_DDGI_PROBE_FLAG_SOURCE_CACHE_INVALID;
    bool finiteWeight = !isnan(activeWeight) && !isinf(activeWeight);
    bool participantActive = finiteWeight &&
        classification != SIMPLE_DDGI_CLASSIFICATION_INACTIVE &&
        (flags & SIMPLE_DDGI_PROBE_FLAG_INACTIVE) == 0u &&
        activeWeight > 0.001;
    bool sourceReady = SimpleDdgiTransportSourceReady(
        flags,
        sourceRayCount,
        requiredSourceRayCount,
        sourceLightingGeneration,
        expectedSourceLightingGeneration,
        sourceEpoch,
        volumeGeneration,
        expectedVolumeGeneration,
        schedulerMetadata,
        cacheProbeBaseWordPlusOne);
    bool sourceStable = (flags & transientFlags) == 0u;
    return transportEnabled && resident && published && participantActive &&
        sourceReady && sourceStable;
}

#endif
