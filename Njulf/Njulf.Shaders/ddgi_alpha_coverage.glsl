#ifndef NJULF_DDGI_ALPHA_COVERAGE_GLSL
#define NJULF_DDGI_ALPHA_COVERAGE_GLSL

#include "material_alpha.glsl"

// Authoritative alpha composition for a DDGI ray-query triangle candidate.
// Production DDGI hit shading and the standalone hardware qualification gate
// both call this helper. Keeping texture sampling outside the helper preserves
// the production path's explicit-LOD policy while making the actual occupancy
// decision impossible to fork.
float ComposeDdgiCandidateAlpha(
    float materialAlpha,
    float vertexAlpha,
    float sampledTextureAlpha)
{
    return clamp(materialAlpha, 0.0, 1.0) *
        clamp(vertexAlpha, 0.0, 1.0) *
        clamp(sampledTextureAlpha, 0.0, 1.0);
}

bool DdgiAlphaCandidateOccupiesOpaqueTransport(
    float materialAlpha,
    float vertexAlpha,
    float sampledTextureAlpha,
    float alphaMode,
    float alphaCutoff)
{
    return MaterialAlphaOccupiesOpaqueTransport(
        ComposeDdgiCandidateAlpha(
            materialAlpha,
            vertexAlpha,
            sampledTextureAlpha),
        alphaMode,
        alphaCutoff);
}

#endif // NJULF_DDGI_ALPHA_COVERAGE_GLSL
