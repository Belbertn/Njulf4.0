#ifndef NJULF_HYBRID_REFLECTION_SPATIAL_KERNEL_GLSL
#define NJULF_HYBRID_REFLECTION_SPATIAL_KERNEL_GLSL

vec3 HybridSpatialFilterNormal(uvec4 payload, float roughness)
{
    return normalize(mix(HybridReflectionPayloadShadingNormal(payload),
        HybridReflectionPayloadGeometricNormal(payload),
        smoothstep(0.15, 0.55, clamp(roughness, 0.0, 1.0))));
}

vec4 HybridFilterSpatialPixel(ivec2 pixel, uvec4 centerPayload)
{
    float centerRoughness = HybridReflectionPayloadPhysicalRoughness(centerPayload);
    vec3 centerNormal = HybridSpatialFilterNormal(centerPayload, centerRoughness);
    uint centerIdentity = HybridReceiverIdentity(centerPayload);
    float centerDepth = HybridReadSpatialDepth(pixel);
    uvec2 centerMetadata = HybridReadSpatialMetadata(pixel);
    if (centerRoughness <= 0.06 && HybridReflectionSpatialSupport(centerMetadata))
    {
        vec4 sharp = HybridReadSpatialInput(pixel);
        return HybridFinite(sharp) ? sharp : vec4(0.0);
    }
    bool sparse = HybridHistoryMetadataSparseState(centerMetadata) !=
        HYBRID_REFLECTION_HISTORY_SPARSE_NONE &&
        !HybridReadSpatialClassifiedReuse(pixel);
    bool geometric = HybridHistoryMetadataSource(centerMetadata) ==
            HYBRID_REFLECTION_SOURCE_SSR ||
        HybridHistoryMetadataSource(centerMetadata) ==
            HYBRID_REFLECTION_SOURCE_RAY_QUERY;
    bool reconstruct = sparse || geometric;
    int radius = 1 + int(min(pc.Iteration, 2u));
    // A 3x3 footprint cannot reach every location in a 4x4 sampling block.
    // Give sparse rough lobes one complete block of support. Sharp mirrors
    // retain a tight footprint even when the shared tile contains roughness.
    if (sparse && centerRoughness > 0.06 && pc.Iteration == 0u)
        radius = 3;
    if (centerRoughness <= 0.06)
        radius = 1;
    float broadLobeWeight = smoothstep(0.20, 0.85, centerRoughness);
    float normalPower = mix(max(pc.NormalPower, 1.0), 2.0, broadLobeWeight);
    float roughnessSigma = mix(max(pc.RoughnessSigma, 1.0e-4), 0.35,
        broadLobeWeight);
    vec4 accumulated = vec4(0.0);
    float totalWeight = 0.0;
    // Budget rejection can leave the local 4x4 block entirely unobserved,
    // especially when motion rejects history. Retry a wider footprint only
    // for these holes, retaining exactly the same surface edge stops.
    int maximumRadius = sparse && centerRoughness > 0.06 && pc.Iteration == 0u
        ? 7 : radius;
    for (int searchRadius = radius; searchRadius <= maximumRadius; searchRadius += 4)
    {
        accumulated = vec4(0.0);
        totalWeight = 0.0;
        for (int y = -searchRadius; y <= searchRadius; y++)
        {
            for (int x = -searchRadius; x <= searchRadius; x++)
            {
                ivec2 samplePixel = pixel + ivec2(x, y);
                if (any(lessThan(samplePixel, ivec2(0))) ||
                    any(greaterThanEqual(samplePixel,
                        ivec2(pc.ScreenWidth, pc.ScreenHeight))))
                    continue;
                uvec4 samplePayload = HybridReadSpatialPayload(samplePixel);
                if (!HybridReflectionPayloadValid(samplePayload))
                    continue;
                uvec2 sampleMetadata = HybridReadSpatialMetadata(samplePixel);
                if (!HybridHistoryMetadataValid(sampleMetadata) ||
                    HybridHistoryMetadataIdentity(sampleMetadata) != centerIdentity ||
                    (!HybridReflectionSpatialSupport(sampleMetadata) &&
                     !HybridReadSpatialClassifiedReuse(samplePixel)))
                    continue;
                vec4 sampleValue = HybridReadSpatialInput(samplePixel);
                if (!HybridFinite(sampleValue))
                    continue;
                float sampleRoughness = HybridReflectionPayloadPhysicalRoughness(samplePayload);
                vec3 sampleNormal = HybridSpatialFilterNormal(samplePayload, sampleRoughness);
                float sampleDepth = HybridReadSpatialDepth(samplePixel);
                float normalDot = dot(centerNormal, sampleNormal);
                // Reconstruction must not borrow a different surface's observation
                // merely because its fallback is very different in brightness.
                if (reconstruct && (normalDot < 0.9 ||
                    abs(centerDepth - sampleDepth) > max(0.0005, abs(centerDepth) * 0.002) ||
                    abs(centerRoughness - sampleRoughness) > max(pc.RoughnessSigma, 0.02)))
                    continue;
                float weight = pow(max(normalDot, 0.0), normalPower) *
                    exp(-abs(centerDepth - sampleDepth) / max(pc.DepthSigma, 1.0e-6)) *
                    exp(-abs(centerRoughness - sampleRoughness) / roughnessSigma) /
                    (1.0 + float(x * x + y * y));
                accumulated += sampleValue * weight;
                totalWeight += weight;
            }
        }
        if (totalWeight > 1.0e-6)
            break;
    }
    vec4 center = HybridReadSpatialInput(pixel);
    return totalWeight > 1.0e-6 ? accumulated / totalWeight
        : HybridFinite(center) ? center : vec4(0.0);
}

#endif
