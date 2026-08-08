#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

// Every forward variant consumes the current frame's depth prepass and keeps
// depth writes disabled.  Make that contract explicit so helper functions with
// diagnostic atomics cannot force late depth testing and shade hidden Sponza
// layers before the depth comparison rejects them.
layout(early_fragment_tests) in;

#ifndef NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT 0
#endif

#include "common.glsl"
#if FORWARD_DDGI_RECEIVER_CACHE
#include "forward_ddgi_receiver_cache.glsl"
#endif
#include "gi_material_transport.glsl"
#include "material_coverage.glsl"
// Detailed captures need representative gather counts, not one globally
// contended atomic per shaded fragment.  Preserve an estimated full-resolution
// count while sampling one pixel from each 16x16 screen tile.
#define SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT ((((uint(gl_FragCoord.x) & 15u) == 0u) && ((uint(gl_FragCoord.y) & 15u) == 0u)) ? 256u : 0u)
#if defined(FORWARD_WEIGHTED_OIT)
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 1u) == 0u) && ((uint(gl_FragCoord.y) & 1u) == 0u))
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#elif defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE)
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 7u) == 0u) && ((uint(gl_FragCoord.y) & 7u) == 0u))
// Current opaque depth owns proactive resident-page retention. Opaque forward
// contributes only compact-publication misses; avoiding resident-touch atomics
// here keeps the authoritative gather path read-only in the common case.
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 0u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 1
#else
// The generic forward artifact is the sorted-transparent pipeline.
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 1u) == 0u) && ((uint(gl_FragCoord.y) & 1u) == 0u))
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#endif
#include "ddgi_simple_shared.glsl"
#undef SIMPLE_DDGI_OPAQUE_GATHER_ORACLE
#undef SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET
#undef SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT
#undef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE
#undef SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT
#include "farfield_clipmap.glsl"

#ifndef FORWARD_SIMPLE_VERTEX_INPUT
#define FORWARD_SIMPLE_VERTEX_INPUT 0
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
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
layout(location = 1) out float outMaterialTransportProvenance;
#endif
#endif

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

const float PI = 3.14159265359;
// R8_UNORM material-transport provenance attachment ABI. Keep synchronized
// with MaterialTransportProvenanceCode on the CPU.
const uint MATERIAL_TRANSPORT_PROVENANCE_BACKGROUND = 0u;
const uint MATERIAL_TRANSPORT_PROVENANCE_DETAILED_MESH = 1u;
const uint MATERIAL_TRANSPORT_PROVENANCE_COMPACT_PRIMITIVE = 2u;
const uint MATERIAL_TRANSPORT_PROVENANCE_FAR_FIELD = 3u;
const uint MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN = 255u;
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
const uint MATERIAL_DEBUG_MATERIAL_OCCLUSION = 55u;
const uint MATERIAL_DEBUG_CANONICAL_DIFFUSE_REFLECTANCE = 56u;
const uint MATERIAL_DEBUG_COMPILED_EMISSION = 57u;
const uint MATERIAL_DEBUG_GEOMETRIC_NORMAL = 58u;
const uint MATERIAL_DEBUG_OPACITY = 59u;
const uint MATERIAL_DEBUG_SIDEDNESS = 60u;
const uint MATERIAL_DEBUG_SHADING_MODEL = 61u;
const uint MATERIAL_DEBUG_TRANSPORT_PROFILE = 62u;
const uint MATERIAL_DEBUG_MATERIAL_REVISIONS = 63u;
// Values 64-70 are animation diagnostics. These two modes are deliberately
// capture-only and execute after the full direct-light loop.
const uint MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE = 71u;
const uint MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR = 72u;
const uint GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT = 80u;
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
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE = 117u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE = 118u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS = 119u;
const uint GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_OCCUPANCY_SLICE = 120u;
const uint GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_TRACE_RESULT = 121u;
const uint GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY = 122u;
const uint GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW = 123u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT = 124u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE = 125u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY = 126u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK = 127u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE = 128u;
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE = 129u;
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
const int REFLECTION_PROBE_BOX_PROJECTION_FLAG = 1;
const int REFLECTION_PROBE_CAPTURED_RADIANCE_AVAILABLE_FLAG = 1 << 1;
const int REFLECTION_PROBE_SHAPE_SPHERE = 1;
const float DEPTH_NORMAL_RELATIVE_EPSILON = 0.000001;

#ifndef FORWARD_SIMPLE_OPAQUE
#define FORWARD_SIMPLE_OPAQUE 0
#endif

#ifndef FORWARD_WEIGHTED_OIT
#define FORWARD_WEIGHTED_OIT 0
#endif

#ifndef FORWARD_DDGI_RECEIVER_CACHE
#define FORWARD_DDGI_RECEIVER_CACHE 0
#endif

#ifndef FORWARD_DDGI_RECEIVER_CACHE_REQUIRED
#define FORWARD_DDGI_RECEIVER_CACHE_REQUIRED 0
#endif

#ifndef FORWARD_GLOBAL_ILLUMINATION_DISABLED
#define FORWARD_GLOBAL_ILLUMINATION_DISABLED 0
#endif

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED && !FORWARD_DDGI_RECEIVER_CACHE
#error FORWARD_DDGI_RECEIVER_CACHE_REQUIRED requires FORWARD_DDGI_RECEIVER_CACHE
#endif

#if FORWARD_DDGI_RECEIVER_CACHE && !NJULF_DDGI_DETAILED_COUNTERS && !NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define FORWARD_DDGI_RECEIVER_CACHE_ACTIVE 1
#else
#define FORWARD_DDGI_RECEIVER_CACHE_ACTIVE 0
#endif

#if FORWARD_DDGI_RECEIVER_CACHE_ACTIVE && FORWARD_DDGI_RECEIVER_CACHE_REQUIRED
#define FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE 1
#else
#define FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE 0
#endif

// Cache-required and GI-disabled opaque artifacts are deliberately narrow
// native programs.  Keeping this as a preprocessor decision (rather than a
// constant runtime branch) prevents parameter-buffer reads, sparse receiver
// demand atomics, far-field fallback code, and debug-only gather paths from
// consuming registers or instruction-cache space in the performance pair.
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE || FORWARD_GLOBAL_ILLUMINATION_DISABLED
#define FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE 1
#else
#define FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE 0
#endif

uint ForwardDebugViewMode()
{
    return pc.Push.DebugAndAoFlags & 0xffu;
}

uint ForwardDirectionalShadowPreviewCascade()
{
    return (pc.Push.DiagnosticFlags >> 8u) & 0x03u;
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

bool ForwardReflectionCaptureEnabled()
{
    // Bit 31 is reserved in DiagnosticFlags so adding the capture mode does
    // not change the established 256-byte forward push-constant ABI.
    return (pc.Push.DiagnosticFlags & (1u << 31u)) != 0u;
}

bool ForwardDdgiReceiverCacheEnabled()
{
    return (pc.Push.DiagnosticFlags & (1u << 30u)) != 0u;
}

bool DdgiForwardEstimateCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 1u) != 0u;
}

bool DdgiClipmapCoverageCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 2u) != 0u;
}

bool DirectionalShadowReceiverCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 4u) != 0u;
}

bool MaterialTransportProvenanceEnabled()
{
    return (pc.Push.DiagnosticFlags & 8u) != 0u;
}

bool ForwardDecalGlobalIlluminationEnabled()
{
    return (pc.Push.DiagnosticFlags & 16u) != 0u;
}

bool DdgiLayeredReceiverCountersEnabled()
{
    return (pc.Push.DiagnosticFlags & 32u) != 0u;
}

bool ForwardDecalReceiveShadows()
{
    return (pc.Push.DiagnosticFlags & 64u) != 0u;
}

bool ForwardLayeredReceiverAcceptsShadows(bool geometryDecal)
{
    if (pc.Push.MeshletDrawBufferBaseIndex !=
        uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX))
        return true;
    return geometryDecal
        ? ForwardDecalReceiveShadows()
        : ForwardTransparentReceiveShadows() != 0u;
}

bool DdgiSparseDiagnosticPixel()
{
    uvec2 pixel = uvec2(max(gl_FragCoord.xy, vec2(0.0)));
    return (pixel.x & 15u) == 0u && (pixel.y & 15u) == 0u;
}

void RecordDecalFragmentAttribution(uint counterIndex)
{
    if (DdgiLayeredReceiverCountersEnabled() && DdgiSparseDiagnosticPixel())
    {
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            counterIndex,
            256u);
    }
}

uint DdgiSparseDiagnosticSampleWeight()
{
    return DdgiForwardEstimateCountersEnabled() && DdgiSparseDiagnosticPixel()
        ? 256u
        : 0u;
}

bool DirectionalShadowReceiverDiagnosticPixel()
{
    return DirectionalShadowReceiverCountersEnabled() && DdgiSparseDiagnosticPixel();
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
const uint DDGI_FORWARD_ESTIMATE_SAMPLED_IRRADIANCE_LUMINANCE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 41u;
const uint DDGI_FORWARD_ESTIMATE_ENVIRONMENT_FALLBACK_WEIGHT_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 42u;
const uint DDGI_SAMPLED_PROBE_CURRENT_FRUSTUM_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 43u;
const uint DDGI_SAMPLED_PROBE_SIDE_REAR_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 44u;
const uint DDGI_SAMPLED_PROBE_STALE_AGE_COUNTER = DDGI_FORWARD_ESTIMATE_COUNTER_BASE + 45u;
// Appended after all pre-existing renderer diagnostic families. Each receiver
// owns sample count, sampled-irradiance luminance, and delivered diffuse
// luminance, in that order.
const uint DDGI_LAYERED_RECEIVER_COUNTER_BASE = 300u;
const uint DDGI_TRANSPARENT_RECEIVER_SAMPLE_COUNT_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 0u;
const uint DDGI_TRANSPARENT_RECEIVER_IRRADIANCE_LUMINANCE_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 1u;
const uint DDGI_TRANSPARENT_RECEIVER_FINAL_LUMINANCE_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 2u;
const uint DDGI_DECAL_RECEIVER_SAMPLE_COUNT_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 3u;
const uint DDGI_DECAL_RECEIVER_IRRADIANCE_LUMINANCE_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 4u;
const uint DDGI_DECAL_RECEIVER_FINAL_LUMINANCE_COUNTER = DDGI_LAYERED_RECEIVER_COUNTER_BASE + 5u;
const uint DDGI_PRIMARY_UPDATE_REASON_AGE_REFRESH = 5u;
const float DDGI_FORWARD_ESTIMATE_LOW_DELIVERED_LUMINANCE_THRESHOLD = 0.00001;

uint PackDdgiForwardEstimateWeight(float value);

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
           debugViewMode <= MATERIAL_DEBUG_MATERIAL_REVISIONS;
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
    return SampleMaterialCoverageTexture(textureIndex, uv);
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
    uint transportSourcePath;
};

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
    result.transportSourcePath = MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;
    return result;
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

