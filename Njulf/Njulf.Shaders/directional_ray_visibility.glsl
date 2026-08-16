#ifndef NJULF_DIRECTIONAL_RAY_VISIBILITY_GLSL
#define NJULF_DIRECTIONAL_RAY_VISIBILITY_GLSL

#include "ray_scene_alpha.glsl"

const uint DIRECTIONAL_RAY_MAX_ALPHA_CANDIDATES = 64u;

// Immutable 8x8 blue-noise rank tile.  The values are a full permutation, so
// every rank occurs exactly once; a frame-dependent Cranley rotation removes
// the small tile's stationary phase while preserving deterministic captures.
const uint DirectionalBlueNoiseRank[64] = uint[64](
    18u, 48u,  6u, 38u, 22u, 54u, 10u, 42u,
    60u, 28u, 52u, 16u, 63u, 31u, 45u, 13u,
     2u, 34u, 24u, 56u,  0u, 36u, 26u, 58u,
    44u, 12u, 40u,  8u, 46u, 14u, 32u,  4u,
    21u, 53u, 11u, 43u, 19u, 49u,  7u, 39u,
    62u, 30u, 47u, 15u, 59u, 27u, 51u, 17u,
     1u, 37u, 25u, 57u,  3u, 35u, 23u, 55u,
    41u,  9u, 33u,  5u, 61u, 29u, 50u, 20u);

bool DirectionalRayFinite(float value)
{
    return !isnan(value) && !isinf(value);
}

bool DirectionalRayFinite(vec3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

uint DirectionalRayHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

vec2 DirectionalLowDiscrepancyDiskSample(
    uvec2 pixel,
    uint sequenceIndex)
{
    uint rank = DirectionalBlueNoiseRank[(pixel.y & 7u) * 8u +
        (pixel.x & 7u)];
    uint seed = DirectionalRayHash(
        (pixel.x >> 3u) * 0x9e3779b9u ^
        (pixel.y >> 3u) * 0x85ebca6bu ^
        rank * 0x27d4eb2du);
    float blue = (float(rank) + 0.5) / 64.0;
    float rotation0 = fract(blue + float(seed & 0xffffu) / 65536.0);
    float rotation1 = fract(float(seed >> 16u) / 65536.0 + blue * 0.61803398875);
    // Four sequence slots are reserved per temporal frame so recovery frames
    // cannot repeat samples when their ray count differs from ordinary frames.
    float sequence = float(sequenceIndex) + 0.5;
    float u = fract(sequence * 0.7548776662 + rotation0);
    float v = fract(sequence * 0.5698402909 + rotation1);
    float radius = sqrt(max(u, 0.0));
    float angle = 6.28318530718 * v;
    return radius * vec2(cos(angle), sin(angle));
}

vec3 DirectionalSampleSunDirection(
    vec3 centerDirection,
    uvec2 pixel,
    uint frameIndex,
    uint sampleIndex,
    float angularRadius,
    bool finiteSun)
{
    if (!finiteSun || angularRadius <= 0.0)
        return centerDirection;

    vec3 axis = abs(centerDirection.z) < 0.999
        ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, 1.0, 0.0);
    vec3 tangent = normalize(cross(axis, centerDirection));
    vec3 bitangent = cross(centerDirection, tangent);
    vec2 disk = DirectionalLowDiscrepancyDiskSample(
        pixel,
        frameIndex * 4u + sampleIndex);
    return normalize(centerDirection +
        (tangent * disk.x + bitangent * disk.y) * tan(angularRadius));
}

