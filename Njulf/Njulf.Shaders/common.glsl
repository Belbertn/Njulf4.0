// =========================================================================
// Njulf Rendering - Common GLSL Contract
// =========================================================================
// This file is the shader-side mirror of:
// - Njulf.Rendering.RenderingConstants
// - Njulf.Rendering.Descriptors.BindlessIndex
// - Njulf.Rendering.Data.GPUStructs
//
// BINDLESS RESOURCE CONTRACT:
// - Storage buffers use set = BINDLESS_STORAGE_SET, binding = BINDLESS_STORAGE_BINDING.
// - BindlessIndex values are descriptor array elements, not descriptor binding numbers.
// - Textures use set = BINDLESS_TEXTURE_SET, binding = BINDLESS_TEXTURE_BINDING.
// =========================================================================

#ifndef NJULF_COMMON_GLSL
#define NJULF_COMMON_GLSL

#extension GL_EXT_nonuniform_qualifier : enable

uint NextSimpleDdgiPhysicalGeneration(uint generation)
{
    uint next = (generation + 1u) & 0x00ffffffu;
    return next == 0u ? 1u : next;
}

// ============================================
// FRAME CONFIGURATION
// ============================================

const int FRAMES_IN_FLIGHT = 2;
const uint FORWARD_CLUSTER_TILE_SIZE = 16u;
const uint FORWARD_CLUSTER_DEPTH_SLICE_COUNT = 24u;
const uint FORWARD_CLUSTER_MAX_LIGHTS = 64u;
const float FORWARD_CLUSTER_NEAR_PLANE = 0.1;
const float FORWARD_CLUSTER_FAR_PLANE = 1000.0;
const int FORWARD_REFLECTION_PROBE_CANDIDATE_LIMIT = 32;

// ============================================
// DESCRIPTOR SET CONTRACT
// ============================================

const int BINDLESS_STORAGE_SET = 0;
const int BINDLESS_STORAGE_BINDING = 0;
const int BINDLESS_TEXTURE_SET = 1;
const int BINDLESS_TEXTURE_BINDING = 0;

// ============================================
// BINDLESS STORAGE BUFFER DESCRIPTOR INDICES
// These values are descriptor array elements in set 0, binding 0.
// ============================================

const int OBJECT_DATA_BUFFER_INDEX = 0;
const int MATERIAL_DATA_BUFFER_INDEX = 1;
const int SCENE_MESH_METADATA_BUFFER_INDEX = 2;
const int VERTEX_BUFFER_INDEX = 3;
const int INDEX_BUFFER_INDEX = 4;
const int MESHLET_BUFFER_INDEX = 5;
const int MESHLET_VERTEX_INDEX_BUFFER_INDEX = 6;
const int MESHLET_TRIANGLE_INDEX_BUFFER_INDEX = 7;
const int INSTANCE_BUFFER_BASE_INDEX = 8;
const int INSTANCE_BUFFER_FRAME1_INDEX = 9;
const int MESHLET_DRAW_BUFFER_BASE_INDEX = 10;
const int MESHLET_DRAW_BUFFER_FRAME1_INDEX = 11;
const int TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX = 12;
const int TRANSPARENT_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 13;
const uint FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_BITS = 10u;
const uint FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_MASK =
    (1u << FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_BITS) - 1u;

uint ForwardDrawBufferBaseIndex(uint packedDrawBufferBaseIndex)
{
    return packedDrawBufferBaseIndex &
        FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_MASK;
}

uint ForwardFirstDraw(uint packedDrawBufferBaseIndex)
{
    return packedDrawBufferBaseIndex >>
        FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_BITS;
}
const int LIGHT_BUFFER_INDEX = 14;
const int TILED_LIGHT_HEADER_BUFFER_INDEX = 15;
const int TILED_LIGHT_INDICES_BUFFER_INDEX = 16;
const int RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX = 17;
const int RENDERER_DIAGNOSTICS_BUFFER_FRAME1_INDEX = 18;
const int DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX = 19;
const int DIRECTIONAL_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 20;
const int DIRECTIONAL_SHADOW_MESHLET_DRAW_BUFFER_COUNT = 2;
const int SPOT_SHADOW_DATA_BUFFER_INDEX = 22;
const int POINT_SHADOW_DATA_BUFFER_INDEX = 23;
const int LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX = 24;
const int LOCAL_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 25;
const int LOCAL_SHADOW_MESHLET_DRAW_BUFFER_COUNT = 2;
const int ENVIRONMENT_DATA_BUFFER_INDEX = 27;
const int REFLECTION_PROBE_BUFFER_INDEX = 28;
const int SOLID_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX = 29;
const int SOLID_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 30;
const int MASKED_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX = 31;
const int MASKED_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 32;
const int SKINNING_VERTEX_DATA_BUFFER_INDEX = 33;
const int SKIN_MATRIX_BUFFER_BASE_INDEX = 34;
const int SKIN_MATRIX_BUFFER_FRAME1_INDEX = 35;
const int SKINNED_VERTEX_BUFFER_BASE_INDEX = 36;
const int SKINNED_VERTEX_BUFFER_FRAME1_INDEX = 37;
const int SKINNING_DISPATCH_BUFFER_BASE_INDEX = 38;
const int SKINNING_DISPATCH_BUFFER_FRAME1_INDEX = 39;
const int PARTICLE_INSTANCE_BUFFER_BASE_INDEX = 40;
const int PARTICLE_INSTANCE_BUFFER_FRAME1_INDEX = 41;
const int PARTICLE_BATCH_BUFFER_BASE_INDEX = 42;
const int PARTICLE_BATCH_BUFFER_FRAME1_INDEX = 43;
const int MATERIAL_EXTENSION_DATA_BUFFER_INDEX = 44;
const int AUTO_EXPOSURE_HISTOGRAM_BUFFER_BASE_INDEX = 45;
const int AUTO_EXPOSURE_HISTOGRAM_BUFFER_FRAME1_INDEX = 46;
const int AUTO_EXPOSURE_STATE_BUFFER_BASE_INDEX = 47;
const int AUTO_EXPOSURE_STATE_BUFFER_FRAME1_INDEX = 48;
const int PACKED_MESHLET_DRAW_BUFFER_BASE_INDEX = 49;
const int PACKED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 50;
const int PACKED_SOLID_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX = 51;
const int PACKED_SOLID_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 52;
const int PACKED_MASKED_DEPTH_MESHLET_DRAW_BUFFER_BASE_INDEX = 53;
const int PACKED_MASKED_DEPTH_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 54;
const int MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX = 55;
const int MESHLET_TASK_FRAME_DATA_BUFFER_FRAME1_INDEX = 56;
const int FULL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 57;
const int FULL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 58;
const int PACKED_FULL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 59;
const int PACKED_FULL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 60;
const int SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 61;
const int SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 62;
const int PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 63;
const int PACKED_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 64;
const int VERTEX_POSITION_BUFFER_INDEX = 65;
const int VERTEX_NORMAL_TANGENT_BUFFER_INDEX = 66;
const int VERTEX_UV_COLOR_BUFFER_INDEX = 67;
const int DIRECTIONAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 68;
const int DIRECTIONAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 69;
const int DIRECTIONAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 70;
const int DIRECTIONAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 71;
const int LOCAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 72;
const int LOCAL_STATIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 73;
const int LOCAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_BASE_INDEX = 74;
const int LOCAL_DYNAMIC_SHADOW_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 75;
const int PARTICLE_FRAME_DATA_BUFFER_BASE_INDEX = 76;
const int PARTICLE_FRAME_DATA_BUFFER_FRAME1_INDEX = 77;
const int GPU_PARTICLE_STATE_BUFFER_BASE_INDEX = 78;
const int GPU_PARTICLE_STATE_BUFFER_FRAME1_INDEX = 79;
const int GPU_PARTICLE_ALIVE_INDEX_BUFFER_BASE_INDEX = 80;
const int GPU_PARTICLE_ALIVE_INDEX_BUFFER_FRAME1_INDEX = 81;
const int GPU_PARTICLE_DEAD_INDEX_BUFFER_INDEX = 82;
const int GPU_PARTICLE_EMITTER_BUFFER_BASE_INDEX = 83;
const int GPU_PARTICLE_EMITTER_BUFFER_FRAME1_INDEX = 84;
const int GPU_PARTICLE_COUNTER_BUFFER_BASE_INDEX = 85;
const int GPU_PARTICLE_COUNTER_BUFFER_FRAME1_INDEX = 86;
const int GPU_PARTICLE_RENDER_INSTANCE_BUFFER_BASE_INDEX = 87;
const int GPU_PARTICLE_RENDER_INSTANCE_BUFFER_FRAME1_INDEX = 88;
const int GPU_PARTICLE_INDIRECT_DRAW_BUFFER_BASE_INDEX = 89;
const int GPU_PARTICLE_INDIRECT_DRAW_BUFFER_FRAME1_INDEX = 90;
const int GPU_PARTICLE_CURVE_SAMPLE_BUFFER_BASE_INDEX = 91;
const int GPU_PARTICLE_CURVE_SAMPLE_BUFFER_FRAME1_INDEX = 92;
const int GPU_PARTICLE_UNSORTED_RENDER_INSTANCE_BUFFER_BASE_INDEX = 93;
const int GPU_PARTICLE_UNSORTED_RENDER_INSTANCE_BUFFER_FRAME1_INDEX = 94;
const int GPU_PARTICLE_SORT_KEY_BUFFER_BASE_INDEX = 95;
const int GPU_PARTICLE_SORT_KEY_BUFFER_FRAME1_INDEX = 96;
const int FOLIAGE_PROTOTYPE_BUFFER_INDEX = 97;
const int FOLIAGE_PATCH_BUFFER_INDEX = 98;
const int FOLIAGE_CLUSTER_BUFFER_INDEX = 99;
const int FOLIAGE_INSTANCE_BUFFER_BASE_INDEX = 100;
const int FOLIAGE_INSTANCE_BUFFER_FRAME1_INDEX = 101;
const int FOLIAGE_VISIBLE_CLUSTER_BUFFER_BASE_INDEX = 102;
const int FOLIAGE_VISIBLE_CLUSTER_BUFFER_FRAME1_INDEX = 103;
const int FOLIAGE_MESHLET_DRAW_BUFFER_BASE_INDEX = 104;
const int FOLIAGE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 105;
const int FOLIAGE_COUNTER_BUFFER_BASE_INDEX = 106;
const int FOLIAGE_COUNTER_BUFFER_FRAME1_INDEX = 107;
const int FOLIAGE_INDIRECT_DISPATCH_BUFFER_BASE_INDEX = 108;
const int FOLIAGE_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX = 109;
const int SCENE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 110;
const int SCENE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 111;
const int SCENE_SIMPLE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 112;
const int SCENE_SIMPLE_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 113;
const int SCENE_SIMPLE_NORMAL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 114;
const int SCENE_SIMPLE_NORMAL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 115;
const int SCENE_FULL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 116;
const int SCENE_FULL_OPAQUE_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 117;
const int SCENE_SUBMISSION_COUNTER_BUFFER_BASE_INDEX = 118;
const int SCENE_SUBMISSION_COUNTER_BUFFER_FRAME1_INDEX = 119;
const int SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_BASE_INDEX = 120;
const int SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX = 121;
const int SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 122;
const int SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 123;
const int SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 124;
const int SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 125;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX = 126;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX = 127;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX = 128;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX = 129;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX = 130;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX = 131;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX = 132;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX = 133;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX = 134;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX = 135;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX = 136;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX = 137;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX = 138;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX = 139;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX = 140;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX = 141;
const int FORWARD_VISIBLE_SIMPLE_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 142;
const int FORWARD_VISIBLE_SIMPLE_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 143;
const int FORWARD_VISIBLE_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 144;
const int FORWARD_VISIBLE_SIMPLE_NORMAL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 145;
const int FORWARD_VISIBLE_FULL_OPAQUE_MESHLET_DRAW_BUFFER_BASE_INDEX = 146;
const int FORWARD_VISIBLE_FULL_OPAQUE_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 147;
const int FORWARD_VISIBILITY_COUNTER_BUFFER_BASE_INDEX = 148;
const int FORWARD_VISIBILITY_COUNTER_BUFFER_FRAME1_INDEX = 149;
const int FORWARD_VISIBILITY_INDIRECT_DISPATCH_BUFFER_BASE_INDEX = 150;
const int FORWARD_VISIBILITY_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX = 151;
const int SIMPLE_DDGI_PARAMS_BUFFER_INDEX = 152;
const int SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX = 153;
const int SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX = 154;
const int SIMPLE_DDGI_RAY_RESULT_SCRATCH_BUFFER_INDEX = 155;
const int SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX = 156;
const int SIMPLE_DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX = 157;
const int SIMPLE_DDGI_RELOCATION_CLASSIFICATION_BUFFER_INDEX = 158;
const int SIMPLE_DDGI_TRANSPORT_IRRADIANCE_ATLAS_BUFFER_INDEX = 159;
const int SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_BUFFER_INDEX = 160;
const int SIMPLE_DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX = 161;
const int SIMPLE_DDGI_EMISSIVE_SOURCE_BUFFER_INDEX = 162;
const int FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX = 163;
const int FAR_FIELD_CLIPMAP_VOXEL_BUFFER_INDEX = 164;
const int FAR_FIELD_CLIPMAP_INSTANCE_BUFFER_INDEX = 165;
const int FAR_FIELD_CLIPMAP_BAKE_VOXEL_BUFFER_INDEX = 166;
const int FAR_FIELD_CLIPMAP_DISTANCE_BUFFER_INDEX = 167;
const int FAR_FIELD_CLIPMAP_JUMP_FLOOD_SCRATCH0_BUFFER_INDEX = 168;
const int FAR_FIELD_CLIPMAP_JUMP_FLOOD_SCRATCH1_BUFFER_INDEX = 169;
const int FAR_FIELD_CLIPMAP_PAGE_TABLE_BUFFER_INDEX = 170;
const int ENVIRONMENT_PREFILTER_DATA_BUFFER_INDEX = 171;
const int ENVIRONMENT_GI_DATA_BUFFER_INDEX = 172;
const int SIMPLE_DDGI_SCHEDULER_ARENA_BUFFER_INDEX = 173;
const int SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX = 174;
const int SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX = 175;
const int SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX = 176;
const int SIMPLE_DDGI_RECEIVER_GATHER_BUFFER_BASE_INDEX = 178;
const int SIMPLE_DDGI_EMISSIVE_SURFACE_BUFFER_INDEX = 180;
const int SIMPLE_DDGI_LIGHT_TREE_NODE_BUFFER_INDEX = 181;
const int SIMPLE_DDGI_LIGHT_TREE_LEAF_BUFFER_INDEX = 182;
const int SIMPLE_DDGI_LIGHT_TREE_STATE_BUFFER_INDEX = 183;
const int SIMPLE_DDGI_LIGHT_TREE_SCRATCH_BUFFER_INDEX = 184;
const int SIMPLE_DDGI_DIRECTIONAL_RADIANCE_BUFFER_INDEX = 185;
const int SIMPLE_DDGI_DIRECTIONAL_RADIANCE_PARITY_BUFFER_INDEX = 186;
const int DDGI_FOLIAGE_PROXY_VERTEX_BUFFER_INDEX = 187;
const int DDGI_FOLIAGE_PROXY_INDEX_BUFFER_INDEX = 188;
const int DDGI_DECAL_CANDIDATE_BUFFER_INDEX = 189;
const int DDGI_FOLIAGE_PROXY_VERTEX_BUFFER_FRAME1_INDEX = 190;
const int DDGI_FOLIAGE_PROXY_INDEX_BUFFER_FRAME1_INDEX = 191;
const int DDGI_FOLIAGE_PROXY_PATCH_BUFFER_INDEX = 192;
const int DDGI_FOLIAGE_PROXY_PATCH_BUFFER_FRAME1_INDEX = 193;
// Append-only advanced-GI slots.  Keep these synchronized with
// BindlessIndexTable; disabled features bind a null buffer and never infer
// availability from the numeric slot alone.
const int SIMPLE_DDGI_RECEIVER_FEEDBACK_RECORDS_BUFFER_INDEX = 194;
const int SIMPLE_DDGI_RECEIVER_FEEDBACK_SORT_SCRATCH_BUFFER_INDEX = 195;
const int SIMPLE_DDGI_RECEIVER_FEEDBACK_SUMMARY_BUFFER_INDEX = 196;
const int OPACITY_MICROMAP_RESIDENT_BUFFER_INDEX = 197;
const int OPACITY_MICROMAP_BUILD_SCRATCH_BUFFER_INDEX = 198;
const int OPACITY_MICROMAP_COMPACTION_BUFFER_INDEX = 199;
const int SIMPLE_DDGI_GUIDING_DISTRIBUTION_BANK0_BUFFER_INDEX = 200;
const int SIMPLE_DDGI_GUIDING_DISTRIBUTION_BANK1_BUFFER_INDEX = 201;
const int SIMPLE_DDGI_GUIDING_TRAINING_SCRATCH_BUFFER_INDEX = 202;
const int SIMPLE_DDGI_GUIDING_DIRECTION_PDF_SIDECAR_BUFFER_INDEX = 203;
const int GI_CAUSTIC_TASK_BUFFER_INDEX = 204;
const int GI_CAUSTIC_PHOTON_BUFFER_INDEX = 205;
const int GI_CAUSTIC_CACHE_BUFFER_INDEX = 206;
const int GI_CAUSTIC_SCRATCH_BUFFER_INDEX = 207;
const int SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_TILE_BUFFER_INDEX = 208;
const int SIMPLE_DDGI_RECEIVER_FEEDBACK_CANDIDATE_BUFFER_INDEX = 209;
const int DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_BASE_INDEX = 210;
const int DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_FRAME1_INDEX = 211;
const int DIRECTIONAL_SHADOW_RAW_BUFFER_BASE_INDEX = 212;
const int DIRECTIONAL_SHADOW_RAW_BUFFER_FRAME1_INDEX = 213;
const int DIRECTIONAL_SHADOW_HISTORY_BUFFER_BASE_INDEX = 214;
const int DIRECTIONAL_SHADOW_HISTORY_BUFFER_FRAME1_INDEX = 215;
const int DIRECTIONAL_SHADOW_SCRATCH_BUFFER_BASE_INDEX = 216;
const int DIRECTIONAL_SHADOW_SCRATCH_BUFFER_FRAME1_INDEX = 217;
const int DIRECTIONAL_SHADOW_DIAGNOSTIC_BUFFER_BASE_INDEX = 218;
const int DIRECTIONAL_SHADOW_DIAGNOSTIC_BUFFER_FRAME1_INDEX = 219;
const int DIRECTIONAL_SHADOW_COUNTER_BUFFER_BASE_INDEX = 220;
const int DIRECTIONAL_SHADOW_COUNTER_BUFFER_FRAME1_INDEX = 221;
const int VOLUMETRIC_FOG_BOUNCE_RADIANCE_BUFFER_INDEX = 222;
const int AREA_RAY_SHADOW_MASK_BUFFER_BASE_INDEX = 223;
const int FORWARD_MATERIAL_DATA_BUFFER_INDEX = 225;
const int SIMPLE_DDGI_RECEIVER_GATHER_SURFACE_BUFFER_BASE_INDEX = 226;
const int SIMPLE_DDGI_RECEIVER_GATHER_SURFACE_BUFFER_FRAME1_INDEX = 227;
const int SCENE_GPU_LOD_HISTORY_BUFFER_BASE_INDEX = 228;
const int SCENE_GPU_LOD_HISTORY_BUFFER_FRAME1_INDEX = 229;
// Slots 230..741 are the frame-partitioned arena for 128 DDGI dynamic-geometry
// submissions. Scene instance candidates are append-only so all published
// descriptors remain numerically stable.
const int SCENE_INSTANCE_CANDIDATE_BUFFER_BASE_INDEX = 742;
const int SCENE_INSTANCE_CANDIDATE_BUFFER_FRAME1_INDEX = 743;
const int MESHLET_PHYSICAL_PAGE_TABLE_BUFFER_BASE_INDEX = 744;
const int MESHLET_PHYSICAL_PAGE_TABLE_BUFFER_FRAME1_INDEX = 745;
const int MESHLET_STREAMING_RANGE_BUFFER_INDEX = 746;
const int MESHLET_STREAMING_RANGE_STATE_BUFFER_BASE_INDEX = 747;
const int MESHLET_STREAMING_RANGE_STATE_BUFFER_FRAME1_INDEX = 748;
const int MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX = 749;
const int MESHLET_STREAMING_DEMAND_BUFFER_BASE_INDEX = 750;
const int MESHLET_STREAMING_DEMAND_BUFFER_FRAME1_INDEX = 751;
const int MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_BASE_INDEX = 752;
const int MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_FRAME1_INDEX = 753;
const int MESHLET_PHYSICAL_PAGE_BANK_BUFFER_BASE_INDEX = 754;
const int MESHLET_PHYSICAL_PAGE_BANK_BUFFER_COUNT = 16;
const int FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX = 770;
const int AUTOMATIC_PLANAR_REFLECTION_BUFFER_INDEX = 771;
const int FOLIAGE_AUTHORED_INSTANCE_COMMAND_BUFFER_BASE_INDEX = 772;
const int FOLIAGE_AUTHORED_INSTANCE_COMMAND_BUFFER_FRAME1_INDEX = 773;
const int FOLIAGE_IMPOSTOR_VIEW_BUFFER_INDEX = 774;
const int STATIC_BUFFER_COUNT = 775;
const uint GPU_PARTICLE_BLEND_BUCKET_COUNT = 5u;

const uint MESHLET_DRAW_FLAG_NEEDS_GPU_FRUSTUM_TEST = 1u << 0;
const uint MESHLET_DRAW_FLAG_CPU_FRUSTUM_VISIBLE = 1u << 1;
const uint MESHLET_DRAW_FLAG_OBJECT_FULLY_INSIDE_FRUSTUM = 1u << 2;
const uint MESHLET_DRAW_FLAG_MATERIAL_MASKED = 1u << 3;
const uint MESHLET_DRAW_FLAG_MATERIAL_BLEND = 1u << 4;
const uint MESHLET_DRAW_FLAG_CAN_HIZ_TEST = 1u << 5;
const uint MESHLET_DRAW_FLAG_MATERIAL_DOUBLE_SIDED = 1u << 6;
const uint MESHLET_DRAW_FLAG_NORMAL_CONE_CULL_ELIGIBLE = 1u << 7;
const uint MESHLET_COMMAND_FLAG_MATERIAL_DOUBLE_SIDED = 1u << 0;
const uint MESHLET_COMMAND_FLAG_NORMAL_CONE_CULL_ELIGIBLE = 1u << 1;
const uint MESHLET_COMMAND_FLAG_LOD_DITHER_TRANSITION = 1u << 2;
const uint MESHLET_COMMAND_FLAG_LOD_DITHER_TARGET = 1u << 3;
const uint MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_SHIFT = 4u;
const uint MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_MASK = 0x0fu <<
    MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_SHIFT;
const uint FOLIAGE_COVERAGE_LOD_MASK = 0x3u;
const uint FOLIAGE_COVERAGE_TRANSITION_SHIFT = 8u;
const uint FOLIAGE_COVERAGE_TRANSITION_MASK = 0xffu <<
    FOLIAGE_COVERAGE_TRANSITION_SHIFT;
const uint FOLIAGE_COVERAGE_TRANSITION_TARGET = 1u << 16u;
const uint FOLIAGE_COVERAGE_TRANSITION_ACTIVE = 1u << 17u;

uint PackFoliageCoverageState(
    uint lod,
    float transitionFraction,
    bool transitionActive,
    bool transitionTarget)
{
    uint quantizedTransition = uint(round(
        clamp(transitionFraction, 0.0, 1.0) * 255.0));
    return (lod & FOLIAGE_COVERAGE_LOD_MASK) |
        (quantizedTransition << FOLIAGE_COVERAGE_TRANSITION_SHIFT) |
        (transitionActive ? FOLIAGE_COVERAGE_TRANSITION_ACTIVE : 0u) |
        (transitionTarget ? FOLIAGE_COVERAGE_TRANSITION_TARGET : 0u);
}

uint PackFoliageCoverageStateFromCommand(
    uint lod,
    uint commandFlags)
{
    bool transitionEnabled = (commandFlags &
        MESHLET_COMMAND_FLAG_LOD_DITHER_TRANSITION) != 0u;
    bool target = (commandFlags &
        MESHLET_COMMAND_FLAG_LOD_DITHER_TARGET) != 0u;
    uint threshold = (commandFlags &
        MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_MASK) >>
        MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_SHIFT;
    return PackFoliageCoverageState(
        lod,
        float(threshold) / 15.0,
        transitionEnabled,
        target);
}

bool MeshletLodTransitionTriangleVisible(
    uint commandFlags,
    uint instanceId,
    uint meshletIndex,
    uint triangleIndex)
{
    if ((commandFlags & MESHLET_COMMAND_FLAG_LOD_DITHER_TRANSITION) == 0u)
        return true;

    uint value = instanceId * 0x9e3779b9u;
    value ^= meshletIndex * 0x85ebca6bu;
    value ^= triangleIndex * 0xc2b2ae35u;
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    uint hashSample = value & 0x0fu;
    uint threshold = (commandFlags &
        MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_MASK) >>
        MESHLET_COMMAND_FLAG_LOD_DITHER_THRESHOLD_SHIFT;
    bool target = (commandFlags &
        MESHLET_COMMAND_FLAG_LOD_DITHER_TARGET) != 0u;
    return target ? hashSample <= threshold : hashSample > threshold;
}

const uint FOLIAGE_PROTOTYPE_FLAG_CAST_SHADOWS = 1u << 0;
const uint FOLIAGE_PROTOTYPE_FLAG_FAR_IMPOSTOR = 1u << 1;
const uint GPU_LIGHT_SHADOW_FLAG_CASTS_SHADOWS = 1u << 0;
const uint GPU_LIGHT_AREA_FLAG_TWO_SIDED = 1u << 0;
const int GPU_LIGHT_TYPE_POINT = 0;
const int GPU_LIGHT_TYPE_DIRECTIONAL = 1;
const int GPU_LIGHT_TYPE_SPOT = 2;
const int GPU_LIGHT_TYPE_RECTANGLE = 3;
const int GPU_LIGHT_TYPE_DISK = 4;
const int GPU_LIGHT_TYPE_TUBE = 5;
const uint GPU_LIGHT_ATTENUATION_MODE_SHIFT = 8u;
const uint GPU_LIGHT_ATTENUATION_MODE_MASK = 3u << GPU_LIGHT_ATTENUATION_MODE_SHIFT;
const uint GPU_LIGHT_ATTENUATION_LEGACY_WINDOWED = 0u;
const uint GPU_LIGHT_ATTENUATION_INVERSE_SQUARE = 1u;
const uint GPU_LIGHT_ATTENUATION_POLYNOMIAL = 2u;

const uint HIZ_TEST_MODE_OFF = 0u;
const uint HIZ_TEST_MODE_BOUNDS_4_TAP = 1u;
const uint HIZ_TEST_MODE_FULL_6_POINT_5_TAP = 2u;

// ============================================
// BINDLESS TEXTURE DESCRIPTOR INDICES
// These values are descriptor array elements in set 1, binding 0.
// ============================================

const int FIRST_TEXTURE_INDEX = 0;
const int MAX_TEXTURES = 65536;
const int DEFAULT_WHITE_TEXTURE = 0;
const int DEFAULT_NORMAL_TEXTURE = 1;
const int DEFAULT_BLACK_TEXTURE = 2;
const int DEPTH_TEXTURE_INDEX = 3;
const int HIZ_DEPTH_TEXTURE_INDEX = 4;
const int HDR_SCENE_COLOR_TEXTURE_INDEX = 5;
const int BLOOM_MIP_TEXTURE_BASE = 6;
const int MAX_BLOOM_MIP_TEXTURES = 8;
const int DIRECTIONAL_SHADOW_TEXTURE_BASE = 14;
const int MAX_DIRECTIONAL_SHADOW_TEXTURES = 4;
const int SPOT_SHADOW_ATLAS_TEXTURE_INDEX = 18;
const int POINT_SHADOW_CUBEMAP_ARRAY_TEXTURE_INDEX = 19;
const int ENVIRONMENT_CUBEMAP_TEXTURE_INDEX = 20;
const int IRRADIANCE_CUBEMAP_TEXTURE_INDEX = 21;
const int PREFILTERED_ENVIRONMENT_TEXTURE_INDEX = 22;
const int BRDF_LUT_TEXTURE_INDEX = 23;
const int AMBIENT_OCCLUSION_RAW_TEXTURE_INDEX = 24;
const int AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX = 25;
const int LDR_SCENE_COLOR_TEXTURE_INDEX = 26;
const int SMAA_EDGES_TEXTURE_INDEX = 27;
const int SMAA_BLEND_WEIGHTS_TEXTURE_INDEX = 28;
const int SMAA_AREA_TEXTURE_INDEX = 29;
const int SMAA_SEARCH_TEXTURE_INDEX = 30;
const int MOTION_VECTOR_TEXTURE_INDEX = 31;
const int TAA_HISTORY_TEXTURE_INDEX = 32;
const int FOGGED_SCENE_COLOR_TEXTURE_INDEX = 33;
const int REFLECTION_PROBE_CUBEMAP_ARRAY_TEXTURE_INDEX = 34;
const int REFLECTION_PROBE_DEBUG_TEXTURE_INDEX = 35;
const int WEIGHTED_OIT_ACCUMULATION_TEXTURE_INDEX = 36;
const int WEIGHTED_OIT_REVEALAGE_TEXTURE_INDEX = 37;
const int MATERIAL_TRANSPORT_PROVENANCE_TEXTURE_INDEX = 38;
const int PREFILTERED_ENVIRONMENT_NEXT_TEXTURE_INDEX = 39;
const int AREA_LIGHT_LTC_MATRIX_TEXTURE_INDEX = 40;
const int AREA_LIGHT_LTC_AMPLITUDE_TEXTURE_INDEX = 41;
// Optional sampled-image Simple-DDGI atlas migration. The two bounded ranges
// stay fixed so the runtime can use device-safe 2D-array groups without
// consuming dynamically allocated material texture slots.
const int SIMPLE_DDGI_SAMPLED_ATLAS_TEXTURE_GROUP_COUNT = 128;
const int SIMPLE_DDGI_SAMPLED_IRRADIANCE_TEXTURE_BASE_INDEX = 42;
const int SIMPLE_DDGI_SAMPLED_VISIBILITY_TEXTURE_BASE_INDEX = 170;
const int OPAQUE_SCENE_COLOR_SNAPSHOT_TEXTURE_INDEX = 298;
const int GTAO_FILTERED_TEXTURE_INDEX = 299;
const int GTAO_DEBUG_TEXTURE_INDEX = 300;
const int FIRST_DYNAMIC_TEXTURE_INDEX = 301;

// ============================================
// GPU STRUCT DEFINITIONS
// These MUST match C# structs in GPUStructs.cs exactly.
// ============================================

struct GPUVertex
{
    vec3 Position;
    float Padding0;
    vec3 Normal;
    float Padding1;
    vec2 TexCoord;
    vec2 TexCoord2;
    vec4 Tangent;
    vec4 Color;
};

struct GPUVertexPositionStream
{
    vec4 Position;
};

struct GPUVertexNormalTangentStream
{
    vec4 Normal;
    vec4 Tangent;
};

struct GPUVertexUvColorStream
{
    vec2 TexCoord;
    vec2 TexCoord2;
    vec4 Color;
};

struct GPUVertexPositionTexCoords
{
    vec3 Position;
    vec2 TexCoord;
    vec2 TexCoord2;
};

struct GPUVertexSimple
{
    vec3 Position;
    vec3 Normal;
    vec2 TexCoord;
};

struct GPUMeshInfo
{
    vec4 BoundingSphere;
    uint SkinningDataOffset;
    uint SkinningDataCount;
    uint Flags;
    uint MeshletOffset;
    uint MeshletCount;
    uint MeshletLod1Offset;
    uint MeshletLod1Count;
    uint MeshletLod2Offset;
    uint MeshletLod2Count;
    uint MeshletLodGeneratedCount;
    uint MeshletLod1ErrorBits;
    uint MeshletLod2ErrorBits;
    uint GpuMeshletRecordCount;
    uint HierarchyNodeOffset;
    uint HierarchyNodeCount;
    uint HierarchyRootNode;
    uint StreamingRangeIndex;
    uint ResidencyFlags;
};

struct GPUVertexSkinningData
{
    uint Joint0;
    uint Joint1;
    uint Joint2;
    uint Joint3;
    float Weight0;
    float Weight1;
    float Weight2;
    float Weight3;
};

struct GPUSkinningDispatch
{
    uint SourceVertexOffset;
    uint SourceSkinningDataOffset;
    uint DestinationVertexOffset;
    uint VertexCount;
    uint SkinMatrixOffset;
    uint ObjectIndex;
    uint SourceMeshMetadataIndex;
    uint Flags;
};

struct GPUSkinningPushConstants
{
    uint DispatchIndex;
    uint CurrentFrameIndex;
    uint Padding0;
    uint Padding1;
};

struct GPUParticleInstance
{
    vec4 PositionSize;
    vec4 VelocityRotation;
    vec4 Color;
    vec4 EmissiveLifetimeSoftClip;
    uint TextureIndex;
    uint FlipbookFrame;
    uint FlipbookColumns;
    uint FlipbookRows;
    uint BlendMode;
    uint BillboardMode;
    uint DebugId;
    uint Padding0;
    vec4 VolumetricAlbedoAndExtinction;
    vec4 VolumetricRadiusAnisotropyAndFlags;
};

struct GPUParticleBatch
{
    uint Start;
    uint Count;
    uint BlendMode;
    uint Padding0;
};

