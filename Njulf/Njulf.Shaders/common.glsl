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

// ============================================
// FRAME CONFIGURATION
// ============================================

const int FRAMES_IN_FLIGHT = 2;

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
const int SCENE_SUBMISSION_COUNTER_BUFFER_BASE_INDEX = 112;
const int SCENE_SUBMISSION_COUNTER_BUFFER_FRAME1_INDEX = 113;
const int SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_BASE_INDEX = 114;
const int SCENE_OPAQUE_INDIRECT_DISPATCH_BUFFER_FRAME1_INDEX = 115;
const int SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 116;
const int SCENE_SOLID_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 117;
const int SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_BASE_INDEX = 118;
const int SCENE_MASKED_DEPTH_COMPACTED_MESHLET_DRAW_BUFFER_FRAME1_INDEX = 119;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX = 120;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX = 121;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX = 122;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX = 123;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX = 124;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX = 125;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX = 126;
const int SCENE_DIRECTIONAL_STATIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX = 127;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_BASE_INDEX = 128;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE0_BUFFER_FRAME1_INDEX = 129;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_BASE_INDEX = 130;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE1_BUFFER_FRAME1_INDEX = 131;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_BASE_INDEX = 132;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE2_BUFFER_FRAME1_INDEX = 133;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_BASE_INDEX = 134;
const int SCENE_DIRECTIONAL_DYNAMIC_SHADOW_COMPACTED_CASCADE3_BUFFER_FRAME1_INDEX = 135;
const int DDGI_PROBE_VOLUME_BUFFER_INDEX = 136;
const int DDGI_PROBE_STATE_BUFFER_INDEX = 137;
const int DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX = 138;
const int DDGI_PROBE_RELOCATION_CLASSIFICATION_BUFFER_INDEX = 139;
const int DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX = 140;
const int DDGI_VISIBILITY_ATLAS_BUFFER_INDEX = 141;
const int DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX = 142;
const int STATIC_BUFFER_COUNT = 143;
const uint GPU_PARTICLE_BLEND_BUCKET_COUNT = 5u;

const uint MESHLET_DRAW_FLAG_NEEDS_GPU_FRUSTUM_TEST = 1u << 0;
const uint MESHLET_DRAW_FLAG_CPU_FRUSTUM_VISIBLE = 1u << 1;
const uint MESHLET_DRAW_FLAG_OBJECT_FULLY_INSIDE_FRUSTUM = 1u << 2;
const uint MESHLET_DRAW_FLAG_MATERIAL_MASKED = 1u << 3;
const uint MESHLET_DRAW_FLAG_MATERIAL_BLEND = 1u << 4;
const uint MESHLET_DRAW_FLAG_CAN_HIZ_TEST = 1u << 5;

const uint FOLIAGE_PROTOTYPE_FLAG_CAST_SHADOWS = 1u << 0;
const uint FOLIAGE_PROTOTYPE_FLAG_FAR_IMPOSTOR = 1u << 1;

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
const int SCENE_NORMAL_TEXTURE_INDEX = 26;
const int SCENE_MATERIAL_TEXTURE_INDEX = 27;
const int SSGI_TRACE_SOURCE_TEXTURE_INDEX = 28;
const int SSGI_RAW_TEXTURE_INDEX = 29;
const int SSGI_FILTERED_TEXTURE_INDEX = 30;
const int SSGI_HISTORY_TEXTURE_INDEX = 31;
const int SSGI_PREVIOUS_DEPTH_TEXTURE_INDEX = 32;
const int SSGI_PREVIOUS_NORMAL_TEXTURE_INDEX = 33;
const int SSGI_MOMENTS_TEXTURE_INDEX = 34;
const int SSGI_HISTORY_LENGTH_TEXTURE_INDEX = 35;
const int GI_FINAL_DIFFUSE_TEXTURE_INDEX = 36;
const int LDR_SCENE_COLOR_TEXTURE_INDEX = 37;
const int SMAA_EDGES_TEXTURE_INDEX = 38;
const int SMAA_BLEND_WEIGHTS_TEXTURE_INDEX = 39;
const int SMAA_AREA_TEXTURE_INDEX = 40;
const int SMAA_SEARCH_TEXTURE_INDEX = 41;
const int MOTION_VECTOR_TEXTURE_INDEX = 42;
const int TAA_HISTORY_TEXTURE_INDEX = 43;
const int FOGGED_SCENE_COLOR_TEXTURE_INDEX = 44;
const int REFLECTION_PROBE_CUBEMAP_ARRAY_TEXTURE_INDEX = 45;
const int REFLECTION_PROBE_DEBUG_TEXTURE_INDEX = 46;
const int WEIGHTED_OIT_ACCUMULATION_TEXTURE_INDEX = 47;
const int WEIGHTED_OIT_REVEALAGE_TEXTURE_INDEX = 48;
const int FIRST_DYNAMIC_TEXTURE_INDEX = 49;

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
    uint Padding0;
    uint Padding1;
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
    vec4 EmissiveOffsetScale;
    vec4 TextureRotations;
    vec4 TextureTexCoordSets;
    int AlbedoTextureIndex;
    int NormalTextureIndex;
    int MetallicRoughnessTextureIndex;
    int EmissiveTextureIndex;
    uint FeatureFlags;
    int ExtensionDataIndex;
    uint Reserved0;
    uint Reserved1;
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
    int Padding0;
    int Padding1;
    int Padding2;
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
    uint Padding;
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
    float BladeHeight;
    float BladeWidth;
    vec4 LodDistances;
    vec4 WindParams;
    vec4 LightingParams;
};

