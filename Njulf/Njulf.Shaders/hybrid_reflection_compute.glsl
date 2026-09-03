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
layout(r16f, set = 3, binding = 5) uniform image2D HybridMomentsPrevious;
layout(r16f, set = 3, binding = 6) uniform image2D HybridMomentsCurrent;
layout(rg32ui, set = 3, binding = 7) uniform uimage2D HybridMetadataPrevious;
layout(rg32ui, set = 3, binding = 8) uniform uimage2D HybridMetadataCurrent;
layout(rgba16f, set = 3, binding = 9) uniform image2D HybridPreviousHistoryScratch;
layout(rgba16f, set = 3, binding = 10) uniform image2D HybridSceneColor;
layout(set = 3, binding = 11) uniform sampler2D HybridMotionVectors;
layout(set = 3, binding = 12) uniform sampler2D HybridSceneDepth;

struct HybridReflectionTaskRecord
{
    uvec4 Primary;
    uvec4 LobeExtension;
};

layout(std430, set = 3, binding = 13) buffer HybridReflectionTaskBuffer
{
    uint HybridTaskCount;
    uint HybridTaskCapacity;
    uint HybridTaskOverflow;
    uint HybridTaskReserved;
    HybridReflectionTaskRecord HybridTasks[];
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
    uint HybridSsrIndirectGroupCountX;
    uint HybridSsrIndirectGroupCountY;
    uint HybridSsrIndirectGroupCountZ;
    uint HybridDdgiExactIndirectGroupCountX;
    uint HybridDdgiExactIndirectGroupCountY;
    uint HybridDdgiExactIndirectGroupCountZ;
};

// Sparse records written at the first two pixels of each DDGI receiver tile.
// The full-resolution reconstruction pass is the only consumer.
layout(rgba16f, set = 3, binding = 16) uniform image2D HybridDdgiCohorts;

layout(std430, set = 3, binding = 17) buffer HybridReflectionTileBuffer
{
    uint HybridTileCount;
    uint HybridTileCapacity;
    uint HybridTileOverflow;
    uint HybridTileReuseCount;
    uvec4 HybridTiles[];
};

const uint HYBRID_REFLECTION_SOURCE_NONE = 0u;
const uint HYBRID_REFLECTION_SOURCE_SSR = 1u;
const uint HYBRID_REFLECTION_SOURCE_RAY_QUERY = 2u;
const uint HYBRID_REFLECTION_SOURCE_DDGI = 3u;
const uint HYBRID_REFLECTION_SOURCE_LOCAL_PROBE = 4u;
const uint HYBRID_REFLECTION_SOURCE_ENVIRONMENT = 5u;
const uint HYBRID_REFLECTION_SOURCE_PLANAR = 6u;

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
const uint HYBRID_REFLECTION_COUNTER_DDGI_FALLBACKS = 6u;
const uint HYBRID_REFLECTION_COUNTER_PROBE_FALLBACKS = 7u;
const uint HYBRID_REFLECTION_COUNTER_ENVIRONMENT_FALLBACKS = 8u;
const uint HYBRID_REFLECTION_COUNTER_FULL_RATE_TILES = 9u;
const uint HYBRID_REFLECTION_COUNTER_HALF_RATE_TILES = 10u;
const uint HYBRID_REFLECTION_COUNTER_QUARTER_RATE_TILES = 11u;
const uint HYBRID_REFLECTION_COUNTER_ANALYTIC_TILES = 12u;
const uint HYBRID_REFLECTION_COUNTER_REUSE_TILES = 13u;
const uint HYBRID_REFLECTION_COUNTER_ACTIVE_TILES = 14u;
const uint HYBRID_REFLECTION_COUNTER_TILE_OVERFLOWS = 15u;
const float HYBRID_REFLECTION_PI = 3.14159265359;
const float HYBRID_REFLECTION_MINIMUM_RADIANCE_LIMIT = 32.0;
const float HYBRID_REFLECTION_RADIANCE_LIMIT_SCALE = 4.0;
const float HYBRID_REFLECTION_ALWAYS_FULL_ROUGHNESS = 0.08;
const float HYBRID_REFLECTION_MIRROR_F0_THRESHOLD = 0.35;
const float HYBRID_REFLECTION_TRANSMISSION_IMPORTANCE_FLOOR = 0.40;
const float HYBRID_REFLECTION_GLOSSY_IMPORTANCE_FLOOR = 0.30;
const float HYBRID_REFLECTION_MINIMUM_RAY_IMPORTANCE = 0.12;
const float HYBRID_REFLECTION_BROAD_IMPORTANCE_SCALE = 0.50;
const uint HYBRID_REFLECTION_SCREEN_COUNTER_SAMPLE_MASK = 63u;
const uint HYBRID_REFLECTION_SCREEN_COUNTER_SAMPLE_WEIGHT = 64u;

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

