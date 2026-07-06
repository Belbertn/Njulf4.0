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

const float GLOBAL_SDF_TRACE_NEAR_BAND_VOXELS = 1.5;
const float GLOBAL_SDF_TRACE_MIN_STEP_VOXELS = 0.25;
const uint GLOBAL_SDF_DDA_MAX_CELLS = 32u;
const uint GLOBAL_SDF_CUBIC_NEWTON_ITERATIONS = 2u;

struct GlobalSdfCellCorners
{
    float C000;
    float C100;
    float C010;
    float C110;
    float C001;
    float C101;
    float C011;
    float C111;
};

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

GlobalSdfCellCorners FetchGlobalSdfCellCorners(ivec3 logicalCell, GPUGlobalSdfCascade cascade)
{
    int res = int(max(cascade.Resolution, 1u));
    ivec3 cell = clamp(logicalCell, ivec3(0), ivec3(max(res - 2, 0)));
    return GlobalSdfCellCorners(
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 0, 0), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 0, 0), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 1, 0), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 1, 0), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 0, 1), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 0, 1), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(0, 1, 1), cascade),
        GlobalSdfFetchLogicalVoxelDistanceMeters(cell + ivec3(1, 1, 1), cascade));
}

float EvaluateGlobalSdfTrilinear(GlobalSdfCellCorners c, vec3 localP)
{
    vec3 f = clamp(localP, vec3(0.0), vec3(1.0));
    float c00 = mix(c.C000, c.C100, f.x);
    float c10 = mix(c.C010, c.C110, f.x);
    float c01 = mix(c.C001, c.C101, f.x);
    float c11 = mix(c.C011, c.C111, f.x);
    float c0 = mix(c00, c10, f.y);
    float c1 = mix(c01, c11, f.y);
    return mix(c0, c1, f.z);
}

vec3 AnalyticTrilinearGradient(GlobalSdfCellCorners c, vec3 localP, float voxelSize)
{
    vec3 p = clamp(localP, vec3(0.0), vec3(1.0));
    float a = c.C100 - c.C000;
    float b = c.C010 - c.C000;
    float d = c.C001 - c.C000;
    float e = c.C110 - c.C100 - c.C010 + c.C000;
    float f = c.C101 - c.C100 - c.C001 + c.C000;
    float g = c.C011 - c.C010 - c.C001 + c.C000;
    float h = c.C111 - c.C110 - c.C101 - c.C011 + c.C100 + c.C010 + c.C001 - c.C000;
    vec3 gradient = vec3(
        a + e * p.y + f * p.z + h * p.y * p.z,
        b + e * p.x + g * p.z + h * p.x * p.z,
        d + f * p.x + g * p.y + h * p.x * p.y) / max(voxelSize, 0.0001);
    return dot(gradient, gradient) > 1.0e-10 ? normalize(gradient) : vec3(0.0, 1.0, 0.0);
}

vec4 GlobalSdfPolyMul(vec4 lhs, vec4 rhs)
{
    return vec4(
        lhs.x * rhs.x,
        lhs.x * rhs.y + lhs.y * rhs.x,
        lhs.x * rhs.z + lhs.y * rhs.y + lhs.z * rhs.x,
        lhs.x * rhs.w + lhs.y * rhs.z + lhs.z * rhs.y + lhs.w * rhs.x);
}

vec4 GlobalSdfTrilinearCubicCoefficients(GlobalSdfCellCorners c, vec3 aLocal, vec3 bLocal)
{
    float a = c.C100 - c.C000;
    float b = c.C010 - c.C000;
    float d = c.C001 - c.C000;
    float e = c.C110 - c.C100 - c.C010 + c.C000;
    float f = c.C101 - c.C100 - c.C001 + c.C000;
    float g = c.C011 - c.C010 - c.C001 + c.C000;
    float h = c.C111 - c.C110 - c.C101 - c.C011 + c.C100 + c.C010 + c.C001 - c.C000;
    vec4 u = vec4(aLocal.x, bLocal.x, 0.0, 0.0);
    vec4 v = vec4(aLocal.y, bLocal.y, 0.0, 0.0);
    vec4 w = vec4(aLocal.z, bLocal.z, 0.0, 0.0);
    return vec4(c.C000, 0.0, 0.0, 0.0) +
        a * u +
        b * v +
        d * w +
        e * GlobalSdfPolyMul(u, v) +
        f * GlobalSdfPolyMul(u, w) +
        g * GlobalSdfPolyMul(v, w) +
        h * GlobalSdfPolyMul(GlobalSdfPolyMul(u, v), w);
}

