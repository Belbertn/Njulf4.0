#ifndef NJULF_DDGI_UPDATE_SHARED_GLSL
#define NJULF_DDGI_UPDATE_SHARED_GLSL

#include "global_sdf.glsl"

layout(set = 2, binding = 0) uniform accelerationStructureEXT SceneTlas;

layout(push_constant) uniform DdgiUpdatePushBlock
{
    vec4 EnvironmentRadianceAndIntensity;
    vec4 RelocationParams;
    uint ProbeCount;
    uint VolumeCount;
    uint StartProbeIndex;
    uint ProbesToUpdate;
    uint RaysPerProbe;
    uint FrameIndex;
    uint IrradianceTexelsPerProbe;
    uint VisibilityTexelsPerProbe;
    uint ProbeStateBufferIndex;
    uint ProbeUpdateQueueBufferIndex;
    uint RelocationClassificationBufferIndex;
    uint IrradianceAtlasBufferIndex;
    uint VisibilityAtlasBufferIndex;
    uint RayResultScratchBufferIndex;
    uint RayCapacityPerProbe;
    uint CurrentFrameIndex;
    uint Flags;
    uint LightCount;
    uint MaxShadedLights;
    uint DirectionalLightCount;
    uint LocalLightCount;
    uint LightSelectionMode;
    uint PrimaryDirectionalLightIndex;
    uint SelectedLocalLightIndex;
    float SelectedLocalLightEnergyScale;
    uint EmissiveSourceCount;
    uint EmissiveSourceRevision;
    uint MaterialTextureMaxCascade;
    uint FrameSerial;
    uint SurfaceCardBufferIndex;
    uint SurfaceCardCount;
    uint SurfaceRadianceAtlasTextureIndex;
    uint GlobalSdfCascadeBufferIndex;
    uint GlobalSdfCascadeCount;
    uint SdfBackendFirstCascade;
    uint SurfaceCacheFlags;
    uint SurfaceCacheWorkBufferIndex;
} pc;

const float PI = 3.14159265359;
const uint DDGI_UPDATE_FLAG_ENABLED = 1u << 0;
const uint DDGI_UPDATE_FLAG_RELOCATION = 1u << 1;
const uint DDGI_UPDATE_FLAG_CLASSIFICATION = 1u << 2;
const uint DDGI_UPDATE_FLAG_GPU_SCHEDULER = 1u << 3;
const uint DDGI_UPDATE_FLAG_RAW_ATLAS_RADIANCE_CONVENTION = 1u << 4;
const uint DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG = 1u << 5;
const uint DDGI_UPDATE_FLAG_TRACE_ENERGY_DIAGNOSTICS = 1u << 6;
const uint DDGI_UPDATE_FLAG_PROBE_L1_METADATA = 1u << 7;
const uint DDGI_PROBE_UPDATE_REASON_NEW_CELL = 1u << 0;
const uint DDGI_PROBE_UPDATE_REASON_DIRTY_BOUNDS = 1u << 1;
const uint DDGI_PROBE_UPDATE_REASON_VISIBLE_FRUSTUM = 1u << 2;
const uint DDGI_PROBE_UPDATE_REASON_AGE_REFRESH = 1u << 3;
const uint DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP = 1u << 4;
const uint DDGI_PROBE_UPDATE_REASON_OUTSIDE_FRUSTUM_SAFETY = 1u << 6;
const uint DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED = 1u << 8;
const uint DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED = 1u << 9;
const uint DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED = 1u << 10;
const uint DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED = 1u << 11;
const uint DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED = 1u << 12;
const uint DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED = 1u << 13;
const uint DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED = 1u << 14;
const uint DDGI_PROBE_UPDATE_REASON_STREAM_IN = 1u << 15;
const uint DDGI_PROBE_UPDATE_REASON_STREAM_OUT = 1u << 16;
const uint DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP = 1u;
const uint DDGI_LOCAL_SIZE = 64u;
const uint DDGI_MAX_RAYS_PER_PROBE = 256u;
const uint DDGI_MAX_SELECTED_HIT_LIGHTS = 2u;
const uint DDGI_LIGHT_SELECTION_MODE_STOCHASTIC_DIRECTIONAL_LOCAL = 1u;
const uint DDGI_INVALID_LIGHT_INDEX = 0xffffffffu;
const uint DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE = 4u;
const uint DDGI_AUTHORED_VOLUME_CASCADE = 0xffffffffu;
const uint DDGI_RAY_RESULT_STRIDE_WORDS = 20u;
const float DDGI_PROBE_TRACE_EPSILON = 0.02;
const float DDGI_DIFFUSE_ALBEDO = 0.78;
const float DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE = 256.0;
const uint DDGI_TRACE_ENERGY_COUNTER_BASE = 55u;
const uint DDGI_TRACE_ENERGY_SAMPLE_COUNT_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 0u;
const uint DDGI_TRACE_ENERGY_HIT_COUNT_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 1u;
const uint DDGI_TRACE_ENERGY_MISS_COUNT_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 2u;
const uint DDGI_TRACE_ENERGY_RAY_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 3u;
const uint DDGI_TRACE_ENERGY_DIRECT_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 4u;
const uint DDGI_TRACE_ENERGY_EMISSIVE_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 5u;
const uint DDGI_TRACE_ENERGY_STABLE_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 6u;
const uint DDGI_TRACE_ENERGY_SKY_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 7u;
const uint DDGI_TRACE_ENERGY_HIT_ZERO_DIRECT_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 8u;
const uint DDGI_TRACE_ENERGY_HIT_WITH_DIRECT_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 9u;
const uint DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER = DDGI_TRACE_ENERGY_COUNTER_BASE + 10u;
const uint DDGI_TRACE_EARLY_OUT_COUNTER_BASE = 66u;
const uint DDGI_TRACE_EARLY_OUT_DISABLED_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 0u;
const uint DDGI_TRACE_EARLY_OUT_BEYOND_REQUEST_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 1u;
const uint DDGI_TRACE_EARLY_OUT_RESOLVE_BOUNDS_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 2u;
const uint DDGI_TRACE_EARLY_OUT_RESOLVE_PROBE_RANGE_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 3u;
const uint DDGI_TRACE_EARLY_OUT_RESOLVE_CLIPMAP_CELL_COUNTER = DDGI_TRACE_EARLY_OUT_COUNTER_BASE + 4u;
const uint DDGI_BLEND_ENERGY_COUNTER_BASE = 72u;
const uint DDGI_BLEND_ENERGY_SAMPLE_COUNT_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 0u;
const uint DDGI_BLEND_ENERGY_IRRADIANCE_LUMINANCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 1u;
const uint DDGI_BLEND_ENERGY_CONFIDENCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 2u;
const uint DDGI_BLEND_ENERGY_LOW_CONFIDENCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 3u;
const uint DDGI_BLEND_ENERGY_NONZERO_IRRADIANCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 4u;
const uint DDGI_BLEND_ENERGY_NONFINITE_IRRADIANCE_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 5u;
const uint DDGI_BLEND_ENERGY_FIREFLY_SUPPRESSED_COUNTER = DDGI_BLEND_ENERGY_COUNTER_BASE + 6u;
const uint DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE = 79u;
const uint DDGI_TRACE_RING_MISMATCH_SAMPLE_VALID_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 0u;
const uint DDGI_TRACE_RING_MISMATCH_SAMPLE_REQUEST_AGE_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 18u;
const uint DDGI_TRACE_RING_MISMATCH_CORRECTED_COUNTER = DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 19u;
const uint DDGI_SURFACE_CACHE_COUNTER_BASE = 100u;
const uint DDGI_SURFACE_CACHE_HIT_COUNTER = DDGI_SURFACE_CACHE_COUNTER_BASE + 0u;
const uint DDGI_SURFACE_CACHE_FALLBACK_COUNTER = DDGI_SURFACE_CACHE_COUNTER_BASE + 1u;
const uint DDGI_SDF_TRACE_COUNTER = DDGI_SURFACE_CACHE_COUNTER_BASE + 2u;
const uint DDGI_RAY_QUERY_TRACE_COUNTER = DDGI_SURFACE_CACHE_COUNTER_BASE + 3u;
const uint DDGI_SDF_TRACE_STEP_COUNTER = DDGI_SURFACE_CACHE_COUNTER_BASE + 4u;
const uint DDGI_SURFACE_CACHE_ANALYTIC_FALLBACK_FLAG = 1u << 0;
const float DDGI_TRACE_ENERGY_LUMINANCE_SCALE = 4096.0;
const float DDGI_TRACE_ENERGY_WEIGHT_SCALE = 1024.0;
const float DDGI_HALF_FLOAT_MAX = 65504.0;
const uint DDGI_RESOLVE_FAILURE_NONE = 0u;
const uint DDGI_RESOLVE_FAILURE_BOUNDS = 1u;
const uint DDGI_RESOLVE_FAILURE_PROBE_RANGE = 2u;
const uint DDGI_RESOLVE_FAILURE_CLIPMAP_CELL = 3u;

shared vec4 SharedRadianceAndRayCount[64];
shared vec4 SharedVisibilityAndHitCount[64];
shared vec4 SharedRelocationAndCloseCount[64];
shared vec4 SharedBackfaceAndMissCount[64];
shared vec4 SharedRayIrradiance[256];
shared vec4 SharedRayDirection[256];

#ifndef DDGI_SURFACE_CACHE_FALLBACK
#define DDGI_SURFACE_CACHE_FALLBACK 0
#endif
shared vec2 SharedRayVisibility[256];
shared vec4 SharedProbeAtlasControl;

bool DdgiRawAtlasRadianceConventionEnabled()
{
    // Phase 2 convention: probe rays store incoming radiance and probe atlases store irradiance.
    // The legacy scaled-atlas path is intentionally disabled for production consistency.
    return true;
}

bool DdgiSurfaceCacheAnalyticFallbackForced()
{
    return DDGI_SURFACE_CACHE_FALLBACK != 0 || (pc.SurfaceCacheFlags & DDGI_SURFACE_CACHE_ANALYTIC_FALLBACK_FLAG) != 0u;
}

bool DdgiTraceEnergyDiagnosticsEnabled()
{
    return (pc.Flags & DDGI_UPDATE_FLAG_TRACE_ENERGY_DIAGNOSTICS) != 0u;
}

bool DdgiProbeL1MetadataEnabled()
{
    return (pc.Flags & DDGI_UPDATE_FLAG_PROBE_L1_METADATA) != 0u;
}

bool DdgiTraceEnergyDiagnosticRay(uint probeIndex, uint rayIndex)
{
    return DdgiTraceEnergyDiagnosticsEnabled() && ((probeIndex + rayIndex + pc.FrameIndex) & 3u) == 0u;
}

bool DdgiBlendEnergyDiagnosticTexel(uint probeIndex, uint texel)
{
    return DdgiTraceEnergyDiagnosticsEnabled() && ((probeIndex + texel + pc.FrameIndex) & 7u) == 0u;
}

float DdgiTraceEnergyLuminance(vec3 value)
{
    return dot(max(value, vec3(0.0)), vec3(0.2126, 0.7152, 0.0722));
}

vec4 SanitizeDdgiProbeL1Metadata(vec4 value)
{
    if (any(isnan(value)) || any(isinf(value)))
        return vec4(0.0);

    vec3 directionalAnisotropy = clamp(value.xyz, vec3(-1.0), vec3(1.0));
    float anisotropy = min(length(directionalAnisotropy), 1.0);
    if (anisotropy > 0.000001)
        directionalAnisotropy = normalize(directionalAnisotropy) * anisotropy;

    return vec4(directionalAnisotropy, clamp(value.w, 0.0, 64.0));
}

vec4 ResolveDdgiProbeL1Metadata(uint rayCount, float historyValid, float blendAlpha, vec4 previousMetadata)
{
    uint sampleCount = min(rayCount, DDGI_MAX_RAYS_PER_PROBE);
    vec3 luminanceMoment = vec3(0.0);
    float luminanceWeight = 0.0;

    for (uint rayIndex = 0u; rayIndex < sampleCount; rayIndex++)
    {
        vec4 rayIrradiance = SharedRayIrradiance[rayIndex];
        vec4 rayDirection = SharedRayDirection[rayIndex];
        float rayWeight = DdgiTraceEnergyLuminance(rayIrradiance.rgb) * rayIrradiance.w * max(rayDirection.w, 0.0);
        luminanceMoment += rayDirection.xyz * rayWeight;
        luminanceWeight += rayWeight;
    }

    float anisotropy = luminanceWeight > 0.000001
        ? clamp(length(luminanceMoment) / luminanceWeight, 0.0, 1.0)
        : 0.0;
    vec3 dominantDirection = anisotropy > 0.000001
        ? normalize(luminanceMoment) * anisotropy
        : vec3(0.0);
    vec4 currentMetadata = SanitizeDdgiProbeL1Metadata(vec4(
        dominantDirection,
        luminanceWeight / max(float(sampleCount), 1.0)));
    vec4 safePrevious = SanitizeDdgiProbeL1Metadata(previousMetadata);
    return historyValid > 0.5
        ? SanitizeDdgiProbeL1Metadata(mix(safePrevious, currentMetadata, blendAlpha))
        : currentMetadata;
}

uint PackDdgiTraceEnergyLuminance(float value)
{
    return uint(round(clamp(value, 0.0, 16.0) * DDGI_TRACE_ENERGY_LUMINANCE_SCALE));
}

uint PackDdgiTraceEnergyWeight(float value)
{
    return uint(round(clamp(value, 0.0, 1.0) * DDGI_TRACE_ENERGY_WEIGHT_SCALE));
}

void RecordDdgiTraceEnergyDiagnostics(
    uint probeIndex,
    uint rayIndex,
    vec3 rayRadiance,
    vec3 directDiffuse,
    vec3 directNoShadowDiffuse,
    vec3 emissiveDiffuse,
    vec3 stableDiffuse,
    vec3 skyDiffuse,
    float hit,
    float miss)
{
    if (!DdgiTraceEnergyDiagnosticRay(probeIndex, rayIndex))
        return;

    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_SAMPLE_COUNT_COUNTER, 1u);
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_RAY_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(rayRadiance)));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_DIRECT_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(directDiffuse)));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_DIRECT_NO_SHADOW_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(directNoShadowDiffuse)));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_EMISSIVE_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(emissiveDiffuse)));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_STABLE_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(stableDiffuse)));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_SKY_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(DdgiTraceEnergyLuminance(skyDiffuse)));

    if (hit > 0.5)
    {
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_HIT_COUNT_COUNTER, 1u);
        if (DdgiTraceEnergyLuminance(directDiffuse) <= 0.00001)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_HIT_ZERO_DIRECT_COUNTER, 1u);
        else
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_HIT_WITH_DIRECT_COUNTER, 1u);
    }
    else if (miss > 0.5)
    {
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_ENERGY_MISS_COUNT_COUNTER, 1u);
    }
}

void RecordDdgiBlendEnergyDiagnostics(uint probeIndex, uint texel, vec4 irradianceSample)
{
    if (!DdgiBlendEnergyDiagnosticTexel(probeIndex, texel))
        return;

    float luminance = DdgiTraceEnergyLuminance(irradianceSample.rgb);
    float confidence = clamp(irradianceSample.a, 0.0, 1.0);
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_SAMPLE_COUNT_COUNTER, 1u);
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_IRRADIANCE_LUMINANCE_COUNTER, PackDdgiTraceEnergyLuminance(luminance));
    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_CONFIDENCE_COUNTER, PackDdgiTraceEnergyWeight(confidence));
    if (confidence <= 0.0001)
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_LOW_CONFIDENCE_COUNTER, 1u);
    if (luminance > 0.00001)
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_NONZERO_IRRADIANCE_COUNTER, 1u);
}

bool DdgiDebugForceProbeActive()
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    return (flags & DDGI_DEBUG_FORCE_PROBE_ACTIVE_FLAG) != 0u;
}

