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
#define GLOBAL_SDF_TRACE_ANALYTIC 1
const float GLOBAL_SDF_TRACE_ANALYTIC_BAND_VOXELS = 1.5;
const uint GLOBAL_SDF_TRACE_ANALYTIC_CASCADE_INDEX = 2u;
const uint GLOBAL_SDF_TRACE_ANALYTIC_MAX_CELLS = 8u;
const uint GLOBAL_SDF_TRACE_ANALYTIC_NEWTON_ITERATIONS = 2u;

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

float GlobalSdfFetchLogicalVoxelDistanceMeters(ivec3 logicalVoxel, GPUGlobalSdfCascade cascade)
{
    int res = int(max(cascade.Resolution, 1u));
    ivec3 clampedLogicalVoxel = clamp(logicalVoxel, ivec3(0), ivec3(res - 1));
    ivec3 physicalTexel = GlobalSdfLogicalVoxelToPhysicalTexel(clampedLogicalVoxel, cascade);
    float encodedDistance = texelFetch(BindlessVolumeTextures[nonuniformEXT(cascade.TextureIndex)], physicalTexel, 0).r;
    return DecodeGlobalSdfDistance(encodedDistance, cascade.WorldMinAndVoxelSize.w);
}

float EvaluateGlobalSdfTrilinearCell(
    ivec3 logicalCell,
    vec3 logicalVoxelFloat,
    GPUGlobalSdfCascade cascade)
{
    int res = int(max(cascade.Resolution, 1u));
    ivec3 cell = clamp(logicalCell, ivec3(0), ivec3(max(res - 2, 0)));
    vec3 f = clamp(logicalVoxelFloat - (vec3(cell) + vec3(0.5)), vec3(0.0), vec3(1.0));
    float c000 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 0, 0), cascade);
    float c100 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 0, 0), cascade);
    float c010 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 1, 0), cascade);
    float c110 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 1, 0), cascade);
    float c001 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 0, 1), cascade);
    float c101 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 0, 1), cascade);
    float c011 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 1, 1), cascade);
    float c111 = GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 1, 1), cascade);
    float c00 = mix(c000, c100, f.x);
    float c10 = mix(c010, c110, f.x);
    float c01 = mix(c001, c101, f.x);
    float c11 = mix(c011, c111, f.x);
    float c0 = mix(c00, c10, f.y);
    float c1 = mix(c01, c11, f.y);
    return mix(c0, c1, f.z);
}