struct GPUFoliagePatch
{
    vec4 BoundsMinDensity;
    vec4 BoundsMaxSeed;
    uint PrototypeIndex;
    uint ClusterOffset;
    uint ClusterCount;
    uint DensityTextureIndex;
    uint Seed;
    uint Flags;
    uint Padding0;
    uint Padding1;
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
};

struct GPUFoliageDispatchArgs
{
    uint GroupCountX;
    uint GroupCountY;
    uint GroupCountZ;
    uint Padding0;
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
    uint Padding0;
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
    uint Padding1;
    uint Padding2;
    uint Padding3;
    uint Padding4;
    uint Padding5;
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
};

struct GPUTiledLightHeader
{
    uint LightCount;
    uint LightOffset;
    uint Padding0;
    uint Padding1;
};

struct GPULightIndex
{
    uint LightIndex;
    uint Padding0;
    uint Padding1;
    uint Padding2;
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
    uint Padding0;
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
    uint LightCount;
    uint LocalLightCount;
    uint HiZTextureIndex;
    uint HiZMipCount;
    uint OcclusionCullingEnabled;
    float OcclusionBias;
    uint DebugAndAoFlags;
};

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
};

struct GPULightCullPushConstants
{
    mat4 ViewProjectionMatrix;
    mat4 InverseViewProjectionMatrix;
    vec3 CameraPosition;
    float Padding0;
    vec2 ScreenDimensions;
    float NearPlane;
    float FarPlane;
    uint LightCount;
    uint MaxLightsPerTile;
    uint TileCountX;
    uint TileCountY;
    uint DepthTextureIndex;
    uint Padding1;
    uint Padding2;
    uint Padding3;
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
    int Padding0;
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
    uint VertexOffset;
    uint IndexOffset;
    uint MaterialIndex;
    uint Padding0;
    mat4 WorldMatrixInverseTranspose;
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
    uint Padding0;
};

// Descriptor arrays matching BindlessHeap. Heterogeneous storage buffers are
// addressed by descriptor array element and interpreted by pass-specific code.
layout(set = 0, binding = 0) buffer BindlessStorageBuffer
{
    uint Words[];
} BindlessStorageBuffers[];

layout(set = 1, binding = 0) uniform sampler2D BindlessTextures[];
layout(set = 1, binding = 0) uniform sampler2DArray BindlessArrayTextures[];
layout(set = 1, binding = 0) uniform samplerCube BindlessCubeTextures[];
layout(set = 1, binding = 0) uniform samplerCubeArray BindlessCubeArrayTextures[];

