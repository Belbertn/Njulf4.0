#ifndef NJULF_GLOBAL_SDF_GLSL
#define NJULF_GLOBAL_SDF_GLSL

struct GlobalSdfSample
{
    float DistanceMeters;
    uint CascadeIndex;
};

struct GlobalSdfTraceResult
{
    bool Hit;
    float T;
    uint CascadeIndex;
    vec3 Normal;
    uint StepCount;
};

float DecodeGlobalSdfDistance(float normalizedDistance, vec3 worldExtent)
{
    float maxExtent = max(worldExtent.x, max(worldExtent.y, worldExtent.z));
    return normalizedDistance * max(maxExtent, 0.0001);
}

GlobalSdfSample SampleGlobalSdfCascade(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    vec3 uvw = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) / max(cascade.WorldExtentAndInvVoxelSize.xyz, vec3(0.0001));
    if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
        return GlobalSdfSample(1.0e20, cascadeIndex);

    float encodedDistance = textureLod(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], uvw, 0.0).r;
    return GlobalSdfSample(DecodeGlobalSdfDistance(encodedDistance, cascade.WorldExtentAndInvVoxelSize.xyz), cascadeIndex);
}

vec3 EstimateGlobalSdfNormal(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    float eps = max(cascade.WorldMinAndVoxelSize.w, 0.0001);
    float dx = SampleGlobalSdfCascade(worldPosition + vec3(eps, 0.0, 0.0), cascade, cascadeIndex).DistanceMeters -
        SampleGlobalSdfCascade(worldPosition - vec3(eps, 0.0, 0.0), cascade, cascadeIndex).DistanceMeters;
    float dy = SampleGlobalSdfCascade(worldPosition + vec3(0.0, eps, 0.0), cascade, cascadeIndex).DistanceMeters -
        SampleGlobalSdfCascade(worldPosition - vec3(0.0, eps, 0.0), cascade, cascadeIndex).DistanceMeters;
    float dz = SampleGlobalSdfCascade(worldPosition + vec3(0.0, 0.0, eps), cascade, cascadeIndex).DistanceMeters -
        SampleGlobalSdfCascade(worldPosition - vec3(0.0, 0.0, eps), cascade, cascadeIndex).DistanceMeters;
    vec3 n = vec3(dx, dy, dz);
    return dot(n, n) > 1.0e-10 ? normalize(n) : vec3(0.0, 1.0, 0.0);
}

GlobalSdfTraceResult TraceGlobalSdfCascade(
    vec3 origin,
    vec3 direction,
    float maxDistance,
    GPUGlobalSdfCascade cascade,
    uint cascadeIndex,
    uint maxSteps)
{
    float t = 0.0;
    uint steps = 0u;
    float hitEpsilon = max(cascade.WorldMinAndVoxelSize.w * 0.75, 0.001);
    for (; steps < maxSteps && t <= maxDistance; steps++)
    {
        vec3 p = origin + direction * t;
        GlobalSdfSample sdfSample = SampleGlobalSdfCascade(p, cascade, cascadeIndex);
        if (sdfSample.DistanceMeters <= hitEpsilon)
            return GlobalSdfTraceResult(true, t, cascadeIndex, EstimateGlobalSdfNormal(p, cascade, cascadeIndex), steps + 1u);

        t += max(sdfSample.DistanceMeters, hitEpsilon);
    }

    return GlobalSdfTraceResult(false, maxDistance, cascadeIndex, vec3(0.0, 1.0, 0.0), steps);
}

#endif
