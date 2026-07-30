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
    uint materialPayloadVersion;
    uint voxelStrideWords;
};

const uint FAR_FIELD_PAGE_TABLE_ENTRY_WORDS = 8u;
const uint FAR_FIELD_PAGE_CASCADE_MASK = 0xffu;
const uint FAR_FIELD_PAGE_ALLOCATED_FLAG = 1u << 8u;
const uint FAR_FIELD_PAGE_VALID_FLAG = 1u << 9u;
const uint FAR_FIELD_MATERIAL_V2_VERSION = 2u;
const uint FAR_FIELD_MATERIAL_SIDEDNESS_CONE_VERSION = 3u;
const uint FAR_FIELD_MATERIAL_OCCLUSION_VERSION = 4u;
const uint FAR_FIELD_MATERIAL_V2_STRIDE_WORDS = 8u;
const uint FAR_FIELD_MATERIAL_V2_EMPTY_KEY = 0xffffffffu;
const uint FAR_FIELD_MATERIAL_V2_OCCUPIED_BIT = 1u << 31u;
const uint FAR_FIELD_MATERIAL_V2_STORED_FLAG_MASK = 0x7fffu;
const uint FAR_FIELD_MATERIAL_DOUBLE_SIDED_FLAG = 1u << 6u;
const float FAR_FIELD_INVERSE_PI = 0.3183098861837907;

struct FarFieldVoxelMaterial
{
    vec3 diffuseReflectance;
    vec3 emissiveRadiance;
    vec3 geometricNormal;
    float materialOcclusion;
    float coverage;
    float normalCone;
    uint materialFlags;
    uint materialRevision;
    uint transportProfileRevision;
};

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
    vec4 materialPayload = ReadStorageVec4(bufferIndex, 36u);
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
    p.materialPayloadVersion = max(uint(max(materialPayload.x, 0.0)), 1u);
    p.voxelStrideWords = max(uint(max(materialPayload.y, 0.0)), 1u);
    return p;
}

uint FarFieldVoxelIndex(ivec3 voxel, FarFieldClipmapParams p)
{
    uvec3 v = uvec3(voxel);
    return v.x + v.y * p.resolution.x + v.z * p.resolution.x * p.resolution.y;
}

uint FarFieldVoxelPayloadWordOffset(uint logicalVoxelIndex, FarFieldClipmapParams p)
{
    return logicalVoxelIndex * max(p.voxelStrideWords, 1u);
}

vec3 DecodeFarFieldOctahedralNormal(uint packed)
{
    vec2 encoded = unpackSnorm2x16(packed);
    vec3 normal = vec3(encoded, 1.0 - abs(encoded.x) - abs(encoded.y));
    if (normal.z < 0.0)
    {
        vec2 signs = vec2(normal.x >= 0.0 ? 1.0 : -1.0, normal.y >= 0.0 ? 1.0 : -1.0);
        normal.xy = (vec2(1.0) - abs(normal.yx)) * signs;
    }
    return normalize(normal);
}

vec3 DecodeFarFieldDiffuseRgb10(uint packed)
{
    return vec3(
        float(packed & 0x3ffu),
        float((packed >> 10u) & 0x3ffu),
        float((packed >> 20u) & 0x3ffu)) / 1023.0;
}

FarFieldVoxelMaterial EmptyFarFieldVoxelMaterial()
{
    FarFieldVoxelMaterial material;
    material.diffuseReflectance = vec3(0.0);
    material.emissiveRadiance = vec3(0.0);
    material.geometricNormal = vec3(0.0);
    material.materialOcclusion = 1.0;
    material.coverage = 0.0;
    material.normalCone = 0.0;
    material.materialFlags = 0u;
    material.materialRevision = 0u;
    material.transportProfileRevision = 0u;
    return material;
}