bool GlobalSdfAcceptRoot(float root, float tMax, inout float bestRoot)
{
    if (root >= -1.0e-5 && root <= tMax + 1.0e-5)
    {
        bestRoot = min(bestRoot, clamp(root, 0.0, tMax));
        return true;
    }
    return false;
}

bool SolveGlobalSdfCubicSmallestRoot(vec4 k, float tMax, out float root)
{
    root = 1.0e20;
    bool found = false;
    float eps = 1.0e-7;
    if (abs(k.w) < eps)
    {
        if (abs(k.z) < eps)
        {
            if (abs(k.y) < eps)
                return false;
            found = GlobalSdfAcceptRoot(-k.x / k.y, tMax, root);
            return found;
        }

        float disc = k.y * k.y - 4.0 * k.z * k.x;
        if (disc < -eps)
            return false;
        float sqrtDisc = sqrt(max(disc, 0.0));
        found = GlobalSdfAcceptRoot((-k.y - sqrtDisc) / (2.0 * k.z), tMax, root) || found;
        found = GlobalSdfAcceptRoot((-k.y + sqrtDisc) / (2.0 * k.z), tMax, root) || found;
        return found;
    }

    float a = k.z / k.w;
    float b = k.y / k.w;
    float c = k.x / k.w;
    float p = b - a * a / 3.0;
    float q = 2.0 * a * a * a / 27.0 - a * b / 3.0 + c;
    float halfQ = q * 0.5;
    float thirdP = p / 3.0;
    float discriminant = halfQ * halfQ + thirdP * thirdP * thirdP;
    if (discriminant > eps)
    {
        float sqrtDisc = sqrt(discriminant);
        float u = sign(-halfQ + sqrtDisc) * pow(abs(-halfQ + sqrtDisc), 1.0 / 3.0);
        float v = sign(-halfQ - sqrtDisc) * pow(abs(-halfQ - sqrtDisc), 1.0 / 3.0);
        found = GlobalSdfAcceptRoot(u + v - a / 3.0, tMax, root) || found;
    }
    else
    {
        float radius = 2.0 * sqrt(max(-thirdP, 0.0));
        float denom = max(radius * radius * radius * 0.125, 1.0e-10);
        float angle = acos(clamp(-halfQ / denom, -1.0, 1.0));
        found = GlobalSdfAcceptRoot(radius * cos(angle / 3.0) - a / 3.0, tMax, root) || found;
        found = GlobalSdfAcceptRoot(radius * cos((angle + 6.28318530718) / 3.0) - a / 3.0, tMax, root) || found;
        found = GlobalSdfAcceptRoot(radius * cos((angle + 12.56637061436) / 3.0) - a / 3.0, tMax, root) || found;
    }
    return found;
}