// Documented sizes (bytes). Tests parse these constants and compare them to C#.
const int SIZEOF_GPU_VERTEX = 80;
const int SIZEOF_GPU_VERTEX_POSITION_STREAM = 16;
const int SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM = 32;
const int SIZEOF_GPU_VERTEX_UV_COLOR_STREAM = 32;
const int SIZEOF_GPU_MESH_INFO = 64;
const int SIZEOF_GPU_VERTEX_SKINNING_DATA = 32;
const int SIZEOF_GPU_SKINNING_DISPATCH = 32;
const int SIZEOF_GPU_SKINNING_PUSH_CONSTANTS = 16;
const int SIZEOF_GPU_PARTICLE_INSTANCE = 96;
const int SIZEOF_GPU_PARTICLE_BATCH = 16;
const int SIZEOF_GPU_PARTICLE_FRAME_DATA = 224;
const int SIZEOF_GPU_PARTICLE_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_PARTICLE_EMITTER = 256;
const int SIZEOF_GPU_PARTICLE_CURVE_SAMPLE = 32;
const int SIZEOF_GPU_PARTICLE_STATE = 80;
const int SIZEOF_GPU_PARTICLE_COUNTERS = 88;
const int SIZEOF_GPU_PARTICLE_DRAW_COMMAND = 16;
const int SIZEOF_GPU_PARTICLE_SORT_KEY = 8;
const int SIZEOF_GPU_PARTICLE_RESET_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_PARTICLE_SIMULATE_PUSH_CONSTANTS = 48;
const int SIZEOF_GPU_PARTICLE_SORT_PUSH_CONSTANTS = 32;
const int SIZEOF_GPU_MESHLET = 48;
const int SIZEOF_GPU_OBJECT_DATA = 208;
const int SIZEOF_GPU_DEBUG_LINE_VERTEX = 32;
const int SIZEOF_GPU_MATERIAL_DATA = 192;
const int SIZEOF_GPU_MATERIAL_EXTENSION_DATA = 548;
const int SIZEOF_GPU_LIGHT = 64;
const int SIZEOF_GPU_SCENE_DATA = 400;
const int SIZEOF_GPU_MESHLET_DRAW_COMMAND = 16;
const int SIZEOF_GPU_PACKED_MESHLET_DRAW_COMMAND = 32;
const int SIZEOF_GPU_MESHLET_TASK_FRAME_DATA = 96;
const int SIZEOF_GPU_FOLIAGE_PROTOTYPE = 96;
const int SIZEOF_GPU_FOLIAGE_PATCH = 64;
const int SIZEOF_GPU_FOLIAGE_CLUSTER = 64;
const int SIZEOF_GPU_FOLIAGE_INSTANCE = 64;
const int SIZEOF_GPU_FOLIAGE_MESHLET_DRAW_COMMAND = 48;
const int SIZEOF_GPU_FOLIAGE_COUNTERS = 40;
const int SIZEOF_GPU_FOLIAGE_DISPATCH_ARGS = 16;
const int SIZEOF_GPU_SCENE_SUBMISSION_COUNTERS = 240;
const int SIZEOF_GPU_SCENE_OPAQUE_COMPACTION_PUSH_CONSTANTS = 116;
const int SIZEOF_GPU_FOLIAGE_CULL_PUSH_CONSTANTS = 52;
const int SIZEOF_GPU_FOLIAGE_DRAW_PUSH_CONSTANTS = 128;
const int SIZEOF_GPU_TILED_LIGHT_HEADER = 16;
const int SIZEOF_GPU_LIGHT_INDEX = 16;
const int SIZEOF_GPU_SCREEN_TO_VIEW_PARAMS = 32;
const int SIZEOF_GPU_LIGHT_CULLING_PARAMS = 192;
const int SIZEOF_GPU_DEPTH_PUSH_CONSTANTS = 96;
const int SIZEOF_GPU_FORWARD_PUSH_CONSTANTS = 256;
const int SIZEOF_GPU_MOTION_VECTOR_PUSH_CONSTANTS = 160;
const int SIZEOF_GPU_LIGHT_CULL_PUSH_CONSTANTS = 192;
const int SIZEOF_GPU_SHADOW_DATA = 304;
const int SIZEOF_GPU_SPOT_SHADOW = 112;
const int SIZEOF_GPU_POINT_SHADOW = 432;
const int SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX = 16;
const int SIZEOF_GPU_ENVIRONMENT_DATA = 48;
const int SIZEOF_GPU_REFLECTION_PROBE_HEADER = 48;
const int SIZEOF_GPU_REFLECTION_PROBE = 144;
const int SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER = 64;
const int SIZEOF_GPU_DDGI_PROBE_VOLUME = 96;
const int SIZEOF_GPU_DDGI_PROBE_STATE = 64;
const int SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST = 16;
const int SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION = 32;
const int SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE = 80;
const int SIZEOF_GPU_DDGI_UPDATE_PUSH_CONSTANTS = 80;
const int SIZEOF_GPU_FOG_PUSH_CONSTANTS = 224;
const int SIZEOF_GPU_ANTI_ALIASING_PUSH_CONSTANTS = 100;
const int SIZEOF_GPU_AMBIENT_OCCLUSION_PUSH_CONSTANTS = 176;
const int SIZEOF_GPU_AMBIENT_OCCLUSION_BLUR_PUSH_CONSTANTS = 96;

const uint DIAGNOSTIC_DEPTH_CANDIDATES = 0u;
const uint DIAGNOSTIC_DEPTH_FRUSTUM_CULLED = 1u;
const uint DIAGNOSTIC_DEPTH_EMITTED = 2u;
const uint DIAGNOSTIC_FORWARD_CANDIDATES = 3u;
const uint DIAGNOSTIC_FORWARD_FRUSTUM_CULLED = 4u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_CULLED = 5u;
const uint DIAGNOSTIC_FORWARD_EMITTED = 6u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_TESTED = 7u;
const uint DIAGNOSTIC_SSGI_HISTORY_REJECTED = 8u;

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

