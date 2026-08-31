#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : enable

#ifndef DIRECTIONAL_TRANSPARENT_RAY_QUERY
#define DIRECTIONAL_TRANSPARENT_RAY_QUERY 0
#endif

// Production ThinGlass is a separate native program. It retains the complete
// dielectric material and direct-specular semantics, but asks DDGI only for
// directional reflected radiance: a zero-thickness sheet has no diffuse
// raster lobe to justify sampling or composing diffuse irradiance.
#ifndef FORWARD_THIN_GLASS_ONLY
#define FORWARD_THIN_GLASS_ONLY 0
#endif

#ifndef FORWARD_TRANSPARENT_ROLE_ORDINARY
#define FORWARD_TRANSPARENT_ROLE_ORDINARY 0
#endif
#ifndef FORWARD_TRANSPARENT_ROLE_DECAL
#define FORWARD_TRANSPARENT_ROLE_DECAL 0
#endif
#ifndef FORWARD_TRANSPARENT_ROLE_THICK
#define FORWARD_TRANSPARENT_ROLE_THICK 0
#endif
#if FORWARD_TRANSPARENT_ROLE_ORDINARY + \
    FORWARD_TRANSPARENT_ROLE_DECAL + \
    FORWARD_TRANSPARENT_ROLE_THICK > 1
#error "Only one transparent material role may be compiled at a time."
#endif

#if defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE) || \
    NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
#define FORWARD_TRANSPARENT_REFLECTIONS_ACTIVE 0
#else
#define FORWARD_TRANSPARENT_REFLECTIONS_ACTIVE 1
#endif

#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
#extension GL_EXT_ray_query : require
#endif

#if !defined(NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION) && \
    defined(FORWARD_DDGI_RECEIVER_CACHE_REQUIRED) && \
    FORWARD_DDGI_RECEIVER_CACHE_REQUIRED && \
    (defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE))
// A cache-required opaque program can still encounter surviving alpha-mask
// fragments. Keep those fragments on the exact B1 ownership path while
// ordinary opaque fragments consume the resolved receiver cache.
#define NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION 1
#endif

#ifndef NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#define NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION 0
#endif

#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_arithmetic : require
#extension GL_KHR_shader_subgroup_ballot : require

// Forward cache optimizations are Vulkan specialization constants: each
// pipeline receives a fixed mask, so native compilation removes inactive
// paths without multiplying embedded SPIR-V artifacts.
const uint NJULF_PERFORMANCE_HYBRID_PROJECTION_ELISION = 1u << 3u;
const uint NJULF_PERFORMANCE_SCREEN_LOCAL_RECEIVER = 1u << 4u;
const uint NJULF_PERFORMANCE_SPLIT_HYBRID_FORWARD = 1u << 5u;
const uint NJULF_RECEIVER_CACHE_LANE_COMBINED = 0u;
const uint NJULF_RECEIVER_CACHE_LANE_ACCEPTED = 1u;
const uint NJULF_RECEIVER_CACHE_LANE_EXACT_FALLBACK = 2u;
layout(constant_id = 30) const uint
    NjulfReceiverCacheLane = NJULF_RECEIVER_CACHE_LANE_COMBINED;
layout(constant_id = 31) const uint
    NjulfPerformanceOptimizationMask = 0x7fffffffu;

bool NjulfPerformanceOptimizationEnabled(uint feature)
{
    return (NjulfPerformanceOptimizationMask & feature) == feature;
}

bool NjulfReceiverCacheAcceptedLane()
{
    return NjulfPerformanceOptimizationEnabled(
            NJULF_PERFORMANCE_SPLIT_HYBRID_FORWARD) &&
        NjulfReceiverCacheLane == NJULF_RECEIVER_CACHE_LANE_ACCEPTED;
}

bool NjulfReceiverCacheExactFallbackLane()
{
    return NjulfPerformanceOptimizationEnabled(
            NJULF_PERFORMANCE_SPLIT_HYBRID_FORWARD) &&
        NjulfReceiverCacheLane ==
            NJULF_RECEIVER_CACHE_LANE_EXACT_FALLBACK;
}

// Ordinary forward variants consume the current frame's depth prepass. The
// reduced C5 source owns a fresh depth target and must run coverage before its
// late depth write so discarded alpha-mask samples cannot occlude later work.
#if !defined(NJULF_C5_TRACE_RESOLUTION_SOURCE) || \
    !NJULF_C5_TRACE_RESOLUTION_SOURCE
layout(early_fragment_tests) in;
#endif

#ifndef NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#define NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT 0
#endif

// C5 owns a dedicated opaque/alpha-mask MRT variant.  It is never inferred
// from SceneColor: output location one contains only shadowed direct diffuse
// plus material emissive in scene-linear radiance.
#ifndef NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
#define NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT 0
#endif

#ifndef NJULF_C5_DIRECT_SOURCE_SEMANTICS_VERSION
#define NJULF_C5_DIRECT_SOURCE_SEMANTICS_VERSION 0
#endif

#ifndef NJULF_C5_TRACE_RESOLUTION_SOURCE
#define NJULF_C5_TRACE_RESOLUTION_SOURCE 0
#endif

#ifndef NJULF_C4_RECEIVER_OUTPUT
#define NJULF_C4_RECEIVER_OUTPUT 0
#endif

#ifndef NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
#define NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT 0
#endif

#ifndef NJULF_HYBRID_REFLECTION_RECEIVER_SEMANTICS_VERSION
#define NJULF_HYBRID_REFLECTION_RECEIVER_SEMANTICS_VERSION 0
#endif

#ifndef NJULF_C4_RECEIVER_SEMANTICS_VERSION
#define NJULF_C4_RECEIVER_SEMANTICS_VERSION 0
#endif

#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
#if defined(FORWARD_WEIGHTED_OIT) || \
    (!defined(FORWARD_OPAQUE) && !defined(FORWARD_SIMPLE_OPAQUE))
#error "C5 direct source is valid only for opaque or alpha-mask forward variants."
#endif
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#error "C5 direct source cannot share the forward MRT variant with material provenance."
#endif
#if NJULF_C5_DIRECT_SOURCE_SEMANTICS_VERSION != 5
#error "C5 direct source shader semantics version mismatch."
#endif
#elif NJULF_C5_DIRECT_SOURCE_SEMANTICS_VERSION != 0
#error "C5 direct source semantics version requires the dedicated output variant."
#endif

#if NJULF_C4_RECEIVER_OUTPUT
#if defined(FORWARD_WEIGHTED_OIT) || \
    (!defined(FORWARD_OPAQUE) && !defined(FORWARD_SIMPLE_OPAQUE))
#error "C4 receiver payload is valid only for opaque or alpha-mask forward variants."
#endif
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#error "C4 receiver payload cannot share the forward MRT variant with material provenance."
#endif
#if NJULF_C4_RECEIVER_SEMANTICS_VERSION != 1
#error "C4 receiver payload shader semantics version mismatch."
#endif
#elif NJULF_C4_RECEIVER_SEMANTICS_VERSION != 0
#error "C4 receiver semantics version requires the dedicated output variant."
#endif

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
#if defined(FORWARD_WEIGHTED_OIT) || \
    (!defined(FORWARD_OPAQUE) && !defined(FORWARD_SIMPLE_OPAQUE))
#error "Hybrid reflection receivers are valid only for opaque and alpha-mask variants."
#endif
#if NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
#error "Hybrid reflection receivers cannot share material-provenance MRT ownership."
#endif
#if NJULF_HYBRID_REFLECTION_RECEIVER_SEMANTICS_VERSION != 2
#error "Hybrid reflection receiver shader semantics version mismatch."
#endif
#elif NJULF_HYBRID_REFLECTION_RECEIVER_SEMANTICS_VERSION != 0
#error "Hybrid reflection semantics version requires the dedicated output variant."
#endif

#include "common.glsl"
#include "automatic_planar_reflection.glsl"

#ifndef NJULF_GTAO_BENT_NORMAL_LIGHTING
#define NJULF_GTAO_BENT_NORMAL_LIGHTING 1
#endif
#include "area_lighting.glsl"
#include "directional_csm_sampling.glsl"
#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
#include "c5_receiver_payload.glsl"
#endif
#if NJULF_C4_RECEIVER_OUTPUT
#include "c4_receiver_payload.glsl"
#endif
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
#include "hybrid_reflection_payload.glsl"
#endif
#include "gi_material_transport.glsl"
#include "dielectric_transport.glsl"
#include "material_coverage.glsl"
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
layout(set = 2, binding = 0) uniform accelerationStructureEXT SceneTlas;
#include "directional_ray_visibility.glsl"
#include "ray_query_surface.glsl"
#if !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#include "thick_transmission_transport.glsl"
#endif
#endif
// Detailed captures need representative gather counts, not one globally
// contended atomic per shaded fragment.  Preserve an estimated full-resolution
// count while sampling one pixel from each 16x16 screen tile.
#define SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT ((((uint(gl_FragCoord.x) & 15u) == 0u) && ((uint(gl_FragCoord.y) & 15u) == 0u)) ? 256u : 0u)
#define SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE (((uint(gl_FragCoord.x) & 7u) == 0u) && ((uint(gl_FragCoord.y) & 7u) == 0u))
#define SIMPLE_DDGI_RECEIVER_COVERAGE_HASH (((uint(gl_FragCoord.x) >> 3u) * 73856093u) ^ ((uint(gl_FragCoord.y) >> 3u) * 19349663u))
#define SIMPLE_DDGI_DIRECTIONAL_RADIANCE_RECEIVER 1
// Reflections consume the stable low-frequency L1 prefix. The full L2 record
// remains the producer/transport representation, while SSR and ray queries
// provide the high-frequency scene detail that L1 deliberately omits. The
// surface-aware cache is the exception: its common path projects full L2 into
// the compact lattice, so rejected fragments retain a matching full-L2 exact
// fallback rather than crossing representation at cache boundaries.
#if !FORWARD_DDGI_RECEIVER_CACHE
#define SIMPLE_DDGI_DIRECTIONAL_L1_PREVIEW_RECEIVER 1
#endif
#if FORWARD_THIN_GLASS_ONLY
// Window panes commonly overlap in screen space. One residency touch per 8x8
// tile retains transparent-only pages without issuing the same atomic for
// every layer and every fragment. The gather itself remains per fragment so
// normals, parallax and confidence do not become blocky.
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 7u) == 0u) && ((uint(gl_FragCoord.y) & 7u) == 0u))
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_TRANSPARENT
#define SIMPLE_DDGI_DIRECTIONAL_ONLY_RECEIVER 1
#define SIMPLE_DDGI_TETRAHEDRAL_DIRECTIONAL_RECEIVER 1
#elif defined(FORWARD_WEIGHTED_OIT)
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 1u) == 0u) && ((uint(gl_FragCoord.y) & 1u) == 0u))
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_TRANSPARENT
#elif defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE)
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 7u) == 0u) && ((uint(gl_FragCoord.y) & 7u) == 0u))
// Current opaque depth owns proactive resident-page retention. Opaque forward
// contributes only compact-publication misses; avoiding resident-touch atomics
// here keeps the authoritative gather path read-only in the common case.
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 0u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 1
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_OPAQUE
#else
// The generic forward artifact is the sorted-transparent pipeline.
#define SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 1u) == 0u) && ((uint(gl_FragCoord.y) & 1u) == 0u))
#define SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1
#define SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u
#define SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 0
#define SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS SIMPLE_DDGI_RECEIVER_CONSUMER_TRANSPARENT
#endif
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
#define NJULF_DDGI_VISUAL_DEBUG_GATHER_DATA 1
#endif
#include "ddgi_simple_shared.glsl"
#if FORWARD_DDGI_RECEIVER_CACHE
#include "forward_ddgi_receiver_cache.glsl"
#endif
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
#undef NJULF_DDGI_VISUAL_DEBUG_GATHER_DATA
#endif
#undef SIMPLE_DDGI_RECEIVER_CONSUMER_FLAGS
#undef SIMPLE_DDGI_OPAQUE_GATHER_ORACLE
#undef SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET
#undef SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT
#undef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE
#undef SIMPLE_DDGI_RECEIVER_COVERAGE_HASH
#undef SIMPLE_DDGI_RECEIVER_CONTRIBUTION_SAMPLE
#undef SIMPLE_DDGI_GATHER_DIAGNOSTIC_SAMPLE_WEIGHT
#undef SIMPLE_DDGI_DIRECTIONAL_RADIANCE_RECEIVER
#if !FORWARD_DDGI_RECEIVER_CACHE
#undef SIMPLE_DDGI_DIRECTIONAL_L1_PREVIEW_RECEIVER
#endif
#if FORWARD_THIN_GLASS_ONLY
#undef SIMPLE_DDGI_TETRAHEDRAL_DIRECTIONAL_RECEIVER
#undef SIMPLE_DDGI_DIRECTIONAL_ONLY_RECEIVER
#endif
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#include "ddgi_receiver_feedback_source_abi.glsl"
#include "ddgi_receiver_feedback_producer.glsl"
#include "ddgi_receiver_feedback_surface_producer.glsl"
#endif
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
#elif NJULF_C5_TRACE_RESOLUTION_SOURCE
layout(location = 0) out vec4 outDirectDiffuseAndEmissive;
layout(location = 1) out uvec4 outNearFieldReceiverPayload;
#else
layout(location = 0) out vec4 outColor;
#if NJULF_C4_RECEIVER_OUTPUT && NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
// Combined advanced-GI ABI. Keep the C4 payload at location one so the
// standalone C4 and combined variants share an identical producer binding;
// C5 shifts its two outputs to the following contiguous locations.
layout(location = 1) out uvec4 outGiCausticReceiverPayload;
layout(location = 2) out vec4 outDirectDiffuseAndEmissive;
layout(location = 3) out uvec4 outNearFieldReceiverPayload;
#elif NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
layout(location = 1) out vec4 outDirectDiffuseAndEmissive;
layout(location = 2) out uvec4 outNearFieldReceiverPayload;
#elif NJULF_C4_RECEIVER_OUTPUT
layout(location = 1) out uvec4 outGiCausticReceiverPayload;
#elif NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
layout(location = 1) out float outMaterialTransportProvenance;
#endif
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
#if NJULF_C4_RECEIVER_OUTPUT && NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
layout(location = 4) out uvec4 outHybridReflectionReceiverPayload;
layout(location = 5) out uvec2 outHybridReflectionLobeExtension;
#elif NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
layout(location = 3) out uvec4 outHybridReflectionReceiverPayload;
layout(location = 4) out uvec2 outHybridReflectionLobeExtension;
#elif NJULF_C4_RECEIVER_OUTPUT
layout(location = 2) out uvec4 outHybridReflectionReceiverPayload;
layout(location = 3) out uvec2 outHybridReflectionLobeExtension;
#else
layout(location = 1) out uvec4 outHybridReflectionReceiverPayload;
layout(location = 2) out uvec2 outHybridReflectionLobeExtension;
#endif
#endif
#endif

layout(push_constant) uniform ForwardPushConstantBlock
{
    GPUForwardPushConstants Push;
} pc;

vec2 ForwardScreenPixel()
{
#if NJULF_C5_TRACE_RESOLUTION_SOURCE
    uint scaleCode = (pc.Push.DiagnosticFlags >> 26u) & 0x3u;
    float scale = scaleCode == 2u
        ? 0.5
        : scaleCode == 1u ? 0.25 : 0.125;
    vec2 fullExtent = max(pc.Push.ScreenDimensions, vec2(1.0));
    vec2 traceExtent = max(ceil(fullExtent * scale), vec2(1.0));
    return gl_FragCoord.xy * fullExtent / traceExtent;
#else
    return gl_FragCoord.xy;
#endif
}

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
const uint DEBUG_VIEW_DIRECTIONAL_RAY_MASK = 8u;
const uint DEBUG_VIEW_DIRECTIONAL_RAY_HIT_DISTANCE = 9u;
const uint DEBUG_VIEW_DIRECTIONAL_RAY_CANDIDATE_COUNT = 10u;
const uint DEBUG_VIEW_DIRECTIONAL_RAY_SCENE_RESIDENCY = 11u;
const uint DEBUG_VIEW_DIRECTIONAL_CSM_RAY_DIFFERENCE = 12u;
const uint DEBUG_VIEW_DIRECTIONAL_HISTORY_CONFIDENCE = 13u;
const uint DEBUG_VIEW_DIRECTIONAL_HISTORY_REJECTION = 14u;
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
const uint GLOBAL_ILLUMINATION_DEBUG_DDGI_RECEIVER_CACHE_REJECTION = 147u;
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
const uint REFLECTION_DEBUG_DDGI_DIRECTIONAL_RADIANCE_LOBE = 11u;
const uint REFLECTION_DEBUG_SOURCE_OWNERSHIP = 12u;
const uint REFLECTION_DEBUG_CONFIDENCE = 13u;
const uint REFLECTION_DEBUG_SOURCE_SELECTION = 14u;
const uint REFLECTION_DEBUG_DETAIL_BUDGET = 15u;
const uint REFLECTION_DEBUG_RECEIVER_MATERIAL = 16u;
const uint REFLECTION_DEBUG_ROUGHNESS_INPUTS = 17u;
const uint FORWARD_REFLECTION_SOURCE_NONE = 0u;
const uint FORWARD_REFLECTION_SOURCE_SSR = 1u;
const uint FORWARD_REFLECTION_SOURCE_RAY_QUERY = 2u;
const uint FORWARD_REFLECTION_SOURCE_DDGI = 3u;
const uint FORWARD_REFLECTION_SOURCE_LOCAL_PROBE = 4u;
const uint FORWARD_REFLECTION_SOURCE_ENVIRONMENT = 5u;
const uint FORWARD_REFLECTION_SOURCE_PLANAR = 6u;
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

// A simple material program omits extension and authored local-probe code but
// does not change the receiver class. Opaque fast paths opt in implicitly;
// geometry decals use the same specialization while retaining transparent
// coverage, blending, and DDGI feedback semantics.
#ifndef FORWARD_SIMPLE_MATERIAL
#define FORWARD_SIMPLE_MATERIAL FORWARD_SIMPLE_OPAQUE
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

#ifndef FORWARD_DDGI_RECEIVER_CACHE_LEGACY
#define FORWARD_DDGI_RECEIVER_CACHE_LEGACY 0
#endif

// The cache-required opaque hybrid artifact has a deliberately exclusive GI
// ownership contract: the receiver cache owns admitted diffuse/visibility and
// the deferred hybrid pass owns indirect specular.  Keep this opt-in so cache
// artifacts without the hybrid receiver retain their compact directional L2
// reconstruction.
#ifndef FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED
#define FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED 0
#endif

#ifndef NJULF_DDGI_RECEIVER_CACHE_DEBUG_VIEW
#define NJULF_DDGI_RECEIVER_CACHE_DEBUG_VIEW 0
#endif

#ifndef FORWARD_GLOBAL_ILLUMINATION_DISABLED
#define FORWARD_GLOBAL_ILLUMINATION_DISABLED 0
#endif

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED && !FORWARD_DDGI_RECEIVER_CACHE
#error FORWARD_DDGI_RECEIVER_CACHE_REQUIRED requires FORWARD_DDGI_RECEIVER_CACHE
#endif

#if FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED && \
    (!FORWARD_DDGI_RECEIVER_CACHE_REQUIRED || \
     !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT || \
     (!defined(FORWARD_OPAQUE) && !defined(FORWARD_SIMPLE_OPAQUE)))
#error FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED requires an opaque cache-required hybrid receiver artifact
#endif

#if FORWARD_DDGI_RECEIVER_CACHE_LEGACY && \
    !FORWARD_DDGI_RECEIVER_CACHE_REQUIRED
#error FORWARD_DDGI_RECEIVER_CACHE_LEGACY requires the cache-required artifact
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
#if FORWARD_GLOBAL_ILLUMINATION_DISABLED || FORWARD_THIN_GLASS_ONLY || \
    FORWARD_DDGI_RECEIVER_CACHE_LEGACY
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
    return (pc.Push.DebugAndAoFlags >> 16u) & 0x3fu;
}

uint ForwardAmbientOcclusionBentNormalMode()
{
    return (pc.Push.DebugAndAoFlags >> 22u) & 0x03u;
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

uint ForwardReflectionCaptureLayer()
{
    return (pc.Push.DiagnosticFlags >> 16u) & 0x1fffu;
}

bool ForwardAutomaticPlanarCaptureEnabled()
{
    return ForwardReflectionCaptureEnabled() &&
        (ForwardReflectionCaptureLayer() &
            AUTOMATIC_PLANAR_CAPTURE_LAYER_FLAG) != 0u;
}

uint ForwardAutomaticPlanarCaptureSlot()
{
    return ForwardReflectionCaptureLayer() &
        (AUTOMATIC_PLANAR_CAPTURE_LAYER_FLAG - 1u);
}

void EnforceAutomaticPlanarCaptureClip()
{
    if (ForwardAutomaticPlanarCaptureEnabled() &&
        AutomaticPlanarShouldDiscardCaptureFragment(
            pc.Push.CurrentFrameIndex,
            ForwardAutomaticPlanarCaptureSlot(),
            fragObjectIndex,
            fragWorldPosition,
            pc.Push.CameraPosition))
    {
        discard;
    }
}

bool ForwardDdgiReceiverCacheEnabled()
{
    return (pc.Push.DiagnosticFlags & (1u << 30u)) != 0u;
}

bool ForwardGeometricSpecularAntialiasingEnabled()
{
    return (pc.Push.DiagnosticFlags & (1u << 29u)) != 0u;
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

bool ForwardThickTransmissionRayQueryEnabled()
{
    return (pc.Push.DiagnosticFlags & (1u << 7u)) != 0u;
}

bool ForwardThickTransmissionDispersionEnabled()
{
    return (pc.Push.DiagnosticFlags & (1u << 10u)) != 0u;
}

uint ForwardEffectiveReflectionMode()
{
    return (pc.Push.DiagnosticFlags >> 11u) & 0x07u;
}

bool ForwardTransparentSampleReflections()
{
    return (pc.Push.DiagnosticFlags & (1u << 14u)) != 0u;
}

bool ForwardOpaqueSceneColorSnapshotAvailable()
{
    return (pc.Push.DiagnosticFlags & (1u << 15u)) != 0u;
}

bool ForwardMaterialSamplesSceneReflections(
    GPUMaterialData material,
    bool geometryDecal)
{
    uint blendMode = uint(max(round(material.OcclusionBinding.z), 0.0));
    return !geometryDecal &&
        !GiMaterialHasFlag(material.TransportFlags, GI_MATERIAL_UNLIT) &&
        blendMode >= 2u && blendMode <= 3u;
}

uint ForwardThickTransmissionMaximumInterfaces()
{
    return (pc.Push.HiZMipCount & 0x07u) + 1u;
}

uint ForwardThickTransmissionMaximumMediaDepth()
{
    return ((pc.Push.HiZMipCount >> 3u) & 0x03u) + 1u;
}

uint ForwardThickTransmissionMaximumCandidatesPerInterface()
{
    return ((pc.Push.HiZMipCount >> 5u) & 0x3fu) + 1u;
}

#if DIRECTIONAL_TRANSPARENT_RAY_QUERY && \
    !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
uint ForwardThickTransmissionTaskBudget()
{
    return pc.Push.OcclusionCullingEnabled >> 2u;
}

bool ForwardTryReserveThickTransmissionTask()
{
    uint taskBudget = ForwardThickTransmissionTaskBudget();
    if (taskBudget == 0u)
        return false;

    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        pc.Push.CurrentFrameIndex;
    uint taskIndex = atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            THICK_TRANSMISSION_TASK_COUNTER],
        1u);
    return taskIndex < taskBudget;
}
#endif

bool ForwardLayeredReceiverAcceptsShadows(bool geometryDecal)
{
    if (ForwardDrawBufferBaseIndex(
            pc.Push.MeshletDrawBufferBaseIndex) !=
        uint(TRANSPARENT_MESHLET_DRAW_BUFFER_BASE_INDEX))
        return true;
    return geometryDecal
        ? ForwardDecalReceiveShadows()
        : ForwardTransparentReceiveShadows() != 0u;
}

