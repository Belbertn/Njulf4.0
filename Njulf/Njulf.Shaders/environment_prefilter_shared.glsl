#ifndef ENVIRONMENT_PREFILTER_IMAGE_FORMAT
#error ENVIRONMENT_PREFILTER_IMAGE_FORMAT must be defined before including this file.
#endif

#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"
#include "ggx_prefilter.glsl"

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
