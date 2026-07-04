#ifndef NJULF_GLOBAL_SDF_GLSL
#define NJULF_GLOBAL_SDF_GLSL

struct GlobalSdfSample
{
    float DistanceMeters;
    uint CascadeIndex;
    bool Valid;
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

int GlobalSdfPositiveModulo(int value, int divisor)
{
    int result = value % divisor;
    return result < 0 ? result + divisor : result;
}

ivec3 GlobalSdfLogicalVoxelToPhysicalTexel(ivec3 logicalVoxel, GPUGlobalSdfCascade cascade)
{
    int bricksPerAxis = int(max(cascade.BricksPerAxis, 1u));
    ivec3 logicalBrick = logicalVoxel / 8;
    ivec3 voxelInBrick = logicalVoxel - logicalBrick * 8;
    ivec3 ringOffset = ivec3(cascade.RingOffsetX, cascade.RingOffsetY, cascade.RingOffsetZ);
    ivec3 physicalBrick = ivec3(
        GlobalSdfPositiveModulo(logicalBrick.x + ringOffset.x, bricksPerAxis),
        GlobalSdfPositiveModulo(logicalBrick.y + ringOffset.y, bricksPerAxis),
        GlobalSdfPositiveModulo(logicalBrick.z + ringOffset.z, bricksPerAxis));
    return physicalBrick * 8 + voxelInBrick;
}

float FetchGlobalSdfCascadeEncodedDistance(ivec3 logicalVoxel, GPUGlobalSdfCascade cascade)
{
    int resolution = int(max(cascade.Resolution, 1u));
    ivec3 clampedLogicalVoxel = clamp(logicalVoxel, ivec3(0), ivec3(resolution - 1));
    ivec3 physicalTexel = GlobalSdfLogicalVoxelToPhysicalTexel(clampedLogicalVoxel, cascade);
    physicalTexel = clamp(physicalTexel, ivec3(0), ivec3(resolution - 1));
    return texelFetch(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], physicalTexel, 0).r;
}

bool GlobalSdfCascadeContains(vec3 worldPosition, GPUGlobalSdfCascade cascade)
{
    vec3 logicalVoxelFloat = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    return all(greaterThanEqual(logicalVoxelFloat, vec3(0.0))) &&
        all(lessThan(logicalVoxelFloat, vec3(float(cascade.Resolution))));
}

GlobalSdfSample SampleGlobalSdfCascadeLod(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex, float lod)
{
    vec3 logicalVoxelFloat = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    if (any(lessThan(logicalVoxelFloat, vec3(0.0))) || any(greaterThanEqual(logicalVoxelFloat, vec3(float(cascade.Resolution)))))
        return GlobalSdfSample(1.0e20, cascadeIndex, false);

    vec3 centeredLogicalVoxel = logicalVoxelFloat - vec3(0.5);
    ivec3 logicalVoxel = ivec3(floor(centeredLogicalVoxel));
    vec3 voxelFraction = fract(centeredLogicalVoxel);
    float c000 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(0, 0, 0), cascade);
    float c100 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(1, 0, 0), cascade);
    float c010 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(0, 1, 0), cascade);
    float c110 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(1, 1, 0), cascade);
    float c001 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(0, 0, 1), cascade);
    float c101 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(1, 0, 1), cascade);
    float c011 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(0, 1, 1), cascade);
    float c111 = FetchGlobalSdfCascadeEncodedDistance(logicalVoxel + ivec3(1, 1, 1), cascade);
    float encodedDistance = mix(
        mix(mix(c000, c100, voxelFraction.x), mix(c010, c110, voxelFraction.x), voxelFraction.y),
        mix(mix(c001, c101, voxelFraction.x), mix(c011, c111, voxelFraction.x), voxelFraction.y),
        voxelFraction.z);
    return GlobalSdfSample(DecodeGlobalSdfDistance(encodedDistance, cascade.WorldExtentAndInvVoxelSize.xyz), cascadeIndex, true);
}

GlobalSdfSample SampleGlobalSdfCascade(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    return SampleGlobalSdfCascadeLod(worldPosition, cascade, cascadeIndex, 0.0);
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

float GlobalSdfRayAabbExit(vec3 origin, vec3 direction, vec3 boundsMin, vec3 boundsMax)
{
    vec3 safeDirection = vec3(
        abs(direction.x) > 1.0e-6 ? direction.x : (direction.x < 0.0 ? -1.0e-6 : 1.0e-6),
        abs(direction.y) > 1.0e-6 ? direction.y : (direction.y < 0.0 ? -1.0e-6 : 1.0e-6),
        abs(direction.z) > 1.0e-6 ? direction.z : (direction.z < 0.0 ? -1.0e-6 : 1.0e-6));
    vec3 invDirection = 1.0 / safeDirection;
    vec3 t0 = (boundsMin - origin) * invDirection;
    vec3 t1 = (boundsMax - origin) * invDirection;
    vec3 tFar = max(t0, t1);
    return min(tFar.x, min(tFar.y, tFar.z));
}

GlobalSdfTraceResult TraceGlobalSdfCascadeSegment(
    vec3 origin,
    vec3 direction,
    float startDistance,
    float maxDistance,
    GPUGlobalSdfCascade cascade,
    uint cascadeIndex,
    uint maxSteps)
{
    float t = max(startDistance, 0.0);
    float initialT = t;
    uint steps = 0u;
    float voxelSize = max(cascade.WorldMinAndVoxelSize.w, 0.001);
    float hitEpsilon = max(voxelSize * 0.75, 0.001);
    float initialSurfaceBandEnd = initialT + voxelSize;
    bool hitTestArmed = false;
    float exitT = min(maxDistance, max(GlobalSdfRayAabbExit(origin, direction, cascade.WorldMinAndVoxelSize.xyz, cascade.WorldMinAndVoxelSize.xyz + cascade.WorldExtentAndInvVoxelSize.xyz), t));
    for (; steps < maxSteps && t <= maxDistance; steps++)
    {
        vec3 p = origin + direction * t;
        if (!GlobalSdfCascadeContains(p, cascade) || t > exitT)
            return GlobalSdfTraceResult(false, min(t, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), steps);

        GlobalSdfSample fineSample = SampleGlobalSdfCascade(p, cascade, cascadeIndex);
        if (!hitTestArmed)
        {
            hitTestArmed = fineSample.DistanceMeters > hitEpsilon || t > initialSurfaceBandEnd;
            if (!hitTestArmed)
            {
                t += max(fineSample.DistanceMeters, hitEpsilon);
                continue;
            }
        }

        if (fineSample.DistanceMeters <= hitEpsilon)
            return GlobalSdfTraceResult(true, t, cascadeIndex, EstimateGlobalSdfNormal(p, cascade, cascadeIndex), steps + 1u);

        t += max(fineSample.DistanceMeters, hitEpsilon);
    }

    return GlobalSdfTraceResult(false, maxDistance, cascadeIndex, vec3(0.0, 1.0, 0.0), steps);
}

GlobalSdfTraceResult TraceGlobalSdfCascade(
    vec3 origin,
    vec3 direction,
    float maxDistance,
    GPUGlobalSdfCascade cascade,
    uint cascadeIndex,
    uint maxSteps)
{
    return TraceGlobalSdfCascadeSegment(origin, direction, 0.0, maxDistance, cascade, cascadeIndex, maxSteps);
}

#endif
