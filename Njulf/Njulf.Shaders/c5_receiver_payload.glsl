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

bool NjulfC5CreateStableCosineDirection(
    uvec2 receiverPixel,
    uvec2 identity,
    uint temporalSampleIndex,
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

    uint seed = NjulfC5Hash(receiverPixel.x);
    seed = NjulfC5HashCombine(seed, receiverPixel.y);
    seed = NjulfC5HashCombine(seed, identity.x);
    seed = NjulfC5HashCombine(seed, identity.y);
    seed = NjulfC5HashCombine(seed, temporalSampleIndex);
    uint second = NjulfC5HashCombine(seed, 0xa511e9b3u);
    const float UINT_TO_UNIT = 1.0 / 4294967296.0;
    float u1 = (float(seed) + 0.5) * UINT_TO_UNIT;
    float u2 = (float(second) + 0.5) * UINT_TO_UNIT;
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
