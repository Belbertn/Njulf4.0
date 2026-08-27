#ifndef NJULF_FORWARD_DDGI_RECEIVER_CACHE_GLSL
#define NJULF_FORWARD_DDGI_RECEIVER_CACHE_GLSL

#include "ddgi_receiver_surface.glsl"

// Compute prefilters the 12x12 exact-gather lattice to one FP16 value per 2x2
// screen block. A fragment quad therefore reads one shared texel while the
// only spatial approximation beyond the accepted bilinear field is bounded to
// a two-pixel footprint.
const uint FORWARD_DDGI_RECEIVER_CACHE_SCALE = 2u;

layout(std430, set = 2, binding = 0) readonly buffer ForwardDdgiReceiverCacheBlock
{
    uvec4 Entries[];
} ForwardDdgiReceiverCache;

layout(std430, set = 2, binding = 1) readonly buffer ForwardDdgiReceiverSurfaceBlock
{
    uvec2 Entries[];
} ForwardDdgiReceiverSurface;

struct ForwardDdgiReceiverCacheSample
{
    // Keep the packed value live until the consumer actually needs each
    // component. This preserves the single aligned cache read without carrying
    // six unpacked FP32 radiance lanes through IBL evaluation.
    uvec4 Packed;
};

struct ForwardDdgiReceiverCacheAdmission
{
    uint EntryIndex;
    uint Reason;
};

bool ForwardDdgiReceiverCacheAdmissionAccepted(
    ForwardDdgiReceiverCacheAdmission admission)
{
    return admission.Reason == SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED;
}

vec3 ForwardDdgiReceiverCacheAdmissionDebugColor(uint reason)
{
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED)
        return vec3(0.05, 0.95, 0.15);
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID ||
        reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE)
    {
        return vec3(0.95, 0.05, 0.85);
    }
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_DEPTH ||
        reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_POSITION)
    {
        return vec3(0.95, 0.05, 0.05);
    }
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_PLANE)
        return vec3(1.0, 0.45, 0.02);
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NORMAL)
        return vec3(0.05, 0.25, 1.0);
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INSUFFICIENT_SUPPORT)
        return vec3(1.0, 0.9, 0.05);
    // White is a fail-closed exact fallback with an unknown future reason.
    return vec3(1.0);
}

void RecordForwardDdgiReceiverCacheAdmission(
    uint frameIndex,
    uint reason)
{
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
    IncrementSimpleDdgiReceiverCacheDiagnostic(
        frameIndex,
        SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_CANDIDATE_COUNTER);
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED)
    {
        IncrementSimpleDdgiReceiverCacheDiagnostic(
            frameIndex,
            SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_ACCEPTED_COUNTER);
        return;
    }

    uint counter = SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_SUPPORT_COUNTER;
    if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_INVALID ||
        reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NON_FINITE)
    {
        counter = SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_INVALID_COUNTER;
    }
    else if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_DEPTH ||
             reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_POSITION)
    {
        counter = SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_DEPTH_POSITION_COUNTER;
    }
    else if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_PLANE)
    {
        counter = SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_PLANE_COUNTER;
    }
    else if (reason == SIMPLE_DDGI_RECEIVER_SURFACE_REJECT_NORMAL)
    {
        counter = SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_NORMAL_COUNTER;
    }
    IncrementSimpleDdgiReceiverCacheDiagnostic(frameIndex, counter);
    IncrementSimpleDdgiReceiverCacheDiagnostic(
        frameIndex,
        SIMPLE_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_COUNTER);
#endif
}

