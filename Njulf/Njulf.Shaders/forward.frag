#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#include "common.glsl"

#ifndef FORWARD_SIMPLE_VERTEX_INPUT
#define FORWARD_SIMPLE_VERTEX_INPUT 0
#endif

#ifndef NJULF_SSGI_TRACE_OUTPUT
#define NJULF_SSGI_TRACE_OUTPUT 0
#endif

#ifndef FORWARD_SSGI_TRACE_SOURCE_OUTPUT
#define FORWARD_SSGI_TRACE_SOURCE_OUTPUT NJULF_SSGI_TRACE_OUTPUT
#endif

#if FORWARD_SIMPLE_VERTEX_INPUT
layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) flat in uint fragMeshletIndex;
const vec4 fragWorldTangent = vec4(1.0, 0.0, 0.0, 1.0);
const vec2 fragTexCoord2 = vec2(0.0);
const vec4 fragVertexColor = vec4(1.0);
#else
layout(location = 0) in vec3 fragNormal;
layout(location = 1) in vec2 fragTexCoord;
layout(location = 2) flat in uint fragMaterialIndex;
layout(location = 3) flat in uint fragObjectIndex;
layout(location = 4) in vec3 fragWorldPosition;
layout(location = 5) in vec4 fragWorldTangent;
layout(location = 6) flat in uint fragMeshletIndex;
layout(location = 7) in vec2 fragTexCoord2;
layout(location = 8) in vec4 fragVertexColor;
#endif

#if FORWARD_WEIGHTED_OIT
layout(location = 0) out vec4 outOitAccumulation;
layout(location = 1) out vec4 outOitRevealage;
#else
layout(location = 0) out vec4 outColor;
#if FORWARD_SSGI_TRACE_SOURCE_OUTPUT
layout(location = 1) out vec4 outSsgiTraceSource;
#endif
#endif

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

const float PI = 3.14159265359;
const uint DEBUG_VIEW_NONE = 0u;
const uint DEBUG_VIEW_MESHLETS = 1u;
const uint DEBUG_VIEW_SHADOW_CASCADE_OVERLAY = 2u;
const uint DEBUG_VIEW_SHADOW_MAP_PREVIEW = 3u;
const uint DEBUG_VIEW_SHADOW_RECEIVER_FACTOR = 4u;
const uint DEBUG_VIEW_SPOT_ATLAS_PREVIEW = 5u;
const uint DEBUG_VIEW_POINT_CUBEMAP_FACE_PREVIEW = 6u;
const uint DEBUG_VIEW_LOCAL_SHADOW_SELECTION = 7u;
const uint ENVIRONMENT_DEBUG_SKYBOX_ONLY = 1u;
const uint ENVIRONMENT_DEBUG_IRRADIANCE_CUBEMAP = 2u;
const uint ENVIRONMENT_DEBUG_PREFILTERED_ENVIRONMENT_MIP = 3u;
const uint ENVIRONMENT_DEBUG_BRDF_LUT = 4u;
const uint ENVIRONMENT_DEBUG_DIFFUSE_IBL_ONLY = 5u;
const uint ENVIRONMENT_DEBUG_SPECULAR_IBL_ONLY = 6u;
const uint ENVIRONMENT_DEBUG_AMBIENT_OCCLUSION = 7u;
const uint AO_DEBUG_RAW = 1u;
const uint AO_DEBUG_BLURRED = 2u;
const uint AO_DEBUG_FINAL = 3u;
const uint AO_DEBUG_RECONSTRUCTED_NORMAL = 4u;
const uint AO_DEBUG_LINEAR_DEPTH = 5u;
const uint AO_FORWARD_SAMPLING_DISABLED = 0u;
const uint AO_FORWARD_SAMPLING_DIRECT = 1u;
const uint AO_FORWARD_SAMPLING_DEPTH_AWARE_UPSAMPLE = 2u;
const uint TRANSPARENCY_DEBUG_ALPHA_MODE = 1u;
const uint TRANSPARENCY_DEBUG_ALPHA_VALUE = 2u;
const uint TRANSPARENCY_DEBUG_ALPHA_CUTOFF = 3u;
const uint TRANSPARENCY_DEBUG_SORT_ORDER = 4u;
const uint MATERIAL_DEBUG_FEATURE_FLAGS = 32u;
const uint MATERIAL_DEBUG_BASE_COLOR = 33u;
const uint MATERIAL_DEBUG_METALLIC = 34u;
const uint MATERIAL_DEBUG_ROUGHNESS = 35u;
const uint MATERIAL_DEBUG_NORMAL_STRENGTH = 36u;
const uint MATERIAL_DEBUG_WORLD_NORMAL = 37u;
const uint MATERIAL_DEBUG_EMISSIVE_INTENSITY = 38u;
const uint MATERIAL_DEBUG_CLEARCOAT_FACTOR = 39u;
const uint MATERIAL_DEBUG_CLEARCOAT_ROUGHNESS = 40u;
const uint MATERIAL_DEBUG_SHEEN_COLOR = 41u;
const uint MATERIAL_DEBUG_SHEEN_ROUGHNESS = 42u;
const uint MATERIAL_DEBUG_ANISOTROPY_STRENGTH = 43u;
const uint MATERIAL_DEBUG_ANISOTROPY_DIRECTION = 44u;
const uint MATERIAL_DEBUG_TRANSMISSION = 45u;
const uint MATERIAL_DEBUG_IOR = 46u;
const uint MATERIAL_DEBUG_VOLUME_THICKNESS = 47u;
const uint MATERIAL_DEBUG_ATTENUATION_COLOR = 48u;
const uint MATERIAL_DEBUG_SUBSURFACE_STRENGTH = 49u;
const uint MATERIAL_DEBUG_SPECULAR_FACTOR = 50u;
const uint MATERIAL_DEBUG_SPECULAR_COLOR = 51u;
const uint MATERIAL_DEBUG_IRIDESCENCE_FACTOR = 52u;
const uint MATERIAL_DEBUG_IRIDESCENCE_THICKNESS = 53u;
const uint MATERIAL_DEBUG_DISPERSION = 54u;
const uint GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT = 80u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_RAW = 81u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED = 82u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_HISTORY = 83u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_RAY_HIT_MASK = 84u;
const uint GLOBAL_ILLUMINATION_DEBUG_SSGI_HISTORY_REJECTION = 85u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE = 86u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY = 87u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX = 88u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE = 89u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION = 90u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP = 91u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE = 92u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION = 93u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT = 94u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS = 95u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET = 96u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME = 97u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP = 98u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT = 99u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK = 100u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE = 101u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK = 102u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT = 103u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT = 104u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED = 105u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE = 106u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS = 107u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE = 108u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE = 109u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE = 110u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE = 111u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN = 112u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION = 113u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION = 114u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION = 115u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT = 116u;
const uint ANIMATION_DEBUG_SKINNED_OBJECTS = 64u;
const uint ANIMATION_DEBUG_JOINT_WEIGHTS = 65u;
const uint ANIMATION_DEBUG_JOINT_INDEX = 66u;
const uint ANIMATION_DEBUG_SKINNING_ERROR = 67u;
const uint ANIMATION_DEBUG_SKELETON = 68u;
const uint ANIMATION_DEBUG_ANIMATED_BOUNDS = 69u;
const uint ANIMATION_DEBUG_CLIP_TIME = 70u;
const uint REFLECTION_DEBUG_PROBE_INFLUENCE = 1u;
const uint REFLECTION_DEBUG_PROBE_INDEX = 2u;
const uint REFLECTION_DEBUG_PROBE_BLEND_WEIGHTS = 3u;
const uint REFLECTION_DEBUG_PROBE_CUBEMAP_FACE = 4u;
const uint REFLECTION_DEBUG_PROBE_PREFILTER_MIP = 5u;
const uint REFLECTION_DEBUG_BOX_PROJECTION_DIRECTION = 6u;
const uint REFLECTION_DEBUG_LOCAL_REFLECTION_ONLY = 9u;
const uint REFLECTION_DEBUG_GLOBAL_FALLBACK_ONLY = 10u;
const uint REFLECTION_ENABLED_FLAG = 1u << 0u;
const uint REFLECTION_BOX_PROJECTION_ENABLED_FLAG = 1u << 1u;
const uint REFLECTION_PROBE_BLENDING_ENABLED_FLAG = 1u << 2u;
const uint DDGI_ENABLED_FLAG = 1u << 0u;
const uint DDGI_EXHAUSTIVE_GATHER_FALLBACK_ENABLED_FLAG = 1u << 3u;
const uint DDGI_RAW_ATLAS_RADIANCE_CONVENTION_ENABLED_FLAG = 1u << 4u;
const uint DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG = 1u << 5u;
const uint DDGI_WARMUP_STATE_DISABLED = 0u;
const uint DDGI_WARMUP_STATE_COLD_START = 1u;
const uint DDGI_WARMUP_STATE_LOCAL_VOLUME = 2u;
const uint DDGI_WARMUP_STATE_NEAR_CASCADE = 3u;
const uint DDGI_WARMUP_STATE_STEADY = 4u;
const uint DDGI_WARMUP_STATE_RECOVERY = 5u;
const uint DDGI_VOLUME_KIND_AUTHORED = 0u;
const uint DDGI_VOLUME_KIND_CAMERA_CLIPMAP = 1u;
const uint DDGI_GATHER_INVALID_VOLUME_INDEX = 0xffffffffu;
const uint DDGI_GATHER_HEADER_ENABLED_FLAG = 1u << 0u;
const uint DDGI_GATHER_TILE_LOCAL_VOLUME_VALID_FLAG = 1u << 0u;
const uint DDGI_GATHER_TILE_PRIMARY_CLIPMAP_VALID_FLAG = 1u << 1u;
const uint DDGI_GATHER_TILE_SECONDARY_CLIPMAP_VALID_FLAG = 1u << 2u;
const uint DDGI_GATHER_TILE_FALLBACK_FLAG = 1u << 3u;
const int REFLECTION_PROBE_BOX_PROJECTION_FLAG = 1;
const int REFLECTION_PROBE_SHAPE_SPHERE = 1;
const float DEPTH_NORMAL_RELATIVE_EPSILON = 0.000001;

#ifndef FORWARD_SIMPLE_OPAQUE
#define FORWARD_SIMPLE_OPAQUE 0
#endif

#ifndef FORWARD_WEIGHTED_OIT
#define FORWARD_WEIGHTED_OIT 0
#endif

uint ForwardDebugViewMode()
{
    return pc.Push.DebugAndAoFlags & 0xffu;
}

uint ForwardAmbientOcclusionEnabled()
{
    return (pc.Push.DebugAndAoFlags >> 8u) & 1u;
}

uint ForwardAmbientOcclusionDebugView()
{
    return (pc.Push.DebugAndAoFlags >> 16u) & 0xffu;
}

uint ForwardTransparentReceiveShadows()
{
    return (pc.Push.DebugAndAoFlags >> 24u) & 1u;
}

uint ForwardTransparencyDebugView()
{
    return (pc.Push.DebugAndAoFlags >> 25u) & 0x07u;
}

uint ForwardAmbientOcclusionSamplingMode()
{
    return (pc.Push.DebugAndAoFlags >> 29u) & 0x03u;
}

uint ForwardGlobalIlluminationEnabled()
{
    return (pc.Push.DebugAndAoFlags >> 31u) & 1u;
}

bool DdgiForwardEstimateCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 1u) != 0u;
}

bool DdgiClipmapCoverageCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 2u) != 0u;
}

bool DdgiSparseDiagnosticPixel()
{
    uvec2 pixel = uvec2(max(gl_FragCoord.xy, vec2(0.0)));
    return (pixel.x & 15u) == 0u && (pixel.y & 15u) == 0u;
}

bool DdgiForwardEstimateDiagnosticPixel()
{
    return DdgiForwardEstimateCountersEnabled() && DdgiSparseDiagnosticPixel();
}

bool DdgiClipmapCoverageDiagnosticPixel()
{
    return DdgiClipmapCoverageCountersEnabled() && DdgiSparseDiagnosticPixel();
}

bool DdgiFastGatherCountersEnabled()
{
    return DdgiForwardEstimateCountersEnabled() || DdgiClipmapCoverageCountersEnabled();
}

bool DdgiFastGatherDiagnosticPixel()
{
    return DdgiFastGatherCountersEnabled() && DdgiSparseDiagnosticPixel();
}

const uint DDGI_FORWARD_ESTIMATE_COUNTER_BASE = 9u;
const float DDGI_FORWARD_ESTIMATE_WEIGHT_SCALE = 1024.0;
const float DDGI_FORWARD_ESTIMATE_LUMINANCE_SCALE = 4096.0;
const uint DDGI_FORWARD_ESTIMATE_SPATIAL_COVERAGE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 0u;
const uint DDGI_FORWARD_ESTIMATE_SUPPORT_COVERAGE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 1u;
const uint DDGI_FORWARD_ESTIMATE_DATA_CONFIDENCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 2u;
const uint DDGI_FORWARD_ESTIMATE_VISIBILITY_CONFIDENCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 3u;
const uint DDGI_FORWARD_ESTIMATE_LEAK_ATTENUATION_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 4u;
const uint DDGI_FORWARD_ESTIMATE_EFFECTIVE_WEIGHT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 5u;
const uint DDGI_FORWARD_ESTIMATE_RAW_LUMINANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 6u;
const uint DDGI_FORWARD_ESTIMATE_FINAL_LUMINANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 7u;
const uint DDGI_FORWARD_ESTIMATE_OWNERSHIP_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 8u;
const uint DDGI_FORWARD_ESTIMATE_SAMPLE_COUNT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 9u;
const uint DDGI_FORWARD_ESTIMATE_ZERO_SUPPORT_SPATIAL_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 10u;
const uint DDGI_FORWARD_ESTIMATE_ZERO_EFFECTIVE_SPATIAL_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 11u;
const uint DDGI_VISIBILITY_MOMENT_MEAN_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 12u;
const uint DDGI_VISIBILITY_MOMENT_VARIANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 13u;
const uint DDGI_VISIBILITY_PROBE_DISTANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 14u;
const uint DDGI_VISIBILITY_MOMENT_SAMPLE_COUNT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 15u;
const uint DDGI_VISIBILITY_LARGE_DISTANCE_MARGIN_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 16u;
const uint DDGI_VISIBILITY_ZERO_TRANSPORT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 17u;
const uint DDGI_VISIBILITY_ZERO_TRANSPORT_WITH_IRRADIANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 18u;
const uint DDGI_SUPPORT_REJECTED_INACTIVE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 19u;
const uint DDGI_SUPPORT_REJECTED_ZERO_IRRADIANCE_ALPHA_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 20u;
const uint DDGI_SUPPORT_REJECTED_LOW_QUALITY_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 21u;
const uint DDGI_PROBE_IRRADIANCE_ALPHA_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 22u;
const uint DDGI_PROBE_QUALITY_X_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 23u;
const uint DDGI_PROBE_QUALITY_Y_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 24u;
const uint DDGI_PROBE_QUALITY_Z_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 25u;
const uint DDGI_PROBE_QUALITY_SAMPLE_COUNT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 26u;
const uint DDGI_CLIPMAP_INFO_PRIMARY_ATTEMPT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 27u;
const uint DDGI_CLIPMAP_INFO_PRIMARY_OK_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 28u;
const uint DDGI_CLIPMAP_INFO_PRIMARY_FAILED_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 29u;
const uint DDGI_CLIPMAP_INFO_PRIMARY_EDGE_FADE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 30u;
const uint DDGI_CLIPMAP_INFO_PRIMARY_BLEND_WEIGHT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 31u;
const uint DDGI_FAST_GATHER_ATTEMPT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 32u;
const uint DDGI_FAST_GATHER_ACCEPTED_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 33u;
const uint DDGI_FAST_GATHER_REJECTED_ZERO_SPATIAL_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 34u;
const uint DDGI_FAST_GATHER_REJECTED_ZERO_SUPPORT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 35u;
const uint DDGI_FAST_GATHER_REJECTED_ZERO_DATA_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 36u;
const uint DDGI_FAST_GATHER_REJECTED_ZERO_OWNERSHIP_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 37u;
const uint DDGI_SHADER_GATHER_FALLBACK_ATTEMPT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 38u;
const uint DDGI_SHADER_GATHER_FALLBACK_ACCEPTED_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 39u;
const uint DDGI_SHADER_GATHER_FALLBACK_EMPTY_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 40u;

uint PackDdgiForwardEstimateWeight(float value);

uint ForwardSsgiEnabled()
{
    return ForwardGlobalIlluminationEnabled() & ((pc.Push.DebugAndAoFlags >> 28u) & 1u);
}

