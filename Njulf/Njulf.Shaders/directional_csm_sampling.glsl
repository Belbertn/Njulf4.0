#ifndef NJULF_DIRECTIONAL_CSM_SAMPLING_GLSL
#define NJULF_DIRECTIONAL_CSM_SAMPLING_GLSL

float DirectionalShadowFetchDepth(
    uint textureIndex,
    ivec2 texel,
    ivec2 maximumTexel)
{
    return texelFetch(
        BindlessTextures[nonuniformEXT(int(textureIndex))],
        clamp(texel, ivec2(0), maximumTexel),
        0).r;
}

// Canonical component order is (x0y0, x1y0, x0y1, x1y1). GLSL textureGather
// returns (x0y1, x1y1, x1y0, x0y0), so reorder it once here for both the
// forward receiver and the full-screen CSM temporal resolve.
vec4 DirectionalShadowGatherDepthBlock(
    uint textureIndex,
    ivec2 blockMinimum,
    ivec2 maximumTexel,
    float mapSize)
{
    bool interior = all(greaterThanEqual(blockMinimum, ivec2(0))) &&
        all(lessThan(blockMinimum, maximumTexel));
    if (interior)
    {
        vec2 gatherUv = (vec2(blockMinimum) + vec2(1.0)) /
            max(mapSize, 1.0);
        vec4 gathered = textureGather(
            BindlessTextures[nonuniformEXT(int(textureIndex))],
            gatherUv,
            0);
        return vec4(gathered.w, gathered.z, gathered.x, gathered.y);
    }

    // Do not depend on the bindless sampler's address mode at cascade edges.
    return vec4(
        DirectionalShadowFetchDepth(
            textureIndex, blockMinimum + ivec2(0, 0), maximumTexel),
        DirectionalShadowFetchDepth(
            textureIndex, blockMinimum + ivec2(1, 0), maximumTexel),
        DirectionalShadowFetchDepth(
            textureIndex, blockMinimum + ivec2(0, 1), maximumTexel),
        DirectionalShadowFetchDepth(
            textureIndex, blockMinimum + ivec2(1, 1), maximumTexel));
}

vec4 DirectionalShadowCompareGather(vec4 depths, float receiverDepth)
{
    // Reverse-Z: the receiver is lit when it is at least as close as the
    // stored caster depth.
    return step(depths, vec4(receiverDepth));
}

#endif