struct GPUParticleFrameData
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewMatrix;
    mat4 InverseProjectionMatrix;
    vec3 CameraPosition;
    float GlobalSoftParticleDistance;
    vec2 ScreenDimensions;
    vec2 Padding0;
};

struct GPUParticlePushConstants
{
    uint CurrentFrameIndex;
    uint ParticleInstanceBufferBaseIndex;
    uint ParticleFrameDataBufferBaseIndex;
    uint DepthTextureIndex;
    uint DebugView;
    uint SoftParticlesEnabled;
    uint InstanceOffset;
    uint Padding0;
};

struct GPUParticleEmitter
{
    mat4 WorldMatrix;
    vec4 SpawnShape0;
    vec4 SpawnShape1;
    vec4 InitialVelocityMin;
    vec4 InitialVelocityMax;
    vec4 AccelerationDrag;
    vec4 LifetimeSize;
    vec4 Color;
    uint MaterialIndex;
    uint MaxParticles;
    uint RandomSeed;
    uint Flags;
    vec4 ColorEnd;
    vec4 EmissiveAngularVelocity;
    vec4 RotationParams;
    vec4 TimingParams;
    vec4 VolumetricAlbedoAndExtinction;
    vec4 VolumetricRadiusAnisotropyAndFlags;
};

struct GPUParticleCurveSample
{
    vec4 Color;
    vec4 Properties;
};

struct GPUParticleState
{
    vec4 PositionAge;
    vec4 VelocityLifetime;
    vec4 Color;
    vec4 SizeRotation;
    uint EmitterIndex;
    uint StableId;
    uint RandomSeed;
    uint Flags;
};

struct GPUParticleCounters
{
    uint AliveCount;
    uint DeadCount;
    uint SpawnedCount;
    uint KilledCount;
    uint CulledCount;
    uint RenderedCount;
    uint DroppedSpawnCount;
    uint BlendBucket0Count;
    uint BlendBucket1Count;
    uint BlendBucket2Count;
    uint BlendBucket3Count;
    uint BlendBucket4Count;
    uint BlendBucket0WriteCount;
    uint BlendBucket1WriteCount;
    uint BlendBucket2WriteCount;
    uint BlendBucket3WriteCount;
    uint BlendBucket4WriteCount;
    uint BlendBucket0Offset;
    uint BlendBucket1Offset;
    uint BlendBucket2Offset;
    uint BlendBucket3Offset;
    uint BlendBucket4Offset;
};

struct GPUParticleDrawCommand
{
    uint VertexCount;
    uint InstanceCount;
    uint FirstVertex;
    uint FirstInstance;
};

struct GPUParticleSortKey
{
    uint Key;
    uint InstanceIndex;
};

struct GPUParticleResetPushConstants
{
    uint CurrentFrameIndex;
    uint ParticleCapacity;
    uint DrawCapacity;
    uint Flags;
    uint Padding0;
    uint Padding1;
    uint Padding2;
    uint Padding3;
};

struct GPUParticleSortPushConstants
{
    uint CurrentFrameIndex;
    uint ParticleCapacity;
    uint Mode;
    uint Bucket;
    uint SortLevel;
    uint SortStage;
    uint Padding0;
    uint Padding1;
};

struct GPUParticleSimulatePushConstants
{
    uint CurrentFrameIndex;
    uint ParticleCapacity;
    uint EmitterCount;
    uint MaxSpawnPerEmitter;
    float DeltaSeconds;
    float TimeSeconds;
    float SoftParticleDistance;
    uint Flags;
    uint Padding0;
    uint Padding1;
    uint Padding2;
    uint Padding3;
};

struct GPUMeshlet
{
    vec3 BoundingSphereCenter;
    float BoundingSphereRadius;
    uint VertexOffset;
    uint VertexCount;
    uint IndexOffset;
    uint IndexCount;
    uint LocalVertexOffset;
    uint LocalVertexCount;
    uint LocalTriangleOffset;
    uint LocalTriangleCount;
    vec3 NormalConeAxis;
    float NormalConeCutoff;
};

// Logical view of a hierarchy node encoded in one 36-byte meshlet record.
// Geometry records and node records never overlap; GPUMeshInfo publishes the
// exact node range and root record.
struct GPUMeshletHierarchyNode
{
    vec3 BoundingSphereCenter;
    float BoundingSphereRadius;
    float GeometricError;
    uint FirstChild;
    uint ChildCount;
    uint MeshletOffset;
    uint MeshletCount;
    uint Depth;
    uint Flags;
    uint Valid;
};

struct GPUObjectData
{
    mat4 WorldMatrix;
    mat4 WorldMatrixInverseTranspose;
    int MeshIndex;
    int MaterialIndex;
    int SkinnedVertexOffset;
    int SkinningEnabled;
    mat4 PreviousWorldMatrix;
    uint NearFieldStableObjectId;
    uint NearFieldStableMaterialId;
    uint NearFieldPackedObjectMaterialRevisions;
    uint NearFieldCoverageMotionFlags;
};

struct GPUMaterialData
{
    vec4 Albedo;
    vec4 Emissive;
    // x = normal scale, y = alpha mode (0 opaque, 1 mask, 2 blend),
    // z = alpha cutoff, w = double-sided flag.
    vec4 NormalScaleBias;
    vec4 MetallicRoughnessAO;
    vec4 BaseColorOffsetScale;
    vec4 NormalOffsetScale;
    vec4 MetallicRoughnessOffsetScale;
    vec4 OcclusionOffsetScale;
    vec4 EmissiveOffsetScale;
    vec4 TextureRotations;
    vec4 TextureTexCoordSets;
    // x = occlusion rotation, y = occlusion texcoord set, z = exact
    // MaterialBlendMode for transparent lighting policy, w reserved.
    vec4 OcclusionBinding;
    int AlbedoTextureIndex;
    int NormalTextureIndex;
    int MetallicRoughnessTextureIndex;
    int OcclusionTextureIndex;
    int EmissiveTextureIndex;
    uint FeatureFlags;
    int ExtensionDataIndex;
    uint TransportFlags;
    uint TransportProfileRevision;
    uint PackedMeanMetallicRoughness;
    uint TransportProfileQuality;
    uint MaterialRevision;
    uint TextureContentRevision;
    // Six binary16 transport values packed into the existing 12-byte
    // std430 alignment region: directional diffuse base RGB followed by
    // dielectric F0 RGB. This preserves the measured 304-byte material ABI.
    uint PackedMeanGiDirectionalDiffuseBaseRg;
    uint PackedMeanGiDirectionalDiffuseBaseBAndF0R;
    uint PackedMeanGiDielectricF0Gb;
    vec4 DdgiAverageAlbedo;
    vec4 DdgiAverageEmissive;
    // xyz = compact canonical thin-sheet diffuse transmittance.
    vec4 DdgiAverageTransmission;
    vec4 DdgiMaterialPolicy;
};

struct GPUMaterialExtensionData
{
    vec4 Clearcoat;
    vec4 SheenColor;
    vec4 Anisotropy;
    vec4 Transmission;
    vec4 AttenuationColor;
    vec4 Subsurface;
    vec4 SpecularColor;
    vec4 Iridescence;
    vec4 Dispersion;
    vec4 ClearcoatOffsetScale;
    vec4 ClearcoatRoughnessOffsetScale;
    vec4 ClearcoatNormalOffsetScale;
    vec4 SheenColorOffsetScale;
    vec4 SheenRoughnessOffsetScale;
    vec4 AnisotropyOffsetScale;
    vec4 TransmissionOffsetScale;
    vec4 ThicknessOffsetScale;
    vec4 SpecularOffsetScale;
    vec4 SpecularColorOffsetScale;
    vec4 IridescenceOffsetScale;
    vec4 IridescenceThicknessOffsetScale;
    vec4 SubsurfaceOffsetScale;
    vec4 ExtensionTextureRotations0;
    vec4 ExtensionTextureRotations1;
    vec4 ExtensionTextureRotations2;
    vec4 ExtensionTextureRotations3;
    vec4 ExtensionTextureTexCoordSets0;
    vec4 ExtensionTextureTexCoordSets1;
    vec4 ExtensionTextureTexCoordSets2;
    vec4 ExtensionTextureTexCoordSets3;
    int ClearcoatTextureIndex;
    int ClearcoatRoughnessTextureIndex;
    int ClearcoatNormalTextureIndex;
    int SheenColorTextureIndex;
    int SheenRoughnessTextureIndex;
    int AnisotropyTextureIndex;
    int TransmissionTextureIndex;
    int ThicknessTextureIndex;
    int SubsurfaceTextureIndex;
    int SpecularTextureIndex;
    int SpecularColorTextureIndex;
    int IridescenceTextureIndex;
    int IridescenceThicknessTextureIndex;
    int Padding0;
    int Padding1;
    int Padding2;
    int Padding3;
};

struct GPULight
{
    vec3 Position;
    float Intensity;
    vec3 Color;
    float Range;
    vec3 Direction;
    float SpotAngle;
    int Type;
    int ShadowFlags;
    float ShadowStrength;
    uint StableIdentity;
    float InnerSpotAngle;
    float AttenuationConstant;
    float AttenuationLinear;
    float AttenuationQuadratic;
    vec3 Up;
    float SizeX;
    float SizeY;
    int IesTextureIndex;
    float IesRotationRadians;
    int AreaFlags;
};

struct GPUSceneData
{
    mat4 ViewMatrix;
    mat4 ProjectionMatrix;
    mat4 ViewProjectionMatrix;
    mat4 InverseViewMatrix;
    mat4 InverseProjectionMatrix;
    vec3 CameraPosition;
    float Time;
    vec4 ScreenDimensions;
    vec4 NearFarPlanes;
    vec4 AmbientLight;
    int LightCount;
    int Padding0;
    int Padding1;
    int Padding2;
};

struct GPUMeshletDrawCommand
{
    uint MeshletIndex;
    uint InstanceId;
    uint MaterialIndex;
    uint Flags;
};

struct GPUSceneInstanceCandidate
{
    uint InstanceId;
    uint MaterialIndex;
    uint CommandFlags;
    uint Classification;
};

struct GPUSceneLodTransitionState
{
    uint SourceLod;
    uint TargetLod;
    uint TransitionStartFrame;
    uint LastUpdatedFrame;
};

struct GPUPackedMeshletDrawCommand
{
    uint MeshletIndex;
    uint InstanceId;
    uint MaterialIndex;
    uint Flags;
    vec4 WorldCenterRadius;
};

struct GPUMeshletTaskFrameData
{
    vec4 FrustumPlane0;
    vec4 FrustumPlane1;
    vec4 FrustumPlane2;
    vec4 FrustumPlane3;
    vec4 FrustumPlane4;
    vec4 FrustumPlane5;
    mat4 ViewProjectionMatrix;
    mat4 InverseViewMatrix;
    mat4 PreviousHiZViewProjectionMatrix;
    mat4 PreviousHiZInverseViewMatrix;
    vec2 ScreenDimensions;
    uint PreviousHiZFrameValid;
    uint Padding0;
    vec2 Padding1;
};

struct GPUFoliagePrototype
{
    uint MeshMetadataIndex;
    uint MeshletOffset;
    uint MeshletCount;
    uint MeshletLod1Offset;
    uint MeshletLod1Count;
    uint MeshletLod2Offset;
    uint MeshletLod2Count;
    uint MaterialIndex;
    uint GeometryMode;
    uint Flags;
    uint ImpostorMetadataIndex;
    uint MeshletOutputClass;
    float BladeHeight;
    float BladeWidth;
    vec4 LodDistances;
    vec4 WindParams;
    vec4 LightingParams;
};

struct GPUFoliageImpostor
{
    uint AlbedoOpacityTextureIndex;
    uint NormalTextureIndex;
    uint DepthTextureIndex;
    uint ViewCount;
    vec4 SourceBoundsMinScale;
    vec4 SourceBoundsMax;
    vec3 Pivot;
    uint ViewDataOffset;
};

struct GPUFoliageImpostorView
{
    vec4 Direction;
    vec4 AtlasRectangle;
};

struct GPUFoliagePatch
{
    vec4 BoundsMinDensity;
    vec4 BoundsMaxSeed;
    uint PrototypeIndex;
    uint ClusterOffset;
    uint ClusterCount;
    uint NearFieldStableObjectId;
    uint Seed;
    uint Flags;
    uint NearFieldStableMaterialId;
    uint NearFieldPackedObjectMaterialRevisions;
    uint DensityTextureIndex;
    uint TerrainDescriptorIndex;
    uint PlacementMode;
    uint ContentRevision;
    vec4 DensityUvScaleOffset;
};

struct GPUFoliageCluster
{
    vec4 WorldCenterRadius;
    vec4 BoundsMinDensity;
    vec4 BoundsMaxLod;
    uint PatchIndex;
    uint FirstInstance;
    uint InstanceCount;
    uint RandomSeed;
};

struct GPUFoliageInstance
{
    vec4 PositionScale;
    vec4 RotationWind;
    vec4 ColorVariation;
    uint PrototypeIndex;
    uint PatchIndex;
    uint ClusterIndex;
    uint Flags;
};

struct GPUFoliageMeshletDrawCommand
{
    uint MeshletIndex;
    uint InstanceIndex;
    uint PrototypeIndex;
    uint MaterialIndex;
    vec4 WorldCenterRadius;
    uint Flags;
    uint LodLevel;
    uint ClusterIndex;
    uint Padding0;
};

struct GPUFoliageCounters
{
    uint VisibleClusterCount;
    uint CulledClusterCount;
    uint Lod0VisibleCount;
    uint Lod1VisibleCount;
    uint Lod2VisibleCount;
    uint HiZTestedCount;
    uint HiZRejectedCount;
    uint VisibleMeshletDrawCount;
    uint MeshletDrawOverflowCount;
    uint FarImpostorVisibleCount;
    uint DensityRejectedCount;
    uint InvalidCommandCount;
};

struct GPUFoliageDispatchArgs
{
    uint GroupCountX;
    uint GroupCountY;
    uint GroupCountZ;
    uint CurrentFrameIndex;
};

struct GPUDdgiFoliageProxyPatch
{
    vec4 BoundsMinimumAndClusterWidth;
    vec4 BoundsMaximumAndCardHeight;
    vec4 WindAndCoverage;
    uint StablePatchKeyLow;
    uint StablePatchKeyHigh;
    uint CardOffset;
    uint CardCount;
    uint GridColumns;
    uint GridRows;
    uint RepresentedInstancesPerCard;
    uint Flags;
};

struct GPUDdgiFoliageProxyGenerationPushConstants
{
    uint PatchBufferIndex;
    uint VertexBufferIndex;
    uint IndexBufferIndex;
    uint PatchCount;
    uint CardCount;
    uint CurrentFrameIndex;
    float WindTimeSeconds;
    uint CadenceGenerationLow;
};

struct GPUSceneSubmissionCounters
{
    uint CandidateCount;
    uint EmittedCount;
    uint FrustumRejectedCount;
    uint OverflowCount;
    uint HiZTestedCount;
    uint HiZRejectedCount;
    uint AppendCount;
    uint Lod0EmittedCount;
    uint Lod1EmittedCount;
    uint Lod2EmittedCount;
    uint MissingLodFallbackCount;
    uint SolidDepthCandidateCount;
    uint SolidDepthEmittedCount;
    uint SolidDepthOverflowCount;
    uint MaskedDepthCandidateCount;
    uint MaskedDepthEmittedCount;
    uint MaskedDepthOverflowCount;
    uint SolidDepthAppendCount;
    uint MaskedDepthAppendCount;
    uint DirectionalStaticShadowCascade0CandidateCount;
    uint DirectionalStaticShadowCascade0EmittedCount;
    uint DirectionalStaticShadowCascade0RejectedCount;
    uint DirectionalStaticShadowCascade0OverflowCount;
    uint DirectionalStaticShadowCascade0AppendCount;
    uint DirectionalStaticShadowCascade1CandidateCount;
    uint DirectionalStaticShadowCascade1EmittedCount;
    uint DirectionalStaticShadowCascade1RejectedCount;
    uint DirectionalStaticShadowCascade1OverflowCount;
    uint DirectionalStaticShadowCascade1AppendCount;
    uint DirectionalStaticShadowCascade2CandidateCount;
    uint DirectionalStaticShadowCascade2EmittedCount;
    uint DirectionalStaticShadowCascade2RejectedCount;
    uint DirectionalStaticShadowCascade2OverflowCount;
    uint DirectionalStaticShadowCascade2AppendCount;
    uint DirectionalStaticShadowCascade3CandidateCount;
    uint DirectionalStaticShadowCascade3EmittedCount;
    uint DirectionalStaticShadowCascade3RejectedCount;
    uint DirectionalStaticShadowCascade3OverflowCount;
    uint DirectionalStaticShadowCascade3AppendCount;
    uint DirectionalDynamicShadowCascade0CandidateCount;
    uint DirectionalDynamicShadowCascade0EmittedCount;
    uint DirectionalDynamicShadowCascade0RejectedCount;
    uint DirectionalDynamicShadowCascade0OverflowCount;
    uint DirectionalDynamicShadowCascade0AppendCount;
    uint DirectionalDynamicShadowCascade1CandidateCount;
    uint DirectionalDynamicShadowCascade1EmittedCount;
    uint DirectionalDynamicShadowCascade1RejectedCount;
    uint DirectionalDynamicShadowCascade1OverflowCount;
    uint DirectionalDynamicShadowCascade1AppendCount;
    uint DirectionalDynamicShadowCascade2CandidateCount;
    uint DirectionalDynamicShadowCascade2EmittedCount;
    uint DirectionalDynamicShadowCascade2RejectedCount;
    uint DirectionalDynamicShadowCascade2OverflowCount;
    uint DirectionalDynamicShadowCascade2AppendCount;
    uint DirectionalDynamicShadowCascade3CandidateCount;
    uint DirectionalDynamicShadowCascade3EmittedCount;
    uint DirectionalDynamicShadowCascade3RejectedCount;
    uint DirectionalDynamicShadowCascade3OverflowCount;
    uint DirectionalDynamicShadowCascade3AppendCount;
    uint SimpleOpaqueAppendCount;
    uint SimpleOpaqueEmittedCount;
    uint SimpleOpaqueOverflowCount;
    uint SimpleNormalOpaqueAppendCount;
    uint SimpleNormalOpaqueEmittedCount;
    uint SimpleNormalOpaqueOverflowCount;
    uint FullOpaqueAppendCount;
    uint FullOpaqueEmittedCount;
    uint FullOpaqueOverflowCount;
    uint DirectionalShadowLodFallbackCount;
    uint OpaqueLodDecimatedCount;
    uint NormalConeCandidateCount;
    uint NormalConeTestedCount;
    uint NormalConeRejectedCount;
    uint NormalConeInvalidCount;
    uint SimpleOpaqueDoubleSidedAppendCount;
    uint SimpleNormalOpaqueDoubleSidedAppendCount;
    uint FullOpaqueDoubleSidedAppendCount;
    uint SolidDepthDoubleSidedAppendCount;
    uint MaskedDepthDoubleSidedAppendCount;
    uint DirectionalStaticShadowCascade0DoubleSidedAppendCount;
    uint DirectionalStaticShadowCascade1DoubleSidedAppendCount;
    uint DirectionalStaticShadowCascade2DoubleSidedAppendCount;
    uint DirectionalStaticShadowCascade3DoubleSidedAppendCount;
    uint DirectionalDynamicShadowCascade0DoubleSidedAppendCount;
    uint DirectionalDynamicShadowCascade1DoubleSidedAppendCount;
    uint DirectionalDynamicShadowCascade2DoubleSidedAppendCount;
    uint DirectionalDynamicShadowCascade3DoubleSidedAppendCount;
};

struct GPUSceneOpaqueCompactionPushConstants
{
    vec4 CameraPosition;
    uint CurrentFrameIndex;
    uint SimpleCandidateCount;
    uint SimpleNormalCandidateCount;
    uint FullCandidateCount;
    uint OutputCapacity;
    uint SolidDepthCandidateCount;
    uint MaskedDepthCandidateCount;
    uint SolidDepthOutputCapacity;
    uint MaskedDepthOutputCapacity;
    uint DirectionalShadowCascadeCount;
    uint DirectionalStaticShadowCandidateCount;
    uint DirectionalDynamicShadowCandidateCount;
    uint DirectionalStaticShadowOutputCapacity;
    uint DirectionalDynamicShadowOutputCapacity;
    uint OutputBufferBaseIndex;
    uint CounterBufferBaseIndex;
    uint Flags;
    uint IndirectDispatchBufferBaseIndex;
    uint SolidDepthOutputBufferBaseIndex;
    uint MaskedDepthOutputBufferBaseIndex;
    uint SimpleOutputCapacity;
    uint SimpleNormalOutputCapacity;
    uint FullOutputCapacity;
    uint SimpleOutputBufferBaseIndex;
    uint SimpleNormalOutputBufferBaseIndex;
    uint FullOutputBufferBaseIndex;
    vec2 ScreenDimensions;
    uint HiZTextureIndex;
    uint HiZMipCount;
    uint OcclusionCullingEnabled;
    float OcclusionBias;
    uint PreviousFrameUvPaddingPixels;
    uint PreviousHiZFrameValid;
    float GpuLod1DistanceRatio;
    float GpuLod2DistanceRatio;
    uint GpuLodSelectionMode;
    float GpuLodTargetPixelError;
    float GpuLodHysteresisFraction;
    float GpuLodProjectionScale;
    uint GpuLodHistoryBufferBaseIndex;
    uint GpuLodHistoryCapacity;
    uint GpuShadowLodBias;
    uint DirectionalStaticShadowCascadeMask;
    vec4 DirectionalShadowLightDirection;
    uint InstanceCandidateCount;
    uint InstanceCandidateBufferBaseIndex;
    uint TemporalFrameIndex;
    uint LodTransitionFrameCount;
};

struct GPUFoliageProceduralDrawCommand
{
    uint ClusterIndex;
    uint LodBand;
    uint CandidateCount;
    uint ActiveCount;
    float DensityFraction;
    float TransitionFraction;
    float WidthCompensation;
    uint Flags;
};

struct GPUFoliageAuthoredInstanceCommand
{
    uint InstanceIndex;
    uint ClusterIndex;
    uint PrototypeIndex;
    uint LodLevel;
    uint FirstMeshlet;
    uint MeshletCount;
    uint TargetFirstMeshlet;
    uint TargetMeshletCount;
    vec4 WorldCenterRadius;
    uint Flags;
    float TransitionFraction;
    uint Padding0;
    uint Padding1;
};

struct GPUForwardVisibilityCompactionPushConstants
{
    uint CurrentFrameIndex;
    uint SimpleInputCapacity;
    uint SimpleNormalInputCapacity;
    uint FullInputCapacity;
    uint SimpleOutputCapacity;
    uint SimpleNormalOutputCapacity;
    uint FullOutputCapacity;
    uint InputCounterBufferBaseIndex;
    uint OutputCounterBufferBaseIndex;
    uint InputSimpleBufferBaseIndex;
    uint InputSimpleNormalBufferBaseIndex;
    uint InputFullBufferBaseIndex;
    uint OutputSimpleBufferBaseIndex;
    uint OutputSimpleNormalBufferBaseIndex;
    uint OutputFullBufferBaseIndex;
    uint IndirectDispatchBufferBaseIndex;
    vec2 ScreenDimensions;
    uint HiZTextureIndex;
    uint HiZMipCount;
    uint OcclusionCullingEnabled;
    float OcclusionBias;
    uint Padding0;
};

struct GPUFoliageCullPushConstants
{
    vec4 CameraPositionMaxDistance;
    uint CurrentFrameIndex;
    uint ClusterCount;
    uint VisibleClusterCapacity;
    uint MeshletDrawCapacity;
    uint IndirectDispatchBufferBaseIndex;
    uint Flags;
    uint AuthoredMeshletWorkItemCount;
    uint FirstAuthoredClusterIndex;
    uint AuthoredClusterCount;
    uint Padding0;
    vec2 ScreenDimensions;
    uint HiZTextureIndex;
    uint HiZMipCount;
    uint OcclusionCullingEnabled;
    float OcclusionBias;
    uint PreviousHiZFrameValid;
    uint PreviousFrameUvPaddingPixels;
};

struct GPUFoliageDrawPushConstants
{
    mat4 ViewProjectionMatrix;
    vec4 CameraPositionTime;
    vec4 ScreenDimensions;
    uint CurrentFrameIndex;
    uint ClusterDrawCount;
    uint VisibleClusterBufferBaseIndex;
    uint Flags;
    uint DebugView;
    float ShadowDensityScale;
    uint Padding1;
    uint Padding2;
    uint FirstDraw;
};

struct GPUTiledLightHeader
{
    uint LightCount;
    uint LightOffset;
    uint OverflowCount;
    uint Padding1;
};

struct GPULightIndex
{
    uint LightIndex;
};

struct GPUScreenToViewParams
{
    vec2 ScreenDimensions;
    vec2 InvScreenDimensions;
    vec2 TileSize;
    vec2 InvTileSize;
};

struct GPULightCullingParams
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewProjectionMatrix;
    vec3 CameraPosition;
    float Padding0;
    vec4 ScreenDimensions;
    vec4 NearFarPlanes;
    uint LightCount;
    uint MaxLightsPerTile;
    uint TileCountX;
    uint TileCountY;
};

struct GPUDepthPushConstants
{
    mat4 ViewProjectionMatrix;
    vec2 ScreenDimensions;
    uint CurrentFrameIndex;
    uint MeshletDrawCount;
    uint MeshletDrawBufferBaseIndex;
    uint FirstDraw;
    uint Padding1;
    uint Padding2;
};

struct GPUForwardPushConstants
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewMatrix;
    mat4 InverseProjectionMatrix;
    vec3 CameraPosition;
    float Time;
    vec2 ScreenDimensions;
    uint CurrentFrameIndex;
    uint MeshletDrawCount;
    uint MeshletDrawBufferBaseIndex;
    uint PackedLightDispatch;
    uint LocalLightCount;
    uint HiZMipCount;
    uint OcclusionCullingEnabled;
    float OcclusionBias;
    uint DebugAndAoFlags;
    uint DiagnosticFlags;
};

const uint FORWARD_TOTAL_LIGHT_COUNT_MASK = 0x7ffu;
const uint FORWARD_DIRECTIONAL_LIGHT_INDEX_MASK = 0x3ffu;

uint ForwardTotalLightCount(GPUForwardPushConstants pushConstants)
{
    return pushConstants.PackedLightDispatch &
        FORWARD_TOTAL_LIGHT_COUNT_MASK;
}

// Scale-invariant geometric-normal offset from Ray Tracing Gems. The integer
// ULP displacement remains effective far from the origin while the small
// floating displacement handles coordinates whose exponent is near zero.
vec3 NjulfOffsetRayOrigin(vec3 position, vec3 geometricNormal)
{
    const float origin = 1.0 / 32.0;
    const float floatScale = 1.0 / 65536.0;
    const float integerScale = 256.0;
    ivec3 integerOffset = ivec3(integerScale * geometricNormal);
    ivec3 positionBits = floatBitsToInt(position);
    positionBits += ivec3(
        position.x < 0.0 ? -integerOffset.x : integerOffset.x,
        position.y < 0.0 ? -integerOffset.y : integerOffset.y,
        position.z < 0.0 ? -integerOffset.z : integerOffset.z);
    vec3 ulpOffsetPosition = intBitsToFloat(positionBits);
    vec3 nearOriginPosition = position + floatScale * geometricNormal;
    return mix(
        ulpOffsetPosition,
        nearOriginPosition,
        lessThan(abs(position), vec3(origin)));
}

uint ForwardDirectionalLightCount(GPUForwardPushConstants pushConstants)
{
    return min(
        ForwardTotalLightCount(pushConstants) -
            min(pushConstants.LocalLightCount,
                ForwardTotalLightCount(pushConstants)),
        2u);
}

uint ForwardDirectionalLightIndex(
    GPUForwardPushConstants pushConstants,
    uint ordinal)
{
    uint shift = ordinal == 0u ? 11u : 21u;
    return (pushConstants.PackedLightDispatch >> shift) &
        FORWARD_DIRECTIONAL_LIGHT_INDEX_MASK;
}

struct GPUMotionVectorPushConstants
{
    mat4 ViewProjectionMatrix;
    mat4 PreviousViewProjectionMatrix;
    vec2 ScreenDimensions;
    uint CurrentFrameIndex;
    uint MeshletDrawCount;
    uint MeshletDrawBufferBaseIndex;
    uint PreviousFrameValid;
    float Time;
    float PreviousTime;
    uint FirstDraw;
    uint Padding0;
    uint Padding1;
    uint Padding2;
    vec4 CameraPosition;
    vec4 PreviousCameraPosition;
};

struct GPULightCullPushConstants
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewProjectionMatrix;
    vec3 CameraPosition;
    float Padding0;
    vec3 CameraForward;
    float PaddingCameraForward;
    vec2 ScreenDimensions;
    float NearPlane;
    float FarPlane;
    uint LightCount;
    uint MaxLightsPerTile;
    uint TileCountX;
    uint TileCountY;
    uint DepthTextureIndex;
    uint ClusterCountZ;
    uint TotalClusterCount;
    uint LightIndexCapacity;
};

struct GPUShadowData
{
    mat4 LightViewProjection0;
    mat4 LightViewProjection1;
    mat4 LightViewProjection2;
    mat4 LightViewProjection3;
    vec4 CascadeSplits;
    vec4 Settings;
    vec4 Indices;
    vec4 CascadeTransitionData;
};

struct GPUDirectionalShadowParameters
{
    vec4 CascadeWorldTexelSizes;
    vec4 FilterAndBias;
    vec4 ModeAndRayDistance;
    vec4 TemporalAndSampling;
    vec4 RaySceneBoundsMinimum;
    vec4 RaySceneBoundsMaximum;
    vec4 RuntimeFlags;
};

struct GPUSpotShadow
{
    mat4 LightViewProjection;
    vec4 AtlasScaleOffset;
    vec4 BiasStrengthTexelSize;
    int LightIndex;
    int AtlasTile;
    int PcfRadius;
    int Enabled;
};

struct GPUPointShadow
{
    mat4 FaceViewProjection0;
    mat4 FaceViewProjection1;
    mat4 FaceViewProjection2;
    mat4 FaceViewProjection3;
    mat4 FaceViewProjection4;
    mat4 FaceViewProjection5;
    vec4 PositionRange;
    vec4 BiasStrengthTexelSize;
    int LightIndex;
    int CubemapIndex;
    int PcfRadius;
    int Enabled;
};

struct GPULocalLightShadowIndex
{
    int SpotShadowIndex;
    int PointShadowIndex;
    int AreaShadowIndex;
    int Padding1;
};

struct GPUEnvironmentData
{
    int EnvironmentTextureIndex;
    int IrradianceTextureIndex;
    int PrefilteredTextureIndex;
    int BrdfLutTextureIndex;
    float SkyIntensity;
    float DiffuseIntensity;
    float SpecularIntensity;
    float RotationRadians;
    uint PrefilteredMipCount;
    uint Enabled;
    uint DebugView;
    uint DebugMipLevel;
    int NextPrefilteredTextureIndex;
    uint SourceKind;
    uint AtmosphereFlags;
    float PrefilteredBlend;
    vec4 SunDirectionAndAngularRadius;
    vec4 SunRadianceAndElevation;
    vec4 MoonDirectionAndAngularRadius;
    vec4 MoonRadianceAndNightBlend;
    vec4 GroundAlbedoAndTurbidity;
    vec4 AtmosphereParameters;
    vec4 GroundRadianceAndAirglow;
    vec4 HosekParametersR0;
    vec4 HosekParametersR1;
    vec4 HosekParametersR2;
    vec4 HosekParametersG0;
    vec4 HosekParametersG1;
    vec4 HosekParametersG2;
    vec4 HosekParametersB0;
    vec4 HosekParametersB1;
    vec4 HosekParametersB2;
    vec4 HosekRadiances;
    vec4 DiffuseIrradianceSh0;
    vec4 DiffuseIrradianceSh1;
    vec4 DiffuseIrradianceSh2;
    vec4 DiffuseIrradianceSh3;
    vec4 DiffuseIrradianceSh4;
    vec4 DiffuseIrradianceSh5;
    vec4 DiffuseIrradianceSh6;
    vec4 DiffuseIrradianceSh7;
    vec4 DiffuseIrradianceSh8;
};

struct GPUReflectionProbeHeader
{
    int ProbeCount;
    int MaxProbesPerPixel;
    int ProbeCubemapArrayTextureIndex;
    int DebugTextureIndex;
    float Intensity;
    float GlobalFallbackIntensity;
    uint ProbeMipCount;
    uint Flags;
    uint DebugView;
    int DebugProbeIndex;
    int DebugCubemapFace;
    int DebugMipLevel;
    uint SsrMaximumSteps;
    float SsrMaximumDistance;
    float SsrConfidenceThreshold;
    uint SceneReflectionRayTaskBudget;
    uint RayQueryHitLightLimit;
    uint SceneReflectionSsrSampleBudget;
    uint Padding1;
    uint Padding2;
};

struct GPUReflectionProbe
{
    mat4 WorldToProbe;
    vec4 PositionAndRadius;
    vec4 BoxMin;
    vec4 BoxMax;
    vec4 BlendParams;
    int CubemapArrayIndex;
    int Shape;
    int Flags;
    int Priority;
};

struct GPUDdgiRayQueryInstance
{
    uint AbiVersion;
    uint GeometryClass;
    uint GeometryFlags;
    uint StableInstanceIdentity;
    uint VertexBufferIndex;
    uint VertexOffset;
    uint VertexStride;
    uint VertexFormat;
    uint PositionOffset;
    uint NormalOffset;
    uint TangentOffset;
    uint TexCoord0Offset;
    uint TexCoord1Offset;
    uint ColorOffset;
    uint IndexBufferIndex;
    uint IndexOffset;
    uint IndexType;
    uint MaterialIndex;
    uint MaterialRevision;
    uint PackedAlpha;
    uint PackedDecalLayerAndOrder;
    float DecalDepthTolerance;
    float DecalDepthBias;
    uint RepresentationGeneration;
    mat4 WorldMatrixInverseTranspose;
};

