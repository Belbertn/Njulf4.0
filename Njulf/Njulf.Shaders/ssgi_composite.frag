#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 inUv;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform SsgiCompositePushBlock
{
    uint GiFinalDiffuseTextureIndex;
    uint SceneMaterialTextureIndex;
    uint DebugView;
    uint Padding0;
} pc;

const uint GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT = 1u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED = 3u;

vec3 ComposeScreenSpaceContactGi(vec4 gi, vec4 material)
{
    vec3 receiverAlbedo = clamp(material.rgb, vec3(0.0), vec3(16.0));
    float diffuseWeight = 1.0 - clamp(material.a, 0.0, 1.0);
    vec3 ssgiDiffuse = clamp(gi.rgb, vec3(0.0), vec3(64.0));
    return ssgiDiffuse * receiverAlbedo * diffuseWeight;
}

void main()
{
    vec4 gi = texture(BindlessTextures[nonuniformEXT(int(pc.GiFinalDiffuseTextureIndex))], inUv);
    vec4 material = texture(BindlessTextures[nonuniformEXT(int(pc.SceneMaterialTextureIndex))], inUv);
    float support = clamp(gi.a, 0.0, 1.0);
    vec3 indirect = ComposeScreenSpaceContactGi(gi, material);

    if (support <= 0.0001 && pc.DebugView != GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED)
        discard;

    if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED)
        outColor = vec4(vec3(support), 1.0);
    else if (pc.DebugView == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)
        outColor = vec4(indirect, 1.0);
    else
        outColor = vec4(indirect, 0.0);
}