const uint DDGI_UPDATE_REQUEST_PRIORITY_MASK = 0x0000ffffu;
const uint DDGI_UPDATE_REQUEST_RAY_COUNT_SHIFT = 16u;

struct DdgiProbeUpdateRequest
{
    uint ProbeIndex;
    uint VolumeIndex;
    uint Flags;
    uint Priority;
    uint RayCount;
    ivec3 LogicalCell;
    uint RequestFrameSerial;
};

void RecordDdgiTraceRingMismatchSample(
    DdgiProbeUpdateRequest request,
    uint firstProbe,
    uint computedProbeIndex,
    ivec3 gridMin,
    ivec3 ringOffset,
    uvec3 probeCounts,
    uint requestAge)
{
#if defined(DDGI_TRACE_PASS)
    if (gl_LocalInvocationID.x != 0u)
        return;

    uint bufferIndex = uint(RENDERER_DIAGNOSTICS_BUFFER_BASE_INDEX) + pc.CurrentFrameIndex;
    if (atomicCompSwap(
        BindlessStorageBuffers[nonuniformEXT(bufferIndex)].Words[DDGI_TRACE_RING_MISMATCH_SAMPLE_VALID_COUNTER],
        0u,
        1u) != 0u)
    {
        return;
    }

    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 1u, gl_WorkGroupID.x);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 2u, request.ProbeIndex);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 3u, request.VolumeIndex);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 4u, uint(request.LogicalCell.x));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 5u, uint(request.LogicalCell.y));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 6u, uint(request.LogicalCell.z));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 7u, firstProbe);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 8u, computedProbeIndex);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 9u, uint(gridMin.x));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 10u, uint(gridMin.y));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 11u, uint(gridMin.z));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 12u, uint(ringOffset.x));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 13u, uint(ringOffset.y));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 14u, uint(ringOffset.z));
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 15u, probeCounts.x);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 16u, probeCounts.y);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_BASE + 17u, probeCounts.z);
    WriteStorageWord(bufferIndex, DDGI_TRACE_RING_MISMATCH_SAMPLE_REQUEST_AGE_COUNTER, requestAge);
#endif
}

struct StableDdgiVolumeSampleInfo
{
    uint firstProbe;
    uint kind;
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
};

void WritePackedHalf4(uint bufferIndex, uint wordOffset, vec4 value)
{
    vec4 safeValue = vec4(
        (isnan(value.x) || isinf(value.x)) ? 0.0 : clamp(value.x, 0.0, DDGI_HALF_FLOAT_MAX),
        (isnan(value.y) || isinf(value.y)) ? 0.0 : clamp(value.y, 0.0, DDGI_HALF_FLOAT_MAX),
        (isnan(value.z) || isinf(value.z)) ? 0.0 : clamp(value.z, 0.0, DDGI_HALF_FLOAT_MAX),
        (isnan(value.w) || isinf(value.w)) ? 0.0 : clamp(value.w, 0.0, DDGI_HALF_FLOAT_MAX));
    WriteStorageWord(bufferIndex, wordOffset + 0u, packHalf2x16(safeValue.xy));
    WriteStorageWord(bufferIndex, wordOffset + 1u, packHalf2x16(safeValue.zw));
}

vec4 ReadPackedHalf4(uint bufferIndex, uint wordOffset)
{
    vec2 xy = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 0u));
    vec2 zw = unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset + 1u));
    return vec4(xy, zw);
}

void WritePackedHalf2(uint bufferIndex, uint wordOffset, vec2 value)
{
    vec2 safeValue = vec2(
        (isnan(value.x) || isinf(value.x)) ? 0.0 : clamp(value.x, 0.0, DDGI_HALF_FLOAT_MAX),
        (isnan(value.y) || isinf(value.y)) ? 0.0 : clamp(value.y, 0.0, DDGI_HALF_FLOAT_MAX));
    WriteStorageWord(bufferIndex, wordOffset, packHalf2x16(safeValue));
}

vec2 ReadPackedHalf2(uint bufferIndex, uint wordOffset)
{
    return unpackHalf2x16(ReadStorageWord(bufferIndex, wordOffset));
}

float DdgiVisibilityGatherWeight(float cosTheta)
{
    float x = max(cosTheta, 0.0);
    float x2 = x * x;
    float x4 = x2 * x2;
    float x8 = x4 * x4;
    float x16 = x8 * x8;
    float x32 = x16 * x16;
    return x32 * x16 * x2;
}

float Hash11(float p)
{
    p = fract(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return fract(p);
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

vec2 Hash22(uvec3 value)
{
    uint seed = value.x * 1664525u + value.y * 1013904223u + value.z * 747796405u;
    return vec2(
        float(HashUint(seed)) * (1.0 / 4294967296.0),
        float(HashUint(seed ^ 0x9e3779b9u)) * (1.0 / 4294967296.0));
}

vec3 DdgiSphericalFibonacci(uint index, uint count)
{
    float sampleCount = max(float(count), 1.0);
    float sampleIndex = min(float(index), sampleCount - 1.0);
    float z = 1.0 - 2.0 * ((sampleIndex + 0.5) / sampleCount);
    float radius = sqrt(max(1.0 - z * z, 0.0));
    float phi = sampleIndex * 2.39996322972865332;
    return vec3(cos(phi) * radius, sin(phi) * radius, z);
}

vec3 DdgiUniformSphereSample(vec2 sampleValue)
{
    float z = sampleValue.x * 2.0 - 1.0;
    float phi = sampleValue.y * (2.0 * PI);
    float radius = sqrt(max(1.0 - z * z, 0.0));
    return vec3(cos(phi) * radius, sin(phi) * radius, z);
}

mat3 DdgiProbeRayRotation(uint probeIndex, uint frameSerial)
{
    vec2 axisSample = Hash22(uvec3(probeIndex, frameSerial, 0x6a09e667u));
    vec3 axis = DdgiUniformSphereSample(axisSample);
    vec3 up = abs(axis.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, axis));
    vec3 bitangent = cross(axis, tangent);
    float roll = Hash11(float(HashUint(probeIndex ^ (frameSerial * 0x9e3779b9u)))) * (2.0 * PI);
    float rollSin = sin(roll);
    float rollCos = cos(roll);
    vec3 rotatedTangent = tangent * rollCos + bitangent * rollSin;
    vec3 rotatedBitangent = bitangent * rollCos - tangent * rollSin;
    return mat3(rotatedTangent, rotatedBitangent, axis);
}

uint ResolvePrimaryProbeUpdateReason(uint flags)
{
    if ((flags & DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP) != 0u)
        return 4u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED) != 0u)
        return 8u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED) != 0u)
        return 7u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED) != 0u)
        return 9u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED) != 0u)
        return 11u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED) != 0u)
        return 12u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED) != 0u)
        return 13u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED) != 0u)
        return 10u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_STREAM_OUT) != 0u)
        return 15u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_STREAM_IN) != 0u)
        return 14u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_NEW_CELL) != 0u)
        return 1u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRTY_BOUNDS) != 0u)
        return 2u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_VISIBLE_FRUSTUM) != 0u)
        return 3u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_OUTSIDE_FRUSTUM_SAFETY) != 0u)
        return 6u;
    if ((flags & DDGI_PROBE_UPDATE_REASON_AGE_REFRESH) != 0u)
        return 5u;
    return 0u;
}

bool ShouldResetDdgiProbeHistory(uint flags)
{
    return (flags & (
        DDGI_PROBE_UPDATE_REASON_NEW_CELL |
        DDGI_PROBE_UPDATE_REASON_TELEPORT_WARMUP |
        DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED |
        DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED |
        DDGI_PROBE_UPDATE_REASON_STREAM_IN |
        DDGI_PROBE_UPDATE_REASON_STREAM_OUT)) != 0u;
}

float ReadDdgiHistoryMetric(uint stateBase, uint wordOffset)
{
    float value = ReadStorageFloat(pc.ProbeStateBufferIndex, stateBase + wordOffset);
    return (isnan(value) || isinf(value)) ? 0.0 : max(value, 0.0);
}

vec3 ReadDdgiIrradianceHistoryMetrics(uint stateBase, bool resetHistory)
{
    if (resetHistory)
        return vec3(0.0);

    return vec3(
        ReadDdgiHistoryMetric(stateBase, 17u),
        ReadDdgiHistoryMetric(stateBase, 18u),
        clamp(ReadDdgiHistoryMetric(stateBase, 19u), 0.0, 1.0));
}

float ResolveDdgiIrradianceReasonBlendFloor(uint flags)
{
    float response = 0.0;
    if ((flags & (DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED | DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED)) != 0u)
        response = max(response, 0.35);
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED) != 0u)
        response = max(response, 0.25);
    if ((flags & DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED) != 0u)
        response = max(response, 0.30);
    return response;
}

float ResolveDdgiIrradianceBlendAlpha(float baseBlendAlpha, uint flags, float inconsistency)
{
    float response = baseBlendAlpha;
    float catchUpResponse = mix(0.0, 0.35, smoothstep(0.20, 0.60, inconsistency));
    response = max(response, catchUpResponse);
    response = max(response, ResolveDdgiIrradianceReasonBlendFloor(flags));
    return clamp(response, 0.0, 1.0);
}

float PackDdgiFallbackProbeIndex(uint probeIndex)
{
    return float(min(probeIndex, 16777215u));
}

float ResolveDdgiVisibilityBlendAlpha(float baseBlendAlpha, uint flags)
{
    float response = baseBlendAlpha;
    if ((flags & (DDGI_PROBE_UPDATE_REASON_GEOMETRY_ADDED | DDGI_PROBE_UPDATE_REASON_GEOMETRY_REMOVED | DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED)) != 0u)
        response = max(response, 0.65);
    return clamp(response, 0.0, 1.0);
}

vec4 ResolveDdgiIrradianceHistory(
    float previousLongMean,
    float previousShortMean,
    float previousInconsistency,
    float currentLuminance,
    float historyValid)
{
    float longResponse = historyValid > 0.5 ? 0.04 : 1.0;
    float shortResponse = historyValid > 0.5 ? 0.35 : 1.0;
    float longMean = mix(previousLongMean, currentLuminance, longResponse);
    float shortMean = mix(previousShortMean, currentLuminance, shortResponse);
    float meanDelta = abs(shortMean - longMean) / max(max(shortMean, longMean), 0.05);
    float instantaneousDelta = abs(currentLuminance - previousShortMean) / max(max(currentLuminance, previousShortMean), 0.05);
    float inconsistency = historyValid > 0.5
        ? max(meanDelta, previousInconsistency * 0.5)
        : 0.0;
    return vec4(longMean, shortMean, clamp(inconsistency, 0.0, 1.0), historyValid > 0.5 ? instantaneousDelta : 0.0);
}

float ResolveDdgiDirtyReasonHysteresis(float baseHysteresis, uint flags)
{
    if (ShouldResetDdgiProbeHistory(flags))
        return 0.0;
    if ((flags & DDGI_PROBE_UPDATE_REASON_TRANSFORM_CHANGED) != 0u)
        return min(baseHysteresis, 0.25);
    if ((flags & DDGI_PROBE_UPDATE_REASON_MATERIAL_CHANGED) != 0u)
        return min(baseHysteresis, 0.35);
    if ((flags & (DDGI_PROBE_UPDATE_REASON_EMISSIVE_CHANGED | DDGI_PROBE_UPDATE_REASON_LOCAL_LIGHT_CHANGED)) != 0u)
        return min(baseHysteresis, 0.65);
    if ((flags & DDGI_PROBE_UPDATE_REASON_DIRECTIONAL_LIGHT_CHANGED) != 0u)
        return min(baseHysteresis, 0.85);
    return baseHysteresis;
}

vec2 SignNotZero(vec2 value)
{
    return vec2(
        value.x >= 0.0 ? 1.0 : -1.0,
        value.y >= 0.0 ? 1.0 : -1.0);
}

vec3 OctahedralDecode(vec2 encoded)
{
    vec2 f = encoded * 2.0 - 1.0;
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
    {
        vec2 folded = (1.0 - abs(n.yx)) * SignNotZero(n.xy);
        n.xy = folded;
    }

    return normalize(n);
}

vec2 OctahedralEncode(vec3 direction)
{
    vec3 n = direction / max(abs(direction.x) + abs(direction.y) + abs(direction.z), 0.0001);
    vec2 encoded = n.xy;
    if (n.z < 0.0)
        encoded = (1.0 - abs(encoded.yx)) * SignNotZero(encoded);
    return encoded * 0.5 + 0.5;
}

vec3 AtlasTexelDirection(uint texel, uint texelsPerProbe, uint frameOffset)
{
    uint texelCount = max(texelsPerProbe * texelsPerProbe, 1u);
    uint rotatedTexel = (texel + frameOffset) % texelCount;
    uint x = rotatedTexel % texelsPerProbe;
    uint y = rotatedTexel / texelsPerProbe;
    vec2 uv = (vec2(float(x), float(y)) + vec2(0.5)) / vec2(float(texelsPerProbe));
    return OctahedralDecode(uv);
}

float OctahedralTexelSolidAngle(vec2 uv, uint texelsPerProbe)
{
    vec2 f = uv * 2.0 - vec2(1.0);
    vec3 n = vec3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    if (n.z < 0.0)
        n.xy = (1.0 - abs(n.yx)) * SignNotZero(n.xy);

    float texelArea = 4.0 / max(float(texelsPerProbe * texelsPerProbe), 1.0);
    return texelArea / max(pow(dot(n, n), 1.5), 0.000001);
}

vec3 JitteredAtlasTexelDirection(
    uint texel,
    uint texelsPerProbe,
    uint probeIndex,
    out float solidAngle)
{
    uint safeTexels = max(texelsPerProbe, 1u);
    uvec2 texelCoord = uvec2(texel % safeTexels, texel / safeTexels);
    vec2 jitter = Hash22(uvec3(probeIndex, texel, safeTexels)) - vec2(0.5);
    vec2 uv = (vec2(texelCoord) + vec2(0.5) + jitter * 0.85) / float(safeTexels);
    uv = clamp(uv, vec2(0.000001), vec2(0.999999));
    solidAngle = OctahedralTexelSolidAngle(uv, safeTexels);
    return OctahedralDecode(uv);
}

uint DirectionToAtlasTexel(vec3 direction, uint texelsPerProbe)
{
    vec2 uv = clamp(OctahedralEncode(direction), vec2(0.0), vec2(0.999999));
    uvec2 coord = uvec2(uv * float(texelsPerProbe));
    return coord.y * texelsPerProbe + coord.x;
}

uvec2 RemapStableDdgiOctahedralTexelCoord(ivec2 coord, uint texelsPerProbe)
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

void StableDdgiBilinearOctahedralTexels(
    vec3 direction,
    uint texelsPerProbe,
    out uvec2 c00,
    out uvec2 c10,
    out uvec2 c01,
    out uvec2 c11,
    out vec2 fraction)
{
    vec2 uv = clamp(OctahedralEncode(direction), vec2(0.0), vec2(1.0));
    vec2 sampleCoord = uv * float(texelsPerProbe) - vec2(0.5);
    ivec2 baseCoord = ivec2(floor(sampleCoord));
    fraction = fract(sampleCoord);

    c00 = RemapStableDdgiOctahedralTexelCoord(baseCoord, texelsPerProbe);
    c10 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 0), texelsPerProbe);
    c01 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(0, 1), texelsPerProbe);
    c11 = RemapStableDdgiOctahedralTexelCoord(baseCoord + ivec2(1, 1), texelsPerProbe);
}

vec4 SampleDdgiMaterialTexture(int textureIndex, vec2 uv, float lod, vec4 fallback)
{
    bool valid = textureIndex >= FIRST_TEXTURE_INDEX && textureIndex < FIRST_TEXTURE_INDEX + MAX_TEXTURES;
    if (!valid)
        return fallback;

    return textureLod(BindlessTextures[nonuniformEXT(textureIndex)], uv, lod);
}

