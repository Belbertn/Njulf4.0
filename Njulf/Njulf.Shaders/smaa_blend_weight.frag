#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outWeights;

layout(push_constant) uniform AntiAliasingPushBlock
{
    vec2 SourceDimensions;
    vec2 InvSourceDimensions;
    uint InputTextureIndex;
    uint SmaaEdgesTextureIndex;
    uint SmaaBlendWeightsTextureIndex;
    uint SmaaAreaTextureIndex;
    uint SmaaSearchTextureIndex;
    float FxaaContrastThreshold;
    float FxaaRelativeThreshold;
    float FxaaSubpixelBlending;
    float SmaaThreshold;
    uint SmaaMaxSearchSteps;
    uint SmaaMaxSearchStepsDiagonal;
    float SmaaCornerRounding;
    uint DebugView;
    uint OutputToSrgb;
    uint SmaaQuality;
    uint SmaaDiagonalEnabled;
    uint SmaaCornerEnabled;
    float TaaFeedbackMin;
    float TaaFeedbackMax;
    float TaaVelocityRejectionScale;
    uint TaaHistoryValid;
} pc;

float SearchDistance(uint edgesTextureIndex, vec2 uv, vec2 direction, int channel)
{
    float distanceValue = 0.0;
    int maxSteps = int(clamp(pc.SmaaMaxSearchSteps, 4u, 32u));
    for (int i = 1; i <= 32; i++)
    {
        if (i > maxSteps)
            break;

        vec2 edge = texture(BindlessTextures[nonuniformEXT(int(edgesTextureIndex))], uv + direction * pc.InvSourceDimensions * float(i)).rg;
        if (edge[channel] < 0.5)
            break;

        distanceValue += 1.0;
    }

    return distanceValue;
}

float DiagonalWeight(uint edgesTextureIndex, vec2 uv)
{
    if (pc.SmaaDiagonalEnabled == 0u || pc.SmaaMaxSearchStepsDiagonal == 0u)
        return 0.0;

    int maxSteps = int(clamp(pc.SmaaMaxSearchStepsDiagonal, 1u, 16u));
    float diagonal = 0.0;
    for (int i = 1; i <= 16; i++)
    {
        if (i > maxSteps)
            break;

        vec2 offset = pc.InvSourceDimensions * float(i);
        diagonal += max(texture(BindlessTextures[nonuniformEXT(int(edgesTextureIndex))], uv + offset).r,
            texture(BindlessTextures[nonuniformEXT(int(edgesTextureIndex))], uv - offset).g);
        diagonal += max(texture(BindlessTextures[nonuniformEXT(int(edgesTextureIndex))], uv + vec2(offset.x, -offset.y)).r,
            texture(BindlessTextures[nonuniformEXT(int(edgesTextureIndex))], uv + vec2(-offset.x, offset.y)).g);
    }

    return clamp(diagonal / max(float(maxSteps) * 2.0, 1.0), 0.0, 1.0);
}

void main()
{
    vec2 edges = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaEdgesTextureIndex))], inUv).rg;
    vec4 lutScale = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaAreaTextureIndex))], vec2(0.5)).rgba *
        texture(BindlessTextures[nonuniformEXT(int(pc.SmaaSearchTextureIndex))], vec2(0.5)).rgba;

    float left = edges.r > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(-1.0, 0.0), 0) : 0.0;
    float right = edges.r > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(1.0, 0.0), 0) : 0.0;
    float up = edges.g > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(0.0, -1.0), 1) : 0.0;
    float down = edges.g > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(0.0, 1.0), 1) : 0.0;

    float horizontal = edges.r * clamp((left + right) / max(float(pc.SmaaMaxSearchSteps), 1.0), 0.0, 1.0);
    float vertical = edges.g * clamp((up + down) / max(float(pc.SmaaMaxSearchSteps), 1.0), 0.0, 1.0);
    float diagonal = DiagonalWeight(pc.SmaaEdgesTextureIndex, inUv);
    horizontal = max(horizontal, edges.r * diagonal * 0.5);
    vertical = max(vertical, edges.g * diagonal * 0.5);

    if (pc.SmaaCornerEnabled != 0u)
    {
        float rounding = clamp(pc.SmaaCornerRounding / 100.0, 0.0, 1.0);
        horizontal *= mix(1.0, 0.75, rounding * vertical);
        vertical *= mix(1.0, 0.75, rounding * horizontal);
    }

    outWeights = vec4(horizontal, horizontal, vertical, vertical) * lutScale;
}
