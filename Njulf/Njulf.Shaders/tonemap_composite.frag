#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform CompositePushBlock
{
    uint SceneColorTextureIndex;
    uint BloomTextureIndex;
    uint BloomDebugTextureIndex;
    uint BloomEnabled;
    float Exposure;
    float BloomIntensity;
    uint ToneMapper;
    uint DebugViewMode;
    uint OutputToSrgb;
    uint Padding0;
    uint Padding1;
    uint Padding2;
} pc;

const uint TONE_MAPPER_NONE = 0u;
const uint TONE_MAPPER_REINHARD = 1u;
const uint TONE_MAPPER_ACES_FITTED = 2u;
const uint DEBUG_VIEW_RAW_HDR = 1u;
const uint DEBUG_VIEW_BLOOM_EXTRACT_MASK = 2u;
const uint DEBUG_VIEW_BLOOM_DOWNSAMPLE_MIP = 3u;
const uint DEBUG_VIEW_BLOOM_UPSAMPLE_RESULT = 4u;
const uint DEBUG_VIEW_BLOOM_ONLY = 5u;

vec3 AcesFitted(vec3 color)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((color * (a * color + b)) / (color * (c * color + d) + e), 0.0, 1.0);
}

vec3 LinearToSrgb(vec3 color)
{
    bvec3 cutoff = lessThanEqual(color, vec3(0.0031308));
    vec3 lower = color * 12.92;
    vec3 higher = 1.055 * pow(max(color, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055;
    return mix(higher, lower, cutoff);
}

void main()
{
    vec3 hdr = max(texture(BindlessTextures[nonuniformEXT(int(pc.SceneColorTextureIndex))], inUv).rgb, vec3(0.0));

    if (pc.DebugViewMode == DEBUG_VIEW_BLOOM_EXTRACT_MASK ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_DOWNSAMPLE_MIP ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_UPSAMPLE_RESULT ||
        pc.DebugViewMode == DEBUG_VIEW_BLOOM_ONLY)
    {
        vec3 debugBloom = max(texture(BindlessTextures[nonuniformEXT(int(pc.BloomDebugTextureIndex))], inUv).rgb, vec3(0.0));
        vec3 debugColor = clamp(debugBloom * max(pc.BloomIntensity, 0.0), 0.0, 1.0);
        if (pc.OutputToSrgb != 0u)
            debugColor = LinearToSrgb(debugColor);
        outColor = vec4(debugColor, 1.0);
        return;
    }

    if (pc.BloomEnabled != 0u && pc.DebugViewMode != DEBUG_VIEW_RAW_HDR)
    {
        vec3 bloom = max(texture(BindlessTextures[nonuniformEXT(int(pc.BloomTextureIndex))], inUv).rgb, vec3(0.0));
        hdr += bloom * max(pc.BloomIntensity, 0.0);
    }

    vec3 color = hdr * max(pc.Exposure, 0.0);

    if (pc.DebugViewMode == DEBUG_VIEW_RAW_HDR || pc.ToneMapper == TONE_MAPPER_NONE)
    {
        color = clamp(color, 0.0, 1.0);
    }
    else if (pc.ToneMapper == TONE_MAPPER_REINHARD)
    {
        color = color / (vec3(1.0) + color);
    }
    else
    {
        color = AcesFitted(color);
    }

    if (pc.OutputToSrgb != 0u)
        color = LinearToSrgb(color);

    outColor = vec4(color, 1.0);
}
