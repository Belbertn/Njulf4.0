#ifndef ENVIRONMENT_PREFILTER_IMAGE_FORMAT
#error ENVIRONMENT_PREFILTER_IMAGE_FORMAT must be defined before including this file.
#endif

#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 2, binding = 0, ENVIRONMENT_PREFILTER_IMAGE_FORMAT)
    uniform writeonly image2DArray PrefilterOutput;

layout(push_constant) uniform EnvironmentPrefilterPushBlock
{
    uint OutputSize;
    uint MipLevel;
    uint MipCount;
    uint SampleCount;
    float Roughness;
    uint Padding0;
    uint Padding1;
    uint Padding2;
} pc;

vec3 EnvironmentPrefilterCubeDirection(uint face, vec2 uv)
{
    float a = 2.0 * uv.x - 1.0;
    float b = 1.0 - 2.0 * uv.y;
    if (face == 0u)
        return normalize(vec3(1.0, b, -a));
    if (face == 1u)
        return normalize(vec3(-1.0, b, a));
    if (face == 2u)
        return normalize(vec3(a, 1.0, -b));
    if (face == 3u)
        return normalize(vec3(a, -1.0, b));
    if (face == 4u)
        return normalize(vec3(a, b, 1.0));
    return normalize(vec3(-a, b, -1.0));
}

float EnvironmentPrefilterRadicalInverse(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xaaaaaaaau) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xccccccccu) >> 2u);
    bits = ((bits & 0x0f0f0f0fu) << 4u) | ((bits & 0xf0f0f0f0u) >> 4u);
    bits = ((bits & 0x00ff00ffu) << 8u) | ((bits & 0xff00ff00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec3 EnvironmentPrefilterImportanceSampleGgx(
    vec2 samplePoint,
    float roughness)
{
    float alpha = roughness * roughness;
    float phi = 6.28318530718 * samplePoint.x;
    float cosTheta = sqrt((1.0 - samplePoint.y) /
        max(1.0 + (alpha * alpha - 1.0) * samplePoint.y, 0.000001));
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    return vec3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
}

void EnvironmentPrefilterBasis(
    vec3 normal,
    out vec3 tangent,
    out vec3 bitangent)
{
    vec3 up = abs(normal.y) < 0.999
        ? vec3(0.0, 1.0, 0.0)
        : vec3(1.0, 0.0, 0.0);
    tangent = normalize(cross(up, normal));
    bitangent = cross(normal, tangent);
}

void main()
{
    uvec3 invocation = gl_GlobalInvocationID;
    if (invocation.x >= pc.OutputSize ||
        invocation.y >= pc.OutputSize ||
        invocation.z >= 6u)
    {
        return;
    }

    vec2 uv = (vec2(invocation.xy) + vec2(0.5)) /
        max(float(pc.OutputSize), 1.0);
    vec3 reflection = EnvironmentPrefilterCubeDirection(invocation.z, uv);
    GPUEnvironmentData environment = ReadEnvironmentDataFrom(
        uint(ENVIRONMENT_PREFILTER_DATA_BUFFER_INDEX));
    vec3 result;
    if (pc.Roughness <= 0.0001 || pc.SampleCount <= 1u)
    {
        result = EvaluateEnvironmentRadiance(
            environment,
            reflection,
            false,
            true,
            true);
    }
    else
    {
        vec3 tangent;
        vec3 bitangent;
        EnvironmentPrefilterBasis(reflection, tangent, bitangent);
        vec3 weightedRadiance = vec3(0.0);
        float totalWeight = 0.0;
        for (uint sampleIndex = 0u; sampleIndex < pc.SampleCount; sampleIndex++)
        {
            vec2 samplePoint = vec2(
                (float(sampleIndex) + 0.5) / float(pc.SampleCount),
                EnvironmentPrefilterRadicalInverse(sampleIndex));
            vec3 localHalf = EnvironmentPrefilterImportanceSampleGgx(
                samplePoint,
                pc.Roughness);
            vec3 halfVector = normalize(
                tangent * localHalf.x +
                bitangent * localHalf.y +
                reflection * localHalf.z);
            vec3 lightDirection = normalize(
                2.0 * dot(reflection, halfVector) * halfVector - reflection);
            float nDotL = max(dot(reflection, lightDirection), 0.0);
            if (nDotL <= 0.0)
                continue;
            weightedRadiance += EvaluateEnvironmentRadiance(
                environment,
                lightDirection,
                false,
                true,
                true) * nDotL;
            totalWeight += nDotL;
        }
        result = totalWeight > 0.0
            ? weightedRadiance / totalWeight
            : EvaluateEnvironmentRadiance(
                environment,
                reflection,
                false,
                true,
                true);
    }

    imageStore(
        PrefilterOutput,
        ivec3(invocation),
        vec4(max(result, vec3(0.0)), 1.0));
}
