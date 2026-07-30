#ifndef NJULF_FOLIAGE_COVERAGE_GLSL
#define NJULF_FOLIAGE_COVERAGE_GLSL

#include "material_alpha.glsl"

// Shared by foliage depth and forward.  It intentionally owns every discard decision that
// affects coverage so the depth buffer cannot contain foliage pixels that forward rejects.
bool IsInsideFoliageLeafCard(vec2 uv)
{
    float y = clamp(uv.y, 0.0, 1.0);
    vec2 centered = vec2(uv.x * 2.0 - 1.0, y * 2.0 - 1.0);
    float halfWidth = mix(0.10, 0.62, sin(y * 3.14159265359));
    return abs(centered.x) <= halfWidth && abs(centered.y) <= 0.98;
}

vec4 SampleFoliageAlbedo(GPUMaterialData material, vec2 uv)
{
    bool valid = material.AlbedoTextureIndex >= FIRST_TEXTURE_INDEX &&
        material.AlbedoTextureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    return valid ? texture(BindlessTextures[nonuniformEXT(material.AlbedoTextureIndex)], uv) : vec4(1.0);
}

float HashFoliageCoverage01(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return float(value & 0x00ffffffu) / float(0x01000000u);
}

float StableFoliageCoverageDither(vec2 pixel, uint stableId)
{
    uvec2 p = uvec2(max(pixel, vec2(0.0)));
    return HashFoliageCoverage01(stableId ^ (p.x * 1973u) ^ (p.y * 9277u));
}

float FoliageLodCoverage(uint lodBand)
{
    if (lodBand == 0u)
        return 1.0;
    if (lodBand == 1u)
        return 0.88;
    return 0.72;
}

bool FoliageCoverageSurvives(
    GPUMaterialData material,
    vec2 uv,
    uint geometryMode,
    uint clusterIndex,
    uint lodBand,
    vec2 pixel,
    out vec4 sampledAlbedo)
{
    sampledAlbedo = SampleFoliageAlbedo(material, uv);

    if (geometryMode == 0u)
    {
        float taper = mix(0.35, 0.08, clamp(uv.y, 0.0, 1.0));
        if (abs(uv.x - 0.5) > taper)
            return false;
    }
    else if (geometryMode == 2u)
    {
        if (!IsInsideFoliageLeafCard(uv) || sampledAlbedo.a < 0.05)
            return false;
    }
    else if (!MaterialAlphaSurvivesRasterCoverage(
                 material.Albedo.a * sampledAlbedo.a,
                 material.NormalScaleBias.y,
                 material.NormalScaleBias.z))
    {
        return false;
    }

    uint stableId = clusterIndex ^ (lodBand * 0x9e3779b9u);
    return StableFoliageCoverageDither(pixel, stableId) <= FoliageLodCoverage(lodBand);
}

#endif // NJULF_FOLIAGE_COVERAGE_GLSL