uint HashUint(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

vec3 MeshletDebugColor(uint meshletIndex)
{
    uint hash = HashUint(meshletIndex + 1u);
    return vec3(
        float(hash & 0xffu),
        float((hash >> 8u) & 0xffu),
        float((hash >> 16u) & 0xffu)) / 255.0;
}

bool IsMaterialDebugView(uint debugViewMode)
{
    return debugViewMode >= MATERIAL_DEBUG_FEATURE_FLAGS &&
           debugViewMode <= MATERIAL_DEBUG_DISPERSION;
}

bool IsAnimationDebugView(uint debugViewMode)
{
    return debugViewMode >= ANIMATION_DEBUG_SKINNED_OBJECTS &&
           debugViewMode <= ANIMATION_DEBUG_CLIP_TIME;
}

float MaxComponent(vec3 value)
{
    return max(max(value.x, value.y), value.z);
}

vec3 MaterialFeatureFlagsDebugColor(uint flags)
{
    if (flags == 0u)
        return vec3(0.02);

    vec3 color = vec3(0.0);
    color.r += (flags & MATERIAL_FEATURE_CLEARCOAT) != 0u ? 0.50 : 0.0;
    color.r += (flags & MATERIAL_FEATURE_SUBSURFACE) != 0u ? 0.35 : 0.0;
    color.g += (flags & MATERIAL_FEATURE_SHEEN) != 0u ? 0.45 : 0.0;
    color.g += (flags & MATERIAL_FEATURE_VOLUME_APPROXIMATION) != 0u ? 0.35 : 0.0;
    color.b += (flags & MATERIAL_FEATURE_ANISOTROPY) != 0u ? 0.40 : 0.0;
    color.b += (flags & MATERIAL_FEATURE_TRANSMISSION) != 0u ? 0.40 : 0.0;
    color += (flags & MATERIAL_FEATURE_EMISSIVE_STRENGTH) != 0u ? vec3(0.20, 0.12, 0.0) : vec3(0.0);
    color += (flags & MATERIAL_FEATURE_SPECULAR) != 0u ? vec3(0.15, 0.15, 0.15) : vec3(0.0);
    color += (flags & MATERIAL_FEATURE_IRIDESCENCE) != 0u ? vec3(0.15, 0.0, 0.25) : vec3(0.0);
    color += (flags & MATERIAL_FEATURE_DISPERSION) != 0u ? vec3(0.0, 0.12, 0.20) : vec3(0.0);
    color += (flags & MATERIAL_FEATURE_FOLIAGE) != 0u ? vec3(0.10, 0.25, 0.04) : vec3(0.0);
    return clamp(color, vec3(0.0), vec3(1.0));
}

vec4 SampleMaterialTexture(int textureIndex, vec2 uv)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    return texture(BindlessTextures[nonuniformEXT(safeIndex)], uv);
}

vec2 SelectUv(float texCoordSet)
{
    return int(round(texCoordSet)) == 1 ? fragTexCoord2 : fragTexCoord;
}

vec2 ApplyTextureTransform(vec2 uv, vec4 offsetScale, float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

bool IsIdentityTextureTransform(vec4 offsetScale, float rotationRadians)
{
    return abs(offsetScale.x) <= 0.0001 &&
           abs(offsetScale.y) <= 0.0001 &&
           abs(offsetScale.z - 1.0) <= 0.0001 &&
           abs(offsetScale.w - 1.0) <= 0.0001 &&
           abs(rotationRadians) <= 0.0001;
}

vec2 MaterialUv(float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = SelectUv(texCoordSet);
    return IsIdentityTextureTransform(offsetScale, rotationRadians)
        ? uv
        : ApplyTextureTransform(uv, offsetScale, rotationRadians);
}

vec2 ExtensionUv(vec4 offsetScale, float rotationRadians, float texCoordSet)
{
    return MaterialUv(texCoordSet, offsetScale, rotationRadians);
}

vec3 ReconstructViewPositionFromDepth(vec2 uv, float depth)
{
    vec4 clip = vec4(uv * 2.0 - vec2(1.0), depth, 1.0);
    vec4 view = MulRowMajor(clip, pc.Push.InverseProjectionMatrix);
    return view.xyz / max(abs(view.w), 0.00001);
}

float FetchDepthAtPixel(ivec2 pixel, ivec2 depthSize)
{
    ivec2 safePixel = clamp(pixel, ivec2(0), depthSize - ivec2(1));
    return texelFetch(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], safePixel, 0).r;
}

float FetchDepthAtUv(vec2 uv, ivec2 depthSize)
{
    ivec2 pixel = ivec2(clamp(uv * vec2(depthSize), vec2(0.0), vec2(depthSize - ivec2(1))));
    return FetchDepthAtPixel(pixel, depthSize);
}

float ReconstructViewDepth(vec2 uv, float depth)
{
    return abs(ReconstructViewPositionFromDepth(uv, depth).z);
}

vec3 ReconstructNormalFromDepth(vec2 uv)
{
    vec2 invScreen = 1.0 / max(pc.Push.ScreenDimensions, vec2(1.0));
    ivec2 depthSize = textureSize(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0);
    float centerDepth = FetchDepthAtUv(uv, depthSize);
    vec3 center = ReconstructViewPositionFromDepth(uv, centerDepth);
    vec2 uvRight = min(uv + vec2(invScreen.x, 0.0), vec2(1.0));
    vec2 uvUp = min(uv + vec2(0.0, invScreen.y), vec2(1.0));
    vec3 right = ReconstructViewPositionFromDepth(uvRight, FetchDepthAtUv(uvRight, depthSize));
    vec3 up = ReconstructViewPositionFromDepth(uvUp, FetchDepthAtUv(uvUp, depthSize));
    vec3 dx = right - center;
    vec3 dy = up - center;
    vec3 normalVector = cross(dy, dx);
    float normalLengthSq = dot(normalVector, normalVector);
    float derivativeAreaSq = max(dot(dx, dx) * dot(dy, dy), 1e-30);
    if (normalLengthSq <= derivativeAreaSq * DEPTH_NORMAL_RELATIVE_EPSILON)
        return vec3(0.0, 0.0, 1.0);

    vec3 normal = normalVector * inversesqrt(normalLengthSq);
    return dot(normal, -center) < 0.0 ? -normal : normal;
}

float SampleScreenSpaceAoDirect()
{
    vec2 uv = clamp(gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0)), vec2(0.0), vec2(1.0));
    return clamp(texture(BindlessTextures[nonuniformEXT(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX)], uv).r, 0.0, 1.0);
}

float SampleScreenSpaceAoDepthAware()
{
    ivec2 depthSize = textureSize(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0);
    ivec2 aoSize = textureSize(BindlessTextures[nonuniformEXT(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX)], 0);
    if (depthSize.x <= 0 || depthSize.y <= 0 || aoSize.x <= 0 || aoSize.y <= 0)
        return 1.0;

    ivec2 depthPixel = ivec2(clamp(gl_FragCoord.xy, vec2(0.0), vec2(depthSize - ivec2(1))));
    vec2 uv = (vec2(depthPixel) + vec2(0.5)) / vec2(depthSize);
    float centerDepth = FetchDepthAtPixel(depthPixel, depthSize);
    if (centerDepth <= 0.000001)
        return 1.0;

    float centerViewDepth = ReconstructViewDepth(uv, centerDepth);
    vec2 aoTexelPosition = uv * vec2(aoSize) - vec2(0.5);
    ivec2 baseAoPixel = ivec2(floor(aoTexelPosition));
    vec2 aoFraction = fract(aoTexelPosition);

    float weightedAo = 0.0;
    float totalWeight = 0.0;
    float depthSigma = max(0.25, centerViewDepth * 0.02);

    for (int y = 0; y <= 1; y++)
    {
        for (int x = 0; x <= 1; x++)
        {
            ivec2 aoPixel = clamp(baseAoPixel + ivec2(x, y), ivec2(0), aoSize - ivec2(1));
            vec2 aoUv = (vec2(aoPixel) + vec2(0.5)) / vec2(aoSize);
            float sampleDepth = FetchDepthAtUv(aoUv, depthSize);
            if (sampleDepth <= 0.000001)
                continue;

            float sampleViewDepth = ReconstructViewDepth(aoUv, sampleDepth);
            float depthWeight = exp(-abs(sampleViewDepth - centerViewDepth) / depthSigma);
            float spatialWeight = (x == 0 ? 1.0 - aoFraction.x : aoFraction.x) *
                                  (y == 0 ? 1.0 - aoFraction.y : aoFraction.y);
            float weight = spatialWeight * depthWeight;
            weightedAo += texelFetch(BindlessTextures[nonuniformEXT(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX)], aoPixel, 0).r * weight;
            totalWeight += weight;
        }
    }

    if (totalWeight <= 0.000001)
        return clamp(texture(BindlessTextures[nonuniformEXT(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX)], uv).r, 0.0, 1.0);

    return clamp(weightedAo / totalWeight, 0.0, 1.0);
}

float SampleScreenSpaceAo()
{
    if (ForwardAmbientOcclusionEnabled() == 0u)
        return 1.0;

    uint samplingMode = ForwardAmbientOcclusionSamplingMode();
    if (samplingMode == AO_FORWARD_SAMPLING_DIRECT)
        return SampleScreenSpaceAoDirect();

    if (samplingMode == AO_FORWARD_SAMPLING_DEPTH_AWARE_UPSAMPLE)
        return SampleScreenSpaceAoDepthAware();

    return 1.0;
}

struct DdgiSampleResult
{
    vec3 irradiance;
    float weight;
    float coverage;
    float spatialCoverage;
    float supportCoverage;
    float ownershipConsumed;
    float visibility;
    float leakClamp;
    float activeProbe;
    uint probeIndex;
    vec3 relocation;
    vec3 logicalProbePosition;
    vec3 relocatedProbePosition;
    float minProbeSpacing;
    float classificationInvalidScore;
    float visibilityMomentMean;
    float visibilityMomentVariance;
    float visibilityProbeDistance;
    float visibilityMaxRayDistance;
    float cascadeIndex;
    float cascadeBlendWeight;
    float updateReason;
    float rayBudget;
    float irradianceAtlasConfidence;
    float rayHitConfidence;
    float stateIrradianceConfidence;
    float visibilityConfidence;
    float qualityConfidence;
    float strongestSupportWeight;
    float sampleTotalWeight;
    float sampleExpectedWeight;
};

struct DdgiVolumeSampleInfo
{
    uint volumeIndex;
    uint firstProbe;
    uint kind;
    uint cascadeIndex;
    uvec3 probeCounts;
    ivec3 gridMinCell;
    ivec3 ringOffset;
    vec3 origin;
    vec3 spacing;
    ivec3 cellBase;
    vec3 cellFraction;
    float edgeFade;
    float normalBias;
    float viewBias;
    float maxRayDistance;
    float raysPerProbe;
    float volumeIntensity;
};

struct DdgiGatherTileInfo
{
    uint localVolumeIndex;
    uint primaryClipmapVolumeIndex;
    uint secondaryClipmapVolumeIndex;
    uint flags;
    vec4 blendWeights;
};

vec4 ReadPackedDdgiHalf4(uint bufferIndex, uint wordOffset)
{
    vec2 xy = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 0u));
    vec2 zw = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 1u));
    return vec4(xy, zw);
}

vec2 ReadPackedDdgiHalf2(uint bufferIndex, uint wordOffset)
{
    return unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset));
}

vec2 DdgiSignNotZero(vec2 value)
{
    return vec2(
        value.x >= 0.0 ? 1.0 : -1.0,
        value.y >= 0.0 ? 1.0 : -1.0);
}

vec2 DdgiOctahedralEncode(vec3 direction)
{
    vec3 n = direction / max(abs(direction.x) + abs(direction.y) + abs(direction.z), 0.0001);
    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * DdgiSignNotZero(encoded);
    return encoded * 0.5 + 0.5;
}

uvec2 RemapDdgiOctahedralTexelCoord(ivec2 coord, uint texelsPerProbe)
{
    int maxCoord = int(max(texelsPerProbe, 1u)) - 1;
    ivec2 remapped = coord;

    if (remapped.x < 0)
    {
        remapped.x = 0;
        remapped.y = maxCoord - remapped.y;
    }
    else if (remapped.x > maxCoord)
    {
        remapped.x = maxCoord;
        remapped.y = maxCoord - remapped.y;
    }

    if (remapped.y < 0)
    {
        remapped.y = 0;
        remapped.x = maxCoord - remapped.x;
    }
    else if (remapped.y > maxCoord)
    {
        remapped.y = maxCoord;
        remapped.x = maxCoord - remapped.x;
    }

    return uvec2(clamp(remapped, ivec2(0), ivec2(maxCoord)));
}

void DdgiBilinearOctahedralTexels(
    vec3 direction,
    uint texelsPerProbe,
    out uvec2 c00,
    out uvec2 c10,
    out uvec2 c01,
    out uvec2 c11,
    out vec2 fraction)
{
    vec2 uv = clamp(DdgiOctahedralEncode(direction), vec2(0.0), vec2(1.0));
    vec2 sampleCoord = uv * float(texelsPerProbe) - vec2(0.5);
    ivec2 baseCoord = ivec2(floor(sampleCoord));
    fraction = fract(sampleCoord);

    c00 = RemapDdgiOctahedralTexelCoord(baseCoord, texelsPerProbe);
    c10 = RemapDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 0), texelsPerProbe);
    c01 = RemapDdgiOctahedralTexelCoord(baseCoord + ivec2(0, 1), texelsPerProbe);
    c11 = RemapDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 1), texelsPerProbe);
}

bool ReadDdgiVolumeSampleInfo(
    uint volumeIndex,
    vec3 worldPosition,
    out DdgiVolumeSampleInfo info)
{
    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 biasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 rayAndUpdateParams = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);
    vec4 blendAndFlags = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_BLEND_AND_FLAGS) / 4u);

    info.volumeIndex = volumeIndex;
    info.firstProbe = uint(originAndFirst.w);
    info.kind = uint(round(gridMinAndKind.w));
    info.cascadeIndex = uint(max(round(ringOffsetAndCascade.w), 0.0));
    info.probeCounts = uvec3(
        max(uint(sizeAndCountX.w), 2u),
        max(uint(spacingAndCountY.w), 2u),
        max(uint(biasAndCountZ.w), 2u));
    info.gridMinCell = ivec3(round(gridMinAndKind.xyz));
    info.ringOffset = ivec3(round(ringOffsetAndCascade.xyz));
    info.origin = originAndFirst.xyz;
    info.spacing = max(spacingAndCountY.xyz, vec3(0.0001));
    info.normalBias = max(biasAndCountZ.x, 0.0);
    info.viewBias = max(biasAndCountZ.y, 0.0);
    info.maxRayDistance = max(biasAndCountZ.z, 0.0001);
    info.raysPerProbe = max(rayAndUpdateParams.x, 0.0);
    info.volumeIntensity = max(rayAndUpdateParams.z, 0.0);

    float volumeEdgeFade;
    if (info.kind == DDGI_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        vec3 logicalPosition = worldPosition / info.spacing;
        vec3 minLogical = vec3(info.gridMinCell);
        vec3 maxLogical = minLogical + vec3(info.probeCounts - uvec3(1u));
        if (any(lessThan(logicalPosition, minLogical - vec3(0.5))) ||
            any(greaterThan(logicalPosition, maxLogical + vec3(0.5))))
            return false;

        vec3 logicalGridPosition = clamp(logicalPosition, minLogical, maxLogical);
        vec3 logicalBase = floor(clamp(logicalGridPosition, minLogical, maxLogical - vec3(1.0)));
        info.cellBase = ivec3(logicalBase);
        info.cellFraction = clamp(logicalGridPosition - logicalBase, vec3(0.0), vec3(1.0));

        vec3 logicalEdgeDistance = min(logicalGridPosition - minLogical, maxLogical - logicalGridPosition);
        float edgeBlendCells = max(blendAndFlags.x * min(min(float(info.probeCounts.x), float(info.probeCounts.y)), float(info.probeCounts.z)), 1.0);
        float edgeBlendDistance = max(blendAndFlags.y / max(min(min(info.spacing.x, info.spacing.y), info.spacing.z), 0.0001), edgeBlendCells);
        vec3 edgeFade3 = smoothstep(vec3(0.0), vec3(edgeBlendDistance), logicalEdgeDistance);
        volumeEdgeFade = min(edgeFade3.x, min(edgeFade3.y, edgeFade3.z));
    }
    else
    {
        vec3 latticeMax = info.origin + info.spacing * vec3(info.probeCounts - uvec3(1u));
        vec3 influenceMin = info.origin - info.spacing * 0.5;
        vec3 influenceMax = latticeMax + info.spacing * 0.5;
        if (any(lessThan(worldPosition, influenceMin)) || any(greaterThan(worldPosition, influenceMax)))
            return false;

        vec3 influenceEdgeDistance = min(worldPosition - influenceMin, influenceMax - worldPosition);
        vec3 edgeFade3 = smoothstep(vec3(0.0), info.spacing * 0.25, influenceEdgeDistance);
        volumeEdgeFade = min(edgeFade3.x, min(edgeFade3.y, edgeFade3.z));
        vec3 gridPosition = clamp((worldPosition - info.origin) / info.spacing, vec3(0.0), vec3(info.probeCounts - uvec3(1u)));
        vec3 localBase = floor(clamp(gridPosition, vec3(0.0), vec3(info.probeCounts - uvec3(2u))));
        info.cellBase = ivec3(localBase);
        info.cellFraction = clamp(gridPosition - localBase, vec3(0.0), vec3(1.0));
    }

    info.edgeFade = clamp(volumeEdgeFade, 0.0, 1.0);
    return true;
}

uint DdgiProbeIndex(DdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        return DdgiCalculatePhysicalProbeIndex(
            probeCoord,
            info.gridMinCell,
            info.ringOffset,
            info.probeCounts,
            info.firstProbe);
    }

    uvec3 localCoord = uvec3(max(probeCoord, ivec3(0)));
    localCoord = min(localCoord, info.probeCounts - uvec3(1u));
    return info.firstProbe + localCoord.x + localCoord.y * info.probeCounts.x + localCoord.z * info.probeCounts.x * info.probeCounts.y;
}