bool ShouldSampleDdgiMaterialTextures(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return true;

    return pc.MaterialTextureMaxCascade < DDGI_MATERIAL_TEXTURE_DISABLED_CASCADE &&
        volumeCascadeIndex <= pc.MaterialTextureMaxCascade;
}

bool ShouldUseCompactDdgiMaterial(uint volumeCascadeIndex)
{
    return !ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
}

float DdgiMaterialTextureLod(uint volumeCascadeIndex)
{
    if (volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE)
        return 0.0;

    return min(float(volumeCascadeIndex) * 1.5, 4.0);
}

float ResolveDdgiMaterialTextureLod(GPUMaterialData material, uint volumeCascadeIndex)
{
    return max(DdgiMaterialTextureLod(volumeCascadeIndex), max(material.DdgiMaterialPolicy.y, 0.0));
}

vec3 ResolveCompactDdgiAlbedo(GPUMaterialData material)
{
    return dot(material.DdgiAverageAlbedo.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageAlbedo.rgb
        : material.Albedo.rgb;
}

vec3 ResolveCompactDdgiEmissive(GPUMaterialData material)
{
    return dot(material.DdgiAverageEmissive.rgb, vec3(1.0)) > 0.000001
        ? material.DdgiAverageEmissive.rgb
        : material.Emissive.rgb;
}

vec2 ApplyDdgiTextureTransform(vec2 uv, vec4 offsetScale, float rotationRadians)
{
    vec2 scaled = uv * offsetScale.zw;
    float s = sin(rotationRadians);
    float c = cos(rotationRadians);
    return offsetScale.xy + vec2(
        scaled.x * c - scaled.y * s,
        scaled.x * s + scaled.y * c);
}

bool IsIdentityDdgiTextureTransform(vec4 offsetScale, float rotationRadians)
{
    return abs(offsetScale.x) <= 0.0001 &&
           abs(offsetScale.y) <= 0.0001 &&
           abs(offsetScale.z - 1.0) <= 0.0001 &&
           abs(offsetScale.w - 1.0) <= 0.0001 &&
           abs(rotationRadians) <= 0.0001;
}

vec2 SelectDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet)
{
    return int(round(texCoordSet)) == 1 ? uv1 : uv0;
}

vec2 MaterialDdgiHitUv(vec2 uv0, vec2 uv1, float texCoordSet, vec4 offsetScale, float rotationRadians)
{
    vec2 uv = SelectDdgiHitUv(uv0, uv1, texCoordSet);
    return IsIdentityDdgiTextureTransform(offsetScale, rotationRadians)
        ? uv
        : ApplyDdgiTextureTransform(uv, offsetScale, rotationRadians);
}

GPUDdgiRayQueryInstance ReadDdgiRayQueryInstance(uint instanceIndex)
{
    uint baseWord = instanceIndex * uint(SIZEOF_GPU_DDGI_RAY_QUERY_INSTANCE / 4);
    GPUDdgiRayQueryInstance instance;
    instance.VertexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 0u);
    instance.IndexOffset = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 1u);
    instance.MaterialIndex = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 2u);
    instance.Padding0 = ReadStorageWord(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 3u);
    instance.WorldMatrixInverseTranspose = ReadStorageMat4(uint(DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX), baseWord + 4u);
    return instance;
}

bool ResolveCommittedHitSurface(
    uint instanceIndex,
    uint primitiveIndex,
    vec2 barycentrics,
    vec3 rayDirection,
    uint volumeCascadeIndex,
    bool sampleMaterialTextures,
    float materialTextureLod,
    out vec3 normal,
    out vec3 albedo,
    out vec3 emissive)
{
    GPUDdgiRayQueryInstance instance = ReadDdgiRayQueryInstance(instanceIndex);
    uint triangleIndexBase = instance.IndexOffset + primitiveIndex * 3u;
    uint i0 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 0u);
    uint i1 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 1u);
    uint i2 = ReadStorageWord(uint(INDEX_BUFFER_INDEX), triangleIndexBase + 2u);
    uint v0 = instance.VertexOffset + i0;
    uint v1 = instance.VertexOffset + i1;
    uint v2 = instance.VertexOffset + i2;

    vec3 bary = vec3(
        1.0 - barycentrics.x - barycentrics.y,
        barycentrics.x,
        barycentrics.y);

    vec3 p0 = ReadSplitVertexPosition(v0);
    vec3 p1 = ReadSplitVertexPosition(v1);
    vec3 p2 = ReadSplitVertexPosition(v2);
    vec3 fallbackLocalNormal = cross(p1 - p0, p2 - p0);
    fallbackLocalNormal = dot(fallbackLocalNormal, fallbackLocalNormal) > 0.000001
        ? normalize(fallbackLocalNormal)
        : vec3(0.0, 1.0, 0.0);
    vec3 localNormal =
        ReadSplitVertexNormal(v0) * bary.x +
        ReadSplitVertexNormal(v1) * bary.y +
        ReadSplitVertexNormal(v2) * bary.z;
    if (dot(localNormal, localNormal) <= 0.000001)
        localNormal = fallbackLocalNormal;

    normal = normalize(MulRowMajor(vec4(normalize(localNormal), 0.0), instance.WorldMatrixInverseTranspose).xyz);
    if (dot(normal, normal) <= 0.000001)
        normal = normalize(-rayDirection);
    if (dot(normal, rayDirection) > 0.0)
        normal = -normal;

    vec2 uv0 =
        ReadSplitVertexTexCoord(v0) * bary.x +
        ReadSplitVertexTexCoord(v1) * bary.y +
        ReadSplitVertexTexCoord(v2) * bary.z;
    vec2 uv1 =
        ReadSplitVertexTexCoord2(v0) * bary.x +
        ReadSplitVertexTexCoord2(v1) * bary.y +
        ReadSplitVertexTexCoord2(v2) * bary.z;
    vec3 vertexColor =
        ReadSplitVertexColor(v0).rgb * bary.x +
        ReadSplitVertexColor(v1).rgb * bary.y +
        ReadSplitVertexColor(v2).rgb * bary.z;

    GPUMaterialData material = ReadMaterial(instance.MaterialIndex);
    if (ShouldUseCompactDdgiMaterial(volumeCascadeIndex))
    {
        albedo = max(ResolveCompactDdgiAlbedo(material) * vertexColor, vec3(0.0));
        emissive = max(ResolveCompactDdgiEmissive(material), vec3(0.0));
        return true;
    }

    materialTextureLod = ResolveDdgiMaterialTextureLod(material, volumeCascadeIndex);
    vec4 albedoSample = vec4(1.0);
    if (sampleMaterialTextures && material.AlbedoTextureIndex != DEFAULT_WHITE_TEXTURE)
    {
        vec2 albedoUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.x,
            material.BaseColorOffsetScale,
            material.TextureRotations.x);
        albedoSample = SampleDdgiMaterialTexture(material.AlbedoTextureIndex, albedoUv, materialTextureLod, vec4(1.0));
    }
    albedo = max(ResolveCompactDdgiAlbedo(material) * albedoSample.rgb * vertexColor, vec3(0.0));

    vec4 emissiveSample = vec4(1.0);
    if (sampleMaterialTextures && material.EmissiveTextureIndex != DEFAULT_BLACK_TEXTURE)
    {
        vec2 emissiveUv = MaterialDdgiHitUv(
            uv0,
            uv1,
            material.TextureTexCoordSets.w,
            material.EmissiveOffsetScale,
            material.TextureRotations.w);
        emissiveSample = SampleDdgiMaterialTexture(material.EmissiveTextureIndex, emissiveUv, materialTextureLod, vec4(0.0));
    }
    emissive = max(ResolveCompactDdgiEmissive(material) * emissiveSample.rgb, vec3(0.0));
    return true;
}

float TraceLightVisibility(vec3 worldPosition, vec3 normal, vec3 lightDirection, float maxDistance)
{
    float normalOffset = DDGI_PROBE_TRACE_EPSILON * 4.0;
    float rayTMin = DDGI_PROBE_TRACE_EPSILON * 2.0;
    float rayDistance = max(maxDistance - normalOffset, rayTMin);
    vec3 origin = worldPosition + normal * normalOffset;

    rayQueryEXT shadowQuery;
    rayQueryInitializeEXT(
        shadowQuery,
        SceneTlas,
        gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT,
        0xff,
        origin,
        rayTMin,
        lightDirection,
        rayDistance);

    while (rayQueryProceedEXT(shadowQuery))
    {
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(shadowQuery, true);
    return hitType == gl_RayQueryCommittedIntersectionNoneEXT ? 1.0 : 0.0;
}

vec3 RotateDdgiEnvironmentDirection(vec3 direction, float radians)
{
    float s = sin(radians);
    float c = cos(radians);
    return normalize(vec3(
        direction.x * c - direction.z * s,
        direction.y,
        direction.x * s + direction.z * c));
}

vec3 SampleDdgiEnvironmentMissRadiance(vec3 direction)
{
    float skyWeight = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
    vec3 fallbackRadiance = pc.EnvironmentRadianceAndIntensity.rgb * skyWeight;
    GPUEnvironmentData environment = ReadEnvironmentData();
    if (environment.Enabled == 0u || environment.EnvironmentTextureIndex < 0)
        return fallbackRadiance;

    vec3 environmentDirection = RotateDdgiEnvironmentDirection(direction, environment.RotationRadians);
    vec3 environmentRadiance = textureLod(
        BindlessCubeTextures[nonuniformEXT(environment.EnvironmentTextureIndex)],
        environmentDirection,
        0.0).rgb;
    return max(environmentRadiance, vec3(0.0)) * max(environment.DiffuseIntensity, 0.0);
}

bool TryReadSelectedDdgiDirectionalLight(out GPULight selectedLight)
{
    if (pc.DirectionalLightCount == 0u ||
        pc.PrimaryDirectionalLightIndex == DDGI_INVALID_LIGHT_INDEX ||
        pc.PrimaryDirectionalLightIndex >= pc.LightCount)
        return false;

    selectedLight = ReadLight(pc.PrimaryDirectionalLightIndex);
    return selectedLight.Type == 1;
}

bool TryBuildDdgiLocalLightContribution(
    vec3 worldPosition,
    vec3 normal,
    GPULight candidateLight,
    out GPULight light,
    out vec3 lightDirection,
    out float distanceToLight,
    out float attenuation,
    out float contributionScore)
{
    light = candidateLight;
    contributionScore = 0.0;
    if (light.Type == 1)
        return false;

    vec3 toLight = light.Position - worldPosition;
    distanceToLight = length(toLight);
    if (distanceToLight >= light.Range || light.Range <= 0.0)
        return false;

    lightDirection = toLight / max(distanceToLight, 0.0001);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return false;

    float rangeFactor = clamp(1.0 - distanceToLight / light.Range, 0.0, 1.0);
    attenuation = rangeFactor * rangeFactor;
    if (light.Type == 2)
    {
        float coneCos = cos(light.SpotAngle);
        float spotCos = dot(normalize(light.Direction), -lightDirection);
        float spotFactor = smoothstep(coneCos, min(coneCos + 0.1, 1.0), spotCos);
        attenuation *= spotFactor;
    }

    vec3 incomingRadiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0);
    contributionScore = DdgiTraceEnergyLuminance(incomingRadiance) * attenuation * nDotL;
    return contributionScore > 0.000001;
}

uint SelectStochasticDdgiLocalLightOrdinal(uint probeIndex, uint rayIndex)
{
    uint localLightCount = max(pc.LocalLightCount, 1u);
    uint seed = probeIndex * 1664525u ^
        rayIndex * 1013904223u ^
        pc.FrameSerial * 747796405u ^
        pc.CurrentFrameIndex * 2891336453u;
    return HashUint(seed) % localLightCount;
}

bool TryBuildStochasticDdgiLocalLightContribution(
    vec3 worldPosition,
    vec3 normal,
    uint probeIndex,
    uint rayIndex,
    out GPULight light,
    out vec3 lightDirection,
    out float distanceToLight,
    out float attenuation,
    out float energyScale)
{
    lightDirection = vec3(0.0);
    distanceToLight = 0.0;
    attenuation = 0.0;
    energyScale = 1.0;

    if (pc.LocalLightCount == 0u || pc.LightCount == 0u)
        return false;

    uint selectedLocalOrdinal = SelectStochasticDdgiLocalLightOrdinal(probeIndex, rayIndex);
    uint localOrdinal = 0u;
    for (uint lightIndex = 0u; lightIndex < pc.LightCount; lightIndex++)
    {
        GPULight candidateLight = ReadLight(lightIndex);
        if (candidateLight.Type == 1)
            continue;

        if (localOrdinal++ != selectedLocalOrdinal)
            continue;

        GPULight candidateSelectedLight;
        float candidateScore;
        bool found = TryBuildDdgiLocalLightContribution(
            worldPosition,
            normal,
            candidateLight,
            candidateSelectedLight,
            lightDirection,
            distanceToLight,
            attenuation,
            candidateScore);
        light = candidateSelectedLight;
        energyScale = float(pc.LocalLightCount);
        return found;
    }

    return false;
}

vec3 EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    vec3 normal,
    vec3 albedo,
    GPULight light,
    vec3 lightDirection,
    float visibilityDistance,
    float attenuation,
    out vec3 noShadowDiffuse)
{
    noShadowDiffuse = vec3(0.0);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    vec3 incomingRadiance = max(light.Color, vec3(0.0)) * max(light.Intensity, 0.0) * attenuation;
    noShadowDiffuse = incomingRadiance * nDotL * (albedo / PI);
    if (DdgiTraceEnergyLuminance(noShadowDiffuse) <= 0.0001)
        return vec3(0.0);

    float visibility = TraceLightVisibility(worldPosition, normal, lightDirection, visibilityDistance);
    return noShadowDiffuse * visibility;
}

vec3 EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    if (pc.EmissiveSourceCount == 0u)
        return vec3(0.0);

    GPUDdgiEmissiveSource source = ReadDdgiEmissiveSource(0u);
    vec3 toSource = source.CenterRadius.xyz - worldPosition;
    float distanceToSource = length(toSource);
    float radius = max(source.CenterRadius.w, 0.001);
    if (distanceToSource >= radius)
        return vec3(0.0);

    vec3 lightDirection = toSource / max(distanceToSource, 0.0001);
    float nDotL = max(dot(normal, lightDirection), 0.0);
    if (nDotL <= 0.0)
        return vec3(0.0);

    float radiusAttenuation = 1.0 - distanceToSource / radius;
    radiusAttenuation *= radiusAttenuation;
    vec3 sourceRadiance = max(source.RadianceImportance.rgb, vec3(0.0));
    return sourceRadiance * nDotL * radiusAttenuation * (albedo / PI);
}

vec3 EvaluateDirectDiffuseRadianceAtHit(
    vec3 worldPosition,
    vec3 normal,
    vec3 albedo,
    uint probeIndex,
    uint rayIndex,
    out vec3 directNoShadowDiffuse)
{
    vec3 directDiffuseRadiance = vec3(0.0);
    directNoShadowDiffuse = vec3(0.0);
    uint selectedLightCapacity = min(pc.MaxShadedLights, DDGI_MAX_SELECTED_HIT_LIGHTS);
    uint selectedLightCount = 0u;
    if (selectedLightCapacity == 0u || pc.LightCount == 0u)
        return directDiffuseRadiance;

    GPULight directionalLight;
    if (TryReadSelectedDdgiDirectionalLight(directionalLight))
    {
        vec3 lightDirection = normalize(-directionalLight.Direction);
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            normal,
            albedo,
            directionalLight,
            lightDirection,
            DDGI_DIRECTIONAL_SHADOW_RAY_DISTANCE,
            1.0,
            lightNoShadowDiffuse);
        directNoShadowDiffuse += lightNoShadowDiffuse;
        selectedLightCount++;
    }

    if (selectedLightCount >= selectedLightCapacity)
        return directDiffuseRadiance;

    GPULight localLight;
    vec3 localLightDirection;
    float localLightDistance;
    float localLightAttenuation;
    float localLightEnergyScale;
    if (TryBuildStochasticDdgiLocalLightContribution(
        worldPosition,
        normal,
        probeIndex,
        rayIndex,
        localLight,
        localLightDirection,
        localLightDistance,
        localLightAttenuation,
        localLightEnergyScale))
    {
        vec3 lightNoShadowDiffuse;
        directDiffuseRadiance += EvaluateSelectedDdgiDirectDiffuseRadianceAtHit(
            worldPosition,
            normal,
            albedo,
            localLight,
            localLightDirection,
            localLightDistance,
            localLightAttenuation,
            lightNoShadowDiffuse) * localLightEnergyScale;
        directNoShadowDiffuse += lightNoShadowDiffuse * localLightEnergyScale;
    }

    return directDiffuseRadiance;
}

