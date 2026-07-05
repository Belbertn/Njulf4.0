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
    float HitErrorMeters;
    uint StepCount;
    bool StepExhausted;
};

const float GLOBAL_SDF_TRACE_MIN_STEP_VOXELS = 0.25;
const float GLOBAL_SDF_TRACE_RELAXATION = 0.9;
const float GLOBAL_SDF_TRACE_SURFACE_ISO_VOXELS = 0.25;
const uint GLOBAL_SDF_TRACE_REFINE_ITERATIONS = 2u;

float DecodeGlobalSdfDistance(float normalizedDistance, float voxelSize)
{
    return DecodeSdfDistance(normalizedDistance, voxelSize);
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

bool GlobalSdfCascadeContains(vec3 worldPosition, GPUGlobalSdfCascade cascade)
{
    vec3 logicalVoxelFloat = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    return all(greaterThanEqual(logicalVoxelFloat, vec3(0.0))) &&
        all(lessThan(logicalVoxelFloat, vec3(float(cascade.Resolution))));
}

GlobalSdfSample SampleGlobalSdfCascade(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    vec3 logicalVoxelFloat = (worldPosition - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    if (any(lessThan(logicalVoxelFloat, vec3(0.0))) || any(greaterThanEqual(logicalVoxelFloat, vec3(float(cascade.Resolution)))))
        return GlobalSdfSample(1.0e20, cascadeIndex, false);

    float res = float(max(cascade.Resolution, 1u));
    vec3 clamped = clamp(logicalVoxelFloat, vec3(0.5), vec3(res - 0.5));
    // logical voxel centers sit at i+0.5; Linear+Repeat maps (voxel + ring*8) / res through the toroidal brick scroll.
    vec3 uvw = (clamped + vec3(cascade.RingOffsetX, cascade.RingOffsetY, cascade.RingOffsetZ) * 8.0) / res;
    float encodedDistance = textureLod(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], uvw, 0.0).r;
    return GlobalSdfSample(DecodeGlobalSdfDistance(encodedDistance, cascade.WorldMinAndVoxelSize.w), cascadeIndex, true);
}

vec3 EstimateGlobalSdfNormal(vec3 worldPosition, GPUGlobalSdfCascade cascade, uint cascadeIndex)
{
    float eps = max(cascade.WorldMinAndVoxelSize.w * 0.5, 0.0001);
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
    uint steps = 0u;
    float voxelSize = max(cascade.WorldMinAndVoxelSize.w, 0.001);
    float surfaceIso = voxelSize * GLOBAL_SDF_TRACE_SURFACE_ISO_VOXELS;
    float minStep = voxelSize * GLOBAL_SDF_TRACE_MIN_STEP_VOXELS;
    float exitT = min(maxDistance, max(GlobalSdfRayAabbExit(origin, direction, cascade.WorldMinAndVoxelSize.xyz, cascade.WorldMinAndVoxelSize.xyz + cascade.WorldExtentAndInvVoxelSize.xyz), t));
    vec3 p = origin + direction * t;
    if (!GlobalSdfCascadeContains(p, cascade) || t > exitT)
        return GlobalSdfTraceResult(false, min(t, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, false);

    float dPrev = SampleGlobalSdfCascade(p, cascade, cascadeIndex).DistanceMeters - surfaceIso;
    bool armed = dPrev > 0.0;
    for (; steps < maxSteps && t <= maxDistance; steps++)
    {
        float tNext = t + max(dPrev * GLOBAL_SDF_TRACE_RELAXATION, minStep);
        if (tNext > exitT || tNext > maxDistance)
            return GlobalSdfTraceResult(false, min(tNext, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps + 1u, false);

        vec3 pNext = origin + direction * tNext;
        if (!GlobalSdfCascadeContains(pNext, cascade))
            return GlobalSdfTraceResult(false, min(tNext, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps + 1u, false);

        float dNext = SampleGlobalSdfCascade(pNext, cascade, cascadeIndex).DistanceMeters - surfaceIso;
        if (!armed)
        {
            armed = dNext > 0.0;
            t = tNext;
            dPrev = dNext;
            continue;
        }

        if (dNext <= 0.0)
        {
            float tA = t;
            float dA = dPrev;
            float tB = tNext;
            float dB = dNext;
            for (uint refineIndex = 0u; refineIndex < GLOBAL_SDF_TRACE_REFINE_ITERATIONS; refineIndex++)
            {
                float tMid = tA + dA * (tB - tA) / max(dA - dB, 1.0e-6);
                float dMid = SampleGlobalSdfCascade(origin + direction * tMid, cascade, cascadeIndex).DistanceMeters - surfaceIso;
                if (dMid > 0.0)
                {
                    tA = tMid;
                    dA = dMid;
                }
                else
                {
                    tB = tMid;
                    dB = dMid;
                }
            }

            float tHit = tA + dA * (tB - tA) / max(dA - dB, 1.0e-6);
            vec3 hitPosition = origin + direction * tHit;
            float hitError = max(min(dA, -dB) + surfaceIso, voxelSize * 0.05);
            return GlobalSdfTraceResult(true, tHit, cascadeIndex, EstimateGlobalSdfNormal(hitPosition, cascade, cascadeIndex), hitError, steps + 1u, false);
        }

        t = tNext;
        dPrev = dNext;
    }

    return GlobalSdfTraceResult(false, maxDistance, cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, steps >= maxSteps && t <= maxDistance);
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
