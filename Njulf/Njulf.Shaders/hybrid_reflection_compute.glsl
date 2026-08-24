#ifndef NJULF_HYBRID_REFLECTION_COMPUTE_GLSL
#define NJULF_HYBRID_REFLECTION_COMPUTE_GLSL

#include "hybrid_reflection_payload.glsl"

// Set zero and one remain the renderer-wide bindless heaps. Set two is the
// shared ray-scene bank, and this pass-local image/buffer bank is set three.
layout(set = 3, binding = 0) uniform usampler2D HybridReceiverPayload;
layout(rgba16f, set = 3, binding = 1) uniform image2D HybridRawRadiance;
layout(rg32ui, set = 3, binding = 2) uniform uimage2D HybridRawMetadata;
layout(rgba16f, set = 3, binding = 3) uniform image2D HybridHistoryPrevious;
layout(rgba16f, set = 3, binding = 4) uniform image2D HybridHistoryCurrent;
layout(rg16f, set = 3, binding = 5) uniform image2D HybridMomentsPrevious;
layout(rg16f, set = 3, binding = 6) uniform image2D HybridMomentsCurrent;
layout(rgba32ui, set = 3, binding = 7) uniform uimage2D HybridMetadataPrevious;
layout(rgba32ui, set = 3, binding = 8) uniform uimage2D HybridMetadataCurrent;
layout(rgba16f, set = 3, binding = 9) uniform image2D HybridFilterScratch;
layout(rgba16f, set = 3, binding = 10) uniform image2D HybridSceneColor;
layout(set = 3, binding = 11) uniform sampler2D HybridMotionVectors;
layout(set = 3, binding = 12) uniform sampler2D HybridSceneDepth;

layout(std430, set = 3, binding = 13) buffer HybridReflectionTaskBuffer
{
    uint HybridTaskCount;
    uint HybridTaskCapacity;
    uint HybridTaskOverflow;
    uint HybridTaskReserved;
    uvec4 HybridTasks[];
};

layout(std430, set = 3, binding = 14) buffer HybridReflectionCounterBuffer
{
    uint HybridCounters[];
};

layout(std430, set = 3, binding = 15) buffer HybridReflectionIndirectBuffer
{
    uint HybridIndirectGroupCountX;
    uint HybridIndirectGroupCountY;
    uint HybridIndirectGroupCountZ;
};

const uint HYBRID_REFLECTION_SOURCE_NONE = 0u;
const uint HYBRID_REFLECTION_SOURCE_SSR = 1u;
const uint HYBRID_REFLECTION_SOURCE_RAY_QUERY = 2u;
const uint HYBRID_REFLECTION_SOURCE_LOCAL_PROBE = 3u;
const uint HYBRID_REFLECTION_SOURCE_ENVIRONMENT = 4u;

const uint HYBRID_REFLECTION_REASON_NONE = 0u;
const uint HYBRID_REFLECTION_REASON_DISOCCLUDED = 1u;
const uint HYBRID_REFLECTION_REASON_INVALID_OR_OFFSCREEN = 2u;
const uint HYBRID_REFLECTION_REASON_LOW_CONFIDENCE = 3u;
const uint HYBRID_REFLECTION_REASON_RESOLUTION_SKIP = 4u;
const uint HYBRID_REFLECTION_REASON_ROUGHNESS_FALLBACK = 5u;
const uint HYBRID_REFLECTION_REASON_RAY_BUDGET = 6u;

const uint HYBRID_REFLECTION_METADATA_VALID = 0x80000000u;
const uint HYBRID_REFLECTION_METADATA_SOURCE_MASK = 0x0fu;
const uint HYBRID_REFLECTION_METADATA_CONFIDENCE_SHIFT = 8u;
const uint HYBRID_REFLECTION_METADATA_REASON_SHIFT = 16u;
const uint HYBRID_REFLECTION_METADATA_AGE_SHIFT = 24u;

