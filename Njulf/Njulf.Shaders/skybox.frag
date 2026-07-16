#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(location = 0) in vec2 fragUv;
layout(location = 0) out vec4 outColor;

struct GPUSkyboxPushConstants
{
    mat4 InverseViewMatrix;
    mat4 InverseProjectionMatrix;
    uint EnvironmentTextureIndex;
    float SkyIntensity;
    float RotationRadians;
    uint DebugView;
};

layout(push_constant) uniform SkyboxPushConstantBlock
{
    GPUSkyboxPushConstants Push;
} pc;

vec3 RotateEnvironmentDirection(vec3 direction, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return normalize(vec3(
        direction.x * c - direction.z * s,
        direction.y,
        direction.x * s + direction.z * c));
}

void main()
{
    vec2 ndc = fragUv * 2.0 - vec2(1.0);
    vec4 view = MulRowMajor(vec4(ndc, 0.0, 1.0), pc.Push.InverseProjectionMatrix);
    view.xyz /= max(abs(view.w), 0.00001);
    vec3 viewDirection = normalize(view.xyz);
    vec3 worldDirection = normalize(MulRowMajor(vec4(viewDirection, 0.0), pc.Push.InverseViewMatrix).xyz);
    worldDirection = RotateEnvironmentDirection(worldDirection, pc.Push.RotationRadians);

    vec3 sky = texture(BindlessCubeTextures[nonuniformEXT(int(pc.Push.EnvironmentTextureIndex))], worldDirection).rgb;
    outColor = vec4(sky * pc.Push.SkyIntensity, 1.0);
}