bool ReadFarFieldVoxelMaterial(
    uint bufferIndex,
    uint logicalVoxelIndex,
    FarFieldClipmapParams p,
    out FarFieldVoxelMaterial material)
{
    material = EmptyFarFieldVoxelMaterial();
    uint wordOffset = FarFieldVoxelPayloadWordOffset(logicalVoxelIndex, p);
    if (p.materialPayloadVersion < FAR_FIELD_MATERIAL_V2_VERSION)
    {
        uint packed = ReadStorageWord(bufferIndex, wordOffset);
        if ((packed & 0x80000000u) == 0u)
            return false;

        material.diffuseReflectance = vec3(
            float((packed >> 0u) & 0xffu),
            float((packed >> 8u) & 0xffu),
            float((packed >> 16u) & 0xffu)) / 255.0;
        // V1 had no sidedness metadata and historically behaved as a
        // conservative two-sided volume.
        material.materialFlags = FAR_FIELD_MATERIAL_DOUBLE_SIDED_FLAG;
        material.coverage = 1.0;
        return true;
    }

    uint winnerKey = ReadStorageWord(bufferIndex, wordOffset + 0u);
    uint metadata = ReadStorageWord(bufferIndex, wordOffset + 1u);
    if (winnerKey == FAR_FIELD_MATERIAL_V2_EMPTY_KEY ||
        (metadata & FAR_FIELD_MATERIAL_V2_OCCUPIED_BIT) == 0u)
    {
        return false;
    }

    uint emissionRg = ReadStorageWord(bufferIndex, wordOffset + 3u);
    uint emissionBAndOcclusion = ReadStorageWord(bufferIndex, wordOffset + 4u);
    material.diffuseReflectance = DecodeFarFieldDiffuseRgb10(
        ReadStorageWord(bufferIndex, wordOffset + 2u));
    material.emissiveRadiance = vec3(
        unpackHalf2x16(emissionRg),
        unpackHalf2x16(emissionBAndOcclusion).x);
    material.materialOcclusion =
        p.materialPayloadVersion >= FAR_FIELD_MATERIAL_OCCLUSION_VERSION
            ? clamp(unpackHalf2x16(emissionBAndOcclusion).y, 0.0, 1.0)
            : 1.0;
    material.geometricNormal = DecodeFarFieldOctahedralNormal(
        ReadStorageWord(bufferIndex, wordOffset + 5u));
    material.coverage = float(metadata & 0xffu) / 255.0;
    material.normalCone = float((metadata >> 8u) & 0xffu) / 255.0;
    material.materialFlags = (metadata >> 16u) & FAR_FIELD_MATERIAL_V2_STORED_FLAG_MASK;
    material.materialRevision = ReadStorageWord(bufferIndex, wordOffset + 6u);
    material.transportProfileRevision = ReadStorageWord(bufferIndex, wordOffset + 7u);
    return true;
}

bool FarFieldVoxelOccupied(
    uint bufferIndex,
    uint logicalVoxelIndex,
    FarFieldClipmapParams p)
{
    uint wordOffset = FarFieldVoxelPayloadWordOffset(logicalVoxelIndex, p);
    uint firstWord = ReadStorageWord(bufferIndex, wordOffset);
    if (p.materialPayloadVersion < FAR_FIELD_MATERIAL_V2_VERSION)
        return (firstWord & 0x80000000u) != 0u;

    uint metadata = ReadStorageWord(bufferIndex, wordOffset + 1u);
    return firstWord != FAR_FIELD_MATERIAL_V2_EMPTY_KEY &&
        (metadata & FAR_FIELD_MATERIAL_V2_OCCUPIED_BIT) != 0u;
}