float ResolveStableDdgiRoundedBoxEdgeFade(vec3 edgeDistance, vec3 blendDistance)
{
    vec3 safeBlendDistance = max(blendDistance, vec3(0.0001));
    vec3 axisFade = clamp(edgeDistance / safeBlendDistance, vec3(0.0), vec3(1.0));
    float perAxisFade = min(axisFade.x, min(axisFade.y, axisFade.z));
    float cornerPressure = clamp(length(vec3(1.0) - axisFade) * 0.70710678, 0.0, 1.0);
    float roundedBoxFade = perAxisFade * mix(1.0, 1.0 - cornerPressure * 0.25, perAxisFade);
    return clamp(roundedBoxFade, 0.0, 1.0);
}

bool ReadStableDdgiVolumeSampleInfo(
    uint volumeIndex,
    vec3 worldPosition,
    out StableDdgiVolumeSampleInfo info)
{
    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 biasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);
    vec4 blendAndFlags = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_BLEND_AND_FLAGS) / 4u);

    info.firstProbe = uint(originAndFirst.w);
    info.kind = uint(round(gridMinAndKind.w));
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

    float volumeEdgeFade;
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
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
        float shortestAxisCells = min(min(float(info.probeCounts.x), float(info.probeCounts.y)), float(info.probeCounts.z));
        float minEdgeBlendCells = min(2.0, max(shortestAxisCells * 0.125, 1.0));
        float edgeBlendCells = max(blendAndFlags.x * shortestAxisCells, minEdgeBlendCells);
        float edgeBlendDistance = max(blendAndFlags.y / max(min(min(info.spacing.x, info.spacing.y), info.spacing.z), 0.0001), edgeBlendCells);
        volumeEdgeFade = ResolveStableDdgiRoundedBoxEdgeFade(logicalEdgeDistance, vec3(edgeBlendDistance));
    }
    else
    {
        vec3 latticeMax = info.origin + info.spacing * vec3(info.probeCounts - uvec3(1u));
        vec3 influenceMin = info.origin - info.spacing * 0.5;
        vec3 influenceMax = latticeMax + info.spacing * 0.5;
        if (any(lessThan(worldPosition, influenceMin)) || any(greaterThan(worldPosition, influenceMax)))
            return false;

        vec3 influenceEdgeDistance = min(worldPosition - influenceMin, influenceMax - worldPosition);
        volumeEdgeFade = ResolveStableDdgiRoundedBoxEdgeFade(influenceEdgeDistance, info.spacing * 0.5);
        vec3 gridPosition = clamp((worldPosition - info.origin) / info.spacing, vec3(0.0), vec3(info.probeCounts - uvec3(1u)));
        vec3 localBase = floor(clamp(gridPosition, vec3(0.0), vec3(info.probeCounts - uvec3(2u))));
        info.cellBase = ivec3(localBase);
        info.cellFraction = clamp(gridPosition - localBase, vec3(0.0), vec3(1.0));
    }

    info.edgeFade = clamp(volumeEdgeFade, 0.0, 1.0);
    return true;
}

vec3 StableDdgiSurfaceProbeSamplePosition(StableDdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal)
{
    float minProbeSpacing = max(min(min(info.spacing.x, info.spacing.y), info.spacing.z), 0.001);
    float surfaceBias = clamp(max(info.normalBias, minProbeSpacing * 0.16), 0.0, minProbeSpacing * 0.45);
    return worldPosition + normal * surfaceBias;
}

uint StableDdgiProbeIndex(StableDdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
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

vec3 StableDdgiProbeWorldPosition(StableDdgiVolumeSampleInfo info, ivec3 probeCoord)
{
    if (info.kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
        return vec3(probeCoord) * info.spacing;

    return info.origin + info.spacing * vec3(probeCoord);
}

vec4 ReadStableDdgiProbeIrradiance(uint probeIndex, vec3 normal)
{
    uint texelsPerProbe = max(pc.IrradianceTexelsPerProbe, 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uint wordsPerProbe = texelCount * 2u;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    StableDdgiBilinearOctahedralTexels(normal, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * wordsPerProbe;
    vec4 s00 = DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c00.y * texelsPerProbe + c00.x) * 2u));
    vec4 s10 = DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c10.y * texelsPerProbe + c10.x) * 2u));
    vec4 s01 = DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c01.y * texelsPerProbe + c01.x) * 2u));
    vec4 s11 = DecodeDdgiIrradianceAtlasSqrtSample(ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, baseWord + (c11.y * texelsPerProbe + c11.x) * 2u));
    return ResolveDdgiIrradianceAtlasSqrtBlend(mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y));
}

vec2 ReadStableDdgiProbeVisibility(uint probeIndex, vec3 probeToPoint)
{
    uint texelsPerProbe = max(pc.VisibilityTexelsPerProbe, 1u);
    uint texelCount = texelsPerProbe * texelsPerProbe;
    uvec2 c00;
    uvec2 c10;
    uvec2 c01;
    uvec2 c11;
    vec2 fraction;
    StableDdgiBilinearOctahedralTexels(probeToPoint, texelsPerProbe, c00, c10, c01, c11, fraction);
    uint baseWord = probeIndex * texelCount;
    vec2 s00 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c00.y * texelsPerProbe + c00.x);
    vec2 s10 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c10.y * texelsPerProbe + c10.x);
    vec2 s01 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c01.y * texelsPerProbe + c01.x);
    vec2 s11 = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, baseWord + c11.y * texelsPerProbe + c11.x);
    return mix(mix(s00, s10, fraction.x), mix(s01, s11, fraction.x), fraction.y);
}

float EvaluateStableDdgiVisibility(vec2 moments, float probeDistance, float viewBias)
{
    float mean = max(moments.x, 0.0001);
    float mean2 = max(moments.y, mean * mean);
    if (probeDistance <= mean + max(viewBias, 0.02))
        return 1.0;

    float variance = max(mean2 - mean * mean, 0.005);
    float delta = probeDistance - mean;
    return clamp(variance / (variance + delta * delta), 0.0, 1.0);
}

vec3 SampleStableDdgiVolumeIrradiance(StableDdgiVolumeSampleInfo info, vec3 worldPosition, vec3 normal)
{
    vec3 biasedPosition = worldPosition + normal * info.normalBias;
    vec3 accumulated = vec3(0.0);
    float totalWeight = 0.0;

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

                uint probeIndex = StableDdgiProbeIndex(info, corner);
                uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
                vec4 stateIrradiance = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
                vec4 relocationAndClassification = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
                vec4 qualityAndReason = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);
                float probeActive = clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);
                if (DdgiDebugForceProbeActive())
                    probeActive = 1.0;
                if (probeActive <= 0.001)
                    continue;

                vec3 probePosition = StableDdgiProbeWorldPosition(info, corner) + relocationAndClassification.xyz;
                vec3 toProbe = probePosition - worldPosition;
                float distanceToProbe = max(length(toProbe), 0.0001);
                vec3 pointToProbeDirection = toProbe / distanceToProbe;
                float alignment = dot(normal, pointToProbeDirection);
                float normalHemisphereWeight = clamp(alignment * 0.5 + 0.5, 0.0, 1.0);
                float grazingRejection = smoothstep(-0.15, 0.25, alignment);
                float normalWeight = normalHemisphereWeight * normalHemisphereWeight * grazingRejection;
                float distanceWeight = 1.0 / (1.0 + distanceToProbe * 0.025);

                vec4 irradianceSample = ReadStableDdgiProbeIrradiance(probeIndex, normal);
                float irradianceConfidence = clamp(irradianceSample.w, 0.0, 1.0);
                float rayHitConfidence = clamp(qualityAndReason.x, 0.0, 1.0);
                float stateIrradianceConfidence = clamp(qualityAndReason.y, 0.0, 1.0);
                float visibilityConfidence = clamp(qualityAndReason.z, 0.0, 1.0);
                float transportConfidence = clamp(rayHitConfidence + visibilityConfidence, 0.0, 1.0);
                float qualityConfidence = clamp(max(transportConfidence, 0.35) * max(stateIrradianceConfidence, irradianceConfidence), 0.0, 1.0);
                if (irradianceConfidence <= 0.000001 || qualityConfidence <= 0.000001)
                    continue;

                vec3 probeToBiasedPoint = biasedPosition - probePosition;
                float biasedDistanceToProbe = max(length(probeToBiasedPoint), 0.0001);
                vec3 probeToPointDirection = probeToBiasedPoint / biasedDistanceToProbe;
                float visibilityTrust = smoothstep(0.05, 0.20, visibilityConfidence);
                float visibility = 1.0;
                if (visibilityTrust > 0.000001)
                {
                    visibility = EvaluateStableDdgiVisibility(
                        ReadStableDdgiProbeVisibility(probeIndex, probeToPointDirection),
                        biasedDistanceToProbe,
                        info.viewBias);
                }
                float visibilityAttenuation = mix(
                    1.0,
                    clamp(visibility, 0.0, 1.0),
                    clamp(visibilityTrust, 0.0, 1.0));
                float radianceWeight = cellWeight * normalWeight * distanceWeight * probeActive * irradianceConfidence * qualityConfidence;
                accumulated += clamp(irradianceSample.rgb, vec3(0.0), vec3(64.0)) * radianceWeight * visibilityAttenuation;
                totalWeight += radianceWeight;
            }
        }
    }

    return totalWeight > 0.000001
        ? clamp((accumulated / totalWeight) * info.edgeFade, vec3(0.0), vec3(64.0))
        : vec3(0.0);
}

vec3 SampleStableDdgiIrradiance(vec3 worldPosition, vec3 normal)
{
    uint flags = ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 8u);
    uint volumeCount = min(ReadStorageWord(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), 0u), pc.VolumeCount);
    if ((flags & DDGI_UPDATE_FLAG_ENABLED) == 0u || volumeCount == 0u)
        return vec3(0.0);

    vec3 blendedIrradiance = vec3(0.0);
    float blendedCoverage = 0.0;
    float remainingCoverage = 1.0;

    for (uint volumeIndex = 0u; volumeIndex < volumeCount && remainingCoverage > 0.0001; volumeIndex++)
    {
        StableDdgiVolumeSampleInfo info;
        if (!ReadStableDdgiVolumeSampleInfo(volumeIndex, worldPosition, info))
            continue;

        vec3 probeSamplePosition = StableDdgiSurfaceProbeSamplePosition(info, worldPosition, normal);
        StableDdgiVolumeSampleInfo biasedInfo;
        if (ReadStableDdgiVolumeSampleInfo(volumeIndex, probeSamplePosition, biasedInfo))
            info = biasedInfo;

        vec3 irradiance = SampleStableDdgiVolumeIrradiance(info, worldPosition, normal);
        float coverage = clamp(info.edgeFade, 0.0, 1.0);
        if (coverage <= 0.000001)
            continue;

        float contribution = coverage * remainingCoverage;
        blendedIrradiance += irradiance * contribution;
        blendedCoverage += contribution;
        remainingCoverage *= 1.0 - coverage;
    }

    if (blendedCoverage <= 0.000001)
        return vec3(0.0);

    vec3 sampledIrradiance = blendedIrradiance / blendedCoverage;
    return clamp(sampledIrradiance, vec3(0.0), vec3(64.0));
}

vec3 EvaluateStableDdgiDiffuseRadianceAtHit(vec3 worldPosition, vec3 normal, vec3 albedo)
{
    vec3 stableIrradiance = SampleStableDdgiIrradiance(worldPosition + normal * DDGI_PROBE_TRACE_EPSILON, normal);
    return stableIrradiance * (albedo / PI);
}

vec3 EvaluateDdgiRayQuerySurfaceRadianceAtHit(
    vec3 worldPosition,
    vec3 normal,
    vec3 albedo,
    vec3 surfaceEmissive,
    uint probeIndex,
    uint rayIndex,
    out vec3 directDiffuse,
    out vec3 directNoShadowDiffuse,
    out vec3 emissiveDiffuse,
    out vec3 stableDiffuse)
{
    directDiffuse = EvaluateDirectDiffuseRadianceAtHit(worldPosition, normal, albedo, probeIndex, rayIndex, directNoShadowDiffuse);
    vec3 emissiveProxyDiffuse = EvaluateSelectedDdgiEmissiveDiffuseRadianceAtHit(worldPosition, normal, albedo);
    stableDiffuse = EvaluateStableDdgiDiffuseRadianceAtHit(worldPosition, normal, albedo);
    emissiveDiffuse = surfaceEmissive + emissiveProxyDiffuse;
    return directDiffuse + emissiveDiffuse + stableDiffuse;
}

DdgiProbeUpdateRequest ReadProbeUpdateRequest(uint updateIndex)
{
    uint baseWord = updateIndex * (uint(SIZEOF_GPU_DDGI_PROBE_UPDATE_REQUEST) / 4u);
    DdgiProbeUpdateRequest request;
    request.ProbeIndex = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PROBE_INDEX) / 4u);
    request.VolumeIndex = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_VOLUME_INDEX) / 4u);
    request.Flags = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FLAGS) / 4u);
    uint packedPriority = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_PRIORITY) / 4u);
    request.Priority = packedPriority & DDGI_UPDATE_REQUEST_PRIORITY_MASK;
    request.RayCount = packedPriority >> DDGI_UPDATE_REQUEST_RAY_COUNT_SHIFT;
    request.LogicalCell = ivec3(
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_X) / 4u)),
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Y) / 4u)),
        int(ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_LOGICAL_CELL_Z) / 4u)));
    request.RequestFrameSerial = ReadStorageWord(pc.ProbeUpdateQueueBufferIndex, baseWord + uint(OFFSET_GPU_DDGI_PROBE_UPDATE_REQUEST_FRAME_SERIAL) / 4u);
    return request;
}

uint ResolveDdgiRequestRayCount(DdgiProbeUpdateRequest request, vec4 updateParams)
{
    uint volumeRaysPerProbe = clamp(uint(round(updateParams.x)), 1u, DDGI_MAX_RAYS_PER_PROBE);
    uint requestRaysPerProbe = request.RayCount > 0u
        ? clamp(request.RayCount, 1u, DDGI_MAX_RAYS_PER_PROBE)
        : volumeRaysPerProbe;
    return min(requestRaysPerProbe, max(pc.RayCapacityPerProbe, 1u));
}

uint ResolveDdgiUpdateRequestCount()
{
    uint requestedCount = pc.ProbesToUpdate;
    if ((pc.Flags & DDGI_UPDATE_FLAG_GPU_SCHEDULER) == 0u)
        return requestedCount;

    uint gpuRequestCount = ReadStorageWord(
        uint(DDGI_SCHEDULER_COUNTER_BUFFER_INDEX),
        uint(OFFSET_GPU_DDGI_SCHEDULER_COUNTER_REQUEST_COUNT) / 4u);
    return min(gpuRequestCount, requestedCount);
}

