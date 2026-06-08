#version 460
#extension GL_GOOGLE_include_directive : require

#include "common.glsl"

layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

void main()
{
    vec3 normal = normalize(fragNormal);
    float ndotl = max(dot(normal, normalize(vec3(0.35, 0.6, 0.7))), 0.0);
    vec3 baseColor = vec3(0.85, 0.88, 0.82);
    outColor = vec4(baseColor * (0.15 + ndotl * 0.85), 1.0);
}
