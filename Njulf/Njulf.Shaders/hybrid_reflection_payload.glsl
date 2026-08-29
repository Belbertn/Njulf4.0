#ifndef NJULF_HYBRID_REFLECTION_PAYLOAD_GLSL
#define NJULF_HYBRID_REFLECTION_PAYLOAD_GLSL

const uint NJULF_HYBRID_REFLECTION_PAYLOAD_ABI_VERSION = 5u;
const uint NJULF_HYBRID_REFLECTION_OCT12_MASK = 0x0fffu;
const uint NJULF_HYBRID_REFLECTION_IDENTITY_MASK = 0x003fffffu;
const uint NJULF_HYBRID_REFLECTION_SPECULAR_OCCLUSION_MASK = 0x3fu;
const uint NJULF_HYBRID_REFLECTION_LOBE_TRANSMISSIVE = 1u << 0u;
const uint NJULF_HYBRID_REFLECTION_LOBE_ANISOTROPIC = 1u << 1u;
const uint NJULF_HYBRID_REFLECTION_LOBE_BROAD_ANISOTROPIC =
    NJULF_HYBRID_REFLECTION_LOBE_ANISOTROPIC;
const uint NJULF_HYBRID_REFLECTION_LOBE_CLEARCOAT = 1u << 2u;
const uint NJULF_HYBRID_REFLECTION_LOBE_MASK = 0x7u;
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

uint NjulfHybridReflectionPackGeometricNormalSchedulingRoughness(
    vec3 geometricNormal,
    float schedulingRoughness)
{
    vec2 encoded = NjulfHybridReflectionOctEncode(geometricNormal) * 0.5 +
        vec2(0.5);
    uvec2 oct12 = uvec2(round(clamp(encoded, vec2(0.0), vec2(1.0)) *
        float(NJULF_HYBRID_REFLECTION_OCT12_MASK)));
    uint roughness8 = uint(round(clamp(schedulingRoughness, 0.0, 1.0) *
        255.0));
    return oct12.x | (oct12.y << 12u) | (roughness8 << 24u);
}

vec3 NjulfHybridReflectionOct12Decode(uint packed)
{
    vec2 encoded = vec2(
        float(packed & NJULF_HYBRID_REFLECTION_OCT12_MASK),
        float((packed >> 12u) & NJULF_HYBRID_REFLECTION_OCT12_MASK)) /
        float(NJULF_HYBRID_REFLECTION_OCT12_MASK) * 2.0 - vec2(1.0);
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
    float physicalRoughness,
    float schedulingRoughness,
    float specularOcclusion,
    uint lobeFlags,
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
        isnan(physicalRoughness) || isinf(physicalRoughness) ||
        isnan(schedulingRoughness) || isinf(schedulingRoughness) ||
        isnan(specularOcclusion) || isinf(specularOcclusion))
    {
        return false;
    }

    uint identity = NjulfHybridReflectionHash(receiverIdentity.x);
    identity = NjulfHybridReflectionHashCombine(identity, receiverIdentity.y);
    identity = NjulfHybridReflectionHashCombine(identity, receiverIdentity.z);
    identity &= NJULF_HYBRID_REFLECTION_IDENTITY_MASK;
    uint occlusion = uint(round(clamp(specularOcclusion, 0.0, 1.0) *
        float(NJULF_HYBRID_REFLECTION_SPECULAR_OCCLUSION_MASK)));
    uint packedLobeFlags = lobeFlags & NJULF_HYBRID_REFLECTION_LOBE_MASK;
    payload = uvec4(
        NjulfHybridReflectionPackGeometricNormalSchedulingRoughness(
            geometricNormal,
            schedulingRoughness),
        packSnorm2x16(NjulfHybridReflectionOctEncode(shadingNormal)),
        NjulfHybridReflectionPackF0Roughness(f0, physicalRoughness),
        identity | (occlusion << 22u) | (packedLobeFlags << 28u) |
            NJULF_HYBRID_REFLECTION_VALID_BIT);
    return true;
}

bool NjulfHybridReflectionPayloadValid(uvec4 payload)
{
    return (payload.w & NJULF_HYBRID_REFLECTION_VALID_BIT) != 0u;
}

float NjulfHybridReflectionSpecularOcclusion(uvec4 payload)
{
    return float((payload.w >> 22u) &
        NJULF_HYBRID_REFLECTION_SPECULAR_OCCLUSION_MASK) /
        float(NJULF_HYBRID_REFLECTION_SPECULAR_OCCLUSION_MASK);
}

uint NjulfHybridReflectionLobeFlags(uvec4 payload)
{
    return (payload.w >> 28u) & NJULF_HYBRID_REFLECTION_LOBE_MASK;
}