vec3 DdgiProbeWorldPosition(DdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_VOLUME_KIND_CAMERA_CLIPMAP)
        return vec3(probeCoord) * info.spacing;

    return info.origin + info.spacing * vec3(probeCoord);
}

DdgiSampleResult EmptyDdgiSampleResult()
{
    DdgiSampleResult result;
    result.irradiance = vec3(0.0);
    result.weight = 0.0;
    result.coverage = 0.0;
    result.spatialCoverage = 0.0;
    result.supportCoverage = 0.0;
    result.ownershipConsumed = 0.0;
    result.visibility = 0.0;
    result.leakClamp = 0.0;
    result.activeProbe = 0.0;
    result.probeIndex = 0u;
    result.relocation = vec3(0.0);
    result.logicalProbePosition = vec3(0.0);
    result.relocatedProbePosition = vec3(0.0);
    result.minProbeSpacing = 0.0;
    result.classificationInvalidScore = 0.0;
    result.visibilityMomentMean = 0.0;
    result.visibilityMomentVariance = 0.0;
    result.visibilityProbeDistance = 0.0;
    result.visibilityMaxRayDistance = 1.0;
    result.cascadeIndex = 0.0;
    result.cascadeBlendWeight = 0.0;
    result.updateReason = 0.0;
    result.rayBudget = 0.0;
    result.irradianceAtlasConfidence = 0.0;
    result.rayHitConfidence = 0.0;
    result.stateIrradianceConfidence = 0.0;
    result.visibilityConfidence = 0.0;
    result.qualityConfidence = 0.0;
    result.strongestSupportWeight = 0.0;
    result.sampleTotalWeight = 0.0;
    result.sampleExpectedWeight = 0.0;
    return result;
}

bool DdgiHeaderEnabled(out uint volumeCount)
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    volumeCount = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 0u);
    return (flags & DDGI_ENABLED_FLAG) != 0u && volumeCount > 0u;
}

bool DdgiExhaustiveGatherFallbackEnabled()
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    return (flags & DDGI_EXHAUSTIVE_GATHER_FALLBACK_ENABLED_FLAG) != 0u;
}

bool DdgiSampleHasUsableGatherData(DdgiSampleResult ddgiSample)
{
    return !any(isnan(ddgiSample.irradiance)) &&
        !any(isinf(ddgiSample.irradiance)) &&
        ddgiSample.spatialCoverage > 0.000001 &&
        ddgiSample.supportCoverage > 0.000001 &&
        ddgiSample.weight > 0.000001 &&
        ddgiSample.ownershipConsumed > 0.000001;
}

void AddDdgiFastGatherAttemptDiagnostic()
{
    if (!DdgiFastGatherDiagnosticPixel())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_ATTEMPT_COUNTER, 1u);
}

void AddDdgiFastGatherAcceptedDiagnostic()
{
    if (!DdgiFastGatherDiagnosticPixel())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_ACCEPTED_COUNTER, 1u);
}

void AddDdgiFastGatherRejectedDiagnostic(DdgiSampleResult ddgiSample)
{
    if (!DdgiFastGatherDiagnosticPixel())
        return;

    if (ddgiSample.spatialCoverage <= 0.000001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_REJECTED_ZERO_SPATIAL_COUNTER, 1u);
    if (ddgiSample.supportCoverage <= 0.000001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_REJECTED_ZERO_SUPPORT_COUNTER, 1u);
    if (ddgiSample.weight <= 0.000001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_REJECTED_ZERO_DATA_COUNTER, 1u);
    if (ddgiSample.ownershipConsumed <= 0.000001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FAST_GATHER_REJECTED_ZERO_OWNERSHIP_COUNTER, 1u);
}

void AddDdgiShaderGatherFallbackAttemptDiagnostic()
{
    if (!DdgiFastGatherDiagnosticPixel())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SHADER_GATHER_FALLBACK_ATTEMPT_COUNTER, 1u);
}

void AddDdgiShaderGatherFallbackResultDiagnostic(DdgiSampleResult ddgiSample)
{
    if (!DdgiFastGatherDiagnosticPixel())
        return;

    if (DdgiSampleHasUsableGatherData(ddgiSample))
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SHADER_GATHER_FALLBACK_ACCEPTED_COUNTER, 1u);
    else
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SHADER_GATHER_FALLBACK_EMPTY_COUNTER, 1u);
}

bool DdgiRawAtlasRadianceConventionEnabled()
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    return (flags & DDGI_RAW_ATLAS_RADIANCE_CONVENTION_ENABLED_FLAG) != 0u;
}

uint DdgiCacheGeneration()
{
    return ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 16u);
}

uint DdgiCacheLastUpdatedFrameSerial()
{
    return ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 17u);
}

uint DdgiCacheWarmupState()
{
    return ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 18u);
}

bool DdgiCacheValid()
{
    uint cacheGeneration = DdgiCacheGeneration();
    return cacheGeneration > 0u;
}

float DdgiCacheReadiness()
{
    if (!DdgiCacheValid())
        return 0.0;

    uint cacheWarmupState = DdgiCacheWarmupState();
    if (cacheWarmupState == DDGI_WARMUP_STATE_DISABLED)
        return 0.0;
    if (cacheWarmupState == DDGI_WARMUP_STATE_COLD_START)
        return 0.35;
    if (cacheWarmupState == DDGI_WARMUP_STATE_LOCAL_VOLUME)
        return 0.65;
    if (cacheWarmupState == DDGI_WARMUP_STATE_NEAR_CASCADE)
        return 0.85;
    if (cacheWarmupState == DDGI_WARMUP_STATE_RECOVERY)
        return 0.75;

    return 1.0;
}

bool DdgiDebugForceProbeActive()
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    return (flags & DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG) != 0u;
}

bool DdgiDebugBypassFinalSuppression(uint debugViewMode)
{
    return debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE;
}

bool DdgiDebugBypassFinalSuppression()
{
    return DdgiDebugBypassFinalSuppression(ForwardDebugViewMode());
}

bool ReadDdgiGatherTile(out DdgiGatherTileInfo tile)
{
    tile.localVolumeIndex = DDGI_GATHER_INVALID_VOLUME_INDEX;
    tile.primaryClipmapVolumeIndex = DDGI_GATHER_INVALID_VOLUME_INDEX;
    tile.secondaryClipmapVolumeIndex = DDGI_GATHER_INVALID_VOLUME_INDEX;
    tile.flags = DDGI_GATHER_TILE_FALLBACK_FLAG;
    tile.blendWeights = vec4(0.0);

    uint headerFlags = ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), 3u);
    if ((headerFlags & DDGI_GATHER_HEADER_ENABLED_FLAG) == 0u)
        return false;

    uint tileCountX = max(ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), 0u), 1u);
    uint tileCountY = max(ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), 1u), 1u);
    uint tileSize = max(ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), 2u), 1u);
    uvec2 pixel = uvec2(max(gl_FragCoord.xy, vec2(0.0)));
    uvec2 tileCoord = min(pixel / tileSize, uvec2(tileCountX - 1u, tileCountY - 1u));
    uint tileIndex = tileCoord.x + tileCoord.y * tileCountX;
    uint tileBaseWord = uint(SIZEOF_GPU_DDGI_GATHER_TILE_HEADER) / 4u +
        tileIndex * (uint(SIZEOF_GPU_DDGI_GATHER_TILE) / 4u);

    tile.localVolumeIndex = ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), tileBaseWord + uint(OFFSET_GPU_DDGI_GATHER_TILE_LOCAL_VOLUME_INDEX) / 4u);
    tile.primaryClipmapVolumeIndex = ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), tileBaseWord + uint(OFFSET_GPU_DDGI_GATHER_TILE_PRIMARY_CLIPMAP_VOLUME_INDEX) / 4u);
    tile.secondaryClipmapVolumeIndex = ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), tileBaseWord + uint(OFFSET_GPU_DDGI_GATHER_TILE_SECONDARY_CLIPMAP_VOLUME_INDEX) / 4u);
    tile.flags = ReadStorageWord(uint(DDGI_GATHER_TILE_BUFFER_INDEX), tileBaseWord + uint(OFFSET_GPU_DDGI_GATHER_TILE_FLAGS) / 4u);
    tile.blendWeights = ReadStorageVec4(uint(DDGI_GATHER_TILE_BUFFER_INDEX), tileBaseWord + uint(OFFSET_GPU_DDGI_GATHER_TILE_BLEND_WEIGHTS) / 4u);
    return true;
}

vec4 ReadDdgiProbeIrradiance(uint probeIndex, vec3 normal)
{
    uint texelsPerProbe = max(ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 10u), 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uint wordsPerProbe = texelCount * 2u;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    DdgiBilinearOctahedralTexels(normal, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * wordsPerProbe;
    vec4 s00 = ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), baseWord + (c00.y * texelsPerProbe + c00.x) * 2u);
    vec4 s10 = ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), baseWord + (c10.y * texelsPerProbe + c10.x) * 2u);
    vec4 s01 = ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), baseWord + (c01.y * texelsPerProbe + c01.x) * 2u);
    vec4 s11 = ReadPackedDdgiHalf4(uint(DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX), baseWord + (c11.y * texelsPerProbe + c11.x) * 2u);
    return mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y);
}

vec2 ReadDdgiProbeVisibility(uint probeIndex, vec3 probeToPoint)
{
    uint texelsPerProbe = max(ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 11u), 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    DdgiBilinearOctahedralTexels(probeToPoint, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * texelCount;
    vec2 s00 = ReadPackedDdgiHalf2(uint(DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), baseWord + c00.y * texelsPerProbe + c00.x);
    vec2 s10 = ReadPackedDdgiHalf2(uint(DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), baseWord + c10.y * texelsPerProbe + c10.x);
    vec2 s01 = ReadPackedDdgiHalf2(uint(DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), baseWord + c01.y * texelsPerProbe + c01.x);
    vec2 s11 = ReadPackedDdgiHalf2(uint(DDGI_VISIBILITY_ATLAS_BUFFER_INDEX), baseWord + c11.y * texelsPerProbe + c11.x);
    return mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y);
}

float EvaluateDdgiVisibility(
    vec2 moments,
    float probeDistance,
    float viewBias,
    float minProbeSpacing,
    out float mean,
    out float variance)
{
    mean = max(moments.x, 0.0001);
    float mean2 = max(moments.y, mean * mean);
    float minVariance = max(0.005, minProbeSpacing * minProbeSpacing * 0.0025);
    variance = max(mean2 - mean * mean, minVariance);
    if (probeDistance <= mean + max(viewBias, 0.02))
        return 1.0;

    float delta = probeDistance - mean;
    return clamp(variance / (variance + delta * delta), 0.0, 1.0);
}

float DdgiVisibilityConfidence(float visibilityTransport)
{
    return smoothstep(0.02, 0.40, clamp(visibilityTransport, 0.0, 1.0));
}

float DdgiVisibilityMomentTrust(float visibilityConfidence)
{
    return smoothstep(0.05, 0.20, clamp(visibilityConfidence, 0.0, 1.0));
}

void AccumulateDdgiVisibilityMomentDiagnostics(
    float mean,
    float variance,
    float probeDistance,
    float maxRayDistance,
    float visibilityTransport,
    float irradianceConfidence);

DdgiSampleResult SampleDdgiVolumeIrradiance(DdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal, float indirectAo, float globalIntensity)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    vec3 viewVector = pc.Push.CameraPosition - worldPosition;
    float viewLength = length(viewVector);
    vec3 viewDirection = viewLength > 0.0001 ? viewVector / viewLength : vec3(0.0);
    vec3 biasedPosition = worldPosition + normal * info.normalBias + viewDirection * info.viewBias;
    vec3 accumulated = vec3(0.0);
    float totalWeight = 0.0;
    float expectedWeight = 0.0;
    float spatialCoveredWeight = 0.0;
    float supportWeightSum = 0.0;
    float dataWeightSum = 0.0;
    float visibilityWeightedSupport = 0.0;
    float totalVisibility = 0.0;
    float totalActive = 0.0;
    float strongestWeight = -1.0;
    result.rayBudget = clamp(info.raysPerProbe / 128.0, 0.0, 1.0);

    for (uint z = 0u; z <= 1u; z++)
    {
        for (uint y = 0u; y <= 1u; y++)
        {
            for (uint x = 0u; x <= 1u; x++)
            {
                ivec3 corner = info.cellBase + ivec3(x, y, z);
                vec3 trilinear = mix(vec3(1.0) - info.cellFraction, info.cellFraction, vec3(x, y, z));
                float cellWeight = trilinear.x * trilinear.y * trilinear.z;
                if (cellWeight <= 0.000001)
                    continue;

                uint probeIndex = DdgiProbeIndex(info, corner);
                uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
                vec4 stateIrradiance = ReadStorageVec4(uint(DDGI_PROBE_STATE_BUFFER_INDEX), stateBase);
                vec4 relocationAndClassification = ReadStorageVec4(uint(DDGI_PROBE_STATE_BUFFER_INDEX), stateBase + 8u);
                vec4 qualityAndReason = ReadStorageVec4(uint(DDGI_PROBE_STATE_BUFFER_INDEX), stateBase + 12u);
                float probeActive = clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);
                if (DdgiDebugForceProbeActive())
                    probeActive = 1.0;
                vec3 logicalProbePosition = DdgiProbeWorldPosition(info, corner);
                vec3 probePosition = logicalProbePosition + relocationAndClassification.xyz;
                vec3 toProbe = probePosition - worldPosition;
                float distanceToProbe = max(length(toProbe), 0.0001);
                vec3 pointToProbeDirection = toProbe / distanceToProbe;
                float alignment = dot(normal, pointToProbeDirection);
                float normalHemisphereWeight = clamp(alignment * 0.5 + 0.5, 0.0, 1.0);
                float grazingRejection = smoothstep(-0.15, 0.25, alignment);
                float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;

                float distanceWeight = 1.0 / (1.0 + distanceToProbe * 0.025);
                float expectedContributionWeight = cellWeight * normalWeight * distanceWeight;
                expectedWeight += expectedContributionWeight;
                spatialCoveredWeight += expectedContributionWeight;
                if (probeActive <= 0.001)
                {
                    if (DdgiForwardEstimateDiagnosticPixel())
                        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SUPPORT_REJECTED_INACTIVE_COUNTER, 1u);
                    continue;
                }

                vec4 probeIrradianceSample = ReadDdgiProbeIrradiance(probeIndex, normal);
                vec3 probeIrradiance = probeIrradianceSample.rgb;
                float irradianceConfidence = clamp(probeIrradianceSample.w, 0.0, 1.0);
                float rayHitConfidence = clamp(qualityAndReason.x, 0.0, 1.0);
                float stateIrradianceConfidence = clamp(qualityAndReason.y, 0.0, 1.0);
                float visibilityConfidence = clamp(qualityAndReason.z, 0.0, 1.0);
                if (DdgiForwardEstimateDiagnosticPixel())
                {
                    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_PROBE_IRRADIANCE_ALPHA_COUNTER, PackDdgiForwardEstimateWeight(irradianceConfidence));
                    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_PROBE_QUALITY_X_COUNTER, PackDdgiForwardEstimateWeight(rayHitConfidence));
                    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_PROBE_QUALITY_Y_COUNTER, PackDdgiForwardEstimateWeight(stateIrradianceConfidence));
                    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_PROBE_QUALITY_Z_COUNTER, PackDdgiForwardEstimateWeight(visibilityConfidence));
                    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_PROBE_QUALITY_SAMPLE_COUNT_COUNTER, 1u);
                }
                float qualityConfidence = clamp(max(rayHitConfidence, 0.25) * max(stateIrradianceConfidence, irradianceConfidence) * max(visibilityConfidence, 0.25), 0.0, 1.0);
                if (DdgiDebugBypassFinalSuppression())
                    qualityConfidence = max(qualityConfidence, 0.25);
                if (irradianceConfidence <= 0.000001)
                {
                    if (DdgiForwardEstimateDiagnosticPixel())
                        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SUPPORT_REJECTED_ZERO_IRRADIANCE_ALPHA_COUNTER, 1u);
                    continue;
                }
                if (qualityConfidence <= 0.000001 || expectedContributionWeight <= 0.000001)
                {
                    if (DdgiForwardEstimateDiagnosticPixel())
                        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_SUPPORT_REJECTED_LOW_QUALITY_COUNTER, 1u);
                    continue;
                }

                float supportWeight = expectedContributionWeight * probeActive * irradianceConfidence * qualityConfidence;
                supportWeightSum += supportWeight;

                vec3 probeToBiasedPoint = biasedPosition - probePosition;
                float biasedDistanceToProbe = max(length(probeToBiasedPoint), 0.0001);
                vec3 probeToPointDirection = probeToBiasedPoint / biasedDistanceToProbe;
                float minProbeSpacing = max(min(min(info.spacing.x, info.spacing.y), info.spacing.z), 0.001);
                float visibilityMean = info.maxRayDistance;
                float visibilityVariance = 0.0;
                float visibilityTransport = 1.0;
                float visibilityTrust = DdgiVisibilityMomentTrust(visibilityConfidence);
                if (visibilityTrust > 0.000001)
                {
                    vec2 visibilityMoments = ReadDdgiProbeVisibility(probeIndex, probeToPointDirection);
                    visibilityTransport = EvaluateDdgiVisibility(
                        visibilityMoments,
                        biasedDistanceToProbe,
                        info.viewBias,
                        minProbeSpacing,
                        visibilityMean,
                        visibilityVariance);
                }
                float visibilityAttenuation = mix(
                    1.0,
                    clamp(visibilityTransport, 0.0, 1.0),
                    clamp(visibilityTrust, 0.0, 1.0));
                float probeVisibilityConfidence = DdgiVisibilityConfidence(visibilityAttenuation);
                AccumulateDdgiVisibilityMomentDiagnostics(
                    visibilityMean,
                    visibilityVariance,
                    biasedDistanceToProbe,
                    info.maxRayDistance,
                    visibilityTransport,
                    irradianceConfidence);
                float radianceWeight = supportWeight;
                accumulated += clamp(probeIrradiance, vec3(0.0), vec3(64.0)) * radianceWeight;
                totalWeight += radianceWeight;
                dataWeightSum += radianceWeight;
                visibilityWeightedSupport += supportWeight * visibilityAttenuation;
                totalVisibility += probeVisibilityConfidence * supportWeight;
                totalActive += probeActive * irradianceConfidence * qualityConfidence * cellWeight;

                if (supportWeight > strongestWeight)
                {
                    uint relocationBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION) / 4u);
                    vec4 classification = ReadStorageVec4(uint(DDGI_PROBE_RELOCATION_CLASSIFICATION_BUFFER_INDEX), relocationBase + 4u);
                    strongestWeight = supportWeight;
                    result.probeIndex = probeIndex;
                    result.relocation = relocationAndClassification.xyz;
                    result.logicalProbePosition = logicalProbePosition;
                    result.relocatedProbePosition = probePosition;
                    result.minProbeSpacing = minProbeSpacing;
                    result.classificationInvalidScore = clamp(classification.y, 0.0, 1.0);
                    result.visibility = probeVisibilityConfidence;
                    result.activeProbe = probeActive;
                    result.leakClamp = visibilityAttenuation * normalWeight * indirectAo;
                    result.visibilityMomentMean = visibilityMean;
                    result.visibilityMomentVariance = visibilityVariance;
                    result.visibilityProbeDistance = biasedDistanceToProbe;
                    result.visibilityMaxRayDistance = info.maxRayDistance;
                    result.updateReason = clamp(qualityAndReason.w / 255.0, 0.0, 1.0);
                    result.irradianceAtlasConfidence = irradianceConfidence;
                    result.rayHitConfidence = rayHitConfidence;
                    result.stateIrradianceConfidence = stateIrradianceConfidence;
                    result.visibilityConfidence = visibilityConfidence;
                    result.qualityConfidence = qualityConfidence;
                    result.strongestSupportWeight = supportWeight;
                }
            }
        }
    }

    float volumeEdgeFade = info.edgeFade;
    float safeExpectedWeight = max(expectedWeight, 0.000001);
    float edgeFade = clamp(volumeEdgeFade, 0.0, 1.0);
    float spatialCoverage = clamp(spatialCoveredWeight / safeExpectedWeight, 0.0, 1.0) * edgeFade;
    float supportCoverage = clamp(supportWeightSum / safeExpectedWeight, 0.0, 1.0) * edgeFade;
    float dataConfidence = clamp(dataWeightSum / safeExpectedWeight, 0.0, 1.0) * edgeFade;
    result.coverage = spatialCoverage;
    result.spatialCoverage = spatialCoverage;
    result.supportCoverage = supportCoverage;
    result.sampleTotalWeight = dataWeightSum;
    result.sampleExpectedWeight = expectedWeight;
    result.visibility = clamp(totalVisibility / max(supportWeightSum, 0.000001), 0.0, 1.0);
    result.activeProbe = clamp(totalActive, 0.0, 1.0);

    if (totalWeight <= 0.000001)
        return result;

    float finalIntensity = DdgiRawAtlasRadianceConventionEnabled()
        ? globalIntensity * info.volumeIntensity
        : globalIntensity;
    result.irradiance = clamp((accumulated / totalWeight) * finalIntensity, vec3(0.0), vec3(64.0));
    result.weight = dataConfidence;
    result.leakClamp = clamp(visibilityWeightedSupport / max(supportWeightSum, 0.000001), 0.0, 1.0);
    result.leakClamp = clamp(result.leakClamp, 0.0, 1.0);
    result.cascadeIndex = float(info.cascadeIndex);
    result.cascadeBlendWeight = clamp(volumeEdgeFade, 0.0, 1.0);
    return result;
}