bool IntersectTrilinearCell(
    GlobalSdfCellCorners corners,
    vec3 aLocal,
    vec3 bLocal,
    float tEnter,
    float tExit,
    float voxelSize,
    out float tHit,
    out float residual,
    out vec3 normal)
{
    float cellTMax = max(tExit - tEnter, 0.0);
    vec4 k = GlobalSdfTrilinearCubicCoefficients(corners, aLocal, bLocal);
    float root;
    if (!SolveGlobalSdfCubicSmallestRoot(k, cellTMax, root))
        return false;

    for (uint i = 0u; i < GLOBAL_SDF_CUBIC_NEWTON_ITERATIONS; i++)
    {
        float f = ((k.w * root + k.z) * root + k.y) * root + k.x;
        float df = (3.0 * k.w * root + 2.0 * k.z) * root + k.y;
        if (abs(df) > 1.0e-6)
            root = clamp(root - f / df, 0.0, cellTMax);
    }

    vec3 localHit = aLocal + bLocal * root;
    tHit = tEnter + root;
    residual = abs(EvaluateGlobalSdfTrilinear(corners, localHit));
    normal = AnalyticTrilinearGradient(corners, localHit, voxelSize);
    return true;
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

bool GlobalSdfRayAabbInterval(vec3 origin, vec3 direction, vec3 boundsMin, vec3 boundsMax, out float enterT, out float exitT)
{
    vec3 safeDirection = vec3(
        abs(direction.x) > 1.0e-6 ? direction.x : (direction.x < 0.0 ? -1.0e-6 : 1.0e-6),
        abs(direction.y) > 1.0e-6 ? direction.y : (direction.y < 0.0 ? -1.0e-6 : 1.0e-6),
        abs(direction.z) > 1.0e-6 ? direction.z : (direction.z < 0.0 ? -1.0e-6 : 1.0e-6));
    vec3 invDirection = 1.0 / safeDirection;
    vec3 t0 = (boundsMin - origin) * invDirection;
    vec3 t1 = (boundsMax - origin) * invDirection;
    vec3 tNear = min(t0, t1);
    vec3 tFar = max(t0, t1);
    enterT = max(tNear.x, max(tNear.y, tNear.z));
    exitT = min(tFar.x, min(tFar.y, tFar.z));
    return exitT >= max(enterT, 0.0);
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
    float requestedStartT = max(startDistance, 0.0);
    uint steps = 0u;
    float voxelSize = max(cascade.WorldMinAndVoxelSize.w, 0.001);
    float minStep = voxelSize * GLOBAL_SDF_TRACE_MIN_STEP_VOXELS;
    float enterT;
    float rawExitT;
    if (!GlobalSdfRayAabbInterval(
        origin,
        direction,
        cascade.WorldMinAndVoxelSize.xyz,
        cascade.WorldMinAndVoxelSize.xyz + cascade.WorldExtentAndInvVoxelSize.xyz,
        enterT,
        rawExitT))
    {
        return GlobalSdfTraceResult(false, min(requestedStartT, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, false);
    }

    float exitT = min(maxDistance, rawExitT);
    float t = max(requestedStartT, max(enterT, 0.0));
    if (t > exitT)
        return GlobalSdfTraceResult(false, min(requestedStartT, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, false);

    float cellAdvanceEpsilon = voxelSize * 0.001;
    int res = int(max(cascade.Resolution, 1u));
    uint ddaCells = 0u;
    bool ddaExhausted = false;
    vec3 p;
    while (steps < maxSteps && t <= exitT && t <= maxDistance)
    {
        p = origin + direction * t;
        GlobalSdfSample sampleValue = SampleGlobalSdfCascade(p, cascade, cascadeIndex);
        if (!sampleValue.Valid)
            return GlobalSdfTraceResult(false, min(t, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, false);

        if (abs(sampleValue.DistanceMeters) > voxelSize * GLOBAL_SDF_TRACE_NEAR_BAND_VOXELS)
        {
            float coarseStep = max(sampleValue.DistanceMeters - voxelSize * GLOBAL_SDF_TRACE_NEAR_BAND_VOXELS, minStep);
            t += coarseStep;
            steps++;
            continue;
        }

        vec3 logicalVoxelFloat = (p - cascade.WorldMinAndVoxelSize.xyz) * cascade.WorldExtentAndInvVoxelSize.w;
        if (any(lessThan(logicalVoxelFloat, vec3(0.0))) || any(greaterThanEqual(logicalVoxelFloat, vec3(float(res)))))
            return GlobalSdfTraceResult(false, min(t, maxDistance), cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, false);

        ivec3 logicalCell = clamp(ivec3(floor(logicalVoxelFloat - vec3(0.5))), ivec3(0), ivec3(max(res - 2, 0)));
        vec3 cellMin = cascade.WorldMinAndVoxelSize.xyz + (vec3(logicalCell) + vec3(0.5)) * voxelSize;
        vec3 cellMax = cellMin + vec3(voxelSize);
        float cellExitT = min(GlobalSdfRayAabbExit(origin, direction, cellMin, cellMax), min(maxDistance, exitT));
        float tEnter = t;
        float tExit = max(tEnter, cellExitT);
        vec3 aLocal = (origin + direction * tEnter - cellMin) / voxelSize;
        vec3 bLocal = direction / voxelSize;
        GlobalSdfCellCorners corners = FetchGlobalSdfCellCorners(logicalCell, cascade);
        float tHit;
        float residual;
        vec3 hitNormal;
        if (IntersectTrilinearCell(corners, aLocal, bLocal, tEnter, tExit, voxelSize, tHit, residual, hitNormal))
            return GlobalSdfTraceResult(true, tHit, cascadeIndex, hitNormal, residual, steps + 1u, false);

        if (sampleValue.DistanceMeters <= 0.0)
        {
            hitNormal = EstimateGlobalSdfNormal(p, cascade, cascadeIndex);
            return GlobalSdfTraceResult(true, t, cascadeIndex, hitNormal, abs(sampleValue.DistanceMeters), steps + 1u, false);
        }

        steps++;
        ddaCells++;
        if (ddaCells >= GLOBAL_SDF_DDA_MAX_CELLS)
        {
            ddaExhausted = true;
            break;
        }
        t = max(t + cellAdvanceEpsilon, tExit + cellAdvanceEpsilon);
    }

    bool stepExhausted = (steps >= maxSteps || ddaExhausted) && t <= exitT && t <= maxDistance;
    float missT = stepExhausted ? min(t, maxDistance) : min(exitT, maxDistance);
    return GlobalSdfTraceResult(false, missT, cascadeIndex, vec3(0.0, 1.0, 0.0), 0.0, steps, stepExhausted);
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
