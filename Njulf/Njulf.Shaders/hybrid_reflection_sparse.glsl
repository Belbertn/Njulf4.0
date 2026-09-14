#ifndef NJULF_HYBRID_REFLECTION_SPARSE_GLSL
#define NJULF_HYBRID_REFLECTION_SPARSE_GLSL

uint HybridReflectionSamplePhase(uvec2 pixel, uint tier, uint frame, uint lobe)
{
    uvec2 block = pixel / tier;
    uint key = NjulfHybridReflectionHash(block.x * 0x9e3779b9u ^
        block.y * 0x85ebca6bu ^ lobe * 0xc2b2ae35u);
    uint mask = tier * tier - 1u;
    // An odd stride permutes a power-of-two block. Keep the permutation
    // constant across cycles so EVERY sliding window covers every lane,
    // including frame-counter wraparound. Only ray directions are stochastic.
    return (frame * ((key >> 8u) | 1u) + key) & mask;
}

bool HybridReflectionMissingObservation(uint reason)
{
    return reason == HYBRID_REFLECTION_REASON_RESOLUTION_SKIP ||
        reason == HYBRID_REFLECTION_REASON_RAY_BUDGET;
}

uint HybridReflectionRayMissMetadata()
{
    // The query observed empty space. Resolve must still shade the analytic
    // background, but the resulting value is an observation, not a skipped ray.
    return HybridPackMetadata(HYBRID_REFLECTION_SOURCE_RAY_QUERY, 0.0,
        HYBRID_REFLECTION_REASON_NONE, 0u, false);
}

uint HybridReflectionResolvedSource(uint rawMetadata, uint resolvedSource)
{
    return resolvedSource != HYBRID_REFLECTION_SOURCE_PLANAR &&
        !HybridMetadataValid(rawMetadata) &&
        HybridMetadataSource(rawMetadata) == HYBRID_REFLECTION_SOURCE_RAY_QUERY
            ? HYBRID_REFLECTION_SOURCE_RAY_QUERY : resolvedSource;
}

bool HybridReflectionSpatialSupport(uvec2 metadata)
{
    uint source = HybridHistoryMetadataSource(metadata);
    // A measured ray miss is a valid analytic observation (including black).
    // Only an unobserved fallback is excluded. Carried geometric history has
    // already passed temporal surface validation and the bounded age check.
    return HybridHistoryMetadataValid(metadata) &&
        (HybridHistoryMetadataSparseState(metadata) ==
            HYBRID_REFLECTION_HISTORY_SPARSE_NONE ||
         source == HYBRID_REFLECTION_SOURCE_SSR ||
         source == HYBRID_REFLECTION_SOURCE_RAY_QUERY);
}

bool HybridReflectionClassifiedReuse(uint rawMetadata)
{
    // Only classification publishes a valid resolution-skip with nonzero
    // age. Fresh resolve results reset the raw age to zero. Preserve the
    // temporal policy for this marker; spatial filtering may still use its
    // validated analytic lighting, rather than interpreting it as a hole.
    return HybridMetadataValid(rawMetadata) &&
        HybridMetadataReason(rawMetadata) == HYBRID_REFLECTION_REASON_RESOLUTION_SKIP &&
        HybridMetadataAge(rawMetadata) > 0u;
}

bool HybridReflectionFilterTile(float roughness, float variance,
    float threshold, bool missingObservation)
{
    return missingObservation || (roughness > 0.06 && variance > threshold);
}

bool HybridReflectionCanBoundHistory(uint observationCount, bool sharp)
{
    // One stochastic sample cannot bound a broad lobe. Applying that bound
    // on every skipped frame repeatedly clips bright observations downward.
    // Sharp paths retain their aggressive anti-trail envelope.
    return sharp || observationCount >= 4u;
}

vec4 HybridReflectionCarryObservation(vec3 radiance, float confidence, float motionWeight)
{
    // Reprojection uncertainty changes confidence, not measured energy.
    // Mixing an unobserved fallback into this value would falsely publish it
    // as a geometric observation and contaminate spatial reconstruction.
    return vec4(radiance, clamp(confidence * motionWeight, 0.0, 1.0));
}

#endif