bool DdgiSparseDiagnosticPixel()
{
    uvec2 pixel = uvec2(max(ForwardScreenPixel(), vec2(0.0)));
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

vec4 SampleMaterialTextureFootprint(
    int textureIndex,
    vec2 uv,
    float footprintScale)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX &&
        textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    int safeIndex = valid ? textureIndex : DEFAULT_BLACK_TEXTURE;
    float scale = max(footprintScale, 1.0);
    return textureGrad(
        BindlessTextures[nonuniformEXT(safeIndex)],
        uv,
        dFdx(uv) * scale,
        dFdy(uv) * scale);
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
    vec2 uv = clamp(
        ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0)),
        vec2(0.0),
        vec2(1.0));
    return clamp(textureLod(
        BindlessTextures[
            nonuniformEXT(AMBIENT_OCCLUSION_BLURRED_TEXTURE_INDEX)],
        uv,
        0.0).r, 0.0, 1.0);
}

float SampleScreenSpaceAo()
{
    if (ForwardAmbientOcclusionEnabled() == 0u)
        return 1.0;
    return SampleScreenSpaceAoDirect();
}

#if NJULF_GTAO_BENT_NORMAL_LIGHTING
vec3 DecodeGtaoOctahedralNormal(vec2 encoded)
{
    vec2 oct = clamp(encoded, vec2(-1.0), vec2(1.0));
    vec3 normal = vec3(oct, 1.0 - abs(oct.x) - abs(oct.y));
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) * sign(normal.xy);
    }
    float lengthSquared = dot(normal, normal);
    return lengthSquared > 1.0e-8
        ? normal * inversesqrt(lengthSquared)
        : vec3(0.0, 0.0, 1.0);
}

bool TryResolveIndirectDiffuseNormal(
    vec3 shadingNormal,
    out vec3 resolvedNormal)
{
    resolvedNormal = shadingNormal;
    if (ForwardAmbientOcclusionBentNormalMode() == 0u)
        return false;
    vec2 uv = clamp(
        ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0)),
        vec2(0.0),
        vec2(1.0));
    vec4 payload = textureLod(
        BindlessTextures[nonuniformEXT(GTAO_FILTERED_TEXTURE_INDEX)],
        uv,
        0.0);
    if (any(isnan(payload)) || any(isinf(payload)) || payload.w <= 0.0)
        return false;
    vec3 viewBentNormal = DecodeGtaoOctahedralNormal(payload.xy);
    vec3 worldBentNormal = MulRowMajor(
        vec4(viewBentNormal, 0.0),
        pc.Push.InverseViewMatrix).xyz;
    float lengthSquared = dot(worldBentNormal, worldBentNormal);
    if (lengthSquared <= 1.0e-8)
        return false;
    worldBentNormal *= inversesqrt(lengthSquared);
    float hemisphere = dot(worldBentNormal, shadingNormal);
    if (hemisphere <= 0.0)
        return false;
    resolvedNormal = normalize(mix(shadingNormal, worldBentNormal,
        smoothstep(0.0, 0.25, hemisphere)));
    return true;
}
#endif

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
        bool ignoredRefinementOrBaseFallback;
        bool diagnosticInVolume = SelectSimpleDdgiVolume(
            simpleParams,
            worldPosition,
            diagnosticVolumeIndex,
            diagnosticVolume,
            diagnosticEdgeWeight,
            ignoredRefinementOrBaseFallback);
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
    vec4 compared = DirectionalShadowCompareGather(
        DirectionalShadowGatherDepthBlock(
            textureIndex,
            baseTexel,
            maxTexel,
            safeMapSize),
        receiverDepth);
    vec4 tapWeights = vec4(
        (1.0 - weights.x) * (1.0 - weights.y),
        weights.x * (1.0 - weights.y),
        (1.0 - weights.x) * weights.y,
        weights.x * weights.y);
    return dot(compared, tapWeights);
}

float DirectionalShadowPcfAxisWeight(
    int offset,
    int radius,
    float fraction,
    int filterMode)
{
    if (filterMode == 1)
    {
        return max(
            float(radius + 1) - abs(float(offset) - fraction),
            0.0);
    }
    return offset == -radius
        ? 1.0 - fraction
        : (offset == radius + 1 ? fraction : 1.0);
}

float SampleDirectionalShadowPcf(
    uint textureIndex,
    vec2 uv,
    float receiverDepth,
    float mapSize,
    int radius,
    int filterMode)
{
    float safeMapSize = max(mapSize, 1.0);
    vec2 texelPosition = uv * safeMapSize - vec2(0.5);
    ivec2 baseTexel = ivec2(floor(texelPosition));
    ivec2 maxTexel = ivec2(max(int(safeMapSize) - 1, 0));
    vec2 weights = fract(texelPosition);
    int safeRadius = clamp(radius, 1, 3);
    float lit = 0.0;
    float totalWeight = 0.0;

    // Adjacent bilinear PCF taps share their inner texels. Accumulating the
    // unique (2r + 2)^2 grid preserves the same filter while avoiding four
    // independent fetches for every tap.
    for (int y = -safeRadius; y <= safeRadius + 1; y += 2)
    {
        float weightY0 = DirectionalShadowPcfAxisWeight(
            y, safeRadius, weights.y, filterMode);
        float weightY1 = DirectionalShadowPcfAxisWeight(
            y + 1, safeRadius, weights.y, filterMode);
        for (int x = -safeRadius; x <= safeRadius + 1; x += 2)
        {
            float weightX0 = DirectionalShadowPcfAxisWeight(
                x, safeRadius, weights.x, filterMode);
            float weightX1 = DirectionalShadowPcfAxisWeight(
                x + 1, safeRadius, weights.x, filterMode);
            vec4 tapWeights = vec4(
                weightX0 * weightY0,
                weightX1 * weightY0,
                weightX0 * weightY1,
                weightX1 * weightY1);
            vec4 compared = DirectionalShadowCompareGather(
                DirectionalShadowGatherDepthBlock(
                    textureIndex,
                    baseTexel + ivec2(x, y),
                    maxTexel,
                    safeMapSize),
                receiverDepth);
            lit += dot(compared, tapWeights);
            totalWeight += dot(tapWeights, vec4(1.0));
        }
    }

    return lit / max(totalWeight, 0.00001);
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

            float sampledDepth = DirectionalShadowFetchDepth(
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
    vec3 worldPosition,
    vec3 geometricNormal,
    float mapSize,
    int radius,
    vec4 worldTexelSizes,
    vec4 filterAndBias,
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

    float normalBias = ReadShadowSettings().y;
    int biasMode = int(clamp(round(filterAndBias.y), 0.0, 1.0));
    if (biasMode == 1)
    {
        float worldTexelSize = max(worldTexelSizes[int(cascade)], 0.0);
        normalBias = min(
            worldTexelSize * max(filterAndBias.z, 0.0),
            max(filterAndBias.w, 0.0));
    }
    vec3 biasedWorldPosition = worldPosition + geometricNormal * normalBias;
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
    int resolvedRadius = ResolveDirectionalShadowPcfRadius(
        cascade,
        radius,
        worldTexelSizes);
    diagnosticReceiverDepth = receiverDepth;
    if (collectDepthDiagnostics)
    {
        InspectDirectionalShadowFootprint(
            textureIndex,
            uv,
            mapSize,
            resolvedRadius,
            diagnosticMinimumSampledDepth,
            diagnosticMaximumSampledDepth);
    }

    if (resolvedRadius <= 0)
    {
        shadow = SampleDirectionalShadowTap(textureIndex, uv, receiverDepth, mapSize);
        return true;
    }

    shadow = SampleDirectionalShadowPcf(
        textureIndex,
        uv,
        receiverDepth,
        mapSize,
        resolvedRadius,
        int(clamp(round(filterAndBias.x), 0.0, 1.0)));
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

#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
void AddDirectionalTransparentRayCounter(uint counter, uint value)
{
#if NJULF_DIRECTIONAL_SHADOW_DETAILED_COUNTERS
    if (!DirectionalShadowReceiverCountersEnabled() ||
        value == 0u || counter >= 64u)
        return;
    uint frameSlot = min(
        pc.Push.CurrentFrameIndex,
        uint(FRAMES_IN_FLIGHT - 1));
    uint bufferIndex = uint(DIRECTIONAL_SHADOW_COUNTER_BUFFER_BASE_INDEX) +
        frameSlot;
    atomicAdd(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counter],
        value);
#endif
}

float EvaluateDirectionalTransparentRay(
    uint lightIndex,
    vec3 worldPosition,
    vec3 geometricNormal,
    uint effectiveMode)
{
    vec4 shadowIndices = ReadShadowIndices();
    if (shadowIndices.x < 0.5 ||
        int(round(shadowIndices.w)) != int(lightIndex) ||
        !ForwardLayeredReceiverAcceptsShadows(false))
    {
        return 1.0;
    }

    GPULight light = ReadLight(lightIndex);
    vec3 centerDirection = -light.Direction;
    float directionLengthSquared = dot(centerDirection, centerDirection);
    if (!DirectionalRayFinite(centerDirection) ||
        directionLengthSquared <= 1.0e-8 ||
        !DirectionalRayFinite(worldPosition))
    {
        AddDirectionalTransparentRayCounter(18u, 1u);
        return 1.0;
    }
    centerDirection *= inversesqrt(directionLengthSquared);

    vec4 modeAndDistance = ReadDirectionalShadowModeAndRayDistance();
    vec4 shadowSettings = ReadShadowSettings();
    float maximumDistance = effectiveMode == 1u
        ? max(modeAndDistance.z, 0.0)
        : max(ReadShadowCascadeTransitionData().z, 0.0);
    if (!DirectionalRayFinite(maximumDistance) || maximumDistance <= 0.0)
        return 1.0;

    vec3 normal = geometricNormal;
    float normalLengthSquared = dot(normal, normal);
    if (!DirectionalRayFinite(normal) || normalLengthSquared <= 1.0e-10)
    {
        AddDirectionalTransparentRayCounter(18u, 1u);
        return 1.0;
    }
    normal *= inversesqrt(normalLengthSquared);
    if (dot(normal, centerDirection) < -0.25)
        return 1.0;
    if (dot(normal, centerDirection) < 0.0)
        normal = -normal;

    float footprint = max(
        length(dFdx(worldPosition)),
        length(dFdy(worldPosition)));
    if (!DirectionalRayFinite(footprint) || footprint <= 1.0e-7)
        footprint = max(length(worldPosition - pc.Push.CameraPosition) /
            max(max(pc.Push.ScreenDimensions.x, pc.Push.ScreenDimensions.y), 1.0),
            0.0001);

    float coordinateScale = max(
        max(abs(worldPosition.x), max(abs(worldPosition.y), abs(worldPosition.z))),
        1.0);
    float normalEpsilon = clamp(
        coordinateScale * 1.0e-7,
        0.0005,
        0.02);
    float rayEpsilon = clamp(
        max(0.0005, maximumDistance * 1.0e-6),
        0.0005,
        0.01);
    vec3 origin = worldPosition + normal * normalEpsilon +
        centerDirection * rayEpsilon;
    float boundedMaximum;
    if (!DirectionalIntersectQualifiedBounds(
            origin,
            centerDirection,
            maximumDistance,
            boundedMaximum))
    {
        AddDirectionalTransparentRayCounter(18u, 1u);
        return 1.0;
    }

    vec4 temporalAndSampling = ReadDirectionalShadowTemporalAndSampling();
    uint sampleCount = effectiveMode == 3u
        ? clamp(uint(round(temporalAndSampling.w)), 1u, 4u)
        : 1u;
    float visibility = 0.0;
    uint totalCandidates = 0u;
    uint totalAlphaSamples = 0u;
    uint hitCount = 0u;
    bool capHit = false;
    uvec2 pixel = uvec2(max(floor(ForwardScreenPixel()), vec2(0.0)));
    for (uint sampleIndex = 0u; sampleIndex < sampleCount; sampleIndex++)
    {
        vec3 direction = DirectionalSampleSunDirection(
            centerDirection,
            pixel,
            0u,
            sampleIndex,
            max(modeAndDistance.w, 0.0),
            effectiveMode == 3u);
        float sampleMaximum;
        if (!DirectionalIntersectQualifiedBounds(
                origin,
                direction,
                maximumDistance,
                sampleMaximum))
        {
            visibility += 1.0;
            AddDirectionalTransparentRayCounter(18u, 1u);
            continue;
        }

        float hitDistance;
        uint candidates;
        uint alphaSamples;
        bool sampleCapHit;
        bool blocked = DirectionalTraceVisibility(
            SceneTlas,
            origin,
            direction,
            sampleMaximum,
            footprint,
            0x02u,
            hitDistance,
            candidates,
            alphaSamples,
            sampleCapHit);
        hitCount += blocked ? 1u : 0u;
        totalCandidates += candidates;
        totalAlphaSamples += alphaSamples;
        capHit = capHit || sampleCapHit;
        float sampleVisibility = blocked ? 0.0 : 1.0;
        if (blocked && effectiveMode == 1u)
        {
            sampleVisibility = smoothstep(
                sampleMaximum * 0.8,
                sampleMaximum,
                clamp(hitDistance, 0.0, sampleMaximum));
        }
        visibility += sampleVisibility;
    }

    AddDirectionalTransparentRayCounter(12u, sampleCount);
    AddDirectionalTransparentRayCounter(13u, hitCount);
    AddDirectionalTransparentRayCounter(14u, sampleCount - hitCount);
    AddDirectionalTransparentRayCounter(15u, totalCandidates);
    AddDirectionalTransparentRayCounter(16u, totalAlphaSamples);
    AddDirectionalTransparentRayCounter(17u, capHit ? 1u : 0u);
    float authoredVisibility = visibility / float(sampleCount);
    return mix(
        1.0,
        authoredVisibility,
        clamp(shadowSettings.x, 0.0, 1.0));
}
#endif

bool DirectionalRayShadowMaskSupportsReceiver(bool geometryDecal)
{
#if defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE)
    if (!geometryDecal)
        return true;
#else
    if (!geometryDecal)
        return false;
#endif
    ivec2 pixel = ivec2(ForwardScreenPixel());
    float ownerDepth = texelFetch(
        BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)],
        pixel,
        0).r;
    float tolerance = max(0.00001, abs(dFdx(gl_FragCoord.z)) + abs(dFdy(gl_FragCoord.z)));
    return abs(ownerDepth - gl_FragCoord.z) <= tolerance;
}

float EvaluateDirectionalCsmTemporalMask(
    uint lightIndex,
    bool geometryDecal)
{
    vec4 runtimeFlags = ReadDirectionalShadowRuntimeFlags();
    vec4 shadowIndices = ReadShadowIndices();
    if (runtimeFlags.x < 0.5 || runtimeFlags.w < 0.5 ||
        shadowIndices.x < 0.5 ||
        int(round(shadowIndices.w)) != int(lightIndex) ||
        !DirectionalRayShadowMaskSupportsReceiver(geometryDecal))
        return -1.0;

    uvec2 dimensions = uvec2(max(round(pc.Push.ScreenDimensions), vec2(1.0)));
    uvec2 pixel = uvec2(clamp(
        floor(ForwardScreenPixel()),
        vec2(0.0),
        vec2(dimensions - uvec2(1u))));
    uint pixelIndex = pixel.y * dimensions.x + pixel.x;
    uint frameSlot = min(pc.Push.CurrentFrameIndex, uint(FRAMES_IN_FLIGHT - 1));
    uint historyIndex = uint(DIRECTIONAL_SHADOW_HISTORY_BUFFER_BASE_INDEX) + frameSlot;
    return clamp(unpackHalf2x16(ReadStorageWord(
        historyIndex,
        pixelIndex * 3u)).x, 0.0, 1.0);
}

float EvaluateDirectionalRayShadowMask(
    uint lightIndex,
    bool geometryDecal)
{
    vec4 shadowIndices = ReadShadowIndices();
    if (shadowIndices.x < 0.5 ||
        !ForwardLayeredReceiverAcceptsShadows(geometryDecal) ||
        int(round(shadowIndices.w)) != int(lightIndex) ||
        !DirectionalRayShadowMaskSupportsReceiver(geometryDecal))
    {
        return 1.0;
    }

    uvec2 dimensions = uvec2(max(
        round(pc.Push.ScreenDimensions),
        vec2(1.0)));
    uvec2 pixel = uvec2(clamp(
        floor(ForwardScreenPixel()),
        vec2(0.0),
        vec2(dimensions - uvec2(1u))));
    uint pixelIndex = pixel.y * dimensions.x + pixel.x;
    uint frameSlot = min(pc.Push.CurrentFrameIndex, uint(FRAMES_IN_FLIGHT - 1));
    uint bufferIndex =
        uint(DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_BASE_INDEX) + frameSlot;
    uint packedVisibility = ReadStorageWord(
        bufferIndex,
        pixelIndex >> 2u);
    uint byteShift = (pixelIndex & 3u) * 8u;
    float visibility = float((packedVisibility >> byteShift) & 0xffu) /
        255.0;
    float shadowStrength = clamp(ReadShadowSettings().x, 0.0, 1.0);
    return mix(1.0, visibility, shadowStrength);
}

float EvaluateAreaRayShadowMask(
    uint lightIndex,
    GPULight light,
    bool geometryDecal)
{
    int shadowIndex = ReadLocalAreaShadowIndex(lightIndex);
    if (ForwardReflectionCaptureEnabled() ||
        shadowIndex < 0 || shadowIndex >= 4 ||
        !ForwardLayeredReceiverAcceptsShadows(geometryDecal) ||
        !DirectionalRayShadowMaskSupportsReceiver(geometryDecal))
    {
        return 1.0;
    }
    uvec2 dimensions = uvec2(max(
        round(pc.Push.ScreenDimensions),
        vec2(1.0)));
    uvec2 pixel = uvec2(clamp(
        floor(ForwardScreenPixel()),
        vec2(0.0),
        vec2(dimensions - uvec2(1u))));
    uint pixelIndex = pixel.y * dimensions.x + pixel.x;
    uint frameSlot = min(pc.Push.CurrentFrameIndex, uint(FRAMES_IN_FLIGHT - 1));
    uint bufferIndex = uint(AREA_RAY_SHADOW_MASK_BUFFER_BASE_INDEX) + frameSlot;
    uint packedVisibility = ReadStorageWord(bufferIndex, pixelIndex);
    uint byteShift = uint(shadowIndex) * 8u;
    float visibility = float((packedVisibility >> byteShift) & 0xffu) / 255.0;
    return mix(1.0, visibility, clamp(light.ShadowStrength, 0.0, 1.0));
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
    vec4 worldTexelSizes = ReadDirectionalShadowWorldTexelSizes();
    vec4 filterAndBias = ReadDirectionalShadowFilterAndBias();
    float cameraDistance = CameraForwardDistance(worldPosition);
    selectedCascade = SelectShadowCascade(cameraDistance, splits, cascadeCount);

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
        worldPosition,
        normal,
        mapSize,
        radius,
        worldTexelSizes,
        filterAndBias,
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
                worldPosition,
                normal,
                mapSize,
                radius,
                worldTexelSizes,
                filterAndBias,
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
                worldPosition,
                normal,
                mapSize,
                radius,
                worldTexelSizes,
                filterAndBias,
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
                    worldPosition,
                    normal,
                    mapSize,
                    radius,
                    worldTexelSizes,
                    filterAndBias,
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
                    worldPosition,
                    normal,
                    mapSize,
                    radius,
                    worldTexelSizes,
                    filterAndBias,
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

float EvaluateDirectionalShadowForEffectiveMode(
    uint lightIndex,
    vec3 worldPosition,
    vec3 normal,
    bool geometryDecal,
    out uint selectedCascade)
{
    uint effectiveMode = uint(clamp(
        round(ReadDirectionalShadowModeAndRayDistance().y),
        0.0,
        3.0));
    if (effectiveMode == 0u)
    {
        float temporalCsm = EvaluateDirectionalCsmTemporalMask(
            lightIndex,
            geometryDecal);
        if (temporalCsm >= 0.0)
        {
            selectedCascade = 0u;
            return temporalCsm;
        }
    }
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
    if (!geometryDecal && (effectiveMode == 2u || effectiveMode == 3u))
    {
        selectedCascade = 0u;
        return EvaluateDirectionalTransparentRay(
            lightIndex,
            worldPosition,
            normal,
            effectiveMode);
    }
#endif
    bool maskReceiver =
        DirectionalRayShadowMaskSupportsReceiver(geometryDecal);
    if (maskReceiver &&
        (effectiveMode == 2u || effectiveMode == 3u))
    {
        selectedCascade = 0u;
        return EvaluateDirectionalRayShadowMask(lightIndex, geometryDecal);
    }

    float cascaded = EvaluateDirectionalShadow(
        lightIndex,
        worldPosition,
        normal,
        geometryDecal,
        selectedCascade);
    if (maskReceiver && effectiveMode == 1u)
    {
        // Both values already contain the authored shadow strength. Taking the
        // darker result avoids applying that strength twice at contact hits.
        return min(
            cascaded,
            EvaluateDirectionalRayShadowMask(lightIndex, geometryDecal));
    }
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
    if (!geometryDecal && effectiveMode == 1u)
    {
        return min(
            cascaded,
            EvaluateDirectionalTransparentRay(
                lightIndex,
                worldPosition,
                normal,
                effectiveMode));
    }
#endif
    return cascaded;
}

uint DirectionalShadowScreenPixelIndex(out uint frameSlot)
{
    uvec2 dimensions = uvec2(max(
        round(pc.Push.ScreenDimensions),
        vec2(1.0)));
    uvec2 pixel = uvec2(clamp(
        floor(ForwardScreenPixel()),
        vec2(0.0),
        vec2(dimensions - uvec2(1u))));
    frameSlot = min(
        pc.Push.CurrentFrameIndex,
        uint(FRAMES_IN_FLIGHT - 1));
    return pixel.y * dimensions.x + pixel.x;
}

float ReadDirectionalShadowDebugMask(uint pixelIndex, uint frameSlot)
{
    uint bufferIndex =
        uint(DIRECTIONAL_RAY_SHADOW_MASK_BUFFER_BASE_INDEX) + frameSlot;
    uint packedVisibility = ReadStorageWord(bufferIndex, pixelIndex >> 2u);
    uint byteShift = (pixelIndex & 3u) * 8u;
    return float((packedVisibility >> byteShift) & 0xffu) / 255.0;
}

vec3 DirectionalShadowRejectionColor(uint rejection)
{
    return rejection == 0u ? vec3(0.05, 0.85, 0.15) :
        rejection == 1u ? vec3(1.0, 0.75, 0.05) :
        rejection == 2u ? vec3(0.95, 0.1, 0.85) :
        rejection == 3u ? vec3(0.1, 0.45, 1.0) :
        vec3(1.0, 0.1, 0.05);
}

bool TryEvaluateDirectionalShadowDebug(
    uint debugViewMode,
    vec3 worldPosition,
    vec3 normal,
    bool geometryDecal,
    float effectiveVisibility,
    out vec3 debugColor)
{
    debugColor = vec3(0.0);
    if (debugViewMode < DEBUG_VIEW_DIRECTIONAL_RAY_MASK ||
        debugViewMode > DEBUG_VIEW_DIRECTIONAL_HISTORY_REJECTION)
    {
        return false;
    }

    vec4 modeAndDistance = ReadDirectionalShadowModeAndRayDistance();
    uint effectiveMode = uint(clamp(round(modeAndDistance.y), 0.0, 3.0));
    vec4 runtimeFlags = ReadDirectionalShadowRuntimeFlags();
    uint frameSlot;
    uint pixelIndex = DirectionalShadowScreenPixelIndex(frameSlot);

    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_RAY_SCENE_RESIDENCY)
    {
        vec4 minimumBounds = ReadDirectionalShadowRaySceneBoundsMinimum();
        vec4 maximumBounds = ReadDirectionalShadowRaySceneBoundsMaximum();
        bool validBounds = minimumBounds.w > 0.5 && maximumBounds.w > 0.5;
        bool resident = validBounds &&
            all(greaterThanEqual(worldPosition, minimumBounds.xyz)) &&
            all(lessThanEqual(worldPosition, maximumBounds.xyz));
        debugColor = !validBounds
            ? vec3(1.0, 0.05, 0.05)
            : resident
                ? vec3(0.05, 0.85, 0.15)
                : vec3(1.0, 0.65, 0.05);
        return true;
    }

    bool temporalDebug =
        debugViewMode == DEBUG_VIEW_DIRECTIONAL_HISTORY_CONFIDENCE ||
        debugViewMode == DEBUG_VIEW_DIRECTIONAL_HISTORY_REJECTION;
    if (effectiveMode == 0u && !(temporalDebug && runtimeFlags.x > 0.5))
    {
        debugColor = vec3(0.18, 0.02, 0.02);
        return true;
    }

    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_RAY_MASK)
    {
        float visibility = ReadDirectionalShadowDebugMask(pixelIndex, frameSlot);
        debugColor = vec3(visibility);
        return true;
    }

    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_CSM_RAY_DIFFERENCE)
    {
        uint ignoredCascade;
        int shadowLightIndex = int(round(ReadShadowIndices().w));
        float cascaded = shadowLightIndex >= 0
            ? EvaluateDirectionalShadow(
                uint(shadowLightIndex),
                worldPosition,
                normal,
                geometryDecal,
                ignoredCascade)
            : 1.0;
        float difference = effectiveVisibility - cascaded;
        debugColor = difference < 0.0
            ? vec3(min(-difference * 4.0, 1.0), 0.0, 0.0)
            : vec3(0.0, 0.25 * min(difference * 4.0, 1.0),
                min(difference * 4.0, 1.0));
        return true;
    }

    // Detailed per-pixel storage is allocated only while one of these views
    // is selected. A missing generation is rendered as an explicit unavailable
    // state rather than reading an unbound descriptor.
    if (runtimeFlags.z < 0.5)
    {
        debugColor = vec3(1.0, 0.0, 1.0);
        return true;
    }

    uint diagnosticIndex =
        uint(DIRECTIONAL_SHADOW_DIAGNOSTIC_BUFFER_BASE_INDEX) + frameSlot;
    uint diagnostic = ReadStorageWord(diagnosticIndex, pixelIndex);
    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_RAY_HIT_DISTANCE)
    {
        float normalizedDistance = float((diagnostic >> 8u) & 0xffffu) /
            65535.0;
        debugColor = vec3(
            normalizedDistance,
            1.0 - abs(normalizedDistance * 2.0 - 1.0),
            1.0 - normalizedDistance);
        return true;
    }
    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_RAY_CANDIDATE_COUNT)
    {
        float normalizedCandidates = min(
            float(diagnostic & 0xffu) / 16.0,
            1.0);
        debugColor = vec3(
            normalizedCandidates,
            normalizedCandidates * normalizedCandidates,
            0.05);
        return true;
    }

    if (runtimeFlags.w < 0.5)
    {
        debugColor = vec3(0.2, 0.0, 0.2);
        return true;
    }
    uint historyIndex =
        uint(DIRECTIONAL_SHADOW_HISTORY_BUFFER_BASE_INDEX) + frameSlot;
    uint metadata = ReadStorageWord(historyIndex, pixelIndex * 3u + 2u);
    bool historyValid = (metadata & (1u << 29u)) != 0u;
    if (debugViewMode == DEBUG_VIEW_DIRECTIONAL_HISTORY_CONFIDENCE)
    {
        uint age = (metadata >> 24u) & 0x1fu;
        float maximumAge = max(
            ReadDirectionalShadowTemporalAndSampling().y,
            1.0);
        float confidence = historyValid
            ? clamp(float(age) / maximumAge, 0.0, 1.0)
            : 0.0;
        debugColor = vec3(1.0 - confidence, confidence, 0.05);
        return true;
    }

    uint rejection = (diagnostic >> 24u) & 0xffu;
    debugColor = historyValid
        ? DirectionalShadowRejectionColor(0u)
        : DirectionalShadowRejectionColor(rejection);
    return true;
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

    // Normal maps encode directions rather than colors. A conservative
    // two-mip footprint prevents sub-pixel direction flips from becoming
    // black N.L speckles after the decoded vector is normalized.
    vec3 tangentNormal = SampleMaterialTextureFootprint(
        material.NormalTextureIndex,
        uv,
        4.0).xyz * 2.0 - 1.0;
    if ((material.FeatureFlags & MATERIAL_FEATURE_NORMAL_GREEN_INVERTED) != 0u)
        tangentNormal.y = -tangentNormal.y;
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

