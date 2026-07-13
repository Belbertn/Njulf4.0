#ifndef NJULF_FARFIELD_CLIPMAP_GLSL
#define NJULF_FARFIELD_CLIPMAP_GLSL

struct FarFieldClipmapParams
{
    vec3 origin;
    float voxelSize;
    uvec3 resolution;
    float extent;
    float startDistance;
    uint maxTraceSteps;
    bool enabled;
    bool forceAll;
    uint instanceCount;
    uint voxelBufferIndex;
    uint distanceBufferIndex;
    bool distanceFieldValid;
};

FarFieldClipmapParams ReadFarFieldClipmapParams(uint bufferIndex)
{
    FarFieldClipmapParams p;
    vec4 origin = ReadStorageVec4(bufferIndex, 0u);
    vec4 resolution = ReadStorageVec4(bufferIndex, 4u);
    vec4 trace = ReadStorageVec4(bufferIndex, 8u);
    vec4 bake = ReadStorageVec4(bufferIndex, 12u);
    vec4 diagnostics = ReadStorageVec4(bufferIndex, 16u);
    vec4 jumpFlood = ReadStorageVec4(bufferIndex, 20u);
    p.origin = origin.xyz;
    p.voxelSize = max(origin.w, 0.0001);
    p.resolution = uvec3(max(resolution.xyz, vec3(1.0)));
    p.extent = max(resolution.w, p.voxelSize);
    p.startDistance = max(trace.x, 0.0);
    p.maxTraceSteps = max(uint(trace.y), 1u);
    p.enabled = trace.z > 0.5;
    p.forceAll = trace.w > 0.5;
    p.instanceCount = uint(max(bake.x, 0.0));
    p.voxelBufferIndex = uint(max(diagnostics.x, 0.0));
    p.distanceBufferIndex = uint(max(jumpFlood.x, 0.0));
    p.distanceFieldValid = jumpFlood.w > 0.5;
    return p;
}

uint FarFieldVoxelIndex(ivec3 voxel, FarFieldClipmapParams p)
{
    uvec3 v = uvec3(voxel);
    return v.x + v.y * p.resolution.x + v.z * p.resolution.x * p.resolution.y;
}

bool FarFieldInside(ivec3 voxel, FarFieldClipmapParams p)
{
    return all(greaterThanEqual(voxel, ivec3(0))) && all(lessThan(voxel, ivec3(p.resolution)));
}

uint FarFieldPackedDistanceWordIndex(uint voxelIndex)
{
    return voxelIndex >> 1u;
}

float ReadFarFieldDistanceVoxels(ivec3 voxel, FarFieldClipmapParams p)
{
    uint voxelIndex = FarFieldVoxelIndex(voxel, p);
    uint packed = ReadStorageWord(p.distanceBufferIndex, FarFieldPackedDistanceWordIndex(voxelIndex));
    uint encoded = ((voxelIndex & 1u) == 0u) ? (packed & 0xffffu) : ((packed >> 16u) & 0xffffu);
    return float(encoded) * (1.0 / 256.0);
}

vec3 EstimateFarFieldNormal(ivec3 voxel, FarFieldClipmapParams p, vec3 fallbackNormal)
{
    ivec3 lo = max(voxel - ivec3(1), ivec3(0));
    ivec3 hi = min(voxel + ivec3(1), ivec3(p.resolution) - ivec3(1));
    vec3 gradient = vec3(
        ReadFarFieldDistanceVoxels(ivec3(hi.x, voxel.y, voxel.z), p) - ReadFarFieldDistanceVoxels(ivec3(lo.x, voxel.y, voxel.z), p),
        ReadFarFieldDistanceVoxels(ivec3(voxel.x, hi.y, voxel.z), p) - ReadFarFieldDistanceVoxels(ivec3(voxel.x, lo.y, voxel.z), p),
        ReadFarFieldDistanceVoxels(ivec3(voxel.x, voxel.y, hi.z), p) - ReadFarFieldDistanceVoxels(ivec3(voxel.x, voxel.y, lo.z), p));
    float len2 = dot(gradient, gradient);
    return len2 > 0.000001 ? normalize(gradient) : fallbackNormal;
}

bool TraceFarFieldClipmapDetailed(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out bool stepExhausted,
    out uint visitedSteps);

bool TraceFarFieldClipmap(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo)
{
    bool stepExhausted;
    uint visitedSteps;
    return TraceFarFieldClipmapDetailed(origin, dir, tMin, tMax, hitT, faceNormal, albedo, stepExhausted, visitedSteps);
}

