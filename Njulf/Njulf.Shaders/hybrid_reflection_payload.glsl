#ifndef NJULF_HYBRID_REFLECTION_PAYLOAD_GLSL
#define NJULF_HYBRID_REFLECTION_PAYLOAD_GLSL

const uint NJULF_HYBRID_REFLECTION_PAYLOAD_ABI_VERSION = 2u;
const uint NJULF_HYBRID_REFLECTION_IDENTITY_MASK = 0x007fffffu;
const uint NJULF_HYBRID_REFLECTION_VALID_BIT = 0x80000000u;

uint NjulfHybridReflectionHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

uint NjulfHybridReflectionHashCombine(uint seed, uint value)
{
    return NjulfHybridReflectionHash(
        seed ^ (value + 0x9e3779b9u + (seed << 6u) + (seed >> 2u)));
}

vec2 NjulfHybridReflectionOctEncode(vec3 value)
{
    vec3 normal = normalize(value);
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return clamp(normal.xy, vec2(-1.0), vec2(1.0));
}

vec3 NjulfHybridReflectionOctDecode(uint packed)
{
    vec2 encoded = unpackSnorm2x16(packed);
    vec3 normal = vec3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return normalize(normal);
}

uint NjulfHybridReflectionPackF0Roughness(vec3 f0, float roughness)
{
    uvec4 bytes = uvec4(round(clamp(
        vec4(f0, roughness), vec4(0.0), vec4(1.0)) * 255.0));
    return bytes.x | (bytes.y << 8u) | (bytes.z << 16u) |
        (bytes.w << 24u);
}

void NjulfHybridReflectionUnpackF0Roughness(
    uint packed,
    out vec3 f0,
    out float roughness)
{
    f0 = vec3(
        float(packed & 255u),
        float((packed >> 8u) & 255u),
        float((packed >> 16u) & 255u)) / 255.0;
    roughness = float(packed >> 24u) / 255.0;
}

bool NjulfHybridReflectionCreatePayload(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 f0,
    float roughness,
    float specularOcclusion,
    uvec3 receiverIdentity,
    out uvec4 payload)
{
    payload = uvec4(0u);
    float geometricLength = dot(geometricNormal, geometricNormal);
    float shadingLength = dot(shadingNormal, shadingNormal);
    if (geometricLength <= 1.0e-12 || shadingLength <= 1.0e-12 ||
        any(isnan(geometricNormal)) || any(isinf(geometricNormal)) ||
        any(isnan(shadingNormal)) || any(isinf(shadingNormal)) ||
        any(isnan(f0)) || any(isinf(f0)) ||
        isnan(roughness) || isinf(roughness) ||
        isnan(specularOcclusion) || isinf(specularOcclusion))
    {
        return false;
    }

    uint identity = NjulfHybridReflectionHash(receiverIdentity.x);
    identity = NjulfHybridReflectionHashCombine(identity, receiverIdentity.y);
    identity = NjulfHybridReflectionHashCombine(identity, receiverIdentity.z);
    identity &= NJULF_HYBRID_REFLECTION_IDENTITY_MASK;
    uint occlusion = uint(round(clamp(specularOcclusion, 0.0, 1.0) * 255.0));
    payload = uvec4(
        packSnorm2x16(NjulfHybridReflectionOctEncode(geometricNormal)),
        packSnorm2x16(NjulfHybridReflectionOctEncode(shadingNormal)),
        NjulfHybridReflectionPackF0Roughness(f0, roughness),
        identity | (occlusion << 23u) |
            NJULF_HYBRID_REFLECTION_VALID_BIT);
    return true;
}

bool NjulfHybridReflectionPayloadValid(uvec4 payload)
{
    return (payload.w & NJULF_HYBRID_REFLECTION_VALID_BIT) != 0u;
}

float NjulfHybridReflectionSpecularOcclusion(uvec4 payload)
{
    return float((payload.w >> 23u) & 255u) / 255.0;
}

uint NjulfHybridReflectionReceiverIdentity(uvec4 payload)
{
    return payload.w & NJULF_HYBRID_REFLECTION_IDENTITY_MASK;
}

// Compute consumers use concise semantic accessors while the forward ABI
// keeps its Njulf-prefixed producer symbols stable.
bool HybridReflectionPayloadValid(uvec4 payload)
{
    return NjulfHybridReflectionPayloadValid(payload);
}

vec3 HybridReflectionPayloadGeometricNormal(uvec4 payload)
{
    return NjulfHybridReflectionOctDecode(payload.x);
}

vec3 HybridReflectionPayloadShadingNormal(uvec4 payload)
{
    return NjulfHybridReflectionOctDecode(payload.y);
}

vec3 HybridReflectionPayloadF0(uvec4 payload)
{
    vec3 f0;
    float roughness;
    NjulfHybridReflectionUnpackF0Roughness(payload.z, f0, roughness);
    return f0;
}

float HybridReflectionPayloadRoughness(uvec4 payload)
{
    vec3 f0;
    float roughness;
    NjulfHybridReflectionUnpackF0Roughness(payload.z, f0, roughness);
    return roughness;
}

float HybridReflectionPayloadSpecularOcclusion(uvec4 payload)
{
    return NjulfHybridReflectionSpecularOcclusion(payload);
}

#endif
