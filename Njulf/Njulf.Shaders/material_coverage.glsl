#ifndef NJULF_MATERIAL_COVERAGE_GLSL
#define NJULF_MATERIAL_COVERAGE_GLSL

#include "material_alpha.glsl"

// The depth, shadow, and forward passes must use this exact alpha contract.  Keeping the
// sampling and alpha-mode decisions here prevents a depth prepass hole from drifting from
// the corresponding shaded pixel when a material feature changes.
struct MaterialAlphaCoverage
{
    float Alpha;
    float AlphaMode;
    float AlphaCutoff;
};

MaterialAlphaCoverage ResolveMaterialAlphaCoverage(
    GPUMaterialData material,
    vec4 albedoSample,
    float vertexAlpha)
{
    MaterialAlphaCoverage coverage;
    coverage.Alpha = material.Albedo.a * albedoSample.a * vertexAlpha;
    coverage.AlphaMode = material.NormalScaleBias.y;
    coverage.AlphaCutoff = material.NormalScaleBias.z;
    return coverage;
}

vec4 SampleMaterialCoverageTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
#ifdef NJULF_VISIBILITY_COMPUTE
    return textureGrad(BindlessTextures[nonuniformEXT(safeIndex)], uv, dFdx(uv), dFdy(uv));
#else
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
#endif
}

vec2 MaterialCoverageUv(vec2 texCoord0, vec2 texCoord1, float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = int(round(texCoordSet)) == 1 ? texCoord1 : texCoord0;
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

MaterialAlphaCoverage EvaluateMaterialAlphaCoverage(
    GPUMaterialData material,
    vec2 texCoord0,
    vec2 texCoord1,
    float vertexAlpha)
{
    vec2 uv = MaterialCoverageUv(
        texCoord0,
        texCoord1,
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialCoverageTexture(material.AlbedoTextureIndex, uv);

    return ResolveMaterialAlphaCoverage(
        material,
        albedoSample,
        vertexAlpha);
}

bool MaterialCoverageSurvivesForward(MaterialAlphaCoverage coverage)
{
    return MaterialAlphaSurvivesRasterCoverage(
        coverage.Alpha,
        coverage.AlphaMode,
        coverage.AlphaCutoff);
}

#endif // NJULF_MATERIAL_COVERAGE_GLSL
