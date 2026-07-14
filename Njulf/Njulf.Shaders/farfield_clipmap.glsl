#ifndef NJULF_FARFIELD_CLIPMAP_GLSL
#define NJULF_FARFIELD_CLIPMAP_GLSL

// GPUFarFieldClipmapParams.  The legacy fields stay first so the emergency
// single-cube A/B path remains valid; the final vectors describe the bounded
// world-keyed page cache.
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
    uint pageTableBufferIndex;
    uint pageTableCapacity;
    uint pagePoolCapacity;
    uint cascadeCount;
    float baseVoxelSize;
    float cascadeVoxelScale;
    bool pagedEnabled;
    vec3 cameraPosition;
};

const uint FAR_FIELD_PAGE_TABLE_ENTRY_WORDS = 8u;
const uint FAR_FIELD_PAGE_CASCADE_MASK = 0xffu;
const uint FAR_FIELD_PAGE_ALLOCATED_FLAG = 1u << 8u;
const uint FAR_FIELD_PAGE_VALID_FLAG = 1u << 9u;

struct FarFieldPageReference
{
    ivec3 worldPage;
    uint cascade;
    uint physicalPageIndex;
    uint generation;
    bool allocated;
    bool valid;
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
    vec4 paging = ReadStorageVec4(bufferIndex, 24u);
    vec4 pagingLayout = ReadStorageVec4(bufferIndex, 28u);
    vec4 camera = ReadStorageVec4(bufferIndex, 32u);
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
    p.pageTableBufferIndex = uint(max(paging.x, 0.0));
    p.pageTableCapacity = uint(max(paging.y, 0.0));
    p.pagePoolCapacity = uint(max(paging.z, 0.0));
    p.cascadeCount = max(uint(max(paging.w, 0.0)), 1u);
    p.baseVoxelSize = max(pagingLayout.y, 0.0001);
    p.cascadeVoxelScale = max(pagingLayout.z, 1.0001);
    p.pagedEnabled = pagingLayout.w > 0.5;
    p.cameraPosition = camera.xyz;
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

uint FarFieldPageHash(uint cascade, ivec3 worldPage)
{
    uint hash = 2166136261u;
    hash = (hash ^ cascade) * 16777619u;
    hash = (hash ^ uint(worldPage.x)) * 16777619u;
    hash = (hash ^ uint(worldPage.y)) * 16777619u;
    hash = (hash ^ uint(worldPage.z)) * 16777619u;
    hash ^= hash >> 16u;
    hash *= 0x7feb352du;
    hash ^= hash >> 15u;
    hash *= 0x846ca68bu;
    return hash ^ (hash >> 16u);
}

bool FindFarFieldPage(
    FarFieldClipmapParams p,
    uint cascade,
    ivec3 worldPage,
    out FarFieldPageReference page)
{
    page.worldPage = worldPage;
    page.cascade = cascade;
    page.physicalPageIndex = 0u;
    page.generation = 0u;
    page.allocated = false;
    page.valid = false;
    if (p.pageTableCapacity == 0u || (p.pageTableCapacity & (p.pageTableCapacity - 1u)) != 0u)
        return false;

    uint mask = p.pageTableCapacity - 1u;
    uint tableIndex = FarFieldPageHash(cascade, worldPage) & mask;
    for (uint probe = 0u; probe < p.pageTableCapacity; probe++)
    {
        uint base = tableIndex * FAR_FIELD_PAGE_TABLE_ENTRY_WORDS;
        uint flags = ReadStorageWord(p.pageTableBufferIndex, base + 3u);
        if ((flags & FAR_FIELD_PAGE_ALLOCATED_FLAG) == 0u)
            return false;

        if ((flags & FAR_FIELD_PAGE_CASCADE_MASK) == cascade &&
            int(ReadStorageWord(p.pageTableBufferIndex, base + 0u)) == worldPage.x &&
            int(ReadStorageWord(p.pageTableBufferIndex, base + 1u)) == worldPage.y &&
            int(ReadStorageWord(p.pageTableBufferIndex, base + 2u)) == worldPage.z)
        {
            page.physicalPageIndex = ReadStorageWord(p.pageTableBufferIndex, base + 4u);
            page.generation = ReadStorageWord(p.pageTableBufferIndex, base + 5u);
            page.allocated = true;
            page.valid = (flags & FAR_FIELD_PAGE_VALID_FLAG) != 0u && page.physicalPageIndex < p.pagePoolCapacity;
            return true;
        }

        tableIndex = (tableIndex + 1u) & mask;
    }

    return false;
}

float FarFieldCascadeVoxelSize(FarFieldClipmapParams p, uint cascade)
{
    return max(p.baseVoxelSize * pow(p.cascadeVoxelScale, float(cascade)), 0.0001);
}

float FarFieldPageExtent(FarFieldClipmapParams p, uint cascade)
{
    return FarFieldCascadeVoxelSize(p, cascade) * float(max(p.resolution.x, 1u));
}

vec3 FarFieldPageOrigin(FarFieldClipmapParams p, uint cascade, ivec3 worldPage)
{
    return vec3(worldPage) * FarFieldPageExtent(p, cascade);
}

uint FarFieldPageVoxelOffset(FarFieldClipmapParams p, FarFieldPageReference page)
{
    uint pageVoxelCount = p.resolution.x * p.resolution.y * p.resolution.z;
    return page.physicalPageIndex * pageVoxelCount;
}

float ReadFarFieldPagedDistanceVoxels(ivec3 voxel, FarFieldClipmapParams p, FarFieldPageReference page)
{
    uint voxelIndex = FarFieldVoxelIndex(voxel, p);
    uint pageVoxelOffset = FarFieldPageVoxelOffset(p, page);
    uint packed = ReadStorageWord(p.distanceBufferIndex, (pageVoxelOffset >> 1u) + FarFieldPackedDistanceWordIndex(voxelIndex));
    uint encoded = ((voxelIndex & 1u) == 0u) ? (packed & 0xffffu) : ((packed >> 16u) & 0xffffu);
    return float(encoded) * (1.0 / 256.0);
}

vec3 EstimateFarFieldPagedNormal(
    ivec3 voxel,
    FarFieldClipmapParams p,
    FarFieldPageReference page,
    vec3 fallbackNormal)
{
    ivec3 lo = max(voxel - ivec3(1), ivec3(0));
    ivec3 hi = min(voxel + ivec3(1), ivec3(p.resolution) - ivec3(1));
    vec3 gradient = vec3(
        ReadFarFieldPagedDistanceVoxels(ivec3(hi.x, voxel.y, voxel.z), p, page) - ReadFarFieldPagedDistanceVoxels(ivec3(lo.x, voxel.y, voxel.z), p, page),
        ReadFarFieldPagedDistanceVoxels(ivec3(voxel.x, hi.y, voxel.z), p, page) - ReadFarFieldPagedDistanceVoxels(ivec3(voxel.x, lo.y, voxel.z), p, page),
        ReadFarFieldPagedDistanceVoxels(ivec3(voxel.x, voxel.y, hi.z), p, page) - ReadFarFieldPagedDistanceVoxels(ivec3(voxel.x, voxel.y, lo.z), p, page));
    float len2 = dot(gradient, gradient);
    return len2 > 0.000001 ? normalize(gradient) : fallbackNormal;
}

uint SelectFarFieldCascade(FarFieldClipmapParams p, vec3 worldPosition)
{
    if (p.cascadeCount <= 1u)
        return 0u;

    float relativeDistance = max(length(worldPosition - p.cameraPosition) - p.startDistance, 0.0);
    float baseExtent = max(FarFieldPageExtent(p, 0u), 0.0001);
    float logarithm = log(max(relativeDistance / baseExtent, 1.0)) / log(p.cascadeVoxelScale);
    return min(uint(max(floor(logarithm), 0.0)), p.cascadeCount - 1u);
}

float FarFieldTraceMaximumDistance(FarFieldClipmapParams p)
{
    if (!p.pagedEnabled)
        return max(p.extent, p.startDistance + p.voxelSize);

    uint outerCascade = max(p.cascadeCount, 1u) - 1u;
    // Three page widths cover the requested radius-one working set around the
    // camera.  A missing page simply returns no hit and lets the caller use the
    // environment fallback; this value never grows with world size.
    return max(p.startDistance + FarFieldPageExtent(p, outerCascade) * 3.0, p.startDistance + p.baseVoxelSize);
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

float FarFieldAdvanceToPageBoundary(vec3 origin, vec3 dir, float t, vec3 pageOrigin, float pageExtent)
{
    vec3 nextBoundary = pageOrigin + vec3(
        dir.x >= 0.0 ? pageExtent : 0.0,
        dir.y >= 0.0 ? pageExtent : 0.0,
        dir.z >= 0.0 ? pageExtent : 0.0);
    vec3 candidate = vec3(
        abs(dir.x) > 0.000001 ? (nextBoundary.x - origin.x) / dir.x : 1.0e30,
        abs(dir.y) > 0.000001 ? (nextBoundary.y - origin.y) / dir.y : 1.0e30,
        abs(dir.z) > 0.000001 ? (nextBoundary.z - origin.z) / dir.z : 1.0e30);
    float nextT = min(candidate.x, min(candidate.y, candidate.z));
    return max(nextT + max(pageExtent * 0.000001, 0.00001), t + 0.00001);
}

bool TraceFarFieldPaged(
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
    float t = max(tMin, 0.0);
    vec3 safeDirection = normalize(dir);

    for (uint stepIndex = 0u; stepIndex < p.maxTraceSteps && t <= tMax; stepIndex++)
    {
        visitedSteps = stepIndex + 1u;
        vec3 position = origin + safeDirection * t;
        uint cascade = SelectFarFieldCascade(p, position);
        float voxelSize = FarFieldCascadeVoxelSize(p, cascade);
        float pageExtent = FarFieldPageExtent(p, cascade);
        ivec3 worldPage = ivec3(floor(position / pageExtent));
        vec3 pageOrigin = FarFieldPageOrigin(p, cascade, worldPage);
        FarFieldPageReference page;
        if (!FindFarFieldPage(p, cascade, worldPage, page) || !page.valid)
        {
            // Missing/pending/stale pages are an explicit no-hit condition.  Do
            // not read a reused physical page and do not fabricate occlusion.
            t = FarFieldAdvanceToPageBoundary(origin, safeDirection, t, pageOrigin, pageExtent);
            continue;
        }

        ivec3 voxel = ivec3(floor((position - pageOrigin) / voxelSize));
        if (!FarFieldInside(voxel, p))
        {
            t = FarFieldAdvanceToPageBoundary(origin, safeDirection, t, pageOrigin, pageExtent);
            continue;
        }

        uint packed = ReadStorageWord(p.voxelBufferIndex, FarFieldPageVoxelOffset(p, page) + FarFieldVoxelIndex(voxel, p));
        if ((packed & 0x80000000u) != 0u)
        {
            albedo = vec3(
                float((packed >> 0u) & 0xffu),
                float((packed >> 8u) & 0xffu),
                float((packed >> 16u) & 0xffu)) / 255.0;
            hitT = t;
            faceNormal = EstimateFarFieldPagedNormal(voxel, p, page, -safeDirection);
            return true;
        }

        // The page-local jump-flood field lets empty regions advance in O(1),
        // while clamping to the page boundary prevents a page-local distance
        // estimate from skipping geometry in a neighbouring page.
        float distanceVoxels = ReadFarFieldPagedDistanceVoxels(voxel, p, page);
        float distanceStep = clamp(max(distanceVoxels * voxelSize, voxelSize * 0.5), voxelSize * 0.5, voxelSize * 8.0);
        float pageExit = FarFieldAdvanceToPageBoundary(origin, safeDirection, t, pageOrigin, pageExtent);
        t = min(t + distanceStep, pageExit);
        faceNormal = -safeDirection;
    }

    stepExhausted = visitedSteps >= p.maxTraceSteps && t <= tMax;
    return false;
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
            albedo = vec3(
                float((packed >> 0u) & 0xffu),
                float((packed >> 8u) & 0xffu),
                float((packed >> 16u) & 0xffu)) / 255.0;
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
            albedo = vec3(
                float((packed >> 0u) & 0xffu),
                float((packed >> 8u) & 0xffu),
                float((packed >> 16u) & 0xffu)) / 255.0;
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

    if (p.pagedEnabled)
        return TraceFarFieldPaged(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, stepExhausted, visitedSteps);

    if (p.distanceFieldValid)
        return TraceFarFieldClipmapSphereMarch(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, stepExhausted, visitedSteps);

    return TraceFarFieldClipmapDda(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, stepExhausted, visitedSteps);
}

bool ReadFarFieldDebugVoxel(FarFieldClipmapParams p, vec2 uv, out uint packed, out bool missing)
{
    packed = 0u;
    missing = false;
    if (!p.pagedEnabled)
    {
        uvec2 xy = min(uvec2(floor(uv * vec2(p.resolution.xy))), p.resolution.xy - uvec2(1u));
        ivec3 voxel = ivec3(int(xy.x), int(xy.y), int(p.resolution.z / 2u));
        packed = ReadStorageWord(p.voxelBufferIndex, FarFieldVoxelIndex(voxel, p));
        return true;
    }

    uint cascade = 0u;
    float extent = FarFieldPageExtent(p, cascade);
    ivec3 worldPage = ivec3(floor(p.cameraPosition / extent));
    FarFieldPageReference page;
    if (!FindFarFieldPage(p, cascade, worldPage, page) || !page.valid)
    {
        missing = true;
        return false;
    }

    uvec2 xy = min(uvec2(floor(uv * vec2(p.resolution.xy))), p.resolution.xy - uvec2(1u));
    ivec3 voxel = ivec3(int(xy.x), int(xy.y), int(p.resolution.z / 2u));
    packed = ReadStorageWord(p.voxelBufferIndex, FarFieldPageVoxelOffset(p, page) + FarFieldVoxelIndex(voxel, p));
    return true;
}

#endif
