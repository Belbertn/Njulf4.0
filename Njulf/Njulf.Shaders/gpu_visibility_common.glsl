#ifndef NJULF_GPU_VISIBILITY_COMMON_GLSL
#define NJULF_GPU_VISIBILITY_COMMON_GLSL

const uint VIS_COUNTER_INPUT_OBJECT_COUNT = 0u;
const uint VIS_COUNTER_FRUSTUM_CULLED_OBJECT_COUNT = 1u;
const uint VIS_COUNTER_OCCLUSION_TESTED_OBJECT_COUNT = 2u;
const uint VIS_COUNTER_OCCLUSION_REJECTED_OBJECT_COUNT = 3u;
const uint VIS_COUNTER_OPAQUE_MESHLET_COUNT = 4u;
const uint VIS_COUNTER_MASKED_MESHLET_COUNT = 5u;
const uint VIS_COUNTER_TRANSPARENT_MESHLET_COUNT = 6u;
const uint VIS_COUNTER_SHADOW_MESHLET_COUNT = 7u;
const uint VIS_COUNTER_OVERFLOW_FLAGS = 8u;
const uint VIS_COUNTER_REQUIRED_OPAQUE_CAPACITY = 9u;
const uint VIS_COUNTER_REQUIRED_MASKED_CAPACITY = 10u;
const uint VIS_COUNTER_REQUIRED_TRANSPARENT_CAPACITY = 11u;
const uint VIS_COUNTER_REQUIRED_SHADOW_CAPACITY = 12u;
const uint VIS_COUNTER_LOD0_COUNT = 13u;
const uint VIS_COUNTER_LOD1_COUNT = 14u;
const uint VIS_COUNTER_LOD2_COUNT = 15u;
const uint VIS_COUNTER_SOLID_DEPTH_MESHLET_COUNT = 16u;
const uint VIS_COUNTER_DIRECTIONAL_SHADOW_MESHLET_COUNT = 17u;
const uint VIS_COUNTER_LOCAL_SHADOW_MESHLET_COUNT = 18u;
const uint VIS_COUNTER_REQUIRED_SOLID_DEPTH_CAPACITY = 19u;
const uint VIS_COUNTER_REQUIRED_DIRECTIONAL_SHADOW_CAPACITY = 20u;
const uint VIS_COUNTER_REQUIRED_LOCAL_SHADOW_CAPACITY = 21u;
const uint VIS_COUNTER_OCCLUSION_ACCEPTED_OBJECT_COUNT = 22u;
const uint VIS_COUNTER_OCCLUSION_SKIPPED_OBJECT_COUNT = 23u;

const uint OVERFLOW_OPAQUE = 1u << 0;
const uint OVERFLOW_MASKED = 1u << 1;
const uint OVERFLOW_TRANSPARENT = 1u << 2;
const uint OVERFLOW_SHADOW = 1u << 3;

const uint RENDER_CLASS_SOLID = 0u;
const uint RENDER_CLASS_MASKED = 1u;
const uint RENDER_CLASS_TRANSPARENT = 2u;

const uint DIRECTIONAL_SHADOW_LIST_COUNT = 4u;
const uint MAX_SPOT_SHADOW_LISTS = 32u;
const uint MAX_POINT_SHADOW_LISTS = 4u;
const uint POINT_SHADOW_FACE_COUNT = 6u;
const uint MAX_LOCAL_SHADOW_LISTS = MAX_SPOT_SHADOW_LISTS + MAX_POINT_SHADOW_LISTS * POINT_SHADOW_FACE_COUNT;
const uint INDIRECT_LIST_COUNT = 4u + DIRECTIONAL_SHADOW_LIST_COUNT + MAX_LOCAL_SHADOW_LISTS;
const uint PER_LIST_COUNTER_WORD_OFFSET = uint(SIZEOF_GPU_VISIBILITY_COUNTERS / 4);
const uint INDIRECT_WORD_OFFSET = PER_LIST_COUNTER_WORD_OFFSET + DIRECTIONAL_SHADOW_LIST_COUNT + MAX_LOCAL_SHADOW_LISTS;

const uint INDIRECT_LIST_SOLID_DEPTH = 0u;
const uint INDIRECT_LIST_MASKED_DEPTH = 1u;
const uint INDIRECT_LIST_OPAQUE = 2u;
const uint INDIRECT_LIST_TRANSPARENT = 3u;
const uint INDIRECT_LIST_DIRECTIONAL_SHADOW_BASE = 4u;
const uint INDIRECT_LIST_LOCAL_SHADOW_BASE = INDIRECT_LIST_DIRECTIONAL_SHADOW_BASE + DIRECTIONAL_SHADOW_LIST_COUNT;

uint VisibilityFrameIndex()
{
    return uint(pc.Push.CameraPositionAndFrameIndex.w);
}

uint CounterBufferIndex()
{
    return uint(GPU_VISIBILITY_COUNTER_BUFFER_BASE_INDEX) + VisibilityFrameIndex();
}

uint AtomicAddCounter(uint counterIndex, uint value)
{
    return atomicAdd(BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[counterIndex], value);
}

void AtomicOrCounter(uint counterIndex, uint value)
{
    atomicOr(BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[counterIndex], value);
}

uint AtomicAddRawCounter(uint wordOffset, uint value)
{
    return atomicAdd(BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[wordOffset], value);
}

uint ReadRawCounter(uint wordOffset)
{
    return BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[wordOffset];
}

void WriteRawCounter(uint wordOffset, uint value)
{
    BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[wordOffset] = value;
}

void WriteIndirectCommand(uint listIndex, uint groupCountX, uint capacity)
{
    uint baseWord = INDIRECT_WORD_OFFSET + listIndex * uint(SIZEOF_GPU_MESH_TASK_INDIRECT_COMMAND / 4);
    BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[baseWord + 0u] = min(groupCountX, capacity);
    BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[baseWord + 1u] = 1u;
    BindlessStorageBuffers[nonuniformEXT(CounterBufferIndex())].Words[baseWord + 2u] = 1u;
}

void SelectMeshletRange(GPUMeshInfo meshInfo, vec3 worldCenter, float worldRadius, out uint offset, out uint count, out uint lod)
{
    float distanceToCamera = length(worldCenter - pc.Push.CameraPositionAndFrameIndex.xyz);
    float ratio = distanceToCamera / max(worldRadius, 0.001);
    lod = ratio >= 32.0 ? 2u : ratio >= 12.0 ? 1u : 0u;

    if (lod == 2u && meshInfo.MeshletLod2Count > 0u)
    {
        offset = meshInfo.MeshletLod2Offset;
        count = meshInfo.MeshletLod2Count;
        return;
    }

    if (lod >= 1u && meshInfo.MeshletLod1Count > 0u)
    {
        offset = meshInfo.MeshletLod1Offset;
        count = meshInfo.MeshletLod1Count;
        lod = 1u;
        return;
    }

    offset = meshInfo.MeshletOffset;
    count = meshInfo.MeshletCount;
    lod = 0u;
}

#endif
