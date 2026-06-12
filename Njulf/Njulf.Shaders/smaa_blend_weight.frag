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
    uint SmaaSampleCount;
    uint SmaaMode;
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

void main()
{
    vec2 edges = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaEdgesTextureIndex))], inUv).rg;
    vec4 lutScale = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaAreaTextureIndex))], vec2(0.5)).rgba *
        texture(BindlessTextures[nonuniformEXT(int(pc.SmaaSearchTextureIndex))], vec2(0.5)).rgba;

    float left = edges.r > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(-1.0, 0.0), 0) : 0.0;
    float right = edges.r > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(1.0, 0.0), 0) : 0.0;
    float up = edges.g > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(0.0, -1.0), 1) : 0.0;
    float down = edges.g > 0.0 ? SearchDistance(pc.SmaaEdgesTextureIndex, inUv, vec2(0.0, 1.0), 1) : 0.0;

    float quality = clamp(log2(float(max(pc.SmaaSampleCount, 1u))) / 4.0, 0.0, 1.0);
    float minimumWeight = mix(0.30, 0.62, quality);
    float horizontal = edges.r * clamp(mix(minimumWeight, 1.0, (left + right) / max(float(pc.SmaaMaxSearchSteps), 1.0)), 0.0, 1.0);
    float vertical = edges.g * clamp(mix(minimumWeight, 1.0, (up + down) / max(float(pc.SmaaMaxSearchSteps), 1.0)), 0.0, 1.0);
    float rounding = clamp(pc.SmaaCornerRounding / 100.0, 0.0, 1.0);
    horizontal *= mix(1.0, 0.75, rounding * vertical);
    vertical *= mix(1.0, 0.75, rounding * horizontal);
    outWeights = vec4(horizontal, horizontal, vertical, vertical) * lutScale;
}