void RecordLegacyForwardDdgiReceiverCacheAdmission(uint frameIndex)
{
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
    IncrementSimpleDdgiReceiverCacheDiagnostic(
        frameIndex,
        SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_CANDIDATE_COUNTER);
    IncrementSimpleDdgiReceiverCacheDiagnostic(
        frameIndex,
        SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_ACCEPTED_COUNTER);
    IncrementSimpleDdgiReceiverCacheDiagnostic(
        frameIndex,
        SIMPLE_DDGI_RECEIVER_CACHE_LEGACY_FRAGMENT_COUNTER);
#endif
}

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
    ForwardDdgiReceiverCacheSample cacheSample,
    float perceptualRoughness)
{
    // The resolve output retains the gather metadata's upper-bit ABI:
    // [ unused:10 | visibility:10 | minimum roughness:6 | full roughness:6 ].
    uint metadata = cacheSample.Packed.w;
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

ForwardDdgiReceiverCacheAdmission EvaluateForwardDdgiReceiverCacheAdmission(
    vec2 fragmentCoordinate,
    float fragmentReverseZ,
    vec3 fragmentWorldPosition,
    vec3 fragmentGeometricNormal,
    GPUForwardPushConstants pushConstants)
{
    ForwardDdgiReceiverCacheAdmission result;
    result.EntryIndex = ForwardDdgiReceiverCacheEntryIndex(
        fragmentCoordinate,
        pushConstants.ScreenDimensions);
    uvec2 screenExtent = uvec2(max(
        round(pushConstants.ScreenDimensions),
        vec2(1.0)));
    uvec2 fragmentPixel = uvec2(clamp(
        fragmentCoordinate,
        vec2(0.0),
        vec2(screenExtent - uvec2(1u))));
    uvec2 cacheCoordinate = fragmentPixel >> uvec2(1u);
    // This sidecar load is deliberately the first cache payload read. The
    // sixteen-byte radiance record remains untouched on exact fallbacks.
    uvec2 surface = ForwardDdgiReceiverSurface.Entries[result.EntryIndex];
    result.Reason = SimpleDdgiReceiverSurfaceEvaluateFragment(
        surface,
        cacheCoordinate,
        FORWARD_DDGI_RECEIVER_CACHE_SCALE,
        fragmentPixel,
        fragmentReverseZ,
        fragmentWorldPosition,
        fragmentGeometricNormal,
        pushConstants.InverseProjectionMatrix,
        pushConstants.InverseViewMatrix,
        screenExtent,
        pushConstants.CameraPosition);
    return result;
}

ForwardDdgiReceiverCacheSample LoadForwardDdgiReceiverCache(
    uint entryIndex)
{
    ForwardDdgiReceiverCacheSample result;
    result.Packed = ForwardDdgiReceiverCache.Entries[entryIndex];
    return result;
}

ForwardDdgiReceiverCacheSample SampleForwardDdgiReceiverCache(
    vec2 fragmentCoordinate,
    vec2 screenDimensions)
{
    uint entryIndex = ForwardDdgiReceiverCacheEntryIndex(
        fragmentCoordinate,
        screenDimensions);
    // Sixteen-byte entries deliberately produce one naturally aligned vector
    // load. Leave the payload packed so only four words remain live through
    // IBL; diffuse composition unpacks the six FP16 lanes at first use.
    return LoadForwardDdgiReceiverCache(entryIndex);
}

vec3 ForwardDdgiReceiverCacheDdgiIrradiance(
    ForwardDdgiReceiverCacheSample cacheSample)
{
    vec2 ddgiXy = unpackHalf2x16(cacheSample.Packed.x);
    vec2 ddgiZEnvironmentX = unpackHalf2x16(cacheSample.Packed.y);
    return vec3(ddgiXy, ddgiZEnvironmentX.x);
}

vec3 ForwardDdgiReceiverCacheEnvironmentIrradiance(
    ForwardDdgiReceiverCacheSample cacheSample)
{
    vec2 ddgiZEnvironmentX = unpackHalf2x16(cacheSample.Packed.y);
    vec2 environmentYz = unpackHalf2x16(cacheSample.Packed.z);
    return vec3(ddgiZEnvironmentX.y, environmentYz);
}

#if !FORWARD_DDGI_RECEIVER_CACHE_LEGACY
const uint FORWARD_DDGI_DIRECTIONAL_GATHER_ENTRY_WORDS = 20u;
// Keep synchronized with SimpleDdgiReceiverFeedbackCaptureSourceAbi and the
// gather dispatch owned by ForwardPlusPass. This include is also compiled in
// artifacts that do not publish exact feedback, so it cannot depend on that
// optional producer's ABI header being present.
const uint FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE = 12u;
const uint FORWARD_DDGI_DIRECTIONAL_COEFFICIENT_WORD = 4u;
const uint FORWARD_DDGI_DIRECTIONAL_COEFFICIENT_WORDS = 14u;
const uint FORWARD_DDGI_DIRECTIONAL_SUPPORT_WORD = 18u;
const uint FORWARD_DDGI_DIRECTIONAL_FRAME_WORD = 19u;
const float FORWARD_DDGI_DIRECTIONAL_MINIMUM_COMPATIBLE_WEIGHT = 0.125;

bool SampleForwardDdgiCompactDirectionalRadiance(
    vec2 fragmentCoordinate,
    float fragmentReverseZ,
    vec3 fragmentWorldPosition,
    vec3 fragmentGeometricNormal,
    vec3 queryDirection,
    float perceptualRoughness,
    uint directionalMode,
    uint ddgiFrameIndex,
    GPUForwardPushConstants pushConstants,
    out vec3 radiance,
    out float confidence)
{
    radiance = vec3(0.0);
    confidence = 0.0;
    uint coefficientCount =
        SimpleDdgiRadianceShCoefficientCount(directionalMode);
    float directionLengthSquared = dot(queryDirection, queryDirection);
    if (coefficientCount == 0u ||
        !(directionLengthSquared > 1.0e-12) ||
        isnan(directionLengthSquared) || isinf(directionLengthSquared))
    {
        return false;
    }

    uvec2 screenExtent = uvec2(max(
        round(pushConstants.ScreenDimensions),
        vec2(1.0)));
    uvec2 fragmentPixel = uvec2(clamp(
        fragmentCoordinate,
        vec2(0.0),
        vec2(screenExtent - uvec2(1u))));
    uint gatherWidth =
        (screenExtent.x + FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE - 1u) /
        FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE;
    uint gatherHeight =
        (screenExtent.y + FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE - 1u) /
        FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE;
    vec2 latticePosition =
        (fragmentCoordinate - vec2(0.5)) /
            float(FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE) -
        vec2(0.5);
    ivec2 baseCoordinate = ivec2(floor(latticePosition));
    vec2 blend = fract(latticePosition);
    const ivec2 candidateOffsets[4] = ivec2[](
        ivec2(0, 0),
        ivec2(1, 0),
        ivec2(0, 1),
        ivec2(1, 1));
    vec4 candidateWeights = vec4(
        (1.0 - blend.x) * (1.0 - blend.y),
        blend.x * (1.0 - blend.y),
        (1.0 - blend.x) * blend.y,
        blend.x * blend.y);
    vec3 coefficients[9];
    for (uint coefficient = 0u; coefficient < 9u; coefficient++)
        coefficients[coefficient] = vec3(0.0);

    uint frameBank = pushConstants.CurrentFrameIndex & 1u;
    uint gatherBufferIndex =
        uint(SIMPLE_DDGI_RECEIVER_GATHER_BUFFER_BASE_INDEX) + frameBank;
    uint gatherSurfaceBufferIndex =
        uint(SIMPLE_DDGI_RECEIVER_GATHER_SURFACE_BUFFER_BASE_INDEX) +
        frameBank;
    float compatibleWeight = 0.0;
    float supportedWeight = 0.0;
    for (uint candidateIndex = 0u; candidateIndex < 4u; candidateIndex++)
    {
        ivec2 coordinate =
            baseCoordinate + candidateOffsets[candidateIndex];
        if (any(lessThan(coordinate, ivec2(0))) ||
            any(greaterThanEqual(
                coordinate,
                ivec2(gatherWidth, gatherHeight))))
        {
            continue;
        }

        uint entryIndex = uint(coordinate.y) * gatherWidth +
            uint(coordinate.x);
        uint entryWord = entryIndex *
            FORWARD_DDGI_DIRECTIONAL_GATHER_ENTRY_WORDS;
        if (ReadStorageWordUniform(
                gatherBufferIndex,
                entryWord + FORWARD_DDGI_DIRECTIONAL_FRAME_WORD) !=
            ddgiFrameIndex)
        {
            continue;
        }

        uint surfaceWord = entryIndex * 2u;
        uvec2 surface = uvec2(
            ReadStorageWordUniform(
                gatherSurfaceBufferIndex,
                surfaceWord),
            ReadStorageWordUniform(
                gatherSurfaceBufferIndex,
                surfaceWord + 1u));
        uint reason = SimpleDdgiReceiverSurfaceEvaluateFragment(
            surface,
            uvec2(coordinate),
            FORWARD_DDGI_DIRECTIONAL_GATHER_SCALE,
            fragmentPixel,
            fragmentReverseZ,
            fragmentWorldPosition,
            fragmentGeometricNormal,
            pushConstants.InverseProjectionMatrix,
            pushConstants.InverseViewMatrix,
            screenExtent,
            pushConstants.CameraPosition);
        if (reason != SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED)
            continue;

        float candidateWeight = candidateWeights[candidateIndex];
        compatibleWeight += candidateWeight;
        float support = unpackHalf2x16(ReadStorageWordUniform(
            gatherBufferIndex,
            entryWord + FORWARD_DDGI_DIRECTIONAL_SUPPORT_WORD)).x;
        if (!(support > 0.0) || isnan(support) || isinf(support))
            continue;
        float weightedSupport = candidateWeight * clamp(support, 0.0, 1.0);
        supportedWeight += weightedSupport;
        for (uint word = 0u;
             word < FORWARD_DDGI_DIRECTIONAL_COEFFICIENT_WORDS;
             word++)
        {
            vec2 values = unpackHalf2x16(ReadStorageWordUniform(
                gatherBufferIndex,
                entryWord + FORWARD_DDGI_DIRECTIONAL_COEFFICIENT_WORD +
                    word));
            if (any(isnan(values)) || any(isinf(values)))
                return false;
            uint firstValueIndex = word * 2u;
            if (firstValueIndex < coefficientCount * 3u)
            {
                coefficients[firstValueIndex / 3u]
                    [firstValueIndex % 3u] += values.x * weightedSupport;
            }
            uint secondValueIndex = firstValueIndex + 1u;
            if (secondValueIndex < coefficientCount * 3u)
            {
                coefficients[secondValueIndex / 3u]
                    [secondValueIndex % 3u] += values.y * weightedSupport;
            }
        }
    }

    if (compatibleWeight <
            FORWARD_DDGI_DIRECTIONAL_MINIMUM_COMPATIBLE_WEIGHT ||
        supportedWeight <= 0.000001)
    {
        return false;
    }

    float basis[9];
    SimpleDdgiEvaluateRadianceShL2Basis(
        queryDirection * inversesqrt(directionLengthSquared),
        basis);
    vec3 bandScales = SimpleDdgiGgxBandScales(perceptualRoughness);
    vec3 reconstructed = vec3(0.0);
    for (uint coefficient = 0u;
         coefficient < coefficientCount;
         coefficient++)
    {
        uint band = coefficient == 0u ? 0u :
            coefficient <= 3u ? 1u : 2u;
        reconstructed +=
            (coefficients[coefficient] / supportedWeight) *
            (basis[coefficient] * bandScales[band]);
    }
    if (any(isnan(reconstructed)) || any(isinf(reconstructed)))
        return false;

    radiance = max(reconstructed, vec3(0.0));
    confidence = clamp(supportedWeight / compatibleWeight, 0.0, 1.0);
    return confidence > 0.000001;
}
#endif

#endif