const uint DDGI_RAY_QUERY_INSTANCE_ABI_V2 = 0x44520002u;
const uint DDGI_RAY_GEOMETRY_INVALID = 0u;
const uint DDGI_RAY_GEOMETRY_STATIC_OPAQUE = 1u;
const uint DDGI_RAY_GEOMETRY_RIGID_OPAQUE = 2u;
const uint DDGI_RAY_GEOMETRY_SKINNED_CURRENT_POSE = 3u;
const uint DDGI_RAY_GEOMETRY_ALPHA_MASK = 4u;
const uint DDGI_RAY_GEOMETRY_ALPHA_BLEND = 5u;
const uint DDGI_RAY_GEOMETRY_THIN_TRANSMISSION = 6u;
const uint DDGI_RAY_GEOMETRY_DECAL_OVERLAY = 7u;
const uint DDGI_RAY_GEOMETRY_AUTHORED_FOLIAGE = 8u;
const uint DDGI_RAY_GEOMETRY_PROCEDURAL_FOLIAGE = 9u;
const uint DDGI_RAY_GEOMETRY_CONSERVATIVE_PROXY = 10u;
const uint DDGI_RAY_GEOMETRY_VOLUME_TRANSMISSION = 11u;
const uint DDGI_RAY_GEOMETRY_WATER_SURFACE = 12u;
const uint DDGI_RAY_VERTEX_FORMAT_SPLIT_STATIC = 1u;
const uint DDGI_RAY_VERTEX_FORMAT_GPU_VERTEX = 2u;
const uint DDGI_RAY_VERTEX_FORMAT_FOLIAGE_PROXY = 3u;
const uint DDGI_RAY_GEOMETRY_FLAG_ALPHA_MASK = 1u << 0u;
const uint DDGI_RAY_GEOMETRY_FLAG_ALPHA_BLEND = 1u << 1u;
const uint DDGI_RAY_GEOMETRY_FLAG_THIN_TRANSMISSION = 1u << 2u;
const uint DDGI_RAY_GEOMETRY_FLAG_TWO_SIDED = 1u << 3u;
const uint DDGI_RAY_GEOMETRY_FLAG_DECAL_OVERLAY = 1u << 4u;
const uint DDGI_RAY_GEOMETRY_FLAG_FOLIAGE = 1u << 5u;
const uint DDGI_RAY_GEOMETRY_FLAG_DYNAMIC_VERTEX_SOURCE = 1u << 6u;
const uint DDGI_RAY_GEOMETRY_FLAG_CONSERVATIVE_PROXY = 1u << 7u;
const uint DDGI_RAY_GEOMETRY_FLAG_PREMULTIPLIED_ALPHA = 1u << 8u;
const uint DDGI_RAY_GEOMETRY_FLAG_UNSUPPORTED_MATERIAL_PROXY = 1u << 9u;
const uint DDGI_RAY_GEOMETRY_FLAG_VOLUME_TRANSMISSION = 1u << 10u;
const uint DDGI_RAY_GEOMETRY_FLAG_WATER_SURFACE = 1u << 11u;

struct GPUDdgiEmissiveSource
{
    vec4 Vertex0Area;
    vec4 Edge1AliasProbability;
    vec4 Edge2AliasFlags;
    vec4 RadianceSelectionProbability;
};

struct GPUDdgiEmissiveSurface
{
    vec4 Uv0Vertex01;
    vec4 Uv0Vertex2Uv1Vertex0;
    vec4 Uv1Vertex12;
    vec4 MaterialAndVertexAlpha;
};

struct GPUFogPushConstants
{
    mat4 InverseViewProjectionMatrix;
    vec4 CameraPositionAndTime;
    vec4 ScreenDimensions;
    vec4 FogColorAndDensity;
    vec4 FogHeightParams;
    vec4 FogDistanceParams;
    vec4 DirectionalInscatteringColorAndIntensity;
    vec4 DirectionalInscatteringDirectionAndExponent;
    vec4 SkyColorAndBlend;
    uint SceneColorTextureIndex;
    uint DepthTextureIndex;
    uint EnvironmentTextureIndex;
    uint Mode;
    uint ColorMode;
    uint DebugView;
    uint DirectionalInscatteringEnabled;
    uint CurrentFrameIndex;
};

// Descriptor arrays matching BindlessHeap. Heterogeneous storage buffers are
// addressed by descriptor array element and interpreted by pass-specific code.
layout(set = 0, binding = 0) buffer BindlessStorageBuffer
{
    uint Words[];
} BindlessStorageBuffers[];

// Read-only vector view of the same storage-buffer descriptors. This is used
// only by accessors that can prove a dynamically-uniform descriptor index; the
// alternate view lets SPIR-V retain 128-bit loads instead of scalarizing every
// four-word structure member at the source level.
layout(set = 0, binding = 0) readonly buffer BindlessStorageVectorBuffer
{
    uvec4 Vectors[];
} BindlessStorageVectorBuffers[];

// Read-only 64-bit view for compact two-word records. As with the uvec4 view,
// callers must prove both a dynamically-uniform descriptor and record-aligned
// word offset.
layout(set = 0, binding = 0) readonly buffer BindlessStoragePairBuffer
{
    uvec2 Pairs[];
} BindlessStoragePairBuffers[];

layout(set = 1, binding = 0) uniform sampler2D BindlessTextures[];
layout(set = 1, binding = 0) uniform sampler2DArray BindlessArrayTextures[];
layout(set = 1, binding = 0) uniform samplerCube BindlessCubeTextures[];
layout(set = 1, binding = 0) uniform samplerCubeArray BindlessCubeArrayTextures[];

// Documented sizes (bytes). Tests parse these constants and compare them to C#.
const int SIZEOF_GPU_VERTEX = 80;
const int SIZEOF_GPU_VERTEX_POSITION_STREAM = 16;
const int SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM = 32;
const int SIZEOF_GPU_VERTEX_UV_COLOR_STREAM = 32;
const int SIZEOF_GPU_MESH_INFO = 88;
const int SIZEOF_GPU_VERTEX_SKINNING_DATA = 32;
const int SIZEOF_GPU_SKINNING_DISPATCH = 32;
const int SIZEOF_GPU_SKINNING_PUSH_CONSTANTS = 16;
const int SIZEOF_GPU_PARTICLE_INSTANCE = 128;
const int SIZEOF_GPU_PARTICLE_BATCH = 16;
const int SIZEOF_GPU_PARTICLE_FRAME_DATA = 224;
const int SIZEOF_GPU_PARTICLE_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_PARTICLE_EMITTER = 288;
const int SIZEOF_GPU_PARTICLE_CURVE_SAMPLE = 32;
const int SIZEOF_GPU_PARTICLE_STATE = 80;
const int SIZEOF_GPU_PARTICLE_COUNTERS = 88;
const int SIZEOF_GPU_PARTICLE_DRAW_COMMAND = 16;
const int SIZEOF_GPU_PARTICLE_SORT_KEY = 8;
const int SIZEOF_GPU_PARTICLE_RESET_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_PARTICLE_SIMULATE_PUSH_CONSTANTS = 48;
const int SIZEOF_GPU_PARTICLE_SORT_PUSH_CONSTANTS = 32;
const int GPU_MESHLET_ABI_VERSION = 2;
const int SIZEOF_GPU_MESHLET = 36;
const int SIZEOF_GPU_OBJECT_DATA = 224;
const int SIZEOF_GPU_DEBUG_LINE_VERTEX = 32;
const int SIZEOF_GPU_MATERIAL_DATA = 320;
const int SIZEOF_GPU_FORWARD_MATERIAL_DATA = 112;
const int SIZEOF_GPU_MATERIAL_EXTENSION_DATA = 548;
const int SIZEOF_GPU_LIGHT = 112;
const int SIZEOF_GPU_SCENE_DATA = 400;
const int SIZEOF_GPU_MESHLET_DRAW_COMMAND = 16;
const int SIZEOF_GPU_SCENE_INSTANCE_CANDIDATE = 16;
const int SIZEOF_GPU_SCENE_LOD_TRANSITION_STATE = 16;
const int SIZEOF_GPU_PACKED_MESHLET_DRAW_COMMAND = 32;
const int SIZEOF_GPU_MESHLET_TASK_FRAME_DATA = 376;
const int SIZEOF_GPU_FOLIAGE_PROTOTYPE = 104;
const int SIZEOF_GPU_FOLIAGE_IMPOSTOR = 64;
const int SIZEOF_GPU_FOLIAGE_IMPOSTOR_VIEW = 32;
const int SIZEOF_GPU_FOLIAGE_PATCH = 96;
const int SIZEOF_GPU_FOLIAGE_CLUSTER = 64;
const int SIZEOF_GPU_FOLIAGE_INSTANCE = 64;
const int SIZEOF_GPU_FOLIAGE_MESHLET_DRAW_COMMAND = 48;
const int SIZEOF_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND = 32;
const int SIZEOF_GPU_FOLIAGE_AUTHORED_INSTANCE_COMMAND = 64;
const int SIZEOF_GPU_FOLIAGE_COUNTERS = 48;
const int SIZEOF_GPU_FOLIAGE_DISPATCH_ARGS = 16;
const int SIZEOF_GPU_DDGI_FOLIAGE_PROXY_PATCH = 80;
const int SIZEOF_GPU_DDGI_FOLIAGE_PROXY_GENERATION_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_SCENE_SUBMISSION_COUNTERS = 360;
const int SIZEOF_GPU_SCENE_OPAQUE_COMPACTION_PUSH_CONSTANTS = 224;
const int SIZEOF_GPU_FORWARD_VISIBILITY_COMPACTION_PUSH_CONSTANTS = 92;
const int SIZEOF_GPU_FOLIAGE_CULL_PUSH_CONSTANTS = 88;
const int SIZEOF_GPU_FOLIAGE_DRAW_PUSH_CONSTANTS = 132;
const int SIZEOF_GPU_TILED_LIGHT_HEADER = 16;
const int SIZEOF_GPU_LIGHT_INDEX = 4;
const int SIZEOF_GPU_SCREEN_TO_VIEW_PARAMS = 32;
const int SIZEOF_GPU_LIGHT_CULLING_PARAMS = 192;
const int SIZEOF_GPU_DEPTH_PUSH_CONSTANTS = 96;
const int SIZEOF_GPU_FORWARD_PUSH_CONSTANTS = 256;
const int SIZEOF_GPU_MOTION_VECTOR_PUSH_CONSTANTS = 208;
const int SIZEOF_GPU_LIGHT_CULL_PUSH_CONSTANTS = 208;
const int SIZEOF_GPU_SHADOW_DATA = 320;
const int SIZEOF_GPU_DIRECTIONAL_SHADOW_PARAMETERS = 112;
const int SIZEOF_GPU_SPOT_SHADOW = 112;
const int SIZEOF_GPU_POINT_SHADOW = 432;
const int SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX = 16;
const int SIZEOF_GPU_ENVIRONMENT_DATA = 48;
const int SIZEOF_GPU_REFLECTION_PROBE_HEADER = 80;
const int SIZEOF_GPU_REFLECTION_PROBE = 144;
const int SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER = 80;
const int SIZEOF_GPU_DDGI_PROBE_VOLUME = 144;
const int SIZEOF_GPU_DDGI_PROBE_STATE = 96;
const int SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST = 32;
const int SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION = 48;
const int SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE = 160;
const int SIZEOF_GPU_DDGI_RAY_RESULT = 80;
const int SIZEOF_GPU_DDGI_EMISSIVE_SOURCE = 64;
const int SIZEOF_GPU_DDGI_EMISSIVE_SURFACE = 64;
const int SIZEOF_GPU_DDGI_UPDATE_PUSH_CONSTANTS = 148;
const int SIZEOF_GPU_FOG_PUSH_CONSTANTS = 224;
const int SIZEOF_GPU_ANTI_ALIASING_PUSH_CONSTANTS = 120;
const int SIZEOF_GPU_AMBIENT_OCCLUSION_PUSH_CONSTANTS = 176;
const int SIZEOF_GPU_AMBIENT_OCCLUSION_BLUR_PUSH_CONSTANTS = 96;

const int OFFSET_GPU_DDGI_PROBE_STATE_IRRADIANCE = 0;
const int OFFSET_GPU_DDGI_PROBE_STATE_VISIBILITY = 16;
const int OFFSET_GPU_DDGI_PROBE_STATE_RELOCATION_AND_CLASSIFICATION = 32;
const int OFFSET_GPU_DDGI_PROBE_STATE_QUALITY_AND_REASON = 48;
const int OFFSET_GPU_DDGI_PROBE_STATE_UPDATE_METADATA = 64;
const int OFFSET_GPU_DDGI_PROBE_STATE_REPRESENTATION_METADATA = 80;

const uint DIAGNOSTIC_DEPTH_CANDIDATES = 0u;
const uint DIAGNOSTIC_DEPTH_FRUSTUM_CULLED = 1u;
const uint DIAGNOSTIC_DEPTH_EMITTED = 2u;
const uint DIAGNOSTIC_FORWARD_CANDIDATES = 3u;
const uint DIAGNOSTIC_FORWARD_FRUSTUM_CULLED = 4u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_CULLED = 5u;
const uint DIAGNOSTIC_FORWARD_EMITTED = 6u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_TESTED = 7u;
const uint DIAGNOSTIC_RESERVED_8 = 8u;

const uint MATERIAL_FEATURE_CLEARCOAT = 1u << 0;
const uint MATERIAL_FEATURE_CLEARCOAT_TEXTURE = 1u << 1;
const uint MATERIAL_FEATURE_CLEARCOAT_ROUGHNESS_TEXTURE = 1u << 2;
const uint MATERIAL_FEATURE_CLEARCOAT_NORMAL_TEXTURE = 1u << 3;
const uint MATERIAL_FEATURE_SHEEN = 1u << 4;
const uint MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE = 1u << 5;
const uint MATERIAL_FEATURE_SHEEN_ROUGHNESS_TEXTURE = 1u << 6;
const uint MATERIAL_FEATURE_ANISOTROPY = 1u << 7;
const uint MATERIAL_FEATURE_ANISOTROPY_TEXTURE = 1u << 8;
const uint MATERIAL_FEATURE_TRANSMISSION = 1u << 9;
const uint MATERIAL_FEATURE_TRANSMISSION_TEXTURE = 1u << 10;
const uint MATERIAL_FEATURE_VOLUME_APPROXIMATION = 1u << 11;
const uint MATERIAL_FEATURE_SUBSURFACE = 1u << 12;
const uint MATERIAL_FEATURE_SUBSURFACE_TEXTURE = 1u << 13;
const uint MATERIAL_FEATURE_EMISSIVE_STRENGTH = 1u << 14;
const uint MATERIAL_FEATURE_SPECULAR = 1u << 15;
const uint MATERIAL_FEATURE_SPECULAR_TEXTURE = 1u << 16;
const uint MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE = 1u << 17;
const uint MATERIAL_FEATURE_IRIDESCENCE = 1u << 18;
const uint MATERIAL_FEATURE_IRIDESCENCE_TEXTURE = 1u << 19;
const uint MATERIAL_FEATURE_IRIDESCENCE_THICKNESS_TEXTURE = 1u << 20;
const uint MATERIAL_FEATURE_DISPERSION = 1u << 21;
const uint MATERIAL_FEATURE_FOLIAGE = 1u << 22;
const uint MATERIAL_FEATURE_COMPRESSED_NORMAL_BC5 = 1u << 23;
const uint MATERIAL_FEATURE_IOR = 1u << 24;
const uint MATERIAL_FEATURE_NORMAL_GREEN_INVERTED = 1u << 25;

// Documented byte offsets for layout-critical fields. These are parsed by
// tests because GLSL has no portable compile-time offsetof operator.
const int OFFSET_GPU_VERTEX_POSITION = 0;
const int OFFSET_GPU_VERTEX_NORMAL = 16;
const int OFFSET_GPU_VERTEX_TEX_COORD = 32;
const int OFFSET_GPU_VERTEX_TANGENT = 48;
const int OFFSET_GPU_VERTEX_COLOR = 64;

const int OFFSET_GPU_VERTEX_SKINNING_DATA_JOINT0 = 0;
const int OFFSET_GPU_VERTEX_SKINNING_DATA_WEIGHT0 = 16;

const int OFFSET_GPU_SKINNING_DISPATCH_SOURCE_VERTEX_OFFSET = 0;
const int OFFSET_GPU_SKINNING_DISPATCH_SOURCE_SKINNING_DATA_OFFSET = 4;
const int OFFSET_GPU_SKINNING_DISPATCH_DESTINATION_VERTEX_OFFSET = 8;
const int OFFSET_GPU_SKINNING_DISPATCH_VERTEX_COUNT = 12;
const int OFFSET_GPU_SKINNING_DISPATCH_SKIN_MATRIX_OFFSET = 16;

// GPUMaterialData is a public CPU/GPU ABI. Keep these constants explicit so
// layout tests catch accidental field insertion, reordering, or padding.
const int OFFSET_GPU_MATERIAL_DATA_ALBEDO = 0;
const int OFFSET_GPU_MATERIAL_DATA_EMISSIVE = 16;
const int OFFSET_GPU_MATERIAL_DATA_NORMAL_SCALE_BIAS = 32;
const int OFFSET_GPU_MATERIAL_DATA_METALLIC_ROUGHNESS_AO = 48;
const int OFFSET_GPU_MATERIAL_DATA_BASE_COLOR_OFFSET_SCALE = 64;
const int OFFSET_GPU_MATERIAL_DATA_NORMAL_OFFSET_SCALE = 80;
const int OFFSET_GPU_MATERIAL_DATA_METALLIC_ROUGHNESS_OFFSET_SCALE = 96;
const int OFFSET_GPU_MATERIAL_DATA_OCCLUSION_OFFSET_SCALE = 112;
const int OFFSET_GPU_MATERIAL_DATA_EMISSIVE_OFFSET_SCALE = 128;
const int OFFSET_GPU_MATERIAL_DATA_TEXTURE_ROTATIONS = 144;
const int OFFSET_GPU_MATERIAL_DATA_TEXTURE_TEX_COORD_SETS = 160;
const int OFFSET_GPU_MATERIAL_DATA_OCCLUSION_BINDING = 176;
const int OFFSET_GPU_MATERIAL_DATA_ALBEDO_TEXTURE_INDEX = 192;
const int OFFSET_GPU_MATERIAL_DATA_NORMAL_TEXTURE_INDEX = 196;
const int OFFSET_GPU_MATERIAL_DATA_METALLIC_ROUGHNESS_TEXTURE_INDEX = 200;
const int OFFSET_GPU_MATERIAL_DATA_OCCLUSION_TEXTURE_INDEX = 204;
const int OFFSET_GPU_MATERIAL_DATA_EMISSIVE_TEXTURE_INDEX = 208;
const int OFFSET_GPU_MATERIAL_DATA_FEATURE_FLAGS = 212;
const int OFFSET_GPU_MATERIAL_DATA_EXTENSION_DATA_INDEX = 216;
const int OFFSET_GPU_MATERIAL_DATA_TRANSPORT_FLAGS = 220;
const int OFFSET_GPU_MATERIAL_DATA_TRANSPORT_PROFILE_REVISION = 224;
const int OFFSET_GPU_MATERIAL_DATA_PACKED_MEAN_METALLIC_ROUGHNESS = 228;
const int OFFSET_GPU_MATERIAL_DATA_TRANSPORT_PROFILE_QUALITY = 232;
const int OFFSET_GPU_MATERIAL_DATA_MATERIAL_REVISION = 236;
const int OFFSET_GPU_MATERIAL_DATA_TEXTURE_CONTENT_REVISION = 240;
const int OFFSET_GPU_MATERIAL_DATA_REVISION_PADDING0 = 244;
const int OFFSET_GPU_MATERIAL_DATA_REVISION_PADDING1 = 248;
const int OFFSET_GPU_MATERIAL_DATA_REVISION_PADDING2 = 252;
const int OFFSET_GPU_MATERIAL_DATA_DDGI_AVERAGE_ALBEDO = 256;
const int OFFSET_GPU_MATERIAL_DATA_DDGI_AVERAGE_EMISSIVE = 272;
const int OFFSET_GPU_MATERIAL_DATA_DDGI_MATERIAL_POLICY = 288;

const int OFFSET_GPU_PARTICLE_INSTANCE_POSITION_SIZE = 0;
const int OFFSET_GPU_PARTICLE_INSTANCE_VELOCITY_ROTATION = 16;
const int OFFSET_GPU_PARTICLE_INSTANCE_COLOR = 32;
const int OFFSET_GPU_PARTICLE_INSTANCE_EMISSIVE_LIFETIME_SOFT_CLIP = 48;
const int OFFSET_GPU_PARTICLE_INSTANCE_TEXTURE_INDEX = 64;
const int OFFSET_GPU_PARTICLE_INSTANCE_BLEND_MODE = 80;
const int OFFSET_GPU_PARTICLE_INSTANCE_VOLUMETRIC_ALBEDO = 96;
const int OFFSET_GPU_PARTICLE_INSTANCE_VOLUMETRIC_RADIUS = 112;
const int OFFSET_GPU_PARTICLE_BATCH_START = 0;
const int OFFSET_GPU_PARTICLE_BATCH_COUNT = 4;
const int OFFSET_GPU_PARTICLE_FRAME_DATA_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_PARTICLE_FRAME_DATA_INVERSE_VIEW_MATRIX = 64;
const int OFFSET_GPU_PARTICLE_FRAME_DATA_INVERSE_PROJECTION_MATRIX = 128;
const int OFFSET_GPU_PARTICLE_FRAME_DATA_CAMERA_POSITION = 192;
const int OFFSET_GPU_PARTICLE_FRAME_DATA_SCREEN_DIMENSIONS = 208;
const int OFFSET_GPU_PARTICLE_PUSH_CURRENT_FRAME_INDEX = 0;
const int OFFSET_GPU_PARTICLE_PUSH_INSTANCE_BUFFER_BASE_INDEX = 4;
const int OFFSET_GPU_PARTICLE_PUSH_FRAME_DATA_BUFFER_BASE_INDEX = 8;
const int OFFSET_GPU_PARTICLE_PUSH_DEPTH_TEXTURE_INDEX = 12;
const int OFFSET_GPU_PARTICLE_PUSH_DEBUG_VIEW = 16;
const int OFFSET_GPU_PARTICLE_PUSH_SOFT_PARTICLES_ENABLED = 20;
const int OFFSET_GPU_PARTICLE_PUSH_INSTANCE_OFFSET = 24;
const int OFFSET_GPU_PARTICLE_EMITTER_WORLD_MATRIX = 0;
const int OFFSET_GPU_PARTICLE_EMITTER_SPAWN_SHAPE0 = 64;
const int OFFSET_GPU_PARTICLE_EMITTER_SPAWN_SHAPE1 = 80;
const int OFFSET_GPU_PARTICLE_EMITTER_INITIAL_VELOCITY_MIN = 96;
const int OFFSET_GPU_PARTICLE_EMITTER_INITIAL_VELOCITY_MAX = 112;
const int OFFSET_GPU_PARTICLE_EMITTER_ACCELERATION_DRAG = 128;
const int OFFSET_GPU_PARTICLE_EMITTER_LIFETIME_SIZE = 144;
const int OFFSET_GPU_PARTICLE_EMITTER_COLOR = 160;
const int OFFSET_GPU_PARTICLE_EMITTER_MATERIAL_INDEX = 176;
const int OFFSET_GPU_PARTICLE_EMITTER_COLOR_END = 192;
const int OFFSET_GPU_PARTICLE_EMITTER_EMISSIVE_ANGULAR_VELOCITY = 208;
const int OFFSET_GPU_PARTICLE_EMITTER_ROTATION_PARAMS = 224;
const int OFFSET_GPU_PARTICLE_EMITTER_TIMING_PARAMS = 240;
const int OFFSET_GPU_PARTICLE_EMITTER_VOLUMETRIC_ALBEDO = 256;
const int OFFSET_GPU_PARTICLE_EMITTER_VOLUMETRIC_RADIUS = 272;
const int OFFSET_GPU_PARTICLE_CURVE_SAMPLE_COLOR = 0;
const int OFFSET_GPU_PARTICLE_CURVE_SAMPLE_PROPERTIES = 16;
const int OFFSET_GPU_PARTICLE_STATE_POSITION_AGE = 0;
const int OFFSET_GPU_PARTICLE_STATE_VELOCITY_LIFETIME = 16;
const int OFFSET_GPU_PARTICLE_STATE_COLOR = 32;
const int OFFSET_GPU_PARTICLE_STATE_SIZE_ROTATION = 48;
const int OFFSET_GPU_PARTICLE_STATE_EMITTER_INDEX = 64;
const int OFFSET_GPU_PARTICLE_COUNTERS_ALIVE_COUNT = 0;
const int OFFSET_GPU_PARTICLE_COUNTERS_DEAD_COUNT = 4;
const int OFFSET_GPU_PARTICLE_COUNTERS_RENDERED_COUNT = 20;
const int OFFSET_GPU_PARTICLE_DRAW_COMMAND_VERTEX_COUNT = 0;
const int OFFSET_GPU_PARTICLE_DRAW_COMMAND_INSTANCE_COUNT = 4;
const int OFFSET_GPU_PARTICLE_SORT_KEY_KEY = 0;
const int OFFSET_GPU_PARTICLE_SORT_KEY_INSTANCE_INDEX = 4;
const int OFFSET_GPU_PARTICLE_RESET_PUSH_CURRENT_FRAME_INDEX = 0;
const int OFFSET_GPU_PARTICLE_RESET_PUSH_PARTICLE_CAPACITY = 4;
const int OFFSET_GPU_PARTICLE_RESET_PUSH_DRAW_CAPACITY = 8;
const int OFFSET_GPU_PARTICLE_RESET_PUSH_FLAGS = 12;
const int OFFSET_GPU_PARTICLE_SIMULATE_PUSH_CURRENT_FRAME_INDEX = 0;
const int OFFSET_GPU_PARTICLE_SIMULATE_PUSH_PARTICLE_CAPACITY = 4;
const int OFFSET_GPU_PARTICLE_SIMULATE_PUSH_EMITTER_COUNT = 8;
const int OFFSET_GPU_PARTICLE_SIMULATE_PUSH_DELTA_SECONDS = 16;
const int OFFSET_GPU_PARTICLE_SIMULATE_PUSH_TIME_SECONDS = 20;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_CURRENT_FRAME_INDEX = 0;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_PARTICLE_CAPACITY = 4;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_MODE = 8;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_BUCKET = 12;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_SORT_LEVEL = 16;
const int OFFSET_GPU_PARTICLE_SORT_PUSH_SORT_STAGE = 20;

const int OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX = 0;
const int OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX_INVERSE_TRANSPOSE = 64;
const int OFFSET_GPU_OBJECT_DATA_MESH_INDEX = 128;
const int OFFSET_GPU_OBJECT_DATA_MATERIAL_INDEX = 132;
const int OFFSET_GPU_OBJECT_DATA_SKINNED_VERTEX_OFFSET = 136;
const int OFFSET_GPU_OBJECT_DATA_SKINNING_ENABLED = 140;
const int OFFSET_GPU_OBJECT_DATA_PREVIOUS_WORLD_MATRIX = 144;

const int OFFSET_GPU_MESHLET_BOUNDING_SPHERE_CENTER = 0;
const int OFFSET_GPU_MESHLET_BOUNDING_SPHERE_RADIUS = 12;
const int OFFSET_GPU_MESHLET_VERTEX_OFFSET = 16;
const int OFFSET_GPU_MESHLET_VERTEX_COUNT = 20;
const int OFFSET_GPU_MESHLET_INDEX_OFFSET = 24;
const int OFFSET_GPU_MESHLET_INDEX_COUNT = 28;
const int OFFSET_GPU_MESHLET_LOCAL_VERTEX_OFFSET = 32;
const int OFFSET_GPU_MESHLET_LOCAL_VERTEX_COUNT = 36;
const int OFFSET_GPU_MESHLET_LOCAL_TRIANGLE_OFFSET = 40;
const int OFFSET_GPU_MESHLET_LOCAL_TRIANGLE_COUNT = 44;
const int OFFSET_GPU_MESHLET_NORMAL_CONE_AXIS = 48;
const int OFFSET_GPU_MESHLET_NORMAL_CONE_CUTOFF = 60;

const int OFFSET_GPU_MESHLET_DRAW_COMMAND_MESHLET_INDEX = 0;
const int OFFSET_GPU_MESHLET_DRAW_COMMAND_INSTANCE_ID = 4;
const int OFFSET_GPU_MESHLET_DRAW_COMMAND_MATERIAL_INDEX = 8;
const int OFFSET_GPU_MESHLET_DRAW_COMMAND_FLAGS = 12;

const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MESHLET_INDEX = 0;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_INSTANCE_ID = 4;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MATERIAL_INDEX = 8;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_FLAGS = 12;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS = 16;

const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE0 = 0;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE5 = 80;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_VIEW_PROJECTION_MATRIX = 96;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_INVERSE_VIEW_MATRIX = 160;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_VIEW_PROJECTION_MATRIX = 224;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_INVERSE_VIEW_MATRIX = 288;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_SCREEN_DIMENSIONS = 352;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_PREVIOUS_HIZ_FRAME_VALID = 360;

const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESH_METADATA_INDEX = 0;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_OFFSET = 4;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_COUNT = 8;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD1_OFFSET = 12;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD1_COUNT = 16;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD2_OFFSET = 20;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_LOD2_COUNT = 24;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MATERIAL_INDEX = 28;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_GEOMETRY_MODE = 32;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_FLAGS = 36;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_IMPOSTOR_METADATA_INDEX = 40;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_MESHLET_OUTPUT_CLASS = 44;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_HEIGHT = 48;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_WIDTH = 52;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_LOD_DISTANCES = 56;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_WIND_PARAMS = 72;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_LIGHTING_PARAMS = 88;

const int OFFSET_GPU_FOLIAGE_IMPOSTOR_ALBEDO_OPACITY_TEXTURE_INDEX = 0;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_NORMAL_TEXTURE_INDEX = 4;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_DEPTH_TEXTURE_INDEX = 8;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_COUNT = 12;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_SOURCE_BOUNDS_MIN_SCALE = 16;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_SOURCE_BOUNDS_MAX = 32;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_PIVOT = 48;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_DATA_OFFSET = 60;

const int OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_DIRECTION = 0;
const int OFFSET_GPU_FOLIAGE_IMPOSTOR_VIEW_ATLAS_RECTANGLE = 16;

const int OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MIN_DENSITY = 0;
const int OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MAX_SEED = 16;
const int OFFSET_GPU_FOLIAGE_PATCH_PROTOTYPE_INDEX = 32;
const int OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_OFFSET = 36;
const int OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_COUNT = 40;
const int OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_STABLE_OBJECT_ID = 44;
const int OFFSET_GPU_FOLIAGE_PATCH_SEED = 48;
const int OFFSET_GPU_FOLIAGE_PATCH_FLAGS = 52;
const int OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_STABLE_MATERIAL_ID = 56;
const int OFFSET_GPU_FOLIAGE_PATCH_NEAR_FIELD_PACKED_OBJECT_MATERIAL_REVISIONS = 60;
const int OFFSET_GPU_FOLIAGE_PATCH_DENSITY_TEXTURE_INDEX = 64;
const int OFFSET_GPU_FOLIAGE_PATCH_TERRAIN_DESCRIPTOR_INDEX = 68;
const int OFFSET_GPU_FOLIAGE_PATCH_PLACEMENT_MODE = 72;
const int OFFSET_GPU_FOLIAGE_PATCH_CONTENT_REVISION = 76;
const int OFFSET_GPU_FOLIAGE_PATCH_DENSITY_UV_SCALE_OFFSET = 80;

const int OFFSET_GPU_FOLIAGE_CLUSTER_WORLD_CENTER_RADIUS = 0;
const int OFFSET_GPU_FOLIAGE_CLUSTER_BOUNDS_MIN_DENSITY = 16;
const int OFFSET_GPU_FOLIAGE_CLUSTER_BOUNDS_MAX_LOD = 32;
const int OFFSET_GPU_FOLIAGE_CLUSTER_PATCH_INDEX = 48;
const int OFFSET_GPU_FOLIAGE_CLUSTER_FIRST_INSTANCE = 52;
const int OFFSET_GPU_FOLIAGE_CLUSTER_INSTANCE_COUNT = 56;
const int OFFSET_GPU_FOLIAGE_CLUSTER_RANDOM_SEED = 60;

const int OFFSET_GPU_FOLIAGE_INSTANCE_POSITION_SCALE = 0;
const int OFFSET_GPU_FOLIAGE_INSTANCE_ROTATION_WIND = 16;
const int OFFSET_GPU_FOLIAGE_INSTANCE_COLOR_VARIATION = 32;
const int OFFSET_GPU_FOLIAGE_INSTANCE_PROTOTYPE_INDEX = 48;
const int OFFSET_GPU_FOLIAGE_INSTANCE_PATCH_INDEX = 52;
const int OFFSET_GPU_FOLIAGE_INSTANCE_CLUSTER_INDEX = 56;
const int OFFSET_GPU_FOLIAGE_INSTANCE_FLAGS = 60;

const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_MESHLET_INDEX = 0;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_INSTANCE_INDEX = 4;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_PROTOTYPE_INDEX = 8;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_MATERIAL_INDEX = 12;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS = 16;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_FLAGS = 32;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_LOD_LEVEL = 36;
const int OFFSET_GPU_FOLIAGE_MESHLET_DRAW_COMMAND_CLUSTER_INDEX = 40;