float ApplyGeometricSpecularAntialiasing(
    float perceptualRoughness,
    vec3 shadingNormal)
{
    if (!ForwardGeometricSpecularAntialiasingEnabled())
        return perceptualRoughness;

    vec3 normalDx = dFdx(shadingNormal);
    vec3 normalDy = dFdy(shadingNormal);
    // Filter GGX in alpha-squared space. The bounded variance is deliberately
    // conservative: it removes sub-pixel normal-map/glancing-triangle fireflies
    // without turning a genuinely smooth continuous surface into matte paint.
    float geometricVariance = clamp(
        0.5 * (dot(normalDx, normalDx) + dot(normalDy, normalDy)),
        0.0,
        0.25);
    float alpha = max(perceptualRoughness * perceptualRoughness, 0.0016);
    float filteredAlphaSquared = clamp(
        alpha * alpha + geometricVariance,
        0.00000256,
        1.0);
    return sqrt(sqrt(filteredAlphaSquared));
}

float EstimateReflectionSchedulingRoughness(
    float physicalRoughness,
    float conservativeFootprintRoughness,
    vec3 shadingNormal)
{
    // Normal and roughness maps can vary by substantially more than one
    // microfacet lobe in a screen pixel. Folding both footprints into alpha
    // prevents a single dark roughness texel (or normal-map spike) inside a
    // broad material from scheduling an isolated SSR/ray-query firefly. This
    // value controls work selection only; authored physical roughness remains
    // the sole BRDF, Fresnel, LUT, and prefiltered-radiance input.
    vec3 normalDx = dFdx(shadingNormal);
    vec3 normalDy = dFdy(shadingNormal);
    float normalVariance = clamp(
        0.5 * (dot(normalDx, normalDx) + dot(normalDy, normalDy)),
        0.0,
        0.18);
    float roughnessDx = dFdx(physicalRoughness);
    float roughnessDy = dFdy(physicalRoughness);
    float roughnessFootprintVariance = clamp(
        0.5 * (roughnessDx * roughnessDx +
            roughnessDy * roughnessDy),
        0.0,
        0.50);
    float schedulingBase = max(
        physicalRoughness,
        conservativeFootprintRoughness);
    float alphaSquared = schedulingBase * schedulingBase *
        schedulingBase * schedulingBase;
    return pow(clamp(
        alphaSquared + normalVariance + roughnessFootprintVariance,
        0.00000256,
        1.0), 0.25);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness)
{
    return f0 + (max(vec3(1.0 - roughness), f0) - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

vec3 FresnelSchlickIndirectRoughness(
    float cosTheta,
    vec3 f0,
    float roughness)
{
    float perceptualRoughness = clamp(roughness, 0.0, 1.0);
    vec3 standardGrazing = max(
        vec3(1.0 - perceptualRoughness),
        f0);
    float remainingGloss = 1.0 - perceptualRoughness;
    vec3 broadDielectricGrazing = f0 +
        (vec3(1.0) - f0) * remainingGloss * remainingGloss;
    // A prefiltered cubemap has no receiver-local horizon information. The
    // standard roughness approximation therefore turns authored rough stone
    // into a bright, nearly uniform lobe at grazing angles. Clamp only the
    // broad indirect dielectric endpoint: smooth lobes remain bit-identical,
    // and high-F0 metals retain their standard endpoint through the min().
    float broadLobeWeight = smoothstep(
        0.35,
        0.70,
        perceptualRoughness);
    vec3 grazing = mix(
        standardGrazing,
        min(standardGrazing, broadDielectricGrazing),
        broadLobeWeight);
    return f0 + (grazing - f0) *
        pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
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

float DistributionCharlie(float nDotH, float roughness)
{
    float alpha = max(roughness, 0.07);
    float inverseAlpha = 1.0 / alpha;
    float sinThetaSquared = max(1.0 - nDotH * nDotH, 0.000001);
    return (2.0 + inverseAlpha) *
        pow(sinThetaSquared, 0.5 * inverseAlpha) /
        (2.0 * PI);
}

float VisibilitySheen(float nDotV, float nDotL)
{
    return 1.0 / max(
        4.0 * (nDotL + nDotV - nDotL * nDotV),
        0.000001);
}

float SheenDirectionalAlbedo(
    float nDotDirection,
    float roughness)
{
    float grazing = pow(
        clamp(1.0 - nDotDirection, 0.0, 1.0),
        5.0);
    return clamp(
        mix(0.35, 0.75, grazing) * mix(1.0, 0.65, roughness),
        0.0,
        1.0);
}

float LayeredBaseSpecularScale(
    float nDotV,
    float nDotL,
    float hDotV,
    float clearcoatFactor,
    vec3 sheenColor,
    float sheenRoughness)
{
    float clearcoatFresnel = clearcoatFactor *
        FresnelSchlick(hDotV, vec3(0.04)).x;
    float sheenEnergy = MaxComponent(clamp(
        sheenColor,
        vec3(0.0),
        vec3(1.0))) * max(
            SheenDirectionalAlbedo(nDotV, sheenRoughness),
            SheenDirectionalAlbedo(nDotL, sheenRoughness));
    return clamp(
        (1.0 - clearcoatFresnel) * (1.0 - sheenEnergy),
        0.0,
        1.0);
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    vec3 directionalDiffuseBase,
    float roughness,
    vec3 dielectricF0,
    float clearcoatFactor,
    float clearcoatRoughness,
    vec3 clearcoatNormal,
    vec3 sheenColor,
    float sheenRoughness,
    vec3 normal,
    vec3 viewDirection,
    vec3 lightDirection,
    vec3 radiance,
    out vec3 diffuseContribution);

struct ForwardTransparentReflectionSample
{
    vec3 Radiance;
    float Confidence;
    uint Source;
};

vec3 ForwardReflectionSourceColor(uint source)
{
    if (source == FORWARD_REFLECTION_SOURCE_SSR)
        return vec3(0.0, 0.9, 1.0);
    if (source == FORWARD_REFLECTION_SOURCE_RAY_QUERY)
        return vec3(1.0, 0.0, 0.9);
    if (source == FORWARD_REFLECTION_SOURCE_DDGI)
        return vec3(0.15, 1.0, 0.25);
    if (source == FORWARD_REFLECTION_SOURCE_LOCAL_PROBE)
        return vec3(1.0, 0.85, 0.0);
    if (source == FORWARD_REFLECTION_SOURCE_ENVIRONMENT)
        return vec3(0.1, 0.3, 1.0);
    if (source == FORWARD_REFLECTION_SOURCE_PLANAR)
        return vec3(1.0, 0.45, 0.05);
    return vec3(0.0);
}

bool ForwardTransparentReflectionDiagnosticSample()
{
    uvec2 pixel = uvec2(max(floor(ForwardScreenPixel()), vec2(0.0)));
    return (pixel.x & 7u) == 0u && (pixel.y & 7u) == 0u;
}

void ForwardAddTransparentReflectionEstimate(uint counter)
{
    if (!ForwardTransparentReflectionDiagnosticSample())
        return;
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        pc.Push.CurrentFrameIndex;
    uint subgroupValue = subgroupAdd(64u);
    if (subgroupElect())
    {
        atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counter],
            subgroupValue);
    }
}

void ForwardAddTransparentReflectionExact(uint counter, uint value)
{
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        pc.Push.CurrentFrameIndex;
    uint subgroupValue = subgroupAdd(value);
    if (subgroupElect())
    {
        atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[counter],
            subgroupValue);
    }
}

uint ForwardTransparentReflectionAdmissionHash(uint salt)
{
    uvec2 pixel = uvec2(max(floor(ForwardScreenPixel()), vec2(0.0)));
    return HashUint(pixel.x * 0x9e3779b9u ^
        pixel.y * 0x85ebca6bu ^
        pc.Push.CurrentFrameIndex * 0xc2b2ae35u ^
        floatBitsToUint(pc.Push.Time) ^ salt);
}

bool ForwardTryReserveTransparentSsr(
    GPUReflectionProbeHeader header,
    out uint reservedSamples)
{
    // Every march step may issue both a hierarchical lookup and a full-detail
    // confirmation before continuing. Reserve that exact worst case before
    // the first lookup so over-budget fragments cannot partially execute.
    reservedSamples = max(header.SsrMaximumSteps, 8u) * 2u;
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        pc.Push.CurrentFrameIndex;
    uint maximumTraces = header.SceneReflectionSsrSampleBudget /
        reservedSamples;
    uint requested = subgroupAdd(1u);
    bool hashAccepted = header.Padding1 != 0u &&
        ForwardTransparentReflectionAdmissionHash(0x51f15e5du) <=
            header.Padding1;
    uint candidate = hashAccepted ? 1u : 0u;
    uint candidates = subgroupAdd(candidate);
    uint prefix = subgroupExclusiveAdd(candidate);
    uint allocationBase = 0u;
    if (subgroupElect())
    {
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_SSR_ELIGIBLE_COUNTER], requested);
        allocationBase = atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
                TRANSPARENT_REFLECTION_SSR_ALLOCATION_CURSOR],
            candidates);
    }
    allocationBase = subgroupBroadcastFirst(allocationBase);
    bool admitted = hashAccepted &&
        allocationBase + prefix < maximumTraces;
    uint admittedCount = subgroupAdd(admitted ? 1u : 0u);
    if (subgroupElect())
    {
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_SSR_ADMITTED_COUNTER], admittedCount);
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_SSR_BUDGET_REJECT_COUNTER],
            requested - admittedCount);
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_SSR_RESERVED_SAMPLE_COUNTER],
            admittedCount * reservedSamples);
    }
    return admitted;
}

bool ForwardTryReserveTransparentReflectionRay(
    GPUReflectionProbeHeader header)
{
    uint budget = header.SceneReflectionRayTaskBudget;
    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) +
        pc.Push.CurrentFrameIndex;
    uint requested = subgroupAdd(1u);
    bool hashAccepted = header.Padding2 != 0u &&
        ForwardTransparentReflectionAdmissionHash(0x7a143595u) <=
            header.Padding2;
    uint candidate = hashAccepted ? 1u : 0u;
    uint candidates = subgroupAdd(candidate);
    uint prefix = subgroupExclusiveAdd(candidate);
    uint allocationBase = 0u;
    if (subgroupElect())
    {
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_TASK_COUNTER], requested);
        allocationBase = atomicAdd(
            BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
                TRANSPARENT_REFLECTION_RAY_ALLOCATION_CURSOR],
            candidates);
    }
    allocationBase = subgroupBroadcastFirst(allocationBase);
    bool admitted = hashAccepted && allocationBase + prefix < budget;
    uint admittedCount = subgroupAdd(admitted ? 1u : 0u);
    if (subgroupElect())
    {
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_RAY_ADMITTED_COUNTER], admittedCount);
        atomicAdd(BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[
            TRANSPARENT_REFLECTION_RAY_EXACT_BUDGET_REJECT_COUNTER],
            requested - admittedCount);
    }
    if (!admitted)
        ForwardAddTransparentReflectionEstimate(
            TRANSPARENT_REFLECTION_BUDGET_REJECT_COUNTER);
    return admitted;
}

vec3 ForwardSampleOpaqueReflectionColorCone(vec2 uv, float logicalLod)
{
    vec3 fullResolution = textureLod(
        BindlessTextures[nonuniformEXT(
            OPAQUE_SCENE_COLOR_SNAPSHOT_TEXTURE_INDEX)],
        uv,
        0.0).rgb;
    if (logicalLod <= 0.0)
        return max(fullResolution, vec3(0.0));

    float tailLod = max(logicalLod - 1.0, 0.0);
    uint lower = min(
        uint(floor(tailLod)),
        uint(MAX_BLOOM_MIP_TEXTURES - 1));
    uint upper = min(lower + 1u,
        uint(MAX_BLOOM_MIP_TEXTURES - 1));
    vec3 lowerValue = textureLod(
        BindlessTextures[nonuniformEXT(
            BLOOM_MIP_TEXTURE_BASE + int(lower))],
        uv,
        0.0).rgb;
    vec3 upperValue = textureLod(
        BindlessTextures[nonuniformEXT(
            BLOOM_MIP_TEXTURE_BASE + int(upper))],
        uv,
        0.0).rgb;
    vec3 tail = mix(lowerValue, upperValue, fract(tailLod));
    return max(mix(fullResolution, tail,
        clamp(logicalLod, 0.0, 1.0)), vec3(0.0));
}

bool ForwardTraceTransparentSsr(
    GPUReflectionProbeHeader header,
    vec3 worldPosition,
    vec3 geometricNormal,
    vec3 reflectionDirection,
    float schedulingRoughness,
    out ForwardTransparentReflectionSample result)
{
    result.Radiance = vec3(0.0);
    result.Confidence = 0.0;
    result.Source = FORWARD_REFLECTION_SOURCE_NONE;
    if (!ForwardTransparentSampleReflections() ||
        !ForwardOpaqueSceneColorSnapshotAvailable() ||
        header.SsrMaximumSteps == 0u ||
        header.SsrMaximumDistance <= 0.0 ||
        dot(reflectionDirection, geometricNormal) <= 0.001)
    {
        return false;
    }

    uint reservedSamples;
    if (!ForwardTryReserveTransparentSsr(header, reservedSamples))
        return false;

    uint stepCount = max(header.SsrMaximumSteps, 8u);
    uint actualSamples = 0u;
    float maximumDistance = header.SsrMaximumDistance;
    int mipCount = max(textureQueryLevels(
        BindlessTextures[nonuniformEXT(HIZ_DEPTH_TEXTURE_INDEX)]), 1);
    float jitter = fract(sin(dot(ForwardScreenPixel(),
        vec2(12.9898, 78.233))) * 43758.5453);
    for (uint stepIndex = 0u; stepIndex < stepCount; ++stepIndex)
    {
        float normalizedStep =
            (float(stepIndex) + 0.75 + jitter * 0.5) /
            float(stepCount);
        float distanceAlongRay = max(0.02,
            normalizedStep * normalizedStep * maximumDistance);
        vec3 samplePosition = worldPosition + reflectionDirection *
            distanceAlongRay;
        vec4 clip = MulRowMajor(
            vec4(samplePosition, 1.0),
            pc.Push.ViewProjectionMatrix);
        if (any(isnan(clip)) || any(isinf(clip)) || clip.w <= 1.0e-6)
            continue;
        vec3 ndc = clip.xyz / clip.w;
        vec2 uv = ndc.xy * 0.5 + vec2(0.5);
        if (any(lessThan(uv, vec2(0.0))) ||
            any(greaterThanEqual(uv, vec2(1.0))))
        {
            break;
        }

        float footprint = 1.0 + normalizedStep *
            mix(4.0, 12.0, schedulingRoughness);
        float mip = clamp(log2(footprint), 0.0,
            float(mipCount - 1));
        float sceneDepth = textureLod(
            BindlessTextures[nonuniformEXT(HIZ_DEPTH_TEXTURE_INDEX)],
            uv,
            mip).r;
        ++actualSamples;
        float rayDepth = ndc.z;
        float depthError = sceneDepth - rayDepth;
        float thickness = 0.0015 + normalizedStep * 0.006 +
            schedulingRoughness * 0.003;
        if (sceneDepth <= 0.0 || depthError < 0.0 ||
            depthError > thickness)
        {
            continue;
        }

        float fineDepth = textureLod(
            BindlessTextures[nonuniformEXT(HIZ_DEPTH_TEXTURE_INDEX)],
            uv,
            0.0).r;
        ++actualSamples;
        float fineError = abs(fineDepth - rayDepth);
        float edge = min(min(uv.x, uv.y),
            min(1.0 - uv.x, 1.0 - uv.y));
        float edgeConfidence = smoothstep(0.0, 0.08, edge);
        float depthConfidence = 1.0 - clamp(
            fineError / max(thickness, 1.0e-5), 0.0, 1.0);
        float marchConfidence = 1.0 - float(stepIndex) /
            float(max(stepCount, 1u));
        float confidence = clamp(edgeConfidence * depthConfidence *
            mix(0.5, 1.0, marchConfidence), 0.0, 1.0);
        if (confidence < header.SsrConfidenceThreshold)
            continue;

        float colorLod = clamp(max(
            mip,
            schedulingRoughness *
                float(MAX_BLOOM_MIP_TEXTURES)),
            0.0,
            float(MAX_BLOOM_MIP_TEXTURES));
        vec3 radiance = ForwardSampleOpaqueReflectionColorCone(
            uv,
            colorLod);
        if (any(isnan(radiance)) || any(isinf(radiance)))
        {
            ForwardAddTransparentReflectionExact(
                TRANSPARENT_REFLECTION_SSR_ACTUAL_SAMPLE_COUNTER,
                actualSamples);
            return false;
        }
        result.Radiance = max(radiance, vec3(0.0));
        result.Confidence = confidence;
        result.Source = FORWARD_REFLECTION_SOURCE_SSR;
        ForwardAddTransparentReflectionEstimate(
            TRANSPARENT_REFLECTION_SSR_HIT_COUNTER);
        ForwardAddTransparentReflectionExact(
            TRANSPARENT_REFLECTION_SSR_ACTUAL_SAMPLE_COUNTER,
            actualSamples);
        ForwardAddTransparentReflectionExact(
            TRANSPARENT_REFLECTION_SSR_EXACT_HIT_COUNTER,
            1u);
        return true;
    }
    ForwardAddTransparentReflectionExact(
        TRANSPARENT_REFLECTION_SSR_ACTUAL_SAMPLE_COUNTER,
        actualSamples);
    return false;
}

#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
bool ForwardTransparentReflectionCandidatePasses(rayQueryEXT query)
{
    uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(
        query, false);
    GPUDdgiRayQueryInstance instance =
        GiCausticReadRayQueryInstance(instanceIndex);
    if (!GiCausticRayInstanceValid(instance))
        return true;
    if (GiCausticRayGeometryIsDecal(instance) ||
        instance.GeometryClass == DDGI_RAY_GEOMETRY_VOLUME_TRANSMISSION ||
        instance.GeometryClass == DDGI_RAY_GEOMETRY_WATER_SURFACE ||
        (instance.GeometryFlags &
            (DDGI_RAY_GEOMETRY_FLAG_VOLUME_TRANSMISSION |
             DDGI_RAY_GEOMETRY_FLAG_WATER_SURFACE)) != 0u)
    {
        return false;
    }
    return GiCausticCandidatePassesOpacity(
        instanceIndex,
        rayQueryGetIntersectionPrimitiveIndexEXT(query, false),
        rayQueryGetIntersectionBarycentricsEXT(query, false),
        rayQueryGetIntersectionFrontFaceEXT(query, false));
}

bool ForwardTraceTransparentReflectionNearest(
    vec3 origin,
    vec3 direction,
    float maximumDistance,
    out RayQuerySurfaceHit hit)
{
    rayQueryEXT query;
    rayQueryInitializeEXT(query, SceneTlas, gl_RayFlagsNoneEXT, 0xff,
        origin, 0.002, direction, maximumDistance);
    uint candidates = 0u;
    bool exceeded = false;
    while (rayQueryProceedEXT(query))
    {
        if (rayQueryGetIntersectionTypeEXT(query, false) !=
            gl_RayQueryCandidateIntersectionTriangleEXT)
        {
            continue;
        }
        ++candidates;
        if (candidates > 64u)
        {
            exceeded = true;
            rayQueryTerminateEXT(query);
            break;
        }
        if (ForwardTransparentReflectionCandidatePasses(query))
            rayQueryConfirmIntersectionEXT(query);
    }
    if (exceeded || rayQueryGetIntersectionTypeEXT(query, true) ==
            gl_RayQueryCommittedIntersectionNoneEXT)
    {
        return false;
    }
    return RayQuerySurfaceResolveCommittedHit(
        query, origin, direction, hit);
}

