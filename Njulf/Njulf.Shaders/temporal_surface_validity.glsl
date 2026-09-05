#ifndef NJULF_TEMPORAL_SURFACE_VALIDITY_GLSL
#define NJULF_TEMPORAL_SURFACE_VALIDITY_GLSL

const uint TEMPORAL_SURFACE_VALIDITY_ABI_VERSION = 1u;
const uint TEMPORAL_SURFACE_VALIDITY_WORDS_PER_PIXEL = 4u;
const uint TEMPORAL_SURFACE_NORMAL_PAYLOAD_MASK = 0x0fffffffu;

bool TemporalSurfaceFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool TemporalSurfaceFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

vec2 TemporalSurfaceSignNotZero(vec2 value)
{
    return vec2(
        value.x < 0.0 ? -1.0 : 1.0,
        value.y < 0.0 ? -1.0 : 1.0);
}

vec2 EncodeTemporalSurfaceOctahedral(vec3 normal)
{
    normal /= max(abs(normal.x) + abs(normal.y) + abs(normal.z), 1.0e-7);
    vec2 encoded = normal.xy;
    if (normal.z < 0.0)
        encoded = (vec2(1.0) - abs(encoded.yx)) *
            TemporalSurfaceSignNotZero(encoded.xy);
    return encoded * 0.5 + vec2(0.5);
}

vec3 DecodeTemporalSurfaceOctahedral(uint packed)
{
    vec2 encoded = vec2(
        float(packed & 0x3fffu),
        float((packed >> 14u) & 0x3fffu)) / 16383.0;
    vec2 value = encoded * 2.0 - vec2(1.0);
    vec3 normal = vec3(value, 1.0 - abs(value.x) - abs(value.y));
    if (normal.z < 0.0)
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            TemporalSurfaceSignNotZero(normal.xy);
    return normalize(normal);
}

uint PackTemporalSurfaceNormal(vec3 normal, uint tapMask)
{
    uvec2 oct = uvec2(round(clamp(
        EncodeTemporalSurfaceOctahedral(normal),
        vec2(0.0),
        vec2(1.0)) * 16383.0));
    return (oct.x & 0x3fffu) |
        ((oct.y & 0x3fffu) << 14u) |
        ((tapMask & 0xfu) << 28u);
}

uint TemporalSurfaceNearestTapBit(vec2 previousPixelCenter)
{
    ivec2 base = ivec2(floor(previousPixelCenter - vec2(0.5)));
    ivec2 nearest = ivec2(floor(previousPixelCenter));
    ivec2 offset = clamp(nearest - base, ivec2(0), ivec2(1));
    return 1u << uint(offset.x + offset.y * 2);
}

#ifdef TEMPORAL_SURFACE_FRAGMENT_WRITER
void WriteTemporalSurfaceSeed(
    uint frameIndex,
    uint pixelIndex,
    uint identity,
    float currentViewDepth,
    float previousViewDepth,
    vec3 currentWorldPosition,
    vec3 cameraPosition)
{
    vec3 normal = cross(
        dFdx(currentWorldPosition),
        dFdy(currentWorldPosition));
    float normalLengthSquared = dot(normal, normal);
    bool valid = identity != 0u &&
        TemporalSurfaceFinite(currentViewDepth) && currentViewDepth > 0.0 &&
        TemporalSurfaceFinite(previousViewDepth) && previousViewDepth > 0.0 &&
        TemporalSurfaceFinite(normal) && normalLengthSquared > 1.0e-12;
    if (!valid)
        identity = 0u;
    else
    {
        normal *= inversesqrt(normalLengthSquared);
        if (dot(normal, cameraPosition - currentWorldPosition) < 0.0)
            normal = -normal;
    }

    uint bufferIndex =
        uint(TEMPORAL_SURFACE_VALIDITY_BUFFER_BASE_INDEX) + frameIndex;
    uint wordOffset = pixelIndex * TEMPORAL_SURFACE_VALIDITY_WORDS_PER_PIXEL;
    WriteStorageWord(bufferIndex, wordOffset + 0u, identity);
    WriteStorageWord(
        bufferIndex,
        wordOffset + 1u,
        valid ? floatBitsToUint(currentViewDepth) : 0u);
    WriteStorageWord(
        bufferIndex,
        wordOffset + 2u,
        valid ? floatBitsToUint(previousViewDepth) : 0u);
    WriteStorageWord(
        bufferIndex,
        wordOffset + 3u,
        valid ? PackTemporalSurfaceNormal(normal, 0u) : 0u);
}
#endif

#endif