const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_CLUSTER_INDEX = 0;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_LOD_BAND = 4;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_CANDIDATE_COUNT = 8;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_ACTIVE_COUNT = 12;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_DENSITY_FRACTION = 16;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_TRANSITION_FRACTION = 20;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_WIDTH_COMPENSATION = 24;
const int OFFSET_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND_FLAGS = 28;

const int OFFSET_GPU_FOLIAGE_COUNTERS_VISIBLE_CLUSTER_COUNT = 0;
const int OFFSET_GPU_FOLIAGE_COUNTERS_CULLED_CLUSTER_COUNT = 4;
const int OFFSET_GPU_FOLIAGE_COUNTERS_LOD0_VISIBLE_COUNT = 8;
const int OFFSET_GPU_FOLIAGE_COUNTERS_LOD1_VISIBLE_COUNT = 12;
const int OFFSET_GPU_FOLIAGE_COUNTERS_LOD2_VISIBLE_COUNT = 16;
const int OFFSET_GPU_FOLIAGE_COUNTERS_HIZ_TESTED_COUNT = 20;
const int OFFSET_GPU_FOLIAGE_COUNTERS_HIZ_REJECTED_COUNT = 24;
const int OFFSET_GPU_FOLIAGE_COUNTERS_VISIBLE_MESHLET_DRAW_COUNT = 28;
const int OFFSET_GPU_FOLIAGE_COUNTERS_MESHLET_DRAW_OVERFLOW_COUNT = 32;
const int OFFSET_GPU_FOLIAGE_COUNTERS_FAR_IMPOSTOR_VISIBLE_COUNT = 36;
const int OFFSET_GPU_FOLIAGE_COUNTERS_DENSITY_REJECTED_COUNT = 40;
const int OFFSET_GPU_FOLIAGE_COUNTERS_INVALID_COMMAND_COUNT = 44;

const int OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_X = 0;
const int OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_Y = 4;
const int OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_GROUP_COUNT_Z = 8;
const int OFFSET_GPU_FOLIAGE_DISPATCH_ARGS_PADDING0 = 12;

const int OFFSET_GPU_FOLIAGE_CULL_PUSH_CAMERA_POSITION_MAX_DISTANCE = 0;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_CURRENT_FRAME_INDEX = 16;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_CLUSTER_COUNT = 20;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_VISIBLE_CLUSTER_CAPACITY = 24;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_MESHLET_DRAW_CAPACITY = 28;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_INDIRECT_DISPATCH_BUFFER_BASE_INDEX = 32;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_FLAGS = 36;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_AUTHORED_MESHLET_WORK_ITEM_COUNT = 40;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_FIRST_AUTHORED_CLUSTER_INDEX = 44;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_AUTHORED_CLUSTER_COUNT = 48;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_SCREEN_DIMENSIONS = 56;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_HIZ_TEXTURE_INDEX = 64;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_HIZ_MIP_COUNT = 68;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_OCCLUSION_CULLING_ENABLED = 72;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_OCCLUSION_BIAS = 76;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_PREVIOUS_HIZ_FRAME_VALID = 80;
const int OFFSET_GPU_FOLIAGE_CULL_PUSH_PREVIOUS_FRAME_UV_PADDING_PIXELS = 84;

const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CAMERA_POSITION_TIME = 64;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_SCREEN_DIMENSIONS = 80;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CURRENT_FRAME_INDEX = 96;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CLUSTER_DRAW_COUNT = 100;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_VISIBLE_CLUSTER_BUFFER_BASE_INDEX = 104;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_FLAGS = 108;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_DEBUG_VIEW = 112;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_SHADOW_DENSITY_SCALE = 116;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_FIRST_DRAW = 128;

const int OFFSET_GPU_DEPTH_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_DEPTH_PUSH_SCREEN_DIMENSIONS = 64;
const int OFFSET_GPU_DEPTH_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX = 80;

const int OFFSET_GPU_FORWARD_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_FORWARD_PUSH_INVERSE_VIEW_MATRIX = 64;
const int OFFSET_GPU_FORWARD_PUSH_INVERSE_PROJECTION_MATRIX = 128;
const int OFFSET_GPU_FORWARD_PUSH_CAMERA_POSITION = 192;
const int OFFSET_GPU_FORWARD_PUSH_TIME = 204;
const int OFFSET_GPU_FORWARD_PUSH_SCREEN_DIMENSIONS = 208;
const int OFFSET_GPU_FORWARD_PUSH_HIZ_MIP_COUNT = 236;
const int OFFSET_GPU_FORWARD_PUSH_OCCLUSION_CULLING_ENABLED = 240;
const int OFFSET_GPU_FORWARD_PUSH_OCCLUSION_BIAS = 244;
const int OFFSET_GPU_FORWARD_PUSH_DEBUG_AND_AO_FLAGS = 248;
const int OFFSET_GPU_FORWARD_PUSH_DIAGNOSTIC_FLAGS = 252;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_VIEW_PROJECTION_MATRIX = 64;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_SCREEN_DIMENSIONS = 128;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_CURRENT_FRAME_INDEX = 136;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_COUNT = 140;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX = 144;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_FRAME_VALID = 148;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_TIME = 152;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_TIME = 156;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_FIRST_DRAW = 160;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_CAMERA_POSITION = 176;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_CAMERA_POSITION = 192;

const int OFFSET_GPU_LIGHT_CULL_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_LIGHT_CULL_PUSH_INVERSE_VIEW_PROJECTION_MATRIX = 64;
const int OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_POSITION = 128;
const int OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_FORWARD = 144;
const int OFFSET_GPU_LIGHT_CULL_PUSH_SCREEN_DIMENSIONS = 160;
const int OFFSET_GPU_LIGHT_CULL_PUSH_NEAR_PLANE = 168;
const int OFFSET_GPU_LIGHT_CULL_PUSH_FAR_PLANE = 172;
const int OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_COUNT = 176;
const int OFFSET_GPU_LIGHT_CULL_PUSH_TILE_COUNT_Y = 188;
const int OFFSET_GPU_LIGHT_CULL_PUSH_DEPTH_TEXTURE_INDEX = 192;
const int OFFSET_GPU_LIGHT_CULL_PUSH_TOTAL_CLUSTER_COUNT = 200;
const int OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_INDEX_CAPACITY = 204;

const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0 = 0;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION1 = 64;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION2 = 128;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION3 = 192;
const int OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS = 256;
const int OFFSET_GPU_SHADOW_DATA_SETTINGS = 272;
const int OFFSET_GPU_SHADOW_DATA_INDICES = 288;
const int OFFSET_GPU_SHADOW_DATA_CASCADE_TRANSITION_DATA = 304;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_CASCADE_WORLD_TEXEL_SIZES = 0;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_FILTER_AND_BIAS = 16;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_MODE_AND_RAY_DISTANCE = 32;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_TEMPORAL_AND_SAMPLING = 48;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MINIMUM = 64;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MAXIMUM = 80;
const int OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RUNTIME_FLAGS = 96;
const int OFFSET_GPU_SPOT_SHADOW_LIGHT_VIEW_PROJECTION = 0;
const int OFFSET_GPU_SPOT_SHADOW_ATLAS_SCALE_OFFSET = 64;
const int OFFSET_GPU_SPOT_SHADOW_BIAS_STRENGTH_TEXEL_SIZE = 80;
const int OFFSET_GPU_SPOT_SHADOW_LIGHT_INDEX = 96;
const int OFFSET_GPU_POINT_SHADOW_FACE_VIEW_PROJECTION0 = 0;
const int OFFSET_GPU_POINT_SHADOW_POSITION_RANGE = 384;
const int OFFSET_GPU_POINT_SHADOW_BIAS_STRENGTH_TEXEL_SIZE = 400;
const int OFFSET_GPU_POINT_SHADOW_LIGHT_INDEX = 416;
const int OFFSET_GPU_ENVIRONMENT_TEXTURE_INDEX = 0;
const int OFFSET_GPU_ENVIRONMENT_SKY_INTENSITY = 16;
const int OFFSET_GPU_ENVIRONMENT_PREFILTERED_MIP_COUNT = 32;
const int OFFSET_GPU_REFLECTION_PROBE_WORLD_TO_PROBE = 0;
const int OFFSET_GPU_REFLECTION_PROBE_POSITION_AND_RADIUS = 64;
const int OFFSET_GPU_REFLECTION_PROBE_BOX_MIN = 80;
const int OFFSET_GPU_REFLECTION_PROBE_BOX_MAX = 96;
const int OFFSET_GPU_REFLECTION_PROBE_BLEND_PARAMS = 112;
const int OFFSET_GPU_REFLECTION_PROBE_CUBEMAP_ARRAY_INDEX = 128;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX = 0;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X = 16;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y = 32;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z = 48;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS = 64;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_DEBUG_COLOR_AND_FLAGS = 80;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND = 96;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE = 112;
const int OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_BLEND_AND_FLAGS = 128;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PROBE_INDEX = 0;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_VOLUME_INDEX = 4;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FLAGS = 8;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PRIORITY = 12;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_X = 16;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Y = 20;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Z = 24;
const int OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FRAME_SERIAL = 28;
const uint FAR_FIELD_COUNTER_BASE = 99u;
const uint FAR_FIELD_RAY_COUNTER = FAR_FIELD_COUNTER_BASE + 0u;
const uint FAR_FIELD_HIT_COUNTER = FAR_FIELD_COUNTER_BASE + 1u;
const uint FAR_FIELD_STEP_EXHAUSTED_COUNTER = FAR_FIELD_COUNTER_BASE + 2u;
const uint FAR_FIELD_BAKED_TRIANGLE_COUNTER = FAR_FIELD_COUNTER_BASE + 3u;
const uint FAR_FIELD_OCCUPIED_VOXEL_WRITE_COUNTER = FAR_FIELD_COUNTER_BASE + 4u;
const uint FAR_FIELD_STEP_BUCKET_0_COUNTER = FAR_FIELD_COUNTER_BASE + 5u;
const uint FAR_FIELD_STEP_BUCKET_1_COUNTER = FAR_FIELD_COUNTER_BASE + 6u;
const uint FAR_FIELD_STEP_BUCKET_2_COUNTER = FAR_FIELD_COUNTER_BASE + 7u;
const uint FAR_FIELD_STEP_BUCKET_3_COUNTER = FAR_FIELD_COUNTER_BASE + 8u;
const uint FAR_FIELD_STEP_BUCKET_4_COUNTER = FAR_FIELD_COUNTER_BASE + 9u;
const uint DDGI_INVESTIGATION_COUNTER_BASE = FAR_FIELD_COUNTER_BASE + 10u;
const uint DDGI_INVESTIGATION_SIMPLE_FORWARD_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 0u;
const uint DDGI_INVESTIGATION_LEGACY_FORWARD_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 1u;
const uint DDGI_INVESTIGATION_FRESH_ATLAS_FORWARD_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 2u;
const uint DDGI_INVESTIGATION_SIMPLE_ZERO_IRRADIANCE_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 3u;
const uint DDGI_INVESTIGATION_SIMPLE_NONZERO_IRRADIANCE_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 4u;
const uint DDGI_INVESTIGATION_SIMPLE_IRRADIANCE_LUMINANCE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 5u;
const uint DDGI_INVESTIGATION_SIMPLE_VISIBILITY_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 6u;
const uint DDGI_INVESTIGATION_SIMPLE_LOW_VISIBILITY_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 7u;
const uint DDGI_INVESTIGATION_FORWARD_ZERO_FINAL_INDIRECT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 8u;
const uint DDGI_INVESTIGATION_FORWARD_ZERO_DDGI_NONZERO_IBL_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 9u;
const uint DDGI_INVESTIGATION_FORWARD_ZERO_DDGI_ZERO_IBL_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 10u;
const uint DDGI_INVESTIGATION_FORWARD_OUT_OF_GRID_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 11u;
const uint DDGI_INVESTIGATION_FORWARD_CLAMPED_PROBE_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 12u;
const uint DDGI_INVESTIGATION_FORWARD_NONFINITE_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 13u;
const uint DDGI_INVESTIGATION_IRRADIANCE_ZERO_TEXEL_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 14u;
const uint DDGI_INVESTIGATION_VISIBILITY_ZERO_MOMENT_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 15u;
const uint DDGI_INVESTIGATION_ATLAS_WRITE_PROBE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 16u;
const uint DDGI_INVESTIGATION_ATLAS_WRITE_TEXEL_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 17u;
const uint DDGI_INVESTIGATION_BLEND_ZERO_RAY_WEIGHT_PROBE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 18u;
const uint DDGI_INVESTIGATION_BLEND_NONZERO_IRRADIANCE_PROBE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 19u;
const uint DDGI_INVESTIGATION_BLEND_PREVIOUS_ATLAS_USED_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 20u;
const uint DDGI_INVESTIGATION_BLEND_HYSTERESIS_ZERO_FRAME_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 21u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_HIT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 22u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_MISS_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 23u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_ZERO_RADIANCE_HIT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 24u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_DIRECT_LIGHT_HIT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 25u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_EMISSIVE_HIT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 26u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_FAR_FIELD_HIT_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 27u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_FAR_FIELD_MISS_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 28u;
const uint DDGI_INVESTIGATION_SIMPLE_TRACE_TLAS_UNAVAILABLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 29u;
const uint DDGI_INVESTIGATION_SKY_VISIBILITY_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 30u;
const uint DDGI_INVESTIGATION_SKY_VISIBILITY_ACCUM_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 31u;
const uint DDGI_INVESTIGATION_FAR_SUN_SHADOW_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 32u;
const uint DDGI_INVESTIGATION_FAR_SUN_SHADOW_OCCLUDED_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 33u;
const uint DDGI_INVESTIGATION_ROUGH_SPECULAR_SAMPLE_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 34u;
const uint DDGI_INVESTIGATION_ROUGH_SPECULAR_NONZERO_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 35u;
const uint DDGI_INVESTIGATION_SIMPLE_GATHER_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 36u;
const uint DDGI_INVESTIGATION_SIMPLE_SECOND_VOLUME_GATHER_COUNTER = DDGI_INVESTIGATION_COUNTER_BASE + 37u;
const uint DDGI_INVESTIGATION_SIMPLE_VOLUME_PRIMARY_GATHER_COUNTER_BASE = DDGI_INVESTIGATION_COUNTER_BASE + 38u;
const uint DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE = DDGI_INVESTIGATION_SIMPLE_VOLUME_PRIMARY_GATHER_COUNTER_BASE + 16u;
// Appended V2 transport telemetry. Keep this after the fixed investigation
// range so legacy counter ABI offsets remain unchanged.
const uint SIMPLE_DDGI_TRANSPORT_COUNTER_BASE = DDGI_INVESTIGATION_SIMPLE_VOLUME_SAMPLED_GATHER_COUNTER_BASE + 16u;
const uint SIMPLE_DDGI_TRANSPORT_SAMPLE_COUNT_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_HIT_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_MISS_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_TRANSPORT_BOUNCE_LUMINANCE_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_TRANSPORT_SOURCE_LUMINANCE_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_TRANSPORT_TOTAL_LUMINANCE_COUNTER = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 5u;
// Sparse forward receiver telemetry for directional shadows. Keep this family
// appended so prior renderer-diagnostic counter offsets remain stable.
const uint DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE = SIMPLE_DDGI_TRANSPORT_COUNTER_BASE + 6u;
const uint DIRECTIONAL_SHADOW_RECEIVER_CASCADE_COUNT = 4u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_SELECTION_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 0u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PROJECTION_REJECT_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 4u;
const uint DIRECTIONAL_SHADOW_RECEIVER_UV_DEPTH_REJECT_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 8u;
const uint DIRECTIONAL_SHADOW_RECEIVER_FALLBACK_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 12u;
const uint DIRECTIONAL_SHADOW_RECEIVER_TRANSITION_BLEND_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 16u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_RESOLVED_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 20u;
const uint DIRECTIONAL_SHADOW_RECEIVER_CLEAR_DEPTH_FOOTPRINT_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 24u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_FULLY_LIT_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 28u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_PARTIAL_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 32u;
const uint DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_FULLY_SHADOWED_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 36u;
const uint DIRECTIONAL_SHADOW_RECEIVER_FINAL_FULLY_LIT_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 40u;
const uint DIRECTIONAL_SHADOW_RECEIVER_FINAL_PARTIAL_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 44u;
const uint DIRECTIONAL_SHADOW_RECEIVER_FINAL_FULLY_SHADOWED_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 48u;
const uint DIRECTIONAL_SHADOW_RECEIVER_RECEIVER_DEPTH_SUM_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 52u;
const uint DIRECTIONAL_SHADOW_RECEIVER_MIN_SAMPLED_DEPTH_SUM_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 56u;
const uint DIRECTIONAL_SHADOW_RECEIVER_MAX_SAMPLED_DEPTH_SUM_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 60u;
const uint DIRECTIONAL_SHADOW_RECEIVER_UNRESOLVED_COUNTER = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 64u;
const uint FAR_FIELD_MATERIAL_V2_COUNTER_BASE = DIRECTIONAL_SHADOW_RECEIVER_COUNTER_BASE + 65u;
const uint FAR_FIELD_MATERIAL_CONFLICT_COUNTER = FAR_FIELD_MATERIAL_V2_COUNTER_BASE + 0u;
const uint FAR_FIELD_MATERIAL_STALE_PUBLICATION_COUNTER = FAR_FIELD_MATERIAL_V2_COUNTER_BASE + 1u;
const uint MATERIAL_GI_COUNTER_BASE = FAR_FIELD_MATERIAL_V2_COUNTER_BASE + 2u;
const uint MATERIAL_GI_ALPHA_CANDIDATE_TEST_COUNTER = MATERIAL_GI_COUNTER_BASE + 0u;
const uint MATERIAL_GI_ALPHA_CANDIDATE_REJECT_COUNTER = MATERIAL_GI_COUNTER_BASE + 1u;
const uint MATERIAL_GI_NONFINITE_VALUE_COUNTER = MATERIAL_GI_COUNTER_BASE + 2u;
const uint MATERIAL_GI_CLAMPED_VALUE_COUNTER = MATERIAL_GI_COUNTER_BASE + 3u;
const uint MATERIAL_GI_ALPHA_CANDIDATE_LIMIT_COUNTER = MATERIAL_GI_COUNTER_BASE + 4u;
const uint MATERIAL_GI_DETAILED_TRANSPORT_HIT_COUNTER = MATERIAL_GI_COUNTER_BASE + 5u;
const uint MATERIAL_GI_COMPACT_TRANSPORT_HIT_COUNTER = MATERIAL_GI_COUNTER_BASE + 6u;
const uint MATERIAL_GI_CORRECTNESS_FALLBACK_HIT_COUNTER = MATERIAL_GI_COUNTER_BASE + 7u;
const uint MATERIAL_GI_FAR_FIELD_TRANSPORT_HIT_COUNTER = MATERIAL_GI_COUNTER_BASE + 8u;
const uint MATERIAL_GI_EMISSIVE_SAMPLING_INVOCATION_COUNTER = MATERIAL_GI_COUNTER_BASE + 9u;
// Appended diagnostic ABI. Keep synchronized with RendererDiagnosticsBuffer;
// existing counter families above retain their established capture offsets.
const uint DDGI_DELIVERY_FAILURE_COUNTER_BASE = 295u;
const uint DDGI_HIGH_OWNERSHIP_LOW_DELIVERED_INDIRECT_COUNTER =
    DDGI_DELIVERY_FAILURE_COUNTER_BASE + 0u;
const uint DDGI_SHADOW_VISIBILITY_COUNTER_BASE = 296u;
const uint DDGI_SHADOW_VISIBILITY_RAY_COUNTER = DDGI_SHADOW_VISIBILITY_COUNTER_BASE + 0u;
const uint DDGI_SHADOW_VISIBILITY_OCCLUDED_COUNTER = DDGI_SHADOW_VISIBILITY_COUNTER_BASE + 1u;
const uint DDGI_SHADOW_VISIBILITY_NEAR_HIT_COUNTER = DDGI_SHADOW_VISIBILITY_COUNTER_BASE + 2u;
const uint DDGI_SHADOW_VISIBILITY_HIT_DISTANCE_COUNTER = DDGI_SHADOW_VISIBILITY_COUNTER_BASE + 3u;
const uint DDGI_THIN_TRANSPORT_COUNTER_BASE = 306u;
const uint DDGI_THIN_DETAILED_HIT_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 0u;
const uint DDGI_THIN_COMPACT_HIT_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 1u;
const uint DDGI_THIN_FAR_FIELD_EXCLUDED_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 2u;
const uint DDGI_THIN_REFLECTED_DIRECT_LUMINANCE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 3u;
const uint DDGI_THIN_TRANSMITTED_DIRECT_LUMINANCE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 4u;
const uint DDGI_THIN_REFLECTED_RECURSIVE_LUMINANCE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 5u;
const uint DDGI_THIN_TRANSMITTED_RECURSIVE_LUMINANCE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 6u;
const uint DDGI_THIN_SHADOW_TRANSMISSION_RAY_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 7u;
const uint DDGI_THIN_SHADOW_TOTAL_LAYER_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 8u;
const uint DDGI_THIN_SHADOW_MAX_LAYER_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 9u;
const uint DDGI_THIN_SHADOW_LAYER_LIMIT_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 10u;
const uint DDGI_THIN_SHADOW_LOW_TRANSMITTANCE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 11u;
const uint DDGI_THIN_ZERO_RADIANCE_OPAQUE_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 12u;
const uint DDGI_THIN_ZERO_RADIANCE_THIN_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 13u;
const uint DDGI_THIN_ZERO_RADIANCE_UNSUPPORTED_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 14u;
const uint DDGI_THIN_UNSUPPORTED_TRANSMISSION_HIT_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 15u;
const uint DDGI_THIN_ENERGY_CLAMP_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 16u;
const uint DDGI_THIN_INVALID_TRANSMISSION_COUNTER = DDGI_THIN_TRANSPORT_COUNTER_BASE + 17u;
// Appended after the 16 per-volume energy banks (324 + 16 * 19).
// Every albedo class is a packed luminance sum followed by a sample count.
const uint DDGI_ALBEDO_COUNTER_BASE = 628u;
const uint DDGI_RECEIVER_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 0u;
const uint DDGI_RECEIVER_ALBEDO_SAMPLE_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 1u;
const uint DDGI_TRACE_ONE_SIDED_BACKFACE_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 2u;
const uint DDGI_TRACE_ONE_SIDED_BACKFACE_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 3u;
const uint DDGI_TRACE_OPAQUE_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 4u;
const uint DDGI_TRACE_OPAQUE_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 5u;
const uint DDGI_TRACE_THIN_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 6u;
const uint DDGI_TRACE_THIN_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 7u;
const uint DDGI_TRACE_UNSUPPORTED_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 8u;
const uint DDGI_TRACE_UNSUPPORTED_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 9u;
const uint DDGI_TRACE_REFLECT_DISABLED_ALBEDO_LUMINANCE_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 10u;
const uint DDGI_TRACE_REFLECT_DISABLED_COUNT_COUNTER = DDGI_ALBEDO_COUNTER_BASE + 11u;
// Mutually exclusive forward-gather attribution, appended after the albedo ABI.
const uint SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE = DDGI_ALBEDO_COUNTER_BASE + 12u;
const uint SIMPLE_DDGI_ONE_GATHER_PIXEL_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_TWO_GATHER_PIXEL_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_RECOVERY_GATHER_PIXEL_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_SECOND_GATHER_RING_TRANSITION_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_SECOND_GATHER_MISSING_INVALID_PRIMARY_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_SECOND_GATHER_RECOVERY_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 5u;
const uint SIMPLE_DDGI_SECOND_GATHER_COVERAGE_EDGE_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_SECOND_GATHER_OWNERSHIP_BELOW_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 7u;
const uint SIMPLE_DDGI_SECOND_GATHER_DEBUG_ONLY_COUNTER = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 8u;
// Sparse 16x16 geometry-decal fragment attribution. Values are accumulated
// with weight 256 and exported as sampled full-frame estimates.
const uint DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE = SIMPLE_DDGI_GATHER_MULTIPLICITY_COUNTER_BASE + 9u;
const uint DECAL_ESTIMATED_INVOCATION_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 0u;
const uint DECAL_ESTIMATED_BACKFACE_KILLED_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 1u;
const uint DECAL_ESTIMATED_COVERAGE_KILLED_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 2u;
const uint DECAL_ESTIMATED_SURVIVING_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 3u;
const uint DECAL_ESTIMATED_DDGI_GATHER_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 4u;
const uint DECAL_ESTIMATED_SHADOW_EVALUATION_COUNTER = DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 5u;
// Detailed-only packed-storage and compact-mirror qualification counters.
// Keep synchronized with RendererDiagnosticsBuffer. Existing offsets remain
// stable because this family is appended after decal attribution.
const uint SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE =
    DECAL_FRAGMENT_ATTRIBUTION_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_MIRROR_INTERIOR_OPPORTUNITY_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_MIRROR_IMAGE_HIT_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_MIRROR_SEAM_FALLBACK_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_MIRROR_UNMIRRORED_FALLBACK_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_MIRROR_INVALID_MAP_FALLBACK_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_CACHE_PACK_ATTEMPT_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 5u;
const uint SIMPLE_DDGI_CACHE_PACK_NONFINITE_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_CACHE_PACK_RADIANCE_SATURATION_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 7u;
const uint SIMPLE_DDGI_CACHE_PACK_MAX_RADIANCE_ERROR_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 8u;
const uint SIMPLE_DDGI_CACHE_PACK_MAX_DISTANCE_ERROR_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 9u;
const uint SIMPLE_DDGI_DIRECTION_COMPARE_SAMPLE_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 10u;
const uint SIMPLE_DDGI_DIRECTION_EPOCH_MISMATCH_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 11u;
const uint SIMPLE_DDGI_DIRECTION_MAX_ANGULAR_ERROR_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 12u;
const uint SIMPLE_DDGI_DIRECTION_ANGULAR_HISTOGRAM_BASE =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 13u;
const uint SIMPLE_DDGI_DIRECTION_ANGULAR_HISTOGRAM_COUNT = 8u;
const uint SIMPLE_DDGI_INVALID_SOURCE_EPOCH_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 21u;
const uint SIMPLE_DDGI_INVALID_HIT_KIND_COUNTER =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 22u;
// Detailed-only per-volume energy distribution and coherent maximum witness.
// Keep synchronized with RendererDiagnosticsBuffer. Existing offsets remain
// stable because this family is appended after storage validation.
const uint SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_BASE =
    SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE + 23u;
const uint SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_HISTOGRAM_COUNT = 16u;
const uint SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_STRIDE =
    23u + SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_HISTOGRAM_COUNT;
const uint SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_VOLUME_COUNT = 16u;
const uint SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_COUNT =
    SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_VOLUME_COUNT *
    SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_STRIDE;
// Bounded exact caster-attribution bank. Keep this appended ABI synchronized
// with RendererDiagnosticsBuffer; it is written only by the dedicated
// directional-shadow diagnostic compaction pipeline.
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE =
    SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_BASE +
    SIMPLE_DDGI_VOLUME_ENERGY_EVIDENCE_COUNTER_COUNT;
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_HEADER_WORD_COUNT = 7u;
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_FRAME_METADATA_MAGIC = 0x44534346u;
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_CAPACITY = 16u;
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_STRIDE = 28u;
const uint DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_COUNT =
    DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_HEADER_WORD_COUNT +
    DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_CAPACITY *
    DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_RECORD_STRIDE;
const uint DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE =
    DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_BASE +
    DIRECTIONAL_SHADOW_CASTER_DIAGNOSTIC_COUNTER_COUNT;
const uint DDGI_TRANSPARENT_VISIBILITY_LAYER_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 0u;
const uint DDGI_TRANSPARENT_VISIBILITY_LIMIT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 1u;
const uint DDGI_DECAL_CANDIDATE_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 2u;
const uint DDGI_DECAL_RETAINED_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 3u;
const uint DDGI_DECAL_ASSOCIATED_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 4u;
const uint DDGI_DECAL_DEPTH_REJECT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 5u;
const uint DDGI_DECAL_FACING_REJECT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 6u;
const uint DDGI_DECAL_CANDIDATE_LIMIT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 7u;
const uint DDGI_FOLIAGE_PROXY_HIT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 8u;
const uint DDGI_RAY_METADATA_INVALID_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 9u;
const uint DDGI_STOCHASTIC_ALPHA_ACCEPT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 10u;
const uint DDGI_STOCHASTIC_ALPHA_REJECT_COUNTER =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 11u;
const uint DDGI_MANY_LIGHT_COUNTER_BASE =
    DDGI_GEOMETRY_PARTICIPATION_COUNTER_BASE + 12u;
const uint DDGI_MANY_LIGHT_BYPASS_HIT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 0u;
const uint DDGI_MANY_LIGHT_EXACT_HIT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 1u;
const uint DDGI_MANY_LIGHT_TREE_ATTEMPT_HIT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 2u;
const uint DDGI_MANY_LIGHT_TREE_SUCCESS_HIT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 3u;
const uint DDGI_MANY_LIGHT_TREE_FALLBACK_HIT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 4u;
const uint DDGI_MANY_LIGHT_SAMPLED_LIGHT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 5u;
const uint DDGI_MANY_LIGHT_DUPLICATE_DRAW_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 6u;
const uint DDGI_MANY_LIGHT_VISIBILITY_EVALUATION_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 7u;
const uint DDGI_MANY_LIGHT_REJECTED_ZERO_TERM_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 8u;
const uint DDGI_MANY_LIGHT_UNIFORM_REPAIR_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 9u;
const uint DDGI_MANY_LIGHT_INVALID_SAMPLE_PDF_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 10u;
const uint DDGI_MANY_LIGHT_PDF_SUM_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 11u;
const uint DDGI_MANY_LIGHT_NEGATIVE_LOG2_PDF_SUM_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 12u;
const uint DDGI_MANY_LIGHT_MAX_NEGATIVE_LOG2_PDF_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 13u;
const uint DDGI_MANY_LIGHT_MAX_ESTIMATOR_WEIGHT_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 14u;
const uint DDGI_MANY_LIGHT_EXACT_LIGHT_EVALUATION_COUNTER =
    DDGI_MANY_LIGHT_COUNTER_BASE + 15u;
// B4 producer and receiver dispositions. Appended so every established
// renderer-diagnostic offset remains capture-compatible.
const uint SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE =
    DDGI_MANY_LIGHT_COUNTER_BASE + 16u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_COHERENT_CLUSTER_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_REJECTED_CLUSTER_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_INSUFFICIENT_CONFIDENCE_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_INVALID_DEPTH_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_NO_DISCREPANCY_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_RECEIVER_IN_FRONT_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 5u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_APPLIED_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_EVALUATION_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 7u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_CLAMP_SUM_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 8u;
const uint SIMPLE_DDGI_NEAR_VISIBILITY_CLAMP_MAX_COUNTER =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 9u;
// Fence-complete counters written only by the bounded DDGI debug vertex pass.
// Keep synchronized with RendererDiagnosticsBuffer.DebugDdgiOverlayCounterBase.
const uint DEBUG_DDGI_OVERLAY_COUNTER_BASE =
    SIMPLE_DDGI_NEAR_VISIBILITY_COUNTER_BASE + 10u;
const uint DEBUG_DDGI_OVERLAY_MODE_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 0u;
const uint DEBUG_DDGI_OVERLAY_DRAWN_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 1u;
const uint DEBUG_DDGI_OVERLAY_FILTERED_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 2u;
const uint DEBUG_DDGI_OVERLAY_NONRESIDENT_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 3u;
const uint DEBUG_DDGI_OVERLAY_STALE_MAPPING_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 4u;
const uint DEBUG_DDGI_OVERLAY_STATE_UNAVAILABLE_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 5u;
const uint DEBUG_DDGI_OVERLAY_INVALID_TRANSACTION_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 6u;
const uint DEBUG_DDGI_OVERLAY_MULTI_REASON_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 7u;
const uint DEBUG_DDGI_OVERLAY_REASON_COUNTER_BASE =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 8u;
const uint DEBUG_DDGI_OVERLAY_VOLUME_GENERATION_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 24u;
const uint DEBUG_DDGI_OVERLAY_SCHEDULER_GENERATION_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 25u;
const uint DEBUG_DDGI_OVERLAY_RESIDENCY_GENERATION_COUNTER =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 26u;
// Required frame-local runtime admission state, appended after all existing
// diagnostic families so established capture offsets remain stable.
const uint THICK_TRANSMISSION_COUNTER_BASE =
    DEBUG_DDGI_OVERLAY_COUNTER_BASE + 27u;
const uint THICK_TRANSMISSION_TASK_COUNTER =
    THICK_TRANSMISSION_COUNTER_BASE + 0u;
const uint DDGI_AREA_LIGHT_COUNTER_BASE =
    THICK_TRANSMISSION_COUNTER_BASE + 1u;
const uint DDGI_AREA_LIGHT_SAMPLE_ATTEMPT_COUNTER =
    DDGI_AREA_LIGHT_COUNTER_BASE + 0u;
const uint DDGI_AREA_LIGHT_SAMPLE_ACCEPT_COUNTER =
    DDGI_AREA_LIGHT_COUNTER_BASE + 1u;
const uint DDGI_AREA_LIGHT_INVALID_PDF_COUNTER =
    DDGI_AREA_LIGHT_COUNTER_BASE + 2u;
const uint DDGI_AREA_LIGHT_VISIBILITY_RAY_COUNTER =
    DDGI_AREA_LIGHT_COUNTER_BASE + 3u;
const uint TRANSPARENT_REFLECTION_COUNTER_BASE =
    DDGI_AREA_LIGHT_COUNTER_BASE + 4u;