bool ResolveProbeUpdateRequest(
    inout DdgiProbeUpdateRequest request,
    out uint localProbeIndex,
    out vec3 probePosition,
    out vec3 probeSpacing,
    out vec4 biasAndDistance,
    out vec4 updateParams,
    out uint volumeIndex,
    out uint volumeCascadeIndex,
    out uint resolveFailure)
{
    resolveFailure = DDGI_RESOLVE_FAILURE_NONE;
    if (request.VolumeIndex >= pc.VolumeCount || request.ProbeIndex >= pc.ProbeCount)
    {
        resolveFailure = DDGI_RESOLVE_FAILURE_BOUNDS;
        localProbeIndex = 0u;
        probePosition = vec3(0.0);
        probeSpacing = vec3(1.0);
        biasAndDistance = vec4(0.0);
        updateParams = vec4(0.0);
        volumeIndex = 0u;
        volumeCascadeIndex = DDGI_AUTHORED_VOLUME_CASCADE;
        return false;
    }

    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    volumeIndex = request.VolumeIndex;
    volumeCascadeIndex = DDGI_AUTHORED_VOLUME_CASCADE;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 volumeBiasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 volumeUpdateParams = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_RAY_AND_UPDATE_PARAMS) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);

    uint firstProbe = uint(originAndFirst.w);
    uvec3 probeCounts = uvec3(
        max(uint(sizeAndCountX.w), 1u),
        max(uint(spacingAndCountY.w), 1u),
        max(uint(volumeBiasAndCountZ.w), 1u));
    uint countX = probeCounts.x;
    uint countY = probeCounts.y;
    uint countZ = probeCounts.z;
    uint volumeProbeCount = probeCounts.x * probeCounts.y * probeCounts.z;
    if (request.ProbeIndex < firstProbe || request.ProbeIndex >= firstProbe + volumeProbeCount)
    {
        resolveFailure = DDGI_RESOLVE_FAILURE_PROBE_RANGE;
        localProbeIndex = 0u;
        probePosition = vec3(0.0);
        probeSpacing = vec3(1.0);
        biasAndDistance = vec4(0.0);
        updateParams = vec4(0.0);
        return false;
    }

    probeSpacing = max(spacingAndCountY.xyz, vec3(0.0001));
    uint kind = uint(round(gridMinAndKind.w));
    if (kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        volumeCascadeIndex = uint(max(round(ringOffsetAndCascade.w), 0.0));
        ivec3 gridMin = ivec3(round(gridMinAndKind.xyz));
        ivec3 ringOffset = ivec3(round(ringOffsetAndCascade.xyz));
        ivec3 requestLogicalCell = request.LogicalCell;
        localProbeIndex = request.ProbeIndex - firstProbe;
        request.LogicalCell = DdgiDecodeLogicalCellFromPhysicalProbeIndex(
            request.ProbeIndex,
            gridMin,
            ringOffset,
            probeCounts,
            firstProbe);
        ivec3 relative = request.LogicalCell - gridMin;
        bool inGrid =
            relative.x >= 0 && relative.x < int(countX) &&
            relative.y >= 0 && relative.y < int(countY) &&
            relative.z >= 0 && relative.z < int(countZ);
        if (!inGrid)
        {
            resolveFailure = DDGI_RESOLVE_FAILURE_CLIPMAP_CELL;
            localProbeIndex = 0u;
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        if (any(notEqual(requestLogicalCell, request.LogicalCell)))
        {
            DdgiProbeUpdateRequest sampleRequest = request;
            sampleRequest.LogicalCell = requestLogicalCell;
            uint requestAge = pc.FrameSerial - request.RequestFrameSerial;
            RecordDdgiTraceRingMismatchSample(
                sampleRequest,
                firstProbe,
                request.ProbeIndex,
                gridMin,
                ringOffset,
                probeCounts,
                requestAge);
#if defined(DDGI_TRACE_PASS)
            if (gl_LocalInvocationID.x == 0u)
                AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_RING_MISMATCH_CORRECTED_COUNTER, 1u);
#endif
        }

        probePosition = vec3(request.LogicalCell) * probeSpacing;
    }
    else
    {
        bool inVolume =
            request.LogicalCell.x >= 0 && request.LogicalCell.x < int(countX) &&
            request.LogicalCell.y >= 0 && request.LogicalCell.y < int(countY) &&
            request.LogicalCell.z >= 0 && request.LogicalCell.z < int(countZ);
        if (!inVolume)
        {
            localProbeIndex = 0u;
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        localProbeIndex = uint(request.LogicalCell.x) +
            uint(request.LogicalCell.y) * countX +
            uint(request.LogicalCell.z) * countX * countY;
        if (firstProbe + localProbeIndex != request.ProbeIndex)
        {
            probePosition = vec3(0.0);
            biasAndDistance = vec4(0.0);
            updateParams = vec4(0.0);
            return false;
        }

        probePosition = originAndFirst.xyz + probeSpacing * vec3(request.LogicalCell);
    }

    biasAndDistance = vec4(volumeBiasAndCountZ.xyz, 0.0);
    updateParams = volumeUpdateParams;
    return true;
}

uint ResolveDdgiNeighborProbeIndex(
    uint volumeIndex,
    ivec3 logicalCell,
    out bool valid)
{
    valid = false;
    uint volumeBaseWord = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME_HEADER) / 4u;
    uint volumeStrideWords = uint(SIZEOF_GPU_DDGI_PROBE_VOLUME) / 4u;
    uint baseWord = volumeBaseWord + volumeIndex * volumeStrideWords;
    vec4 originAndFirst = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_ORIGIN_AND_FIRST_PROBE_INDEX) / 4u);
    vec4 sizeAndCountX = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_SIZE_AND_PROBE_COUNT_X) / 4u);
    vec4 spacingAndCountY = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_PROBE_SPACING_AND_PROBE_COUNT_Y) / 4u);
    vec4 volumeBiasAndCountZ = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_BIAS_AND_PROBE_COUNT_Z) / 4u);
    vec4 gridMinAndKind = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_GRID_MIN_AND_KIND) / 4u);
    vec4 ringOffsetAndCascade = ReadStorageVec4(uint(DDGI_PROBE_VOLUME_BUFFER_INDEX), baseWord + uint(OFFSET_GPU_DDGI_PROBE_VOLUME_CLIPMAP_RING_OFFSET_AND_CASCADE) / 4u);

    uint firstProbe = uint(originAndFirst.w);
    uvec3 probeCounts = uvec3(
        max(uint(sizeAndCountX.w), 1u),
        max(uint(spacingAndCountY.w), 1u),
        max(uint(volumeBiasAndCountZ.w), 1u));
    uint kind = uint(round(gridMinAndKind.w));

    if (kind == DDGI_PROBE_VOLUME_KIND_CAMERA_CLIPMAP)
    {
        ivec3 gridMin = ivec3(round(gridMinAndKind.xyz));
        ivec3 relative = logicalCell - gridMin;
        if (any(lessThan(relative, ivec3(0))) || any(greaterThanEqual(relative, ivec3(probeCounts))))
            return firstProbe;

        valid = true;
        return DdgiCalculatePhysicalProbeIndex(
            logicalCell,
            gridMin,
            ivec3(round(ringOffsetAndCascade.xyz)),
            probeCounts,
            firstProbe);
    }

    if (any(lessThan(logicalCell, ivec3(0))) || any(greaterThanEqual(logicalCell, ivec3(probeCounts))))
        return firstProbe;

    uint localIndex = uint(logicalCell.x) +
        uint(logicalCell.y) * probeCounts.x +
        uint(logicalCell.z) * probeCounts.x * probeCounts.y;
    valid = true;
    return firstProbe + localIndex;
}

float ReadDdgiProbeStoredActive(uint probeIndex)
{
    if (probeIndex >= pc.ProbeCount)
        return 0.0;

    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    vec4 stateIrradiance = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    vec4 relocationAndClassification = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
    return clamp(min(stateIrradiance.w, relocationAndClassification.w), 0.0, 1.0);
}

uint ResolveDdgiInactiveProbeFallback(
    uint volumeIndex,
    ivec3 logicalCell,
    uint probeIndex,
    float activeProbe)
{
    if (activeProbe > 0.50)
        return probeIndex;

    uint bestProbeIndex = probeIndex;
    int bestDistanceSq = 2147483647;
    float bestActive = 0.0;
    for (int dz = -1; dz <= 1; dz++)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0 && dz == 0)
                    continue;

                ivec3 offset = ivec3(dx, dy, dz);
                bool validNeighbor;
                uint neighborProbeIndex = ResolveDdgiNeighborProbeIndex(volumeIndex, logicalCell + offset, validNeighbor);
                if (!validNeighbor || neighborProbeIndex == probeIndex || neighborProbeIndex >= pc.ProbeCount)
                    continue;

                float neighborActive = ReadDdgiProbeStoredActive(neighborProbeIndex);
                if (neighborActive <= 0.50)
                    continue;

                int distanceSq = dx * dx + dy * dy + dz * dz;
                if (distanceSq < bestDistanceSq || (distanceSq == bestDistanceSq && neighborActive > bestActive))
                {
                    bestDistanceSq = distanceSq;
                    bestActive = neighborActive;
                    bestProbeIndex = neighborProbeIndex;
                }
            }
        }
    }

    return bestProbeIndex;
}

GPUSurfaceCard ReadDdgiSurfaceCard(uint cardIndex)
{
    uint baseWord = cardIndex * (uint(SIZEOF_GPU_SURFACE_CARD) / 4u);
    GPUSurfaceCard card;
    card.ObjectIndex = ReadStorageUint(pc.SurfaceCardBufferIndex, baseWord + 0u);
    card.Axis = ReadStorageUint(pc.SurfaceCardBufferIndex, baseWord + 1u);
    card.LastCaptureFrame = ReadStorageUint(pc.SurfaceCardBufferIndex, baseWord + 2u);
    card.Flags = ReadStorageUint(pc.SurfaceCardBufferIndex, baseWord + 3u);
    card.AtlasRect = ReadStorageVec4(pc.SurfaceCardBufferIndex, baseWord + 4u);
    card.WorldOriginAndTileSize = ReadStorageVec4(pc.SurfaceCardBufferIndex, baseWord + 8u);
    card.WorldAxisUAndHalfExtent = ReadStorageVec4(pc.SurfaceCardBufferIndex, baseWord + 12u);
    card.WorldAxisVAndHalfExtent = ReadStorageVec4(pc.SurfaceCardBufferIndex, baseWord + 16u);
    card.WorldAxisNAndDepthRange = ReadStorageVec4(pc.SurfaceCardBufferIndex, baseWord + 20u);
    return card;
}

GPUGlobalSdfCascade ReadDdgiGlobalSdfCascade(uint cascadeIndex)
{
    uint baseWord = cascadeIndex * (uint(SIZEOF_GPU_GLOBAL_SDF_CASCADE) / 4u);
    GPUGlobalSdfCascade cascade;
    cascade.WorldMinAndVoxelSize = ReadStorageVec4(pc.GlobalSdfCascadeBufferIndex, baseWord + 0u);
    cascade.WorldExtentAndInvVoxelSize = ReadStorageVec4(pc.GlobalSdfCascadeBufferIndex, baseWord + 4u);
    cascade.TextureIndex = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 8u);
    cascade.Resolution = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 9u);
    cascade.MipCount = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 10u);
    cascade.Flags = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 11u);
    cascade.LogicalGridMinX = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 12u));
    cascade.LogicalGridMinY = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 13u));
    cascade.LogicalGridMinZ = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 14u));
    cascade.RingOffsetX = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 15u));
    cascade.RingOffsetY = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 16u));
    cascade.RingOffsetZ = int(ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 17u));
    cascade.BricksPerAxis = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 18u);
    cascade.Padding0 = ReadStorageUint(pc.GlobalSdfCascadeBufferIndex, baseWord + 19u);
    return cascade;
}

bool TryProjectDdgiSurfaceCard(GPUSurfaceCard card, vec3 worldPosition, vec3 hitNormal, out vec2 atlasPixel, out float score)
{
    vec3 axisU = normalize(card.WorldAxisUAndHalfExtent.xyz);
    vec3 axisV = normalize(card.WorldAxisVAndHalfExtent.xyz);
    vec3 axisN = normalize(card.WorldAxisNAndDepthRange.xyz);
    vec3 origin = card.WorldOriginAndTileSize.xyz;
    float halfU = max(card.WorldAxisUAndHalfExtent.w, 0.0001);
    float halfV = max(card.WorldAxisVAndHalfExtent.w, 0.0001);
    float depthRange = max(card.WorldAxisNAndDepthRange.w, 0.0001);
    vec3 relative = worldPosition - origin;
    float u = dot(relative, axisU) / (halfU * 2.0);
    float v = dot(relative, axisV) / (halfV * 2.0);
    float d = dot(relative, axisN) / depthRange;
    if (u < -0.02 || u > 1.02 || v < -0.02 || v > 1.02 || d < -0.05 || d > 1.05)
    {
        atlasPixel = vec2(0.0);
        score = 0.0;
        return false;
    }

    float normalScore = max(dot(normalize(hitNormal), axisN), 0.0);
    if (normalScore <= 0.001)
    {
        atlasPixel = vec2(0.0);
        score = 0.0;
        return false;
    }

    atlasPixel = card.AtlasRect.xy + clamp(vec2(u, v), vec2(0.0), vec2(1.0)) * max(card.AtlasRect.zw - vec2(1.0), vec2(1.0));
    score = normalScore * (1.0 - clamp(abs(d - 0.5) * 0.1, 0.0, 0.5));
    return true;
}

uint ReadDdgiSurfaceCacheWorkWord(uint wordOffset)
{
    return ReadStorageWord(pc.SurfaceCacheWorkBufferIndex, wordOffset);
}

vec3 ReadDdgiSurfaceCacheGridMin()
{
    return vec3(
        uintBitsToFloat(ReadDdgiSurfaceCacheWorkWord(4u)),
        uintBitsToFloat(ReadDdgiSurfaceCacheWorkWord(5u)),
        uintBitsToFloat(ReadDdgiSurfaceCacheWorkWord(6u)));
}

bool TryResolveDdgiSurfaceCacheGridCell(vec3 worldPosition, out ivec3 cell)
{
    uint gridResolution = ReadDdgiSurfaceCacheWorkWord(0u);
    float cellSize = uintBitsToFloat(ReadDdgiSurfaceCacheWorkWord(7u));
    if (gridResolution == 0u || cellSize <= 0.0 || isnan(cellSize) || isinf(cellSize))
    {
        cell = ivec3(0);
        return false;
    }

    vec3 gridMin = ReadDdgiSurfaceCacheGridMin();
    ivec3 candidate = ivec3(floor((worldPosition - gridMin) / cellSize));
    ivec3 gridMax = ivec3(int(gridResolution) - 1);
    if (any(lessThan(candidate, ivec3(0))) || any(greaterThan(candidate, gridMax)))
    {
        cell = ivec3(0);
        return false;
    }

    cell = candidate;
    return true;
}

void ConsiderDdgiSurfaceCacheCard(
    uint cardIndex,
    vec3 worldPosition,
    vec3 hitNormal,
    inout vec2 bestPixel0,
    inout vec2 bestPixel1,
    inout float bestScore0,
    inout float bestScore1)
{
    if (cardIndex >= pc.SurfaceCardCount)
        return;

    GPUSurfaceCard card = ReadDdgiSurfaceCard(cardIndex);
    vec2 atlasPixel;
    float score;
    if (!TryProjectDdgiSurfaceCard(card, worldPosition, hitNormal, atlasPixel, score))
        return;

    if (score > bestScore0)
    {
        bestScore1 = bestScore0;
        bestPixel1 = bestPixel0;
        bestScore0 = score;
        bestPixel0 = atlasPixel;
    }
    else if (score > bestScore1)
    {
        bestScore1 = score;
        bestPixel1 = atlasPixel;
    }
}