const int OFFSET_GPU_PARTICLE_INSTANCE_POSITION_SIZE = 0;
const int OFFSET_GPU_PARTICLE_INSTANCE_VELOCITY_ROTATION = 16;
const int OFFSET_GPU_PARTICLE_INSTANCE_COLOR = 32;
const int OFFSET_GPU_PARTICLE_INSTANCE_EMISSIVE_LIFETIME_SOFT_CLIP = 48;
const int OFFSET_GPU_PARTICLE_INSTANCE_TEXTURE_INDEX = 64;
const int OFFSET_GPU_PARTICLE_INSTANCE_BLEND_MODE = 80;
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

const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MESHLET_INDEX = 0;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_INSTANCE_ID = 4;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_MATERIAL_INDEX = 8;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_FLAGS = 12;
const int OFFSET_GPU_PACKED_MESHLET_DRAW_COMMAND_WORLD_CENTER_RADIUS = 16;

const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE0 = 0;
const int OFFSET_GPU_MESHLET_TASK_FRAME_DATA_FRUSTUM_PLANE5 = 80;

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
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_HEIGHT = 40;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_BLADE_WIDTH = 44;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_LOD_DISTANCES = 48;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_WIND_PARAMS = 64;
const int OFFSET_GPU_FOLIAGE_PROTOTYPE_LIGHTING_PARAMS = 80;

const int OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MIN_DENSITY = 0;
const int OFFSET_GPU_FOLIAGE_PATCH_BOUNDS_MAX_SEED = 16;
const int OFFSET_GPU_FOLIAGE_PATCH_PROTOTYPE_INDEX = 32;
const int OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_OFFSET = 36;
const int OFFSET_GPU_FOLIAGE_PATCH_CLUSTER_COUNT = 40;
const int OFFSET_GPU_FOLIAGE_PATCH_DENSITY_TEXTURE_INDEX = 44;
const int OFFSET_GPU_FOLIAGE_PATCH_SEED = 48;
const int OFFSET_GPU_FOLIAGE_PATCH_FLAGS = 52;

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

const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CAMERA_POSITION_TIME = 64;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_SCREEN_DIMENSIONS = 80;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CURRENT_FRAME_INDEX = 96;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_CLUSTER_DRAW_COUNT = 100;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_VISIBLE_CLUSTER_BUFFER_BASE_INDEX = 104;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_FLAGS = 108;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_DEBUG_VIEW = 112;
const int OFFSET_GPU_FOLIAGE_DRAW_PUSH_SHADOW_DENSITY_SCALE = 116;

const int OFFSET_GPU_DEPTH_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_DEPTH_PUSH_SCREEN_DIMENSIONS = 64;
const int OFFSET_GPU_DEPTH_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX = 80;

const int OFFSET_GPU_FORWARD_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_FORWARD_PUSH_INVERSE_VIEW_MATRIX = 64;
const int OFFSET_GPU_FORWARD_PUSH_INVERSE_PROJECTION_MATRIX = 128;
const int OFFSET_GPU_FORWARD_PUSH_CAMERA_POSITION = 192;
const int OFFSET_GPU_FORWARD_PUSH_TIME = 204;
const int OFFSET_GPU_FORWARD_PUSH_SCREEN_DIMENSIONS = 208;
const int OFFSET_GPU_FORWARD_PUSH_HIZ_TEXTURE_INDEX = 236;
const int OFFSET_GPU_FORWARD_PUSH_HIZ_MIP_COUNT = 240;
const int OFFSET_GPU_FORWARD_PUSH_OCCLUSION_CULLING_ENABLED = 244;
const int OFFSET_GPU_FORWARD_PUSH_OCCLUSION_BIAS = 248;
const int OFFSET_GPU_FORWARD_PUSH_DEBUG_AND_AO_FLAGS = 252;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_VIEW_PROJECTION_MATRIX = 64;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_SCREEN_DIMENSIONS = 128;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_CURRENT_FRAME_INDEX = 136;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_COUNT = 140;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_MESHLET_DRAW_BUFFER_BASE_INDEX = 144;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_FRAME_VALID = 148;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_TIME = 152;
const int OFFSET_GPU_MOTION_VECTOR_PUSH_PREVIOUS_TIME = 156;