const uint TRANSPARENT_REFLECTION_TASK_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 0u;
const uint TRANSPARENT_REFLECTION_SSR_HIT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 1u;
const uint TRANSPARENT_REFLECTION_RAY_HIT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 2u;
const uint TRANSPARENT_REFLECTION_RAY_MISS_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 3u;
const uint TRANSPARENT_REFLECTION_BUDGET_REJECT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 4u;
const uint TRANSPARENT_REFLECTION_DDGI_FALLBACK_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 5u;
const uint TRANSPARENT_REFLECTION_PROBE_FALLBACK_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 6u;
const uint TRANSPARENT_REFLECTION_ENVIRONMENT_FALLBACK_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 7u;
const uint TRANSPARENT_REFLECTION_SSR_ELIGIBLE_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 8u;
const uint TRANSPARENT_REFLECTION_SSR_ADMITTED_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 9u;
const uint TRANSPARENT_REFLECTION_SSR_RESERVED_SAMPLE_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 10u;
const uint TRANSPARENT_REFLECTION_SSR_ACTUAL_SAMPLE_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 11u;
const uint TRANSPARENT_REFLECTION_SSR_EXACT_HIT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 12u;
const uint TRANSPARENT_REFLECTION_SSR_BUDGET_REJECT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 13u;
const uint TRANSPARENT_REFLECTION_RAY_ADMITTED_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 14u;
const uint TRANSPARENT_REFLECTION_RAY_EXACT_BUDGET_REJECT_COUNTER =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 15u;
const uint TRANSPARENT_REFLECTION_SSR_ALLOCATION_CURSOR =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 16u;
const uint TRANSPARENT_REFLECTION_RAY_ALLOCATION_CURSOR =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 17u;
// Qualification-only surface-aware receiver-cache evidence. Production timing
// artifacts compile every write out; dedicated diagnostic artifacts set the
// active marker and populate this appended, fence-complete family.
const uint SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE =
    TRANSPARENT_REFLECTION_COUNTER_BASE + 18u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_DIAGNOSTIC_ACTIVE_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 0u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_CANDIDATE_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 1u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_VALID_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 2u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_INVALID_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 3u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_DEPTH_POSITION_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 4u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_PLANE_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 5u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_NORMAL_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 6u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_RESOLVE_SUPPORT_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 7u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_CANDIDATE_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 8u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_ACCEPTED_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 9u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_INVALID_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 10u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_DEPTH_POSITION_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 11u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_PLANE_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 12u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_NORMAL_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 13u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_FORWARD_SUPPORT_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 14u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_EXACT_FALLBACK_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 15u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_LEGACY_FRAGMENT_COUNTER =
    SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_BASE + 16u;
const uint SIMPLE_DDGI_RECEIVER_CACHE_COUNTER_COUNT = 17u;
const float SIMPLE_DDGI_NEAR_VISIBILITY_CLAMP_SUM_SCALE = 256.0;
const float SIMPLE_DDGI_NEAR_VISIBILITY_CLAMP_MAX_SCALE = 65535.0;
const float DDGI_MANY_LIGHT_PDF_SCALE = 1048576.0;
const float DDGI_MANY_LIGHT_LOG_PDF_SCALE = 1024.0;
const float DDGI_MANY_LIGHT_ESTIMATOR_WEIGHT_SCALE = 1024.0;
const float DDGI_THIN_LUMINANCE_SCALE = 4096.0;
const float DDGI_SHADOW_VISIBILITY_HIT_DISTANCE_SCALE = 256.0;
const float DIRECTIONAL_SHADOW_RECEIVER_DEPTH_QUANTIZATION_SCALE = 65535.0;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_ABI_VERSION = 0;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_GEOMETRY_CLASS = 4;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_GEOMETRY_FLAGS = 8;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_STABLE_INSTANCE_IDENTITY = 12;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_BUFFER_INDEX = 16;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_OFFSET = 20;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_STRIDE = 24;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_FORMAT = 28;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_BUFFER_INDEX = 56;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_OFFSET = 60;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_MATERIAL_INDEX = 68;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_REPRESENTATION_GENERATION = 92;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_WORLD_MATRIX_INVERSE_TRANSPOSE = 96;


const uint MESHLET_MAX_VERTICES = 64u;
const uint MESHLET_MAX_TRIANGLES = 126u;
const uint MESHLET_TASK_GROUP_SIZE = 1u;

#ifndef NJULF_GPU_DIAGNOSTIC_COUNTERS
#define NJULF_GPU_DIAGNOSTIC_COUNTERS 0
#endif

#ifndef NJULF_DDGI_DETAILED_COUNTERS
#define NJULF_DDGI_DETAILED_COUNTERS 0
#endif

#ifndef NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
#define NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS 0
#endif

// Directional-shadow traversal/filter counters are investigation-only.  Keep
// their compile-time switch independent of DDGI so production shaders contain
// no hidden diagnostic atomics even when the runtime counter flag is false.
#ifndef NJULF_DIRECTIONAL_SHADOW_DETAILED_COUNTERS
#define NJULF_DIRECTIONAL_SHADOW_DETAILED_COUNTERS 0
#endif

uint ReadStorageWord(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset];
}

// Most renderer parameter/state descriptors are selected from push constants or
// immutable parameter blocks and are therefore dynamically uniform for the
// entire draw/dispatch.  Keep that contract explicit: decorating those indices
// as non-uniform forces some drivers to emit a descriptor-waterfall loop even
// though every lane addresses the same descriptor.
uint ReadStorageWordUniform(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageBuffers[bufferIndex].Words[wordOffset];
}

float ReadStorageFloatUniform(uint bufferIndex, uint wordOffset)
{
    return uintBitsToFloat(ReadStorageWordUniform(bufferIndex, wordOffset));
}

uvec4 ReadStorageUVec4(uint bufferIndex, uint wordOffset)
{
    return uvec4(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset + 0u],
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset + 1u],
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset + 2u],
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset + 3u]);
}

uvec4 ReadStorageUVec4Uniform(uint bufferIndex, uint wordOffset)
{
    // A general word offset cannot safely be reconstructed from two aligned
    // vector loads: the second load can cross the descriptor range for the last
    // record in a tightly packed buffer. Keep the uniform-descriptor benefit
    // here and reserve true 128-bit loads for the aligned accessor below.
    return uvec4(
        ReadStorageWordUniform(bufferIndex, wordOffset + 0u),
        ReadStorageWordUniform(bufferIndex, wordOffset + 1u),
        ReadStorageWordUniform(bufferIndex, wordOffset + 2u),
        ReadStorageWordUniform(bufferIndex, wordOffset + 3u));
}

// wordOffset must be four-word aligned. Keep this separate from the general
// accessor so hot structure headers compile to one unconditional vector load.
uvec4 ReadStorageAlignedUVec4Uniform(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageVectorBuffers[bufferIndex].Vectors[wordOffset >> 2u];
}

// wordOffset must be two-word aligned.
uvec2 ReadStorageAlignedUVec2Uniform(uint bufferIndex, uint wordOffset)
{
    return BindlessStoragePairBuffers[bufferIndex].Pairs[wordOffset >> 1u];
}

void WriteStorageWord(uint bufferIndex, uint wordOffset, uint value)
{
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset] = value;
}

void WriteStorageWordUniform(uint bufferIndex, uint wordOffset, uint value)
{
    BindlessStorageBuffers[bufferIndex].Words[wordOffset] = value;
}

void WriteStorageFloat(uint bufferIndex, uint wordOffset, float value)
{
    WriteStorageWord(bufferIndex, wordOffset, floatBitsToUint(value));
}

void WriteStorageFloatUniform(uint bufferIndex, uint wordOffset, float value)
{
    WriteStorageWordUniform(bufferIndex, wordOffset, floatBitsToUint(value));
}

void WriteStorageVec4(uint bufferIndex, uint wordOffset, vec4 value)
{
    WriteStorageFloat(bufferIndex, wordOffset + 0u, value.x);
    WriteStorageFloat(bufferIndex, wordOffset + 1u, value.y);
    WriteStorageFloat(bufferIndex, wordOffset + 2u, value.z);
    WriteStorageFloat(bufferIndex, wordOffset + 3u, value.w);
}

void WriteStorageVec4Uniform(uint bufferIndex, uint wordOffset, vec4 value)
{
    WriteStorageFloatUniform(bufferIndex, wordOffset + 0u, value.x);
    WriteStorageFloatUniform(bufferIndex, wordOffset + 1u, value.y);
    WriteStorageFloatUniform(bufferIndex, wordOffset + 2u, value.z);
    WriteStorageFloatUniform(bufferIndex, wordOffset + 3u, value.w);
}

void IncrementRendererDiagnostic(uint frameIndex, uint counterIndex)
{
#if NJULF_GPU_DIAGNOSTIC_COUNTERS
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], 1u);
#endif
}

void AddRendererDiagnostic(uint frameIndex, uint counterIndex, uint value)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], value);
#endif
}

void IncrementRendererDiagnosticOptional(uint frameIndex, uint counterIndex)
{
#if NJULF_GPU_DIAGNOSTIC_COUNTERS
    IncrementRendererDiagnostic(frameIndex, counterIndex);
#endif
}

float ReadStorageFloat(uint bufferIndex, uint wordOffset)
{
    return uintBitsToFloat(ReadStorageWord(bufferIndex, wordOffset));
}

vec2 ReadStorageVec2(uint bufferIndex, uint wordOffset)
{
    return vec2(
        ReadStorageFloat(bufferIndex, wordOffset + 0u),
        ReadStorageFloat(bufferIndex, wordOffset + 1u));
}

vec3 ReadStorageVec3(uint bufferIndex, uint wordOffset)
{
    return vec3(
        ReadStorageFloat(bufferIndex, wordOffset + 0u),
        ReadStorageFloat(bufferIndex, wordOffset + 1u),
        ReadStorageFloat(bufferIndex, wordOffset + 2u));
}

vec4 ReadStorageVec4(uint bufferIndex, uint wordOffset)
{
    return uintBitsToFloat(ReadStorageUVec4(bufferIndex, wordOffset));
}

vec4 ReadStorageVec4Uniform(uint bufferIndex, uint wordOffset)
{
    return uintBitsToFloat(ReadStorageUVec4Uniform(bufferIndex, wordOffset));
}

vec4 ReadStorageAlignedVec4Uniform(uint bufferIndex, uint wordOffset)
{
    return uintBitsToFloat(
        ReadStorageAlignedUVec4Uniform(bufferIndex, wordOffset));
}

mat4 ReadStorageAlignedMat4Uniform(uint bufferIndex, uint wordOffset)
{
    return mat4(
        ReadStorageAlignedVec4Uniform(bufferIndex, wordOffset + 0u),
        ReadStorageAlignedVec4Uniform(bufferIndex, wordOffset + 4u),
        ReadStorageAlignedVec4Uniform(bufferIndex, wordOffset + 8u),
        ReadStorageAlignedVec4Uniform(bufferIndex, wordOffset + 12u));
}

mat4 ReadStorageMat4(uint bufferIndex, uint wordOffset)
{
    return mat4(
        ReadStorageVec4(bufferIndex, wordOffset + 0u),
        ReadStorageVec4(bufferIndex, wordOffset + 4u),
        ReadStorageVec4(bufferIndex, wordOffset + 8u),
        ReadStorageVec4(bufferIndex, wordOffset + 12u));
}

vec4 TransformRowMajorPoint(vec3 position, uint bufferIndex, uint matrixWordOffset)
{
    vec4 v = vec4(position, 1.0);
    return vec4(
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 0u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 4u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 8u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 12u))),
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 1u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 5u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 9u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 13u))),
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 2u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 6u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 10u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 14u))),
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 3u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 7u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 11u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 15u))));
}

vec3 TransformRowMajorVector(vec3 vector, uint bufferIndex, uint matrixWordOffset)
{
    vec4 v = vec4(vector, 0.0);
    return vec3(
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 0u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 4u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 8u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 12u))),
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 1u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 5u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 9u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 13u))),
        dot(v, vec4(
            ReadStorageFloat(bufferIndex, matrixWordOffset + 2u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 6u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 10u),
            ReadStorageFloat(bufferIndex, matrixWordOffset + 14u))));
}

vec4 MulRowMajor(vec4 v, mat4 m)
{
    return vec4(
        dot(v, vec4(m[0][0], m[1][0], m[2][0], m[3][0])),
        dot(v, vec4(m[0][1], m[1][1], m[2][1], m[3][1])),
        dot(v, vec4(m[0][2], m[1][2], m[2][2], m[3][2])),
        dot(v, vec4(m[0][3], m[1][3], m[2][3], m[3][3])));
}

float ReadRowMajorMaxScale(uint bufferIndex, uint matrixWordOffset)
{
    vec3 axisX = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 0u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 1u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 2u));
    vec3 axisY = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 4u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 5u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 6u));
    vec3 axisZ = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 8u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 9u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 10u));

    return max(max(length(axisX), length(axisY)), length(axisZ));
}

vec4 NormalizePlane(vec4 plane)
{
    float lengthSq = dot(plane.xyz, plane.xyz);
    if (lengthSq <= 0.0)
        return vec4(0.0, 0.0, 0.0, -1.0);
    return plane * inversesqrt(lengthSq);
}

bool SphereIntersectsRowMajorFrustum(vec3 worldCenter, float worldRadius, mat4 viewProjection)
{
    vec4 leftPlane = NormalizePlane(vec4(
        viewProjection[0][0] + viewProjection[0][3],
        viewProjection[1][0] + viewProjection[1][3],
        viewProjection[2][0] + viewProjection[2][3],
        viewProjection[3][0] + viewProjection[3][3]));
    vec4 rightPlane = NormalizePlane(vec4(
        -viewProjection[0][0] + viewProjection[0][3],
        -viewProjection[1][0] + viewProjection[1][3],
        -viewProjection[2][0] + viewProjection[2][3],
        -viewProjection[3][0] + viewProjection[3][3]));
    vec4 bottomPlane = NormalizePlane(vec4(
        viewProjection[0][1] + viewProjection[0][3],
        viewProjection[1][1] + viewProjection[1][3],
        viewProjection[2][1] + viewProjection[2][3],
        viewProjection[3][1] + viewProjection[3][3]));
    vec4 topPlane = NormalizePlane(vec4(
        -viewProjection[0][1] + viewProjection[0][3],
        -viewProjection[1][1] + viewProjection[1][3],
        -viewProjection[2][1] + viewProjection[2][3],
        -viewProjection[3][1] + viewProjection[3][3]));
    vec4 nearPlane = NormalizePlane(vec4(
        viewProjection[0][2],
        viewProjection[1][2],
        viewProjection[2][2],
        viewProjection[3][2]));
    vec4 farPlane = NormalizePlane(vec4(
        -viewProjection[0][2] + viewProjection[0][3],
        -viewProjection[1][2] + viewProjection[1][3],
        -viewProjection[2][2] + viewProjection[2][3],
        -viewProjection[3][2] + viewProjection[3][3]));

    return dot(leftPlane.xyz, worldCenter) + leftPlane.w >= -worldRadius &&
           dot(rightPlane.xyz, worldCenter) + rightPlane.w >= -worldRadius &&
           dot(bottomPlane.xyz, worldCenter) + bottomPlane.w >= -worldRadius &&
           dot(topPlane.xyz, worldCenter) + topPlane.w >= -worldRadius &&
           dot(nearPlane.xyz, worldCenter) + nearPlane.w >= -worldRadius &&
           dot(farPlane.xyz, worldCenter) + farPlane.w >= -worldRadius;
}

bool SphereIntersectsFrustumPlanes(vec3 worldCenter, float worldRadius, vec4 planes[6])
{
    return dot(planes[0].xyz, worldCenter) + planes[0].w >= -worldRadius &&
           dot(planes[1].xyz, worldCenter) + planes[1].w >= -worldRadius &&
           dot(planes[2].xyz, worldCenter) + planes[2].w >= -worldRadius &&
           dot(planes[3].xyz, worldCenter) + planes[3].w >= -worldRadius &&
           dot(planes[4].xyz, worldCenter) + planes[4].w >= -worldRadius &&
           dot(planes[5].xyz, worldCenter) + planes[5].w >= -worldRadius;
}

vec4 ReadMeshletTaskFrustumPlane(uint frameIndex, uint planeIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = planeIndex * 4u;
    return ReadStorageVec4(bufferIndex, baseWord);
}

void ReadMeshletTaskFrustumPlanes(uint frameIndex, out vec4 planes[6])
{
    planes[0] = ReadMeshletTaskFrustumPlane(frameIndex, 0u);
    planes[1] = ReadMeshletTaskFrustumPlane(frameIndex, 1u);
    planes[2] = ReadMeshletTaskFrustumPlane(frameIndex, 2u);
    planes[3] = ReadMeshletTaskFrustumPlane(frameIndex, 3u);
    planes[4] = ReadMeshletTaskFrustumPlane(frameIndex, 4u);
    planes[5] = ReadMeshletTaskFrustumPlane(frameIndex, 5u);
}

mat4 ReadMeshletTaskViewProjectionMatrix(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageMat4(bufferIndex, 24u);
}

mat4 ReadMeshletTaskInverseViewMatrix(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageMat4(bufferIndex, 40u);
}

mat4 ReadMeshletTaskPreviousHiZViewProjectionMatrix(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageMat4(bufferIndex, 56u);
}

mat4 ReadMeshletTaskPreviousHiZInverseViewMatrix(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageMat4(bufferIndex, 72u);
}

vec2 ReadMeshletTaskScreenDimensions(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageVec4(bufferIndex, 88u).xy;
}

uint ReadMeshletTaskPreviousHiZFrameValid(uint frameIndex)
{
    uint bufferIndex = uint(MESHLET_TASK_FRAME_DATA_BUFFER_BASE_INDEX) + frameIndex;
    return ReadStorageWord(bufferIndex, 90u);
}

GPUVertex ReadVertexFromBuffer(uint bufferIndex, uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    GPUVertex vertex;
    vertex.Position = ReadStorageVec3(bufferIndex, baseWord + 0u);
    vertex.Padding0 = ReadStorageFloat(bufferIndex, baseWord + 3u);
    vertex.Normal = ReadStorageVec3(bufferIndex, baseWord + 4u);
    vertex.Padding1 = ReadStorageFloat(bufferIndex, baseWord + 7u);
    vertex.TexCoord = ReadStorageVec2(bufferIndex, baseWord + 8u);
    vertex.TexCoord2 = ReadStorageVec2(bufferIndex, baseWord + 10u);
    vertex.Tangent = ReadStorageVec4(bufferIndex, baseWord + 12u);
    vertex.Color = ReadStorageVec4(bufferIndex, baseWord + 16u);
    return vertex;
}

vec3 ReadSplitVertexPosition(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_POSITION_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_POSITION_BUFFER_INDEX),
        baseWord).xyz;
}

vec3 ReadSplitVertexNormal(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_NORMAL_TANGENT_BUFFER_INDEX),
        baseWord).xyz;
}

vec4 ReadSplitVertexTangent(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_NORMAL_TANGENT_BUFFER_INDEX),
        baseWord + 4u);
}

vec2 ReadSplitVertexTexCoord(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_UV_COLOR_BUFFER_INDEX),
        baseWord).xy;
}

vec2 ReadSplitVertexTexCoord2(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_UV_COLOR_BUFFER_INDEX),
        baseWord).zw;
}

vec4 ReadSplitVertexColor(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(VERTEX_UV_COLOR_BUFFER_INDEX),
        baseWord + 4u);
}

GPUVertex ReadSplitVertex(uint vertexIndex)
{
    GPUVertex vertex;
    vertex.Position = ReadSplitVertexPosition(vertexIndex);
    vertex.Padding0 = 0.0;
    vertex.Normal = ReadSplitVertexNormal(vertexIndex);
    vertex.Padding1 = 0.0;
    vertex.TexCoord = ReadSplitVertexTexCoord(vertexIndex);
    vertex.TexCoord2 = ReadSplitVertexTexCoord2(vertexIndex);
    vertex.Tangent = ReadSplitVertexTangent(vertexIndex);
    vertex.Color = ReadSplitVertexColor(vertexIndex);
    return vertex;
}

GPUVertexPositionTexCoords ReadSplitVertexPositionTexCoords(uint vertexIndex)
{
    GPUVertexPositionTexCoords vertex;
    vertex.Position = ReadSplitVertexPosition(vertexIndex);
    vertex.TexCoord = ReadSplitVertexTexCoord(vertexIndex);
    vertex.TexCoord2 = ReadSplitVertexTexCoord2(vertexIndex);
    return vertex;
}

GPUVertexSimple ReadSplitVertexSimple(uint vertexIndex)
{
    GPUVertexSimple vertex;
    vertex.Position = ReadSplitVertexPosition(vertexIndex);
    vertex.Normal = ReadSplitVertexNormal(vertexIndex);
    vertex.TexCoord = ReadSplitVertexTexCoord(vertexIndex);
    return vertex;
}

vec3 ReadVertexPositionFromBuffer(uint bufferIndex, uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    return ReadStorageVec3(bufferIndex, baseWord + 0u);
}

GPUVertexPositionTexCoords ReadVertexPositionTexCoordsFromBuffer(uint bufferIndex, uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    GPUVertexPositionTexCoords vertex;
    vertex.Position = ReadStorageVec3(bufferIndex, baseWord + 0u);
    vertex.TexCoord = ReadStorageVec2(bufferIndex, baseWord + 8u);
    vertex.TexCoord2 = ReadStorageVec2(bufferIndex, baseWord + 10u);
    return vertex;
}

GPUVertexSimple ReadVertexSimpleFromBuffer(uint bufferIndex, uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    GPUVertexSimple vertex;
    vertex.Position = ReadStorageVec3(bufferIndex, baseWord + 0u);
    vertex.Normal = ReadStorageVec3(bufferIndex, baseWord + 4u);
    vertex.TexCoord = ReadStorageVec2(bufferIndex, baseWord + 8u);
    return vertex;
}

GPUVertex ReadVertex(uint vertexIndex)
{
    // Static source vertices are canonical split streams. Keeping this helper avoids
    // an interleaved duplicate solely for skinning source reads.
    return ReadSplitVertex(vertexIndex);
}

// The determinant of the world linear transform selects the correct tangent-space
// handedness for mirrored instances without changing the normal-map convention.
float ReadRowMajorLinearDeterminant(uint bufferIndex, uint matrixWordOffset)
{
    vec3 x = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 0u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 1u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 2u));
    vec3 y = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 4u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 5u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 6u));
    vec3 z = vec3(
        ReadStorageFloat(bufferIndex, matrixWordOffset + 8u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 9u),
        ReadStorageFloat(bufferIndex, matrixWordOffset + 10u));
    return dot(x, cross(y, z));
}

void MaxRendererDiagnostic(uint frameIndex, uint counterIndex, uint value)
{
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicMax(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], value);
}

// Storage qualification retains its appended logical counter IDs for report
// compatibility, while the physical detailed-only bank starts at word zero.
// Low, bounded offsets avoid coupling native-driver code generation for this
// optional telemetry to the size and churn of the general diagnostics SSBO.
void AddSimpleDdgiStorageValidationDiagnostic(
    uint frameIndex,
    uint logicalCounterIndex,
    uint value)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    uint bufferIndex = uint(SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX) +
        frameIndex % uint(FRAMES_IN_FLIGHT);
    uint physicalCounterIndex = logicalCounterIndex -
        SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE;
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[physicalCounterIndex],
        value);
#endif
}

void MaxSimpleDdgiStorageValidationDiagnostic(
    uint frameIndex,
    uint logicalCounterIndex,
    uint value)
{
#if NJULF_DDGI_DETAILED_COUNTERS
    uint bufferIndex = uint(SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX) +
        frameIndex % uint(FRAMES_IN_FLIGHT);
    uint physicalCounterIndex = logicalCounterIndex -
        SIMPLE_DDGI_STORAGE_VALIDATION_COUNTER_BASE;
    atomicMax(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[physicalCounterIndex],
        value);
#endif
}

// A negative-determinant instance reverses projected triangle winding. Mesh
// shaders restore the authored logical facing before fixed-function culling
// and gl_FrontFacing so depth, shadow, motion, alpha, and forward passes agree.
uvec3 ResolveMirroredInstanceTriangle(
    uint i0,
    uint i1,
    uint i2,
    bool negativeDeterminant)
{
    return negativeDeterminant ? uvec3(i0, i2, i1) : uvec3(i0, i1, i2);
}

// Shared TBN construction for material passes. Tangent orthonormalization is required
// after a non-uniform world transform; faceSign keeps double-sided normal maps coherent.
mat3 BuildOrthonormalTbn(vec3 normal, vec4 tangent, float faceSign)
{
    vec3 n = normalize(normal) * faceSign;
    vec3 t = tangent.xyz - n * dot(n, tangent.xyz);
    float tangentLengthSquared = dot(t, t);
    if (tangentLengthSquared <= 0.000001)
    {
        vec3 fallbackAxis = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
        t = normalize(cross(fallbackAxis, n));
    }
    else
    {
        t *= inversesqrt(tangentLengthSquared);
    }

    vec3 b = normalize(cross(n, t)) * tangent.w * faceSign;
    return mat3(t, b, n);
}

void WriteVertexToBuffer(uint bufferIndex, uint vertexIndex, GPUVertex vertex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    WriteStorageFloat(bufferIndex, baseWord + 0u, vertex.Position.x);
    WriteStorageFloat(bufferIndex, baseWord + 1u, vertex.Position.y);
    WriteStorageFloat(bufferIndex, baseWord + 2u, vertex.Position.z);
    WriteStorageFloat(bufferIndex, baseWord + 3u, vertex.Padding0);
    WriteStorageFloat(bufferIndex, baseWord + 4u, vertex.Normal.x);
    WriteStorageFloat(bufferIndex, baseWord + 5u, vertex.Normal.y);
    WriteStorageFloat(bufferIndex, baseWord + 6u, vertex.Normal.z);
    WriteStorageFloat(bufferIndex, baseWord + 7u, vertex.Padding1);
    WriteStorageFloat(bufferIndex, baseWord + 8u, vertex.TexCoord.x);
    WriteStorageFloat(bufferIndex, baseWord + 9u, vertex.TexCoord.y);
    WriteStorageFloat(bufferIndex, baseWord + 10u, vertex.TexCoord2.x);
    WriteStorageFloat(bufferIndex, baseWord + 11u, vertex.TexCoord2.y);
    WriteStorageFloat(bufferIndex, baseWord + 12u, vertex.Tangent.x);
    WriteStorageFloat(bufferIndex, baseWord + 13u, vertex.Tangent.y);
    WriteStorageFloat(bufferIndex, baseWord + 14u, vertex.Tangent.z);
    WriteStorageFloat(bufferIndex, baseWord + 15u, vertex.Tangent.w);
    WriteStorageFloat(bufferIndex, baseWord + 16u, vertex.Color.x);
    WriteStorageFloat(bufferIndex, baseWord + 17u, vertex.Color.y);
    WriteStorageFloat(bufferIndex, baseWord + 18u, vertex.Color.z);
    WriteStorageFloat(bufferIndex, baseWord + 19u, vertex.Color.w);
}

GPUVertexSkinningData ReadVertexSkinningData(uint skinningDataIndex)
{
    uint baseWord = skinningDataIndex * uint(SIZEOF_GPU_VERTEX_SKINNING_DATA / 4);
    GPUVertexSkinningData data;
    data.Joint0 = ReadStorageWord(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 0u);
    data.Joint1 = ReadStorageWord(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 1u);
    data.Joint2 = ReadStorageWord(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 2u);
    data.Joint3 = ReadStorageWord(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 3u);
    data.Weight0 = ReadStorageFloat(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 4u);
    data.Weight1 = ReadStorageFloat(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 5u);
    data.Weight2 = ReadStorageFloat(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 6u);
    data.Weight3 = ReadStorageFloat(uint(SKINNING_VERTEX_DATA_BUFFER_INDEX), baseWord + 7u);
    return data;
}

GPUSkinningDispatch ReadSkinningDispatch(uint frameIndex, uint dispatchIndex)
{
    uint bufferIndex = uint(SKINNING_DISPATCH_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = dispatchIndex * uint(SIZEOF_GPU_SKINNING_DISPATCH / 4);
    GPUSkinningDispatch dispatch;
    dispatch.SourceVertexOffset = ReadStorageWord(bufferIndex, baseWord + 0u);
    dispatch.SourceSkinningDataOffset = ReadStorageWord(bufferIndex, baseWord + 1u);
    dispatch.DestinationVertexOffset = ReadStorageWord(bufferIndex, baseWord + 2u);
    dispatch.VertexCount = ReadStorageWord(bufferIndex, baseWord + 3u);
    dispatch.SkinMatrixOffset = ReadStorageWord(bufferIndex, baseWord + 4u);
    dispatch.ObjectIndex = ReadStorageWord(bufferIndex, baseWord + 5u);
    dispatch.SourceMeshMetadataIndex = ReadStorageWord(bufferIndex, baseWord + 6u);
    dispatch.Flags = ReadStorageWord(bufferIndex, baseWord + 7u);
    return dispatch;
}

GPUParticleInstance ReadParticleInstance(uint bufferBaseIndex, uint frameIndex, uint instanceIndex)
{
    uint bufferIndex = bufferBaseIndex + frameIndex;
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_PARTICLE_INSTANCE / 4);
    GPUParticleInstance particle;
    particle.PositionSize = ReadStorageVec4(bufferIndex, baseWord + 0u);
    particle.VelocityRotation = ReadStorageVec4(bufferIndex, baseWord + 4u);
    particle.Color = ReadStorageVec4(bufferIndex, baseWord + 8u);
    particle.EmissiveLifetimeSoftClip = ReadStorageVec4(bufferIndex, baseWord + 12u);
    particle.TextureIndex = ReadStorageWord(bufferIndex, baseWord + 16u);
    particle.FlipbookFrame = ReadStorageWord(bufferIndex, baseWord + 17u);
    particle.FlipbookColumns = ReadStorageWord(bufferIndex, baseWord + 18u);
    particle.FlipbookRows = ReadStorageWord(bufferIndex, baseWord + 19u);
    particle.BlendMode = ReadStorageWord(bufferIndex, baseWord + 20u);
    particle.BillboardMode = ReadStorageWord(bufferIndex, baseWord + 21u);
    particle.DebugId = ReadStorageWord(bufferIndex, baseWord + 22u);
    particle.Padding0 = ReadStorageWord(bufferIndex, baseWord + 23u);
    particle.VolumetricAlbedoAndExtinction = ReadStorageVec4(bufferIndex, baseWord + 24u);
    particle.VolumetricRadiusAnisotropyAndFlags = ReadStorageVec4(bufferIndex, baseWord + 28u);
    return particle;
}

GPUParticleInstance ReadParticleInstance(uint frameIndex, uint instanceIndex)
{
    return ReadParticleInstance(uint(PARTICLE_INSTANCE_BUFFER_BASE_INDEX), frameIndex, instanceIndex);
}