vec3 ForwardShadeTransparentReflectionHit(
    RayQuerySurfaceHit hit,
    vec3 incomingDirection,
    GPUEnvironmentData environment,
    GPUReflectionProbeHeader header)
{
    vec4 baseColor = RayQuerySurfaceSampleBaseColor(hit);
    vec2 metallicRoughness =
        RayQuerySurfaceSampleMetallicRoughness(hit);
    vec3 albedo = max(baseColor.rgb, vec3(0.0));
    float metallic = clamp(metallicRoughness.x, 0.0, 1.0);
    float roughness = clamp(metallicRoughness.y, 0.04, 1.0);
    vec3 normal = RayQuerySurfaceOrientedNormal(hit);
    vec3 viewDirection = normalize(-incomingDirection);
    vec3 diffuseBase = albedo * (1.0 - metallic);
    vec3 dielectricF0 = vec3(0.04);
    vec3 radiance = RayQuerySurfaceSampleEmissive(hit);

    uint lightLimit = min(header.RayQueryHitLightLimit,
        ForwardTotalLightCount(pc.Push));
    for (uint lightIndex = 0u; lightIndex < lightLimit; ++lightIndex)
    {
        GPULight light = ReadLight(lightIndex);
        if (NjulfIsAreaLight(light))
        {
            NjulfAreaLightResult area = EvaluateNjulfAreaLightLtc(
                light,
                hit.Position,
                normal,
                viewDirection,
                roughness,
                diffuseBase,
                mix(dielectricF0, albedo, metallic));
            radiance += area.lighting;
            continue;
        }
        vec3 lightDirection;
        float attenuation = 1.0;
        if (light.Type == GPU_LIGHT_TYPE_DIRECTIONAL)
        {
            lightDirection = normalize(-light.Direction);
        }
        else if (NjulfIsPunctualLight(light))
        {
            vec3 toLight = light.Position - hit.Position;
            float distanceToLight = length(toLight);
            if (distanceToLight <= 0.001 || distanceToLight >= light.Range ||
                light.Range <= 0.0)
            {
                continue;
            }
            lightDirection = toLight / distanceToLight;
            attenuation = EvaluateNjulfLightDistanceAttenuation(
                light, distanceToLight) *
                EvaluateNjulfIesProfile(light, -lightDirection);
            if (light.Type == GPU_LIGHT_TYPE_SPOT)
                attenuation *= EvaluateNjulfSpotAttenuation(
                    light, lightDirection);
        }
        else
        {
            continue;
        }
        vec3 ignoredDiffuse;
        vec3 incident = max(light.Color, vec3(0.0)) *
            max(light.Intensity, 0.0) * attenuation;
        radiance += EvaluatePbrLight(
            albedo,
            metallic,
            diffuseBase,
            roughness,
            dielectricF0,
            0.0,
            0.04,
            normal,
            vec3(0.0),
            0.0,
            normal,
            viewDirection,
            lightDirection,
            incident,
            ignoredDiffuse);
    }

    vec3 environmentDiffuse = EvaluateEnvironmentDiffuseIrradiance(
        environment, normal) * diffuseBase / GI_MATERIAL_PI;
    vec3 indirectDiffuse = environmentDiffuse;
    if (ForwardGlobalIlluminationEnabled() != 0u)
    {
        vec3 ddgiIrradiance = SampleSimpleDdgiIrradiance(
            hit.Position, normal, viewDirection);
        if (any(greaterThan(ddgiIrradiance, vec3(0.000001))))
            indirectDiffuse = max(ddgiIrradiance, vec3(0.0)) *
                diffuseBase / GI_MATERIAL_PI;
    }
    float nDotV = max(dot(normal, viewDirection), 0.0);
    vec3 f0 = mix(dielectricF0, albedo, metallic);
    vec3 fresnel = FresnelSchlickIndirectRoughness(
        nDotV, f0, roughness);
    vec2 brdf = texture(
        BindlessTextures[nonuniformEXT(environment.BrdfLutTextureIndex)],
        vec2(nDotV, roughness)).rg;
    float maxLod = max(float(environment.PrefilteredMipCount) - 1.0, 0.0);
    vec3 indirectSpecular = SampleEnvironmentPrefilteredRadiance(
        environment,
        reflect(-viewDirection, normal),
        roughness * maxLod) * (fresnel * brdf.x + brdf.y) *
        environment.SpecularIntensity;
    return clamp(radiance + indirectDiffuse + indirectSpecular,
        vec3(0.0), vec3(65504.0));
}

bool ForwardTraceTransparentRayReflection(
    GPUReflectionProbeHeader header,
    vec3 worldPosition,
    vec3 geometricNormal,
    vec3 reflectionDirection,
    GPUEnvironmentData environment,
    out ForwardTransparentReflectionSample result)
{
    result.Radiance = vec3(0.0);
    result.Confidence = 0.0;
    result.Source = FORWARD_REFLECTION_SOURCE_NONE;
    if (ForwardEffectiveReflectionMode() != 5u ||
        dot(reflectionDirection, geometricNormal) <= 0.001 ||
        !ForwardTryReserveTransparentReflectionRay(header))
    {
        return false;
    }
    RayQuerySurfaceHit hit;
    if (!ForwardTraceTransparentReflectionNearest(
            NjulfOffsetRayOrigin(worldPosition, geometricNormal),
            reflectionDirection,
            header.SsrMaximumDistance,
            hit))
    {
        ForwardAddTransparentReflectionEstimate(
            TRANSPARENT_REFLECTION_RAY_MISS_COUNTER);
        return false;
    }
    result.Radiance = ForwardShadeTransparentReflectionHit(
        hit, reflectionDirection, environment, header);
    result.Confidence = clamp(1.0 - hit.Distance /
        max(header.SsrMaximumDistance, 0.001), 0.65, 1.0);
    result.Source = FORWARD_REFLECTION_SOURCE_RAY_QUERY;
    ForwardAddTransparentReflectionEstimate(
        TRANSPARENT_REFLECTION_RAY_HIT_COUNTER);
    return true;
}
#endif

vec3 EvaluateReflectionSpecular(
    GPUEnvironmentData environment,
    vec3 worldPosition,
    vec3 reflectionDirection,
    float globalLod,
    float roughness,
    vec2 brdf,
    vec3 fresnel,
    float specularOcclusion,
    vec3 ddgiDirectionalRadiance,
    float ddgiDirectionalConfidence,
    out bool debugActive,
    out vec3 debugColor,
    out uint dominantSource)
{
    debugActive = false;
    debugColor = vec3(0.0);
    dominantSource = FORWARD_REFLECTION_SOURCE_NONE;

    GPUReflectionProbeHeader header = ReadReflectionProbeHeader();
    bool reflectionsEnabled = (header.Flags & REFLECTION_ENABLED_FLAG) != 0u;
    if (!reflectionsEnabled)
        return vec3(0.0);

    vec3 globalDirection = EnvironmentUsesAnalyticSky(environment)
        ? normalize(reflectionDirection)
        : RotateEnvironmentDirection(reflectionDirection, environment.RotationRadians);
    // The global environment and local probes do not necessarily have the
    // same mip count. Mapping a probe through the shorter environment chain
    // leaves rough materials sampling a much sharper local mip, which makes
    // stone and plaster read as wet.
    float probeMaxLod = max(float(header.ProbeMipCount) - 1.0, 0.0);
    // Mip zero is the raw radiance capture and can contain a delta directional
    // highlight smaller than one cubemap texel. Local probes start at the first
    // antialiased GGX mip; truly sharp materials still retain global/direct
    // specular without exposing a captured firefly.
    float probeLod = probeMaxLod > 0.0
        ? mix(1.0, probeMaxLod, roughness)
        : 0.0;

    vec3 localReflection = vec3(0.0);
    vec3 firstWeightColor = vec3(0.0);
    vec3 projectedDirection = globalDirection;
    float totalWeight = 0.0;
    int acceptedProbeCount = 0;
    int selectedProbeIndex = -1;
    bool blendingEnabled = (header.Flags & REFLECTION_PROBE_BLENDING_ENABLED_FLAG) != 0u;
    int maxAcceptedProbes = max(header.MaxProbesPerPixel, 1);
    int candidateProbeCount = min(
        header.ProbeCount,
        FORWARD_REFLECTION_PROBE_CANDIDATE_LIMIT);

    if (!ForwardReflectionCaptureEnabled())
    {
        // Probe records are priority sorted on upload. Bound the miss-heavy
        // volume scan so a pixel outside authored volumes cannot walk all 256
        // records before falling back to DDGI/global radiance.
        for (int probeIndex = 0;
             probeIndex < candidateProbeCount &&
                 acceptedProbeCount < maxAcceptedProbes;
             probeIndex++)
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
                probeLod).rgb * max(probe.BlendParams.y, 0.0);

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

    // Explicit source ownership: local geometric captures consume the first
    // share, qualified DDGI consumes the represented remainder, and the global
    // environment is the canonical fallback. The weights sum to one before a
    // single split-sum BRDF application.
    float remainingWeight = 1.0 - localWeight;
    float ddgiWeight = remainingWeight * clamp(
        ddgiDirectionalConfidence,
        0.0,
        1.0);
    float globalWeight = max(remainingWeight - ddgiWeight, 0.0);
    dominantSource = localWeight >= ddgiWeight &&
            localWeight >= globalWeight && localWeight > 0.0001
        ? FORWARD_REFLECTION_SOURCE_LOCAL_PROBE
        : ddgiWeight >= globalWeight && ddgiWeight > 0.0001
            ? FORWARD_REFLECTION_SOURCE_DDGI
            : FORWARD_REFLECTION_SOURCE_ENVIRONMENT;
    // A fully weighted local probe owns the reflected-radiance source. Avoid
    // an environment cubemap sample whose result would be multiplied by zero;
    // this keeps local-probe quality at the cost of the previous global-only
    // path for pixels in an authored volume.
    vec3 globalReflection = vec3(0.0);
    if (globalWeight > 0.0001)
    {
        globalReflection = SampleEnvironmentPrefilteredRadiance(
            environment,
            reflectionDirection,
            globalLod) * header.GlobalFallbackIntensity *
            max(environment.SpecularIntensity, 0.0);
    }
    vec3 reflectedRadiance = (
        localReflection * localWeight +
        ddgiDirectionalRadiance * ddgiWeight +
        globalReflection * globalWeight) * header.Intensity;
    // Environment.SpecularIntensity owns only the global IBL share above.
    // Scene captures and DDGI remain visible when environment lighting is off.
    vec3 specular = reflectedRadiance * (fresnel * brdf.x + brdf.y) *
        specularOcclusion;

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
            debugColor = vec3(header.ProbeMipCount <= 1u ? 0.0 : clamp(probeLod / float(header.ProbeMipCount - 1u), 0.0, 1.0));
        else if (header.DebugView == REFLECTION_DEBUG_BOX_PROJECTION_DIRECTION)
            debugColor = projectedDirection * 0.5 + vec3(0.5);
        else if (header.DebugView == REFLECTION_DEBUG_LOCAL_REFLECTION_ONLY)
            debugColor = localReflection * header.Intensity;
        else if (header.DebugView == REFLECTION_DEBUG_GLOBAL_FALLBACK_ONLY)
            debugColor = globalReflection * header.Intensity;
        else if (header.DebugView ==
                REFLECTION_DEBUG_DDGI_DIRECTIONAL_RADIANCE_LOBE)
            debugColor = ddgiDirectionalRadiance * header.Intensity;
        else if (header.DebugView == REFLECTION_DEBUG_SOURCE_OWNERSHIP)
            debugColor = vec3(localWeight, ddgiWeight, globalWeight);
        else if (header.DebugView == REFLECTION_DEBUG_SOURCE_SELECTION)
            debugColor = ForwardReflectionSourceColor(dominantSource);
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
    float specularOcclusion,
    vec3 ddgiDirectionalRadiance,
    float ddgiDirectionalConfidence,
    out bool debugActive,
    out vec3 debugColor,
    out uint dominantSource)
{
    debugActive = false;
    debugColor = vec3(0.0);
    dominantSource = FORWARD_REFLECTION_SOURCE_NONE;
    GPUReflectionProbeHeader header = ReadReflectionProbeHeader();
    bool reflectionsEnabled = (header.Flags & REFLECTION_ENABLED_FLAG) != 0u;
    if (!reflectionsEnabled)
        return vec3(0.0);

    vec3 globalReflection = SampleEnvironmentPrefilteredRadiance(
        environment,
        reflectionDirection,
        lod) * header.GlobalFallbackIntensity *
        max(environment.SpecularIntensity, 0.0);
    float ddgiWeight = clamp(ddgiDirectionalConfidence, 0.0, 1.0);
    dominantSource = ddgiWeight >= 0.5
        ? FORWARD_REFLECTION_SOURCE_DDGI
        : FORWARD_REFLECTION_SOURCE_ENVIRONMENT;
    vec3 reflectedRadiance = mix(
        globalReflection,
        ddgiDirectionalRadiance,
        ddgiWeight) * header.Intensity;
    vec3 specular = reflectedRadiance * (fresnel * brdf.x + brdf.y) *
        specularOcclusion;

    if (header.DebugView != 0u)
    {
        debugActive = true;
        if (header.DebugView == REFLECTION_DEBUG_GLOBAL_FALLBACK_ONLY)
            debugColor = globalReflection * header.Intensity;
        else if (header.DebugView ==
                REFLECTION_DEBUG_DDGI_DIRECTIONAL_RADIANCE_LOBE)
            debugColor = ddgiDirectionalRadiance * header.Intensity;
        else if (header.DebugView == REFLECTION_DEBUG_SOURCE_OWNERSHIP)
            debugColor = vec3(0.0, ddgiWeight, 1.0 - ddgiWeight);
        else if (header.DebugView == REFLECTION_DEBUG_SOURCE_SELECTION)
            debugColor = ForwardReflectionSourceColor(dominantSource);
        else
            debugColor = specular;
    }

    return specular;
}

vec3 EvaluateTransparentReflectionSpecular(
    GPUEnvironmentData environment,
    vec3 worldPosition,
    vec3 geometricNormal,
    vec3 reflectionDirection,
    float globalLod,
    float physicalRoughness,
    float schedulingRoughness,
    vec2 brdf,
    vec3 fresnel,
    float specularOcclusion,
    vec3 ddgiDirectionalRadiance,
    float ddgiDirectionalConfidence,
    bool allowLocalProbes,
    bool sampleSceneReflections,
    out bool debugActive,
    out vec3 debugColor)
{
    uint fallbackSource;
    vec3 fallbackSpecular = allowLocalProbes
        ? EvaluateReflectionSpecular(
            environment,
            worldPosition,
            reflectionDirection,
            globalLod,
            physicalRoughness,
            brdf,
            fresnel,
            specularOcclusion,
            ddgiDirectionalRadiance,
            ddgiDirectionalConfidence,
            debugActive,
            debugColor,
            fallbackSource)
        : EvaluateGlobalReflectionSpecular(
            environment,
            reflectionDirection,
            globalLod,
            brdf,
            fresnel,
            specularOcclusion,
            ddgiDirectionalRadiance,
            ddgiDirectionalConfidence,
            debugActive,
            debugColor,
            fallbackSource);

    GPUReflectionProbeHeader header = ReadReflectionProbeHeader();
    if (header.DebugView == REFLECTION_DEBUG_ROUGHNESS_INPUTS)
    {
        debugActive = true;
        debugColor = vec3(
            physicalRoughness,
            schedulingRoughness,
            abs(schedulingRoughness - physicalRoughness));
        return debugColor;
    }
#if !FORWARD_TRANSPARENT_REFLECTIONS_ACTIVE
    return fallbackSpecular;
#else
    uint effectiveMode = ForwardEffectiveReflectionMode();
    bool planarEnabled = sampleSceneReflections &&
        ForwardTransparentSampleReflections() &&
        (effectiveMode == 4u || effectiveMode == 5u);
    bool screenGeometricEnabled = sampleSceneReflections &&
        ForwardTransparentSampleReflections() &&
        ForwardOpaqueSceneColorSnapshotAvailable() &&
        (effectiveMode == 3u || effectiveMode == 5u);
    bool geometricEnabled = planarEnabled || screenGeometricEnabled;
    ForwardTransparentReflectionSample geometric;
    geometric.Radiance = vec3(0.0);
    geometric.Confidence = 0.0;
    geometric.Source = FORWARD_REFLECTION_SOURCE_NONE;
    bool geometricHit = false;
    if (planarEnabled)
    {
        GPUMaterialData receiverMaterial =
            ReadForwardMaterial(fragMaterialIndex);
        geometricHit = AutomaticPlanarTrySample(
            pc.Push.CurrentFrameIndex,
            AutomaticPlanarReceiverIdentity(
                fragObjectIndex,
                fragMaterialIndex,
                receiverMaterial.MaterialRevision),
            worldPosition,
            geometricNormal,
            physicalRoughness,
            geometric.Radiance,
            geometric.Confidence);
        if (geometricHit)
            geometric.Source = FORWARD_REFLECTION_SOURCE_PLANAR;
    }
    if (!geometricHit && screenGeometricEnabled)
    {
        geometricHit = ForwardTraceTransparentSsr(
            header,
            worldPosition,
            geometricNormal,
            reflectionDirection,
            schedulingRoughness,
            geometric);
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY
        if (!geometricHit && effectiveMode == 5u)
        {
            geometricHit = ForwardTraceTransparentRayReflection(
                header,
                worldPosition,
                geometricNormal,
                reflectionDirection,
                environment,
                geometric);
        }
#endif
    }

    float geometricWeight = geometricHit
        ? clamp(geometric.Confidence, 0.0, 1.0)
        : 0.0;
    vec3 geometricSpecular = geometric.Radiance * header.Intensity *
        (fresnel * brdf.x + brdf.y) * specularOcclusion;
    vec3 result = geometricSpecular * geometricWeight +
        fallbackSpecular * (1.0 - geometricWeight);
    uint selectedSource = geometricWeight >= 0.5
        ? geometric.Source
        : fallbackSource;

    if (geometricEnabled)
    {
        if (selectedSource == FORWARD_REFLECTION_SOURCE_DDGI)
            ForwardAddTransparentReflectionEstimate(
                TRANSPARENT_REFLECTION_DDGI_FALLBACK_COUNTER);
        else if (selectedSource == FORWARD_REFLECTION_SOURCE_LOCAL_PROBE)
            ForwardAddTransparentReflectionEstimate(
                TRANSPARENT_REFLECTION_PROBE_FALLBACK_COUNTER);
        else if (selectedSource == FORWARD_REFLECTION_SOURCE_ENVIRONMENT)
            ForwardAddTransparentReflectionEstimate(
                TRANSPARENT_REFLECTION_ENVIRONMENT_FALLBACK_COUNTER);
    }

    if (header.DebugView == REFLECTION_DEBUG_SOURCE_SELECTION)
    {
        debugActive = true;
        debugColor = ForwardReflectionSourceColor(selectedSource);
    }
    else if (header.DebugView == REFLECTION_DEBUG_CONFIDENCE)
    {
        debugActive = true;
        debugColor = vec3(geometricWeight);
    }
    else if (header.DebugView == 7u)
    {
        debugActive = true;
        debugColor = geometric.Source == FORWARD_REFLECTION_SOURCE_SSR
            ? vec3(0.0, 1.0, 1.0)
            : vec3(0.0);
    }
    return result;
#endif
}

void EvaluateIbl(
    vec3 albedo,
    float metallic,
    vec3 diffuseReflectance,
    float roughness,
    float reflectionSchedulingRoughness,
    vec3 dielectricF0,
    vec3 normal,
    vec3 diffuseIndirectNormal,
    vec3 geometricNormal,
    vec3 viewDirection,
    float ambientOcclusion,
    float indirectSpecularVisibility,
    vec3 ddgiDirectionalRadiance,
    float ddgiDirectionalConfidence,
    bool sampleSceneReflections,
    out vec3 diffuseIbl,
    out vec3 specularIbl,
    out bool reflectionDebugActive,
    out vec3 reflectionDebugColor)
{
    diffuseIbl = vec3(0.0);
    specularIbl = vec3(0.0);
    reflectionDebugActive = false;
    reflectionDebugColor = vec3(0.0);

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    // The receiver cache owns diffuse environment/DDGI and the deferred
    // hybrid pass owns all indirect specular. This hot production variant has
    // no forward IBL work left; avoid reading environment descriptors or
    // evaluating a reflection that final composition deliberately excludes.
    return;
#endif

    GPUEnvironmentData environment = ReadEnvironmentData();
    if (environment.Enabled == 0u)
        return;

    vec3 f0 = mix(dielectricF0, albedo, metallic);
    float nDotV = max(dot(normal, viewDirection), 0.0);
    vec3 fresnel = FresnelSchlickIndirectRoughness(
        nDotV,
        f0,
        roughness);
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE || FORWARD_THIN_GLASS_ONLY
    // The cache producer evaluates diffuse environment irradiance once per
    // low-frequency gather sample and preserves it separately from DDGI so
    // their AO policies remain exact. ThinGlass has no diffuse raster lobe,
    // so its irradiance result is identically zero as well.
    diffuseIbl = vec3(0.0);
#else
    vec3 irradiance = EvaluateEnvironmentDiffuseIrradiance(
        environment,
        diffuseIndirectNormal);
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

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    // HybridReflectionComposite adds the sole base indirect-specular term.
    // Computing the legacy forward reflection here both wasted work and made
    // accidental future composition changes prone to double reflections.
    return;
#endif

    vec3 reflectionDirection = reflect(-viewDirection, normal);
    float globalMaxLod = max(float(environment.PrefilteredMipCount) - 1.0, 0.0);
    float globalLod = roughness * globalMaxLod;
    vec2 brdf = texture(BindlessTextures[nonuniformEXT(environment.BrdfLutTextureIndex)], vec2(nDotV, roughness)).rg;
    // Material/screen AO handles local creases. DDGI contributes the missing
    // absolute transport visibility for broad reflection lobes, after local,
    // directional, and global reflection sources have selected their shares.
    float specularOcclusion = clamp(
        pow(ambientOcclusion, 1.0 + roughness) * indirectSpecularVisibility,
        0.0,
        1.0);
#if FORWARD_SIMPLE_MATERIAL || FORWARD_THIN_GLASS_ONLY
    // ThinGlass deliberately has no authored local-probe branch. DDGI owns
    // reflected scene radiance and the environment fills only its unsupported
    // confidence share.
    specularIbl = EvaluateTransparentReflectionSpecular(
        environment,
        fragWorldPosition,
        geometricNormal,
        reflectionDirection,
        globalLod,
        roughness,
        reflectionSchedulingRoughness,
        brdf,
        fresnel,
        specularOcclusion,
        ddgiDirectionalRadiance,
        ddgiDirectionalConfidence,
        false,
        sampleSceneReflections,
        reflectionDebugActive,
        reflectionDebugColor);
#else
    specularIbl = EvaluateTransparentReflectionSpecular(
        environment,
        fragWorldPosition,
        geometricNormal,
        reflectionDirection,
        globalLod,
        roughness,
        reflectionSchedulingRoughness,
        brdf,
        fresnel,
        specularOcclusion,
        ddgiDirectionalRadiance,
        ddgiDirectionalConfidence,
        true,
        sampleSceneReflections,
        reflectionDebugActive,
        reflectionDebugColor);
#endif

    // The directional DDGI sidecar participates as one normalized reflection
    // source. Diffuse irradiance remains a separate certified field.
}

vec3 EvaluatePbrLight(
    vec3 albedo,
    float metallic,
    vec3 directionalDiffuseBase,
    float roughness,
    vec3 dielectricF0,
    float clearcoatFactor,
    float clearcoatRoughness,
    vec3 clearcoatNormal,
    vec3 sheenColor,
    float sheenRoughness,
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

    float baseSpecularScale = LayeredBaseSpecularScale(
        nDotV,
        nDotL,
        hDotV,
        clearcoatFactor,
        sheenColor,
        sheenRoughness);
    vec3 layeredSpecular = specular * baseSpecularScale;

    float clearcoatNdotL = max(dot(clearcoatNormal, lightDirection), 0.0);
    float clearcoatNdotV = max(dot(clearcoatNormal, viewDirection), 0.0);
    if (clearcoatFactor > 0.0 && clearcoatNdotL > 0.0 &&
        clearcoatNdotV > 0.0)
    {
        vec3 clearcoatHalf = normalize(viewDirection + lightDirection);
        float clearcoatNdotH = max(
            dot(clearcoatNormal, clearcoatHalf),
            0.0);
        float clearcoatHdotV = max(
            dot(clearcoatHalf, viewDirection),
            0.0);
        float clearcoatDistribution = DistributionGGX(
            clearcoatNdotH,
            clearcoatRoughness);
        float clearcoatGeometry = GeometrySmith(
            clearcoatNdotV,
            clearcoatNdotL,
            clearcoatRoughness);
        vec3 clearcoatFresnel = FresnelSchlick(
            clearcoatHdotV,
            vec3(0.04));
        layeredSpecular += clearcoatFactor *
            clearcoatDistribution * clearcoatGeometry *
            clearcoatFresnel /
            max(4.0 * clearcoatNdotV * clearcoatNdotL, 0.000001) *
            (clearcoatNdotL / nDotL);
    }

    if (MaxComponent(sheenColor) > 0.0)
    {
        float sheenDistribution = DistributionCharlie(
            nDotH,
            sheenRoughness);
        float sheenVisibility = VisibilitySheen(nDotV, nDotL);
        layeredSpecular += sheenColor * sheenDistribution *
            sheenVisibility;
    }

    return diffuseContribution + layeredSpecular * radiance * nDotL;
}

