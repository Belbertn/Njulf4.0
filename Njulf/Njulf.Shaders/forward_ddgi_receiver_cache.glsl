#ifndef NJULF_FORWARD_DDGI_RECEIVER_CACHE_GLSL
#define NJULF_FORWARD_DDGI_RECEIVER_CACHE_GLSL

// Compute prefilters the 8x8 exact-gather lattice to one FP16 value per 2x2
// screen block. A fragment quad therefore reads one shared texel while the
// only spatial approximation beyond the accepted bilinear field is bounded to
// a two-pixel footprint.
const uint FORWARD_DDGI_RECEIVER_CACHE_SCALE = 2u;

layout(std430, set = 2, binding = 0) readonly buffer ForwardDdgiReceiverCacheBlock
{
    uvec4 Entries[];
} ForwardDdgiReceiverCache;

struct ForwardDdgiReceiverCacheSample
{
    vec3 DdgiIrradiance;
    vec3 EnvironmentIrradiance;
};

uint ForwardDdgiReceiverCacheEntryIndex(
    vec2 fragmentCoordinate,
    vec2 screenDimensions)
{
    // Cache scale is a compile-time power of two. Spell out the shift because
    // glslc otherwise retains an OpUDiv in this per-fragment hot path.
    uvec2 cacheCoordinate = uvec2(fragmentCoordinate) >> uvec2(1u);
    // Derive the half-resolution row stride from the existing screen extent.
    // This leaves the push-constant Time word available to C5's independent
    // temporal sample seed when the cache and SSGI MRT are active together.
    uint fullWidth = uint(max(round(screenDimensions.x), 1.0));
    uint cacheWidth = (fullWidth + FORWARD_DDGI_RECEIVER_CACHE_SCALE - 1u) >> 1u;
    return cacheCoordinate.y * cacheWidth + cacheCoordinate.x;
}

float SampleForwardDdgiReceiverCacheRoughSpecularVisibility(
    vec2 fragmentCoordinate,
    vec2 screenDimensions,
    float perceptualRoughness)
{
    uint entryIndex = ForwardDdgiReceiverCacheEntryIndex(
        fragmentCoordinate,
        screenDimensions);
    // The resolve output retains the gather metadata's upper-bit ABI:
    // [ unused:10 | visibility:10 | minimum roughness:6 | full roughness:6 ].
    // Read only this scalar before IBL; the RGB payload can stay out of the live
    // register set until diffuse composition after the direct-light loop.
    uint metadata = ForwardDdgiReceiverCache.Entries[entryIndex].w;
    float indirectSpecularVisibility =
        float((metadata >> 10u) & 0x3ffu) / 1023.0;
    float minimumRoughness = float((metadata >> 20u) & 0x3fu) / 63.0;
    float fullWeightRoughness = max(
        float((metadata >> 26u) & 0x3fu) / 63.0,
        minimumRoughness + 1.0 / 63.0);
    float roughnessWeight = smoothstep(
        minimumRoughness,
        fullWeightRoughness,
        clamp(perceptualRoughness, 0.0, 1.0));
    return mix(
        1.0,
        clamp(indirectSpecularVisibility, 0.0, 1.0),
        roughnessWeight);
}

ForwardDdgiReceiverCacheSample SampleForwardDdgiReceiverCache(
    vec2 fragmentCoordinate,
    vec2 screenDimensions)
{
    uint entryIndex = ForwardDdgiReceiverCacheEntryIndex(
        fragmentCoordinate,
        screenDimensions);
    // Sixteen-byte entries deliberately produce one naturally aligned vector
    // load. Only the FP16 payload lanes are consumed, allowing the driver to
    // eliminate dead upper-lane traffic while retaining aligned addressing.
    uvec4 packed = ForwardDdgiReceiverCache.Entries[entryIndex];
    vec2 ddgiXy = unpackHalf2x16(packed.x);
    vec2 ddgiZEnvironmentX = unpackHalf2x16(packed.y);
    vec2 environmentYz = unpackHalf2x16(packed.z);
    ForwardDdgiReceiverCacheSample result;
    result.DdgiIrradiance = vec3(ddgiXy, ddgiZEnvironmentX.x);
    result.EnvironmentIrradiance = vec3(
        ddgiZEnvironmentX.y,
        environmentYz);
    return result;
}

#endif
