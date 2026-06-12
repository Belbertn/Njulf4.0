#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec2 outEdges;

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
    uint Padding0;
    uint Padding1;
} pc;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    float center = Luma(texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv).rgb);
    float left = Luma(texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(px.x, 0.0)).rgb);
    float top = Luma(texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(0.0, px.y)).rgb);
    float horizontal = abs(center - left);
    float vertical = abs(center - top);
    outEdges = vec2(horizontal >= pc.SmaaThreshold ? 1.0 : 0.0, vertical >= pc.SmaaThreshold ? 1.0 : 0.0);
}