const int OFFSET_GPU_LIGHT_CULL_PUSH_VIEW_PROJECTION_MATRIX = 0;
const int OFFSET_GPU_LIGHT_CULL_PUSH_INVERSE_VIEW_PROJECTION_MATRIX = 64;
const int OFFSET_GPU_LIGHT_CULL_PUSH_CAMERA_POSITION = 128;
const int OFFSET_GPU_LIGHT_CULL_PUSH_SCREEN_DIMENSIONS = 144;
const int OFFSET_GPU_LIGHT_CULL_PUSH_NEAR_PLANE = 152;
const int OFFSET_GPU_LIGHT_CULL_PUSH_FAR_PLANE = 156;
const int OFFSET_GPU_LIGHT_CULL_PUSH_LIGHT_COUNT = 160;
const int OFFSET_GPU_LIGHT_CULL_PUSH_TILE_COUNT_Y = 172;
const int OFFSET_GPU_LIGHT_CULL_PUSH_DEPTH_TEXTURE_INDEX = 176;

const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0 = 0;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION1 = 64;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION2 = 128;
const int OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION3 = 192;
const int OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS = 256;
const int OFFSET_GPU_SHADOW_DATA_SETTINGS = 272;
const int OFFSET_GPU_SHADOW_DATA_INDICES = 288;
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
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_VERTEX_OFFSET = 0;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_INDEX_OFFSET = 4;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_MATERIAL_INDEX = 8;
const int OFFSET_GPU_DDGI_RAY_QUERY_INSTANCE_WORLD_MATRIX_INVERSE_TRANSPOSE = 16;


const uint MESHLET_MAX_VERTICES = 64u;
const uint MESHLET_MAX_TRIANGLES = 126u;
const uint MESHLET_TASK_GROUP_SIZE = 1u;

#ifndef NJULF_GPU_DIAGNOSTIC_COUNTERS
#define NJULF_GPU_DIAGNOSTIC_COUNTERS 0
#endif

uint ReadStorageWord(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset];
}

void WriteStorageWord(uint bufferIndex, uint wordOffset, uint value)
{
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset] = value;
}

void WriteStorageFloat(uint bufferIndex, uint wordOffset, float value)
{
    WriteStorageWord(bufferIndex, wordOffset, floatBitsToUint(value));
}

void WriteStorageVec4(uint bufferIndex, uint wordOffset, vec4 value)
{
    WriteStorageFloat(bufferIndex, wordOffset + 0u, value.x);
    WriteStorageFloat(bufferIndex, wordOffset + 1u, value.y);
    WriteStorageFloat(bufferIndex, wordOffset + 2u, value.z);
    WriteStorageFloat(bufferIndex, wordOffset + 3u, value.w);
}

void IncrementRendererDiagnostic(uint frameIndex, uint counterIndex)
{
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], 1u);
}

void AddRendererDiagnostic(uint frameIndex, uint counterIndex, uint value)
{
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], value);
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
    return vec4(
        ReadStorageFloat(bufferIndex, wordOffset + 0u),
        ReadStorageFloat(bufferIndex, wordOffset + 1u),
        ReadStorageFloat(bufferIndex, wordOffset + 2u),
        ReadStorageFloat(bufferIndex, wordOffset + 3u));
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
    return ReadStorageVec3(uint(VERTEX_POSITION_BUFFER_INDEX), baseWord + 0u);
}

vec3 ReadSplitVertexNormal(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM / 4);
    return ReadStorageVec3(uint(VERTEX_NORMAL_TANGENT_BUFFER_INDEX), baseWord + 0u);
}

vec4 ReadSplitVertexTangent(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_NORMAL_TANGENT_STREAM / 4);
    return ReadStorageVec4(uint(VERTEX_NORMAL_TANGENT_BUFFER_INDEX), baseWord + 4u);
}

vec2 ReadSplitVertexTexCoord(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageVec2(uint(VERTEX_UV_COLOR_BUFFER_INDEX), baseWord + 0u);
}

vec2 ReadSplitVertexTexCoord2(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageVec2(uint(VERTEX_UV_COLOR_BUFFER_INDEX), baseWord + 2u);
}

vec4 ReadSplitVertexColor(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX_UV_COLOR_STREAM / 4);
    return ReadStorageVec4(uint(VERTEX_UV_COLOR_BUFFER_INDEX), baseWord + 4u);
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
    return ReadVertexFromBuffer(uint(VERTEX_BUFFER_INDEX), vertexIndex);
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

