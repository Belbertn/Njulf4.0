#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform WeightedOitCompositePushBlock
{
    uint AccumulationTextureIndex;
    uint RevealageTextureIndex;
    uint DebugView;
    uint Padding0;
} pc;

const uint TRANSPARENCY_DEBUG_WEIGHTED_OIT_ACCUMULATION = 6u;
const uint TRANSPARENCY_DEBUG_WEIGHTED_OIT_REVEALAGE = 7u;

void main()
{
    vec4 accumulation = texture(BindlessTextures[nonuniformEXT(int(pc.AccumulationTextureIndex))], inUv);
    float alphaCoverage = clamp(texture(BindlessTextures[nonuniformEXT(int(pc.RevealageTextureIndex))], inUv).r, 0.0, 1.0);

    if (pc.DebugView == TRANSPARENCY_DEBUG_WEIGHTED_OIT_ACCUMULATION)
    {
        vec3 debugColor = accumulation.a <= 0.00001
            ? vec3(0.0)
            : accumulation.rgb / max(accumulation.a, 0.00001);
        outColor = vec4(clamp(debugColor, vec3(0.0), vec3(1.0)), 1.0);
        return;
    }

    if (pc.DebugView == TRANSPARENCY_DEBUG_WEIGHTED_OIT_REVEALAGE)
    {
        outColor = vec4(vec3(alphaCoverage), 1.0);
        return;
    }

    if (accumulation.a <= 0.00001 || alphaCoverage <= 0.00001)
    {
        discard;
    }

    vec3 weightedColor = accumulation.rgb / max(accumulation.a, 0.00001);
    float alpha = alphaCoverage;
    outColor = vec4(max(weightedColor, vec3(0.0)), alpha);
}