float AccumulateDdgiCandidate(
    uint volumeIndex,
    uint volumeCount,
    vec3 worldPosition,
    vec3 normal,
    float indirectAo,
    float globalIntensity,
    inout vec3 blendedIrradiance,
    inout float blendedSpatialCoverage,
    inout float blendedSupportCoverage,
    inout float totalOwnership,
    inout float remainingOwnership,
    inout float blendedVisibility,
    inout float blendedActive,
    inout float blendedDataConfidence,
    inout float blendedLeakClamp,
    inout float bestDebugWeight,
    inout DdgiSampleResult result)
{
    if (volumeIndex == DDGI_GATHER_INVALID_VOLUME_INDEX || volumeIndex >= volumeCount || remainingOwnership <= 0.001)
        return -1.0;

    DdgiVolumeSampleInfo info;
    if (!ReadDdgiVolumeSampleInfo(volumeIndex, worldPosition, info))
        return -1.0;

    DdgiSampleResult candidate = SampleDdgiVolumeIrradiance(info, worldPosition, normal, indirectAo, globalIntensity);
    float candidateSpatial = clamp(candidate.spatialCoverage, 0.0, 1.0);
    float candidateSupport = clamp(candidate.supportCoverage, 0.0, 1.0);
    float candidateData = clamp(candidate.weight, 0.0, 1.0);
    result.spatialCoverage = max(result.spatialCoverage, candidateSpatial);
    result.coverage = result.spatialCoverage;
    if (candidateSpatial <= 0.000001)
        return -1.0;

    float candidateOwnership = candidateSupport * smoothstep(0.02, 0.25, candidateData);
    if (candidateOwnership <= 0.000001)
        return -1.0;

    float blendWeight = clamp(candidateOwnership * remainingOwnership, 0.0, remainingOwnership);
    blendedIrradiance += candidate.irradiance * blendWeight;
    blendedSpatialCoverage = max(blendedSpatialCoverage, candidateSpatial);
    blendedSupportCoverage += candidateSupport * blendWeight;
    totalOwnership += blendWeight;
    blendedVisibility += candidate.visibility * blendWeight;
    blendedActive += candidate.activeProbe * blendWeight;
    blendedDataConfidence += candidateData * blendWeight;
    blendedLeakClamp += candidate.leakClamp * blendWeight;
    remainingOwnership = clamp(remainingOwnership - blendWeight, 0.0, 1.0);

    if (blendWeight > bestDebugWeight)
    {
        bestDebugWeight = blendWeight;
        result.probeIndex = candidate.probeIndex;
        result.relocation = candidate.relocation;
        result.minProbeSpacing = candidate.minProbeSpacing;
        result.classificationInvalidScore = candidate.classificationInvalidScore;
        result.cascadeIndex = candidate.cascadeIndex;
        result.cascadeBlendWeight = candidate.cascadeBlendWeight;
        result.updateReason = candidate.updateReason;
        result.rayBudget = candidate.rayBudget;
        result.irradianceAtlasConfidence = candidate.irradianceAtlasConfidence;
        result.rayHitConfidence = candidate.rayHitConfidence;
        result.stateIrradianceConfidence = candidate.stateIrradianceConfidence;
        result.visibilityConfidence = candidate.visibilityConfidence;
        result.qualityConfidence = candidate.qualityConfidence;
        result.strongestSupportWeight = candidate.strongestSupportWeight;
        result.sampleTotalWeight = candidate.sampleTotalWeight;
        result.sampleExpectedWeight = candidate.sampleExpectedWeight;
    }

    return candidate.cascadeBlendWeight;
}

void AddDdgiClipmapCoverageAttempt(float primaryBlendWeight)
{
    if (!DdgiClipmapCoverageCountersEnabled())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_ATTEMPT_COUNTER, 1u);
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_BLEND_WEIGHT_COUNTER, PackDdgiForwardEstimateWeight(primaryBlendWeight));
}

void AddDdgiClipmapCoverageOk(float primaryEdgeFade, float primaryBlendWeight)
{
    if (!DdgiClipmapCoverageCountersEnabled())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_OK_COUNTER, 1u);
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_EDGE_FADE_COUNTER, PackDdgiForwardEstimateWeight(primaryEdgeFade));
}

void AddDdgiClipmapCoverageFail(float primaryBlendWeight)
{
    if (!DdgiClipmapCoverageCountersEnabled())
        return;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_CLIPMAP_INFO_PRIMARY_FAILED_COUNTER, 1u);
}

void AddDdgiClipmapCoverageDiagnostics(DdgiGatherTileInfo tile, uint volumeCount, vec3 worldPosition)
{
    if (!DdgiClipmapCoverageDiagnosticPixel())
        return;

    if ((tile.flags & DDGI_GATHER_TILE_PRIMARY_CLIPMAP_VALID_FLAG) == 0u)
        return;

    AddDdgiClipmapCoverageAttempt(tile.blendWeights.y);

    DdgiVolumeSampleInfo info;
    bool primaryInfoOk =
        tile.primaryClipmapVolumeIndex != DDGI_GATHER_INVALID_VOLUME_INDEX &&
        tile.primaryClipmapVolumeIndex < volumeCount &&
        ReadDdgiVolumeSampleInfo(tile.primaryClipmapVolumeIndex, worldPosition, info);

    if (primaryInfoOk)
        AddDdgiClipmapCoverageOk(info.edgeFade, tile.blendWeights.y);
    else
        AddDdgiClipmapCoverageFail(tile.blendWeights.y);
}

DdgiSampleResult ResolveDdgiAccumulation(
    DdgiSampleResult result,
    vec3 blendedIrradiance,
    float blendedSpatialCoverage,
    float blendedSupportCoverage,
    float totalOwnership,
    float blendedVisibility,
    float blendedActive,
    float blendedDataConfidence,
    float blendedLeakClamp)
{
    result.spatialCoverage = max(result.spatialCoverage, clamp(blendedSpatialCoverage, 0.0, 1.0));
    result.coverage = result.spatialCoverage;

    if (totalOwnership <= 0.000001)
        return result;

    float invOwnership = 1.0 / max(totalOwnership, 0.000001);
    result.irradiance = clamp(blendedIrradiance * invOwnership, vec3(0.0), vec3(64.0));
    result.supportCoverage = clamp(blendedSupportCoverage * invOwnership, 0.0, 1.0);
    result.ownershipConsumed = clamp(totalOwnership, 0.0, 1.0);
    result.weight = clamp(blendedDataConfidence * invOwnership, 0.0, 1.0);
    result.visibility = clamp(blendedVisibility * invOwnership, 0.0, 1.0);
    result.activeProbe = clamp(blendedActive * invOwnership, 0.0, 1.0);
    result.leakClamp = clamp(blendedLeakClamp * invOwnership, 0.0, 1.0);
    return result;
}

DdgiSampleResult SampleDdgiGatherCandidates(DdgiGatherTileInfo tile, uint volumeCount, vec3 worldPosition, vec3 normal, float indirectAo, float globalIntensity)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    vec3 blendedIrradiance = vec3(0.0);
    float blendedSpatialCoverage = 0.0;
    float blendedSupportCoverage = 0.0;
    float totalOwnership = 0.0;
    float remainingOwnership = 1.0;
    float blendedVisibility = 0.0;
    float blendedActive = 0.0;
    float blendedDataConfidence = 0.0;
    float blendedLeakClamp = 0.0;
    float bestDebugWeight = -1.0;
    if ((tile.flags & DDGI_GATHER_TILE_LOCAL_VOLUME_VALID_FLAG) != 0u &&
        tile.blendWeights.x > 0.0001)
        AccumulateDdgiCandidate(
            tile.localVolumeIndex,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity,
            blendedIrradiance,
            blendedSpatialCoverage,
            blendedSupportCoverage,
            totalOwnership,
            remainingOwnership,
            blendedVisibility,
            blendedActive,
            blendedDataConfidence,
            blendedLeakClamp,
            bestDebugWeight,
            result);

    float primaryClipmapEdgeFade = -1.0;
    bool primaryClipmapAttempt =
        (tile.flags & DDGI_GATHER_TILE_PRIMARY_CLIPMAP_VALID_FLAG) != 0u &&
        tile.blendWeights.y > 0.0001 &&
        remainingOwnership > 0.001;
    if (primaryClipmapAttempt)
    {
        primaryClipmapEdgeFade = AccumulateDdgiCandidate(
            tile.primaryClipmapVolumeIndex,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity,
            blendedIrradiance,
            blendedSpatialCoverage,
            blendedSupportCoverage,
            totalOwnership,
            remainingOwnership,
            blendedVisibility,
            blendedActive,
            blendedDataConfidence,
            blendedLeakClamp,
            bestDebugWeight,
            result);
    }

    bool nearClipmapTransition = primaryClipmapEdgeFade < 0.0 || primaryClipmapEdgeFade < 0.985;
    if ((tile.flags & DDGI_GATHER_TILE_SECONDARY_CLIPMAP_VALID_FLAG) != 0u &&
        tile.blendWeights.z > 0.0001 &&
        nearClipmapTransition &&
        remainingOwnership > 0.001)
    {
        AccumulateDdgiCandidate(
            tile.secondaryClipmapVolumeIndex,
            volumeCount,
            worldPosition,
            normal,
            indirectAo,
            globalIntensity,
            blendedIrradiance,
            blendedSpatialCoverage,
            blendedSupportCoverage,
            totalOwnership,
            remainingOwnership,
            blendedVisibility,
            blendedActive,
            blendedDataConfidence,
            blendedLeakClamp,
            bestDebugWeight,
            result);
    }

    return ResolveDdgiAccumulation(
        result,
        blendedIrradiance,
        blendedSpatialCoverage,
        blendedSupportCoverage,
        totalOwnership,
        blendedVisibility,
        blendedActive,
        blendedDataConfidence,
        blendedLeakClamp);
}

DdgiSampleResult SampleDdgiIrradianceExhaustive(uint volumeCount, vec3 worldPosition, vec3 normal, float indirectAo, float globalIntensity)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    vec3 blendedIrradiance = vec3(0.0);
    float blendedSpatialCoverage = 0.0;
    float blendedSupportCoverage = 0.0;
    float totalOwnership = 0.0;
    float remainingOwnership = 1.0;
    float blendedVisibility = 0.0;
    float blendedActive = 0.0;
    float blendedDataConfidence = 0.0;
    float blendedLeakClamp = 0.0;
    float bestDebugWeight = -1.0;

    for (uint pass = 0u; pass < 2u && remainingOwnership > 0.001; pass++)
    {
        bool sampleAuthored = pass == 0u;
        for (uint volumeIndex = 0u; volumeIndex < 16u; volumeIndex++)
        {
            if (volumeIndex >= volumeCount || remainingOwnership <= 0.001)
                break;

            DdgiVolumeSampleInfo info;
            if (!ReadDdgiVolumeSampleInfo(volumeIndex, worldPosition, info))
                continue;

            bool isAuthored = info.kind == DDGI_VOLUME_KIND_AUTHORED;
            if (isAuthored != sampleAuthored)
                continue;

            AccumulateDdgiCandidate(
                volumeIndex,
                volumeCount,
                worldPosition,
                normal,
                indirectAo,
                globalIntensity,
                blendedIrradiance,
                blendedSpatialCoverage,
                blendedSupportCoverage,
                totalOwnership,
                remainingOwnership,
                blendedVisibility,
                blendedActive,
                blendedDataConfidence,
                blendedLeakClamp,
                bestDebugWeight,
                result);
        }
    }

    return ResolveDdgiAccumulation(
        result,
        blendedIrradiance,
        blendedSpatialCoverage,
        blendedSupportCoverage,
        totalOwnership,
        blendedVisibility,
        blendedActive,
        blendedDataConfidence,
        blendedLeakClamp);
}

DdgiSampleResult SampleDdgiIrradiance(vec3 worldPosition, vec3 normal, float indirectAo)
{
    DdgiSampleResult result = EmptyDdgiSampleResult();
    if (ForwardGlobalIlluminationEnabled() == 0u)
        return result;

    uint volumeCount;
    if (!DdgiHeaderEnabled(volumeCount))
        return result;

    float globalIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 12u), 0.0, 8.0);
    DdgiGatherTileInfo tile;
    if (ReadDdgiGatherTile(tile))
    {
        AddDdgiClipmapCoverageDiagnostics(tile, volumeCount, worldPosition);

        if ((tile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) == 0u)
        {
            AddDdgiFastGatherAttemptDiagnostic();
            DdgiSampleResult gatherResult = SampleDdgiGatherCandidates(tile, volumeCount, worldPosition, normal, indirectAo, globalIntensity);
            if (DdgiSampleHasUsableGatherData(gatherResult))
            {
                AddDdgiFastGatherAcceptedDiagnostic();
                return gatherResult;
            }

            if (DdgiExhaustiveGatherFallbackEnabled())
            {
                AddDdgiFastGatherRejectedDiagnostic(gatherResult);
                AddDdgiShaderGatherFallbackAttemptDiagnostic();
                DdgiSampleResult fallbackResult = SampleDdgiIrradianceExhaustive(min(volumeCount, 16u), worldPosition, normal, indirectAo, globalIntensity);
                AddDdgiShaderGatherFallbackResultDiagnostic(fallbackResult);
                return fallbackResult;
            }

            AddDdgiFastGatherRejectedDiagnostic(gatherResult);
            return gatherResult;
        }
    }

    if (DdgiExhaustiveGatherFallbackEnabled())
    {
        AddDdgiShaderGatherFallbackAttemptDiagnostic();
        DdgiSampleResult fallbackResult = SampleDdgiIrradianceExhaustive(min(volumeCount, 16u), worldPosition, normal, indirectAo, globalIntensity);
        AddDdgiShaderGatherFallbackResultDiagnostic(fallbackResult);
        return fallbackResult;
    }

    return result;
}