GPUParticleBatch ReadParticleBatch(uint frameIndex, uint batchIndex)
{
    uint bufferIndex = uint(PARTICLE_BATCH_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = batchIndex * uint(SIZEOF_GPU_PARTICLE_BATCH / 4);
    GPUParticleBatch batch;
    batch.Start = ReadStorageWord(bufferIndex, baseWord + 0u);
    batch.Count = ReadStorageWord(bufferIndex, baseWord + 1u);
    batch.BlendMode = ReadStorageWord(bufferIndex, baseWord + 2u);
    batch.Padding0 = ReadStorageWord(bufferIndex, baseWord + 3u);
    return batch;
}

GPUParticleFrameData ReadParticleFrameData(uint frameIndex, uint frameDataBufferBaseIndex)
{
    uint bufferIndex = frameDataBufferBaseIndex + frameIndex;
    GPUParticleFrameData frame;
    frame.ViewProjectionMatrix = ReadStorageMat4(bufferIndex, 0u);
    frame.InverseViewMatrix = ReadStorageMat4(bufferIndex, 16u);
    frame.InverseProjectionMatrix = ReadStorageMat4(bufferIndex, 32u);
    frame.CameraPosition = ReadStorageVec4(bufferIndex, 48u).xyz;
    frame.GlobalSoftParticleDistance = ReadStorageFloat(bufferIndex, 51u);
    frame.ScreenDimensions = ReadStorageVec4(bufferIndex, 52u).xy;
    frame.Padding0 = vec2(0.0);
    return frame;
}

GPUParticleEmitter ReadParticleEmitter(uint frameIndex, uint emitterIndex)
{
    uint bufferIndex = uint(GPU_PARTICLE_EMITTER_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = emitterIndex * uint(SIZEOF_GPU_PARTICLE_EMITTER / 4);
    GPUParticleEmitter emitter;
    emitter.WorldMatrix = ReadStorageMat4(bufferIndex, baseWord + 0u);
    emitter.SpawnShape0 = ReadStorageVec4(bufferIndex, baseWord + 16u);
    emitter.SpawnShape1 = ReadStorageVec4(bufferIndex, baseWord + 20u);
    emitter.InitialVelocityMin = ReadStorageVec4(bufferIndex, baseWord + 24u);
    emitter.InitialVelocityMax = ReadStorageVec4(bufferIndex, baseWord + 28u);
    emitter.AccelerationDrag = ReadStorageVec4(bufferIndex, baseWord + 32u);
    emitter.LifetimeSize = ReadStorageVec4(bufferIndex, baseWord + 36u);
    emitter.Color = ReadStorageVec4(bufferIndex, baseWord + 40u);
    emitter.MaterialIndex = ReadStorageWord(bufferIndex, baseWord + 44u);
    emitter.MaxParticles = ReadStorageWord(bufferIndex, baseWord + 45u);
    emitter.RandomSeed = ReadStorageWord(bufferIndex, baseWord + 46u);
    emitter.Flags = ReadStorageWord(bufferIndex, baseWord + 47u);
    emitter.ColorEnd = ReadStorageVec4(bufferIndex, baseWord + 48u);
    emitter.EmissiveAngularVelocity = ReadStorageVec4(bufferIndex, baseWord + 52u);
    emitter.RotationParams = ReadStorageVec4(bufferIndex, baseWord + 56u);
    emitter.TimingParams = ReadStorageVec4(bufferIndex, baseWord + 60u);
    emitter.VolumetricAlbedoAndExtinction = ReadStorageVec4(bufferIndex, baseWord + 64u);
    emitter.VolumetricRadiusAnisotropyAndFlags = ReadStorageVec4(bufferIndex, baseWord + 68u);
    return emitter;
}

GPUParticleCurveSample ReadParticleCurveSample(uint frameIndex, uint emitterIndex, uint sampleIndex)
{
    uint bufferIndex = uint(GPU_PARTICLE_CURVE_SAMPLE_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = (emitterIndex * 16u + sampleIndex) * uint(SIZEOF_GPU_PARTICLE_CURVE_SAMPLE / 4);
    GPUParticleCurveSample curveSample;
    curveSample.Color = ReadStorageVec4(bufferIndex, baseWord + 0u);
    curveSample.Properties = ReadStorageVec4(bufferIndex, baseWord + 4u);
    return curveSample;
}

void WriteParticleState(uint frameIndex, uint particleIndex, GPUParticleState state)
{
    uint bufferIndex = uint(GPU_PARTICLE_STATE_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = particleIndex * uint(SIZEOF_GPU_PARTICLE_STATE / 4);
    WriteStorageVec4(bufferIndex, baseWord + 0u, state.PositionAge);
    WriteStorageVec4(bufferIndex, baseWord + 4u, state.VelocityLifetime);
    WriteStorageVec4(bufferIndex, baseWord + 8u, state.Color);
    WriteStorageVec4(bufferIndex, baseWord + 12u, state.SizeRotation);
    WriteStorageWord(bufferIndex, baseWord + 16u, state.EmitterIndex);
    WriteStorageWord(bufferIndex, baseWord + 17u, state.StableId);
    WriteStorageWord(bufferIndex, baseWord + 18u, state.RandomSeed);
    WriteStorageWord(bufferIndex, baseWord + 19u, state.Flags);
}

GPUParticleState ReadParticleState(uint frameIndex, uint particleIndex)
{
    uint bufferIndex = uint(GPU_PARTICLE_STATE_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = particleIndex * uint(SIZEOF_GPU_PARTICLE_STATE / 4);
    GPUParticleState state;
    state.PositionAge = ReadStorageVec4(bufferIndex, baseWord + 0u);
    state.VelocityLifetime = ReadStorageVec4(bufferIndex, baseWord + 4u);
    state.Color = ReadStorageVec4(bufferIndex, baseWord + 8u);
    state.SizeRotation = ReadStorageVec4(bufferIndex, baseWord + 12u);
    state.EmitterIndex = ReadStorageWord(bufferIndex, baseWord + 16u);
    state.StableId = ReadStorageWord(bufferIndex, baseWord + 17u);
    state.RandomSeed = ReadStorageWord(bufferIndex, baseWord + 18u);
    state.Flags = ReadStorageWord(bufferIndex, baseWord + 19u);
    return state;
}

void WriteParticleInstance(uint bufferBaseIndex, uint frameIndex, uint instanceIndex, GPUParticleInstance particle)
{
    uint bufferIndex = bufferBaseIndex + frameIndex;
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_PARTICLE_INSTANCE / 4);
    WriteStorageVec4(bufferIndex, baseWord + 0u, particle.PositionSize);
    WriteStorageVec4(bufferIndex, baseWord + 4u, particle.VelocityRotation);
    WriteStorageVec4(bufferIndex, baseWord + 8u, particle.Color);
    WriteStorageVec4(bufferIndex, baseWord + 12u, particle.EmissiveLifetimeSoftClip);
    WriteStorageWord(bufferIndex, baseWord + 16u, particle.TextureIndex);
    WriteStorageWord(bufferIndex, baseWord + 17u, particle.FlipbookFrame);
    WriteStorageWord(bufferIndex, baseWord + 18u, particle.FlipbookColumns);
    WriteStorageWord(bufferIndex, baseWord + 19u, particle.FlipbookRows);
    WriteStorageWord(bufferIndex, baseWord + 20u, particle.BlendMode);
    WriteStorageWord(bufferIndex, baseWord + 21u, particle.BillboardMode);
    WriteStorageWord(bufferIndex, baseWord + 22u, particle.DebugId);
    WriteStorageWord(bufferIndex, baseWord + 23u, particle.Padding0);
    WriteStorageVec4(bufferIndex, baseWord + 24u, particle.VolumetricAlbedoAndExtinction);
    WriteStorageVec4(bufferIndex, baseWord + 28u, particle.VolumetricRadiusAnisotropyAndFlags);
}

void AddSimpleDdgiReceiverCacheDiagnostic(
    uint frameIndex,
    uint counterIndex,
    uint value)
{
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        frameIndex;
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            counterIndex],
        value);
#endif
}

void IncrementSimpleDdgiReceiverCacheDiagnostic(
    uint frameIndex,
    uint counterIndex)
{
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
    AddSimpleDdgiReceiverCacheDiagnostic(frameIndex, counterIndex, 1u);
#endif
}

void WriteParticleInstance(uint frameIndex, uint instanceIndex, GPUParticleInstance particle)
{
    WriteParticleInstance(uint(GPU_PARTICLE_UNSORTED_RENDER_INSTANCE_BUFFER_BASE_INDEX), frameIndex, instanceIndex, particle);
}

GPUParticleSortKey ReadParticleSortKey(uint frameIndex, uint keyIndex)
{
    uint bufferIndex = uint(GPU_PARTICLE_SORT_KEY_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = keyIndex * uint(SIZEOF_GPU_PARTICLE_SORT_KEY / 4);
    GPUParticleSortKey sortKey;
    sortKey.Key = ReadStorageWord(bufferIndex, baseWord + 0u);
    sortKey.InstanceIndex = ReadStorageWord(bufferIndex, baseWord + 1u);
    return sortKey;
}

void WriteParticleSortKey(uint frameIndex, uint keyIndex, GPUParticleSortKey sortKey)
{
    uint bufferIndex = uint(GPU_PARTICLE_SORT_KEY_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = keyIndex * uint(SIZEOF_GPU_PARTICLE_SORT_KEY / 4);
    WriteStorageWord(bufferIndex, baseWord + 0u, sortKey.Key);
    WriteStorageWord(bufferIndex, baseWord + 1u, sortKey.InstanceIndex);
}

GPUVertex FetchRenderableVertex(GPUMeshlet meshlet, uint localVertexIndex, GPUObjectData objectData, uint frameIndex)
{
    if (objectData.SkinningEnabled != 0)
    {
        uint bufferIndex = uint(SKINNED_VERTEX_BUFFER_BASE_INDEX) + frameIndex;
        return ReadVertexFromBuffer(bufferIndex, uint(objectData.SkinnedVertexOffset) + localVertexIndex);
    }

    return ReadSplitVertex(meshlet.VertexOffset + localVertexIndex);
}

vec3 FetchRenderableVertexPosition(GPUMeshlet meshlet, uint localVertexIndex, GPUObjectData objectData, uint frameIndex)
{
    if (objectData.SkinningEnabled != 0)
    {
        uint bufferIndex = uint(SKINNED_VERTEX_BUFFER_BASE_INDEX) + frameIndex;
        return ReadVertexPositionFromBuffer(bufferIndex, uint(objectData.SkinnedVertexOffset) + localVertexIndex);
    }

    return ReadSplitVertexPosition(meshlet.VertexOffset + localVertexIndex);
}

GPUVertexPositionTexCoords FetchRenderableVertexPositionTexCoords(GPUMeshlet meshlet, uint localVertexIndex, GPUObjectData objectData, uint frameIndex)
{
    if (objectData.SkinningEnabled != 0)
    {
        uint bufferIndex = uint(SKINNED_VERTEX_BUFFER_BASE_INDEX) + frameIndex;
        return ReadVertexPositionTexCoordsFromBuffer(bufferIndex, uint(objectData.SkinnedVertexOffset) + localVertexIndex);
    }

    return ReadSplitVertexPositionTexCoords(meshlet.VertexOffset + localVertexIndex);
}

GPUVertexSimple FetchRenderableVertexSimple(GPUMeshlet meshlet, uint localVertexIndex, GPUObjectData objectData, uint frameIndex)
{
    if (objectData.SkinningEnabled != 0)
    {
        uint bufferIndex = uint(SKINNED_VERTEX_BUFFER_BASE_INDEX) + frameIndex;
        return ReadVertexSimpleFromBuffer(bufferIndex, uint(objectData.SkinnedVertexOffset) + localVertexIndex);
    }

    return ReadSplitVertexSimple(meshlet.VertexOffset + localVertexIndex);
}

const uint MESHLET_VIRTUAL_ADDRESS_BIT = 0x80000000u;
const uint MESHLET_VIRTUAL_ADDRESS_INDEX_MASK = 0x7fffffffu;
const uint MESHLET_PAGED_LOCAL_ADDRESS_BIT = 0x80000000u;
const uint MESHLET_PAGED_LOCAL_BANK_SHIFT = 24u;
const uint MESHLET_PAGED_LOCAL_BANK_MASK = 0x0fu;
const uint MESHLET_PAGED_LOCAL_WORD_MASK = 0x00ffffffu;
const uint MESHLET_PHYSICAL_PAGE_WORD_COUNT = 16384u;
const uint MESHLET_PHYSICAL_BANK_PAGE_COUNT = 1024u;
const uint MESHLET_PHYSICAL_PAGE_MAGIC = 0x3147504du;
const uint MESHLET_PHYSICAL_PAGE_VERSION = 1u;
const uint MESHLET_PAGE_TABLE_RESIDENT_FLAG = 1u << 0u;
const uint MESH_RESIDENCY_MANAGED_PHYSICAL_FLAG = 1u << 0u;
const uint MESHLET_STREAMING_INVALID_MAPPING_COUNTER = 2u;
const uint MESHLET_STREAMING_DEMAND_HEADER_WORD_COUNT = 4u;
const uint MESHLET_STREAMING_DEMAND_OVERFLOW_COUNTER = 0u;
const uint MESHLET_STREAMING_DEMAND_ACCEPTED_COUNTER = 1u;
const uint MESHLET_STREAMING_INVALID_DEMAND_COUNTER = 3u;

GPUMeshlet EmptyMeshlet()
{
    GPUMeshlet meshlet;
    meshlet.BoundingSphereCenter = vec3(0.0);
    meshlet.BoundingSphereRadius = 0.0;
    meshlet.VertexOffset = 0u;
    meshlet.VertexCount = 0u;
    meshlet.IndexOffset = 0u;
    meshlet.IndexCount = 0u;
    meshlet.LocalVertexOffset = 0u;
    meshlet.LocalVertexCount = 0u;
    meshlet.LocalTriangleOffset = 0u;
    meshlet.LocalTriangleCount = 0u;
    meshlet.NormalConeAxis = vec3(0.0);
    meshlet.NormalConeCutoff = -1.0;
    return meshlet;
}

void IncrementMeshletInvalidMapping(uint frameIndex)
{
    uint bufferIndex =
        uint(MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_BASE_INDEX) +
        frameIndex;
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            MESHLET_STREAMING_INVALID_MAPPING_COUNTER],
        1u);
}

GPUMeshlet ReadPackedMeshletAt(uint bufferIndex, uint baseWord)
{
    GPUMeshlet meshlet;
    meshlet.BoundingSphereCenter = ReadStorageVec3(
        bufferIndex,
        baseWord + 0u);
    meshlet.BoundingSphereRadius = ReadStorageFloat(
        bufferIndex,
        baseWord + 3u);
    meshlet.VertexOffset = ReadStorageWord(bufferIndex, baseWord + 4u);
    meshlet.LocalVertexOffset = ReadStorageWord(
        bufferIndex,
        baseWord + 5u);
    meshlet.LocalTriangleOffset = ReadStorageWord(
        bufferIndex,
        baseWord + 6u);
    uint packedCounts = ReadStorageWord(bufferIndex, baseWord + 7u);
    meshlet.LocalVertexCount = packedCounts & 0x7fu;
    meshlet.LocalTriangleCount = (packedCounts >> 7u) & 0x7fu;
    meshlet.VertexCount = meshlet.LocalVertexCount;
    meshlet.IndexOffset = 0u;
    meshlet.IndexCount = meshlet.LocalTriangleCount * 3u;

    uint packedCone = ReadStorageWord(bufferIndex, baseWord + 8u);
    const uint packedConeAbiMarker = 1u << 31u;
    const uint packedConeValidFlag = 1u << 30u;
    if ((packedCone & (packedConeAbiMarker | packedConeValidFlag)) ==
        (packedConeAbiMarker | packedConeValidFlag))
    {
        vec2 oct = vec2(
            float(packedCone & 0x3ffu),
            float((packedCone >> 10u) & 0x3ffu)) / 1023.0 * 2.0 - 1.0;
        vec3 axis = vec3(
            oct,
            1.0 - abs(oct.x) - abs(oct.y));
        float fold = clamp(-axis.z, 0.0, 1.0);
        axis.xy += vec2(
            axis.x >= 0.0 ? -fold : fold,
            axis.y >= 0.0 ? -fold : fold);
        float axisLengthSquared = dot(axis, axis);
        meshlet.NormalConeAxis = axisLengthSquared > 1e-12
            ? axis * inversesqrt(axisLengthSquared)
            : vec3(0.0);
        float decodedCutoff =
            float((packedCone >> 20u) & 0x3ffu) / 1023.0 - 0.01;
        meshlet.NormalConeCutoff = decodedCutoff > 0.0
            ? decodedCutoff
            : -1.0;
    }
    else
    {
        meshlet.NormalConeAxis = vec3(0.0);
        meshlet.NormalConeCutoff = -1.0;
    }
    return meshlet;
}

bool RequestMeshletStreamingRange(uint rangeIndex, uint frameIndex);

GPUMeshlet ReadMeshlet(uint meshletAddress, uint frameIndex)
{
    if ((meshletAddress & MESHLET_VIRTUAL_ADDRESS_BIT) == 0u)
    {
        uint baseWord = meshletAddress *
            uint(SIZEOF_GPU_MESHLET / 4);
        return ReadPackedMeshletAt(uint(MESHLET_BUFFER_INDEX), baseWord);
    }

    uint virtualIndex = meshletAddress &
        MESHLET_VIRTUAL_ADDRESS_INDEX_MASK;
    uint mappingWord = virtualIndex * 4u;
    uint globalPageId = ReadStorageWord(
        uint(MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX),
        mappingWord + 0u);
    uint pageLocalMeshletIndex = ReadStorageWord(
        uint(MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX),
        mappingWord + 1u);
    uint mappingFlags = ReadStorageWord(
        uint(MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX),
        mappingWord + 2u);
    uint virtualVertexOffset = ReadStorageWord(
        uint(MESHLET_VIRTUAL_MAPPING_BUFFER_INDEX),
        mappingWord + 3u);
    uint pageTableBuffer =
        uint(MESHLET_PHYSICAL_PAGE_TABLE_BUFFER_BASE_INDEX) + frameIndex;
    uint tableWord = globalPageId * 4u;
    uint bankIndex = ReadStorageWord(
        pageTableBuffer,
        tableWord + 0u);
    uint pageIndexInBank = ReadStorageWord(
        pageTableBuffer,
        tableWord + 1u);
    uint flags = ReadStorageWord(pageTableBuffer, tableWord + 3u);
    if ((flags & MESHLET_PAGE_TABLE_RESIDENT_FLAG) == 0u ||
        bankIndex >= uint(MESHLET_PHYSICAL_PAGE_BANK_BUFFER_COUNT) ||
        pageIndexInBank >= MESHLET_PHYSICAL_BANK_PAGE_COUNT)
    {
        RequestMeshletStreamingRange(
            mappingFlags >> 8u,
            frameIndex);
        IncrementMeshletInvalidMapping(frameIndex);
        return EmptyMeshlet();
    }

    uint pageBuffer =
        uint(MESHLET_PHYSICAL_PAGE_BANK_BUFFER_BASE_INDEX) + bankIndex;
    uint pageBaseWord =
        pageIndexInBank * MESHLET_PHYSICAL_PAGE_WORD_COUNT;
    uint magic = ReadStorageWord(pageBuffer, pageBaseWord + 0u);
    uint version = ReadStorageWord(pageBuffer, pageBaseWord + 1u);
    uint meshletCount = ReadStorageWord(pageBuffer, pageBaseWord + 2u);
    uint meshletWordOffset = ReadStorageWord(
        pageBuffer,
        pageBaseWord + 5u);
    uint vertexWordOffset = ReadStorageWord(
        pageBuffer,
        pageBaseWord + 6u);
    uint triangleWordOffset = ReadStorageWord(
        pageBuffer,
        pageBaseWord + 7u);
    if (magic != MESHLET_PHYSICAL_PAGE_MAGIC ||
        version != MESHLET_PHYSICAL_PAGE_VERSION ||
        pageLocalMeshletIndex >= meshletCount ||
        meshletWordOffset >= MESHLET_PHYSICAL_PAGE_WORD_COUNT ||
        vertexWordOffset >= MESHLET_PHYSICAL_PAGE_WORD_COUNT ||
        triangleWordOffset >= MESHLET_PHYSICAL_PAGE_WORD_COUNT)
    {
        IncrementMeshletInvalidMapping(frameIndex);
        return EmptyMeshlet();
    }

    uint meshletBaseWord = pageBaseWord + meshletWordOffset +
        pageLocalMeshletIndex * uint(SIZEOF_GPU_MESHLET / 4);
    if (meshletBaseWord + uint(SIZEOF_GPU_MESHLET / 4) >
        pageBaseWord + MESHLET_PHYSICAL_PAGE_WORD_COUNT)
    {
        IncrementMeshletInvalidMapping(frameIndex);
        return EmptyMeshlet();
    }
    GPUMeshlet meshlet = ReadPackedMeshletAt(
        pageBuffer,
        meshletBaseWord);
    meshlet.VertexOffset += virtualVertexOffset;
    uint vertexLocalWord = pageBaseWord + vertexWordOffset +
        meshlet.LocalVertexOffset;
    uint triangleLocalWord = pageBaseWord + triangleWordOffset +
        meshlet.LocalTriangleOffset;
    if (vertexLocalWord > MESHLET_PAGED_LOCAL_WORD_MASK ||
        triangleLocalWord > MESHLET_PAGED_LOCAL_WORD_MASK)
    {
        IncrementMeshletInvalidMapping(frameIndex);
        return EmptyMeshlet();
    }
    meshlet.LocalVertexOffset = MESHLET_PAGED_LOCAL_ADDRESS_BIT |
        (bankIndex << MESHLET_PAGED_LOCAL_BANK_SHIFT) |
        vertexLocalWord;
    meshlet.LocalTriangleOffset = MESHLET_PAGED_LOCAL_ADDRESS_BIT |
        (bankIndex << MESHLET_PAGED_LOCAL_BANK_SHIFT) |
        triangleLocalWord;
    return meshlet;
}

// Compatibility overload for non-frame-aware utilities. Production raster
// and compaction passes always use the explicit frame-index form above.
GPUMeshlet ReadMeshlet(uint meshletAddress)
{
    return ReadMeshlet(meshletAddress, 0u);
}

uint ReadMeshletLocalAddressWord(
    uint encodedAddress,
    uint directBufferIndex,
    uint relativeWord)
{
    if ((encodedAddress & MESHLET_PAGED_LOCAL_ADDRESS_BIT) == 0u)
    {
        return ReadStorageWord(
            directBufferIndex,
            encodedAddress + relativeWord);
    }
    uint bankIndex = (encodedAddress >>
        MESHLET_PAGED_LOCAL_BANK_SHIFT) &
        MESHLET_PAGED_LOCAL_BANK_MASK;
    uint wordOffset = encodedAddress &
        MESHLET_PAGED_LOCAL_WORD_MASK;
    return ReadStorageWord(
        uint(MESHLET_PHYSICAL_PAGE_BANK_BUFFER_BASE_INDEX) + bankIndex,
        wordOffset + relativeWord);
}

uint ReadMeshletLocalVertexIndex(
    GPUMeshlet meshlet,
    uint vertexSlot)
{
    return ReadMeshletLocalAddressWord(
        meshlet.LocalVertexOffset,
        uint(MESHLET_VERTEX_INDEX_BUFFER_INDEX),
        vertexSlot);
}

uint ReadMeshletLocalTriangleIndex(
    GPUMeshlet meshlet,
    uint triangleWord)
{
    return ReadMeshletLocalAddressWord(
        meshlet.LocalTriangleOffset,
        uint(MESHLET_TRIANGLE_INDEX_BUFFER_INDEX),
        triangleWord);
}

bool MeshletStreamingRangeReady(uint rangeIndex, uint frameIndex)
{
    if (rangeIndex == 0xffffffffu)
        return false;
    uint bufferIndex =
        uint(MESHLET_STREAMING_RANGE_STATE_BUFFER_BASE_INDEX) +
        frameIndex;
    uint word = ReadStorageWord(bufferIndex, rangeIndex >> 5u);
    return (word & (1u << (rangeIndex & 31u))) != 0u;
}

struct GPUMeshletStreamingRangeData
{
    uint FirstGlobalPageId;
    uint PageCount;
    uint FirstVirtualMeshlet;
    uint MeshletCount;
    uint Flags;
    uint FallbackRangeIndex;
};

GPUMeshletStreamingRangeData ReadMeshletStreamingRange(uint rangeIndex)
{
    uint baseWord = rangeIndex * 8u;
    GPUMeshletStreamingRangeData range;
    range.FirstGlobalPageId = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 0u);
    range.PageCount = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 1u);
    range.FirstVirtualMeshlet = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 2u);
    range.MeshletCount = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 3u);
    range.Flags = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 4u);
    range.FallbackRangeIndex = ReadStorageWord(
        uint(MESHLET_STREAMING_RANGE_BUFFER_INDEX),
        baseWord + 5u);
    return range;
}

// The frame-local bitset is cleared at the fence-safe start of each frame.
// It deduplicates visible range keys before the bounded append buffer is read
// back by the CPU on the next reuse of this frame slot.
bool RequestMeshletStreamingRange(uint rangeIndex, uint frameIndex)
{
    if (rangeIndex == 0xffffffffu)
        return false;
    uint demandBufferIndex =
        uint(MESHLET_STREAMING_DEMAND_BUFFER_BASE_INDEX) + frameIndex;
    uint feedbackBufferIndex =
        uint(MESHLET_STREAMING_FEEDBACK_COUNTER_BUFFER_BASE_INDEX) +
        frameIndex;
    uint capacity = ReadStorageWord(demandBufferIndex, 1u);
    uint rangeCount = ReadStorageWord(demandBufferIndex, 2u);
    if (rangeIndex >= rangeCount)
    {
        atomicAdd(
            BindlessStorageBuffers[
                nonuniformEXT(feedbackBufferIndex)].Words[
                MESHLET_STREAMING_INVALID_DEMAND_COUNTER],
            1u);
        return false;
    }

    uint stampWord = MESHLET_STREAMING_DEMAND_HEADER_WORD_COUNT +
        capacity + (rangeIndex >> 5u);
    uint stampMask = 1u << (rangeIndex & 31u);
    uint previous = atomicOr(
        BindlessStorageBuffers[
            nonuniformEXT(demandBufferIndex)].Words[stampWord],
        stampMask);
    if ((previous & stampMask) != 0u)
        return false;

    uint appendIndex = atomicAdd(
        BindlessStorageBuffers[
            nonuniformEXT(demandBufferIndex)].Words[0u],
        1u);
    if (appendIndex < capacity)
    {
        BindlessStorageBuffers[
            nonuniformEXT(demandBufferIndex)].Words[
                MESHLET_STREAMING_DEMAND_HEADER_WORD_COUNT + appendIndex] =
            rangeIndex;
        atomicAdd(
            BindlessStorageBuffers[
                nonuniformEXT(feedbackBufferIndex)].Words[
                MESHLET_STREAMING_DEMAND_ACCEPTED_COUNTER],
            1u);
        return true;
    }

    atomicAdd(
        BindlessStorageBuffers[
            nonuniformEXT(feedbackBufferIndex)].Words[
            MESHLET_STREAMING_DEMAND_OVERFLOW_COUNTER],
        1u);
    return false;
}

GPUMeshInfo ReadSceneMeshInfo(uint meshIndex)
{
    uint baseWord = meshIndex * uint(SIZEOF_GPU_MESH_INFO / 4);
    GPUMeshInfo info;
    info.BoundingSphere = ReadStorageVec4(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 0u);
    info.SkinningDataOffset = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 4u);
    info.SkinningDataCount = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 5u);
    info.Flags = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 6u);
    info.MeshletOffset = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 7u);
    info.MeshletCount = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 8u);
    info.MeshletLod1Offset = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 9u);
    info.MeshletLod1Count = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 10u);
    info.MeshletLod2Offset = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 11u);
    info.MeshletLod2Count = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 12u);
    info.MeshletLodGeneratedCount = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 13u);
    info.MeshletLod1ErrorBits = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 14u);
    info.MeshletLod2ErrorBits = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 15u);
    info.GpuMeshletRecordCount = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 16u);
    info.HierarchyNodeOffset = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 17u);
    info.HierarchyNodeCount = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 18u);
    info.HierarchyRootNode = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 19u);
    info.StreamingRangeIndex = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 20u);
    info.ResidencyFlags = ReadStorageWord(
        uint(SCENE_MESH_METADATA_BUFFER_INDEX), baseWord + 21u);
    return info;
}

GPUMeshletHierarchyNode ReadMeshletHierarchyNode(uint nodeRecordIndex)
{
    uint baseWord = nodeRecordIndex * uint(SIZEOF_GPU_MESHLET / 4);
    GPUMeshletHierarchyNode node;
    node.BoundingSphereCenter = ReadStorageVec3(
        uint(MESHLET_BUFFER_INDEX), baseWord + 0u);
    node.BoundingSphereRadius = ReadStorageFloat(
        uint(MESHLET_BUFFER_INDEX), baseWord + 3u);
    node.GeometricError = ReadStorageFloat(
        uint(MESHLET_BUFFER_INDEX), baseWord + 4u);
    node.FirstChild = ReadStorageWord(
        uint(MESHLET_BUFFER_INDEX), baseWord + 5u);
    uint packedMetadata = ReadStorageWord(
        uint(MESHLET_BUFFER_INDEX), baseWord + 6u);
    node.ChildCount = packedMetadata & 0x0fu;
    node.Depth = (packedMetadata >> 4u) & 0x0fu;
    node.Flags = (packedMetadata >> 8u) & 0x03u;
    node.Valid = (packedMetadata & (1u << 31u)) != 0u
        ? 1u
        : 0u;
    node.MeshletOffset = ReadStorageWord(
        uint(MESHLET_BUFFER_INDEX), baseWord + 7u);
    node.MeshletCount = ReadStorageWord(
        uint(MESHLET_BUFFER_INDEX), baseWord + 8u);
    return node;
}

bool MeshletNormalConeValid(GPUMeshlet meshlet)
{
    float coneCutoff = meshlet.NormalConeCutoff;
    float objectAxisLengthSquared = dot(
        meshlet.NormalConeAxis,
        meshlet.NormalConeAxis);
    return coneCutoff > 0.0 && coneCutoff <= 1.0 &&
        !isnan(coneCutoff) && !isinf(coneCutoff) &&
        !any(isnan(meshlet.NormalConeAxis)) &&
        !any(isinf(meshlet.NormalConeAxis)) &&
        objectAxisLengthSquared > 1e-12;
}

bool MeshletBackfaceConeCulled(
    GPUMeshlet meshlet,
    uint instanceBufferIndex,
    uint objectWordOffset,
    vec3 worldCenter,
    float worldRadius,
    vec3 viewPosition)
{
    float coneCutoff = meshlet.NormalConeCutoff;
    if (!MeshletNormalConeValid(meshlet))
        return false;

    vec3 worldAxis = TransformRowMajorVector(
        meshlet.NormalConeAxis,
        instanceBufferIndex,
        objectWordOffset + 16u);
    float worldAxisLengthSquared = dot(worldAxis, worldAxis);
    if (worldAxisLengthSquared <= 1e-12 ||
        isnan(worldAxisLengthSquared) ||
        isinf(worldAxisLengthSquared))
        return false;
    worldAxis *= inversesqrt(worldAxisLengthSquared);

    vec3 centerToCamera = viewPosition - worldCenter;
    float distanceSquared = dot(centerToCamera, centerToCamera);
    float safeRadius = max(worldRadius, 0.0);
    if (distanceSquared <= safeRadius * safeRadius + 1e-8 ||
        isnan(distanceSquared) || isinf(distanceSquared) ||
        isnan(safeRadius) || isinf(safeRadius))
        return false;

    float inverseDistance = inversesqrt(distanceSquared);
    float sphereSine = clamp(safeRadius * inverseDistance, 0.0, 1.0);
    float sphereCosine = sqrt(max(1.0 - sphereSine * sphereSine, 0.0));
    float coneSine = sqrt(max(1.0 - coneCutoff * coneCutoff, 0.0));
    float combinedCosine =
        coneCutoff * sphereCosine - coneSine * sphereSine;
    if (combinedCosine <= 0.0)
        return false;

    float combinedSine = min(
        coneSine * sphereCosine + coneCutoff * sphereSine,
        1.0);
    vec3 surfaceToCamera = centerToCamera * inverseDistance;
    const float rejectionSafetyEpsilon = 1e-6;
    return dot(worldAxis, surfaceToCamera) <
        -combinedSine - rejectionSafetyEpsilon;
}

GPUMeshletDrawCommand ReadMeshletDrawCommandFromBase(uint bufferBaseIndex, uint frameIndex, uint drawIndex)
{
    uint bufferIndex = bufferBaseIndex + frameIndex;
    uint baseWord = drawIndex * uint(SIZEOF_GPU_MESHLET_DRAW_COMMAND / 4);
    GPUMeshletDrawCommand command;
    command.MeshletIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.InstanceId = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.MaterialIndex = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.Flags = ReadStorageWord(bufferIndex, baseWord + 3u);
    return command;
}

GPUPackedMeshletDrawCommand ReadPackedMeshletDrawCommandFromBase(uint bufferBaseIndex, uint frameIndex, uint drawIndex)
{
    uint bufferIndex = bufferBaseIndex + frameIndex;
    uint baseWord = drawIndex * uint(SIZEOF_GPU_PACKED_MESHLET_DRAW_COMMAND / 4);
    GPUPackedMeshletDrawCommand command;
    command.MeshletIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.InstanceId = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.MaterialIndex = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.Flags = ReadStorageWord(bufferIndex, baseWord + 3u);
    command.WorldCenterRadius = ReadStorageVec4(bufferIndex, baseWord + 4u);
    return command;
}

GPUFoliagePrototype ReadFoliagePrototype(uint prototypeIndex)
{
    uint baseWord = prototypeIndex * uint(SIZEOF_GPU_FOLIAGE_PROTOTYPE / 4);
    GPUFoliagePrototype prototype;
    prototype.MeshMetadataIndex = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 0u);
    prototype.MeshletOffset = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 1u);
    prototype.MeshletCount = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 2u);
    prototype.MeshletLod1Offset = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 3u);
    prototype.MeshletLod1Count = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 4u);
    prototype.MeshletLod2Offset = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 5u);
    prototype.MeshletLod2Count = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 6u);
    prototype.MaterialIndex = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 7u);
    prototype.GeometryMode = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 8u);
    prototype.Flags = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 9u);
    prototype.ImpostorMetadataIndex = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 10u);
    prototype.MeshletOutputClass = ReadStorageWord(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 11u);
    prototype.BladeHeight = ReadStorageFloat(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 12u);
    prototype.BladeWidth = ReadStorageFloat(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 13u);
    prototype.LodDistances = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 14u);
    prototype.WindParams = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 18u);
    prototype.LightingParams = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 22u);
    return prototype;
}

GPUFoliageImpostor ReadFoliageImpostor(uint impostorIndex)
{
    uint baseWord = impostorIndex * uint(
        SIZEOF_GPU_FOLIAGE_IMPOSTOR / 4);
    GPUFoliageImpostor impostor;
    impostor.AlbedoOpacityTextureIndex = ReadStorageWord(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 0u);
    impostor.NormalTextureIndex = ReadStorageWord(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 1u);
    impostor.DepthTextureIndex = ReadStorageWord(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 2u);
    impostor.ViewCount = ReadStorageWord(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 3u);
    impostor.SourceBoundsMinScale = ReadStorageVec4(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 4u);
    impostor.SourceBoundsMax = ReadStorageVec4(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 8u);
    vec4 pivotAndOffset = ReadStorageVec4(
        uint(FOLIAGE_IMPOSTOR_METADATA_BUFFER_INDEX), baseWord + 12u);
    impostor.Pivot = pivotAndOffset.xyz;
    impostor.ViewDataOffset = floatBitsToUint(pivotAndOffset.w);
    return impostor;
}

GPUFoliageImpostorView ReadFoliageImpostorView(uint viewIndex)
{
    uint baseWord = viewIndex * uint(
        SIZEOF_GPU_FOLIAGE_IMPOSTOR_VIEW / 4);
    GPUFoliageImpostorView view;
    view.Direction = ReadStorageVec4(
        uint(FOLIAGE_IMPOSTOR_VIEW_BUFFER_INDEX), baseWord + 0u);
    view.AtlasRectangle = ReadStorageVec4(
        uint(FOLIAGE_IMPOSTOR_VIEW_BUFFER_INDEX), baseWord + 4u);
    return view;
}

