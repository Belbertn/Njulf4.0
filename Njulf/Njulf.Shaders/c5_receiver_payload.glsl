#ifndef NJULF_C5_RECEIVER_PAYLOAD_GLSL
#define NJULF_C5_RECEIVER_PAYLOAD_GLSL

// Shared verbatim by the forward producer and C5 trace consumer. Any change
// advances the C5 source semantic/ABI versions and invalidates qualification.
uint NjulfC5Hash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

uint NjulfC5HashCombine(uint seed, uint value)
{
    return NjulfC5Hash(seed ^ (value + 0x9e3779b9u + (seed << 6u) +
        (seed >> 2u)));
}

void NjulfC5OrthonormalBasis(
    vec3 normal,
    out vec3 tangent,
    out vec3 bitangent)
{
    vec3 axis = abs(normal.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(1.0, 0.0, 0.0);
    tangent = normalize(cross(axis, normal));
    bitangent = cross(normal, tangent);
}

bool NjulfC5CreateStableSample2D(
    uvec2 receiverPixel,
    uvec2 identity,
    uint sequenceIndex,
    out vec2 sampleValue)
{
    // Owen-scrambled Sobol with a stable 8x8 spatial rank. The sequence key
    // intentionally excludes wall-clock time and TAA jitter.
    uint blueRank = ((receiverPixel.x & 7u) * 37u +
        (receiverPixel.y & 7u) * 17u) & 63u;
    uint sobolIndex = sequenceIndex + blueRank;
    uint seed = NjulfC5Hash(identity.x);
    seed = NjulfC5HashCombine(seed, identity.y);
    seed = NjulfC5HashCombine(seed, receiverPixel.x);
    seed = NjulfC5HashCombine(seed, receiverPixel.y);
    uint first = bitfieldReverse(sobolIndex);
    uint second = 0u;
    uint directionNumber = 0x80000000u;
    for (uint value = sobolIndex; value != 0u; value >>= 1u)
    {
        if ((value & 1u) != 0u)
            second ^= directionNumber;
        directionNumber ^= directionNumber >> 1u;
    }
    first = NjulfC5Hash(first ^ seed);
    second = NjulfC5Hash(second ^ NjulfC5HashCombine(seed, 0xa511e9b3u));
    const float UINT_TO_UNIT = 1.0 / 4294967296.0;
    sampleValue = vec2(
        (float(first) + 0.5) * UINT_TO_UNIT,
        (float(second) + 0.5) * UINT_TO_UNIT);
    return !any(isnan(sampleValue)) && !any(isinf(sampleValue));
}

bool NjulfC5CreateStableCosineDirection(
    uvec2 receiverPixel,
    uvec2 identity,
    uint sequenceIndex,
    vec3 shadingNormal,
    out vec3 direction,
    out float pdf)
{
    direction = vec3(0.0);
    pdf = 0.0;
    float normalLengthSquared = dot(shadingNormal, shadingNormal);
    if (normalLengthSquared <= 1.0e-12 ||
        any(isnan(shadingNormal)) || any(isinf(shadingNormal)))
    {
        return false;
    }
    vec3 unitNormal = shadingNormal * inversesqrt(normalLengthSquared);
    vec2 sampleValue;
    if (!NjulfC5CreateStableSample2D(
            receiverPixel, identity, sequenceIndex, sampleValue))
        return false;
    float u1 = sampleValue.x;
    float u2 = sampleValue.y;
    float radius = sqrt(clamp(u1, 0.0, 1.0));
    float angle = 6.28318530718 * u2;
    vec2 disk = radius * vec2(cos(angle), sin(angle));
    float hemisphereZ = sqrt(max(0.0, 1.0 - dot(disk, disk)));
    vec3 tangent;
    vec3 bitangent;
    NjulfC5OrthonormalBasis(unitNormal, tangent, bitangent);
    direction = normalize(tangent * disk.x + bitangent * disk.y +
        unitNormal * hemisphereZ);
    float cosine = max(dot(unitNormal, direction), 0.0);
    pdf = cosine * 0.318309886184;
    return !any(isnan(direction)) && !any(isinf(direction)) &&
        !isnan(pdf) && !isinf(pdf) && pdf > 0.0;
}

uint NjulfC5PackRgb565(vec3 value)
{
    uvec3 packed = uvec3(round(clamp(value, vec3(0.0), vec3(1.0)) *
        vec3(31.0, 63.0, 31.0)));
    return packed.x | (packed.y << 5u) | (packed.z << 11u);
}

vec3 NjulfC5UnpackRgb565(uint packed)
{
    return vec3(
        float(packed & 31u) / 31.0,
        float((packed >> 5u) & 63u) / 63.0,
        float((packed >> 11u) & 31u) / 31.0);
}

uint NjulfC5PackRgb9E5(vec3 value)
{
    vec3 bounded = clamp(value, vec3(0.0), vec3(65408.0));
    float maximumChannel = max(bounded.x, max(bounded.y, bounded.z));
    int sharedExponent = maximumChannel <= 0.0
        ? 0
        : clamp(max(-16, int(floor(log2(maximumChannel)))) + 16,
            0, 31);
    float sharedScale = exp2(float(sharedExponent - 24));
    if (maximumChannel > 0.0 &&
        floor(maximumChannel / sharedScale + 0.5) >= 512.0 &&
        sharedExponent < 31)
    {
        sharedExponent++;
        sharedScale *= 2.0;
    }
    uvec3 rgb9 = uvec3(clamp(
        floor(bounded / sharedScale + vec3(0.5)),
        vec3(0.0), vec3(511.0)));
    return rgb9.x |
        (rgb9.y << 9u) |
        (rgb9.z << 18u) |
        (uint(sharedExponent) << 27u);
}

vec3 NjulfC5UnpackRgb9E5(uint packed)
{
    uvec3 rgb9 = uvec3(
        packed & 0x1ffu,
        (packed >> 9u) & 0x1ffu,
        (packed >> 18u) & 0x1ffu);
    uint sharedExponent = packed >> 27u;
    return vec3(rgb9) * exp2(float(int(sharedExponent) - 24));
}

#endif