void AccumulateDdgiForwardEstimateDiagnostics(
    HybridDiffuseGiResult hybridDiffuse,
    DdgiSampleResult ddgi,
    vec3 rawDdgiDiffuse,
    vec3 diffuseReflectance,
    bool geometryDecal)
{
    bool opaqueDiagnostic = DdgiForwardEstimateDiagnosticPixel();
    bool layeredDiagnostic =
        DdgiLayeredReceiverCountersEnabled() && DdgiSparseDiagnosticPixel();
    if (!opaqueDiagnostic && !layeredDiagnostic)
        return;

    if (layeredDiagnostic)
    {
        uint receiverCounterBase = geometryDecal
            ? DDGI_DECAL_RECEIVER_SAMPLE_COUNT_COUNTER
            : DDGI_TRANSPARENT_RECEIVER_SAMPLE_COUNT_COUNTER;
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            receiverCounterBase,
            1u);
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            receiverCounterBase + 1u,
            PackDdgiForwardEstimateLuminance(
                DdgiDiagnosticLuminance(ddgi.irradiance)));
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            receiverCounterBase + 2u,
            PackDdgiForwardEstimateLuminance(
                DdgiDiagnosticLuminance(hybridDiffuse.diffuse)));
        return;
    }

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
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_SAMPLED_IRRADIANCE_LUMINANCE_COUNTER, PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(ddgi.irradiance)));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_ENVIRONMENT_FALLBACK_WEIGHT_COUNTER, PackDdgiForwardEstimateWeight(hybridDiffuse.environmentFallbackWeight / 4.0));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_RECEIVER_ALBEDO_LUMINANCE_COUNTER, PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(diffuseReflectance)));
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_RECEIVER_ALBEDO_SAMPLE_COUNT_COUNTER, 1u);

    if (spatialCoverage > 0.75 && supportCoverage < 0.0001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_ZERO_SUPPORT_SPATIAL_COUNTER, 1u);
    if (spatialCoverage > 0.75 && effectiveWeight < 0.0001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_FORWARD_ESTIMATE_ZERO_EFFECTIVE_SPATIAL_COUNTER, 1u);

    // Unlike the legacy zero-effective gate, this observes the failure mode in
    // which DDGI claims the receiver but delivers effectively no indirect light.
    float deliveredDdgiLuminance = DdgiDiagnosticLuminance(
        max(rawDdgiDiffuse, vec3(0.0)) * effectiveWeight);
    if (spatialCoverage > 0.75 && ownershipConsumed > 0.75 &&
        deliveredDdgiLuminance < DDGI_FORWARD_ESTIMATE_LOW_DELIVERED_LUMINANCE_THRESHOLD)
    {
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            DDGI_HIGH_OWNERSHIP_LOW_DELIVERED_INDIRECT_COUNTER,
            1u);
    }
}

void AccumulateDdgiInvestigationForwardDiagnostics(
    bool simplePath,
    SimpleDdgiParams simpleParams,
    vec3 worldPosition,
    vec3 normal,
    vec3 viewDir,
    vec3 simpleIrradiance,
    float simpleVisibility,
    float simpleVisibilityMomentMean,
    vec3 ddgiDiffuse,
    vec3 diffuseIbl,
    vec3 finalDiffuseIndirect)
{
    if (!DdgiForwardEstimateDiagnosticPixel())
        return;

    float ddgiLum = DdgiDiagnosticLuminance(ddgiDiffuse);
    float iblLum = DdgiDiagnosticLuminance(diffuseIbl);
    float finalLum = DdgiDiagnosticLuminance(finalDiffuseIndirect);
    bool nonfinite = any(isnan(simpleIrradiance)) || any(isinf(simpleIrradiance)) ||
        any(isnan(ddgiDiffuse)) || any(isinf(ddgiDiffuse)) ||
        any(isnan(finalDiffuseIndirect)) || any(isinf(finalDiffuseIndirect));

    if (simplePath)
    {
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_FORWARD_SAMPLE_COUNTER, 1u);
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_IRRADIANCE_LUMINANCE_COUNTER, PackDdgiForwardEstimateLuminance(DdgiDiagnosticLuminance(simpleIrradiance)));
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_VISIBILITY_COUNTER, PackDdgiForwardEstimateWeight(simpleVisibility));
        if (simpleParams.hysteresis <= 0.0001)
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FRESH_ATLAS_FORWARD_SAMPLE_COUNTER, 1u);
        if (DdgiDiagnosticLuminance(simpleIrradiance) <= 0.00001)
        {
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_ZERO_IRRADIANCE_SAMPLE_COUNTER, 1u);
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_IRRADIANCE_ZERO_TEXEL_SAMPLE_COUNTER, 1u);
        }
        else
        {
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_NONZERO_IRRADIANCE_SAMPLE_COUNTER, 1u);
        }
        if (simpleVisibilityMomentMean <= 0.00001)
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_VISIBILITY_ZERO_MOMENT_SAMPLE_COUNTER, 1u);
        if (simpleVisibility < 0.05)
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_SIMPLE_LOW_VISIBILITY_COUNTER, 1u);

        uint diagnosticVolumeIndex;
        SimpleDdgiVolume diagnosticVolume;
        float diagnosticEdgeWeight;
        vec3 safeNormal = length(normal) > 0.00001 ? normalize(normal) : vec3(0.0, 1.0, 0.0);
        bool diagnosticInVolume = SelectSimpleDdgiVolume(simpleParams, worldPosition, diagnosticVolumeIndex, diagnosticVolume, diagnosticEdgeWeight);
        bool diagnosticBiasOutsideSelectionDomain;
        vec3 diagnosticWorldPosition = SimpleDdgiResolveInterpolationPosition(
            diagnosticVolume,
            worldPosition,
            safeNormal,
            viewDir,
            simpleParams,
            diagnosticBiasOutsideSelectionDomain);
        vec3 grid = (diagnosticWorldPosition - diagnosticVolume.origin) / diagnosticVolume.spacing;
        vec3 maxGrid = vec3(diagnosticVolume.gridCount) - vec3(1.0);
        if (!diagnosticInVolume || diagnosticBiasOutsideSelectionDomain ||
            any(lessThan(grid, vec3(0.0))) || any(greaterThan(grid, maxGrid)))
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_OUT_OF_GRID_SAMPLE_COUNTER, 1u);
        if (diagnosticBiasOutsideSelectionDomain ||
            any(notEqual(ivec3(round(grid)), clamp(ivec3(round(grid)), ivec3(0), ivec3(diagnosticVolume.gridCount) - ivec3(1)))))
            AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_CLAMPED_PROBE_SAMPLE_COUNTER, 1u);
    }
    else
    {
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_LEGACY_FORWARD_SAMPLE_COUNTER, 1u);
    }

    if (finalLum <= 0.00001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_ZERO_FINAL_INDIRECT_COUNTER, 1u);
    if (ddgiLum <= 0.00001 && iblLum > 0.00001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_ZERO_DDGI_NONZERO_IBL_COUNTER, 1u);
    if (ddgiLum <= 0.00001 && iblLum <= 0.00001)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_ZERO_DDGI_ZERO_IBL_COUNTER, 1u);
    if (nonfinite)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FORWARD_NONFINITE_SAMPLE_COUNTER, 1u);
}