vec3 SampleDdgiDiffuse(DdgiSampleResult ddgi, vec3 albedo, float metallic)
{
    float diffuseWeight = 1.0 - clamp(metallic, 0.0, 1.0);
    return ddgi.irradiance * (albedo / PI) * diffuseWeight;
}

struct HybridDiffuseGiResult
{
    vec3 diffuse;
    float ddgiCoverage;
    float environmentFallbackWeight;
    float nearContactSuppression;
    float effectiveDdgiWeight;
    vec3 suppressionMask;
};

float DdgiDiagnosticLuminance(vec3 value)
{
    return dot(max(value, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

uint PackDdgiForwardEstimateWeight(float value)
{
    return uint(round(clamp(value, 0.0, 1.0) * DDGI_FORWARD_ESTIMATE_WEIGHT_SCALE));
}

uint PackDdgiForwardEstimateLuminance(float value)
{
    return uint(round(clamp(value, 0.0, 16.0) * DDGI_FORWARD_ESTIMATE_LUMINANCE_SCALE));
}

uint PackDdgiForwardEstimateVisibilityMetric(float value)
{
    return uint(round(clamp(value, 0.0, 64.0) * DDGI_FORWARD_ESTIMATE_WEIGHT_SCALE));
}

void AccumulateDdgiVisibilityMomentDiagnostics(
    float mean,
    float variance,
    float probeDistance,
    float maxRayDistance,
    float visibilityTransport,
    float irradianceConfidence)
{
    if (!DdgiForwardEstimateDiagnosticPixel())
        return;

    float safeMaxRayDistance = max(maxRayDistance, 0.0001);
    float standardDeviation = sqrt(max(variance, 0.0));
    bool largeDistanceMargin = probeDistance > mean + max(standardDeviation * 3.0, safeMaxRayDistance * 0.10);
    bool zeroTransport = visibilityTransport <= 0.000001;

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_MOMENT_MEAN_COUNTER, PackDdgiForwardEstimateVisibilityMetric(mean));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_MOMENT_VARIANCE_COUNTER, PackDdgiForwardEstimateVisibilityMetric(variance));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_PROBE_DISTANCE_COUNTER, PackDdgiForwardEstimateVisibilityMetric(probeDistance));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_MOMENT_SAMPLE_COUNT_COUNTER, 1u);

    if (largeDistanceMargin)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_LARGE_DISTANCE_MARGIN_COUNTER, 1u);
    if (zeroTransport)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_ZERO_TRANSPORT_COUNTER, 1u);
    if (zeroTransport && irradianceConfidence > 0.000001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_VISIBILITY_ZERO_TRANSPORT_WITH_IRRADIANCE_COUNTER, 1u);
}

void AccumulateDdgiForwardEstimateDiagnostics(HybridDiffuseGiResult hybridDiffuse, DdgiSampleResult ddgi, vec3 rawDdgiDiffuse)
{
    if (!DdgiForwardEstimateDiagnosticPixel())
        return;

    float spatialCoverage = clamp(ddgi.spatialCoverage, 0.0, 1.0);
    float supportCoverage = clamp(ddgi.supportCoverage, 0.0, 1.0);
    float dataConfidence = clamp(ddgi.weight, 0.0, 1.0);
    float visibilityConfidence = clamp(ddgi.visibility, 0.0, 1.0);
    float leakAttenuation = clamp(1.0 - hybridDiffuse.nearContactSuppression, 0.0, 1.0);
    float effectiveWeight = clamp(hybridDiffuse.effectiveDdgiWeight, 0.0, 1.0);
    float ownershipConsumed = clamp(ddgi.ownershipConsumed, 0.0, 1.0);

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_SPATIAL_COVERAGE_COUNTER, PackDdgiForwardEstimateWeight(spatialCoverage));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_SUPPORT_COVERAGE_COUNTER, PackDdgiForwardEstimateWeight(supportCoverage));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_DATA_CONFIDENCE_COUNTER, PackDdgiForwardEstimateWeight(dataConfidence));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_VISIBILITY_CONFIDENCE_COUNTER, PackDdgiForwardEstimateWeight(visibilityConfidence));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_LEAK_ATTENUATION_COUNTER, PackDdgiForwardEstimateWeight(leakAttenuation));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_EFFECTIVE_WEIGHT_COUNTER, PackDdgiForwardEstimateWeight(effectiveWeight));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_RAW_LUMINANCE_COUNTER, PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(rawDdgiDiffuse)));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_FINAL_LUMINANCE_COUNTER, PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(hybridDiffuse.diffuse)));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_OWNERSHIP_COUNTER, PackDdgiForwardEstimateWeight(ownershipConsumed));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_SAMPLE_COUNT_COUNTER, 1u);

    if (spatialCoverage > 0.75 && supportCoverage < 0.0001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_ZERO_SUPPORT_SPATIAL_COUNTER, 1u);
    if (spatialCoverage > 0.75 && effectiveWeight < 0.0001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_ZERO_EFFECTIVE_SPATIAL_COUNTER, 1u);
}

vec3 SafeRadiance(vec3 value)
{
    if (any(isnan(value)) || any(isinf(value)))
        return vec3(0.0);

    return clamp(value, vec3(0.0), vec3(64.0));
}

HybridDiffuseGiResult ComposeHybridDiffuseGi(vec3 diffuseIbl, vec3 ddgiDiffuse, DdgiSampleResult ddgi, float indirectAo, float environmentFallbackIntensity, uint debugViewMode)
{
    HybridDiffuseGiResult result;
    float spatialCoverage = clamp(ddgi.coverage, 0.0, 1.0);
    float supportCoverage = clamp(ddgi.supportCoverage, 0.0, 1.0);
    float dataConfidence = clamp(ddgi.weight, 0.0, 1.0);
    if (DdgiCacheGeneration() == 0u)
    {
        dataConfidence = 0.0;
        supportCoverage = 0.0;
    }
    float visibilityConfidence = clamp(ddgi.visibility, 0.0, 1.0);
    float visibilityTransport = clamp(ddgi.leakClamp, 0.0, 1.0);
    float indirectAoWeight = clamp(indirectAo, 0.0, 1.0);
    float thinWallLeakClampStrength = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 14u), 0.0, 1.0);
    float thinWallProxyThickness = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 15u), 0.0, 1.0);
    float leakStrength = clamp(thinWallLeakClampStrength * mix(0.35, 0.85, clamp(thinWallProxyThickness * 8.0, 0.0, 1.0)), 0.0, 0.85);
    float leakAttenuation = clamp(mix(1.0, visibilityTransport, leakStrength), 0.05, 1.0);
    float supportTrust = supportCoverage * smoothstep(0.02, 0.25, dataConfidence);
    float ddgiTrust = clamp(supportTrust * leakAttenuation, 0.0, 1.0);
    float environmentTrust = clamp(1.0 - ddgiTrust, 0.0, 1.0);
    vec3 debugSuppression = vec3(
        supportCoverage,
        leakAttenuation,
        dataConfidence);
    float environmentFallbackWeight = clamp(environmentTrust * environmentFallbackIntensity, 0.0, 4.0);
    vec3 ddgiLowFrequencyField = SafeRadiance(ddgiDiffuse * ddgiTrust);
    vec3 environmentFallbackField = SafeRadiance(diffuseIbl * environmentFallbackWeight);
    vec3 nearField = vec3(0.0);

    if (dataConfidence <= 0.000001)
    {
        result.diffuse = SafeRadiance(environmentFallbackField * indirectAoWeight);
        result.ddgiCoverage = spatialCoverage;
        result.environmentFallbackWeight = environmentFallbackWeight;
        result.nearContactSuppression = 0.0;
        result.effectiveDdgiWeight = 0.0;
        result.suppressionMask = debugSuppression;
        return result;
    }

    if (DdgiDebugBypassFinalSuppression(debugViewMode))
    {
        result.diffuse = SafeRadiance(ddgiDiffuse);
        result.ddgiCoverage = spatialCoverage;
        result.environmentFallbackWeight = 0.0;
        result.nearContactSuppression = 0.0;
        result.effectiveDdgiWeight = ddgiTrust;
        result.suppressionMask = debugSuppression;
        return result;
    }

    result.diffuse = SafeRadiance(ddgiLowFrequencyField + (environmentFallbackField + nearField) * indirectAoWeight);
    result.ddgiCoverage = spatialCoverage;
    result.environmentFallbackWeight = environmentFallbackWeight;
    result.nearContactSuppression = 1.0 - leakAttenuation;
    result.effectiveDdgiWeight = ddgiTrust;
    result.suppressionMask = debugSuppression;
    return result;
}

uint SelectShadowCascade(float cameraDistance, vec4 splits, uint cascadeCount)
{
    for (uint cascade = 0u; cascade < cascadeCount; cascade++)
    {
        if (cameraDistance <= splits[cascade])
            return cascade;
    }

    return max(cascadeCount, 1u) - 1u;
}

float CameraForwardDistance(vec3 worldPosition)
{
    vec3 cameraForward = -normalize(vec3(
        pc.Push.InverseViewMatrix[2][0],
        pc.Push.InverseViewMatrix[2][1],
        pc.Push.InverseViewMatrix[2][2]));

    return max(dot(worldPosition - pc.Push.CameraPosition, cameraForward), 0.0);
}

float SampleShadowCascade(uint textureIndex, vec2 uv, float receiverDepth, float bias)
{
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || receiverDepth < 0.0 || receiverDepth > 1.0)
        return 1.0;

    float sampledDepth = texture(BindlessTextures[nonuniformEXT(int(textureIndex))], uv).r;
    return receiverDepth >= sampledDepth - bias ? 1.0 : 0.0;
}

float EvaluateDirectionalShadow(uint lightIndex, vec3 worldPosition, vec3 normal, out uint selectedCascade)
{
    selectedCascade = 0u;
    vec4 shadowIndices = ReadShadowIndices();
    if (shadowIndices.x < 0.5 ||
        (pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX) && ForwardTransparentReceiveShadows() == 0u) ||
        int(round(shadowIndices.w)) != int(lightIndex))
        return 1.0;

    vec4 shadowSettings = ReadShadowSettings();
    uint cascadeCount = clamp(uint(round(shadowIndices.y)), 1u, uint(MAX_DIRECTIONAL_SHADOW_TEXTURES));
    vec4 splits = ReadShadowCascadeSplits();
    float cameraDistance = CameraForwardDistance(worldPosition);
    selectedCascade = SelectShadowCascade(cameraDistance, splits, cascadeCount);

    vec3 biasedPosition = worldPosition + normal * shadowSettings.y;
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), ReadShadowMatrix(selectedCascade));
    vec3 shadowCoord = lightClip.xyz / max(lightClip.w, 0.00001);
    vec2 uv = shadowCoord.xy * 0.5 + vec2(0.5);
    float receiverDepth = shadowCoord.z;

    float mapSize = max(shadowSettings.z, 1.0);
    int radius = int(clamp(round(shadowSettings.w), 0.0, 3.0));
    vec2 texelSize = vec2(1.0 / mapSize);
    uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + selectedCascade;
    if (radius <= 0)
        return SampleShadowCascade(textureIndex, uv, receiverDepth, 0.0005);

    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            lit += SampleShadowCascade(textureIndex, uv + vec2(x, y) * texelSize, receiverDepth, 0.0005);
            taps += 1.0;
        }
    }

    return taps > 0.0 ? lit / taps : 1.0;
}

float CompareReverseZDepth(float receiverDepth, float sampledDepth, float bias)
{
    if (receiverDepth < 0.0 || receiverDepth > 1.0)
        return 1.0;
    return receiverDepth >= sampledDepth - bias ? 1.0 : 0.0;
}

float EvaluateSpotShadow(uint lightIndex, vec3 worldPosition, vec3 normal)
{
    int shadowIndex = ReadLocalSpotShadowIndex(lightIndex);
    if (shadowIndex < 0 ||
        (pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX) && ForwardTransparentReceiveShadows() == 0u))
        return 1.0;

    GPUSpotShadow shadow = ReadSpotShadow(uint(shadowIndex));
    if (shadow.Enabled == 0 || shadow.LightIndex != int(lightIndex))
        return 1.0;
    if (shadow.BiasStrengthTexelSize.z <= 0.0)
        return 1.0;

    vec3 biasedPosition = worldPosition + normal * shadow.BiasStrengthTexelSize.x;
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), shadow.LightViewProjection);
    vec3 shadowCoord = lightClip.xyz / max(lightClip.w, 0.00001);
    vec2 localUv = shadowCoord.xy * 0.5 + vec2(0.5);
    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return 1.0;

    vec2 atlasUv = localUv * shadow.AtlasScaleOffset.xy + shadow.AtlasScaleOffset.zw;
    vec2 minUv = shadow.AtlasScaleOffset.zw;
    vec2 maxUv = shadow.AtlasScaleOffset.zw + shadow.AtlasScaleOffset.xy;
    int radius = int(clamp(shadow.PcfRadius, 0, 3));
    vec2 texelSize = vec2(shadow.BiasStrengthTexelSize.w);
    if (radius <= 0)
    {
        float sampledDepth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], atlasUv).r;
        float visibility = CompareReverseZDepth(shadowCoord.z, sampledDepth, shadow.BiasStrengthTexelSize.y);
        return mix(1.0, visibility, shadow.BiasStrengthTexelSize.z);
    }

    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            vec2 sampleUv = clamp(atlasUv + vec2(x, y) * texelSize, minUv, maxUv);
            float sampledDepth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], sampleUv).r;
            lit += CompareReverseZDepth(shadowCoord.z, sampledDepth, shadow.BiasStrengthTexelSize.y);
            taps += 1.0;
        }
    }

    float visibility = taps > 0.0 ? lit / taps : 1.0;
    return mix(1.0, visibility, shadow.BiasStrengthTexelSize.z);
}

uint SelectPointShadowFace(vec3 direction)
{
    vec3 absDir = abs(direction);
    if (absDir.x >= absDir.y && absDir.x >= absDir.z)
        return direction.x >= 0.0 ? 0u : 1u;
    if (absDir.y >= absDir.x && absDir.y >= absDir.z)
        return direction.y >= 0.0 ? 2u : 3u;
    return direction.z >= 0.0 ? 4u : 5u;
}

mat4 PointShadowFaceMatrix(GPUPointShadow shadow, uint faceIndex)
{
    if (faceIndex == 0u)
        return shadow.FaceViewProjection0;
    if (faceIndex == 1u)
        return shadow.FaceViewProjection1;
    if (faceIndex == 2u)
        return shadow.FaceViewProjection2;
    if (faceIndex == 3u)
        return shadow.FaceViewProjection3;
    if (faceIndex == 4u)
        return shadow.FaceViewProjection4;
    return shadow.FaceViewProjection5;
}

bool ProjectPointShadowFace(
    GPUPointShadow shadow,
    uint faceIndex,
    vec3 biasedPosition,
    out vec3 shadowCoord,
    out vec2 faceUv)
{
    vec4 lightClip = MulRowMajor(vec4(biasedPosition, 1.0), PointShadowFaceMatrix(shadow, faceIndex));
    if (lightClip.w <= 0.00001)
        return false;

    shadowCoord = lightClip.xyz / lightClip.w;
    faceUv = shadowCoord.xy * 0.5 + vec2(0.5);
    return faceUv.x >= 0.0 && faceUv.x <= 1.0 &&
           faceUv.y >= 0.0 && faceUv.y <= 1.0 &&
           shadowCoord.z >= 0.0 && shadowCoord.z <= 1.0;
}

float SamplePointShadowFace(
    GPUPointShadow shadow,
    uint faceIndex,
    vec3 biasedPosition,
    int radius,
    vec2 texelSize,
    out vec2 faceUv)
{
    vec3 shadowCoord;
    if (!ProjectPointShadowFace(shadow, faceIndex, biasedPosition, shadowCoord, faceUv))
    {
        faceUv = vec2(0.5);
        return 1.0;
    }

    float layer = float(shadow.CubemapIndex * 6 + int(faceIndex));
    float lit = 0.0;
    float taps = 0.0;
    for (int y = -radius; y <= radius; y++)
    {
        for (int x = -radius; x <= radius; x++)
        {
            vec2 sampleUv = faceUv + vec2(x, y) * texelSize;
            if (sampleUv.x < 0.0 || sampleUv.x > 1.0 || sampleUv.y < 0.0 || sampleUv.y > 1.0)
                continue;

            float sampledDepth = texture(BindlessArrayTextures[nonuniformEXT(POINT_SHADOW_CUBEMAP_ARRAY_TEXTURE_INDEX)], vec3(sampleUv, layer)).r;
            lit += CompareReverseZDepth(shadowCoord.z, sampledDepth, shadow.BiasStrengthTexelSize.y);
            taps += 1.0;
        }
    }

    return taps > 0.0 ? lit / taps : 1.0;
}

