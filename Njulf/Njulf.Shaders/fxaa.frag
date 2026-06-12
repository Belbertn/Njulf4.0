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
    uint SmaaSampleCount;
    uint SmaaMode;
} pc;

float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

vec3 EncodeOutput(vec3 color)
{
    color = clamp(color, vec3(0.0), vec3(1.0));
    if (pc.OutputToSrgb != 0u)
    {
        bvec3 cutoff = lessThanEqual(color, vec3(0.0031308));
        vec3 lower = color * 12.92;
        vec3 higher = 1.055 * pow(color, vec3(1.0 / 2.4)) - 0.055;
        color = mix(higher, lower, cutoff);
    }
    return color;
}

void main()
{
    vec3 center = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv).rgb;
    float centerLuma = Luma(center);

    if (pc.DebugView == 1u)
    {
        outColor = vec4(EncodeOutput(center), 1.0);
        return;
    }

    if (pc.DebugView == 3u)
    {
        vec2 edges = texture(BindlessTextures[nonuniformEXT(int(pc.SmaaEdgesTextureIndex))], inUv).rg;
        outColor = vec4(edges.r, edges.g, max(edges.r, edges.g), 1.0);
        return;
    }

    if (pc.DebugView == 4u)
    {
        outColor = vec4(texture(BindlessTextures[nonuniformEXT(int(pc.SmaaBlendWeightsTextureIndex))], inUv).rgba);
        return;
    }

    vec2 px = pc.InvSourceDimensions;
    vec3 north = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(0.0, -px.y)).rgb;
    vec3 south = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(0.0, px.y)).rgb;
    vec3 east = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(px.x, 0.0)).rgb;
    vec3 west = texture(BindlessTextures[nonuniformEXT(int(pc.InputTextureIndex))], inUv + vec2(-px.x, 0.0)).rgb;

    float lumaMin = min(centerLuma, min(min(Luma(north), Luma(south)), min(Luma(east), Luma(west))));
    float lumaMax = max(centerLuma, max(max(Luma(north), Luma(south)), max(Luma(east), Luma(west))));
    float contrast = lumaMax - lumaMin;
    float threshold = max(pc.FxaaContrastThreshold, lumaMax * pc.FxaaRelativeThreshold);

    if (pc.DebugView == 2u)
    {
        float edge = contrast >= threshold ? 1.0 : 0.0;
        outColor = vec4(vec3(edge * contrast / max(threshold, 0.0001)), 1.0);
        return;
    }

    if (contrast < threshold)
    {
        outColor = vec4(EncodeOutput(center), 1.0);
        return;
    }

    float horizontal = abs(Luma(west) - centerLuma) + abs(Luma(east) - centerLuma);
    float vertical = abs(Luma(north) - centerLuma) + abs(Luma(south) - centerLuma);
    vec3 blended = horizontal >= vertical
        ? (west + east + center * 2.0) * 0.25
        : (north + south + center * 2.0) * 0.25;

    float subpixel = clamp(pc.FxaaSubpixelBlending, 0.0, 1.0);
    vec3 result = mix(center, blended, subpixel);
    outColor = vec4(EncodeOutput(result), 1.0);
}