bool TraceFarFieldClipmapDda(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    FarFieldClipmapParams p,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out bool stepExhausted,
    out uint visitedSteps)
{
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    stepExhausted = false;
    visitedSteps = 0u;

    vec3 invDir = vec3(
        abs(dir.x) > 0.000001 ? 1.0 / dir.x : 1.0e30,
        abs(dir.y) > 0.000001 ? 1.0 / dir.y : 1.0e30,
        abs(dir.z) > 0.000001 ? 1.0 / dir.z : 1.0e30);
    vec3 boundsMin = p.origin;
    vec3 boundsMax = p.origin + vec3(p.resolution) * p.voxelSize;
    vec3 t0 = (boundsMin - origin) * invDir;
    vec3 t1 = (boundsMax - origin) * invDir;
    vec3 tNear3 = min(t0, t1);
    vec3 tFar3 = max(t0, t1);
    float tNear = max(max(tNear3.x, tNear3.y), max(tNear3.z, tMin));
    float tFar = min(min(tFar3.x, tFar3.y), min(tFar3.z, tMax));
    if (tNear > tFar)
        return false;

    vec3 pos = origin + dir * tNear;
    ivec3 voxel = ivec3(floor((pos - p.origin) / p.voxelSize));
    voxel = clamp(voxel, ivec3(0), ivec3(p.resolution) - ivec3(1));
    ivec3 stepDir = ivec3(sign(dir));
    vec3 nextBoundary = p.origin + (vec3(voxel) + step(vec3(0.0), dir)) * p.voxelSize;
    vec3 tMaxAxis = (nextBoundary - origin) * invDir;
    vec3 tDelta = abs(vec3(p.voxelSize) * invDir);
    tMaxAxis = mix(vec3(1.0e30), tMaxAxis, notEqual(stepDir, ivec3(0)));
    tDelta = mix(vec3(1.0e30), tDelta, notEqual(stepDir, ivec3(0)));
    float t = tNear;

    for (uint stepIndex = 0u; stepIndex < p.maxTraceSteps && t <= tFar; stepIndex++)
    {
        visitedSteps = stepIndex + 1u;
        if (!FarFieldInside(voxel, p))
            break;

        uint packed = ReadStorageWord(p.voxelBufferIndex, FarFieldVoxelIndex(voxel, p));
        if ((packed & 0x80000000u) != 0u)
        {
            vec3 rgb = vec3(
                float((packed >> 0u) & 0xffu),
                float((packed >> 8u) & 0xffu),
                float((packed >> 16u) & 0xffu)) / 255.0;
            albedo = rgb;
            hitT = t;
            return true;
        }

        if (tMaxAxis.x < tMaxAxis.y && tMaxAxis.x < tMaxAxis.z)
        {
            voxel.x += stepDir.x;
            t = tMaxAxis.x;
            tMaxAxis.x += tDelta.x;
            faceNormal = vec3(-stepDir.x, 0.0, 0.0);
        }
        else if (tMaxAxis.y < tMaxAxis.z)
        {
            voxel.y += stepDir.y;
            t = tMaxAxis.y;
            tMaxAxis.y += tDelta.y;
            faceNormal = vec3(0.0, -stepDir.y, 0.0);
        }
        else
        {
            voxel.z += stepDir.z;
            t = tMaxAxis.z;
            tMaxAxis.z += tDelta.z;
            faceNormal = vec3(0.0, 0.0, -stepDir.z);
        }
    }

    stepExhausted = visitedSteps >= p.maxTraceSteps && t <= tFar;
    return false;
}

bool TraceFarFieldClipmapSphereMarch(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    FarFieldClipmapParams p,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out bool stepExhausted,
    out uint visitedSteps)
{
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    stepExhausted = false;
    visitedSteps = 0u;

    vec3 invDir = vec3(
        abs(dir.x) > 0.000001 ? 1.0 / dir.x : 1.0e30,
        abs(dir.y) > 0.000001 ? 1.0 / dir.y : 1.0e30,
        abs(dir.z) > 0.000001 ? 1.0 / dir.z : 1.0e30);
    vec3 boundsMin = p.origin;
    vec3 boundsMax = p.origin + vec3(p.resolution) * p.voxelSize;
    vec3 t0 = (boundsMin - origin) * invDir;
    vec3 t1 = (boundsMax - origin) * invDir;
    vec3 tNear3 = min(t0, t1);
    vec3 tFar3 = max(t0, t1);
    float tNear = max(max(tNear3.x, tNear3.y), max(tNear3.z, tMin));
    float tFar = min(min(tFar3.x, tFar3.y), min(tFar3.z, tMax));
    if (tNear > tFar)
        return false;

    float t = tNear;
    vec3 fallbackNormal = -normalize(dir);
    for (uint stepIndex = 0u; stepIndex < p.maxTraceSteps && t <= tFar; stepIndex++)
    {
        visitedSteps = stepIndex + 1u;
        vec3 pos = origin + dir * t;
        ivec3 voxel = ivec3(floor((pos - p.origin) / p.voxelSize));
        if (!FarFieldInside(voxel, p))
            break;

        uint packed = ReadStorageWord(p.voxelBufferIndex, FarFieldVoxelIndex(voxel, p));
        if ((packed & 0x80000000u) != 0u)
        {
            vec3 rgb = vec3(
                float((packed >> 0u) & 0xffu),
                float((packed >> 8u) & 0xffu),
                float((packed >> 16u) & 0xffu)) / 255.0;
            albedo = rgb;
            hitT = t;
            faceNormal = EstimateFarFieldNormal(voxel, p, fallbackNormal);
            return true;
        }

        float distanceVoxels = ReadFarFieldDistanceVoxels(voxel, p);
        float stepDistance = max(distanceVoxels * p.voxelSize, p.voxelSize * 0.5);
        t += min(stepDistance, p.voxelSize * 8.0);
        faceNormal = fallbackNormal;
    }

    stepExhausted = visitedSteps >= p.maxTraceSteps && t <= tFar;
    return false;
}

bool TraceFarFieldClipmapDetailed(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out bool stepExhausted,
    out uint visitedSteps)
{
    FarFieldClipmapParams p = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    stepExhausted = false;
    visitedSteps = 0u;
    if (!p.enabled)
        return false;

    if (p.distanceFieldValid)
    {
        return TraceFarFieldClipmapSphereMarch(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, stepExhausted, visitedSteps);
    }

    return TraceFarFieldClipmapDda(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, stepExhausted, visitedSteps);
}

#endif