float PointShadowFaceEdgeDistance(vec2 faceUv)
{
    return min(min(faceUv.x, 1.0 - faceUv.x), min(faceUv.y, 1.0 - faceUv.y));
}

float EvaluatePointShadow(uint lightIndex, vec3 worldPosition, vec3 normal)
{
    int shadowIndex = ReadLocalPointShadowIndex(lightIndex);
    if (shadowIndex < 0 ||
        (pc.Push.MeshletDrawBufferBaseIndex == uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX) && ForwardTransparentReceiveShadows() == 0u))
        return 1.0;

    GPUPointShadow shadow = ReadPointShadow(uint(shadowIndex));
    if (shadow.Enabled == 0 || shadow.LightIndex != int(lightIndex))
        return 1.0;
    if (shadow.BiasStrengthTexelSize.z <= 0.0)
        return 1.0;

    vec3 lightPosition = shadow.PositionRange.xyz;
    vec3 toReceiver = worldPosition - lightPosition;
    float range = max(shadow.PositionRange.w, 0.001);
    if (length(toReceiver) > range)
        return 1.0;

    vec3 sampleDirection = normalize(toReceiver);
    uint faceIndex = SelectPointShadowFace(sampleDirection);
    vec3 biasedPosition = worldPosition + normal * shadow.BiasStrengthTexelSize.x;
    int radius = int(clamp(shadow.PcfRadius, 0, 2));
    vec2 texelSize = vec2(shadow.BiasStrengthTexelSize.w);
    vec2 faceUv;
    float visibility = SamplePointShadowFace(shadow, faceIndex, biasedPosition, radius, texelSize, faceUv);

    float seamWidth = max(float(radius + 2), 2.0) * texelSize.x;
    if (radius > 0 && PointShadowFaceEdgeDistance(faceUv) <= seamWidth)
    {
        for (uint adjacentFace = 0u; adjacentFace < 6u; adjacentFace++)
        {
            if (adjacentFace == faceIndex)
                continue;

            vec2 adjacentUv;
            visibility = min(visibility, SamplePointShadowFace(shadow, adjacentFace, biasedPosition, radius, texelSize, adjacentUv));
        }
    }

    return mix(1.0, visibility, shadow.BiasStrengthTexelSize.z);
}

vec3 ResolveNormal(GPUMaterialData material, vec3 interpolatedNormal, vec4 interpolatedTangent, vec2 uv)
{
    vec3 n = normalize(interpolatedNormal);
    float facingSign = gl_FrontFacing ? 1.0 : -1.0;
    n *= facingSign;
    vec3 t = normalize(interpolatedTangent.xyz - n * dot(n, interpolatedTangent.xyz));
    vec3 b = normalize(cross(n, t) * interpolatedTangent.w * facingSign);

    vec3 tangentNormal = SampleMaterialTexture(material.NormalTextureIndex, uv).xyz * 2.0 - 1.0;
    tangentNormal.xy *= material.NormalScaleBias.x;
    tangentNormal = normalize(tangentNormal);

    mat3 tbn = mat3(t, b, n);
    return normalize(tbn * tangentNormal);
}

float DistributionGGX(float nDotH, float roughness)
{
    float alpha = roughness * roughness;
    float alphaSq = alpha * alpha;
    float denom = nDotH * nDotH * (alphaSq - 1.0) + 1.0;
    return alphaSq / max(PI * denom * denom, 0.000001);
}

float GeometrySchlickGGX(float nDotV, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) * 0.125;
    return nDotV / max(nDotV * (1.0 - k) + k, 0.000001);
}

float GeometrySmith(float nDotV, float nDotL, float roughness)
{
    return GeometrySchlickGGX(nDotV, roughness) * GeometrySchlickGGX(nDotL, roughness);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness)
{
    return f0 + (max(vec3(1.0 - roughness), f0) - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 RotateEnvironmentDirection(vec3 direction, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return normalize(vec3(
        direction.x * c - direction.z * s,
        direction.y,
        direction.x * s + direction.z * c));
}

vec3 TransformProbePoint(GPUReflectionProbe probe, vec3 position)
{
    return MulRowMajor(vec4(position, 1.0), probe.WorldToProbe).xyz;
}

vec3 TransformProbeVector(GPUReflectionProbe probe, vec3 direction)
{
    return normalize(MulRowMajor(vec4(direction, 0.0), probe.WorldToProbe).xyz);
}

float SmoothProbeFade(float edge0, float edge1, float value)
{
    if (edge1 <= edge0)
        return value >= edge1 ? 1.0 : 0.0;

    float t = clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

float ProbeInfluenceWeight(GPUReflectionProbe probe, vec3 worldPosition)
{
    float blendDistance = max(probe.BlendParams.x, 0.0);
    if (probe.Shape == REFLECTION_PROBE_SHAPE_SPHERE)
    {
        float radius = max(probe.PositionAndRadius.w, 0.0001);
        float distanceToProbe = length(worldPosition - probe.PositionAndRadius.xyz);
        if (distanceToProbe >= radius)
            return 0.0;
        if (blendDistance <= 0.0)
            return 1.0;

        float innerRadius = max(radius - blendDistance, 0.0);
        return 1.0 - SmoothProbeFade(innerRadius, radius, distanceToProbe);
    }

    vec3 localPosition = TransformProbePoint(probe, worldPosition);
    vec3 boxExtents = max(abs(probe.BoxMax.xyz), vec3(0.0001));
    if (any(greaterThan(abs(localPosition), boxExtents)))
        return 0.0;
    if (blendDistance <= 0.0)
        return 1.0;

    float boundaryDistance = min(
        boxExtents.x - abs(localPosition.x),
        min(boxExtents.y - abs(localPosition.y), boxExtents.z - abs(localPosition.z)));
    return SmoothProbeFade(0.0, blendDistance, boundaryDistance);
}

float AxisBoxIntersection(float position, float direction, float extent)
{
    if (abs(direction) <= 0.00001)
        return 3.402823e38;

    float plane = direction > 0.0 ? extent : -extent;
    return (plane - position) / direction;
}

vec3 ProjectProbeDirection(GPUReflectionProbeHeader header, GPUReflectionProbe probe, vec3 worldPosition, vec3 reflectionDirection)
{
    vec3 localDirection = TransformProbeVector(probe, reflectionDirection);
    if ((header.Flags & REFLECTION_BOX_PROJECTION_ENABLED_FLAG) == 0u ||
        (probe.Flags & REFLECTION_PROBE_BOX_PROJECTION_FLAG) == 0 ||
        probe.Shape == REFLECTION_PROBE_SHAPE_SPHERE)
    {
        return localDirection;
    }

    vec3 localPosition = TransformProbePoint(probe, worldPosition);
    vec3 boxExtents = max(abs(probe.BoxMax.xyz), vec3(0.0001));
    if (any(greaterThan(abs(localPosition), boxExtents)))
        return localDirection;

    float tx = AxisBoxIntersection(localPosition.x, localDirection.x, boxExtents.x);
    float ty = AxisBoxIntersection(localPosition.y, localDirection.y, boxExtents.y);
    float tz = AxisBoxIntersection(localPosition.z, localDirection.z, boxExtents.z);
    float t = min(tx, min(ty, tz));
    if (t <= 0.0 || t >= 3.402823e37)
        return localDirection;

    return normalize(localPosition + localDirection * t);
}

vec3 ReflectionFaceColor(vec3 direction)
{
    vec3 absDirection = abs(direction);
    if (absDirection.x >= absDirection.y && absDirection.x >= absDirection.z)
        return direction.x >= 0.0 ? vec3(1.0, 0.15, 0.1) : vec3(0.1, 0.85, 0.95);
    if (absDirection.y >= absDirection.z)
        return direction.y >= 0.0 ? vec3(0.2, 0.9, 0.25) : vec3(0.85, 0.15, 0.95);
    return direction.z >= 0.0 ? vec3(0.2, 0.4, 1.0) : vec3(1.0, 0.85, 0.1);
}

vec3 EvaluateReflectionSpecular(
    GPUEnvironmentData environment,
    vec3 worldPosition,
    vec3 reflectionDirection,
    float lod,
    vec2 brdf,
    vec3 fresnel,
    float specularOcclusion,
    out bool debugActive,
    out vec3 debugColor)
{
    debugActive = false;
    debugColor = vec3(0.0);

    GPUReflectionProbeHeader header = ReadReflectionProbeHeader();
    bool reflectionsEnabled = (header.Flags & REFLECTION_ENABLED_FLAG) != 0u;
    if (!reflectionsEnabled)
        return vec3(0.0);

    vec3 globalDirection = RotateEnvironmentDirection(reflectionDirection, environment.RotationRadians);
    vec3 globalReflection = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.PrefilteredTextureIndex)],
        globalDirection,
        lod).rgb * header.GlobalFallbackIntensity;

    vec3 localReflection = vec3(0.0);
    vec3 firstWeightColor = vec3(0.0);
    vec3 projectedDirection = globalDirection;
    float totalWeight = 0.0;
    int acceptedProbeCount = 0;
    int selectedProbeIndex = -1;
    bool blendingEnabled = (header.Flags & REFLECTION_PROBE_BLENDING_ENABLED_FLAG) != 0u;
    int maxAcceptedProbes = max(header.MaxProbesPerPixel, 1);

    for (int probeIndex = 0; probeIndex < header.ProbeCount && acceptedProbeCount < maxAcceptedProbes; probeIndex++)
    {
        GPUReflectionProbe probe = ReadReflectionProbe(uint(probeIndex));
        float weight = ProbeInfluenceWeight(probe, worldPosition);
        if (weight <= 0.0001)
            continue;

        if (!blendingEnabled)
            weight = 1.0;

        vec3 probeDirection = ProjectProbeDirection(header, probe, worldPosition, reflectionDirection);
        vec3 probeColor = textureLod(
            BindlessCubeTextures[nonuniformEXT(header.ProbeCubemapArrayTextureIndex)],
            probeDirection,
            lod).rgb * max(probe.BlendParams.y, 0.0);

        if (acceptedProbeCount == 0)
        {
            selectedProbeIndex = probeIndex;
            projectedDirection = probeDirection;
        }
        if (acceptedProbeCount < 3)
            firstWeightColor[acceptedProbeCount] = weight;

        localReflection += probeColor * weight;
        totalWeight += weight;
        acceptedProbeCount++;

        if (!blendingEnabled)
            break;
    }

    float localWeight = clamp(totalWeight, 0.0, 1.0);
    if (totalWeight > 0.0001)
        localReflection /= totalWeight;

    vec3 reflectedRadiance = mix(globalReflection, localReflection, localWeight) * header.Intensity;
    vec3 specular = reflectedRadiance * (fresnel * brdf.x + brdf.y) * environment.SpecularIntensity * specularOcclusion;

    if (header.DebugView != 0u)
    {
        debugActive = true;
        if (header.DebugView == REFLECTION_DEBUG_PROBE_INFLUENCE)
            debugColor = vec3(localWeight);
        else if (header.DebugView == REFLECTION_DEBUG_PROBE_INDEX)
            debugColor = selectedProbeIndex >= 0 ? MeshletDebugColor(uint(selectedProbeIndex)) : vec3(0.0);
        else if (header.DebugView == REFLECTION_DEBUG_PROBE_BLEND_WEIGHTS)
            debugColor = clamp(firstWeightColor, vec3(0.0), vec3(1.0));
        else if (header.DebugView == REFLECTION_DEBUG_PROBE_CUBEMAP_FACE)
            debugColor = ReflectionFaceColor(projectedDirection);
        else if (header.DebugView == REFLECTION_DEBUG_PROBE_PREFILTER_MIP)
            debugColor = vec3(header.ProbeMipCount <= 1u ? 0.0 : clamp(lod / float(header.ProbeMipCount - 1u), 0.0, 1.0));
        else if (header.DebugView == REFLECTION_DEBUG_BOX_PROJECTION_DIRECTION)
            debugColor = projectedDirection * 0.5 + vec3(0.5);
        else if (header.DebugView == REFLECTION_DEBUG_LOCAL_REFLECTION_ONLY)
            debugColor = localReflection * header.Intensity;
        else if (header.DebugView == REFLECTION_DEBUG_GLOBAL_FALLBACK_ONLY)
            debugColor = globalReflection * header.Intensity;
        else
            debugColor = specular;
    }

    return specular;
}

vec3 EvaluateGlobalReflectionSpecular(
    GPUEnvironmentData environment,
    vec3 reflectionDirection,
    float lod,
    vec2 brdf,
    vec3 fresnel,
    float specularOcclusion)
{
    GPUReflectionProbeHeader header = ReadReflectionProbeHeader();
    bool reflectionsEnabled = (header.Flags & REFLECTION_ENABLED_FLAG) != 0u;
    if (!reflectionsEnabled)
        return vec3(0.0);

    vec3 globalDirection = RotateEnvironmentDirection(reflectionDirection, environment.RotationRadians);
    vec3 globalReflection = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.PrefilteredTextureIndex)],
        globalDirection,
        lod).rgb * header.GlobalFallbackIntensity * header.Intensity;

    return globalReflection * (fresnel * brdf.x + brdf.y) * environment.SpecularIntensity * specularOcclusion;
}

void EvaluateIbl(
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 viewDirection,
    float ambientOcclusion,
    out vec3 diffuseIbl,
    out vec3 specularIbl,
    out bool reflectionDebugActive,
    out vec3 reflectionDebugColor)
{
    diffuseIbl = vec3(0.0);
    specularIbl = vec3(0.0);
    reflectionDebugActive = false;
    reflectionDebugColor = vec3(0.0);

    GPUEnvironmentData environment = ReadEnvironmentData();
    if (environment.Enabled == 0u)
        return;

    vec3 f0 = mix(dielectricF0, albedo, metallic);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    vec3 fresnel = FresnelSchlickRoughness(nDotV, f0, roughness);
    vec3 diffuseWeight = (vec3(1.0) - fresnel) * (1.0 - metallic);

    vec3 irradianceDirection = RotateEnvironmentDirection(normal, environment.RotationRadians);
    vec3 irradiance = texture(BindlessCubeTextures[nonuniformEXT(environment.IrradianceTextureIndex)], irradianceDirection).rgb;
    diffuseIbl = diffuseWeight * albedo * irradiance * environment.DiffuseIntensity * ambientOcclusion;

    vec3 reflectionDirection = reflect(-viewDirection, normal);
    float maxLod = max(float(environment.PrefilteredMipCount) - 1.0, 0.0);
    float lod = roughness * maxLod;
    vec2 brdf = texture(BindlessTextures[nonuniformEXT(environment.BrdfLutTextureIndex)], vec2(nDotV, roughness)).rg;
    float specularOcclusion = clamp(pow(ambientOcclusion, 1.0 + roughness), 0.0, 1.0);
#if FORWARD_SIMPLE_OPAQUE
    specularIbl = EvaluateGlobalReflectionSpecular(
        environment,
        reflectionDirection,
        lod,
        brdf,
        fresnel,
        specularOcclusion);
#else
    specularIbl = EvaluateReflectionSpecular(
        environment,
        fragWorldPosition,
        reflectionDirection,
        lod,
        brdf,
        fresnel,
        specularOcclusion,
        reflectionDebugActive,
        reflectionDebugColor);
#endif
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance)
{
    vec3 halfVector = normalize(viewDirection + lightDirection);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    float nDotH = max(dot(normal, halfVector), 0.0);
    float hDotV = max(dot(halfVector, viewDirection), 0.0);

    if (nDotL <= 0.0 || nDotV <= 0.0)
        return vec3(0.0);

    vec3 f0 = mix(dielectricF0, albedo, metallic);
    vec3 fresnel = FresnelSchlick(hDotV, f0);
    float distribution = DistributionGGX(nDotH, roughness);
    float geometry = GeometrySmith(nDotV, nDotL, roughness);

    vec3 specular = (distribution * geometry * fresnel) / max(4.0 * nDotV * nDotL, 0.000001);
    vec3 diffuseWeight = (vec3(1.0) - fresnel) * (1.0 - metallic);
    vec3 diffuse = diffuseWeight * albedo / PI;

    return (diffuse + specular) * radiance * nDotL;
}

void AccumulateLight(
    uint lightIndex,
    vec3 albedo,
    float metallic,
    float roughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 shadowNormal,
    vec3 viewDirection,
    vec3 worldPosition,
    out float shadowFactor,
    out uint shadowCascade,
    inout vec3 directLighting)
{
    GPULight light = ReadLight(lightIndex);
    shadowFactor = 1.0;
    shadowCascade = 0u;

    vec3 lightDirection;
    float attenuation = 1.0;

    if (light.Type == 1)
    {
        lightDirection = normalize(-light.Direction);
        shadowFactor = EvaluateDirectionalShadow(lightIndex, worldPosition, shadowNormal, shadowCascade);
    }
    else
    {
        vec3 toLight = light.Position - worldPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.Range || light.Range <= 0.0)
            return;

        lightDirection = toLight / max(distanceToLight, 0.0001);
        float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
        attenuation = rangeFactor * rangeFactor;

        if (light.Type == 2)
        {
            float coneCos = cos(light.SpotAngle);
            float spotCos = dot(normalize(light.Direction), -lightDirection);
            float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
            attenuation *= spotFactor;
            shadowFactor = EvaluateSpotShadow(lightIndex, worldPosition, shadowNormal);
        }
        else
        {
            shadowFactor = EvaluatePointShadow(lightIndex, worldPosition, shadowNormal);
        }
    }

    vec3 radiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    directLighting += EvaluatePbrLight(
        albedo,
        metallic,
        roughness,
        dielectricF0,
        normal,
        viewDirection,
        lightDirection,
        radiance) * shadowFactor;
}

