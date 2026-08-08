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

ForwardDdgiReceiverCacheSample SampleForwardDdgiReceiverCache(
    vec2 fragmentCoordinate,
    uint cacheWidth)
{
    // Cache scale is a compile-time power of two. Spell out the shift because
    // glslc otherwise retains an OpUDiv in this per-fragment hot path.
    uvec2 cacheCoordinate = uvec2(fragmentCoordinate) >> uvec2(1u);
    uint entryIndex = cacheCoordinate.y * cacheWidth + cacheCoordinate.x;
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