vec3 SafeRadiance(vec3 value)
{
    if (any(isnan(value)) || any(isinf(value)))
        return vec3(0.0);

    return clamp(value, vec3(0.0), vec3(64.0));
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

const uint DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE = 0u;
const uint DIRECTIONAL_SHADOW_SAMPLE_REJECT_PROJECTION = 1u;
const uint DIRECTIONAL_SHADOW_SAMPLE_REJECT_UV_DEPTH = 2u;

float FetchDirectionalShadowDepth(
    uint textureIndex,
    ivec2 texel,
    ivec2 maxTexel)
{
    float sampledDepth = texelFetch(
        BindlessTextures[nonuniformEXT(int(textureIndex))],
        clamp(texel, ivec2(0), maxTexel),
        0).r;
    return sampledDepth;
}

float SampleDirectionalShadowTexel(
    uint textureIndex,
    ivec2 texel,
    ivec2 maxTexel,
    float receiverDepth)
{
    float sampledDepth = FetchDirectionalShadowDepth(textureIndex, texel, maxTexel);
    // The shadow raster pass already applies reverse-Z constant/slope bias and
    // the receiver position carries the authored world-space normal bias. A
    // second normalized-depth bias scales with the full light-space depth span
    // (0.0005 became about 0.25 m at Sponza's 250 m range) and erases valid
    // architectural shadows.
    return receiverDepth >= sampledDepth ? 1.0 : 0.0;
}

float SampleDirectionalShadowTap(
    uint textureIndex,
    vec2 uv,
    float receiverDepth,
    float mapSize)
{
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 1.0;

    // A regular linear depth sample interpolates the caster depths before the
    // comparison. On sloped Sponza geometry that turns a single shadow edge into
    // several false depth contours. Fetch and compare the four texels first, then
    // bilinearly filter visibility (the ordering used by comparison-sampler PCF).
    float safeMapSize = max(mapSize, 1.0);
    vec2 texelPosition = uv * safeMapSize - vec2(0.5);
    ivec2 baseTexel = ivec2(floor(texelPosition));
    ivec2 maxTexel = ivec2(max(int(safeMapSize) - 1, 0));
    vec2 weights = fract(texelPosition);
    float lower = mix(
        SampleDirectionalShadowTexel(textureIndex, baseTexel, maxTexel, receiverDepth),
        SampleDirectionalShadowTexel(textureIndex, baseTexel + ivec2(1, 0), maxTexel, receiverDepth),
        weights.x);
    float upper = mix(
        SampleDirectionalShadowTexel(textureIndex, baseTexel + ivec2(0, 1), maxTexel, receiverDepth),
        SampleDirectionalShadowTexel(textureIndex, baseTexel + ivec2(1, 1), maxTexel, receiverDepth),
        weights.x);
    return mix(lower, upper, weights.y);
}

float SampleDirectionalShadowPcf(
    uint textureIndex,
    vec2 uv,
    float receiverDepth,
    float mapSize,
    int radius)
{
    float safeMapSize = max(mapSize, 1.0);
    vec2 texelPosition = uv * safeMapSize - vec2(0.5);
    ivec2 baseTexel = ivec2(floor(texelPosition));
    ivec2 maxTexel = ivec2(max(int(safeMapSize) - 1, 0));
    vec2 weights = fract(texelPosition);
    int safeRadius = clamp(radius, 1, 3);
    float lit = 0.0;

    // Adjacent bilinear PCF taps share their inner texels. Accumulating the
    // unique (2r + 2)^2 grid preserves the same filter while avoiding four
    // independent fetches for every tap.
    for (int y = -safeRadius; y <= safeRadius + 1; y++)
    {
        float weightY = y == -safeRadius
            ? 1.0 - weights.y
            : (y == safeRadius + 1 ? weights.y : 1.0);
        for (int x = -safeRadius; x <= safeRadius + 1; x++)
        {
            float weightX = x == -safeRadius
                ? 1.0 - weights.x
                : (x == safeRadius + 1 ? weights.x : 1.0);
            lit += SampleDirectionalShadowTexel(
                textureIndex,
                baseTexel + ivec2(x, y),
                maxTexel,
                receiverDepth) * weightX * weightY;
        }
    }

    float filterWidth = float(safeRadius * 2 + 1);
    return lit / (filterWidth * filterWidth);
}

void InspectDirectionalShadowFootprint(
    uint textureIndex,
    vec2 uv,
    float mapSize,
    int radius,
    out float minimumSampledDepth,
    out float maximumSampledDepth)
{
    float safeMapSize = max(mapSize, 1.0);
    vec2 texelPosition = uv * safeMapSize - vec2(0.5);
    ivec2 baseTexel = ivec2(floor(texelPosition));
    ivec2 maxTexel = ivec2(max(int(safeMapSize) - 1, 0));
    vec2 weights = fract(texelPosition);
    int safeRadius = clamp(radius, 0, 3);
    int minimumOffset = safeRadius == 0 ? 0 : -safeRadius;
    int maximumOffset = safeRadius == 0 ? 1 : safeRadius + 1;
    minimumSampledDepth = 1.0;
    maximumSampledDepth = 0.0;

    for (int y = minimumOffset; y <= maximumOffset; y++)
    {
        float weightY = safeRadius == 0
            ? (y == 0 ? 1.0 - weights.y : weights.y)
            : (y == -safeRadius
                ? 1.0 - weights.y
                : (y == safeRadius + 1 ? weights.y : 1.0));
        for (int x = minimumOffset; x <= maximumOffset; x++)
        {
            float weightX = safeRadius == 0
                ? (x == 0 ? 1.0 - weights.x : weights.x)
                : (x == -safeRadius
                    ? 1.0 - weights.x
                    : (x == safeRadius + 1 ? weights.x : 1.0));
            if (weightX * weightY <= 0.0)
                continue;

            float sampledDepth = FetchDirectionalShadowDepth(
                textureIndex,
                baseTexel + ivec2(x, y),
                maxTexel);
            minimumSampledDepth = min(minimumSampledDepth, sampledDepth);
            maximumSampledDepth = max(maximumSampledDepth, sampledDepth);
        }
    }
}

bool TrySampleDirectionalShadowCascade(
    uint cascade,
    vec3 biasedWorldPosition,
    float mapSize,
    int radius,
    bool collectDepthDiagnostics,
    out float shadow,
    out uint rejection,
    out float diagnosticReceiverDepth,
    out float diagnosticMinimumSampledDepth,
    out float diagnosticMaximumSampledDepth)
{
    shadow = 1.0;
    rejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE;
    diagnosticReceiverDepth = 0.0;
    diagnosticMinimumSampledDepth = 0.0;
    diagnosticMaximumSampledDepth = 0.0;

    vec4 lightClip = MulRowMajor(vec4(biasedWorldPosition, 1.0), ReadShadowMatrix(cascade));
    if (abs(lightClip.w) <= 0.00001 || any(isnan(lightClip)) || any(isinf(lightClip)))
    {
        rejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_PROJECTION;
        return false;
    }

    vec3 shadowCoord = lightClip.xyz / lightClip.w;
    if (any(isnan(shadowCoord)) || any(isinf(shadowCoord)))
    {
        rejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_PROJECTION;
        return false;
    }

    vec2 uv = shadowCoord.xy * 0.5 + vec2(0.5);
    float receiverDepth = shadowCoord.z;
    if (uv.x < 0.0 || uv.x > 1.0 ||
        uv.y < 0.0 || uv.y > 1.0 ||
        receiverDepth < 0.0 || receiverDepth > 1.0)
    {
        rejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_UV_DEPTH;
        return false;
    }

    uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + cascade;
    diagnosticReceiverDepth = receiverDepth;
    if (collectDepthDiagnostics)
    {
        InspectDirectionalShadowFootprint(
            textureIndex,
            uv,
            mapSize,
            radius,
            diagnosticMinimumSampledDepth,
            diagnosticMaximumSampledDepth);
    }

    if (radius <= 0)
    {
        shadow = SampleDirectionalShadowTap(textureIndex, uv, receiverDepth, mapSize);
        return true;
    }

    shadow = SampleDirectionalShadowPcf(
        textureIndex,
        uv,
        receiverDepth,
        mapSize,
        radius);
    return true;
}

bool FindDirectionalShadowTransition(
    float cameraDistance,
    vec4 splits,
    vec4 transitionData,
    uint cascadeCount,
    out uint lowerCascade,
    out uint upperCascade,
    out float blendWeight)
{
    lowerCascade = 0u;
    upperCascade = 0u;
    blendWeight = 0.0;
    if (cascadeCount < 2u)
        return false;

    float transitionFraction = clamp(transitionData.x, 0.02, 0.30);
    for (uint boundaryIndex = 0u; boundaryIndex + 1u < cascadeCount; boundaryIndex++)
    {
        float boundary = splits[int(boundaryIndex)];
        float previousBoundary = boundaryIndex == 0u
            ? transitionData.y
            : splits[int(boundaryIndex - 1u)];
        float nextBoundary = boundaryIndex + 2u >= cascadeCount
            ? transitionData.z
            : splits[int(boundaryIndex + 1u)];
        float previousSpan = max(0.001, boundary - previousBoundary);
        float nextSpan = max(0.001, nextBoundary - boundary);
        float transitionWidth = min(previousSpan, nextSpan) * transitionFraction;
        if (transitionWidth > 0.0 &&
            cameraDistance >= boundary - transitionWidth &&
            cameraDistance <= boundary + transitionWidth)
        {
            lowerCascade = boundaryIndex;
            upperCascade = boundaryIndex + 1u;
            blendWeight = smoothstep(
                boundary - transitionWidth,
                boundary + transitionWidth,
                cameraDistance);
            return true;
        }
    }

    return false;
}

float EstimateFarFieldSunShadow(vec3 worldPosition, vec3 normal, vec3 lightDirection)
{
    uint simpleFlags = ReadSimpleDdgiFlags(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((simpleFlags & SIMPLE_DDGI_FLAG_FAR_SUN_SHADOW_ENABLED) == 0u)
        return 1.0;

    FarFieldClipmapParams farField = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
    if (!farField.enabled)
        return 1.0;

    float hitT;
    vec3 hitNormal;
    vec3 hitAlbedo;
    bool stepExhausted;
    uint visitedSteps;
    vec3 origin = worldPosition + normal * max(farField.voxelSize * 0.35, 0.05);
    bool blocked = TraceFarFieldClipmapDetailed(
        origin,
        normalize(lightDirection),
        farField.voxelSize * 0.5,
        FarFieldTraceMaximumDistance(farField),
        hitT,
        hitNormal,
        hitAlbedo,
        stepExhausted,
        visitedSteps);

    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FAR_SUN_SHADOW_SAMPLE_COUNTER, 1u);
    if (blocked)
        AddRendererDiagnostic(pc.Push.CurrentFrameIndex, DDGI_INVESTIGATION_FAR_SUN_SHADOW_OCCLUDED_COUNTER, 1u);
    return blocked ? 0.0 : 1.0;
}

uint QuantizeDirectionalShadowDiagnosticDepth(float depth)
{
    return uint(round(clamp(depth, 0.0, 1.0) *
        DIRECTIONAL_SHADOW_RECEIVER_DEPTH_QUANTIZATION_SCALE));
}

void RecordDirectionalShadowVisibility(
    uint cascade,
    float visibility,
    uint fullyLitCounterBase,
    uint partialCounterBase,
    uint fullyShadowedCounterBase)
{
    uint counter = visibility >= 0.999
        ? fullyLitCounterBase
        : (visibility <= 0.001 ? fullyShadowedCounterBase : partialCounterBase);
    AddRendererDiagnostic(pc.Push.CurrentFrameIndex, counter + cascade, 1u);
}

float EvaluateDirectionalShadow(
    uint lightIndex,
    vec3 worldPosition,
    vec3 normal,
    bool geometryDecal,
    out uint selectedCascade)
{
    selectedCascade = 0u;
    vec4 shadowIndices = ReadShadowIndices();
    if (shadowIndices.x < 0.5 ||
        !ForwardLayeredReceiverAcceptsShadows(geometryDecal) ||
        int(round(shadowIndices.w)) != int(lightIndex))
        return 1.0;

    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_SHADOW_EVALUATION_COUNTER);

    vec4 shadowSettings = ReadShadowSettings();
    uint cascadeCount = clamp(uint(round(shadowIndices.y)), 1u, uint(MAX_DIRECTIONAL_SHADOW_TEXTURES));
    vec4 splits = ReadShadowCascadeSplits();
    vec4 transitionData = ReadShadowCascadeTransitionData();
    float cameraDistance = CameraForwardDistance(worldPosition);
    selectedCascade = SelectShadowCascade(cameraDistance, splits, cascadeCount);

    vec3 biasedPosition = worldPosition + normal * shadowSettings.y;
    float mapSize = max(shadowSettings.z, 1.0);
    int radius = int(clamp(round(shadowSettings.w), 0.0, 3.0));
    bool diagnosticPixel = DirectionalShadowReceiverDiagnosticPixel();
    if (diagnosticPixel)
    {
        AddRendererDiagnostic(
            pc.Push.CurrentFrameIndex,
            DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_SELECTION_COUNTER_BASE + selectedCascade,
            1u);
    }

    float primaryShadow = 1.0;
    uint primaryRejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE;
    float primaryReceiverDepth = 0.0;
    float primaryMinimumSampledDepth = 0.0;
    float primaryMaximumSampledDepth = 0.0;
    bool primaryValid = TrySampleDirectionalShadowCascade(
        selectedCascade,
        biasedPosition,
        mapSize,
        radius,
        diagnosticPixel,
        primaryShadow,
        primaryRejection,
        primaryReceiverDepth,
        primaryMinimumSampledDepth,
        primaryMaximumSampledDepth);
    if (diagnosticPixel)
    {
        if (primaryRejection == DIRECTIONAL_SHADOW_SAMPLE_REJECT_PROJECTION)
        {
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_PROJECTION_REJECT_COUNTER_BASE + selectedCascade,
                1u);
        }
        else if (primaryRejection == DIRECTIONAL_SHADOW_SAMPLE_REJECT_UV_DEPTH)
        {
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_UV_DEPTH_REJECT_COUNTER_BASE + selectedCascade,
                1u);
        }
        else if (primaryValid)
        {
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_RESOLVED_COUNTER_BASE + selectedCascade,
                1u);
            if (primaryMaximumSampledDepth <= 0.000001)
            {
                AddRendererDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    DIRECTIONAL_SHADOW_RECEIVER_CLEAR_DEPTH_FOOTPRINT_COUNTER_BASE + selectedCascade,
                    1u);
            }

            RecordDirectionalShadowVisibility(
                selectedCascade,
                primaryShadow,
                DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_FULLY_LIT_COUNTER_BASE,
                DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_PARTIAL_COUNTER_BASE,
                DIRECTIONAL_SHADOW_RECEIVER_PRIMARY_FULLY_SHADOWED_COUNTER_BASE);
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_RECEIVER_DEPTH_SUM_COUNTER_BASE + selectedCascade,
                QuantizeDirectionalShadowDiagnosticDepth(primaryReceiverDepth));
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_MIN_SAMPLED_DEPTH_SUM_COUNTER_BASE + selectedCascade,
                QuantizeDirectionalShadowDiagnosticDepth(primaryMinimumSampledDepth));
            AddRendererDiagnostic(
                pc.Push.CurrentFrameIndex,
                DIRECTIONAL_SHADOW_RECEIVER_MAX_SAMPLED_DEPTH_SUM_COUNTER_BASE + selectedCascade,
                QuantizeDirectionalShadowDiagnosticDepth(primaryMaximumSampledDepth));
        }
    }

    float shadow = primaryShadow;
    bool resolved = primaryValid;
    uint lowerCascade;
    uint upperCascade;
    float transitionBlend;
    if (FindDirectionalShadowTransition(
            cameraDistance,
            splits,
            transitionData,
            cascadeCount,
            lowerCascade,
            upperCascade,
            transitionBlend))
    {
        float lowerShadow = 1.0;
        float upperShadow = 1.0;
        uint ignoredRejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE;
        float ignoredReceiverDepth = 0.0;
        float ignoredMinimumSampledDepth = 0.0;
        float ignoredMaximumSampledDepth = 0.0;
        bool lowerValid = lowerCascade == selectedCascade
            ? primaryValid
            : TrySampleDirectionalShadowCascade(
                lowerCascade,
                biasedPosition,
                mapSize,
                radius,
                false,
                lowerShadow,
                ignoredRejection,
                ignoredReceiverDepth,
                ignoredMinimumSampledDepth,
                ignoredMaximumSampledDepth);
        if (lowerCascade == selectedCascade)
            lowerShadow = primaryShadow;

        bool upperValid = upperCascade == selectedCascade
            ? primaryValid
            : TrySampleDirectionalShadowCascade(
                upperCascade,
                biasedPosition,
                mapSize,
                radius,
                false,
                upperShadow,
                ignoredRejection,
                ignoredReceiverDepth,
                ignoredMinimumSampledDepth,
                ignoredMaximumSampledDepth);
        if (upperCascade == selectedCascade)
            upperShadow = primaryShadow;

        if (lowerValid && upperValid)
        {
            shadow = mix(lowerShadow, upperShadow, transitionBlend);
            resolved = true;
            if (diagnosticPixel)
            {
                AddRendererDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    DIRECTIONAL_SHADOW_RECEIVER_TRANSITION_BLEND_COUNTER_BASE + lowerCascade,
                    1u);
            }
        }
        else if (lowerValid || upperValid)
        {
            shadow = lowerValid ? lowerShadow : upperShadow;
            resolved = true;
            if (!primaryValid && diagnosticPixel)
            {
                AddRendererDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    DIRECTIONAL_SHADOW_RECEIVER_FALLBACK_COUNTER_BASE + selectedCascade,
                    1u);
            }
        }
    }

    if (!resolved)
    {
        for (uint offset = 1u; offset < cascadeCount && !resolved; offset++)
        {
            if (selectedCascade >= offset)
            {
                uint fallbackCascade = selectedCascade - offset;
                uint ignoredRejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE;
                float ignoredReceiverDepth = 0.0;
                float ignoredMinimumSampledDepth = 0.0;
                float ignoredMaximumSampledDepth = 0.0;
                resolved = TrySampleDirectionalShadowCascade(
                    fallbackCascade,
                    biasedPosition,
                    mapSize,
                    radius,
                    false,
                    shadow,
                    ignoredRejection,
                    ignoredReceiverDepth,
                    ignoredMinimumSampledDepth,
                    ignoredMaximumSampledDepth);
            }

            if (!resolved && selectedCascade + offset < cascadeCount)
            {
                uint fallbackCascade = selectedCascade + offset;
                uint ignoredRejection = DIRECTIONAL_SHADOW_SAMPLE_REJECT_NONE;
                float ignoredReceiverDepth = 0.0;
                float ignoredMinimumSampledDepth = 0.0;
                float ignoredMaximumSampledDepth = 0.0;
                resolved = TrySampleDirectionalShadowCascade(
                    fallbackCascade,
                    biasedPosition,
                    mapSize,
                    radius,
                    false,
                    shadow,
                    ignoredRejection,
                    ignoredReceiverDepth,
                    ignoredMinimumSampledDepth,
                    ignoredMaximumSampledDepth);
            }
        }

        if (diagnosticPixel)
        {
            if (resolved)
            {
                AddRendererDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    DIRECTIONAL_SHADOW_RECEIVER_FALLBACK_COUNTER_BASE + selectedCascade,
                    1u);
            }
            else
            {
                AddRendererDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    DIRECTIONAL_SHADOW_RECEIVER_UNRESOLVED_COUNTER,
                    1u);
            }
        }
    }

    if (!resolved)
        shadow = 1.0;

    if (selectedCascade + 1u >= cascadeCount && cameraDistance > splits[int(selectedCascade)])
    {
        GPULight light = ReadLight(lightIndex);
        shadow *= EstimateFarFieldSunShadow(worldPosition, normal, normalize(-light.Direction));
    }

    float finalShadow = mix(1.0, shadow, clamp(shadowSettings.x, 0.0, 1.0));
    if (diagnosticPixel)
    {
        RecordDirectionalShadowVisibility(
            selectedCascade,
            finalShadow,
            DIRECTIONAL_SHADOW_RECEIVER_FINAL_FULLY_LIT_COUNTER_BASE,
            DIRECTIONAL_SHADOW_RECEIVER_FINAL_PARTIAL_COUNTER_BASE,
            DIRECTIONAL_SHADOW_RECEIVER_FINAL_FULLY_SHADOWED_COUNTER_BASE);
    }

    return finalShadow;
}