void WriteForwardColor(vec4 color)
{
#if FORWARD_WEIGHTED_OIT
    float alpha = clamp(color.a, 0.0, 1.0);
    if (alpha <= 0.001)
        discard;

    float depthWeight = clamp(pow(max(1.0 - gl_FragCoord.z * 0.95, 0.01), 3.0), 0.01, 1.0);
    float alphaWeight = max(alpha * 8.0 + 0.01, 0.01);
    float weight = clamp(alphaWeight * alphaWeight * alphaWeight * 64.0 * depthWeight, 0.01, 3000.0);
    vec3 premultipliedColor = max(color.rgb, vec3(0.0)) * alpha;
    outOitAccumulation = vec4(premultipliedColor * weight, alpha * weight);
    outOitRevealage = vec4(alpha);
#else
    outColor = color;
#endif
}

bool IsDdgiDebugView(uint view)
{
    return view >= GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE &&
           view <= GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT;
}

vec3 DdgiDebugCategoryColor(uint view)
{
    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
        return vec3(1.0, 0.55, 0.10);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)
        return vec3(0.10, 0.85, 1.0);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE)
        return vec3(0.25, 0.45, 1.0);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP)
        return vec3(0.10, 1.0, 0.25);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET)
        return vec3(1.0, 0.10, 0.85);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION)
        return vec3(0.85, 0.85, 0.10);

    return vec3(1.0, 1.0, 1.0);
}

vec3 ApplyDdgiDebugIdentity(vec3 color, uint view)
{
    if (!IsDdgiDebugView(view))
        return color;

    vec2 p = gl_FragCoord.xy;
    vec2 screen = max(pc.Push.ScreenDimensions, vec2(1.0));
    vec3 category = DdgiDebugCategoryColor(view);

    bool border =
        p.x < 4.0 || p.y < 4.0 ||
        p.x >= screen.x - 4.0 ||
        p.y >= screen.y - 4.0;
    if (border)
        color = category;

    bool badge = p.x < 96.0 && p.y < 32.0;
    if (badge)
    {
        float checker = mod(floor(p.x / 8.0) + floor(p.y / 8.0), 2.0);
        color = mix(category * 0.35, category, checker);

        for (uint bit = 0u; bit < 6u; bit++)
        {
            float x0 = 8.0 + float(bit) * 12.0;
            bool inBar = p.x >= x0 && p.x < x0 + 8.0 && p.y >= 20.0 && p.y < 28.0;
            if (inBar)
            {
                bool one = ((view >> bit) & 1u) != 0u;
                color = one ? vec3(1.0) : vec3(0.0);
            }
        }
    }

    bool legend = p.x < 96.0 && p.y >= screen.y - 12.0;
    if (legend)
    {
        if (p.x < 32.0)
            color = vec3(1.0, 0.0, 0.0);
        else if (p.x < 64.0)
            color = vec3(0.0, 1.0, 0.0);
        else
            color = vec3(0.0, 0.0, 1.0);
    }

    return color;
}

void WriteDdgiDebugColor(uint view, vec3 color)
{
    WriteForwardColor(vec4(ApplyDdgiDebugIdentity(color, view), 1.0));
}

void WriteSsgiTraceSource(vec4 color)
{
#if !FORWARD_WEIGHTED_OIT && FORWARD_SSGI_TRACE_SOURCE_OUTPUT
    outSsgiTraceSource = color;
#endif
}

