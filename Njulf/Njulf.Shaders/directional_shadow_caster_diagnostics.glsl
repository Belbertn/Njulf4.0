#ifndef NJULF_DIRECTIONAL_SHADOW_CASTER_DIAGNOSTICS_GLSL
#define NJULF_DIRECTIONAL_SHADOW_CASTER_DIAGNOSTICS_GLSL

// This include is intentionally used only by native diagnostic shader
// variants. It writes a bounded shared record bank, so the normal shadow
// culling and foliage raster paths retain no diagnostic atomics.
#if NJULF_GPU_DIAGNOSTIC_COUNTERS
const uint DIRECTIONAL_SHADOW_CASTER_CLASS_STATIC = 1u;
const uint DIRECTIONAL_SHADOW_CASTER_CLASS_DYNAMIC = 2u;
const uint DIRECTIONAL_SHADOW_CASTER_CLASS_FOLIAGE = 3u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_INPUT = 1u << 0u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_COMPACTION = 1u << 1u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_STATIC = 1u << 2u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_DYNAMIC = 1u << 3u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_FOLIAGE = 1u << 4u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_CONSERVATIVE_LOD = 1u << 5u;
const uint DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_CULL_ACCEPTED = 1u << 6u;

bool ShouldCaptureDirectionalShadowCandidate(
    uint objectId,
    uint instanceId,
    uint meshletId,
    uint candidateIndex,
    uint cascadeIndex,
    uint casterClass)
{
    // Keep the attribution channel deterministic and bounded. The first two
    // candidates make small reproductions immediately inspectable; the hash
    // samples the remaining population without depending on execution order.
    if (candidateIndex < 2u)
        return true;

    uint hash = candidateIndex * 0x9e3779b9u;
    hash ^= objectId * 0x85ebca6bu;
    hash ^= instanceId * 0xc2b2ae35u;
    hash ^= meshletId * 0x27d4eb2du;
    hash ^= cascadeIndex * 0x165667b1u;
    hash ^= casterClass * 0x68bc21ebu;
    hash ^= hash >> 16u;
    hash *= 0x7feb352du;
    hash ^= hash >> 15u;
    return (hash & 0x3fu) == 0u;
}

uint HashDirectionalShadowMatrix(mat4 matrix)
{
    uint hash = 2166136261u;
    for (uint packedRow = 0u; packedRow < 4u; packedRow++)
    {
        for (uint packedColumn = 0u; packedColumn < 4u; packedColumn++)
        {
            uint bits = floatBitsToUint(matrix[packedRow][packedColumn]);
            hash ^= bits & 0xffu;
            hash *= 16777619u;
            hash ^= (bits >> 8u) & 0xffu;
            hash *= 16777619u;
            hash ^= (bits >> 16u) & 0xffu;
            hash *= 16777619u;
            hash ^= (bits >> 24u) & 0xffu;
            hash *= 16777619u;
        }
    }

    return hash;
}

void RecordDirectionalShadowCasterAttribution(
    uint currentFrameIndex,
    uint objectId,
    uint instanceId,
    uint meshletId,
    uint selectedLod,
    uint casterClass,
    uint cascadeIndex,
    uint candidateIndex,
    uint eligibility,
    vec4 sphere,
    mat4 matrix,
    bool accepted)
{
    if (!ShouldCaptureDirectionalShadowCandidate(
        objectId,
        instanceId,
        meshletId,
        candidateIndex,
        cascadeIndex,
        casterClass))
    {
        return;
    }

    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + currentFrameIndex;
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE + 1u],
        1u);
    uint recordIndex = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE + 0u],
        1u);
    if (recordIndex >= DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_CAPACITY)
    {
        atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
                DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE + 2u],
            1u);
        return;
    }

    vec4 planes[6];
    planes[0] = NormalizePlane(vec4(
        matrix[0][0] + matrix[0][3], matrix[1][0] + matrix[1][3],
        matrix[2][0] + matrix[2][3], matrix[3][0] + matrix[3][3]));
    planes[1] = NormalizePlane(vec4(
        -matrix[0][0] + matrix[0][3], -matrix[1][0] + matrix[1][3],
        -matrix[2][0] + matrix[2][3], -matrix[3][0] + matrix[3][3]));
    planes[2] = NormalizePlane(vec4(
        matrix[0][1] + matrix[0][3], matrix[1][1] + matrix[1][3],
        matrix[2][1] + matrix[2][3], matrix[3][1] + matrix[3][3]));
    planes[3] = NormalizePlane(vec4(
        -matrix[0][1] + matrix[0][3], -matrix[1][1] + matrix[1][3],
        -matrix[2][1] + matrix[2][3], -matrix[3][1] + matrix[3][3]));
    planes[4] = NormalizePlane(vec4(
        matrix[0][2], matrix[1][2], matrix[2][2], matrix[3][2]));
    planes[5] = NormalizePlane(vec4(
        -matrix[0][2] + matrix[0][3], -matrix[1][2] + matrix[1][3],
        -matrix[2][2] + matrix[2][3], -matrix[3][2] + matrix[3][3]));

    float signedDistances[6];
    float minimumDistance = 3.402823466e+38;
    uint firstRejectingPlane = 0xffffffffu;
    float firstRejectingDistance = 0.0;
    for (uint plane = 0u; plane < 6u; plane++)
    {
        float distance = dot(planes[plane].xyz, sphere.xyz) + planes[plane].w;
        signedDistances[plane] = distance;
        minimumDistance = min(minimumDistance, distance);
        if (firstRejectingPlane == 0xffffffffu && distance < -sphere.w)
        {
            firstRejectingPlane = plane;
            firstRejectingDistance = distance;
        }
    }

    if (accepted)
        eligibility |= DIRECTIONAL_SHADOW_CASTER_ELIGIBILITY_CULL_ACCEPTED;

    vec4 clipCenter = MulRowMajor(vec4(sphere.xyz, 1.0), matrix);
    uint word = DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE +
        DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_HEADER_WORD_COUNT +
        recordIndex * DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_STRIDE;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 0u] = objectId;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 1u] = instanceId;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 2u] = meshletId;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 3u] = selectedLod;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 4u] = casterClass;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 5u] = cascadeIndex;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 6u] = candidateIndex;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 7u] = eligibility;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 8u] = HashDirectionalShadowMatrix(matrix);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 9u] = 0u;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 10u] = accepted ? 1u : 0u;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 11u] = firstRejectingPlane;
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 12u] = floatBitsToUint(
        firstRejectingPlane == 0xffffffffu ? minimumDistance : firstRejectingDistance);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 13u] = floatBitsToUint(sphere.x);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 14u] = floatBitsToUint(sphere.y);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 15u] = floatBitsToUint(sphere.z);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 16u] = floatBitsToUint(sphere.w);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 17u] = floatBitsToUint(clipCenter.x);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 18u] = floatBitsToUint(clipCenter.y);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 19u] = floatBitsToUint(clipCenter.z);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 20u] = floatBitsToUint(clipCenter.w);
    for (uint plane = 0u; plane < 6u; plane++)
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 21u + plane] =
            floatBitsToUint(signedDistances[plane]);
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[word + 27u] = 0u;
}
#endif

#endif