float CompareReverseZDepth(float receiverDepth, float sampledDepth, float bias)
{
    if (receiverDepth < 0.0 || receiverDepth > 1.0)
        return 1.0;
    return receiverDepth >= sampledDepth - bias ? 1.0 : 0.0;
}

float EvaluateSpotShadow(
    uint lightIndex,
    vec3 worldPosition,
    vec3 normal,
    bool geometryDecal)
{
    int shadowIndex = ReadLocalSpotShadowIndex(lightIndex);
    if (shadowIndex < 0 || !ForwardLayeredReceiverAcceptsShadows(geometryDecal))
        return 1.0;

    GPUSpotShadow shadow = ReadSpotShadow(uint(shadowIndex));
    if (shadow.Enabled == 0 || shadow.LightIndex != int(lightIndex))
        return 1.0;
    if (shadow.BiasStrengthTexelSize.z <= 0.0)
        return 1.0;

    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_SHADOW_EVALUATION_COUNTER);

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

float EvaluatePointShadow(
    uint lightIndex,
    vec3 worldPosition,
    vec3 normal,
    bool geometryDecal)
{
    int shadowIndex = ReadLocalPointShadowIndex(lightIndex);
    if (shadowIndex < 0 || !ForwardLayeredReceiverAcceptsShadows(geometryDecal))
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

    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_SHADOW_EVALUATION_COUNTER);

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
    float facingSign = gl_FrontFacing ? 1.0 : -1.0;

    vec3 tangentNormal = SampleMaterialTexture(material.NormalTextureIndex, uv).xyz * 2.0 - 1.0;
    if ((material.FeatureFlags & MATERIAL_FEATURE_COMPRESSED_NORMAL_BC5) != 0u)
        tangentNormal.z = sqrt(max(0.0, 1.0 - dot(tangentNormal.xy, tangentNormal.xy)));
    tangentNormal.xy *= material.NormalScaleBias.x;
    tangentNormal = normalize(tangentNormal);

    return normalize(BuildOrthonormalTbn(interpolatedNormal, interpolatedTangent, facingSign) * tangentNormal);
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

    vec3 globalDirection = EnvironmentUsesAnalyticSky(environment)
        ? normalize(reflectionDirection)
        : RotateEnvironmentDirection(reflectionDirection, environment.RotationRadians);
    vec3 globalReflection = SampleEnvironmentPrefilteredRadiance(
        environment,
        reflectionDirection,
        lod) * header.GlobalFallbackIntensity;

    vec3 localReflection = vec3(0.0);
    vec3 firstWeightColor = vec3(0.0);
    vec3 projectedDirection = globalDirection;
    float totalWeight = 0.0;
    int acceptedProbeCount = 0;
    int selectedProbeIndex = -1;
    bool blendingEnabled = (header.Flags & REFLECTION_PROBE_BLENDING_ENABLED_FLAG) != 0u;
    int maxAcceptedProbes = max(header.MaxProbesPerPixel, 1);

    if (!ForwardReflectionCaptureEnabled())
    {
        for (int probeIndex = 0; probeIndex < header.ProbeCount && acceptedProbeCount < maxAcceptedProbes; probeIndex++)
        {
            GPUReflectionProbe probe = ReadReflectionProbe(uint(probeIndex));
            // Array layers are recyclable. Only probe captures that have completed both rendering
            // and prefiltering are allowed to contribute local radiance.
            if (probe.CubemapArrayIndex < 0 ||
                (probe.Flags & REFLECTION_PROBE_CAPTURED_RADIANCE_AVAILABLE_FLAG) == 0)
                continue;
            float weight = ProbeInfluenceWeight(probe, worldPosition);
            if (weight <= 0.0001)
                continue;

            if (!blendingEnabled)
                weight = 1.0;

            vec3 probeDirection = ProjectProbeDirection(header, probe, worldPosition, reflectionDirection);
            vec3 probeColor = textureLod(
                BindlessCubeArrayTextures[nonuniformEXT(header.ProbeCubemapArrayTextureIndex)],
                vec4(probeDirection, float(probe.CubemapArrayIndex)),
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

    vec3 globalReflection = SampleEnvironmentPrefilteredRadiance(
        environment,
        reflectionDirection,
        lod) * header.GlobalFallbackIntensity * header.Intensity;

    return globalReflection * (fresnel * brdf.x + brdf.y) * environment.SpecularIntensity * specularOcclusion;
}

void EvaluateIbl(
    vec3 albedo,
    float metallic,
    vec3 diffuseReflectance,
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
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    // The cache producer evaluates diffuse environment irradiance once per
    // low-frequency gather sample and preserves it separately from DDGI so
    // their AO policies remain exact. Avoid repeating this cubemap lookup for
    // every full-resolution opaque fragment.
    diffuseIbl = vec3(0.0);
#else
    vec3 irradiance = EvaluateEnvironmentDiffuseIrradiance(environment, normal);
    // Diffuse IBL is an irradiance-derived radiance field.  AO is applied once by
    // indirect composition to the environment-owned share; DDGI retains its own
    // probe visibility instead of receiving a second screen-space occlusion term.
    // Irradiance cubemaps store E = integral(L cos(theta) dw), for both HDR
    // sources and the procedural sky. Convert that incident irradiance to
    // outgoing Lambertian radiance exactly once, matching DDGI receivers.
    diffuseIbl = EvaluateGiDiffuseFromIrradiance(
        irradiance,
        diffuseReflectance);
#endif

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

    // Simple DDGI stores diffuse hemispherical irradiance only.  It is not a
    // directional radiance representation, so indirect specular remains owned by
    // SSR/reflection probes/prefiltered environment lighting.
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    vec3 directionalDiffuseBase,
    float roughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance,
    out vec3 diffuseContribution)
{
    diffuseContribution = vec3(0.0);
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
    vec3 diffuse = EvaluateGiDiffuseBrdf(
        directionalDiffuseBase,
        dielectricF0,
        nDotL,
        nDotV);
    diffuseContribution = diffuse * radiance * nDotL;

    return diffuseContribution + specular * radiance * nDotL;
}

void AccumulateLight(
    uint lightIndex,
    vec3 albedo,
    float metallic,
    vec3 directionalDiffuseBase,
    float roughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 shadowNormal,
    vec3 viewDirection,
    vec3 worldPosition,
    bool geometryDecal,
    out float shadowFactor,
    out uint shadowCascade,
    inout vec3 directLighting,
    inout vec3 directDiffuseSource)
{
    GPULight light = ReadLight(lightIndex);
    shadowFactor = 1.0;
    shadowCascade = 0u;

    vec3 lightDirection;
    float attenuation = 1.0;

    if (light.Type == 1)
    {
        lightDirection = normalize(-light.Direction);
        // A light below the geometric horizon cannot contribute diffuse or
        // specular BRDF energy.  Skip its shadow lookup before entering the
        // expensive directional PCF path; this is particularly important for
        // the large number of back-facing fragments in dense meshlet scenes.
        if (dot(normal, lightDirection) <= 0.0)
            return;
        shadowFactor = EvaluateDirectionalShadow(
            lightIndex,
            worldPosition,
            shadowNormal,
            geometryDecal,
            shadowCascade);
    }
    else
    {
        vec3 toLight = light.Position - worldPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.Range || light.Range <= 0.0)
            return;

        lightDirection = toLight / max(distanceToLight, 0.0001);
        if (dot(normal, lightDirection) <= 0.0)
            return;
        attenuation = EvaluateNjulfPunctualRangeAttenuation(distanceToLight, light.Range);

        if (light.Type == 2)
        {
            attenuation *= EvaluateNjulfSpotAttenuation(light.Direction, lightDirection, light.SpotAngle);
            shadowFactor = EvaluateSpotShadow(
                lightIndex,
                worldPosition,
                shadowNormal,
                geometryDecal);
        }
        else
        {
            shadowFactor = EvaluatePointShadow(
                lightIndex,
                worldPosition,
                shadowNormal,
                geometryDecal);
        }
    }

    vec3 radiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    vec3 diffuseContribution;
    directLighting += EvaluatePbrLight(
        albedo,
        metallic,
        directionalDiffuseBase,
        roughness,
        dielectricF0,
        normal,
        viewDirection,
        lightDirection,
        radiance,
        diffuseContribution) * shadowFactor;
    directDiffuseSource += diffuseContribution * shadowFactor;
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

float forwardDebugOutputAlpha;

bool IsDdgiDebugView(uint view)
{
    return view >= GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE &&
           view <= GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE;
}

vec3 DdgiDebugCategoryColor(uint view)
{
    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
        return vec3(1.0, 0.55, 0.10);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SPATIAL_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPORT_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_COVERAGE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE)
        return vec3(0.10, 0.85, 1.0);

    if (view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_VISIBILITY_CONFIDENCE ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_CHAIN ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS ||
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
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK ||
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
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RELOCATION_DIRECTION ||
        view == GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE)
        return vec3(0.85, 0.85, 0.10);

    if (view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_OCCUPANCY_SLICE ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_TRACE_RESULT ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY ||
        view == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW)
        return vec3(0.20, 1.0, 0.55);

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
    WriteForwardColor(vec4(
        ApplyDdgiDebugIdentity(color, view),
        forwardDebugOutputAlpha));
}

void WriteMaterialTransportProvenance(uint sourcePath)
{
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    if (MaterialTransportProvenanceEnabled())
        outMaterialTransportProvenance =
            float(min(sourcePath, MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN)) / 255.0;
#endif
}

uint ResolveSimpleDdgiMaterialTransportProvenance(
    SimpleDdgiGatherResult gather,
    SimpleDdgiParams params)
{
#if NJULF_SIMPLE_DDGI_GATHER_ATTRIBUTION
    if (SimpleDdgiRadiometricOwnership(gather) <= 0.000001)
        return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;

    uint sourceVolumeIndex = gather.selectedVolume;
    if (gather.secondaryVolume != SIMPLE_DDGI_INVALID_VOLUME_INDEX &&
        gather.secondaryContributionWeight > gather.primaryContributionWeight)
    {
        sourceVolumeIndex = gather.secondaryVolume;
    }
    if (sourceVolumeIndex >= params.volumeCount)
        return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;

    SimpleDdgiVolume sourceVolume = ReadSimpleDdgiVolume(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        sourceVolumeIndex);
    bool farFieldOnlyRing =
        (params.flags & SIMPLE_DDGI_FLAG_FAR_FIELD_ENABLED) != 0u &&
        sourceVolume.spacing >= 15.999;
    if (farFieldOnlyRing)
        return MATERIAL_TRANSPORT_PROVENANCE_FAR_FIELD;
    return sourceVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED
        ? MATERIAL_TRANSPORT_PROVENANCE_DETAILED_MESH
        : MATERIAL_TRANSPORT_PROVENANCE_COMPACT_PRIMITIVE;
#else
    return MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;
#endif
}

void main()
{
    uint debugViewMode = ForwardDebugViewMode();
    uint ambientOcclusionDebugView = ForwardAmbientOcclusionDebugView();
    WriteMaterialTransportProvenance(MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN);
    GPUMaterialData material = ReadMaterial(fragMaterialIndex);
    bool geometryDecal = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_GEOMETRY_DECAL);
    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_INVOCATION_COUNTER);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
    {
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_BACKFACE_KILLED_COUNTER);
        discard;
    }

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

    MaterialAlphaCoverage materialCoverage = EvaluateMaterialAlphaCoverage(
        material,
        fragTexCoord,
        fragTexCoord2,
        fragVertexColor.a);
    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    float alphaMode = materialCoverage.AlphaMode;
    float alphaCutoff = materialCoverage.AlphaCutoff;
    float outputAlpha = materialCoverage.Alpha;
    forwardDebugOutputAlpha =
        alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha;

    if (!MaterialCoverageSurvivesForward(materialCoverage))
    {
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_COVERAGE_KILLED_COUNTER);
        discard;
    }

    if (geometryDecal)
        RecordDecalFragmentAttribution(DECAL_ESTIMATED_SURVIVING_COUNTER);

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

    // glTF metallic-roughness contract: G = roughness and B = metallic.
    // Occlusion is an independent binding even when it aliases the same image.
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.z,
                material.MetallicRoughnessOffsetScale,
                material.TextureRotations.z));
    // The upload contract binds DefaultWhiteTexture for a missing emissive texture.
    // Sample independently of the factor: material.Emissive is the authoritative black
    // default, while a texture-only material remains valid when its factor is non-zero.
    vec4 emissiveSample = SampleMaterialTexture(
        material.EmissiveTextureIndex,
        MaterialUv(
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w));

    float roughness = clamp(material.MetallicRoughnessAO.y * armSample.g, 0.04, 1.0);
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    float sampledOcclusion = material.OcclusionTextureIndex == DEFAULT_WHITE_TEXTURE
        ? 1.0
        : SampleMaterialTexture(
            material.OcclusionTextureIndex,
            MaterialUv(
                material.OcclusionBinding.y,
                material.OcclusionOffsetScale,
                material.OcclusionBinding.x)).r;
    float ambientOcclusion = EvaluateGiMaterialOcclusion(
        material.MetallicRoughnessAO.z,
        sampledOcclusion);
    float screenSpaceAo = SampleScreenSpaceAo();
    float indirectAo = clamp(ambientOcclusion * screenSpaceAo, 0.0, 1.0);
    // Material AO is receiver energy, not probe visibility/leak metadata. It is
    // applied once after the DDGI irradiance gather below.
    float ddgiIndirectAo = 1.0;
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
    // NJULF's ThinSurface policy is a GI transport contract, not an opt-in to
    // the forward KHR_materials_transmission approximation. The importer keeps
    // the transmission feature bit so the factor/texture reaches DDGI, but the
    // visible opaque cloth must retain its ordinary raster BRDF. Treating the
    // bit alone as raster transmission removes reflected diffuse and replaces
    // it with a saturated environment sample, which makes shadowed curtains
    // look self-lit.
    bool thinGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    bool rasterTransmissionEnabled =
        (material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u &&
        !thinGiTransport;

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

        if (rasterTransmissionEnabled ||
            (material.FeatureFlags & MATERIAL_FEATURE_IOR) != 0u)
        {
            ior = clamp(materialExtension.Transmission.y, 1.0, 3.0);
        }

        if (rasterTransmissionEnabled)
        {
            transmissionFactor = clamp(materialExtension.Transmission.x, 0.0, 1.0);
            if ((material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION_TEXTURE) != 0u)
                transmissionFactor *= SampleMaterialTexture(materialExtension.TransmissionTextureIndex, ExtensionUv(materialExtension.TransmissionOffsetScale, materialExtension.ExtensionTextureRotations1.z, materialExtension.ExtensionTextureTexCoordSets1.z)).r;
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

    bool reflectsIndirectDiffuse = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_REFLECTS_INDIRECT_DIFFUSE);
    vec3 directionalDiffuseBase = reflectsIndirectDiffuse
        ? EvaluateGiDirectionalDiffuseBase(
            albedo,
            metallic,
            transmissionFactor,
            clearcoatFactor,
            sheenColor)
        : vec3(0.0);
    vec3 canonicalDiffuseReflectance = reflectsIndirectDiffuse
        ? EvaluateGiHemisphericalDiffuseReflectance(
            albedo,
            metallic,
            ior,
            specularFactor,
            specularColor,
            transmissionFactor,
            clearcoatFactor,
            sheenColor,
            max(dot(normal, viewDirection), 0.0))
        : vec3(0.0);

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

        if (debugViewMode == MATERIAL_DEBUG_MATERIAL_OCCLUSION)
        {
            WriteForwardColor(vec4(vec3(ambientOcclusion), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_CANONICAL_DIFFUSE_REFLECTANCE)
        {
            WriteForwardColor(vec4(canonicalDiffuseReflectance, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_COMPILED_EMISSION)
        {
            vec3 displayEmission = emissive / (vec3(1.0) + emissive);
            WriteForwardColor(vec4(displayEmission, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_GEOMETRIC_NORMAL)
        {
            WriteForwardColor(vec4(geometricNormal * 0.5 + vec3(0.5), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_OPACITY)
        {
            WriteForwardColor(vec4(vec3(outputAlpha), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SIDEDNESS)
        {
            vec3 sidedness = doubleSided
                ? (gl_FrontFacing ? vec3(0.1, 0.8, 1.0) : vec3(1.0, 0.45, 0.1))
                : vec3(0.2, 0.85, 0.25);
            WriteForwardColor(vec4(sidedness, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_SHADING_MODEL)
        {
            vec3 modelColor = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_UNLIT)
                ? vec3(1.0, 0.65, 0.1)
                : (material.FeatureFlags & MATERIAL_FEATURE_FOLIAGE) != 0u
                    ? vec3(0.15, 0.85, 0.25)
                    : (material.FeatureFlags & MATERIAL_FEATURE_SUBSURFACE) != 0u
                        ? vec3(1.0, 0.25, 0.55)
                        : vec3(0.2, 0.55, 1.0);
            WriteForwardColor(vec4(modelColor, 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_TRANSPORT_PROFILE)
        {
            vec3 validity = vec3(
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_DIFFUSE_PROFILE_VALID) ? 1.0 : 0.0,
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_EMISSION_PROFILE_VALID) ? 1.0 : 0.0,
                GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_ALPHA_PROFILE_VALID) ? 1.0 : 0.0);
            float quality = clamp(float(material.TransportProfileQuality) / 3.0, 0.0, 1.0);
            if (GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_COMPACT_TEXTURE_FALLBACK))
                validity = mix(validity, vec3(1.0, 0.0, 1.0), 0.5);
            WriteForwardColor(vec4(validity * mix(0.3, 1.0, quality), 1.0));
            return;
        }

        if (debugViewMode == MATERIAL_DEBUG_MATERIAL_REVISIONS)
        {
            // Three deliberately incommensurate multipliers keep independently
            // changing revisions visually distinct: red=material publication,
            // green=texture-content publication, blue=transport profile.
            float materialRevision = fract(float(material.MaterialRevision) * 0.61803398875);
            float textureRevision = fract(float(material.TextureContentRevision) * 0.56984029099);
            float profileRevision = fract(float(material.TransportProfileRevision) * 0.75487766625);
            WriteForwardColor(vec4(materialRevision, textureRevision, profileRevision, 1.0));
            return;
        }
    }

    bool unlit = GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_UNLIT);
    if (unlit)
    {
        // KHR_materials_unlit is base-color only. The trace source was cleared
        // before material evaluation, so unlit surfaces neither receive nor
        // reflect diffuse GI unless a future named transport override opts in.
        if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE ||
            debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR)
        {
            WriteForwardColor(vec4(0.0, 0.0, 0.0, 1.0));
            return;
        }
        WriteForwardColor(vec4(albedo, outputAlpha));
        return;
    }

    vec3 diffuseReflectance = canonicalDiffuseReflectance;

    vec3 diffuseIbl = vec3(0.0);
    vec3 specularIbl = vec3(0.0);
    bool reflectionDebugActive = false;
    vec3 reflectionDebugColor = vec3(0.0);
    vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
        ior,
        specularFactor,
        specularColor);

    EvaluateIbl(
        albedo,
        metallic,
        diffuseReflectance,
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
    vec3 directDiffuseSource = vec3(0.0);
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
        uint cascadeCount = max(uint(ReadShadowIndices().y + 0.5), 1u);
        uint previewCascade = min(ForwardDirectionalShadowPreviewCascade(), cascadeCount - 1u);
        uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + previewCascade;
        float depth = texture(BindlessTextures[nonuniformEXT(textureIndex)], previewUv).r;
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
            directionalDiffuseBase,
            roughness,
            dielectricF0,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            geometryDecal,
            lastShadowFactor,
            lastShadowCascade,
            directLighting,
            directDiffuseSource);
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
                directionalDiffuseBase,
                roughness,
                dielectricF0,
                normal,
                shadowNormal,
                viewDirection,
                fragWorldPosition,
                geometryDecal,
                lastShadowFactor,
                lastShadowCascade,
                directLighting,
                directDiffuseSource);
        }
    }

    if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE)
    {
        WriteForwardColor(vec4(
            clamp(
                max(directDiffuseSource, vec3(0.0)),
                vec3(0.0),
                vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE)),
            1.0));
        return;
    }

    if (debugViewMode == MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR)
    {
        // Both terms came from the same light loop and shadow samples, avoiding
        // a second BRDF implementation or persistent MRT.
        vec3 directSpecular = max(directLighting - directDiffuseSource, vec3(0.0));
        WriteForwardColor(vec4(
            clamp(
                directSpecular,
                vec3(0.0),
                vec3(GI_MATERIAL_MAXIMUM_FINITE_RADIANCE)),
            1.0));
        return;
    }

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

    vec3 finalDiffuseIndirect = vec3(0.0);
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    // Pipeline selection is the authoritative handshake: this native program
    // is bound only after the current-depth cache dispatch and its
    // compute-to-fragment barrier complete. The producer scans a complete 8x8
    // tile when its center is empty, so every opaque fragment has a
    // representative. The cached terms already include DDGI intensity,
    // ownership/leak attenuation, and far-field environment visibility.
    // The material compiler clamps diffuseReflectance, material AO is already
    // normalized above, and the producer stores finite non-negative terms.
    // Compose the same linear transport directly so the hot consumer does not
    // repeat those range checks for every full-resolution fragment.
    // Load at first use to keep both cached RGB fields out of the direct-light
    // loop's live register set. The producer already applied intensity,
    // ownership, leak attenuation, fallback weight, far-field visibility, and
    // the Lambert factor.
    ForwardDdgiReceiverCacheSample cachedGather =
        SampleForwardDdgiReceiverCache(
            gl_FragCoord.xy,
            floatBitsToUint(pc.Push.Time));
    finalDiffuseIndirect =
        (cachedGather.DdgiIrradiance * ambientOcclusion +
         cachedGather.EnvironmentIrradiance * indirectAo) *
        diffuseReflectance;
#elif FORWARD_GLOBAL_ILLUMINATION_DISABLED
    // Benchmark control artifact. This is a separate native program so the
    // A/B delta measures only the incremental cache consumer work and does not
    // retain the sparse-gather graph as dead control flow.
    finalDiffuseIndirect = diffuseIbl * indirectAo;
#else
    bool globalIlluminationEnabled = geometryDecal
        ? ForwardDecalGlobalIlluminationEnabled()
        : ForwardGlobalIlluminationEnabled() != 0u;
    SimpleDdgiParams simpleDdgiParams = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    bool simpleDdgiConfigured = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;
    bool simpleDdgiActive = simpleDdgiConfigured &&
        (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) != 0u;
#if NJULF_DDGI_DETAILED_COUNTERS
    DdgiSampleResult ddgiSample = EmptyDdgiSampleResult();
    vec3 simpleDdgiContributingVolumeColor = vec3(0.0);
    vec3 simpleDdgiSourceCacheIrradiance = vec3(0.0);
    uint simpleDdgiPrimaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    uint simpleDdgiSecondaryVolume = SIMPLE_DDGI_INVALID_VOLUME_INDEX;
    float simpleDdgiSecondVolumeUsed = 0.0;
    float simpleDdgiPrimaryContributionWeight = 0.0;
    float simpleDdgiSecondaryContributionWeight = 0.0;
    uint simpleDdgiCombinedRejectionMask = 0u;
    uint simpleDdgiFirstRejectionReason = SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT;
    uint simpleDdgiNonResidentProbeCount = 0u;
    uint simpleDdgiResidencyTableFlags = 0u;
    uint simpleDdgiResidencyHistoryFlags = 0u;
    uint simpleDdgiResidencyDemandMask = 0u;
    uint simpleDdgiPhysicalPageIndex = 0xffffffffu;
    uint simpleDdgiPageMappingGeneration = 0u;
    float simpleDdgiPageAgeNormalized = 0.0;
    float fallbackWeight = 0.0;
    float nearContactSuppression = 0.0;
    vec3 hybridDebugDiffuse = vec3(0.0);
    vec3 hybridSuppressionMask = vec3(0.0);
    float hybridEffectiveDdgiWeight = 0.0;
#endif
    vec3 ddgiDiffuse = vec3(0.0);
    vec3 finalDdgiDiffuse = vec3(0.0);
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    uint materialTransportProvenance =
        MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN;
#endif

    if (!globalIlluminationEnabled)
    {
        // Preserve inexpensive environment diffuse for transparent materials
        // while avoiding DDGI/legacy probe work that the pass explicitly opted
        // out of.  This also gives feature-isolated rendering a stable fallback.
        finalDiffuseIndirect = diffuseIbl * indirectAo;
#if NJULF_DDGI_DETAILED_COUNTERS
        fallbackWeight = 1.0;
        // This view intentionally removes Simple-DDGI ownership/support and
        // environment substitution so it cannot alias FinalIndirect.
        hybridDebugDiffuse = ddgiDiffuse;
        hybridSuppressionMask = vec3(0.0);
#endif
    }
    else if (simpleDdgiActive)
    {
        // A support-aware result distinguishes an unavailable probe field from
        // legitimate zero irradiance.  Fresh, exposed, and invalid slots
        // are excluded before this reaches lighting composition.
        if (geometryDecal)
            RecordDecalFragmentAttribution(DECAL_ESTIMATED_DDGI_GATHER_COUNTER);
        SimpleDdgiGatherResult simpleGather = EmptySimpleDdgiGatherResult();
        float simpleSupport;
        float simpleDirectionalSupport;
        float simpleRadiometricOwnership;
        float simpleLeakAttenuation;
        vec3 simpleIrradiance;
        // Reflection captures, detailed/provenance artifacts, and any frame
        // where cache creation or dispatch is unavailable bind this exact
        // fallback artifact.
        simpleGather = SampleSimpleDdgiGather(
            simpleDdgiParams,
            fragWorldPosition,
            ddgiNormal,
            viewDirection);
        simpleSupport = clamp(simpleGather.validSupport, 0.0, 1.0);
        simpleDirectionalSupport = clamp(
            simpleGather.directionalSupport,
            0.0,
            1.0);
        simpleRadiometricOwnership =
            SimpleDdgiRadiometricOwnership(simpleGather);
        simpleLeakAttenuation = SimpleDdgiLeakAttenuation(
            simpleGather,
            simpleDdgiParams);
        simpleIrradiance = simpleGather.irradiance;
        float simpleOwnership = simpleRadiometricOwnership * simpleLeakAttenuation;
        // Leak attenuation represents blocked transport, not missing field
        // coverage, so it must not be refilled with the environment complement.
        float simpleFallback = (1.0 - simpleRadiometricOwnership) * simpleDdgiParams.environmentFallbackIntensity;
#if NJULF_DDGI_DETAILED_COUNTERS
        simpleDdgiContributingVolumeColor = simpleGather.contributingVolumeColor;
        simpleDdgiSourceCacheIrradiance = simpleGather.sourceCacheIrradiance;
        simpleDdgiPrimaryVolume = simpleGather.selectedVolume;
        simpleDdgiSecondaryVolume = simpleGather.secondaryVolume;
        simpleDdgiSecondVolumeUsed = simpleGather.secondVolumeUsed;
        simpleDdgiPrimaryContributionWeight = simpleGather.primaryContributionWeight;
        simpleDdgiSecondaryContributionWeight = simpleGather.secondaryContributionWeight;
        simpleDdgiCombinedRejectionMask = simpleGather.combinedRejectionMask;
        simpleDdgiFirstRejectionReason = simpleGather.firstRejectionReason;
        simpleDdgiNonResidentProbeCount = simpleGather.nonResidentProbeCount;
        ddgiSample.irradiance = simpleIrradiance;
        ddgiSample.coverage = simpleGather.spatialCoverage;
        ddgiSample.spatialCoverage = simpleGather.spatialCoverage;
        ddgiSample.supportCoverage = simpleSupport;
        // Data confidence is availability. Directional support is geometric
        // estimator authority and has its own debug view/chain channel.
        ddgiSample.weight = simpleSupport;
        ddgiSample.ownershipConsumed = simpleOwnership;
        ddgiSample.visibility = simpleGather.transportVisibility;
        ddgiSample.visibilityConfidence = simpleGather.transportVisibility;
        ddgiSample.activeProbe = simpleSupport;
        ddgiSample.cascadeIndex = float(simpleGather.selectedVolume);
        // Keep the geometric edge transition separate from the actual sampled
        // contribution.  Unsupported probes can force a fallback even when the
        // fragment is not in an authored-volume transition band.
        ddgiSample.cascadeBlendWeight = clamp(1.0 - simpleGather.transitionWeight, 0.0, 1.0);
        ddgiSample.minProbeSpacing = simpleGather.selectedSpacing;
        ddgiSample.rayBudget = float(simpleDdgiParams.raysPerProbe) / 256.0;
        ddgiSample.leakClamp = simpleGather.transportVisibility;
        ddgiSample.irradianceAtlasConfidence = simpleSupport;
        ddgiSample.qualityConfidence = simpleDirectionalSupport;
#endif

        // Diagnostic sampling is intentionally opt-in.  It rereads probe state and
        // atlases, so doing it per shaded fragment made normal production frames
        // pay the cost of a second gather.
#if NJULF_DDGI_DETAILED_COUNTERS
        float simpleDiagnosticVisibility = simpleGather.transportVisibility;
        float simpleDiagnosticVisibilityMean = 0.0;
        if (IsDdgiDebugView(debugViewMode) || DdgiForwardEstimateDiagnosticPixel())
        {
            SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(
                simpleDdgiParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection);
            ddgiSample.probeIndex = simpleDebug.probeIndex;
            ddgiSample.logicalProbePosition = simpleDebug.logicalProbePosition;
            ddgiSample.relocatedProbePosition = simpleDebug.relocatedProbePosition;
            ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;
            ddgiSample.visibilityMomentVariance = simpleDebug.visibilityMomentVariance;
            ddgiSample.visibilityProbeDistance = simpleDebug.visibilityProbeDistance;
            ddgiSample.visibilityMaxRayDistance = simpleDebug.visibilityMaxRayDistance;
            simpleDiagnosticVisibility = simpleDebug.visibility;
            simpleDiagnosticVisibilityMean = simpleDebug.visibilityMomentMean;
            simpleDdgiResidencyTableFlags = simpleDebug.residencyTableFlags;
            simpleDdgiResidencyHistoryFlags = simpleDebug.residencyHistoryFlags;
            simpleDdgiResidencyDemandMask = simpleDebug.residencyDemandMask;
            simpleDdgiPhysicalPageIndex = simpleDebug.physicalPageIndex;
            simpleDdgiPageMappingGeneration = simpleDebug.pageMappingGeneration;
            simpleDdgiPageAgeNormalized = simpleDebug.pageAgeNormalized;
        }
        AccumulateDdgiVisibilityMomentDiagnostics(
            ddgiSample.visibilityMomentMean,
            ddgiSample.visibilityMomentVariance,
            ddgiSample.visibilityProbeDistance,
            ddgiSample.visibilityMaxRayDistance,
            simpleDiagnosticVisibility,
            ddgiSample.irradianceAtlasConfidence);
#endif

        // Once valid probe data produces a normalized estimate, DDGI owns the
        // spatially covered share. Probe-validity mass selects that estimate but
        // must not premultiply it, or inactive probes next to geometry become a
        // visible dark lattice. Screen-space AO is reserved for the environment
        // fallback because probe visibility already occludes DDGI bounce lighting.
        ddgiDiffuse = ApplyGiMaterialOcclusion(
            EvaluateGiDiffuseFromIrradiance(
                simpleIrradiance * simpleDdgiParams.indirectIntensity,
                diffuseReflectance),
            ambientOcclusion);
        finalDdgiDiffuse = ddgiDiffuse * simpleOwnership;
        vec3 simpleEnvironmentFallback = diffuseIbl;
        bool evaluateFarFieldFallback =
            simpleFallback > SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT ||
            (simpleDdgiParams.flags &
                SIMPLE_DDGI_FLAG_FORCE_LEGACY_FAR_FIELD_FALLBACK) != 0u;
        if (evaluateFarFieldFallback &&
            (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
        {
            simpleEnvironmentFallback *= EstimateFarFieldSkyVisibility(
                fragWorldPosition,
                ddgiNormal,
                simpleDdgiParams,
                DdgiSparseDiagnosticSampleWeight());
        }
        finalDiffuseIndirect = finalDdgiDiffuse + simpleEnvironmentFallback * simpleFallback * indirectAo;
#if NJULF_DDGI_DETAILED_COUNTERS
        fallbackWeight = simpleFallback;
        nearContactSuppression = 1.0 - simpleLeakAttenuation;
        hybridDebugDiffuse = finalDiffuseIndirect;
        hybridSuppressionMask = vec3(simpleSupport, simpleLeakAttenuation, simpleDirectionalSupport);
        hybridEffectiveDdgiWeight = simpleOwnership;
#endif
#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
        materialTransportProvenance =
            ResolveSimpleDdgiMaterialTransportProvenance(
                simpleGather,
                simpleDdgiParams);
#endif

#if NJULF_DDGI_DETAILED_COUNTERS
        HybridDiffuseGiResult simpleHybridDiagnostics;
        simpleHybridDiagnostics.diffuse = finalDiffuseIndirect;
        simpleHybridDiagnostics.ddgiCoverage = simpleGather.spatialCoverage;
        simpleHybridDiagnostics.environmentFallbackWeight = simpleFallback;
        simpleHybridDiagnostics.nearContactSuppression = 1.0 - simpleLeakAttenuation;
        simpleHybridDiagnostics.effectiveDdgiWeight = simpleOwnership;
        simpleHybridDiagnostics.suppressionMask = hybridSuppressionMask;
        AccumulateDdgiForwardEstimateDiagnostics(
            simpleHybridDiagnostics,
            ddgiSample,
            ddgiDiffuse,
            diffuseReflectance,
            geometryDecal);
        AccumulateDdgiInvestigationForwardDiagnostics(
            true,
            simpleDdgiParams,
            fragWorldPosition,
            ddgiNormal,
            viewDirection,
            simpleIrradiance,
            simpleDiagnosticVisibility,
            simpleDiagnosticVisibilityMean,
            finalDdgiDiffuse,
            diffuseIbl,
            finalDiffuseIndirect);
#endif
    }
    else
    {
        // Simple DDGI is the only dynamic-GI backend. During startup, recovery,
        // or unsupported ray-query frames, use its configured environment
        // fallback instead of sampling a second probe implementation.
        float simpleDisabledFallbackWeight = simpleDdgiConfigured
            ? simpleDdgiParams.environmentFallbackIntensity
            : 1.0;
        finalDiffuseIndirect = diffuseIbl * simpleDisabledFallbackWeight * indirectAo;
#if NJULF_DDGI_DETAILED_COUNTERS
        fallbackWeight = simpleDisabledFallbackWeight;
        hybridDebugDiffuse = finalDiffuseIndirect;
        hybridSuppressionMask = vec3(0.0);
        HybridDiffuseGiResult simpleFallbackDiagnostics;
        simpleFallbackDiagnostics.diffuse = finalDiffuseIndirect;
        simpleFallbackDiagnostics.ddgiCoverage = 0.0;
        simpleFallbackDiagnostics.environmentFallbackWeight = fallbackWeight;
        simpleFallbackDiagnostics.nearContactSuppression = 0.0;
        simpleFallbackDiagnostics.effectiveDdgiWeight = 0.0;
        simpleFallbackDiagnostics.suppressionMask = vec3(0.0);
        AccumulateDdgiForwardEstimateDiagnostics(
            simpleFallbackDiagnostics,
            ddgiSample,
            vec3(0.0),
            diffuseReflectance,
            geometryDecal);
        if (simpleDdgiConfigured)
        {
            AccumulateDdgiInvestigationForwardDiagnostics(
                true,
                simpleDdgiParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection,
                vec3(0.0),
                0.0,
                0.0,
                vec3(0.0),
                diffuseIbl,
                finalDiffuseIndirect);
        }
#endif
    }

#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    WriteMaterialTransportProvenance(materialTransportProvenance);
#endif
#endif // FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE

#if !FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)
    {
        WriteForwardColor(vec4(finalDiffuseIndirect, 1.0));
        return;
    }

    vec2 giDebugUv = clamp(gl_FragCoord.xy / max(pc.Push.ScreenDimensions, vec2(1.0)), vec2(0.0), vec2(1.0));
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_OCCUPANCY_SLICE)
    {
        FarFieldClipmapParams farField = ReadFarFieldClipmapParams(uint(FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX));
        uint packed;
        bool missing;
        ReadFarFieldDebugVoxel(farField, giDebugUv, packed, missing);
        vec3 rgb = vec3(
            float((packed >> 0u) & 0xffu),
            float((packed >> 8u) & 0xffu),
            float((packed >> 16u) & 0xffu)) / 255.0;
        float occupied = (packed & 0x80000000u) != 0u ? 1.0 : 0.0;
        vec3 base = missing ? vec3(0.08, 0.015, 0.12) : vec3(0.02);
        WriteForwardColor(vec4(mix(base, rgb, occupied), 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_TRACE_RESULT)
    {
        vec3 traceDir = normalize(fragWorldPosition - pc.Push.CameraPosition);
        float hitT;
        vec3 farNormal;
        vec3 farAlbedo;
        bool hitFar = TraceFarFieldClipmap(pc.Push.CameraPosition, traceDir, 0.0, 512.0, hitT, farNormal, farAlbedo);
        vec3 traceColor = hitFar ? farAlbedo * (abs(farNormal) * 0.35 + vec3(0.65)) : vec3(0.0, 0.02, 0.05);
        WriteForwardColor(vec4(traceColor, 1.0));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY)
    {
        float visibility = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u
            ? EstimateFarFieldSkyVisibility(
                fragWorldPosition,
                geometricNormal,
                simpleDdgiParams,
                DdgiSparseDiagnosticSampleWeight())
            : 1.0;
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SKY_VISIBILITY, vec3(visibility));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW)
    {
        float farShadow = 1.0;
        for (uint lightIndex = 0u; lightIndex < uint(pc.Push.LightCount); lightIndex++)
        {
            GPULight light = ReadLight(lightIndex);
            if (light.Type != 1)
                continue;
            farShadow = EstimateFarFieldSunShadow(fragWorldPosition, normal, normalize(-light.Direction));
            break;
        }
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW, vec3(farShadow));
        return;
    }

#if NJULF_DDGI_DETAILED_COUNTERS
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE)
    {
        // A logarithmic, reference-white-normalized presentation keeps exact
        // zero black while making low but nonzero probe energy visible. The raw
        // linear value remains available through DdgiSampledIrradiance.
        vec3 safeIrradiance = max(ddgiSample.irradiance, vec3(0.0));
        float irradianceLuminance = DdgiDiagnosticLuminance(safeIrradiance);
        vec3 presentedIrradiance = vec3(0.0);
        if (irradianceLuminance > 0.00000001)
        {
            const float logScale = 1024.0;
            float presentedLuminance = log2(1.0 + irradianceLuminance * logScale) /
                log2(1.0 + logScale);
            presentedIrradiance = clamp(
                safeIrradiance * (presentedLuminance / irradianceLuminance),
                vec3(0.0),
                vec3(1.0));
        }
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE,
            presentedIrradiance);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE)
    {
        // Use the same normalized logarithmic presentation as DdgiIrradiance.
        // The hue remains radiometric, so a green direct/emissive source is
        // immediately distinguishable from a grey transport result.
        vec3 safeSource = max(simpleDdgiSourceCacheIrradiance, vec3(0.0));
        float sourceLuminance = DdgiDiagnosticLuminance(safeSource);
        vec3 presentedSource = vec3(0.0);
        if (sourceLuminance > 0.00000001)
        {
            const float logScale = 1024.0;
            float presentedLuminance = log2(1.0 + sourceLuminance * logScale) /
                log2(1.0 + logScale);
            presentedSource = clamp(
                safeSource * (presentedLuminance / sourceLuminance),
                vec3(0.0),
                vec3(1.0));
        }
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_SOURCE_CACHE_RADIANCE,
            presentedSource);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY)
    {
        bool suppressed =
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY) != 0u ||
            (simpleDdgiResidencyHistoryFlags &
                SIMPLE_DDGI_PAGE_HISTORY_SUPPRESSED) != 0u;
        bool resident = simpleDdgiPhysicalPageIndex != 0xffffffffu &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_VALID) != 0u;
        bool published = resident &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_PUBLISHED) != 0u &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_INITIALIZING) == 0u;
        bool demandedMissing = simpleDdgiResidencyDemandMask != 0u &&
            !published;
        vec3 residencyColor = suppressed
            ? vec3(0.85, 0.10, 0.85)
            : (demandedMissing
                ? vec3(1.0, 0.08, 0.02)
                : (!resident
                    ? vec3(0.18)
                    : (!published
                        ? vec3(1.0, 0.72, 0.05)
                        : (simpleDdgiResidencyDemandMask == 0u &&
                           simpleDdgiPageAgeNormalized > 0.0
                            ? vec3(0.05, 0.45, 1.0)
                            : vec3(0.05, 0.95, 0.20)))));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_RESIDENCY,
            residencyColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK)
    {
        float missingShare = clamp(
            float(simpleDdgiNonResidentProbeCount) /
                float(SIMPLE_DDGI_PROBES_PER_PAGE),
            0.0,
            1.0);
        bool suppliedByCoarser = missingShare > 0.0 &&
            simpleDdgiSecondVolumeUsed > 0.5 &&
            simpleDdgiSecondaryContributionWeight > 0.000001;
        vec3 supplierColor = suppliedByCoarser
            ? MeshletDebugColor(simpleDdgiSecondaryVolume + 1u)
            : vec3(0.0);
        vec3 residencyFallbackColor = missingShare <= 0.0
            ? vec3(0.04, 0.65, 0.10)
            : (suppliedByCoarser
                ? mix(supplierColor, vec3(1.0, 0.05, 0.02),
                    0.35 + 0.35 * missingShare)
                : vec3(1.0, 0.0, 0.0));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_RESIDENCY_FALLBACK,
            residencyFallbackColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE)
    {
        bool resident = simpleDdgiPhysicalPageIndex != 0xffffffffu &&
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_VALID) != 0u;
        bool suppressed =
            (simpleDdgiResidencyTableFlags &
                SIMPLE_DDGI_PAGE_TABLE_SUPPRESSED_EMPTY) != 0u ||
            (simpleDdgiResidencyHistoryFlags &
                SIMPLE_DDGI_PAGE_HISTORY_SUPPRESSED) != 0u;
        vec3 ageColor = !resident
            ? (suppressed ? vec3(0.85, 0.10, 0.85) : vec3(0.08))
            : mix(
                vec3(0.0, 0.85, 1.0),
                vec3(1.0, 0.08, 0.0),
                simpleDdgiPageAgeNormalized);
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PAGE_AGE,
            ageColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE)
    {
        vec3 pageColor = simpleDdgiPhysicalPageIndex == 0xffffffffu
            ? vec3(0.12)
            : MeshletDebugColor(
                simpleDdgiPhysicalPageIndex ^
                (simpleDdgiPageMappingGeneration * 0x9e3779b9u));
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_PHYSICAL_PAGE,
            pageColor);
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_RAW_DIFFUSE, clamp(ddgiDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SAMPLED_IRRADIANCE, clamp(ddgiSample.irradiance, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_FINAL_DIFFUSE, clamp(finalDdgiDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CONFIDENCE_BYPASS, clamp(hybridDebugDiffuse, vec3(0.0), vec3(64.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_SUPPRESSION_MASK, clamp(hybridSuppressionMask, vec3(0.0), vec3(1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_EFFECTIVE_WEIGHT, vec3(clamp(hybridEffectiveDdgiWeight, 0.0, 1.0)));
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
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_DATA_CONFIDENCE,
            vec3(clamp(ddgiSample.supportCoverage, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT)
    {
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_DIRECTIONAL_SUPPORT,
            vec3(clamp(ddgiSample.qualityConfidence, 0.0, 1.0)));
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
            clamp(ddgiSample.supportCoverage, 0.0, 1.0),
            clamp(ddgiSample.qualityConfidence, 0.0, 1.0),
            clamp(ddgiSample.visibilityConfidence, 0.0, 1.0)));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT)
    {
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_ENVIRONMENT_FALLBACK_WEIGHT, vec3(clamp(fallbackWeight / 4.0, 0.0, 1.0)));
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
        if (simpleDdgiActive && globalIlluminationEnabled &&
            simpleDdgiCombinedRejectionMask != 0u)
        {
            // R = first failing reason, G/B = low/high portions of the combined
            // nine-bit mask. Supported receivers retain the legacy state view.
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE,
                vec3(
                    float(min(simpleDdgiFirstRejectionReason, SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT)) /
                        float(SIMPLE_DDGI_GATHER_REJECTION_REASON_COUNT),
                    float(simpleDdgiCombinedRejectionMask & 0xffu) / 255.0,
                    float((simpleDdgiCombinedRejectionMask >> 8u) & 0x1u)));
        }
        else
        {
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_PROBE_STATE,
                vec3(ddgiSample.activeProbe, ddgiSample.supportCoverage, ddgiSample.weight));
        }
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
        vec3 cascadeContributorColor = simpleDdgiActive && globalIlluminationEnabled
            ? simpleDdgiContributingVolumeColor
            : MeshletDebugColor(uint(max(ddgiSample.cascadeIndex, 0.0)) + 1u);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_CASCADE_SELECTION, cascadeContributorColor);
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
        float blendWeight = simpleDdgiActive && globalIlluminationEnabled
            ? clamp(simpleDdgiSecondaryContributionWeight, 0.0, 1.0)
            : 0.0;
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_BLEND_WEIGHT,
            vec3(blendWeight));
        return;
    }

    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT ||
        debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK)
    {
        if (!(simpleDdgiActive && globalIlluminationEnabled))
        {
            WriteDdgiDebugColor(debugViewMode, vec3(0.0));
            return;
        }

        bool primaryValid = simpleDdgiPrimaryContributionWeight > 0.000001 &&
            simpleDdgiPrimaryVolume < simpleDdgiParams.volumeCount;
        bool secondaryValid = simpleDdgiSecondaryContributionWeight > 0.000001 &&
            simpleDdgiSecondVolumeUsed > 0.5 &&
            simpleDdgiSecondaryVolume < simpleDdgiParams.volumeCount;
        SimpleDdgiVolume primaryVolume = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            primaryValid ? simpleDdgiPrimaryVolume : 0u);
        SimpleDdgiVolume secondaryVolume = ReadSimpleDdgiVolume(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
            secondaryValid ? simpleDdgiSecondaryVolume : 0u);

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME)
        {
            bool primaryAuthored = primaryValid && primaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED;
            bool secondaryAuthored = secondaryValid && secondaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_AUTHORED;
            uint authoredIndex = primaryAuthored
                ? simpleDdgiPrimaryVolume
                : (secondaryAuthored ? simpleDdgiSecondaryVolume : SIMPLE_DDGI_INVALID_VOLUME_INDEX);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_LOCAL_VOLUME,
                authoredIndex != SIMPLE_DDGI_INVALID_VOLUME_INDEX
                    ? MeshletDebugColor(authoredIndex + 1u)
                    : vec3(0.0));
            return;
        }

        bool primaryRing = primaryValid && primaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_RING;
        bool secondaryRing = secondaryValid && secondaryVolume.kind == SIMPLE_DDGI_VOLUME_KIND_RING;
        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP)
        {
            uint ringIndex = primaryRing
                ? simpleDdgiPrimaryVolume
                : (secondaryRing ? simpleDdgiSecondaryVolume : SIMPLE_DDGI_INVALID_VOLUME_INDEX);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP,
                ringIndex != SIMPLE_DDGI_INVALID_VOLUME_INDEX
                    ? MeshletDebugColor(ringIndex + 1u)
                    : vec3(0.0));
            return;
        }

        if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT)
        {
            float ringWeight =
                (primaryRing ? simpleDdgiPrimaryContributionWeight : 0.0) +
                (secondaryRing ? simpleDdgiSecondaryContributionWeight : 0.0);
            WriteDdgiDebugColor(
                GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_CLIPMAP_BLEND_WEIGHT,
                vec3(clamp(ringWeight, 0.0, 1.0)));
            return;
        }

        float fallback = clamp(simpleDdgiSecondVolumeUsed, 0.0, 1.0);
        WriteDdgiDebugColor(
            GLOBAL_ILLUMINATION_DEBUG_DDGI_GATHER_FALLBACK,
            vec3(fallback, 1.0 - fallback, 0.0));
        return;
    }