void AccumulateLight(
    uint lightIndex,
    vec3 albedo,
    float metallic,
    vec3 directionalDiffuseBase,
    vec3 subsurfaceDirectionalDiffuseBase,
    bool subsurfaceBacklightingActive,
    float roughness,
    vec3 dielectricF0,
    float clearcoatFactor,
    float clearcoatRoughness,
    vec3 clearcoatNormal,
    vec3 sheenColor,
    float sheenRoughness,
    vec3 normal,
    vec3 shadowNormal,
    vec3 viewDirection,
    vec3 worldPosition,
    bool geometryDecal,
    out float shadowFactor,
    out uint shadowCascade,
    out vec3 shadowEvaluationNormal,
    inout vec3 directLighting,
    inout vec3 directDiffuseSource,
    inout vec3 directBackDiffuseSource)
{
    GPULight light = ReadLight(lightIndex);
    shadowFactor = 1.0;
    shadowCascade = 0u;
    shadowEvaluationNormal = shadowNormal;

    vec3 lightDirection;
    float attenuation = 1.0;
    float signedNdotL = 0.0;
    bool backSide = false;

    if (light.Type == GPU_LIGHT_TYPE_DIRECTIONAL)
    {
        lightDirection = normalize(-light.Direction);
        signedNdotL = dot(normal, lightDirection);
        if (signedNdotL > 0.0)
        {
            shadowFactor = EvaluateDirectionalShadowForEffectiveMode(
                lightIndex,
                worldPosition,
                shadowEvaluationNormal,
                geometryDecal,
                shadowCascade);
        }
        else if (signedNdotL < 0.0 && subsurfaceBacklightingActive)
        {
            backSide = true;
            shadowEvaluationNormal = -shadowNormal;
            // The screen-space temporal/ray masks are produced for the visible
            // front hemisphere. Use one normal-aware CSM lookup for the bounded
            // backside lobe rather than treating a skipped mask sample as lit.
            shadowFactor = EvaluateDirectionalShadow(
                lightIndex,
                worldPosition,
                shadowEvaluationNormal,
                geometryDecal,
                shadowCascade);
        }
        else
        {
            return;
        }
    }
    else if (NjulfIsAreaLight(light))
    {
        // Area-light visibility is currently a front-hemisphere mask. Its
        // ordinary diffuse participates in the global split below, but the
        // bounded approximation deliberately does not invent a back lobe.
        float nDotV = max(dot(normal, viewDirection), 0.0);
        vec3 diffuseReflectance = EvaluateGiDiffuseBrdf(
            directionalDiffuseBase,
            dielectricF0,
            max(dot(normal, normalize(light.Position - worldPosition)), 0.0),
            nDotV) * GI_MATERIAL_PI;
        vec3 specularF0 = mix(dielectricF0, albedo, metallic);
        NjulfAreaLightResult area = EvaluateNjulfAreaLightLtc(
            light,
            worldPosition,
            normal,
            viewDirection,
            roughness,
            diffuseReflectance,
            specularF0);
        if (area.rangeAttenuation <= 0.0)
            return;
        shadowFactor = EvaluateAreaRayShadowMask(
            lightIndex,
            light,
            geometryDecal);
        vec3 representativeDirection = normalize(
            area.representativeDirection);
        float representativeNdotL = max(
            dot(normal, representativeDirection),
            0.0);
        vec3 representativeHalf = normalize(
            viewDirection + representativeDirection);
        float representativeHdotV = max(
            dot(representativeHalf, viewDirection),
            0.0);
        float baseSpecularScale = LayeredBaseSpecularScale(
            nDotV,
            representativeNdotL,
            representativeHdotV,
            clearcoatFactor,
            sheenColor,
            sheenRoughness);
        vec3 layeredAreaLighting = area.diffuse +
            max(area.lighting - area.diffuse, vec3(0.0)) *
            baseSpecularScale;

        if (clearcoatFactor > 0.0)
        {
            NjulfAreaLightResult clearcoatArea =
                EvaluateNjulfAreaLightLtc(
                    light,
                    worldPosition,
                    clearcoatNormal,
                    viewDirection,
                    clearcoatRoughness,
                    vec3(0.0),
                    vec3(0.04));
            layeredAreaLighting += clearcoatArea.lighting *
                clearcoatFactor;
        }
        if (MaxComponent(sheenColor) > 0.0)
        {
            NjulfAreaLightResult unitDiffuseArea =
                EvaluateNjulfAreaLightLtc(
                    light,
                    worldPosition,
                    normal,
                    viewDirection,
                    max(sheenRoughness, 0.07),
                    vec3(1.0),
                    vec3(0.0));
            float sheenDirectional = SheenDirectionalAlbedo(
                nDotV,
                sheenRoughness);
            layeredAreaLighting += unitDiffuseArea.diffuse *
                sheenColor * sheenDirectional;
        }
        directLighting += layeredAreaLighting * shadowFactor;
        directDiffuseSource += area.diffuse * shadowFactor;
        lightDirection = area.representativeDirection;
        return;
    }
    else if (NjulfIsPunctualLight(light))
    {
        vec3 toLight = light.Position - worldPosition;
        float distanceToLight = length(toLight);
        if (distanceToLight >= light.Range || light.Range <= 0.0)
            return;

        lightDirection = toLight / max(distanceToLight, 0.0001);
        signedNdotL = dot(normal, lightDirection);
        if (signedNdotL > 0.0)
        {
            shadowEvaluationNormal = shadowNormal;
        }
        else if (signedNdotL < 0.0 && subsurfaceBacklightingActive)
        {
            backSide = true;
            shadowEvaluationNormal = -shadowNormal;
        }
        else
        {
            return;
        }

        attenuation = EvaluateNjulfLightDistanceAttenuation(
            light,
            distanceToLight);

        attenuation *= EvaluateNjulfIesProfile(light, -lightDirection);

        if (light.Type == GPU_LIGHT_TYPE_SPOT)
        {
            attenuation *= EvaluateNjulfSpotAttenuation(
                light,
                lightDirection);
            if (attenuation <= 0.0)
                return;
            shadowFactor = EvaluateSpotShadow(
                lightIndex,
                worldPosition,
                shadowEvaluationNormal,
                geometryDecal);
        }
        else if (light.Type == GPU_LIGHT_TYPE_POINT)
        {
            if (attenuation <= 0.0)
                return;
            shadowFactor = EvaluatePointShadow(
                lightIndex,
                worldPosition,
                shadowEvaluationNormal,
                geometryDecal);
        }
    }
    else
    {
        return;
    }

    vec3 radiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    if (backSide)
    {
        float backNdotL = max(-signedNdotL, 0.0);
        float nDotV = max(dot(normal, viewDirection), 0.0);
        vec3 backDiffuse = EvaluateGiDiffuseBrdf(
            subsurfaceDirectionalDiffuseBase,
            dielectricF0,
            backNdotL,
            nDotV);
        directBackDiffuseSource +=
            backDiffuse * radiance * backNdotL * shadowFactor;
        return;
    }

    vec3 diffuseContribution;
    directLighting += EvaluatePbrLight(
        albedo,
        metallic,
        directionalDiffuseBase,
        roughness,
        dielectricF0,
        clearcoatFactor,
        clearcoatRoughness,
        clearcoatNormal,
        sheenColor,
        sheenRoughness,
        normal,
        viewDirection,
        lightDirection,
        radiance,
        diffuseContribution) * shadowFactor;
    directDiffuseSource += diffuseContribution * shadowFactor;
}

#if DIRECTIONAL_TRANSPARENT_RAY_QUERY && \
    !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
uint ForwardThickTransmissionSeed(
    uint stableObjectIdentity,
    uint materialRevision)
{
    uvec2 pixel = uvec2(max(floor(ForwardScreenPixel()), vec2(0.0)));
    return ThickTransmissionHash(
        stableObjectIdentity ^ materialRevision ^
        pixel.x * 0x9e3779b9u ^ pixel.y * 0x85ebca6bu);
}

vec3 ForwardInitialWaterScatterNormal(
    GPUMaterialData material,
    GPUMaterialExtensionData extensionData,
    vec3 orientedNormal)
{
    if (OpticalMaterialBoundaryKind(extensionData) !=
            OPTICAL_BOUNDARY_WATER_SURFACE ||
        material.NormalTextureIndex < FIRST_TEXTURE_INDEX ||
        material.NormalTextureIndex >= FIRST_TEXTURE_INDEX + MAX_TEXTURES)
    {
        return orientedNormal;
    }
    vec3 tangent = normalize(fragWorldTangent.xyz);
    tangent = normalize(tangent - orientedNormal *
        dot(tangent, orientedNormal));
    vec3 bitangent = normalize(cross(orientedNormal, tangent) *
        fragWorldTangent.w);
    vec2 baseUv = int(round(material.TextureTexCoordSets.y)) == 1
        ? fragTexCoord2 : fragTexCoord;
    baseUv = GiCausticTextureTransform(
        baseUv, material.NormalOffsetScale, material.TextureRotations.y);
    vec2 scales = max(
        OpticalMaterialWaterUvScales(extensionData), vec2(0.001));
    vec2 uv0 = baseUv * scales.x +
        OpticalMaterialWaterVelocity0(extensionData) * pc.Push.Time;
    vec2 uv1 = baseUv * scales.y +
        OpticalMaterialWaterVelocity1(extensionData) * pc.Push.Time;
    vec2 wave0 = textureLod(
        BindlessTextures[nonuniformEXT(material.NormalTextureIndex)],
        uv0, 0.0).xy * 2.0 - 1.0;
    vec2 wave1 = textureLod(
        BindlessTextures[nonuniformEXT(material.NormalTextureIndex)],
        uv1, 0.0).xy * 2.0 - 1.0;
    vec2 wave = 0.5 * (wave0 + wave1) *
        max(material.NormalScaleBias.x, 0.0);
    vec3 waterNormal = normalize(orientedNormal +
        tangent * wave.x + bitangent * wave.y);
    return dot(waterNormal, orientedNormal) > 0.0
        ? waterNormal : orientedNormal;
}

#if !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
struct ForwardTerminalDdgiSample
{
    vec3 Irradiance;
    float Ownership;
    float TransportVisibility;
};

ForwardTerminalDdgiSample ForwardSampleSimpleDdgiTerminalReadOnly(
    SimpleDdgiParams p,
    vec3 worldPosition,
    vec3 surfaceNormal,
    vec3 viewDirection)
{
    ForwardTerminalDdgiSample result;
    result.Irradiance = vec3(0.0);
    result.Ownership = 0.0;
    result.TransportVisibility = 0.0;
    if ((p.flags &
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) !=
            (SIMPLE_DDGI_FLAG_ENABLED |
             SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) ||
        p.probeCount == 0u || p.volumeCount == 0u)
    {
        return result;
    }

    vec3 safeNormal = length(surfaceNormal) > 0.00001
        ? normalize(surfaceNormal)
        : vec3(0.0, 1.0, 0.0);
    uint volumeIndex;
    SimpleDdgiVolume volume;
    float edgeWeight;
    bool ignoredRefinementFallback;
    if (!SelectSimpleDdgiVolume(
            p,
            worldPosition,
            true,
            volumeIndex,
            volume,
            edgeWeight,
            ignoredRefinementFallback))
    {
        return result;
    }

    bool ignoredBiasOutsideDomain;
    vec3 samplePosition = SimpleDdgiResolveInterpolationPosition(
        volume,
        worldPosition,
        safeNormal,
        viewDirection,
        p,
        ignoredBiasOutsideDomain);
    vec3 grid = (samplePosition - volume.origin) / volume.spacing;
    vec3 baseF = floor(grid);
    vec3 fraction = clamp(grid - baseF, vec3(0.0), vec3(1.0));
    ivec3 base = ivec3(baseF);
    SimpleDdgiVolumePaging paging = ReadSimpleDdgiVolumePaging(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX),
        volumeIndex);
    vec3 accumulated = vec3(0.0);
    float availableMass = 0.0;
    float directionalMass = 0.0;
    float visibleMass = 0.0;

    // Secondary refracted terminals consume the already-published field but
    // are not independent screen receivers. Keep this loop side-effect free:
    // no residency demand, diagnostic, or contribution feedback is emitted.
    for (uint z = 0u; z < 2u; ++z)
    for (uint y = 0u; y < 2u; ++y)
    for (uint x = 0u; x < 2u; ++x)
    {
        ivec3 coordinate = base + ivec3(int(x), int(y), int(z));
        if (any(lessThan(coordinate, ivec3(0))) ||
            any(greaterThanEqual(coordinate, ivec3(volume.gridCount))))
        {
            continue;
        }

        vec3 cornerWeight = mix(
            1.0 - fraction,
            fraction,
            vec3(x, y, z));
        float trilinear =
            cornerWeight.x * cornerWeight.y * cornerWeight.z;
        SimpleDdgiProbeAddress address = ResolveSimpleDdgiReceiverProbeAddress(
            p,
            volume,
            paging,
            uvec3(coordinate));
        if (!address.resident || !address.published)
            continue;

        SimpleDdgiReceiverProbe probe = ReadSimpleDdgiReceiverProbe(
            uint(SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX),
            address.virtualProbeIndex,
            volume.spacing);
        if (!SimpleDdgiReceiverProbeSupportsGather(probe) ||
            probe.atlasProbeAddress ==
                SIMPLE_DDGI_RECEIVER_INVALID_ATLAS_ADDRESS ||
            probe.atlasProbeAddress >= p.physicalProbeCapacity)
        {
            continue;
        }

        SimpleDdgiAtlasAddress atlasAddress;
        if (!TryBuildSimpleDdgiAtlasAddress(
                p,
                volume,
                paging,
                probe.atlasProbeAddress,
                atlasAddress))
        {
            continue;
        }

        vec3 probePosition = volume.origin +
            vec3(coordinate) * volume.spacing + probe.relocation;
        vec3 toSurface = samplePosition - probePosition;
        float distanceToProbe = length(toSurface);
        vec3 probeToSurface = distanceToProbe > 0.00001
            ? toSurface / distanceToProbe
            : safeNormal;
        vec4 irradiance = SampleSimpleDdgiIrradianceBilinearAtAddress(
            p.publishedIrradianceAtlasBufferIndex,
            atlasAddress,
            safeNormal,
            p.irradianceTexels,
            p);
        if (!SimpleDdgiAtlasSupportsGather(irradiance))
            continue;

        vec2 moments = SampleSimpleDdgiVisibilityBilinearAtAddress(
            uint(SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX),
            atlasAddress,
            probeToSurface,
            p.visibilityTexels,
            p);
        float halfLambert = clamp(
            dot(safeNormal, -probeToSurface) * 0.5 + 0.5,
            0.0,
            1.0);
        float directionalWeight =
            (halfLambert * halfLambert + SIMPLE_DDGI_WRAP_SHADING_OFFSET) /
            (1.0 + SIMPLE_DDGI_WRAP_SHADING_OFFSET);
        float dataWeight = trilinear * clamp(probe.activeWeight, 0.0, 1.0);
        float visibilityBias = clamp(
            0.03 * p.selfShadowBiasScale * volume.spacing,
            0.002,
            volume.spacing * 0.10);
        float biasedDistance = max(distanceToProbe - visibilityBias, 0.0);
        float transportVisibility = SimpleDdgiChebyshev(
            moments.x,
            moments.y,
            biasedDistance,
            volume.spacing);
        transportVisibility = SimpleDdgiApplyNearVisibilitySidecar(
            p,
            volume,
            atlasAddress,
            probeToSurface,
            biasedDistance,
            transportVisibility);
        float selectedWeight = dataWeight * directionalWeight *
            SimpleDdgiVisibilitySelectionWeight(transportVisibility);
        accumulated += max(irradiance.rgb, vec3(0.0)) * selectedWeight;
        availableMass += dataWeight;
        directionalMass += selectedWeight;
        visibleMass += selectedWeight * transportVisibility;
    }

    result.Irradiance = directionalMass > 0.000001
        ? clamp(accumulated / directionalMass, vec3(0.0), vec3(64.0))
        : vec3(0.0);
    result.Ownership = clamp(availableMass * edgeWeight, 0.0, 1.0);
    result.TransportVisibility = directionalMass > 0.000001
        ? clamp(visibleMass / directionalMass, 0.0, 1.0)
        : 0.0;
    return result;
}
#endif

vec3 ForwardEvaluateThickTerminalRadiance(
    ThickTransmissionPathResult path,
    GPUEnvironmentData environment)
{
    if (path.Miss != 0u)
    {
        return SampleEnvironmentPrefilteredRadiance(
            environment,
            path.Direction,
            0.0);
    }

    RayQuerySurfaceHit hit = path.TerminalHit;
    vec4 baseColor = RayQuerySurfaceSampleBaseColor(hit);
    vec2 metallicRoughness =
        RayQuerySurfaceSampleMetallicRoughness(hit);
    float metallic = metallicRoughness.x;
    float roughness = max(metallicRoughness.y, 0.04);
    vec3 normal = RayQuerySurfaceOrientedNormal(hit);
    vec3 viewDirection = normalize(-path.Direction);
    vec3 diffuseBase = baseColor.rgb * (1.0 - metallic);
    float terminalIor = 1.5;
    if (hit.Material.ExtensionDataIndex >= 0)
    {
        GPUMaterialExtensionData terminalExtension =
            ReadMaterialExtension(uint(hit.Material.ExtensionDataIndex));
        if (DielectricFinite(terminalExtension.Transmission.y) &&
            terminalExtension.Transmission.y >= 1.0 &&
            terminalExtension.Transmission.y <= 4.0)
        {
            terminalIor = terminalExtension.Transmission.y;
        }
    }
    vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
        terminalIor, 1.0, vec3(1.0));
    vec3 direct = vec3(0.0);
    vec3 ignoredDiffuse = vec3(0.0);
    vec3 ignoredBackDiffuse = vec3(0.0);
    float ignoredShadow;
    uint ignoredCascade;
    vec3 ignoredShadowNormal;
    for (uint lightIndex = 0u;
         lightIndex < ForwardTotalLightCount(pc.Push);
         ++lightIndex)
    {
        AccumulateLight(
            lightIndex,
            baseColor.rgb,
            metallic,
            diffuseBase,
            vec3(0.0),
            false,
            roughness,
            dielectricF0,
            0.0,
            0.04,
            normal,
            vec3(0.0),
            0.0,
            normal,
            normal,
            viewDirection,
            hit.Position,
            false,
            ignoredShadow,
            ignoredCascade,
            ignoredShadowNormal,
            direct,
            ignoredDiffuse,
            ignoredBackDiffuse);
    }

    vec3 environmentDiffuse = EvaluateEnvironmentDiffuseIrradiance(
        environment, normal) * diffuseBase / GI_MATERIAL_PI;
    vec3 indirect = environmentDiffuse;
#if !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    if (ForwardGlobalIlluminationEnabled() != 0u)
    {
        SimpleDdgiParams params = ReadSimpleDdgiParams(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
        ForwardTerminalDdgiSample terminalDdgi =
            ForwardSampleSimpleDdgiTerminalReadOnly(
            params, hit.Position, normal, viewDirection);
        float visibilityConfidence = smoothstep(
            SIMPLE_DDGI_VISIBILITY_SELECTION_LOW,
            SIMPLE_DDGI_VISIBILITY_SELECTION_HIGH,
            terminalDdgi.TransportVisibility);
        float leak = clamp(
            mix(
                1.0,
                visibilityConfidence,
                params.thinWallLeakClampStrength),
            0.05,
            1.0);
        vec3 ddgi = EvaluateGiDiffuseFromIrradiance(
            terminalDdgi.Irradiance *
                max(params.indirectIntensity, 0.0),
            diffuseBase);
        indirect = ddgi * terminalDdgi.Ownership * leak +
            environmentDiffuse * (1.0 - terminalDdgi.Ownership);
    }
#endif
    vec3 emissive = RayQuerySurfaceSampleEmissive(hit);
    return max(direct + indirect + emissive, vec3(0.0));
}

bool ForwardTraceThickTransmissionChannel(
    GPUMaterialData material,
    GPUMaterialExtensionData extensionData,
    uint stableObjectIdentity,
    vec3 incidentDirection,
    vec3 scatterNormal,
    float roughness,
    uint randomSeed,
    uint spectralChannel,
    GPUEnvironmentData environment,
    out vec3 radiance,
    out ThickTransmissionPathResult path)
{
    DielectricBoundary boundary;
    GPUMaterialExtensionData resolvedExtension;
    float ignoredRoughness;
    bool dispersionEnabled =
        ForwardThickTransmissionDispersionEnabled() &&
        extensionData.Dispersion.x > 0.0;
    if (!ThickTransmissionResolveBoundary(
            stableObjectIdentity,
            material,
            dispersionEnabled,
            spectralChannel,
            boundary,
            resolvedExtension,
            ignoredRoughness) ||
        !ThickTransmissionTracePath(
            fragWorldPosition,
            incidentDirection,
            scatterNormal,
            boundary,
            gl_FrontFacing,
            roughness,
            ForwardThickTransmissionMaximumInterfaces(),
            ForwardThickTransmissionMaximumMediaDepth(),
            ForwardThickTransmissionMaximumCandidatesPerInterface(),
            max(pc.Push.OcclusionBias, GI_CAUSTIC_RAY_EPSILON * 4.0),
            randomSeed,
            pc.Push.Time,
            dispersionEnabled,
            spectralChannel,
            path))
    {
        radiance = vec3(0.0);
        return false;
    }
    radiance = ForwardEvaluateThickTerminalRadiance(path, environment) *
        path.Throughput;
    return DielectricFinite(radiance) &&
        all(greaterThanEqual(radiance, vec3(0.0)));
}
#endif

void WriteForwardColor(vec4 color)
{
#if NJULF_C5_TRACE_RESOLUTION_SOURCE
    // The source-only program has no SceneColor attachment. Debug paths are
    // rejected by admission; retaining this no-op keeps shared material
    // control flow well-formed without publishing indirect lighting.
    color = color;
#elif FORWARD_WEIGHTED_OIT
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

    vec2 p = ForwardScreenPixel();
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

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
float SimpleDdgiTransparentCompositeWeight(float outputAlpha)
{
    float alpha = clamp(outputAlpha, 0.0, 1.0);
#if FORWARD_WEIGHTED_OIT
    float depthWeight = clamp(
        pow(max(1.0 - gl_FragCoord.z * 0.95, 0.01), 3.0),
        0.01,
        1.0);
    float alphaWeight = max(alpha * 8.0 + 0.01, 0.01);
    float oitWeight = clamp(
        alphaWeight * alphaWeight * alphaWeight * 64.0 * depthWeight,
        0.01,
        3000.0);
    // This is the exact coefficient submitted to the weighted accumulation
    // target. The later normalization is shared by all fragments and cannot be
    // attributed without retaining a full per-pixel fragment list.
    return alpha * oitWeight;
#else
    // Sorted source-over uses opacity as this fragment's compositing
    // coefficient. Draw order supplies the accumulated destination
    // transmittance; feedback deliberately never substitutes fragment count.
    return alpha;
#endif
}

void EmitSimpleDdgiSurfaceReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float physicalSurfaceWeight,
    bool eligible,
    uint producer,
    bool tileNamespaceValid,
    uint tileNamespaceBase)
{
    EmitSimpleDdgiSurfaceReceiverFeedbackCore(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        physicalSurfaceWeight,
        eligible,
        producer,
        pc.Push.CurrentFrameIndex,
        pc.Push.ScreenDimensions,
        tileNamespaceValid,
        tileNamespaceBase,
        uvec3(fragObjectIndex, fragMaterialIndex, fragMeshletIndex));
}

void EmitSimpleDdgiTransparentReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float outputAlpha)
{
    EmitSimpleDdgiSurfaceReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        SimpleDdgiTransparentCompositeWeight(outputAlpha),
        true,
        2u,
        true,
        0u);
}