GPUMeshlet ReadMeshlet(uint meshletIndex)
{
    uint baseWord = meshletIndex * uint(SIZEOF_GPU_MESHLET / 4);
    GPUMeshlet meshlet;
    meshlet.BoundingSphereCenter = ReadStorageVec3(uint(MESHLET_BUFFER_INDEX), baseWord + 0u);
    meshlet.BoundingSphereRadius = ReadStorageFloat(uint(MESHLET_BUFFER_INDEX), baseWord + 3u);
    meshlet.VertexOffset = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 4u);
    meshlet.VertexCount = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 5u);
    meshlet.IndexOffset = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 6u);
    meshlet.IndexCount = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 7u);
    meshlet.LocalVertexOffset = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 8u);
    meshlet.LocalVertexCount = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 9u);
    meshlet.LocalTriangleOffset = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 10u);
    meshlet.LocalTriangleCount = ReadStorageWord(uint(MESHLET_BUFFER_INDEX), baseWord + 11u);
    return meshlet;
}

GPUMeshletDrawCommand ReadMeshletDrawCommandFromBase(uint bufferBaseIndex, uint frameIndex, uint drawIndex)
{
    uint bufferIndex = bufferBaseIndex + frameIndex;
    uint baseWord = drawIndex * uint(SIZEOF_GPU_MESHLET_DRAW_COMMAND / 4);
    GPUMeshletDrawCommand command;
    command.MeshletIndex = ReadStorageWord(bufferIndex, baseWord + 0u);
    command.InstanceId = ReadStorageWord(bufferIndex, baseWord + 1u);
    command.MaterialIndex = ReadStorageWord(bufferIndex, baseWord + 2u);
    command.Padding = ReadStorageWord(bufferIndex, baseWord + 3u);
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
    prototype.BladeHeight = ReadStorageFloat(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 10u);
    prototype.BladeWidth = ReadStorageFloat(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 11u);
    prototype.LodDistances = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 12u);
    prototype.WindParams = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 16u);
    prototype.LightingParams = ReadStorageVec4(uint(FOLIAGE_PROTOTYPE_BUFFER_INDEX), baseWord + 20u);
    return prototype;
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
    foliagePatch.DensityTextureIndex = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 11u);
    foliagePatch.Seed = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 12u);
    foliagePatch.Flags = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 13u);
    foliagePatch.Padding0 = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 14u);
    foliagePatch.Padding1 = ReadStorageWord(uint(FOLIAGE_PATCH_BUFFER_INDEX), baseWord + 15u);
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

uint ReadFoliageVisibleClusterIndex(uint visibleClusterBufferBaseIndex, uint frameIndex, uint drawIndex)
{
    return ReadStorageWord(visibleClusterBufferBaseIndex + frameIndex, drawIndex);
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
    return objectData;
}

GPUMaterialData ReadMaterial(uint materialIndex)
{
    uint baseWord = materialIndex * uint(SIZEOF_GPU_MATERIAL_DATA / 4);
    GPUMaterialData material;
    material.Albedo = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 0u);
    material.Emissive = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 4u);
    material.NormalScaleBias = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 8u);
    material.MetallicRoughnessAO = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 12u);
    material.BaseColorOffsetScale = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 16u);
    material.NormalOffsetScale = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 20u);
    material.MetallicRoughnessOffsetScale = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 24u);
    material.EmissiveOffsetScale = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 28u);
    material.TextureRotations = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 32u);
    material.TextureTexCoordSets = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 36u);
    material.AlbedoTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 40u));
    material.NormalTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 41u));
    material.MetallicRoughnessTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 42u));
    material.EmissiveTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 43u));
    material.FeatureFlags = ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 44u);
    material.ExtensionDataIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 45u));
    material.Reserved0 = ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 46u);
    material.Reserved1 = ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 47u);
    return material;
}

