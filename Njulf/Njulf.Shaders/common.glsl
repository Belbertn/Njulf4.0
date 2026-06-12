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
const int STATIC_BUFFER_COUNT = 27;

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
const int FIRST_DYNAMIC_TEXTURE_INDEX = 20;

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
};

struct GPUMeshInfo
{
    vec4 BoundingSphere;
    vec4 Padding0;
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
    int Padding0;
    int Padding1;
};

struct GPUMaterialData
{
    vec4 Albedo;
    vec4 Emissive;
    // x = normal scale, y = alpha mode (0 opaque, 1 mask, 2 blend),
    // z = alpha cutoff, w = double-sided flag.
    vec4 NormalScaleBias;
    vec4 MetallicRoughnessAO;
    vec4 TexCoordOffsetScale;
    int AlbedoTextureIndex;
    int NormalTextureIndex;
    int MetallicRoughnessTextureIndex;
    int EmissiveTextureIndex;
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
    uint DebugViewMode;
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

// Descriptor arrays matching BindlessHeap. Heterogeneous storage buffers are
// addressed by descriptor array element and interpreted by pass-specific code.
layout(set = 0, binding = 0) buffer BindlessStorageBuffer
{
    uint Words[];
} BindlessStorageBuffers[];

layout(set = 1, binding = 0) uniform sampler2D BindlessTextures[];
layout(set = 1, binding = 0) uniform sampler2DArray BindlessArrayTextures[];

// Documented sizes (bytes). Tests parse these constants and compare them to C#.
const int SIZEOF_GPU_VERTEX = 64;
const int SIZEOF_GPU_MESH_INFO = 32;
const int SIZEOF_GPU_MESHLET = 48;
const int SIZEOF_GPU_OBJECT_DATA = 144;
const int SIZEOF_GPU_MATERIAL_DATA = 96;
const int SIZEOF_GPU_LIGHT = 64;
const int SIZEOF_GPU_SCENE_DATA = 400;
const int SIZEOF_GPU_MESHLET_DRAW_COMMAND = 16;
const int SIZEOF_GPU_TILED_LIGHT_HEADER = 16;
const int SIZEOF_GPU_LIGHT_INDEX = 16;
const int SIZEOF_GPU_SCREEN_TO_VIEW_PARAMS = 32;
const int SIZEOF_GPU_LIGHT_CULLING_PARAMS = 192;
const int SIZEOF_GPU_DEPTH_PUSH_CONSTANTS = 96;
const int SIZEOF_GPU_FORWARD_PUSH_CONSTANTS = 256;
const int SIZEOF_GPU_LIGHT_CULL_PUSH_CONSTANTS = 192;
const int SIZEOF_GPU_SHADOW_DATA = 304;
const int SIZEOF_GPU_SPOT_SHADOW = 112;
const int SIZEOF_GPU_POINT_SHADOW = 432;
const int SIZEOF_GPU_LOCAL_LIGHT_SHADOW_INDEX = 16;

const uint DIAGNOSTIC_DEPTH_CANDIDATES = 0u;
const uint DIAGNOSTIC_DEPTH_FRUSTUM_CULLED = 1u;
const uint DIAGNOSTIC_DEPTH_EMITTED = 2u;
const uint DIAGNOSTIC_FORWARD_CANDIDATES = 3u;
const uint DIAGNOSTIC_FORWARD_FRUSTUM_CULLED = 4u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_CULLED = 5u;
const uint DIAGNOSTIC_FORWARD_EMITTED = 6u;
const uint DIAGNOSTIC_FORWARD_OCCLUSION_TESTED = 7u;

// Documented byte offsets for layout-critical fields. These are parsed by
// tests because GLSL has no portable compile-time offsetof operator.
const int OFFSET_GPU_VERTEX_POSITION = 0;
const int OFFSET_GPU_VERTEX_NORMAL = 16;
const int OFFSET_GPU_VERTEX_TEX_COORD = 32;
const int OFFSET_GPU_VERTEX_TANGENT = 48;

const int OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX = 0;
const int OFFSET_GPU_OBJECT_DATA_WORLD_MATRIX_INVERSE_TRANSPOSE = 64;
const int OFFSET_GPU_OBJECT_DATA_MESH_INDEX = 128;
const int OFFSET_GPU_OBJECT_DATA_MATERIAL_INDEX = 132;

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
const int OFFSET_GPU_FORWARD_PUSH_DEBUG_VIEW_MODE = 252;

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


const uint MESHLET_MAX_VERTICES = 64u;
const uint MESHLET_MAX_TRIANGLES = 126u;
const uint MESHLET_TASK_GROUP_SIZE = 1u;

uint ReadStorageWord(uint bufferIndex, uint wordOffset)
{
    return BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset];
}

void WriteStorageWord(uint bufferIndex, uint wordOffset, uint value)
{
    BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[wordOffset] = value;
}

void IncrementRendererDiagnostic(uint frameIndex, uint counterIndex)
{
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + frameIndex;
    atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counterIndex], 1u);
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

GPUVertex ReadVertex(uint vertexIndex)
{
    uint baseWord = vertexIndex * uint(SIZEOF_GPU_VERTEX / 4);
    GPUVertex vertex;
    vertex.Position = ReadStorageVec3(uint(VERTEX_BUFFER_INDEX), baseWord + 0u);
    vertex.Padding0 = ReadStorageFloat(uint(VERTEX_BUFFER_INDEX), baseWord + 3u);
    vertex.Normal = ReadStorageVec3(uint(VERTEX_BUFFER_INDEX), baseWord + 4u);
    vertex.Padding1 = ReadStorageFloat(uint(VERTEX_BUFFER_INDEX), baseWord + 7u);
    vertex.TexCoord = ReadStorageVec2(uint(VERTEX_BUFFER_INDEX), baseWord + 8u);
    vertex.TexCoord2 = ReadStorageVec2(uint(VERTEX_BUFFER_INDEX), baseWord + 10u);
    vertex.Tangent = ReadStorageVec4(uint(VERTEX_BUFFER_INDEX), baseWord + 12u);
    return vertex;
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
    objectData.Padding0 = int(ReadStorageWord(bufferIndex, baseWord + 34u));
    objectData.Padding1 = int(ReadStorageWord(bufferIndex, baseWord + 35u));
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
    material.TexCoordOffsetScale = ReadStorageVec4(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 16u);
    material.AlbedoTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 20u));
    material.NormalTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 21u));
    material.MetallicRoughnessTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 22u));
    material.EmissiveTextureIndex = int(ReadStorageWord(uint(MATERIAL_DATA_BUFFER_INDEX), baseWord + 23u));
    return material;
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