void main()
{
    uint debugViewMode = ForwardDebugViewMode();
    uint ambientOcclusionDebugView = ForwardAmbientOcclusionDebugView();
    WriteSsgiTraceSource(vec4(0.0, 0.0, 0.0, 1.0));
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    if (IsAnimationDebugView(debugViewMode))
    {
        GPUObjectData objectData = ReadInstanceData(pc.Push.CurrentFrameIndex, fragObjectIndex);
        if (objectData.SkinningEnabled != 0)
        {
            vec3 skinnedColor = debugViewMode == ANIMATION_DEBUG_SKINNED_OBJECTS
                ? vec3(1.0, 0.0, 0.85)
                : MeshletDebugColor(fragMeshletIndex);
            WriteForwardColor(vec4(skinnedColor, 1.0));
            return;
        }

        discard;
    }

#if FORWARD_SIMPLE_OPAQUE
    bool hasMaterialExtension = false;
#else
    bool hasMaterialExtension = material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
#endif
    GPUMaterialExtensionData materialExtension;
    if (hasMaterialExtension)
        materialExtension = ReadMaterialExtension(uint(material.ExtensionDataIndex));
    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);

    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    float alphaMode = material.NormalScaleBias.y;
    float alphaCutoff = material.NormalScaleBias.z;
    float outputAlpha = material.Albedo.a * albedoSample.a * fragVertexColor.a;

    if (alphaMode > 0.5 && alphaMode < 1.5 && outputAlpha <= alphaCutoff)
        discard;
    if (alphaMode > 1.5 && outputAlpha <= 0.001)
        discard;

    if (debugViewMode == DEBUG_VIEW_MESHLETS)
    {
        WriteForwardColor(vec4(MeshletDebugColor(fragMeshletIndex), 1.0));
        return;
    }

    uint transparencyDebugView = ForwardTransparencyDebugView();
    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_MODE)
    {
        vec3 modeColor = alphaMode < 0.5 ? vec3(0.1, 0.8, 0.2) :
            alphaMode < 1.5 ? vec3(0.95, 0.85, 0.1) :
            vec3(0.2, 0.55, 1.0);
        WriteForwardColor(vec4(modeColor, 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_VALUE)
    {
        WriteForwardColor(vec4(vec3(outputAlpha), 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_ALPHA_CUTOFF)
    {
        WriteForwardColor(vec4(vec3(alphaCutoff), 1.0));
        return;
    }

    if (transparencyDebugView == TRANSPARENCY_DEBUG_SORT_ORDER)
    {
        WriteForwardColor(vec4(MeshletDebugColor(fragMeshletIndex), alphaMode > 1.5 ? max(outputAlpha, 0.25) : 1.0));
        return;
    }

    vec3 geometricNormal = normalize(fragNormal) * (gl_FrontFacing ? 1.0 : -1.0);
    vec3 shadowNormal = geometricNormal;
    bool useNormalTexture = material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
        material.NormalScaleBias.x > 0.001;
    vec3 normal = useNormalTexture
        ? ResolveNormal(
            material,
            fragNormal,
            fragWorldTangent,
            MaterialUv(
                material.TextureTexCoordSets.y,
                material.NormalOffsetScale,
                material.TextureRotations.y))
        : geometricNormal;
    vec3 ddgiNormal = geometricNormal;
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // glTF metallic-roughness contract: G = roughness, B = metallic.
    // R is occlusion only when the material upload marks this as a shared ORM texture.
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.z,
                material.MetallicRoughnessOffsetScale,
                material.TextureRotations.z));
    bool useEmissiveTexture = material.EmissiveTextureIndex != DEFAULT_BLACK_TEXTURE &&
        MaxComponent(material.Emissive.rgb) > 0.000001;
    vec4 emissiveSample = useEmissiveTexture
        ? SampleMaterialTexture(
            material.EmissiveTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.w,
                material.EmissiveOffsetScale,
                material.TextureRotations.w))
        : vec4(0.0);

    float roughness = clamp(material.MetallicRoughnessAO.y * armSample.g, 0.04, 1.0);
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    float sampledOcclusion = material.MetallicRoughnessAO.w > 0.5 ? armSample.r : 1.0;
    float ambientOcclusion = clamp(material.MetallicRoughnessAO.z * sampledOcclusion, 0.0, 1.0);
    float screenSpaceAo = SampleScreenSpaceAo();
    float indirectAo = clamp(ambientOcclusion * screenSpaceAo, 0.0, 1.0);
    float ddgiIndirectAo = ambientOcclusion;
    vec3 albedo = max(material.Albedo.rgb * albedoSample.rgb * fragVertexColor.rgb, vec3(0.0));
    vec3 emissive = max(material.Emissive.rgb * emissiveSample.rgb, vec3(0.0));

    float clearcoatFactor = 0.0;
    float clearcoatRoughness = 0.04;
    vec3 sheenColor = vec3(0.0);
    float sheenRoughness = 0.0;
    float anisotropyStrength = 0.0;
    float transmissionFactor = 0.0;
    float ior = 1.5;
    float transmissionThickness = 0.0;
    float attenuationDistance = 0.0;
    vec3 attenuationColor = vec3(1.0);
    vec3 subsurfaceColor = vec3(1.0);
    float subsurfaceStrength = 0.0;
    float specularFactor = 1.0;
    vec3 specularColor = vec3(1.0);
    float iridescenceFactor = 0.0;
    float iridescenceThickness = 0.0;
    float dispersion = 0.0;

    if (hasMaterialExtension)
    {
        if ((material.FeatureFlags & MATERIAL_FEATURE_EMISSIVE_STRENGTH) != 0u)
            emissive *= materialExtension.Clearcoat.w;

        if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT) != 0u)
        {
            clearcoatFactor = clamp(materialExtension.Clearcoat.x, 0.0, 1.0);
            clearcoatRoughness = clamp(materialExtension.Clearcoat.y, 0.04, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT_TEXTURE) != 0u)
                clearcoatFactor *= SampleMaterialTexture(materialExtension.ClearcoatTextureIndex, ExtensionUv(materialExtension.ClearcoatOffsetScale, materialExtension.ExtensionTextureRotations0.x, materialExtension.ExtensionTextureTexCoordSets0.x)).r;
            if ((material.FeatureFlags & MATERIAL_FEATURE_CLEARCOAT_ROUGHNESS_TEXTURE) != 0u)
                clearcoatRoughness = clamp(clearcoatRoughness * SampleMaterialTexture(materialExtension.ClearcoatRoughnessTextureIndex, ExtensionUv(materialExtension.ClearcoatRoughnessOffsetScale, materialExtension.ExtensionTextureRotations0.y, materialExtension.ExtensionTextureTexCoordSets0.y)).g, 0.04, 1.0);
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN) != 0u)
        {
            sheenColor = max(materialExtension.SheenColor.rgb, vec3(0.0));
            sheenRoughness = clamp(materialExtension.SheenColor.a, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN_COLOR_TEXTURE) != 0u)
                sheenColor *= SampleMaterialTexture(materialExtension.SheenColorTextureIndex, ExtensionUv(materialExtension.SheenColorOffsetScale, materialExtension.ExtensionTextureRotations0.w, materialExtension.ExtensionTextureTexCoordSets0.w)).rgb;
            if ((material.FeatureFlags & MATERIAL_FEATURE_SHEEN_ROUGHNESS_TEXTURE) != 0u)
                sheenRoughness = clamp(sheenRoughness * SampleMaterialTexture(materialExtension.SheenRoughnessTextureIndex, ExtensionUv(materialExtension.SheenRoughnessOffsetScale, materialExtension.ExtensionTextureRotations1.x, materialExtension.ExtensionTextureTexCoordSets1.x)).a, 0.0, 1.0);
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_ANISOTROPY) != 0u)
        {
            anisotropyStrength = clamp(materialExtension.Anisotropy.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_ANISOTROPY_TEXTURE) != 0u)
                anisotropyStrength *= SampleMaterialTexture(materialExtension.AnisotropyTextureIndex, ExtensionUv(materialExtension.AnisotropyOffsetScale, materialExtension.ExtensionTextureRotations1.y, materialExtension.ExtensionTextureTexCoordSets1.y)).b;
            roughness = clamp(mix(roughness, roughness * 0.65, anisotropyStrength), 0.04, 1.0);
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u)
        {
            transmissionFactor = clamp(materialExtension.Transmission.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION_TEXTURE) != 0u)
                transmissionFactor *= SampleMaterialTexture(materialExtension.TransmissionTextureIndex, ExtensionUv(materialExtension.TransmissionOffsetScale, materialExtension.ExtensionTextureRotations1.z, materialExtension.ExtensionTextureTexCoordSets1.z)).r;
            ior = clamp(materialExtension.Transmission.y, 1.0, 3.0);
            transmissionThickness = max(materialExtension.Transmission.z, 0.0);
            attenuationDistance = max(materialExtension.Transmission.w, 0.0);
            attenuationColor = max(materialExtension.AttenuationColor.rgb, vec3(0.0));
            if ((material.FeatureFlags & MATERIAL_FEATURE_VOLUME_APPROXIMATION) != 0u)
                transmissionThickness *= SampleMaterialTexture(materialExtension.ThicknessTextureIndex, ExtensionUv(materialExtension.ThicknessOffsetScale, materialExtension.ExtensionTextureRotations1.w, materialExtension.ExtensionTextureTexCoordSets1.w)).g;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE) != 0u)
        {
            subsurfaceColor = max(materialExtension.Subsurface.rgb, vec3(0.0));
            subsurfaceStrength = clamp(materialExtension.Subsurface.a, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE_TEXTURE) != 0u)
                subsurfaceColor *= SampleMaterialTexture(materialExtension.SubsurfaceTextureIndex, ExtensionUv(materialExtension.SubsurfaceOffsetScale, materialExtension.ExtensionTextureRotations3.x, materialExtension.ExtensionTextureTexCoordSets3.x)).rgb;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR) != 0u)
        {
            specularFactor = clamp(materialExtension.SpecularColor.a, 0.0, 1.0);
            specularColor = max(materialExtension.SpecularColor.rgb, vec3(0.0));
            if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_TEXTURE) != 0u)
                specularFactor *= SampleMaterialTexture(materialExtension.SpecularTextureIndex, ExtensionUv(materialExtension.SpecularOffsetScale, materialExtension.ExtensionTextureRotations2.x, materialExtension.ExtensionTextureTexCoordSets2.x)).a;
            if ((material.FeatureFlags & MATERIAL_FEATURE_SPECULAR_COLOR_TEXTURE) != 0u)
                specularColor *= SampleMaterialTexture(materialExtension.SpecularColorTextureIndex, ExtensionUv(materialExtension.SpecularColorOffsetScale, materialExtension.ExtensionTextureRotations2.y, materialExtension.ExtensionTextureTexCoordSets2.y)).rgb;
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE) != 0u)
        {
            iridescenceFactor = clamp(materialExtension.Iridescence.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE_TEXTURE) != 0u)
                iridescenceFactor *= SampleMaterialTexture(materialExtension.IridescenceTextureIndex, ExtensionUv(materialExtension.IridescenceOffsetScale, materialExtension.ExtensionTextureRotations2.z, materialExtension.ExtensionTextureTexCoordSets2.z)).r;
            float minThickness = min(materialExtension.Iridescence.z, materialExtension.Iridescence.w);
            float maxThickness = max(materialExtension.Iridescence.z, materialExtension.Iridescence.w);
            float thicknessSample = (material.FeatureFlags & MATERIAL_FEATURE_IRIDESCENCE_THICKNESS_TEXTURE) != 0u
                ? SampleMaterialTexture(materialExtension.IridescenceThicknessTextureIndex, ExtensionUv(materialExtension.IridescenceThicknessOffsetScale, materialExtension.ExtensionTextureRotations2.w, materialExtension.ExtensionTextureTexCoordSets2.w)).g
                : 1.0;
            iridescenceThickness = mix(minThickness, maxThickness, clamp(thicknessSample, 0.0, 1.0));
        }

        if ((material.FeatureFlags & MATERIAL_FEATURE_DISPERSION) != 0u)
        {
            dispersion = clamp(materialExtension.Dispersion.x, 0.0, 1.0);
        }
    }

    if (IsMaterialDebugView(debugViewMode))
    {
        if (debugViewMode == MATERIAL_DEBUG_FEATURE_FLAGS)
        {
            WriteForwardColor(vec4(MaterialFeatureFlagsDebugColor(material.FeatureFlags), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_BASE_COLOR)
        {
            WriteForwardColor(vec4(albedo, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_METALLIC)
        {
            WriteForwardColor(vec4(vec3(metallic), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(roughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_NORMAL_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(clamp(material.NormalScaleBias.x, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_WORLD_NORMAL)
        {
            WriteForwardColor(vec4(normal * 0.5 + vec3(0.5), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_EMISSIVE_INTENSITY)
        {
            float emissiveIntensity = clamp(log2(1.0 + MaxComponent(emissive)) / 6.0, 0.0, 1.0);
            WriteForwardColor(vec4(vec3(emissiveIntensity), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CLEARCOAT_FACTOR)
        {
            WriteForwardColor(vec4(vec3(clearcoatFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CLEARCOAT_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(clearcoatRoughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHEEN_COLOR)
        {
            WriteForwardColor(vec4(sheenColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHEEN_ROUGHNESS)
        {
            WriteForwardColor(vec4(vec3(sheenRoughness), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ANISOTROPY_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(anisotropyStrength), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ANISOTROPY_DIRECTION)
        {
            float anisotropyRotation = hasMaterialExtension ? materialExtension.Anisotropy.y : 0.0;
            vec2 direction = vec2(cos(anisotropyRotation), sin(anisotropyRotation)) * anisotropyStrength;
            WriteForwardColor(vec4(direction * 0.5 + vec2(0.5), anisotropyStrength, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_TRANSMISSION)
        {
            WriteForwardColor(vec4(vec3(transmissionFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IOR)
        {
            WriteForwardColor(vec4(vec3(clamp((ior - 1.0) * 0.5, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_VOLUME_THICKNESS)
        {
            WriteForwardColor(vec4(vec3(clamp(transmissionThickness, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_ATTENUATION_COLOR)
        {
            WriteForwardColor(vec4(attenuationColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SUBSURFACE_STRENGTH)
        {
            WriteForwardColor(vec4(vec3(subsurfaceStrength), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SPECULAR_FACTOR)
        {
            WriteForwardColor(vec4(vec3(specularFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SPECULAR_COLOR)
        {
            WriteForwardColor(vec4(specularColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IRIDESCENCE_FACTOR)
        {
            WriteForwardColor(vec4(vec3(iridescenceFactor), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_IRIDESCENCE_THICKNESS)
        {
            WriteForwardColor(vec4(vec3(clamp(iridescenceThickness / 1200.0, 0.0, 1.0)), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_DISPERSION)
        {
            WriteForwardColor(vec4(vec3(dispersion), 1.0));
            return;
        }
    }

    vec3 diffuseIbl = vec3(0.0);
    vec3 specularIbl = vec3(0.0);
    bool reflectionDebugActive = false;
    vec3 reflectionDebugColor = vec3(0.0);
    float dielectricF0Scalar = pow((ior - 1.0) / max(ior + 1.0, 0.0001), 2.0);
    vec3 dielectricF0 = clamp(vec3(dielectricF0Scalar) * specularColor * specularFactor, vec3(0.0), vec3(1.0));

    EvaluateIbl(
        albedo,
        metallic,
        roughness,
        dielectricF0,
        normal,
        viewDirection,
        indirectAo,
        diffuseIbl,
        specularIbl,
        reflectionDebugActive,
        reflectionDebugColor);
    GPUEnvironmentData environment = ReadEnvironmentData();
    vec3 directLighting = vec3(0.0);
    float lastShadowFactor = 1.0;
    uint lastShadowCascade = 0u;

    if (environment.DebugView == ENVIRONMENT_DEBUG_AMBIENT_OCCLUSION)
    {
        WriteForwardColor(vec4(vec3(indirectAo), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_FINAL)
    {
        WriteForwardColor(vec4(vec3(indirectAo), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_RECONSTRUCTED_NORMAL)
    {
        vec2 uv = gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0));
        WriteForwardColor(vec4(ReconstructNormalFromDepth(uv) * 0.5 + vec3(0.5), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_LINEAR_DEPTH)
    {
        ivec2 depthSize = textureSize(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0);
        ivec2 pixel = ivec2(clamp(gl_FragCoord.xy, vec2(0.0), vec2(depthSize - ivec2(1))));
        vec2 screenUv = (vec2(pixel) + vec2(0.5)) / vec2(depthSize);
        float depth = FetchDepthAtPixel(pixel, depthSize);
        vec3 viewPosition = ReconstructViewPositionFromDepth(screenUv, depth);
        vec3 farPosition = ReconstructViewPositionFromDepth(vec2(0.5), 0.0);
        float farDepth = max(abs(farPosition.z), 0.0001);
        float linearDepth = clamp(abs(viewPosition.z) / farDepth, 0.0, 1.0);
        float visibleDepth = sqrt(linearDepth);
        WriteForwardColor(vec4(vec3(visibleDepth), 1.0));
        return;
    }

    if (reflectionDebugActive)
    {
        WriteForwardColor(vec4(reflectionDebugColor, 1.0));
        return;
    }

    if (environment.DebugView == ENVIRONMENT_DEBUG_DIFFUSE_IBL_ONLY)
    {
        WriteForwardColor(vec4(diffuseIbl, 1.0));
        return;
    }

    if (environment.DebugView == ENVIRONMENT_DEBUG_SPECULAR_IBL_ONLY)
    {
        WriteForwardColor(vec4(specularIbl, 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_MAP_PREVIEW)
    {
        vec2 previewUv = gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(DIRECTIONAL_SHADOW_TEXTURE_BASE)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SPOT_ATLAS_PREVIEW)
    {
        vec2 previewUv = gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    for (uint i = 0u; i < pc.Push.LightCount; i++)
    {
        GPULight light = ReadLight(i);
        if (light.Type != 1)
            continue;

        AccumulateLight(
            i,
            albedo,
            metallic,
            roughness,
            dielectricF0,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            lastShadowFactor,
            lastShadowCascade,
            directLighting);
    }

    if (pc.Push.LocalLightCount == 0u)
    {
        // Directional lights were handled above; there are no tiled local lights.
    }
    else
    {
        vec2 safeScreenSize = max(pc.Push.ScreenDimensions, vec2(1.0));
        uvec2 pixel = uvec2(clamp(gl_FragCoord.xy, vec2(0.0), safeScreenSize - vec2(1.0)));
        uvec2 tile = pixel / uvec2(16u, 16u);
        uint tileCountX = uint(ceil(safeScreenSize.x / 16.0));
        uint tileIndex = tile.y * tileCountX + tile.x;
        GPUTiledLightHeader tileHeader = ReadTiledLightHeader(tileIndex);

        for (uint i = 0u; i < tileHeader.LightCount; i++)
        {
            AccumulateLight(
                ReadTiledLightIndex(tileHeader.LightOffset + i),
                albedo,
                metallic,
                roughness,
                dielectricF0,
                normal,
                shadowNormal,
                viewDirection,
                fragWorldPosition,
                lastShadowFactor,
                lastShadowCascade,
                directLighting);
        }
    }

    // SSGI traces canonical direct lighting, never the visible debug output.
    WriteSsgiTraceSource(vec4(clamp(directLighting + emissive, vec3(0.0), vec3(64.0)), 1.0));

    if (debugViewMode == DEBUG_VIEW_SHADOW_RECEIVER_FACTOR)
    {
        WriteForwardColor(vec4(vec3(lastShadowFactor), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_CASCADE_OVERLAY)
    {
        vec3 cascadeColor = lastShadowCascade == 0u ? vec3(0.9, 0.15, 0.1) :
            lastShadowCascade == 1u ? vec3(0.1, 0.75, 0.2) :
            lastShadowCascade == 2u ? vec3(0.1, 0.35, 0.95) :
            vec3(0.9, 0.8, 0.1);
        directLighting = mix(directLighting, cascadeColor, 0.35);
    }

    DdgiSampleResult ddgiSample = SampleDdgiIrradiance(fragWorldPosition, ddgiNormal, ddgiIndirectAo);
    vec3 ddgiDiffuse = SampleDdgiDiffuse(ddgiSample, albedo, metallic);
    float ddgiEnvironmentFallbackIntensity = clamp(ReadStorageFloat(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 13u), 0.0, 4.0);
    HybridDiffuseGiResult hybridDiffuse = ComposeHybridDiffuseGi(diffuseIbl, ddgiDiffuse, ddgiSample, indirectAo, ddgiEnvironmentFallbackIntensity, debugViewMode);
    AccumulateDdgiForwardEstimateDiagnostics(hybridDiffuse, ddgiSample, ddgiDiffuse);
    float ddgiCoverage = hybridDiffuse.ddgiCoverage;
    float fallbackWeight = hybridDiffuse.environmentFallbackWeight;
    float nearContactSuppression = hybridDiffuse.nearContactSuppression;
    vec3 finalDiffuseIndirect = hybridDiffuse.diffuse;
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)
    {
        WriteForwardColor(vec4(finalDiffuseIndirect, 1.0));
        return;
    }

    vec2 giDebugUv = clamp(gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0)), vec2(0.0), vec2(1.0));
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_SSGI_RAW)
    {
        vec4 ssgiRaw = texture(BindlessTextures[nonuniformEXT(SSGI_RAW_TEXTURE_INDEX)], giDebugUv);
        WriteForwardColor(vec4(clamp(ssgiRaw.rgb, vec3(0.0), vec3(16.0)), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_SSGI_FILTERED)
    {
        vec4 ssgiFiltered = texture(BindlessTextures[nonuniformEXT(SSGI_FILTERED_TEXTURE_INDEX)], giDebugUv);
        WriteForwardColor(vec4(clamp(ssgiFiltered.rgb, vec3(0.0), vec3(16.0)), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_SSGI_HISTORY)
    {
        vec4 ssgiHistory = texture(BindlessTextures[nonuniformEXT(SSGI_HISTORY_TEXTURE_INDEX)], giDebugUv);
        WriteForwardColor(vec4(clamp(ssgiHistory.rgb, vec3(0.0), vec3(16.0)), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_SSGI_RAY_HIT_MASK)
    {
        float hitMask = texture(BindlessTextures[nonuniformEXT(SSGI_RAW_TEXTURE_INDEX)], giDebugUv).a;
        WriteForwardColor(vec4(vec3(clamp(hitMask, 0.0, 1.0)), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_SSGI_HISTORY_REJECTION)
    {
        vec4 rejection = texture(BindlessTextures[nonuniformEXT(SSGI_FILTERED_TEXTURE_INDEX)], giDebugUv);
        WriteForwardColor(vec4(clamp(rejection.rgb, vec3(0.0), vec3(1.0)), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE, ddgiSample.irradiance);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE, clamp(ddgiDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK, clamp(hybridDiffuse.suppressionMask, vec3(0.0), vec3(1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT, vec3(clamp(hybridDiffuse.effectiveDdgiWeight, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE, vec3(clamp(ddgiSample.spatialCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE, vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE, vec3(clamp(ddgiSample.weight, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE, vec3(clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN, vec3(
            clamp(ddgiSample.irradianceAtlasConfidence, 0.0, 1.0),
            clamp(ddgiSample.qualityConfidence, 0.0, 1.0),
            clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT, vec3(clamp(hybridDiffuse.environmentFallbackWeight / 4.0, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY, vec3(ddgiSample.visibility));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS)
    {
        float visibilityMaxDistance = max(ddgiSample.visibilityMaxRayDistance, 0.0001);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_MOMENTS, vec3(
            clamp(ddgiSample.visibilityMomentMean / visibilityMaxDistance, 0.0, 1.0),
            clamp(sqrt(max(ddgiSample.visibilityMomentVariance, 0.0)) / visibilityMaxDistance, 0.0, 1.0),
            clamp(ddgiSample.visibilityProbeDistance / visibilityMaxDistance, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_INDEX, MeshletDebugColor(ddgiSample.probeIndex));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE, vec3(ddgiSample.activeProbe, ddgiSample.supportCoverage, ddgiSample.weight));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION, abs(ddgiSample.relocation));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED)
    {
        float relocationAmount = length(ddgiSample.relocation) / max(ddgiSample.minProbeSpacing * 0.4, 0.001);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RELOCATION_NORMALIZED, vec3(clamp(relocationAmount, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_LOGICAL_POSITION, fract(abs(ddgiSample.logicalProbePosition) * 0.05));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATED_POSITION, fract(abs(ddgiSample.relocatedProbePosition) * 0.05));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION)
    {
        float relocationLength = length(ddgiSample.relocation);
        vec3 relocationDirection = relocationLength > 0.000001
            ? normalize(ddgiSample.relocation) * 0.5 + vec3(0.5)
            : vec3(0.5);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION, relocationDirection);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CLASSIFICATION_INVALID_SCORE, vec3(clamp(ddgiSample.classificationInvalidScore, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_LEAK_CLAMP, vec3(clamp(ddgiSample.leakClamp * (1.0 - nearContactSuppression), 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE, vec3(clamp(ddgiSample.spatialCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION, MeshletDebugColor(uint(max(ddgiSample.cascadeIndex, 0.0)) + 1u));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_BLEND_WEIGHT, vec3(clamp(ddgiSample.cascadeBlendWeight, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS, MeshletDebugColor(uint(clamp(ddgiSample.updateReason * 255.0, 0.0, 255.0))));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAY_BUDGET, vec3(ddgiSample.rayBudget, ddgiSample.supportCoverage, ddgiSample.weight));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT)
    {
        DdgiGatherTileInfo tile;
        if (!ReadDdgiGatherTile(tile))
            WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT, vec3(1.0, 0.0, 1.0));
        else
            WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT, vec3(clamp(tile.blendWeights.y, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK)
    {
        DdgiGatherTileInfo gatherTile;
        bool gatherValid = ReadDdgiGatherTile(gatherTile);
        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME)
        {
            bool hasLocal = gatherValid && (gatherTile.flags & DDGI_GATHER_TILE_LOCAL_VOLUME_VALID_FLAG) != 0u;
            WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME, hasLocal ? MeshletDebugColor(gatherTile.localVolumeIndex + 1u) : vec3(0.0));
            return;
        }

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP)
        {
            bool hasClipmap = gatherValid && (gatherTile.flags & DDGI_GATHER_TILE_PRIMARY_CLIPMAP_VALID_FLAG) != 0u;
            WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP, hasClipmap ? MeshletDebugColor(gatherTile.primaryClipmapVolumeIndex + 1u) : vec3(0.0));
            return;
        }

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT)
        {
            float blendWeight = gatherValid ? clamp(gatherTile.blendWeights.z, 0.0, 1.0) : 0.0;
            WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT, vec3(blendWeight));
            return;
        }

        float fallback = (!gatherValid || (gatherTile.flags & DDGI_GATHER_TILE_FALLBACK_FLAG) != 0u) ? 1.0 : 0.0;
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK, vec3(fallback, 1.0 - fallback, 0.0));
        return;
    }

    vec3 color = finalDiffuseIndirect + specularIbl + directLighting + emissive;

    if (hasMaterialExtension)
    {
        float nDotV = max(dot(normal, viewDirection), 0.0);
        GPUEnvironmentData extensionEnvironment = environment;
        if (clearcoatFactor > 0.0 && extensionEnvironment.Enabled != 0u)
        {
            vec3 clearcoatReflection = reflect(-viewDirection, normal);
            clearcoatReflection = RotateEnvironmentDirection(clearcoatReflection, extensionEnvironment.RotationRadians);
            float clearcoatMaxLod = max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 clearcoatPrefiltered = textureLod(
                BindlessCubeTextures[nonuniformEXT(extensionEnvironment.PrefilteredTextureIndex)],
                clearcoatReflection,
                clearcoatRoughness * clearcoatMaxLod).rgb;
            vec3 clearcoatFresnel = FresnelSchlickRoughness(nDotV, vec3(0.04), clearcoatRoughness);
            color += clearcoatPrefiltered * clearcoatFresnel * clearcoatFactor * extensionEnvironment.SpecularIntensity * indirectAo;
        }

        if (dot(sheenColor, vec3(1.0)) > 0.0)
        {
            float sheenPower = mix(4.0, 1.25, sheenRoughness);
            float sheenRim = pow(clamp(1.0 - nDotV, 0.0, 1.0), sheenPower);
            color += sheenColor * sheenRim * (1.0 - metallic) * indirectAo;
        }

        if (subsurfaceStrength > 0.0 && metallic < 0.5)
        {
            float wrap = clamp(dot(normal, viewDirection) * 0.5 + 0.5, 0.0, 1.0);
            color += albedo * subsurfaceColor * subsurfaceStrength * wrap * indirectAo * 0.35;
        }

        if (iridescenceFactor > 0.0 && metallic < 0.5)
        {
            float nDotVFilm = clamp(dot(normal, viewDirection), 0.0, 1.0);
            float phase = iridescenceThickness * 0.018 + (1.0 - nDotVFilm) * 6.2831853;
            vec3 filmTint = 0.5 + 0.5 * cos(vec3(phase, phase + 2.0943951, phase + 4.1887902));
            float filmFresnel = pow(1.0 - nDotVFilm, 3.0);
            color += filmTint * filmFresnel * iridescenceFactor * specularFactor * indirectAo;
        }

        if (transmissionFactor > 0.0 && extensionEnvironment.Enabled != 0u)
        {
            vec3 transmittedDirection = RotateEnvironmentDirection(-normal, extensionEnvironment.RotationRadians);
            float lod = roughness * max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 transmittedSample = textureLod(
                BindlessCubeTextures[nonuniformEXT(extensionEnvironment.PrefilteredTextureIndex)],
                transmittedDirection,
                lod).rgb;
            if (dispersion > 0.0)
            {
                vec3 tangent = normalize(fragWorldTangent.xyz);
                vec3 redDirection = normalize(transmittedDirection + tangent * dispersion * 0.012);
                vec3 blueDirection = normalize(transmittedDirection - tangent * dispersion * 0.012);
                transmittedSample.r = textureLod(BindlessCubeTextures[nonuniformEXT(extensionEnvironment.PrefilteredTextureIndex)], redDirection, lod).r;
                transmittedSample.b = textureLod(BindlessCubeTextures[nonuniformEXT(extensionEnvironment.PrefilteredTextureIndex)], blueDirection, lod).b;
            }

            vec3 transmitted = transmittedSample * albedo;
            if (attenuationDistance > 0.0 && transmissionThickness > 0.0)
            {
                float attenuationAmount = clamp(transmissionThickness / attenuationDistance, 0.0, 32.0);
                transmitted *= pow(max(attenuationColor, vec3(0.0001)), vec3(attenuationAmount));
            }

            float fresnelKeep = FresnelSchlick(nDotV, dielectricF0).x;
            color = mix(color, transmitted + specularIbl * fresnelKeep, transmissionFactor * (1.0 - fresnelKeep));
            outputAlpha = min(outputAlpha, mix(1.0, 0.35, transmissionFactor));
        }
    }

    WriteForwardColor(vec4(color, alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha));
}