GPUMaterialExtensionData ReadMaterialExtension(uint extensionIndex)
{
    uint baseWord = extensionIndex * uint(SIZEOF_GPU_MATERIAL_EXTENSION_DATA / 4);
    GPUMaterialExtensionData data;
    data.Clearcoat = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 0u);
    data.SheenColor = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 4u);
    data.Anisotropy = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 8u);
    data.Transmission = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 12u);
    data.AttenuationColor = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 16u);
    data.Subsurface = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 20u);
    data.SpecularColor = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 24u);
    data.Iridescence = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 28u);
    data.Dispersion = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 32u);
    data.ClearcoatOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 36u);
    data.ClearcoatRoughnessOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 40u);
    data.ClearcoatNormalOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 44u);
    data.SheenColorOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 48u);
    data.SheenRoughnessOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 52u);
    data.AnisotropyOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 56u);
    data.TransmissionOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 60u);
    data.ThicknessOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 64u);
    data.SpecularOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 68u);
    data.SpecularColorOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 72u);
    data.IridescenceOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 76u);
    data.IridescenceThicknessOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 80u);
    data.SubsurfaceOffsetScale = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 84u);
    data.ExtensionTextureRotations0 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 88u);
    data.ExtensionTextureRotations1 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 92u);
    data.ExtensionTextureRotations2 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 96u);
    data.ExtensionTextureRotations3 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 100u);
    data.ExtensionTextureTexCoordSets0 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 104u);
    data.ExtensionTextureTexCoordSets1 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 108u);
    data.ExtensionTextureTexCoordSets2 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 112u);
    data.ExtensionTextureTexCoordSets3 = ReadStorageVec4(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 116u);
    data.ClearcoatTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 120u));
    data.ClearcoatRoughnessTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 121u));
    data.ClearcoatNormalTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 122u));
    data.SheenColorTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 123u));
    data.SheenRoughnessTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 124u));
    data.AnisotropyTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 125u));
    data.TransmissionTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 126u));
    data.ThicknessTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 127u));
    data.SubsurfaceTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 128u));
    data.SpecularTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 129u));
    data.SpecularColorTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 130u));
    data.IridescenceTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 131u));
    data.IridescenceThicknessTextureIndex = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 132u));
    data.Padding0 = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 133u));
    data.Padding1 = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 134u));
    data.Padding2 = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 135u));
    data.Padding3 = int(ReadStorageWord(uint(MATERIAL_EXTENSION_DATA_BUFFER_INDEX), baseWord + 136u));
    return data;
}

GPUTiledLightHeader ReadTiledLightHeader(uint tileIndex)
{
    uint baseWord = tileIndex * uint(SIZEOF_GPU_TILED_LIGHT_HEADER / 4);
    GPUTiledLightHeader header;
    header.LightCount = ReadStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 0u);
    header.LightOffset = ReadStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 1u);
    header.Padding0 = ReadStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 2u);
    header.Padding1 = ReadStorageWord(uint(TILED_LIGHT_HEADER_BUFFER_INDEX), baseWord + 3u);
    return header;
}

GPULight ReadLight(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LIGHT / 4);
    GPULight light;
    light.Position = ReadStorageVec3(uint(LIGHT_BUFFER_INDEX), baseWord + 0u);
    light.Intensity = ReadStorageFloat(uint(LIGHT_BUFFER_INDEX), baseWord + 3u);
    light.Color = ReadStorageVec3(uint(LIGHT_BUFFER_INDEX), baseWord + 4u);
    light.Range = ReadStorageFloat(uint(LIGHT_BUFFER_INDEX), baseWord + 7u);
    light.Direction = ReadStorageVec3(uint(LIGHT_BUFFER_INDEX), baseWord + 8u);
    light.SpotAngle = ReadStorageFloat(uint(LIGHT_BUFFER_INDEX), baseWord + 11u);
    light.Type = int(ReadStorageWord(uint(LIGHT_BUFFER_INDEX), baseWord + 12u));
    light.Padding0 = int(ReadStorageWord(uint(LIGHT_BUFFER_INDEX), baseWord + 13u));
    light.Padding1 = int(ReadStorageWord(uint(LIGHT_BUFFER_INDEX), baseWord + 14u));
    light.Padding2 = int(ReadStorageWord(uint(LIGHT_BUFFER_INDEX), baseWord + 15u));
    return light;
}

uint ReadTiledLightIndex(uint lightListOffset)
{
    uint baseWord = lightListOffset * uint(SIZEOF_GPU_LIGHT_INDEX / 4);
    return ReadStorageWord(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 0u);
}

mat4 ReadShadowMatrix(uint cascadeIndex)
{
    uint baseWord = uint(OFFSET_GPU_SHADOW_DATA_LIGHT_VIEW_PROJECTION0 / 4) + cascadeIndex * 16u;
    return mat4(
        ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 0u),
        ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 4u),
        ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 8u),
        ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), baseWord + 12u));
}

int ReadLocalSpotShadowIndex(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX / 4);
    return int(ReadStorageWord(uint(LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX), baseWord + 0u));
}

int ReadLocalPointShadowIndex(uint lightIndex)
{
    uint baseWord = lightIndex * uint(SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX / 4);
    return int(ReadStorageWord(uint(LOCAL_LIGHT_SHADOW_INDEX_BUFFER_INDEX), baseWord + 1u));
}

GPUEnvironmentData ReadEnvironmentData()
{
    GPUEnvironmentData environment;
    environment.EnvironmentTextureIndex = int(ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 0u));
    environment.IrradianceTextureIndex = int(ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 1u));
    environment.PrefilteredTextureIndex = int(ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 2u));
    environment.BrdfLutTextureIndex = int(ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 3u));
    environment.SkyIntensity = ReadStorageFloat(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 4u);
    environment.DiffuseIntensity = ReadStorageFloat(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 5u);
    environment.SpecularIntensity = ReadStorageFloat(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 6u);
    environment.RotationRadians = ReadStorageFloat(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 7u);
    environment.PrefilteredMipCount = ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 8u);
    environment.Enabled = ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 9u);
    environment.DebugView = ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 10u);
    environment.DebugMipLevel = ReadStorageWord(uint(ENVIRONMENT_DATA_BUFFER_INDEX), 11u);
    return environment;
}

