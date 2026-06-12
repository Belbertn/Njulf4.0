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
    uint SmaaSampleCount;
    uint SmaaMode;
    float TaaFeedbackMin;
    float TaaFeedbackMax;
    float TaaVelocityRejectionScale;
    uint TaaHistoryValid;
} pc;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

float ColorDelta(vec3 a, vec3 b)
{
    vec3 delta = abs(a - b);
    return max(Luma(delta), max(delta.r, max(delta.g, delta.b)) * 0.5);
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 center = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv).rgb;
    vec3 left = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(px.x, 0.0)).rgb;
    vec3 right = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(px.x, 0.0)).rgb;
    vec3 top = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(0.0, px.y)).rgb;
    vec3 bottom = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(0.0, px.y)).rgb;
    float quality = clamp(log2(float(max(pc.SmaaSampleCount, 1u))) / 4.0, 0.0, 1.0);
    float threshold = max(pc.SmaaThreshold * mix(0.95, 0.45, quality), 0.018);
    float horizontal = max(ColorDelta(center, left), ColorDelta(center, right));
    float vertical = max(ColorDelta(center, top), ColorDelta(center, bottom));
    outEdges = vec2(smoothstep(threshold, threshold * 1.55, horizontal), smoothstep(threshold, threshold * 1.55, vertical));
}
