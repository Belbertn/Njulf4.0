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
    uint DebugViewMode;
    uint Padding0;
} pc;

const uint TRANSPARENCY_DEBUG_WEIGHTED_OIT_ACCUMULATION = 6u;
const uint TRANSPARENCY_DEBUG_WEIGHTED_OIT_REVEALAGE = 7u;

void main()
{
    vec4 accumulation = texture(BindlessTextures[nonuniformEXT(int(pc.AccumulationTextureIndex))], inUv);
    float revealage = clamp(texture(BindlessTextures[nonuniformEXT(int(pc.RevealageTextureIndex))], inUv).r, 0.0, 1.0);

    if (pc.DebugViewMode == TRANSPARENCY_DEBUG_WEIGHTED_OIT_ACCUMULATION)
    {
        outColor = vec4(clamp(accumulation.rgb / max(accumulation.a, 0.0001), vec3(0.0), vec3(1.0)), 1.0);
        return;
    }

    if (pc.DebugViewMode == TRANSPARENCY_DEBUG_WEIGHTED_OIT_REVEALAGE)
    {
        outColor = vec4(vec3(revealage), 1.0);
        return;
    }

    float coverage = clamp(1.0 - revealage, 0.0, 1.0);
    if (coverage <= 0.0001 || accumulation.a <= 0.0001)
    {
        outColor = vec4(0.0);
        return;
    }

    vec3 transparentColor = accumulation.rgb / max(accumulation.a, 0.0001);
    outColor = vec4(max(transparentColor, vec3(0.0)), coverage);
}