const uint HYBRID_REFLECTION_COUNTER_SSR_HITS = 0u;
const uint HYBRID_REFLECTION_COUNTER_RAY_REQUESTS = 1u;
const uint HYBRID_REFLECTION_COUNTER_RAY_QUERIES = 2u;
const uint HYBRID_REFLECTION_COUNTER_RAY_OVERFLOW = 3u;
const uint HYBRID_REFLECTION_COUNTER_RAY_HITS = 4u;
const uint HYBRID_REFLECTION_COUNTER_RAY_MISSES = 5u;
const uint HYBRID_REFLECTION_COUNTER_PROBE_FALLBACKS = 6u;
const uint HYBRID_REFLECTION_COUNTER_ENVIRONMENT_FALLBACKS = 7u;
const float HYBRID_REFLECTION_PI = 3.14159265359;
const float HYBRID_REFLECTION_MINIMUM_RADIANCE_LIMIT = 32.0;
const float HYBRID_REFLECTION_RADIANCE_LIMIT_SCALE = 4.0;

bool HybridFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool HybridFinite(vec2 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool HybridFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

bool HybridFinite(vec4 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

float HybridMaximumComponent(vec3 value)
{
    return max(value.x, max(value.y, value.z));
}

vec3 HybridLimitReflectionRadiance(vec3 radiance, float referenceMaximum)
{
    if (!HybridFinite(radiance))
        return vec3(0.0);
    vec3 nonnegative = max(radiance, vec3(0.0));
    float safeReference = HybridFinite(referenceMaximum)
        ? max(referenceMaximum, 0.0)
        : 0.0;
    float maximum = max(HYBRID_REFLECTION_MINIMUM_RADIANCE_LIMIT,
        safeReference *
            HYBRID_REFLECTION_RADIANCE_LIMIT_SCALE + 1.0);
    float peak = HybridMaximumComponent(nonnegative);
    return peak > maximum
        ? nonnegative * (maximum / peak)
        : nonnegative;
}

uint HybridPackMetadata(
    uint source,
    float confidence,
    uint reason,
    uint age,
    bool valid)
{
    uint confidenceByte = uint(round(clamp(confidence, 0.0, 1.0) * 255.0));
    return (source & HYBRID_REFLECTION_METADATA_SOURCE_MASK) |
        ((confidenceByte & 0xffu) << HYBRID_REFLECTION_METADATA_CONFIDENCE_SHIFT) |
        ((reason & 0xffu) << HYBRID_REFLECTION_METADATA_REASON_SHIFT) |
        ((min(age, 127u) & 0x7fu) << HYBRID_REFLECTION_METADATA_AGE_SHIFT) |
        (valid ? HYBRID_REFLECTION_METADATA_VALID : 0u);
}

bool HybridMetadataValid(uint metadata)
{
    return (metadata & HYBRID_REFLECTION_METADATA_VALID) != 0u;
}

uint HybridMetadataSource(uint metadata)
{
    return metadata & HYBRID_REFLECTION_METADATA_SOURCE_MASK;
}

float HybridMetadataConfidence(uint metadata)
{
    return float((metadata >> HYBRID_REFLECTION_METADATA_CONFIDENCE_SHIFT) &
        0xffu) / 255.0;
}

uint HybridMetadataReason(uint metadata)
{
    return (metadata >> HYBRID_REFLECTION_METADATA_REASON_SHIFT) & 0xffu;
}

uint HybridMetadataAge(uint metadata)
{
    return (metadata >> HYBRID_REFLECTION_METADATA_AGE_SHIFT) & 0x7fu;
}

uint HybridReceiverIdentity(uvec4 payload)
{
    return payload.w & 0x007fffffu;
}

bool HybridReconstructWorldPosition(
    ivec2 pixel,
    uvec2 dimensions,
    mat4 inverseViewProjection,
    out vec3 worldPosition,
    out float depth)
{
    worldPosition = vec3(0.0);
    depth = texelFetch(HybridSceneDepth, pixel, 0).r;
    if (!HybridFinite(depth) || depth <= 0.0 || depth > 1.0)
        return false;
    vec2 uv = (vec2(pixel) + vec2(0.5)) / max(vec2(dimensions), vec2(1.0));
    vec4 world = MulRowMajor(
        vec4(uv * 2.0 - vec2(1.0), depth, 1.0),
        inverseViewProjection);
    if (any(isnan(world)) || any(isinf(world)) || abs(world.w) <= 1.0e-7)
        return false;
    worldPosition = world.xyz / world.w;
    return HybridFinite(worldPosition);
}

uint HybridHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

vec3 HybridFresnelSchlick(float cosine, vec3 f0)
{
    return f0 + (vec3(1.0) - f0) *
        pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

vec3 HybridFresnelSchlickRoughness(
    float cosine,
    vec3 f0,
    float roughness)
{
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    vec3 standardGrazing = max(
        vec3(1.0 - perceptualRoughness),
        f0);
    float remainingGloss = 1.0 - perceptualRoughness;
    vec3 broadDielectricGrazing = f0 +
        (vec3(1.0) - f0) * remainingGloss * remainingGloss;
    float broadLobeWeight = smoothstep(
        0.35,
        0.70,
        perceptualRoughness);
    vec3 grazing = mix(
        standardGrazing,
        min(standardGrazing, broadDielectricGrazing),
        broadLobeWeight);
    return f0 + (grazing - f0) *
        pow(clamp(1.0 - cosine, 0.0, 1.0), 5.0);
}

float HybridDistributionGgx(float nDotH, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSquared = alpha * alpha;
    float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;
    return alphaSquared / max(
        HYBRID_REFLECTION_PI * denominator * denominator, 1.0e-7);
}

float HybridGeometrySchlick(float nDotDirection, float roughness)
{
    float r = roughness + 1.0;
    float k = r * r * 0.125;
    return nDotDirection /
        max(nDotDirection * (1.0 - k) + k, 1.0e-7);
}

float HybridGeometrySmith(float nDotV, float nDotL, float roughness)
{
    return HybridGeometrySchlick(nDotV, roughness) *
        HybridGeometrySchlick(nDotL, roughness);
}

bool HybridAppendRayTask(
    uvec2 pixel,
    uint reason,
    uint identity,
    uint tier,
    uint temporalSampleIndex,
    uint admissionThreshold)
{
    atomicAdd(HybridCounters[HYBRID_REFLECTION_COUNTER_RAY_REQUESTS], 1u);
    uint admissionKey = pixel.x * 0x9e3779b9u ^
        pixel.y * 0x85ebca6bu ^
        temporalSampleIndex * 0xc2b2ae35u ^
        reason * 0x27d4eb2fu;
    if (HybridHash(admissionKey) > admissionThreshold)
    {
        atomicAdd(HybridTaskOverflow, 1u);
        atomicAdd(HybridCounters[HYBRID_REFLECTION_COUNTER_RAY_OVERFLOW], 1u);
        return false;
    }

    uint taskIndex = atomicAdd(HybridTaskCount, 1u);
    if (taskIndex < HybridTaskCapacity)
    {
        HybridTasks[taskIndex] = uvec4(pixel, reason,
            (identity & 0x00ffffffu) | ((tier & 0xffu) << 24u));
        atomicMax(HybridIndirectGroupCountX, taskIndex / 64u + 1u);
        return true;
    }

    atomicAdd(HybridTaskOverflow, 1u);
    atomicAdd(HybridCounters[HYBRID_REFLECTION_COUNTER_RAY_OVERFLOW], 1u);
    return false;
}

vec3 HybridSourceDebugColor(uint source)
{
    if (source == HYBRID_REFLECTION_SOURCE_SSR)
        return vec3(0.0, 0.9, 1.0);
    if (source == HYBRID_REFLECTION_SOURCE_RAY_QUERY)
        return vec3(1.0, 0.0, 0.9);
    if (source == HYBRID_REFLECTION_SOURCE_LOCAL_PROBE)
        return vec3(1.0, 0.85, 0.0);
    if (source == HYBRID_REFLECTION_SOURCE_ENVIRONMENT)
        return vec3(0.1, 0.3, 1.0);
    return vec3(0.0);
}

#endif