bool ResolveFarFieldMaterialFacing(
    FarFieldClipmapParams p,
    vec3 rayDirection,
    inout FarFieldVoxelMaterial material)
{
    if (p.materialPayloadVersion < FAR_FIELD_MATERIAL_V2_VERSION)
        return true;

    bool doubleSided =
        (material.materialFlags & FAR_FIELD_MATERIAL_DOUBLE_SIDED_FLAG) != 0u;
    float facing = dot(material.geometricNormal, rayDirection);
    if (!doubleSided)
    {
        if (p.materialPayloadVersion >= FAR_FIELD_MATERIAL_SIDEDNESS_CONE_VERSION)
        {
            // V3's cone is the conservative maximum angular deviation from
            // the selected normal, normalized by PI. It is accumulated across
            // every alpha-surviving surface in the voxel after winner
            // publication, so an opposed contender cannot be hidden merely by
            // the selected surface facing away from this trace.
            float rayAngleNormalized =
                acos(clamp(facing, -1.0, 1.0)) * FAR_FIELD_INVERSE_PI;
            if (rayAngleNormalized + material.normalCone <= 0.5)
                return false;

            // The compact payload has one representative normal. Flip it only
            // when the conservative overlap cone, rather than that primary
            // normal, made the single-sided hit valid.
            if (facing >= 0.0)
                material.geometricNormal = -material.geometricNormal;
            return true;
        }

        if (facing >= 0.0)
            return false;
    }
    if (doubleSided && facing > 0.0)
        material.geometricNormal = -material.geometricNormal;
    return true;
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
    out vec3 emission,
    out float materialOcclusion,
    out bool stepExhausted,
    out uint visitedSteps);