uint SelectFoliageImpostorView(
    GPUFoliageImpostor impostor,
    vec3 localViewDirection)
{
    uint count = min(impostor.ViewCount, 64u);
    uint selected = 0u;
    float selectedScore = -2.0;
    float directionLengthSquared = dot(
        localViewDirection,
        localViewDirection);
    vec3 direction = directionLengthSquared > 1e-12
        ? localViewDirection * inversesqrt(directionLengthSquared)
        : vec3(0.0, 0.0, 1.0);
    for (uint index = 0u; index < count; index++)
    {
        GPUFoliageImpostorView view = ReadFoliageImpostorView(
            impostor.ViewDataOffset + index);
        float viewLengthSquared = dot(
            view.Direction.xyz,
            view.Direction.xyz);
        vec3 normalizedView = viewLengthSquared > 1e-12
            ? view.Direction.xyz * inversesqrt(viewLengthSquared)
            : vec3(0.0, 0.0, 1.0);
        float score = dot(normalizedView, direction);
        if (score > selectedScore)
        {
            selectedScore = score;
            selected = index;
        }
    }
    return selected;
}

GPUFoliagePatch ReadFoliagePatch(uint patchIndex)
{
    uint baseWord = patchIndex * uint(SIZEOF_GPU_FOLIAGE_PATCH / 4);
    GPUFoliagePatch foliagePatch;
    foliagePatch.BoundsMinDensity = ReadStorageVec4(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 0u);
    foliagePatch.BoundsMaxSeed = ReadStorageVec4(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 4u);
    foliagePatch.PrototypeIndex = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 8u);
    foliagePatch.ClusterOffset = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 9u);
    foliagePatch.ClusterCount = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 10u);
    foliagePatch.NearFieldStableObjectId = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 11u);
    foliagePatch.Seed = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 12u);
    foliagePatch.Flags = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 13u);
    foliagePatch.NearFieldStableMaterialId = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 14u);
    foliagePatch.NearFieldPackedObjectMaterialRevisions = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 15u);
    foliagePatch.DensityTextureIndex = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 16u);
    foliagePatch.TerrainDescriptorIndex = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 17u);
    foliagePatch.PlacementMode = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 18u);
    foliagePatch.ContentRevision = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 19u);
    foliagePatch.DensityUvScaleOffset = ReadStorageVec4(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 20u);
    return foliagePatch;
}

GPUFoliageCluster ReadFoliageCluster(uint clusterIndex)
{
    uint baseWord = clusterIndex * uint(SIZEOF_GPU_FOLIAGE_CLUSTER / 4);
    GPUFoliageCluster cluster;
    cluster.WorldCenterRadius = ReadStorageVec4(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 0u);
    cluster.BoundsMinDensity = ReadStorageVec4(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 4u);
    cluster.BoundsMaxLod = ReadStorageVec4(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 8u);
    cluster.PatchIndex = ReadStorageWord(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 12u);
    cluster.FirstInstance = ReadStorageWord(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 13u);
    cluster.InstanceCount = ReadStorageWord(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 14u);
    cluster.RandomSeed = ReadStorageWord(uint(FOLIAGE_CLUSTER_BUFFER_INDEX), baseWord + 15u);
    return cluster;
}

GPUDdgiFoliageProxyPatch ReadDdgiFoliageProxyPatch(
    uint bufferIndex,
    uint patchIndex)
{
    uint baseWord = patchIndex *
        uint(SIZEOF_GPU_DDGI_FOLIAGE_PROXY_PATCH / 4);
    GPUDdgiFoliageProxyPatch foliagePatch;
    foliagePatch.BoundsMinimumAndClusterWidth =
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 0u);
    foliagePatch.BoundsMaximumAndCardHeight =
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 4u);
    foliagePatch.WindAndCoverage =
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 8u);
    uvec4 range = ReadStorageAlignedUVec4Uniform(
        bufferIndex,
        baseWord + 12u);
    foliagePatch.StablePatchKeyLow = range.x;
    foliagePatch.StablePatchKeyHigh = range.y;
    foliagePatch.CardOffset = range.z;
    foliagePatch.CardCount = range.w;
    uvec4 grid = ReadStorageAlignedUVec4Uniform(
        bufferIndex,
        baseWord + 16u);
    foliagePatch.GridColumns = grid.x;
    foliagePatch.GridRows = grid.y;
    foliagePatch.RepresentedInstancesPerCard = grid.z;
    foliagePatch.Flags = grid.w;
    return foliagePatch;
}

uint ReadFoliageVisibleClusterIndex(uint visibleClusterBufferBaseIndex, uint frameIndex, uint drawIndex)
{
    uint baseWord = drawIndex * uint(
        SIZEOF_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND / 4);
    return ReadStorageWord(
        visibleClusterBufferBaseIndex + frameIndex,
        baseWord);
}

GPUFoliageProceduralDrawCommand ReadFoliageProceduralDrawCommand(
    uint visibleClusterBufferBaseIndex,
    uint frameIndex,
    uint drawIndex)
{
    uint bufferIndex = visibleClusterBufferBaseIndex + frameIndex;
    uint baseWord = drawIndex * uint(
        SIZEOF_GPU_FOLIAGE_PROCEDURAL_DRAW_COMMAND / 4);
    GPUFoliageProceduralDrawCommand command;
    command.ClusterIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.LodBand = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.CandidateCount = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.ActiveCount = ReadStorageWord(bufferIndex, baseWord + 3u);
    command.DensityFraction = ReadStorageFloat(bufferIndex, baseWord + 4u);
    command.TransitionFraction = ReadStorageFloat(bufferIndex, baseWord + 5u);
    command.WidthCompensation = ReadStorageFloat(bufferIndex, baseWord + 6u);
    command.Flags = ReadStorageWord(bufferIndex, baseWord + 7u);
    return command;
}

GPUFoliageAuthoredInstanceCommand ReadFoliageAuthoredInstanceCommand(
    uint frameIndex,
    uint commandIndex)
{
    uint bufferIndex =
        uint(FOLIAGE_AUTHORED_INSTANCE_COMMAND_BUFFER_BASE_INDEX) +
        frameIndex;
    uint baseWord = commandIndex * uint(
        SIZEOF_GPU_FOLIAGE_AUTHORED_INSTANCE_COMMAND / 4);
    GPUFoliageAuthoredInstanceCommand command;
    command.InstanceIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.ClusterIndex = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.PrototypeIndex = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.LodLevel = ReadStorageWord(bufferIndex, baseWord + 3u);
    command.FirstMeshlet = ReadStorageWord(bufferIndex, baseWord + 4u);
    command.MeshletCount = ReadStorageWord(bufferIndex, baseWord + 5u);
    command.TargetFirstMeshlet = ReadStorageWord(bufferIndex, baseWord + 6u);
    command.TargetMeshletCount = ReadStorageWord(bufferIndex, baseWord + 7u);
    command.WorldCenterRadius = ReadStorageVec4(bufferIndex, baseWord + 8u);
    command.Flags = ReadStorageWord(bufferIndex, baseWord + 12u);
    command.TransitionFraction = ReadStorageFloat(
        bufferIndex,
        baseWord + 13u);
    command.Padding0 = ReadStorageWord(bufferIndex, baseWord + 14u);
    command.Padding1 = ReadStorageWord(bufferIndex, baseWord + 15u);
    return command;
}

GPUFoliageInstance ReadFoliageInstance(uint frameIndex, uint instanceIndex)
{
    uint bufferIndex = uint(FOLIAGE_INSTANCE_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_FOLIAGE_INSTANCE / 4);
    GPUFoliageInstance instance;
    instance.PositionScale = ReadStorageVec4(bufferIndex, baseWord + 0u);
    instance.RotationWind = ReadStorageVec4(bufferIndex, baseWord + 4u);
    instance.ColorVariation = ReadStorageVec4(bufferIndex, baseWord + 8u);
    instance.PrototypeIndex = ReadStorageWord(bufferIndex, baseWord + 12u);
    instance.PatchIndex = ReadStorageWord(bufferIndex, baseWord + 13u);
    instance.ClusterIndex = ReadStorageWord(bufferIndex, baseWord + 14u);
    instance.Flags = ReadStorageWord(bufferIndex, baseWord + 15u);
    return instance;
}

GPUFoliageMeshletDrawCommand ReadFoliageMeshletDrawCommand(uint frameIndex, uint drawIndex)
{
    uint bufferIndex = uint(FOLIAGE_MESHLET_DRAW_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = drawIndex * uint(SIZEOF_GPU_FOLIAGE_MESHLET_DRAW_COMMAND / 4);
    GPUFoliageMeshletDrawCommand command;
    command.MeshletIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.InstanceIndex = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.PrototypeIndex = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.MaterialIndex = ReadStorageWord(bufferIndex, baseWord + 3u);
    command.WorldCenterRadius = ReadStorageVec4(bufferIndex, baseWord + 4u);
    command.Flags = ReadStorageWord(bufferIndex, baseWord + 8u);
    command.LodLevel = ReadStorageWord(bufferIndex, baseWord + 9u);
    command.ClusterIndex = ReadStorageWord(bufferIndex, baseWord + 10u);
    command.Padding0 = ReadStorageWord(bufferIndex, baseWord + 11u);
    return command;
}

GPUMeshletDrawCommand ReadMeshletDrawCommand(uint frameIndex, uint drawIndex)
{
    return ReadMeshletDrawCommandFromBase(uint(MESHLET_DRAW_BUFFER_BASE_INDEX), frameIndex, drawIndex);
}

GPUObjectData ReadInstanceData(uint frameIndex, uint instanceIndex)
{
    uint bufferIndex = uint(INSTANCE_BUFFER_BASE_INDEX) + frameIndex;
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_OBJECT_DATA / 4);
    GPUObjectData objectData;
    objectData.WorldMatrix = mat4(1.0);
    objectData.WorldMatrixInverseTranspose = mat4(1.0);
    objectData.MeshIndex = int(ReadStorageWord(bufferIndex, baseWord + 32u));
    objectData.MaterialIndex = int(ReadStorageWord(bufferIndex, baseWord + 33u));
    objectData.SkinnedVertexOffset = int(ReadStorageWord(bufferIndex, baseWord + 34u));
    objectData.SkinningEnabled = int(ReadStorageWord(bufferIndex, baseWord + 35u));
    objectData.PreviousWorldMatrix = mat4(1.0);
    objectData.NearFieldStableObjectId =
        ReadStorageWord(bufferIndex, baseWord + 52u);
    objectData.NearFieldStableMaterialId =
        ReadStorageWord(bufferIndex, baseWord + 53u);
    objectData.NearFieldPackedObjectMaterialRevisions =
        ReadStorageWord(bufferIndex, baseWord + 54u);
    objectData.NearFieldCoverageMotionFlags =
        ReadStorageWord(bufferIndex, baseWord + 55u);
    return objectData;
}

GPUMaterialData ReadMaterial(uint materialIndex)
{
    uint baseWord = materialIndex * uint(SIZEOF_GPU_MATERIAL_DATA / 4);
    GPUMaterialData material;
    uint materialBufferIndex = uint(MATERIAL_DATA_BUFFER_INDEX);
    material.Albedo = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 0u);
    material.Emissive = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 4u);
    material.NormalScaleBias = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 8u);
    material.MetallicRoughnessAO = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 12u);
    material.BaseColorOffsetScale = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 16u);
    material.NormalOffsetScale = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 20u);
    material.MetallicRoughnessOffsetScale = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 24u);
    material.OcclusionOffsetScale = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 28u);
    material.EmissiveOffsetScale = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 32u);
    material.TextureRotations = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 36u);
    material.TextureTexCoordSets = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 40u);
    material.OcclusionBinding = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 44u);
    uvec4 textureBindings = ReadStorageAlignedUVec4Uniform(materialBufferIndex, baseWord + 48u);
    uvec4 transportMetadata = ReadStorageAlignedUVec4Uniform(materialBufferIndex, baseWord + 52u);
    uvec4 revisionMetadata = ReadStorageAlignedUVec4Uniform(materialBufferIndex, baseWord + 56u);
    uvec4 directionalMetadata = ReadStorageAlignedUVec4Uniform(materialBufferIndex, baseWord + 60u);
    material.AlbedoTextureIndex = int(textureBindings.x);
    material.NormalTextureIndex = int(textureBindings.y);
    material.MetallicRoughnessTextureIndex = int(textureBindings.z);
    material.OcclusionTextureIndex = int(textureBindings.w);
    material.EmissiveTextureIndex = int(transportMetadata.x);
    material.FeatureFlags = transportMetadata.y;
    material.ExtensionDataIndex = int(transportMetadata.z);
    material.TransportFlags = transportMetadata.w;
    material.TransportProfileRevision = revisionMetadata.x;
    material.PackedMeanMetallicRoughness = revisionMetadata.y;
    material.TransportProfileQuality = revisionMetadata.z;
    material.MaterialRevision = revisionMetadata.w;
    material.TextureContentRevision = directionalMetadata.x;
    material.PackedMeanGiDirectionalDiffuseBaseRg = directionalMetadata.y;
    material.PackedMeanGiDirectionalDiffuseBaseBAndF0R = directionalMetadata.z;
    material.PackedMeanGiDielectricF0Gb = directionalMetadata.w;
    material.DdgiAverageAlbedo = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 64u);
    material.DdgiAverageEmissive = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 68u);
    material.DdgiAverageTransmission = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 72u);
    material.DdgiMaterialPolicy = ReadStorageAlignedVec4Uniform(materialBufferIndex, baseWord + 76u);
    return material;
}

float UnpackForwardMaterialUvSet(uint packedUvSets, uint selectorIndex)
{
    return float((packedUvSets >> (selectorIndex * 4u)) & 0x0fu);
}

float UnpackForwardMaterialBlendMode(uint packedUvSets)
{
    return float((packedUvSets >> 20u) & 0x07u);
}

GPUMaterialData ReadForwardMaterial(uint materialIndex)
{
    const vec4 identityOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    uint hotBaseWord = materialIndex *
        uint(SIZEOF_GPU_FORWARD_MATERIAL_DATA / 4);
    uint hotBufferIndex = uint(FORWARD_MATERIAL_DATA_BUFFER_INDEX);
    uint coldBaseWord = materialIndex *
        uint(SIZEOF_GPU_MATERIAL_DATA / 4);
    uint coldBufferIndex = uint(MATERIAL_DATA_BUFFER_INDEX);

    GPUMaterialData material;
    material.Albedo = ReadStorageAlignedVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 0u);
    material.Emissive = ReadStorageAlignedVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 4u);
    material.NormalScaleBias = ReadStorageAlignedVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 8u);
    material.MetallicRoughnessAO = ReadStorageAlignedVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 12u);
    uvec4 textureBindings = ReadStorageAlignedUVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 16u);
    uvec4 controls0 = ReadStorageAlignedUVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 20u);
    uvec4 controls1 = ReadStorageAlignedUVec4Uniform(
        hotBufferIndex,
        hotBaseWord + 24u);

    material.AlbedoTextureIndex = int(textureBindings.x);
    material.NormalTextureIndex = int(textureBindings.y);
    material.MetallicRoughnessTextureIndex = int(textureBindings.z);
    material.OcclusionTextureIndex = int(textureBindings.w);
    material.EmissiveTextureIndex = int(controls0.x);
    material.ExtensionDataIndex = int(controls0.y);
    material.FeatureFlags = controls0.z;
    material.TransportFlags = controls0.w;
    material.MaterialRevision = controls1.z;
    material.TextureContentRevision = controls1.w;

    material.TextureTexCoordSets = vec4(
        UnpackForwardMaterialUvSet(controls1.x, 0u),
        UnpackForwardMaterialUvSet(controls1.x, 1u),
        UnpackForwardMaterialUvSet(controls1.x, 2u),
        UnpackForwardMaterialUvSet(controls1.x, 3u));
    material.OcclusionBinding = vec4(
        0.0,
        UnpackForwardMaterialUvSet(controls1.x, 4u),
        UnpackForwardMaterialBlendMode(controls1.x),
        0.0);
    material.BaseColorOffsetScale = identityOffsetScale;
    material.NormalOffsetScale = identityOffsetScale;
    material.MetallicRoughnessOffsetScale = identityOffsetScale;
    material.OcclusionOffsetScale = identityOffsetScale;
    material.EmissiveOffsetScale = identityOffsetScale;
    material.TextureRotations = vec4(0.0);

    uint identityMask = controls1.y;
    if ((identityMask & (1u << 0u)) == 0u)
    {
        material.BaseColorOffsetScale = ReadStorageAlignedVec4Uniform(
            coldBufferIndex,
            coldBaseWord + 16u);
        material.TextureRotations.x = ReadStorageFloatUniform(
            coldBufferIndex,
            coldBaseWord + 36u);
    }
    if ((identityMask & (1u << 1u)) == 0u)
    {
        material.NormalOffsetScale = ReadStorageAlignedVec4Uniform(
            coldBufferIndex,
            coldBaseWord + 20u);
        material.TextureRotations.y = ReadStorageFloatUniform(
            coldBufferIndex,
            coldBaseWord + 37u);
    }
    if ((identityMask & (1u << 2u)) == 0u)
    {
        material.MetallicRoughnessOffsetScale = ReadStorageAlignedVec4Uniform(
            coldBufferIndex,
            coldBaseWord + 24u);
        material.TextureRotations.z = ReadStorageFloatUniform(
            coldBufferIndex,
            coldBaseWord + 38u);
    }
    if ((identityMask & (1u << 3u)) == 0u)
    {
        material.EmissiveOffsetScale = ReadStorageAlignedVec4Uniform(
            coldBufferIndex,
            coldBaseWord + 32u);
        material.TextureRotations.w = ReadStorageFloatUniform(
            coldBufferIndex,
            coldBaseWord + 39u);
    }
    if ((identityMask & (1u << 4u)) == 0u)
    {
        material.OcclusionOffsetScale = ReadStorageAlignedVec4Uniform(
            coldBufferIndex,
            coldBaseWord + 28u);
        material.OcclusionBinding.x = ReadStorageFloatUniform(
            coldBufferIndex,
            coldBaseWord + 44u);
    }

    // These are consumed only by two material diagnostic views. The forward
    // fragment shader demand-loads them for those views, keeping the ordinary
    // material read entirely inside the compact record unless a UV transform
    // is non-identity.
    material.TransportProfileRevision = 0u;
    material.TransportProfileQuality = 0u;
    material.PackedMeanMetallicRoughness = 0u;
    material.PackedMeanGiDirectionalDiffuseBaseRg = 0u;
    material.PackedMeanGiDirectionalDiffuseBaseBAndF0R = 0u;
    material.PackedMeanGiDielectricF0Gb = 0u;
    material.DdgiAverageAlbedo = vec4(0.0);
    material.DdgiAverageEmissive = vec4(0.0);
    material.DdgiAverageTransmission = vec4(0.0);
    material.DdgiMaterialPolicy = vec4(0.0);
    return material;
}

void LoadForwardMaterialDiagnosticMetadata(
    uint materialIndex,
    inout GPUMaterialData material)
{
    uint coldBaseWord = materialIndex *
        uint(SIZEOF_GPU_MATERIAL_DATA / 4);
    uint coldBufferIndex = uint(MATERIAL_DATA_BUFFER_INDEX);
    material.TransportProfileRevision = ReadStorageWordUniform(
        coldBufferIndex,
        coldBaseWord + 56u);
    material.TransportProfileQuality = ReadStorageWordUniform(
        coldBufferIndex,
        coldBaseWord + 58u);
}

GPUMaterialExtensionData ReadMaterialExtension(uint extensionIndex)
{
    uint baseWord = extensionIndex * uint(SIZEOF_GPU_MATERIAL_EXTENSION_DATA / 4);
    uint bufferIndex = uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX);
    GPUMaterialExtensionData data;
    data.Clearcoat = ReadStorageVec4Uniform(bufferIndex, baseWord + 0u);
    data.SheenColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 4u);
    data.Anisotropy = ReadStorageVec4Uniform(bufferIndex, baseWord + 8u);
    data.Transmission = ReadStorageVec4Uniform(bufferIndex, baseWord + 12u);
    data.AttenuationColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 16u);
    data.Subsurface = ReadStorageVec4Uniform(bufferIndex, baseWord + 20u);
    data.SpecularColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 24u);
    data.Iridescence = ReadStorageVec4Uniform(bufferIndex, baseWord + 28u);
    data.Dispersion = ReadStorageVec4Uniform(bufferIndex, baseWord + 32u);
    data.ClearcoatOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 36u);
    data.ClearcoatRoughnessOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 40u);
    data.ClearcoatNormalOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 44u);
    data.SheenColorOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 48u);
    data.SheenRoughnessOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 52u);
    data.AnisotropyOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 56u);
    data.TransmissionOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 60u);
    data.ThicknessOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 64u);
    data.SpecularOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 68u);
    data.SpecularColorOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 72u);
    data.IridescenceOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 76u);
    data.IridescenceThicknessOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 80u);
    data.SubsurfaceOffsetScale = ReadStorageVec4Uniform(bufferIndex, baseWord + 84u);
    data.ExtensionTextureRotations0 = ReadStorageVec4Uniform(bufferIndex, baseWord + 88u);
    data.ExtensionTextureRotations1 = ReadStorageVec4Uniform(bufferIndex, baseWord + 92u);
    data.ExtensionTextureRotations2 = ReadStorageVec4Uniform(bufferIndex, baseWord + 96u);
    data.ExtensionTextureRotations3 = ReadStorageVec4Uniform(bufferIndex, baseWord + 100u);
    data.ExtensionTextureTexCoordSets0 = ReadStorageVec4Uniform(bufferIndex, baseWord + 104u);
    data.ExtensionTextureTexCoordSets1 = ReadStorageVec4Uniform(bufferIndex, baseWord + 108u);
    data.ExtensionTextureTexCoordSets2 = ReadStorageVec4Uniform(bufferIndex, baseWord + 112u);
    data.ExtensionTextureTexCoordSets3 = ReadStorageVec4Uniform(bufferIndex, baseWord + 116u);
    uvec4 textureIndices0 = ReadStorageUVec4Uniform(bufferIndex, baseWord + 120u);
    uvec4 textureIndices1 = ReadStorageUVec4Uniform(bufferIndex, baseWord + 124u);
    uvec4 textureIndices2 = ReadStorageUVec4Uniform(bufferIndex, baseWord + 128u);
    uvec4 textureIndices3 = ReadStorageUVec4Uniform(bufferIndex, baseWord + 132u);
    data.ClearcoatTextureIndex = int(textureIndices0.x);
    data.ClearcoatRoughnessTextureIndex = int(textureIndices0.y);
    data.ClearcoatNormalTextureIndex = int(textureIndices0.z);
    data.SheenColorTextureIndex = int(textureIndices0.w);
    data.SheenRoughnessTextureIndex = int(textureIndices1.x);
    data.AnisotropyTextureIndex = int(textureIndices1.y);
    data.TransmissionTextureIndex = int(textureIndices1.z);
    data.ThicknessTextureIndex = int(textureIndices1.w);
    data.SubsurfaceTextureIndex = int(textureIndices2.x);
    data.SpecularTextureIndex = int(textureIndices2.y);
    data.SpecularColorTextureIndex = int(textureIndices2.z);
    data.IridescenceTextureIndex = int(textureIndices2.w);
    data.IridescenceThicknessTextureIndex = int(textureIndices3.x);
    data.Padding0 = int(textureIndices3.y);
    data.Padding1 = int(textureIndices3.z);
    data.Padding2 = int(textureIndices3.w);
    data.Padding3 = int(ReadStorageWordUniform(bufferIndex, baseWord + 136u));
    return data;
}

GPUMaterialExtensionData EmptyForwardMaterialExtension()
{
    GPUMaterialExtensionData data;
    data.Clearcoat = vec4(0.0);
    data.SheenColor = vec4(0.0);
    data.Anisotropy = vec4(0.0);
    data.Transmission = vec4(0.0);
    data.AttenuationColor = vec4(0.0);
    data.Subsurface = vec4(0.0);
    data.SpecularColor = vec4(0.0);
    data.Iridescence = vec4(0.0);
    data.Dispersion = vec4(0.0);
    data.ClearcoatOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.ClearcoatRoughnessOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.ClearcoatNormalOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.SheenColorOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.SheenRoughnessOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.AnisotropyOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.TransmissionOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.ThicknessOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.SpecularOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.SpecularColorOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.IridescenceOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.IridescenceThicknessOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.SubsurfaceOffsetScale = vec4(0.0, 0.0, 1.0, 1.0);
    data.ExtensionTextureRotations0 = vec4(0.0);
    data.ExtensionTextureRotations1 = vec4(0.0);
    data.ExtensionTextureRotations2 = vec4(0.0);
    data.ExtensionTextureRotations3 = vec4(0.0);
    data.ExtensionTextureTexCoordSets0 = vec4(0.0);
    data.ExtensionTextureTexCoordSets1 = vec4(0.0);
    data.ExtensionTextureTexCoordSets2 = vec4(0.0);
    data.ExtensionTextureTexCoordSets3 = vec4(0.0);
    data.ClearcoatTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.ClearcoatRoughnessTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.ClearcoatNormalTextureIndex = DEFAULT_NORMAL_TEXTURE;
    data.SheenColorTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.SheenRoughnessTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.AnisotropyTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.TransmissionTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.ThicknessTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.SubsurfaceTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.SpecularTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.SpecularColorTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.IridescenceTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.IridescenceThicknessTextureIndex = DEFAULT_WHITE_TEXTURE;
    data.Padding0 = 0;
    data.Padding1 = 0;
    data.Padding2 = 0;
    data.Padding3 = 0;
    return data;
}

GPUMaterialExtensionData ReadForwardMaterialExtension(
    uint extensionIndex,
    uint featureFlags)
{
    uint baseWord = extensionIndex *
        uint(SIZEOF_GPU_MATERIAL_EXTENSION_DATA / 4);
    uint bufferIndex = uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX);
    GPUMaterialExtensionData data = EmptyForwardMaterialExtension();

    const uint clearcoatMask =
        MATERIAL_FEATURE_CLEARCOAT |
        MATERIAL_FEATURE_CLEARCOAT_TEXTURE |
        MATERIAL_FEATURE_CLEARCOAT_ROUGHNESS_TEXTURE |
        MATERIAL_FEATURE_CLEARCOAT_NORMAL_TEXTURE |
        MATERIAL_FEATURE_EMISSIVE_STRENGTH;
    if ((featureFlags & clearcoatMask) != 0u)
    {
        data.Clearcoat = ReadStorageVec4Uniform(bufferIndex, baseWord + 0u);
        if ((featureFlags & MATERIAL_FEATURE_CLEARCOAT_TEXTURE) != 0u)
        {
            data.ClearcoatOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 36u);
            data.ExtensionTextureRotations0.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 88u);
            data.ExtensionTextureTexCoordSets0.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 104u);
            data.ClearcoatTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 120u));
        }
        if ((featureFlags & MATERIAL_FEATURE_CLEARCOAT_ROUGHNESS_TEXTURE) != 0u)
        {
            data.ClearcoatRoughnessOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 40u);
            data.ExtensionTextureRotations0.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 89u);
            data.ExtensionTextureTexCoordSets0.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 105u);
            data.ClearcoatRoughnessTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 121u));
        }
    }

    const uint sheenMask =
        MATERIAL_FEATURE_SHEEN |
        MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE |
        MATERIAL_FEATURE_SHEEN_ROUGHNESS_TEXTURE;
    if ((featureFlags & sheenMask) != 0u)
    {
        data.SheenColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 4u);
        if ((featureFlags & MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE) != 0u)
        {
            data.SheenColorOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 48u);
            data.ExtensionTextureRotations0.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 91u);
            data.ExtensionTextureTexCoordSets0.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 107u);
            data.SheenColorTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 123u));
        }
        if ((featureFlags & MATERIAL_FEATURE_SHEEN_ROUGHNESS_TEXTURE) != 0u)
        {
            data.SheenRoughnessOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 52u);
            data.ExtensionTextureRotations1.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 92u);
            data.ExtensionTextureTexCoordSets1.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 108u);
            data.SheenRoughnessTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 124u));
        }
    }

    const uint anisotropyMask =
        MATERIAL_FEATURE_ANISOTROPY |
        MATERIAL_FEATURE_ANISOTROPY_TEXTURE;
    if ((featureFlags & anisotropyMask) != 0u)
    {
        data.Anisotropy = ReadStorageVec4Uniform(bufferIndex, baseWord + 8u);
        if ((featureFlags & MATERIAL_FEATURE_ANISOTROPY_TEXTURE) != 0u)
        {
            data.AnisotropyOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 56u);
            data.ExtensionTextureRotations1.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 93u);
            data.ExtensionTextureTexCoordSets1.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 109u);
            data.AnisotropyTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 125u));
        }
    }

    const uint transmissionMask =
        MATERIAL_FEATURE_TRANSMISSION |
        MATERIAL_FEATURE_TRANSMISSION_TEXTURE |
        MATERIAL_FEATURE_VOLUME_APPROXIMATION |
        MATERIAL_FEATURE_IOR;
    if ((featureFlags & transmissionMask) != 0u)
    {
        data.Transmission = ReadStorageVec4Uniform(bufferIndex, baseWord + 12u);
        if ((featureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u)
            data.AttenuationColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 16u);
        if ((featureFlags & MATERIAL_FEATURE_TRANSMISSION_TEXTURE) != 0u)
        {
            data.TransmissionOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 60u);
            data.ExtensionTextureRotations1.z = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 94u);
            data.ExtensionTextureTexCoordSets1.z = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 110u);
            data.TransmissionTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 126u));
        }
        if ((featureFlags & MATERIAL_FEATURE_VOLUME_APPROXIMATION) != 0u)
        {
            data.ThicknessOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 64u);
            data.ExtensionTextureRotations1.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 95u);
            data.ExtensionTextureTexCoordSets1.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 111u);
            data.ThicknessTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 127u));
        }
    }

    const uint subsurfaceMask =
        MATERIAL_FEATURE_SUBSURFACE |
        MATERIAL_FEATURE_SUBSURFACE_TEXTURE;
    if ((featureFlags & subsurfaceMask) != 0u)
    {
        data.Subsurface = ReadStorageVec4Uniform(bufferIndex, baseWord + 20u);
        if ((featureFlags & MATERIAL_FEATURE_SUBSURFACE_TEXTURE) != 0u)
        {
            data.SubsurfaceOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 84u);
            data.ExtensionTextureRotations3.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 100u);
            data.ExtensionTextureTexCoordSets3.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 116u);
            data.SubsurfaceTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 128u));
        }
    }

    const uint specularMask =
        MATERIAL_FEATURE_SPECULAR |
        MATERIAL_FEATURE_SPECULAR_TEXTURE |
        MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE;
    if ((featureFlags & specularMask) != 0u)
    {
        data.SpecularColor = ReadStorageVec4Uniform(bufferIndex, baseWord + 24u);
        if ((featureFlags & MATERIAL_FEATURE_SPECULAR_TEXTURE) != 0u)
        {
            data.SpecularOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 68u);
            data.ExtensionTextureRotations2.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 96u);
            data.ExtensionTextureTexCoordSets2.x = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 112u);
            data.SpecularTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 129u));
        }
        if ((featureFlags & MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE) != 0u)
        {
            data.SpecularColorOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 72u);
            data.ExtensionTextureRotations2.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 97u);
            data.ExtensionTextureTexCoordSets2.y = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 113u);
            data.SpecularColorTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 130u));
        }
    }

    const uint iridescenceMask =
        MATERIAL_FEATURE_IRIDESCENCE |
        MATERIAL_FEATURE_IRIDESCENCE_TEXTURE |
        MATERIAL_FEATURE_IRIDESCENCE_THICKNESS_TEXTURE;
    if ((featureFlags & iridescenceMask) != 0u)
    {
        data.Iridescence = ReadStorageVec4Uniform(bufferIndex, baseWord + 28u);
        if ((featureFlags & MATERIAL_FEATURE_IRIDESCENCE_TEXTURE) != 0u)
        {
            data.IridescenceOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 76u);
            data.ExtensionTextureRotations2.z = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 98u);
            data.ExtensionTextureTexCoordSets2.z = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 114u);
            data.IridescenceTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 131u));
        }
        if ((featureFlags & MATERIAL_FEATURE_IRIDESCENCE_THICKNESS_TEXTURE) != 0u)
        {
            data.IridescenceThicknessOffsetScale = ReadStorageVec4Uniform(
                bufferIndex,
                baseWord + 80u);
            data.ExtensionTextureRotations2.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 99u);
            data.ExtensionTextureTexCoordSets2.w = ReadStorageFloatUniform(
                bufferIndex,
                baseWord + 115u);
            data.IridescenceThicknessTextureIndex = int(ReadStorageWordUniform(
                bufferIndex,
                baseWord + 132u));
        }
    }

    // yzw carries the compiled thin-transmission tint for every optical
    // extension, while x is the optional dispersion strength.
    data.Dispersion = ReadStorageVec4Uniform(bufferIndex, baseWord + 32u);
    return data;
}

void ReadForwardThinGlassOptics(
    uint extensionIndex,
    out vec4 transmission,
    out vec4 dispersion)
{
    uint baseWord = extensionIndex *
        uint(SIZEOF_GPU_MATERIAL_EXTENSION_DATA / 4);
    uint bufferIndex = uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX);
    transmission = ReadStorageVec4Uniform(bufferIndex, baseWord + 12u);
    dispersion = ReadStorageVec4Uniform(bufferIndex, baseWord + 32u);
}