float EvaluateGlobalSdfTrilinearCellAtT(
    vec3 origin,
    vec3 direction,
    float t,
    ivec3 logicalCell,
    GPUGlobalSdfCascade cascade)
{
    vec3 logicalVoxelFloat = (origin + direction * t - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
    return EvaluateGlobalSdfTrilinearCell(logicalCell, logicalVoxelFloat, cascade);
}

float GlobalSdfRayAabbExit(vec3 origin, vec3 direction, vec3 boundsMin, vec3 boundsMax);

bool TryTraceGlobalSdfAnalyticCells(
    vec3 origin,
    vec3 direction,
    float startDistance,
    float maxDistance,
    GPUGlobalSdfCascade cascade,
    float surfaceIso,
    out float hitT,
    out float hitErrorMeters,
    out uint cellSteps)
{
    hitT = maxDistance;
    hitErrorMeters = 0.0;
    cellSteps = 0u;

#if GLOBAL_SDF_TRACE_ANALYTIC
    float voxelSize = max(cascade.WorldMinAndVoxelSize.w, 0.001);
    float t = max(startDistance, 0.0);
    float cellAdvanceEpsilon = voxelSize * 0.001;
    int res = int(max(cascade.Resolution, 1u));
    vec3 cascadeMin = cascade.WorldMinAndVoxelSize.xyz;

    for (; cellSteps < GLOBAL_SDF_TRACE_ANALYTIC_MAX_CELLS && t < maxDistance; cellSteps++)
    {
        vec3 p = origin + direction * t;
        vec3 logicalVoxelFloat = (p - cascadeMin) * cascade.WorldExtentAndInvVoxelSize.w;
        if (any(lessThan(logicalVoxelFloat, vec3(0.0))) || any(greaterThanEqual(logicalVoxelFloat, vec3(float(res)))))
            return false;

        ivec3 logicalCell = clamp(ivec3(floor(logicalVoxelFloat - vec3(0.5))), ivec3(0), ivec3(max(res - 2, 0)));
        vec3 cellMin = cascadeMin + (vec3(logicalCell) + vec3(0.5)) * voxelSize;
        vec3 cellMax = cellMin + vec3(voxelSize);
        float cellExitT = min(GlobalSdfRayAabbExit(origin, direction, cellMin, cellMax), maxDistance);
        float tA = t;
        float tB = max(tA, cellExitT);
        float dA = EvaluateGlobalSdfTrilinearCellAtT(origin, direction, tA, logicalCell, cascade) - surfaceIso;
        float dB = EvaluateGlobalSdfTrilinearCellAtT(origin, direction, tB, logicalCell, cascade) - surfaceIso;

        if (dA > 0.0 && dB <= 0.0)
        {
            float tRoot = clamp(tA + dA * (tB - tA) / max(dA - dB, 1.0e-6), tA, tB);
            float bracketA = tA;
            float bracketB = tB;
            float fA = dA;
            for (uint refineIndex = 0u; refineIndex < GLOBAL_SDF_TRACE_ANALYTIC_NEWTON_ITERATIONS; refineIndex++)
            {
                float fRoot = EvaluateGlobalSdfTrilinearCellAtT(origin, direction, tRoot, logicalCell, cascade) - surfaceIso;
                if (fRoot > 0.0)
                {
                    bracketA = tRoot;
                    fA = fRoot;
                }
                else
                {
                    bracketB = tRoot;
                }

                float h = max(min((bracketB - bracketA) * 0.25, voxelSize * 0.25), voxelSize * 0.001);
                float tMinus = max(bracketA, tRoot - h);
                float tPlus = min(bracketB, tRoot + h);
                float derivative = (EvaluateGlobalSdfTrilinearCellAtT(origin, direction, tPlus, logicalCell, cascade) -
                    EvaluateGlobalSdfTrilinearCellAtT(origin, direction, tMinus, logicalCell, cascade)) / max(tPlus - tMinus, 1.0e-6);
                float newtonT = abs(derivative) > 1.0e-5
                    ? tRoot - fRoot / derivative
                    : bracketA + fA * (bracketB - bracketA) / max(fA - min(fRoot, 0.0), 1.0e-6);
                tRoot = clamp(newtonT, bracketA, bracketB);
            }

            hitT = tRoot;
            hitErrorMeters = max(abs(EvaluateGlobalSdfTrilinearCellAtT(origin, direction, hitT, logicalCell, cascade) - surfaceIso), voxelSize * 0.025);
            return true;
        }

        if (cellExitT <= t + cellAdvanceEpsilon)
            t += cellAdvanceEpsilon;
        else
            t = cellExitT + cellAdvanceEpsilon;
    }
#endif

    return false;
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
        if (cascadeIndex == GLOBAL_SDF_TRACE_ANALYTIC_CASCADE_INDEX &&
            armed &&
            abs(dPrev) <= voxelSize * GLOBAL_SDF_TRACE_ANALYTIC_BAND_VOXELS)
        {
            float analyticHitT;
            float analyticHitError;
            uint analyticCellSteps;
            if (TryTraceGlobalSdfAnalyticCells(
                origin,
                direction,
                t,
                min(maxDistance, exitT),
                cascade,
                surfaceIso,
                analyticHitT,
                analyticHitError,
                analyticCellSteps))
            {
                steps += analyticCellSteps;
                vec3 hitPosition = origin + direction * analyticHitT;
                return GlobalSdfTraceResult(true, analyticHitT, cascadeIndex, EstimateGlobalSdfNormal(hitPosition, cascade, cascadeIndex), analyticHitError, steps + 1u, false);
            }
            steps += analyticCellSteps;
        }

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