bool SampleDdgiSurfaceCacheCandidate(vec2 atlasPixel, out vec3 candidateRadiance)
{
    vec2 atlasSize = vec2(textureSize(BindlessTextures[nonuniformEXT(pc.SurfaceRadianceAtlasTextureIndex)], 0));
    vec4 sampleValue = textureLod(BindlessTextures[nonuniformEXT(pc.SurfaceRadianceAtlasTextureIndex)], (atlasPixel + vec2(0.5)) / max(atlasSize, vec2(1.0)), 0.0);
    candidateRadiance = max(sampleValue.rgb, vec3(0.0));
    return sampleValue.a > 0.001;
}

bool TrySampleDdgiSurfaceCacheRadiance(vec3 worldPosition, vec3 hitNormal, vec3 albedo, out vec3 radiance)
{
    radiance = vec3(0.0);
    if (pc.SurfaceCardCount == 0u)
        return false;

    vec2 bestPixel0 = vec2(0.0);
    vec2 bestPixel1 = vec2(0.0);
    float bestScore0 = 0.0;
    float bestScore1 = 0.0;

    ivec3 centerCell;
    if (!TryResolveDdgiSurfaceCacheGridCell(worldPosition, centerCell))
        return false;

    uint gridResolution = ReadDdgiSurfaceCacheWorkWord(0u);
    uint maxRefsPerCell = ReadDdgiSurfaceCacheWorkWord(1u);
    uint gridCellsOffset = ReadDdgiSurfaceCacheWorkWord(9u);
    uint cellStride = maxRefsPerCell + 1u;
    ivec3 gridMax = ivec3(int(gridResolution) - 1);
    for (int dz = -1; dz <= 1; dz++)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                ivec3 cell = centerCell + ivec3(dx, dy, dz);
                if (any(lessThan(cell, ivec3(0))) || any(greaterThan(cell, gridMax)))
                    continue;

                uint cellIndex = uint(cell.x) +
                    uint(cell.y) * gridResolution +
                    uint(cell.z) * gridResolution * gridResolution;
                uint cellBase = gridCellsOffset + cellIndex * cellStride;
                uint refCount = min(ReadDdgiSurfaceCacheWorkWord(cellBase), maxRefsPerCell);
                for (uint refIndex = 0u; refIndex < refCount; refIndex++)
                {
                    uint cardIndex = ReadDdgiSurfaceCacheWorkWord(cellBase + 1u + refIndex);
                    ConsiderDdgiSurfaceCacheCard(
                        cardIndex,
                        worldPosition,
                        hitNormal,
                        bestPixel0,
                        bestPixel1,
                        bestScore0,
                        bestScore1);
                }
            }
        }
    }

    if (bestScore0 <= 0.0)
        return false;

    vec3 r0;
    bool s0Valid = SampleDdgiSurfaceCacheCandidate(bestPixel0, r0);

    if (bestScore1 > 0.0)
    {
        vec3 r1;
        bool s1Valid = SampleDdgiSurfaceCacheCandidate(bestPixel1, r1);
        if (!s0Valid && s1Valid)
        {
            radiance = r1;
            return true;
        }

        if (!s0Valid)
            return false;

        if (!s1Valid)
        {
            radiance = r0;
            return true;
        }

        float w = bestScore1 / max(bestScore0 + bestScore1, 0.0001);
        radiance = mix(r0, r1, w);
    }
    else
    {
        if (!s0Valid)
            return false;

        radiance = r0;
    }

    return true;
}

bool ShouldTraceDdgiRayWithGlobalSdf(uint volumeCascadeIndex)
{
    return pc.GlobalSdfCascadeCount > 0u &&
        volumeCascadeIndex >= pc.SdfBackendFirstCascade &&
        pc.SdfBackendFirstCascade < pc.GlobalSdfCascadeCount;
}

uint SelectDdgiGlobalSdfCascade(uint volumeCascadeIndex)
{
    uint count = max(pc.GlobalSdfCascadeCount, 1u);
    uint first = min(pc.SdfBackendFirstCascade, count - 1u);
    return clamp(volumeCascadeIndex, first, count - 1u);
}

GlobalSdfSample SampleDdgiGlobalSdf(vec3 worldPosition)
{
    uint count = min(pc.GlobalSdfCascadeCount, 4u);
    for (uint cascadeIndex = 0u; cascadeIndex < count; cascadeIndex++)
    {
        GPUGlobalSdfCascade cascade = ReadDdgiGlobalSdfCascade(cascadeIndex);
        GlobalSdfSample sdfSample = SampleGlobalSdfCascade(worldPosition, cascade, cascadeIndex);
        if (sdfSample.Valid)
            return sdfSample;
    }

    return GlobalSdfSample(1.0e20, 0u, false);
}

vec3 EstimateDdgiGlobalSdfNormal(vec3 worldPosition, uint cascadeIndex)
{
    uint count = min(pc.GlobalSdfCascadeCount, 4u);
    for (uint finestCascadeIndex = 0u; finestCascadeIndex < count; finestCascadeIndex++)
    {
        GPUGlobalSdfCascade finestCascade = ReadDdgiGlobalSdfCascade(finestCascadeIndex);
        if (GlobalSdfCascadeContains(worldPosition, finestCascade))
            return EstimateGlobalSdfNormal(worldPosition, finestCascade, finestCascadeIndex);
    }

    if (count > 0u)
    {
        uint fallbackCascadeIndex = min(cascadeIndex, count - 1u);
        GPUGlobalSdfCascade fallbackCascade = ReadDdgiGlobalSdfCascade(fallbackCascadeIndex);
        return EstimateGlobalSdfNormal(worldPosition, fallbackCascade, fallbackCascadeIndex);
    }

    return vec3(0.0, 1.0, 0.0);
}

GlobalSdfTraceResult TraceDdgiGlobalSdf(
    vec3 origin,
    vec3 direction,
    float maxDistance,
    uint startCascadeIndex,
    uint maxSteps)
{
    float t = 0.0;
    uint totalSteps = 0u;
    uint count = min(pc.GlobalSdfCascadeCount, 4u);
    uint cascadeIndex = min(startCascadeIndex, count > 0u ? count - 1u : 0u);
    while (cascadeIndex < count && totalSteps < maxSteps && t < maxDistance)
    {
        GPUGlobalSdfCascade cascade = ReadDdgiGlobalSdfCascade(cascadeIndex);
        if (!GlobalSdfCascadeContains(origin + direction * t, cascade))
        {
            cascadeIndex++;
            continue;
        }

        GlobalSdfTraceResult segment = TraceGlobalSdfCascadeSegment(
            origin,
            direction,
            t,
            maxDistance,
            cascade,
            cascadeIndex,
            maxSteps - totalSteps);
        totalSteps += segment.StepCount;
        if (segment.Hit)
        {
            segment.Normal = EstimateDdgiGlobalSdfNormal(origin + direction * segment.T, segment.CascadeIndex);
            segment.StepCount = totalSteps;
            return segment;
        }

        t = max(segment.T + max(cascade.WorldMinAndVoxelSize.w, 0.001), t + 0.001);
        cascadeIndex++;
    }

    return GlobalSdfTraceResult(false, maxDistance, cascadeIndex, vec3(0.0, 1.0, 0.0), totalSteps);
}

void TraceProbeRay(
    vec3 probePosition,
    vec3 direction,
    uint probeIndex,
    uint rayIndex,
    float normalBias,
    float viewBias,
    float maxDistance,
    uint volumeCascadeIndex,
    out vec3 radiance,
    out vec2 visibilityMoment,
    out float hit,
    out float miss,
    out float closeHit,
    out float backface,
    out vec3 relocation,
    out vec3 directDiffuseOut,
    out vec3 directNoShadowDiffuseOut,
    out vec3 emissiveDiffuseOut,
    out vec3 stableDiffuseOut,
    out vec3 skyDiffuseOut)
{
    directDiffuseOut = vec3(0.0);
    directNoShadowDiffuseOut = vec3(0.0);
    emissiveDiffuseOut = vec3(0.0);
    stableDiffuseOut = vec3(0.0);
    skyDiffuseOut = vec3(0.0);
    float tMin = min(DDGI_PROBE_TRACE_EPSILON, max(maxDistance * 0.01, 0.001));
    vec3 origin = probePosition;

    if (ShouldTraceDdgiRayWithGlobalSdf(volumeCascadeIndex))
    {
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SDF_TRACE_COUNTER, 1u);
        uint sdfCascadeIndex = SelectDdgiGlobalSdfCascade(volumeCascadeIndex);
        GlobalSdfTraceResult sdfTrace = TraceDdgiGlobalSdf(origin + direction * tMin, direction, maxDistance, sdfCascadeIndex, 96u);
        AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SDF_TRACE_STEP_COUNTER, sdfTrace.StepCount);
        if (sdfTrace.Hit)
        {
            float hitT = min(max(sdfTrace.T + tMin, tMin), maxDistance);
            float closeThreshold = max(normalBias + viewBias * 2.0, 0.05);
            float closeWeight = 1.0 - smoothstep(closeThreshold, closeThreshold * 4.0, hitT);
            vec3 hitPosition = origin + direction * hitT;
            vec3 surfaceNormal = dot(sdfTrace.Normal, direction) > 0.0 ? -sdfTrace.Normal : sdfTrace.Normal;
            vec3 surfaceAlbedo = vec3(DDGI_DIFFUSE_ALBEDO);
            vec3 surfaceEmissive = vec3(0.0);
            vec3 cacheRadiance;
            bool forceAnalyticFallback = DdgiSurfaceCacheAnalyticFallbackForced();

            hit = 1.0;
            miss = 0.0;
            closeHit = closeWeight;
            backface = 0.0;
            relocation = -direction * closeWeight;
            visibilityMoment = vec2(hitT, hitT * hitT);

            if (!forceAnalyticFallback && TrySampleDdgiSurfaceCacheRadiance(hitPosition, surfaceNormal, surfaceAlbedo, cacheRadiance))
            {
                AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SURFACE_CACHE_HIT_COUNTER, 1u);
                radiance = cacheRadiance;
                directDiffuseOut = cacheRadiance;
            }
            else
            {
                AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SURFACE_CACHE_FALLBACK_COUNTER, 1u);
                radiance = EvaluateDdgiRayQuerySurfaceRadianceAtHit(
                    hitPosition,
                    surfaceNormal,
                    surfaceAlbedo,
                    surfaceEmissive,
                    probeIndex,
                    rayIndex,
                    directDiffuseOut,
                    directNoShadowDiffuseOut,
                    emissiveDiffuseOut,
                    stableDiffuseOut);
            }
            return;
        }

        radiance = SampleDdgiEnvironmentMissRadiance(direction);
        skyDiffuseOut = radiance;
        visibilityMoment = vec2(maxDistance, maxDistance * maxDistance);
        hit = 0.0;
        miss = 1.0;
        closeHit = 0.0;
        backface = 0.0;
        relocation = vec3(0.0);
        return;
    }

    AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_RAY_QUERY_TRACE_COUNTER, 1u);
    rayQueryEXT query;
    rayQueryInitializeEXT(
        query,
        SceneTlas,
        gl_RayFlagsOpaqueEXT,
        0xff,
        origin,
        tMin,
        direction,
        maxDistance);

    while (rayQueryProceedEXT(query))
    {
    }

    uint hitType = rayQueryGetIntersectionTypeEXT(query, true);
    if (hitType != gl_RayQueryCommittedIntersectionNoneEXT)
    {
        float hitT = rayQueryGetIntersectionTEXT(query, true);
        bool frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);
        float closeThreshold = max(normalBias + viewBias * 2.0, 0.05);
        float closeWeight = 1.0 - smoothstep(closeThreshold, closeThreshold * 4.0, hitT);

        hit = 1.0;
        miss = 0.0;
        closeHit = closeWeight;
        backface = frontFace ? 0.0 : 1.0;
        relocation = -direction * closeWeight;
        visibilityMoment = vec2(hitT, hitT * hitT);

        vec3 hitPosition = origin + direction * hitT;
        vec3 surfaceNormal = normalize(-direction);
        vec3 surfaceAlbedo = vec3(DDGI_DIFFUSE_ALBEDO);
        vec3 surfaceEmissive = vec3(0.0);
        bool sampleMaterialTextures = ShouldSampleDdgiMaterialTextures(volumeCascadeIndex);
        float materialTextureLod = DdgiMaterialTextureLod(volumeCascadeIndex);
        uint instanceIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(query, true);
        uint primitiveIndex = rayQueryGetIntersectionPrimitiveIndexEXT(query, true);
        vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(query, true);
        ResolveCommittedHitSurface(
            instanceIndex,
            primitiveIndex,
            barycentrics,
            direction,
            volumeCascadeIndex,
            sampleMaterialTextures,
            materialTextureLod,
            surfaceNormal,
            surfaceAlbedo,
            surfaceEmissive);
        vec3 cacheRadiance;
        bool forceAnalyticFallback = DdgiSurfaceCacheAnalyticFallbackForced();
        if (!forceAnalyticFallback && TrySampleDdgiSurfaceCacheRadiance(hitPosition, surfaceNormal, surfaceAlbedo, cacheRadiance))
        {
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SURFACE_CACHE_HIT_COUNTER, 1u);
            radiance = cacheRadiance;
            directDiffuseOut = cacheRadiance;
        }
        else
        {
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_SURFACE_CACHE_FALLBACK_COUNTER, 1u);
            radiance = EvaluateDdgiRayQuerySurfaceRadianceAtHit(
                hitPosition,
                surfaceNormal,
                surfaceAlbedo,
                surfaceEmissive,
                probeIndex,
                rayIndex,
                directDiffuseOut,
                directNoShadowDiffuseOut,
                emissiveDiffuseOut,
                stableDiffuseOut);
        }
        return;
    }

    radiance = SampleDdgiEnvironmentMissRadiance(direction);
    skyDiffuseOut = radiance;
    visibilityMoment = vec2(maxDistance, maxDistance * maxDistance);
    hit = 0.0;
    miss = 1.0;
    closeHit = 0.0;
    backface = 0.0;
    relocation = vec3(0.0);
}

void WriteVisibilityAtlasSample(
    uint visibilityTexel,
    vec2 visibilitySample,
    float blendAlpha,
    uint probeIndex)
{
    uint visibilityTexels = max(pc.VisibilityTexelsPerProbe, 1u);
    uint visibilityTexelCount = visibilityTexels * visibilityTexels;

    if (visibilityTexel < visibilityTexelCount)
    {
        uint visibilityBase = probeIndex * visibilityTexelCount;
        uint visibilityWord = visibilityBase + visibilityTexel;
        vec2 previous = ReadPackedHalf2(pc.VisibilityAtlasBufferIndex, visibilityWord);
        WritePackedHalf2(pc.VisibilityAtlasBufferIndex, visibilityWord, mix(previous, visibilitySample, blendAlpha));
    }
}

vec4 AccumulateProbeIrradianceTexel(uint texel, uint texelsPerProbe, uint rayCount, float activeProbe)
{
    vec3 texelDirection = AtlasTexelDirection(texel, texelsPerProbe, 0u);
    vec3 weightedRadiance = vec3(0.0);
    float weightSum = 0.0;
    uint sampleCount = min(rayCount, DDGI_MAX_RAYS_PER_PROBE);

    for (uint rayIndex = 0u; rayIndex < sampleCount; rayIndex++)
    {
        vec4 rayIrradiance = SharedRayIrradiance[rayIndex];
        vec3 rayDirection = SharedRayDirection[rayIndex].xyz;
        float raySolidAngle = max(SharedRayDirection[rayIndex].w, 0.0);
        float weight = max(dot(rayDirection, texelDirection), 0.0) * raySolidAngle * rayIrradiance.w;
        weightedRadiance += rayIrradiance.rgb * weight;
        weightSum += weight;
    }

    float expectedWeight = PI;
    float confidence = clamp(weightSum / expectedWeight, 0.0, 1.0) * activeProbe;
    // Store irradiance for this atlas normal. Receiver diffuse BRDF is applied only in forward shading.
    vec3 irradiance = sampleCount > 0u
        ? weightedRadiance
        : vec3(0.0);

    return vec4(irradiance, confidence);
}

