#ifndef NJULF_RAY_QUERY_SURFACE_GLSL
#define NJULF_RAY_QUERY_SURFACE_GLSL

// The reconstruction implementation retains its old symbol spellings for
// one compatibility ABI. New ray-query consumers use only this neutral
// facade; the caustic, hybrid-reflection, DDGI, and thick-transmission paths
// therefore share identical instance validation, topology reconstruction,
// material UVs, opacity, and candidate limits.
#include "gi_caustic_ray_query.glsl"

#define RayQuerySurfaceHit GiCausticRayHit

bool RayQuerySurfaceResolveCommittedHit(
    rayQueryEXT query,
    vec3 rayOrigin,
    vec3 rayDirection,
    out RayQuerySurfaceHit hit)
{
    return GiCausticResolveCommittedHit(
        query, rayOrigin, rayDirection, hit);
}

bool RayQuerySurfaceTraceNearest(
    vec3 origin,
    vec3 direction,
    float maximumDistance,
    out RayQuerySurfaceHit hit)
{
    return GiCausticTraceNearest(origin, direction, maximumDistance, hit);
}

bool RayQuerySurfaceTraceNearestBounded(
    vec3 origin,
    vec3 direction,
    float maximumDistance,
    uint candidateLimit,
    out uint candidateCount,
    out bool candidateBudgetExceeded,
    out RayQuerySurfaceHit hit)
{
    candidateCount = 0u;
    candidateBudgetExceeded = false;
    if (!GiCausticRayFinite(origin) || !GiCausticRayFinite(direction) ||
        !GiCausticRayFinite(maximumDistance) ||
        maximumDistance <= GI_CAUSTIC_RAY_EPSILON || candidateLimit == 0u)
    {
        return false;
    }

    rayQueryEXT query;
    rayQueryInitializeEXT(
        query,
        SceneTlas,
        gl_RayFlagsNoneEXT,
        0xff,
        origin,
        GI_CAUSTIC_RAY_EPSILON,
        direction,
        maximumDistance);
    while (rayQueryProceedEXT(query))
    {
        if (rayQueryGetIntersectionTypeEXT(query, false) !=
            gl_RayQueryCandidateIntersectionTriangleEXT)
        {
            continue;
        }
        ++candidateCount;
        if (candidateCount > candidateLimit)
        {
            candidateBudgetExceeded = true;
            rayQueryTerminateEXT(query);
            break;
        }
        uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(
            query, false);
        uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(
            query, false);
        vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(
            query, false);
        bool frontFacing = rayQueryGetIntersectionFrontFaceEXT(query, false);
        if (GiCausticCandidatePassesOpacity(
                instanceIndex,
                primitiveIndex,
                barycentrics,
                frontFacing))
        {
            rayQueryConfirmIntersectionEXT(query);
        }
    }
    if (candidateBudgetExceeded ||
        rayQueryGetIntersectionTypeEXT(query, true) ==
            gl_RayQueryCommittedIntersectionNoneEXT)
    {
        return false;
    }
    return RayQuerySurfaceResolveCommittedHit(
        query, origin, direction, hit);
}

vec3 RayQuerySurfaceOrientedNormal(RayQuerySurfaceHit hit)
{
    return GiCausticOrientedNormal(hit);
}

vec2 RayQuerySurfaceMaterialUv(
    RayQuerySurfaceHit hit,
    float texCoordSet,
    vec4 offsetScale,
    float rotation)
{
    return GiCausticMaterialUv(
        hit, texCoordSet, offsetScale, rotation);
}

vec4 RayQuerySurfaceSampleBaseColor(RayQuerySurfaceHit hit)
{
    return GiCausticSampleBaseColor(hit);
}

vec2 RayQuerySurfaceSampleMetallicRoughness(RayQuerySurfaceHit hit)
{
    return GiCausticSampleMetallicRoughness(hit);
}

vec3 RayQuerySurfaceSampleEmissive(RayQuerySurfaceHit hit)
{
    vec3 sampleValue = vec3(1.0);
    if (hit.Material.EmissiveTextureIndex >= FIRST_TEXTURE_INDEX &&
        hit.Material.EmissiveTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        sampleValue = textureLod(
            BindlessTextures[nonuniformEXT(
                hit.Material.EmissiveTextureIndex)],
            RayQuerySurfaceMaterialUv(
                hit,
                hit.Material.TextureTexCoordSets.w,
                hit.Material.EmissiveOffsetScale,
                hit.Material.TextureRotations.w),
            max(hit.Material.DdgiMaterialPolicy.y, 0.0)).rgb;
    }
    return max(hit.Material.Emissive.rgb * sampleValue, vec3(0.0));
}

bool RayQuerySurfaceIsDiffuseReceiver(RayQuerySurfaceHit hit)
{
    return GiCausticHitIsDiffuseReceiver(hit);
}

#endif // NJULF_RAY_QUERY_SURFACE_GLSL
