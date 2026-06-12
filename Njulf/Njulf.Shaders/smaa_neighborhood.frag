#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

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

vec3 EncodeOutput(vec3 color)
{
    color = clamp(color, vec3(0.0), vec3(1.0));
    if (pc.OutputToSrgb != 0u)
        color = pow(color, vec3(1.0 / 2.2));
    return color;
}

void main()
{
    vec2 px = pc.InvSourceDimensions;
    vec3 center = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv).rgb;
    vec4 weights = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaBlendWeightsTextureIndex))], inUv);

    if (pc.DebugView == 1u)
    {
        outColor = vec4(EncodeOutput(center), 1.0);
        return;
    }

    vec3 horizontal = mix(
        texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(px.x, 0.0)).rgb,
        texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(px.x, 0.0)).rgb,
        0.5);
    vec3 vertical = mix(
        texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv - vec2(0.0, px.y)).rgb,
        texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(0.0, px.y)).rgb,
        0.5);

    float horizontalWeight = clamp(max(weights.r, weights.g), 0.0, 1.0);
    float verticalWeight = clamp(max(weights.b, weights.a), 0.0, 1.0);
    vec3 result = mix(center, horizontal, horizontalWeight * 0.5);
    result = mix(result, vertical, verticalWeight * 0.5);
    outColor = vec4(EncodeOutput(result), 1.0);
}