void EmitSimpleDdgiAlphaMaskReceiverFeedback(
    SimpleDdgiGatherResult gather,
    bool gatherContributed,
    float radiometricOwnership,
    float leakAttenuation,
    float survivingCoverage,
    bool alphaMask,
    float roughDdgiOwnership)
{
    // The fragment has already passed the shipping alpha expression. Its
    // projected raster-sample area is one pixel; sampled alpha supplies the
    // sub-pixel coverage estimate without counting rejected fragments.
    bool reflectionFeedback = ForwardReflectionCaptureEnabled() &&
        !ForwardAutomaticPlanarCaptureEnabled();
    uint tileNamespaceBase = 0u;
    bool tileNamespaceValid = !reflectionFeedback ||
        SimpleDdgiTryComputeCubemapTileNamespace(
            ForwardReflectionCaptureLayer(),
            pc.Push.ScreenDimensions,
            tileNamespaceBase);
    float physicalSurfaceWeight = reflectionFeedback
        ? SimpleDdgiCubemapTexelSolidAngle(
              ForwardScreenPixel(),
              pc.Push.ScreenDimensions) *
          clamp(roughDdgiOwnership, 0.0, 1.0)
        : clamp(survivingCoverage, 0.0, 1.0);
    EmitSimpleDdgiSurfaceReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        physicalSurfaceWeight,
        reflectionFeedback || alphaMask,
        reflectionFeedback ? 5u : 1u,
        tileNamespaceValid,
        tileNamespaceBase);
}
#endif

#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
const float C5_MAXIMUM_FINITE_FP16 = 65504.0;

vec2 C5OctEncodeNormal(vec3 value)
{
    float lengthSquared = dot(value, value);
    if (lengthSquared <= 1.0e-12 || any(isnan(value)) || any(isinf(value)))
        return vec2(0.0);
    vec3 normal = value * inversesqrt(lengthSquared);
    normal /= abs(normal.x) + abs(normal.y) + abs(normal.z);
    if (normal.z < 0.0)
    {
        normal.xy = (vec2(1.0) - abs(normal.yx)) *
            vec2(normal.x >= 0.0 ? 1.0 : -1.0,
                 normal.y >= 0.0 ? 1.0 : -1.0);
    }
    return clamp(normal.xy, vec2(-1.0), vec2(1.0));
}

bool C5CreateReceiverPayload(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 diffuseBase,
    vec3 dielectricF0,
    out uvec4 payload)
{
    payload = uvec4(0u);

    vec2 encodedGeometric = C5OctEncodeNormal(geometricNormal);
    vec2 encodedShading = C5OctEncodeNormal(shadingNormal);
    if (dot(encodedGeometric, encodedGeometric) == 0.0 &&
            abs(geometricNormal.z) < 0.5 ||
        dot(encodedShading, encodedShading) == 0.0 &&
            abs(shadingNormal.z) < 0.5)
    {
        return false;
    }

    // The object publication assigns this frame-local token while building
    // the matching frame-buffered C5 surface table. 0xffff is invalid and
    // 0xfffe is intentionally unassigned, leaving exactly 65,534 entries.
    uint surfaceToken = fragObjectIndex;
    if (surfaceToken >= 65534u ||
        any(isnan(diffuseBase)) || any(isinf(diffuseBase)) ||
        any(isnan(dielectricF0)) || any(isinf(dielectricF0)) ||
        any(lessThan(diffuseBase, vec3(0.0))) ||
        any(lessThan(dielectricF0, vec3(0.0))))
    {
        return false;
    }

    payload = uvec4(
        packSnorm2x16(encodedGeometric),
        packSnorm2x16(encodedShading),
        surfaceToken | (NjulfC5PackRgb565(dielectricF0) << 16u),
        NjulfC5PackRgb9E5(diffuseBase));
    return true;
}

float C5ResolveB3FootprintRadius()
{
    SimpleDdgiParams c5Params = ReadSimpleDdgiParams(
        uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    if ((c5Params.flags & SIMPLE_DDGI_FLAG_ENABLED) == 0u ||
        c5Params.probeCount == 0u || c5Params.volumeCount == 0u)
    {
        return 0.0;
    }
    uint selectedVolumeIndex;
    SimpleDdgiVolume selectedVolume;
    float selectedEdgeWeight;
    bool refinementOrBaseFallback;
    SelectSimpleDdgiVolume(
        c5Params,
        fragWorldPosition,
        selectedVolumeIndex,
        selectedVolume,
        selectedEdgeWeight,
        refinementOrBaseFallback);
    float spacing = selectedVolume.spacing;
    return !isnan(spacing) && !isinf(spacing) && spacing > 0.0
        ? spacing * 0.25
        : 0.0;
}

void C5WriteDirectDiffuseAndEmissiveSource(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 directionalDiffuseBase,
    vec3 dielectricF0,
    vec3 directDiffuseSource,
    vec3 emissive)
{
    bool payloadValid = C5CreateReceiverPayload(
        geometricNormal,
        shadingNormal,
        directionalDiffuseBase,
        dielectricF0,
        outNearFieldReceiverPayload);
    float b3FootprintRadius = payloadValid
        ? C5ResolveB3FootprintRadius()
        : 0.0;
    payloadValid = payloadValid && b3FootprintRadius > 0.0;
    if (!payloadValid)
        outNearFieldReceiverPayload = uvec4(0u);
    outDirectDiffuseAndEmissive = vec4(
        clamp(directDiffuseSource + emissive,
            vec3(0.0), vec3(C5_MAXIMUM_FINITE_FP16)),
        payloadValid ? b3FootprintRadius : 0.0);
}
#endif

#if NJULF_C4_RECEIVER_OUTPUT
bool C4CreateReceiverPayload(
    vec3 geometricNormal,
    vec3 shadingNormal,
    vec3 directionalDiffuseBase,
    vec3 dielectricF0,
    out uvec4 payload)
{
    payload = uvec4(0u);
    float geometricLengthSquared = dot(geometricNormal, geometricNormal);
    float shadingLengthSquared = dot(shadingNormal, shadingNormal);
    if (geometricLengthSquared <= 1.0e-12 ||
        shadingLengthSquared <= 1.0e-12 ||
        any(isnan(geometricNormal)) || any(isinf(geometricNormal)) ||
        any(isnan(shadingNormal)) || any(isinf(shadingNormal)) ||
        any(isnan(directionalDiffuseBase)) ||
        any(isinf(directionalDiffuseBase)) ||
        any(isnan(dielectricF0)) || any(isinf(dielectricF0)) ||
        any(lessThan(directionalDiffuseBase, vec3(0.0))) ||
        any(lessThan(dielectricF0, vec3(0.0))))
    {
        return false;
    }

    payload = uvec4(
        packSnorm2x16(NjulfC4OctEncodeNormal(geometricNormal)),
        packSnorm2x16(NjulfC4OctEncodeNormal(shadingNormal)),
        NjulfC4PackRgb9E5(directionalDiffuseBase),
        NjulfC4PackDielectricF0AndFlags(dielectricF0));
    return NjulfC4ReceiverPayloadValid(payload);
}
#endif

#if FORWARD_THIN_GLASS_ONLY
void main()
{
    EnforceAutomaticPlanarCaptureClip();
    GPUMaterialData material = ReadForwardMaterial(fragMaterialIndex);
    bool doubleSided = material.NormalScaleBias.w >= 0.5;
    if (!doubleSided && !gl_FrontFacing)
        discard;

    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);
    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    MaterialAlphaCoverage materialCoverage = ResolveMaterialAlphaCoverage(
        material,
        albedoSample,
        fragVertexColor.a);
    if (!MaterialCoverageSurvivesForward(materialCoverage))
        discard;

    vec3 geometricNormal = normalize(fragNormal) *
        (gl_FrontFacing ? 1.0 : -1.0);
    bool useNormalTexture =
        material.NormalTextureIndex != DEFAULT_NORMAL_TEXTURE &&
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
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // AmazonBistroMaterialProfile is the explicit authority for window lobe
    // width. Do not multiply it by the FBX material's generic packed channel.
    float roughness = clamp(
        material.MetallicRoughnessAO.y,
        0.04,
        1.0);
    float reflectionSchedulingRoughness =
        EstimateReflectionSchedulingRoughness(
            roughness,
            roughness,
            normal);

    // ThinGlass is an explicit compiled material class, so only the dielectric
    // fields needed by this narrow shader are read from its extension record.
    // Profile defaults keep malformed content transmissive rather than turning
    // a missing extension into an opaque black pane.
    float transmissionFactor = 0.90;
    float ior = 1.50;
    vec3 thinTransmissionTint = vec3(1.0);
    bool hasMaterialExtension =
        material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
    if (hasMaterialExtension)
    {
        vec4 transmission;
        vec4 dispersion;
        ReadForwardThinGlassOptics(
            uint(material.ExtensionDataIndex),
            transmission,
            dispersion);
        transmissionFactor = clamp(transmission.x, 0.0, 1.0);
        ior = clamp(transmission.y, 1.0, 3.0);
        thinTransmissionTint = clamp(
            dispersion.yzw,
            vec3(0.0),
            vec3(1.0));
    }

    SimpleDdgiGatherResult gather = EmptySimpleDdgiGatherResult();
    vec3 ddgiDirectionalRadiance = vec3(0.0);
    float ddgiDirectionalConfidence = 0.0;
    bool gatherContributed = false;
    float radiometricOwnership = 0.0;
    float leakAttenuation = 0.0;
    if (ForwardGlobalIlluminationEnabled() != 0u)
    {
        SimpleDdgiParams params = ReadSimpleDdgiParams(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
        uint directionalMode = SimpleDdgiDirectionalRadianceMode(
            params.residencyFlags);
        uint glossyMode = SimpleDdgiGlossyTransportMode(
            params.residencyFlags);
        bool configured =
            (params.flags &
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
            params.probeCount > 0u &&
            directionalMode != SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_OFF &&
            glossyMode != SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_OFF;
        if (configured)
        {
            SetSimpleDdgiDirectionalRadianceQuery(
                reflect(-viewDirection, normal),
                roughness);
            SetSimpleDdgiDirectionalRadianceQueryEligibilityWeight(1.0);
            gather = SampleSimpleDdgiThinGlassDirectionalGather(
                params,
                fragWorldPosition,
                geometricNormal,
                viewDirection);
            radiometricOwnership = SimpleDdgiRadiometricOwnership(gather);
            leakAttenuation = SimpleDdgiLeakAttenuation(gather, params);
            gatherContributed = radiometricOwnership > 0.000001;
            ddgiDirectionalRadiance = gather.directionalRadiance *
                max(params.indirectIntensity, 0.0);
            // The compact L1 glass receiver owns low-frequency local scene
            // radiance. Its deliberately unresolved high-frequency share is
            // filled by the global environment, preserving crisp highlights
            // without falling back to manually placed probes.
            // L1 represents a larger fraction of a broad/frosted lobe than a
            // sharp pane. Reserve the unresolved sharp band for the global HDR
            // environment so windows retain readable reflections without a
            // local probe, while DDGI remains the authoritative local base.
            float representableFrequencyShare = mix(0.55, 0.85, roughness);
            ddgiDirectionalConfidence = clamp(
                gather.directionalRadianceSupport *
                    radiometricOwnership * representableFrequencyShare,
                0.0,
                1.0);
        }
    }

    GPUEnvironmentData environment = ReadEnvironmentData();
    vec3 reflectedSpecular = vec3(0.0);
    if (environment.Enabled != 0u)
    {
        vec3 reflectionDirection = reflect(-viewDirection, normal);
        float maxLod = max(
            float(environment.PrefilteredMipCount) - 1.0,
            0.0);
        float nDotV = max(dot(normal, viewDirection), 0.0);
        vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
            ior,
            1.0,
            vec3(1.0));
        vec3 fresnel = FresnelSchlickIndirectRoughness(
            nDotV,
            dielectricF0,
            roughness);
        vec2 brdf = texture(
            BindlessTextures[nonuniformEXT(environment.BrdfLutTextureIndex)],
            vec2(nDotV, roughness)).rg;
        bool reflectionDebugActive;
        vec3 reflectionDebugColor;
        reflectedSpecular = EvaluateTransparentReflectionSpecular(
            environment,
            fragWorldPosition,
            geometricNormal,
            reflectionDirection,
            roughness * maxLod,
            roughness,
            reflectionSchedulingRoughness,
            brdf,
            fresnel,
            1.0,
            ddgiDirectionalRadiance,
            ddgiDirectionalConfidence,
            false,
            ForwardMaterialSamplesSceneReflections(material, false),
            reflectionDebugActive,
            reflectionDebugColor);
        if (reflectionDebugActive)
        {
            WriteForwardColor(vec4(reflectionDebugColor, 1.0));
            return;
        }
    }

    float glassNdotV = clamp(
        abs(dot(normalize(normal), viewDirection)),
        0.0,
        1.0);
    float glassF0Ratio = (ior - 1.0) / max(ior + 1.0, 0.0001);
    float glassF0 = glassF0Ratio * glassF0Ratio;
    float glassFresnel = glassF0 +
        (1.0 - glassF0) * pow(1.0 - glassNdotV, 5.0);
    float tintTransmission = dot(
        thinTransmissionTint,
        vec3(0.2126, 0.7152, 0.0722));
    float glassOpacity = clamp(
        1.0 - transmissionFactor * tintTransmission *
            (1.0 - glassFresnel),
        0.08,
        1.0);
    float outputAlpha = min(materialCoverage.Alpha, glassOpacity);
    vec3 color = max(reflectedSpecular, vec3(0.0)) / glassOpacity;

#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    EmitSimpleDdgiTransparentReceiverFeedback(
        gather,
        gatherContributed,
        radiometricOwnership,
        leakAttenuation,
        outputAlpha);
#endif
    WriteForwardColor(vec4(color, outputAlpha));
}
#else
void main()
{
    EnforceAutomaticPlanarCaptureClip();
#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
    // Every non-discard path, including diagnostic early returns, has a
    // defined source attachment value. The C5-capability gate excludes debug
    // views; this initialization is the final shader-side safety net.
    outDirectDiffuseAndEmissive = vec4(0.0);
    outNearFieldReceiverPayload = uvec4(0u);
#endif
#if NJULF_C4_RECEIVER_OUTPUT
    outGiCausticReceiverPayload = uvec4(0u);
#endif
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    outHybridReflectionReceiverPayload = uvec4(0u);
    outHybridReflectionLobeExtension = uvec2(0u);
#endif
    uint debugViewMode = ForwardDebugViewMode();
    uint ambientOcclusionDebugView = ForwardAmbientOcclusionDebugView();
    WriteMaterialTransportProvenance(MATERIAL_TRANSPORT_PROVENANCE_UNKNOWN);
    GPUMaterialData material = ReadForwardMaterial(fragMaterialIndex);
    if (debugViewMode == MATERIAL_DEBUG_TRANSPORT_PROFILE ||
        debugViewMode == MATERIAL_DEBUG_MATERIAL_REVISIONS)
    {
        LoadForwardMaterialDiagnosticMetadata(
            fragMaterialIndex,
            material);
    }
#if FORWARD_THIN_GLASS_ONLY || \
    FORWARD_TRANSPARENT_ROLE_ORDINARY || \
    FORWARD_TRANSPARENT_ROLE_THICK
    const bool geometryDecal = false;
#elif FORWARD_TRANSPARENT_ROLE_DECAL
    const bool geometryDecal = true;
#else
    bool geometryDecal = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_GEOMETRY_DECAL);
#endif
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

#if FORWARD_SIMPLE_MATERIAL
    bool hasMaterialExtension = false;
#else
    bool hasMaterialExtension = material.FeatureFlags != 0u && material.ExtensionDataIndex >= 0;
#endif
    GPUMaterialExtensionData materialExtension;
    if (hasMaterialExtension)
        materialExtension = ReadForwardMaterialExtension(
            uint(material.ExtensionDataIndex),
            material.FeatureFlags);
    vec2 baseColorUv = MaterialUv(
        material.TextureTexCoordSets.x,
        material.BaseColorOffsetScale,
        material.TextureRotations.x);

    vec4 albedoSample = material.AlbedoTextureIndex == DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(material.AlbedoTextureIndex, baseColorUv);
    // Coverage and visible albedo use the same transformed base-color sample.
    // Sampling twice was especially expensive for large alpha-blended decal
    // overlays and provided no semantic difference.
    MaterialAlphaCoverage materialCoverage = ResolveMaterialAlphaCoverage(
        material,
        albedoSample,
        fragVertexColor.a);
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
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
#if FORWARD_DDGI_RECEIVER_CACHE_LEGACY
    ForwardDdgiReceiverCacheAdmission receiverCacheAdmission;
    receiverCacheAdmission.EntryIndex = ForwardDdgiReceiverCacheEntryIndex(
        ForwardScreenPixel(),
        pc.Push.ScreenDimensions);
    receiverCacheAdmission.Reason = SIMPLE_DDGI_RECEIVER_SURFACE_ACCEPTED;
    bool receiverCacheAccepted = true;
    RecordLegacyForwardDdgiReceiverCacheAdmission(
        pc.Push.CurrentFrameIndex);
#else
    // Resolve the complementary split before normal and material-extension
    // shading. Coverage and sidedness have already matched the depth-prepass
    // survivor, so discarding here cannot create a coverage disagreement.
    ForwardDdgiReceiverCacheAdmission receiverCacheAdmission =
        EvaluateForwardDdgiReceiverCacheAdmission(
            ForwardScreenPixel(),
            gl_FragCoord.z,
            fragWorldPosition,
            geometricNormal,
            pc.Push);
    bool receiverCacheAccepted =
        ForwardDdgiReceiverCacheAdmissionAccepted(receiverCacheAdmission);
    RecordForwardDdgiReceiverCacheAdmission(
        pc.Push.CurrentFrameIndex,
        receiverCacheAdmission.Reason);
    if (NjulfReceiverCacheAcceptedLane() && !receiverCacheAccepted)
        discard;
    if (NjulfReceiverCacheExactFallbackLane() && receiverCacheAccepted)
        discard;
#endif
#if NJULF_DDGI_RECEIVER_CACHE_DEBUG_VIEW
    WriteForwardColor(vec4(
        ForwardDdgiReceiverCacheAdmissionDebugColor(
            receiverCacheAdmission.Reason),
        1.0));
    return;
#endif
#endif
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
    vec3 diffuseIndirectNormal = normal;
    vec3 ddgiNormal = geometricNormal;
    vec3 viewDirection = normalize(pc.Push.CameraPosition - fragWorldPosition);

    // glTF metallic-roughness contract: G = roughness and B = metallic.
    // Occlusion is an independent binding even when it aliases the same image.
    vec2 metallicRoughnessUv = MaterialUv(
        material.TextureTexCoordSets.z,
        material.MetallicRoughnessOffsetScale,
        material.TextureRotations.z);
    vec4 armSample = material.MetallicRoughnessTextureIndex == DEFAULT_BLACK_TEXTURE
        ? vec4(1.0, 1.0, 1.0, 1.0)
        : SampleMaterialTexture(
            material.MetallicRoughnessTextureIndex,
            metallicRoughnessUv);
    // The upload contract binds DefaultWhiteTexture for a missing emissive texture.
    // Sample independently of the factor: material.Emissive is the authoritative black
    // default, while a texture-only material remains valid when its factor is non-zero.
    vec4 emissiveSample = material.EmissiveTextureIndex ==
            DEFAULT_WHITE_TEXTURE
        ? vec4(1.0)
        : SampleMaterialTexture(
            material.EmissiveTextureIndex,
            MaterialUv(
                material.TextureTexCoordSets.w,
                material.EmissiveOffsetScale,
                material.TextureRotations.w));

    float authoredRoughness = clamp(
        material.MetallicRoughnessAO.y * armSample.g,
        0.04,
        1.0);
    float reflectionFootprintRoughness = authoredRoughness;
#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    if (material.MetallicRoughnessTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        // A derivative-only variance estimate covers one fragment quad, but a
        // dark roughness island can span several quads after texture
        // minification. Such an island scheduled an isolated SSR/ray-query
        // source inside otherwise broad Sponza stone and cloth. A single
        // coarser mip-footprint sample provides the conservative lobe width
        // used by the deferred reflection receiver. Continuous polished
        // regions remain polished because both footprints agree.
        float footprintRoughness = clamp(
            material.MetallicRoughnessAO.y *
                SampleMaterialTextureFootprint(
                    material.MetallicRoughnessTextureIndex,
                    metallicRoughnessUv,
                    4.0).g,
            0.04,
            1.0);
        reflectionFootprintRoughness = max(
            reflectionFootprintRoughness,
            footprintRoughness);
    }
#endif
    float roughness = authoredRoughness;
    float metallic = clamp(material.MetallicRoughnessAO.x * armSample.b, 0.0, 1.0);
    // Preserve the isotropic lobe width for reflection scheduling. The
    // anisotropic BRDF adjustment below sharpens one axis, but a brushed lobe
    // remains broad overall and does not warrant isotropic full-rate tracing.
    float reflectionSchedulingRoughness = roughness;
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
    // Probe visibility owns broad transport occlusion, but cannot represent
    // sub-probe contacts. Retain half of screen-space AO for that missing local
    // band instead of either double-darkening DDGI or leaving it uniformly flat.
    float ddgiIndirectAo = mix(1.0, screenSpaceAo, 0.5);
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
    vec3 thinTransmissionTint = vec3(1.0);
    vec3 clearcoatNormal = normal;
    // ThinSurface is normally a GI-only transport contract (for example,
    // opaque curtains). ThinGlass is the explicit visible dielectric opt-in;
    // keeping these independent prevents cloth from becoming accidental glass.
#if FORWARD_THIN_GLASS_ONLY
    const bool thinGiTransport = true;
    const bool thinGlass = true;
    const bool volumeGiTransport = false;
#elif FORWARD_TRANSPARENT_ROLE_DECAL
    const bool thinGiTransport = false;
    const bool thinGlass = false;
    const bool volumeGiTransport = false;
#elif FORWARD_TRANSPARENT_ROLE_THICK
    const bool thinGiTransport = false;
    const bool thinGlass = false;
    const bool volumeGiTransport = true;
#elif FORWARD_TRANSPARENT_ROLE_ORDINARY
    bool thinGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    bool thinGlass = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_GLASS);
    const bool volumeGiTransport = false;
#else
    bool thinGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_SURFACE_TRANSMISSION);
    bool thinGlass = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_THIN_GLASS);
    bool volumeGiTransport = GiMaterialHasFlag(
        material.TransportFlags,
        GI_MATERIAL_VOLUME_TRANSMISSION);
#endif
    bool rasterTransmissionEnabled =
#if FORWARD_TRANSPARENT_ROLE_DECAL
        false;
#else
        (material.FeatureFlags & MATERIAL_FEATURE_TRANSMISSION) != 0u &&
        (!thinGiTransport || thinGlass);
#endif

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
            if ((material.FeatureFlags &
                    MATERIAL_FEATURE_CLEARCOAT_NORMAL_TEXTURE) != 0u)
            {
                vec2 clearcoatUv = ExtensionUv(
                    materialExtension.ClearcoatNormalOffsetScale,
                    materialExtension.ExtensionTextureRotations0.z,
                    materialExtension.ExtensionTextureTexCoordSets0.z);
                vec3 clearcoatTangentNormal =
                    SampleMaterialTextureFootprint(
                        materialExtension.ClearcoatNormalTextureIndex,
                        clearcoatUv,
                        4.0).xyz * 2.0 - 1.0;
                clearcoatTangentNormal.xy *=
                    materialExtension.Clearcoat.z;
                clearcoatTangentNormal.z = sqrt(max(
                    0.0,
                    1.0 - dot(
                        clearcoatTangentNormal.xy,
                        clearcoatTangentNormal.xy)));
                float facingSign = gl_FrontFacing ? 1.0 : -1.0;
                clearcoatNormal = normalize(
                    BuildOrthonormalTbn(
                        fragNormal,
                        fragWorldTangent,
                        facingSign) *
                    normalize(clearcoatTangentNormal));
            }
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
        thinTransmissionTint = clamp(
            materialExtension.Dispersion.yzw,
            vec3(0.0),
            vec3(1.0));
    }

    roughness = ApplyGeometricSpecularAntialiasing(
        roughness,
        normal);
    clearcoatRoughness = ApplyGeometricSpecularAntialiasing(
        clearcoatRoughness,
        clearcoatNormal);
    reflectionSchedulingRoughness = max(
        roughness,
        EstimateReflectionSchedulingRoughness(
            roughness,
            reflectionFootprintRoughness,
            normal));

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
    if (thinGlass)
    {
        // The Bistro glass base color is a transmission tint, not a Lambertian
        // paint layer. Keep the material in GI transport so it can transmit
        // energy, but remove diffuse raster lighting from the visible sheet.
        directionalDiffuseBase = vec3(0.0);
        canonicalDiffuseReflectance = vec3(0.0);
    }

    subsurfaceColor = clamp(
        subsurfaceColor,
        vec3(0.0),
        vec3(1.0));
    subsurfaceStrength = clamp(subsurfaceStrength, 0.0, 1.0);
    vec3 subsurfaceDirectionalDiffuseBase =
        EvaluateGiSubsurfaceDiffuseBudget(
            directionalDiffuseBase,
            subsurfaceColor);
    vec3 subsurfaceDiffuseReflectance =
        EvaluateGiSubsurfaceDiffuseBudget(
            canonicalDiffuseReflectance,
            subsurfaceColor);
    bool subsurfaceBacklightingActive =
        subsurfaceStrength > 0.000001 &&
        any(greaterThan(
            subsurfaceDirectionalDiffuseBase,
            vec3(0.000001)));

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

