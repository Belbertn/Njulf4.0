#version 460
#extension GL_GOOGLE_include_directive : require

#include "material_alpha.glsl"

layout(set = 0, binding = 0) uniform sampler2D AlphaMaskTexture;
layout(std430, set = 0, binding = 1) buffer AlphaVisibilityResults
{
    uint Values[];
} Results;

layout(push_constant) uniform AlphaVisibilityPushConstants
{
    uint DistanceIndex;
    uint Width;
    uint Height;
    float Distance;
    float RayTextureLod;
    float AlphaCutoff;
    uint SampleCount;
    uint DistanceCount;
} pc;

layout(location = 0) in vec2 fragTexCoord;

const uint ALPHA_VISIBILITY_RASTER_CANDIDATE_PLANE = 0u;
const uint ALPHA_VISIBILITY_RASTER_COVERED_PLANE = 1u;
const float ALPHA_VISIBILITY_MASK_MODE = 1.0;

uint AlphaVisibilityResultIndex(uint plane, uvec2 pixel)
{
    uint planeStride = pc.SampleCount * pc.DistanceCount;
    return plane * planeStride +
        pc.DistanceIndex * pc.SampleCount +
        pixel.y * pc.Width +
        pixel.x;
}

void main()
{
    uvec2 pixel = uvec2(gl_FragCoord.xy);
    if (pixel.x >= pc.Width || pixel.y >= pc.Height)
        return;

    Results.Values[
        AlphaVisibilityResultIndex(
            ALPHA_VISIBILITY_RASTER_CANDIDATE_PLANE,
            pixel)] = 1u;

    float alpha = texture(AlphaMaskTexture, fragTexCoord).a;
    if (!MaterialAlphaSurvivesRasterCoverage(
            alpha,
            ALPHA_VISIBILITY_MASK_MODE,
            pc.AlphaCutoff))
    {
        discard;
    }

    Results.Values[
        AlphaVisibilityResultIndex(
            ALPHA_VISIBILITY_RASTER_COVERED_PLANE,
            pixel)] = 1u;
}
