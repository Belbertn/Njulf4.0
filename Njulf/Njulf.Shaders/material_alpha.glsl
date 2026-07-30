#ifndef NJULF_MATERIAL_ALPHA_GLSL
#define NJULF_MATERIAL_ALPHA_GLSL

// Generated-equivalent mirror: MaterialAlphaCoverageContract.cs.
//
// Keep alpha-mode decoding and comparison order centralized here. The
// raster/depth/shadow/scene-surface paths need compositing coverage, while
// DDGI/far-field occupancy must additionally reject BLEND because transparent
// geometry has a separate authoritative representation.
const int MATERIAL_ALPHA_MODE_OPAQUE = 0;
const int MATERIAL_ALPHA_MODE_MASK = 1;
const int MATERIAL_ALPHA_MODE_BLEND = 2;

int DecodeMaterialAlphaMode(float alphaMode)
{
    return int(round(alphaMode));
}

bool MaterialAlphaSurvivesRasterCoverage(
    float alpha,
    float alphaMode,
    float alphaCutoff)
{
    int mode = DecodeMaterialAlphaMode(alphaMode);
    if (mode == MATERIAL_ALPHA_MODE_MASK)
        return alpha >= alphaCutoff;
    if (mode == MATERIAL_ALPHA_MODE_BLEND)
        return alpha > 0.0;
    return true;
}

bool MaterialAlphaOccupiesOpaqueTransport(
    float alpha,
    float alphaMode,
    float alphaCutoff)
{
    int mode = DecodeMaterialAlphaMode(alphaMode);
    if (mode == MATERIAL_ALPHA_MODE_BLEND)
        return false;
    if (mode == MATERIAL_ALPHA_MODE_MASK)
        return alpha >= alphaCutoff;
    return true;
}

#endif // NJULF_MATERIAL_ALPHA_GLSL