#endif
#endif // !FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE

    vec3 color = finalDiffuseIndirect + specularIbl + directLighting + emissive;

    if (hasMaterialExtension)
    {
        float nDotV = max(dot(normal, viewDirection), 0.0);
        GPUEnvironmentData extensionEnvironment = environment;
        if (clearcoatFactor > 0.0 && extensionEnvironment.Enabled != 0u)
        {
            vec3 clearcoatReflection = reflect(-viewDirection, normal);
            float clearcoatMaxLod = max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 clearcoatPrefiltered = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                clearcoatReflection,
                clearcoatRoughness * clearcoatMaxLod);
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
            vec3 transmittedDirection = -normal;
            float lod = roughness * max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 transmittedSample = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                transmittedDirection,
                lod);
            if (dispersion > 0.0)
            {
                vec3 tangent = normalize(fragWorldTangent.xyz);
                vec3 redDirection = normalize(transmittedDirection + tangent * dispersion * 0.012);
                vec3 blueDirection = normalize(transmittedDirection - tangent * dispersion * 0.012);
                transmittedSample.r = SampleEnvironmentPrefilteredRadiance(
                    extensionEnvironment,
                    redDirection,
                    lod).r;
                transmittedSample.b = SampleEnvironmentPrefilteredRadiance(
                    extensionEnvironment,
                    blueDirection,
                    lod).b;
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