bool DirectionalIntersectQualifiedBounds(
    vec3 origin,
    vec3 direction,
    float configuredMaximum,
    out float boundedMaximum)
{
    vec4 minimumData = ReadDirectionalShadowRaySceneBoundsMinimum();
    vec4 maximumData = ReadDirectionalShadowRaySceneBoundsMaximum();
    boundedMaximum = configuredMaximum;
    if (minimumData.w < 0.5 || maximumData.w < 0.5 ||
        !DirectionalRayFinite(minimumData.xyz) ||
        !DirectionalRayFinite(maximumData.xyz) ||
        any(greaterThan(minimumData.xyz, maximumData.xyz)))
    {
        return false;
    }

    vec3 safeDirection = vec3(
        abs(direction.x) > 1.0e-8 ? direction.x :
            (direction.x < 0.0 ? -1.0e-8 : 1.0e-8),
        abs(direction.y) > 1.0e-8 ? direction.y :
            (direction.y < 0.0 ? -1.0e-8 : 1.0e-8),
        abs(direction.z) > 1.0e-8 ? direction.z :
            (direction.z < 0.0 ? -1.0e-8 : 1.0e-8));
    vec3 inverseDirection = 1.0 / safeDirection;
    vec3 t0 = (minimumData.xyz - origin) * inverseDirection;
    vec3 t1 = (maximumData.xyz - origin) * inverseDirection;
    vec3 nearValues = min(t0, t1);
    vec3 farValues = max(t0, t1);
    float entry = max(max(nearValues.x, nearValues.y), nearValues.z);
    float exitDistance = min(min(farValues.x, farValues.y), farValues.z);
    if (!DirectionalRayFinite(entry) || !DirectionalRayFinite(exitDistance) ||
        exitDistance <= max(entry, 0.0))
    {
        return false;
    }
    boundedMaximum = min(configuredMaximum, max(exitDistance, 0.0));
    return DirectionalRayFinite(boundedMaximum) && boundedMaximum > 0.0;
}

bool DirectionalTraceVisibility(
    accelerationStructureEXT sceneTlas,
    vec3 origin,
    vec3 direction,
    float maximumDistance,
    float primaryFootprint,
    uint instanceMask,
    out float hitDistance,
    out uint candidates,
    out uint alphaSamples,
    out bool capHit)
{
    hitDistance = maximumDistance;
    candidates = 0u;
    alphaSamples = 0u;
    capHit = false;
    rayQueryEXT query;
    rayQueryInitializeEXT(
        query,
        sceneTlas,
        gl_RayFlagsTerminateOnFirstHitEXT,
        instanceMask & 0xffu,
        origin,
        0.0001,
        direction,
        maximumDistance);

    while (rayQueryProceedEXT(query))
    {
        if (rayQueryGetIntersectionTypeEXT(query, false) !=
            gl_RayQueryCandidateIntersectionTriangleEXT)
        {
            continue;
        }

        ++candidates;
        if (candidates > DIRECTIONAL_RAY_MAX_ALPHA_CANDIDATES)
        {
            capHit = true;
            rayQueryConfirmIntersectionEXT(query);
            rayQueryTerminateEXT(query);
            break;
        }

        bool sampledAlpha;
        bool blocks = RaySceneCandidateBlocksDirectionalShadow(
            rayQueryGetIntersectionInstanceCustomIndexEXT(query, false),
            rayQueryGetIntersectionPrimitiveIndexEXT(query, false),
            rayQueryGetIntersectionBarycentricsEXT(query, false),
            rayQueryGetIntersectionFrontFaceEXT(query, false),
            rayQueryGetIntersectionObjectToWorldEXT(query, false),
            primaryFootprint,
            sampledAlpha);
        alphaSamples += sampledAlpha ? 1u : 0u;
        if (blocks)
        {
            rayQueryConfirmIntersectionEXT(query);
            rayQueryTerminateEXT(query);
            break;
        }
    }

    bool blocked = rayQueryGetIntersectionTypeEXT(query, true) !=
        gl_RayQueryCommittedIntersectionNoneEXT;
    if (blocked)
        hitDistance = rayQueryGetIntersectionTEXT(query, true);
    return blocked;
}

#endif // NJULF_DIRECTIONAL_RAY_VISIBILITY_GLSL