bool DdgiHasNonFinite(vec4 value)
{
    return any(isnan(value)) || any(isinf(value));
}

vec4 SanitizeDdgiIrradianceAtlasSample(vec4 value)
{
    return vec4(
        (isnan(value.x) || isinf(value.x)) ? 0.0 : clamp(value.x, 0.0, DDGI_IRRADIANCE_ATLAS_MAX),
        (isnan(value.y) || isinf(value.y)) ? 0.0 : clamp(value.y, 0.0, DDGI_IRRADIANCE_ATLAS_MAX),
        (isnan(value.z) || isinf(value.z)) ? 0.0 : clamp(value.z, 0.0, DDGI_IRRADIANCE_ATLAS_MAX),
        (isnan(value.w) || isinf(value.w)) ? 0.0 : clamp(value.w, 0.0, 1.0));
}

vec4 SanitizeDdgiEncodedIrradianceAtlasSample(vec4 value)
{
    float encodedMax = pow(DDGI_IRRADIANCE_ATLAS_MAX, 1.0 / DDGI_IRRADIANCE_ATLAS_GAMMA);
    return vec4(
        (isnan(value.x) || isinf(value.x)) ? 0.0 : clamp(value.x, 0.0, encodedMax),
        (isnan(value.y) || isinf(value.y)) ? 0.0 : clamp(value.y, 0.0, encodedMax),
        (isnan(value.z) || isinf(value.z)) ? 0.0 : clamp(value.z, 0.0, encodedMax),
        (isnan(value.w) || isinf(value.w)) ? 0.0 : clamp(value.w, 0.0, 1.0));
}

vec3 ApplyDdgiIrradianceFireflySuppression(vec3 previousIrradiance, vec3 currentIrradiance, float historyValid, out bool suppressed)
{
    suppressed = false;
    if (historyValid <= 0.5)
        return currentIrradiance;

    float previousLuminance = DdgiTraceEnergyLuminance(previousIrradiance);
    float currentLuminance = DdgiTraceEnergyLuminance(currentIrradiance);
    if (previousLuminance <= 0.01 || currentLuminance <= 0.0001)
        return currentIrradiance;

    float luminanceLimit = max(previousLuminance * 8.0, 16.0);
    if (currentLuminance <= luminanceLimit)
        return currentIrradiance;

    suppressed = true;
    return currentIrradiance * (luminanceLimit / currentLuminance);
}

float ResolveDdgiAsymmetricIrradianceBlendAlpha(
    float blendAlpha,
    uint flags,
    float historyValid,
    vec3 previousIrradiance,
    vec3 currentIrradiance)
{
    if (historyValid <= 0.5)
        return clamp(blendAlpha, 0.0, 1.0);

    float previousLuminance = DdgiTraceEnergyLuminance(previousIrradiance);
    float currentLuminance = DdgiTraceEnergyLuminance(currentIrradiance);
    float relativeDelta = abs(currentLuminance - previousLuminance) / max(max(currentLuminance, previousLuminance), 0.05);
    float changeAttention = smoothstep(0.02, 0.35, relativeDelta);
    float reasonFloor = ResolveDdgiIrradianceReasonBlendFloor(flags);
    float response = clamp(blendAlpha, 0.0, 1.0);

    if (currentLuminance < previousLuminance)
    {
        float darkeningResponse = max(response, 1.0 / 1024.0);
        darkeningResponse = max(darkeningResponse, mix(response, min(response + 0.20, 0.65), changeAttention));
        response = darkeningResponse;
    }
    else if (currentLuminance > previousLuminance)
    {
        float brighteningDamping = mix(1.0, 0.5, changeAttention);
        response = max(response * brighteningDamping, reasonFloor);
    }

    return clamp(response, 0.0, 1.0);
}

void WriteProbeIrradianceAtlasTexel(uint probeIndex, uint texel, vec4 irradianceSample, float blendAlpha, uint flags, float historyValid)
{
    uint irradianceTexels = max(pc.IrradianceTexelsPerProbe, 1u);
    uint irradianceTexelCount = irradianceTexels * irradianceTexels;
    uint irradianceWordsPerProbe = irradianceTexelCount * 2u;
    uint irradianceBase = probeIndex * irradianceWordsPerProbe;
    vec4 previous = ReadPackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u);
    vec4 safePrevious = SanitizeDdgiEncodedIrradianceAtlasSample(previous);
    vec4 safePreviousLinear = ResolveDdgiIrradianceAtlasSqrtBlend(DecodeDdgiIrradianceAtlasSqrtSample(safePrevious));
    vec4 safeCurrent = SanitizeDdgiIrradianceAtlasSample(irradianceSample);
    bool suppressed;
    safeCurrent.rgb = ApplyDdgiIrradianceFireflySuppression(safePreviousLinear.rgb, safeCurrent.rgb, historyValid, suppressed);
    if (DdgiTraceEnergyDiagnosticsEnabled())
    {
        if (DdgiHasNonFinite(irradianceSample))
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_NONFINITE_IRRADIANCE_COUNTER, 1u);
        if (suppressed)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_BLEND_ENERGY_FIREFLY_SUPPRESSED_COUNTER, 1u);
    }

    vec4 encodedCurrent = vec4(EncodeDdgiIrradianceAtlasRgb(safeCurrent.rgb), safeCurrent.w);
    float asymmetricBlendAlpha = ResolveDdgiAsymmetricIrradianceBlendAlpha(
        blendAlpha,
        flags,
        historyValid,
        safePreviousLinear.rgb,
        safeCurrent.rgb);
    WritePackedHalf4(pc.IrradianceAtlasBufferIndex, irradianceBase + texel * 2u, mix(safePrevious, encodedCurrent, asymmetricBlendAlpha));
}

struct DdgiRayResult
{
    vec3 radiance;
    float confidence;
    vec3 direction;
    float solidAngle;
    float hitDistance;
    float hitDistanceSquared;
    float hit;
    float miss;
    vec3 relocation;
    float closeHit;
    float frontface;
    float backface;
    float flags;
};

uint RayResultBaseWord(uint updateIndex, uint rayIndex)
{
    return (updateIndex * max(pc.RayCapacityPerProbe, 1u) + rayIndex) * DDGI_RAY_RESULT_STRIDE_WORDS;
}

void WriteDdgiRayResult(uint updateIndex, uint rayIndex, DdgiRayResult result)
{
    uint baseWord = RayResultBaseWord(updateIndex, rayIndex);
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 0u, vec4(result.radiance, result.confidence));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 4u, vec4(result.direction, result.solidAngle));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 8u, vec4(result.hitDistance, result.hitDistanceSquared, result.hit, result.miss));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 12u, vec4(result.relocation, result.closeHit));
    WriteStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 16u, vec4(result.frontface, result.backface, result.flags, 0.0));
}

DdgiRayResult ReadDdgiRayResult(uint updateIndex, uint rayIndex)
{
    uint baseWord = RayResultBaseWord(updateIndex, rayIndex);
    vec4 radiance = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 0u);
    vec4 direction = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 4u);
    vec4 visibility = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 8u);
    vec4 relocation = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 12u);
    vec4 evidence = ReadStorageVec4(pc.RayResultScratchBufferIndex, baseWord + 16u);

    DdgiRayResult result;
    result.radiance = radiance.rgb;
    result.confidence = radiance.w;
    result.direction = direction.xyz;
    result.solidAngle = direction.w;
    result.hitDistance = visibility.x;
    result.hitDistanceSquared = visibility.y;
    result.hit = visibility.z;
    result.miss = visibility.w;
    result.relocation = relocation.xyz;
    result.closeHit = relocation.w;
    result.frontface = evidence.x;
    result.backface = evidence.y;
    result.flags = evidence.z;
    return result;
}

vec3 ClampDdgiRelocationVector(vec3 relocation, float maxRelocationDistance)
{
    float relocationLength = length(relocation);
    if (relocationLength <= maxRelocationDistance || relocationLength <= 0.000001)
        return relocation;

    return relocation * (maxRelocationDistance / relocationLength);
}