GPUReflectionProbeHeader ReadReflectionProbeHeader()
{
    GPUReflectionProbeHeader header;
    header.ProbeCount = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 0u));
    header.MaxProbesPerPixel = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 1u));
    header.ProbeCubemapArrayTextureIndex = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 2u));
    header.DebugTextureIndex = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 3u));
    header.Intensity = ReadStorageFloat(uint(REFLECTION_PROBE_BUFFER_INDEX), 4u);
    header.GlobalFallbackIntensity = ReadStorageFloat(uint(REFLECTION_PROBE_BUFFER_INDEX), 5u);
    header.ProbeMipCount = ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 6u);
    header.Flags = ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 7u);
    header.DebugView = ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 8u);
    header.DebugProbeIndex = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 9u));
    header.DebugCubemapFace = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 10u));
    header.DebugMipLevel = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), 11u));
    return header;
}

GPUReflectionProbe ReadReflectionProbe(uint probeIndex)
{
    uint baseWord = uint(SIZEOF_GPU_REFLECTION_PROBE_HEADER / 4) + probeIndex * uint(SIZEOF_GPU_REFLECTION_PROBE / 4);
    GPUReflectionProbe probe;
    probe.WorldToProbe = mat4(
        ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 0u),
        ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 4u),
        ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 8u),
        ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 12u));
    probe.PositionAndRadius = ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 16u);
    probe.BoxMin = ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 20u);
    probe.BoxMax = ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 24u);
    probe.BlendParams = ReadStorageVec4(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 28u);
    probe.CubemapArrayIndex = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 32u));
    probe.Shape = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 33u));
    probe.Flags = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 34u));
    probe.Priority = int(ReadStorageWord(uint(REFLECTION_PROBE_BUFFER_INDEX), baseWord + 35u));
    return probe;
}

GPUSpotShadow ReadSpotShadow(uint shadowIndex)
{
    uint baseWord = shadowIndex * uint(SIZEOF_GPU_SPOT_SHADOW / 4);
    GPUSpotShadow shadow;
    shadow.LightViewProjection = mat4(
        ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 0u),
        ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 4u),
        ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 8u),
        ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 12u));
    shadow.AtlasScaleOffset = ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 16u);
    shadow.BiasStrengthTexelSize = ReadStorageVec4(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 20u);
    shadow.LightIndex = int(ReadStorageWord(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 24u));
    shadow.AtlasTile = int(ReadStorageWord(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 25u));
    shadow.PcfRadius = int(ReadStorageWord(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 26u));
    shadow.Enabled = int(ReadStorageWord(uint(SPOT_SHADOW_DATA_BUFFER_INDEX), baseWord + 27u));
    return shadow;
}

mat4 ReadPointShadowFaceMatrix(uint shadowIndex, uint faceIndex)
{
    uint baseWord = shadowIndex * uint(SIZEOF_GPU_POINT_SHADOW / 4) + faceIndex * 16u;
    return mat4(
        ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 0u),
        ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 4u),
        ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 8u),
        ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 12u));
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
    shadow.PositionRange = ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 96u);
    shadow.BiasStrengthTexelSize = ReadStorageVec4(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 100u);
    shadow.LightIndex = int(ReadStorageWord(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 104u));
    shadow.CubemapIndex = int(ReadStorageWord(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 105u));
    shadow.PcfRadius = int(ReadStorageWord(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 106u));
    shadow.Enabled = int(ReadStorageWord(uint(POINT_SHADOW_DATA_BUFFER_INDEX), baseWord + 107u));
    return shadow;
}

vec4 ReadShadowCascadeSplits()
{
    return ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_CASCADE_SPLITS / 4));
}

vec4 ReadShadowSettings()
{
    return ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_SETTINGS / 4));
}

vec4 ReadShadowIndices()
{
    return ReadStorageVec4(uint(DIRECTIONAL_SHADOW_DATA_BUFFER_INDEX), uint(OFFSET_GPU_SHADOW_DATA_INDICES / 4));
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
    WriteStorageWord(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 1u, 0u);
    WriteStorageWord(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 2u, 0u);
    WriteStorageWord(uint(TILED_LIGHT_INDICES_BUFFER_INDEX), baseWord + 3u, 0u);
}

#endif // NJULF_COMMON_GLSL