uint NjulfHybridReflectionReceiverIdentity(uvec4 payload)
{
    return payload.w & NJULF_HYBRID_REFLECTION_IDENTITY_MASK;
}

uint NjulfHybridReflectionPackUnorm8(float value)
{
    return uint(round(clamp(value, 0.0, 1.0) * 255.0));
}

vec3 NjulfHybridReflectionCanonicalTangentBasisX(vec3 normal)
{
    vec3 n = normalize(normal);
    vec3 reference = abs(n.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, 1.0, 0.0);
    return normalize(cross(reference, n));
}

float NjulfHybridReflectionEncodeTangentAzimuth(
    vec3 shadingNormal,
    vec3 tangent)
{
    vec3 n = normalize(shadingNormal);
    vec3 x = NjulfHybridReflectionCanonicalTangentBasisX(n);
    vec3 y = cross(n, x);
    vec3 projected = tangent - n * dot(tangent, n);
    if (dot(projected, projected) <= 1.0e-12)
        return 0.0;
    projected = normalize(projected);
    float angle = atan(dot(projected, y), dot(projected, x));
    return fract(angle / 6.28318530718 + 1.0);
}

vec3 NjulfHybridReflectionDecodeTangentAzimuth(
    vec3 shadingNormal,
    float encodedAzimuth)
{
    vec3 n = normalize(shadingNormal);
    vec3 x = NjulfHybridReflectionCanonicalTangentBasisX(n);
    vec3 y = cross(n, x);
    float angle = clamp(encodedAzimuth, 0.0, 1.0) * 6.28318530718;
    return normalize(x * cos(angle) + y * sin(angle));
}

uvec2 NjulfHybridReflectionCreateLobeExtension(
    vec3 clearcoatNormal,
    float clearcoatFactor,
    float clearcoatRoughness,
    float anisotropyStrength,
    vec3 shadingNormal,
    vec3 tangent)
{
    vec3 safeClearcoatNormal = dot(clearcoatNormal, clearcoatNormal) > 1.0e-12
        ? normalize(clearcoatNormal)
        : normalize(shadingNormal);
    uint factor8 = NjulfHybridReflectionPackUnorm8(clearcoatFactor);
    uint roughness8 = NjulfHybridReflectionPackUnorm8(clearcoatRoughness);
    uint anisotropy8 = NjulfHybridReflectionPackUnorm8(
        abs(anisotropyStrength));
    uint tangent8 = NjulfHybridReflectionPackUnorm8(
        NjulfHybridReflectionEncodeTangentAzimuth(shadingNormal, tangent));
    return uvec2(
        packSnorm2x16(NjulfHybridReflectionOctEncode(
            safeClearcoatNormal)),
        factor8 | (roughness8 << 8u) | (anisotropy8 << 16u) |
            (tangent8 << 24u));
}

void NjulfHybridReflectionDecodeLobeExtension(
    uvec2 extension,
    vec3 shadingNormal,
    out vec3 clearcoatNormal,
    out float clearcoatFactor,
    out float clearcoatRoughness,
    out float anisotropyStrength,
    out vec3 tangent)
{
    clearcoatNormal = NjulfHybridReflectionOctDecode(extension.x);
    clearcoatFactor = float(extension.y & 255u) / 255.0;
    clearcoatRoughness = float((extension.y >> 8u) & 255u) / 255.0;
    anisotropyStrength = float((extension.y >> 16u) & 255u) / 255.0;
    float tangentAzimuth = float(extension.y >> 24u) / 255.0;
    tangent = NjulfHybridReflectionDecodeTangentAzimuth(
        shadingNormal,
        tangentAzimuth);
}

// Compute consumers use concise semantic accessors while the forward ABI
// keeps its Njulf-prefixed producer symbols stable.
bool HybridReflectionPayloadValid(uvec4 payload)
{
    return NjulfHybridReflectionPayloadValid(payload);
}

vec3 HybridReflectionPayloadGeometricNormal(uvec4 payload)
{
    return NjulfHybridReflectionOct12Decode(payload.x);
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

float HybridReflectionPayloadPhysicalRoughness(uvec4 payload)
{
    vec3 f0;
    float roughness;
    NjulfHybridReflectionUnpackF0Roughness(payload.z, f0, roughness);
    return roughness;
}

float HybridReflectionPayloadSchedulingRoughness(uvec4 payload)
{
    return float(payload.x >> 24u) / 255.0;
}

float HybridReflectionPayloadSpecularOcclusion(uvec4 payload)
{
    return NjulfHybridReflectionSpecularOcclusion(payload);
}

uint HybridReflectionPayloadLobeFlags(uvec4 payload)
{
    return NjulfHybridReflectionLobeFlags(payload);
}

#endif