#if defined(DDGI_TRACE_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    uint updateRequestCount = ResolveDdgiUpdateRequestCount();
    bool updateEnabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u && pc.ProbeCount > 0u;
    bool withinUpdateRequestCount = updateIndex < updateRequestCount;
    bool enabled = updateEnabled && withinUpdateRequestCount;
    if (localIndex == 0u)
    {
        if (!updateEnabled)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_DISABLED_COUNTER, 1u);
        else if (!withinUpdateRequestCount)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_BEYOND_REQUEST_COUNTER, 1u);
    }

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.RayCount = 0u;
    request.RequestFrameSerial = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    uint resolveFailure;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex,
        resolveFailure);
    if (localIndex == 0u && enabled && !resolved)
    {
        if (resolveFailure == DDGI_RESOLVE_FAILURE_BOUNDS)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_BOUNDS_COUNTER, 1u);
        else if (resolveFailure == DDGI_RESOLVE_FAILURE_PROBE_RANGE)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_PROBE_RANGE_COUNTER, 1u);
        else if (resolveFailure == DDGI_RESOLVE_FAILURE_CLIPMAP_CELL)
            AddRendererDiagnostic(pc.CurrentFrameIndex, DDGI_TRACE_EARLY_OUT_RESOLVE_CLIPMAP_CELL_COUNTER, 1u);
    }

    uint probeIndex = request.ProbeIndex;
    uint raysPerProbe = ResolveDdgiRequestRayCount(request, updateParams);
    float normalBias = max(biasAndDistance.x, 0.0);
    float viewBias = max(biasAndDistance.y, 0.0);
    float maxDistance = max(biasAndDistance.z > 0.0 ? biasAndDistance.z : 16.0, 0.1);
    float intensity = max(updateParams.z, 0.0);
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool relocationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_RELOCATION) != 0u;
    bool classificationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_CLASSIFICATION) != 0u;
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);
    vec4 previousState = vec4(0.0);
    vec4 previousStateHistory = vec4(0.0);
    vec4 previousRelocationAndClassification = vec4(0.0);
    vec4 previousQualityAndReason = vec4(0.0);
    if (resolved)
    {
        if (!resetHistory)
        {
            previousState = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
            previousStateHistory = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
            previousRelocationAndClassification = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
            previousQualityAndReason = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);
        }
        else if (localIndex == 0u)
        {
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 16u, vec4(0.0));
            WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 20u, vec4(0.0));
            if (relocationEnabled || classificationEnabled)
            {
                uint relocationBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION) / 4u);
                WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase, vec4(0.0));
                WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 4u, vec4(0.0));
                WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(0.0));
            }
        }
    }

    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float blendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;
    vec3 traceProbePosition = probePosition + (resetHistory ? vec3(0.0) : previousRelocationAndClassification.xyz);

    vec3 localRadiance = vec3(0.0);
    vec2 localVisibility = vec2(0.0);
    vec3 localRelocation = vec3(0.0);
    float localRayCount = 0.0;
    float localHitCount = 0.0;
    float localCloseCount = 0.0;
    float localBackfaceCount = 0.0;
    float localMissCount = 0.0;

    if (resolved)
    {
        mat3 rayRotation = DdgiProbeRayRotation(probeIndex, pc.FrameSerial);
        float raySolidAngle = (4.0 * PI) / max(float(raysPerProbe), 1.0);

        for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
        {
            vec3 direction = rayRotation * DdgiSphericalFibonacci(rayIndex, raysPerProbe);
            vec3 radiance;
            vec2 visibilityMoment;
            float hit;
            float miss;
            float closeHit;
            float backface;
            vec3 relocation;
            vec3 directDiffuse;
            vec3 directNoShadowDiffuse;
            vec3 emissiveDiffuse;
            vec3 stableDiffuse;
            vec3 skyDiffuse;
            TraceProbeRay(
                traceProbePosition,
                direction,
                probeIndex,
                rayIndex,
                normalBias,
                viewBias,
                maxDistance,
                volumeCascadeIndex,
                radiance,
                visibilityMoment,
                hit,
                miss,
                closeHit,
                backface,
                relocation,
                directDiffuse,
                directNoShadowDiffuse,
                emissiveDiffuse,
                stableDiffuse,
                skyDiffuse);

            // Store radiance arriving at the probe; atlas integration converts it to irradiance.
            vec3 probeRayRadiance = radiance;
            RecordDdgiTraceEnergyDiagnostics(
                probeIndex,
                rayIndex,
                probeRayRadiance,
                directDiffuse,
                directNoShadowDiffuse,
                emissiveDiffuse,
                stableDiffuse,
                skyDiffuse,
                hit,
                miss);
            DdgiRayResult rayResult;
            rayResult.radiance = probeRayRadiance;
            rayResult.confidence = 1.0;
            rayResult.direction = direction;
            rayResult.solidAngle = raySolidAngle;
            rayResult.hitDistance = visibilityMoment.x;
            rayResult.hitDistanceSquared = visibilityMoment.y;
            rayResult.hit = hit;
            rayResult.miss = miss;
            rayResult.relocation = relocation;
            rayResult.closeHit = closeHit;
            rayResult.frontface = 1.0 - backface;
            rayResult.backface = backface;
            rayResult.flags = resolved ? 1.0 : 0.0;
            WriteDdgiRayResult(updateIndex, rayIndex, rayResult);
        }
    }
}
#elif defined(DDGI_BLEND_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    bool enabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u &&
        updateIndex < ResolveDdgiUpdateRequestCount() &&
        pc.ProbeCount > 0u;

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.RayCount = 0u;
    request.RequestFrameSerial = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    uint resolveFailure;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex,
        resolveFailure);

    if (!resolved)
        return;

    uint probeIndex = request.ProbeIndex;
    uint raysPerProbe = ResolveDdgiRequestRayCount(request, updateParams);
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);
    vec4 previousState = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    vec4 previousStateHistory = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
    vec4 previousRelocationAndClassification = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
    vec4 previousRepresentationMetadata = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 20u);
    vec3 previousIrradianceHistory = ReadDdgiIrradianceHistoryMetrics(stateBase, resetHistory);
    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float baseBlendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;
    float visibilityBlendAlpha = ResolveDdgiVisibilityBlendAlpha(baseBlendAlpha, request.Flags);

    vec3 localRadiance = vec3(0.0);
    vec2 localVisibility = vec2(0.0);
    float localRayCount = 0.0;

    uint visibilityTexels = max(pc.VisibilityTexelsPerProbe, 1u);
    uint visibilityTexelCount = visibilityTexels * visibilityTexels;
    for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
    {
        DdgiRayResult result = ReadDdgiRayResult(updateIndex, rayIndex);
        if (result.flags <= 0.0)
        {
            SharedRayIrradiance[rayIndex] = vec4(0.0);
            SharedRayDirection[rayIndex] = vec4(0.0);
            SharedRayVisibility[rayIndex] = vec2(0.0);
            continue;
        }

        vec2 visibilityMoment = vec2(result.hitDistance, result.hitDistanceSquared);
        SharedRayIrradiance[rayIndex] = vec4(result.radiance, result.confidence);
        SharedRayDirection[rayIndex] = vec4(result.direction, result.solidAngle);
        SharedRayVisibility[rayIndex] = visibilityMoment;
        localRadiance += result.radiance;
        localVisibility += visibilityMoment;
        localRayCount += result.confidence;
    }

    SharedRadianceAndRayCount[localIndex] = vec4(localRadiance, localRayCount);
    SharedVisibilityAndHitCount[localIndex] = vec4(localVisibility, 0.0, 0.0);
    barrier();

    for (uint visibilityTexel = localIndex; visibilityTexel < visibilityTexelCount; visibilityTexel += DDGI_LOCAL_SIZE)
    {
        vec3 texelDirection = AtlasTexelDirection(visibilityTexel, visibilityTexels, 0u);
        vec2 weightedVisibility = vec2(0.0);
        float weightSum = 0.0;
        uint sampleCount = min(raysPerProbe, DDGI_MAX_RAYS_PER_PROBE);

        for (uint rayIndex = 0u; rayIndex < sampleCount; rayIndex++)
        {
            vec4 rayDirectionAndSolidAngle = SharedRayDirection[rayIndex];
            float rayValid = SharedRayIrradiance[rayIndex].w > 0.0 ? 1.0 : 0.0;
            float weight = DdgiVisibilityGatherWeight(dot(rayDirectionAndSolidAngle.xyz, texelDirection)) * rayValid;
            weightedVisibility += SharedRayVisibility[rayIndex] * weight;
            weightSum += weight;
        }

        if (weightSum > 0.0001)
            WriteVisibilityAtlasSample(visibilityTexel, weightedVisibility / weightSum, visibilityBlendAlpha, probeIndex);
    }

    if (localIndex == 0u)
    {
        vec3 totalRadiance = vec3(0.0);
        vec2 totalVisibility = vec2(0.0);
        float totalRayCount = 0.0;
        for (uint i = 0u; i < DDGI_LOCAL_SIZE; i++)
        {
            totalRadiance += SharedRadianceAndRayCount[i].xyz;
            totalRayCount += SharedRadianceAndRayCount[i].w;
            totalVisibility += SharedVisibilityAndHitCount[i].xy;
        }

        float invRayCount = 1.0 / max(totalRayCount, 1.0);
        vec3 irradiance = totalRadiance * invRayCount;
        vec2 visibility = totalVisibility * invRayCount;
        float currentLuminance = dot(irradiance, vec3(0.2126, 0.7152, 0.0722));
        vec4 irradianceHistory = ResolveDdgiIrradianceHistory(
            previousIrradianceHistory.x,
            previousIrradianceHistory.y,
            previousIrradianceHistory.z,
            currentLuminance,
            historyValid);
        float luminanceChange = irradianceHistory.w;
        float luminanceInconsistency = irradianceHistory.z;
        float irradianceBlendAlpha = ResolveDdgiIrradianceBlendAlpha(baseBlendAlpha, request.Flags, luminanceInconsistency);
        float previousActiveProbe = historyValid > 0.5
            ? clamp(min(previousState.w, previousRelocationAndClassification.w), 0.0, 1.0)
            : 1.0;
        vec4 blendedIrradiance = vec4(mix(previousState.rgb, irradiance, irradianceBlendAlpha), previousActiveProbe);
        WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, blendedIrradiance);
        WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u, vec4(visibility, clamp(luminanceInconsistency, 0.0, 1.0), 1.0));
        WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 17u, irradianceHistory.x);
        WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 18u, irradianceHistory.y);
        WriteStorageFloat(pc.ProbeStateBufferIndex, stateBase + 19u, luminanceInconsistency);
        WriteStorageVec4(
            pc.ProbeStateBufferIndex,
            stateBase + 20u,
            DdgiProbeL1MetadataEnabled()
                ? ResolveDdgiProbeL1Metadata(raysPerProbe, historyValid, irradianceBlendAlpha, previousRepresentationMetadata)
                : vec4(0.0));
        SharedProbeAtlasControl = vec4(previousActiveProbe, irradianceBlendAlpha, historyValid, visibilityBlendAlpha);
    }

    barrier();

    uint irradianceTexels = max(pc.IrradianceTexelsPerProbe, 1u);
    uint irradianceTexelCount = irradianceTexels * irradianceTexels;
    if (localIndex < irradianceTexelCount)
    {
        vec4 directionalIrradiance = AccumulateProbeIrradianceTexel(
            localIndex,
            irradianceTexels,
            raysPerProbe,
            SharedProbeAtlasControl.x);
        RecordDdgiBlendEnergyDiagnostics(probeIndex, localIndex, directionalIrradiance);
        WriteProbeIrradianceAtlasTexel(
            probeIndex,
            localIndex,
            directionalIrradiance,
            SharedProbeAtlasControl.y,
            request.Flags,
            SharedProbeAtlasControl.z);
    }
}
#elif defined(DDGI_RELOCATE_CLASSIFY_PASS)
void main()
{
    uint localIndex = gl_LocalInvocationID.x;
    uint updateIndex = gl_WorkGroupID.x;
    bool enabled = (pc.Flags & DDGI_UPDATE_FLAG_ENABLED) != 0u &&
        updateIndex < ResolveDdgiUpdateRequestCount() &&
        pc.ProbeCount > 0u;

    DdgiProbeUpdateRequest request;
    request.ProbeIndex = 0u;
    request.VolumeIndex = 0u;
    request.Flags = 0u;
    request.Priority = 0u;
    request.RayCount = 0u;
    request.RequestFrameSerial = 0u;
    request.LogicalCell = ivec3(0);
    if (enabled)
        request = ReadProbeUpdateRequest(updateIndex);

    uint volumeIndex;
    uint volumeCascadeIndex;
    uint localProbeIndex;
    vec3 probePosition;
    vec3 probeSpacing;
    vec4 biasAndDistance;
    vec4 updateParams;
    uint resolveFailure;
    bool resolved = enabled && ResolveProbeUpdateRequest(
        request,
        localProbeIndex,
        probePosition,
        probeSpacing,
        biasAndDistance,
        updateParams,
        volumeIndex,
        volumeCascadeIndex,
        resolveFailure);

    if (!resolved)
        return;

    uint probeIndex = request.ProbeIndex;
    uint raysPerProbe = ResolveDdgiRequestRayCount(request, updateParams);
    float normalBias = max(biasAndDistance.x, 0.0);
    float viewBias = max(biasAndDistance.y, 0.0);
    float hysteresis = ResolveDdgiDirtyReasonHysteresis(clamp(updateParams.w, 0.0, 0.999), request.Flags);
    uint stateBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_STATE) / 4u);
    bool relocationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_RELOCATION) != 0u;
    bool classificationEnabled = (pc.Flags & DDGI_UPDATE_FLAG_CLASSIFICATION) != 0u;
    bool resetHistory = ShouldResetDdgiProbeHistory(request.Flags);

    vec4 previousState = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    vec4 previousStateHistory = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 4u);
    vec4 previousRelocationAndClassification = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u);
    vec4 previousQualityAndReason = resetHistory ? vec4(0.0) : ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u);

    vec3 localRelocation = vec3(0.0);
    float localRayCount = 0.0;
    float localHitCount = 0.0;
    float localCloseCount = 0.0;
    float localBackfaceCount = 0.0;
    float localMissCount = 0.0;
    float localNearestHitDistance = 3.402823466e+38;

    for (uint rayIndex = localIndex; rayIndex < raysPerProbe; rayIndex += DDGI_LOCAL_SIZE)
    {
        DdgiRayResult result = ReadDdgiRayResult(updateIndex, rayIndex);
        if (result.flags <= 0.0)
            continue;

        localRelocation += result.relocation;
        localRayCount += result.confidence;
        localHitCount += result.hit;
        localCloseCount += result.closeHit;
        localBackfaceCount += result.backface;
        localMissCount += result.miss;
        if (result.hit > 0.0)
            localNearestHitDistance = min(localNearestHitDistance, max(result.hitDistance, 0.0));
    }

    SharedRadianceAndRayCount[localIndex] = vec4(0.0, 0.0, 0.0, localRayCount);
    SharedVisibilityAndHitCount[localIndex] = vec4(0.0, 0.0, localHitCount, 0.0);
    SharedRelocationAndCloseCount[localIndex] = vec4(localRelocation, localCloseCount);
    SharedBackfaceAndMissCount[localIndex] = vec4(localBackfaceCount, localMissCount, localNearestHitDistance, 0.0);
    barrier();

    if (localIndex != 0u)
        return;

    vec3 totalRelocation = vec3(0.0);
    float totalRayCount = 0.0;
    float totalHitCount = 0.0;
    float totalCloseCount = 0.0;
    float totalBackfaceCount = 0.0;
    float totalMissCount = 0.0;
    float nearestHitDistance = 3.402823466e+38;
    for (uint i = 0u; i < DDGI_LOCAL_SIZE; i++)
    {
        totalRayCount += SharedRadianceAndRayCount[i].w;
        totalHitCount += SharedVisibilityAndHitCount[i].z;
        totalRelocation += SharedRelocationAndCloseCount[i].xyz;
        totalCloseCount += SharedRelocationAndCloseCount[i].w;
        totalBackfaceCount += SharedBackfaceAndMissCount[i].x;
        totalMissCount += SharedBackfaceAndMissCount[i].y;
        nearestHitDistance = min(nearestHitDistance, SharedBackfaceAndMissCount[i].z);
    }

    float invRayCount = 1.0 / max(totalRayCount, 1.0);
    float closeRatio = clamp(totalCloseCount * invRayCount, 0.0, 1.0);
    float backfaceRatio = clamp(totalBackfaceCount * invRayCount, 0.0, 1.0);
    float missRatio = clamp(totalMissCount * invRayCount, 0.0, 1.0);
    float hitRatio = clamp(totalHitCount * invRayCount, 0.0, 1.0);
    float minProbeSpacing = max(min(min(probeSpacing.x, probeSpacing.y), probeSpacing.z), 0.001);
    float targetSurfaceDistance = max(minProbeSpacing * pc.RelocationParams.x, pc.RelocationParams.y);
    float maxRelocationDistance = pc.RelocationParams.z * minProbeSpacing;
    float relocationBlendAlpha = pc.RelocationParams.w;
    vec3 sdfProbePosition = probePosition + previousRelocationAndClassification.xyz;
    GlobalSdfSample probeSdf = SampleDdgiGlobalSdf(sdfProbePosition);
    float sdfCloseRatio = 0.0;
    vec3 sdfRelocationDirection = vec3(0.0);
    if (probeSdf.Valid)
    {
        float sdfTarget = max(targetSurfaceDistance, 0.001);
        sdfCloseRatio = 1.0 - smoothstep(-sdfTarget * 0.25, sdfTarget * 2.0, probeSdf.DistanceMeters);
        nearestHitDistance = min(nearestHitDistance, max(probeSdf.DistanceMeters, 0.0));
        sdfRelocationDirection = EstimateDdgiGlobalSdfNormal(sdfProbePosition, probeSdf.CascadeIndex);
    }

    bool useSdfRelocationDirection = sdfCloseRatio > closeRatio && dot(sdfRelocationDirection, sdfRelocationDirection) > 0.0001;
    closeRatio = max(closeRatio, clamp(sdfCloseRatio, 0.0, 1.0));
    hitRatio = max(hitRatio, clamp(sdfCloseRatio, 0.0, 1.0));
    // Triangle winding is not reliable probe-validity evidence for production scenes:
    // solid shell rooms and imported two-sided walls often present their interior face
    // as a ray-query backface. Treat committed hits as valid transport and use close
    // hit distance as the embedded-probe signal.
    float softInvalidProbeScore = smoothstep(0.25, 0.45, closeRatio);
    float hardInvalidProbeScore = smoothstep(0.70, 0.90, closeRatio);
    float invalidProbeScore = softInvalidProbeScore;
    float historyValid = clamp(previousStateHistory.w, 0.0, 1.0);
    float blendAlpha = historyValid > 0.5 ? 1.0 - hysteresis : 1.0;
    float stateBlendAlpha = historyValid > 0.5 ? clamp(max(blendAlpha, 0.08), 0.0, 1.0) : 1.0;
    float previousActiveProbe = historyValid > 0.5
        ? clamp(min(previousState.w, previousRelocationAndClassification.w), 0.0, 1.0)
        : 1.0;
    float hardInvalid = smoothstep(0.75, 0.95, hardInvalidProbeScore);
    float softInvalid = smoothstep(0.35, 0.75, softInvalidProbeScore);
    float clipmapActiveFloor = volumeCascadeIndex == DDGI_AUTHORED_VOLUME_CASCADE ? 0.0 : 0.35;
    float targetActiveProbe = classificationEnabled ? max(1.0 - hardInvalid, clipmapActiveFloor) : 1.0;
    float activeBlendAlpha = targetActiveProbe > previousActiveProbe
        ? max(stateBlendAlpha, 0.35)
        : stateBlendAlpha;
    float activeProbe = mix(previousActiveProbe, targetActiveProbe, activeBlendAlpha);
    float confidencePenalty = classificationEnabled ? 1.0 - softInvalid * 0.75 : 1.0;
    vec3 relocationDirection = useSdfRelocationDirection
        ? sdfRelocationDirection
        : (length(totalRelocation) > 0.0001 ? normalize(totalRelocation) : vec3(0.0));
    nearestHitDistance = nearestHitDistance < 3.402823466e+37
        ? nearestHitDistance
        : max(normalBias + viewBias, 0.05);
    float relocationEvidence = smoothstep(0.10, 0.35, closeRatio) * (1.0 - missRatio);
    float neededPush = max(targetSurfaceDistance - nearestHitDistance, 0.0);
    float closePush = closeRatio * max(normalBias + viewBias, 0.01) * 4.0;
    float unclampedRelocationDistance = max(neededPush, closePush) * relocationEvidence;
    float relocationDistance = relocationEnabled ? clamp(unclampedRelocationDistance, 0.0, maxRelocationDistance) : 0.0;
    vec3 relocation = relocationEnabled ? relocationDirection * relocationDistance : vec3(0.0);
    vec3 blendedRelocationUnclamped = historyValid > 0.5
        ? mix(previousRelocationAndClassification.xyz, relocation, relocationBlendAlpha)
        : relocation;
    vec3 blendedRelocation = ClampDdgiRelocationVector(blendedRelocationUnclamped, maxRelocationDistance);
    float blendedRelocationDistance = length(blendedRelocation);

    float traceSampleConfidence = clamp(hitRatio + missRatio * 0.35, 0.0, 1.0);
    float rayHitConfidence = clamp(mix(0.35, 1.0, traceSampleConfidence) * confidencePenalty, 0.0, 1.0);
    float luminanceChange = clamp(previousStateHistory.z, 0.0, 1.0);
    float luminanceConfidence = 1.0 - luminanceChange * 0.45;
    float irradianceConfidence = clamp(activeProbe * confidencePenalty * luminanceConfidence, 0.0, 1.0);
    float visibilityConfidence = clamp((hitRatio + missRatio * 0.35) * (1.0 - closeRatio * 0.5) * confidencePenalty, 0.0, 1.0);
    vec3 qualityConfidence = vec3(rayHitConfidence, irradianceConfidence, visibilityConfidence);
    vec3 blendedQualityConfidence = historyValid > 0.5
        ? mix(previousQualityAndReason.xyz, qualityConfidence, stateBlendAlpha)
        : qualityConfidence;
    float lastUpdateReason = float(ResolvePrimaryProbeUpdateReason(request.Flags));

    vec4 currentIrradiance = ReadStorageVec4(pc.ProbeStateBufferIndex, stateBase);
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase, vec4(currentIrradiance.rgb, activeProbe));
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 8u, vec4(blendedRelocation, activeProbe));
    WriteStorageVec4(pc.ProbeStateBufferIndex, stateBase + 12u, vec4(blendedQualityConfidence, lastUpdateReason));
    WriteStorageWord(pc.ProbeStateBufferIndex, stateBase + 16u, pc.FrameSerial);

    if (relocationEnabled || classificationEnabled)
    {
        uint relocationBase = probeIndex * (uint(SIZEOF_GPU_DDGI_PROBE_RELOCATION_CLASSIFICATION) / 4u);
        uint fallbackProbeIndex = ResolveDdgiInactiveProbeFallback(volumeIndex, request.LogicalCell, probeIndex, activeProbe);
        WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase, vec4(blendedRelocation, blendedRelocationDistance));
        WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 4u, vec4(activeProbe, classificationEnabled ? invalidProbeScore : 0.0, closeRatio, backfaceRatio));
        WriteStorageVec4(pc.RelocationClassificationBufferIndex, relocationBase + 8u, vec4(nearestHitDistance, missRatio, PackDdgiFallbackProbeIndex(fallbackProbeIndex), hitRatio));
    }
}
#endif

#endif