GPUTiledLightHeader ReadTiledLightHeader(uint tileIndex)
{
    uint baseWord = tileIndex * uint(SIZEOF_GPU_TILED_LIGHT_HEADER / 4);
    uvec4 packed = ReadStorageAlignedUVec4Uniform(
        uint(TILED_LIGHT_HEADER_BUFFER_INDEX),
        baseWord);
    GPUTiledLightHeader header;
    header.LightCount = packed.x;
    header.LightOffset = packed.y;
    header.OverflowCount = packed.z;
    header.Padding1 = packed.w;
    return header;
}

GPULight ReadLight(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LIGHT / 4);
    uint lightBufferIndex = uint(LIGHT_BUFFER_INDEX);
    vec4 positionIntensity = ReadStorageAlignedVec4Uniform(lightBufferIndex, baseWord + 0u);
    vec4 colorRange = ReadStorageAlignedVec4Uniform(lightBufferIndex, baseWord + 4u);
    vec4 directionAngle = ReadStorageAlignedVec4Uniform(lightBufferIndex, baseWord + 8u);
    uvec4 typeShadow = ReadStorageAlignedUVec4Uniform(lightBufferIndex, baseWord + 12u);
    vec4 attenuation = ReadStorageAlignedVec4Uniform(lightBufferIndex, baseWord + 16u);
    vec4 upSize = ReadStorageAlignedVec4Uniform(lightBufferIndex, baseWord + 20u);
    uvec4 shapeProfile = ReadStorageAlignedUVec4Uniform(lightBufferIndex, baseWord + 24u);
    GPULight light;
    light.Position = positionIntensity.xyz;
    light.Intensity = positionIntensity.w;
    light.Color = colorRange.xyz;
    light.Range = colorRange.w;
    light.Direction = directionAngle.xyz;
    light.SpotAngle = directionAngle.w;
    light.Type = int(typeShadow.x);
    light.ShadowFlags = int(typeShadow.y);
    light.ShadowStrength = uintBitsToFloat(typeShadow.z);
    light.StableIdentity = typeShadow.w;
    light.InnerSpotAngle = attenuation.x;
    light.AttenuationConstant = attenuation.y;
    light.AttenuationLinear = attenuation.z;
    light.AttenuationQuadratic = attenuation.w;
    light.Up = upSize.xyz;
    light.SizeX = upSize.w;
    light.SizeY = uintBitsToFloat(shapeProfile.x);
    light.IesTextureIndex = int(shapeProfile.y);
    light.IesRotationRadians = uintBitsToFloat(shapeProfile.z);
    light.AreaFlags = int(shapeProfile.w);
    return light;
}

bool NjulfIsAreaLight(GPULight light)
{
    return light.Type == GPU_LIGHT_TYPE_RECTANGLE ||
        light.Type == GPU_LIGHT_TYPE_DISK ||
        light.Type == GPU_LIGHT_TYPE_TUBE;
}

bool NjulfIsPunctualLight(GPULight light)
{
    return light.Type == GPU_LIGHT_TYPE_POINT ||
        light.Type == GPU_LIGHT_TYPE_SPOT;
}

bool NjulfIsLocalLight(GPULight light)
{
    return light.Type != GPU_LIGHT_TYPE_DIRECTIONAL;
}

bool NjulfAreaLightIsTwoSided(GPULight light)
{
    return (uint(light.AreaFlags) & GPU_LIGHT_AREA_FLAG_TWO_SIDED) != 0u;
}

// Legacy authored lights retain Njulf's squared scene-range convention. Model
// imports select physical inverse-square or Assimp polynomial attenuation via
// reserved flag bits; raster and GI share the dispatch helpers below.
float EvaluateNjulfPunctualRangeAttenuation(float distanceToLight, float lightRange)
{
    if (lightRange <= 0.0 || distanceToLight >= lightRange)
        return 0.0;
    float rangeFactor = clamp(1.0 - max(distanceToLight, 0.0) / lightRange, 0.0, 1.0);
    return rangeFactor * rangeFactor;
}

float EvaluateNjulfFiniteRangeWindow(float distanceToLight, float lightRange)
{
    if (lightRange <= 0.0 || distanceToLight >= lightRange)
        return 0.0;
    float ratio = max(distanceToLight, 0.0) / lightRange;
    float factor = clamp(1.0 - ratio * ratio * ratio * ratio, 0.0, 1.0);
    return factor * factor;
}

uint NjulfLightAttenuationMode(GPULight light)
{
    return (uint(light.ShadowFlags) & GPU_LIGHT_ATTENUATION_MODE_MASK) >>
        GPU_LIGHT_ATTENUATION_MODE_SHIFT;
}

float EvaluateNjulfLightDistanceAttenuation(
    GPULight light,
    float distanceToLight)
{
    uint mode = NjulfLightAttenuationMode(light);
    if (mode == GPU_LIGHT_ATTENUATION_LEGACY_WINDOWED)
    {
        return EvaluateNjulfPunctualRangeAttenuation(
            distanceToLight,
            light.Range);
    }

    float rangeWindow = EvaluateNjulfFiniteRangeWindow(
        distanceToLight,
        light.Range);
    if (rangeWindow <= 0.0)
        return 0.0;
    float distance = max(distanceToLight, 0.0);
    if (mode == GPU_LIGHT_ATTENUATION_INVERSE_SQUARE)
        return rangeWindow / max(distance * distance, 1e-4);
    if (mode == GPU_LIGHT_ATTENUATION_POLYNOMIAL)
    {
        float denominator = light.AttenuationConstant +
            light.AttenuationLinear * distance +
            light.AttenuationQuadratic * distance * distance;
        return rangeWindow / max(denominator, 1e-4);
    }
    return 0.0;
}

float EvaluateNjulfSpotAttenuation(vec3 lightDirection, vec3 directionToLight, float spotAngle)
{
    float coneCos = cos(spotAngle);
    float spotCos = dot(normalize(lightDirection), -directionToLight);
    return smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
}

float EvaluateNjulfSpotAttenuation(
    GPULight light,
    vec3 directionToLight)
{
    if (NjulfLightAttenuationMode(light) ==
        GPU_LIGHT_ATTENUATION_LEGACY_WINDOWED)
    {
        return EvaluateNjulfSpotAttenuation(
            light.Direction,
            directionToLight,
            light.SpotAngle);
    }

    float outerCos = cos(light.SpotAngle);
    float innerCos = cos(light.InnerSpotAngle);
    float spotCos = dot(normalize(light.Direction), -directionToLight);
    float coneWidth = innerCos - outerCos;
    if (coneWidth <= 1e-6)
        return step(outerCos, spotCos);
    float attenuation = clamp(
        (spotCos - outerCos) / coneWidth,
        0.0,
        1.0);
    return attenuation * attenuation;
}

GPUDdgiEmissiveSource ReadDdgiEmissiveSource(uint sourceIndex)
{
    uint baseWord = sourceIndex * uint(SIZEOF_GPU_DDGI_EMISSIVE_SOURCE / 4);
    GPUDdgiEmissiveSource source;
    uint sourceBufferIndex = uint(SIMPLE_DDGI_EMISSIVE_SOURCE_BUFFER_INDEX);
    source.Vertex0Area = ReadStorageAlignedVec4Uniform(sourceBufferIndex, baseWord + 0u);
    source.Edge1AliasProbability = ReadStorageAlignedVec4Uniform(sourceBufferIndex, baseWord + 4u);
    source.Edge2AliasFlags = ReadStorageAlignedVec4Uniform(sourceBufferIndex, baseWord + 8u);
    source.RadianceSelectionProbability = ReadStorageAlignedVec4Uniform(sourceBufferIndex, baseWord + 12u);
    return source;
}

uint ReadTiledLightIndex(uint lightListOffset)
{
    uint baseWord = lightListOffset * uint(SIZEOF_GPU_LIGHT_INDEX / 4);
    return ReadStorageWordUniform(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 0u);
}

mat4 ReadShadowMatrix(uint cascadeIndex)
{
    uint baseWord = uint(OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0 / 4) + cascadeIndex * 16u;
    return mat4(
        ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 0u),
        ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 4u),
        ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 8u),
        ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 12u));
}

int ReadLocalSpotShadowIndex(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX / 4);
    return int(ReadStorageWordUniform(uint(LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX), baseWord + 0u));
}

int ReadLocalPointShadowIndex(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX / 4);
    return int(ReadStorageWordUniform(uint(LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX), baseWord + 1u));
}

int ReadLocalAreaShadowIndex(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX / 4);
    return int(ReadStorageWordUniform(uint(LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX), baseWord + 2u));
}

GPUEnvironmentData ReadEnvironmentDataFrom(uint bufferIndex)
{
    uvec4 textureIndices = ReadStorageAlignedUVec4Uniform(bufferIndex, 0u);
    vec4 intensities = ReadStorageAlignedVec4Uniform(bufferIndex, 4u);
    uvec4 controls = ReadStorageAlignedUVec4Uniform(bufferIndex, 8u);
    uvec4 transition = ReadStorageAlignedUVec4Uniform(bufferIndex, 12u);
    GPUEnvironmentData environment;
    environment.EnvironmentTextureIndex = int(textureIndices.x);
    environment.IrradianceTextureIndex = int(textureIndices.y);
    environment.PrefilteredTextureIndex = int(textureIndices.z);
    environment.BrdfLutTextureIndex = int(textureIndices.w);
    environment.SkyIntensity = intensities.x;
    environment.DiffuseIntensity = intensities.y;
    environment.SpecularIntensity = intensities.z;
    environment.RotationRadians = intensities.w;
    environment.PrefilteredMipCount = controls.x;
    environment.Enabled = controls.y;
    environment.DebugView = controls.z;
    environment.DebugMipLevel = controls.w;
    environment.NextPrefilteredTextureIndex = int(transition.x);
    environment.SourceKind = transition.y;
    environment.AtmosphereFlags = transition.z;
    environment.PrefilteredBlend = uintBitsToFloat(transition.w);
    environment.SunDirectionAndAngularRadius = ReadStorageAlignedVec4Uniform(bufferIndex, 16u);
    environment.SunRadianceAndElevation = ReadStorageAlignedVec4Uniform(bufferIndex, 20u);
    environment.MoonDirectionAndAngularRadius = ReadStorageAlignedVec4Uniform(bufferIndex, 24u);
    environment.MoonRadianceAndNightBlend = ReadStorageAlignedVec4Uniform(bufferIndex, 28u);
    environment.GroundAlbedoAndTurbidity = ReadStorageAlignedVec4Uniform(bufferIndex, 32u);
    environment.AtmosphereParameters = ReadStorageAlignedVec4Uniform(bufferIndex, 36u);
    environment.GroundRadianceAndAirglow = ReadStorageAlignedVec4Uniform(bufferIndex, 40u);
    environment.HosekParametersR0 = ReadStorageAlignedVec4Uniform(bufferIndex, 44u);
    environment.HosekParametersR1 = ReadStorageAlignedVec4Uniform(bufferIndex, 48u);
    environment.HosekParametersR2 = ReadStorageAlignedVec4Uniform(bufferIndex, 52u);
    environment.HosekParametersG0 = ReadStorageAlignedVec4Uniform(bufferIndex, 56u);
    environment.HosekParametersG1 = ReadStorageAlignedVec4Uniform(bufferIndex, 60u);
    environment.HosekParametersG2 = ReadStorageAlignedVec4Uniform(bufferIndex, 64u);
    environment.HosekParametersB0 = ReadStorageAlignedVec4Uniform(bufferIndex, 68u);
    environment.HosekParametersB1 = ReadStorageAlignedVec4Uniform(bufferIndex, 72u);
    environment.HosekParametersB2 = ReadStorageAlignedVec4Uniform(bufferIndex, 76u);
    environment.HosekRadiances = ReadStorageAlignedVec4Uniform(bufferIndex, 80u);
    environment.DiffuseIrradianceSh0 = ReadStorageAlignedVec4Uniform(bufferIndex, 84u);
    environment.DiffuseIrradianceSh1 = ReadStorageAlignedVec4Uniform(bufferIndex, 88u);
    environment.DiffuseIrradianceSh2 = ReadStorageAlignedVec4Uniform(bufferIndex, 92u);
    environment.DiffuseIrradianceSh3 = ReadStorageAlignedVec4Uniform(bufferIndex, 96u);
    environment.DiffuseIrradianceSh4 = ReadStorageAlignedVec4Uniform(bufferIndex, 100u);
    environment.DiffuseIrradianceSh5 = ReadStorageAlignedVec4Uniform(bufferIndex, 104u);
    environment.DiffuseIrradianceSh6 = ReadStorageAlignedVec4Uniform(bufferIndex, 108u);
    environment.DiffuseIrradianceSh7 = ReadStorageAlignedVec4Uniform(bufferIndex, 112u);
    environment.DiffuseIrradianceSh8 = ReadStorageAlignedVec4Uniform(bufferIndex, 116u);
    return environment;
}

GPUDdgiEmissiveSurface ReadDdgiEmissiveSurface(uint sourceIndex)
{
    uint baseWord = sourceIndex * uint(SIZEOF_GPU_DDGI_EMISSIVE_SURFACE / 4);
    uint surfaceBufferIndex = uint(SIMPLE_DDGI_EMISSIVE_SURFACE_BUFFER_INDEX);
    GPUDdgiEmissiveSurface surface;
    surface.Uv0Vertex01 = ReadStorageAlignedVec4Uniform(surfaceBufferIndex, baseWord + 0u);
    surface.Uv0Vertex2Uv1Vertex0 = ReadStorageAlignedVec4Uniform(surfaceBufferIndex, baseWord + 4u);
    surface.Uv1Vertex12 = ReadStorageAlignedVec4Uniform(surfaceBufferIndex, baseWord + 8u);
    surface.MaterialAndVertexAlpha = ReadStorageAlignedVec4Uniform(surfaceBufferIndex, baseWord + 12u);
    return surface;
}

GPUEnvironmentData ReadEnvironmentData()
{
    return ReadEnvironmentDataFrom(uint(ENVIRONMENT_DATA_BUFFER_INDEX));
}

GPUEnvironmentData ReadGiEnvironmentData()
{
    return ReadEnvironmentDataFrom(uint(ENVIRONMENT_GI_DATA_BUFFER_INDEX));
}

const uint ENVIRONMENT_ATMOSPHERE_FLAG_ANALYTIC = 1u << 0;
const uint ENVIRONMENT_ATMOSPHERE_FLAG_PREFILTER_READY = 1u << 1;

bool EnvironmentUsesAnalyticSky(GPUEnvironmentData environment)
{
    return (environment.AtmosphereFlags & ENVIRONMENT_ATMOSPHERE_FLAG_ANALYTIC) != 0u;
}

vec3 RotateEnvironmentSampleDirection(vec3 direction, float radians)
{
    float cosine = cos(radians);
    float sine = sin(radians);
    return vec3(
        direction.x * cosine - direction.z * sine,
        direction.y,
        direction.x * sine + direction.z * cosine);
}

float EvaluateHosekWilkieChannel(
    float directionY,
    float gamma,
    vec4 parameters0,
    vec4 parameters1,
    vec4 parameters2,
    float radianceScale)
{
    float cosGamma = cos(gamma);
    float cosGamma2 = cosGamma * cosGamma;
    float cosTheta = abs(directionY);
    float exponentialM = exp(parameters1.x * gamma);
    float mieDenominator = pow(max(
        1.0 + parameters2.x * parameters2.x -
        2.0 * parameters2.x * cosGamma,
        0.00001), 1.5);
    float mieM = (1.0 + cosGamma2) / mieDenominator;
    float lhs = 1.0 + parameters0.x *
        exp(parameters0.y / (cosTheta + 0.01));
    float rhs = parameters0.z +
        parameters0.w * exponentialM +
        parameters1.y * cosGamma2 +
        parameters1.z * mieM +
        parameters1.w * sqrt(cosTheta);
    return radianceScale * lhs * rhs;
}

float EnvironmentHash13(vec3 value)
{
    value = fract(value * 0.1031);
    value += dot(value, value.yzx + 33.33);
    return fract((value.x + value.y) * value.z);
}

vec3 EvaluateEnvironmentDisc(
    vec3 direction,
    vec3 toDisc,
    float angularRadius,
    vec3 irradiance)
{
    float radius = clamp(angularRadius, 0.0005, 0.05);
    float disc = smoothstep(
        cos(radius * 1.08),
        cos(radius * 0.92),
        dot(direction, toDisc));
    float solidAngle = 2.0 * 3.14159265359 * (1.0 - cos(radius));
    return min(
        max(irradiance, vec3(0.0)) / max(solidAngle, 0.000001),
        vec3(60000.0)) * disc;
}

vec3 EvaluateProceduralEnvironmentRadiance(
    GPUEnvironmentData environment,
    vec3 direction,
    bool diffuseTransport,
    bool includeCelestialDiscs,
    bool includeStars)
{
    vec3 safeDirection = length(direction) > 0.00001
        ? normalize(direction)
        : vec3(0.0, 1.0, 0.0);
    if (safeDirection.y < 0.0)
    {
        return max(environment.GroundRadianceAndAirglow.xyz, vec3(0.0)) *
            max(environment.SkyIntensity, 0.0);
    }

    vec3 toSun = normalize(environment.SunDirectionAndAngularRadius.xyz);
    float gamma = acos(clamp(dot(safeDirection, toSun), -1.0, 1.0));
    vec3 daylight = vec3(
        EvaluateHosekWilkieChannel(
            safeDirection.y,
            gamma,
            environment.HosekParametersR0,
            environment.HosekParametersR1,
            environment.HosekParametersR2,
            environment.HosekRadiances.x),
        EvaluateHosekWilkieChannel(
            safeDirection.y,
            gamma,
            environment.HosekParametersG0,
            environment.HosekParametersG1,
            environment.HosekParametersG2,
            environment.HosekRadiances.y),
        EvaluateHosekWilkieChannel(
            safeDirection.y,
            gamma,
            environment.HosekParametersB0,
            environment.HosekParametersB1,
            environment.HosekParametersB2,
            environment.HosekRadiances.z));
    daylight = max(daylight, vec3(0.0)) *
        environment.AtmosphereParameters.y *
        environment.AtmosphereParameters.x;
    if (diffuseTransport)
    {
        daylight *= smoothstep(
            radians(3.0),
            radians(10.0),
            gamma);
    }

    float horizonBand = exp(-safeDirection.y * 7.0);
    vec2 directionAzimuth = length(safeDirection.xz) > 0.00001
        ? normalize(safeDirection.xz)
        : vec2(0.0, 1.0);
    vec2 sunAzimuth = length(toSun.xz) > 0.00001
        ? normalize(toSun.xz)
        : vec2(0.0, 1.0);
    float towardSun = pow(max(dot(directionAzimuth, sunAzimuth), 0.0), 4.0);
    vec3 twilightColor = mix(
        vec3(0.012, 0.024, 0.080),
        vec3(1.15, 0.18, 0.025),
        towardSun);
    vec3 twilight = twilightColor *
        environment.AtmosphereParameters.z *
        (0.12 + 0.88 * horizonBand) *
        environment.AtmosphereParameters.x;

    float nightBlend = environment.MoonRadianceAndNightBlend.w;
    vec3 nightGradient = mix(
        vec3(0.12, 0.18, 0.34),
        vec3(0.018, 0.035, 0.095),
        sqrt(clamp(safeDirection.y, 0.0, 1.0)));
    vec3 result = daylight + twilight +
        nightGradient * environment.GroundRadianceAndAirglow.w * nightBlend;

    if (includeCelestialDiscs)
    {
        result += EvaluateEnvironmentDisc(
            safeDirection,
            toSun,
            environment.SunDirectionAndAngularRadius.w,
            environment.SunRadianceAndElevation.xyz);
        result += EvaluateEnvironmentDisc(
            safeDirection,
            normalize(environment.MoonDirectionAndAngularRadius.xyz),
            environment.MoonDirectionAndAngularRadius.w,
            environment.MoonRadianceAndNightBlend.xyz);
    }

    if (includeStars && nightBlend > 0.0)
    {
        vec3 starCell = floor(safeDirection * 4096.0);
        float selector = EnvironmentHash13(starCell);
        float star = pow(smoothstep(0.9985, 1.0, selector), 6.0);
        vec3 starColor = mix(
            vec3(0.62, 0.75, 1.0),
            vec3(1.0),
            EnvironmentHash13(starCell.yzx + 17.0));
        result += starColor * star * environment.AtmosphereParameters.w * nightBlend;
    }

    return max(result, vec3(0.0)) * max(environment.SkyIntensity, 0.0);
}

vec3 EvaluateEnvironmentRadiance(
    GPUEnvironmentData environment,
    vec3 direction,
    bool diffuseTransport,
    bool includeCelestialDiscs,
    bool includeStars)
{
    if (environment.Enabled == 0u)
        return vec3(0.0);
    if (EnvironmentUsesAnalyticSky(environment))
    {
        return EvaluateProceduralEnvironmentRadiance(
            environment,
            direction,
            diffuseTransport,
            includeCelestialDiscs,
            includeStars);
    }
    if (environment.EnvironmentTextureIndex < 0)
        return vec3(0.0);
    vec3 sampleDirection = RotateEnvironmentSampleDirection(
        direction,
        environment.RotationRadians);
    return max(textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.EnvironmentTextureIndex)],
        sampleDirection,
        0.0).rgb, vec3(0.0)) * max(environment.SkyIntensity, 0.0);
}

void EvaluateEnvironmentShBasis(vec3 direction, out float basis[9])
{
    vec3 d = normalize(direction);
    basis[0] = 0.2820947918;
    basis[1] = 0.4886025119 * d.z;
    basis[2] = 0.4886025119 * d.y;
    basis[3] = 0.4886025119 * d.x;
    basis[4] = 1.0925484306 * d.x * d.z;
    basis[5] = 1.0925484306 * d.z * d.y;
    basis[6] = 0.3153915653 * (3.0 * d.y * d.y - 1.0);
    basis[7] = 1.0925484306 * d.x * d.y;
    basis[8] = 0.5462742153 * (d.x * d.x - d.z * d.z);
}

vec3 EvaluateEnvironmentDiffuseIrradianceUnscaled(
    GPUEnvironmentData environment,
    vec3 normal)
{
    if (environment.Enabled == 0u)
        return vec3(0.0);
    if (!EnvironmentUsesAnalyticSky(environment))
    {
        if (environment.IrradianceTextureIndex < 0)
            return vec3(0.0);
        vec3 sampleDirection = RotateEnvironmentSampleDirection(
            normal,
            environment.RotationRadians);
        return max(texture(
            BindlessCubeTextures[nonuniformEXT(environment.IrradianceTextureIndex)],
            sampleDirection).rgb, vec3(0.0));
    }

    float basis[9];
    EvaluateEnvironmentShBasis(normal, basis);
    vec3 irradiance =
        environment.DiffuseIrradianceSh0.xyz * basis[0] +
        environment.DiffuseIrradianceSh1.xyz * basis[1] +
        environment.DiffuseIrradianceSh2.xyz * basis[2] +
        environment.DiffuseIrradianceSh3.xyz * basis[3] +
        environment.DiffuseIrradianceSh4.xyz * basis[4] +
        environment.DiffuseIrradianceSh5.xyz * basis[5] +
        environment.DiffuseIrradianceSh6.xyz * basis[6] +
        environment.DiffuseIrradianceSh7.xyz * basis[7] +
        environment.DiffuseIrradianceSh8.xyz * basis[8];
    return max(irradiance, vec3(0.0));
}

vec3 EvaluateEnvironmentDiffuseIrradiance(
    GPUEnvironmentData environment,
    vec3 normal)
{
    return EvaluateEnvironmentDiffuseIrradianceUnscaled(environment, normal) *
        max(environment.DiffuseIntensity, 0.0);
}

vec3 EvaluateEnvironmentTransportIrradiance(
    GPUEnvironmentData environment,
    vec3 normal)
{
    return EvaluateEnvironmentDiffuseIrradianceUnscaled(environment, normal) *
        max(environment.SkyIntensity, 0.0);
}

vec3 SampleEnvironmentPrefilteredRadiance(
    GPUEnvironmentData environment,
    vec3 direction,
    float lod)
{
    if (environment.Enabled == 0u)
        return vec3(0.0);
    if (EnvironmentUsesAnalyticSky(environment) &&
        (environment.AtmosphereFlags & ENVIRONMENT_ATMOSPHERE_FLAG_PREFILTER_READY) == 0u)
    {
        return EvaluateProceduralEnvironmentRadiance(
            environment,
            direction,
            false,
            true,
            true);
    }
    if (environment.PrefilteredTextureIndex < 0)
        return vec3(0.0);
    vec3 sampleDirection = EnvironmentUsesAnalyticSky(environment)
        ? normalize(direction)
        : RotateEnvironmentSampleDirection(direction, environment.RotationRadians);
    vec3 current = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.PrefilteredTextureIndex)],
        sampleDirection,
        lod).rgb;
    if (environment.NextPrefilteredTextureIndex < 0 ||
        environment.NextPrefilteredTextureIndex == environment.PrefilteredTextureIndex)
    {
        return max(current, vec3(0.0));
    }
    vec3 next = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.NextPrefilteredTextureIndex)],
        sampleDirection,
        lod).rgb;
    return max(mix(current, next, clamp(environment.PrefilteredBlend, 0.0, 1.0)), vec3(0.0));
}

GPUReflectionProbeHeader ReadReflectionProbeHeader()
{
    uint bufferIndex = uint(REFLECTION_PROBE_BUFFER_INDEX);
    uvec4 textureControls = ReadStorageAlignedUVec4Uniform(bufferIndex, 0u);
    uvec4 lightingControls = ReadStorageAlignedUVec4Uniform(bufferIndex, 4u);
    uvec4 debugControls = ReadStorageAlignedUVec4Uniform(bufferIndex, 8u);
    uvec4 screenTraceControls =
        ReadStorageAlignedUVec4Uniform(bufferIndex, 12u);
    uvec4 rayTraceControls =
        ReadStorageAlignedUVec4Uniform(bufferIndex, 16u);
    GPUReflectionProbeHeader header;
    header.ProbeCount = int(textureControls.x);
    header.MaxProbesPerPixel = int(textureControls.y);
    header.ProbeCubemapArrayTextureIndex = int(textureControls.z);
    header.DebugTextureIndex = int(textureControls.w);
    header.Intensity = uintBitsToFloat(lightingControls.x);
    header.GlobalFallbackIntensity = uintBitsToFloat(lightingControls.y);
    header.ProbeMipCount = lightingControls.z;
    header.Flags = lightingControls.w;
    header.DebugView = debugControls.x;
    header.DebugProbeIndex = int(debugControls.y);
    header.DebugCubemapFace = int(debugControls.z);
    header.DebugMipLevel = int(debugControls.w);
    header.SsrMaximumSteps = screenTraceControls.x;
    header.SsrMaximumDistance = uintBitsToFloat(screenTraceControls.y);
    header.SsrConfidenceThreshold = uintBitsToFloat(screenTraceControls.z);
    header.SceneReflectionRayTaskBudget = screenTraceControls.w;
    header.RayQueryHitLightLimit = rayTraceControls.x;
    header.SceneReflectionSsrSampleBudget = rayTraceControls.y;
    header.Padding1 = rayTraceControls.z;
    header.Padding2 = rayTraceControls.w;
    return header;
}

GPUReflectionProbe ReadReflectionProbe(uint probeIndex)
{
    uint baseWord = uint(SIZEOF_GPU_REFLECTION_PROBE_HEADER / 4) + probeIndex * uint(SIZEOF_GPU_REFLECTION_PROBE / 4);
    uint bufferIndex = uint(REFLECTION_PROBE_BUFFER_INDEX);
    GPUReflectionProbe probe;
    probe.WorldToProbe = mat4(
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 0u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 4u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 8u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 12u));
    probe.PositionAndRadius = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 16u);
    probe.BoxMin = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 20u);
    probe.BoxMax = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 24u);
    probe.BlendParams = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 28u);
    uvec4 metadata = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 32u);
    probe.CubemapArrayIndex = int(metadata.x);
    probe.Shape = int(metadata.y);
    probe.Flags = int(metadata.z);
    probe.Priority = int(metadata.w);
    return probe;
}

GPUSpotShadow ReadSpotShadow(uint shadowIndex)
{
    uint baseWord = shadowIndex * uint(SIZEOF_GPU_SPOT_SHADOW / 4);
    uint bufferIndex = uint(SPOT_SHADOW_DATA_BUFFER_INDEX);
    GPUSpotShadow shadow;
    shadow.LightViewProjection = mat4(
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 0u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 4u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 8u),
        ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 12u));
    shadow.AtlasScaleOffset = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 16u);
    shadow.BiasStrengthTexelSize = ReadStorageAlignedVec4Uniform(bufferIndex, baseWord + 20u);
    uvec4 metadata = ReadStorageAlignedUVec4Uniform(bufferIndex, baseWord + 24u);
    shadow.LightIndex = int(metadata.x);
    shadow.AtlasTile = int(metadata.y);
    shadow.PcfRadius = int(metadata.z);
    shadow.Enabled = int(metadata.w);
    return shadow;
}

mat4 ReadPointShadowFaceMatrix(uint shadowIndex, uint faceIndex)
{
    uint baseWord = shadowIndex * uint(SIZEOF_GPU_POINT_SHADOW / 4) + faceIndex * 16u;
    return mat4(
        ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 0u),
        ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 4u),
        ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 8u),
        ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 12u));
}

GPUPointShadow ReadPointShadow(uint shadowIndex)
{
    uint baseWord = shadowIndex * uint(SIZEOF_GPU_POINT_SHADOW / 4);
    GPUPointShadow shadow;
    shadow.FaceViewProjection0 = ReadPointShadowFaceMatrix(shadowIndex, 0u);
    shadow.FaceViewProjection1 = ReadPointShadowFaceMatrix(shadowIndex, 1u);
    shadow.FaceViewProjection2 = ReadPointShadowFaceMatrix(shadowIndex, 2u);
    shadow.FaceViewProjection3 = ReadPointShadowFaceMatrix(shadowIndex, 3u);
    shadow.FaceViewProjection4 = ReadPointShadowFaceMatrix(shadowIndex, 4u);
    shadow.FaceViewProjection5 = ReadPointShadowFaceMatrix(shadowIndex, 5u);
    shadow.PositionRange = ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 96u);
    shadow.BiasStrengthTexelSize = ReadStorageAlignedVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 100u);
    uvec4 metadata = ReadStorageAlignedUVec4Uniform(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 104u);
    shadow.LightIndex = int(metadata.x);
    shadow.CubemapIndex = int(metadata.y);
    shadow.PcfRadius = int(metadata.z);
    shadow.Enabled = int(metadata.w);
    return shadow;
}

vec4 ReadShadowCascadeSplits()
{
    return ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS / 4));
}

vec4 ReadShadowSettings()
{
    return ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_SETTINGS / 4));
}

vec4 ReadShadowIndices()
{
    return ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_INDICES / 4));
}

vec4 ReadShadowCascadeTransitionData()
{
    return ReadStorageAlignedVec4Uniform(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_CASCADE_TRANSITION_DATA / 4));
}

vec4 ReadDirectionalShadowWorldTexelSizes()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_CASCADE_WORLD_TEXEL_SIZES / 4));
}

vec4 ReadDirectionalShadowFilterAndBias()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_FILTER_AND_BIAS / 4));
}

vec4 ReadDirectionalShadowModeAndRayDistance()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_MODE_AND_RAY_DISTANCE / 4));
}

vec4 ReadDirectionalShadowTemporalAndSampling()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_TEMPORAL_AND_SAMPLING / 4));
}

vec4 ReadDirectionalShadowRaySceneBoundsMinimum()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MINIMUM / 4));
}

vec4 ReadDirectionalShadowRaySceneBoundsMaximum()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RAY_SCENE_BOUNDS_MAXIMUM / 4));
}

vec4 ReadDirectionalShadowRuntimeFlags()
{
    uint baseWord = uint(SIZEOF_GPU_SHADOW_DATA / 4);
    return ReadStorageAlignedVec4Uniform(
        uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX),
        baseWord + uint(OFFSET_GPU_DIRECTIONAL_SHADOW_PARAMETERS_RUNTIME_FLAGS / 4));
}

int ResolveDirectionalShadowPcfRadius(
    uint cascade,
    int configuredRadius,
    vec4 worldTexelSizes)
{
    int radius = clamp(configuredRadius, 0, 3);
    int radiusMode = int(clamp(
        round(ReadDirectionalShadowRuntimeFlags().y),
        0.0,
        1.0));
    if (radiusMode == 0 || radius == 0)
        return radius;

    float referenceTexelSize = worldTexelSizes.x;
    float cascadeTexelSize = worldTexelSizes[int(min(cascade, 3u))];
    if (referenceTexelSize <= 1.0e-7 ||
        cascadeTexelSize <= 1.0e-7 ||
        isnan(referenceTexelSize) || isinf(referenceTexelSize) ||
        isnan(cascadeTexelSize) || isinf(cascadeTexelSize))
    {
        return radius;
    }

    // Radius r spans approximately r + 1 texels from the bilinear center.
    // Reduce that support as cascade texels grow to preserve the near-cascade
    // world footprint while avoiding redundant far-cascade taps.
    float targetSupport = float(radius + 1) *
        referenceTexelSize / cascadeTexelSize;
    return clamp(int(round(targetSupport)) - 1, 0, radius);
}

void WriteTiledLightHeader(uint tileIndex, uint lightCount, uint lightOffset, uint overflowCount)
{
    uint baseWord = tileIndex * uint(SIZEOF_GPU_TILED_LIGHT_HEADER / 4);
    WriteStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 0u, lightCount);
    WriteStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 1u, lightOffset);
    WriteStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 2u, overflowCount);
    WriteStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 3u, 0u);
}

void WriteTiledLightIndex(uint lightListOffset, uint lightIndex)
{
    uint baseWord = lightListOffset * uint(SIZEOF_GPU_LIGHT_INDEX / 4);
    WriteStorageWord(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 0u, lightIndex);
}

#endif // NJULF_COMMON_GLSL
