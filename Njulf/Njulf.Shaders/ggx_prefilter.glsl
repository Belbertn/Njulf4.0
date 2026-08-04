#ifndef NJULF_GGX_PREFILTER_GLSL
#define NJULF_GGX_PREFILTER_GLSL

// Shared, deterministic Hammersley/GGX helpers. Environment and authored local
// reflection filtering intentionally use the same sequence so roughness changes
// do not introduce a second visual kernel.
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

#endif