#if NJULF_GTAO_BENT_NORMAL_LIGHTING
    // Resolve only after material-debug and unlit early-outs. The sample is
    // shared by environment diffuse and, at Ultra, the exact DDGI lookup.
    bool bentNormalValid = TryResolveIndirectDiffuseNormal(
        normal,
        diffuseIndirectNormal);
    if (ForwardAmbientOcclusionBentNormalMode() == 2u && bentNormalValid)
        ddgiNormal = diffuseIndirectNormal;
#endif

    vec3 diffuseIbl = vec3(0.0);
    vec3 specularIbl = vec3(0.0);
    bool reflectionDebugActive = false;
    vec3 reflectionDebugColor = vec3(0.0);
    vec3 dielectricF0 = EvaluateGiMaterialDielectricF0(
        ior,
        specularFactor,
        specularColor);

    SimpleDdgiGatherResult precomputedSimpleDdgiGather =
        EmptySimpleDdgiGatherResult();
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
    bool exactFeedbackGatherContributed = false;
    float exactFeedbackRadiometricOwnership = 0.0;
    float exactFeedbackLeakAttenuation = 0.0;
    float exactFeedbackRoughDdgiOwnership = 0.0;
#endif
    vec3 ddgiDirectionalRadiance = vec3(0.0);
    float ddgiDirectionalConfidence = 0.0;
    float indirectSpecularVisibility = 1.0;
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    ForwardDdgiReceiverCacheSample cachedGather;
    cachedGather.Packed = uvec4(0u);
#endif
#if !FORWARD_GLOBAL_ILLUMINATION_DISABLED && \
    !FORWARD_DDGI_RECEIVER_CACHE_LEGACY
    if (!NjulfReceiverCacheAcceptedLane())
    {
    bool directionalGlobalIlluminationEnabled = geometryDecal
        ? ForwardDecalGlobalIlluminationEnabled()
        : ForwardGlobalIlluminationEnabled() != 0u;
    if (directionalGlobalIlluminationEnabled)
    {
        SimpleDdgiParams directionalParams = ReadSimpleDdgiParams(
            uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
        uint directionalMode = SimpleDdgiDirectionalRadianceMode(
            directionalParams.residencyFlags);
        uint glossyMode = SimpleDdgiGlossyTransportMode(
            directionalParams.residencyFlags);
        float roughnessWeight = SimpleDdgiRoughSpecularWeight(
            directionalParams.residencyFlags,
            roughness);
        bool directionalConfigured =
            (directionalParams.flags &
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
            directionalParams.probeCount > 0u &&
            directionalMode !=
                SIMPLE_DDGI_DIRECTIONAL_RADIANCE_MODE_OFF &&
            glossyMode != SIMPLE_DDGI_GLOSSY_TRANSPORT_MODE_OFF &&
            (roughnessWeight > 0.0 || thinGlass);
        bool diffuseGatherRequired =
            (directionalParams.flags &
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED)) ==
                (SIMPLE_DDGI_FLAG_ENABLED |
                 SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) &&
            directionalParams.probeCount > 0u;
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
        bool receiverCompactDirectionalResolved = !directionalConfigured;
#if FORWARD_DDGI_CACHE_HYBRID_OWNERSHIP_LOCKED
        if (NjulfPerformanceOptimizationEnabled(
                NJULF_PERFORMANCE_HYBRID_PROJECTION_ELISION))
        {
        // Accepted opaque receivers have no forward directional-specular
        // owner in this artifact.  Mark the directional requirement resolved
        // without touching the compact L2 record; rejected/exception paths
        // still fall through to the authoritative exact gather below.
            receiverCompactDirectionalResolved =
                receiverCompactDirectionalResolved || receiverCacheAccepted;
        }
        else
#endif
        {
            if (receiverCacheAccepted && directionalConfigured)
            {
                vec3 compactDirectionalRadiance;
                float compactDirectionalConfidence;
#if NJULF_DDGI_RECEIVER_CACHE_DIAGNOSTICS
                IncrementSimpleDdgiReceiverCacheDiagnostic(
                    pc.Push.CurrentFrameIndex,
                    SIMPLE_DDGI_RECEIVER_CACHE_DIRECTIONAL_EVALUATION_COUNTER);
#endif
                receiverCompactDirectionalResolved =
                    SampleForwardDdgiCompactDirectionalRadiance(
                        ForwardScreenPixel(),
                        gl_FragCoord.z,
                        fragWorldPosition,
                        ddgiNormal,
                        reflect(-viewDirection, normal),
                        roughness,
                        directionalMode,
                        directionalParams.frameIndex,
                        pc.Push,
                        compactDirectionalRadiance,
                        compactDirectionalConfidence);
                if (receiverCompactDirectionalResolved)
                {
                    ddgiDirectionalRadiance = compactDirectionalRadiance *
                        max(directionalParams.indirectIntensity, 0.0);
                    ddgiDirectionalConfidence = compactDirectionalConfidence;
                }
            }
        }
        bool exactGatherRequired =
            !receiverCacheAccepted ||
            !receiverCompactDirectionalResolved ||
            (ForwardAmbientOcclusionBentNormalMode() == 2u &&
             bentNormalValid);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
        // Cache-required opaque artifacts also own B1 attribution. Only a
        // surviving alpha-mask fragment pays for the exact structured gather;
        // opaque receivers retain the cache fast path.
        exactGatherRequired = exactGatherRequired ||
            (alphaMode > 0.5 && alphaMode < 1.5);
#endif
        if (exactGatherRequired && diffuseGatherRequired)
#else
        if (diffuseGatherRequired)
#endif
        {
            if (directionalConfigured)
            {
                SetSimpleDdgiDirectionalRadianceQuery(
                    reflect(-viewDirection, normal),
                    roughness);
                if (thinGlass)
                {
                    // Transparent sheets have no deferred SSR target. Their
                    // explicit ThinGlass classification therefore admits the
                    // directional DDGI field as the default reflected scene
                    // at any roughness, with the environment only filling
                    // genuinely unsupported DDGI weight.
                    SetSimpleDdgiDirectionalRadianceQueryEligibilityWeight(
                        1.0);
                }
            }
            precomputedSimpleDdgiGather = SampleSimpleDdgiGather(
                directionalParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection);
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
            // Cache-accepted alpha-mask receivers skip the exact diffuse
            // composition block below, so publish B1 ownership directly from
            // this authoritative gather. ThinGlass uses the same path and
            // explicitly owns its reflection lobe at every roughness.
            exactFeedbackGatherContributed = true;
            exactFeedbackRadiometricOwnership =
                SimpleDdgiRadiometricOwnership(
                    precomputedSimpleDdgiGather);
            exactFeedbackLeakAttenuation = SimpleDdgiLeakAttenuation(
                precomputedSimpleDdgiGather,
                directionalParams);
#if FORWARD_THIN_GLASS_ONLY
            exactFeedbackRoughDdgiOwnership = 1.0;
#else
            exactFeedbackRoughDdgiOwnership =
                SimpleDdgiRoughSpecularWeight(
                    directionalParams.residencyFlags,
                    roughness);
#endif
#endif
            indirectSpecularVisibility =
                SimpleDdgiRoughIndirectSpecularVisibility(
                    precomputedSimpleDdgiGather,
                    directionalParams,
                    roughness);
            if (directionalConfigured)
            {
                ddgiDirectionalRadiance =
                    precomputedSimpleDdgiGather.directionalRadiance *
                    max(directionalParams.indirectIntensity, 0.0);
                ddgiDirectionalConfidence = clamp(
                    precomputedSimpleDdgiGather.directionalRadianceSupport *
                    SimpleDdgiRadiometricOwnership(
                        precomputedSimpleDdgiGather),
                    0.0,
                    1.0);
            }
        }
    }
    }
#endif

    GPUEnvironmentData environment = ReadEnvironmentData();
    vec3 directLighting = vec3(0.0);
    vec3 directDiffuseSource = vec3(0.0);
    vec3 directBackDiffuseSource = vec3(0.0);
    float lastShadowFactor = 1.0;
    uint lastShadowCascade = 0u;
    vec3 lastShadowEvaluationNormal = shadowNormal;

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
        vec2 uv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        WriteForwardColor(vec4(ReconstructNormalFromDepth(uv) * 0.5 + vec3(0.5), 1.0));
        return;
    }

    if (ambientOcclusionDebugView == AO_DEBUG_LINEAR_DEPTH)
    {
        ivec2 depthSize = textureSize(BindlessTextures[nonuniformEXT(DEPTH_TEXTURE_INDEX)], 0);
        ivec2 pixel = ivec2(clamp(ForwardScreenPixel(), vec2(0.0), vec2(depthSize - ivec2(1))));
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

    if (debugViewMode == DEBUG_VIEW_SHADOW_MAP_PREVIEW)
    {
        vec2 previewUv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        uint cascadeCount = max(uint(ReadShadowIndices().y + 0.5), 1u);
        uint previewCascade = min(ForwardDirectionalShadowPreviewCascade(), cascadeCount - 1u);
        uint textureIndex = uint(DIRECTIONAL_SHADOW_TEXTURE_BASE) + previewCascade;
        float depth = texture(BindlessTextures[nonuniformEXT(textureIndex)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SPOT_ATLAS_PREVIEW)
    {
        vec2 previewUv = ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0));
        float depth = texture(BindlessTextures[nonuniformEXT(SPOT_SHADOW_ATLAS_TEXTURE_INDEX)], previewUv).r;
        WriteForwardColor(vec4(vec3(depth), 1.0));
        return;
    }

    uint directionalLightCount = ForwardDirectionalLightCount(pc.Push);
    float directionalShadowFactor = 1.0;
    uint directionalShadowCascade = 0u;
    vec3 directionalShadowEvaluationNormal = shadowNormal;
    int configuredDirectionalShadowLightIndex =
        int(round(ReadShadowIndices().w));
    if (directionalLightCount > 0u)
    {
        uint directionalLightIndex =
            ForwardDirectionalLightIndex(pc.Push, 0u);
        AccumulateLight(
            directionalLightIndex,
            albedo,
            metallic,
            directionalDiffuseBase,
            subsurfaceDirectionalDiffuseBase,
            subsurfaceBacklightingActive,
            roughness,
            dielectricF0,
            clearcoatFactor,
            clearcoatRoughness,
            clearcoatNormal,
            sheenColor,
            sheenRoughness,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            geometryDecal,
            lastShadowFactor,
            lastShadowCascade,
            lastShadowEvaluationNormal,
            directLighting,
            directDiffuseSource,
            directBackDiffuseSource);
        if (int(directionalLightIndex) ==
                configuredDirectionalShadowLightIndex ||
            lastShadowFactor < 1.0 || lastShadowCascade != 0u)
        {
            directionalShadowFactor = lastShadowFactor;
            directionalShadowCascade = lastShadowCascade;
            directionalShadowEvaluationNormal =
                lastShadowEvaluationNormal;
        }
    }
    if (directionalLightCount > 1u)
    {
        uint directionalLightIndex =
            ForwardDirectionalLightIndex(pc.Push, 1u);
        AccumulateLight(
            directionalLightIndex,
            albedo,
            metallic,
            directionalDiffuseBase,
            subsurfaceDirectionalDiffuseBase,
            subsurfaceBacklightingActive,
            roughness,
            dielectricF0,
            clearcoatFactor,
            clearcoatRoughness,
            clearcoatNormal,
            sheenColor,
            sheenRoughness,
            normal,
            shadowNormal,
            viewDirection,
            fragWorldPosition,
            geometryDecal,
            lastShadowFactor,
            lastShadowCascade,
            lastShadowEvaluationNormal,
            directLighting,
            directDiffuseSource,
            directBackDiffuseSource);
        // Prefer the configured shadow owner even when its exact result is the
        // fully-lit cascade-zero sentinel. Retain the non-default fallback for
        // diagnostic configurations that do not publish an owner index.
        if (int(directionalLightIndex) ==
                configuredDirectionalShadowLightIndex ||
            lastShadowFactor < 1.0 || lastShadowCascade != 0u)
        {
            directionalShadowFactor = lastShadowFactor;
            directionalShadowCascade = lastShadowCascade;
            directionalShadowEvaluationNormal =
                lastShadowEvaluationNormal;
        }
    }
    vec3 directionalShadowDebugColor;
    if (TryEvaluateDirectionalShadowDebug(
            debugViewMode,
            fragWorldPosition,
            directionalShadowEvaluationNormal,
            geometryDecal,
            directionalShadowFactor,
            directionalShadowDebugColor))
    {
        WriteForwardColor(vec4(directionalShadowDebugColor, 1.0));
        return;
    }

    if (pc.Push.LocalLightCount == 0u)
    {
        // Directional lights were handled above; there are no tiled local lights.
    }
    else
    {
        vec2 safeScreenSize = max(pc.Push.ScreenDimensions, vec2(1.0));
        uvec2 pixel = uvec2(clamp(
            ForwardScreenPixel(),
            vec2(0.0),
            safeScreenSize - vec2(1.0)));
        uvec2 tile = pixel / uvec2(
            FORWARD_CLUSTER_TILE_SIZE,
            FORWARD_CLUSTER_TILE_SIZE);
        uint tileCountX = uint(ceil(
            safeScreenSize.x / float(FORWARD_CLUSTER_TILE_SIZE)));
        uint tileCountY = uint(ceil(
            safeScreenSize.y / float(FORWARD_CLUSTER_TILE_SIZE)));
        float viewDepth = clamp(
            CameraForwardDistance(fragWorldPosition),
            FORWARD_CLUSTER_NEAR_PLANE,
            FORWARD_CLUSTER_FAR_PLANE);
        float normalizedClusterDepth =
            log(viewDepth / FORWARD_CLUSTER_NEAR_PLANE) /
            log(FORWARD_CLUSTER_FAR_PLANE /
                FORWARD_CLUSTER_NEAR_PLANE);
        uint depthSlice = min(
            uint(clamp(
                floor(normalizedClusterDepth *
                    float(FORWARD_CLUSTER_DEPTH_SLICE_COUNT)),
                0.0,
                float(FORWARD_CLUSTER_DEPTH_SLICE_COUNT - 1u))),
            FORWARD_CLUSTER_DEPTH_SLICE_COUNT - 1u);
        uint clusterIndex =
            (depthSlice * tileCountY + tile.y) * tileCountX + tile.x;
        GPUTiledLightHeader tileHeader =
            ReadTiledLightHeader(clusterIndex);

        for (uint i = 0u; i < tileHeader.LightCount; i++)
        {
            AccumulateLight(
                ReadTiledLightIndex(tileHeader.LightOffset + i),
                albedo,
                metallic,
                directionalDiffuseBase,
                subsurfaceDirectionalDiffuseBase,
                subsurfaceBacklightingActive,
                roughness,
                dielectricF0,
                clearcoatFactor,
                clearcoatRoughness,
                clearcoatNormal,
                sheenColor,
                sheenRoughness,
                normal,
                shadowNormal,
                viewDirection,
                fragWorldPosition,
                geometryDecal,
                lastShadowFactor,
                lastShadowCascade,
                lastShadowEvaluationNormal,
                directLighting,
                directDiffuseSource,
                directBackDiffuseSource);
        }
    }

    if (subsurfaceStrength > 0.0)
    {
        vec3 originalDirectDiffuseSource = directDiffuseSource;
        directDiffuseSource = ApplyGiSubsurfaceDiffuseSplit(
            originalDirectDiffuseSource,
            directBackDiffuseSource,
            subsurfaceStrength);
        directLighting +=
            directDiffuseSource - originalDirectDiffuseSource;
    }

#if NJULF_C5_TRACE_RESOLUTION_SOURCE
    C5WriteDirectDiffuseAndEmissiveSource(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        directDiffuseSource,
        emissive);
    return;
#endif

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
        WriteForwardColor(vec4(vec3(directionalShadowFactor), 1.0));
        return;
    }

    if (debugViewMode == DEBUG_VIEW_SHADOW_CASCADE_OVERLAY)
    {
        vec3 cascadeColor = directionalShadowCascade == 0u ? vec3(0.9, 0.15, 0.1) :
            directionalShadowCascade == 1u ? vec3(0.1, 0.75, 0.2) :
            directionalShadowCascade == 2u ? vec3(0.1, 0.35, 0.95) :
            vec3(0.9, 0.8, 0.1);
        directLighting = mix(directLighting, cascadeColor, 0.35);
    }

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    if (receiverCacheAccepted)
    {
        // Keep the radiance record out of the direct-light loop. Rejected
        // fragments never issue this sixteen-byte load.
        cachedGather = LoadForwardDdgiReceiverCache(
            receiverCacheAdmission.EntryIndex);
        indirectSpecularVisibility =
            SampleForwardDdgiReceiverCacheRoughSpecularVisibility(
                cachedGather,
                roughness);
    }
#endif

    EvaluateIbl(
        albedo,
        metallic,
        diffuseReflectance,
        roughness,
        reflectionSchedulingRoughness,
        dielectricF0,
        normal,
        diffuseIndirectNormal,
        geometricNormal,
        viewDirection,
        indirectAo,
        indirectSpecularVisibility,
        ddgiDirectionalRadiance,
        ddgiDirectionalConfidence,
        ForwardMaterialSamplesSceneReflections(material, geometryDecal),
        diffuseIbl,
        specularIbl,
        reflectionDebugActive,
        reflectionDebugColor);

#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    if ((!receiverCacheAccepted ||
         (ForwardAmbientOcclusionBentNormalMode() != 0u &&
          bentNormalValid)) &&
        environment.Enabled != 0u)
    {
        // EvaluateIbl deliberately skips diffuse environment work in the
        // accepted cache path. A rejected fragment restores the exact
        // environment owner from the same normal/material inputs.
        vec3 exactEnvironmentIrradiance =
            EvaluateEnvironmentDiffuseIrradiance(
                environment,
                diffuseIndirectNormal);
        diffuseIbl = EvaluateGiDiffuseFromIrradiance(
            exactEnvironmentIrradiance,
            diffuseReflectance);
    }
#endif

#if !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    if (reflectionDebugActive)
    {
        WriteForwardColor(vec4(reflectionDebugColor, forwardDebugOutputAlpha));
        return;
    }
#endif

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

    vec3 subsurfaceBackDiffuseIndirect = vec3(0.0);
    if (subsurfaceStrength > 0.0 &&
        environment.Enabled != 0u &&
        any(greaterThan(
            subsurfaceDiffuseReflectance,
            vec3(0.000001))))
    {
        vec3 subsurfaceBackEnvironmentIrradiance =
            EvaluateEnvironmentDiffuseIrradiance(
                environment,
                -normal);
        subsurfaceBackDiffuseIndirect =
            EvaluateGiDiffuseFromIrradiance(
                subsurfaceBackEnvironmentIrradiance,
                subsurfaceDiffuseReflectance) * indirectAo;
    }

    vec3 finalDiffuseIndirect = vec3(0.0);
#if FORWARD_THIN_GLASS_ONLY
    // ThinGlass participates in GI transport as a transmitting surface but
    // exposes no Lambertian raster lobe. Directional DDGI was already consumed
    // by EvaluateIbl as the default reflected-radiance source.
#elif FORWARD_GLOBAL_ILLUMINATION_DISABLED
    // Benchmark control artifact. This is a separate native program so the
    // A/B delta measures only the incremental cache consumer work and does not
    // retain the sparse-gather graph as dead control flow.
    finalDiffuseIndirect = diffuseIbl * indirectAo;