bool HybridReflectionRequiresSharpDetail(uvec4 payload)
{
    float roughness = HybridReflectionPayloadPhysicalRoughness(payload);
    uint lobeFlags = HybridReflectionPayloadLobeFlags(payload);
    bool transmissive = (lobeFlags &
        NJULF_HYBRID_REFLECTION_LOBE_TRANSMISSIVE) != 0u;
    bool broadAnisotropic = (lobeFlags &
        NJULF_HYBRID_REFLECTION_LOBE_BROAD_ANISOTROPIC) != 0u;
    float maximumF0 = HybridMaximumComponent(
        HybridReflectionPayloadF0(payload));
    return transmissive || roughness <= 0.08 ||
        (!broadAnisotropic && maximumF0 >= 0.35 && roughness <= 0.25);
}

vec3 HybridReflectionTraceNormal(uvec4 payload)
{
    vec3 shadingNormal = HybridReflectionPayloadShadingNormal(payload);
    if (HybridReflectionRequiresSharpDetail(payload))
        return shadingNormal;
    vec3 geometricNormal = HybridReflectionPayloadGeometricNormal(payload);
    float roughness = HybridReflectionPayloadPhysicalRoughness(payload);
    // Broad architectural and cloth lobes must converge to their footprint
    // normal before high-frequency normal-map detail can become reflection
    // sparkle. Sharp glass/mirrors take the early return above unchanged.
    float geometricWeight = smoothstep(0.12, 0.45, roughness);
    return normalize(mix(shadingNormal, geometricNormal, geometricWeight));
}

uint HybridResolveBaseReflectionTier(
    float roughness,
    float fullResolutionRoughness,
    float halfResolutionRoughness,
    float quarterResolutionRoughness)
{
    float fullThreshold = clamp(fullResolutionRoughness, 0.0, 1.0);
    float halfThreshold = max(fullThreshold,
        clamp(halfResolutionRoughness, 0.0, 1.0));
    float quarterThreshold = max(halfThreshold,
        clamp(quarterResolutionRoughness, 0.0, 1.0));
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    if (perceptualRoughness <= fullThreshold)
        return 1u;
    if (perceptualRoughness <= halfThreshold)
        return 2u;
    if (perceptualRoughness <= quarterThreshold)
        return 4u;
    return 0u;
}

uint HybridDemoteReflectionTier(uint tier)
{
    if (tier == 1u)
        return 2u;
    if (tier == 2u)
        return 4u;
    // A quarter-rate trace is still a geometric reflection and is visibly
    // wrong on broad, low-F0 masonry. DDGI owns that low-frequency lobe; only
    // sharp/transmissive/high-F0 receivers are protected below.
    if (tier == 4u)
        return 0u;
    return tier;
}