bool TraceFarFieldClipmapDetailed(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out vec3 emission,
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
    vec3 emission;
    bool stepExhausted;
    uint visitedSteps;
    return TraceFarFieldClipmapDetailed(
        origin,
        dir,
        tMin,
        tMax,
        hitT,
        faceNormal,
        albedo,
        emission,
        stepExhausted,
        visitedSteps);
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
    out vec3 emission,
    out float materialOcclusion,
    out bool stepExhausted,
    out uint visitedSteps)
{
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    emission = vec3(0.0);
    materialOcclusion = 1.0;
    stepExhausted = false;
    visitedSteps = 0u;
    vec3 safeDirection = normalize(dir);
    float t = max(tMin, 0.0);

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

        uint logicalVoxelIndex =
            FarFieldPageVoxelOffset(p, page) + FarFieldVoxelIndex(voxel, p);
        FarFieldVoxelMaterial voxelMaterial;
        if (ReadFarFieldVoxelMaterial(
                p.voxelBufferIndex,
                logicalVoxelIndex,
                p,
                voxelMaterial) &&
            ResolveFarFieldMaterialFacing(p, safeDirection, voxelMaterial))
        {
            albedo = voxelMaterial.diffuseReflectance;
            emission = voxelMaterial.emissiveRadiance;
            materialOcclusion = voxelMaterial.materialOcclusion;
            hitT = t;
            faceNormal = p.materialPayloadVersion >= FAR_FIELD_MATERIAL_V2_VERSION
                ? voxelMaterial.geometricNormal
                : EstimateFarFieldPagedNormal(voxel, p, page, -safeDirection);
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
    out vec3 emission,
    out float materialOcclusion,
    out bool stepExhausted,
    out uint visitedSteps)
{
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    emission = vec3(0.0);
    materialOcclusion = 1.0;
    stepExhausted = false;
    visitedSteps = 0u;
    vec3 safeDirection = normalize(dir);

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

        FarFieldVoxelMaterial voxelMaterial;
        if (ReadFarFieldVoxelMaterial(
                p.voxelBufferIndex,
                FarFieldVoxelIndex(voxel, p),
                p,
                voxelMaterial) &&
            ResolveFarFieldMaterialFacing(p, safeDirection, voxelMaterial))
        {
            albedo = voxelMaterial.diffuseReflectance;
            emission = voxelMaterial.emissiveRadiance;
            materialOcclusion = voxelMaterial.materialOcclusion;
            if (p.materialPayloadVersion >= FAR_FIELD_MATERIAL_V2_VERSION)
                faceNormal = voxelMaterial.geometricNormal;
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
    out vec3 emission,
    out float materialOcclusion,
    out bool stepExhausted,
    out uint visitedSteps)
{
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    emission = vec3(0.0);
    materialOcclusion = 1.0;
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

        FarFieldVoxelMaterial voxelMaterial;
        if (ReadFarFieldVoxelMaterial(
                p.voxelBufferIndex,
                FarFieldVoxelIndex(voxel, p),
                p,
                voxelMaterial) &&
            ResolveFarFieldMaterialFacing(p, -fallbackNormal, voxelMaterial))
        {
            albedo = voxelMaterial.diffuseReflectance;
            emission = voxelMaterial.emissiveRadiance;
            materialOcclusion = voxelMaterial.materialOcclusion;
            hitT = t;
            faceNormal = p.materialPayloadVersion >= FAR_FIELD_MATERIAL_V2_VERSION
                ? voxelMaterial.geometricNormal
                : EstimateFarFieldNormal(voxel, p, fallbackNormal);
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
    out vec3 emission,
    out float materialOcclusion,
    out bool stepExhausted,
    out uint visitedSteps)
{
    FarFieldClipmapParams p = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
    hitT = tMax;
    faceNormal = vec3(0.0);
    albedo = vec3(0.0);
    emission = vec3(0.0);
    materialOcclusion = 1.0;
    stepExhausted = false;
    visitedSteps = 0u;
    if (!p.enabled)
        return false;

    if (p.pagedEnabled)
        return TraceFarFieldPaged(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, emission, materialOcclusion, stepExhausted, visitedSteps);

    if (p.distanceFieldValid)
        return TraceFarFieldClipmapSphereMarch(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, emission, materialOcclusion, stepExhausted, visitedSteps);

    return TraceFarFieldClipmapDda(origin, dir, tMin, tMax, p, hitT, faceNormal, albedo, emission, materialOcclusion, stepExhausted, visitedSteps);
}

bool TraceFarFieldClipmapDetailed(
    vec3 origin,
    vec3 dir,
    float tMin,
    float tMax,
    out float hitT,
    out vec3 faceNormal,
    out vec3 albedo,
    out vec3 emission,
    out bool stepExhausted,
    out uint visitedSteps)
{
    float materialOcclusion;
    return TraceFarFieldClipmapDetailed(
        origin,
        dir,
        tMin,
        tMax,
        hitT,
        faceNormal,
        albedo,
        emission,
        materialOcclusion,
        stepExhausted,
        visitedSteps);
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
    vec3 emission;
    return TraceFarFieldClipmapDetailed(
        origin,
        dir,
        tMin,
        tMax,
        hitT,
        faceNormal,
        albedo,
        emission,
        stepExhausted,
        visitedSteps);
}

uint ReadFarFieldDebugPackedVoxel(
    FarFieldClipmapParams p,
    uint logicalVoxelIndex)
{
    if (p.materialPayloadVersion < FAR_FIELD_MATERIAL_V2_VERSION)
    {
        return ReadStorageWord(
            p.voxelBufferIndex,
            FarFieldVoxelPayloadWordOffset(logicalVoxelIndex, p));
    }

    FarFieldVoxelMaterial material;
    if (!ReadFarFieldVoxelMaterial(
            p.voxelBufferIndex,
            logicalVoxelIndex,
            p,
            material))
    {
        return 0u;
    }

    uvec3 rgb = uvec3(round(clamp(material.diffuseReflectance, vec3(0.0), vec3(1.0)) * 255.0));
    return 0x80000000u | rgb.r | (rgb.g << 8u) | (rgb.b << 16u);
}

bool ReadFarFieldDebugVoxel(FarFieldClipmapParams p, vec2 uv, out uint packed, out bool missing)
{
    packed = 0u;
    missing = false;
    if (!p.pagedEnabled)
    {
        uvec2 xy = min(uvec2(floor(uv * vec2(p.resolution.xy))), p.resolution.xy - uvec2(1u));
        ivec3 voxel = ivec3(int(xy.x), int(xy.y), int(p.resolution.z / 2u));
        packed = ReadFarFieldDebugPackedVoxel(p, FarFieldVoxelIndex(voxel, p));
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
    packed = ReadFarFieldDebugPackedVoxel(
        p,
        FarFieldPageVoxelOffset(p, page) + FarFieldVoxelIndex(voxel, p));
    return true;
}

#endif