#else
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    if (receiverCacheAccepted)
    {
        // The admitted sidecar proves that this resolved record belongs to
        // the fragment's local receiver surface. The producer already applied
        // intensity, ownership, leak attenuation, fallback visibility and the
        // Lambert factor; material/AO composition remains fragment exact.
        vec3 cachedDdgiDiffuse =
            ForwardDdgiReceiverCacheDdgiIrradiance(cachedGather) *
            ambientOcclusion * ddgiIndirectAo * diffuseReflectance;
        vec3 cachedEnvironmentDiffuse =
            ForwardDdgiReceiverCacheEnvironmentIrradiance(cachedGather) *
            indirectAo * diffuseReflectance;
        if (ForwardAmbientOcclusionBentNormalMode() != 0u &&
            bentNormalValid)
        {
            // EnvironmentOnly and EnvironmentAndDdgi evaluate the authored
            // bent direction at the fragment. The cache continues to own the
            // DDGI estimate (EnvironmentOnly), admission, and rough-specular
            // visibility without reusing normal-dependent sky irradiance.
            cachedEnvironmentDiffuse = diffuseIbl * indirectAo;
        }

        finalDiffuseIndirect = cachedDdgiDiffuse + cachedEnvironmentDiffuse;
#if !FORWARD_DDGI_RECEIVER_CACHE_LEGACY
        if (!NjulfReceiverCacheAcceptedLane() &&
            ForwardAmbientOcclusionBentNormalMode() == 2u && bentNormalValid)
        {
            // Ultra's DDGI lobe is also bent-normal dependent. Re-evaluate
            // only that lobe from the canonical gather while retaining the
            // cache for receiver admission, environment replacement, and
            // rough-specular visibility. This is the safe visible fallback
            // when no compact diffuse-directional record is available.
            SimpleDdgiParams bentParams = ReadSimpleDdgiParams(
                uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
            SimpleDdgiGatherResult bentGather = precomputedSimpleDdgiGather;
            float bentRadiometricOwnership =
                SimpleDdgiRadiometricOwnership(bentGather);
            float bentLeakAttenuation = SimpleDdgiLeakAttenuation(
                bentGather,
                bentParams);
            float bentOwnership =
                bentRadiometricOwnership * bentLeakAttenuation;
            float bentEnvironmentFallback =
                (1.0 - bentRadiometricOwnership) *
                bentParams.environmentFallbackIntensity;
            vec3 bentDdgiDiffuse = ApplyGiMaterialOcclusion(
                EvaluateGiDiffuseFromIrradiance(
                    bentGather.irradiance * bentParams.indirectIntensity,
                    diffuseReflectance),
                ambientOcclusion * ddgiIndirectAo) * bentOwnership;
            vec3 bentEnvironmentDiffuse = diffuseIbl;
            if (bentEnvironmentFallback >
                    SIMPLE_DDGI_ENVIRONMENT_FALLBACK_MIN_WEIGHT &&
                (bentParams.flags &
                    SIMPLE_DDGI_FLAG_SKY_VISIBILITY_ENABLED) != 0u)
            {
                bentEnvironmentDiffuse *= EstimateFarFieldSkyVisibility(
                    fragWorldPosition,
                    ddgiNormal,
                    bentParams,
                    DdgiSparseDiagnosticSampleWeight());
            }
            finalDiffuseIndirect = bentDdgiDiffuse +
                bentEnvironmentDiffuse * bentEnvironmentFallback * indirectAo;
        }
#endif
    }
#if !FORWARD_DDGI_RECEIVER_CACHE_LEGACY
    if (!NjulfReceiverCacheAcceptedLane() && !receiverCacheAccepted)
    {
#endif
#endif
    bool globalIlluminationEnabled = geometryDecal
        ? ForwardDecalGlobalIlluminationEnabled()
        : ForwardGlobalIlluminationEnabled() != 0u;
    SimpleDdgiParams simpleDdgiParams = ReadSimpleDdgiParams(uint(SIMPLE_DDGI_PARAMS_BUFFER_INDEX));
    bool simpleDdgiConfigured = (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_ENABLED) != 0u && simpleDdgiParams.probeCount > 0u;
    bool simpleDdgiActive = simpleDdgiConfigured &&
        (simpleDdgiParams.flags & SIMPLE_DDGI_FLAG_STRUCTURED_GATHER_ENABLED) != 0u;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
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
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
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
        // Non-cache native programs perform exactly one structured gather per
        // fragment. It feeds both directional specular and diffuse ownership;
        // retaining a second syntactic call site duplicates the optimized
        // residency atomics even when the branches are mutually exclusive.
        simpleGather = precomputedSimpleDdgiGather;
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
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
        exactFeedbackGatherContributed = true;
        exactFeedbackRadiometricOwnership = simpleRadiometricOwnership;
        exactFeedbackLeakAttenuation = simpleLeakAttenuation;
        exactFeedbackRoughDdgiOwnership = SimpleDdgiRoughSpecularWeight(
            simpleDdgiParams.residencyFlags,
            roughness);
#endif
        float simpleOwnership = simpleRadiometricOwnership * simpleLeakAttenuation;
        // Leak attenuation represents blocked transport, not missing field
        // coverage, so it must not be refilled with the environment complement.
        float simpleFallback = (1.0 - simpleRadiometricOwnership) * simpleDdgiParams.environmentFallbackIntensity;
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        simpleDdgiContributingVolumeColor = simpleGather.contributingVolumeColor;
#if NJULF_DDGI_DETAILED_COUNTERS
        simpleDdgiSourceCacheIrradiance = simpleGather.sourceCacheIrradiance;
#endif
        simpleDdgiPrimaryVolume = simpleGather.selectedVolume;
        simpleDdgiSecondaryVolume = simpleGather.secondaryVolume;
        simpleDdgiSecondVolumeUsed = simpleGather.secondVolumeUsed;
        simpleDdgiPrimaryContributionWeight = simpleGather.primaryContributionWeight;
        simpleDdgiSecondaryContributionWeight = simpleGather.secondaryContributionWeight;
#if NJULF_DDGI_DETAILED_COUNTERS
        simpleDdgiCombinedRejectionMask = simpleGather.combinedRejectionMask;
        simpleDdgiFirstRejectionReason = simpleGather.firstRejectionReason;
#endif
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
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
#if NJULF_DDGI_DETAILED_COUNTERS
        float simpleDiagnosticVisibility = simpleGather.transportVisibility;
        float simpleDiagnosticVisibilityMean = 0.0;
        bool sampleSimpleDdgiDebug = IsDdgiDebugView(debugViewMode) ||
            DdgiForwardEstimateDiagnosticPixel();
#else
        bool sampleSimpleDdgiDebug = IsDdgiDebugView(debugViewMode);
#endif
        if (sampleSimpleDdgiDebug)
        {
            SimpleDdgiDebugSample simpleDebug = SampleSimpleDdgiDebug(
                simpleDdgiParams,
                fragWorldPosition,
                ddgiNormal,
                viewDirection);
            ddgiSample.probeIndex = simpleDebug.probeIndex;
            ddgiSample.logicalProbePosition = simpleDebug.logicalProbePosition;
            ddgiSample.relocatedProbePosition = simpleDebug.relocatedProbePosition;
            ddgiSample.relocation = simpleDebug.relocation;
            // Nearest-probe state is diagnostic only; the authoritative
            // receiver estimate above remains the structured eight-corner
            // gather. This makes relocation/state views observable without
            // changing lighting composition or normal-frame buffer traffic.
            ddgiSample.activeProbe = simpleDebug.activeWeight;
            ddgiSample.visibilityMomentMean = simpleDebug.visibilityMomentMean;
            ddgiSample.visibilityMomentVariance = simpleDebug.visibilityMomentVariance;
            ddgiSample.visibilityProbeDistance = simpleDebug.visibilityProbeDistance;
            ddgiSample.visibilityMaxRayDistance = simpleDebug.visibilityMaxRayDistance;
#if NJULF_DDGI_DETAILED_COUNTERS
            simpleDiagnosticVisibility = simpleDebug.visibility;
            simpleDiagnosticVisibilityMean = simpleDebug.visibilityMomentMean;
#endif
            simpleDdgiResidencyTableFlags = simpleDebug.residencyTableFlags;
            simpleDdgiResidencyHistoryFlags = simpleDebug.residencyHistoryFlags;
            simpleDdgiResidencyDemandMask = simpleDebug.residencyDemandMask;
            simpleDdgiPhysicalPageIndex = simpleDebug.physicalPageIndex;
            simpleDdgiPageMappingGeneration = simpleDebug.pageMappingGeneration;
            simpleDdgiPageAgeNormalized = simpleDebug.pageAgeNormalized;
        }
#if NJULF_DDGI_DETAILED_COUNTERS
        AccumulateDdgiVisibilityMomentDiagnostics(
            ddgiSample.visibilityMomentMean,
            ddgiSample.visibilityMomentVariance,
            ddgiSample.visibilityProbeDistance,
            ddgiSample.visibilityMaxRayDistance,
            simpleDiagnosticVisibility,
            ddgiSample.irradianceAtlasConfidence);
#endif
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
            ambientOcclusion * ddgiIndirectAo);
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
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
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
        vec3 diagnosticFinalDiffuseIndirect =
            ApplyGiSubsurfaceDiffuseSplit(
                finalDiffuseIndirect,
                subsurfaceBackDiffuseIndirect,
                subsurfaceStrength);
        HybridDiffuseGiResult simpleHybridDiagnostics;
        simpleHybridDiagnostics.diffuse = diagnosticFinalDiffuseIndirect;
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
            diagnosticFinalDiffuseIndirect);
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
#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
        fallbackWeight = simpleDisabledFallbackWeight;
        hybridDebugDiffuse = finalDiffuseIndirect;
        hybridSuppressionMask = vec3(0.0);
#endif
#if NJULF_DDGI_DETAILED_COUNTERS
        vec3 diagnosticFinalDiffuseIndirect =
            ApplyGiSubsurfaceDiffuseSplit(
                finalDiffuseIndirect,
                subsurfaceBackDiffuseIndirect,
                subsurfaceStrength);
        HybridDiffuseGiResult simpleFallbackDiagnostics;
        simpleFallbackDiagnostics.diffuse = diagnosticFinalDiffuseIndirect;
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
                diagnosticFinalDiffuseIndirect);
        }
#endif
    }

#if !FORWARD_WEIGHTED_OIT && NJULF_MATERIAL_TRANSPORT_PROVENANCE_OUTPUT
    WriteMaterialTransportProvenance(materialTransportProvenance);
#endif
#if FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_LEGACY
    }
#endif
#endif // FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE

    if (subsurfaceStrength > 0.0)
    {
        finalDiffuseIndirect = ApplyGiSubsurfaceDiffuseSplit(
            finalDiffuseIndirect,
            subsurfaceBackDiffuseIndirect,
            subsurfaceStrength);
    }

#if !FORWARD_GI_STATIC_SPECIALIZATION_ACTIVE && \
    !FORWARD_DDGI_RECEIVER_CACHE_REQUIRED_ACTIVE
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT)
    {
        WriteForwardColor(vec4(finalDiffuseIndirect, forwardDebugOutputAlpha));
        return;
    }

    vec2 giDebugUv = clamp(ForwardScreenPixel() / max(pc.Push.ScreenDimensions, vec2(1.0)), vec2(0.0), vec2(1.0));
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
        int shadowLightIndex = int(round(ReadShadowIndices().w));
        if (shadowLightIndex >= 0 &&
            shadowLightIndex < int(ForwardTotalLightCount(pc.Push)))
        {
            uint lightIndex = uint(shadowLightIndex);
            GPULight light = ReadLight(lightIndex);
            farShadow = EstimateFarFieldSunShadow(fragWorldPosition, normal, normalize(-light.Direction));
        }
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_FAR_FIELD_SUN_SHADOW, vec3(farShadow));
        return;
    }

#if NJULF_DDGI_VISUAL_DEBUG_VIEWS
    if (debugViewMode == GLOBAL_ILLUMINATION_DEBUG_DDGI_IRRADIANCE)
    {
        // A logarithmic presentation keeps exact zero black while retaining
        // headroom for the sun-lit tail. Eight linear units map to white; one
        // unit remains mid-bright instead of saturating the whole scene. The
        // raw linear value remains available through DdgiSampledIrradiance.
        vec3 safeIrradiance = max(ddgiSample.irradiance, vec3(0.0));
        float irradianceLuminance = DdgiDiagnosticLuminance(safeIrradiance);
        vec3 presentedIrradiance = vec3(0.0);
        if (irradianceLuminance > 0.00000001)
        {
            const float logScale = 64.0;
            const float referenceWhite = 8.0;
            float presentedLuminance = log2(1.0 + irradianceLuminance * logScale) /
                log2(1.0 + referenceWhite * logScale);
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
            const float logScale = 64.0;
            const float referenceWhite = 8.0;
            float presentedLuminance = log2(1.0 + sourceLuminance * logScale) /
                log2(1.0 + referenceWhite * logScale);
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
            simpleDdgiCombinedRejectionMask != 0u &&
            ddgiSample.supportCoverage <= 0.000001)
        {
            // R = first failing reason, G/B = low/high portions of the combined
            // nine-bit mask. A structured gather normally rejects some of its
            // corner candidates while still producing a valid estimate; showing
            // those routine rejections made healthy receivers look failed. Only
            // expose the rejection payload when the gather has no usable support.
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
        uint updateReason = uint(clamp(ddgiSample.updateReason * 255.0, 0.0, 255.0));
        vec3 updateReasonColor = updateReason != 0u
            ? MeshletDebugColor(updateReason)
            : vec3(0.0);
        WriteDdgiDebugColor(GLOBAL_ILLUMINATION_DEBUG_DDGI_UPDATE_REASONS, updateReasonColor);
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

#if NJULF_C5_DIRECT_DIFFUSE_EMISSIVE_OUTPUT
    // This must remain independent of final scene colour and every indirect
    // owner. AccumulateLight has already applied the exact shadow factor to
    // directDiffuseSource, while emissive follows the frozen material
    // photometric convention.
    C5WriteDirectDiffuseAndEmissiveSource(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        directDiffuseSource,
        emissive);
#endif

#if NJULF_C4_RECEIVER_OUTPUT
    // C4 stores incident photon flux independently. This MRT publishes only
    // current receiver normals and BRDF parameters; the resolve applies them
    // once for each photon direction and the composite adds separate C4
    // radiance exactly once.
    C4CreateReceiverPayload(
        geometricNormal,
        normal,
        directionalDiffuseBase,
        dielectricF0,
        outGiCausticReceiverPayload);
#endif

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    // The deferred reflection pass owns only base indirect specular. Direct
    // light, diffuse GI, emissive, and material extensions remain in SceneColor.
    float layerNdotV = max(dot(normal, viewDirection), 0.0);
    float clearcoatViewFresnel = clearcoatFactor *
        FresnelSchlick(layerNdotV, vec3(0.04)).x;
    float sheenViewEnergy = MaxComponent(clamp(
        sheenColor,
        vec3(0.0),
        vec3(1.0))) * SheenDirectionalAlbedo(
            layerNdotV,
            sheenRoughness);
    float baseLayerSpecularScale = clamp(
        (1.0 - clearcoatViewFresnel) *
        (1.0 - sheenViewEnergy),
        0.0,
        1.0);
    float hybridSpecularOcclusion = clamp(
        pow(indirectAo, 1.0 + roughness) * indirectSpecularVisibility,
        0.0,
        1.0) * baseLayerSpecularScale;
    uint hybridReflectionLobeFlags = 0u;
    if (transmissionFactor >= 0.05)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_TRANSMISSIVE;
    }
    if (anisotropyStrength >= 0.35 &&
        reflectionSchedulingRoughness >= 0.20)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_BROAD_ANISOTROPIC;
    }
    if (clearcoatFactor > 0.0)
    {
        hybridReflectionLobeFlags |=
            NJULF_HYBRID_REFLECTION_LOBE_CLEARCOAT;
    }
    vec3 hybridTangent = fragWorldTangent.xyz - normal *
        dot(fragWorldTangent.xyz, normal);
    if (dot(hybridTangent, hybridTangent) <= 1.0e-12)
        hybridTangent = NjulfHybridReflectionCanonicalTangentBasisX(normal);
    else
        hybridTangent = normalize(hybridTangent);
    vec3 hybridBitangent = normalize(cross(normal, hybridTangent) *
        (fragWorldTangent.w < 0.0 ? -1.0 : 1.0));
    float hybridAnisotropyRotation = hasMaterialExtension
        ? materialExtension.Anisotropy.y
        : 0.0;
    hybridTangent = normalize(
        hybridTangent * cos(hybridAnisotropyRotation) +
        hybridBitangent * sin(hybridAnisotropyRotation));
    NjulfHybridReflectionCreatePayload(
        geometricNormal,
        normal,
        mix(dielectricF0, albedo, metallic),
        roughness,
        reflectionSchedulingRoughness,
        hybridSpecularOcclusion,
        hybridReflectionLobeFlags,
        // Meshlet IDs are rasterization details and change across otherwise
        // continuous surfaces. History identity must remain stable across them.
        uvec3(
            fragObjectIndex,
            fragMaterialIndex,
            material.MaterialRevision),
        outHybridReflectionReceiverPayload);
    outHybridReflectionLobeExtension =
        NjulfHybridReflectionCreateLobeExtension(
            clearcoatNormal,
            clearcoatFactor,
            clearcoatRoughness,
            anisotropyStrength,
            normal,
            hybridTangent);
#endif

#if NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
    vec3 color = finalDiffuseIndirect + directLighting + emissive;
#else
    float layerNdotV = max(dot(normal, viewDirection), 0.0);
    float clearcoatViewFresnel = clearcoatFactor *
        FresnelSchlick(layerNdotV, vec3(0.04)).x;
    float sheenViewEnergy = MaxComponent(clamp(
        sheenColor,
        vec3(0.0),
        vec3(1.0))) * SheenDirectionalAlbedo(
            layerNdotV,
            sheenRoughness);
    float baseLayerSpecularScale = clamp(
        (1.0 - clearcoatViewFresnel) *
        (1.0 - sheenViewEnergy),
        0.0,
        1.0);
    vec3 color = finalDiffuseIndirect +
        specularIbl * baseLayerSpecularScale +
        directLighting + emissive;
#endif

    if (hasMaterialExtension)
    {
        float nDotV = max(dot(normal, viewDirection), 0.0);
        GPUEnvironmentData extensionEnvironment = environment;
#if !NJULF_HYBRID_REFLECTION_RECEIVER_OUTPUT
        if (clearcoatFactor > 0.0 && extensionEnvironment.Enabled != 0u)
        {
            float clearcoatNdotV = max(
                dot(clearcoatNormal, viewDirection),
                0.0);
            vec3 clearcoatReflection = reflect(
                -viewDirection,
                clearcoatNormal);
            float clearcoatMaxLod = max(float(extensionEnvironment.PrefilteredMipCount) - 1.0, 0.0);
            vec3 clearcoatPrefiltered = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                clearcoatReflection,
                clearcoatRoughness * clearcoatMaxLod);
            vec3 clearcoatFresnel = FresnelSchlickRoughness(
                clearcoatNdotV,
                vec3(0.04),
                clearcoatRoughness);
            vec2 clearcoatBrdf = texture(
                BindlessTextures[nonuniformEXT(
                    extensionEnvironment.BrdfLutTextureIndex)],
                vec2(clearcoatNdotV, clearcoatRoughness)).rg;
            color += clearcoatPrefiltered *
                (clearcoatFresnel * clearcoatBrdf.x +
                    clearcoatBrdf.y) *
                clearcoatFactor *
                extensionEnvironment.SpecularIntensity * indirectAo;
        }
#endif

        if (MaxComponent(sheenColor) > 0.0 &&
            extensionEnvironment.Enabled != 0u)
        {
            vec3 sheenReflection = reflect(-viewDirection, normal);
            float sheenMaxLod = max(
                float(extensionEnvironment.PrefilteredMipCount) - 1.0,
                0.0);
            vec3 sheenRadiance = SampleEnvironmentPrefilteredRadiance(
                extensionEnvironment,
                sheenReflection,
                max(sheenRoughness, 0.07) * sheenMaxLod);
            color += sheenRadiance * sheenColor *
                SheenDirectionalAlbedo(nDotV, sheenRoughness) *
                extensionEnvironment.SpecularIntensity * indirectAo;
        }

        if (iridescenceFactor > 0.0 && metallic < 0.5)
        {
            float nDotVFilm = clamp(dot(normal, viewDirection), 0.0, 1.0);
            float phase = iridescenceThickness * 0.018 + (1.0 - nDotVFilm) * 6.2831853;
            vec3 filmTint = 0.5 + 0.5 * cos(vec3(phase, phase + 2.0943951, phase + 4.1887902));
            float filmFresnel = pow(1.0 - nDotVFilm, 3.0);
            color += filmTint * filmFresnel * iridescenceFactor * specularFactor * indirectAo;
        }

        if (transmissionFactor > 0.0)
        {
            if (thinGlass)
            {
                // A zero-thickness sheet exits parallel to the incident ray.
                // Let fixed-function source-over blending retain the already
                // rendered opaque scene behind the window, while this fragment
                // contributes only its Fresnel reflection/lighting. Dividing by
                // opacity converts that reflected radiance to the pipeline's
                // non-premultiplied blend convention instead of attenuating it
                // a second time.
                float glassNdotV = clamp(
                    abs(dot(normalize(normal), viewDirection)),
                    0.0,
                    1.0);
                float glassF0Ratio = (ior - 1.0) / max(ior + 1.0, 0.0001);
                float glassF0 = glassF0Ratio * glassF0Ratio;
                float glassFresnel = glassF0 +
                    (1.0 - glassF0) * pow(1.0 - glassNdotV, 5.0);
                float tintTransmission = dot(
                    thinTransmissionTint,
                    vec3(0.2126, 0.7152, 0.0722));
                float glassOpacity = clamp(
                    1.0 - transmissionFactor *
                        tintTransmission * (1.0 - glassFresnel),
                    0.08,
                    1.0);
                color = max(color, vec3(0.0)) / glassOpacity;
                outputAlpha = min(outputAlpha, glassOpacity);
            }
            else if (extensionEnvironment.Enabled != 0u)
            {
            vec3 incidentDirection = normalize(-viewDirection);
            vec3 orientedNormal = normalize(normal);
            vec3 transmitted = vec3(0.0);
            bool resolvedPhysicalPath = false;
#if DIRECTIONAL_TRANSPARENT_RAY_QUERY && \
    !NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
            if (volumeGiTransport &&
                ForwardThickTransmissionRayQueryEnabled() &&
                ForwardTryReserveThickTransmissionTask())
            {
                GPUObjectData objectData = ReadInstanceData(
                    pc.Push.CurrentFrameIndex,
                    fragObjectIndex);
                uint stableObjectIdentity =
                    objectData.NearFieldStableObjectId;
                vec3 scatterNormal =
                    ForwardInitialWaterScatterNormal(
                        material,
                        materialExtension,
                        orientedNormal);
                uint randomSeed = ForwardThickTransmissionSeed(
                    stableObjectIdentity,
                    material.MaterialRevision);
                ThickTransmissionPathResult centralPath;
                vec3 centralRadiance;
                resolvedPhysicalPath =
                    ForwardTraceThickTransmissionChannel(
                        material,
                        materialExtension,
                        stableObjectIdentity,
                        incidentDirection,
                        scatterNormal,
                        roughness,
                        randomSeed,
                        THICK_TRANSMISSION_SPECTRAL_CENTRAL,
                        extensionEnvironment,
                        centralRadiance,
                        centralPath);
                transmitted = centralRadiance;
                if (resolvedPhysicalPath &&
                    ForwardThickTransmissionDispersionEnabled() &&
                    dispersion > 0.0)
                {
                    ThickTransmissionPathResult redPath;
                    ThickTransmissionPathResult bluePath;
                    vec3 redRadiance;
                    vec3 blueRadiance;
                    bool redValid = ForwardTraceThickTransmissionChannel(
                        material, materialExtension, stableObjectIdentity,
                        incidentDirection, scatterNormal, roughness,
                        randomSeed, THICK_TRANSMISSION_SPECTRAL_RED,
                        extensionEnvironment, redRadiance, redPath);
                    bool blueValid = ForwardTraceThickTransmissionChannel(
                        material, materialExtension, stableObjectIdentity,
                        incidentDirection, scatterNormal, roughness,
                        randomSeed, THICK_TRANSMISSION_SPECTRAL_BLUE,
                        extensionEnvironment, blueRadiance, bluePath);
                    // The central IOR is exactly the green-channel IOR in the
                    // Khronos RGB approximation, so the already-traced central
                    // path is the deterministic green sample.
                    if (redValid && blueValid)
                    {
                        transmitted = vec3(
                            redRadiance.r,
                            centralRadiance.g,
                            blueRadiance.b);
                    }
                }
            }
#endif
            if (!resolvedPhysicalPath)
            {
                float centralReflectance;
                vec3 transmittedDirection;
                bool refracted = DielectricTryRefract(
                    incidentDirection,
                    orientedNormal,
                    1.0,
                    ior,
                    transmittedDirection,
                    centralReflectance);
                if (!refracted)
                    transmittedDirection = normalize(reflect(
                        incidentDirection, orientedNormal));
                float lod = roughness * max(
                    float(extensionEnvironment.PrefilteredMipCount) - 1.0,
                    0.0);
                vec3 transmittedSample =
                    SampleEnvironmentPrefilteredRadiance(
                        extensionEnvironment,
                        transmittedDirection,
                        lod);
                if (ForwardThickTransmissionDispersionEnabled() &&
                    dispersion > 0.0)
                {
                    vec3 rgbIors = DielectricRgbIors(ior, dispersion);
                    vec3 redDirection;
                    vec3 blueDirection;
                    float ignoredReflectance;
                    bool redRefracted = DielectricTryRefract(
                        incidentDirection, orientedNormal, 1.0,
                        rgbIors.r, redDirection, ignoredReflectance);
                    bool blueRefracted = DielectricTryRefract(
                        incidentDirection, orientedNormal, 1.0,
                        rgbIors.b, blueDirection, ignoredReflectance);
                    if (redRefracted)
                    {
                        transmittedSample.r =
                            SampleEnvironmentPrefilteredRadiance(
                                extensionEnvironment,
                                redDirection,
                                lod).r;
                    }
                    if (blueRefracted)
                    {
                        transmittedSample.b =
                            SampleEnvironmentPrefilteredRadiance(
                                extensionEnvironment,
                                blueDirection,
                                lod).b;
                    }
                }
                transmitted = transmittedSample;
                if (attenuationDistance > 0.0 &&
                    transmissionThickness > 0.0)
                {
                    transmitted *= DielectricBeerLambert(
                        DielectricAbsorptionCoefficient(
                            attenuationColor,
                            attenuationDistance),
                        transmissionThickness);
                }
            }

            transmitted *= albedo;
            color = mix(color, transmitted, transmissionFactor);
            outputAlpha = min(outputAlpha, mix(1.0, 0.35, transmissionFactor));
            }
        }
    }

    float finalOutputAlpha =
        alphaMode > 0.5 && alphaMode < 1.5 ? 1.0 : outputAlpha;
#if NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION
#if defined(FORWARD_OPAQUE) || defined(FORWARD_SIMPLE_OPAQUE)
    EmitSimpleDdgiAlphaMaskReceiverFeedback(
        precomputedSimpleDdgiGather,
        exactFeedbackGatherContributed,
        exactFeedbackRadiometricOwnership,
        exactFeedbackLeakAttenuation,
        materialCoverage.Alpha,
        alphaMode > 0.5 && alphaMode < 1.5,
        exactFeedbackRoughDdgiOwnership);
#else
    EmitSimpleDdgiTransparentReceiverFeedback(
        precomputedSimpleDdgiGather,
        exactFeedbackGatherContributed,
        exactFeedbackRadiometricOwnership,
        exactFeedbackLeakAttenuation,
        finalOutputAlpha);
#endif
#endif
    WriteForwardColor(vec4(color, finalOutputAlpha));
}
#endif