uint HybridResolveAdaptiveReflectionTier(
    float roughness,
    vec3 f0,
    float specularOcclusion,
    uint lobeFlags,
    float fullResolutionRoughness,
    float halfResolutionRoughness,
    float quarterResolutionRoughness)
{
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    float maximumF0 = clamp(HybridMaximumComponent(f0), 0.0, 1.0);
    uint tier = HybridResolveBaseReflectionTier(
        perceptualRoughness,
        fullResolutionRoughness,
        halfResolutionRoughness,
        quarterResolutionRoughness);
    if (tier == 0u)
        return 0u;

    bool startsInFullBand = tier == 1u;
    bool transmissive = (lobeFlags &
        NJULF_HYBRID_REFLECTION_LOBE_TRANSMISSIVE) != 0u;
    bool broadAnisotropic = (lobeFlags &
        NJULF_HYBRID_REFLECTION_LOBE_BROAD_ANISOTROPIC) != 0u;
    bool requiresFullQuality = perceptualRoughness <=
            HYBRID_REFLECTION_ALWAYS_FULL_ROUGHNESS ||
        transmissive ||
        maximumF0 >= HYBRID_REFLECTION_MIRROR_F0_THRESHOLD;
    if (tier == 1u && !requiresFullQuality)
        tier = HybridDemoteReflectionTier(tier);
    if (broadAnisotropic)
        tier = HybridDemoteReflectionTier(tier);
    float importanceFloor = transmissive
        ? HYBRID_REFLECTION_TRANSMISSION_IMPORTANCE_FLOOR
        : startsInFullBand
            ? HYBRID_REFLECTION_GLOSSY_IMPORTANCE_FLOOR
            : 0.0;
    float remainingGloss = 1.0 - perceptualRoughness;
    float rayImportance = max(maximumF0, importanceFloor) *
        remainingGloss * remainingGloss * remainingGloss * remainingGloss *
        clamp(specularOcclusion, 0.0, 1.0);
    if (broadAnisotropic)
        rayImportance *= HYBRID_REFLECTION_BROAD_IMPORTANCE_SCALE;
    if (rayImportance < HYBRID_REFLECTION_MINIMUM_RAY_IMPORTANCE &&
        (tier != 4u || !requiresFullQuality))
        tier = HybridDemoteReflectionTier(tier);
    return tier;
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

vec3 HybridLimitBroadReflectionRadiance(
    vec3 radiance,
    float analyticReferenceMaximum)
{
    if (!HybridFinite(radiance))
        return vec3(0.0);
    vec3 nonnegative = max(radiance, vec3(0.0));
    float safeReference = HybridFinite(analyticReferenceMaximum)
        ? max(analyticReferenceMaximum, 0.0)
        : 0.0;
    // A single SSR/ray-query sample is not an integration of a broad lobe.
    // Bound it to the prefiltered analytic lobe so an HDR sky texel cannot
    // become a long-lived firefly, while retaining several stops of local
    // scene contrast. Sharp/high-value materials bypass this helper.
    float maximum = max(4.0,
        safeReference * HYBRID_REFLECTION_RADIANCE_LIMIT_SCALE + 1.0);
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

const uint HYBRID_REFLECTION_HISTORY_IDENTITY_MASK = 0x003fffffu;
const uint HYBRID_REFLECTION_HISTORY_SPARSE_NONE = 0u;
const uint HYBRID_REFLECTION_HISTORY_SPARSE_RESOLUTION = 1u;
const uint HYBRID_REFLECTION_HISTORY_SPARSE_RAY_BUDGET = 2u;
const uint HYBRID_REFLECTION_HISTORY_SPARSE_RESERVED = 3u;

uvec2 HybridPackHistoryMetadata(
    uint identity,
    float depth,
    vec3 normal,
    uint source,
    uint age,
    uint sparseState,
    bool valid)
{
    if (!valid)
        return uvec2(0u);
    uint depth16 = packHalf2x16(vec2(clamp(depth, 0.0, 1.0), 0.0)) &
        0xffffu;
    uint normal16 = packSnorm4x8(vec4(
        NjulfHybridReflectionOctEncode(normal), 0.0, 0.0)) & 0xffffu;
    uint word0 = (identity & HYBRID_REFLECTION_HISTORY_IDENTITY_MASK) |
        ((depth16 & 0x03ffu) << 22u);
    uint word1 = ((depth16 >> 10u) & 0x003fu) |
        (normal16 << 6u) |
        ((source & 0x7u) << 22u) |
        ((min(age, 31u) & 0x1fu) << 25u) |
        ((sparseState & 0x3u) << 30u);
    return uvec2(word0, word1);
}

uint HybridHistoryMetadataIdentity(uvec2 metadata)
{
    return metadata.x & HYBRID_REFLECTION_HISTORY_IDENTITY_MASK;
}

float HybridHistoryMetadataDepth(uvec2 metadata)
{
    uint packed = ((metadata.x >> 22u) & 0x03ffu) |
        ((metadata.y & 0x003fu) << 10u);
    return unpackHalf2x16(packed).x;
}

vec3 HybridHistoryMetadataNormal(uvec2 metadata)
{
    uint packed = (metadata.y >> 6u) & 0xffffu;
    vec2 encoded = unpackSnorm4x8(packed).xy;
    vec3 normal = vec3(
        encoded,
        1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return normalize(normal);
}

uint HybridHistoryMetadataSource(uvec2 metadata)
{
    return (metadata.y >> 22u) & 0x7u;
}

uint HybridHistoryMetadataAge(uvec2 metadata)
{
    return (metadata.y >> 25u) & 0x1fu;
}

uint HybridHistoryMetadataSparseState(uvec2 metadata)
{
    return metadata.y >> 30u;
}

bool HybridHistoryMetadataValid(uvec2 metadata)
{
    return HybridHistoryMetadataSource(metadata) !=
        HYBRID_REFLECTION_SOURCE_NONE;
}

uint HybridReceiverIdentity(uvec4 payload)
{
    return payload.w & NJULF_HYBRID_REFLECTION_IDENTITY_MASK;
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

void HybridAccumulateScreenCounter(uint counterIndex, uvec2 pixel)
{
    uint sampleKey = pixel.x * 0x9e3779b9u ^
        pixel.y * 0x85ebca6bu ^ counterIndex * 0xc2b2ae35u;
    if ((HybridHash(sampleKey) &
            HYBRID_REFLECTION_SCREEN_COUNTER_SAMPLE_MASK) == 0u)
    {
        atomicAdd(HybridCounters[counterIndex],
            HYBRID_REFLECTION_SCREEN_COUNTER_SAMPLE_WEIGHT);
    }
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

void HybridReflectionAnisotropicAxes(
    float roughness,
    float anisotropyStrength,
    out float alphaX,
    out float alphaY)
{
    float alpha = max(roughness * roughness, 0.001);
    float aspect = sqrt(max(1.0 -
        0.9 * clamp(anisotropyStrength, 0.0, 1.0), 0.1));
    alphaX = max(alpha / aspect, 0.001);
    alphaY = max(alpha * aspect, 0.001);
}

vec2 HybridReflectionRandom2(uint seed)
{
    uint first = HybridHash(seed);
    uint second = HybridHash(first ^ 0x68bc21ebu);
    return vec2(first & 0x00ffffffu, second & 0x00ffffffu) *
        (1.0 / 16777216.0);
}

vec3 HybridReflectionSampleDirection(
    vec3 viewDirection,
    vec3 normal,
    vec3 tangent,
    float roughness,
    float anisotropyStrength,
    uint receiverIdentity,
    uvec2 pixel,
    uint temporalSampleIndex,
    uint lobeId)
{
    vec3 n = normalize(normal);
    if (roughness <= 0.06)
        return normalize(reflect(-viewDirection, n));

    vec3 t = tangent - n * dot(tangent, n);
    if (dot(t, t) <= 1.0e-12)
        t = NjulfHybridReflectionCanonicalTangentBasisX(n);
    else
        t = normalize(t);
    vec3 b = normalize(cross(n, t));
    float alphaX;
    float alphaY;
    HybridReflectionAnisotropicAxes(
        roughness, anisotropyStrength, alphaX, alphaY);

    vec3 localView = vec3(
        dot(viewDirection, t),
        dot(viewDirection, b),
        max(dot(viewDirection, n), 1.0e-5));
    vec3 stretchedView = normalize(vec3(
        alphaX * localView.x,
        alphaY * localView.y,
        localView.z));
    float lensq = dot(stretchedView.xy, stretchedView.xy);
    vec3 basis1 = lensq > 1.0e-10
        ? vec3(-stretchedView.y, stretchedView.x, 0.0) /
            sqrt(lensq)
        : vec3(1.0, 0.0, 0.0);
    vec3 basis2 = cross(stretchedView, basis1);
    uint seed = receiverIdentity ^ pixel.x * 0x9e3779b9u ^
        pixel.y * 0x85ebca6bu ^
        temporalSampleIndex * 0xc2b2ae35u ^
        lobeId * 0x27d4eb2fu;
    vec2 random = HybridReflectionRandom2(seed);
    float radius = sqrt(random.x);
    float phi = 2.0 * HYBRID_REFLECTION_PI * random.y;
    float diskX = radius * cos(phi);
    float diskY = radius * sin(phi);
    float blend = 0.5 * (1.0 + stretchedView.z);
    diskY = mix(sqrt(max(0.0, 1.0 - diskX * diskX)),
        diskY, blend);
    vec3 visibleNormal = diskX * basis1 + diskY * basis2 +
        sqrt(max(0.0, 1.0 - diskX * diskX - diskY * diskY)) *
            stretchedView;
    vec3 localHalf = normalize(vec3(
        alphaX * visibleNormal.x,
        alphaY * visibleNormal.y,
        max(visibleNormal.z, 0.0)));
    vec3 halfVector = normalize(
        t * localHalf.x + b * localHalf.y + n * localHalf.z);
    vec3 direction = normalize(reflect(-viewDirection, halfVector));
    return dot(direction, n) > 0.0
        ? direction
        : normalize(reflect(-viewDirection, n));
}

bool HybridAppendRayTask(
    uvec2 pixel,
    uint reason,
    uint identity,
    uint tier,
    uint lobeId,
    uvec2 lobeExtension,
    uint temporalSampleIndex,
    uint admissionThreshold)
{
    HybridAccumulateScreenCounter(
        HYBRID_REFLECTION_COUNTER_RAY_REQUESTS, pixel);
    uint admissionKey = pixel.x * 0x9e3779b9u ^
        pixel.y * 0x85ebca6bu ^
        temporalSampleIndex * 0xc2b2ae35u ^
        reason * 0x27d4eb2fu;
    if (HybridHash(admissionKey) > admissionThreshold)
        return false;

    uint taskIndex = atomicAdd(HybridTaskCount, 1u);
    if (taskIndex < HybridTaskCapacity)
    {
        HybridTasks[taskIndex].Primary = uvec4(pixel, reason,
            (identity & NJULF_HYBRID_REFLECTION_IDENTITY_MASK) |
            ((lobeId & 0x1u) << 22u) |
            ((tier & 0xffu) << 24u));
        HybridTasks[taskIndex].LobeExtension =
            uvec4(lobeExtension, 0u, 0u);
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
    if (source == HYBRID_REFLECTION_SOURCE_DDGI)
        return vec3(0.15, 1.0, 0.25);
    if (source == HYBRID_REFLECTION_SOURCE_LOCAL_PROBE)
        return vec3(1.0, 0.85, 0.0);
    if (source == HYBRID_REFLECTION_SOURCE_ENVIRONMENT)
        return vec3(0.1, 0.3, 1.0);
    if (source == HYBRID_REFLECTION_SOURCE_PLANAR)
        return vec3(1.0, 0.35, 0.05);
    return vec3(0.0);
}

#endif
